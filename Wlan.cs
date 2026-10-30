using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Doca;

internal sealed record WifiNetwork(string Ssid, int Signal, bool Secured, bool Connected, bool HasProfile, string? ProfileName, int Auth);

/// <summary>Native Wi-Fi (wlanapi.dll): list, scan, connect and disconnect without opening Windows' own flyout.</summary>
internal static class Wlan
{
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string xml, string? security, bool overwrite, IntPtr reserved, out uint reason);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
        public IntPtr pDot11Ssid, pDesiredBssidList;
        public int dot11BssType;
        public uint dwFlags;
    }

    // WLAN_AVAILABLE_NETWORK field offsets (struct is 628 bytes).
    // name[512] | ssidLen@512 ssid@516[32] | bssType@548 nBssids@552 connectable@556 reason@560 nPhy@564 phy@568[32]
    // | morePhy@600 | signal@604 | secured@608 | auth@612 | cipher@616 | flags@620 | reserved@624
    private const int NetSize = 628, OffSsidLen = 512, OffSsid = 516, OffSignal = 604, OffSecured = 608, OffAuth = 612, OffFlags = 620;
    private const int InterfaceInfoSize = 532;

    private static IntPtr handle;

    private static bool Open()
    {
        if (handle != IntPtr.Zero) return true;
        try { return WlanOpenHandle(2, IntPtr.Zero, out _, out handle) == 0; }
        catch (DllNotFoundException) { return false; }
    }

    /// <summary>The first wireless adapter, if the PC has one.</summary>
    public static Guid? Interface()
    {
        if (!Open() || WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0) return null;
        try
        {
            int count = Marshal.ReadInt32(list);
            if (count == 0) return null;
            var bytes = new byte[16];
            Marshal.Copy(list + 8, bytes, 0, 16);
            return new Guid(bytes);
        }
        finally { WlanFreeMemory(list); }
    }

    public static void Scan()
    {
        if (Interface() is not Guid g) return;
        WlanScan(handle, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Visible networks, one entry per SSID (strongest), connected first, then by signal.</summary>
    public static List<WifiNetwork> Networks()
    {
        var result = new Dictionary<string, WifiNetwork>();
        if (Interface() is not Guid g || WlanGetAvailableNetworkList(handle, ref g, 0, IntPtr.Zero, out IntPtr list) != 0)
            return new();
        try
        {
            int count = Marshal.ReadInt32(list);
            for (int i = 0; i < count; i++)
            {
                IntPtr n = list + 8 + i * NetSize;
                int len = Math.Clamp(Marshal.ReadInt32(n + OffSsidLen), 0, 32);
                if (len == 0) continue; // hidden network
                var ssidBytes = new byte[len];
                Marshal.Copy(n + OffSsid, ssidBytes, 0, len);
                string ssid = Encoding.UTF8.GetString(ssidBytes);
                string profile = Marshal.PtrToStringUni(n) ?? "";
                uint flags = (uint)Marshal.ReadInt32(n + OffFlags);
                var net = new WifiNetwork(
                    ssid,
                    Marshal.ReadInt32(n + OffSignal),
                    Marshal.ReadInt32(n + OffSecured) != 0,
                    (flags & 1) != 0,
                    (flags & 2) != 0,
                    profile.Length > 0 ? profile : null,
                    Marshal.ReadInt32(n + OffAuth));

                if (!result.TryGetValue(ssid, out var old) || Better(net, old)) result[ssid] = net;
            }
        }
        finally { WlanFreeMemory(list); }
        return result.Values.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToList();

        static bool Better(WifiNetwork a, WifiNetwork b) =>
            a.Connected != b.Connected ? a.Connected : a.HasProfile != b.HasProfile ? a.HasProfile : a.Signal > b.Signal;
    }

    public static WifiNetwork? Current() => Networks().FirstOrDefault(n => n.Connected);

    public static bool Connect(string profile)
    {
        if (Interface() is not Guid g) return false;
        var p = new WLAN_CONNECTION_PARAMETERS { wlanConnectionMode = 0, strProfile = profile, dot11BssType = 1 };
        return WlanConnect(handle, ref g, ref p, IntPtr.Zero) == 0;
    }

    public static void Disconnect()
    {
        if (Interface() is not Guid g) return;
        WlanDisconnect(handle, ref g, IntPtr.Zero);
    }

    /// <summary>Whether Doca can create a profile for this network itself (personal/open security).</summary>
    public static bool CanJoin(WifiNetwork n) => n.Auth is 1 or 4 or 7 or 9 || !n.Secured;

    /// <summary>Creates a profile for a new network (password optional for open ones) and connects.</summary>
    public static bool Join(WifiNetwork n, string? password)
    {
        if (Interface() is not Guid g) return false;
        string ssid = SecurityElement.Escape(n.Ssid) ?? n.Ssid;
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(n.Ssid));
        string security = n.Auth switch
        {
            9 => Personal("WPA3SAE", "AES", password),
            4 => Personal("WPAPSK", "AES", password),
            7 => Personal("WPA2PSK", "AES", password),
            _ => "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>",
        };
        string xml = $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{ssid}</name>
              <SSIDConfig><SSID><hex>{hex}</hex><name>{ssid}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>auto</connectionMode>
              <MSM><security>{security}</security></MSM>
            </WLANProfile>
            """;
        if (WlanSetProfile(handle, ref g, 0, xml, null, true, IntPtr.Zero, out _) != 0) return false;
        return Connect(n.Ssid);

        static string Personal(string auth, string enc, string? pass) =>
            $"<authEncryption><authentication>{auth}</authentication><encryption>{enc}</encryption><useOneX>false</useOneX></authEncryption>" +
            $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(pass ?? "")}</keyMaterial></sharedKey>";
    }

    /// <summary>Fluent glyph for a signal quality 0–100.</summary>
    public static string SignalGlyph(int q) => q < 25 ? "" : q < 50 ? "" : q < 75 ? "" : "";
}
