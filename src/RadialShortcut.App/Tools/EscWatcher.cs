using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Esc for overlays and recordings, whichever window has the keyboard. Windows often refuses focus to a
/// tray app, so a key handler on our own window misses the press, and polling the key state misses a
/// quick tap. A low-level keyboard hook sees every press; while a watcher is active the Esc is consumed,
/// so it doesn't also close something in the app underneath. The hook lives on its own thread: Windows
/// drops a hook whose thread is too busy to answer, and the UI thread sometimes is (scroll stitching).
/// </summary>
internal sealed class EscWatcher : IDisposable
{
    private static readonly object Gate = new();
    private static readonly List<EscWatcher> Active = [];
    private static readonly HookProc Proc = OnKey; // kept alive for as long as the hook may call it
    private static uint _thread;

    private readonly Action _onEsc;
    private readonly Dispatcher _dispatcher;

    private EscWatcher(Action onEsc)
    {
        _onEsc = onEsc;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Calls <paramref name="onEsc"/> on the starting thread at each Esc press until disposed. The newest watcher wins.</summary>
    public static EscWatcher Start(Action onEsc)
    {
        var watcher = new EscWatcher(onEsc);
        lock (Gate)
        {
            Active.Add(watcher);
            if (Active.Count == 1)
                Post(WM_INSTALL);
        }
        return watcher;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (Active.Remove(this) && Active.Count == 0)
                Post(WM_REMOVE);
        }
    }

    /// <summary>Hands work to the hook thread, starting it the first time.</summary>
    private static void Post(uint message)
    {
        if (_thread == 0)
        {
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "RadialShortcut Esc" };
            thread.Start();
            ready.Wait();
        }
        PostThreadMessage(_thread, message, IntPtr.Zero, IntPtr.Zero);
    }

    private static void Loop(ManualResetEventSlim ready)
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // gives the thread its queue before anyone posts
        _thread = GetCurrentThreadId();
        ready.Set();
        var hook = IntPtr.Zero;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_INSTALL && hook == IntPtr.Zero)
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, GetModuleHandle(null), 0);
            else if (msg.message == WM_REMOVE && hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }
    }

    private static IntPtr OnKey(int code, IntPtr message, IntPtr info)
    {
        if (code >= 0 && Marshal.ReadInt32(info) == VK_ESCAPE)
        {
            EscWatcher? watcher;
            lock (Gate)
                watcher = Active.Count > 0 ? Active[^1] : null;
            if (watcher is not null)
            {
                // Key-up is swallowed too, so the app below never sees half a press.
                if ((int)message is WM_KEYDOWN or WM_SYSKEYDOWN)
                    watcher._dispatcher.BeginInvoke(watcher._onEsc);
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, info);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr info);

    private const int WH_KEYBOARD_LL = 13, VK_ESCAPE = 0x1B, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const uint WM_INSTALL = 0x8001, WM_REMOVE = 0x8002, PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr info);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
