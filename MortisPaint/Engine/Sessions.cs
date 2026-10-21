using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum StrokeMode { Paint, Erase }

/// <summary>
/// Trait de pinceau ou de gomme : les empreintes sont accumulées dans un tampon puis fusionnées
/// au calque avec l'opacité du trait (pas d'accumulation sur les chevauchements, comme dans ibisPaint).
/// </summary>
public sealed class BrushStrokeSession : ILayerPreview, IDisposable
{
    readonly PaintEngine _e;
    readonly BrushSettings _b;
    readonly StrokeMode _mode;
    readonly SKColor _color;
    readonly SKBitmap _buffer;
    readonly SKCanvas _bufCanvas;
    readonly List<SKMatrix> _sym;
    readonly SKPaint _compositePaint;
    readonly SKColorFilter? _bufferFilter;
    StrokePath _path;
    DabRenderer _dabs;
    SKRectI _dirty = SKRectI.Empty;

    public Layer Target { get; }
    public bool HasContent => !_dirty.IsEmpty;

    /// <summary>Contrainte de règle, appliquée après la stabilisation (voir <see cref="StrokePath.Constraint"/>).</summary>
    public Func<SKPoint, SKPoint>? Constraint
    {
        get => _path.Constraint;
        set => _path.Constraint = value;
    }

    /// <param name="viewScale">Ignoré : l'espacement des empreintes ne dépend pas du zoom.</param>
    public BrushStrokeSession(PaintEngine e, BrushSettings brush, SKColor color, StrokeMode mode, float viewScale)
    {
        _e = e;
        _b = brush.Clone();
        _mode = mode;
        _color = color;
        Target = e.Document.ActiveLayer;
        _buffer = e.StrokeBuffer;
        _bufCanvas = new SKCanvas(_buffer);
        _sym = e.Symmetry.GetTransforms();
        // « Pression → opacité » : l'opacité du trait plafonne à pression × opacité (tampon en gris).
        _dabs = new DabRenderer(_b, color, _sym, capOpacity: true);
        _bufferFilter = _dabs.CreateBufferFilter();
        _path = new StrokePath(_b, OnDab);
        var blend = mode == StrokeMode.Erase
            ? SKBlendMode.DstOut
            : Target.AlphaLock ? SKBlendMode.SrcATop : SKBlendMode.SrcOver;
        _compositePaint = new SKPaint
        {
            BlendMode = blend,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(_b.Opacity * 255f + 0.5f, 0f, 255f)),
            ColorFilter = _bufferFilter,
        };
        e.SetPreview(this);
    }

    void OnDab(float x, float y, float p, float angle, float along) => _dabs.Dab(_bufCanvas, x, y, p, angle, along);

    public void Add(InputPoint p)
    {
        _path.Add(p);
        Flush();
    }

    void Flush()
    {
        var d = BitmapUtil.Clamp(_dabs.TakeDirty(), _e.Document.Width, _e.Document.Height);
        if (d.IsEmpty) return;
        _dirty = BitmapUtil.Union(_dirty, d);
        _e.Invalidate(d);
    }

    /// <summary>Remplace le contenu du tampon par une forme (outil Formes) : polyligne tracée au pinceau.</summary>
    public void SetShape(IReadOnlyList<SKPoint> points, bool closed, SKPath? fillPath)
    {
        ClearBuffer();
        _dabs.Dispose();
        var shapeBrush = _b.Clone();
        shapeBrush.Stabilizer = 0;
        _dabs = new DabRenderer(shapeBrush, _color, _sym, capOpacity: true);
        if (fillPath != null)
        {
            // Tampon en gris (plafond d'opacité) : blanc = opacité pleine.
            using var paint = new SKPaint { IsAntialias = true, Color = _dabs.CapsOpacity ? SKColors.White : _color.WithAlpha(255) };
            foreach (var m in _sym)
            {
                using var p = new SKPath(fillPath);
                p.Transform(m);
                _bufCanvas.DrawPath(p, paint);
                _dirty = BitmapUtil.Union(_dirty, BitmapUtil.Clamp(BitmapUtil.RoundOut(p.Bounds, 2), _e.Document.Width, _e.Document.Height));
            }
        }
        if (points.Count > 0)
        {
            _path = new StrokePath(shapeBrush, OnDab);
            foreach (var pt in points) _path.Add(new InputPoint(pt.X, pt.Y, 1f));
            if (closed) _path.Add(new InputPoint(points[0].X, points[0].Y, 1f));
            _path.End();
        }
        Flush();
    }

    void ClearBuffer()
    {
        if (_dirty.IsEmpty) return;
        _buffer.Erase(SKColors.Transparent, _dirty);
        _e.Invalidate(_dirty);
        _dirty = SKRectI.Empty;
    }

    public void DrawPreview(SKCanvas c)
    {
        if (_dirty.IsEmpty) return;
        c.Save();
        _e.Selection.ClipCanvas(c);
        c.DrawBitmap(_buffer, 0, 0, _compositePaint);
        c.Restore();
    }

    public void End()
    {
        _path.End();
        Flush();
        if (!_dirty.IsEmpty)
        {
            var before = BitmapUtil.CopyRegion(Target.Bitmap, _dirty);
            using (var c = new SKCanvas(Target.Bitmap))
            {
                c.ClipRect(new SKRect(_dirty.Left, _dirty.Top, _dirty.Right, _dirty.Bottom));
                DrawPreview(c);
            }
            var after = BitmapUtil.CopyRegion(Target.Bitmap, _dirty);
            Target.Touch();
            _e.History.Push(new PixelAction(_mode == StrokeMode.Erase ? "Gomme" : "Trait", Target, _dirty, before, after));
            _e.NotifyLayerPixels(Target);
        }
        Finish();
    }

    public void Cancel() => Finish();

    void Finish()
    {
        ClearBuffer();
        _e.SetPreview(null);
        Dispose();
    }

    public void Dispose()
    {
        _bufCanvas.Dispose();
        _dabs.Dispose();
        _compositePaint.Dispose();
        _bufferFilter?.Dispose();
    }
}

