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

    [Fact]
    public void RowHashesIgnoreSkippedRightColumns()
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[12] = 1; // second row, right pixel (a scrollbar)
        var h = ScrollStitch.RowHashes(pixels, 2, 2, 8, skipRight: 1);
        Assert.Equal(h[0], h[1]);
    }
}

public class ScrollProfileTests
{
    // A page of distinct rows; each row's profile is derived from its page position.
    private static byte[][] Frame(int offset, int height, int noise = 0, int changedFrom = -1, int changedTo = -1)
    {
        var rng = new Random(offset);
        return Enumerable.Range(0, height).Select(y =>
        {
            var row = Enumerable.Range(0, 32).Select(k => (byte)Math.Clamp(new Random((y + offset) * 64 + k).Next(20, 236) + rng.Next(-noise, noise + 1), 0, 255)).ToArray();
            if (y >= changedFrom && y < changedTo)
                row = row.Select(v => (byte)(255 - v)).ToArray();
            return row;
        }).ToArray();
    }

    [Fact]
    public void SmallRenderingDifferencesStillLineUp() =>
        Assert.Equal(40, ScrollStitch.Match(Frame(0, 200), Frame(40, 200, noise: 4))!.Value.Shift);

    [Fact]
    public void PartlyChangedFrameStillLinesUp() =>
        Assert.Equal(40, ScrollStitch.Match(Frame(0, 200), Frame(40, 200, changedFrom: 20, changedTo: 50))!.Value.Shift);

    [Fact]
    public void PlayingVideoInStillFrameIsNoScroll() =>
        Assert.Equal(0, ScrollStitch.Match(Frame(0, 200), Frame(0, 200, changedFrom: 60, changedTo: 120))!.Value.Shift);

    [Fact]
    public void UnrelatedFramesDoNotLineUp() =>
        Assert.Null(ScrollStitch.Match(Frame(0, 200), Frame(0, 200).Select(r => r.Select(v => (byte)(v ^ 0x80)).ToArray()).ToArray()));

    [Fact]
    public void ProfilesIgnoreSkippedColumns()
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[12] = pixels[13] = pixels[14] = 255; // second row, right pixel
        var p = ScrollStitch.RowProfiles(pixels, 2, 2, 8, skipRight: 1);
        Assert.Equal(p[0], p[1]);
    }
}

public class VideoCutsTests
{
    private static Cut C(double a, double b) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));

    [Fact]
    public void CleanClampsDropsShortAndKeepsOrder()
    {
        var kept = VideoCuts.Clean([C(20, 40), C(-1, 2), C(5, 5.1), C(50, 99)], TimeSpan.FromSeconds(60));
        Assert.Equal([C(20, 40), C(0, 2), C(50, 60)], kept);
        Assert.Equal(TimeSpan.FromSeconds(32), VideoCuts.Total(kept));
    }

    [Fact]
    public void FilterTrimsAndConcatsEveryPiece()
    {
        Assert.Equal(
            "[0:v:0]trim=start=1.5:end=4,setpts=PTS-STARTPTS[v0];[0:a:0]atrim=start=1.5:end=4,asetpts=PTS-STARTPTS[a0];" +
            "[0:v:0]trim=start=10:end=12.25,setpts=PTS-STARTPTS[v1];[0:a:0]atrim=start=10:end=12.25,asetpts=PTS-STARTPTS[a1];" +
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]",
            VideoCuts.Filter([C(1.5, 4), C(10, 12.25)], video: true, audio: true));
        Assert.Equal(
            "[0:v:0]trim=start=0:end=1,setpts=PTS-STARTPTS[v0];[0:v:0]trim=start=3:end=4,setpts=PTS-STARTPTS[v1];[v0][v1]concat=n=2:v=1:a=0[v]",
            VideoCuts.Filter([C(0, 1), C(3, 4)], video: true, audio: false));
    }

    [Fact]
    public void ConcatListEscapesQuotes()
    {
        Assert.Equal("ffconcat version 1.0\nfile 'C:\\a\\it'\\''s.mp4'\ninpoint 1.5\noutpoint 4\n",
            VideoCuts.ConcatList(@"C:\a\it's.mp4", [C(1.5, 4)]));
    }

    [Fact]
    public void SplitRefusesNearEdges()
    {
        var cuts = new List<Cut> { C(0, 10) };
        Assert.False(VideoCuts.Split(cuts, 0, TimeSpan.FromSeconds(0.1)));
        Assert.True(VideoCuts.Split(cuts, 0, TimeSpan.FromSeconds(4)));
        Assert.Equal([C(0, 4), C(4, 10)], cuts);
        Assert.Equal(1, VideoCuts.IndexAt(cuts, 0, TimeSpan.FromSeconds(4)));
        Assert.Equal(-1, VideoCuts.IndexAt(cuts, 0, TimeSpan.FromSeconds(10)));
    }
}

