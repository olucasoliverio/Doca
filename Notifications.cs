using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Doca;

/// <summary>A Windows notification (toast) as the island shows it.</summary>
internal sealed record WinNotification(uint Id, string App, string Aumid, string Title, string Body, DateTimeOffset Time);

/// <summary>A notification that just arrived: the island shows it for a few seconds.</summary>
internal sealed record NoticeActivity(WinNotification N) : IslandActivity;

/// <summary>
/// Windows notifications (the toasts in the notification center), read with UserNotificationListener.
/// <para>
/// Unpackaged apps can read and remove notifications, but NotificationChanged throws (COMException: it needs
/// package identity), and the toast popup gives no window events either (none at all with Do Not Disturb on).
/// What does change on every notification is the platform's own database, SQLite in WAL mode: the change
/// counter in the wal-index header (wpndatabase.db-shm, offset 8) goes up with each write transaction. The
/// directory entry's size/time are not (the files are memory-mapped), so FileSystemWatcher misses them.
/// Reading those 48 bytes once a second costs microseconds; the list itself (~16 ms of CPU per call) is only
/// fetched when the counter moved.
/// </para>
/// </summary>
internal static class WinNotifications
{
    public static List<WinNotification> Items { get; private set; } = new();
    /// <summary>The list changed (arrived, removed, cleared). UI thread.</summary>
    public static event Action? Changed;
    /// <summary>Access to notifications was refused (Configurações → Privacidade → Notificações).</summary>
    public static bool Denied { get; private set; }
    /// <summary>Used to bring an app that is already open forward instead of launching it again.</summary>
    public static WindowTracker? Tracker { get; set; }

    private static readonly string Shm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db-shm");
    private static UserNotificationListener? listener;
    private static DispatcherTimer? timer;
    private static ulong lastStamp;
    private static bool loading, again, started;
    /// <summary>Ids already seen: only notifications after Doca started are announced.</summary>
    private static readonly HashSet<uint> seen = new();

    public static void Start()
    {
        if (started) return;
        started = true;
        _ = Init();
    }

