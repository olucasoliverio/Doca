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
    private static readonly IEasingFunction ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Gives the border its own (animatable) highlight brush, fully transparent at rest.</summary>
    public static void Prepare(Border b) => b.Background = new SolidColorBrush(Color.FromArgb(0, 0xFF, 0xFF, 0xFF));

    public static void Hot(Border b, bool on) => To(b, on ? HoverAlpha : (byte)0, on ? 90 : 200);

    public static void Pressed(Border b) => To(b, PressedAlpha, 60);

    private static void To(Border b, byte alpha, int ms)
    {
        if (b.Background is not SolidColorBrush { IsFrozen: false } brush) { Prepare(b); brush = (SolidColorBrush)b.Background; }
        var anim = new ColorAnimation(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
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
    private static readonly ConditionalWeakTable<ImageSource, ImageSource> cache = new();

    public static ImageSource? From(ImageSource? src)
    {
        if (src == null) return null;
        if (cache.TryGetValue(src, out var done)) return done;
        ImageSource result;
        try { result = Convert(src) ?? src; }
        catch (Exception ex) { App.Log(ex); result = src; }
        cache.AddOrUpdate(src, result);
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

    private static BitmapSource? Convert(ImageSource src)
    {
        var bmp = ToBitmap(src);
        if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelWidth > 256) return null;
        if (bmp.Format != PixelFormats.Bgra32) bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        // Brightness range of the visible pixels decides how much inner detail there is to keep.
        double min = 1, max = 0, sum = 0, weight = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = px[i + 3] / 255.0;
            if (a < 0.2) continue;
            double l = Luma(px, i);
            min = Math.Min(min, l);
            max = Math.Max(max, l);
            sum += l * a;
            weight += a;
        }
        if (weight == 0) return null;
        bool flat = max - min < 0.15;            // one color (already a glyph): plain silhouette
        bool darkIcon = sum / weight < 0.3;      // black glyph made for a light taskbar: bright = background

        for (int i = 0; i < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a == 0) continue;
            // Unread/alert dots (Discord, Teams…) stay red: that is the information.
            if (r > 180 && g < 100 && b < 100) continue;
            double t = flat ? 1 : (Luma(px, i) - min) / (max - min);
            if (darkIcon && !flat) t = 1 - t;
            double alpha = a * (flat ? 1 : 0.35 + 0.65 * t);
            px[i] = px[i + 1] = px[i + 2] = 0xFF;
            px[i + 3] = (byte)Math.Clamp(alpha, 0, 255);
        }

        var outBmp = BitmapSource.Create(w, h, bmp.DpiX, bmp.DpiY, PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    private static double Luma(byte[] px, int i) => (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
}

/// <summary>
/// Three bouncing bars next to the song title while music plays (still and low when paused).
/// Capped at 20 fps and stopped whenever nothing plays, so an idle bar stays at ~0% CPU.
/// </summary>
internal sealed class Equalizer : StackPanel
{
    private readonly ScaleTransform[] bars = new ScaleTransform[3];
    private bool playing;

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

    public bool Playing
    {
        get => playing;
        set
        {
            if (value == playing) return;
            playing = value;
            // Paused, low still bars read as "…" after the title: hide them (the dimmed item already says paused).
            Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            double[] periods = { 0.42, 0.55, 0.36 };
            for (int i = 0; i < bars.Length; i++)
            {
                if (!value) { bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null); bars[i].ScaleY = 0.3; continue; }
                var a = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(periods[i]))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromSeconds(i * 0.13),
                };
                Timeline.SetDesiredFrameRate(a, 20);
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
        }
    }
}
