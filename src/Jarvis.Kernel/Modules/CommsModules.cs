using System.Text;
using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class EmailModule(EmailService email, ImapEmailService imap) : IJarvisModule
{
    public string Name => "email";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "email_unread",
            Description = "read out the unread inbox (sender and subject, newest first)",
            Patterns = [@"^(any new (e?mail|emails)|read (my |the )?unread (e?mails?|messages|inbox)|check my inbox)\??$"],
            Handler = async ctx =>
            {
                ctx.Audit.Tool = "imap";
                var (mails, err) = await imap.UnreadAsync(5);
                if (mails is null) return new CommandResult($"I can't read the inbox: {err}");
                if (mails.Count == 0) return new CommandResult("No unread email. Inbox clear.");
                ctx.S<DialogContext>().RecordItems(mails.Select(m =>
                    new DialogContext.ListItem("email", m.Uid.ToString(), $"{m.From}: {m.Subject}")));
                var lines = mails.Select((m, i) => $"{SpeechText.Number(i + 1)}: {m.From} — {m.Subject}");
                return new CommandResult($"{mails.Count} unread. " + string.Join(". ", lines) +
                    ". Say: read the first one, archive it, or flag it.", new { mails });
            }
        },
        new()
        {
            Name = "email_read_one",
            Description = "read the body of a listed email aloud",
            Patterns = [@"^read (the )?(?<ord>first|second|third|fourth|fifth|last)( one| email| message)?$",
                        @"^read (it|that)( out| aloud)?$"],
            SlotHints = ["ord"],
            Handler = async ctx =>
            {
                var item = PickEmail(ctx);
                if (item is null) return new CommandResult("") { Unhandled = true };
                ctx.Audit.Tool = "imap";
                var body = await imap.BodyAsync(uint.Parse(item.Id));
                return new CommandResult(body is null
                    ? "Couldn't fetch that message."
                    : $"From {item.Title}. {body}", new { item.Id });
            }
        },
        new()
        {
            Name = "email_archive",
            Description = "archive a listed email",
            Patterns = [@"^archive (it|that|this one|the (?<ord>first|second|third|fourth|fifth|last)( one| email)?)$"],
            SlotHints = ["ord"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var item = PickEmail(ctx);
                if (item is null) return new CommandResult("There's no recent email list to act on. Say: read my unread email.");
                ctx.Audit.Tool = "imap";
                return new CommandResult(await imap.ArchiveAsync(uint.Parse(item.Id))
                    ? $"Archived: {item.Title}." : "Archive failed — check the email connection.");
            }
        },
        new()
        {
            Name = "email_flag",
            Description = "flag a listed email for follow-up",
            Patterns = [@"^flag (it|that|this one|the (?<ord>first|second|third|fourth|fifth|last)( one| email)?)( for follow ?up)?$"],
            SlotHints = ["ord"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var item = PickEmail(ctx);
                if (item is null) return new CommandResult("There's no recent email list to act on. Say: read my unread email.");
                ctx.Audit.Tool = "imap";
                return new CommandResult(await imap.FlagAsync(uint.Parse(item.Id))
                    ? $"Flagged: {item.Title}." : "Flag failed — check the email connection.");
            }
        },
        new()
        {
            Name = "mark_vip",
            Description = "mark a person as VIP — their Teams messages and emails get announced",
            Patterns = [@"^(mark|make) (?<name>[\w .'-]+?) (as )?(a )?vip$", @"^add (?<name>[\w .'-]+?) to (the )?vips?$"],
            SlotHints = ["name"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var name = ctx.Slot("name");
                if (name is null) return new CommandResult("Who should I mark as VIP?");
                var db = ctx.S<JarvisDb>();
                var person = await db.People.FirstOrDefaultAsync(p =>
                    p.Name.ToLower() == name.ToLower() || p.Aliases.ToLower().Contains(name.ToLower()));
                if (person is null) { person = new Person { Name = name }; db.People.Add(person); }
                person.IsVip = true;
                person.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return new CommandResult($"{person.Name} is now a VIP — I'll announce their Teams messages and emails.");
            }
        },
        new()
        {
            Name = "email_send",
            Description = "send that email",
            Patterns =
            [
                @"^(send|email|shoot|fire off|drop|write) (?<msg>.+?) to (this (e?mail|address|person) )?(?<to>[^\s@]+@[^\s@]+\.[^\s@]+)$",
                @"^(e?mail|send|tell|message) (this (e?mail|address|person) )?(?<to>[^\s@]+@[^\s@]+\.[^\s@]+) (?:saying|that says|that|to say|with|reading)[:,]? (?<msg>.+)$",
            ],
            SlotHints = ["to", "msg"],
            Examples =
            [
                "send a hi to sam@example.com",
                "email john@example.com saying running five minutes late",
                "shoot a thanks to maria@contoso.com",
            ],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var to = ctx.Slot("to");
                var msg = ctx.Slot("msg");
                if (to is null || !to.Contains('@')) return new CommandResult("I need a valid email address to send to.");
                if (msg is null) return new CommandResult("I need something to say in the email.");
                // "a hi" -> "hi": drop a leading article so a short greeting reads cleanly
                msg = System.Text.RegularExpressions.Regex.Replace(msg.Trim(), @"^(a|an|the)\s+", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                ctx.Audit.Tool = "smtp";
                var subject = msg.Length <= 78 ? msg : msg[..78];
                var (ok, detail) = await email.SendAsync(subject, msg, to);
                return new CommandResult(ok ? $"Sent \"{msg}\" to {to}." : $"Send failed: {detail}");
            }
        },
        new()
        {
            Name = "email_check",
            Description = "verify the email connection (no mail is sent)",
            Patterns = [@"^(check|test|verify) (the )?email( connection| setup)?$"],
            Handler = async _ =>
            {
                var (ok, detail) = await email.VerifyAsync();
                return new CommandResult(ok
                    ? "Email is configured and authenticated. Sends still require your confirmation."
                    : $"Email isn't working: {detail}");
            }
        },
        new()
        {
            Name = "email_briefing",
            Description = "email the morning briefing to the owner",
            Patterns = [@"^(e?mail|send) me the (morning |latest )?briefing$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var briefing = ctx.S<BriefingService>();
                var (md, _) = await briefing.BuildMorningAsync();
                var (ok, detail) = await email.SendAsync($"JARVIS briefing — {DateTime.Now:ddd MMM d}", md);
                return new CommandResult(ok ? "Briefing emailed." : $"Send failed: {detail}");
            }
        },
        new()
        {
            Name = "email_open_loops",
            Description = "email all open commitments to the owner",
            Patterns = [@"^(e?mail|send) me (my )?(the )?open (loops|commitments)$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var open = await db.Commitments.AsNoTracking().Where(c => c.Status == "open")
                    .OrderBy(c => c.DueDate ?? DateTime.MaxValue).ToListAsync();
                var body = new StringBuilder($"Open loops as of {DateTime.Now:f}\n\n");
                foreach (var c in open)
                    body.AppendLine($"- {c.Text}{(c.PersonName != "" ? $" (for {c.PersonName})" : "")}" +
                                    $"{(c.DueDate is { } d ? $" — due {d:ddd MMM d}" : "")}");
                if (open.Count == 0) body.AppendLine("(none — ledger clear)");
                var (ok, detail) = await email.SendAsync($"JARVIS — {open.Count} open loops", body.ToString());
                return new CommandResult(ok ? $"Emailed {open.Count} open loops." : $"Send failed: {detail}");
            }
        },
        new()
        {
            Name = "email_last_report",
            Description = "email the last agent run report to the owner",
            Patterns = [@"^(e?mail|send) me (claude'?s|codex'?s|the) (last |latest )?(report|agent run|result)$"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var run = await db.AgentRuns.AsNoTracking().Where(r => r.Status == "done")
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
                if (run is null) return new CommandResult("No completed agent runs to email.");
                var body = $"Agent: {run.Agent}\nTemplate: {run.PromptTemplate}\nFinished: {run.EndedAt:f}\n\n{run.ResultSummary}\n\nTranscript: {run.TranscriptPath}";
                var (ok, detail) = await email.SendAsync($"JARVIS — {run.Agent} report: {run.PromptTemplate}", body);
                return new CommandResult(ok ? "Report emailed." : $"Send failed: {detail}");
            }
        },
    ];

    /// <summary>Resolve "the second one" / "it" against the last listed emails (DialogContext).</summary>
    private static DialogContext.ListItem? PickEmail(CommandContext ctx)
    {
        var dialog = ctx.S<DialogContext>();
        var ord = ctx.Slot("ord") switch
        {
            "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5,
            "last" => dialog.LastItems.Count, _ => 1,
        };
        var item = dialog.ItemByOrdinal(ord);
        return item?.Kind == "email" ? item : null;
    }
}

