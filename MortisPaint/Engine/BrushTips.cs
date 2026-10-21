using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Pointes de pinceau créées à partir d'images : le sombre devient opaque, le clair transparent
/// (ou l'alpha de l'image s'il y en a). Elles sont stockées en PNG dans <see cref="Directory"/>.
/// </summary>
public static class BrushTips
{
    public const int MaxSide = 256;
    static readonly Dictionary<string, SKImage?> Cache = new();

    public static string? Directory { get; set; }

    public static SKImage? Get(string? id)
    {
        if (id == null || Directory == null) return null;
        if (Cache.TryGetValue(id, out var img)) return img;
        var path = Path.Combine(Directory, id + ".png");
        img = File.Exists(path) ? SKImage.FromEncodedData(path) : null;
        Cache[id] = img;
        return img;
    }

    /// <summary>Crée une pointe à partir d'une image et renvoie son identifiant.</summary>
    public static string Create(SKBitmap source)
    {
        if (Directory == null) throw new InvalidOperationException("Dossier des pointes non configuré.");
        System.IO.Directory.CreateDirectory(Directory);
        int w = source.Width, h = source.Height;
        var rgba = new byte[w * h * 4];
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = rgba)
            {
                using var pix = source.PeekPixels();
                pix.ReadPixels(info, (IntPtr)p, w * 4, 0, 0);
            }
        }
        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 250) { hasAlpha = true; break; }

        // Obscurité maximale de la partie visible : la nuance la plus sombre devient opaque,
        // et une forme claire (PNG blanc sur transparent) garde son alpha tel quel.
        int maxDark = 0;
        if (hasAlpha)
            for (int i = 0; i < w * h; i++)
            {
                if (rgba[i * 4 + 3] < 64) continue; // RVB des pixels transparents sans signification
                int dk = 255 - (rgba[i * 4] * 299 + rgba[i * 4 + 1] * 587 + rgba[i * 4 + 2] * 114) / 1000;
                if (dk > maxDark) maxDark = dk;
            }

        // Masque : alpha de l'image si elle en a (nuancé par l'obscurité relative), sinon l'obscurité.
        using var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        unsafe
        {
            byte* d = (byte*)mask.GetPixels();
            for (int i = 0; i < w * h; i++)
            {
                byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
                int dark = 255 - (r * 299 + g * 587 + b * 114) / 1000;
                int shade = maxDark < 16 ? 255 : Math.Clamp(dark * 255 / maxDark, 64, 255);
                byte alpha = hasAlpha ? (byte)(a * shade / 255) : (byte)dark;
                d[i * 4] = 255; d[i * 4 + 1] = 255; d[i * 4 + 2] = 255; d[i * 4 + 3] = alpha;
            }
        }
        using var premul = BitmapUtil.Normalize(mask.Copy());
        var bounds = BitmapUtil.AlphaBounds(premul);
        if (bounds.IsEmpty) throw new InvalidDataException("L'image ne contient aucune forme utilisable (elle est vide ou blanche).");
        using var cropped = BitmapUtil.CopyRegion(premul, bounds);
        float s = MathF.Min(1f, MaxSide / (float)Math.Max(cropped.Width, cropped.Height));
        using var tip = BitmapUtil.Scaled(cropped, Math.Max(1, (int)(cropped.Width * s)), Math.Max(1, (int)(cropped.Height * s)));
        var id = Guid.NewGuid().ToString("N");
        using (var fs = File.Create(Path.Combine(Directory, id + ".png")))
            tip.Encode(fs, SKEncodedImageFormat.Png, 100);
        return id;
    }

    public static void Delete(string id)
    {
        if (Directory == null) return;
        Cache.Remove(id);
        var path = Path.Combine(Directory, id + ".png");
        if (File.Exists(path)) File.Delete(path);
    }
}
