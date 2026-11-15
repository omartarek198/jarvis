using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Local text-to-speech — the single choke point all spoken audio flows through.
/// Profile-driven (voice/profiles.json): each profile picks engine, voice, speed, chunking,
/// formatter, mastering and caching. Fallback chain never crashes the kernel:
///   kokoro (neural sidecar :5593) → piper (local VITS) → Windows SAPI → silence (text-only).
/// Long replies are chunked per sentence/paragraph and stitched with fixed pauses; short
/// system phrases are served instantly from voice/cache/&lt;profile&gt;/.
/// </summary>
public class TtsService(IServiceScopeFactory scopes, ILogger<TtsService> log, IEventBus bus)
{
    private static string PiperExe => Path.Combine(JarvisPaths.Voice, "tts", "piper", "piper.exe");
    private static string VoicesDir => Path.Combine(JarvisPaths.Voice, "tts", "voices");
    private static string CacheRoot => Path.Combine(JarvisPaths.Voice, "cache");
    private static string PhrasesFile => Path.Combine(JarvisPaths.Voice, "phrases.json");

    private readonly VoiceProfileStore _profiles = new(log);

    /// <summary>Active profile name (canonical — aliases like legacy "jarvis" resolve on load).</summary>
    public string CurrentProfile { get; private set; } = "default";

    public IReadOnlyList<string> ProfileNames => _profiles.Names;
    public string? ResolveProfileName(string? name) => _profiles.ResolveName(name);
    public VoiceProfile GetProfile(string? name) => _profiles.Get(name);

