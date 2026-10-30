using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Doca;

/// <summary>DWM helpers: per-pixel transparent windows composited by the GPU (no layered-window readback).</summary>
internal static class Glass
{
    /// <summary>
    /// Makes the window a non-activating tool window whose transparent pixels show the desktop.
    /// Uses DwmExtendFrameIntoClientArea instead of AllowsTransparency, so WPF renders straight
    /// into the DWM surface on the GPU.
    /// </summary>
    public static void MakeOverlay(Window w)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        var src = HwndSource.FromHwnd(h);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;

        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(h, ref m);

        int dark = 1;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int noRound = 1; // DWMWCP_DONOTROUND
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(h, Native.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        long ex = Native.ExStyle(h);
        ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Acrylic blur behind the whole window. tint is AABBGGRR.</summary>
    public static void EnableAcrylic(Window w, uint tint)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        var accent = new Native.AccentPolicy { AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 2, GradientColor = tint };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttribData { Attribute = Native.WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            Native.SetWindowCompositionAttribute(h, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void Place(Window w, int x, int y, int cx, int cy)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, cx, cy, Native.SWP_NOACTIVATE);
    }
}
