using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using MortisPaint.Engine;
using SkiaSharp;

namespace MortisPaint.Controls;

/// <summary>Ligne « libellé + valeur + curseur » liée à un getter / setter.</summary>
public sealed class SliderRow
{
    readonly TextBlock _value;
    readonly Func<double> _get;
    readonly Action<double> _set;
    readonly Func<string> _format;
    bool _sync;

    public Slider Slider { get; }
    public Control View { get; }

    public SliderRow(string label, double min, double max, Func<double> get, Action<double> set, Func<string> format)
    {
        _get = get;
        _set = set;
        _format = format;
        var lbl = new TextBlock { Text = label, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        _value = new TextBlock { FontSize = 12, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(lbl);
        Grid.SetColumn(_value, 1);
        header.Children.Add(_value);
        Slider = new Slider { Minimum = min, Maximum = max, Margin = new Thickness(0, -8, 0, -4) };
        Slider.ValueChanged += (_, e) =>
        {
            if (_sync) return;
            _set(e.NewValue);
            _value.Text = _format();
        };
        View = new StackPanel { Margin = new Thickness(0, 2), Children = { header, Slider } };
        Refresh();
    }

    public void Refresh()
    {
        _sync = true;
        Slider.Value = Math.Clamp(_get(), Slider.Minimum, Slider.Maximum);
        _value.Text = _format();
        _sync = false;
    }
}

public static class BrushPreview
{
    /// <summary>Dessine un trait d'exemple (sinusoïde avec variation de pression).</summary>
    public static WriteableBitmap Render(BrushSettings brush, bool smear)
    {
        const int w = 260, h = 64;
        using var bmp = new SKBitmap(BitmapUtil.Info(w, h));
        bmp.Erase(SKColors.Transparent);
        var b = brush.Clone();
        b.Stabilizer = 0;
        b.TaperStart = Math.Min(b.TaperStart, 40);
        float max = h * 0.5f;
        if (b.Shape == BrushShape.Pixel) b.Size = 4;
        else if (b.Size > max) b.Size = max;
        if (b.Size < 2.5f) b.Size = 2.5f;
        if (smear)
        {
            b.Flow = 0.35f;
            b.Hardness = Math.Min(b.Hardness, 0.4f);
        }

        using (var c = new SKCanvas(bmp))
        using (var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(Math.Clamp(b.Opacity, 0.15f, 1f) * 255)) })
        {
            c.SaveLayer(layerPaint);
            using var dabs = new DabRenderer(b, new SKColor(0x2A, 0x2C, 0x30), new List<SKMatrix> { SKMatrix.Identity });
            var path = new StrokePath(b, (x, y, p, a, along) => dabs.Dab(c, x, y, p, a, along));
            for (int i = 0; i <= 80; i++)
            {
                float t = i / 80f;
                float x = 16 + t * (w - 32);
                float y = h / 2f + MathF.Sin(t * MathF.PI * 2) * h * 0.2f;
                path.Add(new InputPoint(x, y, 0.15f + 0.85f * MathF.Sin(t * MathF.PI)));
            }
            path.End();
            c.Restore();
        }
        return SkiaBitmaps.FromSkia(bmp, w, false);
    }
}
