using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Marge (px) de contexte dont le filtre a besoin autour d'une zone pour la calculer sur un extrait de la toile ;
    /// null si le résultat dépend de la position dans la toile (le filtre est alors calculé sur toute la toile).
    /// </summary>
    public Func<float[], int>? Margin { get; init; }
}

public static class Filters
{
    public static readonly List<FilterDef> All = new()
    {
        new FilterDef
        {
            Name = "Luminosité / Contraste", Category = "Couleur",
            Params = new[] { new FilterParam("Luminosité", -100, 100, 0), new FilterParam("Contraste", -100, 100, 0) },
            Margin = _ => 0,
            Apply = (s, d, v) =>
            {
                float b = v[0] / 100f * 255f;
                float c = v[1] / 100f;
                float factor = c >= 0 ? 1f + c * 2f : 1f + c;
                var lut = new byte[256];
                for (int i = 0; i < 256; i++)
                    lut[i] = Clamp((i - 128f) * factor + 128f + b);
                // Indice arrondi : réglages neutres = pixels identiques.
                MapRgb(s, d, (ref float r, ref float g, ref float bl) => { r = lut[(int)(r + 0.5f)]; g = lut[(int)(g + 0.5f)]; bl = lut[(int)(bl + 0.5f)]; });
            },
        },
        new FilterDef
        {
            Name = "Teinte / Saturation / Luminosité", Category = "Couleur",
            Params = new[] { new FilterParam("Teinte", -180, 180, 0, 1, "°"), new FilterParam("Saturation", -100, 100, 0), new FilterParam("Luminosité", -100, 100, 0) },
            Margin = _ => 0,
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
            Margin = _ => 0,
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
            Margin = _ => 0,
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
            Margin = _ => 0,
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
            Margin = _ => 0,
            Apply = (s, d, v) => MapRgb(s, d, (ref float r, ref float g, ref float b) => { r = 255 - r; g = 255 - g; b = 255 - b; }),
        },
        new FilterDef
        {
            Name = "Postérisation", Category = "Couleur",
            Params = new[] { new FilterParam("Niveaux", 2, 16, 5) },
            Margin = _ => 0,
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
            Margin = _ => 0,
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
            // Marge de 3 sigmas ; Clamp : les bords de la toile ne s'estompent pas dans le transparent.
            Margin = v => (int)MathF.Ceiling(v[0] * 1.5f) + 2,
            Apply = (s, d, v) => DrawWithFilter(s, d, v[0] <= 0 ? null : SKImageFilter.CreateBlur(v[0] * 0.5f, v[0] * 0.5f, SKShaderTileMode.Clamp)),
        },
        new FilterDef
        {
            Name = "Flou de mouvement", Category = "Flou",
            Params = new[] { new FilterParam("Distance", 0, 120, 20, 1, " px"), new FilterParam("Angle", -90, 90, 0, 1, "°") },
            Margin = v => (int)MathF.Ceiling(v[0] / 2f) + 2,
            Apply = (s, d, v) => MotionBlur(s, d, v[0], v[1]),
        },
        new FilterDef
        {
            Name = "Netteté", Category = "Flou",
            Params = new[] { new FilterParam("Force", 0, 300, 80, 1, "%"), new FilterParam("Rayon", 0.5f, 10, 1.5f, 0.5f, " px") },
            Margin = v => (int)MathF.Ceiling(v[1] * 3f) + 2,
            Apply = (s, d, v) => Sharpen(s, d, v[0] / 100f, v[1]),
        },
        new FilterDef
        {
            Name = "Lueur", Category = "Effets",
            Params = new[] { new FilterParam("Rayon", 1, 80, 16, 1, " px"), new FilterParam("Intensité", 0, 200, 80, 1, "%") },
            Margin = v => (int)MathF.Ceiling(v[0] * 1.5f) + 2,
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
            Margin = _ => 0,
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
            Margin = _ => 0,
            Apply = (s, d, v) => ExtractLines(s, d, v[0] / 100f, v[1] >= 0.5f),
        },
        new FilterDef
        {
            Name = "Contours (détection)", Category = "Dessin",
            Params = new[] { new FilterParam("Force", 10, 400, 100, 1, "%") },
            Margin = _ => 2,
            Apply = (s, d, v) => EdgeDetect(s, d, v[0] / 100f),
        },
    };

