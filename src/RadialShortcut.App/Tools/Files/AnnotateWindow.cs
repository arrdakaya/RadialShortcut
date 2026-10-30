using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Not ekle: arrows, lines, boxes, ellipses, a pen, a highlighter, numbered steps, pixelated blur, text and
/// a crop (free or a fixed ratio) on an image, in a choice of colors and sizes. Shift keeps lines at 45 degrees
/// and shapes square. 1-0 pick a tool, [ and ] change the size, Ctrl+Z / Ctrl+Y undo and redo, Ctrl+C copies,
/// Ctrl+S or Enter saves and copies, Esc closes.
/// </summary>
internal sealed class AnnotateWindow : Window
{
    private enum Tool { Arrow, Line, Box, Ellipse, Pen, Marker, Number, Blur, Text, Crop }

    private static readonly (Tool Tool, string Label)[] ToolNames =
    [
        (Tool.Arrow, T("Ok", "Arrow")), (Tool.Line, T("Çizgi", "Line")), (Tool.Box, T("Kutu", "Box")), (Tool.Ellipse, T("Elips", "Ellipse")), (Tool.Pen, T("Kalem", "Pen")),
        (Tool.Marker, T("Fosforlu", "Marker")), (Tool.Number, T("Numara", "Number")), (Tool.Blur, T("Bulanık", "Blur")), (Tool.Text, T("Metin", "Text")), (Tool.Crop, T("Kırp", "Crop")),
    ];

    private static readonly (string Label, double Scale)[] Sizes = [(T("İnce", "Thin"), 0.5), (T("Orta", "Medium"), 1), (T("Kalın", "Thick"), 2), (T("Çok kalın", "Extra thick"), 3.5)];

    private static readonly (string Label, Color Color)[] Colors =
    [
        (T("Kırmızı", "Red"), Color.FromRgb(0xE5, 0x39, 0x35)), (T("Sarı", "Yellow"), Color.FromRgb(0xFD, 0xD8, 0x35)),
        (T("Mavi", "Blue"), Color.FromRgb(0x1E, 0x88, 0xE5)), (T("Yeşil", "Green"), Color.FromRgb(0x43, 0xA0, 0x47)),
        (T("Turuncu", "Orange"), Color.FromRgb(0xFB, 0x8C, 0x00)), (T("Mor", "Purple"), Color.FromRgb(0x8E, 0x24, 0xAA)),
        (T("Pembe", "Pink"), Color.FromRgb(0xEC, 0x40, 0x7A)), (T("Beyaz", "White"), Colors_.White), (T("Siyah", "Black"), Colors_.Black),
    ];

