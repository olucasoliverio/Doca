using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Doca;

/// <summary>One entry of an app menu, independent of where it came from.</summary>
internal sealed class MenuNode
{
    public string Text = "";
    public string? Shortcut;
    public bool Enabled = true;
    public bool Checked;
    public bool IsSeparator;
    public Func<List<MenuNode>>? Children; // built lazily (Win32 submenus refresh on open)
    public Action? Invoke;
}

/// <summary>Top-level menus of the foreground app.</summary>
internal sealed class AppMenuSet
{
    public required string Source; // "win32", "uia" or "generic"
    public required List<MenuNode> Menus;
}

/// <summary>
/// Finds the foreground app's menus for the menu bar:
/// 1) classic Win32 menu bar (read cross-process, commands sent as WM_COMMAND);
/// 2) UI Automation MenuBar (WPF/WinForms/XAML apps; opens the app's own dropdown);
/// 3) otherwise standard macOS-style menus that send universal shortcuts.
/// </summary>
internal static class AppMenus
{
    private const byte CTRL = 0x11, SHIFT = 0x10, WIN = 0x5B;

    // UI Automation lookups are slow-ish; share one per window across all menu bars.
    private static readonly Dictionary<IntPtr, Task<AppMenuSet>> cache = new();
    public static int CacheCount => cache.Count;

    public static Task<AppMenuSet> GetAsync(IntPtr hwnd)
    {
        var win32 = FromWin32(hwnd); // cheap, always fresh
        if (win32 != null) return Task.FromResult(win32);
        if (cache.TryGetValue(hwnd, out var cached) && Native.IsWindow(hwnd)) return cached;
        if (cache.Count > 300) cache.Clear();
        return cache[hwnd] = LookupAsync(hwnd);
    }

    private static async Task<AppMenuSet> LookupAsync(IntPtr hwnd)
    {
        AppMenuSet? uia = null;
        try
        {
            uia = await Task.Run(() => { try { return FromUia(hwnd); } catch { return null; } })
                .WaitAsync(TimeSpan.FromSeconds(1.5));
        }
        catch (TimeoutException) { }
        return uia ?? Generic(hwnd);
    }

    #region Win32

    private static AppMenuSet? FromWin32(IntPtr hwnd)
    {
        IntPtr menu = Native.GetMenu(hwnd);
        if (menu == IntPtr.Zero || !Native.IsMenu(menu)) return null;
        var items = ReadMenu(hwnd, menu, refresh: false);
        items.RemoveAll(i => i.IsSeparator || string.IsNullOrWhiteSpace(i.Text));
        return items.Count == 0 ? null : new AppMenuSet { Source = "win32", Menus = items };
    }

