using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace MortisPaint.Engine;

public sealed class ProjectManifest
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public uint PaperColor { get; set; } = 0xFFFFFFFF;
    public bool PaperVisible { get; set; } = true;
    public int ActiveLayer { get; set; }
    public List<LayerEntry> Layers { get; set; } = new();
}

public sealed class LayerEntry
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public bool Visible { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public string Blend { get; set; } = nameof(LayerBlendMode.Normal);
    public bool Clipping { get; set; }
    public bool AlphaLock { get; set; }
}

public sealed class AppSettingsData
{
    public List<uint> Palette { get; set; } = new();
    public List<uint> Recent { get; set; } = new();
    public uint Color { get; set; } = 0xFF1E1E2E;
    public bool TouchDraws { get; set; } = true;
    public bool TwoFingerUndo { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProjectManifest))]
[JsonSerializable(typeof(AppSettingsData))]
internal partial class MortisJsonContext : JsonSerializerContext
{
}

/// <summary>Format de projet MortisPaint (.mpaint) : archive ZIP contenant un manifeste JSON et un PNG par calque.</summary>
public static class ProjectIO
{
    public const string Extension = ".mpaint";
    const string ManifestEntry = "manifest.json";
    const string ThumbnailEntry = "thumbnail.png";

    static readonly SKPngEncoderOptions FastPng = new(SKPngEncoderFilterFlags.AllFilters, 4);

    public static void Save(PaintEngine engine, Stream output)
    {
        var doc = engine.Document;
        var manifest = new ProjectManifest
        {
            Name = doc.Name,
            Width = doc.Width,
            Height = doc.Height,
            PaperColor = (uint)doc.PaperColor,
            PaperVisible = doc.PaperVisible,
            ActiveLayer = doc.ActiveIndex,
        };
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var l = doc.Layers[i];
            string file = $"layers/{i:D3}.png";
            manifest.Layers.Add(new LayerEntry
            {
                Name = l.Name,
                File = file,
                Visible = l.Visible,
                Opacity = l.Opacity,
                Blend = l.BlendMode.ToString(),
                Clipping = l.Clipping,
                AlphaLock = l.AlphaLock,
            });
            WritePng(zip, file, l.Bitmap);
        }
        using (var thumb = engine.RenderThumbnail(480))
            WritePng(zip, ThumbnailEntry, thumb);
        var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using var s = entry.Open();
        JsonSerializer.Serialize(s, manifest, MortisJsonContext.Default.ProjectManifest);
    }

    static void WritePng(ZipArchive zip, string name, SKBitmap bmp)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        using var pixmap = bmp.PeekPixels();
        pixmap.Encode(s, FastPng);
    }

    public static Document Load(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var mEntry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Manifeste manquant");
        ProjectManifest manifest;
        using (var s = mEntry.Open())
            manifest = JsonSerializer.Deserialize(s, MortisJsonContext.Default.ProjectManifest)
                       ?? throw new InvalidDataException("Manifeste invalide");
        if (manifest.Width <= 0 || manifest.Height <= 0 || manifest.Width > 16384 || manifest.Height > 16384)
            throw new InvalidDataException("Dimensions invalides");

        var doc = new Document(manifest.Width, manifest.Height)
        {
            Name = manifest.Name,
            PaperColor = new SKColor(manifest.PaperColor),
            PaperVisible = manifest.PaperVisible,
        };
        foreach (var le in manifest.Layers)
        {
            var layer = new Layer(manifest.Width, manifest.Height, le.Name)
            {
                Visible = le.Visible,
                Opacity = Math.Clamp(le.Opacity, 0f, 1f),
                BlendMode = Enum.TryParse<LayerBlendMode>(le.Blend, out var bm) ? bm : LayerBlendMode.Normal,
                Clipping = le.Clipping,
                AlphaLock = le.AlphaLock,
            };
            var e = zip.GetEntry(le.File);
            if (e != null)
            {
                using var es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                ms.Position = 0;
                using var bmp = BitmapUtil.Decode(ms);
                if (bmp != null)
                {
                    using var c = new SKCanvas(layer.Bitmap);
                    using var p = new SKPaint { BlendMode = SKBlendMode.Src };
                    c.DrawBitmap(bmp, 0, 0, p);
                }
            }
            doc.Layers.Add(layer);
        }
        if (doc.Layers.Count == 0) doc.Layers.Add(new Layer(doc.Width, doc.Height, "Calque 1"));
        doc.ActiveIndex = manifest.ActiveLayer;
        return doc;
    }

    public static (string Name, int Width, int Height)? ReadInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var e = zip.GetEntry(ManifestEntry);
            if (e == null) return null;
            using var s = e.Open();
            var m = JsonSerializer.Deserialize(s, MortisJsonContext.Default.ProjectManifest);
            return m == null ? null : (m.Name, m.Width, m.Height);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ReadThumbnailPng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var e = zip.GetEntry(ThumbnailEntry);
            if (e == null) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static void ExportImage(Document doc, Stream output, SKEncodedImageFormat format, int quality = 92)
    {
        bool opaque = format == SKEncodedImageFormat.Jpeg;
        using var flat = Compositor.Flatten(doc, includePaper: true);
        if (opaque && !doc.PaperVisible)
        {
            using var c = new SKCanvas(flat);
            c.DrawColor(SKColors.White, SKBlendMode.DstOver);
        }
        using var pixmap = flat.PeekPixels();
        if (format == SKEncodedImageFormat.Png) pixmap.Encode(output, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6));
        else pixmap.Encode(output, format, quality);
    }

    public static string SettingsToJson(AppSettingsData data) =>
        JsonSerializer.Serialize(data, MortisJsonContext.Default.AppSettingsData);

    public static AppSettingsData? SettingsFromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, MortisJsonContext.Default.AppSettingsData); }
        catch { return null; }
    }
}
