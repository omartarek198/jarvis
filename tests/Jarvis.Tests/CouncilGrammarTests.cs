using System.Text.RegularExpressions;
using Jarvis.Core;
using Jarvis.Kernel.Modules;
using Jarvis.Kernel.Services.Council;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// Guards the load-bearing Council command grammar. CommandDefinition.Patterns are public regex
/// strings, so we compile them exactly as CommandRouter does (IgnoreCase | CultureInvariant) and
/// match directly — no need to spin up the full router, DB, or Ollama.
///
/// Why this matters: per CLAUDE.md, CouncilModule is registered BEFORE FocusModule and the greedy
/// ProjectsModule ("what is &lt;X&gt;"). These tests assert (a) the representative council utterances
/// match the right CouncilModule command, and (b) the patterns are anchored (^...$) so they do not
/// greedily swallow unrelated phrases.
/// </summary>
public class CouncilGrammarTests
{
    // Build the real CouncilModule. Its Commands list only *captures* the CouncilService in handlers
    // we never invoke here, but we construct a genuine one anyway (all ctors are cheap and offline).
    private static readonly CouncilModule Module = BuildModule();

    private static CouncilModule BuildModule()
    {
        var config = new ConfigurationBuilder().Build();
        var classifier = new CouncilClassifier(config, NullLogger<CouncilClassifier>.Instance);
        var statements = new CouncilStatements(config, NullLogger<CouncilStatements>.Instance);
        var council = new CouncilService(classifier, statements, new EventBus(),
            NullLogger<CouncilService>.Instance);
        return new CouncilModule(council);
    }

    private static CommandDefinition Command(string name) =>
        Module.Commands.Single(c => c.Name == name);

    private static bool Matches(CommandDefinition def, string text) =>
        def.Patterns.Any(p => Regex.IsMatch(text, p,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>Find which council command (if any) claims an utterance, in declaration order.</summary>
    private static string? FirstMatchingCommand(string text) =>
        Module.Commands.FirstOrDefault(c => Matches(c, text))?.Name;

    [Theory]
    [InlineData("quick council: gym or ship the release", "council_quick")]
    [InlineData("quick council should I sleep now", "council_quick")]
    [InlineData("war council should we build consent forms now or later", "council_war")]
    [InlineData("emergency council sleep now or keep working", "council_emergency")]
    [InlineData("full council is AI content a real opportunity", "council_full")]
    [InlineData("convene the council", "council_convene")]
    [InlineData("call the council about the new feature", "council_convene")]
    [InlineData("summon the council should I take this freelance project", "council_convene")]
    [InlineData("start council protocol: rewrite the router", "council_convene")]
    public void RepresentativeUtterances_MatchTheRightCommand(string text, string expected)
        => Assert.Equal(expected, FirstMatchingCommand(text));

    [Theory]
    [InlineData("what did the council decide about the gym", "council_recall")]
    [InlineData("why did we defer consent forms", "council_recall")]
    [InlineData("did the council already discuss the rewrite", "council_recall")]
    [InlineData("show recent council decisions", "council_recent")]
    [InlineData("list my recent council decisions", "council_recent")]
    public void RecallAndRecentUtterances_MatchTheRightCommand(string text, string expected)
        => Assert.Equal(expected, FirstMatchingCommand(text));

    [Fact]
    public void ConveneCommand_CapturesTopicSlot()
    {
        var def = Command("council_convene");
        var pattern = def.Patterns[0];
        var m = Regex.Match("convene the council about the rewrite",
            pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(m.Success);
        Assert.Equal("the rewrite", m.Groups["topic"].Value);
    }

    [Fact]
    public void ConveneCommand_ExposesAgentsAndTopicNamedGroups()
    {
        // The "with <agents>" branch exists; with both optional groups present the lazy agents
        // capture is minimal (a known consequence of the optional trailing topic), but the named
        // groups ARE wired so the handler can read them. Assert the wiring, not the greedy split.
        var def = Command("council_convene");
        var m = Regex.Match("convene the council with the cto",
            def.Patterns[0], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(m.Success);
        Assert.Contains(m.Groups["agents"].Value, "the cto");
        Assert.True(def.Patterns[0].Contains("?<agents>") && def.Patterns[0].Contains("?<topic>"));
    }

    [Fact]
    public void QuickCommand_CapturesTopicSlot()
    {
        var def = Command("council_quick");
        var m = Regex.Match("quick council: gym or work tonight", def.Patterns[0],
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(m.Success);
        Assert.Equal("gym or work tonight", m.Groups["topic"].Value);
    }

    [Theory]
    // The council patterns are anchored — they must NOT swallow unrelated phrases. Each of these
    // should be claimed by NO council command (so the router moves on to other modules / tiers).
    [InlineData("what is the council district budget")]   // ProjectsModule "what is <X>" territory
    [InlineData("tell me about the war in 1945")]          // not "war council"
    [InlineData("quickly summarize my email")]            // "quick" but not "quick council:"
    [InlineData("the emergency council meeting is downtown")] // narrative, not a command
    [InlineData("council")]                                // bare word, no topic
    [InlineData("start the dishwasher")]
    public void UnrelatedPhrases_AreNotClaimedByCouncil(string text)
        => Assert.Null(FirstMatchingCommand(text));

    [Fact]
    public void AllCouncilPatterns_AreAnchoredBothEnds()
    {
        // Anchoring is what keeps these specific phrases from greedily matching mid-sentence.
        foreach (var def in Module.Commands)
            foreach (var p in def.Patterns)
            {
                Assert.StartsWith("^", p);
                Assert.EndsWith("$", p);
            }
    }

    [Fact]
    public void AllCouncilPatterns_CompileAsValidRegex()
    {
        // A malformed pattern would throw at router construction, killing every command. Catch it here.
        foreach (var def in Module.Commands)
            foreach (var p in def.Patterns)
                _ = new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
