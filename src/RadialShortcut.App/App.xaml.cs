using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Microsoft.Win32;
using RadialShortcut.App.Tools;
using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.App;

public partial class App : Application
{
    private const string MutexName = @"Local\RadialShortcut.SingleInstance";

    private Mutex? _singleInstance;
    private TaskbarIcon? _tray;
    private MouseHookService? _hook;
    private DebugOverlay? _debug;
    private GestureSettings _settings = new();
    private RingStyle _ringStyle = new();
    private RingCatalog _catalog = new([], 8);
    private readonly Dictionary<string, RingWindow> _rings = [];
    private RingWindow? _ring; // The ring of the current gesture.
    private bool _darkRings; // The theme the ring windows were built with.
    private bool _welcomeShown; // The welcome notice is up; clicking it opens the help page.
    private IntPtr _ringTarget; // The window under the gesture, kept for a switch to the file ring.
    private string _ringLayer = RingLayers.Main;
    private FileSystemWatcher? _configWatcher;
    private DispatcherTimer? _reloadTimer;
    private HotkeyRing? _hotkey;
    private const string DefaultUpdateRepo = "arrdakaya/RadialShortcut";
    private string? _updateRepo;
    private Updater.Release? _update;
    private bool _installing;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _layerTimer;
    private ScreenPoint _ringOrigin, _ringPoint;
    private string _baseLayer = RingLayers.Main;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--snapshot", var folder])
        {
            // Draws the README images and exits; no tray, hook or single-instance lock.
            base.OnStartup(e);
            Dispatcher.InvokeAsync(async () =>
            {
                await Snapshots.WriteAsync(folder);
                Shutdown();
            });
            return;
        }

#if DEBUG
        if (e.Args is ["--settings-shot", var shots])
        {
            base.OnStartup(e);
            Dispatcher.InvokeAsync(async () =>
            {
                await SettingsWindow.ShotAsync(shots);
                Shutdown();
            });
            return;
        }
#endif

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is already running in the tray.
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppPaths.EnsureCreated();
        MemoryTrim.Install();

        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();

        // The debug panel is for development builds only; released builds drop it and its menu item.
        var debugItem = _tray.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == "DebugItem");
#if DEBUG
        _debug = new DebugOverlay(_settings);
        _debug.Show();
        debugItem.IsChecked = true;
#else
        debugItem.Visibility = Visibility.Collapsed;
