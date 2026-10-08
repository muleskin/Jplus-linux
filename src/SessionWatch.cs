using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Watch every Claude Code session on this machine (port of session_watch.py).
///
/// Three levels, and the distinction matters: a *process* is one `claude`; a
/// *conversation* is one `sessionId` and one transcript; a *project* is one cwd.
/// Measured on 2026-09-03, 17 live processes were 14 conversations in 10
/// projects — three processes shared one hammer conversation. Counting processes
/// is what made JARVIS miscount sessions out loud, so speech uses conversations.
///
/// Pure filesystem reading: no server imports, no LLM. Every parse is
/// failure-tolerant, because these files are written live by other processes and
/// will be read mid-write.
///
/// Windows notes (measured on this machine, 2026-10): the roster lives in
/// `%USERPROFILE%\.claude\sessions\&lt;pid&gt;.json` with the same field names as on
/// macOS (`pid`, `sessionId`, `cwd`, `startedAt`, `status`, `statusUpdatedAt`,
/// `entrypoint`, `messagingSocketPath`, …); `messagingSocketPath` is a named pipe
/// (`\\.\pipe\LOCAL\cc-msg-…`), not a Unix socket file. Project directories
/// encode `C:\Users\Jedd\Desktop` as `C--Users-Jedd-Desktop` — every
/// non-alphanumeric character, including `:` and `\`, becomes `-`.
/// </summary>
public static partial class SessionWatch
{
    private static readonly ILogger Log = Py.Log("session_watch");

    // Both roots must be read: the CLI's default and the one Orcha sets via
    // CLAUDE_CONFIG_DIR. Neither root may be dropped.
    public static readonly string[] DefaultRoots = ["~/.claude", "~/.claude-orcha"];

    /// <summary>Every directory that may hold a `sessions/` roster, in priority order.</summary>
    public static List<string> ConfigRoots()
    {
        var roots = DefaultRoots.Select(r => Py.ExpandUser(r)).ToList();
        var extra = Py.Getenv("JARVIS_CLAUDE_CONFIG_DIRS", "") ?? "";
        foreach (var raw0 in extra.Split(Path.PathSeparator))
        {
            var raw = raw0.Trim();
            if (raw.Length == 0) continue;
            var p = Py.ExpandUser(raw);
            if (!roots.Any(r => SamePathText(r, p))) roots.Add(p);
        }
        return roots;
    }

    /// <summary>
    /// True if the process exists. `pid` must be a positive integer: 0 and
    /// negative pids are not real processes, so both are rejected up front.
    /// </summary>
    public static bool PidAlive(long pid)
    {
        if (pid <= 0 || pid > int.MaxValue) return false;
        return Py.PidAlive((int)pid);
    }

    private static readonly Regex NonAlnum = new("[^a-zA-Z0-9]", RegexOptions.CultureInvariant);

    /// <summary>
    /// The CLI's transcript directory name: EVERY non-alphanumeric becomes `-`.
    /// Not just the separator: a worktree path contains dots, and on Windows the
    /// drive colon and backslashes go the same way (`C:\Users\x` → `C--Users-x`,
    /// verified against `~/.claude/projects` on this machine).
    /// </summary>
    public static string EncodeCwd(string cwd) => NonAlnum.Replace(cwd ?? "", "-");

    /// <summary>Roster timestamps are epoch milliseconds; we work in seconds.</summary>
    public static double? Ms(JsonNode? value)
    {
        var d = PyFloat(value);
        return d is double v ? v / 1000.0 : null;
    }

    /// <summary>One `sessions/&lt;pid&gt;.json` — one live `claude` process.</summary>
    public sealed record RosterEntry
    {
        public int Pid { get; init; }
        public string SessionId { get; init; } = "";
        public string Cwd { get; init; } = "";
        public string Name { get; init; } = "";
        public string Root { get; init; } = "";
        public string Kind { get; init; } = "interactive";
        public string Entrypoint { get; init; } = "cli";
        public string? Status { get; init; }
        public string? WaitingFor { get; init; }
        public double? StartedAt { get; init; }
        public double? StatusUpdatedAt { get; init; }
        public string? SocketPath { get; init; }
        public string Version { get; init; } = "";

        /// <summary>
        /// A process can only be steered if it bound an inbox socket. Measured: 4
        /// of 17 live entries had none. `ListAgents` cannot see those at all, which
        /// is why this watcher exists. (On Windows the "socket" is a named pipe;
        /// `File.Exists` answers for `\\.\pipe\…` paths as well — verified.)
        /// </summary>
        public bool Steerable => !string.IsNullOrEmpty(SocketPath) && SocketExists(SocketPath);
    }

    public const string PipePrefix = @"\\.\pipe\";

