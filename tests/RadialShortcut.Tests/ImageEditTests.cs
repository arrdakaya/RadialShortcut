using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class ImageEditTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void QuarterTurnSwapsSides()
    {
        var turn = new ImageTurn().TurnedRight();
        Assert.Equal((300.0, 400.0), turn.Turned(400, 300));
        Assert.Equal((400.0, 300.0), turn.TurnedRight().Turned(400, 300));
        Assert.True(turn.TurnedLeft().IsNone);
    }

    [Fact]
    public void StraighteningKeepsShapeAndShrinks()
    {
        var (w, h) = new ImageTurn(0, false, false, 10).Straightened(1600, 900);
        Assert.Equal(1600.0 / 900, w / h, 6);
        Assert.True(w < 1600 && h < 900);
        // The kept rectangle turned back by 10 degrees still lies inside the picture.
        var a = 10 * Math.PI / 180;
        Assert.True(w * Math.Cos(a) + h * Math.Sin(a) <= 1600 + 1e-6);
        Assert.True(w * Math.Sin(a) + h * Math.Cos(a) <= 900 + 1e-6);
        Assert.Equal((1600.0, 900.0), new ImageTurn().Straightened(1600, 900));
    }

    [Fact]
    public void FourTurnsBringTheCropBack()
    {
        var crop = new CropArea(0.1, 0.2, 0.3, 0.4);
        var turned = CropMath.TurnRight(CropMath.TurnRight(CropMath.TurnRight(CropMath.TurnRight(crop))));
        Assert.Equal(crop.X, turned.X, 9);
        Assert.Equal(crop.Y, turned.Y, 9);
        var back = CropMath.TurnLeft(CropMath.TurnRight(crop));
        Assert.Equal(crop.Width, back.Width, 9);
        Assert.Equal(crop.Y, back.Y, 9);
        // The top-left corner area ends up top-right after a clockwise turn.
        var corner = CropMath.TurnRight(new CropArea(0, 0, 0.2, 0.1));
        Assert.Equal(0.9, corner.X, 9);
        Assert.Equal(0, corner.Y, 9);
    }

    [Fact]
    public void LargestFillsOneSide()
    {
        // 1:1 in a 16:9 picture: full height, centered.
        var square = CropMath.Largest(1, 16.0 / 9);
        Assert.Equal(1, square.Height, 9);
        Assert.Equal(9.0 / 16, square.Width, 9);
        Assert.Equal((1 - 9.0 / 16) / 2, square.X, 9);
    }

    [Theory]
    [InlineData(CropEdges.Right | CropEdges.Bottom, 0.9, 0.6)]
    [InlineData(CropEdges.Left | CropEdges.Top, 0.0, 0.0)]
    [InlineData(CropEdges.Right, 1.0, 0.5)]
    [InlineData(CropEdges.Top, 0.5, 0.0)]
    public void HeldDragKeepsRatioAndStaysInside(CropEdges edges, double x, double y)
    {
        const double ratio = 16.0 / 9, aspect = 4.0 / 3;
        var start = new CropArea(0.3, 0.3, 0.3, 0.3 * aspect / ratio);
        var dragged = CropMath.DragHeld(start, edges, x, y, ratio, aspect);
        Assert.Equal(ratio, dragged.Width * aspect / dragged.Height, 6);
        Assert.True(dragged.X >= -Eps && dragged.Y >= -Eps && dragged.Right <= 1 + Eps && dragged.Bottom <= 1 + Eps);
    }

    [Fact]
    public void FitKeepsCenter()
    {
        var crop = new CropArea(0.4, 0.4, 0.2, 0.2);
        var fit = CropMath.Fit(crop, 2, 1);
        Assert.Equal(2, fit.Width / fit.Height, 6);
        Assert.Equal(0.5, fit.X + fit.Width / 2, 9);
        Assert.Equal(0.5, fit.Y + fit.Height / 2, 9);
    }

    [Fact]
    public void DrawnHeldToRatio()
    {
        var drawn = CropMath.Drawn(0.5, 0.5, 0.1, 0.45, 1, 2);
        Assert.NotNull(drawn);
        Assert.Equal(1, drawn!.Value.Width * 2 / drawn.Value.Height, 6);
        Assert.Equal(0.5, drawn.Value.Right, 9);
    }
}
