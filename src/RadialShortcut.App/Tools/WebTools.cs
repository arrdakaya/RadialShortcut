using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using RadialShortcut.App.Capture;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// The two tools that send data over the internet. Each asks once before its first use.
/// </summary>
internal static class WebTools
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task LensAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;
        if (!await ConsentAsync("lens", T("Görselle ara", "Search by image"), T("Seçtiğin görüntü Google Lens'e gönderilecek.", "The region you picked is sent to Google Lens."), region.ScreenRect))
            return;

        // Uploading from here gives a results link bound to our own session, which the browser cannot open.
        // A local page that posts the image itself makes the upload belong to the browser's session.
        var page = Path.Combine(Path.GetTempPath(), "RadialShortcut-lens.html");
        File.WriteAllText(page, LensPage(Convert.ToBase64String(ToolRunner.Png(region.Image))));
        ToolRunner.CopyImage(region.Image);
        ToolRunner.OpenUrl(page);
    }

    /// <summary>
    /// Fills lens.google.com's own upload form with the image and submits it. If the browser refuses to
    /// set the file, the page says to paste the image (already on the clipboard) instead.
    /// </summary>
    private static string LensPage(string base64) => $$"""
        <!doctype html><meta charset="utf-8"><title>Google Lens</title>
        <body style="font:15px Segoe UI,sans-serif;margin:40px">
        <p id="msg">{{T("Google Lens'e gönderiliyor…", "Sending to Google Lens…")}}</p>
        <form id="f" method="post" enctype="multipart/form-data" action="https://lens.google.com/v3/upload?hl={{(English ? "en" : "tr")}}&ep=gsbubb&st={{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}">
        <input type="file" name="encoded_image" id="i" hidden></form>
        <script>
        try {
          const bytes = Uint8Array.from(atob("{{base64}}"), c => c.charCodeAt(0));
          const dt = new DataTransfer();
          dt.items.add(new File([bytes], "image.png", { type: "image/png" }));
          document.getElementById("i").files = dt.files;
          document.getElementById("f").submit();
        } catch (e) {
          document.getElementById("msg").innerHTML = '{{T("Görüntü panoya kopyalandı. <a href=\"https://lens.google.com/\">Google Lens</a> sayfasında Ctrl+V ile yapıştır.", "The image is on the clipboard. Paste it with Ctrl+V on <a href=\"https://lens.google.com/\">Google Lens</a>.")}}';
        }
        </script>
        """;

    public static async Task TranslateAsync()
    {
        if (await RegionPicker.PickAsync() is not { } region)
            return;
        if (await ToolRunner.RecognizeAsync(region) is not { } text)
            return;

        var settings = ToolRunner.Settings;
        var target = settings.TranslateTarget.ToUpperInvariant();
        var useDeepL = !string.IsNullOrWhiteSpace(settings.DeepLApiKey);
        if (!await ConsentAsync("translate", T("Ekrandan çevir", "Translate from screen"), T($"Okunan metin çeviri için {(useDeepL ? "DeepL'e" : "Google Çeviri'ye")} gönderilecek.", $"The text read from the screen is sent to {(useDeepL ? "DeepL" : "Google Translate")}."), region.ScreenRect))
            return;

        Func<string, Task<(string Text, string Source)>> translate = useDeepL
            ? t => DeepLAsync(settings.DeepLApiKey!, text, t == "EN" ? "EN-US" : t)
            : t => GoogleAsync(text, t);

        var (translated, source) = await translate(target);
        // Text already in the target language is turned into English instead, which is what the user wants then.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && target != "EN")
        {
            target = "EN";
            (translated, source) = await translate(target);
        }

        ResultCard.ForText($"{T("Çeviri", "Translation")} ({source.ToUpperInvariant()} → {target})", translated, region.ScreenRect)
            .WithButton(T("Orijinali kopyala", "Copy original"), () => ToolRunner.CopyText(text))
            .WithButton(T("Kopyala", "Copy"), () => ToolRunner.CopyText(translated), primary: true)
            .ShowNear();
    }

    /// <summary>
    /// Google Translate's keyless endpoint (the one its browser extensions use). Not an official API,
    /// so it may change; a DeepL key in config.json is the stable alternative.
    /// </summary>
    private static async Task<(string Text, string Source)> GoogleAsync(string text, string target)
    {
        using var form = new FormUrlEncodedContent([new("q", text)]);
        using var response = await Http.PostAsync($"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={target.ToLowerInvariant()}&dt=t", form);
        response.EnsureSuccessStatusCode();

        // [[["çeviri","original",...], ...], null, "en", ...]
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var translated = string.Concat(root[0].EnumerateArray().Select(part => part[0].GetString()));
        return (translated, root[2].GetString() ?? "?");
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
            throw new InvalidOperationException(T("DeepL anahtarı geçersiz (config.json → deepLApiKey).", "The DeepL key is invalid (config.json → deepLApiKey)."));
        if (response.StatusCode == (HttpStatusCode)456)
            throw new InvalidOperationException(T("DeepL aylık karakter sınırı doldu.", "The DeepL monthly character limit is used up."));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DeepLResponse>();
        var first = body?.Translations.FirstOrDefault() ?? throw new InvalidOperationException(T("DeepL boş yanıt verdi.", "DeepL returned an empty response."));
        return (first.Text, first.DetectedSourceLanguage);
    }

    private sealed record DeepLResponse([property: JsonPropertyName("translations")] List<DeepLTranslation> Translations);

    private sealed record DeepLTranslation(
        [property: JsonPropertyName("detected_source_language")] string DetectedSourceLanguage,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>Asks once per tool; the answer is kept in consent.json, so config.json stays as the user wrote it.</summary>
    internal static async Task<bool> ConsentAsync(string tool, string title, string what, Int32Rect anchor)
    {
        var granted = ConsentStore.Load(AppPaths.ConsentFile);
        if (granted.Contains(tool))
            return true;

        if (!await ResultCard.AskAsync(T($"{title}: internet izni", $"{title}: internet access"), T($"{what} Bu soru bir kez sorulur.", $"{what} You'll only be asked once."), T("İzin ver", "Allow"), anchor))
            return false;

        granted.Add(tool);
        ConsentStore.Save(AppPaths.ConsentFile, granted);
        return true;
    }
}
