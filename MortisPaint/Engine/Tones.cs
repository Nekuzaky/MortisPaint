using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum ToneKind { Solid, Dots, Lines, Cross, Grain }

/// <summary>Trames manga : motifs répétés (points, lignes, hachures, grain) utilisés par le pot de peinture.</summary>
public static class Tones
{
    public static readonly (ToneKind Kind, string Label)[] All =
    {
        (ToneKind.Solid, "Couleur"), (ToneKind.Dots, "Points"), (ToneKind.Lines, "Lignes"),
        (ToneKind.Cross, "Hachures"), (ToneKind.Grain, "Grain"),
    };

    // Les bitmaps des motifs doivent rester vivants tant que les shaders les utilisent.
    static readonly Dictionary<(ToneKind, int, float), SKBitmap> Tiles = new();

    /// <summary>Shader du motif dans la couleur donnée, ou null pour un aplat.</summary>
    public static SKShader? Create(ToneKind kind, SKColor color, float cell, float density, float angleDegrees)
    {
        if (kind == ToneKind.Solid) return null;
        int s = Math.Clamp((int)MathF.Round(cell), 3, 128);
        density = Math.Clamp(density, 0.05f, 0.95f);
        var key = (kind, s, MathF.Round(density, 2));
        if (!Tiles.TryGetValue(key, out var tile))
        {
            tile = BuildTile(kind, s, density);
            Tiles[key] = tile;
        }
        var local = SKMatrix.CreateRotationDegrees(kind == ToneKind.Grain ? 0 : angleDegrees);
        using var pattern = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, local);
        using var solid = SKShader.CreateColor(color.WithAlpha(255));
        // Couleur masquée par l'alpha du motif.
        return SKShader.CreateCompose(solid, pattern, SKBlendMode.DstIn);
    }

    static SKBitmap BuildTile(ToneKind kind, int s, float density)
    {
        var bmp = new SKBitmap(BitmapUtil.Info(s, s));
        bmp.Erase(SKColors.Transparent);
        using var c = new SKCanvas(bmp);
        using var p = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        switch (kind)
        {
            case ToneKind.Dots:
            {
                // Rayon tel que la surface couverte corresponde à la densité.
                float r = MathF.Sqrt(density * s * s / MathF.PI);
                c.DrawCircle(s / 2f, s / 2f, MathF.Min(r, s * 0.7f), p);
                break;
            }
            case ToneKind.Lines:
                c.DrawRect(0, (s - s * density) / 2f, s, s * density, p);
                break;
            case ToneKind.Cross:
            {
                float w = s * density * 0.55f;
                c.DrawRect(0, (s - w) / 2f, s, w, p);
                c.DrawRect((s - w) / 2f, 0, w, s, p);
                break;
            }
            case ToneKind.Grain:
            {
                var rng = new Random(7);
                int n = (int)(s * s * density * 0.5f);
                for (int i = 0; i < n; i++)
                    c.DrawCircle((float)rng.NextDouble() * s, (float)rng.NextDouble() * s, 0.6f, p);
                break;
            }
        }
        return bmp;
    }

    // Rayon d'un point (en cellules) qui encre la part i/255 de sa cellule. Au-delà de π/4 le disque déborde sur
    // les cellules voisines : on inverse alors l'aire du disque coupé par la cellule (un carré unité).
    static readonly float[] DotRadius = BuildDotRadius();

    static float[] BuildDotRadius()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float cover = i / 255f;
            if (cover <= MathF.PI / 4f)
            {
                lut[i] = MathF.Sqrt(cover / MathF.PI);
                continue;
            }
            float lo = 0.5f, hi = MathF.Sqrt(0.5f);
            for (int k = 0; k < 24; k++)
            {
                float mid = (lo + hi) / 2f;
                if (CellCoverage(mid) < cover) lo = mid; else hi = mid;
            }
            lut[i] = hi;
        }
        return lut;
    }

    /// <summary>Aire d'un disque de rayon r (½ ≤ r ≤ √½) centré dans une cellule unité, limitée à la cellule.</summary>
    static float CellCoverage(float r)
    {
        float chord = MathF.Sqrt(r * r - 0.25f);
        float segment = r * r * MathF.Acos(0.5f / r) - 0.5f * chord;
        return MathF.PI * r * r - 4f * segment;
    }

    /// <summary>Filtre demi-teinte : transforme les valeurs du calque en points de trame.</summary>
    public static void Halftone(SKBitmap src, SKBitmap dst, float cell, float angleDegrees, bool keepColor)
    {
        cell = Math.Max(3, cell);
        int w = src.Width, h = src.Height;
        using var blurred = new SKBitmap(src.Info);
        blurred.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(blurred))
        using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(cell * 0.35f, cell * 0.35f, SKShaderTileMode.Clamp) })
            c.DrawBitmap(src, 0, 0, p);

        dst.Erase(SKColors.Transparent);
        using var dc = new SKCanvas(dst);
        using var dot = new SKPaint { IsAntialias = true };
        double a = angleDegrees * Math.PI / 180;
        float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
        float cx = w / 2f, cy = h / 2f;
        float half = MathF.Sqrt(w * w + h * h) / 2f + cell;
        for (float u = -half; u <= half && !Filters.Cancelled; u += cell)
        {
            for (float v = -half; v <= half; v += cell)
            {
                float x = cx + u * cos - v * sin, y = cy + u * sin + v * cos;
                if (x < -cell || y < -cell || x >= w + cell || y >= h + cell) continue;
                var col = blurred.GetPixel(Math.Clamp((int)x, 0, w - 1), Math.Clamp((int)y, 0, h - 1));
                if (col.Alpha < 8) continue;
                float lum = (0.299f * col.Red + 0.587f * col.Green + 0.114f * col.Blue) / 255f;
                float dark = 1f - lum;
                if (dark < 0.02f) continue;
                // Part de la cellule encrée = obscurité (gris à 50 % → 50 % de points).
                float r = cell * DotRadius[Math.Clamp((int)(dark * 255f + 0.5f), 0, 255)];
                dot.Color = keepColor ? col.WithAlpha(255) : SKColors.Black;
                dot.Color = dot.Color.WithAlpha(col.Alpha);
                dc.DrawCircle(x, y, r, dot);
            }
        }
    }
}
