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

    static void DrawRange(SKCanvas c, IReadOnlyList<Layer> layers, int start, int end, LayerFolder? level,
        ILayerPreview? preview, FrameFilter? frames)
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
                bool hasClipped = false;
                for (int k = i + 1; k < next; k++)
                    if (IsVisible(units[k])) { hasClipped = true; break; }

                using var basePaint = UnitPaint(baseUnit);
                if (!hasClipped)
                {
                    DrawUnit(c, layers, baseUnit, basePaint, preview);
                }
                else
                {
                    // Groupe d'écrêtage : base + calques écrêtés, puis fusion avec le mode de la base.
                    SaveLayer(c, basePaint);
                    DrawUnit(c, layers, baseUnit, null, preview);
                    using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    for (int k = i + 1; k < next; k++)
                    {
                        var u = units[k];
                        if (!IsVisible(u)) continue;
                        using var lp = UnitPaint(u) ?? new SKPaint();
                        c.SaveLayer(lp);
                        DrawUnit(c, layers, u, null, preview);
                        DrawUnit(c, layers, baseUnit, maskPaint, preview);
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

    static SKPaint? UnitPaint(Unit u)
    {
        var mode = u.Layer?.BlendMode ?? u.Folder!.BlendMode;
        float opacity = u.Layer?.Opacity ?? u.Folder!.Opacity;
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

    static void DrawUnit(SKCanvas c, IReadOnlyList<Layer> layers, Unit u, SKPaint? paint, ILayerPreview? preview)
    {
        if (u.Layer != null)
        {
            DrawContent(c, u.Layer, paint, preview);
            return;
        }
        // Un dossier est composé isolément puis fusionné avec son mode et son opacité.
        SaveLayer(c, paint);
        DrawRange(c, layers, u.Start, u.End, u.Folder, preview, null);
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
