using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Doca;

/// <summary>Something short-lived for the island to show.</summary>
internal abstract record IslandActivity;
/// <summary>Volume or brightness, 0–1.</summary>
internal sealed record LevelActivity(string Glyph, double Level, bool Muted) : IslandActivity;
internal sealed record DeviceActivity(string Name, bool Connected) : IslandActivity;

internal static class IslandGlyphs
{
    public const string Brightness = "", Mute = "", Headphones = "", Music = "", Pause = "";
    public static string Volume(double level, bool muted) =>
        muted || level < 0.01 ? Mute : level < 0.34 ? "" : level < 0.67 ? "" : "";
}

/// <summary>Event hub: volume, brightness and Bluetooth changes reach every island (one per menu bar).</summary>
internal static class Island
{
    /// <summary>Raised on the UI thread.</summary>
    public static event Action<IslandActivity>? Activity;
    private static bool started;

    public static void Notify(IslandActivity a) => Activity?.Invoke(a);

    public static void Start()
    {
        if (started) return;
        started = true;
        Audio.StartWatching();
        Audio.VolumeChanged += (level, muted) => Notify(new LevelActivity(IslandGlyphs.Volume(level, muted), level, muted));
        BluetoothWatch.Start();
    }
}

/// <summary>
/// Paired Bluetooth devices connecting/disconnecting (headphones, speakers), event driven through a
/// DeviceWatcher on the association endpoints' IsConnected property: no polling.
/// </summary>
internal static class BluetoothWatch
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private static DeviceWatcher? watcher;
    private static readonly Dictionary<string, (string name, bool connected)> devices = new();
    private static bool ready; // the initial enumeration lists what is already connected: not news

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public static void Start()
    {
        try
        {
            watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                new[] { IsConnected }, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (_, d) =>
            {
                bool c = d.Properties.TryGetValue(IsConnected, out var v) && v is true;
                OnUi(() => devices[d.Id] = (d.Name, c));
            };
            watcher.Updated += (_, u) =>
            {
                if (!u.Properties.TryGetValue(IsConnected, out var v) || v is not bool c) return;
                OnUi(() =>
                {
                    if (!devices.TryGetValue(u.Id, out var known) || known.connected == c) return;
                    devices[u.Id] = (known.name, c);
                    if (ready && known.name.Length > 0) Island.Notify(new DeviceActivity(known.name, c));
                });
            };
            watcher.Removed += (_, u) => OnUi(() => devices.Remove(u.Id));
            watcher.EnumerationCompleted += (_, _) => OnUi(() => ready = true);
            watcher.Start();
        }
        catch (Exception ex) { App.Log(ex); }
    }
}

