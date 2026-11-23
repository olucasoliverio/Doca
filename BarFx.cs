using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Doca;

/// <summary>
/// Menu bar item highlight with short fades (hover, pressed, menu open) instead of switching the
/// background at once. Animations only run during the transition, so an idle bar costs nothing.
/// </summary>
internal static class BarFx
{
    private const byte HoverAlpha = 0x2E, PressedAlpha = 0x4D;
    /// <summary>Softer highlight for the transparent bar, so hovering doesn't paint a piece of strip back.</summary>
    public static bool Subtle { get; set; }
    private static readonly IEasingFunction ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Gives the border its own (animatable) highlight brush, fully transparent at rest.</summary>
    // Highlights in the theme's ink (white on dark, dark on the island's light themes).
    private static Color Ink => IslandTheme.Ink;

    public static void Prepare(Border b) => b.Background = new SolidColorBrush(Color.FromArgb(0, Ink.R, Ink.G, Ink.B));

    public static void Hot(Border b, bool on) => To(b, on ? (Subtle ? (byte)0x1C : HoverAlpha) : (byte)0, on ? 90 : 200);

    public static void Pressed(Border b) => To(b, Subtle ? (byte)0x33 : PressedAlpha, 60);

    private static void To(Border b, byte alpha, int ms)
    {
        if (b.Background is not SolidColorBrush { IsFrozen: false } brush) { Prepare(b); brush = (SolidColorBrush)b.Background; }
        var anim = new ColorAnimation(Color.FromArgb(alpha, Ink.R, Ink.G, Ink.B), TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}

/// <summary>
/// Turns a colored tray icon into a white "template" icon like the macOS menu bar's: the shape stays,
/// inner detail is kept as shades of white (by brightness) and red notification dots keep their color.
/// </summary>
internal static class TemplateIcon
{
    // Keyed by the app's icon object: the same icon converts once, a new one (app changed it) converts again.
    // Also keyed by the ink: the island's light themes want dark silhouettes.
    private sealed record Done(Color Ink, ImageSource Image);
    private static readonly ConditionalWeakTable<ImageSource, Done> cache = new();

    public static ImageSource? From(ImageSource? src)
    {
        if (src == null) return null;
        var ink = IslandTheme.Ink;
        if (cache.TryGetValue(src, out var done) && done.Ink == ink) return done.Image;
        ImageSource result;
        try { result = Convert(src, ink) ?? src; }
        catch (Exception ex) { App.Log(ex); result = src; }
        cache.AddOrUpdate(src, new Done(ink, result));
        return result;
    }

    private static BitmapSource? ToBitmap(ImageSource src)
    {
        if (src is BitmapSource bs) return bs;
        // DrawingImage and friends: render at 32 px (icons are shown at 16 DIP, up to 200% scale).
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, 32, 32));
        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return rtb;
    }