#endif

        _hook = new MouseHookService(_settings);
        _hook.Event += ev => Dispatcher.BeginInvoke(() => OnGesture(ev));
        _hook.LeftPressed += at => Dispatcher.BeginInvoke(() => FileDrag.Pressed(at));
        _hook.DragStarted += start => Dispatcher.BeginInvoke(() => FileDrag.Started(start, _debug is null ? null : _debug.Note));
        _hook.DragEnded += () => Dispatcher.BeginInvoke(FileDrag.Ended);
        _hotkey = new HotkeyRing(OpenFromHotkey, Release);
        LoadConfig(startup: true);
        StartUpdateChecks();
        _hook.Start();
        WatchConfig();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            if (_welcomeShown)
                OpenSettings(page: 2);
        };
        _tray.TrayBalloonTipClosed += (_, _) => _welcomeShown = false;
        // One notification per start: the welcome, the update result, or just that it is running.
        if (!Welcome() && !SayUpdated())
            _tray?.ShowNotification("RadialShortcut başladı", "Tepside çalışıyor. Sağ tuşu basılı tutup çek: halka açılır.");
        Dispatcher.BeginInvoke(SelectedFiles.WarmUp, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>The first start after an update says it went through; the file was left by the old version.</summary>
    private bool SayUpdated()
    {
        try
        {
            if (!File.Exists(AppPaths.UpdatedFile))
                return false;
            var version = File.ReadAllText(AppPaths.UpdatedFile).Trim();
            File.Delete(AppPaths.UpdatedFile);
            // When the swap failed the old version starts again; then there is nothing to celebrate.
            if (Version.TryParse(version, out var v) && v <= Updater.Current)
                _tray?.ShowNotification("Güncellendi", $"RadialShortcut {Updater.Current.ToString(3)} kuruldu.");
            else
                _tray?.ShowNotification("Güncellenemedi", "Yeni sürüm kurulamadı, önceki sürüm açıldı. Tepsi menüsünden tekrar deneyebilirsin.");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>First launch: the app lives in the tray, so say how to reach it once.</summary>
    private bool Welcome()
    {
        if (File.Exists(AppPaths.WelcomedFile))
            return false;
        _tray?.ShowNotification("RadialShortcut çalışıyor",
            "Sağ tuşu basılı tutup fareyi bir yöne çek: halka açılır, bırakınca seçilir.\n"
            + "Explorer'da dosya seçiliyken dosya halkası açılır. Dosyayı sürüklerken bir an bekletirsen araçlar çıkar.\n"
            + "Tıkla: nasıl kullanılır. Tepsi simgesi: ayarlar, sağ tık: menü.");
        _welcomeShown = true;
        try { File.WriteAllText(AppPaths.WelcomedFile, ""); } catch (IOException) { }
        return true;
    }

    /// <summary>
    /// Reads config.json and rebuilds the rings. A file that does not parse leaves the last good
    /// rings in place (PLAN.md, section 9); either way problems are shown in a tray notice.
    /// </summary>
    private void LoadConfig(bool startup = false)
    {
        WriteExampleConfig();
        if (!UserSettings.TryLoad(AppPaths.ConfigFile, out var config, out var error))
        {
            _tray?.ShowNotification("config.json okunamadı", $"{error}\nSon geçerli ayarlar kullanılıyor.");
            return;
        }

        Palette.Theme = config.Theme;
        _updateRepo = string.IsNullOrWhiteSpace(config.UpdateRepo) ? DefaultUpdateRepo : config.UpdateRepo.Trim();
        var hotkeyProblem = _hotkey is not null && !_hotkey.Register(config.RingHotkey)
            ? $"Klavye kısayolu \"{config.RingHotkey}\" kullanılamıyor (geçersiz ya da başka bir uygulamada)."
            : null;
        _hook?.SetExcludedProcesses(config.ExcludedProcesses?.Select(RingCatalog.ProcessName) ?? _settings.ExcludedProcesses);

        _ringStyle = config.RingStyle();
        _catalog = new RingCatalog(config.Rings, _settings.SliceCount);
        RebuildRings();

        if (_catalog.Warnings.Count > 0)
            _tray?.ShowNotification("config.json: bazı halkalar atlandı", string.Join("\n", _catalog.Warnings.Take(4)));
        else if (hotkeyProblem is not null)
            _tray?.ShowNotification("config.json", hotkeyProblem);
        else if (!startup)
            _tray?.ShowNotification("Ayarlar yüklendi", $"{_catalog.All.Count()} halka hazır.");
    }

    /// <summary>Drops every ring window; they take their colors when built.</summary>
    private void RebuildRings()
    {
        _darkRings = Palette.IsDark;
        foreach (var window in _rings.Values)
            window.Close();
        _rings.Clear();
        _ring = null; // Closed with the rest; the next gesture picks a fresh one.
        // The global rings open most often, so they are built ahead of time; per-app rings on first use.
        foreach (var ring in _catalog.Globals)
            WindowFor(ring);
    }

    /// <summary>With the "system" theme the rings follow Windows switching between light and dark.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() =>
            {
                if (Palette.IsDark != _darkRings)
                    RebuildRings();
            });
    }

    private RingWindow WindowFor(Ring ring)
    {
        if (!_rings.TryGetValue(ring.Id, out var window))
        {
            window = new RingWindow(_ringStyle, _settings, ring.Items);
            window.Show();
            _rings[ring.Id] = window;
        }
        return window;
    }

    /// <summary>Hand edits apply right away; saves come in bursts, so wait for them to settle.</summary>
    private void WatchConfig()
    {
        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            LoadConfig();
        };

        _configWatcher = new FileSystemWatcher(AppPaths.ConfigDirectory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler restart = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
        _configWatcher.Changed += restart;
        _configWatcher.Created += restart;
        _configWatcher.Renamed += (s, e) => restart(s, e);
    }

    /// <summary>A reference for editing config.json by hand; rewritten on every start.</summary>
    private static void WriteExampleConfig()
    {
        try
        {
            var example = Path.Combine(AppPaths.ConfigDirectory, "config.example.json");
            using var stream = typeof(App).Assembly.GetManifestResourceStream("RadialShortcut.App.config.example.json")!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (!File.Exists(example) || File.ReadAllText(example) != text)
                File.WriteAllText(example, text);
        }
        catch (IOException)
        {
        }
    }

    private void OnGesture(GestureEvent ev)
    {
        _debug?.Show(ev);
        if (ev.Kind == GestureEventKind.RingOpened)
        {
            // The ring follows the window that was right-clicked, not the one with focus (PLAN.md, section 8).
            var target = Native.WindowAt(ev.Origin.X, ev.Origin.Y);
            // Ctrl opens the app ring; Shift picks the other of main and files: over files selected in
            // Explorer the file ring is the default. Checked here, on the UI thread, so the hook callback stays fast.
            var files = SelectedFiles.IsExplorer(target) && SelectedFiles.HasSelection(target);
            _baseLayer = files ? RingLayers.Files : RingLayers.Main;
            _ringLayer = Native.IsControlDown() ? RingLayers.Apps
                : files != Native.IsShiftDown() ? RingLayers.Files : RingLayers.Main;
            _ringTarget = target;
            _ring = RingFor(target, _ringLayer);
        }
        if (_ring is null)
            return;

        switch (ev.Kind)
        {
            case GestureEventKind.RingOpened:
                _ring.Open(ev.Origin, IdleHint(_ringLayer));
                _ringOrigin = _ringPoint = ev.Origin;
                WatchLayerKeys(true);
                break;
            case GestureEventKind.RingMoved:
                _ringPoint = ev.Point;
                SwitchLayerIfPressed();
                _ring.Track(ev.Point);
                break;
            case GestureEventKind.RingReleased:
                WatchLayerKeys(false);
                Release(_ring, _ring.SliceAt(ev.Point), ev.Origin);
                break;
            case GestureEventKind.RingCancelled:
                WatchLayerKeys(false);
                _ring.Close(-1);
                break;
        }
    }

    /// <summary>While the ring is open, Ctrl or Shift switch rings as soon as they are pressed, without waiting for the mouse to move.</summary>
    private void WatchLayerKeys(bool on)
    {
        if (!on)
        {
            _layerTimer?.Stop();
            return;
        }
        if (_layerTimer is null)
        {
            _layerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _layerTimer.Tick += (_, _) => SwitchLayerIfPressed();
        }
        _layerTimer.Start();
    }

    /// <summary>Ctrl or Shift switch rings at the same spot while held; letting go returns to the default ring.</summary>
    private void SwitchLayerIfPressed()
    {
        var layer = HeldLayer();
        if (_ring is null || layer == _ringLayer)
            return;
        _ringLayer = layer;
        var next = RingFor(_ringTarget, layer);
        if (next == _ring)
            return;
        _ring.Close(-1);
        _ring = next;
        _ring.Open(_ringOrigin, IdleHint(layer));
        _ring.Track(_ringPoint);
    }

    /// <summary>The ring the held modifiers ask for: Ctrl the app ring, Shift the other of main and files, none the default.</summary>
    private string HeldLayer()
    {
        if (Native.IsControlDown())
            return RingLayers.Apps;
        if (Native.IsShiftDown())
            return _baseLayer == RingLayers.Main ? RingLayers.Files : RingLayers.Main;
        return _baseLayer;
    }

    /// <summary>Under the title while nothing is hovered: the rings still reachable from this one.</summary>
    private static string? IdleHint(string layer) => layer switch
    {
        RingLayers.Main => "Shift: dosya\nCtrl: uygulama",
        RingLayers.Files => "Ctrl: uygulama",
        _ => null,
    };

    private RingWindow RingFor(IntPtr target, string layer)
    {
        var ring = _catalog.For(Native.ProcessName(target), Native.WindowTitle(target), layer);
        return WindowFor(ring);
    }

    private RingWindow OpenFromHotkey(string layer, ScreenPoint at)
    {
        var ring = RingFor(Native.WindowAt(at.X, at.Y), layer);
        ring.Open(at);
        return ring;
    }

    private static void Release(RingWindow ring, int slice, ScreenPoint origin)
    {
        ring.Close(slice);
        if (slice >= 0)
            _ = RunAfterRingFadesAsync(ring.ItemAt(slice), origin);
    }

    /// <summary>Half a minute after start, then once a day.</summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            await CheckForUpdateAsync();
        };
        _updateTimer.Start();
    }

    /// <summary>True when a newer release is waiting behind the "Güncelle" item.</summary>
    private async Task<bool> CheckForUpdateAsync()
    {
        if (_update is not null)
            return true;
        if (_updateRepo is null || await Updater.CheckAsync(_updateRepo) is not { } release)
            return false;
        _update = release;
        var item = TrayItem("UpdateItem");
        item.Header = $"Güncelle (v{release.Version})";
        item.Visibility = Visibility.Visible;
        _tray?.ShowNotification("Güncelleme var", $"RadialShortcut {release.Version} hazır. Tepsi menüsünden \"Güncelle\" ile kurabilirsin.");
        return true;
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        item.IsEnabled = false;
        item.Header = "Denetleniyor…";
        try
        {
            if (!await CheckForUpdateAsync())
                _tray?.ShowNotification("Güncelleme yok", $"RadialShortcut {Updater.Current.ToString(3)} en son sürüm (ya da GitHub'a ulaşılamadı).");
        }
        finally
        {
            item.Header = "Güncellemeleri denetle";
            item.IsEnabled = true;
        }
    }

    private MenuItem TrayItem(string name) => _tray!.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == name);

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update is null)
            return;
        var item = (MenuItem)sender;
        item.IsEnabled = false;
        if (await InstallUpdateAsync(text => item.Header = text) is { } error)
        {
            _tray?.ShowNotification("Güncellenemedi", error);
            item.Header = $"Güncelle (v{_update.Version})";
            item.IsEnabled = true;
        }
    }

    /// <summary>Installs the waiting release, from the tray or the settings window; null when it went through.</summary>
    private async Task<string?> InstallUpdateAsync(Action<string> status)
    {
        // The tray and the settings window share one download folder, so only one install runs.
        if (_installing)
            return "Güncelleme zaten kuruluyor.";
        _installing = true;
        UpdateToast? toast = null;
        try
        {
            if (_update is null && !await CheckForUpdateAsync())
                return "Kurulacak yeni sürüm bulunamadı.";
            toast = new UpdateToast(_update!.Version);
            toast.Show();
            await Updater.InstallAsync(_update, text =>
            {
                status(text);
                toast.Status(text);
            }, toast.Progress);
            toast = null; // the app is shutting down and takes the card with it
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            // Any failure, caught here or not, must not leave "installing" on screen.
            toast?.Close();
            _installing = false;
        }
    }

    private void OnLibraryClick(object sender, RoutedEventArgs e) => LibraryWindow.ShowOrActivate();

    /// <summary>Screen tools freeze the screen first, so the ring has to be gone before they start.</summary>
    private static async Task RunAfterRingFadesAsync(RingItem item, ScreenPoint origin)
    {
        await Task.Delay(200);
        await ToolRunner.RunAsync(item, origin);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _configWatcher?.Dispose();
        _hotkey?.Dispose();
        _hook?.Dispose();
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        var paused = ((MenuItem)sender).IsChecked;
        if (_hook is not null)
            _hook.Paused = paused;
        if (_tray is not null)
            _tray.ToolTipText = paused ? "RadialShortcut (duraklatıldı)" : "RadialShortcut";
    }

    private void OnDebugClick(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).IsChecked)
            _debug?.Show();
        else
            _debug?.Hide();
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings(int? page = null) => SettingsWindow.ShowOrActivate(_hotkey, CheckForUpdateAsync, InstallUpdateAsync, page);

    private void OnExitClick(object sender, RoutedEventArgs e) => Shutdown();
}
