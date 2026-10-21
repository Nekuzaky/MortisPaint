using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MortisPaint.Engine;

namespace MortisPaint.Services;

public static class AppPaths
{
    /// <summary>Dossier racine alternatif (tests).</summary>
    public static string? RootOverride { get; set; }

    public static string Root
    {
        get
        {
            var dir = RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MortisPaint");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Gallery
    {
        get
        {
            var dir = Path.Combine(Root, "Galerie");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string NewProjectPath() => Path.Combine(Gallery, Guid.NewGuid().ToString("N") + ProjectIO.Extension);
}

public sealed record GalleryItem(string Path, string Name, int Width, int Height, DateTime Modified);

public static class GalleryStore
{
    public static List<GalleryItem> List()
    {
        var list = new List<GalleryItem>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Gallery, "*" + ProjectIO.Extension))
        {
            var info = ProjectIO.ReadInfo(f);
            if (info == null) continue;
            list.Add(new GalleryItem(f, info.Value.Name, info.Value.Width, info.Value.Height, File.GetLastWriteTime(f)));
        }
        return list.OrderByDescending(i => i.Modified).ToList();
    }

    /// <summary>Enregistre le projet dans la galerie (écriture atomique via fichier temporaire).</summary>
    public static void Save(PaintEngine engine)
    {
        var doc = engine.Document;
        doc.ProjectPath ??= AppPaths.NewProjectPath();
        var tmp = doc.ProjectPath + ".tmp";
        using (var fs = File.Create(tmp))
            ProjectIO.Save(engine, fs);
        File.Move(tmp, doc.ProjectPath, overwrite: true);
        doc.IsDirty = false;
    }

    public static Document Load(string path)
    {
        using var fs = File.OpenRead(path);
        var doc = ProjectIO.Load(fs);
        doc.ProjectPath = path;
        return doc;
    }

    public static void Delete(string path)
    {
        var info = ProjectIO.ReadInfo(path);
        if (File.Exists(path)) File.Delete(path);
        // Le timelapse de l'œuvre part avec elle.
        if (info?.Id is { } id)
        {
            var dir = Path.Combine(AppPaths.Root, "Timelapse", id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public static string Duplicate(GalleryItem item)
    {
        var doc = Load(item.Path);
        doc.Name = item.Name + " (copie)";
        doc.ProjectPath = AppPaths.NewProjectPath();
        doc.Id = Guid.NewGuid();
        using var engine = new PaintEngine(doc);
        Save(engine);
        return doc.ProjectPath;
    }

    public static void Rename(GalleryItem item, string name)
    {
        var doc = Load(item.Path);
        doc.Name = name;
        using var engine = new PaintEngine(doc);
        Save(engine);
    }
}

/// <summary>Préférences persistantes (palette, couleurs récentes, options tactiles).</summary>
public static class AppSettings
{
    static AppSettingsData? _data;

    public static AppSettingsData Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    _data = ProjectIO.SettingsFromJson(File.ReadAllText(AppPaths.SettingsFile));
            }
            catch
            {
                // Préférences illisibles : on repart des valeurs par défaut.
            }
            _data ??= new AppSettingsData();
            if (_data.Palette.Count == 0) _data.Palette.AddRange(DefaultPalette);
            return _data;
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, ProjectIO.SettingsToJson(Data));
        }
        catch
        {
            // Sauvegarde des préférences non critique.
        }
    }

    static readonly uint[] DefaultPalette =
    {
        0xFF000000, 0xFF3A3A44, 0xFF7A7A88, 0xFFBDBDC8, 0xFFFFFFFF, 0xFFFFE8D6,
        0xFFE53935, 0xFFFF7043, 0xFFFFB300, 0xFFFDD835, 0xFFC0CA33, 0xFF43A047,
        0xFF00897B, 0xFF00ACC1, 0xFF1E88E5, 0xFF3949AB, 0xFF8E24AA, 0xFFD81B60,
        0xFF6D4C41, 0xFFA1887F, 0xFFF8BBD0, 0xFFB3E5FC, 0xFFC8E6C9, 0xFFFFF9C4,
    };
}
