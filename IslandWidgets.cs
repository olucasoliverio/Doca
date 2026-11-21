using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Doca;

/// <summary>
/// CPU, memory, system disk and network throughput. Sampled once a second, and only while something shows
/// it (the panel's Sistema widget): Subscribe/Unsubscribe count the viewers.
/// </summary>
internal static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private static int viewers;
    private static long lastIdle, lastKernel, lastUser, lastRx, lastTx;
    private static DateTime lastNet;

    public static double Cpu { get; private set; }
    public static double Memory { get; private set; }
    public static ulong MemoryUsed { get; private set; }
    public static ulong MemoryTotal { get; private set; }
    public static double Disk { get; private set; }
    public static string DiskText { get; private set; } = "";
    public static double Down { get; private set; }
    public static double Up { get; private set; }
    public static event Action? Changed;

    static SystemStats() => tick.Tick += (_, _) => Sample();

    public static void Subscribe(Action a)
    {
        Changed += a;
        if (viewers++ == 0) { Sample(); tick.Start(); }
    }

    public static void Unsubscribe(Action a)
    {
        Changed -= a;
        if (--viewers <= 0) { viewers = 0; tick.Stop(); }
    }

    private static void Sample()
    {
        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long dIdle = idle - lastIdle, dTotal = kernel - lastKernel + user - lastUser; // kernel time includes idle
                if (lastKernel != 0 && dTotal > 0) Cpu = Math.Clamp(1 - dIdle / (double)dTotal, 0, 1);
                (lastIdle, lastKernel, lastUser) = (idle, kernel, user);
            }
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m))
            {
                MemoryTotal = m.ullTotalPhys;
                MemoryUsed = m.ullTotalPhys - m.ullAvailPhys;
                Memory = MemoryUsed / (double)Math.Max(1, MemoryTotal);
            }
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            if (drive.IsReady)
            {
                Disk = 1 - drive.AvailableFreeSpace / (double)Math.Max(1, drive.TotalSize);
                DiskText = $"{drive.AvailableFreeSpace / 1e9:0} GB livres";
            }
            long rx = 0, tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.Now;
            double secs = (now - lastNet).TotalSeconds;
            if (lastRx != 0 && secs > 0.2 && secs < 5)
            {
                Down = Math.Max(0, (rx - lastRx) / secs);
                Up = Math.Max(0, (tx - lastTx) / secs);
            }
            (lastRx, lastTx, lastNet) = (rx, tx, now);
        }
        catch (Exception ex) { App.Log(ex); }
        Changed?.Invoke();
    }
}

/// <summary>
/// The panel's home tab: a grid of small widgets (music, calendar, system, timer, downloads, clipboard) chosen
/// in Preferências. A click on a widget's header opens its full tab.
/// </summary>
internal sealed class HomeView : UniformGrid
{
    public const double CardW = 197, CardH = 112;
    private readonly Action<int> openTab;
    private readonly List<Action> cleanup = new();

    /// <param name="openTab">Opens a panel tab by index.</param>
    public HomeView(DocaConfig cfg, Action<int> openTab, Action dismiss)
    {
        this.openTab = openTab;
        Columns = 2;
        Margin = new Thickness(-5, -5, -5, -5);
        foreach (var w in cfg.IslandWidgets)
        {
            var card = w switch
            {
                "Música" => Music(),
                "Calendário" => Calendar(),
                "Sistema" => Stats(),
                "Timer" => TimerCard(),
                "Downloads" => Downloads(),
                "Copiados" => cfg.IslandClipboard ? Clipboard(dismiss) : null,
                _ => null,
            };
            if (card != null) Children.Add(card);
        }
        Unloaded += (_, _) => { foreach (var c in cleanup) c(); cleanup.Clear(); };
    }

    /// <summary>Hooks an event while the view is shown.</summary>
    private void While(Action subscribe, Action unsubscribe)
    {
        subscribe();
        cleanup.Add(unsubscribe);
    }

