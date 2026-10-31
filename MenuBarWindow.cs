using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private static readonly Brush hoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

    private readonly DocaConfig cfg;
    private readonly WindowTracker tracker;
    private readonly TextBlock appName = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock clock = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel appMenus = new() { Orientation = Orientation.Horizontal };
    private int appMenusVersion;
    private IntPtr menusFor;
    private readonly TextBlock wifi = Glyph(""), volume = Glyph(""), battery = Glyph("");
    private readonly TextBlock batteryPct = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), FontSize = 12 };
    private readonly Border batteryItem, volumeItem, wifiItem, bluetoothItem;
    private readonly TextBlock bluetooth = Glyph("");
    private readonly TextBlock nowPlayingGlyph = new() { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock nowPlayingTitle = new() { MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border nowPlayingItem;
    private readonly DispatcherTimer clockTimer = new(), statusTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly IntPtr monitor;
    private AppBar? appBar;
    private int statusTicks;
    private bool closed;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static TextBlock Glyph(string g) => new()
    {
        Text = g, FontFamily = new FontFamily(IconFont), FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
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
        left.Children.Add(Item(logo, ShowSystemMenu, "Menu do sistema"));
        left.Children.Add(Item(appName, ShowAppMenu, null));
        left.Children.Add(appMenus);

        var batteryPanel = new StackPanel { Orientation = Orientation.Horizontal };
        batteryPanel.Children.Add(batteryPct);
        batteryPanel.Children.Add(battery);
        batteryPct.Margin = new Thickness(0, 0, 5, 0);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        batteryItem = Item(batteryPanel, _ => Shell.WinKey(0x41), null);
        wifiItem = Item(wifi, b => Flyout.Toggle("wifi", b, () => new WifiFlyout(cfg)), null);
        bluetoothItem = Item(bluetooth, b => Flyout.Toggle("bluetooth", b, () => new BluetoothFlyout(cfg)), null);
        bluetoothItem.Visibility = Visibility.Collapsed; // until we know there is an adapter
        volumeItem = Item(volume, b => Flyout.Toggle("sound", b, () => new SoundFlyout(cfg)), null);
        volumeItem.MouseWheel += (_, e) => { Audio.Nudge(e.Delta > 0 ? 0.02f : -0.02f); UpdateVolume(); };
        volumeItem.MouseRightButtonUp += (_, _) => { Audio.ToggleMute(); UpdateVolume(); };
        TrayIconsPanel? tray = cfg.ShowTrayIcons ? new TrayIconsPanel(hoverBrush, cfg) : null;
        if (tray != null) right.Children.Add(tray);
        var nowPlayingPanel = new StackPanel { Orientation = Orientation.Horizontal };
        nowPlayingPanel.Children.Add(nowPlayingGlyph);
        nowPlayingPanel.Children.Add(nowPlayingTitle);
        nowPlayingItem = Item(nowPlayingPanel, b => Flyout.Toggle("media", b, () => new MediaFlyout(cfg)), null);
        right.Children.Add(nowPlayingItem);
        right.Children.Add(Item(Glyph(""), _ => SpotlightWindow.Toggle(cfg), "Spotlight (Alt+Espaço)"));
        right.Children.Add(batteryItem);
        right.Children.Add(bluetoothItem);
        right.Children.Add(wifiItem);
        right.Children.Add(volumeItem);
        right.Children.Add(Item(Glyph(""), b => Flyout.Toggle("control", b, () => new ControlCenterFlyout(cfg)), "Central de Controle"));
        right.Children.Add(Item(clock, b => Flyout.Toggle("calendar", b, () => new CalendarFlyout(cfg)), null));

        var root = new Grid();
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        // Tray icons get whatever width the app menus (left) and status items (right) leave free;
        // the rest goes behind ⌄ instead of disappearing under the menus.
        void FitTray()
        {
            if (tray == null) return;
            double fixedRight = right.ActualWidth - tray.ActualWidth;
            tray.Available = Math.Max(0, ActualWidth - left.ActualWidth - left.Margin.Left - fixedRight - right.Margin.Right - 24);
        }
        left.SizeChanged += (_, _) => FitTray();
        right.SizeChanged += (_, _) => FitTray();
        SizeChanged += (_, _) => FitTray();

        SourceInitialized += (_, _) =>
        {
            Glass.MakeOverlay(this);
            uint alpha = (uint)Math.Clamp(cfg.MenuBarOpacity * 255, 1, 255);
            Glass.EnableAcrylic(this, (alpha << 24) | 0x141414);
            appBar = new AppBar(this, Native.ABE_TOP);
            appBar.ExplorerRestarted += Taskbar.EnsureHidden;
            Reposition();
        };

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
            appBar?.Release();
        };

        clockTimer.Tick += (_, _) => UpdateClock();
        statusTimer.Tick += (_, _) => UpdateStatus();

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
        bool show = cfg.ShowNowPlaying && NowPlaying.HasSession;
        nowPlayingItem.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        nowPlayingTitle.Text = NowPlaying.Title;
        nowPlayingItem.Opacity = NowPlaying.Playing ? 1 : 0.6;
        nowPlayingItem.ToolTip = NowPlaying.Artist.Length > 0 ? $"{NowPlaying.Title} — {NowPlaying.Artist}" : NowPlaying.Title;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateNetwork);

    private void Reposition()
    {
        if (closed) return;
        var m = Native.MonitorInfo(monitor);
        int h = (int)Math.Round(cfg.MenuBarHeight * m.Scale);
        Glass.Place(this, m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, h);
        appBar?.Reserve(m.Bounds, h);
    }

    /// <summary>Visible on its monitor unless something is fullscreen there (video, game).</summary>
    private void UpdateVisibility()
    {
        if (closed) return;
        bool fs = tracker.IsFullscreen(monitor);
        if (fs && IsVisible) Hide();
        else if (!fs && !IsVisible) { Show(); Reposition(); }
    }

    private Border Item(UIElement content, Action<Border> click, string? tip)
    {
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.Background = hoverBrush;
        b.MouseLeave += (_, _) => { if (b.ContextMenu?.IsOpen != true) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => click(b);
        return b;
    }

    private void Open(Border anchor, ContextMenu m)
    {
        anchor.ContextMenu = m;
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Bottom;
        m.VerticalOffset = 3;
        m.Opened += (_, _) => anchor.Background = hoverBrush;
        m.Closed += (_, _) => { if (!anchor.IsMouseOver) anchor.Background = Brushes.Transparent; };
        Ui.CloseOnOutsideClick(m);
        m.IsOpen = true;
    }

    #region Menus

    private void ShowSystemMenu(Border b)
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Sobre este PC", () => Shell.Open("ms-settings:about")));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Configurações do Sistema…", () => Shell.Open("ms-settings:")));
        m.Items.Add(Ui.Item("Menu Iniciar", () => Shell.WinKey()));
        m.Items.Add(Ui.Item("Gerenciador de Tarefas", () => Shell.Open("taskmgr.exe")));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Suspender", () => Native.SetSuspendState(false, false, false)));
        m.Items.Add(Ui.Item("Reiniciar…", () => Confirm("Reiniciar o computador agora?", () => Shell.Open("shutdown.exe", "/r /t 0"))));
        m.Items.Add(Ui.Item("Desligar…", () => Confirm("Desligar o computador agora?", () => Shell.Open("shutdown.exe", "/s /t 0"))));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Bloquear Tela", () => Native.LockWorkStation()));
        m.Items.Add(Ui.Item("Encerrar Sessão…", () => Confirm("Encerrar a sessão agora?", () => Shell.Open("shutdown.exe", "/l"))));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Preferências do Doca…", App.ShowSettings));
        m.Items.Add(Ui.Item("Iniciar com o Windows", Startup.Toggle, Startup.Enabled));
        m.Items.Add(Ui.Item("Reiniciar o Doca", App.Restart));
        m.Items.Add(Ui.Item("Sair do Doca", () => Application.Current.Shutdown()));
        Open(b, m);
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

    private static void Confirm(string text, Action action)
    {
        if (MessageBox.Show(text, "Doca", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) action();
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
