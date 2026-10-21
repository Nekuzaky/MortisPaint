using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum SymmetryMode { None, Vertical, Horizontal, Quad, Radial, Kaleidoscope }

/// <summary>Règle de symétrie : chaque empreinte est répétée selon les transformations calculées.</summary>
public sealed class Symmetry
{
    public SymmetryMode Mode { get; set; } = SymmetryMode.None;
    public int RadialCount { get; set; } = 6;
    public float CenterX { get; set; }
    public float CenterY { get; set; }

    public bool IsActive => Mode != SymmetryMode.None;

    public string Label => Mode switch
    {
        SymmetryMode.Vertical => "Miroir vertical",
        SymmetryMode.Horizontal => "Miroir horizontal",
        SymmetryMode.Quad => "Miroir 4 quarts",
        SymmetryMode.Radial => $"Radiale ×{RadialCount}",
        SymmetryMode.Kaleidoscope => $"Kaléidoscope ×{RadialCount}",
        _ => "Aucune",
    };

    /// <summary>Retourne les matrices (canvas -> canvas) à appliquer, identité comprise.</summary>
    public List<SKMatrix> GetTransforms()
    {
        var list = new List<SKMatrix> { SKMatrix.Identity };
        float cx = CenterX, cy = CenterY;
        var mirrorX = SKMatrix.CreateScale(-1, 1, cx, cy);
        var mirrorY = SKMatrix.CreateScale(1, -1, cx, cy);
        switch (Mode)
        {
            case SymmetryMode.Vertical:
                list.Add(mirrorX);
                break;
            case SymmetryMode.Horizontal:
                list.Add(mirrorY);
                break;
            case SymmetryMode.Quad:
                list.Add(mirrorX);
                list.Add(mirrorY);
                list.Add(SKMatrix.CreateScale(-1, -1, cx, cy));
                break;
            case SymmetryMode.Radial:
            case SymmetryMode.Kaleidoscope:
                int n = Math.Clamp(RadialCount, 2, 32);
                for (int i = 1; i < n; i++)
                    list.Add(SKMatrix.CreateRotationDegrees(360f * i / n, cx, cy));
                if (Mode == SymmetryMode.Kaleidoscope)
                {
                    int count = list.Count;
                    for (int i = 0; i < count; i++)
                        list.Add(SKMatrix.Concat(list[i], mirrorX));
                }
                break;
        }
        return list;
    }

    /// <summary>Segments de guides à afficher (coordonnées toile).</summary>
    public List<(SKPoint A, SKPoint B)> GetGuides(int width, int height)
    {
        var guides = new List<(SKPoint, SKPoint)>();
        float cx = CenterX, cy = CenterY;
        float len = width + height;
        switch (Mode)
        {
            case SymmetryMode.Vertical:
                guides.Add((new SKPoint(cx, 0), new SKPoint(cx, height)));
                break;
            case SymmetryMode.Horizontal:
                guides.Add((new SKPoint(0, cy), new SKPoint(width, cy)));
                break;
            case SymmetryMode.Quad:
                guides.Add((new SKPoint(cx, 0), new SKPoint(cx, height)));
                guides.Add((new SKPoint(0, cy), new SKPoint(width, cy)));
                break;
            case SymmetryMode.Radial:
            case SymmetryMode.Kaleidoscope:
                int n = Math.Clamp(RadialCount, 2, 32);
                for (int i = 0; i < n; i++)
                {
                    double a = -Math.PI / 2 + 2 * Math.PI * i / n;
                    guides.Add((new SKPoint(cx, cy),
                        new SKPoint(cx + (float)Math.Cos(a) * len, cy + (float)Math.Sin(a) * len)));
                }
                break;
        }
        return guides;
    }
}
