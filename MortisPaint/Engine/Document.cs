using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MortisPaint.Engine;

public sealed class Document : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Calques de bas (index 0) en haut.</summary>
    public List<Layer> Layers { get; } = new();

    int _activeIndex;
    public int ActiveIndex
    {
        get => _activeIndex;
        set => _activeIndex = Math.Clamp(value, 0, Math.Max(0, Layers.Count - 1));
    }

    public Layer ActiveLayer => Layers[ActiveIndex];

    public SKColor PaperColor { get; set; } = SKColors.White;
    public bool PaperVisible { get; set; } = true;

    public string Name { get; set; } = "Sans titre";

    /// <summary>Chemin du fichier .mpaint dans la galerie (null tant que jamais enregistré).</summary>
    public string? ProjectPath { get; set; }

    public bool IsDirty { get; set; }

    public Document(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public static Document CreateBlank(int width, int height, string name)
    {
        var doc = new Document(width, height) { Name = name };
        doc.Layers.Add(new Layer(width, height, "Calque 1"));
        return doc;
    }

    public SKRectI Bounds => new(0, 0, Width, Height);

    public string NextLayerName()
    {
        int n = Layers.Count + 1;
        while (Layers.Exists(l => l.Name == $"Calque {n}")) n++;
        return $"Calque {n}";
    }

    public void Dispose()
    {
        foreach (var l in Layers) l.Dispose();
        Layers.Clear();
    }
}
