
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Doca;

// Island features beyond the basics: file shelf, clipboard history, camera/microphone in use, browser
// downloads and a timer. Each source is event driven (window messages, registry and file system
// notifications); the only timers run while something is actually counting (a timer, a download).

internal static class ExtraGlyphs
{
    // Segoe Fluent Icons code points.
    public const string Folder = "", Clipboard = "", Timer = "", Download = "", Mic = "", Camera = "",
        Pin = "", Unpin = "", Delete = "", Play = "", Pause = "", Cancel = "", Add = "",
        Done = "", Photo = "";
}

#region Shelf

/// <summary>Files dropped on the island, kept (by path, not copied) until dragged out or removed.</summary>
internal static class Shelf
{
    private static DocaConfig? cfg;
    public static event Action? Changed;
    public static IReadOnlyList<string> Items => cfg?.ShelfFiles ?? (IReadOnlyList<string>)Array.Empty<string>();

    public static void Start(DocaConfig c)
    {
        cfg = c;
        // Files moved or deleted since last time are gone from the shelf.
        if (cfg.ShelfFiles.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0) cfg.Save();
    }

    public static void Add(IEnumerable<string> paths)
    {
        if (cfg == null) return;
        foreach (var p in paths)
        {
            cfg.ShelfFiles.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
            cfg.ShelfFiles.Insert(0, p);
        }
        cfg.Save();
        Changed?.Invoke();
    }

    public static void Remove(string path)
    {
        if (cfg == null || cfg.ShelfFiles.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        cfg.Save();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        if (cfg == null || cfg.ShelfFiles.Count == 0) return;
        cfg.ShelfFiles.Clear();
        cfg.Save();
        Changed?.Invoke();
    }
}

#endregion

#region Clipboard history

internal sealed class ClipEntry
{
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public BitmapSource? Thumb { get; init; }
    public string[]? Files { get; init; }
    public bool Pinned { get; set; }
}

/// <summary>
/// Recent clipboard contents (text, images, files) through AddClipboardFormatListener. Kept in memory only;
/// pinned texts are saved in the config. Content that password managers mark as private is skipped.
/// </summary>
internal static class ClipboardHistory
{
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    private const int WM_CLIPBOARDUPDATE = 0x031D, MaxItems = 30, MaxImages = 6, MaxText = 20_000;

    private static DocaConfig? cfg;
    private static HwndSource? window;
    private static readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private static int ignoreNext;
    public static readonly List<ClipEntry> Items = new();
    public static event Action? Changed;

    public static void Start(DocaConfig c)
    {
        if (window != null) return;
        cfg = c;
        foreach (var t in cfg.ClipboardPinned) Items.Add(new ClipEntry { Text = t, Pinned = true });
        try
        {
            window = new HwndSource(new HwndSourceParameters("Doca.Clipboard") { Width = 0, Height = 0, WindowStyle = 0 });
            window.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                // The owner may still be adding formats: read a moment later, once.
                if (msg == WM_CLIPBOARDUPDATE) { settle.Stop(); settle.Start(); }
                return IntPtr.Zero;
            });
            settle.Tick += (_, _) => { settle.Stop(); Read(); };
            AddClipboardFormatListener(window.Handle);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void Read()
    {
        if (ignoreNext > 0) { ignoreNext--; return; }
        if (cfg?.IslandClipboard == false) return;
        IDataObject? data = null;
        for (int i = 0; i < 3 && data == null; i++)
        {
            try { data = System.Windows.Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(30); } // another app has it open
        }
        if (data == null) return;
        try
        {
            // Password managers and Windows' own history honor these: so do we.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms
                && ms.Length >= 4 && new BinaryReader(ms).ReadInt32() == 0) return;

            ClipEntry? e = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                e = new ClipEntry { Files = files };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
                e = new ClipEntry { Text = text.Length > MaxText ? text[..MaxText] : text };
            else if (data.GetDataPresent(DataFormats.Bitmap) && System.Windows.Clipboard.GetImage() is BitmapSource img)
            {
                var full = new WriteableBitmap(img);
                full.Freeze();
                double s = Math.Min(1, 240.0 / Math.Max(full.PixelWidth, full.PixelHeight));
                var thumb = new TransformedBitmap(full, new ScaleTransform(s, s));
                thumb.Freeze();
                e = new ClipEntry { Image = full, Thumb = thumb };
            }
            if (e == null) return;
            Add(e);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void Add(ClipEntry e)
    {
        // The same text again moves to the top (keeping its pin).
        if (e.Text != null)
        {
            var same = Items.FirstOrDefault(x => x.Text == e.Text);
            if (same != null) { Items.Remove(same); e.Pinned = same.Pinned; }
        }
        Items.Insert(0, e);
        // Limits: unpinned entries beyond 30, images beyond 6 (a screenshot is megabytes).
        var images = Items.Where(x => x.Image != null).Skip(MaxImages).ToList();
        foreach (var x in images) Items.Remove(x);
        while (Items.Count(x => !x.Pinned) > MaxItems) Items.Remove(Items.Last(x => !x.Pinned));
        Changed?.Invoke();
    }

    public static void Copy(ClipEntry e)
    {
        try
        {
            ignoreNext++;
            if (e.Files != null)
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(e.Files);
                System.Windows.Clipboard.SetFileDropList(list);
            }
            else if (e.Image != null) System.Windows.Clipboard.SetImage(e.Image);
            else System.Windows.Clipboard.SetText(e.Text ?? "");
            // Back to the top, like copying it again.
            Items.Remove(e);
            Items.Insert(0, e);
            Changed?.Invoke();
        }
        catch (Exception ex) { ignoreNext = 0; App.Log(ex); }
    }

