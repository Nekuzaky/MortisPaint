using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Sélection vectorielle (rectangle, ellipse, lasso) qui limite peinture, remplissage et filtres.</summary>
public sealed class Selection : IDisposable
{
    readonly int _width, _height;
    SKBitmap? _mask;

    public Selection(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public SKPath? Path { get; private set; }
    public bool HasSelection => Path != null;

    public event Action? Changed;

    int _version;

    public void Set(SKPath? path)
    {
        // Une forme sans surface (rectangle plat, lasso réduit à un trait) ne laisserait rien peindre :
        // elle vaut absence de sélection.
        if (path != null && IsDegenerate(path))
        {
            path.Dispose();
            path = null;
        }
        Path?.Dispose();
        Path = path;
        _mask?.Dispose();
        _mask = null;
        _version++;
        Changed?.Invoke();
    }

    /// <summary>Vrai si le contour n'enferme aucune surface : boîte plate, ou tous ses points alignés.</summary>
    static bool IsDegenerate(SKPath p)
    {
        if (p.IsEmpty) return true;
        var b = p.TightBounds;
        if (!(b.Width >= 0.5f && b.Height >= 0.5f)) return true;
        // Lasso tracé d'un geste rectiligne en biais : points alignés (vérifié sur les petits contours seulement).
        if (p.PointCount > 4096) return false;
        var pts = p.Points;
        var a = pts[0];
        SKPoint far = a;
        float best = 0;
        foreach (var q in pts)
        {
            float d = SKPoint.DistanceSquared(a, q);
            if (d > best) { best = d; far = q; }
        }
        float len = MathF.Sqrt(best);
        if (len < 0.5f) return true;
        float nx = -(far.Y - a.Y) / len, ny = (far.X - a.X) / len;
        foreach (var q in pts)
            if (MathF.Abs((q.X - a.X) * nx + (q.Y - a.Y) * ny) >= 0.5f) return false;
        return true;
    }

    /// <summary>
    /// Applique <paramref name="change"/> et, s'il a modifié la sélection, l'inscrit dans l'historique
    /// pour qu'Annuler la rétablisse. Retourne vrai si la sélection a changé.
    /// </summary>
    public bool Record(History history, string name, Action change)
    {
        var before = Path == null ? null : new SKPath(Path);
        int version = _version;
        change();
        if (version == _version || (before == null && Path == null))
        {
            before?.Dispose();
            return false;
        }
        history.Push(new SelectionAction(name, before, Path == null ? null : new SKPath(Path)));
        return true;
    }

    public void Clear() => Set(null);

    public void SelectAll()
    {
        var p = new SKPath();
        p.AddRect(new SKRect(0, 0, _width, _height));
        Set(p);
    }

    public void Invert()
    {
        if (Path == null) return;
        var p = new SKPath(Path)
        {
            FillType = Path.FillType == SKPathFillType.InverseWinding || Path.FillType == SKPathFillType.InverseEvenOdd
                ? SKPathFillType.Winding
                : SKPathFillType.InverseWinding,
        };
        Set(p);
    }

    /// <summary>Combine une nouvelle forme avec la sélection existante (remplacer, ajouter, retirer).</summary>
    public void Combine(SKPath shape, SelectionOp op)
    {
        if (op == SelectionOp.Replace || Path == null)
        {
            if (op == SelectionOp.Subtract) { shape.Dispose(); return; }
            Set(shape);
            return;
        }
        var result = Path.Op(shape, op == SelectionOp.Add ? SKPathOp.Union : SKPathOp.Difference);
        shape.Dispose();
        if (result == null) return;
        Set(result.IsEmpty ? null : result);
    }

    // Directions des arêtes du contour, sur la grille des coins de pixels (y vers le bas).
    const byte DirRight = 1, DirDown = 2, DirLeft = 4, DirUp = 8;

    /// <summary>
    /// Contour vectoriel exact d'un masque binaire (≥ 128 = sélectionné), en un passage linéaire :
    /// chaque côté de pixel entre plein et vide devient une arête orientée (le plein à sa droite),
    /// les arêtes sont chaînées en boucles fermées et seuls les coins sont gardés. Les trous tournent
    /// en sens inverse, si bien que le remplissage Winding est juste sans Path.Simplify.
    /// </summary>
    public static SKPath MaskToPath(byte[] mask, int width, SKRectI bounds)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        int bx = bounds.Left, by = bounds.Top, bw = bounds.Width, bh = bounds.Height;
        if (bw <= 0 || bh <= 0) return path;
        int vw = bw + 1;
        // Arêtes sortantes de chaque coin (bits Dir*).
        var outs = new byte[vw * (bh + 1)];
        for (int y = 0; y < bh; y++)
        {
            int row = (by + y) * width + bx;
            int up = row - width, down = row + width;
            bool hasUp = y > 0, hasDown = y < bh - 1;
            int v = y * vw;
            for (int x = 0; x < bw; x++, v++)
            {
                if (mask[row + x] < 128) continue;
                if (!hasUp || mask[up + x] < 128) outs[v] |= DirRight;
                if (x == bw - 1 || mask[row + x + 1] < 128) outs[v + 1] |= DirDown;
                if (!hasDown || mask[down + x] < 128) outs[v + vw + 1] |= DirLeft;
                if (x == 0 || mask[row + x - 1] < 128) outs[v + vw] |= DirUp;
            }
        }

        var loop = new List<SKPoint>();
        var span = outs.AsSpan();
        int from = 0;
        while (true)
        {
            int k = span[from..].IndexOfAnyExcept((byte)0);
            if (k < 0) break;
            int start = from + k;
            from = start;
            int vtx = start;
            int o = outs[vtx];
            int dir = o & -o;
            loop.Clear();
            loop.Add(new SKPoint(bx + vtx % vw, by + vtx / vw));
            while (true)
            {
                outs[vtx] &= (byte)~dir;
                vtx += dir switch { DirRight => 1, DirDown => vw, DirLeft => -1, _ => -vw };
                o = outs[vtx];
                if (o == 0) break;
                // Un seul départ possible, sauf au coin commun de deux pixels en diagonale : on tourne à droite.
                int next = (o & (o - 1)) == 0 ? o : ((dir << 1) | (dir >> 3)) & 0xF;
                if ((o & next) == 0) next = o & -o;
                if (next != dir) loop.Add(new SKPoint(bx + vtx % vw, by + vtx / vw));
                dir = next;
            }
            if (loop.Count >= 3) path.AddPoly(CollectionsMarshal.AsSpan(loop), true);
        }
        return path;
    }

