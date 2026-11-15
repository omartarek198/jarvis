using System.Diagnostics;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

public record GitStatus(string Branch, bool Clean, int DirtyCount, string[] DirtyFiles, string LastCommit);

public class GitService(ILogger<GitService> log)
{
    public async Task<(int Code, string Output)> RunAsync(string repoPath, string args)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{repoPath}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (-1, "git timed out"); }
        if (p.ExitCode != 0) log.LogWarning("git {Args} in {Path} -> {Err}", args, repoPath, stderr.Trim());
        return (p.ExitCode, p.ExitCode == 0 ? stdout : stderr);
    }

    public async Task<GitStatus?> StatusAsync(string repoPath)
    {
        var (bc, branch) = await RunAsync(repoPath, "rev-parse --abbrev-ref HEAD");
        if (bc != 0) return null;
        var (_, porcelain) = await RunAsync(repoPath, "status --porcelain");
        var dirty = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var (_, last) = await RunAsync(repoPath, "log -1 --pretty=format:\"%h %s (%cr)\"");
        return new(branch.Trim(), dirty.Length == 0, dirty.Length,
            [.. dirty.Take(10).Select(d => d.Trim())], last.Trim());
    }

    public async Task<string[]> RecentCommitsAsync(string repoPath, int n)
    {
        var (c, output) = await RunAsync(repoPath, $"log -{n} --pretty=format:\"%h %s (%an, %cr)\"");
        return c == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    public async Task<string[]> ChangedTodayAsync(string repoPath)
    {
        var (c, output) = await RunAsync(repoPath, "log --since=midnight --pretty=format:\"%h %s (%cr)\"");
        return c == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    public async Task<string[]> BranchesAsync(string repoPath)
    {
        var (c, output) = await RunAsync(repoPath, "branch --format=\"%(refname:short)|%(committerdate:relative)\"");
        return c == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }
}

/// <summary>Resolves spoken project/repo hints against the registry ("northwind" → Northwind API).</summary>
public class EntityResolver(JarvisDb db, DialogContext dialog)
{
    public async Task<Project?> ResolveProjectAsync(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        hint = hint.Trim().ToLowerInvariant().TrimEnd('?', '.', '!');
        var projects = await db.Projects.AsNoTracking().ToListAsync();
        var match = projects.FirstOrDefault(p => p.Slug.Equals(hint, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.Name.Equals(hint, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                   .Any(a => a.Equals(hint, StringComparison.OrdinalIgnoreCase)))
            ?? projects.FirstOrDefault(p => p.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                                         || hint.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) dialog.RecordProject(match);   // feeds "and for X" / "close it" follow-ups
        return match;
    }

    public async Task<Repo?> ResolveRepoAsync(string? hint)
    {
        var repos = await db.Repos.AsNoTracking().ToListAsync();
        if (repos.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(hint))
            return repos.Count == 1 ? repos[0] : repos.FirstOrDefault(r => r.IsPrimary);

        var project = await ResolveProjectAsync(hint);
        if (project is not null)
        {
            var byProject = repos.Where(r => r.ProjectId == project.Id).ToList();
            if (byProject.Count > 0) return byProject.FirstOrDefault(r => r.IsPrimary) ?? byProject[0];
        }
        hint = hint.Trim().TrimEnd('?', '.', '!');
        return repos.FirstOrDefault(r => r.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }
}
