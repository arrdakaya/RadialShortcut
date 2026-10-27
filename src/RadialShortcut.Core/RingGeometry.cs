namespace RadialShortcut.Core;

public static class RingGeometry
{
    /// <summary>
    /// Angle in degrees, clockwise, 0 = straight up. Screen coordinates (y grows downward).
    /// </summary>
    public static double AngleOf(double dx, double dy) =>
        (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;

    /// <summary>
    /// Index of the slice the offset points at, or -1 inside the dead zone.
    /// Slice 0 is centered on "up", indices increase clockwise. There is no outer limit.
    /// </summary>
    public static int SliceAt(double dx, double dy, int sliceCount, double deadZone)
    {
        if (Math.Sqrt(dx * dx + dy * dy) < deadZone)
            return -1;

        var step = 360.0 / sliceCount;
        return (int)Math.Floor((AngleOf(dx, dy) + step / 2) / step) % sliceCount;
    }
}
