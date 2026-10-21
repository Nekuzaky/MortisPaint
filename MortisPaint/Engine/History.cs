using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace MortisPaint.Engine;

public interface IHistoryAction : IDisposable
{
    string Name { get; }
    long Bytes { get; }
    void Undo(PaintEngine e);
    void Redo(PaintEngine e);
    /// <summary>Calques référencés (pour libérer ceux qui ne sont plus utilisés).</summary>
    IEnumerable<Layer> ReferencedLayers { get; }
}

/// <summary>Modification de pixels dans un rectangle d'un calque (avant / après).</summary>
public sealed class PixelAction : IHistoryAction
{
    readonly Layer _layer;
    readonly SKRectI _rect;
    readonly SKBitmap _before, _after;

    public PixelAction(string name, Layer layer, SKRectI rect, SKBitmap before, SKBitmap after)
    {
        Name = name;
        _layer = layer;
        _rect = rect;
        _before = before;
        _after = after;
    }

    public string Name { get; }
    public long Bytes => _before.ByteCount + _after.ByteCount;
    public IEnumerable<Layer> ReferencedLayers { get { yield return _layer; } }

    public void Undo(PaintEngine e) => Apply(e, _before);
    public void Redo(PaintEngine e) => Apply(e, _after);

    void Apply(PaintEngine e, SKBitmap patch)
    {
        if (_layer.IsDisposed) return;
        BitmapUtil.Paste(_layer.Bitmap, patch, _rect.Left, _rect.Top);
        _layer.Touch();
        e.Invalidate(_rect);
        e.NotifyLayerPixels(_layer);
    }

    public void Dispose()
    {
        _before.Dispose();
        _after.Dispose();
    }
}

/// <summary>État de la pile de calques (ordre, propriétés, papier) — pour ajout, suppression, fusion, etc.</summary>
public sealed class DocState
{
    public List<Layer> Layers = new();
    public List<LayerProps> Props = new();
    public List<(LayerFolder Folder, FolderProps Props)> Folders = new();
    public int Active;
    public bool PaperVisible;
    public SKColor PaperColor;

    public static DocState Capture(Document d) => new()
    {
        Layers = d.Layers.ToList(),
        Props = d.Layers.Select(l => l.GetProps()).ToList(),
        Folders = d.AllFolders().Select(f => (f, f.GetProps())).ToList(),
        Active = d.ActiveIndex,
        PaperVisible = d.PaperVisible,
        PaperColor = d.PaperColor,
    };

    public void Apply(Document d)
    {
        d.Layers.Clear();
        d.Layers.AddRange(Layers);
        for (int i = 0; i < Layers.Count; i++) Layers[i].SetProps(Props[i]);
        foreach (var (f, p) in Folders) f.SetProps(p);
        d.ActiveIndex = Active;
        d.PaperVisible = PaperVisible;
        d.PaperColor = PaperColor;
    }
}

public sealed class StructureAction : IHistoryAction
{
    readonly DocState _before, _after;
    readonly List<PixelAction> _pixels;

    public StructureAction(string name, DocState before, DocState after, List<PixelAction>? pixels = null)
    {
        Name = name;
        _before = before;
        _after = after;
        _pixels = pixels ?? new List<PixelAction>();
    }

    public string Name { get; }
    public long Bytes => _pixels.Sum(p => p.Bytes) + 256;

    public IEnumerable<Layer> ReferencedLayers =>
        _before.Layers.Concat(_after.Layers).Concat(_pixels.SelectMany(p => p.ReferencedLayers));

    public void Undo(PaintEngine e)
    {
        for (int i = _pixels.Count - 1; i >= 0; i--) _pixels[i].Undo(e);
        _before.Apply(e.Document);
        e.NotifyStructure();
    }

    public void Redo(PaintEngine e)
    {
        _after.Apply(e.Document);
        foreach (var p in _pixels) p.Redo(e);
        e.NotifyStructure();
    }

    public void Dispose()
    {
        foreach (var p in _pixels) p.Dispose();
    }
}

public sealed class CompositeAction : IHistoryAction
{
    readonly List<IHistoryAction> _items;

    public CompositeAction(string name, List<IHistoryAction> items)
    {
        Name = name;
        _items = items;
    }

    public string Name { get; }
    public long Bytes => _items.Sum(i => i.Bytes);
    public IEnumerable<Layer> ReferencedLayers => _items.SelectMany(i => i.ReferencedLayers);

    public void Undo(PaintEngine e)
    {
        for (int i = _items.Count - 1; i >= 0; i--) _items[i].Undo(e);
    }

    public void Redo(PaintEngine e)
    {
        foreach (var i in _items) i.Redo(e);
    }

    public void Dispose()
    {
        foreach (var i in _items) i.Dispose();
    }
}

public sealed class History : IDisposable
{
    readonly List<IHistoryAction> _undo = new();
    readonly List<IHistoryAction> _redo = new();
    readonly PaintEngine _engine;

    public int MaxSteps { get; set; } = 60;
    /// <summary>Budget mémoire de l'historique (plus serré sur mobile).</summary>
    public long MaxBytes { get; set; } = (OperatingSystem.IsAndroid() ? 220L : 600L) * 1024 * 1024;

    public History(PaintEngine engine) => _engine = engine;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public event Action? Changed;

    public void Push(IHistoryAction action)
    {
        var dropped = new List<IHistoryAction>(_redo);
        _redo.Clear();
        _undo.Add(action);
        long total = _undo.Sum(a => a.Bytes);
        while (_undo.Count > 1 && (_undo.Count > MaxSteps || total > MaxBytes))
        {
            total -= _undo[0].Bytes;
            dropped.Add(_undo[0]);
            _undo.RemoveAt(0);
        }
        Release(dropped);
        _engine.Document.IsDirty = true;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var a = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        a.Undo(_engine);
        _redo.Add(a);
        _engine.Document.IsDirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var a = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        a.Redo(_engine);
        _undo.Add(a);
        _engine.Document.IsDirty = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Libère les actions abandonnées et les calques que plus personne ne référence.</summary>
    void Release(List<IHistoryAction> dropped)
    {
        if (dropped.Count == 0) return;
        var alive = new HashSet<Layer>(_engine.Document.Layers);
        foreach (var a in _undo.Concat(_redo))
            foreach (var l in a.ReferencedLayers) alive.Add(l);
        foreach (var a in dropped)
        {
            foreach (var l in a.ReferencedLayers)
                if (!alive.Contains(l)) l.Dispose();
            a.Dispose();
        }
    }

    public void Clear()
    {
        var all = _undo.Concat(_redo).ToList();
        _undo.Clear();
        _redo.Clear();
        Release(all);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        var inDoc = new HashSet<Layer>(_engine.Document.Layers);
        foreach (var a in _undo.Concat(_redo))
        {
            foreach (var l in a.ReferencedLayers)
                if (!inDoc.Contains(l)) l.Dispose();
            a.Dispose();
        }
        _undo.Clear();
        _redo.Clear();
    }
}
