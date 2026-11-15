using System.Net.Http.Json;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jarvis.Tray;

/// <summary>
/// Local meeting transcription. While meeting mode is on, captures BOTH sides:
///  - "Them": WASAPI loopback of the default render device (everything the user hears)
///  - "Me":   the communications mic
/// Every ~30s each side's audio goes to the local Whisper sidecar (via the kernel's
/// /api/voice/transcribe) and lands in the active meeting's notes as timestamped lines.
/// 100% local — audio never leaves the machine and is discarded after transcription.
/// </summary>
public sealed class MeetingScribe(HttpClient http) : IDisposable
{
    private const int FlushSeconds = 30;
    private const int TargetRate = 16000;

    private readonly Lock _lock = new();
    private WasapiLoopbackCapture? _loopback;
    private WaveInEvent? _mic;
    private MemoryStream _themPcm = new();
    private MemoryStream _mePcm = new();
    private System.Threading.Timer? _flushTimer;
    private bool _running;

    public bool Running { get { lock (_lock) return _running; } }

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _themPcm = new MemoryStream();
            _mePcm = new MemoryStream();

            try
            {
                _loopback = new WasapiLoopbackCapture();   // default render device, mix format
                var src = _loopback.WaveFormat;
                _loopback.DataAvailable += (_, e) => OnLoopback(e, src);
                _loopback.StartRecording();
            }
            catch { _loopback = null; }   // no render device — mic-only

            try
            {
                _mic = new WaveInEvent { WaveFormat = new WaveFormat(TargetRate, 16, 1), BufferMilliseconds = 100 };
                _mic.DataAvailable += (_, e) => { lock (_lock) { if (_running) _mePcm.Write(e.Buffer, 0, e.BytesRecorded); } };
                _mic.StartRecording();
            }
            catch { _mic = null; }        // mic busy — loopback-only

            _flushTimer = new System.Threading.Timer(_ => _ = FlushAsync(false), null,
                TimeSpan.FromSeconds(FlushSeconds), TimeSpan.FromSeconds(FlushSeconds));
        }
    }

    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            timer = _flushTimer; _flushTimer = null;
            StopCapture(ref _loopback);
            StopMic(ref _mic);
        }
        timer?.Dispose();
        _ = FlushAsync(true);   // final partial chunk
    }

    private static void StopCapture(ref WasapiLoopbackCapture? cap)
    {
        var c = cap; cap = null;
        if (c is null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    private static void StopMic(ref WaveInEvent? mic)
    {
        var m = mic; mic = null;
        if (m is null) return;
        var stopped = new ManualResetEventSlim();
        m.RecordingStopped += (_, _) => stopped.Set();
        try { m.StopRecording(); } catch { stopped.Set(); }
        stopped.Wait(TimeSpan.FromSeconds(2));   // same native-AV race as WakeListener: wait, then free
        try { m.Dispose(); } catch { }
        stopped.Dispose();
    }

    /// <summary>Loopback arrives in the device mix format (usually 32-bit float stereo 44.1/48k)
    /// — downmix to mono and linear-resample to 16k int16 for Whisper.</summary>
    private void OnLoopback(WaveInEventArgs e, WaveFormat src)
    {
        if (src.Encoding != WaveFormatEncoding.IeeeFloat || src.BitsPerSample != 32) return;
        var channels = src.Channels;
        var frames = e.BytesRecorded / 4 / channels;
        if (frames == 0) return;

        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
            mono[f] = sum / channels;
        }
        var ratio = (double)src.SampleRate / TargetRate;
        var outFrames = (int)(frames / ratio);
        var pcm = new byte[outFrames * 2];
        for (var i = 0; i < outFrames; i++)
        {
            var v = mono[Math.Min(frames - 1, (int)(i * ratio))];
            var s = (short)(Math.Clamp(v, -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        lock (_lock) { if (_running) _themPcm.Write(pcm, 0, pcm.Length); }
    }

    private async Task FlushAsync(bool final)
    {
        byte[] them, me;
        lock (_lock)
        {
            them = _themPcm.ToArray(); _themPcm = new MemoryStream();
            me = _mePcm.ToArray(); _mePcm = new MemoryStream();
        }
        await TranscribeIntoNotesAsync("Them", them);
        await TranscribeIntoNotesAsync("Me", me);
        _ = final;
    }

    private async Task TranscribeIntoNotesAsync(string who, byte[] pcm)
    {
        try
        {
            if (pcm.Length < TargetRate) return;            // <0.5s — nothing
            if (Recorder.Peak(WrapWav(pcm)) < 250) return;  // silence — skip the STT round trip
            using var content = new ByteArrayContent(WrapWav(pcm));
            content.Headers.ContentType = new("audio/wav");
            var resp = await http.PostAsync("/api/voice/transcribe", content);
            var doc = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var text = doc.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return;
            await http.PostAsJsonAsync("/api/meetings/notes",
                new { text = $"[{DateTime.Now:HH:mm}] {who}: {text.Trim()}" });
        }
        catch { /* scribe is best-effort; one bad chunk must not kill the meeting */ }
    }

    private static byte[] WrapWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(ms), new WaveFormat(TargetRate, 16, 1)))
            w.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
