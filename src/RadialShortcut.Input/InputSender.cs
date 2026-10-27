using System.Runtime.InteropServices;
using static RadialShortcut.Input.NativeMethods;

namespace RadialShortcut.Input;

internal static class InputSender
{
    /// <summary>Stamped on every event we inject so our own hook lets it through.</summary>
    public static readonly IntPtr Signature = 0x52534843; // "RSHC"

    public static void RightClick() =>
        Send(Button(MOUSEEVENTF_RIGHTDOWN), Button(MOUSEEVENTF_RIGHTUP));

    /// <summary>Presses the right button where the user originally pressed, then moves on to where the cursor is now.</summary>
    public static void RightDownAt(ScreenPoint at, ScreenPoint current) =>
        Send(MoveTo(at), Button(MOUSEEVENTF_RIGHTDOWN), MoveTo(current));

    private static INPUT Button(uint flag) => new()
    {
        type = INPUT_MOUSE,
        mi = new MOUSEINPUT { dwFlags = flag, dwExtraInfo = Signature },
    };

    private static INPUT MoveTo(ScreenPoint p)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((p.X - left) * 65535.0 / (width - 1)),
                dy = (int)Math.Round((p.Y - top) * 65535.0 / (height - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                dwExtraInfo = Signature,
            },
        };
    }

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
