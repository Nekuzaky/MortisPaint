using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public sealed record FilterParam(string Name, float Min, float Max, float Default, float Step = 1f, string Unit = "");

public sealed class FilterDef
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public FilterParam[] Params { get; init; } = Array.Empty<FilterParam>();
    /// <summary>Écrit dans dst le résultat du filtre appliqué à src (mêmes dimensions).</summary>
    public required Action<SKBitmap, SKBitmap, float[]> Apply { get; init; }
}

public static class Filters
{
    public static readonly List<FilterDef> All = new()
    {
        new FilterDef
        {
            Name = "Luminosité / Contraste", Category = "Couleur",
            Params = new[] { new FilterParam("Luminosité", -100, 100, 0), new FilterParam("Contraste", -100, 100, 0) },
            Apply = (s, d, v) =>
            {
                float b = v[0] / 100f * 255f;
                float c = v[1] / 100f;
                float factor = c >= 0 ? 1f + c * 2f : 1f + c;
                var lut = new byte[256];
                for (int i = 0; i < 256; i++)
                    lut[i] = Clamp((i - 128f) * factor + 128f + b);
                MapRgb(s, d, (ref float r, ref float g, ref float bl) => { r = lut[(int)r]; g = lut[(int)g]; bl = lut[(int)bl]; });
            },
        },
        new FilterDef
        {
            Name = "Teinte / Saturation / Luminosité", Category = "Couleur",
            Params = new[] { new FilterParam("Teinte", -180, 180, 0, 1, "°"), new FilterParam("Saturation", -100, 100, 0), new FilterParam("Luminosité", -100, 100, 0) },
            Apply = (s, d, v) =>
            {
                var hue = HueMatrix(v[0]);
                float sat = 1f + v[1] / 100f;
                float light = v[2] / 100f;
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    float nr = hue[0] * r + hue[1] * g + hue[2] * b;
                    float ng = hue[3] * r + hue[4] * g + hue[5] * b;
                    float nb = hue[6] * r + hue[7] * g + hue[8] * b;
                    float l = 0.299f * nr + 0.587f * ng + 0.114f * nb;
                    nr = l + (nr - l) * sat; ng = l + (ng - l) * sat; nb = l + (nb - l) * sat;
                    if (light > 0) { nr += (255 - nr) * light; ng += (255 - ng) * light; nb += (255 - nb) * light; }
                    else { nr *= 1 + light; ng *= 1 + light; nb *= 1 + light; }
                    r = nr; g = ng; b = nb;
                });
            },
        },
        new FilterDef
        {
            Name = "Balance des couleurs", Category = "Couleur",
            Params = new[] { new FilterParam("Rouge", -100, 100, 0), new FilterParam("Vert", -100, 100, 0), new FilterParam("Bleu", -100, 100, 0) },
            Apply = (s, d, v) =>
            {
                float dr = v[0] * 1.28f, dg = v[1] * 1.28f, db = v[2] * 1.28f;
                MapRgb(s, d, (ref float r, ref float g, ref float b) => { r += dr; g += dg; b += db; });
            },
        },
        new FilterDef
        {
            Name = "Niveaux de gris", Category = "Couleur",
            Params = new[] { new FilterParam("Intensité", 0, 100, 100, 1, "%") },
            Apply = (s, d, v) =>
            {
                float k = v[0] / 100f;
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    float l = 0.299f * r + 0.587f * g + 0.114f * b;
                    r += (l - r) * k; g += (l - g) * k; b += (l - b) * k;
                });
            },
        },
        new FilterDef
        {
            Name = "Sépia", Category = "Couleur",
            Params = new[] { new FilterParam("Intensité", 0, 100, 100, 1, "%") },
            Apply = (s, d, v) =>
            {
                float k = v[0] / 100f;
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    float sr = 0.393f * r + 0.769f * g + 0.189f * b;
                    float sg = 0.349f * r + 0.686f * g + 0.168f * b;
                    float sb = 0.272f * r + 0.534f * g + 0.131f * b;
                    r += (sr - r) * k; g += (sg - g) * k; b += (sb - b) * k;
                });
            },
        },
        new FilterDef
        {
            Name = "Négatif", Category = "Couleur",
            Apply = (s, d, v) => MapRgb(s, d, (ref float r, ref float g, ref float b) => { r = 255 - r; g = 255 - g; b = 255 - b; }),
        },
        new FilterDef
        {
            Name = "Postérisation", Category = "Couleur",
            Params = new[] { new FilterParam("Niveaux", 2, 16, 5) },
            Apply = (s, d, v) =>
            {
                int n = Math.Max(2, (int)v[0]);
                float step = 255f / (n - 1);
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    r = MathF.Round(r / step) * step; g = MathF.Round(g / step) * step; b = MathF.Round(b / step) * step;
                });
            },
        },
        new FilterDef
        {
            Name = "Seuil (noir et blanc)", Category = "Couleur",
            Params = new[] { new FilterParam("Seuil", 1, 254, 128) },
            Apply = (s, d, v) =>
            {
                float t = v[0];
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    float l = 0.299f * r + 0.587f * g + 0.114f * b;
                    r = g = b = l >= t ? 255 : 0;
                });
            },
        },
        new FilterDef
        {
            Name = "Flou gaussien", Category = "Flou",
            Params = new[] { new FilterParam("Rayon", 0, 100, 6, 0.5f, " px") },
            Apply = (s, d, v) => DrawWithFilter(s, d, v[0] <= 0 ? null : SKImageFilter.CreateBlur(v[0] * 0.5f, v[0] * 0.5f, SKShaderTileMode.Decal)),
        },
        new FilterDef
        {
            Name = "Flou de mouvement", Category = "Flou",
            Params = new[] { new FilterParam("Distance", 0, 120, 20, 1, " px"), new FilterParam("Angle", -90, 90, 0, 1, "°") },
            Apply = (s, d, v) => MotionBlur(s, d, v[0], v[1]),
        },
        new FilterDef
        {
            Name = "Netteté", Category = "Flou",
            Params = new[] { new FilterParam("Force", 0, 300, 80, 1, "%"), new FilterParam("Rayon", 0.5f, 10, 1.5f, 0.5f, " px") },
            Apply = (s, d, v) => Sharpen(s, d, v[0] / 100f, v[1]),
        },
        new FilterDef
        {
            Name = "Lueur", Category = "Effets",
            Params = new[] { new FilterParam("Rayon", 1, 80, 16, 1, " px"), new FilterParam("Intensité", 0, 200, 80, 1, "%") },
            Apply = (s, d, v) =>
            {
                BitmapUtil.CopyAll(s, d);
                using var c = new SKCanvas(d);
                using var paint = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateBlur(v[0] * 0.5f, v[0] * 0.5f, SKShaderTileMode.Decal),
                    BlendMode = SKBlendMode.Screen,
                    Color = SKColors.White.WithAlpha((byte)Math.Clamp(v[1] / 100f * 255f, 0, 255)),
                };
                c.DrawBitmap(s, 0, 0, paint);
                if (v[1] > 100)
                {
                    paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp((v[1] - 100) / 100f * 255f, 0, 255));
                    c.DrawBitmap(s, 0, 0, paint);
                }
            },
        },
        new FilterDef
        {
            Name = "Mosaïque", Category = "Effets",
            Params = new[] { new FilterParam("Taille", 2, 120, 12, 1, " px") },
            Apply = (s, d, v) => Mosaic(s, d, Math.Max(2, (int)v[0])),
        },
        new FilterDef
        {
            Name = "Bruit", Category = "Effets",
            Params = new[] { new FilterParam("Quantité", 0, 100, 20, 1, "%"), new FilterParam("Monochrome", 0, 1, 1) },
            Apply = (s, d, v) =>
            {
                var rng = new Random(42);
                float amt = v[0] / 100f * 127f;
                bool mono = v[1] >= 0.5f;
                MapRgb(s, d, (ref float r, ref float g, ref float b) =>
                {
                    float n = ((float)rng.NextDouble() * 2 - 1) * amt;
                    if (mono) { r += n; g += n; b += n; }
                    else
                    {
                        r += n;
                        g += ((float)rng.NextDouble() * 2 - 1) * amt;
                        b += ((float)rng.NextDouble() * 2 - 1) * amt;
                    }
                });
            },
        },
        new FilterDef
        {
            Name = "Vignette", Category = "Effets",
            Params = new[] { new FilterParam("Intensité", 0, 100, 50, 1, "%"), new FilterParam("Taille", 10, 100, 60, 1, "%") },
            Apply = (s, d, v) =>
            {
                BitmapUtil.CopyAll(s, d);
                using var c = new SKCanvas(d);
                float cx = s.Width / 2f, cy = s.Height / 2f;
                float radius = MathF.Sqrt(cx * cx + cy * cy);
                float inner = Math.Clamp(v[1] / 100f, 0.05f, 0.99f);
                using var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                    new[] { SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha((byte)(v[0] / 100f * 255)) },
                    new[] { inner, 1f }, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcATop };
                c.DrawPaint(paint);
            },
        },
        new FilterDef
        {
            Name = "Trame de points (demi-teinte)", Category = "Manga",
            Params = new[] { new FilterParam("Taille des cellules", 3, 48, 8, 1, " px"), new FilterParam("Angle", 0, 90, 45, 1, "°"), new FilterParam("Garder la couleur", 0, 1, 0) },
            Apply = (s, d, v) => Tones.Halftone(s, d, v[0], v[1], v[2] >= 0.5f),
        },
        new FilterDef
        {
            Name = "Extraire le trait", Category = "Dessin",
            Params = new[] { new FilterParam("Blanc → transparent", 0, 100, 25, 1, "%"), new FilterParam("Garder la couleur", 0, 1, 0) },
            Apply = (s, d, v) => ExtractLines(s, d, v[0] / 100f, v[1] >= 0.5f),
        },
        new FilterDef
        {
            Name = "Contours (détection)", Category = "Dessin",
            Params = new[] { new FilterParam("Force", 10, 400, 100, 1, "%") },
            Apply = (s, d, v) => EdgeDetect(s, d, v[0] / 100f),
        },
    };

    delegate void RgbMap(ref float r, ref float g, ref float b);

    static byte Clamp(float v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5f);

    /// <summary>Applique une transformation RVB pixel par pixel (en non prémultiplié).</summary>
    static unsafe void MapRgb(SKBitmap src, SKBitmap dst, RgbMap map)
    {
        bool bgra = src.ColorType == SKColorType.Bgra8888;
        byte* s = (byte*)src.GetPixels();
        byte* d = (byte*)dst.GetPixels();
        int n = src.Width * src.Height;
        for (int i = 0; i < n; i++, s += 4, d += 4)
        {
            byte a = s[3];
            if (a == 0) { *(uint*)d = 0; continue; }
            float inv = 255f / a;
            float r = (bgra ? s[2] : s[0]) * inv;
            float g = s[1] * inv;
            float b = (bgra ? s[0] : s[2]) * inv;
            r = MathF.Min(255, r); g = MathF.Min(255, g); b = MathF.Min(255, b);
            map(ref r, ref g, ref b);
            float k = a / 255f;
            byte R = Clamp(Math.Clamp(r, 0, 255) * k), G = Clamp(Math.Clamp(g, 0, 255) * k), B = Clamp(Math.Clamp(b, 0, 255) * k);
            if (bgra) { d[0] = B; d[1] = G; d[2] = R; }
            else { d[0] = R; d[1] = G; d[2] = B; }
            d[3] = a;
        }
    }

    static float[] HueMatrix(float degrees)
    {
        double a = degrees * Math.PI / 180;
        float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
        const float lr = 0.213f, lg = 0.715f, lb = 0.072f;
        return new[]
        {
            lr + cos * (1 - lr) + sin * -lr, lg + cos * -lg + sin * -lg, lb + cos * -lb + sin * (1 - lb),
            lr + cos * -lr + sin * 0.143f, lg + cos * (1 - lg) + sin * 0.140f, lb + cos * -lb + sin * -0.283f,
            lr + cos * -lr + sin * -(1 - lr), lg + cos * -lg + sin * lg, lb + cos * (1 - lb) + sin * lb,
        };
    }

    static void DrawWithFilter(SKBitmap src, SKBitmap dst, SKImageFilter? filter)
    {
        dst.Erase(SKColors.Transparent);
        using var c = new SKCanvas(dst);
        using var paint = new SKPaint { ImageFilter = filter };
        c.DrawBitmap(src, 0, 0, paint);
        filter?.Dispose();
    }

    static void MotionBlur(SKBitmap src, SKBitmap dst, float distance, float angleDeg)
    {
        dst.Erase(SKColors.Transparent);
        if (distance < 1) { BitmapUtil.CopyAll(src, dst); return; }
        int steps = Math.Clamp((int)distance, 2, 60);
        float rad = angleDeg * MathF.PI / 180f;
        float dx = MathF.Cos(rad) * distance, dy = MathF.Sin(rad) * distance;
        using var acc = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.RgbaF16, SKAlphaType.Premul));
        acc.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(acc))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Plus, ColorF = new SKColorF(1, 1, 1, 1f / steps) })
        {
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1) - 0.5f;
                c.DrawBitmap(src, dx * t, dy * t, paint);
            }
        }
        using var c2 = new SKCanvas(dst);
        c2.DrawBitmap(acc, 0, 0);
    }

    static unsafe void Sharpen(SKBitmap src, SKBitmap dst, float amount, float radius)
    {
        using var blur = new SKBitmap(src.Info);
        DrawWithFilter(src, blur, SKImageFilter.CreateBlur(radius, radius, SKShaderTileMode.Clamp));
        byte* s = (byte*)src.GetPixels();
        byte* b = (byte*)blur.GetPixels();
        byte* d = (byte*)dst.GetPixels();
        int n = src.Width * src.Height;
        for (int i = 0; i < n; i++, s += 4, b += 4, d += 4)
        {
            byte a = s[3];
            d[3] = a;
            for (int c = 0; c < 3; c++)
            {
                float v = s[c] + (s[c] - b[c]) * amount;
                d[c] = (byte)Math.Clamp(v, 0, a);
            }
        }
    }

    static void Mosaic(SKBitmap src, SKBitmap dst, int cell)
    {
        int sw = Math.Max(1, src.Width / cell), sh = Math.Max(1, src.Height / cell);
        using var small = BitmapUtil.Scaled(src, sw, sh);
        dst.Erase(SKColors.Transparent);
        using var c = new SKCanvas(dst);
        using var img = SKImage.FromBitmap(small);
        using var paint = new SKPaint();
        c.DrawImage(img, new SKRect(0, 0, sw * cell, sh * cell), new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }

    static unsafe void ExtractLines(SKBitmap src, SKBitmap dst, float whiteCut, bool keepColor)
    {
        bool bgra = src.ColorType == SKColorType.Bgra8888;
        byte* s = (byte*)src.GetPixels();
        byte* d = (byte*)dst.GetPixels();
        int n = src.Width * src.Height;
        for (int i = 0; i < n; i++, s += 4, d += 4)
        {
            byte a = s[3];
            if (a == 0) { *(uint*)d = 0; continue; }
            float inv = 255f / a;
            float r = MathF.Min(255, (bgra ? s[2] : s[0]) * inv), g = MathF.Min(255, s[1] * inv), b = MathF.Min(255, (bgra ? s[0] : s[2]) * inv);
            float l = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            float darkness = l > 1f - whiteCut ? 0f : Math.Clamp((1f - whiteCut - l) / MathF.Max(0.01f, 1f - whiteCut), 0f, 1f);
            float na = darkness * (a / 255f);
            byte A = Clamp(na * 255f);
            if (keepColor)
            {
                // Couleur assombrie conservée, alpha selon l'obscurité.
                float k = A / 255f;
                byte R = Clamp(r * k * 0.85f), G = Clamp(g * k * 0.85f), B = Clamp(b * k * 0.85f);
                if (bgra) { d[0] = B; d[1] = G; d[2] = R; } else { d[0] = R; d[1] = G; d[2] = B; }
            }
            else
            {
                d[0] = 0; d[1] = 0; d[2] = 0;
            }
            d[3] = A;
        }
    }

    static unsafe void EdgeDetect(SKBitmap src, SKBitmap dst, float strength)
    {
        int w = src.Width, h = src.Height;
        bool bgra = src.ColorType == SKColorType.Bgra8888;
        var lum = new float[w * h];
        byte* s = (byte*)src.GetPixels();
        for (int i = 0; i < w * h; i++)
        {
            byte* p = s + i * 4;
            float a = p[3] / 255f;
            // Luminance composée sur blanc.
            float r = (bgra ? p[2] : p[0]) + 255 * (1 - a), g = p[1] + 255 * (1 - a), b = (bgra ? p[0] : p[2]) + 255 * (1 - a);
            lum[i] = (0.299f * r + 0.587f * g + 0.114f * b);
        }
        byte* d = (byte*)dst.GetPixels();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1), ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);
                float gx = -lum[ym * w + xm] - 2 * lum[y * w + xm] - lum[yp * w + xm] + lum[ym * w + xp] + 2 * lum[y * w + xp] + lum[yp * w + xp];
                float gy = -lum[ym * w + xm] - 2 * lum[ym * w + x] - lum[ym * w + xp] + lum[yp * w + xm] + 2 * lum[yp * w + x] + lum[yp * w + xp];
                float mag = MathF.Sqrt(gx * gx + gy * gy) / 4f * strength;
                byte A = Clamp(mag);
                byte* o = d + (y * w + x) * 4;
                o[0] = 0; o[1] = 0; o[2] = 0; o[3] = A;
            }
    }
}

