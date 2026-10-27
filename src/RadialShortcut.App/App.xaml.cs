using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
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
    private RingWindow? _ring;

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

        var settings = new GestureSettings();
        _debug = new DebugOverlay(settings);
        _debug.Show();

        _ring = new RingWindow(new RingStyle(), settings, DefaultRing.For(settings.SliceCount));
        _ring.Show();

        _hook = new MouseHookService(settings);
        _hook.Event += ev => Dispatcher.BeginInvoke(() => OnGesture(ev));
        _hook.Start();
    }

    private void OnGesture(GestureEvent ev)
    {
        _debug?.Show(ev);
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
                _ring.Close(_ring.SliceAt(ev.Point));
                break;
            case GestureEventKind.RingCancelled:
                _ring.Close(-1);
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
