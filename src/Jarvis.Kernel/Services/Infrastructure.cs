using System.Text.Json;
using Jarvis.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace Jarvis.Kernel.Services;

public class JarvisHub : Hub { }

/// <summary>Forwards every bus event to SignalR clients (the UI's live feed).</summary>
public class EventForwarder(IEventBus bus, IHubContext<JarvisHub> hub) : IHostedService
{
    private IDisposable? _sub;

    public Task StartAsync(CancellationToken ct)
    {
        _sub = bus.Subscribe(e => hub.Clients.All.SendAsync("event",
            new { kind = e.Kind, payload = e.Payload, actor = e.Actor, at = e.At }, ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }
}

/// <summary>Persists every bus event into the audit table.</summary>
public class AuditSubscriber(IEventBus bus, IServiceScopeFactory scopes) : IHostedService
{
    // High-frequency progress noise stays on the live feed but out of the audit table
    private static readonly HashSet<string> Skip = ["agent.progress", "focus.sample", "voice.state", "voice.levels", "voice.tts"];
    private IDisposable? _sub;

    public Task StartAsync(CancellationToken ct)
    {
        _sub = bus.Subscribe(async e =>
        {
            if (Skip.Contains(e.Kind)) return;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
            db.Events.Add(new AuditEvent
            {
                At = e.At,
                Kind = e.Kind,
                Actor = e.Actor,
                PayloadJson = JsonSerializer.Serialize(e.Payload),
            });
            await db.SaveChangesAsync();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }
}

/// <summary>SQLite FTS5 index over notes, commitments, tasks, decisions and agent summaries.</summary>
public class MemoryIndex
{
    private readonly string _connString = $"Data Source={JarvisPaths.DbFile}";

    public void EnsureCreated()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(kind, ref_id UNINDEXED, title, body)";
        cmd.ExecuteNonQuery();
    }

    public void Add(string kind, string refId, string title, string body)
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO memory_fts (kind, ref_id, title, body) VALUES ($k, $r, $t, $b)";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$r", refId);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.ExecuteNonQuery();
    }

    /// <summary>RAG-lite: (re)index every data/brains/**/*.md as ~500-word chunks under kind
    /// 'brain_doc'. Deletes the kind first — reindexing must not duplicate rows. FTS5 over this
    /// corpus beats a vector store at this size; revisit embeddings only if recall fails.</summary>
    public int ReindexBrains()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM memory_fts WHERE kind = 'brain_doc'";
            del.ExecuteNonQuery();
        }
        var chunks = 0;
        foreach (var file in Directory.EnumerateFiles(JarvisPaths.Brains, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(JarvisPaths.Brains, file).Replace('\\', '/');
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i += 500)
            {
                var body = string.Join(' ', words.Skip(i).Take(500));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO memory_fts (kind, ref_id, title, body) VALUES ('brain_doc', $r, $t, $b)";
                cmd.Parameters.AddWithValue("$r", $"{rel}#{i / 500}");
                cmd.Parameters.AddWithValue("$t", rel);
                cmd.Parameters.AddWithValue("$b", body);
                cmd.ExecuteNonQuery();
                chunks++;
            }
        }
        return chunks;
    }

    public List<(string Kind, string RefId, string Title, string Snippet)> Search(string query, int limit = 8)
    {
        var results = new List<(string, string, string, string)>();
        var terms = string.Join(" ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => $"\"{t.Replace("\"", "")}\""));
        if (terms.Length == 0) return results;

        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, ref_id, title, snippet(memory_fts, 3, '', '', '…', 18)
            FROM memory_fts WHERE memory_fts MATCH $q ORDER BY rank LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$q", terms);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            results.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));
        return results;
    }
}

/// <summary>Keeps the brain_doc FTS index fresh: full reindex at startup, then debounced
/// reindex whenever any markdown under data/brains changes.</summary>
public class BrainWatcher(MemoryIndex index, ILogger<BrainWatcher> log) : IHostedService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() =>
        {
            try { log.LogInformation("[brains] indexed {Count} chunks", index.ReindexBrains()); }
            catch (Exception ex) { log.LogWarning("[brains] initial index failed: {Msg}", ex.Message); }
        }, ct);

        _watcher = new FileSystemWatcher(JarvisPaths.Brains, "*.md") { IncludeSubdirectories = true };
        _watcher.Changed += OnChange; _watcher.Created += OnChange;
        _watcher.Deleted += OnChange; _watcher.Renamed += OnChange;
        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try { log.LogInformation("[brains] reindexed {Count} chunks", index.ReindexBrains()); }
            catch (Exception ex) { log.LogWarning("[brains] reindex failed: {Msg}", ex.Message); }
        }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    public Task StopAsync(CancellationToken ct) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { _watcher?.Dispose(); _debounce?.Dispose(); }
}

public static class SpeechText
{
    private static readonly string[] Small =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    public static string Number(int n) => n is >= 0 and <= 20 ? Small[n] : n.ToString();

