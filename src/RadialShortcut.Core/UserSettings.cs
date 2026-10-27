using System.Text.Json;

namespace RadialShortcut.Core;

/// <summary>
/// What the app reads from config.json: tool options, excluded processes and rings.
/// Edited by hand until the settings window exists (Faz 5); the app never writes it.
/// </summary>
public sealed class UserSettings
{
    /// <summary>DeepL API key; keys ending in ":fx" belong to the Free plan.</summary>
    public string? DeepLApiKey { get; set; }

    /// <summary>Target language for "translate", as a DeepL / Google code.</summary>
    public string TranslateTarget { get; set; } = "TR";

    /// <summary>Opens the ring at the cursor from the keyboard; hold it, point, release to pick. Shift added opens the file ring.</summary>
    public string RingHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>GitHub "owner/repo" whose releases carry updates; empty turns update checks off.</summary>
    public string? UpdateRepo { get; set; }

    /// <summary>Processes where the right button is left alone; null keeps the built-in list.</summary>
    public List<string>? ExcludedProcesses { get; set; }

    /// <summary>The global ring (no match) and app-specific rings; empty means the built-in tool ring.</summary>
    public List<RingConfig> Rings { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static UserSettings Load(string file) => TryLoad(file, out var settings, out _) ? settings : new();

    /// <summary>False with a readable reason when the file cannot be used; a missing file is fine.</summary>
    public static bool TryLoad(string file, out UserSettings settings, out string? error)
    {
        settings = new();
        error = null;
        try
        {
            if (File.Exists(file))
                settings = Parse(File.ReadAllText(file));
            return true;
        }
        catch (JsonException e)
        {
            // A hand-edited file with a typo must not stop the app.
            error = e.LineNumber is { } line ? $"{line + 1}. satırda hata: {e.Message}" : e.Message;
            return false;
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }
    }

    public static UserSettings Parse(string json) => JsonSerializer.Deserialize<UserSettings>(json, Json) ?? new();
}