    public static void TogglePin(ClipEntry e)
    {
        if (e.Text == null) return; // only texts persist
        e.Pinned = !e.Pinned;
        SavePinned();
        Changed?.Invoke();
    }

    public static void Remove(ClipEntry e)
    {
        Items.Remove(e);
        if (e.Pinned) SavePinned();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Items.RemoveAll(x => !x.Pinned);
        Changed?.Invoke();
    }

    private static void SavePinned()
    {
        if (cfg == null) return;
        cfg.ClipboardPinned = Items.Where(x => x.Pinned && x.Text != null).Select(x => x.Text!).ToList();
        cfg.Save();
    }
}

#endregion

#region Camera / microphone in use

/// <summary>
/// Camera and microphone in use, from the registry entries Windows keeps for its own privacy indicator
/// (CapabilityAccessManager: LastUsedTimeStop = 0 while an app is using the device). RegNotifyChangeKeyValue
/// wakes a background thread on changes: no polling.
/// </summary>
internal static class PrivacyWatch
{
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, uint filter, SafeWaitHandle evt, bool async);
    private const uint REG_NOTIFY_CHANGE_NAME = 1, REG_NOTIFY_CHANGE_LAST_SET = 4;
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static string? MicApp { get; private set; }
    public static string? CamApp { get; private set; }
    /// <summary>Raised on the UI thread.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "Doca.Privacy" };
        t.Start();
    }

    private static void Run()
    {
        try
        {
            using var mic = Registry.CurrentUser.OpenSubKey(Store + "microphone");
            using var cam = Registry.CurrentUser.OpenSubKey(Store + "webcam");
            var keys = new[] { mic, cam }.Where(k => k != null).Cast<RegistryKey>().ToArray();
            if (keys.Length == 0) return;
            var events = keys.Select(_ => new AutoResetEvent(false)).ToArray();
            Publish(mic, cam);
            while (true)
            {
                // Re-armed each round: a notification fires once.
                for (int i = 0; i < keys.Length; i++)
                    RegNotifyChangeKeyValue(keys[i].Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, events[i].SafeWaitHandle, true);
                WaitHandle.WaitAny(events);
                Thread.Sleep(150); // a start writes several values
                Publish(mic, cam);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void Publish(RegistryKey? mic, RegistryKey? cam)
    {
        string? m = InUse(mic), c = InUse(cam);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (m == MicApp && c == CamApp) return;
            bool micStarted = m != null && MicApp == null, camStarted = c != null && CamApp == null;
            MicApp = m;
            CamApp = c;
            Changed?.Invoke();
            if (Island.Config?.IslandPrivacy == false) return;
            if (camStarted) Island.Notify(new StatusActivity(ExtraGlyphs.Camera, c!, "Usando a câmera", true));
            else if (micStarted) Island.Notify(new StatusActivity(ExtraGlyphs.Mic, m!, "Usando o microfone", true, StateBrush: IslandBrushes.Orange));
        });
    }

    /// <summary>Name of an app using the device right now, or null.</summary>
    private static string? InUse(RegistryKey? store)
    {
        if (store == null) return null;
        try
        {
            foreach (var name in store.GetSubKeyNames())
            {
                using var k = store.OpenSubKey(name);
                if (k == null) continue;
                if (name == "NonPackaged")
                {
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        using var app = k.OpenSubKey(sub);
                        // "C:#Program Files#Discord#Discord.exe"
                        var exe = sub.Replace('#', '\\');
                        if (app != null && Active(app) && Running(exe)) return Path.GetFileNameWithoutExtension(exe);
                    }
                }
                else if (Active(k)) return name.Split('_')[0].Split('.').Last(); // "Microsoft.WindowsCamera_8wekyb3d8bbwe"
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return null;
    }

    /// <summary>
    /// Started and not stopped. An app that crashed or was killed never writes the stop time, so its entry says
    /// "in use" forever: only uses that began since the PC started count.
    /// </summary>
    private static bool Active(RegistryKey k) =>
        k.GetValue("LastUsedTimeStart") is long start && start != 0 && k.GetValue("LastUsedTimeStop") is long stop && stop == 0
        && DateTime.FromFileTime(start) > BootTime;

    private static readonly DateTime BootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The program is actually open (a crash in this same session would also leave a stale entry).</summary>
    private static bool Running(string exe)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; } // elevated or protected process: the same name is good enough
            }
        }
        return false;
    }
}

