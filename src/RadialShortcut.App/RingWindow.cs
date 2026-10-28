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
    private static readonly Color TextColor = Color.FromRgb(0xE8, 0xEA, 0xED);
    private static readonly Color LabelColor = Color.FromRgb(0xB8, 0xBC, 0xC3);
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)));

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
    private readonly Color _accent2;
    private readonly Ellipse _centerRing = new();
    private readonly Ellipse _rim = new();
    private readonly ScaleTransform _rimScale;
    private readonly TranslateTransform _shimmer = new();
    private double _shimmerTravel;
    private readonly Canvas _indicator = new();
    private readonly RotateTransform _indicatorRotation;
    private readonly double _center;
    private readonly double _step;

    private IntPtr _hwnd;
    private ScreenPoint _origin;
    private (double X, double Y) _ringCenter;
    private int _hover = int.MinValue;
    private double _indicatorAngle;
    private double _deadZonePx;
    private double _cancelBeyondPx;
    private EventHandler? _pendingOpen;
    private readonly List<BitmapCache> _caches = [];

    public RingWindow(RingStyle style, GestureSettings settings, IReadOnlyList<RingItem> items)
    {
        _style = style;
        _settings = settings;
        _items = items;
        _accent = (Color)ColorConverter.ConvertFromString(style.Accent);
        _accent2 = HueShift(_accent, 45);
        _center = style.Size / 2;
        _step = 360.0 / items.Count;
        _indicatorRotation = new RotateTransform(0, _center, _center);
        _rimScale = new ScaleTransform(1, 1, style.OuterRadius + 3, style.OuterRadius + 3);

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

        var canvas = new Canvas { Width = style.Size, Height = style.Size };
        BuildSlices(canvas);
        BuildIndicator(canvas);
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
        _origin = origin;
        _idleHint = idleHint;
        SetHover(-1);
        // SetHover skips the text when nothing was hovered before, so the idle hint is set here too.
        _title.Text = "Genel";
        _hint.Text = idleHint ?? "Bırak: iptal";
        foreach (var e in new UIElement[] { _indicator, _centerRing })
        {
            e.BeginAnimation(OpacityProperty, null);
            e.Opacity = 0;
        }
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
        Content = null;
        if (animate)
            AppearNow();
        else
            _root.Opacity = 1;
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
            Opacity = 0.45,
            RenderTransform = new TranslateTransform(0, 8),
            Effect = new BlurEffect { Radius = 28 },
            CacheMode = Cache(),
            IsHitTestVisible = false,
        };
    }

    /// <summary>Starts the open animation on the first frame after the window is shown, so it does not skip ahead.</summary>
    private void Appear()
    {
        EventHandler? start = null;
        start = (_, _) =>
        {
            CompositionTarget.Rendering -= start;
            if (_pendingOpen == start)
                AppearNow();
        };
        _pendingOpen = start;
        CompositionTarget.Rendering += start;
    }

    private void AppearNow()
    {
        // One quick fade and scale of the whole ring: anything more makes opening feel sluggish.
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(_style.OpenAnimationMs, 1, 120));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        AnimateScale(0.9, 1, duration, ease);

        _rim.BeginAnimation(OpacityProperty, null);
        _rim.Opacity = 0;
        foreach (var slice in _slices)
        {
            slice.Shape.BeginAnimation(OpacityProperty, null);
            slice.Shape.Opacity = 1;
            slice.ShapeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            slice.ShapeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            slice.ShapeScale.ScaleX = slice.ShapeScale.ScaleY = 1;
        }
    }

    /// <summary>
    /// Selection is measured from the ring's visible center, so a ring slid away from a screen edge
    /// can still be reached in every direction; the visible center disc is the only cancel zone.
    /// </summary>
    public void Track(ScreenPoint point) => SetHover(SliceAt(point));

    /// <summary>Fades the ring out. A chosen slice pulses and stays highlighted a little longer so the choice registers.</summary>
    public void Close(int selected)
    {
        _pendingOpen = null;
        SetHover(selected);
        if (selected >= 0)
            _slices[selected].Pulse();

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(selected >= 0 ? 90 : 60);
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        AnimateScale(_rootScale.ScaleX, selected >= 0 ? 1.05 : 0.94, duration, ease);
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
        _cancelBeyondPx = _style.OuterRadius * scale * 1.2;

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

    private void AnimateScale(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var scale = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void SetHover(int slice)
    {
        if (slice == _hover)
            return;

        var previous = _hover;
        if (previous >= 0)
            _slices[previous].SetActive(false);
        _hover = slice;
        if (_hover >= 0)
            _slices[_hover].SetActive(true);

        MoveIndicator(previous, slice);

        // While something is hovered the other slices step back, so the choice stands out.
        for (var i = 0; i < _slices.Count; i++)
            _slices[i].Content.BeginAnimation(OpacityProperty, new DoubleAnimation(slice < 0 || i == slice ? 1 : 0.45, TimeSpan.FromMilliseconds(160)));

        var item = _hover >= 0 ? _items[_hover] : null;
        _title.Text = item?.Title ?? "Genel";
        _hint.Text = item?.Hint ?? _idleHint ?? "Bırak: iptal";
        _centerText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>
    /// The highlight glides between slices the shorter way round; coming from the center it appears
    /// in place instead of sweeping in from wherever it was last.
    /// </summary>
    private void MoveIndicator(int from, int to)
    {
        if (to < 0)
        {
            FadeHighlight(0, 140);
            return;
        }

        var target = to * _step;
        if (from < 0)
        {
            _indicatorRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _indicatorAngle = target;
            _indicatorRotation.Angle = target;
        }
        else
        {
            _indicatorAngle += ((target - _indicatorAngle) % 360 + 540) % 360 - 180;
            _indicatorRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_indicatorAngle, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut },
            });
        }
        FadeHighlight(1, 90);

        // A soft band of light sweeps across the newly highlighted slice.
        _shimmer.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_shimmerTravel, _shimmerTravel, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void FadeHighlight(double to, int ms)
    {
        foreach (var e in new UIElement[] { _indicator, _centerRing })
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    private void BuildSlices(Canvas canvas)
    {
        double r0 = _style.InnerRadius, r1 = _style.OuterRadius;
        var mid = (r0 + r1) / 2;
        var labelWidth = Math.Round(Math.Min(84, 2 * Math.PI * mid / _items.Count * 0.88));

        // Radial so every slice looks the same: darker toward the center, a little lighter at the rim.
        var fill = Freeze(new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(_center, _center),
            GradientOrigin = new Point(_center, _center),
            RadiusX = r1,
            RadiusY = r1,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xF4, 0x1E, 0x20, 0x25), r0 / r1),
                new GradientStop(Color.FromArgb(0xF4, 0x30, 0x33, 0x3A), 1),
            },
        });
        var stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));

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
                Data = Geometry.Parse(_items[i].Icon),
                Stroke = new SolidColorBrush(TextColor),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
                LayoutTransform = new ScaleTransform(22.0 / 24, 22.0 / 24),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var glow = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = Lighten(_accent, 0.2), Opacity = 0 };
            var label = new TextBlock
            {
                Text = _items[i].Label,
                FontSize = 11,
                Foreground = new SolidColorBrush(LabelColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var content = new StackPanel { Width = labelWidth, Children = { icon, label } };
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var a = angle * Math.PI / 180;
            Canvas.SetLeft(content, _center + mid * Math.Sin(a) - labelWidth / 2);
            Canvas.SetTop(content, _center - mid * Math.Cos(a) - content.DesiredSize.Height / 2);
            var scale = new ScaleTransform(1, 1, labelWidth / 2, content.DesiredSize.Height / 2);
            content.RenderTransform = scale;

            var shapeScale = new ScaleTransform(1, 1, _center, _center);
            path.RenderTransform = shapeScale;

            canvas.Children.Add(path);
            canvas.Children.Add(content);
            _slices.Add(new Slice(path, content, icon, label, shapeScale, scale, glow));
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

    /// <summary>Accent glow over the hovered slice plus a bright arc just outside it; rotated to follow the hover.</summary>
    private void BuildIndicator(Canvas canvas)
    {
        double r0 = _style.InnerRadius, r1 = _style.OuterRadius, half = _step / 2;
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(_center, _center),
            GradientOrigin = new Point(_center, _center),
            RadiusX = r1,
            RadiusY = r1,
            GradientStops =
            {
                new GradientStop(WithAlpha(_accent2, 0x50), r0 / r1),
                new GradientStop(WithAlpha(_accent, 0xE0), 1),
            },
        };
        _indicator.Children.Add(new Path
        {
            Data = Sector(r0, r1, -half, half, _style.Gap),
            Fill = Freeze(glow),
            Stroke = Freeze(new SolidColorBrush(Lighten(_accent, 0.45))),
            StrokeThickness = 1.2,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Color = _accent, Opacity = 0.75 },
            CacheMode = Cache(),
        });

        // Shimmer: a diagonal light band that starts parked off the slice and is swept across on hover.
        _shimmerTravel = r1 * Math.Sin(Math.Min(half, 90) * Math.PI / 180) + 30;
        _shimmer.X = -_shimmerTravel;
        var band = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(_center - _shimmerTravel, _center - r1),
            EndPoint = new Point(_center + _shimmerTravel, _center - r1 + 40),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.38),
                new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.62),
            },
            Transform = _shimmer,
        };
        _indicator.Children.Add(new Path { Data = Sector(r0, r1, -half, half, _style.Gap), Fill = band });

        // Two-tone arc across the slice's outer edge.
        var light = Lighten(_accent, 0.35);
        _indicator.Children.Add(new Path
        {
            Data = Sector(r1 + 3, r1 + 8, -half + 4, half - 4, 0),
            Fill = Freeze(new LinearGradientBrush(Lighten(_accent2, 0.3), light, 0)),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Color = light, Opacity = 1 },
            CacheMode = Cache(),
        });

        var rimSize = 2 * (r1 + 3);
        _rim.Width = _rim.Height = rimSize;
        _rim.StrokeThickness = 1.5;
        _rim.Stroke = Freeze(new LinearGradientBrush(Lighten(_accent, 0.3), Lighten(_accent2, 0.2), 45));
        _rim.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = _accent, Opacity = 0.8 };
        _rim.RenderTransform = _rimScale;
        _rim.CacheMode = Cache();
        _rim.Opacity = 0;
        Canvas.SetLeft(_rim, _center - rimSize / 2);
        Canvas.SetTop(_rim, _center - rimSize / 2);
        canvas.Children.Add(_rim);

        _indicator.Width = _indicator.Height = _style.Size;
        _indicator.RenderTransform = _indicatorRotation;
        _indicator.Opacity = 0;
        _indicator.IsHitTestVisible = false;
        // Above the slice surfaces, below every icon and label.
        canvas.Children.Insert(_slices.Count == 0 ? 0 : canvas.Children.IndexOf(_slices[0].Content), _indicator);
    }

    private void BuildCenter(Canvas canvas, double size)
    {
        var fill = new RadialGradientBrush(Color.FromRgb(0x2C, 0x2F, 0x36), Color.FromRgb(0x1A, 0x1C, 0x21))
        {
            GradientOrigin = new Point(0.5, 0.3),
            Center = new Point(0.5, 0.4),
            RadiusX = 0.7,
            RadiusY = 0.7,
        };
        var disc = new Ellipse { Width = size, Height = size, Fill = Freeze(fill), Stroke = EdgeBrush(size / 2), StrokeThickness = 1 };
        Canvas.SetLeft(disc, _center - size / 2);
        Canvas.SetTop(disc, _center - size / 2);
        canvas.Children.Add(disc);

        _centerRing.Width = _centerRing.Height = size;
        _centerRing.StrokeThickness = 1.5;
        _centerRing.Stroke = Freeze(new LinearGradientBrush(Lighten(_accent, 0.3), _accent2, 45));
        _centerRing.Opacity = 0;
        Canvas.SetLeft(_centerRing, _center - size / 2);
        Canvas.SetTop(_centerRing, _center - size / 2);
        canvas.Children.Add(_centerRing);

        _title.FontSize = 12;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Freeze(new SolidColorBrush(TextColor));
        _hint.FontSize = 10;
        _hint.Foreground = MutedBrush;
        _hint.Margin = new Thickness(0, 1, 0, 0);
        foreach (var t in new[] { _title, _hint })
        {
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _title.MaxHeight = 32;
        _hint.MaxHeight = 26;

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

    /// <summary>Hairline that is brighter on top, like light catching the upper edge.</summary>
    private Brush EdgeBrush(double radius) => Freeze(new LinearGradientBrush(
        [new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 1)],
        new Point(0, _center - radius), new Point(0, _center + radius)) { MappingMode = BrushMappingMode.Absolute });

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

    private sealed record Slice(Path Shape, FrameworkElement Content, FrameworkElement Icon, TextBlock Label, ScaleTransform ShapeScale, ScaleTransform ContentScale, DropShadowEffect Glow)
    {
        private int _glowToken;

        public void SetActive(bool active)
        {
            // Entering a slice reacts quickly; leaving fades a bit slower so sweeping across the ring
            // leaves a short trail instead of hard flicker.
            var colors = TimeSpan.FromMilliseconds(active ? 90 : 180);
            if (Icon is Path line)
                Fade(line.Stroke, active ? Colors.White : TextColor, colors);
            Fade(Label.Foreground, active ? Colors.White : LabelColor, colors);
            // The glow is attached only while it shows: an invisible effect still costs a blur pass every frame.
            var token = ++_glowToken;
            Icon.Effect = Glow;
            var glow = new DoubleAnimation(active ? 1 : 0, colors);
            if (!active)
                glow.Completed += (_, _) =>
                {
                    if (token == _glowToken)
                        Icon.Effect = null;
                };
            Glow.BeginAnimation(DropShadowEffect.OpacityProperty, glow);

            Scale(active ? 1.16 : 1, TimeSpan.FromMilliseconds(active ? 220 : 200),
                active ? new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        public void Pulse() => Scale(1.28, TimeSpan.FromMilliseconds(120), new CubicEase { EasingMode = EasingMode.EaseOut });

        private void Scale(double to, TimeSpan duration, IEasingFunction ease)
        {
            var scale = new DoubleAnimation(to, duration) { EasingFunction = ease };
            ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
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
