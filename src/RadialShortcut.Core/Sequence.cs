namespace RadialShortcut.Core;

/// <summary>
/// The result as one strip: the pieces end to end in the order they are joined. "Result time" is
/// a moment of the saved clip; these map it to a piece and a moment of that piece's file, and back.
/// </summary>
public static class Sequence
{
    /// <summary>Where each piece starts in the result.</summary>
    public static TimeSpan[] Starts(IReadOnlyList<Cut> cuts)
    {
        var starts = new TimeSpan[cuts.Count];
        var at = TimeSpan.Zero;
        for (var i = 0; i < cuts.Count; i++)
        {
            starts[i] = at;
            at += cuts[i].Length;
        }
        return starts;
    }

    /// <summary>
    /// The piece playing at result time <paramref name="t"/> and the moment of its file there.
    /// Before the start that is the first piece's start, at or past the end the last piece's end;
    /// on the seam between two pieces it is the later one. (-1, 0) without pieces.
    /// </summary>
    public static (int Index, TimeSpan At) Locate(IReadOnlyList<Cut> cuts, TimeSpan t)
    {
        if (cuts.Count == 0)
            return (-1, TimeSpan.Zero);
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        var start = TimeSpan.Zero;
        for (var i = 0; i < cuts.Count; i++)
        {
            var end = start + cuts[i].Length;
            if (t < end)
                return (i, cuts[i].Start + (t - start));
            start = end;
        }
        return (cuts.Count - 1, cuts[^1].End);
    }

    /// <summary>
    /// As <see cref="Locate"/>, but on the seam between two pieces it is the earlier one, at its end:
    /// the piece whose end a mark at <paramref name="t"/> would move.
    /// </summary>
    public static (int Index, TimeSpan At) LocateEnd(IReadOnlyList<Cut> cuts, TimeSpan t)
    {
        if (cuts.Count == 0)
            return (-1, TimeSpan.Zero);
        if (t <= TimeSpan.Zero)
            return (0, cuts[0].Start);
        var start = TimeSpan.Zero;
        for (var i = 0; i < cuts.Count; i++)
        {
            var end = start + cuts[i].Length;
            if (t <= end)
                return (i, cuts[i].Start + (t - start));
            start = end;
        }
        return (cuts.Count - 1, cuts[^1].End);
    }

    /// <summary>Result time of moment <paramref name="at"/> of piece <paramref name="index"/>'s file, kept inside the piece.</summary>
    public static TimeSpan ToResult(IReadOnlyList<Cut> cuts, int index, TimeSpan at)
    {
        if (index < 0 || index >= cuts.Count)
            return TimeSpan.Zero;
        var c = cuts[index];
        var within = at < c.Start ? TimeSpan.Zero : at > c.End ? c.Length : at - c.Start;
        var start = TimeSpan.Zero;
        for (var i = 0; i < index; i++)
            start += cuts[i].Length;
        return start + within;
    }

    /// <summary>The piece under result time <paramref name="t"/>, or -1 outside them all.</summary>
    public static int IndexAt(IReadOnlyList<Cut> cuts, TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            return -1;
        var start = TimeSpan.Zero;
        for (var i = 0; i < cuts.Count; i++)
        {
            start += cuts[i].Length;
            if (t < start)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Where a piece dropped at result time <paramref name="t"/> goes, 0 to Count: before the piece
    /// under it when it is in that piece's first half, after it in the second.
    /// </summary>
    public static int DropSlot(IReadOnlyList<Cut> cuts, TimeSpan t)
    {
        var start = TimeSpan.Zero;
        for (var i = 0; i < cuts.Count; i++)
        {
            if (t < start + cuts[i].Length / 2)
                return i;
            start += cuts[i].Length;
        }
        return cuts.Count;
    }

    /// <summary>
    /// Moves piece <paramref name="from"/> to drop slot <paramref name="slot"/> (as <see cref="DropSlot"/>
    /// gives it, counted before the move) and returns where it is now, or -1 when nothing moved.
    /// </summary>
    public static int Move(List<Cut> cuts, int from, int slot)
    {
        if (from < 0 || from >= cuts.Count || slot < 0 || slot > cuts.Count)
            return -1;
        var to = slot > from ? slot - 1 : slot; // taking it out shifts the later slots down by one
        if (to == from)
            return -1;
        var c = cuts[from];
        cuts.RemoveAt(from);
        cuts.Insert(to, c);
        return to;
    }

    /// <summary>
    /// The piece edge within <paramref name="tolerance"/> pixels of <paramref name="x"/> on the result
    /// strip, laid out from <paramref name="starts"/>. On a seam the side the mouse is on decides:
    /// left of it is the end of the earlier piece, right of it the start of the later one.
    /// </summary>
    public static CutEdge? EdgeAt(IReadOnlyList<Cut> cuts, IReadOnlyList<TimeSpan> starts, TimelineView view, double width, double x, double tolerance)
    {
        CutEdge? best = null;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < cuts.Count; i++)
        {
            var left = view.ToX(starts[i], width);
            var right = view.ToX(starts[i] + cuts[i].Length, width);
            foreach (var end in (bool[])[false, true])
            {
                var distance = Math.Abs(x - (end ? right : left));
                if (distance > tolerance)
                    continue;
                if (end ? x > right && i < cuts.Count - 1 : x < left && i > 0)
                    continue; // that side of the seam belongs to the neighbour's edge
                // A piece too narrow to hold both grips gives the nearer one.
                if (distance < bestDistance)
                {
                    best = new CutEdge(i, end);
                    bestDistance = distance;
                }
            }
        }
        return best;
    }
}

/// <summary>The frames under the timeline: one ffmpeg pass takes a frame every <see cref="Step"/>.</summary>
public static class FilmStrip
{
    /// <summary>At most this many frames per file, so an hour's recording stays a few megabytes.</summary>
    public const int Most = 240;

    /// <summary>Half a second apart, or wider so a long file has no more than <see cref="Most"/>.</summary>
    public static TimeSpan Step(TimeSpan length) =>
        TimeSpan.FromSeconds(Math.Max(0.5, length.TotalSeconds / Most));

    /// <summary>The index in ascending <paramref name="times"/> nearest to <paramref name="t"/>, or -1 when empty.</summary>
    public static int Nearest(IReadOnlyList<TimeSpan> times, TimeSpan t)
    {
        if (times.Count == 0)
            return -1;
        int lo = 0, hi = times.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] < t)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo > 0 && t - times[lo - 1] <= times[lo] - t ? lo - 1 : lo;
    }
}
