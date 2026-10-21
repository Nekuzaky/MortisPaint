using System;
using System.IO;
using SkiaSharp;

namespace MortisPaint.Engine;

public static class BitmapUtil
{
    public static SKImageInfo Info(int width, int height) =>
        new(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);

    public static bool IsBgra => SKImageInfo.PlatformColorType == SKColorType.Bgra8888;

    public static SKBitmap Copy(SKBitmap src)
    {
        var dst = new SKBitmap(src.Info);
        unsafe
        {
            Buffer.MemoryCopy((void*)src.GetPixels(), (void*)dst.GetPixels(), dst.ByteCount, src.ByteCount);
        }
        return dst;
    }

    /// <summary>Copie une zone rectangulaire (déjà bornée au bitmap) dans un nouveau bitmap.</summary>
    public static SKBitmap CopyRegion(SKBitmap src, SKRectI r)
    {
        var dst = new SKBitmap(Info(Math.Max(1, r.Width), Math.Max(1, r.Height)));
        if (r.Width <= 0 || r.Height <= 0) { dst.Erase(SKColors.Transparent); return dst; }
        unsafe
        {
            byte* s = (byte*)src.GetPixels();
            byte* d = (byte*)dst.GetPixels();
            int sRow = src.RowBytes, dRow = dst.RowBytes, len = r.Width * 4;
            for (int y = 0; y < r.Height; y++)
                Buffer.MemoryCopy(s + (r.Top + y) * sRow + r.Left * 4, d + y * dRow, len, len);
        }
        return dst;
    }

    /// <summary>Remplace les pixels de dst par ceux de patch à la position donnée (copie brute).</summary>
    public static void Paste(SKBitmap dst, SKBitmap patch, int x, int y)
    {
        var r = Clamp(new SKRectI(x, y, x + patch.Width, y + patch.Height), dst.Width, dst.Height);
        if (r.IsEmpty) return;
        unsafe
        {
            byte* s = (byte*)patch.GetPixels();
            byte* d = (byte*)dst.GetPixels();
            int sRow = patch.RowBytes, dRow = dst.RowBytes, len = r.Width * 4;
            for (int yy = r.Top; yy < r.Bottom; yy++)
                Buffer.MemoryCopy(s + (yy - y) * sRow + (r.Left - x) * 4, d + yy * dRow + r.Left * 4, len, len);
        }
    }

    public static void CopyAll(SKBitmap src, SKBitmap dst)
    {
        unsafe
        {
            Buffer.MemoryCopy((void*)src.GetPixels(), (void*)dst.GetPixels(), dst.ByteCount, Math.Min(src.ByteCount, dst.ByteCount));
        }
    }

    public static SKRectI Clamp(SKRectI r, int width, int height) =>
        SKRectI.Intersect(r, new SKRectI(0, 0, width, height));

    public static SKRectI Union(SKRectI a, SKRectI b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return SKRectI.Union(a, b);
    }

    public static SKRectI RoundOut(SKRect r, int pad = 1) => new(
        (int)MathF.Floor(r.Left) - pad, (int)MathF.Floor(r.Top) - pad,
        (int)MathF.Ceiling(r.Right) + pad, (int)MathF.Ceiling(r.Bottom) + pad);

