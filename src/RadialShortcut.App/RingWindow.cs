using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    internal const int CloseAnimationMs = 75;
    // Light or dark, fixed when the window is built; rings are rebuilt when the theme changes.
    private readonly RingColors _colors = Palette.Ring;
    private readonly bool _animate;

    private readonly RingStyle _style;
    private readonly GestureSettings _settings;
    private readonly IReadOnlyList<RingItem> _items;
    private readonly Color _accent;
    private readonly Grid _root = new();
    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly List<Slice> _slices = [];
    private readonly TextBlock _title = new();
    private readonly TextBlock _hint = new();
    private string? _idleHint;
    private readonly StackPanel _centerText = new();
    private readonly Color _activeText;
    private readonly double _center;
    private readonly double _step;

    private IntPtr _hwnd;
    private (double X, double Y) _ringCenter;
    private int _hover = int.MinValue;
    private double _deadZonePx;
    private double _cancelBeyondPx;
    private ScreenPoint _press;
    private double _aimPx;
    private bool _aimed;
    private EventHandler? _pendingOpen;
    private TaskCompletionSource<bool>? _pendingClose;
    private readonly List<BitmapCache> _caches = [];

    public RingWindow(RingStyle style, GestureSettings settings, IReadOnlyList<RingItem> items)
        : this(style, settings, items, SystemParameters.ClientAreaAnimation)
    {
    }

    internal RingWindow(RingStyle style, GestureSettings settings, IReadOnlyList<RingItem> items, bool animate)
    {
        _animate = animate;
        _style = style;
        _settings = settings;
        _items = items;
        _accent = (Color)ColorConverter.ConvertFromString(style.Accent);
        // Keep custom accents readable in both themes without brightening every other slice.
        _activeText = Palette.IsDark ? Colors.White : Darken(_accent, 0.7);
        _center = style.Size / 2;
        _step = 360.0 / items.Count;

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
        SnapsToDevicePixels = true;

        var centerSize = Math.Max(2 * (style.InnerRadius - style.Gap), 40);

        var canvas = new Canvas { Width = style.Size, Height = style.Size };
        BuildSlices(canvas);
        BuildCenter(canvas, centerSize);

        _root.Children.Add(BuildShadow(centerSize));
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
            ScaleCaches(VisualTreeHelper.GetDpi(this).DpiScaleX);
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScaleCaches(newDpi.DpiScaleX);
    }

    /// <summary>A bitmap cache renders at 96 DPI unless told otherwise, which blurs it on a scaled monitor.</summary>
    private BitmapCache Cache()
    {
        var cache = new BitmapCache();
        _caches.Add(cache);
        return cache;
    }

    private void ScaleCaches(double scale)
    {
        foreach (var cache in _caches)
            cache.RenderAtScale = scale;
    }

    /// <summary>Shows the ring centered on the press point, slid inside the monitor's work area if needed.</summary>
    /// <param name="idleHint">Shown under the title while nothing is hovered, e.g. how to reach the other ring.</param>
    public void Open(ScreenPoint origin, string? idleHint = null)
    {
        CancelPendingOpen();
        CancelPendingClose();
        // A reused window must not flash its last frame before the first opening frame.
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 0;
        _idleHint = idleHint;
        SetHover(-1);
        // SetHover skips the text when nothing was hovered before, so the idle hint is set here too.
        _title.Text = T("Genel", "General");
        _hint.Text = idleHint ?? T("Bırak: iptal", "Release: cancel");
        PlaceAt(origin);
        Appear();
    }

    /// <summary>
    /// The ring as it looks once open with a slice hovered, drawn off screen (for README images):
    /// the open animation runs in place, then the settled ring is rendered at twice its size.
    /// </summary>
    public async Task<BitmapSource> SnapshotAsync(int hover)
    {
        SetHover(-1);
        Appear();
        await Task.Delay(Math.Max(_style.OpenAnimationMs, 1) + 600);
        SetHover(hover);
        await Task.Delay(500);
        var bitmap = new RenderTargetBitmap((int)(_style.Size * 2), (int)(_style.Size * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(_root);
        return bitmap;
    }

    /// <summary>
    /// The ring lifted out of this window, which is never shown, for the settings preview. With
    /// <paramref name="animate"/> it plays the open animation, otherwise it is simply there.
    /// </summary>
    public FrameworkElement TakeForPreview(bool animate)
    {
        CancelPendingClose();
        Content = null;
        if (animate)
            AppearNow();
        else
        {
            ResetScale();
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 1;
        }
        return _root;
    }

    /// <summary>Highlights a slice as if the cursor were on it; -1 clears.</summary>
    public void Highlight(int slice) => SetHover(slice < _slices.Count ? slice : -1);

    /// <summary>
    /// The drop shadow as its own blurred silhouette, rasterized once and cached. A shadow effect on the
    /// canvas itself would be re-blurred on every frame of the open animation, which made it stutter.
    /// </summary>
    private UIElement BuildShadow(double centerSize)
    {
        var shapes = new GeometryGroup();
        for (var i = 0; i < _items.Count; i++)
            shapes.Children.Add(Sector(_style.InnerRadius, _style.OuterRadius, i * _step - _step / 2, i * _step + _step / 2, _style.Gap));
        shapes.Children.Add(new EllipseGeometry(new Point(_center, _center), centerSize / 2, centerSize / 2));
        return new Path
        {
            Data = Freeze(shapes),
            Fill = Brushes.Black,
            Opacity = _colors.Shadow,
            RenderTransform = new TranslateTransform(0, 4),
            Effect = new BlurEffect { Radius = 16 },
            CacheMode = Cache(),
            IsHitTestVisible = false,
        };
    }

    /// <summary>Starts the open animation on the first frame after the window is shown, so it does not skip ahead.</summary>
    private void Appear()
    {
        CancelPendingOpen();
        CancelPendingClose();
        EventHandler? start = null;
        start = (_, _) =>
        {
            CompositionTarget.Rendering -= start;
            if (_pendingOpen == start)
            {
                _pendingOpen = null;
                AppearNow();
            }
        };
        _pendingOpen = start;
        CompositionTarget.Rendering += start;
    }

    private void AppearNow()
    {
        CancelPendingClose();
        ResetScale();
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1;
        if (!_animate)
            return;

        // Only the whole menu moves, briefly. Hover never changes icon size or layout.
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(_style.OpenAnimationMs, 1, 100));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        AnimateScale(0.97, 1, duration, ease);
    }

    private void CancelPendingOpen()
    {
        if (_pendingOpen is null)
            return;
        CompositionTarget.Rendering -= _pendingOpen;
        _pendingOpen = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelPendingOpen();
        CancelPendingClose();
        base.OnClosed(e);
    }

    private void CancelPendingClose()
    {
        _pendingClose?.TrySetResult(false);
        _pendingClose = null;
    }

    /// <summary>
    /// Selection is measured from the ring's visible center, so a ring slid away from a screen edge
    /// can still be reached in every direction; the visible center disc is the only cancel zone.
    /// </summary>
    public void Track(ScreenPoint point) => SetHover(SliceAt(point));

    /// <summary>A short fade preserves selection feedback without a pulse or a layout change.</summary>
    public void Close(int selected) => _ = CloseForActionAsync(selected);

    /// <summary>True once the fade has actually finished; false if the menu is reused or disposed first.</summary>
    internal Task<bool> CloseForActionAsync(int selected)
    {
        CancelPendingOpen();
        CancelPendingClose();
        SetHover(selected);
        var opacity = _root.Opacity;
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 0;
        ResetScale();
        if (!_animate || opacity <= 0)
            return Task.FromResult(true);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingClose = completion;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(CloseAnimationMs);
        var fade = new DoubleAnimation(opacity, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (_pendingClose != completion)
                return;
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 0;
            _pendingClose = null;
            completion.TrySetResult(true);
        };
        _root.BeginAnimation(OpacityProperty, fade);
        return completion.Task;
    }

    /// <summary>
    /// Takes the window itself off screen. A transparent layered window can still show its last
    /// opaque frame for a moment, so screen tools do not rely on opacity alone. Open shows it again.
    /// </summary>
    internal void HideFromScreen()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>-1 in the center disc and also well outside the ring: releasing far away closes it.</summary>
    public int SliceAt(ScreenPoint point)
    {
        if (!IsAimed(point))
            return -1;

        double dx = point.X - _ringCenter.X, dy = point.Y - _ringCenter.Y;
        return Math.Sqrt(dx * dx + dy * dy) > _cancelBeyondPx ? -1 : RingGeometry.SliceAt(dx, dy, _items.Count, _deadZonePx);
    }

    /// <summary>
    /// A ring slid away from a screen edge leaves the cursor over a slice instead of the center disc,
    /// so nothing counts until the cursor has moved off the press point once; releasing in place cancels.
    /// </summary>
    private bool IsAimed(ScreenPoint point)
    {
        if (!_aimed)
        {
            double dx = point.X - _press.X, dy = point.Y - _press.Y;
            _aimed = Math.Sqrt(dx * dx + dy * dy) >= _aimPx;
        }

        return _aimed;
    }

    public RingItem ItemAt(int slice) => _items[slice];

    private void PlaceAt(ScreenPoint origin)
    {
        var monitor = MonitorFromPoint(new POINT { X = origin.X, Y = origin.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        _deadZonePx = Math.Max(_style.InnerRadius * scale, _settings.DeadZonePx);
        _cancelBeyondPx = _style.OuterRadius * scale * 1.2;

        var work = info.rcWork;
        var (cx, cy) = RingGeometry.FitInside(origin.X, origin.Y, _style.OuterRadius * scale, work.Left, work.Top, work.Right, work.Bottom);
        _ringCenter = (cx, cy);
        _press = origin;
        _aimPx = _settings.DeadZonePx * scale;
        _aimed = Math.Abs(cx - origin.X) < 1 && Math.Abs(cy - origin.Y) < 1; // not slid: the center disc already guards
        var size = (int)Math.Round(_style.Size * scale);
        int left = (int)Math.Round(cx - size / 2.0), top = (int)Math.Round(cy - size / 2.0);

        // Moving onto a monitor with another DPI makes WPF rescale and apply its own suggested rectangle,
        // so the position is set a second time once the window has taken the new DPI.
        for (var i = 0; i < 2; i++)
            SetWindowPos(_hwnd, HWND_TOPMOST, left, top, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void AnimateScale(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var scale = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void ResetScale()
    {
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _rootScale.ScaleX = _rootScale.ScaleY = 1;
    }

    private void SetHover(int slice)
    {
        if (slice == _hover)
            return;

        var previous = _hover;
        if (previous >= 0)
            _slices[previous].SetActive(false, _animate);
        _hover = slice;
        if (_hover >= 0)
            _slices[_hover].SetActive(true, _animate);

        var item = _hover >= 0 ? _items[_hover] : null;
        _title.Text = item?.Title ?? T("Genel", "General");
        _hint.Text = item?.Hint ?? _idleHint ?? T("Bırak: iptal", "Release: cancel");
    }

    private void BuildSlices(Canvas canvas)
    {
        double r0 = _style.InnerRadius, r1 = _style.OuterRadius;
        var compact = _items.Count >= 9;
        var density = compact ? Math.Min(1, r1 / 140) : 1;
        var iconSize = compact ? 20 * density : 24;
        var mid = (r0 + r1) / 2 + (compact ? (r1 - r0) * 0.06 : 0);
        var maxLabelWidth = Math.Min(84, 2 * Math.PI * mid / _items.Count * 0.88);

        // Opaque matte surfaces stay legible over both light and busy desktops.
        var fill = Freeze(new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(_center, _center),
            GradientOrigin = new Point(_center, _center),
            RadiusX = r1,
            RadiusY = r1,
            GradientStops =
            {
                new GradientStop(_colors.Inner, r0 / r1),
                new GradientStop(_colors.Outer, 1),
            },
        });
        var stroke = Freeze(new SolidColorBrush(_colors.Edge));
        var text = Freeze(new SolidColorBrush(_colors.Text));
        var labelBrush = Freeze(new SolidColorBrush(_colors.Label));
        var activeText = Freeze(new SolidColorBrush(_activeText));
        var activeFill = Freeze(new SolidColorBrush(WithAlpha(_accent, 0x32)));
        var activeEdge = Freeze(new SolidColorBrush(_accent));

        for (var i = 0; i < _items.Count; i++)
        {
            var angle = i * _step;
            var path = new Path
            {
                Data = Sector(r0, r1, angle - _step / 2, angle + _step / 2, _style.Gap),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };

            var icon = AppIcon(_items[i]) ?? (FrameworkElement)new Path
            {
                Data = IconGeometry(_items[i].Icon, _items[i].Id),
                Stroke = text,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = _items[i].Label,
                FontSize = compact ? Math.Max(9, 10 * density) : 12,
                Foreground = labelBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, compact ? 3 * density : 5, 0, 0),
            };
            var iconHost = new Viewbox { Width = iconSize, Height = iconSize, Child = icon };
            var content = new StackPanel { Width = maxLabelWidth, Children = { iconHost, label } };
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Fit the entire horizontal icon/label stack between the two radial edges.
            // A circumference-based width alone overlaps neighbours in small, dense rings.
            var labelWidth = maxLabelWidth;
            var clearance = mid * Math.Sin(_step / 2 * Math.PI / 180) - _style.Gap / 2 - 1;
            foreach (var boundary in new[] { angle - _step / 2, angle + _step / 2 })
            {
                var radians = boundary * Math.PI / 180;
                var horizontal = Math.Abs(Math.Cos(radians));
                if (horizontal > 0.001)
                    labelWidth = Math.Min(labelWidth,
                        (2 * clearance - content.DesiredSize.Height * Math.Abs(Math.Sin(radians))) / horizontal);
            }
            var a = angle * Math.PI / 180;
            var x = Math.Abs(mid * Math.Sin(a));
            var y = Math.Abs(mid * Math.Cos(a));
            var halfHeight = content.DesiredSize.Height / 2;
            // Wide slices still have curved inner/outer edges: long custom labels must
            // fit those circles too, rather than extending into the hub or desktop.
            var outerY = y + halfHeight;
            labelWidth = Math.Min(labelWidth, 2 * (Math.Sqrt(Math.Max(0, Math.Pow(r1 - 1, 2) - outerY * outerY)) - x));
            var innerY = Math.Max(0, y - halfHeight);
            if (innerY < r0 + 1)
                labelWidth = Math.Min(labelWidth, 2 * (x - Math.Sqrt(Math.Pow(r0 + 1, 2) - innerY * innerY)));
            labelWidth = Math.Max(iconSize, Math.Floor(labelWidth));
            content.Width = labelWidth;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Canvas.SetLeft(content, _center + mid * Math.Sin(a) - labelWidth / 2);
            Canvas.SetTop(content, _center - mid * Math.Cos(a) - content.DesiredSize.Height / 2);
            // Geometry is built once. Selection fades this layer in place; nothing rotates,
            // scales, blurs, or rebuilds when the pointer moves to another slice.
            var highlight = new Canvas
            {
                Width = _style.Size,
                Height = _style.Size,
                Opacity = 0,
                IsHitTestVisible = false,
                Children =
                {
                    new Path { Data = path.Data, Fill = activeFill },
                    new Path
                    {
                        Data = Sector(r1 - 2, r1, angle - _step / 2, angle + _step / 2, _style.Gap),
                        Fill = activeEdge,
                    },
                },
            };

            canvas.Children.Add(path);
            canvas.Children.Add(highlight);
            canvas.Children.Add(content);
            _slices.Add(new Slice(highlight, icon, label, text, labelBrush, activeText));
        }
    }

    /// <summary>
    /// A slice that opens an app with the default launch icon shows that app's own icon instead;
    /// picking another icon in settings keeps the line icon.
    /// </summary>
    private static FrameworkElement? AppIcon(RingItem item)
    {
        if (item.Action is not { Type: "launch" } action || item.Icon != RingCatalog.LaunchIcon || AppIcons.For(action.Path) is not { } source)
            return null;
        var image = new Image { Source = source, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    internal static Geometry IconGeometry(string? data, string? toolId = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(data))
                return Freeze(Geometry.Parse(data));
        }
        catch (FormatException)
        {
            // A hand-edited icon must not prevent every ring (or the settings preview) from opening.
        }
        var fallback = DefaultRing.Items.Concat(DefaultFileRing.Items).FirstOrDefault(i => i.Id == toolId)?.Icon
            ?? RingCatalog.LaunchIcon;
        return Freeze(Geometry.Parse(fallback));
    }

    private void BuildCenter(Canvas canvas, double size)
    {
        var fill = Freeze(new LinearGradientBrush(_colors.CenterLight, _colors.CenterDark, 90));
        var edge = Freeze(new SolidColorBrush(_colors.Edge));
        var disc = new Ellipse { Width = size, Height = size, Fill = fill, Stroke = edge, StrokeThickness = 1 };
        Canvas.SetLeft(disc, _center - size / 2);
        Canvas.SetTop(disc, _center - size / 2);
        canvas.Children.Add(disc);

        _title.FontSize = 12;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Freeze(new SolidColorBrush(_colors.Text));
        _hint.FontSize = 10;
        _hint.Foreground = Freeze(new SolidColorBrush(_colors.Muted));
        _hint.Margin = new Thickness(0, 3, 0, 0);
        foreach (var t in new[] { _title, _hint })
        {
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _title.MaxHeight = 32;
        _hint.MaxHeight = 30; // two lines; at 26 the second line (~13.3px each) was cut off

        _centerText.Width = size - 14;
        _centerText.Children.Add(_title);
        _centerText.Children.Add(_hint);
        _centerText.VerticalAlignment = VerticalAlignment.Center;
        var host = new Grid { Width = size, Height = size, Children = { _centerText } };
        Canvas.SetLeft(host, _center - size / 2);
        Canvas.SetTop(host, _center - size / 2);
        canvas.Children.Add(host);

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

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    internal static Color Lighten(Color c, double t) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    internal static Color Darken(Color c, double t) =>
        Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

    internal static Color HueShift(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        double s = max == 0 ? 0 : d / max, v = max;

        h = ((h + degrees) % 360 + 360) % 360;
        double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        var (r1, g1, b1) = h switch
        {
            < 60 => (cc, x, 0.0),
            < 120 => (x, cc, 0.0),
            < 180 => (0.0, cc, x),
            < 240 => (0.0, x, cc),
            < 300 => (x, 0.0, cc),
            _ => (cc, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed record Slice(Canvas Highlight, FrameworkElement Icon, TextBlock Label, Brush Text, Brush LabelBrush, Brush Active)
    {
        public void SetActive(bool active, bool animate)
        {
            Highlight.BeginAnimation(OpacityProperty, null);
            Highlight.Opacity = active ? 1 : 0;
            // Clear the old selection immediately; never leave a trail of highlighted slices.
            if (active && animate)
                Highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(70))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            if (Icon is Path line)
                line.Stroke = active ? Active : Text;
            Label.Foreground = active ? Active : LabelBrush;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
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
