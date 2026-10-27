using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Screen recording to MP4 (PLAN.md, section 7): pick a region, 3-2-1, then record until the stop button
/// or Esc. Frames are GDI grabs (cursor drawn in) fed through a MediaStreamSource into Windows' own
/// H.264 encoder, so no extra codec is needed. No audio and no GIF yet.
/// </summary>
internal static class ScreenRecorder
{
    private const int Fps = 30;

    public static async Task RunAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        // H.264 needs even dimensions.
        var r = region.ScreenRect;
        var rect = new Int32Rect(r.X, r.Y, r.Width & ~1, r.Height & ~1);
        if (rect.Width < 32 || rect.Height < 32)
        {
            ResultCard.ForMessage("Bölge çok küçük", "Kayıt için en az 32 × 32 piksellik bir bölge seç.", r).ShowNear();
            return;
        }

        using var stop = new CancellationTokenSource();
        using var frame = RegionFrame.Show(rect, RegionFrame.Ready);
        var badge = new Badge(rect) { AtScreenTop = true }.WithStop(stop.Cancel);
        badge.Show();
        for (var i = 3; i > 0 && !stop.IsCancellationRequested; i--)
        {
            badge.Text = $"Kayıt {i} sn sonra başlıyor";
            await Task.Delay(1000);
        }
        if (stop.IsCancellationRequested)
        {
            badge.Close();
            return;
        }

        Directory.CreateDirectory(AppPaths.RecordingDirectory);
        var file = Path.Combine(AppPaths.RecordingDirectory, $"Ekran kaydı {DateTime.Now:yyyy-MM-dd HHmmss}.mp4");
        frame.Color = RegionFrame.Live;
        var clock = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => badge.Text = $"Kaydediliyor {clock.Elapsed:mm\\:ss} · Esc";
        timer.Start();

        try
        {
            await Task.Run(() => RecordAsync(rect, file, stop.Token));
        }
        finally
        {
            timer.Stop();
            badge.Close();
        }

        var duration = clock.Elapsed;
        ToolRunner.Remember("record", file);
        ResultCard.ForMessage("Kayıt kaydedildi", $"{rect.Width} × {rect.Height} · {duration:mm\\:ss} · {Path.GetFileName(file)}", rect)
            .WithButton("Klasörde göster", () => ToolRunner.ShowInFolder(file))
            .WithButton("Kırp", () => TrimWindow.Open(file))
            .WithButton("Oynat", () => ToolRunner.OpenUrl(file), primary: true)
            .ShowNear();
    }

    private static async Task RecordAsync(Int32Rect rect, string file, CancellationToken stop)
    {
        using var grabber = new FrameGrabber(rect, drawCursor: true);
        var frames = new BlockingCollection<(byte[] Pixels, TimeSpan Time)>(boundedCapacity: Fps * 2);

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)rect.Width, (uint)rect.Height);
        input.FrameRate.Numerator = Fps;
        input.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            // Blocks the encoder's thread until the next frame; a null sample ends the stream.
            if (!frames.TryTake(out var frame, Timeout.Infinite))
            {
                e.Request.Sample = null;
                return;
            }
            var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Time);
            sample.Duration = TimeSpan.FromSeconds(1.0 / Fps);
            e.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p); // Auto fails with MediaStreamSource (0xC00D6D60); size and rate are set below anyway
        profile.Audio = null;
        profile.Video.Width = (uint)rect.Width;
        profile.Video.Height = (uint)rect.Height;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        // About 0.1 bit per pixel per frame: crisp text without huge files (≈6 Mbit/s at 1080p).
        profile.Video.Bitrate = (uint)Math.Clamp(rect.Width * rect.Height * Fps / 10, 1_000_000, 40_000_000);

        // Media Foundation wants a native WinRT stream; the .NET stream wrapper fails with some encoders.
        File.WriteAllBytes(file, []);
        var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
        using var stream = await storage.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Kodlayıcı hazırlanamadı ({prepared.FailureReason}).");

        var encoding = prepared.TranscodeAsync().AsTask();

        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / Fps);
        try
        {
            for (long n = 0; !stop.IsCancellationRequested && !Native.IsKeyDown(Native.VK_ESCAPE); n++)
            {
                var due = interval * n;
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                else if (wait < -interval)
                    continue; // Fell behind; skip this frame instead of piling up.

                var pixels = grabber.NewBuffer();
                grabber.Grab(pixels);
                FlipRows(pixels, grabber.Stride);
                frames.Add((pixels, clock.Elapsed));
            }
        }
        finally
        {
            frames.CompleteAdding();
        }

        await encoding;
    }

    /// <summary>The encoder reads uncompressed frames bottom-up, while the grabber delivers them top-down.</summary>
    private static void FlipRows(byte[] pixels, int stride)
    {
        var spare = new byte[stride];
        for (int top = 0, bottom = pixels.Length - stride; top < bottom; top += stride, bottom -= stride)
        {
            Buffer.BlockCopy(pixels, top, spare, 0, stride);
            Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            Buffer.BlockCopy(spare, 0, pixels, bottom, stride);
        }
    }
}
