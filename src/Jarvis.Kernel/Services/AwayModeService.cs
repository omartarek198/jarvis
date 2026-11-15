using System.Diagnostics;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Supervises the away-from-desk daemon (~/.claude/tools/away_mode.py): a Python process that
/// turns the owner's inbox into a control channel for headless Claude sessions. JARVIS only starts
/// and stops it — the daemon owns the email loop. One instance at a time.
/// </summary>
public class AwayModeService(IEventBus bus, ExternalActions external, ILogger<AwayModeService> log)
{
    private static string ScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tools", "away_mode.py");

    private Process? _proc;
    private readonly Lock _lock = new();

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    public (bool ok, string spoken) Start(bool yolo = false)
    {
        lock (_lock)
        {
            if (!external.Enabled)
                return (false, "Away mode is switched off in this build: it emails you and runs headless agent sessions, " +
                               "so it needs Jarvis:ExternalActions:Enabled set to true.");
            if (_proc is { HasExited: false })
                return (false, "Away mode is already running. I'm watching your inbox.");
            if (!File.Exists(ScriptPath))
                return (false, "The away-mode script isn't installed.");

            // Resolve a Python launcher: "python" on PATH, else the Windows "py" launcher.
            var (exe, argsPrefix) = ResolvePython();
            if (exe is null)
                return (false, "I couldn't find Python to run the away-mode daemon.");

            var psi = new ProcessStartInfo(exe, $"{argsPrefix}\"{ScriptPath}\"{(yolo ? " --yolo" : "")}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ScriptPath)!,
            };
            try
            {
                _proc = Process.Start(psi);
                if (_proc is not null)
                {
                    _proc.EnableRaisingEvents = true;
                    _proc.Exited += OnDaemonExited;
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "away mode start failed");
                return (false, $"I couldn't start away mode: {ex.Message}");
            }
        }

        // Confirm it stayed up — a daemon that dies in the first second (bad creds, import
        // error) must not be reported as "engaged".
        Thread.Sleep(800);
        lock (_lock)
        {
            if (_proc is null || _proc.HasExited)
            {
                var code = _proc?.ExitCode;
                _proc = null;
                log.LogWarning("away mode daemon exited immediately (code {Code})", code);
                return (false, "Away mode failed to start — the daemon exited immediately. Check the away-mode log.");
            }
        }

        bus.Publish(new JarvisEvent("away.started", new { yolo, spoken = "" }, "voice"));
        log.LogInformation("away mode daemon started (pid {Pid}, yolo={Yolo})", _proc?.Id, yolo);
        return (true, "Away mode engaged. I've emailed you asking what to work on, and I'm watching your inbox. " +
                      "Email me any task and I'll spin up a session; say 'stop away mode' in an email or tell me when you're back.");
    }

    private void OnDaemonExited(object? sender, EventArgs e)
    {
        int? code = null;
        lock (_lock)
        {
            if (sender is not Process p || p != _proc) return;   // stale handler from an old process
            try { code = p.ExitCode; } catch { /* ignore */ }
            _proc = null;
        }
        // Code 0 = clean "stop away mode" by email; anything else is unexpected.
        if (code is 0 or null) return;
        log.LogWarning("away mode daemon exited unexpectedly (code {Code})", code);
        bus.Publish(new JarvisEvent("away.exited",
            new { code, spoken = "Heads up — the away-mode daemon stopped unexpectedly. I'm no longer watching your inbox." },
            "system"));
    }

    private static (string? Exe, string ArgsPrefix) ResolvePython()
    {
        foreach (var (exe, prefix) in new[] { ("python", ""), ("py", "-3 ") })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(exe, "--version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (probe is null) continue;
                probe.WaitForExit(3000);
                if (probe.HasExited && probe.ExitCode == 0) return (exe, prefix);
            }
            catch { /* try next */ }
        }
        return (null, "");
    }

    public (bool ok, string spoken) Stop()
    {
        lock (_lock)
        {
            if (_proc is null || _proc.HasExited)
            {
                _proc = null;
                return (false, "Away mode isn't running.");
            }
            try { _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _proc = null;
        }
        bus.Publish(new JarvisEvent("away.stopped", new { spoken = "" }, "voice"));
        log.LogInformation("away mode daemon stopped");
        return (true, "Away mode off. Welcome back.");
    }
}
