using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Doca;

/// <summary>
/// Registers a window as a shell AppBar so the reserved strip is excluded from the work area
/// (maximized windows stop at the bar instead of sliding under it).
/// Re-registers automatically when Explorer restarts.
/// </summary>
internal sealed class AppBar : IDisposable
{
    private static readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr hwnd;
    private readonly uint edge;
    private readonly uint callback;
    private bool registered;
    private Native.RECT monitor;
    private int thickness;

    public event Action? ExplorerRestarted;

    public AppBar(Window w, uint edge)
    {
        hwnd = new WindowInteropHelper(w).Handle;
        this.edge = edge;
        callback = Native.RegisterWindowMessage("Doca.AppBar");
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    private Native.APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd, uCallbackMessage = callback, uEdge = edge };

    public void Reserve(Native.RECT monitorPx, int thicknessPx)
    {
        monitor = monitorPx;
        thickness = thicknessPx;
        if (!registered)
        {
            var abd = Data();
            Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
            registered = true;
            lock (active) { active.Add(hwnd); Watchdog.SaveAppBars(active); }
        }
        Apply();
    }

    private void Apply()
    {
        var abd = Data();
        abd.rc = monitor;
        Clamp(ref abd.rc);
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        Clamp(ref abd.rc);
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
    }

    private void Clamp(ref Native.RECT rc)
    {
        if (edge == Native.ABE_TOP) rc.Bottom = rc.Top + thickness;
        else rc.Top = rc.Bottom - thickness;
    }

    public void Release()
    {
        if (!registered) return;
        var abd = Data();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        registered = false;
        lock (active) { active.Remove(hwnd); Watchdog.SaveAppBars(active); }
    }

    /// <summary>Registered AppBar windows, persisted so the watchdog can release them after a crash.</summary>
    private static readonly HashSet<IntPtr> active = new();

    private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Fullscreen is detected per monitor by WindowTracker; ABN_FULLSCREENAPP is unreliable with video players.
        if (msg == callback && registered)
        {
            if (wParam.ToInt32() == Native.ABN_POSCHANGED) Apply();
        }
        else if (msg == taskbarCreated)
        {
            if (registered)
            {
                registered = false;
                Reserve(monitor, thickness);
            }
            ExplorerRestarted?.Invoke();
        }
        else if (registered && (msg == Native.WM_ACTIVATE || msg == Native.WM_WINDOWPOSCHANGED))
        {
            var abd = Data();
            Native.SHAppBarMessage(msg == Native.WM_ACTIVATE ? Native.ABM_ACTIVATE : Native.ABM_WINDOWPOSCHANGED, ref abd);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Release();
}

/// <summary>Hides the Windows taskbar (all monitors) and restores it on exit.</summary>
internal static class Taskbar
{
    private static bool hidden;

    private static IEnumerable<IntPtr> Windows()
    {
        var list = new List<IntPtr>();
        uint own = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            // Skip our own tray host window (ManagedShell registers a Shell_TrayWnd too).
            if (Native.ClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" &&
                Native.GetWindowThreadProcessId(h, out uint pid) != 0 && pid != own)
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool GetAutoHide()
    {
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
        return (Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToInt64() & Native.ABS_AUTOHIDE) != 0;
    }

    private static void SetAutoHide(bool on)
    {
        var abd = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = Native.FindWindow("Shell_TrayWnd", null),
            lParam = new IntPtr(on ? Native.ABS_AUTOHIDE : Native.ABS_ALWAYSONTOP),
        };
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
    }

    public static void Hide(DocaConfig cfg)
    {
        if (cfg.TaskbarAutoHideBefore == null)
        {
            cfg.TaskbarAutoHideBefore = GetAutoHide();
            cfg.Save();
        }
        // Auto-hide releases the taskbar's reserved strip; hiding the window stops it from popping up.
        SetAutoHide(true);
        hidden = true;
        EnsureHidden();
    }

    public static void EnsureHidden()
    {
        if (!hidden) return;
        foreach (var h in Windows())
            if (Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_HIDE);
    }

    public static void Restore(DocaConfig cfg)
    {
        if (!hidden) return;
        hidden = false;
        ForceRestore(cfg);
    }

    /// <summary>Shows the taskbar and puts back its auto-hide state (also used by the watchdog after a crash).</summary>
    public static void ForceRestore(DocaConfig cfg)
    {
        foreach (var h in Windows()) Native.ShowWindow(h, Native.SW_SHOWNA);
        SetAutoHide(cfg.TaskbarAutoHideBefore ?? false);
        cfg.TaskbarAutoHideBefore = null;
        cfg.Save();
    }
}
