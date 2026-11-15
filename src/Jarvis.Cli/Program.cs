using System.Net.Http.Json;
using System.Text.Json;

// Flags: --json (machine-readable output, used by the Codex phone relay),
//        --source <tag> (audit trail provenance: cli | relay | test)
var json = false;
var source = "cli";
var words = new List<string>();
for (var a = 0; a < args.Length; a++)
{
    if (args[a] == "--json") json = true;
    else if (args[a] == "--source" && a + 1 < args.Length) source = args[++a];
    else words.Add(args[a]);
}

var baseUrl = Environment.GetEnvironmentVariable("JARVIS_URL") ?? "http://localhost:5588";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };

if (words.Count > 0)
{
    await SendAsync(string.Join(' ', words));
    return;
}

Console.WriteLine("JARVIS CLI — type commands, 'exit' to quit.");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(line)) continue;
    await SendAsync(line.Trim());
}

async Task SendAsync(string text)
{
    try
    {
        var resp = await http.PostAsJsonAsync("/api/command", new { text, source });
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var spoken = root.TryGetProperty("spoken", out var s) ? s.GetString() : body;
        var intent = root.TryGetProperty("intent", out var i) ? i.GetString() : "?";
        var tier = root.TryGetProperty("tier", out var t) ? t.GetString() : "?";
        var awaiting = root.TryGetProperty("awaitingConfirmation", out var a) && a.ValueKind == JsonValueKind.True;
        var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { spoken, intent, tier, awaitingConfirmation = awaiting, status }));
            return;
        }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(spoken);
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [{intent} · {tier}]");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new
                { error = $"JARVIS kernel unreachable at {baseUrl}", detail = ex.Message }));
        else
            Console.Error.WriteLine($"Kernel unreachable at {baseUrl}: {ex.Message}");
        Environment.ExitCode = 1;
    }
}
