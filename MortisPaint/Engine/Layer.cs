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
    string Name, bool Visible, float Opacity, LayerBlendMode BlendMode, bool Clipping, bool AlphaLock, LayerFolder? Folder);

public readonly record struct FolderProps(
    string Name, bool Visible, float Opacity, LayerBlendMode BlendMode, bool Expanded, LayerFolder? Parent);

/// <summary>
/// Dossier de calques. Les calques restent dans une liste plate (de bas en haut) :
/// ceux d'un même dossier y sont contigus et pointent vers lui via <see cref="Layer.Folder"/>.
/// Un dossier existe tant qu'il contient au moins un calque.
/// En mode animation, chaque dossier de premier niveau est une image.
/// </summary>
public sealed class LayerFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; }
    public bool Visible { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
    public bool Expanded { get; set; } = true;
    public LayerFolder? Parent { get; set; }

    public LayerFolder(string name) => Name = name;

    public int Depth
    {
        get
        {
            int d = 0;
            for (var f = Parent; f != null; f = f.Parent) d++;
            return d;
        }
    }

    /// <summary>Vrai si ce dossier est <paramref name="other"/> ou l'un de ses descendants.</summary>
    public bool IsWithin(LayerFolder? other)
    {
        if (other == null) return true;
        for (var f = this; f != null; f = f.Parent)
            if (f == other) return true;
        return false;
    }

    /// <summary>Visible avec tous ses parents.</summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (var f = this; f != null; f = f.Parent)
                if (!f.Visible) return false;
            return true;
        }
    }

    public FolderProps GetProps() => new(Name, Visible, Opacity, BlendMode, Expanded, Parent);

    public void SetProps(FolderProps p)
    {
        Name = p.Name;
        Visible = p.Visible;
        Opacity = p.Opacity;
        BlendMode = p.BlendMode;
        Expanded = p.Expanded;
        Parent = p.Parent;
    }
}

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
    /// <summary>Dossier qui contient le calque (null = racine).</summary>
    public LayerFolder? Folder { get; set; }

    /// <summary>Vrai si le calque est dans <paramref name="folder"/> (directement ou non).</summary>
    public bool IsIn(LayerFolder? folder) => folder == null || (Folder != null && Folder.IsWithin(folder));

    /// <summary>Visible, ainsi que tous ses dossiers parents.</summary>
    public bool IsEffectivelyVisible => Visible && (Folder?.IsEffectivelyVisible ?? true);

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
            Folder = Folder,
        };
        return copy;
    }

    public LayerProps GetProps() => new(Name, Visible, Opacity, BlendMode, Clipping, AlphaLock, Folder);

    public void SetProps(LayerProps p)
    {
        Name = p.Name;
        Visible = p.Visible;
        Opacity = p.Opacity;
        BlendMode = p.BlendMode;
        Clipping = p.Clipping;
        AlphaLock = p.AlphaLock;
        Folder = p.Folder;
    }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Bitmap.Dispose();
    }
}
