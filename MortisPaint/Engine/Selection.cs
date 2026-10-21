using System;
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
