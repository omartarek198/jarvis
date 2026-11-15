using System.Text.RegularExpressions;
using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

public class CommitmentsModule : IJarvisModule
{
    public string Name => "commitments";

    private const string DueWords =
        @"today|tonight|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday|next week|next \w+day|the weekend|this weekend|end of (the )?week|end of day|in \d+ days?";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "capture_commitment",
            Description = "capture a commitment / open loop",
            Patterns =
            [
                @"^(remind me to|i need to|i have to|don'?t let me forget( to)?) (?<text>.+)$",
            ],
            SlotHints = ["text", "due"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var text = ctx.Slot("text") ?? "";
                if (text.Length < 3) return new CommandResult("") { Unhandled = true };
                DateTime? due = DateParser.TryParse(ctx.Slot("due"));
                var dueMatch = Regex.Match(text, $@"^(?<rest>.+?)\s+by\s+(?<due>{DueWords})$", RegexOptions.IgnoreCase);
                if (due is null && dueMatch.Success)
                {
                    text = dueMatch.Groups["rest"].Value;
                    due = DateParser.TryParse(dueMatch.Groups["due"].Value);
                }

                var db = ctx.S<JarvisDb>();
                var project = await InferProjectAsync(ctx, text);
                var c = new Commitment { Text = text, DueDate = due, ProjectId = project?.Id, Source = ctx.Source };
                db.Commitments.Add(c);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("commitment", c.Id, text, text);
                ctx.S<DialogContext>().RecordItems([new DialogContext.ListItem("commitment", c.Id, c.Text)]);
                return new CommandResult(
                    $"Captured: {text}. Due {DateParser.Speak(due)}, filed under {project?.Name ?? "general"}.",
                    new { commitment = c });
            }
        },
        new()
        {
            Name = "capture_promise",
            Description = "capture a promise made to a specific person",
            Patterns = [@"^i (promised|owe) (?<person>[a-z]+( [a-z]+)?) (to |that )?(?<text>.+)$"],
            SlotHints = ["person", "text", "due"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var personName = ctx.Slot("person") ?? "someone";
                var text = ctx.Slot("text") ?? "";
                if (text.Length < 3) return new CommandResult("") { Unhandled = true };
                var person = await db.People.FirstOrDefaultAsync(p => p.Name.ToLower() == personName.ToLower());
                if (person is null) { person = new Person { Name = personName }; db.People.Add(person); }
                var due = DateParser.TryParse(ctx.Slot("due"));
                var c = new Commitment
                {
                    Text = text, PersonId = person.Id, PersonName = person.Name,
                    DueDate = due, Source = ctx.Source,
                    ProjectId = (await InferProjectAsync(ctx, text))?.Id,
                };
                db.Commitments.Add(c);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("commitment", c.Id, $"promise to {person.Name}", text);
                return new CommandResult($"Logged. You owe {person.Name}: {text}.", new { commitment = c });
            }
        },
        new()
        {
            Name = "owed_to_people",
            Description = "list open commitments owed to specific people",
            Patterns = [@"^(what do i owe( people)?|who am i letting down)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var owed = await db.Commitments.AsNoTracking()
                    .Where(c => c.Status == "open" && c.PersonName != "")
                    .OrderBy(c => c.DueDate ?? DateTime.MaxValue).ToListAsync();
                var general = await db.Commitments.CountAsync(c => c.Status == "open" && c.PersonName == "");
                ctx.S<DialogContext>().RecordItems(owed.Select(c => new DialogContext.ListItem("commitment", c.Id, c.Text)));
                if (owed.Count == 0)
                    return new CommandResult("You owe nobody anything specific. " + general switch
                    {
                        0 => "No general open loops either. Spotless.",
                        1 => "One general open loop remains.",
                        _ => $"{SpeechText.Number(general)} general open loops remain.",
                    });
                var lines = owed.Take(6).Select(c => $"{c.PersonName}: {c.Text}, due {DateParser.Speak(c.DueDate)}");
                return new CommandResult(
                    $"You owe {owed.Count} thing{(owed.Count == 1 ? "" : "s")}. " + string.Join(". ", lines) + ".",
                    new { owed });
            }
        },
        new()
        {
            Name = "promised_this_week",
            Description = "list commitments made this week",
            Patterns = [@"^what did i promise( people)?( this week)?$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek + 1);
                var list = await db.Commitments.AsNoTracking()
                    .Where(c => c.CreatedAt >= weekStart)
                    .OrderByDescending(c => c.CreatedAt).ToListAsync();
                if (list.Count == 0) return new CommandResult("No commitments captured this week so far.");
                var lines = list.Take(6).Select(c =>
                    (c.PersonName != "" ? $"to {c.PersonName}: " : "") + c.Text + (c.Status == "done" ? " — done" : ""));
                return new CommandResult($"This week, {list.Count} commitment{(list.Count == 1 ? "" : "s")}: "
                    + string.Join(". ", lines) + ".", new { commitments = list });
            }
        },
        new()
        {
            Name = "open_loops",
            Description = "list all open commitments / open loops",
            Patterns = [@"^(what('s| is) still open|list( open)? commitments|(show( me)? )?(all )?open loops( across .+)?)$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var open = await db.Commitments.AsNoTracking().Where(c => c.Status == "open")
                    .OrderBy(c => c.DueDate ?? DateTime.MaxValue).ThenByDescending(c => c.Priority).ToListAsync();
                ctx.S<DialogContext>().RecordItems(open.Select(c => new DialogContext.ListItem("commitment", c.Id, c.Text)));
                if (open.Count == 0) return new CommandResult("Zero open loops. Remarkable.");
                var lines = open.Take(7).Select(c => c.Text + (c.DueDate != null ? $" (due {DateParser.Speak(c.DueDate)})" : ""));
                return new CommandResult($"{open.Count} open loop{(open.Count == 1 ? "" : "s")}: "
                    + string.Join(". ", lines) + (open.Count > 7 ? ". And more on the ledger." : "."),
                    new { commitments = open });
            }
        },
        new()
        {
            Name = "open_before_weekend",
            Description = "list commitments still open before the weekend ends",
            Patterns = [@"^(remind me )?what('s| is) (still )?open before the weekend( ends)?$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var nextMonday = DateTime.Today.AddDays(((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7 + (DateTime.Today.DayOfWeek == DayOfWeek.Monday ? 7 : 0));
                var list = await db.Commitments.AsNoTracking()
                    .Where(c => c.Status == "open" && c.DueDate != null && c.DueDate < nextMonday)
                    .OrderBy(c => c.DueDate).ToListAsync();
                if (list.Count == 0) return new CommandResult("Nothing is due before the weekend ends.");
                return new CommandResult($"Before the weekend ends: " +
                    string.Join(". ", list.Take(6).Select(c => $"{c.Text}, due {DateParser.Speak(c.DueDate)}")) + ".",
                    new { commitments = list });
            }
        },
        new()
        {
            Name = "close_commitment",
            Description = "close a commitment by matching its text",
            Patterns = [@"^(close|complete|finish|mark (off|done)) (the )?(?<text>.+?)( commitment)?$"],
            SlotHints = ["text"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var needle = ctx.Slot("text") ?? "";
                var db = ctx.S<JarvisDb>();
                var open = await db.Commitments.Where(c => c.Status == "open").ToListAsync();
                var words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var matches = open
                    .Select(c => (C: c, Hits: words.Count(w => c.Text.Contains(w, StringComparison.OrdinalIgnoreCase))))
                    .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).ToList();
                if (matches.Count == 0)
                    return new CommandResult($"No open commitment matches '{needle}'.") { Unhandled = ctx.Source == "voice" && open.Count == 0 };
                if (matches.Count > 1 && matches[0].Hits == matches[1].Hits)
                    return new CommandResult("That matches several: " +
                        string.Join("; ", matches.Take(3).Select(m => m.C.Text)) + ". Be more specific.");
                var hit = matches[0].C;
                hit.Status = "done"; hit.ClosedAt = DateTime.UtcNow; hit.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return new CommandResult($"Closed: {hit.Text}. Well done.", new { commitment = hit });
            }
        },
    ];

    private static async Task<Project?> InferProjectAsync(CommandContext ctx, string text)
    {
        var db = ctx.S<JarvisDb>();
        foreach (var p in await db.Projects.AsNoTracking().ToListAsync())
        {
            if (text.Contains(p.Name, StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var a in p.Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (a.Length > 2 && text.Contains(a, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }
}

public class TasksModule : IJarvisModule
{
    public string Name => "tasks";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "add_task",
            Description = "add a task, optionally to a project",
            Patterns = [@"^(add|create)( a)? task[,:]? (?<text>.+?)( (to|for|in) (?<project>[\w. -]+))?$"],
            SlotHints = ["text", "project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                var t = new TaskItem { Title = ctx.Slot("text") ?? "", ProjectId = project?.Id, Source = ctx.Source };
                db.Tasks.Add(t);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("task", t.Id, t.Title, t.Title);
                return new CommandResult($"Task added to {project?.Name ?? "the general list"}: {t.Title}.", new { task = t });
            }
        },
        new()
        {
            Name = "list_tasks",
            Description = "list open tasks, optionally for a project",
            Patterns = [@"^(list|show)( my| me)? tasks( (for|in|on) (?<project>.+))?$"],
            SlotHints = ["project"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                var q = db.Tasks.AsNoTracking().Where(t => t.Status == "open" || t.Status == "doing");
                if (project is not null) q = q.Where(t => t.ProjectId == project.Id);
                var tasks = await q.OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                    .Take(7).ToListAsync();
                ctx.S<DialogContext>().RecordItems(tasks.Select(t => new DialogContext.ListItem("task", t.Id, t.Title)));
                if (tasks.Count == 0)
                    return new CommandResult(project is null ? "No open tasks." : $"No open tasks on {project.Name}.");
                return new CommandResult(
                    $"{(project is null ? "Open tasks" : project.Name + " tasks")}: " +
                    string.Join(". ", tasks.Select(t => t.Title)) + ".", new { tasks });
            }
        },
        new()
        {
            Name = "rank_tasks",
            Description = "rank tasks by ROI score and say what to work on",
            Patterns =
            [
                @"^(rank my tasks|prioriti[sz]e( my tasks)?)$",
                @"^what('s| is) the highest roi( task)?( right now)?$",
                @"^what should i (do|work on) (next|now|first)$",
            ],
            Handler = async ctx =>
            {
                var ranked = await ctx.S<Prioritizer>().RankAsync(take: 5);
                if (ranked.Count == 0) return new CommandResult("No open tasks to rank. Add some or enjoy the silence.");
                var top = ranked[0];
                var rest = ranked.Skip(1).Take(2).Select(r => $"{r.Task.Title} on {r.Project}");
                var why = top.Task.DueDate is { } d && d.Date <= DateTime.Today.AddDays(2)
                    ? "it's due " + DateParser.Speak(d) : $"it scores {top.Score:F1} on the ROI model";
                return new CommandResult(
                    $"Work on: {top.Task.Title}, on {top.Project} — {why}. " +
                    (rest.Any() ? "Then: " + string.Join("; then ", rest) + "." : ""),
                    new { ranked = ranked.Select(r => new { r.Task, r.Score, r.Project }) });
            }
        },
    ];
}

public class NotesModule : IJarvisModule
{
    public string Name => "notes";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "add_note",
            Description = "save a note, optionally to a project",
            Patterns = [@"^(note|take a note|write down)[,:]? (?<text>.+?)( (to|for|in) project (?<project>.+))?$"],
            SlotHints = ["text", "project"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var project = await ctx.S<EntityResolver>().ResolveProjectAsync(ctx.Slot("project"));
                var text = ctx.Slot("text") ?? "";
                var n = new Note { ProjectId = project?.Id, Title = text.Length > 60 ? text[..60] : text, BodyMd = text };
                db.Notes.Add(n);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("note", n.Id, n.Title, n.BodyMd);
                return new CommandResult($"Noted{(project is null ? "" : $" on {project.Name}")}.", new { note = n });
            }
        },
        new()
        {
            Name = "search_memory",
            Description = "full-text search across notes, commitments, tasks and reports",
            Patterns = [@"^search( my)? memory for (?<query>.+)$", @"^what do (i|we) know about (?<query>.+)$"],
            SlotHints = ["query"],
            Handler = ctx =>
            {
                var query = ctx.Slot("query") ?? "";
                var hits = ctx.S<MemoryIndex>().Search(query);
                if (hits.Count == 0) return Task.FromResult(new CommandResult($"Nothing in memory about {query} yet."));
                var spoken = $"Found {hits.Count} memor{(hits.Count == 1 ? "y" : "ies")} on {query}. " +
                    string.Join(". ", hits.Take(3).Select(h => $"A {h.Kind}: {h.Title}"));
                return Task.FromResult(new CommandResult(spoken,
                    new { results = hits.Select(h => new { h.Kind, h.RefId, h.Title, h.Snippet }) }));
            }
        },
    ];
}

