using System;
using System.Collections.Generic;
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

    public void Set(SKPath? path)
    {
        Path?.Dispose();
        Path = path;
        _mask?.Dispose();
        _mask = null;
        Changed?.Invoke();
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

    /// <summary>
    /// Contour vectoriel d'un masque binaire : les segments horizontaux identiques d'une ligne à l'autre
    /// sont fusionnés en rectangles, puis le tout est simplifié en contours propres.
    /// </summary>
    public static SKPath MaskToPath(byte[] mask, int width, SKRectI bounds)
    {
        var raw = new SKPath { FillType = SKPathFillType.Winding };
        var open = new Dictionary<(int X0, int X1), int>();
        var current = new HashSet<(int, int)>();
        for (int y = bounds.Top; y <= bounds.Bottom; y++)
        {
            current.Clear();
            if (y < bounds.Bottom)
            {
                int x = bounds.Left;
                while (x < bounds.Right)
                {
                    while (x < bounds.Right && mask[y * width + x] < 128) x++;
                    if (x >= bounds.Right) break;
                    int x0 = x;
                    while (x < bounds.Right && mask[y * width + x] >= 128) x++;
                    current.Add((x0, x));
                }
            }
            var toClose = new List<(int, int)>();
            foreach (var run in open.Keys)
                if (!current.Contains(run)) toClose.Add(run);
            foreach (var run in toClose)
            {
                raw.AddRect(new SKRect(run.Item1, open[run], run.Item2, y));
                open.Remove(run);
            }
            foreach (var run in current)
                if (!open.ContainsKey(run)) open[run] = y;
        }
        var simple = raw.Simplify();
        if (simple == null) return raw;
        raw.Dispose();
        return simple;
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
