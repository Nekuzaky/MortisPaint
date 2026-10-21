using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum DropPosition { Above, Below, Into }

public enum SelectionOp { Replace, Add, Subtract }

/// <summary>Dossiers, opérations sur plusieurs calques, animation et sélections avancées.</summary>
public sealed partial class PaintEngine
{
    public Ruler Ruler { get; } = new();

    // ================================================================ dossiers

    /// <summary>
    /// Range les calques donnés dans un nouveau dossier, créé dans leur dossier commun, à la place du plus haut.
    /// Un sous-dossier entièrement sélectionné y passe tel quel (il n'est pas dissous).
    /// </summary>
    public LayerFolder? GroupLayers(IReadOnlyCollection<Layer> selection, string? name = null)
    {
        var layers = Document.Layers;
        var sel = layers.Where(selection.Contains).ToList();
        if (sel.Count == 0) return null;
        var set = new HashSet<Layer>(sel);
        var parent = CommonFolder(sel);
        var active = ActiveLayer;
        var folder = new LayerFolder(name ?? Document.NextFolderName()) { Parent = parent };
        var covered = new Dictionary<LayerFolder, bool>();
        bool Covered(LayerFolder f)
        {
            if (covered.TryGetValue(f, out var c)) return c;
            var (s, e) = Document.FolderRange(f);
            for (int k = s; k < e; k++)
                if (!set.Contains(layers[k])) return covered[f] = false;
            return covered[f] = true;
        }
        var direct = new List<Layer>();
        var moved = new HashSet<LayerFolder>();
        foreach (var l in sel)
        {
            // Le plus haut sous-dossier entièrement sélectionné qui contient ce calque.
            LayerFolder? keep = null;
            for (var f = l.Folder; f != null && f != parent; f = f.Parent)
                if (Covered(f)) keep = f;
            if (keep != null) moved.Add(keep);
            else direct.Add(l);
        }
        int insertAt = LevelSlotAbove(sel[^1], parent) - sel.Count;
        Structure("Grouper dans un dossier", () =>
        {
            foreach (var l in sel) layers.Remove(l);
            layers.InsertRange(insertAt, sel);
            foreach (var l in direct) l.Folder = folder;
            foreach (var f in moved) f.Parent = folder;
            Document.ActiveIndex = layers.IndexOf(active);
        });
        return folder;
    }

    /// <summary>Dossier le plus profond qui contient tous les calques donnés (null = racine).</summary>
    static LayerFolder? CommonFolder(IEnumerable<Layer> layers)
    {
        bool first = true;
        LayerFolder? common = null;
        foreach (var l in layers)
        {
            if (first) { common = l.Folder; first = false; continue; }
            while (common != null && !l.IsIn(common)) common = common.Parent;
        }
        return common;
    }

    /// <summary>
    /// Index juste au-dessus de l'unité de <paramref name="level"/> qui contient <paramref name="layer"/>
    /// (le calque lui-même, ou le sous-dossier direct de <paramref name="level"/> qui le contient).
    /// Insérer là ne coupe aucun dossier.
    /// </summary>
    int LevelSlotAbove(Layer layer, LayerFolder? level)
    {
        if (layer.Folder == level) return Document.Layers.IndexOf(layer) + 1;
        var child = layer.Folder!;
        while (child.Parent != level) child = child.Parent!;
        return Document.FolderRange(child).End;
    }

    /// <summary>Supprime le dossier mais garde son contenu, remonté d'un niveau.</summary>
    public void Ungroup(LayerFolder folder)
    {
        Structure("Dégrouper", () =>
        {
            foreach (var l in Document.Layers)
                if (l.Folder == folder) l.Folder = folder.Parent;
            foreach (var f in Document.AllFolders())
                if (f.Parent == folder) f.Parent = folder.Parent;
        });
    }

