using Jarvis.Kernel.Services.Council;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// CouncilAgents (CouncilAgents.cs) is a static, fully deterministic, offline class: the alias
/// resolver, selection matrix, vote-weight matrix, and veto map. Exactly the load-bearing tonight
/// work to lock in.
/// </summary>
public class CouncilAgentsTests
{
    [Theory]
    // exact key / display-name match
    [InlineData("ceo", "ceo")]
    [InlineData("CEO", "ceo")]
    [InlineData("Health Coach", "health")]
    // fuzzy speech aliases (STT-friendly)
    [InlineData("the tech guy", "cto")]        // "tech" -> cto
    [InlineData("the body coach", "health")]   // "coach"/"body" -> health
    [InlineData("sceptic", "skeptic")]          // British spelling alias
    [InlineData("talk to the money person", "investor")] // "money" -> investor
    [InlineData("our boss", "ceo")]             // "boss" -> ceo
    public void ByKey_ResolvesExactAndFuzzy(string input, string expectedKey)
        => Assert.Equal(expectedKey, CouncilAgents.ByKey(input)?.Key);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonexistent advisor")]
    public void ByKey_UnknownOrEmpty_ReturnsNull(string input)
        => Assert.Null(CouncilAgents.ByKey(input));

    [Theory]
    [InlineData("doctor")]
    [InlineData("the doctor")]
    [InlineData("ask the doctor to weigh in")]
    public void ByKey_Doctor_ResolvesToHealth(string input)
    {
        // Regression: ByKey matches aliases on WORD BOUNDARIES, not substring. Previously
        // "do(cto)r" matched the "cto" alias and resolved to CTO; now "doctor" correctly maps
        // to the Health Coach and "cto" only matches the standalone token.
        Assert.Equal("health", CouncilAgents.ByKey(input)?.Key);
    }

    [Fact]
    public void Select_QuickMode_ReturnsExactlyThree()
    {
        foreach (DecisionCategory cat in Enum.GetValues<DecisionCategory>())
            Assert.Equal(3, CouncilAgents.Select(cat, CouncilMode.Quick).Length);
    }

    [Fact]
    public void Select_EmergencyMode_ReturnsUpToFour()
    {
        // Emergency takes the first 4 of the roster — but PersonalMaintenance/SocialFun only have 3.
        Assert.Equal(4, CouncilAgents.Select(DecisionCategory.HealthSleep, CouncilMode.Emergency).Length);
        Assert.Equal(3, CouncilAgents.Select(DecisionCategory.SocialFun, CouncilMode.Emergency).Length);
    }

    [Fact]
    public void Select_HealthSleep_Quick_HasHealthFirst()
    {
        var roster = CouncilAgents.Select(DecisionCategory.HealthSleep, CouncilMode.Quick);
        Assert.Equal(new[] { "health", "operator", "future" }, roster.Select(a => a.Key).ToArray());
    }

    [Fact]
    public void Select_Architecture_Full_HasCtoLeading()
    {
        var roster = CouncilAgents.Select(DecisionCategory.Architecture, CouncilMode.Full);
        Assert.Equal(new[] { "cto", "operator", "skeptic", "ceo" }, roster.Select(a => a.Key).ToArray());
    }

    [Fact]
    public void Select_Product_Full_HasFullFiveLensRoster()
    {
        var roster = CouncilAgents.Select(DecisionCategory.Product, CouncilMode.Full);
        Assert.Equal(new[] { "ceo", "customer", "operator", "cto", "skeptic" },
            roster.Select(a => a.Key).ToArray());
    }

    [Theory]
    // The signature weighting: health is the loudest voice on health/sleep decisions.
    [InlineData(DecisionCategory.HealthSleep, "health", 3.0)]
    [InlineData(DecisionCategory.EmergencyRisk, "health", 3.0)]
    [InlineData(DecisionCategory.Architecture, "cto", 3.0)]
    [InlineData(DecisionCategory.FinancialRisk, "investor", 3.0)]
    [InlineData(DecisionCategory.FinancialRisk, "skeptic", 3.0)]
    // unlisted pairs weigh 1.0
    [InlineData(DecisionCategory.HealthSleep, "skeptic", 1.0)]
    [InlineData(DecisionCategory.SocialFun, "future", 1.0)]
    [InlineData(DecisionCategory.General, "ceo", 1.0)]
    public void WeightFor_MatchesMatrix(DecisionCategory cat, string key, double expected)
        => Assert.Equal(expected, CouncilAgents.WeightFor(cat, key));

    [Theory]
    [InlineData(DecisionCategory.HealthSleep, "health")]
    [InlineData(DecisionCategory.EmergencyRisk, "health")]
    [InlineData(DecisionCategory.PersonalMaintenance, "health")]
    [InlineData(DecisionCategory.FinancialRisk, "investor")]
    [InlineData(DecisionCategory.Architecture, "skeptic")]  // default veto holder
    [InlineData(DecisionCategory.General, "skeptic")]
    public void VetoHolder_MapsCorrectly(DecisionCategory cat, string expected)
        => Assert.Equal(expected, CouncilAgents.VetoHolder(cat));
}
