namespace RadialShortcut.Core;

public enum ColorNotation { Hex, Rgb, Hsl }

public static class ColorFormat
{
    public static string Format(ColorNotation notation, byte r, byte g, byte b) => notation switch
    {
        ColorNotation.Rgb => $"rgb({r}, {g}, {b})",
        ColorNotation.Hsl => ToHsl(r, g, b) is var (h, s, l) ? $"hsl({h}, {s}%, {l}%)" : "",
        _ => $"#{r:X2}{g:X2}{b:X2}",
    };

    public static ColorNotation Next(ColorNotation n) => (ColorNotation)(((int)n + 1) % 3);

    /// <summary>Hue in degrees, saturation and lightness in percent, all rounded.</summary>
    public static (int H, int S, int L) ToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double l = (max + min) / 2, d = max - min;
        if (d == 0)
            return (0, 0, (int)Math.Round(l * 100));

        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == rf ? (gf - bf) / d % 6 : max == gf ? (bf - rf) / d + 2 : (rf - gf) / d + 4;
        h = (h * 60 + 360) % 360;
        return ((int)Math.Round(h) % 360, (int)Math.Round(s * 100), (int)Math.Round(l * 100));
    }
}
