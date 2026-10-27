using System.Runtime.InteropServices;
using static RadialShortcut.Input.NativeMethods;

namespace RadialShortcut.Input;

internal static class InputSender
{
    /// <summary>Stamped on every event we inject so our own hook lets it through.</summary>
    public static readonly IntPtr Signature = 0x52534843; // "RSHC"

    public static void RightClick() =>
        Send(Button(MOUSEEVENTF_RIGHTDOWN), Button(MOUSEEVENTF_RIGHTUP));

    /// <summary>
    /// Presses the right button where the cursor is now. Jumping back to the original press point first
    /// made the cursor visibly stutter; the few pixels of difference do not matter to a drag.
    /// </summary>
    public static void RightDown() => Send(Button(MOUSEEVENTF_RIGHTDOWN));

    private static INPUT Button(uint flag) => new()
    {
        type = INPUT_MOUSE,
        mi = new MOUSEINPUT { dwFlags = flag, dwExtraInfo = Signature },
    };

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
