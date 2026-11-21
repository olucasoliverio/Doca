using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// <summary>A named thing changing state: a Bluetooth device, Caps Lock.</summary>
internal sealed record StatusActivity(string Glyph, string Title, string State, bool On, bool SymbolFont = false) : IslandActivity;

internal static class IslandGlyphs
{
    public const string Brightness = "", Mute = "", Headphones = "", Music = "", Pause = "",
        Calendar = "", Controls = "", Tray = "", More = "";
    public static string Volume(double level, bool muted) =>
        muted || level < 0.01 ? Mute : level < 0.34 ? "" : level < 0.67 ? "" : "";
}

/// <summary>Event hub: volume, brightness, Bluetooth and Caps Lock changes reach every island.</summary>
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
        CapsLockWatch.Start();
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
                    if (ready && known.name.Length > 0)
                        Island.Notify(new StatusActivity(IslandGlyphs.Headphones, known.name, c ? "Conectado" : "Desconectado", c));
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
/// Caps Lock on/off. Raw input with RIDEV_INPUTSINK delivers key events to a background window without a
/// global keyboard hook (a hook would sit in the path of every keystroke, games included).
/// </summary>
internal static class CapsLockWatch
{
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    private const int WM_INPUT = 0x00FF, VK_CAPITAL = 0x14;
    private const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100;

    private static HwndSource? window;
    private static bool held, on;

    public static void Start()
    {
        try
        {
            window = new HwndSource(new HwndSourceParameters("Doca.Keys") { Width = 0, Height = 0, WindowStyle = 0 });
            var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = window.Handle } };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) return;
            on = (GetKeyState(VK_CAPITAL) & 1) != 0;
            window.AddHook(Hook);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        uint header = (uint)(8 + 2 * IntPtr.Size); // RAWINPUTHEADER
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0 || size > 1024) return IntPtr.Zero;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size || Marshal.ReadInt32(buf) != 1 /* RIM_TYPEKEYBOARD */) return IntPtr.Zero;
            // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (USHORTs)
            int flags = (ushort)Marshal.ReadInt16(buf, (int)header + 2), vk = (ushort)Marshal.ReadInt16(buf, (int)header + 6);
            if (vk != VK_CAPITAL) return IntPtr.Zero;
            if ((flags & 1) != 0) { held = false; return IntPtr.Zero; } // RI_KEY_BREAK
            if (held) return IntPtr.Zero; // auto-repeat while held does not toggle
            held = true;
            // The toggle state updates after the key is processed. If it reads unchanged (state not synced to
            // this background thread), the key still toggled: flip what we had.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                bool read = (GetKeyState(VK_CAPITAL) & 1) != 0;
                on = read != on ? read : !on;
                Island.Notify(new StatusActivity("⇪", "Caps Lock", on ? "Ativado" : "Desativado", on, SymbolFont: true));
            };
            t.Start();
        }
        finally { Marshal.FreeHGlobal(buf); }
        return IntPtr.Zero;
    }
}

