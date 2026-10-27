using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RadialShortcut.App.Capture;

/// <summary>A monitor in physical pixels, with its DPI scale (1.0 = 96 DPI).</summary>
public sealed record Display(Int32Rect Bounds, Int32Rect WorkArea, double Scale);

/// <summary>Monitor enumeration, GDI screen grabs and pixel-exact window placement.</summary>
public static class ScreenCapture
{
    public static IReadOnlyList<Display> Displays()
    {
        var list = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            list.Add(FromHandle(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Display DisplayAt(int x, int y) => FromHandle(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Grabs a rectangle of the screen. CAPTUREBLT includes layered windows.</summary>
    public static BitmapSource Grab(Int32Rect r)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(memory, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Moves a window to a physical-pixel position. Landing on a monitor with another DPI makes WPF
    /// rescale and apply its own rectangle, so the call is repeated once the new DPI has been taken.
    /// </summary>
    public static void Place(Window window, int x, int y, int width = 0, int height = 0)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var flags = SWP_NOACTIVATE | SWP_NOZORDER | (width == 0 ? SWP_NOSIZE : 0);
        for (var i = 0; i < 2; i++)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
    }

    private static Display FromHandle(IntPtr h)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(h, ref info);
        var scale = GetDpiForMonitor(h, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new Display(info.rcMonitor.ToInt32Rect(), info.rcWork.ToInt32Rect(), scale);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Int32Rect ToInt32Rect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
