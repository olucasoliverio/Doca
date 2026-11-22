using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Doca;

/// <summary>What a setting change has to refresh.</summary>
internal enum Apply { Dock, Screens, Taskbar, Hotkeys }

/// <summary>
/// Preferences window (Windows 11 Fluent dark theme, laid out like macOS System Settings: a sidebar with colored
/// icon tiles, titled sections of cards, switches). Every change applies immediately.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? open;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string LogoData = "M0,0 H6.6 V6.6 H0 Z M7.4,0 H14 V6.6 H7.4 Z M0,7.4 H6.6 V14 H0 Z M7.4,7.4 H14 V14 H7.4 Z";

    private readonly DocaConfig cfg;
    private readonly ContentControl page = new();
    private readonly (string icon, string title, string subtitle, Color from, Color to, Func<UIElement> build)[] pages;
    private readonly List<NavItem> navItems = new();
    private int current = -1;

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

    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static SolidColorBrush White(byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental
    private SettingsWindow(DocaConfig cfg)
    {
        this.cfg = cfg;
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Title = "Preferências do Doca";
        Width = 920;
        Height = 660;
        MinWidth = 700;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(cfg.Font);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;

        pages = new (string, string, string, Color, Color, Func<UIElement>)[]
        {
            ("", "Geral", "Inicialização, atalhos de teclado e a aparência da tela.", C(0x9A9AA0), C(0x5C5C62), General),
            ("", "Dock", "Onde a Dock aparece, como se comporta e o tamanho dos ícones.", C(0x4C9BFF), C(0x1E62E0), DockPage),
            ("", "Topo da tela", "A ilha ou a barra de menu no alto de cada monitor.", C(0xC07BFF), C(0x7B3FE4), MenuBarPage),
            ("", "Apps fixados", "Os apps que ficam sempre na Dock, na ordem em que aparecem.", C(0xFFB340), C(0xF2680A), PinnedPage),
            ("", "Sobre", "Versão, créditos e controle do Doca.", C(0x5AD0C8), C(0x1F8E9C), AboutPage),
        };

        // Sidebar: app header + navigation (built by hand instead of a ListBox: Fluent's selection is a heavy
        // accent block, macOS uses a soft highlight).
        var side = new StackPanel { Margin = new Thickness(12, 16, 12, 16) };
        side.Children.Add(SidebarHeader());
        for (int i = 0; i < pages.Length; i++)
        {
            int n = i;
            var item = new NavItem(pages[i].icon, pages[i].title, pages[i].from, pages[i].to, () => ShowPage(n));
            navItems.Add(item);
            side.Children.Add(item);
        }
        var sidebar = new Border
        {
            Child = side,
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0))),
            BorderBrush = White(0x10),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(sidebar);
        Grid.SetColumn(page, 1);
        root.Children.Add(page);
        Content = root;
        ShowPage(0);
    }

    private void ShowPage(int n)
    {
        current = n;
        for (int i = 0; i < navItems.Count; i++) navItems[i].Selected = i == n;
        var p = pages[n];
        page.Content = Scroll(p.title, p.subtitle, p.build());
    }

    /// <summary>Rebuilds the page on screen (a choice that changes which settings exist).</summary>
    private void Reload() => Dispatcher.BeginInvoke(() => ShowPage(current));

    private static UIElement SidebarHeader()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var g = new Grid { Margin = new Thickness(6, 0, 0, 18) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(LogoTile(36, 9));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = "Doca", FontSize = 16, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = $"Preferências · {version}", FontSize = 11.5, Opacity = 0.55 });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    /// <summary>The ⊞ logo on a blue→violet squircle (sidebar header and Sobre).</summary>
    private static Border LogoTile(double size, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius),
        Background = Frozen(new LinearGradientBrush(C(0x4C9BFF), C(0x8A4DFF), 45)),
        Child = new Path
        {
            Data = Geometry.Parse(LogoData), Fill = Brushes.White, Stretch = Stretch.Uniform,
            Width = size * 0.42, Height = size * 0.42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Colored icon tile, like the ones in macOS System Settings.</summary>
    private static Border IconTile(string glyph, Color from, Color to, double size = 26) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.27),
        Background = Frozen(new LinearGradientBrush(from, to, 90)),
        Child = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size * 0.54, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Sidebar entry: soft highlight when selected, with a short accent bar on the left (Windows 11).</summary>
    private sealed class NavItem : Border
    {
        private readonly Border marker;
        private readonly string title;
        private readonly Action click;
        private bool selected;

        public NavItem(string glyph, string title, Color from, Color to, Action click)
        {
            this.title = title;
            this.click = click;
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(8, 6, 8, 6);
            Margin = new Thickness(0, 1, 0, 1);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            marker = new Border
            {
                Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(-8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden,
            };
            marker.SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");
            g.Children.Add(marker);
            g.Children.Add(IconTile(glyph, from, to));
            var text = new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Child = g;
            MouseEnter += (_, _) => Paint();
            MouseLeave += (_, _) => Paint();
            MouseLeftButtonUp += (_, _) => click();
        }

        public bool Selected
        {
            get => selected;
            set { selected = value; Paint(); }
        }

        private void Paint()
        {
            Background = selected ? White(0x16) : IsMouseOver ? White(0x0B) : Brushes.Transparent;
            marker.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }

        // A plain Border is invisible to screen readers and UI Automation: expose it as a button.
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(NavItem owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
            protected override string GetNameCore() => owner.title;
            protected override bool IsControlElementCore() => true;
            public override object? GetPattern(PatternInterface p) => p == PatternInterface.Invoke ? this : base.GetPattern(p);
            public void Invoke() => owner.click();
        }
    }

    #region Pages

    private UIElement General() => Stack(
        Section("Inicialização"),
        Card(
            Toggle("Iniciar com o Windows", "Abre o Doca automaticamente quando você entra no Windows.",
                () => Startup.Enabled, _ => Startup.Toggle()),
            Toggle("Esconder a barra de tarefas do Windows", "A Dock e a barra de menu substituem a taskbar. Ela volta ao sair do Doca.",
                () => cfg.HideTaskbar, v => { cfg.HideTaskbar = v; Changed(Apply.Taskbar); })),
        Section("Atalhos de teclado"),
        Card(
            Toggle("Alternar entre janelas do mesmo app", "Como Cmd+` no Mac: troca entre as janelas do app em foco (ex.: duas janelas do Chrome). Com Shift, volta.",
                () => cfg.WindowCycleHotkey, v => { cfg.WindowCycleHotkey = v; Changed(Apply.Hotkeys); }),
            Row("Tecla do atalho", "A tecla à esquerda do 1, junto com:",
                Choice(new[] { "Alt", "Ctrl" }, () => cfg.WindowCycleModifier, v => { cfg.WindowCycleModifier = v; Changed(Apply.Hotkeys); })),
            Toggle("Spotlight com Alt+Espaço", "Busca rápida de apps, configurações e contas no centro da tela. A lupa da barra também abre.",
                () => cfg.Spotlight, v => { cfg.Spotlight = v; Changed(Apply.Hotkeys); })),
        Section("Tela"),
        Card(
            Toggle("Cantos arredondados na tela", "Arredonda os quatro cantos de cada monitor, como num Mac.",
                () => cfg.ScreenCorners, v => { cfg.ScreenCorners = v; Changed(Apply.Screens); }),
            SliderRow("Raio dos cantos", 4, 32, () => cfg.ScreenCornerRadius, v => { cfg.ScreenCornerRadius = Math.Round(v); Changed(Apply.Screens); }, v => $"{v:0} px")),
        Section("Avançado"),
        Card(
            Row("Arquivo de configuração", DocaConfig.FilePath,
                Btn("Abrir pasta", () => Shell.Open("explorer.exe", $"\"{DocaConfig.Dir}\""), ""))));

    private UIElement DockPage() => Stack(
        Section("Comportamento"),
        Card(
            Toggle("Mostrar em todas as telas", "Uma Dock em cada monitor. Desligado: só na tela principal.",
                () => cfg.DockAllScreens, v => { cfg.DockAllScreens = v; Changed(Apply.Screens); }),
            Toggle("Ocultar automaticamente", "A Dock fica escondida e aparece ao encostar o mouse na borda de baixo.",
                () => cfg.AutoHideDock, v => { cfg.AutoHideDock = v; Changed(Apply.Dock); }),
            Toggle("Mostrar em tela cheia ao apontar para baixo", "Num vídeo em tela cheia, deixar o ponteiro na borda de baixo mostra a Dock por cima, como no Mac.",
                () => cfg.DockRevealInFullscreen, v => { cfg.DockRevealInFullscreen = v; Changed(Apply.Dock); }),
            Toggle("Pré-visualização das janelas", "Ao parar o mouse sobre um app aberto, mostra miniaturas ao vivo das janelas dele.",
                () => cfg.WindowPreviews, v => { cfg.WindowPreviews = v; Changed(Apply.Dock); })),
        Section("Ícones e fundo"),
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

    private UIElement MenuBarPage() => cfg.IslandOnly && cfg.DynamicIsland ? IslandPage() : BarPage();

    /// <summary>Island or full bar; switching rebuilds this page (each mode has its own settings).</summary>
    private Border ModeCard() => Card(
        Row("O que fica no topo", "Só a ilha: uma pílula com o relógio que abre um painel com música, calendário, controles e bandeja. Barra completa: menus do app, ícones e relógio de ponta a ponta.",
            Choice(new[] { "Só a ilha", "Barra completa" }, () => cfg.IslandOnly && cfg.DynamicIsland ? "Só a ilha" : "Barra completa",
                v =>
                {
                    cfg.IslandOnly = v == "Só a ilha";
                    if (cfg.IslandOnly) cfg.DynamicIsland = true;
                    Changed(Apply.Screens);
                    Reload();
                })));

    private UIElement IslandPage() => Stack(
        Section("Modo"),
        ModeCard(),
        Section("Comportamento"),
        Card(
            Toggle("Abrir ao passar o mouse", "Desligado: clique na ilha para abrir o painel (ela não abre sem querer quando você vai até as abas do navegador). Ligado: basta parar o ponteiro sobre ela.",
                () => cfg.IslandOpenOnHover, v => { cfg.IslandOpenOnHover = v; Changed(Apply.Screens); }),
            Toggle("Reservar espaço no topo", "Janelas maximizadas começam logo abaixo da ilha, então nada fica escondido embaixo dela. Desligado: as janelas usam a tela inteira e a ilha fica por cima.",
                () => cfg.IslandReserveTop, v => { cfg.IslandReserveTop = v; Changed(Apply.Screens); }),
            Row("Telas", "Uma ilha em cada monitor, só na principal, ou uma só que vai para a tela onde está o mouse.",
                Choice(new[] { "Todas", "Só a principal", "Seguir o mouse" },
                    () => cfg.IslandFollowCursor ? "Seguir o mouse" : cfg.MenuBarAllScreens ? "Todas" : "Só a principal",
                    v =>
                    {
                        cfg.IslandFollowCursor = v == "Seguir o mouse";
                        if (!cfg.IslandFollowCursor) cfg.MenuBarAllScreens = v == "Todas";
                        Changed(Apply.Screens);
                    }))),
        Section("Tema e cores"),
        ThemeCard(),
        Section("Forma e tamanho"),
        Card(
            Row("Formato", "Entalhe: preso à borda de cima, como a câmera de um notebook. Ilha: flutuando um pouco abaixo da borda, arredondada em volta.",
                Choice(new[] { "Entalhe", "Ilha flutuante" }, () => cfg.IslandShape == "Ilha" ? "Ilha flutuante" : "Entalhe",
                    v => { cfg.IslandShape = v == "Ilha flutuante" ? "Ilha" : "Entalhe"; Changed(Apply.Screens); })),
            SliderRow("Distância do topo (ilha flutuante)", 2, 30, () => cfg.IslandGap, v => { cfg.IslandGap = Math.Round(v); Changed(Apply.Screens); }, v => $"{v:0} px"),
            Row("Tamanho", "Da ilha fechada.",
                Choice(new[] { "Pequena", "Média", "Grande" }, () => cfg.IslandSize, v => { cfg.IslandSize = v; Changed(Apply.Screens); })),
            SliderRow("Cantos do painel", 16, 56, () => cfg.IslandCorner, v => { cfg.IslandCorner = Math.Round(v); Changed(Apply.Screens); }, v => $"{v:0} px")),
        Section("Ilha fechada"),
        Card(
            Row("Com a ilha fechada", "O que aparece no centro da ilha.",
                Choice(new[] { "Hora", "Data e hora", "Hora e CPU" }, () => cfg.IslandRestFace, v => { cfg.IslandRestFace = v; Changed(Apply.Screens); })),
            Toggle("Mostrar os segundos", "No relógio da ilha. A data completa aparece ao parar o ponteiro sobre ela.",
                () => cfg.ClockShowSeconds, v => { cfg.ClockShowSeconds = v; Changed(Apply.Screens); }),
            Toggle("Música tocando", "Capa e equalizador na ilha enquanto algo toca (Spotify, YouTube…), e o aviso de nova faixa.",
                () => cfg.ShowNowPlaying, v => { cfg.ShowNowPlaying = v; Changed(Apply.Screens); }),
            Toggle("Cor da música na ilha", "Enquanto algo toca, a cor da capa do álbum aparece suavemente na parte de baixo da ilha.",
                () => cfg.IslandMusicTint, v => { cfg.IslandMusicTint = v; Changed(Apply.Screens); })),
        Section("Painel"),
        TabsCard(),
        Card(WidgetToggles()),
        Card(
            Toggle("Ícones da bandeja monocromáticos", "Na aba Bandeja do painel. Avisos em vermelho (mensagens não lidas) continuam coloridos.",
                () => cfg.TrayIconsMonochrome, v => { cfg.TrayIconsMonochrome = v; Changed(Apply.Screens); })),
        Section("Avisos e recursos"),
        Card(
            Toggle("Histórico do que você copia", "Aba Copiados: textos, imagens e arquivos copiados, para copiar de novo. Fica só na memória (os textos fixados são salvos). Senhas copiadas de gerenciadores de senha não entram.",
                () => cfg.IslandClipboard, v => { cfg.IslandClipboard = v; if (!v) ClipboardHistory.Clear(); Changed(Apply.Screens); }),
            Toggle("Câmera e microfone em uso", "Um ponto verde (câmera) ou laranja (microfone) na ilha enquanto algum app usa, e o nome do app quando começa.",
                () => cfg.IslandPrivacy, v => { cfg.IslandPrivacy = v; Changed(Apply.Screens); }),
            Toggle("Downloads do navegador", "O tamanho baixado aparece na ilha; ao terminar, clique no aviso para ver o arquivo na pasta.",
                () => cfg.IslandDownloads, v => { cfg.IslandDownloads = v; Changed(Apply.Screens); }),
            Toggle("Bateria do mouse e do headset", "Mouse Rapoo e headset MCHOSE V9 PRO sem fio (2.4 GHz): cartão Baterias no Início, aviso ao começar a carregar e abaixo de 20%, 10% e 5%, e a porcentagem na ilha quando estiver baixa.",
                () => cfg.IslandPeripherals, v => { cfg.IslandPeripherals = v; Changed(Apply.Screens); }),
            Toggle("Ctrl+Alt+M muta o microfone", "Muta todos os microfones de uma vez (vale para qualquer app: Discord, jogo, chamada). Um microfone vermelho fica na ilha enquanto estiver mudo.",
                () => cfg.IslandMicHotkey, v => { cfg.IslandMicHotkey = v; Changed(Apply.Hotkeys); }),
            Toggle("Claude Code na ilha", "Mostra quando o Claude está trabalhando e avisa quando ele precisa de você ou termina uma tarefa longa. Usa os hooks do Claude Code (Doca.exe --claude).",
                () => cfg.IslandAgents, v => { cfg.IslandAgents = v; Changed(Apply.Screens); }),
            Toggle("Esconder de prints e gravações", "A ilha não aparece em capturas de tela, gravações nem transmissões (OBS, Discord).",
                () => cfg.IslandHideFromCapture, v => { cfg.IslandHideFromCapture = v; Changed(Apply.Screens); })),
        Hint("Clique na ilha para abrir o painel; troque de aba clicando ou com a rodinha. Arraste arquivos até a ilha para guardá-los na aba Arquivos e arraste-os de lá quando for usar. Botão direito na ilha (ou ⋯ no painel): menu do sistema; ⏻ no painel: desligar, reiniciar e suspender. A ilha também avisa volume, brilho, nova música, fones Bluetooth (com a bateria), Caps Lock, downloads e o fim do timer."));

    private static readonly Dictionary<string, string> AccentColors = new()
    {
        ["Do tema"] = "#FFFFFF", ["Azul"] = "#0A84FF", ["Verde"] = "#30D158", ["Laranja"] = "#FF9F0A", ["Rosa"] = "#FF375F", ["Roxo"] = "#BF5AF2", ["Amarelo"] = "#FFD60A",
    };
    private static readonly string[] AllTabs = { "Início", "Música", "Calendário", "Controles", "Bandeja", "Arquivos", "Copiados", "Timer" };

    /// <summary>Theme and accent as clickable swatches (a list of names said nothing about the colors).</summary>
    private Border ThemeCard()
    {
        var accents = new WrapPanel();
        var accentName = SideLabel();
        void FillAccents()
        {
            var theme = IslandTheme.Find(cfg);
            string selected = NameOf(AccentColors, cfg.IslandAccent);
            accentName.Text = selected;
            accents.Children.Clear();
            foreach (var (name, hex) in AccentColors)
            {
                // "Do tema": the theme's own gradient with its accent in the middle.
                Brush fill = name == "Do tema"
                    ? Frozen(new LinearGradientBrush(theme.Top, theme.Bottom, 90))
                    : IslandTheme.Brush(IslandBrushes.Parse(hex, Colors.White));
                UIElement? inner = name == "Do tema"
                    ? new Ellipse { Width = 10, Height = 10, Fill = IslandTheme.Brush(theme.Accent) }
                    : null;
                accents.Children.Add(Swatch(name, fill, inner, name == selected, () =>
                {
                    cfg.IslandAccent = hex;
                    Changed(Apply.Screens);
                    FillAccents();
                }));
            }
        }

        var themes = new WrapPanel();
        var themeName = SideLabel();
        void FillThemes()
        {
            string selected = IslandTheme.Find(cfg).Name;
            themeName.Text = selected;
            themes.Children.Clear();
            foreach (var t in IslandTheme.All)
            {
                // The theme's symbol (♥, ✿, ✦, ★) or a dot, in its ink: shows how text reads on it.
                var mark = new TextBlock
                {
                    Text = t.Deco ?? "●", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = t.Deco == null ? 7 : 11,
                    Foreground = IslandTheme.Brush(t.Deco == null ? t.Accent : t.Ink),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                themes.Children.Add(Swatch(t.Name, Frozen(new LinearGradientBrush(t.Top, t.Bottom, 90)), mark, t.Name == selected, () =>
                {
                    cfg.IslandTheme = t.Name;
                    Changed(Apply.Screens);
                    FillThemes();
                    FillAccents();
                }));
            }
        }
        FillThemes();
        FillAccents();

        return Card(
            StackedRow("Tema", "Os painéis da ilha seguem o tema. Os símbolos enfeitam as barras de progresso.", themeName, themes),
            StackedRow("Cor de destaque", "Sliders, barras de progresso e a aba selecionada.", accentName, accents));
    }

    private static TextBlock SideLabel() => new() { FontSize = 12.5, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Top };

    /// <summary>A color circle; the selected one gets an accent ring.</summary>
    private static FrameworkElement Swatch(string name, Brush fill, UIElement? inner, bool selected, Action pick)
    {
        var ring = new Ellipse { Width = 36, Height = 36, StrokeThickness = 2 };
        if (selected) ring.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        var dot = new Ellipse { Width = 28, Height = 28, Fill = fill, Stroke = White(0x30), StrokeThickness = 1 };
        var g = new Grid { Width = 38, Height = 38, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = name, Background = Brushes.Transparent };
        g.Children.Add(ring);
        g.Children.Add(dot);
        if (inner != null) g.Children.Add(inner);
        g.MouseEnter += (_, _) => { if (!selected) ring.Stroke = White(0x40); };
        g.MouseLeave += (_, _) => { if (!selected) ring.Stroke = null; };
        g.MouseLeftButtonUp += (_, _) => pick();
        return g;
    }

    /// <summary>
    /// The panel's tabs: on/off and order (arrows). Hidden tabs are listed after the visible ones; at least one
    /// stays on.
    /// </summary>
    private Border TabsCard()
    {
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            list.Children.Add(Row("Abas do painel", "Quais aparecem no topo do painel e em que ordem. A rodinha passa por elas nessa ordem.", null));
            var visible = cfg.IslandTabs.Where(AllTabs.Contains).Distinct().ToList();
            var all = visible.Concat(AllTabs.Except(visible)).ToList();
            for (int i = 0; i < all.Count; i++)
            {
                string tab = all[i];
                bool on = visible.Contains(tab);
                int pos = visible.IndexOf(tab);
                var controls = new StackPanel { Orientation = Orientation.Horizontal };
                controls.Children.Add(Arrow("", "Mover para a esquerda", on && pos > 0, () => Move(visible, pos, -1)));
                controls.Children.Add(Arrow("", "Mover para a direita", on && pos < visible.Count - 1, () => Move(visible, pos, +1)));
                var sw = new Switch(on) { Margin = new Thickness(14, 0, 0, 0) };
                sw.Toggled += v =>
                {
                    if (v) visible.Add(tab);
                    else if (visible.Count > 1) visible.Remove(tab);
                    else { sw.IsOn = true; return; } // the panel needs at least one tab
                    Save(visible);
                };
                controls.Children.Add(sw);
                var row = Row(tab, null, controls);
                row.Margin = new Thickness(0, 6, 0, 6);
                row.Opacity = on ? 1 : 0.55;
                list.Children.Add(Separator());
                list.Children.Add(row);
            }
        }
        void Move(List<string> visible, int pos, int by)
        {
            (visible[pos], visible[pos + by]) = (visible[pos + by], visible[pos]);
            Save(visible);
        }
        void Save(List<string> visible)
        {
            cfg.IslandTabs = visible.ToList();
            Changed(Apply.Screens);
            Fill();
        }
        Fill();
        return Card(list);
    }

    private static Button Arrow(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 11 },
            Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled, MinWidth = 0,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static readonly string[] AllWidgets = { "Música", "Calendário", "Sistema", "Timer", "Baterias", "Downloads", "Copiados" };

    private static string NameOf(Dictionary<string, string> map, string hex) =>
        map.FirstOrDefault(kv => kv.Value.Equals(hex, StringComparison.OrdinalIgnoreCase)).Key ?? map.Keys.First();

    /// <summary>Which widgets the panel's Início tab shows (kept in this order).</summary>
    private UIElement[] WidgetToggles()
    {
        var rows = new List<UIElement> { Row("Widgets do Início", "A primeira aba do painel mostra estes cartões, dois por linha.", null) };
        foreach (var w in AllWidgets)
            rows.Add(Toggle(w, null, () => cfg.IslandWidgets.Contains(w), v =>
            {
                var set = cfg.IslandWidgets.ToHashSet();
                if (v) set.Add(w); else set.Remove(w);
                cfg.IslandWidgets = AllWidgets.Where(set.Contains).ToList();
                Changed(Apply.Screens);
            }));
        return rows.ToArray();
    }

    private UIElement BarPage() => Stack(
        Section("Modo"),
        ModeCard(),
        Section("Barra"),
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
                () => cfg.MenuBarRevealInFullscreen, v => { cfg.MenuBarRevealInFullscreen = v; Changed(Apply.Screens); }),
            Toggle("Menus do app", "Mostra os menus do app em foco (Arquivo, Editar…). Apps sem menu ganham menus padrão com os atalhos universais.",
                () => cfg.AppMenus, v => { cfg.AppMenus = v; Changed(Apply.Screens); })),
        Section("Bandeja do sistema"),
        Card(
            Toggle("Ícones da bandeja", "Ícones dos apps em segundo plano (Discord, Steam…), inclusive os que o Windows esconde.",
                () => cfg.ShowTrayIcons, v => { cfg.ShowTrayIcons = v; Changed(Apply.Screens); }),
            Toggle("Ícones da bandeja monocromáticos", "Desenha os ícones em branco, como no Mac. Avisos em vermelho (mensagens não lidas) continuam coloridos.",
                () => cfg.TrayIconsMonochrome, v => { cfg.TrayIconsMonochrome = v; Changed(Apply.Screens); }),
            Row("Ordem dos ícones da bandeja", "Arraste os ícones na barra para reordenar, ou até ⌄ para recolher.",
                Btn("Restaurar", () => { cfg.TrayOrder.Clear(); cfg.TrayCollapsed.Clear(); Changed(Apply.Screens); }, ""))),
        TrayIconsCard(),
        Section("Ícones de status"),
        Card(
            Toggle("Wi-Fi na barra", "Sinal e rede atual. Também fica na Central de Controle.",
                () => cfg.ShowWifi, v => { cfg.ShowWifi = v; Changed(Apply.Screens); }),
            Toggle("Bluetooth na barra", "Também fica na Central de Controle.",
                () => cfg.ShowBluetooth, v => { cfg.ShowBluetooth = v; Changed(Apply.Screens); }),
            Toggle("Som na barra", "Role sobre o ícone para mudar o volume; botão direito silencia. Também fica na Central de Controle.",
                () => cfg.ShowVolume, v => { cfg.ShowVolume = v; Changed(Apply.Screens); })),
        Section("Ilha e música"),
        Card(
            Toggle("Ilha dinâmica", "Pílula no centro da barra, como no iPhone: mostra a música e aparece por um instante ao mudar volume ou brilho e ao conectar fones Bluetooth. Passe o mouse para os controles.",
                () => cfg.DynamicIsland, v => { cfg.DynamicIsland = v; Changed(Apply.Screens); }),
            Toggle("Música tocando", "Mostra o que está tocando (Spotify, YouTube…): na ilha ou, sem ela, à direita da barra.",
                () => cfg.ShowNowPlaying, v => { cfg.ShowNowPlaying = v; Changed(Apply.Screens); })),
        Section("Relógio"),
        Card(
            Toggle("Mostrar a data no relógio", "Ex.: Qui 29 Out 21:35",
                () => cfg.ClockShowDate, v => { cfg.ClockShowDate = v; Changed(Apply.Screens); }),
            Toggle("Mostrar os segundos", null,
                () => cfg.ClockShowSeconds, v => { cfg.ClockShowSeconds = v; Changed(Apply.Screens); })));

    /// <summary>Every running tray icon with "show in the bar" (off = behind ⌄).</summary>
    private UIElement TrayIconsCard()
    {
        var rows = new List<UIElement> { Row("Mostrar na barra", "Desligados ficam no ⌄. Os que não couberem na barra também vão para lá.", null) };
        foreach (var icon in TrayIconsPanel.Ordered(cfg))
        {
            string title = (icon.Title ?? "").Split('\n')[0].Trim();
            if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(icon.Path ?? "") is { Length: > 0 } n ? n : "Ícone sem nome";
            if (title.Length > 50) title = title[..47] + "…";

            var sw = new Switch(!TrayIconsPanel.IsCollapsed(cfg, icon));
            var id = icon.Identifier ?? "";
            sw.Toggled += v =>
            {
                cfg.TrayCollapsed.Remove(id);
                if (!v) cfg.TrayCollapsed.Add(id);
                cfg.Save();
                TrayIconsPanel.RaiseChanged();
            };

            var g = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Image { Source = icon.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Grid.SetColumn(sw, 2);
            g.Children.Add(sw);
            rows.Add(g);
        }
        if (rows.Count == 1) rows.Add(Row("Nenhum ícone de bandeja no momento.", null, null));
        return Card(rows.ToArray());
    }

    private UIElement PinnedPage()
    {
        var list = new ListBox
        {
            MinHeight = 120, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 6, -10, 6),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        void Fill(int select = -1)
        {
            list.Items.Clear();
            foreach (var p in cfg.Pinned)
            {
                var img = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 14, 0) };
                Shell.RequestIcon(p.IconSource, s => img.Source = s);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
                row.Children.Add(img);
                row.Children.Add(new TextBlock { Text = p.DisplayName, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
                list.Items.Add(new ListBoxItem { Content = row, Tag = p });
            }
            if (cfg.Pinned.Count == 0)
                list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Nenhum app fixado ainda.", Opacity = 0.6 }, IsEnabled = false });
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

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var add = Btn("Adicionar…", () =>
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
        }, "");
        add.SetResourceReference(StyleProperty, "AccentButtonStyle");
        buttons.Children.Add(add);
        buttons.Children.Add(Btn("Remover", () =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: PinnedApp p }) return;
            int i = list.SelectedIndex;
            cfg.Pinned.Remove(p);
            Changed(Apply.Dock);
            Fill(Math.Min(i, cfg.Pinned.Count - 1));
        }, ""));
        buttons.Children.Add(Btn("Subir", () => Move(-1), ""));
        buttons.Children.Add(Btn("Descer", () => Move(1), ""));

        Fill();
        return Stack(
            Section("Na Dock"),
            Card(list),
            buttons,
            Hint("Também dá para arrastar arquivos e apps para a Dock, e arrastar os ícones para reordenar."));
    }

    private UIElement AboutPage()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 22) };
        var logo = LogoTile(84, 20);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        logo.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = C(0x3A2A90) };
        hero.Children.Add(logo);
        hero.Children.Add(new TextBlock { Text = "Doca", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 2) });
        hero.Children.Add(new TextBlock { Text = $"Versão {version}", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
        hero.Children.Add(new TextBlock { Text = "Dock e barra de menu estilo macOS para Windows", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });

        return Stack(
            hero,
            Section("Créditos"),
            Card(
                Row("Bandeja do sistema", "ManagedShell (MIT) · github.com/cairoshell/ManagedShell", null)),
            Section("Controle"),
            Card(
                Row("Reiniciar o Doca", "Recarrega tudo do zero.", Btn("Reiniciar", App.Restart, "")),
                Row("Sair do Doca", "Restaura a barra de tarefas do Windows.", Btn("Sair", () => Application.Current.Shutdown(), ""))));
    }

    #endregion

    #region Building blocks

    private void Changed(Apply what)
    {
        cfg.Save();
        App.ApplySettings(what);
    }

    private static UIElement Scroll(string title, string subtitle, UIElement body)
    {
        // Stretch (not Left): pages whose cards have short rows (Sobre, Apps fixados) still fill the width.
        var s = new StackPanel { Margin = new Thickness(36, 26, 36, 32), MaxWidth = 780 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold });
        s.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap });
        s.Children.Add(body);
        return new ScrollViewer { Content = s, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>Small title above a group of cards.</summary>
    private static TextBlock Section(string title) => new()
    {
        Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0.75, Margin = new Thickness(2, 18, 0, 8),
    };

    private static Border Separator() => new() { Height = 1, Background = White(0x14), Margin = new Thickness(0) };

    private static Border Card(params UIElement[] rows)
    {
        var s = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) s.Children.Add(Separator());
            s.Children.Add(rows[i]);
        }
        return new Border
        {
            Child = s,
            Background = White(0x0D),
            BorderBrush = White(0x14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 2, 18, 2),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>Info bar: tips that used to be plain gray text.</summary>
    private static Border Hint(string text)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 15, Margin = new Thickness(0, 1, 12, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        g.Children.Add(icon);
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, LineHeight = 19 };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return new Border
        {
            Child = g, Background = White(0x08), BorderBrush = White(0x12), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 16, 0, 8),
        };
    }

    private static StackPanel RowText(string title, string? description)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 17 });
        return text;
    }

    private static Grid Row(string title, string? description, UIElement? control)
    {
        var g = new Grid { Margin = new Thickness(0, 13, 0, 13), MinHeight = 32 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(RowText(title, description));
        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }
        return g;
    }

    /// <summary>Title and description on top, a wide control (swatches) below; the current value on the right.</summary>
    private static Grid StackedRow(string title, string description, UIElement side, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 13, 0, 9) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(RowText(title, description));
        Grid.SetColumn(side, 1);
        g.Children.Add(side);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(-1, 12, 0, 0);
        Grid.SetRow(control, 1);
        Grid.SetColumnSpan(control, 2);
        g.Children.Add(control);
        return g;
    }

    /// <summary>A switch row; clicking anywhere on the row flips it, as in Windows and macOS settings.</summary>
    private static Grid Toggle(string title, string? description, Func<bool> get, Action<bool> set)
    {
        var sw = new Switch(get());
        System.Windows.Automation.AutomationProperties.SetName(sw, title);
        sw.Toggled += set;
        var row = Row(title, description, sw);
        row.Background = Brushes.Transparent; // hit-testable between the texts
        row.MouseLeftButtonUp += (_, e) => { if (!e.Handled) sw.Flip(); };
        return row;
    }

    private static Grid SliderRow(string title, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var value = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 13 };
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
        var box = new ComboBox { MinWidth = 160, ItemsSource = options, SelectedItem = options.Contains(get()) ? get() : options[0] };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) set(s); };
        return box;
    }

    private static Button Btn(string text, Action action, string? glyph = null)
    {
        object content = text;
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 7), MinWidth = 90 };
        b.Click += (_, _) => action();
        return b;
    }

    #endregion
}

