using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App.Tools;

/// <summary>
/// A small status pill next to a region while a long-running capture works ("Kaydediliyor 00:12 ■").
/// Never takes focus and is excluded from captures, so it can sit anywhere.
/// </summary>
internal sealed class Badge : Window
{
    private readonly TextBlock _text = new() { Foreground = Palette.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly Int32Rect _region;

    public Badge(Int32Rect region)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _row.Children.Add(_text);
        Content = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 5, 6, 5),
            Child = _row,
        };

        SourceInitialized += (_, _) => Native.ExcludeFromCapture(this);
        Loaded += (_, _) => PlaceOutsideRegion();
        SizeChanged += (_, _) => { if (IsLoaded) PlaceOutsideRegion(); };
    }

    /// <summary>Top center of the region's screen instead of next to the region, where it is always seen.</summary>
    public bool AtScreenTop { get; init; }

    public string Text
    {
        set => _text.Text = value;
    }

    /// <summary>A round stop button; clicking it does not activate the badge.</summary>
    public Badge WithStop(Action onStop)
    {
        var stop = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(10, 0, 0, 0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            Cursor = Cursors.Hand,
            ToolTip = "Durdur",
            Child = new Border { Width = 8, Height = 8, Background = Brushes.White, CornerRadius = new CornerRadius(1) },
        };
        stop.MouseLeftButtonUp += (_, _) => onStop();
        _row.Children.Add(stop);
        return this;
    }

    /// <summary>Above the region's top-right corner, or below it, or inside when the region fills the screen.</summary>
    private void PlaceOutsideRegion()
    {
        var display = ScreenCapture.DisplayAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        var bounds = display.Bounds;
        int w = (int)Math.Ceiling(ActualWidth * display.Scale), h = (int)Math.Ceiling(ActualHeight * display.Scale);
        int gap = (int)(6 * display.Scale);
        if (AtScreenTop)
        {
            // Excluded from captures, so covering the region there does no harm.
            ScreenCapture.Place(this, display.WorkArea.X + (display.WorkArea.Width - w) / 2, display.WorkArea.Y + 2 * gap);
            return;
        }
        int x = _region.X + _region.Width - w;
        int y = _region.Y - h - gap;
        if (y < bounds.Y)
            y = _region.Y + _region.Height + gap;
        if (y + h > bounds.Y + bounds.Height)
            y = _region.Y + gap;
        x = Math.Clamp(x, bounds.X, bounds.X + bounds.Width - w);
        ScreenCapture.Place(this, x, y);
    }
}
