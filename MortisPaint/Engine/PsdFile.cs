using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Lecture / écriture du format Photoshop (PSD, 8 bits, RVB) avec calques.
/// C'est le format d'échange d'ibisPaint (« PSD (calques conservés) ») : on peut donc
/// continuer dans MortisPaint un dessin commencé dans ibisPaint, et inversement.
/// Pris en charge : noms (Unicode), visibilité, opacité, 17 modes de fusion, écrêtage,
/// verrou alpha, masques de calque (appliqués), dossiers (aplatis), compressions brute / RLE / ZIP.
/// </summary>
public static class PsdFile
{
    public const string Extension = ".psd";
    public const int MaxSide = 8192;

    static readonly (LayerBlendMode Mode, string Key)[] BlendKeys =
    {
        (LayerBlendMode.Normal, "norm"), (LayerBlendMode.Multiply, "mul "), (LayerBlendMode.Screen, "scrn"),
        (LayerBlendMode.Overlay, "over"), (LayerBlendMode.Darken, "dark"), (LayerBlendMode.Lighten, "lite"),
        (LayerBlendMode.ColorDodge, "div "), (LayerBlendMode.ColorBurn, "idiv"), (LayerBlendMode.HardLight, "hLit"),
        (LayerBlendMode.SoftLight, "sLit"), (LayerBlendMode.Difference, "diff"), (LayerBlendMode.Exclusion, "smud"),
        (LayerBlendMode.Hue, "hue "), (LayerBlendMode.Saturation, "sat "), (LayerBlendMode.Color, "colr"),
        (LayerBlendMode.Luminosity, "lum "), (LayerBlendMode.Add, "lddg"),
    };

    static string KeyOf(LayerBlendMode m)
    {
        foreach (var (mode, key) in BlendKeys)
            if (mode == m) return key;
        return "norm";
    }

    static LayerBlendMode ModeOf(string key)
    {
        foreach (var (mode, k) in BlendKeys)
            if (k == key) return mode;
        // Modes sans équivalent exact : approximation la plus proche.
        return key switch
        {
            "dkCl" or "lbrn" => LayerBlendMode.Darken,
            "lgCl" => LayerBlendMode.Lighten,
            "vLit" or "lLit" or "pLit" or "hMix" => LayerBlendMode.HardLight,
            "fsub" => LayerBlendMode.Difference,
            "fdiv" => LayerBlendMode.ColorDodge,
            _ => LayerBlendMode.Normal,
        };
    }

    // ================================================================ écriture

