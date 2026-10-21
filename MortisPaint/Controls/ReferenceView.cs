using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using MortisPaint.Engine;
using SkiaSharp;

namespace MortisPaint.Controls;

/// <summary>
/// Image de référence : glisser pour déplacer, molette ou pincement pour zoomer,
/// toucher sans bouger pour prélever une couleur (comme la fenêtre de référence d'ibisPaint).
/// </summary>
public sealed class ReferenceView : Control, ICustomHitTest
{
    SKBitmap? _source;
    WriteableBitmap? _display;
    double _zoom = 1;
    Point _offset;
    readonly Dictionary<int, Point> _touches = new();
    Point _pressPos, _lastPos;
    bool _moved;
    double _pinchStartDist, _pinchStartZoom;

    public event Action<SKColor>? ColorPicked;

    public bool HasImage => _source != null;
    public bool HitTest(Point point) => true;

    public void SetImage(SKBitmap bitmap)
    {
        Release();
        _source = bitmap;
        _display = SkiaBitmaps.FromSkia(bitmap, 2048, checker: false);
        Fit();
    }

    /// <summary>Libère l'image (fermeture de l'éditeur).</summary>
    public void Release()
    {
        _source?.Dispose();
        _source = null;
        _display?.Dispose();
        _display = null;
        InvalidateVisual();
    }

    public void Fit()
    {
        if (_source == null || Bounds.Width <= 0 || Bounds.Height <= 0) { InvalidateVisual(); return; }
        _zoom = Math.Min(Bounds.Width / _source.Width, Bounds.Height / _source.Height);
        _offset = new Point((Bounds.Width - _source.Width * _zoom) / 2, (Bounds.Height - _source.Height * _zoom) / 2);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_source != null && e.PreviousSize.Width <= 0) Fit();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)), new Rect(Bounds.Size));
        if (_source == null || _display == null)
        {
            var t = new FormattedText("Choisis une image avec le dossier ci-dessus.", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x86)))
            { MaxTextWidth = Math.Max(40, Bounds.Width - 24), TextAlignment = TextAlignment.Center };
            ctx.DrawText(t, new Point(12, Math.Max(8, Bounds.Height / 2 - t.Height / 2)));
            return;
        }
        using (ctx.PushClip(new Rect(Bounds.Size)))
        {
            var rect = new Rect(_offset.X, _offset.Y, _source.Width * _zoom, _source.Height * _zoom);
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = _zoom > 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality }))
                ctx.DrawImage(_display, rect);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
        _touches[e.Pointer.Id] = p;
        if (_touches.Count == 2)
        {
            var pts = new List<Point>(_touches.Values);
            _pinchStartDist = Dist(pts[0], pts[1]);
            _pinchStartZoom = _zoom;
            _moved = true;
            return;
        }
        _pressPos = _lastPos = p;
        _moved = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_touches.ContainsKey(e.Pointer.Id)) return;
        var p = e.GetPosition(this);
        _touches[e.Pointer.Id] = p;
        if (_touches.Count >= 2)
        {
            var pts = new List<Point>(_touches.Values);
            double d = Dist(pts[0], pts[1]);
            if (_pinchStartDist > 1) ZoomAt(new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2), _pinchStartZoom * d / _pinchStartDist / _zoom);
            return;
        }
        if (Dist(p, _pressPos) > 6) _moved = true;
        if (_moved)
        {
            _offset += p - _lastPos;
            InvalidateVisual();
        }
        _lastPos = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        bool tap = !_moved && _touches.Count == 1;
        _touches.Remove(e.Pointer.Id);
        e.Pointer.Capture(null);
        ResumeAfterPinch();
        if (tap) Pick(e.GetPosition(this));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _touches.Remove(e.Pointer.Id);
        ResumeAfterPinch();
    }

    /// <summary>Fin d'un pincement : le doigt restant reprend le déplacement depuis sa position actuelle, sans saut.</summary>
    void ResumeAfterPinch()
    {
        if (_touches.Count == 1)
            foreach (var p in _touches.Values) _lastPos = p;
        else if (_touches.Count >= 2)
        {
            var pts = new List<Point>(_touches.Values);
            _pinchStartDist = Dist(pts[0], pts[1]);
            _pinchStartZoom = _zoom;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), Math.Pow(1.18, e.Delta.Y));
        e.Handled = true;
    }

    void ZoomAt(Point at, double factor)
    {
        double nz = Math.Clamp(_zoom * factor, 0.02, 32);
        var img = (at - _offset) / _zoom;
        _zoom = nz;
        _offset = at - img * _zoom;
        InvalidateVisual();
    }

    void Pick(Point p)
    {
        if (_source == null) return;
        int x = (int)((p.X - _offset.X) / _zoom), y = (int)((p.Y - _offset.Y) / _zoom);
        if (x < 0 || y < 0 || x >= _source.Width || y >= _source.Height) return;
        var c = _source.GetPixel(x, y);
        if (c.Alpha < 8) return;
        ColorPicked?.Invoke(c.WithAlpha(255));
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