    public async Task LoadProfileAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var s = await db.Settings.FindAsync("voice.profile");
        if (s is not null) CurrentProfile = _profiles.ResolveName(s.ValueJson.Trim('"')) ?? "default";
    }

    public async Task SetProfileAsync(string profile)
    {
        CurrentProfile = _profiles.ResolveName(profile) ?? profile.ToLowerInvariant();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var s = await db.Settings.FindAsync("voice.profile");
        if (s is null) db.Settings.Add(new Setting { Key = "voice.profile", ValueJson = $"\"{CurrentProfile}\"" });
        else s.ValueJson = $"\"{CurrentProfile}\"";
        await db.SaveChangesAsync();
    }

    /// <summary>The CONFIGURED engine preference of the active profile (availability-checked for piper).</summary>
    public string Engine
    {
        get
        {
            var p = _profiles.Get(CurrentProfile);
            if (p.Engine == "kokoro") return "kokoro";
            return FindVoiceModel(p.PiperVoices) is not null && File.Exists(PiperExe) ? "piper" : "sapi";
        }
    }

    /// <summary>The engine that ACTUALLY produced the last reply — a silent fallback can't hide behind config.</summary>
    public string LastEngineUsed { get; private set; } = "none";

    public Task<byte[]> SynthesizeAsync(string text) => SynthesizeAsync(text, null);

    /// <summary>Synthesize with an explicit profile override (e.g. "alert" for confirm prompts,
    /// "briefing" forced by a caller). Null = active profile + length-based auto-escalation.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, string? profileOverride)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var profile = _profiles.Get(profileOverride ?? CurrentProfile);
        if (profileOverride is null && profile.AutoEscalate && text.Length > _profiles.Selection.BriefingMinChars)
        {
            var briefing = _profiles.ResolveName(_profiles.Selection.BriefingProfile);
            if (briefing is not null && briefing != profile.Name)
            {
                log.LogInformation("[voice] {Len} chars — escalating {From} → {To}", text.Length, profile.Name, briefing);
                profile = _profiles.Get(briefing);
            }
        }

        var formatted = profile.Formatter
            ? SpeechTextFormatter.Format(text, profile.MaxSpokenChars, _profiles.Lexicon)
            : System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ").Trim();
        if (formatted.Length == 0) return [];

        var sw = Stopwatch.StartNew();

        // Cached system phrases: instant + consistent. Long/private replies are never cached.
        var cacheEligible = profile.Cache && formatted.Length <= _profiles.CacheMaxChars;
        if (cacheEligible && TryGetCached(profile, formatted) is { } hit)
        {
            LastEngineUsed = "cache";
            PublishSynthEvent(profile, "cache", cached: true, sw.ElapsedMilliseconds, formatted.Length);
            return hit;
        }

        // Engine chain — each step degrades gracefully, the kernel never throws over audio.
        byte[]? wav = null;
        var engineUsed = "none";

        if (profile.Engine == "kokoro")
        {
            wav = await KokoroSynthAsync(formatted, profile);
            if (wav is not null) engineUsed = "kokoro";
            else log.LogWarning("[voice] kokoro unavailable/failed for profile {Profile} — falling back to piper", profile.Name);
        }

        if (wav is null && FindVoiceModel(profile.PiperVoices) is { } model && File.Exists(PiperExe))
        {
            try
            {
                wav = await PiperSynthSentencesAsync(formatted, model, profile);
                engineUsed = "piper";
            }
            catch (Exception ex) { log.LogWarning("[voice] piper failed ({Msg}) — falling back to SAPI", ex.Message); }
        }

        if (wav is null)
        {
            try
            {
                wav = SapiSynth(formatted.Replace('\n', ' '));
                engineUsed = wav.Length > 0 ? "sapi" : "none";
            }
            catch (Exception ex)
            {
                log.LogError("[voice] all TTS engines failed ({Msg}) — reply is text-only", ex.Message);
                wav = [];
            }
        }

        LastEngineUsed = engineUsed;
        if (engineUsed == "none")
            log.LogError("[voice] no engine produced audio for profile {Profile} — reply is text-only", profile.Name);

        // Only cache audio rendered by the profile's PRIMARY engine — a one-off SAPI fallback
        // must not become the permanent cached sound of "Done."
        if (cacheEligible && engineUsed == profile.Engine && wav.Length > 44)
            StoreCached(profile, formatted, wav);

        PublishSynthEvent(profile, engineUsed, cached: false, sw.ElapsedMilliseconds, formatted.Length);
        return wav;
    }

    private void PublishSynthEvent(VoiceProfile profile, string engine, bool cached, long ms, int chars)
    {
        try
        {
            bus.Publish(new JarvisEvent("voice.tts",
                new { profile = profile.Name, voice = profile.Voice, engine, cached, ms, chars }, "kernel"));
        }
        catch { /* telemetry only */ }
    }

    // ---------------------------------------------------------------- kokoro

    private static readonly HttpClient KokoroHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Kokoro neural sidecar with per-chunk synthesis and deterministic pause stitching.
    /// Null = sidecar unavailable or any chunk failed → caller falls through to Piper/SAPI.</summary>
    private async Task<byte[]?> KokoroSynthAsync(string text, VoiceProfile p)
    {
        var chunks = SpeechChunker.Split(text, p.Chunking);
        if (chunks.Count == 0) return null;

        try
        {
            if (chunks.Count == 1)
            {
                var wav = await KokoroPostAsync(chunks[0].Text, p);
                if (wav is null) return null;
                if (!p.Mastering) return wav;
                var (pcm, rate) = AudioPcm.ExtractPcm(wav);
                return AudioPcm.BuildWav(AudioMastering.Master(pcm, rate), rate);
            }

            var clips = new List<(byte[] Pcm, int GapAfterMs)>();
            var sampleRate = 0;
            foreach (var chunk in chunks)
            {
                var wav = await KokoroPostAsync(chunk.Text, p);
                if (wav is null) return null;   // partial audio is worse than a clean fallback
                var (pcm, rate) = AudioPcm.ExtractPcm(wav);
                sampleRate = rate;
                clips.Add((AudioPcm.TrimSilence(pcm, rate), chunk.GapAfterMs));
            }
            var stitched = AudioPcm.Stitch(clips, sampleRate, p.Chunking.LeadInMs, p.Chunking.TailMs);
            if (p.Mastering) stitched = AudioMastering.Master(stitched, sampleRate);
            return AudioPcm.BuildWav(stitched, sampleRate);
        }
        catch (Exception ex)
        {
            log.LogWarning("[voice] kokoro pipeline error: {Msg}", ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> KokoroPostAsync(string text, VoiceProfile p)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { text, voice = p.Voice, speed = p.Speed });
            var resp = await KokoroHttp.PostAsync("http://127.0.0.1:5593/synthesize",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;
            var wav = await resp.Content.ReadAsByteArrayAsync();
            return wav.Length > 44 ? wav : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- cache

    private static string CachePath(VoiceProfile p, string formattedText)
    {
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(formattedText)))[..16].ToLowerInvariant();
        return Path.Combine(CacheRoot, p.CacheKey, sha + ".wav");
    }

    private byte[]? TryGetCached(VoiceProfile p, string formattedText)
    {
        try
        {
            var path = CachePath(p, formattedText);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length > 44 ? bytes : null;
        }
        catch { return null; }
    }

    private void StoreCached(VoiceProfile p, string formattedText, byte[] wav)
    {
        try
        {
            var path = CachePath(p, formattedText);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, wav);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { log.LogWarning("[voice] phrase cache write failed: {Msg}", ex.Message); }
    }

    /// <summary>Wipe voice/cache and re-render every phrase in voice/phrases.json for every
    /// cache-enabled profile. Returns (cached, attempted).</summary>
    public async Task<(int Cached, int Attempted)> RegenerateCacheAsync()
    {
        try { if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, recursive: true); }
        catch (Exception ex) { log.LogWarning("[voice] cache wipe failed: {Msg}", ex.Message); }

        var phrases = LoadPhrases();
        int cached = 0, attempted = 0;
        foreach (var profile in _profiles.All.Where(p => p.Cache))
        {
            foreach (var phrase in phrases)
            {
                attempted++;
                await SynthesizeAsync(phrase, profile.Name);
                if (LastEngineUsed == profile.Engine) cached++;
            }
        }
        log.LogInformation("[voice] phrase cache regenerated: {Cached}/{Attempted}", cached, attempted);
        return (cached, attempted);
    }

    private string[] LoadPhrases()
    {
        try
        {
            if (!File.Exists(PhrasesFile)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(PhrasesFile), new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return [.. doc.RootElement.GetProperty("phrases").EnumerateArray()
                .Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>()];
        }
        catch (Exception ex)
        {
            log.LogWarning("[voice] phrases.json invalid: {Msg}", ex.Message);
            return [];
        }
    }

    // ---------------------------------------------------------------- piper

    /// <summary>
    /// Piper's VITS voices sample pause durations stochastically (noise_w), so a sentence
    /// boundary occasionally renders as ~1s of dead air mid-reply. Synthesize per sentence,
    /// trim each clip, and stitch with the profile's fixed gaps so pacing is deterministic.
    /// </summary>
    private async Task<byte[]> PiperSynthSentencesAsync(string text, string model, VoiceProfile p)
    {
        // Force one sentence per chunk — piper's mid-chunk pauses are the very bug this fixes
        var perSentence = new VoiceChunking
        {
            Enabled = true,
            MinSplitChars = 0,
            MaxChunkChars = 1,
            SentenceGapMs = p.Chunking.SentenceGapMs,
            ParagraphGapMs = p.Chunking.ParagraphGapMs,
            LeadInMs = p.Chunking.LeadInMs,
            TailMs = p.Chunking.TailMs,
        };
        var chunks = SpeechChunker.Split(text, perSentence);
        if (chunks.Count == 0) return [];

        var clips = new List<(byte[] Pcm, int GapAfterMs)>();
        var sampleRate = 0;
        foreach (var chunk in chunks)
        {
            var wav = await PiperSynthAsync(chunk.Text, model);
            var (pcm, rate) = AudioPcm.ExtractPcm(wav);
            sampleRate = rate;
            clips.Add((AudioPcm.TrimSilence(pcm, rate), chunk.GapAfterMs));
        }

        var stitched = AudioPcm.Stitch(clips, sampleRate, perSentence.LeadInMs, perSentence.TailMs);
        if (p.Mastering) stitched = AudioMastering.Master(stitched, sampleRate);
        return AudioPcm.BuildWav(stitched, sampleRate);
    }

    private static string? FindVoiceModel(string[] prefixes)
    {
        if (!Directory.Exists(VoicesDir)) return null;
        var voices = Directory.GetFiles(VoicesDir, "*.onnx");
        if (voices.Length == 0) return null;
        foreach (var prefix in prefixes)
        {
            var hit = voices.FirstOrDefault(v => Path.GetFileName(v).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return voices[0];
    }

    // Persistent piper process per voice model (--json-input): the ONNX model loads once,
    // then each synthesis is a single stdin line — saves ~0.5s of cold-start per reply.
    private Process? _piper;
    private string? _piperModel;
    private readonly SemaphoreSlim _piperLock = new(1, 1);
    private readonly Queue<string> _piperErr = new();

    private void DrainPiperStderr(Process p)
    {
        // stderr must be drained continuously or piper blocks once the pipe buffer fills
        _ = Task.Run(async () =>
        {
            string? line;
            try
            {
                while ((line = await p.StandardError.ReadLineAsync()) is not null)
                    lock (_piperErr) { _piperErr.Enqueue(line); while (_piperErr.Count > 20) _piperErr.Dequeue(); }
            }
            catch { /* process ended */ }
        });
    }

    private string LastPiperErrors()
    {
        lock (_piperErr) return string.Join(" | ", _piperErr);
    }

    private async Task<byte[]> PiperSynthAsync(string text, string modelPath)
    {
        await _piperLock.WaitAsync();
        try
        {
            if (_piper is null || _piper.HasExited || _piperModel != modelPath)
            {
                try { if (_piper is { HasExited: false }) _piper.Kill(true); } catch { }
                var psi = new ProcessStartInfo(PiperExe, $"-m \"{modelPath}\" --json-input")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _piper = Process.Start(psi)!;
                _piperModel = modelPath;
                DrainPiperStderr(_piper);
            }

            var outFile = Path.Combine(Path.GetTempPath(), $"jarvis-tts-{Guid.NewGuid():n}.wav");
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    text = text.Replace("\r", " ").Replace("\n", " "),
                    output_file = outFile,
                });
                await _piper.StandardInput.WriteLineAsync(payload);
                await _piper.StandardInput.FlushAsync();

                // piper prints the output path to stdout once the wav is FULLY written —
                // that's the completion signal (polling file size races piper's between-sentence pauses)
                var readLine = _piper.StandardOutput.ReadLineAsync();
                var finished = await Task.WhenAny(readLine, Task.Delay(30000));
                if (finished != readLine)
                {
                    try { _piper.Kill(true); } catch { }
                    _piper = null;
                    throw new TimeoutException("piper timed out");
                }
                if (readLine.Result is null) // stdout closed: process died
                {
                    _piper = null;
                    throw new Exception("piper exited: " + LastPiperErrors());
                }
                // piper prints the path BEFORE releasing its write handle — read share-tolerant with retry,
                // and trust the RIFF size field to reject any partial read
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using var fs = new FileStream(outFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var buf = new MemoryStream();
                        await fs.CopyToAsync(buf);
                        var bytes = buf.ToArray();
                        if (bytes.Length >= 44 && BitConverter.ToInt32(bytes, 4) + 8 == bytes.Length)
                            return bytes;
                        if (attempt >= 100) throw new InvalidDataException($"piper wav incomplete ({bytes.Length} bytes)");
                    }
                    catch (IOException) when (attempt < 100)
                    {
                        // sharing violation or still flushing — piper is finishing up
                    }
                    await Task.Delay(10);
                }
            }
            finally { try { File.Delete(outFile); } catch { } }
        }
        finally { _piperLock.Release(); }
    }

    // ---------------------------------------------------------------- sapi

    private byte[] SapiSynth(string text)
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        try
        {
            var male = synth.GetInstalledVoices()
                .FirstOrDefault(v => v.VoiceInfo.Gender == System.Speech.Synthesis.VoiceGender.Male && v.Enabled);
            if (male is not null) synth.SelectVoice(male.VoiceInfo.Name);
        }
        catch { /* default voice is fine */ }
        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        return ms.ToArray();
    }
}

