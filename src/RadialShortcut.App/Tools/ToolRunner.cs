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

namespace RadialShortcut.App.Tools;

/// <summary>Runs the tool behind a ring slice. Runs on the UI thread.</summary>
public static class ToolRunner
{
    private static bool _busy;

    public static async Task RunAsync(RingItem item, ScreenPoint at)
    {
        // A second gesture while a picker or tool is still up would stack overlays.
        if (_busy)
            return;
        _busy = true;
        try
        {
            switch (item.Id)
            {
                case "crop":
                    await CropAsync();
                    break;
                case "ocr":
                    await OcrAsync();
                    break;
                default:
                    var near = new Int32Rect(at.X, at.Y, 1, 1);
                    ResultCard.ForMessage(item.Title, "Bu araç henüz hazır değil.", near).ShowNear();
                    break;
            }
        }
        catch (Exception e)
        {
            var (x, y) = ScreenCapture.CursorPosition();
            ResultCard.ForMessage($"{item.Title}: hata", e.Message, new Int32Rect(x, y, 1, 1)).ShowNear();
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task CropAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        CopyImage(region.Image);
        var file = Save(region.Image);

        ResultCard.ForImage("Panoya kopyalandı", $"{region.ScreenRect.Width} × {region.ScreenRect.Height} · {Path.GetFileName(file)}", region.Image, region.ScreenRect)
            .WithButton("Klasörde göster", () => Process.Start("explorer.exe", $"/select,\"{file}\""))
            .WithButton("Tamam", () => { }, primary: true)
            .ShowNear();
    }

    private static async Task OcrAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            ResultCard.ForMessage("Metin okunamadı", "Windows'ta OCR destekli bir dil yüklü değil (Ayarlar → Zaman ve dil → Dil).", region.ScreenRect).ShowNear();
            return;
        }

        var result = await engine.RecognizeAsync(ToSoftwareBitmap(Upscale(region.Image)));
        var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
        if (text.Length == 0)
        {
            ResultCard.ForMessage("Metin bulunamadı", "Seçilen bölgede okunabilir metin yok.", region.ScreenRect).ShowNear();
            return;
        }

        CopyText(text);
        ResultCard.ForText("Metin panoya kopyalandı", text, region.ScreenRect)
            .WithButton("Tamam", () => { }, primary: true)
            .ShowNear();
    }

    /// <summary>The Windows OCR engine misses small text; doubling small captures helps a lot.</summary>
    private static BitmapSource Upscale(BitmapSource image)
    {
        if (image.PixelHeight >= 120 || image.PixelWidth * 2 > OcrEngine.MaxImageDimension)
            return image;

        var scaled = new TransformedBitmap(image, new ScaleTransform(2, 2));
        scaled.Freeze();
        return scaled;
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bgra.PixelWidth, bgra.PixelHeight, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    private static string Save(BitmapSource image)
    {
        Directory.CreateDirectory(AppPaths.CaptureDirectory);
        var file = Path.Combine(AppPaths.CaptureDirectory, $"Ekran görüntüsü {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        using var stream = File.Create(file);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        encoder.Save(stream);
        return file;
    }

    // The clipboard can be briefly locked by another process; retry a few times before giving up.
    private static void CopyImage(BitmapSource image) => Retry(() => Clipboard.SetImage(image));

    private static void CopyText(string text) => Retry(() => Clipboard.SetText(text));

    private static void Retry(Action action)
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
