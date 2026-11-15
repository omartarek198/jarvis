using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class PlaywrightModule(PlaywrightService playwright, AgentService agents) : IJarvisModule
{
    public string Name => "playwright";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "run_ui_audit",
            Description = "run the Playwright test suite / UI audit for a project",
            Patterns = [@"^run (the )?(?<project>[\w .'-]+?) (ui |browser )?(audit|tests?|test suite)$"],
            SlotHints = ["project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                if (project is null) return new CommandResult("") { Unhandled = true };
                var db = ctx.S<JarvisDb>();
                var repo = await db.Repos.AsNoTracking().Where(r => r.ProjectId == project.Id)
                    .OrderByDescending(r => r.IsPrimary).FirstOrDefaultAsync();
                if (repo is null)
                    return new CommandResult($"No repo registered for {project.Name}. Say: register repo <path> for {project.Name}.");
                if (!PlaywrightService.HasConfig(repo))
                    return new CommandResult($"{repo.Name} has no playwright config. Add playwright.config.ts to the repo first.");
                var (spoken, runId) = playwright.Start(project, repo, "test suite");
                return new CommandResult(spoken, new { runId });
            }
        },
        new()
        {
            Name = "playwright_failures",
            Description = "summarize the failures of the last Playwright run",
            Patterns = [@"^summari[sz]e (the )?(playwright|test) (failures|results)( (for|in) (?<project>.+))?$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                var q = db.PlaywrightRuns.AsNoTracking().AsQueryable();
                if (project is not null) q = q.Where(r => r.ProjectId == project.Id);
                var run = await q.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                if (run is null) return new CommandResult("No Playwright runs yet. Say: run <project> ui audit.");
                return new CommandResult($"Last run ({run.EndedAt:t}): {run.Summary}", new { run });
            }
        },
        new()
        {
            Name = "failures_to_claude",
            Description = "dispatch Claude to fix the last Playwright failures (edits files)",
            Patterns = [@"^(send|give) (these|the) (playwright |test )?failures to claude$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var run = await db.PlaywrightRuns.AsNoTracking().Where(r => r.Failed > 0)
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                if (run is null) return new CommandResult("No failing Playwright run to hand over.");
                var project = run.ProjectId is null ? null : await db.Projects.FindAsync(run.ProjectId);
                var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Path == run.RepoPath);
                var prompt = $"""
                    The Playwright suite has {run.Failed} failing test(s). Diagnose and fix the underlying causes
                    (app code or tests — judge which is wrong). Failures JSON:

                    {run.FailuresJson}

                    Re-run the failing specs with `npx playwright test --last-failed` to verify before finishing.
                    """;
                var (agentRun, spoken) = await agents.DispatchAsync("claude", prompt, "playwright-fix", project, repo, edit: true);
                return new CommandResult(spoken, new { agentRun.Id });
            }
        },
        new()
        {
            Name = "tester_issues_summary",
            Description = "summarize open tester issues for a project",
            Patterns = [@"^summari[sz]e( the)? tester issues( (for|in|on) (?<project>.+))?$", @"^what are the tester issues( (for|in|on) (?<project>.+))?$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"))
                    ?? await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
                if (project is null) return new CommandResult("No project found.");
                var issues = (await TesterIssuesService.ReadAsync(project)).Where(i => !i.Closed).ToList();
                if (issues.Count == 0)
                    return new CommandResult($"No open tester issues for {project.Name}. The inbox file is {TesterIssuesService.FilePath(project)}.");
                var bySev = issues.GroupBy(i => i.Severity).OrderBy(g => g.Key)
                    .Select(g => $"{g.Count()} {g.Key}");
                var top = issues.OrderBy(i => i.Severity).Take(3).Select(i => $"{i.Severity}: {i.Text}");
                return new CommandResult(
                    $"{project.Name} has {issues.Count} open tester issues — {string.Join(", ", bySev)}. Top: {string.Join(". ", top)}. " +
                    "Say 'ask Claude to fix high severity tester issues' to dispatch a fix run.",
                    new { issues });
            }
        },
    ];
}