    public void DeleteFolder(LayerFolder folder)
    {
        var (start, end) = Document.FolderRange(folder);
        if (end <= start) return;
        Structure("Supprimer le dossier", () =>
        {
            Document.Layers.RemoveRange(start, end - start);
            if (Document.Layers.Count == 0)
                Document.Layers.Add(new Layer(Width, Height, Document.NextLayerName()));
            Document.ActiveIndex = Math.Min(start, Document.Layers.Count - 1);
        });
    }

    public void SetFolderProps(LayerFolder folder, FolderProps props, string name) =>
        Structure(name, () => folder.SetProps(props));

    /// <summary>Copie un dossier (et ses sous-dossiers) juste au-dessus de lui.</summary>
    public LayerFolder? DuplicateFolder(LayerFolder folder, string? name = null)
    {
        var (start, end) = Document.FolderRange(folder);
        if (end <= start) return null;
        var map = new Dictionary<LayerFolder, LayerFolder>();
        LayerFolder Map(LayerFolder f)
        {
            if (f == folder.Parent) return f;
            if (map.TryGetValue(f, out var m)) return m;
            var copy = new LayerFolder(f == folder ? name ?? f.Name + " (copie)" : f.Name)
            {
                Visible = f.Visible, Opacity = f.Opacity, BlendMode = f.BlendMode, Expanded = f.Expanded,
                PassThrough = f.PassThrough,
            };
            map[f] = copy;
            copy.Parent = f.Parent == null || f == folder ? folder.Parent : Map(f.Parent);
            return copy;
        }
        var clones = new List<Layer>();
        for (int i = start; i < end; i++)
        {
            var src = Document.Layers[i];
            var c = src.Clone();
            c.Folder = src.Folder == null ? null : Map(src.Folder);
            clones.Add(c);
        }
        Structure("Dupliquer le dossier", () =>
        {
            Document.Layers.InsertRange(end, clones);
            Document.ActiveIndex = end + clones.Count - 1;
        });
        return map[folder];
    }

    /// <summary>Déplace un calque ou un dossier (glisser-déposer) par rapport à une cible.</summary>
    public void MoveNode(Layer? layer, LayerFolder? folder, Layer? targetLayer, LayerFolder? targetFolder, DropPosition pos)
    {
        var layers = Document.Layers;
        if (layer == null && folder == null) return;
        if (targetLayer == null && targetFolder == null) return;
        if (folder != null)
        {
            // On ne range pas un dossier dans lui-même.
            if (targetLayer != null && targetLayer.IsIn(folder)) return;
            if (targetFolder != null && targetFolder.IsWithin(folder)) return;
        }
        if (layer != null && layer == targetLayer) return;

        var active = ActiveLayer;
        Structure("Déplacer", () =>
        {
            int bs, count;
            if (layer != null)
            {
                bs = layers.IndexOf(layer);
                count = 1;
            }
            else
            {
                var (s, e) = Document.FolderRange(folder!);
                bs = s;
                count = e - s;
            }
            if (bs < 0 || count <= 0) return;
            var block = layers.GetRange(bs, count);

            // Position calculée avant le retrait du bloc : la cible peut ne contenir que lui
            // (sa plage serait alors vide et l'insertion se ferait tout en bas).
            int insertAt;
            LayerFolder? parent;
            if (targetLayer != null)
            {
                int t = layers.IndexOf(targetLayer);
                parent = targetLayer.Folder;
                insertAt = pos == DropPosition.Below ? t : t + 1;
            }
            else
            {
                var (s, e) = Document.FolderRange(targetFolder!);
                switch (pos)
                {
                    case DropPosition.Into:
                        parent = targetFolder;
                        insertAt = e;
                        break;
                    case DropPosition.Below:
                        parent = targetFolder!.Parent;
                        insertAt = s;
                        break;
                    default:
                        parent = targetFolder!.Parent;
                        insertAt = e;
                        break;
                }
            }
            // Puis ramenée aux index d'après le retrait.
            if (insertAt >= bs + count) insertAt -= count;
            else if (insertAt > bs) insertAt = bs;
            layers.RemoveRange(bs, count);
            layers.InsertRange(Math.Clamp(insertAt, 0, layers.Count), block);
            if (layer != null) layer.Folder = parent;
            else folder!.Parent = parent;
            Document.ActiveIndex = layers.IndexOf(active);
        });
    }

