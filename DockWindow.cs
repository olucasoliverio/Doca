using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Doca;

internal enum DockItemKind { App, Separator, Trash }

internal sealed class DockItem
{
    public required string Key { get; init; }
    public required DockItemKind Kind { get; init; }
    public string Name = "";
    public PinnedApp? Pin;
    public RunningApp? App;
    public ImageSource? Icon;
    public string? IconSource;
    public FormattedText? Label;

    public IReadOnlyList<IntPtr> Windows => App?.Windows ?? (IReadOnlyList<IntPtr>)Array.Empty<IntPtr>();
    public bool Running => App != null && App.Windows.Count > 0;

    // Animation / layout state (DIP).
    public double Presence;
    public bool Removing;
    public double BounceStart = -1, BounceEnd;
    public bool LaunchedCold;
    public bool Attention;
    public double SlotStart, Slot, X, Width;
    public double DrawOffset; // eases items into their new slot after a reorder
}

/// <summary>
/// The Dock. Everything is drawn in a single retained visual and animated from
/// CompositionTarget.Rendering (vsync), which only runs while something moves:
/// idle CPU is zero. Transparent areas are click-through via a window region.
/// </summary>
internal sealed class DockWindow : Window
{
    private const double Gap = 6, SepWidth = 14, PadX = 8, PadTop = 7, PadBottom = 9, BottomGap = 5, LabelSpace = 44, Radius = 20;
    private const double BouncePeriod = 0.5, BounceHeight = 20;

    private readonly DocaConfig cfg;
    private readonly WindowTracker tracker;
    private readonly Surface surface;
    private readonly List<DockItem> items = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly IntPtr monitor;
    private Native.RECT monitorBounds;
    private IntPtr hwnd;
    private AppBar? appBar;
    private bool closed;
    private double scale = 1;
    private int winX, winY, winW, winH;
    private bool built;

    private bool looping;
    private TimeSpan lastFrame;
    private bool inside;
    private double lastInside = -10, hover, mouseX, hideOffset, lastPx = -1, lastPy = -1;
    private DockItem? hot, pressed;
    private bool menuOpen;
    private double revealUntil;

    // Drag to reorder.
    private bool dragging;
    private double pressX, dragGrab, dragX, dragY;
    private Dictionary<DockItem, double>? slideFrom;
    private double panelL, panelR, panelTop, panelBottom;
    private (Int32Rect a, Int32Rect? b)? region;

    private Brush panelFill = PanelBrush(0.75);

