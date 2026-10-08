using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Jplus;

/// <summary>
/// Per-session token usage, read off Claude Code's own transcripts (port of usage_scan.py).
///
/// WHY: `UsageStore` holds the SUBSCRIPTION's limits; what each conversation
/// actually cost is only answerable from the transcripts, the only place a
/// per-message `usage` block is written down.
///
/// ON DISK, under each config root (see SessionWatch.ConfigRoots):
///     projects/&lt;encoded cwd&gt;/&lt;sessionId&gt;.jsonl                  the conversation
///     projects/&lt;encoded cwd&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.jsonl   one per subagent
///
/// THE TRAPS, all measured:
/// 1. THE ROOTS ARE HARDLINKED. Dedupe by (volume serial, file index) — Python's
///    (st_dev, st_ino) — or every token is doubled.
/// 2. THE CORPUS IS HUGE (548 MB). A Cache remembers each file's identity, length
///    and running totals; a rescan reads only appended bytes. A file that shrank or
///    changed identity is read again from zero.
/// 3. SUMMED INPUT TOKENS ARE NOT THE CONTEXT. The context is the LAST turn's
///    input + cache_read + cache_creation, reported separately.
///
/// HONESTY: absence stays absence (`measured: false`, `context_tokens: null`), and
/// JARVIS's own brain and runs are reported in their own bucket, never added into
/// the user's totals. Never raises on a file being written underneath it.
/// </summary>
public static partial class UsageScan
{
    private static readonly ILogger Log = Py.Log("jarvis.usage_scan");

    // How recently a subagent transcript must have been written for the agent to
    // read as "active" (never "running": there is no process to check).
    public const double ActiveWithinSec = 90.0;

    // The prompt a subagent was launched with, clipped for a list row.
    public const int MaxPrompt = 240;

    // Only lines carrying this substring are handed to the JSON parser — a filter
    // on work, never on truth: a line with a usage block always contains it.
    public static ReadOnlySpan<byte> UsageMarker => "\"output_tokens\""u8;

    // ── quantities ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The four counts the CLI reports, kept apart because they are not
    /// interchangeable: cache reads are most of a long conversation's input.
    /// </summary>
    public sealed record Tokens(long Input = 0, long Output = 0, long CacheRead = 0, long CacheCreation = 0)
    {
        public long Total => Input + Output + CacheRead + CacheCreation;

        public static Tokens operator +(Tokens a, Tokens b) =>
            new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheCreation + b.CacheCreation);

