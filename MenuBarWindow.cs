using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Doca;

internal static class Ui
{
    public static MenuItem Item(string header, Action action, bool? isChecked = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (isChecked != null) mi.IsChecked = isChecked.Value;
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Gray section title inside a menu (Jump List categories).</summary>
    public static MenuItem Header(string text) =>
        new() { Header = text, IsEnabled = false, Focusable = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold };

    public static string Shorten(string s, int max = 60) => s.Length > max ? s[..(max - 3)] + "…" : s;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr msg, IntPtr info);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr info);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;

    /// <summary>
    /// Closes the menu on a click anywhere outside Doca. The bar and Dock never take focus
    /// (WS_EX_NOACTIVATE), and without focus WPF's "click outside" detection (mouse capture) only
    /// sees clicks on Doca's own windows: clicking another app or the desktop left the menu open.
    /// A low-level mouse hook, installed only while the menu is open, sees every click.
    /// </summary>
    public static void CloseOnOutsideClick(ContextMenu m)
    {
        IntPtr hook = IntPtr.Zero;
        LowLevelMouseProc? proc = null; // must stay referenced while installed, or the GC frees the thunk
        uint ownPid = (uint)Environment.ProcessId;
        m.Opened += (_, _) =>
        {
            if (hook != IntPtr.Zero) return;
            proc = (code, msg, info) =>
            {
                int wm = msg.ToInt32();
                if (code >= 0 && (wm == WM_LBUTTONDOWN || wm == WM_RBUTTONDOWN || wm == WM_MBUTTONDOWN))
                {
                    // MSLLHOOKSTRUCT starts with the cursor position.
                    Native.GetWindowThreadProcessId(WindowFromPoint(Marshal.PtrToStructure<Native.POINT>(info)), out uint pid);
                    // Clicks on Doca (the menu itself, the bar) are left to WPF as before.
                    if (pid != ownPid) m.Dispatcher.BeginInvoke(() => m.IsOpen = false);
                }
                return CallNextHookEx(IntPtr.Zero, code, msg, info);
            };
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        };
        m.Closed += (_, _) =>
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            proc = null;
        };
    }
}

