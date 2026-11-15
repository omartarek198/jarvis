using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class AgentsModule(AgentService agents) : IJarvisModule
{
    public string Name => "agents";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "fix_tester_issues",
            Description = "dispatch Claude to fix open tester issues in a project's repo (edits files)",
            Patterns = [@"^ask claude to fix (all |the )?((?<sev>high|p1|p2|critical)([ -]severity)? )?tester issues( (in|on|for) (?<project>.+))?$"],
            SlotHints = ["project", "sev"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var (project, repo, err) = await ResolveTargetAsync(ctx, defaultSlug: Seeder.PrimaryProjectSlug);
                if (err is not null) return new CommandResult(err);
                var issues = (await TesterIssuesService.ReadAsync(project!)).Where(i => !i.Closed).ToList();
                if (issues.Count == 0)
                    return new CommandResult($"The {project!.Name} tester inbox is empty. Add issues to {TesterIssuesService.FilePath(project)} first.");
                var sevFilter = ctx.Slot("sev");
                if (sevFilter is "high" or "critical" or "p1") issues = [.. issues.Where(i => i.Severity is "P1" or "P2")];

                var prompt = $"""
                    You are working in the {project!.Name} repository. Fix the following tester-reported issues,
                    highest severity first. Make minimal, safe changes. Build after each fix if a build command exists.

                    Open tester issues:
                    {string.Join("\n", issues.Select(i => $"- [{i.Severity}] {i.Text}"))}

                    After fixing an issue, leave its checklist line for the user to verify (do NOT edit the issues file).
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "fix-tester-issues", project, repo, edit: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id, issues = issues.Count });
            }
        },
        new()
        {
            Name = "ask_claude_edit",
            Description = "dispatch Claude to do a coding task (edits files in the project repo)",
            Patterns = [@"^ask claude to (?<task>.+?)( (on|in|for) (?<project>[\w .'-]+))?$"],
            SlotHints = ["task", "project"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var task = ctx.Slot("task");
                if (task is null || task.Length < 5) return new CommandResult("") { Unhandled = true };
                var (project, repo, _) = await ResolveTargetAsync(ctx, defaultSlug: null);
                var prompt = $"""
                    Task from the user (via JARVIS voice command): {task}

                    Work in the current repository. Make minimal, conventional changes. Verify with a build/tests where possible.
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "claude-task", project, repo, edit: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "ask_claude_question",
            Description = "ask Claude a read-only question about a project/codebase",
            Patterns = [@"^ask claude (?<q>(about|what|how|why|where|which|whether|if) .+)$"],
            SlotHints = ["q", "project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var q = ctx.Slot("q") ?? "";
                var (project, repo, _) = await ResolveTargetAsync(ctx, defaultSlug: null);
                // RAG-lite: top brain-note hits ride along so Claude starts from the user's own notes
                var hits = ctx.S<MemoryIndex>().Search(q, 3)
                    .Where(h => h.Kind == "brain_doc")
                    .Select(h => $"- {h.Title}: {h.Snippet}").ToList();
                var context = hits.Count > 0
                    ? $"\n\nPossibly relevant notes from the user's project brains (data/brains/):\n{string.Join("\n", hits)}\n"
                    : "";
                var prompt = $"Question from the user about this codebase/project: {q}{context}\n\nInvestigate (read-only) and answer concisely.";
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "claude-question", project, repo, edit: false);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "codex_review",
            Description = "dispatch Codex to review the latest changes in a repo (read-only)",
            Patterns = [@"^ask codex to (review|verify|check)( (claude'?s|the|my))?( last| latest| recent)? ?(changes|work|run|fix(es)?|commit)?( (on|in|for) (?<project>.+))?$"],
            SlotHints = ["project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var (project, repo, err) = await ResolveTargetAsync(ctx, defaultSlug: null, requireRepo: true);
                if (err is not null) return new CommandResult(err);
                var prompt = """
                    Review the most recent changes in this repository: run `git log -3 --stat` and `git diff HEAD~1..HEAD`
                    (plus `git diff` for uncommitted work). Assess correctness, bugs, risks and missed edge cases.
                    Be specific and reference files/lines. Verdict: approve, approve-with-nits, or needs-work.
                    """;
                var (run, spoken) = await agents.DispatchAsync("codex", prompt, "codex-review", project, repo, edit: false);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "ask_codex_edit",
            Description = "dispatch Codex to do a coding task (edits files in the project repo)",
            Patterns = [@"^ask codex to (?<task>.+?)( (on|in|for) (?<project>[\w .'-]+))?$"],
            SlotHints = ["task", "project"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var task = ctx.Slot("task");
                if (task is null || task.Length < 5) return new CommandResult("") { Unhandled = true };
                var (project, repo, err) = await ResolveTargetAsync(ctx, defaultSlug: null, requireRepo: true);
                if (err is not null) return new CommandResult(err);
                var prompt = $"""
                    Task from the user (via JARVIS voice command): {task}

                    Work in the current repository. Make minimal, conventional changes. Verify with a build/tests where possible.
                    """;
                var (run, spoken) = await agents.DispatchAsync("codex", prompt, "codex-task", project, repo, edit: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "agent_follow_up",
            Description = "continue the last agent run as a conversation (read-only)",
            Patterns = [@"^(follow up[,:]? (?<text>.+)|tell (?<agent>claude|codex)[,:]? (?<text>.+))$"],
            SlotHints = ["text", "agent"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var text = ctx.Slot("text");
                if (text is null || text.Length < 3) return new CommandResult("") { Unhandled = true };
                var parent = await LastRunAsync(ctx, ctx.Slot("agent"));
                if (parent is null) return new CommandResult("No agent run to follow up on yet.");
                if (parent.Status == "running") return new CommandResult($"{parent.Agent} is still working — I'll announce when it's done.");
                if (parent.Mode == "edit")
                    return new CommandResult("That was an edit session. Say: follow up with edits, then your instruction.");
                var tools = parent.PromptTemplate.StartsWith("jira") ? JiraMcpReadTools : null;
                var (run, spoken) = await agents.DispatchFollowUpAsync(parent, text, tools);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id, parent = parent.Id });
            }
        },
        new()
        {
            Name = "agent_follow_up_edit",
            Description = "continue the last agent session with file edits allowed",
            Patterns = [@"^follow up with edits[,:]? (?<text>.+)$"],
            SlotHints = ["text"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var parent = await LastRunAsync(ctx, agent: null);
                if (parent is null) return new CommandResult("No agent run to follow up on yet.");
                var (run, spoken) = await agents.DispatchFollowUpAsync(parent, ctx.Slot("text") ?? "", editOverride: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id, parent = parent.Id });
            }
        },
        new()
        {
            Name = "agent_retry_edit",
            Description = "re-run the last agent task with file edits allowed",
            Patterns = [@"^retry (that|it|the last run) with edits( allowed)?$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var parent = await LastRunAsync(ctx, agent: null);
                if (parent is null) return new CommandResult("No agent run to retry yet.");
                var (run, spoken) = await agents.DispatchFollowUpAsync(parent,
                    "Now actually make the changes you described or investigated.", editOverride: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id, parent = parent.Id });
            }
        },
        new()
        {
            Name = "claude_plan",
            Description = "have Claude plan a feature (read-only, follow-ups become tasks)",
            Patterns = [@"^(ask )?claude,? plan (?<feature>.+?)( (for|on|in) (?<project>[\w .'-]+))?$"],
            SlotHints = ["feature", "project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var (project, repo, _) = await ResolveTargetAsync(ctx, defaultSlug: null);
                var prompt = $"""
                    Plan this feature for the user: {ctx.Slot("feature")}

                    Investigate the codebase read-only, design the implementation, and put each concrete
                    implementation step into the RESULT followUps array so they become tracked tasks.
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "claude-plan", project, repo, edit: false);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "summarize_uncommitted",
            Description = "summarize uncommitted work in a repo (read-only, pre-standup)",
            Patterns = [@"^summari[sz]e (my |the )?uncommitted (work|changes)( (on|in|for) (?<project>.+))?$"],
            SlotHints = ["project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var (project, repo, err) = await ResolveTargetAsync(ctx, defaultSlug: null, requireRepo: true);
                if (err is not null) return new CommandResult(err);
                var prompt = """
                    Summarize the uncommitted work in this repository for a standup: run `git status --porcelain`
                    and `git diff` (plus `git diff --staged`), then describe what's in flight in 3-5 spoken-friendly
                    sentences: what areas changed, what it's for, and anything risky or half-done.
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "summarize-uncommitted", project, repo,
                    edit: false, readonlyAllowedTools: "Read,Glob,Grep,Bash(git status:*),Bash(git diff:*),Bash(git log:*)");
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "jira_my_issues",
            Description = "list my open Jira issues (via Claude's Atlassian MCP — read-only)",
            Patterns = [@"^(what are )?my( open)? jira (issues|bugs|tickets)\??$", @"^jira issues\??$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var prompt = """
                    Using the Atlassian MCP Jira tools available to you (e.g. atlassianUserInfo,
                    searchJiraIssuesUsingJql), find the issues currently assigned to me that are not done
                    (JQL: assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC).
                    List key, priority, status and summary for up to 10.
                    If the Atlassian MCP tools are NOT available in this session, state that plainly.
                    """;
                var (run, spoken) = await ResolveAndDispatchJiraAsync(ctx, prompt, "jira-my-issues", edit: false);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "jira_pull_bugs",
            Description = "pull the main project's open Jira bugs into its tester inbox (writes issues.md)",
            Patterns = [@"^pull (the )?(open )?(bugs|issues) from jira( into the (tester )?inbox)?$",
                        @"^sync (the )?tester inbox (with|from) jira$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var main = await ctx.S<JarvisDb>().Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
                if (main is null) return new CommandResult("No main project is registered.");
                var inbox = Path.GetRelativePath(JarvisPaths.Root, TesterIssuesService.FilePath(main)).Replace('\\', '/');
                var prompt = $"""
                    Using the Atlassian MCP Jira tools, find the open bugs of the Jira project that best matches
                    "{main.Name}" (discover the project key via getVisibleJiraProjects if needed; statusCategory != Done,
                    type Bug, order by priority). Then UPDATE the file {inbox}:
                    keep its header, ensure each open bug appears once as a checklist line
                    '- [ ] [P1] KEY: summary' (map priority Highest/High→P1, Medium→P2, Low→P3, Lowest→P4).
                    Do not remove manually added lines. If the Atlassian MCP tools are NOT available, state that plainly and change nothing.
                    """;
                var (run, spoken) = await ResolveAndDispatchJiraAsync(ctx, prompt, "jira-pull-bugs", edit: true);
                Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "screen_question",
            Description = "capture the screen and ask Claude about it (the screenshot is sent to the agent)",
            Patterns = [@"^what('s| is) on my screen\??$",
                        @"^explain (this|the|that) (error|exception|stack ?trace|screen|message)$",
                        @"^look at my screen[,:]? ?(?<q>.+)?$"],
            SlotHints = ["q"],
            // Public edition: a full-screen capture leaves the machine with the agent run, so it
            // waits for an explicit "confirm" like other Mutate actions.
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var path = ctx.S<ScreenshotService>().Capture();
                if (path is null) return new CommandResult("I couldn't capture the screen.");
                var q = ctx.Slot("q")
                    ?? (ctx.Utterance.Contains("error") || ctx.Utterance.Contains("exception") || ctx.Utterance.Contains("trace")
                        ? "explain the error/exception visible on screen and how to fix it"
                        : "describe what's on screen, focusing on anything that looks wrong or needs attention");
                var prompt = $"""
                    Read the screenshot image at {path} and answer for the user (spoken-friendly, max 5 sentences): {q}
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "screen-question",
                    project: null, repo: null, edit: false, readonlyAllowedTools: "Read");
                Tag(ctx, run);
                return new CommandResult($"Taking a look at your screen — about twenty seconds. {spoken}", new { run.Id, path });
            }
        },
        new()
        {
            Name = "jira_comment",
            Description = "add a comment to a Jira issue (verifies the issue first)",
            Patterns = [@"^comment on (?<key>[\w -]+?)\s*[,:] (?<text>.+)$",
                        @"^add a comment (to|on) (?<key>[\w -]+?)\s*[,:] (?<text>.+)$",
                        @"^comment on (?<key>[\w -]+?) (saying|that says) (?<text>.+)$"],
            SlotHints = ["key", "text"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var key = NormalizeIssueKey(ctx.Slot("key"));
                if (key is null) return new CommandResult($"I couldn't make a Jira key out of '{ctx.Slot("key")}'.");
                var prompt = $"""
                    Using the Atlassian MCP Jira tools: first fetch issue {key} (getJiraIssue) and verify it
                    exists — if it doesn't, STOP and report that, changing nothing. If it exists, add this
                    comment to it from the user (via voice): "{ctx.Slot("text")}"
                    Then confirm what you did, quoting the issue summary.
                    """;
                var (run, spoken) = await DispatchJiraWriteAsync(ctx, prompt, "jira-comment");
                return new CommandResult(spoken, new { run.Id, key });
            }
        },
        new()
        {
            Name = "jira_transition",
            Description = "move a Jira issue to another status (in progress, done…)",
            // The key MUST look like an issue key (letters + number/number-words) — a Mutate
            // command parks before its handler runs, so a greedy pattern here would hijack
            // ordinary "set X to Y" phrases (it ate "set voice output to headset").
            Patterns = [@"^move (?<key>[a-z]+(?:[ -](?:\d+|zero|oh|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred))+) to (?<state>[\w ]+)$",
                        @"^(set|mark) (?<key>[a-z]+(?:[ -](?:\d+|zero|oh|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred))+) (as|to) (?<state>[\w ]+)$"],
            SlotHints = ["key", "state"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var key = NormalizeIssueKey(ctx.Slot("key"));
                if (key is null) return new CommandResult("") { Unhandled = true };  // "mark X as done" may be a task/commitment
                var prompt = $"""
                    Using the Atlassian MCP Jira tools: fetch issue {key} and verify it exists — if not, STOP
                    and report that. Then call getTransitionsForJiraIssue, pick the transition that best matches
                    "{ctx.Slot("state")}" (fuzzy match: 'in progress' ≈ 'Start Progress' etc.), and apply it with
                    transitionJiraIssue. Report the issue summary and the exact transition you applied.
                    """;
                var (run, spoken) = await DispatchJiraWriteAsync(ctx, prompt, "jira-transition");
                return new CommandResult(spoken, new { run.Id, key });
            }
        },
        new()
        {
            Name = "jira_create",
            Description = "create a Jira issue in a project",
            Patterns = [@"^create a jira (?<type>bug|task|story|issue) (in|for|on) (?<proj>[\w ]+?)[,:]? (?<summary>.+)$"],
            SlotHints = ["type", "proj", "summary"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var type = ctx.Slot("type") == "issue" ? "Task" : Cap(ctx.Slot("type") ?? "task");
                var prompt = $"""
                    Using the Atlassian MCP Jira tools: find the project matching "{ctx.Slot("proj")}"
                    (getVisibleJiraProjects, fuzzy match the name). Create a {type} issue in it with
                    summary: "{ctx.Slot("summary")}" and description noting it was filed by the user via JARVIS voice.
                    Report the new issue key.
                    """;
                var (run, spoken) = await DispatchJiraWriteAsync(ctx, prompt, "jira-create");
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "agent_status",
            Description = "report running and recent agent runs",
            Patterns = [@"^(agent status|what are the agents doing|any agents? running\??)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var recent = await db.AgentRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(3).ToListAsync();
                if (agents.RunningCount == 0 && recent.Count == 0)
                    return new CommandResult("No agent runs yet. Say: ask Claude to, or ask Codex to review.");
                var lines = recent.Select(r => $"{r.Agent} {r.PromptTemplate}: {r.Status}" +
                    (r.Status == "done" ? $" — {Truncate(r.ResultSummary, 80)}" : ""));
                return new CommandResult(
                    $"{agents.RunningCount} agent{(agents.RunningCount == 1 ? "" : "s")} running. Recent: {string.Join(". ", lines)}.",
                    new { running = agents.RunningCount, recent });
            }
        },
        new()
        {
            Name = "last_agent_result",
            Description = "speak the result of the last Claude/Codex run",
            Patterns = [@"^what did (?<agent>claude|codex) (do|say|find|report)( last( time)?)?\??$"],
            SlotHints = ["agent"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var agent = ctx.Slot("agent")?.ToLowerInvariant() ?? "claude";
                var run = await db.AgentRuns.AsNoTracking().Where(r => r.Agent == agent)
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                if (run is null) return new CommandResult($"{agent} hasn't run yet.");
                return new CommandResult($"Last {agent} run ({run.PromptTemplate}, {run.Status}): {run.ResultSummary}",
                    new { run });
            }
        },
        new()
        {
            Name = "compare_reports",
            Description = "compare the last Claude report with the last Codex report",
            Patterns = [@"^compare (claude('?s)? (report )?(with|and|to) codex('?s)?( report)?|the reports)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var claude = await db.AgentRuns.AsNoTracking().Where(r => r.Agent == "claude" && r.Status == "done")
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                var codex = await db.AgentRuns.AsNoTracking().Where(r => r.Agent == "codex" && r.Status == "done")
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                if (claude is null || codex is null)
                    return new CommandResult("I need at least one completed run from each agent to compare.");
                return new CommandResult(
                    $"Claude reported: {Truncate(claude.ResultSummary, 200)} — Codex reported: {Truncate(codex.ResultSummary, 200)}",
                    new { claude = claude.ResultSummary, codex = codex.ResultSummary });
            }
        },
    ];

    private const string JiraMcpReadTools =
        "mcp__atlassian__getAccessibleAtlassianResources,mcp__atlassian__atlassianUserInfo," +
        "mcp__atlassian__searchJiraIssuesUsingJql,mcp__atlassian__getJiraIssue," +
        "mcp__atlassian__getVisibleJiraProjects,mcp__atlassian__lookupJiraAccountId,Read,Glob";

    // Writes get the read tools plus exactly the mutation tools each command needs.
    // Tool names drift across MCP versions — verify with `claude mcp list` when they 404.
    private const string JiraMcpWriteTools = JiraMcpReadTools +
        ",mcp__atlassian__addCommentToJiraIssue,mcp__atlassian__transitionJiraIssue," +
        "mcp__atlassian__createJiraIssue,mcp__atlassian__getTransitionsForJiraIssue,mcp__atlassian__editJiraIssue";

    private async Task<(AgentRun Run, string Spoken)> DispatchJiraWriteAsync(
        CommandContext ctx, string prompt, string template)
    {
        var db = ctx.S<JarvisDb>();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
        // Writes ride an explicit MCP allowlist (not edit mode): Jira mutates, local files don't
        var (run, spoken) = await agents.DispatchAsync("claude", prompt, template, project, repo: null,
            edit: false, readonlyAllowedTools: JiraMcpWriteTools);
        Tag(ctx, run);
        return (run, spoken);
    }

    private static readonly Dictionary<string, int> SpokenDigits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["oh"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11,
        ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30,
        ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>
    /// STT mangles issue keys: "DEN-142" arrives as "den one forty two" / "den 142" / "den dash 142".
    /// Letters become the project key; trailing digits/number-words compose the number
    /// (tens+units add, "hundred" multiplies: "one forty two" → 1,40,2 → 142).
    /// </summary>
    internal static string? NormalizeIssueKey(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        var words = spoken.ToLowerInvariant().Replace("-", " ").Replace("dash", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = new List<string>();
        var digits = "";
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (int.TryParse(w, out var n)) digits += n.ToString();
            else if (w == "hundred") { /* "one hundred forty two" — digit concatenation already yields 142 */ }
            else if (SpokenDigits.TryGetValue(w, out var d))
            {
                if (d >= 20 && d % 10 == 0 && i + 1 < words.Length
                    && SpokenDigits.TryGetValue(words[i + 1], out var unit) && unit is >= 1 and <= 9)
                {
                    digits += $"{d / 10}{unit}";   // "forty two" → "42"
                    i++;
                }
                else digits += d.ToString();        // "fifteen" → "15", bare "twenty" → "20"
            }
            else if (digits.Length == 0 && w.All(char.IsLetter)) letters.Add(w);
            else return null;   // letters after digits = not a key
        }
        if (letters.Count == 0 || digits.Length == 0) return null;
        return $"{string.Concat(letters).ToUpperInvariant()}-{int.Parse(digits)}";
    }

    private async Task<(AgentRun Run, string Spoken)> ResolveAndDispatchJiraAsync(
        CommandContext ctx, string prompt, string template, bool edit)
    {
        var db = ctx.S<JarvisDb>();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
        // Jira runs execute in the JARVIS workspace (issues.md lives in data/brains), not the code repo
        return await agents.DispatchAsync("claude", prompt, template, project, repo: null, edit,
            readonlyAllowedTools: edit ? null : JiraMcpReadTools);
    }

    private static async Task<AgentRun?> LastRunAsync(CommandContext ctx, string? agent)
    {
        var db = ctx.S<JarvisDb>();
        var q = db.AgentRuns.AsNoTracking().Where(r => r.Status != "failed" || r.ResultSummary != "");
        if (agent is not null) q = q.Where(r => r.Agent == agent.ToLowerInvariant());
        return await q.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    }

    private static async Task<(Project? Project, Repo? Repo, string? Error)> ResolveTargetAsync(
        CommandContext ctx, string? defaultSlug, bool requireRepo = false)
    {
        var resolver = ctx.S<EntityResolver>();
        var db = ctx.S<JarvisDb>();
        var project = await resolver.ResolveProjectAsync(ctx.Slot("project"));
        if (project is null && defaultSlug is not null)
            project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == defaultSlug);
        Repo? repo = null;
        if (project is not null)
            repo = await db.Repos.AsNoTracking().Where(r => r.ProjectId == project.Id)
                .OrderByDescending(r => r.IsPrimary).FirstOrDefaultAsync();
        repo ??= await resolver.ResolveRepoAsync(ctx.Slot("project"));
        if (requireRepo && repo is null)
            return (project, null, $"No repo registered{(project is null ? "" : $" for {project.Name}")}. Say: register repo <path> for <project>.");
        return (project, repo, null);
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>Enrich the command audit trail with what this dispatch actually ran.</summary>
    internal static void Tag(CommandContext ctx, AgentRun run)
    {
        ctx.Audit.Tool = run.Agent == "codex" ? "codex exec" : "claude -p";
        ctx.Audit.AgentRunId = run.Id;
        ctx.Audit.LogRef = run.TranscriptPath ?? "";
    }
}