    // Annulation du calcul en cours sur ce fil (aperçu d'arrière-plan) : les filtres longs s'arrêtent tôt.
    [ThreadStatic] static CancellationToken _cancel;

    /// <summary>Le calcul en cours sur ce fil est abandonné : son résultat sera jeté.</summary>
    internal static bool Cancelled => _cancel.IsCancellationRequested;

    /// <summary>Applique le filtre ; les filtres longs consultent <see cref="Cancelled"/> et s'arrêtent tôt si le jeton est annulé.</summary>
    internal static void Apply(FilterDef filter, SKBitmap src, SKBitmap dst, float[] values, CancellationToken cancel)
    {
        var previous = _cancel;
        _cancel = cancel;
        try { filter.Apply(src, dst, values); }
        finally { _cancel = previous; }
    }

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
        // Image sans copie des pixels ; Clamp : les bords se prolongent au lieu de s'estomper dans le transparent.
        using (var pixels = src.PeekPixels())
        using (var img = SKImage.FromPixels(pixels))
        using (var c = new SKCanvas(acc))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Plus, ColorF = new SKColorF(1, 1, 1, 1f / steps) })
        {
            var rect = new SKRect(0, 0, src.Width, src.Height);
            for (int i = 0; i < steps && !Cancelled; i++)
            {
                float t = i / (float)(steps - 1) - 0.5f;
                using var shader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(dx * t, dy * t));
                paint.Shader = shader;
                c.DrawRect(rect, paint);
            }
            paint.Shader = null;
        }
        // Le cumul en demi-flottants perd un peu à chaque ajout (un opaque finit à 254) : la somme obtenue
        // pour un pixel opaque est mesurée, puis l'alpha est remis à l'échelle (matrice en non prémultiplié).
        float total = OpaqueSum(steps);
        using var c2 = new SKCanvas(dst);
        using var fix = new SKPaint();
        if (total > 0 && total < 1)
            fix.ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                1, 0, 0, 0, 0,
                0, 1, 0, 0, 0,
                0, 0, 1, 0, 0,
                0, 0, 0, 1f / total, 0,
            });
        c2.DrawBitmap(acc, 0, 0, fix);
    }

    /// <summary>Alpha cumulé, dans le même format que le flou de mouvement, de <paramref name="steps"/> ajouts d'un opaque.</summary>
    static unsafe float OpaqueSum(int steps)
    {
        using var probe = new SKBitmap(new SKImageInfo(1, 1, SKColorType.RgbaF16, SKAlphaType.Premul));
        probe.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(probe))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Plus, ColorF = new SKColorF(1, 1, 1, 1f / steps) })
            for (int i = 0; i < steps; i++) c.DrawRect(new SKRect(0, 0, 1, 1), paint);
        return (float)((Half*)probe.GetPixels())[3];
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

    /// <summary>Moyenne exacte (prémultipliée) de chaque case ; les cases incomplètes du bord droit et du bas comptent aussi.</summary>
    static unsafe void Mosaic(SKBitmap src, SKBitmap dst, int cell)
    {
        int w = src.Width, h = src.Height;
        int cols = (w + cell - 1) / cell;
        var sum = new int[cols * 4];
        var avg = new uint[cols];
        byte* s = (byte*)src.GetPixels();
        byte* d = (byte*)dst.GetPixels();
        int sRow = src.RowBytes, dRow = dst.RowBytes;
        for (int y0 = 0; y0 < h; y0 += cell)
        {
            int y1 = Math.Min(h, y0 + cell);
            Array.Clear(sum);
            for (int y = y0; y < y1; y++)
            {
                byte* p = s + y * sRow;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int k = x / cell * 4;
                    sum[k] += p[0]; sum[k + 1] += p[1]; sum[k + 2] += p[2]; sum[k + 3] += p[3];
                }
            }
            for (int cx = 0; cx < cols; cx++)
            {
                int n = (Math.Min(w, (cx + 1) * cell) - cx * cell) * (y1 - y0);
                uint v = 0;
                for (int ch = 3; ch >= 0; ch--)
                    v = (v << 8) | (uint)((sum[cx * 4 + ch] + n / 2) / n);
                avg[cx] = v;
            }
            for (int y = y0; y < y1; y++)
            {
                uint* q = (uint*)(d + y * dRow);
                for (int x = 0; x < w; x++) q[x] = avg[x / cell];
            }
        }
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

