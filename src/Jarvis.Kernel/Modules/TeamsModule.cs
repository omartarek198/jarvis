using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

/// <summary>
/// Teams by voice: unread, meetings, login, announce settings — fast paths via Graph.
/// Deep summarization questions go through Claude + the Microsoft 365 MCP server.
/// </summary>
public class TeamsModule(GraphService graph, AgentService agents) : IJarvisModule
{
    public string Name => "teams";

    /// <summary>The claude.ai Microsoft 365 connector (authenticated via /mcp). Tools discovered
    /// 2026-06-12: chat_message_search (Teams), outlook_email_search, outlook_calendar_search,
    /// find_meeting_availability, sharepoint_search/folder_search, read_resource.</summary>
    private const string M365McpTools = "mcp__claude_ai_Microsoft_365,Read,Glob";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "graph_login",
            Description = "connect the Microsoft account (Teams, calendar, mail) via device code",
            Patterns = [@"^(connect|log ?in to|set ?up) (my )?(microsoft|teams|graph|office)( account)?$"],
            Handler = async ctx =>
            {
                ctx.Audit.Tool = "http:graph";
                return new CommandResult(await graph.StartDeviceLoginAsync());
            }
        },
        new()
        {
            Name = "teams_unread",
            Description = "summarize unread Teams chats",
            Patterns = [@"^(any )?(unread |new )?teams (messages|chats)\??$", @"^what did i miss( on teams)?\??$",
                        @"^did anyone (message|ping) me( on teams)?\??$"],
            Handler = async ctx =>
            {
                ctx.Audit.Tool = "http:graph";
                if (!graph.LoggedIn)
                    return await McpFallbackAsync(ctx,
                        "List my unread Microsoft Teams chats: who messaged, in which chat/channel, and a one-line gist of each. Max 6, newest first, spoken-friendly.",
                        "teams-unread-mcp");
                var unread = await graph.UnreadChatsAsync();
                if (unread is null) return new CommandResult("Teams didn't answer — Graph token may need a re-login. Say: connect microsoft.");
                if (unread.Count == 0) return new CommandResult("No unread Teams messages. Inbox zero.");
                ctx.S<DialogContext>().RecordItems(unread.Take(8).Select(u =>
                    new DialogContext.ListItem("teams_chat", u.ChatId, $"{u.Who}: {u.Preview}")));
                var lines = unread.Take(5).Select(u =>
                    u.Topic.Length > 0 ? $"{u.Who} in {u.Topic}: {u.Preview}" : $"{u.Who}: {u.Preview}");
                return new CommandResult(
                    $"{unread.Count} unread chat{(unread.Count == 1 ? "" : "s")}. " + string.Join(". ", lines) + ".",
                    new { unread });
            }
        },
        new()
        {
            Name = "teams_meetings",
            Description = "read today's or tomorrow's meetings from the Microsoft 365 calendar",
            Patterns = [@"^(what )?meetings( do i have)?( (?<when>today|tomorrow))?\??$"],
            SlotHints = ["when"],
            Handler = async ctx =>
            {
                ctx.Audit.Tool = "http:graph";
                var day = ctx.Slot("when") == "tomorrow" ? DateTime.Today.AddDays(1) : DateTime.Today;
                if (!graph.LoggedIn)
                    return await McpFallbackAsync(ctx,
                        $"Read my Microsoft 365 calendar for {day:dddd, MMMM d, yyyy} and list the meetings: start time, title, online or not. Spoken-friendly, chronological.",
                        "calendar-mcp");
                var events = await graph.CalendarViewAsync(day);
                if (events is null) return new CommandResult("") { Unhandled = true };
                if (events.Count == 0)
                    return new CommandResult($"No meetings {(ctx.Slot("when") == "tomorrow" ? "tomorrow" : "today")}.");
                var lines = events.Select(e => $"{e.StartLocal:h:mm tt} {e.Subject}{(e.IsOnline ? " (online)" : "")}");
                return new CommandResult($"{events.Count} meeting{(events.Count == 1 ? "" : "s")}: " +
                    string.Join(". ", lines) + ".", new { events });
            }
        },
        new()
        {
            Name = "teams_announce_setting",
            Description = "set proactive Teams announcements to off, vip, or all",
            Patterns = [@"^(set )?teams (announcements?|notifications?) (to )?(?<mode>off|vip|vips|all)( only)?$"],
            SlotHints = ["mode"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var mode = ctx.Slot("mode")?.TrimEnd('s') switch { "vip" => "vip", "all" => "all", _ => "off" };
                var db = ctx.S<JarvisDb>();
                var s = await db.Settings.FindAsync("teams.announce");
                if (s is null) db.Settings.Add(new Setting { Key = "teams.announce", ValueJson = $"\"{mode}\"" });
                else s.ValueJson = $"\"{mode}\"";
                await db.SaveChangesAsync();
                return new CommandResult(mode switch
                {
                    "off" => "Teams announcements off.",
                    "vip" => "I'll only announce Teams messages from your VIPs.",
                    _ => "I'll announce every new Teams message.",
                });
            }
        },
        new()
        {
            Name = "teams_summarize",
            Description = "deep-summarize Teams content via Claude with the Microsoft 365 MCP",
            Patterns = [@"^summarize (the |my )?teams (discussion|messages|chat|channel)( (about|in|with|from) (?<what>.+))?$"],
            SlotHints = ["what"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var what = ctx.Slot("what") ?? "my recent unread conversations";
                var prompt = $"""
                    Using the Microsoft 365 MCP tools available to you (Teams chat/channel reads), summarize: {what}.
                    Focus on decisions, asks directed at the user, and deadlines. 5 sentences max, spoken-friendly.
                    If the Microsoft 365 MCP tools are NOT available in this session, state that plainly and
                    mention the server must be added to Claude's MCP config as 'ms365'.
                    """;
                var (run, spoken) = await agents.DispatchAsync("claude", prompt, "teams-summarize",
                    project: null, repo: null, edit: false, readonlyAllowedTools: M365McpTools);
                AgentsModule.Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id });
            }
        },
        new()
        {
            Name = "work_email",
            Description = "check or search the work (Outlook) inbox via the Microsoft 365 MCP",
            Patterns = [@"^(any new |check my |read my )?work (e?mail|inbox|emails)\??$",
                        @"^search( my)? work e?mail for (?<q>.+)$"],
            SlotHints = ["q"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var prompt = ctx.Slot("q") is { } q
                    ? $"Search my Outlook work mailbox for: {q}. Report the top matches — sender, subject, date, one-line gist. Spoken-friendly."
                    : "Check my Outlook work inbox for unread or recent important email. Report sender, subject and a one-line gist for the top 5, newest first. Spoken-friendly.";
                return await McpFallbackAsync(ctx, prompt, "work-email-mcp");
            }
        },
    ];

    /// <summary>Graph isn't connected (org blocks user consent) — answer via Claude + the
    /// Microsoft 365 MCP instead. Slower (spawns Claude) but covers all reads on demand.</summary>
    private async Task<CommandResult> McpFallbackAsync(CommandContext ctx, string prompt, string template)
    {
        var full = $"""
            {prompt}
            Use the Microsoft 365 MCP tools available to you. If they are NOT available or not
            authenticated in this session, state that plainly and mention: run claude, type /mcp,
            and authenticate Microsoft 365.
            """;
        var (run, spoken) = await agents.DispatchAsync("claude", full, template,
            project: null, repo: null, edit: false, readonlyAllowedTools: M365McpTools);
        AgentsModule.Tag(ctx, run);
        return new CommandResult($"Checking via Microsoft 365 — give me about twenty seconds. {spoken}", new { run.Id });
    }
}
