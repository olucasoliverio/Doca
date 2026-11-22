using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Doca;

/// <summary>
/// Volume per app: icon, name, slider, mute and a thin peak meter for each app with sound on the default
/// output. Used by the Control Center (island's Controles tab) and the bar's sound panel. Refreshes only while
/// loaded: the app list once a second, the meters ~15 times a second.
/// </summary>
internal sealed class AppMixer : StackPanel
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static int shown;
    private readonly DispatcherTimer meters = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer apps = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Row> rows = new();
    private readonly TextBlock caption;

    private sealed class Row
    {
        public required Grid Root;
        public required Slider Slider;
        public required TextBlock Mute;
        public required Border Meter;
        public required double MeterWidth;
        public bool Muted;
    }

    public AppMixer(string? title = "Apps")
    {
        caption = new TextBlock { Text = title ?? "", FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 10, 0, 2), Visibility = title == null ? Visibility.Collapsed : Visibility.Visible };
        meters.Tick += (_, _) => UpdateMeters();
        apps.Tick += (_, _) => Fill();
        Loaded += (_, _) => { shown++; Fill(); apps.Start(); meters.Start(); };
        Unloaded += (_, _) =>
        {
            apps.Stop();
            meters.Stop();
            if (--shown <= 0) { shown = 0; AppAudio.Drop(); }
        };
    }

    private void Fill()
    {
        var list = AppAudio.List();
        // Same apps as before: update in place (a rebuild would drop a slider being dragged).
        if (list.Select(a => a.Key).SequenceEqual(rows.Keys))
        {
            foreach (var a in list) Update(rows[a.Key], a);
            return;
        }
        Children.Clear();
        rows.Clear();
        if (list.Count == 0) return;
        Children.Add(caption);
        foreach (var a in list)
        {
            var row = Build(a);
            rows[a.Key] = row;
            Update(row, a);
            Children.Add(row.Root);
        }
    }

    private static void Update(Row r, AppSound a)
    {
        // Leave it alone while the user drags or scrolls it.
        if (!r.Slider.IsMouseCaptureWithin && !r.Slider.IsMouseOver) r.Slider.Value = Math.Round(a.Volume * 100);
        r.Muted = a.Muted;
        r.Mute.Text = a.Muted ? "" : "";
        r.Mute.Opacity = a.Muted ? 1 : 0.6;
        r.Root.Opacity = a.Muted ? 0.6 : 1;
    }

    private Row Build(AppSound a)
    {
        // One line per app (icon · slider · mute), name in the icon's tooltip: the Controles tab is already tall.
        var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

        var iconSlot = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var fallback = new TextBlock { Text = a.System ? "" : "", FontFamily = new FontFamily(IconFont), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        iconSlot.Children.Add(fallback);
        if (a.ExePath != null)
        {
            var img = new Image { Width = 18, Height = 18 };
            iconSlot.Children.Add(img);
            Shell.RequestIcon(a.ExePath, s => { if (s == null) return; img.Source = s; fallback.Visibility = Visibility.Hidden; }, 32);
        }
        iconSlot.ToolTip = a.Name;
        iconSlot.Background = Brushes.Transparent;
        g.Children.Add(iconSlot);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true, SmallChange = 2, LargeChange = 10, Margin = new Thickness(0, 0, 0, -4), ToolTip = a.Name };
        string key = a.Key;
        slider.ValueChanged += (_, e) => { if (slider.IsMouseCaptureWithin || slider.IsMouseOver || slider.IsKeyboardFocusWithin) AppAudio.SetVolume(key, (float)(e.NewValue / 100)); };
        // The wheel nudges this app, not the panel's tabs.
        slider.MouseWheel += (_, e) => { e.Handled = true; slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 4 : -4), 0, 100); };
        mid.Children.Add(slider);
        // Peak meter: a hairline under the slider, in the accent color.
        var track = new Grid { Height = 2, Margin = new Thickness(0, 0, 0, 2) };
        var meter = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Opacity = 0.8 };
        meter.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        track.Children.Add(meter);
        mid.Children.Add(track);
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var mute = new TextBlock { FontFamily = new FontFamily(IconFont), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(4) };
        Grid.SetColumn(mute, 2);
        g.Children.Add(mute);

        var row = new Row { Root = g, Slider = slider, Mute = mute, Meter = meter, MeterWidth = 0 };
        mute.ToolTip = a.System ? "Silenciar os sons do sistema" : $"Silenciar {a.Name}";
        mute.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            row.Muted = !row.Muted;
            AppAudio.SetMute(key, row.Muted);
            Update(row, a with { Muted = row.Muted, Volume = (float)(slider.Value / 100) });
        };
        track.SizeChanged += (_, _) => row.MeterWidth = track.ActualWidth;
        return row;
    }

    private void UpdateMeters()
    {
        if (rows.Count == 0) return;
        var peaks = AppAudio.Peaks();
        foreach (var (key, r) in rows)
        {
            float p = r.Muted ? 0 : peaks.GetValueOrDefault(key);
            // Scaled by the app's own volume, so the hairline follows what its slider lets through.
            double w = Math.Round(r.MeterWidth * Math.Min(1, p * r.Slider.Value / 100));
            if (Math.Abs(r.Meter.Width - w) >= 1) r.Meter.Width = w;
        }
    }
}

