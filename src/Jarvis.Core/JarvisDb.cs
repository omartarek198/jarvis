using Microsoft.EntityFrameworkCore;

namespace Jarvis.Core;

public class JarvisDb(DbContextOptions<JarvisDb> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Repo> Repos => Set<Repo>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Commitment> Commitments => Set<Commitment>();
    public DbSet<Idea> Ideas => Set<Idea>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingAction> MeetingActions => Set<MeetingAction>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<PlaywrightRun> PlaywrightRuns => Set<PlaywrightRun>();
    public DbSet<FocusSample> FocusSamples => Set<FocusSample>();
    public DbSet<FocusSession> FocusSessions => Set<FocusSession>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<MachineCommand> MachineCommands => Set<MachineCommand>();
    public DbSet<Briefing> Briefings => Set<Briefing>();
    public DbSet<AuditEvent> Events => Set<AuditEvent>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Fact> Facts => Set<Fact>();
    public DbSet<CommandLog> CommandLogs => Set<CommandLog>();
    public DbSet<SleepLog> SleepLogs => Set<SleepLog>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<RouteCorrection> RouteCorrections => Set<RouteCorrection>();
    public DbSet<CouncilSession> CouncilSessions => Set<CouncilSession>();
    public DbSet<CouncilTurn> CouncilTurns => Set<CouncilTurn>();
    public DbSet<CouncilVote> CouncilVotes => Set<CouncilVote>();
    public DbSet<CouncilAction> CouncilActions => Set<CouncilAction>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CouncilTurn>().HasIndex(t => t.SessionId);
        b.Entity<CouncilVote>().HasIndex(v => v.SessionId);
        b.Entity<Setting>().HasKey(s => s.Key);
        b.Entity<AuditEvent>().HasKey(e => e.Id);
        b.Entity<Project>().HasIndex(p => p.Slug).IsUnique();
        b.Entity<FocusSample>().HasIndex(f => f.At);
        b.Entity<AuditEvent>().HasIndex(e => e.At);
        b.Entity<CommandLog>().HasIndex(c => c.CreatedAt);
        b.Entity<SleepLog>().HasIndex(s => s.Date);
        b.Entity<HabitLog>().HasIndex(h => new { h.HabitId, h.Date });
    }
}

public static class JarvisPaths
{
    /// <summary>Repo root = nearest ancestor containing Jarvis.sln; falls back to %USERPROFILE%\.jarvis</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Jarvis.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jarvis");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string Data { get { var p = Path.Combine(Root, "data"); Directory.CreateDirectory(p); return p; } }
    public static string Brains { get { var p = Path.Combine(Data, "brains"); Directory.CreateDirectory(p); return p; } }
    public static string Voice => Path.Combine(Root, "voice");
    public static string DbFile => Path.Combine(Data, "jarvis.db");
}
