using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

/// <summary>Point d'entrée du moteur : document, composition, historique et opérations sur les calques.</summary>
public sealed partial class PaintEngine : IDisposable
{
    ILayerPreview? _preview;
    SKRectI _dirty = SKRectI.Empty;

    public Document Document { get; }
    /// <summary>Image composée (papier + calques), tenue à jour par zones.</summary>
    public SKBitmap Composite { get; }
    /// <summary>Tampon du trait en cours.</summary>
    public SKBitmap StrokeBuffer { get; }
    public History History { get; }
    public Selection Selection { get; }
    public Symmetry Symmetry { get; } = new();

    /// <summary>Une zone de l'image composée doit être recalculée.</summary>
    public event Action? DisplayInvalidated;
    /// <summary>La pile de calques ou leurs propriétés ont changé.</summary>
    public event Action? StructureChanged;
    public event Action<Layer>? LayerPixelsChanged;

    public PaintEngine(Document doc)
    {
        Document = doc;
        var info = BitmapUtil.Info(doc.Width, doc.Height);
        Composite = new SKBitmap(info);
        StrokeBuffer = new SKBitmap(info);
        StrokeBuffer.Erase(SKColors.Transparent);
        History = new History(this);
        Selection = new Selection(doc.Width, doc.Height);
        Selection.Changed += () => DisplayInvalidated?.Invoke();
        Symmetry.CenterX = doc.Width / 2f;
        Symmetry.CenterY = doc.Height / 2f;
        Invalidate(doc.Bounds);
    }

    public int Width => Document.Width;
    public int Height => Document.Height;
    public Layer ActiveLayer => Document.ActiveLayer;

    // ---------------------------------------------------------------- affichage

    public void Invalidate(SKRectI r)
    {
        r = BitmapUtil.Clamp(r, Width, Height);
        if (r.IsEmpty) return;
        _dirty = BitmapUtil.Union(_dirty, r);
        DisplayInvalidated?.Invoke();
    }

    public void SetPreview(ILayerPreview? preview) => _preview = preview;

    /// <summary>Recompose la zone sale. Retourne false s'il n'y avait rien à faire.</summary>
    public bool UpdateComposite(out SKRectI rect)
    {
        rect = _dirty;
        if (_dirty.IsEmpty) return false;
        _dirty = SKRectI.Empty;
        Compositor.Render(Composite, Document, rect, _preview, includePaper: true, CurrentFrameFilter());
        return true;
    }

    public void NotifyStructure()
    {
        Invalidate(Document.Bounds);
        StructureChanged?.Invoke();
    }

    public void NotifyLayerPixels(Layer l) => LayerPixelsChanged?.Invoke(l);

    // ---------------------------------------------------------------- calques

    void Structure(string name, Action change, List<PixelAction>? pixels = null)
    {
        var before = DocState.Capture(Document);
        change();
        var after = DocState.Capture(Document);
        History.Push(new StructureAction(name, before, after, pixels));
        NotifyStructure();
    }

    public void SetActive(int index)
    {
        if (index == Document.ActiveIndex) return;
        Document.ActiveIndex = index;
        StructureChanged?.Invoke();
    }

    public void AddLayer()
    {
        Structure("Nouveau calque", () =>
        {
            var l = new Layer(Width, Height, Document.NextLayerName()) { Folder = ActiveLayer.Folder };
            Document.Layers.Insert(Document.ActiveIndex + 1, l);
            Document.ActiveIndex++;
        });
    }

    public Layer InsertLayer(Layer layer, string actionName)
    {
        Structure(actionName, () =>
        {
            Document.Layers.Insert(Document.ActiveIndex + 1, layer);
            Document.ActiveIndex++;
        });
        return layer;
    }

    public void DuplicateLayer()
    {
        var src = ActiveLayer;
        InsertLayer(src.Clone(src.Name + " (copie)"), "Dupliquer le calque");
    }

    public void DeleteLayer()
    {
        if (Document.Layers.Count <= 1)
        {
            ClearLayer();
            return;
        }
        Structure("Supprimer le calque", () =>
        {
            int i = Document.ActiveIndex;
            Document.Layers.RemoveAt(i);
            Document.ActiveIndex = Math.Max(0, i - 1);
        });
    }

    /// <summary>
    /// Monte (delta &gt; 0) ou descend le calque actif d'un cran. Au bord d'un dossier, le calque en sort ;
    /// face à un dossier voisin, il y entre. Sinon il échange sa place avec son voisin.
    /// </summary>
    public void MoveLayer(int delta)
    {
        var layers = Document.Layers;
        int i = Document.ActiveIndex, n = layers.Count;
        var l = layers[i];
        var f = l.Folder;
        int j = delta > 0 ? i + 1 : i - 1;
        bool atEdge = f != null && (j < 0 || j >= n || !layers[j].IsIn(f));
        if (!atEdge && (j < 0 || j >= n)) return;
        Structure("Déplacer le calque", () =>
        {
            if (atEdge)
            {
                l.Folder = f!.Parent;
                return;
            }
            var neighbour = layers[j];
            if (neighbour.Folder != f)
            {
                var g = neighbour.Folder;
                while (g != null && g.Parent != f) g = g.Parent;
                if (g != null)
                {
                    l.Folder = g;
                    return;
                }
            }
            layers[i] = neighbour;
            layers[j] = l;
            Document.ActiveIndex = j;
        });
    }

