using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.App;

/// <summary>
/// The radial menu. A transparent, click-through, never-activated topmost window that is created once
/// and kept around; it only becomes visible while the gesture has the ring open.
/// Hook coordinates are physical pixels, the content is laid out in DIPs, so the window is sized and
/// positioned in pixels for the DPI of the monitor the press happened on.
/// </summary>
public sealed class RingWindow : Window
{
    private static readonly Brush SliceFill = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
    private static readonly Brush SliceStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)));
    private static readonly Brush CenterFill = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));

    private readonly RingStyle _style;
    private readonly GestureSettings _settings;
    private readonly IReadOnlyList<RingItem> _items;
    private readonly Brush _accent;
    private readonly Grid _root = new();
    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly List<Slice> _slices = [];
    private readonly TextBlock _title = new();
    private readonly TextBlock _hint = new();
    private readonly Ellipse _dot = new();
    private readonly double _center;
    private readonly double _dotRadius;

    private IntPtr _hwnd;
    private ScreenPoint _origin;
    private (double X, double Y) _ringCenter;
    private int _hover = -1;
    private double _deadZonePx;
    private double _cancelBeyondPx;

    public RingWindow(RingStyle style, GestureSettings settings, IReadOnlyList<RingItem> items)
    {
        _style = style;
        _settings = settings;
        _items = items;
        _accent = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Accent)));
        _center = style.Size / 2;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = style.Size;
        Left = Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        var centerSize = Math.Max(2 * style.InnerRadius - 2 * style.Gap - 4, 40);
        _dotRadius = centerSize / 2 - 7;

        var canvas = new Canvas { Width = style.Size, Height = style.Size };
        canvas.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black };
        BuildSlices(canvas);
        BuildCenter(canvas, centerSize);

        _root.Children.Add(canvas);
        _root.Opacity = 0;
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = _rootScale;
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>Shows the ring centered on the press point, slid inside the monitor's work area if needed.</summary>
    public void Open(ScreenPoint origin)
    {
        _origin = origin;
        SetHover(-1);
        _dot.Visibility = Visibility.Hidden;
        PlaceAt(origin);

        Animate(0, 1, 0.85, 1, _style.OpenAnimationMs, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>
    /// Selection is measured from the ring's visible center, so a ring slid away from a screen edge
    /// can still be reached in every direction; the visible center disc is the only cancel zone.
    /// </summary>
    public void Track(ScreenPoint point)
    {
        var slice = SliceAt(point);
        SetHover(slice);

        double dx = point.X - _ringCenter.X, dy = point.Y - _ringCenter.Y;
        var outside = slice >= 0;
        _dot.Visibility = outside ? Visibility.Visible : Visibility.Hidden;
        if (outside)
        {
            var a = RingGeometry.AngleOf(dx, dy) * Math.PI / 180;
            Canvas.SetLeft(_dot, _center + _dotRadius * Math.Sin(a) - 4);
            Canvas.SetTop(_dot, _center - _dotRadius * Math.Cos(a) - 4);
        }
    }

    /// <summary>Fades the ring out. A chosen slice stays highlighted a little longer so the choice registers.</summary>
    public void Close(int selected)
    {
        SetHover(selected);
        _dot.Visibility = Visibility.Hidden;
        Animate(_root.Opacity, 0, _rootScale.ScaleX, 1.04, selected >= 0 ? 160 : 90, new CubicEase { EasingMode = EasingMode.EaseIn });
    }

    /// <summary>-1 in the center disc and also well outside the ring: releasing far away closes it.</summary>
    public int SliceAt(ScreenPoint point)
    {
        double dx = point.X - _ringCenter.X, dy = point.Y - _ringCenter.Y;
        return Math.Sqrt(dx * dx + dy * dy) > _cancelBeyondPx ? -1 : RingGeometry.SliceAt(dx, dy, _items.Count, _deadZonePx);
    }

    public RingItem ItemAt(int slice) => _items[slice];

    private void PlaceAt(ScreenPoint origin)
    {
        var monitor = MonitorFromPoint(new POINT { X = origin.X, Y = origin.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        _deadZonePx = Math.Max(_style.InnerRadius * scale, _settings.DeadZonePx);
        _cancelBeyondPx = _style.OuterRadius * scale * 1.5;

        var work = info.rcWork;
        var (cx, cy) = RingGeometry.FitInside(origin.X, origin.Y, _style.OuterRadius * scale, work.Left, work.Top, work.Right, work.Bottom);
        _ringCenter = (cx, cy);
        var size = (int)Math.Round(_style.Size * scale);
        int left = (int)Math.Round(cx - size / 2.0), top = (int)Math.Round(cy - size / 2.0);

        // Moving onto a monitor with another DPI makes WPF rescale and apply its own suggested rectangle,
        // so the position is set a second time once the window has taken the new DPI.
        for (var i = 0; i < 2; i++)
            SetWindowPos(_hwnd, HWND_TOPMOST, left, top, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void Animate(double fromOpacity, double toOpacity, double fromScale, double toScale, int ms, IEasingFunction ease)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(ms, 1));
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease });
        var scale = new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = ease };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void SetHover(int slice)
    {
        if (slice == _hover)
            return;

        if (_hover >= 0)
            _slices[_hover].SetActive(false, _accent);
        _hover = slice;
        if (_hover >= 0)
            _slices[_hover].SetActive(true, _accent);

        var item = _hover >= 0 ? _items[_hover] : null;
        _title.Text = item?.Title ?? "Genel";
        _hint.Text = item?.Hint ?? "Bırak: iptal";
    }

    private void BuildSlices(Canvas canvas)
    {
        double r0 = _style.InnerRadius, r1 = _style.OuterRadius, step = 360.0 / _items.Count;
        var mid = (r0 + r1) / 2;
        var labelWidth = Math.Round(Math.Min(84, 2 * Math.PI * mid / _items.Count * 0.88));

        for (var i = 0; i < _items.Count; i++)
        {
            var angle = i * step;
            var path = new Path
            {
                Data = Sector(r0, r1, angle - step / 2, angle + step / 2, _style.Gap),
                Fill = new SolidColorBrush(((SolidColorBrush)SliceFill).Color),
                Stroke = new SolidColorBrush(((SolidColorBrush)SliceStroke).Color),
                StrokeThickness = 1,
            };

            var icon = new Path
            {
                Data = Geometry.Parse(_items[i].Icon),
                Stroke = new SolidColorBrush(((SolidColorBrush)TextBrush).Color),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
                LayoutTransform = new ScaleTransform(22.0 / 24, 22.0 / 24),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = _items[i].Label,
                FontSize = 11,
                Foreground = icon.Stroke,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var content = new StackPanel { Width = labelWidth, Children = { icon, label } };
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var a = angle * Math.PI / 180;
            Canvas.SetLeft(content, _center + mid * Math.Sin(a) - labelWidth / 2);
            Canvas.SetTop(content, _center - mid * Math.Cos(a) - content.DesiredSize.Height / 2);

            var pop = new ScaleTransform(1, 1, _center, _center);
            path.RenderTransform = pop;
            content.RenderTransform = new ScaleTransform(1, 1, _center - Canvas.GetLeft(content), _center - Canvas.GetTop(content));

            canvas.Children.Add(path);
            canvas.Children.Add(content);
            _slices.Add(new Slice(path, icon, label, pop, (ScaleTransform)content.RenderTransform));
        }
    }

    private void BuildCenter(Canvas canvas, double size)
    {
        var disc = new Ellipse { Width = size, Height = size, Fill = CenterFill, Stroke = SliceStroke, StrokeThickness = 1 };
        Canvas.SetLeft(disc, _center - size / 2);
        Canvas.SetTop(disc, _center - size / 2);
        canvas.Children.Add(disc);

        _title.FontSize = 12;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = TextBrush;
        _hint.FontSize = 10;
        _hint.Foreground = MutedBrush;
        foreach (var t in new[] { _title, _hint })
        {
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _title.MaxHeight = 32;
        _hint.MaxHeight = 26;

        var text = new StackPanel { Width = size - 14, Children = { _title, _hint } };
        var host = new Grid { Width = size, Height = size, Children = { text } };
        text.VerticalAlignment = VerticalAlignment.Center;
        Canvas.SetLeft(host, _center - size / 2);
        Canvas.SetTop(host, _center - size / 2);
        canvas.Children.Add(host);

        _dot.Width = _dot.Height = 8;
        _dot.Fill = _accent;
        _dot.Visibility = Visibility.Hidden;
        canvas.Children.Add(_dot);

        SetHover(-1);
    }

    /// <summary>Annular sector with a constant-width gap between neighbours. Angles in degrees, clockwise from up.</summary>
    private Geometry Sector(double r0, double r1, double a0, double a1, double gap)
    {
        const double rad = Math.PI / 180;
        double go = gap / 2 / r1 / rad, gi = gap / 2 / r0 / rad;
        Point At(double r, double a) => new(_center + r * Math.Sin(a * rad), _center - r * Math.Cos(a * rad));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double o0 = a0 + go, o1 = a1 - go, i0 = a0 + gi, i1 = a1 - gi;
            ctx.BeginFigure(At(r1, o0), isFilled: true, isClosed: true);
            ctx.ArcTo(At(r1, o1), new Size(r1, r1), 0, o1 - o0 > 180, SweepDirection.Clockwise, true, true);
            ctx.LineTo(At(r0, i1), true, true);
            ctx.ArcTo(At(r0, i0), new Size(r0, r0), 0, i1 - i0 > 180, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed record Slice(Path Shape, Path Icon, TextBlock Label, ScaleTransform ShapeScale, ScaleTransform ContentScale)
    {
        public void SetActive(bool active, Brush accent)
        {
            // Entering a slice reacts quickly; leaving fades a bit slower so sweeping across the ring
            // leaves a short trail instead of hard flicker.
            var accentColor = ((SolidColorBrush)accent).Color;
            var colors = TimeSpan.FromMilliseconds(active ? 90 : 160);
            Fade(Shape.Fill, active ? accentColor : ((SolidColorBrush)SliceFill).Color, colors);
            Fade(Shape.Stroke, active ? accentColor : ((SolidColorBrush)SliceStroke).Color, colors);
            Fade(Icon.Stroke, active ? Colors.White : ((SolidColorBrush)TextBrush).Color, colors);

            var scale = new DoubleAnimation(active ? 1.06 : 1, TimeSpan.FromMilliseconds(active ? 140 : 180))
            {
                EasingFunction = active ? new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            foreach (var t in new[] { ShapeScale, ContentScale })
            {
                t.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
                t.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            }
        }

        private static void Fade(Brush brush, Color to, TimeSpan duration) =>
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
