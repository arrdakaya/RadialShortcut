using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RadialShortcut.Core;
using static RadialShortcut.Input.NativeMethods;

namespace RadialShortcut.Input;

public enum GestureEventKind { Phase, RingOpened, RingMoved, RingReleased, RingCancelled }

public sealed record GestureEvent(GestureEventKind Kind, GesturePhase Phase, ScreenPoint Origin, ScreenPoint Point, string Message);

/// <summary>
/// Owns the low-level mouse hook on a dedicated thread with its own message loop.
/// The hook callback only runs the state machine; injection is deferred until the callback has returned.
/// <see cref="Event"/> is raised on the hook thread: subscribers must not block.
/// </summary>
public sealed class MouseHookService : IGestureSink, IDisposable
{
    private readonly GestureStateMachine _machine;
    private readonly TargetFilter _filter;
    private readonly ConcurrentQueue<Action> _deferred = new();
    private readonly LowLevelMouseProc _proc; // Kept alive: the hook holds only a native pointer to it.
    private readonly ManualResetEventSlim _started = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private UIntPtr _holdTimer;

    public MouseHookService(GestureSettings settings)
    {
        _machine = new GestureStateMachine(settings, this);
        _filter = new TargetFilter(settings.ExcludedProcesses);
        _proc = HookProc;
    }

    public event Action<GestureEvent>? Event;

    public volatile bool Paused;

    /// <summary>Processes where the right button is left alone; takes effect on the next press.</summary>
    public void SetExcludedProcesses(IEnumerable<string> processes) => _filter.SetExcluded(processes);

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RadialShortcut mouse hook" };
        _thread.Start();
        _started.Wait();

        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed (error {Marshal.GetLastPInvokeError()}).");
    }

    public void Dispose()
    {
        if (_thread is null)
            return;

        PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        _started.Set();
        if (_hook == IntPtr.Zero)
            return;

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_TIMER && msg.wParam == _holdTimer)
                {
                    StopHoldTimer();
                    _machine.OnHoldElapsed();
                }
                else if (msg.message == WM_APP)
                {
                    while (_deferred.TryDequeue(out var work))
                        work();
                }
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (info.dwExtraInfo != InputSender.Signature && Handle((int)wParam, new ScreenPoint(info.pt.X, info.pt.Y)))
                    return 1;
            }
            catch
            {
                // Never let an exception escape into the input pipeline; the event simply passes.
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool Handle(int message, ScreenPoint p) => message switch
    {
        WM_RBUTTONDOWN => _machine.OnRightDown(p, !Paused && _filter.IsAllowed(p)),
        WM_RBUTTONUP => _machine.OnRightUp(p),
        WM_MOUSEMOVE => _machine.OnMove(p),
        WM_LBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN => _machine.OnOtherButtonDown(p),
        WM_LBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP => _machine.OnOtherButtonUp(),
        WM_MOUSEWHEEL or WM_MOUSEHWHEEL => _machine.OnWheel(),
        _ => false,
    };

    private void Defer(Action work)
    {
        _deferred.Enqueue(work);
        PostThreadMessage(_threadId, WM_APP, UIntPtr.Zero, IntPtr.Zero);
    }

    private void Raise(GestureEventKind kind, ScreenPoint origin, ScreenPoint point, string message = "") =>
        Event?.Invoke(new GestureEvent(kind, _machine.Phase, origin, point, message));

    public void StartHoldTimer(int milliseconds) =>
        _holdTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)milliseconds, IntPtr.Zero);

    public void StopHoldTimer()
    {
        if (_holdTimer == UIntPtr.Zero)
            return;

        KillTimer(IntPtr.Zero, _holdTimer);
        _holdTimer = UIntPtr.Zero;
    }

    public void ReplayClick() => Defer(InputSender.RightClick);

    public void ReplayDown(ScreenPoint at, ScreenPoint current) => Defer(InputSender.RightDown);

    public void RingOpened(ScreenPoint origin) => Raise(GestureEventKind.RingOpened, origin, origin);

    public void RingMoved(ScreenPoint origin, ScreenPoint current) => Raise(GestureEventKind.RingMoved, origin, current);

    public void RingReleased(ScreenPoint origin, ScreenPoint current) => Raise(GestureEventKind.RingReleased, origin, current);

    public void RingCancelled() => Raise(GestureEventKind.RingCancelled, default, default);

    public void PhaseChanged(GesturePhase phase, string reason) => Raise(GestureEventKind.Phase, default, default, reason);
}
