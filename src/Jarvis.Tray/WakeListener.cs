using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

namespace Jarvis.Tray;

/// <summary>
/// Always-on "Jarvis" detection: a dedicated low-rate capture (DEFAULT device, separate from
/// the PTT Recorder's comms device) streams 16 kHz PCM to the openWakeWord sidecar at
/// ws://127.0.0.1:5592/ws and raises Detected on a hit. Reconnects forever — the sidecar may
/// start slower than the tray, die, or be restarted by the kernel. ~1% CPU.
/// Note: if the default mic is a Bluetooth headset, holding it open keeps it in call-mode —
/// set JARVIS_WAKE_MIC to a wired/array device, or turn the wake word off.
/// </summary>
public sealed class WakeListener : IDisposable
{
    public event Action<double>? Detected;

    /// <summary>Peak of the most recent ambient frame (0..32767) — feeds the console's MIC trace.</summary>
    public volatile int AmbientPeak;

    private static readonly string WsUrl =
        Environment.GetEnvironmentVariable("JARVIS_WAKE_WS") ?? "ws://127.0.0.1:5592/ws";

    private CancellationTokenSource? _cts;
    private readonly Lock _lock = new();

    public bool Running { get { lock (_lock) return _cts is not null; } }

    public void Start()
    {
        lock (_lock)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private static int ResolveDevice()
    {
        var pref = Environment.GetEnvironmentVariable("JARVIS_WAKE_MIC");
        if (!string.IsNullOrWhiteSpace(pref))
        {
            if (int.TryParse(pref, out var idx) && idx >= -1 && idx < WaveInEvent.DeviceCount) return idx;
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                if (WaveInEvent.GetCapabilities(i).ProductName.Contains(pref, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        return -1;   // system default
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WaveInEvent? waveIn = null;
            ClientWebSocket? ws = null;
            EventHandler<WaveInEventArgs>? onData = null;
            var stopped = new ManualResetEventSlim();
            try
            {
                ws = new ClientWebSocket();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(WsUrl), connectCts.Token);

                var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                    { FullMode = BoundedChannelFullMode.DropOldest });
                waveIn = new WaveInEvent
                {
                    DeviceNumber = ResolveDevice(),
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 80,
                };
                onData = (_, e) =>
                {
                    var peak = 0;
                    for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        var s = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
                        if (s > peak) peak = s;
                    }
                    AmbientPeak = peak;
                    frames.Writer.TryWrite(e.Buffer[..e.BytesRecorded]);
                };
                waveIn.DataAvailable += onData;
                waveIn.RecordingStopped += (_, _) => stopped.Set();
                waveIn.StartRecording();

                var sender = Task.Run(async () =>
                {
                    await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                        await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                }, ct);

                var buf = new byte[1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buf, 0, res.Count);
                    if (text.Contains("\"detected\""))
                    {
                        var score = 0.0;
                        var m = System.Text.RegularExpressions.Regex.Match(text, @"""score""\s*:\s*([\d.]+)");
                        if (m.Success) double.TryParse(m.Groups[1].Value, out score);
                        Detected?.Invoke(score);
                    }
                }
                await sender.ContinueWith(_ => { }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { /* sidecar down or mic busy — retry below */ }
            finally
            {
                // Disposing WaveInEvent while its record thread is mid buffer-reuse is a native
                // access violation (0xC0000005, uncatchable). Detach, stop, WAIT for the record
                // thread to confirm, then free the buffers.
                if (waveIn is not null)
                {
                    try { if (onData is not null) waveIn.DataAvailable -= onData; } catch { }
                    try { waveIn.StopRecording(); } catch { stopped.Set(); }
                    stopped.Wait(TimeSpan.FromSeconds(2));
                    try { waveIn.Dispose(); } catch { }
                }
                try { ws?.Dispose(); } catch { }
                stopped.Dispose();
            }
            if (!ct.IsCancellationRequested)
                try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => Stop();
}
