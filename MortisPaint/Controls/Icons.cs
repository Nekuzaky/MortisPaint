using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MortisPaint.Controls;

/// <summary>
/// Icônes au trait dessinées pour MortisPaint, sur une grille de 24 × 24.
/// Elles sont tracées (pas remplies) avec des extrémités rondes, comme un trait de stylo.
/// </summary>
public static class Icons
{
    public static readonly Geometry Brush = G("M20.5,3.5 L13.2,10.8 M10.6,10.6 L13.4,13.4 M10.6,10.6 C7.9,10.4 6.2,12.3 6,14.6 C5.8,16.8 4.8,18.8 3.5,20.5 C7,20.6 10.2,19.6 12,17.7 C13.2,16.4 13.6,14.9 13.4,13.4");
    public static readonly Geometry Eraser = G("M8,20 H20 M4.2,14.1 L13.8,4.5 C14.4,3.9 15.3,3.9 15.9,4.5 L19.5,8.1 C20.1,8.7 20.1,9.6 19.5,10.2 L10.4,19.3 C10,19.7 9.5,19.9 9,19.9 H7.9 C7.4,19.9 6.9,19.7 6.5,19.3 L4.2,17 C3.4,16.2 3.4,14.9 4.2,14.1 Z M8.7,9.6 L14.4,15.3");
    public static readonly Geometry Smudge = G("M3,8 C6,5.5 8.5,10.5 12,8 C15,5.8 17.5,9.5 21,7.5 M3,13 C6,10.5 8.5,15.5 12,13 C14.5,11.2 16.5,13.8 18.5,12.8 M3,18 C5,16.2 7,19.6 9.5,18");
    public static readonly Geometry Blur = G("M12,3.5 C12,3.5 6,10 6,14.5 C6,17.8 8.7,20.5 12,20.5 C15.3,20.5 18,17.8 18,14.5 C18,10 12,3.5 12,3.5 Z M9.2,15 C9.2,16.5 10.4,17.7 12,17.7");
    public static readonly Geometry Fill = G("M4.6,11.6 L11,5.2 L17.9,12.1 L12.1,17.9 C11.5,18.5 10.6,18.5 10,17.9 L4.6,12.5 C4.3,12.2 4.3,11.9 4.6,11.6 Z M4.5,12 H17.8 M8.4,3.4 L11,6 M20,14.8 C20,14.8 18.4,16.8 18.4,18 C18.4,18.9 19.1,19.6 20,19.6 C20.9,19.6 21.6,18.9 21.6,18 C21.6,16.8 20,14.8 20,14.8 Z");
    public static readonly Geometry Gradient = G("M4,4 H20 V20 H4 Z M7.5,4 V20 M10.5,4 V20 M12.9,4 V20 M14.8,4 V20 M16.3,4 V20 M17.5,4 V20");
    public static readonly Geometry Eyedropper = G("M14.4,5.6 L18.4,9.6 M16.1,3.9 C16.9,3.1 18.1,3.1 18.9,3.9 L20.1,5.1 C20.9,5.9 20.9,7.1 20.1,7.9 L17.6,10.4 L13.6,6.4 Z M15,8.8 L6,17.8 L5,20 L4,19 L6.2,18 M15,8.8 L6.2,17.6");
    public static readonly Geometry Shapes = G("M3.5,10.5 H12.5 V19.5 H3.5 Z M15.5,3.5 C18.3,3.5 20.5,5.7 20.5,8.5 C20.5,11.3 18.3,13.5 15.5,13.5 C12.7,13.5 10.5,11.3 10.5,8.5 C10.5,5.7 12.7,3.5 15.5,3.5 Z");
    public static readonly Geometry Select = G("M4,8 V4 H8 M16,4 H20 V8 M20,16 V20 H16 M8,20 H4 V16 M11,4 H13 M20,11 V13 M11,20 H13 M4,11 V13");
    public static readonly Geometry Lasso = G("M12,4.5 C17,4.5 21,6.8 21,9.8 C21,12.8 17,15 12,15 C7,15 3,12.8 3,9.8 C3,6.8 7,4.5 12,4.5 Z M7.5,14 C6.3,15.6 6.8,17.8 9,18.3 C10.8,18.7 10.9,20.4 9.4,21");
    public static readonly Geometry Move = G("M12,3 V21 M3,12 H21 M9,6 L12,3 L15,6 M9,18 L12,21 L15,18 M6,9 L3,12 L6,15 M18,9 L21,12 L18,15");
    public static readonly Geometry Hand = G("M8,13.5 V6.5 C8,5.7 8.7,5 9.5,5 C10.3,5 11,5.7 11,6.5 V11 M11,11 V4.5 C11,3.7 11.7,3 12.5,3 C13.3,3 14,3.7 14,4.5 V11 M14,11 V5.5 C14,4.7 14.7,4 15.5,4 C16.3,4 17,4.7 17,5.5 V12 M17,9.5 C17,8.7 17.7,8 18.5,8 C19.3,8 20,8.7 20,9.5 V15 C20,18.3 17.3,21 14,21 H12.8 C10.9,21 9.1,20.1 8,18.6 L4.6,14.2 C4.1,13.5 4.3,12.6 4.9,12.1 C5.6,11.7 6.4,11.8 6.9,12.4 L8,13.5");
    public static readonly Geometry Undo = G("M9,14 L4,9 L9,4 M4,9 H14.5 C17.5,9 20,11.5 20,14.5 C20,17.5 17.5,20 14.5,20 H11");
    public static readonly Geometry Redo = G("M15,14 L20,9 L15,4 M20,9 H9.5 C6.5,9 4,11.5 4,14.5 C4,17.5 6.5,20 9.5,20 H13");
    public static readonly Geometry Menu = G("M4,7 H20 M4,12 H20 M4,17 H14");
    public static readonly Geometry Back = G("M10,5 L3,12 L10,19 M3,12 H21");
    public static readonly Geometry Close = G("M6,6 L18,18 M18,6 L6,18");
    public static readonly Geometry Plus = G("M12,5 V19 M5,12 H19");
    public static readonly Geometry Delete = G("M4,7 H20 M9,7 V4.5 H15 V7 M6.5,7 L7.5,20 H16.5 L17.5,7 M10,11 V16 M14,11 V16");
    public static readonly Geometry Copy = G("M8,8 H19 V20 H8 Z M5,16 V4 H16");
    public static readonly Geometry MergeDown = G("M12,4 V15 M7,10 L12,15 L17,10 M5,20 H19");
    public static readonly Geometry ArrowUp = G("M12,19 V5 M6,11 L12,5 L18,11");
    public static readonly Geometry ArrowDown = G("M12,5 V19 M6,13 L12,19 L18,13");
    public static readonly Geometry Eye = G("M2.5,12 C5,7.3 8.4,5 12,5 C15.6,5 19,7.3 21.5,12 C19,16.7 15.6,19 12,19 C8.4,19 5,16.7 2.5,12 Z M12,9 C13.7,9 15,10.3 15,12 C15,13.7 13.7,15 12,15 C10.3,15 9,13.7 9,12 C9,10.3 10.3,9 12,9 Z");
    public static readonly Geometry EyeOff = G("M2.5,12 C5,7.3 8.4,5 12,5 C15.6,5 19,7.3 21.5,12 C19,16.7 15.6,19 12,19 C8.4,19 5,16.7 2.5,12 Z M4,20 L20,4");
    public static readonly Geometry Lock = G("M6,11 H18 V20 H6 Z M8.5,11 V8 C8.5,6.1 10.1,4.5 12,4.5 C13.9,4.5 15.5,6.1 15.5,8 V11");
    public static readonly Geometry Clip = G("M6,4 V17 H20 M10,8.5 L13.5,12 L10,15.5");
    public static readonly Geometry More = G("M11,5 A1,1 0 1,0 13,5 A1,1 0 1,0 11,5 M11,12 A1,1 0 1,0 13,12 A1,1 0 1,0 11,12 M11,19 A1,1 0 1,0 13,19 A1,1 0 1,0 11,19");
    public static readonly Geometry Layers = G("M12,3.5 L21,8.5 L12,13.5 L3,8.5 Z M3,12.5 L12,17.5 L21,12.5 M3,16.5 L12,21.5 L21,16.5");
    public static readonly Geometry Palette = G("M12,3 C7,3 3,7 3,11.5 C3,16.5 7,21 12,21 C13.2,21 13.9,20 13.4,19 C12.9,17.9 13.5,16.5 15,16.5 H17 C19.4,16.5 21,14.8 21,12 C21,7 17,3 12,3 Z M7,11.5 A1.2,1.2 0 1,0 9.4,11.5 A1.2,1.2 0 1,0 7,11.5 M9.8,7.3 A1.2,1.2 0 1,0 12.2,7.3 A1.2,1.2 0 1,0 9.8,7.3 M14.4,8.3 A1.2,1.2 0 1,0 16.8,8.3 A1.2,1.2 0 1,0 14.4,8.3");
    public static readonly Geometry Tune = G("M4,7 H13 M17,7 H20 M15,4.5 V9.5 M4,17 H7 M11,17 H20 M9,14.5 V19.5");
    public static readonly Geometry Image = G("M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L15,13 L20,18 M15,8.5 A1.2,1.2 0 1,0 17.4,8.5 A1.2,1.2 0 1,0 15,8.5");
    public static readonly Geometry Save = G("M5,4 H16 L19,7 V20 H5 Z M8,4 V9 H15 V4 M8,20 V14 H16 V20");
    public static readonly Geometry Folder = G("M3,6.5 C3,5.7 3.7,5 4.5,5 H9 L11,7 H19.5 C20.3,7 21,7.7 21,8.5 V17.5 C21,18.3 20.3,19 19.5,19 H4.5 C3.7,19 3,18.3 3,17.5 Z");
    public static readonly Geometry Export = G("M12,15 V3 M7,8 L12,3 L17,8 M5,13 V19 H19 V13");
    public static readonly Geometry Fit = G("M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15");
    public static readonly Geometry Symmetry = G("M12,2 V5 M12,8 V11 M12,14 V17 M12,20 V22 M9,6 L3,18 H9 Z M15,6 L21,18 H15 Z");
    public static readonly Geometry Check = G("M4.5,12.5 L9.5,17.5 L19.5,6.5");
    public static readonly Geometry Flip = G("M4,12 H20 M7,9 L4,12 L7,15 M17,9 L20,12 L17,15 M12,4 V7 M12,17 V20");
    public static readonly Geometry Rotate = G("M18.5,8.5 A7.5,7.5 0 1,0 19.5,13 M19,4 V8.5 H14.5");
    public static readonly Geometry Swap = G("M7,4 L4,7 L7,10 M4,7 H18 M17,14 L20,17 L17,20 M20,17 H6");
    public static readonly Geometry Nib = G("M12,3 L18,10 L14.5,20 H9.5 L6,10 Z M12,3 V12 M12,12 A1.4,1.4 0 1,0 12,14.8 A1.4,1.4 0 1,0 12,12 M9.5,20 V21.5 H14.5 V20");

    static Geometry G(string data) => Geometry.Parse(data);

    public static LineIcon Make(Geometry g, double size = 20) => new() { Data = g, Width = size, Height = size };
}

/// <summary>Affiche une icône <see cref="Icons"/> au trait, dans la couleur de texte héritée.</summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeWidth), 1.6);

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
    }

    public LineIcon()
    {
        Width = 20;
        Height = 20;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Data == null) return;
        double side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        double s = side / 24;
        var offset = new Vector((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        var pen = new Pen(Foreground ?? Brushes.Black, StrokeWidth, null, PenLineCap.Round, PenLineJoin.Round);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(offset.X, offset.Y)))
            ctx.DrawGeometry(null, pen, Data);
    }
}
