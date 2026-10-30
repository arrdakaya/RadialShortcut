using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadialShortcut.Core;

/// <summary>
/// What the app reads from config.json: tool options, excluded processes and rings.
/// Edited by hand or through the settings window, which rewrites the whole file on save.
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

    /// <summary>Interface language: "system", "tr" or "en"; see <see cref="Loc"/>.</summary>
    public string Language { get; set; } = "system";

    /// <summary>Ring size relative to the default, 0.8–1.4.</summary>
    public double RingScale { get; set; } = 1;

    /// <summary>Ring highlight color as "#RRGGBB"; null keeps the default blue.</summary>
    public string? RingAccent { get; set; }

    /// <summary>The ring's look from <see cref="RingScale"/> and <see cref="RingAccent"/>, with bad values ignored.</summary>
    public RingStyle RingStyle()
    {
        var style = new RingStyle();
        var scale = Math.Clamp(double.IsFinite(RingScale) ? RingScale : 1, 0.8, 1.4);
        style = style with { OuterRadius = style.OuterRadius * scale, InnerRadius = style.InnerRadius * scale };
        return RingAccent is { } accent && System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9A-Fa-f]{6}$") ? style with { Accent = accent } : style;
    }

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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps "Ş" and "ı" readable in the file
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
            error = e.LineNumber is { } line ? Loc.T($"{line + 1}. satırda hata: {e.Message}", $"Error on line {line + 1}: {e.Message}") : e.Message;
            return false;
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }
    }

    public static UserSettings Parse(string json) => JsonSerializer.Deserialize<UserSettings>(json, Json) ?? new();

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>A copy to edit without touching this one.</summary>
    public UserSettings Clone() => Parse(Serialize());

    /// <summary>Writes the file; the previous one is kept as config.json.bak, since hand-written comments do not survive.</summary>
    public void Save(string file)
    {
        if (File.Exists(file))
            File.Copy(file, file + ".bak", overwrite: true);
        var temp = file + ".tmp";
        File.WriteAllText(temp, Serialize());
        File.Move(temp, file, overwrite: true);
    }
}
