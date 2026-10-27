using RadialShortcut.Core;

namespace RadialShortcut.Tests;

public class AppPathsTests
{
    [Fact]
    public void ConfigFile_LivesInRoamingAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "RadialShortcut", "config.json"), AppPaths.ConfigFile);
    }
}
