using System.Text;
using System.Text.Json;

namespace Jarvis.Kernel.Services.Council;

public record AgentStatement(
    string AgentKey, string Statement, string Position, int Confidence,
    string Risk, string Action, bool FromFallback);

/// <summary>
/// Generates one short argument + vote per agent — SEQUENTIALLY, each agent seeing what the
/// others already said and forbidden from repeating it. Forced specificity (must cite a real
/// fact: day, hours, deadline, named task) plus a per-agent signature move keep the lenses
/// from converging into the same NPC sentence. Hand-written deterministic templates remain
/// the offline floor — the council must stay useful with no LLM at all.
/// </summary>
public class CouncilStatements(IConfiguration config, ILogger<CouncilStatements> log)
{
    // 30s per statement: the 7B council model is slower than the router model, but a truly
    // stalled call must still drop to the deterministic template rather than freeze the session.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string BaseUrl => config["Jarvis:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    /// <summary>Council debates deserve the strongest local model — quality of argument is the product.</summary>
    private string Model => config["Jarvis:Ollama:CouncilModel"] ?? "qwen2.5:7b";
    private static readonly string[] Positions = ["now", "later", "reject", "compromise", "ask_more"];

    public async Task<AgentStatement> GenerateOneAsync(
        CouncilAgentDef agent, string decision, DecisionCategory category,
        IReadOnlyDictionary<string, string> facts, CouncilMode mode,
        IReadOnlyList<(string DisplayName, AgentStatement Statement)> prior)
    {
        var generated = await TryOllamaAsync(agent, decision, category, facts, mode, prior);
        return generated ?? Fallback(agent, decision, category, facts, mode);
    }

