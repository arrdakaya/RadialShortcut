namespace RadialShortcut.App.Tools;

internal static class WindowTools
{
    /// <summary>Toggles "always on top" for the right-clicked window and says which way it went.</summary>
    public static void ToggleTopmost(IntPtr target, (int X, int Y) at)
    {
        if (target == IntPtr.Zero)
            return;

        var on = !Native.IsTopmost(target);
        var title = Native.WindowTitle(target);
        if (title.Length > 40)
            title = title[..40] + "…";

        if (!Native.SetTopmost(target, on))
        {
            ResultCard.Near(at, T("Değiştirilemedi", "Couldn't change it"), T("Bu pencere yönetici olarak çalışıyor olabilir.", "This window may be running as administrator.")).ShowToast(2400);
            return;
        }
        ResultCard.Near(at, on ? T("Üstte tutuluyor", "Kept on top") : T("Artık üstte değil", "No longer on top"), title).ShowToast();
    }
}
