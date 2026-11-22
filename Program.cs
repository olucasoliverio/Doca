namespace Doca;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The watchdog is the same exe started without any UI (no WPF loaded, a few MB of memory).
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
            return Watchdog.Run(pid);
        // Hooks of coding agents and scripts: forward to the running Doca and exit (no WPF, a few ms).
        if (args.Length >= 1 && args[0] is "--claude" or "--notify")
            return Agents.Send(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
