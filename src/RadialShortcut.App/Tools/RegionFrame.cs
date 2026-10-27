using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App.Tools;

/// <summary>
/// A dashed outline just outside a region while it is being captured or recorded. Click-through and
/// excluded from captures, so it never ends up in the result or in the way of scrolling.
/// </summary>
internal sealed class RegionFrame : Window, IDisposable
{
    private const int Thickness = 2;

    private RegionFrame(Int32Rect region)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = Top = -10000;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x2A)),
            BorderThickness = new System.Windows.Thickness(Thickness),
        };

        SourceInitialized += (_, _) =>
        {
            Native.ExcludeFromCapture(this);
            Native.MakeClickThrough(this);
            // Device pixels, just outside the region so the outline is never inside the captured area.
            var scale = ScreenCapture.DisplayAt(region.X, region.Y).Scale;
            var t = (int)Math.Ceiling(Thickness * scale);
            ScreenCapture.Place(this, region.X - t, region.Y - t, region.Width + 2 * t, region.Height + 2 * t);
        };
    }

    public static RegionFrame Show(Int32Rect region)
    {
        var frame = new RegionFrame(region);
        frame.Show();
        return frame;
    }

    public void Dispose() => Close();
}
