using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RadialShortcut.Core;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Boyutlandır for pictures: crop (free or a fixed ratio, with handles and a rule-of-thirds grid),
/// quarter turns, mirroring, straightening by a small angle, the output size in pixels or percent,
/// and the format and quality to save in. With several pictures the first is shown and the same
/// edit goes to all of them. R / Shift+R turn, H and V mirror, Del drops the crop, Ctrl+C copies,
/// Enter saves, Esc closes.
/// </summary>
internal sealed class ImageEditWindow : Window
{
    private static readonly (string Label, double Ratio)[] Ratios =
    [
        (T("Serbest", "Free"), 0), (T("Orijinal", "Original"), -1), ("1:1", 1), ("4:3", 4.0 / 3), ("3:4", 3.0 / 4),
        ("3:2", 3.0 / 2), ("2:3", 2.0 / 3), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:5", 4.0 / 5), ("5:4", 5.0 / 4),
    ];

    private static readonly (string Label, int Percent, int LongSide)[] SizePresets =
    [
        ("%25", 25, 0), ("%50", 50, 0), ("%75", 75, 0), ("%100", 100, 0),
        ("3840", 0, 3840), ("1920", 0, 1920), ("1280", 0, 1280), ("800", 0, 800),
    ];

    /// <summary>Past this the bitmap to draw into would not fit in memory comfortably.</summary>
    private const int MaxSide = 16384;
    private const long MaxPixels = 120_000_000;

    internal enum Format { Same, Png, Jpg, Webp }

    /// <summary>Everything that decides the result, so the other pictures of a batch get the same.</summary>
    internal sealed record Edit(ImageTurn Turn, CropArea? Crop, double Ratio, double ScaleX, double ScaleY, int LongSide, Format Format, int Quality);

    private readonly List<string> _files;
    private readonly BitmapSource _image;
    private readonly BitmapSource _preview;
    private readonly Int32Rect _anchor;

    private readonly Grid _host = new() { ClipToBounds = true };
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Image _picture = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly ShapePath _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly ShapePath _grid = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
    private readonly ShapePath _handles = new() { Fill = Brushes.White, IsHitTestVisible = false };

    private readonly List<Button> _ratioButtons = [];
    private readonly Slider _angle = new() { Minimum = -ImageTurn.MaxAngle, Maximum = ImageTurn.MaxAngle, SmallChange = 0.1, LargeChange = 1, TickFrequency = 0.1, IsSnapToTickEnabled = true };
    private readonly TextBlock _angleLabel = new();
    private readonly TextBox _width = new() { Width = 72 };
    private readonly TextBox _height = new() { Width = 72 };
    private readonly CheckBox _lock = new() { IsChecked = true, Focusable = false };
    private readonly ComboBox _format = new() { Focusable = false };
    private readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 90, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _qualityLabel = new();
    private readonly StackPanel _qualityRow = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save;

    private ImageTurn _turn;
    private CropArea? _crop;
    private double _ratio;
    private double _scaleX = 1, _scaleY = 1;
    private int _longSide;
    private bool _quiet; // the size boxes are being filled in, not typed into

    private double _zoom = 1; // screen DIPs per pixel of the turned picture
    private CropEdges? _drag; // None while a new area is being drawn
    private Point _dragFrom;
    private CropArea? _dragStart;
    private bool _straightening;

    public static async Task OpenAsync(List<string> files, Int32Rect anchor)
    {
        BitmapSource image;
        try
        {
            image = await FileTools.LoadAnyAsync(files[0], CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var window = new ImageEditWindow(files, image, anchor);
        window.Show();
        window.Activate();
    }

    private ImageEditWindow(List<string> files, BitmapSource image, Int32Rect anchor)
    {
        _files = files;
        _image = image;
        _anchor = anchor;
        // Turning and straightening redraw the picture live: a smaller copy keeps that smooth.
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        _preview = longest > 2000 ? ImageIO.Resize(image, Math.Max(1, image.PixelWidth * 2000 / longest), Math.Max(1, image.PixelHeight * 2000 / longest)) : image;

        Title = files.Count == 1
            ? $"{T("Boyutlandır ve düzenle", "Resize and edit")} · {IOPath.GetFileName(files[0])}"
            : $"{T("Boyutlandır ve düzenle", "Resize and edit")} · {T($"{files.Count} resim", $"{files.Count} images")}";
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(work.Width * 0.92, 1180);
        Height = Math.Min(work.Height * 0.92, 780);
        MinWidth = 760;
        MinHeight = 520;

        _picture.Source = _preview;
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(_picture);
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_grid);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_handles);
        _canvas.HorizontalAlignment = HorizontalAlignment.Center;
        _canvas.VerticalAlignment = VerticalAlignment.Center;
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvas.LostMouseCapture += (_, _) => EndDrag();
        _host.Background = Solid(Palette.IsDark ? 0x1B1D21 : 0xE9EBEE);
        _host.Children.Add(_canvas);
        _host.SizeChanged += (_, _) => Layout();

        _save = Action(T("Kaydet", "Save"), Save, primary: true);
        var side = SidePanel();
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(_host);
        Content = root;

        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { PickRatio(0); Refresh(); };
    }

