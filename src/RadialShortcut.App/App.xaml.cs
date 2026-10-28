using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
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
    private RingCatalog _catalog = new([], 8);
    private readonly Dictionary<string, RingWindow> _rings = [];
    private RingWindow? _ring; // The ring of the current gesture.
    private FileSystemWatcher? _configWatcher;
    private DispatcherTimer? _reloadTimer;
    private HotkeyRing? _hotkey;
    private const string DefaultUpdateRepo = "arrdakaya/RadialShortcut";
    private string? _updateRepo;
    private Updater.Release? _update;
    private DispatcherTimer? _updateTimer;

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

        var startupItem = (MenuItem)_tray.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == "StartupItem");
        startupItem.IsChecked = StartupRegistration.IsEnabled();

        _debug = new DebugOverlay(_settings);
        _debug.Show();

        _hook = new MouseHookService(_settings);
        _hook.Event += ev => Dispatcher.BeginInvoke(() => OnGesture(ev));
        _hook.ShiftDragStarted += start => Dispatcher.BeginInvoke(() =>
        {
            // Only drags that start in Explorer or on the desktop can be carrying files. Matched by
            // process: the desktop's window class differs between Windows builds.
            var source = Native.WindowAt(start.X, start.Y);
            var process = Native.ProcessName(source);
            var fromShell = process.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
            _debug?.Note($"Shift-sürükleme: {process} / {Native.ClassName(source)} → {(fromShell ? "alan açıldı" : "yok sayıldı")}");
            DragHint.Dismiss();
            if (fromShell)
                DropZones.ShowAt(start);
        });
        _hook.PlainDragStarted += at => Dispatcher.BeginInvoke(() =>
        {
            // Only file drags out of Explorer or the desktop, not every window move or text selection.
            var source = Native.WindowAt(at.X, at.Y);
            if (SelectedFiles.IsExplorer(source) || SelectedFiles.IsDesktop(source)
                || Native.ProcessName(source).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                DragHint.ShowAt(at);
        });
        _hook.DragEnded += () => Dispatcher.BeginInvoke(() =>
        {
            DragHint.Dismiss();
            DropZones.EndDrag();
        });
        _hotkey = new HotkeyRing(OpenFromHotkey, Release);
        LoadConfig(startup: true);
        StartUpdateChecks();
        _hook.Start();
        WatchConfig();
        Welcome();
        Dispatcher.BeginInvoke(SelectedFiles.WarmUp, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>First launch: the app lives in the tray, so say how to reach it once.</summary>
    private void Welcome()
    {
        if (File.Exists(AppPaths.WelcomedFile))
            return;
        _tray?.ShowNotification("RadialShortcut çalışıyor",
            "Sağ tuşu basılı tutup fareyi bir yöne çek: halka açılır, bırakınca seçilir.\n"
            + "Explorer'da dosya seçiliyken dosya halkası açılır. Dosyayı sürüklerken Shift'e basarsan araçlar çıkar.\n"
            + "Duraklatma ve ayarlar tepsi simgesinde.");
        try { File.WriteAllText(AppPaths.WelcomedFile, ""); } catch (IOException) { }
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

        _catalog = new RingCatalog(config.Rings, _settings.SliceCount);
        foreach (var window in _rings.Values)
            window.Close();
        _rings.Clear();
        // Built ahead of time so the first open of a ring is as quick as the rest.
        foreach (var ring in _catalog.All)
        {
            var window = new RingWindow(new RingStyle(), _settings, ring.Items);
            window.Show();
            _rings[ring.Id] = window;
        }

        if (_catalog.Warnings.Count > 0)
            _tray?.ShowNotification("config.json: bazı halkalar atlandı", string.Join("\n", _catalog.Warnings.Take(4)));
        else if (hotkeyProblem is not null)
            _tray?.ShowNotification("config.json", hotkeyProblem);
        else if (!startup)
            _tray?.ShowNotification("Ayarlar yüklendi", $"{_rings.Count} halka hazır.");
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

    /// <summary>A reference for hand-editing until the settings window exists (Faz 5); rewritten on every start.</summary>
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
            // Shift picks the other layer: over files selected in Explorer the file ring is the default.
            // Checked here, on the UI thread, so the hook callback stays fast.
            var files = SelectedFiles.IsExplorer(target) && SelectedFiles.HasSelection(target);
            var layer = files != Native.IsShiftDown() ? RingLayers.Files : RingLayers.Main;
            _ring = RingFor(target, layer);
        }
        if (_ring is null)
            return;

        switch (ev.Kind)
        {
            case GestureEventKind.RingOpened:
                _ring.Open(ev.Origin);
                break;
            case GestureEventKind.RingMoved:
                _ring.Track(ev.Point);
                break;
            case GestureEventKind.RingReleased:
                Release(_ring, _ring.SliceAt(ev.Point), ev.Origin);
                break;
            case GestureEventKind.RingCancelled:
                _ring.Close(-1);
                break;
        }
    }

    private RingWindow RingFor(IntPtr target, string layer)
    {
        var ring = _catalog.For(Native.ProcessName(target), Native.WindowTitle(target), layer);
        return _rings.GetValueOrDefault(ring.Id) ?? _rings[_catalog.GlobalFor(layer).Id];
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

    /// <summary>Only with "updateRepo" set: half a minute after start, then once a day.</summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_updateRepo is null || _update is not null || await Updater.CheckAsync(_updateRepo) is not { } release)
                return;
            _update = release;
            var item = TrayItem("UpdateItem");
            item.Header = $"Güncelle (v{release.Version})";
            item.Visibility = Visibility.Visible;
            _tray?.ShowNotification("Güncelleme var", $"RadialShortcut {release.Version} hazır. Tepsi menüsünden \"Güncelle\" ile kurabilirsin.");
        };
        _updateTimer.Start();
    }

    private MenuItem TrayItem(string name) => _tray!.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == name);

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update is null)
            return;
        var item = (MenuItem)sender;
        item.IsEnabled = false;
        try
        {
            await Updater.InstallAsync(_update, text => item.Header = text);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            _tray?.ShowNotification("Güncellenemedi", ex.Message);
            item.Header = $"Güncelle (v{_update.Version})";
            item.IsEnabled = true;
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

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        StartupRegistration.SetEnabled(item.IsChecked);
        item.IsChecked = StartupRegistration.IsEnabled();
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppPaths.ConfigDirectory) { UseShellExecute = true });
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Shutdown();
}