public class JoinFilesTests
{
    private static Cut C(double a, double b, int source) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), source);

    private const string Phone = """
          Duration: 00:01:02.50, start: 0.000000, bitrate: 9000 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 8800 kb/s, 29.97 fps, 29.97 tbr, 90k tbn (default)
              Side data:
                displaymatrix: rotation of -90.00 degrees
          Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 192 kb/s (default)
        """;

    [Fact]
    public void ProbeReadsTurnedSizeCodecsAndLength()
    {
        var info = MediaProbe.Parse(Phone);
        Assert.Equal(new MediaInfo(true, true, 1080, 1920, 29.97, "h264", "aac", 48000, new TimeSpan(0, 0, 1, 2, 500)), info);
    }

    [Fact]
    public void ProbeIgnoresCoverArt()
    {
        var info = MediaProbe.Parse("""
              Stream #0:0: Audio: mp3 (mp3float), 44100 Hz, mono, fltp, 64 kb/s
              Stream #0:1: Video: mjpeg (Baseline), yuvj420p(pc), 64x64 [SAR 1:1 DAR 1:1], 90k tbr, 90k tbn (attached pic)
            """);
        Assert.False(info.Video);
        Assert.True(info.Audio);
        Assert.Equal(44100, info.SampleRate);
    }

    [Fact]
    public void InputsListsEachFileOnceInOrderOfUse()
    {
        var (files, cuts) = VideoCuts.Inputs([C(0, 1, 3), C(0, 1, 1), C(2, 3, 3)]);
        Assert.Equal([3, 1], files);
        Assert.Equal([C(0, 1, 0), C(0, 1, 1), C(2, 3, 0)], cuts);
    }

    [Fact]
    public void JoinFormatTakesTheFirstPicture()
    {
        MediaInfo[] inputs = [new(false, true), new(true, false, 1281, 721, 59.94)];
        Assert.Equal(new JoinFormat(true, true, 1280, 720, 60), JoinFormat.For([C(0, 1, 0), C(0, 1, 1)], inputs));
        Assert.Equal(new JoinFormat(false, true, 1280, 720, 30), JoinFormat.For([C(0, 1, 0)], inputs));
    }

    [Fact]
    public void JoinFilterFillsMissingPictureAndSound()
    {
        MediaInfo[] inputs = [new(true, false, 640, 480, 25), new(false, true)];
        var filter = VideoCuts.JoinFilter([C(1, 2, 0), C(0, 1.5, 1)], inputs, new JoinFormat(true, true, 640, 480, 25));
        Assert.Equal(
            "[0:v:0]trim=start=1:end=2,setpts=PTS-STARTPTS,scale=640:480:force_original_aspect_ratio=decrease,pad=640:480:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=25,format=yuv420p[v0];" +
            "anullsrc=r=48000:cl=stereo,atrim=duration=1,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a0];" +
            "color=c=black:s=640x480:r=25:d=1.5,setsar=1,fps=25,format=yuv420p[v1];" +
            "[1:a:0]atrim=start=0:end=1.5,asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a1];" +
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]",
            filter);
    }

    [Fact]
    public void CopyOnlyForFilesMadeAlike()
    {
        var a = new MediaInfo(true, true, 1920, 1080, 30, "h264", "aac", 48000);
        Assert.True(VideoCuts.CanCopy(["a.mp4", "b.MP4"], [a, a with { Fps = 29.97 }]));
        Assert.False(VideoCuts.CanCopy(["a.mp4", "b.mp4"], [a, a with { Width = 1280 }]));
        Assert.False(VideoCuts.CanCopy(["a.mp4", "b.mov"], [a, a]));
        Assert.False(VideoCuts.CanCopy(["a.mp4", "b.mp4"], [a, a with { Audio = false, AudioCodec = null }]));
    }

    [Fact]
    public void ConcatListNamesEachPiecesFile()
    {
        Assert.Equal("ffconcat version 1.0\nfile 'b.mp4'\ninpoint 0\noutpoint 1\nfile 'a.mp4'\ninpoint 2\noutpoint 3\n",
            VideoCuts.ConcatList(["a.mp4", "b.mp4"], [C(0, 1, 1), C(2, 3, 0)]));
    }

    [Fact]
    public void CleanClampsEachPieceToItsOwnFile()
    {
        var kept = VideoCuts.Clean([C(0, 99, 0), C(0, 99, 1)], [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]);
        Assert.Equal([C(0, 10, 0), C(0, 20, 1)], kept);
    }
}

