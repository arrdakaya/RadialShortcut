using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialShortcut.App.Capture;

/// <summary>
/// Grabs the same screen rectangle over and over into a reused top-down BGRA buffer.
/// Used by the screen recorder (with the cursor drawn in) and the scrolling screenshot (without).
/// </summary>
internal sealed class FrameGrabber : IDisposable
{
    private readonly Int32Rect _rect;
    private readonly bool _cursor;
    private readonly IntPtr _screen, _memory, _bitmap, _old;
    private BITMAPINFO _info;

    public FrameGrabber(Int32Rect rect, bool drawCursor)
    {
        _rect = rect;
        _cursor = drawCursor;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        _bitmap = CreateCompatibleBitmap(_screen, rect.Width, rect.Height);
        _old = SelectObject(_memory, _bitmap);
        // A negative height asks GetDIBits for top-down rows.
        _info = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = rect.Width, biHeight = -rect.Height, biPlanes = 1, biBitCount = 32 };
    }

    public int Width => _rect.Width;

    public int Height => _rect.Height;

    public int Stride => _rect.Width * 4;

    public byte[] NewBuffer() => new byte[Stride * Height];

    /// <summary>Fills the buffer with the current screen contents. CAPTUREBLT includes layered windows.</summary>
    public void Grab(byte[] buffer)
    {
        BitBlt(_memory, 0, 0, _rect.Width, _rect.Height, _screen, _rect.X, _rect.Y, SRCCOPY | CAPTUREBLT);
        if (_cursor)
            DrawCursor();
        // GetDIBits wants the bitmap not selected into a DC.
        SelectObject(_memory, _old);
        GetDIBits(_memory, _bitmap, 0, (uint)_rect.Height, buffer, ref _info, 0);
        SelectObject(_memory, _bitmap);
    }

    public static BitmapSource ToImage(byte[] pixels, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private void DrawCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return;
        if (!GetIconInfo(ci.hCursor, out var icon))
            return;
        try
        {
            DrawIconEx(_memory, ci.ptScreenPos.X - icon.xHotspot - _rect.X, ci.ptScreenPos.Y - icon.yHotspot - _rect.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }

    public void Dispose()
    {
        SelectObject(_memory, _old);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
}
