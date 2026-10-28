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
        if (Cache.TryGetValue(path, out var cached))
            return cached;

        ImageSource? image = null;
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
        Cache[path] = image;
        return image;
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
