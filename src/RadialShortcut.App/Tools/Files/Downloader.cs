using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Linkten indir: the link on the clipboard, fetched with yt-dlp into Documents\RadialShortcut\İndirilenler.
/// Public media only: no browser cookies are read, so nothing behind a login is reached.
/// </summary>
internal static partial class Downloader
{
    public static async Task RunAsync(Int32Rect anchor)
    {
        string? url = null;
        ToolRunner.Retry(() => url = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ResultCard.ForMessage("Linkten indir", "Önce bir video ya da ses linki kopyala (YouTube, X, Instagram, ...).", anchor).ShowNear();
            return;
        }

        var pick = await ResultCard.ChooseAsync("Linkten indir", Shorten(url), ["Video", "Sadece ses (MP3)"], anchor);
        if (pick < 0 || !await WebTools.ConsentAsync("download", "Linkten indir", "Link yt-dlp ile açılıp medya indirilecek.", anchor))
            return;
        if (!await ToolDeps.EnsureAsync(anchor, ToolDeps.YtDlp, ToolDeps.Ffmpeg))
            return;

        await UpdateIfOldAsync();

        string[] format = pick == 0
            ? ["-f", "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b", "--merge-output-format", "mp4"]
            : ["-x", "--audio-format", "mp3", "--audio-quality", "0"];
        await FileJobs.RunAsync("download", "İndiriliyor", [url], anchor, (link, progress, cancel) => DownloadAsync(link, format, progress, cancel));
    }

    private static async Task<string?> DownloadAsync(string url, string[] format, Action<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(AppPaths.DownloadDirectory);
        var start = Start();
        foreach (var a in (IEnumerable<string>)["--no-playlist", "--newline", "--no-mtime", "--restrict-filenames", "--windows-filenames",
                     "--ffmpeg-location", AppPaths.ToolsDirectory, "-P", AppPaths.DownloadDirectory, "-o", "%(title).120B.%(ext)s",
                     "--progress-template", "download:%(progress._percent_str)s", "--print", "after_move:filepath", .. format, "--", url])
            start.ArgumentList.Add(a);

        using var process = Process.Start(start)!;
        using var kill = cancel.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var errors = process.StandardError.ReadToEndAsync(cancel);

        string? file = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (Percent().Match(line) is { Success: true } m && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var p))
                progress(p);
            else if (File.Exists(line.Trim()))
                file = line.Trim();
        }
        await process.WaitForExitAsync();
        cancel.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 || file is null)
        {
            var last = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Contains("ERROR"));
            throw new InvalidOperationException(last?.Replace("ERROR: ", "") ?? "yt-dlp indiremedi.");
        }
        return file;
    }

    /// <summary>Sites change often and an old yt-dlp stops working; it updates itself (and checks its own signature).</summary>
    private static async Task UpdateIfOldAsync()
    {
        var exe = ToolDeps.PathOf(ToolDeps.YtDlp);
        if (DateTime.Now - File.GetLastWriteTime(exe) < TimeSpan.FromDays(7))
            return;
        var start = Start();
        start.ArgumentList.Add("-U");
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            File.SetLastWriteTime(exe, DateTime.Now); // try again in a week even when already current
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ProcessStartInfo Start()
    {
        var start = new ProcessStartInfo(ToolDeps.PathOf(ToolDeps.YtDlp))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        return start;
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)%")]
    private static partial Regex Percent();
}
