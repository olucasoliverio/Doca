using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace Doca;

/// <summary>A wireless peripheral's battery, as last reported.</summary>
internal sealed class Peripheral
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    /// <summary>0–100, or null while unknown or asleep.</summary>
    public int? Level { get; set; }
    public bool Charging { get; set; }
    public bool Asleep { get; set; }
    public DateTime Seen { get; set; }
}

/// <summary>
/// Battery of 2.4 GHz peripherals through their USB receivers. There is no standard for this (no HID Battery
/// Strength usage on these devices), so each one is a small vendor protocol, learned from the vendor's own app:
/// <list type="bullet">
/// <item>Rapoo gaming mouse (24AE): the receiver sends a status report on its FF00:0002 collection every few
/// seconds by itself; report 07 20 …, byte 8 = battery %. Doca only listens.</item>
/// <item>MCHOSE V9 PRO headset (291D:385D): on the FF90 collection, output report 55 with 65 01 asks for the
/// battery; the reply (and unsolicited reports when charging starts/stops) is 55 65 [level] [state], state 2 =
/// on battery, 3 = charging, 4 = full, 1A = asleep. Same query MCHOSE HUB sends, every 10 minutes.</item>
/// </list>
/// Each device runs on async reads (no polling of its own beyond the headset query); a missing receiver is looked
/// for again every minute.
/// </summary>
internal static class Peripherals
{
    public static readonly List<Peripheral> Items = new();
    /// <summary>Raised on the UI thread when a level or state changes.</summary>
    public static event Action? Changed;
    private static bool started;
    private static Timer? rescan;
    private static readonly Dictionary<string, FileStream> open = new();