    public void MergeDown()
    {
        int i = Document.ActiveIndex;
        if (i <= 0) return;
        var upper = Document.Layers[i];
        var lower = Document.Layers[i - 1];
        var full = Document.Bounds;
        var before = BitmapUtil.Copy(lower.Bitmap);
        if (upper.Visible)
        {
            using var c = new SKCanvas(lower.Bitmap);
            using var p = new SKPaint
            {
                BlendMode = upper.Clipping ? SKBlendMode.SrcATop : upper.BlendMode.ToSkia(),
                Color = SKColors.White.WithAlpha((byte)(upper.Opacity * 255)),
            };
            if (upper.Clipping && upper.BlendMode != LayerBlendMode.Normal)
            {
                // Mode de fusion + écrêtage : on applique le mode puis on masque par l'alpha de la base.
                using var tmp = BitmapUtil.Copy(lower.Bitmap);
                using (var tc = new SKCanvas(tmp))
                using (var bp = new SKPaint { BlendMode = upper.BlendMode.ToSkia(), Color = p.Color })
                {
                    tc.DrawBitmap(upper.Bitmap, 0, 0, bp);
                    using var mp = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    tc.DrawBitmap(lower.Bitmap, 0, 0, mp);
                }
                using var src = new SKPaint { BlendMode = SKBlendMode.Src };
                c.DrawBitmap(tmp, 0, 0, src);
            }
            else
            {
                c.DrawBitmap(upper.Bitmap, 0, 0, p);
            }
        }
        var after = BitmapUtil.Copy(lower.Bitmap);
        lower.Touch();
        var pixel = new PixelAction("Fusion", lower, full, before, after);
        // Les pixels sont déjà modifiés : l'action structurelle enregistre l'état avant/après.
        var stBefore = DocState.Capture(Document);
        Document.Layers.RemoveAt(i);
        Document.ActiveIndex = i - 1;
        var stAfter = DocState.Capture(Document);
        History.Push(new StructureAction("Fusionner vers le bas", stBefore, stAfter, new List<PixelAction> { pixel }));
        NotifyStructure();
        NotifyLayerPixels(lower);
    }

    /// <summary>Aplatit tous les calques visibles en un seul.</summary>
    public void FlattenAll()
    {
        var flat = new Layer(Compositor.Flatten(Document, includePaper: false), "Image aplatie");
        Structure("Aplatir l'image", () =>
        {
            Document.Layers.Clear();
            Document.Layers.Add(flat);
            Document.ActiveIndex = 0;
        });
    }

    public DocState BeginPropsEdit() => DocState.Capture(Document);

    public void EndPropsEdit(DocState before, string name)
    {
        var after = DocState.Capture(Document);
        History.Push(new StructureAction(name, before, after));
        NotifyStructure();
    }

    public void SetLayerProps(Layer layer, LayerProps props, string name)
    {
        Structure(name, () => layer.SetProps(props));
    }

    public void SetPaper(bool visible, SKColor color)
    {
        Structure("Papier", () =>
        {
            Document.PaperVisible = visible;
            Document.PaperColor = color;
        });
    }

    // ---------------------------------------------------------------- pixels

    void PixelEdit(string name, Layer layer, SKRectI rect, Action<SKCanvas> draw)
    {
        rect = BitmapUtil.Clamp(rect, Width, Height);
        if (rect.IsEmpty) return;
        var before = BitmapUtil.CopyRegion(layer.Bitmap, rect);
        using (var c = new SKCanvas(layer.Bitmap)) draw(c);
        var after = BitmapUtil.CopyRegion(layer.Bitmap, rect);
        layer.Touch();
        History.Push(new PixelAction(name, layer, rect, before, after));
        Invalidate(rect);
        NotifyLayerPixels(layer);
    }

    public void ClearLayer()
    {
        var layer = ActiveLayer;
        PixelEdit(Selection.HasSelection ? "Effacer la sélection" : "Effacer le calque", layer, Selection.Bounds, c =>
        {
            Selection.ClipCanvas(c);
            c.Clear(SKColors.Transparent);
        });
    }

