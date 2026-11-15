using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

public class PlaywrightService(IServiceScopeFactory scopes, IEventBus bus, ILogger<PlaywrightService> log)
{
    public static bool HasConfig(Repo repo) =>
        repo.PlaywrightConfig is not null
        || File.Exists(Path.Combine(repo.Path, "playwright.config.ts"))
        || File.Exists(Path.Combine(repo.Path, "playwright.config.js"));

    /// <summary>Run a Playwright suite and return immediately; result is announced via the event bus.</summary>
    public (string Spoken, string RunId) Start(Project project, Repo repo, string suite)
    {
        var run = new PlaywrightRun { ProjectId = project.Id, RepoPath = repo.Path, Suite = suite, StartedAt = DateTime.UtcNow };
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
            db.PlaywrightRuns.Add(run);
            db.SaveChanges();
        }
        bus.Publish(new JarvisEvent("playwright.started", new { runId = run.Id, project = project.Slug, suite }, "system"));
        _ = Task.Run(() => ExecuteAsync(run.Id));
        return ($"Running the {project.Name} Playwright {suite}. I'll announce the results.", run.Id);
    }

    private async Task ExecuteAsync(string runId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var run = await db.PlaywrightRuns.FirstAsync(r => r.Id == runId);
        var stdout = new StringBuilder();
        try
        {
            var npx = AgentService.FindOnPath("npx") ?? "npx";
            var psi = new ProcessStartInfo(npx)
            {
                WorkingDirectory = run.RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("playwright"); psi.ArgumentList.Add("test");
            psi.ArgumentList.Add("--reporter=json");
            psi.Environment["CI"] = "1";

            using var proc = Process.Start(psi)!;
            var readOut = proc.StandardOutput.ReadToEndAsync();
            var readErr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15 * 60 * 1000)) { try { proc.Kill(true); } catch { } }
            stdout.Append(await readOut);
            var stderr = await readErr;

            var (passed, failed, skipped, failures) = ParseReport(stdout.ToString());
            run.Passed = passed; run.Failed = failed; run.Skipped = skipped;
            run.FailuresJson = JsonSerializer.Serialize(failures);
            run.Summary = failed == 0
                ? $"{passed} passed, {skipped} skipped — all green."
                : $"{passed} passed, {failed} failed: " + string.Join("; ", failures.Take(3).Select(f => f.Spec));
            if (passed == 0 && failed == 0)
                run.Summary = "No tests ran — " + Truncate(stderr.Length > 0 ? stderr : stdout.ToString(), 200);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Playwright run {Id} crashed", runId);
            run.Summary = $"Playwright run error: {ex.Message}";
        }
        run.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var spoken = $"Playwright finished: {run.Summary}";
        bus.Publish(new JarvisEvent("playwright.done",
            new { runId = run.Id, run.Passed, run.Failed, run.Skipped, spoken, summary = run.Summary }, "system"));
    }

    public record Failure(string Spec, string Error);

    private static (int Passed, int Failed, int Skipped, List<Failure> Failures) ParseReport(string json)
    {
        var failures = new List<Failure>();
        try
        {
            var start = json.IndexOf('{');
            if (start < 0) return (0, 0, 0, failures);
            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            int passed = 0, failed = 0, skipped = 0;
            if (root.TryGetProperty("stats", out var stats))
            {
                passed = stats.TryGetProperty("expected", out var e) ? e.GetInt32() : 0;
                failed = stats.TryGetProperty("unexpected", out var u) ? u.GetInt32() : 0;
                skipped = stats.TryGetProperty("skipped", out var s) ? s.GetInt32() : 0;
            }
            if (root.TryGetProperty("suites", out var suites)) WalkSuites(suites, "", failures);
            return (passed, failed, skipped, failures);
        }
        catch (JsonException) { return (0, 0, 0, failures); }
    }

    private static void WalkSuites(JsonElement suites, string prefix, List<Failure> failures)
    {
        if (suites.ValueKind != JsonValueKind.Array) return;
        foreach (var suite in suites.EnumerateArray())
        {
            var title = suite.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var path = string.IsNullOrEmpty(title) ? prefix : $"{prefix}{title} › ";
            if (suite.TryGetProperty("suites", out var inner)) WalkSuites(inner, path, failures);
            if (!suite.TryGetProperty("specs", out var specs) || specs.ValueKind != JsonValueKind.Array) continue;
            foreach (var spec in specs.EnumerateArray())
            {
                var ok = spec.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (ok) continue;
                var specTitle = spec.TryGetProperty("title", out var st) ? st.GetString() ?? "?" : "?";
                var error = "";
                if (spec.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Array)
                    foreach (var test in tests.EnumerateArray())
                        if (test.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                            foreach (var r in results.EnumerateArray())
                                if (r.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                                { error = Truncate(msg.GetString() ?? "", 300); break; }
                failures.Add(new Failure(path + specTitle, error));
            }
        }
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
