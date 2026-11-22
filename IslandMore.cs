using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Doca;

#region Microphone mute (Ctrl+Alt+M)

/// <summary>
/// Ctrl+Alt+M mutes every microphone (all apps at once); the island says so and keeps a red mic in the pill
/// while muted. The state is read again when something asks (it can change in Windows settings or a headset).
/// </summary>
internal static class MicMute
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private const int WM_HOTKEY = 0x312, Id = 7;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_M = 0x4D;

    private static HwndSource? sink;
    public static bool Muted { get; private set; }
    public static event Action? Changed;

    public static void Start(DocaConfig cfg)
    {
        Stop();
        Muted = Audio.MicMuted() == true;
        if (!cfg.IslandMicHotkey) return;
        sink = new HwndSource(new HwndSourceParameters("Doca.MicMute") { Width = 0, Height = 0, WindowStyle = 0 });
        sink.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && w.ToInt32() == Id) { Toggle(); handled = true; }
            return IntPtr.Zero;
        });
        if (!RegisterHotKey(sink.Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M))
            App.Log(new InvalidOperationException("Atalho Ctrl+Alt+M já está em uso por outro programa."));
    }

    public static void Stop()
    {
        if (sink == null) return;
        UnregisterHotKey(sink.Handle, Id);
        sink.Dispose();
        sink = null;
    }

    public static void Toggle()
    {
        var m = Audio.ToggleMicMute();
        if (m == null) { Island.Notify(new StatusActivity(ExtraGlyphs.Mic, "Microfone", "Nenhum encontrado", false)); return; }
        Muted = m.Value;
        Changed?.Invoke();
        Island.Notify(Muted
            ? new StatusActivity(Glyphs.MicOff, "Microfone", "Mudo", false, StateBrush: IslandBrushes.Red)
            : new StatusActivity(ExtraGlyphs.Mic, "Microfone", "Ligado", true));
    }

    /// <summary>Re-reads the state (changed outside Doca); raises Changed when it differs.</summary>
    public static void Refresh()
    {
        bool m = Audio.MicMuted() == true;
        if (m == Muted) return;
        Muted = m;
        Changed?.Invoke();
    }
}

internal static class Glyphs
{
    public const string MicOff = "", Agent = "", Snip = "", ColorPicker = "", Power = "", NightLight = "",
        Check = "";
}

#endregion

#region Coding agents (Claude Code hooks) and "Doca.exe --notify"

/// <summary>
/// What coding agents are doing, sent by their hooks through "Doca.exe --claude &lt;state&gt;" (Claude Code passes the
/// hook's JSON on stdin: session, folder, message). The second Doca.exe forwards one JSON line over a named pipe
/// and exits at once, without loading WPF, so hooks stay fast. "Doca.exe --notify texto" shows any text.
/// </summary>
internal static class Agents
{
    public const string Pipe = "Doca.Notify";

    public sealed class Session
    {
        public required string Id { get; init; }
        public string Project { get; set; } = "";
        public string State { get; set; } = "working";
        public DateTime Since { get; set; } = DateTime.Now;
    }

    public static readonly Dictionary<string, Session> Sessions = new();
    public static event Action? Changed;

    /// <summary>The session to show in the pill: one waiting for you first, then one working.</summary>
    public static Session? Current =>
        Sessions.Values.Where(s => s.State == "waiting").OrderByDescending(s => s.Since).FirstOrDefault()
        ?? Sessions.Values.Where(s => s.State == "working").OrderByDescending(s => s.Since).FirstOrDefault();