public enum PixelBrushMode { Smudge, Blur }

/// <summary>Doigt (étaler) et flou : modifient directement les pixels du calque.</summary>
public sealed class PixelBrushSession
{
    readonly PaintEngine _e;
    readonly BrushSettings _b;
    readonly PixelBrushMode _mode;
    readonly Layer _target;
    readonly SKBitmap _snapshot;
    readonly List<SKMatrix> _sym;
    readonly StrokePath _path;
    readonly byte[]?[] _carried;
    readonly int _diameter;
    readonly bool _alphaLock;
    int[] _blurSrc = Array.Empty<int>(), _blurRows = Array.Empty<int>(), _blurCols = Array.Empty<int>();
    SKRectI _dirty = SKRectI.Empty;

    public PixelBrushSession(PaintEngine e, BrushSettings brush, PixelBrushMode mode)
    {
        _e = e;
        _b = brush.Clone();
        _mode = mode;
        _target = e.Document.ActiveLayer;
        _alphaLock = _target.AlphaLock;
        _snapshot = BitmapUtil.Copy(_target.Bitmap);
        _sym = e.Symmetry.GetTransforms();
        _carried = new byte[]?[_sym.Count];
        // Diamètre maximal : fenêtre du doigt et borne des empreintes (pression → taille).
        _diameter = Math.Clamp((int)MathF.Round(_b.Size), 2, 400);
        _path = new StrokePath(_b, OnDab);
    }

    /// <summary>Contrainte de règle, appliquée après la stabilisation (voir <see cref="StrokePath.Constraint"/>).</summary>
    public Func<SKPoint, SKPoint>? Constraint
    {
        get => _path.Constraint;
        set => _path.Constraint = value;
    }

    public void Add(InputPoint p) => _path.Add(p);

    void OnDab(float x, float y, float pressure, float angle, float along)
    {
        float strength = Math.Clamp(_b.Opacity * (_b.PressureOpacity ? pressure : 1f), 0f, 1f);
        float size = Math.Clamp(_b.SizeAt(pressure, along), 2f, _diameter);
        for (int s = 0; s < _sym.Count; s++)
        {
            var pt = s == 0 ? new SKPoint(x, y) : _sym[s].MapPoint(x, y);
            if (_mode == PixelBrushMode.Smudge) SmudgeDab(s, pt.X, pt.Y, strength, size);
            else BlurDab(pt.X, pt.Y, strength, size);
        }
    }

