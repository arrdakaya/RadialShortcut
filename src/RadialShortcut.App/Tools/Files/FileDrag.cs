using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using RadialShortcut.Input;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Opens the drop zones while files are dragged out of Explorer or the desktop. A drag only counts when
/// it starts on a file or folder, so a rubber-band selection or a window move never shows them. That is
/// checked at the press: once the drag is under way Explorer's drag image covers the spot. The zones
/// open when the cursor rests for a moment, or when Shift is pressed after the drag began; Shift held
/// from the start is a range selection, not a request.
/// </summary>
internal static class FileDrag
{
    private const int RestMs = 450;
    private const int RestSlack = 6;

    private static DispatcherTimer? _timer;
    private static ScreenPoint _last;
    private static DateTime _still;
    private static bool _shiftAtStart;
    private static int _drag;
    private static Task<bool> _pressedOnItem = Task.FromResult(false);
    private static List<string> _files = [];

    public static void Pressed(ScreenPoint at)
    {
        var source = Native.WindowAt(at.X, at.Y);
        // The taskbar is Explorer too, and its buttons are list items; reordering them is not a file drag.
        var shell = Native.ProcessName(source).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            && Native.ClassName(source) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
        // UI Automation asks Explorer across processes, so off the UI thread.
        _pressedOnItem = shell
            ? Task.Run(() => IsItemAt(at))
            : Task.FromResult(false);
    }

    public static async void Started(ScreenPoint start, Action<string>? note)
    {
        var drag = ++_drag;
        _shiftAtStart = Native.IsShiftDown();
        var onItem = await _pressedOnItem;
        note?.Invoke($"Sürükleme: {(onItem ? "dosya" : "dosya değil")}");
        if (!onItem || drag != _drag)
            return;
        // What is being dragged picks the tiles; when it cannot be read every tile shows.
        var source = Native.WindowAt(start.X, start.Y);
        _files = SelectedFiles.IsExplorer(source) ? SelectedFiles.InExplorer(source)
            : SelectedFiles.IsDesktop(source) ? SelectedFiles.OnDesktopView()
            : [];
        if (_files.Count > 0 && !DropZones.AnyFor(_files))
            return;

        DragHint.ShowAt(Native.CursorPosition());
        _last = Native.CursorPosition();
        _still = DateTime.UtcNow;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public static void Ended()
    {
        _drag++;
        _timer?.Stop();
        DragHint.Dismiss();
        DropZones.EndDrag();
    }

    private static void Tick()
    {
        var now = Native.CursorPosition();
        if (Math.Abs(now.X - _last.X) + Math.Abs(now.Y - _last.Y) > RestSlack)
        {
            _last = now;
            _still = DateTime.UtcNow;
        }
        var shift = Native.IsShiftDown();
        if (!shift)
            _shiftAtStart = false;
        if (shift && !_shiftAtStart || (DateTime.UtcNow - _still).TotalMilliseconds >= RestMs)
        {
            _timer?.Stop();
            DragHint.Dismiss();
            DropZones.ShowAt(now, _files);
        }
    }

    /// <summary>True when the point is on a file or folder in an Explorer view or on the desktop.</summary>
    private static bool IsItemAt(ScreenPoint at)
    {
        // The desktop's icon list shows no items to UI Automation on some setups, so ask the list itself.
        var list = Native.ChildWindowAt(at.X, at.Y);
        if (Native.ClassName(list) == "SysListView32")
            return Native.ListViewItemAt(list, at.X, at.Y);
        try
        {
            var element = AutomationElement.FromPoint(new Point(at.X, at.Y));
            // The point may land on the item's name or icon; the list item is a step or two up.
            for (var i = 0; i < 4 && element is not null; i++)
            {
                if (element.Current.ControlType == ControlType.ListItem || element.Current.ControlType == ControlType.DataItem)
                    return true;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception)
        {
            // Explorer busy or the element gone: treat it as not a file drag.
        }
        return false;
    }
}
