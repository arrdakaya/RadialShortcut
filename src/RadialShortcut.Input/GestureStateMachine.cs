using RadialShortcut.Core;

namespace RadialShortcut.Input;

public readonly record struct ScreenPoint(int X, int Y);

public enum GesturePhase { Idle, Pending, RingOpen, Swallowing, Passthrough }

/// <summary>Side effects the state machine asks for. Called synchronously from inside the hook callback.</summary>
public interface IGestureSink
{
    void StartHoldTimer(int milliseconds);
    void StopHoldTimer();
    void ReplayClick();
    void ReplayDown(ScreenPoint at, ScreenPoint current);
    void RingOpened(ScreenPoint origin);
    void RingMoved(ScreenPoint origin, ScreenPoint current);
    void RingReleased(ScreenPoint origin, ScreenPoint current);
    void RingCancelled();
    void PhaseChanged(GesturePhase phase, string reason);
}

/// <summary>
/// Right-button hold gesture (PLAN.md, section 4). Pure logic: no Win32, no clock.
/// Every On* method returns true when the mouse event must be swallowed.
/// </summary>
public sealed class GestureStateMachine(GestureSettings settings, IGestureSink sink)
{
    private ScreenPoint _press;
    private bool _swallowOtherUp;

    public GesturePhase Phase { get; private set; } = GesturePhase.Idle;

    public bool OnRightDown(ScreenPoint p, bool allowed)
    {
        if (Phase != GesturePhase.Idle)
        {
            // We missed an up event somewhere (e.g. secure desktop). Start over.
            sink.StopHoldTimer();
            if (Phase == GesturePhase.RingOpen)
                sink.RingCancelled();
        }

        if (!allowed)
        {
            SetPhase(GesturePhase.Passthrough, "Hedef hariç / yönetici / duraklatıldı: geçirildi");
            return false;
        }

        _press = p;
        _swallowOtherUp = false;
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
            case GesturePhase.Passthrough:
                SetPhase(GesturePhase.Idle, "Sağ tuş bırakıldı (geçirildi)");
                return false;
            default:
                return false;
        }
    }

    public bool OnOtherButtonDown(ScreenPoint p)
    {
        switch (Phase)
        {
            case GesturePhase.Pending:
                sink.StopHoldTimer();
                sink.ReplayDown(_press, p);
                SetPhase(GesturePhase.Passthrough, "Başka tuş basıldı: sağ tuş geçirildi");
                return false;
            case GesturePhase.RingOpen:
                sink.RingCancelled();
                _swallowOtherUp = true;
                SetPhase(GesturePhase.Swallowing, "Başka tuş: halka iptal");
                return true;
            case GesturePhase.Swallowing:
                _swallowOtherUp = true;
                return true;
            default:
                return false;
        }
    }

    public bool OnOtherButtonUp()
    {
        if (!_swallowOtherUp)
            return false;

        _swallowOtherUp = false;
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
