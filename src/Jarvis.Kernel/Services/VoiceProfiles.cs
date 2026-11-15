using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>Pause/stitching rules for long replies. All values hand-editable in voice/profiles.json.</summary>
public class VoiceChunking
{
    public bool Enabled { get; set; } = true;
    /// <summary>Replies at or under this length are synthesized in one shot — no chunking latency.</summary>
    public int MinSplitChars { get; set; } = 160;
    /// <summary>Sentences are merged into chunks up to roughly this many characters.</summary>
    public int MaxChunkChars { get; set; } = 220;
    public int SentenceGapMs { get; set; } = 240;
    public int ParagraphGapMs { get; set; } = 480;
    public int LeadInMs { get; set; } = 60;
    public int TailMs { get; set; } = 140;
}

/// <summary>One named voice configuration. Engine chain: kokoro → piper → SAPI → silence
/// (an engine:"piper" profile simply starts further down the chain).</summary>
public class VoiceProfile
{
    public string Name { get; set; } = "default";
    public string Description { get; set; } = "";
    /// <summary>"kokoro" or "piper" — the PREFERRED engine; everything below it stays as fallback.</summary>
    public string Engine { get; set; } = "kokoro";
    /// <summary>Kokoro voice id (bm_fable, bm_george, am_michael, …).</summary>
    public string Voice { get; set; } = "bm_fable";
    public double Speed { get; set; } = 1.0;
    /// <summary>Piper model filename prefixes, most preferred first (used when Engine=piper or as fallback).</summary>
    public string[] PiperVoices { get; set; } = ["en_GB-alan", "en_GB", "en_US"];
    public bool Formatter { get; set; } = true;
    /// <summary>Spoken length cap — the console always shows the full text.</summary>
    public int MaxSpokenChars { get; set; } = 420;
    public VoiceChunking Chunking { get; set; } = new();
    public bool Mastering { get; set; } = true;
    /// <summary>Cache short/system phrases to voice/cache for instant playback.</summary>
    public bool Cache { get; set; } = false;
    /// <summary>Allow auto-escalation to the briefing profile when the reply is long.</summary>
    public bool AutoEscalate { get; set; }

    /// <summary>Directory name for this profile's phrase cache. Encodes everything that changes
    /// the audio, so editing the profile (or the formatter) invalidates the cache automatically.</summary>
    public string CacheKey
    {
        get
        {
            var sig = $"{Engine}|{Voice}|{Speed:F3}|{Mastering}|{Formatter}|fmt{SpeechTextFormatter.Version}";
            var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sig)))[..8].ToLowerInvariant();
            return $"{Name}-{h}";
        }
    }
}

public class VoiceSelection
{
    /// <summary>Replies longer than this auto-escalate (from AutoEscalate profiles) to BriefingProfile.</summary>
    public int BriefingMinChars { get; set; } = 280;
    public string BriefingProfile { get; set; } = "briefing";
}

/// <summary>
/// Loads voice/profiles.json (hand-editable, hot-reloaded on file change via mtime check).
/// Never throws: a missing or broken file degrades to a built-in default profile so the
/// kernel always has a voice.
/// </summary>
public class VoiceProfileStore(ILogger log)
{
    public static string FilePath => Path.Combine(JarvisPaths.Voice, "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Lock _lock = new();
    private DateTime _loadedMtime = DateTime.MinValue;
    private Model _model = Model.Builtin();

    public VoiceSelection Selection { get { Reload(); return _model.Selection; } }
    public IReadOnlyDictionary<string, string> Lexicon { get { Reload(); return _model.Lexicon; } }
    public int CacheMaxChars { get { Reload(); return _model.CacheMaxChars; } }
    public IReadOnlyList<string> Names { get { Reload(); return [.. _model.Profiles.Keys]; } }
    public IReadOnlyList<VoiceProfile> All { get { Reload(); return [.. _model.Profiles.Values]; } }

    /// <summary>Resolve aliases (legacy "jarvis" → "default", etc.) to a canonical profile name,
    /// or null when nothing matches.</summary>
    public string? ResolveName(string? name)
    {
        Reload();
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().ToLowerInvariant();
        if (_model.Aliases.TryGetValue(name, out var target)) name = target;
        return _model.Profiles.ContainsKey(name) ? name : null;
    }

    /// <summary>Profile by name (after aliasing); falls back to "default", then to built-in.</summary>
    public VoiceProfile Get(string? name)
    {
        Reload();
        var resolved = ResolveName(name) ?? "default";
        return _model.Profiles.TryGetValue(resolved, out var p) ? p
             : _model.Profiles.TryGetValue("default", out var d) ? d
             : Model.Builtin().Profiles["default"];
    }

    private void Reload()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    if (_loadedMtime != DateTime.MinValue) return;   // already warned once
                    log.LogWarning("[voice] {Path} not found — using built-in default profile", FilePath);
                    _loadedMtime = DateTime.UnixEpoch;
                    return;
                }
                var mtime = File.GetLastWriteTimeUtc(FilePath);
                if (mtime == _loadedMtime) return;
                var raw = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath), JsonOpts);
                if (raw is null || raw.Profiles.Count == 0)
                    throw new InvalidDataException("no profiles defined");
                foreach (var (name, p) in raw.Profiles) p.Name = name.ToLowerInvariant();
                raw.Aliases = new Dictionary<string, string>(raw.Aliases, StringComparer.OrdinalIgnoreCase);
                _model = raw;
                _loadedMtime = mtime;
                log.LogInformation("[voice] loaded {Count} voice profiles from profiles.json", raw.Profiles.Count);
            }
            catch (Exception ex)
            {
                // keep the previous good model; never take the voice down over a JSON typo
                log.LogWarning("[voice] profiles.json invalid ({Msg}) — keeping previous profiles", ex.Message);
                _loadedMtime = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.UnixEpoch;
            }
        }
    }

    private class Model
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public VoiceSelection Selection { get; set; } = new();
        /// <summary>Short texts (≤ this length) are cache-eligible; long replies are never cached.</summary>
        public int CacheMaxChars { get; set; } = 100;
        public Dictionary<string, string> Lexicon { get; set; } = [];
        public Dictionary<string, VoiceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static Model Builtin() => new()
        {
            Profiles = new(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new VoiceProfile { Name = "default", AutoEscalate = true, Cache = true },
            },
        };
    }
}

/// <summary>Splits a (formatted) reply into ordered speech chunks with explicit pause lengths —
/// generalizes the per-sentence stitching the Piper path proved out, for any engine.</summary>
public static class SpeechChunker
{
    public record Chunk(string Text, int GapAfterMs);

    public static List<Chunk> Split(string text, VoiceChunking c)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        if (!c.Enabled || text.Length <= c.MinSplitChars)
            return [new Chunk(Flatten(text), 0)];

        var chunks = new List<Chunk>();
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var para in paragraphs)
        {
            var sentences = Regex.Split(para, @"(?<=[.!?])\s+")
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            var current = "";
            foreach (var s in sentences)
            {
                if (current.Length > 0 && current.Length + 1 + s.Length > c.MaxChunkChars)
                {
                    chunks.Add(new Chunk(current, c.SentenceGapMs));
                    current = s;
                }
                else current = current.Length == 0 ? s : current + " " + s;
            }
            if (current.Length > 0) chunks.Add(new Chunk(current, c.ParagraphGapMs));
        }
        if (chunks.Count > 0) chunks[^1] = chunks[^1] with { GapAfterMs = 0 };
        return chunks;
    }

    private static string Flatten(string s) => s.Replace('\n', ' ');
}
