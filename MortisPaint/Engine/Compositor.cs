using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Aperçu temporaire dessiné par-dessus le contenu d'un calque (trait en cours, dégradé...).</summary>
public interface ILayerPreview
{
    Layer Target { get; }
    void DrawPreview(SKCanvas canvas);
}

/// <summary>Filtre d'animation : n'affiche qu'une image (dossier de premier niveau) et ses pelures d'oignon.</summary>
public sealed class FrameFilter
{
    public LayerFolder? Frame { get; init; }
    public LayerFolder? OnionPrev { get; init; }
    public LayerFolder? OnionNext { get; init; }
    public float OnionOpacity { get; init; } = 0.3f;

    public static readonly SKColor PrevTint = new(0xE5, 0x53, 0x3D);
    public static readonly SKColor NextTint = new(0x41, 0xC4, 0x63);
}

public static class Compositor
{
    /// <summary>Compose le document dans <paramref name="target"/>, limité au rectangle donné.</summary>
    public static void Render(SKBitmap target, Document doc, SKRectI rect, ILayerPreview? preview, bool includePaper, FrameFilter? frames = null)
    {
        using var c = new SKCanvas(target);
        c.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        c.Clear(SKColors.Transparent);
        if (includePaper && doc.PaperVisible)
            c.DrawColor(doc.PaperColor, SKBlendMode.Src);
        DrawLayers(c, doc.Layers, preview, frames);
    }

    public static void DrawLayers(SKCanvas c, IReadOnlyList<Layer> layers, ILayerPreview? preview, FrameFilter? frames = null)
    {
        DrawRange(c, layers, 0, layers.Count, null, preview, frames);
    }

    /// <summary>
    /// Compose l'intérieur d'un dossier (ou de la racine), sans le mode ni l'opacité de ce dossier,
    /// en ne dessinant que les calques retenus par <paramref name="include"/> (tous si null).
    /// Les calques écartés restent des bases d'écrêtage : un calque écrêté retenu reste masqué par sa base.
    /// Sert aux fusions et à l'aplatissement d'une image.
    /// </summary>
    internal static void DrawSubset(SKCanvas c, IReadOnlyList<Layer> layers, LayerFolder? level, Func<Layer, bool>? include)
    {
        int start = -1, end = -1;
        for (int i = 0; i < layers.Count; i++)
        {
            if (!layers[i].IsIn(level)) continue;
            if (start < 0) start = i;
            end = i + 1;
        }
        if (start < 0) return;
        DrawRange(c, layers, start, end, level, null, null, 1f, include);
    }

    /// <summary>Unité de composition : un calque, ou un dossier entier (plage de calques).</summary>
    readonly record struct Unit(Layer? Layer, LayerFolder? Folder, int Start, int End);

    static List<Unit> BuildUnits(IReadOnlyList<Layer> layers, int start, int end, LayerFolder? level)
    {
        var units = new List<Unit>();
        int i = start;
        while (i < end)
        {
            var l = layers[i];
            if (l.Folder == level)
            {
                units.Add(new Unit(l, null, i, i + 1));
                i++;
                continue;
            }
            // Dossier enfant direct de « level » qui contient ce calque.
            var child = l.Folder;
            while (child != null && child.Parent != level) child = child.Parent;
            if (child == null)
            {
                // Incohérence (calque hors du niveau courant) : on le traite comme un calque simple.
                units.Add(new Unit(l, null, i, i + 1));
                i++;
                continue;
            }
            int j = i + 1;
            while (j < end && layers[j].IsIn(child)) j++;
            units.Add(new Unit(null, child, i, j));
            i = j;
        }
        return units;
    }