    public static void Start()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Pipe, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (line != null) Application.Current?.Dispatcher.BeginInvoke(() => Handle(line));
                }
                catch (Exception ex) { App.Log(ex); await Task.Delay(1000); }
            }
        });
        // Sessions whose terminal was closed mid-task never send "done": forget them after a while.
        var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        sweep.Tick += (_, _) =>
        {
            int removed = 0;
            foreach (var s in Sessions.Values.ToList())
                if (DateTime.Now - s.Since > TimeSpan.FromHours(2)) { Sessions.Remove(s.Id); removed++; }
            if (removed > 0) Changed?.Invoke();
        };
        sweep.Start();
    }

    private static void Handle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            if (kind == "notify")
            {
                string text = r.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                string title = r.TryGetProperty("title", out var ti) ? ti.GetString() ?? "Aviso" : "Aviso";
                Island.Notify(new StatusActivity("", title, text, true, Seconds: 5));
                return;
            }
            if (kind != "agent" || Island.Config?.IslandAgents == false) return;
            string state = r.GetProperty("state").GetString() ?? "working";
            string id = r.TryGetProperty("session", out var se) ? se.GetString() ?? "?" : "?";
            string project = r.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "";
            string message = r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string name = r.TryGetProperty("agent", out var a) ? a.GetString() ?? "Claude" : "Claude";
            Sessions.TryGetValue(id, out var s);
            string? before = s?.State;
            if (state == "done" || state == "end")
            {
                Sessions.Remove(id);
                // A finished task is news only when it ran a while (a quick answer you are watching is not).
                if (state == "done" && s != null && before == "working" && DateTime.Now - s.Since > TimeSpan.FromSeconds(20))
                    Island.Notify(new StatusActivity(Glyphs.Agent, $"{name} · {Label(project)}", "Terminou", true, Seconds: 4));
            }
            else
            {
                if (s == null) Sessions[id] = s = new Session { Id = id };
                s.Project = project;
                if (s.State != state) s.Since = DateTime.Now;
                s.State = state;
                if (state == "waiting" && before != "waiting")
                    Island.Notify(new StatusActivity(Glyphs.Agent, $"{name} · {Label(project)}",
                        message.Contains("permission", StringComparison.OrdinalIgnoreCase) ? "Pede permissão" : "Precisa de você",
                        false, Seconds: 8, StateBrush: IslandBrushes.Orange));
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static string Label(string project) => string.IsNullOrEmpty(project) ? "sessão" : project;

    /// <summary>
    /// Client side ("Doca.exe --claude working|waiting|done" or "--notify texto [título]"). Never fails and never
    /// waits long: a hook must not slow the agent down.
    /// </summary>
    public static int Send(string[] args)
    {
        try
        {
            string json;
            if (args[0] == "--notify")
                json = JsonSerializer.Serialize(new { kind = "notify", text = args.Length > 1 ? args[1] : "", title = args.Length > 2 ? args[2] : "Aviso" });
            else
            {
                string state = args.Length > 1 ? args[1] : "working";
                string session = "?", project = "", message = "";
                // Claude Code hooks pass a JSON object on stdin.
                // Read raw bytes as UTF-8: a GUI exe has no console code page, and Console.In may decode wrongly.
                var read = Task.Run(() =>
                {
                    using var stdin = Console.OpenStandardInput();
                    using var reader = new StreamReader(stdin, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    return reader.ReadToEnd();
                });
                string input = read.Wait(1000) ? read.Result.Trim('﻿', ' ', '\r', '\n', '\t') : "";
                {
                    if (input.StartsWith('{'))
                    {
                        using var doc = JsonDocument.Parse(input);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("session_id", out var s)) session = s.GetString() ?? "?";
                        if (r.TryGetProperty("cwd", out var c)) project = Path.GetFileName((c.GetString() ?? "").TrimEnd('\\', '/'));
                        if (r.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
                    }
                }
                json = JsonSerializer.Serialize(new { kind = "agent", agent = "Claude", state, session, project, message });
            }
            using var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.Out);
            pipe.Connect(300);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch { /* Doca not running: nothing to show */ }
        return 0;
    }
}

#endregion

#region Quick actions (Controles tab): snip, color picker, power mode, night light

internal static class PowerMode
{
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid id);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid id);

    // Windows 11's "Modo de energia" (Settings → System → Power) overlays.
    private static readonly (Guid id, string name)[] modes =
    {
        (new Guid("961CC777-2547-4F9D-8174-7D86181B8A7A"), "Economia"),
        (Guid.Empty, "Equilibrado"),
        (new Guid("DED574B5-45A0-4F42-8737-46345C09C238"), "Desempenho"),
    };

    public static string Current()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var id) != 0) return "Equilibrado";
            return modes.FirstOrDefault(m => m.id == id).name ?? "Equilibrado";
        }
        catch { return "Equilibrado"; }
    }

    /// <summary>Next mode (Economia → Equilibrado → Desempenho → …); returns its name.</summary>
    public static string Next()
    {
        int i = Array.FindIndex(modes, m => m.name == Current());
        var next = modes[(i + 1) % modes.Length];
        try { PowerSetActiveOverlayScheme(next.id); } catch (Exception ex) { App.Log(ex); }
        return Current();
    }
}

