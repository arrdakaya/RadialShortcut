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
/// Dragging files out of Explorer or the desktop (see FileDrag) opens a strip of tool tiles next to the
/// cursor; dropping the files on a tile runs that tool on them. The strip goes away when the drag
/// ends anywhere else.
/// </summary>
internal sealed class DropZones : Window
{
    private static DropZones? _open;

    // Tools that work on files; download and library have no input to drop.
    private static readonly string[] ToolIds = ["convert", "compress", "resize", "trim", "annotate", "nobg", "unlock"];

    // The ring's look: its accent pair, slice fill, hairline edge and label colors.
    private static readonly Color Accent = (Color)ColorConverter.ConvertFromString(new RingStyle().Accent);
    private static readonly Color Accent2 = RingWindow.HueShift(Accent, 45);
    private static readonly Brush HotFill = Frozen(new LinearGradientBrush(RingWindow.WithAlpha(Accent, 0xE0), RingWindow.WithAlpha(Accent2, 0xC0), 45));
    private static readonly Brush HotEdge = Frozen(new SolidColorBrush(RingWindow.Lighten(Accent, 0.45)));

    // Light or dark, taken when the strip opens (each drag builds a new one).
    private readonly GlassColors _glass = Palette.Glass;
    private readonly Brush _tileFill;
    private readonly Brush _edge;
    private readonly Brush _label;

    private readonly ScreenPoint _origin;
    private bool _dropped;

    /// <summary>Which file kinds each tile takes, as the tools themselves check (the first file decides).</summary>
    private static bool Accepts(string tool, MediaKind kind) => tool switch
    {
        "convert" => kind is MediaKind.Image or MediaKind.Video or MediaKind.Audio or MediaKind.Pdf,
        "compress" => kind is MediaKind.Image or MediaKind.Video or MediaKind.Audio,
        "resize" => kind is MediaKind.Image or MediaKind.Video,
        "trim" => kind is MediaKind.Video or MediaKind.Audio,
        "annotate" or "nobg" => kind == MediaKind.Image,
        _ => true,
    };

    public static bool AnyFor(IReadOnlyList<string> files) => ToolIds.Any(id => Accepts(id, MediaKinds.Of(files[0])));

    /// <param name="files">What is being dragged; empty when unknown, which shows every tile.</param>
    public static void ShowAt(ScreenPoint origin, IReadOnlyList<string> files)
    {
        _open?.Close();
        _open = new DropZones(origin, files);
        _open.Closed += (s, _) => { if (_open == s) _open = null; };
        _open.Show();
    }

    /// <summary>The strip drawn off screen at twice its size, one tile hovered (for README images).</summary>
    public static BitmapSource Snapshot(int hover)
    {
        var zones = new DropZones(default, []);
        var strip = (FrameworkElement)zones.Content;
        var tiles = ((WrapPanel)((StackPanel)((Border)strip).Child).Children[1]).Children;
        zones.SetHot((Border)tiles[hover], hot: true);
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

    private DropZones(ScreenPoint origin, IReadOnlyList<string> files)
    {
        _origin = origin;
        Title = T("RadialShortcut bırakma alanı", "RadialShortcut drop zones");
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
        _tileFill = Frozen(new LinearGradientBrush(_glass.Outer, _glass.Inner, 90));
        _edge = Frozen(new SolidColorBrush(_glass.Edge));
        _label = Frozen(new SolidColorBrush(_glass.Label));

        var items = DefaultFileRing.Items.Where(i => ToolIds.Contains(i.Id) && (files.Count == 0 || Accepts(i.Id, MediaKinds.Of(files[0])))).ToList();
        var tiles = new WrapPanel { Width = Math.Min(3, items.Count) * 108 };
        foreach (var item in items)
            tiles.Children.Add(Tile(item));

        // Margin leaves room for the shadow inside the transparent window.
        Content = new Border
        {
            Margin = new Thickness(24),
            Background = Frozen(new RadialGradientBrush(_glass.CenterLight, _glass.CenterDark) { RadiusX = 0.8, RadiusY = 0.9 }),
            BorderBrush = Frozen(new LinearGradientBrush(_glass.EdgeTop, _glass.EdgeBottom, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = _glass.Shadow, Color = Colors.Black },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = T("Dosyayı bir işin üstüne bırak", "Drop the file on a tool"), Foreground = Frozen(new SolidColorBrush(_glass.Muted)), FontSize = 11.5, Margin = new Thickness(6, 0, 0, 8) },
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
            Background = _tileFill,
            BorderBrush = _edge,
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
                        Stroke = Frozen(new SolidColorBrush(_glass.Text)),
                        StrokeThickness = 1.6,
                        Width = 22,
                        Height = 22,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock { Text = item.Title, Foreground = _label, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
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

    private void Hover(Border tile, DragEventArgs e, bool on)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetHot(tile, on && files);
    }

    /// <summary>The hovered tile lights up like the ring's hovered slice.</summary>
    private void SetHot(Border tile, bool hot)
    {
        tile.Background = hot ? HotFill : _tileFill;
        tile.BorderBrush = hot ? HotEdge : _edge;
        tile.Effect = hot ? new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Color = Accent, Opacity = 0.75 } : null;
        var label = (TextBlock)((StackPanel)tile.Child).Children[1];
        label.Foreground = hot ? Brushes.White : _label;
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
