namespace RadialShortcut.Core;

/// <summary>
/// The stretch of a file the timeline shows, from Start for Length: all of it, or less once it is
/// zoomed in, so a cut can be placed to the frame in a long recording.
/// </summary>
public readonly record struct TimelineView(TimeSpan Start, TimeSpan Length)
{
    /// <summary>Zoomed in no further than this much of the file across the whole strip.</summary>
    public static readonly TimeSpan Narrowest = TimeSpan.FromSeconds(2);

    public TimeSpan End => Start + Length;

    public static TimelineView All(TimeSpan total) => new(TimeSpan.Zero, total);

    public bool IsAll(TimeSpan total) => Start <= TimeSpan.Zero && End >= total;

    public double ToX(TimeSpan t, double width) =>
        Length > TimeSpan.Zero ? width * (t - Start).TotalSeconds / Length.TotalSeconds : 0;

    public TimeSpan ToTime(double x, double width) =>
        width > 0 ? Start + TimeSpan.FromSeconds(Length.TotalSeconds * x / width) : Start;

    /// <summary>
    /// Narrower (factor below 1) or wider, keeping <paramref name="around"/> where it is on the
    /// strip, within the file and no narrower than <see cref="Narrowest"/>.
    /// </summary>
    public TimelineView Zoomed(double factor, TimeSpan around, TimeSpan total)
    {
        if (total <= TimeSpan.Zero || Length <= TimeSpan.Zero)
            return All(total);
        var narrowest = total < Narrowest ? total : Narrowest;
        var length = TimeSpan.FromSeconds(Math.Clamp(Length.TotalSeconds * factor, narrowest.TotalSeconds, total.TotalSeconds));
        var share = Math.Clamp((around - Start).TotalSeconds / Length.TotalSeconds, 0, 1);
        return Within(around - length * share, length, total);
    }

    public TimelineView Panned(TimeSpan by, TimeSpan total) => Within(Start + by, Length, total);

    /// <summary>Scrolled just enough to have <paramref name="t"/> on the strip, a page at a time as playback runs on.</summary>
    public TimelineView Showing(TimeSpan t, TimeSpan total)
    {
        if (t >= Start && t <= End)
            return this;
        return Within(t < Start ? t - Length * 0.9 : t - Length * 0.1, Length, total);
    }

    private static TimelineView Within(TimeSpan start, TimeSpan length, TimeSpan total)
    {
        if (length >= total)
            return All(total);
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (start + length > total)
            start = total - length;
        return new TimelineView(start, length);
    }
}

/// <summary>An edge of a piece on the timeline: the start of piece Index, or its end.</summary>
public readonly record struct CutEdge(int Index, bool End);

public static class CutEdges
{
    /// <summary>
    /// The edge of a piece of file <paramref name="source"/> within <paramref name="tolerance"/>
    /// pixels of <paramref name="x"/>, the nearest when several are, or null.
    /// </summary>
    public static CutEdge? At(IReadOnlyList<Cut> cuts, int source, TimelineView view, double width, double x, double tolerance)
    {
        CutEdge? best = null;
        var bestDistance = tolerance;
        for (var i = 0; i < cuts.Count; i++)
        {
            if (cuts[i].Source != source)
                continue;
            foreach (var end in (bool[])[false, true])
            {
                var distance = Math.Abs(view.ToX(end ? cuts[i].End : cuts[i].Start, width) - x);
                if (distance <= bestDistance)
                {
                    best = new CutEdge(i, end);
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    /// <summary>The piece with that edge moved to <paramref name="to"/>, kept in the file and no shorter than <see cref="VideoCuts.Shortest"/>.</summary>
    public static Cut Move(Cut cut, bool end, TimeSpan to, TimeSpan length)
    {
        if (end)
        {
            var max = length > TimeSpan.Zero ? length : TimeSpan.MaxValue;
            return cut with { End = to < cut.Start + VideoCuts.Shortest ? cut.Start + VideoCuts.Shortest : to > max ? max : to };
        }
        var start = to < TimeSpan.Zero ? TimeSpan.Zero : to;
        return cut with { Start = start > cut.End - VideoCuts.Shortest ? cut.End - VideoCuts.Shortest : start };
    }
}