    float Falloff(float dist)
    {
        float h = Math.Clamp(_b.Hardness, 0f, 0.99f);
        if (dist >= 1f) return 0f;
        if (dist <= h) return 1f;
        float t = (dist - h) / (1f - h);
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Écrit un pixel prémultiplié calculé en flottants. Verrou alpha : l'alpha d'origine <paramref name="a0"/>
    /// est conservé et seule la teinte change, le transparent reste transparent.
    /// </summary>
    unsafe void Store(byte* p, float* n, byte a0)
    {
        if (!_alphaLock)
        {
            for (int c = 0; c < 4; c++) p[c] = (byte)Math.Clamp(n[c] + 0.5f, 0f, 255f);
            return;
        }
        if (a0 == 0 || n[3] < 0.5f) return;
        float s = a0 / n[3];
        for (int c = 0; c < 3; c++) p[c] = (byte)Math.Clamp(n[c] * s + 0.5f, 0f, a0);
        p[3] = a0;
    }

    unsafe void SmudgeDab(int index, float cx, float cy, float strength, float size)
    {
        // Fenêtre fixe (diamètre maximal) pour que la peinture portée garde sa place ;
        // le rayon réel de l'empreinte suit la pression.
        int d = _diameter;
        float r = d * 0.5f;
        float rr = MathF.Max(1f, size * 0.5f);
        int x0 = (int)MathF.Floor(cx - r), y0 = (int)MathF.Floor(cy - r);
        var bmp = _target.Bitmap;
        int w = bmp.Width, h = bmp.Height, row = bmp.RowBytes;
        byte* px = (byte*)bmp.GetPixels();
        var mask = _e.Selection.Mask;
        byte* mp = mask != null ? (byte*)mask.GetPixels() : null;
        int mrow = mask?.RowBytes ?? 0;
        float* n = stackalloc float[4];

        var carried = _carried[index];
        if (carried == null)
        {
            // Hors de la toile, le doigt prend la couleur du bord (et non du transparent qui trouerait le fond).
            carried = new byte[d * d * 4];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < d; i++)
                {
                    int X = Math.Clamp(x0 + i, 0, w - 1), Y = Math.Clamp(y0 + j, 0, h - 1);
                    byte* p = px + Y * row + X * 4;
                    int o = (j * d + i) * 4;
                    carried[o] = p[0]; carried[o + 1] = p[1]; carried[o + 2] = p[2]; carried[o + 3] = p[3];
                }
            _carried[index] = carried;
            return;
        }

        for (int j = 0; j < d; j++)
        {
            int Y = y0 + j;
            bool outY = Y < 0 || Y >= h;
            for (int i = 0; i < d; i++)
            {
                int X = x0 + i;
                int o = (j * d + i) * 4;
                if (outY || X < 0 || X >= w)
                {
                    byte* e = px + Math.Clamp(Y, 0, h - 1) * row + Math.Clamp(X, 0, w - 1) * 4;
                    carried[o] = e[0]; carried[o + 1] = e[1]; carried[o + 2] = e[2]; carried[o + 3] = e[3];
                    continue;
                }
                float dx = (X + 0.5f - cx) / rr, dy = (Y + 0.5f - cy) / rr;
                float m = Falloff(MathF.Sqrt(dx * dx + dy * dy));
                if (mp != null) m *= mp[Y * mrow + X] / 255f;
                byte* p = px + Y * row + X * 4;
                float k = m * strength;
                if (k <= 0f)
                {
                    // Hors de l'empreinte : le doigt reprend la couleur sous lui.
                    carried[o] = p[0]; carried[o + 1] = p[1]; carried[o + 2] = p[2]; carried[o + 3] = p[3];
                    continue;
                }
                for (int c = 0; c < 4; c++)
                {
                    float L = p[c], C = carried[o + c];
                    n[c] = L + (C - L) * k;
                }
                Store(p, n, p[3]);
                for (int c = 0; c < 4; c++)
                {
                    float C = carried[o + c];
                    carried[o + c] = (byte)(C + (p[c] - C) * (1f - k) + 0.5f);
                }
            }
        }
        Mark(new SKRectI(x0, y0, x0 + d, y0 + d));
    }

