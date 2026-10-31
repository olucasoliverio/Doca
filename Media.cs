using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Doca;

/// <summary>
/// What is playing right now (Spotify, YouTube in Chrome, any app that talks to Windows' media overlay),
/// through the same API as the volume flyout's media controls. Event driven; all state lives on the UI thread.
/// </summary>
internal static class NowPlaying
{
    private static MediaManager? manager;
    private static MediaSession? session;
    private static int version; // drops results of async reads that finished after the session changed

    public static bool HasSession => session != null && Title.Length > 0;
    public static string Title { get; private set; } = "";
    public static string Artist { get; private set; } = "";
    public static string? AppId { get; private set; }
    public static ImageSource? Art { get; private set; }
    public static bool Playing { get; private set; }
    public static bool CanPrevious { get; private set; }
    public static bool CanNext { get; private set; }
    private static TimeSpan position, duration;
    private static DateTimeOffset positionAt;

    /// <summary>Raised on the UI thread when the track, artwork or play state changes.</summary>
    public static event Action? Changed;

    public static async void Start()
    {
        try
        {
            manager = await MediaManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => OnUi(() => Attach(manager.GetCurrentSession()));
            manager.SessionsChanged += (_, _) => OnUi(() => Attach(manager.GetCurrentSession()));
            Attach(manager.GetCurrentSession());
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static void Attach(MediaSession? s)
    {
        if (s != null && ReferenceEquals(s, session)) { RefreshAll(); return; }
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnMedia;
            session.PlaybackInfoChanged -= OnPlayback;
            session.TimelinePropertiesChanged -= OnTimeline;
        }
        session = s;
        version++;
        if (s == null)
        {
            Title = Artist = "";
            Art = null;
            AppId = null;
            Playing = false;
            Changed?.Invoke();
            return;
        }
        s.MediaPropertiesChanged += OnMedia;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
        AppId = s.SourceAppUserModelId;
        RefreshAll();
    }

    private static void OnMedia(MediaSession s, MediaPropertiesChangedEventArgs e) => OnUi(() => _ = RefreshMedia());
    private static void OnPlayback(MediaSession s, PlaybackInfoChangedEventArgs e) => OnUi(RefreshPlayback);
    private static void OnTimeline(MediaSession s, TimelinePropertiesChangedEventArgs e) => OnUi(RefreshTimeline);

    private static void RefreshAll()
    {
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshMedia();
    }

    private static async Task RefreshMedia()
    {
        var s = session;
        int v = version;
        if (s == null) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (v != version || p == null) return;
            bool trackChanged = p.Title != Title || p.Artist != Artist;
            Title = p.Title ?? "";
            Artist = p.Artist ?? "";
            if (trackChanged || Art == null) Art = p.Thumbnail != null ? await LoadArt(p.Thumbnail) : null;
            if (v != version) return;
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Decoded small (the flyout shows it at 64 px) and frozen, so it costs ~100 KB, not the full cover.</summary>
    private static async Task<ImageSource?> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            copy.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = copy;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void RefreshPlayback()
    {
        if (session == null) return;
        try
        {
            var info = session.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool prev = info.Controls.IsPreviousEnabled, next = info.Controls.IsNextEnabled;
            if (playing == Playing && prev == CanPrevious && next == CanNext) return;
            Playing = playing;
            CanPrevious = prev;
            CanNext = next;
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void RefreshTimeline()
    {
        if (session == null) return;
        try
        {
            var t = session.GetTimelineProperties();
            position = t.Position - t.StartTime;
            duration = t.EndTime - t.StartTime;
            positionAt = t.LastUpdatedTime;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>
    /// Apps report the position only now and then (Spotify on seek/pause); in between it is
    /// extrapolated from the last report while playing.
    /// </summary>
    public static (TimeSpan position, TimeSpan duration)? Timeline()
    {
        if (duration <= TimeSpan.Zero) return null;
        var pos = position;
        if (Playing && positionAt.Year > 2000) pos += DateTimeOffset.Now - positionAt;
        return (pos < TimeSpan.Zero ? TimeSpan.Zero : pos > duration ? duration : pos, duration);
    }

    public static async void PlayPause() { try { if (session != null) await session.TryTogglePlayPauseAsync(); } catch (Exception ex) { App.Log(ex); } }
    public static async void Next() { try { if (session != null) await session.TrySkipNextAsync(); } catch (Exception ex) { App.Log(ex); } }
    public static async void Previous() { try { if (session != null) await session.TrySkipPreviousAsync(); } catch (Exception ex) { App.Log(ex); } }

    /// <summary>Brings the player app forward (Store apps and Chrome alike are AppsFolder entries).</summary>
    public static void OpenApp()
    {
        if (AppId != null) Shell.OpenAumid(AppId);
    }

    public static string? AppName => AppId == null ? null : Shell.DisplayName(@"shell:AppsFolder\" + AppId);
}

/// <summary>Cover, title, artist, timeline and ◀◀ ▶ ▶▶. Used by the music flyout and the Control Center.</summary>
internal sealed class PlayerView : StackPanel
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Brush hover = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly Brush artPlaceholder = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush trackBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private readonly Border art = new() { CornerRadius = new CornerRadius(8), Background = artPlaceholder };
    private readonly TextBlock artGlyph = new() { Text = "", FontFamily = new FontFamily(IconFont), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock artist = new() { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock app = new() { FontSize = 11, Opacity = 0.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Grid timeline = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border fill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock elapsed = new() { FontSize = 11, Opacity = 0.55 }, remaining = new() { FontSize = 11, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border prev, play, next;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public PlayerView(bool compact)
    {
        double artSize = compact ? 48 : 64;
        art.Width = art.Height = artSize;
        artGlyph.FontSize = artSize * 0.38;
        art.Child = artGlyph;

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        texts.Children.Add(title);
        texts.Children.Add(artist);
        if (!compact) texts.Children.Add(app);

        prev = Button("", 15, NowPlaying.Previous, "Anterior");
        play = Button("", compact ? 18 : 22, NowPlaying.PlayPause, null);
        next = Button("", 15, NowPlaying.Next, "Próxima");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(prev);
        buttons.Children.Add(play);
        buttons.Children.Add(next);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(art);
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);
        // Clicking the cover or the names opens the player, like macOS' Now Playing.
        foreach (var e in new FrameworkElement[] { art, texts })
        {
            e.Cursor = System.Windows.Input.Cursors.Hand;
            e.MouseLeftButtonUp += (_, _) => NowPlaying.OpenApp();
        }
        Children.Add(header);

        if (compact)
        {
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);
        }
        else
        {
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = trackBrush };
            timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timeline.Children.Add(track);
            timeline.Children.Add(fill);
            var times = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            times.Children.Add(elapsed);
            times.Children.Add(remaining);
            Grid.SetRow(times, 1);
            timeline.Children.Add(times);
            Children.Add(timeline);
            buttons.Margin = new Thickness(0, 4, 0, 0);
            Children.Add(buttons);
            timeline.SizeChanged += (_, _) => UpdateTimeline();
            tick.Tick += (_, _) => UpdateTimeline();
        }

        // Subscribed only while on screen: the flyouts are short-lived.
        Loaded += (_, _) => { NowPlaying.Changed += Update; Update(); if (!compact) tick.Start(); };
        Unloaded += (_, _) => { NowPlaying.Changed -= Update; tick.Stop(); };
    }

    private static Border Button(string glyph, double size, Action click, string? tip)
    {
        var b = new Border
        {
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = size + 22, Height = size + 18, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private void Update()
    {
        title.Text = NowPlaying.HasSession ? NowPlaying.Title : "Nada tocando";
        artist.Text = NowPlaying.Artist;
        app.Text = NowPlaying.AppName ?? "";
        art.Background = NowPlaying.Art is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : artPlaceholder;
        artGlyph.Visibility = NowPlaying.Art == null ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)play.Child).Text = NowPlaying.Playing ? "" : "";
        play.ToolTip = NowPlaying.Playing ? "Pausar" : "Tocar";
        prev.Opacity = NowPlaying.CanPrevious ? 1 : 0.35;
        next.Opacity = NowPlaying.CanNext ? 1 : 0.35;
        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        var t = NowPlaying.Timeline();
        timeline.Visibility = t != null && NowPlaying.HasSession ? Visibility.Visible : Visibility.Collapsed;
        if (t is not { } v) return;
        fill.Width = Math.Max(4, timeline.ActualWidth * (v.position.TotalSeconds / v.duration.TotalSeconds));
        elapsed.Text = Format(v.position);
        remaining.Text = "-" + Format(v.duration - v.position);
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

/// <summary>Music item in the menu bar → this panel.</summary>
internal sealed class MediaFlyout : Flyout
{
    public MediaFlyout(DocaConfig cfg) : base(cfg, 320)
    {
        Body.Children.Add(new PlayerView(compact: false));
    }
}