    // ================================================================ plusieurs calques

    public void DeleteLayers(IReadOnlyCollection<Layer> selection)
    {
        var sel = Document.Layers.Where(selection.Contains).ToList();
        if (sel.Count == 0) return;
        Structure("Supprimer les calques", () =>
        {
            int first = Document.Layers.IndexOf(sel[0]);
            foreach (var l in sel) Document.Layers.Remove(l);
            if (Document.Layers.Count == 0)
                Document.Layers.Add(new Layer(Width, Height, Document.NextLayerName()));
            Document.ActiveIndex = Math.Clamp(first - 1, 0, Document.Layers.Count - 1);
        });
    }

    /// <summary>
    /// Fusionne des calques en un seul, dans leur dossier commun, à la place du plus haut.
    /// En animation, seuls ceux de l'image du plus haut sont fusionnés.
    /// </summary>
    public void MergeLayers(IReadOnlyCollection<Layer> selection, string actionName = "Fusionner les calques")
    {
        var sel = Document.Layers.Where(selection.Contains).ToList();
        if (sel.Count == 0) return;
        if (Document.AnimationEnabled)
        {
            // On ne colle pas deux images (ou une image et les fonds) ensemble.
            var frame = Document.TopFolder(sel[^1]);
            sel = sel.Where(l => Document.TopFolder(l) == frame).ToList();
        }
        if (sel.Count < 2) return;
        MergeInto(sel, sel[^1].Name, actionName);
    }

    /// <summary>
    /// Fusionne tous les calques affichés (les calques masqués sont gardés).
    /// En animation, seulement ceux de l'image courante : les fonds et les autres images restent.
    /// </summary>
    public void MergeVisible()
    {
        var frame = CurrentFrame;
        var visible = new List<Layer>();
        for (int i = 0; i < Document.Layers.Count; i++)
            if (Document.Layers[i].IsIn(frame) && IsDisplayed(i)) visible.Add(Document.Layers[i]);
        if (visible.Count < 2) return;
        MergeInto(visible, "Calques fusionnés", "Fusionner les calques visibles");
    }

    /// <summary>
    /// Remplace les calques donnés (de bas en haut) par un seul, placé dans leur dossier commun le plus profond,
    /// juste au-dessus de l'unité qui contient le plus haut : aucun dossier n'est coupé. La composition respecte
    /// les sous-dossiers (masqués, mode, opacité, pass-through) et l'écrêtage ; le dossier commun s'applique encore
    /// au résultat. Des calques tous écrêtés sur une même base non fusionnée donnent un calque écrêté.
    /// </summary>
    void MergeInto(List<Layer> sel, string layerName, string actionName)
    {
        var layers = Document.Layers;
        var set = new HashSet<Layer>(sel);
        var container = CommonFolder(sel);
        var baseUnit = ClipBase(layers.IndexOf(sel[0]));
        bool clipped = sel.All(l => l.Clipping)
            && (baseUnit.Layer != null || baseUnit.Folder != null)
            && !(baseUnit.Layer != null && set.Contains(baseUnit.Layer))
            && sel.All(l => ClipBase(layers.IndexOf(l)) == baseUnit);
        var merged = new Layer(Width, Height, layerName) { Folder = container, Clipping = clipped };
        using (var c = new SKCanvas(merged.Bitmap))
        {
            if (clipped)
            {
                // Mêmes base et niveau : on les compose entre eux, l'écrêtage du résultat fera le reste.
                foreach (var l in sel)
                {
                    if (!l.Visible || l.Opacity <= 0f) continue;
                    using var p = LayerPaint(l);
                    c.DrawBitmap(l.Bitmap, 0, 0, p);
                }
            }
            else
            {
                Compositor.DrawSubset(c, layers, container, set.Contains);
            }
        }
        int insertAt = LevelSlotAbove(sel[^1], container) - sel.Count;
        Structure(actionName, () =>
        {
            foreach (var l in sel) layers.Remove(l);
            layers.Insert(insertAt, merged);
            Document.ActiveIndex = insertAt;
        });
    }

