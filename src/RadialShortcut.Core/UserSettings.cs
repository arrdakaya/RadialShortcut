using System.Text.Json;

namespace RadialShortcut.Core;

/// <summary>
/// The part of config.json the tools need before the settings window exists (Faz 5).
/// Edited by hand for now.
/// </summary>
public sealed class UserSettings
{
    /// <summary>DeepL API key; keys ending in ":fx" belong to the Free plan.</summary>
    public string? DeepLApiKey { get; set; }

    /// <summary>Target language for "translate", as a DeepL / Google code.</summary>
    public string TranslateTarget { get; set; } = "TR";

    /// <summary>Tool ids the user has allowed to send data over the internet.</summary>
    public List<string> InternetConsent { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static UserSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new(); // A hand-edited file with a typo must not stop the app.
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}