    private async Task<AgentStatement?> TryOllamaAsync(
        CouncilAgentDef agent, string decision, DecisionCategory category,
        IReadOnlyDictionary<string, string> facts, CouncilMode mode,
        IReadOnlyList<(string DisplayName, AgentStatement Statement)> prior)
    {
        try
        {
            var factsText = facts.Count == 0 ? "none recorded"
                : string.Join("\n", facts.Select(f => $"- {f.Key}: {f.Value}"));
            var priorText = prior.Count == 0 ? ""
                : "\nWHAT THE OTHERS ALREADY SAID:\n" + string.Join("\n",
                    prior.Select(p => $"- {p.DisplayName} ({p.Statement.Position}): {p.Statement.Statement}"));
            var cap = mode == CouncilMode.Quick ? "two" : "three";
            var tone = mode == CouncilMode.Emergency
                ? "Be blunt. If one option has serious downside, say so plainly — don't both-sides it."
                : "Be direct, like a sharp adviser who knows you well — not a press release.";

            var prompt = $$"""
                You are the {{agent.DisplayName}} in the user's private decision council — several advisers
                who each see the same decision differently and argue. The user is a solo founder building
                a software product toward its first paying customer, alongside a full-time job.

                DECISION ON THE TABLE: "{{decision}}"
                KNOWN FACTS:
                {{factsText}}
                {{priorText}}

                YOUR LENS: {{agent.Lens}}
                YOUR SIGNATURE MOVE (your statement MUST do this): {{agent.StyleRule}}

                Hard rules:
                - {{tone}}
                - Cite at least one SPECIFIC detail that is ACTUALLY STATED in the decision or facts
                  (a day, an hour count, a time, a deadline). Generic advice that fits any decision
                  is worthless — but NEVER invent tasks, people, commitments, or events that are not
                  stated. If you don't know a detail, don't make one up.
                - NEVER repeat a point, risk, or recommended action another adviser already gave.
                  If you agree, add a NEW angle; if their argument has a hole, attack the hole.
                - {{cap}} sentences maximum. No greetings, no addressing other advisers by name,
                  and speak about the user as "you", never first-person as the user.

                position meaning: now = do it / yes to the first option; later = defer; reject = don't;
                compromise = a smaller middle path (describe it in action); ask_more = a critical fact is missing.

                Reply with ONLY JSON:
                {"statement":"...","position":"now|later|reject|compromise|ask_more","confidence":0-100,"risk":"one specific risk","action":"one concrete next action with a time or size"}
                """;

            var body = JsonSerializer.Serialize(new
            {
                model = Model, stream = false, format = "json",
                options = new { temperature = 0.7, top_p = 0.9, repeat_penalty = 1.12, num_predict = 260 },
                prompt,
            });
            var resp = await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            using var parsed = JsonDocument.Parse(doc.RootElement.GetProperty("response").GetString() ?? "{}");
            var root = parsed.RootElement;
            var statement = root.TryGetProperty("statement", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(statement)) return null;
            var position = root.TryGetProperty("position", out var p) ? (p.GetString() ?? "") : "";
            if (!Positions.Contains(position)) position = "compromise";
            var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var ci)
                ? Math.Clamp(ci, 0, 100) : 60;
            return new AgentStatement(agent.Key, CapWords(statement!, mode == CouncilMode.Quick ? 50 : 75),
                position, confidence,
                root.TryGetProperty("risk", out var r) ? r.GetString() ?? "" : "",
                root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                FromFallback: false);
        }
        catch (Exception ex)
        {
            log.LogWarning("council statement ({Agent}) fell back to template: {Msg}", agent.Key, ex.Message);
            return null;
        }
    }

    /// <summary>Cross-examination / follow-up line. Returns null offline — enrichment, never templated noise.</summary>
    public async Task<string?> ChallengeAsync(CouncilAgentDef agent, string decision, string targetStatement, string instruction)
    {
        try
        {
            var prompt = $$"""
                You are the {{agent.DisplayName}} ({{agent.Lens}}) in the user's decision council,
                debating: "{{decision}}".
                Another adviser argued: "{{targetStatement}}"
                {{instruction}} You must add something NEW — a fresh angle or an honest concession — not a
                restatement of any prior point. NEVER invent tasks, people, or commitments that were not
                stated; if you have no genuine new argument, concede in one sentence instead.
                Maximum two short sentences. No greetings, no theatrics.
                Reply with ONLY JSON: {"statement":"..."}
                """;
            var body = JsonSerializer.Serialize(new
            {
                model = Model, stream = false, format = "json",
                options = new { temperature = 0.7, top_p = 0.9, repeat_penalty = 1.12, num_predict = 160 },
                prompt,
            });
            var resp = await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            using var parsed = JsonDocument.Parse(doc.RootElement.GetProperty("response").GetString() ?? "{}");
            var statement = parsed.RootElement.TryGetProperty("statement", out var st) ? st.GetString() : null;
            return string.IsNullOrWhiteSpace(statement) ? null : CapWords(statement!, 50);
        }
        catch (Exception ex)
        {
            log.LogWarning("council challenge ({Agent}) skipped: {Msg}", agent.Key, ex.Message);
            return null;
        }
    }

    /// <summary>Deterministic offline statements: the lens applied to the category, no LLM.</summary>
    private static AgentStatement Fallback(
        CouncilAgentDef agent, string decision, DecisionCategory category,
        IReadOnlyDictionary<string, string> facts, CouncilMode mode)
    {
        var lowSleep = facts.TryGetValue("hours_slept", out var hs)
            && double.TryParse(hs, out var hours) && hours < 6;

        (string Text, string Position, int Confidence, string Risk, string Action) t = agent.Key switch
        {
            "health" when lowSleep => (
                $"On {hs} hours of sleep this is an energy problem, not a planning problem. Sleep first; everything else degrades without it.",
                "reject", 90, "decision quality collapses under sleep debt", "sleep now, revisit after a full night"),
            "health" => (
                "Sustainable output beats one heroic push. Protect sleep, food, and movement around whatever is chosen.",
                "compromise", 65, "burnout creep", "timebox the work and lock a hard stop"),
            "ceo" => (
                "This matters only if it moves the product closer to a paying customer or a stronger demo. If it does neither, it is not urgent.",
                category is DecisionCategory.BusinessRevenue or DecisionCategory.MarketingSales ? "now" : "later",
                70, "losing market momentum while polishing internals", "tie the choice to the first-customer path"),
            "cto" => (
                "Attractive, but it touches risky areas with thin test coverage. Without boundaries and tests it creates regressions.",
                "later", 70, "regression risk in untested areas", "write a short spec and tests before any build"),
            "operator" => (
                "This debate is useless unless the next 45 minutes are defined. Pick the smallest executable step and start it.",
                "compromise", 75, "overplanning instead of executing", "define and start a 45-minute block now"),
            "investor" => (
                "These hours must be compared against the highest-probability money path. Default to the option with better risk-adjusted return.",
                category == DecisionCategory.FinancialRisk ? "reject" : "later",
                70, "opportunity cost is invisible until too late", "allocate a fixed, small budget of time or money — no more"),
            "skeptic" => (
                "This sounds productive, but it is probably avoidance of the harder, more important task. Name what is being avoided first.",
                "later", 65, "self-deception dressed as progress", "write down what this is displacing, then decide"),
            "future" => (
                "Tomorrow's you will not care about the exciting detour. You will care whether the real commitment moved forward.",
                "compromise", 65, "regret over misallocated weeks", "do the committed thing first, then the new thing"),
            "customer" => (
                "A customer forgives a missing advanced feature. They never forgive a confusing core workflow. Judge it by what customers actually do every day.",
                "later", 70, "building for builders, not for customers", "validate with a real customer workflow before building"),
            "sales" => (
                "If this does not change what the first demo or the ad shows, it is not urgent. If it does, it is.",
                category == DecisionCategory.MarketingSales ? "now" : "later",
                65, "shipping things no prospect ever sees", "check it against the demo script"),
            _ => ("No strong signal from this lens.", "compromise", 40, "", "gather one more fact"),
        };

        return new AgentStatement(agent.Key, t.Text, t.Position, t.Confidence, t.Risk, t.Action, FromFallback: true);
    }

    private static string CapWords(string text, int maxWords)
    {
        // Small models occasionally emit replacement/control glyphs mid-sentence — strip them.
        text = new string(text.Where(c => c != '�' && (!char.IsControl(c) || c == '\n')).ToArray());
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return text.Trim();
        var cut = string.Join(' ', words.Take(maxWords));
        var lastStop = cut.LastIndexOfAny(['.', '!', '?']);
        return lastStop > 40 ? cut[..(lastStop + 1)] : cut + "…";
    }
}
