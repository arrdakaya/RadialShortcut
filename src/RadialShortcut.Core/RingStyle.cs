namespace RadialShortcut.Core;

/// <summary>Visual parameters of the ring, in device-independent pixels (96 DPI units).</summary>
public sealed record RingStyle
{
    public double OuterRadius { get; init; } = 140;
    public double InnerRadius { get; init; } = 48;
    public double Gap { get; init; } = 3;
    public string Accent { get; init; } = "#3D8BFF";
    public int OpenAnimationMs { get; init; } = 90;

    /// <summary>Room around the ring for the hover scale-up and shadow.</summary>
    public double Margin => 26;

    public double Size => 2 * (OuterRadius + Margin);
}
