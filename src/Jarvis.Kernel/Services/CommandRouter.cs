using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

public record RouteResponse(string Intent, string Spoken, object? Data, string Tier, bool AwaitingConfirmation = false)
{
    /// <summary>CommandLog row id for this utterance — the audit trail handle.</summary>
    public string? LogId { get; init; }
    /// <summary>ok | failed | unhandled | awaiting_confirm</summary>
    public string? Status { get; init; }
    public int DurationMs { get; init; }
}

public class CommandRouter
{
    private readonly List<(CommandDefinition Def, Regex[] Patterns)> _routes = [];
    private readonly OllamaIntentService _ollama;
    private readonly GeneralAnswerService _answers;
    private readonly DialogContext _dialog;
    private readonly PendingActionService _pending;
    private readonly IEventBus _bus;
    private readonly ILogger<CommandRouter> _log;
    private readonly Council.CouncilService _council;

    public IReadOnlyList<CommandDefinition> Definitions { get; }

    private readonly SemanticRouter _semantic;

    public CommandRouter(IEnumerable<IJarvisModule> modules, OllamaIntentService ollama,
        GeneralAnswerService answers, DialogContext dialog,
        PendingActionService pending, IEventBus bus, ILogger<CommandRouter> log,
        SemanticRouter semantic, Council.CouncilService council)
    {
        _ollama = ollama; _answers = answers; _dialog = dialog;
        _pending = pending; _bus = bus; _log = log; _semantic = semantic; _council = council;
        var defs = new List<CommandDefinition>();
        foreach (var m in modules)
            foreach (var d in m.Commands)
            {
                defs.Add(d);
                _routes.Add((d, [.. d.Patterns.Select(p =>
                    new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))]));
            }
        Definitions = defs;
    }

    public static string Normalize(string text)
    {
        text = text.Trim();
        text = Regex.Replace(text, @"^\s*(hey\s+)?jarvis[,!.\s]+", "", RegexOptions.IgnoreCase);
        // trailing vocative too: "tell me about the weather, Jarvis" must not leak "jarvis"
        // into slots (it fuzzy-resolved to the JARVIS project and answered nonsense)
        text = Regex.Replace(text, @"[,.!?\s]+(hey\s+)?jarvis[,!.?\s]*$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[\s]+", " ");
        return text.TrimEnd('.', '!', '?', ',', ' ');
    }

    public async Task<RouteResponse> RouteAsync(string rawText, string source, IServiceProvider scoped)
    {
        var text = Normalize(rawText);
        if (string.IsNullOrWhiteSpace(text))
            return new("none", "I didn't catch anything.", null, "none");

        _bus.Publish(new JarvisEvent("router.transcript", new { text, source }, source));

        // An active council session gets first claim on the utterance (answers, control
        // phrases). It returns null for unrelated input — normal routing proceeds.
        if (_council.HasActiveSession)
        {
            var consumed = await _council.TryConsumeAsync(text, source, scoped);
            if (consumed is not null)
            {
                var councilResponse = new RouteResponse("council", consumed.Spoken, consumed.Data, "council")
                    { Status = "ok" };
                _bus.Publish(new JarvisEvent("router.response",
                    new { intent = councilResponse.Intent, spoken = councilResponse.Spoken, tier = councilResponse.Tier,
                          awaitingConfirmation = false, status = councilResponse.Status,
                          durationMs = 0, logId = (string?)null, data = councilResponse.Data }, source));
                return councilResponse;
            }
        }

        var response = await RouteTier1Async(text, source, scoped)
                    ?? await RouteTier2Async(text, source, scoped)
                    ?? await RouteTier3Async(text, source, scoped)
                    ?? await LogFallbackAsync(text, source, scoped);

        _bus.Publish(new JarvisEvent("router.response",
            new { intent = response.Intent, spoken = response.Spoken, tier = response.Tier,
                  awaitingConfirmation = response.AwaitingConfirmation,
                  status = response.Status, durationMs = response.DurationMs, logId = response.LogId,
                  data = response.Data }, source));
        return response;
    }

    private async Task<RouteResponse?> RouteTier1Async(string text, string source, IServiceProvider scoped)
    {
        foreach (var (def, patterns) in _routes)
        {
            foreach (var rx in patterns)
            {
                Match m;
                try { m = rx.Match(text); } catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) continue;

                var ctx = BuildContext(text, source, scoped);
                foreach (var g in rx.GetGroupNames().Where(n => !int.TryParse(n, out _)))
                    if (m.Groups[g].Success) ctx.Slots[g] = m.Groups[g].Value;

                var outcome = await ExecuteAsync(def, ctx, "grammar", confidence: 1.0);
                if (outcome.Result.Unhandled) continue;
                return new RouteResponse(def.Name, outcome.Result.Spoken, outcome.Result.Data, "grammar",
                        outcome.Result.AwaitingConfirmation)
                    { LogId = outcome.LogId, Status = outcome.Status, DurationMs = outcome.DurationMs };
            }
        }
        return null;
    }

    private async Task<RouteResponse?> RouteTier2Async(string text, string source, IServiceProvider scoped)
    {
        // Semantic routing: the WHICH-command decision is embedding similarity, not an LLM
        // guess. The LLM is demoted to slot extraction (intent already chosen) or a
        // constrained pick among the top few candidates in the gray zone.
        if (_semantic.Ready)
        {
            var ranked = await _semantic.RankAsync(text);
            if (ranked.Count == 0) return null;   // embeddings briefly unavailable → tier-3

            var top = ranked[0];
            if (top.Score >= SemanticRouter.RouteThreshold)
            {
                var ctx = BuildContext(text, source, scoped);
                foreach (var (k, v) in await _ollama.ExtractSlotsAsync(text, top.Def)) ctx.Slots[k] = v;
                var outcome = await ExecuteAsync(top.Def, ctx, "semantic", top.Score);
                if (!outcome.Result.Unhandled)
                    return new RouteResponse(top.Def.Name, outcome.Result.Spoken, outcome.Result.Data,
                            "semantic", outcome.Result.AwaitingConfirmation)
                        { LogId = outcome.LogId, Status = outcome.Status, DurationMs = outcome.DurationMs };
            }
            else if (top.Score >= SemanticRouter.ConsiderThreshold)
            {
                var candidates = ranked.TakeWhile(r => r.Score >= SemanticRouter.ConsiderThreshold)
                    .Take(3).Select(r => r.Def).ToList();
                var choice = await _ollama.ChooseAmongAsync(text, candidates);
                if (choice is not null)
                {
                    var def = candidates.FirstOrDefault(d =>
                        d.Name.Equals(choice.Value.Intent, StringComparison.OrdinalIgnoreCase));
                    if (def is not null)
                    {
                        var ctx = BuildContext(text, source, scoped);
                        foreach (var (k, v) in choice.Value.Slots) ctx.Slots[k] = v;
                        var outcome = await ExecuteAsync(def, ctx, "llm", choice.Value.Confidence);
                        if (!outcome.Result.Unhandled)
                            return new RouteResponse(def.Name, outcome.Result.Spoken, outcome.Result.Data,
                                    "llm", outcome.Result.AwaitingConfirmation)
                                { LogId = outcome.LogId, Status = outcome.Status, DurationMs = outcome.DurationMs };
                    }
                }
            }
            return null;   // not close to any command → the general tier answers properly
        }

        // Legacy path (semantic index not built yet): full-catalog LLM choice
        var resolved = await _ollama.ResolveAsync(text, Definitions);
        if (resolved is null) return null;

        var legacyDef = Definitions.FirstOrDefault(d => d.Name.Equals(resolved.Value.Intent, StringComparison.OrdinalIgnoreCase));
        if (legacyDef is null) return null;

        var legacyCtx = BuildContext(text, source, scoped);
        foreach (var (k, v) in resolved.Value.Slots) legacyCtx.Slots[k] = v;

        var legacy = await ExecuteAsync(legacyDef, legacyCtx, "llm", resolved.Value.Confidence);
        if (legacy.Result.Unhandled) return null;
        return new RouteResponse(legacyDef.Name, legacy.Result.Spoken, legacy.Result.Data, "llm",
                legacy.Result.AwaitingConfirmation)
            { LogId = legacy.LogId, Status = legacy.Status, DurationMs = legacy.DurationMs };
    }

    /// <summary>Tier-3: no command matched — answer the question anyway with the local model.</summary>
    private async Task<RouteResponse?> RouteTier3Async(string text, string source, IServiceProvider scoped)
    {
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return null;
        var sw = Stopwatch.StartNew();
        var answer = await _answers.AnswerAsync(text);
        if (answer is null) return null;
        var logId = await WriteLogAsync(scoped, new CommandLog
        {
            Utterance = text, Source = source, Tier = "answer", Intent = "general_answer",
            Safety = nameof(SafetyClass.Read), Tool = "ollama", Status = "ok",
            Spoken = answer, DurationMs = (int)sw.ElapsedMilliseconds,
        });
        return new RouteResponse("general_answer", answer, new { source = "local-model" }, "answer")
            { LogId = logId, Status = "ok", DurationMs = (int)sw.ElapsedMilliseconds };
    }

    private async Task<RouteResponse> LogFallbackAsync(string text, string source, IServiceProvider scoped)
    {
        const string spoken = "I don't have a command for that yet. Say 'help' to hear what I can do.";
        var logId = await WriteLogAsync(scoped, new CommandLog
        {
            Utterance = text, Source = source, Tier = "fallback", Intent = "unknown",
            Safety = nameof(SafetyClass.Read), Tool = "none", Status = "unhandled", Spoken = spoken,
        });
        return new RouteResponse("unknown", spoken, null, "fallback")
            { LogId = logId, Status = "unhandled" };
    }

    /// <summary>Re-dispatch a known intent with explicit slots (used by dialog-context follow-ups).</summary>
    public async Task<CommandResult?> ExecuteByNameAsync(string intentName, Dictionary<string, string> slots,
        string source, IServiceProvider scoped)
    {
        var def = Definitions.FirstOrDefault(d => d.Name.Equals(intentName, StringComparison.OrdinalIgnoreCase));
        if (def is null) return null;
        var ctx = BuildContext($"[follow-up:{intentName}]", source, scoped);
        foreach (var (k, v) in slots) ctx.Slots[k] = v;
        return (await ExecuteAsync(def, ctx, "context", confidence: null)).Result;
    }

    // Meta-intents must not become the "last exchange" — follow-ups would shadow the real command
    private static readonly HashSet<string> MetaIntents = ["confirm", "cancel", "followup_project", "close_referenced", "help"];

    private record ExecOutcome(CommandResult Result, string? LogId, string Status, int DurationMs);

    private async Task<ExecOutcome> ExecuteAsync(CommandDefinition def, CommandContext ctx, string tier, double? confidence)
    {
        var sw = Stopwatch.StartNew();
        if (def.Safety == SafetyClass.Mutate)
        {
            if (!MetaIntents.Contains(def.Name))
                _dialog.RecordExchange(ctx.Utterance, def.Name, new(ctx.Slots, StringComparer.OrdinalIgnoreCase));
            // The prompt names the parameters too ("task: …, project: …"), so the user approves
            // the concrete action rather than a generic description of the command.
            var details = DescribeSlots(ctx.Slots);
            var logId = await WriteLogAsync(ctx.Services, NewLog(def, ctx, tier, confidence,
                status: "awaiting_confirm", spoken: $"This will {def.Description}{details}.", sw));
            _pending.Park(def.Name, ctx.Utterance, () => def.Handler(ctx), logId, ctx);
            _bus.Publish(new JarvisEvent("safety.confirm_request",
                new { command = def.Name, description = def.Description, utterance = ctx.Utterance,
                      slots = new Dictionary<string, string>(ctx.Slots) }, ctx.Source));
            var result = new CommandResult($"This will {def.Description}{details}. Say 'confirm' to proceed or 'cancel' to abort.")
                { AwaitingConfirmation = true };
            return new(result, logId, "awaiting_confirm", (int)sw.ElapsedMilliseconds);
        }
        try
        {
            var result = await def.Handler(ctx);
            if (result.Unhandled) return new(result, null, "unhandled", (int)sw.ElapsedMilliseconds);
            if (!MetaIntents.Contains(def.Name))
                _dialog.RecordExchange(ctx.Utterance, def.Name, new(ctx.Slots, StringComparer.OrdinalIgnoreCase));
            if (def.Name is "confirm" or "cancel")
                await FinalizeParkedLogAsync(ctx.Services, result.Spoken, error: null);
            var logId = await WriteLogAsync(ctx.Services, NewLog(def, ctx, tier, confidence, "ok", result.Spoken, sw));
            return new(result, logId, "ok", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command {Name} failed", def.Name);
            if (def.Name is "confirm" or "cancel")
                await FinalizeParkedLogAsync(ctx.Services, spoken: "", error: ex.Message);
            var spoken = $"The {def.Name} command hit an error: {ex.Message}";
            var logId = await WriteLogAsync(ctx.Services, NewLog(def, ctx, tier, confidence, "failed", spoken, sw, ex.Message));
            return new(new CommandResult(spoken), logId, "failed", (int)sw.ElapsedMilliseconds);
        }
    }

    private static CommandLog NewLog(CommandDefinition def, CommandContext ctx, string tier, double? confidence,
        string status, string spoken, Stopwatch sw, string error = "") => new()
    {
        Utterance = ctx.Utterance,
        Source = ctx.Source,
        Tier = tier,
        Confidence = confidence,
        Intent = def.Name,
        SlotsJson = JsonSerializer.Serialize(ctx.Slots),
        ProjectSlug = ctx.Audit.ProjectSlug ?? ctx.Slot("project"),
        Safety = def.Safety.ToString(),
        Tool = ctx.Audit.Tool ?? "internal",
        Status = status,
        Error = error,
        LogRef = ctx.Audit.LogRef ?? "",
        Spoken = spoken,
        AgentRunId = ctx.Audit.AgentRunId,
        DurationMs = (int)sw.ElapsedMilliseconds,
    };

    private async Task<string?> WriteLogAsync(IServiceProvider scoped, CommandLog row)
    {
        try
        {
            var db = scoped.GetRequiredService<JarvisDb>();
            db.CommandLogs.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }
        catch (Exception ex)
        {
            _log.LogWarning("command log write failed: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>After confirm/cancel executes, close out the original Mutate command's log row.</summary>
    private async Task FinalizeParkedLogAsync(IServiceProvider scoped, string spoken, string? error)
    {
        var resolved = _pending.ConsumeResolved();
        if (resolved?.LogId is null) return;
        try
        {
            var db = scoped.GetRequiredService<JarvisDb>();
            var row = await db.CommandLogs.FindAsync(resolved.LogId);
            if (row is null) return;
            row.Status = resolved.Outcome switch
            {
                "confirmed" => error is null ? "ok" : "failed",
                _ => resolved.Outcome,   // cancelled | expired
            };
            row.Error = error ?? "";
            if (!string.IsNullOrEmpty(spoken)) row.Spoken = spoken;
            if (resolved.Ctx is CommandContext orig)
            {
                if (orig.Audit.Tool is not null) row.Tool = orig.Audit.Tool;
                if (orig.Audit.LogRef is not null) row.LogRef = orig.Audit.LogRef;
                row.AgentRunId ??= orig.Audit.AgentRunId;
                row.ProjectSlug ??= orig.Audit.ProjectSlug;
            }
            row.DurationMs = (int)(DateTime.UtcNow - row.CreatedAt).TotalMilliseconds;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("parked command log finalize failed: {Msg}", ex.Message);
        }
    }

    public static string DescribeSlots(IReadOnlyDictionary<string, string> slots)
    {
        var parts = slots.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{kv.Key}: {(kv.Value.Length > 80 ? kv.Value[..80] + "…" : kv.Value.Trim())}")
            .ToList();
        return parts.Count == 0 ? "" : $" ({string.Join("; ", parts)})";
    }

    private static CommandContext BuildContext(string text, string source, IServiceProvider scoped)
        => new() { Utterance = text, Source = source, Services = scoped };
}

/// <summary>Holds the single most recent Mutate command awaiting spoken/clicked confirmation.</summary>
public class PendingActionService
{
    private readonly Lock _lock = new();
    private (string Name, string Utterance, Func<Task<CommandResult>> Action, DateTime ParkedAt, string? LogId, CommandContext? Ctx)? _pending;

    /// <summary>Set when confirm/cancel resolves a parked action; consumed by the router to finalize the audit row.</summary>
    public record ResolvedAction(string? LogId, CommandContext? Ctx, string Name, string Outcome);
    private ResolvedAction? _lastResolved;

    public void Park(string name, string utterance, Func<Task<CommandResult>> action,
        string? logId = null, CommandContext? ctx = null)
    {
        lock (_lock) _pending = (name, utterance, action, DateTime.UtcNow, logId, ctx);
    }

    public ResolvedAction? ConsumeResolved()
    {
        lock (_lock) { var r = _lastResolved; _lastResolved = null; return r; }
    }

    /// <summary>freshServices: the CONFIRMING request's scope — the parked command's own scope
    /// was disposed when its request ended, so the handler must run against a live one.</summary>
    public async Task<CommandResult> ConfirmAsync(IServiceProvider? freshServices = null)
    {
        (string Name, string Utterance, Func<Task<CommandResult>> Action, DateTime ParkedAt, string? LogId, CommandContext? Ctx)? p;
        lock (_lock) { p = _pending; _pending = null; }
        if (p is null) return new("There's nothing waiting for confirmation.");
        if (DateTime.UtcNow - p.Value.ParkedAt > TimeSpan.FromMinutes(2))
        {
            lock (_lock) _lastResolved = new(p.Value.LogId, p.Value.Ctx, p.Value.Name, "expired");
            return new("That request expired. Please repeat the original command.");
        }
        if (p.Value.Ctx is not null && freshServices is not null) p.Value.Ctx.Services = freshServices;
        lock (_lock) _lastResolved = new(p.Value.LogId, p.Value.Ctx, p.Value.Name, "confirmed");
        return await p.Value.Action();
    }

    public CommandResult Cancel()
    {
        lock (_lock)
        {
            if (_pending is null) return new("Nothing to cancel.");
            var (name, _, _, _, logId, ctx) = _pending.Value;
            _lastResolved = new(logId, ctx, name, "cancelled");
            _pending = null;
            return new($"Cancelled the pending {name} action.");
        }
    }
}