    /// <summary>Spell out standalone small integers — TTS engines read bare digits like "1" as
    /// their own sentence, inserting a hard pause mid-reply.</summary>
    public static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\b([0-9]|1[0-9]|20)\b", m => Small[int.Parse(m.Value)]);

    /// <summary>
    /// What reaches the EAR, not the console: URLs, paths, hex ids and markdown are noise out
    /// loud, and nobody wants 90 seconds of agent report read at them. The console always shows
    /// the full text — this only governs the audio.
    /// </summary>
    public static string ForSpeech(string text, int maxChars = 420)
    {
        var r = System.Text.RegularExpressions.Regex.Replace(text, @"https?://[^\s)\]}>,;""']+|www\.[^\s)\]}>,;""']+", "link");
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\b([\w.+-]+)@[\w-]+(\.[\w-]+)+\b", "$1's email");
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\b[A-Za-z]:\\[^\s,;""']+|(?<=\s)/[\w./-]{8,}", "a file path");
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\b[a-f0-9]{10,}\b", "an id",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        r = r.Replace("**", "").Replace("##", "").Replace("`", "").Replace("—", ", ").Replace("…", "...");
        r = System.Text.RegularExpressions.Regex.Replace(r, @"\s+", " ").Trim();
        if (r.Length > maxChars)
        {
            var cut = r.LastIndexOfAny(['.', '!', '?'], maxChars - 1);
            r = (cut > maxChars / 2 ? r[..(cut + 1)] : r[..maxChars].TrimEnd() + ".")
                + " There's more, it's in the console.";
        }
        return r;
    }
}

/// <summary>Spoken-friendly relative date parsing ("tomorrow", "friday", "the weekend", "in 3 days").</summary>
public static class DateParser
{
    public static DateTime? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        var today = DateTime.Today;

        if (text is "today" or "tonight" or "eod" or "end of day") return today.AddHours(18);
        if (text is "tomorrow") return today.AddDays(1).AddHours(18);
        if (text is "next week") return NextWeekday(today, DayOfWeek.Monday).AddHours(9);
        if (text is "the weekend" or "this weekend" or "end of week" or "end of the week" or "before the weekend")
            return NextWeekday(today, DayOfWeek.Saturday).AddHours(10);

        var inDays = System.Text.RegularExpressions.Regex.Match(text, @"^in (\d+) days?$");
        if (inDays.Success) return today.AddDays(int.Parse(inDays.Groups[1].Value)).AddHours(18);

        foreach (var dow in Enum.GetValues<DayOfWeek>())
            if (text == dow.ToString().ToLowerInvariant() || text == $"next {dow.ToString().ToLowerInvariant()}")
                return NextWeekday(today, dow).AddHours(18);

        return DateTime.TryParse(text, out var dt) ? dt : null;
    }

    private static DateTime NextWeekday(DateTime from, DayOfWeek day)
    {
        var diff = ((int)day - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(diff == 0 ? 7 : diff);
    }

    public static string Speak(DateTime? d) =>
        d is null ? "no due date"
        : d.Value.Date == DateTime.Today ? "today"
        : d.Value.Date == DateTime.Today.AddDays(1) ? "tomorrow"
        : d.Value.ToString("dddd, MMM d");

    /// <summary>Clock-time specs: "7pm", "7:30 pm", "19:00", "tomorrow 9am", "tomorrow at 9".</summary>
    public static DateTime? TryParseTimeSpec(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        var tomorrow = text.Contains("tomorrow");
        text = text.Replace("tomorrow", "").Replace(" at ", " ").Trim();

        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"^(?<h>\d{1,2})([:.](?<m>\d{2}))?\s*(?<ap>am|pm|a m|p m)?$");
        if (!m.Success) return null;
        var h = int.Parse(m.Groups["h"].Value);
        var min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        var ap = m.Groups["ap"].Value.Replace(" ", "");
        if (ap == "pm" && h < 12) h += 12;
        if (ap == "am" && h == 12) h = 0;
        if (h > 23 || min > 59) return null;

        var at = DateTime.Today.AddHours(h).AddMinutes(min);
        if (tomorrow) at = at.AddDays(1);
        else if (at <= DateTime.Now) at = ap == "" && h < 12 && at.AddHours(12) > DateTime.Now
            ? at.AddHours(12)              // "at 7" said in the afternoon → 7pm today
            : at.AddDays(1);               // otherwise: same time tomorrow
        return at;
    }

    /// <summary>Durations: "20 minutes", "1 hour", "an hour and a half", "90 seconds", "1 hour and 20 minutes".</summary>
    public static TimeSpan? TryParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant()
            .Replace("an hour and a half", "90 minutes").Replace("half an hour", "30 minutes")
            .Replace("an hour", "1 hour").Replace("a minute", "1 minute");
        var total = TimeSpan.Zero;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"(?<n>\d+(\.\d+)?)\s*(?<u>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b"))
        {
            var n = double.Parse(m.Groups["n"].Value);
            total += m.Groups["u"].Value[0] switch
            {
                'h' => TimeSpan.FromHours(n),
                's' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMinutes(n),
            };
        }
        return total > TimeSpan.Zero ? total : null;
    }
}