#endregion

#region Downloads

/// <summary>
/// Browser downloads in the Downloads folder: Chrome/Edge write "name.crdownload" and Firefox "name.part",
/// renamed to the final name when done. FileSystemWatcher only; while something downloads, a 1 s timer reads
/// its size for the pill.
/// </summary>
internal static class DownloadWatch
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out string path);

    private static FileSystemWatcher? watcher;
    private static readonly Dictionary<string, long> active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public static int Count => active.Count;
    public static long Bytes => active.Values.Sum();
    /// <summary>Bytes per second over all active downloads (smoothed).</summary>
    public static double Speed { get; private set; }
    /// <summary>Active downloads: display name and bytes so far.</summary>
    public static IEnumerable<(string name, long bytes)> Active => active.Select(kv => (DisplayName(kv.Key), kv.Value));
    /// <summary>Finished in this session, newest first (at most 5).</summary>
    public static readonly List<string> Recent = new();
    private static long lastBytes;
    private static DateTime lastTime;
    /// <summary>Raised on the UI thread: started, finished, or size changed.</summary>
    public static event Action? Changed;

    private static bool Partial(string path) =>
        path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

    public static void Start()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var folder) != 0 || !Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            watcher.Created += (_, e) => OnUi(() => { if (Partial(e.FullPath)) Begin(e.FullPath); });
            watcher.Renamed += (_, e) => OnUi(() =>
            {
                bool was = Partial(e.OldFullPath), now = Partial(e.FullPath);
                if (was && now) { active.Remove(e.OldFullPath); Begin(e.FullPath, announce: false); } // "Unconfirmed 123" → real name
                else if (was) Finish(e.OldFullPath, e.FullPath);
                else if (now) Begin(e.FullPath);
            });
            watcher.Deleted += (_, e) => OnUi(() =>
            {
                if (!active.Remove(e.FullPath)) return;
                // Firefox may delete the .part after writing the final file; otherwise it was cancelled.
                var final = e.FullPath[..e.FullPath.LastIndexOf('.')];
                if (File.Exists(final) && DateTime.Now - File.GetLastWriteTime(final) < TimeSpan.FromSeconds(10)) Done(final);
                Update();
            });
            watcher.EnableRaisingEvents = true;
            tick.Tick += (_, _) => Measure();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public static string DisplayName(string partial)
    {
        var name = Path.GetFileName(partial);
        name = name[..name.LastIndexOf('.')];
        // Chrome's placeholder before the real name is known.
        return name.StartsWith("Unconfirmed ") || name.StartsWith("Não confirmado ") ? "Download" : name;
    }

    private static void Begin(string path, bool announce = true)
    {
        if (active.ContainsKey(path)) return;
        active[path] = 0;
        if (announce && Island.Config?.IslandDownloads != false) Island.Notify(new StatusActivity(ExtraGlyphs.Download, DisplayName(path), "Baixando", false));
        Update();
    }

    private static void Finish(string partial, string final)
    {
        active.Remove(partial);
        Done(final);
        Update();
    }

    private static void Done(string final)
    {
        Recent.RemoveAll(x => x.Equals(final, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, final);
        if (Recent.Count > 5) Recent.RemoveAt(5);
        if (Island.Config?.IslandDownloads == false) return;
        Island.Notify(new StatusActivity(ExtraGlyphs.Download, Path.GetFileName(final), "Concluído", true, Seconds: 5,
            Click: () => Shell.Reveal(final)));
    }

    private static void Update()
    {
        if (active.Count > 0) { Measure(); tick.Start(); }
        else tick.Stop();
        Changed?.Invoke();
    }

    private static void Measure()
    {
        foreach (var p in active.Keys.ToList())
        {
            try { active[p] = new FileInfo(p).Length; }
            catch { /* renamed meanwhile */ }
        }
        // Browsers write in bursts: an exponential average keeps the speed readable.
        var now = DateTime.Now;
        long bytes = Bytes;
        double secs = (now - lastTime).TotalSeconds;
        if (active.Count == 0) Speed = 0;
        else if (secs > 0.3 && secs < 5 && bytes >= lastBytes) Speed = Speed * 0.6 + (bytes - lastBytes) / secs * 0.4;
        lastBytes = bytes;
        lastTime = now;
        Changed?.Invoke();
    }

    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / (1 << 20):0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s";

    public static string Size(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0} MB" : $"{bytes / 1024.0:0} KB";
}

