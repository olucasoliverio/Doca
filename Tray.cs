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
    public static string Stats => $"taskWindows={windows.Count} flashing={flashing.Count} titles={titles.Count}";
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

/// <summary>
/// Tray icons of a menu bar: as many as fit, the rest (plus the ones the user collapsed) behind a ⌄ button,
/// like Bartender on macOS. Drag to reorder; drag onto ⌄ to collapse.
/// </summary>
internal sealed class TrayIconsPanel : StackPanel
{
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();

    /// <summary>Raised after order/collapse changes so the bars on every screen follow.</summary>
    private static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    private const double Slot = 30; // icon 16 + padding 12 + margin 2

    private readonly Brush hover;
    private readonly DocaConfig cfg;
    private readonly StackPanel icons = new() { Orientation = Orientation.Horizontal };
    private readonly Border chevron;
    private double available = double.PositiveInfinity;
    private List<NotifyIcon> overflow = new();

    // Drag state: a left press is held back until release so the icon can be dragged instead of clicked.
    private Border? pressed;
    private Point pressPoint;
    private bool dragging, overChevron;

    public TrayIconsPanel(Brush hover, DocaConfig cfg)
    {
        this.hover = hover;
        this.cfg = cfg;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Stretch;

        chevron = new Border
        {
            Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(1, 3, 1, 3), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, ToolTip = "Ícones recolhidos", Visibility = Visibility.Collapsed,
        };
        chevron.MouseEnter += (_, _) => chevron.Background = hover;
        chevron.MouseLeave += (_, _) => { if (!dragging) chevron.Background = Brushes.Transparent; };
        chevron.MouseLeftButtonUp += (_, _) =>
        {
            if (dragging) return;
            var list = overflow.ToList();
            Flyout.Toggle("tray-overflow", chevron, () => new TrayOverflowFlyout(cfg, hover, list));
        };
        Children.Add(chevron);
        Children.Add(icons);

        var area = Tray.Area;
        if (area == null) return;
        area.TrayIcons.CollectionChanged += OnCollectionChanged;
        Changed += Rebuild;
        Unloaded += (_, _) =>
        {
            area.TrayIcons.CollectionChanged -= OnCollectionChanged;
            Changed -= Rebuild;
        };
        Rebuild();
    }

    /// <summary>Width the menu bar can give to tray icons (set by the bar as its contents change).</summary>
    public double Available
    {
        set
        {
            if (Math.Abs(value - available) < 1) return;
            int before = Fit(available);
            available = value;
            if (Fit(value) != before) Rebuild();
        }
    }

    private static int Fit(double width) =>
        double.IsInfinity(width) ? int.MaxValue : (int)Math.Max(0, Math.Floor((width - Slot) / Slot)); // keep room for ⌄

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Rebuild);

    /// <summary>All app tray icons in the user's order (Windows' volume/network/power excluded: the bar has its own).</summary>
    public static List<NotifyIcon> Ordered(DocaConfig cfg)
    {
        var area = Tray.Area;
        if (area == null) return new();
        int Rank(NotifyIcon i) { int r = cfg.TrayOrder.IndexOf(i.Identifier ?? ""); return r < 0 ? int.MaxValue : r; }
        return area.TrayIcons.Where(i => !IsBuiltIn(i) && !i.IsHidden).OrderBy(Rank).ToList();
    }

    public static bool IsCollapsed(DocaConfig cfg, NotifyIcon i) => cfg.TrayCollapsed.Contains(i.Identifier ?? "");

    private void Rebuild()
    {
        icons.Children.Clear();
        var all = Ordered(cfg);
        var inBar = all.Where(i => !IsCollapsed(cfg, i)).ToList();
        int fit = Fit(available);
        foreach (var icon in inBar.Take(fit)) icons.Children.Add(Button(icon));
        overflow = inBar.Skip(fit).Concat(all.Where(i => IsCollapsed(cfg, i))).ToList();
        chevron.Visibility = overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsBuiltIn(NotifyIcon icon)
    {
        string g = icon.GUID.ToString();
        return g.StartsWith("7820ae73", StringComparison.OrdinalIgnoreCase)
            || g.StartsWith("7820ae74", StringComparison.OrdinalIgnoreCase)
            || g.StartsWith("7820ae75", StringComparison.OrdinalIgnoreCase);
    }

    #region Shared icon button pieces (bar and overflow flyout)

    public static uint MousePos() => MouseHelper.GetCursorPositionParam();
    public static int DoubleClick() => GetDoubleClickTime();

    /// <summary>Icon visual that follows the app's icon/tooltip/visibility changes while it is on screen.</summary>
    public static Border Visual(NotifyIcon icon, Action? hiddenChanged = null)
    {
        var img = new Image { Width = 16, Height = 16, Source = icon.Icon, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var b = new Border
        {
            Child = img, Tag = icon,
            Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(1, 3, 1, 3), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title,
        };
        PropertyChangedEventHandler changed = (_, e) => b.Dispatcher.BeginInvoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(NotifyIcon.Icon): img.Source = icon.Icon; break;
                case nameof(NotifyIcon.Title): b.ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title; break;
                case nameof(NotifyIcon.IsHidden): hiddenChanged?.Invoke(); break;
            }
        });
        // Loaded/Unloaded pair (not ctor) because dragging re-parents the button.
        b.Loaded += (_, _) => { icon.PropertyChanged -= changed; icon.PropertyChanged += changed; };
        b.Unloaded += (_, _) => icon.PropertyChanged -= changed;
        return b;
    }

    /// <summary>Apps use this rect to position their own menus/flyouts next to the icon.</summary>
    public static void Place(Border b, NotifyIcon icon)
    {
        if (PresentationSource.FromVisual(b) == null) return;
        var tl = b.PointToScreen(new Point(0, 0));
        var br = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
        icon.Placement = new ManagedShell.Interop.NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    #endregion

    private Border Button(NotifyIcon icon)
    {
        var b = Visual(icon, () => Rebuild());

        b.MouseEnter += (_, _) => { b.Background = hover; Place(b, icon); icon.IconMouseEnter(MousePos()); };
        b.MouseLeave += (_, _) => { if (!dragging) b.Background = Brushes.Transparent; icon.IconMouseLeave(MousePos()); };
        b.MouseMove += (_, e) =>
        {
            if (pressed == b && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(icons);
                if (!dragging && Math.Abs(p.X - pressPoint.X) > 6)
                {
                    dragging = true;
                    chevron.Visibility = Visibility.Visible; // drop target for "collapse"
                }
                if (dragging) { DragTo(b, p.X, e.GetPosition(chevron)); return; }
            }
            icon.IconMouseMove(MousePos());
        };
        b.MouseDown += (_, e) =>
        {
            e.Handled = true;
            Place(b, icon);
            if (e.ChangedButton == MouseButton.Left)
            {
                pressed = b;
                pressPoint = e.GetPosition(icons);
                dragging = false;
                b.CaptureMouse();
                return;
            }
            icon.IconMouseDown(e.ChangedButton, MousePos(), DoubleClick());
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
                    chevron.Background = Brushes.Transparent;
                    EndDrag(icon);
                    return;
                }
                icon.IconMouseDown(MouseButton.Left, MousePos(), DoubleClick());
            }
            icon.IconMouseUp(e.ChangedButton, MousePos(), DoubleClick());
        };
        return b;
    }

    /// <summary>Moves the dragged icon to the slot under the cursor, or highlights ⌄ when hovering it.</summary>
    private void DragTo(Border b, double x, Point onChevron)
    {
        overChevron = onChevron.X >= 0 && onChevron.X <= chevron.ActualWidth;
        chevron.Background = overChevron ? hover : Brushes.Transparent;
        b.Opacity = overChevron ? 0.35 : 0.6;
        if (overChevron) return;

        int from = icons.Children.IndexOf(b), to = 0;
        double acc = 0;
        foreach (UIElement c in icons.Children)
        {
            if (c == b) continue;
            double w = ((FrameworkElement)c).ActualWidth + 2;
            if (x > acc + w / 2) to++;
            acc += w;
        }
        if (to == from) return;
        icons.Children.RemoveAt(from);
        icons.Children.Insert(to, b);
        b.CaptureMouse(); // re-parenting drops capture
    }

    private void EndDrag(NotifyIcon icon)
    {
        var ids = icons.Children.OfType<Border>().Select(c => (c.Tag as NotifyIcon)?.Identifier).OfType<string>().ToList();
        // Keep remembered positions of icons that are not in the bar right now after these.
        cfg.TrayOrder = ids.Concat(cfg.TrayOrder.Where(id => !ids.Contains(id))).ToList();
        if (overChevron && icon.Identifier is string id && !cfg.TrayCollapsed.Contains(id)) cfg.TrayCollapsed.Add(id);
        overChevron = false;
        cfg.Save();
        RaiseChanged();
    }
}