    private static BitmapSource? Convert(ImageSource src, Color ink)
    {
        var bmp = ToBitmap(src);
        if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelWidth > 256) return null;
        if (bmp.Format != PixelFormats.Bgra32) bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        // Brightness range of the visible pixels decides how much inner detail there is to keep.
        double min = 1, max = 0, sum = 0, weight = 0;
        int visible = 0, red = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = px[i + 3] / 255.0;
            if (a < 0.2) continue;
            visible++;
            if (IsAlertRed(px, i)) red++;
            double l = Luma(px, i);
            min = Math.Min(min, l);
            max = Math.Max(max, l);
            sum += l * a;
            weight += a;
        }
        if (weight == 0) return null;
        bool flat = max - min < 0.15;            // one color (already a glyph): plain silhouette
        bool darkIcon = sum / weight < 0.3;      // black glyph made for a light taskbar: bright = background
        // A red dot is a small part of the icon; a red logo (AMD…) is most of it and goes white like the rest.
        bool keepRed = red > 0 && red < visible * 0.3;

        for (int i = 0; i < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a == 0) continue;
            // Unread/alert dots (Discord, Teams…) stay red: that is the information.
            if (keepRed && IsAlertRed(px, i)) continue;
            double t = flat ? 1 : (Luma(px, i) - min) / (max - min);
            if (darkIcon && !flat) t = 1 - t;
            double alpha = a * (flat ? 1 : 0.35 + 0.65 * t);
            px[i] = ink.B; px[i + 1] = ink.G; px[i + 2] = ink.R;
            px[i + 3] = (byte)Math.Clamp(alpha, 0, 255);
        }

        var outBmp = BitmapSource.Create(w, h, bmp.DpiX, bmp.DpiY, PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    private static bool IsAlertRed(byte[] px, int i) => px[i + 2] > 180 && px[i + 1] < 100 && px[i] < 100;

    private static double Luma(byte[] px, int i) => (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
}

/// <summary>
/// Music bars, shared by the menu bar and the island. Two things keep them cheap while music plays:
/// <list type="bullet">
/// <item>One shared ticker moves every bar of every equalizer at <see cref="Fps"/>, so a frame is rendered
/// 8 times a second in total. Each equalizer running its own WPF animations (smooth 30 fps on two islands)
/// cost ~6.5% of a core; even stepped at 8 fps, clocks started at different moments didn't line up and
/// doubled the frames.</item>
/// <item>They stop while nothing is actually heard, since apps keep saying "playing" for a muted tab,
/// a silent ad or a video stuck buffering (<see cref="OutputActivity"/>).</item>
/// </list>
/// </summary>
internal abstract class EqualizerBase : StackPanel
{
    public const int Fps = 8;
    private static readonly System.Windows.Threading.DispatcherTimer ticker =
        new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / Fps) };
    private static readonly List<EqualizerBase> moving = new();
    private static long frame;
    private bool playing, tracking, animating;

    private bool resting;

    // Every window that changes costs ~5 ms of CPU per frame in the render thread (D3D9 present + driver), so
    // with one island per screen only the one on the mouse's screen moves; the others rest.
    static EqualizerBase() => ticker.Tick += (_, _) =>
    {
        frame++;
        IntPtr screen = moving.Count > 1 && Native.GetCursorPos(out var pt) ? Native.MonitorFromPoint(pt, 2 /* NEAREST */) : IntPtr.Zero;
        foreach (var eq in moving) eq.Tick(screen);
    };

    private void Tick(IntPtr screen)
    {
        if (screen == IntPtr.Zero || Screen() == screen) { Step(frame); resting = false; }
        else if (!resting) { Rest(); resting = true; }
    }

    private IntPtr Screen() =>
        PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src
            ? Native.MonitorFromWindow(src.Handle, 2 /* NEAREST */) : IntPtr.Zero;

    protected EqualizerBase()
    {
        Loaded += (_, _) => Track();
        Unloaded += (_, _) => Track();
    }

    public bool Playing
    {
        get => playing;
        set
        {
            if (playing == value) return;
            playing = value;
            OnPlayingChanged(value);
            Track();
        }
    }

    protected virtual void OnPlayingChanged(bool value) { }

    /// <summary>Sets the bars for this frame of the shared ticker.</summary>
    protected abstract void Step(long frame);

    /// <summary>Puts the bars back at rest.</summary>
    protected abstract void Rest();

    // Only a loaded, playing equalizer listens to the output meter (a rebuilt island must not keep it alive).
    private void Track()
    {
        bool want = playing && IsLoaded;
        if (want != tracking)
        {
            tracking = want;
            if (want) OutputActivity.Changed += Apply; else OutputActivity.Changed -= Apply;
            OutputActivity.Watch(this, want);
        }
        Apply();
    }

    private void Apply()
    {
        bool on = tracking && OutputActivity.Audible;
        if (on == animating) return;
        animating = on;
        if (on)
        {
            moving.Add(this);
            resting = true; // the next tick decides whether this one moves
            if (!ticker.IsEnabled) ticker.Start();
        }
        else
        {
            moving.Remove(this);
            Rest();
            if (moving.Count == 0) ticker.Stop();
        }
    }

    /// <summary>One cycle of <paramref name="at"/> (0..1 → scale), sampled at <see cref="Fps"/>.</summary>
    protected static double[] Steps(double cycleSeconds, Func<double, double> at)
    {
        int n = Math.Max(2, (int)Math.Round(cycleSeconds * Fps));
        var values = new double[n];
        for (int k = 0; k < n; k++) values[k] = at((double)k / n);
        return values;
    }

    protected static double At(double[] cycle, long frame) => cycle[(int)(frame % cycle.Length)];

    /// <summary>Sine ease in/out between consecutive values of a looping sequence, at 0..1 of the loop.</summary>
    protected static double Smooth(double[] seq, double u)
    {
        double x = u * seq.Length;
        int j = (int)Math.Floor(x);
        double f = (1 - Math.Cos(Math.PI * (x - j))) / 2;
        return seq[j % seq.Length] + (seq[(j + 1) % seq.Length] - seq[j % seq.Length]) * f;
    }
}

/// <summary>Three bouncing bars next to the song title while music plays (hidden when paused).</summary>
internal sealed class Equalizer : EqualizerBase
{
    // 0.25 → 1 → 0.25 with a sine ease, each bar with its own period and a little behind the previous one.
    private static readonly double[][] cycles = new[] { 0.42, 0.55, 0.36 }.Select((period, i) =>
    {
        double cycle = 2 * period, phase = i * 0.13 / cycle;
        return Steps(cycle, u => 0.25 + 0.75 * (1 - Math.Cos(2 * Math.PI * (u - phase))) / 2);
    }).ToArray();
    private readonly ScaleTransform[] bars = new ScaleTransform[3];

    public Equalizer()
    {
        Visibility = Visibility.Collapsed;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 10;
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = new ScaleTransform(1, 0.3);
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = 2, Height = 10, RadiusX = 1, RadiusY = 1, Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = bars[i], Fill = Brushes.White,
            };
            Children.Add(r);
        }
        Unloaded += (_, _) => Playing = false;
    }

    // Paused, low still bars read as "…" after the title: hide them (the dimmed item already says paused).
    protected override void OnPlayingChanged(bool value) => Visibility = value ? Visibility.Visible : Visibility.Collapsed;

    protected override void Step(long frame)
    {
        for (int i = 0; i < bars.Length; i++) bars[i].ScaleY = At(cycles[i], frame);
    }

    protected override void Rest()
    {
        foreach (var b in bars) b.ScaleY = 0.3;
    }
}