using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Captures the monitor hosting the foreground window for "what's on my screen" vision
/// questions. Screenshots may contain secrets: they live only in data/screenshots/
/// (gitignored), are never pushed anywhere, and old ones are purged on every new capture.
/// </summary>
[SupportedOSPlatform("windows")]
public class ScreenshotService(ILogger<ScreenshotService> log)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public string? Capture()
    {
        try
        {
            var dir = Path.Combine(JarvisPaths.Data, "screenshots");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.png"))   // purge: secrets shouldn't linger
                try { File.Delete(old); } catch { }

            var monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            var bounds = GetMonitorInfo(monitor, ref info)
                ? new Rectangle(info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top)
                : new Rectangle(0, 0, 1920, 1080);

            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            log.LogWarning("screenshot failed: {Msg}", ex.Message);
            return null;
        }
    }
}
