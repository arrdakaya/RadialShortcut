using System.Runtime;
using System.Windows;
using System.Windows.Threading;

namespace RadialShortcut.App;

/// <summary>
/// Screenshots, recordings and pins hold large bitmaps. A few seconds after the last such window
/// closes, compact the heap so that memory goes back to Windows instead of lingering until the next GC.
/// </summary>
internal static class MemoryTrim
{
    private static DispatcherTimer? _timer;

    public static void Install()
    {
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        // Window.Closed is not routed; Unloaded is, and every window raises it when it closes.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((_, _) =>
        {
            _timer.Stop();
            _timer.Start();
        }));
    }
}
