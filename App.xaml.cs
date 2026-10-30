using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Doca;

public partial class App : Application
{
    private static readonly string[] myDockFinderProcesses = { "Dock_64", "dock", "Mydock", "Dockmod", "Dockmod64", "Dockmod64arm", "trayico" };

    private Mutex? mutex;
    private DocaConfig? cfg;
    private WindowTracker? tracker;
    private readonly List<MenuBarWindow> bars = new();
    private readonly List<DockWindow> docks = new();
    private readonly List<CornerWindow> corners = new();
    private bool cleaned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "Doca.exe --quit" asks the running instance to exit cleanly (restoring taskbar and work area).
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Doca.Quit");
        if (e.Args.Contains("--quit")) { quit.Set(); Shutdown(); return; }
        // "Doca.exe --settings" opens Preferences in the running instance.
        var settings = new EventWaitHandle(false, EventResetMode.AutoReset, "Doca.Settings");
        if (e.Args.Contains("--settings") && Mutex.TryOpenExisting("Doca.SingleInstance", out _)) { settings.Set(); Shutdown(); return; }

        mutex = new Mutex(true, "Doca.SingleInstance", out bool created);
        if (!created && e.Args.Contains("--restart"))
        {
            try { created = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created) { Shutdown(); return; }
        Watchdog.Spawn();

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        // A fatal error still gives the screen back, but leaves the exit "unclean" so the watchdog restarts Doca.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log(ex.ExceptionObject as Exception); Cleanup(clean: false); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        SessionEnding += (_, _) => Cleanup();

        new Thread(() => { quit.WaitOne(); Dispatcher.BeginInvoke(() => Shutdown()); }) { IsBackground = true }.Start();
        new Thread(() => { while (settings.WaitOne()) Dispatcher.BeginInvoke(ShowSettings); }) { IsBackground = true }.Start();

        // --preview: try Doca without touching the taskbar or other docks.
        bool preview = e.Args.Contains("--preview");
        if (!preview) CloseMyDockFinder();

        cfg = DocaConfig.Load();
        tracker = new WindowTracker();
        tracker.Start();
        if (cfg.HideTaskbar && !preview) Taskbar.Hide(cfg);

        Tray.Start();
        WindowCycler.Start(cfg, tracker);
        CreateScreens();
        if (e.Args.Contains("--settings")) ShowSettings();
        tracker.FullscreenChanged += CornerWindow.BringAllToTop;

        // Safety net: Explorer sometimes re-shows its taskbar without a "show" event we can see.
        var taskbarGuard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        taskbarGuard.Tick += (_, _) => Taskbar.EnsureHidden();
        taskbarGuard.Start();

        // Startup decodes dozens of icons; give that one-time garbage back once things settle.
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        settle.Start();

        // Monitors plugged/unplugged or resolution changed: rebuild the bar and Dock of every screen.
        var displayDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        displayDebounce.Tick += (_, _) => { displayDebounce.Stop(); CreateScreens(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => { displayDebounce.Stop(); displayDebounce.Start(); });
    }

    /// <summary>One menu bar and one Dock per monitor (or only the primary one, per settings).</summary>
    private void CreateScreens()
    {
        foreach (var b in bars) b.Close();
        foreach (var d in docks) d.Close();
        foreach (var c in corners) c.Close();
        bars.Clear();
        docks.Clear();
        corners.Clear();
        foreach (var m in Native.Monitors().OrderByDescending(m => m.Primary))
        {
            if (cfg!.ScreenCorners) corners.AddRange(CornerWindow.CreateFor(m, cfg.ScreenCornerRadius));
            if (cfg!.MenuBarAllScreens || m.Primary)
            {
                var b = new MenuBarWindow(cfg, tracker!, m.Handle);
                b.Show();
                bars.Add(b);
            }
            if (cfg.DockAllScreens || m.Primary)
            {
                var d = new DockWindow(cfg, tracker!, m.Handle);
                d.Show();
                docks.Add(d);
            }
        }
        // Bars and Docks are topmost too; corners go above them.
        Dispatcher.BeginInvoke(CornerWindow.BringAllToTop, System.Windows.Threading.DispatcherPriority.Background);
    }

    private System.Windows.Threading.DispatcherTimer? screensDebounce;

    /// <summary>Applies a changed setting live (screen rebuilds are debounced so sliders stay smooth).</summary>
    internal static void ApplySettings(Apply what)
    {
        var app = (App)Current;
        switch (what)
        {
            case Apply.Dock:
                DockWindow.RaiseSettingsChanged();
                break;
            case Apply.Screens:
                if (app.screensDebounce == null)
                {
                    app.screensDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    app.screensDebounce.Tick += (_, _) => { app.screensDebounce.Stop(); app.CreateScreens(); };
                }
                app.screensDebounce.Stop();
                app.screensDebounce.Start();
                break;
            case Apply.Hotkeys:
                WindowCycler.Start(app.cfg!, app.tracker!);
                break;
            case Apply.Taskbar:
                if (app.cfg!.HideTaskbar) Taskbar.Hide(app.cfg);
                else Taskbar.Restore(app.cfg);
                break;
        }
    }

    public static void ShowSettings() => SettingsWindow.ShowSingle(((App)Current).cfg!);

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    /// <summary>Gives the screen back exactly as it was: AppBar space released, taskbar restored.</summary>
    private void Cleanup(bool clean = true)
    {
        if (cleaned) return;
        cleaned = true;
        try
        {
            foreach (var d in docks) d.ReleaseAppBar();
            foreach (var b in bars) b.ReleaseAppBar();
            tracker?.Dispose();
            WindowCycler.Stop();
            Tray.Stop();
            if (cfg != null) Taskbar.Restore(cfg);
        }
        catch (Exception ex) { Log(ex); }
        if (clean) Watchdog.MarkCleanExit();
        try { mutex?.ReleaseMutex(); } catch { }
    }

    public static void Restart()
    {
        var app = (App)Current;
        app.Cleanup();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
        app.Shutdown();
    }

    private static void CloseMyDockFinder()
    {
        // Only the Dock itself conflicts; helper/launcher processes alone are harmless.
        if (!new[] { "Dock_64", "dock" }.SelectMany(Process.GetProcessesByName).Any()) return;
        var running = myDockFinderProcesses.SelectMany(Process.GetProcessesByName).ToList();
        var answer = MessageBox.Show(
            "O MyDockFinder está aberto. Ele também esconde a barra de tarefas e reserva espaço na tela, " +
            "então os dois juntos vão conflitar.\n\nFechar o MyDockFinder agora?",
            "Doca", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var p in running)
        {
            try { p.Kill(); p.WaitForExit(2000); } catch (Exception ex) { Log(ex); }
        }
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(DocaConfig.Dir);
            File.AppendAllText(Path.Combine(DocaConfig.Dir, "log.txt"), $"[{DateTime.Now:s}] {ex}\n");
        }
        catch { }
    }
}
