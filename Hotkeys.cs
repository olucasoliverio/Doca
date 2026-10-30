using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Doca;

/// <summary>
/// macOS Cmd+` on Windows: cycles through the windows of the app in focus (e.g. two Chrome windows).
/// Alt+` goes forward, Alt+Shift+` backward (modifier configurable).
/// </summary>
internal static class WindowCycler
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private const int WM_HOTKEY = 0x312, IdForward = 1, IdBackward = 2;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    // Physical key left of "1" (scan code 0x29: ` on US, ' on ABNT2), the same spot as on a Mac.
    private static uint KeyLeftOfOne => MapVirtualKey(0x29, 1 /* MAPVK_VSC_TO_VK */) is uint vk and not 0 ? vk : 0xC0;

    private static HwndSource? sink;
    private static WindowTracker? tracker;

    public static string Shortcut(DocaConfig cfg) => (cfg.WindowCycleModifier == "Ctrl" ? "Ctrl" : "Alt") + "+`";

    public static void Start(DocaConfig cfg, WindowTracker t)
    {
        Stop();
        tracker = t;
        if (!cfg.WindowCycleHotkey) return;
        sink = new HwndSource(new HwndSourceParameters("Doca.Hotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        sink.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY) { Cycle(backward: wParam.ToInt32() == IdBackward); handled = true; }
            return IntPtr.Zero;
        });
        uint mod = (cfg.WindowCycleModifier == "Ctrl" ? MOD_CONTROL : MOD_ALT) | MOD_NOREPEAT;
        uint vk = KeyLeftOfOne;
        if (!RegisterHotKey(sink.Handle, IdForward, mod, vk) | !RegisterHotKey(sink.Handle, IdBackward, mod | MOD_SHIFT, vk))
            App.Log(new InvalidOperationException($"Atalho {Shortcut(cfg)} já está em uso por outro programa."));
    }

    public static void Stop()
    {
        if (sink == null) return;
        UnregisterHotKey(sink.Handle, IdForward);
        UnregisterHotKey(sink.Handle, IdBackward);
        sink.Dispose();
        sink = null;
    }

    /// <summary>Brings the next window of the foreground app to the front.</summary>
    public static void Cycle(bool backward)
    {
        if (tracker == null) return;
        IntPtr fg = Native.GetForegroundWindow();
        var app = tracker.Describe(fg);
        if (app == null) return;

        // Fresh z-ordered list (topmost first) of this app's visible, non-minimized windows.
        var wins = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (WindowTracker.IsAppWindow(h) && !Native.IsIconic(h) && tracker.Describe(h)?.Key == app.Key) wins.Add(h);
            return true;
        }, IntPtr.Zero);
        if (wins.Count < 2) return;

        if (backward)
        {
            // Send the current one to the back of the stack, then show the next one down.
            var current = wins[0];
            Shell.Focus(wins[1]);
            Native.SetWindowPos(current, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            // Bringing the backmost forward walks through every window, like Cmd+`.
            Shell.Focus(wins[^1]);
        }
    }
}
