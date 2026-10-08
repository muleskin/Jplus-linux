using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jplus;

/// <summary>
/// The Projects tab: a JOIN of what already exists, nothing new captured.
///
/// Every fact surfaced here is already recorded elsewhere — SessionWatch knows
/// the conversations, RunStore the runs, Builds the plan, RepoRead the
/// repository. This module only joins them by project name and orders them.
///
/// Two passes, split by cost:
///   * `BuildProjectViews` is the CHEAP join — sessions and runs already held in
///     memory plus one directory-exists check per project. It backs the list.
///   * `RepoSummary` / `BuildSummary` are the EXPENSIVE half — a bounded walk and
///     a plan-file read — only for the one project a user clicked into.
///
/// Three honesty rules:
///   1. A project with no runs is `runs: []`, never a count coerced to 0.
///   2. A directory can be gone by the time someone clicks — `directory_exists`
///      says so plainly.
///   3. JARVIS's own spawned runs are not the user's conversations: `sessions`
///      is an argument, and the caller passes `Server.SnapshotOrEmpty()` (which
///      already filters them), not the raw snapshot.
/// </summary>
public static partial class ProjectsView
{
    // "Something is actively happening here right now". `needs_you` is its own,
    // higher-priority bucket and deliberately not included.
    public static readonly HashSet<string> ActiveSessionStates = new(StringComparer.Ordinal)
    {
        SessionWatch.Working, SessionWatch.Shell,
    };

    // Conversations that have STOPPED being conversations. Counted separately
    // (not removed): "what does this project know about" and "is anything going
    // on here" both have a reader.
    public static readonly HashSet<string> DeadSessionStates = new(StringComparer.Ordinal)
    {
        SessionWatch.Gone, SessionWatch.Fresh,
    };

    // How many of a project's most recent runs the detail view carries.
    public const int DetailRunLimit = 20;

    /// <summary>One project: every conversation and run known for it, joined.</summary>
    public class ProjectView
    {
        public string Name { get; set; } = "";
        public string PrimaryPath { get; set; } = "";
        public List<string> Paths { get; set; } = [];
        public bool DirectoryExists { get; set; }
        public List<SessionWatch.SessionState> Sessions { get; set; } = [];
        public List<Dictionary<string, object?>> Runs { get; set; } = [];
        public List<SessionWatch.SessionState> NeedsYou { get; set; } = [];
        public bool Active { get; set; }
        public double? LastActivity { get; set; }

        public Dictionary<string, object?>? LatestRun => Runs.Count > 0 ? Runs[0] : null;
    }

