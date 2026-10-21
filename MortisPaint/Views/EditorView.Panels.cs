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
    // Pinceaux
    readonly List<SliderRow> _brushRows = new();
    readonly Dictionary<BrushSettings, WriteableBitmap> _brushPreviews = new();
    readonly Dictionary<BrushSettings, Image> _brushPreviewImages = new();
    readonly Dictionary<BrushSettings, TextBlock> _brushSizeLabels = new();
    DispatcherTimer? _brushPreviewTimer;
    bool _editingFromPanel, _editingFromQuick, _syncQuick;

    // Couleurs
    readonly List<SliderRow> _rgbRows = new();
    bool _fromWheel;

    // ================================================================ pinceau

    void BuildBrushPanel()
    {
        BrushResetButton.Click += (_, _) => _state.ResetCurrentBrush();
        NewImageBrushButton.Click += async (_, _) => await CreateImageBrushAsync();
        _brushPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _brushPreviewTimer.Tick += (_, _) =>
        {
            _brushPreviewTimer.Stop();
            if (_closed) return;
            var b = _state.CurrentBrush;
            _brushPreviews.Remove(b, out var old);
            if (_brushPreviewImages.TryGetValue(b, out var img)) img.Source = GetBrushPreview(b);
            old?.Dispose();
            if (b.IsCustom) _state.SaveCustomBrushes();
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

    static string FormatSize(float s) => s < 10 ? $"{s:0.#}" : $"{s:0}";

    static double SizeToSlider(BrushSettings b) => 1000 * Math.Log(Math.Max(1, b.Size)) / Math.Log(Math.Max(2, b.MaxSize));
    static float SliderToSize(BrushSettings b, double v) => (float)Math.Clamp(Math.Exp(v / 1000 * Math.Log(Math.Max(2, b.MaxSize))), 1, b.MaxSize);

    void UpdateQuickSliders()
    {
        bool show = EditorState.IsBrushTool(_state.Tool) && _filter == null && _text == null;
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
        NewImageBrushButton.IsVisible = _state.Tool is ToolKind.Brush or ToolKind.Shape;
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            int idx = i;
            bool selected = idx == _state.CurrentBrushIndex;
            var img = new Image { Source = GetBrushPreview(b), Height = 26, Width = 112, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right };
            _brushPreviewImages[b] = img;
            var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = selected ? MortisTheme.Accent : Brushes.Transparent, Margin = new Thickness(0, 2, 8, 2) };
            var sizeLabel = new TextBlock { Text = $"{b.Size:0.#} px", Classes = { "faint", "mono" } };
            _brushSizeLabels[b] = sizeLabel;
            var name = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = b.Name, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal },
                    sizeLabel,
                },
            };
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
            if (b.IsCustom)
            {
                var del = new MenuItem { Header = "Supprimer ce pinceau" };
                del.Click += (_, _) => _state.RemoveCustomBrush(b);
                var flyout = new MenuFlyout();
                flyout.Items.Add(del);
                btn.ContextFlyout = flyout;
            }
            BrushList.Children.Add(btn);
        }
    }

    async System.Threading.Tasks.Task CreateImageBrushAsync()
    {
        try
        {
            using var bmp = await PickImageAsync("Image pour le nouveau pinceau");
            if (bmp == null) return;
            var id = BrushTips.Create(bmp);
            int n = _state.Brushes.Count(b => b.IsCustom) + 1;
            var brush = BrushPresets.CreateImageBrush($"Image {n}", id);
            if (_state.Tool is not (ToolKind.Brush or ToolKind.Shape)) _state.SetTool(ToolKind.Brush);
            _state.AddCustomBrush(brush);
            ShowToast("Pinceau créé : clic droit dessus pour le supprimer");
        }
        catch (Exception ex)
        {
            ShowToast("Création impossible : " + ex.Message);
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
        bool image = cur.Shape == BrushShape.Image;

        if (!pixelTool && !image)
        {
            BrushSettingsPanel.Children.Add(new TextBlock { Text = "Pointe", Classes = { "dim" }, Margin = new Thickness(0, 2, 0, 2) });
            var shapes = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            var shapeList = new (BrushShape Shape, string Label)[] { (BrushShape.Round, "Ronde"), (BrushShape.Ellipse, "Plate"), (BrushShape.Square, "Carrée"), (BrushShape.Pixel, "Pixel") };
            foreach (var (shape, label) in shapeList)
            {
                var t = new ToggleButton { Classes = { "chip" }, Content = label, IsChecked = cur.Shape == shape };
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
        if (cur.Shape is not (BrushShape.Pixel or BrushShape.Image))
            AddBrushRow("Dureté", 0, 100, b => b.Hardness * 100, (b, v) => b.Hardness = (float)(v / 100), b => $"{b.Hardness * 100:0} %");
        if (!pixelTool)
            AddBrushRow("Densité", 1, 100, b => b.Flow * 100, (b, v) => b.Flow = (float)(v / 100), b => $"{b.Flow * 100:0} %");
        if (cur.Shape != BrushShape.Pixel)
            AddBrushRow("Espacement", 1, 100, b => b.Spacing * 100, (b, v) => b.Spacing = (float)(v / 100), b => $"{b.Spacing * 100:0} %");
        AddBrushRow("Stabilisation", 0, 20, b => b.Stabilizer, (b, v) => b.Stabilizer = (float)v, b => $"{b.Stabilizer:0}");
        if (image)
        {
            AddBrushRow("Rotation aléatoire", 0, 180, b => b.RotationJitter, (b, v) => b.RotationJitter = (float)v, b => $"{b.RotationJitter:0}°");
            AddBrushRow("Angle", 0, 360, b => b.Angle, (b, v) => b.Angle = (float)v, b => $"{b.Angle:0}°");
        }
        if (!pixelTool && cur.Shape != BrushShape.Pixel)
        {
            if (!image) AddBrushRow("Grain papier", 0, 100, b => b.Grain * 100, (b, v) => b.Grain = (float)(v / 100), b => $"{b.Grain * 100:0} %");
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
        if (cur.Shape is BrushShape.Ellipse or BrushShape.Image)
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
        if (_text != null) UpdateTextPreview();
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
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(c.ToAvalonia()),
                BorderBrush = current ? MortisTheme.Accent : MortisTheme.LineStrong,
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
}
