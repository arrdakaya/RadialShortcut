using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class FileToolTests
{
    [Theory]
    [InlineData("a.JPG", MediaKind.Image)]
    [InlineData("klip.mkv", MediaKind.Video)]
    [InlineData("şarkı.flac", MediaKind.Audio)]
    [InlineData("belge.pdf", MediaKind.Pdf)]
    [InlineData("notlar.txt", MediaKind.Other)]
    public void KindByExtension(string file, MediaKind kind) => Assert.Equal(kind, MediaKinds.Of(file));

    [Fact]
    public void ConvertTargetsLeaveOutOwnFormat()
    {
        Assert.DoesNotContain("jpg", MediaKinds.ConvertTargets("foto.jpeg"));
        Assert.Contains("png", MediaKinds.ConvertTargets("foto.jpeg"));
        Assert.Contains("gif", MediaKinds.ConvertTargets("klip.mov"));
        Assert.Empty(MediaKinds.ConvertTargets("notlar.txt"));
    }

    [Fact]
    public void FreeNameSkipsTakenNames()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "a.png"), "");
        File.WriteAllText(Path.Combine(dir, "a (2).png"), "");
        Assert.Equal(Path.Combine(dir, "a (3).png"), MediaKinds.FreeName(dir, "a", "png"));
        Assert.Equal(Path.Combine(dir, "b.png"), MediaKinds.FreeName(dir, "b", "png"));
    }

    [Fact]
    public void HistoryKeepsNewestFirstAndSkipsBrokenLines()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "history.jsonl");
        History.Add(file, new HistoryEntry(new DateTime(2026, 1, 1), "ocr", Text: "ilk"));
        File.AppendAllText(file, "{bozuk\n");
        History.Add(file, new HistoryEntry(new DateTime(2026, 1, 2), "capture", Path: @"C:\x\Ekran görüntüsü.png"));
        var entries = History.Load(file);
        Assert.Equal(2, entries.Count);
        Assert.Equal("capture", entries[0].Tool);
    }

    [Fact]
    public void HistorySearchIgnoresCaseAndAccents()
    {
        var entry = new HistoryEntry(DateTime.Now, "capture", Path: @"C:\x\Ekran görüntüsü.png");
        Assert.True(History.Matches(entry, "GORUNTU"));
        Assert.True(History.Matches(entry, "ekran png"));
        Assert.False(History.Matches(entry, "video"));
    }
}
