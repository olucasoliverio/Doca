using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Doca;

/// <summary>
/// Rounded screen corners: four tiny click-through windows per monitor, each masking one corner with black.
/// <para>
/// Plain Win32 layered windows painted once with UpdateLayeredWindow (a premultiplied 32-bit bitmap,
/// antialiased by 4×4 supersampling), not WPF windows: eight WPF windows cost ~11 MB of private memory and
/// ~1000 handles (each brings its own HwndSource, render target and MIL resources) to draw a few black pixels.
/// </para>
/// </summary>
internal sealed class CornerWindow
{
    private readonly IntPtr monitor;
    private readonly int corner; // 0 TL, 1 TR, 2 BL, 3 BR
    private readonly double radius;
    private IntPtr hwnd;
    /// <summary>Size of the bitmap last painted (pixels); repainted when the monitor's scale changes.</summary>
    private int painted = -1;
    private static readonly List<CornerWindow> all = new();
    private static DispatcherTimer? safety;

    /// <summary>Set by App: monitors with a fullscreen app (game, video) get no corners.</summary>
    public static Func<IntPtr, bool>? IsFullscreen;

    /// <summary>
    /// Puts the corners back above other topmost windows (our bars included) after something reappears.
    /// On a fullscreen monitor they are hidden instead: a topmost window over a game draws black corners on
    /// it and, worse, forces DWM to composite it (no independent flip: stutter, input lag; exclusive
    /// fullscreen games may even minimize).
    /// </summary>
    public static void BringAllToTop()
    {
        foreach (var c in all.ToList())
        {
            if (c.hwnd == IntPtr.Zero) continue;
            if (IsFullscreen?.Invoke(c.monitor) == true) { if (Native.IsWindowVisible(c.hwnd)) Native.ShowWindow(c.hwnd, SW_HIDE); continue; }
            c.Place();
            if (!Native.IsWindowVisible(c.hwnd)) Native.ShowWindow(c.hwnd, SW_SHOWNOACTIVATE);
        }
    }

    public static List<CornerWindow> CreateFor(Native.Monitor m, double radius)
    {
        var list = new List<CornerWindow>();
        for (int c = 0; c < 4; c++)
        {
            var w = new CornerWindow(m.Handle, c, radius);
            if (w.hwnd == IntPtr.Zero) continue;
            w.Place();
            if (IsFullscreen?.Invoke(m.Handle) != true) Native.ShowWindow(w.hwnd, SW_SHOWNOACTIVATE);
            list.Add(w);
        }
        return list;
    }

    private CornerWindow(IntPtr monitor, int corner, double radius)
    {
        this.monitor = monitor;
        this.corner = corner;
        this.radius = radius;
        RegisterClass();
        hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "Doca Corner", WS_POPUP, -20000, -20000, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { App.Log(new Exception($"CreateWindowEx (canto) falhou: {Marshal.GetLastWin32Error()}")); return; }
        // Re-asserted on events (BringAllToTop) plus a slow safety check, not on a fast timer.
        all.Add(this);
        if (safety == null)
        {
            safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            safety.Tick += (_, _) => BringAllToTop();
            safety.Start();
        }
    }

    public void Close()
    {
        all.Remove(this);
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        hwnd = IntPtr.Zero;
    }

    private void Place()
    {
        var m = Native.MonitorInfo(monitor);
        double r = radius * m.Scale;
        int size = (int)Math.Ceiling(r);
        var b = m.Bounds;
        int x = corner is 0 or 2 ? b.Left : b.Right - size;
        int y = corner is 0 or 1 ? b.Top : b.Bottom - size;
        if (size != painted) Paint(x, y, size, r);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Square minus a quarter circle centered on the inner corner, black, alpha = coverage.</summary>
    private void Paint(int x, int y, int size, double r)
    {
        // Circle center (in pixels) on the corner of the square that points into the screen.
        double cx = corner is 0 or 2 ? size : 0, cy = corner is 0 or 1 ? size : 0;
        var pixels = new int[size * size];
        const int ss = 4;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                int outside = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double dx = px + (sx + 0.5) / ss - cx, dy = py + (sy + 0.5) / ss - cy;
                        if (dx * dx + dy * dy > r * r) outside++;
                    }
                // Premultiplied black: only alpha is nonzero.
                pixels[py * size + px] = unchecked((int)((uint)(outside * 255 / (ss * ss)) << 24));
            }

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return;
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            IntPtr old = SelectObject(mem, dib);
            var dst = new POINT { X = x, Y = y };
            var sz = new SIZE { cx = size, cy = size };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            if (UpdateLayeredWindow(hwnd, screen, ref dst, ref sz, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */)) painted = size;
            SelectObject(mem, old);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    #region Win32

    private const string ClassName = "Doca.Corner";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    /// <summary>Kept alive for as long as the class is registered (the GC would free the thunk).</summary>
    private static WndProc? proc;

    private static void RegisterClass()
    {
        if (proc != null) return;
        proc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null), lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0) App.Log(new Exception($"RegisterClassEx (canto) falhou: {Marshal.GetLastWin32Error()}"));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    private struct POINT { public int X, Y; }
    private struct SIZE { public int cx, cy; }
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE size, IntPtr src, ref POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    #endregion
}
