using Jarvis.Kernel.Services.Council;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// CouncilClassifier.ClassifyAsync is public. Its keyword table (the private FirstKeywordMatch) and
/// the War/Emergency mode overrides are fully deterministic and OFFLINE — they only fall through to
/// Ollama when NO keyword matches AND mode is not War/Emergency. Every case below is engineered to
/// take a keyword/override branch, so no network call is ever made and the test stays deterministic.
///
/// NOTE: topics with no keyword in Full/Quick mode would invoke Ollama (the classifier swallows the
/// failure and returns General), so those are intentionally NOT tested here — they are non-pure.
/// </summary>
public class CouncilClassifierTests
{
    private static CouncilClassifier NewClassifier()
    {
        var config = new ConfigurationBuilder().Build();
        return new CouncilClassifier(config, NullLogger<CouncilClassifier>.Instance);
    }

    [Theory]
    [InlineData("should I pull an all-nighter or sleep now", CouncilMode.Quick, DecisionCategory.HealthSleep)]
    [InlineData("hit the gym or keep coding", CouncilMode.Quick, DecisionCategory.HealthSleep)]
    [InlineData("should I invest in bitcoin", CouncilMode.Full, DecisionCategory.FinancialRisk)]
    [InlineData("time to refactor the database layer", CouncilMode.Full, DecisionCategory.Architecture)]
    [InlineData("launch the ad campaign or wait", CouncilMode.Full, DecisionCategory.MarketingSales)]
    [InlineData("build the onboarding flow for testers", CouncilMode.Full, DecisionCategory.Product)]
    public async Task ClassifyAsync_KeywordTable_DeterministicOffline(string topic, CouncilMode mode, DecisionCategory expected)
        => Assert.Equal(expected, await NewClassifier().ClassifyAsync(topic, mode));

    [Fact]
    public async Task ClassifyAsync_WarMode_NoKeyword_FallsToProduct()
    {
        // War mode short-circuits to Product without any keyword and without Ollama.
        Assert.Equal(DecisionCategory.Product,
            await NewClassifier().ClassifyAsync("some vague strategic question", CouncilMode.War));
    }

    [Fact]
    public async Task ClassifyAsync_WarMode_PullsTimeAllocationToProduct()
    {
        // "tonight ... or ..." keyword resolves to TimeAllocation, but War mode pulls it to product.
        Assert.Equal(DecisionCategory.Product,
            await NewClassifier().ClassifyAsync("tonight should I do this or that", CouncilMode.War));
    }

    [Fact]
    public async Task ClassifyAsync_EmergencyMode_KeepsConcreteHealthDomain()
    {
        Assert.Equal(DecisionCategory.HealthSleep,
            await NewClassifier().ClassifyAsync("I am exhausted, sleep now?", CouncilMode.Emergency));
    }

    [Fact]
    public async Task ClassifyAsync_EmergencyMode_GenericTopic_BecomesEmergencyRisk()
    {
        Assert.Equal(DecisionCategory.EmergencyRisk,
            await NewClassifier().ClassifyAsync("the server is on fire what do I do", CouncilMode.Emergency));
    }
}
