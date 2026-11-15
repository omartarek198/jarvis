using System.Text;
using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class GitModule : IJarvisModule
{
    public string Name => "git";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "repo_clean",
            Description = "check whether a repo has uncommitted changes",
            Patterns = [@"^is (?<repo>.+?) clean\??$"],
            SlotHints = ["repo"],
            Handler = async ctx =>
            {
                var repo = await ctx.S<EntityResolver>().ResolveRepoAsync(ctx.Slot("repo"));
                if (repo is null) return new CommandResult(NoRepoMessage(ctx.Slot("repo")));
                var st = await ctx.S<GitService>().StatusAsync(repo.Path);
                if (st is null) return new CommandResult($"I couldn't read git status for {repo.Name}.");
                return new CommandResult(st.Clean
                    ? $"{repo.Name} is clean on branch {st.Branch}. Last commit: {st.LastCommit}."
                    : $"{repo.Name} has {st.DirtyCount} uncommitted file{(st.DirtyCount == 1 ? "" : "s")} on branch {st.Branch}.",
                    new { repo = repo.Name, status = st });
            }
        },
        new()
        {
            Name = "changed_today",
            Description = "summarize commits made today",
            Patterns = [@"^what changed today( in (?<repo>.+))?$"],
            SlotHints = ["repo"],
            Handler = async ctx =>
            {
                var git = ctx.S<GitService>();
                var db = ctx.S<JarvisDb>();
                var hint = ctx.Slot("repo");
                var repos = hint is null
                    ? await db.Repos.AsNoTracking().ToListAsync()
                    : [await ctx.S<EntityResolver>().ResolveRepoAsync(hint) ?? new Repo()];
                var parts = new List<string>();
                var data = new Dictionary<string, string[]>();
                foreach (var r in repos.Where(r => r.Path != "" && Directory.Exists(r.Path)))
                {
                    var commits = await git.ChangedTodayAsync(r.Path);
                    data[r.Name] = commits;
                    if (commits.Length > 0)
                        parts.Add($"{r.Name}: {commits.Length} commit{(commits.Length == 1 ? "" : "s")} — latest: {commits[0]}");
                }
                return new CommandResult(parts.Count == 0
                    ? "No commits in any registered repo today."
                    : string.Join(". ", parts) + ".", new { repos = data });
            }
        },
        new()
        {
            Name = "recent_commits",
            Description = "summarize the last N commits of a repo",
            Patterns = [@"^summari[sz]e( the)? last (?<n>\d+) commits( (in|of) (?<repo>.+))?$"],
            SlotHints = ["n", "repo"],
            Handler = async ctx =>
            {
                var repo = await ctx.S<EntityResolver>().ResolveRepoAsync(ctx.Slot("repo"));
                if (repo is null) return new CommandResult(NoRepoMessage(ctx.Slot("repo")));
                var n = int.TryParse(ctx.Slot("n"), out var parsed) ? Math.Clamp(parsed, 1, 20) : 5;
                var commits = await ctx.S<GitService>().RecentCommitsAsync(repo.Path, n);
                if (commits.Length == 0) return new CommandResult($"{repo.Name} has no commits yet.");
                return new CommandResult($"Last {commits.Length} commits in {repo.Name}: " +
                    string.Join(". ", commits) + ".", new { commits });
            }
        },
        new()
        {
            Name = "list_branches",
            Description = "list branches of a repo",
            Patterns = [@"^(what branches( exist)?|list branches)( (in|on|for) (?<repo>.+))?$"],
            SlotHints = ["repo"],
            Handler = async ctx =>
            {
                var repo = await ctx.S<EntityResolver>().ResolveRepoAsync(ctx.Slot("repo"));
                if (repo is null) return new CommandResult(NoRepoMessage(ctx.Slot("repo")));
                var branches = await ctx.S<GitService>().BranchesAsync(repo.Path);
                if (branches.Length == 0) return new CommandResult($"No branches found in {repo.Name}.");
                var spoken = branches.Take(8).Select(b =>
                {
                    var parts = b.Split('|');
                    return parts.Length == 2 ? $"{parts[0]} (updated {parts[1]})" : b;
                });
                return new CommandResult($"{repo.Name} has {branches.Length} branch{(branches.Length == 1 ? "" : "es")}: " +
                    string.Join(", ", spoken) + ".", new { branches });
            }
        },
        new()
        {
            Name = "register_repo",
            Description = "register a local repo path under a project",
            Patterns = [@"^register repo (?<path>.+?) (for|as|under) (?<project>.+)$"],
            SlotHints = ["path", "project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var path = ctx.Slot("path")?.Trim('"') ?? "";
                if (!Directory.Exists(path)) return new CommandResult($"That path doesn't exist: {path}.");
                if (!Directory.Exists(Path.Combine(path, ".git")))
                    return new CommandResult($"{path} exists but isn't a git repository.");
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                if (project is null) return new CommandResult($"I don't know a project called {ctx.Slot("project")}.");
                var db = ctx.S<JarvisDb>();
                var isFirst = !await db.Repos.AnyAsync(r => r.ProjectId == project.Id);
                db.Repos.Add(new Repo { ProjectId = project.Id, Path = path, Name = new DirectoryInfo(path).Name, IsPrimary = isFirst });
                await db.SaveChangesAsync();
                return new CommandResult($"Registered {new DirectoryInfo(path).Name} under {project.Name}.");
            }
        },
        new()
        {
            Name = "handoff_report",
            Description = "prepare a handoff report for a repo and save it as a note",
            Patterns = [@"^prepare a handoff( report)?( for (?<repo>.+))?$"],
            SlotHints = ["repo"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var repo = await ctx.S<EntityResolver>().ResolveRepoAsync(ctx.Slot("repo"));
                if (repo is null) return new CommandResult(NoRepoMessage(ctx.Slot("repo")));
                var git = ctx.S<GitService>();
                var st = await git.StatusAsync(repo.Path);
                var commits = await git.RecentCommitsAsync(repo.Path, 5);
                var md = new StringBuilder($"# Handoff — {repo.Name} — {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
                if (st is not null)
                {
                    md.AppendLine($"- Branch: `{st.Branch}` — {(st.Clean ? "clean" : $"{st.DirtyCount} dirty files")}");
                    foreach (var f in st.DirtyFiles) md.AppendLine($"  - {f}");
                }
                md.AppendLine("\n## Recent commits");
                foreach (var c in commits) md.AppendLine($"- {c}");

                var db = ctx.S<JarvisDb>();
                var note = new Note { ProjectId = repo.ProjectId, Title = $"Handoff {repo.Name} {DateTime.Now:MM-dd}", BodyMd = md.ToString() };
                db.Notes.Add(note);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("note", note.Id, note.Title, note.BodyMd);
                return new CommandResult($"Handoff report for {repo.Name} saved. Branch {st?.Branch}, " +
                    $"{(st?.Clean == true ? "working tree clean" : $"{st?.DirtyCount} uncommitted files")}, " +
                    $"{commits.Length} recent commits captured.", new { markdown = md.ToString() });
            }
        },
    ];

    private static string NoRepoMessage(string? hint) => hint is null
        ? "No repos are registered yet. Say: register repo, then the full path, then 'for', then the project name."
        : $"I couldn't match '{hint}' to a registered repo. Say 'register repo <path> for <project>' first.";
}

