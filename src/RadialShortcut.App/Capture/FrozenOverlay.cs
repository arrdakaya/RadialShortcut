using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialShortcut.App.Capture;

/// <summary>
/// A full-screen window over one monitor showing a frozen grab of it. The region picker, the color
/// picker and the ruler all draw on its canvas. Esc or right click cancels.
/// </summary>
internal abstract class FrozenOverlay : Window
{
    protected FrozenOverlay(Display display, BitmapSource frozen, Action cancel)
    {
        Display = display;
        Frozen = frozen;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var image = new Image { Source = frozen, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Content = new Grid { Children = { image, Canvas } };

        SourceInitialized += (_, _) => ScreenCapture.Place(this, display.Bounds.X, display.Bounds.Y, display.Bounds.Width, display.Bounds.Height);
        MouseRightButtonUp += (_, _) => cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                cancel();
        };
    }

    protected Display Display { get; }

    protected BitmapSource Frozen { get; }

    protected Canvas Canvas { get; } = new();

    public bool Contains(int x, int y) =>
        x >= Display.Bounds.X && x < Display.Bounds.X + Display.Bounds.Width &&
        y >= Display.Bounds.Y && y < Display.Bounds.Y + Display.Bounds.Height;

    /// <summary>Pixels of the frozen image per DIP of the canvas.</summary>
    protected double PixelsPerDip => Frozen.PixelWidth / Math.Max(1, Canvas.ActualWidth);

    /// <summary>A canvas point → the frozen image pixel under it, clamped to the image.</summary>
    protected (int X, int Y) ToPixel(Point p) =>
        (Math.Clamp((int)(p.X * PixelsPerDip), 0, Frozen.PixelWidth - 1),
         Math.Clamp((int)(p.Y * Frozen.PixelHeight / Math.Max(1, Canvas.ActualHeight)), 0, Frozen.PixelHeight - 1));

    /// <summary>DIPs on this overlay → pixels of the frozen image, clamped to it.</summary>
    protected Int32Rect ToPixels(Rect r)
    {
        double sx = PixelsPerDip, sy = Frozen.PixelHeight / Math.Max(1, Canvas.ActualHeight);
        int left = Math.Clamp((int)Math.Round(r.Left * sx), 0, Frozen.PixelWidth);
        int top = Math.Clamp((int)Math.Round(r.Top * sy), 0, Frozen.PixelHeight);
        int right = Math.Clamp((int)Math.Round(r.Right * sx), 0, Frozen.PixelWidth);
        int bottom = Math.Clamp((int)Math.Round(r.Bottom * sy), 0, Frozen.PixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Freezes every monitor, opens one overlay per monitor and completes with the first result
    /// (null on cancel). Keyboard focus goes to the overlay under the cursor so Esc works right away.
    /// </summary>
    public static Task<T?> RunAsync<T>(Func<Display, BitmapSource, Action<T?>, FrozenOverlay> create) where T : class
    {
        var done = new TaskCompletionSource<T?>();
        var displays = ScreenCapture.Displays();
        var frozen = displays.Select(d => ScreenCapture.Grab(d.Bounds)).ToList();
        var overlays = new List<FrozenOverlay>();

        void Finish(T? result)
        {
            if (done.Task.IsCompleted)
                return;
            foreach (var o in overlays)
                o.Close();
            done.SetResult(result);
        }

        for (var i = 0; i < displays.Count; i++)
            overlays.Add(create(displays[i], frozen[i], Finish));

        foreach (var o in overlays)
            o.Show();

        var (x, y) = ScreenCapture.CursorPosition();
        (overlays.FirstOrDefault(o => o.Contains(x, y)) ?? overlays[0]).Activate();

        return done.Task;
    }
}
