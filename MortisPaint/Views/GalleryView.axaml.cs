using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Engine;
using MortisPaint.Services;
using SkiaSharp;

namespace MortisPaint.Views;

public partial class GalleryView : UserControl
{
    static IBrush SheetBrush => MortisTheme.Elevated;
    static readonly IBrush DeskBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
    static IBrush RuleBrush => MortisTheme.LineStrong;

    static readonly (string Name, int W, int H)[] Presets =
    {
        ("Portrait HD", 1080, 1920),
        ("Paysage HD", 1920, 1080),
        ("Carré", 2048, 2048),
        ("Illustration", 1600, 2000),
        ("A4 150 dpi", 1240, 1754),
        ("A4 300 dpi", 2480, 3508),
        ("Manga B5", 1800, 2560),
        ("Fond d'écran", 1440, 3120),
        ("Bannière", 1500, 500),
        ("Icône", 512, 512),
        ("Pixel art 64", 64, 64),
        ("Pixel art 256", 256, 256),
    };

    const int MaxSide = 6000;

    public static readonly FilePickerFileType PsdFileType = new("Photoshop / ibisPaint (PSD)")
    {
        Patterns = new[] { "*.psd" },
        MimeTypes = new[] { "image/vnd.adobe.photoshop", "application/x-photoshop", "application/octet-stream" },
        AppleUniformTypeIdentifiers = new[] { "com.adobe.photoshop-image" },
    };

    List<GalleryItem> _items = new();
    double _cardWidth = 180;
    readonly List<(Border Card, Border Thumb)> _cards = new();

    public GalleryView()
    {
        InitializeComponent();
        NewButton.Click += (_, _) => ShowNewCanvas();
        EmptyNewButton.Click += (_, _) => ShowNewCanvas();
        ImportButton.Click += async (_, _) => await ImportAsync();
        NewCancel.Click += (_, _) => NewCanvasLayer.IsVisible = false;
        NewCreate.Click += (_, _) => CreateCanvas();
        SwapButton.Click += (_, _) => (WidthBox.Text, HeightBox.Text) = (HeightBox.Text, WidthBox.Text);
        WidthBox.TextChanged += (_, _) => UpdateNewInfo();
        HeightBox.TextChanged += (_, _) => UpdateNewInfo();
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CreateCanvas(); };

