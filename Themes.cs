using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Doca;

/// <param name="Top">Island color at the top edge.</param>
/// <param name="Bottom">Island color at the bottom (a soft vertical gradient).</param>
/// <param name="Ink">Text, icons, and (with transparency) cards, hovers and tracks.</param>
/// <param name="Accent">Default accent: sliders, progress, selected tab (an accent chosen in Preferências wins).</param>
/// <param name="Deco">Progress bars drawn as a row of this symbol (hearts, petals…), or null for a plain bar.</param>
internal sealed record ThemeDef(string Name, Color Top, Color Bottom, Color Ink, Color Accent, bool Light, string? Deco = null);

/// <summary>
/// Island themes. Everything the island draws (and the panels it borrows: calendar, Control Center, player,
/// and the windows those open) takes its colors from the current theme instead of assuming white on black.
/// The bar mode keeps the classic dark look.
/// </summary>
internal static class IslandTheme
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly ThemeDef[] All =
    {
        new("Preto", C(0x000000), C(0x000000), Colors.White, Colors.White, false),
        new("Grafite", C(0x1C1C1E), C(0x2A2A2D), Colors.White, Colors.White, false),
        new("Meia-noite", C(0x0A1426), C(0x15284A), Colors.White, C(0x64A8FF), false),
        new("Roxo", C(0x150A26), C(0x2A1450), Colors.White, C(0xBF8CFF), false),
        new("Vinho", C(0x1E070F), C(0x3A0F20), Colors.White, C(0xFF7A9C), false),
        new("Candy", C(0xFF4F9A), C(0x7A5CFF), Colors.White, Colors.White, false, "★"),
        new("Branco", C(0xFBFBFB), C(0xEDEDEF), C(0x18181B), C(0x0A84FF), true),
        new("Rosé", C(0xFCE7EE), C(0xF7CFDC), C(0x4A1F2C), C(0xE0567A), true, "♥"),
        new("Lavanda", C(0xEFE9FD), C(0xDCD1F8), C(0x2D2350), C(0x7A5AE0), true, "✦"),
        new("Pêssego", C(0xFFEDE2), C(0xFFD7C2), C(0x4D2A17), C(0xF07B3F), true),
        new("Sakura", C(0xFFF3F6), C(0xFBDCE6), C(0x5A2335), C(0xF26D8F), true, "✿"),
    };

    /// <summary>The theme in use (set by Use when the screens are built).</summary>
    public static ThemeDef Current { get; private set; } = All[0];
    public static Color Accent { get; private set; } = Colors.White;
    public static Color Ink => Current.Ink;
    public static bool Light => Current.Light;

    /// <summary>Picks the theme for the island-only mode (the bar keeps the dark one).</summary>
    public static void Use(DocaConfig cfg)
    {
        Current = cfg.IslandOnly && cfg.DynamicIsland ? Find(cfg) : All[0];
        // "White" means "the theme's own" (it was the only default before themes): white on a light theme
        // would vanish.
        bool own = string.IsNullOrEmpty(cfg.IslandAccent) || cfg.IslandAccent.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase);
        Accent = own || !(cfg.IslandOnly && cfg.DynamicIsland) ? Current.Accent : IslandBrushes.Parse(cfg.IslandAccent, Current.Accent);
        cache.Clear();
    }

    public static ThemeDef Find(DocaConfig cfg)
    {
        if (All.FirstOrDefault(t => t.Name == cfg.IslandTheme) is { } t) return t;
        // Before themes there was only a color: keep the look it gave.
        return cfg.IslandColor.ToUpperInvariant() switch
        {
            "#1C1C1E" => All[1],
            "#0B1A33" => All[2],
            "#1E0B33" => All[3],
            "#2A0A14" => All[4],
            _ => All[0],
        };
    }

    private static readonly Dictionary<uint, SolidColorBrush> cache = new();

    /// <summary>The ink at some opacity (cached, frozen): 0x12 cards, 0x30 tracks, 0x99 secondary text…</summary>
    public static SolidColorBrush InkA(byte alpha) => Brush(Color.FromArgb(alpha, Ink.R, Ink.G, Ink.B));
    public static SolidColorBrush InkBrush => Brush(Ink);
    public static SolidColorBrush AccentBrush => Brush(Accent);

    public static SolidColorBrush Brush(Color c)
    {
        uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        if (!cache.TryGetValue(key, out var b)) { b = new SolidColorBrush(c); b.Freeze(); cache[key] = b; }
        return b;
    }

    /// <summary>Fluent control styles matching the theme (light themes need dark text on controls).</summary>
    public static Uri FluentDictionary => new(Light
        ? "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml"
        : "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml");

    /// <summary>A vivid color made readable on the island (light themes: darker, dark themes: brighter).</summary>
    public static Color Readable(Color c)
    {
        if (!Light || c == Colors.White) return c == Colors.White ? Ink : c;
        const double k = 0.45; // toward black
        return Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));
    }
}

/// <summary>
/// Progress/level bar in the theme's style: a rounded bar, or a row of the theme's symbol (♥, ✿, ✦, ★) filling
/// up in the accent color.
/// </summary>
internal sealed class ThemedBar : Grid
{
    private readonly Border? fill;
    private readonly Border? clip;
    private double value;

    public ThemedBar(double height = 5)
    {
        var deco = IslandTheme.Current.Deco;
        VerticalAlignment = VerticalAlignment.Center;
        if (deco == null)
        {
            Height = height;
            Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = IslandTheme.InkA(IslandTheme.Light ? (byte)0x22 : (byte)0x30) });
            fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = IslandTheme.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left };
            Children.Add(fill);
        }
        else
        {
            // Two rows of symbols: the faint one is the track, the accent one is clipped to the value.
            Height = 12;
            ClipToBounds = true;
            Children.Add(Row(deco, IslandTheme.InkA(0x30)));
            clip = new Border { HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, Child = Row(deco, IslandTheme.AccentBrush) };
            Children.Add(clip);
        }
        SizeChanged += (_, _) => Apply();
    }

    private static TextBlock Row(string deco, Brush brush) => new()
    {
        Text = string.Concat(Enumerable.Repeat(deco + " ", 60)), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 10,
        Foreground = brush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, Width = 2000,
    };

    /// <summary>0–1.</summary>
    public double Value
    {
        get => value;
        set { this.value = Math.Clamp(value, 0, 1); Apply(); }
    }

    private void Apply()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(w) || w <= 0) return;
        if (fill != null) fill.Width = Math.Max(Height, w * value);
        if (clip != null) clip.Width = w * value;
    }
}
