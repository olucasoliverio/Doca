using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Doca;

/// <summary>
/// A panel that drops down from a menu bar item (instead of Windows' flyouts at the bottom of the screen).
/// Acrylic, rounded, closes when it loses focus or on Esc; clicking the same item again toggles it.
/// </summary>
internal abstract class Flyout : Window
{
    private static Flyout? current;

    /// <summary>A panel is open (the menu bar revealed over fullscreen stays while it is).</summary>
    public static bool IsOpen => current != null;
    private static string? lastKey;
    private static DateTime lastClosed;

    protected const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Brush blue = new SolidColorBrush(Color.FromRgb(0x0A, 0x63, 0xD9));
    /// <summary>"On" circles, today in the calendar: the island theme's accent when it has a color, else blue.</summary>
    protected static Brush Accent => IslandTheme.Accent == IslandTheme.Ink ? blue : IslandTheme.AccentBrush;
    // Ink-based (IslandTheme): white on the dark look, dark on the island's light themes.
    protected static Brush RowHover => IslandTheme.InkA(0x24);

    private FrameworkElement? anchor;
    /// <summary>The menu bar item this panel hangs from; another panel opened from here reuses it.</summary>
    protected FrameworkElement? Anchor => anchor;
    private bool closing;
    private string key = "";
    protected readonly StackPanel Body = new();

    /// <summary>Raised when the panel asks to close (a link was used); the island collapses on it.</summary>
    public event Action? Dismissed;

    /// <summary>
    /// Hands the panel's content to another host (the island's tabs) instead of showing this window.
    /// anchor is where panels opened from inside it (Wi-Fi, Bluetooth…) hang from. The caller closes
    /// this window when it is done with the content. Lifecycle hooks of the content use Body.Loaded/Unloaded,
    /// so they work in both places.
    /// </summary>
    public FrameworkElement Detach(FrameworkElement anchor)
    {
        this.anchor = anchor;
        ((Border)Content).Child = null;
        return Body;
    }

    public static void Toggle(string key, FrameworkElement anchor, Func<Flyout> create)
    {
        var previous = current;
        if (previous?.key == key) { previous.CloseFlyout(); return; }
        // The click that dismissed this flyout (by deactivating it) must not reopen it.
        if (previous == null && lastKey == key && (DateTime.Now - lastClosed).TotalMilliseconds < 350) return;

        var next = create();
        next.key = key;
        next.anchor = anchor;
        current = next;
        // Open the new one before closing the old: closing the active window first would hand
        // activation back to the app behind, which would immediately dismiss the new flyout.
        if (previous != null) previous.switching = true;
        next.Show();
        next.Activate();
        previous?.CloseFlyout();
    }

    private bool switching;

#pragma warning disable WPF0001 // Fluent theme
    protected Flyout(DocaConfig cfg, double width)
    {
        ThemeMode = IslandTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = width;
        Left = Top = -20000;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily(cfg.Font);
        Foreground = IslandTheme.InkBrush;
        // Over the acrylic: a dark veil, or the island theme's color on its light themes.
        var veil = IslandTheme.Light ? Color.FromArgb(0xB8, IslandTheme.Current.Top.R, IslandTheme.Current.Top.G, IslandTheme.Current.Top.B) : Color.FromArgb(0x60, 0x20, 0x20, 0x22);
        Content = new Border { Padding = new Thickness(14, 12, 14, 12), Child = Body, Background = new SolidColorBrush(veil) };

        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(h, ref m);
            int dark = IslandTheme.Light ? 0 : 1, round = 2 /* DWMWCP_ROUND */, acrylic = 3 /* DWMSBT_TRANSIENTWINDOW */;
            Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            Native.DwmSetWindowAttribute(h, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref acrylic, sizeof(int));
            long ex = (Native.ExStyle(h) | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) => { if (!switching) CloseFlyout(); };
        // Closed some other way (Alt+F4, app shutdown): don't let the Deactivated that follows call Close again.
        Closing += (_, _) =>
        {
            if (closing) return;
            closing = true;
            if (current == this) current = null;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseFlyout(); };
    }