    static int[] Grow(ref int[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new int[length];
        return buffer;
    }

    unsafe void BlurDab(float cx, float cy, float strength, float size)
    {
        int d = Math.Max(2, (int)MathF.Round(size));
        float r = d * 0.5f;
        int rad = Math.Max(1, d / 10);
        int x0 = (int)MathF.Floor(cx - r), y0 = (int)MathF.Floor(cy - r);
        var bmp = _target.Bitmap;
        int w = bmp.Width, h = bmp.Height, row = bmp.RowBytes;
        byte* px = (byte*)bmp.GetPixels();
        var mask = _e.Selection.Mask;
        byte* mp = mask != null ? (byte*)mask.GetPixels() : null;
        int mrow = mask?.RowBytes ?? 0;
        float* n = stackalloc float[4];

        // Échantillons autour de l'empreinte (le bord de la toile est prolongé), tampons réutilisés.
        int ew = d + rad * 2, win = rad * 2 + 1;
        var src = Grow(ref _blurSrc, ew * ew * 4);
        var rows = Grow(ref _blurRows, ew * d * 4);
        var cols = Grow(ref _blurCols, d * 4);
        for (int j = 0; j < ew; j++)
        {
            byte* line = px + Math.Clamp(y0 - rad + j, 0, h - 1) * row;
            for (int i = 0; i < ew; i++)
            {
                byte* p = line + Math.Clamp(x0 - rad + i, 0, w - 1) * 4;
                int o = (j * ew + i) * 4;
                src[o] = p[0]; src[o + 1] = p[1]; src[o + 2] = p[2]; src[o + 3] = p[3];
            }
        }
        // Flou boîte séparable par sommes glissantes : coût en d² quel que soit le rayon.
        for (int j = 0; j < ew; j++)
        {
            int sb = j * ew * 4, db = j * d * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = 0; k < win; k++) sum += src[sb + k * 4 + c];
                for (int i = 0; i < d; i++)
                {
                    rows[db + i * 4 + c] = sum;
                    if (i + 1 < d) sum += src[sb + (i + win) * 4 + c] - src[sb + i * 4 + c];
                }
            }
        }
        Array.Clear(cols, 0, d * 4);
        for (int q = 0; q < win; q++)
            for (int i = 0; i < d * 4; i++) cols[i] += rows[q * d * 4 + i];
        float inv = 1f / (win * win);
        for (int j = 0; j < d; j++)
        {
            int Y = y0 + j;
            if (Y >= 0 && Y < h)
            {
                for (int i = 0; i < d; i++)
                {
                    int X = x0 + i;
                    if (X < 0 || X >= w) continue;
                    float dx = (X + 0.5f - cx) / r, dy = (Y + 0.5f - cy) / r;
                    float m = Falloff(MathF.Sqrt(dx * dx + dy * dy));
                    if (mp != null) m *= mp[Y * mrow + X] / 255f;
                    if (m <= 0f) continue;
                    float k = m * strength;
                    byte* p = px + Y * row + X * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float blurred = cols[i * 4 + c] * inv;
                        n[c] = p[c] + (blurred - p[c]) * k;
                    }
                    Store(p, n, p[3]);
                }
            }
            if (j + 1 < d)
            {
                int add = (j + win) * d * 4, sub = j * d * 4;
                for (int i = 0; i < d * 4; i++) cols[i] += rows[add + i] - rows[sub + i];
            }
        }
        Mark(new SKRectI(x0, y0, x0 + d, y0 + d));
    }

    void Mark(SKRectI r)
    {
        r = BitmapUtil.Clamp(r, _e.Document.Width, _e.Document.Height);
        if (r.IsEmpty) return;
        _dirty = BitmapUtil.Union(_dirty, r);
        _e.Invalidate(r);
    }

    public void End()
    {
        _path.End();
        if (!_dirty.IsEmpty)
        {
            var before = BitmapUtil.CopyRegion(_snapshot, _dirty);
            var after = BitmapUtil.CopyRegion(_target.Bitmap, _dirty);
            _target.Touch();
            _e.History.Push(new PixelAction(_mode == PixelBrushMode.Smudge ? "Doigt" : "Flou", _target, _dirty, before, after));
            _e.NotifyLayerPixels(_target);
        }
        _snapshot.Dispose();
    }

    public void Cancel()
    {
        if (!_dirty.IsEmpty)
        {
            using var patch = BitmapUtil.CopyRegion(_snapshot, _dirty);
            BitmapUtil.Paste(_target.Bitmap, patch, _dirty.Left, _dirty.Top);
            _e.Invalidate(_dirty);
        }
        _snapshot.Dispose();
    }
}

