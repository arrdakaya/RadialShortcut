using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Kilidi aç: for "the file is open in another program" errors. Windows' Restart Manager names the
/// processes holding the selected files; after a confirmation they are asked to close, and the ones
/// still running a moment later are ended.
/// </summary>
internal static class FileUnlocker
{
    public static async Task RunAsync(IntPtr target, Int32Rect anchor)
    {
        var files = await SelectedFiles.GetAsync(target, "Kilidi açılacak dosyalar", "Tüm dosyalar|*.*");
        if (files.Count == 0)
            return;

        var holders = await Task.Run(() => Holders(files));
        var what = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} dosya";
        if (holders.Count == 0)
        {
            ResultCard.ForMessage("Dosya serbest", $"{what} şu an hiçbir uygulamada açık değil.", anchor)
                .WithButton("Tamam", () => { }, primary: true).ShowNear();
            return;
        }

        var names = string.Join(", ", holders.Select(p => p.ProcessName + ".exe").Distinct());
        if (!await ResultCard.AskAsync("Kapatılsın mı?", $"{what} şunlarda açık: {names}. Kaydedilmemiş işler kaybolabilir.", "Kapat", anchor))
            return;

        await Task.Run(() => Close(holders));
        foreach (var process in holders)
            process.Dispose();
        var left = await Task.Run(() => Holders(files));
        foreach (var process in left)
            process.Dispose();
        if (left.Count == 0)
            ResultCard.ForMessage("Kilit açıldı", $"{what} artık serbest.", anchor)
                .WithButton("Tamam", () => { }, primary: true).ShowNear();
        else
            ResultCard.ForMessage("Kapatılamadı", $"{string.Join(", ", left.Select(p => p.ProcessName + ".exe").Distinct())} kapanmadı; yönetici olarak çalışıyor olabilir.", anchor)
                .WithButton("Tamam", () => { }, primary: true).ShowNear();
    }

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
    /// The apps that have any of the files open. Services, Explorer (it often holds a file for a
    /// thumbnail; ending it takes the desktop down), critical system processes and this app are left
    /// out, since closing them is never what the user means.
    /// </summary>
    private static List<Process> Holders(IReadOnlyList<string> files)
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

    private const int CCH_RM_SESSION_KEY = 32, CCH_RM_MAX_APP_NAME = 255, CCH_RM_MAX_SVC_NAME = 63, ERROR_MORE_DATA = 234;
    private const int RmService = 3, RmExplorer = 4, RmCritical = 1000;

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

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, RM_UNIQUE_PROCESS[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] infos, out uint rebootReasons);
}