public class VideoCropTests
{
    [Fact]
    public void BetweenOrdersCornersAndDropsStrayClicks()
    {
        Assert.Equal(new CropArea(0.25, 0.5, 0.5, 0.25), CropArea.Between(0.75, 0.75, 0.25, 0.5));
        Assert.Null(CropArea.Between(0.5, 0.5, 0.51, 0.9));
        Assert.Null(CropArea.Between(-1, -1, 2, 2)); // the whole picture is no crop
    }

    [Fact]
    public void MovingStopsAtThePictureEdges()
    {
        var a = new CropArea(0.1, 0.1, 0.5, 0.5);
        Assert.Equal(new CropArea(0.5, 0, 0.5, 0.5), a.Moved(0.9, -0.9));
    }

    [Fact]
    public void DraggingAnEdgeNeverCrossesTheOther()
    {
        var a = new CropArea(0.2, 0.2, 0.4, 0.4);
        var dragged = a.Dragged(CropEdges.Left | CropEdges.Top, 0.9, 0.1);
        Assert.Equal(0.6 - CropArea.Smallest, dragged.X, 6);
        Assert.Equal(0.1, dragged.Y, 6);
        Assert.Equal(0.6, dragged.Right, 6);
        Assert.Equal(0.6, dragged.Bottom, 6);
    }

    [Fact]
    public void HitTestFindsCornersEdgesAndInside()
    {
        var a = new CropArea(0.2, 0.2, 0.4, 0.4);
        Assert.Equal(CropEdges.Left | CropEdges.Top, a.HitTest(0.21, 0.19, 0.02, 0.02));
        Assert.Equal(CropEdges.Right, a.HitTest(0.6, 0.4, 0.02, 0.02));
        Assert.Equal(CropEdges.Move, a.HitTest(0.4, 0.4, 0.02, 0.02));
        Assert.Equal(CropEdges.None, a.HitTest(0.9, 0.9, 0.02, 0.02));
    }

