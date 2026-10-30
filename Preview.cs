using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Doca;

/// <summary>
/// Live window previews above a Dock icon. DWM draws the thumbnails straight into this window
/// on the GPU (DwmRegisterThumbnail), so they stay live at no CPU cost.
/// </summary>
internal sealed class PreviewWindow : Window
{
    private const double ThumbH = 128, ThumbMaxW = 230, Pad = 10, TitleH = 24, Gap = 8;

    private readonly Canvas canvas = new();
    private readonly List<IntPtr> thumbs = new();
    private IntPtr hwnd;
    private double scale = 1;

    public IReadOnlyList<IntPtr> Windows { get; private set; } = Array.Empty<IntPtr>();
    public bool IsOpen { get; private set; }
    public event Action? PointerLeft;
    public event Action? WindowChosen;

    private static readonly Brush cardFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x2A)));
    private static readonly Brush slotHover = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public PreviewWindow(string font)
    {
        Title = "Doca Preview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        Left = Top = -20000;
        Width = Height = 10;
        FontFamily = new FontFamily(font);
        Foreground = Brushes.White;
        Content = new Border
        {
            Background = cardFill,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = canvas,
        };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Glass.MakeOverlay(this);
        };
        MouseLeave += (_, _) => PointerLeft?.Invoke();
    }

    /// <summary>Shows previews of <paramref name="windows"/>, centered above <paramref name="anchorPx"/> (screen px).</summary>
    public void ShowFor(IReadOnlyList<IntPtr> windows, Point anchorPx, double monitorScale, Native.RECT monitorPx)
    {
        if (windows.Count == 0) { HidePreview(); return; }
        if (hwnd == IntPtr.Zero) { Show(); Hide(); }
        ClearThumbs();
        Windows = windows.Take(8).ToList();
        scale = monitorScale;
        canvas.Children.Clear();

        // Register first: thumbnail source sizes decide each slot's width.
        var slots = new List<(IntPtr win, IntPtr thumb, double w)>();
        foreach (var w in Windows)
        {
            Native.DwmRegisterThumbnail(hwnd, w, out var t);
            double width = ThumbMaxW * 0.75;
            if (t != IntPtr.Zero && Native.DwmQueryThumbnailSourceSize(t, out var size) == 0 && size.cy > 0)
                width = Math.Clamp(ThumbH * size.cx / size.cy, 80, ThumbMaxW);
            if (t != IntPtr.Zero) thumbs.Add(t);
            slots.Add((w, t, width));
        }

        double x = Pad;
        foreach (var (win, thumb, w) in slots)
        {
            AddSlot(win, thumb, x, w);
            x += w + Pad * 2 + Gap;
        }
        double totalW = x - Gap + Pad, totalH = Pad * 2 + TitleH + ThumbH + Pad;

        int pw = (int)Math.Ceiling(totalW * scale), ph = (int)Math.Ceiling(totalH * scale);
        int px = (int)Math.Clamp(anchorPx.X - pw / 2.0, monitorPx.Left + 8, monitorPx.Right - pw - 8);
        int py = (int)(anchorPx.Y - ph);
        Show();
        Glass.Place(this, px, py, pw, ph);
        IsOpen = true;
    }

    private void AddSlot(IntPtr win, IntPtr thumb, double x, double w)
    {
        var slot = new Border { Width = w + Pad * 2, Height = TitleH + ThumbH + Pad * 2, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        Canvas.SetLeft(slot, x - Pad + Pad);
        Canvas.SetTop(slot, Pad / 2);

        var grid = new Grid { Margin = new Thickness(Pad, Pad / 2, Pad, Pad) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleH) });
        grid.RowDefinitions.Add(new RowDefinition());
        var title = new TextBlock { Text = Native.WindowTitle(win), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0) };
        var close = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5), Visibility = Visibility.Hidden, Background = Brushes.Transparent, ToolTip = "Fechar janela",
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Shell.CloseAll(new[] { win }, Native.WindowTitle(win));
            HidePreview();
        };
        grid.Children.Add(title);
        grid.Children.Add(close);
        if (Native.IsIconic(win))
        {
            var min = new TextBlock { Text = "Minimizada", Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(min, 1);
            grid.Children.Add(min);
        }
        slot.Child = grid;
        slot.MouseEnter += (_, _) => { slot.Background = slotHover; close.Visibility = Visibility.Visible; };
        slot.MouseLeave += (_, _) => { slot.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
        slot.MouseLeftButtonUp += (_, _) =>
        {
            Shell.Focus(win);
            HidePreview();
            WindowChosen?.Invoke();
        };
        canvas.Children.Add(slot);

        if (thumb == IntPtr.Zero) return;
        // Destination rect in this window's pixels, below the title row.
        double left = x + Pad, top = Pad / 2 + Pad / 2 + TitleH;
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Native.RECT
            {
                Left = (int)(left * scale), Top = (int)(top * scale),
                Right = (int)((left + w) * scale), Bottom = (int)((top + ThumbH) * scale),
            },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        Native.DwmUpdateThumbnailProperties(thumb, ref props);
    }

    public void HidePreview()
    {
        if (!IsOpen) return;
        IsOpen = false;
        ClearThumbs();
        Windows = Array.Empty<IntPtr>();
        Hide();
    }

    private void ClearThumbs()
    {
        foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t);
        thumbs.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbs();
        base.OnClosed(e);
    }
}
