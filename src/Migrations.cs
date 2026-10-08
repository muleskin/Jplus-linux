using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// migrations/001_dispatches_to_runs.py — backfill legacy `dispatches` rows
/// into `runs`.
///
/// The dispatches table is left in place, unread, for one release.
/// Idempotent: rows already migrated are skipped via the marker column.
/// </summary>
public static partial class Migrations
{
    public static readonly ILogger Log = Py.Log("jarvis.migrations");

    public static readonly Dictionary<string, string> StatusMap = new()
    {
        ["pending"] = RunStore.RunStatus.Queued,
        ["queued"] = RunStore.RunStatus.Queued,
        ["building"] = RunStore.RunStatus.Running,
        ["planning"] = RunStore.RunStatus.Running,
        ["working"] = RunStore.RunStatus.Running,
        ["running"] = RunStore.RunStatus.Running,
        ["completed"] = RunStore.RunStatus.Succeeded,
        ["done"] = RunStore.RunStatus.Succeeded,
        ["succeeded"] = RunStore.RunStatus.Succeeded,
        ["failed"] = RunStore.RunStatus.Failed,
        ["error"] = RunStore.RunStatus.Failed,
        ["timeout"] = RunStore.RunStatus.TimedOut,
        ["timed_out"] = RunStore.RunStatus.TimedOut,
        ["cancelled"] = RunStore.RunStatus.Cancelled,
    };

    public static bool HasTable(SqliteConnection conn, string name) =>
        RunStore.QueryOne(conn, "SELECT name FROM sqlite_master WHERE type='table' AND name=@p0", name) is not null;

    /// <summary>Run migration 001. Returns how many dispatch rows were moved into runs.</summary>
    public static int Migrate001()
    {
        int migrated = 0;
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = DataPaths.DbPath(), DefaultTimeout = 5 }.ToString()))
        {
            conn.Open();

            if (!HasTable(conn, "dispatches"))
                return 0;

            var cols = RunStore.Query(conn, "PRAGMA table_info(dispatches)")
                .Select(r => (string)r["name"]!).ToHashSet();
            if (!cols.Contains("migrated_run_id"))
                RunStore.Exec(conn, "ALTER TABLE dispatches ADD COLUMN migrated_run_id TEXT");

            var rows = RunStore.Query(conn, "SELECT * FROM dispatches WHERE migrated_run_id IS NULL");

            // One transaction, committed once at the end, as the Python did.
            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                var runId = Guid.NewGuid().ToString();
                var raw = (Col(row, "status") as string ?? "").ToLowerInvariant();
                var status = StatusMap.TryGetValue(raw, out var mapped) ? mapped : RunStore.RunStatus.Failed;
                using (var cmd = RunStore.Cmd(conn,
                           "INSERT INTO runs (id, project_name, project_path, prompt, origin, " +
                           "status, result_text, summary, created_at, started_at, ended_at) " +
                           "VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)",
                           runId, Col(row, "project_name"), Col(row, "project_path"),
                           Col(row, "original_prompt"), "voice", status,
                           OrEmpty(Col(row, "claude_response")), OrEmpty(Col(row, "summary")),
                           Col(row, "created_at"), Col(row, "created_at"), Col(row, "completed_at")))
                {
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = RunStore.Cmd(conn, "UPDATE dispatches SET migrated_run_id=@p0 WHERE id=@p1",
                           runId, Col(row, "id")))
                {
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                migrated++;
            }
            tx.Commit();
        }

        if (migrated > 0)
            Log.LogInformation("migrated {Count} dispatch row(s) into runs", migrated);
        return migrated;
    }

    /// <summary>`row["col"]` — a missing column throws, like sqlite3.Row's IndexError.</summary>
    private static object? Col(Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) ? v : throw new KeyNotFoundException($"No item with that key: {name}");

    /// <summary>`value or ""`.</summary>
    private static object OrEmpty(object? v) => v switch
    {
        null => "",
        string s when s.Length == 0 => "",
        _ => v,
    };
}
