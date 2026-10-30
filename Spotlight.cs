using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Doca;

/// <summary>One Spotlight result. Id is stable across sessions (usage counts are keyed by it).</summary>
internal sealed record SpotlightItem(string Id, string Name, string Detail, string? IconSource, string? Glyph, string Keywords, Action Run);

/// <summary>
/// macOS Spotlight on Alt+Space: a centered glass search box that opens apps, Settings pages and
/// system actions, does quick math and falls back to web/file search. Keyboard only: ↑/↓, Enter, Esc.
/// </summary>
internal sealed class SpotlightWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const int MaxResults = 8;
    private static readonly Brush selectedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x0A, 0x63, 0xD9)));
    private static readonly Brush hoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));

    private static SpotlightWindow? current;
    private static List<SpotlightItem>? apps;
    private static bool loadingApps;

    private readonly DocaConfig cfg;
    private readonly TextBox box;
    private readonly StackPanel list = new();
    private readonly Border divider;
    private List<SpotlightItem> results = new();
    private int selected;
    private bool closing;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static void Toggle(DocaConfig cfg)
    {
        if (current != null) { current.Dismiss(); return; }
        LoadAppsAsync(); // refreshed on every open so newly installed apps show up
        current = new SpotlightWindow(cfg);
        current.Show();
        current.Activate();
        current.box.Focus();
    }

    private SpotlightWindow(DocaConfig cfg)
    {
        this.cfg = cfg;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 640;
        Left = Top = -20000;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily(cfg.Font);
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        // Small text (result rows) snaps to pixels like the menu bar; Ideal mode looks soft at 11–15 px.
        // The big search text keeps Ideal, where Display mode's hinting would make it uneven.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        var display = new FontFamily("Segoe UI Variable Display, Segoe UI");

        // Plain (non-Fluent) TextBox: the Fluent template draws its own field and focus underline,
        // but here the whole window is the field.
        // "Variable Display" is the optical size Segoe UI ships for large text; the window font
        // ("Variable Text") is cut for 9–14 px and looks wide and heavy at 24.
        box = new TextBox
        {
            FontSize = 24, FontFamily = display, FontWeight = FontWeight.FromOpenTypeWeight(350), Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
            SelectionBrush = selectedBrush,
        };
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Ideal);
        var placeholder = new TextBlock
        {
            Text = "Pesquisar apps, configurações, contas…", FontSize = 24, FontFamily = display, FontWeight = FontWeight.FromOpenTypeWeight(350), Opacity = 0.35,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0),
        };
        TextOptions.SetTextFormattingMode(placeholder, TextFormattingMode.Ideal);
        var field = new Grid();
        field.Children.Add(placeholder);
        field.Children.Add(box);

        var search = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };
        var lens = new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 22, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 14, 0) };
        DockPanel.SetDock(lens, Dock.Left);
        search.Children.Add(lens);
        search.Children.Add(field);

        divider = new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
        // Hidden until there are results: its margin alone would push the search field 7 px off center.
        list.Margin = new Thickness(8, 6, 8, 8);
        list.Visibility = Visibility.Collapsed;
        var body = new StackPanel();
        body.Children.Add(search);
        body.Children.Add(divider);
        body.Children.Add(list);
        Content = new Border { Child = body, Background = new SolidColorBrush(Color.FromArgb(0x60, 0x20, 0x20, 0x22)) };

        box.TextChanged += (_, _) =>
        {
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Search();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Dismiss();
        Closing += (_, _) => { closing = true; if (current == this) current = null; };
        SourceInitialized += (_, _) => MakeGlass();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Same acrylic, rounded, dark look as the menu bar flyouts.</summary>
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

    /// <summary>
    /// Centered on the monitor under the mouse, with the search box at a fixed height: the window grows
    /// downward as results come in instead of jumping around.
    /// </summary>
    private void Place()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return;
        Native.GetCursorPos(out var pt);
        var mon = Native.MonitorInfo(Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */));
        int x = mon.Bounds.Left + (mon.Bounds.Width - r.Width) / 2;
        int y = mon.Bounds.Top + mon.Bounds.Height / 4;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Dismiss()
    {
        if (closing) return;
        closing = true;
        if (current == this) current = null;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (box.Text.Length > 0) box.Clear(); else Dismiss();
                break;
            case Key.Down:
                Select(selected + 1);
                break;
            case Key.Up:
                Select(selected - 1);
                break;
            case Key.Enter:
                if (results.Count > 0) Run(results[selected]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    #region Results

    private void Search()
    {
        string q = Normalize(box.Text.Trim());
        results = q.Length == 0 ? new() : Rank(box.Text.Trim(), q);
        selected = 0;
        list.Children.Clear();
        for (int i = 0; i < results.Count; i++) list.Children.Add(BuildRow(results[i], i));
        divider.Visibility = list.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Select(0);
    }

    private List<SpotlightItem> Rank(string raw, string q)
    {
        var found = new List<SpotlightItem>();
        if (Calculate(raw) is { } calc) found.Add(calc);

        var candidates = (apps ?? new()).Concat(BuiltIns());
        found.AddRange(candidates
            .Select(it => (it, score: Score(it, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.it.Name.Length)
            .Take(MaxResults - 2 - found.Count)
            .Select(x => x.it));

        string text = raw;
        found.Add(new SpotlightItem("web", $"Pesquisar “{text}” na web", "Navegador padrão", null, "", "",
            () => Shell.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(text))));
        found.Add(new SpotlightItem("files", $"Procurar arquivos com “{text}”", "Explorador de Arquivos", null, "", "",
            () => Shell.Open("explorer.exe", "\"search-ms:query=" + Uri.EscapeDataString(text) + "\"")));
        return found;
    }

    /// <summary>
    /// Accent-insensitive: name prefix beats word prefix beats initials ("vsc" → Visual Studio Code)
    /// beats substring beats keywords. Items opened often get a bonus so habits win ties.
    /// </summary>
    private int Score(SpotlightItem it, string q)
    {
        string name = Normalize(it.Name);
        var words = name.Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        int s = name == q ? 100
              : name.StartsWith(q) ? 80
              : words.Any(w => w.StartsWith(q)) ? 60
              : q.Length > 1 && string.Concat(words.Select(w => w[0])).StartsWith(q) ? 50
              : name.Contains(q) ? 40
              : Normalize(it.Keywords).Split(' ').Any(k => k.StartsWith(q)) ? 30
              : 0;
        if (s == 0) return 0;
        return s + Math.Min(cfg.SpotlightUsage.GetValueOrDefault(it.Id), 10) * 2;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private Border BuildRow(SpotlightItem it, int index)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        if (it.IconSource != null)
        {
            var img = new Image { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Shell.RequestIcon(it.IconSource, src => img.Source = src, 64);
            g.Children.Add(img);
        }
        else
        {
            g.Children.Add(new TextBlock { Text = it.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 20, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = it.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = it.Detail, FontSize = 12, Opacity = 0.65, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);

        var row = new Border { Child = g, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => { if (index != selected) row.Background = hoverBrush; };
        row.MouseLeave += (_, _) => { if (index != selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => Run(it);
        return row;
    }

    private void Select(int index)
    {
        if (results.Count == 0) return;
        selected = (index + results.Count) % results.Count;
        for (int i = 0; i < list.Children.Count; i++)
            ((Border)list.Children[i]).Background = i == selected ? selectedBrush : Brushes.Transparent;
    }

    private void Run(SpotlightItem it)
    {
        cfg.SpotlightUsage[it.Id] = cfg.SpotlightUsage.GetValueOrDefault(it.Id) + 1;
        cfg.Save();
        Dismiss();
        try { it.Run(); } catch (Exception ex) { App.Log(ex); }
    }

    #endregion

    #region Sources

    /// <summary>
    /// "2*3,5" → 7. With a comma, it is the decimal separator and dots group thousands (pt-BR);
    /// without one, a dot is the decimal point ("2.5"). Enter copies the result.
    /// </summary>
    private static SpotlightItem? Calculate(string raw)
    {
        if (raw.Length < 2 || !raw.Any(char.IsDigit) || raw.Any(c => !"0123456789.,+-*/()% ".Contains(c))) return null;
        if (!raw.Skip(1).Any(c => "+-*/%".Contains(c))) return null;
        try
        {
            string expr = raw.Contains(',') ? raw.Replace(".", "").Replace(',', '.') : raw;
            object v = new DataTable().Compute(expr, null);
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            string text = d.ToString("#,##0.##########", CultureInfo.GetCultureInfo("pt-BR"));
            return new SpotlightItem("calc", "= " + text, "Enter copia o resultado", null, "", "", () => Clipboard.SetText(text));
        }
        catch { return null; }
    }

    private static List<SpotlightItem>? builtIns;

    private static List<SpotlightItem> BuiltIns() => builtIns ??= new()
    {
        Setting("Wi-Fi", "network-wifi", "rede internet wireless"),
        Setting("Bluetooth e dispositivos", "bluetooth", "fone mouse teclado"),
        Setting("Som", "sound", "audio volume microfone saida"),
        Setting("Tela", "display", "monitor resolucao brilho escala"),
        Setting("Plano de fundo", "personalization-background", "papel parede wallpaper personalizacao"),
        Setting("Cores", "colors", "tema escuro modo"),
        Setting("Aplicativos instalados", "appsfeatures", "desinstalar programas"),
        Setting("Aplicativos padrão", "defaultapps", "navegador abrir com"),
        Setting("Aplicativos de inicialização", "startupapps", "iniciar com o windows"),
        Setting("Windows Update", "windowsupdate", "atualizacoes atualizar"),
        Setting("Energia e bateria", "powersleep", "suspensao desligar tela"),
        Setting("Mouse", "mousetouchpad", "cursor ponteiro"),
        Setting("Idioma e região", "regionlanguage", "teclado layout abnt"),
        Setting("Data e hora", "dateandtime", "relogio fuso"),
        Setting("Notificações", "notifications", "nao perturbe"),
        Setting("Armazenamento", "storagesense", "disco espaco limpeza"),
        Setting("Impressoras e scanners", "printers", "imprimir"),
        Setting("Privacidade e segurança", "privacy", "permissoes camera localizacao"),
        Setting("Rede e Internet", "network", "ethernet vpn proxy"),
        Setting("Configurações", "", "sistema preferencias"),
        Action("Bloquear tela", "lock", "", "travar sair", () => Native.LockWorkStation()),
        Action("Suspender", "sleep", "", "dormir", () => Native.SetSuspendState(false, false, false)),
        Action("Gerenciador de Tarefas", "taskmgr", "", "processos task manager", () => Shell.Open("taskmgr.exe")),
        Action("Lixeira", "trash", "", "apagados excluidos", () => Shell.Open("explorer.exe", "shell:RecycleBinFolder")),
        Action("Preferências do Doca", "doca-settings", "", "dock barra", App.ShowSettings),
    };

    private static SpotlightItem Setting(string name, string page, string keywords) =>
        new("ms-settings:" + page, name, "Configurações do Windows", null, "", keywords, () => Shell.Open("ms-settings:" + page));

    private static SpotlightItem Action(string name, string id, string glyph, string keywords, Action run) =>
        new("action:" + id, name, "Sistema", null, glyph, keywords, run);

    /// <summary>
    /// Everything in the Start menu's "All apps" (shell:AppsFolder): desktop and Store apps alike, each
    /// launched by its parsing name. Enumerating takes ~100–300 ms of COM, so it runs off the UI thread
    /// and the open window re-runs its search when the list arrives.
    /// </summary>
    private static void LoadAppsAsync()
    {
        if (loadingApps) return;
        loadingApps = true;
        var t = new Thread(() =>
        {
            var found = new List<SpotlightItem>();
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                dynamic sh = Activator.CreateInstance(type!)!;
                dynamic folder = sh.NameSpace("shell:AppsFolder");
                dynamic items = folder.Items();
                int n = items.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name, path = item.Path;
                    Marshal.ReleaseComObject(item);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || IsClutter(name, path)) continue;
                    string parsing = @"shell:AppsFolder\" + path;
                    found.Add(new SpotlightItem("app:" + path.ToLowerInvariant(), name, "Aplicativo", parsing, null, "",
                        () => Shell.OpenAumid(path)));
                }
                Marshal.ReleaseComObject(items);
                Marshal.ReleaseComObject(folder);
                Marshal.ReleaseComObject(sh);
            }
            catch (Exception ex) { App.Log(ex); }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                loadingApps = false;
                if (found.Count == 0) return;
                apps = found.GroupBy(a => a.Id).Select(g => g.First()).ToList();
                if (current != null && current.box.Text.Trim().Length > 0) current.Search();
            });
        }) { IsBackground = true, Name = "Doca.SpotlightApps" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Start menu folders also hold uninstallers, readmes and web links nobody searches for.</summary>
    private static bool IsClutter(string name, string path)
    {
        string n = name.ToLowerInvariant(), p = path.ToLowerInvariant();
        return n.Contains("uninstall") || n.Contains("desinstalar") || n.StartsWith("readme") || n.Contains("leia-me")
            || p.EndsWith(".url") || p.EndsWith(".chm") || p.EndsWith(".txt") || p.EndsWith(".pdf") || p.EndsWith(".html") || p.StartsWith("http");
    }

    #endregion
}

/// <summary>Alt+Space opens Spotlight (taking it from the window system menu, like PowerToys Run does).</summary>
internal static class SpotlightHotkey
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private const int WM_HOTKEY = 0x312, Id = 1;
    private const uint MOD_ALT = 0x1, MOD_NOREPEAT = 0x4000, VK_SPACE = 0x20;

    private static HwndSource? sink;

    public static void Start(DocaConfig cfg)
    {
        Stop();
        if (!cfg.Spotlight) return;
        sink = new HwndSource(new HwndSourceParameters("Doca.Spotlight") { Width = 0, Height = 0, WindowStyle = 0 });
        sink.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            // Receiving the hotkey grants this process foreground rights, so Activate() works.
            if (msg == WM_HOTKEY) { SpotlightWindow.Toggle(cfg); handled = true; }
            return IntPtr.Zero;
        });
        if (!RegisterHotKey(sink.Handle, Id, MOD_ALT | MOD_NOREPEAT, VK_SPACE))
            App.Log(new InvalidOperationException("Atalho Alt+Espaço já está em uso por outro programa."));
    }

    public static void Stop()
    {
        if (sink == null) return;
        UnregisterHotKey(sink.Handle, Id);
        sink.Dispose();
        sink = null;
    }
}
