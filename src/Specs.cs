using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// The review surface: what JARVIS proposes, and what JARVIS produced.
///
/// `Builds` is the producer; this is the *reader*. The user asked for "a
/// clean/simple UI for specs and plans for people to actually see it and
/// communicate feedback to JARVIS by voice" — an approval gate needs an eye,
/// so the document goes on a page and the answer comes back by voice.
///
/// Three things are load-bearing:
/// 1. **Numbering is the mechanism.** "change three", "drop five" only work if
///    the number resolves to the same section on both sides, so exactly ONE
///    function numbers a document — `SectionsOf` — and both the `/api/specs`
///    payload and the `review_document` tool call it. Numbering is a pure
///    function of the file's text.
/// 2. **Approval is a recorded act**: a small JSON file in the project holding a
///    digest of the exact text approved. Text that no longer matches is
///    **superseded** and needs the user's eye again.
/// 3. **This reads files off a path that arrived in a URL.** Containment is
///    `RepoRead.ResolveWithin`, then narrowed to the two document directories.
///
/// No server imports, no subprocess: Server calls the blocking parts via Task.Run.
/// </summary>
public static partial class Specs
{
    // Approvals live beside the artifacts they approve: a human opening the
    // project finds them, and they outlive the session and the restart.
    public const string ApprovalDir = "docs/superpowers/approvals";

    // The only two directories a document may be read from.
    public static readonly string[] DocumentDirs = [Builds.SpecDir, Builds.PlanDir];

    // A spec is written by JARVIS from a conversation; a plan by the session
    // from the spec. They are read differently, so they are told apart.
    public static readonly Dictionary<string, string> KindOfDir = new(StringComparer.OrdinalIgnoreCase)
    {
        [Builds.SpecDir] = "spec",
        [Builds.PlanDir] = "plan",
    };

    // A megabyte of a spec or a plan is a file that went wrong.
    public const long MaxDocumentBytes = 400_000;

    // ---------------------------------------------------------------------------
    // Numbering
    // ---------------------------------------------------------------------------

    // See Builds.OnOneLine: "any character that is not one of splitlines' ten".
    public const string OnOneLine = Builds.OnOneLine;

    // Python fullmatch → \A ... \z.
    public static readonly Regex Heading = new(
        @"\A(#{1,6})[ \t]+(" + OnOneLine + @"*?)[ \t]*#*[ \t]*\z", RegexOptions.CultureInvariant);

    // re.match → anchored at the start.
    public static readonly Regex Fence = new(@"\A\s*(```|~~~)", RegexOptions.CultureInvariant);

    /// <summary>
    /// One numbered, top-level section of a document. `Number` is what the user
    /// says out loud: 1-based, contiguous, derived from nothing but the text.
    /// </summary>
    public sealed record Section(int Number, string Title, int Level, string Body)
    {
        /// <summary>The section as it appears in the file, heading included.</summary>
        public string Text
        {
            get
            {
                var head = $"{new string('#', Level)} {Title}";
                return Body.Length > 0 ? $"{head}\n{Body}" : head;
            }
        }
    }

    /// <summary>A parsed document: what comes before section 1, and the sections.</summary>
    public class Document
    {
        public string Preamble { get; set; } = "";
        public List<Section> Sections { get; set; } = [];
        public string Title { get; set; } = "";
    }

    /// <summary>
    /// (line index, level, title) for every heading OUTSIDE a code fence. A spec
    /// quoting a Markdown example would otherwise shift every number after it —
    /// a user saying "drop five" and losing section six.
    /// </summary>
    public static List<(int Index, int Level, string Title)> Headings(string text)
    {
        var found = new List<(int, int, string)>();
        string? fence = null;
        var lines = Builds.SplitLines(text);
        for (int index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var marker = Fence.Match(line);
            if (marker.Success)
            {
                var token = marker.Groups[1].Value;
                if (fence is null)
                    fence = token;
                else if (line.Trim().StartsWith(fence, StringComparison.Ordinal))
                    fence = null;
                continue;
            }
            if (fence is not null) continue;
            var heading = Heading.Match(line);
            if (heading.Success && heading.Groups[2].Value.Length > 0)
                found.Add((index, heading.Groups[1].Value.Length, heading.Groups[2].Value));
        }
        return found;
    }

    /// <summary>
    /// Which heading level counts as a top-level section: normally the
    /// shallowest. But a LONE shallowest heading sitting first is the document's
    /// title (`# &lt;topic&gt; — Design` from `Builds.RenderSpec`), and the
    /// sections are one level in — otherwise every spoken number is one too high.
    /// </summary>
    public static int SectionLevel(List<(int Index, int Level, string Title)> headings)
    {
        var levels = headings.Select(h => h.Level).ToList();
        var top = levels.Min();
        if (levels.Count > 1 && levels[0] == top && levels.Count(l => l == top) == 1)
        {
            var deeper = levels.Where(l => l > top).ToList();
            if (deeper.Count > 0) return deeper.Min();
        }
        return top;
    }

