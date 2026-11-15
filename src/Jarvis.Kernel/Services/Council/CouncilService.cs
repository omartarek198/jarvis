using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;
using Jarvis.Kernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services.Council;

/// <summary>
/// The Council session engine and moderator. Holds at most ONE active session. While a session
/// awaits user input, the CommandRouter hands every utterance to TryConsumeAsync first — control
/// phrases and answers are handled here; anything unrelated closes the session gracefully and
/// falls back to normal routing (the council must never trap the router).
/// </summary>
public class CouncilService(
    CouncilClassifier classifier, CouncilStatements statements,
    IEventBus bus, ILogger<CouncilService> log)
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);
    private readonly Lock _lock = new();
    private ActiveSession? _active;

    private enum Phase { AwaitingTopic, AwaitingAnswer, AwaitingUserInput, AwaitingAction }

    private class ActiveSession
    {
        public required string SessionId;
        public required CouncilMode Mode;
        public DecisionCategory Category;
        public string Topic = "";
        public Phase Phase;
        public DateTime LastActivityAt = DateTime.UtcNow;
        public CouncilAgentDef[] Agents = [];
        public readonly Dictionary<string, string> Facts = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Key, string Question)> PendingQuestions = [];
        public int Seq;
        public AgentStatement[]? Statements;
        public string FinalRecommendation = "";
        public string OfferedAction = "";
        public string LastWinner = "";
        public string? ProjectId;
    }

    public bool HasActiveSession { get { lock (_lock) return _active is not null; } }

    /// <summary>The live session's id, or null when none is active (for /api/council/active).</summary>
    public string? ActiveSessionId { get { lock (_lock) return _active?.SessionId; } }

    // ---------------------------------------------------------------- session start

    public async Task<CommandResult> StartAsync(CommandContext ctx, CouncilMode mode, string? topic)
    {
        lock (_lock)
        {
            if (_active is not null && DateTime.UtcNow - _active.LastActivityAt > InactivityTimeout)
                _active = null;   // stale session — silently expire
            if (_active is not null)
                return new CommandResult("A council is already in session. Say 'stop council' first, or continue it.");
        }

        var db = ctx.S<JarvisDb>();
        var session = new CouncilSession
        {
            Mode = mode.ToString().ToLowerInvariant(),
            OriginalUtterance = ctx.Utterance,
            Status = "active",
        };
        db.CouncilSessions.Add(session);
        await db.SaveChangesAsync();

        var state = new ActiveSession { SessionId = session.Id, Mode = mode, Phase = Phase.AwaitingTopic };
        lock (_lock) _active = state;

        if (string.IsNullOrWhiteSpace(topic))
        {
            bus.Publish(new JarvisEvent("council.started",
                new { sessionId = session.Id, mode = session.Mode, spoken = "" }, ctx.Source));
            return new CommandResult("Council protocol initiated. What decision are we evaluating?");
        }
        return await FrameAsync(ctx, state, topic.Trim());
    }

    private async Task<CommandResult> FrameAsync(CommandContext ctx, ActiveSession s, string topic)
    {
        s.Topic = topic;
        s.Category = await classifier.ClassifyAsync(topic, s.Mode);
        s.Agents = CouncilAgents.Select(s.Category, s.Mode);
        ApplyRosterOverride(ctx, s);
        s.LastActivityAt = DateTime.UtcNow;

        var db = ctx.S<JarvisDb>();
        await PrefillFactsAsync(ctx, s, db);

        var row = await db.CouncilSessions.FindAsync(s.SessionId);
        if (row is not null)
        {
            row.FramedDecision = s.Topic;
            row.Category = s.Category.ToString();
            row.AgentKeysCsv = string.Join(",", s.Agents.Select(a => a.Key));
            row.ProjectId = s.ProjectId;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        s.PendingQuestions = BuildQuestions(s);
        var names = string.Join(", ", s.Agents.Select(a => a.DisplayName));
        var opening = s.Mode switch
        {
            CouncilMode.Emergency => $"Emergency council. Decision: {s.Topic}. {names} are present.",
            CouncilMode.Quick => $"Quick council on: {s.Topic}. {names}.",
            CouncilMode.War => $"War council convened: {s.Topic}. {names} at the table.",
            _ => $"Council protocol initiated. Decision: {s.Topic}. Category: {Describe(s.Category)}. Summoning {names}.",
        };

        bus.Publish(new JarvisEvent("council.started", new
        {
            sessionId = s.SessionId, mode = s.Mode.ToString().ToLowerInvariant(),
            category = s.Category.ToString(), framedDecision = s.Topic,
            agents = s.Agents.Select(a => new { a.Key, a.DisplayName, a.VoiceProfile }),
            spoken = opening,
        }, ctx.Source));
        await RecordTurnAsync(ctx, s, "jarvis", "framing", opening);

        if (s.PendingQuestions.Count > 0)
        {
            s.Phase = Phase.AwaitingAnswer;
            var q = s.PendingQuestions[0].Question;
            bus.Publish(new JarvisEvent("council.question.asked",
                new { sessionId = s.SessionId, question = q, spoken = q }, ctx.Source));
            await RecordTurnAsync(ctx, s, "jarvis", "clarify", q);
            return new CommandResult($"{opening} One question first: {q}");
        }
        var deliberation = await DeliberateAsync(ctx, s);
        return new CommandResult($"{opening}\n{deliberation}");
    }

    /// <summary>"convene the council with the cto and the skeptic" — explicit roster wins.</summary>
    private static void ApplyRosterOverride(CommandContext ctx, ActiveSession s)
    {
        var agentsSlot = ctx.Slot("agents");
        if (agentsSlot is null) return;
        var picked = Regex.Split(agentsSlot, @"\s*(?:,|and|the)\s*", RegexOptions.IgnoreCase)
            .Select(p => CouncilAgents.ByKey(p.Trim()))
            .Where(a => a is not null).Select(a => a!).Distinct().ToArray();
        if (picked.Length >= 2) s.Agents = picked;
    }

    // ---------------------------------------------------------------- context prefill

    private async Task PrefillFactsAsync(CommandContext ctx, ActiveSession s, JarvisDb db)
    {
        try
        {
            // Anchor every debate in real time — "tonight" means something different on a
            // Friday with a Sunday job than on a Tuesday.
            s.Facts["right_now"] = DateTime.Now.ToString("dddd, MMMM d, h:mm tt");

            // NOTE: deliberately NOT injecting db.Facts here — random personal facts plus a
            // "cite something specific" rule made the model weave irrelevant facts into
            // arguments (invented staging-server tasks, etc.). High-signal context only.

            // Last night's sleep, if known — health questions skip asking when this exists.
            var today = DateTime.Now.Date;
            var sleep = await db.SleepLogs.AsNoTracking()
                .Where(x => x.Date == today && x.DurationMin != null)
                .Select(x => x.DurationMin).FirstOrDefaultAsync();
            if (sleep is > 0) s.Facts["hours_slept"] = Math.Round(sleep.Value / 60.0, 1).ToString("0.#");

            if (s.Mode == CouncilMode.War || s.Category == DecisionCategory.Product)
            {
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"))
                    ?? await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
                if (project is not null)
                {
                    s.ProjectId = project.Id;
                    var openTasks = await db.Tasks.AsNoTracking()
                        .CountAsync(t => t.ProjectId == project.Id && t.Status != "done" && t.Status != "dropped");
                    s.Facts["open_tasks"] = openTasks.ToString();
                    if (s.Mode == CouncilMode.War)
                    {
                        // The war room's spoken digest doubles as the council's situation brief.
                        var warRoom = await ctx.S<WarRoomService>().SpokenAsync(project);
                        if (!string.IsNullOrWhiteSpace(warRoom)) s.Facts["war_room"] = warRoom;
                    }
                }
            }
        }
        catch (Exception ex) { log.LogWarning("council fact prefill failed: {Msg}", ex.Message); }
    }

    // ---------------------------------------------------------------- clarifying questions

    private static List<(string Key, string Question)> BuildQuestions(ActiveSession s)
    {
        List<(string, string)> bank = s.Category switch
        {
            DecisionCategory.HealthSleep or DecisionCategory.EmergencyRisk =>
            [
                ("hours_slept", "How many hours did you sleep?"),
                ("avoiding", "Are you trying to avoid another task right now?"),
            ],
            DecisionCategory.Product =>
            [
                ("blocking_customer", "Is this blocking a paying customer or the first demo?"),
                ("tester_requested", "Did a real customer or tester request it?"),
                ("time_estimate", "What is your rough time estimate?"),
            ],
            DecisionCategory.TimeAllocation =>
            [
                ("hours_slept", "How many hours did you sleep?"),
                ("moved_today", "Have you moved or trained today?"),
            ],
            DecisionCategory.FinancialRisk =>
            [
                ("amount", "How much money or time is at stake?"),
                ("emotional", "Are you deciding this tired, frustrated, or hyped?"),
            ],
            DecisionCategory.ShinyObject =>
            [
                ("displaces", "What committed work would this displace?"),
            ],
            DecisionCategory.Architecture =>
            [
                ("breaks_if_deferred", "What breaks if you defer it?"),
            ],
            _ => [],
        };
        var unanswered = bank.Where(q => !s.Facts.ContainsKey(q.Item1)).ToList();
        var cap = s.Mode switch { CouncilMode.Quick => 1, CouncilMode.Emergency => 1, _ => 3 };
        return unanswered.Take(cap).ToList();
    }

    // ---------------------------------------------------------------- router hook

    /// <summary>Returns null when the utterance is not for the council (no active session, or the
    /// session is in a state where an unrelated command should win — we close and step aside).</summary>
    public async Task<CommandResult?> TryConsumeAsync(string text, string source, IServiceProvider scoped)
    {
        ActiveSession? s;
        lock (_lock) s = _active;
        if (s is null) return null;

        var ctx = new CommandContext { Utterance = text, Source = source, Services = scoped };

        if (DateTime.UtcNow - s.LastActivityAt > InactivityTimeout)
        {
            await CloseAsync(ctx, s, "cancelled", "Session expired from inactivity.");
            return null;   // let the utterance route normally
        }
        s.LastActivityAt = DateTime.UtcNow;

        // Control phrases work in every phase.
        if (Regex.IsMatch(text, @"^(stop|cancel|end)( the)? council$", RegexOptions.IgnoreCase))
        {
            await CloseAsync(ctx, s, "cancelled", "Council adjourned without a decision.");
            return new CommandResult("Council adjourned.");
        }
        if (Regex.IsMatch(text, @"^(vote now|summari[sz]e and end|just decide)$", RegexOptions.IgnoreCase)
            && s.Phase is Phase.AwaitingAnswer or Phase.AwaitingUserInput)
        {
            s.PendingQuestions.Clear();
            if (s.Phase == Phase.AwaitingUserInput) return new CommandResult(await VoteAndRecommendAsync(ctx, s));
            return new CommandResult(await DeliberateAsync(ctx, s));
        }

        // "let the skeptic respond" / "ask the cto to explain" — one extra line from that lens.
        var follow = Regex.Match(text,
            @"^(let( the)? (?<agent>[\w ]+?) (respond|reply|speak)|ask( the)? (?<agent>[\w ]+?) to (explain|elaborate|respond))$",
            RegexOptions.IgnoreCase);
        if (follow.Success && s.Statements is not null)
        {
            var agent = CouncilAgents.ByKey(follow.Groups["agent"].Value.Trim());
            if (agent is not null && s.Agents.Contains(agent))
            {
                var prior = string.Join(" ", s.Statements.Select(st => st.Statement));
                var line = await statements.ChallengeAsync(agent, s.Topic, prior,
                    "Expand your view or respond to the debate so far.")
                    ?? "That lens has nothing to add right now.";
                bus.Publish(new JarvisEvent("council.agent.speaking.started", new
                {
                    sessionId = s.SessionId, agentKey = agent.Key, displayName = agent.DisplayName,
                    voiceProfile = agent.VoiceProfile, phase = "followup", spoken = line,
                }, source));
                await RecordTurnAsync(ctx, s, agent.Key, "followup", line);
                return new CommandResult(ctx.Source == "voice" ? "" : $"{agent.DisplayName}: {line}");
            }
        }

        switch (s.Phase)
        {
            case Phase.AwaitingTopic:
                return await FrameAsync(ctx, s, text);

            case Phase.AwaitingAnswer:
            {
                if (Regex.IsMatch(text, @"^skip( that| this)?( question)?$", RegexOptions.IgnoreCase))
                    s.PendingQuestions.RemoveAt(0);
                else
                {
                    var (key, _) = s.PendingQuestions[0];
                    s.Facts[key] = ExtractFactValue(key, text);
                    s.PendingQuestions.RemoveAt(0);
                    await RecordTurnAsync(ctx, s, "user", "clarify", text);
                }
                if (s.PendingQuestions.Count > 0)
                {
                    var q = s.PendingQuestions[0].Question;
                    bus.Publish(new JarvisEvent("council.question.asked",
                        new { sessionId = s.SessionId, question = q, spoken = q }, source));
                    await RecordTurnAsync(ctx, s, "jarvis", "clarify", q);
                    return new CommandResult(q);
                }
                return new CommandResult(await DeliberateAsync(ctx, s));
            }

            case Phase.AwaitingUserInput:
                s.Facts["user_priority"] = text;
                await RecordTurnAsync(ctx, s, "user", "input", text);
                return new CommandResult(await VoteAndRecommendAsync(ctx, s));

            case Phase.AwaitingAction:
            {
                if (Regex.IsMatch(text, @"^(send( it| this)? to claude|dispatch claude|deep dive)$", RegexOptions.IgnoreCase))
                    return await DispatchToClaudeAsync(ctx, s);
                if (Regex.IsMatch(text, @"^(confirm|yes|do it|create (the )?(task|reminder|timer|focus( session)?|commitment)|set (the )?reminder|start (the )?(timer|focus)|save( this)?|lock it( in)?|approved?)$", RegexOptions.IgnoreCase))
                    return await ConvertActionAsync(ctx, s, text);
                if (Regex.IsMatch(text, @"^(no|reject( decision| it)?|discard|skip)$", RegexOptions.IgnoreCase))
                {
                    await CloseAsync(ctx, s, "decided", "Decision recorded, no action created.");
                    return new CommandResult("Understood. Decision recorded, no action created. Council adjourned.");
                }
                if (Regex.IsMatch(text, @"^don'?t save( this)?$", RegexOptions.IgnoreCase))
                {
                    await CloseAsync(ctx, s, "discarded", "Record discarded at the user's request.");
                    return new CommandResult("Discarded. The council leaves no record.");
                }
                // Unrelated utterance → save the record, step aside, let it route normally.
                await CloseAsync(ctx, s, "decided", "Closed implicitly by an unrelated command.");
                return null;
            }
        }
        return null;
    }

    private static string ExtractFactValue(string key, string answer)
    {
        if (key == "hours_slept")
        {
            var m = Regex.Match(answer, @"(\d+([.,]\d+)?)");
            if (m.Success) return m.Groups[1].Value.Replace(',', '.');
        }
        return answer.Trim();
    }

    // ---------------------------------------------------------------- deliberation

    private async Task<string> DeliberateAsync(CommandContext ctx, ActiveSession s)
    {
        // Voice sessions get the deliberation through council.* events (the tray speaks each
        // agent in its own voice) — the route response stays silent to avoid double speech.
        var voice = ctx.Source == "voice";
        bus.Publish(new JarvisEvent("council.deliberation.started",
            new { sessionId = s.SessionId, spoken = "" }, ctx.Source));

        // Sequential, debate-style: each agent sees what was already said and must add or
        // attack, never repeat. Events stream out per speaker so voice/UI track live.
        var sb = new StringBuilder();
        var heard = new List<(string DisplayName, AgentStatement Statement)>();
        var collected = new List<AgentStatement>();
        foreach (var agent in s.Agents)
        {
            var st = await statements.GenerateOneAsync(agent, s.Topic, s.Category, s.Facts, s.Mode, heard);
            collected.Add(st);
            heard.Add((agent.DisplayName, st));
            bus.Publish(new JarvisEvent("council.agent.speaking.started", new
            {
                sessionId = s.SessionId, agentKey = agent.Key, displayName = agent.DisplayName,
                voiceProfile = agent.VoiceProfile, position = st.Position, confidence = st.Confidence,
                spoken = st.Statement,
            }, ctx.Source));
            await RecordTurnAsync(ctx, s, agent.Key, "statement", st.Statement);
            bus.Publish(new JarvisEvent("council.agent.speaking.ended",
                new { sessionId = s.SessionId, agentKey = agent.Key }, ctx.Source));
            sb.AppendLine($"{agent.DisplayName}: {st.Statement}");
        }
        s.Statements = [.. collected];

        // Full/War councils cross-examine, then bring the user in before the vote.
        if (s.Mode is CouncilMode.Full or CouncilMode.War)
        {
            var exam = await CrossExamineAsync(ctx, s);
            if (!string.IsNullOrEmpty(exam)) sb.AppendLine(exam);

            s.Phase = Phase.AwaitingUserInput;
            const string ask = "Your call first — what matters more right now: speed, quality, money, or energy?";
            bus.Publish(new JarvisEvent("council.user.input.requested",
                new { sessionId = s.SessionId, prompt = ask, spoken = ask }, ctx.Source));
            await RecordTurnAsync(ctx, s, "jarvis", "input", ask);
            sb.AppendLine(ask);
            return voice ? "" : sb.ToString().TrimEnd();
        }

        var decision = await VoteAndRecommendAsync(ctx, s);
        if (voice) return "";
        sb.AppendLine(decision);
        return sb.ToString().TrimEnd();
    }

    /// <summary>One challenge round: the strongest dissenter challenges the leading position's
    /// strongest advocate, who answers. Skipped silently offline (Ollama-only, no fallback noise).</summary>
    private async Task<string> CrossExamineAsync(CommandContext ctx, ActiveSession s)
    {
        try
        {
            if (s.Statements is null || s.Statements.All(st => st.FromFallback)) return "";
            var byPosition = s.Statements.GroupBy(st => st.Position).OrderByDescending(g => g.Count()).ToList();
            if (byPosition.Count < 2) return "";
            var leader = byPosition[0].OrderByDescending(st => st.Confidence).First();
            var challenger = byPosition[1].OrderByDescending(st => st.Confidence).First();

            var sb = new StringBuilder();
            foreach (var (asker, target, instruction) in new[]
            {
                (challenger, leader, "Challenge this argument from your lens — name its weakest assumption."),
                (leader, challenger, "Answer the challenge briefly from your lens. Concede what is true."),
            })
            {
                var agent = CouncilAgents.ByKey(asker.AgentKey)!;
                var line = await statements.ChallengeAsync(agent, s.Topic, target.Statement, instruction);
                if (string.IsNullOrWhiteSpace(line)) return sb.ToString().TrimEnd();
                bus.Publish(new JarvisEvent("council.agent.speaking.started", new
                {
                    sessionId = s.SessionId, agentKey = agent.Key, displayName = agent.DisplayName,
                    voiceProfile = agent.VoiceProfile, phase = "crossexam", spoken = line,
                }, ctx.Source));
                await RecordTurnAsync(ctx, s, agent.Key, "crossexam", line);
                bus.Publish(new JarvisEvent("council.agent.speaking.ended",
                    new { sessionId = s.SessionId, agentKey = agent.Key }, ctx.Source));
                sb.AppendLine($"{agent.DisplayName}: {line}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            log.LogWarning("cross-examination skipped: {Msg}", ex.Message);
            return "";
        }
    }

    private async Task<string> VoteAndRecommendAsync(CommandContext ctx, ActiveSession s)
    {
        var db = ctx.S<JarvisDb>();
        bus.Publish(new JarvisEvent("council.vote.started", new { sessionId = s.SessionId }, ctx.Source));

        // the user's stated priority nudges the matching lenses.
        var priorityBoost = (s.Facts.GetValueOrDefault("user_priority") ?? "").ToLowerInvariant() switch
        {
            var p when p.Contains("speed") => "operator",
            var p when p.Contains("quality") => "cto",
            var p when p.Contains("money") => "investor",
            var p when p.Contains("energy") => "health",
            _ => null,
        };

        var tally = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var votes = new List<CouncilVote>();
        double totalWeight = 0;
        foreach (var st in s.Statements ?? [])
        {
            var weight = CouncilAgents.WeightFor(s.Category, st.AgentKey);
            if (st.AgentKey == priorityBoost) weight *= 1.5;
            var score = weight * (st.Confidence / 100.0);
            tally[st.Position] = tally.GetValueOrDefault(st.Position) + score;
            totalWeight += score;
            var vote = new CouncilVote
            {
                SessionId = s.SessionId, AgentKey = st.AgentKey, Position = st.Position,
                Confidence = st.Confidence, Reason = st.Statement, PrimaryRisk = st.Risk,
                RecommendedAction = st.Action, Weight = weight,
            };
            votes.Add(vote);
            db.CouncilVotes.Add(vote);
            bus.Publish(new JarvisEvent("council.vote.cast",
                new { sessionId = s.SessionId, agentKey = st.AgentKey, position = st.Position, confidence = st.Confidence }, ctx.Source));
        }

        // Veto: protective agent rejects with conviction in an emergency-grade situation.
        var vetoKey = CouncilAgents.VetoHolder(s.Category);
        var vetoVote = votes.FirstOrDefault(v => v.AgentKey == vetoKey && v.Position == "reject" && v.Confidence >= 70);
        var lowSleep = s.Facts.TryGetValue("hours_slept", out var hs) && double.TryParse(hs, out var hours) && hours < 6;
        var vetoFires = vetoVote is not null && (s.Mode == CouncilMode.Emergency
            || (s.Category is DecisionCategory.HealthSleep && lowSleep));

        string winner;
        var splitHouse = false;
        if (vetoFires)
        {
            winner = "reject";
            vetoVote!.Vetoed = true;
        }
        else if (tally.Count == 0)
        {
            winner = "compromise";
        }
        else
        {
            winner = tally.MaxBy(t => t.Value).Key;
            // Split house → bias to the cautious option, and say so.
            if (tally[winner] < totalWeight * 0.55 && tally.Count > 1)
            {
                splitHouse = true;
                winner = tally.Keys.OrderBy(k => k switch
                { "reject" => 0, "later" => 1, "compromise" => 2, "ask_more" => 3, _ => 4 }).First();
            }
        }

        var winningVotes = votes.Where(v => v.Position == winner).OrderByDescending(v => v.Weight * v.Confidence).ToList();
        var dissent = votes.Where(v => v.Position != winner).OrderByDescending(v => v.Weight * v.Confidence).FirstOrDefault();
        var nextAction = winningVotes.Select(v => v.RecommendedAction).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? votes.Select(v => v.RecommendedAction).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
        var confidence = winningVotes.Count > 0 ? (int)winningVotes.Average(v => v.Confidence) : 50;

        s.FinalRecommendation = BuildRecommendation(s, winner, nextAction);
        s.OfferedAction = nextAction;
        s.LastWinner = winner;

        var dissentText = dissent is null || string.IsNullOrWhiteSpace(dissent.PrimaryRisk) ? "" :
            $" Dissent — {CouncilAgents.ByKey(dissent.AgentKey)!.DisplayName}: {Sentence(dissent.PrimaryRisk)}";
        var vetoText = vetoFires ? $" {CouncilAgents.ByKey(vetoKey)!.DisplayName} exercises veto." : "";
        // Spoken head-count by position, e.g. "two say later, two say now, one rejects".
        var countText = SpokenTally(votes);
        var lead =
            vetoFires ? "Council decision:" :
            winningVotes.Count == votes.Count ? "Unanimous council decision:" :
            splitHouse ? $"The council is split — {countText}. Taking the cautious path:" :
            winningVotes.Count * 2 > votes.Count ? $"Council decision, {winningVotes.Count} of {votes.Count} lenses aligned:" :
            $"Council decision — {countText}, weighted:";

        var row = await db.CouncilSessions.FindAsync(s.SessionId);
        if (row is not null)
        {
            row.ContextFactsJson = JsonSerializer.Serialize(s.Facts);
            row.ResultJson = JsonSerializer.Serialize(tally);
            row.FinalRecommendation = s.FinalRecommendation;
            row.DissentSummary = dissent is null ? "" : $"{dissent.AgentKey}: {dissent.Reason}";
            row.NextAction = nextAction;
            row.Confidence = confidence;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        var offerKind = string.IsNullOrWhiteSpace(nextAction) ? "" : ResolveActionKind("", nextAction, s);
        var offerVerb = offerKind switch
        {
            "reminder" => "set a reminder", "focus" => "start a focus timer",
            "commitment" => "log a commitment", _ => "create a task",
        };
        var spoken = $"{lead}{vetoText} {Sentence(s.FinalRecommendation)}{dissentText}" +
            (string.IsNullOrWhiteSpace(nextAction) ? "" :
                $" I can {offerVerb}: \"{Truncate(nextAction.TrimEnd('.'), 120)}\". Confirm, or say 'reject decision'.");

        bus.Publish(new JarvisEvent("council.decision.ready", new
        {
            sessionId = s.SessionId, recommendation = s.FinalRecommendation,
            tally = tally.ToDictionary(t => t.Key, t => Math.Round(t.Value, 2)),
            dissent = dissentText.Trim(), veto = vetoFires, spoken,
        }, ctx.Source));
        await RecordTurnAsync(ctx, s, "jarvis", "decision", spoken);

        if (string.IsNullOrWhiteSpace(nextAction))
        {
            await CloseAsync(ctx, s, "decided", s.FinalRecommendation);
            return ctx.Source == "voice" ? "" : spoken + " Council adjourned.";
        }
        s.Phase = Phase.AwaitingAction;
        return ctx.Source == "voice" ? "" : spoken;
    }

    private static string BuildRecommendation(ActiveSession s, string winner, string nextAction)
    {
        // Either/or topics ("gym or the release") make positional templates nonsensical
        // ("proceed now — gym or the release") — the agents' concrete action is the real answer.
        var eitherOr = Regex.IsMatch(s.Topic, @"\s(or|versus|vs\.?)\s", RegexOptions.IgnoreCase);
        if (eitherOr && winner is "now" or "later" or "reject" && !string.IsNullOrWhiteSpace(nextAction))
            return nextAction;

        return winner switch
        {
            "now" => $"Proceed now — {s.Topic}.",
            "later" => $"Defer: {s.Topic}. Not now.",
            "reject" => s.Mode == CouncilMode.Emergency || s.Category == DecisionCategory.HealthSleep
                ? "Do not do it. Protect the asset."
                : $"Rejected: {s.Topic}.",
            "compromise" => $"Take the middle path on: {s.Topic}." +
                (string.IsNullOrWhiteSpace(nextAction) ? "" : $" Specifically: {nextAction}"),
            "ask_more" => "The council lacks a critical fact — gather it before committing.",
            _ => s.Topic,
        };
    }

    // ---------------------------------------------------------------- action conversion + record

    /// <summary>Convert the decision into the right kind of action. The council's recommended
    /// action determines the natural kind; an explicit verb in the user's confirm ("create the
    /// reminder", "start the timer") overrides. Honors reminder / focus / commitment / task —
    /// not just task, which was the original gap.</summary>
    private async Task<CommandResult> ConvertActionAsync(CommandContext ctx, ActiveSession s, string confirmText)
    {
        var db = ctx.S<JarvisDb>();
        var action = s.OfferedAction.Trim();
        var kind = ResolveActionKind(confirmText, action, s);

        string refId, spoken, eventKind;
        switch (kind)
        {
            case "reminder":
            {
                // Undated council actions default to a next-day review (flagged for the user).
                var at = DateParser.TryParseTimeSpec(action)
                    ?? (DateParser.TryParseDuration(action) is { } d ? DateTime.Now.Add(d) : DateTime.Now.AddHours(24));
                var rem = new Reminder { Text = Truncate(action, 200), At = at, Kind = "reminder" };
                db.Reminders.Add(rem);
                refId = rem.Id; eventKind = "reminder";
                spoken = $"Reminder set for {at:ddd h:mm tt}: {Truncate(action, 100)}.";
                break;
            }
            case "focus":
            {
                var mins = DateParser.TryParseDuration(action) is { } fd
                    ? Math.Clamp((int)fd.TotalMinutes, 5, 240)
                    : (Regex.Match(action, @"(\d{1,3})\s*min") is { Success: true } mm ? Math.Clamp(int.Parse(mm.Groups[1].Value), 5, 240) : 45);
                var existing = await db.FocusSessions.AnyAsync(f => f.EndedAt == null);
                if (existing)
                {
                    refId = ""; eventKind = "focus";
                    spoken = "A focus session is already running, so I logged the decision instead of starting a new timer.";
                }
                else
                {
                    var fs = new FocusSession { PlannedMinutes = mins };
                    db.FocusSessions.Add(fs);
                    refId = fs.Id; eventKind = "focus";
                    spoken = $"Focus session started for {mins} minutes: {Truncate(action, 100)}.";
                }
                break;
            }
            case "commitment":
            {
                var c = new Commitment
                {
                    Text = Truncate(action, 200), ProjectId = s.ProjectId,
                    Source = "voice", SourceNote = $"Council ({s.Mode}) decision",
                };
                db.Commitments.Add(c);
                refId = c.Id; eventKind = "commitment";
                spoken = $"Commitment logged: {Truncate(action, 100)}.";
                break;
            }
            default:
            {
                var task = new TaskItem
                {
                    Title = Truncate(action, 200),
                    Detail = $"Council decision: {s.FinalRecommendation}",
                    ProjectId = s.ProjectId, Source = "council",
                };
                db.Tasks.Add(task);
                refId = task.Id; kind = "task"; eventKind = "task";
                spoken = $"Task created: {task.Title}.";
                break;
            }
        }

        db.CouncilActions.Add(new CouncilAction
        { SessionId = s.SessionId, Kind = kind, RefId = refId, Status = "confirmed" });
        await db.SaveChangesAsync();

        bus.Publish(new JarvisEvent("council.action.created",
            new { sessionId = s.SessionId, kind = eventKind, refId, spoken = "" }, ctx.Source));

        await CloseAsync(ctx, s, "decided", s.FinalRecommendation);
        return new CommandResult($"{spoken} Decision recorded. Council adjourned.");
    }

    /// <summary>Pick task | reminder | focus | commitment from the confirm verb, the action
    /// text, and the winning position.</summary>
    private static string ResolveActionKind(string confirmText, string action, ActiveSession s)
    {
        var t = confirmText.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\breminder\b")) return "reminder";
        if (Regex.IsMatch(t, @"\b(timer|focus)\b")) return "focus";
        if (Regex.IsMatch(t, @"\bcommitment\b")) return "commitment";

        var a = action.ToLowerInvariant();
        // A focus/timer-shaped action: "start a 45-minute block", "focus for an hour".
        if (Regex.IsMatch(a, @"\b(\d{1,3}\s*min|focus|timebox|time-box|block now|sprint)\b")) return "focus";
        // A time-bound review/check action: "review after the demo", "revisit tomorrow", "at 6am".
        if (Regex.IsMatch(a, @"\b(review|revisit|check back|follow up|tomorrow|tonight|later|after|by \w+day|at \d)\b")) return "reminder";
        // Compromise "do X then Y" sequencing reads as a commitment to a plan.
        if (s.LastWinner == "compromise" && Regex.IsMatch(a, @"\bthen\b")) return "commitment";
        return "task";
    }

    /// <summary>"send to claude" — hand the decided work to a Claude agent run (readonly plan mode).</summary>
    private async Task<CommandResult> DispatchToClaudeAsync(CommandContext ctx, ActiveSession s)
    {
        var db = ctx.S<JarvisDb>();
        var project = s.ProjectId is null ? null : await db.Projects.FindAsync(s.ProjectId);
        var prompt = $"""
            Council decision follow-up for {(project?.Name ?? "the user")}.
            Decision debated: {s.Topic}
            Council recommendation: {s.FinalRecommendation}
            Next action: {s.OfferedAction}
            Produce a concrete implementation plan or analysis for this next action.
            """;
        var (run, spoken) = await ctx.S<AgentService>().DispatchAsync(
            "claude", prompt, "council-followup", project, repo: null, edit: false);
        db.CouncilActions.Add(new CouncilAction
        { SessionId = s.SessionId, Kind = "agent_prompt", RefId = run.Id, Status = "confirmed" });
        await db.SaveChangesAsync();
        ctx.Audit.AgentRunId = run.Id;
        bus.Publish(new JarvisEvent("council.action.created",
            new { sessionId = s.SessionId, kind = "agent_prompt", refId = run.Id, spoken = "" }, ctx.Source));
        await CloseAsync(ctx, s, "decided", s.FinalRecommendation);
        return new CommandResult($"{spoken} Decision recorded. Council adjourned.");
    }

    private async Task CloseAsync(CommandContext ctx, ActiveSession s, string status, string note)
    {
        lock (_lock) if (_active == s) _active = null;
        try
        {
            var db = ctx.S<JarvisDb>();
            var row = await db.CouncilSessions.FindAsync(s.SessionId);
            if (row is not null)
            {
                row.Status = status;
                row.EndedAt = DateTime.UtcNow;
                row.UpdatedAt = DateTime.UtcNow;
                if (string.IsNullOrEmpty(row.ContextFactsJson) || row.ContextFactsJson == "{}")
                    row.ContextFactsJson = JsonSerializer.Serialize(s.Facts);

                if (status == "decided" && !string.IsNullOrWhiteSpace(s.FinalRecommendation))
                {
                    var decision = new Decision
                    {
                        ProjectId = s.ProjectId,
                        Text = s.FinalRecommendation,
                        Context = $"Council ({s.Mode}, {s.Category}) on \"{s.Topic}\" — session {s.SessionId}",
                    };
                    db.Decisions.Add(decision);
                    row.DecisionId = decision.Id;
                    await AppendBrainRecordAsync(db, s);
                }
                await db.SaveChangesAsync();
            }
            bus.Publish(new JarvisEvent(status == "cancelled" ? "council.cancelled" : "council.saved",
                new { sessionId = s.SessionId, status, note, spoken = "" }, ctx.Source));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "council close failed for session {Id}", s.SessionId);
            lock (_lock) if (_active == s) _active = null;   // never leave a wedged session holding the router
            bus.Publish(new JarvisEvent("council.error",
                new { sessionId = s.SessionId, message = ex.Message,
                      spoken = "The council hit a snag while saving, but I kept the decision. We can revisit it." }, ctx.Source));
        }
    }

    /// <summary>Decision records land in the project brain so BrainWatcher indexes them for recall.</summary>
    private async Task AppendBrainRecordAsync(JarvisDb db, ActiveSession s)
    {
        try
        {
            var slug = s.ProjectId is null ? null
                : await db.Projects.AsNoTracking().Where(p => p.Id == s.ProjectId).Select(p => p.Slug).FirstOrDefaultAsync();
            var dir = Path.Combine(JarvisPaths.Brains, slug ?? "personal");
            Directory.CreateDirectory(dir);
            var line = $"\n## {DateTime.Now:yyyy-MM-dd HH:mm} — council ({s.Mode}, {s.Category})\n" +
                       $"- Decision: {s.Topic}\n- Recommendation: {s.FinalRecommendation}\n" +
                       (s.Facts.Count > 0 ? $"- Facts: {string.Join("; ", s.Facts.Select(f => $"{f.Key}={f.Value}"))}\n" : "");
            await File.AppendAllTextAsync(Path.Combine(dir, "decisions.md"), line);
        }
        catch (Exception ex) { log.LogWarning("brain decision record failed: {Msg}", ex.Message); }
    }

    private async Task RecordTurnAsync(CommandContext ctx, ActiveSession s, string speaker, string phase, string text)
    {
        try
        {
            var db = ctx.S<JarvisDb>();
            db.CouncilTurns.Add(new CouncilTurn
            { SessionId = s.SessionId, Seq = ++s.Seq, Speaker = speaker, Phase = phase, Text = text });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning("council turn write failed: {Msg}", ex.Message); }
    }

    private static string Describe(DecisionCategory c) => c switch
    {
        DecisionCategory.Product => "Product prioritization",
        DecisionCategory.Architecture => "technical architecture",
        DecisionCategory.BusinessRevenue => "business and revenue",
        DecisionCategory.MarketingSales => "marketing and sales",
        DecisionCategory.HealthSleep => "health and sleep",
        DecisionCategory.PersonalMaintenance => "personal maintenance",
        DecisionCategory.TimeAllocation => "time allocation",
        DecisionCategory.SocialFun => "social",
        DecisionCategory.FinancialRisk => "financial risk",
        DecisionCategory.ShinyObject => "idea evaluation",
        DecisionCategory.EmergencyRisk => "high-risk behavior",
        _ => "general",
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static readonly string[] CountWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    private static readonly Dictionary<string, string> PositionVerb = new()
    {
        ["now"] = "say now", ["later"] = "say later", ["reject"] = "reject",
        ["compromise"] = "want a compromise", ["ask_more"] = "want more info",
    };

    /// <summary>"two say later, two say now, one rejects" — head-count by position, most first.</summary>
    private static string SpokenTally(List<CouncilVote> votes)
    {
        var groups = votes.GroupBy(v => v.Position).OrderByDescending(g => g.Count()).ToList();
        var parts = groups.Select(g =>
        {
            var n = g.Count();
            var word = n < CountWords.Length ? CountWords[n] : n.ToString();
            return $"{word} {PositionVerb.GetValueOrDefault(g.Key, "abstain")}";
        }).ToList();
        return parts.Count switch
        {
            0 => "no votes",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + ", and " + parts[^1],
        };
    }

    /// <summary>Normalize a fragment into a clean sentence — exactly one terminal period.</summary>
    private static string Sentence(string text)
    {
        text = text.Trim().TrimEnd('.', ' ');
        return text.Length == 0 ? "" : text + ".";
    }
}
