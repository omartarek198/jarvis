using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

/// <summary>
/// Voice access to the command audit trail: what did you just do, why, history, retry,
/// escalate to Claude/Codex, reliability stats. The transparency layer that keeps voice
/// control trustworthy.
/// </summary>
public class AuditModule(AgentService agents) : IJarvisModule
{
    public string Name => "audit";

    /// <summary>Intents that never count as "the last command" — they're about other commands.</summary>
    public static readonly string[] MetaIntents =
        ["last_command", "why_did_you", "command_history", "retry_last", "escalate_last",
         "reliability_report", "confirm", "cancel", "help", "route_correction", "copy_that"];

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "last_command",
            Description = "explain the last command end to end: intent, tool, status, log",
            Patterns = [@"^(what did you( just)? do|last command|what was that|audit( that| the last command)?)\??$"],
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("No commands logged yet this session.");
                var spoken = await DescribeAsync(ctx, row);
                return new CommandResult(spoken, new { log = row });
            }
        },
        new()
        {
            Name = "why_did_you",
            Description = "explain why the last command was routed the way it was",
            Patterns = [@"^(why did you (do|choose|pick|run) that|why that( command)?|how did you understand that)\??$"],
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("No commands logged yet this session.");
                var def = ctx.S<CommandRouter>().Definitions.FirstOrDefault(d => d.Name == row.Intent);
                var why = row.Tier switch
                {
                    "grammar" => $"your words matched the exact phrase pattern for {row.Intent}" +
                                 (def is null ? "" : $" — {def.Description}"),
                    "semantic" => $"its meaning matched {row.Intent} at {(int)((row.Confidence ?? 0) * 100)} percent similarity" +
                                  (def is null ? "" : $" — {def.Description}") + ". Say 'that's wrong' if it shouldn't",
                    "llm" => $"no exact pattern matched, so the local model mapped it to {row.Intent} " +
                             $"with {(int)((row.Confidence ?? 0) * 100)} percent confidence" +
                             (def is null ? "" : $" — {def.Description}"),
                    "answer" => "no command matched at all, so the local model answered it directly as a general question",
                    "fallback" => "nothing matched and the general answer tier declined, so I gave up. " +
                                  "Say 'send that to claude' to escalate it",
                    _ => $"it was routed via the {row.Tier} tier",
                };
                return new CommandResult($"You said \"{row.Utterance}\". I chose it because {why}.", new { log = row });
            }
        },
        new()
        {
            Name = "command_history",
            Description = "list the most recent commands and their outcomes",
            Patterns = [@"^(command history|recent commands|what have you done (recently|today))\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var rows = await db.CommandLogs.AsNoTracking()
                    .Where(c => !MetaIntents.Contains(c.Intent))
                    .OrderByDescending(c => c.CreatedAt).Take(5).ToListAsync();
                if (rows.Count == 0) return new CommandResult("No commands logged yet.");
                var lines = rows.Select(r => $"{r.Intent} {SpeakStatus(r.Status)}");
                return new CommandResult($"Last {rows.Count}: {string.Join("; ", lines)}.",
                    new { history = rows });
            }
        },
        new()
        {
            Name = "retry_last",
            Description = "run the last command again",
            Patterns = [@"^(retry( that| it)?|try (that|it) again|run (that|it) again)$"],
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("Nothing to retry yet.");
                var router = ctx.S<CommandRouter>();
                var resp = await router.RouteAsync(row.Utterance, ctx.Source, ctx.Services);
                return new CommandResult($"Retried \"{row.Utterance}\" — {resp.Spoken}", resp.Data);
            }
        },
        new()
        {
            Name = "escalate_last",
            Description = "send the last command and its outcome to Claude or Codex for a proper answer",
            Patterns = [@"^((send|give) (that|it|this) to (?<agent>claude|codex)|ask (?<agent>claude|codex) instead|that'?s wrong,? ask (?<agent>claude|codex))$"],
            SlotHints = ["agent"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("There's no recent command to escalate.");
                var agent = ctx.Slot("agent")?.ToLowerInvariant() ?? "claude";
                var prompt = $"""
                    The user gave their voice assistant JARVIS this spoken command, and the local handling
                    wasn't good enough — answer or investigate it properly instead.

                    Utterance: "{row.Utterance}"
                    JARVIS routed it to intent '{row.Intent}' (tier {row.Tier}), status: {row.Status}.
                    {(row.Error.Length > 0 ? $"Error: {row.Error}" : "")}
                    JARVIS replied: "{Truncate(row.Spoken, 300)}"

                    Investigate read-only and give a complete, correct answer to the user's original request.
                    """;
                var (run, spoken) = await agents.DispatchAsync(agent, prompt, "escalate-command",
                    project: null, repo: null, edit: false);
                ctx.Audit.Tool = agent == "codex" ? "codex exec" : "claude -p";
                ctx.Audit.AgentRunId = run.Id;
                ctx.Audit.LogRef = run.TranscriptPath ?? "";
                return new CommandResult(spoken, new { run.Id, escalated = row.Utterance });
            }
        },
        new()
        {
            Name = "route_correction",
            Description = "mark the last answer as wrongly routed — JARVIS learns and re-answers",
            Patterns = [@"^(that('s| was| is) wrong|wrong answer|no,? that('s| is) not (it|right|what i (asked|meant)))$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("Nothing to correct yet.");
                if (row.Intent is "general_answer" or "unknown")
                    return new CommandResult(
                        "That came from the general answer tier, not a misrouted command — try rephrasing, or say: send that to claude.");
                var learned = await ctx.S<SemanticRouter>().AddCorrectionAsync(row.Utterance, row.Intent);
                if (!learned)
                    return new CommandResult("I couldn't store the correction — the embedding model isn't answering.");
                // re-route immediately: with the correction active, the utterance lands elsewhere
                var retry = await ctx.S<CommandRouter>().RouteAsync(row.Utterance, ctx.Source, ctx.Services);
                return new CommandResult($"Understood — that won't route to {row.Intent.Replace('_', ' ')} again. {retry.Spoken}",
                    retry.Data);
            }
        },
        new()
        {
            Name = "copy_that",
            Description = "copy the last response (or just its link) to the clipboard",
            Patterns = [@"^copy (that|this|it)( to (the )?clipboard)?$", @"^copy the (?<what>link|url|code|id)$"],
            SlotHints = ["what"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var row = await LastRealCommandAsync(ctx);
                if (row is null) return new CommandResult("Nothing to copy yet.");
                var what = ctx.Slot("what");
                string? payload = what switch
                {
                    "link" or "url" => System.Text.RegularExpressions.Regex
                        .Match(row.Spoken, @"https?://[^\s)\]}>,;""']+").Value is { Length: > 0 } u ? u : null,
                    "code" or "id" => System.Text.RegularExpressions.Regex
                        .Match(row.Spoken, @"\b[A-Z0-9][A-Z0-9-]{5,}\b").Value is { Length: > 0 } c ? c : null,
                    _ => row.Spoken,
                };
                if (payload is null) return new CommandResult($"I couldn't find a {what} in the last response.");
                ctx.S<IEventBus>().Publish(new JarvisEvent("clipboard.set",
                    new { text = payload, label = what ?? "last response" }, ctx.Source));
                return new CommandResult(what is null ? "Copied." : $"Copied the {what}.");
            }
        },
        new()
        {
            Name = "reliability_report",
            Description = "report per-command success rates over the last week",
            Patterns = [@"^(how reliable (are you|have you been)|reliability report|command stats)\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var since = DateTime.UtcNow.AddDays(-7);
                var rows = await db.CommandLogs.AsNoTracking()
                    .Where(c => c.CreatedAt >= since && !MetaIntents.Contains(c.Intent))
                    .Select(c => new { c.Intent, c.Status }).ToListAsync();
                if (rows.Count == 0) return new CommandResult("No command history in the last week yet.");
                var ok = rows.Count(r => r.Status == "ok");
                var failed = rows.Count(r => r.Status == "failed");
                var unrouted = rows.Count(r => r.Status == "unhandled");
                var pct = (int)Math.Round(100.0 * ok / Math.Max(1, rows.Count - rows.Count(r => r.Status is "awaiting_confirm" or "cancelled")));
                var worst = rows.Where(r => r.Status == "failed")
                    .GroupBy(r => r.Intent).OrderByDescending(g => g.Count()).Take(3)
                    .Select(g => $"{g.Key} failed {SpeechText.Number(g.Count())} time{(g.Count() == 1 ? "" : "s")}").ToList();
                var spoken = $"Last 7 days: {rows.Count} commands, {pct} percent succeeded. " +
                             $"{SpeechText.Number(failed)} failed, {SpeechText.Number(unrouted)} I couldn't route." +
                             (worst.Count > 0 ? $" Worst: {string.Join("; ", worst)}." : "");
                var byIntent = rows.GroupBy(r => r.Intent).Select(g => new
                {
                    intent = g.Key, total = g.Count(),
                    ok = g.Count(r => r.Status == "ok"), failed = g.Count(r => r.Status == "failed"),
                }).OrderByDescending(x => x.failed).ThenByDescending(x => x.total);
                return new CommandResult(spoken, new { total = rows.Count, ok, failed, unrouted, byIntent });
            }
        },
    ];

    private static async Task<CommandLog?> LastRealCommandAsync(CommandContext ctx)
    {
        var db = ctx.S<JarvisDb>();
        return await db.CommandLogs.AsNoTracking()
            .Where(c => !MetaIntents.Contains(c.Intent))
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync();
    }

    private static async Task<string> DescribeAsync(CommandContext ctx, CommandLog row)
    {
        var tier = row.Tier switch
        {
            "grammar" => "an exact grammar match",
            "semantic" => $"a meaning match at {(int)((row.Confidence ?? 0) * 100)} percent similarity",
            "llm" => $"the local model at {(int)((row.Confidence ?? 0) * 100)} percent confidence",
            "answer" => "the general answer tier",
            "fallback" => "no match at all",
            _ => $"the {row.Tier} tier",
        };
        var parts = new List<string>
        {
            $"You said \"{row.Utterance}\".",
            $"I matched it to {row.Intent} via {tier}, safety {row.Safety}.",
        };
        if (row.Tool is not ("internal" or "none" or "")) parts.Add($"Tool: {row.Tool}.");
        parts.Add($"Status: {SpeakStatus(row.Status)} in {SpeakDuration(row.DurationMs)}.");
        if (row.Error.Length > 0) parts.Add($"Error: {Truncate(row.Error, 120)}.");
        if (row.LogRef.Length > 0) parts.Add($"Full log saved at {row.LogRef}.");

        if (row.AgentRunId is not null)
        {
            var run = await ctx.S<JarvisDb>().AgentRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == row.AgentRunId);
            if (run is not null)
            {
                parts.Add($"Agent run is {run.Status}.");
                var followUps = System.Text.Json.JsonSerializer.Deserialize<string[]>(run.FollowUpTaskIds) ?? [];
                if (followUps.Length > 0)
                    parts.Add($"{SpeechText.Number(followUps.Length)} follow-up task{(followUps.Length == 1 ? "" : "s")} created.");
            }
        }
        parts.Add("Say 'retry that' or 'send that to claude'.");
        return string.Join(" ", parts);
    }

    private static string SpeakStatus(string status) => status switch
    {
        "ok" => "succeeded",
        "failed" => "failed",
        "unhandled" => "not understood",
        "awaiting_confirm" => "awaiting your confirmation",
        "cancelled" => "cancelled",
        "expired" => "expired unconfirmed",
        _ => status,
    };

    private static string SpeakDuration(int ms) => ms switch
    {
        < 1000 => $"{ms} milliseconds",
        < 60_000 => $"{Math.Round(ms / 1000.0, 1)} seconds",
        _ => $"{Math.Round(ms / 60000.0, 1)} minutes",
    };

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
