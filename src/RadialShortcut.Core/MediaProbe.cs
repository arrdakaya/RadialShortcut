using System.Globalization;
using System.Text.RegularExpressions;

namespace RadialShortcut.Core;

/// <summary>
/// What a media file holds, as far as joining it with others goes. Width and Height are as the
/// picture is shown, so a phone video recorded on its side reports the turned size.
/// </summary>
public sealed record MediaInfo(bool Video, bool Audio, int Width = 0, int Height = 0, double Fps = 0,
    string? VideoCodec = null, string? AudioCodec = null, int SampleRate = 0, TimeSpan Duration = default);

/// <summary>Reads <see cref="MediaInfo"/> out of the stream list ffmpeg prints for "ffmpeg -i file".</summary>
public static partial class MediaProbe
{
    public static MediaInfo Parse(string ffmpegOutput)
    {
        var info = new MediaInfo(false, false);
        if (DurationLine().Match(ffmpegOutput) is { Success: true } d && TimeSpan.TryParse(d.Groups[1].Value, CultureInfo.InvariantCulture, out var duration))
            info = info with { Duration = duration };
        var lines = ffmpegOutput.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("Stream #"))
                continue;
            if (!info.Video && line.Contains(": Video:") && !line.Contains("(attached pic)"))
            {
                var size = Size().Match(line);
                int w = size.Success ? int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                int h = size.Success ? int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                // The rotation is in the side data under the stream, before the next stream starts.
                for (var j = i + 1; j < lines.Length && !lines[j].Contains("Stream #"); j++)
                {
                    if (Rotation().Match(lines[j]) is { Success: true } r
                        && double.TryParse(r.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees)
                        && Math.Abs(Math.Round(degrees)) % 180 == 90)
                        (w, h) = (h, w);
                }
                var fps = Fps().Match(line);
                info = info with
                {
                    Video = true,
                    Width = w,
                    Height = h,
                    Fps = fps.Success && double.TryParse(fps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0,
                    VideoCodec = Codec("Video").Match(line).Groups[1].Value,
                };
            }
            else if (!info.Audio && line.Contains(": Audio:"))
            {
                var rate = Rate().Match(line);
                info = info with
                {
                    Audio = true,
                    AudioCodec = Codec("Audio").Match(line).Groups[1].Value,
                    SampleRate = rate.Success ? int.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                };
            }
        }
        return info;
    }

    private static Regex Codec(string kind) => kind == "Video" ? VideoCodec() : AudioCodec();

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+\.\d+)")]
    private static partial Regex DurationLine();

    [GeneratedRegex(@"[ ,](\d{2,5})x(\d{2,5})[ ,\[]")]
    private static partial Regex Size();

    [GeneratedRegex(@"([\d.]+) fps")]
    private static partial Regex Fps();

    [GeneratedRegex(@"(\d+) Hz")]
    private static partial Regex Rate();

    [GeneratedRegex(@"rotation of (-?[\d.]+) degrees")]
    private static partial Regex Rotation();

    [GeneratedRegex(@": Video: (\w+)")]
    private static partial Regex VideoCodec();

    [GeneratedRegex(@": Audio: (\w+)")]
    private static partial Regex AudioCodec();
}
