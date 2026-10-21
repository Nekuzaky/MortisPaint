using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MortisPaint.Controls;

/// <summary>Sélecteur TSV : anneau de teinte + carré saturation / valeur (façon ibisPaint).</summary>
public sealed class ColorWheel : Control
{
    float _h, _s = 1, _v = 1;
    WriteableBitmap? _bmp;
    float _bmpHue = -1;
    PixelSize _bmpSize;
    enum Drag { None, Ring, Square }
    Drag _drag;

    public event Action<SKColor>? ColorChanged;

    public SKColor Color
    {
        get => ColorUtil.FromHsv(_h, _s, _v);
        set
        {
            if (value.WithAlpha(255) == Color) return;
            ColorUtil.ToHsv(value, out var h, out var s, out var v);
            // Garde la teinte quand la couleur est grise.
            if (s > 0.001f && v > 0.001f) _h = h;
            _s = s;
            _v = v;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side)) side = 240;
        return new Size(side, side);
    }

    double Side => Math.Min(Bounds.Width, Bounds.Height);
    Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    double OuterR => Side / 2 - 2;
    double RingWidth => Math.Max(14, Side * 0.11);
    double InnerR => OuterR - RingWidth;
    double SquareHalf => InnerR * 0.68;

    public override void Render(DrawingContext ctx)
    {
        if (Side < 20) return;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var px = new PixelSize((int)(Side * scaling), (int)(Side * scaling));
        if (_bmp == null || _bmpSize != px || Math.Abs(_bmpHue - _h) > 0.01f) Rebuild(px);

        var rect = new Rect(Center.X - Side / 2, Center.Y - Side / 2, Side, Side);
        ctx.DrawImage(_bmp!, rect);

        // Marqueur de teinte
        double a = _h * Math.PI / 180;
        double rm = (OuterR + InnerR) / 2;
        var hp = new Point(Center.X + Math.Cos(a) * rm, Center.Y + Math.Sin(a) * rm);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 3), hp, RingWidth / 2 - 1, RingWidth / 2 - 1);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 1), hp, RingWidth / 2 + 1, RingWidth / 2 + 1);

        // Marqueur saturation / valeur
        var sp = new Point(Center.X - SquareHalf + _s * SquareHalf * 2, Center.Y - SquareHalf + (1 - _v) * SquareHalf * 2);
        var fill = new SolidColorBrush(Color.ToAvalonia());
        ctx.DrawEllipse(fill, new Pen(Brushes.White, 2.5), sp, 8, 8);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 1), sp, 9.5, 9.5);
    }

    void Rebuild(PixelSize px)
    {
        _bmp?.Dispose();
        _bmp = SkiaBitmaps.Create(px.Width, px.Height);
        _bmpSize = px;
        _bmpHue = _h;
        float k = px.Width / (float)Side;
        float cx = px.Width / 2f, cy = px.Height / 2f;
        float outer = (float)OuterR * k, inner = (float)InnerR * k, half = (float)SquareHalf * k;
        SkiaBitmaps.Draw(_bmp, c =>
        {
            c.Clear(SKColors.Transparent);
            var hues = new SKColor[13];
            for (int i = 0; i < 13; i++) hues[i] = ColorUtil.FromHsv(i * 30, 1, 1);
            using (var sweep = SKShader.CreateSweepGradient(new SKPoint(cx, cy), hues))
            using (var p = new SKPaint { Shader = sweep, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = outer - inner })
                c.DrawCircle(cx, cy, (outer + inner) / 2, p);

            var sq = new SKRect(cx - half, cy - half, cx + half, cy + half);
            using (var p = new SKPaint { Color = ColorUtil.FromHsv(_h, 1, 1) })
                c.DrawRect(sq, p);
            using (var white = SKShader.CreateLinearGradient(new SKPoint(sq.Left, 0), new SKPoint(sq.Right, 0),
                       new[] { SKColors.White, SKColors.White.WithAlpha(0) }, SKShaderTileMode.Clamp))
            using (var p = new SKPaint { Shader = white })
                c.DrawRect(sq, p);
            using (var black = SKShader.CreateLinearGradient(new SKPoint(0, sq.Top), new SKPoint(0, sq.Bottom),
                       new[] { SKColors.Black.WithAlpha(0), SKColors.Black }, SKShaderTileMode.Clamp))
            using (var p = new SKPaint { Shader = black })
                c.DrawRect(sq, p);
        });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (Math.Abs(dx) <= SquareHalf + 6 && Math.Abs(dy) <= SquareHalf + 6) _drag = Drag.Square;
        else if (r >= InnerR - 6 && r <= OuterR + 8) _drag = Drag.Ring;
        else return;
        e.Pointer.Capture(this);
        e.Handled = true;
        Update(p);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None) return;
        Update(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
    }

    void Update(Point p)
    {
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        if (_drag == Drag.Ring)
        {
            double a = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (a < 0) a += 360;
            _h = (float)a;
        }
        else
        {
            _s = (float)Math.Clamp((dx + SquareHalf) / (SquareHalf * 2), 0, 1);
            _v = (float)Math.Clamp(1 - (dy + SquareHalf) / (SquareHalf * 2), 0, 1);
        }
        InvalidateVisual();
        ColorChanged?.Invoke(Color);
    }
}
