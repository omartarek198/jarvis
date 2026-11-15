namespace Jarvis.Kernel.Services.Council;

public enum DecisionCategory
{
    Product, Architecture, BusinessRevenue, MarketingSales, HealthSleep,
    PersonalMaintenance, TimeAllocation, SocialFun, FinancialRisk, ShinyObject,
    EmergencyRisk, General,
}

public enum CouncilMode { Quick, Full, War, Emergency }

/// <summary>One decision lens. Static definitions — they version with code, not data.</summary>
public record CouncilAgentDef(
    string Key,
    string DisplayName,
    string Lens,
    /// <summary>The agent's signature rhetorical move — what its statement MUST do. This is
    /// what keeps four advisers from converging on the same generic sentence.</summary>
    string StyleRule,
    /// <summary>Offline position bias when Ollama is unavailable: now | later | reject | compromise.</summary>
    string FallbackBias,
    string VoiceProfile);

public static class CouncilAgents
{
    public static readonly CouncilAgentDef Ceo = new("ceo", "CEO",
        "revenue, first paying customer, demo strength, market timing, business momentum, opportunity cost.",
        "Tie the argument to the first paying customer or the next demo. Name the revenue-relevant thing that changes — or admit nothing does.",
        "now", "council.ceo");

    public static readonly CouncilAgentDef Cto = new("cto", "CTO",
        "architecture, technical risk, regression risk, test coverage, maintainability, implementation complexity.",
        "Name the specific component or risk area this touches and the boundary or test that's missing. No abstract risk talk.",
        "later", "council.cto");

    public static readonly CouncilAgentDef Operator = new("operator", "Operator",
        "what can actually be done today, execution sequence, energy available, dependencies, the next physical action.",
        "Prescribe the concrete next block: which task, how long, starting at what time tonight. Use actual clock times.",
        "compromise", "council.operator");

    public static readonly CouncilAgentDef Investor = new("investor", "Investor",
        "expected value, ROI, time/money tradeoff, risk-adjusted upside, leverage, opportunity cost.",
        "State the explicit trade: these hours on option A versus option B, and which has the better risk-adjusted return. Show the comparison.",
        "later", "council.investor");

    public static readonly CouncilAgentDef Health = new("health", "Health Coach",
        "sleep, gym, food, burnout, stimulants, sustainability, physical condition, mental clarity.",
        "Use the actual numbers: hours slept, current time, what tomorrow demands. Prescribe a concrete sleep/wake plan, not a platitude.",
        "reject", "council.health");

    public static readonly CouncilAgentDef Skeptic = new("skeptic", "Skeptic",
        "hidden assumptions, self-deception, shiny object syndrome, FOMO, avoidance, failure modes.",
        "Name the specific self-deception or avoidance at play in THIS decision. One pointed question allowed. Never restate others' risks.",
        "later", "council.skeptic");

    public static readonly CouncilAgentDef Future = new("future", "Future You",
        "regret minimization, long-term identity, the three-month version of you, tomorrow-morning reflection.",
        "Run the regret test concretely: paint the two versions of the relevant morning-after and say which one you keep.",
        "compromise", "council.future");

    public static readonly CouncilAgentDef Customer = new("customer", "Customer Voice",
        "real customer workflows, day-to-day usability, the moments where the product is actually used.",
        "Ground it in one real customer moment — the screen they are on, the task they are trying to finish. Would a paying customer even notice this?",
        "later", "council.customer");

    public static readonly CouncilAgentDef Sales = new("sales", "Sales",
        "lead generation, demo conversion, messaging, ads, landing page, the first-customer path.",
        "Judge only by what the first demo or the ad shows. Name what the prospect would see differently — or concede it's invisible to them.",
        "now", "council.sales");

    public static readonly CouncilAgentDef[] All =
        [Ceo, Cto, Operator, Investor, Health, Skeptic, Future, Customer, Sales];

