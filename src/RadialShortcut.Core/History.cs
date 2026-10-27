using System.Text.Json;

namespace RadialShortcut.Core;

/// <summary>One thing a tool made: a file, or a piece of text (OCR, translation).</summary>
public sealed record HistoryEntry(DateTime Time, string Tool, string? Path = null, string? Text = null);

/// <summary>
/// The library behind "Kütüphane": one JSON object per line, appended as tools finish.
/// Appending a line is cheap and a torn last line only loses that line.
/// </summary>
public static class History
{
    private const int MaxEntries = 2000;

    public static void Add(string file, HistoryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.AppendAllText(file, JsonSerializer.Serialize(entry) + "\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Newest first. Trims the file when it has grown past <see cref="MaxEntries"/>.</summary>
    public static List<HistoryEntry> Load(string file)
    {
        string[] lines;
        try
        {
            lines = File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (IOException)
        {
            return [];
        }

        var entries = new List<HistoryEntry>();
        foreach (var line in lines)
        {
            try
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<HistoryEntry>(line) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }

        if (entries.Count > MaxEntries)
        {
            entries = entries[^MaxEntries..];
            try
            {
                File.WriteAllLines(file, entries.Select(e => JsonSerializer.Serialize(e)));
            }
            catch (IOException)
            {
            }
        }
        entries.Reverse();
        return entries;
    }

    /// <summary>Case- and accent-insensitive match on file name, tool and text.</summary>
    public static bool Matches(HistoryEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var haystack = $"{System.IO.Path.GetFileName(entry.Path)} {entry.Tool} {entry.Text}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, word,
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);
    }
}
