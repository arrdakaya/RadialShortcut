namespace RadialShortcut.Core;

/// <summary>A ring in config.json. A ring without <see cref="Match"/> is the global one.</summary>
public sealed class RingConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }

    /// <summary>"main" (default, plain right-hold) or "files" (Shift + right-hold) or "apps" (Ctrl + right-hold); each layer has its own global ring.</summary>
    public string? Layer { get; set; }
    public RingMatch? Match { get; set; }
    public List<SliceConfig> Slices { get; set; } = [];
}

/// <summary>Which windows a ring is for.</summary>
public sealed class RingMatch
{
    /// <summary>Process file name, with or without ".exe" ("chrome", "Code.exe").</summary>
    public string? Process { get; set; }

    /// <summary>Optional regular expression on the window title; a ring with one beats a process-only ring.</summary>
    public string? WindowTitle { get; set; }
}

/// <summary>A built-in tool (<see cref="Tool"/>) or a user action (<see cref="Action"/>), with optional overrides.</summary>
public sealed class SliceConfig
{
    public string? Tool { get; set; }
    public string? Label { get; set; }
    public string? Title { get; set; }
    public string? Hint { get; set; }
    public string? Icon { get; set; }
    public SliceAction? Action { get; set; }
}

/// <summary>A basic action: launch, url, hotkey or text.</summary>
public sealed class SliceAction
{
    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public string? Args { get; set; }
    public string? Url { get; set; }
    public string? Keys { get; set; }
    public string? Text { get; set; }
}
