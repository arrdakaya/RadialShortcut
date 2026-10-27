using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using RadialShortcut.App.Tools;

namespace RadialShortcut.App;

/// <summary>
/// Updates from the GitHub releases of "updateRepo" in config.json. A release carries
/// RadialShortcut-win-x64.zip and RadialShortcut-win-x64.zip.sha256; the zip is used only when
/// its hash matches. The old folder is kept as "…backup" so a bad update can be undone by hand.
/// </summary>
internal static class Updater
{
    private const string Asset = "RadialShortcut-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    public sealed record Release(Version Version, string ZipUrl, string ShaUrl);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url);

    /// <summary>The latest release when it is newer than this build; null otherwise or when GitHub cannot be reached.</summary>
    public static async Task<Release?> CheckAsync(string repo)
    {
        try
        {
            var latest = await Http.GetFromJsonAsync<GitHubRelease>($"https://api.github.com/repos/{repo}/releases/latest");
            if (latest is null || latest.Draft || latest.Prerelease || !Version.TryParse(latest.Tag.TrimStart('v', 'V'), out var version) || version <= Current)
                return null;
            var zip = latest.Assets.FirstOrDefault(a => a.Name == Asset);
            var sha = latest.Assets.FirstOrDefault(a => a.Name == Asset + ".sha256");
            return zip is null || sha is null ? null : new Release(version, zip.Url, sha.Url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads and checks the release, waits for running tools to finish, then hands over to a
    /// small script that swaps the folders once this process has exited and starts the new version.
    /// </summary>
    public static async Task InstallAsync(Release release, Action<string> status)
    {
        var work = Path.Combine(Path.GetTempPath(), "RadialShortcut-update");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        status("Güncelleme indiriliyor");
        var zip = Path.Combine(work, Asset);
        await using (var source = await Http.GetStreamAsync(release.ZipUrl))
        await using (var target = File.Create(zip))
            await source.CopyToAsync(target);

        var expected = (await Http.GetStringAsync(release.ShaUrl)).Split(' ', '\t', '\n')[0].Trim();
        string actual;
        await using (var file = File.OpenRead(zip))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("İndirilen güncelleme doğrulanamadı (SHA-256 tutmuyor), kurulmadı.");

        var fresh = Path.Combine(work, "new");
        ZipFile.ExtractToDirectory(zip, fresh);
        if (!File.Exists(Path.Combine(fresh, "RadialShortcut.exe")))
            throw new InvalidDataException("Güncelleme paketinde RadialShortcut.exe yok.");

        // A recording or a conversion in progress finishes first.
        while (ToolRunner.IsBusy)
        {
            status("Açık araç bitince güncellenecek");
            await Task.Delay(1000);
        }

        var app = AppContext.BaseDirectory.TrimEnd('\\');
        var backup = app + ".backup";
        var script = Path.Combine(work, "swap.cmd");
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul && (timeout /t 1 >nul & goto wait)
            if exist "{backup}" rmdir /s /q "{backup}"
            move "{app}" "{backup}" || goto fail
            move "{fresh}" "{app}" || (move "{backup}" "{app}" & goto fail)
            start "" "{app}\RadialShortcut.exe"
            exit /b 0
            :fail
            start "" "{app}\RadialShortcut.exe"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = work });
        System.Windows.Application.Current.Shutdown();
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RadialShortcut/{Current}");
        return http;
    }
}
