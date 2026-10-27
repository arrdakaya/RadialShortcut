using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>Reading and writing images with WPF's own codecs, plus a minimal PDF writer.</summary>
internal static class ImageIO
{
    /// <summary>
    /// The first frame, upright: phone photos keep their rotation in EXIF, which WPF does not apply.
    /// Null when no installed codec reads the file (WebP or HEIC without the Store extensions).
    /// </summary>
    public static BitmapSource? TryLoad(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            BitmapSource image = frame;
            if (Orientation(frame) is { } rotate and not 1)
            {
                var transform = new TransformGroup();
                if (rotate is 2 or 4 or 5 or 7)
                    transform.Children.Add(new ScaleTransform(-1, 1));
                transform.Children.Add(new RotateTransform(rotate switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 }));
                image = new TransformedBitmap(frame, transform);
            }
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static int? Orientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort value ? value : null;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encoder by extension; only for <see cref="MediaKinds.NativeImages"/>.</summary>
    public static void Save(BitmapSource image, string path, int jpegQuality = 90)
    {
        BitmapEncoder encoder = MediaKinds.Extension(path) switch
        {
            "jpg" or "jpeg" or "jfif" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tif" or "tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG has no alpha: flatten onto white instead of the encoder's black.
        if (encoder is JpegBitmapEncoder && HasAlpha(image))
            image = OnWhite(image);
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static byte[] Jpeg(BitmapSource image, int quality = 90)
    {
        if (HasAlpha(image))
            image = OnWhite(image);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>True when some pixel is not fully opaque.</summary>
    public static bool HasAlpha(BitmapSource image)
    {
        if (!image.Format.Equals(PixelFormats.Bgra32) && !image.Format.Equals(PixelFormats.Pbgra32)
            && !image.Format.Equals(PixelFormats.Prgba64) && !image.Format.Equals(PixelFormats.Rgba64) && !image.Format.Equals(PixelFormats.Indexed8))
            return false;
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
                return true;
        }
        return false;
    }

    /// <summary>High-quality downscale to an exact pixel size.</summary>
    public static BitmapSource Resize(BitmapSource image, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(image, new System.Windows.Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource OnWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new System.Windows.Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(image, rect);
        }
        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    /// <summary>
    /// One page per image, each page the size of its image at 96 DPI, with the JPEG embedded as is.
    /// Just enough of the PDF format for that: catalog, pages, one image XObject per page.
    /// </summary>
    public static void WritePdf(string path, IReadOnlyList<(byte[] Jpeg, int Width, int Height)> pages)
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        void Write(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            file.Write(bytes);
        }
        void Begin()
        {
            offsets.Add(file.Position);
            Write($"{offsets.Count} 0 obj\n");
        }

        Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        // Objects: 1 catalog, 2 pages, then per page: page, content, image.
        Begin();
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin();
        var kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var (jpeg, w, h) = pages[i];
            // 72 points per inch, the image's pixels at 96 per inch.
            double pw = w * 0.75, ph = h * 0.75;
            var size = FormattableString.Invariant($"{pw:0.##} {ph:0.##}");
            Begin();
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size}] /Resources << /XObject << /Im0 {5 + i * 3} 0 R >> >> /Contents {4 + i * 3} 0 R >>\nendobj\n");
            var content = FormattableString.Invariant($"q {pw:0.##} 0 0 {ph:0.##} 0 0 cm /Im0 Do Q");
            Begin();
            Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            Begin();
            Write($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            file.Write(jpeg);
            Write("\nendstream\nendobj\n");
        }

        var xref = file.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
