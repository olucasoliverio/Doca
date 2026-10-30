using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Doca;

/// <summary>
/// Rounded screen corners: four tiny click-through windows per monitor, each masking one corner
/// with black. Static layered windows of a few pixels cost nothing to composite.
/// </summary>
internal sealed class CornerWindow : Window
{
    private readonly IntPtr monitor;
    private readonly int corner; // 0 TL, 1 TR, 2 BL, 3 BR
    private readonly double radius;
    private readonly DispatcherTimer keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public static List<CornerWindow> CreateFor(Native.Monitor m, double radius)
    {
        var list = new List<CornerWindow>();
        for (int c = 0; c < 4; c++)
        {
            var w = new CornerWindow(m.Handle, c, radius);
            w.Show();
            list.Add(w);
        }
        return list;
    }

    private CornerWindow(IntPtr monitor, int corner, double radius)
    {
        this.monitor = monitor;
        this.corner = corner;
        this.radius = radius;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = Top = -20000;
        Width = Height = radius;

        // Square minus a quarter circle, oriented toward this corner.
        double r = radius;
        string d = corner switch
        {
            0 => $"M0,0 H{r} A{r},{r} 0 0 0 0,{r} Z",
            1 => $"M0,0 H{r} V{r} A{r},{r} 0 0 0 0,0 Z",
            2 => $"M0,0 A{r},{r} 0 0 0 {r},{r} H0 Z",
            _ => $"M{r},0 V{r} H0 A{r},{r} 0 0 0 {r},0 Z",
        };
        Content = new Path { Data = Geometry.Parse(d), Fill = Brushes.Black, Stretch = Stretch.None };

        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            long ex = Native.ExStyle(h) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
            Place();
        };
        DpiChanged += (_, _) => Place();
        // Other topmost windows (our bars included) can end up above; re-assert cheaply.
        keepOnTop.Tick += (_, _) => Place();
        keepOnTop.Start();
        Closed += (_, _) => keepOnTop.Stop();
    }

    private void Place()
    {
        var m = Native.MonitorInfo(monitor);
        int size = (int)Math.Ceiling(radius * m.Scale);
        var b = m.Bounds;
        int x = corner is 0 or 2 ? b.Left : b.Right - size;
        int y = corner is 0 or 1 ? b.Top : b.Bottom - size;
        Glass.Place(this, x, y, size, size);
    }
}