    [Fact]
    public void PixelsAreEvenAndInsideThePicture()
    {
        Assert.Equal(new PixelCrop(192, 108, 960, 540), new CropArea(0.1, 0.1, 0.5, 0.5).ToPixels(1920, 1080));
        var p = new CropArea(0.5, 0.5, 0.5, 0.5).ToPixels(1281, 721);
        Assert.True(p.Width % 2 == 0 && p.Height % 2 == 0);
        Assert.True(p.X + p.Width <= 1281 && p.Y + p.Height <= 721);
        Assert.Equal("crop=960:540:192:108", new CropArea(0.1, 0.1, 0.5, 0.5).Filter(1920, 1080));
        Assert.StartsWith("crop=trunc(iw*0.5/2)*2", new CropArea(0.1, 0.1, 0.5, 0.5).Filter(0, 0));
    }

    [Fact]
    public void FiltersCropBeforeFittingTheJoinedSize()
    {
        var c = new Cut(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0);
        Assert.Equal("[0:v:0]trim=start=0:end=1,setpts=PTS-STARTPTS,crop=2:2:0:0[v0];[v0]concat=n=1:v=1:a=0[v]",
            VideoCuts.Filter([c], true, false, ["crop=2:2:0:0"]));
        var join = VideoCuts.JoinFilter([c], [new MediaInfo(true, false, 640, 480, 30)], new JoinFormat(true, false, 320, 240, 30), ["crop=320:240:0:0"]);
        Assert.StartsWith("[0:v:0]trim=start=0:end=1,setpts=PTS-STARTPTS,crop=320:240:0:0,scale=320:240", join);
        Assert.Equal(new MediaInfo(true, true, 960, 540), new CropArea(0.1, 0.1, 0.5, 0.5).Apply(new MediaInfo(true, true, 1920, 1080)));
    }
}
public class TimelineTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void ZoomKeepsThePointUnderTheMouseInPlace()
    {
        var view = TimelineView.All(S(100)).Zoomed(0.5, S(25), S(100));
        Assert.Equal(new TimelineView(S(12.5), S(50)), view);
        Assert.Equal(S(25), view.ToTime(view.ToX(S(25), 800), 800));
    }

    [Fact]
    public void ZoomStaysInsideTheFileAndStopsAtTheNarrowest()
    {
        var view = TimelineView.All(S(100)).Zoomed(0.001, S(99), S(100));
        Assert.Equal(TimelineView.Narrowest, view.Length);
        Assert.InRange(S(99), view.Start, view.End);
        Assert.Equal(new TimelineView(S(98), S(2)), new TimelineView(S(90), S(10)).Zoomed(0.001, S(100), S(100))); // the end stays the end
        Assert.Equal(TimelineView.All(S(100)), view.Zoomed(1000, S(50), S(100)));
    }

    [Fact]
    public void PanningAndFollowingStopAtTheEnds()
    {
        var view = new TimelineView(S(10), S(10));
        Assert.Equal(new TimelineView(S(0), S(10)), view.Panned(S(-50), S(100)));
        Assert.Equal(new TimelineView(S(90), S(10)), view.Panned(S(500), S(100)));
        Assert.Equal(view, view.Showing(S(15), S(100)));
        Assert.Equal(new TimelineView(S(24), S(10)), view.Showing(S(25), S(100)));
    }

    [Fact]
    public void EdgeIsFoundWithinTolerance()
    {
        Cut[] cuts = [new(S(10), S(20), 0), new(S(20), S(30), 1), new(S(40), S(50), 0)];
        var view = TimelineView.All(S(100)); // 10 px a second over 1000 px
        Assert.Equal(new CutEdge(0, true), CutEdges.At(cuts, 0, view, 1000, 203, 6));
        Assert.Equal(new CutEdge(2, false), CutEdges.At(cuts, 0, view, 1000, 398, 6));
        Assert.Null(CutEdges.At(cuts, 0, view, 1000, 300, 6)); // that edge is file 1's
    }

    [Fact]
    public void MovedEdgeKeepsThePieceInTheFileAndLongEnough()
    {
        var c = new Cut(S(10), S(20));
        Assert.Equal(new Cut(S(10), S(30)), CutEdges.Move(c, true, S(99), S(30)));
        Assert.Equal(new Cut(S(10), S(10) + VideoCuts.Shortest), CutEdges.Move(c, true, S(5), S(30)));
        Assert.Equal(new Cut(S(0), S(20)), CutEdges.Move(c, false, S(-3), S(30)));
        Assert.Equal(new Cut(S(20) - VideoCuts.Shortest, S(20)), CutEdges.Move(c, false, S(25), S(30)));
    }
}

