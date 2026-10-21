using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Dessine les empreintes d'un pinceau (rond, ellipse, carré, pixel) avec symétrie et grain.</summary>
public sealed class DabRenderer : IDisposable
{
    readonly BrushSettings _b;
    readonly SKColor _color;
    readonly List<SKMatrix> _sym;
    readonly Random _rng = new();
    readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKColor[] _gradColors;
    readonly float[] _gradPos;
    readonly SKShader? _grain;
    readonly SKColor[]?[] _grayGrad = new SKColor[]?[256];
    readonly SKColorFilter?[] _grayTip = new SKColorFilter?[256];
    SKRectI _dirty = SKRectI.Empty;

    public DabRenderer(BrushSettings brush, SKColor color, List<SKMatrix> symmetry) : this(brush, color, symmetry, false) { }

    /// <param name="capOpacity">
    /// Plafonne l'opacité du trait à la pression (« Pression → opacité ») au lieu de laisser les empreintes
    /// s'accumuler. Le tampon reçoit alors l'opacité du trait codée en gris (mélange Éclaircir = maximum) :
    /// il doit être fusionné avec <see cref="CreateBufferFilter"/>.
    /// </param>
    public DabRenderer(BrushSettings brush, SKColor color, List<SKMatrix> symmetry, bool capOpacity)
    {
        _b = brush;
        _color = color.WithAlpha(255);
        _sym = symmetry;
        CapsOpacity = capOpacity && brush.PressureOpacity;
        float h = Math.Clamp(brush.Hardness, 0f, 0.98f);
        _gradPos = new[] { 0f, h, h + (1 - h) * 0.33f, h + (1 - h) * 0.66f, 1f };
        _gradColors = GradientFor(_color);
        if (brush.Grain > 0.01f) _grain = GrainTexture.Get(brush.Grain);
        if (brush.Shape == BrushShape.Image)
        {
            _tip = BrushTips.Get(brush.TipId);
            _tipPaint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(_color, SKBlendMode.SrcIn) };
        }
        if (CapsOpacity)
        {
            _paint.BlendMode = SKBlendMode.Lighten;
            if (_tipPaint != null) _tipPaint.BlendMode = SKBlendMode.Lighten;
        }
    }

    readonly SKImage? _tip;
    readonly SKPaint? _tipPaint;

    /// <summary>Le tampon contient l'opacité du trait en gris (voir <see cref="CreateBufferFilter"/>).</summary>
    public bool CapsOpacity { get; }

    /// <summary>
    /// Filtre de fusion du tampon : convertit le gris (opacité plafonnée) en couleur du trait.
    /// Null hors plafond d'opacité, le tampon contenant alors directement la couleur.
    /// </summary>
    public SKColorFilter? CreateBufferFilter()
    {
        if (!CapsOpacity) return null;
        // Luma d'un gris prémultiplié = sa valeur : elle devient l'alpha, puis on teinte.
        using var luma = SKColorFilter.CreateLumaColor();
        using var tint = SKColorFilter.CreateBlendMode(_color, SKBlendMode.SrcIn);
        return SKColorFilter.CreateCompose(tint, luma);
    }

    static SKColor[] GradientFor(SKColor ink) => new[]
    {
        ink, ink, ink.WithAlpha(158), ink.WithAlpha(56), ink.WithAlpha(0),
    };

    public SKRectI TakeDirty()
    {
        var d = _dirty;
        _dirty = SKRectI.Empty;
        return d;
    }

    public float SizeAt(float pressure, float along) => _b.SizeAt(pressure, along);

    public void Dab(SKCanvas c, float x, float y, float pressure, float angle, float along)
    {
        float baseSize = SizeAt(pressure, along);
        float press = _b.PressureOpacity ? Math.Clamp(pressure, 0.05f, 1f) : 1f;
        // Plafond : la pression fixe le gris (opacité maximale), le débit la densité de l'empreinte.
        SKColor ink = _color;
        float alpha = _b.Flow * press;
        if (CapsOpacity)
        {
            byte g = (byte)Math.Clamp(press * 255f + 0.5f, 1f, 255f);
            ink = new SKColor(g, g, g);
            alpha = _b.Flow;
        }
        int count = Math.Max(1, _b.Count);
        bool spray = count > 1;

        for (int k = 0; k < count; k++)
        {
            float d = spray ? MathF.Max(1f, baseSize * 0.05f) : baseSize;
            if (_b.SizeJitter > 0) d *= 1f + ((float)_rng.NextDouble() * 2f - 1f) * _b.SizeJitter;
            float px = x, py = y;
            if (_b.Scatter > 0)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                double rr = Math.Sqrt(_rng.NextDouble()) * _b.Scatter * baseSize;
                px += (float)(Math.Cos(a) * rr);
                py += (float)(Math.Sin(a) * rr);
            }
            // Angle de la pointe (fixe ou direction du trait, fourni par StrokePath) ; la rotation
            // aléatoire est tirée une fois pour que les copies symétriques restent des reflets exacts.
            float deg = angle * 180f / MathF.PI
                        + (_b.RotationJitter > 0 ? ((float)_rng.NextDouble() * 2 - 1) * _b.RotationJitter : 0);

            for (int s = 0; s < _sym.Count; s++)
            {
                var m = _sym[s];
                float sx = px, sy = py;
                var lin = SKMatrix.Identity;
                if (s > 0)
                {
                    var mp = m.MapPoint(px, py);
                    sx = mp.X; sy = mp.Y;
                    // Partie linéaire (rotation, miroir) : la pointe est reflétée comme le trait.
                    lin = new SKMatrix(m.ScaleX, m.SkewX, 0, m.SkewY, m.ScaleY, 0, 0, 0, 1);
                }
                DrawShape(c, sx, sy, MathF.Max(0.3f, d), deg, alpha, ink, lin);
            }
        }
    }

    void DrawShape(SKCanvas c, float x, float y, float d, float deg, float alpha, SKColor ink, SKMatrix lin)
    {
        byte a8 = (byte)Math.Clamp(alpha * 255f + 0.5f, 1f, 255f);
        float r = d * 0.5f;

        if (_b.Shape == BrushShape.Pixel)
        {
            int s = Math.Max(1, (int)MathF.Round(d));
            int ix = (int)MathF.Floor(x - s * 0.5f + 0.5f);
            int iy = (int)MathF.Floor(y - s * 0.5f + 0.5f);
            _paint.IsAntialias = false;
            _paint.Shader = null;
            _paint.Color = ink.WithAlpha(a8);
            c.DrawRect(ix, iy, s, s, _paint);
            Mark(new SKRectI(ix, iy, ix + s, iy + s));
            return;
        }

        if (_b.Shape == BrushShape.Image)
        {
            if (_tip == null || _tipPaint == null) return;
            float scale = d / Math.Max(_tip.Width, _tip.Height);
            var tipLocal = SKMatrix.CreateTranslation(x, y)
                .PreConcat(lin)
                .PreConcat(SKMatrix.CreateRotationDegrees(deg))
                .PreConcat(SKMatrix.CreateScale(scale, scale));
            if (CapsOpacity) _tipPaint.ColorFilter = _grayTip[ink.Red] ??= SKColorFilter.CreateBlendMode(ink, SKBlendMode.SrcIn);
            _tipPaint.Color = SKColors.White.WithAlpha(a8);
            c.Save();
            c.Concat(in tipLocal);
            c.DrawImage(_tip, -_tip.Width / 2f, -_tip.Height / 2f, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _tipPaint);
            c.Restore();
            float hw = _tip.Width / 2f, hh = _tip.Height / 2f;
            Mark(BitmapUtil.RoundOut(tipLocal.MapRect(new SKRect(-hw, -hh, hw, hh)), 2));
            return;
        }

        _paint.IsAntialias = true;
        _paint.Color = ink.WithAlpha(a8);
        bool soft = _b.Hardness < 0.97f && _b.Shape != BrushShape.Square;
        var grad = ink == _color ? _gradColors : _grayGrad[ink.Red] ??= GradientFor(ink);

        if (_b.Shape == BrushShape.Round)
        {
            SKShader? shader = soft
                ? SKShader.CreateRadialGradient(new SKPoint(x, y), r, grad, _gradPos, SKShaderTileMode.Clamp)
                : null;
            ApplyGrain(shader, SKMatrix.Identity, ink, out var finalShader);
            _paint.Shader = finalShader;
            c.DrawCircle(x, y, r, _paint);
            _paint.Shader = null;
            finalShader?.Dispose();
            if (!ReferenceEquals(shader, finalShader)) shader?.Dispose();
            Mark(BitmapUtil.RoundOut(new SKRect(x - r, y - r, x + r, y + r), 2));
        }
        else
        {
            float ry = _b.Shape == BrushShape.Ellipse ? Math.Clamp(_b.Roundness, 0.05f, 1f) : 1f;
            var local = SKMatrix.CreateTranslation(x, y)
                .PreConcat(lin)
                .PreConcat(SKMatrix.CreateRotationDegrees(deg))
                .PreConcat(SKMatrix.CreateScale(1, ry));
            c.Save();
            c.Concat(in local);
            SKShader? shader = soft
                ? SKShader.CreateRadialGradient(new SKPoint(0, 0), r, grad, _gradPos, SKShaderTileMode.Clamp)
                : null;
            local.TryInvert(out var inv);
            ApplyGrain(shader, inv, ink, out var finalShader);
            _paint.Shader = finalShader;
            if (_b.Shape == BrushShape.Square) c.DrawRect(-r, -r, d, d, _paint);
            else c.DrawCircle(0, 0, r, _paint);
            _paint.Shader = null;
            finalShader?.Dispose();
            if (!ReferenceEquals(shader, finalShader)) shader?.Dispose();
            c.Restore();
            // Emprise réelle de la forme tournée (un carré à 45° déborde de r·√2).
            Mark(BitmapUtil.RoundOut(local.MapRect(new SKRect(-r, -r, r, r)), 2));
        }
    }

    void ApplyGrain(SKShader? shape, SKMatrix grainLocal, SKColor ink, out SKShader? result)
    {
        if (_grain == null) { result = shape; return; }
        using var grain = grainLocal.IsIdentity ? null : _grain.WithLocalMatrix(grainLocal);
        using var solid = shape == null ? SKShader.CreateColor(ink) : null;
        result = SKShader.CreateCompose(shape ?? solid!, grain ?? _grain, SKBlendMode.DstIn);
    }

    void Mark(SKRectI r) => _dirty = BitmapUtil.Union(_dirty, r);

    public void Dispose()
    {
        _paint.Dispose();
        _tipPaint?.Dispose();
        foreach (var f in _grayTip) f?.Dispose();
    }
}

