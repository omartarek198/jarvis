namespace Jarvis.Core;

public enum SafetyClass { Read, WriteLocal, Mutate }

public record CommandResult(string Spoken, object? Data = null)
{
    /// <summary>Set when a Mutate command is parked awaiting "confirm".</summary>
    public bool AwaitingConfirmation { get; init; }
    /// <summary>Handler looked at the match and declined it — router keeps trying lower tiers.</summary>
    public bool Unhandled { get; init; }
}

public class CommandContext
{
    public required string Utterance { get; init; }
    public required string Source { get; init; }          // voice | ui | cli | relay | scheduler
    /// <summary>Settable (not init): a parked Mutate command's scope is disposed by the time
    /// 'confirm' arrives, so the confirming request re-points this at its own live scope.</summary>
    public required IServiceProvider Services { get; set; }
    public Dictionary<string, string> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Handlers enrich the command audit trail here (what tool ran, where the log is).</summary>
    public CommandAudit Audit { get; } = new();

    public string? Slot(string name)
        => Slots.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Handler-reported audit details, merged into the CommandLog row by the router.</summary>
public class CommandAudit
{
    public string? Tool { get; set; }
    public string? LogRef { get; set; }
    public string? AgentRunId { get; set; }
    public string? ProjectSlug { get; set; }
}

public class CommandDefinition
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Regex patterns with named groups; matched case-insensitively against the normalized utterance.</summary>
    public required string[] Patterns { get; init; }
    public SafetyClass Safety { get; init; } = SafetyClass.Read;
    /// <summary>Slot names the LLM tier should try to extract.</summary>
    public string[] SlotHints { get; init; } = [];
    /// <summary>Example utterances for the semantic router — embedded at startup; the more
    /// collision-prone the command, the more examples it should carry.</summary>
    public string[] Examples { get; init; } = [];
    public required Func<CommandContext, Task<CommandResult>> Handler { get; init; }
}

public interface IJarvisModule
{
    string Name { get; }
    IEnumerable<CommandDefinition> Commands { get; }
}