public class IdeasModule : IJarvisModule
{
    public string Name => "ideas";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "park_idea",
            Description = "park a new idea in the anti-shiny-object parking lot",
            Patterns = [@"^(new idea|park (this |an )?idea|idea)[,:]? (?<text>.+)$"],
            SlotHints = ["text"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var idea = new Idea { Text = ctx.Slot("text") ?? "" };
                db.Ideas.Add(idea);
                await db.SaveChangesAsync();
                ctx.S<MemoryIndex>().Add("idea", idea.Id, idea.Text, idea.Text);
                return new CommandResult(
                    "Parked in the lot. Before it goes anywhere, answer me this: what project does it belong to, " +
                    "what does it replace, is it for now or later — and is it really higher ROI than your main project?",
                    new { idea });
            }
        },
    ];
}

public class MeetingsModule : IJarvisModule
{
    public string Name => "meetings";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "start_meeting",
            Description = "start meeting mode",
            Patterns = [@"^(start|begin) meeting( mode)?( (?<title>.+))?$"],
            SlotHints = ["title"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var active = await db.Meetings.FirstOrDefaultAsync(m => m.EndedAt == null);
                if (active is not null)
                    return new CommandResult($"A meeting is already running: {active.Title}. End it first.");
                var m = new Meeting { Title = ctx.Slot("title") ?? $"Meeting {DateTime.Now:HH:mm}" };
                db.Meetings.Add(m);
                await db.SaveChangesAsync();
                var scribe = (await db.Settings.FindAsync("meeting.scribe"))?.ValueJson.Trim('"') != "off";
                ctx.S<IEventBus>().Publish(new JarvisEvent("meeting.started", new { m.Id, m.Title, scribe }, ctx.Source));
                return new CommandResult(scribe
                    ? $"Meeting mode on: {m.Title}. I'm transcribing locally, both sides, and I'll pull action items when you end it."
                    : $"Meeting mode on: {m.Title}. Transcription is off — I'm only tracking time.", new { meeting = m });
            }
        },
        new()
        {
            Name = "meeting_scribe_toggle",
            Description = "turn meeting transcription on or off (applies to the next meeting)",
            Patterns = [@"^(turn )?meeting (transcription|scribe|recording) (?<on>on|off)$"],
            SlotHints = ["on"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var on = ctx.Slot("on") == "on";
                var db = ctx.S<JarvisDb>();
                var s = await db.Settings.FindAsync("meeting.scribe");
                if (s is null) db.Settings.Add(new Setting { Key = "meeting.scribe", ValueJson = on ? "\"on\"" : "\"off\"" });
                else s.ValueJson = on ? "\"on\"" : "\"off\"";
                await db.SaveChangesAsync();
                return new CommandResult(on
                    ? "Meeting transcription on — I'll scribe the next meeting locally."
                    : "Meeting transcription off — meetings will only be timed.");
            }
        },
        new()
        {
            Name = "end_meeting",
            Description = "end meeting mode and extract action items from notes",
            Patterns = [@"^end( the)? meeting( mode)?( and extract action items)?$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var m = await db.Meetings.FirstOrDefaultAsync(x => x.EndedAt == null);
                if (m is null) return new CommandResult("No meeting is currently running.");
                m.EndedAt = DateTime.UtcNow;
                m.UpdatedAt = DateTime.UtcNow;

                var actionLines = m.NotesMd.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => Regex.IsMatch(l, @"\b(i'?ll|i will|action|todo|need to|follow ?up)\b", RegexOptions.IgnoreCase))
                    .Select(l => l.Trim().TrimStart('-', '*', ' ')).ToList();
                foreach (var line in actionLines)
                {
                    var c = new Commitment { Text = line, Source = "meeting", SourceNote = m.Title, ProjectId = m.ProjectId };
                    db.Commitments.Add(c);
                    db.MeetingActions.Add(new MeetingAction { MeetingId = m.Id, Text = line, CommitmentId = c.Id });
                    ctx.S<MemoryIndex>().Add("commitment", c.Id, $"from {m.Title}", line);
                }
                await db.SaveChangesAsync();
                ctx.S<IEventBus>().Publish(new JarvisEvent("meeting.ended", new { m.Id, actions = actionLines.Count }, ctx.Source));

                var mins = (int)(m.EndedAt.Value - m.StartedAt).TotalMinutes;
                return new CommandResult($"Meeting ended after {mins} minutes. " +
                    (actionLines.Count > 0
                        ? $"Extracted {actionLines.Count} action item{(actionLines.Count == 1 ? "" : "s")} into the ledger."
                        : "No action items found in the notes — paste a transcript or add notes next time."),
                    new { meeting = m, actions = actionLines });
            }
        },
        new()
        {
            Name = "meeting_summary",
            Description = "have Claude summarize the last meeting's transcript and file the action items",
            Patterns = [@"^summari[sz]e (the |that |my )?(last )?meeting$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var m = await db.Meetings.AsNoTracking().Where(x => x.NotesMd != "")
                    .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync();
                if (m is null) return new CommandResult("No meeting with notes or a transcript yet.");
                var prompt = $"""
                    Summarize this meeting transcript for the user (they are "Me"). 4-6 spoken-friendly sentences:
                    what was discussed, what was decided, and who owes what. Put every action item that is
                    THE USER'S to do into the RESULT followUps array so they become tracked tasks.

                    Meeting: {m.Title} ({m.StartedAt.ToLocalTime():ddd MMM d HH:mm})
                    Transcript/notes:
                    {(m.NotesMd.Length > 12000 ? m.NotesMd[^12000..] : m.NotesMd)}
                    """;
                var (run, spoken) = await ctx.S<AgentService>().DispatchAsync("claude", prompt, "meeting-summary",
                    project: null, repo: null, edit: false, readonlyAllowedTools: "Read");
                AgentsModule.Tag(ctx, run);
                return new CommandResult(spoken, new { run.Id, meeting = m.Title });
            }
        },
        new()
        {
            Name = "meetings_today",
            Description = "list today's meetings",
            Patterns = [@"^what meetings did i have today\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var list = await db.Meetings.AsNoTracking()
                    .Where(m => m.StartedAt >= DateTime.UtcNow.Date).OrderBy(m => m.StartedAt).ToListAsync();
                if (list.Count == 0)
                    return new CommandResult("No meetings logged today. Say 'start meeting mode' when one begins.");
                var lines = list.Select(m =>
                {
                    var mins = m.EndedAt is { } e ? (int)(e - m.StartedAt).TotalMinutes : -1;
                    return $"{m.Title}{(mins >= 0 ? $", {mins} minutes" : ", still running")}";
                });
                return new CommandResult($"{list.Count} meeting{(list.Count == 1 ? "" : "s")} today: "
                    + string.Join(". ", lines) + ".", new { meetings = list });
            }
        },
        new()
        {
            Name = "promised_in_meetings",
            Description = "list commitments that came out of meetings",
            Patterns = [@"^what did i promise in( my)? meetings?( this week)?$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek + 1);
                var list = await db.Commitments.AsNoTracking()
                    .Where(c => c.Source == "meeting" && c.CreatedAt >= weekStart).ToListAsync();
                if (list.Count == 0) return new CommandResult("No meeting commitments captured this week.");
                return new CommandResult("From meetings this week: " +
                    string.Join(". ", list.Take(6).Select(c => c.Text)) + ".", new { commitments = list });
            }
        },
    ];
}
