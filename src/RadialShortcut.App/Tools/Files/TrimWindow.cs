using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Video kırp: keep pieces of one or more videos and save them, in the list's order, as one clip.
/// More files come in with "Dosya ekle" or by dropping them on the window; the player shows one
/// file at a time and the timeline that file's pieces.
/// I and O move the start and end of the selected piece, S splits it at the playhead, N starts a
/// new piece there and Delete drops the selected one, so cutting out the middle is S, S, Delete.
/// P plays only the kept pieces, Space plays and pauses, the arrows step a second (Shift: five,
/// , and . a frame), Ctrl+Z undoes, Enter saves.
/// </summary>
internal sealed class TrimWindow : Window
{
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1 / 30.0);

    private sealed record Source(string File, TimeSpan Length);

    private readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    private readonly Slider _position = new() { Minimum = 0, IsMoveToPointEnabled = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Canvas _timeline = new() { Height = 22, Margin = new Thickness(5, 4, 5, 0), Background = Palette.Line, ClipToBounds = true, Cursor = Cursors.Hand };
    private readonly Rectangle _playhead = new() { Width = 2, Height = 22, Fill = Palette.Text, IsHitTestVisible = false };
    private readonly ComboBox _fileBox = new() { Focusable = false, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new() { Background = Palette.Raised, Foreground = Palette.Text, BorderBrush = Palette.Line, Focusable = false };
    private readonly TextBlock _total = new() { Foreground = Palette.Muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _info = new() { Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _previewButton;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<Source> _sources = [];
    private readonly List<Cut> _cuts = [];
    private readonly Stack<(List<Cut> Cuts, int Selected)> _undo = new();
    private int _current = -1; // the file in the player
    private TimeSpan _length; // of that file
    private TimeSpan? _pendingSeek; // where to go once the file being opened is ready
    private int _selected = -1;
    private int _previewing = -1; // the piece P is playing, or -1
    private bool _previewSettled; // Position has caught up with the seek to the previewed piece
    private long _previewSince;
    private bool _playing, _dragging, _resumeAfterDrag, _syncing, _listing, _saving, _closeArmed, _closed, _adding;
    private double? _scrubTo;
    private string? _message;

    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var files = await SelectedFiles.GetAsync(target, T("Kırpılacak videoları seç", "Choose videos to trim"), SelectedFiles.Videos);
        var media = files.Where(IsMedia).ToList();
        if (media.Count == 0)
        {
            if (files.Count > 0)
                ResultCard.ForMessage(T("Video kırp", "Trim video"), T("Seçilen dosya bir video değil.", "The selected file is not a video."), anchor).ShowNear();
            return;
        }
        Open(media);
    }

    public static void Open(string file) => Open([file]);

    public static void Open(IReadOnlyList<string> files) => new TrimWindow(files).Show();

    private static bool IsMedia(string file) => MediaKinds.Of(file) is MediaKind.Video or MediaKind.Audio;

    private TrimWindow(IReadOnlyList<string> files)
    {
        Title = T("Video kırp", "Trim video");
        Width = 1100;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        AllowDrop = true;

        // Files and pieces on the right, the pieces in the order they are joined.
        var addFile = Button(T("+ Dosya ekle", "+ Add file"), () => _ = PickFilesAsync(), tip: T("Başka videolar ekle; parçaları sırayla birleşir. Pencereye sürükleyip bırakmak da olur.", "Add more videos; their pieces are joined in order. Dropping them on the window works too."), first: true);
        addFile.HorizontalAlignment = HorizontalAlignment.Stretch;
        addFile.Margin = new Thickness(0, 0, 0, 12);
        var listTools = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        listTools.Children.Add(Button("↑", () => Move(-1), tip: T("Yukarı taşı (Alt+↑)", "Move up (Alt+↑)"), first: true));
        listTools.Children.Add(Button("↓", () => Move(+1), tip: T("Aşağı taşı (Alt+↓)", "Move down (Alt+↓)")));
        listTools.Children.Add(Button(T("Sil", "Delete"), Delete, tip: T("Seçili parçayı sil (Del)", "Delete the selected piece (Del)")));
        var side = new DockPanel { Width = 270, Margin = new Thickness(12, 0, 0, 0) };
        var top = new StackPanel();
        top.Children.Add(Heading(T("Gösterilen dosya", "Showing")));
        top.Children.Add(_fileBox);
        top.Children.Add(addFile);
        top.Children.Add(Heading(T("Parçalar (birleşme sırası)", "Pieces (joined in this order)")));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_total, Dock.Bottom);
        DockPanel.SetDock(listTools, Dock.Bottom);
        side.Children.Add(top);
        side.Children.Add(_total);
        side.Children.Add(listTools);
        side.Children.Add(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (_listing || _list.SelectedIndex < 0)
                return;
            StopPreview();
            _selected = _list.SelectedIndex;
            Show(_cuts[_selected].Source, _cuts[_selected].Start);
            DrawTimeline();
            Update();
        };
        _fileBox.SelectionChanged += (_, _) =>
        {
            if (_listing || _fileBox.SelectedIndex < 0 || _fileBox.SelectedIndex == _current)
                return;
            StopPreview();
            var source = _fileBox.SelectedIndex;
            _selected = _cuts.FindIndex(c => c.Source == source);
            Show(source, _selected >= 0 ? _cuts[_selected].Start : TimeSpan.Zero);
            Refresh();
        };

        // Editing on the left, saving on the right.
        var edit = new WrapPanel { Orientation = Orientation.Horizontal };
        edit.Children.Add(Button(T("Başlangıç (I)", "Start (I)"), MarkIn, tip: T("Seçili parçanın başlangıcı burası", "The selected piece starts here"), first: true));
        edit.Children.Add(Button(T("Bitiş (O)", "End (O)"), MarkOut, tip: T("Seçili parçanın bitişi burası", "The selected piece ends here")));
        edit.Children.Add(Button(T("Böl (S)", "Split (S)"), Split, tip: T("Parçayı oynatma noktasından ikiye böl", "Split the piece at the playhead")));
        edit.Children.Add(Button(T("Yeni parça (N)", "New piece (N)"), NewPiece, tip: T("Buradan başlayan yeni bir parça ekle", "Add a new piece starting here")));
        edit.Children.Add(_previewButton = Button(T("Önizle (P)", "Preview (P)"), Preview, tip: T("Sadece tutulan parçaları sırayla oynat", "Play only the kept pieces, in order")));
        var save = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        save.Children.Add(Button(T("Hızlı kaydet", "Quick save"), () => _ = SaveAsync(exact: false), tip: T("Anında, ama kesimler önceki anahtar kareye kayar. Biçimleri farklı dosyalar yine de tam kaydedilir.", "Instant, but cuts snap to the keyframe before. Files of different formats are still saved exactly.")));
        save.Children.Add(Button(T("Kaydet (Enter)", "Save (Enter)"), () => _ = SaveAsync(exact: true), primary: true, tip: T("Kesimler tam karesinde olur; farklı boyuttaki videolar ilk videonun boyutuna getirilir", "Cuts land on the exact frame; videos of other sizes are fitted to the first one's")));

        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(save, Dock.Right);
        actions.Children.Add(save);
        actions.Children.Add(edit);

        var info = new Border { Margin = new Thickness(0, 8, 0, 0), Child = _info };

        var main = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        DockPanel.SetDock(info, Dock.Bottom);
        DockPanel.SetDock(_timeline, Dock.Bottom);
        DockPanel.SetDock(_position, Dock.Bottom);
        main.Children.Add(actions);
        main.Children.Add(info);
        main.Children.Add(_timeline);
        main.Children.Add(_position);
        main.Children.Add(new Border { Background = Brushes.Black, Child = _player });

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(main);
        Content = root;

        _player.MediaOpened += (_, _) =>
        {
            if (_current < 0)
                return;
            if (_player.NaturalDuration.HasTimeSpan && _player.NaturalDuration.TimeSpan > TimeSpan.Zero)
            {
                _length = _player.NaturalDuration.TimeSpan;
                _sources[_current] = _sources[_current] with { Length = _length };
            }
            _position.Maximum = Math.Max(0.1, _length.TotalSeconds);
            if (_pendingSeek is { } at)
            {
                _pendingSeek = null;
                Seek(at.TotalSeconds);
            }
            if (!_playing)
                _player.Pause();
            _previewSince = Environment.TickCount64;
            Refresh();
        };
        _player.MediaFailed += (_, e) =>
        {
            _pendingSeek = null;
            if (_previewing >= 0)
                NextPreview(); // can't show this piece; go on rather than wait on it
            Say(T($"Oynatılamadı: {e.ErrorException?.Message}. Parçaları yine de kaydedebilirsin.", $"Can't play it: {e.ErrorException?.Message}. You can still save its pieces."));
        };
        _player.MediaEnded += (_, _) =>
        {
            // A piece that runs to the end of the file never reaches its End by position; go on to the next one.
            if (_previewing >= 0)
            {
                NextPreview();
                if (_previewing >= 0)
                    _player.Play();
                return;
            }
            _player.Pause();
            _playing = false;
        };

        // While the thumb is dragged the video pauses and follows it frame by frame; the seeks are
        // coalesced on the render loop so a fast drag doesn't queue up dozens of them.
        _position.AddHandler(Thumb.DragStartedEvent, new RoutedEventHandler((_, _) =>
        {
            StopPreview();
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
            {
                StopPreview();
                Seek(e.NewValue);
            }
        };
        _timeline.MouseLeftButtonDown += (_, e) => ClickTimeline(e.GetPosition(_timeline).X);
        CompositionTarget.Rendering += OnFrame;
        _timeline.SizeChanged += (_, _) => DrawTimeline();
        _tick.Tick += (_, _) =>
        {
            if (!_dragging && _pendingSeek is null)
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
            _closed = true;
            _tick.Stop();
            CompositionTarget.Rendering -= OnFrame;
            _player.Close();
        };
        PreviewKeyDown += OnKey;
        // By the character, not the key: on a Turkish Q keyboard . and , aren't on the OEM keys a US layout uses.
        PreviewTextInput += (_, e) =>
        {
            if (e.Text is not ("," or "."))
                return;
            Jump(e.Text == "," ? -Frame.TotalSeconds : Frame.TotalSeconds);
            e.Handled = true;
        };
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                _ = AddFilesAsync(dropped);
        };
        Loaded += (_, _) => _ = AddFilesAsync(files);
    }

    private async Task PickFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = T("Eklenecek videoları seç", "Choose videos to add"), Filter = SelectedFiles.Videos, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await AddFilesAsync(dialog.FileNames);
    }

    /// <summary>
    /// Adds the files as sources, each with one piece covering all of it, after the selected
    /// piece, and shows the first of them.
    /// </summary>
    private async Task AddFilesAsync(IEnumerable<string> files)
    {
        if (_adding)
            return;
        _adding = true;
        try
        {
            var wanted = files.Where(IsMedia).ToList();
            var skipped = files.Count() - wanted.Count;
            var unreadable = new List<string>();
            var added = new List<Cut>();
            foreach (var file in wanted)
            {
                Say(T($"Açılıyor: {Path.GetFileName(file)}", $"Opening {Path.GetFileName(file)}"));
                var source = _sources.FindIndex(s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase));
                if (source < 0)
                {
                    var length = await LengthAsync(file);
                    if (_closed)
                        return;
                    if (length is not { } l)
                    {
                        unreadable.Add(Path.GetFileName(file));
                        continue;
                    }
                    source = _sources.Count;
                    _sources.Add(new Source(file, l));
                }
                added.Add(new Cut(TimeSpan.Zero, _sources[source].Length, source));
            }

            _message = null;
            if (added.Count > 0)
            {
                if (_cuts.Count > 0)
                    Remember();
                var at = HasSelection ? _selected + 1 : _cuts.Count;
                _cuts.InsertRange(at, added);
                _selected = at;
                Show(added[0].Source, TimeSpan.Zero);
            }
            Refresh();
            if (unreadable.Count > 0)
                Say(T($"Açılamadı: {string.Join(", ", unreadable)}", $"Couldn't open {string.Join(", ", unreadable)}"));
            else if (skipped > 0)
                Say(T($"{skipped} dosya video ya da ses değil, atlandı.", $"Skipped {skipped} file(s) that aren't video or audio."));
        }
        finally
        {
            _adding = false;
        }
    }

    /// <summary>
    /// The file's length, from Windows' own player without showing it, or from ffmpeg for a file
    /// that player can't open. Null when neither can read it.
    /// </summary>
    private static async Task<TimeSpan?> LengthAsync(string file)
    {
        var opened = new TaskCompletionSource<TimeSpan?>();
        var probe = new MediaPlayer { IsMuted = true };
        probe.MediaOpened += (_, _) => opened.TrySetResult(probe.NaturalDuration.HasTimeSpan ? probe.NaturalDuration.TimeSpan : null);
        probe.MediaFailed += (_, _) => opened.TrySetResult(null);
        probe.Open(new Uri(file));
        var length = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(15))) == opened.Task ? opened.Task.Result : null;
        probe.Close();
        if (length > TimeSpan.Zero)
            return length;
        if (!File.Exists(ToolDeps.PathOf(ToolDeps.Ffmpeg)))
            return null;
        try
        {
            var info = await Ffmpeg.ProbeAsync(file, CancellationToken.None);
            return info.Duration > TimeSpan.Zero ? info.Duration : null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Puts file <paramref name="source"/> in the player at <paramref name="at"/>, opening it first if another one is there.</summary>
    private void Show(int source, TimeSpan at)
    {
        if (source < 0 || source >= _sources.Count)
            return;
        if (source == _current)
        {
            Seek(at.TotalSeconds);
            return;
        }
        _current = source;
        _length = _sources[source].Length;
        _position.Maximum = Math.Max(0.1, _length.TotalSeconds);
        _pendingSeek = at;
        _syncing = true;
        _position.Value = at.TotalSeconds;
        _syncing = false;
        _player.Source = new Uri(_sources[source].File);
        _player.Play(); // opens it; MediaOpened pauses again unless something is playing
        var name = Path.GetFileName(_sources[source].File);
        Title = _sources.Count > 1 ? $"{T("Video kırp", "Trim video")} · {name} ({source + 1}/{_sources.Count})" : $"{T("Video kırp", "Trim video")} · {name}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var step = mods.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt turns arrows into system keys.
        if (key != Key.Escape)
            _closeArmed = false;
        switch (key)
        {
            case Key.Space:
                PlayPause();
                break;
            case Key.I:
                MarkIn();
                break;
            case Key.O:
                MarkOut();
                break;
            case Key.S:
                Split();
                break;
            case Key.N:
                NewPiece();
                break;
            case Key.P:
                Preview();
                break;
            case Key.Delete or Key.Back:
                Delete();
                break;
            case Key.Z when mods.HasFlag(ModifierKeys.Control):
                Undo();
                break;
            case Key.Up when mods.HasFlag(ModifierKeys.Alt):
                Move(-1);
                break;
            case Key.Down when mods.HasFlag(ModifierKeys.Alt):
                Move(+1);
                break;
            case Key.Up:
                Select(_selected - 1);
                break;
            case Key.Down:
                Select(_selected + 1);
                break;
            case Key.Left:
                Jump(-step);
                break;
            case Key.Right:
                Jump(step);
                break;
            case Key.Home:
                Jump(-_length.TotalSeconds);
                break;
            case Key.End:
                Jump(_length.TotalSeconds);
                break;
            case Key.Enter:
                _ = SaveAsync(exact: true);
                break;
            case Key.Escape when _undo.Count > 0 && !_closeArmed:
                // The pieces may be a few minutes of work; one stray Esc shouldn't throw them away.
                _closeArmed = true;
                Say(T("Parçalar kaydedilmedi. Yine de kapatmak için tekrar Esc'ye bas.", "The pieces aren't saved. Press Esc again to close anyway."));
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void PlayPause()
    {
        if (_current < 0)
            return;
        StopPreview();
        if (_playing)
            _player.Pause();
        else
            _player.Play();
        _playing = !_playing;
    }

    private void Jump(double seconds)
    {
        StopPreview();
        Seek(Now.TotalSeconds + seconds);
    }

    private void Seek(double seconds)
    {
        var at = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _length.TotalSeconds));
        if (_pendingSeek is not null)
            _pendingSeek = at; // the file is still opening
        else
            _player.Position = at;
        _syncing = true;
        _position.Value = at.TotalSeconds;
        _syncing = false;
        Update();
    }

    /// <summary>Scrubbing, and while previewing, the jump from the end of one piece to the start of the next.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_scrubTo is { } target)
        {
            _scrubTo = null;
            if (_pendingSeek is null)
                _player.Position = TimeSpan.FromSeconds(Math.Clamp(target, 0, _length.TotalSeconds));
        }
        if (_previewing < 0 || _previewing >= _cuts.Count || _pendingSeek is not null)
            return;
        var cut = _cuts[_previewing];
        var at = _player.Position;
        if (!_previewSettled)
        {
            // Right after a seek Position can still read the old spot for a frame or two, which is
            // past the end of a piece that comes earlier in the file; wait until it has arrived.
            if (at >= cut.Start - TimeSpan.FromSeconds(0.5) && at < cut.End || Environment.TickCount64 - _previewSince > 1000)
                _previewSettled = true;
            return;
        }
        if (at >= cut.End)
            NextPreview();
    }

    private void NextPreview()
    {
        _previewing++;
        if (_previewing < _cuts.Count)
            PreviewPiece(_previewing);
        else
        {
            _player.Pause();
            _playing = false;
            StopPreview();
        }
    }

    private void PreviewPiece(int index)
    {
        _previewing = index;
        _selected = index;
        _previewSettled = false;
        _previewSince = Environment.TickCount64;
        Show(_cuts[index].Source, _cuts[index].Start);
        Refresh();
    }

    private void Preview()
    {
        if (_previewing >= 0)
        {
            _player.Pause();
            _playing = false;
            StopPreview();
            return;
        }
        if (_cuts.Count == 0)
            return;
        _previewButton.Content = T("Önizlemeyi durdur (P)", "Stop preview (P)");
        _playing = true; // before Show, so a file that has to be opened starts playing once it is
        PreviewPiece(0);
        _player.Play();
    }

    private void StopPreview()
    {
        if (_previewing < 0)
            return;
        _previewing = -1;
        _previewButton.Content = T("Önizle (P)", "Preview (P)");
    }

    private TimeSpan Now => _pendingSeek ?? (_dragging ? TimeSpan.FromSeconds(_position.Value) : _player.Position);

    private bool HasSelection => _selected >= 0 && _selected < _cuts.Count;

    /// <summary>The selected piece is in the file the player shows, so I and O can move it.</summary>
    private bool SelectedHere => HasSelection && _cuts[_selected].Source == _current;

    private void MarkIn()
    {
        if (!SelectedHere)
        {
            NewPiece();
            return;
        }
        Remember();
        var c = _cuts[_selected];
        _cuts[_selected] = c with { Start = Now, End = c.End <= Now ? _length : c.End };
        Refresh();
    }

    private void MarkOut()
    {
        if (!SelectedHere)
        {
            Say(T("Bu dosyada seçili parça yok: önce I ya da N ile bir parça başlat.", "No piece of this file is selected: start one with I or N first."));
            return;
        }
        Remember();
        var c = _cuts[_selected];
        _cuts[_selected] = c with { Start = c.Start >= Now ? TimeSpan.Zero : c.Start, End = Now };
        Refresh();
    }

    /// <summary>Splits the piece under the playhead, or the selected one when the playhead is in a gap.</summary>
    private void Split()
    {
        var at = Now;
        var index = SelectedHere && _cuts[_selected].Contains(at) ? _selected : VideoCuts.IndexAt(_cuts, _current, at);
        if (index < 0)
        {
            Say(T("Oynatma noktası bir parçanın içinde değil.", "The playhead isn't inside a piece."));
            return;
        }
        StopPreview();
        var before = Snapshot();
        if (!VideoCuts.Split(_cuts, index, at))
        {
            Say(T("Parçanın kenarına çok yakın, bölünemez.", "Too close to the edge of the piece to split."));
            return;
        }
        _undo.Push(before);
        _selected = index + 1;
        Refresh();
    }

    /// <summary>A piece from the playhead to the end of the file, put right after the selected one; O then sets its end.</summary>
    private void NewPiece()
    {
        if (_current < 0)
            return;
        if (_length - Now < VideoCuts.Shortest)
        {
            Say(T("Videonun sonundasın; yeni parça için geri sar.", "You're at the end of the video; seek back to start a new piece."));
            return;
        }
        Remember();
        _selected = HasSelection ? _selected + 1 : _cuts.Count;
        _cuts.Insert(_selected, new Cut(Now, _length, _current));
        Refresh();
    }

    private void Delete()
    {
        if (!HasSelection)
            return;
        Remember();
        _cuts.RemoveAt(_selected);
        _selected = Math.Min(_selected, _cuts.Count - 1);
        Refresh();
    }

    private void Move(int by)
    {
        var to = _selected + by;
        if (!HasSelection || to < 0 || to >= _cuts.Count)
            return;
        Remember();
        (_cuts[_selected], _cuts[to]) = (_cuts[to], _cuts[_selected]);
        _selected = to;
        Refresh();
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _cuts.Count)
            return;
        StopPreview();
        _selected = index;
        Show(_cuts[index].Source, _cuts[index].Start);
        Refresh();
    }

    /// <summary>A click seeks there and selects the piece under it, if any.</summary>
    private void ClickTimeline(double x)
    {
        if (_length <= TimeSpan.Zero || _timeline.ActualWidth <= 0)
            return;
        StopPreview();
        var at = TimeSpan.FromSeconds(_length.TotalSeconds * Math.Clamp(x / _timeline.ActualWidth, 0, 1));
        var index = VideoCuts.IndexAt(_cuts, _current, at);
        if (index >= 0)
            _selected = index;
        Seek(at.TotalSeconds);
        Refresh();
    }

    private (List<Cut>, int) Snapshot() => ([.. _cuts], _selected);

    private void Remember()
    {
        StopPreview();
        _undo.Push(Snapshot());
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(100).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep)
                _undo.Push(s);
        }
    }

    private void Undo()
    {
        if (!_undo.TryPop(out var last))
            return;
        StopPreview();
        _cuts.Clear();
        _cuts.AddRange(last.Cuts);
        _selected = last.Selected;
        if (SelectedHere || !HasSelection)
            Refresh();
        else
            Select(_selected);
    }

    private void Say(string message)
    {
        _message = message;
        Update();
    }

    private List<TimeSpan> Lengths => [.. _sources.Select(s => s.Length)];

    /// <summary>After the pieces or files change: the lists, the timeline and the totals.</summary>
    private void Refresh()
    {
        _message = null;
        _listing = true;
        var many = _sources.Count > 1;
        _fileBox.Items.Clear();
        for (var i = 0; i < _sources.Count; i++)
            _fileBox.Items.Add($"{i + 1}. {Path.GetFileName(_sources[i].File)}  ({Format(_sources[i].Length)})");
        _fileBox.SelectedIndex = _current;

        _list.Items.Clear();
        for (var i = 0; i < _cuts.Count; i++)
        {
            var c = _cuts[i];
            var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            row.Children.Add(new TextBlock
            {
                Text = $"{i + 1}.  {Format(c.Start)} – {Format(c.End)}   ({Format(c.Length)})",
                Foreground = c.Length < VideoCuts.Shortest ? Palette.Muted : Palette.Text,
            });
            if (many)
                row.Children.Add(new TextBlock
                {
                    Text = $"{c.Source + 1}. {Path.GetFileName(_sources[c.Source].File)}",
                    Foreground = Palette.Muted,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            _list.Items.Add(row);
        }
        _list.SelectedIndex = HasSelection ? _selected : -1;
        if (HasSelection)
            _list.ScrollIntoView(_list.Items[_selected]);
        _listing = false;

        var kept = VideoCuts.Clean(_cuts, Lengths);
        var files = kept.Select(c => c.Source).Distinct().Count();
        _total.Text = kept.Count switch
        {
            0 => T("Tutulan parça yok. N ile yeni parça ekle.", "No pieces kept. Press N to add one."),
            1 => T($"Sonuç: {Format(VideoCuts.Total(kept))}", $"Result: {Format(VideoCuts.Total(kept))}"),
            var n when files > 1 => T($"Sonuç: {files} dosyadan {n} parça birleşir, {Format(VideoCuts.Total(kept))}", $"Result: {n} pieces from {files} files joined, {Format(VideoCuts.Total(kept))}"),
            var n => T($"Sonuç: {n} parça birleşir, {Format(VideoCuts.Total(kept))}", $"Result: {n} pieces joined, {Format(VideoCuts.Total(kept))}"),
        };
        DrawTimeline();
        Update();
    }

    /// <summary>The shown file's pieces on the strip, numbered by their place in the result, the selected one fully lit.</summary>
    private void DrawTimeline()
    {
        _timeline.Children.Clear();
        var w = _timeline.ActualWidth;
        if (_length > TimeSpan.Zero && w > 0)
        {
            for (var i = 0; i < _cuts.Count; i++)
            {
                var c = _cuts[i];
                if (c.Source != _current)
                    continue;
                var left = w * c.Start.TotalSeconds / _length.TotalSeconds;
                var width = Math.Max(2, w * c.Length.TotalSeconds / _length.TotalSeconds);
                var piece = new Border
                {
                    Width = width,
                    Height = _timeline.Height,
                    Background = Palette.Accent,
                    Opacity = i == _selected ? 1 : 0.5,
                    BorderBrush = Palette.Surface,
                    BorderThickness = new Thickness(1, 0, 1, 0),
                    IsHitTestVisible = false,
                    Child = width < 18 ? null : new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Foreground = Brushes.White,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Canvas.SetLeft(piece, left);
                _timeline.Children.Add(piece);
            }
        }
        _timeline.Children.Add(_playhead);
        PlacePlayhead();
    }

    private void PlacePlayhead()
    {
        if (_length > TimeSpan.Zero)
            Canvas.SetLeft(_playhead, _timeline.ActualWidth * Now.TotalSeconds / _length.TotalSeconds - 1);
    }

    private void Update()
    {
        PlacePlayhead();
        if (_message is not null)
        {
            _info.Text = _message;
            return;
        }
        if (_current < 0)
        {
            _info.Text = T("Bir video ekle ya da pencereye sürükle.", "Add a video or drop one on the window.");
            return;
        }
        var hint = T("Space oynat · ← → sar (Shift ×5, , . kare) · S böl · N yeni · Del sil · Ctrl+Z geri al",
            "Space play · ← → seek (Shift ×5, , . frame) · S split · N new · Del delete · Ctrl+Z undo");
        var piece = SelectedHere ? T($"   ·   parça {_selected + 1}: {Format(_cuts[_selected].Start)} – {Format(_cuts[_selected].End)}", $"   ·   piece {_selected + 1}: {Format(_cuts[_selected].Start)} – {Format(_cuts[_selected].End)}") : "";
        _info.Text = $"{Format(Now)} / {Format(_length)}{piece}   ·   {hint}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

    /// <summary>
    /// Exact re-encodes so every cut lands on the marked frame; quick copies the streams, which is
    /// instant but starts each piece at the keyframe before its mark. More than one piece is
    /// joined, in the list's order, into one clip, next to the first file used.
    /// </summary>
    private async Task SaveAsync(bool exact)
    {
        if (_saving || _adding)
            return; // a second Enter while ffmpeg is being checked would start the job twice
        var cuts = VideoCuts.Clean(_cuts, Lengths);
        if (cuts.Count == 0)
        {
            Say(T("Kaydedilecek parça yok: önce başlangıç (I) ve bitiş (O) işaretle.", "Nothing to save: mark a start (I) and end (O) first."));
            return;
        }
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, 1);
        _saving = true;
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.Ffmpeg))
        {
            _saving = false;
            return;
        }

        _player.Pause();
        _playing = false;
        StopPreview();
        var (used, renumbered) = VideoCuts.Inputs(cuts);
        var files = used.Select(i => Path.GetFullPath(_sources[i].File)).ToList();
        var length = VideoCuts.Total(cuts);
        var suffix = cuts.Count == 1 ? T(" (kırpılmış)", " (trimmed)") : T(" (birleştirilmiş)", " (joined)");
        var verb = cuts.Count == 1 ? T("Kırpılıyor", "Trimming") : T("Birleştiriliyor", "Joining");
        Close();
        await FileJobs.RunAsync("trim", verb, [files[0]], anchor, async (_, progress, cancel) =>
            files.Count == 1
                ? await OneFileAsync(files[0], renumbered, exact, suffix, length, progress, cancel)
                : await ManyFilesAsync(files, renumbered, exact, suffix, length, progress, cancel));
    }

    private static string[] Encode(bool video, bool audio) =>
        [.. video ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"] : (string[])[],
         .. audio ? ["-c:a", "aac", "-b:a", "160k"] : (string[])[], "-movflags", "+faststart"];

    private static string[] Maps(bool video, bool audio) =>
        [.. video ? ["-map", "[v]"] : (string[])[], .. audio ? ["-map", "[a]"] : (string[])[]];

    private static readonly string[] Copy = ["-c", "copy", "-avoid_negative_ts", "make_zero"];

    private static async Task<string> OneFileAsync(string input, List<Cut> cuts, bool exact, string suffix, TimeSpan length, Action<double> progress, CancellationToken cancel)
    {
        if (!exact)
        {
            var copied = FileJobs.OutputFor(input, suffix, MediaKinds.Extension(input));
            if (cuts.Count == 1)
                await Ffmpeg.RunAsync(input, ["-t", VideoCuts.Seconds(length), .. Copy], copied, progress, cancel,
                    before: ["-ss", VideoCuts.Seconds(cuts[0].Start)], length: length);
            else
                await ConcatAsync([input], cuts, copied, length, progress, cancel);
            return copied;
        }

        var info = await Ffmpeg.ProbeAsync(input, cancel);
        var output = FileJobs.OutputFor(input, suffix, info.Video ? "mp4" : "m4a");
        if (cuts.Count == 1)
        {
            // One piece: seeking before -i skips decoding everything up to it.
            await Ffmpeg.RunAsync(input, ["-t", VideoCuts.Seconds(length), .. info.Video ? (string[])[] : ["-vn"], .. Encode(info.Video, info.Audio)], output, progress, cancel,
                before: ["-ss", VideoCuts.Seconds(cuts[0].Start)], length: length);
            return output;
        }
        await Ffmpeg.RunAsync(input, ["-filter_complex", VideoCuts.Filter(cuts, info.Video, info.Audio), .. Maps(info.Video, info.Audio), .. Encode(info.Video, info.Audio)],
            output, progress, cancel, length: length);
        return output;
    }

    /// <summary>
    /// Pieces of several files. Files made the same way are copied together when quick is asked
    /// for; otherwise every piece is brought to one size, frame rate and sound format and encoded.
    /// </summary>
    private static async Task<string> ManyFilesAsync(List<string> files, List<Cut> cuts, bool exact, string suffix, TimeSpan length, Action<double> progress, CancellationToken cancel)
    {
        var infos = new List<MediaInfo>();
        foreach (var file in files)
            infos.Add(await Ffmpeg.ProbeAsync(file, cancel));

        if (!exact && VideoCuts.CanCopy(files, infos))
        {
            var copied = FileJobs.OutputFor(files[0], suffix, MediaKinds.Extension(files[0]));
            await ConcatAsync(files, cuts, copied, length, progress, cancel);
            return copied;
        }

        var format = JoinFormat.For(cuts, infos);
        var output = FileJobs.OutputFor(files[0], suffix, format.Video ? "mp4" : "m4a");
        await Ffmpeg.RunAsync(files[0], ["-filter_complex", VideoCuts.JoinFilter(cuts, infos, format), .. Maps(format.Video, format.Audio), .. Encode(format.Video, format.Audio)],
            output, progress, cancel, length: length, moreInputs: files.Skip(1));
        return output;
    }

    /// <summary>The concat demuxer reads the pieces straight out of the files, so nothing is re-encoded.</summary>
    private static async Task ConcatAsync(IReadOnlyList<string> files, List<Cut> cuts, string output, TimeSpan length, Action<double> progress, CancellationToken cancel)
    {
        var list = Path.Combine(Path.GetTempPath(), $"rs-cuts-{Guid.NewGuid():N}.ffconcat");
        await File.WriteAllTextAsync(list, VideoCuts.ConcatList(files, cuts), new System.Text.UTF8Encoding(false), cancel);
        try
        {
            await Ffmpeg.RunAsync(list, Copy, output, progress, cancel, before: ["-f", "concat", "-safe", "0"], length: length);
        }
        finally
        {
            ToolRunner.TryDelete(list);
        }
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, Foreground = Palette.Text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    private static Button Button(string label, Action onClick, bool primary = false, string? tip = null, bool first = false)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(first ? 0 : 6, 0, 0, 4),
            Padding = new Thickness(12, 5, 12, 5),
            Foreground = primary ? Brushes.White : Palette.Text,
            Background = primary ? Palette.Accent : Palette.Raised,
            BorderBrush = Palette.Line,
            ToolTip = tip,
            Focusable = false, // Space and Enter belong to the player, not to the last clicked button.
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
