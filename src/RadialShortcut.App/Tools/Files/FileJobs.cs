using System.IO;
using System.Windows;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// Runs one piece of work per file with a progress badge and a stop button, then shows one card
/// for the whole batch. A file that fails does not stop the others.
/// </summary>
internal static class FileJobs
{
    /// <param name="work">Makes the output for one input and returns its path, or null when there was nothing to do.</param>
    public static async Task RunAsync(string tool, string verb, IReadOnlyList<string> inputs, Int32Rect anchor,
        Func<string, Action<double>, CancellationToken, Task<string?>> work)
    {
        using var cancel = new CancellationTokenSource();
        var badge = new Badge(anchor) { AtScreenTop = true }.WithStop(cancel.Cancel);
        badge.Show();

        var outputs = new List<string>();
        _extra = [];
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < inputs.Count && !cancel.IsCancellationRequested; i++)
            {
                var input = inputs[i];
                var prefix = inputs.Count > 1 ? $"{i + 1}/{inputs.Count} · " : "";
                badge.Text = $"{prefix}{verb}: {Path.GetFileName(input)}";
                try
                {
                    var output = await work(input, p => badge.Dispatcher.BeginInvoke(() => badge.Text = $"{prefix}{verb} %{p:0}"), cancel.Token);
                    if (output is not null)
                    {
                        outputs.Add(output);
                        ToolRunner.Remember(tool, output);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
                {
                    failures.Add($"{Path.GetFileName(input)}: {e.Message}");
                }
            }
        }
        finally
        {
            badge.Close();
        }
        var extra = _extra;
        _extra = null;

        if (outputs.Count == 0 && failures.Count == 0)
            return;

        var title = outputs.Count switch
        {
            0 => $"{verb}: olmadı",
            1 => $"{verb}: hazır",
            var n => $"{verb}: {n} dosya hazır",
        };
        var lines = outputs.Take(4).Select(o => $"{Path.GetFileName(o)} · {MediaKinds.Size(new FileInfo(o).Length)}")
            .Concat(failures.Take(3));
        if (cancel.IsCancellationRequested)
            lines = lines.Append("Durduruldu.");

        var card = ResultCard.ForMessage(title, string.Join("\n", lines), anchor);
        if (outputs.Count > 0)
        {
            var first = outputs[0];
            card.WithButton("Sil", () => ToolRunner.ConfirmDelete([.. outputs, .. extra], anchor));
            card.WithButton("Klasörde göster", () => ToolRunner.ShowInFolder(first));
            if (outputs.Count == 1)
                card.WithButton("Aç", () => ToolRunner.OpenUrl(first));
        }
        card.WithButton("Tamam", () => { }, primary: true).ShowNear();
    }

    private static List<string>? _extra;

    /// <summary>
    /// A file a job made besides the one it returns (the other pages of a PDF): it goes into the
    /// history and is deleted along with the rest from the card.
    /// </summary>
    public static void AlsoMade(string tool, string file)
    {
        _extra?.Add(file);
        ToolRunner.Remember(tool, file);
    }

    /// <summary>Next to the input when that folder can be written to, otherwise Documents\RadialShortcut\Dosyalar.</summary>
    public static string OutputFor(string input, string suffix, string extension)
    {
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var folder = Path.GetDirectoryName(input)!;
        if (!CanWrite(folder))
        {
            folder = AppPaths.FilesDirectory;
            Directory.CreateDirectory(folder);
        }
        return MediaKinds.FreeName(folder, name, extension);
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".rs-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