        public Dictionary<string, object?> AsDict() => new()
        {
            ["input"] = Input,
            ["output"] = Output,
            ["cache_read"] = CacheRead,
            ["cache_creation"] = CacheCreation,
            ["total"] = Total,
        };
    }

    public static readonly Tokens Zero = new();

    /// <summary>A token count, or 0. A JSON boolean is not a count.</summary>
    public static long Int(JsonNode? raw)
    {
        if (raw is not JsonValue v) return 0;
        if (v.TryGetValue<bool>(out _)) return 0;
        if (v.TryGetValue<string>(out _)) return 0;
        long value;
        if (v.TryGetValue<long>(out var l)) value = l;
        else if (v.TryGetValue<double>(out var d))
        {
            if (!double.IsFinite(d) || Math.Abs(d) >= 9.2e18) return 0;
            value = (long)Math.Truncate(d);
        }
        else return 0;
        return value > 0 ? value : 0;
    }

    public static Tokens TokensFrom(JsonNode? usage)
    {
        if (usage is not JsonObject u) return Zero;
        return new Tokens(
            Int(u["input_tokens"]),
            Int(u["output_tokens"]),
            Int(u["cache_read_input_tokens"]),
            Int(u["cache_creation_input_tokens"]));
    }

    // A moment DayKey can actually name. On Windows the floor is the epoch itself
    // (the Python's `fromtimestamp` refused negative epochs there); two years of
    // slack below year 9999 covers every UTC offset.
    public static readonly double DayMin = 0.0;
    public static readonly double DayMax = Py.ToEpoch(new DateTimeOffset(9997, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The ISO-8601 timestamp on a transcript line, as epoch seconds; null for
    /// anything DayKey could not then name.
    /// </summary>
    public static double? Epoch(JsonNode? stamp)
    {
        if (stamp is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0) return null;
        var text = s.EndsWith('Z') ? s[..^1] + "+00:00" : s;
        // A naive stamp is local time, as Python's `datetime.timestamp()` reads it.
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
            return null;
        var epoch = Py.ToEpoch(when);
        return epoch >= DayMin && epoch <= DayMax ? epoch : null;
    }

    /// <summary>
    /// The local calendar day a moment falls in. Local, not UTC: "today" has to
    /// mean the user's today or the headline number is wrong every evening.
    /// </summary>
    public static string DayKey(double epoch) =>
        Py.FromEpoch(epoch).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── one file's running totals ───────────────────────────────────────────────

    /// <summary>
    /// Everything read out of one transcript, and where reading stopped. Kept per
    /// FILE so an incremental re-read has somewhere to resume.
    /// </summary>
    public sealed class FileTotals
    {
        public long Dev { get; set; }
        public long Ino { get; set; }
        public long Offset { get; set; }
        public Tokens Tokens { get; set; } = Zero;
        public int Turns { get; set; }
        public Dictionary<string, Tokens> ByDay { get; set; } = new();
        public Dictionary<string, Tokens> ByModel { get; set; } = new();
        public double? FirstAt { get; set; }
        public double? LastAt { get; set; }
        public string Cwd { get; set; } = "";
        public string Model { get; set; } = "";
        public string Prompt { get; set; } = "";
        // The last turn's input+cache: what the model was actually carrying.
        public long? ContextTokens { get; set; }

        /// <summary>
        /// A scratch copy to parse into, so a half-finished read is thrown away
        /// rather than left in the cache. The buckets must be copied too.
        /// </summary>
        public FileTotals Copy()
        {
            var clone = (FileTotals)MemberwiseClone();
            clone.ByDay = new Dictionary<string, Tokens>(ByDay);
            clone.ByModel = new Dictionary<string, Tokens>(ByModel);
            return clone;
        }
    }

    /// <summary>
    /// Remembers where each transcript was last read to. Hold one for the life of
    /// the process. Keyed by path; every entry records the file identity it was
    /// built from, so a rotated file is never resumed into. Not thread-safe: the
    /// server serialises access with its own lock.
    /// </summary>
    public sealed class Cache
    {
        private readonly Dictionary<string, FileTotals> _files = new();
        // Subagent sidecars, by path. Written once at spawn and never rewritten.
        public Dictionary<string, Dictionary<string, object?>> Meta { get; } = new();

        public FileTotals? Get(string path) => _files.TryGetValue(path, out var t) ? t : null;

        public void Put(string path, FileTotals totals) => _files[path] = totals;

        public void ForgetMissing(HashSet<string> seen)
        {
            foreach (var key in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(key);
        }

        public int Count => _files.Count;
    }

    public static readonly Cache DefaultCache = new();

    public static void Add(Dictionary<string, Tokens> bucket, string key, Tokens tokens) =>
        bucket[key] = (bucket.TryGetValue(key, out var t) ? t : Zero) + tokens;

    /// <summary>
    /// (totals for this file, bytes read this call, was it readable at all).
    ///
    /// Reads only what was appended since the last call; a partial final line is
    /// left unconsumed. THE THIRD VALUE IS THE HONESTY ONE: a file that could not
    /// be stat'd or opened contributes nothing, and saying so is the difference
    /// between "you have used nothing" and "I could not look". THE CURSOR MOVES
    /// LAST: parsing goes into a scratch copy committed only once the chunk is
    /// through.
    /// </summary>
    public static (FileTotals totals, long read, bool wasRead) ScanFile(string path, Cache cache)
    {
        var prior = cache.Get(path);
        var st = Stat(path);
        if (st is null)
            // Transient or permanent: the last thing we knew is still the best answer.
            return (prior ?? new FileTotals(), 0, false);
        var (dev, ino, size, _) = st.Value;

        FileTotals totals;
        if (prior is not null && prior.Dev == dev && prior.Ino == ino && size >= prior.Offset)
        {
            if (size == prior.Offset) return (prior, 0, true);    // already read, to the last byte
            totals = prior.Copy();
        }
        else
        {
            // New, rotated (same path, different file), or shrank: any resume
            // would be a total that no longer describes it.
            totals = new FileTotals { Dev = dev, Ino = ino };
        }
        totals.Dev = dev;
        totals.Ino = ino;

        byte[] chunk;
        try
        {
            using var fh = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            fh.Seek(totals.Offset, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            fh.CopyTo(ms);
            chunk = ms.ToArray();
        }
        catch (Exception)
        {
            return (prior ?? totals, 0, false);
        }

        int cut = Array.LastIndexOf(chunk, (byte)'\n');
        if (cut < 0) return (totals, 0, true);                 // readable; nothing complete yet
        int consumed = cut + 1;

        int start = 0;
        while (start < consumed)
        {
            int nl = Array.IndexOf(chunk, (byte)'\n', start, consumed - start);
            int end = nl < 0 ? consumed : nl;
            try { ScanLine(totals, chunk.AsSpan(start, end - start)); }
            catch (Exception e)
            {
                // One line in a shape nobody predicted costs itself and nothing
                // around it: the alternative, once measured, was a 503 page-wide.
                Log.LogDebug(e, "unreadable transcript line in {Path}", path);
            }
            start = end + 1;
        }

        totals.Offset += consumed;
        cache.Put(path, totals);
        return (totals, consumed, true);
    }

    /// <summary>Fold one transcript line into a file's running totals.</summary>
    public static void ScanLine(FileTotals totals, ReadOnlySpan<byte> raw)
    {
        if (raw.IndexOf(UsageMarker) < 0)
        {
            NotePrompt(totals, raw);
            return;
        }
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (Exception) { return; }
        if (parsed is not JsonObject line) return;
        if (line["message"] is not JsonObject message) return;
        var tokens = TokensFrom(message["usage"]);
        if (tokens.Total == 0 && message["usage"] is not JsonObject) return;

        totals.Turns += 1;
        totals.Tokens = totals.Tokens + tokens;
        var model = Py.Str(message, "model");
        if (!string.IsNullOrEmpty(model))
        {
            Add(totals.ByModel, model, tokens);
            totals.Model = model;
        }
        var cwd = Py.Str(line, "cwd");
        if (!string.IsNullOrEmpty(cwd)) totals.Cwd = cwd;

        var when = Epoch(line["timestamp"]);
        if (when is double w)
        {
            Add(totals.ByDay, DayKey(w), tokens);
            totals.FirstAt = totals.FirstAt is null ? w : Math.Min(totals.FirstAt.Value, w);
            totals.LastAt = totals.LastAt is null ? w : Math.Max(totals.LastAt.Value, w);
        }
        // The context is what the LAST REAL request carried. A `<synthetic>` turn
        // with all four counts at zero is the CLI's own bookkeeping (it once
        // reported a 481k context as 0), so it leaves the previous reading standing.
        long carried = tokens.Input + tokens.CacheRead + tokens.CacheCreation;
        if (carried > 0) totals.ContextTokens = carried;
    }

    /// <summary>
    /// A subagent's first `user` line is the brief it was dispatched with — the
    /// only human-readable answer to "what is this agent doing".
    /// </summary>
    public static void NotePrompt(FileTotals totals, ReadOnlySpan<byte> raw)
    {
        if (totals.Prompt.Length > 0 || raw.IndexOf("\"user\""u8) < 0) return;
        string text0;
        try { text0 = Encoding.UTF8.GetString(raw); }
        catch { return; }
        if (!text0.Replace(", ", ",").Replace(": ", ":").Contains("\"isSidechain\":true", StringComparison.Ordinal))
            return;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (Exception) { return; }
        if (parsed is not JsonObject line || Py.Str(line, "type") != "user") return;
        var content = line["message"] is JsonObject msg ? msg["content"] : null;
        var text = "";
        if (content is JsonValue cv && cv.TryGetValue<string>(out var cs)) text = cs;
        else if (content is JsonArray arr)
        {
            foreach (var blockNode in arr)
            {
                if (blockNode is JsonObject block && Py.Str(block, "type") == "text")
                {
                    text = SessionWatch.Truthy(block["text"]) ? SessionWatch.PyStr(block["text"]) : "";
                    break;
                }
            }
        }
        text = SessionWatch.JoinWs(text);
        if (text.Length > 0) totals.Prompt = text.Length > MaxPrompt ? text[..MaxPrompt] : text;
        var cwd = Py.Str(line, "cwd");
        if (!string.IsNullOrEmpty(cwd) && totals.Cwd.Length == 0) totals.Cwd = cwd;
    }

    // ── discovery ───────────────────────────────────────────────────────────────

    /// <summary>Every file belonging to one conversation, across every root.</summary>
    public sealed class TranscriptSet(string sessionId)
    {
        public string SessionId { get; set; } = sessionId;
        public List<string> Main { get; set; } = [];
        public Dictionary<string, string> Agents { get; set; } = new();
    }

    private static IEnumerable<string> SortedEntries(string dir) =>
        Directory.GetFileSystemEntries(dir).OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// Map session id → its files, deduped by file identity. The two roots are
    /// hardlinked on the live machine, so an identity already seen is the SAME
    /// file reached by a second name, not a second file.
    /// </summary>
    public static Dictionary<string, TranscriptSet> Discover(IEnumerable<string>? roots = null)
    {
        var found = new Dictionary<string, TranscriptSet>();
        var seen = new HashSet<(long, long)>();

        bool Claim(string path)
        {
            var st = Stat(path);
            if (st is null) return false;
            return seen.Add((st.Value.dev, st.Value.ino));
        }

        foreach (var root in roots ?? SessionWatch.ConfigRoots())
        {
            var baseDir = Path.Combine(root, "projects");
            List<string> projectDirs;
            try { projectDirs = SortedEntries(baseDir).ToList(); }
            catch { continue; }
            foreach (var pdir in projectDirs)
            {
                List<string> entries;
                try { entries = SortedEntries(pdir).ToList(); }
                catch { continue; }
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry);
                    if (name.EndsWith(".jsonl", StringComparison.Ordinal) && File.Exists(entry))
                    {
                        if (Claim(entry))
                        {
                            var stem = Path.GetFileNameWithoutExtension(name);
                            if (!found.TryGetValue(stem, out var set)) found[stem] = set = new TranscriptSet(stem);
                            set.Main.Add(entry);
                        }
                    }
                    else if (Directory.Exists(entry))
                        ClaimAgents(entry, found, Claim);
                }
            }
        }
        return found;
    }

    public static void ClaimAgents(string sessionDir, Dictionary<string, TranscriptSet> found, Func<string, bool> claim)
    {
        List<string> agentFiles;
        try { agentFiles = SortedEntries(Path.Combine(sessionDir, "subagents")).ToList(); }
        catch { return; }
        var sessionName = Path.GetFileName(sessionDir);
        foreach (var f in agentFiles)
        {
            var name = Path.GetFileName(f);
            if (!(name.EndsWith(".jsonl", StringComparison.Ordinal) && File.Exists(f)) || !claim(f)) continue;
            var stem = Path.GetFileNameWithoutExtension(name);
            var agentId = stem.StartsWith("agent-", StringComparison.Ordinal) ? stem["agent-".Length..] : stem;
            if (!found.TryGetValue(sessionName, out var set)) found[sessionName] = set = new TranscriptSet(sessionName);
            set.Agents[agentId] = f;
        }
    }

    // ── the reduced picture ─────────────────────────────────────────────────────

    // The sidecar beside every subagent transcript (`agentType`, `description`,
    // `parentAgentId`, `spawnDepth`) — the ONLY place a subagent says what it is.
    // Its name is `agent-<id>.meta.json`: two suffixes, so replacing only the last
    // one finds nothing (that bug once shipped with the tests green).
    public const int MaxDescription = 120;
    public const string MetaSuffix = ".meta.json";

    /// <summary>
    /// The sidecar beside `path`, or empty fields. Written once at spawn, so cached
    /// by path forever. Never raises: it can be caught mid-write.
    /// </summary>
    public static Dictionary<string, object?> ReadAgentMeta(string path, Cache cache)
    {
        var side = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + MetaSuffix);
        if (cache.Meta.TryGetValue(side, out var hit)) return hit;
        JsonObject body;
        try { body = JsonNode.Parse(File.ReadAllText(side)) as JsonObject ?? new JsonObject(); }
        catch { body = new JsonObject(); }
        string S(string key) => SessionWatch.Truthy(body[key]) ? SessionWatch.PyStr(body[key]) : "";
        var desc = SessionWatch.JoinWs(S("description"));
        var output = new Dictionary<string, object?>
        {
            ["agent_type"] = S("agentType"),
            ["description"] = desc.Length > MaxDescription ? desc[..MaxDescription] : desc,
            ["parent_agent_id"] = S("parentAgentId"),
            ["depth"] = (int)Math.Min(int.MaxValue, Int(body["spawnDepth"])),
        };
        cache.Meta[side] = output;
        return output;
    }

    /// <summary>One subagent this conversation dispatched.</summary>
    public sealed class AgentUsage
    {
        public string AgentId { get; set; } = "";
        public string Model { get; set; } = "";
        public Tokens Tokens { get; set; } = Zero;
        public int Turns { get; set; }
        public double? FirstAt { get; set; }
        public double? LastAt { get; set; }
        public string Prompt { get; set; } = "";
        public bool Active { get; set; }
        // From the sidecar; empty when there isn't one (older transcripts).
        public string AgentType { get; set; } = "";
        public string Description { get; set; } = "";
        public string ParentAgentId { get; set; } = "";
        public int Depth { get; set; }

        public Dictionary<string, object?> AsDict() => new()
        {
            ["agent_id"] = AgentId,
            ["model"] = Model,
            ["tokens"] = Tokens.AsDict(),
            ["turns"] = Turns,
            ["first_at"] = FirstAt,
            ["last_at"] = LastAt,
            ["prompt"] = Prompt,
            ["active"] = Active,
            ["agent_type"] = AgentType,
            ["description"] = Description,
            ["parent_agent_id"] = ParentAgentId,
            ["depth"] = Depth,
        };
    }

    /// <summary>What one conversation, and everything it dispatched, has spent.</summary>
    public sealed class SessionUsage
    {
        public string SessionId { get; set; } = "";
        public string Cwd { get; set; } = "";
        public string Project { get; set; } = "";
        public Tokens Tokens { get; set; } = Zero;          // the conversation itself
        public int Turns { get; set; }
        public List<AgentUsage> Agents { get; set; } = [];
        public Tokens AgentTokens { get; set; } = Zero;     // everything it dispatched
        public Dictionary<string, Tokens> Models { get; set; } = new();
        public double? FirstAt { get; set; }
        public double? LastAt { get; set; }
        public long? ContextTokens { get; set; }           // the last turn's carried context, or null
        public bool Own { get; set; }                      // JARVIS's own machinery, not the user's
        // Day buckets, so the report can roll up the USER's days without JARVIS's
        // runs in them (Python attached this as `_by_day`). Not part of AsDict.
        public Dictionary<string, Tokens> ByDay { get; set; } = new();

        public Tokens TotalTokens => Tokens + AgentTokens;

        public int ActiveAgents => Agents.Count(a => a.Active);

        public Dictionary<string, object?> AsDict() => new()
        {
            ["session_id"] = SessionId,
            ["cwd"] = Cwd,
            ["project"] = Project,
            ["tokens"] = Tokens.AsDict(),
            ["agent_tokens"] = AgentTokens.AsDict(),
            ["total_tokens"] = TotalTokens.AsDict(),
            ["turns"] = Turns,
            ["context_tokens"] = ContextTokens,
            ["first_at"] = FirstAt,
            ["last_at"] = LastAt,
            ["active_agents"] = ActiveAgents,
            ["agents"] = Agents.Select(a => a.AsDict()).ToList(),
            ["models"] = ModelsList(Models),
            ["own"] = Own,
        };
    }

    public sealed record DayUsage(string Day, Tokens Tokens);

    /// <summary>`report()`'s result. (The function is BuildReport: C# cannot have a method and a nested type both named Report.)</summary>
    public sealed class Report
    {
        public bool Measured { get; set; }
        public double ScannedAt { get; set; }
        public List<SessionUsage> Sessions { get; set; } = [];
        public List<SessionUsage> OwnSessions { get; set; } = [];
        public Tokens Totals { get; set; } = Zero;
        public Tokens OwnTotals { get; set; } = Zero;
        public Tokens Today { get; set; } = Zero;
        public List<DayUsage> Daily { get; set; } = [];
        public Dictionary<string, Tokens> Models { get; set; } = new();
        public int Files { get; set; }
        public long BytesRead { get; set; }
        public List<string> Roots { get; set; } = [];
    }

    public static string ProjectOf(string cwd, string fallback) =>
        string.IsNullOrEmpty(cwd) ? fallback : SessionWatch.ProjectName(cwd);

    public static void Merge(Dictionary<string, Tokens> dst, Dictionary<string, Tokens> src)
    {
        foreach (var (key, value) in src) Add(dst, key, value);
    }

    private static List<Dictionary<string, object?>> ModelsList(Dictionary<string, Tokens> models) =>
        models.OrderBy(kv => -kv.Value.Total)
              .Select(kv => new Dictionary<string, object?> { ["model"] = kv.Key, ["tokens"] = kv.Value.AsDict() })
              .ToList();

    /// <summary>`report()`: read (incrementally) and reduce. Never raises on a file underneath it.</summary>
    public static Report BuildReport(IEnumerable<string>? roots = null, double? now = null, Cache? cache = null,
        IEnumerable<string>? ownSessionIds = null)
    {
        double at = now ?? Py.Now();
        cache ??= DefaultCache;
        var ownIds = ownSessionIds is null ? new HashSet<string>() : new HashSet<string>(ownSessionIds);
        var rootPaths = (roots ?? SessionWatch.ConfigRoots()).ToList();

        var sets = Discover(rootPaths);
        var brainCwd = SessionWatch.BrainCwd();

        var sessions = new List<SessionUsage>();
        long bytesRead = 0;
        // Files actually READ, not walked past: "0 tokens, read from 1 transcript"
        // must be impossible to render off a file nobody could open.
        int files = 0;
        var livePaths = new HashSet<string>();

        foreach (var (sessionId, group) in sets)
        {
            var mainTokens = Zero;
            int mainTurns = 0;
            var byDay = new Dictionary<string, Tokens>();
            var byModel = new Dictionary<string, Tokens>();
            double? firstAt = null, lastAt = null;
            long? context = null;
            var cwd = "";

            foreach (var path in group.Main)
            {
                livePaths.Add(path);
                var (totals, read, wasRead) = ScanFile(path, cache);
                files += wasRead ? 1 : 0;
                bytesRead += read;
                mainTokens = mainTokens + totals.Tokens;
                mainTurns += totals.Turns;
                Merge(byDay, totals.ByDay);
                Merge(byModel, totals.ByModel);
                if (cwd.Length == 0) cwd = totals.Cwd;
                if (totals.ContextTokens is not null) context = totals.ContextTokens;
                firstAt = Min(firstAt, totals.FirstAt);
                lastAt = Max(lastAt, totals.LastAt);
            }

            var agents = new List<AgentUsage>();
            var agentTokens = Zero;
            foreach (var (agentId, path) in group.Agents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                livePaths.Add(path);
                var (totals, read, wasRead) = ScanFile(path, cache);
                files += wasRead ? 1 : 0;
                bytesRead += read;
                agentTokens = agentTokens + totals.Tokens;
                Merge(byDay, totals.ByDay);
                Merge(byModel, totals.ByModel);
                if (cwd.Length == 0) cwd = totals.Cwd;
                firstAt = Min(firstAt, totals.FirstAt);
                lastAt = Max(lastAt, totals.LastAt);
                var meta = ReadAgentMeta(path, cache);
                agents.Add(new AgentUsage
                {
                    AgentId = agentId,
                    Model = totals.Model,
                    Tokens = totals.Tokens,
                    Turns = totals.Turns,
                    FirstAt = totals.FirstAt,
                    LastAt = totals.LastAt,
                    Prompt = totals.Prompt,
                    Active = totals.LastAt is not null && at - totals.LastAt.Value <= ActiveWithinSec,
                    AgentType = (string)meta["agent_type"]!,
                    Description = (string)meta["description"]!,
                    ParentAgentId = (string)meta["parent_agent_id"]!,
                    Depth = (int)meta["depth"]!,
                });
            }
            agents = agents.OrderBy(a => !a.Active).ThenBy(a => -(a.LastAt ?? 0.0)).ToList();

            string fallback;
            if (group.Main.Count > 0) fallback = SessionWatch.ParentName(group.Main[0]);
            else if (group.Agents.Count > 0)
            {
                // agent file → subagents → <sessionId> → <encoded cwd>
                var first = group.Agents.Values.First();
                var sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(first) ?? "") ?? "";
                fallback = SessionWatch.ParentName(sessionDir);
            }
            else fallback = "";
            bool own = ownIds.Contains(sessionId) || (cwd.Length > 0 && SamePath(cwd, brainCwd));

            sessions.Add(new SessionUsage
            {
                SessionId = sessionId,
                Cwd = cwd,
                Project = ProjectOf(cwd, fallback),
                Tokens = mainTokens,
                Turns = mainTurns,
                Agents = agents,
                AgentTokens = agentTokens,
                Models = byModel,
                FirstAt = firstAt,
                LastAt = lastAt,
                ContextTokens = context,
                Own = own,
                ByDay = byDay,
            });
        }

        cache.ForgetMissing(livePaths);

        List<SessionUsage> Ordered(IEnumerable<SessionUsage> xs) =>
            xs.OrderBy(s => s.LastAt is null).ThenBy(s => -(s.LastAt ?? 0.0)).ToList();
        var mine = Ordered(sessions.Where(s => !s.Own));
        var theirs = Ordered(sessions.Where(s => s.Own));

        var days = new Dictionary<string, Tokens>();
        var models = new Dictionary<string, Tokens>();
        var totalsAll = Zero;
        foreach (var s in mine)
        {
            totalsAll = totalsAll + s.TotalTokens;
            Merge(days, s.ByDay);
            Merge(models, s.Models);
        }
        var ownTotals = Zero;
        foreach (var s in theirs) ownTotals = ownTotals + s.TotalTokens;

        return new Report
        {
            Measured = files > 0,
            ScannedAt = at,
            Sessions = mine,
            OwnSessions = theirs,
            Totals = totalsAll,
            OwnTotals = ownTotals,
            Today = days.TryGetValue(DayKey(at), out var today) ? today : Zero,
            Daily = days.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new DayUsage(kv.Key, kv.Value)).ToList(),
            Models = models,
            Files = files,
            BytesRead = bytesRead,
            Roots = rootPaths.ToList(),
        };
    }

    public static double? Min(double? a, double? b) => a is null ? b : (b is null ? a : Math.Min(a.Value, b.Value));

    public static double? Max(double? a, double? b) => a is null ? b : (b is null ? a : Math.Max(a.Value, b.Value));

    /// <summary>`Path(a) == Path(b)` with Windows semantics (case-insensitive, separators normalised).</summary>
    public static bool SamePath(string a, string b)
    {
        try { return SessionWatch.SamePathText(a, b); }
        catch { return false; }
    }

    // ── the shape the dashboard reads ───────────────────────────────────────────

    // How many days of history the sparkline gets.
    public const int DailyDays = 30;
    // How many conversations the payload carries, per ordering. The totals still
    // count every one of them.
    public const int MaxSessions = 40;

    /// <summary>
    /// (the conversations to ship, how many of the biggest are among them).
    ///
    /// TWO ORDERINGS, UNIONED: the Usage tab ranks by SPEND and says "N smaller
    /// conversations not listed" — only true if the biggest are in the payload;
    /// the Sessions tab looks up LIVE conversations, which have often barely spent
    /// anything. The second value is the licence for the word "smaller".
    /// </summary>
    public static (List<SessionUsage> listed, int largestListed) Listed(List<SessionUsage> sessions, int limit)
    {
        limit = Math.Max(0, limit);
        var biggest = sessions.OrderBy(s => -s.TotalTokens.Total).Take(limit).ToList();
        var keep = sessions.Take(limit).Select(s => s.SessionId).ToHashSet();
        keep.UnionWith(biggest.Select(s => s.SessionId));
        // Emitted in the caller's order, which is by recency.
        return (sessions.Where(s => keep.Contains(s.SessionId)).ToList(), biggest.Count);
    }

    /// <summary>
    /// The JSON `/api/usage/sessions` returns. `measured: false` means "nothing has
    /// been read" — the zeros beside it are the arithmetic of an empty set, not a
    /// measurement, and the UI must never render them as one.
    /// </summary>
    public static Dictionary<string, object?> Snapshot(IEnumerable<string>? roots = null, double? now = null,
        Cache? cache = null, IEnumerable<string>? ownSessionIds = null, int limit = MaxSessions)
    {
        var r = BuildReport(roots, now, cache, ownSessionIds);
        var (listed, largestListed) = Listed(r.Sessions, limit);
        var (ownListed, _) = Listed(r.OwnSessions, limit);
        return new Dictionary<string, object?>
        {
            ["measured"] = r.Measured,
            ["scanned_at"] = r.ScannedAt,
            ["active_within_sec"] = ActiveWithinSec,
            ["roots"] = r.Roots,
            ["files"] = r.Files,
            ["bytes_read"] = r.BytesRead,
            ["totals"] = r.Totals.AsDict(),
            ["own_totals"] = r.OwnTotals.AsDict(),
            ["today"] = r.Today.AsDict(),
            ["session_count"] = r.Sessions.Count,
            ["own_session_count"] = r.OwnSessions.Count,
            ["project_count"] = r.Sessions.Where(s => s.Project.Length > 0).Select(s => s.Project).Distinct().Count(),
            ["active_agents"] = r.Sessions.Sum(s => s.ActiveAgents),
            ["daily"] = r.Daily.Skip(Math.Max(0, r.Daily.Count - DailyDays))
                .Select(d => new Dictionary<string, object?> { ["day"] = d.Day, ["tokens"] = d.Tokens.AsDict() })
                .ToList(),
            ["models"] = ModelsList(r.Models),
            // How many of the machine's biggest spenders are in `sessions`.
            ["largest_listed"] = largestListed,
            ["sessions"] = listed.Select(s => s.AsDict()).ToList(),
            ["own_sessions"] = ownListed.Select(s => s.AsDict()).ToList(),
        };
    }

    // ── os.stat on Windows: (st_dev, st_ino, st_size, st_mtime) ────────────────

    /// <summary>
    /// The volume serial number and 64-bit file index (what Python's os.stat
    /// reports as st_dev / st_ino on Windows), size and mtime; null if the file
    /// cannot be opened for attributes. Hardlinks share dev+ino.
    /// </summary>
    public static (long dev, long ino, long size, double mtime)? Stat(string path)
    {
        if (!OperatingSystem.IsWindows())
            return Posix.StatPath(path) is { } st ? ((long)st.Dev, (long)st.Ino, st.Size, st.Mtime) : null;
        try
        {
            using var h = CreateFileW(path, FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (!GetFileInformationByHandle(h, out var info)) return null;
            long ino = (long)(((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            long size = (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow);
            long ft = (long)(((ulong)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
            double mtime = Py.ToEpoch(DateTimeOffset.FromFileTime(ft));
            return (info.VolumeSerialNumber, ino, size, mtime);
        }
        catch { return null; }
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);
}