/// <summary>Texture de grain « papier » partagée, fixée à la toile.</summary>
public static class GrainTexture
{
    const int N = 160;
    static float[]? _noise;
    static readonly Dictionary<int, SKShader> Cache = new();

    public static SKShader Get(float amount)
    {
        int key = Math.Clamp((int)MathF.Round(amount * 20f), 1, 20);
        if (Cache.TryGetValue(key, out var shader)) return shader;
        var noise = _noise ??= BuildNoise();
        float g = key / 20f;
        var bmp = new SKBitmap(BitmapUtil.Info(N, N)); // conservé : référencé par le shader
        unsafe
        {
            byte* p = (byte*)bmp.GetPixels();
            for (int i = 0; i < N * N; i++)
            {
                float a = 1f - g * noise[i];
                byte v = (byte)Math.Clamp(a * 255f, 0f, 255f);
                p[i * 4] = v; p[i * 4 + 1] = v; p[i * 4 + 2] = v; p[i * 4 + 3] = v;
            }
        }
        shader = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        Cache[key] = shader;
        return shader;
    }

    static float[] BuildNoise()
    {
        var rng = new Random(1337);
        var raw = new float[N * N];
        for (int i = 0; i < raw.Length; i++) raw[i] = (float)rng.NextDouble();
        // Léger flou pour imiter le grain du papier, puis contraste.
        var outp = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += raw[((y + dy + N) % N) * N + (x + dx + N) % N];
                float v = sum / 9f;
                v = Math.Clamp((v - 0.5f) * 3.2f + 0.5f, 0f, 1f);
                outp[y * N + x] = v;
            }
        return outp;
    }
}
