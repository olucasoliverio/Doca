using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Doca;

/// <summary>Something short-lived for the island to show.</summary>
internal abstract record IslandActivity;
/// <summary>Volume or brightness, 0–1.</summary>
internal sealed record LevelActivity(string Glyph, double Level, bool Muted) : IslandActivity;
/// <summary>A named thing changing state: a Bluetooth device, Caps Lock, a download, the timer.</summary>
/// <param name="Seconds">How long it stays.</param>
/// <param name="Click">What a click on the pill does while it shows (instead of opening the panel).</param>
/// <param name="StateBrush">Color of the state text (default: green when On, gray otherwise).</param>
internal sealed record StatusActivity(string Glyph, string Title, string State, bool On, bool SymbolFont = false,
    double Seconds = 2.5, Action? Click = null, Brush? StateBrush = null) : IslandActivity;

internal static class IslandGlyphs
{
    public const string Brightness = "", Mute = "", Headphones = "", Music = "", Pause = "",
        Calendar = "", Controls = "", Tray = "", More = "", Search = "";
    public static string Volume(double level, bool muted) =>
        muted || level < 0.01 ? Mute : level < 0.34 ? "" : level < 0.67 ? "" : "";
}

/// <summary>Event hub: volume, brightness, Bluetooth and Caps Lock changes reach every island.</summary>
internal static class Island
{
    /// <summary>Raised on the UI thread.</summary>
    public static event Action<IslandActivity>? Activity;
    private static bool started;
    public static DocaConfig? Config { get; private set; }

    public static void Notify(IslandActivity a) => Activity?.Invoke(a);