#endregion

#region Timer

internal static class IslandTimer
{
    private static readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static DateTime end;
    private static TimeSpan left;

    public static bool Running { get; private set; }
    public static bool Paused { get; private set; }
    public static TimeSpan Total { get; private set; }
    public static TimeSpan Left
    {
        get
        {
            if (Paused) return left;
            if (!Running) return TimeSpan.Zero;
            var l = end - DateTime.Now;
            return l > TimeSpan.Zero ? l : TimeSpan.Zero;
        }
    }
    public static bool Active => Running || Paused;
    /// <summary>Raised on each second and on start/stop.</summary>
    public static event Action? Changed;

    static IslandTimer() => tick.Tick += (_, _) => Tick();

    public static void Start(TimeSpan span)
    {
        Total = span;
        end = DateTime.Now + span;
        Running = true;
        Paused = false;
        tick.Start();
        Changed?.Invoke();
    }

    public static void TogglePause()
    {
        if (!Active) return;
        if (Paused) { end = DateTime.Now + left; Paused = false; tick.Start(); }
        else { left = Left; Paused = true; tick.Stop(); }
        Changed?.Invoke();
    }

    public static void AddMinute()
    {
        if (!Active) return;
        Total += TimeSpan.FromMinutes(1);
        if (Paused) left += TimeSpan.FromMinutes(1); else end += TimeSpan.FromMinutes(1);
        Changed?.Invoke();
    }

    public static void Cancel()
    {
        Running = Paused = false;
        tick.Stop();
        Changed?.Invoke();
    }

    private static int lastSecond = -1;

    private static void Tick()
    {
        var l = Left;
        if (l <= TimeSpan.Zero)
        {
            Cancel();
            Island.Notify(new StatusActivity(ExtraGlyphs.Timer, "Timer", "Acabou o tempo", true, Seconds: 8));
            // Three chimes, a second apart.
            int n = 0;
            var chime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            chime.Tick += (_, _) => { SystemSounds.Exclamation.Play(); if (++n >= 3) chime.Stop(); };
            SystemSounds.Exclamation.Play();
            n++;
            chime.Start();
            return;
        }
        int s = (int)Math.Ceiling(l.TotalSeconds);
        if (s != lastSecond) { lastSecond = s; Changed?.Invoke(); }
    }

