namespace RadialShortcut.Core;

public sealed record GestureSettings
{
    public int HoldDelayMs { get; init; } = 180;
    public int DragSlopPx { get; init; } = 6;
    public int DeadZonePx { get; init; } = 22;
    public int SliceCount { get; init; } = 8;
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = ["mstsc.exe", "vmconnect.exe"];
}
