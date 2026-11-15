using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Kernel.Services.Council;

/// <summary>Two-tier decision classification: deterministic keyword table first (fast, offline),
/// Ollama JSON fallback for ambiguous topics. Never throws — worst case is General.</summary>
public class CouncilClassifier(IConfiguration config, ILogger<CouncilClassifier> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string BaseUrl => config["Jarvis:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    private string Model => config["Jarvis:Ollama:AnswerModel"] ?? config["Jarvis:Ollama:Model"] ?? "qwen2.5:3b";

    private static readonly (Regex Rx, DecisionCategory Cat)[] Keywords =
    [
        (new(@"\b(sleep|all.?nighter|tired|exhausted|nap|stay up|awake|melatonin|caffeine|energy drink)\b", RegexOptions.IgnoreCase), DecisionCategory.HealthSleep),
        (new(@"\b(gym|workout|exercise|run|food|eat|meal|burnout|health)\b", RegexOptions.IgnoreCase), DecisionCategory.HealthSleep),
        (new(@"\b(crypto|bitcoin|coin|trade|trading|invest in|stock|leverage|futures)\b", RegexOptions.IgnoreCase), DecisionCategory.FinancialRisk),
        (new(@"\b(buy|spend|subscription|pay for|purchase)\b", RegexOptions.IgnoreCase), DecisionCategory.FinancialRisk),
        (new(@"\b(architecture|refactor|tech(nical)? debt|rewrite|migration|database|framework)\b", RegexOptions.IgnoreCase), DecisionCategory.Architecture),
        (new(@"\b(ad|ads|marketing|landing page|campaign|launch the ad|lead|demo conversion)\b", RegexOptions.IgnoreCase), DecisionCategory.MarketingSales),
        (new(@"\b(tester|testers|onboarding|billing|invoice|bug report|user feedback)\b", RegexOptions.IgnoreCase), DecisionCategory.Product),
        (new(@"\b(feature|ship|build .* now|defer|backlog|release|launch)\b", RegexOptions.IgnoreCase), DecisionCategory.Product),
        (new(@"\b(client|customer|freelance|pricing|revenue|paying|deal|contract)\b", RegexOptions.IgnoreCase), DecisionCategory.BusinessRevenue),
        (new(@"\b(new idea|opportunity|fomo|shiny|trend|ai content|should i (try|learn|start))\b", RegexOptions.IgnoreCase), DecisionCategory.ShinyObject),
        (new(@"\b(tonight|today|this weekend|prioriti[sz]e|spend (the )?(night|day|evening)|or work on| or |versus|vs\.?)\b", RegexOptions.IgnoreCase), DecisionCategory.TimeAllocation),
        (new(@"\b(friends?|party|movie|game|hang out|social)\b", RegexOptions.IgnoreCase), DecisionCategory.SocialFun),
    ];

    public async Task<DecisionCategory> ClassifyAsync(string topic, CouncilMode mode)
    {
        // Mode overrides narrow the space before any matching.
        if (mode == CouncilMode.Emergency)
        {
            var kw = FirstKeywordMatch(topic);
            // Emergency keeps the concrete domain when one is obvious (sleep, money), else generic emergency.
            return kw is DecisionCategory.HealthSleep or DecisionCategory.FinancialRisk ? kw.Value : DecisionCategory.EmergencyRisk;
        }

        var match = FirstKeywordMatch(topic);
        if (match is not null)
        {
            // War mode pulls generic matches toward the product/business family.
            if (mode == CouncilMode.War && match is DecisionCategory.TimeAllocation or DecisionCategory.General)
                return DecisionCategory.Product;
            return match.Value;
        }
        if (mode == CouncilMode.War) return DecisionCategory.Product;

        return await OllamaClassifyAsync(topic) ?? DecisionCategory.General;
    }

    private static DecisionCategory? FirstKeywordMatch(string topic)
    {
        foreach (var (rx, cat) in Keywords)
            if (rx.IsMatch(topic)) return cat;
        return null;
    }

    private async Task<DecisionCategory?> OllamaClassifyAsync(string topic)
    {
        try
        {
            var names = string.Join(", ", Enum.GetNames<DecisionCategory>());
            var prompt = $$"""
                Classify this personal/business decision into exactly one category.
                Categories: {{names}}
                Decision: "{{topic}}"
                Reply with ONLY JSON: {"category":"<name>"}
                """;
            var body = JsonSerializer.Serialize(new
            { model = Model, stream = false, format = "json", options = new { temperature = 0.0 }, prompt });
            var resp = await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            using var parsed = JsonDocument.Parse(doc.RootElement.GetProperty("response").GetString() ?? "{}");
            if (parsed.RootElement.TryGetProperty("category", out var c)
                && Enum.TryParse<DecisionCategory>(c.GetString(), ignoreCase: true, out var cat))
                return cat;
        }
        catch (Exception ex) { log.LogWarning("council classify fallback: {Msg}", ex.Message); }
        return null;
    }
}
