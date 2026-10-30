using System.IO;
using System.Windows.Threading;

namespace Doca;

internal sealed class RunningApp
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public string? Aumid { get; init; }
    /// <summary>Top-level windows in z-order (topmost first).</summary>
    public List<IntPtr> Windows { get; } = new();
}

/// <summary>
/// Tracks app windows through WinEvent hooks (event driven, no polling).
/// Bursts of events are coalesced into a single refresh.
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private static readonly HashSet<string> ignoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly List<IntPtr> hooks = new();
    private readonly Native.WinEventProc proc;
    private readonly DispatcherTimer coalesce;
    private readonly Dictionary<uint, string?> pidPaths = new();
    private readonly uint ownPid = (uint)Environment.ProcessId;
    private readonly DispatcherTimer fullscreenCheck;
    private HashSet<IntPtr> fullscreenMonitors = new();

    public List<RunningApp> Apps { get; private set; } = new();
    public IntPtr Foreground { get; private set; }
    public RunningApp? ForegroundApp { get; private set; }

    public event Action? AppsChanged;
    public event Action? ForegroundChanged;
    public event Action? FullscreenChanged;

    public WindowTracker()
    {
        proc = OnWinEvent;
        coalesce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        coalesce.Tick += (_, _) => { coalesce.Stop(); Refresh(); };
        fullscreenCheck = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
        fullscreenCheck.Tick += (_, _) => { fullscreenCheck.Stop(); CheckFullscreen(); };
    }

    public void Start()
    {
        void Hook(uint min, uint max) =>
            hooks.Add(Native.SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS));

        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);
        Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE);
        Hook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED);
        Hook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE);

        Refresh();
        OnForeground(Native.GetForegroundWindow());
        CheckFullscreen();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;

        if (evt != Native.EVENT_OBJECT_DESTROY &&
            (Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_CHILD) != 0)
            return;

        // Any top-level move/resize/show/hide can start or end fullscreen (e.g. a Chrome video).
        if (!fullscreenCheck.IsEnabled) fullscreenCheck.Start();

        if (evt == Native.EVENT_OBJECT_LOCATIONCHANGE) return;
        if (evt == Native.EVENT_SYSTEM_FOREGROUND) { OnForeground(hwnd); return; }
        if (!coalesce.IsEnabled) coalesce.Start();
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (Native.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == ownPid) return;
        Foreground = hwnd;
        ForegroundApp = Describe(hwnd);
        ForegroundChanged?.Invoke();
        if (!coalesce.IsEnabled) coalesce.Start();
    }

    public bool IsFullscreen(IntPtr monitor) => fullscreenMonitors.Contains(monitor);

    /// <summary>
    /// Per monitor: walks windows top-down in z-order; the first real window touching a monitor decides.
    /// If it covers the whole monitor (Chrome video, games, F11) that monitor is fullscreen.
    /// Maximized windows never qualify because the menu bar's AppBar keeps them off the top strip.
    /// </summary>
    private void CheckFullscreen()
    {
        var pending = Native.Monitors().ToDictionary(m => m.Handle, m => m.Bounds);
        var result = new HashSet<IntPtr>();
        IntPtr fg = Native.GetForegroundWindow();

        Native.EnumWindows((h, _) =>
        {
            if (pending.Count == 0) return false;
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownPid) return true;
            long ex = Native.ExStyle(h);
            if ((ex & (Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return true;
            if (ignoredClasses.Contains(Native.ClassName(h))) return true;
            if (!Native.GetWindowRect(h, out var r) || r.Width <= 0 || r.Height <= 0) return true;

            bool app = IsAppWindow(h);
            bool candidate = app || h == fg || Native.GetWindowTextLength(h) > 0;
            foreach (var (mon, b) in pending.ToList())
            {
                bool covers = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
                if (covers && candidate) { result.Add(mon); pending.Remove(mon); }
                else if (app && r.Left < b.Right && r.Right > b.Left && r.Top < b.Bottom && r.Bottom > b.Top) pending.Remove(mon);
            }
            return true;
        }, IntPtr.Zero);

        if (result.SetEquals(fullscreenMonitors)) return;
        fullscreenMonitors = result;
        FullscreenChanged?.Invoke();
    }

    public void Refresh()
    {
        var map = new Dictionary<string, RunningApp>();
        var seen = new HashSet<uint>();
        Native.EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            seen.Add(pid);
            var info = Describe(h, pid);
            if (info == null) return true;
            if (!map.TryGetValue(info.Key, out var app)) map[info.Key] = app = info;
            app.Windows.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (var pid in pidPaths.Keys.Where(p => !seen.Contains(p)).ToList()) pidPaths.Remove(pid);

        // Keep previous ordering so apps don't jump around in the Dock.
        var ordered = new List<RunningApp>(map.Count);
        foreach (var old in Apps)
            if (map.Remove(old.Key, out var a)) ordered.Add(a);
        ordered.AddRange(map.Values);

        bool changed = ordered.Count != Apps.Count ||
                       ordered.Zip(Apps).Any(p => p.First.Key != p.Second.Key || !p.First.Windows.SequenceEqual(p.Second.Windows));
        Apps = ordered;
        if (changed) AppsChanged?.Invoke();
    }

    public static bool IsAppWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        long ex = Native.ExStyle(h);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        }
        if (Native.GetWindowTextLength(h) == 0) return false;
        if (Native.IsCloaked(h)) return false;
        return !ignoredClasses.Contains(Native.ClassName(h));
    }

    public RunningApp? Describe(IntPtr h, uint pid = 0)
    {
        if (pid == 0) Native.GetWindowThreadProcessId(h, out pid);
        if (pid == ownPid) return null;
        if (!pidPaths.TryGetValue(pid, out var path)) pidPaths[pid] = path = Native.ProcessPath(pid);
        if (path == null) return null;

        if (Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var aumid = Shell.WindowAumid(h);
            if (aumid == null) return null;
            return new RunningApp
            {
                Key = "aumid:" + aumid.ToLowerInvariant(),
                Aumid = aumid,
                Name = Shell.DisplayName(@"shell:AppsFolder\" + aumid) ?? Native.WindowTitle(h),
            };
        }
        return new RunningApp { Key = path.ToLowerInvariant(), ExePath = path, Name = Shell.AppName(path) };
    }

    public void Dispose()
    {
        foreach (var h in hooks) Native.UnhookWinEvent(h);
        hooks.Clear();
    }
}
