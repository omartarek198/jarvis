// DEMO ADAPTER: a deterministic stand-in for the Claude Code and Codex CLIs.
//
// JARVIS's AgentService launches coding agents as child processes and parses their stdout.
// In demo mode it launches this program instead, with the same argument shapes it would pass to
// the real tools:
//
//   claude-style:  -p <prompt> --output-format stream-json --verbose [--permission-mode m]
//                  [--allowedTools list] [--resume session-id]      (prompt may also come on stdin)
//   codex-style:   exec --sandbox <mode> [<prompt> | -]              (prompt may also come on stdin)
//
// It answers in the matching output format (Claude Code stream-json lines, or plain codex-style
// text), so the kernel's real supervision, streaming, transcript and RESULT-parsing code runs
// unchanged. The "work" it reports is a real, read-only scan of the working directory (file
// counts and TODO/FIXME markers). It never calls a language model and never writes files.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);

var codexStyle = args.Length > 0 && args[0] == "exec";
string? prompt = null, permissionMode = null, sandbox = null, resume = null;
for (var i = codexStyle ? 1 : 0; i < args.Length; i++)
{
    var a = args[i];
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (a)
    {
        case "-p" or "--print":
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) prompt = Next();
            break;
        case "--output-format" or "--allowedTools" or "--allowed-tools": Next(); break;
        case "--permission-mode": permissionMode = Next(); break;
        case "--sandbox": sandbox = Next(); break;
        case "--resume": resume = Next(); break;
        case "--verbose": break;
        case "-": break;
        default:
            if (codexStyle && !a.StartsWith('-')) prompt = a;
            break;
    }
}
if (string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected)
    prompt = Console.In.ReadToEnd();
prompt ??= "";

var fast = Environment.GetEnvironmentVariable("JARVIS_DEMO_AGENT_FAST") == "1";
var cwd = Directory.GetCurrentDirectory();
var sessionId = resume ?? $"demo-{Guid.NewGuid():N}"[..21];
var started = DateTime.UtcNow;
var editRequested = permissionMode == "acceptEdits" || sandbox == "workspace-write";
var task = FirstLine(prompt);

// ---- the actual (read-only) inspection --------------------------------------------------------
var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".git", "bin", "obj", "node_modules", ".venv", "venv", "__pycache__", ".vs", "dist" };
var files = EnumerateFiles(cwd, skipDirs).Take(5000).ToList();
var byExt = files.GroupBy(f => string.IsNullOrEmpty(Path.GetExtension(f)) ? "(none)" : Path.GetExtension(f).ToLowerInvariant())
    .OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Count()} {g.Key}").ToList();
var markers = new List<(string File, int Line, string Text)>();
var markerRx = new Regex(@"\b(TODO|FIXME|HACK)\b[:\s-]*(?<t>.*)$", RegexOptions.IgnoreCase);
foreach (var f in files.Where(IsText))
{
    string[] lines;
    try { lines = File.ReadAllLines(f); } catch { continue; }
    for (var n = 0; n < lines.Length && markers.Count < 10; n++)
    {
        var m = markerRx.Match(lines[n]);
        if (m.Success) markers.Add((Rel(f), n + 1, m.Groups["t"].Value.Trim().TrimEnd('*', '/', ' ')));
    }
    if (markers.Count >= 10) break;
}
var readme = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase));

var summary = new StringBuilder();
summary.Append($"Demo agent (scripted, no model) inspected {files.Count} files in '{new DirectoryInfo(cwd).Name}'");
summary.Append(byExt.Count > 0 ? $" ({string.Join(", ", byExt)})." : ".");
summary.Append(markers.Count == 0
    ? " No TODO/FIXME markers found."
    : $" Found {markers.Count} TODO/FIXME marker{(markers.Count == 1 ? "" : "s")}; the first is in {markers[0].File} line {markers[0].Line}.");
if (editRequested)
    summary.Append(" Edit mode was requested; the demo adapter never edits files.");

