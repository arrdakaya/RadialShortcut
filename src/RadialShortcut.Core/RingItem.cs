namespace RadialShortcut.Core;

/// <param name="Label">Short text drawn on the slice.</param>
/// <param name="Title">Full name shown in the center while the slice is hovered.</param>
/// <param name="Hint">One-line description under the title.</param>
/// <param name="Icon">24×24 stroke-only path data (SVG/WPF mini-language).</param>
/// <param name="Action">Set for user-defined slices (Id "action"); null for built-in tools.</param>
public sealed record RingItem(string Id, string Label, string Title, string Hint, string Icon, SliceAction? Action = null);

public static class DefaultRing
{
    /// <summary>Clockwise from the top. The first eight form the default ring; twelve-slice rings use all of them.</summary>
    public static IReadOnlyList<RingItem> Items { get; } =
    [
        new("ocr", "Metin al", "Ekrandan metin al (OCR)", "Bölge seç → panoya", "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M8 9h8 M12 9v7"),
        new("picker", "Renk", "Renk seçici", "Büyüteç · HEX/RGB/HSL", "M14 7l3 3 M16 3.5l4.5 4.5-3 3-4.5-4.5z M13.5 6.5L5 15v4h4l8.5-8.5"),
        new("translate", "Çevir", "Ekrandan çevir", "Bölge → çeviri kartı", "M4 5h9 M8.5 3v2 M11 5c-1 4-3.5 7-7 9 M6 9c1.5 2 3.5 3.5 6 4.5 M13 21l4-9 4 9 M14.5 18h5"),
        new("crop", "Kırp", "Bölge ekran görüntüsü", "Panoya + dosyaya", "M6 2v14h14 M2 6h14v14"),
        new("scroll", "Uzun", "Kaydırmalı ekran görüntüsü", "Sayfayı kaydırıp birleştir", "M6 3h12v18H6z M12 7v10 M9.5 9.5L12 7l2.5 2.5 M9.5 14.5L12 17l2.5-2.5"),
        new("record", "Kayıt", "Ekran kaydı", "Bölge → MP4 / GIF", "M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16 M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6"),
        new("lens", "Görsel ara", "Görselle ara", "Bölge → Google Lens", "M4 8V4h4 M16 4h4v4 M20 16v4h-4 M8 20H4v-4 M11 8a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7 M13.5 14l3 3"),
        new("qr", "QR", "QR / barkod oku", "Bölge → panoya", "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h2v2h-2z M18 18h2v2h-2z M14 18h2 M18 14h2"),
        new("ruler", "Ölç", "Ekran cetveli", "Piksel mesafe ölç", "M3 17L17 3l4 4L7 21z M7 13l2 2 M10 10l2 2 M13 7l2 2"),
        new("top", "Üstte tut", "Pencereyi üstte tut", "Aç / kapat", "M5 4h14 M12 20V8 M7 13l5-5 5 5"),
        new("search", "Web ara", "Seçili metni ara", "Tarayıcıda", "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14 M16 16l4 4"),
        new("calc", "Hesapla", "Seçili ifadeyi hesapla", "12*4+3 → 51", "M6 3h12v18H6z M9 7h6 M9 12h1 M14 12h1 M9 16h1 M14 16h1"),
    ];

    public static IReadOnlyList<RingItem> For(int sliceCount) => Items.Take(sliceCount).ToList();
}