    /// <summary>Right-aligned under the anchor, kept on the anchor's monitor.</summary>
    private void Place()
    {
        if (anchor == null || PresentationSource.FromVisual(anchor) == null) return;
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return;
        var br = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var mon = Native.MonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = (int)br.X, Y = (int)br.Y }, 2));
        int x = Math.Clamp((int)br.X - r.Width + (int)(8 * mon.Scale), mon.Bounds.Left + 6, mon.Bounds.Right - r.Width - 6);
        int y = (int)br.Y + (int)(6 * mon.Scale);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected void CloseFlyout()
    {
        Dismissed?.Invoke();
        if (closing) return;
        closing = true;
        lastKey = key;
        lastClosed = DateTime.Now;
        if (current == this) current = null;
        Close();
    }

    #region Building blocks

    protected static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) };

    protected static TextBlock Caption(string text) => new() { Text = text, FontSize = 12, Opacity = 0.6, Margin = new Thickness(2, 10, 0, 4) };

    protected static TextBlock Glyph(string g, double size = 16) => new() { Text = g, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    protected static Border Divider() => new() { Height = 1, Background = IslandTheme.InkA(0x26), Margin = new Thickness(-14, 10, -14, 8) };

    /// <summary>Clickable row with hover highlight: [icon] text … [trailing].</summary>
    protected static Border Row(UIElement? icon, string text, string? detail, UIElement? trailing, Action? click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 10, 0); fe.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(icon); }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail != null) texts.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.6 });
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        if (trailing != null) { Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }

        var b = new Border { Child = g, Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        if (click != null)
        {
            b.MouseEnter += (_, _) => b.Background = RowHover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) => click();
        }
        return b;
    }

    protected Button Link(string text, Action action, bool close = true)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 5) };
        b.Click += (_, _) => { action(); if (close) CloseFlyout(); };
        return b;
    }

    protected static WrapPanel Footer(params UIElement[] items)
    {
        var p = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    #endregion
}