/// <summary>An app playing (or able to play) sound on the default output: its sessions grouped by executable.</summary>
internal sealed record AppSound(string Key, string Name, string? ExePath, bool System, float Volume, bool Muted);

/// <summary>
/// Per-app volume (what EarTrumpet does): the audio sessions of the default output device.
/// <para>
/// Read on demand: the mixer refreshes while it is visible (sessions 1x/s, peak meters ~15x/s) instead of
/// registering IAudioSessionNotification/IAudioSessionEvents, whose COM callbacks arrive on MTA threads, only
/// start after a first enumeration, and would run all day to serve a panel that is closed nearly all the time.
/// The sessions of the last refresh are kept (a snapshot) so meters and slider drags don't re-enumerate;
/// Drop releases them when no mixer is shown.
/// </para>
/// One app can own several sessions (a browser's tabs, a game's channels): they are shown and set together.
/// </summary>
internal static class AppAudio
{
    private sealed class Entry
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public string? ExePath { get; init; }
        public bool System { get; init; }
        /// <summary>Session RCWs (IAudioSessionControl2 / ISimpleAudioVolume / IAudioMeterInformation: one object).</summary>
        public List<object> Sessions { get; } = new();
    }

    private static readonly uint ownPid = (uint)Environment.ProcessId;
    private static List<Entry>? snapshot;
    private static long snapshotAt;

    /// <summary>Re-enumerates and returns the apps with a live session (system sounds last).</summary>
    public static List<AppSound> List() => Current(force: true).Select(e =>
    {
        float vol = 0;
        bool muted = e.Sessions.Count > 0;
        foreach (var s in e.Sessions)
        {
            var v = (ISimpleAudioVolume)s;
            if (v.GetMasterVolume(out float l) == 0) vol = Math.Max(vol, l);
            if (v.GetMute(out bool mu) == 0 && !mu) muted = false;
        }
        return new AppSound(e.Key, e.Name, e.ExePath, e.System, vol, muted);
    }).ToList();

    /// <summary>Peak level per app (meters), from the last snapshot.</summary>
    public static Dictionary<string, float> Peaks()
    {
        var peaks = new Dictionary<string, float>();
        foreach (var e in Current(force: false))
        {
            float peak = 0;
            foreach (var s in e.Sessions)
                if (s is IAudioMeterInformation m && m.GetPeakValue(out float p) == 0) peak = Math.Max(peak, p);
            peaks[e.Key] = peak;
        }
        return peaks;
    }

    public static void SetVolume(string key, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        Apply(key, v =>
        {
            var ctx = Guid.Empty;
            v.SetMasterVolume(level, ref ctx);
            if (level > 0) v.SetMute(false, ref ctx);
        });
    }

    public static void SetMute(string key, bool mute) => Apply(key, v => { var ctx = Guid.Empty; v.SetMute(mute, ref ctx); });

    /// <summary>
    /// Changes an app's volume by delta (Dock wheel). Returns the new level, or null when it has no sessions.
    /// Store apps hosted by ApplicationFrameHost have an "aumid:" key on the Dock: those match by name.
    /// </summary>
    public static float? Nudge(string key, string name, float delta)
    {
        var all = Current(force: false);
        var e = all.FirstOrDefault(x => x.Key == key) ?? all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (e == null || e.Sessions.Count == 0) return null;
        key = e.Key;
        float cur = 0;
        foreach (var s in e.Sessions) if (((ISimpleAudioVolume)s).GetMasterVolume(out float l) == 0) cur = Math.Max(cur, l);
        float now = Math.Clamp(cur + delta, 0f, 1f);
        Apply(key, v =>
        {
            var ctx = Guid.Empty;
            v.SetMasterVolume(now, ref ctx);
            if (delta > 0) v.SetMute(false, ref ctx);
        });
        return now;
    }

    /// <summary>Releases the kept sessions (the last mixer closed).</summary>
    public static void Drop()
    {
        if (snapshot != null) Release(snapshot);
        snapshot = null;
    }

    private static void Apply(string key, Action<ISimpleAudioVolume> act)
    {
        foreach (var e in Current(force: false).Where(x => x.Key == key))
            foreach (var s in e.Sessions)
            {
                try { act((ISimpleAudioVolume)s); }
                catch (Exception ex) { App.Log(ex); }
            }
    }

    /// <summary>The kept sessions, reloaded when forced or older than a second (a new app started playing).</summary>
    private static List<Entry> Current(bool force)
    {
        long now = Environment.TickCount64;
        if (snapshot != null && !force && now - snapshotAt < 1000) return snapshot;
        var fresh = Load();
        if (snapshot != null) Release(snapshot);
        snapshot = fresh;
        snapshotAt = now;
        return fresh;
    }

    private static void Release(List<Entry> entries)
    {
        foreach (var e in entries)
            foreach (var s in e.Sessions)
                try { Marshal.ReleaseComObject(s); } catch { }
    }

    private static List<Entry> Load()
    {
        var byKey = new Dictionary<string, Entry>();
        try
        {
            // Created from the CLSID: a second [ComImport] class with the same CLSID as Audio's makes the runtime
            // hand back Audio's RCW type, and the cast fails ("MMDeviceEnumerator to MMDeviceEnumerator").
            var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) < 0) return new();
            var iid = typeof(IAudioSessionManager2).GUID;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) < 0) return new();
            var mgr = (IAudioSessionManager2)o;
            if (mgr.GetSessionEnumerator(out var list) < 0) return new();
            list.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (list.GetSession(i, out object ctl) < 0) continue;
                bool kept = false;
                try
                {
                    var c2 = (IAudioSessionControl2)ctl;
                    if (c2.GetState(out int state) < 0 || state == 2 /* expired */) continue;
                    c2.GetProcessId(out uint pid);
                    bool system = c2.IsSystemSoundsSession() == 0; // S_OK = yes
                    if (pid == ownPid && !system) continue;
                    string? exe = system ? null : Native.ProcessPath(pid);
                    if (!system && exe == null) continue; // process gone
                    string key = system ? "system" : Shell.AppKey(exe!);
                    if (!byKey.TryGetValue(key, out var entry))
                        byKey[key] = entry = new Entry { Key = key, Name = system ? "Sons do sistema" : Shell.AppName(exe!), ExePath = exe, System = system };
                    entry.Sessions.Add(ctl);
                    kept = true;
                }
                catch (Exception ex) { App.Log(ex); }
                finally { if (!kept) Marshal.ReleaseComObject(ctl); }
            }
            Marshal.ReleaseComObject(list);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(dev);
        }
        catch (Exception ex) { App.Log(ex); }
        return byKey.Values.OrderBy(e => e.System).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    #region Core Audio session interfaces

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator list);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    // IAudioSessionControl's methods are repeated: COM interop needs the whole vtable of a derived interface.
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    #endregion
}