/// <summary>
/// Application d'un filtre avec aperçu en direct sur le calque actif. L'aperçu peut être calculé en arrière-plan
/// (<see cref="UpdatePreviewAsync"/>) ; le calque n'est écrit que par le fil appelant. Seules les bornes de la
/// sélection prise à l'ouverture sont modifiées, et un filtre local n'est calculé que sur un extrait autour d'elles.
/// </summary>
public sealed class FilterSession
{
    readonly PaintEngine _e;
    readonly Layer _layer;
    readonly SKBitmap _original;
    /// <summary>Zone touchée : bornes de la sélection à l'ouverture (toute la toile sans sélection).</summary>
    readonly SKRectI _rect;
    /// <summary>Couverture de la sélection sur _rect (Alpha8 anticrénelé), null sans sélection.</summary>
    readonly SKBitmap? _mask;
    // Dernier résultat : _out couvre _outRect (toile ou extrait), la zone _rect y est déjà composée.
    SKBitmap? _out;
    SKRectI _outRect;
    float[]? _outValues; // réglages du résultat de _out (null : absent, en cours ou raté)
    bool _outAlphaLock;
    // Extrait de l'original autour de _rect, gardé d'un calcul à l'autre.
    SKBitmap? _crop;
    SKRectI _cropRect;
    Task? _compute; // calcul d'arrière-plan : n'écrit jamais dans le calque
    CancellationTokenSource? _computeCancel;
    float[]? _computeValues;
    int _generation; // dernière demande ; les résultats plus anciens ne sont pas affichés
    bool _suspended, _closed;

    public FilterDef Filter { get; }
    public float[] Values { get; }
    /// <summary>Vrai si <see cref="Commit"/> a modifié des pixels (et donc créé une entrée d'historique).</summary>
    public bool HadEffect { get; private set; }
    /// <summary>Un aperçu est en cours de calcul en arrière-plan.</summary>
    public bool IsComputing => _compute is { IsCompleted: false };

    public FilterSession(PaintEngine e, FilterDef filter) : this(e, filter, previewNow: true) { }

    /// <param name="previewNow">false : aucun aperçu calculé ici, il est demandé ensuite avec <see cref="UpdatePreviewAsync"/>.</param>
    public FilterSession(PaintEngine e, FilterDef filter, bool previewNow)
    {
        _e = e;
        Filter = filter;
        _layer = e.Document.ActiveLayer;
        _original = BitmapUtil.Copy(_layer.Bitmap);
        _rect = e.Selection.Bounds;
        if (e.Selection.Path is { } path && !_rect.IsEmpty)
        {
            // Copie locale : la sélection peut changer pendant un calcul d'arrière-plan.
            _mask = new SKBitmap(new SKImageInfo(_rect.Width, _rect.Height, SKColorType.Alpha8, SKAlphaType.Premul));
            _mask.Erase(SKColors.Transparent);
            using var c = new SKCanvas(_mask);
            c.Translate(-_rect.Left, -_rect.Top);
            using var p = new SKPaint { IsAntialias = true, Color = SKColors.White };
            c.DrawPath(path, p);
        }
        Values = Array.ConvertAll(filter.Params, p => p.Default);
        if (previewNow) Preview();
    }

