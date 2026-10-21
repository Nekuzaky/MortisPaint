using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MortisPaint.Views;

/// <summary>Fenêtre de référence flottante : déplaçable par son titre, redimensionnable par son coin.</summary>
public partial class EditorView
{
    Point _refDragStart;
    Thickness _refStartMargin;
    Size _refStartSize;
    bool _refMoving, _refResizing;

    void BuildReference()
    {
        RefOpenButton.Click += async (_, _) => await OpenReferenceImageAsync();
        RefFitButton.Click += (_, _) => RefView.Fit();
        RefCloseButton.Click += (_, _) => HideReference();
        // Rotation de l'écran, redimensionnement : la fenêtre reste dans la zone de la toile.
        CanvasArea.SizeChanged += (_, _) => ClampReferenceWindow();
        RefView.ColorPicked += c =>
        {
            _state.SetColor(c);
            ShowToast("Couleur prélevée sur la référence");
        };

        RefHeader.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
            _refMoving = true;
            _refDragStart = e.GetPosition(CanvasArea);
            _refStartMargin = RefWindow.Margin;
            e.Pointer.Capture(RefHeader);
            e.Handled = true;
        };
        RefHeader.PointerMoved += (_, e) =>
        {
            if (!_refMoving) return;
            var d = e.GetPosition(CanvasArea) - _refDragStart;
            double x = Math.Clamp(_refStartMargin.Left + d.X, 0, Math.Max(0, CanvasArea.Bounds.Width - 80));
            double y = Math.Clamp(_refStartMargin.Top + d.Y, 0, Math.Max(0, CanvasArea.Bounds.Height - 40));
            RefWindow.Margin = new Thickness(x, y, 0, 0);
        };
        RefHeader.PointerReleased += (_, e) => { _refMoving = false; e.Pointer.Capture(null); };

        RefResize.PointerPressed += (_, e) =>
        {
            _refResizing = true;
            _refDragStart = e.GetPosition(CanvasArea);
            _refStartSize = new Size(RefWindow.Width, RefWindow.Height);
            e.Pointer.Capture(RefResize);
            e.PreventGestureRecognition();
            e.Handled = true;
        };
        RefResize.PointerMoved += (_, e) =>
        {
            if (!_refResizing) return;
            var d = e.GetPosition(CanvasArea) - _refDragStart;
            RefWindow.Width = Math.Clamp(_refStartSize.Width + d.X, 160, Math.Max(160, CanvasArea.Bounds.Width - RefWindow.Margin.Left));
            RefWindow.Height = Math.Clamp(_refStartSize.Height + d.Y, 120, Math.Max(120, CanvasArea.Bounds.Height - RefWindow.Margin.Top));
            e.Handled = true;
        };
        RefResize.PointerReleased += (_, e) => { _refResizing = false; e.Pointer.Capture(null); RefView.Fit(); };
    }

    void HideReference()
    {
        RefWindow.IsVisible = false;
        ReferenceButton.Classes.Set("active", false);
    }

    void ClampReferenceWindow()
    {
        var area = CanvasArea.Bounds.Size;
        if (area.Width <= 0 || area.Height <= 0) return;
        double w = Math.Clamp(RefWindow.Width, 160, Math.Max(160, area.Width));
        double h = Math.Clamp(RefWindow.Height, 120, Math.Max(120, area.Height));
        double x = Math.Clamp(RefWindow.Margin.Left, 0, Math.Max(0, area.Width - w));
        double y = Math.Clamp(RefWindow.Margin.Top, 0, Math.Max(0, area.Height - h));
        bool resized = w != RefWindow.Width || h != RefWindow.Height;
        RefWindow.Width = w;
        RefWindow.Height = h;
        RefWindow.Margin = new Thickness(x, y, 0, 0);
        if (resized) RefView.Fit();
    }

    void ToggleReference()
    {
        bool show = !RefWindow.IsVisible;
        RefWindow.IsVisible = show;
        ReferenceButton.Classes.Set("active", show);
        if (show && !RefView.HasImage) _ = OpenReferenceImageAsync();
    }

    async System.Threading.Tasks.Task OpenReferenceImageAsync()
    {
        try
        {
            var bmp = await PickImageAsync("Image de référence");
            if (bmp == null) return;
            RefWindow.IsVisible = true;
            ReferenceButton.Classes.Set("active", true);
            RefView.SetImage(bmp);
            ShowToast("Touche la référence pour prélever une couleur");
        }
        catch (Exception ex)
        {
            ShowToast("Ouverture impossible : " + ex.Message);
        }
    }
}
