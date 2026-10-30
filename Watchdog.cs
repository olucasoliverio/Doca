using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Doca;

/// <summary>
/// Separate process that waits for Doca to end. A clean exit signals an event; anything else
/// (crash, kill) leaves the desktop broken — hidden taskbar, reserved screen strips — so the
/// watchdog repairs it and, for a crash, starts Doca again (at most 3 times in 2 minutes).
/// </summary>
internal static class Watchdog
{
    private static string AppBarsFile => Path.Combine(DocaConfig.Dir, "appbars.txt");
    private static string CrashFile => Path.Combine(DocaConfig.Dir, "crashes.txt");
    private static string CleanEventName(int pid) => $"Doca.CleanExit.{pid}";

    private static EventWaitHandle? cleanExit;

    /// <summary>Main instance: start the watchdog for this process.</summary>
    public static void Spawn()
    {
        try
        {
            cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanEventName(Environment.ProcessId));
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Main instance: everything was restored, nothing to repair.</summary>
    public static void MarkCleanExit() => cleanExit?.Set();

    /// <summary>Main instance: remember AppBar windows so a dead instance's strips can be released.</summary>
    public static void SaveAppBars(IEnumerable<IntPtr> hwnds)
    {
        try
        {
            Directory.CreateDirectory(DocaConfig.Dir);
            File.WriteAllLines(AppBarsFile, hwnds.Select(h => h.ToInt64().ToString()));
        }
        catch { }
    }

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    public static int Run(int pid)
    {
        // Our own handle (opened while Doca is alive) keeps its exit code readable afterwards.
        IntPtr parent = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */ | 0x100000 /* SYNCHRONIZE */, false, (uint)pid);
        if (parent == IntPtr.Zero) return 0;

        EventWaitHandle? clean = null;
        for (int i = 0; i < 50 && clean == null; i++)
        {
            try { clean = EventWaitHandle.OpenExisting(CleanEventName(pid)); }
            catch { Thread.Sleep(100); }
        }

        WaitForSingleObject(parent, 0xFFFFFFFF);
        if (clean?.WaitOne(0) == true) { Native.CloseHandle(parent); return 0; }

        int code = GetExitCodeProcess(parent, out uint c) ? unchecked((int)c) : -1;
        Native.CloseHandle(parent);
        Repair();
        // Task Manager's "End task" exits with 1: the user wanted it closed, so don't bring it back.
        bool crashed = code != 1;
        App.Log(new Exception($"Doca terminou inesperadamente (código {code}). Barra de tarefas restaurada{(crashed ? "" : "; não será reaberto")}."));
        if (crashed && AllowRestart()) Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        return 0;
    }

    private static void Repair()
    {
        // Release the dead instance's reserved screen strips (menu bar, Dock).
        try
        {
            if (File.Exists(AppBarsFile))
            {
                foreach (var line in File.ReadAllLines(AppBarsFile))
                {
                    if (!long.TryParse(line, out long h)) continue;
                    var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = new IntPtr(h) };
                    Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
                }
                File.Delete(AppBarsFile);
            }
        }
        catch { }

        // Bring the Windows taskbar back exactly as it was before Doca hid it.
        try
        {
            if (!File.Exists(DocaConfig.FilePath)) return;
            var cfg = JsonSerializer.Deserialize<DocaConfig>(File.ReadAllText(DocaConfig.FilePath));
            if (cfg?.TaskbarAutoHideBefore != null) Taskbar.ForceRestore(cfg);
        }
        catch { }
    }

    private static bool AllowRestart()
    {
        try
        {
            var now = DateTime.UtcNow;
            var recent = (File.Exists(CrashFile) ? File.ReadAllLines(CrashFile) : Array.Empty<string>())
                .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => now - t < TimeSpan.FromMinutes(2))
                .Append(now)
                .ToList();
            File.WriteAllLines(CrashFile, recent.Select(t => t.ToString("o")));
            return recent.Count <= 3;
        }
        catch { return false; }
    }
}