    public static void Start(DocaConfig cfg)
    {
        Config = cfg;
        if (started) return;
        started = true;
        Audio.StartWatching();
        Audio.VolumeChanged += (level, muted) => Notify(new LevelActivity(IslandGlyphs.Volume(level, muted), level, muted));
        BluetoothWatch.Start();
        CapsLockWatch.Start();
        // Each one checks its own setting when something happens, so the toggles apply without a restart.
        Shelf.Start(cfg);
        ClipboardHistory.Start(cfg);
        PrivacyWatch.Start();
        DownloadWatch.Start();
        Peripherals.Start();
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
                    if (!ready || known.name.Length == 0) return;
                    Island.Notify(new StatusActivity(IslandGlyphs.Headphones, known.name, c ? "Conectado" : "Desconectado", c));
                    if (c) ShowBattery(u.Id, known.name);
                });
            };
            watcher.Removed += (_, u) => OnUi(() => devices.Remove(u.Id));
            watcher.EnumerationCompleted += (_, _) => OnUi(() => ready = true);
            watcher.Start();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>
    /// The headset reports its battery a few seconds after connecting: look twice, then show "Conectado · 80%".
    /// The endpoint id ends with the device address ("…-aa:bb:cc:dd:ee:ff").
    /// </summary>
    private static async void ShowBattery(string id, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id.ToLowerInvariant(), "([0-9a-f]{2}(:[0-9a-f]{2}){5})$");
        if (!m.Success) return;
        ulong address = Convert.ToUInt64(m.Value.Replace(":", ""), 16);
        foreach (var wait in new[] { 2500, 5000 })
        {
            await Task.Delay(wait);
            await Bluetooth.RefreshBatteryAsync();
            var d = Bluetooth.Devices().FirstOrDefault(x => x.Address == address);
            if (d == null || !d.Connected) return;
            if (d.Battery is int b)
            {
                Island.Notify(new StatusActivity(IslandGlyphs.Headphones, name, $"Conectado · {b}%", true, Seconds: 3));
                return;
            }
        }
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
    private const double MaxW = 480, MaxH = 580; // window size (DIP); the notch lives inside it
    /// <summary>Bottom corner radius of the open panel (smaller shapes use half their height: a pill).</summary>
    private double Radius => Math.Clamp(cfg.IslandCorner, 12, 56);
    /// <summary>Resting size: Pequena / Média / Grande.</summary>
    private double SizeScale => cfg.IslandSize switch { "Pequena" => 0.88, "Grande" => 1.15, _ => 1 };

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    private readonly DocaConfig cfg;
    private readonly Window? bar;
    private readonly WindowTracker tracker;
    private IntPtr monitor;
    private readonly bool standalone;
    private readonly double inset;
    // A notch, not a floating pill: square top glued to the screen edge, flared "shoulders" into the edge and
    // squircle bottom corners. Drawn as one path in window coordinates; the content is clipped to the body.
    private readonly System.Windows.Shapes.Path pill = new() { Fill = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Grid host = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private IntPtr hwnd;
    private double scale = 1;

    // Spring state: current size, velocity, target.
    private double w, h, vw, vh, tw, th;
    private bool animating, hideAtEnd, growing;
    private TimeSpan lastFrame;

    private readonly RestView rest;
    private LevelView? level;
    private TrackView? track;
    private Dashboard? dashboard;
    private FrameworkElement? current;
    private IslandActivity? hud;
    private bool trackHud, hovering;
    private int menusOpen;
    private long menuClosedAt;
    private string? lastTrack;
    /// <summary>Pointer over the island (the resting pill grows a little: it is clickable).</summary>
    private bool pointerIn;
    private IDisposable? outsideClicks;
    private AppBar? appBar;
    private readonly DispatcherTimer hudTimer = new(), hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(280) },
        leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) }, clockTimer = new(),
        followTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Standalone: capped so a tall bar setting doesn't make a chunky pill (iPhone's is slim).
    private double Collapsed => standalone ? Math.Round(31 * SizeScale) : Math.Max(16, cfg.MenuBarHeight - 4);

    /// <summary>Height of the strip kept free at the top (standalone, IslandReserveTop).</summary>
    private double Strip => Collapsed + inset;

    /// <param name="bar">The menu bar this island sits on, or null for the standalone island.</param>
    public IslandWindow(DocaConfig cfg, IntPtr monitor, WindowTracker tracker, Window? bar = null)
    {
        this.cfg = cfg;
        this.bar = bar;
        this.tracker = tracker;
        this.monitor = monitor;
        standalone = bar == null;
        inset = 0; // glued to the top edge
        rest = new RestView(standalone, 14 * SizeScale);
        pill.Fill = Frozen(new SolidColorBrush(IslandBrushes.Parse(cfg.IslandColor, Colors.Black)));
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
            if (standalone && cfg.IslandReserveTop)
            {
                // Maximized windows start below the island: browser tabs in the middle stay clickable.
                appBar = new AppBar(this, Native.ABE_TOP);
                appBar.ExplorerRestarted += Taskbar.EnsureHidden;
            }
            Place();
        };

        hudTimer.Tick += (_, _) => { hudTimer.Stop(); hud = null; trackHud = false; Refresh(); };
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); Expand(); };
        leaveTimer.Tick += (_, _) =>
        {
            // Stay open while the pointer is back, a slider is being dragged, or a menu/panel opened from here is up.
            if (IsMouseOver || Mouse.Captured != null || menusOpen > 0 || Flyout.IsOpen || holds > 0) return;
            leaveTimer.Stop();
            Collapse();
        };
        MouseEnter += (_, _) =>
        {
            pointerIn = true;
            leaveTimer.Stop();
            if (hovering) return;
            if (cfg.IslandOpenOnHover && CanExpand) hoverTimer.Start();
            Refresh(); // grow a little: "this is clickable"
        };
        MouseLeave += (_, _) =>
        {
            pointerIn = false;
            hoverTimer.Stop();
            if (hovering) leaveTimer.Start();
            else Refresh();
        };
        // A click on the small pill opens it right away.
        MouseLeftButtonUp += (_, _) =>
        {
            if (hovering) return;
            // A notice with an action (a finished download: show it in the folder) does that instead.
            if (hud is StatusActivity { Click: { } act }) { act(); hudTimer.Stop(); hud = null; Refresh(); return; }
            if (CanExpand) { hoverTimer.Stop(); Expand(); }
        };
        if (standalone)
        {
            // Files dragged onto the island: the panel opens on the shelf and keeps them.
            AllowDrop = true;
            DragEnter += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
                e.Effects = DragDropEffects.Link;
                leaveTimer.Stop();
                Expand(Dashboard.ShelfTab);
            };
            DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
                e.Handled = true;
                leaveTimer.Stop();
            };
            DragLeave += (_, _) => { if (hovering) leaveTimer.Start(); };
            Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && holds == 0) Shelf.Add(files);
                e.Handled = true;
            };
        }
        // Closed pill: the wheel sets the volume (the level appears in the pill), the middle button plays/pauses.
        MouseWheel += (_, e) =>
        {
            if (hovering) return;
            e.Handled = true;
            Audio.Nudge(e.Delta > 0 ? 0.02f : -0.02f);
        };
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle && !hovering && NowPlaying.HasSession) NowPlaying.PlayPause(); };
        if (standalone)
        {
            // No bar, so no ⊞ menu: right-click the island for it.
            MouseRightButtonUp += (_, e) => { e.Handled = true; ShowSystemMenu(null); };
            clockTimer.Tick += (_, _) => UpdateClock();
            tracker.FullscreenChanged += UpdateVisibility;
            UpdateClock();
        }

        Island.Activity += OnActivity;
        NowPlaying.Changed += OnMedia;
        // Pill badges: camera/microphone dots, timer countdown, download size.
        PrivacyWatch.Changed += OnBadges;
        IslandTimer.Changed += OnBadges;
        DownloadWatch.Changed += OnBadges;
        Peripherals.Changed += OnBadges;
        Closed += (_, _) =>
        {
            Island.Activity -= OnActivity;
            NowPlaying.Changed -= OnMedia;
            PrivacyWatch.Changed -= OnBadges;
            IslandTimer.Changed -= OnBadges;
            DownloadWatch.Changed -= OnBadges;
            Peripherals.Changed -= OnBadges;
            if (standalone) tracker.FullscreenChanged -= UpdateVisibility;
            hudTimer.Stop();
            hoverTimer.Stop();
            leaveTimer.Stop();
            clockTimer.Stop();
            followTimer.Stop();
            if (animating) CompositionTarget.Rendering -= OnFrame;
            outsideClicks?.Dispose();
            appBar?.Release();
            host.Children.Clear(); // releases a panel's borrowed content
        };
        lastTrack = NowPlaying.HasSession ? NowPlaying.Title : null;
        Refresh();
    }

    private bool CanExpand => standalone || NowPlaying.HasSession;

    /// <summary>Something inside the panel needs it open (a file being dragged out of the shelf).</summary>
    private int holds;

    private void OnBadges()
    {
        // Only the resting pill shows them; it re-measures when they come and go.
        if (current == rest && !hovering && hud == null && !trackHud) Refresh();
    }

    public void ReleaseAppBar() => appBar?.Release();

    /// <summary>
    /// One island for all screens: it moves to the monitor under the mouse. A cursor check four times a second
    /// (two cheap calls) instead of a mouse hook; it waits while the island is in use.
    /// </summary>
    public void FollowCursor()
    {
        followTimer.Tick += (_, _) =>
        {
            if (!Native.GetCursorPos(out var pt)) return;
            var m = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (m == monitor || m == IntPtr.Zero || hovering || holds > 0 || menusOpen > 0 || Flyout.IsOpen) return;
            monitor = m;
            // Gone from the old screen at once, then drops in on the new one.
            hud = null;
            trackHud = false;
            current = null;
            host.Children.Clear();
            if (animating) { CompositionTarget.Rendering -= OnFrame; animating = false; }
            pill.Visibility = Visibility.Collapsed;
            w = h = vw = vh = 0;
            SetRegion(0, 0);
            Place();
            if (tracker.IsFullscreen(monitor)) { Hide(); return; }
            if (!IsVisible) Show();
            Refresh();
        };
        followTimer.Start();
    }

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
        appBar?.Reserve(m.Bounds, (int)Math.Round(Strip * scale));
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
        // The pill only has room for the time; the full date is a hover away.
        string date = now.ToString("D", CultureInfo.CurrentCulture);
        rest.ToolTip = char.ToUpper(date[0]) + date[1..];
        clockTimer.Interval = cfg.ClockShowSeconds
            ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 10)
            : TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 30);
        clockTimer.Start();
    }

    /// <param name="button">The panel's ⋯ button (menu hangs under it, right edges aligned), or null for a
    /// right-click on the island (menu at the pointer, like any context menu).</param>
    private void ShowSystemMenu(FrameworkElement? button = null)
    {
        // A click on ⋯ with the menu open first closes the menu (outside click), then reaches the button:
        // the button toggles, so that same click must not open it again.
        if (Environment.TickCount64 - menuClosedAt < 350) return;
        var m = MenuBarWindow.SystemMenu(cfg);
        if (button != null)
        {
            m.PlacementTarget = button;
            m.Placement = PlacementMode.Custom;
            m.CustomPopupPlacementCallback = (popup, target, _) =>
                new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Horizontal) };
        }
        else m.Placement = PlacementMode.MousePoint;
        m.Opened += (_, _) => menusOpen++;
        m.Closed += (_, _) => { menusOpen = Math.Max(0, menusOpen - 1); menuClosedAt = Environment.TickCount64; };
        Ui.CloseOnOutsideClick(m);
        m.IsOpen = true;
    }

    #region State

    /// <param name="tab">Panel tab to show (files dragged in: the shelf).</param>
    private void Expand(int? tab = null)
    {
        if (tab is int t) { if (dashboard != null) dashboard.SelectTab(t); else Dashboard.Preselect = t; }
        // Open until the pointer leaves or a click lands outside Doca (also when opened by a click or --island,
        // where the pointer may never come in).
        outsideClicks ??= Ui.OnOutsideClick(() => { leaveTimer.Stop(); Collapse(); });
        hovering = true;
        hud = null;
        trackHud = false;
        hudTimer.Stop();
        Refresh();
    }

    private void Collapse()
    {
        outsideClicks?.Dispose();
        outsideClicks = null;
        if (!hovering) return;
        hovering = false;
        Refresh();
    }

    private void OnActivity(IslandActivity a)
    {
        if (hovering) return; // the panel is open: don't cover it
        hud = a;
        trackHud = false;
        hudTimer.Interval = TimeSpan.FromSeconds(a is StatusActivity s ? s.Seconds : 1.6);
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
        // Hand cursor on the closed pill: it opens on click.
        Cursor = !hovering && (CanExpand || hud is StatusActivity { Click: not null }) ? Cursors.Hand : null;

        if (hovering && standalone)
        {
            dashboard ??= new Dashboard(cfg, host, () => { leaveTimer.Stop(); Collapse(); }, ShowSystemMenu,
                hold: () => holds++,
                release: () => { holds = Math.Max(0, holds - 1); if (!IsMouseOver && hovering) leaveTimer.Start(); });
            Show(dashboard, 440, null);
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
        else if (hud is StatusActivity sa) Show(new StatusView(sa), 320, 34);
        else if (trackHud && hasMusic)
        {
            track ??= new TrackView();
            track.Update();
            Show(track, 310, 52);
        }
        else if (standalone || hasMusic)
        {
            rest.Update(cfg.ShowNowPlaying);
            // As wide as what it shows (see RestView), never narrower than clearly wider-than-tall.
            double width = rest.DesiredWidth;
            // Pointer on it (and it opens on click): a small stretch, like a button lifting.
            if (pointerIn && CanExpand) width += 14;
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
            // Shrink back up into the top edge, then vanish.
            hideAtEnd = true;
            AnimateTo(Collapsed * 2, 0);
            return;
        }
        hideAtEnd = false;
        view.Height = height ?? double.NaN;
        if (view != current)
        {
            host.Children.Clear();
            host.Children.Add(view);
            current = view;
            // New content comes into focus while the notch reshapes around it.
            FadeIn(view, 60);
        }
        host.Width = width;
        if (pill.Visibility != Visibility.Visible)
        {
            // Drop down out of the top edge.
            pill.Visibility = Visibility.Visible;
            w = Collapsed * 2; h = 0; vw = vh = 0;
        }
        AnimateTo(width, height ?? Measure(view, width));
    }

    /// <summary>Content appears out of focus and sharpens (alpha 0→1, blur 16→0, 250 ms ease-out).</summary>
    internal static void FadeIn(UIElement view, double delayMs = 0)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var span = TimeSpan.FromMilliseconds(250);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var blur = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian };
        view.Effect = blur;
        view.Opacity = 0;
        var fade = new DoubleAnimation(1, span) { BeginTime = begin, EasingFunction = ease };
        var sharpen = new DoubleAnimation(0, span) { BeginTime = begin, EasingFunction = ease };
        // An effect makes WPF render the element through an offscreen surface: drop it once sharp.
        sharpen.Completed += (_, _) => { if (view.Effect == blur) view.Effect = null; };
        view.BeginAnimation(OpacityProperty, fade);
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
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
        // Opening is springy, closing settles quickly (bigger target = opening).
        growing = tw * th >= w * h;
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
        Step(ref w, ref vw, tw, dt, growing);
        Step(ref h, ref vh, th, dt, growing);
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

    /// <summary>
    /// Damped spring. Opening: 2.5 Hz, damping 0.6 (a visible bounce); closing: 3 Hz, damping 0.9 (settles
    /// without wobbling), the feel of iOS's Dynamic Island.
    /// </summary>
    private static void Step(ref double x, ref double v, double target, double dt, bool open)
    {
        double f = open ? 2.5 : 3, z = open ? 0.6 : 0.9, wn = 2 * Math.PI * f;
        double k = wn * wn, c = 2 * z * wn;
        for (int i = 0; i < 4; i++)
        {
            double d = dt / 4;
            v += (k * (target - x) - c * v) * d;
            x += v * d;
        }
    }

    /// <summary>Shoulder width: the concave curve that blends the notch into the top edge.</summary>
    private static double Shoulder(double r) => Math.Clamp(r / 4, 5, 11);

    private void Apply()
    {
        double pw = Math.Max(0, w), ph = Math.Max(0, h);
        double r = Math.Min(Radius, Math.Min(pw, ph) / 2);
        double cx = MaxW / 2;
        pill.Data = Notch(cx - pw / 2, cx + pw / 2, ph, r, Shoulder(r));
        // The content keeps its final layout; the clip reveals it as the notch grows.
        double hw = double.IsNaN(host.Width) ? pw : host.Width;
        host.Clip = Notch((hw - pw) / 2, (hw + pw) / 2, ph, r, 0);
    }

    /// <summary>
    /// Square top at y=0, flared shoulders of width s, and squircle bottom corners (superellipse, exponent 2.8,
    /// a bit squarer than a circle, like Apple's "continuous" corners).
    /// </summary>
    private static Geometry Notch(double l, double r, double b, double R, double s)
    {
        var g = new StreamGeometry();
        if (r - l > 0 && b > 0)
        {
            using var c = g.Open();
            double fillet = Math.Min(s, b / 2);
            c.BeginFigure(new Point(l - s, 0), true, true);
            c.LineTo(new Point(r + s, 0), false, false);
            if (s > 0) c.BezierTo(new Point(r + s / 8, 0), new Point(r, fillet / 2), new Point(r, fillet), false, true);
            Corner(c, r - R, b - R, R, 0);
            Corner(c, l + R, b - R, R, Math.PI / 2);
            c.LineTo(new Point(l, fillet), false, true);
            if (s > 0) c.BezierTo(new Point(l, fillet / 2), new Point(l - s / 8, 0), new Point(l - s, 0), false, true);
        }
        g.Freeze();
        return g;
    }

    private static void Corner(StreamGeometryContext c, double cx, double cy, double R, double a0)
    {
        const int n = 12;
        const double p = 2 / 2.8;
        var pts = new Point[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = a0 + Math.PI / 2 * i / n, cos = Math.Cos(a), sin = Math.Sin(a);
            pts[i] = new Point(cx + Math.Sign(cos) * Math.Pow(Math.Abs(cos), p) * R, cy + Math.Sign(sin) * Math.Pow(Math.Abs(sin), p) * R);
        }
        c.PolyLineTo(pts, true, true);
    }

    private void SetRegion(double width, double height)
    {
        if (hwnd == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) { SetWindowRgn(hwnd, CreateRectRgn(0, 0, 0, 0), true); return; }
        int full = (int)Math.Round(MaxW * scale);
        // Shoulders included.
        double s = Shoulder(Math.Min(Radius, Math.Min(width, height) / 2));
        int pw = (int)Math.Ceiling((width + 2 * s) * scale), ph = (int)Math.Ceiling(height * scale), top = (int)Math.Round(inset * scale);
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

    /// <summary>
    /// Resting pill: the clock in the middle with what is going on next to it (camera/microphone dots, the paused
    /// song's cover, timer, download). While music plays, a round cover on the left and a waveform in the album's
    /// color on the right frame it. The pill is as wide as that.
    /// </summary>
    private sealed class RestView : Grid
    {
        private readonly bool withClock;
        private readonly double scale;
        private readonly Grid playing = new();
        private readonly Border art = Art(22, 11);
        private readonly IslandEq eq = new();
        private readonly StackPanel center = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border pausedArt = Art(16, 8);
        private readonly TextBlock clock = new()
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly System.Windows.Shapes.Ellipse camDot = Dot(IslandBrushes.Green), micDot = Dot(IslandBrushes.Orange);
        private readonly TextBlock timerText = BadgeText(), downloadText = BadgeText();
        private readonly StackPanel timerBadge, downloadBadge, batteryBadge;
        private readonly TextBlock batteryText = BadgeText(), batteryGlyph = Glyph("", 11);
        private bool? wasPlaying;

        public RestView(bool withClock, double clockSize)
        {
            this.withClock = withClock;
            scale = clockSize / 14;
            clock.FontSize = clockSize;
            art.HorizontalAlignment = HorizontalAlignment.Left;
            art.Margin = new Thickness(Math.Round(10 * scale), 0, 0, 0);
            eq.HorizontalAlignment = HorizontalAlignment.Right;
            eq.Margin = new Thickness(0, 0, Math.Round(14 * scale), 0);
            playing.Children.Add(art);
            playing.Children.Add(eq);

            pausedArt.Opacity = 0.55;
            pausedArt.Margin = new Thickness(0, 0, 8, 0);
            pausedArt.ToolTip = "Pausado";
            timerBadge = Badge(ExtraGlyphs.Timer, timerText);
            // Low battery of the mouse/headset (≤ 20 %), in orange.
            batteryBadge = new StackPanel { Orientation = Orientation.Horizontal };
            batteryGlyph.Foreground = batteryText.Foreground = IslandBrushes.Orange;
            batteryBadge.Children.Add(batteryGlyph);
            batteryBadge.Children.Add(batteryText);
            downloadBadge = Badge(ExtraGlyphs.Download, downloadText);
            center.Children.Add(camDot);
            center.Children.Add(micDot);
            center.Children.Add(pausedArt);
            center.Children.Add(clock);
            center.Children.Add(timerBadge);
            center.Children.Add(downloadBadge);
            center.Children.Add(batteryBadge);
            Children.Add(playing);
            Children.Add(center);
        }

        private static System.Windows.Shapes.Ellipse Dot(Brush b) => new() { Width = 7, Height = 7, Fill = b, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        private static TextBlock BadgeText() => new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };

        private static StackPanel Badge(string glyph, TextBlock text)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            var g = Glyph(glyph, 11);
            g.Opacity = 0.8;
            s.Children.Add(g);
            s.Children.Add(text);
            return s;
        }

        public string Clock { set => clock.Text = value; }

        /// <summary>Width the pill wants for what it shows now (set by Update).</summary>
        public double DesiredWidth { get; private set; }

        public void Update(bool showMusic)
        {
            bool music = showMusic && NowPlaying.HasSession, play = music && NowPlaying.Playing;
            playing.Visibility = play ? Visibility.Visible : Visibility.Collapsed;
            if (play)
            {
                SetArt(art, null);
                eq.Color = ArtColor.Of(NowPlaying.Art);
            }
            eq.Playing = play;
            // Paused: the cover stays as a small, dim reminder next to the clock (a click opens the player).
            pausedArt.Visibility = music && !play ? Visibility.Visible : Visibility.Collapsed;
            if (music && !play) SetArt(pausedArt, null);
            // Always the time in the middle: unlike the iPhone there is no camera there, it would be dead space.
            clock.Visibility = withClock ? Visibility.Visible : Visibility.Collapsed;

            bool privacy = Island.Config?.IslandPrivacy != false;
            camDot.Visibility = privacy && PrivacyWatch.CamApp != null ? Visibility.Visible : Visibility.Collapsed;
            micDot.Visibility = privacy && PrivacyWatch.MicApp != null ? Visibility.Visible : Visibility.Collapsed;
            camDot.ToolTip = PrivacyWatch.CamApp is { } c ? $"{c} está usando a câmera" : null;
            micDot.ToolTip = PrivacyWatch.MicApp is { } m ? $"{m} está usando o microfone" : null;
            timerBadge.Visibility = IslandTimer.Active ? Visibility.Visible : Visibility.Collapsed;
            timerText.Text = IslandTimer.Format(IslandTimer.Left);
            timerBadge.Opacity = IslandTimer.Paused ? 0.5 : 1;
            bool dl = Island.Config?.IslandDownloads != false && DownloadWatch.Count > 0;
            downloadBadge.Visibility = dl ? Visibility.Visible : Visibility.Collapsed;
            downloadText.Text = !dl ? "" : DownloadWatch.Speed > 0 ? DownloadWatch.Rate(DownloadWatch.Speed) : DownloadWatch.Size(DownloadWatch.Bytes);
            downloadBadge.ToolTip = dl ? $"{DownloadWatch.Size(DownloadWatch.Bytes)} baixados" : null;
            var low = Island.Config?.IslandPeripherals != false ? Peripherals.Low() : null;
            batteryBadge.Visibility = low != null ? Visibility.Visible : Visibility.Collapsed;
            if (low != null)
            {
                batteryGlyph.Text = low.Glyph;
                batteryText.Text = $"{low.Level}%";
                batteryBadge.ToolTip = $"{low.Name}: bateria fraca ({low.Level}%)";
            }

            // The first visible item in the middle has no left gap.
            bool first = true;
            foreach (FrameworkElement e in center.Children)
            {
                if (e.Visibility != Visibility.Visible) continue;
                if (e is StackPanel badge) badge.Margin = new Thickness(first ? 0 : 10, 0, 0, 0);
                first = false;
            }
            center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Rounded up in steps so the pill doesn't twitch as digits change width.
            double mid = Math.Ceiling(center.DesiredSize.Width / 8) * 8;
            DesiredWidth = play
                ? Math.Max(Math.Round(190 * scale), mid + 2 * Math.Round(52 * scale)) // cover and waveform on the sides
                : Math.Max(Math.Round(112 * scale), mid + Math.Round(52 * scale));

            // Switching between the two looks: the new one comes into focus.
            if (wasPlaying != null && wasPlaying != play && IsLoaded) IslandWindow.FadeIn(play ? playing : center);
            wasPlaying = play;
        }
    }

    /// <summary>
    /// iOS-style waveform: four rounded bars swinging around the middle, each with its own rhythm, in the album's
    /// color. Animated (30 fps) only while playing.
    /// </summary>
    private sealed class IslandEq : StackPanel
    {
        private static readonly double[][] steps =
        {
            new[] { 0.35, 0.9, 0.5, 1.0, 0.4 }, new[] { 0.7, 0.3, 1.0, 0.45, 0.8 },
            new[] { 0.45, 1.0, 0.35, 0.75, 0.55 }, new[] { 0.9, 0.4, 0.7, 0.3, 1.0 },
        };
        private static readonly double[] beats = { 0.95, 1.15, 1.05, 1.3 };
        private readonly ScaleTransform[] bars = new ScaleTransform[4];
        private readonly SolidColorBrush fill = new(Colors.White);
        private bool playing;

        public IslandEq()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Height = 16;
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new ScaleTransform(1, 0.4);
                Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = fill, Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = bars[i],
                });
            }
        }

        public Color Color
        {
            set
            {
                if (fill.Color == value) return;
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(value, TimeSpan.FromMilliseconds(400)));
            }
        }

        public bool Playing
        {
            set
            {
                if (playing == value) return;
                playing = value;
                for (int i = 0; i < bars.Length; i++)
                {
                    if (!value) { bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null); bars[i].ScaleY = 0.4; continue; }
                    var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                    var seq = steps[i];
                    double step = beats[i] / seq.Length;
                    for (int k = 0; k <= seq.Length; k++)
                        anim.KeyFrames.Add(new EasingDoubleKeyFrame(seq[k % seq.Length], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(step * k)),
                            new SineEase { EasingMode = EasingMode.EaseInOut }));
                    Timeline.SetDesiredFrameRate(anim, 30);
                    bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            }
        }
    }

    /// <summary>
    /// A vivid color from the cover (saturated pixels weigh more), brightened to read on black. Cached per image.
    /// </summary>
    private static class ArtColor
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, object> cache = new();

        public static Color Of(ImageSource? img)
        {
            if (img is not System.Windows.Media.Imaging.BitmapSource bmp) return Colors.White;
            if (cache.TryGetValue(img, out var c)) return (Color)c;
            var color = Compute(bmp);
            cache.AddOrUpdate(img, color);
            return color;
        }

        private static Color Compute(System.Windows.Media.Imaging.BitmapSource bmp)
        {
            try
            {
                double k = 24.0 / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
                var small = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(k, k)), PixelFormats.Bgra32, null, 0);
                int w = small.PixelWidth, h = small.PixelHeight;
                var px = new byte[w * h * 4];
                small.CopyPixels(px, w * 4, 0);
                // Hue histogram of the vivid pixels (12 bins): the most present vivid hue wins, even when most of
                // the cover is white or dark. An average would mix everything into gray.
                var bins = new (double r, double g, double b, double weight)[12];
                int vivid = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double B = px[i] / 255.0, G = px[i + 1] / 255.0, R = px[i + 2] / 255.0;
                    double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
                    double sat = max == 0 ? 0 : d / max;
                    if (sat < 0.35 || max < 0.25) continue;
                    vivid++;
                    double hue = d == 0 ? 0 : max == R ? (G - B) / d % 6 : max == G ? (B - R) / d + 2 : (R - G) / d + 4;
                    int bin = ((int)Math.Floor((hue < 0 ? hue + 6 : hue) * 2) % 12 + 12) % 12;
                    double wgt = sat * max;
                    bins[bin] = (bins[bin].r + R * wgt, bins[bin].g + G * wgt, bins[bin].b + B * wgt, bins[bin].weight + wgt);
                }
                // Almost no color in it (black-and-white cover): plain white bars.
                if (vivid < px.Length / 4 * 0.04) return Colors.White;
                var best = bins.MaxBy(x => x.weight);
                double r = best.r / best.weight, g = best.g / best.weight, b = best.b / best.weight;
                double top = Math.Max(r, Math.Max(g, b));
                if (top <= 0) return Colors.White;
                // Full brightness, then a touch of white so dark hues still read on the black pill.
                r /= top; g /= top; b /= top;
                const double lift = 0.25;
                return Color.FromRgb((byte)(255 * (r + (1 - r) * lift)), (byte)(255 * (g + (1 - g) * lift)), (byte)(255 * (b + (1 - b) * lift)));
            }
            catch (Exception ex) { App.Log(ex); return Colors.White; }
        }
    }

    /// <summary>Volume/brightness: glyph, level bar, percent. Updated in place while keys repeat.</summary>
    private sealed class LevelView : Grid
    {
        private readonly TextBlock glyph = Glyph("", 14);
        private readonly Border fill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), Background = IslandBrushes.Accent, HorizontalAlignment = HorizontalAlignment.Left };
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
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = Glyph(a.Glyph, a.SymbolFont ? 15 : 14, a.SymbolFont);
            glyph.Margin = new Thickness(14, 0, 0, 0);
            var name = new TextBlock { Text = a.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var state = new TextBlock
            {
                Text = a.State, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 18, 0),
                Foreground = a.StateBrush ?? (a.On ? IslandBrushes.Green : IslandBrushes.Dim),
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
    /// Standalone island's panel: tabs Música · Calendário · Controles · Bandeja · Arquivos · Copiados · Timer
    /// (click or mouse wheel) and the system menu. Calendar and Control Center are the menu bar panels' own content, borrowed with
    /// Flyout.Detach and given back (window closed) when the tab changes or the island collapses.
    /// </summary>
    private sealed class Dashboard : StackPanel
    {
        private static int lastTab = -1;
        private static readonly (string glyph, string name)[] tabs =
        {
            ("\uE80F", "Início"), (IslandGlyphs.Music, "Música"), (IslandGlyphs.Calendar, "Calendário"), (IslandGlyphs.Controls, "Controles"),
            (IslandGlyphs.Tray, "Bandeja"), (ExtraGlyphs.Folder, "Arquivos"), (ExtraGlyphs.Clipboard, "Copiados"), (ExtraGlyphs.Timer, "Timer"),
        };
        public const int HomeTab = 0, MusicTab = 1, ShelfTab = 5;
        /// <summary>Every tab's name, in the default order (Preferências lists these).</summary>
        public static IEnumerable<string> TabNames => tabs.Select(t => t.name);
        /// <summary>Visible tabs (ids into tabs), in the order chosen in Preferências.</summary>
        private readonly int[] order;
        /// <summary>Tab the next panel opens on (files dragged in), instead of the last one used.</summary>
        public static int? Preselect;
        private readonly Brush chipOn;

        private readonly DocaConfig cfg;
        private readonly FrameworkElement anchor;
        private readonly Action dismiss, hold, release;
        private readonly Border?[] chips = new Border?[tabs.Length];
        private readonly Border body = new();
        private Flyout? lent;
        private int index = -1;

        public Dashboard(DocaConfig cfg, FrameworkElement anchor, Action dismiss, Action<FrameworkElement> systemMenu, Action hold, Action release)
        {
            this.cfg = cfg;
            this.anchor = anchor;
            this.dismiss = dismiss;
            this.hold = hold;
            this.release = release;
            // Room for the big squircle corners at the bottom.
            Margin = new Thickness(18, 10, 18, 20);
            // Fluent dark styles for the borrowed panels' sliders and buttons, scoped to this panel (a window-wide
            // ThemeMode would also give the island window a backdrop).
            try { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml") }); }
            catch (Exception ex) { App.Log(ex); }
            // The island's accent (white by default) instead of the Windows accent blue on sliders, toggles and buttons.
            var ac = IslandBrushes.Parse(cfg.IslandAccent, Colors.White);
            var accent = Frozen(new SolidColorBrush(ac));
            var accentDim = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, ac.R, ac.G, ac.B)));
            var accentLow = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, ac.R, ac.G, ac.B)));
            Resources["AccentFillColorDefaultBrush"] = accent;
            Resources["AccentFillColorSecondaryBrush"] = accentDim;
            Resources["AccentFillColorTertiaryBrush"] = accentLow;
            Resources["TextOnAccentFillColorPrimaryBrush"] = IslandBrushes.On(ac);
            // The Fluent slider paints its value and thumb with these (aliases of the accent resolved at load).
            Resources["SliderThumbBackground"] = accent;
            Resources["SliderThumbBackgroundPointerOver"] = accentDim;
            Resources["SliderThumbBackgroundPressed"] = accentLow;
            chipOn = Frozen(new SolidColorBrush(ac == Colors.White ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x55, ac.R, ac.G, ac.B)));

            order = cfg.IslandTabs.Select(name => Array.FindIndex(tabs, t => t.name == name)).Where(i => i >= 0).Distinct().ToArray();
            if (order.Length == 0) order = Enumerable.Range(0, tabs.Length).ToArray();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (int i in order)
            {
                int n = i;
                // Icon-only tabs (name in the tooltip): a quiet toolbar instead of a row of labels.
                var chip = new Border
                {
                    Child = Glyph(tabs[i].glyph, 15), Width = 36, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 2, 0),
                    Cursor = Cursors.Hand, ToolTip = tabs[i].name,
                };
                BarFx.Prepare(chip);
                chip.MouseEnter += (_, _) => { if (index != n) BarFx.Hot(chip, true); };
                chip.MouseLeave += (_, _) => { if (index != n) BarFx.Hot(chip, false); };
                chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(n); };
                chips[i] = chip;
                row.Children.Add(chip);
            }
            header.Children.Add(row);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            // Spotlight lost its bar button in this mode; it lives here (and on Alt+Espaço).
            buttons.Children.Add(RoundButton(IslandGlyphs.Search, "Spotlight (Alt+Espaço)", _ => { dismiss(); SpotlightWindow.Toggle(cfg); }));
            buttons.Children.Add(RoundButton(IslandGlyphs.More, "Menu do sistema", systemMenu));
            header.Children.Add(buttons);
            Children.Add(header);
            Children.Add(body);

            MouseWheel += (_, e) =>
            {
                // Sliders use the wheel themselves.
                if (e.OriginalSource is DependencyObject d && FindSlider(d)) return;
                e.Handled = true;
                int pos = Array.IndexOf(order, index);
                Select(order[pos < 0 ? 0 : (pos + (e.Delta < 0 ? 1 : order.Length - 1)) % order.Length]);
            };
            Unloaded += (_, _) => Release();
            // Last tab used, except Música with nothing playing: the home widgets say more than "Nada tocando".
            Loaded += (_, _) =>
            {
                if (index >= 0) return;
                if (Preselect is int p) { Preselect = null; Select(p); return; }
                bool lastOk = order.Contains(lastTab) && !(lastTab == MusicTab && !NowPlaying.HasSession);
                Select(lastOk ? lastTab : order.Contains(HomeTab) ? HomeTab : order[0]);
            };
        }

        public void SelectTab(int n)
        {
            if (IsLoaded) Select(n); else Preselect = n;
        }

        private static Border RoundButton(string glyph, string tip, Action<FrameworkElement> click)
        {
            var b = new Border { Child = Glyph(glyph, 14), Width = 32, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip };
            BarFx.Prepare(b);
            b.MouseEnter += (_, _) => BarFx.Hot(b, true);
            b.MouseLeave += (_, _) => BarFx.Hot(b, false);
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(b); };
            return b;
        }

        /// <summary>Next tab; false when already on the last one.</summary>
        public bool Next()
        {
            int pos = Array.IndexOf(order, index);
            if (pos >= order.Length - 1) return false;
            Select(order[pos + 1]);
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
            // A hidden tab can still open (files dragged in, a widget's title): no chip lights up then.
            if (index >= 0 && chips[index] is { } old) { old.Background = null; BarFx.Prepare(old); }
            index = lastTab = n;
            if (chips[n] is { } chip) chip.Background = chipOn;
            Release();

            FrameworkElement content;
            switch (n)
            {
                case 0: content = new HomeView(cfg, Select, dismiss); break;
                case 1:
                    content = NowPlaying.HasSession
                        ? new PlayerView(compact: false) { Margin = new Thickness(4, 2, 4, 0) }
                        : Empty("Nada tocando agora.");
                    break;
                case 2: content = Borrow(new CalendarFlyout(cfg)); break;
                case 3: content = Borrow(new ControlCenterFlyout(cfg, embedded: true)); break;
                case 5: content = new ShelfView(hold, release); break;
                case 6:
                    content = cfg.IslandClipboard
                        ? new ClipboardView(dismiss, hold, release)
                        : Empty("O histórico está desligado (Preferências → Topo da tela).");
                    break;
                case 7: content = new TimerView(); break;
                default: content = TrayGrid(); break;
            }
            body.Child = content;
            FadeIn(content);
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
                    // A left click opens the app: get out of its way. A right click shows the app's menu: the
                    // panel closes when that menu is used (outside click).
                    if (e.ChangedButton == MouseButton.Left) Dispatcher.BeginInvoke(dismiss, DispatcherPriority.Background);
                };
                grid.Children.Add(b);
            }
            return grid;
        }
    }

    #endregion
}