        foreach (var p in Presets)
        {
            var b = new Button
            {
                Classes = { "chip" },
                Padding = new Thickness(10, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{p.W} × {p.H}", Classes = { "faint", "mono" } },
                    },
                },
            };
            b.Click += (_, _) =>
            {
                WidthBox.Text = p.W.ToString();
                HeightBox.Text = p.H.ToString();
            };
            PresetPanel.Children.Add(b);
        }

        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) LayoutCards();
        };
    }

    public bool HandleBack()
    {
        if (NewCanvasLayer.IsVisible)
        {
            NewCanvasLayer.IsVisible = false;
            return true;
        }
        return false;
    }

    public void Refresh()
    {
        try
        {
            _items = GalleryStore.List();
        }
        catch (Exception ex)
        {
            _items = new List<GalleryItem>();
            _ = MainView.Current?.ShowMessageAsync("Galerie", "Impossible de lire la galerie : " + ex.Message);
        }
        CountText.Text = _items.Count == 0 ? "Galerie" : $"Galerie · {_items.Count} œuvre{(_items.Count > 1 ? "s" : "")}";
        EmptyState.IsVisible = _items.Count == 0;
        BuildCards();
    }

    void ComputeCardWidth()
    {
        double w = Math.Max(240, (Bounds.Width > 0 ? Bounds.Width : 800) - 20);
        int cols = Math.Max(2, (int)(w / 220));
        _cardWidth = Math.Floor(w / cols) - 12;
        bool roomy = Bounds.Width <= 0 || Bounds.Width > 460;
        NewButtonText.IsVisible = roomy;
        ImportButtonText.IsVisible = roomy;
    }

    void LayoutCards()
    {
        ComputeCardWidth();
        foreach (var (card, thumb) in _cards)
        {
            card.Width = _cardWidth;
            thumb.Height = _cardWidth * 0.85;
        }
    }

    void BuildCards()
    {
        CardsPanel.Children.Clear();
        _cards.Clear();
        ComputeCardWidth();
        foreach (var item in _items) CardsPanel.Children.Add(BuildCard(item));
    }

    Control BuildCard(GalleryItem item)
    {
        // La miniature est posée sur le plan de travail comme une feuille, avec une ombre franche.
        var img = new Image { Stretch = Stretch.Uniform };
        var sheet = new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 18 -4 #C0000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = img,
        };
        var thumb = new Border
        {
            Height = _cardWidth * 0.85,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = DeskBrush,
            Padding = new Thickness(14),
            Child = sheet,
            Margin = new Thickness(6, 6, 6, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _ = LoadThumbAsync(item.Path, img);

        var texts = new StackPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = item.Name, Classes = { "title" }, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{item.Width}×{item.Height} · {item.Modified:dd/MM HH:mm}", Classes = { "faint", "mono" }, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var more = new Button { Classes = { "icon", "small" }, Content = Icons.Make(Icons.More, 18), Flyout = BuildCardMenu(item) };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 2, 4, 6) };
        bottom.Children.Add(texts);
        Grid.SetColumn(more, 1);
        bottom.Children.Add(more);

        thumb.Tapped += (_, _) => _ = OpenAsync(item.Path);
        texts.Tapped += (_, _) => _ = OpenAsync(item.Path);

        var card = new Border
        {
            Width = _cardWidth,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = SheetBrush,
            BorderBrush = MortisTheme.Line,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("inset 0 1 0 0 #0DFFFFFF"),
            Child = new StackPanel { Children = { thumb, bottom } },
        };
        _cards.Add((card, thumb));
        return card;
    }

    static async Task LoadThumbAsync(string path, Image target)
    {
        var png = await Task.Run(() => ProjectIO.ReadThumbnailPng(path));
        target.Source = SkiaBitmaps.FromPng(png);
    }

    MenuFlyout BuildCardMenu(GalleryItem item)
    {
        var menu = new MenuFlyout();
        void Add(string header, Func<Task> action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) => await action();
            menu.Items.Add(mi);
        }
        Add("Ouvrir", () => OpenAsync(item.Path));
        Add("Renommer…", async () =>
        {
            var name = await MainView.Current!.PromptAsync("Renommer l'œuvre", item.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            await RunBusy("Enregistrement…", () => GalleryStore.Rename(item, name));
            Refresh();
        });
        Add("Dupliquer", async () =>
        {
            await RunBusy("Copie…", () => GalleryStore.Duplicate(item));
            Refresh();
        });
        Add("Exporter en PNG…", () => ExportAsync(item, psd: false));
        Add("Exporter en PSD (ibisPaint, Photoshop)…", () => ExportAsync(item, psd: true));
        Add("Supprimer…", async () =>
        {
            if (!await MainView.Current!.ConfirmAsync("Supprimer l'œuvre ?", $"« {item.Name} » sera définitivement supprimée.", "Supprimer"))
                return;
            GalleryStore.Delete(item.Path);
            Refresh();
        });
        return menu;
    }

    async Task RunBusy(string text, Action work)
    {
        BusyText.Text = text;
        BusyLayer.IsVisible = true;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            await MainView.Current!.ShowMessageAsync("Erreur", ex.Message);
        }
        finally
        {
            BusyLayer.IsVisible = false;
        }
    }

    async Task OpenAsync(string path)
    {
        BusyText.Text = "Ouverture…";
        BusyLayer.IsVisible = true;
        try
        {
            var doc = await Task.Run(() => GalleryStore.Load(path));
            BusyLayer.IsVisible = false;
            MainView.Current?.OpenEditor(doc);
        }
        catch (Exception ex)
        {
            BusyLayer.IsVisible = false;
            await MainView.Current!.ShowMessageAsync("Impossible d'ouvrir", ex.Message);
        }
    }

    async Task ExportAsync(GalleryItem item, bool psd)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = psd ? "Exporter en PSD" : "Exporter en PNG",
            SuggestedFileName = item.Name + (psd ? ".psd" : ".png"),
            DefaultExtension = psd ? "psd" : "png",
            FileTypeChoices = new[] { psd ? PsdFileType : FilePickerFileTypes.ImagePng },
        });
        if (file == null) return;
        byte[]? data = null;
        await RunBusy("Export…", () =>
        {
            using var doc = GalleryStore.Load(item.Path);
            using var ms = new MemoryStream();
            if (psd) PsdFile.Write(doc, ms);
            else ProjectIO.ExportImage(doc, ms, SKEncodedImageFormat.Png);
            data = ms.ToArray();
        });
        if (data == null) return;
        await using var s = await file.OpenWriteAsync();
        await s.WriteAsync(data);
    }

    async Task ImportAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ouvrir un projet ou une image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Projets, PSD et images") { Patterns = new[] { "*.mpaint", "*.psd", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" }, MimeTypes = new[] { "image/*", "image/vnd.adobe.photoshop", "application/x-photoshop", "application/zip", "application/octet-stream" } },
                FilePickerFileTypes.All,
            },
        });
        var file = files.FirstOrDefault();
        if (file == null) return;

        BusyText.Text = "Importation…";
        BusyLayer.IsVisible = true;
        try
        {
            using var ms = new MemoryStream();
            await using (var s = await file.OpenReadAsync()) await s.CopyToAsync(ms);
            string fileName = file.Name;
            var doc = await Task.Run(() => ImportDocument(ms, fileName));
            BusyLayer.IsVisible = false;
            MainView.Current?.OpenEditor(doc);
        }
        catch (Exception ex)
        {
            BusyLayer.IsVisible = false;
            await MainView.Current!.ShowMessageAsync("Import impossible", ex.Message);
        }
    }

    static Document ImportDocument(MemoryStream ms, string fileName)
    {
        ms.Position = 0;
        var head = ms.GetBuffer();
        bool isZip = ms.Length > 4 && head[0] == (byte)'P' && head[1] == (byte)'K';
        bool isPsd = ms.Length > 4 && head[0] == (byte)'8' && head[1] == (byte)'B' && head[2] == (byte)'P' && head[3] == (byte)'S';
        if (isPsd)
        {
            var psdDoc = PsdFile.Read(ms, Path.GetFileNameWithoutExtension(fileName));
            psdDoc.ProjectPath = AppPaths.NewProjectPath();
            psdDoc.IsDirty = true;
            return psdDoc;
        }
        if (isZip)
        {
            var doc = ProjectIO.Load(ms);
            doc.ProjectPath = AppPaths.NewProjectPath();
            doc.Id = Guid.NewGuid();
            return doc;
        }
        var bmp = BitmapUtil.Decode(ms) ?? throw new InvalidDataException("Format d'image non reconnu.");
        if (bmp.Width > MaxSide || bmp.Height > MaxSide)
        {
            float s = MaxSide / (float)Math.Max(bmp.Width, bmp.Height);
            var scaled = BitmapUtil.Scaled(bmp, (int)(bmp.Width * s), (int)(bmp.Height * s));
            bmp.Dispose();
            bmp = scaled;
        }
        var d = new Document(bmp.Width, bmp.Height) { Name = Path.GetFileNameWithoutExtension(fileName) };
        d.Layers.Add(new Layer(bmp, "Image"));
        d.Layers.Add(new Layer(bmp.Width, bmp.Height, "Calque 2"));
        d.ActiveIndex = 1;
        return d;
    }

    // ---------------------------------------------------------------- nouvelle toile

    void ShowNewCanvas()
    {
        WidthBox.Text ??= "1080";
        HeightBox.Text ??= "1920";
        if (string.IsNullOrEmpty(WidthBox.Text)) WidthBox.Text = "1080";
        if (string.IsNullOrEmpty(HeightBox.Text)) HeightBox.Text = "1920";
        NameBox.Text = $"Œuvre {_items.Count + 1}";
        UpdateNewInfo();
        NewCanvasLayer.IsVisible = true;
    }

    bool TryGetSize(out int w, out int h)
    {
        w = h = 0;
        return int.TryParse(WidthBox.Text, out w) && int.TryParse(HeightBox.Text, out h)
               && w >= 16 && h >= 16 && w <= MaxSide && h <= MaxSide;
    }

    void UpdateNewInfo()
    {
        if (!TryGetSize(out var w, out var h))
        {
            NewInfo.Text = $"Taille entre 16 et {MaxSide} px.";
            NewCreate.IsEnabled = false;
            return;
        }
        NewCreate.IsEnabled = true;
        double mb = w * (double)h * 4 / 1024 / 1024;
        NewInfo.Text = $"{mb:0.#} Mo par calque" + (mb > 40 ? " — grande toile : prévois peu de calques sur mobile." : "");
    }

    void CreateCanvas()
    {
        if (!TryGetSize(out var w, out var h)) return;
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Sans titre" : NameBox.Text!.Trim();
        NewCanvasLayer.IsVisible = false;
        var doc = Document.CreateBlank(w, h, name);
        MainView.Current?.OpenEditor(doc);
    }
}
