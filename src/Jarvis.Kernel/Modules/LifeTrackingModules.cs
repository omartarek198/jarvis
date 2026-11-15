using Jarvis.Core;
using Jarvis.Kernel.Services;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Modules;

/// <summary>
/// Sleep by voice + briefing chaining. Registered BEFORE BriefingModule: "good morning" /
/// "good night" land here, log the check-in, then chain the same briefing the old commands
/// gave. All times LOCAL; a SleepLog's Date is the WAKE morning's date — a "good night"
/// said after 15:00 belongs to tomorrow's log, one said at 00:30 to today's.
/// </summary>
public class SleepModule : IJarvisModule
{
    public string Name => "sleep";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "sleep_goodnight",
            Description = "log bedtime and give the shutdown briefing",
            Patterns = [@"^good ?night( jarvis)?$", @"^(i'?m )?(going|heading|off) to (bed|sleep)$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var now = DateTime.Now;
                var logDate = now.Hour >= 15 ? now.Date.AddDays(1) : now.Date;
                var row = await GetOrCreateAsync(db, logDate);
                row.BedAt = now; row.BedSource = "voice"; row.UpdatedAt = DateTime.UtcNow;
                Reconcile(row);
                await db.SaveChangesAsync();

                var (md, spoken) = await ctx.S<BriefingService>().BuildShutdownAsync();
                db.Briefings.Add(new Briefing { Kind = "shutdown", ContentMd = md, SpokenSummary = spoken });
                await db.SaveChangesAsync();
                return new CommandResult($"Good night, sir. Bedtime logged. {spoken}", new { sleep = row, markdown = md });
            }
        },
        new()
        {
            Name = "sleep_goodmorning",
            Description = "log wake time and give the morning briefing",
            Patterns = [@"^good morning( jarvis)?$", @"^(i'?m|just) (up|awake)$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var now = DateTime.Now;
                var row = await GetOrCreateAsync(db, now.Date);
                row.WakeAt = now; row.WakeSource = "voice"; row.UpdatedAt = DateTime.UtcNow;
                Reconcile(row);
                await db.SaveChangesAsync();

                var slept = row.DurationMin is { } m
                    ? $"You slept {m / 60} hours {SpeechText.Number(m % 60)} minutes. " : "";
                var (md, spoken) = await ctx.S<BriefingService>().BuildMorningAsync();
                db.Briefings.Add(new Briefing { Kind = "morning", ContentMd = md, SpokenSummary = spoken });
                await db.SaveChangesAsync();
                return new CommandResult($"{slept}{spoken}", new { sleep = row, markdown = md });
            }
        },
        new()
        {
            Name = "sleep_report",
            Description = "report last night's sleep and the weekly average",
            Patterns = [@"^(how (did|do) i sleep|sleep report|how much (did|do) i sleep( this week)?)\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var week = await db.SleepLogs.AsNoTracking()
                    .Where(s => s.Date >= DateTime.Today.AddDays(-7) && s.DurationMin != null)
                    .OrderByDescending(s => s.Date).ToListAsync();
                if (week.Count == 0)
                    return new CommandResult("No sleep data yet. Say good night when you head off, and good morning when you're up.");
                var last = week[0];
                var avg = (int)week.Average(s => s.DurationMin!.Value);
                var parts = new List<string>();
                if (last.Date == DateTime.Today && last.DurationMin is { } lm)
                    parts.Add($"Last night: {lm / 60} hours {SpeechText.Number(lm % 60)} minutes ({last.BedSource}/{last.WakeSource}).");
                parts.Add($"{week.Count}-night average: {avg / 60} hours {SpeechText.Number(avg % 60)} minutes.");
                return new CommandResult(string.Join(" ", parts), new { week });
            }
        },
    ];

    internal static async Task<SleepLog> GetOrCreateAsync(JarvisDb db, DateTime logDate)
    {
        var row = await db.SleepLogs.FirstOrDefaultAsync(s => s.Date == logDate);
        if (row is null) { row = new SleepLog { Date = logDate }; db.SleepLogs.Add(row); }
        return row;
    }

    internal static void Reconcile(SleepLog row)
    {
        if (row.BedAt is { } bed && row.WakeAt is { } wake && wake > bed && (wake - bed).TotalHours < 20)
            row.DurationMin = (int)(wake - bed).TotalMinutes;
    }
}

/// <summary>Habits: streaks, voice check-ins, scheduled nags (executed by SchedulerService).</summary>
public class HabitsModule : IJarvisModule
{
    public string Name => "habits";

