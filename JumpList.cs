using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Doca;

internal sealed class JumpItem
{
    public string Title = "";
    public bool IsSeparator;
    /// <summary>Shortcut target; empty for shell items (Settings pages, packaged apps).</summary>
    public string Path = "";
    public string Arguments = "";
    public string WorkingDirectory = "";
    /// <summary>Copy of the shortcut's ITEMIDLIST, used to launch items that have no file path.</summary>
    public byte[]? IdList;
    /// <summary>Recent/frequent/pinned documents are opened with the app; tasks run as they are.</summary>
    public bool IsDocument;
}

internal sealed class JumpCategory
{
    public required string Name { get; init; }
    public List<JumpItem> Items { get; } = new();
}

/// <summary>
/// Reads the Jump Lists Windows keeps for each app (the taskbar's right-click menu), so the Dock can show them.
/// There is no API to read another app's list, so this reads the files the shell writes:
/// - %APPDATA%\Microsoft\Windows\Recent\CustomDestinations\{hash}.customDestinations-ms: what the app set itself
///   (tasks like "Nova janela anônima", custom categories like Steam's recent games). A small header, then per
///   category a type, the entries (CLSID + persisted IShellLink) and the 0xBABFFBAB footer.
/// - ...\AutomaticDestinations\{hash}.automaticDestinations-ms: recent/frequent/pinned documents kept by Windows.
///   A compound file: a "DestList" stream (order, pin state, frecency) and one stream per entry with its IShellLink.
/// {hash} is a CRC-64 of the app's AppUserModelID. Both formats are undocumented but unchanged since Windows 7;
/// anything unexpected just yields an empty list.
/// </summary>
internal static class JumpLists
{
    private static readonly string recentDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private const uint CategoryFooter = 0xBABFFBAB;
    private const int MaxAutomatic = 10;

    #region AppID

