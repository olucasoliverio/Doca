using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Doca;

/// <summary>
/// "Abrir ao Iniciar Sessão" for Dock apps, over the same per-user entries the Task Manager's Startup tab shows:
/// values under HKCU\...\Run and shortcuts in the Startup folder. Entries apps created themselves (Steam,
/// Discord…) are never deleted: turning one off marks it disabled under Explorer\StartupApproved, exactly like
/// the Task Manager, so the app's own setting and the Task Manager stay in agreement and it can be undone.
/// Machine-wide entries (HKLM) need admin and are left alone.
/// </summary>
internal static class LoginItems
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private sealed record Entry(string Name, bool InFolder);

    public static bool CanToggle(string? exe, string? aumid) => (exe != null && File.Exists(exe)) || aumid != null;

    public static bool IsEnabled(string? exe, string? aumid) => Find(exe, aumid).Any(e => !IsDisabled(e));

    public static void Set(string? exe, string? aumid, string name, bool on)
    {
        try
        {
            var found = Find(exe, aumid);
            if (on)
            {
                if (found.Count > 0) { foreach (var e in found) SetApproved(e, true); return; }
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                string cmd = exe != null && File.Exists(exe) ? $"\"{exe}\"" : $"explorer.exe shell:AppsFolder\\{aumid}";
                run.SetValue(UniqueName(run, name), cmd);
            }
            else foreach (var e in found) SetApproved(e, false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static List<Entry> Find(string? exe, string? aumid)
    {
        var list = new List<Entry>();
        // Matched by file name, not full path: Squirrel apps start through Update.exe ("--processStart Discord.exe")
        // and their exe folder changes on every update.
        Regex? byExe = exe != null ? new Regex(@"(^|[\\/""\s])" + Regex.Escape(Path.GetFileName(exe)) + @"($|[""\s])", RegexOptions.IgnoreCase) : null;
        // Packaged apps register through their execution alias (…\WindowsApps\MSTeams_8wekyb3d8bbwe\ms-teams.exe),
        // which carries the package family name, the part of the AUMID before '!'.
        string? family = aumid?.Split('!')[0];
        bool Matches(string? cmd) => cmd != null
            && ((byExe != null && byExe.IsMatch(cmd)) || (family != null && cmd.Contains(family, StringComparison.OrdinalIgnoreCase)));

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            if (run != null)
                foreach (var v in run.GetValueNames())
                    if (Matches(run.GetValue(v) as string)) list.Add(new Entry(v, false));

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(folder))
            foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
                if (Matches(Shell.ResolveShortcut(lnk))) list.Add(new Entry(Path.GetFileName(lnk), true));
        return list;
    }

    // StartupApproved values are 12 bytes: a flag (2 = on, 3 = off; odd means disabled) and, when disabled,
    // the FILETIME of the change. A missing value means enabled.
    private static bool IsDisabled(Entry e)
    {
        using var k = Registry.CurrentUser.OpenSubKey(e.InFolder ? ApprovedFolder : ApprovedRun);
        return k?.GetValue(e.Name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    private static void SetApproved(Entry e, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(e.InFolder ? ApprovedFolder : ApprovedRun);
        var data = new byte[12];
        data[0] = on ? (byte)2 : (byte)3;
        if (!on) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        k.SetValue(e.Name, data, RegistryValueKind.Binary);
    }

    private static string UniqueName(RegistryKey run, string name)
    {
        string n = name;
        for (int i = 2; run.GetValue(n) != null; i++) n = $"{name} ({i})";
        return n;
    }
}
