using System.Text.RegularExpressions;

namespace RadialShortcut.Core;

/// <summary>A ring ready to draw.</summary>
public sealed record Ring(string Id, IReadOnlyList<RingItem> Items);

/// <summary>
/// The rings from config.json, checked and turned into slices, plus the choice of ring for a window:
/// process + title, then process, then the global ring (PLAN.md, section 8).
/// </summary>
public sealed class RingCatalog
{
    public const int MinSlices = 2, MaxSlices = 12;

    private readonly List<(Ring Ring, string Layer, string Process, Regex? Title)> _specific = [];
    private readonly Dictionary<string, Ring> _globals = [];

    /// <summary>The global ring of the main layer.</summary>
    public Ring Global => _globals[RingLayers.Main];

    public Ring GlobalFor(string layer) => _globals[layer];

    public IEnumerable<Ring> All => _globals.Values.Concat(_specific.Select(s => s.Ring));

    /// <summary>What was skipped and why, in Turkish, for the tray notice.</summary>
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
                warnings.Add($"{name}: her halkanın farklı bir id'si olmalı, halka atlandı.");
                continue;
            }
            var items = config.Slices.Select(s => ToItem(s, name, warnings)).OfType<RingItem>().Take(MaxSlices).ToList();
            if (config.Slices.Count > MaxSlices)
                warnings.Add($"{name}: en fazla {MaxSlices} dilim olabilir, fazlası atlandı.");
            if (items.Count < MinSlices)
            {
                warnings.Add($"{name}: en az {MinSlices} geçerli dilim gerekli, halka atlandı.");
                continue;
            }

            var layer = config.Layer?.Trim().ToLowerInvariant() ?? RingLayers.Main;
            if (!RingLayers.All.Contains(layer))
            {
                warnings.Add($"{name}: \"{config.Layer}\" diye bir katman yok (main veya files), halka atlandı.");
                continue;
            }

            var ring = new Ring(config.Id, items);
            if (config.Match is null)
            {
                if (!_globals.TryAdd(layer, ring))
                    warnings.Add($"{name}: {layer} katmanında eşleşmesi olmayan ikinci halka atlandı (genel halka zaten var).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(config.Match.Process))
            {
                warnings.Add($"{name}: match.process boş, halka atlandı.");
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
                    warnings.Add($"{name}: windowTitle geçerli bir düzenli ifade değil, halka atlandı.");
                    continue;
                }
            }
            _specific.Add((ring, layer, ProcessName(config.Match.Process), title));
        }

        // Ids from the file are never empty or start with a control character, so these cannot clash.
        _globals.TryAdd(RingLayers.Main, new Ring("", DefaultRing.For(defaultSliceCount)));
        _globals.TryAdd(RingLayers.Files, new Ring("files", DefaultFileRing.Items));
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

    private static RingItem? ToItem(SliceConfig slice, string ring, List<string> warnings)
    {
        if (slice.Tool is { } toolId)
        {
            var tool = DefaultRing.Items.Concat(DefaultFileRing.Items).FirstOrDefault(t => t.Id == toolId);
            if (tool is null)
            {
                warnings.Add($"{ring}: \"{toolId}\" diye bir araç yok, dilim atlandı.");
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
            warnings.Add($"{ring}: \"{slice.Label}\" diliminde ne tool ne action var, dilim atlandı.");
            return null;
        }

        var label = slice.Label ?? "";
        string? problem = action.Type switch
        {
            "launch" when string.IsNullOrWhiteSpace(action.Path) => "launch için path gerekli",
            "url" when string.IsNullOrWhiteSpace(action.Url) => "url için url gerekli",
            "hotkey" when !KeyChord.TryParse(action.Keys, out _) => $"\"{action.Keys}\" tanınan bir kısayol değil",
            "text" when string.IsNullOrEmpty(action.Text) => "text için text gerekli",
            "launch" or "url" or "hotkey" or "text" => null,
            _ => $"bilinmeyen aksiyon türü \"{action.Type}\"",
        };
        if (problem is null && label.Length == 0)
            problem = "label gerekli";
        if (problem is not null)
        {
            warnings.Add($"{ring}: \"{label}\" dilimi atlandı ({problem}).");
            return null;
        }

        var hint = slice.Hint ?? action.Type switch
        {
            "launch" => Path.GetFileName(action.Path!),
            "url" => action.Url!,
            "hotkey" => action.Keys!,
            _ => "Metni yapıştır",
        };
        return new RingItem("action", label, slice.Title ?? label, hint, slice.Icon ?? ActionIcons[action.Type], action);
    }

    private static readonly Dictionary<string, string> ActionIcons = new()
    {
        ["launch"] = "M5 5h14v14H5z M10 9h5v5 M15 9l-6 6",
        ["url"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M3 12h18 M12 3c-3 3-3 15 0 18 M12 3c3 3 3 15 0 18",
        ["hotkey"] = "M3 7h18v10H3z M6 10h1 M9 10h1 M12 10h1 M15 10h1 M18 10h1 M8 14h8",
        ["text"] = "M5 6h14 M5 10h14 M5 14h9 M5 18h6",
    };
}
