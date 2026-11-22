using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Video kırp: an editor for the result. The strip shows the pieces end to end in the order they
/// are joined, each as wide as it is long, with its frames and sound; Space plays the result
/// straight through. Click a piece to select it, drag it to move it, drag its edge to trim it;
/// S splits at the playhead, Delete drops the selected piece, I and O bring the start and end of
/// the piece under the playhead to it, Ctrl+Z undoes, Enter saves. The files are on the right:
/// click one or drag it onto the strip to use it (again). C crops the picture of the shown file.
/// The sound can be left out, and the result saved as a GIF.
/// </summary>
internal sealed class TrimWindow : Window
{
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1 / 30.0);
    private const string SourceFormat = "RadialShortcut.TrimSource"; // a file row dragged onto the strip

    private sealed record Source(string File, TimeSpan Length, CropArea? Crop = null);

    /// <summary>A file's own player, opened once and kept, so going from one file to the next is only showing another.</summary>
    private sealed class Media(MediaElement player)
    {
        public readonly MediaElement Player = player;
        public readonly TrimStrip Strip = new();
        public bool Opened, Failed;
        public TimeSpan? Pending; // where to go once it has opened
        public Border? Row;
        public TextBlock? Detail;
        public Image? Thumb;
    }

    /// <summary>A drawing the strip redraws in one go, instead of a crowd of elements.</summary>
    private sealed class Layer : FrameworkElement
    {
        public readonly DrawingVisual Visual = new();

        public Layer()
        {
            AddVisualChild(Visual);
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => Visual;
    }

    private enum Press { None, Scrub, Piece, Reorder, Edge }

    private readonly Grid _stage = new() { Background = Brushes.Black };
    private readonly Canvas _cropLayer = new() { ClipToBounds = true };
    private readonly Button _cropButton;
    private readonly CheckBox _muteBox = new()
    {
        Content = T("Sesi kaldır", "Remove sound"),
        Foreground = Palette.Text,
        Focusable = false, // Space belongs to the player
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 4),
        ToolTip = T("Kaydedilen videoda ses olmaz; hızlı kaydette de çalışır.", "The saved video has no sound; works with quick save too."),
    };
    private bool _cropping; // the picture takes the mouse to draw, move and size the crop
    private CropEdges? _cropDrag; // what the mouse holds; None while a new area is being drawn
    private Point _cropAnchor; // where the drag started, as a share of the picture
    private CropArea? _cropStart; // the area as it was when the drag started
    private Snap? _cropBefore;

    // The strip: a time ruler on top, then the pieces' frames and under them their sound.
    private const double RulerHeight = 18, ThumbHeight = 44, WaveHeight = 26, BlockHeight = ThumbHeight + WaveHeight, StripHeight = RulerHeight + BlockHeight;
    private readonly Canvas _timeline = new() { Height = StripHeight, Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true, AllowDrop = true };
    private readonly Layer _content = new(); // frames, sound and ruler: redrawn when the view or the pieces change
    private readonly Layer _overlay = new(); // outlines, selection and drop line: cheap, redrawn while dragging
    private readonly Rectangle _playhead = new() { Width = 2, Height = StripHeight, IsHitTestVisible = false };
    private readonly Canvas _overview = new() { Height = 6, Margin = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand, Visibility = Visibility.Hidden };
    private readonly Rectangle _overviewWindow = new() { Height = 6, IsHitTestVisible = false };
    private bool _contentDirty = true, _overlayDirty = true;
    private TimelineView _view; // the stretch of the result the strip shows
    private bool _viewAll = true; // showing all of it, which stays so as the result grows or shrinks
    private TimeSpan[] _starts = []; // where each piece is drawn, in result time
    private bool _overviewDrag;

    // The mouse on the strip.
    private Press _press;
    private double _pressX, _dragX;
    private int _pressPiece = -1, _dropSlot = -1;
    private bool _resume; // playing when the press came, and plays on after a scrub
    private CutEdge _edge;
    private Cut _edgeCut; // the piece as it was when its edge was picked up
    private TimeSpan _edgeDown;
    private TimeSpan[] _edgeStarts = [];
    private Snap? _before;
    private (int Index, Point At)? _rowPress;

    private readonly StackPanel _files = new();
    private readonly TextBlock _total = new() { Foreground = Palette.Muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _info = new() { Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _playButton;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<Source> _sources = [];
    private readonly List<Media> _media = [];
    private readonly List<Cut> _cuts = [];
    private readonly Stack<Snap> _undo = new();
    private readonly CancellationTokenSource _stripCancel = new();

    private sealed record Snap(List<Cut> Cuts, int Selected, CropArea?[] Crops);

    private int _current = -1; // the file in view
    private int _selected = -1;
    private TimeSpan _head; // the playhead, in result time

    // Playing the result: piece _playPiece is on, and the one after it may already be cued in its own player.
    private bool _playing;
    private int _playPiece = -1, _cued = -1;
    private bool _settled; // the player has caught up with the seek to the piece's start
    private long _playSince;

    // While the mouse drags, the frame it is on is shown at most this often: a seek takes a while,
    // and one sent before the last has landed only queues up behind it.
    private const long SeekEvery = 66;
    private (int Source, TimeSpan At)? _scrubTo;
    private long _lastSeek;

    private bool _saving, _closeArmed, _closed, _adding;
    private string? _message;

    // Taken once: Palette makes a new brush on every read.
    private readonly Brush _text = Palette.Text, _muted = Palette.Muted, _accent = Palette.Accent, _surface = Palette.Surface, _line = Palette.Line, _raised = Palette.Raised;
    private readonly Pen _accentPen, _seamPen, _tickPen, _wavePen;
    private static readonly Brush Block = Freeze(new SolidColorBrush(Color.FromArgb(255, 0x1c, 0x1f, 0x24)));
    private static readonly Brush Dimmed = Freeze(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
    private static readonly Brush Badge = Freeze(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)));
    private static readonly Brush Ghost = Freeze(new SolidColorBrush(Color.FromArgb(90, 0x00, 0x67, 0xc0)));
    private static readonly Brush WaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7c, 0xb3, 0xff)));
    private static readonly Typeface Face = new("Segoe UI");

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
        Height = 720;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = _surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        AllowDrop = true;
        _accentPen = Freeze(new Pen(_accent, 2));
        _seamPen = Freeze(new Pen(_surface, 2));
        _tickPen = Freeze(new Pen(_muted, 1));
        _wavePen = Freeze(new Pen(WaveBrush, 1));
        _timeline.Background = _raised;
        _overview.Background = _line;
        _overviewWindow.Fill = _accent;
        _playhead.Fill = _text;

        // The files on the right: click one to put it on the strip, or drag it to where it goes.
        var addFile = Button(T("+ Dosya ekle", "+ Add file"), () => _ = PickFilesAsync(), tip: T("Başka videolar ekle. Pencereye sürükleyip bırakmak da olur.", "Add more videos. Dropping them on the window works too."), first: true);
        addFile.HorizontalAlignment = HorizontalAlignment.Stretch;
        addFile.Margin = new Thickness(0, 0, 0, 10);
        var side = new DockPanel { Width = 250, Margin = new Thickness(12, 0, 0, 0) };
        var top = new StackPanel();
        top.Children.Add(Heading(T("Medya", "Media")));
        top.Children.Add(addFile);
        top.Children.Add(new TextBlock
        {
            Text = T("Tıkla: şeride ekle · sürükle: istediğin yere bırak", "Click: add to the strip · drag: drop it where it goes"),
            Foreground = _muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_total, Dock.Bottom);
        side.Children.Add(top);
        side.Children.Add(_total);
        side.Children.Add(new ScrollViewer { Content = _files, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        // Editing on the left, saving on the right.
        var edit = new WrapPanel { Orientation = Orientation.Horizontal };
        edit.Children.Add(_playButton = Button(T("▶ Oynat (Space)", "▶ Play (Space)"), PlayPause, tip: T("Sonucu baştan sona, parçalar arka arkaya oynat", "Play the result, the pieces one after another"), first: true));
        edit.Children.Add(Button(T("Böl (S)", "Split (S)"), Split, tip: T("Oynatma noktasındaki parçayı ikiye böl", "Split the piece at the playhead")));
        edit.Children.Add(Button(T("Sil (Del)", "Delete (Del)"), Delete, tip: T("Seçili parçayı sil", "Delete the selected piece")));
        edit.Children.Add(Button(T("Geri al", "Undo"), Undo, tip: "Ctrl+Z"));
        edit.Children.Add(_cropButton = Button(T("Görüntüyü kırp (C)", "Crop picture (C)"), ToggleCrop, tip: T("Görüntüde yalnızca bir pencereyi ya da bölgeyi bırak: üzerine sürükleyerek alan seç. Gösterilen dosyanın tüm parçalarına uygulanır.", "Keep only a window or region of the picture: drag over it to pick the area. Applies to every piece of the shown file.")));
        var save = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        save.Children.Add(_muteBox);
        save.Children.Add(Button(T("GIF olarak kaydet", "Save as GIF"), () => _ = SaveAsync(exact: true, gif: true), tip: T($"Paylaşmak için hareketli GIF: saniyede {VideoCuts.GifFps} kare, en fazla {VideoCuts.GifWidth} piksel genişlik, sessiz. Kısa kayıtlarda iyi sonuç verir; uzun olanlar büyük dosya olur.", $"An animated GIF to share: {VideoCuts.GifFps} frames a second, at most {VideoCuts.GifWidth} pixels wide, no sound. Best for short clips; long ones make big files.")));
        save.Children.Add(Button(T("Hızlı kaydet", "Quick save"), () => _ = SaveAsync(exact: false), tip: T("Anında, ama kesimler önceki anahtar kareye kayar. Kırpılmış görüntü ve biçimleri farklı dosyalar yine de tam kaydedilir.", "Instant, but cuts snap to the keyframe before. A cropped picture and files of different formats are still saved exactly.")));
        save.Children.Add(Button(T("Kaydet (Enter)", "Save (Enter)"), () => _ = SaveAsync(exact: true), primary: true, tip: T("Kesimler tam karesinde olur; farklı boyuttaki videolar ilk videonun boyutuna getirilir", "Cuts land on the exact frame; videos of other sizes are fitted to the first one's")));

        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(save, Dock.Right);
        actions.Children.Add(save);
        actions.Children.Add(edit);

        var info = new Border { Margin = new Thickness(0, 8, 0, 0), Child = _info };

        _timeline.Children.Add(_content);
        _timeline.Children.Add(_overlay);
        _timeline.Children.Add(_playhead);
        _overview.Children.Add(_overviewWindow);

        var main = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        DockPanel.SetDock(info, Dock.Bottom);
        DockPanel.SetDock(_overview, Dock.Bottom);
        DockPanel.SetDock(_timeline, Dock.Bottom);
        main.Children.Add(actions);
        main.Children.Add(info);
        main.Children.Add(_overview);
        main.Children.Add(_timeline);
        _stage.Children.Add(_cropLayer);
        main.Children.Add(_stage);
        _cropLayer.MouseLeftButtonDown += (_, e) => CropDown(e);
        _cropLayer.MouseMove += (_, e) => CropMove(e);
        _cropLayer.MouseLeftButtonUp += (_, _) => CropUp();
        _cropLayer.LostMouseCapture += (_, _) => CropUp();
        _cropLayer.SizeChanged += (_, _) => DrawCrop();

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(main);
        Content = root;

        _timeline.MouseLeftButtonDown += (_, e) => StripDown(e.GetPosition(_timeline));
        _timeline.MouseMove += (_, e) => StripMove(e.GetPosition(_timeline));
        _timeline.MouseLeftButtonUp += (_, _) => StripUp();
        _timeline.LostMouseCapture += (_, _) => StripUp();
        _timeline.MouseLeave += (_, _) =>
        {
            if (_press == Press.None)
                _timeline.Cursor = null;
        };
        _timeline.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (_press != Press.None)
                return; // the drag maps the mouse through the view it started in; a new one would make it jump
            var x = e.GetPosition(_timeline).X;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                SetView(_view.Panned(_view.Length * (e.Delta > 0 ? -0.15 : 0.15), Total));
            else
                SetView(_view.Zoomed(e.Delta > 0 ? 0.8 : 1.25, _view.ToTime(x, _timeline.ActualWidth), Total));
            e.Handled = true;
        };
        _timeline.DragOver += (_, e) =>
        {
            var ours = e.Data.GetDataPresent(SourceFormat) || e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ours ? DragDropEffects.Copy : DragDropEffects.None;
            var slot = ours ? Sequence.DropSlot(_cuts, StripTime(e.GetPosition(_timeline).X)) : -1;
            if (slot != _dropSlot)
            {
                _dropSlot = slot;
                _overlayDirty = true;
            }
            e.Handled = true;
        };
        _timeline.DragLeave += (_, _) =>
        {
            _dropSlot = -1;
            _overlayDirty = true;
        };
        _timeline.Drop += (_, e) =>
        {
            var slot = Sequence.DropSlot(_cuts, StripTime(e.GetPosition(_timeline).X));
            _dropSlot = -1;
            _overlayDirty = true;
            if (e.Data.GetData(SourceFormat) is int source)
                AddPiece(source, slot);
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                _ = AddFilesAsync(dropped, slot);
            e.Handled = true;
        };
        _overview.MouseLeftButtonDown += (_, e) =>
        {
            _overviewDrag = _overview.CaptureMouse();
            PanOverviewTo(e.GetPosition(_overview).X);
        };
        _overview.MouseMove += (_, e) =>
        {
            if (_overviewDrag)
                PanOverviewTo(e.GetPosition(_overview).X);
        };
        _overview.MouseLeftButtonUp += (_, _) => _overview.ReleaseMouseCapture();
        _overview.LostMouseCapture += (_, _) => _overviewDrag = false;
        _overview.SizeChanged += (_, _) => DrawOverview();
        _timeline.SizeChanged += (_, _) =>
        {
            _contentDirty = _overlayDirty = true;
            PlacePlayhead();
        };
        CompositionTarget.Rendering += OnFrame;
        _tick.Tick += (_, _) =>
        {
            if (_playing)
                Update();
        };
        _tick.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _stripCancel.Cancel();
            _tick.Stop();
            CompositionTarget.Rendering -= OnFrame;
            foreach (var m in _media)
                m.Player.Close();
        };
        PreviewKeyDown += OnKey;
        // By the character, not the key: on a Turkish Q keyboard . and , aren't on the OEM keys a US layout uses.
        PreviewTextInput += (_, e) =>
        {
            if (e.Text is "+" or "-")
                SetView(_view.Zoomed(e.Text == "+" ? 0.5 : 2, _head, Total)); // around the playhead
            else if (e.Text is "," or ".")
                Jump(e.Text == "," ? -Frame.TotalSeconds : Frame.TotalSeconds);
            else
                return;
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
                _ = AddFilesAsync(dropped, null);
        };
        Loaded += (_, _) => _ = AddFilesAsync(files, null);
        Refresh();
    }

    private async Task PickFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = T("Eklenecek videoları seç", "Choose videos to add"), Filter = SelectedFiles.Videos, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await AddFilesAsync(dialog.FileNames, null);
    }

    /// <summary>
    /// Adds the files to the media list and each, whole, as a piece at drop slot <paramref name="slot"/>,
    /// or after the selected piece. A file already in the list is used again.
    /// </summary>
    private async Task AddFilesAsync(IEnumerable<string> files, int? slot)
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
                    OpenMedia(source);
                }
                added.Add(new Cut(TimeSpan.Zero, _sources[source].Length, source));
            }

            _message = null;
            if (added.Count > 0)
            {
                if (_cuts.Count > 0)
                    Remember();
                var at = Math.Clamp(slot ?? (HasSelection ? _selected + 1 : _cuts.Count), 0, _cuts.Count);
                _cuts.InsertRange(at, added);
                _selected = at;
                Refresh();
                SetHead(_starts[at]);
            }
            else
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

    /// <summary>All of file <paramref name="source"/> as a piece at <paramref name="slot"/>, or after the selected piece.</summary>
    private void AddPiece(int source, int? slot)
    {
        if (source < 0 || source >= _sources.Count)
            return;
        Remember();
        var at = Math.Clamp(slot ?? (HasSelection ? _selected + 1 : _cuts.Count), 0, _cuts.Count);
        _cuts.Insert(at, new Cut(TimeSpan.Zero, _sources[source].Length, source));
        _selected = at;
        Refresh();
        SetHead(_starts[at]);
    }

    /// <summary>
    /// Gives the file its own player, opened now and kept paused out of sight, its row in the
    /// media list, and starts making its strip.
    /// </summary>
    private void OpenMedia(int source)
    {
        var file = _sources[source].File;
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
            IsMuted = true, // until it is paused again: opening plays it for a moment
        };
        var m = new Media(player);
        _media.Add(m);
        _stage.Children.Insert(_stage.Children.Count - 1, player); // under the crop layer
        player.SizeChanged += (_, _) =>
        {
            if (source == _current)
                DrawCrop();
        };
        player.MediaOpened += (_, _) =>
        {
            m.Opened = true;
            if (player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan > TimeSpan.Zero)
                _sources[source] = _sources[source] with { Length = player.NaturalDuration.TimeSpan };
            if (m.Pending is { } at)
            {
                m.Pending = null;
                player.Position = at;
            }
            if (_playing && source == _current)
                player.Play();
            else
                player.Pause();
            player.IsMuted = false;
            if (source == _current)
                DrawCrop();
        };
        player.MediaFailed += (_, e) =>
        {
            m.Opened = m.Failed = true;
            m.Pending = null;
            Say(T($"Oynatılamadı: {Path.GetFileName(file)}: {e.ErrorException?.Message}. Parçaları yine de kaydedebilirsin.", $"Can't play {Path.GetFileName(file)}: {e.ErrorException?.Message}. You can still save its pieces."));
        };
        player.MediaEnded += (_, _) =>
        {
            // A piece that runs to the end of the file never reaches its End by position; go on to the next one.
            // Only for such a piece, and once: the render loop may have gone on already, and a second
            // step would skip the next piece when it is in this same file.
            if (_playing && _settled && _playPiece >= 0 && _playPiece < _cuts.Count && _cuts[_playPiece] is var c
                && c.Source == source && c.End >= _sources[source].Length - TimeSpan.FromSeconds(0.5))
                Advance();
        };
        player.Source = new Uri(file);
        player.Play(); // opens it; MediaOpened pauses again

        AddRow(source, m);
        m.Strip.Changed += () =>
        {
            if (m.Thumb is { Source: null } && m.Strip.Frames.Count > 0)
                m.Thumb.Source = m.Strip.Frames[0];
            if (_cuts.Exists(c => c.Source == source))
                _contentDirty = true; // drawn on the next frame, however many arrive before it
        };
        _ = m.Strip.FillAsync(file, _sources[source].Length, (int)ThumbHeight * 2, Dispatcher, _stripCancel.Token);
    }

    private void AddRow(int source, Media m)
    {
        m.Thumb = new Image { Stretch = Stretch.UniformToFill };
        var thumbBox = new Border { Background = Block, Child = m.Thumb, Width = 64, Height = 36, Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true };
        m.Detail = new TextBlock { Foreground = _muted, FontSize = 11 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(_sources[source].File), Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(m.Detail);
        var content = new DockPanel();
        DockPanel.SetDock(thumbBox, Dock.Left);
        content.Children.Add(thumbBox);
        content.Children.Add(text);
        var row = m.Row = new Border
        {
            Child = content,
            Background = _raised,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = _sources[source].File,
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            _rowPress = (source, e.GetPosition(this));
            e.Handled = true;
        };
        row.MouseMove += (_, e) =>
        {
            if (_rowPress is not { } p || p.Index != source || e.LeftButton != MouseButtonState.Pressed)
                return;
            var d = e.GetPosition(this) - p.At;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _rowPress = null;
            DragDrop.DoDragDrop(row, new DataObject(SourceFormat, source), DragDropEffects.Copy);
            _dropSlot = -1;
            _overlayDirty = true;
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_rowPress is { } p && p.Index == source)
                AddPiece(source, null);
            _rowPress = null;
        };
        _files.Children.Add(row);
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

    private MediaElement? Player => _current >= 0 ? _media[_current].Player : null;

    /// <summary>Brings file <paramref name="source"/>'s player into view and puts the one that was there away.</summary>
    private void ShowSource(int source)
    {
        if (source == _current || source < 0 || source >= _media.Count)
            return;
        if (Player is { } old)
        {
            old.Pause();
            old.Visibility = Visibility.Hidden;
        }
        _current = source;
        _media[source].Player.Visibility = Visibility.Visible;
        var name = Path.GetFileName(_sources[source].File);
        Title = _sources.Count > 1 ? $"{T("Video kırp", "Trim video")} · {name} ({source + 1}/{_sources.Count})" : $"{T("Video kırp", "Trim video")} · {name}";
        UpdateRows();
        DrawCrop();
    }

    /// <summary>Moves file <paramref name="source"/>'s player to <paramref name="at"/>, or once it has opened.</summary>
    private void SeekSource(int source, TimeSpan at)
    {
        var m = _media[source];
        var length = _sources[source].Length;
        at = at < TimeSpan.Zero ? TimeSpan.Zero : at > length ? length : at;
        if (m.Opened)
            m.Player.Position = at;
        else
            m.Pending = at;
    }

    /// <summary>
    /// Puts the playhead at result time <paramref name="t"/> and the frame there in view. While the
    /// mouse drags (<paramref name="scrub"/>) the seek waits for the render loop, which sends at
    /// most one every <see cref="SeekEvery"/> ms.
    /// </summary>
    private void SetHead(TimeSpan t, bool scrub = false)
    {
        var total = Total;
        _head = t < TimeSpan.Zero ? TimeSpan.Zero : t > total ? total : t;
        var (index, at) = Sequence.Locate(_cuts, _head);
        if (index >= 0)
        {
            var source = _cuts[index].Source;
            ShowSource(source);
            if (_playing && !scrub)
                StartPiece(index, at);
            else if (scrub)
                _scrubTo = (source, at);
            else
            {
                _scrubTo = null;
                SeekSource(source, at);
            }
        }
        if (!scrub && _view.Showing(_head, total) is var shown && shown != _view)
            SetView(shown);
        PlacePlayhead();
        Update();
    }

    private void PlayPause()
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    private void Play()
    {
        if (_cuts.Count == 0 || _cropping)
            return;
        if (_head >= Total - Frame)
            _head = TimeSpan.Zero; // at the end: from the top
        var (index, at) = Sequence.Locate(_cuts, _head);
        _playing = true;
        _playButton.Content = T("❚❚ Duraklat (Space)", "❚❚ Pause (Space)");
        StartPiece(index, at);
        Update();
    }

    private void Pause()
    {
        if (!_playing)
            return;
        _playing = false;
        _playPiece = _cued = -1;
        Player?.Pause();
        _playButton.Content = T("▶ Oynat (Space)", "▶ Play (Space)");
        Update();
    }

    /// <summary>Plays piece <paramref name="index"/> from <paramref name="at"/> in its file, and cues the next one if it is in another file.</summary>
    private void StartPiece(int index, TimeSpan at, bool seek = true)
    {
        _playPiece = index;
        var cut = _cuts[index];
        ShowSource(cut.Source);
        if (seek)
            SeekSource(cut.Source, at);
        _media[cut.Source].Player.Play();
        _settled = !seek;
        _playSince = Environment.TickCount64;
        _cued = -1;
        if (index + 1 < _cuts.Count && _cuts[index + 1].Source != cut.Source)
        {
            // Its player is paused out of sight: going there is then only showing it and pressing play.
            SeekSource(_cuts[index + 1].Source, _cuts[index + 1].Start);
            _cued = index + 1;
        }
    }

    /// <summary>On from the end of the playing piece to the start of the next, or the end of the result.</summary>
    private void Advance()
    {
        if (_playPiece < 0)
            return;
        var next = _playPiece + 1;
        if (next >= _cuts.Count)
        {
            _head = Total;
            Pause();
            PlacePlayhead();
            return;
        }
        var cut = _cuts[_playPiece];
        var n = _cuts[next];
        // A piece that goes on where the last stopped, as after a split, plays on without a seek.
        var seamless = n.Source == cut.Source && Math.Abs((n.Start - cut.End).TotalMilliseconds) < 60;
        StartPiece(next, n.Start, seek: !seamless && _cued != next);
        if (!seamless && _cued == next)
            _settled = false;
    }

    /// <summary>
    /// Once per rendered frame: the coalesced scrub seek, the strip's layers if they changed, and
    /// while playing the playhead and the step from one piece to the next.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (_scrubTo is { } s && now - _lastSeek >= SeekEvery && _media[s.Source].Opened)
        {
            _scrubTo = null;
            _lastSeek = now;
            _media[s.Source].Player.Position = s.At;
        }
        if (_contentDirty)
            RenderContent();
        if (_overlayDirty)
            RenderOverlay();
        if (!_playing)
            return;
        if (_playPiece < 0 || _playPiece >= _cuts.Count)
        {
            Pause();
            return;
        }
        var cut = _cuts[_playPiece];
        var m = _media[cut.Source];
        if (m.Failed)
        {
            Advance(); // can't show this piece; go on rather than wait on it
            return;
        }
        if (!m.Opened)
            return;
        var at = m.Player.Position;
        if (!_settled)
        {
            // Right after a seek Position can still read the old spot for a frame or two, which is
            // past the end of a piece that comes earlier in the file; wait until it has arrived.
            if (at >= cut.Start - TimeSpan.FromSeconds(0.5) && at < cut.End || now - _playSince > 1000)
                _settled = true;
            return;
        }
        if (at >= cut.End)
        {
            Advance();
            return;
        }
        _head = Sequence.ToResult(_cuts, _playPiece, at);
        // While it plays on, the zoomed-in strip turns the page to keep the playhead in sight.
        if (_view.Showing(_head, Total) is var shown && shown != _view)
            SetView(shown);
        PlacePlayhead();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var step = mods.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt turns arrows into system keys.
        if (_press != Press.None)
        {
            // While the mouse holds a piece or an edge the pieces stay as they are; a key that changed
            // them would leave the drag holding a piece that is gone.
            e.Handled = true;
            return;
        }
        if (key != Key.Escape)
            _closeArmed = false;
        if (_cropping && key is Key.Escape or Key.Enter or Key.Delete or Key.Back)
        {
            // While cropping these act on the crop, not on the pieces or the window.
            if (key is Key.Delete or Key.Back)
                ClearCrop();
            else
                ToggleCrop();
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.C when mods == ModifierKeys.None:
                ToggleCrop();
                break;
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
            case Key.Delete or Key.Back:
                Delete();
                break;
            case Key.Z when mods.HasFlag(ModifierKeys.Control):
                Undo();
                break;
            case Key.Left when mods.HasFlag(ModifierKeys.Alt):
                MoveSelected(-1);
                break;
            case Key.Right when mods.HasFlag(ModifierKeys.Alt):
                MoveSelected(+1);
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
                SetHead(TimeSpan.Zero);
                break;
            case Key.End:
                SetHead(Total);
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

    private void Jump(double seconds) => SetHead(_head + TimeSpan.FromSeconds(seconds));

    private TimeSpan Total => VideoCuts.Total(_cuts);

    private bool HasSelection => _selected >= 0 && _selected < _cuts.Count;

    /// <summary>The piece under the playhead and the moment of its file there; -1 without pieces.</summary>
    private (int Index, TimeSpan At) HeadPiece => Sequence.Locate(_cuts, _head);

    /// <summary>The start of the piece under the playhead comes to it.</summary>
    private void MarkIn()
    {
        var (i, at) = HeadPiece;
        if (i < 0)
            return;
        var moved = CutEdges.Move(_cuts[i], false, at, _sources[_cuts[i].Source].Length);
        if (moved == _cuts[i])
        {
            Say(T("Oynatma noktası zaten parçanın başında.", "The playhead is already at the start of the piece."));
            return;
        }
        Remember();
        _cuts[i] = moved;
        _selected = i;
        Refresh();
        SetHead(_starts[i]);
    }

    /// <summary>The end of the piece under the playhead comes to it.</summary>
    private void MarkOut()
    {
        var (i, at) = Sequence.LocateEnd(_cuts, _head); // on a seam: the piece that ends there
        if (i < 0)
            return;
        var moved = CutEdges.Move(_cuts[i], true, at, _sources[_cuts[i].Source].Length);
        if (moved == _cuts[i])
        {
            Say(T("Oynatma noktası zaten parçanın sonunda.", "The playhead is already at the end of the piece."));
            return;
        }
        Remember();
        _cuts[i] = moved;
        _selected = i;
        Refresh();
        SetHead(_starts[i] + _cuts[i].Length - Frame);
    }

    private void Split()
    {
        var (i, at) = HeadPiece;
        if (i < 0)
            return;
        var before = Snapshot();
        if (!VideoCuts.Split(_cuts, i, at))
        {
            Say(T("Parçanın kenarına çok yakın, bölünemez.", "Too close to the edge of the piece to split."));
            return;
        }
        Pause();
        Push(before);
        _selected = i + 1;
        Refresh();
    }

    private void Delete()
    {
        if (!HasSelection)
        {
            Say(T("Önce şeritte silinecek parçaya tıkla.", "Click the piece to delete on the strip first."));
            return;
        }
        Remember();
        _cuts.RemoveAt(_selected);
        _selected = Math.Min(_selected, _cuts.Count - 1);
        Refresh();
        SetHead(HasSelection ? _starts[_selected] : _head);
    }

    private void MoveSelected(int by)
    {
        if (!HasSelection)
            return;
        var slot = by < 0 ? _selected - 1 : _selected + 2;
        if (slot < 0 || slot > _cuts.Count)
            return;
        Remember();
        _selected = Sequence.Move(_cuts, _selected, slot);
        Refresh();
        SetHead(_starts[_selected]);
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _cuts.Count)
            return;
        _selected = index;
        _overlayDirty = true;
        SetHead(_starts[index]);
    }

    private const double EdgeGrab = 7; // pixels either side of a piece's edge that pick it up

    private TimeSpan StripTime(double x) => _view.ToTime(Math.Clamp(x, 0, _timeline.ActualWidth), _timeline.ActualWidth);

    private double StripX(TimeSpan t) => _view.ToX(t, _timeline.ActualWidth);

    /// <summary>
    /// On the ruler or past the last piece a press moves the playhead and follows the mouse; on a
    /// piece's edge it picks the edge up; on a piece it selects it, and a drag from there moves it.
    /// </summary>
    private void StripDown(Point p)
    {
        var w = _timeline.ActualWidth;
        if (_cuts.Count == 0 || w <= 0 || _cropping)
            return;
        _resume = _playing;
        Pause();
        var edge = p.Y >= RulerHeight ? Sequence.EdgeAt(_cuts, _starts, _view, w, p.X, EdgeGrab) : null;
        var piece = p.Y >= RulerHeight ? Sequence.IndexAt(_cuts, StripTime(p.X)) : -1;
        if (!_timeline.CaptureMouse())
            return;
        if (edge is { } ed)
        {
            _press = Press.Edge;
            _edge = ed;
            _edgeCut = _cuts[ed.Index];
            _edgeDown = StripTime(p.X);
            _edgeStarts = [.. _starts];
            _before = Snapshot();
            _selected = ed.Index;
            _overlayDirty = true;
            ShowEdge();
        }
        else if (piece >= 0)
        {
            _press = Press.Piece;
            _pressX = _dragX = p.X;
            _pressPiece = piece;
            _selected = piece;
            _overlayDirty = true;
            Update();
        }
        else
        {
            _press = Press.Scrub;
            SetHead(StripTime(p.X), scrub: true);
        }
    }

    private void StripMove(Point p)
    {
        switch (_press)
        {
            case Press.None:
                _timeline.Cursor = _cuts.Count == 0 || p.Y < RulerHeight ? Cursors.Arrow
                    : Sequence.EdgeAt(_cuts, _starts, _view, _timeline.ActualWidth, p.X, EdgeGrab) is not null ? Cursors.SizeWE
                    : Sequence.IndexAt(_cuts, StripTime(p.X)) >= 0 ? Cursors.Hand : Cursors.Arrow;
                break;
            case Press.Scrub:
                SetHead(StripTime(p.X), scrub: true);
                break;
            case Press.Piece when Math.Abs(p.X - _pressX) > SystemParameters.MinimumHorizontalDragDistance * 2:
                _press = Press.Reorder;
                _before = Snapshot();
                _timeline.Cursor = Cursors.SizeAll;
                goto case Press.Reorder;
            case Press.Reorder:
                _dragX = p.X;
                _dropSlot = Sequence.DropSlot(_cuts, StripTime(p.X));
                _overlayDirty = true;
                break;
            case Press.Edge:
                // The view holds still while the edge moves, so the strip doesn't rescale under the mouse.
                var by = StripTime(p.X) - _edgeDown;
                var to = (_edge.End ? _edgeCut.End : _edgeCut.Start) + by;
                _cuts[_edge.Index] = CutEdges.Move(_edgeCut, _edge.End, to, _sources[_edgeCut.Source].Length);
                Relayout();
                _contentDirty = _overlayDirty = true;
                ShowEdge();
                break;
        }
    }

    /// <summary>The playhead on the edge being dragged, and the frame there in view.</summary>
    private void ShowEdge()
    {
        var c = _cuts[_edge.Index];
        _head = _starts[_edge.Index] + (_edge.End ? c.Length : TimeSpan.Zero);
        ShowSource(c.Source);
        _scrubTo = (c.Source, _edge.End ? c.End - Frame : c.Start);
        PlacePlayhead();
        Update();
    }

    private void StripUp()
    {
        var press = _press;
        _press = Press.None; // first: releasing the capture comes back here through LostMouseCapture
        if (_timeline.IsMouseCaptured)
            _timeline.ReleaseMouseCapture();
        switch (press)
        {
            case Press.Scrub:
                SetHead(_head);
                if (_resume)
                    Play();
                break;
            case Press.Piece:
                SetHead(StripTime(_pressX)); // a click on a piece selects it and puts the playhead there
                if (_resume)
                    Play();
                break;
            case Press.Reorder:
                _timeline.Cursor = Cursors.Hand;
                var to = Sequence.Move(_cuts, _pressPiece, _dropSlot);
                _dropSlot = -1;
                if (to >= 0)
                {
                    Push(_before!);
                    _selected = to;
                    Refresh();
                    SetHead(_starts[to]);
                }
                else
                    _overlayDirty = true;
                break;
            case Press.Edge:
                var i = _edge.Index;
                if (_before is { } before && !before.Cuts.SequenceEqual(_cuts))
                    Push(before);
                Refresh(); // closes the gap a moved start leaves
                SetHead(_edge.End ? _starts[i] + _cuts[i].Length - Frame : _starts[i]);
                break;
        }
        _pressPiece = -1;
        _before = null;
    }

    /// <summary>
    /// Where each piece is drawn. While a piece's start is dragged in, it keeps its end where it was
    /// and the gap stays open, as the mouse expects; the rest close up once the mouse lets go. Drawn
    /// out, past where it started, it keeps its start instead and pushes the pieces after it along,
    /// so it never lies over the piece before.
    /// </summary>
    private void Relayout()
    {
        if (_press == Press.Edge && !_edge.End && _edge.Index < _cuts.Count && _cuts[_edge.Index].Start > _edgeCut.Start)
        {
            _starts = [.. _edgeStarts];
            _starts[_edge.Index] += _cuts[_edge.Index].Start - _edgeCut.Start;
        }
        else
            _starts = Sequence.Starts(_cuts);
    }

    private void SetView(TimelineView view)
    {
        if (view == _view)
            return;
        _view = view;
        _viewAll = view.IsAll(Total);
        _contentDirty = _overlayDirty = true;
        PlacePlayhead();
        DrawOverview();
    }

    /// <summary>After the result's length changes: all of it stays all of it, a zoomed view stays inside it.</summary>
    private void FitView()
    {
        var total = Total;
        _view = _viewAll || total <= TimeSpan.Zero ? TimelineView.All(total) : _view.Panned(TimeSpan.Zero, total);
        _viewAll = _view.IsAll(total);
        _contentDirty = _overlayDirty = true;
        DrawOverview();
    }

    private void PanOverviewTo(double x)
    {
        var total = Total;
        if (_overview.ActualWidth <= 0 || total <= TimeSpan.Zero)
            return;
        var centre = TimeSpan.FromSeconds(total.TotalSeconds * Math.Clamp(x / _overview.ActualWidth, 0, 1));
        SetView(new TimelineView(centre - _view.Length / 2, _view.Length).Panned(TimeSpan.Zero, total));
    }

    private void DrawOverview()
    {
        var total = Total;
        var zoomed = total > TimeSpan.Zero && !_view.IsAll(total);
        _overview.Visibility = zoomed ? Visibility.Visible : Visibility.Hidden;
        if (!zoomed || _overview.ActualWidth <= 0)
            return;
        var w = _overview.ActualWidth;
        _overviewWindow.Width = Math.Max(4, w * _view.Length.TotalSeconds / total.TotalSeconds);
        Canvas.SetLeft(_overviewWindow, w * _view.Start.TotalSeconds / total.TotalSeconds);
    }

    private Snap Snapshot() => new([.. _cuts], _selected, [.. _sources.Select(s => s.Crop)]);

    private void Remember()
    {
        DropPress();
        Pause();
        Push(Snapshot());
    }

    /// <summary>Lets go of whatever the mouse holds on the strip, for a change that comes in while it does (a file that finished opening).</summary>
    private void DropPress()
    {
        if (_press == Press.None)
            return;
        _press = Press.None;
        _pressPiece = _dropSlot = -1;
        _before = null;
        if (_timeline.IsMouseCaptured)
            _timeline.ReleaseMouseCapture(); // StripUp then finds nothing to do
        _overlayDirty = true;
    }

    private void Push(Snap snap)
    {
        _undo.Push(snap);
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
        DropPress();
        Pause();
        _cuts.Clear();
        _cuts.AddRange(last.Cuts);
        _selected = last.Selected;
        for (var i = 0; i < _sources.Count; i++)
            _sources[i] = _sources[i] with { Crop = i < last.Crops.Length ? last.Crops[i] : null };
        Refresh();
        SetHead(_head);
    }

    private CropArea? CurrentCrop => _current >= 0 ? _sources[_current].Crop : null;

    private void SetCrop(CropArea? area) => _sources[_current] = _sources[_current] with { Crop = area };

    /// <summary>Where the player draws the picture, in the crop layer's coordinates; empty without a picture.</summary>
    private Rect PictureRect()
    {
        if (Player is not { } player || !player.HasVideo || player.ActualWidth <= 0 || player.ActualHeight <= 0)
            return Rect.Empty;
        return new Rect(player.TranslatePoint(new Point(0, 0), _cropLayer), new Size(player.ActualWidth, player.ActualHeight));
    }

    /// <summary>The point as a share of the picture, 0 to 1 across and down.</summary>
    private static Point ToPicture(Point p, Rect picture) => new((p.X - picture.X) / picture.Width, (p.Y - picture.Y) / picture.Height);

    private void ToggleCrop()
    {
        if (!_cropping && PictureRect().IsEmpty)
        {
            Say(T("Bu dosyada kırpılacak bir görüntü yok.", "This file has no picture to crop."));
            return;
        }
        Pause();
        _cropping = !_cropping;
        _cropButton.Content = _cropping ? T("Kırpmayı bitir (C)", "Done cropping (C)") : T("Görüntüyü kırp (C)", "Crop picture (C)");
        _cropButton.Background = _cropping ? _accent : _raised;
        _cropButton.Foreground = _cropping ? Brushes.White : _text;
        _cropLayer.Background = _cropping ? Brushes.Transparent : null; // hit-testable only while cropping
        _cropLayer.Cursor = _cropping ? Cursors.Cross : null;
        _message = null;
        DrawCrop();
        Update();
    }

    private void ClearCrop()
    {
        if (CurrentCrop is null)
            return;
        Remember();
        SetCrop(null);
        Refresh();
    }

    private void CropDown(MouseButtonEventArgs e)
    {
        var picture = PictureRect();
        if (!_cropping || picture.IsEmpty)
            return;
        var at = ToPicture(e.GetPosition(_cropLayer), picture);
        _cropBefore = Snapshot();
        _cropAnchor = at;
        _cropStart = CurrentCrop;
        _cropDrag = CurrentCrop?.HitTest(at.X, at.Y, 7 / picture.Width, 7 / picture.Height) ?? CropEdges.None;
        _cropLayer.CaptureMouse();
        e.Handled = true;
    }

    private void CropMove(MouseEventArgs e)
    {
        var picture = PictureRect();
        if (!_cropping || picture.IsEmpty)
            return;
        var at = ToPicture(e.GetPosition(_cropLayer), picture);
        if (_cropDrag is not { } held)
        {
            // Not dragging: show what a press here would grab.
            var hit = CurrentCrop?.HitTest(at.X, at.Y, 7 / picture.Width, 7 / picture.Height) ?? CropEdges.None;
            _cropLayer.Cursor = hit switch
            {
                CropEdges.Move => Cursors.SizeAll,
                CropEdges.Left or CropEdges.Right => Cursors.SizeWE,
                CropEdges.Top or CropEdges.Bottom => Cursors.SizeNS,
                CropEdges.Left | CropEdges.Top or CropEdges.Right | CropEdges.Bottom => Cursors.SizeNWSE,
                CropEdges.Right | CropEdges.Top or CropEdges.Left | CropEdges.Bottom => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
            return;
        }
        SetCrop(held switch
        {
            CropEdges.None => CropArea.Between(_cropAnchor.X, _cropAnchor.Y, at.X, at.Y),
            CropEdges.Move => _cropStart?.Moved(at.X - _cropAnchor.X, at.Y - _cropAnchor.Y),
            _ => _cropStart?.Dragged(held, at.X, at.Y),
        });
        DrawCrop();
        Update();
    }

    private void CropUp()
    {
        if (_cropDrag is null)
            return;
        _cropDrag = null;
        if (_cropLayer.IsMouseCaptured)
            _cropLayer.ReleaseMouseCapture();
        if (CurrentCrop is { } a)
            SetCrop(CropArea.Make(a.X, a.Y, a.Width, a.Height)); // a crop dragged out to the whole picture is no crop
        else if (_cropStart is not null)
            SetCrop(_cropStart); // a click outside, too small to be a new area, keeps the one there was; Del clears it
        if (_cropBefore is { } before && CurrentCrop != _cropStart)
            Push(before);
        _cropBefore = null;
        Refresh();
    }

    /// <summary>The picture outside the crop dimmed, the crop outlined, and while cropping its handles and size.</summary>
    private void DrawCrop()
    {
        _cropLayer.Children.Clear();
        var picture = PictureRect();
        if (picture.IsEmpty || CurrentCrop is not { } c)
            return;
        var area = new Rect(picture.X + c.X * picture.Width, picture.Y + c.Y * picture.Height, c.Width * picture.Width, c.Height * picture.Height);
        _cropLayer.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(area)),
            Fill = new SolidColorBrush(Color.FromArgb(_cropping ? (byte)150 : (byte)190, 0, 0, 0)),
            IsHitTestVisible = false,
        });
        var outline = new Rectangle { Width = area.Width, Height = area.Height, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        if (!_cropping)
            outline.StrokeDashArray = [4, 3];
        Canvas.SetLeft(outline, area.X);
        Canvas.SetTop(outline, area.Y);
        _cropLayer.Children.Add(outline);
        if (!_cropping)
            return;
        foreach (var (hx, hy) in new[] { (0.0, 0.0), (0.5, 0.0), (1.0, 0.0), (0.0, 0.5), (1.0, 0.5), (0.0, 1.0), (0.5, 1.0), (1.0, 1.0) })
        {
            var handle = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = _accent, IsHitTestVisible = false };
            Canvas.SetLeft(handle, area.X + hx * area.Width - 4);
            Canvas.SetTop(handle, area.Y + hy * area.Height - 4);
            _cropLayer.Children.Add(handle);
        }
        if (CropPixels() is { } p)
        {
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                Padding = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = $"{p.Width} × {p.Height}", Foreground = Brushes.White, FontSize = 12 },
            };
            Canvas.SetLeft(label, area.X + 4);
            Canvas.SetTop(label, area.Y + 4);
            _cropLayer.Children.Add(label);
        }
    }

    /// <summary>The shown file's crop in its own pixels, as far as the player knows its size.</summary>
    private PixelCrop? CropPixels() =>
        CurrentCrop is { } c && Player is { NaturalVideoWidth: > 0, NaturalVideoHeight: > 0 } player
            ? c.ToPixels(player.NaturalVideoWidth, player.NaturalVideoHeight)
            : null;

    private void Say(string message)
    {
        _message = message;
        Update();
    }

    private List<TimeSpan> Lengths => [.. _sources.Select(s => s.Length)];

    /// <summary>After the pieces or files change: the layout, the strip, the media rows and the totals.</summary>
    private void Refresh()
    {
        _message = null;
        if (_selected >= _cuts.Count)
            _selected = _cuts.Count - 1;
        Relayout();
        FitView();
        if (_head > Total)
            _head = Total;
        UpdateRows();

        var kept = VideoCuts.Clean(_cuts, Lengths);
        var files = kept.Select(c => c.Source).Distinct().Count();
        _total.Text = kept.Count switch
        {
            0 => T("Şeritte parça yok. Yukarıdan bir dosyaya tıkla.", "No pieces on the strip. Click a file above."),
            1 => T($"Sonuç: {Format(VideoCuts.Total(kept))}", $"Result: {Format(VideoCuts.Total(kept))}"),
            var n when files > 1 => T($"Sonuç: {files} dosyadan {n} parça, {Format(VideoCuts.Total(kept))}", $"Result: {n} pieces from {files} files, {Format(VideoCuts.Total(kept))}"),
            var n => T($"Sonuç: {n} parça, {Format(VideoCuts.Total(kept))}", $"Result: {n} pieces, {Format(VideoCuts.Total(kept))}"),
        };
        PlacePlayhead();
        DrawCrop();
        Update();
    }

    /// <summary>Each file's row: its length, whether it is cropped and how many pieces use it; the shown one outlined.</summary>
    private void UpdateRows()
    {
        for (var i = 0; i < _media.Count; i++)
        {
            var m = _media[i];
            if (m.Row is null || m.Detail is null)
                continue;
            var uses = _cuts.Count(c => c.Source == i);
            var detail = Format(_sources[i].Length);
            if (uses > 0)
                detail += T($" · {uses} parça", uses == 1 ? " · 1 piece" : $" · {uses} pieces");
            if (_sources[i].Crop is not null)
                detail += T(" · kırpılmış", " · cropped");
            m.Detail.Text = detail;
            m.Row.BorderBrush = i == _current ? _accent : _line;
        }
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    private static readonly double[] TickSteps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    /// <summary>
    /// The frames and sound of each piece end to end, and the ruler. Only drawn when the view, the
    /// pieces or a strip change, not when the playhead moves.
    /// </summary>
    private void RenderContent()
    {
        _contentDirty = false;
        using var dc = _content.Visual.RenderOpen();
        var w = _timeline.ActualWidth;
        if (w <= 0 || _view.Length <= TimeSpan.Zero)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // The ruler: a labelled tick at least 80 px apart, a short one between.
        var stepSeconds = TickSteps.FirstOrDefault(s => s / _view.Length.TotalSeconds * w >= 80, TickSteps[^1]);
        var step = TimeSpan.FromSeconds(stepSeconds);
        for (var t = TimeSpan.FromSeconds(Math.Floor(_view.Start.TotalSeconds / stepSeconds) * stepSeconds); t <= _view.End; t += step)
        {
            var x = Math.Round(StripX(t)) + 0.5;
            dc.DrawLine(_tickPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            var half = Math.Round(StripX(t + step / 2)) + 0.5;
            dc.DrawLine(_tickPen, new Point(half, RulerHeight - 3), new Point(half, RulerHeight));
            var label = new FormattedText(Tick(t, stepSeconds), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, _muted, dpi);
            dc.DrawText(label, new Point(x + 3, 1));
        }

        var perSecond = 1 / Waveform.Step.TotalSeconds;
        var mid = RulerHeight + ThumbHeight + WaveHeight / 2;
        var wave = new StreamGeometry();
        using (var g = wave.Open())
        {
            for (var i = 0; i < _cuts.Count && i < _starts.Length; i++)
            {
                var c = _cuts[i];
                double x0 = StripX(_starts[i]), x1 = StripX(_starts[i] + c.Length);
                if (x1 < 0 || x0 > w)
                    continue;
                var box = new Rect(x0, RulerHeight, Math.Max(1, x1 - x0), BlockHeight);
                dc.DrawRectangle(Block, null, box);
                var strip = _media[c.Source].Strip;
                if (strip.Frames.Count > 0 && strip.FrameHeight > 0)
                {
                    // Frames side by side from the piece's left edge, each the one nearest its middle.
                    dc.PushClip(new RectangleGeometry(box));
                    var slot = Math.Max(16, ThumbHeight * strip.FrameWidth / strip.FrameHeight);
                    var first = Math.Max(0, Math.Floor(-x0 / slot));
                    for (var x = x0 + first * slot; x < Math.Min(x1, w); x += slot)
                    {
                        var at = c.Start + (_view.ToTime(x + slot / 2, w) - _starts[i]);
                        var f = FilmStrip.Nearest(strip.Times, at > c.End ? c.End : at);
                        dc.DrawImage(strip.Frames[f], new Rect(x, RulerHeight, slot, ThumbHeight));
                    }
                    dc.Pop();
                }
                if (strip.Peaks is { Length: > 0 } peaks)
                {
                    // One line per pixel, as loud as the loudest moment under it, scaled to the file's loudest.
                    for (var px = Math.Max(0, Math.Ceiling(x0)); px < Math.Min(x1, w); px++)
                    {
                        var a = c.Start + (_view.ToTime(px, w) - _starts[i]);
                        var b = c.Start + (_view.ToTime(px + 1, w) - _starts[i]);
                        var peak = Waveform.Max(peaks, a.TotalSeconds * perSecond, b.TotalSeconds * perSecond);
                        var h = Math.Max(0.5, peak / strip.Loudest * (WaveHeight / 2 - 2));
                        g.BeginFigure(new Point(px + 0.5, mid - h), false, false);
                        g.LineTo(new Point(px + 0.5, mid + h), true, false);
                    }
                }
            }
        }
        wave.Freeze();
        dc.DrawGeometry(null, _wavePen, wave);
    }

    /// <summary>
    /// The seams between pieces, their numbers, the selected one outlined with grips on its edges,
    /// and while something is dragged the place it would go.
    /// </summary>
    private void RenderOverlay()
    {
        _overlayDirty = false;
        using var dc = _overlay.Visual.RenderOpen();
        var w = _timeline.ActualWidth;
        if (w <= 0 || _view.Length <= TimeSpan.Zero)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Rect? selected = null;
        for (var i = 0; i < _cuts.Count && i < _starts.Length; i++)
        {
            double x0 = StripX(_starts[i]), x1 = StripX(_starts[i] + _cuts[i].Length);
            if (x1 < 0 || x0 > w)
                continue;
            var box = new Rect(x0, RulerHeight, Math.Max(1, x1 - x0), BlockHeight);
            if (i > 0)
                dc.DrawLine(_seamPen, new Point(x0, RulerHeight), new Point(x0, StripHeight));
            if (_press == Press.Reorder && i == _pressPiece)
                dc.DrawRectangle(Dimmed, null, box);
            if (box.Width >= 22)
            {
                var number = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, Brushes.White, dpi);
                var badge = new Rect(Math.Max(x0, 0) + 3, RulerHeight + 3, number.Width + 8, number.Height + 2);
                if (badge.Right < x1)
                {
                    dc.DrawRoundedRectangle(Badge, null, badge, 3, 3);
                    dc.DrawText(number, new Point(badge.X + 4, badge.Y + 1));
                }
            }
            if (i == _selected)
                selected = box;
        }
        if (selected is { } s)
        {
            dc.DrawRectangle(null, _accentPen, new Rect(s.X + 1, s.Y + 1, Math.Max(0, s.Width - 2), s.Height - 2));
            foreach (var gx in (double[])[s.Left + 1, s.Right - 6])
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(gx, s.Y + (s.Height - 22) / 2, 5, 22), 2, 2);
        }
        var showSlot = _dropSlot >= 0 && (_press != Press.Reorder || _dropSlot != _pressPiece && _dropSlot != _pressPiece + 1);
        if (showSlot)
        {
            var x = StripX(_dropSlot < _starts.Length ? _starts[_dropSlot] : Total);
            dc.DrawRectangle(_accent, null, new Rect(Math.Clamp(x, 0, w - 3) - 1.5, RulerHeight - 4, 4, BlockHeight + 4));
        }
        if (_press == Press.Reorder && _pressPiece >= 0 && _pressPiece < _cuts.Count)
        {
            // The piece rides along under the mouse.
            var width = Math.Clamp(StripX(_starts[_pressPiece] + _cuts[_pressPiece].Length) - StripX(_starts[_pressPiece]), 24, 140);
            dc.DrawRoundedRectangle(Ghost, _accentPen, new Rect(_dragX - width / 2, RulerHeight + 8, width, BlockHeight - 16), 4, 4);
        }
    }

    private static string Tick(TimeSpan t, double step) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : step < 1 ? t.ToString(@"m\:ss\.f") : t.ToString(@"m\:ss");

    private void PlacePlayhead()
    {
        _playhead.Visibility = _cuts.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(_playhead, StripX(_head) - 1);
    }

    private void Update()
    {
        if (_message is not null)
        {
            _info.Text = _message;
            return;
        }
        if (_cuts.Count == 0)
        {
            _info.Text = _sources.Count == 0
                ? T("Bir video ekle ya da pencereye sürükle.", "Add a video or drop one on the window.")
                : T("Sağdaki listeden bir dosyaya tıkla ya da şeride sürükle.", "Click a file in the list on the right, or drag it onto the strip.");
            return;
        }
        var area = CropPixels() is { } p ? T($"   ·   alan {p.Width}×{p.Height}", $"   ·   area {p.Width}×{p.Height}") : "";
        if (_cropping)
        {
            _info.Text = T($"Alan seçmek için görüntü üzerinde sürükle · içinden tut: taşı · kenar ve köşeden: boyutlandır · Del kaldır · Enter ya da C bitir{area}",
                $"Drag over the picture to pick the area · hold inside: move · edges and corners: resize · Del remove · Enter or C done{area}");
            return;
        }
        var hint = T("Space oynat · parçaya tıkla: seç, sürükle: taşı, kenarından: kısalt · S böl · Del sil · I / O başını / sonunu buraya al · ← → sar (Shift ×5, , . kare) · tekerlek yakınlaştır (Shift kaydır) · C kırp · Ctrl+Z geri al",
            "Space play · click a piece: select, drag: move, its edge: trim · S split · Del delete · I / O bring its start / end here · ← → seek (Shift ×5, , . frame) · wheel zooms (Shift scrolls) · C crop · Ctrl+Z undo");
        var piece = "";
        if (HasSelection)
        {
            var c = _cuts[_selected];
            var file = _sources.Count > 1 ? $"{Path.GetFileName(_sources[c.Source].File)} " : "";
            piece = T($"   ·   parça {_selected + 1}: {file}{Format(c.Start)} – {Format(c.End)}", $"   ·   piece {_selected + 1}: {file}{Format(c.Start)} – {Format(c.End)}");
        }
        _info.Text = $"{Format(_head)} / {Format(Total)}{piece}{area}   ·   {hint}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

    /// <summary>
    /// Exact re-encodes so every cut lands on the marked frame; quick copies the streams, which is
    /// instant but starts each piece at the keyframe before its mark. More than one piece is
    /// joined, in the list's order, into one clip, next to the first file used.
    /// </summary>
    private async Task SaveAsync(bool exact, bool gif = false)
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

        Pause();
        var (used, renumbered) = VideoCuts.Inputs(cuts);
        var files = used.Select(i => Path.GetFullPath(_sources[i].File)).ToList();
        var crops = used.Select(i => _sources[i].Crop).ToList();
        exact |= gif || crops.Any(c => c is not null); // a GIF or a cropped picture has to be encoded again
        var length = VideoCuts.Total(cuts);
        var suffix = cuts.Count == 1 ? T(" (kırpılmış)", " (trimmed)") : T(" (birleştirilmiş)", " (joined)");
        var verb = gif ? T("GIF yapılıyor", "Making a GIF") : cuts.Count == 1 ? T("Kırpılıyor", "Trimming") : T("Birleştiriliyor", "Joining");
        var output = new Output(exact, _muteBox.IsChecked == true, gif, suffix, length);
        Close();
        await FileJobs.RunAsync("trim", verb, [files[0]], anchor, async (_, progress, cancel) =>
            files.Count == 1
                ? await OneFileAsync(files[0], renumbered, crops[0], output, progress, cancel)
                : await ManyFilesAsync(files, renumbered, crops, output, progress, cancel));
    }

    /// <summary>How to save: Exact re-encodes, Mute leaves the sound out, Gif makes an animated GIF (always exact, never with sound).</summary>
    private sealed record Output(bool Exact, bool Mute, bool Gif, string Suffix, TimeSpan Length);

    private static string[] Encode(bool video, bool audio) =>
        [.. video ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"] : (string[])[],
         .. audio ? ["-c:a", "aac", "-b:a", "160k"] : (string[])[], "-movflags", "+faststart"];

    private static string[] Maps(bool video, bool audio) =>
        [.. video ? ["-map", "[v]"] : (string[])[], .. audio ? ["-map", "[a]"] : (string[])[]];

    private static readonly string[] Copy = ["-c", "copy", "-avoid_negative_ts", "make_zero"];

    /// <summary>The picture and sound left once the options are applied, or why nothing would be.</summary>
    private static (bool Video, bool Audio) Kept(bool video, bool audio, Output o)
    {
        if (o.Gif && !video)
            throw new InvalidOperationException(T("GIF yapılacak görüntü yok: parçalar yalnızca ses.", "There's no picture to make a GIF of: the pieces are sound only."));
        audio &= !o.Mute && !o.Gif;
        if (!video && !audio)
            throw new InvalidOperationException(T("Ses kaldırılınca geriye bir şey kalmıyor: parçalarda görüntü yok.", "Without the sound nothing is left: the pieces have no picture."));
        return (video, audio);
    }

    /// <summary>The -filter_complex tail, maps and codecs for the joined [v] and [a].</summary>
    private static string[] Finish(string graph, bool video, bool audio, int width, Output o) => o.Gif
        ? ["-filter_complex", graph + VideoCuts.GifTail(width), "-map", "[gif]"]
        : ["-filter_complex", graph, .. Maps(video, audio), .. Encode(video, audio)];

    private static string Extension(bool video, Output o) => o.Gif ? "gif" : video ? "mp4" : "m4a";

    private static async Task<string> OneFileAsync(string input, List<Cut> cuts, CropArea? crop, Output o, Action<double> progress, CancellationToken cancel)
    {
        string[] mute = o.Mute ? ["-an"] : [];
        if (!o.Exact)
        {
            if (o.Mute)
            {
                var probed = await Ffmpeg.ProbeAsync(input, cancel);
                Kept(probed.Video, probed.Audio, o); // a sound-only file without its sound is nothing; say so rather than let ffmpeg fail
            }
            var copied = FileJobs.OutputFor(input, o.Suffix, MediaKinds.Extension(input));
            if (cuts.Count == 1)
                await Ffmpeg.RunAsync(input, ["-t", VideoCuts.Seconds(o.Length), .. mute, .. Copy], copied, progress, cancel,
                    before: ["-ss", VideoCuts.Seconds(cuts[0].Start)], length: o.Length);
            else
                await ConcatAsync([input], cuts, copied, o.Length, progress, cancel, mute);
            return copied;
        }

        var info = await Ffmpeg.ProbeAsync(input, cancel);
        var (video, audio) = Kept(info.Video, info.Audio, o);
        var output = FileJobs.OutputFor(input, o.Suffix, Extension(video, o));
        var cropFilter = video && crop is { } c ? c.Filter(info.Width, info.Height) : null;
        if (cuts.Count == 1 && !o.Gif)
        {
            // One piece: seeking before -i skips decoding everything up to it.
            string[] picture = !video ? ["-vn"] : cropFilter is null ? [] : ["-vf", cropFilter];
            await Ffmpeg.RunAsync(input, ["-t", VideoCuts.Seconds(o.Length), .. picture, .. audio ? (string[])[] : ["-an"], .. Encode(video, audio)], output, progress, cancel,
                before: ["-ss", VideoCuts.Seconds(cuts[0].Start)], length: o.Length);
            return output;
        }
        var width = (crop is { } k ? k.Apply(info) : info).Width;
        await Ffmpeg.RunAsync(input, Finish(VideoCuts.Filter(cuts, video, audio, [cropFilter]), video, audio, width, o), output, progress, cancel, length: o.Length);
        return output;
    }

    /// <summary>
    /// Pieces of several files. Files made the same way are copied together when quick is asked
    /// for; otherwise every piece is brought to one size, frame rate and sound format and encoded.
    /// </summary>
    private static async Task<string> ManyFilesAsync(List<string> files, List<Cut> cuts, List<CropArea?> crops, Output o, Action<double> progress, CancellationToken cancel)
    {
        var infos = new List<MediaInfo>();
        foreach (var file in files)
            infos.Add(await Ffmpeg.ProbeAsync(file, cancel));

        if (!o.Exact && VideoCuts.CanCopy(files, infos))
        {
            Kept(infos[0].Video, infos[0].Audio, o); // alike files: the first speaks for all
            var copied = FileJobs.OutputFor(files[0], o.Suffix, MediaKinds.Extension(files[0]));
            await ConcatAsync(files, cuts, copied, o.Length, progress, cancel, o.Mute ? ["-an"] : []);
            return copied;
        }

        // The joined size comes from the first picture as cropped, so a cropped recording isn't padded back out.
        var cropFilters = infos.Select((info, i) => info.Video && crops[i] is { } c ? c.Filter(info.Width, info.Height) : null).ToList();
        var shown = infos.Select((info, i) => crops[i] is { } c ? c.Apply(info) : info).ToList();
        var format = JoinFormat.For(cuts, shown);
        var (video, audio) = Kept(format.Video, format.Audio, o);
        format = format with { Audio = audio };
        var output = FileJobs.OutputFor(files[0], o.Suffix, Extension(video, o));
        await Ffmpeg.RunAsync(files[0], Finish(VideoCuts.JoinFilter(cuts, infos, format, cropFilters), video, audio, format.Width, o),
            output, progress, cancel, length: o.Length, moreInputs: files.Skip(1));
        return output;
    }

    /// <summary>The concat demuxer reads the pieces straight out of the files, so nothing is re-encoded.</summary>
    private static async Task ConcatAsync(IReadOnlyList<string> files, List<Cut> cuts, string output, TimeSpan length, Action<double> progress, CancellationToken cancel, string[] extra)
    {
        var list = Path.Combine(Path.GetTempPath(), $"rs-cuts-{Guid.NewGuid():N}.ffconcat");
        await File.WriteAllTextAsync(list, VideoCuts.ConcatList(files, cuts), new System.Text.UTF8Encoding(false), cancel);
        try
        {
            await Ffmpeg.RunAsync(list, [.. extra, .. Copy], output, progress, cancel, before: ["-f", "concat", "-safe", "0"], length: length);
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