    /// <summary>
    /// Does an inbox endpoint (Unix socket file or Windows named pipe) exist?
    ///
    /// A pipe must NOT be checked with File.Exists / GetFileAttributes: that OPENS
    /// the pipe — a real, empty client connection to the session's inbox (verified:
    /// it consumed a single-instance test server). On a 1 Hz poll that would poke
    /// every session every second, so pipes are looked up by listing the pipe
    /// namespace instead, which connects to nothing.
    /// </summary>
    public static bool SocketExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (Py.IsWindows && path.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
                return PipeNames().Contains(path[PipePrefix.Length..]);
            return File.Exists(path) || Directory.Exists(path);
        }
        catch { return false; }
    }

    private static readonly object PipeGate = new();
    private static HashSet<string> _pipeNames = new(StringComparer.OrdinalIgnoreCase);
    private static double _pipeNamesAt = double.NegativeInfinity;

    /// <summary>Every named pipe on this machine, memoised for a quarter second (one poll asks many times).</summary>
    public static HashSet<string> PipeNames()
    {
        lock (PipeGate)
        {
            var now = Py.Monotonic();
            if (now - _pipeNamesAt < 0.25) return _pipeNames;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var h = FindFirstFileW(PipePrefix + "*", out var data);
            if (h != InvalidHandle)
            {
                try
                {
                    do { if (!string.IsNullOrEmpty(data.FileName)) names.Add(data.FileName); }
                    while (FindNextFileW(h, out data));
                }
                finally { FindClose(h); }
            }
            _pipeNames = names;
            _pipeNamesAt = now;
            return names;
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string pattern, out Win32FindData data);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr h, out Win32FindData data);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr h);

    public static RosterEntry? ParseEntry(string path, string root)
    {
        JsonNode? data;
        try { data = JsonNode.Parse(File.ReadAllText(path)); }
        catch { return null; }            // unreadable, empty, or caught mid-write
        if (data is not JsonObject o) return null;
        int pid;
        string sessionId, cwd;
        try
        {
            if (!o.ContainsKey("pid") || !o.ContainsKey("sessionId") || !o.ContainsKey("cwd")) return null;
            var p = PyInt(o["pid"]);
            if (p is null || p > int.MaxValue || p < int.MinValue) return null;
            pid = (int)p.Value;
            sessionId = PyStr(o["sessionId"]);
            cwd = PyStr(o["cwd"]);
        }
        catch { return null; }            // without these three it is not a session
        var waiting = o["waitingFor"] is JsonValue wv && wv.TryGetValue<string>(out var ws) && ws.Length > 0 ? ws : null;
        return new RosterEntry
        {
            Pid = pid,
            SessionId = sessionId,
            Cwd = cwd,
            Name = Truthy(o["name"]) ? PyStr(o["name"])
                 : (PathName(cwd) is { Length: > 0 } n ? n : sessionId[..Math.Min(8, sessionId.Length)]),
            Root = root,
            Kind = Truthy(o["kind"]) ? PyStr(o["kind"]) : "interactive",
            Entrypoint = Truthy(o["entrypoint"]) ? PyStr(o["entrypoint"]) : "cli",
            Status = Py.Str(o, "status"),
            WaitingFor = waiting,
            StartedAt = Ms(o["startedAt"]),
            StatusUpdatedAt = Ms(o["statusUpdatedAt"]),
            SocketPath = Py.Str(o, "messagingSocketPath"),
            Version = Truthy(o["version"]) ? PyStr(o["version"]) : "",
        };
    }

    /// <summary>Every well-formed roster entry across every root. Never raises.</summary>
    public static List<RosterEntry> ReadRoster(IEnumerable<string>? roots = null)
    {
        var entries = new List<RosterEntry>();
        var seen = new HashSet<(int, string)>();
        foreach (var root in roots ?? ConfigRoots())
        {
            var d = Path.Combine(root, "sessions");
            List<string> files;
            try
            {
                if (!Directory.Exists(d)) continue;
                files = Directory.GetFiles(d, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            catch { continue; }
            foreach (var f in files)
            {
                var entry = ParseEntry(f, root);
                if (entry is null) continue;
                var key = (entry.Pid, entry.SessionId);
                if (!seen.Add(key)) continue;   // the same pid registered under two roots
                entries.Add(entry);
            }
        }
        return entries;
    }

    // A 64 KB tail was sufficient for every live transcript measured, including
    // a 95 MB one, because `ai-title` and `last-prompt` are rewritten on every
    // turn and therefore always sit near the end.
    public const int TailBytes = 64 * 1024;
    public const int MaxRecentTools = 5;
    public const int MaxText = 600;

    /// <summary>What a session is doing, read from the end of its transcript.</summary>
    public sealed class Recap
    {
        public bool Exists { get; set; }
        public string? Title { get; set; }        // the CLI's own `aiTitle`
        public string? LastPrompt { get; set; }   // the CLI's own `lastPrompt`
        public string? LastText { get; set; }     // what the session last said
        public List<string> RecentTools { get; set; } = [];

        /// <summary>One line describing the session: its title, else its last prompt.</summary>
        public string? Summary() => !string.IsNullOrEmpty(Title) ? Title : (string.IsNullOrEmpty(LastPrompt) ? null : LastPrompt);
    }

    public static string TranscriptPath(string root, string cwd, string sessionId) =>
        Path.Combine(root, "projects", EncodeCwd(cwd), $"{sessionId}.jsonl");

    /// <summary>
    /// Parse the JSON objects in the last `nbytes` of a JSONL file. Seeking into a
    /// large file lands mid-line, so the first (partial) line is discarded.
    /// Unparseable lines are skipped: another process appends while we read.
    /// </summary>
    public static List<JsonObject> TailObjects(string path, int nbytes = TailBytes)
    {
        byte[] raw;
        try
        {
            using var fh = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fh.Length;
            if (size > nbytes)
            {
                fh.Seek(size - nbytes, SeekOrigin.Begin);
                int b;
                while ((b = fh.ReadByte()) != -1 && b != '\n') { }   // discard the partial line
            }
            else fh.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            fh.CopyTo(ms);
            raw = ms.ToArray();
        }
        catch { return []; }
        var output = new List<JsonObject>();
        foreach (var line0 in Encoding.UTF8.GetString(raw).Split('\n'))
        {
            var line = line0.Trim();
            if (line.Length == 0) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj) output.Add(obj);
            }
            catch { }
        }
        return output;
    }

    /// <summary>
    /// One line of clipped text, or null — for a value of ANY shape. Every caller
    /// reads a field out of somebody else's transcript, so the type check lives
    /// here: a non-string block once escaped the poll loop and froze the snapshot.
    /// </summary>
    public static string? Clip(JsonNode? text, int limit = MaxText)
    {
        if (text is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0) return null;
        return Clip(s, limit);
    }

    public static string? Clip(string? text, int limit = MaxText)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = JoinWs(text);
        if (text.Length <= limit) return text;
        int cut = limit - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    /// <summary>
    /// Reduce transcript lines to a recap. Unknown line types are ignored — 19
    /// types were observed on the live machine and only these five are used.
    /// </summary>
    public static Recap RecapFrom(IEnumerable<JsonObject> objs)
    {
        var r = new Recap { Exists = true };
        var tools = new List<string>();
        foreach (var o in objs)
        {
            var kind = Py.Str(o, "type");
            if (kind == "ai-title")
                r.Title = Clip(o["aiTitle"], 200) ?? r.Title;
            else if (kind == "last-prompt")
                r.LastPrompt = Clip(o["lastPrompt"]) ?? r.LastPrompt;
            else if (kind == "assistant" && !Truthy(o["isSidechain"]))
            {
                var content = (o["message"] as JsonObject)?["content"];
                if (content is not JsonArray arr) continue;
                foreach (var blockNode in arr)
                {
                    if (blockNode is not JsonObject block) continue;
                    var bt = Py.Str(block, "type");
                    if (bt == "text")
                        r.LastText = Clip(block["text"]) ?? r.LastText;
                    else if (bt == "tool_use" && Truthy(block["name"]))
                        // Clipped like its siblings: a string of any length and
                        // shape out of somebody else's transcript.
                        tools.Add(Clip(block["name"]) ?? "");
                }
            }
        }
        r.RecentTools = tools.Count > MaxRecentTools ? tools.GetRange(tools.Count - MaxRecentTools, MaxRecentTools) : tools;
        return r;
    }

    /// <summary>
    /// Every root this conversation registered in, the primary's first. Where two
    /// roots both hold a transcript (hardlinked) the primary's is the one read.
    /// </summary>
    public static List<string> RootsOf(IEnumerable<RosterEntry> group, RosterEntry primary)
    {
        var output = new List<string> { primary.Root };
        foreach (var e in group)
            if (!output.Contains(e.Root)) output.Add(e.Root);
        return output;
    }

    /// <summary>
    /// The first root that actually has this conversation's transcript. A missing
    /// transcript makes a session `fresh`, so stopping at one root reported a busy
    /// conversation as "not started".
    /// </summary>
    public static Recap FirstRecap(IEnumerable<string> roots, string cwd, string sessionId)
    {
        foreach (var root in roots)
        {
            var recap = ReadRecap(root, cwd, sessionId);
            if (recap.Exists) return recap;
        }
        return new Recap { Exists = false };
    }

    /// <summary>
    /// Recap a session, or `Recap(exists=False)` if it has no transcript. Absence
    /// is meaningful: a session nobody has prompted writes no transcript at all.
    /// </summary>
    public static Recap ReadRecap(string root, string cwd, string sessionId, int nbytes = TailBytes)
    {
        var path = TranscriptPath(root, cwd, sessionId);
        if (!File.Exists(path)) return new Recap { Exists = false };
        return RecapFrom(TailObjects(path, nbytes));
    }

    // Derived conversation states.
    public const string Fresh = "fresh";          // alive but never prompted (no transcript) — never announced
    public const string NeedsYou = "needs_you";
    public const string Working = "working";
    public const string Shell = "shell";
    public const string Idle = "idle";
    public const string Gone = "gone";
    public const string Unknown = "unknown";      // alive, but the roster carries no status for it

    // Keywords matched against a lowercased `waitingFor` reason to flag one a peer
    // message cannot clear. The reason set is OPEN; "input needed" is deliberately
    // NOT here (a peer message is exactly what it wants), and an unrecognised
    // reason is treated as answerable by default.
    public static readonly string[] HumanHandReasons = ["permission", "dialog", "trust", "login", "auth"];

    public static readonly string[] QuestionTools = ["AskUserQuestion", "ExitPlanMode"];

    public static readonly Regex UrlRe = new(@"\S+://\S+|\bwww\.\S+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Did the session stop to ask something? A bare "?" is too eager: a URL query
    /// string counts (a spend-limit message ending `…/usage?from=…` was announced
    /// as needing the user). URLs are stripped first, then a question mark only
    /// counts at the close of the message or of one of its last lines.
    /// </summary>
    public static bool LooksLikeAQuestion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var cleaned = UrlRe.Replace(text, " ").TrimEnd();
        if (cleaned.Length == 0) return false;
        if (cleaned.EndsWith('?')) return true;
        // A question can be followed by options ("...or SQLite?\n1. Postgres").
        var lines = SplitLines(cleaned);
        return lines.Skip(Math.Max(0, lines.Count - 6)).Any(l => l.TrimEnd().EndsWith('?'));
    }

    /// <summary>One conversation: what it is, what it is doing, and how to reach it.</summary>
    public sealed class SessionState
    {
        public string SessionId { get; set; } = "";
        public string Cwd { get; set; } = "";
        public string Project { get; set; } = "";
        public string State { get; set; } = "";
        public List<int> Pids { get; set; } = [];
        public int? PrimaryPid { get; set; }
        public string RosterName { get; set; } = "";
        public string VoiceName { get; set; } = "";
        public string? Needs { get; set; }
        public string? Title { get; set; }
        public string? LastPrompt { get; set; }
        public string? LastText { get; set; }
        public List<string> RecentTools { get; set; } = [];
        // When the SESSION STARTED — age ordering only, never spoken elapsed time.
        public double? Started { get; set; }
        // When the CURRENT STATE began — spoken elapsed time. Measured gaps between
        // the two reached 102 HOURS, so they must stay two fields.
        public double? Since { get; set; }
        public string Origin { get; set; } = "terminal";
        public bool Steerable { get; set; }
        public string? SocketPath { get; set; }
        // Is this the conversation the user is actually sitting at, in this
        // project? See MarkPrimary; it can answer "no" about every one.
        public bool Primary { get; set; }
        public string PrimaryReason { get; set; } = "";
        // Subagents counted off the `<sessionId>/subagents/` folder. "Active"
        // means written to within AgentActiveWithinSec, never "a process is alive".
        public int AgentsSeen { get; set; }
        public int AgentsActive { get; set; }
        // AgentsSeen hit MaxAgentFiles: it is a floor, not a total.
        public bool AgentsCapped { get; set; }

        /// <summary>`fresh` sessions are never spoken about unprompted.</summary>
        public bool Announceable => State != Fresh;

        /// <summary>True when the thing it waits on cannot be answered over the socket.</summary>
        public bool NeedsAHumanHand
        {
            get
            {
                var reason = (Needs ?? "").ToLowerInvariant();
                return HumanHandReasons.Any(w => reason.Contains(w, StringComparison.Ordinal));
            }
        }

        public string? Summary() => !string.IsNullOrEmpty(Title) ? Title : (string.IsNullOrEmpty(LastPrompt) ? null : LastPrompt);

        /// <summary>Shallow copy (Python's `SessionState(**prior.__dict__)`).</summary>
        public SessionState Copy() => (SessionState)MemberwiseClone();
    }

    // ── which conversation is the main one ──────────────────────────────────────
    //
    //   Primary is decided PER PROJECT. A conversation is ELIGIBLE if it is alive,
    //   has been prompted at least once, and was started interactively
    //   (`entrypoint: cli`, origin "terminal").
    //
    //   Among the eligible, the most recently active wins, measured by `since`.
    //
    //   AND: if the runner-up is within PrimaryMarginSec of the leader, NEITHER is
    //   primary — claiming one anyway would be a guess wearing a fact's clothes.
    //
    // Not used: transcript size, and whether JARVIS spawned it (his own runs are
    // filtered out of the roster upstream, in the server).
    public const double PrimaryMarginSec = 120.0;

    public const string PrimaryOnly = "the only live conversation here";
    public const string PrimaryRecent = "most recently active";
    public const string PrimaryTied = "equally live as another here";
    public const string NotPrimaryBackground = "a background conversation";
    public const string NotPrimaryFresh = "never prompted";
    public const string NotPrimaryGone = "finished";

    /// <summary>Set `Primary` / `PrimaryReason` on every conversation.</summary>
    public static void MarkPrimary(List<SessionState> sessions)
    {
        var byProject = new Dictionary<string, List<SessionState>>();
        foreach (var s in sessions)
        {
            if (s.State == Gone) { s.Primary = false; s.PrimaryReason = NotPrimaryGone; }
            else if (s.State == Fresh) { s.Primary = false; s.PrimaryReason = NotPrimaryFresh; }
            else if (s.Origin != "terminal") { s.Primary = false; s.PrimaryReason = NotPrimaryBackground; }
            else
            {
                s.Primary = false; s.PrimaryReason = NotPrimaryBackground;
                if (!byProject.TryGetValue(s.Project, out var l)) byProject[s.Project] = l = [];
                l.Add(s);
            }
        }

        foreach (var group in byProject.Values)
        {
            if (group.Count == 1)
            {
                group[0].Primary = true; group[0].PrimaryReason = PrimaryOnly;
                continue;
            }
            var ranked = group.OrderByDescending(s => s.Since ?? double.NegativeInfinity).ToList();
            var lead = ranked[0];
            var runner = ranked[1];
            var leadAt = lead.Since;
            var runnerAt = runner.Since;
            bool separated = leadAt is not null && (runnerAt is null || leadAt - runnerAt > PrimaryMarginSec);
            if (!separated)
            {
                // Inside the margin nothing is claimed. Every member of the tie
                // says so; the ones further back are simply background.
                foreach (var s in ranked)
                {
                    var at = s.Since;
                    bool tied = at is not null && leadAt is not null && leadAt - at <= PrimaryMarginSec;
                    s.PrimaryReason = tied ? PrimaryTied : NotPrimaryBackground;
                }
                continue;
            }
            lead.Primary = true; lead.PrimaryReason = PrimaryRecent;
            foreach (var s in ranked.Skip(1)) { s.Primary = false; s.PrimaryReason = NotPrimaryBackground; }
        }
    }

    // ── subagents ───────────────────────────────────────────────────────────────

    // A subagent transcript written this recently is taken to be working. This is
    // a FILE age — there is no process to check here.
    public const double AgentActiveWithinSec = 90.0;
    // Cap the stat() calls one conversation can cost a 1 Hz poll. 209 subagent
    // transcripts were measured under a single conversation.
    public const int MaxAgentFiles = 300;

    public static (int seen, int active, bool capped) CountAgents(string root, string cwd, string sessionId, double now) =>
        CountAgents(new[] { root }, cwd, sessionId, now);

    /// <summary>
    /// (subagent transcripts seen, how many were written recently, capped?).
    ///
    /// Several roots are deduped BY FILE NAME (they are hardlinked, so one agent is
    /// reached by two names). THE CAP COUNTS TRANSCRIPTS, NOT DIRECTORY ENTRIES —
    /// every `agent-x.jsonl` has a sidecar beside it; filter, then cap. BOTH COUNTS
    /// ARE FLOORS once the cap is hit, because the slice is taken by file name,
    /// which is uncorrelated with recency. Never raises.
    /// </summary>
    public static (int seen, int active, bool capped) CountAgents(IEnumerable<string> roots, string cwd,
        string sessionId, double now)
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var d = Path.Combine(root, "projects", EncodeCwd(cwd), sessionId, "subagents");
            string[] entries;
            try
            {
                if (!Directory.Exists(d)) continue;
                entries = Directory.GetFileSystemEntries(d);
            }
            catch { continue; }
            foreach (var f in entries)
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".jsonl", StringComparison.Ordinal)) byName.TryAdd(name, f);
            }
        }

        var transcripts = byName.Keys.OrderBy(n => n, StringComparer.Ordinal).Select(n => byName[n]).ToList();
        bool capped = transcripts.Count > MaxAgentFiles;
        int seen = 0, active = 0;
        foreach (var f in transcripts.Take(MaxAgentFiles))
        {
            DateTime mtime;
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists) continue;           // vanished, or a directory
                mtime = fi.LastWriteTimeUtc;
            }
            catch { continue; }
            seen++;
            if (now - Py.ToEpoch(new DateTimeOffset(mtime, TimeSpan.Zero)) <= AgentActiveWithinSec) active++;
        }
        return (seen, active, capped);
    }

    /// <summary>
    /// The project name for a cwd, tolerant of Claude Code's own worktree layout
    /// (`&lt;repo&gt;/.claude/worktrees/&lt;branch&gt;` → the repo name). Worktrees made
    /// elsewhere keep their own directory name: detecting those would need `git`
    /// on a 1-second poll, so it is deliberately not attempted.
    /// </summary>
    public static string ProjectName(string cwd)
    {
        var parts = PathParts(cwd);
        int i = parts.IndexOf(".claude");
        if (i >= 0)
        {
            if (i > 0 && parts[i - 1].Length > 0 && i + 1 < parts.Count && parts[i + 1] == "worktrees")
                return parts[i - 1];
        }
        var name = PathName(cwd);
        return name.Length > 0 ? name : cwd;
    }

    /// <summary>
    /// The worktree name in `&lt;repo&gt;/.claude/worktrees/&lt;branch&gt;`, or "". The
    /// other half of ProjectName: "which copy of it".
    /// </summary>
    public static string WorktreeBranch(string cwd)
    {
        var parts = PathParts(cwd);
        int i = parts.IndexOf(".claude");
        if (i < 0) return "";
        if (i > 0 && i + 2 < parts.Count && parts[i + 1] == "worktrees") return parts[i + 2];
        return "";
    }

    public static readonly Dictionary<string, string> OriginByEntrypoint = new()
    {
        ["cli"] = "terminal",
        ["sdk-cli"] = "background",
        ["claude-desktop"] = "desktop",
        ["desktop"] = "desktop",
    };

    public static string OriginOf(RosterEntry entry) =>
        OriginByEntrypoint.TryGetValue(entry.Entrypoint, out var o) ? o : "terminal";

    /// <summary>
    /// Where JARVIS's own brain process runs: `&lt;JARVIS_DATA_DIR&gt;/jarvis`,
    /// computed WITHOUT DataPaths and without its mkdir side effect — this is
    /// polled once a second and must stay pure reading.
    /// </summary>
    public static string BrainCwd()
    {
        var raw = Py.Getenv("JARVIS_DATA_DIR");
        var baseDir = !string.IsNullOrEmpty(raw) ? Py.ExpandUser(raw) : Path.Combine(Py.AppDir, "data");
        return Path.Combine(baseDir, "jarvis");
    }

    /// <summary>
    /// True for JARVIS's own brain process, never a user conversation: entrypoint
    /// `sdk-cli` and a cwd equal to the brain home. A roster name of "jarvis" is
    /// NOT used — a user can have a real project called jarvis.
    /// </summary>
    public static bool IsOwnBrain(RosterEntry entry)
    {
        if (entry.Entrypoint != "sdk-cli") return false;
        try { return SameDir(entry.Cwd, BrainCwd()); }
        catch { return false; }
    }

    /// <summary>
    /// Two spellings of one directory. `realpath`, not a textual compare: a
    /// JARVIS_DATA_DIR that is a symlink/junction spells the brain's home one way
    /// here and another way in the roster.
    /// </summary>
    public static bool SameDir(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return SamePathText(RealPath(a), RealPath(b));
    }

    public static RosterEntry PickPrimary(List<RosterEntry> entries) =>
        entries.OrderByDescending(e => PidAlive(e.Pid))
               .ThenByDescending(e => e.Steerable)
               .ThenByDescending(e => e.StatusUpdatedAt ?? 0.0)
               .First();

    /// <summary>The conversation's state and the reason it needs you, if it does.</summary>
    public static (string state, string? needs) DeriveState(List<RosterEntry> entries, Recap recap)
    {
        var live = entries.Where(e => PidAlive(e.Pid)).ToList();
        if (live.Count == 0) return (Gone, null);
        // Nobody has ever prompted it — true even at a startup dialog.
        if (!recap.Exists) return (Fresh, null);

        var waiting = live.FirstOrDefault(e => !string.IsNullOrEmpty(e.WaitingFor));
        if (waiting is not null) return (NeedsYou, waiting.WaitingFor);
        if (live.Any(e => e.Status == "waiting")) return (NeedsYou, null);
        if (live.Any(e => e.Status == "busy")) return (Working, null);
        if (live.Any(e => e.Status == "shell")) return (Shell, null);
        if (live.All(e => e.Status is null)) return (Unknown, null);
        // Idle, but it may have stopped to ask something.
        if (recap.RecentTools.Any(t => QuestionTools.Contains(t)) || LooksLikeAQuestion(recap.LastText))
            return (NeedsYou, null);
        return (Idle, null);
    }

    /// <summary>
    /// Give every conversation a name a person can say and hear. Collisions
    /// measured: two in ONE directory (folder can't disambiguate) and three across
    /// TWO directories (folder can). Roster suffixes like `-4b` are never used.
    /// </summary>
    public static void AssignVoiceNames(List<SessionState> sessions)
    {
        var byProject = new Dictionary<string, List<SessionState>>();
        foreach (var s in sessions)
        {
            if (!byProject.TryGetValue(s.Project, out var l)) byProject[s.Project] = l = [];
            l.Add(s);
        }

        foreach (var (project, group) in byProject)
        {
            if (group.Count == 1)
            {
                group[0].VoiceName = project;
                continue;
            }
            var byParent = new Dictionary<string, List<SessionState>>();
            foreach (var s in group)
            {
                var parent = ParentName(s.Cwd);
                if (!byParent.TryGetValue(parent, out var l)) byParent[parent] = l = [];
                l.Add(s);
            }
            if (byParent.Count > 1)
            {
                // The parent folder tells them apart — "chitauri in Desktop" vs
                // "chitauri in Projects"; a folder holding several gets the chain too.
                foreach (var (parent, sub) in byParent)
                {
                    var baseName = $"{project} in {parent}";
                    if (sub.Count == 1) sub[0].VoiceName = baseName;
                    else NameGroup(sub, baseName);
                }
            }
            else NameGroup(group, project);
        }
    }

    /// <summary>
    /// Name several conversations sharing a base: by what they are ABOUT, then
    /// what they are DOING, and only as a last resort how OLD they are. Each rule
    /// applies to the WHOLE group at once, never half and half.
    /// </summary>
    public static void NameGroup(List<SessionState> group, string baseName)
    {
        if (NameByTopic(group, baseName)) return;
        if (NameByState(group, baseName)) return;
        NameByAge(group, baseName);
    }

    // Generic words that carry no topic of their own.
    public static readonly HashSet<string> TitleStopwords =
    [
        "a", "an", "the", "and", "or", "to", "for", "of", "in", "on", "with",
        "at", "by", "from", "is", "are", "this", "that", "it", "its", "your",
        "my", "our",
    ];

    private static readonly Regex NonAlnumLower = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    private static readonly Regex NonAlnumDashLower = new("[^a-z0-9-]+", RegexOptions.CultureInvariant);

    public static HashSet<string> TopicWords(string project) =>
        NonAlnumLower.Split(project.ToLowerInvariant()).Where(w => w.Length > 0).ToHashSet();

    /// <summary>
    /// A short (≤2 word) sayable phrase from a title: stopwords and the project's
    /// own name stripped, last couple of words kept. Null if nothing is left.
    /// </summary>
    public static string? TopicPhrase(string title, string project)
    {
        var tokens = NonAlnumLower.Split(title.ToLowerInvariant()).Where(w => w.Length > 0).ToList();
        var projWords = TopicWords(project);
        var kept = tokens.Where(w => !TitleStopwords.Contains(w) && !projWords.Contains(w)).ToList();
        if (kept.Count == 0) return null;
        return string.Join(" ", kept.Skip(Math.Max(0, kept.Count - 2)));
    }

    /// <summary>
    /// Name every conversation in the group by what it's ABOUT. Succeeds only when
    /// every member has a title and the phrases are pairwise distinct.
    /// </summary>
    public static bool NameByTopic(List<SessionState> group, string baseName)
    {
        var phrases = new Dictionary<string, string>();
        foreach (var s in group)
        {
            var title = (s.Title ?? "").Trim();
            if (title.Length == 0) return false;
            var phrase = TopicPhrase(title, s.Project);
            if (string.IsNullOrEmpty(phrase)) return false;
            phrases[s.SessionId] = phrase;
        }
        if (phrases.Values.Distinct().Count() != phrases.Count) return false;
        foreach (var s in group) s.VoiceName = $"{baseName}, the {phrases[s.SessionId]} one";
        return true;
    }

    // Short, sayable phrases for what a conversation is doing right now.
    public static readonly Dictionary<string, string> StatePhrases = new()
    {
        [NeedsYou] = "that needs you",
        [Working] = "that's working",
        [Idle] = "that's idle",
        [Shell] = "in a shell",
        [Gone] = "that's finished",
        [Fresh] = "that hasn't started",
        [Unknown] = "in an unclear state",
    };

    /// <summary>Name every conversation by what it is DOING; only when every state differs.</summary>
    public static bool NameByState(List<SessionState> group, string baseName)
    {
        var states = group.Select(s => s.State).ToList();
        if (states.Distinct().Count() != states.Count) return false;
        foreach (var s in group)
        {
            var phrase = StatePhrases.TryGetValue(s.State, out var p) ? p : "in an unclear state";
            s.VoiceName = $"the {baseName} {phrase}";
        }
        return true;
    }

    /// <summary>
    /// Two conversations in one folder: 'the newer hammer' / 'the older hammer'.
    /// Beyond two, ordinals. The last resort — it says nothing about what a
    /// conversation IS.
    /// </summary>
    public static void NameByAge(List<SessionState> group, string baseName)
    {
        var ordered = group.OrderByDescending(s => s.Started ?? 0.0).ToList();
        if (ordered.Count == 2)
        {
            ordered[0].VoiceName = $"the newer {baseName}";
            ordered[1].VoiceName = $"the older {baseName}";
            return;
        }
        string[] words = ["newest", "second", "third", "fourth", "fifth"];
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].VoiceName = i < words.Length ? $"the {words[i]} {baseName}" : $"{baseName} number {i + 1}";
    }

    // Words a person says around a name that carry no identity of their own.
    public static readonly HashSet<string> Filler =
    [
        "the", "a", "an", "one", "ones", "session", "sessions", "conversation",
        "conversations", "project", "in", "on", "at", "my", "please", "that",
        "this", "it", "s", "lets", "let", "go", "with", "use", "about",
    ];

    /// <summary>Every word that could identify this conversation out loud.</summary>
    public static HashSet<string> NameWords(SessionState s)
    {
        var text = $"{s.VoiceName} {s.Project}".ToLowerInvariant();
        return NonAlnumDashLower.Split(text).Where(w => w.Length > 0).ToHashSet();
    }

    /// <summary>
    /// Among several matches, drop `fresh` ones IF at least one match is real. If
    /// every match is fresh keep them all; several real matches are all returned
    /// so the caller asks — that is a safety property.
    /// </summary>
    public static List<SessionState> PreferReal(List<SessionState> matches)
    {
        var real = matches.Where(s => s.State != Fresh).ToList();
        return real.Count > 0 ? real : matches;
    }

    private static void SortSessions(List<SessionState> sessions)
    {
        var sorted = sessions
            .OrderBy(s => s.Project, StringComparer.Ordinal)
            .ThenBy(s => s.VoiceName, StringComparer.Ordinal)
            .ToList();
        sessions.Clear();
        sessions.AddRange(sorted);
    }

    /// <summary>Every conversation on this machine at one instant.</summary>
    public sealed class Snapshot
    {
        public List<SessionState> Sessions { get; set; } = [];
        public double TakenAt { get; set; }

        public SessionState? ById(string? sessionId) => Sessions.FirstOrDefault(s => s.SessionId == sessionId);

        /// <summary>
        /// This snapshot without those conversations, RE-DERIVED (voice names and
        /// the "main" badge are computed across a project, so they are redone over
        /// what is kept). The same object comes back when nothing is excluded.
        /// </summary>
        public Snapshot Excluding(IEnumerable<string> sessionIds)
        {
            var ids = new HashSet<string>(sessionIds);
            if (ids.Count == 0) return this;
            var kept = Sessions.Where(s => !ids.Contains(s.SessionId)).ToList();
            if (kept.Count == Sessions.Count) return this;
            AssignVoiceNames(kept);
            MarkPrimary(kept);
            SortSessions(kept);
            return new Snapshot { Sessions = kept, TakenAt = TakenAt };
        }

        public Dictionary<string, List<SessionState>> ByProject()
        {
            var output = new Dictionary<string, List<SessionState>>();
            foreach (var s in Sessions)
            {
                if (!output.TryGetValue(s.Project, out var l)) output[s.Project] = l = [];
                l.Add(s);
            }
            return output;
        }

        /// <summary>
        /// Waiting conversations, most-recently-waiting first — ordered by `Since`
        /// (current state), NOT `Started`, which differ by up to 102 hours.
        /// </summary>
        public List<SessionState> NeedingYou() =>
            Sessions.Where(s => s.State == NeedsYou)
                    .OrderByDescending(s => s.Since ?? double.NegativeInfinity)
                    .ToList();

        /// <summary>
        /// Map what the user said to conversations. Returns EVERY candidate when
        /// ambiguous: the caller must ask; picking the first steers the wrong
        /// session and cannot be undone.
        /// </summary>
        public List<SessionState> Resolve(string? reference, string? lastMentioned = null)
        {
            var refText = JoinWs((reference ?? "").ToLowerInvariant());
            if (refText.Length == 0) return [];
            if (refText is "that one" or "that" or "it" or "the same one" or "that session")
            {
                var s = !string.IsNullOrEmpty(lastMentioned) ? ById(lastMentioned) : null;
                return s is not null ? [s] : [];
            }

            var exact = Sessions.Where(s => s.VoiceName.ToLowerInvariant() == refText).ToList();
            if (exact.Count > 0) return exact;
            var sid = Sessions.Where(s => s.SessionId == reference).ToList();
            if (sid.Count > 0) return sid;
            var roster = Sessions.Where(s => s.RosterName.ToLowerInvariant() == refText).ToList();
            if (roster.Count > 0) return roster;

            // Exact PROJECT match before any substring fallback: "hammer" must not
            // also pull in a sibling project "hammer-private".
            var exactProject = Sessions.Where(s => s.Project.ToLowerInvariant() == refText).ToList();
            if (exactProject.Count > 0) return PreferReal(exactProject);

            // A qualified answer to our OWN disambiguation question must resolve
            // ("the newer one" once looped forever): if every distinguishing word
            // the user said appears in a voice name, that name is a candidate, and
            // the narrowest set wins.
            var refWords = NonAlnumDashLower.Split(refText).Where(w => w.Length > 0 && !Filler.Contains(w)).ToHashSet();
            if (refWords.Count > 0)
            {
                var tokenHits = Sessions.Where(s => refWords.IsSubsetOf(NameWords(s))).ToList();
                if (tokenHits.Count > 0)
                {
                    var narrowed = PreferReal(tokenHits);
                    if (narrowed.Count < Sessions.Count) return narrowed;
                }
            }

            // Substring, both directions: "the chitauri one" contains "chitauri",
            // and "hammer" is contained in "the newer hammer".
            var loose = Sessions.Where(s =>
                refText.Contains(s.VoiceName.ToLowerInvariant(), StringComparison.Ordinal)
                || refText.Contains(s.Project.ToLowerInvariant(), StringComparison.Ordinal)
                || s.VoiceName.ToLowerInvariant().Contains(refText, StringComparison.Ordinal)).ToList();
            return PreferReal(loose);
        }
    }

    /// <summary>
    /// Read everything and reduce it to conversations. Processes are grouped by
    /// `sessionId`: the conversation is the unit JARVIS names and counts.
    /// </summary>
    public static Snapshot BuildSnapshot(List<RosterEntry>? entries = null, IEnumerable<string>? roots = null,
        double? now = null)
    {
        entries ??= ReadRoster(roots);
        // JARVIS's own brain registers in the roster like any session; it is never
        // one of the user's conversations. Filtered here, not in ReadRoster.
        entries = entries.Where(e => !IsOwnBrain(e)).ToList();

        var grouped = new Dictionary<string, List<RosterEntry>>();
        foreach (var e in entries)
        {
            if (!grouped.TryGetValue(e.SessionId, out var l)) grouped[e.SessionId] = l = [];
            l.Add(e);
        }

        var sessions = new List<SessionState>();
        double at = now ?? Py.Now();
        foreach (var (sessionId, group) in grouped)
        {
            var primary = PickPrimary(group);
            // EVERY root the conversation registered in, not just the primary's,
            // or a transcript under the second root read as `fresh`.
            var rootsHere = RootsOf(group, primary);
            var recap = FirstRecap(rootsHere, primary.Cwd, sessionId);
            var (state, needs) = DeriveState(group, recap);
            var (agentsSeen, agentsActive, agentsCapped) = CountAgents(rootsHere, primary.Cwd, sessionId, at);
            var live = group.Where(e => PidAlive(e.Pid)).ToList();
            var steerableEntry = live.FirstOrDefault(e => e.Steerable);
            sessions.Add(new SessionState
            {
                SessionId = sessionId,
                Cwd = primary.Cwd,
                Project = ProjectName(primary.Cwd),
                State = state,
                Pids = group.Select(e => e.Pid).ToList(),
                PrimaryPid = primary.Pid,
                RosterName = primary.Name,
                Needs = needs,
                Title = recap.Title,
                LastPrompt = recap.LastPrompt,
                LastText = recap.LastText,
                RecentTools = recap.RecentTools,
                Started = NonZero(primary.StartedAt) ?? primary.StatusUpdatedAt,
                Since = NonZero(primary.StatusUpdatedAt) ?? primary.StartedAt,
                Origin = OriginOf(primary),
                Steerable = steerableEntry is not null,
                SocketPath = steerableEntry?.SocketPath,
                AgentsSeen = agentsSeen,
                AgentsActive = agentsActive,
                AgentsCapped = agentsCapped,
            });
        }

        AssignVoiceNames(sessions);
        MarkPrimary(sessions);
        SortSessions(sessions);
        return new Snapshot { Sessions = sessions, TakenAt = at };
    }

    // A conversation that has died is kept this long so a completion can still be
    // announced after the process is gone.
    public const double GoneRetentionSec = 600.0;
    // Work shorter than this is a flicker, not a job worth announcing.
    public const double MinWorkSec = 30.0;

    /// <summary>
    /// Polls the roster and publishes only the transitions worth speaking about.
    /// Startup is silent by construction: the first poll fills `_previous` without
    /// emitting, so JARVIS never greets you by reciting every open session.
    ///
    /// The Python ran polls on a worker thread and marshalled callbacks back onto
    /// the asyncio loop thread. .NET has no loop thread to return to, so
    /// subscribers are invoked inline on the polling thread and must be
    /// thread-safe (spawn work with Py.Spawn rather than touching shared state).
    /// </summary>
    public sealed class SessionWatcher
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _previous = new();        // session_id -> state
        private readonly Dictionary<string, double> _workingSince = new();
        private readonly Dictionary<string, double> _goneAt = new();
        private readonly Dictionary<string, SessionState> _goneCache = new();
        private readonly List<Action<Dictionary<string, object?>>> _subscribers = [];
        private bool _started;
        private Task? _task;
        private CancellationTokenSource? _cts;
        private volatile Snapshot _snapshot = new();

        public List<string>? Roots { get; set; }
        public double Interval { get; set; }

        /// <summary>The latest snapshot. Replaced whole, so readers never see a half-built one.</summary>
        public Snapshot Snapshot { get => _snapshot; set => _snapshot = value; }

        public SessionWatcher(List<string>? roots = null, double interval = 1.0)
        {
            Roots = roots;
            Interval = interval;
        }

        public void OnEvent(Action<Dictionary<string, object?>> callback)
        {
            lock (_subscribers) _subscribers.Add(callback);
        }

        /// <summary>Notify every subscriber. One bad subscriber must not stop the rest.</summary>
        public void Publish(string kind, SessionState session, double at)
        {
            var evt = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["at"] = at,
                ["session"] = SessionToDict(session),
            };
            List<Action<Dictionary<string, object?>>> subs;
            lock (_subscribers) subs = [.. _subscribers];
            foreach (var cb in subs)
            {
                try { cb(evt); }
                catch (Exception e) { Log.LogWarning(e, "session event subscriber failed"); }
            }
        }

        public Snapshot PollOnce(double? now = null)
        {
            lock (_gate)
            {
                double t = now ?? Py.Now();
                var snap = BuildSnapshot(roots: Roots, now: t);

                // Carry recently-dead conversations forward so a completion can
                // still be announced after the process has exited.
                var present = snap.Sessions.Select(s => s.SessionId).ToHashSet();
                foreach (var (sid, cached) in _goneCache.ToList())
                {
                    if (present.Contains(sid))
                    {
                        _goneCache.Remove(sid);
                        _goneAt.Remove(sid);
                        continue;
                    }
                    if (t - (_goneAt.TryGetValue(sid, out var g) ? g : t) > GoneRetentionSec)
                    {
                        _goneCache.Remove(sid);
                        _goneAt.Remove(sid);
                        _previous.Remove(sid);
                        _workingSince.Remove(sid);
                    }
                    else snap.Sessions.Add(cached);
                }
                foreach (var sid in _previous.Keys.ToList())
                {
                    if (!present.Contains(sid) && !_goneCache.ContainsKey(sid))
                    {
                        var prior = Snapshot.ById(sid);
                        if (prior is not null)
                        {
                            var gone = prior.Copy();
                            gone.State = Gone;
                            gone.Steerable = false;
                            gone.SocketPath = null;
                            _goneCache[sid] = gone;
                            _goneAt[sid] = t;
                            snap.Sessions.Add(gone);
                        }
                    }
                }

                // Every session PUBLISHED must have been through naming together,
                // and primary is a comparison: both are redone over the final set
                // or a gone-cache entry carries a stale name or crown.
                AssignVoiceNames(snap.Sessions);
                MarkPrimary(snap.Sessions);
                SortSessions(snap.Sessions);

                bool firstPoll = !_started;
                Snapshot = snap;                     // snapshot BEFORE notifying
                _started = true;

                foreach (var s in snap.Sessions)
                {
                    _previous.TryGetValue(s.SessionId, out var was);
                    if (s.State == Working && was != Working) _workingSince[s.SessionId] = t;

                    if (!firstPoll && s.Announceable)
                    {
                        if (s.State == NeedsYou && was != NeedsYou)
                            Publish("needs_you", s, t);
                        else if (was == Working && s.State is Idle or Gone or Unknown)
                        {
                            // A session exiting while its roster entry is torn down
                            // can land on `unknown` rather than `gone` — treat it the
                            // same for the "finished" announcement.
                            if (_workingSince.TryGetValue(s.SessionId, out var started) && t - started >= MinWorkSec)
                                Publish("finished", s, t);
                        }
                    }
                    _previous[s.SessionId] = s.State;
                }
                return snap;
            }
        }

        public Task Start()
        {
            if (_task is not null) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Run(() => Loop(ct));
            return Task.CompletedTask;
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Run(() => PollOnce(), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) { Log.LogWarning(e, "watch tick failed"); }
                try { await Task.Delay(TimeSpan.FromSeconds(Interval), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        public async Task Stop()
        {
            var task = _task;
            var cts = _cts;
            _task = null;
            _cts = null;
            if (task is null) return;
            cts?.Cancel();
            try { await task; }
            catch (OperationCanceledException) { }      // expected: our own cancel
            catch (Exception e) { Log.LogWarning(e, "session watcher did not stop cleanly"); }
            finally { cts?.Dispose(); }
        }
    }

    /// <summary>The JSON shape used by /api/sessions, /ws/sessions, and the tools.</summary>
    public static Dictionary<string, object?> SessionToDict(SessionState s) => new()
    {
        ["session_id"] = s.SessionId,
        ["voice_name"] = s.VoiceName,
        ["roster_name"] = s.RosterName,
        ["project"] = s.Project,
        ["cwd"] = s.Cwd,
        ["state"] = s.State,
        ["needs"] = s.Needs,
        ["needs_a_human_hand"] = s.NeedsAHumanHand,
        ["title"] = s.Title,
        ["summary"] = s.Summary(),
        ["last_prompt"] = s.LastPrompt,
        ["last_text"] = s.LastText,
        ["recent_tools"] = s.RecentTools.ToList(),
        // TWO stamps, never one: `started` is when the conversation began, `since`
        // when its CURRENT STATE began. Either may be null — an absent stamp is an
        // absence of evidence; 0 would render as 1970.
        ["started"] = s.Started,
        ["since"] = s.Since,
        ["origin"] = s.Origin,
        ["steerable"] = s.Steerable,
        ["pids"] = s.Pids.ToList(),
        ["primary_pid"] = s.PrimaryPid,
        // The verdict AND the reason: the reason is what lets a reader check it.
        ["primary"] = s.Primary,
        ["primary_reason"] = s.PrimaryReason,
        ["agents_seen"] = s.AgentsSeen,
        ["agents_active"] = s.AgentsActive,
        ["agents_capped"] = s.AgentsCapped,
    };

    // ── small Python-semantics helpers ──────────────────────────────────────────

    /// <summary>Python's `x or y` treats 0.0 as false; `started_at or status_updated_at`.</summary>
    private static double? NonZero(double? v) => v is double d && d != 0.0 ? d : null;

    /// <summary>Python truthiness of a JSON value.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0.0,
        _ => true,
    };

    /// <summary>Python `str(value)` for a JSON value (strings unquoted, null as "None").</summary>
    public static string PyStr(JsonNode? n) => n switch
    {
        null => "None",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        _ => n.ToJsonString(),
    };

    /// <summary>Python `int(value)` for a JSON value: numbers truncate, numeric strings parse, bools are 0/1.</summary>
    public static long? PyInt(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d))
            return double.IsFinite(d) && Math.Abs(d) < 9.2e18 ? (long)Math.Truncate(d) : null;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    /// <summary>Python `float(value)` for a JSON value, or null where float() would raise.</summary>
    public static double? PyFloat(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b ? 1.0 : 0.0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    /// <summary>`" ".join(text.split())`.</summary>
    public static string JoinWs(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>`Path(p).name` (Windows semantics: trailing separators ignored, "C:\" → "").</summary>
    public static string PathName(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var t = p.TrimEnd('\\', '/');
        if (t.Length == 0) return "";
        try { return Path.GetFileName(t) ?? ""; } catch { return ""; }
    }

    /// <summary>`Path(p).parent.name`.</summary>
    public static string ParentName(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try
        {
            var parent = Path.GetDirectoryName(p.TrimEnd('\\', '/'));
            return PathName(parent);
        }
        catch { return ""; }
    }

    /// <summary>`Path(p).parts`: the anchor first ("C:\" or "/"), then each segment.</summary>
    public static List<string> PathParts(string? p)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(p)) return parts;
        var segs = p.Split('\\', '/').ToList();
        if (segs.Count > 0 && segs[0].Length == 2 && segs[0][1] == ':')
        {
            parts.Add(segs[0] + "\\");
            segs.RemoveAt(0);
        }
        else if (p.StartsWith('\\') || p.StartsWith('/'))
            parts.Add(p[0].ToString());
        parts.AddRange(segs.Where(s => s.Length > 0 && s != "."));
        return parts;
    }

    /// <summary>`os.path.realpath`: absolute, with a symlink/junction at the end resolved.</summary>
    public static string RealPath(string p)
    {
        var full = Path.GetFullPath(Py.ExpandUser(p));
        try
        {
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) return Path.GetFullPath(target.FullName);
        }
        catch { }
        return full;
    }

    /// <summary>
    /// Path equality, trailing separator ignored. Windows: separators normalised,
    /// case-insensitive. Linux: exact (a backslash is an ordinary filename character there).
    /// </summary>
    public static bool SamePathText(string a, string b)
    {
        if (!Py.IsWindows) return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
        static string N(string x) => x.Replace('/', '\\').TrimEnd('\\');
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}