/// <summary>Clock → calendar of the month, like macOS' Notification Center date view.</summary>
internal sealed class CalendarFlyout : Flyout
{
    private readonly TextBlock time = new() { FontSize = 30, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock date = new() { Opacity = 0.7, Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBlock monthTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly UniformGrid grid = new() { Columns = 7 };
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime month;

    public CalendarFlyout(DocaConfig cfg) : base(cfg, 316)
    {
        var c = CultureInfo.CurrentCulture;
        month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        Body.Children.Add(time);
        Body.Children.Add(date);
        Body.Children.Add(Divider());

        var header = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        header.Children.Add(monthTitle);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        nav.Children.Add(NavButton("", -1));
        nav.Children.Add(NavButton("", 1));
        header.Children.Add(nav);
        monthTitle.MouseLeftButtonUp += (_, _) => { month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Fill(); };
        monthTitle.ToolTip = "Voltar para hoje";
        Body.Children.Add(header);
        Body.Children.Add(grid);
        Body.Children.Add(Divider());
        Body.Children.Add(Footer(
            Link("Notificações", () => Shell.WinKey(0x4E)),
            Link("Data e hora…", () => Shell.Open("ms-settings:dateandtime"))));

        tick.Tick += (_, _) => UpdateTime();
        Body.Loaded += (_, _) => tick.Start();
        Body.Unloaded += (_, _) => tick.Stop();
        Closed += (_, _) => tick.Stop();
        UpdateTime();
        Fill();
    }

    private Button NavButton(string glyph, int delta)
    {
        var b = new Button { Content = Glyph(glyph, 11), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        b.Click += (_, _) => { month = month.AddMonths(delta); Fill(); };
        return b;
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;
        time.Text = now.ToString("HH:mm:ss");
        string d = now.ToString("D", CultureInfo.CurrentCulture);
        date.Text = char.ToUpper(d[0]) + d[1..];
    }

    private void Fill()
    {
        var c = CultureInfo.CurrentCulture;
        string title = month.ToString("MMMM 'de' yyyy", c);
        monthTitle.Text = char.ToUpper(title[0]) + title[1..];
        grid.Children.Clear();

        var first = c.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            string name = c.DateTimeFormat.ShortestDayNames[((int)first + i) % 7];
            grid.Children.Add(new TextBlock { Text = name.ToUpper(), FontSize = 11, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        }

        int offset = ((int)month.DayOfWeek - (int)first + 7) % 7;
        var day = month.AddDays(-offset);
        for (int i = 0; i < 42; i++, day = day.AddDays(1))
        {
            bool today = day == DateTime.Today, inMonth = day.Month == month.Month;
            var cell = new Border
            {
                Width = 32, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(1),
                Background = today ? Accent : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
                },
                Opacity = inMonth ? 1 : 0.35,
            };
            grid.Children.Add(cell);
        }
    }
}

/// <summary>Volume, mute and output device selection.</summary>
internal sealed class SoundFlyout : Flyout
{
    private readonly StackPanel devices = new();
    private readonly Slider slider = new() { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly TextBlock value = new() { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button mute = new() { Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private bool updating;

    public SoundFlyout(DocaConfig cfg) : base(cfg, 330)
    {
        Body.Children.Add(Heading("Som"));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(mute);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        Body.Children.Add(row);

        mute.Click += (_, _) => { Audio.ToggleMute(); Refresh(); };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{e.NewValue:0}%";
            if (!updating) { Audio.SetLevel((float)(e.NewValue / 100)); UpdateMuteGlyph(); }
        };

        Body.Children.Add(Caption("Saída"));
        Body.Children.Add(devices);
        Body.Children.Add(Divider());
        Body.Children.Add(Footer(
            Link("Mixer de volume", () => Shell.Open("ms-settings:apps-volume")),
            Link("Configurações de som", () => Shell.Open("ms-settings:sound"))));

        Refresh();
        FillDevices();
    }

    private void Refresh()
    {
        updating = true;
        if (Audio.Get() is { } v) slider.Value = Math.Round(v.level * 100);
        updating = false;
        value.Text = $"{slider.Value:0}%";
        UpdateMuteGlyph();
    }

    private void UpdateMuteGlyph()
    {
        var v = Audio.Get();
        bool muted = v == null || v.Value.muted || v.Value.level < 0.01f;
        mute.Content = Glyph(muted ? "" : "", 16);
        mute.ToolTip = muted ? "Ativar som" : "Silenciar";
    }

    private void FillDevices()
    {
        devices.Children.Clear();
        foreach (var d in Audio.Outputs())
        {
            bool headset = d.Name.Contains("fone", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("head", StringComparison.OrdinalIgnoreCase);
            var check = d.IsDefault ? Glyph("", 13) : null;
            var id = d.Id;
            devices.Children.Add(Row(Glyph(headset ? "" : ""), d.Name, null, check, () =>
            {
                Audio.SetDefaultOutput(id);
                FillDevices();
                Refresh();
            }));
        }
        if (devices.Children.Count == 0) devices.Children.Add(new TextBlock { Text = "Nenhum dispositivo de saída", Opacity = 0.6, Margin = new Thickness(2) });
    }
}

/// <summary>Bluetooth on/off and paired devices; click connects/disconnects audio devices.</summary>
internal sealed class BluetoothFlyout : Flyout
{
    private readonly StackPanel list = new();
    private readonly CheckBox power = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, MinWidth = 0, ToolTip = "Ligar/desligar o Bluetooth" };
    private readonly TextBlock status = new() { FontSize = 12, Opacity = 0.7, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };

    public BluetoothFlyout(DocaConfig cfg) : base(cfg, 330)
    {
        var header = new Grid();
        header.Children.Add(Heading("Bluetooth"));
        header.Children.Add(power);
        Body.Children.Add(header);
        Body.Children.Add(list);
        Body.Children.Add(status);
        Body.Children.Add(Divider());
        Body.Children.Add(Footer(
            Link("Adicionar dispositivo…", () => Shell.Open("ms-settings:bluetooth")),
            Link("Configurações…", () => Shell.Open("ms-settings:bluetooth"))));

        power.Click += async (_, _) =>
        {
            bool on = power.IsChecked == true;
            status.Text = on ? "Ligando…" : "Desligando…";
            if (!await Bluetooth.SetOnAsync(on)) status.Text = "O Windows não permitiu mudar o Bluetooth.";
            else status.Text = "";
            Fill();
        };
        // Connection changes and battery reports arrive over a few seconds; keep the list current while open.
        refresh.Tick += (_, _) => Fill();
        Closed += (_, _) => refresh.Stop();
        Loaded += async (_, _) =>
        {
            await Bluetooth.LoadRadiosAsync();
            Fill();
            refresh.Start();
            await Bluetooth.RefreshBatteryAsync();
            Fill();
        };
    }

    private void Fill()
    {
        list.Children.Clear();
        power.Visibility = Bluetooth.HasAdapter ? Visibility.Visible : Visibility.Collapsed;
        power.IsChecked = Bluetooth.IsOn;
        if (!Bluetooth.HasAdapter) { list.Children.Add(Caption("Nenhum adaptador Bluetooth encontrado.")); return; }
        if (!Bluetooth.IsOn) { list.Children.Add(Caption("O Bluetooth está desligado.")); return; }

        var devices = Bluetooth.Devices();
        list.Children.Add(Caption(devices.Count == 0 ? "Nenhum dispositivo pareado." : "Dispositivos"));
        foreach (var d in devices)
        {
            string detail = d.Connected ? "Conectado" : "Não conectado";
            if (d.Battery is int b) detail += $" · bateria {b}%";
            UIElement? action = d.IsAudio ? new TextBlock { Text = d.Connected ? "Desconectar" : "Conectar", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center } : null;
            var icon = Glyph(d.Glyph, 18);
            if (!d.Connected) icon.Opacity = 0.55;
            var dev = d;
            list.Children.Add(Row(icon, d.Name, detail, action, () => Toggle(dev)));
        }
    }

    private void Toggle(BtDevice d)
    {
        if (!d.IsAudio)
        {
            // Only audio devices have a public "connect" request; others are managed in Settings.
            Shell.Open("ms-settings:bluetooth");
            CloseFlyout();
            return;
        }
        bool connect = !d.Connected;
        status.Text = Bluetooth.SetAudioConnected(d, connect)
            ? (connect ? $"Conectando a {d.Name}…" : $"Desconectando {d.Name}…")
            : "Não foi possível falar com o dispositivo. Abra as Configurações de Bluetooth.";
    }
}

/// <summary>Wi-Fi networks: current connection, nearby networks, join with password, disconnect.</summary>
internal sealed class WifiFlyout : Flyout
{
    private readonly StackPanel list = new();
    private readonly TextBlock status = new() { FontSize = 12, Opacity = 0.7, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? expanded;

    public WifiFlyout(DocaConfig cfg) : base(cfg, 340)
    {
        var header = new Grid();
        header.Children.Add(Heading("Wi-Fi"));
        var rescan = new Button { Content = Glyph("", 12), VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Procurar redes" };
        rescan.Click += (_, _) => { Wlan.Scan(); status.Text = "Procurando redes…"; refresh.Stop(); refresh.Start(); };
        var power = new CheckBox { MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Ligar/desligar o Wi-Fi", Visibility = Visibility.Collapsed };
        power.Click += async (_, _) =>
        {
            bool on = power.IsChecked == true;
            status.Text = on ? "Ligando o Wi-Fi…" : "Desligando o Wi-Fi…";
            if (!await Bluetooth.SetWifiOnAsync(on)) status.Text = "O Windows não permitiu mudar o Wi-Fi.";
            Later(2.5);
        };
        Loaded += async (_, _) =>
        {
            await Bluetooth.LoadRadiosAsync();
            power.Visibility = Bluetooth.HasWifiRadio ? Visibility.Visible : Visibility.Collapsed;
            power.IsChecked = Bluetooth.WifiOn;
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(rescan);
        tools.Children.Add(power);
        header.Children.Add(tools);
        Body.Children.Add(header);
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body.Children.Add(status);
        Body.Children.Add(Divider());
        Body.Children.Add(Footer(Link("Configurações de rede…", () => Shell.Open("ms-settings:network-wifi"))));

        refresh.Tick += (_, _) => { refresh.Stop(); Fill(); };
        Closed += (_, _) => refresh.Stop();

        Wlan.Scan();
        Fill();
        refresh.Start(); // the scan takes a moment; show fresh results when it lands
    }

    private void Fill()
    {
        list.Children.Clear();
        if (Wlan.Interface() == null)
        {
            var eth = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                n.GetIPProperties().GatewayAddresses.Count > 0);
            list.Children.Add(Row(Glyph(""), eth != null ? "Conectado por cabo" : "Sem conexão", eth?.Name, null, null));
            return;
        }

        var nets = Wlan.Networks();
        var current = nets.FirstOrDefault(n => n.Connected);
        if (current != null)
        {
            var off = Link("Desconectar", () => { Wlan.Disconnect(); status.Text = "Desconectado."; Later(1.5); }, close: false);
            list.Children.Add(Row(Glyph(Wlan.SignalGlyph(current.Signal), 18), current.Ssid, $"Conectado · sinal {current.Signal}%", off, null));
            list.Children.Add(Caption("Outras redes"));
        }
        else list.Children.Add(Caption("Redes disponíveis"));

        foreach (var n in nets.Where(n => !n.Connected))
        {
            var lockIcon = n.Secured ? Glyph("", 12) : null;
            if (lockIcon != null) lockIcon.Opacity = 0.6;
            var net = n;
            list.Children.Add(Row(Glyph(Wlan.SignalGlyph(n.Signal), 18), n.Ssid, n.HasProfile ? "Salva" : null, lockIcon, () => Choose(net)));
            if (expanded == n.Ssid) list.Children.Add(PasswordRow(n));
        }
        if (nets.Count == 0) list.Children.Add(new TextBlock { Text = "Nenhuma rede encontrada.", Opacity = 0.6, Margin = new Thickness(2) });
    }

    private void Choose(WifiNetwork n)
    {
        if (n.HasProfile && n.ProfileName != null)
        {
            status.Text = Wlan.Connect(n.ProfileName) ? $"Conectando a {n.Ssid}…" : "Não foi possível conectar.";
            Later(4);
        }
        else if (!n.Secured)
        {
            status.Text = Wlan.Join(n, null) ? $"Conectando a {n.Ssid}…" : "Não foi possível conectar.";
            Later(4);
        }
        else if (Wlan.CanJoin(n))
        {
            expanded = expanded == n.Ssid ? null : n.Ssid;
            Fill();
        }
        else
        {
            // Enterprise (802.1X) networks need Windows' own sign-in UI.
            Shell.Open("ms-availablenetworks:");
            CloseFlyout();
        }
    }

    private UIElement PasswordRow(WifiNetwork n)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var go = new Button { Content = "Conectar", Padding = new Thickness(12, 4, 12, 5) };
        void Join()
        {
            if (box.Password.Length == 0) return;
            status.Text = Wlan.Join(n, box.Password) ? $"Conectando a {n.Ssid}…" : "Não foi possível criar o perfil da rede.";
            expanded = null;
            Later(5);
        }
        go.Click += (_, _) => Join();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Join(); };
        var g = new Grid { Margin = new Thickness(36, 2, 6, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(box);
        Grid.SetColumn(go, 1);
        g.Children.Add(go);
        Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
        return g;
    }

    private void Later(double seconds)
    {
        refresh.Stop();
        refresh.Interval = TimeSpan.FromSeconds(seconds);
        refresh.Start();
    }
}
