using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace Doca;

/// <summary>
/// Hosts the notification area (system tray) through ManagedShell: every app's tray icon,
/// including the ones Windows tucks into the overflow, with clicks forwarded to the app.
/// </summary>
internal static class Tray
{
    private static ShellManager? shell;

    public static NotificationArea? Area => shell?.NotificationArea;

    public static void Start()
    {
        try
        {
            var c = ShellManager.DefaultShellConfig;
            c.EnableTasksService = true; // window overlay badges + "needs attention" flashing
            c.AutoStartTasksService = true;
            c.EnableTrayService = true;
            c.AutoStartTrayService = true;
            shell = new ShellManager(c);
            Badges.Attach(shell.Tasks);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static void Stop()
    {
        try { Badges.Stop(); shell?.Dispose(); } catch (Exception ex) { App.Log(ex); }
        shell = null;
    }
}

/// <summary>
/// App notification state per window: the taskbar overlay badge (unread counters from Discord,
/// WhatsApp, Teams…) and FlashWindow attention requests.
/// </summary>
internal static class Badges
{
    private static readonly Dictionary<IntPtr, ApplicationWindow> windows = new();
    private static readonly HashSet<IntPtr> flashing = new();
    private static readonly Dictionary<IntPtr, string> titles = new();
    private static System.Windows.Interop.HwndSource? shellHook;
    private static uint shellMessage;

    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr h);

    /// <summary>Raised on the UI thread whenever any badge or attention state changes.</summary>
    public static event Action? Changed;

    public static void Attach(Tasks tasks)
    {
        StartShellHook();
        AttachTasks(tasks);
    }

    /// <summary>FlashWindowEx (an app asking for attention) arrives as HSHELL_FLASH; activation clears it.</summary>
    private static void StartShellHook()
    {
        shellHook = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Doca.ShellHook") { Width = 0, Height = 0, WindowStyle = 0 });
        shellMessage = Native.RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(shellHook.Handle);
        Native.EnumWindows((h, _) => { if (Native.IsWindowVisible(h)) titles[h] = Native.WindowTitle(h); return true; }, IntPtr.Zero);
        shellHook.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != shellMessage) return IntPtr.Zero;
            switch (wParam.ToInt32())
            {
                case 0x8006: // HSHELL_FLASH (only when we'd be the real shell)
                    if (lParam != Native.GetForegroundWindow() && flashing.Add(lParam)) Raise();
                    break;
                case 0x0006: // HSHELL_REDRAW: with Explorer as shell, FlashWindowEx arrives as this
                {
                    string title = Native.WindowTitle(lParam);
                    bool sameTitle = titles.TryGetValue(lParam, out var old) && old == title;
                    titles[lParam] = title;
                    // A title change is just a redraw; an unchanged background window "redrawn" is asking for attention.
                    if (sameTitle && lParam != Native.GetForegroundWindow() && flashing.Add(lParam)) Raise();
                    break;
                }
                case 0x0001: // HSHELL_WINDOWCREATED
                    titles[lParam] = Native.WindowTitle(lParam);
                    break;
                case 0x0004: // HSHELL_WINDOWACTIVATED
                case 0x8004: // HSHELL_RUDEAPPACTIVATED
                    titles[lParam] = Native.WindowTitle(lParam);
                    if (flashing.Remove(lParam)) Raise();
                    break;
                case 0x0002: // HSHELL_WINDOWDESTROYED
                    titles.Remove(lParam);
                    if (flashing.Remove(lParam)) Raise();
                    break;
            }
            return IntPtr.Zero;
        });
    }

    public static void Stop()
    {
        if (shellHook == null) return;
        DeregisterShellHookWindow(shellHook.Handle);
        shellHook.Dispose();
        shellHook = null;
    }

    private static void AttachTasks(Tasks tasks)
    {
        if (tasks.GroupedWindows == null) tasks.Initialize(false);
        var source = tasks.GroupedWindows?.SourceCollection;
        if (source == null) return;
        foreach (var w in source.OfType<ApplicationWindow>()) Add(w);
        if (source is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var w in windows.Values) w.PropertyChanged -= OnProperty;
                    windows.Clear();
                    foreach (var w in source.OfType<ApplicationWindow>()) Add(w);
                }
                foreach (var w in e.OldItems?.OfType<ApplicationWindow>() ?? [])
                {
                    w.PropertyChanged -= OnProperty;
                    windows.Remove(w.Handle);
                }
                foreach (var w in e.NewItems?.OfType<ApplicationWindow>() ?? []) Add(w);
                Raise();
            };
    }

    private static void Add(ApplicationWindow w)
    {
        windows[w.Handle] = w;
        w.PropertyChanged += OnProperty;
    }

    private static void OnProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.OverlayIcon) or nameof(ApplicationWindow.State)) Raise();
    }

    private static void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public static (ImageSource? overlay, bool attention) For(IReadOnlyList<IntPtr> hwnds)
    {
        ImageSource? overlay = null;
        bool attention = false;
        foreach (var h in hwnds)
        {
            attention |= flashing.Contains(h);
            if (!windows.TryGetValue(h, out var w)) continue;
            overlay ??= w.OverlayIcon;
            attention |= w.State == ApplicationWindow.WindowState.Flashing;
        }
        return (overlay, attention);
    }
}

/// <summary>Row of tray icons for a menu bar. Mirrors NotificationArea.TrayIcons live; drag to reorder.</summary>
internal sealed class TrayIconsPanel : StackPanel
{
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();

