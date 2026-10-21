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
/// verrou alpha, masques de calque (appliqués), dossiers (imbriqués), compressions brute / RLE / ZIP.
/// </summary>
public static class PsdFile
{
    public const string Extension = ".psd";
    public const int MaxSide = 8192;

    /// <summary>Nom du calque qui porte le papier dans un PSD écrit par MortisPaint.</summary>
    const string PaperName = "Papier";

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

    /// <summary>Dossier « pass-through » : la clé du bloc lsct fait foi, à défaut celle de l'enregistrement.</summary>
    static bool IsPass(LayerRec l) => (l.SectionBlend ?? l.Blend) == "pass";

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

    // Ordre des canaux d'un calque dans le fichier (alpha, R, V, B), en indices des plans R, V, B, A.
    static readonly int[] LayerOrder = { 3, 0, 1, 2 };

    /// <summary>
    /// Écrit le document en PSD à calques. Le papier visible devient un calque « Papier » en bas.
    /// Écriture en flux : le format exige les tailles avant les données, donc chaque calque est encodé deux fois
    /// (tailles des lignes, puis octets) sans garder plus d'un calque décodé en mémoire ; le flux n'a pas à être
    /// déplaçable. Le document n'est que lu : l'appel peut tourner hors du fil d'interface tant que personne ne le modifie.
    /// </summary>
    public static void Write(Document doc, Stream output)
    {
        int w = doc.Width, h = doc.Height;
        // Enregistrements de bas en haut. Un dossier s'écrit : séparateur de fin (type 3), contenu, puis
        // l'enregistrement du dossier lui-même (type 1 ouvert / 2 fermé) — c'est ainsi qu'ibisPaint et Photoshop les lisent.
        var layers = new List<(string Name, SKBitmap? Bitmap, bool Visible, float Opacity, LayerBlendMode Blend, bool Clip, bool Lock, bool Owned, int Section, bool Pass)>();
        if (doc.PaperVisible)
        {
            var paper = new SKBitmap(BitmapUtil.Info(w, h));
            paper.Erase(doc.PaperColor);
            layers.Add((PaperName, paper, true, 1f, LayerBlendMode.Normal, false, false, true, 0, false));
        }
        var open = new List<LayerFolder>();
        void CloseTo(int depth)
        {
            while (open.Count > depth)
            {
                var f = open[^1];
                open.RemoveAt(open.Count - 1);
                layers.Add((f.Name, null, f.Visible, f.Opacity, f.BlendMode, false, false, false, f.Expanded ? 1 : 2, f.PassThrough));
            }
        }
        foreach (var l in doc.Layers)
        {
            var chain = new List<LayerFolder>();
            for (var f = l.Folder; f != null; f = f.Parent) chain.Insert(0, f);
            int common = 0;
            while (common < open.Count && common < chain.Count && open[common] == chain[common]) common++;
            CloseTo(common);
            for (int k = common; k < chain.Count; k++)
            {
                layers.Add(("</Layer group>", null, true, 1f, LayerBlendMode.Normal, false, false, false, 3, false));
                open.Add(chain[k]);
            }
            layers.Add((l.Name, l.Bitmap, l.Visible, l.Opacity, l.BlendMode, l.Clipping, l.AlphaLock, false, 0, false));
        }
        CloseTo(0);

        try
        {
            // Beaucoup de petites écritures (en-têtes de 2 ou 4 octets) : tampon devant le flux de destination.
            var buffered = new BufferedStream(output, 1 << 16);
            var o = new BeWriter(buffered);
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

            // Calques, passe 1 : enregistrements (quelques octets chacun) et taille RLE de chaque ligne de chaque canal.
            var records = new MemoryStream();
            var li = new BeWriter(records);
            var bounds = new List<SKRectI>();
            var rowCounts = new List<ushort[][]?>();
            long channelBytes = 0;
            var scratch = new byte[2 * w + 16];
            foreach (var l in layers)
            {
                var r = l.Bitmap == null ? SKRectI.Empty : BitmapUtil.AlphaBounds(l.Bitmap);
                var lengths = new long[] { 2, 2, 2, 2 }; // calque vide : compression brute, sans données
                ushort[][]? counts = null;
                if (!r.IsEmpty)
                {
                    var planes = ExtractPlanes(l.Bitmap!, r);
                    counts = new ushort[4][];
                    for (int c = 0; c < 4; c++)
                    {
                        counts[c] = MeasureRows(planes[LayerOrder[c]], r.Width, r.Height, scratch);
                        lengths[c] = 2 + 2L * r.Height + Sum(counts[c]);
                    }
                }
                bounds.Add(r);
                rowCounts.Add(counts);
                foreach (var len in lengths) channelBytes += len;

                li.I32(r.Top); li.I32(r.Left); li.I32(r.Bottom); li.I32(r.Right);
                li.U16(4);
                short[] ids = { -1, 0, 1, 2 };
                for (int c = 0; c < 4; c++)
                {
                    li.I16(ids[c]);
                    li.U32((uint)lengths[c]);
                }
                li.Ascii("8BIM");
                // Dossier « pass-through » : clé « pass », comme Photoshop.
                li.Ascii(l.Pass ? "pass" : KeyOf(l.Blend));
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
                if (l.Section != 0)
                {
                    ex.Ascii("8BIM");
                    ex.Ascii("lsct");
                    ex.U32(12);
                    ex.U32((uint)l.Section);
                    ex.Ascii("8BIM");
                    ex.Ascii(l.Section == 3 || l.Pass ? "pass" : KeyOf(l.Blend));
                }
                li.U32((uint)extra.Length);
                li.Bytes(extra.GetBuffer(), 0, (int)extra.Length);
            }
            long infoLen = 2 + records.Length + channelBytes;
            long infoPadded = (infoLen + 3) / 4 * 4;
            if (infoPadded + 8 > uint.MaxValue)
                throw new IOException("Document trop lourd pour le format PSD (4 Go de calques au plus).");

            o.U32((uint)(4 + infoPadded + 4)); // longueur de la section calques et masques
            o.U32((uint)infoPadded);
            o.I16((short)(compositeAlpha ? -layers.Count : layers.Count));
            o.Bytes(records.GetBuffer(), 0, (int)records.Length);
            // Passe 2 : données des canaux, réencodées calque par calque.
            for (int i = 0; i < layers.Count; i++)
            {
                var counts = rowCounts[i];
                if (counts == null)
                {
                    for (int c = 0; c < 4; c++) o.U16(0);
                    continue;
                }
                var r = bounds[i];
                var planes = ExtractPlanes(layers[i].Bitmap!, r);
                for (int c = 0; c < 4; c++)
                {
                    o.U16(1); // RLE
                    foreach (var n in counts[c]) o.U16(n);
                    WriteRows(o, planes[LayerOrder[c]], r.Width, r.Height, counts[c], scratch);
                }
            }
            o.Zeros((int)(infoPadded - infoLen));
            o.U32(0); // masque global

            // Image composée (aperçu pour les lecteurs sans calques)
            using (var flat = Compositor.Flatten(doc, includePaper: true))
            {
                var planes = ExtractPlanes(flat, new SKRectI(0, 0, w, h));
                o.U16(1); // RLE
                var compositeCounts = new ushort[compositeChannels][];
                for (int c = 0; c < compositeChannels; c++)
                {
                    compositeCounts[c] = MeasureRows(planes[c], w, h, scratch);
                    foreach (var n in compositeCounts[c]) o.U16(n);
                }
                for (int c = 0; c < compositeChannels; c++)
                    WriteRows(o, planes[c], w, h, compositeCounts[c], scratch);
            }
            buffered.Flush();
        }
        finally
        {
            foreach (var l in layers)
                if (l.Owned) l.Bitmap?.Dispose();
        }
    }

