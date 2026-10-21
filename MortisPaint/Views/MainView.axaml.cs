using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MortisPaint.Engine;

namespace MortisPaint.Views;

/// <summary>Racine de l'application : bascule entre la galerie et l'éditeur, gère les dialogues et le bouton retour.</summary>
public partial class MainView : UserControl
{
    TaskCompletionSource<bool>? _dialog;
    GalleryView? _gallery;
    EditorView? _editor;

    public static MainView? Current { get; private set; }

    public MainView()
    {
        InitializeComponent();
        Current = this;
        DialogOk.Click += (_, _) => CloseDialog(true);
        DialogCancel.Click += (_, _) => CloseDialog(false);
        DialogInput.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) CloseDialog(true);
        };
        ShowGallery();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        top.BackRequested += OnBackRequested;
        var insets = top.InsetsManager;
        if (insets != null)
        {
            // Barres système sombres, comme le reste de l'interface.
            insets.SystemBarColor = Avalonia.Media.Color.FromRgb(0x08, 0x08, 0x08);
            insets.SafeAreaChanged += OnSafeAreaChanged;
        }
        if (top.InputPane is { } pane) pane.StateChanged += OnInputPaneChanged;
        UpdatePadding();
        if (Lifetime is { } life) life.Deactivated += OnDeactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is TopLevel top)
        {
            top.BackRequested -= OnBackRequested;
            if (top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeAreaChanged;
            if (top.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
        }
        if (Lifetime is { } life) life.Deactivated -= OnDeactivated;
    }

    static IActivatableLifetime? Lifetime =>
        Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;

    void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs a) => UpdatePadding();

    void OnInputPaneChanged(object? sender, InputPaneStateEventArgs a) => UpdatePadding();

    /// <summary>
    /// Marges système, plus le clavier virtuel : en affichage bord à bord (Android 15), adjustResize ne
    /// réduit plus la fenêtre et le clavier recouvrirait le panneau Texte. On ne compte que la partie
    /// du clavier qui chevauche réellement la fenêtre, pour ne rien ajouter quand elle a déjà été réduite.
    /// </summary>
    void UpdatePadding()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var pad = top.InsetsManager?.SafeAreaPadding ?? default;
        if (top.InputPane is { State: InputPaneState.Open } pane)
        {
            double overlap = top.ClientSize.Height - pane.OccludedRect.Top;
            if (overlap > pad.Bottom) pad = new Thickness(pad.Left, pad.Top, pad.Right, overlap);
        }
        Padding = pad;
    }

    // Mise en arrière-plan (Android) : on enregistre sans valider le filtre, le texte ou la transformation en cours.
    void OnDeactivated(object? sender, ActivatedEventArgs e) => _editor?.SaveForSuspend();

    async void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DialogLayer.IsVisible)
        {
            e.Handled = true;
            CloseDialog(false);
            return;
        }
        if (_editor != null)
        {
            e.Handled = true;
            if (!_editor.HandleBack()) await CloseEditorAsync();
            return;
        }
        if (_gallery != null && _gallery.HandleBack()) e.Handled = true;
    }

    public void ShowGallery()
    {
        _editor = null;
        _gallery ??= new GalleryView();
        PageHost.Content = _gallery;
        _gallery.Refresh();
    }

    public void OpenEditor(Document doc)
    {
        _editor = new EditorView(doc);
        PageHost.Content = _editor;
    }

    public async Task CloseEditorAsync()
    {
        if (_editor == null) return;
        var ed = _editor;
        var error = ed.AutoSave();
        // Échec d'enregistrement : on ne ferme pas sans prévenir, sinon le travail est perdu.
        if (error != null && !await ConfirmUnsavedAsync(error, "Fermer sans enregistrer")) return;
        if (_editor != ed) return;
        ed.Close();
        ShowGallery();
    }

    /// <summary>Enregistre avant la fermeture de la fenêtre (bureau). Retourne le message d'erreur, ou null.</summary>
    public string? OnAppClosing() => _editor?.AutoSave();

    public Task<bool> ConfirmUnsavedAsync(string error, string leave) =>
        ConfirmAsync("Enregistrement impossible",
            $"L'œuvre n'a pas pu être enregistrée : {error}\n\nSi tu continues, les dernières modifications seront perdues.",
            leave, "Rester");

    public bool IsDialogOpen => DialogLayer.IsVisible;

    // ---------------------------------------------------------------- dialogues

    public Task<bool> ConfirmAsync(string title, string message, string ok = "OK", string cancel = "Annuler")
    {
        DialogInput.IsVisible = false;
        return ShowDialog(title, message, ok, cancel);
    }

    public async Task<string?> PromptAsync(string title, string initial, string ok = "OK")
    {
        DialogInput.IsVisible = true;
        DialogInput.Text = initial;
        var task = ShowDialog(title, "", ok, "Annuler");
        Dispatcher.UIThread.Post(() =>
        {
            DialogInput.Focus();
            DialogInput.SelectAll();
        });
        bool res = await task;
        DialogInput.IsVisible = false;
        return res ? DialogInput.Text?.Trim() : null;
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowDialog(title, message, "OK", null);

    Task<bool> ShowDialog(string title, string message, string ok, string? cancel)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogMessage.IsVisible = !string.IsNullOrEmpty(message);
        DialogOk.Content = ok;
        DialogCancel.Content = cancel ?? "";
        DialogCancel.IsVisible = cancel != null;
        DialogLayer.IsVisible = true;
        return _dialog.Task;
    }

    void CloseDialog(bool result)
    {
        DialogLayer.IsVisible = false;
        var d = _dialog;
        _dialog = null;
        d?.TrySetResult(result);
    }
}
