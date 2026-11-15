using System.Text;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

public class Prioritizer(JarvisDb db)
{
    public static double Score(TaskItem t)
    {
        double D(double? v) => v ?? 2.5;
        var score = 2.0 * D(t.MoneyImpact) + 1.6 * D(t.Urgency) + 1.3 * D(t.StrategicValue)
                  + 0.8 * D(t.CompletionOdds) + 0.4 * D(t.Fun)
                  - 1.2 * D(t.Effort) - 0.5 * D(t.Energy)
                  + t.Priority * 1.5;
        if (t.DueDate is { } due)
        {
            if (due.Date < DateTime.Today) score += 5;
            else if (due.Date <= DateTime.Today.AddDays(2)) score += 3;
        }
        return score;
    }

    public async Task<List<(TaskItem Task, double Score, string Project)>> RankAsync(string? projectId = null, int take = 5)
    {
        var q = db.Tasks.AsNoTracking().Where(t => t.Status == "open" || t.Status == "doing");
        if (projectId is not null) q = q.Where(t => t.ProjectId == projectId);
        var tasks = await q.ToListAsync();
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        return [.. tasks
            .Select(t => (t, Score(t), t.ProjectId is not null && projects.TryGetValue(t.ProjectId, out var n) ? n : "general"))
            .OrderByDescending(x => x.Item2)
            .Take(take)];
    }
}

public class ProjectStatusService(JarvisDb db, GitService git)
{
    public async Task<string> SynthesizeAsync(Project p)
    {
        var sb = new StringBuilder();
        var openTasks = await db.Tasks.CountAsync(t => t.ProjectId == p.Id && (t.Status == "open" || t.Status == "doing"));
        var blocked = await db.Tasks.CountAsync(t => t.ProjectId == p.Id && t.Status == "blocked");
        var openCommitments = await db.Commitments.CountAsync(c => c.ProjectId == p.Id && c.Status == "open");

        sb.Append($"{p.Name}: {p.Status}. ");
        if (!string.IsNullOrWhiteSpace(p.Summary)) sb.Append(p.Summary.TrimEnd('.') + ". ");
        sb.Append($"{openTasks} open task{(openTasks == 1 ? "" : "s")}");
        if (blocked > 0) sb.Append($", {blocked} blocked");
        sb.Append($", {openCommitments} open commitment{(openCommitments == 1 ? "" : "s")}. ");

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == p.Id && r.IsPrimary)
                ?? await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == p.Id);
        if (repo is not null && Directory.Exists(repo.Path))
        {
            var st = await git.StatusAsync(repo.Path);
            if (st is not null)
            {
                sb.Append($"Repo on branch {st.Branch}, {(st.Clean ? "clean" : $"{st.DirtyCount} uncommitted files")}. ");
                if (!string.IsNullOrWhiteSpace(st.LastCommit)) sb.Append($"Last commit: {st.LastCommit}. ");
            }
        }

        var lastRun = await db.AgentRuns.AsNoTracking().Where(a => a.ProjectId == p.Id)
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync();
        if (lastRun is not null)
            sb.Append($"Last agent run: {lastRun.Agent} — {lastRun.Status}. ");

        if (!string.IsNullOrWhiteSpace(p.NextAction)) sb.Append($"Suggested next action: {p.NextAction}.");
        return sb.ToString().Trim();
    }
}

