using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace Doca;

/// <summary>
/// Brightness of external monitors over DDC/CI (VCP code 0x10), the same channel as the monitor's own
/// buttons. Each read or write takes ~50 ms on the I2C bus, so writes run on a background thread and
/// only the latest slider value is sent.
/// </summary>
internal sealed class DdcMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hmon, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hmon, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    private const byte VcpBrightness = 0x10;

    private readonly PHYSICAL_MONITOR[] handles;
    private readonly IntPtr physical;
    private readonly object gate = new();
    private int pending = -1;
    private bool writing;

    public string Name { get; }
    public uint Max { get; }
    public int Level { get; private set; } // 0–100

    private DdcMonitor(PHYSICAL_MONITOR[] handles, IntPtr physical, string name, uint current, uint max)
    {
        this.handles = handles;
        this.physical = physical;
        Name = name;
        Max = max;
        Level = (int)Math.Round(current * 100.0 / max);
    }

    /// <summary>Monitors that answered a brightness read, left to right. Blocking (~50 ms each): call off the UI thread.</summary>
    public static List<DdcMonitor> Open()
    {
        var list = new List<(int left, DdcMonitor m)>();
        foreach (var mon in Native.Monitors())
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon.Handle, out uint n) || n == 0) continue;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(mon.Handle, n, arr)) continue;
            // One HMONITOR is one physical screen unless the desktop is cloned; take the first that answers.
            var ok = arr.FirstOrDefault(p => GetVCPFeatureAndVCPFeatureReply(p.Handle, VcpBrightness, IntPtr.Zero, out _, out uint mx) && mx > 0);
            if (ok.Handle == IntPtr.Zero || !GetVCPFeatureAndVCPFeatureReply(ok.Handle, VcpBrightness, IntPtr.Zero, out uint cur, out uint max))
            {
                DestroyPhysicalMonitors(n, arr);
                continue;
            }
            list.Add((mon.Bounds.Left, new DdcMonitor(arr, ok.Handle, FriendlyName(mon.Handle) ?? (mon.Primary ? "Tela principal" : "Tela"), cur, max)));
        }
        return list.OrderBy(x => x.left).Select(x => x.m).ToList();
    }

    /// <summary>
    /// The model name from the monitor's EDID ("LG ULTRAGEAR"); Windows itself only calls them
    /// "Generic PnP Monitor". MONITOR\GSM5BBF\… → HKLM\…\Enum\DISPLAY\GSM5BBF\*\Device Parameters\EDID.
    /// </summary>
    private static string? FriendlyName(IntPtr hmon)
    {
        try
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hmon, ref mi)) return null;
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(mi.Device, 0, ref dd, 0)) return null;
            var parts = dd.DeviceID.Split('\\');
            if (parts.Length < 2) return null;
            using var model = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1]);
            if (model == null) return null;
            foreach (var inst in model.GetSubKeyNames())
            {
                using var p = model.OpenSubKey(inst + @"\Device Parameters");
                if (p?.GetValue("EDID") is not byte[] edid || edid.Length < 128) continue;
                // Four 18-byte descriptors from offset 54; tag 0xFC is the monitor name.
                for (int o = 54; o <= 108; o += 18)
                    if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == 0xFC)
                        return Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Queues a level (0–100); slider drags collapse into the last value.</summary>
    public void Set(int level)
    {
        Level = level;
        lock (gate)
        {
            pending = level;
            if (writing) return;
            writing = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int v;
                lock (gate)
                {
                    if (pending < 0) { writing = false; return; }
                    v = pending;
                    pending = -1;
                }
                SetVCPFeature(physical, VcpBrightness, (uint)Math.Round(v * Max / 100.0));
            }
        });
    }

    public void Dispose()
    {
        // Let a write in flight finish before the handle goes away.
        Task.Run(() =>
        {
            for (int i = 0; i < 40; i++) { lock (gate) if (!writing) break; Thread.Sleep(25); }
            DestroyPhysicalMonitors((uint)handles.Length, handles);
        });
    }
}

/// <summary>Light/dark mode for apps and Windows, like the Settings → Personalization → Colors switch.</summary>
internal static class Theme
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    public static bool Dark
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("AppsUseLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
            }
            // The broadcast Settings sends: open apps (Explorer, Chrome, Store apps) repaint in the new mode.
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 0x2 /* SMTO_ABORTIFHUNG */, 200, out _);
        }
    }
}