public enum GradientKind { Linear, Radial }

/// <summary>Outil dégradé : aperçu direct puis application au calque.</summary>
public sealed class GradientSession : ILayerPreview
{
    readonly PaintEngine _e;
    readonly SKColor _from, _to;
    readonly GradientKind _kind;
    readonly float _opacity;
    SKPoint _a, _b;

    public Layer Target { get; }

    public GradientSession(PaintEngine e, SKPoint start, SKColor from, SKColor to, GradientKind kind, float opacity)
    {
        _e = e;
        Target = e.Document.ActiveLayer;
        _a = _b = start;
        _from = from;
        _to = to;
        _kind = kind;
        _opacity = opacity;
        e.SetPreview(this);
    }

    public void Update(SKPoint end)
    {
        _b = end;
        _e.Invalidate(_e.Document.Bounds);
    }

    public void DrawPreview(SKCanvas c)
    {
        var dx = _b.X - _a.X;
        var dy = _b.Y - _a.Y;
        if (dx * dx + dy * dy < 1f) return;
        var colors = new[] { _from, _to };
        using var shader = _kind == GradientKind.Linear
            ? SKShader.CreateLinearGradient(_a, _b, colors, SKShaderTileMode.Clamp)
            : SKShader.CreateRadialGradient(_a, MathF.Sqrt(dx * dx + dy * dy), colors, SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            Shader = shader,
            IsDither = true,
            BlendMode = Target.AlphaLock ? SKBlendMode.SrcATop : SKBlendMode.SrcOver,
            Color = SKColors.White.WithAlpha((byte)(_opacity * 255)),
        };
        c.Save();
        _e.Selection.ClipCanvas(c);
        c.DrawPaint(paint);
        c.Restore();
    }

    public void End()
    {
        _e.SetPreview(null);
        var dx = _b.X - _a.X;
        var dy = _b.Y - _a.Y;
        if (dx * dx + dy * dy < 4f) { _e.Invalidate(_e.Document.Bounds); return; }
        var rect = _e.Selection.Bounds;
        var before = BitmapUtil.CopyRegion(Target.Bitmap, rect);
        using (var c = new SKCanvas(Target.Bitmap)) DrawPreview(c);
        var after = BitmapUtil.CopyRegion(Target.Bitmap, rect);
        Target.Touch();
        _e.History.Push(new PixelAction("Dégradé", Target, rect, before, after));
        _e.Invalidate(_e.Document.Bounds);
        _e.NotifyLayerPixels(Target);
    }

    public void Cancel()
    {
        _e.SetPreview(null);
        _e.Invalidate(_e.Document.Bounds);
    }
}

/// <summary>Transformation libre (déplacer, redimensionner, pivoter, retourner) du calque ou de la sélection.</summary>
public sealed class TransformSession
{
    readonly PaintEngine _e;
    readonly Layer _layer;
    readonly SKBitmap _original;
    readonly SKBitmap _base;
    readonly SKImage _floating;
    readonly bool _hadSelection;
    SKRectI _lastRect;
    bool _rendered;                     // le calque a été redessiné au moins une fois
    bool _done;                         // validée ou annulée

    public SKRect ContentBounds { get; }
    public SKMatrix Matrix { get; private set; } = SKMatrix.Identity;
    public bool IsEmpty => ContentBounds.IsEmpty;

