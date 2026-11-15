using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Tray;

/// <summary>Types text into the foreground window via SendInput KEYEVENTF_UNICODE —
/// works regardless of keyboard layout, no SendKeys escaping games.</summary>
internal static class TextInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nuint dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x4;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static string ForegroundWindowTitle()
    {
        var sb = new StringBuilder(256);
        _ = GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    public static void Type(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } },
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
            });
        }
        // chunked: very long strings can overflow the input queue
        for (var i = 0; i < inputs.Count; i += 256)
        {
            var slice = inputs.Skip(i).Take(256).ToArray();
            _ = SendInput((uint)slice.Length, slice, Marshal.SizeOf<INPUT>());
            Thread.Sleep(8);
        }
    }
}
