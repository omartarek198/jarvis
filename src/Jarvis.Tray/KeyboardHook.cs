using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Tray;

/// <summary>Global low-level keyboard hook tracking hold/release of the push-to-talk key.</summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly int _pttVk;
    private readonly LowLevelKeyboardProc _proc;
    private readonly IntPtr _hook;
    private bool _isDown;

    public event Action? PttDown;
    public event Action? PttUp;

    public KeyboardHook(int pttVk = 0xA3 /* VK_RCONTROL */)
    {
        _pttVk = pttVk;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam); // first field of KBDLLHOOKSTRUCT
            if (vk == _pttVk)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (!_isDown) { _isDown = true; PttDown?.Invoke(); }
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    if (_isDown) { _isDown = false; PttUp?.Invoke(); }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
