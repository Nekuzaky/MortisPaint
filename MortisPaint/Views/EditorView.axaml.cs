using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Editor;
using MortisPaint.Engine;
using MortisPaint.Services;
using SkiaSharp;

namespace MortisPaint.Views;

/// <summary>Éditeur de dessin : disposition responsive (rail + panneau latéral sur grand écran, barre + tiroir sur mobile).</summary>
public partial class EditorView : UserControl
{
    enum Tab { Brush, Color, Layers }

    const double WideThreshold = 760;

    PaintEngine _engine;
    readonly EditorState _state = new();
    readonly Dictionary<ToolKind, Button> _toolButtons = new();
    readonly DispatcherTimer _toastTimer;
    TopLevel? _top;
    bool _wide = true;
    bool _layoutInit;
    Tab _tab = Tab.Brush;
    bool _sheetOpen;
    bool _closed;

    // Timelapse
    TimelapseRecorder? _timelapse;
    readonly DispatcherTimer _timelapseTimer;

    public static readonly FilePickerFileType GifFileType = new("GIF animé")
    {
        Patterns = new[] { "*.gif" },
        MimeTypes = new[] { "image/gif" },
        AppleUniformTypeIdentifiers = new[] { "com.compuserve.gif" },
    };

    public EditorView(Document doc)
    {
        InitializeComponent();
        _engine = new PaintEngine(doc);
        _engine.Ruler.CenterX = doc.Width / 2f;
        _engine.Ruler.CenterY = doc.Height / 2f;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.IsVisible = false;
        };
        _timelapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _timelapseTimer.Tick += (_, _) =>
        {
            _timelapseTimer.Stop();
            // Sans pelures d'oignon : le timelapse montre le dessin, pas l'aide à l'animation.
            if (_timelapse != null && !_closed) _timelapse.Capture(_engine.Document, _engine.CurrentFrameFilter(withOnion: false));
        };

        Canvas.Attach(_engine, _state);
        BuildToolButtons();
        BuildMenus();
        BuildBrushPanel();
        BuildColorPanel();
        BuildLayersPanel();
        BuildToolSupport();
        BuildTextPanel();
        BuildCanvasDialog();
        BuildAnimation();
        BuildReference();

        BackButton.Click += async (_, _) => await MainView.Current!.CloseEditorAsync();
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        TabBrush.Click += (_, _) => ShowTab(Tab.Brush, toggle: true);
        TabColor.Click += (_, _) => ShowTab(Tab.Color, toggle: true);
        TabLayers.Click += (_, _) => ShowTab(Tab.Layers, toggle: true);
        SheetClose.Click += (_, _) => SetSheetOpen(false);
        BottomBrushButton.Click += (_, _) => ShowTab(Tab.Brush, toggle: true);
        BottomColorButton.Click += (_, _) => ShowTab(Tab.Color, toggle: true);
        BottomLayersButton.Click += (_, _) => ShowTab(Tab.Layers, toggle: true);
        RailColorButton.Click += (_, _) => ShowTab(Tab.Color, toggle: false);
        AnimationButton.Click += (_, _) => ToggleAnimation();
        ReferenceButton.Click += (_, _) => ToggleReference();

        Canvas.ViewChanged += UpdateZoomText;
        Canvas.CanvasPressed += () =>
        {
            if (!_wide) SetSheetOpen(false);
        };
        Canvas.UndoGesture += Undo;
        Canvas.RedoGesture += Redo;
        Canvas.Message += ShowToast;
        Canvas.BrushSizeChangedByWheel += () => ShowToast($"Taille : {_state.CurrentBrush.Size:0.#} px");
        Canvas.TextRequested += BeginText;

        BindEngine(_engine);

        _state.ToolChanged += OnToolChanged;
        _state.BrushChanged += OnBrushChanged;
        _state.BrushEdited += OnBrushEdited;
        _state.ColorChanged += OnColorChanged;
        _state.PaletteChanged += RebuildSwatches;
        MortisTheme.Changed += OnThemeChanged;

        SizeChanged += (_, e) => ApplyLayout(e.NewSize);

