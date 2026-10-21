using System;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum LayerBlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn,
    HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity, Add
}

public static class BlendModes
{
    public static readonly LayerBlendMode[] All = (LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode));

    public static SKBlendMode ToSkia(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => SKBlendMode.Multiply,
        LayerBlendMode.Screen => SKBlendMode.Screen,
        LayerBlendMode.Overlay => SKBlendMode.Overlay,
        LayerBlendMode.Darken => SKBlendMode.Darken,
        LayerBlendMode.Lighten => SKBlendMode.Lighten,
        LayerBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        LayerBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        LayerBlendMode.HardLight => SKBlendMode.HardLight,
        LayerBlendMode.SoftLight => SKBlendMode.SoftLight,
        LayerBlendMode.Difference => SKBlendMode.Difference,
        LayerBlendMode.Exclusion => SKBlendMode.Exclusion,
        LayerBlendMode.Hue => SKBlendMode.Hue,
        LayerBlendMode.Saturation => SKBlendMode.Saturation,
        LayerBlendMode.Color => SKBlendMode.Color,
        LayerBlendMode.Luminosity => SKBlendMode.Luminosity,
        LayerBlendMode.Add => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };

    public static string Label(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Normal => "Normal",
        LayerBlendMode.Multiply => "Produit",
        LayerBlendMode.Screen => "Superposition claire",
        LayerBlendMode.Overlay => "Incrustation",
        LayerBlendMode.Darken => "Obscurcir",
        LayerBlendMode.Lighten => "Éclaircir",
        LayerBlendMode.ColorDodge => "Densité couleur -",
        LayerBlendMode.ColorBurn => "Densité couleur +",
        LayerBlendMode.HardLight => "Lumière crue",
        LayerBlendMode.SoftLight => "Lumière tamisée",
        LayerBlendMode.Difference => "Différence",
        LayerBlendMode.Exclusion => "Exclusion",
        LayerBlendMode.Hue => "Teinte",
        LayerBlendMode.Saturation => "Saturation",
        LayerBlendMode.Color => "Couleur",
        LayerBlendMode.Luminosity => "Luminosité",
        LayerBlendMode.Add => "Addition",
        _ => mode.ToString(),
    };
}

/// <summary>Propriétés non-pixel d'un calque, utilisées pour l'historique.</summary>
public readonly record struct LayerProps(
    string Name, bool Visible, float Opacity, LayerBlendMode BlendMode, bool Clipping, bool AlphaLock);

public sealed class Layer : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; }
    public SKBitmap Bitmap { get; }
    public bool Visible { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
    /// <summary>Écrêtage : le calque n'est visible que là où le calque de base en dessous est opaque.</summary>
    public bool Clipping { get; set; }
    /// <summary>Verrouillage alpha : on ne peut peindre que sur les pixels déjà opaques.</summary>
    public bool AlphaLock { get; set; }

    /// <summary>Incrémenté à chaque modification des pixels (sert à rafraîchir les miniatures).</summary>
    public int Version { get; private set; }

    public Layer(int width, int height, string name)
    {
        Name = name;
        Bitmap = new SKBitmap(BitmapUtil.Info(width, height));
        Bitmap.Erase(SKColors.Transparent);
    }

    public Layer(SKBitmap bitmap, string name)
    {
        Name = name;
        Bitmap = bitmap;
    }

    public void Touch() => Version++;

    public Layer Clone(string? name = null)
    {
        var copy = new Layer(BitmapUtil.Copy(Bitmap), name ?? Name)
        {
            Visible = Visible,
            Opacity = Opacity,
            BlendMode = BlendMode,
            Clipping = Clipping,
            AlphaLock = AlphaLock,
        };
        return copy;
    }

    public LayerProps GetProps() => new(Name, Visible, Opacity, BlendMode, Clipping, AlphaLock);

    public void SetProps(LayerProps p)
    {
        Name = p.Name;
        Visible = p.Visible;
        Opacity = p.Opacity;
        BlendMode = p.BlendMode;
        Clipping = p.Clipping;
        AlphaLock = p.AlphaLock;
    }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Bitmap.Dispose();
    }
}
