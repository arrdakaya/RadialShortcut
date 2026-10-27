using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Arka planı sil: a small segmentation model (silueta, a compressed U²-Net) runs on this computer
/// and its mask becomes the alpha channel. Works on selected images, or on a piece of the screen.
/// </summary>
internal static class BackgroundRemover
{
    private const int Side = 320;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f], Std = [0.229f, 0.224f, 0.225f];

    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var images = (await SelectedFiles.SelectionOnlyAsync(target)).Where(f => MediaKinds.Of(f) == MediaKind.Image).ToList();
        CapturedRegion? region = null;
        if (images.Count == 0 && (region = await RegionPicker.PickAsync()) is null)
            return;
        if (region is not null)
            anchor = region.ScreenRect;
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Silueta))
            return;

        // The session holds about 150 MB; it lives only as long as this run.
        using var session = await Task.Run(() => new InferenceSession(ToolDeps.PathOf(ToolDeps.Silueta)));
        if (region is not null)
        {
            var result = await RemoveAsync(session, region.Image);
            ToolRunner.CopyImage(result);
            var file = ToolRunner.Save(result, "Arka plansız");
            ResultCard.ForImage("Arka plan silindi", $"Panoya kopyalandı · {Path.GetFileName(file)}", result, anchor)
                .WithButton("Klasörde göster", () => ToolRunner.ShowInFolder(file))
                .WithButton("Tamam", () => { }, primary: true)
                .ShowNear();
            return;
        }

        await FileJobs.RunAsync("nobg", "Arka plan siliniyor", images, anchor, async (input, progress, cancel) =>
        {
            var image = ImageIO.TryLoad(input) ?? throw new NotSupportedException("resim okunamadı (PNG ya da JPG dene).");
            var result = await RemoveAsync(session, image);
            var output = FileJobs.OutputFor(input, " (arka plansız)", "png");
            ImageIO.Save(result, output);
            return output;
        });
    }

    private static async Task<BitmapSource> RemoveAsync(InferenceSession session, BitmapSource image)
    {
        var (pixels, w, h) = Bgra(image);
        var (small, _, _) = Bgra(ImageIO.Resize(image, Side, Side));

        var mask = await Task.Run(() =>
        {
            // The model expects RGB scaled by the brightest value, then normalised per channel, NCHW.
            var input = new DenseTensor<float>([1, 3, Side, Side]);
            float max = 1;
            for (var i = 0; i < small.Length; i += 4)
                max = Math.Max(max, Math.Max(small[i], Math.Max(small[i + 1], small[i + 2])));
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    var i = (y * Side + x) * 4;
                    input[0, 0, y, x] = (small[i + 2] / max - Mean[0]) / Std[0];
                    input[0, 1, y, x] = (small[i + 1] / max - Mean[1]) / Std[1];
                    input[0, 2, y, x] = (small[i] / max - Mean[2]) / Std[2];
                }
            }

            using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
            var prediction = outputs.First().AsTensor<float>();
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in prediction)
            {
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            var bytes = new byte[Side * Side];
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                    bytes[y * Side + x] = (byte)Math.Clamp((prediction[0, 0, y, x] - lo) / Math.Max(hi - lo, 1e-6f) * 255, 0, 255);
            }
            return bytes;
        });

        // Back to full size with smoothing, so the edge follows the subject instead of 320 px steps.
        var maskImage = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Gray8, null, mask, Side);
        var (full, _, _) = Bgra(ImageIO.Resize(maskImage, w, h));
        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = (byte)(pixels[i + 3] * full[i] / 255);

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        result.Freeze();
        return result;
    }

    private static (byte[] Pixels, int W, int H) Bgra(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    }
}
