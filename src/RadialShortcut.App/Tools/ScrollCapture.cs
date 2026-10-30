using System.IO;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Scrolling screenshot: the user scrolls the region while frames are grabbed,
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
            ResultCard.ForMessage(T("Bölge çok kısa", "Region too short"), T("Kaydırmalı görüntü için en az 40 piksel yüksekliğinde bir bölge seç.", "Pick a region at least 40 pixels tall for a scrolling capture."), rect).ShowNear();
            return;
        }

        using var stop = new CancellationTokenSource();
        using var frame = RegionFrame.Show(rect);
        var badge = new Badge(rect) { Text = T("Bölgede aşağı kaydır · bitince ■ veya Esc", "Scroll down in the region · ■ or Esc when done") }.WithStop(stop.Cancel);
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
            var previousRows = ScrollStitch.RowProfiles(previous, w, h, stride, skip);

            var started = false;
            int matched = 0, missed = 0;
            var footer = 0;

            // The user scrolls; each frame is lined up with the last one that matched and its new rows are added.
            while (!stop.IsCancellationRequested && !Native.IsKeyDown(Native.VK_ESCAPE))
            {
                await Task.Delay(PollMs);

                var current = grabber.NewBuffer();
                grabber.Grab(current);
                var rows = ScrollStitch.RowProfiles(current, w, h, stride, skip);

                // No match: mid-animation or scrolled too far at once. Keep the last good frame and look again.
                if (ScrollStitch.Match(previousRows, rows) is not { } found)
                {
                    missed++;
                    continue;
                }
                if (found is not { Shift: > 0 } match)
                    continue;
                matched++;

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

                badge.Text = T($"{outputRows} px · kaydırmaya devam et, bitince ■ veya Esc", $"{outputRows} px · keep scrolling, ■ or Esc when done");
                previous = current;
                previousRows = rows;

                if (outputRows >= MaxHeight)
                {
                    stopReason = T($"En fazla {MaxHeight} piksel yüksekliğe ulaşıldı.", $"Reached the {MaxHeight} pixel height limit.");
                    break;
                }
            }

            if (!started)
            {
                output.Write(previous, 0, h * stride);
                outputRows = h;
                stopReason ??= missed > 0
                    ? T($"Kaydırılan kareler hizalanamadı ({missed} kare); tek kare kaydedildi. Daha yavaş kaydırmayı dene.", $"Couldn't line up the scrolled frames ({missed} frames); saved a single frame. Try scrolling slower.")
                    : T("Kaydırma algılanmadı; tek kare kaydedildi.", "No scrolling detected; saved a single frame.");
            }
            else if (footer > 0)
            {
                output.Write(previous, (h - footer) * stride, footer * stride);
                outputRows += footer;
            }

            var image = FrameGrabber.ToImage(output.ToArray(), w, outputRows);
            ToolRunner.CopyImage(image);
            var file = ToolRunner.Save(image, T("Uzun ekran görüntüsü", "Long screenshot"));

            ResultCard.ForImage(T("Panoya kopyalandı", "Copied to clipboard"), stopReason ?? T($"{w} × {outputRows} · {matched} kaydırma eklendi, {missed} kare hizalanamadı · {Path.GetFileName(file)}", $"{w} × {outputRows} · {matched} scrolls added, {missed} frames not matched · {Path.GetFileName(file)}"), image, rect)
                .WithButton(T("Dosyayı sil", "Delete file"), () => ToolRunner.DeleteCapture(file))
                .WithButton(T("Klasörde göster", "Show in folder"), () => ToolRunner.ShowInFolder(file))
                .WithButton(T("Tamam", "OK"), () => { }, primary: true)
                .ShowNear();
        }
        finally
        {
            badge.Close();
        }
    }
}
