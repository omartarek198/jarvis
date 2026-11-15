using System.Security.Cryptography;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Push notifications to the owner's phone via ntfy.sh (free tier). The topic name is the only
/// secret — random 32 chars, auto-generated, kept in the DPAPI store. Titles/one-liners only:
/// never email bodies or Jira payloads. Fire-and-forget with a 5s timeout — ntfy outages
/// must never block the bus.
/// </summary>
public class NtfyService(SecretsService secrets, IEventBus bus, IServiceScopeFactory scopes,
    ExternalActions external, ILogger<NtfyService> log) : IHostedService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Dictionary<string, string> KindTags = new()
    {
        ["reminder.due"] = "alarm_clock",
        ["agent.done"] = "robot",
        ["teams.message"] = "speech_balloon",
        ["teams.mention"] = "speech_balloon",
        ["email.vip"] = "envelope",
        ["habit.nag"] = "calendar",
        ["focus.nag"] = "hourglass",
        ["briefing.ready"] = "newspaper",
        ["graph.code"] = "key",
    };
    private IDisposable? _sub;

    public string Topic
    {
        get
        {
            var t = secrets.Get("ntfy.topic");
            if (t is null)
            {
                t = "jarvis-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                secrets.Set("ntfy.topic", t);
            }
            return t;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!external.Enabled)
        {
            log.LogInformation("[ntfy] push notifications disabled (Jarvis:ExternalActions:Enabled=false)");
            return Task.CompletedTask;
        }
        _ = Topic;   // ensure generated on first start
        _sub = bus.Subscribe(async e =>
        {
            if (!KindTags.TryGetValue(e.Kind, out var tag)) return;
            var spoken = TryGetSpoken(e.Payload);
            if (string.IsNullOrWhiteSpace(spoken)) return;
            if (await InQuietHoursAsync() && e.Kind != "reminder.due") return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{Topic}")
                    { Content = new StringContent(spoken) };
                req.Headers.Add("Title", "JARVIS");
                req.Headers.Add("Tags", tag);
                await Http.SendAsync(req);
            }
            catch (Exception ex) { log.LogDebug("[ntfy] push failed: {Msg}", ex.Message); }
        });
        log.LogInformation("[ntfy] forwarding {Count} event kinds to topic ntfy.sh/{Topic}",
            KindTags.Count, Topic[..10] + "…");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }

    private static string? TryGetSpoken(object? payload)
    {
        if (payload is null) return null;
        var prop = payload.GetType().GetProperty("spoken");
        return prop?.GetValue(payload) as string;
    }

    /// <summary>Setting ntfy.quiet = "22-08" (local hours). Empty/missing = never quiet.</summary>
    private async Task<bool> InQuietHoursAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
            var raw = (await db.Settings.FindAsync("ntfy.quiet"))?.ValueJson.Trim('"');
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = raw.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var from) || !int.TryParse(parts[1], out var to))
                return false;
            var h = DateTime.Now.Hour;
            return from <= to ? h >= from && h < to : h >= from || h < to;
        }
        catch { return false; }
    }
}