    // ---- the side panel ----

    private UIElement SidePanel()
    {
        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

        if (_files.Count > 1)
            panel.Children.Add(Note(T($"{_files.Count} resim: ilki gösteriliyor, aynı ayarlar hepsine uygulanır.", $"{_files.Count} images: the first is shown, the same settings go to all.")));

        panel.Children.Add(Heading(T("Kırp", "Crop")));
        var ratios = new WrapPanel();
        for (var i = 0; i < Ratios.Length; i++)
        {
            var index = i;
            var button = new Button { Content = Ratios[i].Label, Focusable = false, MinWidth = 52 };
            Dress(button);
            button.Click += (_, _) => PickRatio(index);
            _ratioButtons.Add(button);
            ratios.Children.Add(button);
        }
        panel.Children.Add(ratios);
        panel.Children.Add(Row(
            Action(T("Kırpmayı kaldır", "Clear crop"), ClearCrop, tip: "Del"),
            Action(T("Ortala", "Center"), CenterCrop)));
        panel.Children.Add(Note(T("Resmin üstünde sürükleyerek alan seç; kenar ve köşelerden boyutlandır, içinden taşı.", "Drag on the picture to pick an area; size it from its edges and corners, move it from inside.")));

        panel.Children.Add(Heading(T("Döndür ve çevir", "Rotate and flip")));
        panel.Children.Add(Row(
            Action("⟲ 90°", () => Turn(right: false), tip: T("Sola döndür (Shift+R)", "Rotate left (Shift+R)")),
            Action("⟳ 90°", () => Turn(right: true), tip: T("Sağa döndür (R)", "Rotate right (R)")),
            Action(T("⇆ Yatay", "⇆ Horizontal"), () => Mirror(horizontal: true), tip: T("Yatay çevir (H)", "Flip horizontally (H)")),
            Action(T("⇅ Dikey", "⇅ Vertical"), () => Mirror(horizontal: false), tip: T("Dikey çevir (V)", "Flip vertically (V)"))));
        _angle.ValueChanged += (_, _) => { _turn = _turn with { Angle = _angle.Value }; Refresh(); };
        _angle.PreviewMouseLeftButtonDown += (_, _) => { _straightening = true; DrawCrop(); };
        _angle.PreviewMouseLeftButtonUp += (_, _) => { _straightening = false; DrawCrop(); };
        _angle.MouseDoubleClick += (_, _) => _angle.Value = 0;
        _angle.ToolTip = T("Eğik ufku düzelt; kenarlar boş kalmasın diye resim biraz kırpılır. Çift tıkla sıfırlanır.", "Straighten a tilted horizon; the picture is cropped a little so no empty corners show. Double-click resets.");
        panel.Children.Add(Row(new TextBlock { Text = T("Düzelt", "Straighten"), Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) }, _angleLabel));
        panel.Children.Add(_angle);

