using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using RadialShortcut.Input;

namespace RadialShortcut.App.Tools;

/// <summary>Win32 calls the tools need: target windows, keyboard and wheel injection, capture exclusion.</summary>
internal static class Native
{
    /// <summary>Same stamp the hook uses for its own injected events ("RSHC").</summary>
    private static readonly IntPtr Signature = 0x52534843;

    /// <summary>The top-level window under a screen point; the ring is gone by the time tools run.</summary>
    public static IntPtr WindowAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);

    public static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public static bool SetTopmost(IntPtr hwnd, bool on) =>
        SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>
    /// Brings a window to the front before keys are sent to it. Windows only lets the process that
    /// sent the last input do this, so a harmless key-up goes first (PLAN.md, section 7b).
    /// </summary>
    public static void Focus(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
            return;
        Send(Key(VK_MENU, up: true));
        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Ctrl+Insert copies in almost every app, and unlike Ctrl+C it never interrupts a terminal.
    /// </summary>
    public static void SendCopy() => Chord(VK_CONTROL, VK_INSERT);

    /// <summary>Presses the keys in order and releases them in reverse, like a person typing a shortcut.</summary>
    public static void SendKeys(IReadOnlyList<ushort> keys) =>
        Send([.. keys.Select(k => Key(k, false)), .. keys.Reverse().Select(k => Key(k, true))]);

    /// <summary>File name of the process that owns a window ("chrome.exe"), or "" when it cannot be read.</summary>
    public static string ProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    public static void SendPaste() => Chord(VK_SHIFT, VK_INSERT);

    public static uint ClipboardSequence() => GetClipboardSequenceNumber();

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static bool IsShiftDown() => IsKeyDown(VK_SHIFT);
    public static bool IsControlDown() => IsKeyDown(VK_CONTROL);

    public static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public const int VK_ESCAPE = 0x1B;

    /// <summary>Scrolls whatever is under the cursor. Negative notches scroll down.</summary>
    public static void Wheel(int notches) => Send(new INPUT
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { mouseData = notches * 120, dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = Signature } },
    });

    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    public static ScreenPoint CursorPosition() => GetCursorPos(out var p) ? new ScreenPoint(p.X, p.Y) : default;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORPOINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CURSORPOINT point);

    /// <summary>Keeps a window out of screenshots and recordings (Windows 10 2004+), so our own UI never shows in them.</summary>
    public static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Makes a window ignore the mouse; clicks go to whatever is below it.</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private static void Chord(ushort modifier, ushort key) =>
        Send(Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true));

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (RadialShortcut.Core.KeyChord.IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = Signature,
            },
        },
    };

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_INSERT = 0x2D;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