/// <summary>
/// Windows 11 style toggle switch (WPF's Fluent theme has none; a CheckBox read like a form, not a setting).
/// The knob slides with a short ease; on = the accent color.
/// </summary>
internal sealed class Switch : Border
{
    private readonly Border knob;
    private readonly TranslateTransform move = new();
    private readonly ScaleTransform grow = new(1, 1, 6, 6);
    private bool on;

    /// <summary>Raised by a click or Space/Enter (not when IsOn is set from code).</summary>
    public event Action<bool>? Toggled;

    public Switch(bool on)
    {
        Width = 40;
        Height = 20;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        Focusable = true;
        var t = new TransformGroup();
        t.Children.Add(grow);
        t.Children.Add(move);
        knob = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransform = t,
        };
        Child = knob;
        this.on = on;
        Paint(false);
        MouseLeftButtonUp += (_, e) => { e.Handled = true; Flip(); };
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; Flip(); } };
        // The knob grows a little under the pointer, like the Windows 11 switch.
        MouseEnter += (_, _) => Grow(1.17);
        MouseLeave += (_, _) => Grow(1);
    }

    public bool IsOn
    {
        get => on;
        set { if (on == value) return; on = value; Paint(true); }
    }

    public void Flip()
    {
        IsOn = !on;
        Toggled?.Invoke(on);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Screen readers see a toggle (named after its row's title by the row, via AutomationProperties).</summary>
    private sealed class Peer(Switch owner) : FrameworkElementAutomationPeer(owner), IToggleProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
        protected override bool IsControlElementCore() => true;
        public override object? GetPattern(PatternInterface p) => p == PatternInterface.Toggle ? this : base.GetPattern(p);
        public ToggleState ToggleState => owner.on ? ToggleState.On : ToggleState.Off;
        public void Toggle() => owner.Flip();
    }

    private void Grow(double s)
    {
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(120));
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void Paint(bool animate)
    {
        if (on)
        {
            SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");
            SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
            knob.SetResourceReference(BackgroundProperty, "TextOnAccentFillColorPrimaryBrush");
        }
        else
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF));
            knob.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }
        // Inner width 38: the knob travels from 3 to 38 - 3 - 12 = 23.
        double x = on ? 20 : 0;
        if (animate) move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else move.X = x;
    }
}