    /// <summary>Raised after the user reorders icons so the bars on other screens follow.</summary>
    private static event Action? OrderChanged;

    private readonly Brush hover;
    private readonly DocaConfig cfg;

    // Drag state: a left press is held back until release so the icon can be dragged instead of clicked.
    private Border? pressed;
    private Point pressPoint;
    private bool dragging;

    public TrayIconsPanel(Brush hover, DocaConfig cfg)
    {
        this.hover = hover;
        this.cfg = cfg;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Stretch;

        var area = Tray.Area;
        if (area == null) return;
        area.TrayIcons.CollectionChanged += OnCollectionChanged;
        OrderChanged += Rebuild;
        Unloaded += (_, _) =>
        {
            area.TrayIcons.CollectionChanged -= OnCollectionChanged;
            OrderChanged -= Rebuild;
        };
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        var area = Tray.Area;
        Children.Clear();
        if (area == null) return;
        int Rank(NotifyIcon i) { int r = cfg.TrayOrder.IndexOf(i.Identifier ?? ""); return r < 0 ? int.MaxValue : r; }
        foreach (var icon in area.TrayIcons.Where(i => !IsBuiltIn(i)).OrderBy(Rank).ToList())
            Children.Add(Button(icon));
    }

    /// <summary>Windows' own volume/network/power icons: the menu bar already has these.</summary>
    private static bool IsBuiltIn(NotifyIcon icon)
    {
        string g = icon.GUID.ToString();
        return g.StartsWith("7820ae73", StringComparison.OrdinalIgnoreCase)
            || g.StartsWith("7820ae74", StringComparison.OrdinalIgnoreCase)
            || g.StartsWith("7820ae75", StringComparison.OrdinalIgnoreCase);
    }

    private Border Button(NotifyIcon icon)
    {
        var img = new Image { Width = 16, Height = 16, Source = icon.Icon, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var b = new Border
        {
            Child = img,
            Tag = icon,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title,
            Visibility = icon.IsHidden ? Visibility.Collapsed : Visibility.Visible,
        };

        PropertyChangedEventHandler changed = (_, e) => Dispatcher.BeginInvoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(NotifyIcon.Icon): img.Source = icon.Icon; break;
                case nameof(NotifyIcon.Title): b.ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title; break;
                case nameof(NotifyIcon.IsHidden): b.Visibility = icon.IsHidden ? Visibility.Collapsed : Visibility.Visible; break;
            }
        });
        // Loaded/Unloaded pair (not ctor) because dragging re-parents the button.
        b.Loaded += (_, _) => { icon.PropertyChanged -= changed; icon.PropertyChanged += changed; };
        b.Unloaded += (_, _) => icon.PropertyChanged -= changed;

        uint Mouse() => MouseHelper.GetCursorPositionParam();
        void Place()
        {
            // Apps use this rect to position their flyouts next to the icon.
            if (PresentationSource.FromVisual(b) == null) return;
            var tl = b.PointToScreen(new Point(0, 0));
            var br = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        }

        b.MouseEnter += (_, _) => { b.Background = hover; Place(); icon.IconMouseEnter(Mouse()); };
        b.MouseLeave += (_, _) => { if (!dragging) b.Background = Brushes.Transparent; icon.IconMouseLeave(Mouse()); };
        b.MouseMove += (_, e) =>
        {
            if (pressed == b && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(this);
                if (!dragging && Math.Abs(p.X - pressPoint.X) > 6) dragging = true;
                if (dragging) { DragTo(b, p.X); return; }
            }
            icon.IconMouseMove(Mouse());
        };
        b.MouseDown += (_, e) =>
        {
            e.Handled = true;
            Place();
            if (e.ChangedButton == MouseButton.Left)
            {
                pressed = b;
                pressPoint = e.GetPosition(this);
                dragging = false;
                b.CaptureMouse();
                return;
            }
            icon.IconMouseDown(e.ChangedButton, Mouse(), GetDoubleClickTime());
        };
        b.MouseUp += (_, e) =>
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left && pressed == b)
            {
                b.ReleaseMouseCapture();
                pressed = null;
                if (dragging)
                {
                    dragging = false;
                    b.Opacity = 1;
                    SaveOrder();
                    return;
                }
                icon.IconMouseDown(MouseButton.Left, Mouse(), GetDoubleClickTime());
            }
            icon.IconMouseUp(e.ChangedButton, Mouse(), GetDoubleClickTime());
        };
        return b;
    }

    /// <summary>Moves the dragged icon to the slot under the cursor.</summary>
    private void DragTo(Border b, double x)
    {
        b.Opacity = 0.6;
        int from = Children.IndexOf(b), to = 0;
        double acc = 0;
        foreach (UIElement c in Children)
        {
            if (c == b) continue;
            double w = ((FrameworkElement)c).ActualWidth + 2;
            if (x > acc + w / 2) to++;
            acc += w;
        }
        if (to == from) return;
        Children.RemoveAt(from);
        Children.Insert(to, b);
        b.CaptureMouse(); // re-parenting drops capture
    }

    private void SaveOrder()
    {
        var ids = Children.OfType<Border>().Select(c => (c.Tag as NotifyIcon)?.Identifier).OfType<string>().ToList();
        // Keep remembered positions of icons that are not running right now at the end.
        cfg.TrayOrder = ids.Concat(cfg.TrayOrder.Where(id => !ids.Contains(id))).ToList();
        cfg.Save();
        OrderChanged?.Invoke();
    }
}
