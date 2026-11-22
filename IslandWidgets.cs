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
    /// <summary>Busiest GPU engine, 0–1 (like Task Manager), or null when there are no GPU counters.</summary>
    public static double? Gpu { get; private set; }
    /// <summary>°C, or null when the driver doesn't report it.</summary>
    public static double? GpuTemp { get; private set; }
    public static uint GpuFan { get; private set; }
    /// <summary>Dedicated video memory in use (bytes).</summary>
    public static double GpuMemory { get; private set; }
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
            GpuSensors.Sample();
            Gpu = GpuSensors.Usage;
            GpuTemp = GpuSensors.Temperature;
            GpuFan = GpuSensors.Fan;
            GpuMemory = GpuSensors.DedicatedUsed;
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
/// GPU usage, temperature and video memory without admin rights or vendor SDKs.
/// <para>
/// Usage and memory: the "GPU Engine" / "GPU Adapter Memory" performance counters (PDH), the ones Task Manager
/// reads. Utilization is per process and engine; summed per engine, the busiest engine is the GPU's usage (so a
/// video playing on the decode engine counts too, not only 3D). Temperature and fan: D3DKMTQueryAdapterInfo
/// with KMTQAITYPE_ADAPTERPERFDATA (WDDM 2.4+), what Task Manager shows as "GPU temperature"; ~0.7 ms.
/// CPU temperature has no such API: it needs a kernel driver and admin rights, so it is left out.
/// </para>
/// </summary>
internal static class GpuSensors
{
    public static double? Usage { get; private set; }
    public static double? Temperature { get; private set; }
    public static uint Fan { get; private set; }
    public static double DedicatedUsed { get; private set; }

    private static IntPtr query, engines, memory;
    private static bool pdhFailed;
    private static uint adapter;
    private static bool adapterSearched;

    public static void Sample()
    {
        SampleCounters();
        SampleAdapter();
    }

