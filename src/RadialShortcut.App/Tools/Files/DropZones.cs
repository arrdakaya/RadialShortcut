using System.IO;
using System.Windows;
using System.Windows.Controls;
using RadialShortcut.Input;
using System.Windows.Media;

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

    private readonly ScreenPoint _origin;
    private bool _dropped;

    public static void ShowAt(ScreenPoint origin)
    {
        _open?.Close();
        _open = new DropZones(origin);
        _open.Closed += (s, _) => { if (_open == s) _open = null; };
        _open.Show();
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

        Content = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Dosyayı bir işin üstüne bırak", Foreground = Palette.Muted, FontSize = 11, Margin = new Thickness(4, 0, 0, 6) },
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
            CornerRadius = new CornerRadius(8),
            Background = Palette.Raised,
            BorderBrush = Palette.Line,
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
                        Stroke = Palette.Text,
                        StrokeThickness = 1.6,
                        Width = 22,
                        Height = 22,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock { Text = item.Title, Foreground = Palette.Text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
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
        tile.BorderBrush = on && files ? Palette.Accent : Palette.Line;
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