/// <summary>
/// iPhone-style Dynamic Island at the top center of a menu bar: a black pill that shows what is playing,
/// grows for a moment on volume/brightness changes, new tracks and Bluetooth devices, and opens into a
/// player on hover. Owned by its bar (always above it, hidden with it). The window has a fixed size and
/// its region follows the pill, so the rest of it never takes clicks; the spring animation runs on
/// CompositionTarget.Rendering only while the pill changes shape.
/// </summary>
internal sealed class IslandWindow : Window
{
    private const double MaxW = 440, MaxH = 220;  // window size (DIP); the pill lives inside it
    private const double Inset = 3;                // gap above the collapsed pill, like the bar's items
    private const double MusicWidth = 168;

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    private readonly DocaConfig cfg;
    private readonly Window bar;
    private readonly IntPtr monitor;
    private readonly Border pill = new() { Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly Grid host = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly RectangleGeometry clip = new();
    private IntPtr hwnd;
    private double scale = 1;

    // Spring state: current size, velocity, target.
    private double w, h, vw, vh, tw, th;
    private bool animating, hideAtEnd;
    private TimeSpan lastFrame;

    private readonly MusicView music = new();
    private LevelView? level;
    private TrackView? track;
    private FrameworkElement? current;
    private IslandActivity? hud;
    private bool trackHud, hovering;
    private string? lastTrack;
    private readonly DispatcherTimer hudTimer = new(), hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) },
        leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private double Collapsed => Math.Max(16, cfg.MenuBarHeight - 2 * Inset);

    public IslandWindow(DocaConfig cfg, Window bar, IntPtr monitor)
    {
        this.cfg = cfg;
        this.bar = bar;
        this.monitor = monitor;
        Title = "Doca Island";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent; // hit-testable everywhere the region lets the mouse in
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000;
        Width = MaxW;
        Height = MaxH;
        FontFamily = new FontFamily(cfg.Font);
        FontSize = 13;
        Foreground = Brushes.White;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;

        pill.Margin = host.Margin = new Thickness(0, Inset, 0, 0);
        host.Clip = clip;
        var root = new Grid();
        root.Children.Add(pill);
        root.Children.Add(host);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            Glass.MakeOverlay(this);
            hwnd = new WindowInteropHelper(this).Handle;
            SetRegion(0, 0);
            Place();
        };

        hudTimer.Tick += (_, _) => { hudTimer.Stop(); hud = null; trackHud = false; Refresh(); };
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); hovering = true; Refresh(); };
        leaveTimer.Tick += (_, _) =>
        {
            leaveTimer.Stop();
            if (IsMouseOver) return;
            hovering = false;
            Refresh();
        };
        MouseEnter += (_, _) => { leaveTimer.Stop(); if (!hovering && NowPlaying.HasSession) hoverTimer.Start(); };
        MouseLeave += (_, _) => { hoverTimer.Stop(); if (hovering) leaveTimer.Start(); };
        // A click on the small pill opens the player right away.
        MouseLeftButtonUp += (_, _) => { if (!hovering && NowPlaying.HasSession) { hoverTimer.Stop(); hovering = true; Refresh(); } };

        Island.Activity += OnActivity;
        NowPlaying.Changed += OnMedia;
        Closed += (_, _) =>
        {
            Island.Activity -= OnActivity;
            NowPlaying.Changed -= OnMedia;
            hudTimer.Stop();
            hoverTimer.Stop();
            leaveTimer.Stop();
            if (animating) CompositionTarget.Rendering -= OnFrame;
        };
        lastTrack = NowPlaying.HasSession ? NowPlaying.Title : null;
        Refresh();
    }

    /// <summary>Centered on the bar, top edges aligned (called whenever the bar moves).</summary>
    public void Place()
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr barHwnd = new WindowInteropHelper(bar).Handle;
        if (!Native.GetWindowRect(barHwnd, out var r)) return;
        scale = Native.MonitorInfo(monitor).Scale;
        int width = (int)Math.Round(MaxW * scale), height = (int)Math.Round(MaxH * scale);
        Glass.Place(this, r.Left + (r.Right - r.Left - width) / 2, r.Top, width, height);
        SetRegion(pill.Visibility == Visibility.Visible ? Math.Max(w, tw) : 0, Math.Max(h, th));
    }

    #region State

    private void OnActivity(IslandActivity a)
    {
        if (hovering) return; // the player is open: don't cover it
        hud = a;
        trackHud = false;
        hudTimer.Interval = TimeSpan.FromSeconds(a is DeviceActivity ? 3 : 1.6);
        hudTimer.Stop();
        hudTimer.Start();
        Refresh();
    }

    private void OnMedia()
    {
        string? title = NowPlaying.HasSession ? NowPlaying.Title : null;
        bool newTrack = title != null && lastTrack != null && title != lastTrack && NowPlaying.Playing;
        lastTrack = title ?? lastTrack;
        if (newTrack && !hovering && cfg.ShowNowPlaying)
        {
            hud = null;
            trackHud = true;
            hudTimer.Interval = TimeSpan.FromSeconds(3);
            hudTimer.Stop();
            hudTimer.Start();
        }
        if (!NowPlaying.HasSession && hovering) hovering = false;
        music.Update();
        track?.Update();
        Refresh();
    }

    /// <summary>Picks what the pill shows: player (hover) › activity › track › music › nothing.</summary>
    private void Refresh()
    {
        bool hasMusic = cfg.ShowNowPlaying && NowPlaying.HasSession;
        if (hovering && NowPlaying.HasSession)
        {
            if (current is PlayerShell) return;
            Show(new PlayerShell(), 372, null);
        }
        else if (hud is LevelActivity la)
        {
            level ??= new LevelView();
            level.Set(la);
            Show(level, 260, 34);
        }
        else if (hud is DeviceActivity da) Show(new DeviceView(da), 290, 34);
        else if (trackHud && hasMusic)
        {
            track ??= new TrackView();
            track.Update();
            Show(track, 310, 52);
        }
        else if (hasMusic)
        {
            music.Update();
            Show(music, MusicWidth, Collapsed);
        }
        else Show(null, 0, 0);
    }

    /// <param name="height">null: measured from the content at this width.</param>
    private void Show(FrameworkElement? view, double width, double? height)
    {
        if (view == null)
        {
            if (current == null && !animating) return;
            current = null;
            host.Children.Clear();
            // Shrink into a small pill, then vanish.
            hideAtEnd = true;
            AnimateTo(Collapsed * 2, Collapsed);
            return;
        }
        hideAtEnd = false;
        double hgt = height ?? Measure(view, width);
        if (view != current)
        {
            host.Children.Clear();
            host.Children.Add(view);
            current = view;
            // New content fades in while the pill reshapes around it.
            view.Opacity = 0;
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(70) });
        }
        host.Width = width;
        host.Height = hgt;
        if (pill.Visibility != Visibility.Visible)
        {
            // Appear from a small pill in the middle of the bar.
            pill.Visibility = Visibility.Visible;
            w = Collapsed * 2; h = Collapsed; vw = vh = 0;
        }
        AnimateTo(width, hgt);
    }

    private static double Measure(FrameworkElement view, double width)
    {
        view.Measure(new Size(width, double.PositiveInfinity));
        return Math.Min(MaxH - Inset, Math.Ceiling(view.DesiredSize.Height));
    }

    #endregion

    #region Spring animation

    private void AnimateTo(double width, double height)
    {
        tw = width;
        th = height;
        // Region covers both shapes while moving; trimmed to the final shape at the end.
        SetRegion(Math.Max(w, tw), Math.Max(h, th));
        if (animating) return;
        animating = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (lastFrame == TimeSpan.Zero) { lastFrame = now; Apply(); return; }
        double dt = Math.Clamp((now - lastFrame).TotalSeconds, 0, 1 / 30.0);
        lastFrame = now;
        if (dt == 0) return;
        Step(ref w, ref vw, tw, dt);
        Step(ref h, ref vh, th, dt);
        bool done = Math.Abs(w - tw) < 0.3 && Math.Abs(h - th) < 0.3 && Math.Abs(vw) < 2 && Math.Abs(vh) < 2;
        if (done)
        {
            w = tw; h = th; vw = vh = 0;
            animating = false;
            CompositionTarget.Rendering -= OnFrame;
            if (hideAtEnd)
            {
                pill.Visibility = Visibility.Collapsed;
                hideAtEnd = false;
                SetRegion(0, 0);
            }
            else SetRegion(w, h);
        }
        Apply();
    }

    /// <summary>Slightly under-damped spring (damping ratio ~0.75): a small overshoot, like iOS.</summary>
    private static void Step(ref double x, ref double v, double target, double dt)
    {
        const double k = 420, c = 31;
        for (int i = 0; i < 4; i++)
        {
            double d = dt / 4;
            v += (k * (target - x) - c * v) * d;
            x += v * d;
        }
    }

    private void Apply()
    {
        double pw = Math.Max(0, w), ph = Math.Max(0, h);
        double r = Math.Min(ph / 2, 22);
        pill.Width = pw;
        pill.Height = ph;
        pill.CornerRadius = new CornerRadius(r);
        // The content keeps its final layout; the clip reveals it as the pill grows.
        double hw = double.IsNaN(host.Width) ? pw : host.Width;
        clip.Rect = new Rect((hw - pw) / 2, 0, pw, ph);
        clip.RadiusX = clip.RadiusY = r;
    }

    private void SetRegion(double width, double height)
    {
        if (hwnd == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) { SetWindowRgn(hwnd, CreateRectRgn(0, 0, 0, 0), true); return; }
        int full = (int)Math.Round(MaxW * scale);
        int pw = (int)Math.Ceiling(width * scale), ph = (int)Math.Ceiling(height * scale), top = (int)Math.Round(Inset * scale);
        int left = (full - pw) / 2;
        // The window owns the region afterwards; no DeleteObject.
        SetWindowRgn(hwnd, CreateRectRgn(left, top, left + pw, top + ph), true);
    }

    #endregion

    #region Views

    private static Border Art(double size, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
    };

    private static void SetArt(Border art, TextBlock? fallback)
    {
        art.Background = NowPlaying.Art is { } img
            ? new ImageBrush(img) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        if (fallback != null) fallback.Visibility = NowPlaying.Art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TextBlock Glyph(string g, double size) => new()
    {
        Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Resting pill: cover on the left, equalizer (or pause) on the right, like the iPhone.</summary>
    private sealed class MusicView : Grid
    {
        private readonly Border art = Art(16, 4);
        private readonly TextBlock note = Glyph(IslandGlyphs.Music, 9);
        private readonly Equalizer eq = new() { HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock paused = Glyph(IslandGlyphs.Pause, 10);

        public MusicView()
        {
            art.HorizontalAlignment = HorizontalAlignment.Left;
            art.Margin = new Thickness(5, 0, 0, 0);
            art.Child = note;
            paused.HorizontalAlignment = HorizontalAlignment.Right;
            paused.Margin = new Thickness(0, 0, 11, 0);
            paused.Opacity = 0.6;
            eq.Margin = new Thickness(0, 0, 11, 0);
            Children.Add(art);
            Children.Add(eq);
            Children.Add(paused);
        }

        public void Update()
        {
            SetArt(art, note);
            eq.Playing = NowPlaying.Playing;
            paused.Visibility = NowPlaying.Playing ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Volume/brightness: glyph, level bar, percent. Updated in place while keys repeat.</summary>
    private sealed class LevelView : Grid
    {
        private readonly TextBlock glyph = Glyph("", 14);
        private readonly Border fill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock pct = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.8 };
        private const double TrackWidth = 150;

        public LevelView()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            glyph.Margin = new Thickness(10, 0, 0, 0);
            var track = new Grid { Width = TrackWidth, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            pct.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(pct, 2);
            Children.Add(glyph);
            Children.Add(track);
            Children.Add(pct);
        }

        public void Set(LevelActivity a)
        {
            double v = Math.Clamp(a.Level, 0, 1);
            glyph.Text = a.Glyph;
            fill.Width = a.Muted ? 0 : Math.Max(5, TrackWidth * v);
            pct.Text = a.Muted ? "—" : $"{Math.Round(v * 100)}";
        }
    }

    /// <summary>New track: cover, title and artist for a few seconds.</summary>
    private sealed class TrackView : Grid
    {
        private readonly Border art = Art(36, 8);
        private readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock artist = new() { FontSize = 11.5, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Equalizer eq = new() { Margin = new Thickness(10, 0, 16, 0) };

        public TrackView()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            art.Margin = new Thickness(8, 0, 10, 0);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(title);
            texts.Children.Add(artist);
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(eq, 2);
            Children.Add(art);
            Children.Add(texts);
            Children.Add(eq);
        }

        public void Update()
        {
            SetArt(art, null);
            title.Text = NowPlaying.Title;
            artist.Text = NowPlaying.Artist;
            artist.Visibility = NowPlaying.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            eq.Playing = NowPlaying.Playing;
        }
    }

    private sealed class DeviceView : Grid
    {
        public DeviceView(DeviceActivity a)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = Glyph(IslandGlyphs.Headphones, 14);
            glyph.Margin = new Thickness(10, 0, 0, 0);
            var name = new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var state = new TextBlock
            {
                Text = a.Connected ? "Conectado" : "Desconectado", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 14, 0),
                Foreground = a.Connected ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(state, 2);
            Children.Add(glyph);
            Children.Add(name);
            Children.Add(state);
        }
    }

    /// <summary>Hover: the full player of the music panel (cover, timeline, ◀◀ ▶ ▶▶).</summary>
    private sealed class PlayerShell : Border
    {
        public PlayerShell()
        {
            Padding = new Thickness(18, 16, 18, 12);
            Child = new PlayerView(compact: false);
        }
    }

    #endregion
}
