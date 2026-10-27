using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RadialShortcut.App.Capture;

/// <summary>A picked region: its pixels and where it was on screen, in physical pixels.</summary>
public sealed record CapturedRegion(BitmapSource Image, Int32Rect ScreenRect, Display Display);

/// <summary>
/// The shared "capture layer" every screen tool starts with (PLAN.md, section 6).
/// Freezes every monitor, covers each with its own overlay, and lets the user drag a rectangle.
/// A click without dragging takes the whole monitor. Esc or right click cancels.
/// A selection stays on one monitor; mixed-DPI rectangles spanning screens are not supported.
/// </summary>
public static class RegionPicker
{
    public static Task<CapturedRegion?> PickAsync() =>
        FrozenOverlay.RunAsync<CapturedRegion>((display, frozen, finish) => new RegionOverlay(display, frozen, finish));
}

internal sealed class RegionOverlay : FrozenOverlay
{
    private const double MinDrag = 4;

    private readonly Action<CapturedRegion?> _finish;
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
    private readonly Rectangle _border = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), StrokeThickness = 1.5, Visibility = Visibility.Hidden };
    private readonly Border _sizeTag = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        Visibility = Visibility.Hidden,
        IsHitTestVisible = false,
    };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11 };

    private Point? _start;

    public RegionOverlay(Display display, BitmapSource frozen, Action<CapturedRegion?> finish)
        : base(display, frozen, () => finish(null))
    {
        _finish = finish;

        _sizeTag.Child = _sizeText;
        Canvas.Children.Add(_dim);
        Canvas.Children.Add(_border);
        Canvas.Children.Add(_sizeTag);

        Loaded += (_, _) => UpdateDim(null);
        SizeChanged += (_, _) => UpdateDim(null);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start)
            return;

        var rect = new Rect(start, e.GetPosition(Canvas));
        UpdateDim(rect.Width < MinDrag && rect.Height < MinDrag ? null : rect);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
            return;

        _start = null;
        ReleaseMouseCapture();

        var rect = new Rect(start, e.GetPosition(Canvas));
        var pixels = rect.Width < MinDrag && rect.Height < MinDrag
            ? new Int32Rect(0, 0, Frozen.PixelWidth, Frozen.PixelHeight)
            : ToPixels(rect);
        if (pixels.Width < 1 || pixels.Height < 1)
        {
            _finish(null);
            return;
        }

        var crop = new CroppedBitmap(Frozen, pixels);
        crop.Freeze();
        var onScreen = new Int32Rect(Display.Bounds.X + pixels.X, Display.Bounds.Y + pixels.Y, pixels.Width, pixels.Height);
        _finish(new CapturedRegion(crop, onScreen, Display));
    }

    private void UpdateDim(Rect? selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Canvas.ActualWidth, Canvas.ActualHeight));
        if (selection is not { } sel)
        {
            _dim.Data = full;
            _border.Visibility = _sizeTag.Visibility = Visibility.Hidden;
            return;
        }

        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(sel));
        _border.Visibility = _sizeTag.Visibility = Visibility.Visible;
        _border.Width = sel.Width;
        _border.Height = sel.Height;
        Canvas.SetLeft(_border, sel.Left);
        Canvas.SetTop(_border, sel.Top);

        var px = ToPixels(sel);
        _sizeText.Text = $"{px.Width} × {px.Height}";
        Canvas.SetLeft(_sizeTag, sel.Left);
        Canvas.SetTop(_sizeTag, sel.Top >= 24 ? sel.Top - 22 : sel.Bottom + 4);
    }
}
