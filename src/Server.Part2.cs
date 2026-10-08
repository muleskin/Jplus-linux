using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// server.py lines 2629–4910: the session / run / project voice tools —
/// `_say_age` through `tool_enable_session_inbox`, the untrusted-content
/// wrapper and its header-line walls, the staged steer / command / dialog
/// read-back pipeline, spawn/status/cancel of runs, create_project, and
/// opening a browser or a terminal.
/// </summary>
public static partial class Server
{
    // No FastAPI routes live in this part of server.py.
    public static void MapRoutes2(WebApplication app) { }


    // Part 1's `speech` global. Python's name collides with the `Speech` module
    // class in C#; read it through this one accessor so only this line depends
    // on what Part 1 called it.
    private static Jplus.Speech.SpeechScheduler? SpeechNow => SpeechInstance;

    // The last conversation JARVIS talked about, so "that one" can be resolved.
    public static string? LastMentionedSession = null;

    /// <summary>An age a person would say out loud. Never a timestamp.</summary>
    public static string SayAge(double? seconds)
    {
        if (seconds is null || seconds < 0) return "at some point";
        double s = seconds.Value;
        if (s < 30) return "just now";
        if (s < 120) return "about a minute ago";
        if (s < 3600) return $"about {(long)Math.Floor(s / 60)} minutes ago";
        if (s < 7200) return "about an hour ago";
        if (s < 86400) return $"about {(long)Math.Floor(s / 3600)} hours ago";
        if (s < 172800) return "yesterday";
        return $"{(long)Math.Floor(s / 86400)} days ago";
    }