    /// <summary>Split a document into its preamble and its numbered sections.</summary>
    public static Document ParseDocument(string? text)
    {
        var lines = Builds.SplitLines(text ?? "");
        var headings = Headings(text ?? "");
        if (headings.Count == 0)
            return new Document { Preamble = string.Join("\n", lines), Sections = [], Title = "" };

        var level = SectionLevel(headings);
        var starts = headings.Where(h => h.Level == level).ToList();

        var title = "";
        if (headings[0].Level < level)
            title = headings[0].Title;

        var first = starts.Count > 0 ? starts[0].Index : lines.Count;
        var preambleLines = lines.Take(first).ToList();
        if (title.Length > 0)
        {
            // Drop the title's own line: it is the document's name, not body.
            preambleLines = lines.Take(first).Where((_, i) => i != headings[0].Index).ToList();
        }

        var sections = new List<Section>();
        for (int k = 0; k < starts.Count; k++)
        {
            var number = k + 1;
            var (index, _, headingTitle) = starts[k];
            var end = number < starts.Count ? starts[number].Index : lines.Count;
            var body = string.Join("\n", lines.Skip(index + 1).Take(Math.Max(0, end - index - 1))).Trim('\n');
            sections.Add(new Section(number, headingTitle, level, body));
        }

        return new Document
        {
            Preamble = string.Join("\n", preambleLines).Trim('\n'),
            Sections = sections,
            Title = title,
        };
    }

    /// <summary>The numbered sections of a document. THE numbering — both the page and JARVIS come through here.</summary>
    public static List<Section> SectionsOf(string? text) => ParseDocument(text).Sections;

    /// <summary>The section the user just said a number for, or null.</summary>
    public static Section? SectionNumber(string? text, int number) =>
        SectionsOf(text).FirstOrDefault(s => s.Number == number);

    // ---------------------------------------------------------------------------
    // Paths — a string out of a URL is not a path until it has been proved one
    // ---------------------------------------------------------------------------

    private static string Posix(string p) => p.Replace('\\', '/');

    private static string ParentPosix(string relative)
    {
        var p = Posix(relative).TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? "." : p[..i];
    }