/// <summary>
/// iPhone-style Dynamic Island. Two modes:
/// <list type="bullet">
/// <item>On a menu bar (bar != null): owned by the bar, shows the music and short activities; hover opens the player.</item>
/// <item>Standalone ("Só a ilha"): no bar at all. A clock at rest (plus cover/equalizer while music plays);
/// hover opens a tabbed panel — Música, Calendário, Controles, Bandeja. Right-click: the system menu.</item>
/// </list>
/// The window has a fixed size and its region follows the pill, so the rest of it never takes clicks; the
/// spring animation runs on CompositionTarget.Rendering only while the pill changes shape.
/// </summary>
internal sealed class IslandWindow : Window
{
    private const double MaxW = 440, MaxH = 580; // window size (DIP); the pill lives inside it

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    private readonly DocaConfig cfg;
    private readonly Window? bar;
    private readonly WindowTracker tracker;
    private readonly IntPtr monitor;
    private readonly bool standalone;
    private readonly double inset;
    private readonly Border pill = new() { Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly Grid host = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly RectangleGeometry clip = new();
    private IntPtr hwnd;
    private double scale = 1;

    // Spring state: current size, velocity, target.
    private double w, h, vw, vh, tw, th;
    private bool animating, hideAtEnd;
    private TimeSpan lastFrame;

    private readonly RestView rest;
    private LevelView? level;
    private TrackView? track;
    private Dashboard? dashboard;
    private FrameworkElement? current;
    private IslandActivity? hud;
    private bool trackHud, hovering;
    private int menusOpen;
    private string? lastTrack;
    private readonly DispatcherTimer hudTimer = new(), hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) },
        leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) }, clockTimer = new();

    private double Collapsed => standalone ? Math.Max(26, cfg.MenuBarHeight + 2) : Math.Max(16, cfg.MenuBarHeight - 6);

    /// <param name="bar">The menu bar this island sits on, or null for the standalone island.</param>
    public IslandWindow(DocaConfig cfg, IntPtr monitor, WindowTracker tracker, Window? bar = null)
    {
        this.cfg = cfg;
        this.bar = bar;
        this.tracker = tracker;
        this.monitor = monitor;
        standalone = bar == null;
        inset = standalone ? 7 : 3;
        rest = new RestView(standalone);
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

        pill.Margin = host.Margin = new Thickness(0, inset, 0, 0);
        host.Clip = clip;
        // Content whose height changes by itself (panel tabs, data loaded later): the pill follows.
        host.SizeChanged += (_, _) =>
        {
            if (current != null && !hideAtEnd && Math.Abs(host.ActualHeight - th) > 0.5) AnimateTo(tw, host.ActualHeight);
        };
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
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); Expand(); };
        leaveTimer.Tick += (_, _) =>
        {
            // Stay open while the pointer is back, a slider is being dragged, or a menu/panel opened from here is up.
            if (IsMouseOver || Mouse.Captured != null || menusOpen > 0 || Flyout.IsOpen) return;
            leaveTimer.Stop();
            Collapse();
        };
        MouseEnter += (_, _) => { leaveTimer.Stop(); if (!hovering && CanExpand) hoverTimer.Start(); };
        MouseLeave += (_, _) => { hoverTimer.Stop(); if (hovering) leaveTimer.Start(); };
        // A click on the small pill opens it right away.
        MouseLeftButtonUp += (_, _) => { if (!hovering && CanExpand) { hoverTimer.Stop(); Expand(); } };
        if (standalone)
        {
            // No bar, so no ⊞ menu: right-click the island for it.
            MouseRightButtonUp += (_, e) => { e.Handled = true; ShowSystemMenu(); };
            clockTimer.Tick += (_, _) => UpdateClock();
            tracker.FullscreenChanged += UpdateVisibility;
            UpdateClock();
        }

        Island.Activity += OnActivity;
        NowPlaying.Changed += OnMedia;
        Closed += (_, _) =>
        {
            Island.Activity -= OnActivity;
            NowPlaying.Changed -= OnMedia;
            if (standalone) tracker.FullscreenChanged -= UpdateVisibility;
            hudTimer.Stop();
            hoverTimer.Stop();
            leaveTimer.Stop();
            clockTimer.Stop();
            if (animating) CompositionTarget.Rendering -= OnFrame;
            host.Children.Clear(); // releases a panel's borrowed content
        };
        lastTrack = NowPlaying.HasSession ? NowPlaying.Title : null;
        Refresh();
    }

    private bool CanExpand => standalone || NowPlaying.HasSession;

    /// <summary>"Doca.exe --island": open the panel, then the next tab on each call, then close.</summary>
    public void StepPanel()
    {
        if (!standalone || !IsVisible) return;
        if (!hovering) { Expand(); return; }
        if (dashboard != null && dashboard.Next()) return;
        Collapse();
    }

    /// <summary>Top center: on the bar (top edges aligned), or of the monitor when standalone.</summary>
    public void Place()
    {
        if (hwnd == IntPtr.Zero) return;
        var m = Native.MonitorInfo(monitor);
        scale = m.Scale;
        int width = (int)Math.Round(MaxW * scale), height = (int)Math.Round(MaxH * scale);
        int x = m.Bounds.Left + (m.Bounds.Width - width) / 2, y = m.Bounds.Top;
        if (bar != null && Native.GetWindowRect(new WindowInteropHelper(bar).Handle, out var r))
        {
            x = r.Left + (r.Right - r.Left - width) / 2;
            y = r.Top;
        }
        Glass.Place(this, x, y, width, height);
        SetRegion(pill.Visibility == Visibility.Visible ? Math.Max(w, tw) : 0, Math.Max(h, th));
    }

    /// <summary>Standalone: hidden while something is fullscreen on this monitor (video, game).</summary>
    private void UpdateVisibility()
    {
        if (tracker.IsFullscreen(monitor)) { if (IsVisible) { Collapse(); Hide(); } }
        else if (!IsVisible) { Show(); Place(); }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        rest.Clock = cfg.ClockShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
        clockTimer.Interval = cfg.ClockShowSeconds
            ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 10)
            : TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 30);
        clockTimer.Start();
    }

    private void ShowSystemMenu()
    {
        var m = MenuBarWindow.SystemMenu(cfg);
        m.PlacementTarget = pill;
        m.Placement = PlacementMode.Bottom;
        m.VerticalOffset = 4;
        m.Opened += (_, _) => menusOpen++;
        m.Closed += (_, _) => menusOpen = Math.Max(0, menusOpen - 1);
        Ui.CloseOnOutsideClick(m);
        m.IsOpen = true;
    }

    #region State

    private void Expand()
    {
        hovering = true;
        hud = null;
        trackHud = false;
        hudTimer.Stop();
        Refresh();
    }

    private void Collapse()
    {
        if (!hovering) return;
        hovering = false;
        Refresh();
    }

    private void OnActivity(IslandActivity a)
    {
        if (hovering) return; // the panel is open: don't cover it
        hud = a;
        trackHud = false;
        hudTimer.Interval = TimeSpan.FromSeconds(a is StatusActivity ? 2.5 : 1.6);
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
        if (!standalone && !NowPlaying.HasSession && hovering) hovering = false;
        rest.Update(cfg.ShowNowPlaying);
        track?.Update();
        Refresh();
    }

    /// <summary>Picks what the pill shows: panel/player (hover) › activity › new track › rest › nothing.</summary>
    private void Refresh()
    {
        bool hasMusic = cfg.ShowNowPlaying && NowPlaying.HasSession;
        if (!hovering && dashboard != null) dashboard = null; // dropped from the tree below → borrowed content released

        if (hovering && standalone)
        {
            dashboard ??= new Dashboard(cfg, pill, () => { leaveTimer.Stop(); Collapse(); }, ShowSystemMenu);
            Show(dashboard, 416, null);
        }
        else if (hovering && NowPlaying.HasSession)
        {
            if (current is not PlayerShell) Show(new PlayerShell(), 372, null);
        }
        else if (hud is LevelActivity la)
        {
            level ??= new LevelView();
            level.Set(la);
            Show(level, 260, 34);
        }
        else if (hud is StatusActivity sa) Show(new StatusView(sa), 290, 34);
        else if (trackHud && hasMusic)
        {
            track ??= new TrackView();
            track.Update();
            Show(track, 310, 52);
        }
        else if (standalone || hasMusic)
        {
            rest.Update(cfg.ShowNowPlaying);
            double width = !standalone ? 168 : hasMusic ? 232 : cfg.ClockShowSeconds ? 118 : 100;
            Show(rest, width, Collapsed);
        }
        else Show(null, 0, 0);
    }

    /// <param name="height">Fixed height, or null to follow the content.</param>
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
        view.Height = height ?? double.NaN;
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
        if (pill.Visibility != Visibility.Visible)
        {
            // Appear from a small pill in the middle.
            pill.Visibility = Visibility.Visible;
            w = Collapsed * 2; h = Collapsed; vw = vh = 0;
        }
        AnimateTo(width, height ?? Measure(view, width));
    }

    private double Measure(FrameworkElement view, double width)
    {
        view.Measure(new Size(width, double.PositiveInfinity));
        return Math.Min(MaxH - inset, Math.Ceiling(view.DesiredSize.Height));
    }

    #endregion

    #region Spring animation

    private void AnimateTo(double width, double height)
    {
        tw = width;
        th = Math.Min(height, MaxH - inset);
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
        int pw = (int)Math.Ceiling(width * scale), ph = (int)Math.Ceiling(height * scale), top = (int)Math.Round(inset * scale);
        int left = (full - pw) / 2;
        // The window owns the region afterwards; no DeleteObject.
        SetWindowRgn(hwnd, CreateRectRgn(left, top, left + pw, top + ph), true);
    }

    #endregion

    #region Views

    private static readonly Brush artPlaceholder = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Border Art(double size, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), VerticalAlignment = VerticalAlignment.Center, Background = artPlaceholder,
    };

    private static void SetArt(Border art, TextBlock? fallback)
    {
        art.Background = NowPlaying.Art is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : artPlaceholder;
        if (fallback != null) fallback.Visibility = NowPlaying.Art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TextBlock Glyph(string g, double size, bool symbol = false) => new()
    {
        Text = g, FontFamily = new FontFamily(symbol ? "Segoe UI Symbol" : "Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Resting pill: cover on the left, equalizer (or pause) on the right; the clock in the middle when standalone.</summary>
    private sealed class RestView : Grid
    {
        private readonly Border art = Art(18, 5);
        private readonly TextBlock note = Glyph(IslandGlyphs.Music, 9);
        private readonly Equalizer eq = new() { HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock paused = Glyph(IslandGlyphs.Pause, 10);
        private readonly TextBlock clock = new() { FontWeight = FontWeights.SemiBold, FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        public RestView(bool withClock)
        {
            art.HorizontalAlignment = HorizontalAlignment.Left;
            art.Margin = new Thickness(6, 0, 0, 0);
            art.Child = note;
            paused.HorizontalAlignment = HorizontalAlignment.Right;
            paused.Margin = new Thickness(0, 0, 12, 0);
            paused.Opacity = 0.6;
            eq.Margin = new Thickness(0, 0, 12, 0);
            Children.Add(art);
            Children.Add(eq);
            Children.Add(paused);
            clock.Visibility = withClock ? Visibility.Visible : Visibility.Collapsed;
            Children.Add(clock);
        }

        public string Clock { set => clock.Text = value; }

        public void Update(bool showMusic)
        {
            bool music = showMusic && NowPlaying.HasSession;
            art.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
            if (music) SetArt(art, note);
            eq.Playing = music && NowPlaying.Playing;
            paused.Visibility = music && !NowPlaying.Playing ? Visibility.Visible : Visibility.Collapsed;
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
            var trackBar = new Grid { Width = TrackWidth, VerticalAlignment = VerticalAlignment.Center };
            trackBar.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
            trackBar.Children.Add(fill);
            Grid.SetColumn(trackBar, 1);
            pct.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(pct, 2);
            Children.Add(glyph);
            Children.Add(trackBar);
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

    /// <summary>Bluetooth device or Caps Lock: glyph, name, state (green when on).</summary>
    private sealed class StatusView : Grid
    {
        public StatusView(StatusActivity a)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = Glyph(a.Glyph, a.SymbolFont ? 15 : 14, a.SymbolFont);
            glyph.Margin = new Thickness(10, 0, 0, 0);
            var name = new TextBlock { Text = a.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var state = new TextBlock
            {
                Text = a.State, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 14, 0),
                Foreground = a.On ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(state, 2);
            Children.Add(glyph);
            Children.Add(name);
            Children.Add(state);
        }
    }

    /// <summary>Hover on a bar's island: the full player of the music panel.</summary>
    private sealed class PlayerShell : Border
    {
        public PlayerShell()
        {
            Padding = new Thickness(18, 16, 18, 12);
            Child = new PlayerView(compact: false);
        }
    }

    /// <summary>
    /// Standalone island's panel: tabs Música · Calendário · Controles · Bandeja (click or mouse wheel) and
    /// the system menu. Calendar and Control Center are the menu bar panels' own content, borrowed with
    /// Flyout.Detach and given back (window closed) when the tab changes or the island collapses.
    /// </summary>
    private sealed class Dashboard : StackPanel
    {
        private static int lastTab = -1;
        private static readonly (string glyph, string name)[] tabs =
        {
            (IslandGlyphs.Music, "Música"), (IslandGlyphs.Calendar, "Calendário"), (IslandGlyphs.Controls, "Controles"), (IslandGlyphs.Tray, "Bandeja"),
        };
        private static readonly Brush chipOn = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));

        private readonly DocaConfig cfg;
        private readonly FrameworkElement anchor;
        private readonly Action dismiss;
        private readonly Border[] chips = new Border[tabs.Length];
        private readonly Border body = new();
        private Flyout? lent;
        private int index = -1;

        public Dashboard(DocaConfig cfg, FrameworkElement anchor, Action dismiss, Action systemMenu)
        {
            this.cfg = cfg;
            this.anchor = anchor;
            this.dismiss = dismiss;
            Margin = new Thickness(14, 10, 14, 14);
            // Fluent dark styles for the borrowed panels' sliders and buttons, scoped to this panel (a window-wide
            // ThemeMode would also give the island window a backdrop).
            try { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml") }); }
            catch (Exception ex) { App.Log(ex); }

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < tabs.Length; i++)
            {
                int n = i;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(Glyph(tabs[i].glyph, 12));
                content.Children.Add(new TextBlock { Text = tabs[i].name, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var chip = new Border { Child = content, Padding = new Thickness(9, 5, 10, 6), CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand };
                BarFx.Prepare(chip);
                chip.MouseEnter += (_, _) => { if (index != n) BarFx.Hot(chip, true); };
                chip.MouseLeave += (_, _) => { if (index != n) BarFx.Hot(chip, false); };
                chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(n); };
                chips[i] = chip;
                row.Children.Add(chip);
            }
            header.Children.Add(row);
            var more = new Border { Child = Glyph(IslandGlyphs.More, 13), Width = 28, Height = 26, CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, ToolTip = "Menu do sistema" };
            BarFx.Prepare(more);
            more.MouseEnter += (_, _) => BarFx.Hot(more, true);
            more.MouseLeave += (_, _) => BarFx.Hot(more, false);
            more.MouseLeftButtonUp += (_, e) => { e.Handled = true; systemMenu(); };
            header.Children.Add(more);
            Children.Add(header);
            Children.Add(body);

            MouseWheel += (_, e) =>
            {
                // Sliders use the wheel themselves.
                if (e.OriginalSource is DependencyObject d && FindSlider(d)) return;
                e.Handled = true;
                Select((index + (e.Delta < 0 ? 1 : tabs.Length - 1)) % tabs.Length);
            };
            Unloaded += (_, _) => Release();
            Loaded += (_, _) => { if (index < 0) Select(lastTab >= 0 ? lastTab : NowPlaying.HasSession ? 0 : 1); };
        }

        /// <summary>Next tab; false when already on the last one.</summary>
        public bool Next()
        {
            if (index >= tabs.Length - 1) return false;
            Select(index + 1);
            return true;
        }

        private static bool FindSlider(DependencyObject d)
        {
            for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (x is Slider) return true;
            return false;
        }

        private void Select(int n)
        {
            if (n == index) return;
            if (index >= 0) { chips[index].Background = null; BarFx.Prepare(chips[index]); }
            index = lastTab = n;
            chips[n].Background = chipOn;
            Release();

            FrameworkElement content;
            switch (n)
            {
                case 0:
                    content = NowPlaying.HasSession
                        ? new PlayerView(compact: false) { Margin = new Thickness(4, 2, 4, 0) }
                        : Empty("Nada tocando agora.");
                    break;
                case 1: content = Borrow(new CalendarFlyout(cfg)); break;
                case 2: content = Borrow(new ControlCenterFlyout(cfg, embedded: true)); break;
                default: content = TrayGrid(); break;
            }
            content.Opacity = 0;
            body.Child = content;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

        private FrameworkElement Borrow(Flyout f)
        {
            lent = f;
            f.Dismissed += dismiss;
            return f.Detach(anchor);
        }

        private void Release()
        {
            body.Child = null;
            if (lent == null) return;
            lent.Dismissed -= dismiss;
            lent.Close();
            lent = null;
        }

        private static TextBlock Empty(string text) => new() { Text = text, Opacity = 0.55, Margin = new Thickness(4, 8, 4, 10), HorizontalAlignment = HorizontalAlignment.Center };

        /// <summary>Every app tray icon; clicks go to the app like in the menu bar.</summary>
        private FrameworkElement TrayGrid()
        {
            var icons = TrayIconsPanel.Ordered(cfg);
            if (icons.Count == 0) return Empty("Nenhum ícone na bandeja.");
            var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var icon in icons)
            {
                var b = TrayIconsPanel.Visual(icon, null, cfg.TrayIconsMonochrome);
                b.Padding = new Thickness(12, 10, 12, 10);
                b.Margin = new Thickness(2);
                b.MouseEnter += (_, _) => { BarFx.Hot(b, true); TrayIconsPanel.Place(b, icon); icon.IconMouseEnter(TrayIconsPanel.MousePos()); };
                b.MouseLeave += (_, _) => { BarFx.Hot(b, false); icon.IconMouseLeave(TrayIconsPanel.MousePos()); };
                b.MouseMove += (_, _) => icon.IconMouseMove(TrayIconsPanel.MousePos());
                b.MouseDown += (_, e) => { e.Handled = true; TrayIconsPanel.Place(b, icon); };
                b.MouseUp += (_, e) =>
                {
                    e.Handled = true;
                    // Apps that draw their menu in a normal window (Steam…) would open it under the island.
                    TrayIconsPanel.StepAside(b);
                    icon.IconMouseDown(e.ChangedButton, TrayIconsPanel.AnchorBelow(b), TrayIconsPanel.DoubleClick());
                    icon.IconMouseUp(e.ChangedButton, TrayIconsPanel.AnchorBelow(b), TrayIconsPanel.DoubleClick());
                };
                grid.Children.Add(b);
            }
            return grid;
        }
    }

    #endregion
}
