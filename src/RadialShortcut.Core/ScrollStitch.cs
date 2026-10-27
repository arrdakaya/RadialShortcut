namespace RadialShortcut.Core;

/// <summary>How a new frame lines up with the previous one while a page scrolls.</summary>
/// <param name="Shift">Rows the content moved up; 0 when nothing moved.</param>
/// <param name="Top">Rows at the top that stayed put (a sticky header).</param>
/// <param name="Bottom">Rows at the bottom that stayed put (a sticky footer).</param>
public readonly record struct ScrollMatch(int Shift, int Top, int Bottom);

/// <summary>Pure alignment logic for the scrolling screenshot. Frames are given as one hash per pixel row.</summary>
public static class ScrollStitch
{
    /// <summary>Share of compared rows that must agree for a shift to count.</summary>
    public const double MinAgreement = 0.9;

    /// <summary>Lower bar for row profiles: hover effects and playing previews change part of the frame.</summary>
    public const double MinProfileAgreement = 0.7;

    /// <summary>Largest difference in a profile bucket (0–255) that still counts as the same row.</summary>
    public const int ProfileTolerance = 10;

    /// <summary>Columns averaged into one profile bucket value.</summary>
    public const int ProfileBuckets = 32;

    /// <summary>How far the content scrolled between two frames of the same height, or null if it cannot tell.</summary>
    public static ScrollMatch? Match(IReadOnlyList<ulong> previous, IReadOnlyList<ulong> current) =>
        Match(previous, current, (a, b) => a == b, MinAgreement);

    /// <summary>
    /// The tolerant version for real screens: rows are compared by their profiles, so slightly different
    /// rendering (smooth scrolling, scaling, font smoothing) still lines up.
    /// </summary>
    public static ScrollMatch? Match(IReadOnlyList<byte[]> previous, IReadOnlyList<byte[]> current) =>
        Match(previous, current, SameProfile, MinProfileAgreement);

    private static ScrollMatch? Match<T>(IReadOnlyList<T> previous, IReadOnlyList<T> current, Func<T, T, bool> same, double minAgreement)
    {
        var h = previous.Count;
        if (h != current.Count || h < 8)
            return null;

        var top = 0;
        while (top < h && same(previous[top], current[top]))
            top++;
        if (top == h)
            return new ScrollMatch(0, 0, 0); // Identical: the page did not move.

        var bottom = 0;
        while (bottom < h - top && same(previous[h - 1 - bottom], current[h - 1 - bottom]))
            bottom++;

        // Long equal runs at the edges are plain background that happened to match, not sticky bars;
        // treating them as fixed would cut real content.
        if (top > h / 3) top = 0;
        if (bottom > h / 3) bottom = 0;

        int bodyStart = top, bodyEnd = h - bottom, body = bodyEnd - bodyStart;
        // A shift must agree on enough of its overlap, and among those the one with the most agreeing rows
        // wins: judged by share alone, a huge shift with a few overlapping rows would score best.
        var best = (Shift: 0, Agree: 0);
        for (var shift = 1; shift < body - 8; shift++)
        {
            int compared = body - shift, agree = 0, informative = 0;
            // Give up on this shift as soon as it cannot reach the bar any more.
            var allowedMisses = (int)(compared * (1 - minAgreement));
            for (int i = bodyStart, misses = 0; i < bodyEnd - shift; i++)
            {
                if (!same(current[i], previous[i + shift]))
                {
                    if (++misses > allowedMisses)
                        break;
                    continue;
                }
                agree++;
                // Rows equal to their neighbour (flat background) match at any shift and prove nothing.
                if (i == bodyStart || !same(current[i], current[i - 1]))
                    informative++;
            }

            var score = (double)agree / compared;
            if (score >= minAgreement && informative > 1 && agree > best.Agree)
                best = (shift, agree);
        }

        return best.Shift == 0 ? null : new ScrollMatch(best.Shift, top, bottom);
    }

    private static bool SameProfile(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > ProfileTolerance)
                return false;
        return true;
    }

    /// <summary>
    /// Each row of a 32-bit BGRA image as <see cref="ProfileBuckets"/> average brightness values, leaving
    /// out the rightmost <paramref name="skipRight"/> columns (see <see cref="RowHashes"/>).
    /// </summary>
    public static byte[][] RowProfiles(ReadOnlySpan<byte> pixels, int width, int height, int stride, int skipRight = 0)
    {
        width = Math.Max(1, width - skipRight);
        var buckets = Math.Min(ProfileBuckets, width);
        var rows = new byte[height][];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            var profile = new byte[buckets];
            for (var k = 0; k < buckets; k++)
            {
                int from = k * width / buckets, to = (k + 1) * width / buckets, sum = 0;
                for (var x = from; x < to; x++)
                    sum += (row[x * 4] + 2 * row[x * 4 + 1] + row[x * 4 + 2]) >> 2;
                profile[k] = (byte)(sum / Math.Max(1, to - from));
            }
            rows[y] = profile;
        }
        return rows;
    }

    /// <summary>
    /// A 64-bit FNV-1a hash of each row of a 32-bit image. The rightmost <paramref name="skipRight"/> columns
    /// are left out: a scrollbar there changes on every step (overlay scrollbars even appear while scrolling)
    /// and would make every row differ.
    /// </summary>
    public static ulong[] RowHashes(ReadOnlySpan<byte> pixels, int width, int height, int stride, int skipRight = 0)
    {
        width = Math.Max(1, width - skipRight);
        var hashes = new ulong[height];
        for (var y = 0; y < height; y++)
        {
            var h = 14695981039346656037UL;
            foreach (var b in pixels.Slice(y * stride, width * 4))
                h = (h ^ b) * 1099511628211UL;
            hashes[y] = h;
        }
        return hashes;
    }
}
