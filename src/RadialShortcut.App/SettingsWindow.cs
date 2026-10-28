using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadialShortcut.App.Tools;
using RadialShortcut.Core;
using Shape = System.Windows.Shapes.Path;

namespace RadialShortcut.App;

/// <summary>
/// Ayarlar: everything in config.json without editing it by hand. The rings are edited on a live
/// preview drawn by <see cref="RingWindow"/> itself. Works on a copy and writes the file on "Kaydet";
/// the config watcher then reloads it like a hand edit. Values the window does not show (DeepL key,
/// updateRepo, slice icons...) are carried over untouched.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private const string GearIcon = "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6 M12 2v3 M12 19v3 M2 12h3 M19 12h3 M4.9 4.9l2.1 2.1 M17 17l2.1 2.1 M4.9 19.1L7 17 M17 7l2.1-2.1";
    private const string RingIcon = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8 M12 3v5 M12 16v5 M3 12h5 M16 12h5";
    private const string FolderIcon = "M3 6h6l2 2h10v11H3z";
    private const string PlusIcon = "M12 5v14 M5 12h14";
    private const string TrashIcon = "M5 7h14 M10 7V4h4v3 M7 7l1 13h8l1-13";
    private const string LeftIcon = "M15 6l-6 6 6 6";
    private const string RightIcon = "M9 6l6 6-6 6";
    private const string GlobeIcon = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M3 12h18 M12 3c-3 3-3 15 0 18 M12 3c3 3 3 15 0 18";
    private const string AppIcon = "M5 5h14v14H5z M10 9h5v5 M15 9l-6 6";
    private const string ToolIcon = "M4 4h7v7H4z M13 4h7v7h-7z M4 13h7v7H4z M13 13h7v7h-7z";
    private const string WarnIcon = "M12 4l9 16H3z M12 10v4 M12 17v.5";

    private static readonly (string Type, string Name, string Icon)[] SliceTypes =
    [
        ("tool", "Araç", ToolIcon),
        ("launch", "Uygulama aç", AppIcon),
        ("url", "Web adresi", GlobeIcon),
        ("hotkey", "Kısayol tuşu", "M3 7h18v10H3z M6 10h1 M9 10h1 M12 10h1 M15 10h1 M18 10h1 M8 14h8"),
        ("text", "Metin yapıştır", "M5 6h14 M5 10h14 M5 14h9 M5 18h6"),
    ];
    private static readonly double[] Scales = [0.85, 1, 1.15, 1.3];
    private static readonly string[] Accents = ["#3D8BFF", "#8B5CF6", "#EC4899", "#EF4444", "#F59E0B", "#22C55E", "#14B8A6"];

    /// <summary>Icons a slice can wear: the tools' own, then some general ones.</summary>
    private static readonly string[] IconChoices =
    [
        .. DefaultRing.Items.Concat(DefaultFileRing.Items).Select(i => i.Icon),
        AppIcon, GlobeIcon,
        "M3 7h18v10H3z M6 10h1 M9 10h1 M12 10h1 M15 10h1 M18 10h1 M8 14h8",
        "M5 6h14 M5 10h14 M5 14h9 M5 18h6",
        "M3 6h6l2 2h10v11H3z", // klasör
        "M4 6h16v12H4z M4 7l8 6 8-6", // posta
        "M4 11l8-7 8 7 M6 9v11h12V9", // ev
        "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z", // yıldız
        "M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z", // kalp
        "M9 18V5l11-2v13 M9 18a3 3 0 1 1-6 0a3 3 0 1 1 6 0 M20 16a3 3 0 1 1-6 0a3 3 0 1 1 6 0", // müzik
        "M4 5h16v14H4z M7 9l3 3-3 3 M12 15h5", // terminal
        "M8 7l-5 5 5 5 M16 7l5 5-5 5", // kod
        "M4 8h4l2-3h4l2 3h4v11H4z M12 10a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7", // kamera
        "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z M10 20h4", // zil
        "M6 3h12v18l-6-4-6 4z", // yer imi
        "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 3", // saat
        "M13 3L5 13h6l-1 8 8-10h-6z", // şimşek
        "M5 12h14 M13 6l6 6-6 6", // ok
    ];

    private static readonly string[] ThemeIds = ["system", "light", "dark"];
    private static readonly IReadOnlyList<RingItem> Tools = [.. DefaultRing.Items, .. DefaultFileRing.Items];

    private readonly UserSettings _settings;
    private readonly HotkeyRing? _hotkey;
    private readonly Func<Task<bool>> _checkUpdate;
    private readonly Func<Action<string>, Task<string?>> _installUpdate;
    private bool _dirty;
    private bool _loading; // set while editors are filled from the model, so their events do not write back

    // Shell
    private readonly ContentControl _page = new();
    private readonly List<(Pill Pill, TextBlock Text, Shape Icon)> _nav = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private FrameworkElement? _generalPage, _ringsPage;
    private int _pageIndex;

    // Genel
    private readonly ChordBox _ringHotkey = new(minKeys: 2);
    private readonly Segmented _theme = new("Sistem", "Açık", "Koyu");
    private readonly Toggle _startup = new();
    private readonly List<string> _excluded = [];
    private readonly WrapPanel _excludedChips = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _updateStatus = Small("");
    private static readonly string[] Languages = ["TR", "EN", "DE", "FR", "ES"];
    private readonly Segmented _translateTarget = new("Türkçe", "İngilizce", "Almanca", "Fransızca", "İspanyolca");
    private readonly Input _deepLKey = new("Boş: ücretsiz Google çevirisi kullanılır");

    // Halkalar
    private int _ringIndex = -1, _slice = -1;
    private readonly StackPanel _ringCards = new();
    private readonly TextBlock _ringHeader = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _ringSubheader = Small("");
    private readonly Border _stageHost = new() { Background = Brushes.Transparent, Width = new RingStyle().Size, Height = new RingStyle().Size };
    private readonly TextBlock _sliceCount = Small("");
    private RingWindow? _preview;
    private FrameworkElement? _previewRoot;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    private readonly Input _ringName = new("Halkanın adı");
    private readonly Segmented _ringLayer = new("Sağ tuş", "Shift + sağ tuş");
    private readonly Segmented _ringWhere = new("Her yerde", "Belirli uygulamada");
    private readonly StackPanel _matchPanel = new();
    private readonly Input _ringProcess = new("örn. chrome");
    private readonly Input _ringTitle = new("İsteğe bağlı, örn. YouTube");

    private readonly TextBlock _sliceHeader = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _sliceEditor = new();
    private readonly List<(Pill Pill, TextBlock Text, Shape Icon)> _typeChips = [];
    private readonly StackPanel _toolPanel = new();
    private readonly List<(Pill Pill, string Id, TextBlock Text, Shape Icon)> _toolTiles = [];
    private readonly Input _sliceLabel = new("");
    private readonly Border _iconPreview = new() { Width = 38, Height = 38, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
    private readonly Popup _iconPopup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    private readonly Segmented _ringScale = new("Küçük", "Normal", "Büyük", "Çok büyük");
    private readonly List<(Pill Pill, string Color)> _swatches = [];
    private readonly TextBlock _valueCaption = Caption("");
    private readonly Input _sliceValue = new("");
    private readonly ChordBox _sliceChord = new(minKeys: 1);
    private readonly Pill _browse;
    private readonly Pill _install;
    private readonly StackPanel _valuePanel = new();
    private readonly TextBlock _sliceProblem = new() { Foreground = Danger, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public static void ShowOrActivate(HotkeyRing? hotkey, Func<Task<bool>> checkUpdate, Func<Action<string>, Task<string?>> installUpdate)
    {
        if (_open is null)
        {
            var window = new SettingsWindow(hotkey, checkUpdate, installUpdate);
            window.Closed += (_, _) => { if (_open == window) _open = null; };
            _open = window;
            _open.Show();
        }
        if (_open.WindowState == WindowState.Minimized)
            _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    private SettingsWindow(HotkeyRing? hotkey, Func<Task<bool>> checkUpdate, Func<Action<string>, Task<string?>> installUpdate)
    {
        _hotkey = hotkey;
        _checkUpdate = checkUpdate;
        _installUpdate = installUpdate;
        _install = TextButton("Güncelle", InstallUpdate, Kind.Primary);
        _install.Visibility = Visibility.Collapsed;
        _install.Margin = new Thickness(0, 0, 8, 0);
        var loaded = UserSettings.TryLoad(AppPaths.ConfigFile, out var settings, out var error);
        _settings = settings.Clone();
        AddBuiltInRings();
        _browse = TextButton("Gözat…", Browse, Kind.Secondary);

        Title = "RadialShortcut Ayarları";
        Width = Math.Min(1300, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        MinWidth = 960;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        SourceInitialized += (_, _) => DarkTitleBar();

        var content = new DockPanel();
        var footer = Footer();
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        content.Children.Add(_page);

        var root = new Grid { Background = Palette.Surface };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = Sidebar();
        Grid.SetColumn(content, 1);
        root.Children.Add(sidebar);
        root.Children.Add(content);
        Content = root;

        Resources.Add(typeof(ScrollBar), ScrollBarStyle());
        PaintScrollBars(_settings.RingStyle().Accent);
        FillGeneral();
        ShowPage(0);
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RebuildPreview(animate: false);
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _preview?.Close();
            _hotkey?.Resume();
        };

        if (!loaded)
            Say($"config.json okunamadı ({error}). Kaydedersen yerine bu ayarlar yazılır; eskisi config.json.bak olarak kalır.", problem: true);
    }

#if DEBUG
    /// <summary><c>--settings-shot &lt;folder&gt;</c>: both pages to PNG, to check the layout without clicking through.</summary>
    public static async Task ShotAsync(string folder)
    {
        var window = new SettingsWindow(null, () => Task.FromResult(false), _ => Task.FromResult<string?>(null));
        window.Show();
        foreach (var (page, name) in new[] { (0, "settings-general"), (1, "settings-rings") })
        {
            window.ShowPage(page);
            if (page == 1)
                window.SelectSlice(2);
            await Task.Delay(1200);
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
            png.Save(file);
        }
        window._dirty = false;
        window.Close();
    }
#endif

    /// <summary>The built-in rings show up as editable rings; saving writes them out like any other.</summary>
    private void AddBuiltInRings()
    {
        bool HasGlobal(string layer) => _settings.Rings.Any(r => r.Match is null && LayerOf(r) == layer);
        if (!HasGlobal(RingLayers.Files))
            _settings.Rings.Insert(0, new RingConfig { Id = NewId("dosya"), Name = "Dosya", Layer = RingLayers.Files, Slices = DefaultFileRing.Items.Select(i => new SliceConfig { Tool = i.Id }).ToList() });
        if (!HasGlobal(RingLayers.Main))
            _settings.Rings.Insert(0, new RingConfig { Id = NewId("genel"), Name = "Genel", Slices = DefaultRing.For(8).Select(i => new SliceConfig { Tool = i.Id }).ToList() });
    }

    // ---- Çerçeve ----

    private FrameworkElement Sidebar()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 22, 18, 26) };
        head.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "RadialShortcut", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        name.Children.Add(Small($"Ayarlar · v{Updater.Current.ToString(3)}"));
        head.Children.Add(name);

        var nav = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
        foreach (var (i, label, icon) in new[] { (0, "Genel", GearIcon), (1, "Halkalar", RingIcon) })
        {
            var shape = IconShape(icon, Palette.Muted);
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Sized(shape, 18, new Thickness(0, 0, 12, 0)));
            row.Children.Add(text);
            var pill = new Pill(row, () => ShowPage(i)) { Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
            _nav.Add((pill, text, shape));
            nav.Children.Add(pill);
        }

        var folder = IconButton(FolderIcon, "Ayar klasörünü aç", () => ToolRunner.OpenUrl(AppPaths.ConfigDirectory), Kind.Ghost);
        folder.Margin = new Thickness(10, 0, 10, 16);
        folder.ToolTip = "config.json'u elle düzenlemek için";

        var panel = new DockPanel { Background = SidebarBrush };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(folder, Dock.Bottom);
        panel.Children.Add(head);
        panel.Children.Add(folder);
        panel.Children.Add(nav);
        return new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    private FrameworkElement Footer()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var revert = TextButton("Değişiklikleri geri al", Revert, Kind.Ghost);
        revert.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(revert);
        buttons.Children.Add(TextButton("Kapat", Close, Kind.Secondary));
        var save = TextButton("Kaydet", () => Save(), Kind.Primary);
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        var bar = new DockPanel { Margin = new Thickness(24, 12, 20, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        _status.Margin = new Thickness(0, 0, 16, 0);
        return new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = SidebarBrush, Child = bar };
    }

    /// <summary>
    /// Throws away unsaved edits by reopening the window from config.json, on the same page and spot;
    /// simpler and surer than putting every editor back by hand.
    /// </summary>
    private void Revert()
    {
        if (!_dirty)
        {
            Say("Geri alınacak bir değişiklik yok.");
            return;
        }
        if (MessageBox.Show(this, "Kaydedilmemiş tüm değişiklikler silinsin mi?", "RadialShortcut Ayarları", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _dirty = false;
        var fresh = new SettingsWindow(_hotkey, _checkUpdate, _installUpdate)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            WindowState = WindowState,
        };
        fresh.Closed += (_, _) => { if (_open == fresh) _open = null; };
        _open = fresh;
        fresh.Show();
        fresh.ShowPage(_pageIndex);
        fresh.Say("Değişiklikler geri alındı.");
        Close();
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        var built = _ringsPage is not null;
        _page.Content = index == 0 ? _generalPage ??= GeneralPage() : _ringsPage ??= RingsPage();
        if (index == 1 && built)
            RebuildPreview(animate: true);
        for (var i = 0; i < _nav.Count; i++)
        {
            var (pill, text, icon) = _nav[i];
            var on = i == index;
            pill.Paint(on ? AccentSoft : Brushes.Transparent, on ? AccentSoft : HoverBrush);
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            text.Foreground = on ? Palette.Text : Palette.Muted;
            icon.Stroke = on ? Palette.Accent : Palette.Muted;
        }
    }

    private void MarkDirty()
    {
        if (_loading)
            return;
        _dirty = true;
        Say("Kaydedilmemiş değişiklikler var.");
    }

    private void Say(string text, bool problem = false)
    {
        _status.Text = text;
        _status.Foreground = problem ? Danger : Palette.Muted;
    }

    // ---- Genel ----

    private FrameworkElement GeneralPage()
    {
        var page = new StackPanel { MaxWidth = 720, Margin = new Thickness(36, 30, 36, 30), HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(PageTitle("Genel", "Halkanın nasıl açılacağı ve uygulamanın davranışı."));

        page.Children.Add(Section("Halkayı açma",
            Row("Sağ tuş", "Sağ tuşu basılı tut, fareyi bir dilime doğru çek, bırak.", Keycaps("Sağ tuş")),
            Row("Dosya halkası", "Explorer'da dosya seçiliyken kendiliğinden açılır; başka yerde Shift ile.", Keycaps("Shift", "Sağ tuş")),
            Row("Klavye kısayolu", "Touchpad için. Basılı tut, imleçle dilime git, bırak. Shift eklersen dosya halkası açılır. Değiştirmek için kutuya tıkla.", _ringHotkey)));

        page.Children.Add(Section("Görünüm",
            Row("Tema", "Sonuç kartları, araç pencereleri ve bu pencere. Yeni açılan pencerelere uygulanır.", _theme)));

        page.Children.Add(Section("Başlangıç",
            Row("Windows ile başlat", "Oturum açınca tepside sessizce başlar.", _startup)));

        _deepLKey.MinWidth = 300;
        page.Children.Add(Section("Çeviri",
            Row("Hedef dil", "\"Çevir\" aracı metni bu dile çevirir.", _translateTarget),
            Row("DeepL API anahtarı", "İsteğe bağlı; daha iyi çeviri için. Ücretsiz anahtarlar \":fx\" ile biter.", _deepLKey)));

        var add = new Input("Uygulama adı, örn. mstsc");
        void AddExcluded()
        {
            var name = add.Text.Trim();
            if (name.Length > 0 && !_excluded.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _excluded.Add(name);
                RefreshExcluded();
                MarkDirty();
            }
            add.Text = "";
        }
        add.Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                AddExcluded();
        };
        var addRow = new DockPanel();
        var addButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var addButton = TextButton("Ekle", AddExcluded, Kind.Secondary);
        addButton.Margin = new Thickness(8, 0, 0, 0);
        Pill? pick = null;
        pick = TextButton("Açık uygulamalardan ▾", () => PickRunningApp(pick!, name =>
        {
            add.Text = name;
            AddExcluded();
        }), Kind.Secondary);
        pick.Margin = new Thickness(8, 0, 0, 0);
        addButtons.Children.Add(addButton);
        addButtons.Children.Add(pick);
        DockPanel.SetDock(addButtons, Dock.Right);
        addRow.Children.Add(addButtons);
        addRow.Children.Add(add);
        var excluded = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        excluded.Children.Add(Small("Bu uygulamalarda sağ tuşa hiç dokunulmaz (uzak masaüstü, oyunlar...). Tam ekran pencerelerde halka zaten açılmaz."));
        excluded.Children.Add(_excludedChips);
        excluded.Children.Add(new Border { Height = 12 });
        excluded.Children.Add(addRow);
        page.Children.Add(Section("Hariç uygulamalar", excluded));

        var check = TextButton("Güncellemeleri denetle", CheckUpdate, Kind.Secondary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_install);
        buttons.Children.Add(check);
        page.Children.Add(Section("Güncellemeler",
            Row($"Sürüm {Updater.Current.ToString(3)}", "", buttons, _updateStatus)));
        _updateStatus.Text = "Günde bir kez kendiliğinden denetlenir.";
        // A release the tray already found shows its button straight away.
        CheckUpdate();

        return Scroll(page);
    }

    private void FillGeneral()
    {
        _loading = true;
        _ringHotkey.Set(_settings.RingHotkey);
        _ringHotkey.Changed += _ => MarkDirty();
        _ringHotkey.Recording += on => { if (on) _hotkey?.Suspend(); else _hotkey?.Resume(); };

        _theme.Select(Math.Max(0, Array.FindIndex(ThemeIds, t => t.Equals(_settings.Theme, StringComparison.OrdinalIgnoreCase))));
        _theme.Changed += _ => MarkDirty();

        _startup.Set(StartupRegistration.IsEnabled());
        _startup.Changed += _ => MarkDirty();

        var language = Array.FindIndex(Languages, l => l.Equals(_settings.TranslateTarget, StringComparison.OrdinalIgnoreCase));
        _translateTarget.Select(Math.Max(0, language));
        _translateTarget.Changed += _ => MarkDirty();
        _deepLKey.Text = _settings.DeepLApiKey ?? "";
        _deepLKey.Box.TextChanged += (_, _) => MarkDirty();

        _excluded.AddRange(_settings.ExcludedProcesses ?? ["mstsc", "vmconnect"]);
        RefreshExcluded();
        _loading = false;
    }

    private void RefreshExcluded()
    {
        _excludedChips.Children.Clear();
        foreach (var name in _excluded)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = RingCatalog.ProcessName(name), VerticalAlignment = VerticalAlignment.Center });
            var remove = new Pill(Sized(IconShape("M7 7l10 10 M17 7L7 17", Palette.Muted), 12, new Thickness(0)), () =>
            {
                _excluded.Remove(name);
                RefreshExcluded();
                MarkDirty();
            })
            { Padding = new Thickness(4), Margin = new Thickness(6, 0, -4, 0), CornerRadius = new CornerRadius(10), ToolTip = "Kaldır" };
            remove.Paint(Brushes.Transparent, HoverBrush);
            row.Children.Add(remove);
            _excludedChips.Children.Add(new Border
            {
                Child = row,
                Background = SidebarBrush,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 8),
            });
        }
        if (_excluded.Count == 0)
            _excludedChips.Children.Add(Small("Liste boş: sağ tuş her uygulamada çalışır."));
    }

    private async void CheckUpdate()
    {
        _updateStatus.Text = "Denetleniyor…";
        var found = await _checkUpdate();
        _install.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        _updateStatus.Text = found
            ? "Yeni sürüm var. \"Güncelle\" ile kurabilirsin."
            : "En son sürümü kullanıyorsun (ya da GitHub'a ulaşılamadı).";
    }

    /// <summary>Downloads and installs the waiting release; the app restarts itself when it is done.</summary>
    private async void InstallUpdate()
    {
        // The app restarts at the end, and a shutdown cannot wait on the "save changes?" question.
        if (_dirty)
        {
            _updateStatus.Text = "Önce değişiklikleri kaydet ya da geri al.";
            return;
        }
        _install.IsEnabled = false;
        var error = await _installUpdate(text => _updateStatus.Text = text);
        if (error is not null)
        {
            _updateStatus.Text = $"Güncellenemedi: {error}";
            _install.IsEnabled = true;
        }
    }

    /// <summary>A menu of the apps that have a window open right now.</summary>
    private static void PickRunningApp(FrameworkElement anchor, Action<string> picked)
    {
        var apps = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle.Length > 0 && process.Id != Environment.ProcessId)
                    apps.TryAdd(process.ProcessName, process.MainWindowTitle);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited meanwhile, or belongs to an elevated process.
            }
            finally
            {
                process.Dispose();
            }
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (name, title) in apps)
        {
            var item = new MenuItem { Header = $"{name}  —  {(title.Length > 50 ? title[..50] + "…" : title)}" };
            item.Click += (_, _) => picked(name);
            menu.Items.Add(item);
        }
        if (apps.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Açık pencere bulunamadı", IsEnabled = false });
        menu.IsOpen = true;
    }

    // ---- Halkalar ----

    private RingConfig? Ring => _ringIndex >= 0 && _ringIndex < _settings.Rings.Count ? _settings.Rings[_ringIndex] : null;
    private SliceConfig? Slice => Ring is { } r && _slice >= 0 && _slice < r.Slices.Count ? r.Slices[_slice] : null;

    private static string LayerOf(RingConfig ring) => ring.Layer?.Trim().ToLowerInvariant() ?? RingLayers.Main;

    private FrameworkElement RingsPage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        // Left: the rings, grouped by how they open.
        var newRing = IconButton(PlusIcon, "Yeni", AddRing, Kind.Secondary);
        newRing.VerticalAlignment = VerticalAlignment.Center;
        var listHead = new DockPanel { Margin = new Thickness(16, 20, 12, 12) };
        DockPanel.SetDock(newRing, Dock.Right);
        listHead.Children.Add(newRing);
        listHead.Children.Add(new TextBlock { Text = "Halkalar", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var list = new DockPanel();
        DockPanel.SetDock(listHead, Dock.Top);
        list.Children.Add(listHead);
        list.Children.Add(new ScrollViewer { Content = _ringCards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 0, 10, 12) });
        var left = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };

        // Middle: the ring itself, as it will look.
        var head = new StackPanel { Margin = new Thickness(28, 20, 28, 0) };
        head.Children.Add(_ringHeader);
        head.Children.Add(_ringSubheader);
        head.Children.Add(StyleBar());
        var viewbox = new Viewbox { Child = _stageHost, StretchDirection = StretchDirection.Both, MaxWidth = 520, MaxHeight = 520, Margin = new Thickness(12) };
        var stage = new Border
        {
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(28, 16, 28, 0),
            Background = new RadialGradientBrush(Color.FromRgb(0x2A, 0x2F, 0x37), Color.FromRgb(0x14, 0x16, 0x1A)) { RadiusX = 0.75, RadiusY = 0.75 },
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            Child = viewbox,
        };
        _stageHost.MouseMove += (_, e) =>
        {
            var at = SliceUnder(e);
            _preview?.Highlight(at >= 0 ? at : _slice);
            _stageHost.Cursor = at >= 0 ? Cursors.Hand : Cursors.Arrow;
        };
        _stageHost.MouseLeave += (_, _) => _preview?.Highlight(_slice);
        _stageHost.MouseLeftButtonUp += (_, e) =>
        {
            if (SliceUnder(e) is var at && at >= 0)
                SelectSlice(at);
        };
        var addSlice = IconButton(PlusIcon, "Dilim ekle", AddSlice, Kind.Secondary);
        addSlice.VerticalAlignment = VerticalAlignment.Center;
        addSlice.Margin = new Thickness(16, 0, 0, 0);
        var under = new DockPanel { Margin = new Thickness(28, 12, 28, 20) };
        DockPanel.SetDock(addSlice, Dock.Right);
        under.Children.Add(addSlice);
        var tip = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tip.Children.Add(Small("Bir dilime tıkla, sağda düzenle. Dilimler saat yönünde, en üstten başlar."));
        tip.Children.Add(_sliceCount);
        under.Children.Add(tip);
        var middle = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(under, Dock.Bottom);
        middle.Children.Add(head);
        middle.Children.Add(under);
        middle.Children.Add(stage);

        // Right: the ring's settings and the chosen slice.
        var inspector = new StackPanel { Margin = new Thickness(0, 20, 20, 20) };
        inspector.Children.Add(RingInspector());
        inspector.Children.Add(SliceInspector());
        var right = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);

        SelectRing(0);
        return grid;
    }

    /// <summary>Size and color, shared by every ring, right above the preview that shows them.</summary>
    private FrameworkElement StyleBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(new TextBlock { Text = "Tüm halkalar:", Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        bar.Children.Add(_ringScale);
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var color in Accents)
        {
            var fill = (Brush)new BrushConverter().ConvertFromString(color)!;
            var swatch = new Pill(new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = fill }, () =>
            {
                _settings.RingAccent = color == Accents[0] ? null : color;
                PaintSwatches();
                RebuildPreview(animate: false);
                MarkDirty();
            })
            { Padding = new Thickness(3), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 3, 0), ToolTip = "Vurgu rengi" };
            _swatches.Add((swatch, color));
            colors.Children.Add(swatch);
        }
        bar.Children.Add(colors);

        _ringScale.Select(Math.Max(0, Array.FindIndex(Scales, x => Math.Abs(x - _settings.RingScale) < 0.01)));
        _ringScale.Changed += i =>
        {
            _settings.RingScale = Scales[i];
            RebuildPreview(animate: true);
            MarkDirty();
        };
        PaintSwatches();
        return bar;
    }

    private void PaintSwatches()
    {
        var current = _settings.RingStyle().Accent;
        PaintScrollBars(current);
        foreach (var (pill, color) in _swatches)
            pill.Paint(Brushes.Transparent, HoverBrush, color.Equals(current, StringComparison.OrdinalIgnoreCase) ? Palette.Text : Brushes.Transparent);
    }

    /// <summary>The scroll thumb takes the ring accent, softer at rest and full on hover or drag.</summary>
    private void PaintScrollBars(string accent)
    {
        var color = (Color)ColorConverter.ConvertFromString(accent);
        Resources["ScrollThumb"] = Frozen(new SolidColorBrush(Color.FromArgb(0x70, color.R, color.G, color.B)));
        Resources["ScrollThumbHot"] = Frozen(new SolidColorBrush(color));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>A slim rounded thumb with no arrows or track, in place of the stock Windows bar; both orientations.</summary>
    private static Style ScrollBarStyle() => (Style)System.Windows.Markup.XamlReader.Parse($"""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="10"/>
          <Setter Property="MinWidth" Value="10"/>
          <Setter Property="Template">
            <Setter.Value>{ScrollTemplate(vertical: true)}</Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="NaN"/>
              <Setter Property="MinWidth" Value="0"/>
              <Setter Property="Height" Value="10"/>
              <Setter Property="MinHeight" Value="10"/>
              <Setter Property="Template">
                <Setter.Value>{ScrollTemplate(vertical: false)}</Setter.Value>
              </Setter>
            </Trigger>
          </Style.Triggers>
        </Style>
        """);

    private static string ScrollTemplate(bool vertical) => $$"""
        <ControlTemplate TargetType="ScrollBar">
          <Track x:Name="PART_Track" IsDirectionReversed="{{vertical}}" Margin="{{(vertical ? "2,4" : "4,2")}}">
            <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.{{(vertical ? "PageUp" : "PageLeft")}}Command" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.{{(vertical ? "PageDown" : "PageRight")}}Command" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb {{(vertical ? "MinHeight" : "MinWidth")}}="28">
                <Thumb.Template>
                  <ControlTemplate TargetType="Thumb">
                    <Border x:Name="Bar" CornerRadius="3" Background="{DynamicResource ScrollThumb}"/>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bar" Property="Background" Value="{DynamicResource ScrollThumbHot}"/></Trigger>
                      <Trigger Property="IsDragging" Value="True"><Setter TargetName="Bar" Property="Background" Value="{DynamicResource ScrollThumbHot}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
        """;

    private FrameworkElement IconGrid()
    {
        var grid = new UniformGrid { Columns = 7 };
        foreach (var icon in IconChoices.Distinct())
        {
            var pill = new Pill(Sized(IconShape(icon, Palette.Text), 20, new Thickness(0)), () =>
            {
                _iconPopup.IsOpen = false;
                EditSlice(s => s.Icon = icon);
            })
            { Padding = new Thickness(8), Margin = new Thickness(2), CornerRadius = new CornerRadius(6) };
            pill.Paint(Brushes.Transparent, HoverBrush);
            grid.Children.Add(pill);
        }
        return new Border
        {
            Child = grid,
            Background = Palette.Raised,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 4, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35 },
        };
    }

    private FrameworkElement RingInspector()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Caption("Ad"));
        panel.Children.Add(_ringName);
        panel.Children.Add(Gap());
        panel.Children.Add(Caption("Nasıl açılır"));
        panel.Children.Add(_ringLayer);
        panel.Children.Add(Gap());
        panel.Children.Add(Caption("Nerede açılır"));
        panel.Children.Add(_ringWhere);

        Pill? pick = null;
        pick = TextButton("Seç ▾", () => PickRunningApp(pick!, name => _ringProcess.Text = name), Kind.Secondary);
        pick.Margin = new Thickness(8, 0, 0, 0);
        var processRow = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right);
        processRow.Children.Add(pick);
        processRow.Children.Add(_ringProcess);
        _matchPanel.Children.Add(Gap());
        _matchPanel.Children.Add(Caption("Uygulama"));
        _matchPanel.Children.Add(processRow);
        _matchPanel.Children.Add(Gap());
        _matchPanel.Children.Add(Caption("Pencere başlığında geçen"));
        _matchPanel.Children.Add(_ringTitle);
        _matchPanel.Children.Add(Small("Doluysa sadece başlığı buna uyan pencerelerde açılır (düzenli ifade)."));
        panel.Children.Add(_matchPanel);

        var reset = IconButton("M4 12a8 8 0 1 0 2.3-5.6 M4 4v4h4", "Varsayılana döndür", ResetRing, Kind.Ghost);
        var delete = IconButton(TrashIcon, "Halkayı sil", RemoveRing, Kind.Danger);
        var actions = new WrapPanel { Margin = new Thickness(-14, 16, 0, 0) };
        actions.Children.Add(reset);
        actions.Children.Add(delete);
        panel.Children.Add(actions);

        _ringName.Box.TextChanged += (_, _) => EditRing(r => r.Name = _ringName.Text.Trim().Length == 0 ? null : _ringName.Text);
        _ringLayer.Changed += i => EditRing(r => r.Layer = i == 1 ? RingLayers.Files : null);
        _ringWhere.Changed += i =>
        {
            _matchPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            EditRing(SetMatch);
            if (i == 1)
                _ringProcess.Box.Focus();
        };
        _ringProcess.Box.TextChanged += (_, _) => EditRing(SetMatch);
        _ringTitle.Box.TextChanged += (_, _) => EditRing(SetMatch);
        return Card("Halka", panel);
    }

    private FrameworkElement SliceInspector()
    {
        var back = IconOnly(LeftIcon, "Saat yönünün tersine taşı", () => MoveSlice(-1));
        var forward = IconOnly(RightIcon, "Saat yönünde taşı", () => MoveSlice(1));
        var delete = IconOnly(TrashIcon, "Dilimi sil", RemoveSlice);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(back);
        tools.Children.Add(forward);
        tools.Children.Add(delete);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(_sliceHeader);
        _sliceEditor.Children.Add(head);

        _sliceEditor.Children.Add(Caption("Ne yapsın"));
        var types = new WrapPanel();
        foreach (var (type, name, icon) in SliceTypes)
        {
            var shape = IconShape(icon, Palette.Muted);
            var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Sized(shape, 15, new Thickness(0, 0, 6, 0)));
            row.Children.Add(text);
            var chip = new Pill(row, () => EditSlice(s => ChangeSliceType(s, type))) { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
            _typeChips.Add((chip, text, shape));
            types.Children.Add(chip);
        }
        _sliceEditor.Children.Add(types);
        _sliceEditor.Children.Add(Gap());

        _toolPanel.Children.Add(Caption("Araç"));
        var grid = new UniformGrid { Columns = 3 };
        foreach (var tool in Tools)
        {
            var shape = IconShape(tool.Icon, Palette.Muted);
            var text = new TextBlock { Text = tool.Title, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
            var tile = new StackPanel();
            tile.Children.Add(Sized(shape, 22, new Thickness(0)));
            tile.Children.Add(text);
            var id = tool.Id;
            var pill = new Pill(tile, () => EditSlice(s => { s.Tool = id; s.Label = null; }))
            {
                Padding = new Thickness(4, 10, 4, 8),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1),
                ToolTip = tool.Hint,
            };
            _toolTiles.Add((pill, id, text, shape));
            grid.Children.Add(pill);
        }
        _toolPanel.Children.Add(grid);
        _toolPanel.Children.Add(Gap());
        _sliceEditor.Children.Add(_toolPanel);

        _sliceEditor.Children.Add(Caption("Dilimde yazan"));
        _sliceEditor.Children.Add(_sliceLabel);
        _sliceEditor.Children.Add(Gap());

        _sliceEditor.Children.Add(Caption("İkon"));
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal };
        _iconPreview.Background = Palette.Surface;
        _iconPreview.BorderBrush = Palette.Line;
        iconRow.Children.Add(_iconPreview);
        var change = TextButton("Değiştir ▾", () => _iconPopup.IsOpen = true, Kind.Secondary);
        change.VerticalAlignment = VerticalAlignment.Center;
        var reset = TextButton("Varsayılan", () => EditSlice(s => s.Icon = null), Kind.Ghost);
        reset.VerticalAlignment = VerticalAlignment.Center;
        iconRow.Children.Add(change);
        iconRow.Children.Add(reset);
        _iconPopup.PlacementTarget = change;
        _iconPopup.Child = IconGrid();
        _sliceEditor.Children.Add(iconRow);
        _sliceEditor.Children.Add(Gap());

        var valueRow = new DockPanel();
        _browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_browse, Dock.Right);
        valueRow.Children.Add(_browse);
        valueRow.Children.Add(_sliceValue);
        _valuePanel.Children.Add(_valueCaption);
        _valuePanel.Children.Add(valueRow);
        _valuePanel.Children.Add(_sliceChord);
        _sliceEditor.Children.Add(_valuePanel);
        _sliceEditor.Children.Add(_sliceProblem);

        _sliceLabel.Box.TextChanged += (_, _) => EditSlice(s => s.Label = _sliceLabel.Text.Length == 0 ? null : _sliceLabel.Text);
        _sliceValue.Box.TextChanged += (_, _) => EditSlice(SetActionValue);
        _sliceChord.Changed += keys => EditSlice(s => { if (s.Action is { } a) a.Keys = keys; });
        _sliceChord.Recording += on => { if (on) _hotkey?.Suspend(); else _hotkey?.Resume(); };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_sliceEditor);
        return Card("Dilim", panel);
    }

    private void RefreshRingCards()
    {
        _ringCards.Children.Clear();
        foreach (var (layer, title) in new[] { (RingLayers.Main, "Sağ tuş"), (RingLayers.Files, "Shift + sağ tuş") })
        {
            _ringCards.Children.Add(new TextBlock { Text = title, Foreground = Palette.Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 10, 0, 6) });
            var rings = _settings.Rings.Select((r, i) => (Ring: r, Index: i)).Where(x => LayerOf(x.Ring) == layer).OrderBy(x => x.Ring.Match is null ? 0 : 1).ToList();
            foreach (var (ring, index) in rings)
                _ringCards.Children.Add(RingCard(ring, index));
            if (rings.Count == 0)
                _ringCards.Children.Add(new TextBlock { Text = "Yerleşik halka kullanılıyor", Foreground = Palette.Muted, FontSize = 12, Margin = new Thickness(8, 0, 0, 6) });
        }
    }

    private FrameworkElement RingCard(RingConfig ring, int index)
    {
        var selected = index == _ringIndex;
        var icon = IconShape(ring.Match is null ? GlobeIcon : AppIcon, selected ? Palette.Accent : Palette.Muted);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = DisplayName(ring), FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{Where(ring)} · {ring.Slices.Count} dilim", Foreground = Palette.Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new DockPanel();
        var iconBox = Sized(icon, 18, new Thickness(0, 0, 12, 0));
        DockPanel.SetDock(iconBox, Dock.Left);
        row.Children.Add(iconBox);
        row.Children.Add(text);
        var card = new Pill(row, () => SelectRing(index)) { Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1) };
        card.Paint(selected ? AccentSoft : Brushes.Transparent, selected ? AccentSoft : HoverBrush, selected ? Palette.Accent : Brushes.Transparent);
        return card;
    }

    private static string DisplayName(RingConfig ring) => string.IsNullOrWhiteSpace(ring.Name) ? ring.Id : ring.Name;

    private static string Where(RingConfig ring) => ring.Match is { } m
        ? (string.IsNullOrWhiteSpace(m.Process) ? "Uygulama seçilmedi" : RingCatalog.ProcessName(m.Process)) + (string.IsNullOrEmpty(m.WindowTitle) ? "" : $" · \"{m.WindowTitle}\"")
        : "Her yerde";

    private void SelectRing(int index)
    {
        _ringIndex = Math.Min(index, _settings.Rings.Count - 1);
        RefreshRingCards();
        FillRing();
        RebuildPreview(animate: true);
        SelectSlice(Ring is { Slices.Count: > 0 } ? 0 : -1);
    }

    private void FillRing()
    {
        if (Ring is not { } ring)
        {
            _ringHeader.Text = "Halka yok";
            _ringSubheader.Text = "Soldan \"Yeni\" ile bir halka ekle.";
            return;
        }
        _loading = true;
        _ringName.Text = ring.Name ?? "";
        _ringLayer.Select(LayerOf(ring) == RingLayers.Files ? 1 : 0);
        _ringWhere.Select(ring.Match is null ? 0 : 1);
        _matchPanel.Visibility = ring.Match is null ? Visibility.Collapsed : Visibility.Visible;
        _ringProcess.Text = ring.Match?.Process ?? "";
        _ringTitle.Text = ring.Match?.WindowTitle ?? "";
        _loading = false;
        FillRingHeader();
    }

    private void FillRingHeader()
    {
        if (Ring is not { } ring)
            return;
        _ringHeader.Text = DisplayName(ring);
        var how = LayerOf(ring) == RingLayers.Files ? "Shift + sağ tuşu basılı tut" : "Sağ tuşu basılı tut";
        _ringSubheader.Text = ring.Match is null
            ? $"{how}. Başka halka eşleşmeyince her yerde bu açılır."
            : $"{how}. Sadece {Where(ring)} penceresinde açılır.";
        _sliceCount.Text = $"{ring.Slices.Count} / {RingCatalog.MaxSlices} dilim";
    }

    private void EditRing(Action<RingConfig> change)
    {
        if (_loading || Ring is not { } ring)
            return;
        change(ring);
        RefreshRingCards();
        FillRingHeader();
        MarkDirty();
    }

    private void SetMatch(RingConfig ring)
    {
        if (_ringWhere.Selected == 0)
        {
            ring.Match = null;
            return;
        }
        var title = _ringTitle.Text.Trim();
        ring.Match = new RingMatch { Process = _ringProcess.Text.Trim(), WindowTitle = title.Length == 0 ? null : title };
    }

    private void AddRing()
    {
        _settings.Rings.Add(new RingConfig
        {
            Id = NewId("halka"),
            Name = "Yeni halka",
            Match = new RingMatch { Process = "" },
            Slices = [new SliceConfig { Tool = "crop" }, new SliceConfig { Tool = "ocr" }, new SliceConfig { Tool = "lens" }, new SliceConfig { Tool = "pin" }],
        });
        SelectRing(_settings.Rings.Count - 1);
        MarkDirty();
        _ringProcess.Box.Focus();
    }

    private void RemoveRing()
    {
        if (Ring is not { } ring)
            return;
        if (MessageBox.Show(this, $"\"{DisplayName(ring)}\" halkası silinsin mi?", "Halkayı sil", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _settings.Rings.Remove(ring);
        SelectRing(Math.Max(0, _ringIndex - 1));
        MarkDirty();
        if (ring.Match is null)
            Say("Genel halkayı sildin: kaydedince bu katmanda yerleşik halka kullanılır.");
    }

    /// <summary>Puts back the built-in slices of the ring's layer.</summary>
    private void ResetRing()
    {
        if (Ring is not { } ring)
            return;
        if (MessageBox.Show(this, $"\"{DisplayName(ring)}\" halkasının dilimleri yerleşik dilimlerle değiştirilsin mi?", "Varsayılana döndür", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var defaults = LayerOf(ring) == RingLayers.Files ? DefaultFileRing.Items : DefaultRing.For(8);
        ring.Slices = defaults.Select(i => new SliceConfig { Tool = i.Id }).ToList();
        SelectRing(_ringIndex);
        MarkDirty();
    }

    private string NewId(string stem)
    {
        var id = stem;
        for (var n = 2; _settings.Rings.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); n++)
            id = $"{stem}-{n}";
        return id;
    }

    // ---- Önizleme ----

    private void RebuildPreview(bool animate)
    {
        _stageHost.Child = null;
        _preview?.Close();
        _preview = null;
        _previewRoot = null;
        if (Ring is not { } ring || ring.Slices.Count < RingCatalog.MinSlices)
        {
            _stageHost.Child = new TextBlock
            {
                Text = $"En az {RingCatalog.MinSlices} dilim ekle",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        var items = ring.Slices.Select(s => RingCatalog.ItemFor(s, out var problem)
            ?? new RingItem("invalid", string.IsNullOrEmpty(s.Label) ? "?" : s.Label, "Eksik bilgi", problem ?? "", WarnIcon)).ToList();
        var style = _settings.RingStyle();
        _stageHost.Width = _stageHost.Height = style.Size;
        _preview = new RingWindow(style, new GestureSettings(), items);
        _previewRoot = _preview.TakeForPreview(animate);
        _stageHost.Child = _previewRoot;
        _preview.Highlight(_slice);
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private int SliceUnder(MouseEventArgs e)
    {
        if (_previewRoot is null || Ring is not { } ring)
            return -1;
        var style = _settings.RingStyle();
        var p = e.GetPosition(_previewRoot);
        double dx = p.X - style.Size / 2, dy = p.Y - style.Size / 2;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        return distance < style.InnerRadius || distance > style.OuterRadius + 16 ? -1 : RingGeometry.SliceAt(dx, dy, ring.Slices.Count, 0);
    }

    // ---- Dilimler ----

    private void SelectSlice(int index)
    {
        _slice = index;
        _preview?.Highlight(index);
        FillSlice();
    }

    private void FillSlice()
    {
        _sliceEditor.Visibility = Slice is null ? Visibility.Collapsed : Visibility.Visible;
        if (Ring is not { } ring || Slice is not { } slice)
            return;
        _loading = true;
        var type = slice.Tool is not null ? "tool" : slice.Action?.Type ?? "launch";
        _sliceHeader.Text = $"{_slice + 1}. dilim";

        foreach (var (pill, text, icon) in _typeChips)
        {
            var on = SliceTypes[_typeChips.FindIndex(c => c.Pill == pill)].Type == type;
            PaintChoice(pill, text, icon, on);
        }
        foreach (var (pill, id, text, icon) in _toolTiles)
            PaintChoice(pill, text, icon, id == slice.Tool);

        var tool = Tools.FirstOrDefault(t => t.Id == slice.Tool);
        _sliceLabel.Placeholder = tool is not null ? $"{tool.Label} (varsayılan)" : "Zorunlu, örn. Yeni sekme";
        _sliceLabel.Text = slice.Label ?? "";
        _toolPanel.Visibility = type == "tool" ? Visibility.Visible : Visibility.Collapsed;
        _valuePanel.Visibility = type == "tool" ? Visibility.Collapsed : Visibility.Visible;
        _sliceChord.Visibility = type == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_sliceValue.Parent).Visibility = type == "hotkey" ? Visibility.Collapsed : Visibility.Visible;
        _browse.Visibility = type == "launch" ? Visibility.Visible : Visibility.Collapsed;
        _sliceValue.Multiline = type == "text";
        (_valueCaption.Text, _sliceValue.Placeholder) = type switch
        {
            "launch" => ("Açılacak uygulama, dosya ya da klasör", @"örn. notepad.exe ya da C:\Projeler"),
            "url" => ("Açılacak adres", "https://…"),
            "hotkey" => ("Gönderilecek kısayol", ""),
            _ => ("Yapıştırılacak metin", "Birden çok satır olabilir"),
        };
        _sliceValue.Text = type switch
        {
            "launch" => slice.Action?.Path,
            "url" => slice.Action?.Url,
            "text" => slice.Action?.Text,
            _ => null,
        } ?? "";
        _sliceChord.Set(slice.Action?.Keys ?? "");
        _loading = false;
        ShowSliceProblem(slice);
        FillRingHeader();
    }

    private void ShowSliceProblem(SliceConfig slice)
    {
        var item = RingCatalog.ItemFor(slice, out var problem);
        var icon = slice.Icon ?? item?.Icon ?? Tools.FirstOrDefault(t => t.Id == slice.Tool)?.Icon;
        _iconPreview.Child = slice.Icon is null && slice.Action is { Type: "launch" } && AppIcons.For(slice.Action.Path) is { } app
            ? new Image { Source = app, Width = 24, Height = 24 }
            : icon is null ? null : Sized(IconShape(icon, Palette.Text), 20, new Thickness(0));
        _sliceProblem.Text = problem is null ? "" : $"Bu dilim kaydedilemez: {problem}.";
        _sliceProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditSlice(Action<SliceConfig> change)
    {
        if (_loading || Slice is not { } slice)
            return;
        var before = slice.Tool ?? slice.Action?.Type;
        var tool = slice.Tool;
        change(slice);
        // A different kind or tool changes which fields show; text edits keep the caret where it is.
        if ((slice.Tool ?? slice.Action?.Type) != before || slice.Tool != tool)
            FillSlice();
        else
            ShowSliceProblem(slice);
        RefreshRingCards();
        SchedulePreview();
        MarkDirty();
    }

    private static void ChangeSliceType(SliceConfig slice, string type)
    {
        if (type == "tool")
        {
            if (slice.Tool is not null)
                return;
            slice.Action = null;
            slice.Label = null;
            slice.Tool = "crop";
        }
        else if (slice.Action?.Type != type)
        {
            slice.Tool = null;
            slice.Action = new SliceAction { Type = type };
        }
    }

    private void SetActionValue(SliceConfig slice)
    {
        if (slice.Action is not { } action)
            return;
        var value = _sliceValue.Text;
        switch (action.Type)
        {
            case "launch": action.Path = value; break;
            case "url": action.Url = value; break;
            case "text": action.Text = value; break;
        }
    }

    private void Browse()
    {
        // Starts in the Start menu's shortcuts, where installed apps are easiest to find; shortcuts are kept as-is.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Açılacak uygulama ya da dosya",
            Filter = "Uygulamalar|*.exe;*.lnk;*.bat;*.cmd|Tüm dosyalar|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog(this) == true)
            _sliceValue.Text = dialog.FileName;
    }

    private void AddSlice()
    {
        if (Ring is not { } ring)
            return;
        if (ring.Slices.Count >= RingCatalog.MaxSlices)
        {
            Say($"Bir halkada en fazla {RingCatalog.MaxSlices} dilim olabilir.", problem: true);
            return;
        }
        var unused = Tools.FirstOrDefault(t => ring.Slices.All(s => s.Tool != t.Id))?.Id ?? "crop";
        ring.Slices.Add(new SliceConfig { Tool = unused });
        RefreshRingCards();
        RebuildPreview(animate: false);
        SelectSlice(ring.Slices.Count - 1);
        MarkDirty();
    }

    private void RemoveSlice()
    {
        if (Ring is not { } ring || Slice is not { } slice)
            return;
        ring.Slices.Remove(slice);
        RefreshRingCards();
        RebuildPreview(animate: false);
        SelectSlice(Math.Min(_slice, ring.Slices.Count - 1));
        MarkDirty();
    }

    private void MoveSlice(int by)
    {
        if (Ring is not { } ring || Slice is not { } slice)
            return;
        // Wraps around: the ring has no ends.
        var to = (_slice + by + ring.Slices.Count) % ring.Slices.Count;
        ring.Slices.RemoveAt(_slice);
        ring.Slices.Insert(to, slice);
        RebuildPreview(animate: false);
        SelectSlice(to);
        MarkDirty();
    }

    // ---- Kaydet ----

    private bool Save()
    {
        var chord = _ringHotkey.Chord;
        if (!KeyChord.TryParse(chord, out var keys) || keys.Length < 2)
        {
            ShowPage(0);
            Say("Klavye kısayolu en az bir değiştirici tuş (Ctrl, Alt, Shift, Win) ve bir tuş olmalı.", problem: true);
            return false;
        }

        // The same checks the app runs on load, so a bad ring is caught here instead of being skipped.
        var check = new RingCatalog(_settings.Rings, 8);
        if (check.Warnings.Count > 0)
        {
            ShowPage(1);
            Say(string.Join("\n", check.Warnings.Take(3)), problem: true);
            return false;
        }

        _settings.RingHotkey = chord;
        _settings.Theme = ThemeIds[_theme.Selected];
        _settings.ExcludedProcesses = [.. _excluded];
        // A language the window does not offer (set by hand) stays until another one is picked.
        if (Array.FindIndex(Languages, l => l.Equals(_settings.TranslateTarget, StringComparison.OrdinalIgnoreCase)) is var known && (known >= 0 || _translateTarget.Selected != 0))
            _settings.TranslateTarget = Languages[_translateTarget.Selected];
        _settings.DeepLApiKey = _deepLKey.Text.Trim() is { Length: > 0 } key ? key : null;
        try
        {
            if (_startup.IsOn != StartupRegistration.IsEnabled())
                StartupRegistration.SetEnabled(_startup.IsOn);
            _settings.Save(AppPaths.ConfigFile);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Say($"Kaydedilemedi: {e.Message}", problem: true);
            return false;
        }
        // The watcher reloads config.json and shows "Ayarlar yüklendi".
        _dirty = false;
        Say("Kaydedildi.");
        return true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty)
            return;
        var answer = MessageBox.Show(this, "Değişiklikler kaydedilsin mi?", "RadialShortcut Ayarları", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !Save())
            e.Cancel = true;
    }

    private void DarkTitleBar()
    {
        if (!Palette.IsDark)
            return;
        var on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- Parçalar ----

    private static Brush Rgb(int rgb, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private static Brush SidebarBrush => Palette.IsDark ? Rgb(0x1E2125) : Rgb(0xEEEFF1);
    private static Brush HoverBrush => Palette.IsDark ? Rgb(0x3A3F47) : Rgb(0xE4E6EA);
    private static Brush AccentSoft => Palette.IsDark ? Rgb(0x3D8BFF, 0x30) : Rgb(0x0067C0, 0x1C);
    private static Brush Danger => Rgb(0xE5534B);

    private enum Kind { Primary, Secondary, Ghost, Danger }

    private static void PaintChoice(Pill pill, TextBlock text, Shape icon, bool on)
    {
        pill.Paint(on ? AccentSoft : Brushes.Transparent, on ? AccentSoft : HoverBrush, on ? Palette.Accent : Palette.Line);
        text.Foreground = on ? Palette.Text : Palette.Muted;
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        icon.Stroke = on ? Palette.Accent : Palette.Muted;
    }

    private static Pill TextButton(string label, Action onClick, Kind kind) =>
        Styled(new Pill(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, onClick), kind);

    private static Pill IconButton(string icon, string label, Action onClick, Kind kind)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Sized(IconShape(icon, kind == Kind.Danger ? Danger : kind == Kind.Primary ? Brushes.White : Palette.Text), 15, new Thickness(0, 0, 7, 0)));
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return Styled(new Pill(row, onClick), kind);
    }

    private static Pill IconOnly(string icon, string tip, Action onClick)
    {
        var pill = new Pill(Sized(IconShape(icon, Palette.Text), 16, new Thickness(0)), onClick) { Padding = new Thickness(7), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, BorderThickness = new Thickness(1) };
        pill.Paint(Palette.Raised, HoverBrush, Palette.Line);
        return pill;
    }

    private static Pill Styled(Pill pill, Kind kind)
    {
        pill.BorderThickness = new Thickness(1);
        switch (kind)
        {
            case Kind.Primary:
                pill.Paint(Palette.Accent, Rgb(0x1A7BD6), Palette.Accent);
                LabelOf(pill).Foreground = Brushes.White;
                LabelOf(pill).FontWeight = FontWeights.SemiBold;
                pill.Padding = new Thickness(22, 8, 22, 8);
                break;
            case Kind.Secondary:
                pill.Paint(Palette.Raised, HoverBrush, Palette.Line);
                break;
            case Kind.Ghost:
                pill.Paint(Brushes.Transparent, HoverBrush, Brushes.Transparent);
                LabelOf(pill).Foreground = Palette.Muted;
                break;
            case Kind.Danger:
                pill.Paint(Brushes.Transparent, Rgb(0xE5534B, 0x22), Brushes.Transparent);
                LabelOf(pill).Foreground = Danger;
                break;
        }
        return pill;
    }

    private static TextBlock LabelOf(Pill pill) => pill.Child switch
    {
        TextBlock t => t,
        Panel p => p.Children.OfType<TextBlock>().First(),
        _ => throw new InvalidOperationException(),
    };

    private static Shape IconShape(string data, Brush stroke) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke,
        StrokeThickness = 1.8,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 24,
        Height = 24,
    };

    private static Viewbox Sized(UIElement child, double size, Thickness margin) =>
        new() { Child = child, Width = size, Height = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };

    private static FrameworkElement PageTitle(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Palette.Muted, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    private static FrameworkElement Section(string title, params UIElement[] rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(2, 0, 0, 8) });
        var list = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                list.Children.Add(new Border { Height = 1, Background = Palette.Line });
            list.Children.Add(rows[i]);
        }
        panel.Children.Add(new Border { Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = list });
        return panel;
    }

    private static FrameworkElement Card(string title, UIElement body)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Palette.Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 12, 16, 0) });
        panel.Children.Add(body);
        return new Border { Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 14), Child = panel };
    }

    private static FrameworkElement Row(string title, string description, UIElement control, UIElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (below is not null)
            text.Children.Add(below);
        var grid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (control is FrameworkElement f)
            f.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement Keycaps(params string[] keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var key in keys)
            row.Children.Add(Keycap(key));
        return row;
    }

    private static Border Keycap(string key) => new()
    {
        Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold },
        Background = SidebarBrush,
        BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 0, 5, 0),
    };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    private static TextBlock Small(string text) => new() { Text = text, Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private static Border Gap() => new() { Height = 14 };

    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    /// <summary>A flat, rounded clickable surface; WPF's own button chrome does not follow the dark theme.</summary>
    private sealed class Pill : Border
    {
        private Brush _normal = Brushes.Transparent, _hover = Brushes.Transparent;

        public Pill(UIElement content, Action onClick)
        {
            Child = content;
            Cursor = Cursors.Hand;
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(14, 7, 14, 7);
            Background = _normal;
            MouseEnter += (_, _) => Background = _hover;
            MouseLeave += (_, _) => Background = _normal;
            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }

        public void Paint(Brush normal, Brush hover, Brush? border = null)
        {
            _normal = normal;
            _hover = hover;
            Background = IsMouseOver ? hover : normal;
            if (border is not null)
                BorderBrush = border;
        }
    }

    /// <summary>A row of options, one chosen.</summary>
    private sealed class Segmented : Border
    {
        private readonly List<(Pill Pill, TextBlock Text)> _options = [];

        public int Selected { get; private set; }

        /// <summary>Raised only when the user picks an option.</summary>
        public event Action<int>? Changed;

        public Segmented(params string[] labels)
        {
            Background = SidebarBrush;
            BorderBrush = Palette.Line;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(3);
            HorizontalAlignment = HorizontalAlignment.Left;
            var row = new UniformGrid { Rows = 1 };
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var text = new TextBlock { Text = labels[i], HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
                var pill = new Pill(text, () =>
                {
                    if (Selected == index)
                        return;
                    Select(index);
                    Changed?.Invoke(index);
                })
                { Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
                _options.Add((pill, text));
                row.Children.Add(pill);
            }
            Child = row;
            Select(0);
        }

        public void Select(int index)
        {
            Selected = index;
            for (var i = 0; i < _options.Count; i++)
            {
                var on = i == index;
                _options[i].Pill.Paint(on ? Palette.Raised : Brushes.Transparent, on ? Palette.Raised : HoverBrush, on ? Palette.Line : Brushes.Transparent);
                _options[i].Text.Foreground = on ? Palette.Text : Palette.Muted;
                _options[i].Text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    /// <summary>An on/off switch.</summary>
    private sealed class Toggle : Border
    {
        private readonly Border _knob = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(3) };

        public bool IsOn { get; private set; }

        /// <summary>Raised only when the user flips it.</summary>
        public event Action<bool>? Changed;

        public Toggle()
        {
            Width = 44;
            Height = 24;
            CornerRadius = new CornerRadius(12);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            Child = _knob;
            MouseLeftButtonUp += (_, _) =>
            {
                Set(!IsOn);
                Changed?.Invoke(IsOn);
            };
            Set(false);
        }

        public void Set(bool on)
        {
            IsOn = on;
            Background = on ? Palette.Accent : SidebarBrush;
            BorderBrush = on ? Palette.Accent : Palette.Muted;
            _knob.Background = on ? Brushes.White : Palette.Muted;
            _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    /// <summary>A rounded text box with a placeholder.</summary>
    private sealed class Input : Border
    {
        private readonly TextBlock _hint = new() { Foreground = Palette.Muted, Margin = new Thickness(11, 7, 10, 7), IsHitTestVisible = false, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };

        public TextBox Box { get; } = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Palette.Text,
            CaretBrush = Palette.Text,
            SelectionBrush = Palette.Accent,
            Padding = new Thickness(8, 7, 8, 7),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        public Input(string placeholder)
        {
            Placeholder = placeholder;
            Background = Palette.Surface;
            BorderBrush = Palette.Line;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(7);
            var grid = new Grid();
            grid.Children.Add(_hint);
            grid.Children.Add(Box);
            Child = grid;
            Box.TextChanged += (_, _) => _hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Box.GotKeyboardFocus += (_, _) => BorderBrush = Palette.Accent;
            Box.LostKeyboardFocus += (_, _) => BorderBrush = Palette.Line;
        }

        public string Text
        {
            get => Box.Text;
            set => Box.Text = value;
        }

        public string Placeholder
        {
            get => _hint.Text;
            set => _hint.Text = value;
        }

        public bool Multiline
        {
            set
            {
                Box.AcceptsReturn = value;
                Box.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
                Box.MinHeight = value ? 80 : 0;
                Box.VerticalContentAlignment = value ? VerticalAlignment.Top : VerticalAlignment.Center;
                _hint.VerticalAlignment = value ? VerticalAlignment.Top : VerticalAlignment.Center;
            }
        }
    }

    /// <summary>Shows a shortcut as keycaps; click it and press keys to record a new one.</summary>
    private sealed class ChordBox : Border
    {
        private readonly WrapPanel _keys = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly int _minKeys;
        private bool _recording;
        private string? _note;

        public string Chord { get; private set; } = "";

        /// <summary>Raised only when the user records or clears a shortcut.</summary>
        public event Action<string>? Changed;

        /// <summary>True when recording starts, false when it ends.</summary>
        public event Action<bool>? Recording;

        public ChordBox(int minKeys)
        {
            _minKeys = minKeys;
            Focusable = true;
            Cursor = Cursors.Hand;
            CornerRadius = new CornerRadius(7);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(8, 6, 8, 6);
            MinWidth = 200;
            MinHeight = 36;
            Background = Palette.Surface;
            BorderBrush = Palette.Line;
            Child = _keys;
            ToolTip = "Tıkla ve tuşlara bas. Backspace siler, Esc vazgeçer.";
            MouseLeftButtonDown += (_, e) =>
            {
                Focus();
                Start();
                e.Handled = true;
            };
            LostKeyboardFocus += (_, _) => Stop();
            PreviewKeyDown += OnKey;
            Show();
        }

        public void Set(string chord)
        {
            Chord = chord;
            Show();
        }

        private void Start()
        {
            if (_recording)
                return;
            _recording = true;
            _note = null;
            BorderBrush = Palette.Accent;
            Recording?.Invoke(true);
            Show();
        }

        private void Stop()
        {
            if (!_recording)
                return;
            _recording = false;
            BorderBrush = Palette.Line;
            Recording?.Invoke(false);
            Show();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (!_recording)
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    Start();
                    e.Handled = true;
                }
                return;
            }
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            if (key == Key.Escape && mods == ModifierKeys.None)
            {
                Stop();
                return;
            }
            if (key is Key.Back or Key.Delete && mods == ModifierKeys.None)
            {
                Chord = "";
                Changed?.Invoke(Chord);
                Stop();
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
            if (KeyName(key) is not { } name)
                return;

            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(name);
            if (parts.Count < _minKeys)
            {
                _note = "Ctrl, Alt, Shift ya da Win ile birlikte bas";
                Show();
                return;
            }
            Chord = string.Join("+", parts);
            Changed?.Invoke(Chord);
            Stop();
        }

        private void Show()
        {
            _keys.Children.Clear();
            if (_recording)
            {
                _keys.Children.Add(new TextBlock { Text = _note ?? "Tuşlara bas…", Foreground = _note is null ? Palette.Accent : Danger, VerticalAlignment = VerticalAlignment.Center });
                return;
            }
            if (Chord.Length == 0)
            {
                _keys.Children.Add(new TextBlock { Text = "Tıkla ve kısayola bas", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center });
                return;
            }
            foreach (var part in Chord.Split('+'))
                _keys.Children.Add(Keycap(part == "Space" ? "Boşluk" : part));
        }

        /// <summary>The <see cref="KeyChord"/> name of a key, or null for keys it does not know.</summary>
        private static string? KeyName(Key key) => key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Escape => "Esc", Key.Back => "Backspace",
            Key.Delete => "Delete", Key.Insert => "Insert", Key.Home => "Home", Key.End => "End",
            Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Left => "Left", Key.Up => "Up", Key.Right => "Right", Key.Down => "Down",
            Key.PrintScreen => "PrintScreen", Key.OemPlus => "Plus", Key.OemMinus => "Minus", Key.OemComma => "Comma", Key.OemPeriod => "Period",
            Key.MediaPlayPause => "PlayPause", Key.MediaNextTrack => "NextTrack", Key.MediaPreviousTrack => "PrevTrack", Key.MediaStop => "Stop",
            Key.VolumeUp => "VolumeUp", Key.VolumeDown => "VolumeDown", Key.VolumeMute => "Mute",
            _ => null,
        };
    }
}