        StartTimelapse();
        OnToolChanged();
        OnColorChanged();
        UpdateUndoButtons();
        UpdateTitle();
        UpdateZoomText();
        UpdateAnimationUi();
        ShowTab(Tab.Brush, toggle: false);
    }

    void BindEngine(PaintEngine e)
    {
        e.History.Changed += OnHistoryChanged;
        e.StructureChanged += OnStructureChanged;
        e.LayerPixelsChanged += OnLayerPixelsChanged;
        e.Selection.Changed += UpdateTitle;
    }

    void UnbindEngine(PaintEngine e)
    {
        e.History.Changed -= OnHistoryChanged;
        e.StructureChanged -= OnStructureChanged;
        e.LayerPixelsChanged -= OnLayerPixelsChanged;
        e.Selection.Changed -= UpdateTitle;
    }

    /// <summary>Remplace le document (changement de taille de toile) : nouveau moteur, historique vidé.</summary>
    void ReplaceDocument(Document doc)
    {
        CommitPending();
        StopPlayback();
        var old = _engine;
        UnbindEngine(old);
        Canvas.Detach();
        _engine = new PaintEngine(doc);
        _engine.Symmetry.Mode = old.Symmetry.Mode;
        _engine.Symmetry.RadialCount = old.Symmetry.RadialCount;
        _engine.Ruler.Mode = old.Ruler.Mode;
        _engine.Ruler.AngleDegrees = old.Ruler.AngleDegrees;
        _engine.Ruler.CenterX = doc.Width / 2f;
        _engine.Ruler.CenterY = doc.Height / 2f;
        DisposeThumbs();
        _frameThumbs.Clear();
        BindEngine(_engine);
        Canvas.Attach(_engine, _state);
        old.Dispose();
        _engine.Document.IsDirty = true;
        RebuildLayers();
        UpdateTitle();
        UpdateUndoButtons();
        UpdateAnimationUi();
        ScheduleTimelapse();
    }

    // ================================================================ cycle de vie

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _top?.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        ApplyLayout(Bounds.Size.Width > 0 ? Bounds.Size : _top?.ClientSize ?? new Size(1200, 800));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _top?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _top?.RemoveHandler(KeyUpEvent, OnKeyUp);
        _top = null;
    }

    /// <summary>Gère le bouton retour Android. Retourne false si l'éditeur doit se fermer.</summary>
    public bool HandleBack()
    {
        if (CanvasDialog.IsVisible) { CanvasDialog.IsVisible = false; return true; }
        if (FilterPanel.IsVisible) { CancelFilter(); return true; }
        if (TextPanel.IsVisible) { CancelText(); return true; }
        if (!_wide && _sheetOpen) { SetSheetOpen(false); return true; }
        if (RefWindow.IsVisible) { HideReference(); return true; }
        if (Canvas.Transform != null) { Canvas.CancelTransform(); return true; }
        return false;
    }

    void CommitPending()
    {
        FlushOpacityEdit();
        if (_filter != null) ApplyFilter();
        if (Canvas.Transform != null) Canvas.CommitTransform();
        if (_text != null) CommitText();
    }

    /// <summary>Valide ce qui est en cours et enregistre. Retourne le message d'erreur, ou null si tout va bien.</summary>
    public string? AutoSave()
    {
        if (_closed) return null;
        CommitPending();
        // La lecture coupe les pelures d'oignon : on la stoppe pour enregistrer le vrai réglage.
        StopPlayback();
        var doc = _engine.Document;
        if (!doc.IsDirty && doc.ProjectPath != null) return null;
        try
        {
            GalleryStore.Save(_engine);
            return null;
        }
        catch (Exception ex)
        {
            ShowToast("Erreur d'enregistrement : " + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>
    /// Mise en arrière-plan (Android) : enregistre sans valider ce qui est en cours. L'application peut
    /// être tuée en arrière-plan ; si on revient, le filtre, le texte ou la transformation sont toujours là.
    /// </summary>
    public void SaveForSuspend()
    {
        if (_closed) return;
        FlushOpacityEdit();
        StopPlayback();
        var doc = _engine.Document;
        if (!doc.IsDirty && doc.ProjectPath != null) return;
        // Le filtre et la transformation travaillent directement dans le calque : on enregistre
        // ses pixels d'origine, puis on réaffiche l'aperçu. Le texte n'est qu'un aperçu superposé.
        var transform = Canvas.Transform;
        var filter = _filter;
        transform?.ShowOriginal();
        filter?.ShowOriginal();
        try
        {
            GalleryStore.Save(_engine);
        }
        catch (Exception ex)
        {
            ShowToast("Erreur d'enregistrement : " + ex.Message);
        }
        finally
        {
            filter?.ShowPreview();
            transform?.ShowTransformed();
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        StopPlayback();
        CloseAnimation();
        // Toutes les minuteries : une seule qui tourne encore lirait des calques déjà libérés.
        _timelapseTimer.Stop();
        _toastTimer.Stop();
        _thumbTimer?.Stop();
        _opacityTimer?.Stop();
        _filterTimer?.Stop();
        _brushPreviewTimer?.Stop();
        MortisTheme.Changed -= OnThemeChanged;
        // Fermeture sans enregistrement réussi : on abandonne ce qui est en cours.
        _filter?.Cancel();
        _filter = null;
        _text?.Cancel();
        _text = null;
        if (Canvas.Transform != null) Canvas.CancelTransform();
        UnbindEngine(_engine);
        Canvas.Detach();
        DisposeThumbs();
        foreach (var bmp in _brushPreviews.Values) bmp.Dispose();
        _brushPreviews.Clear();
        RefView.Release();
        _engine.Dispose();
    }

    void SaveNow()
    {
        CommitPending();
        StopPlayback();
        try
        {
            GalleryStore.Save(_engine);
            ShowToast("Enregistré dans la galerie");
        }
        catch (Exception ex)
        {
            ShowToast("Erreur d'enregistrement : " + ex.Message);
        }
    }

    void OnThemeChanged()
    {
        RebuildBrushList();
        RebuildSwatches();
        RebuildLayers();
        RebuildTimeline();
        foreach (var (kind, btn) in _toolButtons)
            btn.Classes.Set("active", kind == _state.Tool);
    }

    // ================================================================ timelapse

    void StartTimelapse()
    {
        if (!_state.Timelapse) { _timelapse = null; RecBadge.IsVisible = false; return; }
        _timelapse = new TimelapseRecorder(Path.Combine(AppPaths.Root, "Timelapse", _engine.Document.Id.ToString("N")));
        RecBadge.IsVisible = true;
        if (_timelapse.FrameCount == 0) ScheduleTimelapse();
    }

    void ScheduleTimelapse()
    {
        if (_timelapse == null || _playing) return;
        _timelapseTimer.Stop();
        _timelapseTimer.Start();
    }

    async Task ClearTimelapseAsync()
    {
        if (_timelapse is not { } recorder) return;
        if (!await MainView.Current!.ConfirmAsync("Effacer le timelapse ?",
                "Toutes les images enregistrées de cette œuvre seront supprimées. Le dessin n'est pas touché.", "Effacer")) return;
        try
        {
            await recorder.FlushAsync();
            recorder.Clear();
            ShowToast("Timelapse effacé");
        }
        catch (Exception ex)
        {
            ShowToast("Effacement impossible : " + ex.Message);
        }
    }

    async Task ExportTimelapseAsync()
    {
        CommitPending();
        if (_timelapse == null)
        {
            ShowToast("Le timelapse est désactivé (Préférences)");
            return;
        }
        try
        {
            await _timelapse.FlushAsync();
            var frames = _timelapse.Frames();
            if (frames.Count < 2)
            {
                ShowToast("Pas encore assez d'images : dessine un peu !");
                return;
            }
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exporter le timelapse",
                SuggestedFileName = _engine.Document.Name + " - timelapse.gif",
                DefaultExtension = "gif",
                FileTypeChoices = new[] { GifFileType },
            });
            if (file == null) return;
            ShowToast("Création du timelapse…");
            var bytes = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                TimelapseRecorder.ExportGif(frames, ms, 540, 240, 6);
                return ms.ToArray();
            });
            await using var s = await file.OpenWriteAsync();
            await s.WriteAsync(bytes);
            ShowToast($"Timelapse exporté ({frames.Count} étapes)");
        }
        catch (Exception ex)
        {
            ShowToast("Export impossible : " + ex.Message);
        }
    }

    // ================================================================ disposition responsive

    void ApplyLayout(Size size)
    {
        if (size.Width <= 0) return;
        bool wide = size.Width >= WideThreshold;
        SideHost.Width = size.Width >= 1150 ? 330 : 300;
        WideActions.IsVisible = size.Width >= 560;
        // Sur téléphone, la bande des images d'animation passe sur sa propre ligne.
        bool narrow = size.Width < 560;
        Grid.SetRow(FrameScroll, narrow ? 1 : 0);
        Grid.SetColumn(FrameScroll, narrow ? 0 : 1);
        Grid.SetColumnSpan(FrameScroll, narrow ? 3 : 1);
        FrameScroll.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        if (!wide) Sheet.Height = Math.Clamp((size.Height - 110) * 0.62, 240, 560);
        if (_layoutInit && wide == _wide) return;
        _layoutInit = true;
        _wide = wide;

        ToolRail.IsVisible = wide;
        SideHost.IsVisible = wide;
        BottomBar.IsVisible = !wide;
        SubtitleText.IsVisible = size.Width > 420;

        var target = wide ? ToolRailPanel : ToolBottomPanel;
        foreach (var (kind, _, _) in EditorState.ToolList)
        {
            var b = _toolButtons[kind];
            (b.Parent as Panel)?.Children.Remove(b);
            target.Children.Add(b);
        }

        if (wide)
        {
            Sheet.Child = null;
            SideHost.Child = PanelsRoot;
            Sheet.IsVisible = false;
            SheetClose.IsVisible = false;
            _sheetOpen = false;
        }
        else
        {
            SideHost.Child = null;
            Sheet.Child = PanelsRoot;
            SheetClose.IsVisible = true;
            Sheet.IsVisible = _sheetOpen;
        }
        UpdateTabs();
    }

    void ShowTab(Tab tab, bool toggle)
    {
        if (!_wide)
        {
            if (toggle && _sheetOpen && _tab == tab)
            {
                SetSheetOpen(false);
                return;
            }
            _tab = tab;
            SetSheetOpen(true);
        }
        else
        {
            _tab = tab;
        }
        UpdateTabs();
    }

    void SetSheetOpen(bool open)
    {
        if (_wide) return;
        _sheetOpen = open;
        Sheet.IsVisible = open && _filter == null && _text == null;
    }

    void UpdateTabs()
    {
        BrushPage.IsVisible = _tab == Tab.Brush;
        ColorPage.IsVisible = _tab == Tab.Color;
        LayersPage.IsVisible = _tab == Tab.Layers;
        TabBrush.IsChecked = _tab == Tab.Brush;
        TabColor.IsChecked = _tab == Tab.Color;
        TabLayers.IsChecked = _tab == Tab.Layers;
        if (_tab == Tab.Layers) RefreshAllThumbnails();
    }

    // ================================================================ outils

    static Geometry IconFor(ToolKind k) => k switch
    {
        ToolKind.Brush => Icons.Brush,
        ToolKind.Eraser => Icons.Eraser,
        ToolKind.Smudge => Icons.Smudge,
        ToolKind.Blur => Icons.Blur,
        ToolKind.Fill => Icons.Fill,
        ToolKind.Gradient => Icons.Gradient,
        ToolKind.Eyedropper => Icons.Eyedropper,
        ToolKind.Shape => Icons.Shapes,
        ToolKind.Text => Icons.Text,
        ToolKind.Select => Icons.Select,
        ToolKind.Transform => Icons.Move,
        _ => Icons.Hand,
    };

    void BuildToolButtons()
    {
        foreach (var (kind, label, key) in EditorState.ToolList)
        {
            var b = new Button { Classes = { "icon" }, Content = Icons.Make(IconFor(kind), 21) };
            ToolTip.SetTip(b, $"{label} ({key})");
            b.Click += (_, _) => SelectTool(kind);
            _toolButtons[kind] = b;
            ToolRailPanel.Children.Add(b);
        }
    }

    void SelectTool(ToolKind tool)
    {
        if (_filter != null) return;
        if (_state.Tool == tool)
        {
            if (EditorState.IsBrushTool(tool)) ShowTab(Tab.Brush, toggle: true);
            else if (tool == ToolKind.Transform && Canvas.Transform == null) Canvas.BeginTransform();
            return;
        }
        _state.SetTool(tool);
    }

    void OnToolChanged()
    {
        foreach (var (kind, btn) in _toolButtons)
            btn.Classes.Set("active", kind == _state.Tool);
        if (_state.Tool != ToolKind.Text && _text != null) CommitText();
        Canvas.OnToolChanged();
        if (_state.Tool == ToolKind.Transform) Canvas.BeginTransform();
        BuildToolOptions();
        UpdateQuickSliders();
        if (_state.Tool == ToolKind.Text) ShowToast("Touche la toile pour placer un texte");
    }

    // ================================================================ historique

    void Undo()
    {
        if (_filter != null || _text != null) return;
        FlushOpacityEdit();
        if (Canvas.Transform != null)
        {
            Canvas.CancelTransform();
            return;
        }
        var name = _engine.History.UndoName;
        if (_engine.History.Undo()) ShowToast("Annulé : " + name);
    }

    void Redo()
    {
        if (_filter != null || _text != null) return;
        FlushOpacityEdit();
        if (Canvas.Transform != null) Canvas.CommitTransform();
        var name = _engine.History.RedoName;
        if (_engine.History.Redo()) ShowToast("Rétabli : " + name);
    }

    void OnHistoryChanged()
    {
        UpdateUndoButtons();
        ScheduleTimelapse();
    }

    void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _engine.History.CanUndo;
        RedoButton.IsEnabled = _engine.History.CanRedo;
    }

    void OnStructureChanged()
    {
        if (_playing) return;
        RebuildLayers();
        UpdateTitle();
        UpdateAnimationUi();
    }

    void UpdateTitle()
    {
        var doc = _engine.Document;
        TitleText.Text = doc.Name;
        var frame = _engine.CurrentFrame;
        SubtitleText.Text = $"{doc.Width}×{doc.Height} · {doc.ActiveLayer.Name}"
                            + (frame != null ? $" · {frame.Name}" : "")
                            + (_engine.Selection.HasSelection ? " · sélection" : "");
    }

    void UpdateZoomText()
    {
        var z = Canvas.DisplayZoomPercent;
        var rot = Canvas.RotationDegrees % 360;
        if (rot < 0) rot += 360;
        ZoomText.Text = $"{z:0} %" + (Math.Abs(rot) > 0.5 && Math.Abs(rot - 360) > 0.5 ? $" · {rot:0}°" : "") + (Canvas.IsViewFlipped ? " · miroir" : "");
    }

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ================================================================ menus

    static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var mi = new MenuItem { Header = header };
        if (gesture != null) mi.InputGesture = KeyGesture.Parse(gesture);
        mi.Click += (_, _) => action();
        return mi;
    }

    MenuItem Toggle(string header, Func<bool> get, Action<bool> set)
    {
        var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        mi.Click += (_, _) =>
        {
            set(!get());
            mi.IsChecked = get();
        };
        return mi;
    }

    /// <summary>Commande qui modifie le document : valide d'abord le filtre, le texte ou la transformation en cours.</summary>
    Action Cmd(Action action) => () =>
    {
        CommitPending();
        action();
    };

    void BuildMenus()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Enregistrer", SaveNow, "Ctrl+S"));
        var export = new MenuItem { Header = "Exporter" };
        export.Items.Add(Item("Image PNG…", () => _ = ExportAsync(SKEncodedImageFormat.Png), "Ctrl+E"));
        export.Items.Add(Item("Image JPEG…", () => _ = ExportAsync(SKEncodedImageFormat.Jpeg)));
        export.Items.Add(Item("PSD à calques (ibisPaint, Photoshop)…", () => _ = ExportPsdAsync(), "Ctrl+Shift+E"));
        export.Items.Add(Item("Animation en GIF…", () => _ = ExportAnimationGifAsync()));
        export.Items.Add(Item("Timelapse en GIF…", () => _ = ExportTimelapseAsync()));
        menu.Items.Add(export);
        menu.Items.Add(Item("Importer une image…", () => _ = ImportImageAsync(), "Ctrl+I"));
        menu.Items.Add(new Separator());

        var filters = new MenuItem { Header = "Filtres" };
        foreach (var group in Filters.All.GroupBy(f => f.Category))
        {
            var gi = new MenuItem { Header = group.Key };
            foreach (var f in group) gi.Items.Add(Item(f.Name, () => StartFilter(f)));
            filters.Items.Add(gi);
        }
        menu.Items.Add(filters);

        var sel = new MenuItem { Header = "Sélection" };
        sel.Items.Add(Item("Tout sélectionner", Cmd(() => _engine.Selection.SelectAll()), "Ctrl+A"));
        sel.Items.Add(Item("Désélectionner", Cmd(() => _engine.Selection.Clear()), "Ctrl+D"));
        sel.Items.Add(Item("Inverser la sélection", Cmd(() => _engine.Selection.Invert()), "Ctrl+Shift+I"));
        sel.Items.Add(Item("Sélectionner le contenu du calque", Cmd(() =>
        {
            if (!_engine.SelectLayerContent(_state.SelectOp)) ShowToast("Le calque est vide");
        })));
        sel.Items.Add(new Separator());
        sel.Items.Add(Item("Effacer le contenu", ClearLayerOrSelection, "Delete"));
        sel.Items.Add(Item("Remplir avec la couleur", Cmd(() => _engine.FillSelection(_state.Color))));
        sel.Items.Add(Item("Remplir avec la trame", FillWithTone));
        sel.Items.Add(Item("Recadrer la toile sur la sélection", CropToSelection));
        menu.Items.Add(sel);

        var canvas = new MenuItem { Header = "Toile" };
        canvas.Items.Add(Item("Taille de la toile…", () => ShowCanvasDialog(scale: false)));
        canvas.Items.Add(Item("Redimensionner l'image…", () => ShowCanvasDialog(scale: true)));
        canvas.Items.Add(Item("Pivoter de 90° à droite", () => RotateCanvas(1)));
        canvas.Items.Add(Item("Pivoter de 90° à gauche", () => RotateCanvas(-1)));
        canvas.Items.Add(Item("Pivoter de 180°", () => RotateCanvas(2)));
        canvas.Items.Add(Item("Retourner horizontalement", Cmd(() => _engine.FlipCanvas(true))));
        canvas.Items.Add(Item("Retourner verticalement", Cmd(() => _engine.FlipCanvas(false))));
        canvas.Items.Add(new Separator());
        canvas.Items.Add(Item("Ajuster à l'écran", Canvas.FitToView, "Ctrl+D0"));
        canvas.Items.Add(Item("Taille réelle (100 %)", Canvas.ZoomActualPixels, "Ctrl+D1"));
        canvas.Items.Add(Item("Retourner la vue (miroir)", Canvas.FlipView, "F"));
        canvas.Items.Add(Item("Réinitialiser la rotation", Canvas.ResetRotation));
        canvas.Items.Add(new Separator());
        canvas.Items.Add(Item("Fusionner les calques visibles", Cmd(() => _engine.MergeVisible())));
        canvas.Items.Add(Item("Aplatir l'image", Cmd(() => _engine.FlattenAll())));
        canvas.Items.Add(Item("Renommer l'œuvre…", () => _ = RenameAsync()));
        menu.Items.Add(canvas);

        var tools = new MenuItem { Header = "Assistants" };
        tools.Items.Add(Item("Fenêtre de référence", ToggleReference));
        tools.Items.Add(Item("Mode animation", ToggleAnimation));
        tools.Items.Add(Item("Règles et symétrie…", () => GuidesButton.Flyout?.ShowAt(MenuButton)));
        menu.Items.Add(tools);

        var timelapse = new MenuItem { Header = "Timelapse" };
        timelapse.Items.Add(Toggle("Enregistrer le timelapse", () => _state.Timelapse, v =>
        {
            _state.Timelapse = v;
            StartTimelapse();
        }));
        timelapse.Items.Add(Item("Exporter en GIF…", () => _ = ExportTimelapseAsync()));
        timelapse.Items.Add(Item("Effacer le timelapse de cette œuvre", () => _ = ClearTimelapseAsync()));
        menu.Items.Add(timelapse);

        var prefs = new MenuItem { Header = "Préférences" };
        var accent = new MenuItem { Header = "Couleur d'accent" };
        foreach (var (name, color) in MortisTheme.Accents)
        {
            var c = color;
            accent.Items.Add(Item(name, () =>
            {
                MortisTheme.Apply(c);
                AppSettings.Data.Accent = c == MortisTheme.Accents[0].Color ? 0 : c.ToUInt32();
                AppSettings.Save();
            }));
        }
        prefs.Items.Add(accent);
        prefs.Items.Add(Toggle("Le doigt dessine (sinon : navigation)", () => _state.TouchDraws, v => _state.TouchDraws = v));
        prefs.Items.Add(Toggle("Tap 2 doigts : annuler · 3 doigts : rétablir", () => _state.TwoFingerUndo, v => _state.TwoFingerUndo = v));
        menu.Items.Add(prefs);
        menu.Items.Add(Item("Raccourcis et gestes", () => _ = ShowHelpAsync()));
        MenuButton.Flyout = menu;

        GuidesButton.Flyout = BuildGuidesMenu();

        var view = new MenuFlyout();
        view.Items.Add(Item("Ajuster à l'écran", Canvas.FitToView));
        view.Items.Add(Item("Taille réelle (100 %)", Canvas.ZoomActualPixels));
        view.Items.Add(Item("Zoom avant", () => Canvas.ZoomBy(1.25), "Ctrl+OemPlus"));
        view.Items.Add(Item("Zoom arrière", () => Canvas.ZoomBy(0.8), "Ctrl+OemMinus"));
        view.Items.Add(new Separator());
        view.Items.Add(Item("Pivoter de -15°", () => Canvas.RotateView(-15)));
        view.Items.Add(Item("Pivoter de +15°", () => Canvas.RotateView(15)));
        view.Items.Add(Item("Réinitialiser la rotation", Canvas.ResetRotation));
        view.Items.Add(Item("Retourner la vue (miroir)", Canvas.FlipView));
        ZoomButton.Flyout = view;
    }

    MenuFlyout BuildGuidesMenu()
    {
        var m = new MenuFlyout();
        var sym = new MenuItem { Header = "Symétrie" };
        void SymItem(string header, SymmetryMode mode, int count = 6)
        {
            sym.Items.Add(Item(header, () =>
            {
                _engine.Symmetry.Mode = mode;
                _engine.Symmetry.RadialCount = count;
                UpdateGuidesButton();
                Canvas.InvalidateVisual();
                ShowToast("Symétrie : " + _engine.Symmetry.Label);
            }));
        }
        SymItem("Aucune", SymmetryMode.None);
        SymItem("Miroir vertical", SymmetryMode.Vertical);
        SymItem("Miroir horizontal", SymmetryMode.Horizontal);
        SymItem("Miroir 4 quarts", SymmetryMode.Quad);
        SymItem("Radiale ×6", SymmetryMode.Radial, 6);
        SymItem("Radiale ×8", SymmetryMode.Radial, 8);
        SymItem("Radiale ×12", SymmetryMode.Radial, 12);
        SymItem("Kaléidoscope ×6", SymmetryMode.Kaleidoscope, 6);
        SymItem("Kaléidoscope ×8", SymmetryMode.Kaleidoscope, 8);
        m.Items.Add(sym);

        var ruler = new MenuItem { Header = "Règle" };
        void RulerItem(string header, RulerMode mode)
        {
            ruler.Items.Add(Item(header, () =>
            {
                _engine.Ruler.Mode = mode;
                UpdateGuidesButton();
                Canvas.InvalidateVisual();
                ShowToast(mode == RulerMode.None ? "Règle retirée" : _engine.Ruler.Label + " — déplace la poignée bleue");
            }));
        }
        RulerItem("Aucune", RulerMode.None);
        RulerItem("Parallèle (hachures)", RulerMode.Parallel);
        RulerItem("Concentrique (cercles)", RulerMode.Concentric);
        RulerItem("Rayonnante (lignes de vitesse)", RulerMode.Radial);
        RulerItem("Perspective à 1 point", RulerMode.Perspective);
        m.Items.Add(ruler);
        m.Items.Add(new Separator());
        m.Items.Add(Item("Recentrer les guides", () =>
        {
            _engine.Symmetry.CenterX = _engine.Ruler.CenterX = _engine.Width / 2f;
            _engine.Symmetry.CenterY = _engine.Ruler.CenterY = _engine.Height / 2f;
            Canvas.InvalidateVisual();
        }));
        return m;
    }

    void UpdateGuidesButton() =>
        GuidesButton.Classes.Set("active", _engine.Symmetry.IsActive || _engine.Ruler.IsActive);

    async Task RenameAsync()
    {
        var name = await MainView.Current!.PromptAsync("Renommer l'œuvre", _engine.Document.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _engine.Document.Name = name;
        _engine.Document.IsDirty = true;
        UpdateTitle();
    }

    Task ShowHelpAsync() => MainView.Current!.ShowMessageAsync("Raccourcis et gestes",
        "Gestes tactiles : 1 doigt dessine · 2 doigts pour déplacer, zoomer et pivoter · tap 2 doigts = annuler · tap 3 doigts = rétablir · appui long = pipette.\n\n" +
        "Souris / stylet : molette = zoom · Ctrl+molette = taille · Alt+molette = rotation · clic milieu ou Espace = déplacer · clic droit = pipette · gomme du stylet = gomme. Sélection : Maj = ajouter, Alt = retirer.\n\n" +
        "Outils : B pinceau · E gomme · S doigt · R flou · G pot · D dégradé · I pipette · U formes · T texte · M sélection · V transformer · H main.\n\n" +
        "Calques : glisser la poignée ⋮⋮ pour réordonner ou ranger dans un dossier · Ctrl/Maj + clic pour en sélectionner plusieurs · Ctrl+G grouper · Ctrl+Maj+N nouveau calque · Ctrl+J dupliquer.\n\n" +
        "Ctrl+Z / Ctrl+Y annuler / rétablir · [ ] taille · X échanger les couleurs · Ctrl+S enregistrer · Ctrl+E exporter en PNG · Ctrl+Maj+E exporter en PSD · Ctrl+A / Ctrl+D sélection · Suppr effacer · Entrée valider · Échap annuler · F miroir de la vue · Ctrl+0 ajuster · , et . image précédente / suivante en animation.");

    // ================================================================ clavier

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_closed || MainView.Current == null || MainView.Current.IsDialogOpen) return;
        if (_top?.FocusManager?.GetFocusedElement() is TextBox) return;
        if (_text != null)
        {
            // Saisie d'un texte en cours : le focus a pu quitter le champ (police, gras…),
            // les raccourcis ne doivent pas agir sur le calque derrière.
            if (e.Key == Key.Enter) CommitText();
            else if (e.Key == Key.Escape) CancelText();
            else return;
            e.Handled = true;
            return;
        }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool handled = true;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.S: SaveNow(); break;
                case Key.E when shift: _ = ExportPsdAsync(); break;
                case Key.E: _ = ExportAsync(SKEncodedImageFormat.Png); break;
                case Key.I when shift: Cmd(() => _engine.Selection.Invert())(); break;
                case Key.I: _ = ImportImageAsync(); break;
                case Key.A: Cmd(() => _engine.Selection.SelectAll())(); break;
                case Key.D: Cmd(() => _engine.Selection.Clear())(); break;
                case Key.D0: case Key.NumPad0: Canvas.FitToView(); break;
                case Key.D1: case Key.NumPad1: Canvas.ZoomActualPixels(); break;
                case Key.OemPlus: case Key.Add: Canvas.ZoomBy(1.25); break;
                case Key.OemMinus: case Key.Subtract: Canvas.ZoomBy(0.8); break;
                case Key.N when shift: Cmd(() => { ClearLayerSelection(); _engine.AddLayer(); })(); break;
                case Key.J: Cmd(DuplicateSelection)(); break;
                case Key.G: GroupSelection(); break;
                case Key.T: CommitPending(); SelectTool(ToolKind.Transform); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Space: Canvas.SpacePan = true; break;
                case Key.B: SelectTool(ToolKind.Brush); break;
                case Key.E: SelectTool(ToolKind.Eraser); break;
                case Key.S: SelectTool(ToolKind.Smudge); break;
                case Key.R: SelectTool(ToolKind.Blur); break;
                case Key.G: SelectTool(ToolKind.Fill); break;
                case Key.D: SelectTool(ToolKind.Gradient); break;
                case Key.I: SelectTool(ToolKind.Eyedropper); break;
                case Key.U: SelectTool(ToolKind.Shape); break;
                case Key.T: SelectTool(ToolKind.Text); break;
                case Key.M: SelectTool(ToolKind.Select); break;
                case Key.V: SelectTool(ToolKind.Transform); break;
                case Key.H: SelectTool(ToolKind.Hand); break;
                case Key.X: _state.SwapColors(); break;
                case Key.F: Canvas.FlipView(); break;
                case Key.OemComma: StepFrame(-1); break;
                case Key.OemPeriod: StepFrame(1); break;
                case Key.OemOpenBrackets: ResizeBrush(1 / 1.15); break;
                case Key.OemCloseBrackets: ResizeBrush(1.15); break;
                case Key.Delete:
                    ClearLayerOrSelection();
                    break;
                case Key.Enter:
                    if (_filter != null) ApplyFilter();
                    else if (_text != null) CommitText();
                    else if (Canvas.Transform != null) Canvas.CommitTransform();
                    else handled = false;
                    break;
                case Key.Escape:
                    if (CanvasDialog.IsVisible) CanvasDialog.IsVisible = false;
                    else if (_filter != null) CancelFilter();
                    else if (_text != null) CancelText();
                    else if (Canvas.Transform != null) Canvas.CancelTransform();
                    else if (_engine.Selection.HasSelection) _engine.Selection.Clear();
                    else handled = false;
                    break;
                default: handled = false; break;
            }
        }
        if (handled) e.Handled = true;
    }

    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Canvas.SpacePan = false;
            if (_top?.FocusManager?.GetFocusedElement() is not TextBox) e.Handled = true;
        }
    }

    void ResizeBrush(double factor)
    {
        var b = _state.CurrentBrush;
        b.Size = (float)Math.Clamp(b.Size * factor, 1, b.MaxSize);
        _state.NotifyBrushEdited();
        ShowToast($"Taille : {b.Size:0.#} px");
    }

    void ClearLayerOrSelection()
    {
        CommitPending();
        _engine.ClearLayer();
    }

    void FillWithTone()
    {
        CommitPending();
        if (_state.FillTone == ToneKind.Solid)
        {
            ShowToast("Choisis une trame dans les options du pot de peinture");
            return;
        }
        using var tone = _state.CreateTone();
        _engine.FillSelection(_state.Color, tone);
    }

    // ================================================================ fichiers

    async Task ExportAsync(SKEncodedImageFormat format)
    {
        CommitPending();
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        bool png = format == SKEncodedImageFormat.Png;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = png ? "Exporter en PNG" : "Exporter en JPEG",
            SuggestedFileName = _engine.Document.Name + (png ? ".png" : ".jpg"),
            DefaultExtension = png ? "png" : "jpg",
            FileTypeChoices = new[] { png ? FilePickerFileTypes.ImagePng : FilePickerFileTypes.ImageJpg },
        });
        if (file == null || _closed) return;
        try
        {
            using var ms = new MemoryStream();
            ProjectIO.ExportImage(_engine.Document, ms, format, 92);
            ms.Position = 0;
            await using var s = await file.OpenWriteAsync();
            await ms.CopyToAsync(s);
            ShowToast("Image exportée : " + file.Name);
        }
        catch (Exception ex)
        {
            ShowToast("Export impossible : " + ex.Message);
        }
    }

    async Task ExportPsdAsync()
    {
        CommitPending();
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exporter en PSD",
            SuggestedFileName = _engine.Document.Name + ".psd",
            DefaultExtension = "psd",
            FileTypeChoices = new[] { GalleryView.PsdFileType },
        });
        if (file == null || _closed) return;
        try
        {
            using var ms = new MemoryStream();
            PsdFile.Write(_engine.Document, ms);
            ms.Position = 0;
            await using var s = await file.OpenWriteAsync();
            await ms.CopyToAsync(s);
            ShowToast("PSD exporté : " + file.Name + " — calques et dossiers conservés");
        }
        catch (Exception ex)
        {
            ShowToast("Export impossible : " + ex.Message);
        }
    }

    async Task<SKBitmap?> PickImageAsync(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        var f = files.FirstOrDefault();
        if (f == null || _closed) return null;
        using var ms = new MemoryStream();
        await using (var s = await f.OpenReadAsync()) await s.CopyToAsync(ms);
        ms.Position = 0;
        var bmp = BitmapUtil.Decode(ms);
        if (bmp == null) ShowToast("Format d'image non reconnu");
        return bmp;
    }

    async Task ImportImageAsync()
    {
        CommitPending();
        try
        {
            using var bmp = await PickImageAsync("Importer une image");
            if (bmp == null) return;
            _engine.ImportImage(bmp, "Image importée");
            if (_state.Tool == ToolKind.Transform) Canvas.BeginTransform();
            else _state.SetTool(ToolKind.Transform);
            ShowToast("Place l'image puis valide ✓");
        }
        catch (Exception ex)
        {
            ShowToast("Import impossible : " + ex.Message);
        }
    }
}
