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

    /// <summary>Range les calques donnés dans un nouveau dossier, à la place du plus haut d'entre eux.</summary>
    public LayerFolder? GroupLayers(IReadOnlyCollection<Layer> selection, string? name = null)
    {
        var layers = Document.Layers;
        var sel = layers.Where(selection.Contains).ToList();
        if (sel.Count == 0) return null;
        var anchor = sel[^1];
        var active = ActiveLayer;
        var folder = new LayerFolder(name ?? Document.NextFolderName()) { Parent = anchor.Folder };
        Structure("Grouper dans un dossier", () =>
        {
            foreach (var l in sel)
                if (l != anchor) layers.Remove(l);
            int at = layers.IndexOf(anchor);
            layers.InsertRange(at, sel.Where(l => l != anchor));
            foreach (var l in sel) l.Folder = folder;
            Document.ActiveIndex = layers.IndexOf(active);
        });
        return folder;
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
            List<Layer> block;
            if (layer != null)
            {
                block = new List<Layer> { layer };
            }
            else
            {
                var (s, e) = Document.FolderRange(folder!);
                block = layers.GetRange(s, e - s);
            }
            foreach (var l in block) layers.Remove(l);

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

    /// <summary>Fusionne des calques en un seul, à la place du plus haut.</summary>
    public void MergeLayers(IReadOnlyCollection<Layer> selection, string actionName = "Fusionner les calques")
    {
        var sel = Document.Layers.Where(selection.Contains).ToList();
        if (sel.Count < 2) return;
        var top = sel[^1];
        var merged = new Layer(Width, Height, top.Name) { Folder = top.Folder };
        // Composition de la seule sélection, sans tenir compte des dossiers (comme une fusion Photoshop).
        var saved = sel.Select(l => l.Folder).ToList();
        try
        {
            foreach (var l in sel) l.Folder = null;
            using var c = new SKCanvas(merged.Bitmap);
            Compositor.DrawLayers(c, sel, null);
        }
        finally
        {
            for (int i = 0; i < sel.Count; i++) sel[i].Folder = saved[i];
        }
        Structure(actionName, () =>
        {
            int at = Document.Layers.IndexOf(top);
            Document.Layers[at] = merged;
            foreach (var l in sel)
                if (l != top) Document.Layers.Remove(l);
            Document.ActiveIndex = Document.Layers.IndexOf(merged);
        });
    }

    /// <summary>Fusionne tous les calques visibles (les calques masqués sont gardés).</summary>
    public void MergeVisible()
    {
        var visible = Document.Layers.Where(l => l.IsEffectivelyVisible).ToList();
        if (visible.Count < 2) return;
        var merged = new Layer(Compositor.Flatten(Document, includePaper: false), "Calques fusionnés");
        Structure("Fusionner les calques visibles", () =>
        {
            int at = Document.Layers.IndexOf(visible[^1]);
            Document.Layers[at] = merged;
            foreach (var l in visible.Take(visible.Count - 1)) Document.Layers.Remove(l);
            Document.ActiveIndex = Document.Layers.IndexOf(merged);
        });
    }

    // ================================================================ animation

    /// <summary>Image courante (dossier de premier niveau du calque actif) en mode animation.</summary>
    public LayerFolder? CurrentFrame
    {
        get
        {
            if (!Document.AnimationEnabled) return null;
            return Document.TopFolder(ActiveLayer) ?? Document.Frames().FirstOrDefault();
        }
    }

    public FrameFilter? CurrentFrameFilter()
    {
        var cur = CurrentFrame;
        if (cur == null) return null;
        var frames = Document.Frames();
        int i = frames.IndexOf(cur);
        bool onion = Document.OnionSkin;
        return new FrameFilter
        {
            Frame = cur,
            OnionPrev = onion && i > 0 ? frames[i - 1] : null,
            OnionNext = onion && i >= 0 && i < frames.Count - 1 ? frames[i + 1] : null,
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
        if (CurrentFrame == frame) return;
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
        SKBitmap? flat = sampleAll ? Compositor.Flatten(Document, includePaper: true, CurrentFrameFilter()) : null;
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