public class WaveformTests
{
    [Fact]
    public void EachPeakIsTheLoudestOfItsSamples()
    {
        var per = Waveform.SamplesPerPeak;
        var bytes = new byte[(per + 1) * 2];
        BitConverter.GetBytes((short)-16384).CopyTo(bytes, 2 * 3); // half scale in the first peak
        BitConverter.GetBytes(short.MaxValue).CopyTo(bytes, 2 * per); // the lone sample of the second
        var peaks = Waveform.Peaks(bytes);
        Assert.Equal(2, peaks.Length);
        Assert.Equal(0.5f, peaks[0], 3);
        Assert.Equal(1f, peaks[1], 3);
    }

    [Fact]
    public void MaxCoversFractionalRangesAndStaysInBounds()
    {
        float[] peaks = [0.1f, 0.9f, 0.2f];
        Assert.Equal(0.9f, Waveform.Max(peaks, 0.5, 1.2));
        Assert.Equal(0.1f, Waveform.Max(peaks, 0.2, 0.4));
        Assert.Equal(0.2f, Waveform.Max(peaks, 2.5, 99));
        Assert.Equal(0f, Waveform.Max([], 0, 1));
    }
}

public class GifTests
{
    [Fact]
    public void GifIsNarrowedAndGetsItsOwnPalette()
    {
        Assert.Contains("scale=960:-2", VideoCuts.GifTail(1920));
        Assert.Contains("scale=640:-2", VideoCuts.GifTail(641));
        Assert.Contains("scale='min(960,iw)':-2", VideoCuts.GifTail(0));
        Assert.StartsWith(";[v]fps=15,", VideoCuts.GifTail(800));
        Assert.EndsWith("paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[gif]", VideoCuts.GifTail(800));
    }
}

