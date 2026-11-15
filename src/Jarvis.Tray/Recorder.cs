using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;

namespace Jarvis.Tray;

/// <summary>
/// Captures microphone audio (16 kHz mono PCM) into an in-memory WAV while PTT is held.
///
/// Session semantics for Bluetooth: opening/closing the mic flips a BT headset between
/// call-mode (HFP) and music-mode (A2DP), and that switch mid-reply audibly swallows audio.
/// So the wave-in SESSION stays open from PTT-down until the reply has finished playing
/// (plus a grace period) — buffers are simply discarded when not capturing.
/// </summary>
public sealed class Recorder
{
    private readonly Lock _lock = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private MemoryStream? _buffer;
    private bool _capturing;
    private string? _overrideDevice;   // set by dead-mic auto-fallback

    public bool IsCapturing { get { lock (_lock) return _capturing; } }

    /// <summary>Peak of the most recent audio chunk — cheap live VAD for conversation mode.</summary>
    public volatile int RecentPeak;
    public bool SessionOpen { get { lock (_lock) return _waveIn is not null; } }
    public string DeviceName { get; private set; } = "system default";

    /// <summary>
    /// The Windows *default* capture device can be a dead jack (observed: Realtek "Jack Mic" with zero
    /// signal while the real headset is only the *communications* default). Prefer the communications
    /// device — the one Teams uses. Override order: auto-fallback pick > JARVIS_MIC env > comms default.
    /// </summary>
    private int ResolveDeviceNumber()
    {
        var over = _overrideDevice ?? Environment.GetEnvironmentVariable("JARVIS_MIC");
        if (!string.IsNullOrWhiteSpace(over))
        {
            if (int.TryParse(over, out var idx) && idx >= -1 && idx < WaveInEvent.DeviceCount)
            { DeviceName = idx < 0 ? "system default" : WaveInEvent.GetCapabilities(idx).ProductName; return idx; }
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                if (WaveInEvent.GetCapabilities(i).ProductName.Contains(over, StringComparison.OrdinalIgnoreCase))
                { DeviceName = WaveInEvent.GetCapabilities(i).ProductName; return i; }
        }
        try
        {
            using var mm = new MMDeviceEnumerator();
            var comm = mm.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName.Trim();
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                // legacy API truncates names to 31 chars — prefix-match either way
                var prod = WaveInEvent.GetCapabilities(i).ProductName.Trim();
                if (comm.StartsWith(prod, StringComparison.OrdinalIgnoreCase) ||
                    prod.StartsWith(comm, StringComparison.OrdinalIgnoreCase))
                { DeviceName = prod; return i; }
            }
        }
        catch { /* fall through to mapper */ }
        DeviceName = "system default";
        return -1;
    }

    /// <summary>Resolve the capture device without recording (startup diagnostics).</summary>
    public string Probe() { ResolveDeviceNumber(); return DeviceName; }

    /// <summary>Permanently prefer a specific device for this tray session (dead-mic fallback).</summary>
    public void OverrideDevice(string nameOrIndex)
    {
        lock (_lock) _overrideDevice = nameOrIndex;
    }

    /// <summary>Open the wave-in session without capturing (keeps a BT headset in call-mode).</summary>
    public void BeginSession()
    {
        lock (_lock)
        {
            if (_waveIn is not null) return;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = ResolveDeviceNumber(),
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
        }
    }

    /// <summary>Close the wave-in session (lets a BT headset return to music-mode).</summary>
    public void EndSession()
    {
        lock (_lock)
        {
            if (_waveIn is null) return;
            _capturing = false;
            _waveIn.DataAvailable -= OnData;
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.Dispose(); _waveIn = null;
            _writer?.Dispose(); _writer = null;
            _buffer?.Dispose(); _buffer = null;
        }
    }

    public void StartCapture()
    {
        BeginSession();
        lock (_lock)
        {
            if (_capturing) return;
            _buffer = new MemoryStream();
            _writer = new WaveFileWriter(new IgnoreDisposeStream(_buffer), _waveIn!.WaveFormat);
            _capturing = true;
        }
    }

    /// <summary>Stop buffering and return the captured WAV. The session stays open.</summary>
    public byte[] StopCapture()
    {
        lock (_lock)
        {
            if (!_capturing) return [];
            _capturing = false;
            _writer!.Dispose(); _writer = null;
            var bytes = _buffer!.ToArray();
            _buffer.Dispose(); _buffer = null;
            return bytes;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var peak = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            var s = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
            if (s > peak) peak = s;
        }
        RecentPeak = peak;
        lock (_lock)
        {
            if (_capturing) _writer?.Write(e.Buffer, 0, e.BytesRecorded); // else: discard, session just holds the device
        }
    }

    /// <summary>Peak amplitude of 16-bit PCM wav bytes (skips the 44-byte header).</summary>
    public static int Peak(byte[] wav)
    {
        var peak = 0;
        for (var i = 44; i + 1 < wav.Length; i += 2)
        {
            var s = Math.Abs((int)BitConverter.ToInt16(wav, i));
            if (s > peak) peak = s;
        }
        return peak;
    }

    /// <summary>Record briefly from every capture device and return (index, name, peak) — loudest first.</summary>
    public static List<(int Index, string Name, int Peak)> MeterAllDevices(int millis = 400)
    {
        var results = new List<(int, string, int)>();
        for (var dev = 0; dev < WaveInEvent.DeviceCount; dev++)
        {
            var name = WaveInEvent.GetCapabilities(dev).ProductName;
            try
            {
                using var wi = new WaveInEvent { DeviceNumber = dev, WaveFormat = new WaveFormat(16000, 16, 1) };
                var peak = 0;
                wi.DataAvailable += (_, e) =>
                {
                    for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        var s = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
                        if (s > peak) peak = s;
                    }
                };
                wi.StartRecording();
                Thread.Sleep(millis);
                wi.StopRecording();
                results.Add((dev, name, peak));
            }
            catch
            {
                results.Add((dev, name, -1));
            }
        }
        return [.. results.OrderByDescending(r => r.Item3)];
    }
}
