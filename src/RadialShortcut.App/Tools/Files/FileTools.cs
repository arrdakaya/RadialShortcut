using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media.Imaging;
using RadialShortcut.Core;
using Windows.Data.Pdf;

namespace RadialShortcut.App.Tools;

/// <summary>Dönüştür, Sıkıştır, Boyutlandır: one choice card, then the same work for every selected file.</summary>
internal static class FileTools
{
    public static async Task ConvertAsync(IntPtr target, Int32Rect anchor)
    {
        if (await FilesOfOneKindAsync(target, "Dönüştürülecek dosyaları seç", anchor) is not { } files)
            return;

        var targets = MediaKinds.ConvertTargets(files[0]).ToList();
        var kind = MediaKinds.Of(files[0]);
        var choices = targets.Select(t => t == "png" && kind == MediaKind.Video ? "Kare (PNG)" : t.ToUpperInvariant()).ToList();
        if (kind == MediaKind.Image && files.Count > 1)
            choices.Add("Tek PDF");
        if (kind == MediaKind.Video)
            choices.AddRange(VideoEdits.Select(e => e.Label));

        var pick = await ResultCard.ChooseAsync("Dönüştür", Describe(files), choices, anchor);
        if (pick < 0)
            return;

        if (kind == MediaKind.Video && pick >= targets.Count)
        {
            var edit = VideoEdits[pick - targets.Count];
            if (await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
                await FileJobs.RunAsync("convert", "Dönüştürülüyor", files, anchor, (input, progress, cancel) => EditVideoAsync(input, edit, progress, cancel));
            return;
        }

        if (pick == targets.Count)
        {
            await ImagesToOnePdfAsync(files, anchor);
            return;
        }

        var to = targets[pick];
        if (!NeedsFfmpeg(kind, files, to) || await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            await FileJobs.RunAsync("convert", "Dönüştürülüyor", files, anchor, (input, progress, cancel) => ConvertOneAsync(input, to, progress, cancel));
    }

    public static async Task CompressAsync(IntPtr target, Int32Rect anchor)
    {
        if (await FilesOfOneKindAsync(target, "Sıkıştırılacak dosyaları seç", anchor) is not { } files)
            return;

        if (MediaKinds.Of(files[0]) == MediaKind.Pdf)
        {
            ResultCard.ForMessage("Sıkıştır", "PDF sıkıştırma henüz yok. Resim, video ve ses dosyaları sıkıştırılabilir.", anchor).ShowNear();
            return;
        }

        var kind = MediaKinds.Of(files[0]);
        string[] levels = kind == MediaKind.Image ? ["Dengeli", "Daha küçük"] : ["Dengeli", "Daha küçük", "En küçük"];
        var estimates = await EstimateCompressedAsync(files, kind, levels.Length);
        var labels = levels.Select((l, i) => estimates?[i] is { } bytes ? $"{l} · ~{MediaKinds.Size(bytes)}" : l).ToList();
        var message = estimates is null ? Describe(files)
            : $"{Describe(files)}\nTahmini boyutlar{(kind == MediaKind.Video ? "; videoda içeriğe göre epey değişebilir" : "")}.";
        var pick = await ResultCard.ChooseAsync("Sıkıştır", message, labels, anchor);
        if (pick < 0)
            return;

        if (kind != MediaKind.Image || files.Any(f => !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f))))
        {
            if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
                return;
        }
        await FileJobs.RunAsync("compress", "Sıkıştırılıyor", files, anchor, (input, progress, cancel) => CompressOneAsync(input, pick, progress, cancel));
    }

