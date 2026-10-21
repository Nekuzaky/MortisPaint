using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
            // Barres système couleur papier (icônes sombres) pour le thème clair.
            insets.SystemBarColor = Avalonia.Media.Color.FromRgb(0xF6, 0xF5, 0xF2);
            Padding = insets.SafeAreaPadding;
            insets.SafeAreaChanged += (_, a) => Padding = a.SafeAreaPadding;
        }
        if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime life)
            life.Deactivated += (_, _) => _editor?.AutoSave();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top != null) top.BackRequested -= OnBackRequested;
    }

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
        ed.AutoSave();
        ed.Close();
        ShowGallery();
        await Task.CompletedTask;
    }

    /// <summary>Ferme proprement l'éditeur à la fermeture de la fenêtre (bureau).</summary>
    public void OnAppClosing() => _editor?.AutoSave();

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
