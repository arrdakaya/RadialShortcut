using System.Globalization;

namespace RadialShortcut.Core;

/// <summary>"1-3, 5, 8-" style page lists, numbered from 1 as people count pages.</summary>
public static class PageRange
{
    /// <summary>
    /// The pages, in order and each once, that exist in a document of <paramref name="count"/> pages.
    /// "8-" runs to the end, "-3" from the start. Null when the text can't be read; empty when it names
    /// no page that exists.
    /// </summary>
    public static List<int>? Parse(string text, int count)
    {
        // No PDF has this many pages; the cap keeps "1-" against an unknown count from listing billions.
        count = Math.Min(count, 100_000);
        var pages = new SortedSet<int>();
        // "4 – 6" is one range: the spaces around a dash go before splitting on spaces.
        text = System.Text.RegularExpressions.Regex.Replace(text.Replace('–', '-'), @"\s*-\s*", "-");
        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return null;
        foreach (var part in parts)
        {
            var dash = part.IndexOfAny(['-', '–']);
            int from, to;
            if (dash < 0)
            {
                if (!Number(part, out from))
                    return null;
                to = from;
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                from = 1;
                to = count;
                if (left.Length > 0 && !Number(left, out from) || right.Length > 0 && !Number(right, out to))
                    return null;
                if (from > to)
                    (from, to) = (to, from);
            }
            for (var p = Math.Max(1, from); p <= Math.Min(count, to); p++)
                pages.Add(p);
        }
        return [.. pages];
    }

    /// <summary>The shortest text for the pages: "1-3, 5".</summary>
    public static string Format(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count;)
        {
            var j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1)
                j++;
            parts.Add(j == i ? $"{pages[i]}" : $"{pages[i]}-{pages[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static bool Number(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
