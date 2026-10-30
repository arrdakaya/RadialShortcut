using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Video kırp: play the video, mark the start (I) and end (O), save the range.
/// Space plays and pauses, the arrows step a second (Shift: five), Enter saves.
/// </summary>
internal sealed class TrimWindow : Window
{
    private readonly string _file;
    private readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    private readonly Slider _position = new() { Minimum = 0, IsMoveToPointEnabled = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Canvas _range = new() { Height = 6, Margin = new Thickness(5, 0, 5, 0), Background = Palette.Line };
    private readonly Rectangle _selected = new() { Height = 6, Fill = Palette.Accent };
    private readonly TextBlock _info = new() { Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private TimeSpan _length, _in, _out;
    private bool _playing, _dragging, _resumeAfterDrag, _syncing;
    private double? _scrubTo;

    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var files = await SelectedFiles.GetAsync(target, T("Kırpılacak videoyu seç", "Choose a video to trim"), SelectedFiles.Videos);
        var video = files.FirstOrDefault(f => MediaKinds.Of(f) is MediaKind.Video or MediaKind.Audio);
        if (video is null)
        {
            if (files.Count > 0)
                ResultCard.ForMessage(T("Video kırp", "Trim video"), T("Seçilen dosya bir video değil.", "The selected file is not a video."), anchor).ShowNear();
            return;
        }
        Open(video);
    }

    public static void Open(string file) => new TrimWindow(file).Show();

    private TrimWindow(string file)
    {
        _file = file;
        Title = $"{T("Video kırp", "Trim video")} · {Path.GetFileName(file)}";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _range.Children.Add(_selected);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button(T("Başlangıç (I)", "Start (I)"), MarkIn));
        buttons.Children.Add(Button(T("Bitiş (O)", "End (O)"), MarkOut));
        buttons.Children.Add(Button(T("Hızlı kaydet", "Quick save"), () => _ = SaveAsync(exact: false)));
        buttons.Children.Add(Button(T("Kaydet (Enter)", "Save (Enter)"), () => _ = SaveAsync(exact: true), primary: true));

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_info);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_range, Dock.Bottom);
        DockPanel.SetDock(_position, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_range);
        root.Children.Add(_position);
        root.Children.Add(new Border { Background = Brushes.Black, Child = _player });
        Content = root;

        _player.MediaOpened += (_, _) =>
        {
            _length = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            _out = _length;
            _position.Maximum = Math.Max(0.1, _length.TotalSeconds);
            _player.Pause();
            Update();
        };
        _player.MediaFailed += (_, e) => _info.Text = T($"Oynatılamadı: {e.ErrorException?.Message}. Yine de I/O ile süre seçip kaydedebilirsin.", $"Can't play it: {e.ErrorException?.Message}. You can still mark a range with I/O and save.");
        _player.MediaEnded += (_, _) => { _player.Pause(); _playing = false; };
        _player.Source = new Uri(file);
        _player.Play();

        // While the thumb is dragged the video pauses and follows it frame by frame; the seeks are
        // coalesced on the render loop so a fast drag doesn't queue up dozens of them.
        _position.AddHandler(Thumb.DragStartedEvent, new RoutedEventHandler((_, _) =>
        {
            _dragging = true;
            _resumeAfterDrag = _playing;
            if (_playing)
            {
                _player.Pause();
                _playing = false;
            }
        }));
        _position.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) =>
        {
            _dragging = false;
            _scrubTo = null;
            Seek(_position.Value);
            if (_resumeAfterDrag)
            {
                _player.Play();
                _playing = true;
            }
        }));
        _position.ValueChanged += (_, e) =>
        {
            if (_syncing)
                return;
            if (_dragging)
            {
                _scrubTo = e.NewValue;
                Update();
            }
            else
                Seek(e.NewValue);
        };
        CompositionTarget.Rendering += Scrub;
        _range.SizeChanged += (_, _) => Update();
        _tick.Tick += (_, _) =>
        {
            if (!_dragging)
            {
                _syncing = true;
                _position.Value = _player.Position.TotalSeconds;
                _syncing = false;
            }
            Update();
        };
        _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            CompositionTarget.Rendering -= Scrub;
            _player.Close();
        };
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        switch (e.Key)
        {
            case Key.Space:
                if (_playing)
                    _player.Pause();
                else
                    _player.Play();
                _playing = !_playing;
                break;
            case Key.I:
                MarkIn();
                break;
            case Key.O:
                MarkOut();
                break;
            case Key.Left:
                Seek(_player.Position.TotalSeconds - step);
                break;
            case Key.Right:
                Seek(_player.Position.TotalSeconds + step);
                break;
            case Key.Enter:
                _ = SaveAsync(exact: true);
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Seek(double seconds)
    {
        _player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _length.TotalSeconds));
        _syncing = true;
        _position.Value = _player.Position.TotalSeconds;
        _syncing = false;
        Update();
    }

    private void Scrub(object? sender, EventArgs e)
    {
        if (_scrubTo is not { } target)
            return;
        _scrubTo = null;
        _player.Position = TimeSpan.FromSeconds(Math.Clamp(target, 0, _length.TotalSeconds));
    }

    private void MarkIn()
    {
        _in = _player.Position;
        if (_out <= _in)
            _out = _length;
        Update();
    }

    private void MarkOut()
    {
        _out = _player.Position;
        if (_in >= _out)
            _in = TimeSpan.Zero;
        Update();
    }

    private void Update()
    {
        if (_length <= TimeSpan.Zero)
            return;
        var w = _range.ActualWidth;
        Canvas.SetLeft(_selected, w * _in.TotalSeconds / _length.TotalSeconds);
        _selected.Width = Math.Max(1, w * (_out - _in).TotalSeconds / _length.TotalSeconds);
        var now = _dragging ? TimeSpan.FromSeconds(_position.Value) : _player.Position;
        _info.Text = T($"{Format(now)} / {Format(_length)}   ·   seçim {Format(_in)} – {Format(_out)} ({Format(_out - _in)})   ·   Space oynat, ← → sar", $"{Format(now)} / {Format(_length)}   ·   selection {Format(_in)} – {Format(_out)} ({Format(_out - _in)})   ·   Space play, ← → seek");
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

    /// <summary>
    /// Exact re-encodes so the cut lands on the marked frame; quick copies the streams, which is
    /// instant but starts at the keyframe before the mark.
    /// </summary>
    private async Task SaveAsync(bool exact)
    {
        if (_out - _in < TimeSpan.FromSeconds(0.2))
        {
            _info.Text = T("Seçim çok kısa: önce başlangıç (I) ve bitiş (O) işaretle.", "Selection too short: mark the start (I) and end (O) first.");
            return;
        }
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, 1);
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
            return;

        _player.Pause();
        _playing = false;
        var ext = exact ? "mp4" : MediaKinds.Extension(_file);
        string[] seek = ["-ss", Invariant(_in)];
        string[] args = exact
            ? ["-t", Invariant(_out - _in), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]
            : ["-t", Invariant(_out - _in), "-c", "copy", "-avoid_negative_ts", "make_zero"];
        var file = _file;
        var length = _out - _in;
        Close();
        await FileJobs.RunAsync("trim", T("Kırpılıyor", "Trimming"), [file], anchor, async (input, progress, cancel) =>
        {
            var output = FileJobs.OutputFor(input, T(" (kırpılmış)", " (trimmed)"), ext);
            await Ffmpeg.RunAsync(input, args, output, progress, cancel, before: seek, length: length);
            return output;
        });
    }

    private static string Invariant(TimeSpan t) => t.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static Button Button(string label, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
            Foreground = primary ? Brushes.White : Palette.Text,
            Background = primary ? Palette.Accent : Palette.Raised,
            BorderBrush = Palette.Line,
            Focusable = false, // Space and Enter belong to the player, not to the last clicked button.
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
