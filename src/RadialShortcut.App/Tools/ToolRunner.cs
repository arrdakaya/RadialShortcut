using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;
using RadialShortcut.Input;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;

namespace RadialShortcut.App.Tools;

/// <summary>Runs the tool behind a ring slice. Runs on the UI thread.</summary>
public static class ToolRunner
{
    private static bool _busy;

    /// <summary>A tool is open or working; updates wait for this to clear.</summary>
    public static bool IsBusy => _busy;

    /// <summary>Adds what a tool made to the library ("Kütüphane").</summary>
    internal static void Remember(string tool, string? path = null, string? text = null) =>
        History.Add(AppPaths.HistoryFile, new HistoryEntry(DateTime.Now, tool, path, text));

    public static async Task RunAsync(RingItem item, ScreenPoint at)
    {
        // A second gesture while a picker or tool is still up would stack overlays.
        if (_busy)
            return;
        _busy = true;
        try
        {
            // The ring is closed by now, so the window under the press point is the one that was right-clicked.
            var target = Native.WindowAt(at.X, at.Y);
            var point = (at.X, at.Y);
            var anchor = new Int32Rect(at.X, at.Y, 1, 1);
            switch (item.Id)
            {
                case "crop":
                    await CropAsync();
                    break;
                case "ocr":
                    await OcrAsync();
                    break;
                case "qr":
                    await QrAsync();
                    break;
                case "pin":
                    await PinWindow.RunAsync();
                    break;
                case "picker":
                    await ColorPicker.RunAsync();
                    break;
                case "ruler":
                    await Ruler.RunAsync();
                    break;
                case "top":
                    WindowTools.ToggleTopmost(target, point);
                    break;
                case "search":
                    await SelectionTools.SearchAsync(target, point);
                    break;
                case "calc":
                    await SelectionTools.CalculateAsync(target, point);
                    break;
                case "lens":
                    await WebTools.LensAsync();
                    break;
                case "translate":
                    await WebTools.TranslateAsync();
                    break;
                case "record":
                    await ScreenRecorder.RunAsync();
                    break;
                case "scroll":
                    await ScrollCapture.RunAsync();
                    break;
                case "convert":
                    await FileTools.ConvertAsync(target, anchor);
                    break;
                case "compress":
                    await FileTools.CompressAsync(target, anchor);
                    break;
                case "resize":
                    await FileTools.ResizeAsync(target, anchor);
                    break;
                case "trim":
                    await TrimWindow.RunAsync(target, anchor);
                    break;
                case "annotate":
                    await AnnotateWindow.RunAsync(target, anchor);
                    break;
                case "download":
                    await Downloader.RunAsync(anchor);
                    break;
                case "library":
                    LibraryWindow.ShowOrActivate();
                    break;
                case "nobg":
                    await BackgroundRemover.RunAsync(target, anchor);
                    break;
                case "action" when item.Action is { } action:
                    await ActionRunner.RunAsync(action, target);
                    break;
                default:
                    ResultCard.Near(point, item.Title, "Bu araç henüz hazır değil.").ShowNear();
                    break;
            }
        }
        catch (Exception e)
        {
            // The card shows the short message; the log keeps the details for a bug report.
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.ConfigDirectory, "error.log"), $"[{DateTime.Now:u}] {item.Id}: {e}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            ResultCard.Near(ScreenCapture.CursorPosition(), $"{item.Title}: hata", $"{e.Message} (0x{e.HResult:X8})").ShowNear();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Read on every use so hand edits to config.json apply without a restart.</summary>
    internal static UserSettings Settings => UserSettings.Load(AppPaths.ConfigFile);

    private static async Task CropAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        CopyImage(region.Image);
        var file = Save(region.Image);

        ResultCard.ForImage("Panoya kopyalandı", $"{region.ScreenRect.Width} × {region.ScreenRect.Height} · {Path.GetFileName(file)}", region.Image, region.ScreenRect)
            .WithButton("Dosyayı sil", () => DeleteCapture(file))
            .WithButton("Klasörde göster", () => ShowInFolder(file))
            .WithButton("Not ekle", () => _ = AnnotateWindow.OpenAsync(file, region.Image))
            .WithButton("Tamam", () => { }, primary: true)
            .ShowNear();
    }

