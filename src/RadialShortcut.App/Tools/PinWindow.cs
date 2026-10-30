using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App.Tools;

/// <summary>
/// "Ekrana sabitle": a region kept on top of everything as a picture, right where it was taken, to look
/// at while working elsewhere. Drag to move, wheel to zoom, double-click or Esc to close.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double MinZoom = 0.2, MaxZoom = 4;

    private readonly CapturedRegion _region;
    private readonly ScaleTransform _zoom = new(1, 1);

    public static async Task RunAsync()
    {
        if (await RegionPicker.PickAsync() is { } region)
            new PinWindow(region).Show();
    }

    private PinWindow(CapturedRegion region)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = Top = -10000;
        Cursor = Cursors.SizeAll;
        ToolTip = T("Sürükle: taşı · Tekerlek veya + / −: büyüt-küçült · Çift tık / Esc: kapat", "Drag: move · Wheel or + / −: zoom · Double-click / Esc: close");

        // One image pixel per screen pixel at zoom 1, so the pin sits exactly over what it shows.
        var image = new Image
        {
            Source = region.Image,
            Width = region.Image.PixelWidth / region.Display.Scale,
            Height = region.Image.PixelHeight / region.Display.Scale,
            LayoutTransform = _zoom,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Margin = new Thickness(8), // room for the shadow
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xA0, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 },
            Child = image,
        };
        var root = new Grid();
        root.Children.Add(frame);
        root.Children.Add(BuildCloseButton());
        Content = root;
        ContextMenu = BuildMenu();

        Loaded += (_, _) =>
        {
            var margin = (int)Math.Round(9 * region.Display.Scale);
            ScreenCapture.Place(this, region.ScreenRect.X - margin, region.ScreenRect.Y - margin);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                Close();
            else
                DragMove();
        };
        MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Add or Key.OemPlus: Zoom(1.25); break;
                case Key.Subtract or Key.OemMinus: Zoom(1 / 1.25); break;
                case Key.D0 or Key.NumPad0: _zoom.ScaleX = _zoom.ScaleY = 1; break;
            }
        };
    }

    private void Zoom(double factor)
    {
        var zoom = Math.Clamp(_zoom.ScaleX * factor, MinZoom, MaxZoom);
        _zoom.ScaleX = _zoom.ScaleY = zoom;
    }

    /// <summary>The ✕ in the top-right corner; dim until the cursor reaches it so it hides little of the picture.</summary>
    private FrameworkElement BuildCloseButton()
    {
        var idle = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20));
        var hover = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40));
        var button = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = idle,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0), // straddles the frame's corner
            Cursor = Cursors.Hand,
            ToolTip = T("Kapat", "Close"),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = idle;
        // Handled here so the window's drag does not start and swallow the click.
        // Closes only when the press also started on the button, like a real button.
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            button.CaptureMouse();
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!button.IsMouseCaptured)
                return;
            button.ReleaseMouseCapture();
            if (button.IsMouseOver)
                Close();
        };
        return button;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        Add(T("Kopyala", "Copy"), () => ToolRunner.CopyImage(_region.Image));
        Add(T("Dosyaya kaydet", "Save to file"), () => ToolRunner.ShowInFolder(ToolRunner.Save(_region.Image)));
        Add(T("Büyüt (+)", "Zoom in (+)"), () => Zoom(1.25));
        Add(T("Küçült (−)", "Zoom out (−)"), () => Zoom(1 / 1.25));
        Add(T("Gerçek boyut (0)", "Actual size (0)"), () => _zoom.ScaleX = _zoom.ScaleY = 1);
        menu.Items.Add(new Separator());
        Add(T("Kapat", "Close"), Close);
        return menu;
    }
}