    /// <summary>Écrit le document en PSD à calques. Le papier visible devient un calque « Papier » en bas.</summary>
    public static void Write(Document doc, Stream output)
    {
        int w = doc.Width, h = doc.Height;
        var layers = new List<(string Name, SKBitmap Bitmap, bool Visible, float Opacity, LayerBlendMode Blend, bool Clip, bool Lock, bool Owned)>();
        if (doc.PaperVisible)
        {
            var paper = new SKBitmap(BitmapUtil.Info(w, h));
            paper.Erase(doc.PaperColor);
            layers.Add(("Papier", paper, true, 1f, LayerBlendMode.Normal, false, false, true));
        }
        foreach (var l in doc.Layers)
            layers.Add((l.Name, l.Bitmap, l.Visible, l.Opacity, l.BlendMode, l.Clipping, l.AlphaLock, false));

        try
        {
            var o = new BeWriter(output);
            bool compositeAlpha = !doc.PaperVisible;
            int compositeChannels = compositeAlpha ? 4 : 3;

            // En-tête
            o.Ascii("8BPS");
            o.U16(1);
            o.Zeros(6);
            o.U16((ushort)compositeChannels);
            o.U32((uint)h);
            o.U32((uint)w);
            o.U16(8);
            o.U16(3); // RVB
            o.U32(0); // données de mode couleur
            o.U32(0); // ressources image

            // Calques
            var layerInfo = new MemoryStream();
            var li = new BeWriter(layerInfo);
            li.I16((short)(compositeAlpha ? -layers.Count : layers.Count));
            var channelData = new List<byte[][]>();
            foreach (var l in layers)
            {
                var r = BitmapUtil.AlphaBounds(l.Bitmap);
                byte[][] channels = r.IsEmpty ? new[] { Raw0(), Raw0(), Raw0(), Raw0() } : EncodeLayerChannels(l.Bitmap, r);
                channelData.Add(channels);

                li.I32(r.Top); li.I32(r.Left); li.I32(r.Bottom); li.I32(r.Right);
                li.U16(4);
                short[] ids = { -1, 0, 1, 2 };
                for (int c = 0; c < 4; c++)
                {
                    li.I16(ids[c]);
                    li.U32((uint)channels[c].Length);
                }
                li.Ascii("8BIM");
                li.Ascii(KeyOf(l.Blend));
                li.U8((byte)Math.Clamp(MathF.Round(l.Opacity * 255f), 0, 255));
                li.U8((byte)(l.Clip ? 1 : 0));
                li.U8((byte)((l.Lock ? 0x01 : 0) | (l.Visible ? 0 : 0x02)));
                li.U8(0);

                var extra = new MemoryStream();
                var ex = new BeWriter(extra);
                ex.U32(0); // masque
                ex.U32(0); // plages de fusion
                var ascii = Encoding.ASCII.GetBytes(ToAscii(l.Name));
                int nameLen = Math.Min(ascii.Length, 255);
                ex.U8((byte)nameLen);
                ex.Bytes(ascii, 0, nameLen);
                int padded = (nameLen + 1 + 3) / 4 * 4;
                ex.Zeros(padded - nameLen - 1);
                // Nom Unicode
                var uni = Encoding.BigEndianUnicode.GetBytes(l.Name);
                int uniLen = 4 + uni.Length;
                int uniPadded = (uniLen + 3) / 4 * 4;
                ex.Ascii("8BIM");
                ex.Ascii("luni");
                ex.U32((uint)uniPadded);
                ex.U32((uint)l.Name.Length);
                ex.Bytes(uni, 0, uni.Length);
                ex.Zeros(uniPadded - uniLen);
                li.U32((uint)extra.Length);
                li.Bytes(extra.GetBuffer(), 0, (int)extra.Length);
            }
            foreach (var channels in channelData)
                foreach (var ch in channels)
                    li.Bytes(ch, 0, ch.Length);
            while (layerInfo.Length % 4 != 0) li.U8(0);

            o.U32((uint)(4 + layerInfo.Length + 4)); // longueur de la section calques et masques
            o.U32((uint)layerInfo.Length);
            o.Bytes(layerInfo.GetBuffer(), 0, (int)layerInfo.Length);
            o.U32(0); // masque global

            // Image composée (aperçu pour les lecteurs sans calques)
            using var flat = Compositor.Flatten(doc, includePaper: true);
            var planes = ExtractPlanes(flat, new SKRectI(0, 0, w, h));
            o.U16(1); // RLE
            var encodedRows = new List<byte[]>();
            int[] order = compositeAlpha ? new[] { 0, 1, 2, 3 } : new[] { 0, 1, 2 };
            foreach (int c in order)
                for (int y = 0; y < h; y++)
                    encodedRows.Add(PackBits(planes[c], y * w, w));
            foreach (var row in encodedRows) o.U16((ushort)row.Length);
            foreach (var row in encodedRows) o.Bytes(row, 0, row.Length);
        }
        finally
        {
            foreach (var l in layers)
                if (l.Owned) l.Bitmap.Dispose();
        }
    }

    static byte[] Raw0() => new byte[] { 0, 0 };

