using System.Text.RegularExpressions;
using Jarvis.Core;
using Jarvis.Kernel.Modules;
using Xunit;

namespace Jarvis.Tests;

/// <summary>
/// Guards the email_send grammar. Like CouncilGrammarTests, we compile the public CommandDefinition
/// patterns exactly as CommandRouter does and match directly — no DB, SMTP, or Ollama needed.
/// The handler captures EmailService/ImapEmailService it never invokes here, so we pass nulls.
/// </summary>
public class EmailGrammarTests
{
    private static readonly EmailModule Module = new(email: null!, imap: null!);

    private static CommandDefinition Send => Module.Commands.Single(c => c.Name == "email_send");

    private static Match? Match(string text) => Send.Patterns
        .Select(p => Regex.Match(text, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .FirstOrDefault(m => m.Success);

    [Theory]
    [InlineData("send a hi to this email sam@example.com", "sam@example.com", "a hi")]
    [InlineData("send a hi to sam@example.com", "sam@example.com", "a hi")]
    [InlineData("email john@example.com saying running five minutes late", "john@example.com", "running five minutes late")]
    [InlineData("shoot a thanks to maria@contoso.com", "maria@contoso.com", "a thanks")]
    public void Matches_AndCapturesSlots(string text, string to, string msg)
    {
        var m = Match(text);
        Assert.NotNull(m);
        Assert.Equal(to, m!.Groups["to"].Value);
        Assert.Equal(msg, m.Groups["msg"].Value);
    }

    [Theory]
    [InlineData("email me the morning briefing")]   // belongs to email_briefing — no @address
    [InlineData("send me my open loops")]           // belongs to email_open_loops
    [InlineData("any new email")]                   // belongs to email_unread
    public void DoesNotSwallow_OtherEmailCommands(string text) => Assert.Null(Match(text));

    [Fact]
    public void Send_IsMutateGated() => Assert.Equal(SafetyClass.Mutate, Send.Safety);
}
