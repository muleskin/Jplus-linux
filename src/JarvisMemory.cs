using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// JARVIS's long-term memory: a folder of plain Markdown the user can read.
///
/// One fact per file so a single memory can be corrected or deleted by hand
/// without disturbing the rest. `MEMORY.md` is the curated index the brain
/// always sees; everything else is found by `recall`.
///
/// Nothing here is ever destructive: files are created or replaced wholesale by
/// their author, the index is appended to, and no function deletes anything.
/// The user edits this folder directly, and losing their words would be worse
/// than keeping a stale line.
/// </summary>
public static partial class JarvisMemory
{
    public const int MemoryIndexMax = 80;   // lines in MEMORY.md before we ask for a tidy-up
    public const int SlugMax = 60;

    private const RegexOptions Ci = RegexOptions.CultureInvariant;

    public static readonly Regex Apostrophes = new("['’‘\"“”]", Ci);
    public static readonly Regex NonAlnum = new("[^a-z0-9]+", Ci);

    // ---------------------------------------------------------------------
    // Values that land on a STRUCTURED line
    // ---------------------------------------------------------------------
    //
    // `MEMORY.md` is `@`-imported by `jarvis_home/CLAUDE.md`, so every line of
    // it is system text in every future generation. `Server.MemoryWriters` is
    // one half of the gate — WHO may write. This is the other half: the SHAPE
    // of what gets written.
    //
    // One memory is one LINE. A hook with an interior newline once put a
    // heading of its own into the index — and the rewrite loop then preserved
    // it for ever, as "prose the user added". So every structured value is
    // flattened with Python's `str.split()` semantics (every line separator
    // and then some), not a hand-written list of characters.
    public const int FieldMaxChars = 240;

    // The index's own link syntax. A `]` or a `(` in a TITLE closes the link
    // and opens another, so the parser reads back a different title and slug
    // than the ones written. Removed rather than escaped: no name needs brackets.
    public static readonly Regex IndexStructural = new(@"[\[\]()]", Ci);

    /// <summary>`value` reduced to something that can only ever occupy one line.</summary>
    public static string OneLine(string? value, int limit = FieldMaxChars)
    {
        var flat = string.Join(" ", PySplit(value ?? ""));
        return CpLen(flat) > limit ? CpPrefix(flat, limit - 1) + "…" : flat;
    }

    /// <summary>
    /// `MEMORY.md` is at `MemoryIndexMax` and this entry is a NEW one. Raised
    /// rather than silently dropped: the caller has to tell the user nothing
    /// was written.
    /// </summary>
    public class IndexFull : Exception
    {
        public IndexFull(string message) : base(message) { }
    }

    /// <summary>
    /// The line this value would produce does not read back as itself. A line
    /// is only written if the module's OWN reader parses it back to exactly
    /// the values handed in — no list of dangerous characters needed.
    /// </summary>
    public class UnwritableValue : Exception
    {
        public UnwritableValue(string message) : base(message) { }
    }

    /// <summary>
    /// Full-length normalised form of a title, used to tell whether two titles
    /// name the SAME memory. Never truncated (unlike Slugify), so two long
    /// titles sharing their first SlugMax characters stay distinct; trivial
    /// punctuation/case/apostrophe differences still collapse together.
    /// </summary>
    public static string NormalizeTitle(string? text)
    {
        var stripped = Apostrophes.Replace((text ?? "").ToLowerInvariant(), "");
        return NonAlnum.Replace(stripped, "-").Trim('-');
    }

    /// <summary>A filename a person can recognise in a directory listing.</summary>
    public static string Slugify(string? text)
    {
        // Strip apostrophes/quotes rather than treating them as separators, so
        // "Tony's" becomes "tonys" and not "tony-s".
        var norm = NormalizeTitle(text);
        var cut = (norm.Length > SlugMax ? norm[..SlugMax] : norm).TrimEnd('-');
        return cut.Length > 0 ? cut : "note";
    }

    /// <summary>The `# Title` header of an existing memory file, or null.</summary>
    public static string? TitleOf(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (IsOsError(e)) { return null; }
        foreach (var line in SplitLines(text))
            if (line.StartsWith("# ", StringComparison.Ordinal)) return PyStrip(line[2..]);
        return null;
    }

