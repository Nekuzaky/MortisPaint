using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Encodeur GIF animé écrit à la main : palette de 256 couleurs par image (coupe médiane
/// sur un histogramme RVB 5 bits) et compression LZW. Utilisé pour le timelapse et l'animation.
/// Les images sont écrites au fil de l'eau : seule l'image en cours est en mémoire.
/// </summary>
public sealed class GifEncoder : IDisposable
{
    readonly Stream _out;
    readonly int _width, _height;
    bool _headerWritten;

    // Histogramme et sommes des vraies couleurs par case (réutilisés d'une image à l'autre).
    readonly long[] _hist = new long[32768];
    readonly long[] _sumR = new long[32768], _sumG = new long[32768], _sumB = new long[32768];

    public GifEncoder(Stream output, int width, int height)
    {
        _out = output;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Délais (centièmes de seconde) de <paramref name="count"/> images à <paramref name="fps"/> images/s.
    /// L'erreur d'arrondi est reportée d'une image à l'autre : à 24 i/s on obtient 4, 4, 5, 4…
    /// (24 images = 1 s pile) au lieu de 4 partout (25 i/s).
    /// </summary>
    public static int[] FrameDelays(int count, double fps)
    {
        var delays = new int[Math.Max(0, count)];
        double step = 100.0 / (fps > 0 ? fps : 1);
        long previous = 0;
        for (int i = 0; i < delays.Length; i++)
        {
            long t = (long)Math.Round((i + 1) * step, MidpointRounding.AwayFromZero);
            delays[i] = (int)Math.Clamp(t - previous, 2, 65535);
            previous = t;
        }
        return delays;
    }

    /// <summary>
    /// Ajoute une image affichée <paramref name="delayCs"/> centièmes de seconde. Une image d'une autre
    /// taille est ajustée dans le cadre en gardant ses proportions (bandes blanches au besoin).
    /// </summary>
    public void AddFrame(SKBitmap frame, int delayCs)
    {
        if (!_headerWritten) WriteHeader();
        var rgba = Pixels(frame);
        var (palette, lookup) = Quantize(rgba);
        var indices = new byte[_width * _height];
        for (int i = 0; i < indices.Length; i++)
        {
            int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            indices[i] = lookup[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
        }

        // Extension de contrôle graphique : délai, pas de transparence.
        _out.WriteByte(0x21); _out.WriteByte(0xF9); _out.WriteByte(4);
        _out.WriteByte(0x04);
        WriteShort(Math.Clamp(delayCs, 2, 65535));
        _out.WriteByte(0); _out.WriteByte(0);

        // Descripteur d'image avec palette locale de 256 couleurs.
        _out.WriteByte(0x2C);
        WriteShort(0); WriteShort(0);
        WriteShort(_width); WriteShort(_height);
        _out.WriteByte(0x87);
        _out.Write(palette, 0, palette.Length);
        WriteLzw(indices);
    }

    void WriteHeader()
    {
        _headerWritten = true;
        var sig = "GIF89a"u8.ToArray();
        _out.Write(sig, 0, sig.Length);
        WriteShort(_width);
        WriteShort(_height);
        _out.WriteByte(0x70); // pas de palette globale, résolution 8 bits
        _out.WriteByte(0);
        _out.WriteByte(0);
        // Boucle infinie (extension NETSCAPE2.0).
        _out.WriteByte(0x21); _out.WriteByte(0xFF); _out.WriteByte(11);
        var app = "NETSCAPE2.0"u8.ToArray();
        _out.Write(app, 0, app.Length);
        _out.WriteByte(3); _out.WriteByte(1); WriteShort(0); _out.WriteByte(0);
    }

    byte[] Pixels(SKBitmap frame)
    {
        using var sized = frame.Width == _width && frame.Height == _height ? null : Fitted(frame);
        var src = sized ?? frame;
        var rgba = new byte[_width * _height * 4];
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = rgba)
            {
                using var pix = src.PeekPixels();
                pix.ReadPixels(info, (IntPtr)p, _width * 4, 0, 0);
            }
        }
        // Fond blanc sous les zones transparentes.
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            for (int k = 0; k < 3; k++) rgba[i + k] = (byte)((rgba[i + k] * a + 255 * (255 - a)) / 255);
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    /// <summary>
    /// Image ajustée dans le cadre du GIF sans déformation, centrée sur fond blanc (ex. timelapse
    /// d'une toile redimensionnée en cours de route).
    /// </summary>
    SKBitmap Fitted(SKBitmap frame)
    {
        var dst = new SKBitmap(BitmapUtil.Info(_width, _height));
        dst.Erase(SKColors.White);
        float s = MathF.Min(_width / (float)frame.Width, _height / (float)frame.Height);
        float w = frame.Width * s, h = frame.Height * s;
        // Mêmes proportions aux arrondis près : on remplit tout le cadre (pas de liseré blanc).
        if (_width - w <= Math.Max(1f, _width * 0.015f) && _height - h <= Math.Max(1f, _height * 0.015f))
        {
            w = _width;
            h = _height;
        }
        using var canvas = new SKCanvas(dst);
        using var pix = frame.PeekPixels();
        using var img = SKImage.FromPixels(pix);
        using var paint = new SKPaint();
        canvas.DrawImage(img, SKRect.Create((_width - w) / 2, (_height - h) / 2, w, h),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return dst;
    }

    sealed class Box
    {
        public List<int> Colors = new();
        public int RMin, RMax, GMin, GMax, BMin, BMax;
        public long Count;

        public void Fit(long[] hist)
        {
            RMin = GMin = BMin = 31; RMax = GMax = BMax = 0; Count = 0;
            foreach (int c in Colors)
            {
                int r = c >> 10, g = (c >> 5) & 31, b = c & 31;
                RMin = Math.Min(RMin, r); RMax = Math.Max(RMax, r);
                GMin = Math.Min(GMin, g); GMax = Math.Max(GMax, g);
                BMin = Math.Min(BMin, b); BMax = Math.Max(BMax, b);
                Count += hist[c];
            }
        }

        public int LongestAxis => (RMax - RMin) >= (GMax - GMin) && (RMax - RMin) >= (BMax - BMin) ? 0 : (GMax - GMin) >= (BMax - BMin) ? 1 : 2;
        public int Span => Math.Max(RMax - RMin, Math.Max(GMax - GMin, BMax - BMin));
    }

    /// <summary>
    /// Coupe médiane sur 32 768 cases ; renvoie la palette (RVB) et une table case → index.
    /// La couleur d'une boîte est la moyenne des vraies couleurs de ses pixels (le blanc reste 255,
    /// et non le centre de sa case, 252).
    /// </summary>
    (byte[] Palette, byte[] Lookup) Quantize(byte[] rgba)
    {
        var hist = _hist;
        Array.Clear(hist);
        Array.Clear(_sumR);
        Array.Clear(_sumG);
        Array.Clear(_sumB);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            int c = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            hist[c]++;
            _sumR[c] += r; _sumG[c] += g; _sumB[c] += b;
        }
        var root = new Box();
        for (int c = 0; c < hist.Length; c++)
            if (hist[c] > 0) root.Colors.Add(c);
        root.Fit(hist);
        var boxes = new List<Box> { root };
        while (boxes.Count < 256)
        {
            Box? pick = null;
            foreach (var b in boxes)
                if (b.Colors.Count > 1 && (pick == null || b.Count * b.Span > pick.Count * pick.Span)) pick = b;
            if (pick == null) break;
            int axis = pick.LongestAxis;
            pick.Colors.Sort((x, y) => Channel(x, axis).CompareTo(Channel(y, axis)));
            long half = pick.Count / 2, acc = 0;
            int split = 1;
            for (int i = 0; i < pick.Colors.Count - 1; i++)
            {
                acc += hist[pick.Colors[i]];
                if (acc >= half) { split = i + 1; break; }
                split = i + 1;
            }
            var other = new Box { Colors = pick.Colors.GetRange(split, pick.Colors.Count - split) };
            pick.Colors.RemoveRange(split, pick.Colors.Count - split);
            pick.Fit(hist);
            other.Fit(hist);
            boxes.Add(other);
        }
        var palette = new byte[256 * 3];
        var lookup = new byte[32768];
        for (int i = 0; i < boxes.Count; i++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (int c in boxes[i].Colors)
            {
                r += _sumR[c]; g += _sumG[c]; b += _sumB[c]; n += hist[c];
                lookup[c] = (byte)i;
            }
            if (n == 0) n = 1;
            palette[i * 3] = (byte)Math.Min(255, (r + n / 2) / n);
            palette[i * 3 + 1] = (byte)Math.Min(255, (g + n / 2) / n);
            palette[i * 3 + 2] = (byte)Math.Min(255, (b + n / 2) / n);
        }
        return (palette, lookup);
    }

    static int Channel(int c, int axis) => axis == 0 ? c >> 10 : axis == 1 ? (c >> 5) & 31 : c & 31;

    void WriteLzw(byte[] indices)
    {
        const int minCode = 8;
        _out.WriteByte(minCode);
        int clear = 1 << minCode, end = clear + 1;
        // Sous-blocs de 255 octets au plus, précédés de leur longueur.
        var block = new byte[256];
        int blockLen = 0;
        int bitBuf = 0, bitCount = 0;

        void Emit(int code, int size)
        {
            bitBuf |= code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                block[1 + blockLen++] = (byte)(bitBuf & 0xFF);
                bitBuf >>= 8;
                bitCount -= 8;
                if (blockLen == 255) Flush();
            }
        }

        void Flush()
        {
            if (blockLen == 0) return;
            block[0] = (byte)blockLen;
            _out.Write(block, 0, blockLen + 1);
            blockLen = 0;
        }

        var dict = new Dictionary<int, int>();
        int codeSize = minCode + 1, next = end + 1;
        Emit(clear, codeSize);
        int prefix = indices.Length > 0 ? indices[0] : 0;
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out int code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix, codeSize);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear, codeSize);
                dict.Clear();
                codeSize = minCode + 1;
                next = end + 1;
            }
            prefix = k;
        }
        Emit(prefix, codeSize);
        // Même règle que pour les autres codes : le décodeur élargit ses codes avant le code de fin.
        if (next >= (1 << codeSize) && codeSize < 12) codeSize++;
        Emit(end, codeSize);
        if (bitCount > 0) block[1 + blockLen++] = (byte)(bitBuf & 0xFF);
        Flush();
        _out.WriteByte(0);
    }

    void WriteShort(int v)
    {
        _out.WriteByte((byte)(v & 0xFF));
        _out.WriteByte((byte)((v >> 8) & 0xFF));
    }

    public void Dispose()
    {
        if (!_headerWritten) WriteHeader();
        _out.WriteByte(0x3B);
    }
}
