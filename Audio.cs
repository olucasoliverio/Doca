using System.Runtime.InteropServices;

namespace Doca;

/// <summary>Master volume of the default output device (Core Audio).</summary>
internal static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
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
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    // Undocumented but stable since Windows 7 (used by EarTrumpet, SoundSwitch…) to change the default device.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    public sealed record OutputDevice(string Id, string Name, bool IsDefault);

    /// <summary>Active playback devices (speakers, headphones, HDMI…).</summary>
    public static List<OutputDevice> Outputs()
    {
        var list = new List<OutputDevice>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(0, 1, out var def) == 0) def.GetId(out defaultId);
            if (en.EnumAudioEndpoints(0 /* eRender */, 1 /* ACTIVE */, out var col) != 0) return list;
            col.GetCount(out uint n);
            var nameKey = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                dev.GetId(out string id);
                string name = id;
                if (dev.OpenPropertyStore(0, out var store) == 0 && store.GetValue(ref nameKey, out var pv) == 0)
                {
                    if (pv.vt == 31) name = Marshal.PtrToStringUni(pv.p) ?? id;
                    PropVariantClear(ref pv);
                }
                list.Add(new OutputDevice(id, name, id == defaultId));
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return list;
    }

    public static void SetDefaultOutput(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role); // console, multimedia, communications
            age = 99; // re-acquire the volume endpoint for the new device
            DropMeter();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static void SetLevel(float level)
    {
        var ep = Endpoint();
        if (ep == null) return;
        var ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
        if (level > 0) ep.SetMute(false, ref ctx);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float l, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float l);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float l);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float l);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float l);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IAudioEndpointVolume? endpoint;
    private static int age;

    private static IAudioEndpointVolume? Endpoint()
    {
        // Re-acquire periodically so a change of default device (headset, HDMI) is picked up.
        if (endpoint != null && ++age < 15) return endpoint;
        age = 0;
        try
        {
            if (endpoint != null) Marshal.ReleaseComObject(endpoint);
            endpoint = null;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) < 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) < 0) return null;
            endpoint = (IAudioEndpointVolume)o;
        }
        catch { endpoint = null; }
        return endpoint;
    }

    public static (float level, bool muted)? Get()
    {
        var ep = Endpoint();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out float l) < 0) return null;
        ep.GetMute(out bool m);
        return (l, m);
    }

    public static void Nudge(float delta)
    {
        var ep = Endpoint();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out float l) < 0) return;
        var ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(l + delta, 0f, 1f), ref ctx);
        if (delta > 0) ep.SetMute(false, ref ctx);
    }

    #region Change notifications (the island's volume HUD)

    /// <summary>Volume or mute changed (keyboard keys, mixer, any app). Raised on the UI thread.</summary>
    public static event Action<float, bool>? VolumeChanged;

    // Own endpoint, separate from the cached one above (that one is released every few calls).
    private static IAudioEndpointVolume? watched;
    private static IntPtr volumeCallback, deviceCallback;
    private static IMMDeviceEnumerator? watchEnumerator;

    /// <summary>
    /// Core Audio pushes every volume change to IAudioEndpointVolumeCallback, so the HUD reacts at once
    /// without polling. IMMNotificationClient moves the subscription when the default output changes.
    /// </summary>
    public static void StartWatching()
    {
        if (watchEnumerator != null) return;
        try
        {
            watchEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // Kept for the app's lifetime: the native side holds these pointers.
            volumeCallback = Marshal.GetComInterfaceForObject(new VolumeCallback(), typeof(IAudioEndpointVolumeCallback));
            deviceCallback = Marshal.GetComInterfaceForObject(new DeviceCallback(), typeof(IMMNotificationClient));
            watchEnumerator.RegisterEndpointNotificationCallback(deviceCallback);
            WatchDefault();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void WatchDefault()
    {
        try
        {
            if (watched != null)
            {
                watched.UnregisterControlChangeNotify(volumeCallback);
                Marshal.ReleaseComObject(watched);
                watched = null;
            }
            if (watchEnumerator == null || watchEnumerator.GetDefaultAudioEndpoint(0, 1, out var dev) < 0) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) < 0) return;
            watched = (IAudioEndpointVolume)o;
            watched.RegisterControlChangeNotify(volumeCallback);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void OnUi(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>Called by Core Audio on its own thread.</summary>
    internal static void OnVolumeNotify(IntPtr data)
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context (16), BOOL muted, float master volume.
        bool muted = Marshal.ReadInt32(data, 16) != 0;
        float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        OnUi(() => VolumeChanged?.Invoke(level, muted));
    }

    internal static void OnDefaultChanged() => OnUi(() => { age = 99; DropMeter(); WatchDefault(); });

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    private static IAudioMeterInformation? meter;

    private static void DropMeter()
    {
        if (meter != null) { try { Marshal.ReleaseComObject(meter); } catch { } }
        meter = null;
    }

    /// <summary>
    /// Peak (0..1) of what the default output is playing right now, all apps mixed; null if it can't be read.
    /// A plain COM read of a value the audio engine already keeps, so it is cheap enough to poll.
    /// </summary>
    public static float? OutputPeak()
    {
        try
        {
            if (meter == null)
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) < 0) return null;
                var iid = typeof(IAudioMeterInformation).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) < 0) return null;
                meter = (IAudioMeterInformation)o;
            }
            if (meter.GetPeakValue(out float p) == 0) return p;
            DropMeter(); // device gone (unplugged): get the new default next time
        }
        catch { DropMeter(); }
        return null;
    }

    #endregion

    /// <summary>Microphone mute state (the default communications capture device), null without a microphone.</summary>
    public static bool? MicMuted()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(1 /* eCapture */, 2 /* eCommunications */, out var dev) < 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) < 0) return null;
            var ep = (IAudioEndpointVolume)o;
            bool? m = ep.GetMute(out bool muted) < 0 ? null : muted;
            Marshal.ReleaseComObject(ep);
            return m;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mutes (or unmutes) every active microphone at once, so no app keeps listening through another input.
    /// The new state follows the default communications microphone. Returns it, or null without a microphone.
    /// </summary>
    public static bool? ToggleMicMute()
    {
        bool? current = MicMuted();
        if (current == null) return null;
        bool mute = !current.Value;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.EnumAudioEndpoints(1 /* eCapture */, 1 /* DEVICE_STATE_ACTIVE */, out var list) < 0) return null;
            list.GetCount(out uint n);
            var iid = typeof(IAudioEndpointVolume).GUID;
            for (uint i = 0; i < n; i++)
            {
                if (list.Item(i, out var dev) < 0 || dev.Activate(ref iid, 23, IntPtr.Zero, out object o) < 0) continue;
                var ep = (IAudioEndpointVolume)o;
                var ctx = Guid.Empty;
                ep.SetMute(mute, ref ctx);
                Marshal.ReleaseComObject(ep);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return mute;
    }

    public static void ToggleMute()
    {
        var ep = Endpoint();
        if (ep == null || ep.GetMute(out bool m) < 0) return;
        var ctx = Guid.Empty;
        ep.SetMute(!m, ref ctx);
    }
}

