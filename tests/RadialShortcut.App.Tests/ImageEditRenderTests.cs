using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialShortcut.App.Tools;
using RadialShortcut.Core;
using Edit = RadialShortcut.App.Tools.ImageEditWindow.Edit;
using Format = RadialShortcut.App.Tools.ImageEditWindow.Format;

namespace RadialShortcut.App.Tests;

public class ImageEditRenderTests
{
    /// <summary>40×20: the left half red, the right half blue.</summary>
    private static BitmapSource Halves()
    {
        const int w = 40, h = 20;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                pixels[i + (x < w / 2 ? 2 : 0)] = 255; // BGRA
                pixels[i + 3] = 255;
            }
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        image.Freeze();
        return image;
    }

    private static Color At(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    private static bool Red(Color c) => c.R > 200 && c.B < 60;

    private static bool Blue(Color c) => c.B > 200 && c.R < 60;

    private static Edit Plain => new(default, null, 0, 1, 1, 0, Format.Png, 90);

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    [Fact]
    public void Untouched_keeps_size_and_sides() => OnSta(() =>
    {
        var result = ImageEditWindow.Render(Halves(), Plain, first: true);
        Assert.Equal((40, 20), (result.PixelWidth, result.PixelHeight));
        Assert.True(Red(At(result, 5, 10)));
        Assert.True(Blue(At(result, 35, 10)));
    });

    [Fact]
    public void Turning_right_puts_the_left_side_on_top() => OnSta(() =>
    {
        var result = ImageEditWindow.Render(Halves(), Plain with { Turn = new ImageTurn().TurnedRight() }, first: true);
        Assert.Equal((20, 40), (result.PixelWidth, result.PixelHeight));
        Assert.True(Red(At(result, 10, 5)));
        Assert.True(Blue(At(result, 10, 35)));
    });

    [Fact]
    public void Mirroring_swaps_the_sides() => OnSta(() =>
    {
        var result = ImageEditWindow.Render(Halves(), Plain with { Turn = new ImageTurn().MirroredOnScreen() }, first: true);
        Assert.True(Blue(At(result, 5, 10)));
        Assert.True(Red(At(result, 35, 10)));
    });

    [Fact]
    public void Crop_and_scale_keep_only_the_chosen_part() => OnSta(() =>
    {
        var edit = Plain with { Crop = new CropArea(0.5, 0, 0.5, 1), ScaleX = 0.5, ScaleY = 0.5 };
        var result = ImageEditWindow.Render(Halves(), edit, first: true);
        Assert.Equal((10, 10), (result.PixelWidth, result.PixelHeight));
        Assert.True(Blue(At(result, 0, 5)));
        Assert.True(Blue(At(result, 9, 5)));
    });

    [Fact]
    public void Long_side_preset_sets_the_longest_edge() => OnSta(() =>
    {
        var result = ImageEditWindow.Render(Halves(), Plain with { LongSide = 10 }, first: true);
        Assert.Equal((10, 5), (result.PixelWidth, result.PixelHeight));
    });

    [Fact]
    public void Original_ratio_follows_each_picture_of_a_batch() => OnSta(() =>
    {
        // The crop was drawn as a square on another picture; this 2:1 one keeps its own shape.
        var edit = Plain with { Crop = new CropArea(0.25, 0, 0.5, 1), Ratio = -1 };
        var result = ImageEditWindow.Render(Halves(), edit, first: false);
        Assert.Equal(2.0, (double)result.PixelWidth / result.PixelHeight, 1);
    });
}
