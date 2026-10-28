using System.IO;
using System.Windows;
using System.Windows.Controls;
using RadialShortcut.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Shift + dragging files out of Explorer or the desktop opens a strip of tool tiles next to the
/// cursor; dropping the files on a tile runs that tool on them. The strip goes away when the drag
/// ends anywhere else.
/// </summary>
internal sealed class DropZones : Window
{
    private static DropZones? _open;

    // Tools that work on files; download and library have no input to drop.
    private static readonly string[] ToolIds = ["convert", "compress", "resize", "trim", "annotate", "nobg"];

    // The ring's look: its accent pair, slice fill, hairline edge and label colors.
    private static readonly Color Accent = (Color)ColorConverter.ConvertFromString(new RingStyle().Accent);
    private static readonly Color Accent2 = RingWindow.HueShift(Accent, 45);
    private static readonly Brush TileFill = Frozen(new LinearGradientBrush(Color.FromArgb(0xF4, 0x30, 0x33, 0x3A), Color.FromArgb(0xF4, 0x1E, 0x20, 0x25), 90));
    private static readonly Brush HotFill = Frozen(new LinearGradientBrush(RingWindow.WithAlpha(Accent, 0xE0), RingWindow.WithAlpha(Accent2, 0xC0), 45));
    private static readonly Brush Edge = Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HotEdge = Frozen(new SolidColorBrush(RingWindow.Lighten(Accent, 0.45)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC3)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)));

    private readonly ScreenPoint _origin;
    private bool _dropped;

    public static void ShowAt(ScreenPoint origin)
    {
        _open?.Close();
        _open = new DropZones(origin);
        _open.Closed += (s, _) => { if (_open == s) _open = null; };
        _open.Show();
    }

    /// <summary>The strip drawn off screen at twice its size, one tile hovered (for README images).</summary>
    public static BitmapSource Snapshot(int hover)
    {
        var strip = (FrameworkElement)new DropZones(default).Content;
        var tiles = ((WrapPanel)((StackPanel)((Border)strip).Child).Children[1]).Children;
        SetHot((Border)tiles[hover], hot: true);
        strip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        strip.Arrange(new Rect(strip.DesiredSize));
        strip.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(strip.DesiredSize.Width * 2), (int)Math.Ceiling(strip.DesiredSize.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(strip);
        return bitmap;
    }

    /// <summary>The button came up: give a drop that is still being delivered a moment, then go.</summary>
    public static async void EndDrag()
    {
        var window = _open;
        if (window is null)
            return;
        await Task.Delay(400);
        if (!window._dropped)
            window.Close();
    }

    private DropZones(ScreenPoint origin)
    {
        _origin = origin;
        Title = "RadialShortcut bırakma alanı";
        AllowDrop = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var tiles = new WrapPanel { Width = 3 * 108 };
        foreach (var item in DefaultFileRing.Items.Where(i => ToolIds.Contains(i.Id)))
            tiles.Children.Add(Tile(item));

        // Margin leaves room for the shadow inside the transparent window.
        Content = new Border
        {
            Margin = new Thickness(24),
            Background = Frozen(new RadialGradientBrush(Color.FromArgb(0xF4, 0x2C, 0x2F, 0x36), Color.FromArgb(0xF4, 0x1A, 0x1C, 0x21)) { RadiusX = 0.8, RadiusY = 0.9 }),
            BorderBrush = Frozen(new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Dosyayı bir işin üstüne bırak", Foreground = MutedBrush, FontSize = 11.5, Margin = new Thickness(6, 0, 0, 8) },
                    tiles,
                },
            },
        };

        Loaded += (_, _) => Place();
    }

    private Border Tile(RingItem item)
    {
        var tile = new Border
        {
            Width = 100,
            Height = 76,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = TileFill,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            AllowDrop = true,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(item.Icon),
                        Stroke = TextBrush,
                        StrokeThickness = 1.6,
                        Width = 22,
                        Height = 22,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock { Text = item.Title, Foreground = LabelBrush, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };

        tile.DragEnter += (_, e) => Hover(tile, e, on: true);
        tile.DragOver += (_, e) => Hover(tile, e, on: true);
        tile.DragLeave += (_, e) => Hover(tile, e, on: false);
        tile.Drop += (_, e) =>
        {
            e.Effects = DragDropEffects.Copy; // never let Explorer treat it as a move
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                return;
            _dropped = true;
            Close();
            SelectedFiles.Dropped = files.Where(File.Exists).ToList();
            _ = ToolRunner.RunAsync(item, _origin);
        };
        return tile;
    }

    private static void Hover(Border tile, DragEventArgs e, bool on)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetHot(tile, on && files);
    }

    /// <summary>The hovered tile lights up like the ring's hovered slice.</summary>
    private static void SetHot(Border tile, bool hot)
    {
        tile.Background = hot ? HotFill : TileFill;
        tile.BorderBrush = hot ? HotEdge : Edge;
        tile.Effect = hot ? new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Color = Accent, Opacity = 0.75 } : null;
        var label = (TextBlock)((StackPanel)tile.Child).Children[1];
        label.Foreground = hot ? Brushes.White : LabelBrush;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Beside the drag's start, kept on the same monitor and clear of the cursor.</summary>
    private void Place()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = _origin.X / dpi.DpiScaleX + 40;
        var y = _origin.Y / dpi.DpiScaleY - ActualHeight / 2;
        var area = SystemParameters.WorkArea;
        if (x + ActualWidth > area.Right)
            x = _origin.X / dpi.DpiScaleX - 40 - ActualWidth;
        Left = Math.Max(area.Left, x);
        Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }
}