    private static async Task Init()
    {
        try
        {
            listener = UserNotificationListener.Current;
            Denied = await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex) { App.Log(ex); Denied = true; }
        if (Denied) { Changed?.Invoke(); return; }
        await Load(announce: false);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private static void Poll()
    {
        if (Island.Config?.IslandNotifications == false) return;
        ulong stamp = ReadStamp();
        if (stamp == lastStamp) return;
        lastStamp = stamp;
        _ = Load(announce: true);
    }

    /// <summary>wal-index header: iChange (offset 8) and mxFrame (offset 16). 0 when the file can't be read.</summary>
    private static ulong ReadStamp()
    {
        try
        {
            using var fs = new FileStream(Shm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> b = stackalloc byte[24];
            if (fs.Read(b) < 24) return 0;
            return (ulong)BitConverter.ToUInt32(b[8..]) << 32 | BitConverter.ToUInt32(b[16..]);
        }
        catch { return 0; }
    }

    private static async Task Load(bool announce)
    {
        if (listener == null) return;
        // One fetch at a time; a change during it fetches once more.
        if (loading) { again = true; return; }
        loading = true;
        try
        {
            do
            {
                again = false;
                if (lastStamp == 0) lastStamp = ReadStamp();
                var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var items = new List<WinNotification>();
                foreach (var n in list)
                {
                    try { items.Add(Convert(n)); }
                    catch (Exception ex) { App.Log(ex); } // an app whose info can't be read (uninstalled)
                }
                items.Sort((a, b) => b.Time.CompareTo(a.Time));
                var fresh = items.Where(n => !seen.Contains(n.Id)).ToList();
                foreach (var n in items) seen.Add(n.Id);
                bool same = fresh.Count == 0 && items.Count == Items.Count;
                Items = items;
                if (!same) Changed?.Invoke();
                // Several at once (a chat catching up): only the newest gets the island.
                if (announce && fresh.Count > 0 && Island.Config?.IslandNotifications != false)
                    Island.Notify(new NoticeActivity(fresh[0]));
            } while (again);
        }
        catch (Exception ex) { App.Log(ex); }
        finally { loading = false; }
    }

    private static WinNotification Convert(UserNotification n)
    {
        string app = "", aumid = "";
        try { app = n.AppInfo.DisplayInfo.DisplayName; aumid = n.AppInfo.AppUserModelId; } catch { }
        var texts = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
            .Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new();
        string title = texts.Count > 0 ? texts[0] : app;
        string body = string.Join("\n", texts.Skip(1));
        return new WinNotification(n.Id, app.Length > 0 ? app : "Notificação", aumid, title, body, n.CreationTime);
    }

    /// <summary>Opens the app that sent it (its window if open) and clears the notification, like clicking the toast.</summary>
    public static void Open(WinNotification n)
    {
        var app = Tracker?.Apps.FirstOrDefault(a =>
            (a.Aumid != null && a.Aumid.Equals(n.Aumid, StringComparison.OrdinalIgnoreCase)) ||
            a.Name.Equals(n.App, StringComparison.OrdinalIgnoreCase));
        if (app?.Windows.Count > 0) Shell.Focus(app.Windows[0]);
        else if (n.Aumid.Length > 0) Shell.OpenAumid(n.Aumid);
        Remove(n);
    }

    public static void Remove(WinNotification n)
    {
        try { listener?.RemoveNotification(n.Id); }
        catch (Exception ex) { App.Log(ex); }
        Items = Items.Where(x => x.Id != n.Id).ToList();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        try { listener?.ClearNotifications(); }
        catch (Exception ex) { App.Log(ex); }
        Items = new();
        Changed?.Invoke();
    }

    public static string IconSource(WinNotification n) => n.Aumid.Length > 0 ? @"shell:AppsFolder\" + n.Aumid : "";

    /// <summary>"agora", "5 min", "14:32", "ontem", "12/11".</summary>
    public static string When(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        var ago = DateTime.Now - local;
        if (ago.TotalMinutes < 1) return "agora";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min";
        if (local.Date == DateTime.Today) return local.ToString("HH:mm");
        if (local.Date == DateTime.Today.AddDays(-1)) return "ontem";
        return local.ToString("dd/MM");
    }
}

/// <summary>App icon of a notification (from its AppsFolder entry), or a bell when there is none.</summary>
internal sealed class NoticeIcon : Grid
{
    public NoticeIcon(WinNotification n, double size)
    {
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        var bell = IslandBrushes.Glyph(NotificationsView.Bell, size * 0.62);
        Children.Add(bell);
        var img = new Image { Width = size, Height = size };
        Children.Add(img);
        string src = WinNotifications.IconSource(n);
        if (src.Length > 0)
            Shell.RequestIcon(src, s => { if (s == null) return; img.Source = s; bell.Visibility = Visibility.Hidden; }, 64);
    }
}

/// <summary>Aba Notificações: what is in the Windows notification center, newest first.</summary>
internal sealed class NotificationsView : StackPanel
{
    public const string Bell = "";
    private readonly StackPanel list = new();
    private readonly Action dismiss;

    public NotificationsView(Action dismiss)
    {
        this.dismiss = dismiss;
        Children.Add(new ScrollViewer
        {
            Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, -6, 0), Padding = new Thickness(0, 0, 6, 0),
        });
        Loaded += (_, _) => { WinNotifications.Changed += Fill; Fill(); };
        Unloaded += (_, _) => WinNotifications.Changed -= Fill;
    }

    private static TextBlock Empty(string text) => new()
    {
        Text = text, Opacity = 0.55, Margin = new Thickness(4, 8, 4, 10), HorizontalAlignment = HorizontalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };

    private void Fill()
    {
        list.Children.Clear();
        while (Children.Count > 1) Children.RemoveAt(1);
        if (WinNotifications.Denied)
        {
            list.Children.Add(Empty("O Windows não deixou o Doca ler as notificações."));
            var open = IslandBrushes.Button(IslandBrushes.Text("Abrir as permissões", 11.5), () => { dismiss(); Shell.Open("ms-settings:privacy-notifications"); });
            open.HorizontalAlignment = HorizontalAlignment.Center;
            list.Children.Add(open);
            return;
        }
        if (Island.Config?.IslandNotifications == false)
        {
            list.Children.Add(Empty("As notificações estão desligadas (Preferências → Topo da tela)."));
            return;
        }
        var items = WinNotifications.Items;
        if (items.Count == 0)
        {
            list.Children.Add(Empty("Nenhuma notificação."));
            return;
        }
        foreach (var n in items) list.Children.Add(Row(n));
        var footer = new DockPanel { Margin = new Thickness(2, 8, 2, 0), LastChildFill = false };
        footer.Children.Add(IslandBrushes.Text(items.Count == 1 ? "1 notificação" : $"{items.Count} notificações", 11.5, IslandBrushes.Dim));
        var clear = IslandBrushes.Button(IslandBrushes.Text("Limpar tudo", 11.5), WinNotifications.Clear, "Apaga também da central de notificações do Windows");
        DockPanel.SetDock(clear, Dock.Right);
        footer.Children.Add(clear);
        Children.Add(footer);
    }

    private UIElement Row(WinNotification n)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new NoticeIcon(n, 24) { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 2, 0, 0) };
        grid.Children.Add(icon);

        var text = new StackPanel();
        var head = new DockPanel();
        var when = IslandBrushes.Text(WinNotifications.When(n.Time), 11, IslandBrushes.Dim);
        DockPanel.SetDock(when, Dock.Right);
        when.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(when);
        head.Children.Add(IslandBrushes.Text(n.App, 11, IslandBrushes.Dim));
        text.Children.Add(head);
        if (n.Title != n.App || n.Body.Length == 0)
        {
            var title = IslandBrushes.Text(n.Title, 12.5);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(title);
        }
        if (n.Body.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = n.Body, FontSize = 12, Foreground = IslandTheme.InkA(0xD0), TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 50, Margin = new Thickness(0, 1, 0, 0),
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var close = IslandBrushes.Button(IslandBrushes.Glyph("", 10), () => WinNotifications.Remove(n), "Dispensar", 11, new Thickness(7, 6, 7, 6));
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(6, 0, 0, 0);
        close.Visibility = Visibility.Hidden;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var row = new Border { Child = grid, Padding = new Thickness(8, 9, 8, 9), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(12), Background = IslandBrushes.Card, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => { row.Background = IslandBrushes.Faint; close.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { row.Background = IslandBrushes.Card; close.Visibility = Visibility.Hidden; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            dismiss();
            WinNotifications.Open(n);
        };
        return row;
    }
}