    public void Transform(SKMatrix m)
    {
        if (Path == null) return;
        var p = new SKPath(Path);
        p.Transform(m);
        Set(p);
    }

    public bool IsInverse => Path != null &&
        (Path.FillType == SKPathFillType.InverseWinding || Path.FillType == SKPathFillType.InverseEvenOdd);

    /// <summary>Rectangle concerné (toute la toile si aucune sélection ou sélection inversée).</summary>
    public SKRectI Bounds
    {
        get
        {
            var full = new SKRectI(0, 0, _width, _height);
            if (Path == null || IsInverse) return full;
            return BitmapUtil.Clamp(BitmapUtil.RoundOut(Path.Bounds, 2), _width, _height);
        }
    }

    /// <summary>Masque Alpha8 (255 = sélectionné), calculé à la demande.</summary>
    public SKBitmap? Mask
    {
        get
        {
            if (Path == null) return null;
            if (_mask != null) return _mask;
            _mask = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Alpha8, SKAlphaType.Premul));
            _mask.Erase(SKColors.Transparent);
            using var c = new SKCanvas(_mask);
            using var p = new SKPaint { IsAntialias = true, Color = SKColors.White };
            c.DrawPath(Path, p);
            return _mask;
        }
    }

    public void ClipCanvas(SKCanvas c)
    {
        if (Path != null) c.ClipPath(Path, SKClipOperation.Intersect, true);
    }

    public void Dispose()
    {
        Path?.Dispose();
        _mask?.Dispose();
    }
}

/// <summary>Changement de sélection annulable : copies du contour avant / après (null = aucune sélection).</summary>
public sealed class SelectionAction : IHistoryAction
{
    readonly SKPath? _before, _after;

    public SelectionAction(string name, SKPath? before, SKPath? after)
    {
        Name = name;
        _before = before;
        _after = after;
    }

    public string Name { get; }
    public long Bytes => Size(_before) + Size(_after) + 64;
    public IEnumerable<Layer> ReferencedLayers => Array.Empty<Layer>();

    static long Size(SKPath? p) => p == null ? 0 : p.PointCount * 8L + p.VerbCount;

    public void Undo(PaintEngine e) => e.Selection.Set(_before == null ? null : new SKPath(_before));
    public void Redo(PaintEngine e) => e.Selection.Set(_after == null ? null : new SKPath(_after));

    public void Dispose()
    {
        _before?.Dispose();
        _after?.Dispose();
    }
}