var result = new
{
    summary = summary.ToString(),
    changes = Array.Empty<string>(),
    followUps = markers.Take(3).Select(m => $"Resolve {m.File}:{m.Line} - {Truncate(m.Text, 80)}").ToArray(),
    risks = new[] { "Demo adapter output: deterministic file scan, no language model was involved." },
};
var resultBlock = "RESULT: " + JsonSerializer.Serialize(result);
var finalText = $"Here is what I found for: \"{Truncate(task, 120)}\".\n\n{summary}\n\n```\n{resultBlock}\n```";

// ---- emit in the format the caller expects ----------------------------------------------------
if (codexStyle)
{
    Say($"[demo-agent] codex-style run, sandbox={sandbox ?? "read-only"}, cwd={new DirectoryInfo(cwd).Name}");
    Pause(400);
    Say($"[demo-agent] scanning {files.Count} files");
    Pause(600);
    Say(markers.Count == 0 ? "[demo-agent] no TODO/FIXME markers" : $"[demo-agent] {markers.Count} TODO/FIXME markers");
    Pause(500);
    Say(finalText);
    return 0;
}

Emit(new { type = "system", subtype = "init", session_id = sessionId, cwd, model = "jarvis-demo-agent",
           permissionMode = permissionMode ?? "default", tools = new[] { "Glob", "Grep", "Read" } });
Pause(300);
AssistantText(resume is null
    ? "I'll start by looking at the layout of the working directory."
    : "Continuing the previous session with your follow-up.");
Pause(500);
ToolUse("toolu_demo_1", "Glob", new { pattern = "**/*" }, $"{files.Count} files");
Pause(700);
ToolUse("toolu_demo_2", "Grep", new { pattern = "TODO|FIXME|HACK" },
    markers.Count == 0 ? "no matches" : string.Join("\n", markers.Select(m => $"{m.File}:{m.Line}: {m.Text}")));
Pause(700);
if (readme is not null)
{
    ToolUse("toolu_demo_3", "Read", new { file_path = Rel(readme) }, "(read)");
    Pause(500);
}
AssistantText(finalText);
Pause(200);
Emit(new { type = "result", subtype = "success", is_error = false, session_id = sessionId,
           num_turns = readme is null ? 3 : 4,
           duration_ms = (int)(DateTime.UtcNow - started).TotalMilliseconds, result = finalText });
return 0;

// ---- helpers -----------------------------------------------------------------------------------
void Emit(object o) { Console.WriteLine(JsonSerializer.Serialize(o)); Console.Out.Flush(); }
void Say(string s) { Console.WriteLine(s); Console.Out.Flush(); }
void Pause(int ms) { if (!fast) Thread.Sleep(ms); }
void AssistantText(string text) => Emit(new
{
    type = "assistant", session_id = sessionId,
    message = new { role = "assistant", content = new object[] { new { type = "text", text } } },
});
void ToolUse(string id, string name, object input, string output)
{
    Emit(new
    {
        type = "assistant", session_id = sessionId,
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
    });
    Emit(new
    {
        type = "user", session_id = sessionId,
        message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = output } } },
    });
}
string Rel(string path) => Path.GetRelativePath(cwd, path).Replace('\\', '/');

static IEnumerable<string> EnumerateFiles(string root, HashSet<string> skip)
{
    var stack = new Stack<string>([root]);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        string[] subdirs = [], here = [];
        try { subdirs = Directory.GetDirectories(dir); here = Directory.GetFiles(dir); } catch { /* unreadable */ }
        foreach (var f in here) yield return f;
        foreach (var d in subdirs.Where(d => !skip.Contains(Path.GetFileName(d)))) stack.Push(d);
    }
}

static bool IsText(string f) => Path.GetExtension(f).ToLowerInvariant() is
    ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".md" or ".json" or ".txt" or ".html" or ".css" or ".ps1" or ".sh";

static string FirstLine(string s)
{
    foreach (var line in s.Split('\n'))
        if (line.Trim().Length > 0) return line.Trim();
    return "";
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
