using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Proactive Teams awareness: polls unread chats every 90s (safe under Graph throttling
/// budgets at $top=20) and announces new ones per the teams.announce setting (off|vip|all,
/// default vip). Only sender/preview titles are spoken — never full bodies.
/// </summary>
public class TeamsPoller(GraphService graph, IEventBus bus, IServiceScopeFactory scopes,
    ILogger<TeamsPoller> log) : BackgroundService
{
    private readonly HashSet<string> _announced = [];   // chatId + lastMessage timestamp

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);   // let the kernel finish booting
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { log.LogWarning("[teams-poller] tick failed: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(90), ct);
        }
    }

    private async Task TickAsync()
    {
        if (!graph.LoggedIn) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        var mode = (await db.Settings.FindAsync("teams.announce"))?.ValueJson.Trim('"') ?? "vip";
        if (mode == "off") return;

        var unread = await graph.UnreadChatsAsync();
        if (unread is null || unread.Count == 0) return;

        List<string> vips = mode == "vip"
            ? await db.People.AsNoTracking().Where(p => p.IsVip).Select(p => p.Name).ToListAsync()
            : [];

        var announcedThisTick = 0;
        foreach (var u in unread)
        {
            if (announcedThisTick >= 3) break;                       // never machine-gun the speaker
            var key = $"{u.ChatId}|{u.At:O}";
            if (!_announced.Add(key)) continue;
            if (mode == "vip" && !vips.Any(v => u.Who.Contains(v, StringComparison.OrdinalIgnoreCase) ||
                                                v.Contains(u.Who, StringComparison.OrdinalIgnoreCase)))
                continue;

            var where = u.Topic.Length > 0 ? $" in {u.Topic}" : "";
            bus.Publish(new JarvisEvent("teams.message", new
            {
                chatId = u.ChatId, who = u.Who, topic = u.Topic,
                spoken = $"Teams: {u.Who} messaged you{where}.",
            }, "scheduler"));
            announcedThisTick++;
        }

        // Bounded memory: drop old keys once the set grows
        if (_announced.Count > 500) _announced.Clear();
    }
}
