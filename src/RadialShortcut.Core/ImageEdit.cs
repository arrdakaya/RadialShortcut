namespace RadialShortcut.Core;

/// <summary>
/// How a picture is turned before it is cropped: quarter turns clockwise, mirrored left-right or
/// top-bottom (applied before the turn), and a small straightening angle in degrees (clockwise),
/// after which the corners the turn leaves empty are cut away.
/// </summary>
public readonly record struct ImageTurn(int Quarters, bool FlipX, bool FlipY, double Angle)
{
    /// <summary>The straightening slider's reach either way.</summary>
    public const double MaxAngle = 45;

    public bool Sideways => ((Quarters % 4) + 4) % 4 is 1 or 3;

    public bool IsNone => ((Quarters % 4) + 4) % 4 == 0 && !FlipX && !FlipY && Angle == 0;

    /// <summary>The picture's size after the quarter turns.</summary>
    public (double W, double H) Turned(double width, double height) => Sideways ? (height, width) : (width, height);

    /// <summary>
    /// The size after straightening: the biggest rectangle of the turned picture's own shape that the
    /// tilted picture fills completely, so no empty corners show.
    /// </summary>
    public (double W, double H) Straightened(double width, double height)
    {
        var (w, h) = Turned(width, height);
        var s = InnerScale(w, h, Angle);
        return (w * s, h * s);
    }

    /// <summary>How much a w×h picture tilted by the angle shrinks so a same-shaped rectangle fits inside it.</summary>
    public static double InnerScale(double w, double h, double degrees)
    {
        if (w <= 0 || h <= 0)
            return 1;
        var a = Math.Abs(degrees) * Math.PI / 180;
        double c = Math.Cos(a), s = Math.Sin(a);
        // The kept rectangle turned back by the angle must fit inside the picture.
        return Math.Min(1, Math.Min(w / (w * c + h * s), h / (w * s + h * c)));
    }

    public ImageTurn TurnedRight() => this with { Quarters = (Quarters + 1) % 4 };

    public ImageTurn TurnedLeft() => this with { Quarters = (Quarters + 3) % 4 };

    /// <summary>Mirrored as the viewer sees it: left-right on screen, whichever way the picture is turned.</summary>
    public ImageTurn MirroredOnScreen() => Sideways ? this with { FlipY = !FlipY, Angle = -Angle } : this with { FlipX = !FlipX, Angle = -Angle };

    /// <summary>Mirrored top-bottom as the viewer sees it.</summary>
    public ImageTurn FlippedOnScreen() => Sideways ? this with { FlipX = !FlipX, Angle = -Angle } : this with { FlipY = !FlipY, Angle = -Angle };
}

/// <summary>Crop helpers for the picture editor, on top of <see cref="CropArea"/>'s fractions.</summary>
public static class CropMath
{
    /// <summary>The same area once the picture turns a quarter clockwise.</summary>
    public static CropArea TurnRight(CropArea a) => new(1 - a.Bottom, a.X, a.Height, a.Width);

    /// <summary>The same area once the picture turns a quarter counter-clockwise.</summary>
    public static CropArea TurnLeft(CropArea a) => new(a.Y, 1 - a.Right, a.Height, a.Width);

    public static CropArea MirrorX(CropArea a) => a with { X = 1 - a.Right };

    public static CropArea MirrorY(CropArea a) => a with { Y = 1 - a.Bottom };

    /// <summary>
    /// The biggest area of the given shape (width over height, in pixels) centered in a picture of
    /// that aspect, or around a given center as far as the picture allows.
    /// </summary>
    public static CropArea Largest(double ratio, double pictureAspect, double cx = 0.5, double cy = 0.5)
    {
        // In fractions, width/height = ratio / aspect.
        var r = ratio / pictureAspect;
        double w = 1, h = 1;
        if (r < 1)
            w = r;
        else
            h = 1 / r;
        return Centered(w, h, cx, cy);
    }

