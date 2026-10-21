using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Engine;

namespace MortisPaint.Views;

/// <summary>Panneau Calques : dossiers, glisser-déposer, sélection multiple, propriétés.</summary>
public partial class EditorView
{
    readonly Dictionary<Layer, (int Version, WriteableBitmap Bitmap)> _thumbs = new();
    readonly Dictionary<Layer, Image> _thumbImages = new();
    readonly HashSet<Layer> _dirtyThumbs = new();
    DispatcherTimer? _thumbTimer;
    DispatcherTimer? _opacityTimer;
    DocState? _opacityBefore;
    bool _syncingLayer;

    readonly HashSet<Layer> _multi = new();
    bool _multiMode;
    LayerFolder? _selectedFolder;
    readonly List<(Control Row, Layer? Layer, LayerFolder? Folder, int Depth)> _rows = new();
    ToggleButton? _multiButton;
    Button? _mergeButton;

    // Glisser-déposer
    Layer? _dragLayer;
    LayerFolder? _dragFolder;
    bool _dragging;
    Point _dragStart;
    (Layer? Layer, LayerFolder? Folder, DropPosition Pos)? _dropTarget;

    const double IndentStep = 14;

    Button LayerButton(Geometry icon, string tip, Action action)
    {
        var b = new Button { Classes = { "icon", "small" }, Content = Icons.Make(icon, 18), Margin = new Thickness(1) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            CommitPending();
            action();
        };
        LayerButtons.Children.Add(b);
        return b;
    }

