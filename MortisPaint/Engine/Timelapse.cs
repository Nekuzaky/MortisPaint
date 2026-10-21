using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Enregistrement du timelapse : après chaque action, une image réduite de la toile est
/// enregistrée sur disque (JPEG). L'export assemble ces images en GIF animé.
/// </summary>
public sealed class TimelapseRecorder
{
    public const int FrameSide = 720;
    readonly string _dir;
    int _count;
    Task _pending = Task.CompletedTask;

    public TimelapseRecorder(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        _count = Directory.EnumerateFiles(_dir, "*.jpg").Count();
    }

    public int FrameCount => _count;

    /// <summary>Capture la toile ; l'encodage et l'écriture se font en arrière-plan.</summary>
    public void Capture(Document doc, FrameFilter? frames)
    {
        var bmp = Compositor.RenderScaled(doc, FrameSide, includePaper: true, frames);
        int index = ++_count;
        var path = Path.Combine(_dir, $"{index:D6}.jpg");
        var previous = _pending;
        _pending = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                using (bmp)
                using (var img = SKImage.FromBitmap(bmp))
                using (var fs = File.Create(path))
                {
                    // JPEG sans alpha : on aplatit sur blanc (format natif, accepté par l'encodeur).
                    using var flat = new SKBitmap(BitmapUtil.Info(bmp.Width, bmp.Height));
                    using (var c = new SKCanvas(flat))
                    {
                        c.Clear(SKColors.White);
                        c.DrawImage(img, 0, 0);
                    }
                    if (!flat.Encode(fs, SKEncodedImageFormat.Jpeg, 85))
                        throw new IOException("Encodage JPEG impossible.");
                }
            }
            catch (IOException)
            {
                // Disque plein, dossier supprimé ou encodage raté : le timelapse saute cette image.
                try { File.Delete(path); } catch (IOException) { }
            }
        });
    }

    public Task FlushAsync() => _pending;

    public IReadOnlyList<string> Frames() =>
        Directory.EnumerateFiles(_dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();

    public void Clear()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.jpg")) File.Delete(f);
        _count = 0;
    }

    /// <summary>
    /// Assemble le timelapse en GIF : au plus <paramref name="maxFrames"/> images réparties sur tout
    /// l'enregistrement, la dernière image tenue deux secondes.
    /// </summary>
    public static void ExportGif(IReadOnlyList<string> frames, Stream output, int maxSide, int maxFrames, int delayCs)
    {
        if (frames.Count == 0) throw new InvalidOperationException("Le timelapse est vide.");
        var picked = new List<string>();
        int n = Math.Min(maxFrames, frames.Count);
        for (int i = 0; i < n; i++)
            picked.Add(frames[(int)Math.Round(i * (frames.Count - 1) / (double)Math.Max(1, n - 1))]);

        picked.RemoveAll(f => new FileInfo(f).Length == 0);
        if (picked.Count == 0) throw new InvalidOperationException("Le timelapse est vide.");
        using var first = SKBitmap.Decode(picked[0]) ?? throw new InvalidDataException("Image du timelapse illisible.");
        float s = MathF.Min(1f, maxSide / (float)Math.Max(first.Width, first.Height));
        int w = Math.Max(1, (int)(first.Width * s)), h = Math.Max(1, (int)(first.Height * s));
        using var gif = new GifEncoder(output, w, h);
        for (int i = 0; i < picked.Count; i++)
        {
            using var raw = SKBitmap.Decode(picked[i]);
            if (raw == null) continue;
            using var frame = BitmapUtil.Normalize(raw.Copy());
            gif.AddFrame(frame, i == picked.Count - 1 ? 200 : delayCs);
        }
    }
}