/// <summary>Drop-down with the tray icons that did not fit or were collapsed. 📌 brings a collapsed icon back to the bar.</summary>
internal sealed class TrayOverflowFlyout : Flyout
{
    public TrayOverflowFlyout(DocaConfig cfg, Brush hover, List<NotifyIcon> icons) : base(cfg, 276)
    {
        var grid = new WrapPanel();
        foreach (var icon in icons)
        {
            var b = TrayIconsPanel.Visual(icon);
            b.Padding = new Thickness(10, 8, 10, 8);
            b.Margin = new Thickness(2);
            b.MouseEnter += (_, _) => { b.Background = hover; TrayIconsPanel.Place(b, icon); icon.IconMouseEnter(TrayIconsPanel.MousePos()); };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; icon.IconMouseLeave(TrayIconsPanel.MousePos()); };
            b.MouseMove += (_, _) => icon.IconMouseMove(TrayIconsPanel.MousePos());
            b.MouseDown += (_, e) => { e.Handled = true; TrayIconsPanel.Place(b, icon); };
            // Down+up together on release: the app's menu will take focus and close this flyout right after.
            b.MouseUp += (_, e) =>
            {
                e.Handled = true;
                icon.IconMouseDown(e.ChangedButton, TrayIconsPanel.MousePos(), TrayIconsPanel.DoubleClick());
                icon.IconMouseUp(e.ChangedButton, TrayIconsPanel.MousePos(), TrayIconsPanel.DoubleClick());
            };

            var tile = new Grid();
            tile.Children.Add(b);
            if (TrayIconsPanel.IsCollapsed(cfg, icon))
            {
                var pin = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily(IconFont), FontSize = 10, Padding = new Thickness(3),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Visibility = Visibility.Hidden, Background = Brushes.Transparent, ToolTip = "Mostrar na barra",
                };
                pin.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    cfg.TrayCollapsed.Remove(icon.Identifier ?? "");
                    cfg.Save();
                    TrayIconsPanel.RaiseChanged();
                    CloseFlyout();
                };
                tile.MouseEnter += (_, _) => pin.Visibility = Visibility.Visible;
                tile.MouseLeave += (_, _) => pin.Visibility = Visibility.Hidden;
                tile.Children.Add(pin);
            }
            grid.Children.Add(tile);
        }
        Body.Children.Add(grid);
        Body.Children.Add(new TextBlock
        {
            Text = "Arraste um ícone da barra até ⌄ para recolhê-lo. Passe o mouse e clique em 📌 para trazê-lo de volta.",
            FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0),
        });
    }
}