    /// <summary>
    /// The area brought to the shape, keeping its center and its size as near as fits: used when a
    /// ratio is picked for an existing crop or the same crop goes to a picture of another shape.
    /// </summary>
    public static CropArea Fit(CropArea a, double ratio, double pictureAspect)
    {
        var r = ratio / pictureAspect;
        var area = a.Width * a.Height;
        var w = Math.Sqrt(area * r);
        var h = w / r;
        var scale = Math.Min(1, Math.Min(1 / w, 1 / h));
        return Centered(w * scale, h * scale, a.X + a.Width / 2, a.Y + a.Height / 2);
    }

    /// <summary>
    /// An edge or corner drag held to the shape. A corner keeps the opposite corner still and follows
    /// whichever way the mouse moved more; an edge keeps the opposite edge still and grows the other
    /// side evenly about the middle. The area shrinks rather than leave the picture.
    /// </summary>
    public static CropArea DragHeld(CropArea a, CropEdges edges, double x, double y, double ratio, double pictureAspect)
    {
        var r = ratio / pictureAspect;
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        bool left = edges.HasFlag(CropEdges.Left), right = edges.HasFlag(CropEdges.Right);
        bool top = edges.HasFlag(CropEdges.Top), bottom = edges.HasFlag(CropEdges.Bottom);
        var min = CropArea.Smallest;

        if ((left || right) && (top || bottom))
        {
            var ax = left ? a.Right : a.X; // the corner that stays
            var ay = top ? a.Bottom : a.Y;
            var maxW = left ? ax : 1 - ax;
            var maxH = top ? ay : 1 - ay;
            var w = Math.Max(min, Math.Abs(x - ax));
            var h = Math.Max(min, Math.Abs(y - ay));
            if (w / h > r)
                h = w / r;
            else
                w = h * r;
            var fit = Math.Min(1, Math.Min(maxW / w, maxH / h));
            w *= fit;
            h *= fit;
            return new CropArea(left ? ax - w : ax, top ? ay - h : ay, w, h);
        }

        if (left || right)
        {
            var ax = left ? a.Right : a.X;
            var cy = a.Y + a.Height / 2;
            var w = Math.Max(min, left ? ax - x : x - ax);
            w = Math.Min(w, left ? ax : 1 - ax);
            var h = w / r;
            var maxH = 2 * Math.Min(cy, 1 - cy);
            if (h > maxH)
            {
                h = maxH;
                w = h * r;
            }
            return new CropArea(left ? ax - w : ax, cy - h / 2, w, h);
        }

        if (top || bottom)
        {
            var ay = top ? a.Bottom : a.Y;
            var cx = a.X + a.Width / 2;
            var h = Math.Max(min, top ? ay - y : y - ay);
            h = Math.Min(h, top ? ay : 1 - ay);
            var w = h * r;
            var maxW = 2 * Math.Min(cx, 1 - cx);
            if (w > maxW)
            {
                w = maxW;
                h = w / r;
            }
            return new CropArea(cx - w / 2, top ? ay - h : ay, w, h);
        }
        return a;
    }

    /// <summary>A new area drawn from one corner to the mouse, held to the shape when one is given.</summary>
    public static CropArea? Drawn(double x1, double y1, double x2, double y2, double ratio, double pictureAspect)
    {
        if (ratio <= 0)
            return CropArea.Between(x1, y1, x2, y2);
        var edges = (x2 < x1 ? CropEdges.Left : CropEdges.Right) | (y2 < y1 ? CropEdges.Top : CropEdges.Bottom);
        var seed = new CropArea(Math.Clamp(x1, 0, 1), Math.Clamp(y1, 0, 1), 0, 0);
        var held = DragHeld(seed, edges, x2, y2, ratio, pictureAspect);
        return held.Width < CropArea.Smallest * 1.5 && held.Height < CropArea.Smallest * 1.5 ? null : held;
    }

    private static CropArea Centered(double w, double h, double cx, double cy)
    {
        w = Math.Clamp(w, CropArea.Smallest, 1);
        h = Math.Clamp(h, CropArea.Smallest, 1);
        return new CropArea(Math.Clamp(cx - w / 2, 0, 1 - w), Math.Clamp(cy - h / 2, 0, 1 - h), w, h);
    }
}
