using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Editor;
using MortisPaint.Engine;
using SkiaSharp;

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

    static Border OptSeparator() => new()
    {
        Width = 1,
        Height = 20,
        Background = MortisTheme.LineStrong,
        Margin = new Thickness(4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    Control MiniSlider(string label, double min, double max, double value, Action<double> set, Func<double, string> fmt)
    {
        var text = new TextBlock { Text = fmt(value), Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, FontSize = 12 };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 100, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            text.Text = fmt(e.NewValue);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { OptLabel(label), slider, text } };
    }

    static void ChipGroup(Panel host, string[] labels, int selected, Action<int> onSelect)
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

    static ToggleButton ToggleChip(string label, bool value, Action<bool> set)
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
        var b = new Button { Classes = { "icon", "small" }, Content = Icons.Make(icon, 18) };
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
                p.Children.Add(OptLabel("Réf."));
                ChipGroup(p, new[] { "Toile", "Calque" }, _state.FillSampleAll ? 0 : 1, i => _state.FillSampleAll = i == 0);
                p.Children.Add(OptSeparator());
                ChipGroup(p, Tones.All.Select(t => t.Label).ToArray(), Array.FindIndex(Tones.All, t => t.Kind == _state.FillTone), i =>
                {
                    _state.FillTone = Tones.All[i].Kind;
                    BuildToolOptions();
                });
                if (_state.FillTone != ToneKind.Solid)
                {
                    p.Children.Add(MiniSlider("Trame", 3, 40, _state.ToneCell, v => _state.ToneCell = (float)v, v => $"{v:0} px"));
                    p.Children.Add(MiniSlider("Densité", 5, 90, _state.ToneDensity * 100, v => _state.ToneDensity = (float)(v / 100), v => $"{v:0} %"));
                    if (_state.FillTone != ToneKind.Grain)
                        p.Children.Add(MiniSlider("Angle", 0, 90, _state.ToneAngle, v => _state.ToneAngle = (float)v, v => $"{v:0}°"));
                }
                break;
            case ToolKind.Gradient:
                ChipGroup(p, new[] { "Linéaire", "Radial" }, _state.GradientKind == GradientKind.Linear ? 0 : 1,
                    i => _state.GradientKind = i == 0 ? GradientKind.Linear : GradientKind.Radial);
                p.Children.Add(OptLabel("Vers"));
                ChipGroup(p, new[] { "Transparent", "2ᵉ couleur" }, _state.GradientToTransparent ? 0 : 1, i => _state.GradientToTransparent = i == 0);
                p.Children.Add(MiniSlider("Opacité", 1, 100, _state.FillOpacity * 100, v => _state.FillOpacity = (float)(v / 100), v => $"{v:0} %"));
                break;
            case ToolKind.Eyedropper:
                p.Children.Add(OptLabel("Prélever sur"));
                ChipGroup(p, new[] { "Toile", "Calque" }, _state.PickSampleAll ? 0 : 1, i => _state.PickSampleAll = i == 0);
                break;
            case ToolKind.Shape:
                ChipGroup(p, new[] { "Ligne", "Rectangle", "Ellipse" }, (int)_state.Shape, i => _state.Shape = (ShapeKind)i);
                p.Children.Add(ToggleChip("Rempli", _state.ShapeFilled, v => _state.ShapeFilled = v));
                break;
            case ToolKind.Text:
                p.Children.Add(OptLabel("Touche la toile pour écrire"));
                break;
            case ToolKind.Select:
                ChipGroup(p, new[] { "Rectangle", "Ellipse", "Lasso", "Baguette" }, (int)_state.SelectKind, i =>
                {
                    _state.SelectKind = (SelectKind)i;
                    BuildToolOptions();
                });
                p.Children.Add(OptSeparator());
                ChipGroup(p, new[] { "Nouvelle", "Ajouter", "Retirer" }, (int)_state.SelectOp, i => _state.SelectOp = (SelectionOp)i);
                if (_state.SelectKind == SelectKind.Wand)
                    p.Children.Add(MiniSlider("Tolérance", 0, 255, _state.FillTolerance, v => _state.FillTolerance = (int)v, v => $"{v:0}"));
                p.Children.Add(OptSeparator());
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
        ToolOptions.IsVisible = p.Children.Count > 0 && _filter == null && _text == null;
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

    // ================================================================ texte

    TextSession? _text;
    SliderRow? _textSizeRow;
    readonly List<ToggleButton> _alignChips = new();
    bool _syncText;

    void BuildTextPanel()
    {
        var families = SKFontManager.Default.FontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith('@'))
            .Distinct().OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!families.Contains(_state.TextFamily)) families.Insert(0, _state.TextFamily);
        FontCombo.ItemsSource = families;
        FontCombo.SelectedItem = _state.TextFamily;
        FontCombo.SelectionChanged += (_, _) =>
        {
            if (_syncText || FontCombo.SelectedItem is not string f) return;
            _state.TextFamily = f;
            UpdateTextPreview();
        };
        _textSizeRow = new SliderRow("Taille", 8, 600, () => _state.TextSize, v =>
        {
            _state.TextSize = (float)v;
            UpdateTextPreview();
        }, () => $"{_state.TextSize:0} px");
        TextSizeHost.Children.Add(_textSizeRow.View);
        BoldToggle.Click += (_, _) => { _state.TextBold = BoldToggle.IsChecked == true; UpdateTextPreview(); };
        ItalicToggle.Click += (_, _) => { _state.TextItalic = ItalicToggle.IsChecked == true; UpdateTextPreview(); };
        foreach (var (label, i) in new[] { ("Gauche", 0), ("Centre", 1), ("Droite", 2) })
        {
            var t = new ToggleButton { Classes = { "chip" }, Content = label, IsChecked = _state.TextAlign == i };
            t.Click += (_, _) =>
            {
                _state.TextAlign = i;
                for (int k = 0; k < _alignChips.Count; k++) _alignChips[k].IsChecked = k == i;
                UpdateTextPreview();
            };
            _alignChips.Add(t);
            TextAlignHost.Children.Add(t);
        }
        TextField.TextChanged += (_, _) => UpdateTextPreview();
        TextApply.Click += (_, _) => CommitText();
        TextCancel.Click += (_, _) => CancelText();
    }

    TextStyle CurrentTextStyle() => new(TextField.Text ?? "", _state.TextFamily, _state.TextSize, _state.TextBold, _state.TextItalic, _state.TextAlign, _state.Color);

    void BeginText(SKPoint at)
    {
        if (_text != null) return;
        if (!_engine.ActiveLayer.Visible)
        {
            ShowToast("Le calque actif est masqué");
            return;
        }
        _syncText = true;
        TextField.Text = "";
        BoldToggle.IsChecked = _state.TextBold;
        ItalicToggle.IsChecked = _state.TextItalic;
        _textSizeRow?.Refresh();
        _syncText = false;
        _text = new TextSession(_engine, CurrentTextStyle(), at);
        Canvas.Text = _text;
        TextPanel.IsVisible = true;
        ToolOptions.IsVisible = false;
        QuickSliders.IsVisible = false;
        Sheet.IsVisible = false;
        Dispatcher.UIThread.Post(() => TextField.Focus());
    }

    void UpdateTextPreview()
    {
        if (_text == null || _syncText) return;
        _text.Update(CurrentTextStyle(), _text.Origin);
    }

    void CommitText()
    {
        if (_text == null) return;
        var session = _text;
        _text = null;
        Canvas.Text = null;
        if (string.IsNullOrWhiteSpace(session.Style.Text)) session.Cancel();
        else session.Commit();
        CloseTextPanel();
    }

    void CancelText()
    {
        if (_text == null) return;
        _text.Cancel();
        _text = null;
        Canvas.Text = null;
        CloseTextPanel();
    }

    void CloseTextPanel()
    {
        TextPanel.IsVisible = false;
        Sheet.IsVisible = !_wide && _sheetOpen;
        BuildToolOptions();
        UpdateQuickSliders();
    }

    // ================================================================ taille de la toile

    // Même plafond que la création d'une toile dans la galerie (6000 × 6000).
    const long MaxCanvasPixels = 6000L * 6000;
    static readonly long MaxCanvasBytes = OperatingSystem.IsAndroid() ? 800L << 20 : 3000L << 20;

    bool _canvasScale;
    float _anchorX = 0.5f, _anchorY = 0.5f;
    readonly List<ToggleButton> _anchorButtons = new();
    bool _syncCanvas;

    void BuildCanvasDialog()
    {
        for (int i = 0; i < 9; i++)
        {
            int ax = i % 3, ay = i / 3;
            var b = new ToggleButton { Classes = { "chip" }, Margin = new Thickness(2), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsChecked = ax == 1 && ay == 1 };
            b.Click += (_, _) =>
            {
                _anchorX = ax / 2f;
                _anchorY = ay / 2f;
                for (int k = 0; k < _anchorButtons.Count; k++) _anchorButtons[k].IsChecked = k == ay * 3 + ax;
            };
            _anchorButtons.Add(b);
            AnchorGrid.Children.Add(b);
        }
        CanvasW.TextChanged += (_, _) => SyncRatio(fromWidth: true);
        CanvasH.TextChanged += (_, _) => SyncRatio(fromWidth: false);
        CanvasCancel.Click += (_, _) => CanvasDialog.IsVisible = false;
        CanvasApply.Click += async (_, _) =>
        {
            if (!int.TryParse(CanvasW.Text, out var w) || !int.TryParse(CanvasH.Text, out var h) || w < 16 || h < 16 || w > PsdFile.MaxSide || h > PsdFile.MaxSide)
            {
                ShowToast($"Taille entre 16 et {PsdFile.MaxSide} px");
                return;
            }
            var doc = _engine.Document;
            // Garde mémoire : chaque calque coûte 4 octets par pixel, plus la composition et l'aperçu.
            long bytes = (long)w * h * 4 * (doc.Layers.Count + 3);
            if ((long)w * h > MaxCanvasPixels || bytes > MaxCanvasBytes)
            {
                ShowToast($"Toile trop grande pour {doc.Layers.Count} calque(s) : environ {bytes / (1024 * 1024)} Mo");
                return;
            }
            CanvasDialog.IsVisible = false;
            await System.Threading.Tasks.Task.Yield();
            try
            {
                ReplaceDocument(_canvasScale ? CanvasOps.Scale(doc, w, h) : CanvasOps.Resize(doc, w, h, _anchorX, _anchorY));
                ShowToast($"Toile : {w} × {h}");
            }
            catch (Exception ex)
            {
                ShowToast("Changement de taille impossible : " + ex.Message);
            }
        };
    }

    void SyncRatio(bool fromWidth)
    {
        if (_syncCanvas || KeepRatio.IsChecked != true) return;
        var doc = _engine.Document;
        _syncCanvas = true;
        if (fromWidth && int.TryParse(CanvasW.Text, out var w))
            CanvasH.Text = Math.Max(1, (int)Math.Round(w * doc.Height / (double)doc.Width)).ToString();
        else if (!fromWidth && int.TryParse(CanvasH.Text, out var h))
            CanvasW.Text = Math.Max(1, (int)Math.Round(h * doc.Width / (double)doc.Height)).ToString();
        _syncCanvas = false;
    }

    void ShowCanvasDialog(bool scale)
    {
        CommitPending();
        _canvasScale = scale;
        var doc = _engine.Document;
        CanvasDialogTitle.Text = scale ? "Redimensionner l'image" : "Taille de la toile";
        CanvasDialogHint.Text = scale
            ? $"Tout le dessin est agrandi ou réduit. Taille actuelle : {doc.Width} × {doc.Height}."
            : $"Le dessin garde sa taille ; la toile s'agrandit ou se recadre autour du point d'ancrage. Taille actuelle : {doc.Width} × {doc.Height}.";
        AnchorBox.IsVisible = !scale;
        KeepRatio.IsChecked = scale;
        _syncCanvas = true;
        CanvasW.Text = doc.Width.ToString();
        CanvasH.Text = doc.Height.ToString();
        _syncCanvas = false;
        CanvasDialog.IsVisible = true;
    }

    async void RotateCanvas(int quarterTurns)
    {
        try
        {
            CommitPending();
            if (!await ConfirmResetHistory()) return;
            ReplaceDocument(CanvasOps.Rotate(_engine.Document, quarterTurns));
            Canvas.FitToView();
        }
        catch (Exception ex)
        {
            ShowToast("Rotation impossible : " + ex.Message);
        }
    }

    async void CropToSelection()
    {
        try
        {
            CommitPending();
            var sel = _engine.Selection;
            if (sel.Path is not { } path || sel.IsInverse)
            {
                ShowToast("Fais d'abord une sélection");
                return;
            }
            // Bornes exactes de la sélection (Selection.Bounds garde une marge pour l'anticrénelage),
            // limitées à la toile : une sélection déplacée peut en sortir.
            var b = path.Bounds;
            var rect = BitmapUtil.Clamp(new SKRectI((int)MathF.Floor(b.Left), (int)MathF.Floor(b.Top),
                (int)MathF.Ceiling(b.Right), (int)MathF.Ceiling(b.Bottom)), _engine.Width, _engine.Height);
            if (rect.Width < 1 || rect.Height < 1)
            {
                ShowToast("La sélection est en dehors de la toile");
                return;
            }
            if (!await ConfirmResetHistory()) return;
            ReplaceDocument(CanvasOps.Crop(_engine.Document, rect));
            Canvas.FitToView();
            ShowToast($"Toile recadrée : {rect.Width} × {rect.Height}");
        }
        catch (Exception ex)
        {
            ShowToast("Recadrage impossible : " + ex.Message);
        }
    }

    async System.Threading.Tasks.Task<bool> ConfirmResetHistory()
    {
        if (!_engine.History.CanUndo) return true;
        return await MainView.Current!.ConfirmAsync("Modifier la toile ?",
            "Cette opération change la taille de la toile : l'historique d'annulation repart de zéro.", "Continuer");
    }
}
