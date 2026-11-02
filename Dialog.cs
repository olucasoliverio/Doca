using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Doca;

/// <summary>
/// macOS-style alert replacing MessageBox: a small centered glass card with a colored icon,
/// title, message and Cancel / action buttons. Enter confirms, Esc cancels, ←/→/Tab move focus.
/// </summary>
internal sealed class DocaDialog : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Color accent = Color.FromRgb(0x0A, 0x63, 0xD9);
    private static DocaDialog? current;

    /// <summary>Icon presets (glyph + gradient), so every call site looks the same.</summary>
    public sealed record Kind(string Glyph, Color Top, Color Bottom)
    {
        public static readonly Kind Shutdown = new("", Color.FromRgb(0xFF, 0x6B, 0x63), Color.FromRgb(0xD9, 0x32, 0x2B));
        public static readonly Kind Restart = new("", Color.FromRgb(0xFF, 0xC1, 0x4D), Color.FromRgb(0xF0, 0x8A, 0x00));
        public static readonly Kind SignOut = new("", Color.FromRgb(0x5A, 0xA8, 0xFF), Color.FromRgb(0x0A, 0x63, 0xD9));
        public static readonly Kind Question = new("", Color.FromRgb(0x5A, 0xA8, 0xFF), Color.FromRgb(0x0A, 0x63, 0xD9));
        public static readonly Kind Info = new("", Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0x5A, 0x5A, 0x5F));
    }

    private readonly List<Border> buttons = new();
    private readonly List<Action> actions = new();
    private int focused;
    private bool done;

    /// <summary>Non-blocking confirmation: onConfirm runs only if the action button is chosen.</summary>
    public static void Confirm(Kind kind, string title, string message, string actionText, Action onConfirm, string? font = null)
    {
        current?.Finish(-1); // one at a time: a new request replaces the old one (cancelled)
        var d = new DocaDialog(kind, title, message, font, ("Cancelar", null), (actionText, onConfirm));
        current = d;
        d.Show();
        d.Activate();
    }

    /// <summary>Blocking yes/no, for places that need the answer before going on (startup).</summary>
    public static bool Ask(Kind kind, string title, string message, string yes, string no, string? font = null)
    {
        bool answer = false;
        new DocaDialog(kind, title, message, font, (no, null), (yes, () => answer = true)).ShowDialog();
        return answer;
    }

    public static void Info(string title, string message, string? font = null)
    {
        var d = new DocaDialog(Kind.Info, title, message, font, ("OK", null));
        d.Show();
        d.Activate();
    }

    private DocaDialog(Kind kind, string title, string message, string? font, params (string Text, Action? Run)[] choices)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 280;
        Left = Top = -20000; // placed on the cursor's monitor once its size is known
        Background = Brushes.Transparent;
        FontFamily = new FontFamily(font ?? "Segoe UI Variable Text, Segoe UI");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var icon = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(30), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush(kind.Top, kind.Bottom, 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Color = kind.Bottom },
            Child = new TextBlock
            {
                Text = kind.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 26, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var titleText = new TextBlock
        {
            Text = title, FontFamily = display, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0),
        };
        TextOptions.SetTextFormattingMode(titleText, TextFormattingMode.Ideal);
        var messageText = new TextBlock
        {
            Text = message, FontSize = 12.5, Opacity = 0.7, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            LineHeight = 17, Margin = new Thickness(0, 6, 0, 0), Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        var row = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < choices.Length; i++)
        {
            if (i > 0) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var b = MakeButton(choices[i].Text, primary: i == choices.Length - 1, index: i);
            Grid.SetColumn(b, i * 2);
            row.Children.Add(b);
            var run = choices[i].Run;
            actions.Add(run ?? (() => { }));
        }
        focused = choices.Length - 1; // the action is the default, like macOS

        var body = new StackPanel { Margin = new Thickness(20, 24, 20, 18) };
        body.Children.Add(icon);
        body.Children.Add(titleText);
        body.Children.Add(messageText);
        body.Children.Add(row);

        // Pops in like a macOS sheet: slight scale-up while fading in. Only the content animates;
        // the acrylic backdrop is DWM's and appears with the window.
        var scale = new ScaleTransform(0.92, 0.92);
        var card = new Border
        {
            Child = body, Background = new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x20, 0x22)), Opacity = 0,
            RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Content = card;
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = TimeSpan.FromMilliseconds(170);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            Paint();
        };

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => MakeGlass();
        SizeChanged += (_, _) => Place();
        Closed += (_, _) => { if (current == this) current = null; };
        // The bar and Dock never take focus, so a plain Activate can lose to the foreground lock.
        ContentRendered += (_, _) => Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    private Border MakeButton(string text, bool primary, int index)
    {
        var b = new Border
        {
            Height = 30, CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Tag = primary,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 8, 1),
            },
        };
        b.MouseEnter += (_, _) => Paint();
        b.MouseLeave += (_, _) => Paint();
        b.MouseLeftButtonDown += (_, e) => { b.CaptureMouse(); Paint(pressed: b); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            b.ReleaseMouseCapture();
            Paint();
            if (b.IsMouseOver) Finish(index);
            e.Handled = true;
        };
        buttons.Add(b);
        return b;
    }

    private void Paint(Border? pressed = null)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            bool primary = (bool)b.Tag;
            Color c = primary ? accent : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
            if (b == pressed) c = primary ? Color.FromRgb(0x08, 0x50, 0xB0) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
            else if (b.IsMouseOver) c = primary ? Color.FromRgb(0x2A, 0x7A, 0xEB) : Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF);
            b.Background = new SolidColorBrush(c);
            // Keyboard focus ring: only drawn when it isn't on the default button, where it would be noise.
            b.BorderBrush = i == focused && !primary
                ? new SolidColorBrush(Color.FromArgb(0xB0, 0x5A, 0xA8, 0xFF))
                : new SolidColorBrush(Color.FromArgb(primary ? (byte)0x30 : (byte)0x14, 0xFF, 0xFF, 0xFF));
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Finish(-1); break;
            case Key.Enter: case Key.Space: Finish(focused); break;
            case Key.Left: focused = Math.Max(0, focused - 1); Paint(); break;
            case Key.Right: focused = Math.Min(buttons.Count - 1, focused + 1); Paint(); break;
            case Key.Tab: focused = (focused + 1) % buttons.Count; Paint(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>index -1 = cancelled (Esc or replaced). The action runs after the window is gone.</summary>
    private void Finish(int index)
    {
        if (done) return;
        done = true;
        if (current == this) current = null;
        Close();
        if (index >= 0 && index < actions.Count) actions[index]();
    }

    /// <summary>Same acrylic, rounded, dark look as Spotlight.</summary>
    private void MakeGlass()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(h, ref m);
        int dark = 1, round = 2 /* DWMWCP_ROUND */, acrylic = 3 /* DWMSBT_TRANSIENTWINDOW */;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(h, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref acrylic, sizeof(int));
        long ex = (Native.ExStyle(h) | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Centered on the monitor under the mouse, a bit above the middle like macOS alerts.</summary>
    private void Place()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return;
        Native.GetCursorPos(out var pt);
        var mon = Native.MonitorInfo(Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */));
        int x = mon.Bounds.Left + (mon.Bounds.Width - r.Width) / 2;
        int y = mon.Bounds.Top + (mon.Bounds.Height - r.Height) * 2 / 5;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
