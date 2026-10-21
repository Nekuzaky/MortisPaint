using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace MortisPaint.Engine;

public sealed class Document : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Identifiant stable (sert au dossier du timelapse).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Calques de bas (index 0) en haut. Les calques d'un dossier sont contigus.</summary>
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

    // ------------------------------------------------------------ animation
    /// <summary>Mode animation : chaque dossier de premier niveau est une image.</summary>
    public bool AnimationEnabled { get; set; }
    public int Fps { get; set; } = 8;
    public bool OnionSkin { get; set; } = true;
    public float OnionOpacity { get; set; } = 0.3f;

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

    public string NextFolderName(string prefix = "Dossier")
    {
        var names = new HashSet<string>(AllFolders().Select(f => f.Name));
        int n = 1;
        while (names.Contains($"{prefix} {n}")) n++;
        return $"{prefix} {n}";
    }

    // ------------------------------------------------------------ dossiers

    /// <summary>Tous les dossiers utilisés (avec leurs parents), de bas en haut.</summary>
    public List<LayerFolder> AllFolders()
    {
        var list = new List<LayerFolder>();
        var seen = new HashSet<LayerFolder>();
        foreach (var l in Layers)
        {
            var chain = new List<LayerFolder>();
            for (var f = l.Folder; f != null; f = f.Parent) chain.Add(f);
            for (int i = chain.Count - 1; i >= 0; i--)
                if (seen.Add(chain[i])) list.Add(chain[i]);
        }
        return list;
    }

    /// <summary>Dossiers de premier niveau dans l'ordre (= images en mode animation).</summary>
    public List<LayerFolder> Frames() => AllFolders().Where(f => f.Parent == null).ToList();

    /// <summary>Plage [début, fin[ des calques contenus dans un dossier.</summary>
    public (int Start, int End) FolderRange(LayerFolder folder)
    {
        int start = -1, end = -1;
        for (int i = 0; i < Layers.Count; i++)
        {
            if (!Layers[i].IsIn(folder)) continue;
            if (start < 0) start = i;
            end = i + 1;
        }
        return start < 0 ? (0, 0) : (start, end);
    }

    /// <summary>Le dossier de premier niveau qui contient un calque (son image en animation).</summary>
    public static LayerFolder? TopFolder(Layer layer)
    {
        var f = layer.Folder;
        while (f?.Parent != null) f = f.Parent;
        return f;
    }

    public void Dispose()
    {
        foreach (var l in Layers) l.Dispose();
        Layers.Clear();
    }
}
