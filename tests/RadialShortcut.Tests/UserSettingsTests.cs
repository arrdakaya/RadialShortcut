using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class UserSettingsTests
{
    [Fact]
    public void Save_round_trips_and_keeps_a_backup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "config.json");
        File.WriteAllText(file, "// el yazısı\n{ \"theme\": \"dark\" }");

        var settings = UserSettings.Load(file);
        settings.RingHotkey = "Ctrl+Shift+Q";
        settings.Rings.Add(new RingConfig
        {
            Id = "chrome", Name = "Şık", Match = new RingMatch { Process = "chrome" },
            Slices = [new() { Tool = "ocr" }, new() { Label = "Yeni sekme", Action = new SliceAction { Type = "hotkey", Keys = "Ctrl+T" } }],
        });
        settings.Save(file);

        var back = UserSettings.Load(file);
        Assert.Equal("dark", back.Theme);
        Assert.Equal("Ctrl+Shift+Q", back.RingHotkey);
        Assert.Equal("Ctrl+T", back.Rings[0].Slices[1].Action!.Keys);
        Assert.Contains("\"Şık\"", File.ReadAllText(file));
        Assert.DoesNotContain("null", File.ReadAllText(file));
        Assert.StartsWith("// el yazısı", File.ReadAllText(file + ".bak"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Ring_style_clamps_size_and_ignores_bad_colors()
    {
        var big = new UserSettings { RingScale = 5, RingAccent = "#22C55E" }.RingStyle();
        Assert.Equal(new RingStyle().OuterRadius * 1.4, big.OuterRadius, 3);
        Assert.Equal("#22C55E", big.Accent);
        Assert.Equal(new RingStyle().Accent, new UserSettings { RingAccent = "kırmızı" }.RingStyle().Accent);
    }
}
