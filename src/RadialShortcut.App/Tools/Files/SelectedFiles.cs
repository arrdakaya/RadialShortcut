using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RadialShortcut.App.Tools;

/// <summary>The files the file tools work on: selected in Explorer or on the desktop, or picked in a dialog.</summary>
internal static class SelectedFiles
{
    [ThreadStatic] private static object? _shell; // one per thread: the UI thread and the worker each keep their own

    /// <summary>Files dropped on a <see cref="DropZones"/> tile; the next tool takes them instead of the selection.</summary>
    public static List<string>? Dropped;

    public static bool IsExplorer(IntPtr hwnd) => Native.ClassName(hwnd) == "CabinetWClass";

    public static bool IsDesktop(IntPtr hwnd) => Native.ClassName(hwnd) is "Progman" or "WorkerW";

    /// <summary>
    /// The selection in an Explorer window, through the Shell's own automation object. With tabs
    /// (Windows 11) all tabs share the window; the first one with a selection wins.
    /// </summary>
    public static List<string> InExplorer(IntPtr hwnd) => Read(hwnd, countOnly: false).Paths;

    /// <summary>
    /// The desktop's selection without touching the clipboard, so it can be read mid-drag. The Shell
    /// hands out the desktop's view by asking for it as the desktop (SWC_DESKTOP).
    /// </summary>
    public static List<string> OnDesktopView()
    {
        var paths = new List<string>();
        try
        {
            dynamic shell = _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            object location = 0, root = 0;
            dynamic desktop = shell.Windows().FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
            foreach (dynamic item in desktop.Document.SelectedItems())
            {
                string path = item.Path;
                if (File.Exists(path))
                    paths.Add(path);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // No desktop view (Explorer restarting): the strip then shows every tile.
        }
        return paths;
    }

    /// <summary>
    /// Whether files (not just folders) are selected. Asked on every ring open, so it runs on its own
    /// thread and gives up after a short wait: a busy or hung Explorer then opens the main ring
    /// instead of holding the ring back.
    /// </summary>
    public static bool HasSelection(IntPtr hwnd)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Worker.Add(() => answer.SetResult(Read(hwnd, countOnly: true).Count > 0));
        return answer.Task.Wait(150) && answer.Task.Result;
    }

    /// <summary>Creates the Shell object on the worker ahead of the first ring open.</summary>
    public static void WarmUp() => Worker.Add(() => Read(IntPtr.Zero, countOnly: true));

    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Worker = StartWorker();

    private static System.Collections.Concurrent.BlockingCollection<Action> StartWorker()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        }) { IsBackground = true, Name = "RadialShortcut shell" };
        thread.SetApartmentState(ApartmentState.STA); // Shell automation objects want an STA
        thread.Start();
        return queue;
    }

    private static (List<string> Paths, int Count) Read(IntPtr hwnd, bool countOnly)
    {
        var paths = new List<string>();
        var count = 0;
        try
        {
            dynamic shell = _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic window in shell.Windows())
            {
                if (count > 0)
                    break;
                try
                {
                    if ((IntPtr)(long)window.HWND != hwnd)
                        continue;
                    dynamic items = window.Document.SelectedItems();
                    if (countOnly)
                    {
                        // Only the first few: enough to tell files from a folder-only selection.
                        var seen = 0;
                        foreach (dynamic item in items)
                        {
                            if (++seen > 8 || count > 0)
                                break;
                            if (!(bool)item.IsFolder && (bool)item.IsFileSystem)
                                count = 1;
                        }
                        if (count == 0)
                            continue;
                        break;
                    }
                    foreach (dynamic item in items)
                    {
                        string path = item.Path;
                        if (File.Exists(path))
                            paths.Add(path);
                    }
                    count = paths.Count;
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // Not a folder view (Control Panel, Home), or the window closed meanwhile.
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _shell = null;
        }
        return (paths, count);
    }

    /// <summary>
    /// What the tool should work on: the Explorer or desktop selection under the ring, otherwise
    /// files picked in a dialog. Empty when the dialog is cancelled.
    /// </summary>
    public static async Task<List<string>> GetAsync(IntPtr target, string title, string filter)
    {
        var files = await SelectionOnlyAsync(target);
        if (files.Count > 0)
            return files;

        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        return dialog.ShowDialog() == true ? [.. dialog.FileNames] : [];
    }

    /// <summary>The Explorer or desktop selection under the ring; empty anywhere else.</summary>
    public static async Task<List<string>> SelectionOnlyAsync(IntPtr target) =>
        Interlocked.Exchange(ref Dropped, null) is { } dropped ? dropped
        : IsExplorer(target) ? InExplorer(target)
        : IsDesktop(target) ? await OnDesktopAsync(target)
        : [];

    /// <summary>The desktop has no automation object; copying the selection is the simple way to read it.</summary>
    private static async Task<List<string>> OnDesktopAsync(IntPtr target)
    {
        var saved = SelectionTools.SaveClipboard();
        var before = Native.ClipboardSequence();
        Native.Focus(target);
        Native.SendKeys([0x11, 0x43]); // Ctrl+C
        for (var i = 0; i < 12 && Native.ClipboardSequence() == before; i++)
            await Task.Delay(25);

        var files = new List<string>();
        if (Native.ClipboardSequence() != before)
        {
            ToolRunner.Retry(() =>
            {
                if (Clipboard.ContainsFileDropList())
                    files.AddRange(Clipboard.GetFileDropList().Cast<string>().Where(File.Exists));
            });
            SelectionTools.RestoreClipboard(saved);
        }
        return files;
    }

    public static string AllMedia => T("Resim, video, ses, PDF", "Images, video, audio, PDF") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif;*.ico;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v;*.mp3;*.m4a;*.wav;*.ogg;*.opus;*.flac;*.aac;*.pdf|" + T("Tüm dosyalar", "All files") + "|*.*";
    public static string Images => T("Resimler", "Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif|" + T("Tüm dosyalar", "All files") + "|*.*";
    public static string Videos => T("Videolar", "Videos") + "|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v|" + T("Tüm dosyalar", "All files") + "|*.*";
}