    static string ToAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s) sb.Append(ch >= 32 && ch < 127 ? ch : '_');
        return sb.ToString();
    }

    /// <summary>Plans R, V, B, A non prémultipliés pour un rectangle.</summary>
    static byte[][] ExtractPlanes(SKBitmap bmp, SKRectI r)
    {
        int w = r.Width, h = r.Height;
        var rgba = new byte[w * h * 4];
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = rgba)
            {
                using var pixmap = bmp.PeekPixels();
                pixmap.ReadPixels(info, (IntPtr)p, w * 4, r.Left, r.Top);
            }
        }
        var planes = new[] { new byte[w * h], new byte[w * h], new byte[w * h], new byte[w * h] };
        for (int i = 0, n = w * h; i < n; i++)
        {
            planes[0][i] = rgba[i * 4];
            planes[1][i] = rgba[i * 4 + 1];
            planes[2][i] = rgba[i * 4 + 2];
            planes[3][i] = rgba[i * 4 + 3];
        }
        return planes;
    }

    /// <summary>Canaux d'un calque dans l'ordre alpha, R, V, B, compressés en RLE.</summary>
    static byte[][] EncodeLayerChannels(SKBitmap bmp, SKRectI r)
    {
        var planes = ExtractPlanes(bmp, r);
        int w = r.Width, h = r.Height;
        var result = new byte[4][];
        int[] order = { 3, 0, 1, 2 };
        for (int c = 0; c < 4; c++)
        {
            var plane = planes[order[c]];
            var rows = new byte[h][];
            int total = 0;
            for (int y = 0; y < h; y++)
            {
                rows[y] = PackBits(plane, y * w, w);
                total += rows[y].Length;
            }
            var ms = new MemoryStream(2 + h * 2 + total);
            var bw = new BeWriter(ms);
            bw.U16(1);
            foreach (var row in rows) bw.U16((ushort)row.Length);
            foreach (var row in rows) bw.Bytes(row, 0, row.Length);
            result[c] = ms.ToArray();
        }
        return result;
    }

    /// <summary>Compression PackBits d'une ligne.</summary>
    static byte[] PackBits(byte[] data, int offset, int length)
    {
        var o = new List<byte>(length / 2 + 4);
        int i = 0;
        while (i < length)
        {
            int run = 1;
            byte v = data[offset + i];
            while (i + run < length && run < 128 && data[offset + i + run] == v) run++;
            if (run >= 2)
            {
                o.Add(unchecked((byte)(sbyte)(1 - run)));
                o.Add(v);
                i += run;
                continue;
            }
            int start = i, lit = 0;
            while (i < length && lit < 128)
            {
                if (i + 2 < length && data[offset + i] == data[offset + i + 1] && data[offset + i] == data[offset + i + 2]) break;
                i++;
                lit++;
            }
            o.Add((byte)(lit - 1));
            for (int k = 0; k < lit; k++) o.Add(data[offset + start + k]);
        }
        return o.ToArray();
    }

    // ================================================================ lecture

    sealed class LayerRec
    {
        public int Top, Left, Bottom, Right;
        public List<(short Id, uint Length)> Channels = new();
        public Dictionary<short, byte[]> Planes = new();
        public string Blend = "norm";
        public byte Opacity = 255, Clipping, Flags;
        public string Name = "";
        public int SectionType;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault = 255, MaskFlags;
        public bool HasMask;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static Document Read(Stream input, string fallbackName)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            input.CopyTo(ms);
            data = ms.ToArray();
        }
        var r = new BeReader(data);
        if (r.Ascii(4) != "8BPS") throw new InvalidDataException("Ce fichier n'est pas un PSD.");
        int version = r.U16();
        if (version != 1) throw new InvalidDataException("Les fichiers PSB (très grands formats) ne sont pas pris en charge.");
        r.Skip(6);
        int channels = r.U16();
        int height = (int)r.U32();
        int width = (int)r.U32();
        int depth = r.U16();
        int mode = r.U16();
        if (depth != 8) throw new InvalidDataException($"PSD en {depth} bits : seul le 8 bits par canal est pris en charge.");
        if (mode != 3 && mode != 1) throw new InvalidDataException("Seuls les PSD en RVB ou niveaux de gris sont pris en charge.");
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
            throw new InvalidDataException($"Dimensions non prises en charge ({width}×{height}, maximum {MaxSide}).");
        bool gray = mode == 1;

        r.Skip((int)r.U32()); // mode couleur
        r.Skip((int)r.U32()); // ressources image

        var layers = new List<LayerRec>();
        long lmLen = r.U32();
        long lmEnd = r.Pos + lmLen;
        if (lmLen > 0)
        {
            long liLen = r.U32();
            long liEnd = r.Pos + liLen;
            if (liLen > 0)
            {
                int count = Math.Abs((int)r.I16());
                for (int i = 0; i < count; i++) layers.Add(ReadRecord(r));
                foreach (var l in layers)
                    foreach (var (id, len) in l.Channels)
                    {
                        long end = r.Pos + len;
                        bool isMask = id == -2;
                        int cw = isMask ? l.MaskRight - l.MaskLeft : l.Width;
                        int chh = isMask ? l.MaskBottom - l.MaskTop : l.Height;
                        if (len >= 2 && cw > 0 && chh > 0 && id >= -2)
                            l.Planes[id] = DecodeChannel(r, cw, chh, (int)len - 2);
                        r.Pos = end;
                    }
            }
            r.Pos = liEnd;
        }
        r.Pos = lmEnd;

        var doc = new Document(width, height) { Name = fallbackName };
        ApplyFolders(layers);
        foreach (var l in layers)
        {
            if (l.SectionType != 0) continue;
            var layer = new Layer(width, height, string.IsNullOrWhiteSpace(l.Name) ? doc.NextLayerName() : l.Name)
            {
                Visible = (l.Flags & 0x02) == 0,
                Opacity = l.Opacity / 255f,
                BlendMode = ModeOf(l.Blend),
                Clipping = l.Clipping == 1,
                AlphaLock = (l.Flags & 0x01) != 0,
            };
            if (l.Width > 0 && l.Height > 0) PaintLayer(layer.Bitmap, l, gray);
            doc.Layers.Add(layer);
        }

        if (doc.Layers.Count == 0)
        {
            // PSD sans calques : on utilise l'image composée.
            var layer = new Layer(width, height, "Image");
            int compression = r.U16();
            var planes = new byte[channels][];
            if (compression == 1)
            {
                var counts = new int[channels * height];
                for (int i = 0; i < counts.Length; i++) counts[i] = r.U16();
                for (int c = 0; c < channels; c++)
                {
                    planes[c] = new byte[width * height];
                    for (int y = 0; y < height; y++)
                    {
                        int n = counts[c * height + y];
                        UnpackBits(data, r.Pos, n, planes[c], y * width, width);
                        r.Pos += n;
                    }
                }
            }
            else if (compression == 0)
            {
                for (int c = 0; c < channels; c++)
                {
                    planes[c] = new byte[width * height];
                    Array.Copy(data, r.Pos, planes[c], 0, width * height);
                    r.Pos += width * height;
                }
            }
            else
            {
                throw new InvalidDataException("Compression de l'image composée non prise en charge.");
            }
            var rec = new LayerRec { Right = width, Bottom = height };
            if (gray)
            {
                rec.Planes[0] = planes[0];
                if (channels > 1) rec.Planes[-1] = planes[1];
            }
            else
            {
                rec.Planes[0] = planes[0];
                rec.Planes[1] = planes[1];
                rec.Planes[2] = planes[2];
                if (channels > 3) rec.Planes[-1] = planes[3];
            }
            PaintLayer(layer.Bitmap, rec, gray);
            doc.Layers.Add(layer);
        }
        doc.ActiveIndex = doc.Layers.Count - 1;
        return doc;
    }

    static LayerRec ReadRecord(BeReader r)
    {
        var l = new LayerRec
        {
            Top = r.I32(), Left = r.I32(), Bottom = r.I32(), Right = r.I32(),
        };
        int n = r.U16();
        for (int c = 0; c < n; c++) l.Channels.Add((r.I16(), r.U32()));
        r.Ascii(4); // 8BIM
        l.Blend = r.Ascii(4);
        l.Opacity = r.U8();
        l.Clipping = r.U8();
        l.Flags = r.U8();
        r.U8();
        long extraLen = r.U32();
        long extraEnd = r.Pos + extraLen;

        long maskLen = r.U32();
        long maskEnd = r.Pos + maskLen;
        if (maskLen >= 18)
        {
            l.MaskTop = r.I32(); l.MaskLeft = r.I32(); l.MaskBottom = r.I32(); l.MaskRight = r.I32();
            l.MaskDefault = r.U8();
            l.MaskFlags = r.U8();
            l.HasMask = (l.MaskFlags & 0x02) == 0;
        }
        r.Pos = maskEnd;
        r.Skip((int)r.U32()); // plages de fusion

        int nameLen = r.U8();
        l.Name = DecodeName(r.Bytes(nameLen));
        int padded = (nameLen + 1 + 3) / 4 * 4;
        r.Skip(padded - nameLen - 1);

        while (r.Pos + 12 <= extraEnd)
        {
            var sig = r.Ascii(4);
            if (sig != "8BIM" && sig != "8B64") break;
            var key = r.Ascii(4);
            long len = r.U32();
            long end = r.Pos + len;
            if (key == "luni" && len >= 4)
            {
                int chars = (int)r.U32();
                var bytes = r.Bytes(Math.Min(chars * 2, (int)len - 4));
                l.Name = Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
            }
            else if ((key == "lsct" || key == "lsdk") && len >= 4)
            {
                l.SectionType = (int)r.U32();
            }
            r.Pos = end;
        }
        r.Pos = extraEnd;
        return l;
    }

    static string DecodeName(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Les dossiers sont aplatis : leur visibilité et leur opacité se reportent sur leurs calques.</summary>
    static void ApplyFolders(List<LayerRec> layers)
    {
        var stack = new Stack<int>();
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            if (l.SectionType == 3)
            {
                stack.Push(i);
            }
            else if ((l.SectionType == 1 || l.SectionType == 2) && stack.Count > 0)
            {
                int start = stack.Pop();
                bool hidden = (l.Flags & 0x02) != 0;
                for (int k = start + 1; k < i; k++)
                {
                    var child = layers[k];
                    if (child.SectionType != 0) continue;
                    if (hidden) child.Flags |= 0x02;
                    child.Opacity = (byte)(child.Opacity * l.Opacity / 255);
                }
            }
        }
    }

    static byte[] DecodeChannel(BeReader r, int w, int h, int size)
    {
        int compression = r.U16();
        var plane = new byte[w * h];
        switch (compression)
        {
            case 0:
                Array.Copy(r.Data, r.Pos, plane, 0, Math.Min(plane.Length, size));
                break;
            case 1:
            {
                var counts = new int[h];
                for (int y = 0; y < h; y++) counts[y] = r.U16();
                for (int y = 0; y < h; y++)
                {
                    UnpackBits(r.Data, r.Pos, counts[y], plane, y * w, w);
                    r.Pos += counts[y];
                }
                break;
            }
            case 2:
            case 3:
            {
                using var zs = new ZLibStream(new MemoryStream(r.Data, (int)r.Pos, size), CompressionMode.Decompress);
                int read = 0;
                while (read < plane.Length)
                {
                    int n = zs.Read(plane, read, plane.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (compression == 3)
                    for (int y = 0; y < h; y++)
                        for (int x = 1; x < w; x++)
                            plane[y * w + x] = (byte)(plane[y * w + x] + plane[y * w + x - 1]);
                break;
            }
            default:
                throw new InvalidDataException("Compression de calque inconnue.");
        }
        return plane;
    }

    static void UnpackBits(byte[] src, long pos, int count, byte[] dst, int dstOffset, int width)
    {
        long end = pos + count;
        int x = 0;
        while (pos < end && x < width)
        {
            sbyte n = unchecked((sbyte)src[pos++]);
            if (n >= 0)
            {
                int len = n + 1;
                for (int k = 0; k < len && x < width && pos < end; k++) dst[dstOffset + x++] = src[pos++];
            }
            else if (n != -128)
            {
                int len = 1 - n;
                byte v = src[pos++];
                for (int k = 0; k < len && x < width; k++) dst[dstOffset + x++] = v;
            }
        }
    }

    static void PaintLayer(SKBitmap target, LayerRec l, bool gray)
    {
        int w = l.Width, h = l.Height;
        var rgba = new byte[w * h * 4];
        l.Planes.TryGetValue(0, out var p0);
        l.Planes.TryGetValue(1, out var p1);
        l.Planes.TryGetValue(2, out var p2);
        l.Planes.TryGetValue(-1, out var pa);
        l.Planes.TryGetValue(-2, out var mask);
        int mw = l.MaskRight - l.MaskLeft;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte rr = p0?[i] ?? 0;
                byte gg = gray ? rr : p1?[i] ?? 0;
                byte bb = gray ? rr : p2?[i] ?? 0;
                int a = pa?[i] ?? 255;
                if (l.HasMask && mask != null)
                {
                    int mx = l.Left + x - l.MaskLeft, my = l.Top + y - l.MaskTop;
                    int m = mx >= 0 && my >= 0 && mx < mw && my < l.MaskBottom - l.MaskTop ? mask[my * mw + mx] : l.MaskDefault;
                    a = a * m / 255;
                }
                rgba[i * 4] = rr;
                rgba[i * 4 + 1] = gg;
                rgba[i * 4 + 2] = bb;
                rgba[i * 4 + 3] = (byte)a;
            }
        }
        using var src = new SKBitmap();
        unsafe
        {
            fixed (byte* p = rgba)
            {
                src.InstallPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), (IntPtr)p, w * 4);
                using var c = new SKCanvas(target);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                c.DrawBitmap(src, l.Left, l.Top, paint);
            }
        }
    }

    // ================================================================ octets big-endian

    sealed class BeWriter
    {
        readonly Stream _s;
        readonly byte[] _buf = new byte[8];
        public BeWriter(Stream s) => _s = s;
        public void U8(byte v) => _s.WriteByte(v);
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_buf, v); _s.Write(_buf, 0, 2); }
        public void I16(short v) { BinaryPrimitives.WriteInt16BigEndian(_buf, v); _s.Write(_buf, 0, 2); }
        public void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_buf, v); _s.Write(_buf, 0, 4); }
        public void I32(int v) { BinaryPrimitives.WriteInt32BigEndian(_buf, v); _s.Write(_buf, 0, 4); }
        public void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); _s.Write(b, 0, b.Length); }
        public void Bytes(byte[] b, int offset, int count) => _s.Write(b, offset, count);
        public void Zeros(int n) { for (int i = 0; i < n; i++) _s.WriteByte(0); }
    }

    sealed class BeReader
    {
        public readonly byte[] Data;
        public long Pos;
        public BeReader(byte[] data) => Data = data;

        void Need(int n)
        {
            if (Pos < 0 || Pos + n > Data.Length) throw new InvalidDataException("Fichier PSD tronqué ou corrompu.");
        }

        public byte U8() { Need(1); return Data[Pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)Pos)); Pos += 2; return v; }
        public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan((int)Pos)); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)Pos)); Pos += 4; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan((int)Pos)); Pos += 4; return v; }
        public string Ascii(int n) { Need(n); var s = Encoding.ASCII.GetString(Data, (int)Pos, n); Pos += n; return s; }
        public byte[] Bytes(int n) { Need(n); var b = new byte[n]; Array.Copy(Data, Pos, b, 0, n); Pos += n; return b; }
        public void Skip(int n) { Need(n); Pos += n; }
    }
}