    /// <summary>
    /// One fact, one file. Re-writing the same title updates it in place.
    ///
    /// Two different titles can slugify to the same filename. An existing file
    /// at the target name is only reused if its own `# Title` header names the
    /// SAME memory; otherwise a fresh, non-colliding filename is chosen so
    /// neither memory is lost. The TITLE is flattened to one line (it is read
    /// back as the first `# ` line); the BODY is left alone.
    /// </summary>
    public static string WriteMemory(string title, string body)
    {
        DataPaths.EnsureMemoryLayout();
        var directory = DataPaths.MemoryDir();
        title = OneLine(title);
        var slug = Slugify(title);
        var norm = NormalizeTitle(title);

        var path = Path.Combine(directory, $"{slug}.md");
        int n = 2;
        while (File.Exists(path) || Directory.Exists(path))
        {
            var existingTitle = TitleOf(path);
            if (existingTitle is not null && NormalizeTitle(existingTitle) == norm)
                break;   // same memory (mod trivial punctuation/case) — update in place
            path = Path.Combine(directory, $"{slug}-{n}.md");
            n++;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        WriteText(path, $"# {PyStrip(title)}\n\n_{stamp}_\n\n{PyStrip(body ?? "")}\n");
        return path;
    }

    public static string? ReadMemory(string name)
    {
        var path = Path.Combine(DataPaths.MemoryDir(), $"{Slugify(name)}.md");
        try { return File.ReadAllText(path); }
        catch (Exception e) when (IsOsError(e)) { return null; }
    }

    public static List<string> ListMemories()
    {
        try
        {
            return GlobMd(DataPaths.MemoryDir()).Select(p => Path.GetFileNameWithoutExtension(p))
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (IsOsError(e)) { return []; }
    }

    public const string IndexName = "MEMORY.md";
    public const string IndexHeader =
        "# What JARVIS remembers\n\n" +
        "One line per memory. This file is loaded into every conversation, so it\n" +
        "stays short; the detail lives in `memory/`.\n\n";

    public static string IndexPath() => Path.Combine(DataPaths.BrainHome(), IndexName);

    /// <summary>
    /// Create the whole memory folder, including an empty index. `CLAUDE.md`
    /// imports `@MEMORY.md`; on a fresh install nothing had created the file,
    /// so the import dangled. Seed it here. Never overwrites an existing index.
    /// </summary>
    public static string EnsureLayout()
    {
        var home = DataPaths.EnsureMemoryLayout();
        var index = IndexPath();
        if (!File.Exists(index) && !Directory.Exists(index)) WriteText(index, IndexHeader);
        return home;
    }

    public static List<string> IndexLines()
    {
        string text;
        try { text = File.ReadAllText(IndexPath()); }
        catch (Exception e) when (IsOsError(e)) { return []; }
        return SplitLines(text).Where(ln => ln.StartsWith("- [", StringComparison.Ordinal)).ToList();
    }

    // The index's format, in one place: the writer round-trips through it
    // before it commits a line, and IndexEntries parses rows back out of it.
    // Anchored with \A...\z (a whole-value match), never `$`, which matches
    // before a trailing newline. `.` and `\s` would accept nine of the ten
    // characters Python's splitlines() breaks on, so the line class below is
    // "any character that is not one of the ten".
    public const string OnOneLine = @"[^\r\n\v\f\x1c\x1d\x1e\x85\u2028\u2029]";
    public const string Separators = @"\r\n\v\f\x1c\x1d\x1e\x85\u2028\u2029";

    public static readonly Regex IndexLineRe = new(
        @"\A- \[(?<title>[^\]" + Separators + @"]*)\]" +
        @"\((?<slug>[^)/\\" + Separators + @"]*?)\.md\)" +
        @"(?:[ \t]*[—–-][ \t]*(?<hook>" + OnOneLine + @"*))?\z", Ci);

    /// <summary>
    /// The one line this memory occupies, proven to read back as itself.
    /// Throws UnwritableValue rather than writing something the index's own
    /// parser would disagree with.
    /// </summary>
    public static string IndexLine(string title, string slug, string hook)
    {
        var line = $"- [{title}]({slug}.md) — {hook}";
        var m = IndexLineRe.Match(line);
        var lines = SplitLines(line);
        if (lines.Count != 1 || lines[0] != line || !m.Success
            || m.Groups["title"].Value != title || m.Groups["slug"].Value != slug
            || (m.Groups["hook"].Success ? m.Groups["hook"].Value : "") != hook)
            throw new UnwritableValue("that memory cannot be written as a single index line");
        return line;
    }

    /// <summary>
    /// One line per memory. A repeated title updates its hook in place.
    ///
    /// Rewrites only the generated lines and keeps whatever prose the user has
    /// added around them. Both values are flattened to one line and the
    /// finished line is round-tripped before anything is written.
    ///
    /// Throws IndexFull for a NEW entry once the index is at MemoryIndexMax:
    /// this file is loaded whole into every generation, so "the brain will
    /// tidy it up" is not a bound. Updating an existing line is always allowed.
    /// </summary>
    public static void AddToIndex(string title, string hook)
    {
        DataPaths.EnsureMemoryLayout();
        var path = IndexPath();
        title = IndexStructural.Replace(OneLine(title), "");
        hook = OneLine(hook);
        var slug = Slugify(title);
        var line = IndexLine(title, slug, hook);

        string existing;
        try { existing = File.ReadAllText(path); }
        catch (Exception e) when (IsOsError(e)) { existing = IndexHeader; }

        var kept = new List<string>();
        bool replaced = false;
        foreach (var ln in SplitLines(existing))
        {
            if (ln.StartsWith("- [", StringComparison.Ordinal) && ln.Contains($"]({slug}.md)", StringComparison.Ordinal))
            {
                kept.Add(line);
                replaced = true;
            }
            else kept.Add(ln);
        }
        if (!replaced)
        {
            if (IndexIsFull())
                throw new IndexFull($"MEMORY.md already holds {MemoryIndexMax} memories");
            kept.Add(line);
        }
        WriteText(path, string.Join("\n", kept).TrimEnd('\n') + "\n");
    }

    /// <summary>True when the index has outgrown what belongs in every conversation.</summary>
    public static bool IndexIsFull() => IndexLines().Count >= MemoryIndexMax;

    /// <summary>
    /// What JARVIS knows about one project, appended in order (last week's
    /// finding is still true; a replace would quietly discard it). One note is
    /// one stamped LINE, so both values are flattened.
    /// </summary>
    public static string WriteProjectNote(string project, string text)
    {
        DataPaths.EnsureMemoryLayout();
        project = OneLine(project);
        text = OneLine(text);
        var path = Path.Combine(DataPaths.ProjectsDir(), $"{Slugify(project)}.md");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (!File.Exists(path)) WriteText(path, $"# {project}\n\n");
        File.AppendAllText(path, $"_{stamp}_ — {text}\n", Py.Utf8NoBom);
        return path;
    }

    public static string? ReadProjectNote(string project)
    {
        var path = Path.Combine(DataPaths.ProjectsDir(), $"{Slugify(project)}.md");
        try { return File.ReadAllText(path); }
        catch (Exception e) when (IsOsError(e)) { return null; }
    }

    public const string JournalStampFmt = "yyyy-MM-dd-HHmmss-ffffff";   // fixed-width: lexicographic == chronological
    public static readonly Regex JournalNameRe = new(@"\A(\d{4}-\d{2}-\d{2}-\d{6}-\d{6})-(.+)\.md\z", Ci);

    // Reasons that mark an entry as a PLACEHOLDER: a tombstone proving a
    // generation ended rather than vanished. Still written to disk, but never
    // carried into the next generation as "where you left off", or one silent
    // shutdown would erase a real handover written minutes earlier. The marker
    // lives in the REASON (slugified into the filename), so it can be decided
    // from a directory listing and survives hand-edits of the body.
    public static readonly HashSet<string> PlaceholderReasons = new(StringComparer.Ordinal) { "rotation-silent", "shutdown-silent" };

    // WriteJournal appends "-2", "-3"… to break a filename collision, so the
    // reason parsed back out of a name may carry that suffix.
    public static readonly Regex CollisionSuffix = new(@"-\d+$", Ci);

    /// <summary>
    /// How one brain generation hands over to the next.
    ///
    /// `untrustedSource` is what the writing generation had read that JARVIS
    /// did not write ("a web page", "another session's transcript"), or null.
    /// It is recorded IN THE FILE because that is the only place it survives a
    /// restart. The filename carries a fixed-width microsecond timestamp so
    /// write order is recorded directly. `reason` and `untrustedSource` land on
    /// the single `# ` header line, so both are flattened to one line.
    /// </summary>
    public static string WriteJournal(string text, string reason = "shutdown", string? untrustedSource = null)
    {
        DataPaths.EnsureMemoryLayout();
        reason = OneLine(reason);
        if (reason.Length == 0) reason = "shutdown";
        untrustedSource = OneLine(untrustedSource);
        if (untrustedSource.Length == 0) untrustedSource = null;
        var stamp = DateTime.Now.ToString(JournalStampFmt, CultureInfo.InvariantCulture);
        var path = Path.Combine(DataPaths.JournalDir(), $"{stamp}-{Slugify(reason)}.md");
        int n = 2;
        while (File.Exists(path) || Directory.Exists(path))
        {
            path = Path.Combine(DataPaths.JournalDir(), $"{stamp}-{Slugify(reason)}-{n}.md");
            n++;
        }
        var headerStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var provenance = untrustedSource is not null
            ? $"\n\nThe generation that wrote this had read {untrustedSource} that day."
            : "";
        WriteText(path, $"# {headerStamp} ({reason}){provenance}\n\n{PyStrip(text ?? "")}\n");
        return path;
    }

    /// <summary>The timestamp component of a journal filename, or null (renamed by hand).</summary>
    public static string? JournalStamp(string path)
    {
        var m = JournalNameRe.Match(Path.GetFileName(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The reason component of a journal filename, or "" if it doesn't parse.</summary>
    public static string JournalReason(string path)
    {
        var m = JournalNameRe.Match(Path.GetFileName(path));
        return m.Success ? m.Groups[2].Value : "";
    }

    /// <summary>True for a tombstone entry — written to prove a generation ended.</summary>
    public static bool IsPlaceholderReason(string reason)
    {
        if (PlaceholderReasons.Contains(reason)) return true;
        return PlaceholderReasons.Contains(CollisionSuffix.Replace(reason, "", 1));
    }

    /// <summary>
    /// Every parseable entry as (stamp, reason, path), oldest first — ordered
    /// on the timestamp IN THE FILENAME, not mtime (this folder is user-editable,
    /// and fixing a typo must not make an old entry the newest).
    /// </summary>
    public static List<(string stamp, string reason, string path)> JournalEntries()
    {
        List<string> candidates;
        try { candidates = GlobMd(DataPaths.JournalDir()); }
        catch (Exception e) when (IsOsError(e)) { return []; }
        var entries = new List<(string stamp, string reason, string path)>();
        foreach (var p in candidates)
            if (JournalStamp(p) is { } stamp) entries.Add((stamp, JournalReason(p), p));
        return entries.OrderBy(e => e.stamp, StringComparer.Ordinal).ToList();   // stable, like list.sort
    }

    /// <summary>
    /// The most recent real handover, bounded — prepended to every new brain.
    /// Placeholder entries are skipped by default; they stay on disk but are
    /// never the thing that gets carried.
    /// </summary>
    public static string? LatestJournal(int limit = 1200, bool includePlaceholders = false)
    {
        var entries = JournalEntries();
        if (!includePlaceholders) entries = entries.Where(e => !IsPlaceholderReason(e.reason)).ToList();
        if (entries.Count == 0) return null;
        string text;
        try { text = File.ReadAllText(entries[^1].path); }
        catch (Exception e) when (IsOsError(e)) { return null; }
        return CpLen(text) <= limit ? text : CpPrefix(text, limit - 1) + "…";
    }

    public const int ExcerptChars = 240;

    // Words too common to mean anything on their own. A query built entirely
    // of these must not fall through to matching every file by substring.
    public static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "did", "do",
        "does", "for", "from", "had", "has", "have", "he", "her", "his", "i",
        "in", "is", "it", "its", "of", "on", "or", "our", "she", "so", "that",
        "the", "their", "them", "there", "they", "this", "to", "was", "we",
        "were", "what", "when", "which", "who", "with", "you", "your",
    };

    /// <summary>Title matches count for more: a file named for the thing is almost always the one meant.</summary>
    public static int Score(HashSet<string> queryWords, string title, string body)
    {
        var titleL = title.ToLowerInvariant();
        var bodyL = body.ToLowerInvariant();
        int score = 0;
        foreach (var w in queryWords)
        {
            if (titleL.Contains(w, StringComparison.Ordinal)) score += 10;
            score += CountOccurrences(bodyL, w);
        }
        return score;
    }

    public static readonly Regex UrlRe = new(@"https?://\S+", Ci);
    public const int UrlLongMin = 30;   // shorter than this reads fine spoken as-is
    public static readonly Regex TableRow = new(@"\A\|.*\|\z", Ci);

    /// <summary>
    /// Polish a line for text-to-speech without touching ordinary prose: a
    /// Markdown table row has its pipes collapsed, and a URL long enough to be
    /// character-soup is replaced by its domain. A short URL or a stray "|" in
    /// normal prose is left alone.
    /// </summary>
    public static string Speakable(string text)
    {
        static string TameUrl(Match m)
        {
            var url = m.Value;
            if (CpLen(url) < UrlLongMin) return url;
            var domain = Regex.Replace(url, "^https?://", "", Ci).Split('/')[0];
            domain = Regex.Replace(domain, @"^www\.", "", Ci);
            return domain.Length > 0 ? domain : "a link";
        }

        text = UrlRe.Replace(text, TameUrl);
        if (TableRow.IsMatch(PyStrip(text)))
        {
            var cells = PyStrip(text).Trim('|').Split('|').Select(PyStrip);
            text = string.Join(", ", cells.Where(c => c.Length > 0));
        }
        return text;
    }

    public static string Cap(string text)
    {
        text = Speakable(string.Join(" ", PySplit(text)));
        return CpLen(text) <= ExcerptChars ? text : CpPrefix(text, ExcerptChars - 1) + "…";
    }

    public static readonly Regex LeadingStamp = new(@"^_[^_\n]+_\s*[—-]\s*", Ci);

    /// <summary>
    /// A line stripped to what could be spoken, or null if it is furniture:
    /// a leading heading marker and a project note's "_stamp_ — " are removed,
    /// and a line with no letters at all is dropped.
    /// </summary>
    public static string? CleanLine(string line)
    {
        var candidate = PyStrip(PyStrip(line).TrimStart('#'));
        candidate = LeadingStamp.Replace(candidate, "", 1);
        if (candidate.Length == 0 || !candidate.Any(char.IsLetter)) return null;
        return candidate;
    }

    /// <summary>
    /// The line that matched, so the brain sees why this was returned.
    /// 1. A substantive line that CONTAINS a query word.
    /// 2. Else a body line that only echoes query words (honest about why it matched).
    /// 3. Only when no body line matches (a title-only hit), the first
    ///    substantive body line. A heading that merely echoes the query does
    ///    not count as a body match.
    /// </summary>
    public static string Excerpt(string body, HashSet<string> queryWords)
    {
        string? echoOnly = null;
        foreach (var line in SplitLines(body))
        {
            bool isHeading = PyStrip(line).StartsWith('#');
            var candidate = CleanLine(line);
            if (candidate is null) continue;
            var low = candidate.ToLowerInvariant();
            if (!queryWords.Any(w => low.Contains(w, StringComparison.Ordinal))) continue;
            var lineWords = NonAlnum.Split(low).Where(x => x.Length > 0);
            if (lineWords.All(queryWords.Contains))
            {
                if (!isHeading) echoOnly ??= candidate;   // matched, says nothing new
                continue;
            }
            return Cap(candidate);
        }

        if (echoOnly is not null) return Cap(echoOnly);

        foreach (var line in SplitLines(body))
        {
            if (PyStrip(line).StartsWith('#')) continue;
            var candidate = CleanLine(line);
            if (!string.IsNullOrEmpty(candidate)) return Cap(candidate);
        }

        return Cap(body);
    }

    public static List<(string kind, string path)> Sources()
    {
        var output = new List<(string kind, string path)>();
        foreach (var (kind, directory) in new[]
                 {
                     ("memory", DataPaths.MemoryDir()),
                     ("project", DataPaths.ProjectsDir()),
                     ("journal", DataPaths.JournalDir()),
                 })
        {
            try
            {
                foreach (var path in GlobMd(directory)) output.Add((kind, path));
            }
            catch (Exception e) when (IsOsError(e)) { }
        }
        return output;
    }

    /// <summary>Scored scan of the folder. No index, so it can never be stale.</summary>
    public static List<Dictionary<string, object?>> Search(string? query, int limit = 5)
    {
        var words = NonAlnum.Split((query ?? "").ToLowerInvariant())
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
        if (words.Count == 0) return [];
        var hits = new List<Dictionary<string, object?>>();
        foreach (var (kind, path) in Sources())
        {
            string body;
            try { body = File.ReadAllText(path); }
            catch (Exception e) when (IsOsError(e)) { continue; }
            var stem = Path.GetFileNameWithoutExtension(path);
            int score = Score(words, stem, body);
            if (score != 0)
                hits.Add(new Dictionary<string, object?>
                {
                    ["kind"] = kind, ["name"] = stem, ["path"] = path,
                    ["excerpt"] = Excerpt(body, words), ["score"] = score,
                });
        }
        return hits.OrderBy(h => -(int)h["score"]!).ThenBy(h => (string)h["name"]!, StringComparer.Ordinal)
            .Take(limit).ToList();
    }

    // ---------------------------------------------------------------------
    // Listings — what the dashboard's Memory view reads.
    //
    // Read-only by construction: nothing here creates the folder. A brain that
    // has never remembered anything has no `jarvis/` directory at all, and a
    // GET must report that as empty rather than bring it into being.
    // ---------------------------------------------------------------------

    /// <summary>MEMORY.md, parsed. A line reshaped by hand is skipped rather than half-parsed.</summary>
    public static List<Dictionary<string, object?>> IndexEntries()
    {
        var output = new List<Dictionary<string, object?>>();
        foreach (var line in IndexLines())
        {
            var m = IndexLineRe.Match(PyStrip(line));
            if (!m.Success) continue;
            output.Add(new Dictionary<string, object?>
            {
                ["title"] = PyStrip(m.Groups["title"].Value),
                ["slug"] = m.Groups["slug"].Value,
                ["hook"] = PyStrip(m.Groups["hook"].Success ? m.Groups["hook"].Value : ""),
            });
        }
        return output;
    }

    /// <summary>
    /// slug / title / mtime for each `.md` in one folder, newest first. `title`
    /// is the file's own `# ` header where it has one, falling back to the slug.
    /// </summary>
    public static List<Dictionary<string, object?>> FileEntries(string directory)
    {
        List<string> paths;
        try { paths = GlobMd(directory); }
        catch (Exception e) when (IsOsError(e)) { return []; }
        var output = new List<Dictionary<string, object?>>();
        foreach (var path in paths)
        {
            double modified;
            try
            {
                if (!File.Exists(path)) continue;   // deleted between the glob and here
                modified = Py.ToEpoch(new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception e) when (IsOsError(e)) { continue; }
            var stem = Path.GetFileNameWithoutExtension(path);
            output.Add(new Dictionary<string, object?>
            {
                ["slug"] = stem,
                ["title"] = TitleOf(path) is { Length: > 0 } t ? t : stem,
                ["modified"] = modified,
            });
        }
        return output.OrderByDescending(e => (double)e["modified"]!).ToList();
    }

    public static List<Dictionary<string, object?>> MemoryEntries() => FileEntries(DataPaths.MemoryDir());

    public static List<Dictionary<string, object?>> ProjectEntries() => FileEntries(DataPaths.ProjectsDir());

    /// <summary>
    /// Epoch seconds for a journal filename's own timestamp — NOT mtime, which
    /// a hand-edit changes without changing when the entry was written.
    /// </summary>
    public static double StampToEpoch(string stamp)
    {
        if (DateTime.TryParseExact(stamp, JournalStampFmt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
            return (dt.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;   // keeps the microseconds
        return 0.0;
    }

    /// <summary>slug / when / reason for each journal entry, newest first.</summary>
    public static List<Dictionary<string, object?>> JournalEntriesMeta()
    {
        var entries = JournalEntries();
        entries.Reverse();
        return entries.Select(e => new Dictionary<string, object?>
        {
            ["slug"] = Path.GetFileNameWithoutExtension(e.path),
            ["when"] = StampToEpoch(e.stamp),
            ["reason"] = e.reason,
        }).ToList();
    }

    /// <summary>The entry a new brain will actually carry (placeholders skipped).</summary>
    public static string? LatestJournalSlug()
    {
        var entries = JournalEntries().Where(e => !IsPlaceholderReason(e.reason)).ToList();
        return entries.Count > 0 ? Path.GetFileNameWithoutExtension(entries[^1].path) : null;
    }

    public static readonly Dictionary<string, Func<string>> DocDirs = new(StringComparer.Ordinal)
    {
        ["memory"] = DataPaths.MemoryDir,
        ["project"] = DataPaths.ProjectsDir,
        ["journal"] = DataPaths.JournalDir,
    };

    /// <summary>
    /// The file for one (kind, slug), or null — including every case where
    /// `slug` tries to name a file outside its own folder.
    ///
    /// `slug` arrives from a URL, so it is hostile. The string checks reject
    /// the obvious; the containment check on fully resolved paths (links
    /// followed on both sides) is what actually decides.
    /// </summary>
    public static string? DocPath(string kind, string slug)
    {
        if (!DocDirs.TryGetValue(kind, out var getDir)) return null;
        if (string.IsNullOrEmpty(slug) || slug.Contains('/') || slug.Contains('\\') || slug.Contains('\0'))
            return null;
        // ':' is a drive / alternate-data-stream separator on Windows.
        if (slug.StartsWith('.') || slug.Contains(':') || Path.GetFileName(slug) != slug) return null;
        string root, candidate;
        try
        {
            root = RealPath(getDir());
            candidate = RealPath(Path.Combine(root, $"{slug}.md"));
        }
        catch (Exception e) when (IsOsError(e)) { return null; }
        if (!IsRelativeTo(candidate, root)) return null;
        return File.Exists(candidate) ? candidate : null;
    }

    // ── helpers: Python semantics the format depends on ─────────────────────

    /// <summary>Python's `str.isspace()` set: .NET whitespace plus \x1c-\x1f.</summary>
    public static bool IsPySpace(char c) => char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');

    /// <summary>`str.split()` with no argument.</summary>
    public static List<string> PySplit(string s)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsPySpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !IsPySpace(s[i])) i++;
            if (i > start) parts.Add(s[start..i]);
        }
        return parts;
    }

    /// <summary>`str.strip()`.</summary>
    public static string PyStrip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsPySpace(s[a])) a++;
        while (b > a && IsPySpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>`str.splitlines()` — the ten separators, "\r\n" as one, no trailing empty line.</summary>
    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int i = 0, start = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (IsLineBreak(c))
            {
                lines.Add(s[start..i]);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                i++;
                start = i;
            }
            else i++;
        }
        if (start < s.Length) lines.Add(s[start..]);
        return lines;
    }

    /// <summary>Length in code points, as Python's `len`.</summary>
    public static int CpLen(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>`s[:n]` in code points (never splits a surrogate pair).</summary>
    public static string CpPrefix(string s, int n)
    {
        if (n <= 0) return "";
        int i = 0, count = 0;
        while (i < s.Length && count < n)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i += 2;
            else i++;
            count++;
        }
        return s[..i];
    }

    /// <summary>`str.count(sub)` — non-overlapping occurrences.</summary>
    public static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return haystack.Length + 1;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>`sorted(directory.glob("*.md"))` — files only, ordinal order.</summary>
    public static List<string> GlobMd(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.md")
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>`Path.write_text` — UTF-8, no BOM, "\n" exactly as given.</summary>
    public static void WriteText(string path, string text) => File.WriteAllText(path, text, Py.Utf8NoBom);

    public static bool IsOsError(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException;

    /// <summary>`Path.resolve()`: absolute, with every symlink / junction along the way followed.</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            for (int hops = 0; hops < 40; hops++)
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists || info.LinkTarget is null) break;
                var target = info.LinkTarget;
                next = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            }
            current = next;
        }
        return current;
    }

    /// <summary>`Path.is_relative_to` on two resolved paths (case-insensitive on Windows).</summary>
    public static bool IsRelativeTo(string candidate, string root)
    {
        var cmp = Py.PathComparison;
        var r = root.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(candidate, r, cmp)
               || candidate.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }
}