    /// <summary>Which of the two document directories this path sits directly in.</summary>
    public static string? DocumentDir(string relative)
    {
        var parent = ParentPosix(relative);
        return DocumentDirs.FirstOrDefault(d => d.Equals(parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The real file `relative` names inside the project, or null. Containment is
    /// `RepoRead.ResolveWithin` (both sides resolved); then the file must sit
    /// directly in specs/ or plans/ and be Markdown. Null for every miss, never a
    /// reason — telling a prober which attempts were traversal buys us nothing.
    /// </summary>
    public static string? ResolveDocument(string projectPath, string? relative)
    {
        string real;
        try
        {
            real = RepoRead.ResolveWithin(projectPath, relative);
        }
        catch
        {
            return null;
        }
        string inside;
        try
        {
            var realRoot = RepoRead.RealPath(projectPath);
            if (!RepoRead.IsRelativeTo(real, realRoot)) return null;
            inside = Path.GetRelativePath(realRoot, real);
        }
        catch
        {
            return null;
        }
        if (DocumentDir(inside) is null) return null;
        if (!Builds.Suffix(real).Equals(".md", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(real)) return null;
        return real;
    }

    public static string? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxDocumentBytes) return null;
            return Builds.ReadTextUtf8(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------
    // Approval — the act, recorded, in a file
    // ---------------------------------------------------------------------------

    /// <summary>What was approved, in 64 characters. Any edit changes it.</summary>
    public static string DigestOf(string? text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    /// <summary>
    /// The record's filename, derived from the document's path — flattened, so
    /// the approvals directory is one flat list a person can read.
    /// </summary>
    public static string ApprovalFilename(string relative)
    {
        var stem = Posix(relative);
        foreach (var prefix in DocumentDirs)
        {
            if (stem.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                stem = stem[(prefix.Length + 1)..];
                var prefixName = prefix[(prefix.LastIndexOf('/') + 1)..];
                return $"{prefixName}__{stem}.json";
            }
        }
        return $"{stem.Replace("/", "__")}.json";
    }

    public static string ApprovalRecordPath(string projectPath, string relative) =>
        Path.Combine(projectPath, ApprovalDir.Replace('/', Path.DirectorySeparatorChar), ApprovalFilename(relative));

    /// <summary>
    /// Write down that the user approved this exact text. Returns the record.
    /// Blocking. The digest is of the text on disk right now. Throws
    /// ArgumentException (Python's ValueError) for a missing or unreadable document.
    /// </summary>
    public static Dictionary<string, object?> RecordApproval(string projectPath, string relative, string by = "voice")
    {
        var path = ResolveDocument(projectPath, relative);
        if (path is null)
            throw new ArgumentException("no such document");
        var text = Read(path);
        if (text is null)
            throw new ArgumentException("unreadable document");

        var record = new Dictionary<string, object?>
        {
            ["document"] = Posix(relative),
            ["digest"] = DigestOf(text),
            ["approved_at"] = Py.Now(),
            ["approved_by"] = by,
            ["sections"] = SectionsOf(text).Count,
        };
        var target = ApprovalRecordPath(projectPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, Py.Dumps(record, indent: true).ReplaceLineEndings("\n") + "\n", Py.Utf8NoBom);
        return record;
    }

    public static JsonObject? StoredApproval(string projectPath, string relative)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(ApprovalRecordPath(projectPath, relative), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
        // A corrupt record is not a yes. Read as "nobody approved this".
        return Py.TryLoads(raw) as JsonObject;
    }

    private static double ApprovedAtOf(JsonObject record)
    {
        var n = Py.Num(record, "approved_at");
        if (n is double d) return d;
        var s = Py.Str(record, "approved_at");
        if (!string.IsNullOrWhiteSpace(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0.0;
    }

    private static string ApprovedByOf(JsonObject record)
    {
        if (!record.TryGetPropertyValue("approved_by", out var v) || v is null) return "";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<bool>(out var b)) return b ? "True" : "";
        }
        return v.ToJsonString();
    }

    /// <summary>
    /// The approval state of one document:
    /// * **awaiting** — no record; nobody has said yes to this text.
    /// * **approved** — a record whose digest is the text on disk.
    /// * **superseded** — a record for text since revised; it needs the eye again.
    /// </summary>
    public static Dictionary<string, object?> ApprovalOf(string projectPath, string relative, string? text = null)
    {
        if (text is null)
        {
            var path = ResolveDocument(projectPath, relative);
            text = path is not null ? Read(path) : null;
        }
        var record = StoredApproval(projectPath, relative);
        if (record is null || text is null)
            return new Dictionary<string, object?> { ["state"] = "awaiting", ["approved_at"] = null, ["approved_by"] = "" };
        var approved = Py.Str(record, "digest") == DigestOf(text);
        return new Dictionary<string, object?>
        {
            ["state"] = approved ? "approved" : "superseded",
            ["approved_at"] = ApprovedAtOf(record),
            ["approved_by"] = ApprovedByOf(record),
        };
    }

    // ---------------------------------------------------------------------------
    // Documents and the two states
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The section number the plan's `## Task N` heading was numbered as. A plan
    /// opening with `## Goal` shifts every task down one; the user says the
    /// number they can SEE, so match the heading rather than assume.
    /// </summary>
    public static int SectionOfTask(string text, Builds.PlanTask task)
    {
        foreach (var section in SectionsOf(text))
        {
            if (Builds.TaskNumberOf(section.Title) == task.Number)
                return section.Number;
        }
        return 0;
    }

    /// <summary>
    /// Task progress off a plan's checkboxes, through `Builds.ParsePlan` — not
    /// re-implemented: two parsers would eventually disagree about how far a
    /// build had got.
    /// </summary>
    public static Dictionary<string, object?>? ProgressOf(string text)
    {
        var tasks = Builds.ParsePlan(text);
        if (tasks.Count == 0) return null;
        var current = tasks.FirstOrDefault(t => !t.Done);
        return new Dictionary<string, object?>
        {
            ["done"] = tasks.Count(t => t.Done),
            ["total"] = tasks.Count,
            ["current"] = current is not null ? current.Title : "",
            ["current_section"] = current is not null ? SectionOfTask(text, current) : 0,
            ["steps_done"] = tasks.Sum(t => t.StepsDone),
            ["steps_total"] = tasks.Sum(t => t.StepsTotal),
        };
    }

    private static double Mtime(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return 0.0;
            // Full precision, like st_mtime (Py.ToEpoch truncates to milliseconds).
            return (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
        }
        catch
        {
            return 0.0;
        }
    }

    public static Dictionary<string, object?>? DocumentMeta(string projectPath, string path, string kind)
    {
        var text = Read(path);
        if (text is null) return null;
        var name = Path.GetFileName(path);
        var relative = $"{(kind == "spec" ? Builds.SpecDir : Builds.PlanDir)}/{name}";
        var parsed = ParseDocument(text);
        var modified = Mtime(path);
        return new Dictionary<string, object?>
        {
            ["path"] = relative,
            ["kind"] = kind,
            ["title"] = parsed.Title.Length > 0 ? parsed.Title : Builds.Stem(path),
            ["modified"] = modified,
            ["sections"] = parsed.Sections.Count,
            ["approval"] = ApprovalOf(projectPath, relative, text),
            ["progress"] = kind == "plan" ? ProgressOf(text) : null,
        };
    }

    /// <summary>
    /// Every spec and plan in a project, newest first. Metadata only: bodies are
    /// fetched one at a time by `ReadDocument`.
    /// </summary>
    public static List<Dictionary<string, object?>> ListDocuments(string projectPath)
    {
        var output = new List<Dictionary<string, object?>>();
        foreach (var (directory, kind) in KindOfDir)
        {
            List<FileInfo> found;
            try
            {
                found = Builds.GlobMd(Path.Combine(projectPath, directory.Replace('/', Path.DirectorySeparatorChar)))
                    .OrderBy(f => f.FullName, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            foreach (var file in found)
            {
                if (!File.Exists(file.FullName)) continue;
                var meta = DocumentMeta(projectPath, file.FullName, kind);
                if (meta is not null) output.Add(meta);
            }
        }
        // Stable, newest first — Python's sort(reverse=True) keeps ties in order.
        return output.OrderByDescending(d => (double)d["modified"]!).ToList();
    }

    /// <summary>
    /// One document, numbered, with its approval state and its progress — what
    /// the page renders and what `review_document` answers from.
    /// </summary>
    public static Dictionary<string, object?>? ReadDocument(string projectPath, string? relative)
    {
        var path = ResolveDocument(projectPath, relative);
        if (path is null) return null;
        var text = Read(path);
        if (text is null) return null;
        var rel = Posix(relative!);
        var kind = KindOfDir.TryGetValue(ParentPosix(rel), out var k) ? k : "spec";
        var parsed = ParseDocument(text);
        var modified = Mtime(path);
        return new Dictionary<string, object?>
        {
            ["path"] = rel,
            ["kind"] = kind,
            ["title"] = parsed.Title.Length > 0 ? parsed.Title : Builds.Stem(path),
            ["modified"] = modified,
            ["preamble"] = parsed.Preamble,
            ["sections"] = parsed.Sections.Select(s => new Dictionary<string, object?>
            {
                ["number"] = s.Number,
                ["title"] = s.Title,
                ["level"] = s.Level,
                ["body"] = s.Body,
            }).ToList(),
            ["approval"] = ApprovalOf(projectPath, rel, text),
            ["progress"] = kind == "plan" ? ProgressOf(text) : null,
        };
    }

    /// <summary>
    /// (number, title) for every section — the list JARVIS reads from. Built out
    /// of `ReadDocument`, not a second parse: one parse, both sides.
    /// </summary>
    public static List<(int Number, string Title)> Outline(string projectPath, string relative)
    {
        var doc = ReadDocument(projectPath, relative);
        if (doc is null) return [];
        return ((List<Dictionary<string, object?>>)doc["sections"]!)
            .Select(s => ((int)s["number"]!, (string)s["title"]!)).ToList();
    }

    /// <summary>
    /// What this project needs from the user, or null if it has no documents:
    /// * **awaiting** — never approved, or revised since. Outranks everything.
    /// * **planning** — approved, no plan written yet.
    /// * **building** — a plan with work left on it.
    /// * **review** — every task ticked: finished work, waiting to be looked at.
    /// </summary>
    public static Dictionary<string, object?>? ProjectReview(string projectPath)
    {
        var documents = ListDocuments(projectPath);
        if (documents.Count == 0) return null;

        var plans = documents.Where(d => (string?)d["kind"] == "plan" && d["progress"] is not null).ToList();
        // max() keeps the FIRST of equal maxima, as Python's does.
        Dictionary<string, object?>? plan = null;
        foreach (var d in plans)
            if (plan is null || (double)d["modified"]! > (double)plan["modified"]!) plan = d;
        var progress = plan?["progress"] as Dictionary<string, object?>;

        var pending = documents
            .Where(d => (string?)((Dictionary<string, object?>)d["approval"]!)["state"] != "approved").ToList();
        string state;
        if (pending.Count > 0)
            state = "awaiting";
        else if (progress is null)
            state = "planning";
        else if ((int)progress["done"]! >= (int)progress["total"]!)
            state = "review";
        else
            state = "building";

        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["documents"] = documents,
            ["progress"] = progress,
            ["plan_path"] = plan is not null ? plan["path"] : "",
            ["awaiting"] = pending.Select(d => d["path"]).ToList(),
            ["modified"] = documents.Max(d => (double)d["modified"]!),
        };
    }
}
