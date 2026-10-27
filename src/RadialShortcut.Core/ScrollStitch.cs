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

    /// <summary>How far the content scrolled between two frames of the same height, or null if it cannot tell.</summary>
    public static ScrollMatch? Match(IReadOnlyList<ulong> previous, IReadOnlyList<ulong> current)
    {
        var h = previous.Count;
        if (h != current.Count || h < 8)
            return null;

        var top = 0;
        while (top < h && previous[top] == current[top])
            top++;
        if (top == h)
            return new ScrollMatch(0, 0, 0); // Identical: the page did not move, the end was reached.

        var bottom = 0;
        while (bottom < h - top && previous[h - 1 - bottom] == current[h - 1 - bottom])
            bottom++;

        // Long equal runs at the edges are plain background that happened to match, not sticky bars;
        // treating them as fixed would cut real content.
        if (top > h / 3) top = 0;
        if (bottom > h / 3) bottom = 0;

        int bodyStart = top, bodyEnd = h - bottom, body = bodyEnd - bodyStart;
        var best = (Shift: 0, Score: 0.0);
        for (var shift = 1; shift < body - 4; shift++)
        {
            int compared = body - shift, agree = 0, informative = 0;
            for (var i = bodyStart; i < bodyEnd - shift; i++)
            {
                if (current[i] != previous[i + shift])
                    continue;
                agree++;
                // Rows equal to their neighbour (flat background) match at any shift and prove nothing.
                if (i == bodyStart || current[i] != current[i - 1])
                    informative++;
            }

            var score = (double)agree / compared;
            if (score >= MinAgreement && informative > 1 && score > best.Score)
                best = (shift, score);
        }

        return best.Shift == 0 ? null : new ScrollMatch(best.Shift, top, bottom);
    }

    /// <summary>A 64-bit FNV-1a hash of each row of a 32-bit image.</summary>
    public static ulong[] RowHashes(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
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