    static long Sum(ushort[] values)
    {
        long s = 0;
        foreach (var v in values) s += v;
        return s;
    }

    /// <summary>Taille PackBits de chaque ligne d'un plan.</summary>
    static ushort[] MeasureRows(byte[] plane, int w, int h, byte[] scratch)
    {
        var counts = new ushort[h];
        for (int y = 0; y < h; y++) counts[y] = (ushort)PackBits(plane, y * w, w, scratch);
        return counts;
    }

    /// <summary>Écrit les lignes PackBits d'un plan ; leurs tailles doivent être celles mesurées à la passe 1.</summary>
    static void WriteRows(BeWriter o, byte[] plane, int w, int h, ushort[] counts, byte[] scratch)
    {
        for (int y = 0; y < h; y++)
        {
            int n = PackBits(plane, y * w, w, scratch);
            if (n != counts[y]) throw new InvalidOperationException("Le document a changé pendant l'export PSD.");
            o.Bytes(scratch, 0, n);
        }
    }

    static string ToAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s) sb.Append(ch >= 32 && ch < 127 ? ch : '_');
        return sb.ToString();
    }

    /// <summary>Plans R, V, B, A non prémultipliés pour un rectangle (lu par bandes : pas de copie RVBA complète).</summary>
    static byte[][] ExtractPlanes(SKBitmap bmp, SKRectI r)
    {
        int w = r.Width, h = r.Height;
        var planes = new[] { new byte[w * h], new byte[w * h], new byte[w * h], new byte[w * h] };
        int band = Math.Max(1, (1 << 20) / w);
        var rgba = new byte[w * Math.Min(band, h) * 4];
        using var pixmap = bmp.PeekPixels();
        unsafe
        {
            fixed (byte* p = rgba)
            {
                for (int y0 = 0; y0 < h; y0 += band)
                {
                    int bh = Math.Min(band, h - y0);
                    var info = new SKImageInfo(w, bh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    pixmap.ReadPixels(info, (IntPtr)p, w * 4, r.Left, r.Top + y0);
                    for (int i = 0, n = w * bh, o = y0 * w; i < n; i++)
                    {
                        planes[0][o + i] = rgba[i * 4];
                        planes[1][o + i] = rgba[i * 4 + 1];
                        planes[2][o + i] = rgba[i * 4 + 2];
                        planes[3][o + i] = rgba[i * 4 + 3];
                    }
                }
            }
        }
        return planes;
    }

    /// <summary>Compression PackBits d'une ligne dans <paramref name="dst"/> (2·longueur + 2 octets suffisent) ; renvoie la taille.</summary>
    static int PackBits(byte[] data, int offset, int length, byte[] dst)
    {
        int o = 0, i = 0;
        while (i < length)
        {
            int run = 1;
            byte v = data[offset + i];
            while (i + run < length && run < 128 && data[offset + i + run] == v) run++;
            if (run >= 2)
            {
                dst[o++] = unchecked((byte)(sbyte)(1 - run));
                dst[o++] = v;
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
            dst[o++] = (byte)(lit - 1);
            Buffer.BlockCopy(data, offset + start, dst, o, lit);
            o += lit;
        }
        return o;
    }

    // ================================================================ lecture

    /// <summary>Côté maximal d'un rectangle de calque : la limite du format PSD (30 000 px).</summary>
    const int MaxLayerSide = 30000;
    /// <summary>Nombre maximal de canaux (image ou calque) selon la spécification.</summary>
    const int MaxChannels = 56;
    const string Corrupt = "Fichier PSD tronqué ou corrompu.";

    sealed class LayerRec
    {
        public int Top, Left, Bottom, Right;
        public List<(short Id, uint Length)> Channels = new();
        /// <summary>Plans décodés, limités à la partie sur la toile (<see cref="Visible"/>, ou <see cref="MaskVisible"/> pour le masque).</summary>
        public Dictionary<short, byte[]> Planes = new();
        public string Blend = "norm";
        public byte Opacity = 255, Clipping, Flags;
        public string Name = "";
        public int SectionType;
        /// <summary>Clé de fusion du bloc lsct (prioritaire sur celle de l'enregistrement pour un dossier).</summary>
        public string? SectionBlend;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault = 255, MaskFlags;
        public bool HasMask;
        public SKRectI Visible, MaskVisible;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsPixels => SectionType is not (1 or 2 or 3);
    }

    public static Document Read(Stream input, string fallbackName)
    {
        byte[] data;
        int length;
        if (input is MemoryStream m && m.Position == 0 && m.TryGetBuffer(out var seg) && seg.Offset == 0)
        {
            // Fichier déjà en mémoire (import) : pas de seconde copie.
            data = seg.Array!;
            length = (int)m.Length;
        }
        else
        {
            var ms = new MemoryStream();
            input.CopyTo(ms);
            data = ms.GetBuffer();
            length = (int)ms.Length;
        }
        var r = new BeReader(data, length);
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
        int colorChannels = gray ? 1 : 3;
        if (channels < colorChannels || channels > MaxChannels)
            throw new InvalidDataException($"PSD à {channels} canaux : fichier corrompu.");

        r.Skip(r.Len(r.Length)); // mode couleur
        long resLen = r.Len(r.Length);
        long resEnd = r.Pos + resLen;
        var alphaIds = ReadAlphaIdentifiers(r, resEnd);
        r.Pos = resEnd;

        var layers = new List<LayerRec>();
        bool mergedAlpha = false; // transparence de l'image composée annoncée par le fichier
        long lmLen = r.Len(r.Length);
        long lmEnd = r.Pos + lmLen;
        long channelPos = 0, liEnd = 0;
        if (lmLen >= 4)
        {
            long liLen = r.Len(lmEnd);
            liEnd = r.Pos + liLen;
            if (liLen >= 2)
            {
                // Nombre négatif : le premier canal alpha de l'image composée est sa transparence (spécification).
                short rawCount = r.I16();
                mergedAlpha = rawCount < 0;
                int count = Math.Abs((int)rawCount);
                for (int i = 0; i < count; i++) layers.Add(ReadRecord(r, liEnd));
                channelPos = r.Pos;
            }
            r.Pos = liEnd;
            // Masque global puis informations additionnelles : « Mtrn » (psd-tools) signale aussi cette transparence.
            if (r.Pos + 4 <= lmEnd)
            {
                long globalMask = r.U32();
                if (globalMask <= lmEnd - r.Pos)
                {
                    r.Pos += globalMask;
                    while (r.Pos + 12 <= lmEnd)
                    {
                        var sig = r.Ascii(4);
                        if (sig != "8BIM" && sig != "8B64") break;
                        var key = r.Ascii(4);
                        long len = r.U32();
                        if (key is "Mtrn" or "Mt16" or "Mt32") mergedAlpha = true;
                        if (len > lmEnd - r.Pos) break;
                        r.Pos += len;
                    }
                }
            }
        }
        r.Pos = lmEnd;

        // Chaque calque devient un bitmap pleine toile : on vérifie que l'ensemble tient en mémoire avant d'allouer.
        int pixelLayers = 0;
        foreach (var l in layers)
            if (l.IsPixels) pixelLayers++;
        BitmapUtil.EnsureLayersFit(Math.Max(1, pixelLayers), width, height);

        var doc = new Document(width, height) { Name = fallbackName };
        try
        {
            ReadLayers(r, doc, layers, channelPos, liEnd, gray);
            if (doc.Layers.Count == 0)
            {
                // PSD sans calques : on utilise l'image composée.
                r.Pos = lmEnd;
                int alpha = CompositeAlphaIndex(channels, colorChannels, mergedAlpha, alphaIds);
                var wanted = new List<int> { 0 };
                if (!gray) wanted.AddRange(new[] { 1, 2 });
                if (alpha >= 0) wanted.Add(alpha);
                var planes = ReadComposite(r, width, height, channels, wanted);
                var rec = new LayerRec { Right = width, Bottom = height, Visible = new SKRectI(0, 0, width, height) };
                rec.Planes[0] = planes[0]!;
                if (!gray)
                {
                    rec.Planes[1] = planes[1]!;
                    rec.Planes[2] = planes[2]!;
                }
                if (alpha >= 0) rec.Planes[-1] = planes[alpha]!;
                var layer = new Layer(width, height, "Image");
                doc.Layers.Add(layer);
                PaintLayer(layer.Bitmap, rec, gray);
            }
        }
        catch
        {
            doc.Dispose();
            throw;
        }
        doc.ActiveIndex = doc.Layers.Count - 1;
        return doc;
    }

    /// <summary>
    /// Lit les calques dans l'ordre du fichier (de bas en haut) : décode les canaux d'un calque, le peint, puis
    /// libère ses plans avant de passer au suivant.
    /// </summary>
    static void ReadLayers(BeReader r, Document doc, List<LayerRec> layers, long channelPos, long liEnd, bool gray)
    {
        int width = doc.Width, height = doc.Height;
        var canvas = new SKRectI(0, 0, width, height);
        r.Pos = channelPos;
        bool paper = false;
        // Reconstruit les dossiers : un séparateur (type 3) ouvre un contexte, l'enregistrement du dossier le ferme.
        var contexts = new Stack<List<object>>();
        contexts.Push(new List<object>());
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            l.Visible = SKRectI.Intersect(new SKRectI(l.Left, l.Top, l.Right, l.Bottom), canvas);
            l.MaskVisible = SKRectI.Intersect(new SKRectI(l.MaskLeft, l.MaskTop, l.MaskRight, l.MaskBottom), canvas);
            ReadChannels(r, l, liEnd, gray);
            if (l.SectionType == 3)
            {
                contexts.Push(new List<object>());
                continue;
            }
            if (l.SectionType is 1 or 2)
            {
                if (contexts.Count < 2) continue;
                var folder = new LayerFolder(string.IsNullOrWhiteSpace(l.Name) ? doc.NextFolderName() : l.Name)
                {
                    Visible = (l.Flags & 0x02) == 0,
                    Opacity = l.Opacity / 255f,
                    BlendMode = IsPass(l) ? LayerBlendMode.Normal : ModeOf(l.SectionBlend ?? l.Blend),
                    PassThrough = IsPass(l),
                    Expanded = l.SectionType == 1,
                };
                foreach (var item in contexts.Pop())
                {
                    if (item is Layer child && child.Folder == null) child.Folder = folder;
                    else if (item is LayerFolder sub && sub.Parent == null) sub.Parent = folder;
                }
                contexts.Peek().Add(folder);
                continue;
            }
            // Le papier écrit par MortisPaint redevient le papier du document, pas un calque de plus à chaque aller-retour.
            if (i == 0 && TryReadPaper(l, width, height, gray, out var paperColor))
            {
                doc.PaperColor = paperColor;
                doc.PaperVisible = true;
                paper = true;
                l.Planes.Clear();
                continue;
            }
            var layer = new Layer(width, height, string.IsNullOrWhiteSpace(l.Name) ? doc.NextLayerName() : l.Name)
            {
                Visible = (l.Flags & 0x02) == 0,
                Opacity = l.Opacity / 255f,
                BlendMode = ModeOf(l.Blend),
                Clipping = l.Clipping == 1,
                AlphaLock = (l.Flags & 0x01) != 0,
            };
            doc.Layers.Add(layer);
            if (!IsEmpty(l.Visible)) PaintLayer(layer.Bitmap, l, gray);
            l.Planes.Clear();
            contexts.Peek().Add(layer);
        }
        // Un PSD réduit à son papier garde un calque où dessiner.
        if (paper && doc.Layers.Count == 0) doc.Layers.Add(new Layer(width, height, doc.NextLayerName()));
    }

    /// <summary>
    /// Reconnaît le calque « Papier » qu'écrit <see cref="Write"/> : tout en bas, à la racine, normal, opaque, visible,
    /// pleine toile et uni.
    /// </summary>
    static bool TryReadPaper(LayerRec l, int width, int height, bool gray, out SKColor color)
    {
        color = default;
        if (l.Name != PaperName || l.SectionType != 0 || l.Blend != "norm" || l.Opacity != 255 || l.Clipping != 0
            || (l.Flags & 0x02) != 0 || l.HasMask || l.Left != 0 || l.Top != 0 || l.Right != width || l.Bottom != height)
            return false;
        int n = width * height;
        if (!l.Planes.TryGetValue(0, out var pr) || pr.Length != n) return false;
        byte[]? pg = pr, pb = pr;
        if (!gray && (!l.Planes.TryGetValue(1, out pg) || !l.Planes.TryGetValue(2, out pb) || pg.Length != n || pb.Length != n))
            return false;
        l.Planes.TryGetValue(-1, out var pa);
        if (pa != null && pa.Length != n) return false;
        byte a = pa?[0] ?? 255;
        if (a == 0 || !Uniform(pr) || !Uniform(pg!) || !Uniform(pb!) || (pa != null && !Uniform(pa))) return false;
        color = new SKColor(pr[0], pg![0], pb![0], a);
        return true;
    }

    static bool Uniform(byte[] plane) => plane.AsSpan().IndexOfAnyExcept(plane[0]) < 0;

    // SKRectI.IsEmpty ne reconnaît que le rectangle nul : une intersection qui touche un bord a une largeur nulle sans l'être.
    static bool IsEmpty(SKRectI r) => r.Width <= 0 || r.Height <= 0;

    /// <summary>
    /// Index du canal de transparence de l'image composée (−1 : opaque), selon la spécification et psd-tools :
    /// il y a transparence si le fichier l'annonce (nombre de calques négatif, bloc « Mtrn »), ou s'il y a des canaux
    /// en plus des couleurs sans que la ressource 1053 les désigne tous comme des sélections enregistrées (Id non nul).
    /// Le canal est celui d'Id 0 dans cette ressource, à défaut le premier canal après les couleurs.
    /// </summary>
    static int CompositeAlphaIndex(int channels, int colorChannels, bool mergedAlpha, List<uint>? alphaIds)
    {
        if (channels <= colorChannels) return -1;
        if (!mergedAlpha && alphaIds is { Count: > 0 } && alphaIds.TrueForAll(id => id > 0)) return -1;
        if (alphaIds != null)
        {
            int k = alphaIds.IndexOf(0);
            int index = channels - alphaIds.Count + k;
            if (k >= 0 && index >= colorChannels && index < channels) return index;
        }
        return colorChannels;
    }

    /// <summary>Identifiants des canaux alpha (ressource 1053), ou null si le fichier n'en donne pas.</summary>
    static List<uint>? ReadAlphaIdentifiers(BeReader r, long end)
    {
        List<uint>? ids = null;
        while (r.Pos + 12 <= end)
        {
            if (r.Ascii(4) != "8BIM") break;
            int id = r.U16();
            int nameLen = r.U8();
            long pos = r.Pos + nameLen + (nameLen + 1) % 2; // nom Pascal, longueur totale paire
            if (pos + 4 > end) break;
            r.Pos = pos;
            long size = r.U32();
            if (size > end - r.Pos) break;
            long dataEnd = r.Pos + size;
            if (id == 1053)
            {
                ids = new List<uint>();
                while (r.Pos + 4 <= dataEnd) ids.Add(r.U32());
            }
            r.Pos = dataEnd + (size & 1);
        }
        return ids;
    }

    /// <summary>Plans de l'image composée (seuls les canaux demandés sont décodés).</summary>
    static byte[]?[] ReadComposite(BeReader r, int width, int height, int channels, List<int> wanted)
    {
        int compression = r.U16();
        int size = width * height;
        var planes = new byte[]?[channels];
        foreach (int c in wanted) planes[c] = new byte[size];
        if (compression == 1)
        {
            long start = r.Pos;
            long pos = start + 2L * channels * height;
            if (pos > r.Length) throw new InvalidDataException(Corrupt);
            for (int c = 0; c < channels; c++)
            {
                for (int y = 0; y < height; y++)
                {
                    int n = BinaryPrimitives.ReadUInt16BigEndian(r.Data.AsSpan((int)(start + 2L * (c * height + y))));
                    if (planes[c] != null) UnpackBits(r.Data, pos, Math.Min(pos + n, r.Length), planes[c]!, y * width, width);
                    pos += n;
                }
            }
        }
        else if (compression == 0)
        {
            foreach (int c in wanted)
            {
                long src = r.Pos + (long)c * size;
                long n = Math.Min(size, r.Length - src);
                if (n > 0) Array.Copy(r.Data, src, planes[c]!, 0, n);
            }
        }
        else
        {
            throw new InvalidDataException("Compression de l'image composée non prise en charge.");
        }
        return planes;
    }

    /// <summary>Lit un rectangle : vide s'il est inversé, refusé s'il dépasse le format (allocation démesurée).</summary>
    static void ReadRect(BeReader r, out int top, out int left, out int bottom, out int right)
    {
        top = r.I32();
        left = r.I32();
        bottom = r.I32();
        right = r.I32();
        long w = (long)right - left, h = (long)bottom - top;
        if (w > MaxLayerSide || h > MaxLayerSide)
            throw new InvalidDataException($"Calque de {w}×{h} px, au-delà du format PSD : fichier corrompu.");
        if (w < 0 || h < 0)
        {
            right = left;
            bottom = top;
        }
    }

    static LayerRec ReadRecord(BeReader r, long limit)
    {
        var l = new LayerRec();
        ReadRect(r, out l.Top, out l.Left, out l.Bottom, out l.Right);
        int n = r.U16();
        if (n > MaxChannels) throw new InvalidDataException($"Calque à {n} canaux : fichier corrompu.");
        for (int c = 0; c < n; c++) l.Channels.Add((r.I16(), r.U32()));
        r.Ascii(4); // 8BIM
        l.Blend = r.Ascii(4);
        l.Opacity = r.U8();
        l.Clipping = r.U8();
        l.Flags = r.U8();
        r.U8();
        long extraLen = r.Len(limit);
        long extraEnd = r.Pos + extraLen;

        long maskLen = r.Len(extraEnd);
        long maskEnd = r.Pos + maskLen;
        if (maskLen >= 18)
        {
            ReadRect(r, out l.MaskTop, out l.MaskLeft, out l.MaskBottom, out l.MaskRight);
            l.MaskDefault = r.U8();
            l.MaskFlags = r.U8();
            l.HasMask = (l.MaskFlags & 0x02) == 0;
        }
        r.Pos = maskEnd;
        r.Skip(r.Len(extraEnd)); // plages de fusion

        int nameLen = r.U8();
        if (r.Pos + nameLen > extraEnd) throw new InvalidDataException(Corrupt);
        l.Name = DecodeName(r.Bytes(nameLen));
        int padded = (nameLen + 1 + 3) / 4 * 4;
        r.Skip(padded - nameLen - 1);

        while (r.Pos + 12 <= extraEnd)
        {
            var sig = r.Ascii(4);
            if (sig != "8BIM" && sig != "8B64") break;
            var key = r.Ascii(4);
            long len = r.U32();
            if (len > extraEnd - r.Pos) break;
            long end = r.Pos + len;
            if (key == "luni" && len >= 4)
            {
                long chars = r.U32();
                var bytes = r.Bytes((int)Math.Min(chars * 2, len - 4));
                l.Name = Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
            }
            else if ((key == "lsct" || key == "lsdk") && len >= 4)
            {
                l.SectionType = (int)r.U32();
                if (len >= 12 && r.Ascii(4) == "8BIM") l.SectionBlend = r.Ascii(4);
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

    /// <summary>
    /// Lit les canaux d'un calque à la position courante. Seuls les canaux utiles d'un calque de pixels sont décodés,
    /// et seulement sur la partie visible de la toile.
    /// </summary>
    static void ReadChannels(BeReader r, LayerRec l, long limit, bool gray)
    {
        foreach (var (id, len) in l.Channels)
        {
            if (len > limit - r.Pos) throw new InvalidDataException(Corrupt);
            long end = r.Pos + len;
            bool isMask = id == -2;
            bool wanted = id == -1 || id == 0 || (!gray && id is 1 or 2) || (isMask && l.HasMask);
            int cw = isMask ? l.MaskRight - l.MaskLeft : l.Width;
            int chh = isMask ? l.MaskBottom - l.MaskTop : l.Height;
            if (l.IsPixels && wanted && len >= 2 && cw > 0 && chh > 0)
            {
                var vis = isMask ? l.MaskVisible : l.Visible;
                int ox = isMask ? l.MaskLeft : l.Left, oy = isMask ? l.MaskTop : l.Top;
                l.Planes[id] = IsEmpty(vis)
                    ? Array.Empty<byte>()
                    : DecodeChannel(r, cw, chh, len - 2, new SKRectI(vis.Left - ox, vis.Top - oy, vis.Right - ox, vis.Bottom - oy));
            }
            r.Pos = end;
        }
    }

    /// <summary>
    /// Décode un canal de <paramref name="w"/>×<paramref name="h"/> en ne gardant que <paramref name="crop"/> (relatif au canal) :
    /// la mémoire reste bornée par la toile, même si le fichier annonce un calque immense.
    /// </summary>
    static byte[] DecodeChannel(BeReader r, int w, int h, long size, SKRectI crop)
    {
        int compression = r.U16();
        long start = r.Pos, end = start + size;
        int cw = crop.Width, ch = crop.Height;
        var plane = new byte[cw * ch];
        switch (compression)
        {
            case 0:
                for (int y = 0; y < ch; y++)
                {
                    long src = start + (long)(crop.Top + y) * w + crop.Left;
                    long n = Math.Min(cw, end - src);
                    if (n <= 0) break;
                    Array.Copy(r.Data, src, plane, (long)y * cw, n);
                }
                break;
            case 1:
            {
                long pos = start + 2L * h;
                if (pos > end) throw new InvalidDataException(Corrupt);
                var row = new byte[w];
                for (int y = 0; y < crop.Bottom; y++)
                {
                    int n = BinaryPrimitives.ReadUInt16BigEndian(r.Data.AsSpan((int)(start + 2L * y)));
                    if (y >= crop.Top)
                    {
                        Array.Clear(row);
                        UnpackBits(r.Data, pos, Math.Min(pos + n, end), row, 0, w);
                        Array.Copy(row, crop.Left, plane, (y - crop.Top) * cw, cw);
                    }
                    pos += n;
                }
                break;
            }
            case 2:
            case 3:
            {
                using var zs = new ZLibStream(new MemoryStream(r.Data, (int)start, (int)size), CompressionMode.Decompress);
                var row = new byte[w];
                for (int y = 0; y < crop.Bottom; y++)
                {
                    int read = 0;
                    while (read < w)
                    {
                        int n = zs.Read(row, read, w - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    Array.Clear(row, read, w - read);
                    if (compression == 3)
                        for (int x = 1; x < w; x++)
                            row[x] = (byte)(row[x] + row[x - 1]);
                    if (y >= crop.Top) Array.Copy(row, crop.Left, plane, (y - crop.Top) * cw, cw);
                    if (read < w) break; // flux épuisé : le reste du plan est nul
                }
                break;
            }
            default:
                throw new InvalidDataException("Compression de calque inconnue.");
        }
        return plane;
    }

    /// <summary>Décompresse une ligne PackBits lue dans [<paramref name="pos"/>, <paramref name="end"/>[.</summary>
    static void UnpackBits(byte[] src, long pos, long end, byte[] dst, int dstOffset, int width)
    {
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
                if (pos >= end) break;
                int len = 1 - n;
                byte v = src[pos++];
                for (int k = 0; k < len && x < width; k++) dst[dstOffset + x++] = v;
            }
        }
    }

    /// <summary>Peint les plans d'un calque (partie visible) dans le bitmap pleine toile, par bandes de lignes.</summary>
    static void PaintLayer(SKBitmap target, LayerRec l, bool gray)
    {
        var v = l.Visible;
        int w = v.Width, h = v.Height;
        l.Planes.TryGetValue(0, out var p0);
        l.Planes.TryGetValue(1, out var p1);
        l.Planes.TryGetValue(2, out var p2);
        l.Planes.TryGetValue(-1, out var pa);
        l.Planes.TryGetValue(-2, out var mask);
        bool masked = l.HasMask && mask != null;
        var mv = l.MaskVisible;
        int band = Math.Max(1, (1 << 20) / w);
        var rgba = new byte[w * Math.Min(band, h) * 4];
        using var c = new SKCanvas(target);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        for (int y0 = 0; y0 < h; y0 += band)
        {
            int bh = Math.Min(band, h - y0);
            for (int y = 0; y < bh; y++)
            {
                int py = v.Top + y0 + y;
                for (int x = 0; x < w; x++)
                {
                    int i = (y0 + y) * w + x;
                    int o = (y * w + x) * 4;
                    byte rr = p0?[i] ?? 0;
                    byte gg = gray ? rr : p1?[i] ?? 0;
                    byte bb = gray ? rr : p2?[i] ?? 0;
                    int a = pa?[i] ?? 255;
                    if (masked)
                    {
                        int px = v.Left + x;
                        int mval = px >= mv.Left && px < mv.Right && py >= mv.Top && py < mv.Bottom
                            ? mask![(py - mv.Top) * mv.Width + (px - mv.Left)]
                            : l.MaskDefault;
                        a = a * mval / 255;
                    }
                    rgba[o] = rr;
                    rgba[o + 1] = gg;
                    rgba[o + 2] = bb;
                    rgba[o + 3] = (byte)a;
                }
            }
            using var src = new SKBitmap();
            unsafe
            {
                fixed (byte* p = rgba)
                {
                    src.InstallPixels(new SKImageInfo(w, bh, SKColorType.Rgba8888, SKAlphaType.Unpremul), (IntPtr)p, w * 4);
                    c.DrawBitmap(src, v.Left, v.Top + y0, paint);
                }
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
        /// <summary>Taille utile de <see cref="Data"/> (le tampon d'un MemoryStream peut être plus grand).</summary>
        public readonly long Length;
        public long Pos;

        public BeReader(byte[] data, int length)
        {
            Data = data;
            Length = length;
        }

        void Need(long n)
        {
            if (n < 0 || Pos < 0 || Pos + n > Length) throw new InvalidDataException(Corrupt);
        }

        public byte U8() { Need(1); return Data[Pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)Pos)); Pos += 2; return v; }
        public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan((int)Pos)); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)Pos)); Pos += 4; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan((int)Pos)); Pos += 4; return v; }
        public string Ascii(int n) { Need(n); var s = Encoding.ASCII.GetString(Data, (int)Pos, n); Pos += n; return s; }
        public byte[] Bytes(int n) { Need(n); var b = new byte[n]; Array.Copy(Data, Pos, b, 0, n); Pos += n; return b; }
        public void Skip(long n) { Need(n); Pos += n; }

        /// <summary>Lit une longueur de section (4 octets) et vérifie qu'elle tient avant <paramref name="end"/>.</summary>
        public long Len(long end)
        {
            long n = U32();
            if (n > end - Pos) throw new InvalidDataException(Corrupt);
            return n;
        }
    }
}