    /// <summary>Recalcule l'aperçu sur le fil appelant (bloquant) et l'affiche.</summary>
    public void Preview()
    {
        if (_closed) return;
        var values = Snapshot();
        bool alphaLock = _layer.AlphaLock;
        Settle(values);
        _generation++;
        if (!HasResult(values, alphaLock)) Run(values, alphaLock, CancellationToken.None);
        if (!_suspended) Show();
    }

    /// <summary>
    /// Recalcule l'aperçu en arrière-plan avec les réglages actuels, puis l'affiche sur le fil appelant (à appeler
    /// depuis le fil d'interface). Une demande plus récente, l'annulation du jeton, <see cref="Commit"/> ou
    /// <see cref="Cancel"/> la rendent caduque : elle se termine alors sans rien afficher et renvoie false, et
    /// son calcul s'arrête tôt (flou de mouvement, demi-teinte). Les calculs ne se chevauchent jamais ; un résultat
    /// complet est resservi si les mêmes réglages reviennent. Les erreurs du filtre (manque de mémoire...) sont propagées.
    /// </summary>
    public async Task<bool> UpdatePreviewAsync(CancellationToken cancel = default)
    {
        if (_closed) return false;
        int gen = ++_generation;
        var values = Snapshot();
        bool alphaLock = _layer.AlphaLock;
        bool Stale() => _closed || gen != _generation || cancel.IsCancellationRequested;
        // Le calcul en cours ne sert plus s'il porte sur d'autres réglages.
        if (IsComputing && !Same(_computeValues, values)) AbortCompute();
        while (_compute is { IsCompleted: false } running)
        {
            try { await running; }
            catch { /* erreur déjà remontée à la demande qui a lancé ce calcul */ }
            if (Stale()) return false;
        }
        if (Stale()) return false;
        if (!HasResult(values, alphaLock))
        {
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var task = Task.Run(() => Run(values, alphaLock, abort.Token));
            _compute = task;
            _computeCancel = abort;
            _computeValues = values;
            try { await task; }
            finally { if (_computeCancel == abort) _computeCancel = null; }
            if (Stale() || !HasResult(values, alphaLock)) return false;
        }
        if (!_suspended) Show();
        return true;
    }

    /// <summary>
    /// Remet les pixels d'origine dans le calque sans terminer la session (mise en veille, enregistrement).
    /// Aucun aperçu, même d'un calcul en cours, n'est plus écrit dans le calque avant <see cref="ShowPreview"/>.
    /// </summary>
    public void ShowOriginal()
    {
        if (_closed) return;
        _suspended = true;
        WriteLayer(_original, _rect.Left, _rect.Top);
    }

    /// <summary>
    /// Réaffiche le dernier aperçu calculé, après avoir attendu le calcul en cours s'il y en a un.
    /// Le filtre n'est recalculé (sur le fil appelant) que si aucun résultat valide n'existe.
    /// </summary>
    public void ShowPreview()
    {
        if (_closed) return;
        _suspended = false;
        WaitCompute();
        if (_outValues == null) Run(Snapshot(), _layer.AlphaLock, CancellationToken.None);
        Show();
    }

