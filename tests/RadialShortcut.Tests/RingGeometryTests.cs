using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class RingGeometryTests
{
    [Theory]
    [InlineData(0, -50, 0)]   // up
    [InlineData(50, -50, 1)]  // up-right
    [InlineData(50, 0, 2)]    // right
    [InlineData(0, 50, 4)]    // down
    [InlineData(-50, 0, 6)]   // left
    [InlineData(-50, -50, 7)] // up-left
    public void EightSlices_MapCompassDirections(double dx, double dy, int expected) =>
        Assert.Equal(expected, RingGeometry.SliceAt(dx, dy, 8, 22));

    [Fact]
    public void InsideDeadZone_IsNoSelection() =>
        Assert.Equal(-1, RingGeometry.SliceAt(10, -10, 8, 22));

    [Fact]
    public void FarOutside_StillSelects() =>
        Assert.Equal(0, RingGeometry.SliceAt(0, -5000, 8, 22));

    [Fact]
    public void SliceBoundary_JustLeftOfUp_IsStillUp() =>
        Assert.Equal(0, RingGeometry.SliceAt(-Math.Tan(20 * Math.PI / 180) * 100, -100, 8, 22));
}
