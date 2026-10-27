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
    public static Task<CapturedRegion?> PickAsync()
    {
        var done = new TaskCompletionSource<CapturedRegion?>();
        var displays = ScreenCapture.Displays();
        var frozen = displays.Select(d => ScreenCapture.Grab(d.Bounds)).ToList();
        var overlays = new List<RegionOverlay>();

        void Finish(CapturedRegion? result)
        {
            if (done.Task.IsCompleted)
                return;
            foreach (var o in overlays)
                o.Close();
            done.SetResult(result);
        }

        for (var i = 0; i < displays.Count; i++)
            overlays.Add(new RegionOverlay(displays[i], frozen[i], Finish));

        foreach (var o in overlays)
            o.Show();

        // Take keyboard focus on the monitor under the cursor so Esc works right away.
        var (x, y) = ScreenCapture.CursorPosition();
        (overlays.FirstOrDefault(o => o.Contains(x, y)) ?? overlays[0]).Activate();

        return done.Task;
    }
}

internal sealed class RegionOverlay : Window
{
    private const double MinDrag = 4;

    private readonly Display _display;
    private readonly BitmapSource _frozen;
    private readonly Action<CapturedRegion?> _finish;
    private readonly Canvas _canvas = new();
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
    {
        _display = display;
        _frozen = frozen;
        _finish = finish;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var image = new Image { Source = frozen, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _sizeTag.Child = _sizeText;
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_border);
        _canvas.Children.Add(_sizeTag);
        Content = new Grid { Children = { image, _canvas } };

        SourceInitialized += (_, _) => ScreenCapture.Place(this, display.Bounds.X, display.Bounds.Y, display.Bounds.Width, display.Bounds.Height);
        Loaded += (_, _) => UpdateDim(null);
        SizeChanged += (_, _) => UpdateDim(null);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => _finish(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                _finish(null);
        };
    }

    public bool Contains(int x, int y) =>
        x >= _display.Bounds.X && x < _display.Bounds.X + _display.Bounds.Width &&
        y >= _display.Bounds.Y && y < _display.Bounds.Y + _display.Bounds.Height;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start)
            return;

        var rect = new Rect(start, e.GetPosition(_canvas));
        UpdateDim(rect.Width < MinDrag && rect.Height < MinDrag ? null : rect);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
            return;

        _start = null;
        ReleaseMouseCapture();

        var rect = new Rect(start, e.GetPosition(_canvas));
        var pixels = rect.Width < MinDrag && rect.Height < MinDrag
            ? new Int32Rect(0, 0, _frozen.PixelWidth, _frozen.PixelHeight)
            : ToPixels(rect);
        if (pixels.Width < 1 || pixels.Height < 1)
        {
            _finish(null);
            return;
        }

        var crop = new CroppedBitmap(_frozen, pixels);
        crop.Freeze();
        var onScreen = new Int32Rect(_display.Bounds.X + pixels.X, _display.Bounds.Y + pixels.Y, pixels.Width, pixels.Height);
        _finish(new CapturedRegion(crop, onScreen, _display));
    }

    /// <summary>DIPs on this overlay → pixels of the frozen image, clamped to it.</summary>
    private Int32Rect ToPixels(Rect r)
    {
        double sx = _frozen.PixelWidth / _canvas.ActualWidth, sy = _frozen.PixelHeight / _canvas.ActualHeight;
        int left = Math.Clamp((int)Math.Round(r.Left * sx), 0, _frozen.PixelWidth);
        int top = Math.Clamp((int)Math.Round(r.Top * sy), 0, _frozen.PixelHeight);
        int right = Math.Clamp((int)Math.Round(r.Right * sx), 0, _frozen.PixelWidth);
        int bottom = Math.Clamp((int)Math.Round(r.Bottom * sy), 0, _frozen.PixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private void UpdateDim(Rect? selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
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
