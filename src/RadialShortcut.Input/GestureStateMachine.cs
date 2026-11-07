using RadialShortcut.Core;

namespace RadialShortcut.Input;

public readonly record struct ScreenPoint(int X, int Y);

public enum GesturePhase { Idle, Pending, RingOpen, Swallowing, Passthrough }

/// <summary>Mouse buttons other than the right one, as logical buttons (after any left/right swap).</summary>
[Flags]
public enum OtherButtons { None = 0, Left = 1, Middle = 2, X1 = 4, X2 = 8 }

/// <summary>Side effects the state machine asks for. Called synchronously from inside the hook callback.</summary>
public interface IGestureSink
{
    void StartHoldTimer(int milliseconds);
    void StopHoldTimer();
    void ReplayClick();
    void ReplayDown(ScreenPoint at, ScreenPoint current);
    void ReplayUp();
    void RingOpened(ScreenPoint origin);
    void RingMoved(ScreenPoint origin, ScreenPoint current);
    void RingReleased(ScreenPoint origin, ScreenPoint current);
    void RingCancelled();
    void PhaseChanged(GesturePhase phase, string reason);
}

/// <summary>
/// Right-button hold gesture. Pure logic: no Win32, no clock.
/// Every On* method returns true when the mouse event must be swallowed.
/// </summary>
public sealed class GestureStateMachine(GestureSettings settings, IGestureSink sink)
{
    private ScreenPoint _press;
    private OtherButtons _swallowUps; // buttons whose down we swallowed, so their up is swallowed too
    private bool _replayedDown;

    public GesturePhase Phase { get; private set; } = GesturePhase.Idle;

    /// <summary>Other buttons currently held, as seen through this machine's own down/up events.</summary>
    public OtherButtons Held { get; private set; }

    /// <summary>
    /// Drops held buttons that are no longer down, for when their up event never reached us
    /// (e.g. secure desktop). Never adds: <paramref name="stillDown"/> only narrows <see cref="Held"/>.
    /// </summary>
    public void ForgetReleased(OtherButtons stillDown)
    {
        Held &= stillDown;
        _swallowUps &= stillDown;
    }

    public bool OnRightDown(ScreenPoint p, bool allowed)
    {
        if (Phase != GesturePhase.Idle)
        {
            // We missed an up event somewhere (e.g. secure desktop). Start over.
            sink.StopHoldTimer();
            if (Phase == GesturePhase.RingOpen)
                sink.RingCancelled();
        }

        _replayedDown = false;
        if (!allowed)
        {
            SetPhase(GesturePhase.Passthrough, "Hedef hariç / yönetici / duraklatıldı: geçirildi");
            return false;
        }

        if (Held != OtherButtons.None)
        {
            // Right pressed during a left drag, a selection or game input: that chord belongs to the app.
            SetPhase(GesturePhase.Passthrough, $"Başka tuş basılı ({Held}): geçirildi");
            return false;
        }

        _press = p;
        SetPhase(GesturePhase.Pending, "Sağ tuş basıldı, bekleniyor");
        sink.StartHoldTimer(settings.HoldDelayMs);
        return true;
    }

    public void OnHoldElapsed()
    {
        if (Phase != GesturePhase.Pending)
            return;

        SetPhase(GesturePhase.RingOpen, $"{settings.HoldDelayMs} ms basılı tutuldu: halka açıldı");
        sink.RingOpened(_press);
    }

    public bool OnMove(ScreenPoint p)
    {
        switch (Phase)
        {
            case GesturePhase.Pending when Distance(_press, p) >= settings.DragSlopPx:
                sink.StopHoldTimer();
                sink.ReplayDown(_press, p);
                _replayedDown = true;
                SetPhase(GesturePhase.Passthrough, $"Hareket ≥ {settings.DragSlopPx} px: normal sağ sürükleme");
                break;
            case GesturePhase.RingOpen:
                sink.RingMoved(_press, p);
                break;
        }

        // Moves always pass: swallowing them would freeze the cursor.
        return false;
    }

    public bool OnRightUp(ScreenPoint p)
    {
        switch (Phase)
        {
            case GesturePhase.Pending:
                sink.StopHoldTimer();
                sink.ReplayClick();
                SetPhase(GesturePhase.Idle, "Kısa tık: normal sağ tık tekrar gönderildi");
                return true;
            case GesturePhase.RingOpen:
                sink.RingReleased(_press, p);
                SetPhase(GesturePhase.Idle, "Halka bırakıldı");
                return true;
            case GesturePhase.Swallowing:
                SetPhase(GesturePhase.Idle, "Sağ tuş bırakıldı ve yutuldu");
                return true;
            case GesturePhase.Passthrough when _replayedDown:
                // The replayed down is still queued behind the hook callback. Letting this real up pass
                // could land it before that down, leaving Windows with the right button held. Queue our own up after it.
                _replayedDown = false;
                sink.ReplayUp();
                SetPhase(GesturePhase.Idle, "Sağ tuş bırakıldı (tekrar gönderilen basışın ardından)");
                return true;
            case GesturePhase.Passthrough:
                SetPhase(GesturePhase.Idle, "Sağ tuş bırakıldı (geçirildi)");
                return false;
            default:
                return false;
        }
    }

    public bool OnOtherButtonDown(ScreenPoint p, OtherButtons button)
    {
        Held |= button;
        switch (Phase)
        {
            case GesturePhase.Pending:
                sink.StopHoldTimer();
                sink.ReplayDown(_press, p);
                _replayedDown = true;
                SetPhase(GesturePhase.Passthrough, "Başka tuş basıldı: sağ tuş geçirildi");
                return false;
            case GesturePhase.RingOpen:
                sink.RingCancelled();
                _swallowUps |= button;
                SetPhase(GesturePhase.Swallowing, "Başka tuş: halka iptal");
                return true;
            case GesturePhase.Swallowing:
                _swallowUps |= button;
                return true;
            default:
                return false;
        }
    }

    public bool OnOtherButtonUp(OtherButtons button)
    {
        Held &= ~button;
        if ((_swallowUps & button) == 0)
            return false;

        _swallowUps &= ~button;
        return true;
    }

    public bool OnWheel() => Phase is GesturePhase.RingOpen or GesturePhase.Swallowing;

    private void SetPhase(GesturePhase phase, string reason)
    {
        Phase = phase;
        sink.PhaseChanged(phase, reason);
    }

    private static double Distance(ScreenPoint a, ScreenPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
