using System.Text;
using System.Text.Json;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Short-term conversational memory so follow-ups work: "close it", "the second one",
/// "and for atlas?". Fed by EntityResolver (project/repo resolutions) and by list-producing
/// handlers; consumed by ContextModule and prompt builders.
/// </summary>
public class DialogContext
{
    private readonly Lock _lock = new();

    public record ListItem(string Kind, string Id, string Title);
    public record Exchange(string Utterance, string Intent, Dictionary<string, string> Slots, DateTime At);

    private readonly LinkedList<Exchange> _exchanges = [];

    public string? LastProjectSlug { get; private set; }
    public List<ListItem> LastItems { get; private set; } = [];

    public Exchange? LastExchange { get { lock (_lock) return _exchanges.Last?.Value; } }

    public List<Exchange> RecentExchanges(int take = 3)
    {
        lock (_lock) return [.. _exchanges.Reverse().Take(take).Reverse()];
    }

    public void RecordExchange(string utterance, string intent, Dictionary<string, string> slots)
    {
        lock (_lock)
        {
            _exchanges.AddLast(new Exchange(utterance, intent, new(slots, StringComparer.OrdinalIgnoreCase), DateTime.UtcNow));
            while (_exchanges.Count > 5) _exchanges.RemoveFirst();
        }
    }

    public void RecordProject(Project p) { lock (_lock) LastProjectSlug = p.Slug; }

    public void RecordItems(IEnumerable<ListItem> items)
    {
        lock (_lock) LastItems = [.. items.Take(10)];
    }

    public ListItem? ItemByOrdinal(int oneBased)
    {
        lock (_lock) return oneBased >= 1 && oneBased <= LastItems.Count ? LastItems[oneBased - 1] : null;
    }

    public ListItem? SingleRecentItem()
    {
        lock (_lock) return LastItems.Count >= 1 ? LastItems[0] : null;
    }
}

/// <summary>
/// Tier-3: when no command matches, answer anyway with the local model.
/// Now context-aware: live date/time, the user's stored facts and recent dialog are injected,
/// and questions that smell like live data get free DuckDuckGo results to answer from.
/// Labeled as local-model so the UI/user knows the source. No cloud, no cost.
/// </summary>
public class GeneralAnswerService(IConfiguration config, ILogger<GeneralAnswerService> log,
    IServiceScopeFactory scopes, DialogContext dialog, WebSearchService web)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private string BaseUrl => config["Jarvis:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    private string Model => config["Jarvis:Ollama:AnswerModel"]
        ?? config["Jarvis:Ollama:Model"] ?? "qwen2.5:3b";

    // Questions that need live data — answered from web snippets, not model memory
    private static readonly System.Text.RegularExpressions.Regex Fresh = new(
        @"\b(today|latest|current(ly)?|right now|this (week|month|year)|news|price|cost of|score|version of|who won|just (released|announced)|stock|weather in)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public async Task<string?> AnswerAsync(string utterance)
    {
        try
        {
            var context = new StringBuilder();
            context.AppendLine($"Current local date and time: {DateTime.Now:dddd, MMMM d, yyyy, h:mm tt}.");

            // the user's stored facts — capped so a 3b model doesn't drown
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
                var facts = await db.Facts.AsNoTracking()
                    .OrderByDescending(f => f.CreatedAt).Take(30).ToListAsync();
                if (facts.Count > 0)
                    context.AppendLine("Things the user told you to remember: " +
                        string.Join("; ", facts.Select(f => f.Text)) + ".");
            }

            var recent = dialog.RecentExchanges(3);
            if (recent.Count > 0)
                context.AppendLine("Recent conversation: " +
                    string.Join(" | ", recent.Select(e => $"User said \"{e.Utterance}\" ({e.Intent})")) + ".");

            var webBlock = "";
            var usedWeb = false;
            if (Fresh.IsMatch(utterance))
            {
                var hits = await web.SearchAsync(utterance, take: 4);
                if (hits.Count > 0)
                {
                    usedWeb = true;
                    var lines = hits.Select(h => $"- {h.Title}: {h.Snippet}");
                    webBlock = "\nLive web results (just fetched):\n" + Truncate(string.Join("\n", lines), 1500) + "\n";
                }
            }

            var prompt = $"""
                You are JARVIS, a concise spoken voice assistant on the user's PC. Answer the question below
                in one to three short spoken sentences. Plain words, no markdown, no lists, no disclaimers.
                Timeless facts, jokes, explanations and opinions: just answer them directly.
                {context}{webBlock}
                {(usedWeb
                    ? "The question needs live data: answer ONLY from the live web results above. If they don't contain the answer, say you couldn't find it."
                    : "If the question needs live data you don't have, say so briefly rather than guessing.")}
                If it's a deep coding or codebase question, suggest: say 'ask Claude' followed by the question.
                If asked what you can do or integrate with, answer: Teams, email, Jira, Claude and Codex coding
                agents, weather, news, crypto, reminders, habits, meetings — and suggest saying 'help'.
                Never claim you lack an integration — route doubts to 'help' instead.

                Question: {utterance}
                """;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));  // 7b answer model
            var resp = await GenerateAsync(Model, prompt, cts.Token);
            if (resp is null || !resp.IsSuccessStatusCode)
            {
                // answer model missing/still downloading — the base intent model fills in
                var baseModel = config["Jarvis:Ollama:Model"] ?? "qwen2.5:3b";
                if (baseModel == Model) return null;
                resp = await GenerateAsync(baseModel, prompt, cts.Token);
                if (resp is null || !resp.IsSuccessStatusCode) return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var answer = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
            return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        }
        catch (Exception ex)
        {
            log.LogWarning("general answer unavailable: {Msg}", ex.Message);
            return null;
        }
    }

    private async Task<HttpResponseMessage?> GenerateAsync(string model, string prompt, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            { model, stream = false, options = new { temperature = 0.4, num_predict = 220 }, prompt });
            return await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