    public void Commit()
    {
        if (_closed) return;
        var values = Snapshot();
        bool alphaLock = _layer.AlphaLock;
        Settle(values);
        // Le résultat validé est celui des réglages actuels, même si le dernier aperçu est en retard.
        if (!HasResult(values, alphaLock)) Run(values, alphaLock, CancellationToken.None);
        Show();
        _closed = true;
        _generation++;
        // Réglages sans effet : pas d'entrée d'historique.
        HadEffect = !_rect.IsEmpty && LayerIntact && !SameRegion(_original, _layer.Bitmap, _rect);
        if (HadEffect)
        {
            var before = BitmapUtil.CopyRegion(_original, _rect);
            var after = BitmapUtil.CopyRegion(_layer.Bitmap, _rect);
            _layer.Touch();
            _e.History.Push(new PixelAction(Filter.Name, _layer, _rect, before, after));
            _e.NotifyLayerPixels(_layer);
        }
        Release();
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        _generation++;
        AbortCompute();
        WriteLayer(_original, _rect.Left, _rect.Top);
        Release();
    }

    float[] Snapshot() => (float[])Values.Clone();

    static bool Same(float[]? a, float[] b) => a != null && a.AsSpan().SequenceEqual(b);

    bool HasResult(float[] values, bool alphaLock) => _outAlphaLock == alphaLock && Same(_outValues, values);

    void AbortCompute()
    {
        try { _computeCancel?.Cancel(); }
        catch (ObjectDisposedException) { /* calcul déjà terminé */ }
    }

    /// <summary>Attend le calcul d'arrière-plan (il n'a pas besoin du fil d'interface : pas d'interblocage).</summary>
    void WaitCompute()
    {
        var running = _compute;
        if (running == null) return;
        try { running.Wait(); }
        catch (AggregateException) { /* remontée par UpdatePreviewAsync */ }
    }

    /// <summary>Attend le calcul en cours, après l'avoir abandonné s'il porte sur d'autres réglages.</summary>
    void Settle(float[] values)
    {
        if (IsComputing && !Same(_computeValues, values)) AbortCompute();
        WaitCompute();
    }

    /// <summary>Calcule le filtre puis compose la zone _rect dans _out. Jamais deux appels à la fois.</summary>
    void Run(float[] values, bool alphaLock, CancellationToken cancel)
    {
        _outValues = null;
        if (!_rect.IsEmpty)
        {
            int w = _original.Width, h = _original.Height;
            var full = new SKRectI(0, 0, w, h);
            var area = full;
            if (_rect != full && Filter.Margin is { } margin)
            {
                int m = margin(values);
                area = BitmapUtil.Clamp(SKRectI.Inflate(_rect, m, m), w, h);
            }
            var src = _original;
            if (area != full)
            {
                if (_crop == null || _cropRect != area)
                {
                    _crop?.Dispose();
                    _crop = BitmapUtil.CopyRegion(_original, area);
                    _cropRect = area;
                }
                src = _crop;
            }
            if (_out == null || _out.Width != area.Width || _out.Height != area.Height)
            {
                _out?.Dispose();
                _out = new SKBitmap(BitmapUtil.Info(area.Width, area.Height));
            }
            _outRect = area;
            Filters.Apply(Filter, src, _out, values, cancel);
            if (cancel.IsCancellationRequested) return; // résultat incomplet : jeté
            Compose(alphaLock);
        }
        _outAlphaLock = alphaLock;
        _outValues = values;
    }

