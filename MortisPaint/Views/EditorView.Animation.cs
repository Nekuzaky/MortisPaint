using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MortisPaint.Controls;
using MortisPaint.Engine;

namespace MortisPaint.Views;

/// <summary>Mode animation : chaque dossier de premier niveau est une image (comme dans ibisPaint).</summary>
public partial class EditorView
{
    /// <summary>Case de la bande des images : gardée tant que la liste des images ne change pas.</summary>
    sealed record FrameCell(LayerFolder Frame, Button Button, Image Image, TextBlock Label);

    const int FrameThumbSide = 120;
    const int GifSide = 720;

    readonly Dictionary<LayerFolder, (int Key, WriteableBitmap Bitmap)> _frameThumbs = new();
    readonly List<FrameCell> _frameCells = new();
    ScaledRenderer? _frameRenderer;
    DispatcherTimer? _frameThumbTimer;
    DispatcherTimer? _playTimer;
    bool _playing;
    bool _onionBeforePlay;
    bool _exportingGif;

    static readonly int[] FpsChoices = { 2, 4, 6, 8, 12, 15, 24 };

    void BuildAnimation()
    {
        FramePrev.Click += (_, _) => StepFrame(-1);
        FrameNext.Click += (_, _) => StepFrame(1);
        PlayButton.Click += (_, _) => { if (_playing) StopPlayback(); else StartPlayback(); };
        FrameAdd.Click += (_, _) => { CommitPending(); ClearLayerSelection(); _engine.AddFrame(); };
        FrameDuplicate.Click += (_, _) => { CommitPending(); ClearLayerSelection(); _engine.DuplicateFrame(); };
        FrameDelete.Click += (_, _) =>
        {
            CommitPending();
            if (_engine.Document.Frames().Count <= 1) { ShowToast("Il faut au moins une image"); return; }
            ClearLayerSelection();
            _engine.DeleteFrame();
        };
        OnionToggle.Click += (_, _) =>
        {
            _engine.Document.OnionSkin = OnionToggle.IsChecked == true;
            _engine.Document.IsDirty = true;
            _engine.Invalidate(_engine.Document.Bounds);
        };
        ExportGifButton.Click += (_, _) => _ = ExportAnimationGifAsync();
        var fps = new MenuFlyout();
        foreach (int f in FpsChoices)
        {
            int v = f;
            var mi = new MenuItem { Header = $"{v} images / s" };
            mi.Click += (_, _) =>
            {
                _engine.Document.Fps = v;
                _engine.Document.IsDirty = true;
                FpsText.Text = $"{v} i/s";
                if (_playing) { StopPlayback(); StartPlayback(); }
            };
            fps.Items.Add(mi);
        }
        FpsButton.Flyout = fps;
        _playTimer = new DispatcherTimer();
        _playTimer.Tick += (_, _) => StepFrame(1, wrap: true);
        // Les miniatures périmées sont refaites ensemble, un court instant après le dernier changement.
        _frameThumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _frameThumbTimer.Tick += (_, _) =>
        {
            _frameThumbTimer.Stop();
            UpdateFrameThumbs();
        };
    }

    void ToggleAnimation()
    {
        CommitPending();
        StopPlayback();
        bool on = !_engine.Document.AnimationEnabled;
        ClearLayerSelection();
        _engine.SetAnimation(on);
        if (on) ShowToast("Mode animation : chaque dossier de premier niveau est une image");
        UpdateAnimationUi();
    }

    void UpdateAnimationUi()
    {
        var doc = _engine.Document;
        bool on = doc.AnimationEnabled;
        Timeline.IsVisible = on;
        AnimationButton.Classes.Set("active", on);
        OnionToggle.IsChecked = doc.OnionSkin;
        FpsText.Text = $"{doc.Fps} i/s";
        UpdateGuidesButton();
        if (on) RebuildTimeline();
    }

    /// <summary>
    /// Met la bande des images à jour. Les boutons ne sont recréés que si la liste des images change ;
    /// sinon seuls l'image courante, les numéros et les miniatures périmées sont mis à jour.
    /// </summary>
    void RebuildTimeline()
    {
        if (_closed) return;
        if (!_engine.Document.AnimationEnabled)
        {
            FrameStrip.Children.Clear();
            _frameCells.Clear();
            return;
        }
        var frames = _engine.Document.Frames();
        foreach (var dead in _frameThumbs.Keys.Where(f => !frames.Contains(f)).ToList()) _frameThumbs.Remove(dead);
        if (_frameCells.Count != FrameStrip.Children.Count || !_frameCells.Select(c => c.Frame).SequenceEqual(frames))
        {
            FrameStrip.Children.Clear();
            _frameCells.Clear();
            foreach (var f in frames)
            {
                var cell = NewFrameCell(f);
                _frameCells.Add(cell);
                FrameStrip.Children.Add(cell.Button);
            }
        }
        for (int i = 0; i < _frameCells.Count; i++)
        {
            var cell = _frameCells[i];
            cell.Label.Text = (i + 1).ToString();
            ToolTip.SetTip(cell.Button, cell.Frame.Name);
        }
        var cur = _engine.CurrentFrame;
        HighlightFrame(cur == null ? -1 : frames.IndexOf(cur));
        ScheduleFrameThumbs();
    }