/// <summary>
/// macOS-style menu bar. Registered as an AppBar so maximized windows never go under it,
/// event driven (foreground hook, network events) and hidden automatically for fullscreen apps.
/// </summary>
internal sealed class MenuBarWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    /// <summary>Gap around the bar when it floats (DIP).</summary>
    private const double FloatGap = 6;

    private readonly DocaConfig cfg;
    private readonly WindowTracker tracker;
    // Display optical size: the bold app name reads closer to SF Pro's than the Text cut does.
    private readonly TextBlock appName = new() { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock clock = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel appMenus = new() { Orientation = Orientation.Horizontal };
    private int appMenusVersion;
    private IntPtr menusFor;
    private readonly TextBlock wifi = Glyph(""), volume = Glyph(""), battery = Glyph("");
    private readonly TextBlock batteryPct = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), FontSize = 12 };
    private readonly Border batteryItem, volumeItem, wifiItem, bluetoothItem;
    private readonly TextBlock bluetooth = Glyph("");
    private readonly TextBlock nowPlayingGlyph = new() { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Album art, 16 px with rounded corners; the music glyph stands in while there is none.</summary>
    private readonly Border nowPlayingArt = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(3.5), VerticalAlignment = VerticalAlignment.Center };
    private readonly Equalizer equalizer = new() { Margin = new Thickness(7, 0, 0, 0) };
    private readonly TextBlock nowPlayingTitle = new() { MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border nowPlayingItem;
    private readonly DispatcherTimer clockTimer = new(), statusTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly IntPtr monitor;
    private AppBar? appBar;
    private IslandWindow? island;
    /// <summary>Width kept free in the middle of the bar for the island's resting pill (DIP).</summary>
    private const double IslandReserve = 200;
    private int statusTicks;
    private bool closed;
    private int menusOpen;
    /// <summary>Shown over a fullscreen app because the pointer rests on the top edge.</summary>
    private bool revealed;
    private int revealTicks;
    private readonly DispatcherTimer revealTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static TextBlock Glyph(string g) => new()
    {
        Text = g, FontFamily = new FontFamily(IconFont), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
    };

    public MenuBarWindow(DocaConfig cfg, WindowTracker tracker, IntPtr monitor)
    {
        this.cfg = cfg;
        this.tracker = tracker;
        this.monitor = monitor;
        Title = "Doca Menu Bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000;
        Width = Height = 10;
        FontFamily = new FontFamily(cfg.Font);
        FontSize = 13;
        Foreground = Brushes.White;
        // Ideal (unhinted) keeps the font's real shapes, closer to the macOS menu bar; Display looked thin and boxy.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;

        var logo = new Path
        {
            Data = Geometry.Parse("M0,0 H6.6 V6.6 H0 Z M7.4,0 H14 V6.6 H7.4 Z M0,7.4 H6.6 V14 H0 Z M7.4,7.4 H14 V14 H7.4 Z"),
            Fill = Brushes.White, Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0) };
        left.Children.Add(Item(logo, ShowSystemMenu, "Menu do sistema", pad: 10));
        left.Children.Add(Item(appName, ShowAppMenu, null));
        left.Children.Add(appMenus);

        var batteryPanel = new StackPanel { Orientation = Orientation.Horizontal };
        batteryPanel.Children.Add(batteryPct);
        batteryPanel.Children.Add(battery);
        batteryPct.Margin = new Thickness(0, 0, 5, 0);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        batteryItem = Item(batteryPanel, _ => Shell.WinKey(0x41), null);
        wifiItem = Item(wifi, b => Flyout.Toggle("wifi", b, () => new WifiFlyout(cfg)), null, pad: 7);
        bluetoothItem = Item(bluetooth, b => Flyout.Toggle("bluetooth", b, () => new BluetoothFlyout(cfg)), null, pad: 7);
        bluetoothItem.Visibility = Visibility.Collapsed; // until we know there is an adapter
        volumeItem = Item(volume, b => Flyout.Toggle("sound", b, () => new SoundFlyout(cfg)), null, pad: 7);
        volumeItem.MouseWheel += (_, e) => { Audio.Nudge(e.Delta > 0 ? 0.02f : -0.02f); UpdateVolume(); };
        // Right-click opens the panel like everywhere else in the bar; the middle button mutes.
        volumeItem.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { Audio.ToggleMute(); UpdateVolume(); } };
        TrayIconsPanel? tray = cfg.ShowTrayIcons ? new TrayIconsPanel(cfg) : null;
        if (tray != null) right.Children.Add(tray);
        var nowPlayingPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var artSlot = new Grid { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        artSlot.Children.Add(nowPlayingGlyph);
        artSlot.Children.Add(nowPlayingArt);
        nowPlayingPanel.Children.Add(artSlot);
        nowPlayingPanel.Children.Add(nowPlayingTitle);
        nowPlayingPanel.Children.Add(equalizer);
        nowPlayingItem = Item(nowPlayingPanel, b => Flyout.Toggle("media", b, () => new MediaFlyout(cfg)), null);
        right.Children.Add(nowPlayingItem);
        right.Children.Add(Item(Glyph(""), _ => SpotlightWindow.Toggle(cfg), "Spotlight (Alt+Espaço)", pad: 7));
        right.Children.Add(batteryItem);
        right.Children.Add(bluetoothItem);
        right.Children.Add(wifiItem);
        right.Children.Add(volumeItem);
        right.Children.Add(Item(Glyph(""), b => Flyout.Toggle("control", b, () => new ControlCenterFlyout(cfg)), "Central de Controle", pad: 7));
        right.Children.Add(Item(clock, b => Flyout.Toggle("calendar", b, () => new CalendarFlyout(cfg)), null, pad: 10));

        // Transparente has no background at all, like the iPhone's status bar around the island: no strip,
        // just text and icons on the wallpaper (the shadow below keeps them readable).
        var root = new Grid();
        BarFx.Subtle = cfg.MenuBarStyle == "Transparente";
        if (cfg.MenuBarStyle != "Sólido")
        {
            // Light/no tint lets the wallpaper through, so white text needs a soft shadow to stay legible.
            var shadow = Frozen(new DropShadowEffect { ShadowDepth = 0.5, Direction = 270, BlurRadius = 5, Opacity = 0.45, Color = Colors.Black });
            left.Effect = shadow;
            right.Effect = shadow;
        }
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        // Tray icons get whatever width the app menus (left) and status items (right) leave free;
        // the rest goes behind ⌄ instead of disappearing under the menus.
        void FitTray()
        {
            if (tray == null) return;
            double fixedRight = right.ActualWidth - tray.ActualWidth;
            double free = ActualWidth - left.ActualWidth - left.Margin.Left - fixedRight - right.Margin.Right - 24;
            // With the island, tray icons stop short of the middle of the bar.
            if (cfg.DynamicIsland) free = Math.Min(free, ActualWidth / 2 - IslandReserve / 2 - fixedRight - right.Margin.Right - 8);
            tray.Available = Math.Max(0, free);
        }
        left.SizeChanged += (_, _) => FitTray();
        right.SizeChanged += (_, _) => FitTray();
        SizeChanged += (_, _) => FitTray();

        SourceInitialized += (_, _) =>
        {
            Glass.MakeOverlay(this, rounded: cfg.MenuBarFloating);
            switch (cfg.MenuBarStyle)
            {
                case "Transparente": break; // the DWM surface stays fully transparent
                case "Sólido":
                    uint alpha = (uint)Math.Clamp(cfg.MenuBarOpacity * 255, 1, 255);
                    Glass.EnableAcrylic(this, (alpha << 24) | 0x141414);
                    break;
                default: // Vidro: a light veil over the blur keeps the wallpaper's color
                    Glass.EnableAcrylic(this, 0x40000000 | 0x1C1C1C);
                    break;
            }
            appBar = new AppBar(this, Native.ABE_TOP);
            appBar.ExplorerRestarted += Taskbar.EnsureHidden;
            Reposition();
        };

        if (cfg.DynamicIsland)
        {
            // Owned by the bar: always stacked above it. Created once the bar has a window to own it.
            Loaded += (_, _) =>
            {
                if (island != null) return;
                Island.Start();
                island = new IslandWindow(cfg, monitor, tracker, this) { Owner = this };
                island.Show();
            };
            // Hidden with the bar (fullscreen apps), back with it (also over fullscreen when revealed).
            IsVisibleChanged += (_, _) =>
            {
                if (island == null) return;
                if (IsVisible) { island.Show(); island.Place(); }
                else island.Hide();
            };
        }

        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        tracker.ForegroundChanged += UpdateAppName;
        tracker.AppsChanged += RetryAppMenus;
        tracker.FullscreenChanged += UpdateVisibility;
        NowPlaying.Changed += UpdateNowPlaying;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Closed += (_, _) =>
        {
            closed = true;
            tracker.ForegroundChanged -= UpdateAppName;
            tracker.AppsChanged -= RetryAppMenus;
            tracker.FullscreenChanged -= UpdateVisibility;
            NowPlaying.Changed -= UpdateNowPlaying;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            clockTimer.Stop();
            statusTimer.Stop();
            revealTimer.Stop();
            appBar?.Release();
        };

        clockTimer.Tick += (_, _) => UpdateClock();
        statusTimer.Tick += (_, _) => UpdateStatus();
        revealTimer.Tick += (_, _) => OnRevealTick();

        UpdateAppName();
        UpdateNowPlaying();
        UpdateClock();
        UpdateNetwork();
        UpdateStatus();
        statusTimer.Start();
    }

    public void ReleaseAppBar() => appBar?.Release();

    /// <summary>Song title while something plays; paused shows dimmed; nothing at all hides the item.</summary>
    private void UpdateNowPlaying()
    {
        bool show = cfg.ShowNowPlaying && !cfg.DynamicIsland && NowPlaying.HasSession;
        nowPlayingItem.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { equalizer.Playing = false; return; }
        nowPlayingTitle.Text = NowPlaying.Title;
        nowPlayingItem.Opacity = NowPlaying.Playing ? 1 : 0.6;
        nowPlayingArt.Background = NowPlaying.Art != null ? new ImageBrush(NowPlaying.Art) { Stretch = Stretch.UniformToFill } : null;
        nowPlayingGlyph.Visibility = NowPlaying.Art != null ? Visibility.Hidden : Visibility.Visible;
        equalizer.Playing = NowPlaying.Playing;
        nowPlayingItem.ToolTip = NowPlaying.Artist.Length > 0 ? $"{NowPlaying.Title} — {NowPlaying.Artist}" : NowPlaying.Title;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateNetwork);

    private void Reposition()
    {
        if (closed) return;
        var m = Native.MonitorInfo(monitor);
        int h = (int)Math.Round(cfg.MenuBarHeight * m.Scale);
        if (cfg.MenuBarFloating)
        {
            // The window is the floating pill; the reserved strip also covers the gaps above and below it,
            // so maximized windows keep the same distance from the bar as the bar from the screen edge.
            int gap = (int)Math.Round(FloatGap * m.Scale);
            Glass.Place(this, m.Bounds.Left + gap, m.Bounds.Top + gap, m.Bounds.Width - 2 * gap, h);
            appBar?.Reserve(m.Bounds, h + 2 * gap);
            island?.Place();
            return;
        }
        Glass.Place(this, m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, h);
        appBar?.Reserve(m.Bounds, h);
        island?.Place();
    }

    /// <summary>Bottom edge of the bar in screen pixels (below the floating gap when it floats).</summary>
    private int BarBottom(Native.RECT bounds, double scale) =>
        bounds.Top + (int)Math.Round((cfg.MenuBarHeight + (cfg.MenuBarFloating ? FloatGap : 0)) * scale);

    /// <summary>Visible on its monitor unless something is fullscreen there (video, game).</summary>
    private void UpdateVisibility()
    {
        if (closed) return;
        bool fs = tracker.IsFullscreen(monitor);
        if (!fs)
        {
            revealTimer.Stop();
            // Only when coming back: Reserve → ABM_SETPOS makes the shell re-lay out maximized windows.
            if (!IsVisible || revealed) { revealed = false; Show(); Reposition(); }
            return;
        }
        if (revealed) return; // the pointer is on it; OnRevealTick hides it once the pointer leaves
        if (IsVisible) Hide();
        // Polled (only while fullscreen): a low-level mouse hook would add latency to every mouse move,
        // games included, and a hidden bar gets no mouse events.
        revealTicks = 0;
        if (cfg.MenuBarRevealInFullscreen) revealTimer.Start();
    }

    /// <summary>
    /// macOS-style reveal over a fullscreen app: ~250 ms on the top edge shows the bar (not activated, so the
    /// video keeps focus and stays fullscreen); it goes away ~400 ms after the pointer leaves it, unless one of
    /// its menus or panels is open.
    /// </summary>
    private void OnRevealTick()
    {
        if (closed || !Native.GetCursorPos(out var p)) return;
        var b = Native.MonitorInfo(monitor).Bounds;
        bool onMonitor = p.X >= b.Left && p.X < b.Right;

        if (!revealed)
        {
            bool atEdge = onMonitor && p.Y <= b.Top && p.Y >= b.Top - 1 && !Native.CursorClipped();
            revealTicks = atEdge ? revealTicks + 1 : 0;
            if (revealTicks < 3) return;
            revealed = true;
            revealTicks = 0;
            Show();
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // Above the fullscreen window even when it is topmost itself (some players are).
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }

        int barBottom = BarBottom(b, Native.MonitorInfo(monitor).Scale);
        bool over = onMonitor && p.Y < barBottom + 6;
        bool busy = menusOpen > 0 || Flyout.IsOpen || island?.IsMouseOver == true || Native.FindWindow("#32768", null) != IntPtr.Zero; // #32768: a Win32 menu
        revealTicks = over || busy ? 0 : revealTicks + 1;
        if (revealTicks < 5) return;
        revealed = false;
        revealTicks = 0;
        if (tracker.IsFullscreen(monitor)) Hide();
        else { revealTimer.Stop(); Reposition(); }
    }

    /// <param name="pad">Side padding: icons sit tighter together than text items, as on the Mac.</param>
    private Border Item(UIElement content, Action<Border> click, string? tip, double pad = 8)
    {
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(pad, 0, pad, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(6),
            ToolTip = tip,
        };
        BarFx.Prepare(b);
        b.MouseEnter += (_, _) => BarFx.Hot(b, true);
        b.MouseLeave += (_, _) => { if (b.ContextMenu?.IsOpen != true) BarFx.Hot(b, false); };
        b.MouseLeftButtonDown += (_, _) => BarFx.Pressed(b);
        b.MouseLeftButtonUp += (_, _) =>
        {
            click(b);
            BarFx.Hot(b, b.IsMouseOver || b.ContextMenu?.IsOpen == true);
        };
        // On the Mac a right (Control) click on any menu bar item opens the same menu as a left click.
        b.MouseRightButtonUp += (_, e) => { e.Handled = true; click(b); };
        // Open() leaves the last menu in b.ContextMenu; WPF would reopen that stale one on right-click too.
        b.ContextMenuOpening += (_, e) => e.Handled = true;
        return b;
    }

    private void Open(Border anchor, ContextMenu m)
    {
        anchor.ContextMenu = m;
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Bottom;
        m.VerticalOffset = 3;
        m.Opened += (_, _) => { menusOpen++; BarFx.Hot(anchor, true); };
        m.Closed += (_, _) => { menusOpen = Math.Max(0, menusOpen - 1); if (!anchor.IsMouseOver) BarFx.Hot(anchor, false); };
        Ui.CloseOnOutsideClick(m);
        m.IsOpen = true;
    }

    #region Menus

    private void ShowSystemMenu(Border b) => Open(b, SystemMenu(cfg));

    /// <summary>The ⊞ menu (also the island's right-click menu when there is no bar).</summary>
    public static ContextMenu SystemMenu(DocaConfig cfg)
    {
        void Confirm(DocaDialog.Kind kind, string title, string message, string actionText, Action action) =>
            DocaDialog.Confirm(kind, title, message, actionText, action, cfg.Font);
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Sobre este PC", () => Shell.Open("ms-settings:about")));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Configurações do Sistema…", () => Shell.Open("ms-settings:")));
        m.Items.Add(Ui.Item("Menu Iniciar", () => Shell.WinKey()));
        m.Items.Add(Ui.Item("Gerenciador de Tarefas", () => Shell.Open("taskmgr.exe")));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Suspender", () => Native.SetSuspendState(false, false, false)));
        m.Items.Add(Ui.Item("Reiniciar…", () => Confirm(DocaDialog.Kind.Restart, "Reiniciar o computador agora?",
            "Salve o que estiver fazendo: os apps abertos serão fechados.", "Reiniciar",
            () => Shell.Open("shutdown.exe", "/r /t 0"))));
        m.Items.Add(Ui.Item("Desligar…", () => Confirm(DocaDialog.Kind.Shutdown, "Desligar o computador agora?",
            "Salve o que estiver fazendo: os apps abertos serão fechados.", "Desligar",
            () => Shell.Open("shutdown.exe", "/s /t 0"))));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Bloquear Tela", () => Native.LockWorkStation()));
        m.Items.Add(Ui.Item("Encerrar Sessão…", () => Confirm(DocaDialog.Kind.SignOut, "Encerrar a sessão agora?",
            "Os apps abertos serão fechados e você voltará para a tela de entrada.", "Encerrar Sessão",
            () => Shell.Open("shutdown.exe", "/l"))));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Preferências do Doca…", App.ShowSettings));
        m.Items.Add(Ui.Item("Iniciar com o Windows", Startup.Toggle, Startup.Enabled));
        m.Items.Add(Ui.Item("Reiniciar o Doca", App.Restart));
        m.Items.Add(Ui.Item("Sair do Doca", () => Application.Current.Shutdown()));
        return m;
    }

    private void ShowAppMenu(Border b)
    {
        var app = tracker.ForegroundApp;
        var fg = tracker.Foreground;
        bool hasWindow = app != null && Native.IsWindow(fg) && WindowTracker.IsAppWindow(fg);
        string name = app?.Name ?? "App";
        List<IntPtr> Windows(string key) => tracker.Apps.FirstOrDefault(a => a.Key == key)?.Windows ?? new();

        var m = new ContextMenu();
        m.Items.Add(Ui.Item($"Ocultar {name}", () => Windows(app!.Key).ForEach(Shell.Minimize), enabled: app != null));
        m.Items.Add(Ui.Item("Ocultar Outros", () =>
        {
            foreach (var a in tracker.Apps.Where(a => a.Key != app?.Key)) a.Windows.ForEach(Shell.Minimize);
        }));
        m.Items.Add(Ui.Item("Mostrar Tudo", () =>
        {
            foreach (var w in tracker.Apps.SelectMany(a => a.Windows).Where(Native.IsIconic)) Native.ShowWindow(w, 4 /* SW_SHOWNOACTIVATE */);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Minimizar Janela", () => Shell.Minimize(fg), enabled: hasWindow));
        m.Items.Add(Ui.Item(hasWindow && Native.IsZoomed(fg) ? "Restaurar Janela" : "Maximizar Janela", () => Shell.ToggleMaximize(fg), enabled: hasWindow));
        m.Items.Add(Ui.Item("Fechar Janela", () => Shell.Close(fg), enabled: hasWindow));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item($"Encerrar {name}", () => Shell.CloseAll(Windows(app!.Key), name), enabled: app != null && Windows(app.Key).Count > 0));
        m.Items.Add(Ui.Item("Forçar Encerramento", () => Shell.ForceQuit(app!.ExePath, Windows(app.Key), name), enabled: app != null));
        Open(b, m);
    }

    #endregion

    #region Status

    private void UpdateAppName()
    {
        var app = tracker.ForegroundApp;
        appName.Text = app?.Name ?? "Explorador de Arquivos";
        UpdateAppMenus();
    }

    private void RetryAppMenus()
    {
        if (tracker.Foreground != menusFor) UpdateAppName();
    }

    /// <summary>The foreground app's own menus (Arquivo, Editar…) next to its name, like macOS.</summary>
    private async void UpdateAppMenus()
    {
        int version = ++appMenusVersion;
        var fg = tracker.Foreground;
        if (!cfg.AppMenus || !WindowTracker.IsAppWindow(fg))
        {
            // A just-launched app may get focus before it has a title; AppsChanged retries.
            menusFor = IntPtr.Zero;
            appMenus.Children.Clear();
            return;
        }
        menusFor = fg;

        AppMenuSet set;
        try { set = await AppMenus.GetAsync(fg); }
        catch (Exception ex) { App.Log(ex); return; }
        if (version != appMenusVersion || closed) return; // focus moved on meanwhile

        appMenus.Children.Clear();
        foreach (var node in set.Menus)
        {
            var text = new TextBlock { Text = node.Text, VerticalAlignment = VerticalAlignment.Center, Opacity = node.Enabled ? 1 : 0.45 };
            appMenus.Children.Add(Item(text, b =>
            {
                if (!node.Enabled) return;
                if (node.Children == null) { node.Invoke?.Invoke(); return; }
                var m = new ContextMenu();
                FillMenu(m, node.Children());
                Open(b, m);
            }, null));
        }
    }

    private void FillMenu(ItemsControl parent, List<MenuNode> nodes)
    {
        // Drop leading, trailing and doubled separators.
        var clean = new List<MenuNode>();
        foreach (var n in nodes)
            if (!n.IsSeparator || (clean.Count > 0 && !clean[^1].IsSeparator)) clean.Add(n);
        while (clean.Count > 0 && clean[^1].IsSeparator) clean.RemoveAt(clean.Count - 1);

        foreach (var n in clean)
        {
            if (n.IsSeparator) { parent.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = n.Text, IsEnabled = n.Enabled, IsChecked = n.Checked, InputGestureText = n.Shortcut ?? "" };
            if (n.Children != null)
            {
                // Built when opened, so Win32 apps can refresh the submenu first.
                mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
                bool filled = false;
                mi.SubmenuOpened += (_, e) =>
                {
                    if (filled || e.OriginalSource != mi) return;
                    filled = true;
                    mi.Items.Clear();
                    FillMenu(mi, n.Children());
                };
            }
            else if (n.Invoke != null)
            {
                var invoke = n.Invoke;
                // Run after the menu has closed so keystrokes reach the app, not the popup.
                mi.Click += (_, _) => Dispatcher.BeginInvoke(invoke, DispatcherPriority.Background);
            }
            parent.Items.Add(mi);
        }
    }

    private void UpdateClock()
    {
        var c = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].TrimEnd('.');
        string time = cfg.ClockShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
        clock.Text = cfg.ClockShowDate
            ? $"{Cap(now.ToString("ddd", c))} {now.Day} {Cap(now.ToString("MMM", c))}  {time}"
            : time;
        // Fire right after the next second/minute boundary instead of polling.
        clockTimer.Interval = cfg.ClockShowSeconds
            ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 10)
            : TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 30);
        clockTimer.Start();
    }

    private void UpdateStatus()
    {
        UpdateVolume();
        UpdateBattery();
        if (statusTicks % 3 == 0) _ = UpdateBluetooth();
        if (++statusTicks % 20 == 0) UpdateNetwork();
    }

    private void UpdateVolume()
    {
        var v = cfg.ShowVolume ? Audio.Get() : null;
        if (v == null) { volumeItem.Visibility = Visibility.Collapsed; return; }
        volumeItem.Visibility = Visibility.Visible;
        var (level, muted) = v.Value;
        volume.Text = muted || level < 0.01f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
        volumeItem.ToolTip = muted ? "Sem som" : $"Volume: {Math.Round(level * 100)}%  (role para ajustar, botão direito silencia)";
    }

    private void UpdateBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100)
        {
            batteryItem.Visibility = Visibility.Collapsed;
            return;
        }
        batteryItem.Visibility = Visibility.Visible;
        int pct = s.BatteryLifePercent, idx = Math.Clamp(pct / 10, 0, 10);
        bool charging = s.ACLineStatus == 1;
        battery.Text = ((char)(idx == 10 ? (charging ? 0xE83E : 0xE83F) : (charging ? 0xE85A : 0xE850) + idx)).ToString();
        batteryPct.Text = $"{pct}%";
    }

    private async Task UpdateBluetooth()
    {
        // Hidden: skip the radio and device queries too (they run every few seconds).
        if (!cfg.ShowBluetooth) { bluetoothItem.Visibility = Visibility.Collapsed; return; }
        await Bluetooth.LoadRadiosAsync();
        if (closed) return;
        bluetoothItem.Visibility = Bluetooth.HasAdapter ? Visibility.Visible : Visibility.Collapsed;
        if (!Bluetooth.HasAdapter) return;
        bluetooth.Opacity = Bluetooth.IsOn ? 1 : 0.4;
        var connected = Bluetooth.IsOn ? Bluetooth.Devices().Where(d => d.Connected).Select(d => d.Name).ToList() : new();
        bluetoothItem.ToolTip = !Bluetooth.IsOn ? "Bluetooth desligado"
            : connected.Count == 0 ? "Bluetooth: nenhum dispositivo conectado"
            : "Conectado: " + string.Join(", ", connected);
    }

    private void UpdateNetwork()
    {
        wifiItem.Visibility = cfg.ShowWifi ? Visibility.Visible : Visibility.Collapsed;
        if (!cfg.ShowWifi) return;
        NetworkInterface? up = null;
        try
        {
            up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                            n.GetIPProperties().GatewayAddresses.Count > 0)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .FirstOrDefault();
        }
        catch { }
        bool wireless = up?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        var current = wireless ? Wlan.Current() : null;
        wifi.Text = current != null ? Wlan.SignalGlyph(current.Signal) : up == null || wireless ? "" : "";
        wifi.Opacity = up == null ? 0.4 : 1;
        wifiItem.ToolTip = up == null ? "Sem conexão" : current != null ? $"{current.Ssid} · sinal {current.Signal}%" : up.Name;
    }

    #endregion
}

internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("Doca") != null;
        }
    }

    public static void Toggle()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        if (Enabled) k.DeleteValue("Doca", false);
        else k.SetValue("Doca", $"\"{Environment.ProcessPath}\"");
    }
}
