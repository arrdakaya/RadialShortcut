using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class RingCatalogTests
{
    private static RingCatalog Catalog(string json) => new(UserSettings.Parse(json).Rings, 8);

    private const string Rings = """
        {
          // comments and trailing commas are fine in a hand-edited file
          "rings": [
            { "id": "g", "slices": [ { "tool": "ocr" }, { "tool": "crop" } ] },
            { "id": "chrome", "match": { "process": "chrome" },
              "slices": [ { "tool": "lens" }, { "label": "Yeni", "action": { "type": "hotkey", "keys": "Ctrl+T" } } ] },
            { "id": "yt", "match": { "process": "Chrome.exe", "windowTitle": "you ?tube" },
              "slices": [ { "tool": "record" }, { "tool": "crop" }, ] },
          ]
        }
        """;

    [Fact]
    public void TitleMatchBeatsProcessMatch() => Assert.Equal("yt", Catalog(Rings).For("chrome.exe", "Kedi - YouTube").Id);

    [Fact]
    public void ProcessMatchWithoutTitle() => Assert.Equal("chrome", Catalog(Rings).For("CHROME.EXE", "Haberler").Id);

    [Fact]
    public void OtherAppsGetTheGlobalRing() => Assert.Equal("g", Catalog(Rings).For("notepad.exe", "").Id);

    [Fact]
    public void NoRingsMeansBuiltInToolRing()
    {
        var catalog = new RingCatalog([], 8);
        Assert.Equal(8, catalog.Global.Items.Count);
        Assert.Equal("ocr", catalog.Global.Items[0].Id);
    }

    [Fact]
    public void ActionSliceGetsDefaultsFromItsAction()
    {
        var item = Catalog(Rings).For("chrome.exe", "").Items[1];
        Assert.Equal("action", item.Id);
        Assert.Equal("Ctrl+T", item.Hint);
        Assert.Equal("hotkey", item.Action!.Type);
    }

    [Fact]
    public void BadSlicesAndRingsAreSkippedWithWarnings()
    {
        var catalog = Catalog("""
            { "rings": [
              { "id": "a", "match": { "process": "x" }, "slices": [
                  { "tool": "nope" },
                  { "label": "K", "action": { "type": "hotkey", "keys": "Ctrl+Blah" } },
                  { "label": "U", "action": { "type": "url" } },
                  { "tool": "ocr" } ] },
              { "id": "b", "match": { "process": "y", "windowTitle": "(" }, "slices": [ { "tool": "ocr" }, { "tool": "qr" } ] },
              { "id": "b", "match": { "process": "z" }, "slices": [ { "tool": "ocr" }, { "tool": "qr" } ] }
            ] }
            """);
        Assert.Equal(6, catalog.Warnings.Count);
        Assert.Single(catalog.All); // only the built-in global ring is left
    }

    [Fact]
    public void ExampleConfigLoadsCleanly()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "../../../../../src/RadialShortcut.App/config.example.json");
        var settings = UserSettings.Parse(File.ReadAllText(file));
        var catalog = new RingCatalog(settings.Rings, 8);
        Assert.Empty(catalog.Warnings);
        Assert.Equal(4, catalog.All.Count());
        Assert.Equal("chrome-youtube", catalog.For("chrome.exe", "Müzik - YouTube").Id);
    }
}

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T", new ushort[] { 0x11, 0x10, 0x54 })]
    [InlineData("win + v", new ushort[] { 0x5B, 0x56 })]
    [InlineData("Alt+F4", new ushort[] { 0x12, 0x73 })]
    [InlineData("F12", new ushort[] { 0x7B })]
    [InlineData("PlayPause", new ushort[] { 0xB3 })]
    [InlineData("Ctrl+5", new ushort[] { 0x11, 0x35 })]
    public void Parses(string text, ushort[] expected)
    {
        Assert.True(KeyChord.TryParse(text, out var keys));
        Assert.Equal(expected, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("T+Ctrl")]
    [InlineData("Ctrl+Ctrl+T")]
    [InlineData("Ctrl+Blah")]
    [InlineData("F25")]
    public void Rejects(string text) => Assert.False(KeyChord.TryParse(text, out _));
}