    public static string Format(TimeSpan t)
    {
        int s = (int)Math.Ceiling(t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}

#endregion

#region Views (inside the island's panel)

internal static class IslandBrushes
{
    public static readonly Brush Card = F(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Dim = F(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Faint = F(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Orange = F(Color.FromRgb(0xFF, 0x9F, 0x0A));
    public static readonly Brush Red = F(Color.FromRgb(0xFF, 0x45, 0x3A));
    public static readonly Brush Green = F(Color.FromRgb(0x30, 0xD1, 0x58));

    private static string? accentKey;
    private static Brush accent = Brushes.White;
    /// <summary>Island accent (Preferências → Topo da tela): sliders, level and progress bars, selected tab.</summary>
    public static Brush Accent
    {
        get
        {
            var key = Island.Config?.IslandAccent ?? "#FFFFFF";
            if (key != accentKey) { accentKey = key; accent = F(Parse(key, Colors.White)); }
            return accent;
        }
    }

    public static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    /// <summary>Black or white, whichever reads on this color.</summary>
    public static Brush On(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Brushes.Black : Brushes.White;
    private static Brush F(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static TextBlock Glyph(string g, double size) => new()
    {
        Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>A rounded button with the panel's hover/press feedback.</summary>
    public static Border Button(UIElement content, Action click, string? tip = null, double radius = 14, Thickness? padding = null)
    {
        var b = new Border
        {
            Child = content, CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(10, 5, 10, 6),
            Cursor = Cursors.Hand, ToolTip = tip, Background = Card,
        };
        b.MouseEnter += (_, _) => b.Background = Faint;
        b.MouseLeave += (_, _) => b.Background = Card;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    public static TextBlock Text(string s, double size = 12, Brush? brush = null) => new()
    {
        Text = s, FontSize = size, Foreground = brush ?? Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };
}

/// <summary>Arquivos: the shelf. Drop files on the island to keep them here; drag them out to use them.</summary>
internal sealed class ShelfView : StackPanel
{
    private readonly WrapPanel grid = new();
    private readonly Action hold, release;
    /// <summary>Tiles picked with a click: a drag of any of them carries them all.</summary>
    private readonly List<string> selected = new();

    /// <param name="hold">Called while a file is being dragged out (the island must stay open).</param>
    public ShelfView(Action hold, Action release)
    {
        this.hold = hold;
        this.release = release;
        Children.Add(grid);
        Loaded += (_, _) => { Shelf.Changed += Fill; Fill(); };
        Unloaded += (_, _) => Shelf.Changed -= Fill;
    }

    private void Fill()
    {
        grid.Children.Clear();
        while (Children.Count > 1) Children.RemoveAt(1);
        if (Shelf.Items.Count == 0)
        {
            // Dashed drop zone.
            var zone = new Grid { Width = 404, Height = 110 };
            zone.Children.Add(new System.Windows.Shapes.Rectangle
            {
                RadiusX = 16, RadiusY = 16, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
                Stroke = IslandBrushes.Faint,
            });
            zone.Children.Add(Hint());
            grid.Children.Add(zone);
            return;
        }
        selected.RemoveAll(x => !Shelf.Items.Contains(x));
        foreach (var p in Shelf.Items) grid.Children.Add(Tile(p));
        var footer = new DockPanel { Margin = new Thickness(2, 8, 2, 0), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        if (selected.Count > 0)
        {
            footer.Children.Add(IslandBrushes.Text($"{selected.Count} {(selected.Count == 1 ? "selecionado" : "selecionados")} · arraste um para levar todos", 11.5, IslandBrushes.Dim));
            buttons.Children.Add(IslandBrushes.Button(IslandBrushes.Text("Desmarcar", 11.5), () => { selected.Clear(); Fill(); }));
            var remove = IslandBrushes.Button(IslandBrushes.Text("Tirar", 11.5), () => { foreach (var x in selected.ToList()) Shelf.Remove(x); selected.Clear(); Fill(); }, "Tira os selecionados da prateleira");
            remove.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(remove);
        }
        else
        {
            footer.Children.Add(IslandBrushes.Text($"{Shelf.Items.Count} {(Shelf.Items.Count == 1 ? "item" : "itens")} · clique para selecionar, arraste para usar", 11.5, IslandBrushes.Dim));
            buttons.Children.Add(IslandBrushes.Button(IslandBrushes.Text("Limpar", 11.5), Shelf.Clear));
        }
        footer.Children.Add(buttons);
        Children.Add(footer);
    }

    private static UIElement Hint()
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(new TextBlock { Text = ExtraGlyphs.Folder, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
        s.Children.Add(new TextBlock { Text = "Arraste arquivos até a ilha para guardá-los aqui", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) });
        return s;
    }

    private UIElement Tile(string path)
    {
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform };
        Shell.RequestIcon(path, img => icon.Source = img, 64);
        var name = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\')), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, Margin = new Thickness(0, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(name);
        bool isSelected = selected.Contains(path);
        // Selected: accent outline and a check in the corner.
        var check = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = IslandBrushes.Accent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -2, 0), Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock { Text = Glyphs.Check, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = IslandBrushes.On(((SolidColorBrush)IslandBrushes.Accent).Color) },
        };
        var layered = new Grid();
        layered.Children.Add(stack);
        layered.Children.Add(check);
        var tile = new Border
        {
            Child = layered, Width = 94, Padding = new Thickness(6, 10, 6, 8), Margin = new Thickness(0, 0, 9, 9),
            CornerRadius = new CornerRadius(14), Background = IslandBrushes.Card, ToolTip = path, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1.5), BorderBrush = isSelected ? IslandBrushes.Accent : Brushes.Transparent,
        };
        tile.MouseEnter += (_, _) => tile.Background = IslandBrushes.Faint;
        tile.MouseLeave += (_, _) => tile.Background = IslandBrushes.Card;

        Point? down = null;
        bool dragged = false;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            down = e.GetPosition(tile);
            dragged = false;
            if (e.ClickCount == 2) { down = null; dragged = true; Shell.Open(path); }
        };
        tile.MouseMove += (_, e) =>
        {
            if (down is not Point d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(tile);
            if (Math.Abs(p.X - d.X) < 4 && Math.Abs(p.Y - d.Y) < 4) return;
            down = null;
            dragged = true;
            // A selected tile carries the whole selection.
            var paths = selected.Contains(path) && selected.Count > 1 ? selected.ToArray() : new[] { path };
            hold();
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                var result = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                // Moved somewhere else: not here anymore.
                foreach (var x in paths)
                    if (result == DragDropEffects.Move || (!File.Exists(x) && !Directory.Exists(x))) Shelf.Remove(x);
            }
            finally { release(); }
        };
        // A plain click (no drag) selects or unselects.
        tile.MouseLeftButtonUp += (_, e) =>
        {
            bool click = down != null && !dragged;
            down = null;
            if (!click) return;
            e.Handled = true;
            if (!selected.Remove(path)) selected.Add(path);
            Fill();
        };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu();
            m.Items.Add(Ui.Item("Abrir", () => Shell.Open(path)));
            m.Items.Add(Ui.Item("Mostrar na pasta", () => Shell.Reveal(path)));
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Tirar da prateleira", () => Shelf.Remove(path)));
            m.PlacementTarget = tile;
            Ui.CloseOnOutsideClick(m);
            m.IsOpen = true;
        };
        return tile;
    }
}

/// <summary>Copiados: clipboard history. Click copies again; pin keeps a text for good.</summary>
internal sealed class ClipboardView : StackPanel
{
    private readonly StackPanel list = new();
    private readonly ScrollViewer scroll;
    private readonly Action dismiss, hold, release;
    private static readonly string[] kinds = { "Tudo", "Textos", "Links", "Imagens", "Arquivos" };
    private static string kind = "Tudo";
    private readonly StackPanel chips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

    public ClipboardView(Action dismiss, Action hold, Action release)
    {
        this.dismiss = dismiss;
        this.hold = hold;
        this.release = release;
        scroll = new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, -6, 0), Padding = new Thickness(0, 0, 6, 0) };
        Children.Add(chips);
        Children.Add(scroll);
        Loaded += (_, _) => { ClipboardHistory.Changed += Fill; Fill(); };
        Unloaded += (_, _) => ClipboardHistory.Changed -= Fill;
    }

    private void Fill()
    {
        list.Children.Clear();
        while (Children.Count > 2) Children.RemoveAt(2);
        // Type chips (only when there is something to filter).
        chips.Children.Clear();
        chips.Visibility = ClipboardHistory.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var k in kinds)
        {
            int count = ClipboardHistory.Items.Count(x => Is(x, k));
            if (k != "Tudo" && count == 0) continue;
            var chip = IslandBrushes.Button(IslandBrushes.Text(k, 11.5), () => { kind = k; Fill(); }, null, 12, new Thickness(10, 4, 10, 5));
            chip.Margin = new Thickness(0, 0, 6, 0);
            if (k == kind) { chip.Background = IslandBrushes.Accent; ((TextBlock)chip.Child).Foreground = IslandBrushes.On(((SolidColorBrush)IslandBrushes.Accent).Color); chip.MouseLeave += (_, _) => chip.Background = IslandBrushes.Accent; chip.MouseEnter += (_, _) => chip.Background = IslandBrushes.Accent; }
            chips.Children.Add(chip);
        }
        if (!kinds.Contains(kind) || (kind != "Tudo" && !ClipboardHistory.Items.Any(x => Is(x, kind)))) kind = "Tudo";
        var items = ClipboardHistory.Items.Where(x => Is(x, kind)).OrderByDescending(x => x.Pinned).ToList();
        if (items.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Nada copiado ainda. O que você copiar (Ctrl+C) aparece aqui.", Opacity = 0.55, Margin = new Thickness(4, 8, 4, 10), HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }
        foreach (var e in items) list.Children.Add(Row(e));
        var footer = new DockPanel { Margin = new Thickness(2, 8, 2, 0), LastChildFill = false };
        footer.Children.Add(IslandBrushes.Text("Clique para copiar de novo, ou arraste para um app", 11.5, IslandBrushes.Dim));
        var clear = IslandBrushes.Button(IslandBrushes.Text("Limpar", 11.5), ClipboardHistory.Clear, "Apaga tudo, menos os fixados");
        DockPanel.SetDock(clear, Dock.Right);
        footer.Children.Add(clear);
        Children.Add(footer);
    }

    private static bool IsLink(string? t)
    {
        t = t?.Trim();
        return t != null && !t.Contains(' ') && !t.Contains('\n')
            && (t.StartsWith("http://") || t.StartsWith("https://") || t.StartsWith("www."));
    }

    private static bool Is(ClipEntry e, string k) => k switch
    {
        "Textos" => e.Text != null && !IsLink(e.Text),
        "Links" => IsLink(e.Text),
        "Imagens" => e.Image != null,
        "Arquivos" => e.Files != null,
        _ => true,
    };

    private UIElement Row(ClipEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string kind = e.Files != null ? ExtraGlyphs.Folder : e.Image != null ? ExtraGlyphs.Photo : e.Pinned ? ExtraGlyphs.Pin : ExtraGlyphs.Clipboard;
        var g = IslandBrushes.Glyph(kind, 13);
        g.Opacity = 0.7;
        grid.Children.Add(g);

        UIElement content;
        if (e.Thumb != null)
            content = new Border
            {
                CornerRadius = new CornerRadius(6), Height = Math.Min(90, e.Thumb.PixelHeight), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true,
                Width = Math.Min(90, e.Thumb.PixelHeight) * e.Thumb.PixelWidth / Math.Max(1, e.Thumb.PixelHeight),
                Background = new ImageBrush(e.Thumb) { Stretch = Stretch.UniformToFill },
            };
        else
        {
            string text = e.Files != null
                ? string.Join(", ", e.Files.Select(f => Path.GetFileName(f.TrimEnd('\\'))))
                : string.Join(" ", (e.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            content = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, VerticalAlignment = VerticalAlignment.Center };
        }
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Hidden };
        if (e.Text != null)
            actions.Children.Add(IslandBrushes.Button(IslandBrushes.Glyph(e.Pinned ? ExtraGlyphs.Unpin : ExtraGlyphs.Pin, 11), () => ClipboardHistory.TogglePin(e),
                e.Pinned ? "Desafixar" : "Fixar", 11, new Thickness(7, 5, 7, 5)));
        var del = IslandBrushes.Button(IslandBrushes.Glyph(ExtraGlyphs.Delete, 11), () => ClipboardHistory.Remove(e), "Apagar", 11, new Thickness(7, 5, 7, 5));
        del.Margin = new Thickness(4, 0, 0, 0);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var row = new Border { Child = grid, Padding = new Thickness(6, 8, 6, 8), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(12), Background = IslandBrushes.Card, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => { row.Background = IslandBrushes.Faint; actions.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { row.Background = IslandBrushes.Card; actions.Visibility = Visibility.Hidden; };
        // Click copies it again; dragging drops it straight into another app (text, image or files).
        Point? down = null;
        row.MouseLeftButtonDown += (_, ev) => down = ev.GetPosition(row);
        row.MouseMove += (_, ev) =>
        {
            if (down is not Point d || ev.LeftButton != MouseButtonState.Pressed) return;
            var p = ev.GetPosition(row);
            if (Math.Abs(p.X - d.X) < 5 && Math.Abs(p.Y - d.Y) < 5) return;
            down = null;
            if (DragData(e) is not { } data) return;
            hold();
            try { DragDrop.DoDragDrop(row, data, DragDropEffects.Copy); }
            catch (Exception ex) { App.Log(ex); }
            finally { release(); }
        };
        row.MouseLeftButtonUp += (_, ev) =>
        {
            if (down == null) return; // end of a drag
            down = null;
            ev.Handled = true;
            ClipboardHistory.Copy(e);
            dismiss();
            Island.Notify(new StatusActivity(ExtraGlyphs.Done, "Copiado", "Cole com Ctrl+V", true));
        };
        return row;
    }

    /// <summary>
    /// What a drag carries. Images also go as a PNG file: chat apps and e-mail take files, not bitmaps.
    /// </summary>
    private static DataObject? DragData(ClipEntry e)
    {
        var data = new DataObject();
        if (e.Files != null) data.SetData(DataFormats.FileDrop, e.Files);
        else if (e.Image != null)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "Doca", "Copiados");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"Imagem {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                using (var fs = File.Create(file))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(e.Image));
                    enc.Save(fs);
                }
                data.SetData(DataFormats.FileDrop, new[] { file });
                data.SetImage(e.Image);
            }
            catch (Exception ex) { App.Log(ex); return null; }
        }
        else if (e.Text != null) data.SetText(e.Text);
        else return null;
        return data;
    }
}