/// <summary>
/// Whether sound is actually coming out, for the music bars. Media sessions keep saying "playing" for a muted
/// tab, a silent ad or a stalled video, so the output meter is asked once a second, and only while someone
/// watches. Quiet only after ~2 s of silence, so a pause between songs doesn't make the bars stutter.
/// </summary>
internal static class OutputActivity
{
    private const float Threshold = 0.0005f; // about -66 dB: dithering and noise floors stay below
    private static readonly System.Windows.Threading.DispatcherTimer timer =
        new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private static readonly HashSet<object> watchers = new();
    private static int silent;

    public static bool Audible { get; private set; } = true;
    /// <summary>Raised on the UI thread when <see cref="Audible"/> flips.</summary>
    public static event Action? Changed;

    static OutputActivity() => timer.Tick += (_, _) => Sample();

    public static void Watch(object who, bool on)
    {
        if (on ? !watchers.Add(who) : !watchers.Remove(who)) return;
        if (on && watchers.Count == 1)
        {
            // Fresh start: one silent read is enough to stop, so a muted video never animates.
            silent = 2;
            Audible = true;
            Sample();
            timer.Start();
        }
        else if (watchers.Count == 0) timer.Stop();
    }

    private static void Sample()
    {
        float? p = Audio.OutputPeak();
        bool heard = p == null || p > Threshold; // unreadable meter: keep the old behavior (animate)
        silent = heard ? 0 : silent + 1;
        bool now = silent < 3;
        if (now == Audible) return;
        Audible = now;
        Changed?.Invoke();
    }
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr data);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid fmtid; public uint pid; }

// Public and COM-visible: Core Audio calls these through COM callable wrappers.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class VolumeCallback : IAudioEndpointVolumeCallback
{
    public int OnNotify(IntPtr data) { try { Audio.OnVolumeNotify(data); } catch { } return 0; }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceCallback : IMMNotificationClient
{
    public int OnDeviceStateChanged(string id, int state) => 0;
    public int OnDeviceAdded(string id) => 0;
    public int OnDeviceRemoved(string id) => 0;
    public int OnDefaultDeviceChanged(int flow, int role, string id)
    {
        if (flow == 0 && role == 1) Audio.OnDefaultChanged(); // eRender, eMultimedia
        return 0;
    }
    public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
}