    private Border Card(string glyph, string title, int? tab, UIElement body)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var g = IslandBrushes.Glyph(glyph, 11);
        g.Foreground = IslandBrushes.Dim;
        head.Children.Add(g);
        head.Children.Add(new TextBlock { Text = title, FontSize = 11.5, Foreground = IslandBrushes.Dim, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        var card = new Border
        {
            Child = dock, Height = CardH, Margin = new Thickness(5), Padding = new Thickness(14, 11, 14, 12),
            CornerRadius = new CornerRadius(18), Background = IslandBrushes.Card,
        };
        if (tab is int t)
        {
            head.Cursor = Cursors.Hand;
            head.ToolTip = $"Abrir {title}";
            head.MouseLeftButtonUp += (_, e) => { e.Handled = true; openTab(t); };
        }
        return card;
    }

    private static TextBlock Line(double size = 12.5, bool bold = false, Brush? brush = null) => new()
    {
        FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = brush ?? Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Small(string glyph, Action click, string tip, double size = 13)
    {
        var b = IslandBrushes.Button(IslandBrushes.Glyph(glyph, size), click, tip, 14, new Thickness(0));
        b.Width = 30;
        b.Height = 28;
        b.Background = Brushes.Transparent;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    #region Widgets

    private Border Music()
    {
        var art = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(11), Background = IslandBrushes.Faint, VerticalAlignment = VerticalAlignment.Top };
        var title = Line(12.5, true);
        var artist = Line(11.5, false, IslandBrushes.Dim);
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(artist);
        var top = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        top.Children.Add(art);
        top.Children.Add(texts);
        var play = Small(IslandGlyphs.Pause, NowPlaying.PlayPause, "Tocar/pausar", 14);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, -30, -8, -6) };
        controls.Children.Add(Small("\uE892", NowPlaying.Previous, "Anterior", 11));
        controls.Children.Add(play);
        controls.Children.Add(Small("\uE893", NowPlaying.Next, "Próxima", 11));
        var body = new Grid();
        body.Children.Add(top);
        void Update()
        {
            bool has = NowPlaying.HasSession;
            title.Text = has ? NowPlaying.Title : "Nada tocando";
            artist.Text = has ? NowPlaying.Artist : "Toque algo no Spotify, YouTube…";
            art.Background = NowPlaying.Art is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : IslandBrushes.Faint;
            ((TextBlock)play.Child).Text = NowPlaying.Playing ? IslandGlyphs.Pause : ExtraGlyphs.Play;
            controls.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }
        // Controls under the texts, to the right of the cover.
        var stack = new DockPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        controls.Margin = new Thickness(0, 0, -8, -8);
        stack.Children.Add(controls);
        stack.Children.Add(body);
        Update();
        While(() => NowPlaying.Changed += Update, () => NowPlaying.Changed -= Update);
        return Card(IslandGlyphs.Music, "Música", 1, stack);
    }

