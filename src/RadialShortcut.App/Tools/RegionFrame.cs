using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App.Tools;

/// <summary>
/// An outline just outside a region while it is being captured or recorded. Click-through and
/// excluded from captures, so it never ends up in the result or in the way of scrolling.
/// </summary>
internal sealed class RegionFrame : Window, IDisposable
{
    private const int Thickness = 2;

    public static readonly Color Ready = Color.FromRgb(0xF2, 0xB1, 0x34);
    public static readonly Color Live = Color.FromRgb(0xE8, 0x3A, 0x2A);

    private readonly SolidColorBrush _brush = new(Live);

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
            BorderBrush = _brush,
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

    /// <summary>Amber while getting ready, red once capturing has started.</summary>
    public Color Color
    {
        set => _brush.Color = value;
    }

    public static RegionFrame Show(Int32Rect region, Color? color = null)
    {
        var frame = new RegionFrame(region) { Color = color ?? Live };
        frame.Show();
        return frame;
    }

    public void Dispose() => Close();
}
