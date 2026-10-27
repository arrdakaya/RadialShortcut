namespace RadialShortcut.Core;

public static class AppPaths
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadialShortcut");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public static void EnsureCreated() => Directory.CreateDirectory(ConfigDirectory);
}
