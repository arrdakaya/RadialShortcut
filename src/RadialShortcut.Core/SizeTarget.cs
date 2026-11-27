using System.Globalization;

namespace RadialShortcut.Core;

/// <summary>The sums behind "at most 2 MB": reading the size people type and the bitrates that fit it.</summary>
public static class SizeTarget
{
    /// <summary>"2", "2,5", "0.5" or "800 KB" in megabytes unless KB is said; null when unreadable or not positive.</summary>
    public static long? ParseBytes(string text)
    {
        var s = text.Trim().ToLowerInvariant().Replace(" ", "");
        var factor = 1024.0 * 1024;
        if (s.EndsWith("kb"))
        {
            factor = 1024;
            s = s[..^2];
        }
        else if (s.EndsWith("mb"))
            s = s[..^2];
        else if (s.EndsWith("gb"))
        {
            factor = 1024.0 * 1024 * 1024;
            s = s[..^2];
        }
        if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0 || double.IsInfinity(value))
            return null;
        return (long)(value * factor);
    }

    /// <summary>
    /// Video and audio bitrates in kbit/s that fill the target over the length, leaving room for the
    /// container. Null when even a barely watchable picture would not fit.
    /// </summary>
    public static (int VideoKbps, int AudioKbps)? Bitrates(long bytes, double seconds, bool audio)
    {
        if (seconds <= 0)
            return null;
        var total = bytes * 8 / 1000.0 / seconds * 0.94;
        var a = audio ? (int)Math.Clamp(total * 0.12, 32, 128) : 0;
        var v = (int)(total - a);
        return v < 60 ? null : (v, a);
    }

    /// <summary>The smallest size (bytes) for which <see cref="Bitrates"/> gives an answer.</summary>
    public static long Smallest(double seconds, bool audio) => (long)Math.Ceiling((60 + (audio ? 32 : 0)) * 1000 / 8.0 * seconds / 0.94);

    /// <summary>
    /// The tallest height, among the picture's own and the usual steps down, at which the bitrate still
    /// gives each pixel enough bits; the smallest step when none does.
    /// </summary>
    public static int Height(int width, int height, double fps, int videoKbps)
    {
        if (width <= 0 || height <= 0)
            return 0;
        fps = fps > 0 ? Math.Min(fps, 60) : 30;
        int[] steps = [height, 1080, 720, 540, 480, 360];
        foreach (var h in steps.Where(h => h <= height).Distinct().OrderDescending())
        {
            var w = width * (double)h / height;
            if (w * h * fps * 0.04 <= videoKbps * 1000)
                return h;
        }
        return Math.Min(height, 360);
    }
}