    /// <summary>For when only the clipboard copy was wanted; the clipboard keeps the image.</summary>
    internal static void DeleteCapture(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }

    private static async Task OcrAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;
        if (await RecognizeAsync(region) is not { } text)
            return;

        CopyText(text);
        Remember("ocr", text: text);
        ResultCard.ForText("Metin panoya kopyalandı", text, region.ScreenRect)
            .WithButton("Tamam", () => { }, primary: true)
            .ShowNear();
    }

    private static async Task QrAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        var (pixels, w, h) = Bgra(Upscale(region.Image));
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = { TryHarder = true, TryInverted = true } };
        var results = reader.DecodeMultiple(new RGBLuminanceSource(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGRA32));
        if (results is not { Length: > 0 })
        {
            ResultCard.ForMessage("Kod bulunamadı", "Seçilen bölgede okunabilir bir QR kod ya da barkod yok.", region.ScreenRect).ShowNear();
            return;
        }

        var text = string.Join(Environment.NewLine, results.Select(r => r.Text).Distinct());
        CopyText(text);
        var card = ResultCard.ForText($"{results[0].BarcodeFormat} panoya kopyalandı", text, region.ScreenRect);
        if (results.Length == 1 && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            card.WithButton("Aç", () => OpenUrl(uri.AbsoluteUri), primary: true);
        else
            card.WithButton("Tamam", () => { }, primary: true);
        card.ShowNear();
    }

    /// <summary>OCR of a picked region. Shows its own card and returns null when there is nothing to use.</summary>
    internal static async Task<string?> RecognizeAsync(CapturedRegion region)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            ResultCard.ForMessage("Metin okunamadı", "Windows'ta OCR destekli bir dil yüklü değil (Ayarlar → Zaman ve dil → Dil).", region.ScreenRect).ShowNear();
            return null;
        }

        var result = await engine.RecognizeAsync(ToSoftwareBitmap(Upscale(region.Image)));
        var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
        if (text.Length == 0)
        {
            ResultCard.ForMessage("Metin bulunamadı", "Seçilen bölgede okunabilir metin yok.", region.ScreenRect).ShowNear();
            return null;
        }
        return text;
    }

    /// <summary>OCR and barcode readers miss small details; doubling small captures helps a lot.</summary>
    private static BitmapSource Upscale(BitmapSource image)
    {
        if (image.PixelHeight >= 120 || image.PixelWidth * 2 > OcrEngine.MaxImageDimension)
            return image;

        var scaled = new TransformedBitmap(image, new ScaleTransform(2, 2));
        scaled.Freeze();
        return scaled;
    }

    internal static (byte[] Pixels, int Width, int Height) Bgra(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource image)
    {
        var (pixels, w, h) = Bgra(image);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    internal static string Save(BitmapSource image, string name = "Ekran görüntüsü")
    {
        Directory.CreateDirectory(AppPaths.CaptureDirectory);
        var file = Path.Combine(AppPaths.CaptureDirectory, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        using var stream = File.Create(file);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        encoder.Save(stream);
        Remember("capture", file);
        return file;
    }

    internal static byte[] Png(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static void ShowInFolder(string file) => Process.Start("explorer.exe", $"/select,\"{file}\"");

    internal static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // The clipboard can be briefly locked by another process; retry a few times before giving up.
    internal static void CopyImage(BitmapSource image) => Retry(() => Clipboard.SetImage(image));

    internal static void CopyText(string text) => Retry(() => Clipboard.SetText(text));

    internal static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < 4)
            {
                Thread.Sleep(40);
            }
        }
    }
}
