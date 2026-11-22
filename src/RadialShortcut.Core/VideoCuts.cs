using System.Globalization;
using System.Text;

namespace RadialShortcut.Core;

/// <summary>One kept piece, from Start to End of source file number Source.</summary>
public readonly record struct Cut(TimeSpan Start, TimeSpan End, int Source = 0)
{
    public TimeSpan Length => End - Start;

    public bool Contains(TimeSpan t) => t >= Start && t < End;
}

/// <summary>The picture and sound every piece is brought to when pieces of different files are joined.</summary>
public sealed record JoinFormat(bool Video, bool Audio, int Width, int Height, int Fps)
{
    public const int SampleRate = 48000;

    /// <summary>
    /// The size and frame rate of the first piece that has a picture, so a clip joined onto a
    /// recording keeps the recording's look; sound is resampled to 48 kHz stereo.
    /// </summary>
    public static JoinFormat For(IReadOnlyList<Cut> cuts, IReadOnlyList<MediaInfo> sources)
    {
        var used = cuts.Select(c => sources[c.Source]).ToList();
        var first = used.FirstOrDefault(s => s.Video && s.Width > 0 && s.Height > 0);
        int width = first is null ? 1280 : Even(first.Width), height = first is null ? 720 : Even(first.Height);
        var fps = first is { Fps: > 0 } ? (int)Math.Clamp(Math.Round(first.Fps), 1, 120) : 30;
        return new JoinFormat(used.Any(s => s.Video), used.Any(s => s.Audio), width, height, fps);
    }

    private static int Even(int n) => Math.Max(2, n - n % 2);
}

/// <summary>
/// The pieces "Video kırp" keeps, in the order they are joined, and the ffmpeg input that cuts
/// them out of one or more files and puts them back together as a single clip.
/// </summary>
public static class VideoCuts
{
    /// <summary>Pieces shorter than this are dropped: ffmpeg makes nothing useful of them.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(0.2);

    public static List<Cut> Clean(IEnumerable<Cut> cuts, TimeSpan length) => Clean(cuts, [length]);

    /// <summary>
    /// Clamps the pieces to their file (when its length is known), drops the ones too short to
    /// keep and leaves the order alone, since that is the order they are joined in.
    /// </summary>
    public static List<Cut> Clean(IEnumerable<Cut> cuts, IReadOnlyList<TimeSpan> lengths)
    {
        var kept = new List<Cut>();
        foreach (var c in cuts)
        {
            var length = c.Source >= 0 && c.Source < lengths.Count ? lengths[c.Source] : TimeSpan.Zero;
            var start = c.Start < TimeSpan.Zero ? TimeSpan.Zero : c.Start;
            var end = length > TimeSpan.Zero && c.End > length ? length : c.End;
            if (end - start >= Shortest)
                kept.Add(c with { Start = start, End = end });
        }
        return kept;
    }

