using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// The two tools that send data over the internet (PLAN.md, section 7). Each asks once before its first use.
/// </summary>
internal static partial class WebTools
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task LensAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;
        if (!await ConsentAsync("lens", "Görselle ara", "Seçtiğin görüntü Google Lens'e gönderilecek.", region.ScreenRect))
            return;

        string? url = null;
        try
        {
            url = await UploadToLensAsync(ToolRunner.Png(region.Image));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }

        if (url is not null)
        {
            ToolRunner.OpenUrl(url);
            return;
        }

        // The upload address is not an official API and may change; this path always works.
        ToolRunner.CopyImage(region.Image);
        ToolRunner.OpenUrl("https://lens.google.com/");
        ResultCard.ForMessage("Görüntü panoya kopyalandı", "Google Lens açıldı; sayfada Ctrl+V ile yapıştır.", region.ScreenRect).ShowNear();
    }

    /// <summary>
    /// Posts the image the way lens.google.com's own upload form does. Google answers with a redirect
    /// to the results page, or with a page that contains its address.
    /// </summary>
    private static async Task<string?> UploadToLensAsync(byte[] png)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "encoded_image", "image.png");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://lens.google.com/v3/upload?hl=tr&re=df&st={stamp}&ep=gsbubb") { Content = form };
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        using var response = await Http.SendAsync(request);

        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            return new Uri(new Uri("https://lens.google.com/"), location).AbsoluteUri;
        if (!response.IsSuccessStatusCode)
            return null;

        var match = LensResultUrl().Match(await response.Content.ReadAsStringAsync());
        return match.Success ? WebUtility.HtmlDecode(Regex.Unescape(match.Value)) : null;
    }

    [GeneratedRegex(@"https://lens\.google\.com/search\?[^""'\s<>]+")]
    private static partial Regex LensResultUrl();

    public static async Task TranslateAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;
        if (await ToolRunner.RecognizeAsync(region) is not { } text)
            return;

        var settings = ToolRunner.Settings;
        var target = settings.TranslateTarget.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(settings.DeepLApiKey))
        {
            if (!await ConsentAsync("translate", "Ekrandan çevir", "Okunan metin Google Çeviri'de açılacak.", region.ScreenRect))
                return;
            var limited = text.Length > 4000 ? text[..4000] : text;
            ToolRunner.OpenUrl($"https://translate.google.com/?sl=auto&tl={target.ToLowerInvariant()}&op=translate&text={Uri.EscapeDataString(limited)}");
            return;
        }

        if (!await ConsentAsync("translate", "Ekrandan çevir", "Okunan metin çeviri için DeepL'e gönderilecek.", region.ScreenRect))
            return;

        var (translated, source) = await DeepLAsync(settings.DeepLApiKey, text, target);
        // Text already in the target language is turned into English instead, which is what the user wants then.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && target != "EN")
            (translated, source) = await DeepLAsync(settings.DeepLApiKey, text, "EN-US");

        ResultCard.ForText($"Çeviri ({source} → {target})", translated, region.ScreenRect)
            .WithButton("Orijinali kopyala", () => ToolRunner.CopyText(text))
            .WithButton("Kopyala", () => ToolRunner.CopyText(translated), primary: true)
            .ShowNear();
    }

    private static async Task<(string Text, string Source)> DeepLAsync(string key, string text, string target)
    {
        var host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate")
        {
            Content = JsonContent.Create(new { text = new[] { text }, target_lang = target }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        using var response = await Http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("DeepL anahtarı geçersiz (config.json → deepLApiKey).");
        if (response.StatusCode == (HttpStatusCode)456)
            throw new InvalidOperationException("DeepL aylık karakter sınırı doldu.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DeepLResponse>();
        var first = body?.Translations.FirstOrDefault() ?? throw new InvalidOperationException("DeepL boş yanıt verdi.");
        return (first.Text, first.DetectedSourceLanguage);
    }

    private sealed record DeepLResponse([property: JsonPropertyName("translations")] List<DeepLTranslation> Translations);

    private sealed record DeepLTranslation(
        [property: JsonPropertyName("detected_source_language")] string DetectedSourceLanguage,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>Asks once per tool; the answer is kept in config.json.</summary>
    private static async Task<bool> ConsentAsync(string tool, string title, string what, Int32Rect anchor)
    {
        var settings = ToolRunner.Settings;
        if (settings.InternetConsent.Contains(tool))
            return true;

        if (!await ResultCard.AskAsync($"{title}: internet izni", $"{what} Bu soru bir kez sorulur.", "İzin ver", anchor))
            return false;

        settings.InternetConsent.Add(tool);
        settings.Save(AppPaths.ConfigFile);
        return true;
    }
}
