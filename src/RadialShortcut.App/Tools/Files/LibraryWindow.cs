using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Kütüphane: everything the tools made, newest first, searchable (Ctrl+F). Enter opens,
/// Ctrl+C copies, Del deletes the file. Files from before the history existed are listed too.
/// </summary>
internal sealed class LibraryWindow : Window
{
    private static LibraryWindow? _open;

    private static readonly Dictionary<string, string> ToolNames = new()
    {
        ["capture"] = "Ekran görüntüsü", ["record"] = "Ekran kaydı", ["ocr"] = "Metin", ["convert"] = "Dönüştürüldü",
        ["compress"] = "Sıkıştırıldı", ["resize"] = "Boyutlandırıldı", ["trim"] = "Kırpıldı", ["annotate"] = "Not eklendi",
        ["download"] = "İndirildi", ["nobg"] = "Arka plan silindi", ["file"] = "Dosya",
    };

    private readonly TextBox _search = new() { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _count = new() { FontSize = 11, Margin = new Thickness(2, 6, 0, 0) };
    private List<HistoryEntry> _all = [];
    private readonly Dictionary<string, BitmapSource> _thumbs = new(StringComparer.OrdinalIgnoreCase);

    public static void ShowOrActivate()
    {
        if (_open is null)
        {
            _open = new LibraryWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            _open.Reload();
        }
        _open.Activate();
        _open._search.Focus();
    }

    private LibraryWindow()
    {
        Title = "Kütüphane";
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _search.Background = Palette.Raised;
        _search.Foreground = Palette.Text;
        _search.BorderBrush = Palette.Line;
        _search.CaretBrush = Palette.Text;
        _count.Foreground = Palette.Muted;
        _list.Foreground = Palette.Text;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Button("Klasörü aç", () => ToolRunner.OpenUrl(AppPaths.OutputDirectory)));
        buttons.Children.Add(Button("Klasörde göster", () => { if (Selected?.Path is { } p) ToolRunner.ShowInFolder(p); }));
        buttons.Children.Add(Button("Sil", Delete));
        buttons.Children.Add(Button("Kopyala", Copy));
        buttons.Children.Add(Button("Aç", Open, primary: true));

        var top = new StackPanel();
        top.Children.Add(_search);
        top.Children.Add(_count);
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        _search.TextChanged += (_, _) => Filter();
        _list.MouseDoubleClick += (_, _) => Open();
        PreviewKeyDown += OnKey;
        Reload();
    }

    private HistoryEntry? Selected => (_list.SelectedItem as FrameworkElement)?.Tag as HistoryEntry;

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F)
            _search.Focus();
        else if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.Enter)
            Open();
        else if (e.Key == Key.Down && _search.IsKeyboardFocused && _list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
            ((UIElement)_list.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
        }
        else if (ctrl && e.Key == Key.C && !_search.IsKeyboardFocused)
            Copy();
        else if (e.Key == Key.Delete && !_search.IsKeyboardFocused)
            Delete();
        else
            return;
        e.Handled = true;
    }

    private void Reload()
    {
        var entries = History.Load(AppPaths.HistoryFile).Where(e => e.Path is null || File.Exists(e.Path)).ToList();
        var known = new HashSet<string>(entries.Select(e => e.Path).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(AppPaths.OutputDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.OutputDirectory, "*", SearchOption.AllDirectories))
            {
                if (!known.Contains(file) && !Path.GetFileName(file).StartsWith('.'))
                    entries.Add(new HistoryEntry(File.GetLastWriteTime(file), "file", file));
            }
        }
        _all = entries.OrderByDescending(e => e.Time).ToList();
        _thumbs.Clear(); // a deleted file can come back under the same name

        Filter();
    }

    private void Filter()
    {
        _list.Items.Clear();
        var shown = _all.Where(e => History.Matches(e, _search.Text) || ToolNames.GetValueOrDefault(e.Tool, "").Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase)).Take(300).ToList();
        foreach (var entry in shown)
            _list.Items.Add(Row(entry));
        _count.Text = shown.Count == _all.Count ? $"{_all.Count} kayıt" : $"{shown.Count} / {_all.Count} kayıt";
    }

    private FrameworkElement Row(HistoryEntry entry)
    {
        var thumb = new Border { Width = 56, Height = 40, Margin = new Thickness(0, 2, 10, 2), Background = Palette.Raised, CornerRadius = new CornerRadius(4) };
        if (entry.Path is { } path && MediaKinds.Of(path) == MediaKind.Image)
        {
            try
            {
                // Kept for the life of the window, so typing in the search box does not decode them again.
                if (!_thumbs.TryGetValue(path, out var image))
                {
                    var decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.UriSource = new Uri(path);
                    decoded.DecodePixelWidth = 112; // small decode: the list stays light with hundreds of shots
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.EndInit();
                    decoded.Freeze();
                    _thumbs[path] = image = decoded;
                }
                thumb.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
            }
            catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
            {
            }
        }
        else
        {
            var glyph = entry.Text is not null ? "T" : MediaKinds.Of(entry.Path ?? "") switch { MediaKind.Video => "▶", MediaKind.Audio => "♪", MediaKind.Pdf => "PDF", _ => "•" };
            thumb.Child = new TextBlock { Text = glyph, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var name = entry.Path is { } p ? Path.GetFileName(p) : Shorten(entry.Text ?? "");
        var detail = $"{ToolNames.GetValueOrDefault(entry.Tool, entry.Tool)} · {entry.Time:dd.MM.yyyy HH:mm}";
        if (entry.Path is { } file && File.Exists(file))
            detail += $" · {MediaKinds.Size(new FileInfo(file).Length)}";

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Foreground = Palette.Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, Foreground = Palette.Muted, FontSize = 11 });
        var row = new DockPanel { Tag = entry };
        row.Children.Add(thumb);
        row.Children.Add(text);
        return row;
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    private void Open()
    {
        if (Selected is not { } entry)
            return;
        if (entry.Path is { } path)
            ToolRunner.OpenUrl(path);
        else if (entry.Text is { } text)
            ToolRunner.CopyText(text);
    }

    private void Copy()
    {
        if (Selected is not { } entry)
            return;
        if (entry.Text is { } text)
            ToolRunner.CopyText(text);
        else if (entry.Path is { } path)
            ToolRunner.Retry(() => Clipboard.SetFileDropList(new StringCollection { path }));
    }

    private async void Delete()
    {
        if (Selected?.Path is not { } path)
            return;
        var anchor = new Int32Rect((int)Left, (int)Top, (int)ActualWidth, 1);
        if (!await ResultCard.AskAsync("Dosya silinsin mi?", Path.GetFileName(path), "Sil", anchor))
            return;
        ToolRunner.TryDelete(path);
        Reload();
    }

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
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
