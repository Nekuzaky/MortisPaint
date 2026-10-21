using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Composition réduite directe (timelapse, miniatures des images d'animation, export GIF) : chaque
/// calque affiché est réduit avec filtrage (mipmaps), puis la pile est composée à la taille de sortie,
/// sans image pleine résolution. Les calques réduits restent en cache tant que leur version ne change
/// pas : après un trait, seul le calque touché est réduit de nouveau.
/// </summary>
public sealed class ScaledRenderer : IDisposable
{
    static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    readonly Dictionary<Layer, (int Version, SKBitmap Bitmap)> _cache = new();
    SKSizeI _size;
    SKBitmap? _empty;

    /// <summary>Taille de sortie : le plus grand côté ramené à <paramref name="maxSide"/> (jamais agrandi).</summary>
    public static SKSizeI FitSize(int width, int height, int maxSide)
    {
        float s = MathF.Min(1f, maxSide / (float)Math.Max(width, height));
        return new SKSizeI(Math.Max(1, (int)(width * s)), Math.Max(1, (int)(height * s)));
    }

    /// <summary>
    /// Compose le document réduit. <paramref name="live"/> est toujours relu : ses pixels peuvent changer
    /// sur place sans changer de version (aperçu d'un filtre ou d'une transformation).
    /// </summary>
    public SKBitmap Render(Document doc, int maxSide, bool includePaper, FrameFilter? frames = null, Layer? live = null)
    {
        var size = FitSize(doc.Width, doc.Height, maxSide);
        if (size != _size)
        {
            ClearCache();
            _size = size;
        }
        float sx = size.Width / (float)doc.Width, sy = size.Height / (float)doc.Height;
        // Pile de travail à la taille de sortie : mêmes dossiers et propriétés, pixels réduits.
        // (Pas de Dispose : ses bitmaps appartiennent au cache.)
        var small = new Document(size.Width, size.Height) { PaperColor = doc.PaperColor, PaperVisible = doc.PaperVisible };
        var used = new HashSet<Layer>();
        foreach (var l in doc.Layers)
        {
            SKBitmap bmp;
            if (IsDrawn(l, frames))
            {
                bmp = Reduced(l, sx, sy, l == live);
                used.Add(l);
            }
            else
            {
                // Calque que le compositeur ne dessinera pas (masqué, autre image) : inutile de le réduire.
                bmp = _empty ??= NewEmpty();
            }
            var proxy = new Layer(bmp, l.Name);
            proxy.SetProps(l.GetProps());
            small.Layers.Add(proxy);
        }
        // Calques supprimés, masqués ou d'autres images : ils quittent le cache.
        foreach (var gone in _cache.Keys.Where(l => !used.Contains(l)).ToList())
        {
            _cache[gone].Bitmap.Dispose();
            _cache.Remove(gone);
        }
        var target = new SKBitmap(BitmapUtil.Info(size.Width, size.Height));
        Compositor.Render(target, small, small.Bounds, null, includePaper, frames);
        return target;
    }

    /// <summary>Même règle que le compositeur : visible avec ses dossiers et, en animation, dans l'image ou une pelure.</summary>
    static bool IsDrawn(Layer l, FrameFilter? frames)
    {
        if (l.IsDisposed || !l.IsEffectivelyVisible || l.Opacity <= 0f) return false;
        if (frames == null) return true;
        var top = Document.TopFolder(l);
        return top == null || top == frames.Frame || top == frames.OnionPrev || top == frames.OnionNext;
    }

    SKBitmap Reduced(Layer l, float sx, float sy, bool live)
    {
        _cache.TryGetValue(l, out var hit);
        if (hit.Bitmap != null && hit.Version == l.Version && !live) return hit.Bitmap;
        var bmp = hit.Bitmap ?? new SKBitmap(BitmapUtil.Info(_size.Width, _size.Height));
        using (var c = new SKCanvas(bmp))
        using (var pix = l.Bitmap.PeekPixels())
        using (var img = SKImage.FromPixels(pix))
        using (var paint = new SKPaint())
        {
            c.Clear(SKColors.Transparent);
            c.DrawImage(img, new SKRect(0, 0, l.Bitmap.Width * sx, l.Bitmap.Height * sy), Sampling, paint);
        }
        // Un calque « vivant » garde une version invalide : il sera relu la prochaine fois.
        _cache[l] = (live ? int.MinValue : l.Version, bmp);
        return bmp;
    }

    static SKBitmap NewEmpty()
    {
        var bmp = new SKBitmap(BitmapUtil.Info(1, 1));
        bmp.Erase(SKColors.Transparent);
        return bmp;
    }

    void ClearCache()
    {
        foreach (var (_, bmp) in _cache.Values) bmp.Dispose();
        _cache.Clear();
    }

    public void Dispose()
    {
        ClearCache();
        _empty?.Dispose();
        _empty = null;
    }
}

/// <summary>
/// Enregistrement du timelapse : après chaque action, une image réduite de la toile est
/// enregistrée sur disque (JPEG). L'export assemble ces images en GIF animé.
/// Au-delà de <see cref="MaxFrames"/> images, une sur deux est supprimée et chaque image suivante
/// couvre deux fois plus d'actions : le dossier reste borné et le rythme reste régulier.
/// </summary>
public sealed class TimelapseRecorder
{
    public const int FrameSide = 720;
    /// <summary>Plafond d'images gardées sur disque (≈ 100 Ko chacune).</summary>
    public const int MaxFrames = 2000;
    const string StrideFile = "stride.txt";

    readonly string _dir;
    readonly int _maxFrames;
    readonly ScaledRenderer _renderer = new();
    int _count;      // plus grand numéro d'image utilisé
    int _stride = 1; // actions par image (double à chaque élagage)
    int _inSlot;     // captures déjà écrites dans la dernière image
    Task _pending = Task.CompletedTask;

    public TimelapseRecorder(string directory) : this(directory, MaxFrames) { }

    public TimelapseRecorder(string directory, int maxFrames)
    {
        _dir = directory;
        _maxFrames = Math.Max(2, maxFrames);
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.EnumerateFiles(_dir, "*.tmp").ToList()) TryDelete(f);
            // On repart du plus grand numéro, pas du nombre de fichiers : après un trou,
            // les images existantes ne sont pas écrasées.
            _count = Directory.EnumerateFiles(_dir, "*.jpg").Select(IndexOf).DefaultIfEmpty(0).Max();
            var stride = Path.Combine(_dir, StrideFile);
            if (File.Exists(stride) && int.TryParse(File.ReadAllText(stride).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int s))
                _stride = Math.Clamp(s, 1, 1 << 20);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dossier inaccessible : chaque capture réessaiera de le créer.
        }
        _inSlot = _stride; // la prochaine capture ouvre une nouvelle image
    }

    public int FrameCount => _count;

    /// <summary>
    /// Capture la toile : l'image d'animation courante, sans pelures d'oignon. La composition (réduite)
    /// se fait ici ; l'encodage et l'écriture en arrière-plan. Une erreur ne fait que sauter l'image.
    /// </summary>
    public void Capture(Document doc, FrameFilter? frames)
    {
        SKBitmap bmp;
        try
        {
            // Les pelures sont une aide à l'écran : le timelapse montre l'image seule.
            var filter = frames == null ? null : new FrameFilter { Frame = frames.Frame };
            bmp = _renderer.Render(doc, FrameSide, includePaper: true, filter, live: doc.ActiveLayer);
        }
        catch (Exception)
        {
            return; // composition impossible (mémoire) : le timelapse saute cette image
        }

        if (_count == 0 || _inSlot >= _stride)
        {
            // Nouvelle image ; au plafond, on garde d'abord une image sur deux.
            while (_count >= _maxFrames)
            {
                int n = _count;
                _count = (n + 1) / 2;
                _stride *= 2;
                int stride = _stride;
                Enqueue(() => Thin(n, stride));
            }
            _count++;
            _inSlot = 0;
        }
        // Avec un pas > 1, les captures suivantes remplacent la dernière image :
        // elle montre toujours l'état le plus récent.
        _inSlot++;
        int index = _count;
        Enqueue(() => Write(bmp, index));
    }

    public Task FlushAsync() => _pending;

    public IReadOnlyList<string> Frames()
    {
        try
        {
            return Directory.EnumerateFiles(_dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Efface l'enregistrement, après les écritures en cours (sinon une image en vol survivrait).</summary>
    public void Clear()
    {
        _count = 0;
        _stride = 1;
        _inSlot = 0;
        Enqueue(() =>
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.EnumerateFiles(_dir).ToList())
                if (Path.GetExtension(f) is ".jpg" or ".tmp" || Path.GetFileName(f) == StrideFile) TryDelete(f);
        });
    }

    /// <summary>Enchaîne un travail disque après les précédents ; ses erreurs sont rattrapées, la file ne s'arrête jamais.</summary>
    void Enqueue(Action work)
    {
        var previous = _pending;
        _pending = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Les travaux précédents ne lèvent pas ; par prudence, on continue quand même.
            }
            try
            {
                work();
            }
            catch (Exception)
            {
                // Disque plein, dossier supprimé ou verrouillé, encodage raté : on saute ce travail.
            }
        });
    }

    void Write(SKBitmap bmp, int index)
    {
        var path = FramePath(index);
        var tmp = Path.ChangeExtension(path, ".tmp");
        try
        {
            Directory.CreateDirectory(_dir); // le dossier a pu être supprimé entre-temps
            using (var fs = File.Create(tmp))
            {
                // JPEG sans alpha : on aplatit sur blanc (format natif, accepté par l'encodeur).
                using var flat = new SKBitmap(BitmapUtil.Info(bmp.Width, bmp.Height));
                using (var c = new SKCanvas(flat))
                {
                    c.Clear(SKColors.White);
                    c.DrawBitmap(bmp, 0, 0);
                }
                if (!flat.Encode(fs, SKEncodedImageFormat.Jpeg, 85))
                    throw new IOException("Encodage JPEG impossible.");
            }
            // Fichier temporaire puis renommage : une image n'est jamais à moitié écrite.
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            bmp.Dispose();
        }
    }

    /// <summary>Garde une image sur deux parmi 1..n (dont la plus récente) et renumérote à partir de 1.</summary>
    void Thin(int n, int stride)
    {
        for (int i = 1; i <= n; i++)
        {
            var path = FramePath(i);
            int j = (i + n % 2) / 2;
            try
            {
                if ((n - i) % 2 != 0) File.Delete(path);
                else if (j != i && File.Exists(path)) File.Move(path, FramePath(j), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Image verrouillée (export en cours) : elle reste à sa place, le reste suit.
            }
        }
        File.WriteAllText(Path.Combine(_dir, StrideFile), stride.ToString(CultureInfo.InvariantCulture));
    }

    string FramePath(int index) => Path.Combine(_dir, $"{index:D6}.jpg");

    static int IndexOf(string path) =>
        int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : 0;

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Assemble le timelapse en GIF : au plus <paramref name="maxFrames"/> images réparties sur tout
    /// l'enregistrement, la dernière image tenue deux secondes. Le cadre suit les proportions de la
    /// dernière image (l'œuvre finale) ; celles d'une autre taille de toile y sont ajustées sans déformation.
    /// </summary>
    public static void ExportGif(IReadOnlyList<string> frames, Stream output, int maxSide, int maxFrames, int delayCs)
    {
        if (frames.Count == 0) throw new InvalidOperationException("Le timelapse est vide.");
        var picked = new List<string>();
        int n = Math.Min(maxFrames, frames.Count);
        for (int i = 0; i < n; i++)
            picked.Add(frames[(int)Math.Round(i * (frames.Count - 1) / (double)Math.Max(1, n - 1))]);

        picked.RemoveAll(f => FileLength(f) == 0);
        if (picked.Count == 0) throw new InvalidOperationException("Le timelapse est vide.");
        var size = SKSizeI.Empty;
        for (int i = picked.Count - 1; i >= 0 && size.IsEmpty; i--) size = ImageSize(picked[i]);
        if (size.IsEmpty) throw new InvalidDataException("Image du timelapse illisible.");
        var gifSize = ScaledRenderer.FitSize(size.Width, size.Height, maxSide);
        using var gif = new GifEncoder(output, gifSize.Width, gifSize.Height);
        for (int i = 0; i < picked.Count; i++)
        {
            using var frame = Decode(picked[i]);
            if (frame == null) continue;
            gif.AddFrame(frame, i == picked.Count - 1 ? 200 : delayCs);
        }
    }

    static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    static SKSizeI ImageSize(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec == null ? SKSizeI.Empty : new SKSizeI(codec.Info.Width, codec.Info.Height);
    }

    /// <summary>Image lue du disque, ou null si elle a disparu entre-temps (élagage) ou est illisible.</summary>
    static SKBitmap? Decode(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return BitmapUtil.Decode(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
