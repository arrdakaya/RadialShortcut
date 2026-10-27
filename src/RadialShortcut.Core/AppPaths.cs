namespace RadialShortcut.Core;

public static class AppPaths
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadialShortcut");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Tools the user has allowed to use the internet. Kept apart so config.json is never rewritten.</summary>
    public static string ConsentFile => Path.Combine(ConfigDirectory, "consent.json");

    /// <summary>Pictures\RadialShortcut: where screenshots are saved. Created on first use.</summary>
    public static string CaptureDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RadialShortcut");

    /// <summary>Videos\RadialShortcut: where screen recordings are saved. Created on first use.</summary>
    public static string RecordingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RadialShortcut");

    public static void EnsureCreated() => Directory.CreateDirectory(ConfigDirectory);
}
