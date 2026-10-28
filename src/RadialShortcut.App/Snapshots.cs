using System.IO;
using System.Windows.Media.Imaging;
using RadialShortcut.App.Tools;
using RadialShortcut.Core;

namespace RadialShortcut.App;

/// <summary>
/// <c>RadialShortcut.exe --snapshot &lt;folder&gt;</c>: draws the default rings and the drop zones to PNG
/// files without touching the screen, mouse or tray, so the README images follow the app.
/// </summary>
internal static class Snapshots
{
    public static async Task WriteAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        Palette.Theme = "dark";
        var settings = new GestureSettings();
        var catalog = new RingCatalog(null, settings.SliceCount);

        foreach (var (layer, hover) in new[] { (RingLayers.Main, 0), (RingLayers.Files, 2) })
        {
            var ring = new RingWindow(new RingStyle(), settings, catalog.GlobalFor(layer).Items);
            ring.Show(); // stays at its off-screen starting point; Open is never called
            Save(await ring.SnapshotAsync(hover), Path.Combine(folder, $"ring-{layer}.png"));
            ring.Close();
        }
        Save(DropZones.Snapshot(hover: 0), Path.Combine(folder, "drop-zones.png"));
    }

    private static void Save(BitmapSource image, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
