using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Core;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Samples the foreground window every 5s (process + title ONLY — never content), categorizes time,
/// and detects Teams meeting windows. Privacy: titles stay in the local DB; nothing leaves the machine.
/// </summary>
public class FocusSampler(IServiceScopeFactory scopes, IEventBus bus, IConfiguration config, ILogger<FocusSampler> log) : BackgroundService
{
    private string? _machineId;
    private (string Proc, string Title, string Category, DateTime Start)? _segment;
    private DateTime _lastMeetingToast = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Records the foreground process name and window title every 5s (local SQLite only).
        // Opt-in via Jarvis:Focus:SampleForegroundWindow so a fresh clone never logs window titles.
        if (!config.GetValue("Jarvis:Focus:SampleForegroundWindow", false))
        {
            log.LogInformation("[focus] foreground-window sampling disabled (Jarvis:Focus:SampleForegroundWindow=false)");
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // let the kernel settle
        while (!ct.IsCancellationRequested)
        {
            try { await SampleAsync(); }
            catch (Exception ex) { log.LogWarning("focus sample failed: {Msg}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
        await FlushAsync(); // persist the open segment on shutdown
    }

    private async Task SampleAsync()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        var title = GetTitle(hwnd);
        GetWindowThreadProcessId(hwnd, out var pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { return; }

        var category = Categorize(proc, title);
        DetectTeamsMeeting(proc, title);

        if (_segment is { } seg && (seg.Proc != proc || seg.Category != category
            || (DateTime.UtcNow - seg.Start).TotalSeconds >= 60)) // cap segments at 60s so long sessions still persist
        {
            await FlushAsync();
        }
        _segment ??= (proc, Truncate(title, 120), category, DateTime.UtcNow);
    }

    private async Task FlushAsync()
    {
        if (_segment is not { } seg) return;
        _segment = null;
        var duration = (int)(DateTime.UtcNow - seg.Start).TotalSeconds;
        if (duration < 5) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();
        _machineId ??= (await db.Machines.AsNoTracking().FirstOrDefaultAsync())?.Id ?? "local";
        db.FocusSamples.Add(new FocusSample
        {
            MachineId = _machineId, At = seg.Start, ProcessName = seg.Proc,
            WindowTitle = seg.Title, Category = seg.Category, DurationSec = duration,
        });
        await db.SaveChangesAsync();
    }

    public static string Categorize(string proc, string title)
    {
        var t = title.ToLowerInvariant();
        if (proc is "devenv" or "rider64" or "code" or "cursor" or "ssms" or "datagrip64") return "ide";
        if (proc.Contains("teams")) return "teams";
        if (proc is "windowsterminal" or "wt" or "cmd" or "powershell" or "pwsh" or "conhost" or "wezterm-gui" or "alacritty") return "terminal";
        if (proc is "chrome" or "msedge" or "firefox" or "brave" or "opera")
            return t.Contains("youtube") || t.Contains("netflix") || t.Contains("twitter") || t.Contains("reddit")
                || t.Contains("instagram") || t.Contains("facebook") || t.Contains("tiktok") || t.Contains("twitch")
                || t.Contains("9gag")
                ? "distraction" : "browser";
        if (proc is "discord" or "telegram" or "whatsapp" or "slack") return "distraction";
        return "other";
    }

    private void DetectTeamsMeeting(string proc, string title)
    {
        if (!proc.Contains("teams")) return;
        var t = title.ToLowerInvariant();
        var looksLikeMeeting = t.Contains("meeting") || t.Contains("call") || t.Contains("| microsoft teams") && t.Contains(",");
        if (!looksLikeMeeting || DateTime.UtcNow - _lastMeetingToast < TimeSpan.FromMinutes(15)) return;
        _lastMeetingToast = DateTime.UtcNow;
        bus.Publish(new JarvisEvent("meeting.detected",
            new { title = Truncate(title, 80), hint = "Say 'start meeting mode' to track it. Nothing is being recorded." }, "system"));
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len];
    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

/// <summary>Time-based duties: auto morning briefing, focus-session expiry, reminders, nightly backup.</summary>
public class SchedulerService(IServiceScopeFactory scopes, IEventBus bus, IConfiguration config, ILogger<SchedulerService> log) : BackgroundService
{
    private DateTime _lastBackupCheck = DateTime.MinValue;
    private DateTime _sleepInferredFor = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _habitNaggedOn = [];
    private DateTime _lastFocusNag = DateTime.MinValue;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { log.LogWarning("scheduler tick failed: {Msg}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JarvisDb>();

        // Auto morning briefing at the configured local time (default 07:30)
        var timeSetting = await db.Settings.FindAsync("briefing.morningTime");
        var timeStr = timeSetting?.ValueJson.Trim('"') ?? "07:30";
        if (TimeOnly.TryParse(timeStr, out var at))
        {
            var now = TimeOnly.FromDateTime(DateTime.Now);
            var withinWindow = now >= at && now < at.AddMinutes(30);
            if (withinWindow)
            {
                var alreadyToday = await db.Briefings.AnyAsync(b => b.Kind == "morning" && b.At >= DateTime.UtcNow.Date);
                if (!alreadyToday)
                {
                    var briefing = scope.ServiceProvider.GetRequiredService<BriefingService>();
                    var (md, spoken) = await briefing.BuildMorningAsync();
                    db.Briefings.Add(new Briefing { Kind = "morning", ContentMd = md, SpokenSummary = spoken });
                    await db.SaveChangesAsync();
                    bus.Publish(new JarvisEvent("briefing.ready", new { kind = "morning", spoken }, "scheduler"));
                }
            }
        }

        // Nightly backup (also self-heals if the kernel wasn't running at night)
        if (DateTime.UtcNow - _lastBackupCheck > TimeSpan.FromMinutes(10))
        {
            _lastBackupCheck = DateTime.UtcNow;
            try { await BackupIfNeededAsync(); }
            catch (Exception ex) { log.LogWarning("backup failed: {Msg}", ex.Message); }
        }

        // Reminders / alarms / timers
        var due = await db.Reminders.Where(r => r.Status == "pending").ToListAsync();
        foreach (var r in due.Where(r => r.At <= DateTime.Now))
        {
            r.FiredAt = DateTime.UtcNow;
            r.UpdatedAt = DateTime.UtcNow;
            if (r.Recurrence == "daily") r.At = r.At.AddDays(1);
            else if (r.Recurrence == "weekdays")
            {
                do r.At = r.At.AddDays(1);
                while (r.At.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            }
            else r.Status = "done";
            await db.SaveChangesAsync();
            var spoken = r.Kind switch
            {
                "alarm" => $"Alarm. It's {DateTime.Now:h:mm tt}." + (r.Text is "Alarm" or "" ? "" : $" {r.Text}."),
                "timer" => $"Time's up — {r.Text}.",
                _ => $"Reminder: {r.Text}.",
            };
            bus.Publish(new JarvisEvent("reminder.due", new { r.Id, r.Kind, spoken }, "scheduler"));

            // A morning alarm wakes you INTO the briefing — alarm sound first, then the day
            if (r.Kind == "alarm" && DateTime.Now.Hour is >= 5 and < 11
                && !await db.Briefings.AnyAsync(b => b.Kind == "morning" && b.At >= DateTime.UtcNow.Date))
            {
                var briefing = scope.ServiceProvider.GetRequiredService<BriefingService>();
                var (md, briefSpoken) = await briefing.BuildMorningAsync();
                db.Briefings.Add(new Briefing { Kind = "morning", ContentMd = md, SpokenSummary = briefSpoken });
                await db.SaveChangesAsync();
                bus.Publish(new JarvisEvent("briefing.ready", new { kind = "morning", spoken = briefSpoken }, "scheduler"));
            }
        }

        // Weekly review: Friday at weekly.reviewTime (default 16:30), once per week
        var reviewStr = (await db.Settings.FindAsync("weekly.reviewTime"))?.ValueJson.Trim('"') ?? "16:30";
        if (DateTime.Today.DayOfWeek == DayOfWeek.Friday && TimeOnly.TryParse(reviewStr, out var reviewAt))
        {
            var nowT = TimeOnly.FromDateTime(DateTime.Now);
            if (nowT >= reviewAt && nowT < reviewAt.AddMinutes(30))
            {
                var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.Today.DayOfWeek);
                if (!await db.Briefings.AnyAsync(b => b.Kind == "weekly" && b.At >= weekStart))
                {
                    var briefing = scope.ServiceProvider.GetRequiredService<BriefingService>();
                    var (md, spoken) = await briefing.BuildWeeklyAsync();
                    db.Briefings.Add(new Briefing { Kind = "weekly", ContentMd = md, SpokenSummary = spoken });
                    await db.SaveChangesAsync();
                    bus.Publish(new JarvisEvent("briefing.ready", new { kind = "weekly", spoken }, "scheduler"));
                }
            }
        }

        // Screen-time guardrail: too much distraction in the rolling hour during work hours
        try { await GuardrailAsync(db); }
        catch (Exception ex) { log.LogWarning("guardrail failed: {Msg}", ex.Message); }

        // Sleep inference: after 10:00, fill last night's log from lock/unlock events + focus samples
        if (DateTime.Now.Hour >= 10 && _sleepInferredFor != DateTime.Today)
        {
            _sleepInferredFor = DateTime.Today;
            try { await InferSleepAsync(db); }
            catch (Exception ex) { log.LogWarning("sleep inference failed: {Msg}", ex.Message); }
        }

        // Habit nags: at NagTime, once per habit per day, snoozeable, quiet during meetings
        try { await NagHabitsAsync(db); }
        catch (Exception ex) { log.LogWarning("habit nag failed: {Msg}", ex.Message); }

        // Focus session expiry
        var expired = await db.FocusSessions
            .Where(f => f.EndedAt == null).ToListAsync();
        foreach (var f in expired.Where(f => f.StartedAt.AddMinutes(f.PlannedMinutes) <= DateTime.UtcNow))
        {
            f.EndedAt = DateTime.UtcNow;
            f.Outcome = "completed";
            await db.SaveChangesAsync();
            bus.Publish(new JarvisEvent("focus.ended",
                new { spoken = $"Focus session complete — {f.PlannedMinutes} minutes logged. Well done, sir." }, "scheduler"));
        }
    }

    /// <summary>
    /// Fill gaps in last night's SleepLog. Priority: voice > lock > inferred. Bed proxy = last
    /// session-lock or focus sample between 19:00 yesterday and 06:00 today; wake proxy = first
    /// unlock or focus sample after 04:00 today. All comparisons in LOCAL time (session.state
    /// events store local; FocusSample.At is UTC — converted here).
    /// </summary>
    private async Task InferSleepAsync(JarvisDb db)
    {
        var row = await db.SleepLogs.FirstOrDefaultAsync(s => s.Date == DateTime.Today);
        if (row is { BedAt: not null, WakeAt: not null }) return;
        if (row is null) { row = new SleepLog { Date = DateTime.Today }; db.SleepLogs.Add(row); }

        var sinceUtc = DateTime.Today.AddHours(-5).ToUniversalTime();   // 19:00 yesterday local
        var sessionEvents = await db.Events.AsNoTracking()
            .Where(e => e.Kind == "session.state" && e.At >= sinceUtc)
            .OrderBy(e => e.At).ToListAsync();

        if (row.BedAt is null)
        {
            var lastLock = sessionEvents.LastOrDefault(e =>
                e.PayloadJson.Contains("locked") && !e.PayloadJson.Contains("unlocked")
                && e.At.ToLocalTime() < DateTime.Today.AddHours(6));
            if (lastLock is not null) { row.BedAt = lastLock.At.ToLocalTime(); row.BedSource = "lock"; }
            else
            {
                var lastSample = await db.FocusSamples.AsNoTracking()
                    .Where(f => f.At >= sinceUtc && f.At < DateTime.Today.AddHours(6).ToUniversalTime())
                    .OrderByDescending(f => f.At).FirstOrDefaultAsync();
                if (lastSample is not null)
                { row.BedAt = lastSample.At.ToLocalTime().AddSeconds(lastSample.DurationSec); row.BedSource = "inferred"; }
            }
        }
        if (row.WakeAt is null)
        {
            var earliestUtc = DateTime.Today.AddHours(4).ToUniversalTime();
            var firstUnlock = sessionEvents.FirstOrDefault(e =>
                e.PayloadJson.Contains("unlocked") && e.At >= earliestUtc);
            if (firstUnlock is not null) { row.WakeAt = firstUnlock.At.ToLocalTime(); row.WakeSource = "lock"; }
            else
            {
                var firstSample = await db.FocusSamples.AsNoTracking()
                    .Where(f => f.At >= earliestUtc).OrderBy(f => f.At).FirstOrDefaultAsync();
                if (firstSample is not null) { row.WakeAt = firstSample.At.ToLocalTime(); row.WakeSource = "inferred"; }
            }
        }
        Modules.SleepModule.Reconcile(row);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>One gentle nag when distraction exceeds the budget in the rolling hour —
    /// weekdays 09:00-19:00 only, 2h cooldown, never mid-meeting.</summary>
    private async Task GuardrailAsync(JarvisDb db)
    {
        if (DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        if (DateTime.Now.Hour is < 9 or >= 19) return;
        if (DateTime.UtcNow - _lastFocusNag < TimeSpan.FromHours(2)) return;

        var budgetMin = int.TryParse((await db.Settings.FindAsync("guardrails.distractionMin"))?.ValueJson.Trim('"'),
            out var b) ? b : 20;
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var distractedSec = await db.FocusSamples.AsNoTracking()
            .Where(f => f.At >= hourAgo && f.Category == "distraction").SumAsync(f => f.DurationSec);
        if (distractedSec < budgetMin * 60) return;

        var recent = await db.FocusSamples.AsNoTracking().OrderByDescending(f => f.At).FirstOrDefaultAsync();
        if (recent?.Category == "teams" && recent.At > DateTime.UtcNow.AddMinutes(-3)) return;

        _lastFocusNag = DateTime.UtcNow;
        bus.Publish(new JarvisEvent("focus.nag", new
        {
            minutes = distractedSec / 60,
            spoken = $"You've had {distractedSec / 60} minutes of distraction this hour — back to it?",
        }, "scheduler"));
    }

    private async Task NagHabitsAsync(JarvisDb db)
    {
        var snoozeRaw = (await db.Settings.FindAsync("habit.snoozeUntil"))?.ValueJson.Trim('"');
        if (snoozeRaw is not null && DateTime.TryParse(snoozeRaw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var snooze) && DateTime.Now < snooze)
            return;

        var habits = await db.Habits.AsNoTracking().Where(h => h.Active && h.NagTime != "").ToListAsync();
        foreach (var h in habits)
        {
            if (_habitNaggedOn.GetValueOrDefault(h.Id) == DateTime.Today) continue;
            if (h.Schedule == "weekdays" && DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (!TimeOnly.TryParse(h.NagTime, out var at)) continue;
            var now = TimeOnly.FromDateTime(DateTime.Now);
            if (now < at || now >= at.AddMinutes(30)) continue;
            if (await db.HabitLogs.AnyAsync(l => l.HabitId == h.Id && l.Date == DateTime.Today)) continue;

            // Don't nag mid-meeting: the most recent focus sample tells us what's on screen
            var recent = await db.FocusSamples.AsNoTracking()
                .OrderByDescending(f => f.At).FirstOrDefaultAsync();
            if (recent?.Category == "teams" && recent.At > DateTime.UtcNow.AddMinutes(-3)) continue;

            _habitNaggedOn[h.Id] = DateTime.Today;
            bus.Publish(new JarvisEvent("habit.nag", new
            {
                habitId = h.Id, habit = h.Name,
                spoken = $"Nudge: {h.Name} isn't logged today. Say 'log {h.Name}' when it's done, or 'nag me later'.",
            }, "scheduler"));
        }
    }

    /// <summary>One backup per day: consistent SQLite snapshot + local commit of the brains folder.</summary>
    private async Task BackupIfNeededAsync()
    {
        var dir = Path.Combine(JarvisPaths.Data, "backups");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"jarvis-{DateTime.Now:yyyyMMdd}.db");
        if (File.Exists(target)) return;

        await using (var src = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={JarvisPaths.DbFile}"))
        await using (var dst = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target}"))
        {
            src.Open(); dst.Open();
            src.BackupDatabase(dst);
        }

        foreach (var old in Directory.GetFiles(dir, "jarvis-*.db").OrderByDescending(f => f).Skip(14))
            try { File.Delete(old); } catch { }

        // Optional local snapshot commit of the markdown brains (never pushed). Off by default:
        // in a cloned repo it would create commits the user did not ask for.
        if (config.GetValue("Jarvis:Backups:CommitBrains", false))
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git",
                $"-C \"{JarvisPaths.Root}\" add data/brains")
            { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
            psi = new System.Diagnostics.ProcessStartInfo("git",
                $"-C \"{JarvisPaths.Root}\" commit --quiet -m \"brains snapshot {DateTime.Now:yyyy-MM-dd}\" -- data/brains")
            { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(10000); // exit 1 = nothing to commit, fine
        }
        catch { }

        bus.Publish(new JarvisEvent("backup.done", new { file = Path.GetFileName(target) }, "scheduler"));
        log.LogInformation("[backup] {File} written", Path.GetFileName(target));
    }
}
