using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.Tests;

public class GestureStateMachineTests
{
    private sealed class FakeSink : IGestureSink
    {
        public List<string> Calls { get; } = [];
        public bool TimerRunning { get; private set; }

        public void StartHoldTimer(int milliseconds) { TimerRunning = true; Calls.Add($"timer {milliseconds}"); }
        public void StopHoldTimer() { TimerRunning = false; }
        public void ReplayClick() => Calls.Add("replay click");
        public void ReplayDown(ScreenPoint at, ScreenPoint current) => Calls.Add($"replay down {at.X},{at.Y} -> {current.X},{current.Y}");
        public void RingOpened(ScreenPoint origin) => Calls.Add("ring opened");
        public void RingMoved(ScreenPoint origin, ScreenPoint current) => Calls.Add("ring moved");
        public void RingReleased(ScreenPoint origin, ScreenPoint current) => Calls.Add("ring released");
        public void RingCancelled() => Calls.Add("ring cancelled");
        public void PhaseChanged(GesturePhase phase, string reason) { }
    }

    private static readonly ScreenPoint Origin = new(100, 100);

    private readonly FakeSink _sink = new();
    private readonly GestureStateMachine _machine;

    public GestureStateMachineTests()
    {
        _machine = new GestureStateMachine(new GestureSettings(), _sink);
    }

    [Fact]
    public void ShortClick_IsSwallowedThenReplayed()
    {
        Assert.True(_machine.OnRightDown(Origin, allowed: true));
        Assert.True(_machine.OnRightUp(Origin));

        Assert.Contains("replay click", _sink.Calls);
        Assert.False(_sink.TimerRunning);
        Assert.Equal(GesturePhase.Idle, _machine.Phase);
    }

    [Fact]
    public void SmallJitter_StillCountsAsClick()
    {
        _machine.OnRightDown(Origin, allowed: true);
        _machine.OnMove(new ScreenPoint(104, 102));
        _machine.OnRightUp(new ScreenPoint(104, 102));

        Assert.Contains("replay click", _sink.Calls);
    }

    [Fact]
    public void DragBeforeHoldDelay_BecomesNormalRightDrag()
    {
        _machine.OnRightDown(Origin, allowed: true);
        Assert.False(_machine.OnMove(new ScreenPoint(110, 100)));

        Assert.Equal(GesturePhase.Passthrough, _machine.Phase);
        Assert.Contains("replay down 100,100 -> 110,100", _sink.Calls);
        Assert.False(_sink.TimerRunning);
        Assert.False(_machine.OnRightUp(new ScreenPoint(150, 100)));
        Assert.Equal(GesturePhase.Idle, _machine.Phase);
    }

    [Fact]
    public void Hold_OpensRing_AndReleaseSelects()
    {
        _machine.OnRightDown(Origin, allowed: true);
        _machine.OnHoldElapsed();
        Assert.Equal(GesturePhase.RingOpen, _machine.Phase);

        _machine.OnMove(new ScreenPoint(100, 40));
        Assert.True(_machine.OnRightUp(new ScreenPoint(100, 40)));

        Assert.Equal(["timer 180", "ring opened", "ring moved", "ring released"], _sink.Calls);
        Assert.Equal(GesturePhase.Idle, _machine.Phase);
    }

    [Fact]
    public void LeftClickWhileRingOpen_CancelsAndSwallowsTheRest()
    {
        _machine.OnRightDown(Origin, allowed: true);
        _machine.OnHoldElapsed();

        Assert.True(_machine.OnOtherButtonDown(Origin));
        Assert.True(_machine.OnOtherButtonUp());
        Assert.Equal(GesturePhase.Swallowing, _machine.Phase);
        Assert.True(_machine.OnRightUp(Origin));

        Assert.Contains("ring cancelled", _sink.Calls);
        Assert.DoesNotContain("replay click", _sink.Calls);
        Assert.Equal(GesturePhase.Idle, _machine.Phase);
    }

    [Fact]
    public void LeftClickWhilePending_ReplaysRightDownAndPassesLeft()
    {
        _machine.OnRightDown(Origin, allowed: true);

        Assert.False(_machine.OnOtherButtonDown(Origin));
        Assert.False(_machine.OnOtherButtonUp());
        Assert.Equal(GesturePhase.Passthrough, _machine.Phase);
    }

    [Fact]
    public void DisallowedTarget_PassesEverything()
    {
        Assert.False(_machine.OnRightDown(Origin, allowed: false));
        _machine.OnHoldElapsed();
        Assert.False(_machine.OnRightUp(Origin));

        Assert.Empty(_sink.Calls);
    }

    [Fact]
    public void WheelIsSwallowedOnlyWhileRingIsOpen()
    {
        Assert.False(_machine.OnWheel());
        _machine.OnRightDown(Origin, allowed: true);
        _machine.OnHoldElapsed();
        Assert.True(_machine.OnWheel());
    }

    [Fact]
    public void LateTimer_AfterRelease_DoesNothing()
    {
        _machine.OnRightDown(Origin, allowed: true);
        _machine.OnRightUp(Origin);
        _machine.OnHoldElapsed();

        Assert.Equal(GesturePhase.Idle, _machine.Phase);
        Assert.DoesNotContain("ring opened", _sink.Calls);
    }
}