public class CalendarModule(CalendarService calendar) : IJarvisModule
{
    public string Name => "calendar";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "calendar_today",
            Description = "read today's or tomorrow's calendar from Outlook",
            Patterns = [@"^what('?s| is) on (my|the) calendar( (?<when>today|tomorrow))?\??$",
                        @"^(my )?calendar( for)?( (?<when>today|tomorrow))?$"],
            SlotHints = ["when"],
            Handler = ctx =>
            {
                if (!calendar.Available)
                    return Task.FromResult(new CommandResult(
                        "Classic Outlook isn't available on this machine, so I can't read the calendar yet."));
                var day = ctx.Slot("when") == "tomorrow" ? DateTime.Today.AddDays(1) : DateTime.Today;
                var events = calendar.EventsFor(day);
                if (events is null) return Task.FromResult(new CommandResult("Outlook didn't answer — is it running?"));
                if (events.Count == 0)
                    return Task.FromResult(new CommandResult($"Nothing on the calendar {(day == DateTime.Today ? "today" : "tomorrow")}."));
                var lines = events.Select(e => $"{e.Start:h:mm tt} {e.Subject}");
                return Task.FromResult(new CommandResult(
                    $"{events.Count} event{(events.Count == 1 ? "" : "s")}: " + string.Join(". ", lines) + ".",
                    new { events }));
            }
        },
    ];
}