    private static void SampleCounters()
    {
        if (pdhFailed) return;
        if (query == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0
                || PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engines) != 0)
            {
                pdhFailed = true;
                return;
            }
            PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out memory);
            PdhCollectQueryData(query); // rate counters need two samples: the first one only primes them
            return;
        }
        if (PdhCollectQueryData(query) != 0) return;

        // Instance names: pid_1234_luid_0x…_0x…_phys_0_eng_3_engtype_VideoDecode. Sum over processes per engine.
        var perEngine = new Dictionary<string, double>();
        foreach (var (name, value) in Read(engines))
        {
            int at = name.IndexOf("luid_", StringComparison.Ordinal);
            string engine = at >= 0 ? name[at..] : name;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
        }
        Usage = perEngine.Count > 0 ? Math.Clamp(perEngine.Values.Max() / 100, 0, 1) : null;
        DedicatedUsed = memory != IntPtr.Zero ? Read(memory).Select(x => x.value).DefaultIfEmpty(0).Max() : 0;
    }

    private static List<(string name, double value)> Read(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                // 0 valid, 1 new data; anything else (a process that just appeared) has no rate yet.
                if (item.CStatus > 1) continue;
                list.Add((Marshal.PtrToStringUni(item.szName) ?? "", item.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>The adapter that reports a temperature (the discrete GPU; Microsoft Basic Render has none).</summary>
    private static void SampleAdapter()
    {
        if (!adapterSearched)
        {
            adapterSearched = true;
            var e = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
            int sz = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return;
                uint best = 0, bestTemp = 0;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var a = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * sz);
                    if (Perf(a.hAdapter) is { Temperature: > 0 } p && p.Temperature > bestTemp) (best, bestTemp) = (a.hAdapter, p.Temperature);
                }
                // The handles stay open for the session (one per adapter): reopening every second costs more.
                adapter = best;
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }
        if (adapter == 0) { Temperature = null; return; }
        if (Perf(adapter) is { } d)
        {
            Temperature = d.Temperature > 0 ? d.Temperature / 10.0 : null; // deci-°C
            Fan = d.FanRPM;
        }
    }

    private static D3DKMT_ADAPTER_PERFDATA? Perf(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA(), buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public uint CStatus; public double doubleValue; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
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
                "Baterias" => cfg.IslandPeripherals ? Batteries() : null,
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
        FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = brush ?? IslandTheme.InkBrush,
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
        bool plain = IslandTheme.Accent == IslandTheme.Ink;
        var weekday = Line(12, true, plain ? IslandBrushes.Red : IslandBrushes.Accent);
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var bars = new Border[3];
        var values = new TextBlock[3];
        var temps = new TextBlock[3];
        // CPU temperature needs a kernel driver (admin): only the GPU's is shown.
        string[] names = { "CPU", "GPU", "RAM" };
        for (int i = 0; i < 3; i++)
        {
            temps[i] = Line(10.5, false, IslandBrushes.Dim);
            temps[i].HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(temps[i], i);
            Grid.SetColumn(temps[i], 3);
            grid.Children.Add(temps[i]);
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
        Grid.SetColumnSpan(net, 4);
        grid.Children.Add(net);
        void Update()
        {
            double?[] v = { SystemStats.Cpu, SystemStats.Gpu, SystemStats.Memory };
            for (int i = 0; i < 3; i++)
            {
                double w = TrackWidth + 2 - 28; // value column 2 px narrower, plus the 28 px temperature column
                bars[i].Width = Math.Max(4, w * (v[i] ?? 0));
                values[i].Text = v[i] is double d ? $"{d * 100:0}%" : "–";
            }
            temps[1].Text = SystemStats.GpuTemp is double t ? $"{t:0}°" : "";
            // Hotter GPU, warmer color: orange from 80 °C, red from 90 °C.
            temps[1].Foreground = SystemStats.GpuTemp switch { >= 90 => IslandBrushes.Red, >= 80 => IslandBrushes.Orange, _ => IslandBrushes.Dim };
            var tip = $"Memória: {SystemStats.MemoryUsed / 1e9:0.0} de {SystemStats.MemoryTotal / 1e9:0.0} GB";
            if (SystemStats.GpuMemory > 0) tip += $"\nMemória de vídeo em uso: {SystemStats.GpuMemory / 1e9:0.0} GB";
            if (SystemStats.GpuTemp != null) tip += $"\nGPU: {SystemStats.GpuTemp:0} °C · ventoinha {(SystemStats.GpuFan > 0 ? $"{SystemStats.GpuFan} rpm" : "parada")}";
            grid.ToolTip = tip;
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

    /// <summary>Mouse and headset batteries: glyph, bar, percent (green while charging, orange when low).</summary>
    private Border Batteries()
    {
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        void Update()
        {
            rows.Children.Clear();
            foreach (var p in Peripherals.Items.OrderBy(x => x.Name))
            {
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 4), ToolTip = p.Asleep ? $"{p.Name}: desligado ou dormindo (último valor)" : p.Charging ? $"{p.Name}: carregando" : p.Name };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                var g = IslandBrushes.Glyph(p.Glyph, 14);
                g.HorizontalAlignment = HorizontalAlignment.Left;
                grid.Children.Add(g);
                double level = (p.Level ?? 0) / 100.0;
                var color = p.Charging ? IslandBrushes.Green : p.Level <= 20 ? IslandBrushes.Orange : IslandBrushes.Accent;
                var track = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                track.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = IslandBrushes.Faint });
                track.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = color, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(6, (CardW - 28 - 24 - 40 - 8) * level) });
                Grid.SetColumn(track, 1);
                grid.Children.Add(track);
                var pct = Line(12, true);
                pct.Text = p.Level is int l ? $"{l}%" : "—";
                pct.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(pct, 2);
                grid.Children.Add(pct);
                // Asleep, or quiet for a while (the mouse only reports while in use): last known value, dimmed.
                bool stale = p.Asleep || DateTime.Now - p.Seen > TimeSpan.FromMinutes(3);
                grid.Opacity = stale ? 0.45 : 1;
                if (stale && !p.Asleep) grid.ToolTip = $"{p.Name}: parado, último valor às {p.Seen:HH:mm}";
                rows.Children.Add(grid);
            }
            if (rows.Children.Count == 0)
            {
                var t = Line(11.5, false, IslandBrushes.Dim);
                t.Text = "Nenhum mouse ou headset sem fio encontrado";
                t.TextWrapping = TextWrapping.Wrap;
                rows.Children.Add(t);
            }
        }
        Update();
        While(() => Peripherals.Changed += Update, () => Peripherals.Changed -= Update);
        return Card("", "Baterias", null, rows);
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
