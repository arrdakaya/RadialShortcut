namespace RadialShortcut.App;

/// <summary>An app from the Start menu's "All apps" list; <see cref="Path"/> launches it through the shell.</summary>
internal sealed record InstalledApp(string Name, string Path);

/// <summary>
/// The apps Windows lists under Start → All apps, Store apps included, read from shell:AppsFolder.
/// Each launches as "shell:AppsFolder\&lt;id&gt;", so no file location is needed. Must run on an STA thread.
/// </summary>
internal static class InstalledApps
{
    public const string Prefix = @"shell:AppsFolder\";

    public static List<InstalledApp> Load()
    {
        var apps = new List<InstalledApp>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (var item in folder.Items())
            {
                string name = item.Name, id = item.Path;
                // Uninstallers and help files sit next to the apps in the Start menu; they are not worth a slice.
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)
                    || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("Kaldır", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("http", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    continue;
                apps.Add(new InstalledApp(name, Prefix + id));
            }
        }
        catch (Exception)
        {
            // Late-bound shell calls can fail in more ways than one; an empty list sends the user to "Gözat" instead of crashing settings.
        }
        return apps.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
