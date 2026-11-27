using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RadialShortcut.App.Tools;

/// <summary>
/// A few settings on a card before a file job: rows of choice chips, a text box or a slider, then
/// Cancel and the action. Enter in a text box counts as the action, Esc or clicking away as Cancel.
/// Each Add returns a getter for the value as it stands when the card closes.
/// </summary>
internal sealed class OptionsForm
{
    private readonly StackPanel _body = new() { MinWidth = 300 };
    private TextBox? _firstText;
    private Action? _submit;

    public Func<int> Chips(string label, IReadOnlyList<string> options, int selected, Action<int>? changed = null)
    {
        Label(label);
        var row = new WrapPanel();
        var buttons = new List<Button>();
        void Mark()
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].Background = i == selected ? Palette.Accent : Palette.Raised;
                buttons[i].Foreground = i == selected ? Brushes.White : Palette.Text;
            }
        }
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var button = new Button { Content = options[i], Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 4), BorderBrush = Palette.Line, Focusable = false, Cursor = Cursors.Hand };
            button.Click += (_, _) => { selected = index; Mark(); changed?.Invoke(index); };
            buttons.Add(button);
            row.Children.Add(button);
        }
        Mark();
        _body.Children.Add(row);
        return () => selected;
    }

    public TextBox Text(string label, string value, string? hint = null)
    {
        Label(label);
        var box = new TextBox { Text = value, Padding = new Thickness(6, 3, 6, 3), Foreground = Palette.Text, Background = Palette.Raised, BorderBrush = Palette.Line, CaretBrush = Palette.Text };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _submit?.Invoke();
            }
        };
        _firstText ??= box;
        _body.Children.Add(box);
        if (hint is not null)
            Hint(hint);
        return box;
    }

    public Func<int> Slider(string label, int min, int max, int value, Func<int, string> describe)
    {
        var title = Label(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Focusable = false };
        void Show() => title.Text = $"{label}: {describe((int)slider.Value)}";
        slider.ValueChanged += (_, _) => Show();
        Show();
        _body.Children.Add(slider);
        return () => (int)slider.Value;
    }

    public void Hint(string text) =>
        _body.Children.Add(new TextBlock { Text = text, Foreground = Palette.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

    private TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, Foreground = Palette.Text, FontSize = 12, Margin = new Thickness(0, _body.Children.Count == 0 ? 0 : 10, 0, 4) };
        _body.Children.Add(label);
        return label;
    }

    /// <summary>True when the action was chosen.</summary>
    public Task<bool> ShowAsync(string title, string? subtitle, string action, Int32Rect anchor)
    {
        var answer = new TaskCompletionSource<bool>();
        var card = ResultCard.ForContent(title, subtitle, _body, anchor)
            .WithButton(T("Vazgeç", "Cancel"), () => answer.TrySetResult(false))
            .WithButton(action, () => answer.TrySetResult(true), primary: true);
        _submit = () =>
        {
            answer.TrySetResult(true);
            card.Dismiss();
        };
        card.Closed += (_, _) => answer.TrySetResult(false);
        card.Loaded += (_, _) =>
        {
            if (_firstText is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        };
        card.ShowNear();
        return answer.Task;
    }
}
