using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Editor;
using MortisPaint.Engine;

namespace MortisPaint.Views;

public partial class EditorView
{
    FilterSession? _filter;
    DispatcherTimer? _filterTimer;
    bool _filterPending;

    void BuildToolSupport()
    {
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            if (_filter == null || !_filterPending) return;
            _filterPending = false;
            _filter.Preview();
        };
        FilterApply.Click += (_, _) => ApplyFilter();
        FilterCancel.Click += (_, _) => CancelFilter();
    }

    // ================================================================ options contextuelles

    static TextBlock OptLabel(string text) => new()
    {
        Text = text,
        Classes = { "dim" },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 2, 0),
    };

    Control MiniSlider(string label, double min, double max, double value, Action<double> set, Func<double, string> fmt)
    {
        var text = new TextBlock { Text = fmt(value), Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, FontSize = 12 };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            text.Text = fmt(e.NewValue);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { OptLabel(label), slider, text } };
    }

    void ChipGroup(Panel host, string[] labels, int selected, Action<int> onSelect)
    {
        var chips = new List<ToggleButton>();
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var t = new ToggleButton { Classes = { "chip" }, Content = labels[i], IsChecked = i == selected };
            t.Click += (_, _) =>
            {
                for (int j = 0; j < chips.Count; j++) chips[j].IsChecked = j == idx;
                onSelect(idx);
            };
            chips.Add(t);
            host.Children.Add(t);
        }
    }

    ToggleButton ToggleChip(string label, bool value, Action<bool> set)
    {
        var t = new ToggleButton { Classes = { "chip" }, Content = label, IsChecked = value };
        t.Click += (_, _) => set(t.IsChecked == true);
        return t;
    }

    static Button ActionChip(string label, Action action, bool accent = false)
    {
        var b = new Button { Classes = { accent ? "accent" : "chip" }, Content = label };
        if (accent)
        {
            b.Padding = new Thickness(12, 6);
            b.Margin = new Thickness(2);
        }
        b.Click += (_, _) => action();
        return b;
    }

    static Button IconChip(Geometry icon, string tip, Action action)
    {
        var b = new Button { Classes = { "icon", "small" }, Content = Icons.Make(icon, 19) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    void BuildToolOptions()
    {
        var p = ToolOptionsPanel;
        p.Children.Clear();
        switch (_state.Tool)
        {
            case ToolKind.Fill:
                p.Children.Add(MiniSlider("Tolérance", 0, 255, _state.FillTolerance, v => _state.FillTolerance = (int)v, v => $"{v:0}"));
                p.Children.Add(MiniSlider("Étendre", 0, 6, _state.FillExpand, v => _state.FillExpand = (int)Math.Round(v), v => $"{Math.Round(v):0} px"));
                p.Children.Add(OptLabel("Référence"));
                ChipGroup(p, new[] { "Toile", "Calque" }, _state.FillSampleAll ? 0 : 1, i => _state.FillSampleAll = i == 0);
                break;
            case ToolKind.Gradient:
                ChipGroup(p, new[] { "Linéaire", "Radial" }, _state.GradientKind == GradientKind.Linear ? 0 : 1,
                    i => _state.GradientKind = i == 0 ? GradientKind.Linear : GradientKind.Radial);
                p.Children.Add(OptLabel("Vers"));
                ChipGroup(p, new[] { "Transparent", "2ᵉ couleur" }, _state.GradientToTransparent ? 0 : 1, i => _state.GradientToTransparent = i == 0);
                p.Children.Add(MiniSlider("Opacité", 1, 100, _state.FillOpacity * 100, v => _state.FillOpacity = (float)(v / 100), v => $"{v:0}%"));
                break;
            case ToolKind.Eyedropper:
                p.Children.Add(OptLabel("Prélever sur"));
                ChipGroup(p, new[] { "Toile", "Calque" }, _state.PickSampleAll ? 0 : 1, i => _state.PickSampleAll = i == 0);
                break;
            case ToolKind.Shape:
                ChipGroup(p, new[] { "Ligne", "Rectangle", "Ellipse" }, (int)_state.Shape, i => _state.Shape = (ShapeKind)i);
                p.Children.Add(ToggleChip("Rempli", _state.ShapeFilled, v => _state.ShapeFilled = v));
                break;
            case ToolKind.Select:
                ChipGroup(p, new[] { "Rectangle", "Ellipse", "Lasso" }, (int)_state.SelectKind, i => _state.SelectKind = (SelectKind)i);
                p.Children.Add(ActionChip("Tout", () => _engine.Selection.SelectAll()));
                p.Children.Add(ActionChip("Aucune", () => _engine.Selection.Clear()));
                p.Children.Add(ActionChip("Inverser", () => _engine.Selection.Invert()));
                p.Children.Add(ActionChip("Effacer", ClearLayerOrSelection));
                p.Children.Add(ActionChip("Remplir", () => _engine.FillSelection(_state.Color)));
                break;
            case ToolKind.Transform:
                p.Children.Add(IconChip(Icons.Flip, "Miroir horizontal", () => Canvas.TransformFlip(true)));
                p.Children.Add(ActionChip("↕", () => Canvas.TransformFlip(false)));
                p.Children.Add(ActionChip("⟲ 90°", () => Canvas.TransformRotate(-90)));
                p.Children.Add(ActionChip("⟳ 90°", () => Canvas.TransformRotate(90)));
                p.Children.Add(ActionChip("Réinit.", Canvas.TransformReset));
                p.Children.Add(ActionChip("Annuler", Canvas.CancelTransform));
                p.Children.Add(ActionChip("✓ Valider", Canvas.CommitTransform, accent: true));
                break;
            case ToolKind.Hand:
                p.Children.Add(IconChip(Icons.Fit, "Ajuster à l'écran", Canvas.FitToView));
                p.Children.Add(ActionChip("100 %", Canvas.ZoomActualPixels));
                p.Children.Add(ActionChip("Miroir de la vue", Canvas.FlipView));
                p.Children.Add(ActionChip("Rotation 0°", Canvas.ResetRotation));
                break;
        }
        ToolOptions.IsVisible = p.Children.Count > 0 && _filter == null;
    }

    // ================================================================ filtres

    void StartFilter(FilterDef def)
    {
        CommitPending();
        if (!_engine.ActiveLayer.Visible)
        {
            ShowToast("Le calque actif est masqué");
            return;
        }
        _filter = new FilterSession(_engine, def);
        FilterTitle.Text = def.Name + (_engine.Selection.HasSelection ? " (sélection)" : "");
        FilterParams.Children.Clear();
        for (int i = 0; i < def.Params.Length; i++)
        {
            var prm = def.Params[i];
            int idx = i;
            if (prm.Min == 0 && prm.Max == 1 && prm.Step >= 1)
            {
                var cb = new CheckBox { Content = prm.Name, IsChecked = prm.Default >= 0.5f };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (_filter == null) return;
                    _filter.Values[idx] = cb.IsChecked == true ? 1 : 0;
                    ScheduleFilterPreview();
                };
                FilterParams.Children.Add(cb);
                continue;
            }
            string format = prm.Step < 1 ? "0.0" : "0";
            var row = new SliderRow(prm.Name, prm.Min, prm.Max,
                () => _filter?.Values[idx] ?? prm.Default,
                v =>
                {
                    if (_filter == null) return;
                    float step = prm.Step;
                    _filter.Values[idx] = (float)(Math.Round(v / step) * step);
                    ScheduleFilterPreview();
                },
                () => (_filter?.Values[idx] ?? prm.Default).ToString(format) + prm.Unit);
            FilterParams.Children.Add(row.View);
        }
        if (def.Params.Length == 0)
            FilterParams.Children.Add(new TextBlock { Text = "Aucun réglage pour ce filtre.", Classes = { "dim" } });

        FilterPanel.IsVisible = true;
        Canvas.InputLocked = true;
        Sheet.IsVisible = false;
        ToolOptions.IsVisible = false;
        QuickSliders.IsVisible = false;
    }

    void ScheduleFilterPreview()
    {
        _filterPending = true;
        _filterTimer?.Stop();
        _filterTimer?.Start();
    }

    void ApplyFilter()
    {
        if (_filter == null) return;
        _filterTimer?.Stop();
        if (_filterPending) _filter.Preview();
        _filterPending = false;
        var name = _filter.Filter.Name;
        _filter.Commit();
        _filter = null;
        CloseFilterPanel();
        ShowToast("Filtre appliqué : " + name);
    }

    void CancelFilter()
    {
        if (_filter == null) return;
        _filterTimer?.Stop();
        _filterPending = false;
        _filter.Cancel();
        _filter = null;
        CloseFilterPanel();
    }

    void CloseFilterPanel()
    {
        FilterPanel.IsVisible = false;
        Canvas.InputLocked = false;
        Sheet.IsVisible = !_wide && _sheetOpen;
        BuildToolOptions();
        UpdateQuickSliders();
    }
}
