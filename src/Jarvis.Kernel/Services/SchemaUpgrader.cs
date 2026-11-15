using Jarvis.Core;
using Microsoft.Data.Sqlite;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Idempotent raw-SQL migrations for existing databases. EnsureCreated only creates a schema
/// from scratch — it never alters an existing data/jarvis.db, so every new table/column added
/// after V1 ships gets a keyed entry here. Each entry runs exactly once (recorded in
/// schema_upgrades); statements must also be individually idempotent (IF NOT EXISTS) because
/// a fresh DB already has everything via EnsureCreated.
/// </summary>
public static class SchemaUpgrader
{
    // Append-only. Key format: NNN_description. SQL must match what EF Core would create
    // (table name = DbSet property name, column types: TEXT/INTEGER/REAL).
    private static readonly (string Key, string Sql)[] Upgrades =
    [
        ("001_command_logs", """
            CREATE TABLE IF NOT EXISTS "CommandLogs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommandLogs" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "Utterance" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "Tier" TEXT NOT NULL,
                "Confidence" REAL NULL,
                "Intent" TEXT NOT NULL,
                "SlotsJson" TEXT NOT NULL,
                "ProjectSlug" TEXT NULL,
                "Safety" TEXT NOT NULL,
                "Tool" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Error" TEXT NOT NULL,
                "LogRef" TEXT NOT NULL,
                "Spoken" TEXT NOT NULL,
                "AgentRunId" TEXT NULL,
                "FollowUpsJson" TEXT NOT NULL,
                "DurationMs" INTEGER NOT NULL
            )
            """),
        ("002_command_logs_idx", """CREATE INDEX IF NOT EXISTS "IX_CommandLogs_CreatedAt" ON "CommandLogs" ("CreatedAt")"""),
        ("003_agent_run_session", """ALTER TABLE "AgentRuns" ADD COLUMN "SessionId" TEXT NULL"""),
        ("004_person_isvip", """ALTER TABLE "People" ADD COLUMN "IsVip" INTEGER NOT NULL DEFAULT 0"""),
        ("005_sleep_logs", """
            CREATE TABLE IF NOT EXISTS "SleepLogs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SleepLogs" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Date" TEXT NOT NULL, "BedAt" TEXT NULL, "WakeAt" TEXT NULL,
                "BedSource" TEXT NOT NULL, "WakeSource" TEXT NOT NULL, "DurationMin" INTEGER NULL
            )
            """),
        ("006_sleep_logs_idx", """CREATE INDEX IF NOT EXISTS "IX_SleepLogs_Date" ON "SleepLogs" ("Date")"""),
        ("007_habits", """
            CREATE TABLE IF NOT EXISTS "Habits" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Habits" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Name" TEXT NOT NULL, "Aliases" TEXT NOT NULL, "Schedule" TEXT NOT NULL,
                "NagTime" TEXT NOT NULL, "Active" INTEGER NOT NULL
            )
            """),
        ("008_habit_logs", """
            CREATE TABLE IF NOT EXISTS "HabitLogs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_HabitLogs" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "HabitId" TEXT NOT NULL, "Date" TEXT NOT NULL, "Note" TEXT NOT NULL
            )
            """),
        ("009_habit_logs_idx", """CREATE INDEX IF NOT EXISTS "IX_HabitLogs_HabitId_Date" ON "HabitLogs" ("HabitId", "Date")"""),
        ("010_route_corrections", """
            CREATE TABLE IF NOT EXISTS "RouteCorrections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_RouteCorrections" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Utterance" TEXT NOT NULL, "WrongIntent" TEXT NOT NULL, "EmbeddingJson" TEXT NOT NULL
            )
            """),
        ("011_council_sessions", """
            CREATE TABLE IF NOT EXISTS "CouncilSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CouncilSessions" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "Mode" TEXT NOT NULL, "Category" TEXT NOT NULL,
                "OriginalUtterance" TEXT NOT NULL, "FramedDecision" TEXT NOT NULL,
                "Status" TEXT NOT NULL, "AgentKeysCsv" TEXT NOT NULL,
                "ContextFactsJson" TEXT NOT NULL, "EndedAt" TEXT NULL,
                "ProjectId" TEXT NULL, "DecisionId" TEXT NULL,
                "ResultJson" TEXT NOT NULL, "FinalRecommendation" TEXT NOT NULL,
                "DissentSummary" TEXT NOT NULL, "NextAction" TEXT NOT NULL,
                "Confidence" INTEGER NOT NULL, "TagsCsv" TEXT NOT NULL
            )
            """),
        ("012_council_turns", """
            CREATE TABLE IF NOT EXISTS "CouncilTurns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CouncilTurns" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL, "Seq" INTEGER NOT NULL,
                "Speaker" TEXT NOT NULL, "Phase" TEXT NOT NULL, "Text" TEXT NOT NULL
            )
            """),
        ("013_council_turns_idx", """CREATE INDEX IF NOT EXISTS "IX_CouncilTurns_SessionId" ON "CouncilTurns" ("SessionId")"""),
        ("014_council_votes", """
            CREATE TABLE IF NOT EXISTS "CouncilVotes" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CouncilVotes" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL, "AgentKey" TEXT NOT NULL,
                "Position" TEXT NOT NULL, "Confidence" INTEGER NOT NULL,
                "Reason" TEXT NOT NULL, "PrimaryRisk" TEXT NOT NULL,
                "RecommendedAction" TEXT NOT NULL, "Weight" REAL NOT NULL, "Vetoed" INTEGER NOT NULL
            )
            """),
        ("015_council_votes_idx", """CREATE INDEX IF NOT EXISTS "IX_CouncilVotes_SessionId" ON "CouncilVotes" ("SessionId")"""),
        ("016_council_actions", """
            CREATE TABLE IF NOT EXISTS "CouncilActions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CouncilActions" PRIMARY KEY,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL, "Kind" TEXT NOT NULL,
                "RefId" TEXT NOT NULL, "Status" TEXT NOT NULL
            )
            """),
    ];

    public static void Apply(ILogger log)
    {
        using var conn = new SqliteConnection($"Data Source={JarvisPaths.DbFile}");
        conn.Open();
        Exec(conn, "CREATE TABLE IF NOT EXISTS schema_upgrades (key TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");

        var applied = new HashSet<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key FROM schema_upgrades";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) applied.Add(rd.GetString(0));
        }

        foreach (var (key, sql) in Upgrades)
        {
            if (applied.Contains(key)) continue;
            Exec(conn, sql);
            using var mark = conn.CreateCommand();
            mark.CommandText = "INSERT INTO schema_upgrades (key, applied_at) VALUES ($k, $at)";
            mark.Parameters.AddWithValue("$k", key);
            mark.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
            log.LogInformation("schema upgrade applied: {Key}", key);
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Fresh DBs get new columns from EnsureCreated before the keyed ALTER runs — already done.
        }
    }
}