/// <summary>A row of round buttons under the Control Center in the island's Controles tab.</summary>
internal sealed class QuickActions : UniformGrid
{
    public QuickActions(Action dismiss)
    {
        Columns = 4;
        Margin = new Thickness(-4, 10, -4, 0);
        Children.Add(Action(Glyphs.Snip, "Recortar", () => { dismiss(); Later(() => Shell.Open("ms-screenclip:")); }, "Print de uma área da tela (Win+Shift+S)"));
        Children.Add(Action(Glyphs.ColorPicker, "Cor", () => { dismiss(); Later(ColorPickerWindow.Pick); }, "Copia a cor de qualquer ponto da tela"));
        TextBlock? powerLabel = null;
        var power = Action(Glyphs.Power, PowerMode.Current(), () => { var n = PowerMode.Next(); if (powerLabel != null) powerLabel.Text = n; }, "Modo de energia: clique para trocar", l => powerLabel = l);
        Children.Add(power);
        Children.Add(Action(Glyphs.NightLight, "Luz noturna", () => { dismiss(); Shell.Open("ms-settings:nightlight"); }, "Abre a luz noturna nas Configurações"));
    }

    /// <summary>After the island has closed (a snip of the screen shouldn't include it).</summary>
    private static void Later(Action a)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    private static Border Action(string glyph, string label, Action click, string tip, Action<TextBlock>? labelOut = null)
    {
        var s = new StackPanel();
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = IslandBrushes.Card, HorizontalAlignment = HorizontalAlignment.Center, Child = IslandBrushes.Glyph(glyph, 15) };
        s.Children.Add(circle);
        var text = new TextBlock { Text = label, FontSize = 11, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        labelOut?.Invoke(text);
        s.Children.Add(text);
        var b = new Border { Child = s, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Padding = new Thickness(0, 2, 0, 2) };
        b.MouseEnter += (_, _) => circle.Background = IslandBrushes.Faint;
        b.MouseLeave += (_, _) => circle.Background = IslandBrushes.Card;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}

/// <summary>
/// Full-screen crosshair over every monitor: shows the color under the pointer, a click copies it (#RRGGBB),
/// Esc or right-click cancels. The overlay is a layered window, which GetPixel on the screen never sees.
/// </summary>
internal sealed class ColorPickerWindow : Window
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    private readonly Canvas canvas = new();
    private readonly Border tag;
    private readonly Border swatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
    private readonly TextBlock hex = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private Color color;

    public static void Pick() => new ColorPickerWindow().Show();

    private ColorPickerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // catches the clicks, invisible
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(hex);
        tag = new Border { Child = row, Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x12, 0x12)), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), IsHitTestVisible = false };
        canvas.Children.Add(tag);
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            // The whole virtual screen, in pixels (monitors may differ in scale).
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
            Glass.Place(this, x, y, w, h);
        };
        Loaded += (_, _) => { Activate(); Focus(); Sample(); };
        MouseMove += (_, _) => Sample();
        MouseLeftButtonUp += (_, _) =>
        {
            string text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            Close();
            try { Clipboard.SetText(text); } catch (Exception ex) { App.Log(ex); }
            Island.Notify(new StatusActivity("●", "Cor copiada", text, true, SymbolFont: true, StateBrush: new SolidColorBrush(color)));
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => Close();
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

    private void Sample()
    {
        if (!Native.GetCursorPos(out var pt)) return;
        IntPtr dc = GetDC(IntPtr.Zero);
        uint c = GetPixel(dc, pt.X, pt.Y);
        ReleaseDC(IntPtr.Zero, dc);
        if (c == 0xFFFFFFFF) return;
        color = Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        swatch.Background = new SolidColorBrush(color);
        hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var p = Mouse.GetPosition(canvas);
        Canvas.SetLeft(tag, p.X + 18);
        Canvas.SetTop(tag, p.Y + 18);
    }
}

#endregion