/// <summary>
/// macOS Control Center: Wi-Fi, Bluetooth, dark mode and projection toggles, display brightness
/// per monitor, volume and what is playing, in one panel.
/// </summary>
internal sealed class ControlCenterFlyout : Flyout
{
    private static readonly Brush tileOff = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush circleOff = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush panel = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private readonly DocaConfig cfg;
    private readonly StackPanel displays = new();
    private readonly List<DdcMonitor> monitors = new();
    private readonly Slider volume = new() { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock volumeGlyph;
    private readonly TextBlock outputName = new() { FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
    private (Border tile, Border circle, TextBlock detail) wifi, bluetooth, dark;
    private bool updatingVolume;

    public ControlCenterFlyout(DocaConfig cfg, bool embedded = false) : base(cfg, 340)
    {
        this.cfg = cfg;
        var tiles = new UniformGrid { Columns = 2 };
        wifi = Tile("", "Wi-Fi", ToggleWifi, () => Flyout.Toggle("wifi", Anchor!, () => new WifiFlyout(cfg)));
        bluetooth = Tile("", "Bluetooth", ToggleBluetooth, () => Flyout.Toggle("bluetooth", Anchor!, () => new BluetoothFlyout(cfg)));
        dark = Tile("", "Modo escuro", ToggleDark, null);
        var project = Tile("", "Projetar", () => { CloseFlyout(); Shell.WinKey(0x50); }, null);
        project.detail.Text = "Segunda tela";
        foreach (var t in new[] { wifi.tile, bluetooth.tile, dark.tile, project.tile }) tiles.Children.Add(t);
        Body.Children.Add(tiles);

        Body.Children.Add(Section("Tela", displays));
        displays.Children.Add(new TextBlock { Text = "Lendo os monitores…", FontSize = 12, Opacity = 0.55 });

        volumeGlyph = Glyph("", 16);
        volumeGlyph.Width = 22;
        volumeGlyph.Cursor = System.Windows.Input.Cursors.Hand;
        volumeGlyph.ToolTip = "Silenciar";
        volumeGlyph.MouseLeftButtonUp += (_, _) => { Audio.ToggleMute(); RefreshVolume(); };
        var sound = new StackPanel();
        sound.Children.Add(SliderRow(volumeGlyph, volume));
        sound.Children.Add(outputName);
        outputName.Margin = new Thickness(30, 0, 0, 0);
        outputName.Cursor = System.Windows.Input.Cursors.Hand;
        outputName.MouseLeftButtonUp += (_, _) => Flyout.Toggle("sound", Anchor!, () => new SoundFlyout(cfg));
        Body.Children.Add(Section("Som", sound));
        volume.ValueChanged += (_, e) => { if (!updatingVolume) { Audio.SetLevel((float)(e.NewValue / 100)); UpdateVolumeGlyph(); } };

        if (NowPlaying.HasSession && !embedded) Body.Children.Add(Section(null, new PlayerView(compact: true)));

        Body.Children.Add(Divider());
        Body.Children.Add(Footer(Link("Configurações…", () => Shell.Open("ms-settings:"))));

        RefreshVolume();
        dark.circle.Background = Theme.Dark ? Accent : circleOff;
        dark.detail.Text = Theme.Dark ? "Ativado" : "Desativado";
        // Body, not window, events: the island hosts this content without ever showing the window.
        Body.Loaded += async (_, _) =>
        {
            await Bluetooth.LoadRadiosAsync();
            RefreshRadios();
            var found = await Task.Run(DdcMonitor.Open);
            if (!Body.IsLoaded) { foreach (var m in found) m.Dispose(); return; }
            monitors.AddRange(found);
            FillDisplays();
        };
        void Release() { foreach (var m in monitors) m.Dispose(); monitors.Clear(); }
        Body.Unloaded += (_, _) => Release();
        Closed += (_, _) => Release();
    }

    private Border Section(string? title, UIElement content)
    {
        var s = new StackPanel();
        if (title != null) s.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        s.Children.Add(content);
        return new Border { Child = s, Background = panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(2, 8, 2, 0) };
    }

    private static Grid SliderRow(UIElement icon, Slider slider)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(icon);
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        return g;
    }

    /// <summary>Round toggle on the left (like macOS); the label opens the detailed panel when there is one.</summary>
    private (Border tile, Border circle, TextBlock detail) Tile(string glyph, string title, Action toggle, Action? more)
    {
        var circle = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = circleOff, Cursor = System.Windows.Input.Cursors.Hand,
            Child = Glyph(glyph, 15),
        };
        ((TextBlock)circle.Child).HorizontalAlignment = HorizontalAlignment.Center;
        circle.MouseLeftButtonUp += (_, e) => { e.Handled = true; toggle(); };

        var detail = new TextBlock { FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(detail);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(circle);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);

        var tile = new Border { Child = g, Background = tileOff, CornerRadius = new CornerRadius(12), Padding = new Thickness(9), Margin = new Thickness(2) };
        tile.MouseLeftButtonUp += (_, _) => (more ?? toggle)();
        tile.MouseEnter += (_, _) => tile.Background = RowHover;
        tile.MouseLeave += (_, _) => tile.Background = tileOff;
        return (tile, circle, detail);
    }