    private Border Calendar()
    {
        // The weekday in red like macOS, or in the accent when one is chosen.
        bool white = string.Equals(Island.Config?.IslandAccent ?? "#FFFFFF", "#FFFFFF", StringComparison.OrdinalIgnoreCase);
        var weekday = Line(12, true, white ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : IslandBrushes.Accent);
        var day = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 34, Margin = new Thickness(0, -4, 0, -6) };
        var month = Line(11.5, false, IslandBrushes.Dim);
        var s = new StackPanel();
        s.Children.Add(weekday);
        s.Children.Add(day);
        s.Children.Add(month);
        void Update()
        {
            var now = DateTime.Now;
            var c = System.Globalization.CultureInfo.CurrentCulture;
            string wd = now.ToString("dddd", c);
            weekday.Text = char.ToUpper(wd[0]) + wd[1..];
            day.Text = now.Day.ToString();
            string m = now.ToString("MMMM 'de' yyyy", c);
            month.Text = char.ToUpper(m[0]) + m[1..];
        }
        Update();
        // Midnight: a minute timer is enough for a date.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        t.Tick += (_, _) => Update();
        While(t.Start, t.Stop);
        return Card(IslandGlyphs.Calendar, "Calendário", 2, s);
    }

    private const double TrackWidth = CardW - 28 - 30 - 34 - 8;

    private Border Stats()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        var bars = new Border[3];
        var values = new TextBlock[3];
        string[] names = { "CPU", "RAM", "Disco" };
        for (int i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            var n = Line(10.5, false, IslandBrushes.Dim);
            n.Text = names[i];
            Grid.SetRow(n, i);
            grid.Children.Add(n);
            var track = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = IslandBrushes.Faint });
            bars[i] = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = IslandBrushes.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(bars[i]);
            Grid.SetRow(track, i);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);
            values[i] = Line(10.5);
            values[i].HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(values[i], i);
            Grid.SetColumn(values[i], 2);
            grid.Children.Add(values[i]);
        }
        var net = Line(10.5, false, IslandBrushes.Dim);
        net.Margin = new Thickness(0, 2, 0, 0);
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        Grid.SetRow(net, 3);
        Grid.SetColumnSpan(net, 3);
        grid.Children.Add(net);
        void Update()
        {
            double[] v = { SystemStats.Cpu, SystemStats.Memory, SystemStats.Disk };
            for (int i = 0; i < 3; i++)
            {
                double w = TrackWidth;
                bars[i].Width = Math.Max(4, w * v[i]);
                values[i].Text = $"{v[i] * 100:0}%";
            }
            grid.ToolTip = $"Memória: {SystemStats.MemoryUsed / 1e9:0.0} de {SystemStats.MemoryTotal / 1e9:0.0} GB\nDisco do sistema: {SystemStats.DiskText}";
            net.Text = $"↓ {DownloadWatch.Rate(SystemStats.Down)}   ↑ {DownloadWatch.Rate(SystemStats.Up)}";
        }
        While(() => SystemStats.Subscribe(Update), () => SystemStats.Unsubscribe(Update));
        return Card("\uE9D9", "Sistema", null, grid);
    }

    private Border TimerCard()
    {
        var host = new ContentControl();
        var big = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 28, VerticalAlignment = VerticalAlignment.Center };
        bool? shown = null;
        void Update()
        {
            bool active = IslandTimer.Active;
            if (shown != active)
            {
                shown = active;
                if (active)
                {
                    var row = new DockPanel();
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    buttons.Children.Add(Small(IslandTimer.Paused ? ExtraGlyphs.Play : ExtraGlyphs.Pause, () => { IslandTimer.TogglePause(); shown = null; Update(); }, "Pausar/continuar"));
                    buttons.Children.Add(Small(ExtraGlyphs.Cancel, IslandTimer.Cancel, "Cancelar", 11));
                    DockPanel.SetDock(buttons, Dock.Right);
                    row.Children.Add(buttons);
                    row.Children.Add(big);
                    host.Content = row;
                }
                else
                {
                    var quick = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 4, -3, 0), VerticalAlignment = VerticalAlignment.Top };
                    foreach (var m in new[] { 5, 10, 25 })
                    {
                        var b = IslandBrushes.Button(new TextBlock { Text = $"{m} min", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }, () => IslandTimer.Start(TimeSpan.FromMinutes(m)), null, 11, new Thickness(0, 7, 0, 8));
                        b.Margin = new Thickness(3);
                        quick.Children.Add(b);
                    }
                    host.Content = quick;
                }
            }
            big.Text = IslandTimer.Format(IslandTimer.Left);
            big.Opacity = IslandTimer.Paused ? 0.5 : 1;
        }
        Update();
        While(() => IslandTimer.Changed += Update, () => IslandTimer.Changed -= Update);
        return Card(ExtraGlyphs.Timer, "Timer", 7, host);
    }

    private Border Downloads()
    {
        var name = Line(12.5, true);
        var detail = Line(11.5, false, IslandBrushes.Dim);
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(name);
        s.Children.Add(detail);
        string? reveal = null;
        s.MouseLeftButtonUp += (_, e) => { if (reveal != null) { e.Handled = true; Shell.Reveal(reveal); } };
        void Update()
        {
            var active = DownloadWatch.Active.ToList();
            if (active.Count > 0)
            {
                name.Text = active.Count == 1 ? active[0].name : $"{active.Count} downloads";
                detail.Text = $"{DownloadWatch.Size(DownloadWatch.Bytes)} · {DownloadWatch.Rate(DownloadWatch.Speed)}";
                reveal = null;
            }
            else if (DownloadWatch.Recent.Count > 0)
            {
                reveal = DownloadWatch.Recent[0];
                name.Text = Path.GetFileName(reveal);
                detail.Text = "Concluído · clique para ver na pasta";
            }
            else
            {
                name.Text = "Nenhum download";
                detail.Text = "Chrome, Edge e Firefox aparecem aqui";
                reveal = null;
            }
            s.Cursor = reveal != null ? Cursors.Hand : null;
        }
        Update();
        While(() => DownloadWatch.Changed += Update, () => DownloadWatch.Changed -= Update);
        return Card(ExtraGlyphs.Download, "Downloads", null, s);
    }

    private Border Clipboard(Action dismiss)
    {
        var s = new StackPanel();
        void Update()
        {
            s.Children.Clear();
            foreach (var e in ClipboardHistory.Items.Take(2))
            {
                string text = e.Files != null ? string.Join(", ", e.Files.Select(f => Path.GetFileName(f.TrimEnd('\\'))))
                    : e.Image != null ? "Imagem" : string.Join(" ", (e.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var row = new Border { Child = Line(12), Padding = new Thickness(8, 4, 8, 5), Margin = new Thickness(-8, 0, -8, 1), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Copiar de novo" };
                ((TextBlock)row.Child).Text = text;
                row.MouseEnter += (_, _) => row.Background = IslandBrushes.Card;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, ev) =>
                {
                    ev.Handled = true;
                    ClipboardHistory.Copy(e);
                    dismiss();
                    Island.Notify(new StatusActivity(ExtraGlyphs.Done, "Copiado", "Cole com Ctrl+V", true));
                };
                s.Children.Add(row);
            }
            if (s.Children.Count == 0) { var t = Line(11.5, false, IslandBrushes.Dim); t.Text = "O que você copiar aparece aqui"; s.Children.Add(t); }
        }
        Update();
        While(() => ClipboardHistory.Changed += Update, () => ClipboardHistory.Changed -= Update);
        return Card(ExtraGlyphs.Clipboard, "Copiados", 6, s);
    }

    #endregion
}
