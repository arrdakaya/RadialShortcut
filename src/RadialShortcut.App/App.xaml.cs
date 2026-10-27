using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using RadialShortcut.Core;

namespace RadialShortcut.App;

public partial class App : Application
{
    private const string MutexName = @"Local\RadialShortcut.SingleInstance";

    private Mutex? _singleInstance;
    private TaskbarIcon? _tray;

    public bool IsPaused { get; private set; }

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        IsPaused = ((MenuItem)sender).IsChecked;
        if (_tray is not null)
            _tray.ToolTipText = IsPaused ? "RadialShortcut (duraklatıldı)" : "RadialShortcut";
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
