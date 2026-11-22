using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace RadialShortcut.App.Capture;

/// <summary>
/// A "+" cursor that stays visible on any background: a white core with a black outline and a small
/// gap in the middle so the pixel being aimed at is not covered. The system cross is a thin dark line
/// that disappears on dimmed or dark screens. Built once per DPI scale and kept for the app's life.
/// </summary>
internal static class CrosshairCursor
{
    private static readonly Dictionary<int, Cursor> Cache = [];

    public static Cursor For(double scale)
    {
        var key = (int)Math.Round(Math.Max(1, scale) * 100);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var cursor = Create(key / 100.0) ?? Cursors.Cross;
        Cache[key] = cursor;
        return cursor;
    }

    private static Cursor? Create(double scale)
    {
        int half = scale >= 1.75 ? 1 : 0;             // core is 1 or 3 px thick
        int outline = scale >= 1.5 ? 2 : 1;
        int arm = (int)Math.Round(10 * scale);
        int gap = (int)Math.Round(3 * scale);
        int c = arm + outline;
        int size = 2 * c + 1;

        var pixels = new byte[size * size * 4];       // BGRA, transparent by default
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int dx = Math.Abs(x - c), dy = Math.Abs(y - c);
                bool core = Arm(dx, dy, half, gap, arm) || Arm(dy, dx, half, gap, arm);
                bool edge = !core && (Arm(dx, dy, half + outline, gap - outline, arm + outline) || Arm(dy, dx, half + outline, gap - outline, arm + outline));
                if (!core && !edge)
                    continue;
                var i = (y * size + x) * 4;
                byte v = core ? (byte)255 : (byte)0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        var color = CreateBitmap(size, size, 1, 32, pixels);
        var mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        try
        {
            var info = new ICONINFO { fIcon = false, xHotspot = c, yHotspot = c, hbmMask = mask, hbmColor = color };
            var handle = CreateIconIndirect(ref info);
            return handle == IntPtr.Zero ? null : CursorInteropHelper.Create(new CursorHandle(handle));
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    /// <summary>A horizontal arm: within <paramref name="half"/> of the center row, between gap and arm along it.</summary>
    private static bool Arm(int along, int across, int half, int gap, int arm) =>
        across <= half && along >= gap && along <= arm;

    private sealed class CursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle(IntPtr handle) : base(true) => SetHandle(handle);

        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
