using System.Globalization;

namespace RadialShortcut.Core;

/// <summary>
/// The interface language. Every visible string is written as a Turkish/English pair at the call site,
/// so nothing can fall out of sync with a key table. Set once at startup; a change needs a restart.
/// </summary>
public static class Loc
{
    public static readonly string[] Languages = ["system", "tr", "en"];

    public static bool English { get; private set; }

    /// <summary>"tr", "en", or "system"/null for the Windows display language (Turkish only on a Turkish Windows).</summary>
    public static void Set(string? language) => English = Resolve(language) == "en";

    public static string Resolve(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "tr" => "tr",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en",
    };

    public static string T(string tr, string en) => English ? en : tr;
}
