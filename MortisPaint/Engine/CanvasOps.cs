using System;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>
/// Opérations qui changent la taille de la toile. Elles produisent un nouveau document
/// (les tampons du moteur dépendent de la taille) : l'éditeur remplace alors son moteur.
/// </summary>
public static class CanvasOps
{
    static Document CopyShell(Document src, int width, int height)
    {
        return new Document(width, height)
        {
            Id = src.Id,
            Name = src.Name,
            ProjectPath = src.ProjectPath,
            PaperColor = src.PaperColor,
            PaperVisible = src.PaperVisible,
            AnimationEnabled = src.AnimationEnabled,
            Fps = src.Fps,
            OnionSkin = src.OnionSkin,
            OnionOpacity = src.OnionOpacity,
            IsDirty = true,
        };
    }

    /// <summary>Recopie chaque calque à travers une transformation (les dossiers sont conservés tels quels).</summary>
    static Document Remap(Document src, int width, int height, SKMatrix matrix, bool smooth)
    {
        var doc = CopyShell(src, width, height);
        var sampling = smooth ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKFilterMode.Nearest);
        foreach (var l in src.Layers)
        {
            var nl = new Layer(width, height, l.Name);
            nl.SetProps(l.GetProps());
            using (var c = new SKCanvas(nl.Bitmap))
            using (var img = SKImage.FromBitmap(l.Bitmap))
            using (var p = new SKPaint())
            {
                c.SetMatrix(matrix);
                c.DrawImage(img, 0, 0, sampling, p);
            }
            doc.Layers.Add(nl);
        }
        doc.ActiveIndex = src.ActiveIndex;
        return doc;
    }

    /// <summary>Pivote la toile d'un quart de tour (1 = horaire, -1 = antihoraire, 2 = demi-tour).</summary>
    public static Document Rotate(Document src, int quarterTurns)
    {
        int q = ((quarterTurns % 4) + 4) % 4;
        int w = q % 2 == 0 ? src.Width : src.Height;
        int h = q % 2 == 0 ? src.Height : src.Width;
        var m = q switch
        {
            1 => new SKMatrix(0, -1, src.Height, 1, 0, 0, 0, 0, 1),
            2 => new SKMatrix(-1, 0, src.Width, 0, -1, src.Height, 0, 0, 1),
            3 => new SKMatrix(0, 1, 0, -1, 0, src.Width, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        return Remap(src, w, h, m, smooth: false);
    }

    /// <summary>Change la taille de la toile sans redimensionner le dessin (ancrage 0..1).</summary>
    public static Document Resize(Document src, int width, int height, float anchorX, float anchorY)
    {
        float dx = (width - src.Width) * anchorX, dy = (height - src.Height) * anchorY;
        return Remap(src, width, height, SKMatrix.CreateTranslation(MathF.Round(dx), MathF.Round(dy)), smooth: false);
    }

    /// <summary>Redimensionne le dessin entier (rééchantillonnage).</summary>
    public static Document Scale(Document src, int width, int height)
    {
        return Remap(src, width, height, SKMatrix.CreateScale(width / (float)src.Width, height / (float)src.Height), smooth: true);
    }

    /// <summary>Recadre la toile sur un rectangle.</summary>
    public static Document Crop(Document src, SKRectI rect)
    {
        rect = BitmapUtil.Clamp(rect, src.Width, src.Height);
        if (rect.Width < 1 || rect.Height < 1) throw new ArgumentException("Zone de recadrage vide.");
        return Remap(src, rect.Width, rect.Height, SKMatrix.CreateTranslation(-rect.Left, -rect.Top), smooth: false);
    }
}
