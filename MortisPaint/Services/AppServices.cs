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

    /// <summary>Dossier des timelapses : un sous-dossier par œuvre, nommé d'après son Id.</summary>
    public static string TimelapseRoot => Path.Combine(Root, "Timelapse");

    /// <summary>Dossier du timelapse d'une œuvre. L'Id est un Guid : jamais un chemin venu d'un fichier.</summary>
    public static string TimelapseDir(Guid id) => Path.Combine(TimelapseRoot, id.ToString("N"));
}

/// <summary>Écriture atomique : le contenu va dans un fichier temporaire qui ne remplace la cible qu'une fois complet.</summary>
public static class AtomicFile
{
    /// <summary>
    /// Écrit <paramref name="path"/> via « path.tmp » : si l'écriture échoue (stockage plein, manque de mémoire),
    /// l'exception remonte et la version précédente reste intacte.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true); // sur le disque avant de remplacer l'ancienne version
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
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

    /// <summary>
    /// Enregistre le projet dans la galerie (écriture atomique via fichier temporaire). En cas d'échec, l'exception
    /// remonte, la version précédente du fichier reste intacte et le document reste « modifié ».
    /// </summary>
    public static void Save(PaintEngine engine)
    {
        var doc = engine.Document;
        doc.ProjectPath ??= AppPaths.NewProjectPath();
        AtomicFile.Write(doc.ProjectPath, fs => ProjectIO.Save(engine, fs));
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
        // Le timelapse de l'œuvre part avec elle. L'Id vient du fichier : seul un Guid valide sert à bâtir le chemin.
        if (ProjectIO.TryParseId(info?.Id, out var id))
        {
            var dir = AppPaths.TimelapseDir(id);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Image verrouillée : l'œuvre est supprimée, son timelapse orphelin partira au prochain nettoyage.
            }
        }
    }

    /// <summary>
    /// Nettoyage de la galerie : timelapses sans œuvre (dossier nommé d'après un Guid qu'aucun projet ne porte)
    /// et fichiers temporaires d'enregistrements interrompus. Prudent : aucun timelapse n'est effacé si un projet
    /// est illisible, ni rien de ce qui a bougé dans l'heure (une toile neuve enregistre son timelapse avant d'être enregistrée).
    /// </summary>
    public static void CleanOrphans()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        try
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            bool complete = true;
            foreach (var f in Directory.EnumerateFiles(AppPaths.Gallery, "*" + ProjectIO.Extension))
            {
                var info = ProjectIO.ReadInfo(f);
                if (info == null) complete = false;
                else if (info.Value.Id != null) ids.Add(info.Value.Id);
            }
            foreach (var tmp in Directory.EnumerateFiles(AppPaths.Gallery, "*" + ProjectIO.Extension + ".tmp"))
                if (File.GetLastWriteTimeUtc(tmp) < cutoff) TryDelete(() => File.Delete(tmp));
            if (!complete || !Directory.Exists(AppPaths.TimelapseRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.TimelapseRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var id) || ids.Contains(id.ToString("N"))) continue;
                if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
                TryDelete(() => Directory.Delete(dir, recursive: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage non critique : on réessaiera à la prochaine ouverture.
        }
    }

    static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            // Écriture atomique : une coupure en pleine écriture ne doit pas effacer palette et pinceaux.
            var json = System.Text.Encoding.UTF8.GetBytes(ProjectIO.SettingsToJson(Data));
            AtomicFile.Write(AppPaths.SettingsFile, s => s.Write(json, 0, json.Length));
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
