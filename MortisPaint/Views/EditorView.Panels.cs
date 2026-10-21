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
using MortisPaint.Editor;
using MortisPaint.Engine;
using SkiaSharp;

namespace MortisPaint.Views;

public partial class EditorView
{
    static readonly IBrush RibbonBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x32, 0x1E));
    static readonly IBrush RibbonSoftBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xE6, 0xE2));
    static readonly IBrush RuleBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE2, 0xDD));
    static readonly IBrush RuleStrongBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xCC, 0xC5));
    static readonly IBrush InkFaintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x94));

    // Pinceaux
    readonly List<SliderRow> _brushRows = new();
    readonly Dictionary<BrushSettings, WriteableBitmap> _brushPreviews = new();
    readonly Dictionary<BrushSettings, Image> _brushPreviewImages = new();
    readonly Dictionary<BrushSettings, TextBlock> _brushSizeLabels = new();
    DispatcherTimer? _brushPreviewTimer;
    bool _editingFromPanel, _editingFromQuick;

    // Couleurs
    readonly List<SliderRow> _rgbRows = new();
    bool _fromWheel;

    // Calques
    readonly Dictionary<Layer, (int Version, WriteableBitmap Bitmap)> _thumbs = new();
    readonly Dictionary<Layer, Image> _thumbImages = new();
    readonly HashSet<Layer> _dirtyThumbs = new();
    DispatcherTimer? _thumbTimer;
    DispatcherTimer? _opacityTimer;
    DocState? _opacityBefore;
    bool _syncingLayer;
    Button? _mergeButton, _upButton, _downButton;

    // ================================================================ pinceau

    void BuildBrushPanel()
    {
        BrushResetButton.Click += (_, _) => _state.ResetCurrentBrush();
        _brushPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _brushPreviewTimer.Tick += (_, _) =>
        {
            _brushPreviewTimer.Stop();
            var b = _state.CurrentBrush;
            _brushPreviews.Remove(b);
            if (_brushPreviewImages.TryGetValue(b, out var img)) img.Source = GetBrushPreview(b);
        };

        QuickSize.ValueChanged += (_, e) =>
        {
            if (_syncQuick) return;
            var b = _state.CurrentBrush;
            b.Size = SliderToSize(b, e.NewValue);
            _editingFromQuick = true;
            _state.NotifyBrushEdited();
            _editingFromQuick = false;
            QuickSizeText.Text = FormatSize(b.Size);
        };
        QuickOpacity.ValueChanged += (_, e) =>
        {
            if (_syncQuick) return;
            var b = _state.CurrentBrush;
            b.Opacity = (float)(e.NewValue / 100);
            _editingFromQuick = true;
            _state.NotifyBrushEdited();
            _editingFromQuick = false;
            QuickOpacityText.Text = $"{b.Opacity * 100:0} %";
        };
        RebuildBrushList();
        RebuildBrushSettings();
    }

    bool _syncQuick;

    static string FormatSize(float s) => s < 10 ? $"{s:0.#}" : $"{s:0}";

    static double SizeToSlider(BrushSettings b) => 1000 * Math.Log(Math.Max(1, b.Size)) / Math.Log(Math.Max(2, b.MaxSize));
    static float SliderToSize(BrushSettings b, double v) => (float)Math.Clamp(Math.Exp(v / 1000 * Math.Log(Math.Max(2, b.MaxSize))), 1, b.MaxSize);

    void UpdateQuickSliders()
    {
        bool show = EditorState.IsBrushTool(_state.Tool) && _filter == null;
        QuickSliders.IsVisible = show;
        if (!show) return;
        var b = _state.CurrentBrush;
        _syncQuick = true;
        QuickSize.Value = SizeToSlider(b);
        QuickOpacity.Value = Math.Clamp(b.Opacity * 100, 1, 100);
        _syncQuick = false;
        QuickSizeText.Text = FormatSize(b.Size);
        QuickOpacityText.Text = $"{b.Opacity * 100:0} %";
        ToolTip.SetTip(QuickOpacity, _state.Tool is ToolKind.Smudge or ToolKind.Blur ? "Force" : "Opacité");
    }

    void OnBrushChanged()
    {
        RebuildBrushList();
        RebuildBrushSettings();
        UpdateQuickSliders();
    }

    void OnBrushEdited()
    {
        if (!_editingFromQuick) UpdateQuickSliders();
        if (!_editingFromPanel)
            foreach (var r in _brushRows) r.Refresh();
        var cur = _state.CurrentBrush;
        if (_brushSizeLabels.TryGetValue(cur, out var label)) label.Text = $"{cur.Size:0.#} px";
        _brushPreviewTimer?.Stop();
        _brushPreviewTimer?.Start();
        Canvas.InvalidateVisual();
    }

    WriteableBitmap GetBrushPreview(BrushSettings b)
    {
        if (_brushPreviews.TryGetValue(b, out var bmp)) return bmp;
        bmp = BrushPreview.Render(b, _state.Tool is ToolKind.Smudge or ToolKind.Blur);
        _brushPreviews[b] = bmp;
        return bmp;
    }

    void RebuildBrushList()
    {
        BrushList.Children.Clear();
        _brushPreviewImages.Clear();
        _brushSizeLabels.Clear();
        var list = _state.CurrentBrushList;
        BrushListTitle.Text = _state.Tool switch
        {
            ToolKind.Eraser => "Gommes",
            ToolKind.Smudge => "Doigt",
            ToolKind.Blur => "Flou",
            _ => "Pinceaux",
        };
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            int idx = i;
            bool selected = idx == _state.CurrentBrushIndex;
            var img = new Image { Source = GetBrushPreview(b), Height = 26, Width = 112, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right };
            _brushPreviewImages[b] = img;
            var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = selected ? RibbonBrush : Brushes.Transparent, Margin = new Thickness(0, 2, 8, 2) };
            var name = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = b.Name, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal },
                },
            };
            var sizeLabel = new TextBlock { Text = $"{b.Size:0.#} px", Classes = { "faint", "mono" } };
            name.Children.Add(sizeLabel);
            _brushSizeLabels[b] = sizeLabel;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(marker);
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            Grid.SetColumn(img, 2);
            grid.Children.Add(img);
            var btn = new Button { Classes = { "row" }, Padding = new Thickness(4, 5, 8, 5), Content = grid };
            if (selected) btn.Classes.Add("selected");
            btn.Click += (_, _) =>
            {
                _state.SelectBrush(idx);
                if (!_wide) SetSheetOpen(false);
            };
            BrushList.Children.Add(btn);
        }
    }

    void AddBrushRow(string label, double min, double max, Func<BrushSettings, double> get, Action<BrushSettings, double> set, Func<BrushSettings, string> fmt)
    {
        var row = new SliderRow(label, min, max,
            () => get(_state.CurrentBrush),
            v =>
            {
                set(_state.CurrentBrush, v);
                _editingFromPanel = true;
                _state.NotifyBrushEdited();
                _editingFromPanel = false;
            },
            () => fmt(_state.CurrentBrush));
        _brushRows.Add(row);
        BrushSettingsPanel.Children.Add(row.View);
    }

    void AddBrushToggle(WrapPanel host, string label, Func<BrushSettings, bool> get, Action<BrushSettings, bool> set)
    {
        var t = new ToggleButton { Classes = { "chip" }, Content = label, IsChecked = get(_state.CurrentBrush) };
        t.Click += (_, _) =>
        {
            set(_state.CurrentBrush, t.IsChecked == true);
            _state.NotifyBrushEdited();
        };
        host.Children.Add(t);
    }

    void RebuildBrushSettings()
    {
        BrushSettingsPanel.Children.Clear();
        _brushRows.Clear();
        var tool = _state.Tool;
        bool pixelTool = tool is ToolKind.Smudge or ToolKind.Blur;
        var cur = _state.CurrentBrush;

        if (!pixelTool)
        {
            var shapes = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            BrushSettingsPanel.Children.Add(new TextBlock { Text = "Pointe", Classes = { "dim" }, Margin = new Thickness(0, 2, 0, 2) });
            var shapeList = new (BrushShape Shape, string Label)[] { (BrushShape.Round, "Ronde"), (BrushShape.Ellipse, "Plate"), (BrushShape.Square, "Carrée"), (BrushShape.Pixel, "Pixel") };
            var toggles = new List<ToggleButton>();
            foreach (var (shape, label) in shapeList)
            {
                var t = new ToggleButton { Classes = { "chip" }, Content = label, IsChecked = cur.Shape == shape };
                toggles.Add(t);
                t.Click += (_, _) =>
                {
                    var b = _state.CurrentBrush;
                    b.Shape = shape;
                    b.MaxSize = shape == BrushShape.Pixel ? 64 : 500;
                    if (b.Size > b.MaxSize) b.Size = b.MaxSize;
                    if (shape == BrushShape.Ellipse && b.Roundness > 0.95f) b.Roundness = 0.3f;
                    _state.NotifyBrushEdited();
                    RebuildBrushSettings();
                };
                shapes.Children.Add(t);
            }
            BrushSettingsPanel.Children.Add(shapes);
        }

        AddBrushRow("Taille", 0, 1000, SizeToSlider, (b, v) => b.Size = SliderToSize(b, v), b => $"{b.Size:0.#} px");
        AddBrushRow(pixelTool ? "Force" : "Opacité", 1, 100, b => b.Opacity * 100, (b, v) => b.Opacity = (float)(v / 100), b => $"{b.Opacity * 100:0} %");
        if (cur.Shape != BrushShape.Pixel)
            AddBrushRow("Dureté", 0, 100, b => b.Hardness * 100, (b, v) => b.Hardness = (float)(v / 100), b => $"{b.Hardness * 100:0} %");
        if (!pixelTool)
            AddBrushRow("Densité", 1, 100, b => b.Flow * 100, (b, v) => b.Flow = (float)(v / 100), b => $"{b.Flow * 100:0} %");
        if (cur.Shape != BrushShape.Pixel)
            AddBrushRow("Espacement", 1, 100, b => b.Spacing * 100, (b, v) => b.Spacing = (float)(v / 100), b => $"{b.Spacing * 100:0} %");
        AddBrushRow("Stabilisation", 0, 20, b => b.Stabilizer, (b, v) => b.Stabilizer = (float)v, b => $"{b.Stabilizer:0}");
        if (!pixelTool && cur.Shape != BrushShape.Pixel)
        {
            AddBrushRow("Grain papier", 0, 100, b => b.Grain * 100, (b, v) => b.Grain = (float)(v / 100), b => $"{b.Grain * 100:0} %");
            AddBrushRow("Dispersion", 0, 100, b => b.Scatter * 100, (b, v) => b.Scatter = (float)(v / 100), b => $"{b.Scatter * 100:0} %");
            AddBrushRow("Variation de taille", 0, 100, b => b.SizeJitter * 100, (b, v) => b.SizeJitter = (float)(v / 100), b => $"{b.SizeJitter * 100:0} %");
            AddBrushRow("Effilement (début)", 0, 150, b => b.TaperStart, (b, v) => b.TaperStart = (float)v, b => $"{b.TaperStart:0} px");
        }
        if (cur.Shape == BrushShape.Ellipse)
        {
            AddBrushRow("Rondeur", 5, 100, b => b.Roundness * 100, (b, v) => b.Roundness = (float)(v / 100), b => $"{b.Roundness * 100:0} %");
            AddBrushRow("Angle", 0, 180, b => b.Angle, (b, v) => b.Angle = (float)v, b => $"{b.Angle:0}°");
        }
        AddBrushRow("Taille min. (pression)", 1, 100, b => b.MinSize * 100, (b, v) => b.MinSize = (float)(v / 100), b => $"{b.MinSize * 100:0} %");

        var toggleHost = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddBrushToggle(toggleHost, "Pression → taille", b => b.PressureSize, (b, v) => b.PressureSize = v);
        AddBrushToggle(toggleHost, pixelTool ? "Pression → force" : "Pression → opacité", b => b.PressureOpacity, (b, v) => b.PressureOpacity = v);
        if (cur.Shape == BrushShape.Ellipse)
            AddBrushToggle(toggleHost, "Suivre la direction", b => b.FollowDirection, (b, v) => b.FollowDirection = v);
        BrushSettingsPanel.Children.Add(toggleHost);
    }

    // ================================================================ couleur

    void BuildColorPanel()
    {
        Wheel.Color = _state.Color;
        Wheel.ColorChanged += c =>
        {
            _fromWheel = true;
            _state.SetColor(c);
            _fromWheel = false;
        };
        HexBox.LostFocus += (_, _) => ApplyHex();
        HexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyHex();
        };
        SwapColorsButton.Click += (_, _) => _state.SwapColors();
        AddToPaletteButton.Click += (_, _) => _state.AddToPalette(_state.Color);

        var channels = new (string Label, int Index)[] { ("Rouge", 0), ("Vert", 1), ("Bleu", 2) };
        foreach (var (label, idx) in channels)
        {
            var row = new SliderRow(label, 0, 255,
                () => idx == 0 ? _state.Color.Red : idx == 1 ? _state.Color.Green : _state.Color.Blue,
                v =>
                {
                    var c = _state.Color;
                    byte b = (byte)Math.Round(v);
                    _state.SetColor(idx == 0 ? new SKColor(b, c.Green, c.Blue) : idx == 1 ? new SKColor(c.Red, b, c.Blue) : new SKColor(c.Red, c.Green, b));
                },
                () => (idx == 0 ? _state.Color.Red : idx == 1 ? _state.Color.Green : _state.Color.Blue).ToString());
            _rgbRows.Add(row);
            RgbSliders.Children.Add(row.View);
        }
        RebuildSwatches();
    }

    void ApplyHex()
    {
        if (ColorUtil.TryParseHex(HexBox.Text, out var c)) _state.SetColor(c);
        else HexBox.Text = ColorUtil.ToHex(_state.Color);
    }

    void OnColorChanged()
    {
        var c = _state.Color;
        var brush = new SolidColorBrush(c.ToAvalonia());
        PrimarySwatch.Background = brush;
        RailColorSwatch.Background = brush;
        BottomColorSwatch.Background = brush;
        SecondarySwatch.Background = new SolidColorBrush(_state.SecondaryColor.ToAvalonia());
        if (!_fromWheel) Wheel.Color = c;
        if (!HexBox.IsFocused) HexBox.Text = ColorUtil.ToHex(c);
        foreach (var r in _rgbRows) r.Refresh();
        RebuildSwatches();
    }

    Button Swatch(SKColor c, bool removable)
    {
        bool current = c == _state.Color;
        var b = new Button
        {
            Width = 30,
            Height = 30,
            Margin = new Thickness(1),
            Padding = new Thickness(2),
            Classes = { "flat" },
            Content = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(c.ToAvalonia()),
                BorderBrush = current ? RibbonBrush : RuleStrongBrush,
                BorderThickness = new Thickness(current ? 2 : 1),
            },
        };
        ToolTip.SetTip(b, ColorUtil.ToHex(c));
        b.Click += (_, _) => _state.SetColor(c);
        if (removable)
        {
            var mi = new MenuItem { Header = "Retirer de la palette" };
            mi.Click += (_, _) => _state.RemoveFromPalette(c);
            var flyout = new MenuFlyout();
            flyout.Items.Add(mi);
            b.ContextFlyout = flyout;
        }
        return b;
    }

    void RebuildSwatches()
    {
        RecentPanel.Children.Clear();
        foreach (var c in _state.Recent) RecentPanel.Children.Add(Swatch(c, false));
        if (_state.Recent.Count == 0)
            RecentPanel.Children.Add(new TextBlock { Text = "Les couleurs utilisées apparaîtront ici.", Classes = { "faint" } });
        PalettePanel.Children.Clear();
        foreach (var c in _state.Palette) PalettePanel.Children.Add(Swatch(c, true));
    }

    // ================================================================ calques

    Button LayerButton(Geometry icon, string tip, Action action)
    {
        var b = new Button { Classes = { "icon", "small" }, Content = Icons.Make(icon, 19), Margin = new Thickness(1) };
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
        LayerButton(Icons.Plus, "Nouveau calque (Ctrl+Maj+N)", _engine.AddLayer);
        LayerButton(Icons.Copy, "Dupliquer (Ctrl+J)", _engine.DuplicateLayer);
        _mergeButton = LayerButton(Icons.MergeDown, "Fusionner vers le bas", _engine.MergeDown);
        _upButton = LayerButton(Icons.ArrowUp, "Monter", () => _engine.MoveLayer(1));
        _downButton = LayerButton(Icons.ArrowDown, "Descendre", () => _engine.MoveLayer(-1));
        LayerButton(Icons.Delete, "Supprimer le calque", _engine.DeleteLayer);
        var more = new Button { Classes = { "icon", "small" }, Content = Icons.Make(Icons.More, 19), Margin = new Thickness(1) };
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
        Add("Effacer le calque", _engine.ClearLayer);
        Add("Remplir avec la couleur", () => _engine.FillSelection(_state.Color));
        Add("Retourner horizontalement", () => _engine.FlipLayer(true));
        Add("Retourner verticalement", () => _engine.FlipLayer(false));
        Add("Importer une image…", () => _ = ImportImageAsync());
        Add("Aplatir l'image", _engine.FlattenAll);
        more.Flyout = menu;
        LayerButtons.Children.Add(more);

        BlendCombo.ItemsSource = BlendModes.All.Select(m => m.Label()).ToList();
        BlendCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingLayer || BlendCombo.SelectedIndex < 0) return;
            FlushOpacityEdit();
            var l = _engine.ActiveLayer;
            var mode = BlendModes.All[BlendCombo.SelectedIndex];
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
            _engine.ActiveLayer.Opacity = (float)(e.NewValue / 100);
            LayerOpacityText.Text = $"{e.NewValue:0} %";
            _engine.Invalidate(_engine.Document.Bounds);
            _opacityTimer.Stop();
            _opacityTimer.Start();
        };

        _thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _thumbTimer.Tick += (_, _) =>
        {
            _thumbTimer.Stop();
            foreach (var l in _dirtyThumbs)
                if (_thumbImages.TryGetValue(l, out var img)) img.Source = GetThumb(l);
            _dirtyThumbs.Clear();
        };

        RebuildLayers();
    }

    void ApplyLayerName()
    {
        var l = _engine.ActiveLayer;
        var name = LayerNameBox.Text?.Trim();
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
        _engine.EndPropsEdit(before, "Opacité du calque");
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
        return bmp;
    }

    void RefreshAllThumbnails()
    {
        foreach (var (l, img) in _thumbImages) img.Source = GetThumb(l);
    }

    void RebuildLayers()
    {
        LayerList.Children.Clear();
        _thumbImages.Clear();
        var layers = _engine.Document.Layers;
        // Oublie les miniatures des calques supprimés.
        foreach (var dead in _thumbs.Keys.Where(k => !layers.Contains(k)).ToList()) _thumbs.Remove(dead);
        for (int i = layers.Count - 1; i >= 0; i--) LayerList.Children.Add(BuildLayerRow(layers[i], i));
        int active = _engine.Document.ActiveIndex;
        if (_mergeButton != null) _mergeButton.IsEnabled = active > 0;
        if (_upButton != null) _upButton.IsEnabled = active < layers.Count - 1;
        if (_downButton != null) _downButton.IsEnabled = active > 0;
        UpdateLayerProps();
        BuildPaperRow();
    }

    Control BuildLayerRow(Layer l, int index)
    {
        bool active = index == _engine.Document.ActiveIndex;
        var eye = new Button
        {
            Classes = { "icon", "small" },
            Content = Icons.Make(l.Visible ? Icons.Eye : Icons.EyeOff, 18),
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
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Background = Brushes.White,
            BorderBrush = active ? RibbonBrush : RuleStrongBrush,
            BorderThickness = new Thickness(1),
            Child = thumb,
            Margin = new Thickness(2, 0, 10, 0),
        };

        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (l.Clipping) flags.Children.Add(new LineIcon { Data = Icons.Clip, Width = 13, Height = 13, Foreground = InkFaintBrush });
        if (l.AlphaLock) flags.Children.Add(new LineIcon { Data = Icons.Lock, Width = 13, Height = 13, Foreground = InkFaintBrush });
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = l.Name, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis },
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

        var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = active ? RibbonBrush : Brushes.Transparent, Margin = new Thickness(0, 4, 4, 4) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
        grid.Children.Add(marker);
        Grid.SetColumn(eye, 1);
        grid.Children.Add(eye);
        Grid.SetColumn(thumbBorder, 2);
        grid.Children.Add(thumbBorder);
        Grid.SetColumn(info, 3);
        grid.Children.Add(info);

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2, 5),
            Background = active ? RibbonSoftBrush : Brushes.Transparent,
            Margin = l.Clipping && index > 0 ? new Thickness(20, 0, 0, 0) : new Thickness(0),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.Tapped += (_, _) =>
        {
            FlushOpacityEdit();
            if (Canvas.Transform != null) Canvas.CommitTransform();
            _engine.SetActive(index);
        };
        return row;
    }

    void BuildPaperRow()
    {
        var doc = _engine.Document;
        var eye = new Button
        {
            Classes = { "icon", "small" },
            Content = Icons.Make(doc.PaperVisible ? Icons.Eye : Icons.EyeOff, 18),
            Opacity = doc.PaperVisible ? 1 : 0.5,
        };
        ToolTip.SetTip(eye, doc.PaperVisible ? "Papier transparent" : "Afficher le papier");
        eye.Click += (_, _) => _engine.SetPaper(!doc.PaperVisible, doc.PaperColor);
        var swatch = new Border
        {
            Width = 46,
            Height = 30,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(doc.PaperColor.ToAvalonia()),
            BorderBrush = RuleStrongBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2, 0, 10, 0),
        };
        var label = new TextBlock { Text = "Papier", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var useColor = new Button { Classes = { "flat" }, Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center, Content = new TextBlock { Text = "Couleur actuelle", FontSize = 12, Foreground = RibbonBrush } };
        useColor.Click += (_, _) => _engine.SetPaper(true, _state.Color);
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
        var l = _engine.ActiveLayer;
        if (!LayerNameBox.IsFocused) LayerNameBox.Text = l.Name;
        BlendCombo.SelectedIndex = Array.IndexOf(BlendModes.All, l.BlendMode);
        LayerOpacity.Value = l.Opacity * 100;
        LayerOpacityText.Text = $"{l.Opacity * 100:0} %";
        ClipToggle.IsChecked = l.Clipping;
        AlphaLockToggle.IsChecked = l.AlphaLock;
        _syncingLayer = false;
    }
}
