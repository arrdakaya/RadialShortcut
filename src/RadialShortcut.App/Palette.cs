using Microsoft.Win32;
using System.Windows.Media;

namespace RadialShortcut.App;

/// <summary>
/// Colors of cards, badges and tool windows, light or dark. Read when a window is built,
/// so a theme change in config.json applies to the next window that opens.
/// </summary>
internal static class Palette
{
    /// <summary>"system", "light" or "dark", from config.json.</summary>
    public static string Theme { get; set; } = "system";

    public static bool IsDark => Theme.ToLowerInvariant() switch
    {
        "light" => false,
        "dark" => true,
        _ => !SystemUsesLightTheme(),
    };

    public static Brush Surface => Brush(IsDark ? 0x262A30 : 0xF7F7F8);
    public static Brush Raised => Brush(IsDark ? 0x30343B : 0xFFFFFF);
    public static Brush Line => Brush(IsDark ? 0x353A42 : 0xDADCE0);
    public static Brush Text => Brush(IsDark ? 0xF0F0F0 : 0x1F2328);
    public static Brush Muted => Brush(IsDark ? 0xA8A8A8 : 0x5F6670);
    public static Brush Accent => Brush(0x0067C0);

    private static SolidColorBrush Brush(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