    public static void Start()
    {
        if (started) return;
        started = true;
        rescan = new Timer(_ => Scan(), null, 0, 60_000);
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static void Scan()
    {
        try
        {
            foreach (var (path, vid, pid, page, usage) in Hid.Interfaces())
            {
                if (vid == 0x24AE && page == 0xFF00 && usage == 0x0002) Watch("rapoo-mouse", path, false, Rapoo);
                else if (vid == 0x291D && pid == 0x385D && page == 0xFF90) Watch("mchose-v9", path, true, Mchose);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void Watch(string key, string path, bool write, Func<string, FileStream, byte[], int, bool> parse)
    {
        lock (open) if (open.ContainsKey(key)) return;
        var h = Hid.Open(path, write);
        if (h == null) return;
        var fs = new FileStream(h, write ? FileAccess.ReadWrite : FileAccess.Read, 64, true);
        lock (open) open[key] = fs;
        _ = Task.Run(async () =>
        {
            var buf = new byte[Math.Max(64, Hid.InputLength(h))];
            Timer? query = null;
            try
            {
                if (key == "mchose-v9") query = new Timer(_ => MchoseQuery(fs), null, 500, 10 * 60_000);
                while (true)
                {
                    int n = await fs.ReadAsync(buf);
                    if (n <= 0) break;
                    parse(key, fs, buf, n);
                }
            }
            catch (Exception) { /* receiver unplugged */ }
            finally
            {
                query?.Dispose();
                lock (open) open.Remove(key);
                fs.Dispose();
                OnUi(() => { Items.RemoveAll(p => p.Key == key); Changed?.Invoke(); });
            }
        });
    }

    private static bool Rapoo(string key, FileStream _, byte[] b, int n)
    {
        if (n < 9 || b[0] != 0x07 || b[1] != 0x20 || b[8] > 100) return false;
        Update(key, "Mouse", "", b[8], charging: false, asleep: false);
        return true;
    }

    private static bool Mchose(string key, FileStream _, byte[] b, int n)
    {
        if (n < 4 || b[0] != 0x55 || b[1] != 0x65) return false;
        byte state = b[3];
        bool asleep = state == 0x1A;
        Update(key, "Headset", IslandGlyphs.Headphones, asleep || b[2] > 100 ? null : b[2], charging: state is 0x03 or 0x04, asleep);
        return true;
    }

    private static void MchoseQuery(FileStream fs)
    {
        try
        {
            var q = new byte[64];
            q[0] = 0x55; q[1] = 0x65; q[2] = 0x01;
            lock (fs) fs.Write(q, 0, q.Length);
        }
        catch { /* gone: the read loop ends too */ }
    }

    private static void Update(string key, string name, string glyph, int? level, bool charging, bool asleep)
    {
        OnUi(() =>
        {
            var p = Items.FirstOrDefault(x => x.Key == key);
            bool isNew = p == null;
            if (p == null) Items.Add(p = new Peripheral { Key = key, Name = name, Glyph = glyph });
            int? before = p.Level;
            bool wasCharging = p.Charging;
            p.Seen = DateTime.Now;
            if (p.Level == level && p.Charging == charging && p.Asleep == asleep) return;
            p.Level = level ?? p.Level; // asleep: keep the last known level, shown dimmed
            p.Charging = charging;
            p.Asleep = asleep;
            Changed?.Invoke();
            if (isNew || Island.Config?.IslandPeripherals == false) return;
            // Notices: plugged in to charge, and crossing 20%, 10% and 5% on battery.
            if (charging && !wasCharging) Island.Notify(new StatusActivity(glyph, name, level is int l ? $"Carregando · {l}%" : "Carregando", true));
            else if (!charging && level is int now && before is int prev)
                foreach (var mark in new[] { 5, 10, 20 })
                    if (prev > mark && now <= mark)
                    {
                        Island.Notify(new StatusActivity(glyph, name, $"Bateria fraca · {now}%", false, Seconds: 5, StateBrush: IslandBrushes.Orange));
                        break;
                    }
        });
    }

    /// <summary>Lowest battery among devices on battery, for the pill badge (null when none is low).</summary>
    public static Peripheral? Low(int threshold = 20) =>
        Items.Where(p => p.Level is int l && l <= threshold && !p.Charging && !p.Asleep).OrderBy(p => p.Level).FirstOrDefault();
}

/// <summary>Minimal HID access: enumerate interfaces with their top-level usage, open one.</summary>
internal static class Hid
{
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr d);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr d);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr d, out CAPS c);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, int f);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref DID did);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref DID did, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

    [StructLayout(LayoutKind.Sequential)] private struct DID { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CAPS
    {
        public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }

    /// <summary>Present HID interfaces: path, vendor, product and top-level usage page/usage.</summary>
    public static List<(string path, int vid, int pid, int page, int usage)> Interfaces()
    {
        var list = new List<(string, int, int, int, int)>();
        HidD_GetHidGuid(out var g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        try
        {
            for (int i = 0; ; i++)
            {
                var did = new DID { cbSize = Marshal.SizeOf<DID>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                string path;
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out _, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(buf + 4) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
                var m = System.Text.RegularExpressions.Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int vid = Convert.ToInt32(m.Groups[1].Value, 16), pid = Convert.ToInt32(m.Groups[2].Value, 16);
                // Only the vendors Doca knows: opening every HID device would be pointless.
                if (vid != 0x24AE && vid != 0x291D) continue;
                // Access 0: asks for the usage without opening the device for I/O (works on mice/keyboards too).
                using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid || !HidD_GetPreparsedData(h, out var data)) continue;
                try { HidP_GetCaps(data, out var caps); list.Add((path, vid, pid, caps.UsagePage, caps.Usage)); }
                finally { HidD_FreePreparsedData(data); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    public static SafeFileHandle? Open(string path, bool write)
    {
        var h = CreateFile(path, write ? 0xC0000000 : 0x80000000, 3, IntPtr.Zero, 3, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    public static int InputLength(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var data)) return 64;
        try { HidP_GetCaps(data, out var caps); return caps.InLen; }
        finally { HidD_FreePreparsedData(data); }
    }
}
