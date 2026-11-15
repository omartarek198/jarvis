using System.Text;
using System.Text.Json;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

/// <summary>Local text embeddings via Ollama (nomic-embed-text) — ~30ms per call, free.</summary>
public class EmbeddingService(IConfiguration config, ILogger<EmbeddingService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };  // first call cold-loads the model
    private string BaseUrl => config["Jarvis:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    private const string Model = "nomic-embed-text";

    public async Task<float[]?> EmbedAsync(string text)
        => (await EmbedBatchAsync([text]))?.FirstOrDefault();

    public async Task<List<float[]>?> EmbedBatchAsync(IReadOnlyList<string> texts)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { model = Model, input = texts });
            var resp = await Http.PostAsync($"{BaseUrl}/api/embed",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("embed failed: {Code} (is nomic-embed-text pulled?)", resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var result = new List<float[]>();
            foreach (var e in doc.RootElement.GetProperty("embeddings").EnumerateArray())
            {
                var v = new float[e.GetArrayLength()];
                var i = 0;
                foreach (var x in e.EnumerateArray()) v[i++] = x.GetSingle();
                Normalize(v);
                result.Add(v);
            }
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning("embed unavailable: {Msg}", ex.Message);
            return null;
        }
    }

    private static void Normalize(float[] v)
    {
        double n = 0;
        for (var i = 0; i < v.Length; i++) n += v[i] * v[i];
        var inv = (float)(1.0 / Math.Max(1e-9, Math.Sqrt(n)));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    /// <summary>Vectors are pre-normalized — cosine is just the dot product.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        double d = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) d += a[i] * b[i];
        return d;
    }
}

/// <summary>
/// Semantic intent routing: the decision of WHICH command an utterance means is geometric
/// (embedding similarity against per-command example utterances), not a forced choice by a
/// small LLM. Below the similarity gate, utterances fall through to the general-answer tier
/// instead of being misrouted ("find out when is my birthday" is not close to "what's the
/// date" in embedding space — the old tier-2 guessed it was).
///
/// Learns from the user: "that's wrong" stores the (utterance, wrong intent) pair with its
/// embedding; any future utterance semantically close to a stored correction can never
/// route to that intent again.
/// </summary>
public class SemanticRouter(EmbeddingService emb, IServiceScopeFactory scopes, ILogger<SemanticRouter> log)
{
    /// <summary>Route directly when the best command similarity is at least this.</summary>
    public const double RouteThreshold = 0.78;
    /// <summary>Between this and RouteThreshold: let the LLM pick among the top candidates (or general).</summary>
    public const double ConsiderThreshold = 0.62;
    /// <summary>A correction suppresses an intent when the utterance is at least this close to it.</summary>
    public const double CorrectionThreshold = 0.86;

    private sealed record IntentIndex(CommandDefinition Def, float[][] Vectors);
    private List<IntentIndex> _index = [];
    private readonly List<(float[] Vec, string Intent)> _negatives = [];
    private readonly Lock _lock = new();

    public bool Ready { get; private set; }

    public async Task InitializeAsync(IReadOnlyList<CommandDefinition> defs)
    {
        for (var attempt = 1; attempt <= 5 && !Ready; attempt++)
        {
            if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(15));
            await InitOnceAsync(defs);
        }
    }

    private async Task InitOnceAsync(IReadOnlyList<CommandDefinition> defs)
    {
        try
        {
            // one text per command from name+description, plus any explicit examples
            var texts = new List<string>();
            var spans = new List<(CommandDefinition Def, int Start, int Count)>();
            foreach (var d in defs)
            {
                var start = texts.Count;
                texts.Add(d.Name.Replace('_', ' ') + ". " + d.Description);
                texts.AddRange(d.Examples);
                spans.Add((d, start, texts.Count - start));
            }

            var all = new List<float[]>();
            for (var i = 0; i < texts.Count; i += 100)
            {
                var batch = await emb.EmbedBatchAsync(texts.Skip(i).Take(100).ToList());
                if (batch is null) { log.LogWarning("semantic router init aborted — embeddings unavailable"); return; }
                all.AddRange(batch);
            }

            var index = spans.Select(s =>
                new IntentIndex(s.Def, all.Skip(s.Start).Take(s.Count).ToArray())).ToList();

            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
                foreach (var c in await db.RouteCorrections.AsNoTracking().ToListAsync())
                {
                    var v = JsonSerializer.Deserialize<float[]>(c.EmbeddingJson);
                    if (v is { Length: > 0 }) lock (_lock) _negatives.Add((v, c.WrongIntent));
                }
            }

            lock (_lock) _index = index;
            Ready = true;
            log.LogInformation("semantic router ready: {Intents} intents, {Vectors} vectors, {Corrections} corrections",
                index.Count, all.Count, _negatives.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning("semantic router init failed: {Msg} — tier-2 falls back to catalog LLM", ex.Message);
        }
    }

    public sealed record Ranked(CommandDefinition Def, double Score);

    /// <summary>Rank commands by similarity, with learned corrections applied. Empty = not ready/unavailable.</summary>
    public async Task<List<Ranked>> RankAsync(string utterance)
    {
        if (!Ready) return [];
        var v = await emb.EmbedAsync(utterance);
        if (v is null) return [];

        HashSet<string> suppressed = [];
        lock (_lock)
            foreach (var (nv, intent) in _negatives)
                if (EmbeddingService.Cosine(v, nv) >= CorrectionThreshold)
                    suppressed.Add(intent);

        var ranked = new List<Ranked>();
        List<IntentIndex> index;
        lock (_lock) index = _index;
        foreach (var ix in index)
        {
            if (suppressed.Contains(ix.Def.Name)) continue;
            double best = 0;
            foreach (var iv in ix.Vectors)
            {
                var c = EmbeddingService.Cosine(v, iv);
                if (c > best) best = c;
            }
            ranked.Add(new Ranked(ix.Def, best));
        }
        ranked.Sort((x, y) => y.Score.CompareTo(x.Score));
        return ranked;
    }

    /// <summary>"That's wrong": never route anything like this utterance to that intent again.</summary>
    public async Task<bool> AddCorrectionAsync(string utterance, string wrongIntent)
    {
        var v = await emb.EmbedAsync(utterance);
        if (v is null) return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        db.RouteCorrections.Add(new RouteCorrection
        {
            Utterance = utterance, WrongIntent = wrongIntent,
            EmbeddingJson = JsonSerializer.Serialize(v),
        });
        await db.SaveChangesAsync();
        lock (_lock) _negatives.Add((v, wrongIntent));
        log.LogInformation("route correction learned: \"{U}\" must not be {Intent}", utterance, wrongIntent);
        return true;
    }
}