    private static List<MenuNode> ReadMenu(IntPtr hwnd, IntPtr menu, bool refresh)
    {
        // Let the app update enabled/checked states (and build dynamic items) like it would on open.
        if (refresh)
            Native.SendMessageTimeout(hwnd, Native.WM_INITMENUPOPUP, menu, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 200, out _);

        var list = new List<MenuNode>();
        int count = Native.GetMenuItemCount(menu);
        for (uint i = 0; i < count; i++)
        {
            var info = new Native.MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<Native.MENUITEMINFO>(),
                fMask = Native.MIIM_FTYPE | Native.MIIM_STATE | Native.MIIM_ID | Native.MIIM_SUBMENU | Native.MIIM_STRING,
            };
            if (!Native.GetMenuItemInfo(menu, i, true, ref info)) continue;
            if ((info.fType & Native.MFT_SEPARATOR) != 0) { list.Add(new MenuNode { IsSeparator = true }); continue; }

            string raw = ReadString(menu, i, info.cch);
            if (raw.Length == 0) continue; // owner-drawn / bitmap items have no text
            var (text, shortcut) = SplitLabel(raw);
            var node = new MenuNode
            {
                Text = text,
                Shortcut = shortcut,
                Enabled = (info.fState & Native.MFS_DISABLED) == 0,
                Checked = (info.fState & Native.MFS_CHECKED) != 0,
            };
            IntPtr sub = info.hSubMenu;
            uint id = info.wID;
            if (sub != IntPtr.Zero) node.Children = () => ReadMenu(hwnd, sub, refresh: true);
            else node.Invoke = () =>
            {
                Shell.Focus(hwnd);
                Native.PostMessage(hwnd, Native.WM_COMMAND, new IntPtr(id & 0xFFFF), IntPtr.Zero);
            };
            list.Add(node);
        }
        return list;
    }

    private static string ReadString(IntPtr menu, uint pos, uint cch)
    {
        if (cch == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)(cch + 1) * 2);
        try
        {
            var info = new Native.MENUITEMINFO { cbSize = Marshal.SizeOf<Native.MENUITEMINFO>(), fMask = Native.MIIM_STRING, dwTypeData = buf, cch = cch + 1 };
            return Native.GetMenuItemInfo(menu, pos, true, ref info) ? Marshal.PtrToStringUni(buf) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>"&amp;Save\tCtrl+S" → ("Save", "Ctrl+S").</summary>
    private static (string, string?) SplitLabel(string raw)
    {
        string? shortcut = null;
        int tab = raw.IndexOf('\t');
        if (tab >= 0) { shortcut = raw[(tab + 1)..].Trim(); raw = raw[..tab]; }
        raw = raw.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        return (raw.Trim(), string.IsNullOrEmpty(shortcut) ? null : shortcut);
    }

    #endregion

    #region UI Automation

    private static AppMenuSet? FromUia(IntPtr hwnd)
    {
        // Breadth-first, depth- and size-limited: menu bars sit near the top of the tree
        // (XAML apps like Notepad nest them a few levels down); never walk a whole browser tree.
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement el, int depth)>();
        queue.Enqueue((AutomationElement.FromHandle(hwnd), 0));
        AutomationElement? bar = null;
        int visited = 0;
        while (queue.Count > 0 && bar == null && visited++ < 400)
        {
            var (el, depth) = queue.Dequeue();
            if (depth > 0 && IsAppMenuBar(el)) { bar = el; break; }
            if (depth >= 6) continue;
            for (var c = walker.GetFirstChild(el); c != null; c = walker.GetNextSibling(c))
                queue.Enqueue((c, depth + 1));
        }
        if (bar == null) return null;

        var menus = new List<MenuNode>();
        foreach (AutomationElement item in bar.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
        {
            string name = item.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var el = item;
            menus.Add(new MenuNode
            {
                Text = name.Replace("&", ""),
                Enabled = item.Current.IsEnabled,
                // The app opens its own dropdown (it is the only one that knows its items).
                Invoke = () =>
                {
                    Shell.Focus(hwnd);
                    Task.Run(() =>
                    {
                        try
                        {
                            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var p)) ((ExpandCollapsePattern)p).Expand();
                            else if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) ((InvokePattern)i).Invoke();
                        }
                        catch { }
                    });
                },
            });
        }
        return menus.Count == 0 ? null : new AppMenuSet { Source = "uia", Menus = menus };
    }

    private static bool IsAppMenuBar(AutomationElement el) =>
        el.Current.ControlType == ControlType.MenuBar && el.Current.AutomationId != "SystemMenuBar" && !el.Current.IsOffscreen;

    #endregion

    #region Generic (macOS-style) menus

    private static AppMenuSet Generic(IntPtr hwnd)
    {
        MenuNode Key(string text, string shortcut, params byte[] keys) => new()
        {
            Text = text,
            Shortcut = shortcut,
            Invoke = () => { Shell.Focus(hwnd); Shell.SendKeys(keys); },
        };
        MenuNode Do(string text, Action a) => new() { Text = text, Invoke = a };
        MenuNode Sep() => new() { IsSeparator = true };
        MenuNode Menu(string text, Func<List<MenuNode>> items) => new() { Text = text, Children = items };

        var menus = new List<MenuNode>
        {
            Menu("Arquivo", () => new()
            {
                Key("Nova Janela", "Ctrl+N", CTRL, (byte)'N'),
                Key("Nova Aba", "Ctrl+T", CTRL, (byte)'T'),
                Key("Abrir…", "Ctrl+O", CTRL, (byte)'O'),
                Sep(),
                Key("Salvar", "Ctrl+S", CTRL, (byte)'S'),
                Key("Salvar Como…", "Ctrl+Shift+S", CTRL, SHIFT, (byte)'S'),
                Key("Imprimir…", "Ctrl+P", CTRL, (byte)'P'),
                Sep(),
                Key("Fechar Aba", "Ctrl+W", CTRL, (byte)'W'),
                Do("Fechar Janela", () => Shell.Close(hwnd)),
            }),
            Menu("Editar", () => new()
            {
                Key("Desfazer", "Ctrl+Z", CTRL, (byte)'Z'),
                Key("Refazer", "Ctrl+Y", CTRL, (byte)'Y'),
                Sep(),
                Key("Recortar", "Ctrl+X", CTRL, (byte)'X'),
                Key("Copiar", "Ctrl+C", CTRL, (byte)'C'),
                Key("Colar", "Ctrl+V", CTRL, (byte)'V'),
                Key("Selecionar Tudo", "Ctrl+A", CTRL, (byte)'A'),
                Sep(),
                Key("Localizar…", "Ctrl+F", CTRL, (byte)'F'),
                Sep(),
                Do("Emojis e Símbolos", () => { Shell.Focus(hwnd); Shell.SendKeys(WIN, 0xBE); }),
                Do("Histórico da Área de Transferência", () => { Shell.Focus(hwnd); Shell.SendKeys(WIN, (byte)'V'); }),
            }),
            Menu("Visualizar", () => new()
            {
                Key("Aumentar Zoom", "Ctrl++", CTRL, 0xBB),
                Key("Diminuir Zoom", "Ctrl+-", CTRL, 0xBD),
                Key("Tamanho Real", "Ctrl+0", CTRL, (byte)'0'),
                Sep(),
                Key("Atualizar", "F5", 0x74),
                Key("Tela Cheia", "F11", 0x7A),
            }),
            Menu("Janela", () =>
            {
                var w = new List<MenuNode>
                {
                    Do("Minimizar", () => Shell.Minimize(hwnd)),
                    Do(Native.IsZoomed(hwnd) ? "Restaurar" : "Maximizar", () => Shell.ToggleMaximize(hwnd)),
                    Do("Alternar Janelas do App", () => { Shell.Focus(hwnd); WindowCycler.Cycle(backward: false); }),
                    Sep(),
                    Key("Encaixar à Esquerda", "Win+←", WIN, 0x25),
                    Key("Encaixar à Direita", "Win+→", WIN, 0x27),
                    Key("Mover para Outra Tela", "Win+Shift+→", WIN, SHIFT, 0x27),
                    Key("Layouts de Encaixe", "Win+Z", WIN, (byte)'Z'),
                };
                return w;
            }),
            Menu("Ajuda", () => new() { Key("Ajuda do App", "F1", 0x70) }),
        };
        return new AppMenuSet { Source = "generic", Menus = menus };
    }

    #endregion
}