        panel.Children.Add(Heading(T("Boyut", "Size")));
        Dress(_width);
        Dress(_height);
        _width.TextChanged += (_, _) => Typed(width: true);
        _width.LostKeyboardFocus += (_, _) => Refresh();
        _height.LostKeyboardFocus += (_, _) => Refresh();
        _height.TextChanged += (_, _) => Typed(width: false);
        _lock.Content = T("Oranı koru", "Keep ratio");
        _lock.Foreground = Palette.Text;
        _lock.VerticalAlignment = VerticalAlignment.Center;
        _lock.Click += (_, _) => { if (_lock.IsChecked == true) { _scaleY = _scaleX; Refresh(); } };
        panel.Children.Add(Row(_width, new TextBlock { Text = "×", Margin = new Thickness(0, 0, 4, 6), VerticalAlignment = VerticalAlignment.Center }, _height,
            new TextBlock { Text = "px", Foreground = Palette.Muted, Margin = new Thickness(0, 0, 10, 6), VerticalAlignment = VerticalAlignment.Center }));
        panel.Children.Add(_lock);
        var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, percent, longSide) in SizePresets)
        {
            var button = Action(label, () => PresetSize(percent, longSide), tip: longSide > 0 ? T($"Uzun kenar {longSide} px", $"Longest side {longSide} px") : null);
            button.MinWidth = 52;
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);

        panel.Children.Add(Heading(T("Biçim", "Format")));
        var same = SameExtension(_files[0]).ToUpperInvariant();
        _format.ItemsSource = new[] { T($"Aynı ({same})", $"Same ({same})"), "PNG", "JPG", "WebP" };
        _format.SelectedIndex = 0;
        _format.Margin = new Thickness(0, 0, 0, 6);
        _format.SelectionChanged += (_, _) => Refresh();
        panel.Children.Add(_format);
        _qualityRow.Children.Add(_qualityLabel);
        _qualityRow.Children.Add(_quality);
        _quality.ValueChanged += (_, _) => Refresh();
        panel.Children.Add(_qualityRow);

        panel.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 12, 0, 10) });
        panel.Children.Add(_info);

        var buttons = Row(Action(T("Sıfırla", "Reset"), Reset));
        if (_files.Count == 1)
            buttons.Children.Add(Action(T("Kopyala", "Copy"), Copy, tip: "Ctrl+C"));
        buttons.Children.Add(_save);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(buttons);

        return new ScrollViewer
        {
            Width = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Surface,
            Content = panel,
        };
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Palette.Text, Margin = new Thickness(0, 12, 0, 6) };

    private static TextBlock Note(string text) =>
        new() { Text = text, Foreground = Palette.Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel();
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static void Dress(Control control)
    {
        control.Margin = new Thickness(0, 0, 4, 6);
        control.Padding = new Thickness(8, 4, 8, 4);
        control.Foreground = Palette.Text;
        control.Background = Palette.Raised;
        control.BorderBrush = Palette.Line;
    }

    private static Button Action(string label, Action onClick, bool primary = false, string? tip = null)
    {
        var button = new Button { Content = label, Focusable = false, ToolTip = tip };
        Dress(button);
        if (primary)
        {
            button.Background = Palette.Accent;
            button.Foreground = Brushes.White;
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    private static SolidColorBrush Solid(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    // ---- what the panel changes ----

    /// <summary>The turned and straightened picture's size, in the full picture's pixels.</summary>
    private (double W, double H) Base => _turn.Straightened(_image.PixelWidth, _image.PixelHeight);

    private double Aspect => Base.W / Base.H;

    /// <summary>The chosen ratio in pixels; 0 for free.</summary>
    private double RatioValue => _ratio < 0 ? Aspect : _ratio;

    private void PickRatio(int index)
    {
        _ratio = Ratios[index].Ratio;
        for (var i = 0; i < _ratioButtons.Count; i++)
            MarkRatio(i, i == index);
        if (_ratio > 0 || _ratio < 0 && _crop is not null)
            _crop = _crop is { } crop ? CropMath.Fit(crop, RatioValue, Aspect) : CropMath.Largest(RatioValue, Aspect);
        Refresh();
    }

    private void MarkRatio(int index, bool on)
    {
        var button = _ratioButtons[index];
        button.Background = on ? Palette.Accent : Palette.Raised;
        button.Foreground = on ? Brushes.White : Palette.Text;
    }

    /// <summary>After a quarter turn a 16:9 crop is 9:16: the buttons follow.</summary>
    private void SyncRatioButtons()
    {
        var match = _ratio <= 0 ? Array.FindIndex(Ratios, r => r.Ratio == _ratio) : Array.FindIndex(Ratios, r => r.Ratio > 0 && Math.Abs(r.Ratio - _ratio) < 1e-6);
        for (var i = 0; i < _ratioButtons.Count; i++)
            MarkRatio(i, i == match);
    }

    private void ClearCrop()
    {
        _crop = null;
        if (_ratio > 0)
            PickRatio(0);
        else
            Refresh();
    }

    private void CenterCrop()
    {
        if (_crop is { } c)
        {
            _crop = c with { X = (1 - c.Width) / 2, Y = (1 - c.Height) / 2 };
            Refresh();
        }
    }

    private void Turn(bool right)
    {
        _turn = right ? _turn.TurnedRight() : _turn.TurnedLeft();
        if (_crop is { } c)
            _crop = right ? CropMath.TurnRight(c) : CropMath.TurnLeft(c);
        if (_ratio > 0)
            _ratio = 1 / _ratio;
        (_scaleX, _scaleY) = (_scaleY, _scaleX);
        SyncRatioButtons();
        Refresh();
    }

    private void Mirror(bool horizontal)
    {
        _turn = horizontal ? _turn.MirroredOnScreen() : _turn.FlippedOnScreen();
        if (_crop is { } c)
            _crop = horizontal ? CropMath.MirrorX(c) : CropMath.MirrorY(c);
        _quiet = true;
        _angle.Value = _turn.Angle; // mirroring a tilt tilts it the other way
        _quiet = false;
        Refresh();
    }

    private void PresetSize(int percent, int longSide)
    {
        _longSide = longSide;
        if (percent > 0)
            _scaleX = _scaleY = percent / 100.0;
        _lock.IsChecked = true;
        Refresh();
    }

    private void Typed(bool width)
    {
        if (_quiet || !int.TryParse((width ? _width : _height).Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value <= 0)
            return;
        var (cw, ch) = CropPixels();
        _longSide = 0;
        if (width)
        {
            _scaleX = value / cw;
            if (_lock.IsChecked == true)
                _scaleY = _scaleX;
        }
        else
        {
            _scaleY = value / ch;
            if (_lock.IsChecked == true)
                _scaleX = _scaleY;
        }
        Refresh(typing: width ? _width : _height);
    }

    private void Reset()
    {
        _turn = default;
        _crop = null;
        _scaleX = _scaleY = 1;
        _longSide = 0;
        _lock.IsChecked = true;
        _format.SelectedIndex = 0;
        _quality.Value = 90;
        _quiet = true;
        _angle.Value = 0;
        _quiet = false;
        PickRatio(0);
    }

    private Edit Current => new(_turn, _crop, _ratio, _scaleX, _scaleY, _longSide, (Format)Math.Max(0, _format.SelectedIndex), (int)_quality.Value);

    /// <summary>The crop's size in the full picture's pixels.</summary>
    private (double W, double H) CropPixels() => CropPixels(Base, _crop);

    private static (double W, double H) CropPixels((double W, double H) size, CropArea? crop) =>
        crop is { } c ? (Math.Max(1, c.Width * size.W), Math.Max(1, c.Height * size.H)) : (Math.Max(1, size.W), Math.Max(1, size.H));

    private static (int W, int H) OutputSize((double W, double H) crop, Edit edit)
    {
        double kx = edit.ScaleX, ky = edit.ScaleY;
        if (edit.LongSide > 0)
            kx = ky = edit.LongSide / Math.Max(crop.W, crop.H);
        return (Math.Max(1, (int)Math.Round(crop.W * kx)), Math.Max(1, (int)Math.Round(crop.H * ky)));
    }

    private static bool TooBig((int W, int H) size) => size.W > MaxSide || size.H > MaxSide || (long)size.W * size.H > MaxPixels;

    /// <summary>Redraws the picture and crop and fills in the size boxes and the summary.</summary>
    private void Refresh(TextBox? typing = null)
    {
        if (!IsLoaded)
            return;
        var edit = Current;
        var crop = CropPixels();
        var size = OutputSize(crop, edit);
        _quiet = true;
        if (typing != _width)
            _width.Text = size.W.ToString(CultureInfo.CurrentCulture);
        if (typing != _height)
            _height.Text = size.H.ToString(CultureInfo.CurrentCulture);
        _quiet = false;
        _angleLabel.Text = FormattableString.Invariant($"{_turn.Angle:+0.0;-0.0;0}°");
        _angleLabel.Foreground = Palette.Text;
        _angleLabel.VerticalAlignment = VerticalAlignment.Center;
        _angleLabel.Margin = new Thickness(0, 0, 0, 6);

        var format = OutputExtension(_files[0], edit.Format);
        _qualityRow.Visibility = format is "jpg" or "webp" ? Visibility.Visible : Visibility.Collapsed;
        _qualityLabel.Text = T($"Kalite: {edit.Quality}", $"Quality: {edit.Quality}");
        _qualityLabel.Foreground = Palette.Muted;

        var lines = new List<string>
        {
            T($"Kaynak: {_image.PixelWidth} × {_image.PixelHeight} px", $"Source: {_image.PixelWidth} × {_image.PixelHeight} px"),
        };
        if (_crop is not null || _turn.Angle != 0)
            lines.Add(T($"Kırpılan: {Math.Round(crop.W)} × {Math.Round(crop.H)} px", $"Cropped: {Math.Round(crop.W)} × {Math.Round(crop.H)} px"));
        var percent = 100.0 * size.W / crop.W;
        lines.Add(T($"Sonuç: {size.W} × {size.H} px (%{percent:0}) · {format.ToUpperInvariant()}", $"Result: {size.W} × {size.H} px ({percent:0}%) · {format.ToUpperInvariant()}"));
        if (percent > 100.5)
            lines.Add(T("Büyütülüyor: ayrıntı eklenmez, resim yumuşar.", "Enlarging: no detail is added, the picture gets softer."));
        var tooBig = TooBig(size);
        var unchanged = _files.Count == 1 && edit.Turn.IsNone && edit.Crop is null && edit.Format == Format.Same
            && size.W == _image.PixelWidth && size.H == _image.PixelHeight;
        if (unchanged)
            lines.Add(T("Henüz bir değişiklik yok.", "Nothing changed yet."));
        if (tooBig)
            lines.Add(T($"Çok büyük: en fazla {MaxSide} px kenar ve {MaxPixels / 1_000_000} megapiksel.", $"Too big: at most {MaxSide} px a side and {MaxPixels / 1_000_000} megapixels."));
        _info.Text = string.Join("\n", lines);
        _info.Foreground = tooBig ? Brushes.IndianRed : Palette.Muted;
        _save.IsEnabled = !tooBig && !unchanged;

        Layout();
    }

    // ---- the picture and the crop on screen ----

    private void Layout()
    {
        if (!IsLoaded)
            return;
        var (bw, bh) = Base;
        var room = new Size(Math.Max(40, _host.ActualWidth - 40), Math.Max(40, _host.ActualHeight - 40));
        _zoom = Math.Min(room.Width / bw, room.Height / bh);
        _canvas.Width = bw * _zoom;
        _canvas.Height = bh * _zoom;

        double w = _image.PixelWidth * _zoom, h = _image.PixelHeight * _zoom;
        _picture.Width = w;
        _picture.Height = h;
        var m = Matrix.Identity;
        m.Translate(-w / 2, -h / 2);
        m.Scale(_turn.FlipX ? -1 : 1, _turn.FlipY ? -1 : 1);
        m.Rotate(_turn.Quarters * 90 + _turn.Angle);
        m.Translate(_canvas.Width / 2, _canvas.Height / 2);
        _picture.RenderTransform = new MatrixTransform(m);
        DrawCrop();
    }

    private Rect CropOnScreen() => _crop is { } c
        ? new Rect(c.X * _canvas.Width, c.Y * _canvas.Height, c.Width * _canvas.Width, c.Height * _canvas.Height)
        : new Rect(0, 0, _canvas.Width, _canvas.Height);

    private void DrawCrop()
    {
        var r = CropOnScreen();
        var all = new Rect(0, 0, _canvas.Width, _canvas.Height);
        _shade.Data = _crop is null ? null : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(r));
        _frame.Visibility = _crop is null ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_frame, r.X);
        Canvas.SetTop(_frame, r.Y);
        _frame.Width = r.Width;
        _frame.Height = r.Height;

        // Thirds while cropping, a fine grid while straightening to line the horizon up against.
        var lines = new GeometryGroup();
        var cells = _straightening ? 12 : _crop is not null || _drag is not null ? 3 : 0;
        var area = _straightening ? all : r;
        for (var i = 1; i < cells; i++)
        {
            var x = area.X + area.Width * i / cells;
            var y = area.Y + area.Height * i / cells;
            lines.Children.Add(new LineGeometry(new Point(x, area.Top), new Point(x, area.Bottom)));
            lines.Children.Add(new LineGeometry(new Point(area.Left, y), new Point(area.Right, y)));
        }
        _grid.Data = lines;

        var handles = new GeometryGroup();
        if (_crop is not null)
        {
            const double len = 16, thick = 4;
            foreach (var (cx, cy, sx, sy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
            {
                handles.Children.Add(new RectangleGeometry(Bar(cx - (sx > 0 ? thick / 2 : len - thick / 2), cy - thick / 2, len, thick)));
                handles.Children.Add(new RectangleGeometry(Bar(cx - thick / 2, cy - (sy > 0 ? thick / 2 : len - thick / 2), thick, len)));
            }
            foreach (var (x, y, w, h) in new[] { (r.X + r.Width / 2 - 10, r.Top - 2, 20.0, 4.0), (r.X + r.Width / 2 - 10, r.Bottom - 2, 20, 4), (r.Left - 2, r.Y + r.Height / 2 - 10, 4, 20), (r.Right - 2, r.Y + r.Height / 2 - 10, 4, 20) })
                handles.Children.Add(new RectangleGeometry(new Rect(x, y, w, h)));
        }
        _handles.Data = handles;
    }

    private static Rect Bar(double x, double y, double w, double h) => new(x, y, w, h);

    private Point Fraction(Point p) => new(p.X / _canvas.Width, p.Y / _canvas.Height);

    private CropEdges Hit(Point p)
    {
        if (_crop is not { } c)
            return CropEdges.None;
        var f = Fraction(p);
        return c.HitTest(f.X, f.Y, 10 / _canvas.Width, 10 / _canvas.Height);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        var p = e.GetPosition(_canvas);
        _dragFrom = Fraction(p);
        _dragStart = _crop;
        _drag = Hit(p);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_drag is not { } held)
        {
            _canvas.Cursor = Hit(p) switch
            {
                CropEdges.Move => Cursors.SizeAll,
                CropEdges.Left or CropEdges.Right => Cursors.SizeWE,
                CropEdges.Top or CropEdges.Bottom => Cursors.SizeNS,
                CropEdges.Left | CropEdges.Top or CropEdges.Right | CropEdges.Bottom => Cursors.SizeNWSE,
                CropEdges.None => Cursors.Cross,
                _ => Cursors.SizeNESW,
            };
            return;
        }
        var f = Fraction(p);
        var ratio = _ratio == 0 ? 0 : RatioValue;
        if (held == CropEdges.None)
            _crop = CropMath.Drawn(_dragFrom.X, _dragFrom.Y, f.X, f.Y, ratio, Aspect) ?? _dragStart;
        else if (held == CropEdges.Move && _dragStart is { } moved)
            _crop = moved.Moved(f.X - _dragFrom.X, f.Y - _dragFrom.Y);
        else if (_dragStart is { } start)
            _crop = ratio > 0 ? CropMath.DragHeld(start, held, f.X, f.Y, ratio, Aspect) : start.Dragged(held, f.X, f.Y);
        Refresh();
    }

    private void EndDrag()
    {
        if (_drag is null)
            return;
        _drag = null;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
        // A crop that keeps (almost) everything is no crop.
        if (_crop is { Width: > 0.995, Height: > 0.995 })
            _crop = null;
        Refresh();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key is not (Key.Escape or Key.Enter))
            return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key is Key.Escape or Key.Enter && Keyboard.FocusedElement is TextBox)
            Keyboard.ClearFocus();
        else if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.Enter && _save.IsEnabled)
            Save();
        else if (ctrl && e.Key == Key.C && _files.Count == 1)
            Copy();
        else if (e.Key == Key.R && !ctrl)
            Turn(right: !shift);
        else if (e.Key == Key.H && !ctrl)
            Mirror(horizontal: true);
        else if (e.Key == Key.V && !ctrl)
            Mirror(horizontal: false);
        else if (e.Key is Key.Delete or Key.Back)
            ClearCrop();
        else
            return;
        e.Handled = true;
    }

    // ---- making the result ----

    /// <summary>
    /// One picture through the edit in a single draw: turned about its middle, tilted, the crop's corner
    /// moved to the origin and scaled to the output size.
    /// </summary>
    internal static BitmapSource Render(BitmapSource image, Edit edit, bool first)
    {
        var turn = edit.Turn;
        var size = turn.Straightened(image.PixelWidth, image.PixelHeight);
        var crop = edit.Crop;
        // Another picture of the batch: a fixed shape is kept by fitting the same crop to its shape.
        // "Original" (negative) means each picture's own shape.
        if (!first && crop is { } c && edit.Ratio != 0)
            crop = CropMath.Fit(c, edit.Ratio < 0 ? size.W / size.H : edit.Ratio, size.W / size.H);
        var cropPx = CropPixels(size, crop);
        var output = OutputSize(cropPx, edit);
        if (TooBig(output))
            throw new InvalidOperationException(T("sonuç çok büyük olurdu.", "the result would be too big."));
        double cx = (crop?.X ?? 0) * size.W, cy = (crop?.Y ?? 0) * size.H;

        var m = Matrix.Identity;
        m.Translate(-image.PixelWidth / 2.0, -image.PixelHeight / 2.0);
        m.Scale(turn.FlipX ? -1 : 1, turn.FlipY ? -1 : 1);
        m.Rotate(turn.Quarters * 90 + turn.Angle);
        m.Translate(size.W / 2 - cx, size.H / 2 - cy);
        m.Scale(output.W / cropPx.W, output.H / cropPx.H);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(m));
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var target = new RenderTargetBitmap(output.W, output.H, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The input's own format when it can be written, otherwise a sensible one.</summary>
    private static string SameExtension(string input) => MediaKinds.Extension(input) switch
    {
        "jpeg" or "jfif" => "jpg",
        "webp" => "webp",
        var e when MediaKinds.NativeImages.Contains(e) => e,
        _ => "jpg", // HEIC, AVIF and the like: JPG opens everywhere
    };

    private static string OutputExtension(string input, Format format) => format switch
    {
        Format.Png => "png",
        Format.Jpg => "jpg",
        Format.Webp => "webp",
        _ => SameExtension(input),
    };

    private void Copy()
    {
        try
        {
            ToolRunner.CopyImage(Render(_image, Current, first: true));
            _info.Text += T("\nPanoya kopyalandı.", "\nCopied to the clipboard.");
        }
        catch (InvalidOperationException e)
        {
            _info.Text += $"\n{e.Message}";
        }
    }

    private void Save()
    {
        var edit = Current;
        var files = _files;
        var image = _image;
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight);
        var onlySize = edit.Turn.IsNone && edit.Crop is null;
        Close();
        _ = SaveAsync(files, image, edit, onlySize, anchor);
    }

    private static async Task SaveAsync(List<string> files, BitmapSource first, Edit edit, bool onlySize, Int32Rect anchor)
    {
        try
        {
            await SaveFilesAsync(files, first, edit, onlySize, anchor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ResultCard.ForMessage(T("Kaydedilemedi", "Couldn't save"), e.Message, anchor).ShowNear();
        }
    }

    private static async Task SaveFilesAsync(List<string> files, BitmapSource first, Edit edit, bool onlySize, Int32Rect anchor)
    {
        var needsFfmpeg = files.Any(f => OutputExtension(f, edit.Format) == "webp" || !MediaKinds.NativeImages.Contains(MediaKinds.Extension(f)));
        if (needsFfmpeg && !await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            return;

        await FileJobs.RunAsync("resize", T("Kaydediliyor", "Saving"), files, anchor, async (input, progress, cancel) =>
        {
            var isFirst = input == files[0];
            var image = isFirst ? first : await FileTools.LoadAnyAsync(input, cancel);
            var result = Render(image, edit, isFirst);
            var ext = OutputExtension(input, edit.Format);
            var tag = onlySize ? $" ({result.PixelWidth}x{result.PixelHeight})" : T(" (düzenlendi)", " (edited)");
            var output = FileJobs.OutputFor(input, tag, ext);
            if (ext == "webp")
            {
                var temp = IOPath.Combine(IOPath.GetTempPath(), $"rs-{Guid.NewGuid():N}.png");
                try
                {
                    ImageIO.Save(result, temp);
                    await Ffmpeg.RunAsync(temp, ["-c:v", "libwebp", "-quality", edit.Quality.ToString(CultureInfo.InvariantCulture), "-frames:v", "1"], output, progress, cancel);
                }
                finally
                {
                    ToolRunner.TryDelete(temp);
                }
            }
            else
            {
                ImageIO.Save(result, output, edit.Quality);
            }
            progress(100);
            return output;
        });
    }
}
