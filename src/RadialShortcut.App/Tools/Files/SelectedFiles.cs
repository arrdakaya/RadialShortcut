using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RadialShortcut.App.Tools;

/// <summary>The files the file tools work on: selected in Explorer or on the desktop, or picked in a dialog.</summary>
internal static class SelectedFiles
{
    private static object? _shell;

    public static bool IsExplorer(IntPtr hwnd) => Native.ClassName(hwnd) == "CabinetWClass";

    public static bool IsDesktop(IntPtr hwnd) => Native.ClassName(hwnd) is "Progman" or "WorkerW";

    /// <summary>
    /// The selection in an Explorer window, through the Shell's own automation object. With tabs
    /// (Windows 11) all tabs share the window; the first one with a selection wins.
    /// </summary>
    public static List<string> InExplorer(IntPtr hwnd)
    {
        var paths = new List<string>();
        try
        {
            dynamic shell = _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic window in shell.Windows())
            {
                if (paths.Count > 0)
                    break;
                try
                {
                    if ((IntPtr)(long)window.HWND != hwnd)
                        continue;
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        string path = item.Path;
                        if (File.Exists(path))
                            paths.Add(path);
                    }
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
        return paths;
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
        IsExplorer(target) ? InExplorer(target)
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

    public const string AllMedia = "Resim, video, ses, PDF|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif;*.ico;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v;*.mp3;*.m4a;*.wav;*.ogg;*.opus;*.flac;*.aac;*.pdf|Tüm dosyalar|*.*";
    public const string Images = "Resimler|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif|Tüm dosyalar|*.*";
    public const string Videos = "Videolar|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v|Tüm dosyalar|*.*";
}