    public static readonly Regex TagOpenRe = new("<session-output", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex TagCloseRe = new("</session-output>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Swap the ASCII hyphen in a matched delimiter for a non-breaking one,
    /// whatever case the delimiter was written in.</summary>
    public static string BreakTagHyphen(Match match) => match.Value.Replace("-", "‑");

    // Headroom below the tool-result cap for the wrap's own tags plus whatever a
    // caller puts around it, so `_cap_tool_result`'s end-of-string cut never
    // lands between a `<session-output>` and its closing tag. (A property, not a
    // field: ToolResultCap lives in another partial file.)
    public static int WrapContentCap => ToolResultCap - 300;

    // The wrapper's NAME is interpolated raw into `name="…"`. The rule at every
    // call site: the name is a LITERAL and anything variable goes in the BODY.
    // The shape test is the second wall: no quote, bracket, `=` or newline.
    public static readonly Regex WrapNameShape = new(@"^[a-z][a-z ]{0,23}\z", RegexOptions.CultureInvariant);
    public const string WrapNameFallback = "untrusted content";

    public const string SessionsWrapName = "sessions";
    public const string SessionWrapName = "session";
    public const string RunWrapName = "run output";
    public const string ProjectWrapName = "project";
    public const string FileWrapName = "file";
    public const string DocumentWrapName = "document";
    public const string MemoryWrapName = "memory";
    public const string RunsWrapName = "runs";

    /// <summary>
    /// Everything another session said arrives clearly labelled. The delimiter
    /// is escaped case-insensitively so a transcript cannot close its own block,
    /// and the content is bounded BEFORE wrapping so the block always carries
    /// its own closing tag.
    /// </summary>
    public static string WrapUntrusted(string name, string text)
    {
        text ??= "";
        if (text.Length > WrapContentCap)
            text = text[..WrapContentCap].TrimEnd() + "\n… (truncated)";
        if (!WrapNameShape.IsMatch(name ?? ""))
        {
            P2.Log.LogWarning("wrapper name {Name} is not a literal; using the fallback", P2.Repr(name));
            name = WrapNameFallback;
        }
        var safe = TagOpenRe.Replace(text, BreakTagHyphen);
        safe = TagCloseRe.Replace(safe, BreakTagHyphen);
        return $"<session-output name=\"{name}\" untrusted=\"true\">\n{safe}\n</session-output>";
    }

    // Everything variable that has to sit in a HEADER line: whitespace
    // collapses (a newline forges a whole line of JARVIS), the delimiter's own
    // characters go, and the result is bounded.
    public static readonly Regex LabelUnsafe = new(@"[^\w \-./+@,:()\[\]']", RegexOptions.CultureInvariant);

    /// <summary>
    /// For text the USER supplied — his own search query, echoed back. NOT for
    /// text somebody else chose: there is no length at which prose stops being
    /// prose, so that goes through `PlainName` or the untrusted block.
    /// </summary>
    public static string SafeLabel(object? text, int limit = 80)
    {
        var cleaned = LabelUnsafe.Replace(string.Join(" ", P2.SplitWs(P2.PyStr(text))), "").Trim();
        return cleaned.Length > limit ? cleaned[..limit] + "…" : cleaned;
    }

    // A stricter rule for IDENTIFIERS — a repo-relative path, a session's roster
    // name, a project's directory name: either the value IS an ordinary name,
    // or it does not appear in the header at all. Full match, anchored with \z
    // (a trailing newline is one whole line of forged JARVIS).
    // Windows: `\` and `:` are added to the class so a drive-letter path (the
    // Windows form of a cwd) is still an identifier; neither can write a line or
    // a tag.
    public static readonly Regex PlainNameRe = new(@"^[\w.\-/+\\:]{1,60}\z", RegexOptions.CultureInvariant);

    public static string PlainName(object? text, string fallback)
    {
        var value = P2.PyStr(text);
        return PlainNameRe.IsMatch(value) ? value : fallback;
    }

    // For foreign values that legitimately have SPACES — a `waitingFor` reason,
    // a task heading out of a plan. Thirty-two characters, beginning and ending
    // on a word character; no quote, angle bracket, colon or line separator.
    public static readonly Regex PlainPhraseRe = new(@"^\w([\w \-./+]{0,30}\w)?\z", RegexOptions.CultureInvariant);

    public static string PlainPhrase(object? text, string fallback)
    {
        var value = P2.PyStr(text);
        return PlainPhraseRe.IsMatch(value) ? value : fallback;
    }

    // A conversation's voice name, as JARVIS may say it or write it to the
    // brain. Not an identifier — `_assign_voice_names` COMPOSES phrases
    // ("hammer, the memory tools one", "the newer hammer") — so the wall is on
    // the character class and a bound: no line separator, no `<`, `>`, `"` or
    // `=`. Sixty-four characters, ending on a word character.
    public static readonly Regex VoiceNameRe = new(@"^\w([\w ,.\-/+']{0,62}\w)?\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The voice name of a session, a staged item — or an event PAYLOAD (a
    /// mapping). One function, both shapes, so the next reader cannot pick the
    /// wrong wall by picking the wrong access.
    /// </summary>
    public static string SaidName(object? item, string fallback = "that session")
    {
        string value = item switch
        {
            null => "",
            SessionWatch.SessionState s => s.VoiceName ?? "",
            StagedSteer st => st.VoiceName ?? "",
            StagedDialog sd => sd.VoiceName ?? "",
            IDictionary<string, object?> d => d.TryGetValue("voice_name", out var v) ? P2.PyStr(P2.OrNull(v)) : "",
            JsonObject o => Py.Str(o, "voice_name") ?? "",
            _ => P2.PyStr(P2.OrNull(item.GetType().GetProperty("VoiceName")?.GetValue(item))),
        };
        return VoiceNameRe.IsMatch(value) ? value : fallback;
    }

    // --- JARVIS's own runs are not the user's conversations ------------------
    //
    // Every `spawn_run` starts a `claude -p` process that registers in the
    // roster like any other. `run_executor._command` passes the run id as
    // `--session-id`, so a roster session whose id is a row in `runs` IS a run
    // JARVIS started. Runs stay reportable through `run_status`; they are
    // simply not conversations.
    public const double RunIdsTtlSec = 2.0;
    public static (double Stamped, HashSet<string> Ids) RunIdsCache = (0.0, new HashSet<string>());

    /// <summary>
    /// Every session id that belongs to a run, cached briefly. Fails OPEN (an
    /// empty set, i.e. filter nothing) rather than hiding real conversations.
    /// </summary>
    public static HashSet<string> JarvisRunSessionIds()
    {
        var now = Py.Now();
        var (stamped, ids) = RunIdsCache;
        if (now - stamped < RunIdsTtlSec) return ids;
        try
        {
            ids = new HashSet<string>(RunStore.AllRunIds());
        }
        catch (SqliteException e)
        {
            // Expected before init_db has run; failing open is right, a
            // traceback for a routine startup ordering is noise.
            P2.Log.LogDebug("run ids unavailable ({Error}); not filtering the roster", e.Message);
            ids = new HashSet<string>();
        }
        catch (Exception e)
        {
            P2.Log.LogWarning(e, "could not read run ids; not filtering the roster");
            ids = new HashSet<string>();
        }
        RunIdsCache = (now, ids);
        return ids;
    }

    /// <summary>
    /// The conversations JARVIS talks about: the roster, minus his own runs.
    /// `Snapshot.Excluding` re-derives the voice names and "main" badge.
    /// </summary>
    public static SessionWatch.Snapshot SnapshotOrEmpty()
    {
        var snap = SessionWatcher is not null ? SessionWatcher.Snapshot : new SessionWatch.Snapshot();
        return snap.Excluding(JarvisRunSessionIds());
    }

    public static readonly Dictionary<string, string> StateWords = new()
    {
        [SessionWatch.Working] = "working",
        [SessionWatch.Idle] = "idle",
        [SessionWatch.NeedsYou] = "needs you",
        [SessionWatch.Shell] = "in a shell",
        [SessionWatch.Gone] = "finished",
        [SessionWatch.Fresh] = "not started",
        [SessionWatch.Unknown] = "running",
    };

    /// <summary>
    /// The word for a state, or a plain form of whatever the roster said — the
    /// roster is a JSON file some other process writes, and this lands in a
    /// HEADER line.
    /// </summary>
    public static string StateWord(object? state)
    {
        if (state is string key && StateWords.TryGetValue(key, out var known)) return known;
        return PlainName(state, "in a state I don't recognise");
    }

    // The only `waitingFor` reasons observed live, phrased for speech. The set
    // is OPEN, so an unknown one must fall back to something grammatical.
    public static readonly Dictionary<string, string> NeedsPhrases = new()
    {
        ["permission prompt"] = "a permission prompt",
        ["dialog open"] = "a dialog",
        ["input needed"] = "input",
    };

    /// <summary>
    /// A raw `waitingFor` reason turned into 'waiting on ...' for speech. An
    /// unknown reason goes through `PlainPhrase`: `waitingFor` is foreign text
    /// reaching header lines and a spoken URGENT announcement.
    /// </summary>
    public static string PhraseNeeds(string? reason)
    {
        if (reason is not null && NeedsPhrases.TryGetValue(reason, out var known))
            return $"waiting on {known}";
        return $"waiting on {PlainPhrase(reason, "something I cannot name")}";
    }

    /// <summary>One conversation, in full: state, why it's waiting, age, what
    /// it's on, and whether JARVIS can reach it.</summary>
    public static string SessionLine(SessionWatch.SessionState s, double now)
    {
        var age = P2.Truthy(s.Since) ? SayAge(now - s.Since!.Value) : "at some point";
        var bits = new List<string> { $"  {SaidName(s, "one of them")}: {StateWord(s.State)}" };
        if (!string.IsNullOrEmpty(s.Needs))
            bits.Add(PhraseNeeds(s.Needs) + (s.NeedsAHumanHand ? " — that one needs your own keystroke" : ""));
        if (s.State == SessionWatch.NeedsYou || s.State == SessionWatch.Idle)
            bits.Add($"since {age}");
        var summary = s.Summary();
        if (!string.IsNullOrEmpty(summary))
            bits.Add($"on “{summary}”");
        if (!s.Steerable && s.State != SessionWatch.Gone)
            bits.Add("(I cannot send to this one)");
        return string.Join(", ", bits);
    }

    /// <summary>Full per-conversation detail, grouped by project.</summary>
    public static string DetailedSessionListing(List<SessionWatch.SessionState> sessions, double now, bool header = true)
    {
        var groups = new Dictionary<string, List<SessionWatch.SessionState>>();
        foreach (var s in sessions)
        {
            if (!groups.TryGetValue(s.Project, out var g)) groups[s.Project] = g = [];
            g.Add(s);
        }
        var lines = new List<string>();
        if (header)
        {
            int nConv = sessions.Count, nProj = groups.Count;
            lines.Add($"{nConv} conversation{(nConv != 1 ? "s" : "")} in {nProj} project{(nProj != 1 ? "s" : "")}:");
        }
        foreach (var project in groups.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var group = groups[project];
            if (group.Count > 1) lines.Add($"{project} — {group.Count} conversations:");
            foreach (var s in group) lines.Add(SessionLine(s, now));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Voice name, the reason, whether it needs a human keystroke, and its age.
    /// Its only caller is deliberately NOT wrapped, so every value here sits in
    /// a line the brain reads as JARVIS's own.
    /// </summary>
    public static string NeedsYouClause(SessionWatch.SessionState s, double now)
    {
        var age = P2.Truthy(s.Since) ? SayAge(now - s.Since!.Value) : "at some point";
        var bits = new List<string> { SaidName(s, "one of them") };
        if (!string.IsNullOrEmpty(s.Needs))
            bits.Add(PhraseNeeds(s.Needs) + (s.NeedsAHumanHand ? " that needs your own keystroke" : ""));
        bits.Add(age);
        return string.Join(", ", bits);
    }

    public static string NeedsYouSummary(List<SessionWatch.SessionState> needsYou, double now)
    {
        int n = needsYou.Count;
        var lead = n == 1 ? "One needs you: " : $"{n} need you: ";
        var clauses = needsYou.Select(s => NeedsYouClause(s, now)).ToList();
        var body = clauses.Count == 1
            ? clauses[0]
            : string.Join(", ", clauses.Take(clauses.Count - 1)) + $", and {clauses[^1]}";
        return lead + body + ".";
    }

    /// <summary>
    /// The remainder, summarised rather than itemised: project, counts and
    /// states, no per-session summary() quote. Not wrapped, so project names go
    /// through `PlainName` too.
    /// </summary>
    public static string RestSummary(List<SessionWatch.SessionState> rest)
    {
        var groups = new Dictionary<string, List<SessionWatch.SessionState>>();
        foreach (var s in rest)
        {
            var key = PlainName(s.Project, "an unnamed project");
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = [];
            g.Add(s);
        }
        var projects = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        int n = rest.Count;

        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var s in rest)
        {
            var st = s.State ?? "";
            if (!counts.ContainsKey(st)) { counts[st] = 0; order.Add(st); }
            counts[st]++;
        }
        // Python's max(): the first maximal item wins.
        string dominantState = order[0];
        foreach (var st in order) if (counts[st] > counts[dominantState]) dominantState = st;
        int dominantN = counts[dominantState];
        var mostly = dominantN * 2 > n ? $" — mostly {StateWord(dominantState)}" : "";

        const int named = 3;
        string listed;
        if (projects.Count <= named)
        {
            listed = string.Join(", ", projects);
        }
        else
        {
            int remaining = projects.Count - named;
            listed = string.Join(", ", projects.Take(named))
                     + $" and {remaining} other project{(remaining != 1 ? "s" : "")}";
        }
        return $"Otherwise {n} more across {listed}{mostly}.";
    }

    // Above this many non-`needs_you` conversations, per-session detail is
    // dropped in favour of a project-grouped count: ~130 chars each, so listing
    // them all is wrong for speech and, past a dozen, past the tool-result cap.
    public const int RestDetailThreshold = 6;

    /// <summary>
    /// Every conversation — urgency first, everything else adaptive. A
    /// `needs_you` conversation is never dropped or truncated.
    /// </summary>
    public static string ToolListSessions(JsonObject args)
    {
        var snap = SnapshotOrEmpty();
        var wanted = P2.Arg(args, "filter").Trim();
        var sessions = snap.Sessions.Where(s => s.Announceable).ToList();
        if (wanted.Length > 0) sessions = sessions.Where(s => s.State == wanted).ToList();
        if (sessions.Count == 0)
            return wanted.Length == 0 ? "Nothing is running." : $"Nothing is {StateWord(wanted)}.";

        var now = Py.Now();

        if (wanted.Length > 0)
        {
            // Each line embeds another session's title/prompt, so the whole
            // listing is wrapped once here rather than per session.
            return WrapUntrusted(SessionsWrapName, DetailedSessionListing(sessions, now));
        }

        // Most-recently-waiting first (by `since`), narrowed to the announceable set.
        var scopedIds = sessions.Select(s => s.SessionId).ToHashSet();
        var needsYou = snap.NeedingYou().Where(s => scopedIds.Contains(s.SessionId)).ToList();
        var rest = sessions.Where(s => s.State != SessionWatch.NeedsYou).ToList();

        int nConv = sessions.Count;
        int nProj = sessions.Select(s => s.Project).Distinct().Count();
        var lines = new List<string>
        {
            $"{nConv} conversation{(nConv != 1 ? "s" : "")} in {nProj} project{(nProj != 1 ? "s" : "")}:",
        };

        if (needsYou.Count > 0) lines.Add(NeedsYouSummary(needsYou, now));

        if (rest.Count > 0)
        {
            if (rest.Count <= RestDetailThreshold)
                // The only other place a per-session summary() reaches the result.
                lines.Add(WrapUntrusted(SessionsWrapName, DetailedSessionListing(rest, now, header: false)));
            else
                lines.Add(RestSummary(rest));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// (session, null, null) or (null, the sentence JARVIS should say, a short
    /// machine-readable reason: "unresolved" or "ambiguous").
    /// </summary>
    public static (SessionWatch.SessionState? Session, string? Problem, string? Reason) ResolveOrExplain(string name)
    {
        var snap = SnapshotOrEmpty();
        var matches = snap.Resolve(name, lastMentioned: LastMentionedSession);
        if (matches.Count == 0)
        {
            // `name` is the brain's own argument, composed from whatever it has
            // been reading. A reference not shaped like a name is DROPPED.
            var said = PlainName(name, "");
            return (null, "I don't see a session"
                          + (said.Length > 0 ? $" called {said}" : " by that name")
                          + ". Ask me what's running and I'll list them.", "unresolved");
        }
        if (matches.Count > 1)
        {
            var names = matches.Select(m => SaidName(m, "one of them")).ToList();
            var listed = string.Join(", ", names.Take(names.Count - 1)) + $" and {names[^1]}";
            return (null, $"There are {matches.Count}: {listed}. Which one?", "ambiguous");
        }
        return (matches[0], null, null);
    }

    /// <summary>Collapse consecutive duplicates, preserving order and recency.</summary>
    public static List<string> CollapseConsecutive(IEnumerable<string> items)
    {
        var output = new List<string>();
        foreach (var it in items)
            if (output.Count == 0 || output[^1] != it) output.Add(it);
        return output;
    }

    /// <summary>'Bash', 'Bash and Agent', 'Bash, Edit and Agent'.</summary>
    public static string JoinNatural(IList<string> items)
    {
        if (items.Count <= 1) return string.Join(", ", items);
        return string.Join(", ", items.Take(items.Count - 1)) + $" and {items[^1]}";
    }

    /// <summary>What one session is on, and where it left off.</summary>
    public static string ToolSessionDetail(JsonObject args)
    {
        var (session, problem, _) = ResolveOrExplain(P2.Arg(args, "name"));
        if (problem is not null) return problem;

        LastMentionedSession = session!.SessionId;
        if (session.State == SessionWatch.Fresh)
            return $"{SaidName(session, "That session")} is open in "
                   + $"{PlainName(session.Cwd, "a directory")} but has never been "
                   + "used — there's nothing in it yet.";

        var age = P2.Truthy(session.Since) ? SayAge(Py.Now() - session.Since!.Value) : "at some point";
        var head = new List<string>
        {
            $"{SaidName(session, "That session")} ({PlainName(session.Project, "a project")}) is "
            + $"{StateWord(session.State)}, as of {age}.",
        };
        if (!string.IsNullOrEmpty(session.Needs))
            head.Add($"It is {PhraseNeeds(session.Needs)}"
                     + (session.NeedsAHumanHand ? ", which needs your own keystroke — I cannot answer it." : "."));
        if (session.RecentTools is { Count: > 0 })
        {
            // A tool name is an identifier, so anything not shaped like one is not one.
            var tools = session.RecentTools.Select(t => PlainName(t, "something")).ToList();
            head.Add($"Recently using: {JoinNatural(CollapseConsecutive(tools))}.");
        }
        if (!session.Steerable)
            head.Add("I cannot send messages to this one — it has no inbox socket.");

        // The TOPIC belongs in the block with the rest of what that session said.
        var body = new List<string>();
        if (!string.IsNullOrEmpty(session.Needs))
            // The reason again, RAW — the header may have declined to say it.
            body.Add($"Waiting for: {session.Needs}");
        if (!string.IsNullOrEmpty(session.Title)) body.Add($"Topic: {session.Title}");
        if (!string.IsNullOrEmpty(session.LastPrompt)) body.Add($"You last told it: {session.LastPrompt}");
        if (!string.IsNullOrEmpty(session.LastText)) body.Add($"It last said: {session.LastText}");
        var detail = string.Join("\n", head);
        if (body.Count > 0) detail += "\n" + WrapUntrusted(SessionWrapName, string.Join("\n", body));
        return detail;
    }

    public static string ToolListProjects(JsonObject args)
    {
        var snap = SnapshotOrEmpty();
        var groups = snap.ByProject();
        if (groups.Count == 0) return "No projects have sessions open.";
        var lines = new List<string>();
        foreach (var project in groups.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var group = groups[project];
            // A project name can span more than one directory, so list every
            // distinct one. Both are DIRECTORY names out of another process's roster.
            var cwds = group.Select(s => PlainName(s.Cwd, "a directory")).Distinct()
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
            var where = cwds.Count == 1 ? cwds[0] : JoinNatural(cwds);
            lines.Add($"{PlainName(project, "an unnamed project")} ({where}): {group.Count} "
                      + $"conversation{(group.Count != 1 ? "s" : "")}");
        }
        return string.Join("\n", lines);
    }

    private static double? _steerCancelWindow;

    /// <summary>JARVIS_STEER_CANCEL_WINDOW (default 2.0s). Read on first use
    /// rather than at type init, so the `.env` loader has run.</summary>
    public static double SteerCancelWindow =>
        _steerCancelWindow ??= double.Parse(Py.Getenv("JARVIS_STEER_CANCEL_WINDOW", "2.0")!, CultureInfo.InvariantCulture);

    // How long to wait for the read-back to actually finish PLAYING before
    // opening the cancel window: say() returns once QUEUED, not once heard.
    public const double ReadbackTimeout = 60.0;

    /// <summary>A validated steer waiting for the current turn to finish speaking.</summary>
    public class StagedSteer
    {
        public string SessionId { get; set; } = "";
        public string VoiceName { get; set; } = "";
        public string Project { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string? SocketPath { get; set; }
    }

    /// <summary>
    /// A validated shell command waiting for the same read-back and window. It
    /// rides the steer staging list: one list means one drain, one ordering, and
    /// one place where "performed exactly once even if performing it raises" holds.
    /// </summary>
    public class StagedCommand
    {
        public string Project { get; set; } = "";
        public string Path { get; set; } = "";
        public string Command { get; set; } = "";
        public bool Documented { get; set; }
    }

    // Steers staged by the brain during the turn in flight, in order. Drained by
    // PerformStagedSteers() once the turn utterance is done — the work cannot
    // happen inside the tool call (see ToolSteerSession).
    public static List<object> StagedSteers = [];

    public static void StageSteer(object staged)
    {
        lock (StagedLock) StagedSteers.Add(staged);
    }

    private static readonly object StagedLock = new();

    /// <summary>Whether a steered message lands as a turn or as an approval
    /// prompt. Never raises.</summary>
    public static bool InboundAccepted()
    {
        try
        {
            return Preflight.CrossSessionInboundAccepted();
        }
        catch (Exception e)
        {
            P2.Log.LogWarning(e, "could not read crossSessionInbound");
            return false;
        }
    }

    /// <summary>The half-sentence that stops "sent" from being a lie.</summary>
    public static string InboundCaveat()
    {
        if (InboundAccepted()) return "";
        return " It'll ask you to approve it first — say the word and I'll set "
               + "your sessions to accept them.";
    }

    /// <summary>
    /// Read back, offer the cancel window, and send — after the turn has ended.
    /// Drains the staging list FIRST and unconditionally, so nothing is ever
    /// performed twice even if performing one raises.
    /// </summary>
    public static async Task PerformStagedSteers()
    {
        List<object> staged;
        lock (StagedLock) { staged = StagedSteers; StagedSteers = []; }
        foreach (var item in staged)
        {
            try
            {
                if (item is StagedCommand cmd) await PerformCommand(cmd);
                else await PerformSteer((StagedSteer)item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                var what = item is StagedSteer st && !string.IsNullOrEmpty(st.VoiceName)
                    ? st.VoiceName
                    : item is StagedCommand c ? c.Project : (item as StagedSteer)?.Project;
                P2.Log.LogError(e, "staged action for {What} failed: {Error}", what, e.Message);
            }
        }
    }

    /// <summary>
    /// One staged steer, start to finish. Records EXACTLY one audit row. The
    /// cancel window opens only AFTER the read-back has finished playing;
    /// `WasCancelled` is checked separately from `heard`; a read-back that never
    /// completes sends NOTHING; nothing is ever sent unheard.
    /// </summary>
    public static async Task PerformSteer(StagedSteer item)
    {
        bool recorded = false;
        void Record(string outcome)
        {
            if (recorded) return;
            recorded = true;
            RunStore.RecordSteer(item.SessionId, item.VoiceName, item.Project, item.Prompt, outcome);
        }

        try
        {
            var speech = SpeechNow;
            if (speech is null)
            {
                Record("no_voice");             // the mouth went away between turns
                return;
            }
            var utt = await speech.Say($"Telling {SaidName(item)}: {item.Prompt}", Jplus.Speech.Priority.Normal);
            var heard = await speech.WaitFor(utt, timeout: ReadbackTimeout);
            if (utt.WasCancelled)
            {
                if (utt.WasAbandoned)
                {
                    // A transport failure, not the user's decision: telling him
                    // "you cancelled it" would be a lie. Nothing was sent either way.
                    Record("readback_failed");
                    return;
                }
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }
            if (!heard)
            {
                // Never send something the user cannot be shown to have heard.
                Record("readback_failed");
                return;
            }
            if (await speech.OpenCancelWindow(SteerCancelWindow))
            {
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }

            var outcome = await Task.Run(() => SessionSteer.PostToSession(item.SocketPath, item.Prompt));
            Record(outcome);
            if (outcome == SessionSteer.Sent)
            {
                // SENT means the bytes left over the socket, nothing more —
                // delivery is not observable, so it is not asserted; and when
                // the setting says it WILL need approving, that is said too.
                await speech.Say($"Passed to {SaidName(item)}, sir." + InboundCaveat(), Jplus.Speech.Priority.Normal);
            }
            else if (outcome == SessionSteer.NotLive)
            {
                await speech.Say($"{SaidName(item)} didn't answer its socket, sir — it may "
                                 + "have just exited.", Jplus.Speech.Priority.Normal);
            }
            else
            {
                await speech.Say($"I couldn't deliver that to {SaidName(item)}, sir.", Jplus.Speech.Priority.Normal);
            }
        }
        catch
        {
            Record("failed");                   // the audit trail must never have a gap
            throw;
        }
    }

    // The audit trail's name for "this went to a Terminal window, not a session".
    public const string CommandAuditName = "a Terminal window";

    /// <summary>
    /// One staged command, start to finish. Records EXACTLY one audit row.
    /// Structurally identical to PerformSteer on purpose — this puts a command
    /// from LLM-generated text onto a real shell. The window is VISIBLE, never a
    /// hidden subprocess.
    /// </summary>
    public static async Task PerformCommand(StagedCommand item)
    {
        bool recorded = false;
        void Record(string outcome)
        {
            if (recorded) return;
            recorded = true;
            RunStore.RecordSteer("", CommandAuditName, item.Project, item.Command, outcome);
        }

        try
        {
            var speech = SpeechNow;
            if (speech is null)
            {
                Record("no_voice");
                return;
            }
            // An undocumented command is not refused, it is flagged out loud.
            var caveat = item.Documented ? "" : " That isn't a command the project documents, mind.";
            var utt = await speech.Say($"Running {item.Command} in {item.Project}, sir.{caveat}", Jplus.Speech.Priority.Normal);
            var heard = await speech.WaitFor(utt, timeout: ReadbackTimeout);
            if (utt.WasCancelled)
            {
                if (utt.WasAbandoned)
                {
                    Record("readback_failed");
                    return;
                }
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }
            if (!heard)
            {
                Record("readback_failed");
                return;
            }
            if (await speech.OpenCancelWindow(SteerCancelWindow))
            {
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }

            // `cd` into the project first; the path is quoted, and the command has
            // already been through `Builds.CommandProblem`, which permits no shell
            // metacharacter. (cmd.exe form of `cd <shlex.quote(path)> && cmd`.)
            var result = await Actions.OpenTerminal($"{P2.CdCommand(item.Path)} && {item.Command}");
            if (P2.Bool(result, "success"))
            {
                Record("ran");
                await speech.Say("Running in a Terminal window, sir.", Jplus.Speech.Priority.Normal);
            }
            else
            {
                Record("failed");
                await speech.Say("Terminal wouldn't open, sir.", Jplus.Speech.Priority.Normal);
            }
        }
        catch
        {
            Record("failed");                   // the audit trail must never have a gap
            throw;
        }
    }

    /// <summary>
    /// A validated keypress waiting for the current turn to finish speaking.
    /// `Pid` is already resolved to a process whose tty was read, and `Key` has
    /// been through `Dialog.NormalizeKey` — a closed set of values.
    /// </summary>
    public class StagedDialog
    {
        public string SessionId { get; set; } = "";
        public string VoiceName { get; set; } = "";
        public string Project { get; set; } = "";
        public int Pid { get; set; }
        public string Key { get; set; } = "";   // normalized: "return", "escape", or one digit 1-9
    }

    // Keypresses staged during the turn in flight; drained after the turn
    // utterance ends, for the same reason as StagedSteers.
    public static List<StagedDialog> StagedDialogs = [];

    public static void StageDialog(StagedDialog staged)
    {
        lock (StagedLock) StagedDialogs.Add(staged);
    }

    /// <summary>Read back, offer the cancel window, and press — after the turn
    /// has ended. Drains FIRST, so a keypress can never happen twice.</summary>
    public static async Task PerformStagedDialogs()
    {
        List<StagedDialog> staged;
        lock (StagedLock) { staged = StagedDialogs; StagedDialogs = []; }
        foreach (var item in staged)
        {
            try
            {
                await PerformDialog(item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                P2.Log.LogError(e, "staged dialog for {Voice} failed: {Error}", item.VoiceName, e.Message);
            }
        }
    }

    /// <summary>
    /// One staged keypress, start to finish. Records EXACTLY one audit row.
    /// Same gate as PerformSteer, for a sharper reason: this steals the user's
    /// focus and types into a terminal.
    /// </summary>
    public static async Task PerformDialog(StagedDialog item)
    {
        bool recorded = false;
        var said = Dialog.SpokenKey(item.Key);
        void Record(string outcome)
        {
            if (recorded) return;
            recorded = true;
            RunStore.RecordSteer(item.SessionId, item.VoiceName, item.Project, item.Key, $"dialog:{outcome}");
        }

        try
        {
            var speech = SpeechNow;
            if (speech is null)
            {
                Record("no_voice");             // the mouth went away between turns
                return;
            }
            // The read-back names the key AND warns about the focus theft.
            var utt = await speech.Say($"Pressing {said} on {SaidName(item)} — this will bring that "
                                       + "window forward.", Jplus.Speech.Priority.Normal);
            var heard = await speech.WaitFor(utt, timeout: ReadbackTimeout);
            if (utt.WasCancelled)
            {
                if (utt.WasAbandoned)
                {
                    Record("readback_failed");
                    return;
                }
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }
            if (!heard)
            {
                Record("readback_failed");
                return;
            }
            if (await speech.OpenCancelWindow(SteerCancelWindow))
            {
                Record("cancelled_by_user");
                await speech.Say("Cancelled, sir.", Jplus.Speech.Priority.Normal);
                return;
            }

            var outcome = await Dialog.Answer(item.Pid, item.Key);
            Record(outcome);
            if (outcome == Dialog.Sent)
            {
                await speech.Say($"Pressed {said} on {SaidName(item)}.", Jplus.Speech.Priority.Normal);
            }
            else if (outcome == Dialog.NotFound)
            {
                await speech.Say($"{SaidName(item)} isn't in a Terminal window I can reach, "
                                 + "sir — another application is hosting it, so that one needs "
                                 + "your own hand.", Jplus.Speech.Priority.Normal);
            }
            else if (outcome == Dialog.NotPermitted)
            {
                // macOS-ism replaced: on Windows the refusal is UIPI (an elevated
                // window) or no interactive desktop, not an Accessibility setting;
                // on Linux it is the tmux / WezTerm socket refusing us.
                await speech.Say(Py.IsWindows
                    ? "Windows won't let me send keystrokes to that window, sir — it may be running as administrator."
                    : "That terminal won't let me send keystrokes to it, sir — it may belong to another user.",
                    Jplus.Speech.Priority.Normal);
            }
            else if (outcome == Dialog.NoTty)
            {
                await speech.Say($"{SaidName(item)} has no terminal of its own any more, "
                                 + "sir — I pressed nothing.", Jplus.Speech.Priority.Normal);
            }
            else
            {
                await speech.Say($"I couldn't press that for {SaidName(item)}, sir.", Jplus.Speech.Priority.Normal);
            }
        }
        catch
        {
            Record("failed");                   // the audit trail must never have a gap
            throw;
        }
    }

    /// <summary>
    /// (pid, tty, null), or (null, null, the sentence JARVIS should say). If the
    /// session's processes disagree about their terminal this asks rather than
    /// picking — the wrong answer types into the wrong window.
    /// </summary>
    public static async Task<(int? Pid, string? Tty, string? Problem)> TtyForSessionOrExplain(SessionWatch.SessionState session)
    {
        var found = new List<(string Tty, int Pid)>();
        var pids = session.Pids is { Count: > 0 }
            ? new List<int>(session.Pids)
            : (session.PrimaryPid is int pp0 && pp0 != 0 ? new List<int> { pp0 } : new List<int>());
        if (session.PrimaryPid is int pp && pp != 0 && !pids.Contains(pp)) pids.Insert(0, pp);
        // Concurrently, off the voice path: one round-trip for all of them.
        var ttys = await Task.WhenAll(pids.Select(pid => Dialog.TtyForPidAsync(pid)));
        for (int i = 0; i < pids.Count; i++)
        {
            var tty = ttys[i];
            if (!string.IsNullOrEmpty(tty) && !found.Any(f => f.Tty == tty)) found.Add((tty, pids[i]));
        }
        if (found.Count == 0)
            return (null, null, $"{SaidName(session)} isn't attached to a terminal I can see, "
                                + "sir, so there's nothing for me to press.");
        if (found.Count > 1)
            return (null, null, $"{SaidName(session)} spans more than one terminal, sir — I "
                                + "won't guess which window to type into.");
        return (found[0].Pid, found[0].Tty, null);
    }

    /// <summary>
    /// Validate the user's decision to press a key, and STAGE it. Same shape as
    /// ToolSteerSession and for the same reason (a read-back queued mid-turn is
    /// queued BEHIND the turn waiting on it). Three refusals, never guesses:
    /// exactly one conversation, exactly one controlling terminal, a key inside
    /// `Dialog`'s closed vocabulary. Whether a terminal window actually owns that
    /// tty is the staged phase's job.
    /// </summary>
    public static async Task<string> ToolAnswerDialog(JsonObject args)
    {
        var name = P2.Arg(args, "name");
        var rawKey = P2.Arg(args, "key");
        var (session, problem, reason) = ResolveOrExplain(name);
        if (problem is not null)
        {
            RunStore.RecordSteer("", name, "", rawKey, $"dialog:{(string.IsNullOrEmpty(reason) ? "unresolved" : reason)}");
            return problem;
        }

        var key = Dialog.NormalizeKey(rawKey);
        if (key is null)
        {
            // JARVIS presses keys, he does not type: no best-effort reading of free text.
            RunStore.RecordSteer(session!.SessionId, session.VoiceName, session.Project, rawKey, "dialog:bad_key");
            return "I can only press Return, Escape, or a single numbered option "
                   + "between one and nine — nothing else goes into that terminal. "
                   + "Which of those did the user mean?";
        }

        LastMentionedSession = session!.SessionId;

        var (pid, _, ttyProblem) = await TtyForSessionOrExplain(session);
        if (ttyProblem is not null)
        {
            RunStore.RecordSteer(session.SessionId, session.VoiceName, session.Project, key, "dialog:no_tty");
            return ttyProblem;
        }

        if (SpeechNow is null)
        {
            // No voice means no read-back, and no read-back means no gate at all.
            RunStore.RecordSteer(session.SessionId, session.VoiceName, session.Project, key, "dialog:no_voice");
            return "I can't read that back to you right now, sir, so I won't press "
                   + $"anything in {SaidName(session)} unannounced.";
        }

        StageDialog(new StagedDialog
        {
            SessionId = session.SessionId,
            VoiceName = session.VoiceName,
            Project = session.Project,
            Pid = pid!.Value,
            Key = key,
        });
        return "staged — I'll say what I'm about to press and then press "
               + $"{Dialog.SpokenKey(key)} on {SaidName(session)} the moment this "
               + "turn ends, unless he stops me. It only works if that session is "
               + "in a Terminal window; if it isn't, he'll be told. Say briefly "
               + "that it is going out and end your turn; do not call this tool "
               + "again for it.";
    }

    /// <summary>
    /// Validate the user's decision and STAGE it; the server sends it later.
    /// The policy: JARVIS says what he is about to send, waits a moment, and
    /// sends unless told to stop. None of that can happen here — this runs
    /// mid-turn while the turn utterance is still open, and the scheduler will
    /// not advance past an open utterance, so a read-back queued from here
    /// would deadlock behind the turn waiting on it. Everything the brain must
    /// be told is decided now; PerformStagedSteers() does the rest.
    /// </summary>
    public static async Task<string> ToolSteerSession(JsonObject args)
    {
        var name = P2.Arg(args, "name");
        var (session, problem, reason) = ResolveOrExplain(name);
        if (problem is not null)
        {
            // Record what we do know so "did you send that?" always has an answer.
            RunStore.RecordSteer("", name, "", P2.Arg(args, "prompt"), string.IsNullOrEmpty(reason) ? "unresolved" : reason);
            return problem;
        }
        var prompt = P2.Arg(args, "prompt").Trim();
        if (prompt.Length == 0)
        {
            RunStore.RecordSteer(session!.SessionId, session.VoiceName, session.Project, "", "empty_prompt");
            return "There was nothing to send.";
        }

        LastMentionedSession = session!.SessionId;

        if (session.NeedsAHumanHand)
        {
            RunStore.RecordSteer(session.SessionId, session.VoiceName, session.Project, prompt, "needs_a_human_hand");
            // `waitingFor` is foreign text and this sentence has no block around it.
            return $"{SaidName(session)} is {PhraseNeeds(session.Needs)}, "
                   + "which the socket cannot answer. Ask me to answer it instead "
                   + "and I'll send the keystroke, if that permission prompt is in "
                   + "a Terminal window — use answer_dialog, not this tool.";
        }
        if (!session.Steerable)
        {
            // Distinct from SessionSteer.NotLive: this one never had a socket.
            RunStore.RecordSteer(session.SessionId, session.VoiceName, session.Project, prompt, "not_steerable");
            return $"I can't send anything to {SaidName(session)} — it has no "
                   + "inbox socket, so it was started before cross-session "
                   + "messaging or declined to bind one.";
        }

        if (SpeechNow is null)
        {
            // No voice → no read-back → no safety gate at all.
            RunStore.RecordSteer(session.SessionId, session.VoiceName, session.Project, prompt, "no_voice");
            return "I can't read that back to you right now, sir, so I won't send "
                   + $"it to {SaidName(session)} unheard.";
        }

        StageSteer(new StagedSteer
        {
            SessionId = session.SessionId,
            VoiceName = session.VoiceName,
            Project = session.Project,
            Prompt = prompt,
            SocketPath = session.SocketPath,
        });
        var stagedNote = "staged — I'll read it back to the user and send it to "
                         + $"{SaidName(session)} the moment this turn ends, unless "
                         + "he stops me. Say briefly that it is going out and end "
                         + "your turn; do not call this tool again for it.";
        if (!InboundAccepted())
        {
            // The brain must not say "sent" when the message will sit unapproved.
            stagedNote += " NOTE: that session is not set to accept inbound "
                          + "messages, so it will ask the user to approve it. Say "
                          + "that too, and that you can turn it on if he wants "
                          + "(enable_session_inbox) — never do that unasked.";
        }
        return stagedNote;
    }

    // A project is named by a DIRECTORY, and a directory name may hold a quote,
    // an angle bracket or a newline. The wall is at the DOOR of the map every
    // resolver reads: a project JARVIS cannot say aloud is a project he does not
    // know. A PATH is walled for no-line / no-tag only.
    // Windows: a path starts with a drive letter or a UNC `\\`, not only `/`.
    public static readonly Regex PlainPathRe = new(
        "^(?:/|[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\x00-\\x1f\\x7f-\\x9f<>\"=\\u2028\\u2029]{0,299}\\z",
        RegexOptions.CultureInvariant);

    public static bool ProjectNameSpeakable(object? name) => VoiceNameRe.IsMatch(P2.PyStr(name));

    public static bool ProjectPathSpeakable(object? path) => PlainPathRe.IsMatch(P2.PyStr(path));

    /// <summary>
    /// Every project JARVIS could start work in: name -> its directories. The
    /// watcher knows what is being worked on now; the cached scan knows what
    /// exists at all. Everything in the map is speakable by construction.
    /// </summary>
    public static Dictionary<string, HashSet<string>> ProjectCandidates()
    {
        var output = new Dictionary<string, HashSet<string>>();
        foreach (var (project, group) in SnapshotOrEmpty().ByProject())
        {
            if (!string.IsNullOrEmpty(project) && ProjectNameSpeakable(project))
            {
                if (!output.TryGetValue(project, out var set)) output[project] = set = [];
                foreach (var s in group)
                    if (!string.IsNullOrEmpty(s.Cwd) && ProjectPathSpeakable(s.Cwd)) set.Add(s.Cwd);
            }
        }
        foreach (var entry in CachedProjects.ToList())
        {
            var name = P2.Get(entry, "name");
            var path = P2.Get(entry, "path");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path)
                && ProjectNameSpeakable(name) && ProjectPathSpeakable(path))
            {
                if (!output.TryGetValue(name, out var set)) output[name] = set = [];
                set.Add(path);
            }
        }
        return output.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// (name, path, null), or (null, null, the sentence JARVIS should say).
    /// Never guesses: this starts an unattended Claude Code process with
    /// --dangerously-skip-permissions in whatever directory comes back. Asked
    /// twice over — which project, and which directory when one name spans more.
    /// </summary>
    public static (string? Name, string? Path, string? Problem) ResolveProjectOrExplain(string reference)
    {
        var candidates = ProjectCandidates();
        if (candidates.Count == 0)
            return (null, null, "I don't know of any projects to start that in, sir.");

        var refLower = reference.ToLowerInvariant();
        var exact = candidates.Keys.Where(n => n.ToLowerInvariant() == refLower).ToList();
        var matches = exact.Count > 0
            ? exact
            : candidates.Keys.Where(n => n.ToLowerInvariant().Contains(refLower)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (matches.Count == 0)
        {
            // By definition nothing matched the brain's argument, so it is not said.
            return (null, null, "I don't see that project, sir. Ask me which "
                                + "projects I know and I'll list them.");
        }
        if (matches.Count > 1)
            return (null, null, $"There are {matches.Count}: {JoinNatural(matches)}. Which one?");

        var name = matches[0];
        var paths = candidates[name].OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (paths.Count > 1)
            return (null, null, $"{name} lives in more than one place: "
                                + $"{JoinNatural(paths)}. Which one should I use?");
        return (name, paths[0], null);
    }

    // --- The unattended framing every spawned run is given -------------------
    //
    // A run is `claude -p`: one shot, nobody on the other end. The CLI still
    // loads the user's own skills, and one carries a hard human-approval gate;
    // a run that obeyed it asked a question, exited zero and built nothing. So
    // this states the OPERATING CONDITION and names the approval gate.
    public const string UnattendedPreamble =
        "[Unattended run] You are running with no human present. This is one "
        + "non-interactive turn: nobody will read a question you ask and no answer "
        + "can ever arrive, so ending your turn with a question means the work "
        + "simply never happens. Do not ask clarifying questions. Do not present a "
        + "plan, a design or a list of options for approval, and do not invoke any "
        + "brainstorming or planning skill that requires the user to approve "
        + "something before you implement — that approval cannot be given here. "
        + "Where the task leaves a choice open, decide it sensibly yourself, say in "
        + "one line what you chose, and carry on. Finish the work: actually create "
        + "and edit the files before your turn ends.\n\n"
        + "The task, in the user's own words:\n";

    /// <summary>The prompt a spawned run is actually given: the user's text
    /// appended VERBATIM.</summary>
    public static string ComposeRunPrompt(string userPrompt) => UnattendedPreamble + userPrompt;

    /// <summary>The user's half of a stored run prompt, for anything spoken aloud.</summary>
    public static string UserPromptOf(string? storedPrompt)
    {
        var text = storedPrompt ?? "";
        if (text.StartsWith(UnattendedPreamble, StringComparison.Ordinal))
            return text[UnattendedPreamble.Length..];
        if (Builds.IsBuildPrompt(text))
        {
            // A build's prompt is framing to its last line; its topic comes off the spec path.
            var gist = Builds.GistOfBuild(text);
            return string.IsNullOrEmpty(gist) ? "a build" : gist;
        }
        return text;
    }

    // What a person says when they mean a model. A full model id is passed through.
    public static readonly string[] ModelFamilies = ["opus", "sonnet", "haiku", "fable"];

    // Heard-not-typed spellings out of speech recognition — phonetically close,
    // lexically far enough that the fuzzy pass would miss them.
    public static readonly Dictionary<string, string> ModelMishearings = new()
    {
        ["sonic"] = "sonnet", ["sonnett"] = "sonnet", ["sonet"] = "sonnet",
        ["sonnet's"] = "sonnet", ["sonic five"] = "sonnet", ["sonnet five"] = "sonnet",
        ["opis"] = "opus", ["opals"] = "opus", ["octopus"] = "opus", ["oh pus"] = "opus",
        ["opus five"] = "opus", ["campus"] = "opus",
        ["haiko"] = "haiku", ["high coo"] = "haiku", ["haiku's"] = "haiku",
        ["table"] = "fable", ["fabel"] = "fable", ["fable five"] = "fable",
    };

    public static readonly Regex ModelIdRe = new(@"^claude-[a-z0-9][a-z0-9.\-]{0,62}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// A spoken model name, resolved to a family the CLI knows — or null when
    /// nothing recognisable was said, so the caller asks again rather than
    /// silently running on the wrong model. A full id must have the shape of one.
    /// </summary>
    public static string? NormaliseModel(string? raw)
    {
        var spoken = string.Join(" ", P2.SplitWs(raw ?? "")).ToLowerInvariant();
        if (spoken.Length == 0) return null;
        if (spoken.StartsWith("claude-", StringComparison.Ordinal))
        {
            if (!ModelIdRe.IsMatch(spoken)) return null;
            var plain = PlainName(spoken, "");
            return plain.Length > 0 ? plain : null;
        }

        if (ModelMishearings.TryGetValue(spoken, out var heard)) return heard;

        foreach (var family in ModelFamilies)
        {
            if (spoken == family || spoken.StartsWith(family + " ", StringComparison.Ordinal)
                || Regex.IsMatch(spoken, "^" + Regex.Escape(family) + @"[-\s]?[\d.]+\z", RegexOptions.CultureInvariant))
                return family;
        }

        // A near-miss on the bare word: cut the version off first so "sonnit 4.5"
        // still lands. 0.75 keeps "haiku" and "fable" apart.
        var head = Regex.Replace(spoken, @"[-\s]?[\d.]+$", "", RegexOptions.CultureInvariant).Trim();
        if (ModelMishearings.TryGetValue(head, out var heard2)) return heard2;
        return P2.GetCloseMatch(head, ModelFamilies, 0.75);
    }

    /// <summary>A `true` that arrived as the string "true" must not mean False.</summary>
    public static bool TruthyArg(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s))
            return s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
        return P2.NodeTruthy(value);
    }

    /// <summary>
    /// The most recent FINISHED run in this exact directory, or null. Narrow on
    /// purpose: resuming the wrong session is worse than starting cold.
    /// </summary>
    public static Dictionary<string, object?>? LastRunToResume(string projectName, string projectPath)
    {
        foreach (var run in RunStore.ListRuns(project: projectName, limit: 25))
        {
            if (P2.Get(run, "project_name") != projectName) continue;
            if (P2.Get(run, "project_path") != projectPath) continue;
            if (RunStore.RunStatus.Terminal.Contains(P2.Get(run, "status") ?? "")) return run;
        }
        return null;
    }

    /// <summary>
    /// Start NEW work. An ACTING tool, the most consequential one: it spawns a
    /// Claude Code process that edits files unattended. Returns as soon as the
    /// run is recorded; the model is read back from the store so what JARVIS says
    /// is what was persisted.
    /// </summary>
    public static async Task<string> ToolSpawnRun(JsonObject args)
    {
        var prompt = P2.Arg(args, "prompt").Trim();
        if (prompt.Length == 0) return "There was nothing to start.";
        var reference = P2.Arg(args, "project").Trim();
        if (reference.Length == 0) return "Which project should I start that in, sir?";
        var model = NormaliseModel(P2.Arg(args, "model"));

        var (name, path, problem) = ResolveProjectOrExplain(reference);
        if (problem is not null) return problem;
        if (RunExecutorInstance is null) return "I can't start anything just now, sir.";

        // A follow-up builds on the most recent finished run in this exact directory.
        string? resumeFrom = null;
        var askedToResume = TruthyArg(args["resume"]);
        if (askedToResume)
        {
            var previous = LastRunToResume(name!, path!);
            resumeFrom = previous is not null ? P2.Get(previous, "id") : null;
        }

        var composed = ComposeRunPrompt(prompt);

        string runId;
        try
        {
            runId = await RunExecutorInstance.Spawn(composed, name!, path!, "voice", resumeFrom: resumeFrom, model: model);
        }
        catch (Exception e)
        {
            P2.Log.LogError(e, "spawn_run failed for {Name}: {Error}", name, e.Message);
            return $"I couldn't start that in {name}, sir.";
        }

        // Nobody can say a UUID out loud: remember the referent for "cancel that".
        LastStartedRun = runId;

        var run = RunStore.GetRun(runId) ?? new Dictionary<string, object?>();
        var requested = P2.Get(run, "requested_model");
        var startedOn = !string.IsNullOrEmpty(requested) ? requested : model;
        string where;
        if (!string.IsNullOrEmpty(resumeFrom)) where = $"Picked up the last run in {name}";
        else if (askedToResume) where = $"Nothing to pick up in {name}, so I started fresh";
        else where = $"Started on {name}";
        return !string.IsNullOrEmpty(startedOn) ? $"{where}, running {startedOn}." : $"{where}, sir.";
    }

    // ---------------------------------------------------------------------------
    // Runs, spoken about: creating a project, checking on work, stopping it
    // ---------------------------------------------------------------------------

    // The run JARVIS himself started most recently. Never spoken.
    public static string? LastStartedRun = null;

    // What a person says instead of a run id. A closed set: anything else is a
    // project name, and an unrecognised reference asks rather than guesses.
    public static readonly HashSet<string> RunBackrefs =
    [
        "that", "that one", "it", "this", "this one", "one", "last one",
        "last", "latest", "latest one", "most recent", "most recent one",
        "run", "last run", "latest run", "current run", "current one",
        "job", "work", "one you just started", "thing you just started",
        "work you just started", "one you started", "you just started",
    ];

    // How far back a loose reference may reach among finished runs.
    public const int RunLookback = 20;

    // A failed run's `error` is somebody else's stderr: wrapped, and only a head.
    public const int RunErrorChars = 200;

    /// <summary>Everything a loose reference could mean: live first, then just ended.</summary>
    public static List<Dictionary<string, object?>> RecentRuns()
    {
        var active = RunStore.ListRuns(status: RunStore.RunStatus.Active.ToList(), limit: RunLookback);
        var recent = RunStore.ListRuns(limit: RunLookback);
        var seen = active.Select(r => P2.Get(r, "id")).ToHashSet();
        return active.Concat(recent.Where(r => !seen.Contains(P2.Get(r, "id")))).ToList();
    }

    public static string NormaliseRunReference(string reference)
    {
        var stripped = reference.Trim().Trim(' ', '.', ',', '?', '!', '\'', '"').ToLowerInvariant();
        if (stripped.StartsWith("the ", StringComparison.Ordinal)) stripped = stripped[4..];
        return stripped.Trim();
    }

    /// <summary>
    /// (runs, null) or (null, the sentence JARVIS should say). Never guesses —
    /// cancel_run shares this. In order: an exact run id, a back-reference, a
    /// project name (exact before substring), a few words of the prompt.
    /// </summary>
    public static (List<Dictionary<string, object?>>? Runs, string? Problem) ResolveRunsOrExplain(string? reference)
    {
        var refText = (reference ?? "").Trim();
        if (refText.Length == 0) return (null, "Which one, sir?");

        var direct = RunStore.GetRun(refText);
        if (direct is not null) return (new List<Dictionary<string, object?>> { direct }, null);

        var key = NormaliseRunReference(refText);
        if (RunBackrefs.Contains(key))
        {
            if (!string.IsNullOrEmpty(LastStartedRun))
            {
                var run = RunStore.GetRun(LastStartedRun);
                if (run is not null) return (new List<Dictionary<string, object?>> { run }, null);
            }
            return (null, "I haven't started anything of my own lately, sir.");
        }

        var pool = RecentRuns();
        if (pool.Count == 0) return (null, "I haven't started any work at all, sir.");

        var exact = pool.Where(r => (P2.Get(r, "project_name") ?? "").ToLowerInvariant() == key).ToList();
        var matches = exact.Count > 0
            ? exact
            : pool.Where(r => (P2.Get(r, "project_name") ?? "").ToLowerInvariant().Contains(key)).ToList();
        if (matches.Count == 0)
        {
            // The user's own words only — every stored prompt also carries the preamble.
            matches = pool.Where(r => UserPromptOf(P2.Get(r, "prompt") ?? "").ToLowerInvariant().Contains(key)).ToList();
        }
        if (matches.Count == 0)
        {
            // Nothing matched the brain's argument, so nothing true can be said of it.
            return (null, "I don't have any work under that name, sir. Ask me "
                          + "what's running and I'll tell you.");
        }

        var projects = matches.Select(RunProject).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (projects.Count > 1)
            return (null, $"There are {projects.Count}: {JoinNatural(projects)}. Which one?");
        return (matches, null);
    }

    /// <summary>
    /// A run's project name, as JARVIS may say it. `project_name` comes from a
    /// request body or a directory name on disk — foreign text — so `PlainName`.
    /// </summary>
    public static string RunProject(Dictionary<string, object?> run) =>
        PlainName(P2.Get(run, "project_name") ?? "", "an unnamed project");

    // What one run's gist may say: seven words is not a bound when one "word"
    // can be 100,000 characters.
    public const int GistWordChars = 24;
    public const int GistChars = 80;

    /// <summary>One line per run, for the block under a sentence that counts
    /// them. The sentence counts; the block names.</summary>
    public static string RunGists(IEnumerable<Dictionary<string, object?>> runs) =>
        string.Join("\n", runs.Select(r => $"- {RunGist(r)}"));

    /// <summary>A handful of words from the prompt, so two runs in one project
    /// can be told apart. Only ever printed INSIDE a block.</summary>
    public static string RunGist(Dictionary<string, object?> run, int words = 7)
    {
        var parts = P2.SplitWs(UserPromptOf(P2.Get(run, "prompt") ?? ""));
        if (parts.Count == 0) return "an unnamed job";
        var kept = parts.Take(words).Select(w => w.Length > GistWordChars ? w[..GistWordChars] : w);
        var gist = SafeLabel(string.Join(" ", kept), GistChars);
        if (gist.Length == 0) return "an unnamed job";
        return gist + (parts.Count > words ? "…" : "");
    }

    // How many events we read back to judge a run's outcome; more than this is
    // already the answer (it did a great deal of work).
    public const int OutcomeEventCap = 800;
    public const int OutcomePage = 200;

    /// <summary>
    /// Did a run that exited zero actually do anything? Fails OPEN, always: a run
    /// is only downgraded on positive evidence.
    /// </summary>
    public static string RunOutcome(Dictionary<string, object?> run)
    {
        var runId = P2.Get(run, "id");
        if (string.IsNullOrEmpty(runId)) return StreamParser.Ok;
        try
        {
            var total = RunStore.CountEvents(runId);
            if (total == 0 || total > OutcomeEventCap) return StreamParser.Ok;
            var events = new List<JsonObject>();
            int after = 0;
            while (events.Count < total)
            {
                var page = RunStore.GetEvents(runId, afterSeq: after, limit: OutcomePage);
                if (page is null || page.Count == 0) break;
                after = Convert.ToInt32(page[^1]["seq"], CultureInfo.InvariantCulture);
                foreach (var row in page)
                {
                    var parsed = StreamParser.ParseLine(P2.Get(row, "payload") ?? "");
                    if (parsed is not null) events.Add(parsed);
                }
            }
            return StreamParser.AssessOutcome(events, P2.Get(run, "result_text") ?? "");
        }
        catch (Exception e)
        {
            P2.Log.LogWarning(e, "could not assess run {RunId}; reporting it as it stands", runId);
            return StreamParser.Ok;
        }
    }

    /// <summary>One speakable sentence about one run: where, and how it is
    /// going. Ages, never timestamps.</summary>
    public static string DescribeRun(Dictionary<string, object?> run, bool withReason = false)
    {
        var project = RunProject(run);
        var status = P2.Get(run, "status");
        var now = Py.Now();

        if (status == RunStore.RunStatus.Queued)
        {
            var asked = SayAge(now - P2.NumOr(run, "created_at", now));
            return $"The work in {project} is queued behind something else, sir "
                   + $"— asked for {asked}.";
        }
        if (status == RunStore.RunStatus.Running)
        {
            var started = P2.NumOr(run, "started_at", P2.NumOr(run, "created_at", now));
            return $"The work in {project} is still going, sir — started "
                   + $"{SayAge(now - started)}.";
        }

        var ended = P2.Num(run, "ended_at");
        var when = ended is double e && e != 0 ? SayAge(now - e) : "at some point";
        if (status == RunStore.RunStatus.Succeeded)
        {
            var outcome = RunOutcome(run);
            if (outcome == StreamParser.Stalled)
            {
                var line0 = $"The work in {project} stopped to ask a question {when}, "
                            + "sir, so nothing was built — it needs the answer in the "
                            + "prompt.";
                var question = (P2.Get(run, "result_text") ?? "").Trim();
                if (withReason && question.Length > 0)
                    line0 += "\n" + WrapUntrusted(RunWrapName, P2.Head(question, RunErrorChars));
                return line0;
            }
            if (outcome == StreamParser.NoChanges)
                return $"The work in {project} finished {when}, sir, but I can't "
                       + "see that it changed anything.";
            return $"The work in {project} finished {when}, sir, and it worked.";
        }
        if (status == RunStore.RunStatus.Cancelled)
            return $"The work in {project} was stopped {when}, sir.";
        string line;
        if (status == RunStore.RunStatus.TimedOut)
            line = $"The work in {project} ran out of time {when}, sir.";
        else
            line = $"The work in {project} failed {when}, sir.";
        var reason = (P2.Get(run, "error") ?? "").Trim();
        if (withReason && reason.Length > 0)
            line += "\n" + WrapUntrusted(RunWrapName, P2.Head(reason, RunErrorChars));
        return line;
    }

    /// <summary>What is going on right now, with nothing to point at.</summary>
    public static string RunningNowSummary()
    {
        var active = RunStore.ListRuns(status: RunStore.RunStatus.Active.ToList(), limit: 10);
        if (active.Count == 0)
        {
            var recent = RunStore.ListRuns(limit: 1);
            if (recent.Count > 0)
                return $"Nothing is running just now, sir. {DescribeRun(recent[0])}";
            return "Nothing is running just now, sir.";
        }
        if (active.Count == 1) return DescribeRun(active[0]);
        var now = Py.Now();
        var items = active.Select(r =>
            $"{RunProject(r)}, started {SayAge(now - P2.NumOr(r, "started_at", P2.NumOr(r, "created_at", now)))}").ToList();
        return $"{P2.Capitalize(SayNumber(active.Count))} runs going, sir: {CapListing(items)}.";
    }

    /// <summary>How work JARVIS started is going. Read-only, so NOT an acting tool.</summary>
    public static string ToolRunStatus(JsonObject args)
    {
        var refText = P2.ArgOr(args, "run", "run_id").Trim();
        if (refText.Length == 0) return RunningNowSummary();

        var (runs, problem) = ResolveRunsOrExplain(refText);
        if (problem is not null) return problem;

        var active = runs!.Where(r => RunStore.RunStatus.Active.Contains(P2.Get(r, "status") ?? "")).ToList();
        var chosen = active.Count > 0 ? active : runs.Take(1).ToList();
        if (chosen.Count == 1) return DescribeRun(chosen[0], withReason: true);
        var project = RunProject(chosen[0]);
        return $"{P2.Capitalize(SayNumber(chosen.Count))} going in {project}, sir.\n"
               + WrapUntrusted(RunsWrapName, RunGists(chosen.Take(3)));
    }

    /// <summary>Stop work already in flight. An ACTING tool: it kills a process.</summary>
    public static async Task<string> ToolCancelRun(JsonObject args)
    {
        var refText = P2.ArgOr(args, "run", "run_id").Trim();
        if (refText.Length == 0) return "Which one should I stop, sir?";

        var (runs, problem) = ResolveRunsOrExplain(refText);
        if (problem is not null) return problem;

        var active = runs!.Where(r => RunStore.RunStatus.Active.Contains(P2.Get(r, "status") ?? "")).ToList();
        if (active.Count == 0)
            // Honest: nothing was stopped, because there was nothing left to stop.
            return $"There's nothing to stop, sir. {DescribeRun(runs[0])}";
        if (active.Count > 1)
        {
            var project = RunProject(active[0]);
            return $"There are {SayNumber(active.Count)} going in {project}, "
                   + "sir — which one?\n"
                   + WrapUntrusted(RunsWrapName, RunGists(active.Take(3)));
        }

        var run = active[0];
        if (RunExecutorInstance is null) return "I can't stop anything just now, sir.";
        var runId = P2.Get(run, "id")!;
        bool stopped;
        try
        {
            stopped = await RunExecutorInstance.Cancel(runId);
        }
        catch (Exception e)
        {
            P2.Log.LogError(e, "cancel_run failed for {RunId}: {Error}", runId, e.Message);
            return $"I couldn't stop the work in {RunProject(run)}, sir.";
        }
        if (stopped)
        {
            // It must not then be announced as a completion — they were just told.
            var projectName = P2.Get(run, "project_name");
            PendingRunCompletions.RemoveAll(p => p == projectName);
            return $"Stopped the work in {RunProject(run)}, sir.";
        }

        var latest = RunStore.GetRun(runId) ?? run;
        return $"It finished before I could stop it, sir. {DescribeRun(latest)}";
    }

    /// <summary>Add one project to the cache ResolveProjectOrExplain reads.
    /// Mutated in place rather than rebound.</summary>
    public static void RegisterProject(string name, string path)
    {
        foreach (var entry in CachedProjects)
            if (P2.Get(entry, "path") == path) return;
        CachedProjects.Add(new Dictionary<string, object?> { ["name"] = name, ["path"] = path, ["branch"] = "" });
    }

    /// <summary>
    /// Make a brand-new project directory, so spawn_run has somewhere to go. An
    /// ACTING tool; `ProjectMaker` validates the name and proves containment.
    /// Nothing here ever overwrites or deletes.
    /// </summary>
    public static async Task<string> ToolCreateProject(JsonObject args)
    {
        var raw = P2.Arg(args, "name").Trim();
        var description = P2.Arg(args, "description").Trim();
        if (raw.Length == 0) return "What should I call it, sir?";

        Dictionary<string, object?> result;
        try
        {
            result = await ProjectMaker.Create(raw, description);
        }
        catch (ProjectMaker.BadName)
        {
            // A spoken name is slugified now, so anything still refused is a path.
            return "I can't use that as a project name, sir — it looks like a "
                   + "path rather than a name.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            P2.Log.LogError(e, "create_project failed for {Raw}: {Error}", P2.Repr(raw), e.Message);
            return "I couldn't create that, sir.";
        }

        var rootName = P2.Get(result, "root_name");
        var where = string.IsNullOrEmpty(rootName) ? "projects" : rootName;
        var resultName = P2.Get(result, "name");
        if (!P2.Bool(result, "created"))
            return $"There's already a {resultName} in your {where} folder, "
                   + "sir — I've left it exactly as it is.";

        // Startable immediately, without waiting for a rescan.
        RegisterProject(resultName!, P2.Get(result, "path")!);

        if (!P2.Bool(result, "git"))
            return $"Created {resultName} in your {where} folder, sir, though "
                   + "I couldn't make it a git repository.";
        return $"Created {resultName} in your {where} folder, sir. "
               + "A fresh git repository with a README, ready to start work in.";
    }

    // ---------------------------------------------------------------------------
    // Opening the result: a browser, or a terminal
    // ---------------------------------------------------------------------------
    //
    // The dangerous half is `file://`. A target is text an LLM wrote, so an
    // absolute path is NEVER opened on trust: it is resolved and proven to sit
    // inside a known project. Only http and https URLs are opened as URLs.

    public static readonly string[] WebSchemes = ["http://", "https://"];

    // Opened when the target names a directory rather than a file.
    public static readonly string[] DirectoryIndexes = ["index.html", "index.htm"];

    // --- which browser -------------------------------------------------------
    //
    // JARVIS_DEFAULT_BROWSER, read at CALL time. Exactly two browsers can be
    // driven, so exactly two names are accepted; a third is refused out loud
    // rather than quietly falling through to Chrome.
    public static readonly Dictionary<string, string> BrowserNames = new()
    {
        ["chrome"] = "chrome", ["google chrome"] = "chrome", ["google-chrome"] = "chrome",
        ["chromium"] = "chrome",
        ["firefox"] = "firefox", ["mozilla firefox"] = "firefox", ["mozilla"] = "firefox",
    };

    public const string DefaultBrowserFallback = "chrome";

    /// <summary>The user's configured default browser, or Chrome.</summary>
    public static string DefaultBrowser()
    {
        var raw = (Py.Getenv("JARVIS_DEFAULT_BROWSER") ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return DefaultBrowserFallback;
        if (!BrowserNames.TryGetValue(raw, out var picked))
        {
            // Loudly in the log, quietly to the user.
            P2.Log.LogWarning("JARVIS_DEFAULT_BROWSER={Raw} is not a browser I can drive; using {Fallback}",
                P2.Repr(raw), DefaultBrowserFallback);
            return DefaultBrowserFallback;
        }
        return picked;
    }

    /// <summary>(browser, null), or (null, the sentence JARVIS should say).</summary>
    public static (string? Browser, string? Refusal) BrowserFor(JsonObject args)
    {
        var asked = P2.Arg(args, "browser").Trim().ToLowerInvariant();
        if (asked.Length == 0) return (DefaultBrowser(), null);
        if (!BrowserNames.TryGetValue(asked, out var picked))
            return (null, "I can only drive Chrome or Firefox, sir, not "
                          + $"{PlainName(asked, "that")} — I've opened nothing.");
        return (picked, null);
    }

    // --- the microphone is a real constraint, not a preference ---------------
    //
    // JARVIS's own voice page uses the Web Speech API's SpeechRecognition, which
    // Firefox does not implement at all. Opening JARVIS HIMSELF there is refused
    // with the reason; the read-only /dashboard is deliberately not covered.

    public static readonly HashSet<string> LoopbackHostnames = ["localhost", "127.0.0.1", "::1", "[::1]", "0.0.0.0"];

    // The Vite dev server, alongside the API port.
    public const int ViteDevPort = 5173;

    public static readonly HashSet<string> VoiceUiPaths = ["", "/", "/index.html"];

    /// <summary>True when this URL is JARVIS's own voice page on this machine.</summary>
    public static bool IsJarvisVoiceUi(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parts)) return false;
        if (parts.Scheme is not ("http" or "https")) return false;
        var host = (parts.Host ?? "").ToLowerInvariant();
        if (!LoopbackHostnames.Contains(host)) return false;
        // urlsplit's `port` is None when the URL names none.
        int? port = parts.IsDefaultPort ? null : parts.Port;
        var apiPort = int.Parse(Py.Getenv("JARVIS_PORT", "8340")!, CultureInfo.InvariantCulture);
        if (port != apiPort && port != ViteDevPort) return false;
        return VoiceUiPaths.Contains(parts.AbsolutePath);
    }

    public const string MicNeedsChrome =
        "My own interface only works in Chrome, sir — Firefox has no speech "
        + "recognition at all, so the microphone would be dead and you'd not be "
        + "able to say a word to me. I've left it. I'll open it in Chrome if you "
        + "like, or anything else in Firefox.";

    /// <summary>(name, resolved directory) for everything JARVIS could open in.</summary>
    public static List<(string Name, string Root)> ProjectRoots()
    {
        var output = new List<(string, string)>();
        foreach (var (name, paths) in ProjectCandidates())
        {
            foreach (var raw in paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                try { output.Add((name, P2.RealPath(raw))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return output;
    }

    /// <summary>
    /// (project name, resolved path) if `candidate` is inside a known project,
    /// else null. Resolved on both sides, so a link cannot make the comparison lie.
    /// </summary>
    public static (string Name, string Path)? InsideAProject(string candidate)
    {
        string real;
        try { real = P2.RealPath(candidate); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        foreach (var (name, root) in ProjectRoots())
            if (P2.IsSameOrUnder(real, root)) return (name, real);
        return null;
    }

    /// <summary>
    /// Whether `resolved` is a file JARVIS will not put on the user's screen.
    /// Containment alone would open `~/.ssh/id_rsa` (the home directory is a
    /// project here), where `look_at_screen` reads it straight back — so the
    /// repo readers' own two walls apply.
    /// </summary>
    public static bool TooPrivateToOpen(string resolved)
    {
        foreach (var (_, root) in ProjectRoots())
        {
            if (P2.IsSameOrUnder(resolved, root))
            {
                var relative = System.IO.Path.GetRelativePath(root, resolved);
                if (!string.IsNullOrEmpty(RepoRead.SensitiveReason(relative))) return true;
                break;
            }
        }
        return RepoRead.PrivateReason(resolved) is not null;
    }

    /// <summary>
    /// Which project a bare filename should be resolved against: what the user
    /// named, else the project of the run JARVIS started most recently. Never a
    /// search of every project on the machine.
    /// </summary>
    public static (string Name, string Path)? BaseProjectForOpen(string hint)
    {
        if (!string.IsNullOrEmpty(hint))
        {
            var (name, path, problem) = ResolveProjectOrExplain(hint);
            if (problem is null) return (name!, path!);
            return null;
        }
        if (!string.IsNullOrEmpty(LastStartedRun))
        {
            var run = RunStore.GetRun(LastStartedRun) ?? new Dictionary<string, object?>();
            var projectPath = P2.Get(run, "project_path");
            if (!string.IsNullOrEmpty(projectPath)) return (RunProject(run), projectPath);
        }
        return null;
    }

    /// <summary>Open a URL, or a file inside a project the user is talking about.</summary>
    public static async Task<string> ToolOpenInBrowser(JsonObject args)
    {
        var target = P2.Arg(args, "target").Trim();
        if (target.Length == 0) return "What should I open, sir?";
        var hint = P2.Arg(args, "project").Trim();
        var (which, refusal) = BrowserFor(args);
        if (refusal is not null) return refusal;

        var lowered = target.ToLowerInvariant();
        if (WebSchemes.Any(s => lowered.StartsWith(s, StringComparison.Ordinal)))
        {
            if (which != "chrome" && IsJarvisVoiceUi(target)) return MicNeedsChrome;
            var opened = await Actions.OpenBrowser(target, which!);
            var conf = P2.Get(opened, "confirmation");
            return string.IsNullOrEmpty(conf) ? "Opened that, sir." : conf;
        }
        if (target.Contains("://") || lowered.StartsWith("file:", StringComparison.Ordinal)
            || lowered.StartsWith("data:", StringComparison.Ordinal) || lowered.StartsWith("javascript:", StringComparison.Ordinal))
            return "I only open web addresses and files inside your projects, "
                   + "sir — that one I've left alone.";

        // A path. Work out which project it belongs to before touching the disk.
        var raw = Py.ExpandUser(target);
        string projectName;
        string resolved;
        if (System.IO.Path.IsPathFullyQualified(raw))
        {
            var found = InsideAProject(raw);
            if (found is null)
                return "That isn't inside a project I know, sir, so I've not opened it.";
            (projectName, resolved) = found.Value;
        }
        else
        {
            var parts = P2.PathParts(raw);
            (string Name, string Path)? baseProject = null;
            if (parts.Count > 0)
            {
                // "tony-starks-website/index.html" — the project names itself.
                var named = hint.Length == 0 ? BaseProjectForOpen(parts[0]) : null;
                if (named is not null && parts.Count > 1)
                {
                    baseProject = named;
                    raw = System.IO.Path.Combine(parts.Skip(1).ToArray());
                }
            }
            baseProject ??= BaseProjectForOpen(hint);
            if (baseProject is null) return "Which project is that in, sir?";
            string root;
            (projectName, root) = baseProject.Value;
            var found = InsideAProject(System.IO.Path.Combine(root, raw));
            if (found is null)
                return "That isn't inside a project I know, sir, so I've not opened it.";
            resolved = found.Value.Path;
        }

        if (Directory.Exists(resolved))
        {
            var index = DirectoryIndexes.Select(n => System.IO.Path.Combine(resolved, n)).FirstOrDefault(File.Exists);
            var folderName = PlainName(P2.PathName(resolved), "that folder");
            if (index is null)
                return $"There's nothing to open in {folderName}, sir — no index.html in it.";
            // Re-resolved, because containment was decided about a DIFFERENT path:
            // a link at `site/index.html` pointing at JARVIS's own data would
            // otherwise be opened.
            try
            {
                index = P2.RealPath(index);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return $"There's nothing to open in {folderName}, sir — no index.html in it.";
            }
            // And containment is re-decided too: the index may now point anywhere.
            var found = InsideAProject(index);
            if (found is null)
                return "That isn't inside a project I know, sir, so I've not opened it.";
            resolved = found.Value.Path;
        }
        if (!File.Exists(resolved))
        {
            // Opening nothing and saying it worked is the stalled-run bug again.
            return $"There's no {PlainName(P2.PathName(target), "such file")} in "
                   + $"{projectName}, sir — I've opened nothing.";
        }
        // Containment was the ONLY wall before this; checked after the index
        // step, so an index.html chosen for him is judged too.
        if (TooPrivateToOpen(resolved)) return RepoSensitiveRefusal;

        var result = await Actions.OpenBrowser(new Uri(resolved).AbsoluteUri, which!);
        if (!P2.Bool(result, "success"))
        {
            var conf = P2.Get(result, "confirmation");
            return string.IsNullOrEmpty(conf) ? "The browser wouldn't open, sir." : conf;
        }
        return $"Opened {PlainName(P2.PathName(resolved), "that file")} from {projectName}, sir.";
    }

    /// <summary>Open a terminal window in a project directory.</summary>
    public static async Task<string> ToolOpenInTerminal(JsonObject args)
    {
        var reference = P2.Arg(args, "project").Trim();
        if (reference.Length == 0) return "Which project, sir?";
        var (name, path, problem) = ResolveProjectOrExplain(reference);
        if (problem is not null) return problem;
        var result = await Actions.OpenTerminal(P2.CdCommand(path!));
        if (!P2.Bool(result, "success"))
        {
            var conf = P2.Get(result, "confirmation");
            return string.IsNullOrEmpty(conf) ? "Terminal wouldn't open, sir." : conf;
        }
        return $"Terminal's open in {name}, sir.";
    }

    /// <summary>
    /// Set `"crossSessionInbound": "accept"` in the user's settings.json — ONLY
    /// after the user has said yes out loud. A read-modify-write of the parsed
    /// object; a file that will not parse is refused rather than replaced.
    /// </summary>
    public static async Task<string> ToolEnableSessionInbox(JsonObject args)
    {
        if (InboundAccepted()) return "Already set, sir — messages go straight in.";
        var (ok, detail) = await Task.Run(() => Preflight.EnableCrossSessionInbound());
        if (!ok)
        {
            P2.Log.LogWarning("enable_session_inbox refused: {Detail}", detail);
            return "I couldn't change that settings file, sir — it isn't "
                   + "readable, so I've left it alone.";
        }
        return "Done, sir — your sessions will take messages from me without "
               + "asking. New sessions, at least; the ones already open keep the "
               + "old setting.";
    }

    /// <summary>Small Python-semantics helpers private to this part.</summary>
    public static class P2
    {
        public static readonly ILogger Log = Py.Log("jarvis");

        /// <summary>`str(x)` for the values that reach the walls above.</summary>
        public static string PyStr(object? x) => x switch
        {
            null => "None",
            string s => s,
            bool b => b ? "True" : "False",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            JsonNode n => n is JsonValue v && v.TryGetValue<string>(out var sv) ? sv : n.ToJsonString(),
            _ => x.ToString() ?? "",
        };

        /// <summary>`x or ""` before `str()`: falsy values become "".</summary>
        public static object? OrNull(object? x) => x switch
        {
            null => "",
            string s when s.Length == 0 => "",
            bool b when !b => "",
            _ => x,
        };

        /// <summary>`repr(s)` for log lines.</summary>
        public static string Repr(string? s) => s is null ? "None" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        /// <summary>`str.split()` with no arguments.</summary>
        public static List<string> SplitWs(string? s) =>
            (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        public static bool Truthy(double? d) => d is double v && v != 0;

        /// <summary>`str(args.get(key) or "")`.</summary>
        public static string Arg(JsonObject? args, string key)
        {
            if (args is null || !args.TryGetPropertyValue(key, out var node) || !NodeTruthy(node)) return "";
            return PyStr(node);
        }

        /// <summary>`str(args.get(a) or args.get(b) or "")`.</summary>
        public static string ArgOr(JsonObject? args, string a, string b)
        {
            var first = Arg(args, a);
            return first.Length > 0 ? first : Arg(args, b);
        }

        /// <summary>Python truthiness of a JSON value.</summary>
        public static bool NodeTruthy(JsonNode? node) => node switch
        {
            null => false,
            JsonObject o => o.Count > 0,
            JsonArray a => a.Count > 0,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
            JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
            _ => true,
        };

        /// <summary>A row value as a string (`row.get(key)`), or null.</summary>
        public static string? Get(IDictionary<string, object?>? d, string key)
        {
            if (d is null || !d.TryGetValue(key, out var v) || v is null) return null;
            return v switch
            {
                string s => s,
                JsonNode n => n is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : n.ToJsonString(),
                System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString(),
                _ => PyStr(v),
            };
        }

        public static double? Num(IDictionary<string, object?>? d, string key)
        {
            if (d is null || !d.TryGetValue(key, out var v) || v is null) return null;
            try
            {
                return v switch
                {
                    double x => x,
                    float x => x,
                    long x => x,
                    int x => x,
                    decimal x => (double)x,
                    string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
                    JsonValue jv when jv.TryGetValue<double>(out var jd) => jd,
                    _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
                };
            }
            catch { return null; }
        }

        /// <summary>`d.get(key) or fallback` for a number (0 is falsy).</summary>
        public static double NumOr(IDictionary<string, object?>? d, string key, double fallback) =>
            Num(d, key) is double v && v != 0 ? v : fallback;

        public static bool Bool(IDictionary<string, object?>? d, string key)
        {
            if (d is null || !d.TryGetValue(key, out var v) || v is null) return false;
            return v switch
            {
                bool b => b,
                string s => s.Length > 0,
                JsonNode n => NodeTruthy(n),
                _ => Num(d, key) is double x ? x != 0 : true,
            };
        }

        /// <summary>`s[:n]`.</summary>
        public static string Head(string s, int n) => s.Length > n ? s[..n] : s;

        /// <summary>`str.capitalize()`.</summary>
        public static string Capitalize(string s) =>
            s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

        /// <summary>
        /// `cd {shlex.quote(path)}` on Linux. On Windows the cmd.exe form: `/d` so a
        /// different drive works, and double quotes (a Windows path cannot contain one).
        /// </summary>
        public static string CdCommand(string path) => Py.IsWindows ? $"cd /d \"{path}\"" : $"cd {Py.ShQuote(path)}";

        /// <summary>`PurePath(p).parts` for a relative path: separators either way,
        /// empty and `.` components dropped.</summary>
        public static List<string> PathParts(string p) =>
            p.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToList();

        /// <summary>`Path(p).name`.</summary>
        public static string PathName(string p) => System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));

        /// <summary>
        /// `os.path.realpath`: absolute, with every symbolic link / junction along
        /// the path resolved (components that don't exist are kept as written).
        /// </summary>
        public static string RealPath(string path)
        {
            var full = System.IO.Path.GetFullPath(path);
            for (int hops = 0; hops < 40; hops++)
            {
                var root = System.IO.Path.GetPathRoot(full) ?? "";
                var rest = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                var current = root;
                bool changed = false;
                for (int i = 0; i < rest.Length; i++)
                {
                    var next = System.IO.Path.Combine(current, rest[i]);
                    FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                    if (info.Exists && info.LinkTarget is not null)
                    {
                        var target = info.LinkTarget;
                        var resolved = System.IO.Path.IsPathRooted(target)
                            ? target
                            : System.IO.Path.Combine(current, target);
                        var tail = rest.Skip(i + 1).ToArray();
                        full = System.IO.Path.GetFullPath(tail.Length > 0
                            ? System.IO.Path.Combine(new[] { resolved }.Concat(tail).ToArray())
                            : resolved);
                        changed = true;
                        break;
                    }
                    current = next;
                }
                if (!changed) return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
            }
            return full;
        }

        /// <summary>`real == root or root in real.parents` (case-insensitively on Windows only).</summary>
        public static bool IsSameOrUnder(string real, string root)
        {
            var a = real.TrimEnd('\\', '/');
            var b = root.TrimEnd('\\', '/');
            if (b.Length == 0) return a.StartsWith('/');   // root is "/"
            if (string.Equals(a, b, Py.PathComparison)) return true;
            return a.StartsWith(b + System.IO.Path.DirectorySeparatorChar, Py.PathComparison)
                   || a.StartsWith(b + "/", Py.PathComparison);
        }

        /// <summary>`difflib.get_close_matches(word, possibilities, n=1, cutoff)`.</summary>
        public static string? GetCloseMatch(string word, IEnumerable<string> possibilities, double cutoff)
        {
            string? best = null;
            double bestScore = -1;
            foreach (var x in possibilities)
            {
                var score = SequenceRatio(x, word);
                if (score < cutoff) continue;
                // heapq.nlargest over (score, x): ties go to the larger string.
                if (score > bestScore || (score == bestScore && string.CompareOrdinal(x, best) > 0))
                {
                    best = x;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>`SequenceMatcher(None, a, b).ratio()` (no junk; strings here are short).</summary>
        public static double SequenceRatio(string a, string b)
        {
            int total = a.Length + b.Length;
            if (total == 0) return 1.0;
            return 2.0 * Matching(a, 0, a.Length, b, 0, b.Length) / total;
        }

        private static int Matching(string a, int alo, int ahi, string b, int blo, int bhi)
        {
            if (alo >= ahi || blo >= bhi) return 0;
            int besti = alo, bestj = blo, bestsize = 0;
            var j2len = new Dictionary<int, int>();
            for (int i = alo; i < ahi; i++)
            {
                var newj2len = new Dictionary<int, int>();
                for (int j = blo; j < bhi; j++)
                {
                    if (b[j] != a[i]) continue;
                    int k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
                }
                j2len = newj2len;
            }
            if (bestsize == 0) return 0;
            return bestsize
                   + Matching(a, alo, besti, b, blo, bestj)
                   + Matching(a, besti + bestsize, ahi, b, bestj + bestsize, bhi);
        }
    }
}
