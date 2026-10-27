namespace RadialShortcut.Core;

/// <summary>
/// Parses shortcuts like "Ctrl+Shift+T", "Win+V" or "Alt+F4" into virtual-key codes: modifiers first,
/// in the order written, then the key. Media keys ("PlayPause", "VolumeUp") work on their own.
/// </summary>
public static class KeyChord
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12, ["Win"] = 0x5B,
        ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Tab"] = 0x09, ["Space"] = 0x20,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Ins"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C, ["Plus"] = 0xBB, ["Minus"] = 0xBD, ["Comma"] = 0xBC, ["Period"] = 0xBE,
        ["PlayPause"] = 0xB3, ["NextTrack"] = 0xB0, ["PrevTrack"] = 0xB1, ["Stop"] = 0xB2,
        ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["Mute"] = 0xAD,
    };

    private static readonly HashSet<ushort> Modifiers = [0x11, 0x10, 0x12, 0x5B];

    /// <summary>Keys that need the extended-key flag, or apps read them as numpad keys.</summary>
    public static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x2E or 0x5B;

    public static bool TryParse(string? text, out ushort[] keys)
    {
        keys = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var result = new List<ushort>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (Key(parts[i]) is not { } vk || result.Contains(vk))
                return false;
            // Everything before the last part must be a modifier.
            if (i < parts.Length - 1 && !Modifiers.Contains(vk))
                return false;
            result.Add(vk);
        }

        keys = [.. result];
        return true;
    }

    private static ushort? Key(string name)
    {
        if (Named.TryGetValue(name, out var vk))
            return vk;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        return null;
    }
}
