using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Dispatches work to Claude Code / Codex CLIs as supervised child processes.
/// No paid per-token APIs — the CLIs run on the user's existing subscriptions.
/// Falls back to a clipboard "prompt pack" when a CLI is unavailable.
///
/// Agent mode (<c>Jarvis:Agents:Mode</c>):
///   <c>demo</c> (public-edition default) — every dispatch launches the scripted
///               <c>jarvis-demo-agent</c> adapter instead of a real CLI. Same process
///               supervision, streaming, transcript and RESULT parsing; no model, no edits.
///   <c>cli</c>  — the real <c>claude</c> / <c>codex</c> executables found on PATH.
/// </summary>
public class AgentService : IDisposable
{
    private const string ResultContract = """

        When you are completely finished, end your reply with exactly this fenced block (valid JSON):
        ```
        RESULT: {"summary": "<one short paragraph of what you did/found>", "changes": ["<file or area>"], "followUps": ["<task for the user>"], "risks": ["<risk>"]}
        ```
        """;

    private readonly IServiceScopeFactory _scopes;
    private readonly IEventBus _bus;
    private readonly ILogger<AgentService> _log;
    private readonly ConcurrentDictionary<string, Process> _running = new();
    private readonly IDisposable _stopSub;

    public AgentService(IServiceScopeFactory scopes, IEventBus bus, ILogger<AgentService> log, IConfiguration config)
    {
        _scopes = scopes; _bus = bus; _log = log;
        Mode = string.Equals(config["Jarvis:Agents:Mode"], "cli", StringComparison.OrdinalIgnoreCase) ? "cli" : "demo";
        _stopSub = bus.Subscribe(e =>
        {
            if (e.Kind == "system.stop") KillAll();
            return Task.CompletedTask;
        });
    }

    /// <summary>"demo" (scripted adapter) or "cli" (real Claude Code / Codex).</summary>
    public string Mode { get; }
    public bool ClaudeAvailable => ResolveAgent("claude") is not null;
    public bool CodexAvailable => ResolveAgent("codex") is not null;
    public int RunningCount => _running.Count;

