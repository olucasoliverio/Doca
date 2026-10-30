using System.Runtime.InteropServices;
using Windows.Devices.Enumeration.Pnp;
using Windows.Devices.Radios;

namespace Doca;

internal sealed record BtDevice(ulong Address, string Name, uint ClassOfDevice, bool Connected, int? Battery)
{
    public string AddressHex => Address.ToString("x12");
    public int MajorClass => (int)((ClassOfDevice >> 8) & 0x1F);
    public int MinorClass => (int)((ClassOfDevice >> 2) & 0x3F);
    public bool IsAudio => MajorClass == 4;

    public string Glyph => MajorClass switch
    {
        4 => MinorClass is 5 or 7 ? "" : "", // loudspeaker / portable audio vs. headset/headphones
        5 => (MinorClass & 0x10) != 0 ? "" : (MinorClass & 0x20) != 0 ? "" : "", // keyboard / mouse / gamepad
        2 => "",
        1 => "",
        _ => "",
    };
}

/// <summary>
/// Bluetooth for the menu bar: radio on/off (WinRT Radios), paired devices (bthprops), and
/// connect/disconnect of audio devices through the same KS "one-shot reconnect" request
/// Windows' own Sound flyout uses.
/// </summary>
internal static class Bluetooth
{
    #region Radios (Bluetooth and Wi-Fi on/off)

    private static Radio? btRadio, wifiRadio;
    private static bool radiosLoaded;

    public static async Task LoadRadiosAsync()
    {
        if (radiosLoaded) return;
        try
        {
            await Radio.RequestAccessAsync();
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.Bluetooth) btRadio ??= r;
                if (r.Kind == RadioKind.WiFi) wifiRadio ??= r;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        radiosLoaded = true;
    }

    public static bool HasAdapter => btRadio != null;
    public static bool IsOn => btRadio?.State == RadioState.On;
    public static bool HasWifiRadio => wifiRadio != null;
    public static bool WifiOn => wifiRadio?.State == RadioState.On;

    public static async Task<bool> SetOnAsync(bool on) => await SetRadioAsync(btRadio, on);
    public static async Task<bool> SetWifiOnAsync(bool on) => await SetRadioAsync(wifiRadio, on);

    private static async Task<bool> SetRadioAsync(Radio? r, bool on)
    {
        if (r == null) return false;
        try { return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    #endregion

    #region Paired devices

    [StructLayout(LayoutKind.Sequential)]
    private struct SEARCH_PARAMS
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected, fRemembered, fAuthenticated;
        public SYSTEMTIME stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl")] private static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO info);
    [DllImport("bthprops.cpl")] private static extern bool BluetoothFindNextDevice(IntPtr find, ref DEVICE_INFO info);
    [DllImport("bthprops.cpl")] private static extern bool BluetoothFindDeviceClose(IntPtr find);

    private static Dictionary<string, int> battery = new();

    /// <summary>Paired devices, connected first. Local query only (no radio inquiry), so it is fast.</summary>
    public static List<BtDevice> Devices()
    {
        var list = new List<BtDevice>();
        try
        {
            var p = new SEARCH_PARAMS
            {
                dwSize = Marshal.SizeOf<SEARCH_PARAMS>(),
                fReturnAuthenticated = true, fReturnRemembered = true, fReturnConnected = true,
            };
            var info = new DEVICE_INFO { dwSize = Marshal.SizeOf<DEVICE_INFO>() };
            IntPtr find = BluetoothFindFirstDevice(ref p, ref info);
            if (find == IntPtr.Zero) return list;
            do
            {
                if (info.fRemembered || info.fAuthenticated)
                {
                    var hex = info.Address.ToString("x12");
                    list.Add(new BtDevice(info.Address, info.szName, info.ulClassofDevice, info.fConnected,
                        battery.TryGetValue(hex, out var b) ? b : null));
                }
                info = new DEVICE_INFO { dwSize = Marshal.SizeOf<DEVICE_INFO>() };
            } while (BluetoothFindNextDevice(find, ref info));
            BluetoothFindDeviceClose(find);
        }
        catch (DllNotFoundException) { }
        catch (Exception ex) { App.Log(ex); }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Battery level published by connected devices (Windows 11 "{104EA319…} 2" property).</summary>
    public static async Task RefreshBatteryAsync()
    {
        const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        var result = new Dictionary<string, int>();
        try
        {
            var objs = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BatteryKey, "System.Devices.DeviceInstanceId" },
                "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"");
            foreach (var o in objs)
            {
                if (!o.Properties.TryGetValue(BatteryKey, out var v) || v is not byte) continue;
                byte level = (byte)v;
                var id = (o.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var i) ? i as string : null) ?? o.Id;
                var m = System.Text.RegularExpressions.Regex.Match(id.ToLowerInvariant(), "([0-9a-f]{12})");
                if (m.Success) result[m.Groups[1].Value] = level;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        battery = result;
    }

    #endregion

    #region Connect / disconnect audio devices (KSPROPERTY_ONESHOT_RECONNECT / DISCONNECT)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    private static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    /// <summary>Asks the Bluetooth audio driver to (re)connect or disconnect the device. Returns false when no audio endpoint matched.</summary>
    public static bool SetAudioConnected(BtDevice device, bool connect)
    {
        bool sent = false;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.EnumAudioEndpoints(2 /* eAll */, 0xF /* all states: disconnected ones are "unplugged" */, out var col) != 0) return false;
            col.GetCount(out uint n);
            var topoIid = typeof(IDeviceTopology).GUID;
            var ksIid = typeof(IKsControl).GUID;
            var done = new HashSet<string>();
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var endpoint) != 0) continue;
                if (endpoint.Activate(ref topoIid, 23, IntPtr.Zero, out object t) != 0) continue;
                var topo = (IDeviceTopology)t;
                if (topo.GetConnector(0, out var connector) != 0) continue;
                if (connector.GetDeviceIdConnectedTo(out string filterId) != 0) continue;
                // The KS filter id of a Bluetooth endpoint embeds the device address.
                string id = filterId.ToLowerInvariant();
                if (!id.Contains("bth") || !id.Contains(device.AddressHex) || !done.Add(id)) continue;
                if (en.GetDevice(filterId, out var filter) != 0) continue;
                if (filter.Activate(ref ksIid, 23, IntPtr.Zero, out object k) != 0) continue;
                var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = connect ? 0u : 1u, Flags = 1 /* KSPROPERTY_TYPE_GET */ };
                ((IKsControl)k).KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                sent = true;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return sent;
    }

    #endregion
}