public class ProjectsModule : IJarvisModule
{
    public string Name => "projects";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "list_projects",
            Description = "list all projects",
            Patterns = [@"^(what projects do i have|list( my)? projects|show projects)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var projects = await db.Projects.AsNoTracking().Where(p => p.Status == "active").ToListAsync();
                return new CommandResult($"{projects.Count} active projects: " +
                    string.Join(", ", projects.Select(p => p.Name)) + ".", new { projects });
            }
        },
        new()
        {
            Name = "project_status",
            Description = "synthesize the live status of a project (tasks, commitments, git, agents)",
            Patterns =
            [
                @"^what('s| is) (happening|going on|the status) (with|in|on|of) (?<project>.+)$",
                @"^status of (?<project>.+)$",
            ],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                if (project is null)
                    return new CommandResult($"I don't know a project called {ctx.Slot("project")}.") { Unhandled = true };
                var spoken = await ctx.S<ProjectStatusService>().SynthesizeAsync(project);
                return new CommandResult(spoken, new { project = project.Slug });
            }
        },
        new()
        {
            Name = "describe_project",
            Description = "describe what a project is",
            Patterns = [@"^(what is|tell me about|describe) (?<project>.+)$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                if (project is null) return new CommandResult("") { Unhandled = true };
                var goals = string.IsNullOrWhiteSpace(project.Goals) ? "" : $" Goals: {project.Goals}.";
                return new CommandResult($"{project.Name}: {project.Summary}{goals}", new { project });
            }
        },
        new()
        {
            Name = "what_blocked",
            Description = "list blocked tasks",
            Patterns = [@"^what('s| is) blocked( (on|in) (?<project>.+))?$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                var q = db.Tasks.AsNoTracking().Where(t => t.Status == "blocked");
                if (project is not null) q = q.Where(t => t.ProjectId == project.Id);
                var blocked = await q.ToListAsync();
                if (blocked.Count == 0) return new CommandResult("Nothing is marked blocked.");
                return new CommandResult($"{blocked.Count} blocked: " + string.Join(". ",
                    blocked.Take(5).Select(t => t.Title + (t.BlockedReason != "" ? $" — {t.BlockedReason}" : ""))) + ".",
                    new { blocked });
            }
        },
    ];
}
