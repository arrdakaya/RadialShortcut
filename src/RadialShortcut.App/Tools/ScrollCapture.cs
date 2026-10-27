using System.IO;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Scrolling screenshot (PLAN.md, section 7): grab the region, scroll it with the wheel, grab again,
/// line the frames up by their overlapping rows and stack the new rows. Stops at the end of the page,
/// when frames cannot be lined up, or on Esc.
/// </summary>
internal static class ScrollCapture
{
    private const int MaxHeight = 30000;
    private const int MaxSteps = 300;
    private const int SettleMs = 280;   // smooth scrolling in browsers takes about this long

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

        var badge = new Badge(rect) { Text = "Kaydırılıyor… Esc ile durdur" };
        badge.Show();
        var cursor = ScreenCapture.CursorPosition();
        // The wheel goes to whatever is under the cursor.
        Native.MoveCursor(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        // With "scroll inactive windows" off the wheel goes to the focused window, so focus the one below.
        Native.Focus(Native.WindowAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));

        string? stopReason = null;
        var output = new MemoryStream();
        int outputRows = 0;
        try
        {
            using var grabber = new FrameGrabber(rect, drawCursor: false);
            int w = grabber.Width, h = grabber.Height, stride = grabber.Stride;
            var skip = Math.Min(w / 6, 40);

            await Task.Delay(300); // let the picker overlays disappear and the focus settle
            var previous = grabber.NewBuffer();
            grabber.Grab(previous);
            var previousHashes = ScrollStitch.RowHashes(previous, w, h, stride, skip);

            var notches = 1;
            var idle = 0;
            var started = false;
            var footer = 0;

            for (var step = 0; step < MaxSteps; step++)
            {
                if (Native.IsKeyDown(Native.VK_ESCAPE))
                    break;

                Native.Wheel(-notches);
                await Task.Delay(SettleMs);
                if (Native.IsKeyDown(Native.VK_ESCAPE))
                    break;

                var current = grabber.NewBuffer();
                grabber.Grab(current);
                var hashes = ScrollStitch.RowHashes(current, w, h, stride, skip);

                if (ScrollStitch.Match(previousHashes, hashes) is not { } match)
                {
                    stopReason = "Kareler hizalanamadı; o noktaya kadar olan kısım kaydedildi.";
                    break;
                }

                if (match.Shift == 0)
                {
                    // Slow pages may need a second look before calling it the end.
                    if (++idle >= 3)
                        break;
                    continue;
                }
                idle = 0;

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

                // Scroll about half the moving area per step: plenty of overlap, not too slow.
                var pixelsPerNotch = Math.Max(1.0, (double)match.Shift / notches);
                notches = Math.Clamp((int)(0.5 * (h - match.Top - match.Bottom) / pixelsPerNotch), 1, 10);

                badge.Text = $"Kaydırılıyor… {outputRows} px · Esc ile durdur";
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
                stopReason ??= "Sayfa kaydırılmadı; tek kare kaydedildi.";
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
            Native.MoveCursor(cursor.X, cursor.Y);
        }
    }
}