    /// <summary>
    /// Base d'écrêtage du calque d'index donné, comme dans le compositeur : l'unité du même niveau juste sous
    /// les calques écrêtés (calque ou sous-dossier), ou le plus bas de ces calques en bas du niveau.
    /// (null, null) pour un calque non écrêté ou sans base.
    /// </summary>
    (Layer? Layer, LayerFolder? Folder) ClipBase(int index)
    {
        var layers = Document.Layers;
        if (index < 0 || !layers[index].Clipping) return (null, null);
        var level = layers[index].Folder;
        Layer? lowest = null;
        for (int j = index - 1; j >= 0; j--)
        {
            var b = layers[j];
            if (b.Folder == level)
            {
                if (!b.Clipping) return (b, null);
                lowest = b;
                continue;
            }
            if (!b.IsIn(level)) break;
            var child = b.Folder!;
            while (child.Parent != level) child = child.Parent!;
            return (null, child);
        }
        return (lowest, null);
    }

    /// <summary>Vrai si le calque d'index donné apparaît dans l'image (lui, ses dossiers et sa base d'écrêtage visibles).</summary>
    bool IsDisplayed(int index)
    {
        var l = Document.Layers[index];
        if (!l.Visible || l.Opacity <= 0f) return false;
        for (var f = l.Folder; f != null; f = f.Parent)
            if (!f.Visible || f.Opacity <= 0f) return false;
        var (bl, bf) = ClipBase(index);
        if (bl != null) return bl.Visible && bl.Opacity > 0f;
        if (bf != null) return bf.Visible && bf.Opacity > 0f;
        return true;
    }

    // ================================================================ animation

    LayerFolder? _lastFrame;

    /// <summary>
    /// Image courante (dossier de premier niveau du calque actif) en mode animation.
    /// Sur un calque de fond, c'est la dernière image affichée (à défaut la première).
    /// </summary>
    public LayerFolder? CurrentFrame
    {
        get
        {
            if (!Document.AnimationEnabled) return null;
            var top = Document.TopFolder(ActiveLayer);
            if (top != null) return _lastFrame = top;
            var frames = Document.Frames();
            return _lastFrame != null && frames.Contains(_lastFrame) ? _lastFrame : frames.FirstOrDefault();
        }
    }

    public FrameFilter? CurrentFrameFilter() => CurrentFrameFilter(Document.OnionSkin);

    /// <summary>
    /// Filtre de l'image courante (null hors animation). Sans pelures d'oignon pour échantillonner
    /// la toile (pot, pipette, baguette) ou pour une capture.
    /// </summary>
    public FrameFilter? CurrentFrameFilter(bool withOnion)
    {
        var cur = CurrentFrame;
        if (cur == null) return null;
        if (!withOnion) return new FrameFilter { Frame = cur };
        var frames = Document.Frames();
        int i = frames.IndexOf(cur);
        return new FrameFilter
        {
            Frame = cur,
            OnionPrev = i > 0 ? frames[i - 1] : null,
            OnionNext = i >= 0 && i < frames.Count - 1 ? frames[i + 1] : null,
            OnionOpacity = Document.OnionOpacity,
        };
    }

    /// <summary>Active le mode animation ; crée une première image si le document n'a pas encore de dossier.</summary>
    public void SetAnimation(bool enabled)
    {
        if (enabled && Document.Frames().Count == 0)
            GroupLayers(new[] { ActiveLayer }, "Image 1");
        Document.AnimationEnabled = enabled;
        Document.IsDirty = true;
        NotifyStructure();
    }

    public void GoToFrame(LayerFolder frame)
    {
        var (start, end) = Document.FolderRange(frame);
        if (end <= start) return;
        // On compare l'image du calque actif, pas CurrentFrame : sur un calque de fond, celle-ci n'est qu'un repli.
        if (Document.TopFolder(ActiveLayer) == frame) return;
        Document.ActiveIndex = end - 1;
        NotifyStructure();
    }

