using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.App;

/// <summary>
/// The ring from the keyboard (for touchpads): hold the hotkey, point at a slice, let go of the
/// last key to pick it. Esc cancels. The same hotkey with Shift opens the file ring.
/// </summary>
internal sealed class HotkeyRing : IDisposable
{
    private const int WM_HOTKEY = 0x0312, Main = 1, Files = 2;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _window = new(new HwndSourceParameters("RadialShortcut.Hotkey") { WindowStyle = 0, ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Func<string, ScreenPoint, RingWindow> _open;
    private readonly Action<RingWindow, int, ScreenPoint> _release;
    private RingWindow? _ring;
    private ScreenPoint _origin;
    private int _key;

    /// <param name="open">Shows the ring of a layer at a point and returns it.</param>
    /// <param name="release">Closes the ring with the chosen slice (-1: none) and runs it.</param>
    public HotkeyRing(Func<string, ScreenPoint, RingWindow> open, Action<RingWindow, int, ScreenPoint> release)
    {
        _open = open;
        _release = release;
        _window.AddHook(WndProc);
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>False with nothing registered when the text is not a chord or another app owns it.</summary>
    public bool Register(string? chord)
    {
        UnregisterHotKey(_window.Handle, Main);
        UnregisterHotKey(_window.Handle, Files);
        if (!KeyChord.TryParse(chord, out var keys) || keys.Length < 2)
            return false;

        uint mods = 0;
        foreach (var k in keys[..^1])
            mods |= k switch { 0x11 => MOD_CONTROL, 0x10 => MOD_SHIFT, 0x12 => MOD_ALT, _ => MOD_WIN };
        _key = keys[^1];
        var ok = RegisterHotKey(_window.Handle, Main, mods | MOD_NOREPEAT, (uint)_key);
        if ((mods & MOD_SHIFT) == 0)
            RegisterHotKey(_window.Handle, Files, mods | MOD_SHIFT | MOD_NOREPEAT, (uint)_key);
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _ring is null)
        {
            var (x, y) = ScreenCapture.CursorPosition();
            _origin = new ScreenPoint(x, y);
            _ring = _open(wParam.ToInt32() == Files ? RingLayers.Files : RingLayers.Main, _origin);
            _poll.Start();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Poll()
    {
        if (_ring is null)
            return;
        var (x, y) = ScreenCapture.CursorPosition();
        var p = new ScreenPoint(x, y);
        var ring = _ring;
        if (Tools.Native.IsKeyDown(Tools.Native.VK_ESCAPE))
            Finish(ring, -1, p);
        else if (!Tools.Native.IsKeyDown(_key))
            Finish(ring, ring.SliceAt(p), p);
        else
            ring.Track(p);
    }

    private void Finish(RingWindow ring, int slice, ScreenPoint at)
    {
        _poll.Stop();
        _ring = null;
        _release(ring, slice, _origin);
    }

    public void Dispose()
    {
        _poll.Stop();
        UnregisterHotKey(_window.Handle, Main);
        UnregisterHotKey(_window.Handle, Files);
        _window.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
