namespace Doca;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The watchdog is the same exe started without any UI (no WPF loaded, a few MB of memory).
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
            return Watchdog.Run(pid);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
