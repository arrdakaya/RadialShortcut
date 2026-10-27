namespace RadialShortcut.Core;

public static class AppPaths
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadialShortcut");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Pictures\RadialShortcut: where screenshots are saved. Created on first use.</summary>
    public static string CaptureDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RadialShortcut");

    public static void EnsureCreated() => Directory.CreateDirectory(ConfigDirectory);
}