    /// <summary>Executable (+ leading arguments) to launch for an agent in the current mode.</summary>
    internal (string Exe, string[] PrefixArgs)? ResolveAgent(string agent)
    {
        if (Mode == "cli")
            return FindOnPath(agent) is { } exe ? (exe, []) : null;

        // Demo mode: the adapter is built next to the kernel (project reference).
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "jarvis-demo-agent.exe" : "jarvis-demo-agent");
        if (File.Exists(apphost)) return (apphost, []);
        var dll = Path.Combine(dir, "jarvis-demo-agent.dll");
        if (File.Exists(dll) && FindOnPath("dotnet") is { } dotnet) return (dotnet, [dll]);
        return null;
    }

    /// <summary>Queue an agent run and return immediately with what JARVIS should say.
    /// readonlyAllowedTools: explicit tool allowlist for read-only runs that need more than plan mode
    /// (e.g. MCP tools like Jira) — used instead of --permission-mode plan.</summary>
    public async Task<(AgentRun Run, string Spoken)> DispatchAsync(
        string agent, string taskPrompt, string template, Project? project, Repo? repo, bool edit,
        string? readonlyAllowedTools = null, string? resumeSessionId = null)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();

        var target = ResolveAgent(agent);
        var run = new AgentRun
        {
            Agent = agent,
            ProjectId = project?.Id,
            RepoPath = repo?.Path,
            Mode = edit ? "edit" : "readonly",
            Prompt = taskPrompt + ResultContract,
            PromptTemplate = template,
            Status = target is null ? "failed" : "running",
            StartedAt = DateTime.UtcNow,
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        if (target is null)
        {
            var packPath = await WritePromptPackAsync(run);
            run.ResultSummary = $"{agent} CLI not available — prompt pack written";
            run.Status = "failed";
            await db.SaveChangesAsync();
            return (run, $"The {agent} CLI isn't available. I wrote the full prompt to the outbox and copied it " +
                         $"to your clipboard — paste it into {(agent == "codex" ? "ChatGPT" : "Claude")} manually.");
        }

        _ = Task.Run(() => RunProcessAsync(run.Id, target.Value.Exe, target.Value.PrefixArgs, readonlyAllowedTools, resumeSessionId));
        var where = project?.Name ?? repo?.Name ?? "the workspace";
        _bus.Publish(new JarvisEvent("agent.queued", new { runId = run.Id, agent, template, project = project?.Slug, mode = run.Mode, adapter = Mode }, "agent"));
        var verb = resumeSessionId is null ? "Dispatching" : "Resuming";
        var demoNote = Mode == "demo" ? " (demo adapter)" : "";
        return (run, $"{verb} {Cap(agent)}{demoNote} on {where} in {(edit ? "edit" : "read-only")} mode. I'll announce the result.");
    }

    /// <summary>
    /// Continue a previous run as a conversation. Claude runs resume their CLI session
    /// (--resume session_id); runs without a session (codex, old rows) fall back to a fresh
    /// run with the parent's summary pasted in as context.
    /// </summary>
    public async Task<(AgentRun Run, string Spoken)> DispatchFollowUpAsync(
        AgentRun parent, string text, string? readonlyAllowedTools = null, bool? editOverride = null)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var project = parent.ProjectId is null ? null : await db.Projects.FindAsync(parent.ProjectId);
        var repo = parent.RepoPath is null ? null
            : await db.Repos.FirstOrDefaultAsync(r => r.Path == parent.RepoPath);

        var edit = editOverride ?? parent.Mode == "edit";
        var canResume = parent.Agent == "claude" && !string.IsNullOrEmpty(parent.SessionId);
        var prompt = canResume
            ? $"Follow-up from the user: {text}"
            : $"""
              Earlier you worked on this task and reported: {parent.ResultSummary}

              Follow-up from the user: {text}
              """;
        return await DispatchAsync(parent.Agent, prompt, $"{parent.PromptTemplate}-followup",
            project, repo, edit, readonlyAllowedTools, canResume ? parent.SessionId : null);
    }

    private async Task RunProcessAsync(string runId, string exe, string[] prefixArgs, string? readonlyAllowedTools = null,
        string? resumeSessionId = null)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var run = await db.AgentRuns.FirstAsync(r => r.Id == runId);
        var project = run.ProjectId is null ? null : await db.Projects.FindAsync(run.ProjectId);

        var brainDir = Path.Combine(JarvisPaths.Brains, project?.Slug ?? "general", "agent-runs");
        Directory.CreateDirectory(brainDir);
        var transcriptPath = Path.Combine(brainDir, $"{run.Id}.jsonl");
        run.TranscriptPath = transcriptPath;
        await db.SaveChangesAsync();

        var resultText = new StringBuilder();
        var lastAssistantText = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = run.RepoPath is not null && Directory.Exists(run.RepoPath) ? run.RepoPath : JarvisPaths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
            // The prompt travels on STDIN, never on the command line: it contains user-derived
            // text, and when the CLI resolves to a .cmd shim (npm installs) Windows routes the
            // command line through cmd.exe, where quotes and metacharacters are re-interpreted.
            if (run.Agent == "claude")
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("stream-json");
                psi.ArgumentList.Add("--verbose");
                if (resumeSessionId is not null)
                {
                    psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(resumeSessionId);
                }
                if (run.Mode == "edit")
                {
                    psi.ArgumentList.Add("--permission-mode"); psi.ArgumentList.Add("acceptEdits");
                    psi.ArgumentList.Add("--allowedTools");
                    psi.ArgumentList.Add("Read,Edit,Write,Glob,Grep,Bash(dotnet build:*),Bash(dotnet test:*),Bash(git status:*),Bash(git diff:*),Bash(git log:*),Bash(npm test:*),Bash(npx playwright test:*)");
                }
                else if (readonlyAllowedTools is not null)
                {
                    psi.ArgumentList.Add("--allowedTools");
                    psi.ArgumentList.Add(readonlyAllowedTools);
                }
                else
                {
                    psi.ArgumentList.Add("--permission-mode"); psi.ArgumentList.Add("plan");
                }
            }
            else // codex
            {
                psi.ArgumentList.Add("exec");
                psi.ArgumentList.Add("--sandbox");
                psi.ArgumentList.Add(run.Mode == "edit" ? "workspace-write" : "read-only");
                psi.ArgumentList.Add("-");   // read the prompt from stdin
            }

            using var proc = Process.Start(psi)!;
            _running[run.Id] = proc;
            await proc.StandardInput.WriteAsync(run.Prompt);
            proc.StandardInput.Close();

            await using var transcript = new StreamWriter(transcriptPath, append: false, Encoding.UTF8);
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                await transcript.WriteLineAsync(line);
                if (run.Agent == "claude") ParseClaudeLine(line, run.Id, lastAssistantText, resultText,
                    sid => run.SessionId ??= sid);
                else
                {
                    resultText.AppendLine(line);
                    if (line.Trim().Length > 0)
                        _bus.Publish(new JarvisEvent("agent.progress", new { runId = run.Id, text = Truncate(line, 180) }, "agent"));
                }
            }
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (resultText.Length == 0 && stderr.Length > 0) resultText.Append(stderr);
            run.ExitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Agent run {Id} crashed", runId);
            resultText.AppendLine($"[agent process error] {ex.Message}");
            run.ExitCode = -1;
        }
        finally
        {
            _running.TryRemove(run.Id, out _);
        }

        var finalText = resultText.Length > 0 ? resultText.ToString() : lastAssistantText.ToString();
        var parsed = TryParseResultBlock(finalText);
        run.Status = run.ExitCode == 0 ? "done" : "failed";
        run.EndedAt = DateTime.UtcNow;
        run.ResultJson = parsed?.GetRawText() ?? "";
        run.ResultSummary = parsed is not null && parsed.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()! : Truncate(finalText.Trim(), 400);

        // RESULT.followUps become real tasks (Source=agent)
        var followUpIds = new List<string>();
        if (run.Status == "done" && parsed is not null && parsed.Value.TryGetProperty("followUps", out var fu) && fu.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fu.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(f.GetString())) continue;
                var t = new TaskItem { Title = f.GetString()!, ProjectId = run.ProjectId, Source = "agent" };
                db.Tasks.Add(t);
                followUpIds.Add(t.Id);
            }
        }
        run.FollowUpTaskIds = JsonSerializer.Serialize(followUpIds);
        await db.SaveChangesAsync();

        var mins = run.StartedAt is { } st ? (DateTime.UtcNow - st).TotalMinutes : 0;
        var spoken = run.Status == "done"
            ? $"{Cap(run.Agent)} finished after {(mins < 1 ? "under a minute" : $"{mins:F0} minutes")}. {run.ResultSummary}" +
              (followUpIds.Count > 0 ? $" {followUpIds.Count} follow-up task{(followUpIds.Count == 1 ? "" : "s")} filed." : "")
            : $"{Cap(run.Agent)} run failed: {Truncate(run.ResultSummary, 160)}";
        _bus.Publish(new JarvisEvent("agent.done", new { runId = run.Id, agent = run.Agent, status = run.Status, spoken, summary = run.ResultSummary }, "agent"));
    }

    private void ParseClaudeLine(string line, string runId, StringBuilder assistantText, StringBuilder resultText,
        Action<string>? onSessionId = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "system" && onSessionId is not null
                && doc.RootElement.TryGetProperty("session_id", out var sid)
                && sid.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(sid.GetString()))
            {
                onSessionId(sid.GetString()!);
            }
            if (type == "assistant" && doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                        && block.TryGetProperty("text", out var txt))
                    {
                        var text = txt.GetString() ?? "";
                        assistantText.AppendLine(text);
                        _bus.Publish(new JarvisEvent("agent.progress", new { runId, text = Truncate(text, 180) }, "agent"));
                    }
                    else if (block.TryGetProperty("type", out var bt2) && bt2.GetString() == "tool_use"
                        && block.TryGetProperty("name", out var toolName))
                    {
                        _bus.Publish(new JarvisEvent("agent.progress", new { runId, text = $"[tool] {toolName.GetString()}" }, "agent"));
                    }
                }
            }
            else if (type == "result" && doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
            {
                resultText.Append(res.GetString());
            }
        }
        catch (JsonException) { /* non-JSON line — ignore */ }
    }

    /// <summary>Find the last RESULT: {...} block by balanced-brace extraction.</summary>
    private static JsonElement? TryParseResultBlock(string text)
    {
        var idx = text.LastIndexOf("RESULT", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = text.IndexOf('{', idx);
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                try { return JsonDocument.Parse(text[start..(i + 1)]).RootElement.Clone(); }
                catch (JsonException) { return null; }
            }
        }
        return null;
    }

    private static async Task<string> WritePromptPackAsync(AgentRun run)
    {
        var outbox = Path.Combine(JarvisPaths.Data, "outbox");
        Directory.CreateDirectory(outbox);
        var path = Path.Combine(outbox, $"{run.Agent}-{run.Id}.md");
        await File.WriteAllTextAsync(path, run.Prompt);
        try
        {
            var psi = new ProcessStartInfo("cmd", $"/c clip < \"{path}\"") { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { /* clipboard is best-effort */ }
        return path;
    }

    public void KillAll()
    {
        foreach (var (id, proc) in _running)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            _bus.Publish(new JarvisEvent("agent.done", new { runId = id, status = "stopped", spoken = "Agent run stopped." }, "system"));
        }
        _running.Clear();
    }

    public static string? FindOnPath(string name)
    {
        var exts = new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public void Dispose() => _stopSub.Dispose();
}
