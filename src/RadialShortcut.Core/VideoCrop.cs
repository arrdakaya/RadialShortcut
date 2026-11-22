using System.Globalization;

namespace RadialShortcut.Core;

/// <summary>Which part of a crop rectangle a drag holds: its edges, or all of it to move it.</summary>
[Flags]
public enum CropEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    Move = 16,
}

/// <summary>A crop in the picture's own pixels, as ffmpeg's crop filter takes it.</summary>
public readonly record struct PixelCrop(int X, int Y, int Width, int Height)
{
    public string Filter => $"crop={Width}:{Height}:{X}:{Y}";
}

/// <summary>
/// The part of the picture to keep, as fractions of the shown picture's width and height, so it
/// stays put whatever size the player draws the video at.
/// </summary>
public readonly record struct CropArea(double X, double Y, double Width, double Height)
{
    /// <summary>Smaller than this share of the picture a crop is a stray click, not a choice.</summary>
    public const double Smallest = 0.02;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>The rectangle between two corners, clamped to the picture; null when too small or the whole picture.</summary>
    public static CropArea? Between(double x1, double y1, double x2, double y2)
    {
        double left = Clamp01(Math.Min(x1, x2)), right = Clamp01(Math.Max(x1, x2));
        double top = Clamp01(Math.Min(y1, y2)), bottom = Clamp01(Math.Max(y1, y2));
        return Make(left, top, right - left, bottom - top);
    }

    /// <summary>Kept inside the picture; null when too small to mean anything or when it keeps all of it.</summary>
    public static CropArea? Make(double x, double y, double width, double height)
    {
        width = Clamp01(width);
        height = Clamp01(height);
        if (width < Smallest || height < Smallest || width > 0.995 && height > 0.995)
            return null;
        return new CropArea(Math.Clamp(x, 0, 1 - width), Math.Clamp(y, 0, 1 - height), width, height);
    }

    /// <summary>The same size, moved by the offset but no further than the picture's edges.</summary>
    public CropArea Moved(double dx, double dy) =>
        new(Math.Clamp(X + dx, 0, 1 - Width), Math.Clamp(Y + dy, 0, 1 - Height), Width, Height);

    /// <summary>The held edges brought to the point, never past the opposite edge.</summary>
    public CropArea Dragged(CropEdges edges, double x, double y)
    {
        x = Clamp01(x);
        y = Clamp01(y);
        double left = X, top = Y, right = Right, bottom = Bottom;
        if (edges.HasFlag(CropEdges.Left))
            left = Math.Min(x, right - Smallest);
        if (edges.HasFlag(CropEdges.Right))
            right = Math.Max(x, left + Smallest);
        if (edges.HasFlag(CropEdges.Top))
            top = Math.Min(y, bottom - Smallest);
        if (edges.HasFlag(CropEdges.Bottom))
            bottom = Math.Max(y, top + Smallest);
        return new CropArea(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// What a press at the point grabs: the edges within the tolerance (both at a corner), the
    /// whole rectangle inside it, nothing outside.
    /// </summary>
    public CropEdges HitTest(double x, double y, double toleranceX, double toleranceY)
    {
        if (x < X - toleranceX || x > Right + toleranceX || y < Y - toleranceY || y > Bottom + toleranceY)
            return CropEdges.None;
        var edges = CropEdges.None;
        if (Math.Abs(x - X) <= toleranceX)
            edges |= CropEdges.Left;
        else if (Math.Abs(x - Right) <= toleranceX)
            edges |= CropEdges.Right;
        if (Math.Abs(y - Y) <= toleranceY)
            edges |= CropEdges.Top;
        else if (Math.Abs(y - Bottom) <= toleranceY)
            edges |= CropEdges.Bottom;
        if (edges != CropEdges.None)
            return edges;
        return x > X && x < Right && y > Y && y < Bottom ? CropEdges.Move : CropEdges.None;
    }

    /// <summary>In pixels of a picture that size, with an even width and height as H.264 needs.</summary>
    public PixelCrop ToPixels(int width, int height)
    {
        var w = Math.Clamp(Even(Width * width), 2, Math.Max(2, width - width % 2));
        var h = Math.Clamp(Even(Height * height), 2, Math.Max(2, height - height % 2));
        var x = Math.Clamp(Even(X * width), 0, Math.Max(0, width - w));
        var y = Math.Clamp(Even(Y * height), 0, Math.Max(0, height - h));
        return new PixelCrop(x, y, w, h);
    }

    /// <summary>The crop filter for a picture that size, or in terms of ffmpeg's iw/ih when the size isn't known.</summary>
    public string Filter(int width, int height) => width > 0 && height > 0
        ? ToPixels(width, height).Filter
        : $"crop=trunc(iw*{F(Width)}/2)*2:trunc(ih*{F(Height)}/2)*2:trunc(iw*{F(X)}/2)*2:trunc(ih*{F(Y)}/2)*2";

    /// <summary>The file as it is once cropped: the picture's size becomes the crop's.</summary>
    public MediaInfo Apply(MediaInfo info)
    {
        if (!info.Video || info.Width <= 0 || info.Height <= 0)
            return info;
        var p = ToPixels(info.Width, info.Height);
        return info with { Width = p.Width, Height = p.Height };
    }

    private static int Even(double v) => (int)Math.Round(v / 2) * 2;

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
