using System.Text.RegularExpressions;
using static RadialShortcut.Core.Loc;

namespace RadialShortcut.Core;

/// <summary>A ring ready to draw.</summary>
public sealed record Ring(string Id, IReadOnlyList<RingItem> Items);

/// <summary>
/// The rings from config.json, checked and turned into slices, plus the choice of ring for a window:
/// process + title, then process, then the global ring.
/// </summary>
public sealed class RingCatalog
{
    public const int MinSlices = 2, MaxSlices = 12;

    private readonly List<(Ring Ring, string Layer, string Process, Regex? Title)> _specific = [];
    private readonly Dictionary<string, Ring> _globals = [];

    /// <summary>The global ring of the main layer.</summary>
    public Ring Global => _globals[RingLayers.Main];

    public Ring GlobalFor(string layer) => _globals[layer];

    public IEnumerable<Ring> Globals => _globals.Values;

    public IEnumerable<Ring> All => _globals.Values.Concat(_specific.Select(s => s.Ring));

    /// <summary>What was skipped and why, in the interface language, for the tray notice.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public RingCatalog(IEnumerable<RingConfig>? rings, int defaultSliceCount)
    {
        var warnings = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in rings ?? [])
        {
            var name = config.Name ?? config.Id;
            if (string.IsNullOrWhiteSpace(config.Id) || !ids.Add(config.Id))
            {
                warnings.Add(T($"{name}: her halkanın farklı bir id'si olmalı, halka atlandı.", $"{name}: every ring needs its own id, ring skipped."));
                continue;
            }
            var items = config.Slices.Select(s => ToItem(s, name, warnings)).OfType<RingItem>().Take(MaxSlices).ToList();
            if (config.Slices.Count > MaxSlices)
                warnings.Add(T($"{name}: en fazla {MaxSlices} dilim olabilir, fazlası atlandı.", $"{name}: at most {MaxSlices} slices, the rest skipped."));
            if (items.Count < MinSlices)
            {
                warnings.Add(T($"{name}: en az {MinSlices} geçerli dilim gerekli, halka atlandı.", $"{name}: needs at least {MinSlices} valid slices, ring skipped."));
                continue;
            }

            var layer = config.Layer?.Trim().ToLowerInvariant() ?? RingLayers.Main;
            if (!RingLayers.All.Contains(layer))
            {
                warnings.Add(T($"{name}: \"{config.Layer}\" diye bir katman yok (main, files veya apps), halka atlandı.", $"{name}: there is no layer \"{config.Layer}\" (main, files or apps), ring skipped."));
                continue;
            }

            var ring = new Ring(config.Id, items);
            if (config.Match is null)
            {
                if (!_globals.TryAdd(layer, ring))
                    warnings.Add(T($"{name}: {layer} katmanında eşleşmesi olmayan ikinci halka atlandı (genel halka zaten var).", $"{name}: a second ring without a match on the {layer} layer was skipped (the global ring already exists)."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(config.Match.Process))
            {
                warnings.Add(T($"{name}: match.process boş, halka atlandı.", $"{name}: match.process is empty, ring skipped."));
                continue;
            }

            Regex? title = null;
            if (!string.IsNullOrEmpty(config.Match.WindowTitle))
            {
                try
                {
                    title = new Regex(config.Match.WindowTitle, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    warnings.Add(T($"{name}: windowTitle geçerli bir düzenli ifade değil, halka atlandı.", $"{name}: windowTitle is not a valid regular expression, ring skipped."));
                    continue;
                }
            }
            _specific.Add((ring, layer, ProcessName(config.Match.Process), title));
        }

        // Ids from the file are never empty or start with a control character, so these cannot clash.
        _globals.TryAdd(RingLayers.Main, new Ring("", DefaultRing.For(defaultSliceCount)));
        _globals.TryAdd(RingLayers.Files, new Ring("files", DefaultFileRing.Items));
        _globals.TryAdd(RingLayers.Apps, new Ring("\u0001apps", DefaultAppRing.Slices().Select(s => ToItem(s, "", [])!).ToList()));
        Warnings = warnings;
    }

    public Ring For(string process, string windowTitle, string layer = RingLayers.Main)
    {
        process = ProcessName(process);
        Ring? byProcess = null;
        foreach (var (ring, l, p, title) in _specific)
        {
            if (l != layer || !string.Equals(p, process, StringComparison.OrdinalIgnoreCase))
                continue;
            if (title is null)
                byProcess ??= ring;
            else if (Matches(title, windowTitle))
                return ring;
        }
        return byProcess ?? _globals[layer];
    }

    /// <summary>"chrome", "Chrome.exe" and "C:\...\chrome.exe" all become "chrome.exe"-style names.</summary>
    public static string ProcessName(string name)
    {
        name = Path.GetFileName(name.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private static bool Matches(Regex title, string text)
    {
        try
        {
            return title.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The slice as the ring draws it, or null and why not (for the settings preview).</summary>
    public static RingItem? ItemFor(SliceConfig slice, out string? problem)
    {
        var warnings = new List<string>();
        var item = ToItem(slice, "", warnings);
        problem = warnings.FirstOrDefault()?.TrimStart(':', ' ');
        return item;
    }

    private static RingItem? ToItem(SliceConfig slice, string ring, List<string> warnings)
    {
        if (slice.Tool is { } toolId)
        {
            var tool = DefaultRing.Items.Concat(DefaultFileRing.Items).FirstOrDefault(t => t.Id == toolId);
            if (tool is null)
            {
                warnings.Add(T($"{ring}: \"{toolId}\" diye bir araç yok, dilim atlandı.", $"{ring}: there is no tool \"{toolId}\", slice skipped."));
                return null;
            }
            return tool with
            {
                Label = slice.Label ?? tool.Label,
                Title = slice.Title ?? slice.Label ?? tool.Title,
                Hint = slice.Hint ?? tool.Hint,
                Icon = slice.Icon ?? tool.Icon,
            };
        }

        if (slice.Action is not { } action)
        {
            warnings.Add(T($"{ring}: \"{slice.Label}\" diliminde ne tool ne action var, dilim atlandı.", $"{ring}: slice \"{slice.Label}\" has neither a tool nor an action, slice skipped."));
            return null;
        }

        var label = slice.Label ?? "";
        string? problem = action.Type switch
        {
            "launch" when string.IsNullOrWhiteSpace(action.Path) => T("launch için path gerekli", "launch needs a path"),
            "url" when string.IsNullOrWhiteSpace(action.Url) => T("url için url gerekli", "url needs a url"),
            "hotkey" when !KeyChord.TryParse(action.Keys, out _) => T($"\"{action.Keys}\" tanınan bir kısayol değil", $"\"{action.Keys}\" is not a known shortcut"),
            "text" when string.IsNullOrEmpty(action.Text) => T("text için text gerekli", "text needs a text"),
            "launch" or "url" or "hotkey" or "text" => null,
            _ => T($"bilinmeyen aksiyon türü \"{action.Type}\"", $"unknown action type \"{action.Type}\""),
        };
        if (problem is null && label.Length == 0)
            problem = T("label gerekli", "label is required");
        if (problem is not null)
        {
            warnings.Add(T($"{ring}: \"{label}\" dilimi atlandı ({problem}).", $"{ring}: slice \"{label}\" skipped ({problem})."));
            return null;
        }

        var hint = slice.Hint ?? action.Type switch
        {
            // Apps picked from the Start menu list launch by an app id, not a file name worth showing.
            "launch" when action.Path!.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) => T("Uygulama", "App"),
            "launch" => Path.GetFileName(action.Path!),
            "url" => action.Url!,
            "hotkey" => action.Keys!,
            _ => T("Metni yapıştır", "Paste text"),
        };
        return new RingItem("action", label, slice.Title ?? label, hint, slice.Icon ?? ActionIcons[action.Type], action);
    }

    /// <summary>The default launch icon; the ring swaps it for the launched app's own icon.</summary>
    public const string LaunchIcon = "M5 5h14v14H5z M10 9h5v5 M15 9l-6 6";

    private static readonly Dictionary<string, string> ActionIcons = new()
    {
        ["launch"] = LaunchIcon,
        ["url"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M3 12h18 M12 3c-3 3-3 15 0 18 M12 3c3 3 3 15 0 18",
        ["hotkey"] = "M3 7h18v10H3z M6 10h1 M9 10h1 M12 10h1 M15 10h1 M18 10h1 M8 14h8",
        ["text"] = "M5 6h14 M5 10h14 M5 14h9 M5 18h6",
    };
}
