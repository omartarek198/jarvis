using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>Tier-2 intent resolution: asks a local Ollama model to map an utterance onto the known command catalog.</summary>
public class OllamaIntentService(IConfiguration config, ILogger<OllamaIntentService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private string BaseUrl => config["Jarvis:Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    private string Model => config["Jarvis:Ollama:Model"] ?? "qwen2:7b";

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var r = await Http.GetAsync($"{BaseUrl}/api/tags", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>The intent is ALREADY decided (semantic router) — the LLM only fills the slots.</summary>
    public async Task<Dictionary<string, string>> ExtractSlotsAsync(string utterance, CommandDefinition def)
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.SlotHints.Length == 0) return slots;
        try
        {
            var prompt = $$"""
                Extract slot values from the spoken command for the action "{{def.Name}}" ({{def.Description}}).
                Slots to extract: {{string.Join(", ", def.SlotHints)}}. Omit slots that are not present.
                Utterance: "{{utterance}}"
                Reply with ONLY JSON: {"slots": { "name": "value" } }
                """;
            var body = JsonSerializer.Serialize(new
            { model = Model, stream = false, format = "json", options = new { temperature = 0.0 }, prompt });
            var resp = await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return slots;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            using var parsed = JsonDocument.Parse(doc.RootElement.GetProperty("response").GetString() ?? "{}");
            if (parsed.RootElement.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        slots[p.Name] = p.Value.GetString()!;
        }
        catch (Exception ex) { log.LogWarning("slot extraction failed: {Msg}", ex.Message); }
        return slots;
    }

    /// <summary>Gray-zone disambiguation: pick among a FEW semantically plausible candidates,
    /// with "general" (just answer the question) as a first-class option.</summary>
    public async Task<(string Intent, Dictionary<string, string> Slots, double Confidence)?> ChooseAmongAsync(
        string utterance, IReadOnlyList<CommandDefinition> candidates)
    {
        var result = await ResolveCoreAsync(utterance, candidates,
            "If the utterance is a general question or doesn't CLEARLY mean one of these commands, use \"general\".");
        if (result is null || result.Value.Intent.Equals("general", StringComparison.OrdinalIgnoreCase)) return null;
        return result;
    }

    public Task<(string Intent, Dictionary<string, string> Slots, double Confidence)?> ResolveAsync(
        string utterance, IReadOnlyList<CommandDefinition> catalog)
        => ResolveCoreAsync(utterance, catalog, null);

    private async Task<(string Intent, Dictionary<string, string> Slots, double Confidence)?> ResolveCoreAsync(
        string utterance, IReadOnlyList<CommandDefinition> catalog, string? extraRule)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("You map a spoken command to one intent from a fixed catalog.");
            sb.AppendLine("Catalog (name | description | slots):");
            foreach (var d in catalog)
                sb.AppendLine($"- {d.Name} | {d.Description} | {string.Join(",", d.SlotHints)}");
            sb.AppendLine();
            sb.AppendLine("Rules: if the utterance is a QUESTION, pick a query/list intent — never a capture/create/add one.");
            sb.AppendLine("Capture/create intents are only for imperative statements. If unsure, use \"unknown\".");
            if (extraRule is not null) sb.AppendLine(extraRule);
            sb.AppendLine("Examples:");
            sb.AppendLine("  \"do I owe anything to anyone?\" -> {\"intent\":\"owed_to_people\",\"slots\":{},\"confidence\":0.9}");
            sb.AppendLine("  \"what's going on in the invoicing project?\" -> {\"intent\":\"project_status\",\"slots\":{\"project\":\"northwind\"},\"confidence\":0.85}");
            sb.AppendLine("  \"any loose ends left this week?\" -> {\"intent\":\"open_loops\",\"slots\":{},\"confidence\":0.8}");
            sb.AppendLine("  \"how's the repo looking?\" -> {\"intent\":\"repo_clean\",\"slots\":{},\"confidence\":0.7}");
            sb.AppendLine();
            sb.AppendLine($"Utterance: \"{utterance}\"");
            sb.AppendLine("Reply with ONLY JSON: {\"intent\":\"<name or unknown>\",\"slots\":{...},\"confidence\":0..1}");

            // /api/generate works across all Ollama versions and models (some lack chat templates)
            var body = JsonSerializer.Serialize(new
            {
                model = Model,
                stream = false,
                format = "json",
                options = new { temperature = 0.0 },
                prompt = sb.ToString(),
            });

            var resp = await Http.PostAsync($"{BaseUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var content = doc.RootElement.GetProperty("response").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;

            using var parsed = JsonDocument.Parse(content);
            var root = parsed.RootElement;
            var intent = root.TryGetProperty("intent", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(intent) || intent == "unknown") return null;
            if (intent.Equals("general", StringComparison.OrdinalIgnoreCase)) return ("general", [], 1.0);

            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble() : 1.0;
            if (confidence < 0.4) return null;

            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        slots[p.Name] = p.Value.GetString()!;

            return (intent!, slots, confidence);
        }
        catch (Exception ex)
        {
            log.LogWarning("Ollama tier-2 unavailable: {Msg}", ex.Message);
            return null;
        }
    }
}
