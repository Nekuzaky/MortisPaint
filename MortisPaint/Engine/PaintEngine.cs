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
        var frame = CurrentFrame;
        Document.ActiveIndex = index;
        // En animation, changer d'image change ce qui est affiché (image et pelures d'oignon).
        if (CurrentFrame != frame) Invalidate(Document.Bounds);
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

    /// <summary>
    /// Vrai si le calque actif peut être fusionné avec celui du dessous : il faut un voisin du même dossier
    /// (sinon on mélangerait deux dossiers, ou deux images en animation).
    /// </summary>
    public bool CanMergeDown
    {
        get
        {
            int i = Document.ActiveIndex;
            return i > 0 && i < Document.Layers.Count && Document.Layers[i - 1].Folder == Document.Layers[i].Folder;
        }
    }

    public void MergeDown()
    {
        if (!CanMergeDown) return;
        int i = Document.ActiveIndex;
        var upper = Document.Layers[i];
        var lower = Document.Layers[i - 1];
        // Seule la zone non transparente du calque du haut change le calque du bas.
        var rect = upper.Visible && upper.Opacity > 0f ? BitmapUtil.AlphaBounds(upper.Bitmap) : SKRectI.Empty;
        var pixels = new List<PixelAction>();
        if (!rect.IsEmpty)
        {
            var before = BitmapUtil.CopyRegion(lower.Bitmap, rect);
            using (var c = new SKCanvas(lower.Bitmap))
            using (var p = LayerPaint(upper))
            {
                if (upper.Clipping && !lower.Clipping)
                {
                    // Écrêté sur le calque du bas : comme le compositeur, on masque d'abord par l'alpha
                    // de la base, puis on fusionne avec le mode et l'opacité du calque.
                    using var masked = BitmapUtil.Copy(upper.Bitmap);
                    using (var mc = new SKCanvas(masked))
                    using (var mp = new SKPaint { BlendMode = SKBlendMode.DstIn })
                        mc.DrawBitmap(lower.Bitmap, 0, 0, mp);
                    c.DrawBitmap(masked, 0, 0, p);
                }
                else
                {
                    // Calque non écrêté, ou deux calques écrêtés sur la même base (le résultat le reste).
                    c.DrawBitmap(upper.Bitmap, 0, 0, p);
                }
            }
            var after = BitmapUtil.CopyRegion(lower.Bitmap, rect);
            lower.Touch();
            pixels.Add(new PixelAction("Fusion", lower, rect, before, after));
        }
        // Les pixels sont déjà modifiés : l'action structurelle enregistre l'état avant/après.
        var stBefore = DocState.Capture(Document);
        Document.Layers.RemoveAt(i);
        Document.ActiveIndex = i - 1;
        var stAfter = DocState.Capture(Document);
        History.Push(new StructureAction("Fusionner vers le bas", stBefore, stAfter, pixels));
        NotifyStructure();
        NotifyLayerPixels(lower);
    }

    /// <summary>Peinture d'un calque (mode et opacité), arrondie comme dans le compositeur.</summary>
    static SKPaint LayerPaint(Layer l) => new()
    {
        BlendMode = l.BlendMode.ToSkia(),
        Color = SKColors.White.WithAlpha((byte)(Math.Clamp(l.Opacity, 0f, 1f) * 255f + 0.5f)),
    };

    /// <summary>
    /// Aplatit tous les calques visibles en un seul. En animation, seule l'image courante est aplatie,
    /// dans son dossier : les autres images et les calques de fond restent.
    /// </summary>
    public void FlattenAll()
    {
        var frame = CurrentFrame;
        if (frame != null)
        {
            var (s, e) = Document.FolderRange(frame);
            if (e <= s) return;
            var layer = new Layer(Width, Height, "Image aplatie") { Folder = frame };
            // Intérieur de l'image, sans le mode ni l'opacité du dossier (qui s'appliquent encore au résultat).
            using (var c = new SKCanvas(layer.Bitmap)) Compositor.DrawSubset(c, Document.Layers, frame, null);
            Structure("Aplatir l'image", () =>
            {
                Document.Layers.RemoveRange(s, e - s);
                Document.Layers.Insert(s, layer);
                Document.ActiveIndex = s;
            });
            return;
        }
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

    /// <summary>Retourne un bitmap sur place : permutation exacte des pixels (4 octets, quel que soit l'ordre des canaux).</summary>
    static void Flip(SKBitmap bmp, bool horizontal)
    {
        int w = bmp.Width, h = bmp.Height, row = bmp.RowBytes, len = w * 4;
        unsafe
        {
            byte* p = (byte*)bmp.GetPixels();
            if (horizontal)
            {
                for (int y = 0; y < h; y++)
                {
                    uint* line = (uint*)(p + y * row);
                    for (int a = 0, b = w - 1; a < b; a++, b--)
                        (line[a], line[b]) = (line[b], line[a]);
                }
                return;
            }
            var tmp = new byte[len];
            fixed (byte* t = tmp)
            {
                for (int a = 0, b = h - 1; a < b; a++, b--)
                {
                    Buffer.MemoryCopy(p + a * row, t, len, len);
                    Buffer.MemoryCopy(p + b * row, p + a * row, len, len);
                    Buffer.MemoryCopy(t, p + b * row, len, len);
                }
            }
        }
    }

    /// <summary>Applique une opération réversible à des calques entiers ; l'historique rejouera l'inverse.</summary>
    void Reversible(string name, List<Layer> layers, Action<Layer> redo, Action<Layer> undo)
    {
        var action = new ReversibleAction(name, layers, redo, undo);
        action.Redo(this);
        History.Push(action);
    }

    public void FlipLayer(bool horizontal)
    {
        Action<Layer> flip = l => Flip(l.Bitmap, horizontal);
        Reversible(horizontal ? "Retourner horizontalement" : "Retourner verticalement", new List<Layer> { ActiveLayer }, flip, flip);
    }

    public void FlipCanvas(bool horizontal)
    {
        Action<Layer> flip = l => Flip(l.Bitmap, horizontal);
        Reversible("Retourner la toile", new List<Layer>(Document.Layers), flip, flip);
    }

    /// <summary>
    /// Pivote la toile d'un demi-tour sur place (la taille ne change pas) : contrairement à
    /// <see cref="CanvasOps.Rotate"/>, l'historique est conservé et l'opération s'annule.
    /// </summary>
    public void RotateCanvas180()
    {
        Action<Layer> turn = l =>
        {
            Flip(l.Bitmap, true);
            Flip(l.Bitmap, false);
        };
        Reversible("Pivoter de 180°", new List<Layer>(Document.Layers), turn, turn);
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
        // « Toile » en animation : l'image courante seulement, sans pelures d'oignon.
        SKBitmap? flat = sampleAll ? Compositor.Flatten(Document, includePaper: true, CurrentFrameFilter(withOnion: false)) : null;
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
                Compositor.DrawLayers(c, Document.Layers, null, CurrentFrameFilter(withOnion: false));
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
        // En animation, la miniature montre la première image (et les fonds), pas toutes les images superposées.
        var frames = Document.AnimationEnabled ? Document.Frames() : null;
        var filter = frames is { Count: > 0 } ? new FrameFilter { Frame = frames[0] } : null;
        using var flat = Compositor.Flatten(Document, includePaper: true, filter);
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