    /// <summary>Verrou alpha, puis fondu avec l'original selon la couverture de la sélection (pixels prémultipliés).</summary>
    unsafe void Compose(bool alphaLock)
    {
        if (!alphaLock && _mask == null) return;
        byte* o0 = (byte*)_original.GetPixels();
        byte* f0 = (byte*)_out!.GetPixels();
        byte* m0 = _mask == null ? null : (byte*)_mask.GetPixels();
        int oRow = _original.RowBytes, fRow = _out.RowBytes, mRow = _mask?.RowBytes ?? 0;
        int fx = _rect.Left - _outRect.Left, fy = _rect.Top - _outRect.Top;
        for (int y = 0; y < _rect.Height; y++)
        {
            byte* o = o0 + (_rect.Top + y) * oRow + _rect.Left * 4;
            byte* f = f0 + (fy + y) * fRow + fx * 4;
            byte* m = m0 == null ? null : m0 + y * mRow;
            for (int x = 0; x < _rect.Width; x++, o += 4, f += 4)
            {
                int k = m == null ? 255 : m[x];
                if (k == 0) { *(uint*)f = *(uint*)o; continue; }
                if (alphaLock)
                {
                    // Alpha d'origine gardé, couleur filtrée ramenée à cet alpha.
                    byte oa = o[3], fa = f[3];
                    if (oa == 0) *(uint*)f = 0;
                    else if (fa == 0) *(uint*)f = *(uint*)o;
                    else if (fa != oa)
                    {
                        float s = oa / (float)fa;
                        f[0] = (byte)MathF.Min(oa, f[0] * s + 0.5f);
                        f[1] = (byte)MathF.Min(oa, f[1] * s + 0.5f);
                        f[2] = (byte)MathF.Min(oa, f[2] * s + 0.5f);
                        f[3] = oa;
                    }
                }
                if (k < 255)
                {
                    // Mélange linéaire : pas de liseré au bord anticrénelé de la sélection.
                    float t = k / 255f;
                    for (int c = 0; c < 4; c++) f[c] = (byte)(o[c] + (f[c] - o[c]) * t + 0.5f);
                }
            }
        }
    }

    /// <summary>Écrit le dernier résultat dans le calque (fil appelant, aucun calcul en cours).</summary>
    void Show()
    {
        if (_out == null) return;
        WriteLayer(_out, _rect.Left - _outRect.Left, _rect.Top - _outRect.Top);
    }

    /// <summary>Le calque n'a été ni libéré ni redimensionné (rotation de la toile...) depuis l'ouverture.</summary>
    bool LayerIntact => !_layer.IsDisposed && _layer.Bitmap.Width == _original.Width && _layer.Bitmap.Height == _original.Height;

    /// <summary>Copie dans la zone _rect du calque les pixels de src lus à partir de (sx, sy).</summary>
    void WriteLayer(SKBitmap src, int sx, int sy)
    {
        if (_rect.IsEmpty || !LayerIntact) return;
        CopyRect(src, sx, sy, _layer.Bitmap, _rect);
        _e.Invalidate(_rect);
    }

    /// <summary>Libère les tampons, après le calcul en cours s'il y en a un (il les utilise encore).</summary>
    void Release()
    {
        var running = _compute;
        if (running is { IsCompleted: false }) running.ContinueWith(_ => DisposeBuffers(), TaskScheduler.Default);
        else DisposeBuffers();
    }

    void DisposeBuffers()
    {
        _original.Dispose();
        _out?.Dispose();
        _crop?.Dispose();
        _mask?.Dispose();
    }

    /// <summary>Copie dans la zone r de dst les pixels de src lus à partir de (sx, sy).</summary>
    static unsafe void CopyRect(SKBitmap src, int sx, int sy, SKBitmap dst, SKRectI r)
    {
        if (r.IsEmpty) return;
        byte* s = (byte*)src.GetPixels();
        byte* d = (byte*)dst.GetPixels();
        int sRow = src.RowBytes, dRow = dst.RowBytes, len = r.Width * 4;
        for (int y = 0; y < r.Height; y++)
            Buffer.MemoryCopy(s + (sy + y) * sRow + sx * 4, d + (r.Top + y) * dRow + r.Left * 4, len, len);
    }

    static unsafe bool SameRegion(SKBitmap a, SKBitmap b, SKRectI r)
    {
        byte* pa = (byte*)a.GetPixels();
        byte* pb = (byte*)b.GetPixels();
        int len = r.Width * 4;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var ra = new ReadOnlySpan<byte>(pa + y * a.RowBytes + r.Left * 4, len);
            var rb = new ReadOnlySpan<byte>(pb + y * b.RowBytes + r.Left * 4, len);
            if (!ra.SequenceEqual(rb)) return false;
        }
        return true;
    }
}
