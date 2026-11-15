namespace Jarvis.Core;

public abstract class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Project : Entity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    /// <summary>product | fulltime | freelance | personal | hobby | crypto | system</summary>
    public string Type { get; set; } = "personal";
    public string Summary { get; set; } = "";
    public string Goals { get; set; } = "";
    /// <summary>active | paused | done</summary>
    public string Status { get; set; } = "active";
    public string BrainPath { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string NextAction { get; set; } = "";
    public string Aliases { get; set; } = "";   // comma-separated speech aliases
}

public class Repo : Entity
{
    public string ProjectId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MachineId { get; set; }
    public bool IsPrimary { get; set; } = true;
    public string BuildCommand { get; set; } = "";
    public string TestCommand { get; set; } = "";
    public string? PlaywrightConfig { get; set; }
}

public class Person : Entity
{
    public string Name { get; set; } = "";
    public string Aliases { get; set; } = "";
    public string Org { get; set; } = "";
    public string Role { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>VIPs trigger proactive announcements (Teams messages, emails).</summary>
    public bool IsVip { get; set; }
}

public class TaskItem : Entity
{
    public string? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>open | doing | blocked | done | dropped</summary>
    public string Status { get; set; } = "open";
    public DateTime? DueDate { get; set; }
    public int Priority { get; set; } = 1;     // 0..3
    /// <summary>voice | meeting | agent | manual</summary>
    public string Source { get; set; } = "manual";
    // Prioritizer dimensions, 0..5, null = unscored
    public double? MoneyImpact { get; set; }
    public double? Urgency { get; set; }
    public double? Effort { get; set; }
    public double? Energy { get; set; }
    public double? StrategicValue { get; set; }
    public double? Fun { get; set; }
    public double? CompletionOdds { get; set; }
    public string BlockedReason { get; set; } = "";
    public DateTime? CompletedAt { get; set; }
}

public class Commitment : Entity
{
    public string Text { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? PersonId { get; set; }
    public string PersonName { get; set; } = "";   // denormalized for fast speech
    /// <summary>meeting | voice | chat | manual</summary>
    public string Source { get; set; } = "voice";
    public string SourceNote { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public int Priority { get; set; } = 1;
    /// <summary>open | done | dropped</summary>
    public string Status { get; set; } = "open";
    public DateTime LastMentionedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

public class Idea : Entity
{
    public string Text { get; set; } = "";
    public string? ProjectId { get; set; }
    /// <summary>now | later | parking_lot</summary>
    public string Bucket { get; set; } = "parking_lot";
    public string ReplacesWhat { get; set; } = "";
    public string RoiVsMainProject { get; set; } = "";
    public string Status { get; set; } = "open";
}

public class Meeting : Entity
{
    public string Title { get; set; } = "";
    public string? ProjectId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    /// <summary>manual | teams_detected</summary>
    public string Source { get; set; } = "manual";
    public string NotesMd { get; set; } = "";
    public string? TranscriptPath { get; set; }
    public string Summary { get; set; } = "";
    public string DecisionsJson { get; set; } = "[]";
}

public class MeetingAction : Entity
{
    public string MeetingId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Owner { get; set; } = "me";   // "me" or PersonId
    public string? CommitmentId { get; set; }
    public string? TaskId { get; set; }
}

public class Decision : Entity
{
    public string? ProjectId { get; set; }
    public string Text { get; set; } = "";
    public string Context { get; set; } = "";
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
    /// <summary>open | decided | revisit</summary>
    public string Status { get; set; } = "decided";
}

public class Note : Entity
{
    public string? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string BodyMd { get; set; } = "";
    public string Tags { get; set; } = "";
}

public class AgentRun : Entity
{
    /// <summary>claude | codex | prompt_pack</summary>
    public string Agent { get; set; } = "claude";
    public string? ProjectId { get; set; }
    public string? RepoPath { get; set; }
    /// <summary>readonly | edit</summary>
    public string Mode { get; set; } = "readonly";
    public string Prompt { get; set; } = "";
    public string PromptTemplate { get; set; } = "";
    /// <summary>queued | awaiting_confirm | running | done | failed | stopped</summary>
    public string Status { get; set; } = "queued";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? TranscriptPath { get; set; }
    public string ResultSummary { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string FollowUpTaskIds { get; set; } = "[]";
    public int? ExitCode { get; set; }
    /// <summary>Claude CLI session id (from stream-json init) — enables --resume follow-ups.</summary>
    public string? SessionId { get; set; }
}

public class PlaywrightRun : Entity
{
    public string? ProjectId { get; set; }
    public string RepoPath { get; set; } = "";
    public string Suite { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? ReportPath { get; set; }
    public string FailuresJson { get; set; } = "[]";
    public string Summary { get; set; } = "";
}

public class FocusSample : Entity
{
    public string MachineId { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    /// <summary>ide | teams | browser | distraction | terminal | other</summary>
    public string Category { get; set; } = "other";
    public string? ProjectId { get; set; }
    public int DurationSec { get; set; }
}

public class FocusSession : Entity
{
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public int PlannedMinutes { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Outcome { get; set; } = "";
}

public class Machine : Entity
{
    public string Name { get; set; } = "";
    /// <summary>main | node</summary>
    public string Role { get; set; } = "main";
    public DateTime? LastSeenAt { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Status { get; set; } = "";
    public string OsInfo { get; set; } = "";
    public string PairingFingerprint { get; set; } = "";
}

public class MachineCommand : Entity
{
    public string MachineId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Script { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RequiresConfirm { get; set; } = true;
}

public class Briefing : Entity
{
    /// <summary>morning | shutdown</summary>
    public string Kind { get; set; } = "morning";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string ContentMd { get; set; } = "";
    public string SpokenSummary { get; set; } = "";
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";
    /// <summary>voice | ui | cli | scheduler | agent | system</summary>
    public string Actor { get; set; } = "system";
    public string PayloadJson { get; set; } = "";
    public bool RequiresConfirm { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string Outcome { get; set; } = "";
}

public class Setting
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
}

public class Reminder : Entity
{
    public string Text { get; set; } = "";
    /// <summary>Local time the reminder fires.</summary>
    public DateTime At { get; set; }
    /// <summary>"" | daily | weekdays</summary>
    public string Recurrence { get; set; } = "";
    /// <summary>reminder | alarm | timer</summary>
    public string Kind { get; set; } = "reminder";
    /// <summary>pending | done | cancelled</summary>
    public string Status { get; set; } = "pending";
    public DateTime? FiredAt { get; set; }
}

public class Fact : Entity
{
    public string Text { get; set; } = "";
}

/// <summary>One night of sleep. Date = the WAKE morning's local date; times are local.</summary>
public class SleepLog : Entity
{
    public DateTime Date { get; set; }
    public DateTime? BedAt { get; set; }
    public DateTime? WakeAt { get; set; }
    /// <summary>voice | lock | inferred — voice wins, lock beats inferred</summary>
    public string BedSource { get; set; } = "";
    public string WakeSource { get; set; } = "";
    public int? DurationMin { get; set; }
}

public class Habit : Entity
{
    public string Name { get; set; } = "";
    public string Aliases { get; set; } = "";   // comma-separated speech aliases
    /// <summary>daily | weekdays</summary>
    public string Schedule { get; set; } = "daily";
    /// <summary>Local "HH:mm" to nag if unlogged; empty = never nag</summary>
    public string NagTime { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class HabitLog : Entity
{
    public string HabitId { get; set; } = "";
    /// <summary>Local date the habit was done.</summary>
    public DateTime Date { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A learned routing mistake: "utterance X must NOT route to intent Y". Stored with
/// its embedding so semantically similar utterances are suppressed too. Written when the user
/// says "that's wrong"; read by the semantic router on every tier-2 decision.</summary>
public class RouteCorrection : Entity
{
    public string Utterance { get; set; } = "";
    /// <summary>The intent this utterance must never route to again.</summary>
    public string WrongIntent { get; set; } = "";
    public string EmbeddingJson { get; set; } = "[]";
}

/// <summary>
/// One row per routed utterance — the command audit trail. Answers: did it understand me,
/// what did it choose, what tool ran, did it succeed, where's the log, can I retry.
/// </summary>
public class CommandLog : Entity
{
    public string Utterance { get; set; } = "";
    /// <summary>voice | ui | cli | relay | scheduler</summary>
    public string Source { get; set; } = "";
    /// <summary>grammar | llm | answer | context | fallback</summary>
    public string Tier { get; set; } = "";
    /// <summary>1.0 for grammar; tier-2 LLM confidence; null for tier-3/fallback</summary>
    public double? Confidence { get; set; }
    public string Intent { get; set; } = "";
    public string SlotsJson { get; set; } = "{}";
    public string? ProjectSlug { get; set; }
    /// <summary>Read | WriteLocal | Mutate</summary>
    public string Safety { get; set; } = "";
    /// <summary>What actually ran: claude -p | codex exec | ollama | http:... | sql | none</summary>
    public string Tool { get; set; } = "";
    /// <summary>ok | failed | unhandled | awaiting_confirm | cancelled | expired</summary>
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>Where the detailed log lives (agent transcript path etc.)</summary>
    public string LogRef { get; set; } = "";
    public string Spoken { get; set; } = "";
    public string? AgentRunId { get; set; }
    public string FollowUpsJson { get; set; } = "[]";
    public int DurationMs { get; set; }
}

/// <summary>One council deliberation: a framed decision argued by selected lenses,
/// voted with weights, ended with a recommendation, an action, and this record.</summary>
public class CouncilSession : Entity
{
    /// <summary>quick | full | war | emergency</summary>
    public string Mode { get; set; } = "quick";
    public string Category { get; set; } = "";
    public string OriginalUtterance { get; set; } = "";
    public string FramedDecision { get; set; } = "";
    /// <summary>active | decided | cancelled | discarded</summary>
    public string Status { get; set; } = "active";
    public string AgentKeysCsv { get; set; } = "";
    public string ContextFactsJson { get; set; } = "{}";
    public DateTime? EndedAt { get; set; }
    public string? ProjectId { get; set; }
    /// <summary>The Decision row created when the session closes decided.</summary>
    public string? DecisionId { get; set; }
    /// <summary>Weighted tally: position → score.</summary>
    public string ResultJson { get; set; } = "{}";
    public string FinalRecommendation { get; set; } = "";
    public string DissentSummary { get; set; } = "";
    public string NextAction { get; set; } = "";
    public int Confidence { get; set; }
    public string TagsCsv { get; set; } = "";
}

/// <summary>One spoken/heard line inside a council session, in order.</summary>
public class CouncilTurn : Entity
{
    public string SessionId { get; set; } = "";
    public int Seq { get; set; }
    /// <summary>jarvis | user | agent key (ceo, cto, …)</summary>
    public string Speaker { get; set; } = "";
    /// <summary>framing | clarify | statement | input | decision</summary>
    public string Phase { get; set; } = "";
    public string Text { get; set; } = "";
}

public class CouncilVote : Entity
{
    public string SessionId { get; set; } = "";
    public string AgentKey { get; set; } = "";
    /// <summary>now | later | reject | compromise | ask_more</summary>
    public string Position { get; set; } = "";
    public int Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string PrimaryRisk { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public bool Vetoed { get; set; }
}

/// <summary>An action the council converted its decision into (task, reminder, …).</summary>
public class CouncilAction : Entity
{
    public string SessionId { get; set; } = "";
    /// <summary>task | reminder | agent_prompt | backlog | focus | review_date</summary>
    public string Kind { get; set; } = "";
    public string RefId { get; set; } = "";
    /// <summary>offered | confirmed | rejected</summary>
    public string Status { get; set; } = "offered";
}
