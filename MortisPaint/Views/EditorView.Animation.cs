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
    readonly Dictionary<LayerFolder, (int Key, WriteableBitmap Bitmap)> _frameThumbs = new();
    DispatcherTimer? _playTimer;
    bool _playing;
    bool _onionBeforePlay;

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

    void RebuildTimeline()
    {
        FrameStrip.Children.Clear();
        if (!_engine.Document.AnimationEnabled) return;
        var frames = _engine.Document.Frames();
        foreach (var dead in _frameThumbs.Keys.Where(f => !frames.Contains(f)).ToList()) _frameThumbs.Remove(dead);
        var cur = _engine.CurrentFrame;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            bool current = f == cur;
            var img = new Image { Source = FrameThumb(f), Stretch = Stretch.Uniform, Height = 44, Width = 58 };
            var content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Border { Background = Brushes.White, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = img },
                    new TextBlock { Text = (i + 1).ToString(), Classes = { "mono" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = current ? MortisTheme.TextStrong : MortisTheme.TextMute },
                },
            };
            var b = new Button
            {
                Classes = { "flat" },
                Padding = new Thickness(3),
                BorderThickness = new Thickness(1.5),
                BorderBrush = current ? MortisTheme.Accent : Brushes.Transparent,
                Background = current ? MortisTheme.Active : Brushes.Transparent,
                Content = content,
            };
            ToolTip.SetTip(b, f.Name);
            b.Click += (_, _) =>
            {
                CommitPending();
                StopPlayback();
                ClearLayerSelection();
                _engine.GoToFrame(f);
            };
            FrameStrip.Children.Add(b);
        }
    }

    static int FrameKey(Document doc, LayerFolder f)
    {
        int key = 17;
        foreach (var l in doc.Layers)
            if (l.IsIn(f)) key = key * 31 + l.Version * 7 + (l.Visible ? 1 : 0) + l.GetHashCode();
        return key;
    }

    WriteableBitmap FrameThumb(LayerFolder f)
    {
        var doc = _engine.Document;
        int key = FrameKey(doc, f);
        if (_frameThumbs.TryGetValue(f, out var t) && t.Key == key) return t.Bitmap;
        using var bmp = Compositor.RenderScaled(doc, 120, includePaper: true, new FrameFilter { Frame = f });
        var wb = SkiaBitmaps.FromSkia(bmp, 120, checker: false);
        _frameThumbs[f] = (key, wb);
        return wb;
    }

    void RefreshFrameThumbs()
    {
        if (_engine.Document.AnimationEnabled && !_playing) RebuildTimeline();
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
        for (int k = 0; k < FrameStrip.Children.Count; k++)
            if (FrameStrip.Children[k] is Button b)
            {
                b.BorderBrush = k == index ? MortisTheme.Accent : Brushes.Transparent;
                b.Background = k == index ? MortisTheme.Active : Brushes.Transparent;
            }
    }

    void StartPlayback()
    {
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
        if (file == null) return;
        try
        {
            var frames = _engine.RenderFrames(720);
            int delay = (int)Math.Round(100.0 / Math.Max(1, doc.Fps));
            var bytes = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                using (var gif = new GifEncoder(ms, frames[0].Width, frames[0].Height))
                    foreach (var f in frames) gif.AddFrame(f, delay);
                foreach (var f in frames) f.Dispose();
                return ms.ToArray();
            });
            await using var s = await file.OpenWriteAsync();
            await s.WriteAsync(bytes);
            ShowToast($"Animation exportée ({frames.Count} images, {doc.Fps} i/s)");
        }
        catch (Exception ex)
        {
            ShowToast("Export impossible : " + ex.Message);
        }
    }
}