public class BriefingService(JarvisDb db, GitService git, Prioritizer prioritizer,
    WeatherService weather, CryptoService crypto, NewsService news,
    GraphService graph, CalendarService outlookCal)
{
    /// <summary>Friday-afternoon (or on-demand) week-in-review: output, focus, habits, sleep.</summary>
    public async Task<(string Markdown, string Spoken)> BuildWeeklyAsync()
    {
        var weekAgoUtc = DateTime.UtcNow.AddDays(-7);
        var prevWeekUtc = DateTime.UtcNow.AddDays(-14);
        var md = new StringBuilder($"# Weekly Review — {DateTime.Now:dddd, MMM d}\n\n");
        var spoken = new StringBuilder("Weekly review. ");

        var completed = await db.Tasks.AsNoTracking().CountAsync(t => t.CompletedAt >= weekAgoUtc);
        var created = await db.Tasks.AsNoTracking().CountAsync(t => t.CreatedAt >= weekAgoUtc);
        md.AppendLine($"## Tasks\nCompleted: {completed} · Created: {created}\n");
        spoken.Append($"{SpeechText.Number(completed)} tasks done, {SpeechText.Number(created)} new. ");

        var ranked = await prioritizer.RankAsync(take: 3);
        if (ranked.Count > 0)
        {
            md.AppendLine("## Top open by ROI");
            foreach (var r in ranked) md.AppendLine($"- {r.Task.Title}");
            md.AppendLine();
            spoken.Append($"Top open: {ranked[0].Task.Title}. ");
        }

        var aging = await db.Commitments.AsNoTracking()
            .Where(c => c.Status == "open" && c.CreatedAt < weekAgoUtc).ToListAsync();
        if (aging.Count > 0)
        {
            md.AppendLine($"## Commitments aging > 7 days ({aging.Count})");
            foreach (var c in aging.Take(5)) md.AppendLine($"- {c.Text}");
            md.AppendLine();
            spoken.Append($"{SpeechText.Number(aging.Count)} commitments are over a week old. ");
        }

        var thisWeek = await db.FocusSamples.AsNoTracking().Where(f => f.At >= weekAgoUtc).ToListAsync();
        var lastWeek = await db.FocusSamples.AsNoTracking()
            .Where(f => f.At >= prevWeekUtc && f.At < weekAgoUtc).ToListAsync();
        if (thisWeek.Count > 0)
        {
            md.AppendLine("## Focus (minutes)");
            foreach (var g in thisWeek.GroupBy(f => f.Category).OrderByDescending(g => g.Sum(f => f.DurationSec)))
                md.AppendLine($"- {g.Key}: {g.Sum(f => f.DurationSec) / 60}");
            var distNow = thisWeek.Where(f => f.Category == "distraction").Sum(f => f.DurationSec) / 60;
            var distPrev = lastWeek.Where(f => f.Category == "distraction").Sum(f => f.DurationSec) / 60;
            md.AppendLine($"\nDistraction: {distNow}m (prev week {distPrev}m)\n");
            if (distPrev > 0)
                spoken.Append(distNow <= distPrev
                    ? $"Distraction down to {distNow} minutes from {distPrev}. "
                    : $"Distraction up to {distNow} minutes from {distPrev}. ");
        }

        var habits = await db.Habits.AsNoTracking().Where(h => h.Active).ToListAsync();
        if (habits.Count > 0)
        {
            md.AppendLine("## Habits");
            foreach (var h in habits)
            {
                var done = await db.HabitLogs.AsNoTracking()
                    .CountAsync(l => l.HabitId == h.Id && l.Date >= DateTime.Today.AddDays(-7));
                md.AppendLine($"- {h.Name}: {done}/{(h.Schedule == "weekdays" ? 5 : 7)}");
            }
            md.AppendLine();
        }

        var sleep = await db.SleepLogs.AsNoTracking()
            .Where(s => s.Date >= DateTime.Today.AddDays(-7) && s.DurationMin != null).ToListAsync();
        if (sleep.Count > 0)
        {
            var avg = (int)sleep.Average(s => s.DurationMin!.Value);
            md.AppendLine($"## Sleep\nAverage: {avg / 60}h {avg % 60}m over {sleep.Count} nights\n");
            spoken.Append($"Sleep averaged {avg / 60} hours {SpeechText.Number(avg % 60)}. ");
        }

        var runs = await db.AgentRuns.AsNoTracking()
            .CountAsync(r => r.Status == "done" && r.CreatedAt >= weekAgoUtc);
        md.AppendLine($"## Agents\n{runs} completed runs this week\n");
        spoken.Append($"{SpeechText.Number(runs)} agent runs shipped.");

        return (md.ToString(), spoken.ToString());
    }

    public async Task<(string Markdown, string Spoken)> BuildMorningAsync()
    {
        var md = new StringBuilder("# Morning Briefing — " + DateTime.Now.ToString("dddd, MMM d HH:mm") + "\n\n");
        var spoken = new StringBuilder($"Good morning. ");

        // Sleep: last night + weekly average (best-effort)
        try
        {
            var sleep = await db.SleepLogs.AsNoTracking()
                .Where(s => s.Date >= DateTime.Today.AddDays(-7) && s.DurationMin != null).ToListAsync();
            var lastNight = sleep.FirstOrDefault(s => s.Date == DateTime.Today);
            if (lastNight?.DurationMin is { } mins)
            {
                md.AppendLine($"## Sleep\nLast night: {mins / 60}h {mins % 60}m ({lastNight.BedSource}/{lastNight.WakeSource})\n");
                spoken.Append($"You slept {mins / 60} hours {SpeechText.Number(mins % 60)} minutes. ");
            }
            if (sleep.Count >= 3)
            {
                var avg = (int)sleep.Average(s => s.DurationMin!.Value);
                md.AppendLine($"7-day average: {avg / 60}h {avg % 60}m\n");
            }
        }
        catch { }

        // Monday habit scorecard
        try
        {
            if (DateTime.Today.DayOfWeek == DayOfWeek.Monday)
            {
                var habits = await db.Habits.AsNoTracking().Where(h => h.Active).ToListAsync();
                if (habits.Count > 0)
                {
                    md.AppendLine("## Habit scorecard (last 7 days)");
                    var spokenBits = new List<string>();
                    foreach (var h in habits)
                    {
                        var weekAgo = DateTime.Today.AddDays(-7);
                        var done = await db.HabitLogs.AsNoTracking()
                            .CountAsync(l => l.HabitId == h.Id && l.Date >= weekAgo);
                        var target = h.Schedule == "weekdays" ? 5 : 7;
                        md.AppendLine($"- {h.Name}: {done}/{target}");
                        spokenBits.Add($"{h.Name} {SpeechText.Number(done)} of {SpeechText.Number(target)}");
                    }
                    md.AppendLine();
                    spoken.Append("Habit scorecard: " + string.Join(", ", spokenBits) + ". ");
                }
            }
        }
        catch { }

        // Calendar: Graph (M365) first, classic Outlook COM fallback — both best-effort
        try
        {
            var graphEvents = graph.LoggedIn
                ? await graph.CalendarViewAsync(DateTime.Today).WaitAsync(TimeSpan.FromSeconds(5))
                : null;
            if (graphEvents is { Count: > 0 })
            {
                md.AppendLine($"## Calendar today ({graphEvents.Count})");
                foreach (var e in graphEvents) md.AppendLine($"- {e.StartLocal:HH:mm} {e.Subject}");
                md.AppendLine();
                spoken.Append($"{graphEvents.Count} meeting{(graphEvents.Count == 1 ? "" : "s")} today, " +
                              $"first at {graphEvents[0].StartLocal:h:mm tt}: {graphEvents[0].Subject}. ");
            }
            else if (graphEvents is null && outlookCal.EventsFor(DateTime.Today) is { Count: > 0 } comEvents)
            {
                md.AppendLine($"## Calendar today ({comEvents.Count})");
                foreach (var e in comEvents) md.AppendLine($"- {e.Start:HH:mm} {e.Subject}");
                md.AppendLine();
                spoken.Append($"{comEvents.Count} calendar event{(comEvents.Count == 1 ? "" : "s")} today. ");
            }
        }
        catch { }

        // Live extras are best-effort — a dead API must never block the briefing
        try
        {
            var wx = await weather.SpokenForecastAsync(null, "today").WaitAsync(TimeSpan.FromSeconds(4));
            if (wx is not null) { md.AppendLine($"## Weather\n{wx}\n"); spoken.Append(wx + " "); }
        }
        catch { }
        try
        {
            var prices = await crypto.PricesAsync(await crypto.WatchlistAsync()).WaitAsync(TimeSpan.FromSeconds(4));
            if (prices is { Count: > 0 })
                md.AppendLine("## Crypto\n" + string.Join(" · ", prices.Select(p => $"{p.Key} ${p.Value:N0}")) + "\n");
        }
        catch { }
        try
        {
            var heads = await news.HeadlinesAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
            if (heads.Count > 0)
            {
                md.AppendLine("## Headlines");
                foreach (var h in heads) md.AppendLine($"- {h.Title} ({h.Source})");
                md.AppendLine();
            }
        }
        catch { }

        var meetings = await db.Meetings.AsNoTracking()
            .Where(m => m.StartedAt >= DateTime.UtcNow.Date).ToListAsync();
        if (meetings.Count > 0)
        {
            md.AppendLine($"## Meetings today: {meetings.Count}");
            spoken.Append($"You have {meetings.Count} meeting{(meetings.Count == 1 ? "" : "s")} logged today. ");
        }

        var overdue = await db.Commitments.AsNoTracking()
            .Where(c => c.Status == "open" && c.DueDate != null && c.DueDate < DateTime.Now).ToListAsync();
        var dueSoon = await db.Commitments.AsNoTracking()
            .Where(c => c.Status == "open" && c.DueDate != null && c.DueDate >= DateTime.Now && c.DueDate <= DateTime.Now.AddDays(2))
            .ToListAsync();
        if (overdue.Count > 0)
        {
            md.AppendLine($"## ⚠ Overdue commitments ({overdue.Count})");
            foreach (var c in overdue) md.AppendLine($"- {c.Text} (due {DateParser.Speak(c.DueDate)})");
            spoken.Append($"Warning: {overdue.Count} overdue commitment{(overdue.Count == 1 ? "" : "s")} — first one: {overdue[0].Text}. ");
        }
        if (dueSoon.Count > 0)
        {
            md.AppendLine($"## Due in the next 2 days ({dueSoon.Count})");
            foreach (var c in dueSoon) md.AppendLine($"- {c.Text} (due {DateParser.Speak(c.DueDate)})");
            spoken.Append($"{dueSoon.Count} commitment{(dueSoon.Count == 1 ? " is" : "s are")} due within two days. ");
        }

        var ranked = await prioritizer.RankAsync(take: 3);
        if (ranked.Count > 0)
        {
            md.AppendLine("## Top tasks");
            foreach (var (t, s, proj) in ranked) md.AppendLine($"- [{proj}] {t.Title} (score {s:F1})");
            spoken.Append($"Highest ROI task right now: {ranked[0].Task.Title}, on {ranked[0].Project}. ");
        }

        var dirtyRepos = new List<string>();
        foreach (var r in await db.Repos.AsNoTracking().ToListAsync())
        {
            if (!Directory.Exists(r.Path)) continue;
            var st = await git.StatusAsync(r.Path);
            if (st is { Clean: false }) dirtyRepos.Add($"{r.Name} ({st.DirtyCount} files on {st.Branch})");
        }
        if (dirtyRepos.Count > 0)
        {
            md.AppendLine("## Uncommitted work");
            foreach (var d in dirtyRepos) md.AppendLine($"- {d}");
            spoken.Append($"Heads up: uncommitted work in {string.Join(" and ", dirtyRepos)}. ");
        }

        if (overdue.Count == 0 && dueSoon.Count == 0 && ranked.Count == 0)
            spoken.Append("Ledger is clear and no urgent tasks are queued. Good day for deep work on your main project. ");

        return (md.ToString(), spoken.ToString().Trim());
    }

    public async Task<(string Markdown, string Spoken)> BuildShutdownAsync()
    {
        var md = new StringBuilder("# Shutdown Briefing — " + DateTime.Now.ToString("dddd, MMM d HH:mm") + "\n\n");
        var spoken = new StringBuilder("Shutting down. ");
        var since = DateTime.UtcNow.Date;

        var doneTasks = await db.Tasks.AsNoTracking().Where(t => t.CompletedAt >= since).ToListAsync();
        var doneCommitments = await db.Commitments.AsNoTracking().Where(c => c.ClosedAt >= since).ToListAsync();
        var openCommitments = await db.Commitments.AsNoTracking().Where(c => c.Status == "open").CountAsync();
        var stillOpenToday = await db.Commitments.AsNoTracking()
            .Where(c => c.Status == "open" && c.DueDate != null && c.DueDate <= DateTime.Now.AddDays(1)).ToListAsync();

        md.AppendLine($"## Done today: {doneTasks.Count} tasks, {doneCommitments.Count} commitments closed");
        foreach (var t in doneTasks) md.AppendLine($"- ✔ {t.Title}");
        foreach (var c in doneCommitments) md.AppendLine($"- ✔ {c.Text}");
        spoken.Append($"Today you closed {doneTasks.Count} task{(doneTasks.Count == 1 ? "" : "s")} and {doneCommitments.Count} commitment{(doneCommitments.Count == 1 ? "" : "s")}. ");

        md.AppendLine($"\n## Still open: {openCommitments} commitments total");
        if (stillOpenToday.Count > 0)
        {
            md.AppendLine("### Due now/tomorrow:");
            foreach (var c in stillOpenToday) md.AppendLine($"- {c.Text} (due {DateParser.Speak(c.DueDate)})");
            spoken.Append($"{stillOpenToday.Count} commitment{(stillOpenToday.Count == 1 ? " is" : "s are")} due by tomorrow — don't forget: {stillOpenToday[0].Text}. ");
        }

        var ranked = await (new Prioritizer(db)).RankAsync(take: 1);
        if (ranked.Count > 0)
        {
            md.AppendLine($"\n## Suggested first task tomorrow\n- {ranked[0].Task.Title} [{ranked[0].Project}]");
            spoken.Append($"Suggested first task tomorrow: {ranked[0].Task.Title}. ");
        }
        spoken.Append("Good night, sir.");
        return (md.ToString(), spoken.ToString());
    }
}

