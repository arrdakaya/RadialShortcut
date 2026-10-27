using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using RadialShortcut.App.Capture;

namespace RadialShortcut.App;

/// <summary>
/// Small card shown next to the captured region (PLAN.md, section 6): a preview plus a few buttons.
/// Stays while hovered or focused; closes on Esc or when the user clicks elsewhere.
/// </summary>
public sealed class ResultCard : Window
{
    private static Brush Surface => Palette.Surface;
    private static Brush Line => Palette.Line;
    private static Brush Text => Palette.Text;
    private static Brush Muted => Palette.Muted;
    private static Brush Accent => Palette.Accent;

    // Wraps, so a card with many choices ("PNG", "JPG", "WebP"...) stays narrow.
    private readonly WrapPanel _buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, -6) };
    private readonly Int32Rect _anchor;

    private ResultCard(string title, string? subtitle, UIElement? body, Int32Rect anchor)
    {
        _anchor = anchor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        var panel = new StackPanel { MinWidth = 220, MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Text, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (subtitle is not null)
            panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (body is not null)
        {
            if (body is FrameworkElement fe)
                fe.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(body);
        }
        panel.Children.Add(_buttons);

        Content = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(8),
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.4 },
            Child = panel,
        };

        Loaded += (_, _) => PlaceNextToAnchor();
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Dismiss();
        };
    }

    private bool _closing;

    /// <summary>Closing deactivates the window, which would call Close again and throw; this makes it idempotent.</summary>
    public void Dismiss()
    {
        if (!_closing)
            Close();
    }

    public static ResultCard ForText(string title, string text, Int32Rect anchor) =>
        new(title, null, new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Foreground = Text,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Padding = new Thickness(0),
        }, anchor);

    public static ResultCard ForImage(string title, string subtitle, BitmapSource image, Int32Rect anchor) =>
        new(title, subtitle, new Image { Source = image, MaxWidth = 320, MaxHeight = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }, anchor);

    public static ResultCard ForMessage(string title, string message, Int32Rect anchor) => new(title, message, null, anchor);

    public static ResultCard ForContent(string title, string? subtitle, UIElement body, Int32Rect anchor) => new(title, subtitle, body, anchor);

    /// <summary>A message near a point; for tools that start without a region.</summary>
    public static ResultCard Near((int X, int Y) point, string title, string message) => ForMessage(title, message, new Int32Rect(point.X, point.Y, 1, 1));

    /// <summary>
    /// Asks a yes/no question and completes when the card closes. Anything but the "yes" button,
    /// including Esc or clicking elsewhere, counts as no.
    /// </summary>
    public static Task<bool> AskAsync(string title, string message, string yes, Int32Rect anchor)
    {
        var answer = new TaskCompletionSource<bool>();
        var card = ForMessage(title, message, anchor)
            .WithButton("Vazgeç", () => answer.TrySetResult(false))
            .WithButton(yes, () => answer.TrySetResult(true), primary: true);
        card.Closed += (_, _) => answer.TrySetResult(false);
        card.ShowNear();
        return answer.Task;
    }

    /// <summary>
    /// Offers a few choices and completes with the index of the one clicked, or -1 when the card
    /// is closed any other way.
    /// </summary>
    public static Task<int> ChooseAsync(string title, string? message, IReadOnlyList<string> choices, Int32Rect anchor)
    {
        var answer = new TaskCompletionSource<int>();
        var card = new ResultCard(title, message, null, anchor);
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            card.WithButton(choices[i], () => answer.TrySetResult(index), primary: i == 0);
        }
        card.Closed += (_, _) => answer.TrySetResult(-1);
        card.ShowNear();
        return answer.Task;
    }

    public ResultCard WithButton(string label, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(6, 0, 0, 6),
            Padding = new Thickness(12, 5, 12, 5),
            Foreground = primary ? Brushes.White : Text,
            Background = primary ? Accent : Line,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(),
        };
        button.Click += (_, _) =>
        {
            onClick();
            Dismiss();
        };
        _buttons.Children.Add(button);
        return this;
    }

    public void ShowNear()
    {
        if (_buttons.Children.Count == 0)
            WithButton("Kapat", Dismiss);
        Show();
        Activate();
    }

    /// <summary>
    /// Shows the card briefly without taking focus, so the window the user works in stays active.
    /// </summary>
    public void ShowToast(int milliseconds = 1600)
    {
        ShowActivated = false;
        Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss();
        };
        timer.Start();
    }

    /// <summary>To the right of the region if it fits, otherwise below, otherwise above; always inside the work area.</summary>
    private void PlaceNextToAnchor()
    {
        var display = ScreenCapture.DisplayAt(_anchor.X + _anchor.Width / 2, _anchor.Y + _anchor.Height / 2);
        var work = display.WorkArea;
        int w = (int)Math.Ceiling(ActualWidth * display.Scale), h = (int)Math.Ceiling(ActualHeight * display.Scale);
        int right = _anchor.X + _anchor.Width, bottom = _anchor.Y + _anchor.Height;

        int x, y;
        if (right + w <= work.X + work.Width)
            (x, y) = (right, _anchor.Y);
        else if (bottom + h <= work.Y + work.Height)
            (x, y) = (_anchor.X, bottom);
        else
            (x, y) = (_anchor.X, _anchor.Y - h);

        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - w));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        ScreenCapture.Place(this, x, y);
    }

    private static ControlTemplate ButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(TextElement.FontSizeProperty, 12.0);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
