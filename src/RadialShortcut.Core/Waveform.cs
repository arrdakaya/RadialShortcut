namespace RadialShortcut.Core;

/// <summary>
/// A file's loudness for the timeline: ffmpeg turns the sound into mono 16-bit samples at a low
/// rate, and each <see cref="Step"/> of them becomes one peak, 0 (silence) to 1 (full scale).
/// </summary>
public static class Waveform
{
    /// <summary>The sample rate ffmpeg is asked for: plenty to see speech and silence, small for an hour.</summary>
    public const int SampleRate = 2000;

    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    public static int SamplesPerPeak => (int)(SampleRate * Step.TotalSeconds);

    /// <summary>The peaks of raw little-endian 16-bit samples; a trailing odd byte is ignored.</summary>
    public static float[] Peaks(ReadOnlySpan<byte> samples)
    {
        var count = samples.Length / 2;
        var per = SamplesPerPeak;
        var peaks = new float[(count + per - 1) / per];
        for (var i = 0; i < count; i++)
        {
            var v = Math.Abs((int)(short)(samples[2 * i] | samples[2 * i + 1] << 8)) / 32768f;
            if (v > peaks[i / per])
                peaks[i / per] = v;
        }
        return peaks;
    }

    /// <summary>The loudest peak from index <paramref name="from"/> up to <paramref name="to"/> (fractions allowed); 0 outside the sound.</summary>
    public static float Max(float[] peaks, double from, double to)
    {
        var first = Math.Max(0, (int)Math.Floor(from));
        var last = Math.Min(peaks.Length - 1, Math.Max(first, (int)Math.Ceiling(to) - 1));
        var max = 0f;
        for (var i = first; i <= last; i++)
            max = Math.Max(max, peaks[i]);
        return max;
    }
}
