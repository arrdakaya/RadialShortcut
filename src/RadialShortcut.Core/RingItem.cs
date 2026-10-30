using static RadialShortcut.Core.Loc;

namespace RadialShortcut.Core;

/// <param name="Label">Short text drawn on the slice.</param>
/// <param name="Title">Full name shown in the center while the slice is hovered.</param>
/// <param name="Hint">One-line description under the title.</param>
/// <param name="Icon">24×24 stroke-only path data (SVG/WPF mini-language).</param>
/// <param name="Action">Set for user-defined slices (Id "action"); null for built-in tools.</param>
public sealed record RingItem(string Id, string Label, string Title, string Hint, string Icon, SliceAction? Action = null);

public static class DefaultRing
{
    /// <summary>Clockwise from the top. The first eight form the default ring, twelve-slice rings use twelve; the rest only through config.json.</summary>
    public static IReadOnlyList<RingItem> Items { get; } =
    [
        new("ocr", T("Metin al", "Text"), T("Metin al", "Grab text"), T("Yazıyı kopyala", "Copy any text"), "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M8 9h8 M12 9v7"),
        new("picker", T("Renk", "Color"), T("Renk seç", "Pick color"), "HEX · RGB · HSL", "M14 7l3 3 M16 3.5l4.5 4.5-3 3-4.5-4.5z M13.5 6.5L5 15v4h4l8.5-8.5"),
        new("translate", T("Çevir", "Translate"), T("Çevir", "Translate"), T("Bölgeyi çevir", "Translate a region"), "M4 5h9 M8.5 3v2 M11 5c-1 4-3.5 7-7 9 M6 9c1.5 2 3.5 3.5 6 4.5 M13 21l4-9 4 9 M14.5 18h5"),
        new("crop", T("Kırp", "Capture"), T("Ekran görüntüsü", "Screenshot"), T("Bölge kırp", "Capture a region"), "M6 2v14h14 M2 6h14v14"),
        new("scroll", T("Sayfa", "Page"), T("Tam sayfa", "Full page"), T("Kaydırarak al", "Scrolling capture"), "M6 3h12v18H6z M12 7v10 M9.5 9.5L12 7l2.5 2.5 M9.5 14.5L12 17l2.5-2.5"),
        new("record", T("Kayıt", "Record"), T("Ekran kaydı", "Screen recording"), T("Bölge → MP4", "Region → MP4"), "M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16 M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6"),
        new("lens", T("Görsel ara", "Lens"), T("Görselle ara", "Search by image"), "Google Lens", "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M11 8a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7 M13.5 14l3 3"),
        new("pin", T("Sabitle", "Pin"), T("Sabitle", "Pin to screen"), T("Üstte kalsın", "Stays on top"), "M9 4h6 M10 4v6l-3 4h10l-3-4V4 M12 14v6"),
        new("ruler", T("Ölç", "Measure"), T("Cetvel", "Ruler"), T("Piksel ölç", "Measure pixels"), "M3 17L17 3l4 4L7 21z M7 13l2 2 M10 10l2 2 M13 7l2 2"),
        new("top", T("Üstte tut", "On top"), T("Üstte tut", "Always on top"), T("Aç / kapat", "Toggle"), "M5 4h14 M12 20V8 M7 13l5-5 5 5"),
        new("search", T("Web ara", "Search"), T("Web'de ara", "Search the web"), T("Seçili metin", "Selected text"), "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14 M16 16l4 4"),
        new("calc", T("Hesapla", "Calc"), T("Hesapla", "Calculate"), "12*4+3 → 51", "M6 3h12v18H6z M9 7h6 M9 12h1 M14 12h1 M9 16h1 M14 16h1"),
        new("qr", "QR", T("QR oku", "Read QR"), T("Barkod da olur", "Barcodes too"), "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h2v2h-2z M18 18h2v2h-2z M14 18h2 M18 14h2"),
    ];