/// <summary>Timer: presets, then a big countdown with pause, +1 min and cancel.</summary>
internal sealed class TimerView : StackPanel
{
    private static readonly int[] presets = { 1, 3, 5, 10, 15, 20, 25, 30, 45, 60 };
    private readonly TextBlock big = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 44,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly Border fill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), Background = IslandBrushes.Accent, HorizontalAlignment = HorizontalAlignment.Left };
    private bool runningLayout;

    public TimerView()
    {
        Loaded += (_, _) => { IslandTimer.Changed += Update; Build(); };
        Unloaded += (_, _) => IslandTimer.Changed -= Update;
    }

    private void Build()
    {
        Children.Clear();
        runningLayout = IslandTimer.Active;
        if (!runningLayout)
        {
            Children.Add(IslandBrushes.Text("Iniciar um timer", 12, IslandBrushes.Dim));
            var grid = new UniformGrid { Columns = 5, Margin = new Thickness(-3, 8, -3, 0) };
            foreach (var m in presets)
            {
                var label = new TextBlock { Text = m == 60 ? "1 h" : $"{m} min", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
                var b = IslandBrushes.Button(label, () => IslandTimer.Start(TimeSpan.FromMinutes(m)), null, 12, new Thickness(0, 10, 0, 11));
                b.Margin = new Thickness(3);
                grid.Children.Add(b);
            }
            Children.Add(grid);
            return;
        }
        Children.Add(big);
        var track = new Grid { Width = 300, HorizontalAlignment = HorizontalAlignment.Center };
        track.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = IslandBrushes.Faint });
        track.Children.Add(fill);
        Children.Add(track);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 2) };
        row.Children.Add(Round(IslandTimer.Paused ? ExtraGlyphs.Play : ExtraGlyphs.Pause, IslandTimer.TogglePause, IslandTimer.Paused ? "Continuar" : "Pausar"));
        row.Children.Add(Round(ExtraGlyphs.Add, IslandTimer.AddMinute, "Mais 1 minuto"));
        row.Children.Add(Round(ExtraGlyphs.Cancel, IslandTimer.Cancel, "Cancelar"));
        Children.Add(row);
        Update();
    }

    private static Border Round(string glyph, Action click, string tip)
    {
        var b = IslandBrushes.Button(IslandBrushes.Glyph(glyph, 14), click, tip, 20, new Thickness(0));
        b.Width = b.Height = 40;
        b.Margin = new Thickness(6, 0, 6, 0);
        return b;
    }

    private bool lastPaused;

    private void Update()
    {
        if (IslandTimer.Active != runningLayout || (runningLayout && IslandTimer.Paused != lastPaused)) { lastPaused = IslandTimer.Paused; Build(); return; }
        if (!runningLayout) return;
        big.Text = IslandTimer.Format(IslandTimer.Left);
        big.Opacity = IslandTimer.Paused ? 0.5 : 1;
        double total = Math.Max(1, IslandTimer.Total.TotalSeconds);
        fill.Width = 300 * Math.Clamp(IslandTimer.Left.TotalSeconds / total, 0, 1);
    }
}

#endregion