    FrameCell NewFrameCell(LayerFolder f)
    {
        // Miniature déjà connue (même périmée) affichée tout de suite ; sinon rendue maintenant.
        var img = new Image
        {
            Source = _frameThumbs.TryGetValue(f, out var t) ? t.Bitmap : FrameThumb(f),
            Stretch = Stretch.Uniform, Height = 44, Width = 58,
        };
        var label = new TextBlock { Classes = { "mono" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        var content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new Border { Background = Brushes.White, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = img },
                label,
            },
        };
        var b = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(3),
            BorderThickness = new Thickness(1.5),
            Content = content,
        };
        b.Click += (_, _) =>
        {
            CommitPending();
            StopPlayback();
            ClearLayerSelection();
            _engine.GoToFrame(f);
        };
        return new FrameCell(f, b, img, label);
    }

    /// <summary>
    /// Clé de la miniature d'une image : tout ce qui change son rendu. Pixels, propriétés et ordre des
    /// calques de l'image et du fond commun (calques hors dossier, présents dans toutes les images),
    /// propriétés des dossiers, papier. Les autres images n'y entrent pas : en ajouter une ne périme
    /// pas les miniatures existantes.
    /// </summary>
    static int FrameKey(Document doc, LayerFolder f)
    {
        var h = new HashCode();
        h.Add(doc.Width);
        h.Add(doc.Height);
        h.Add(doc.PaperVisible);
        h.Add(doc.PaperColor);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var l = doc.Layers[i];
            if (l.Folder != null && !l.IsIn(f)) continue; // autre image : masquée
            // Calque du fond écrêté juste au-dessus d'une autre image : sa base est masquée.
            if (l.Clipping && l.Folder == null) h.Add(i > 0 && doc.Layers[i - 1].Folder != null && !doc.Layers[i - 1].IsIn(f));
            h.Add(l);
            h.Add(l.Version);
            h.Add(l.Visible);
            h.Add(l.Opacity);
            h.Add(l.BlendMode);
            h.Add(l.Clipping);
            for (var p = l.Folder; p != null; p = p.Parent)
            {
                h.Add(p);
                h.Add(p.Visible);
                h.Add(p.Opacity);
                h.Add(p.BlendMode);
            }
        }
        return h.ToHashCode();
    }

    /// <summary>Un filtre ou une transformation modifie le calque actif sur place, sans changer sa version.</summary>
    bool EditingInPlace => _filter != null || Canvas.Transform != null;

    WriteableBitmap FrameThumb(LayerFolder f)
    {
        var doc = _engine.Document;
        int key = FrameKey(doc, f);
        if (_frameThumbs.TryGetValue(f, out var t) && t.Key == key) return t.Bitmap;
        // Composition réduite directe : les calques inchangés sont repris du cache du rendu.
        bool editing = EditingInPlace;
        _frameRenderer ??= new ScaledRenderer();
        using var bmp = _frameRenderer.Render(doc, FrameThumbSide, includePaper: true, new FrameFilter { Frame = f },
            live: editing ? doc.ActiveLayer : null);
        var wb = SkiaBitmaps.FromSkia(bmp, FrameThumbSide, checker: false);
        // Rendue pendant un aperçu : clé volontairement fausse, elle sera refaite après.
        _frameThumbs[f] = (editing ? unchecked(key + 1) : key, wb);
        return wb;
    }

    void ScheduleFrameThumbs()
    {
        _frameThumbTimer?.Stop();
        _frameThumbTimer?.Start();
    }

    /// <summary>Refait les seules miniatures dont le rendu a changé, sans toucher aux boutons.</summary>
    void UpdateFrameThumbs()
    {
        if (_closed || _playing || !_engine.Document.AnimationEnabled) return;
        if (EditingInPlace)
        {
            // Aperçu en cours (le calque peut encore être annulé) : on attend qu'il soit validé.
            ScheduleFrameThumbs();
            return;
        }
        foreach (var cell in _frameCells)
        {
            var wb = FrameThumb(cell.Frame);
            if (!ReferenceEquals(cell.Image.Source, wb)) cell.Image.Source = wb;
        }
    }

    /// <summary>Appelé après un changement de pixels (déjà regroupé par le minuteur des miniatures).</summary>
    void RefreshFrameThumbs() => UpdateFrameThumbs();

    /// <summary>À la fermeture de l'éditeur : minuteur des miniatures arrêté, cache du rendu réduit libéré.</summary>
    void CloseAnimation()
    {
        _frameThumbTimer?.Stop();
        _frameRenderer?.Dispose();
        _frameRenderer = null;
    }

    void StepFrame(int delta, bool wrap = false)
    {
        var frames = _engine.Document.Frames();
        if (!_engine.Document.AnimationEnabled || frames.Count == 0) return;
        var cur = _engine.CurrentFrame;
        int i = cur == null ? 0 : frames.IndexOf(cur);
        int j = i + delta;
        if (wrap) j = ((j % frames.Count) + frames.Count) % frames.Count;
        if (j < 0 || j >= frames.Count) return;
        if (!_playing) CommitPending();
        _engine.GoToFrame(frames[j]);
        if (_playing) HighlightFrame(j);
    }

    void HighlightFrame(int index)
    {
        for (int k = 0; k < _frameCells.Count; k++)
        {
            var cell = _frameCells[k];
            bool current = k == index;
            cell.Button.BorderBrush = current ? MortisTheme.Accent : Brushes.Transparent;
            cell.Button.Background = current ? MortisTheme.Active : Brushes.Transparent;
            cell.Label.Foreground = current ? MortisTheme.TextStrong : MortisTheme.TextMute;
        }
    }

    void StartPlayback()
    {
        if (_exportingGif)
        {
            ShowToast("Export du GIF en cours");
            return;
        }
        if (!_engine.Document.AnimationEnabled || _engine.Document.Frames().Count < 2)
        {
            ShowToast("Ajoute au moins deux images pour lire l'animation");
            return;
        }
        CommitPending();
        _playing = true;
        _onionBeforePlay = _engine.Document.OnionSkin;
        _engine.Document.OnionSkin = false;
        Canvas.InputLocked = true;
        PlayIcon.Data = Icons.Stop;
        _playTimer!.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _engine.Document.Fps));
        _playTimer.Start();
    }

    void StopPlayback()
    {
        if (!_playing) return;
        _playing = false;
        _playTimer?.Stop();
        _engine.Document.OnionSkin = _onionBeforePlay;
        Canvas.InputLocked = false;
        PlayIcon.Data = Icons.Play;
        _engine.Invalidate(_engine.Document.Bounds);
        RebuildLayers();
        UpdateTitle();
        RebuildTimeline();
    }

    async Task ExportAnimationGifAsync()
    {
        if (_exportingGif)
        {
            ShowToast("Export du GIF déjà en cours");
            return;
        }
        CommitPending();
        StopPlayback();
        var doc = _engine.Document;
        if (!doc.AnimationEnabled || doc.Frames().Count < 2)
        {
            ShowToast("Active le mode animation et crée au moins deux images");
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exporter l'animation",
            SuggestedFileName = doc.Name + ".gif",
            DefaultExtension = "gif",
            FileTypeChoices = new[] { GifFileType },
        });
        if (file == null || _closed || _engine.Document != doc) return;
        try
        {
            await using var s = await file.OpenWriteAsync();
            int count = await WriteAnimationGifAsync(s);
            if (count > 0) ShowToast($"Animation exportée ({count} images, {doc.Fps} i/s)");
        }
        catch (Exception ex)
        {
            ShowToast("Export impossible : " + ex.Message);
        }
    }

    /// <summary>
    /// Écrit l'animation en GIF au fil de l'eau. Chaque image est composée directement à la taille de
    /// sortie sur le fil d'interface (le seul à lire les calques), puis quantifiée et compressée en
    /// arrière-plan pendant que la suivante se compose : deux images au plus en mémoire, et l'interface
    /// traite ses événements entre deux images. Renvoie le nombre d'images, 0 si l'éditeur a été fermé
    /// ou la toile remplacée entre-temps.
    /// </summary>
    async Task<int> WriteAnimationGifAsync(Stream output)
    {
        var doc = _engine.Document;
        var frames = doc.Frames();
        var delays = GifEncoder.FrameDelays(frames.Count, doc.Fps);
        var size = ScaledRenderer.FitSize(doc.Width, doc.Height, GifSide);
        var buffered = new BufferedStream(output, 1 << 16);
        var gif = new GifEncoder(buffered, size.Width, size.Height);
        using var renderer = new ScaledRenderer();
        _exportingGif = true;
        Canvas.InputLocked = true;
        Task encoding = Task.CompletedTask;
        try
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (_closed || _engine.Document != doc) return 0;
                ShowToast($"Création du GIF… {i + 1}/{frames.Count}");
                var bmp = renderer.Render(doc, GifSide, includePaper: true, new FrameFilter { Frame = frames[i] });
                int delay = delays[i];
                var previous = encoding;
                encoding = Task.Run(async () =>
                {
                    try
                    {
                        await previous.ConfigureAwait(false);
                        gif.AddFrame(bmp, delay);
                    }
                    finally
                    {
                        bmp.Dispose();
                    }
                });
                // L'image précédente doit être encodée avant de composer la suivante (mémoire bornée),
                // puis on laisse passer saisie et rendu.
                await previous;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            await encoding;
            await Task.Run(() =>
            {
                gif.Dispose();
                buffered.Flush();
            });
            return frames.Count;
        }
        finally
        {
            // Le fichier n'est pas refermé tant qu'une image est en cours d'encodage.
            try { await encoding; }
            catch (Exception) { /* déjà signalée par l'attente principale */ }
            _exportingGif = false;
            if (!_playing) Canvas.InputLocked = false;
        }
    }
}