    private static Brush PanelBrush(double opacity)
    {
        byte a = (byte)Math.Clamp(opacity * 255, 0, 255);
        return Frozen(new LinearGradientBrush(
            Color.FromArgb((byte)(a * 0.93), 0x3A, 0x3A, 0x40), Color.FromArgb(a, 0x22, 0x22, 0x27), 90));
    }
    private static readonly Pen panelStroke = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen sepPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush dotFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush labelFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x2A, 0x2A, 0x2E)));
    private static readonly Pen labelStroke = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush placeholder = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush badgeFill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)));
    private static readonly Pen badgeStroke = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x1E, 0x22))), 1.5));
    private static readonly Brush progressTrack = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x1E, 0x1E, 0x22)));
    private static readonly Pen progressStroke = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush progressNormal = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush progressPaused = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly Brush progressError = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly Brush progressBusy = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x84, 0xFF)));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private double IconSize => cfg.IconSize;
    private double MaxSize => cfg.Magnification ? Math.Max(cfg.MaxIconSize, cfg.IconSize) : cfg.IconSize;
    private double PanelH => IconSize + PadTop + PadBottom;
    private double WinHDip => BottomGap + PadBottom + MaxSize + LabelSpace;
    private double HiddenOffset => PanelH + BottomGap + 2;

    /// <summary>Raised when a Dock setting changes so every screen's Dock re-applies it.</summary>
    private static event Action? SettingsChanged;

    public static void RaiseSettingsChanged() => SettingsChanged?.Invoke();

    private void OnSettingsChanged()
    {
        // Bigger icons need sharper bitmaps: reload any icon smaller than the new maximum.
        int needed = (int)Math.Ceiling(MaxSize * Native.MonitorInfo(monitor).Scale);
        if (needed > Shell.IconPixelSize)
        {
            Shell.IconPixelSize = needed;
            Shell.ClearIconCache();
        }
        foreach (var it in items)
            if (it.Icon is System.Windows.Media.Imaging.BitmapSource b && b.PixelWidth < Shell.IconPixelSize - 1)
                it.IconSource = null;
        Reposition();
        Rebuild();
    }

    public DockWindow(DocaConfig cfg, WindowTracker tracker, IntPtr monitor)
    {
        this.cfg = cfg;
        this.tracker = tracker;
        this.monitor = monitor;
        if (cfg.AutoHideDock) hideOffset = HiddenOffset; // start tucked away
        Title = "Doca Dock";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        AllowDrop = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000;
        Width = Height = 10;

        surface = new Surface(this);
        RenderOptions.SetBitmapScalingMode(surface, BitmapScalingMode.Linear);
        Content = surface;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Glass.MakeOverlay(this);
            appBar = new AppBar(this, Native.ABE_BOTTOM);
            appBar.ExplorerRestarted += Taskbar.EnsureHidden;
            Reposition();
        };

        tracker.AppsChanged += Rebuild;
        tracker.FullscreenChanged += OnFullscreenChanged;
        Badges.Changed += OnBadgesChanged;
        previewTimer.Tick += (_, _) => OnPreviewTimer();
        SettingsChanged += OnSettingsChanged;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        Closed += (_, _) =>
        {
            closed = true;
            tracker.AppsChanged -= Rebuild;
            tracker.FullscreenChanged -= OnFullscreenChanged;
            Badges.Changed -= OnBadgesChanged;
            SettingsChanged -= OnSettingsChanged;
            if (looping) CompositionTarget.Rendering -= OnFrame;
            looping = false;
            appBar?.Release();
            previewTimer.Stop();
            preview?.Close();
        };

        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => Wake();
        MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(surface);
            pressed = ItemAt(p);
            pressX = p.X;
            if (pressed != null) CaptureMouse();
            Wake();
        };
        MouseLeftButtonUp += OnLeftUp;
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle && ItemAt(e.GetPosition(surface)) is { Kind: DockItemKind.App } it) Launch(it); };
        MouseRightButtonUp += (_, e) => ShowMenu(ItemAt(e.GetPosition(surface)));
        DragOver += OnDragOver;
        Drop += OnDrop;

        Shell.IconPixelSize = Math.Max(Shell.IconPixelSize, (int)Math.Ceiling(MaxSize * Native.MonitorInfo(monitor).Scale));
        Rebuild();
        built = true;
    }

    public void ReleaseAppBar() => appBar?.Release();

    private void Reposition()
    {
        if (hwnd == IntPtr.Zero || closed) return;
        var m = Native.MonitorInfo(monitor);
        var mon = m.Bounds;
        monitorBounds = mon;
        scale = m.Scale;
        panelFill = PanelBrush(cfg.DockOpacity);
        winW = mon.Width;
        winH = (int)Math.Ceiling(WinHDip * scale);
        winX = mon.Left;
        winY = mon.Bottom - winH;
        Glass.Place(this, winX, winY, winW, winH);

        if (cfg.AutoHideDock) appBar?.Release();
        else appBar?.Reserve(mon, (int)Math.Round((PanelH + BottomGap) * scale));

        foreach (var it in items) it.Label = null;
        region = null;
        Wake();
    }

    private void OnFullscreenChanged()
    {
        if (closed) return;
        bool fs = tracker.IsFullscreen(monitor);
        if (fs && IsVisible) { HidePreview(); Hide(); }
        else if (!fs && !IsVisible) { Show(); Reposition(); }
    }

    #region Model

    private void Rebuild()
    {
        var existing = items.ToDictionary(i => i.Key);
        var running = tracker.Apps.ToDictionary(a => a.Key);
        var desired = new List<DockItem>();
        var used = new HashSet<string>();

        DockItem Get(string key, DockItemKind kind)
        {
            if (existing.TryGetValue(key, out var it)) { it.Removing = false; return it; }
            return new DockItem { Key = key, Kind = kind, Presence = built ? 0 : 1 };
        }

        foreach (var p in cfg.Pinned)
        {
            if (!used.Add(p.Key)) continue;
            var it = Get(p.Key, DockItemKind.App);
            it.Pin = p;
            it.App = running.GetValueOrDefault(p.Key);
            // The pin matched through Shell.AppKey but points at an old version folder: follow the update
            // so launching from the Dock keeps working after the running copy closes.
            if (p.Aumid == null && p.ResolvedPath == null && it.App?.ExePath is { } exe &&
                !string.Equals(p.Path, exe, StringComparison.OrdinalIgnoreCase) && !File.Exists(p.Path))
            {
                p.Path = exe;
                cfg.Save();
            }
            SetName(it, p.Name ?? it.App?.Name ?? (p.Aumid != null ? Shell.DisplayName(@"shell:AppsFolder\" + p.Aumid) : null) ?? Shell.AppName(p.ResolvedPath ?? p.Path ?? ""));
            SetIcon(it, p.Aumid != null ? @"shell:AppsFolder\" + p.Aumid : p.ResolvedPath ?? p.Path ?? "");
            desired.Add(it);
        }

        // Running apps follow the pinned ones with no divider between them (Lucas prefers one continuous row).
        var extras = tracker.Apps.Where(a => !used.Contains(a.Key)).ToList();
        foreach (var a in extras)
        {
            var it = Get(a.Key, DockItemKind.App);
            it.Pin = null;
            it.App = a;
            SetName(it, a.Name);
            SetIcon(it, a.Aumid != null ? @"shell:AppsFolder\" + a.Aumid : a.ExePath ?? "");
            desired.Add(it);
        }

        desired.Add(Get("|sep-trash", DockItemKind.Separator));
        var trash = Get("|trash", DockItemKind.Trash);
        SetName(trash, "Lixeira");
        SetIcon(trash, Shell.RecycleBin);
        desired.Add(trash);

        // Items that disappeared stay where they were while they shrink away.
        var keys = desired.Select(d => d.Key).ToHashSet();
        for (int i = 0; i < items.Count; i++)
        {
            var old = items[i];
            if (keys.Contains(old.Key)) continue;
            old.Removing = true;
            desired.Insert(Math.Min(i, desired.Count), old);
        }

        items.Clear();
        items.AddRange(desired);
        Wake();
    }

    private static void SetName(DockItem it, string name)
    {
        if (it.Name == name) return;
        it.Name = name;
        it.Label = null;
    }

    private void SetIcon(DockItem it, string source)
    {
        if (it.IconSource == source || source.Length == 0) return;
        it.IconSource = source;
        Shell.RequestIcon(source, img =>
        {
            if (it.IconSource != source) return;
            it.Icon = img;
            surface.InvalidateVisual();
        });
    }

    #endregion

    #region Animation

    private void Wake()
    {
        if (looping) return;
        looping = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == lastFrame) return;
        double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((t - lastFrame).TotalSeconds, 0.001, 0.05);
        lastFrame = t;

        bool busy = Step(dt);
        surface.InvalidateVisual();
        UpdateRegion(busy || inside || hover > 0.001);
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            looping = false;
        }
    }

    /// <summary>Frame-rate independent exponential approach. Returns true while still moving.</summary>
    private static bool Approach(ref double v, double target, double rate, double dt, double eps = 0.002)
    {
        double d = target - v;
        if (Math.Abs(d) < eps) { v = target; return false; }
        v += d * (1 - Math.Exp(-rate * dt));
        return true;
    }

    private bool Step(double dt)
    {
        double now = clock.Elapsed.TotalSeconds;
        bool busy = false;

        Native.GetCursorPos(out var p);
        double px = (p.X - winX) / scale, py = (p.Y - winY) / scale;
        double w = winW / scale;
        bool hidden = cfg.AutoHideDock && hideOffset > PanelH * 0.5;

        // "inside": over the Dock itself (magnification, hover, clicks).
        // "present": also touching the bottom edge anywhere, which reveals an auto-hidden Dock and
        // keeps it up while the pointer stays there (otherwise it flickers at the screen's sides).
        bool atEdge = px >= 0 && px <= w && py >= WinHDip - 3 && py <= WinHDip + 1;
        inside = !hidden && px >= panelL && px <= panelR && py >= IconsTop() - 6 && py <= WinHDip + 1;
        bool present = inside || (cfg.AutoHideDock && atEdge);
        if (present) lastInside = now;
        if (inside) mouseX = px;

        bool magnify = inside && cfg.Magnification && !menuOpen;
        busy |= Approach(ref hover, magnify ? 1 : 0, 13, dt);

        if (cfg.AutoHideDock)
        {
            bool show = present || menuOpen || dragging || now - lastInside < 0.4 || now < revealUntil;
            if (!present && show) busy = true;
            busy |= Approach(ref hideOffset, show ? 0 : HiddenOffset, show ? 18 : 9, dt, 0.1);
        }
        else hideOffset = 0;

        foreach (var it in items)
        {
            busy |= Approach(ref it.Presence, it.Removing ? 0 : 1, 11, dt, 0.004);
            busy |= Approach(ref it.DrawOffset, 0, 16, dt, 0.05);
            if (it.BounceStart < 0) continue;
            if (it.LaunchedCold && it.Running)
            {
                // App is up: finish the current hop and land.
                it.BounceEnd = Math.Min(it.BounceEnd, it.BounceStart + Math.Ceiling((now - it.BounceStart) / BouncePeriod) * BouncePeriod);
                it.LaunchedCold = false;
            }
            if (now >= it.BounceEnd) it.BounceStart = -1;
            else busy = true;
        }
        items.RemoveAll(i => i.Removing && i.Presence <= 0.004);

        Layout();
        var prevHot = hot;
        hot = inside && !hidden ? ItemAt(new Point(px, py)) : null;
        if (hot != prevHot) OnHotChanged();

        // A resting pointer needs no frames: MouseMove/MouseLeave wake the loop again.
        bool moved = Math.Abs(px - lastPx) > 0.01 || Math.Abs(py - lastPy) > 0.01;
        lastPx = px;
        lastPy = py;
        return busy || dragging || (present && moved) || hot != prevHot;
    }

    private void Layout()
    {
        double w = winW / scale;
        double k = MaxSize / IconSize - 1;
        double range = IconSize * 3.2;

        double total = 0;
        foreach (var it in items)
        {
            double bw = (it.Kind == DockItemKind.Separator ? SepWidth : IconSize) * it.Presence;
            it.SlotStart = total;
            it.Slot = bw + Gap * it.Presence;
            it.Width = bw;
            total += it.Slot;
        }
        double baseStart = (w - total) / 2;

        // Magnify using distances in the *base* layout (stable, no feedback loop).
        double mx = Math.Clamp(mouseX, baseStart, baseStart + total);
        double magPos = 0, mapped = mx;
        foreach (var it in items)
        {
            double center = baseStart + it.SlotStart + it.Slot / 2;
            double baseSlot = it.Slot;
            if (it.Kind != DockItemKind.Separator && hover > 0)
            {
                double d = Math.Abs(mx - center);
                double f = d >= range ? 0 : (Math.Cos(Math.PI * d / range) + 1) / 2;
                it.Width *= 1 + k * hover * f;
            }
            it.Slot = it.Width + Gap * it.Presence;

            double bs = baseStart + it.SlotStart;
            if (mx >= bs && mx <= bs + baseSlot && baseSlot > 0)
                mapped = baseStart + magPos + (mx - bs) / baseSlot * it.Slot;
            it.SlotStart = magPos;
            magPos += it.Slot;
        }

        // Keep the point under the cursor fixed; the Dock grows away from it on both sides.
        double origin = baseStart + (mx - mapped);
        foreach (var it in items) it.X = origin + it.SlotStart + Gap * it.Presence / 2;

        // After a reorder, start each item from where it was drawn and let DrawOffset ease it home.
        if (slideFrom != null)
        {
            foreach (var it in items)
                if (slideFrom.TryGetValue(it, out var oldX)) it.DrawOffset += oldX - it.X;
            slideFrom = null;
        }

        panelBottom = WinHDip - BottomGap + hideOffset;
        panelTop = panelBottom - PanelH;
        if (items.Count > 0)
        {
            panelL = items[0].X - PadX;
            panelR = items[^1].X + items[^1].Width + PadX;
        }
    }

    private double IconsTop()
    {
        double max = IconSize;
        foreach (var it in items) if (it.Kind != DockItemKind.Separator && it.Width > max) max = it.Width;
        return panelBottom - PadBottom - max;
    }

    private double Bounce(DockItem it, double now) =>
        it.BounceStart < 0 ? 0 : BounceHeight * Math.Abs(Math.Sin(Math.PI * (now - it.BounceStart) / BouncePeriod));

    /// <summary>While hovered/animating the whole window takes input; at rest only the Dock itself does.</summary>
    private void UpdateRegion(bool full)
    {
        if (hwnd == IntPtr.Zero) return;
        var edge = new Int32Rect(0, winH - 2, winW, 2);
        (Int32Rect a, Int32Rect? b) want;
        if (full) want = (new Int32Rect(0, 0, winW, winH), null);
        else if (cfg.AutoHideDock && hideOffset > 1) want = (edge, null);
        else
        {
            int l = (int)Math.Floor((panelL - 2) * scale), t = (int)Math.Floor((panelTop - 2) * scale);
            var dock = new Int32Rect(l, t, (int)Math.Ceiling((panelR - panelL + 4) * scale), winH - t);
            // An open auto-hide Dock keeps listening along the whole bottom edge so it notices when the pointer leaves.
            want = (dock, cfg.AutoHideDock ? edge : null);
        }
        if (region == want) return;
        region = want;

        static IntPtr Rgn(Int32Rect r) => Native.CreateRectRgn(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
        IntPtr rgn;
        if (full) rgn = IntPtr.Zero;
        else
        {
            rgn = Rgn(want.a);
            if (want.b is Int32Rect extra)
            {
                IntPtr other = Rgn(extra);
                Native.CombineRgn(rgn, rgn, other, 2 /* RGN_OR */);
                Native.DeleteObject(other);
            }
        }
        Native.SetWindowRgn(hwnd, rgn, true);
    }

    #endregion

    #region Rendering

    private sealed class Surface(DockWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.Draw(dc);
    }

    private void Draw(DrawingContext dc)
    {
        if (items.Count == 0 || winW == 0) return;
        double now = clock.Elapsed.TotalSeconds;

        dc.DrawRoundedRectangle(panelFill, panelStroke, new Rect(panelL, panelTop, Math.Max(0, panelR - panelL), PanelH), Radius, Radius);

        foreach (var it in items)
        {
            if (it.Presence < 0.01) continue;
            if (it.Kind == DockItemKind.Separator)
            {
                double cx = Math.Round(it.X + it.Width / 2) + 0.5;
                dc.PushOpacity(it.Presence);
                dc.DrawLine(sepPen, new Point(cx, panelTop + 12), new Point(cx, panelBottom - 12));
                dc.Pop();
                continue;
            }

            if (dragging && it == pressed) continue; // drawn last, on top
            double x = it.X + it.DrawOffset;
            DrawIcon(dc, it, new Rect(x, panelBottom - PadBottom - it.Width - Bounce(it, now), it.Width, it.Width), it == pressed && it == hot ? 0.6 : 1);
            if (it.Running)
                dc.DrawEllipse(dotFill, null, new Point(x + it.Width / 2, panelBottom - PadBottom / 2 + 0.5), 2, 2);
        }

        if (dragging && pressed != null)
        {
            double size = pressed.Width;
            double y = Math.Min(panelBottom - PadBottom - size, dragY - size / 2);
            DrawIcon(dc, pressed, new Rect(dragX - dragGrab, y, size, size), InRemoveZone() ? 0.45 : 0.9);
        }

        if (hot is { Kind: not DockItemKind.Separator } h && !menuOpen && pressed == null && preview?.IsOpen != true)
        {
            h.Label ??= new FormattedText(h.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(cfg.Font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                13, Brushes.White, scale);
            var ft = h.Label;
            double cx = h.X + h.Width / 2;
            double top = panelBottom - PadBottom - h.Width - Bounce(h, now) - 10 - (ft.Height + 8);
            var lr = new Rect(Math.Round(cx - ft.Width / 2 - 10), Math.Max(1, Math.Round(top)), Math.Ceiling(ft.Width + 20), Math.Ceiling(ft.Height + 8));
            dc.DrawRoundedRectangle(labelFill, labelStroke, lr, 7, 7);
            dc.DrawText(ft, new Point(lr.X + 10, lr.Y + 4));
        }
    }

    /// <summary>Icon plus its notification badge (app overlay badge, or a red dot when it wants attention).</summary>
    private void DrawIcon(DrawingContext dc, DockItem it, Rect r, double opacity)
    {
        double size = r.Width;
        if (opacity < 1) dc.PushOpacity(opacity);
        if (it.Icon != null) dc.DrawImage(it.Icon, r);
        else dc.DrawRoundedRectangle(placeholder, null, r, size * 0.22, size * 0.22);

        if (it.Kind == DockItemKind.App && it.Running)
        {
            var (overlay, attention) = Badges.For(it.Windows);
            double bs = Math.Round(size * 0.40);
            var br = new Rect(r.Right - bs * 0.78, r.Top - bs * 0.12, bs, bs);
            if (overlay != null)
            {
                dc.DrawEllipse(badgeStroke.Brush, null, new Point(br.X + bs / 2, br.Y + bs / 2), bs / 2 + 1.5, bs / 2 + 1.5);
                dc.DrawImage(overlay, br);
            }
            else if (attention)
            {
                double rad = bs * 0.36;
                dc.DrawEllipse(badgeFill, badgeStroke, new Point(br.X + bs / 2, br.Y + bs / 2), rad, rad);
            }

            // Download/copy progress as a bar across the bottom of the icon, like macOS' Finder and Safari.
            // Drawn statically ("busy" is a dimmed full bar): animating would keep the Dock rendering every frame.
            if (Badges.Progress(it.Windows) is { } p)
            {
                double h = Math.Max(4, Math.Round(size * 0.1)), w = Math.Round(size * 0.8);
                var track = new Rect(r.X + (size - w) / 2, r.Bottom - h - Math.Round(size * 0.08), w, h);
                dc.DrawRoundedRectangle(progressTrack, progressStroke, track, h / 2, h / 2);
                double done = p.value ?? 1;
                if (done > 0)
                {
                    var bar = new Rect(track.X + 1, track.Y + 1, Math.Max(h - 2, (track.Width - 2) * done), track.Height - 2);
                    var fill = p.kind switch
                    {
                        Badges.ProgressKind.Paused => progressPaused,
                        Badges.ProgressKind.Error => progressError,
                        Badges.ProgressKind.Busy => progressBusy,
                        _ => progressNormal,
                    };
                    dc.DrawRoundedRectangle(fill, null, bar, bar.Height / 2, bar.Height / 2);
                }
            }
        }
        if (opacity < 1) dc.Pop();
    }

    #endregion

    #region Input

    private DockItem? ItemAt(Point p)
    {
        if (!inside && !(p.X >= panelL && p.X <= panelR && p.Y >= panelTop)) return null;
        foreach (var it in items)
            if (!it.Removing && it.Presence > 0.5 && it.Kind != DockItemKind.Separator &&
                p.X >= it.X - Gap / 2 && p.X <= it.X + it.Width + Gap / 2)
                return it;
        return null;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (dragging)
        {
            EndDrag();
            return;
        }
        var it = ItemAt(e.GetPosition(surface));
        bool same = it != null && it == pressed;
        pressed = null;
        Wake();
        if (!same) return;
        HidePreview();
        Activate(it!, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Wake();
        if (pressed is not { Kind: DockItemKind.App } it || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(surface);
        if (!dragging)
        {
            if (Math.Abs(p.X - pressX) < 6) return;
            dragging = true;
            dragGrab = pressX - it.X;
            HidePreview();
        }
        dragX = p.X;
        dragY = p.Y;
        Reorder(it, p.X);
    }

    private bool InRemoveZone() => pressed?.Pin != null && dragY < panelTop - MaxSize - 10;

    /// <summary>Moves the dragged app to the pinned slot under the cursor (pinning it if it was only running).</summary>
    private void Reorder(DockItem it, double x)
    {
        var pinnedItems = items.Where(i => i.Pin != null && !i.Removing && i != it).ToList();
        int target = pinnedItems.Count(i => i.X + i.Width / 2 < x);
        bool overPinned = pinnedItems.Count == 0 || x < pinnedItems[^1].X + pinnedItems[^1].Width + IconSize;
        if (it.Pin == null && !overPinned) return; // running app dragged around the running section: nothing to do

        var pin = it.Pin ?? new PinnedApp { Path = it.App?.ExePath, Aumid = it.App?.Aumid };
        int current = cfg.Pinned.IndexOf(pin);
        // Convert the visible slot to an index in cfg.Pinned (duplicates are skipped when building).
        int index = target < pinnedItems.Count ? cfg.Pinned.IndexOf(pinnedItems[target].Pin!) : cfg.Pinned.Count;
        if (current >= 0 && current < index) index--;
        if (current == index) return;

        slideFrom = items.ToDictionary(i => i, i => i.X + i.DrawOffset);
        if (current >= 0) cfg.Pinned.RemoveAt(current);
        cfg.Pinned.Insert(Math.Clamp(index, 0, cfg.Pinned.Count), pin);
        Rebuild();
    }

    private void EndDrag()
    {
        var it = pressed;
        bool remove = InRemoveZone();
        dragging = false;
        pressed = null;
        if (it?.Pin != null)
        {
            slideFrom = items.ToDictionary(i => i, i => i.X + i.DrawOffset);
            if (it.Pin != null) slideFrom[it] = dragX - dragGrab;
            if (remove) cfg.Pinned.Remove(it.Pin!);
            cfg.Save();
            Rebuild();
        }
        Wake();
    }

    #region Window previews

    private PreviewWindow? preview;
    private readonly System.Windows.Threading.DispatcherTimer previewTimer = new();
    private bool previewShowPending;

    /// <summary>Hovering a running app shows its windows after a short dwell; moving between apps switches instantly.</summary>
    private void OnHotChanged()
    {
        if (!cfg.WindowPreviews || dragging || menuOpen) return;
        if (hot is { Kind: DockItemKind.App, Running: true } h)
        {
            if (preview?.IsOpen == true) ShowPreview(h);
            else ArmPreviewTimer(show: true, 0.45);
        }
        else ArmPreviewTimer(show: false, 0.35);
    }

    private void ArmPreviewTimer(bool show, double seconds)
    {
        previewShowPending = show;
        previewTimer.Stop();
        previewTimer.Interval = TimeSpan.FromSeconds(seconds);
        previewTimer.Start();
    }

    private void OnPreviewTimer()
    {
        previewTimer.Stop();
        if (previewShowPending)
        {
            if (hot is { Kind: DockItemKind.App, Running: true } h && !dragging && !menuOpen && pressed == null) ShowPreview(h);
        }
        else if (preview?.IsOpen == true && !preview.IsMouseOver && hot is not { Kind: DockItemKind.App, Running: true })
            preview.HidePreview();
    }

    private void ShowPreview(DockItem it)
    {
        if (preview == null)
        {
            preview = new PreviewWindow(cfg.Font);
            preview.PointerLeft += () => ArmPreviewTimer(show: false, 0.3);
        }
        var anchor = new Point(winX + (it.X + it.Width / 2) * scale, winY + (IconsTop() - 6) * scale);
        preview.ShowFor(it.Windows, anchor, scale, monitorBounds);
    }

    private void HidePreview()
    {
        previewTimer.Stop();
        preview?.HidePreview();
    }

    #endregion

    /// <summary>An app asking for attention bounces and, like macOS, briefly reveals a hidden Dock.</summary>
    private void OnBadgesChanged()
    {
        double now = clock.Elapsed.TotalSeconds;
        foreach (var it in items)
        {
            bool attention = it.Kind == DockItemKind.App && it.Running && Badges.For(it.Windows).attention;
            if (attention && !it.Attention)
            {
                it.BounceStart = now;
                it.BounceEnd = now + BouncePeriod * 3;
                it.LaunchedCold = false;
                revealUntil = now + 3;
            }
            it.Attention = attention;
        }
        Wake();
    }

    private void Activate(DockItem it, bool newInstance)
    {
        if (it.Kind == DockItemKind.Trash) { Shell.Open("explorer.exe", "shell:RecycleBinFolder"); return; }
        if (!it.Running || newInstance) { Launch(it); return; }

        var wins = it.Windows;
        var fg = Native.GetForegroundWindow();
        if (wins.Contains(fg))
        {
            if (wins.Count > 1) Shell.Focus(wins[^1]); // cycle through the app's windows
            else Shell.Minimize(fg);
        }
        else
        {
            var target = wins.FirstOrDefault(w => !Native.IsIconic(w));
            Shell.Focus(target != IntPtr.Zero ? target : wins[0]);
        }
    }

    private void Launch(DockItem it)
    {
        double now = clock.Elapsed.TotalSeconds;
        it.BounceStart = now;
        it.BounceEnd = now + BouncePeriod * 4;
        it.LaunchedCold = !it.Running;
        Wake();

        string? aumid = it.Pin?.Aumid ?? it.App?.Aumid;
        if (aumid != null) { Shell.OpenAumid(aumid); return; }
        string? path = it.Pin?.Path ?? it.App?.ExePath;
        if (path != null) Shell.Open(path, it.Pin?.Arguments);
    }

    private void ShowMenu(DockItem? it)
    {
        HidePreview();
        var m = new ContextMenu();
        if (it == null)
        {
            m.Items.Add(Ui.Item("Ampliação", () => { cfg.Magnification = !cfg.Magnification; cfg.Save(); SettingsChanged?.Invoke(); }, cfg.Magnification));
            m.Items.Add(Ui.Item("Ocultar automaticamente", () => { cfg.AutoHideDock = !cfg.AutoHideDock; cfg.Save(); SettingsChanged?.Invoke(); }, cfg.AutoHideDock));
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Preferências do Doca…", App.ShowSettings));
        }
        else if (it.Kind == DockItemKind.Trash)
        {
            m.Items.Add(Ui.Item("Abrir", () => Activate(it, false)));
            m.Items.Add(Ui.Item("Esvaziar Lixeira", () =>
            {
                Native.SHEmptyRecycleBin(hwnd, null, 0);
                Shell.ForgetIcon(Shell.RecycleBin);
                it.IconSource = null;
                SetIcon(it, Shell.RecycleBin);
            }));
        }
        else
        {
            foreach (var w in it.Windows.Take(12))
            {
                string title = Native.WindowTitle(w);
                if (title.Length > 60) title = title[..57] + "…";
                var win = w;
                m.Items.Add(Ui.Item(title, () => Shell.Focus(win)));
            }
            if (it.Running) m.Items.Add(new Separator());

            m.Items.Add(Ui.Item(it.Running ? "Nova janela" : "Abrir", () => Launch(it)));
            if (it.Pin != null)
                m.Items.Add(Ui.Item("Remover da Dock", () => { cfg.Pinned.Remove(it.Pin); cfg.Save(); Rebuild(); }));
            else if (it.App != null)
                m.Items.Add(Ui.Item("Manter na Dock", () =>
                {
                    cfg.Pinned.Add(new PinnedApp { Path = it.App.ExePath, Aumid = it.App.Aumid });
                    cfg.Save();
                    Rebuild();
                }));

            string? path = it.Pin?.ResolvedPath ?? it.Pin?.Path ?? it.App?.ExePath;
            if (path != null && File.Exists(path))
                m.Items.Add(Ui.Item("Mostrar no Explorador", () => Shell.Reveal(path)));

            if (it.Running)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Ui.Item("Encerrar", () => Shell.CloseAll(it.Windows, it.Name)));
                // Apps that only hide to the tray on close (Discord, Steam…) need this to really quit.
                m.Items.Add(Ui.Item("Forçar Encerramento", () => Shell.ForceQuit(it.App?.ExePath, it.Windows.ToList(), it.Name)));
            }
        }

        var anchor = it != null
            ? new Rect(it.X, panelTop - 6, it.Width, 1)
            : new Rect(Mouse.GetPosition(surface).X, panelTop - 6, 1, 1);
        m.PlacementTarget = surface;
        m.Placement = PlacementMode.Top;
        m.PlacementRectangle = anchor;
        m.Opened += (_, _) =>
        {
            menuOpen = true;
            m.HorizontalOffset = (anchor.Width - m.ActualWidth) / 2;
            Wake();
        };
        m.Closed += (_, _) => { menuOpen = false; Wake(); };
        Ui.CloseOnOutsideClick(m);
        m.IsOpen = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        Wake();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files)
        {
            var p = new PinnedApp { Path = f };
            cfg.Resolve(p);
            if (cfg.Pinned.Any(x => x.Key == p.Key)) continue;
            cfg.Pinned.Add(p);
        }
        cfg.Save();
        Rebuild();
    }

    #endregion
}
