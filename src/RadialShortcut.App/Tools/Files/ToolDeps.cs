using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Programs the file tools need but the app does not ship: downloaded once, on first use, into
/// %LocalAppData%\RadialShortcut\tools, and checked against a SHA-256 before they are kept.
/// </summary>
internal static class ToolDeps
{
    /// <param name="Sha256">Pinned hash; null when it is read from <paramref name="SumsUrl"/> at download time.</param>
    /// <param name="ZipEntries">Files taken out of a zip download, by name; null for a plain file.</param>
    private sealed record Dep(string Id, string Title, string Size, string Url, string File, string? Sha256, string? SumsUrl = null, string[]? ZipEntries = null);

    public const string Ffmpeg = "ffmpeg", YtDlp = "yt-dlp", Silueta = "silueta";

    private static readonly Dep[] Deps =
    [
        new(Ffmpeg, "ffmpeg 9.0.2", "115 MB",
            "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip",
            "ffmpeg.exe", "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", ZipEntries: ["ffprobe.exe", "ffmpeg.exe"]),
        // yt-dlp has to keep up with the sites it reads, so it is never pinned: the hash comes from the release itself.
        new(YtDlp, "yt-dlp", "18 MB",
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
            "yt-dlp.exe", null, SumsUrl: "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"),
        new(Silueta, "arka plan modeli", "44 MB",
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/silueta.onnx",
            "silueta.onnx", "75da6c8d2f8096ec743d071951be73b4a8bc7b3e51d9a6625d63644f90ffeedb"),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string PathOf(string id) => Path.Combine(AppPaths.ToolsDirectory, Deps.Single(d => d.Id == id).File);

    /// <summary>
    /// True once every program is on disk, downloading the missing ones in one go after asking once.
    /// False when the user said no or cancelled; a failed download offers a retry first.
    /// </summary>
    public static async Task<bool> EnsureAsync(Int32Rect anchor, params string[] ids)
    {
        var missing = Deps.Where(d => ids.Contains(d.Id) && !File.Exists(Path.Combine(AppPaths.ToolsDirectory, d.File))).ToList();
        if (missing.Count == 0)
            return true;

        var list = string.Join(", ", missing.Select(d => $"{d.Title} ({d.Size})"));
        if (!await ResultCard.AskAsync("Gerekli araç indirilecek", $"Bu iş için bir kerelik indirme gerekiyor: {list}. Sonra internet gerekmez.", "İndir", anchor))
            return false;

        using var cancel = new CancellationTokenSource();
        var badge = new Badge(anchor) { AtScreenTop = true }.WithStop(cancel.Cancel);
        badge.Show();
        try
        {
            for (var i = 0; i < missing.Count; i++)
            {
                var dep = missing[i];
                var prefix = missing.Count > 1 ? $"{i + 1}/{missing.Count} · " : "";
                badge.Text = $"{prefix}{dep.Title} indiriliyor";
                await DownloadAsync(dep, p => badge.Dispatcher.BeginInvoke(() => badge.Text = $"{prefix}{dep.Title} indiriliyor %{p:0}"), cancel.Token);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            badge.Close();
            // Whatever finished stays; a retry only fetches what is still missing.
            return await ResultCard.AskAsync("İndirme tamamlanamadı", e.Message, "Tekrar dene", anchor)
                && await EnsureAsync(anchor, ids);
        }
        finally
        {
            badge.Close();
        }
    }

    private static async Task DownloadAsync(Dep dep, Action<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(AppPaths.ToolsDirectory);
        var part = Path.Combine(AppPaths.ToolsDirectory, dep.Id + ".part");

        var expected = dep.Sha256 ?? await ReadSumAsync(dep, cancel);
        try
        {
            using (var response = await Http.GetAsync(dep.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(part);
                var buffer = new byte[1 << 16];
                long done = 0, shown = -1;
                for (int read; (read = await source.ReadAsync(buffer, cancel)) > 0;)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0 && done * 100 / total != shown)
                        progress(shown = done * 100 / total);
                }
            }

            string actual;
            await using (var file = File.OpenRead(part))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{dep.Title}: indirilen dosya doğrulanamadı (SHA-256 tutmuyor), kullanılmadı.");

            if (dep.ZipEntries is null)
            {
                File.Move(part, Path.Combine(AppPaths.ToolsDirectory, dep.File), overwrite: true);
                return;
            }

            // The main file comes last in ZipEntries, so its presence means the whole set is there.
            using var zip = ZipFile.OpenRead(part);
            foreach (var name in dep.ZipEntries)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{dep.Title}: arşivde {name} yok.");
                entry.ExtractToFile(Path.Combine(AppPaths.ToolsDirectory, name), overwrite: true);
            }
        }
        finally
        {
            ToolRunner.TryDelete(part);
        }
    }

    /// <summary>"hash  yt-dlp.exe" lines, as yt-dlp publishes them next to each release.</summary>
    private static async Task<string> ReadSumAsync(Dep dep, CancellationToken cancel)
    {
        var sums = await Http.GetStringAsync(dep.SumsUrl, cancel);
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == dep.File)
                return parts[0];
        }
        throw new InvalidDataException($"{dep.Title}: yayının SHA-256 listesinde dosya bulunamadı.");
    }
}
