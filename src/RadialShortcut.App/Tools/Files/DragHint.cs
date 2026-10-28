using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.App.Tools;

/// <summary>
/// A small "Shift: tools" label beside the cursor while a file is dragged out of Explorer without
/// Shift, so the drop zones can be found. Shown for the first few drags only, and click-through so
/// the drop still lands on whatever is below.
/// </summary>
internal sealed class DragHint : Window
{
    private const int Limit = 5;

    private static DragHint? _open;

    public static void ShowAt(ScreenPoint at)
    {
        var shown = Count();
        if (shown >= Limit)
            return;
        try { File.WriteAllText(AppPaths.DragHintFile, (shown + 1).ToString()); } catch (IOException) { }

        Dismiss();
        _open = new DragHint(at);
        _open.Show();
    }

    public static void Dismiss()
    {
        _open?.Close();
        _open = null;
    }

    private static int Count()
    {
        try { return int.TryParse(File.ReadAllText(AppPaths.DragHintFile), out var n) ? n : 0; }
        catch (IOException) { return 0; }
    }

    private DragHint(ScreenPoint at)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock { Text = "Bekle ya da Shift: araçlar", Foreground = Palette.Text, FontSize = 12 },
        };

        SourceInitialized += (_, _) => Native.MakeClickThrough(this);
        Loaded += (_, _) =>
        {
            // Below-right of the cursor, clear of Explorer's drag image.
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = at.X / dpi.DpiScaleX + 24;
            Top = at.Y / dpi.DpiScaleY + 28;
        };
    }
}
