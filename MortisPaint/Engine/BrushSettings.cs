using System.Collections.Generic;

namespace MortisPaint.Engine;

public enum BrushShape { Round, Ellipse, Square, Pixel, Image }

public sealed class BrushSettings
{
    public string Name { get; set; } = "";
    public BrushShape Shape { get; set; } = BrushShape.Round;
    /// <summary>Diamètre en pixels de la toile.</summary>
    public float Size { get; set; } = 10f;
    /// <summary>Opacité du trait entier (0..1).</summary>
    public float Opacity { get; set; } = 1f;
    /// <summary>Densité de chaque empreinte (0..1). Faible = effet aérographe qui s'accumule.</summary>
    public float Flow { get; set; } = 1f;
    /// <summary>Dureté du bord (0 = très doux, 1 = net).</summary>
    public float Hardness { get; set; } = 1f;
    /// <summary>Espacement entre empreintes, en fraction du diamètre.</summary>
    public float Spacing { get; set; } = 0.08f;
    public bool PressureSize { get; set; } = true;
    public bool PressureOpacity { get; set; }
    /// <summary>Taille relative à pression nulle (0..1).</summary>
    public float MinSize { get; set; } = 0.15f;
    /// <summary>Stabilisation du trait (0..20).</summary>
    public float Stabilizer { get; set; } = 3f;
    /// <summary>Texture grain papier (0..1).</summary>
    public float Grain { get; set; }
    /// <summary>Dispersion des empreintes (fraction du diamètre).</summary>
    public float Scatter { get; set; }
    public float SizeJitter { get; set; }
    /// <summary>Nombre d'empreintes par pas (spray).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Rapport hauteur/largeur des pointes elliptiques.</summary>
    public float Roundness { get; set; } = 1f;
    /// <summary>Angle fixe de la pointe (degrés).</summary>
    public float Angle { get; set; }
    /// <summary>La pointe suit la direction du trait.</summary>
    public bool FollowDirection { get; set; }
    /// <summary>Longueur (px) d'effilement en début de trait.</summary>
    public float TaperStart { get; set; }

    public float MaxSize { get; set; } = 500f;

    /// <summary>Pointe image (forme <see cref="BrushShape.Image"/>), voir <see cref="BrushTips"/>.</summary>
    public string? TipId { get; set; }
    /// <summary>Rotation aléatoire de chaque empreinte (degrés).</summary>
    public float RotationJitter { get; set; }
    /// <summary>Pinceau créé par l'utilisateur (enregistré dans les préférences).</summary>
    public bool IsCustom { get; set; }

    public BrushSettings Clone() => (BrushSettings)MemberwiseClone();

    /// <summary>Diamètre d'une empreinte (px toile) selon la pression et la distance parcourue (effilement).</summary>
    public float SizeAt(float pressure, float along)
    {
        float f = PressureSize ? MinSize + (1 - MinSize) * pressure : 1f;
        if (TaperStart > 0.5f && along < TaperStart) f *= 0.2f + 0.8f * (along / TaperStart);
        return Size * f;
    }
}

public static class BrushPresets
{
    public static List<BrushSettings> CreateBrushes() => new()
    {
        new() { Name = "Plume G", Size = 8, Hardness = 1, PressureSize = true, MinSize = 0.1f, Spacing = 0.05f, Stabilizer = 4, TaperStart = 20 },
        new() { Name = "Stylo", Size = 5, Hardness = 1, PressureSize = false, Spacing = 0.05f, Stabilizer = 3 },
        new() { Name = "Feutre", Size = 22, Hardness = 0.92f, Opacity = 0.65f, PressureSize = false, Spacing = 0.06f, Stabilizer = 2 },
        new() { Name = "Crayon", Size = 6, Hardness = 0.75f, Grain = 0.7f, PressureSize = true, PressureOpacity = true, MinSize = 0.5f, Spacing = 0.12f, Stabilizer = 2 },
        new() { Name = "Aérographe", Size = 140, Hardness = 0f, Flow = 0.06f, PressureSize = false, PressureOpacity = true, Spacing = 0.1f, Stabilizer = 0 },
        new() { Name = "Aquarelle", Size = 60, Hardness = 0.35f, Flow = 0.3f, Opacity = 0.6f, Grain = 0.35f, SizeJitter = 0.1f, PressureSize = true, MinSize = 0.6f, Spacing = 0.08f, Stabilizer = 2 },
        new() { Name = "Pinceau plat", Shape = BrushShape.Ellipse, Size = 34, Roundness = 0.3f, FollowDirection = true, Hardness = 0.85f, PressureSize = true, MinSize = 0.3f, Spacing = 0.04f, Stabilizer = 3 },
        new() { Name = "Calligraphie", Shape = BrushShape.Ellipse, Size = 22, Roundness = 0.18f, Angle = 45, Hardness = 1, PressureSize = true, MinSize = 0.4f, Spacing = 0.03f, Stabilizer = 4 },
        new() { Name = "Spray", Size = 60, Hardness = 1, Flow = 0.9f, Scatter = 0.5f, Count = 12, PressureSize = false, Spacing = 0.25f, Stabilizer = 0, SizeJitter = 0f, MinSize = 0.05f },
        new() { Name = "Pixel", Shape = BrushShape.Pixel, Size = 1, Hardness = 1, PressureSize = false, Spacing = 0.2f, Stabilizer = 0, MaxSize = 64 },
        new() { Name = "Néon doux", Size = 40, Hardness = 0.1f, Flow = 0.5f, PressureSize = true, MinSize = 0.3f, Spacing = 0.06f, Stabilizer = 3 },
    };

    public static List<BrushSettings> CreateErasers() => new()
    {
        new() { Name = "Gomme dure", Size = 30, Hardness = 1, PressureSize = false, Spacing = 0.05f, Stabilizer = 1 },
        new() { Name = "Gomme douce", Size = 80, Hardness = 0.1f, Flow = 0.35f, PressureSize = false, PressureOpacity = true, Spacing = 0.08f, Stabilizer = 0 },
        new() { Name = "Gomme plume", Size = 10, Hardness = 1, PressureSize = true, MinSize = 0.1f, Spacing = 0.05f, Stabilizer = 3 },
        new() { Name = "Gomme pixel", Shape = BrushShape.Pixel, Size = 1, PressureSize = false, Spacing = 0.2f, Stabilizer = 0, MaxSize = 64 },
    };

    public static List<BrushSettings> CreateSmudges() => new()
    {
        new() { Name = "Doigt", Size = 40, Hardness = 0.3f, Opacity = 0.75f, PressureSize = false, PressureOpacity = true, Spacing = 0.08f, Stabilizer = 1 },
        new() { Name = "Doigt net", Size = 20, Hardness = 0.8f, Opacity = 0.9f, PressureSize = false, Spacing = 0.06f, Stabilizer = 1 },
    };

    public static List<BrushSettings> CreateBlurs() => new()
    {
        new() { Name = "Flou", Size = 50, Hardness = 0.2f, Opacity = 0.6f, PressureSize = false, Spacing = 0.15f, Stabilizer = 0 },
    };

    /// <summary>Réglages par défaut d'un pinceau personnel à pointe image (création et « Réinitialiser »).</summary>
    public static BrushSettings CreateImageBrush(string name, string tipId) => new()
    {
        Name = name,
        Shape = BrushShape.Image,
        TipId = tipId,
        Size = 60,
        Hardness = 1,
        Spacing = 0.3f,
        PressureSize = true,
        MinSize = 0.4f,
        Stabilizer = 1,
        RotationJitter = 0,
        IsCustom = true,
    };
}
