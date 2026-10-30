using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Screen color picker: an 8× magnifier next to the cursor with the live value.
/// Click copies, Tab cycles HEX / RGB / HSL, holding Shift shows recent colors, Esc cancels.
/// </summary>
internal static class ColorPicker
{
    private static readonly List<Color> Recent = [];
    private static ColorNotation _notation = ColorNotation.Hex;

    public static async Task RunAsync()
    {
        var picked = await FrozenOverlay.RunAsync<PickedColor>((display, frozen, finish) => new ColorOverlay(display, frozen, finish));
        if (picked is null)
            return;

        var c = picked.Color;
        Recent.Remove(c);
        Recent.Insert(0, c);
        if (Recent.Count > 8)
            Recent.RemoveAt(Recent.Count - 1);

        var value = Format(_notation, c);
        ToolRunner.CopyText(value);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
        body.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, Enum.GetValues<ColorNotation>().Select(n => Format(n, c))),
            Foreground = Brushes.WhiteSmoke,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var anchor = new Int32Rect(picked.ScreenX, picked.ScreenY, 1, 1);
        var card = ResultCard.ForContent(T($"{value} panoya kopyalandı", $"{value} copied"), null, body, anchor);
        foreach (var n in Enum.GetValues<ColorNotation>().Where(n => n != _notation))
            card.WithButton(n.ToString().ToUpperInvariant(), () => ToolRunner.CopyText(Format(n, c)));
        card.WithButton(T("Tamam", "OK"), () => { }, primary: true).ShowNear();
    }

    private static string Format(ColorNotation n, Color c) => ColorFormat.Format(n, c.R, c.G, c.B);

    private sealed record PickedColor(Color Color, int ScreenX, int ScreenY);

    private sealed class ColorOverlay : FrozenOverlay
    {
        private const int Cells = 15;   // pixels across the magnifier, odd so one sits in the middle
        private const int Zoom = 8;

        private readonly Action<PickedColor?> _finish;
        private readonly Image _zoom = new() { Width = Cells * Zoom, Height = Cells * Zoom, Stretch = Stretch.Fill };
        private readonly Rectangle _cell = new() { Width = Zoom + 2, Height = Zoom + 2, Stroke = Brushes.White, StrokeThickness = 1.5 };
        private readonly Border _swatch = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        private readonly TextBlock _value = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _recent = new() { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Border _loupe;

        private Color _current;
        private Point _cursor;

        public ColorOverlay(Display display, BitmapSource frozen, Action<PickedColor?> finish)
            : base(display, frozen, () => finish(null))
        {
            _finish = finish;
            RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);

            var lens = new Canvas { Width = Cells * Zoom, Height = Cells * Zoom, ClipToBounds = true, Children = { _zoom, _cell } };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { _swatch, _value } };
            _loupe = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x2A, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden,
                Child = new StackPanel
                {
                    Children =
                    {
                        lens,
                        row,
                        new TextBlock { Text = T("Tık: kopyala · Tab: format · Shift: son renkler", "Click: copy · Tab: format · Shift: recent colors"), Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 4, 0, 0) },
                        _recent,
                    },
                },
            };
            Canvas.Children.Add(_loupe);

            MouseMove += (_, e) => Update(e.GetPosition(Canvas));
            MouseLeave += (_, _) => _loupe.Visibility = Visibility.Hidden;
            MouseLeftButtonUp += (_, e) =>
            {
                var (px, py) = ToPixel(e.GetPosition(Canvas));
                _finish(new PickedColor(_current, Display.Bounds.X + px, Display.Bounds.Y + py));
            };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Tab)
                {
                    _notation = ColorFormat.Next(_notation);
                    Update(_cursor);
                    e.Handled = true;
                }
                else if (e.Key is Key.LeftShift or Key.RightShift)
                {
                    ShowRecent(true);
                }
            };
            PreviewKeyUp += (_, e) =>
            {
                if (e.Key is Key.LeftShift or Key.RightShift)
                    ShowRecent(false);
            };
            Loaded += (_, _) =>
            {
                var (x, y) = ScreenCapture.CursorPosition();
                if (Contains(x, y))
                    Update(Mouse.GetPosition(Canvas));
            };
        }

        private void Update(Point p)
        {
            _cursor = p;
            var (px, py) = ToPixel(p);
            _current = PixelAt(px, py);

            // Keep the magnified window inside the image; the highlight then moves off-center at the edges.
            int left = Math.Clamp(px - Cells / 2, 0, Math.Max(0, Frozen.PixelWidth - Cells));
            int top = Math.Clamp(py - Cells / 2, 0, Math.Max(0, Frozen.PixelHeight - Cells));
            int w = Math.Min(Cells, Frozen.PixelWidth), h = Math.Min(Cells, Frozen.PixelHeight);
            _zoom.Source = new CroppedBitmap(Frozen, new Int32Rect(left, top, w, h));
            System.Windows.Controls.Canvas.SetLeft(_cell, (px - left) * Zoom - 1);
            System.Windows.Controls.Canvas.SetTop(_cell, (py - top) * Zoom - 1);
            // A white frame vanishes on light colors.
            _cell.Stroke = _current.R * 0.3 + _current.G * 0.59 + _current.B * 0.11 > 160 ? Brushes.Black : Brushes.White;

            _swatch.Background = new SolidColorBrush(_current);
            _value.Text = Format(_notation, _current);

            _loupe.Visibility = Visibility.Visible;
            _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _loupe.DesiredSize;
            double x = p.X + 20, y = p.Y + 20;
            if (x + size.Width > Canvas.ActualWidth) x = p.X - 20 - size.Width;
            if (y + size.Height > Canvas.ActualHeight) y = p.Y - 20 - size.Height;
            System.Windows.Controls.Canvas.SetLeft(_loupe, x);
            System.Windows.Controls.Canvas.SetTop(_loupe, y);
        }

        private void ShowRecent(bool show)
        {
            _recent.Children.Clear();
            if (show && Recent.Count == 0)
                _recent.Children.Add(new TextBlock { Text = T("Henüz renk yok", "No colors yet"), Foreground = Brushes.Gray, FontSize = 11 });
            foreach (var c in show ? Recent : [])
            {
                _recent.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 0),
                    Children =
                    {
                        new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c) },
                        new TextBlock { Text = Format(_notation, c), Foreground = Brushes.Gainsboro, FontSize = 11, Margin = new Thickness(6, 0, 0, 0) },
                    },
                });
            }
            _recent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Update(_cursor);
        }

        private Color PixelAt(int x, int y)
        {
            var pixel = new byte[4];
            new FormatConvertedBitmap(new CroppedBitmap(Frozen, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0).CopyPixels(pixel, 4, 0);
            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }
    }
}
