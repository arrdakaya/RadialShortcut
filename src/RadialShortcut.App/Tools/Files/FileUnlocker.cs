using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Kilidi aç: for "the file is open in another program" errors. Works on the Explorer selection, or
/// straight on Windows' "File/Folder In Use" dialog: the item it names is found without asking. Windows'
/// Restart Manager names the processes holding the files (for a folder, every file inside it), and
/// programs whose working folder is inside the folder are added. After a confirmation they are asked to
/// close, the ones still running a moment later are ended, and the dialog's Try Again is pressed.
/// </summary>
internal static class FileUnlocker
{
    private const int MaxFilesPerFolder = 20000;

    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var dialog = IntPtr.Zero;
        var files = await SelectedFiles.SelectionOnlyAsync(target, folders: true);
        if (files.Count == 0 && InUseDialogItem(target) is { } named)
        {
            dialog = target;
            files = [named];
        }
        if (files.Count == 0)
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = T("Kilidi açılacak dosyalar", "Files to unlock"), Filter = T("Tüm dosyalar", "All files") + "|*.*", Multiselect = true };
            if (picker.ShowDialog() != true)
                return;
            files = [.. picker.FileNames];
        }

        var holders = await Task.Run(() => Holders(files));
        var what = files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : T($"{files.Count} öge", $"{files.Count} items");
        if (holders.Count == 0)
        {
            ResultCard.ForMessage(T("Tutan bulunamadı", "Nothing holds it"),
                    T($"{what} şu an açık olduğu bir uygulama bulunamadı. Yönetici olarak çalışan bir program ya da Explorer'ın kendisi tutuyor olabilir.",
                      $"No app was found holding {what}. A program running as administrator, or Explorer itself, may be holding it."), anchor)
                .WithButton(T("Tamam", "OK"), () => { }, primary: true).ShowNear();
            return;
        }

        var names = string.Join("\n", holders.Select(Describe).Distinct());
        if (!await ResultCard.AskAsync(T("Kapatılsın mı?", "Close them?"),
                T($"{what} şunlarda açık:\n{names}\n\nKapatılsın mı? Kaydedilmemiş işler kaybolabilir.", $"{what} is open in:\n{names}\n\nClose them? Unsaved work may be lost."),
                T("Kapat", "Close"), anchor))
        {
            foreach (var process in holders)
                process.Dispose();
            return;
        }

        await Task.Run(() => Close(holders));
        foreach (var process in holders)
            process.Dispose();
        var left = await Task.Run(() => Holders(files));
        var leftNames = string.Join(", ", left.Select(p => p.ProcessName + ".exe").Distinct());
        foreach (var process in left)
            process.Dispose();
        if (left.Count > 0)
        {
            ResultCard.ForMessage(T("Kapatılamadı", "Couldn't close"), T($"{leftNames} kapanmadı; yönetici olarak çalışıyor olabilir.", $"{leftNames} didn't close; it may be running as administrator."), anchor)
                .WithButton(T("Tamam", "OK"), () => { }, primary: true).ShowNear();
            return;
        }

        // Explorer's dialog is still waiting; Try Again finishes the delete or move the user started.
        if (dialog != IntPtr.Zero && PressTryAgain(dialog))
            return;
        ResultCard.ForMessage(T("Kilit açıldı", "Unlocked"), T($"{what} artık serbest.", $"{what} is free now."), anchor)
            .WithButton(T("Tamam", "OK"), () => { }, primary: true).ShowNear();
    }

    /// <summary>"Code.exe — main.py - proje" style line for the confirmation.</summary>
    private static string Describe(Process process)
    {
        var name = process.ProcessName + ".exe";
        try
        {
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? name : $"{name} — {title}";
        }
        catch (InvalidOperationException)
        {
            return name;
        }
    }

    // ---- Explorer's "File In Use" / "Folder In Use" dialog ----

    /// <summary>
    /// When the window is Explorer's in-use dialog, the full path of the item it names. The dialog only
    /// shows the name, so it is matched against the Explorer windows' selections first (the item being
    /// deleted is usually still selected), then against the items in their folders and on the desktop.
    /// </summary>
    private static string? InUseDialogItem(IntPtr window)
    {
        if (window == IntPtr.Zero || Native.ClassName(window) != "#32770" || !Native.ProcessName(window).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var texts = DialogTexts(window);
        if (texts.Count == 0)
            return null;

        bool Named(string path)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            // Explorer may hide extensions, so "rapor" in the dialog is "rapor.docx" on disk.
            return texts.Contains(name) || texts.Contains(Path.GetFileNameWithoutExtension(name));
        }

        var folders = new List<string>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic explorer in shell.Windows())
            {
                try
                {
                    foreach (dynamic item in explorer.Document.SelectedItems())
                    {
                        string path = item.Path;
                        if (Named(path) && (File.Exists(path) || Directory.Exists(path)))
                            return path;
                    }
                    string folder = explorer.Document.Folder.Self.Path;
                    folders.Add(folder);
                }
                catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // Not a folder view, or closed meanwhile.
                }
            }
        }
        catch (COMException)
        {
        }
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var text in texts)
            {
                if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    continue;
                var exact = Path.Combine(folder, text);
                if (Directory.Exists(exact) || File.Exists(exact))
                    return exact;
                try
                {
                    var hidden = Directory.EnumerateFiles(folder, text + ".*").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == text);
                    if (hidden is not null)
                        return hidden;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return null;
    }

    /// <summary>Every piece of text in the dialog (title, message, item name, date line).</summary>
    private static HashSet<string> DialogTexts(IntPtr window)
    {
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (AutomationElement element in AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                var name = element.Current.Name?.Trim();
                if (!string.IsNullOrEmpty(name))
                    texts.Add(name);
            }
        }
        catch (Exception)
        {
            // The dialog closed or Explorer is busy.
        }
        return texts;
    }

    /// <summary>Presses the in-use dialog's Try Again button. False when it is gone or has no such button.</summary>
    private static bool PressTryAgain(IntPtr window)
    {
        try
        {
            var buttons = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var name = button.Current.Name ?? "";
                if (name.Contains("Dene", StringComparison.OrdinalIgnoreCase) || name.Contains("Try", StringComparison.OrdinalIgnoreCase) || name.Contains("Retry", StringComparison.OrdinalIgnoreCase))
                {
                    ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // The dialog went away on its own.
        }
        return false;
    }

    // ---- Who holds the items ----

    /// <summary>Windowed apps get the chance to close on their own first; whatever is left is ended.</summary>
    private static void Close(List<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }
        }
        foreach (var process in processes)
        {
            try
            {
                if (!process.WaitForExit(1500))
                    process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to end (another user's or elevated); reported by the recheck.
            }
        }
    }

    /// <summary>
    /// The apps holding any of the items. Folders are opened up into their files, and programs whose
    /// working folder is inside one (a terminal, a script) count too, since that alone blocks a delete.
    /// </summary>
    private static List<Process> Holders(IReadOnlyList<string> items)
    {
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var item in items)
        {
            if (Directory.Exists(item))
            {
                folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item)));
                files.AddRange(FilesIn(item));
            }
            else
                files.Add(item);
        }

        var result = files.Count > 0 ? FileHolders(files) : [];
        if (folders.Count > 0)
        {
            foreach (var process in WorkingIn(folders))
            {
                if (result.Any(p => p.Id == process.Id))
                    process.Dispose();
                else
                    result.Add(process);
            }
        }
        return result;
    }

    private static IEnumerable<string> FilesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Take(MaxFilesPerFolder).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Services, Explorer (it often holds a file for a thumbnail; ending it takes the desktop down),
    /// critical system processes and this app are left out, since closing them is never what the user means.
    /// </summary>
    private static List<Process> FileHolders(IReadOnlyList<string> files)
    {
        var result = new List<Process>();
        var key = new string('\0', CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out var session, 0, key) != 0)
            return result;
        try
        {
            var paths = files.ToArray();
            if (RmRegisterResources(session, (uint)paths.Length, paths, 0, null, 0, null) != 0)
                return result;
            uint needed = 0, count = 0;
            RM_PROCESS_INFO[]? infos = null;
            // ERROR_MORE_DATA: the list grew between the two calls; ask again with room for it.
            int error;
            do
            {
                count = needed;
                infos = new RM_PROCESS_INFO[count];
                error = RmGetList(session, out needed, ref count, infos, out _);
            } while (error == ERROR_MORE_DATA);
            if (error != 0)
                return result;
            for (var i = 0; i < count; i++)
            {
                if (infos[i].ApplicationType is RmService or RmExplorer or RmCritical || infos[i].Process.dwProcessId == Environment.ProcessId)
                    continue;
                if (result.Any(p => p.Id == infos[i].Process.dwProcessId))
                    continue;
                try
                {
                    result.Add(Process.GetProcessById(infos[i].Process.dwProcessId));
                }
                catch (ArgumentException)
                {
                    // Exited meanwhile.
                }
            }
        }
        finally
        {
            RmEndSession(session);
        }
        return result;
    }

    /// <summary>
    /// Processes whose current directory is inside one of the folders, read from each process's PEB.
    /// Only 64-bit processes of this user can be read; others are skipped.
    /// </summary>
    private static List<Process> WorkingIn(List<string> folders)
    {
        var result = new List<Process>();
        if (!Environment.Is64BitProcess)
            return result;
        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                if (process.Id is 0 or 4 || process.Id == Environment.ProcessId || process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (CurrentDirectory(process.Id) is { } cwd)
                {
                    cwd = Path.TrimEndingDirectorySeparator(cwd);
                    keep = folders.Any(f => cwd.Equals(f, StringComparison.OrdinalIgnoreCase) || cwd.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                if (keep)
                    result.Add(process);
                else
                    process.Dispose();
            }
        }
        return result;
    }

    private static string? CurrentDirectory(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64)
                return null; // its 32-bit PEB holds the live directory; the 64-bit copy may be stale
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0 || info.PebBaseAddress == IntPtr.Zero)
                return null;
            // x64 layout: PEB.ProcessParameters at 0x20; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath at 0x38.
            var pointer = new byte[8];
            if (!ReadProcessMemory(handle, info.PebBaseAddress + 0x20, pointer, 8, out _))
                return null;
            var parameters = (IntPtr)BitConverter.ToInt64(pointer);
            var unicode = new byte[16];
            if (parameters == IntPtr.Zero || !ReadProcessMemory(handle, parameters + 0x38, unicode, 16, out _))
                return null;
            var length = BitConverter.ToUInt16(unicode, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(unicode, 8);
            if (length == 0 || length > 0x8000 || buffer == IntPtr.Zero)
                return null;
            var text = new byte[length];
            return ReadProcessMemory(handle, buffer, text, length, out _) ? System.Text.Encoding.Unicode.GetString(text) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int CCH_RM_SESSION_KEY = 32, CCH_RM_MAX_APP_NAME = 255, CCH_RM_MAX_SVC_NAME = 63, ERROR_MORE_DATA = 234;
    private const int RmService = 3, RmExplorer = 4, RmCritical = 1000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_VM_READ = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, RM_UNIQUE_PROCESS[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] infos, out uint rebootReasons);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
