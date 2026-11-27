using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class FileOptionTests
{
    [Theory]
    [InlineData("1-3, 5", 10, new[] { 1, 2, 3, 5 })]
    [InlineData("8-", 10, new[] { 8, 9, 10 })]
    [InlineData("-2", 10, new[] { 1, 2 })]
    [InlineData("3-1,2", 10, new[] { 1, 2, 3 })]
    [InlineData("4 – 6", 10, new[] { 4, 5, 6 })]
    [InlineData("9-20", 10, new[] { 9, 10 })]
    [InlineData("12", 10, new int[0])]
    public void PagesAreRead(string text, int count, int[] pages) => Assert.Equal(pages, PageRange.Parse(text, count));

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("1-x")]
    public void BadPagesAreRefused(string text) => Assert.Null(PageRange.Parse(text, 10));

    [Fact]
    public void PagesAreWrittenShort() => Assert.Equal("1-3, 5, 7-8", PageRange.Format([1, 2, 3, 5, 7, 8]));

    [Theory]
    [InlineData("2", 2L * 1024 * 1024)]
    [InlineData("2,5", (long)(2.5 * 1024 * 1024))]
    [InlineData("0.5 MB", 512L * 1024)]
    [InlineData("800 KB", 800L * 1024)]
    public void SizesAreRead(string text, long bytes) => Assert.Equal(bytes, SizeTarget.ParseBytes(text));

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("iki")]
    public void BadSizesAreRefused(string text) => Assert.Null(SizeTarget.ParseBytes(text));

    [Fact]
    public void BitratesFillTheTarget()
    {
        // 10 MB over 60 s: about 1.3 Mbit/s in all.
        var (v, a) = SizeTarget.Bitrates(10L * 1024 * 1024, 60, audio: true)!.Value;
        Assert.InRange(v + a, 1200, 1400);
        Assert.InRange(a, 32, 128);
        Assert.Null(SizeTarget.Bitrates(100 * 1024, 600, audio: true));
        Assert.NotNull(SizeTarget.Bitrates(SizeTarget.Smallest(600, true), 600, audio: true));
    }

    [Fact]
    public void LowBitrateStepsTheHeightDown()
    {
        Assert.Equal(1080, SizeTarget.Height(1920, 1080, 30, 4000));
        Assert.True(SizeTarget.Height(1920, 1080, 30, 600) < 720);
        Assert.Equal(360, SizeTarget.Height(1920, 1080, 30, 50));
        Assert.Equal(400, SizeTarget.Height(640, 400, 30, 8000));
    }

    [Fact]
    public void OpenRangeAgainstHugeCountStaysSmall() => Assert.Equal(100_000, PageRange.Parse("1-", int.MaxValue)!.Count);
}
