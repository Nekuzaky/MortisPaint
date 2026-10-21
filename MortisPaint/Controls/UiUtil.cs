using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MortisPaint.Engine;
using SkiaSharp;

namespace MortisPaint.Controls;

public static class ColorUtil
{
    public static Color ToAvalonia(this SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    public static SKColor ToSkia(this Color c) => new(c.R, c.G, c.B, c.A);

    public static string ToHex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    public static bool TryParseHex(string? text, out SKColor color)
    {
        color = SKColors.Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        if (t.Length != 6) return false;
        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static void ToHsv(SKColor c, out float h, out float s, out float v)
    {
        float r = c.Red / 255f, g = c.Green / 255f, b = c.Blue / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        h = 0;
        if (d > 1e-6f)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        s = max <= 0 ? 0 : d / max;
        v = max;
    }

    public static SKColor FromHsv(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360;
        float c = v * s;
        float x = c * (1 - MathF.Abs((h / 60f) % 2 - 1));
        float m = v - c;
        float r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return new SKColor((byte)MathF.Round((r + m) * 255), (byte)MathF.Round((g + m) * 255), (byte)MathF.Round((b + m) * 255));
    }

    /// <summary>Couleur de texte lisible sur un fond donné.</summary>
    public static IBrush ContrastBrush(SKColor c) =>
        (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) > 150 ? Brushes.Black : Brushes.White;
}

public static class SkiaBitmaps
{
    public static PixelFormat PlatformFormat =>
        BitmapUtil.IsBgra ? PixelFormats.Bgra8888 : PixelFormats.Rgba8888;

    public static WriteableBitmap Create(int width, int height) =>
        new(new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96), PlatformFormat, AlphaFormat.Premul);

    /// <summary>Dessine dans un WriteableBitmap via un canvas Skia.</summary>
    public static void Draw(WriteableBitmap wb, Action<SKCanvas> draw)
    {
        using var fb = wb.Lock();
        var info = new SKImageInfo(fb.Size.Width, fb.Size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var bmp = new SKBitmap();
        bmp.InstallPixels(info, fb.Address, fb.RowBytes);
        using var c = new SKCanvas(bmp);
        draw(c);
    }

    /// <summary>Copie redimensionnée d'un bitmap Skia (avec damier si demandé).</summary>
    public static WriteableBitmap FromSkia(SKBitmap src, int maxSize, bool checker)
    {
        float s = MathF.Min(1f, maxSize / (float)Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)MathF.Round(src.Width * s)), h = Math.Max(1, (int)MathF.Round(src.Height * s));
        var wb = Create(w, h);
        Draw(wb, c =>
        {
            c.Clear(SKColors.Transparent);
            if (checker)
            {
                using var p = new SKPaint { Shader = BitmapUtil.Checker };
                c.Scale(0.5f);
                c.DrawRect(0, 0, w * 2, h * 2, p);
                c.ResetMatrix();
            }
            using var pix = src.PeekPixels();
            using var img = SKImage.FromPixels(pix);
            using var paint = new SKPaint();
            c.DrawImage(img, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        });
        return wb;
    }

    public static Bitmap? FromPng(byte[]? png)
    {
        if (png == null) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
