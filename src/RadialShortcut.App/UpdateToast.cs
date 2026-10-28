using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadialShortcut.App.Tools;

namespace RadialShortcut.App;

/// <summary>
/// A card above the taskbar's corner while an update downloads and installs, so the app closing
/// and coming back does not look like a crash. Click-through; it goes away with the app.
/// </summary>
internal sealed class UpdateToast : Window
{
    private readonly TextBlock _status = new() { Foreground = Palette.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
    private readonly ProgressBar _bar = new() { Height = 4, IsIndeterminate = true, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0) };

    public UpdateToast(Version version)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _bar.Foreground = Palette.Accent;
        _bar.Background = Palette.Line;
        Content = new Border
        {
            Background = Palette.Raised,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"RadialShortcut v{version.ToString(3)} kuruluyor", Foreground = Palette.Text, FontSize = 13, FontWeight = FontWeights.SemiBold },
                    _status,
                    _bar,
                },
            },
        };

        SourceInitialized += (_, _) =>
        {
            Native.MakeClickThrough(this);
            // The update waits for a recording to end, so the card must not end up in it.
            Native.ExcludeFromCapture(this);
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
    }

    public void Status(string text) => _status.Text = text;

    /// <summary>0 to 1 while the size is known; the bar keeps moving on its own otherwise.</summary>
    public void Progress(double done)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = done;
    }
}