    /// <summary>A run-row value as a number (rows come back as double/long/string/JSON).</summary>
    public static double? AsDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        decimal m => (double)m,
        short s => s,
        uint u => u,
        ulong ul => ul,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        JsonValue jv when jv.TryGetValue<double>(out var jd) => jd,
        _ => null,
    };

    private static string? AsString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        _ => v.ToString(),
    };

    private static object? Get(Dictionary<string, object?> r, string key) =>
        r.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// When this conversation last did something: `since` (when the CURRENT state
    /// began) over `started`, so a conversation idle for days does not out-rank
    /// one that just started.
    /// </summary>
    public static double? SessionActivity(SessionWatch.SessionState s) => s.Since ?? s.Started;

    /// <summary>
    /// Which of a project's directories is "the" repo location. A project can
    /// live in several (two checkouts, a worktree under `.claude/worktrees/`);
    /// the most RECENTLY active wins, ties alphabetical so it is stable.
    /// </summary>
    public static string PickPrimaryPath(List<string> paths, IEnumerable<SessionWatch.SessionState> sessions,
        IEnumerable<Dictionary<string, object?>> runs)
    {
        if (paths.Count == 0) return "";
        if (paths.Count == 1) return paths[0];
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            if (string.IsNullOrEmpty(s.Cwd)) continue;
            var t = SessionActivity(s) ?? 0.0;
            scores[s.Cwd] = Math.Max(scores.GetValueOrDefault(s.Cwd, 0.0), t);
        }
        foreach (var r in runs)
        {
            var p = AsString(Get(r, "project_path"));
            if (string.IsNullOrEmpty(p)) continue;
            var t = AsDouble(Get(r, "created_at")) ?? 0.0;
            scores[p] = Math.Max(scores.GetValueOrDefault(p, 0.0), t);
        }
        return paths
            .OrderBy(p => -scores.GetValueOrDefault(p, 0.0))
            .ThenBy(p => p, StringComparer.Ordinal)
            .First();
    }

    private sealed class Bucket
    {
        public List<SessionWatch.SessionState> Sessions { get; } = [];
        public List<Dictionary<string, object?>> Runs { get; } = [];
        public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The JOIN: every project name known to either a conversation or a run,
    /// folded into one summary each, ordered by what deserves attention first.
    /// A pure function over data the caller already fetched — no I/O beyond one
    /// `exists` check per project, which tests can stub.
    /// </summary>
    public static List<ProjectView> BuildProjectViews(
        IEnumerable<SessionWatch.SessionState> sessions,
        IEnumerable<Dictionary<string, object?>> runs,
        Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        // Insertion-ordered, like a Python dict.
        var order = new List<string>();
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        Bucket BucketOf(string name)
        {
            if (!buckets.TryGetValue(name, out var b))
            {
                b = new Bucket();
                buckets[name] = b;
                order.Add(name);
            }
            return b;
        }

        foreach (var s in sessions)
        {
            if (string.IsNullOrEmpty(s.Project)) continue;
            var b = BucketOf(s.Project);
            b.Sessions.Add(s);
            if (!string.IsNullOrEmpty(s.Cwd)) b.Paths.Add(s.Cwd);
        }

        foreach (var r in runs)
        {
            var name = AsString(Get(r, "project_name"));
            if (string.IsNullOrEmpty(name)) continue;
            var b = BucketOf(name);
            b.Runs.Add(r);
            var path = AsString(Get(r, "project_path"));
            if (!string.IsNullOrEmpty(path)) b.Paths.Add(path);
        }

        var views = new List<ProjectView>();
        foreach (var name in order)
        {
            var b = buckets[name];
            var paths = b.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var runsSorted = b.Runs.OrderByDescending(r => AsDouble(Get(r, "created_at")) ?? 0.0).ToList();
            var sessionsList = b.Sessions;
            var needsYou = sessionsList.Where(s => s.State == SessionWatch.NeedsYou).ToList();
            var activeSession = sessionsList.Any(s => ActiveSessionStates.Contains(s.State ?? ""));
            var activeRun = runsSorted.Any(r => RunStore.RunStatus.Active.Contains(AsString(Get(r, "status")) ?? ""));

            var candidates = sessionsList.Select(SessionActivity).Where(t => t is not null).Select(t => t!.Value).ToList();
            candidates.AddRange(runsSorted.Select(r => AsDouble(Get(r, "created_at")))
                .Where(t => t is not null).Select(t => t!.Value));
            double? lastActivity = candidates.Count > 0 ? candidates.Max() : null;

            var primaryPath = PickPrimaryPath(paths, sessionsList, runsSorted);
            bool dirExists;
            try
            {
                dirExists = primaryPath.Length > 0 && exists(primaryPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                dirExists = false;
            }
            views.Add(new ProjectView
            {
                Name = name,
                PrimaryPath = primaryPath,
                Paths = paths,
                DirectoryExists = dirExists,
                Sessions = sessionsList,
                Runs = runsSorted.Take(DetailRunLimit).ToList(),
                NeedsYou = needsYou,
                Active = activeSession || activeRun,
                LastActivity = lastActivity,
            });
        }

        return OrderProjects(views);
    }

    /// <summary>
    /// Anything needing the user first, then anything active, then the rest by
    /// recency. Never alphabetical — that would bury the project someone is
    /// actually waiting on below one whose name starts with "a".
    /// </summary>
    public static List<ProjectView> OrderProjects(IEnumerable<ProjectView> views) =>
        views
            .OrderBy(v => v.NeedsYou.Count > 0 ? 0 : (v.Active ? 1 : 2))
            .ThenBy(v => -(v.LastActivity ?? 0.0))
            .ThenBy(v => v.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

    // --- The expensive half: only for the one project a user clicked into ------

    /// <summary>
    /// `repo_overview`'s own facts, exactly as it computes them — no second
    /// repository reader. `exists: false` when the directory is gone.
    /// </summary>
    public static Dictionary<string, object?> RepoSummary(string? path, string name)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return new Dictionary<string, object?> { ["exists"] = false, ["headline"] = "", ["body"] = "" };
        var (headline, body) = RepoRead.Overview(path, name);
        return new Dictionary<string, object?> { ["exists"] = true, ["headline"] = headline, ["body"] = body };
    }

    public static Dictionary<string, object?> PlanTaskDict(Builds.PlanTask t) => new()
    {
        ["number"] = t.Number,
        ["title"] = t.Title,
        ["steps_done"] = t.StepsDone,
        ["steps_total"] = t.StepsTotal,
        ["done"] = t.Done,
    };

    private static bool AnyMd(string root, string dir)
    {
        try
        {
            var full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(full) && Builds.GlobMd(full).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Spec presence, plan presence, and progress off the plan's own checkboxes —
    /// reusing `Builds.GetPlanProgress` rather than re-reading the plan here.
    /// </summary>
    public static Dictionary<string, object?> BuildSummary(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return new Dictionary<string, object?> { ["has_spec"] = false, ["has_plan"] = false, ["progress"] = null };

        var hasSpec = AnyMd(path, Builds.SpecDir);
        var hasPlan = AnyMd(path, Builds.PlanDir);

        var progress = Builds.GetPlanProgress(path);
        Dictionary<string, object?>? progressDict = null;
        if (progress is not null)
        {
            var current = progress.Current;
            progressDict = new Dictionary<string, object?>
            {
                ["total"] = progress.Total,
                ["done"] = progress.Done,
                ["finished"] = progress.Finished,
                ["current_task"] = current is not null ? PlanTaskDict(current) : null,
                ["tasks"] = progress.Tasks.Select(PlanTaskDict).ToList(),
            };
        }
        return new Dictionary<string, object?> { ["has_spec"] = hasSpec, ["has_plan"] = hasPlan, ["progress"] = progressDict };
    }

    // --- JSON shapes -------------------------------------------------------------

    /// <summary>The cheap shape: everything the project list needs, nothing that walked a filesystem.</summary>
    public static Dictionary<string, object?> ListItem(ProjectView v) => new()
    {
        ["name"] = v.Name,
        ["primary_path"] = v.PrimaryPath,
        ["paths"] = v.Paths,
        ["directory_exists"] = v.DirectoryExists,
        ["session_count"] = v.Sessions.Count,
        // Of those, the ones still conversations. Zero here with a non-zero
        // `session_count` is a project that has finished, not one idling.
        ["live_session_count"] = v.Sessions.Count(s => !DeadSessionStates.Contains(s.State ?? "")),
        ["needs_you_count"] = v.NeedsYou.Count,
        ["active"] = v.Active,
        ["last_activity"] = v.LastActivity,
        ["latest_run"] = v.LatestRun,
    };

    /// <summary>The full shape for the detail pane.</summary>
    public static Dictionary<string, object?> DetailItem(ProjectView v, Dictionary<string, object?> repo,
        Dictionary<string, object?> build)
    {
        var output = ListItem(v);
        output["sessions"] = v.Sessions.Select(s => (object?)SessionWatch.SessionToDict(s)).ToList();
        output["runs"] = v.Runs;
        output["repo"] = repo;
        output["build"] = build;
        return output;
    }
}
