using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class RingPlacementTests
{
    [Fact]
    public void AwayFromEdges_StaysPut() =>
        Assert.Equal((500.0, 400.0), RingGeometry.FitInside(500, 400, 150, 0, 0, 1920, 1040));

    [Fact]
    public void NearTopLeftCorner_SlidesInside() =>
        Assert.Equal((150.0, 150.0), RingGeometry.FitInside(10, 20, 150, 0, 0, 1920, 1040));

    [Fact]
    public void NearBottomRight_SlidesInside() =>
        Assert.Equal((1770.0, 890.0), RingGeometry.FitInside(1915, 1035, 150, 0, 0, 1920, 1040));

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinates_SlidesInside() =>
        Assert.Equal((-1770.0, 150.0), RingGeometry.FitInside(-1900, 5, 150, -1920, 0, 0, 1080));

    [Fact]
    public void AreaSmallerThanRing_CentersOnThatAxis() =>
        Assert.Equal((100.0, 400.0), RingGeometry.FitInside(30, 400, 150, 0, 0, 200, 1040));
}
