using System.Diagnostics;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>Client for the local faster-whisper sidecar.</summary>
public class SttClient(IConfiguration config, ILogger<SttClient> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private string BaseUrl => config["Jarvis:Stt:BaseUrl"] ?? "http://127.0.0.1:5591";

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var r = await Http.GetAsync($"{BaseUrl}/health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<string?> TranscribeAsync(byte[] wav)
    {
        try
        {
            using var content = new ByteArrayContent(wav);
            content.Headers.ContentType = new("audio/wav");
            var resp = await Http.PostAsync($"{BaseUrl}/transcribe", content);
            if (!resp.IsSuccessStatusCode) { log.LogWarning("STT returned {Code}", resp.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            log.LogWarning("STT unavailable: {Msg}", ex.Message);
            return null;
        }
    }
}

/// <summary>Supervises the openWakeWord sidecar (voice/wake/server.py) — same contract as STT.</summary>
public class WakeSupervisor(ILogger<WakeSupervisor> log) : BackgroundService
{
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var script = Path.Combine(JarvisPaths.Voice, "wake", "server.py");
        if (!File.Exists(script)) { log.LogInformation("wake sidecar script not found — wake word disabled."); return; }

        var restarts = 0;
        while (!ct.IsCancellationRequested && restarts < 30)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                if (await HealthyAsync("http://127.0.0.1:5592/health"))
                {
                    log.LogInformation("adopting an already-running wake sidecar");
                    while (!ct.IsCancellationRequested && await HealthyAsync("http://127.0.0.1:5592/health"))
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;   // it died — fall through to spawn our own
                }
                var psi = new ProcessStartInfo("python", $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                _proc = Process.Start(psi);
                if (_proc is null) { log.LogError("failed to start wake sidecar"); return; }
                log.LogInformation("wake sidecar started (pid {Pid})", _proc.Id);
                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[wake] {Line}", e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[wake] {Line}", e.Data); };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                await _proc.WaitForExitAsync(ct);
                log.LogWarning("wake sidecar exited with code {Code}", _proc.ExitCode);
                if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1)) restarts = 0;  // healthy run — don't count
                restarts++;
                await Task.Delay(TimeSpan.FromSeconds(10), ct);  // outlive TIME_WAIT on the old port
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "wake supervisor error");
                restarts++;
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
        Kill();
    }

    public override Task StopAsync(CancellationToken ct) { Kill(); return base.StopAsync(ct); }

    /// <summary>A previous kernel's sidecar may still own the port (taskkill orphans children).
    /// If something healthy already serves it, adopt it instead of bind-storming.</summary>
    private static async Task<bool> HealthyAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetAsync(url)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }
}

/// <summary>Supervises the Kokoro neural-TTS sidecar (voice/kokoro/server.py) — same contract.</summary>
public class KokoroSupervisor(ILogger<KokoroSupervisor> log) : BackgroundService
{
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var script = Path.Combine(JarvisPaths.Voice, "kokoro", "server.py");
        var model = Path.Combine(JarvisPaths.Voice, "kokoro", "models", "kokoro-v1.0.onnx");
        var voices = Path.Combine(JarvisPaths.Voice, "kokoro", "models", "voices-v1.0.bin");
        if (!File.Exists(script) || !File.Exists(model) || !File.Exists(voices))
        { log.LogInformation("kokoro sidecar/model not present — neural TTS disabled (Piper/SAPI fallback)."); return; }

        var restarts = 0;
        while (!ct.IsCancellationRequested && restarts < 30)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                if (await HealthyAsync("http://127.0.0.1:5593/health"))
                {
                    log.LogInformation("adopting an already-running kokoro sidecar");
                    while (!ct.IsCancellationRequested && await HealthyAsync("http://127.0.0.1:5593/health"))
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                var psi = new ProcessStartInfo("python", $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                _proc = Process.Start(psi);
                if (_proc is null) { log.LogError("failed to start kokoro sidecar"); return; }
                log.LogInformation("kokoro sidecar started (pid {Pid})", _proc.Id);
                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[tts] {Line}", e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[tts] {Line}", e.Data); };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                await _proc.WaitForExitAsync(ct);
                log.LogWarning("kokoro sidecar exited with code {Code}", _proc.ExitCode);
                if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1)) restarts = 0;
                restarts++;
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "kokoro supervisor error");
                restarts++;
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
        Kill();
    }

    public override Task StopAsync(CancellationToken ct) { Kill(); return base.StopAsync(ct); }

    private static async Task<bool> HealthyAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetAsync(url)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }
}

/// <summary>Starts and supervises the Python faster-whisper sidecar (voice/stt/server.py).</summary>
public class SttSupervisor(ILogger<SttSupervisor> log) : BackgroundService
{
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var script = Path.Combine(JarvisPaths.Voice, "stt", "server.py");
        if (!File.Exists(script)) { log.LogWarning("STT sidecar script not found at {Path}; voice input disabled until it exists.", script); return; }

        var restarts = 0;
        while (!ct.IsCancellationRequested && restarts < 30)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                if (await HealthyAsync("http://127.0.0.1:5591/health"))
                {
                    log.LogInformation("adopting an already-running STT sidecar");
                    while (!ct.IsCancellationRequested && await HealthyAsync("http://127.0.0.1:5591/health"))
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                var psi = new ProcessStartInfo("python", $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                _proc = Process.Start(psi);
                if (_proc is null) { log.LogError("Failed to start STT sidecar"); return; }
                log.LogInformation("STT sidecar started (pid {Pid})", _proc.Id);

                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[stt] {Line}", e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[stt] {Line}", e.Data); };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                await _proc.WaitForExitAsync(ct);
                log.LogWarning("STT sidecar exited with code {Code}", _proc.ExitCode);
                if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1)) restarts = 0;  // healthy run — don't count
                restarts++;
                await Task.Delay(TimeSpan.FromSeconds(10), ct);  // outlive TIME_WAIT on the old port
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "STT supervisor error");
                restarts++;
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
        Kill();
    }

    public override Task StopAsync(CancellationToken ct) { Kill(); return base.StopAsync(ct); }

    /// <summary>A previous kernel's sidecar may still own the port (taskkill orphans children).
    /// If something healthy already serves it, adopt it instead of bind-storming.</summary>
    private static async Task<bool> HealthyAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetAsync(url)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }
}
