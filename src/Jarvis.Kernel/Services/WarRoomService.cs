using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

public class WarRoomService(JarvisDb db, GitService git, Prioritizer prioritizer)
{
    public async Task<object> BuildAsync(Project p)
    {
        var repo = await db.Repos.AsNoTracking().Where(r => r.ProjectId == p.Id)
            .OrderByDescending(r => r.IsPrimary).FirstOrDefaultAsync();
        GitStatus? gitStatus = null;
        string[] recentCommits = [];
        if (repo is not null && Directory.Exists(repo.Path))
        {
            gitStatus = await git.StatusAsync(repo.Path);
            recentCommits = await git.RecentCommitsAsync(repo.Path, 5);
        }

        var issues = await TesterIssuesService.ReadAsync(p);
        var openIssues = issues.Where(i => !i.Closed).ToList();
        var p1Count = openIssues.Count(i => i.Severity is "P1");

        var lastPlaywright = await db.PlaywrightRuns.AsNoTracking().Where(r => r.ProjectId == p.Id)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
        var agentRuns = await db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == p.Id)
            .OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();
        var openTasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == p.Id && (t.Status == "open" || t.Status == "doing" || t.Status == "blocked")).ToListAsync();
        var openCommitments = await db.Commitments.AsNoTracking()
            .Where(c => c.ProjectId == p.Id && c.Status == "open").ToListAsync();
        var overdue = openCommitments.Count(c => c.DueDate is { } d && d < DateTime.Now);
        var decisions = await db.Decisions.AsNoTracking()
            .Where(d => d.ProjectId == p.Id && d.Status == "open").ToListAsync();

        // Readiness: start at 100, subtract weighted pain
        var score = 100;
        score -= Math.Min(p1Count * 15, 45);
        score -= Math.Min(openIssues.Count(i => i.Severity == "P2") * 5, 15);
        if (gitStatus is { Clean: false }) score -= 10;
        if (lastPlaywright is not null) score -= Math.Min(lastPlaywright.Failed * 10, 30);
        score -= Math.Min(overdue * 5, 20);
        score = Math.Max(score, 0);

        var ranked = await prioritizer.RankAsync(p.Id, 1);
        var suggested =
            p1Count > 0 ? $"Fix the {p1Count} P1 tester issue{(p1Count == 1 ? "" : "s")} — say: ask Claude to fix high severity tester issues" :
            lastPlaywright is { Failed: > 0 } ? $"Fix {lastPlaywright.Failed} failing Playwright test{(lastPlaywright.Failed == 1 ? "" : "s")}" :
            gitStatus is { Clean: false } ? $"Commit {gitStatus.DirtyCount} dirty files on {gitStatus.Branch}" :
            overdue > 0 ? $"Close the overdue commitment: {openCommitments.First(c => c.DueDate < DateTime.Now).Text}" :
            ranked.Count > 0 ? $"Work on: {ranked[0].Task.Title}" :
            "All clear — pick the next strategic move.";

        return new
        {
            project = new { p.Name, p.Slug, p.Summary, p.Status },
            repo = repo is null ? null : new { repo.Name, repo.Path },
            git = gitStatus is null ? null : new { gitStatus.Branch, gitStatus.Clean, gitStatus.DirtyCount, gitStatus.LastCommit },
            recentCommits,
            testerIssues = new { open = openIssues.Count, p1 = p1Count, items = openIssues.Take(10) },
            playwright = lastPlaywright is null ? null : new { lastPlaywright.Passed, lastPlaywright.Failed, lastPlaywright.Summary, at = lastPlaywright.EndedAt },
            agentRuns = agentRuns.Select(r => new { r.Agent, r.PromptTemplate, r.Status, r.ResultSummary, at = r.CreatedAt }),
            tasks = new { open = openTasks.Count(t => t.Status != "blocked"), blocked = openTasks.Count(t => t.Status == "blocked"), items = openTasks.Take(10) },
            commitments = new { open = openCommitments.Count, overdue, items = openCommitments.Take(10) },
            decisions = decisions.Take(5),
            readinessScore = score,
            suggestedAction = suggested,
        };
    }

    public async Task<string> SpokenAsync(Project p)
    {
        dynamic w = await BuildAsync(p);
        return $"{p.Name} war room: readiness {w.readinessScore} out of 100. " +
               $"{w.testerIssues.open} open tester issues{(w.testerIssues.p1 > 0 ? $", {w.testerIssues.p1} critical" : "")}. " +
               $"{w.tasks.open} open tasks, {w.commitments.open} commitments. " +
               $"Suggested action: {w.suggestedAction}";
    }
}