/// <summary>16-bit mono PCM/WAV utilities shared by every engine path.</summary>
public static class AudioPcm
{
    /// <summary>Walk RIFF chunks to the 'data' payload (16-bit mono PCM assumed — piper/kokoro output).</summary>
    public static (byte[] Pcm, int Rate) ExtractPcm(byte[] wav)
    {
        if (wav.Length < 44) throw new InvalidDataException("wav too short");
        var rate = BitConverter.ToInt32(wav, 24);
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data")
                return (wav[(pos + 8)..Math.Min(pos + 8 + size, wav.Length)], rate);
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    public static byte[] TrimSilence(byte[] pcm, int rate, short threshold = 250)
    {
        int first = -1, last = -1;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var s = Math.Abs((int)BitConverter.ToInt16(pcm, i));
            if (s > threshold) { if (first < 0) first = i; last = i; }
        }
        if (first < 0) return [];
        var margin = (int)(rate * 0.05) * 2; // keep 50ms of natural attack/decay
        first = Math.Max(0, first - margin);
        last = Math.Min(pcm.Length - 2, last + margin);
        var len = last + 2 - first;
        if ((len & 1) == 1) len--;
        return pcm[first..(first + len)];
    }

    /// <summary>Concatenate clips with their trailing gaps plus a lead-in and tail of silence.</summary>
    public static byte[] Stitch(List<(byte[] Pcm, int GapAfterMs)> clips, int rate, int leadInMs, int tailMs)
    {
        static byte[] Silence(int rate, int ms) => new byte[Math.Max(0, (int)(rate * (ms / 1000.0)) * 2)];
        using var pcmOut = new MemoryStream();
        pcmOut.Write(Silence(rate, leadInMs));
        for (var i = 0; i < clips.Count; i++)
        {
            pcmOut.Write(clips[i].Pcm);
            if (i < clips.Count - 1) pcmOut.Write(Silence(rate, clips[i].GapAfterMs));
        }
        pcmOut.Write(Silence(rate, tailMs));
        return pcmOut.ToArray();
    }

    public static byte[] BuildWav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
