using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialShortcut.App;

/// <summary>
/// The icon Explorer shows for a file, shortcut or folder, so a slice that opens Unity Hub looks like
/// Unity Hub. Cached per path; null when the path cannot be found.
/// </summary>
internal static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
        }

        ImageSource? image = null;
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            image = ShellItemIcon(path);
            lock (Cache)
                Cache[path] = image;
            return image;
        }
        var full = Resolve(path);
        if (full is not null)
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfo(full, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    image = bitmap;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        lock (Cache)
            Cache[path] = image;
        return image;
    }

    /// <summary>Loads icons on a background thread so a long list scrolls without stopping to fetch them.</summary>
    public static void Preload(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var thread = new Thread(() =>
        {
            foreach (var path in list)
            {
                try
                {
                    For(path);
                }
                catch (Exception)
                {
                    // A missing icon just stays blank; the row loads it again if it can.
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA); // the shell's icon objects expect one
        thread.Start();
    }

    /// <summary>A full path as is; a bare name like notepad.exe is looked up on PATH.</summary>
    private static string? Resolve(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return path;
        if (Path.IsPathRooted(path))
            return null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, path);
                if (File.Exists(candidate))
                    return candidate;
                if (File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Icons for shell names like "shell:AppsFolder\…" (Store apps have no file to ask SHGetFileInfo about).
    /// The bitmap comes back as a premultiplied DIB, copied by hand so its transparency survives.
    /// </summary>
    private static ImageSource? ShellItemIcon(string name)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, SIIGBF_ICONONLY, out var bitmap) != 0)
                return null;
            try
            {
                var dib = new DIBSECTION();
                if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
                    return null;
                int width = dib.dsBm.bmWidth, height = dib.dsBm.bmHeight, stride = dib.dsBm.bmWidthBytes;
                BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, dib.dsBm.bmBits, stride * height, stride);
                // A positive height means the rows are stored bottom-up.
                if (dib.dsBmih.biHeight > 0)
                    source = new TransformedBitmap(source, new ScaleTransform(1, -1));
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private const int SIIGBF_ICONONLY = 0x4;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public int dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref DIBSECTION dib);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
