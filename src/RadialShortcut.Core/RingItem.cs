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
        new("ocr", "Metin al", "Metin al", "Yazıyı kopyala", "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M8 9h8 M12 9v7"),
        new("picker", "Renk", "Renk seç", "HEX · RGB · HSL", "M14 7l3 3 M16 3.5l4.5 4.5-3 3-4.5-4.5z M13.5 6.5L5 15v4h4l8.5-8.5"),
        new("translate", "Çevir", "Çevir", "Bölgeyi çevir", "M4 5h9 M8.5 3v2 M11 5c-1 4-3.5 7-7 9 M6 9c1.5 2 3.5 3.5 6 4.5 M13 21l4-9 4 9 M14.5 18h5"),
        new("crop", "Kırp", "Ekran görüntüsü", "Bölge kırp", "M6 2v14h14 M2 6h14v14"),
        new("scroll", "Sayfa", "Tam sayfa", "Kaydırarak al", "M6 3h12v18H6z M12 7v10 M9.5 9.5L12 7l2.5 2.5 M9.5 14.5L12 17l2.5-2.5"),
        new("record", "Kayıt", "Ekran kaydı", "Bölge → MP4", "M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16 M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6"),
        new("lens", "Görsel ara", "Görselle ara", "Google Lens", "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M11 8a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7 M13.5 14l3 3"),
        new("pin", "Sabitle", "Sabitle", "Üstte kalsın", "M9 4h6 M10 4v6l-3 4h10l-3-4V4 M12 14v6"),
        new("ruler", "Ölç", "Cetvel", "Piksel ölç", "M3 17L17 3l4 4L7 21z M7 13l2 2 M10 10l2 2 M13 7l2 2"),
        new("top", "Üstte tut", "Üstte tut", "Aç / kapat", "M5 4h14 M12 20V8 M7 13l5-5 5 5"),
        new("search", "Web ara", "Web'de ara", "Seçili metin", "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14 M16 16l4 4"),
        new("calc", "Hesapla", "Hesapla", "12*4+3 → 51", "M6 3h12v18H6z M9 7h6 M9 12h1 M14 12h1 M9 16h1 M14 16h1"),
        new("qr", "QR", "QR oku", "Barkod da olur", "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h2v2h-2z M18 18h2v2h-2z M14 18h2 M18 14h2"),
    ];

    public static IReadOnlyList<RingItem> For(int sliceCount) => Items.Take(sliceCount).ToList();
}

/// <summary>The second layer, opened with Shift + right-hold: tools that work on files rather than the screen.</summary>
public static class DefaultFileRing
{
    /// <summary>Clockwise from the top.</summary>
    public static IReadOnlyList<RingItem> Items { get; } =
    [
        new("convert", "Dönüştür", "Dönüştür", "PNG · MP4 · PDF", "M4 8h13l-3-3 M20 16H7l3 3"),
        new("compress", "Sıkıştır", "Sıkıştır", "Boyutu küçült", "M12 3v6 M9 6l3 3 3-3 M12 21v-6 M9 18l3-3 3 3 M4 12h16"),
        new("resize", "Boyut", "Boyutlandır", "%50 · 1080p", "M4 4h7v7H4z M14 10V4h6v6 M20 4l-6 6 M4 14v6h6"),
        new("trim", "Video kırp", "Video kırp", "Başlangıç · bitiş", "M6 9a3 3 0 1 0 0-6a3 3 0 1 0 0 6 M6 21a3 3 0 1 0 0-6a3 3 0 1 0 0 6 M8.5 7.5L20 18 M8.5 16.5L20 6"),
        new("annotate", "Not", "Not ekle", "Ok · kutu · bulanık", "M4 20h4L19 9l-4-4L4 16z M13 7l4 4"),
        new("download", "İndir", "Linkten indir", "Panodaki link", "M12 4v11 M7 10l5 5 5-5 M5 20h14"),
        new("library", "Geçmiş", "Kütüphane", "Ara · Ctrl+F", "M4 5h16 M4 10h16 M4 15h10 M17 15a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5 M19 19.5l1.5 1.5"),
        new("nobg", "Arka plan", "Arka planı sil", "Şeffaf PNG", "M4 4h4 M10 4h4 M16 4h4 M4 20h4 M10 20h4 M16 20h4 M4 10v4 M20 10v4 M12 7a3 3 0 1 0 0 6a3 3 0 1 0 0-6 M7 17c1-2.5 3-3.5 5-3.5s4 1 5 3.5"),
    ];
}

public static class RingLayers
{
    public const string Main = "main", Files = "files";

    public static IReadOnlyList<string> All { get; } = [Main, Files];
}

