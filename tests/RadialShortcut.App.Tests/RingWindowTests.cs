using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RadialShortcut.App;
using RadialShortcut.Core;
using ShapePath = System.Windows.Shapes.Path;

namespace RadialShortcut.App.Tests;

public class RingWindowTests
{
    [Fact]
    public Task Close_waits_for_the_animation_even_when_the_dispatcher_stalls() => OnUiThread(async () =>
    {
        using var preview = new Preview(animate: true);
        var hidden = preview.Ring.CloseForActionAsync(0);
        Thread.Sleep(180); // A wall-clock delay can expire before WPF has rendered the fade.
        Assert.False(hidden.IsCompleted);
        Assert.True(await hidden.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, preview.Root.Opacity);
    });

    [Fact]
    public Task Reusing_the_menu_cancels_the_old_action_and_restores_visibility() => OnUiThread(async () =>
    {
        using var preview = new Preview(animate: true);
        var hidden = preview.Ring.CloseForActionAsync(0);
        preview.Ring.TakeForPreview(false);
        Assert.False(await hidden);
        await Task.Delay(140);
        Assert.Equal(1, preview.Root.Opacity);
    });

    [Fact]
    public Task Disposing_the_menu_cancels_the_pending_action() => OnUiThread(async () =>
    {
        using var preview = new Preview(animate: true);
        var hidden = preview.Ring.CloseForActionAsync(0);
        preview.Ring.Close();
        Assert.False(await hidden);
    });

    [Fact]
    public Task Disabled_motion_closes_without_waiting() => OnUiThread(async () =>
    {
        using var preview = new Preview(animate: false);
        var hidden = preview.Ring.CloseForActionAsync(0);
        Assert.True(hidden.IsCompletedSuccessfully);
        Assert.True(await hidden);
        Assert.Equal(0, preview.Root.Opacity);
    });

    [Fact]
    public Task Rapid_hover_leaves_only_the_last_slice_selected() => OnUiThread(async () =>
    {
        using var preview = new Preview(animate: true);
        for (var i = 0; i < 200; i++)
            preview.Ring.Highlight(i % 8);
        preview.Ring.Highlight(3);
        await Task.Delay(140);
        var highlights = preview.Canvas.Children.OfType<Canvas>().ToArray();
        Assert.Single(highlights, h => h.Opacity > 0);
        Assert.Equal(1, highlights[3].Opacity);
        preview.Ring.Highlight(-1);
        Assert.All(highlights, h => Assert.Equal(0, h.Opacity));
    });

    [Theory]
    [InlineData("broken icon path")]
    [InlineData("M0 no")]
    [InlineData("")]
    public Task Invalid_custom_icons_still_produce_a_usable_menu(string icon) => OnUiThread(() =>
    {
        var items = DefaultRing.For(8).Select(item => item with { Icon = icon }).ToArray();
        using var preview = new Preview(items: items);
        preview.Ring.Highlight(0);
        var line = (ShapePath)((Viewbox)preview.Canvas.Children.OfType<StackPanel>().First().Children[0]).Child;
        Assert.False(line.Data.IsEmpty());
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFFF00")]
    [InlineData("#22C55E")]
    [InlineData("#F59E0B")]
    [InlineData("#4D9AFF")]
    [InlineData("#000000")]
    public Task Light_theme_selection_has_readable_text_for_custom_accents(string accent) => OnUiThread(() =>
    {
        using var preview = new Preview(theme: "light", accent: accent);
        preview.Ring.Highlight(0);
        var label = preview.Canvas.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().Single();
        var foreground = ((SolidColorBrush)label.Foreground).Color;
        var overlay = (Color)ColorConverter.ConvertFromString(accent);
        var surface = Palette.Ring;
        foreach (var background in new[] { surface.Inner, surface.Outer })
        {
            var opacity = 0x32 / 255.0;
            var selected = Color.FromRgb(
                (byte)(background.R * (1 - opacity) + overlay.R * opacity),
                (byte)(background.G * (1 - opacity) + overlay.G * opacity),
                (byte)(background.B * (1 - opacity) + overlay.B * opacity));
            var light = Math.Max(Luminance(foreground), Luminance(selected));
            var dark = Math.Min(Luminance(foreground), Luminance(selected));
            Assert.True((light + 0.05) / (dark + 0.05) >= 4.5, $"Unreadable selection for {accent}");
        }
        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(RingLayouts))]
    public Task Icon_and_label_stacks_stay_inside_their_slices(int count, double scale) => OnUiThread(() =>
    {
        var items = DefaultRing.For(count).Select(item => item with { Label = "Çok uzun bir özel araç etiketi" }).ToArray();
        using var preview = new Preview(items: items, scale: scale);
        var contents = preview.Canvas.Children.OfType<StackPanel>().ToArray();
        var surfaces = preview.Canvas.Children.OfType<ShapePath>().ToArray();
        for (var i = 0; i < count; i++)
        {
            var content = contents[i];
            var bounds = content.TransformToAncestor(preview.Canvas).TransformBounds(new Rect(content.RenderSize));
            foreach (var corner in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
                Assert.True(surfaces[i].Data.FillContains(corner), $"Slice {i} overflows at scale {scale}");
        }
        return Task.CompletedTask;
    });

    public static IEnumerable<object[]> RingLayouts()
    {
        foreach (var scale in new[] { 0.8, 1.0, 1.4 })
        for (var count = RingCatalog.MinSlices; count <= RingCatalog.MaxSlices; count++)
            yield return [count, scale];
    }

    [Fact]
    public Task Null_theme_from_hand_edited_settings_falls_back_to_system() => OnUiThread(() =>
    {
        Palette.Theme = UserSettings.Parse("{\"theme\":null}").Theme;
        Assert.NotNull(Palette.Ring);
        return Task.CompletedTask;
    });

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Task OnUiThread(Func<Task> test)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await test(); finished.SetResult(); }
                catch (Exception error) { finished.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Preview : IDisposable
    {
        public RingWindow Ring { get; }
        public Grid Root { get; }
        public Canvas Canvas => (Canvas)Root.Children[1];
        private readonly Window _host;

        public Preview(bool animate = false, string theme = "dark", string? accent = null,
            IReadOnlyList<RingItem>? items = null, double scale = 1)
        {
            Palette.Theme = theme;
            var style = new UserSettings { RingScale = scale, RingAccent = accent }.RingStyle();
            Ring = new RingWindow(style, new GestureSettings(), items ?? DefaultRing.For(8), animate);
            Root = (Grid)Ring.TakeForPreview(false);
            _host = new Window
            {
                Content = Root, Width = style.Size, Height = style.Size,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            _host.Show();
            _host.UpdateLayout();
        }

        public void Dispose()
        {
            Ring.Close();
            _host.Close();
        }
    }
}
