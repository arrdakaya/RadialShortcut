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

        var info = GetInfo(pid);
        if (info.Elevated && !_selfElevated)
            return false; // UIPI: we could not replay the click into this window.

        return !_excluded.Contains(info.Name);
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
