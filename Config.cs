using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Doca;

internal sealed class PinnedApp
{
    public string? Path { get; set; }
    public string? Aumid { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }

    /// <summary>Target of a .lnk, resolved at load time; used to match running windows.</summary>
    [JsonIgnore] public string? ResolvedPath { get; set; }

    /// <summary>Parsing name used to load this item's icon.</summary>
    [JsonIgnore]
    public string IconSource => Aumid != null ? @"shell:AppsFolder\" + Aumid : ResolvedPath ?? Path ?? "";

    [JsonIgnore]
    public string DisplayName => Name
        ?? (Aumid != null ? Shell.DisplayName(@"shell:AppsFolder\" + Aumid) : null)
        ?? Shell.AppName(ResolvedPath ?? Path ?? "");

    [JsonIgnore]
    public string Key => Aumid != null ? "aumid:" + Aumid.ToLowerInvariant() : Shell.AppKey(ResolvedPath ?? Path ?? "");
}

internal sealed class DocaConfig
{
    public double IconSize { get; set; } = 56;
    public double MaxIconSize { get; set; } = 88;
    public bool Magnification { get; set; } = true;
    public bool AutoHideDock { get; set; } = true;
    public bool DockAllScreens { get; set; } = true;
    public bool WindowPreviews { get; set; } = true;
    public bool ScreenCorners { get; set; } = true;
    public double ScreenCornerRadius { get; set; } = 12;
    public bool AppMenus { get; set; } = true;
    public bool WindowCycleHotkey { get; set; } = true;
    public string WindowCycleModifier { get; set; } = "Alt"; // "Alt" or "Ctrl"
    public bool Spotlight { get; set; } = true;

    /// <summary>How many times each Spotlight result was opened; frequent ones rank first.</summary>
    public Dictionary<string, int> SpotlightUsage { get; set; } = new();
    public double DockOpacity { get; set; } = 0.75;
    public bool HideTaskbar { get; set; } = true;
    public double MenuBarHeight { get; set; } = 28;
    public bool MenuBarAllScreens { get; set; } = true;
    public double MenuBarOpacity { get; set; } = 0.55;
    /// <summary>In fullscreen (video), resting the pointer on the top edge shows the bar over it, like macOS.</summary>
    public bool MenuBarRevealInFullscreen { get; set; } = true;
    public bool ShowTrayIcons { get; set; } = true;
    public bool ShowNowPlaying { get; set; } = true;
    // Status items also in the Control Center; Bluetooth's icon says little, so it starts off the bar.
    public bool ShowWifi { get; set; } = true;
    public bool ShowBluetooth { get; set; } = false;
    public bool ShowVolume { get; set; } = true;
    public bool ClockShowDate { get; set; } = true;
    public bool ClockShowSeconds { get; set; } = false;
    public string Font { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public List<PinnedApp> Pinned { get; set; } = new();

    /// <summary>Order of tray icons in the menu bar (NotifyIcon identifiers); unknown icons go last.</summary>
    public List<string> TrayOrder { get; set; } = new();

    /// <summary>Tray icons the user collapsed behind the ⌄ button (NotifyIcon identifiers).</summary>
    public List<string> TrayCollapsed { get; set; } = new();

    /// <summary>
    /// Jump List AppID last seen on each Dock item's window. Apps often set it only at runtime (Steam's window
    /// is "Valve.Steam.Client" while its shortcut gives the exe path), so it is kept for when the app is closed.
    /// </summary>
    public Dictionary<string, string> JumpListIds { get; set; } = new();

    /// <summary>Taskbar auto-hide state before Doca changed it; kept on disk so a crash can still restore it.</summary>
    public bool? TaskbarAutoHideBefore { get; set; }

    public static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Doca");
    public static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static DocaConfig Load()
    {
        DocaConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DocaConfig>(File.ReadAllText(FilePath), json) ?? new DocaConfig()
                : CreateDefault();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            cfg = CreateDefault();
        }
        foreach (var p in cfg.Pinned) cfg.Resolve(p);
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, json));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Resolve(PinnedApp p)
    {
        if (p.Path != null && p.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            p.ResolvedPath = Shell.ResolveShortcut(p.Path);
        else if (p.Path != null && !File.Exists(p.Path) &&
                 (Shell.UpdatedSquirrelPath(p.Path) ?? Shell.UpdatedPackagedPath(p.Path)) is { } updated)
            p.Path = updated;
    }

    /// <summary>First run: import pinned apps and sizes from MyDockFinder when it is installed.</summary>
    private static DocaConfig CreateDefault()
    {
        var cfg = new DocaConfig();
        string mdf = @"C:\Program Files (x86)\Steam\steamapps\common\MyDockFinder";

        var ini = ReadIni(System.IO.Path.Combine(mdf, "config.ini"));
        if (ini.TryGetValue("normal", out var normal))
        {
            if (normal.TryGetValue("defaultsize", out var s) && double.TryParse(s, out var size)) cfg.IconSize = Math.Clamp(size, 32, 128);
            if (normal.TryGetValue("maxsize", out var m) && double.TryParse(m, out var max)) cfg.MaxIconSize = Math.Clamp(max, cfg.IconSize, 160);
        }

        foreach (var (_, section) in ReadIni(System.IO.Path.Combine(mdf, "ico.ini")))
        {
            if (section.TryGetValue("filepath", out var path) && File.Exists(path))
                cfg.Pinned.Add(new PinnedApp { Path = path, Name = section.GetValueOrDefault("tag") });
        }

        if (cfg.Pinned.Count == 0)
        {
            cfg.Pinned.Add(new PinnedApp { Path = @"C:\Windows\explorer.exe" });
            foreach (var candidate in new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            })
                if (File.Exists(candidate)) { cfg.Pinned.Add(new PinnedApp { Path = candidate }); break; }
            cfg.Pinned.Add(new PinnedApp { Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", Name = "Configurações" });
        }
        return cfg;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string file)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return result;
        Dictionary<string, string>? cur = null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                result[line[1..^1]] = cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            else if (cur != null && line.IndexOf('=') is int eq and > 0)
                cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }
}
