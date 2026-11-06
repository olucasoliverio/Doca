using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Doca;

/// <summary>Shell integration: icons, display names, launching, shortcuts, window operations.</summary>
internal static class Shell
{
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    #region COM interop

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    #endregion

    #region Icons (loaded on a dedicated STA thread so the UI never stalls)

    private static readonly BlockingCollection<(string name, int size, Action<ImageSource?> done)> queue = new();
    private static readonly Dictionary<string, ImageSource?> iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest on-screen icon size in physical pixels (set by the Dock).</summary>
    public static volatile int IconPixelSize = 128;

    static Shell()
    {
        var t = new Thread(() =>
        {
            foreach (var (name, size, done) in queue.GetConsumingEnumerable())
            {
                ImageSource? img = null;
                try { img = LoadIcon(name, size); } catch (Exception ex) { App.Log(ex); }
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    iconCache[IconKey(name, size)] = img;
                    done(img);
                });
            }
        }) { IsBackground = true, Name = "Doca.Icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Requests an icon for a parsing name (path, shell:AppsFolder\AUMID, ::{CLSID}). Callback runs on the UI thread.</summary>
    /// <remarks>
    /// Lists that show many small icons (Spotlight) ask for a smaller size: 256 px icons cost 256 KB each
    /// and the cache keeps them for the whole session.
    /// </remarks>
    public static void RequestIcon(string parsingName, Action<ImageSource?> done, int size = 256)
    {
        if (iconCache.TryGetValue(IconKey(parsingName, size), out var cached)) { done(cached); return; }
        queue.Add((parsingName, size, done));
    }

    private static string IconKey(string name, int size) => size == 256 ? name : size + "|" + name;

    public static void ForgetIcon(string parsingName) => iconCache.Remove(parsingName);

    public static void ClearIconCache() => iconCache.Clear();

    private static BitmapSource? LoadIcon(string name, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out object obj) < 0) return null;
        var factory = (IShellItemImageFactory)obj;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 /* SIIGBF_ICONONLY */, out IntPtr hbm) < 0) return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var px = new byte[w * h * 4];
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        // Bounding box of visible pixels; small legacy icons come back padded inside a 256px canvas.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] != 0)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < 0)
        {
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            minX = minY = 0; maxX = w - 1; maxY = h - 1;
        }
        else if (minX == 0 || minY == 0 || maxX >= w - 8 || maxY >= h - 8)
        {
            // Exes with only small icons (e.g. VALORANT) come back as a tiny icon centered inside a thin
            // light frame drawn by the shell at the canvas edge. Ignore that frame when finding the content.
            int m = Math.Max(4, w / 32);
            int iMinX = w, iMinY = h, iMaxX = -1, iMaxY = -1;
            for (int y = m; y < h - m; y++)
                for (int x = m; x < w - m; x++)
                    if (px[(y * w + x) * 4 + 3] != 0)
                    {
                        if (x < iMinX) iMinX = x; if (x > iMaxX) iMaxX = x;
                        if (y < iMinY) iMinY = y; if (y > iMaxY) iMaxY = y;
                    }
            if (iMaxX >= 0 && Math.Max(iMaxX - iMinX + 1, iMaxY - iMinY + 1) < w * 0.6)
            {
                // Clear the frame so it can't bleed into the crop.
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (x < m || y < m || x >= w - m || y >= h - m)
                            Array.Clear(px, (y * w + x) * 4, 4);
                minX = iMinX; minY = iMinY; maxX = iMaxX; maxY = iMaxY;
            }
        }

        BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        if (side < w * 0.6)
        {
            side = Math.Min(w, (int)(side * 1.06) + 2);
            int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            int left = Math.Clamp(cx - side / 2, 0, w - side), top = Math.Clamp(cy - side / 2, 0, h - side);
            bmp = new CroppedBitmap(bmp, new Int32Rect(left, top, side, side));
        }

        // Pre-scale once (high quality) to the largest size the Dock will draw, so per-frame
        // drawing is a plain bilinear GPU sample instead of WPF re-scaling bitmaps on the CPU.
        if (bmp.PixelWidth > IconPixelSize)
        {
            double s = (double)IconPixelSize / bmp.PixelWidth;
            bmp = new WriteableBitmap(new TransformedBitmap(bmp, new ScaleTransform(s, s)));
        }
        bmp.Freeze();
        return bmp;
    }

    #endregion

    #region Names

    private static readonly Dictionary<string, string> nameCache = new(StringComparer.OrdinalIgnoreCase);
    public static string CacheStats => $"icons={iconCache.Count} names={nameCache.Count} iconQueue={queue.Count}";

    public static string AppName(string path)
    {
        if (nameCache.TryGetValue(path, out var n)) return n;
        n = "";
        if (Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return nameCache[path] = "Explorador de Arquivos";
        try
        {
            if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                n = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                  : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : "";
            }
        }
        catch { }
        if (n.Length == 0) n = DisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
        return nameCache[path] = n;
    }

    public static string? DisplayName(string parsingName)
    {
        if (nameCache.TryGetValue("dn:" + parsingName, out var cached)) return cached;
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object obj) < 0) return null;
        var item = (IShellItem)obj;
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr p) < 0) return null;
            string s = Marshal.PtrToStringUni(p) ?? "";
            CoTaskMemFree(p);
            return nameCache["dn:" + parsingName] = s;
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// <summary>
    /// Identity of an exe that survives app updates, used to match pinned items with running windows.
    /// Packaged apps live in WindowsApps\Name_1.2.3.0_x64__publisher and Squirrel apps (Discord, Teams)
    /// in app-1.2.3; both change folder on every update, which left a stale pin next to the running copy.
    /// </summary>
    public static string AppKey(string path)
    {
        string key = path.ToLowerInvariant();
        key = Regex.Replace(key, @"(\\windowsapps\\[^\\_]+)_[\d.]+_", "$1_");
        return Regex.Replace(key, @"\\app-[\d.]+\\", @"\app\");
    }

    /// <summary>
    /// Current exe of a Squirrel app whose pinned version folder was removed by an update.
    /// WindowsApps cannot be listed without admin, so packaged apps are healed from their running window instead.
    /// </summary>
    public static string? UpdatedSquirrelPath(string path)
    {
        var m = Regex.Match(path, @"^(.*)\\app-[\d.]+\\(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try
        {
            return Directory.GetDirectories(m.Groups[1].Value, "app-*")
                .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : null))
                .Where(d => d.ver != null && File.Exists(Path.Combine(d.dir, m.Groups[2].Value)))
                .OrderByDescending(d => d.ver)
                .Select(d => Path.Combine(d.dir, m.Groups[2].Value))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Current exe of a packaged app whose pinned version folder (…\WindowsApps\Name_1.2.3.0_x64__hash\…) was removed
    /// by an update. WindowsApps cannot be listed without admin, but PackageManager can find the user's installed
    /// package by family name, so the pin heals even while the app is closed (its icon would otherwise go blank).
    /// </summary>
    public static string? UpdatedPackagedPath(string path)
    {
        var m = Regex.Match(path, @"\\WindowsApps\\([^\\_]+)_[^\\_]+_[^\\_]*_[^\\_]*_([^\\_]+)\\(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try
        {
            string family = m.Groups[1].Value + "_" + m.Groups[2].Value;
            return new Windows.Management.Deployment.PackageManager()
                .FindPackagesForUser(string.Empty, family)
                .Select(p => Path.Combine(p.InstalledLocation.Path, m.Groups[3].Value))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    /// <summary>AppUserModelID of a window (used for UWP apps hosted by ApplicationFrameHost).</summary>
    public static string? WindowAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store) < 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out PROPVARIANT pv) < 0) return null;
            string? s = pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    #endregion

    #region Launching

    public static void Open(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" };
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            Process.Start(psi);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static void OpenAumid(string aumid) => Open("explorer.exe", @"shell:AppsFolder\" + aumid);

    public static void Reveal(string path) => Open("explorer.exe", $"/select,\"{path}\"");

    /// <summary>Resolves a .lnk to its target path; null if it is not a file shortcut.</summary>
    public static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic sh = Activator.CreateInstance(type)!;
            try
            {
                dynamic sc = sh.CreateShortcut(lnk);
                string target = sc.TargetPath;
                Marshal.ReleaseComObject(sc);
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { Marshal.ReleaseComObject(sh); }
        }
        catch { return null; }
    }

    #endregion

    #region Window operations

    public static void Focus(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(h)) return;

        // Foreground lock: borrow the input queue of the current foreground thread.
        uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint me = Native.GetCurrentThreadId();
        Native.AttachThreadInput(me, fgThread, true);
        Native.BringWindowToTop(h);
        Native.SetForegroundWindow(h);
        Native.AttachThreadInput(me, fgThread, false);
    }

    public static void Minimize(IntPtr h) => Native.ShowWindow(h, Native.SW_MINIMIZE);

    /// <summary>
    /// Same as clicking the window's X (SC_CLOSE). Returns false when Windows refuses the message,
    /// which happens for apps running as administrator (UIPI blocks a normal app from closing them).
    /// </summary>
    public static bool Close(IntPtr h) =>
        Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(0xF060 /* SC_CLOSE */), IntPtr.Zero)
        || Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Closes several windows; tells the user when Windows blocked it (elevated app).</summary>
    public static void CloseAll(IEnumerable<IntPtr> windows, string appName)
    {
        bool blocked = false;
        foreach (var w in windows.ToList()) blocked |= !Close(w);
        if (blocked) ElevatedWarning(appName);
    }

    /// <summary>Force Quit: ends every process of the app (unsaved work is lost).</summary>
    public static void ForceQuit(string? exePath, IEnumerable<IntPtr> windows, string appName)
    {
        var pids = new HashSet<int>();
        foreach (var w in windows)
            if (Native.GetWindowThreadProcessId(w, out uint pid) != 0) pids.Add((int)pid);
        // Apps like Discord/Steam run several processes of the same exe; end them all.
        if (exePath != null && !Path.GetFileName(exePath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
            {
                try { if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id); }
                catch { pids.Add(p.Id); } // no access to the module path: same name is good enough
            }

        bool denied = false;
        foreach (var pid in pids)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
            catch (System.ComponentModel.Win32Exception) { denied = true; }
            catch (Exception ex) { App.Log(ex); }
        }
        if (denied) ElevatedWarning(appName);
    }

    private static void ElevatedWarning(string appName) =>
        DocaDialog.Info($"Não foi possível fechar \"{appName}\"",
                        "Ele está rodando como administrador, e o Windows não deixa o Doca fechá-lo. " +
                        "Feche pelo próprio app ou pelo Gerenciador de Tarefas.");

    public static void ToggleMaximize(IntPtr h) => Native.ShowWindow(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE);

    /// <summary>Sends a key chord (modifiers first), e.g. SendKeys(0x11, 0x43) = Ctrl+C.</summary>
    public static void SendKeys(params byte[] vks)
    {
        const uint UP = 2;
        foreach (var vk in vks) Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        for (int i = vks.Length - 1; i >= 0; i--) Native.keybd_event(vks[i], 0, UP, UIntPtr.Zero);
    }

    /// <summary>Presses Win (+ optional key), e.g. Win+A opens Quick Settings.</summary>
    public static void WinKey(byte vk = 0)
    {
        const byte LWIN = 0x5B; const uint UP = 2;
        Native.keybd_event(LWIN, 0, 0, UIntPtr.Zero);
        if (vk != 0)
        {
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, UP, UIntPtr.Zero);
        }
        Native.keybd_event(LWIN, 0, UP, UIntPtr.Zero);
    }

    #endregion
}