    public void AddFrame()
    {
        var frames = Document.Frames();
        var cur = CurrentFrame;
        int insertAt = cur != null ? Document.FolderRange(cur).End : Document.Layers.Count;
        var folder = new LayerFolder(Document.NextFolderName("Image"));
        var layer = new Layer(Width, Height, "Dessin") { Folder = folder };
        Structure("Nouvelle image", () =>
        {
            Document.Layers.Insert(insertAt, layer);
            Document.ActiveIndex = insertAt;
        });
        _ = frames;
    }

    public void DuplicateFrame()
    {
        var cur = CurrentFrame;
        if (cur != null) DuplicateFolder(cur, Document.NextFolderName("Image"));
    }

    public void DeleteFrame()
    {
        var cur = CurrentFrame;
        if (cur == null || Document.Frames().Count <= 1) return;
        DeleteFolder(cur);
    }

    public void MoveFrame(int delta)
    {
        var cur = CurrentFrame;
        if (cur == null) return;
        var frames = Document.Frames();
        int i = frames.IndexOf(cur), j = i + delta;
        if (j < 0 || j >= frames.Count) return;
        MoveNode(null, cur, null, frames[j], delta > 0 ? DropPosition.Above : DropPosition.Below);
    }

    /// <summary>Rendu de chaque image (sans pelures d'oignon), pour l'export GIF.</summary>
    public List<SKBitmap> RenderFrames(int maxSide)
    {
        var list = new List<SKBitmap>();
        foreach (var f in Document.Frames())
            list.Add(Compositor.RenderScaled(Document, maxSide, includePaper: true, new FrameFilter { Frame = f }));
        return list;
    }

    // ================================================================ sélections avancées

    /// <summary>Baguette magique : sélectionne la zone de couleur proche autour du point.</summary>
    public bool SelectByColor(int x, int y, int tolerance, bool sampleAll, SelectionOp op)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        // « Toile » en animation : l'image courante seulement, sans pelures d'oignon.
        SKBitmap? flat = sampleAll ? Compositor.Flatten(Document, includePaper: true, CurrentFrameFilter(withOnion: false)) : null;
        try
        {
            var mask = FloodFill.ComputeMask(flat ?? ActiveLayer.Bitmap, x, y, tolerance, 0, out var bounds);
            if (mask == null || bounds.IsEmpty) return false;
            var path = Selection.MaskToPath(mask, Width, bounds);
            Selection.Combine(path, op);
            return true;
        }
        finally
        {
            flat?.Dispose();
        }
    }

    /// <summary>Sélectionne les pixels non transparents du calque actif.</summary>
    public bool SelectLayerContent(SelectionOp op)
    {
        var bmp = ActiveLayer.Bitmap;
        var bounds = BitmapUtil.AlphaBounds(bmp);
        if (bounds.IsEmpty) return false;
        var mask = new byte[Width * Height];
        unsafe
        {
            byte* p = (byte*)bmp.GetPixels();
            int row = bmp.RowBytes;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                    if (p[y * row + x * 4 + 3] >= 16) mask[y * Width + x] = 255;
        }
        Selection.Combine(Selection.MaskToPath(mask, Width, bounds), op);
        return true;
    }

    /// <summary>Dessine du texte dans un nouveau calque au-dessus du calque actif.</summary>
    public Layer? AddText(TextStyle style, SKPoint origin)
    {
        if (string.IsNullOrWhiteSpace(style.Text)) return null;
        var first = style.Text.Split('\n')[0].Trim();
        if (first.Length > 24) first = first[..24] + "…";
        var layer = new Layer(Width, Height, "Texte : " + first) { Folder = ActiveLayer.Folder };
        using (var c = new SKCanvas(layer.Bitmap)) TextRenderer.Draw(c, style, origin);
        return InsertLayer(layer, "Texte");
    }
}
