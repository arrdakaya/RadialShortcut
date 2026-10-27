using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class ExpressionTests
{
    [Theory]
    [InlineData("12*4+3", 51)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("-2^2", -4)]
    [InlineData("2^3^2", 512)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("7 % 3", 1)]
    [InlineData("3 × 4 ÷ 2", 6)]
    [InlineData("1,5*2", 3)]
    [InlineData("12*4+3 =", 51)]
    [InlineData("--3", 3)]
    public void Evaluates(string input, double expected)
    {
        Assert.True(Expression.TryEvaluate(input, out var v));
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2+")]
    [InlineData("(1+2")]
    [InlineData("1/0")]
    [InlineData("1..2")]
    public void Rejects(string input) => Assert.False(Expression.TryEvaluate(input, out _));

    [Fact]
    public void FormatKeepsDecimalComma()
    {
        Assert.Equal("2,5", Expression.Format(2.5, "5/2,0"));
        Assert.Equal("2.5", Expression.Format(2.5, "5/2"));
        Assert.Equal("0.3", Expression.Format(0.1 + 0.2, "0.1+0.2"));
    }
}

public class ColorFormatTests
{
    [Fact]
    public void FormatsAllNotations()
    {
        Assert.Equal("#0067C0", ColorFormat.Format(ColorNotation.Hex, 0x00, 0x67, 0xC0));
        Assert.Equal("rgb(0, 103, 192)", ColorFormat.Format(ColorNotation.Rgb, 0x00, 0x67, 0xC0));
        Assert.Equal("hsl(208, 100%, 38%)", ColorFormat.Format(ColorNotation.Hsl, 0x00, 0x67, 0xC0));
    }

    [Theory]
    [InlineData(255, 0, 0, 0, 100, 50)]
    [InlineData(0, 255, 0, 120, 100, 50)]
    [InlineData(128, 128, 128, 0, 0, 50)]
    [InlineData(255, 255, 255, 0, 0, 100)]
    public void Hsl(byte r, byte g, byte b, int h, int s, int l) => Assert.Equal((h, s, l), ColorFormat.ToHsl(r, g, b));

    [Fact]
    public void NextCycles() => Assert.Equal(ColorNotation.Hex, ColorFormat.Next(ColorFormat.Next(ColorFormat.Next(ColorNotation.Hex))));
}

public class ScrollStitchTests
{
    /// <summary>A "page" of distinct rows; a frame is a window of it, optionally with a fixed header.</summary>
    private static ulong[] Frame(int offset, int height, int header = 0)
    {
        var rows = new ulong[height];
        for (var i = 0; i < height; i++)
            rows[i] = i < header ? 1000000UL + (ulong)i : (ulong)(offset + i) * 7919;
        return rows;
    }

    [Fact]
    public void FindsShift()
    {
        var m = ScrollStitch.Match(Frame(0, 100), Frame(30, 100));
        Assert.Equal(30, m!.Value.Shift);
    }

    [Fact]
    public void IdenticalFramesMeanEnd() => Assert.Equal(0, ScrollStitch.Match(Frame(0, 100), Frame(0, 100))!.Value.Shift);

    [Fact]
    public void DetectsStickyHeader()
    {
        var m = ScrollStitch.Match(Frame(0, 100, header: 10), Frame(25, 100, header: 10))!.Value;
        Assert.Equal(25, m.Shift);
        Assert.Equal(10, m.Top);
    }

    [Fact]
    public void UnrelatedFramesGiveNull()
    {
        var other = Frame(0, 100).Select(r => r + 3).ToArray();
        Assert.Null(ScrollStitch.Match(Frame(0, 100), other));
    }

    [Fact]
    public void ToleratesAFewChangedRows()
    {
        var curr = Frame(20, 100);
        curr[50] = 42; // e.g. a blinking caret
        Assert.Equal(20, ScrollStitch.Match(Frame(0, 100), curr)!.Value.Shift);
    }

    [Fact]
    public void RowHashesDifferPerRow()
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[8] = 1;
        var h = ScrollStitch.RowHashes(pixels, 2, 2, 8);
        Assert.NotEqual(h[0], h[1]);
    }
}
