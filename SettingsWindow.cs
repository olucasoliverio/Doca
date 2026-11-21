using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Doca;

/// <summary>What a setting change has to refresh.</summary>
internal enum Apply { Dock, Screens, Taskbar, Hotkeys }

/// <summary>Preferences window (Windows 11 Fluent dark theme). Every change applies immediately.</summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? open;

    private readonly DocaConfig cfg;
    private readonly ContentControl page = new();
    private readonly ListBox nav = new();

    public static void ShowSingle(DocaConfig cfg)
    {
        if (open != null)
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        open = new SettingsWindow(cfg);
        open.Closed += (_, _) => open = null;
        open.Show();
        open.Activate();
    }

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental
    private SettingsWindow(DocaConfig cfg)
    {
        this.cfg = cfg;
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Title = "Preferências do Doca";
        Width = 820;
        Height = 600;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(cfg.Font);

        var pages = new (string icon, string title, Func<UIElement> build)[]
        {
            ("", "Geral", General),
            ("", "Dock", DockPage),
            ("", "Barra de menu", MenuBarPage),
            ("", "Apps fixados", PinnedPage),
            ("", "Sobre", AboutPage),
        };

        nav.Margin = new Thickness(8, 12, 0, 12);
        nav.BorderThickness = new Thickness(0);
        nav.Background = Brushes.Transparent;
        foreach (var (icon, title, _) in pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            nav.Items.Add(new ListBoxItem { Content = row });
        }
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedIndex >= 0) page.Content = Scroll(pages[nav.SelectedIndex].title, pages[nav.SelectedIndex].build());
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(nav);
        Grid.SetColumn(page, 1);
        root.Children.Add(page);
        Content = root;
        nav.SelectedIndex = 0;
    }

    #region Pages

    private UIElement General() => Stack(
        Card(
            Toggle("Iniciar com o Windows", "Abre o Doca automaticamente quando você entra no Windows.",
                () => Startup.Enabled, _ => Startup.Toggle()),
            Toggle("Esconder a barra de tarefas do Windows", "A Dock e a barra de menu substituem a taskbar. Ela volta ao sair do Doca.",
                () => cfg.HideTaskbar, v => { cfg.HideTaskbar = v; Changed(Apply.Taskbar); })),
        Card(
            Toggle("Alternar entre janelas do mesmo app", "Como Cmd+` no Mac: troca entre as janelas do app em foco (ex.: duas janelas do Chrome). Com Shift, volta.",
                () => cfg.WindowCycleHotkey, v => { cfg.WindowCycleHotkey = v; Changed(Apply.Hotkeys); }),
            Row("Tecla do atalho", "A tecla à esquerda do 1, junto com:",
                Choice(new[] { "Alt", "Ctrl" }, () => cfg.WindowCycleModifier, v => { cfg.WindowCycleModifier = v; Changed(Apply.Hotkeys); }))),
        Card(
            Toggle("Spotlight com Alt+Espaço", "Busca rápida de apps, configurações e contas no centro da tela. A lupa da barra também abre.",
                () => cfg.Spotlight, v => { cfg.Spotlight = v; Changed(Apply.Hotkeys); })),
        Card(
            Toggle("Cantos arredondados na tela", "Arredonda os quatro cantos de cada monitor, como num Mac.",
                () => cfg.ScreenCorners, v => { cfg.ScreenCorners = v; Changed(Apply.Screens); }),
            SliderRow("Raio dos cantos", 4, 32, () => cfg.ScreenCornerRadius, v => { cfg.ScreenCornerRadius = Math.Round(v); Changed(Apply.Screens); }, v => $"{v:0} px")),
        Card(
            Row("Arquivo de configuração", DocaConfig.FilePath,
                Btn("Abrir pasta", () => Shell.Open("explorer.exe", $"\"{DocaConfig.Dir}\"")))));

    private UIElement DockPage() => Stack(
        Card(
            Toggle("Mostrar em todas as telas", "Uma Dock em cada monitor. Desligado: só na tela principal.",
                () => cfg.DockAllScreens, v => { cfg.DockAllScreens = v; Changed(Apply.Screens); }),
            Toggle("Ocultar automaticamente", "A Dock fica escondida e aparece ao encostar o mouse na borda de baixo.",
                () => cfg.AutoHideDock, v => { cfg.AutoHideDock = v; Changed(Apply.Dock); }),
            Toggle("Mostrar em tela cheia ao apontar para baixo", "Num vídeo em tela cheia, deixar o ponteiro na borda de baixo mostra a Dock por cima, como no Mac.",
                () => cfg.DockRevealInFullscreen, v => { cfg.DockRevealInFullscreen = v; Changed(Apply.Dock); }),
            Toggle("Pré-visualização das janelas", "Ao parar o mouse sobre um app aberto, mostra miniaturas ao vivo das janelas dele.",
                () => cfg.WindowPreviews, v => { cfg.WindowPreviews = v; Changed(Apply.Dock); })),
        Card(
            SliderRow("Tamanho dos ícones", 32, 96, () => cfg.IconSize, v =>
            {
                cfg.IconSize = v;
                if (cfg.MaxIconSize < v) cfg.MaxIconSize = v;
                Changed(Apply.Dock);
            }, v => $"{v:0} px"),
            Toggle("Ampliação", "Os ícones crescem quando o mouse passa por cima.",
                () => cfg.Magnification, v => { cfg.Magnification = v; Changed(Apply.Dock); }),
            SliderRow("Tamanho máximo na ampliação", 48, 160, () => cfg.MaxIconSize, v =>
            {
                cfg.MaxIconSize = Math.Max(v, cfg.IconSize);
                Changed(Apply.Dock);
            }, v => $"{v:0} px"),
            SliderRow("Opacidade do fundo", 0.2, 1, () => cfg.DockOpacity, v => { cfg.DockOpacity = v; Changed(Apply.Dock); }, v => $"{v * 100:0}%")));

    private UIElement MenuBarPage() => Stack(
        Card(
            Row("Topo da tela", "Só a ilha: sem barra, a ilha mostra o relógio e abre um painel com música, calendário, controles e bandeja. Botão direito nela: menu do sistema.",
                Choice(new[] { "Só a ilha", "Barra completa" }, () => cfg.IslandOnly ? "Só a ilha" : "Barra completa",
                    v => { cfg.IslandOnly = v == "Só a ilha"; if (cfg.IslandOnly) cfg.DynamicIsland = true; Changed(Apply.Screens); }))),
        Card(
            Toggle("Mostrar em todas as telas", "Uma barra em cada monitor. Desligado: só na tela principal.",
                () => cfg.MenuBarAllScreens, v => { cfg.MenuBarAllScreens = v; Changed(Apply.Screens); }),
            SliderRow("Altura", 22, 40, () => cfg.MenuBarHeight, v => { cfg.MenuBarHeight = Math.Round(v); Changed(Apply.Screens); }, v => $"{v:0} px"),
            Row("Estilo", "Transparente não tem faixa (só a ilha é sólida, como no iPhone); Vidro deixa a cor do papel de parede passar; Sólido é escuro.",
                Choice(new[] { "Transparente", "Vidro", "Sólido" }, () => cfg.MenuBarStyle, v => { cfg.MenuBarStyle = v; Changed(Apply.Screens); })),
            SliderRow("Opacidade (Sólido)", 0.1, 1, () => cfg.MenuBarOpacity, v => { cfg.MenuBarOpacity = v; Changed(Apply.Screens); }, v => $"{v * 100:0}%"),
            Toggle("Barra flutuante", "Afastada da borda da tela, com cantos arredondados.",
                () => cfg.MenuBarFloating, v => { cfg.MenuBarFloating = v; Changed(Apply.Screens); }),
            Toggle("Mostrar em tela cheia ao apontar para o topo", "Num vídeo em tela cheia, deixar o ponteiro no alto da tela mostra a barra por cima, como no Mac.",
                () => cfg.MenuBarRevealInFullscreen, v => { cfg.MenuBarRevealInFullscreen = v; Changed(Apply.Screens); })),
        Card(
            Toggle("Menus do app", "Mostra os menus do app em foco (Arquivo, Editar…). Apps sem menu ganham menus padrão com os atalhos universais.",
                () => cfg.AppMenus, v => { cfg.AppMenus = v; Changed(Apply.Screens); })),
        Card(
            Toggle("Ícones da bandeja", "Ícones dos apps em segundo plano (Discord, Steam…), inclusive os que o Windows esconde.",
                () => cfg.ShowTrayIcons, v => { cfg.ShowTrayIcons = v; Changed(Apply.Screens); }),
            Toggle("Ícones da bandeja monocromáticos", "Desenha os ícones em branco, como no Mac. Avisos em vermelho (mensagens não lidas) continuam coloridos.",
                () => cfg.TrayIconsMonochrome, v => { cfg.TrayIconsMonochrome = v; Changed(Apply.Screens); }),
            Row("Ordem dos ícones da bandeja", "Arraste os ícones na barra para reordenar, ou até ⌄ para recolher.",
                Btn("Restaurar", () => { cfg.TrayOrder.Clear(); cfg.TrayCollapsed.Clear(); Changed(Apply.Screens); }))),
        TrayIconsCard(),
        Card(
            Toggle("Wi-Fi na barra", "Sinal e rede atual. Também fica na Central de Controle.",
                () => cfg.ShowWifi, v => { cfg.ShowWifi = v; Changed(Apply.Screens); }),
            Toggle("Bluetooth na barra", "Também fica na Central de Controle.",
                () => cfg.ShowBluetooth, v => { cfg.ShowBluetooth = v; Changed(Apply.Screens); }),
            Toggle("Som na barra", "Role sobre o ícone para mudar o volume; botão direito silencia. Também fica na Central de Controle.",
                () => cfg.ShowVolume, v => { cfg.ShowVolume = v; Changed(Apply.Screens); })),
        Card(
            Toggle("Ilha dinâmica", "Pílula no centro da barra, como no iPhone: mostra a música e aparece por um instante ao mudar volume ou brilho e ao conectar fones Bluetooth. Passe o mouse para os controles.",
                () => cfg.DynamicIsland, v => { cfg.DynamicIsland = v; Changed(Apply.Screens); }),
            Toggle("Música tocando", "Mostra o que está tocando (Spotify, YouTube…): na ilha ou, sem ela, à direita da barra.",
                () => cfg.ShowNowPlaying, v => { cfg.ShowNowPlaying = v; Changed(Apply.Screens); })),
        Card(
            Toggle("Mostrar a data no relógio", "Ex.: Qui 29 Out 21:35",
                () => cfg.ClockShowDate, v => { cfg.ClockShowDate = v; Changed(Apply.Screens); }),
            Toggle("Mostrar os segundos", null,
                () => cfg.ClockShowSeconds, v => { cfg.ClockShowSeconds = v; Changed(Apply.Screens); })));

    /// <summary>Every running tray icon with "show in the bar" (unchecked = behind ⌄).</summary>
    private UIElement TrayIconsCard()
    {
        var rows = new List<UIElement>();
        foreach (var icon in TrayIconsPanel.Ordered(cfg))
        {
            string title = (icon.Title ?? "").Split('\n')[0].Trim();
            if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(icon.Path ?? "") is { Length: > 0 } n ? n : "Ícone sem nome";
            if (title.Length > 50) title = title[..47] + "…";

            var box = new CheckBox { IsChecked = !TrayIconsPanel.IsCollapsed(cfg, icon), MinWidth = 0 };
            var id = icon.Identifier ?? "";
            box.Click += (_, _) =>
            {
                cfg.TrayCollapsed.Remove(id);
                if (box.IsChecked != true) cfg.TrayCollapsed.Add(id);
                cfg.Save();
                TrayIconsPanel.RaiseChanged();
            };

            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Image { Source = icon.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Grid.SetColumn(box, 2);
            g.Children.Add(box);
            rows.Add(g);
        }
        if (rows.Count == 0) rows.Add(Hint("Nenhum ícone de bandeja no momento."));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Mostrar na barra", FontSize = 14, Margin = new Thickness(0, 12, 0, 2) });
        stack.Children.Add(new TextBlock { Text = "Desmarcados ficam no ⌄. Os que não couberem na barra também vão para lá.", FontSize = 12, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var r in rows) stack.Children.Add(r);
        return Card(stack);
    }

    private UIElement PinnedPage()
    {
        var list = new ListBox { MinHeight = 300, Margin = new Thickness(0, 0, 0, 12) };

        void Fill(int select = -1)
        {
            list.Items.Clear();
            foreach (var p in cfg.Pinned)
            {
                var img = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
                Shell.RequestIcon(p.IconSource, s => img.Source = s);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                row.Children.Add(img);
                row.Children.Add(new TextBlock { Text = p.DisplayName, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
                list.Items.Add(new ListBoxItem { Content = row, Tag = p });
            }
            if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
        }

        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= cfg.Pinned.Count) return;
            (cfg.Pinned[i], cfg.Pinned[j]) = (cfg.Pinned[j], cfg.Pinned[i]);
            Changed(Apply.Dock);
            Fill(j);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Btn("Adicionar…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha um app, atalho ou arquivo",
                Filter = "Apps e atalhos|*.exe;*.lnk;*.url|Todos os arquivos|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                DereferenceLinks = false,
            };
            if (dlg.ShowDialog(this) != true) return;
            var p = new PinnedApp { Path = dlg.FileName };
            cfg.Resolve(p);
            if (cfg.Pinned.All(x => x.Key != p.Key)) cfg.Pinned.Add(p);
            Changed(Apply.Dock);
            Fill(cfg.Pinned.Count - 1);
        }));
        buttons.Children.Add(Btn("Remover", () =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: PinnedApp p }) return;
            int i = list.SelectedIndex;
            cfg.Pinned.Remove(p);
            Changed(Apply.Dock);
            Fill(Math.Min(i, cfg.Pinned.Count - 1));
        }));
        buttons.Children.Add(Btn("Subir", () => Move(-1)));
        buttons.Children.Add(Btn("Descer", () => Move(1)));

        Fill();
        return Stack(
            Hint("Também dá para arrastar arquivos e apps para a Dock, e arrastar os ícones para reordenar."),
            list,
            buttons);
    }

    private UIElement AboutPage()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        return Stack(
            Card(
                Row("Doca", $"Versão {version} · Dock e barra de menu estilo macOS para Windows", null),
                Row("Bandeja do sistema", "ManagedShell (MIT) · github.com/cairoshell/ManagedShell", null)),
            Card(
                Row("Reiniciar o Doca", "Recarrega tudo do zero.", Btn("Reiniciar", App.Restart)),
                Row("Sair do Doca", "Restaura a barra de tarefas do Windows.", Btn("Sair", () => Application.Current.Shutdown()))));
    }

    #endregion

    #region Building blocks

    private void Changed(Apply what)
    {
        cfg.Save();
        App.ApplySettings(what);
    }

    private static UIElement Scroll(string title, UIElement body)
    {
        var s = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        s.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        s.Children.Add(body);
        return new ScrollViewer { Content = s, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    private static Border Card(params UIElement[] rows)
    {
        var s = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) s.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(-16, 0, -16, 0) });
            s.Children.Add(rows[i]);
        }
        return new Border
        {
            Child = s,
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 2, 16, 2),
            Margin = new Thickness(0, 0, 0, 14),
        };
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
    };

    private static Grid Row(string title, string? description, UIElement? control)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 12), MinHeight = 32 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(text);
        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }
        return g;
    }

    private static Grid Toggle(string title, string? description, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = get(), MinWidth = 0, Content = null };
        box.Click += (_, _) => set(box.IsChecked == true);
        return Row(title, description, box);
    }

    private static Grid SliderRow(string title, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var value = new TextBlock { Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        var slider = new Slider { Minimum = min, Maximum = max, Value = get(), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        value.Text = format(slider.Value);

        // Live preview while dragging, but rebuild-heavy settings are debounced by App.ApplySettings.
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            set(e.NewValue);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(value);
        return Row(title, null, panel);
    }

    private static ComboBox Choice(string[] options, Func<string> get, Action<string> set)
    {
        var box = new ComboBox { MinWidth = 110, ItemsSource = options, SelectedItem = options.Contains(get()) ? get() : options[0] };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) set(s); };
        return box;
    }

    private static Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 5, 14, 6), MinWidth = 90 };
        b.Click += (_, _) => action();
        return b;
    }

    #endregion
}
