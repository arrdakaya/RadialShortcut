namespace RadialShortcut.Core;

public static class AppPaths
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadialShortcut");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Tools the user has allowed to use the internet. Kept apart so config.json is never rewritten.</summary>
    public static string ConsentFile => Path.Combine(ConfigDirectory, "consent.json");

    /// <summary>Documents\RadialShortcut: everything the tools save lives together here.</summary>
    public static string OutputDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RadialShortcut");

    /// <summary>Documents\RadialShortcut\Resimler: where screenshots are saved. Created on first use.</summary>
    public static string CaptureDirectory { get; } = Path.Combine(OutputDirectory, "Resimler");

    /// <summary>Documents\RadialShortcut\Videolar: where screen recordings are saved. Created on first use.</summary>
    public static string RecordingDirectory { get; } = Path.Combine(OutputDirectory, "Videolar");

    /// <summary>Documents\RadialShortcut\İndirilenler: media downloaded from a link.</summary>
    public static string DownloadDirectory { get; } = Path.Combine(OutputDirectory, "İndirilenler");

    /// <summary>Documents\RadialShortcut\Dosyalar: converted files whose own folder cannot be written to.</summary>
    public static string FilesDirectory { get; } = Path.Combine(OutputDirectory, "Dosyalar");

    /// <summary>%LocalAppData%\RadialShortcut	ools: ffmpeg, yt-dlp and the background model, downloaded on first use.</summary>
    public static string ToolsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadialShortcut", "tools");

    public static string HistoryFile => Path.Combine(ConfigDirectory, "history.jsonl");

    /// <summary>Written once the first-run hint has been shown.</summary>
    public static string WelcomedFile => Path.Combine(ConfigDirectory, "welcomed");

    /// <summary>Written just before an update restarts the app, holding the new version; read and removed on the next start.</summary>
    public static string UpdatedFile => Path.Combine(ConfigDirectory, "updated");

    /// <summary>How many times the "Shift: tools" drag hint has been shown; it stops after a few.</summary>
    public static string DragHintFile => Path.Combine(ConfigDirectory, "drag-hint");

    public static void EnsureCreated() => Directory.CreateDirectory(ConfigDirectory);
}
