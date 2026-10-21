using System;
using System.Collections.Generic;
using System.Linq;
using MortisPaint.Engine;
using MortisPaint.Services;
using SkiaSharp;

namespace MortisPaint.Editor;

public enum ToolKind { Brush, Eraser, Smudge, Blur, Fill, Gradient, Eyedropper, Shape, Select, Transform, Hand }
public enum ShapeKind { Line, Rectangle, Ellipse }
public enum SelectKind { Rectangle, Ellipse, Lasso }

/// <summary>État de l'éditeur partagé entre la toile et les panneaux (outil, pinceaux, couleurs, options).</summary>
public sealed class EditorState
{
    public static readonly (ToolKind Kind, string Label, string Shortcut)[] ToolList =
    {
        (ToolKind.Brush, "Pinceau", "B"),
        (ToolKind.Eraser, "Gomme", "E"),
        (ToolKind.Smudge, "Doigt", "S"),
        (ToolKind.Blur, "Flou", "R"),
        (ToolKind.Fill, "Pot de peinture", "G"),
        (ToolKind.Gradient, "Dégradé", "D"),
        (ToolKind.Eyedropper, "Pipette", "I"),
        (ToolKind.Shape, "Formes", "U"),
        (ToolKind.Select, "Sélection", "M"),
        (ToolKind.Transform, "Transformer", "V"),
        (ToolKind.Hand, "Main", "H"),
    };

    ToolKind _tool = ToolKind.Brush;
    SKColor _color;

    public EditorState()
    {
        var s = AppSettings.Data;
        _color = new SKColor(s.Color);
        Palette = s.Palette.Select(c => new SKColor(c)).ToList();
        Recent = s.Recent.Select(c => new SKColor(c)).ToList();
        _touchDraws = s.TouchDraws;
        _twoFingerUndo = s.TwoFingerUndo;
    }

    public ToolKind Tool => _tool;
    public event Action? ToolChanged;

    public void SetTool(ToolKind tool)
    {
        if (_tool == tool) return;
        _tool = tool;
        ToolChanged?.Invoke();
        BrushChanged?.Invoke();
    }

    public static bool IsBrushTool(ToolKind t) => t is ToolKind.Brush or ToolKind.Eraser or ToolKind.Smudge or ToolKind.Blur or ToolKind.Shape;

    // ------------------------------------------------------------ pinceaux

    public List<BrushSettings> Brushes { get; } = BrushPresets.CreateBrushes();
    public List<BrushSettings> Erasers { get; } = BrushPresets.CreateErasers();
    public List<BrushSettings> Smudges { get; } = BrushPresets.CreateSmudges();
    public List<BrushSettings> Blurs { get; } = BrushPresets.CreateBlurs();

    readonly Dictionary<ToolKind, int> _brushIndex = new()
    {
        [ToolKind.Brush] = 0, [ToolKind.Eraser] = 0, [ToolKind.Smudge] = 0, [ToolKind.Blur] = 0,
    };

    /// <summary>Pinceau sélectionné ou outil changé.</summary>
    public event Action? BrushChanged;
    /// <summary>Réglages du pinceau courant modifiés.</summary>
    public event Action? BrushEdited;

    ToolKind BrushOwner => _tool switch
    {
        ToolKind.Eraser => ToolKind.Eraser,
        ToolKind.Smudge => ToolKind.Smudge,
        ToolKind.Blur => ToolKind.Blur,
        _ => ToolKind.Brush,
    };

    public List<BrushSettings> CurrentBrushList => BrushOwner switch
    {
        ToolKind.Eraser => Erasers,
        ToolKind.Smudge => Smudges,
        ToolKind.Blur => Blurs,
        _ => Brushes,
    };

    public int CurrentBrushIndex => _brushIndex[BrushOwner];
    public BrushSettings CurrentBrush => CurrentBrushList[CurrentBrushIndex];
    public BrushSettings PaintBrush => Brushes[_brushIndex[ToolKind.Brush]];
    public BrushSettings EraserBrush => Erasers[_brushIndex[ToolKind.Eraser]];

    public void SelectBrush(int index)
    {
        _brushIndex[BrushOwner] = Math.Clamp(index, 0, CurrentBrushList.Count - 1);
        BrushChanged?.Invoke();
    }

    /// <summary>Signale une modification des réglages du pinceau courant.</summary>
    public void NotifyBrushEdited() => BrushEdited?.Invoke();

    public void ResetCurrentBrush()
    {
        var defaults = BrushOwner switch
        {
            ToolKind.Eraser => BrushPresets.CreateErasers(),
            ToolKind.Smudge => BrushPresets.CreateSmudges(),
            ToolKind.Blur => BrushPresets.CreateBlurs(),
            _ => BrushPresets.CreateBrushes(),
        };
        int i = CurrentBrushIndex;
        if (i < defaults.Count) CurrentBrushList[i] = defaults[i];
        BrushChanged?.Invoke();
    }

    // ------------------------------------------------------------ couleurs

    public SKColor Color => _color;
    public SKColor SecondaryColor { get; set; } = SKColors.White;
    public List<SKColor> Palette { get; }
    public List<SKColor> Recent { get; }
    public event Action? ColorChanged;
    public event Action? PaletteChanged;

    public void SetColor(SKColor c)
    {
        c = c.WithAlpha(255);
        if (c == _color) return;
        _color = c;
        AppSettings.Data.Color = (uint)c;
        ColorChanged?.Invoke();
    }

    public void SwapColors()
    {
        var old = _color;
        _color = SecondaryColor;
        SecondaryColor = old;
        ColorChanged?.Invoke();
    }

    public void PushRecent(SKColor c)
    {
        c = c.WithAlpha(255);
        if (Recent.Count > 0 && Recent[0] == c) return;
        Recent.Remove(c);
        Recent.Insert(0, c);
        if (Recent.Count > 16) Recent.RemoveRange(16, Recent.Count - 16);
        AppSettings.Data.Recent = Recent.Select(x => (uint)x).ToList();
        AppSettings.Save();
        PaletteChanged?.Invoke();
    }

    public void AddToPalette(SKColor c)
    {
        c = c.WithAlpha(255);
        if (Palette.Contains(c)) return;
        Palette.Add(c);
        SavePalette();
    }

    public void RemoveFromPalette(SKColor c)
    {
        if (Palette.Remove(c)) SavePalette();
    }

    void SavePalette()
    {
        AppSettings.Data.Palette = Palette.Select(x => (uint)x).ToList();
        AppSettings.Save();
        PaletteChanged?.Invoke();
    }

    // ------------------------------------------------------------ options d'outils

    public int FillTolerance { get; set; } = 32;
    public int FillExpand { get; set; } = 1;
    public bool FillSampleAll { get; set; } = true;
    public float FillOpacity { get; set; } = 1f;
    public bool PickSampleAll { get; set; } = true;
    public ShapeKind Shape { get; set; } = ShapeKind.Line;
    public bool ShapeFilled { get; set; }
    public SelectKind SelectKind { get; set; } = SelectKind.Rectangle;
    public GradientKind GradientKind { get; set; } = GradientKind.Linear;
    public bool GradientToTransparent { get; set; } = true;

    bool _touchDraws, _twoFingerUndo;

    public bool TouchDraws
    {
        get => _touchDraws;
        set { _touchDraws = value; AppSettings.Data.TouchDraws = value; AppSettings.Save(); }
    }

    public bool TwoFingerUndo
    {
        get => _twoFingerUndo;
        set { _twoFingerUndo = value; AppSettings.Data.TwoFingerUndo = value; AppSettings.Save(); }
    }
}