    private static readonly (string Label, double Ratio)[] Ratios = [(T("Serbest", "Free"), 0), ("1:1", 1), ("4:3", 4.0 / 3), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16)];

    private readonly BitmapSource _image;
    private readonly string? _source;
    private readonly Grid _stage = new();
    private readonly Canvas _layer = new();
    private readonly ShapePath _cropShade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
    private readonly List<UIElement> _items = [];
    private readonly Stack<UIElement> _redo = new();
    private readonly List<ToggleButton> _toolButtons = [];
    private readonly List<Button> _swatches = [];
    private readonly ComboBox _sizeBox = new() { Width = 96, Focusable = false, ToolTip = T("Kalınlık ( [ ve ] )", "Thickness ( [ and ] )") };
    private readonly double _stroke;
    private Tool _tool = Tool.Arrow;
    private Color _color = Colors[0].Color;
    private double _size = 1;
    private double _ratio;
    private Rect? _crop;
    private Point _start;
    private UIElement? _drawing;

    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var image = (await SelectedFiles.SelectionOnlyAsync(target)).FirstOrDefault(f => MediaKinds.Of(f) == MediaKind.Image);
        if (image is not null && ImageIO.TryLoad(image) is { } loaded)
        {
            await OpenAsync(image, loaded);
            return;
        }
        // Nothing selected: annotate a piece of the screen instead.
        if (await RegionPicker.PickAsync() is { } region)
            await OpenAsync(null, region.Image);
    }

    /// <param name="source">The file the image came from; null for a fresh capture.</param>
    public static Task OpenAsync(string? source, BitmapSource image)
    {
        new AnnotateWindow(source, image).Show();
        return Task.CompletedTask;
    }

    private AnnotateWindow(string? source, BitmapSource image)
    {
        _source = source;
        _image = image;
        _stroke = Math.Max(2, Math.Max(image.PixelWidth, image.PixelHeight) / 300.0);
        Title = source is null ? T("Not ekle", "Annotate") : $"{T("Not ekle", "Annotate")} · {IOPath.GetFileName(source)}";
        Background = Palette.Surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(work.Width * 0.9, Math.Max(640, image.PixelWidth + 60));
        Height = Math.Min(work.Height * 0.9, Math.Max(480, image.PixelHeight + 140));

        _stage.Width = image.PixelWidth;
        _stage.Height = image.PixelHeight;
        _stage.Background = Brushes.Transparent;
        _stage.Children.Add(new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, Stretch = Stretch.Fill });
        _stage.Children.Add(_layer);
        _stage.Children.Add(_cropShade);
        _stage.MouseLeftButtonDown += OnDown;
        _stage.MouseMove += OnMove;
        _stage.MouseLeftButtonUp += OnUp;
        _stage.Cursor = Cursors.Cross;

        var root = new DockPanel();
        var bar = Toolbar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = _stage, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12) });
        Content = root;

        PreviewKeyDown += OnKey;
    }

    private UIElement Toolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 0) };
        for (var i = 0; i < ToolNames.Length; i++)
        {
            var (tool, label) = ToolNames[i];
            var button = new ToggleButton { Content = label, IsChecked = tool == _tool, Focusable = false, ToolTip = $"{label} ({(i + 1) % 10})" };
            StyleButton(button);
            button.Click += (_, _) => PickTool(tool);
            _toolButtons.Add(button);
            bar.Children.Add(button);
        }

        bar.Children.Add(new Border { Width = 12 });
        foreach (var (label, color) in Colors)
        {
            var swatch = new Button { Width = 22, Height = 22, Background = new SolidColorBrush(color), ToolTip = label, Focusable = false, Margin = new Thickness(2, 0, 2, 6), BorderBrush = Palette.Line };
            swatch.Click += (_, _) => PickColor(color);
            _swatches.Add(swatch);
            bar.Children.Add(swatch);
        }
        PickColor(_color);

        bar.Children.Add(new Border { Width = 12 });
        _sizeBox.ItemsSource = Sizes.Select(s => s.Label).ToList();
        _sizeBox.SelectedIndex = 1;
        _sizeBox.Margin = new Thickness(0, 0, 4, 6);
        _sizeBox.SelectionChanged += (_, _) => _size = Sizes[Math.Max(0, _sizeBox.SelectedIndex)].Scale;
        bar.Children.Add(_sizeBox);

        bar.Children.Add(new Border { Width = 12 });
        var ratio = new ComboBox { Width = 90, Focusable = false, ItemsSource = Ratios.Select(r => r.Label).ToList(), SelectedIndex = 0, ToolTip = T("Kırpma oranı", "Crop ratio") };
        ratio.SelectionChanged += (_, _) => _ratio = Ratios[Math.Max(0, ratio.SelectedIndex)].Ratio;
        bar.Children.Add(ratio);

        bar.Children.Add(new Border { Width = 12 });
        bar.Children.Add(Action(T("Geri al", "Undo"), Undo));
        bar.Children.Add(Action(T("Yinele", "Redo"), Redo));
        bar.Children.Add(Action(T("Kopyala", "Copy"), () => Copy(Render())));
        bar.Children.Add(Action(T("Kaydet", "Save"), Save, primary: true));
        return bar;
    }

    private void PickTool(Tool tool)
    {
        _tool = tool;
        for (var i = 0; i < _toolButtons.Count; i++)
            _toolButtons[i].IsChecked = ToolNames[i].Tool == tool;
    }

    /// <summary>The chosen swatch gets a thick ring so it is clear which color is on.</summary>
    private void PickColor(Color color)
    {
        _color = color;
        for (var i = 0; i < _swatches.Count; i++)
        {
            var on = Colors[i].Color == color;
            _swatches[i].BorderThickness = new Thickness(on ? 3 : 1);
            _swatches[i].BorderBrush = on ? Palette.Accent : Palette.Line;
        }
    }

    private double Stroke => _stroke * _size;

    private static void StyleButton(ButtonBase button)
    {
        button.Margin = new Thickness(0, 0, 4, 6);
        button.Padding = new Thickness(10, 4, 10, 4);
        button.Foreground = Palette.Text;
        button.Background = Palette.Raised;
        button.BorderBrush = Palette.Line;
    }

    private static Button Action(string label, Action onClick, bool primary = false)
    {
        var button = new Button { Content = label, Focusable = false };
        StyleButton(button);
        if (primary)
        {
            button.Background = Palette.Accent;
            button.Foreground = Brushes.White;
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape)
            return; // typing into a text note
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox)
            Keyboard.ClearFocus();
        else if (e.Key == Key.Escape)
            Close();
        else if (ctrl && e.Key == Key.Z)
            Undo();
        else if (ctrl && e.Key == Key.Y)
            Redo();
        else if (!ctrl && e.Key is >= Key.D0 and <= Key.D9)
            PickTool(ToolNames[(e.Key - Key.D0 + 9) % 10].Tool);
        else if (e.Key == Key.OemOpenBrackets)
            _sizeBox.SelectedIndex = Math.Max(0, _sizeBox.SelectedIndex - 1);
        else if (e.Key == Key.OemCloseBrackets)
            _sizeBox.SelectedIndex = Math.Min(Sizes.Length - 1, _sizeBox.SelectedIndex + 1);
        else if (ctrl && e.Key == Key.C)
            Copy(Render());
        else if (ctrl && e.Key == Key.S || e.Key == Key.Enter)
            Save();
        else
            return;
        e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = Clamp(e.GetPosition(_stage));
        Keyboard.ClearFocus();
        var brush = new SolidColorBrush(_color);
        switch (_tool)
        {
            case Tool.Text:
                var box = new TextBox
                {
                    Foreground = brush,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = Math.Max(16, _stroke * 9) * Math.Sqrt(_size),
                    FontWeight = FontWeights.SemiBold,
                    CaretBrush = brush,
                    MinWidth = 40,
                    AcceptsReturn = true,
                };
                Canvas.SetLeft(box, _start.X);
                Canvas.SetTop(box, _start.Y);
                Add(box);
                Dispatcher.BeginInvoke(() => box.Focus());
                return;
            case Tool.Number:
                Add(Step(_start, brush));
                return;
            case Tool.Pen:
                _drawing = new Polyline { Stroke = brush, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = [_start] };
                break;
            case Tool.Marker:
                // See-through and wide, like a highlighter pen over text.
                _drawing = new Polyline { Stroke = brush, StrokeThickness = Stroke * 5, Opacity = 0.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat, Points = [_start] };
                break;
            case Tool.Arrow:
                _drawing = new ShapePath { Stroke = brush, Fill = brush, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, Tag = Tool.Arrow };
                break;
            case Tool.Line:
                _drawing = new ShapePath { Stroke = brush, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Tag = Tool.Line };
                break;
            case Tool.Ellipse:
                _drawing = new Ellipse { Stroke = brush, StrokeThickness = Stroke };
                break;
            case Tool.Crop:
                _crop = null;
                break;
            default:
                _drawing = new Rectangle { Stroke = _tool == Tool.Box ? brush : Brushes.White, StrokeThickness = _tool == Tool.Box ? Stroke : 1, StrokeDashArray = _tool == Tool.Blur ? [4, 3] : null };
                break;
        }
        if (_drawing is not null)
            _layer.Children.Add(_drawing);
        _stage.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_stage.IsMouseCaptured)
            return;
        var p = Clamp(e.GetPosition(_stage));
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case ShapePath { Tag: Tool.Arrow } arrow:
                arrow.Data = ArrowGeometry(_start, Snap(p), arrow.StrokeThickness);
                break;
            case ShapePath straight:
                straight.Data = new LineGeometry(_start, Snap(p));
                break;
            case Shape shape:
                var r = new Rect(_start, _tool == Tool.Blur ? p : Square(p));
                Canvas.SetLeft(shape, r.X);
                Canvas.SetTop(shape, r.Y);
                shape.Width = r.Width;
                shape.Height = r.Height;
                break;
            case null when _tool == Tool.Crop:
                _crop = CropRect(_start, p);
                ShowCrop();
                break;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stage.IsMouseCaptured)
            return;
        _stage.ReleaseMouseCapture();
        var drawn = _drawing;
        _drawing = null;
        if (drawn is null)
            return;

        _layer.Children.Remove(drawn);
        if (_tool == Tool.Blur && drawn is Rectangle area && area.Width >= 4 && area.Height >= 4)
            Add(Pixelated(new Rect(Canvas.GetLeft(area), Canvas.GetTop(area), area.Width, area.Height)));
        else if (_tool != Tool.Blur && (drawn is not (Rectangle or Ellipse) || drawn is Shape { Width: >= 2, Height: >= 2 }))
            Add(drawn);
    }

    private void Add(UIElement item)
    {
        if (!_layer.Children.Contains(item))
            _layer.Children.Add(item);
        _items.Add(item);
        _redo.Clear();
    }

    private static bool ShiftDown => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>With Shift, a line or arrow snaps to the nearest 45 degrees.</summary>
    private Point Snap(Point p)
    {
        if (!ShiftDown)
            return p;
        var v = p - _start;
        var angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return Clamp(_start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length);
    }

    /// <summary>With Shift, a box or ellipse stays square.</summary>
    private Point Square(Point p)
    {
        if (!ShiftDown)
            return p;
        var side = Math.Max(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        return Clamp(new Point(_start.X + Math.Sign(p.X - _start.X) * side, _start.Y + Math.Sign(p.Y - _start.Y) * side));
    }

    /// <summary>A numbered circle for step-by-step notes; the number follows how many are already placed.</summary>
    private Grid Step(Point at, Brush brush)
    {
        var number = _items.Count(i => i is FrameworkElement { Tag: "step" }) + 1;
        var size = Math.Max(24, _stroke * 10) * Math.Sqrt(_size);
        var light = _color.R * 0.299 + _color.G * 0.587 + _color.B * 0.114 > 160;
        var step = new Grid { Width = size, Height = size, Tag = "step" };
        step.Children.Add(new Ellipse { Fill = brush, Stroke = Brushes.White, StrokeThickness = Math.Max(1.5, size / 14) });
        step.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            Foreground = light ? Brushes.Black : Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = size * 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(step, at.X - size / 2);
        Canvas.SetTop(step, at.Y - size / 2);
        return step;
    }

    private void Undo()
    {
        if (_crop is not null && (_items.Count == 0 || _tool == Tool.Crop))
        {
            _crop = null;
            ShowCrop();
            return;
        }
        if (_items.Count == 0)
            return;
        _layer.Children.Remove(_items[^1]);
        _redo.Push(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
    }

    private void Redo()
    {
        if (!_redo.TryPop(out var item))
            return;
        _layer.Children.Add(item);
        _items.Add(item);
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _image.PixelWidth), Math.Clamp(p.Y, 0, _image.PixelHeight));

    /// <summary>The drag as a rectangle, held to the chosen ratio and kept inside the image.</summary>
    private Rect CropRect(Point a, Point b)
    {
        double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        if (_ratio > 0)
        {
            if (w / Math.Max(h, 1) > _ratio)
                w = h * _ratio;
            else
                h = w / _ratio;
        }
        var x = b.X >= a.X ? a.X : a.X - w;
        var y = b.Y >= a.Y ? a.Y : a.Y - h;
        return Rect.Intersect(new Rect(x, y, w, h), new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
    }

    private void ShowCrop()
    {
        if (_crop is not { IsEmpty: false } crop)
        {
            _cropShade.Data = null;
            return;
        }
        _cropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, _image.PixelWidth, _image.PixelHeight)), new RectangleGeometry(crop));
    }

    /// <summary>The covered pixels scaled down and back up without smoothing: unreadable, but clearly on purpose.</summary>
    private Image Pixelated(Rect area)
    {
        var rect = new Int32Rect((int)area.X, (int)area.Y, Math.Max(1, (int)area.Width), Math.Max(1, (int)area.Height));
        var block = Math.Max(6, Math.Max(_image.PixelWidth, _image.PixelHeight) / 120);
        var small = ImageIO.Resize(new CroppedBitmap(_image, rect), Math.Max(1, rect.Width / block), Math.Max(1, rect.Height / block));
        var pixelated = new Image { Source = small, Width = rect.Width, Height = rect.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(pixelated, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(pixelated, rect.X);
        Canvas.SetTop(pixelated, rect.Y);
        return pixelated;
    }

    private static Geometry ArrowGeometry(Point from, Point to, double stroke)
    {
        var v = to - from;
        if (v.Length < 1)
            return Geometry.Empty;
        v.Normalize();
        var head = Math.Max(12, stroke * 5);
        var n = new Vector(-v.Y, v.X);
        var baseCenter = to - v * head;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(baseCenter, true, false);
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(baseCenter + n * head * 0.55, true, true);
            ctx.LineTo(baseCenter - n * head * 0.55, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The stage at the image's own pixel size, then cut to the crop.</summary>
    private BitmapSource Render()
    {
        Keyboard.ClearFocus();
        _cropShade.Visibility = Visibility.Collapsed;
        _stage.UpdateLayout();
        var full = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        full.Render(_stage);
        _cropShade.Visibility = Visibility.Visible;

        BitmapSource result = full;
        if (_crop is { Width: >= 1, Height: >= 1 } c)
            result = new CroppedBitmap(full, new Int32Rect((int)c.X, (int)c.Y, Math.Max(1, (int)c.Width), Math.Max(1, (int)c.Height)));
        result.Freeze();
        return result;
    }

    private static void Copy(BitmapSource image) => ToolRunner.CopyImage(image);

    private void Save()
    {
        var image = Render();
        string file;
        if (_source is not null)
        {
            file = FileJobs.OutputFor(_source, T(" (not)", " (annotated)"), "png");
            ImageIO.Save(image, file);
            ToolRunner.Remember("annotate", file);
        }
        else
        {
            file = ToolRunner.Save(image, T("Not", "Annotation"));
        }
        Copy(image);
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight);
        Close();
        ResultCard.ForImage(T("Kaydedildi ve kopyalandı", "Saved and copied"), IOPath.GetFileName(file), image, anchor)
            .WithButton(T("Sil", "Delete"), () => ToolRunner.ConfirmDelete([file], anchor))
            .WithButton(T("Klasörde göster", "Show in folder"), () => ToolRunner.ShowInFolder(file))
            .WithButton(T("Tamam", "OK"), () => { }, primary: true)
            .ShowNear();
    }

    private static class Colors_
    {
        public static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
        public static readonly Color Black = Color.FromRgb(0x11, 0x11, 0x11);
    }
}