    public static async Task ResizeAsync(IntPtr target, Int32Rect anchor)
    {
        if (await FilesOfOneKindAsync(target, "Boyutlandırılacak dosyaları seç", anchor) is not { } files)
            return;

        var kind = MediaKinds.Of(files[0]);
        if (kind is not (MediaKind.Image or MediaKind.Video))
        {
            ResultCard.ForMessage("Boyutlandır", "Sadece resim ve video boyutlandırılabilir.", anchor).ShowNear();
            return;
        }

        // Each size is a longest-side limit in pixels, or a negative percentage.
        (string Label, int Size)[] sizes = kind == MediaKind.Image
            ? [("%50", -50), ("%25", -25), ("1920 px", 1920), ("1280 px", 1280), ("800 px", 800)]
            : [("1080p", 1080), ("720p", 720), ("480p", 480), ("%50", -50)];
        var pick = await ResultCard.ChooseAsync("Boyutlandır", Describe(files), sizes.Select(s => s.Label).ToList(), anchor);
        if (pick < 0)
            return;

        var size = sizes[pick].Size;
        if (kind == MediaKind.Video || files.Any(f => !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f))))
        {
            if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
                return;
        }
        await FileJobs.RunAsync("resize", "Boyutlandırılıyor", files, anchor, (input, progress, cancel) => ResizeOneAsync(input, kind, size, progress, cancel));
    }

    /// <summary>The selection, keeping only files of the first file's kind; null (after a card) when nothing usable is left.</summary>
    private static async Task<List<string>?> FilesOfOneKindAsync(IntPtr target, string title, Int32Rect anchor)
    {
        var files = await SelectedFiles.GetAsync(target, title, SelectedFiles.AllMedia);
        if (files.Count == 0)
            return null;

        var kind = MediaKinds.Of(files[0]);
        if (kind == MediaKind.Other)
        {
            ResultCard.ForMessage("Desteklenmeyen dosya", $"{Path.GetFileName(files[0])}: resim, video, ses ya da PDF seç.", anchor).ShowNear();
            return null;
        }
        return files.Where(f => MediaKinds.Of(f) == kind).ToList();
    }

    private static string Describe(List<string> files) => files.Count == 1
        ? $"{Path.GetFileName(files[0])} · {MediaKinds.Size(new FileInfo(files[0]).Length)}"
        : $"{files.Count} dosya · {MediaKinds.Size(files.Sum(f => new FileInfo(f).Length))}";

    private static bool NeedsFfmpeg(MediaKind kind, List<string> files, string to) => kind switch
    {
        MediaKind.Image => to is "webp" or "ico" || files.Any(f => !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f))),
        MediaKind.Pdf => false,
        _ => true,
    };

    private static async Task<string?> ConvertOneAsync(string input, string to, Action<double> progress, CancellationToken cancel)
    {
        var kind = MediaKinds.Of(input);
        if (kind == MediaKind.Pdf)
            return await PdfToImagesAsync(input, to, cancel);

        var output = FileJobs.OutputFor(input, "", to);
        if (kind == MediaKind.Image)
        {
            if (to == "pdf")
            {
                var image = await LoadAnyAsync(input, cancel);
                ImageIO.WritePdf(output, [(ImageIO.Jpeg(image), image.PixelWidth, image.PixelHeight)]);
                return output;
            }
            if (MediaKinds.NativeImages.Contains(to) && ImageIO.TryLoad(input) is { } loaded)
            {
                ImageIO.Save(loaded, output);
                return output;
            }
        }

        string[] args = to switch
        {
            // thumbnail picks a representative frame, so not the black first one. Keyframes only:
            // buffering every frame of a 4K video takes gigabytes.
            "png" when kind == MediaKind.Video => ["-vf", "thumbnail=10", "-frames:v", "1"],
            "mp4" => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"],
            "webm" => ["-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-cpu-used", "4", "-c:a", "libopus"],
            "gif" => ["-vf", "fps=12,scale='min(800,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"],
            "mp3" => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
            "m4a" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
            "wav" => ["-vn", "-c:a", "pcm_s16le"],
            "ogg" => ["-vn", "-c:a", "libvorbis", "-q:a", "5"],
            "webp" => ["-c:v", "libwebp", "-quality", "90", "-frames:v", "1"],
            "ico" => ["-vf", "scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease", "-frames:v", "1"],
            _ => ["-frames:v", "1"], // png / jpg from a format WPF cannot read
        };
        string[] before = kind == MediaKind.Video && to == "png" ? ["-skip_frame", "nokey"] : [];
        await Ffmpeg.RunAsync(input, args, output, progress, cancel, before);
        return output;
    }

    // Speed 1 means the audio is dropped and the picture copied untouched.
    private static readonly (string Label, string Tag, double Speed)[] VideoEdits =
        [("Sessiz", " (sessiz)", 1), ("2x hız", " (2x)", 2), ("0.5x hız", " (0.5x)", 0.5)];

    private static async Task<string?> EditVideoAsync(string input, (string Label, string Tag, double Speed) edit, Action<double> progress, CancellationToken cancel)
    {
        if (edit.Speed == 1)
        {
            // A copied stream only fits its own kind of container (WMV into MP4 fails).
            var ext = MediaKinds.Extension(input);
            var muted = FileJobs.OutputFor(input, edit.Tag, ext);
            string[] faststart = ext is "mp4" or "mov" or "m4v" ? ["-movflags", "+faststart"] : [];
            await Ffmpeg.RunAsync(input, ["-an", "-c:v", "copy", .. faststart], muted, progress, cancel);
            return muted;
        }

        var output = FileJobs.OutputFor(input, edit.Tag, "mp4");

        var speed = edit.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Ffmpeg.RunAsync(input,
            ["-vf", $"setpts=PTS/{speed}", "-af", $"atempo={speed}",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"],
            output, progress, cancel, stretch: 1 / edit.Speed);
        return output;
    }

    private static async Task<string?> CompressOneAsync(string input, int level, Action<double> progress, CancellationToken cancel)
    {
        var kind = MediaKinds.Of(input);
        string output;
        if (kind == MediaKind.Image)
        {
            var image = await LoadAnyAsync(input, cancel);
            if (ImageIO.HasAlpha(image))
            {
                // JPEG would lose the transparency; lossy WebP keeps it.
                if (!File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)) && !await ToolDeps.EnsureAsync(Int32Rect.Empty, ToolDeps.Ffmpeg))
                    return null;
                output = FileJobs.OutputFor(input, " (küçük)", "webp");
                await Ffmpeg.RunAsync(input, ["-c:v", "libwebp", "-quality", level == 0 ? "80" : "60", "-frames:v", "1"], output, progress, cancel);
            }
            else
            {
                output = FileJobs.OutputFor(input, " (küçük)", "jpg");
                ImageIO.Save(image, output, level == 0 ? 75 : 55);
            }
        }
        else if (kind == MediaKind.Video)
        {
            output = FileJobs.OutputFor(input, " (küçük)", "mp4");
            var crf = level switch { 0 => "26", 1 => "30", _ => "34" };
            string[] scale = level == 2 ? ["-vf", "scale='min(1280,iw)':-2"] : [];
            await Ffmpeg.RunAsync(input, [.. scale, "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", level == 0 ? "128k" : "96k", "-movflags", "+faststart"], output, progress, cancel);
        }
        else
        {
            output = FileJobs.OutputFor(input, " (küçük)", "mp3");
            await Ffmpeg.RunAsync(input, ["-vn", "-c:a", "libmp3lame", "-b:a", level switch { 0 => "128k", 1 => "96k", _ => "64k" }], output, progress, cancel);
        }

        // Already well compressed: a "smaller" copy that is bigger helps nobody.
        if (new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            ToolRunner.TryDelete(output);
            throw new InvalidOperationException("zaten yeterince küçük, daha da küçülmedi.");
        }
        return output;
    }

    /// <summary>
    /// Rough output size per level for the files together, or null when it cannot be said (images, or
    /// a file Windows reports no length for). Audio is a fixed bitrate, so close. Video quality is fixed
    /// rather than size, so it is bits per pixel at a typical frame rate, never more than the original.
    /// </summary>
    private static async Task<long?[]?> EstimateCompressedAsync(List<string> files, MediaKind kind, int levels)
    {
        // Each file is asked separately; past a handful the card would wait too long.
        if (kind == MediaKind.Image || files.Count > 20)
            return null;
        int[] audioKbps = kind == MediaKind.Video ? [128, 96, 96] : [128, 96, 64];
        // crf 26, 30, 34 at 30 fps; measured on phone and screen clips, real sizes came within about 1.5x.
        double[] bitsPerPixel = [0.055, 0.035, 0.022];
        var totals = new double[levels];
        try
        {
            foreach (var file in files)
            {
                var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
                var original = new FileInfo(file).Length;
                if (kind == MediaKind.Video)
                {
                    var video = await storage.Properties.GetVideoPropertiesAsync();
                    var seconds = video.Duration.TotalSeconds;
                    if (seconds <= 0 || video.Width == 0 || video.Height == 0)
                        return null;
                    // Phone clips are often stored sideways with a rotation flag; ffmpeg turns them upright first.
                    var width = video.Orientation is Windows.Storage.FileProperties.VideoOrientation.Rotate90 or Windows.Storage.FileProperties.VideoOrientation.Rotate270
                        ? video.Height : video.Width;
                    for (var i = 0; i < levels; i++)
                    {
                        // "En küçük" also scales down to 1280 wide.
                        var scale = i == 2 && width > 1280 ? 1280.0 / width : 1;
                        var pixels = video.Width * scale * video.Height * scale;
                        var bytes = (pixels * 30 * bitsPerPixel[i] + audioKbps[i] * 1000) * seconds / 8;
                        totals[i] += Math.Min(bytes, original);
                    }
                }
                else
                {
                    var seconds = (await storage.Properties.GetMusicPropertiesAsync()).Duration.TotalSeconds;
                    if (seconds <= 0)
                        return null;
                    for (var i = 0; i < levels; i++)
                        totals[i] += Math.Min(audioKbps[i] * 1000 * seconds / 8, original);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
        return [.. totals.Select(t => (long?)t)];
    }

    private static async Task<string?> ResizeOneAsync(string input, MediaKind kind, int size, Action<double> progress, CancellationToken cancel)
    {
        var tag = size < 0 ? $" (%{-size})" : kind == MediaKind.Video ? $" ({size}p)" : $" ({size})";
        if (kind == MediaKind.Video)
        {
            var output = FileJobs.OutputFor(input, tag, "mp4");
            var filter = size < 0 ? $"scale=trunc(iw*{-size}/200)*2:-2" : $"scale=-2:'min({size},ih)'";
            await Ffmpeg.RunAsync(input, ["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart"], output, progress, cancel);
            return output;
        }

        var ext = MediaKinds.Extension(input) switch { "jpeg" or "jfif" => "jpg", var e => e };
        if (MediaKinds.NativeImages.Contains(ext) && ImageIO.TryLoad(input) is { } image)
        {
            var (w, h) = Fit(image.PixelWidth, image.PixelHeight, size);
            if (w >= image.PixelWidth)
                throw new InvalidOperationException("zaten bu boyuttan küçük.");
            var output = FileJobs.OutputFor(input, tag, ext);
            ImageIO.Save(ImageIO.Resize(image, w, h), output);
            return output;
        }

        var other = FileJobs.OutputFor(input, tag, ext);
        var scale = size < 0 ? $"scale=iw*{-size}/100:-1:flags=lanczos" : $"scale='if(gt(iw,ih),min({size},iw),-1)':'if(gt(iw,ih),-1,min({size},ih))':flags=lanczos";
        await Ffmpeg.RunAsync(input, ["-vf", scale, "-frames:v", "1"], other, progress, cancel);
        return other;
    }

    internal static (int W, int H) Fit(int width, int height, int size)
    {
        var factor = size < 0 ? -size / 100.0 : Math.Min(1, (double)size / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
    }

    /// <summary>WPF first; formats it cannot read go through ffmpeg into a temporary PNG.</summary>
    private static async Task<BitmapSource> LoadAnyAsync(string input, CancellationToken cancel)
    {
        if (ImageIO.TryLoad(input) is { } image)
            return image;
        if (!File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)) && !await ToolDeps.EnsureAsync(Int32Rect.Empty, ToolDeps.Ffmpeg))
            throw new OperationCanceledException();

        var temp = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.png");
        try
        {
            await Ffmpeg.RunAsync(input, ["-frames:v", "1"], temp, null, cancel);
            return ImageIO.TryLoad(temp) ?? throw new NotSupportedException("resim okunamadı.");
        }
        finally
        {
            ToolRunner.TryDelete(temp);
        }
    }

    private static async Task ImagesToOnePdfAsync(List<string> files, Int32Rect anchor)
    {
        await FileJobs.RunAsync("convert", "PDF yapılıyor", [files[0]], anchor, async (first, progress, cancel) =>
        {
            var pages = new List<(byte[], int, int)>();
            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var image = await LoadAnyAsync(files[i], cancel);
                pages.Add((ImageIO.Jpeg(image), image.PixelWidth, image.PixelHeight));
                progress(100.0 * (i + 1) / files.Count);
            }
            var output = FileJobs.OutputFor(first, $" ve {files.Count - 1} resim", "pdf");
            ImageIO.WritePdf(output, pages);
            return output;
        });
    }

    /// <summary>Every page at twice its size in points, so text stays sharp; "belge-1.png", "belge-2.png"...</summary>
    private static async Task<string?> PdfToImagesAsync(string input, string to, CancellationToken cancel)
    {
        var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(input);
        var pdf = await PdfDocument.LoadFromFileAsync(storage);
        string? first = null;
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(i);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)(page.Size.Width * 2),
                DestinationHeight = (uint)(page.Size.Height * 2),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            var image = BitmapFrame.Create(stream.AsStream(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var output = FileJobs.OutputFor(input, $"-{i + 1}", to);
            ImageIO.Save(image, output);
            if (first is null)
                first = output;
            else
                FileJobs.AlsoMade("convert", output);
        }
        return first;
    }
}
