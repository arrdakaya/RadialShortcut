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

    /// <summary>The ring's and drop strip's glassy colors; taken once per window, since IsDark reads the registry.</summary>
    public static GlassColors Glass => IsDark ? DarkGlass : LightGlass;

    private static readonly GlassColors DarkGlass = new(
        Argb(0xF41E2025), Argb(0xF430333A), Argb(0xF42C2F36), Argb(0xF41A1C21),
        Argb(0x1EFFFFFF), Argb(0x38FFFFFF), Argb(0x0AFFFFFF),
        Argb(0xFFE8EAED), Argb(0xFFB8BCC3), Argb(0xFF9AA0A8), 0.45);

    private static readonly GlassColors LightGlass = new(
        Argb(0xF4E6E8EC), Argb(0xF4FBFBFC), Argb(0xF4FFFFFF), Argb(0xF4ECEEF1),
        Argb(0x22000000), Argb(0x0C000000), Argb(0x2A000000),
        Argb(0xFF1F2328), Argb(0xFF464C55), Argb(0xFF6B727C), 0.22);

    private static Color Argb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

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

/// <param name="Inner">Slice fill toward the center.</param>
/// <param name="Outer">Slice fill at the rim.</param>
/// <param name="CenterLight">Center disc / strip background, lit side.</param>
/// <param name="CenterDark">Center disc / strip background, far side.</param>
/// <param name="Edge">Hairline between slices and around tiles.</param>
/// <param name="EdgeTop">Outline at the top of the disc and strip.</param>
/// <param name="EdgeBottom">Outline at the bottom.</param>
/// <param name="Shadow">Opacity of the strip's drop shadow.</param>
internal sealed record GlassColors(Color Inner, Color Outer, Color CenterLight, Color CenterDark,
    Color Edge, Color EdgeTop, Color EdgeBottom, Color Text, Color Label, Color Muted, double Shadow);