    /// <summary>
    /// The AppUserModelID the taskbar uses for a window, explicit (set by the app at runtime, e.g. Chrome's
    /// "Chrome.UserData.Profile2" or Steam's "Valve.Steam.Client") or implicit (from the exe path). Only the
    /// shell's internal resolver knows the runtime one; it is undocumented but has kept this layout since Windows 8.
    /// </summary>
    public static string? AppIdForWindow(IntPtr hwnd)
    {
        try
        {
            var res = Resolver();
            if (res == null || res.GetAppIDForWindow(hwnd, out IntPtr p, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0 || p == IntPtr.Zero) return null;
            string? id = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    /// <summary>AppID the shell gives to a .lnk (its System.AppUserModel.ID, or the implicit one of its target).</summary>
    public static string? AppIdForShortcut(string lnk)
    {
        IntPtr item = IntPtr.Zero;
        try
        {
            var res = Resolver();
            var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
            if (res == null || SHCreateItemFromParsingName(lnk, IntPtr.Zero, ref iid, out item) != 0) return null;
            if (res.GetAppIDForShortcut(item, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
            string? id = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception ex) { App.Log(ex); return null; }
        finally { if (item != IntPtr.Zero) Marshal.Release(item); }
    }

    // Folders the shell replaces with their KNOWNFOLDERID in implicit AppIDs, so the ID survives a different
    // drive letter. Seen in practice: Steam's shortcut resolves to "{7C5A40EF-…}\Steam\Steam.exe".
    private static readonly (Environment.SpecialFolder folder, string guid)[] implicitFolders =
    {
        (Environment.SpecialFolder.System, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
        (Environment.SpecialFolder.SystemX86, "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"),
        (Environment.SpecialFolder.CommonProgramFiles, "{6365D5A7-0F0D-45E5-87F6-0DA56B6A4F7D}"),
        (Environment.SpecialFolder.CommonProgramFilesX86, "{DE974D24-D9C6-4D3E-BF91-F4455120B917}"),
        (Environment.SpecialFolder.ProgramFiles, "{6D809377-6AF0-444B-8957-A3773F02200E}"),
        (Environment.SpecialFolder.ProgramFilesX86, "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
        (Environment.SpecialFolder.Windows, "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
    };

    /// <summary>Likely implicit AppIDs of an exe (the shell's own form first); the caller keeps the one that has files.</summary>
    public static IEnumerable<string> ImplicitAppIds(string exe)
    {
        foreach (var (folder, guid) in implicitFolders)
        {
            string dir = Environment.GetFolderPath(folder);
            if (dir.Length > 0 && exe.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                yield return guid + exe[dir.Length..];
                break;
            }
        }
        yield return exe;
    }

    /// <summary>True if Windows keeps any Jump List data for this AppID.</summary>
    public static bool Exists(string appId) => File.Exists(CustomFile(appId)) || File.Exists(AutomaticFile(appId));

    private static string Hash(string appId)
    {
        // CRC-64 (reflected, polynomial 0x92C64265D32139A4, initial value all ones, no final xor)
        // of the upper-cased AppID in UTF-16; checked against the files on a real machine.
        ulong crc = ulong.MaxValue;
        foreach (byte b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x92C64265D32139A4 : crc >> 1;
        }
        return crc.ToString("x16");
    }

    private static string CustomFile(string appId) => System.IO.Path.Combine(recentDir, "CustomDestinations", Hash(appId) + ".customDestinations-ms");
    private static string AutomaticFile(string appId) => System.IO.Path.Combine(recentDir, "AutomaticDestinations", Hash(appId) + ".automaticDestinations-ms");

    #endregion

    #region Reading

    /// <summary>
    /// The app's Jump List in the taskbar's order: pinned, the app's own categories, Recent/Frequent, Tasks.
    /// Must run on an STA thread (shell COM objects).
    /// </summary>
    public static List<JumpCategory> Load(string appId)
    {
        var result = new List<JumpCategory>();
        List<JumpCategory> custom;
        int? known;
        try { (custom, known) = ReadCustom(CustomFile(appId)); }
        catch (Exception ex) { App.Log(ex); (custom, known) = (new(), null); }

        List<(JumpItem item, int pin, long time, float score)> auto;
        try { auto = ReadAutomatic(AutomaticFile(appId)); }
        catch (Exception ex) { App.Log(ex); auto = new(); }

        var pinned = new JumpCategory { Name = "Fixado" };
        pinned.Items.AddRange(auto.Where(a => a.pin >= 0).OrderBy(a => a.pin).Select(a => a.item));
        if (pinned.Items.Count > 0) result.Add(pinned);

        result.AddRange(custom.Where(c => c.Name != Tasks && c.Items.Any(i => !i.IsSeparator)));

        // Like the taskbar: an app with a custom list shows Recent/Frequent only if it asked for that category;
        // apps without one (Notepad, Word…) get Recent by default. KDC_FREQUENT = 1, KDC_RECENT = 2.
        bool hasCustom = File.Exists(CustomFile(appId));
        if (!hasCustom || known != null)
        {
            bool frequent = known == 1;
            var rest = auto.Where(a => a.pin < 0);
            rest = frequent ? rest.OrderByDescending(a => a.score) : rest.OrderByDescending(a => a.time);
            var cat = new JumpCategory { Name = frequent ? "Frequentes" : "Recentes" };
            cat.Items.AddRange(rest.Take(MaxAutomatic).Select(a => a.item));
            if (cat.Items.Count > 0) result.Add(cat);
        }

        result.AddRange(custom.Where(c => c.Name == Tasks && c.Items.Any(i => !i.IsSeparator)));
        foreach (var c in result)
        {
            if (c.Name != Tasks && c.Items.Count > MaxAutomatic) c.Items.RemoveRange(MaxAutomatic, c.Items.Count - MaxAutomatic);
            Trim(c.Items);
        }
        return result;
    }

    private const string Tasks = "Tarefas";

    private static (List<JumpCategory>, int? known) ReadCustom(string file)
    {
        var cats = new List<JumpCategory>();
        int? known = null;
        if (!File.Exists(file)) return (cats, known);
        byte[] d = ReadShared(file);
        int pos = 0;
        int Int() { int v = BitConverter.ToInt32(d, pos); pos += 4; return v; }

        if (d.Length < 12 || Int() != 2) return (cats, known); // format version
        int count = Int();
        Int();
        for (int c = 0; c < count && pos + 4 <= d.Length; c++)
        {
            int type = Int();
            if (type == 1) known = Int(); // known category (Recent/Frequent), items come from the automatic list
            else if (type is 0 or 2)
            {
                string name = Tasks;
                if (type == 0)
                {
                    int len = BitConverter.ToUInt16(d, pos);
                    pos += 2;
                    name = Encoding.Unicode.GetString(d, pos, len * 2);
                    pos += len * 2;
                    // Names can be resource references ("@shell32.dll,-1234"); show them resolved.
                    if (name.StartsWith('@')) name = IndirectString(name) ?? name;
                }
                var cat = new JumpCategory { Name = name };
                int n = Int();
                for (int i = 0; i < n; i++)
                {
                    var clsid = new Guid(d.AsSpan(pos, 16));
                    pos += 16;
                    // The entry is a persisted COM object (normally a ShellLink) with no length prefix:
                    // let it load from a stream and see how many bytes it consumed.
                    var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;
                    try
                    {
                        var stream = SHCreateMemStream(d[pos..], (uint)(d.Length - pos));
                        ((IPersistStream)obj).Load(stream);
                        IntPtr np = Marshal.AllocHGlobal(8);
                        stream.Seek(0, 1 /* STREAM_SEEK_CUR */, np);
                        pos += (int)Marshal.ReadInt64(np);
                        Marshal.FreeHGlobal(np);
                        Marshal.ReleaseComObject(stream);
                        if (obj is IShellLinkW link && FromLink(link, false) is { } item) cat.Items.Add(item);
                    }
                    finally { Marshal.ReleaseComObject(obj); }
                }
                cats.Add(cat);
            }
            else return (cats, known); // unknown layout: keep what was read
            if ((uint)Int() != CategoryFooter) return (cats, known);
        }
        return (cats, known);
    }

    private static List<(JumpItem, int pin, long time, float score)> ReadAutomatic(string file)
    {
        var list = new List<(JumpItem, int, long, float)>();
        if (!File.Exists(file)) return list;
        var iid = typeof(IStorage).GUID;
        // Explorer keeps these files open; STGM_SHARE_DENY_WRITE is the read-only mode the compound file API accepts.
        if (StgOpenStorageEx(file, 0x20, 0, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out IStorage stg) != 0) return list;
        try
        {
            byte[] dl = ReadStream(stg, "DestList");
            if (dl.Length < 32) return list;
            int version = BitConverter.ToInt32(dl, 0), count = BitConverter.ToInt32(dl, 4);
            // Entry layout (also documented by forensic tools): entry number at 88, frecency float at 96,
            // last access FILETIME at 100, pin position at 108 (-1 = not pinned), path length (chars) and
            // path at 112 (Windows 7/8) or 128 (Windows 10+, which also adds 4 bytes after the path).
            int pathAt = version >= 3 ? 128 : 112, tail = version >= 3 ? 4 : 0;
            int pos = 32;
            for (int i = 0; i < count && pos + pathAt + 2 <= dl.Length; i++)
            {
                int entry = BitConverter.ToInt32(dl, pos + 88);
                float score = BitConverter.ToSingle(dl, pos + 96);
                long time = BitConverter.ToInt64(dl, pos + 100);
                int pin = BitConverter.ToInt32(dl, pos + 108);
                int len = BitConverter.ToUInt16(dl, pos + pathAt);
                string path = Encoding.Unicode.GetString(dl, pos + pathAt + 2, len * 2);
                pos += pathAt + 2 + len * 2 + tail;

                byte[] lnk = ReadStream(stg, entry.ToString("x"));
                if (lnk.Length == 0) continue;
                var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid, true)!)!;
                try
                {
                    var stream = SHCreateMemStream(lnk, (uint)lnk.Length);
                    ((IPersistStream)obj).Load(stream);
                    Marshal.ReleaseComObject(stream);
                    if (FromLink((IShellLinkW)obj, true, path) is { } item) list.Add((item, pin, time, score));
                }
                finally { Marshal.ReleaseComObject(obj); }
            }
        }
        finally { Marshal.ReleaseComObject(stg); }
        return list;
    }

    private static JumpItem? FromLink(IShellLinkW link, bool document, string? destPath = null)
    {
        var item = new JumpItem { IsDocument = document };
        if (link is IPropertyStore props)
        {
            if (GetBool(props, AppUserModelKey(6) /* IsDestListSeparator */)) return new JumpItem { IsSeparator = true };
            item.Title = GetString(props, new PROPERTYKEY { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 } /* Title */) ?? "";
        }
        var sb = new StringBuilder(2048);
        link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
        item.Path = sb.ToString();
        sb.Clear();
        link.GetArguments(sb, sb.Capacity);
        item.Arguments = sb.ToString();
        sb.Clear();
        link.GetWorkingDirectory(sb, sb.Capacity);
        item.WorkingDirectory = sb.ToString();
        link.GetIDList(out IntPtr pidl);
        if (pidl != IntPtr.Zero)
        {
            item.IdList = new byte[ILGetSize(pidl)];
            Marshal.Copy(pidl, item.IdList, 0, item.IdList.Length);
            if (item.Title.Length == 0 && SHGetNameFromIDList(pidl, 0 /* SIGDN_NORMALDISPLAY */, out IntPtr name) == 0)
            {
                item.Title = Marshal.PtrToStringUni(name) ?? "";
                Marshal.FreeCoTaskMem(name);
            }
            Marshal.FreeCoTaskMem(pidl);
        }
        if (item.Title.StartsWith('@')) item.Title = IndirectString(item.Title) ?? "";
        if (item.Title.Length == 0 && destPath != null && System.IO.Path.IsPathFullyQualified(destPath))
            item.Title = System.IO.Path.GetFileName(destPath.TrimEnd('\\'));
        if (document && item.Path.Length == 0 && destPath != null && System.IO.Path.IsPathFullyQualified(destPath)) item.Path = destPath;
        // Nothing to show or nothing to open. Packaged apps (Settings, Spotify) keep entries whose real name the
        // shell only gets from the app itself; their display name comes back as a URI ("ms-settings:keyboard")
        // or the AppID ("SpotifyAB…!Spotify"), which is noise in a menu.
        if (item.Title.Length == 0 || (item.Path.Length == 0 && item.IdList == null)) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(item.Title, @"^[A-Za-z][A-Za-z0-9+.\-]+:\S") || (item.Title.Contains('!') && !item.Title.Contains(' '))) return null;
        return item;
    }

    /// <summary>Drops leading, trailing and doubled separators.</summary>
    private static void Trim(List<JumpItem> items)
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].IsSeparator && (i == 0 || i == items.Count - 1 || items[i - 1].IsSeparator)) items.RemoveAt(i);
        while (items.Count > 0 && items[^1].IsSeparator) items.RemoveAt(items.Count - 1);
    }

    #endregion

    #region Launching

    /// <summary>
    /// Opens a Jump List item. Tasks run their shortcut as is; documents open in the app whose list they are in
    /// (the taskbar does the same: a .txt in Notepad++'s list opens there, not in the default editor).
    /// </summary>
    public static void Launch(JumpItem item, string? appExe)
    {
        try
        {
            bool isFile = item.Path.Length > 0 && File.Exists(item.Path);
            if (item.IsDocument && isFile && appExe != null && File.Exists(appExe)
                && !System.IO.Path.GetFileName(appExe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                Shell.Open(appExe, $"\"{item.Path}\"");
                return;
            }
            if (item.Path.Length > 0 && (isFile || Directory.Exists(item.Path)))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true, Arguments = item.Arguments };
                if (Directory.Exists(item.WorkingDirectory)) psi.WorkingDirectory = item.WorkingDirectory;
                System.Diagnostics.Process.Start(psi);
                return;
            }
            if (item.IdList == null) return;
            // Shell items without a path (Settings pages, packaged apps): let the shell open the ID list.
            IntPtr pidl = Marshal.AllocCoTaskMem(item.IdList.Length);
            try
            {
                Marshal.Copy(item.IdList, 0, pidl, item.IdList.Length);
                var sei = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    fMask = 0x4 /* SEE_MASK_IDLIST */,
                    lpIDList = pidl,
                    lpParameters = item.Arguments.Length > 0 ? item.Arguments : null,
                    nShow = 1 /* SW_SHOWNORMAL */,
                };
                ShellExecuteEx(ref sei);
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    #endregion

    #region Interop

    private static IApplicationResolver? resolver;

    private static IApplicationResolver? Resolver()
    {
        if (resolver != null) return resolver;
        var type = Type.GetTypeFromCLSID(new Guid("660b90c8-73a9-4b58-8cae-355b7f55341b"));
        return resolver = type == null ? null : Activator.CreateInstance(type) as IApplicationResolver;
    }

    private static byte[] ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ReadStream(IStorage stg, string name)
    {
        if (stg.OpenStream(name, IntPtr.Zero, 0x10 /* STGM_SHARE_EXCLUSIVE */, 0, out IStream s) != 0) return Array.Empty<byte>();
        IntPtr read = Marshal.AllocHGlobal(4);
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[65536];
            while (true)
            {
                s.Read(buf, buf.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(read);
            Marshal.ReleaseComObject(s);
        }
    }

    private static PROPERTYKEY AppUserModelKey(int pid) => new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = pid };

    private static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return null;
        string? s = pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null;
        PropVariantClear(ref pv);
        return s;
    }

    private static bool GetBool(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return false;
        bool b = pv.vt == 11 /* VT_BOOL */ && (pv.p.ToInt64() & 0xFFFF) != 0;
        PropVariantClear(ref pv);
        return b;
    }

    private static string? IndirectString(string s)
    {
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    [ComImport, Guid("DE25675A-72DE-44b4-9373-05170450C140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationResolver
    {
        [PreserveSig] int GetAppIDForShortcut(IntPtr shellItem, out IntPtr appId);
        [PreserveSig] int GetAppIDForShortcutObject(IntPtr shellLink, IntPtr shellItem, out IntPtr appId);
        [PreserveSig] int GetAppIDForWindow(IntPtr hwnd, out IntPtr appId, IntPtr pinningPrevented, IntPtr explicitAppId, IntPtr embeddedShortcutValid);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
    }

    [ComImport, Guid("0000000b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
        [PreserveSig] int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; private ushort r1, r2, r3; public IntPtr p; private IntPtr p2; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, out IntPtr item);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
    [DllImport("shlwapi.dll")] private static extern IStream SHCreateMemStream(byte[] data, uint size);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder output, int cch, IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int StgOpenStorageEx([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, int format, uint attrs, IntPtr options, IntPtr reserved, ref Guid iid, out IStorage storage);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    #endregion
}