    public IEnumerable<CommandDefinition> Commands =>
    [
        new()
        {
            Name = "habit_add",
            Description = "create a tracked habit, optionally with a daily nag time",
            Patterns = [@"^(add|create|track) (a |the )?(new )?habit[,:]? (?<name>[\w .'-]+?)( every (?<sched>day|weekday|weekdays))?( at (?<time>[\w:. ]+))?$"],
            SlotHints = ["name", "sched", "time"],
            Safety = SafetyClass.Mutate,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var name = ctx.Slot("name")!.Trim();
                if (await FindAsync(db, name) is not null)
                    return new CommandResult($"You already track {name}.");
                var nag = DateParser.TryParseTimeSpec(ctx.Slot("time")) is { } t ? t.ToString("HH:mm") : "";
                var habit = new Habit
                {
                    Name = name,
                    Schedule = ctx.Slot("sched") is "weekday" or "weekdays" ? "weekdays" : "daily",
                    NagTime = nag,
                };
                db.Habits.Add(habit);
                await db.SaveChangesAsync();
                return new CommandResult($"Tracking {name}, {habit.Schedule}" +
                    (nag.Length > 0 ? $", with a nudge at {DateTime.Parse(nag):h:mm tt}." : "."), new { habit });
            }
        },
        new()
        {
            Name = "habit_log",
            Description = "log a habit as done today",
            Patterns = [@"^log (?<habit>[\w .'-]+?)( today| done)?$",
                        @"^i (did|finished|completed)( my| the)? (?<habit>[\w .'-]+?)( today)?$"],
            SlotHints = ["habit"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var habit = await FindAsync(db, ctx.Slot("habit") ?? "");
                if (habit is null) return new CommandResult("") { Unhandled = true };  // not a habit — let other modules try
                if (await db.HabitLogs.AnyAsync(l => l.HabitId == habit.Id && l.Date == DateTime.Today))
                    return new CommandResult($"{habit.Name} was already logged today.");
                db.HabitLogs.Add(new HabitLog { HabitId = habit.Id, Date = DateTime.Today });
                await db.SaveChangesAsync();
                var streak = await StreakAsync(db, habit);
                return new CommandResult($"{habit.Name} logged. Streak: {SpeechText.Number(streak)} " +
                    $"{(habit.Schedule == "weekdays" ? "weekday" : "day")}{(streak == 1 ? "" : "s")}.");
            }
        },
        new()
        {
            Name = "habit_check",
            Description = "check whether a habit was logged today",
            Patterns = [@"^did i (do |log )?(the )?(?<habit>[\w .'-]+?)( today)?\??$"],
            SlotHints = ["habit"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var habit = await FindAsync(db, ctx.Slot("habit") ?? "");
                if (habit is null) return new CommandResult("") { Unhandled = true };
                var done = await db.HabitLogs.AnyAsync(l => l.HabitId == habit.Id && l.Date == DateTime.Today);
                return new CommandResult(done ? $"Yes — {habit.Name} is logged for today." : $"Not yet. Say: log {habit.Name}.");
            }
        },
        new()
        {
            Name = "habit_streaks",
            Description = "report streaks for all tracked habits",
            Patterns = [@"^(habit (streaks|report|status)|(show |what are )?my habits)\??$"],
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var habits = await db.Habits.AsNoTracking().Where(h => h.Active).ToListAsync();
                if (habits.Count == 0)
                    return new CommandResult("No habits tracked yet. Say: add habit gym every weekday at 7 pm.");
                var lines = new List<string>();
                foreach (var h in habits)
                {
                    var streak = await StreakAsync(db, h);
                    var today = await db.HabitLogs.AnyAsync(l => l.HabitId == h.Id && l.Date == DateTime.Today);
                    lines.Add($"{h.Name}: {SpeechText.Number(streak)} day streak{(today ? "" : ", not yet today")}");
                }
                return new CommandResult(string.Join(". ", lines) + ".", new { habits });
            }
        },
        new()
        {
            Name = "habit_snooze",
            Description = "snooze habit nags for an hour",
            Patterns = [@"^(snooze (the )?(habit )?nag|nag me later|not now)$"],
            Safety = SafetyClass.WriteLocal,
            Handler = async ctx =>
            {
                var db = ctx.S<JarvisDb>();
                var until = DateTime.Now.AddHours(1).ToString("O");
                var s = await db.Settings.FindAsync("habit.snoozeUntil");
                if (s is null) db.Settings.Add(new Setting { Key = "habit.snoozeUntil", ValueJson = $"\"{until}\"" });
                else s.ValueJson = $"\"{until}\"";
                await db.SaveChangesAsync();
                return new CommandResult("Alright — I'll nag you again in an hour.");
            }
        },
    ];

    internal static async Task<Habit?> FindAsync(JarvisDb db, string spoken)
    {
        spoken = spoken.Trim().ToLowerInvariant();
        if (spoken.Length < 2) return null;
        var habits = await db.Habits.Where(h => h.Active).ToListAsync();
        return habits.FirstOrDefault(h => h.Name.Equals(spoken, StringComparison.OrdinalIgnoreCase))
            ?? habits.FirstOrDefault(h => h.Aliases.ToLowerInvariant().Split(',',
                   StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(spoken))
            ?? habits.FirstOrDefault(h => h.Name.Contains(spoken, StringComparison.OrdinalIgnoreCase)
                   || spoken.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Consecutive scheduled days logged, walking back from today (or yesterday if today is unlogged).</summary>
    internal static async Task<int> StreakAsync(JarvisDb db, Habit habit)
    {
        var dates = (await db.HabitLogs.AsNoTracking().Where(l => l.HabitId == habit.Id)
            .Select(l => l.Date).ToListAsync()).ToHashSet();
        var day = DateTime.Today;
        if (!dates.Contains(day)) day = PrevScheduled(day, habit.Schedule);
        var streak = 0;
        while (dates.Contains(day)) { streak++; day = PrevScheduled(day, habit.Schedule); }
        return streak;
    }

    private static DateTime PrevScheduled(DateTime day, string schedule)
    {
        do day = day.AddDays(-1);
        while (schedule == "weekdays" && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return day;
    }
}
