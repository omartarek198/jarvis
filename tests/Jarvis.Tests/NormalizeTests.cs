using Jarvis.Kernel.Services;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// CommandRouter.Normalize (CommandRouter.cs ~line 49) is the one piece of the router that is
/// pure, static, and public — so it is directly testable without the whole DI graph. It strips
/// a leading/trailing "Jarvis" vocative, collapses whitespace, and trims trailing punctuation.
/// </summary>
public class NormalizeTests
{
    [Theory]
    // leading vocative, with and without "hey", with assorted separators
    [InlineData("Jarvis, what's the weather", "what's the weather")]
    [InlineData("jarvis what's the weather", "what's the weather")]
    [InlineData("Hey Jarvis, morning briefing", "morning briefing")]
    [InlineData("hey jarvis! war council ship it", "war council ship it")]
    // trailing vocative must not leak "jarvis" into a slot
    [InlineData("tell me about the weather, Jarvis", "tell me about the weather")]
    [InlineData("what changed today jarvis?", "what changed today")]
    [InlineData("good night, hey jarvis", "good night")]
    // whitespace collapse
    [InlineData("quick    council:   gym or   work", "quick council: gym or work")]
    [InlineData("  convene the council  ", "convene the council")]
    // trailing punctuation trim
    [InlineData("status report!!!", "status report")]
    [InlineData("are you there?", "are you there")]
    // no vocative, nothing to strip
    [InlineData("list my projects", "list my projects")]
    public void Normalize_CleansVocativeWhitespaceAndPunctuation(string input, string expected)
        => Assert.Equal(expected, CommandRouter.Normalize(input));

    [Fact]
    public void Normalize_DoesNotStripInteriorJarvis()
    {
        // "Jarvis" only as a leading/trailing vocative is removed — a mid-sentence mention stays,
        // otherwise "what is the jarvis project" would lose its subject.
        Assert.Equal("what is the jarvis project", CommandRouter.Normalize("what is the jarvis project"));
    }

    [Fact]
    public void Normalize_BareVocativeWithSeparator_CollapsesToEmpty()
    {
        // "Jarvis!" (vocative + trailing separator) is fully stripped by the leading rule, then the
        // trailing punctuation trim removes the rest — router then replies "I didn't catch anything".
        Assert.Equal("", CommandRouter.Normalize("Jarvis!"));
    }

    [Fact]
    public void Normalize_BareVocativeNoSeparator_IsLeftIntact()
    {
        // Both vocative rules require a separator adjacent to "jarvis"; a lone "Jarvis" with no
        // punctuation matches neither rule, so it is returned unchanged. Documenting actual behavior.
        Assert.Equal("Jarvis", CommandRouter.Normalize("Jarvis"));
    }
}
