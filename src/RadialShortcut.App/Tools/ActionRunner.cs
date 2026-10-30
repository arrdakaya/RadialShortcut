using System.Diagnostics;
using RadialShortcut.Core;

namespace RadialShortcut.App.Tools;

/// <summary>
/// User-defined slices from config.json. RingCatalog has already checked that
/// each action has what it needs. Errors surface through ToolRunner's card and log.
/// </summary>
internal static class ActionRunner
{
    public static async Task RunAsync(SliceAction action, IntPtr target)
    {
        switch (action.Type)
        {
            case "launch":
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(action.Path!))
                {
                    Arguments = action.Args ?? "",
                    UseShellExecute = true,
                });
                break;
            case "url":
                ToolRunner.OpenUrl(action.Url!);
                break;
            case "hotkey":
                KeyChord.TryParse(action.Keys, out var keys);
                // The shortcut must reach the window that was right-clicked, not whatever had focus.
                Native.Focus(target);
                await Task.Delay(60);
                Native.SendKeys(keys);
                break;
            case "text":
                await SelectionTools.PasteTextAsync(target, action.Text!);
                break;
        }
    }
}
