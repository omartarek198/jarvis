using Jarvis.Core;

namespace Jarvis.Kernel.Services;

public record TesterIssue(string Severity, string Text, bool Closed);

/// <summary>
/// Tester issues live as a human-editable markdown checklist per project:
/// data/brains/&lt;slug&gt;/testers/issues.md with lines like "- [ ] [P1] Booking page crashes on save".
/// The user (or a Claude run pulling from Jira) drops issues in; JARVIS reads it fresh on every query.
/// </summary>
public class TesterIssuesService
{
    public static string FilePath(Project p)
        => Path.Combine(string.IsNullOrEmpty(p.BrainPath) ? Path.Combine(JarvisPaths.Brains, p.Slug) : p.BrainPath,
            "testers", "issues.md");

    public static async Task<List<TesterIssue>> ReadAsync(Project p)
    {
        var path = FilePath(p);
        if (!File.Exists(path)) return [];
        var issues = new List<TesterIssue>();
        foreach (var raw in await File.ReadAllLinesAsync(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("- [")) continue;
            var closed = line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
            var rest = line[5..].Trim();
            var severity = "P3";
            var m = System.Text.RegularExpressions.Regex.Match(rest, @"^\[(?<sev>P[0-4])\]\s*(?<text>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var text = rest;
            if (m.Success) { severity = m.Groups["sev"].Value.ToUpperInvariant(); text = m.Groups["text"].Value; }
            issues.Add(new TesterIssue(severity, text, closed));
        }
        return issues;
    }

    public static async Task EnsureInboxExistsAsync(Project p)
    {
        var path = FilePath(p);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"""
            # {p.Name} — tester issue inbox

            Drop tester findings here as checklist items. JARVIS reads this file live.
            Format: `- [ ] [P1] description` (P1 = critical … P4 = cosmetic). Mark fixed with `- [x]`.

            """);
    }
}
