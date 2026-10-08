using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// The single source of truth for Claude Code runs (run_store.py).
///
/// Every execution path writes here. The dashboard only reads. State
/// transitions are persisted before they are announced.
///
/// Rows come back as `Dictionary&lt;string, object?&gt;` keyed by column name,
/// with SQLite's own types: TEXT → string, INTEGER → long, REAL → double,
/// NULL → null.
/// </summary>
public static partial class RunStore
{
    public static readonly ILogger Log = Py.Log("jarvis.run_store");

    public static class RunStatus
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string TimedOut = "timed_out";
        public const string Cancelled = "cancelled";

        public static readonly string[] All = [Queued, Running, Succeeded, Failed, TimedOut, Cancelled];
        public static readonly HashSet<string> Terminal = [Succeeded, Failed, TimedOut, Cancelled];
        public static readonly HashSet<string> Active = [Queued, Running];
    }

    public static readonly HashSet<string> Updatable =
    [
        "status", "result_text", "summary", "error", "exit_code", "pid",
        "cost_usd", "input_tokens", "output_tokens", "cache_read_tokens",
        "cache_creation_tokens", "num_turns", "model", "requested_model",
        "started_at", "ended_at", "project_path", "project_name", "is_error",
    ];

    /// <summary>
    /// An open connection with the same pragmas as the Python. `Default Timeout=5`
    /// matches sqlite3's 5 s busy timeout.
    /// </summary>
    public static SqliteConnection Connect()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DataPaths.DbPath(),
            DefaultTimeout = 5,
        }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA foreign_keys=ON");
        return conn;
    }

    // ── tiny ADO helpers (shared with Migrations) ───────────────────────────

    public static SqliteCommand Cmd(SqliteConnection conn, string sql, params object?[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Cmd(conn, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static List<Dictionary<string, object?>> Query(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Cmd(conn, sql, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static Dictionary<string, object?>? QueryOne(SqliteConnection conn, string sql, params object?[] args)
    {
        var rows = Query(conn, sql, args);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>`?, ?, ?` placeholders for args starting at `start`.</summary>
    private static string Placeholders(int start, int count) =>
        string.Join(",", Enumerable.Range(start, count).Select(i => $"@p{i}"));

    // ── schema ──────────────────────────────────────────────────────────────

    public static void InitDb()
    {
        using var conn = Connect();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS runs (
                id            TEXT PRIMARY KEY,
                project_name  TEXT NOT NULL,
                project_path  TEXT NOT NULL,
                prompt        TEXT NOT NULL,
                origin        TEXT NOT NULL,
                status        TEXT NOT NULL,
                resume_from   TEXT,
                result_text   TEXT DEFAULT '',
                summary       TEXT DEFAULT '',
                error         TEXT DEFAULT '',
                exit_code     INTEGER,
                pid           INTEGER,
                cost_usd      REAL    DEFAULT 0,
                input_tokens  INTEGER DEFAULT 0,
                output_tokens INTEGER DEFAULT 0,
                cache_read_tokens     INTEGER DEFAULT 0,
                cache_creation_tokens INTEGER DEFAULT 0,
                num_turns     INTEGER DEFAULT 0,
                model         TEXT DEFAULT '',
                requested_model TEXT DEFAULT '',
                is_error      INTEGER DEFAULT 0,
                created_at    REAL NOT NULL,
                started_at    REAL,
                ended_at      REAL
            );
            CREATE INDEX IF NOT EXISTS idx_runs_status  ON runs(status);
            CREATE INDEX IF NOT EXISTS idx_runs_created ON runs(created_at DESC);

            CREATE TABLE IF NOT EXISTS run_events (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id  TEXT    NOT NULL REFERENCES runs(id),
                seq     INTEGER NOT NULL,
                ts      REAL    NOT NULL,
                kind    TEXT    NOT NULL,
                payload TEXT    NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_run_seq
                ON run_events(run_id, seq);

            CREATE TABLE IF NOT EXISTS steers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                voice_name TEXT NOT NULL,
                project TEXT NOT NULL,
                prompt TEXT NOT NULL,
                outcome TEXT NOT NULL,
                created_at REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_steers_created ON steers(created_at DESC);
            """);

        // `CREATE TABLE IF NOT EXISTS` never alters a table that already
        // exists, so a live jarvis.db predating a column needs an explicit
        // backfill. Same PRAGMA-table_info-then-ALTER pattern as Migrations.
        var cols = Query(conn, "PRAGMA table_info(runs)").Select(r => (string)r["name"]!).ToHashSet();
        if (!cols.Contains("requested_model"))
            Exec(conn, "ALTER TABLE runs ADD COLUMN requested_model TEXT DEFAULT ''");
        if (!cols.Contains("is_error"))
            Exec(conn, "ALTER TABLE runs ADD COLUMN is_error INTEGER DEFAULT 0");
    }

    // ── runs ────────────────────────────────────────────────────────────────

    public static string CreateRun(string prompt, string projectName, string projectPath,
                                   string origin, string? resumeFrom = null)
    {
        var runId = Guid.NewGuid().ToString();
        using (var conn = Connect())
        {
            Exec(conn,
                "INSERT INTO runs (id, project_name, project_path, prompt, origin, " +
                "status, resume_from, created_at) VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",
                runId, projectName, projectPath, prompt, origin,
                RunStatus.Queued, resumeFrom, Py.Now());
        }
        Log.LogInformation("run {RunId} created ({Project}, origin={Origin})", runId, projectName, origin);
        return runId;
    }

    public static Dictionary<string, object?>? GetRun(string runId)
    {
        using var conn = Connect();
        return QueryOne(conn, "SELECT * FROM runs WHERE id=@p0", runId);
    }

    /// <summary>
    /// Every run id ever recorded.
    ///
    /// A run id is ALSO the Claude Code session id (RunExecutor passes it as
    /// `--session-id`), so this is the exact set of roster sessions JARVIS
    /// started himself — what lets the voice path tell its own one-shot runs
    /// apart from the user's real conversations.
    /// </summary>
    public static HashSet<string> AllRunIds()
    {
        using var conn = Connect();
        return Query(conn, "SELECT id FROM runs").Select(r => (string)r["id"]!).ToHashSet();
    }

    public static List<Dictionary<string, object?>> ListRuns(IEnumerable<string>? status = null,
        string? project = null, int limit = 50, double? before = null)
    {
        var sql = "SELECT * FROM runs WHERE 1=1";
        var args = new List<object?>();
        var statuses = status?.ToList();
        if (statuses is { Count: > 0 })
        {
            sql += $" AND status IN ({Placeholders(args.Count, statuses.Count)})";
            args.AddRange(statuses);
        }
        if (!string.IsNullOrEmpty(project))
        {
            sql += $" AND project_name LIKE @p{args.Count}";
            args.Add($"%{project}%");
        }
        if (before is not null)
        {
            sql += $" AND created_at < @p{args.Count}";
            args.Add(before.Value);
        }
        sql += $" ORDER BY created_at DESC LIMIT @p{args.Count}";
        args.Add(limit);

        using var conn = Connect();
        return Query(conn, sql, args.ToArray());
    }

    /// <summary>`update_run(run_id, **fields)`. Only columns in `Updatable`; bools are stored as 0/1.</summary>
    public static void UpdateRun(string runId, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var list = fields.ToList();
        if (list.Count == 0) return;
        var unknown = list.Select(kv => kv.Key).Where(k => !Updatable.Contains(k))
            .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"not updatable: [{string.Join(", ", unknown.Select(k => $"'{k}'"))}]");
        var assignments = string.Join(", ", list.Select((kv, i) => $"{kv.Key}=@p{i}"));
        var args = list.Select(kv => kv.Value is bool b ? (b ? 1L : 0L) : kv.Value).ToList();
        args.Add(runId);
        using var conn = Connect();
        Exec(conn, $"UPDATE runs SET {assignments} WHERE id=@p{list.Count}", args.ToArray());
    }

    /// <summary>`update_run(run_id, a=1, b=2)` as `UpdateRun(id, ("a", 1), ("b", 2))`.</summary>
    public static void UpdateRun(string runId, params (string Key, object? Value)[] fields) =>
        UpdateRun(runId, fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)));

    // ── events ──────────────────────────────────────────────────────────────

    public static int NextSeq(string runId)
    {
        using var conn = Connect();
        var row = QueryOne(conn, "SELECT COALESCE(MAX(seq), 0) AS m FROM run_events WHERE run_id=@p0", runId);
        return Convert.ToInt32(row?["m"] ?? 0L) + 1;
    }

    public static void AppendEvent(string runId, int seq, string kind, string payload) =>
        AppendEvents(runId, [(seq, kind, payload)]);

    /// <summary>
    /// Insert many events in one transaction. The executor batches through this
    /// so a chatty build costs one commit per batch rather than one per event.
    /// </summary>
    public static void AppendEvents(string runId, IEnumerable<(int Seq, string Kind, string Payload)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var now = Py.Now();
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO run_events (run_id, seq, ts, kind, payload) " +
                          "VALUES (@p0,@p1,@p2,@p3,@p4)";
        var pRun = cmd.Parameters.Add("@p0", SqliteType.Text);
        var pSeq = cmd.Parameters.Add("@p1", SqliteType.Integer);
        var pTs = cmd.Parameters.Add("@p2", SqliteType.Real);
        var pKind = cmd.Parameters.Add("@p3", SqliteType.Text);
        var pPayload = cmd.Parameters.Add("@p4", SqliteType.Text);
        foreach (var (seq, kind, payload) in list)
        {
            pRun.Value = runId;
            pSeq.Value = seq;
            pTs.Value = now;
            pKind.Value = kind;
            pPayload.Value = payload;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static List<Dictionary<string, object?>> GetEvents(string runId, int afterSeq = 0, int limit = 200)
    {
        using var conn = Connect();
        return Query(conn,
            "SELECT * FROM run_events WHERE run_id=@p0 AND seq>@p1 ORDER BY seq ASC LIMIT @p2",
            runId, afterSeq, limit);
    }

    public static int CountEvents(string runId)
    {
        using var conn = Connect();
        var row = QueryOne(conn, "SELECT COUNT(*) AS n FROM run_events WHERE run_id=@p0", runId);
        return Convert.ToInt32(row?["n"] ?? 0L);
    }

    /// <summary>Mark runs left active by a crashed server as failed.</summary>
    public static int SweepStaleRuns()
    {
        int count;
        using (var conn = Connect())
        {
            count = Exec(conn, "UPDATE runs SET status=@p0, error=@p1, ended_at=@p2 WHERE status IN (@p3,@p4)",
                RunStatus.Failed, "server restarted during run", Py.Now(),
                RunStatus.Queued, RunStatus.Running);
        }
        if (count > 0)
            Log.LogWarning("swept {Count} stale run(s) after restart", count);
        return count;
    }

    public static readonly Dictionary<string, int> Periods = new()
    {
        ["day"] = 86400, ["week"] = 86400 * 7, ["month"] = 86400 * 30,
    };

    public static Dictionary<string, object?> Stats(string period = "day")
    {
        var cutoff = Py.Now() - (Periods.TryGetValue(period, out var s) ? s : 86400);
        List<Dictionary<string, object?>> rows;
        using (var conn = Connect())
        {
            rows = Query(conn,
                "SELECT status, COUNT(*) AS n, " +
                "COALESCE(SUM(cost_usd),0) AS cost, " +
                "COALESCE(SUM(input_tokens),0) AS inp, " +
                "COALESCE(SUM(output_tokens),0) AS out " +
                "FROM runs WHERE created_at >= @p0 GROUP BY status",
                cutoff);
        }

        var byStatus = new Dictionary<string, object?>();
        foreach (var st in RunStatus.All) byStatus[st] = 0L;
        double totalCost = 0;
        long totalIn = 0, totalOut = 0;
        foreach (var r in rows)
        {
            byStatus[(string)r["status"]!] = Convert.ToInt64(r["n"]);
            totalCost += Convert.ToDouble(r["cost"] ?? 0.0);
            totalIn += Convert.ToInt64(r["inp"] ?? 0L);
            totalOut += Convert.ToInt64(r["out"] ?? 0L);
        }

        return new Dictionary<string, object?>
        {
            ["period"] = period,
            ["by_status"] = byStatus,
            ["total_runs"] = byStatus.Values.Sum(v => Convert.ToInt64(v)),
            ["total_cost_usd"] = Math.Round(totalCost, 6),
            ["total_input_tokens"] = totalIn,
            ["total_output_tokens"] = totalOut,
        };
    }

    // ── steers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every steer is recorded, including the refused ones — 'did you send
    /// that?' must have an answer.
    /// </summary>
    public static void RecordSteer(string sessionId, string voiceName, string project,
                                   string prompt, string outcome)
    {
        using var conn = Connect();
        Exec(conn,
            "INSERT INTO steers (session_id, voice_name, project, prompt, outcome, " +
            "created_at) VALUES (@p0, @p1, @p2, @p3, @p4, @p5)",
            sessionId, voiceName, project, prompt, outcome, Py.Now());
    }

    public static List<Dictionary<string, object?>> ListSteers(int limit = 50)
    {
        using var conn = Connect();
        return Query(conn, "SELECT * FROM steers ORDER BY created_at DESC LIMIT @p0", limit);
    }
}
