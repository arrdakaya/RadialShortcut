using System.IO;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Scrolling screenshot (PLAN.md, section 7): the user scrolls the region while frames are grabbed,
/// lined up by their overlapping rows and the new rows stacked. Ends with the stop button or Esc.
/// </summary>
internal static class ScrollCapture
{
    private const int MaxHeight = 30000;
    private const int PollMs = 60;

    public static async Task RunAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;

        var rect = region.ScreenRect;
        if (rect.Height < 40)
        {
            ResultCard.ForMessage("Bölge çok kısa", "Kaydırmalı görüntü için en az 40 piksel yüksekliğinde bir bölge seç.", rect).ShowNear();
            return;
        }

        using var stop = new CancellationTokenSource();
        var badge = new Badge(rect) { Text = "Bölgede aşağı kaydır · bitince ■ veya Esc" }.WithStop(stop.Cancel);
        badge.Show();

        string? stopReason = null;
        var output = new MemoryStream();
        int outputRows = 0;
        try
        {
            using var grabber = new FrameGrabber(rect, drawCursor: false);
            int w = grabber.Width, h = grabber.Height, stride = grabber.Stride;
            var skip = Math.Min(w / 6, 40);

            await Task.Delay(300); // let the picker overlays disappear
            var previous = grabber.NewBuffer();
            grabber.Grab(previous);
            var previousHashes = ScrollStitch.RowHashes(previous, w, h, stride, skip);

            var started = false;
            var footer = 0;

            // The user scrolls; each frame is lined up with the last one that matched and its new rows are added.
            while (!stop.IsCancellationRequested && !Native.IsKeyDown(Native.VK_ESCAPE))
            {
                await Task.Delay(PollMs);

                var current = grabber.NewBuffer();
                grabber.Grab(current);
                var hashes = ScrollStitch.RowHashes(current, w, h, stride, skip);

                // No match: mid-animation or scrolled too far at once. Keep the last good frame and look again.
                if (ScrollStitch.Match(previousHashes, hashes) is not { Shift: > 0 } match)
                    continue;

                if (!started)
                {
                    // Everything of the first frame except a sticky footer; the footer is added once at the end.
                    output.Write(previous, 0, (h - match.Bottom) * stride);
                    outputRows += h - match.Bottom;
                    started = true;
                }

                var newRows = match.Shift;
                var from = h - match.Bottom - newRows;
                output.Write(current, from * stride, newRows * stride);
                outputRows += newRows;
                footer = match.Bottom;

                badge.Text = $"{outputRows} px · kaydırmaya devam et, bitince ■ veya Esc";
                previous = current;
                previousHashes = hashes;

                if (outputRows >= MaxHeight)
                {
                    stopReason = $"En fazla {MaxHeight} piksel yüksekliğe ulaşıldı.";
                    break;
                }
            }

            if (!started)
            {
                output.Write(previous, 0, h * stride);
                outputRows = h;
                stopReason ??= "Kaydırma algılanmadı; tek kare kaydedildi.";
            }
            else if (footer > 0)
            {
                output.Write(previous, (h - footer) * stride, footer * stride);
                outputRows += footer;
            }

            var image = FrameGrabber.ToImage(output.ToArray(), w, outputRows);
            ToolRunner.CopyImage(image);
            var file = ToolRunner.Save(image, "Uzun ekran görüntüsü");

            ResultCard.ForImage("Panoya kopyalandı", stopReason ?? $"{w} × {outputRows} · {Path.GetFileName(file)}", image, rect)
                .WithButton("Klasörde göster", () => ToolRunner.ShowInFolder(file))
                .WithButton("Tamam", () => { }, primary: true)
                .ShowNear();
        }
        finally
        {
            badge.Close();
        }
    }
}
