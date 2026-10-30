using System.Windows;
using System.Windows.Media.Imaging;
using RadialShortcut.Core;
using Expression = RadialShortcut.Core.Expression;

namespace RadialShortcut.App.Tools;

/// <summary>Tools that work on the text selected in the right-clicked window: web search and calculate.</summary>
internal static class SelectionTools
{
    public static async Task SearchAsync(IntPtr target, (int X, int Y) at)
    {
        if (await GetSelectedTextAsync(target) is not { } text)
        {
            ResultCard.Near(at, T("Seçili metin yok", "No text selected"), T("Önce aramak istediğin metni seç, sonra halkadan Web ara'yı kullan.", "Select the text to search for first, then use Search from the ring.")).ShowToast(2600);
            return;
        }

        var query = text.Length > 500 ? text[..500] : text;
        var url = Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        ToolRunner.OpenUrl(url);
    }

    public static async Task CalculateAsync(IntPtr target, (int X, int Y) at)
    {
        if (await GetSelectedTextAsync(target) is not { } text)
        {
            ResultCard.Near(at, T("Seçili ifade yok", "No expression selected"), T("Örneğin 12*4+3 yazıp seç, sonra halkadan Hesapla'yı kullan.", "Type and select something like 12*4+3, then use Calc from the ring.")).ShowToast(2600);
            return;
        }

        if (!Expression.TryEvaluate(text, out var value))
        {
            ResultCard.Near(at, T("Hesaplanamadı", "Couldn't calculate"), T($"\"{Shorten(text)}\" bir aritmetik ifade değil.", $"\"{Shorten(text)}\" is not an arithmetic expression.")).ShowToast(2600);
            return;
        }

        var result = Expression.Format(value, text);
        ResultCard.ForText($"{Shorten(text)} =", result, new Int32Rect(at.X, at.Y, 1, 1))
            .WithButton(T("Kopyala", "Copy"), () => ToolRunner.CopyText(result))
            .WithButton(T("Yerine yaz", "Replace"), () => _ = PasteTextAsync(target, result), primary: true)
            .ShowNear();
    }

    /// <summary>
    /// Pastes text into a window through the clipboard and puts the user's clipboard back afterwards.
    /// A selection that is still there is replaced.
    /// </summary>
    internal static async Task PasteTextAsync(IntPtr target, string text)
    {
        var saved = SaveClipboard();
        ToolRunner.CopyText(text);
        Native.Focus(target);
        await Task.Delay(60);
        Native.SendPaste();
        // The target may read the clipboard late.
        await Task.Delay(400);
        RestoreClipboard(saved);
    }

    /// <summary>
    /// Copies the selection of a window via Ctrl+Insert and reads it back. Null when nothing was selected.
    /// The user's clipboard is put back afterwards.
    /// </summary>
    private static async Task<string?> GetSelectedTextAsync(IntPtr target)
    {
        if (target == IntPtr.Zero)
            return null;

        var saved = SaveClipboard();
        var before = Native.ClipboardSequence();
        Native.Focus(target);
        await Task.Delay(40);
        Native.SendCopy();

        for (var i = 0; i < 12 && Native.ClipboardSequence() == before; i++)
            await Task.Delay(25);
        if (Native.ClipboardSequence() == before)
            return null;

        string? text = null;
        ToolRunner.Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        RestoreClipboard(saved);
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;

    internal sealed record SavedClipboard(string? Text, BitmapSource? Image);

    /// <summary>Only text and images are kept; richer formats would need every format copied by hand.</summary>
    internal static SavedClipboard? SaveClipboard()
    {
        SavedClipboard? saved = null;
        try
        {
            ToolRunner.Retry(() => saved = Clipboard.ContainsText() ? new(Clipboard.GetText(), null)
                : Clipboard.ContainsImage() ? new(null, Clipboard.GetImage()) : null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        return saved;
    }

    internal static void RestoreClipboard(SavedClipboard? saved)
    {
        try
        {
            if (saved?.Text is { } text)
                ToolRunner.CopyText(text);
            else if (saved?.Image is { } image)
                ToolRunner.CopyImage(image);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