    public TransformSession(PaintEngine e)
    {
        _e = e;
        _layer = e.Document.ActiveLayer;
        _original = BitmapUtil.Copy(_layer.Bitmap);
        _hadSelection = e.Selection.HasSelection;

        using var floating = new SKBitmap(_original.Info);
        floating.Erase(SKColors.Transparent);
        _base = new SKBitmap(_original.Info);
        _base.Erase(SKColors.Transparent);
        if (_hadSelection)
        {
            using (var c = new SKCanvas(floating))
            {
                e.Selection.ClipCanvas(c);
                c.DrawBitmap(_original, 0, 0);
            }
            using (var c = new SKCanvas(_base))
            {
                c.DrawBitmap(_original, 0, 0);
                e.Selection.ClipCanvas(c);
                c.Clear(SKColors.Transparent);
            }
        }
        else
        {
            BitmapUtil.CopyAll(_original, floating);
        }
        var b = BitmapUtil.AlphaBounds(floating);
        ContentBounds = new SKRect(b.Left, b.Top, b.Right, b.Bottom);
        _floating = SKImage.FromBitmap(floating);
        _lastRect = b;
    }

    public void SetMatrix(SKMatrix m)
    {
        if (_done) return;
        // Matrice dégénérée (échelle nulle) : le contenu disparaîtrait, on garde la dernière valide.
        if (MathF.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY) < 1e-6f) return;
        Matrix = m;
        _rendered = true;
        var newRect = BitmapUtil.Clamp(BitmapUtil.RoundOut(m.MapRect(ContentBounds), 3), _e.Document.Width, _e.Document.Height);
        var area = BitmapUtil.Union(_lastRect, newRect);
        _lastRect = newRect;
        Render(area);
    }

    /// <summary>
    /// Remet provisoirement les pixels d'origine dans le calque (enregistrement pendant la mise en veille),
    /// sans terminer la session. Sans effet si elle est terminée.
    /// </summary>
    public void ShowOriginal()
    {
        if (_done) return;
        BitmapUtil.CopyAll(_original, _layer.Bitmap);
        _e.Invalidate(_e.Document.Bounds);
    }

    /// <summary>Réaffiche la transformation en cours sur tout le calque (après <see cref="ShowOriginal"/>).</summary>
    public void ShowTransformed()
    {
        if (_done) return;
        // Jamais transformé : l'original est déjà l'image exacte de la session.
        if (!_rendered) return;
        Render(_e.Document.Bounds);
    }

    void Render(SKRectI area)
    {
        if (area.IsEmpty) return;
        using (var c = new SKCanvas(_layer.Bitmap))
        {
            c.ClipRect(new SKRect(area.Left, area.Top, area.Right, area.Bottom));
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(_base, 0, 0);
            c.SetMatrix(Matrix);
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(_floating, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        _e.Invalidate(area);
    }

    public void Commit()
    {
        if (_done) return;
        // Rien n'a bougé : pas d'entrée d'historique (une copie pleine toile pour rien).
        if (IsNearIdentity(Matrix))
        {
            Cancel();
            return;
        }
        _done = true;
        var full = _e.Document.Bounds;
        var before = _original;
        var after = BitmapUtil.Copy(_layer.Bitmap);
        _layer.Touch();
        _e.History.Push(new PixelAction("Transformation", _layer, full, before, after));
        if (_hadSelection) _e.Selection.Transform(Matrix);
        _floating.Dispose();
        _base.Dispose();
        _e.NotifyLayerPixels(_layer);
    }

    public void Cancel()
    {
        if (_done) return;
        _done = true;
        if (_rendered)
        {
            BitmapUtil.CopyAll(_original, _layer.Bitmap);
            _e.Invalidate(_e.Document.Bounds);
        }
        _original.Dispose();
        _floating.Dispose();
        _base.Dispose();
    }

    static bool IsNearIdentity(SKMatrix m) =>
        MathF.Abs(m.ScaleX - 1) < 1e-5f && MathF.Abs(m.ScaleY - 1) < 1e-5f
        && MathF.Abs(m.SkewX) < 1e-5f && MathF.Abs(m.SkewY) < 1e-5f
        && MathF.Abs(m.TransX) < 1e-3f && MathF.Abs(m.TransY) < 1e-3f
        && m.Persp0 == 0 && m.Persp1 == 0 && m.Persp2 == 1;
}
