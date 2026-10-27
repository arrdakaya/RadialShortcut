using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Screen ruler: horizontal and vertical guides follow the cursor; dragging measures between two points
/// in physical pixels. The last measurement stays until the next drag. Shift locks to one axis,
/// Ctrl+C copies "W × H", Esc or right click closes.
/// </summary>
internal static class Ruler
{
    public static Task RunAsync() =>
        FrozenOverlay.RunAsync<object>((display, frozen, finish) => new RulerOverlay(display, frozen, () => finish(null)));

    private sealed class RulerOverlay : FrozenOverlay
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x9C, 0xFF));

        private readonly Line _guideX = Guide(), _guideY = Guide();
        private readonly Line _measure = new() { Stroke = Accent, StrokeThickness = 1.5, Visibility = Visibility.Hidden };
        private readonly Rectangle _box = new() { Stroke = Accent, StrokeThickness = 1, StrokeDashArray = [4, 3], Visibility = Visibility.Hidden };
        private readonly Border _tag = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1F, 0x1F, 0x1F)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            IsHitTestVisible = false,
        };
        private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 12 };

        private Point? _start;
        private (int W, int H) _last;

        public RulerOverlay(Display display, BitmapSource frozen, Action close)
            : base(display, frozen, close)
        {
            _tag.Child = _text;
            Canvas.Children.Add(_box);
            Canvas.Children.Add(_guideX);
            Canvas.Children.Add(_guideY);
            Canvas.Children.Add(_measure);
            Canvas.Children.Add(_tag);
            Canvas.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1F, 0x1F, 0x1F)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(12),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = "Sürükleyerek ölç · Shift: yatay/dikey · Ctrl+C: kopyala · Esc: çık", Foreground = Brushes.Gainsboro, FontSize = 12 },
            });

            MouseMove += (_, e) => Update(e.GetPosition(Canvas));
            MouseLeftButtonDown += (_, e) =>
            {
                _start = e.GetPosition(Canvas);
                CaptureMouse();
                Update(_start.Value);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                Update(e.GetPosition(Canvas));
                _start = null;
                ReleaseMouseCapture();
            };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _last != default)
                    ToolRunner.CopyText($"{_last.W} × {_last.H}");
            };
        }

        private static Line Guide() => new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x9C, 0xFF)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        private void Update(Point p)
        {
            _guideX.X1 = 0; _guideX.X2 = Canvas.ActualWidth; _guideX.Y1 = _guideX.Y2 = p.Y;
            _guideY.Y1 = 0; _guideY.Y2 = Canvas.ActualHeight; _guideY.X1 = _guideY.X2 = p.X;

            var (px, py) = ToPixel(p);
            if (_start is not { } start)
            {
                if (_measure.Visibility != Visibility.Visible)
                    Place($"{px}, {py}", p);
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                p = Math.Abs(p.X - start.X) >= Math.Abs(p.Y - start.Y) ? new Point(p.X, start.Y) : new Point(start.X, p.Y);

            var (sx, sy) = ToPixel(start);
            (px, py) = ToPixel(p);
            int w = Math.Abs(px - sx) + 1, h = Math.Abs(py - sy) + 1;
            _last = (w, h);
            var distance = Math.Sqrt(Math.Pow(px - sx, 2) + Math.Pow(py - sy, 2));

            _measure.X1 = start.X; _measure.Y1 = start.Y; _measure.X2 = p.X; _measure.Y2 = p.Y;
            _measure.Visibility = _box.Visibility = Visibility.Visible;
            var rect = new Rect(start, p);
            _box.Width = rect.Width;
            _box.Height = rect.Height;
            System.Windows.Controls.Canvas.SetLeft(_box, rect.Left);
            System.Windows.Controls.Canvas.SetTop(_box, rect.Top);

            Place($"{w} × {h} px · {distance:0.#} px", p);
        }

        private void Place(string text, Point p)
        {
            _text.Text = text;
            _tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = p.X + 14, y = p.Y + 14;
            if (x + _tag.DesiredSize.Width > Canvas.ActualWidth) x = p.X - 14 - _tag.DesiredSize.Width;
            if (y + _tag.DesiredSize.Height > Canvas.ActualHeight) y = p.Y - 14 - _tag.DesiredSize.Height;
            System.Windows.Controls.Canvas.SetLeft(_tag, x);
            System.Windows.Controls.Canvas.SetTop(_tag, y);
        }
    }
}