    /// <summary>Speech-friendly aliases — STT yields "tech guy", "the doctor", "coach", etc.
    /// Each maps a substring to an agent key; first containment match wins.</summary>
    private static readonly (string Alias, string Key)[] Aliases =
    [
        ("cto", "cto"), ("chief technology", "cto"), ("tech", "cto"), ("engineer", "cto"), ("architect", "cto"),
        ("ceo", "ceo"), ("chief executive", "ceo"), ("business", "ceo"), ("boss", "ceo"),
        ("operator", "operator"), ("execution", "operator"), ("tactical", "operator"), ("doer", "operator"),
        ("investor", "investor"), ("money", "investor"), ("finance", "investor"), ("roi", "investor"),
        ("health", "health"), ("coach", "health"), ("doctor", "health"), ("body", "health"), ("sleep", "health"),
        ("skeptic", "skeptic"), ("sceptic", "skeptic"), ("critic", "skeptic"), ("cynic", "skeptic"), ("devil", "skeptic"),
        ("future", "future"), ("tomorrow", "future"), ("long term", "future"), ("older", "future"),
        ("customer", "customer"), ("client", "customer"), ("product", "customer"),
        ("sales", "sales"), ("marketing", "sales"), ("growth", "sales"),
    ];

    public static CouncilAgentDef? ByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        var exact = All.FirstOrDefault(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)
            || a.DisplayName.Equals(k, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var lower = k.ToLowerInvariant();
        // Word-boundary match, not substring — otherwise "do(cto)r" matches the "cto" alias.
        foreach (var (alias, akey) in Aliases)
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{System.Text.RegularExpressions.Regex.Escape(alias)}\b"))
                return All.First(a => a.Key == akey);
        return null;
    }

    /// <summary>Selection matrix: category → agents, quick mode takes the first 3.</summary>
    public static CouncilAgentDef[] Select(DecisionCategory category, CouncilMode mode)
    {
        CouncilAgentDef[] roster = category switch
        {
            DecisionCategory.HealthSleep => [Health, Operator, Future, Skeptic],
            DecisionCategory.Product => [Ceo, Customer, Operator, Cto, Skeptic],
            DecisionCategory.Architecture => [Cto, Operator, Skeptic, Ceo],
            DecisionCategory.BusinessRevenue => [Ceo, Investor, Operator, Skeptic, Sales],
            DecisionCategory.MarketingSales => [Sales, Ceo, Operator, Investor, Skeptic],
            DecisionCategory.FinancialRisk => [Investor, Skeptic, Future, Operator, Health],
            DecisionCategory.ShinyObject => [Skeptic, Investor, Operator, Future, Ceo],
            DecisionCategory.TimeAllocation => [Operator, Future, Skeptic, Health, Ceo],
            DecisionCategory.PersonalMaintenance => [Health, Operator, Future],
            DecisionCategory.SocialFun => [Future, Health, Operator],
            DecisionCategory.EmergencyRisk => [Health, Skeptic, Future, Operator],
            _ => [Operator, Skeptic, Future, Ceo, Investor],
        };
        return mode switch
        {
            CouncilMode.Quick => [.. roster.Take(3)],
            CouncilMode.Emergency => [.. roster.Take(4)],
            _ => roster,
        };
    }

    /// <summary>Category weight matrix. Anything unlisted weighs 1.0.</summary>
    public static double WeightFor(DecisionCategory category, string agentKey) => (category, agentKey) switch
    {
        (DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk, "health") => 3.0,
        (DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk, "future") => 2.0,
        (DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk, "operator") => 1.5,
        (DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk, "ceo") => 0.5,
        (DecisionCategory.Product, "ceo") => 2.0,
        (DecisionCategory.Product, "customer") => 2.0,
        (DecisionCategory.Product, "cto") => 1.5,
        (DecisionCategory.Product, "operator") => 1.5,
        (DecisionCategory.Architecture, "cto") => 3.0,
        (DecisionCategory.Architecture, "operator") => 2.0,
        (DecisionCategory.Architecture, "skeptic") => 1.5,
        (DecisionCategory.FinancialRisk, "investor") => 3.0,
        (DecisionCategory.FinancialRisk, "skeptic") => 3.0,
        (DecisionCategory.FinancialRisk, "future") => 2.0,
        (DecisionCategory.ShinyObject, "skeptic") => 2.0,
        (DecisionCategory.ShinyObject, "investor") => 1.5,
        (DecisionCategory.MarketingSales, "sales") => 2.0,
        (DecisionCategory.MarketingSales, "ceo") => 1.5,
        _ => 1.0,
    };

    /// <summary>The agent whose 'reject' can veto in emergency-grade situations.</summary>
    public static string VetoHolder(DecisionCategory category) => category switch
    {
        DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk or DecisionCategory.PersonalMaintenance => "health",
        DecisionCategory.FinancialRisk => "investor",
        _ => "skeptic",
    };
}
