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
    private HashSet<IntPtr> known = new(); // app windows seen in the last refresh
    private IntPtr locationHook;
    private uint locationPid;

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

        Refresh();
        OnForeground(Native.GetForegroundWindow());
        CheckFullscreen();
    }

    /// <summary>
    /// Kept cheap on purpose: the system reports every tooltip, popup and child window. Only events
    /// about real app windows (or windows we already track) trigger work, and that work is coalesced.
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;

        switch (evt)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                OnForeground(hwnd);
                ScheduleFullscreenCheck();
                return;
            case Native.EVENT_OBJECT_LOCATIONCHANGE:
                // Hooked for the foreground app only: entering/leaving fullscreen (F11, a video) resizes it.
                if ((Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_CHILD) == 0) ScheduleFullscreenCheck();
                return;
            case Native.EVENT_OBJECT_CREATE:
                return; // windows are created hidden; SHOW follows
            case Native.EVENT_OBJECT_SHOW:
                if (Taskbar.IsTaskbar(hwnd)) Taskbar.EnsureHidden();
                if (!known.Contains(hwnd) && !IsAppWindow(hwnd)) return;
                break;
            case Native.EVENT_OBJECT_UNCLOAKED:
                if (!known.Contains(hwnd) && !IsAppWindow(hwnd)) return;
                break;
            default: // hide, destroy, cloak, minimize: only matters for windows we track
                if (!known.Contains(hwnd)) return;
                break;
        }
        ScheduleFullscreenCheck();
        if (!coalesce.IsEnabled) coalesce.Start();
    }

    private void ScheduleFullscreenCheck()
    {
        if (!fullscreenCheck.IsEnabled) fullscreenCheck.Start();
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (Native.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == ownPid) return;
        Foreground = hwnd;
        ForegroundApp = Describe(hwnd);
        WatchLocationOf(pid);
        ForegroundChanged?.Invoke();
        if (!coalesce.IsEnabled) coalesce.Start();
    }

    /// <summary>Move/resize events are frequent system-wide; listen to the foreground process only.</summary>
    private void WatchLocationOf(uint pid)
    {
        if (pid == locationPid) return;
        if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
        locationPid = pid;
        locationHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, proc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public bool IsFullscreen(IntPtr monitor) => fullscreenMonitors.Contains(monitor);

    /// <summary>
    /// Per monitor: walks windows top-down in z-order; the first real window touching a monitor decides.
    /// If it covers the whole monitor (Chrome video, games, F11) that monitor is fullscreen.
    /// Maximized windows never qualify because the menu bar's AppBar keeps them off the top strip.
    /// </summary>
    public void CheckFullscreen()
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
            // Visible bounds: with the raw rect, a window maximized on the other screen "touched" the video's
            // screen and brought the bar back whenever it got focus.
            if (!Native.GetVisibleRect(h, out var r) || r.Width <= 0 || r.Height <= 0) return true;

            bool app = IsAppWindow(h);
            // A maximized window with a title bar (Chrome, Explorer) is never fullscreen. Its size alone is not
            // enough: whenever our AppBar is gone (screens being rebuilt after a game switched display mode) it
            // grows to the whole monitor, the monitor read as fullscreen, the new bar was born hidden and never
            // reserved its strip again, so the window stayed full-size and the monitor stayed "fullscreen".
            long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
            bool captioned = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            bool maximized = captioned && Native.IsZoomed(h);
            bool candidate = !maximized && (app || h == fg || Native.GetWindowTextLength(h) > 0);
            foreach (var (mon, b) in pending.ToList())
            {
                // Slack: Chrome shrinks a fullscreen window by 1 px when it loses focus (so the taskbar can
                // show over it); without it, clicking the other screen brought the bar back over the video.
                const int slack = 4;
                bool covers = r.Left <= b.Left + slack && r.Top <= b.Top + slack && r.Right >= b.Right - slack && r.Bottom >= b.Bottom - slack;
                if (!covers && candidate && !captioned && h == fg && SnapToMonitor(h, r, b)) covers = true;
                if (covers && candidate) { result.Add(mon); pending.Remove(mon); }
                else if (app && r.Left < b.Right && r.Right > b.Left && r.Top < b.Bottom && r.Bottom > b.Top) pending.Remove(mon);
            }
            return true;
        }, IntPtr.Zero);

        if (result.SetEquals(fullscreenMonitors)) return;
        fullscreenMonitors = result;
        FullscreenChanged?.Invoke();
    }

    /// <summary>
    /// Borderless "windowed fullscreen" games (Valorant) place their monitor-sized window at the work area's
    /// origin, i.e. right below our menu bar: the bar covered the top of the game, its bottom ran off the
    /// screen and the monitor never read as fullscreen. A captionless, monitor-sized window that is only
    /// shifted by about a bar's height is put back on the monitor's origin.
    /// </summary>
    private static IntPtr snapWindow;
    private static DateTime snapSince;
    private static int snapCount;

    private static bool SnapToMonitor(IntPtr h, Native.RECT r, Native.RECT b)
    {
        const int size = 4, shift = 80;
        if (r.Width < b.Width - size || r.Height < b.Height - size || r.Width > b.Width + size || r.Height > b.Height + size) return false;
        int dx = r.Left - b.Left, dy = r.Top - b.Top;
        if (dx == 0 && dy == 0 || Math.Abs(dx) > shift || Math.Abs(dy) > shift) return false;
        // A game that keeps moving itself back would ping-pong with us every frame: give up after a few tries.
        var now = DateTime.UtcNow;
        if (h != snapWindow || now - snapSince > TimeSpan.FromSeconds(10)) { snapWindow = h; snapSince = now; snapCount = 0; }
        if (++snapCount > 3) return false;
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | 0x200 /* SWP_NOOWNERZORDER */;
        return Native.SetWindowPos(h, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, flags);
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
        known = ordered.SelectMany(a => a.Windows).ToHashSet();
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
        return new RunningApp { Key = Shell.AppKey(path), ExePath = path, Name = Shell.AppName(path) };
    }

    public void Dispose()
    {
        foreach (var h in hooks) Native.UnhookWinEvent(h);
        if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
        locationHook = IntPtr.Zero;
        hooks.Clear();
    }
}
