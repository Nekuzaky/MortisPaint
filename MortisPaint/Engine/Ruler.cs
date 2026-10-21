using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public enum RulerMode { None, Parallel, Concentric, Radial, Perspective }

/// <summary>
/// Règles de dessin (comme dans ibisPaint) : le trait est contraint selon un centre et un angle.
/// Parallèle : droites parallèles à l'angle de la règle. Concentrique : cercles autour du centre.
/// Rayonnante : droites qui passent par le centre (lignes de vitesse). Perspective : point de fuite,
/// horizontales et verticales — la direction est choisie au début du trait.
/// </summary>
public sealed class Ruler
{
    public RulerMode Mode { get; set; } = RulerMode.None;
    public float CenterX { get; set; }
    public float CenterY { get; set; }
    public float AngleDegrees { get; set; }

    public bool IsActive => Mode != RulerMode.None;

    public string Label => Mode switch
    {
        RulerMode.Parallel => "Règle parallèle",
        RulerMode.Concentric => "Règle concentrique",
        RulerMode.Radial => "Règle rayonnante",
        RulerMode.Perspective => "Perspective (1 point)",
        _ => "Aucune règle",
    };

    public SKPoint Center => new(CenterX, CenterY);

    /// <summary>Contrainte pour un trait qui commence en <paramref name="start"/>.</summary>
    public Func<SKPoint, SKPoint> Begin(SKPoint start, float decideDistance)
    {
        var c = Center;
        switch (Mode)
        {
            case RulerMode.Parallel:
            {
                double a = AngleDegrees * Math.PI / 180;
                var dir = new SKPoint((float)Math.Cos(a), (float)Math.Sin(a));
                return p => Along(start, dir, p);
            }
            case RulerMode.Concentric:
            {
                float r = SKPoint.Distance(start, c);
                return p =>
                {
                    var v = p - c;
                    float len = v.Length;
                    if (len < 1e-3f || r < 1e-3f) return start;
                    return new SKPoint(c.X + v.X / len * r, c.Y + v.Y / len * r);
                };
            }
            case RulerMode.Radial:
            {
                var v = start - c;
                if (v.Length < 1e-3f) return p => p;
                var dir = new SKPoint(v.X / v.Length, v.Y / v.Length);
                return p => Along(c, dir, p);
            }
            case RulerMode.Perspective:
            {
                SKPoint? chosen = null;
                return p =>
                {
                    if (chosen == null)
                    {
                        var m = p - start;
                        if (m.Length < decideDistance) return start;
                        var md = new SKPoint(m.X / m.Length, m.Y / m.Length);
                        var toVp = c - start;
                        var candidates = new List<SKPoint> { new(1, 0), new(0, 1) };
                        if (toVp.Length > 1e-3f) candidates.Add(new SKPoint(toVp.X / toVp.Length, toVp.Y / toVp.Length));
                        float best = -1;
                        foreach (var d in candidates)
                        {
                            float dot = Math.Abs(md.X * d.X + md.Y * d.Y);
                            if (dot > best) { best = dot; chosen = d; }
                        }
                    }
                    return Along(start, chosen!.Value, p);
                };
            }
            default:
                return p => p;
        }
    }

    static SKPoint Along(SKPoint origin, SKPoint dir, SKPoint p)
    {
        float t = (p.X - origin.X) * dir.X + (p.Y - origin.Y) * dir.Y;
        return new SKPoint(origin.X + dir.X * t, origin.Y + dir.Y * t);
    }

    /// <summary>Lignes guides à afficher (coordonnées toile).</summary>
    public List<(SKPoint A, SKPoint B)> GetGuides(int width, int height)
    {
        var lines = new List<(SKPoint, SKPoint)>();
        float len = width + height;
        var c = Center;
        switch (Mode)
        {
            case RulerMode.Parallel:
            {
                double a = AngleDegrees * Math.PI / 180;
                var d = new SKPoint((float)Math.Cos(a) * len, (float)Math.Sin(a) * len);
                lines.Add((c - d, c + d));
                break;
            }
            case RulerMode.Radial:
            case RulerMode.Perspective:
                for (int i = 0; i < 16; i++)
                {
                    double a = Math.PI * 2 * i / 16;
                    lines.Add((c, new SKPoint(c.X + (float)Math.Cos(a) * len, c.Y + (float)Math.Sin(a) * len)));
                }
                if (Mode == RulerMode.Perspective) lines.Add((new SKPoint(0, c.Y), new SKPoint(width, c.Y)));
                break;
        }
        return lines;
    }
}

public sealed record TextStyle(
    string Text, string Family, float Size, bool Bold, bool Italic, int Align, SKColor Color, float LineSpacing = 1.2f);

/// <summary>Rendu de texte multiligne (outil Texte).</summary>
public static class TextRenderer
{
    public static SKFont CreateFont(TextStyle style)
    {
        var typeface = SKTypeface.FromFamilyName(style.Family,
            new SKFontStyle(style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)) ?? SKTypeface.Default;
        return new SKFont(typeface, style.Size) { Edging = SKFontEdging.Antialias, Subpixel = true };
    }

    /// <summary>Dessine le texte ; l'origine est le coin haut-gauche (ou haut-centre / haut-droit selon l'alignement).</summary>
    public static SKRect Draw(SKCanvas c, TextStyle style, SKPoint origin)
    {
        using var font = CreateFont(style);
        using var paint = new SKPaint { Color = style.Color, IsAntialias = true };
        var lines = style.Text.Replace("\r", "").Split('\n');
        float lineHeight = style.Size * style.LineSpacing;
        float y = origin.Y - font.Metrics.Ascent;
        var align = style.Align switch { 1 => SKTextAlign.Center, 2 => SKTextAlign.Right, _ => SKTextAlign.Left };
        float maxW = 0;
        foreach (var line in lines)
        {
            c.DrawText(line, origin.X, y, align, font, paint);
            maxW = Math.Max(maxW, font.MeasureText(line));
            y += lineHeight;
        }
        float left = style.Align switch { 1 => origin.X - maxW / 2, 2 => origin.X - maxW, _ => origin.X };
        return new SKRect(left, origin.Y, left + maxW, origin.Y + lineHeight * lines.Length);
    }
}

/// <summary>Aperçu du texte en cours de saisie, au-dessus du calque actif.</summary>
public sealed class TextSession : ILayerPreview
{
    readonly PaintEngine _e;

    public TextSession(PaintEngine e, TextStyle style, SKPoint origin)
    {
        _e = e;
        Target = e.ActiveLayer;
        Style = style;
        Origin = origin;
        e.SetPreview(this);
        e.Invalidate(e.Document.Bounds);
    }

    public Layer Target { get; }
    public TextStyle Style { get; private set; }
    public SKPoint Origin { get; private set; }
    public SKRect Bounds { get; private set; }

    public void Update(TextStyle style, SKPoint origin)
    {
        Style = style;
        Origin = origin;
        _e.Invalidate(_e.Document.Bounds);
    }

    public void DrawPreview(SKCanvas canvas) => Bounds = TextRenderer.Draw(canvas, Style, Origin);

    public void Commit()
    {
        _e.SetPreview(null);
        _e.AddText(Style, Origin);
        _e.Invalidate(_e.Document.Bounds);
    }

    public void Cancel()
    {
        _e.SetPreview(null);
        _e.Invalidate(_e.Document.Bounds);
    }
}
