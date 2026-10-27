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
/// Not ekle: arrows, boxes, a pen, pixelated blur, text and a crop (free or a fixed ratio) on an image.
/// Ctrl+Z undoes, Ctrl+C copies, Ctrl+S or Enter saves and copies, Esc closes.
/// </summary>
internal sealed class AnnotateWindow : Window
{
    private enum Tool { Arrow, Box, Pen, Blur, Text, Crop }

    private static readonly (string Label, Color Color)[] Colors =
    [
        ("Kırmızı", Color.FromRgb(0xE5, 0x39, 0x35)), ("Sarı", Color.FromRgb(0xFD, 0xD8, 0x35)),
        ("Mavi", Color.FromRgb(0x1E, 0x88, 0xE5)), ("Yeşil", Color.FromRgb(0x43, 0xA0, 0x47)),
        ("Beyaz", Colors_.White), ("Siyah", Colors_.Black),
    ];

    private static readonly (string Label, double Ratio)[] Ratios = [("Serbest", 0), ("1:1", 1), ("4:3", 4.0 / 3), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16)];

    private readonly BitmapSource _image;
    private readonly string? _source;
    private readonly Grid _stage = new();
    private readonly Canvas _layer = new();
    private readonly ShapePath _cropShade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), IsHitTestVisible = false };
    private readonly List<UIElement> _items = [];
    private readonly double _stroke;
    private Tool _tool = Tool.Arrow;
    private Color _color = Colors[0].Color;
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
        Title = source is null ? "Not ekle" : $"Not ekle · {IOPath.GetFileName(source)}";
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
        var tools = new List<ToggleButton>();
        foreach (var (tool, label) in new[] { (Tool.Arrow, "Ok"), (Tool.Box, "Kutu"), (Tool.Pen, "Kalem"), (Tool.Blur, "Bulanık"), (Tool.Text, "Metin"), (Tool.Crop, "Kırp") })
        {
            var button = new ToggleButton { Content = label, IsChecked = tool == _tool, Focusable = false };
            Style(button);
            button.Click += (_, _) =>
            {
                _tool = tool;
                foreach (var other in tools)
                    other.IsChecked = other == button;
            };
            tools.Add(button);
            bar.Children.Add(button);
        }

        bar.Children.Add(new Border { Width = 12 });
        foreach (var (label, color) in Colors)
        {
            var swatch = new Button { Width = 22, Height = 22, Background = new SolidColorBrush(color), ToolTip = label, Focusable = false, Margin = new Thickness(2, 0, 2, 0), BorderBrush = Palette.Line };
            swatch.Click += (_, _) => _color = color;
            bar.Children.Add(swatch);
        }

        bar.Children.Add(new Border { Width = 12 });
        var ratio = new ComboBox { Width = 90, Focusable = false, ItemsSource = Ratios.Select(r => r.Label).ToList(), SelectedIndex = 0, ToolTip = "Kırpma oranı" };
        ratio.SelectionChanged += (_, _) => _ratio = Ratios[Math.Max(0, ratio.SelectedIndex)].Ratio;
        bar.Children.Add(ratio);

        bar.Children.Add(new Border { Width = 12 });
        bar.Children.Add(Action("Geri al", Undo));
        bar.Children.Add(Action("Kopyala", () => Copy(Render())));
        bar.Children.Add(Action("Kaydet", Save, primary: true));
        return bar;
    }

    private static void Style(ButtonBase button)
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
        Style(button);
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
                    FontSize = Math.Max(16, _stroke * 9),
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
            case Tool.Pen:
                _drawing = new Polyline { Stroke = brush, StrokeThickness = _stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = [_start] };
                break;
            case Tool.Arrow:
                _drawing = new ShapePath { Stroke = brush, Fill = brush, StrokeThickness = _stroke, StrokeLineJoin = PenLineJoin.Round };
                break;
            case Tool.Crop:
                _crop = null;
                break;
            default:
                _drawing = new Rectangle { Stroke = _tool == Tool.Box ? brush : Brushes.White, StrokeThickness = _tool == Tool.Box ? _stroke : 1, StrokeDashArray = _tool == Tool.Blur ? [4, 3] : null };
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
            case ShapePath arrow:
                arrow.Data = ArrowGeometry(_start, p, _stroke);
                break;
            case Rectangle rect:
                var r = new Rect(_start, p);
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                rect.Width = r.Width;
                rect.Height = r.Height;
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
        else if (_tool != Tool.Blur && (drawn is not Rectangle box || box.Width >= 2 && box.Height >= 2))
            Add(drawn);
    }

    private void Add(UIElement item)
    {
        if (!_layer.Children.Contains(item))
            _layer.Children.Add(item);
        _items.Add(item);
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
        _items.RemoveAt(_items.Count - 1);
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
            file = FileJobs.OutputFor(_source, " (not)", "png");
            ImageIO.Save(image, file);
            ToolRunner.Remember("annotate", file);
        }
        else
        {
            file = ToolRunner.Save(image, "Not");
        }
        Copy(image);
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight);
        Close();
        ResultCard.ForImage("Kaydedildi ve kopyalandı", IOPath.GetFileName(file), image, anchor)
            .WithButton("Klasörde göster", () => ToolRunner.ShowInFolder(file))
            .WithButton("Tamam", () => { }, primary: true)
            .ShowNear();
    }

    private static class Colors_
    {
        public static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
        public static readonly Color Black = Color.FromRgb(0x11, 0x11, 0x11);
    }
}
