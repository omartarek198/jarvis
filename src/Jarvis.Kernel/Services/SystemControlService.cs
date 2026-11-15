using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Volume / media-key / lock / open-app control via Win32. Works because the kernel runs as
/// the logged-in user — if it ever becomes a Windows service, SendInput and LockWorkStation
/// stop reaching the interactive desktop and this needs a tray-side relay instead.
/// </summary>
public class SystemControlService(ILogger<SystemControlService> log)
{
    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT = 0xB0;
    private const byte VK_MEDIA_PREV = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    private static void Tap(byte vk, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            keybd_event(vk, 0, 0, 0);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
        }
    }

    public void VolumeUp(int steps = 4) => Tap(VK_VOLUME_UP, steps);       // each tap = 2%
    public void VolumeDown(int steps = 4) => Tap(VK_VOLUME_DOWN, steps);
    public void MuteToggle() => Tap(VK_VOLUME_MUTE);
    public void PlayPause() => Tap(VK_MEDIA_PLAY_PAUSE);
    public void NextTrack() => Tap(VK_MEDIA_NEXT);
    public void PreviousTrack() => Tap(VK_MEDIA_PREV);

    /// <summary>Absolute volume without a CoreAudio dependency: floor to 0 then step up (2% per tap).</summary>
    public void SetVolumePercent(int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        Tap(VK_VOLUME_DOWN, 50);
        if (pct > 0) Tap(VK_VOLUME_UP, (int)Math.Round(pct / 2.0));
    }

    public bool Lock() => LockWorkStation();

    /// <summary>Curated speech-name → launch target. UseShellExecute resolves PATH, URIs and store apps.</summary>
    private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "notepad",
        ["calculator"] = "calc", ["calc"] = "calc",
        ["explorer"] = "explorer", ["file explorer"] = "explorer",
        ["terminal"] = "wt", ["windows terminal"] = "wt",
        ["vs code"] = "code", ["vscode"] = "code", ["visual studio code"] = "code", ["code"] = "code",
        ["teams"] = "msteams:", ["microsoft teams"] = "msteams:",
        ["outlook"] = "outlook",
        ["spotify"] = "spotify:",
        ["task manager"] = "taskmgr",
        ["settings"] = "ms-settings:",
    };

    public static IEnumerable<string> KnownApps => Apps.Keys;

    public bool TryOpenApp(string name, out string error)
    {
        error = "";
        if (!Apps.TryGetValue(name.Trim(), out var target))
        {
            error = $"I don't have {name} in my app list.";
            return false;
        }
        try
        {
            // .cmd shims (like VS Code's) need cmd /c when launched via shell execute
            var psi = target.EndsWith(':') || !target.Contains(' ')
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo("cmd", $"/c start \"\" {target}") { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning("open app {App} failed: {Msg}", name, ex.Message);
            error = $"Couldn't launch {name}: {ex.Message}";
            return false;
        }
    }
}