    private void RefreshRadios()
    {
        bool wifiOn = Bluetooth.WifiOn;
        string? ssid = wifiOn ? Wlan.Networks().FirstOrDefault(n => n.Connected)?.Ssid : null;
        wifi.circle.Background = wifiOn ? Accent : circleOff;
        wifi.detail.Text = !Bluetooth.HasWifiRadio ? "Sem adaptador" : !wifiOn ? "Desligado" : ssid ?? "Não conectado";
        bluetooth.circle.Background = Bluetooth.IsOn ? Accent : circleOff;
        bluetooth.detail.Text = !Bluetooth.HasAdapter ? "Sem adaptador" : Bluetooth.IsOn ? "Ligado" : "Desligado";
    }

    private async void ToggleWifi()
    {
        if (!Bluetooth.HasWifiRadio) return;
        wifi.detail.Text = Bluetooth.WifiOn ? "Desligando…" : "Ligando…";
        if (!await Bluetooth.SetWifiOnAsync(!Bluetooth.WifiOn)) wifi.detail.Text = "Não permitido";
        else RefreshRadios();
    }

    private async void ToggleBluetooth()
    {
        if (!Bluetooth.HasAdapter) return;
        bluetooth.detail.Text = Bluetooth.IsOn ? "Desligando…" : "Ligando…";
        if (!await Bluetooth.SetOnAsync(!Bluetooth.IsOn)) bluetooth.detail.Text = "Não permitido";
        else RefreshRadios();
    }

    private void ToggleDark()
    {
        bool on = !Theme.Dark;
        Theme.Dark = on;
        dark.circle.Background = on ? Accent : circleOff;
        dark.detail.Text = on ? "Ativado" : "Desativado";
    }

    private void FillDisplays()
    {
        displays.Children.Clear();
        if (monitors.Count == 0)
        {
            displays.Children.Add(new TextBlock { Text = "Os monitores não aceitam controle de brilho (DDC/CI). Ele costuma ser ligado no menu do próprio monitor.", FontSize = 12, Opacity = 0.55, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var m in monitors)
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = m.Level, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, e) =>
            {
                m.Set((int)Math.Round(e.NewValue));
                Island.Notify(new LevelActivity(IslandGlyphs.Brightness, e.NewValue / 100, false));
            };
            var sun = Glyph("", 16);
            if (monitors.Count > 1)
                displays.Children.Add(new TextBlock { Text = m.Name, FontSize = 11, Opacity = 0.55, Margin = new Thickness(30, displays.Children.Count > 0 ? 6 : 0, 0, 0) });
            displays.Children.Add(SliderRow(sun, slider));
        }
    }

    private void RefreshVolume()
    {
        updatingVolume = true;
        if (Audio.Get() is { } v) volume.Value = Math.Round(v.level * 100);
        updatingVolume = false;
        outputName.Text = (Audio.Outputs().FirstOrDefault(o => o.IsDefault)?.Name ?? "") + "  ›";
        UpdateVolumeGlyph();
    }

    private void UpdateVolumeGlyph()
    {
        var v = Audio.Get();
        bool muted = v == null || v.Value.muted || v.Value.level < 0.01f;
        volumeGlyph.Text = muted ? "" : "";
        volumeGlyph.ToolTip = muted ? "Ativar som" : "Silenciar";
    }
}