    void BuildLayersPanel()
    {
        LayerButton(Icons.Plus, "Nouveau calque (Ctrl+Maj+N)", () =>
        {
            ClearLayerSelection();
            _engine.AddLayer();
        });
        LayerButton(Icons.FolderPlus, "Grouper dans un dossier (Ctrl+G)", GroupSelection);
        LayerButton(Icons.Copy, "Dupliquer (Ctrl+J)", DuplicateSelection);
        _mergeButton = LayerButton(Icons.MergeDown, "Fusionner (vers le bas, ou la sélection)", MergeSelection);
        LayerButton(Icons.Delete, "Supprimer", DeleteSelection);

        _multiButton = new ToggleButton { Classes = { "icon", "small" }, Content = Icons.Make(Icons.Check, 18), Margin = new Thickness(1) };
        ToolTip.SetTip(_multiButton, "Sélection multiple (ou Ctrl / Maj + clic)");
        _multiButton.Click += (_, _) =>
        {
            _multiMode = _multiButton.IsChecked == true;
            if (_multiMode && _multi.Count == 0) _multi.Add(_engine.ActiveLayer);
            if (!_multiMode) _multi.Clear();
            _selectedFolder = null;
            RebuildLayers();
        };
        LayerButtons.Children.Add(_multiButton);

        var more = new Button { Classes = { "icon", "small" }, Content = Icons.Make(Icons.More, 18), Margin = new Thickness(1) };
        var menu = new MenuFlyout();
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                CommitPending();
                a();
            };
            menu.Items.Add(mi);
        }
        Add("Monter le calque", () => _engine.MoveLayer(1));
        Add("Descendre le calque", () => _engine.MoveLayer(-1));
        Add("Dégrouper le dossier", () =>
        {
            var f = _selectedFolder ?? _engine.ActiveLayer.Folder;
            if (f == null) { ShowToast("Sélectionne un dossier"); return; }
            _selectedFolder = null;
            _engine.Ungroup(f);
        });
        Add("Fusionner les calques visibles", () => _engine.MergeVisible());
        Add("Effacer le calque", () => _engine.ClearLayer());
        Add("Remplir avec la couleur", () => _engine.FillSelection(_state.Color));
        Add("Remplir avec la trame", FillWithTone);
        Add("Sélectionner le contenu du calque", () => _engine.SelectLayerContent(_state.SelectOp));
        Add("Retourner horizontalement", () => _engine.FlipLayer(true));
        Add("Retourner verticalement", () => _engine.FlipLayer(false));
        Add("Importer une image…", () => _ = ImportImageAsync());
        Add("Aplatir l'image", () => _engine.FlattenAll());
        more.Flyout = menu;
        LayerButtons.Children.Add(more);

        BlendCombo.ItemsSource = BlendModes.All.Select(m => m.Label()).ToList();
        BlendCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingLayer || BlendCombo.SelectedIndex < 0) return;
            FlushOpacityEdit();
            var mode = BlendModes.All[BlendCombo.SelectedIndex];
            if (_selectedFolder is { } f)
            {
                if (mode != f.BlendMode) _engine.SetFolderProps(f, f.GetProps() with { BlendMode = mode }, "Mode du dossier");
                return;
            }
            var l = _engine.ActiveLayer;
            if (mode != l.BlendMode) _engine.SetLayerProps(l, l.GetProps() with { BlendMode = mode }, "Mode de fusion");
        };
        LayerNameBox.LostFocus += (_, _) => ApplyLayerName();
        LayerNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyLayerName();
        };
        ClipToggle.Click += (_, _) =>
        {
            FlushOpacityEdit();
            var l = _engine.ActiveLayer;
            _engine.SetLayerProps(l, l.GetProps() with { Clipping = ClipToggle.IsChecked == true }, "Écrêtage");
        };
        AlphaLockToggle.Click += (_, _) =>
        {
            FlushOpacityEdit();
            var l = _engine.ActiveLayer;
            _engine.SetLayerProps(l, l.GetProps() with { AlphaLock = AlphaLockToggle.IsChecked == true }, "Verrou alpha");
        };

        _opacityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _opacityTimer.Tick += (_, _) => FlushOpacityEdit();
        LayerOpacity.ValueChanged += (_, e) =>
        {
            if (_syncingLayer) return;
            _opacityBefore ??= _engine.BeginPropsEdit();
            if (_selectedFolder != null) _selectedFolder.Opacity = (float)(e.NewValue / 100);
            else _engine.ActiveLayer.Opacity = (float)(e.NewValue / 100);
            LayerOpacityText.Text = $"{e.NewValue:0} %";
            _engine.Invalidate(_engine.Document.Bounds);
            _opacityTimer.Stop();
            _opacityTimer.Start();
        };

        _thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _thumbTimer.Tick += (_, _) =>
        {
            _thumbTimer.Stop();
            if (_closed) return;
            foreach (var l in _dirtyThumbs)
                if (_thumbImages.TryGetValue(l, out var img)) img.Source = GetThumb(l);
            _dirtyThumbs.Clear();
            RefreshFrameThumbs();
        };

        RebuildLayers();
    }

    // ---------------------------------------------------------------- actions

    void ClearLayerSelection()
    {
        _multi.Clear();
        _selectedFolder = null;
    }

    void GroupSelection()
    {
        CommitPending();
        var sel = _multi.Count > 0 ? _multi.ToList() : new List<Layer> { _engine.ActiveLayer };
        var f = _engine.GroupLayers(sel);
        _multi.Clear();
        _selectedFolder = f;
        RebuildLayers();
        if (f != null) ShowToast($"Dossier créé : {f.Name}");
    }

    void DuplicateSelection()
    {
        if (_selectedFolder != null)
        {
            _selectedFolder = _engine.DuplicateFolder(_selectedFolder);
            RebuildLayers();
            return;
        }
        ClearLayerSelection();
        _engine.DuplicateLayer();
    }

    void MergeSelection()
    {
        if (_multi.Count >= 2)
        {
            var sel = _multi.ToList();
            _multi.Clear();
            _engine.MergeLayers(sel);
            return;
        }
        if (_selectedFolder != null)
        {
            var (s, e) = _engine.Document.FolderRange(_selectedFolder);
            var layers = _engine.Document.Layers.GetRange(s, e - s);
            _selectedFolder = null;
            if (layers.Count >= 2) _engine.MergeLayers(layers, "Fusionner le dossier");
            return;
        }
        _engine.MergeDown();
    }

    void DeleteSelection()
    {
        if (_multi.Count > 0)
        {
            var sel = _multi.ToList();
            _multi.Clear();
            _engine.DeleteLayers(sel);
            return;
        }
        if (_selectedFolder != null)
        {
            var f = _selectedFolder;
            _selectedFolder = null;
            _engine.DeleteFolder(f);
            return;
        }
        _engine.DeleteLayer();
    }

    void ApplyLayerName()
    {
        var name = LayerNameBox.Text?.Trim();
        if (_selectedFolder is { } f)
        {
            if (string.IsNullOrEmpty(name) || name == f.Name) { LayerNameBox.Text = f.Name; return; }
            _engine.SetFolderProps(f, f.GetProps() with { Name = name }, "Renommer le dossier");
            return;
        }
        var l = _engine.ActiveLayer;
        if (string.IsNullOrEmpty(name) || name == l.Name)
        {
            LayerNameBox.Text = l.Name;
            return;
        }
        _engine.SetLayerProps(l, l.GetProps() with { Name = name }, "Renommer le calque");
    }

    void FlushOpacityEdit()
    {
        _opacityTimer?.Stop();
        if (_opacityBefore == null) return;
        var before = _opacityBefore;
        _opacityBefore = null;
        _engine.EndPropsEdit(before, "Opacité");
    }

    void OnLayerPixelsChanged(Layer l)
    {
        _dirtyThumbs.Add(l);
        _thumbTimer?.Stop();
        _thumbTimer?.Start();
    }

    WriteableBitmap GetThumb(Layer l)
    {
        if (_thumbs.TryGetValue(l, out var t) && t.Version == l.Version) return t.Bitmap;
        var bmp = SkiaBitmaps.FromSkia(l.Bitmap, 96, checker: true);
        _thumbs[l] = (l.Version, bmp);
        t.Bitmap?.Dispose();
        return bmp;
    }

    void DisposeThumbs()
    {
        foreach (var (_, bmp) in _thumbs.Values) bmp.Dispose();
        _thumbs.Clear();
    }

    void RefreshAllThumbnails()
    {
        foreach (var (l, img) in _thumbImages) img.Source = GetThumb(l);
    }

    // ---------------------------------------------------------------- liste

    void RebuildLayers()
    {
        if (_dragging) return;
        var doc = _engine.Document;
        var layers = doc.Layers;
        _multi.RemoveWhere(l => !layers.Contains(l));
        var folders = doc.AllFolders();
        if (_selectedFolder != null && !folders.Contains(_selectedFolder)) _selectedFolder = null;

        LayerList.Children.Clear();
        _rows.Clear();
        _thumbImages.Clear();
        foreach (var dead in _thumbs.Keys.Where(k => !layers.Contains(k)).ToList())
        {
            _thumbs[dead].Bitmap.Dispose();
            _thumbs.Remove(dead);
        }

        // Du haut vers le bas : l'en-tête d'un dossier précède son contenu.
        var emitted = new HashSet<LayerFolder>();
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var l = layers[i];
            var chain = new List<LayerFolder>();
            for (var f = l.Folder; f != null; f = f.Parent) chain.Insert(0, f);
            bool hidden = false;
            foreach (var f in chain)
            {
                if (emitted.Add(f) && !hidden) AddRow(BuildFolderRow(f), null, f, f.Depth);
                if (!f.Expanded) hidden = true;
            }
            if (!hidden) AddRow(BuildLayerRow(l, i, chain.Count), l, null, chain.Count);
        }

        bool multi = _multiMode || _multi.Count > 0;
        MultiHint.IsVisible = multi;
        MultiHint.Text = $"{_multi.Count} calque(s) sélectionné(s) — grouper, fusionner et supprimer s'appliquent à la sélection.";
        if (_multiButton != null) _multiButton.IsChecked = _multiMode;
        // Fusion vers le bas impossible sur le calque du bas ou d'une image à l'autre.
        if (_mergeButton != null) _mergeButton.IsEnabled = _multi.Count >= 2 || _selectedFolder != null || _engine.CanMergeDown;
        UpdateLayerProps();
        BuildPaperRow();
    }

    void AddRow(Control row, Layer? layer, LayerFolder? folder, int depth)
    {
        _rows.Add((row, layer, folder, depth));
        LayerList.Children.Add(row);
    }

    Control BuildGrip(Layer? layer, LayerFolder? folder)
    {
        var grip = new Border
        {
            Width = 26,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new LineIcon { Data = Icons.Grip, Width = 16, Height = 16, Foreground = MortisTheme.TextFaint, StrokeWidth = 2.2 },
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(grip, "Glisser pour déplacer");
        grip.PointerPressed += (_, e) =>
        {
            e.PreventGestureRecognition();
            e.Handled = true;
            e.Pointer.Capture(grip);
            _dragLayer = layer;
            _dragFolder = folder;
            _dragging = false;
            _dragStart = e.GetPosition(LayerList);
            _dropTarget = null;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragLayer == null && _dragFolder == null) return;
            var p = e.GetPosition(LayerList);
            if (!_dragging && Math.Abs(p.Y - _dragStart.Y) < 4) return;
            _dragging = true;
            UpdateDropTarget(p);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            // Valider avant de relâcher la capture : sa perte déclenche FinishDrag(commit: false).
            FinishDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => FinishDrag(commit: false);
        return grip;
    }

    void UpdateDropTarget(Point p)
    {
        _dropTarget = null;
        if (_rows.Count == 0) return;
        double y = 0, indent = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var (row, layer, folder, depth) = _rows[i];
            var b = row.Bounds;
            bool last = i == _rows.Count - 1;
            if (p.Y < b.Top && i == 0) { y = b.Top; _dropTarget = (layer, folder, DropPosition.Above); indent = depth; break; }
            if (p.Y > b.Bottom && !last) continue;
            double t = Math.Clamp((p.Y - b.Top) / Math.Max(1, b.Height), 0, 1);
            if (folder != null)
            {
                if (t < 0.35) { y = b.Top; _dropTarget = (null, folder, DropPosition.Above); indent = depth; }
                else { y = b.Bottom; _dropTarget = (null, folder, DropPosition.Into); indent = depth + 1; }
            }
            else
            {
                if (t < 0.5) { y = b.Top; _dropTarget = (layer, null, DropPosition.Above); }
                else { y = b.Bottom; _dropTarget = (layer, null, DropPosition.Below); }
                indent = depth;
            }
            break;
        }
        if (_dropTarget is { } dt && (dt.Layer != null && dt.Layer == _dragLayer || dt.Folder != null && dt.Folder == _dragFolder))
        {
            _dropTarget = null;
            DropIndicator.IsVisible = false;
            return;
        }
        DropIndicator.IsVisible = _dropTarget != null;
        DropIndicator.Margin = new Thickness(8 + indent * IndentStep, LayerList.Margin.Top + y - 1, 8, 0);
    }

    void FinishDrag(bool commit)
    {
        bool wasDragging = _dragging;
        var target = _dropTarget;
        var layer = _dragLayer;
        var folder = _dragFolder;
        _dragging = false;
        _dragLayer = null;
        _dragFolder = null;
        _dropTarget = null;
        DropIndicator.IsVisible = false;
        if (commit && wasDragging && target is { } t)
        {
            CommitPending();
            _engine.MoveNode(layer, folder, t.Layer, t.Folder, t.Pos);
        }
        else if (wasDragging)
        {
            RebuildLayers();
        }
    }

    Control BuildFolderRow(LayerFolder f)
    {
        bool selected = _selectedFolder == f;
        bool isFrame = _engine.Document.AnimationEnabled && f.Parent == null;
        bool currentFrame = isFrame && _engine.CurrentFrame == f;
        var chevron = new Button
        {
            Classes = { "icon", "small" },
            Width = 24,
            Height = 24,
            Content = new LineIcon { Data = f.Expanded ? Icons.ChevronDown : Icons.ChevronRight, Width = 14, Height = 14 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Click += (_, _) =>
        {
            f.Expanded = !f.Expanded;
            _engine.Document.IsDirty = true;
            RebuildLayers();
        };
        chevron.Tapped += (_, e) => e.Handled = true;
        ToolTip.SetTip(chevron, f.Expanded ? "Replier le dossier" : "Déplier le dossier");

        var eye = new Button
        {
            Classes = { "icon", "small" },
            Content = Icons.Make(f.Visible ? Icons.Eye : Icons.EyeOff, 17),
            Opacity = f.Visible ? 1 : 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(eye, f.Visible ? "Masquer le dossier" : "Afficher le dossier");
        eye.Click += (_, _) =>
        {
            FlushOpacityEdit();
            _engine.SetFolderProps(f, f.GetProps() with { Visible = !f.Visible }, f.Visible ? "Masquer le dossier" : "Afficher le dossier");
        };
        eye.Tapped += (_, e) => e.Handled = true;

        var (s, e2) = _engine.Document.FolderRange(f);
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = f.Name, FontWeight = selected || currentFrame ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = selected ? MortisTheme.TextStrong : MortisTheme.Text },
                new TextBlock { Text = $"{(isFrame ? "Image · " : "")}{e2 - s} calque{(e2 - s > 1 ? "s" : "")} · {f.BlendMode.Label()} · {f.Opacity * 100:0} %", Classes = { "faint" } },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        var spacer = new Border { Width = f.Depth * IndentStep };
        grid.Children.Add(spacer);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var icon = new LineIcon { Data = isFrame ? Icons.Film : Icons.Folder, Width = 18, Height = 18, Foreground = currentFrame ? MortisTheme.Accent : MortisTheme.TextMute, Margin = new Thickness(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 2);
        grid.Children.Add(icon);
        Grid.SetColumn(info, 3);
        grid.Children.Add(info);
        Grid.SetColumn(eye, 4);
        grid.Children.Add(eye);
        var grip = BuildGrip(null, f);
        Grid.SetColumn(grip, 5);
        grid.Children.Add(grip);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2, 4),
            Background = selected ? MortisTheme.Active : Brushes.Transparent,
            BorderBrush = selected ? MortisTheme.LineStrong : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.Tapped += (_, _) =>
        {
            FlushOpacityEdit();
            _multi.Clear();
            _selectedFolder = f;
            // Sans test sur CurrentFrame : depuis un calque de fond, l'image affichée doit pouvoir être choisie.
            if (isFrame) _engine.GoToFrame(f);
            RebuildLayers();
        };
        return row;
    }

    Control BuildLayerRow(Layer l, int index, int depth)
    {
        bool inMulti = _multi.Contains(l);
        bool active = index == _engine.Document.ActiveIndex && _selectedFolder == null;
        bool highlighted = inMulti || (active && _multi.Count == 0);
        var eye = new Button
        {
            Classes = { "icon", "small" },
            Content = Icons.Make(l.Visible ? Icons.Eye : Icons.EyeOff, 17),
            Opacity = l.Visible ? 1 : 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(eye, l.Visible ? "Masquer" : "Afficher");
        eye.Click += (_, _) =>
        {
            FlushOpacityEdit();
            _engine.SetLayerProps(l, l.GetProps() with { Visible = !l.Visible }, l.Visible ? "Masquer le calque" : "Afficher le calque");
        };
        eye.Tapped += (_, e) => e.Handled = true;

        var thumb = new Image { Stretch = Stretch.Uniform, Source = _tab == Tab.Layers || _wide ? GetThumb(l) : null };
        _thumbImages[l] = thumb;
        var thumbBorder = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = MortisTheme.Bg,
            BorderBrush = highlighted ? MortisTheme.Accent : MortisTheme.LineStrong,
            BorderThickness = new Thickness(1),
            Child = thumb,
            Margin = new Thickness(2, 0, 10, 0),
        };

        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (l.Clipping) flags.Children.Add(new LineIcon { Data = Icons.Clip, Width = 13, Height = 13, Foreground = MortisTheme.TextFaint });
        if (l.AlphaLock) flags.Children.Add(new LineIcon { Data = Icons.Lock, Width = 13, Height = 13, Foreground = MortisTheme.TextFaint });
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = l.Name, FontWeight = highlighted ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = highlighted ? MortisTheme.TextStrong : MortisTheme.Text },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = $"{l.BlendMode.Label()} · {l.Opacity * 100:0} %", Classes = { "faint" } },
                        flags,
                    },
                },
            },
        };

        var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = highlighted ? MortisTheme.Accent : Brushes.Transparent, Margin = new Thickness(0, 4, 4, 4) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        grid.Children.Add(new Border { Width = depth * IndentStep + (l.Clipping && index > 0 ? 12 : 0) });
        Grid.SetColumn(marker, 1);
        grid.Children.Add(marker);
        Grid.SetColumn(eye, 2);
        grid.Children.Add(eye);
        Grid.SetColumn(thumbBorder, 3);
        grid.Children.Add(thumbBorder);
        Grid.SetColumn(info, 4);
        grid.Children.Add(info);
        var grip = BuildGrip(l, null);
        Grid.SetColumn(grip, 5);
        grid.Children.Add(grip);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2, 4),
            Background = highlighted ? MortisTheme.Active : Brushes.Transparent,
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.Tapped += (_, e) =>
        {
            FlushOpacityEdit();
            if (Canvas.Transform != null) Canvas.CommitTransform();
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _selectedFolder = null;
            if (_multiMode || ctrl)
            {
                if (_multi.Count == 0) _multi.Add(_engine.ActiveLayer);
                if (!_multi.Remove(l)) _multi.Add(l);
                RebuildLayers();
                return;
            }
            if (shift)
            {
                int a = _engine.Document.ActiveIndex;
                _multi.Clear();
                // Seulement les calques visibles dans la liste : rien de caché dans un dossier replié.
                var shown = _rows.Where(r => r.Layer != null).Select(r => r.Layer!).ToHashSet();
                for (int k = Math.Min(a, index); k <= Math.Max(a, index); k++)
                {
                    var layer = _engine.Document.Layers[k];
                    if (k == a || shown.Contains(layer)) _multi.Add(layer);
                }
                RebuildLayers();
                return;
            }
            _multi.Clear();
            if (index != _engine.Document.ActiveIndex) _engine.SetActive(index);
            else RebuildLayers();
        };
        return row;
    }

    void BuildPaperRow()
    {
        var doc = _engine.Document;
        var eye = new Button
        {
            Classes = { "icon", "small" },
            Content = Icons.Make(doc.PaperVisible ? Icons.Eye : Icons.EyeOff, 17),
            Opacity = doc.PaperVisible ? 1 : 0.5,
        };
        ToolTip.SetTip(eye, doc.PaperVisible ? "Papier transparent" : "Afficher le papier");
        eye.Click += (_, _) =>
        {
            FlushOpacityEdit();
            _engine.SetPaper(!doc.PaperVisible, doc.PaperColor);
        };
        var swatch = new Border
        {
            Width = 42,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(doc.PaperColor.ToAvalonia()),
            BorderBrush = MortisTheme.LineStrong,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2, 0, 10, 0),
        };
        var label = new TextBlock { Text = "Papier", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var useColor = new Button { Classes = { "flat" }, Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center, Content = new TextBlock { Text = "Couleur actuelle", FontSize = 12, Foreground = MortisTheme.Accent } };
        useColor.Click += (_, _) =>
        {
            FlushOpacityEdit();
            _engine.SetPaper(true, _state.Color);
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(7, 0, 0, 0) };
        grid.Children.Add(eye);
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(swatch);
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);
        Grid.SetColumn(useColor, 3);
        grid.Children.Add(useColor);
        PaperRow.Child = grid;
    }

    void UpdateLayerProps()
    {
        _syncingLayer = true;
        if (_selectedFolder is { } f)
        {
            PropsKindIcon.Data = Icons.Folder;
            if (!LayerNameBox.IsFocused) LayerNameBox.Text = f.Name;
            BlendCombo.SelectedIndex = Array.IndexOf(BlendModes.All, f.BlendMode);
            LayerOpacity.Value = f.Opacity * 100;
            LayerOpacityText.Text = $"{f.Opacity * 100:0} %";
            LayerFlags.IsVisible = false;
        }
        else
        {
            var l = _engine.ActiveLayer;
            PropsKindIcon.Data = Icons.Layers;
            if (!LayerNameBox.IsFocused) LayerNameBox.Text = l.Name;
            BlendCombo.SelectedIndex = Array.IndexOf(BlendModes.All, l.BlendMode);
            LayerOpacity.Value = l.Opacity * 100;
            LayerOpacityText.Text = $"{l.Opacity * 100:0} %";
            ClipToggle.IsChecked = l.Clipping;
            AlphaLockToggle.IsChecked = l.AlphaLock;
            LayerFlags.IsVisible = true;
        }
        _syncingLayer = false;
    }
}
