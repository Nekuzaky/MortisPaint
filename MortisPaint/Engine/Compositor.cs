using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Aperçu temporaire dessiné par-dessus le contenu d'un calque (trait en cours, dégradé...).</summary>
public interface ILayerPreview
{
    Layer Target { get; }
    void DrawPreview(SKCanvas canvas);
}

public static class Compositor
{
    /// <summary>Compose le document dans <paramref name="target"/>, limité au rectangle donné.</summary>
    public static void Render(SKBitmap target, Document doc, SKRectI rect, ILayerPreview? preview, bool includePaper)
    {
        using var c = new SKCanvas(target);
        c.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        c.Clear(SKColors.Transparent);
        if (includePaper && doc.PaperVisible)
            c.DrawColor(doc.PaperColor, SKBlendMode.Src);
        DrawLayers(c, doc.Layers, preview);
    }

    public static void DrawLayers(SKCanvas c, IReadOnlyList<Layer> layers, ILayerPreview? preview)
    {
        int i = 0;
        while (i < layers.Count)
        {
            var baseLayer = layers[i];
            int end = i + 1;
            while (end < layers.Count && layers[end].Clipping) end++;

            if (baseLayer.Visible && baseLayer.Opacity > 0f)
            {
                bool hasClipped = false;
                for (int k = i + 1; k < end; k++)
                    if (layers[k].Visible && layers[k].Opacity > 0f) { hasClipped = true; break; }

                using var basePaint = LayerPaint(baseLayer);
                if (!hasClipped)
                {
                    DrawContent(c, baseLayer, basePaint, preview);
                }
                else
                {
                    // Groupe d'écrêtage : base + calques écrêtés, puis fusion avec le mode de la base.
                    SaveLayer(c, basePaint);
                    DrawContent(c, baseLayer, null, preview);
                    using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    for (int k = i + 1; k < end; k++)
                    {
                        var l = layers[k];
                        if (!l.Visible || l.Opacity <= 0f) continue;
                        using var lp = LayerPaint(l) ?? new SKPaint();
                        c.SaveLayer(lp);
                        DrawContent(c, l, null, preview);
                        DrawContent(c, baseLayer, maskPaint, preview);
                        c.Restore();
                    }
                    c.Restore();
                }
            }
            i = end;
        }
    }

    static SKPaint? LayerPaint(Layer l)
    {
        if (l.BlendMode == LayerBlendMode.Normal && l.Opacity >= 0.999f) return null;
        return new SKPaint
        {
            BlendMode = l.BlendMode.ToSkia(),
            Color = SKColors.White.WithAlpha((byte)(l.Opacity * 255f + 0.5f)),
        };
    }

    static void SaveLayer(SKCanvas c, SKPaint? paint)
    {
        if (paint == null) c.SaveLayer();
        else c.SaveLayer(paint);
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
    public static SKBitmap Flatten(Document doc, bool includePaper)
    {
        var bmp = new SKBitmap(BitmapUtil.Info(doc.Width, doc.Height));
        Render(bmp, doc, doc.Bounds, null, includePaper);
        return bmp;
    }
}