    public static IReadOnlyList<RingItem> For(int sliceCount) => Items.Take(sliceCount).ToList();
}

/// <summary>The second layer, opened with Shift + right-hold: tools that work on files rather than the screen.</summary>
public static class DefaultFileRing
{
    /// <summary>Clockwise from the top.</summary>
    public static IReadOnlyList<RingItem> Items { get; } =
    [
        new("convert", T("Dönüştür", "Convert"), T("Dönüştür", "Convert"), "PNG · MP4 · PDF", "M4 8h13l-3-3 M20 16H7l3 3"),
        new("compress", T("Sıkıştır", "Compress"), T("Sıkıştır", "Compress"), T("Boyutu küçült", "Make it smaller"), "M12 3v6 M9 6l3 3 3-3 M12 21v-6 M9 18l3-3 3 3 M4 12h16"),
        new("resize", T("Boyut", "Resize"), T("Boyutlandır", "Resize"), "50% · 1080p", "M4 4h7v7H4z M14 10V4h6v6 M20 4l-6 6 M4 14v6h6"),
        new("trim", T("Video kırp", "Trim"), T("Video kırp", "Trim video"), T("Başlangıç · bitiş", "Start · end"), "M6 9a3 3 0 1 0 0-6a3 3 0 1 0 0 6 M6 21a3 3 0 1 0 0-6a3 3 0 1 0 0 6 M8.5 7.5L20 18 M8.5 16.5L20 6"),
        new("annotate", T("Not", "Annotate"), T("Not ekle", "Annotate"), T("Ok · kutu · bulanık", "Arrow · box · blur"), "M4 20h4L19 9l-4-4L4 16z M13 7l4 4"),
        new("download", T("İndir", "Download"), T("Linkten indir", "Download link"), T("Panodaki link", "Link on clipboard"), "M12 4v11 M7 10l5 5 5-5 M5 20h14"),
        new("library", T("Geçmiş", "Library"), T("Kütüphane", "Library"), T("Ara · Ctrl+F", "Search · Ctrl+F"), "M4 5h16 M4 10h16 M4 15h10 M17 15a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5 M19 19.5l1.5 1.5"),
        new("nobg", T("Arka plan", "No BG"), T("Arka planı sil", "Remove BG"), T("Şeffaf PNG", "Transparent PNG"), "M4 4h4 M10 4h4 M16 4h4 M4 20h4 M10 20h4 M16 20h4 M4 10v4 M20 10v4 M12 7a3 3 0 1 0 0 6a3 3 0 1 0 0-6 M7 17c1-2.5 3-3.5 5-3.5s4 1 5 3.5"),
        new("unlock", T("Kilit aç", "Unlock"), T("Kilidi aç", "Unlock"), T("Dosyayı tutanı kapat", "Close what holds the file"), "M6 11h12v9H6z M9 11V7a3 3 0 0 1 6 0 M12 14.5v2"),
    ];
}

/// <summary>The third layer, opened with Ctrl + right-hold: a few apps every Windows has, as a starting point to edit.</summary>
public static class DefaultAppRing
{
    /// <summary>Clockwise from the top; a fresh list each call, so the settings window can edit it.</summary>
    public static List<SliceConfig> Slices() =>
    [
        Launch(T("Dosyalar", "Files"), "explorer.exe"),
        Launch(T("Not Defteri", "Notepad"), "notepad.exe"),
        Launch(T("Hesap Mak.", "Calculator"), "calc.exe"),
        Launch("Paint", "mspaint.exe"),
        Launch("Terminal", "wt.exe"),
        Launch(T("Görev Yön.", "Task Mgr"), "taskmgr.exe"),
    ];

    private static SliceConfig Launch(string label, string path) =>
        new() { Label = label, Action = new SliceAction { Type = "launch", Path = path } };
}

public static class RingLayers
{
    public const string Main = "main", Files = "files", Apps = "apps";

    public static IReadOnlyList<string> All { get; } = [Main, Files, Apps];
}