public static class Seeder
{
    /// <summary>The project agent, QA and war-room commands fall back to when none is named.</summary>
    public const string PrimaryProjectSlug = "northwind";

    public static async Task SeedAsync(JarvisDb db, MemoryIndex memory, bool demoMode)
    {
        await db.Database.EnsureCreatedAsync();
        memory.EnsureCreated();

        // EnsureCreated never alters an existing db — add post-V1 tables idempotently
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Reminders" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Reminders" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Text" TEXT NOT NULL, "At" TEXT NOT NULL,
                "Recurrence" TEXT NOT NULL, "Kind" TEXT NOT NULL,
                "Status" TEXT NOT NULL, "FiredAt" TEXT NULL)
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Facts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Facts" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Text" TEXT NOT NULL)
            """);

        // Default settings for life modules (idempotent, also lands on existing installs).
        // Neutral defaults: change weather.home to your own city locally.
        async Task EnsureSetting(string key, string valueJson)
        {
            if (await db.Settings.FindAsync(key) is null)
                db.Settings.Add(new Setting { Key = key, ValueJson = valueJson });
        }
        await EnsureSetting("weather.home", """{"name":"London","lat":51.507,"lon":-0.128}""");
        await EnsureSetting("crypto.watchlist", """["bitcoin","ethereum"]""");
        await EnsureSetting("news.feeds", """["https://feeds.bbci.co.uk/news/world/rss.xml","https://hnrss.org/frontpage"]""");
        await db.SaveChangesAsync();

        if (await db.Projects.AnyAsync()) return;

        var seed = new (string Name, string Slug, string Type, string Aliases, string Summary)[]
        {
            ("Northwind API", PrimaryProjectSlug, "product", "northwind,north wind,the api,invoice service,the invoicing project",
                "Sample invoicing service (samples/northwind-api) used as the demo agent target."),
            ("Atlas Mobile", "atlas", "freelance", "atlas,atlas app,the mobile app",
                "Fictional client mobile app with a release next week."),
            ("Personal", "personal", "personal", "life,errands", "Personal tasks, fitness, errands, learning."),
            ("JARVIS", "jarvis", "hobby", "jarvis project,this project", "This command center itself."),
        };
        foreach (var (name, slug, type, aliases, summary) in seed)
        {
            var brain = Path.Combine(JarvisPaths.Brains, slug);
            Directory.CreateDirectory(brain);
            var summaryFile = Path.Combine(brain, "summary.md");
            if (!File.Exists(summaryFile))
                await File.WriteAllTextAsync(summaryFile, $"# {name}\n\n{summary}\n");
            db.Projects.Add(new Project { Name = name, Slug = slug, Type = type, Aliases = aliases, Summary = summary, BrainPath = brain });
        }
        await db.SaveChangesAsync();

        var jarvisProject = await db.Projects.FirstAsync(p => p.Slug == "jarvis");
        db.Repos.Add(new Repo { ProjectId = jarvisProject.Id, Name = "jarvis", Path = JarvisPaths.Root, IsPrimary = true });
        db.Machines.Add(new Machine { Name = "local", Role = "main", OsInfo = Environment.OSVersion.ToString(), LastSeenAt = DateTime.UtcNow, Status = "online" });
        await db.SaveChangesAsync();

        if (demoMode) await SeedDemoDataAsync(db);
    }

    /// <summary>
    /// Synthetic data for the public demo: a sample repo for agent runs, a few commitments,
    /// tasks and notes, and a tester-issue inbox. Every name here is fictional.
    /// </summary>
    private static async Task SeedDemoDataAsync(JarvisDb db)
    {
        var northwind = await db.Projects.FirstAsync(p => p.Slug == PrimaryProjectSlug);
        var atlas = await db.Projects.FirstAsync(p => p.Slug == "atlas");
        var personal = await db.Projects.FirstAsync(p => p.Slug == "personal");

        var sampleRepo = Path.Combine(JarvisPaths.Root, "samples", "northwind-api");
        if (Directory.Exists(sampleRepo))
            db.Repos.Add(new Repo { ProjectId = northwind.Id, Name = "northwind-api", Path = sampleRepo, IsPrimary = true,
                                    TestCommand = "python -m pytest tests" });

        northwind.NextAction = "Fix per-line tax rounding before the invoice export ships.";
        atlas.NextAction = "Send the release-candidate build to the client.";

        var today = DateTime.Today;
        db.Commitments.AddRange(
            new Commitment { Text = "send the invoice export spec", PersonName = "Dana (Atlas client)", ProjectId = atlas.Id,
                             DueDate = today.AddDays(1).AddHours(17), Priority = 2, Source = "demo-seed" },
            new Commitment { Text = "review the tax-rounding pull request", PersonName = "Sam", ProjectId = northwind.Id,
                             DueDate = today.AddDays(-1).AddHours(17), Priority = 2, Source = "demo-seed" },
            new Commitment { Text = "share the Q3 roadmap draft", PersonName = "the team", ProjectId = northwind.Id,
                             DueDate = today.AddDays(4).AddHours(12), Priority = 1, Source = "demo-seed" });

        db.Tasks.AddRange(
            new TaskItem { Title = "Round tax per line item in invoices.py", ProjectId = northwind.Id, Priority = 3,
                           Urgency = 0.8, StrategicValue = 0.7, Effort = 0.3, Source = "demo-seed" },
            new TaskItem { Title = "Validate positive quantities in the create-invoice handler", ProjectId = northwind.Id,
                           Priority = 2, Urgency = 0.5, StrategicValue = 0.6, Effort = 0.2, Source = "demo-seed" },
            new TaskItem { Title = "Prepare Atlas release notes", ProjectId = atlas.Id, Priority = 2,
                           DueDate = today.AddDays(2).AddHours(18), Urgency = 0.6, Effort = 0.4, Source = "demo-seed" },
            new TaskItem { Title = "Book a dentist appointment", ProjectId = personal.Id, Priority = 1, Source = "demo-seed" });

        db.Notes.Add(new Note { ProjectId = northwind.Id, Title = "Rounding decision",
            BodyMd = "Tax is rounded per line item (half-up, 2 dp) to match the accounting export.", Tags = "decision" });
        await db.SaveChangesAsync();

        var inbox = TesterIssuesService.FilePath(northwind);
        Directory.CreateDirectory(Path.GetDirectoryName(inbox)!);
        if (!File.Exists(inbox))
            await File.WriteAllTextAsync(inbox, """
                # Tester issues — Northwind API (demo data)

                - [ ] [P1] Invoice total is off by one cent when a line has three items
                - [ ] [P2] Creating an invoice with quantity 0 returns 200 instead of 400
                - [x] [P3] README run command is wrong
                """);
    }
}