    /// <summary>Remplit la sélection (ou le calque) d'une couleur, ou d'une trame si <paramref name="tone"/> est fourni.</summary>
    public void FillSelection(SKColor color, SKShader? tone = null)
    {
        var layer = ActiveLayer;
        PixelEdit(tone == null ? "Remplir" : "Trame", layer, Selection.Bounds, c =>
        {
            Selection.ClipCanvas(c);
            using var p = new SKPaint { Color = color, Shader = tone, BlendMode = layer.AlphaLock ? SKBlendMode.SrcATop : SKBlendMode.SrcOver };
            c.DrawPaint(p);
        });
    }

    static void Flip(SKCanvas c, SKBitmap src, int w, int h, bool horizontal)
    {
        using var copy = BitmapUtil.Copy(src);
        c.Clear(SKColors.Transparent);
        if (horizontal) c.Scale(-1, 1, w / 2f, 0);
        else c.Scale(1, -1, 0, h / 2f);
        c.DrawBitmap(copy, 0, 0);
    }

    public void FlipLayer(bool horizontal)
    {
        var layer = ActiveLayer;
        PixelEdit(horizontal ? "Retourner horizontalement" : "Retourner verticalement", layer, Document.Bounds,
            c => Flip(c, layer.Bitmap, Width, Height, horizontal));
    }

    public void FlipCanvas(bool horizontal)
    {
        var actions = new List<IHistoryAction>();
        foreach (var layer in Document.Layers)
        {
            var before = BitmapUtil.Copy(layer.Bitmap);
            using (var c = new SKCanvas(layer.Bitmap)) Flip(c, layer.Bitmap, Width, Height, horizontal);
            var after = BitmapUtil.Copy(layer.Bitmap);
            layer.Touch();
            actions.Add(new PixelAction("Retourner", layer, Document.Bounds, before, after));
            NotifyLayerPixels(layer);
        }
        History.Push(new CompositeAction("Retourner la toile", actions));
        Invalidate(Document.Bounds);
    }

    /// <summary>Ajoute une image comme nouveau calque, centrée et réduite si nécessaire.</summary>
    public Layer ImportImage(SKBitmap image, string name)
    {
        var layer = new Layer(Width, Height, name) { Folder = ActiveLayer.Folder };
        float scale = MathF.Min(1f, MathF.Min(Width / (float)image.Width, Height / (float)image.Height));
        float w = image.Width * scale, h = image.Height * scale;
        using (var c = new SKCanvas(layer.Bitmap))
        using (var img = SKImage.FromBitmap(image))
        using (var p = new SKPaint())
        {
            c.DrawImage(img, SKRect.Create((Width - w) / 2f, (Height - h) / 2f, w, h),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
        }
        return InsertLayer(layer, "Importer une image");
    }

    public void Fill(int x, int y, SKColor color, int tolerance, int expand, bool sampleAll, float opacity, SKShader? tone = null)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var layer = ActiveLayer;
        SKBitmap? flat = sampleAll ? Compositor.Flatten(Document, includePaper: true) : null;
        byte[]? mask;
        SKRectI bounds;
        try
        {
            mask = FloodFill.ComputeMask(flat ?? layer.Bitmap, x, y, tolerance, expand, out bounds);
        }
        finally
        {
            flat?.Dispose();
        }
        if (mask == null || bounds.IsEmpty) return;
        using var alpha = FloodFill.ToAlphaBitmap(mask, Width, bounds);
        PixelEdit("Remplissage", layer, bounds, c =>
        {
            Selection.ClipCanvas(c);
            using var p = new SKPaint
            {
                Color = color.WithAlpha((byte)Math.Clamp(opacity * 255f, 0, 255)),
                Shader = tone,
                BlendMode = layer.AlphaLock ? SKBlendMode.SrcATop : SKBlendMode.SrcOver,
            };
            c.DrawBitmap(alpha, bounds.Left, bounds.Top, p);
        });
    }

    public SKColor? PickColor(int x, int y, bool sampleAll)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        SKColor col;
        if (sampleAll)
        {
            using var bmp = new SKBitmap(BitmapUtil.Info(1, 1));
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                c.Translate(-x, -y);
                if (Document.PaperVisible) c.DrawColor(Document.PaperColor, SKBlendMode.Src);
                Compositor.DrawLayers(c, Document.Layers, null);
            }
            col = bmp.GetPixel(0, 0);
        }
        else
        {
            col = ActiveLayer.Bitmap.GetPixel(x, y);
        }
        if (col.Alpha < 8) return null;
        return col.WithAlpha(255);
    }

    public SKBitmap RenderThumbnail(int maxSize)
    {
        using var flat = Compositor.Flatten(Document, includePaper: true);
        float s = MathF.Min(1f, maxSize / (float)Math.Max(Width, Height));
        return BitmapUtil.Scaled(flat, Math.Max(1, (int)(Width * s)), Math.Max(1, (int)(Height * s)));
    }

    public void Dispose()
    {
        History.Dispose();
        Document.Dispose();
        Composite.Dispose();
        StrokeBuffer.Dispose();
        Selection.Dispose();
    }
}