public class SequenceTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    // 10 s of file 0 from 5 s, 4 s of file 1 from 0, 6 s of file 0 from 30 s: 20 s in all.
    private static readonly Cut[] Cuts = [new(S(5), S(15), 0), new(S(0), S(4), 1), new(S(30), S(36), 0)];

    [Fact]
    public void StartsAreTheLengthsBefore()
    {
        Assert.Equal([S(0), S(10), S(14)], Sequence.Starts(Cuts));
        Assert.Empty(Sequence.Starts([]));
    }

    [Fact]
    public void ResultTimeMapsToAPieceAndBack()
    {
        Assert.Equal((0, S(5)), Sequence.Locate(Cuts, S(0)));
        Assert.Equal((0, S(12)), Sequence.Locate(Cuts, S(7)));
        Assert.Equal((1, S(0)), Sequence.Locate(Cuts, S(10))); // the seam is the later piece's
        Assert.Equal((2, S(31)), Sequence.Locate(Cuts, S(15)));
        Assert.Equal((2, S(36)), Sequence.Locate(Cuts, S(99))); // past the end: the last frame
        Assert.Equal((0, S(5)), Sequence.Locate(Cuts, S(-3)));
        Assert.Equal((-1, TimeSpan.Zero), Sequence.Locate([], S(1)));
        foreach (var t in new[] { 0, 3.5, 10, 13.9, 14, 19.5 })
        {
            var (i, at) = Sequence.Locate(Cuts, S(t));
            Assert.Equal(S(t), Sequence.ToResult(Cuts, i, at));
        }
        Assert.Equal(S(14), Sequence.ToResult(Cuts, 1, S(99))); // kept inside the piece
    }

    [Fact]
    public void LocateEndGivesTheEarlierPieceOnASeam()
    {
        Assert.Equal((0, S(15)), Sequence.LocateEnd(Cuts, S(10))); // the piece that ends there
        Assert.Equal((1, S(1)), Sequence.LocateEnd(Cuts, S(11)));
        Assert.Equal((0, S(5)), Sequence.LocateEnd(Cuts, S(0)));
        Assert.Equal((2, S(36)), Sequence.LocateEnd(Cuts, S(20)));
        Assert.Equal((2, S(36)), Sequence.LocateEnd(Cuts, S(99)));
        Assert.Equal((-1, TimeSpan.Zero), Sequence.LocateEnd([], S(1)));
    }

    [Fact]
    public void IndexAtIsMinusOneOutsideThePieces()
    {
        Assert.Equal(1, Sequence.IndexAt(Cuts, S(10)));
        Assert.Equal(-1, Sequence.IndexAt(Cuts, S(20)));
        Assert.Equal(-1, Sequence.IndexAt(Cuts, S(-1)));
    }

    [Fact]
    public void DropSlotSplitsEachPieceInHalves()
    {
        Assert.Equal(0, Sequence.DropSlot(Cuts, S(4)));
        Assert.Equal(1, Sequence.DropSlot(Cuts, S(6)));
        Assert.Equal(2, Sequence.DropSlot(Cuts, S(12.5)));
        Assert.Equal(3, Sequence.DropSlot(Cuts, S(18)));
        Assert.Equal(0, Sequence.DropSlot([], S(1)));
    }

    [Fact]
    public void MoveTakesSlotsCountedBeforeTheMove()
    {
        var cuts = Cuts.ToList();
        Assert.Equal(2, Sequence.Move(cuts, 0, 3)); // first to the end
        Assert.Equal([Cuts[1], Cuts[2], Cuts[0]], cuts);
        cuts = Cuts.ToList();
        Assert.Equal(0, Sequence.Move(cuts, 2, 0)); // last to the front
        Assert.Equal([Cuts[2], Cuts[0], Cuts[1]], cuts);
        cuts = Cuts.ToList();
        Assert.Equal(-1, Sequence.Move(cuts, 1, 1)); // onto its own edges: no move
        Assert.Equal(-1, Sequence.Move(cuts, 1, 2));
        Assert.Equal(Cuts, cuts);
    }

    [Fact]
    public void EdgeOnASeamGoesByTheSideOfTheMouse()
    {
        var view = TimelineView.All(S(20)); // 50 px a second over 1000 px: seams at 500 and 700
        var starts = Sequence.Starts(Cuts);
        Assert.Equal(new CutEdge(0, true), Sequence.EdgeAt(Cuts, starts, view, 1000, 497, 6));
        Assert.Equal(new CutEdge(1, false), Sequence.EdgeAt(Cuts, starts, view, 1000, 503, 6));
        Assert.Equal(new CutEdge(0, false), Sequence.EdgeAt(Cuts, starts, view, 1000, -2, 6)); // the very start
        Assert.Equal(new CutEdge(2, true), Sequence.EdgeAt(Cuts, starts, view, 1000, 1003, 6)); // and the very end
        Assert.Null(Sequence.EdgeAt(Cuts, starts, view, 1000, 600, 6));
    }

    [Fact]
    public void FilmStripStepAndNearestFrame()
    {
        Assert.Equal(S(0.5), FilmStrip.Step(S(30)));
        Assert.Equal(S(15), FilmStrip.Step(S(3600)));
        TimeSpan[] times = [S(0), S(1), S(2), S(3)];
        Assert.Equal(0, FilmStrip.Nearest(times, S(-5)));
        Assert.Equal(1, FilmStrip.Nearest(times, S(1.4)));
        Assert.Equal(2, FilmStrip.Nearest(times, S(1.6)));
        Assert.Equal(3, FilmStrip.Nearest(times, S(9)));
        Assert.Equal(-1, FilmStrip.Nearest([], S(1)));
    }
}