public class WarRoomModule : IJarvisModule
{
    public string Name => "warroom";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "open_war_room",
            Description = "open the project war room (live status board)",
            Patterns = [@"^open( the)?( (?<project>[\w .'-]+?))? war ?room$", @"^war ?room( for (?<project>.+))?$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"))
                    ?? await db.Projects.AsNoTracking().FirstAsync(p => p.Slug == Seeder.PrimaryProjectSlug);
                var warRoom = ctx.S<WarRoomService>();
                var spoken = await warRoom.SpokenAsync(project);
                var url = $"http://localhost:5588/warroom.html?p={project.Slug}";
                if (ctx.Source is "voice" or "cli")
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch { /* browser launch is best-effort */ }
                }
                return new CommandResult(spoken, new { url, warRoom = await warRoom.BuildAsync(project) });
            }
        },
    ];
}

public class FocusModule : IJarvisModule
{
    public string Name => "focus";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "day_report",
            Description = "report where today's time went by app category",
            Patterns = [@"^where did my (day|time) go\??$", @"^(focus|day) report$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var samples = await db.FocusSamples.AsNoTracking()
                    .Where(f => f.At >= DateTime.UtcNow.Date).ToListAsync();
                if (samples.Count == 0) return new CommandResult("No focus data yet today — the tracker samples while the kernel runs.");
                var byCat = samples.GroupBy(s => s.Category)
                    .Select(g => (Cat: g.Key, Mins: g.Sum(s => s.DurationSec) / 60))
                    .Where(x => x.Mins > 0).OrderByDescending(x => x.Mins).ToList();
                var switches = CountSwitches(samples);
                var parts = byCat.Select(x => $"{x.Mins} minutes in {Speak(x.Cat)}");
                return new CommandResult(
                    $"Today: {string.Join(", ", parts)}. About {switches} context switches.",
                    new { byCategory = byCat, switches, totalSamples = samples.Count });
            }
        },
        new()
        {
            Name = "wasting_time",
            Description = "judge whether time is being wasted on distractions",
            Patterns = [@"^am i wasting( my)? time\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var samples = await db.FocusSamples.AsNoTracking()
                    .Where(f => f.At >= DateTime.UtcNow.Date).ToListAsync();
                var distraction = samples.Where(s => s.Category == "distraction").Sum(s => s.DurationSec) / 60;
                var productive = samples.Where(s => s.Category is "ide" or "terminal").Sum(s => s.DurationSec) / 60;
                var verdict = distraction == 0 ? "Zero distraction time logged. Exemplary."
                    : distraction > productive ? $"Yes. {distraction} distraction minutes versus {productive} productive. Close the tabs, sir."
                    : $"Within tolerance: {distraction} distraction minutes against {productive} productive.";
                return new CommandResult(verdict, new { distraction, productive });
            }
        },
        new()
        {
            Name = "focus_mode",
            Description = "start a timed focus session",
            Patterns = [@"^focus mode( for (?<mins>\d+) min(ute)?s?)?$"],
            SlotHints = ["mins"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                if (await db.FocusSessions.AnyAsync(f => f.EndedAt == null))
                    return new CommandResult("A focus session is already running.");
                var mins = int.TryParse(ctx.Slot("mins"), out var m) ? Math.Clamp(m, 5, 240) : 90;
                db.FocusSessions.Add(new FocusSession { PlannedMinutes = mins });
                await db.SaveChangesAsync();
                return new CommandResult($"Focus mode engaged for {mins} minutes. I'll tell you when time's up. Go.");
            }
        },
        new()
        {
            Name = "end_focus",
            Description = "end the running focus session early",
            Patterns = [@"^end focus( mode| session)?$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var f = await db.FocusSessions.FirstOrDefaultAsync(x => x.EndedAt == null);
                if (f is null) return new CommandResult("No focus session is running.");
                f.EndedAt = DateTime.UtcNow;
                f.Outcome = "ended early";
                await db.SaveChangesAsync();
                var actual = (int)(f.EndedAt.Value - f.StartedAt).TotalMinutes;
                return new CommandResult($"Focus session ended at {actual} of {f.PlannedMinutes} minutes.");
            }
        },
    ];

    private static int CountSwitches(List<FocusSample> samples)
    {
        var ordered = samples.OrderBy(s => s.At).ToList();
        var switches = 0;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].ProcessName != ordered[i - 1].ProcessName) switches++;
        return switches;
    }

    private static string Speak(string cat) => cat switch
    {
        "ide" => "the IDE",
        "teams" => "Teams",
        "terminal" => "terminals",
        "browser" => "the browser",
        "distraction" => "distractions",
        _ => "other apps",
    };
}
