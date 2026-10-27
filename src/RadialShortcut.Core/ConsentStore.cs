using System.Text.Json;

namespace RadialShortcut.Core;

/// <summary>Ids of the tools the user has allowed to send data over the internet.</summary>
public static class ConsentStore
{
    public static HashSet<string> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (JsonException)
        {
            return []; // Asking again is the safe outcome.
        }
    }

    public static void Save(string file, IEnumerable<string> tools)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(tools.Order()));
    }
}
