using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// A file's frames and its sound's loudness for the trim timeline, made by ffmpeg in the background.
/// Frames and Peaks are only touched on the UI thread; <see cref="Changed"/> is raised there too.
/// </summary>
internal sealed class TrimStrip
{
    public readonly List<TimeSpan> Times = []; // ascending
    public readonly List<ImageSource> Frames = [];
    public float[]? Peaks;
    public float Loudest = 1;
    public int FrameWidth, FrameHeight; // in pixels, once the first frame is in

    public event Action? Changed;

    private static readonly SemaphoreSlim Gate = new(1, 1); // one file at a time, so a batch of files doesn't start a crowd of ffmpegs

    /// <summary>
    /// Fills the strip: the sound's peaks in one quick pass, then every <see cref="FilmStrip.Step"/>
    /// a frame, from keyframes only, all in one ffmpeg pass that pipes raw pixels back. Both run
    /// below normal priority so playback keeps the processor. Without ffmpeg there is no strip.
    /// </summary>
    public async Task FillAsync(string file, TimeSpan length, int height, Dispatcher ui, CancellationToken cancel)
    {
        if (length <= TimeSpan.Zero || !File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)))
            return;
        try
        {
            await Gate.WaitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var info = await Ffmpeg.ProbeAsync(file, cancel);
            if (info.Audio)
            {
                var raw = new MemoryStream();
                await RunAsync(file, [], ["-map", "0:a:0", "-vn", "-ac", "1", "-ar", Waveform.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1"],
                    async output => await output.CopyToAsync(raw, cancel), cancel);
                var peaks = Waveform.Peaks(raw.GetBuffer().AsSpan(0, (int)raw.Length));
                await ui.InvokeAsync(() =>
                {
                    Loudest = peaks.Length > 0 ? Math.Max(0.01f, peaks.Max()) : 1;
                    Peaks = peaks;
                    Changed?.Invoke();
                });
            }
            if (!info.Video)
                return;

            var aspect = info.Width > 0 && info.Height > 0 ? (double)info.Width / info.Height : 16 / 9.0;
            var h = height - height % 2;
            var w = Math.Clamp((int)Math.Round(h * aspect / 2) * 2, 2, h * 4);
            var step = FilmStrip.Step(length);
            var rate = (1 / step.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture);
            var size = w * h * 4;
            await RunAsync(file, ["-skip_frame", "nokey"],
                ["-map", "0:v:0", "-an", "-sn", "-vf", $"setpts=PTS-STARTPTS,fps={rate},scale={w}:{h},format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"],
                async output =>
                {
                    var buffer = new byte[size];
                    for (var k = 0; ; k++)
                    {
                        try
                        {
                            await output.ReadExactlyAsync(buffer, cancel);
                        }
                        catch (EndOfStreamException)
                        {
                            return;
                        }
                        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, w * 4);
                        image.Freeze();
                        var at = step * k;
                        _ = ui.InvokeAsync(() =>
                        {
                            FrameWidth = w;
                            FrameHeight = h;
                            Times.Add(at);
                            Frames.Add(image);
                            Changed?.Invoke();
                        }, DispatcherPriority.Background);
                    }
                }, cancel);
        }
        catch (Exception)
        {
            // The strip is a help, not the job: whatever goes wrong (ffmpeg, a frame that won't
            // decode, a closed window), what was made stays and the rest is left out.
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Runs ffmpeg below normal priority, handing its output to <paramref name="read"/>.</summary>
    private static async Task RunAsync(string input, string[] before, string[] args, Func<Stream, Task> read, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(ToolDeps.PathOf(ToolDeps.Ffmpeg))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", .. before, "-i", input, .. args])
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to change: it runs at normal priority.
        }
        using var kill = cancel.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        var drain = process.StandardError.ReadToEndAsync(CancellationToken.None); // or a chatty file would fill the pipe and stall it
        await Task.Run(() => read(process.StandardOutput.BaseStream), CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        await drain;
        cancel.ThrowIfCancellationRequested();
    }
}