/// <summary>Application d'un filtre avec aperçu en direct sur le calque actif.</summary>
public sealed class FilterSession
{
    readonly PaintEngine _e;
    readonly Layer _layer;
    readonly SKBitmap _original;
    readonly SKBitmap _work;
    public FilterDef Filter { get; }
    public float[] Values { get; }

    public FilterSession(PaintEngine e, FilterDef filter)
    {
        _e = e;
        Filter = filter;
        _layer = e.Document.ActiveLayer;
        _original = BitmapUtil.Copy(_layer.Bitmap);
        _work = new SKBitmap(_original.Info);
        Values = Array.ConvertAll(filter.Params, p => p.Default);
        Preview();
    }

    public void Preview()
    {
        Filter.Apply(_original, _work, Values);
        if (_e.Selection.HasSelection)
        {
            BitmapUtil.CopyAll(_original, _layer.Bitmap);
            using var c = new SKCanvas(_layer.Bitmap);
            _e.Selection.ClipCanvas(c);
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(_work, 0, 0);
        }
        else
        {
            BitmapUtil.CopyAll(_work, _layer.Bitmap);
        }
        if (_layer.AlphaLock)
        {
            // Préserve l'alpha d'origine.
            using var c = new SKCanvas(_layer.Bitmap);
            using var p = new SKPaint { BlendMode = SKBlendMode.DstIn };
            c.DrawBitmap(_original, 0, 0, p);
        }
        _e.Invalidate(_e.Document.Bounds);
    }

    public void Commit()
    {
        var rect = _e.Selection.Bounds;
        var before = BitmapUtil.CopyRegion(_original, rect);
        var after = BitmapUtil.CopyRegion(_layer.Bitmap, rect);
        _layer.Touch();
        _e.History.Push(new PixelAction(Filter.Name, _layer, rect, before, after));
        _original.Dispose();
        _work.Dispose();
        _e.NotifyLayerPixels(_layer);
    }

    public void Cancel()
    {
        BitmapUtil.CopyAll(_original, _layer.Bitmap);
        _e.Invalidate(_e.Document.Bounds);
        _original.Dispose();
        _work.Dispose();
    }
}
