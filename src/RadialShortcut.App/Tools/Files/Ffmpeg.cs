using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RadialShortcut.App.Tools;

/// <summary>Runs ffmpeg with progress. Arguments go through ArgumentList, so paths need no quoting.</summary>
internal static partial class Ffmpeg
{
    /// <param name="before">Options that must come before -i, such as a seek.</param>
    /// <param name="progress">0–100, once the length of the input is known. Called off the UI thread.</param>
    public static async Task RunAsync(string input, IEnumerable<string> args, string output, Action<double>? progress, CancellationToken cancel,
        IEnumerable<string>? before = null, TimeSpan? length = null)
    {
        var start = new ProcessStartInfo(ToolDeps.PathOf(ToolDeps.Ffmpeg))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in (IEnumerable<string>)["-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", .. before ?? [], "-i", input, .. args, output])
            start.ArgumentList.Add(a);

        using var process = Process.Start(start)!;
        var errors = new StringBuilder();
        var readErrors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (length is null && DurationLine().Match(line) is { Success: true } m)
                    length = TimeSpan.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (errors.Length < 8000)
                    errors.AppendLine(line);
            }
        });

        using var kill = cancel.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });

        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && length is { TotalSeconds: > 0 } total)
                progress?.Invoke(Math.Clamp(us / 1e6 / total.TotalSeconds * 100, 0, 100));
        }
        await process.WaitForExitAsync();
        await readErrors;

        if (cancel.IsCancellationRequested || process.ExitCode != 0)
        {
            ToolRunner.TryDelete(output);
            cancel.ThrowIfCancellationRequested();
            // The useful part of an ffmpeg error is its last lines.
            var last = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2);
            throw new InvalidOperationException("ffmpeg: " + string.Join(" ", last));
        }
    }

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+\.\d+)")]
    private static partial Regex DurationLine();
}