    /// <param name="inherited">Opacité héritée des dossiers « pass-through » englobants.</param>
    /// <param name="include">Calques à dessiner (null = tous) ; les autres ne servent que de masque d'écrêtage.</param>
    static void DrawRange(SKCanvas c, IReadOnlyList<Layer> layers, int start, int end, LayerFolder? level,
        ILayerPreview? preview, FrameFilter? frames, float inherited = 1f, Func<Layer, bool>? include = null)
    {
        var units = BuildUnits(layers, start, end, level);
        int i = 0;
        while (i < units.Count)
        {
            var baseUnit = units[i];
            int next = i + 1;
            while (next < units.Count && units[next].Layer is { Clipping: true }) next++;

            if (level == null && frames != null && baseUnit.Folder != null && baseUnit.Folder != frames.Frame)
            {
                // Mode animation : les autres images sont masquées, sauf les pelures d'oignon.
                var f = baseUnit.Folder;
                if (f == frames.OnionPrev || f == frames.OnionNext)
                {
                    using var onion = new SKPaint
                    {
                        Color = SKColors.White.WithAlpha((byte)(frames.OnionOpacity * 255)),
                        ColorFilter = SKColorFilter.CreateBlendMode(f == frames.OnionPrev ? FrameFilter.PrevTint : FrameFilter.NextTint, SKBlendMode.SrcIn),
                    };
                    c.SaveLayer(onion);
                    DrawRange(c, layers, baseUnit.Start, baseUnit.End, f, preview, null);
                    c.Restore();
                }
                i = next;
                continue;
            }

            if (IsVisible(baseUnit))
            {
                bool baseIncluded = Includes(layers, baseUnit, include);
                bool hasClipped = false;
                for (int k = i + 1; k < next; k++)
                    if (IsVisible(units[k]) && Includes(layers, units[k], include)) { hasClipped = true; break; }

                using var basePaint = UnitPaint(baseUnit, inherited);
                if (!hasClipped)
                {
                    if (baseUnit.Folder is { PassThrough: true } pass)
                    {
                        // Dossier « pass-through » : ses calques se composent directement sur ce qui est dessous,
                        // chacun avec l'opacité du dossier en plus de la sienne.
                        if (baseIncluded)
                            DrawRange(c, layers, baseUnit.Start, baseUnit.End, pass, preview, null, inherited * pass.Opacity, include);
                    }
                    else if (baseIncluded)
                    {
                        DrawUnit(c, layers, baseUnit, basePaint, preview, include);
                    }
                }
                else
                {
                    // Groupe d'écrêtage : base + calques écrêtés, puis fusion avec le mode de la base.
                    // Un dossier « pass-through » qui sert de base est composé isolément (il faut son alpha).
                    SaveLayer(c, basePaint);
                    if (baseIncluded) DrawUnit(c, layers, baseUnit, null, preview, include);
                    using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    for (int k = i + 1; k < next; k++)
                    {
                        var u = units[k];
                        if (!IsVisible(u) || !Includes(layers, u, include)) continue;
                        using var lp = UnitPaint(u, 1f) ?? new SKPaint();
                        c.SaveLayer(lp);
                        DrawUnit(c, layers, u, null, preview, include);
                        // Le masque est toujours la base entière, même si elle n'est pas retenue.
                        DrawUnit(c, layers, baseUnit, maskPaint, preview, null);
                        c.Restore();
                    }
                    c.Restore();
                }
            }
            i = next;
        }
    }

    static bool IsVisible(Unit u) => u.Layer != null
        ? u.Layer.Visible && u.Layer.Opacity > 0f
        : u.Folder!.Visible && u.Folder.Opacity > 0f;

    /// <summary>Vrai si l'unité contient au moins un calque retenu.</summary>
    static bool Includes(IReadOnlyList<Layer> layers, Unit u, Func<Layer, bool>? include)
    {
        if (include == null) return true;
        if (u.Layer != null) return include(u.Layer);
        for (int k = u.Start; k < u.End; k++)
            if (include(layers[k])) return true;
        return false;
    }

    static SKPaint? UnitPaint(Unit u, float inherited)
    {
        var mode = u.Layer?.BlendMode ?? u.Folder!.BlendMode;
        float opacity = (u.Layer?.Opacity ?? u.Folder!.Opacity) * inherited;
        if (mode == LayerBlendMode.Normal && opacity >= 0.999f) return null;
        return new SKPaint
        {
            BlendMode = mode.ToSkia(),
            Color = SKColors.White.WithAlpha((byte)(opacity * 255f + 0.5f)),
        };
    }

    static void SaveLayer(SKCanvas c, SKPaint? paint)
    {
        if (paint == null) c.SaveLayer();
        else c.SaveLayer(paint);
    }

    static void DrawUnit(SKCanvas c, IReadOnlyList<Layer> layers, Unit u, SKPaint? paint, ILayerPreview? preview,
        Func<Layer, bool>? include = null)
    {
        if (u.Layer != null)
        {
            DrawContent(c, u.Layer, paint, preview);
            return;
        }
        // Un dossier est composé isolément puis fusionné avec son mode et son opacité.
        SaveLayer(c, paint);
        DrawRange(c, layers, u.Start, u.End, u.Folder, preview, null, 1f, include);
        c.Restore();
    }

    static void DrawContent(SKCanvas c, Layer l, SKPaint? paint, ILayerPreview? preview)
    {
        if (preview != null && preview.Target == l)
        {
            SaveLayer(c, paint);
            c.DrawBitmap(l.Bitmap, 0, 0);
            preview.DrawPreview(c);
            c.Restore();
        }
        else
        {
            c.DrawBitmap(l.Bitmap, 0, 0, paint);
        }
    }

    /// <summary>Image finale (pour export / miniatures).</summary>
    public static SKBitmap Flatten(Document doc, bool includePaper, FrameFilter? frames = null)
    {
        var bmp = new SKBitmap(BitmapUtil.Info(doc.Width, doc.Height));
        Render(bmp, doc, doc.Bounds, null, includePaper, frames);
        return bmp;
    }

    /// <summary>Image réduite (timelapse, miniatures d'images d'animation).</summary>
    public static SKBitmap RenderScaled(Document doc, int maxSide, bool includePaper, FrameFilter? frames = null)
    {
        float s = System.MathF.Min(1f, maxSide / (float)System.Math.Max(doc.Width, doc.Height));
        int w = System.Math.Max(1, (int)(doc.Width * s)), h = System.Math.Max(1, (int)(doc.Height * s));
        // Composition en pleine résolution puis réduction filtrée (sinon crénelage des traits fins).
        using var full = Flatten(doc, includePaper, frames);
        return BitmapUtil.Scaled(full, w, h);
    }
}
