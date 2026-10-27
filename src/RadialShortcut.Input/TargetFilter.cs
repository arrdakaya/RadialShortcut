using System.Runtime.InteropServices;
using System.Text;
using static RadialShortcut.Input.NativeMethods;

namespace RadialShortcut.Input;

/// <summary>
/// Decides, from inside the hook callback, whether a right-press over a point may be taken over.
/// Results are cached per process id so the hook stays fast.
/// </summary>
public sealed class TargetFilter
{
    private readonly record struct ProcessInfo(string Name, bool Elevated);

    // Swapped whole when config.json changes; the hook thread only reads the reference.
    private volatile HashSet<string> _excluded;
    private readonly Dictionary<uint, ProcessInfo> _cache = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly bool _selfElevated = Environment.IsPrivilegedProcess;

    public TargetFilter(IEnumerable<string> excludedProcesses)
    {
        _excluded = new HashSet<string>(excludedProcesses, StringComparer.OrdinalIgnoreCase);
    }

    public void SetExcluded(IEnumerable<string> processes) =>
        _excluded = new HashSet<string>(processes, StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(ScreenPoint p)
    {
        var hwnd = GetAncestor(WindowFromPoint(new POINT { X = p.X, Y = p.Y }), GA_ROOT);
        if (hwnd == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownPid)
            return false;

        // Games: a right-hold there is aiming or a skill, not a request for the ring.
        if (IsFullscreen(hwnd) || IsCursorClipped())
            return false;

        var info = GetInfo(pid);
        if (info.Elevated && !_selfElevated)
            return false; // UIPI: we could not replay the click into this window.

        return !_excluded.Contains(info.Name);
    }

    /// <summary>Exclusive or borderless full screen: the window covers its whole monitor. The desktop itself does too.</summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        // A maximized window on a monitor without a taskbar covers it too, but is no game.
        if (IsZoomed(hwnd) || !GetWindowRect(hwnd, out var window))
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
            return false;

        var m = info.rcMonitor;
        if (window.Left > m.Left || window.Top > m.Top || window.Right < m.Right || window.Bottom < m.Bottom)
            return false;

        var name = new StringBuilder(32);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW");
    }

    /// <summary>Games that run in a window still lock the cursor inside it.</summary>
    private static bool IsCursorClipped()
    {
        if (!GetClipCursor(out var clip))
            return false;

        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return clip.Left > left || clip.Top > top
            || clip.Right < left + GetSystemMetrics(SM_CXVIRTUALSCREEN)
            || clip.Bottom < top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
    }

    private ProcessInfo GetInfo(uint pid)
    {
        if (_cache.TryGetValue(pid, out var cached))
            return cached;

        if (_cache.Count > 512)
            _cache.Clear();

        return _cache[pid] = Query(pid);
    }

    private ProcessInfo Query(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return new ProcessInfo("", Elevated: true); // Not even queryable: treat as protected.

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            var name = QueryFullProcessImageName(process, 0, buffer, ref size)
                ? Path.GetFileName(buffer.ToString())
                : "";

            return new ProcessInfo(name, IsElevated(process));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsElevated(IntPtr process)
    {
        // A non-elevated process cannot open an elevated process's token.
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            return true;

        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
