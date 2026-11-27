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
        if (await FilesOfOneKindAsync(target, T("Dönüştürülecek dosyaları seç", "Choose files to convert"), anchor) is not { } files)
            return;

        var kind = MediaKinds.Of(files[0]);
        var choices = new List<(string Label, Func<Task> Run)>();
        foreach (var to in MediaKinds.ConvertTargets(files[0]))
            choices.Add((TargetLabel(to, kind), () => ConvertToAsync(files, kind, to, anchor)));
        if (kind == MediaKind.Image && files.Count > 1)
            choices.Add((T("Tek PDF", "One PDF"), () => ImagesToOnePdfAsync(files, anchor)));
        if (kind == MediaKind.Video)
        {
            choices.Add((T("Ses (kayıpsız)", "Audio (lossless)"), () => ExtractAudioAsync(files, anchor)));
            foreach (var edit in VideoEdits)
                choices.Add((edit.Label, async () =>
                {
                    if (await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
                        await FileJobs.RunAsync("convert", T("Dönüştürülüyor", "Converting"), files, anchor, (input, progress, cancel) => EditVideoAsync(input, edit, progress, cancel));
                }));
        }

        var pick = await ResultCard.ChooseAsync(T("Dönüştür", "Convert"), Describe(files), choices.Select(c => c.Label).ToList(), anchor);
        if (pick >= 0)
            await choices[pick].Run();
    }

    /// <summary>What a target is called on the card; "…" where a settings card follows.</summary>
    private static string TargetLabel(string to, MediaKind kind) => (to, kind) switch
    {
        ("png", MediaKind.Video) => T("Kare (PNG)", "Frame (PNG)"),
        ("gif", MediaKind.Video) => "GIF…",
        ("mp3" or "m4a" or "wav", MediaKind.Video) => T($"Ses: {to.ToUpperInvariant()}", $"Audio: {to.ToUpperInvariant()}"),
        (_, MediaKind.Pdf) => $"{to.ToUpperInvariant()}…",
        _ => to.ToUpperInvariant(),
    };

    /// <param name="Width">0 keeps the video's own width.</param>
    private sealed record GifOptions(int Fps, int Width);

    /// <param name="Pages">As typed, such as "1-3, 5"; read against each PDF's own page count.</param>
    private sealed record PdfOptions(string Pages, double Scale);

    private static async Task ConvertToAsync(List<string> files, MediaKind kind, string to, Int32Rect anchor)
    {
        GifOptions? gif = null;
        PdfOptions? pdf = null;
        if (kind == MediaKind.Video && to == "gif" && (gif = await AskGifAsync(files, anchor)) is null)
            return;
        if (kind == MediaKind.Pdf && (pdf = await AskPdfAsync(files, anchor)) is null)
            return;
        if (!NeedsFfmpeg(kind, files, to) || await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            await FileJobs.RunAsync("convert", T("Dönüştürülüyor", "Converting"), files, anchor, (input, progress, cancel) => ConvertOneAsync(input, to, gif, pdf, progress, cancel));
    }

    private static readonly int[] GifRates = [8, 10, 12, 15, 20, 25];
    private static readonly int[] GifWidths = [320, 480, 640, 800, 0];

    private static async Task<GifOptions?> AskGifAsync(List<string> files, Int32Rect anchor)
    {
        var form = new OptionsForm();
        var fps = form.Chips(T("Kare hızı (fps)", "Frame rate (fps)"), GifRates.Select(r => r.ToString()).ToList(), 2);
        var width = form.Chips(T("Genişlik", "Width"), GifWidths.Select(w => w > 0 ? $"{w} px" : T("Orijinal", "Original")).ToList(), 2);
        form.Hint(T("Kare hızı ve genişlik arttıkça GIF akıcılaşır ama boyutu hızla büyür. Video daha dar ise genişletilmez.",
            "A higher frame rate and width make the GIF smoother but much bigger. A narrower video isn't widened."));
        return await form.ShowAsync("GIF", Describe(files), T("Dönüştür", "Convert"), anchor) ? new GifOptions(GifRates[fps()], GifWidths[width()]) : null;
    }

    private static readonly (string Label, double Scale)[] PdfScales =
        [(T("1x (ekran)", "1x (screen)"), 1), (T("2x (net)", "2x (sharp)"), 2), (T("3x (baskı)", "3x (print)"), 3)];

    private static async Task<PdfOptions?> AskPdfAsync(List<string> files, Int32Rect anchor)
    {
        var count = await PageCountAsync(files[0]);
        var form = new OptionsForm();
        var pages = form.Text(T("Sayfalar", "Pages"), count > 0 ? $"1-{count}" : "1-",
            (count > 0 ? T($"{count} sayfa. ", $"{count} pages. ") : "") + T("Örn: 1-3, 5, 8- (8'den sona kadar).", "E.g. 1-3, 5, 8- (8 to the end).")
            + (files.Count > 1 ? T(" Her PDF'e aynı aralık uygulanır.", " The same range goes to every PDF.") : ""));
        var scale = form.Chips(T("Çözünürlük", "Resolution"), PdfScales.Select(s => s.Label).ToList(), 1);
        if (!await form.ShowAsync(T("PDF'ten resim", "PDF to images"), Describe(files), T("Dönüştür", "Convert"), anchor))
            return null;
        if (PageRange.Parse(pages.Text, count > 0 ? count : 9999) is not { Count: > 0 })
        {
            ResultCard.ForMessage(T("PDF'ten resim", "PDF to images"), T($"\"{pages.Text}\" okunamadı ya da bu aralıkta sayfa yok.", $"Couldn't read \"{pages.Text}\", or no pages are in that range."), anchor).ShowNear();
            return null;
        }
        return new PdfOptions(pages.Text, PdfScales[scale()].Scale);
    }

    private static async Task<int> PageCountAsync(string pdf)
    {
        try
        {
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdf);
            return (int)(await PdfDocument.LoadFromFileAsync(storage)).PageCount;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>The sound as it is in the video, copied without re-encoding into the file type its codec belongs in.</summary>
    private static async Task ExtractAudioAsync(List<string> files, Int32Rect anchor)
    {
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            return;
        await FileJobs.RunAsync("convert", T("Ses çıkarılıyor", "Extracting audio"), files, anchor, async (input, progress, cancel) =>
        {
            var info = await Ffmpeg.ProbeAsync(input, cancel);
            if (!info.Audio)
                throw new InvalidOperationException(T("bu videoda ses yok.", "this video has no sound."));
            var ext = info.AudioCodec?.ToLowerInvariant() switch
            {
                "aac" or "alac" => "m4a",
                "mp3" => "mp3",
                "opus" => "opus",
                "vorbis" => "ogg",
                "flac" => "flac",
                var c when c?.StartsWith("pcm_") == true => "wav",
                _ => "mka",
            };
            var output = FileJobs.OutputFor(input, "", ext);
            await Ffmpeg.RunAsync(input, ["-map", "0:a:0", "-vn", "-c:a", "copy"], output, progress, cancel);
            return output;
        });
    }

    public static async Task CompressAsync(IntPtr target, Int32Rect anchor)
    {
        if (await FilesOfOneKindAsync(target, T("Sıkıştırılacak dosyaları seç", "Choose files to compress"), anchor) is not { } files)
            return;

        if (MediaKinds.Of(files[0]) == MediaKind.Pdf)
        {
            ResultCard.ForMessage(T("Sıkıştır", "Compress"), T("PDF sıkıştırma henüz yok. Resim, video ve ses dosyaları sıkıştırılabilir.", "PDF compression isn't available yet. Images, video and audio can be compressed."), anchor).ShowNear();
            return;
        }

        var kind = MediaKinds.Of(files[0]);
        string[] levels = kind == MediaKind.Image ? [T("Dengeli", "Balanced"), T("Daha küçük", "Smaller")] : [T("Dengeli", "Balanced"), T("Daha küçük", "Smaller"), T("En küçük", "Smallest")];
        var estimates = await EstimateCompressedAsync(files, kind, levels.Length);
        var labels = levels.Select((l, i) => estimates?[i] is { } bytes ? $"{l} · ~{MediaKinds.Size(bytes)}" : l).ToList();
        labels.Add(T("Hedef boyut…", "Target size…"));
        labels.Add(T("Özel ayar…", "Custom…"));
        var message = estimates is null ? Describe(files)
            : T($"{Describe(files)}\nTahmini boyutlar{(kind == MediaKind.Video ? "; videoda içeriğe göre epey değişebilir" : "")}.", $"{Describe(files)}\nEstimated sizes{(kind == MediaKind.Video ? "; for video they vary a lot with the content" : "")}.");
        var pick = await ResultCard.ChooseAsync(T("Sıkıştır", "Compress"), message, labels, anchor);
        if (pick < 0)
            return;

        Func<string, Action<double>, CancellationToken, Task<string?>> work;
        if (pick == levels.Length)
        {
            if (await AskTargetAsync(files, anchor) is not { } bytes)
                return;
            work = (input, progress, cancel) => CompressToTargetAsync(input, bytes, progress, cancel);
        }
        else if (pick == levels.Length + 1)
        {
            if (await AskCustomAsync(files, kind, anchor) is not { } custom)
                return;
            work = (input, progress, cancel) => CompressCustomAsync(input, custom, progress, cancel);
        }
        else
        {
            work = (input, progress, cancel) => CompressOneAsync(input, pick, progress, cancel);
        }

        if (kind != MediaKind.Image || files.Any(f => !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f))))
        {
            if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
                return;
        }
        await FileJobs.RunAsync("compress", T("Sıkıştırılıyor", "Compressing"), files, anchor, work);
    }

    private static readonly string[] TargetPresets = ["1 MB", "2 MB", "5 MB", "10 MB", "25 MB"];

    /// <summary>The most each output may weigh, in bytes; null when cancelled or unreadable (after a card).</summary>
    private static async Task<long?> AskTargetAsync(List<string> files, Int32Rect anchor)
    {
        var form = new OptionsForm();
        // A first guess: half the first file, in round megabytes when it is that big.
        var half = new FileInfo(files[0]).Length / 2.0 / (1024 * 1024);
        var guess = half >= 1 ? $"{Math.Floor(half)}" : $"{Math.Max(10, (int)(half * 1024))} KB";
        System.Windows.Controls.TextBox? box = null;
        form.Chips(T("Hazır", "Presets"), TargetPresets, -1, i => { box!.Text = TargetPresets[i].Replace(" MB", ""); box.Focus(); box.SelectAll(); });
        box = form.Text(T("En fazla (MB)", "At most (MB)"), guess,
            T("KB de yazabilirsin: 800 KB. ", "KB works too: 800 KB. ")
            + (files.Count > 1 ? T("Her dosya ayrı ayrı bu boyuta indirilir. ", "Each file is brought under this size on its own. ") : "")
            + T("Resimde kalite, gerekirse boyut düşürülür; videoda bit hızı ve gerekirse çözünürlük.", "Images lose quality, then size if needed; video loses bitrate, then resolution if needed."));
        if (!await form.ShowAsync(T("Hedef boyut", "Target size"), Describe(files), T("Sıkıştır", "Compress"), anchor))
            return null;
        if (SizeTarget.ParseBytes(box.Text) is not { } bytes || bytes < 10 * 1024)
        {
            ResultCard.ForMessage(T("Hedef boyut", "Target size"), T($"\"{box.Text}\" geçerli bir boyut değil (en az 10 KB).", $"\"{box.Text}\" isn't a valid size (10 KB at least)."), anchor).ShowNear();
            return null;
        }
        return bytes;
    }

    /// <param name="Quality">1–100, higher is better: JPEG/WebP quality, or mapped to a CRF for video.</param>
    /// <param name="Height">Video only: the height to bring it down to, 0 to keep.</param>
    /// <param name="AudioKbps">0 drops the sound (video) .</param>
    private sealed record CustomCompress(int Quality, int Height, int AudioKbps, bool Mono);

    private static readonly int[] VideoHeights = [0, 1080, 720, 480];
    private static readonly int[] VideoAudio = [64, 96, 128, 192, 0];
    private static readonly int[] AudioRates = [32, 48, 64, 96, 128, 160, 192, 256, 320];

    /// <summary>CRF 40 at quality 0 to CRF 18 at 100: x264's useful range, worst to near-lossless.</summary>
    private static int Crf(int quality) => (int)Math.Round(40 - quality * 0.22);

    private static async Task<CustomCompress?> AskCustomAsync(List<string> files, MediaKind kind, Int32Rect anchor)
    {
        var form = new OptionsForm();
        CustomCompress Result(Func<int>? quality, Func<int>? height, Func<int>? audio, Func<int>? mono) => new(
            quality?.Invoke() ?? 0,
            height is null ? 0 : VideoHeights[height()],
            audio is null ? 0 : kind == MediaKind.Video ? VideoAudio[audio()] : AudioRates[audio()],
            mono?.Invoke() == 1);

        Func<int>? q = null, h = null, a = null, m = null;
        switch (kind)
        {
            case MediaKind.Image:
                q = form.Slider(T("Kalite", "Quality"), 10, 95, 75, v => v.ToString());
                form.Hint(T("60–80 arası çoğu fotoğrafta farkı göze batmadan küçültür. Saydam resimler WebP olur.", "60–80 shrinks most photos without a visible difference. Transparent images become WebP."));
                break;
            case MediaKind.Video:
                q = form.Slider(T("Kalite", "Quality"), 0, 100, 55, v => $"{v} (CRF {Crf(v)})");
                h = form.Chips(T("Çözünürlük", "Resolution"), VideoHeights.Select(x => x > 0 ? $"{x}p" : T("Orijinal", "Original")).ToList(), 0);
                a = form.Chips(T("Ses", "Audio"), VideoAudio.Select(x => x > 0 ? $"{x} kbps" : T("Sessiz", "None")).ToList(), 2);
                form.Hint(T("Kalite düştükçe dosya küçülür; 50 civarı paylaşmak için iyi bir denge. Video daha küçükse büyütülmez.", "Lower quality, smaller file; around 50 is a good balance for sharing. A smaller video isn't enlarged."));
                break;
            default:
                a = form.Chips(T("Bit hızı", "Bitrate"), AudioRates.Select(x => $"{x} kbps").ToList(), 4);
                m = form.Chips(T("Kanal", "Channels"), [T("Stereo", "Stereo"), T("Mono", "Mono")], 0);
                form.Hint(T("Konuşma için 48–64 kbps mono yeterli, müzik için 128 kbps ve üstü.", "48–64 kbps mono is enough for speech, 128 kbps and up for music."));
                break;
        }
        return await form.ShowAsync(T("Özel sıkıştırma", "Custom compression"), Describe(files), T("Sıkıştır", "Compress"), anchor) ? Result(q, h, a, m) : null;
    }

    /// <summary>For a step that turns out to need ffmpeg after the up-front check passed (a transparent PNG becomes WebP).</summary>
    private static async Task NeedFfmpegAsync()
    {
        if (!File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)) && !await ToolDeps.EnsureAsync(Int32Rect.Empty, ToolDeps.Ffmpeg))
            throw new InvalidOperationException(T("saydam resim için ffmpeg gerekli.", "a transparent image needs ffmpeg."));
    }

    private static async Task<string?> CompressCustomAsync(string input, CustomCompress custom, Action<double> progress, CancellationToken cancel)
    {
        var kind = MediaKinds.Of(input);
        var tag = T(" (küçük)", " (small)");
        string output;
        if (kind == MediaKind.Image)
        {
            var image = await LoadAnyAsync(input, cancel);
            if (ImageIO.HasAlpha(image))
            {
                await NeedFfmpegAsync();
                output = FileJobs.OutputFor(input, tag, "webp");
                await Ffmpeg.RunAsync(input, ["-c:v", "libwebp", "-quality", $"{custom.Quality}", "-frames:v", "1"], output, progress, cancel);
            }
            else
            {
                output = FileJobs.OutputFor(input, tag, "jpg");
                ImageIO.Save(image, output, custom.Quality);
            }
        }
        else if (kind == MediaKind.Video)
        {
            output = FileJobs.OutputFor(input, tag, "mp4");
            string[] scale = custom.Height > 0 ? ["-vf", $"scale=-2:'min({custom.Height},ih)'"] : [];
            string[] audio = custom.AudioKbps > 0 ? ["-c:a", "aac", "-b:a", $"{custom.AudioKbps}k"] : ["-an"];
            await Ffmpeg.RunAsync(input, [.. scale, "-c:v", "libx264", "-preset", "medium", "-crf", $"{Crf(custom.Quality)}", "-pix_fmt", "yuv420p", .. audio, "-movflags", "+faststart"], output, progress, cancel);
        }
        else
        {
            output = FileJobs.OutputFor(input, tag, "mp3");
            string[] mono = custom.Mono ? ["-ac", "1"] : [];
            await Ffmpeg.RunAsync(input, ["-vn", .. mono, "-c:a", "libmp3lame", "-b:a", $"{custom.AudioKbps}k"], output, progress, cancel);
        }
        return NotBigger(input, output);
    }

    /// <summary>The output, unless it came out no smaller than the input: then it is removed and the file reported as already small.</summary>
    private static string NotBigger(string input, string output)
    {
        if (new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            ToolRunner.TryDelete(output);
            throw new InvalidOperationException(T("zaten yeterince küçük, daha da küçülmedi.", "already small enough, it didn't get any smaller."));
        }
        return output;
    }

    /// <summary>Brings one file under the size: images by quality then pixels, video and audio by bitrate.</summary>
    private static async Task<string?> CompressToTargetAsync(string input, long bytes, Action<double> progress, CancellationToken cancel)
    {
        if (new FileInfo(input).Length <= bytes)
            throw new InvalidOperationException(T($"zaten {MediaKinds.Size(bytes)} altında.", $"already under {MediaKinds.Size(bytes)}."));
        var tag = $" (≤{MediaKinds.Size(bytes)})";
        return MediaKinds.Of(input) switch
        {
            MediaKind.Image => await ImageToTargetAsync(input, bytes, tag, progress, cancel),
            MediaKind.Video => await VideoToTargetAsync(input, bytes, tag, progress, cancel),
            _ => await AudioToTargetAsync(input, bytes, tag, progress, cancel),
        };
    }

    /// <summary>
    /// The best quality that fits, found by halving the range; when even the lowest doesn't fit, fewer
    /// pixels and again. JPEG is encoded in memory; transparent images go through WebP.
    /// </summary>
    private static async Task<string> ImageToTargetAsync(string input, long bytes, string tag, Action<double> progress, CancellationToken cancel)
    {
        var image = await LoadAnyAsync(input, cancel);
        var webp = ImageIO.HasAlpha(image);
        if (webp)
            await NeedFfmpegAsync();
        var output = FileJobs.OutputFor(input, tag, webp ? "webp" : "jpg");
        var temp = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.png");
        try
        {
            async Task<long> Encode(BitmapSource source, int quality)
            {
                cancel.ThrowIfCancellationRequested();
                if (!webp)
                {
                    var data = ImageIO.Jpeg(source, quality);
                    await File.WriteAllBytesAsync(output, data, cancel);
                    return data.Length;
                }
                await Ffmpeg.RunAsync(temp, ["-c:v", "libwebp", "-quality", $"{quality}", "-frames:v", "1"], output, null, cancel);
                return new FileInfo(output).Length;
            }

            var source = image;
            for (var round = 0; round < 6; round++)
            {
                if (webp)
                    ImageIO.Save(source, temp);
                int low = 10, high = 92, best = -1;
                long smallest = long.MaxValue;
                while (low <= high)
                {
                    var mid = (low + high) / 2;
                    var size = await Encode(source, mid);
                    smallest = Math.Min(smallest, size);
                    if (size <= bytes)
                    {
                        best = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }
                progress(100.0 * (round + 1) / 6);
                if (best >= 0)
                {
                    // The last try may have been a bigger one that didn't fit.
                    if (new FileInfo(output).Length > bytes)
                        await Encode(source, best);
                    return output;
                }
                // Bytes go roughly with pixels: shrink by the square root of how far off it is.
                var factor = Math.Clamp(Math.Sqrt((double)bytes / smallest) * 0.9, 0.3, 0.9);
                var w = Math.Max(1, (int)(source.PixelWidth * factor));
                var h = Math.Max(1, (int)(source.PixelHeight * factor));
                if (w < 16 || h < 16)
                    break;
                source = ImageIO.Resize(source, w, h);
            }
        }
        catch
        {
            ToolRunner.TryDelete(output);
            throw;
        }
        finally
        {
            ToolRunner.TryDelete(temp);
        }
        ToolRunner.TryDelete(output);
        throw new InvalidOperationException(T("bu boyuta sığdırılamadı.", "couldn't fit it in that size."));
    }

    /// <summary>
    /// A bitrate worked out from the length, a lower height when that bitrate is too thin for the
    /// picture, and a second or third pass with less when the first comes out too big.
    /// </summary>
    private static async Task<string> VideoToTargetAsync(string input, long bytes, string tag, Action<double> progress, CancellationToken cancel)
    {
        var info = await Ffmpeg.ProbeAsync(input, cancel);
        var seconds = info.Duration.TotalSeconds;
        if (seconds <= 0)
            throw new InvalidOperationException(T("videonun süresi okunamadı.", "couldn't read the video's length."));
        if (SizeTarget.Bitrates(bytes, seconds, info.Audio) is not { } rates)
            throw new InvalidOperationException(T($"bu uzunlukta en az {MediaKinds.Size(SizeTarget.Smallest(seconds, info.Audio))} gerekir.", $"a video this long needs at least {MediaKinds.Size(SizeTarget.Smallest(seconds, info.Audio))}."));

        var (video, audio) = rates;
        var output = FileJobs.OutputFor(input, tag, "mp4");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var height = SizeTarget.Height(info.Width, info.Height, info.Fps, video);
            string[] scale = height > 0 && height < info.Height ? ["-vf", $"scale=-2:{height}"] : [];
            string[] sound = info.Audio ? ["-c:a", "aac", "-b:a", $"{audio}k"] : ["-an"];
            await Ffmpeg.RunAsync(input, [.. scale, "-c:v", "libx264", "-preset", "medium", "-b:v", $"{video}k", "-maxrate", $"{video * 3 / 2}k", "-bufsize", $"{video * 2}k", "-pix_fmt", "yuv420p", .. sound, "-movflags", "+faststart"],
                output, p => progress((attempt * 100 + p) / (attempt + 1)), cancel);
            var size = new FileInfo(output).Length;
            if (size <= bytes)
                return output;
            video = (int)(video * (double)bytes / size * 0.92);
            if (video < 60)
                break;
        }
        ToolRunner.TryDelete(output);
        throw new InvalidOperationException(T("bu boyuta sığdırılamadı.", "couldn't fit it in that size."));
    }

    private static async Task<string> AudioToTargetAsync(string input, long bytes, string tag, Action<double> progress, CancellationToken cancel)
    {
        var info = await Ffmpeg.ProbeAsync(input, cancel);
        var seconds = info.Duration.TotalSeconds;
        if (seconds <= 0)
            throw new InvalidOperationException(T("sesin süresi okunamadı.", "couldn't read the length."));
        var kbps = (int)(bytes * 8 / 1000.0 / seconds * 0.96);
        if (kbps < 8)
            throw new InvalidOperationException(T($"bu uzunlukta en az {MediaKinds.Size((long)(8 * 1000 / 8.0 * seconds / 0.96))} gerekir.", $"audio this long needs at least {MediaKinds.Size((long)(8 * 1000 / 8.0 * seconds / 0.96))}."));
        kbps = Math.Min(kbps, 320);
        var output = FileJobs.OutputFor(input, tag, "mp3");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Low bitrates sound much better in mono.
            string[] mono = kbps < 64 ? ["-ac", "1"] : [];
            // MP3 at 44.1/48 kHz starts at 32 kbps; 22.05 kHz goes down to 8.
            string[] rate = kbps < 32 ? ["-ar", "22050"] : [];
            await Ffmpeg.RunAsync(input, ["-vn", .. mono, .. rate, "-c:a", "libmp3lame", "-b:a", $"{kbps}k"], output, progress, cancel);
            var size = new FileInfo(output).Length;
            if (size <= bytes)
                return output;
            kbps = (int)(kbps * (double)bytes / size * 0.95);
            if (kbps < 8)
                break;
        }
        ToolRunner.TryDelete(output);
        throw new InvalidOperationException(T("bu boyuta sığdırılamadı.", "couldn't fit it in that size."));
    }

    public static async Task ResizeAsync(IntPtr target, Int32Rect anchor)
    {
        if (await FilesOfOneKindAsync(target, T("Boyutlandırılacak dosyaları seç", "Choose files to resize"), anchor) is not { } files)
            return;

        var kind = MediaKinds.Of(files[0]);
        if (kind is not (MediaKind.Image or MediaKind.Video))
        {
            ResultCard.ForMessage(T("Boyutlandır", "Resize"), T("Sadece resim ve video boyutlandırılabilir.", "Only images and video can be resized."), anchor).ShowNear();
            return;
        }

        // Pictures get the editor: crop, rotate, flip, straighten, exact size and format.
        if (kind == MediaKind.Image)
        {
            await ImageEditWindow.OpenAsync(files, anchor);
            return;
        }

        // Each size is a height limit in pixels, or a negative percentage; then the turns and the crop.
        (string Label, int Size)[] sizes = [("1080p", 1080), ("720p", 720), ("480p", 480), (T("%50", "50%"), -50)];
        var choices = sizes.Select(s => s.Label).Concat(VideoTurns.Select(t => t.Label)).Append(T("Kırp…", "Crop…")).ToList();
        var pick = await ResultCard.ChooseAsync(T("Boyutlandır", "Resize"), Describe(files), choices, anchor);
        if (pick < 0)
            return;

        if (pick == choices.Count - 1)
        {
            // The trim editor already crops the picture, with a preview to draw on.
            TrimWindow.Open(files);
            return;
        }
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            return;
        if (pick >= sizes.Length)
        {
            var turn = VideoTurns[pick - sizes.Length];
            await FileJobs.RunAsync("resize", T("Döndürülüyor", "Rotating"), files, anchor, (input, progress, cancel) => TurnVideoAsync(input, turn.Filter, turn.Tag, progress, cancel));
            return;
        }
        var size = sizes[pick].Size;
        await FileJobs.RunAsync("resize", T("Boyutlandırılıyor", "Resizing"), files, anchor, (input, progress, cancel) => ResizeOneAsync(input, kind, size, progress, cancel));
    }

    private static readonly (string Label, string Filter, string Tag)[] VideoTurns =
    [
        (T("⟳ 90° sağa", "⟳ 90° right"), "transpose=clock", T(" (döndürüldü)", " (rotated)")),
        (T("⟲ 90° sola", "⟲ 90° left"), "transpose=cclock", T(" (döndürüldü)", " (rotated)")),
        ("180°", "hflip,vflip", T(" (döndürüldü)", " (rotated)")),
        (T("⇆ Yatay çevir", "⇆ Flip horizontal"), "hflip", T(" (çevrildi)", " (flipped)")),
        (T("⇅ Dikey çevir", "⇅ Flip vertical"), "vflip", T(" (çevrildi)", " (flipped)")),
    ];

    private static async Task<string?> TurnVideoAsync(string input, string filter, string tag, Action<double> progress, CancellationToken cancel)
    {
        var output = FileJobs.OutputFor(input, tag, "mp4");
        await Ffmpeg.RunAsync(input, ["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"], output, progress, cancel);
        return output;
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
            ResultCard.ForMessage(T("Desteklenmeyen dosya", "Unsupported file"), T($"{Path.GetFileName(files[0])}: resim, video, ses ya da PDF seç.", $"{Path.GetFileName(files[0])}: pick an image, video, audio file or PDF."), anchor).ShowNear();
            return null;
        }
        return files.Where(f => MediaKinds.Of(f) == kind).ToList();
    }

    private static string Describe(List<string> files) => files.Count == 1
        ? $"{Path.GetFileName(files[0])} · {MediaKinds.Size(new FileInfo(files[0]).Length)}"
        : $"{T($"{files.Count} dosya", $"{files.Count} files")} · {MediaKinds.Size(files.Sum(f => new FileInfo(f).Length))}";

    private static bool NeedsFfmpeg(MediaKind kind, List<string> files, string to) => kind switch
    {
        MediaKind.Image => to is "webp" or "ico" || files.Any(f => !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f))),
        MediaKind.Pdf => false,
        _ => true,
    };

    private static async Task<string?> ConvertOneAsync(string input, string to, GifOptions? gif, PdfOptions? pdf, Action<double> progress, CancellationToken cancel)
    {
        var kind = MediaKinds.Of(input);
        if (kind == MediaKind.Pdf)
            return await PdfToImagesAsync(input, to, pdf ?? new PdfOptions("1-", 2), progress, cancel);

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
            "gif" => ["-vf", GifFilter(gif ?? new GifOptions(12, 800)), "-loop", "0"],
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

    /// <summary>One palette made for the whole clip, only redrawing what moves between frames.</summary>
    private static string GifFilter(GifOptions gif)
    {
        var scale = gif.Width > 0 ? $"scale='min({gif.Width},iw)':-1:flags=lanczos," : "";
        return $"fps={gif.Fps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";
    }

    // Speed 1 means the audio is dropped and the picture copied untouched.
    private static readonly (string Label, string Tag, double Speed)[] VideoEdits =
        [(T("Sesi kaldır", "Remove audio"), T(" (sessiz)", " (muted)"), 1), (T("2x hız", "2x speed"), " (2x)", 2), (T("0.5x hız", "0.5x speed"), " (0.5x)", 0.5)];

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
                output = FileJobs.OutputFor(input, T(" (küçük)", " (small)"), "webp");
                await Ffmpeg.RunAsync(input, ["-c:v", "libwebp", "-quality", level == 0 ? "80" : "60", "-frames:v", "1"], output, progress, cancel);
            }
            else
            {
                output = FileJobs.OutputFor(input, T(" (küçük)", " (small)"), "jpg");
                ImageIO.Save(image, output, level == 0 ? 75 : 55);
            }
        }
        else if (kind == MediaKind.Video)
        {
            output = FileJobs.OutputFor(input, T(" (küçük)", " (small)"), "mp4");
            var crf = level switch { 0 => "26", 1 => "30", _ => "34" };
            string[] scale = level == 2 ? ["-vf", "scale='min(1280,iw)':-2"] : [];
            await Ffmpeg.RunAsync(input, [.. scale, "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", level == 0 ? "128k" : "96k", "-movflags", "+faststart"], output, progress, cancel);
        }
        else
        {
            output = FileJobs.OutputFor(input, T(" (küçük)", " (small)"), "mp3");
            await Ffmpeg.RunAsync(input, ["-vn", "-c:a", "libmp3lame", "-b:a", level switch { 0 => "128k", 1 => "96k", _ => "64k" }], output, progress, cancel);
        }

        // Already well compressed: a "smaller" copy that is bigger helps nobody.
        if (new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            ToolRunner.TryDelete(output);
            throw new InvalidOperationException(T("zaten yeterince küçük, daha da küçülmedi.", "already small enough, it didn't get any smaller."));
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
        var tag = size < 0 ? T($" (%{-size})", $" ({-size}%)") : kind == MediaKind.Video ? $" ({size}p)" : $" ({size})";
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
                throw new InvalidOperationException(T("zaten bu boyuttan küçük.", "already smaller than that."));
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
    internal static async Task<BitmapSource> LoadAnyAsync(string input, CancellationToken cancel)
    {
        if (ImageIO.TryLoad(input) is { } image)
            return image;
        if (!File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)) && !await ToolDeps.EnsureAsync(Int32Rect.Empty, ToolDeps.Ffmpeg))
            throw new OperationCanceledException();

        var temp = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.png");
        try
        {
            await Ffmpeg.RunAsync(input, ["-frames:v", "1"], temp, null, cancel);
            return ImageIO.TryLoad(temp) ?? throw new NotSupportedException(T("resim okunamadı.", "couldn't read the image."));
        }
        finally
        {
            ToolRunner.TryDelete(temp);
        }
    }

    private static async Task ImagesToOnePdfAsync(List<string> files, Int32Rect anchor)
    {
        await FileJobs.RunAsync("convert", T("PDF yapılıyor", "Making PDF"), [files[0]], anchor, async (first, progress, cancel) =>
        {
            var pages = new List<(byte[], int, int)>();
            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var image = await LoadAnyAsync(files[i], cancel);
                pages.Add((ImageIO.Jpeg(image), image.PixelWidth, image.PixelHeight));
                progress(100.0 * (i + 1) / files.Count);
            }
            var output = FileJobs.OutputFor(first, T($" ve {files.Count - 1} resim", $" and {files.Count - 1} images"), "pdf");
            ImageIO.WritePdf(output, pages);
            return output;
        });
    }

    /// <summary>The chosen pages at the chosen multiple of their size in points; "belge-1.png", "belge-2.png"...</summary>
    private static async Task<string?> PdfToImagesAsync(string input, string to, PdfOptions options, Action<double> progress, CancellationToken cancel)
    {
        var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(input);
        var pdf = await PdfDocument.LoadFromFileAsync(storage);
        var pages = PageRange.Parse(options.Pages, (int)pdf.PageCount) ?? [];
        if (pages.Count == 0)
            throw new InvalidOperationException(T($"bu aralıkta sayfa yok ({pdf.PageCount} sayfa).", $"no pages in that range ({pdf.PageCount} pages)."));
        string? first = null;
        for (var n = 0; n < pages.Count; n++)
        {
            cancel.ThrowIfCancellationRequested();
            var i = (uint)(pages[n] - 1);
            progress(100.0 * n / pages.Count);
            using var page = pdf.GetPage(i);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)(page.Size.Width * options.Scale),
                DestinationHeight = (uint)(page.Size.Height * options.Scale),
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