    public static TimeSpan Total(IEnumerable<Cut> cuts) => cuts.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);

    /// <summary>
    /// The files the pieces use, in the order they first appear, and the pieces renumbered to
    /// point into that list: those are the ffmpeg inputs, -i 0, -i 1 and so on.
    /// </summary>
    public static (List<int> Files, List<Cut> Cuts) Inputs(IReadOnlyList<Cut> cuts)
    {
        var files = new List<int>();
        var renumbered = new List<Cut>();
        foreach (var c in cuts)
        {
            var index = files.IndexOf(c.Source);
            if (index < 0)
            {
                index = files.Count;
                files.Add(c.Source);
            }
            renumbered.Add(c with { Source = index });
        }
        return (files, renumbered);
    }

    /// <summary>
    /// A -filter_complex that trims each piece out of its input and concatenates them, ending in
    /// [v] and/or [a]. For pieces of one file, which already match each other.
    /// </summary>
    /// <param name="crops">Per input, a filter such as crop=... to put on its picture, or null.</param>
    public static string Filter(IReadOnlyList<Cut> cuts, bool video, bool audio, IReadOnlyList<string?>? crops = null)
    {
        Check(cuts, video, audio);
        var graph = new StringBuilder();
        for (var i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            if (video)
                graph.Append($"[{c.Source}:v:0]trim={Range(c)},setpts=PTS-STARTPTS{Then(crops, c.Source)}[v{i}];");
            if (audio)
                graph.Append($"[{c.Source}:a:0]atrim={Range(c)},asetpts=PTS-STARTPTS[a{i}];");
        }
        return Concat(graph, cuts.Count, video, audio);
    }

    /// <summary>
    /// The same for pieces of different files: every piece is scaled and padded to one size and
    /// frame rate and its sound resampled, as concat needs; a piece without a picture gets black
    /// and one without sound gets silence, so the picture and the sound stay in step.
    /// </summary>
    public static string JoinFilter(IReadOnlyList<Cut> cuts, IReadOnlyList<MediaInfo> inputs, JoinFormat format, IReadOnlyList<string?>? crops = null)
    {
        Check(cuts, format.Video, format.Audio);
        var size = $"{format.Width}x{format.Height}";
        var graph = new StringBuilder();
        for (var i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            var source = inputs[c.Source];
            if (format.Video)
            {
                graph.Append(source.Video
                    ? $"[{c.Source}:v:0]trim={Range(c)},setpts=PTS-STARTPTS{Then(crops, c.Source)},scale={format.Width}:{format.Height}:force_original_aspect_ratio=decrease,pad={format.Width}:{format.Height}:(ow-iw)/2:(oh-ih)/2:color=black,"
                    : $"color=c=black:s={size}:r={format.Fps}:d={Seconds(c.Length)},");
                graph.Append($"setsar=1,fps={format.Fps},format=yuv420p[v{i}];");
            }
            if (format.Audio)
            {
                graph.Append(source.Audio
                    ? $"[{c.Source}:a:0]atrim={Range(c)},asetpts=PTS-STARTPTS,aresample={JoinFormat.SampleRate},"
                    : $"anullsrc=r={JoinFormat.SampleRate}:cl=stereo,atrim=duration={Seconds(c.Length)},");
                graph.Append($"aformat=sample_fmts=fltp:sample_rates={JoinFormat.SampleRate}:channel_layouts=stereo[a{i}];");
            }
        }
        return Concat(graph, cuts.Count, format.Video, format.Audio);
    }

    public const int GifFps = 15, GifWidth = 960;

    /// <summary>
    /// What turns the joined picture [v] into [gif]: fewer frames, at most <see cref="GifWidth"/>
    /// wide, and a palette made from the clip itself, which keeps screen text sharp where GIF's
    /// fixed 256 colours would smear it. <paramref name="width"/> is the picture's, 0 if unknown.
    /// </summary>
    public static string GifTail(int width)
    {
        var scale = width > 0
            ? $"scale={Math.Max(2, Math.Min(width, GifWidth) / 2 * 2)}:-2:flags=lanczos"
            : $"scale='min({GifWidth},iw)':-2:flags=lanczos"; // quoted, or the comma would end the filter
        return $";[v]fps={GifFps},{scale},split[g0][g1];[g0]palettegen=stats_mode=diff[p];[g1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[gif]";
    }

    /// <summary>
    /// Whether the files can be joined by copying their streams: only when they were made the
    /// same way, the same container, codecs, size and sample rate. Otherwise concat would make a
    /// file that breaks where the second one starts.
    /// </summary>
    public static bool CanCopy(IReadOnlyList<string> files, IReadOnlyList<MediaInfo> inputs)
    {
        if (files.Count != inputs.Count || files.Count == 0)
            return false;
        var ext = MediaKinds.Extension(files[0]);
        var a = inputs[0];
        for (var i = 1; i < files.Count; i++)
        {
            var b = inputs[i];
            if (MediaKinds.Extension(files[i]) != ext || b.Video != a.Video || b.Audio != a.Audio
                || b.VideoCodec != a.VideoCodec || b.Width != a.Width || b.Height != a.Height
                || b.AudioCodec != a.AudioCodec || b.SampleRate != a.SampleRate)
                return false;
        }
        return true;
    }

    public static string ConcatList(string file, IReadOnlyList<Cut> cuts) => ConcatList([file], cuts);

    /// <summary>
    /// An ffconcat list that plays the pieces in order, for the concat demuxer with -c copy:
    /// instant, but each piece starts at the keyframe before its mark.
    /// </summary>
    public static string ConcatList(IReadOnlyList<string> files, IReadOnlyList<Cut> cuts)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (var c in cuts)
        {
            // Inside single quotes everything is literal, so a quote is closed, escaped and reopened.
            var quoted = "'" + files[c.Source].Replace("'", @"'\''") + "'";
            list.Append($"file {quoted}\ninpoint {Seconds(c.Start)}\noutpoint {Seconds(c.End)}\n");
        }
        return list.ToString();
    }

    /// <summary>The piece of file <paramref name="source"/> under <paramref name="t"/>, or -1.</summary>
    public static int IndexAt(IReadOnlyList<Cut> cuts, int source, TimeSpan t)
    {
        for (var i = 0; i < cuts.Count; i++)
            if (cuts[i].Source == source && cuts[i].Contains(t))
                return i;
        return -1;
    }

    /// <summary>
    /// Splits piece <paramref name="index"/> at <paramref name="at"/> into two that follow each
    /// other in the list. False when either half would be too short to keep.
    /// </summary>
    public static bool Split(List<Cut> cuts, int index, TimeSpan at)
    {
        if (index < 0 || index >= cuts.Count)
            return false;
        var c = cuts[index];
        if (at - c.Start < Shortest || c.End - at < Shortest)
            return false;
        cuts[index] = c with { End = at };
        cuts.Insert(index + 1, c with { Start = at });
        return true;
    }

    public static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Then(IReadOnlyList<string?>? filters, int input) =>
        filters is not null && input < filters.Count && !string.IsNullOrEmpty(filters[input]) ? "," + filters[input] : "";

    private static string Range(Cut c) => $"start={Seconds(c.Start)}:end={Seconds(c.End)}";

    private static void Check(IReadOnlyList<Cut> cuts, bool video, bool audio)
    {
        if (cuts.Count == 0)
            throw new ArgumentException("No cuts.", nameof(cuts));
        if (!video && !audio)
            throw new ArgumentException("Nothing to cut: no video and no audio.");
    }

    private static string Concat(StringBuilder graph, int count, bool video, bool audio)
    {
        for (var i = 0; i < count; i++)
        {
            if (video)
                graph.Append($"[v{i}]");
            if (audio)
                graph.Append($"[a{i}]");
        }
        graph.Append($"concat=n={count}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}");
        if (video)
            graph.Append("[v]");
        if (audio)
            graph.Append("[a]");
        return graph.ToString();
    }
}