    /// <summary>Rectangle englobant les pixels non transparents (vide si le calque est vide).</summary>
    public static SKRectI AlphaBounds(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        unsafe
        {
            byte* p = (byte*)bmp.GetPixels();
            int row = bmp.RowBytes;
            for (int y = 0; y < h; y++)
            {
                uint* line = (uint*)(p + y * row);
                int first = -1, last = -1;
                for (int x = 0; x < w; x++)
                {
                    if (line[x] != 0) { first = x; break; }
                }
                if (first < 0) continue;
                for (int x = w - 1; x >= first; x--)
                {
                    if (line[x] != 0) { last = x; break; }
                }
                if (y < minY) minY = y;
                maxY = y;
                if (first < minX) minX = first;
                if (last > maxX) maxX = last;
            }
        }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    public static SKBitmap Scaled(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(Info(Math.Max(1, width), Math.Max(1, height)));
        dst.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(dst);
        using var img = SKImage.FromBitmap(src);
        using var paint = new SKPaint();
        canvas.DrawImage(img, new SKRect(0, 0, dst.Width, dst.Height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return dst;
    }

    /// <summary>Convertit un bitmap décodé quelconque au format interne (couleur plateforme, prémultiplié).</summary>
    public static SKBitmap Normalize(SKBitmap src)
    {
        if (src.ColorType == SKImageInfo.PlatformColorType && src.AlphaType == SKAlphaType.Premul)
            return src;
        var dst = new SKBitmap(Info(src.Width, src.Height));
        dst.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(dst))
            canvas.DrawBitmap(src, 0, 0);
        src.Dispose();
        return dst;
    }

    public static SKBitmap? Decode(System.IO.Stream stream)
    {
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;
        var info = Info(codec.Info.Width, codec.Info.Height);
        var bmp = SKBitmap.Decode(codec, info);
        return bmp == null ? null : Normalize(bmp);
    }

    /// <summary>
    /// Décode une image dont la taille est imposée par le fichier qui la contient (calque d'un projet) :
    /// une image plus grande que <paramref name="maxWidth"/>×<paramref name="maxHeight"/> est refusée avant toute allocation.
    /// </summary>
    public static SKBitmap? Decode(System.IO.Stream stream, int maxWidth, int maxHeight)
    {
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;
        if (codec.Info.Width > maxWidth || codec.Info.Height > maxHeight)
            throw new InvalidDataException($"Image de {codec.Info.Width}×{codec.Info.Height} au lieu de {maxWidth}×{maxHeight} au plus.");
        var bmp = SKBitmap.Decode(codec, Info(codec.Info.Width, codec.Info.Height));
        return bmp == null ? null : Normalize(bmp);
    }

    /// <summary>
    /// Décode une photo en appliquant son orientation EXIF (appareil tenu de côté, selfie retourné...),
    /// pour qu'elle s'affiche comme dans la galerie du téléphone.
    /// </summary>
    public static SKBitmap? DecodeOriented(System.IO.Stream stream)
    {
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;
        int w = codec.Info.Width, h = codec.Info.Height;
        var raw = SKBitmap.Decode(codec, Info(w, h));
        if (raw == null) return null;
        var bmp = Normalize(raw);
        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft) return bmp;
        // Matrice qui envoie l'image stockée sur l'image affichée (x' = ScaleX·x + SkewX·y + TransX).
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // miroir horizontal
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // demi-tour
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // miroir vertical
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transposition
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // quart de tour horaire
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transposition inverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // quart de tour antihoraire
            _ => SKMatrix.Identity,
        };
        if (m.IsIdentity) return bmp;
        var dst = new SKBitmap(Info(swap ? h : w, swap ? w : h));
        dst.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.SetMatrix(m);
            canvas.DrawBitmap(bmp, 0, 0, paint);
        }
        bmp.Dispose();
        return dst;
    }

    /// <summary>
    /// Refuse un fichier dont les calques pleine toile demanderaient plus de la moitié de la mémoire de l'appareil :
    /// quelques octets d'en-tête ne doivent pas pouvoir réclamer des gigaoctets.
    /// </summary>
    public static void EnsureLayersFit(long layers, int width, int height)
    {
        long need = layers * width * (long)height * 4;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 4L << 30;
        if (need > total / 2)
            throw new InvalidDataException($"Fichier trop lourd : {layers} calques de {width}×{height} demanderaient {need >> 20} Mo.");
    }

    static SKShader? _checker;

    /// <summary>Damier gris clair utilisé pour représenter la transparence.</summary>
    public static SKShader Checker
    {
        get
        {
            if (_checker != null) return _checker;
            const int cell = 12;
            var bmp = new SKBitmap(Info(cell * 2, cell * 2)); // conservé : le shader le référence
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(new SKColor(0xFF, 0xFF, 0xFF));
                using var p = new SKPaint { Color = new SKColor(0xD9, 0xD9, 0xDE) };
                c.DrawRect(0, 0, cell, cell, p);
                c.DrawRect(cell, cell, cell, cell, p);
            }
            _checker = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            return _checker;
        }
    }
}
