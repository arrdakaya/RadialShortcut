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

    protected override void OnStartup(StartupEventArgs e)
    {
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

        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();

        var startupItem = (MenuItem)_tray.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == "StartupItem");
        startupItem.IsChecked = StartupRegistration.IsEnabled();

        _debug = new DebugOverlay(_settings);
        _debug.Show();

        _hook = new MouseHookService(_settings);
        _hook.Event += ev => Dispatcher.BeginInvoke(() => OnGesture(ev));
        LoadConfig(startup: true);
        _hook.Start();
        WatchConfig();
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
            var ring = _catalog.For(Native.ProcessName(target), Native.WindowTitle(target));
            _ring = _rings.GetValueOrDefault(ring.Id) ?? _rings[_catalog.Global.Id];
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
                var slice = _ring.SliceAt(ev.Point);
                _ring.Close(slice);
                if (slice >= 0)
                    _ = RunAfterRingFadesAsync(_ring.ItemAt(slice), ev.Origin);
                break;
            case GestureEventKind.RingCancelled:
                _ring.Close(-1);
                break;
        }
    }

    /// <summary>Screen tools freeze the screen first, so the ring has to be gone before they start.</summary>
    private static async Task RunAfterRingFadesAsync(RingItem item, ScreenPoint origin)
    {
        await Task.Delay(200);
        await ToolRunner.RunAsync(item, origin);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _configWatcher?.Dispose();
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
