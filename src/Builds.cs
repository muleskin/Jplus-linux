using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// Real builds: the spec that survives, the brief that drives, the plan that reports.
///
/// A `spawn_run` is one sentence handed to one unattended turn — right for
/// "fix the typo in the footer", wrong for a project. Real work is detailed
/// planning, a written spec, revision of that spec, and then phased execution;
/// the review cycles are where the quality comes from.
///
/// Three things live here, and nothing else. No server imports, no `claude`
/// subprocess: Server calls all of it, the filesystem parts through Task.Run.
///
/// 1. The spec, written into the project (`docs/superpowers/specs/`). It is the
///    only artifact that survives a compaction, a session replacement or
///    JARVIS's own context rotation, so it goes on disk before anything spawns.
/// 2. The brief: the whole process in one prompt — read the settled spec, write
///    a phased plan, review and revise it, execute it under TDD, ticking boxes.
///    `superpowers:brainstorming`'s human-approval gate is named and shut.
/// 3. Progress, read off the plan file (`## Task N: title`, `- [ ]` / `- [x]`).
///
/// Plus one guard: `CommandProblem`, which bounds what `run_command` may put
/// into a terminal window — that text came out of a microphone and an LLM.
/// </summary>
public static partial class Builds
{
    // Where a build's artifacts live inside the project. The superpowers
    // convention, deliberately: those skills read and write these by name.
    public const string SpecDir = "docs/superpowers/specs";
    public const string PlanDir = "docs/superpowers/plans";

    // A spoken topic becomes a filename, so it is slugified hard and bounded.
    public const int SlugMax = 60;

    private const RegexOptions Ci = RegexOptions.CultureInvariant;

    public static string Slug(string? text)
    {
        var slug = Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-", Ci).Trim('-');
        if (slug.Length > SlugMax)
            slug = slug[..SlugMax].TrimEnd('-');
        return slug.Length > 0 ? slug : "build";
    }

    // Words a title picks up that say nothing about the topic: "A local web UI
    // ... — Design" is filed under the topic, not under the word "design".
    public static readonly Regex TitleTail = new(
        @"[\s\-—:]*(design|spec|specification|design doc|design document)\s*$",
        RegexOptions.IgnoreCase | Ci);

    /// <summary>
    /// The topic a spec is about, for its filename: the first markdown heading,
    /// otherwise the first line. Nothing else is consulted — guessing a topic
    /// out of the body would be guessing.
    /// </summary>
    public static string TopicOf(string? spec)
    {
        foreach (var raw in SplitLines(spec))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            line = line.TrimStart('#').Trim();
            if (line.Length == 0) continue;
            var t = TitleTail.Replace(line, "").Trim();
            return t.Length > 0 ? t : line;
        }
        return "build";
    }

    private static string IsoDay(DateOnly? today) =>
        (today ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The project-relative path this spec is written to:
    /// `docs/superpowers/specs/YYYY-MM-DD-&lt;topic&gt;-design.md`.
    /// </summary>
    public static string SpecPath(string? spec, DateOnly? today = null)
    {
        var day = IsoDay(today);
        return $"{SpecDir}/{day}-{Slug(TopicOf(spec))}-design.md";
    }

    /// <summary>
    /// The document written to disk. "Status: Approved" is the sentence the
    /// session is told to trust; the date tells two specs in one project apart.
    /// </summary>
    public static string RenderSpec(string? spec, string? constraints = "", string? nonGoals = "",
        DateOnly? today = null)
    {
        var day = IsoDay(today);
        var body = (spec ?? "").Trim();
        var lines = new List<string>
        {
            $"# {TopicOf(spec)} — Design",
            "",
            $"Date: {day}",
            "Status: Approved — settled with the user by voice, before the " +
            "build was started.",
            "",
            "> Written by JARVIS from the conversation in which this was " +
            "agreed. It is the build's source of truth: it outlives the " +
            "session that reads it, and a session that has compacted or been " +
            "replaced starts again from here.",
            "",
            "## What we agreed",
            "",
            body,
            "",
        };
        if ((constraints ?? "").Trim().Length > 0)
            lines.AddRange(["## Constraints", "", constraints!.Trim(), ""]);
        if ((nonGoals ?? "").Trim().Length > 0)
            lines.AddRange(["## Non-goals", "", nonGoals!.Trim(), ""]);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Write the spec into the project. Returns its project-relative path.
    /// Blocking. Creates both superpowers directories, because a fresh project
    /// from `create_project` has neither and the brief points at both.
    /// </summary>
    public static string WriteSpec(string projectPath, string? spec, string? constraints = "",
        string? nonGoals = "", DateOnly? today = null)
    {
        var relative = SpecPath(spec, today);
        var root = projectPath;
        var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.Combine(root, PlanDir.Replace('/', Path.DirectorySeparatorChar)));
        // LF and no BOM, as the original wrote it.
        File.WriteAllText(target, RenderSpec(spec, constraints, nonGoals, today), Py.Utf8NoBom);
        return relative;
    }

    // --- The brief -----------------------------------------------------------
    //
    // Everything a session needs to run a real project alone. Each paragraph is
    // load-bearing and was put there by something that went wrong without it:
    //   * the operating condition comes FIRST and says no answer can arrive;
    //   * the approval gate is named and shut, with the reason;
    //   * planning, self-review, execution, TDD and verification are numbered
    //     steps — "review and revise your own plan" comes before any code;
    //   * ticking the checkboxes is the only channel `build_status` reads;
    //   * "decide it and write the decision down" closes the last exit;
    //   * the product bar (zero-config first launch, no undismissable modal,
    //     one-command README, a real UI) is stated as a requirement to verify.

    public const string BuildBriefTemplate = """
[Long build — no human present] You are building a real project from a settled design, start to finish, in one long session. Nobody is watching this and nobody can answer a question: no reply can ever reach you, so ending your turn to ask something means the work simply never happens. There is no time limit. Take as long as the work honestly needs.

THE DESIGN IS SETTLED AND APPROVED. The user agreed it out loud and it is written down in this project at:

    {spec_path}

Read that file before anything else. Do NOT brainstorm, do NOT re-open the design, do NOT present a plan, a design or a set of options for approval, and do NOT invoke any skill that requires a human to approve something before you implement — superpowers:brainstorming in particular carries a hard approval gate, and that approval has already been given, in the file above. Treat the spec as the requirement it is: build what it says, and where it is silent, see step 6.

Then run the whole process yourself, in this order.

1. PLAN. Use superpowers:writing-plans to write a phased implementation plan into {plan_dir}/ before you touch any code. Give every task a `## Task N: <title>` heading and break it into `- [ ]` checkbox steps, each one small enough to finish and verify on its own.

2. REVIEW AND REVISE YOUR OWN PLAN, before you execute a line of it. Read the plan back against the spec and hunt for what is wrong with it: requirements in the spec that no task covers, tasks that contradict each other or the spec, steps left vague or holding a placeholder, ordering that would have you build on something that does not exist yet, and anything a reader could not execute without asking a question. Fix all of it in the plan file. Do this at least once and again whenever the plan stops matching reality — the revision is where the quality comes from, not the first draft.

3. EXECUTE with superpowers:subagent-driven-development, one task at a time: a fresh implementer subagent for each task, then a reviewer subagent over its work before you move on. Use superpowers:executing-plans instead only if the tasks are so entangled that handing one to a fresh subagent would cost more context than it saves. Say in one line which you chose and why, then start.

4. TEST-DRIVE EVERYTHING. Use superpowers:test-driven-development for every task — the failing test first, then the code that passes it. Before you call any task or the build done, use superpowers:verification-before-completion: run the tests, read the actual output, and never report work as finished on the strength of having written it. If something breaks, use superpowers:systematic-debugging rather than guessing at a fix.

5. TICK THE CHECKBOXES AS YOU GO. The moment a step is done and verified, edit the plan file and change its `- [ ]` to `- [x]`. That file is the only way the user can see how far you have got, so keep it current and keep it honest: never tick a box for work that is not finished and not verified.

6. NOBODY CAN ANSWER A QUESTION. Where the spec leaves a choice open, decide it yourself on the merits, write the decision and your reason into the plan file under the task it belongs to, and carry on. A question at the end of your turn builds nothing.

7. THE BAR THIS SHIP HAS TO CLEAR. The last project like this one failed on first launch: it opened with a pop-up demanding the user type in scan paths, with no way to dismiss it, and a README so complex the user asked whether there was "a simple command" instead. Do not repeat that. It must work on first launch with zero configuration — where you need to know something about the machine, find it out yourself; never open with a form or a prompt asking the user to type it in. No modal or dialog the user cannot dismiss or get past. The README's happy path is one command, not a checklist. The UI is not an afterthought bolted on last — it is part of what you are building and is held to the same bar as the logic underneath it. Before you report the build done, actually run what you built exactly as a first-time user would, starting from nothing configured, and confirm every one of those holds.

Commit as you go, one commit per completed task, and never push. When the last task is ticked and the tests pass, stop and say what you built.
""";

    /// <summary>
    /// The prompt a build is actually given. Takes the spec's PATH rather than
    /// its text: a session that has compacted can re-read the file.
    /// </summary>
    public static string ComposeBuildBrief(string specRelativePath) =>
        // ReplaceLineEndings: the template must be LF whatever this source file was saved with.
        BuildBriefTemplate.ReplaceLineEndings("\n")
            .Replace("{spec_path}", specRelativePath).Replace("{plan_dir}", PlanDir);

    // `user_prompt_of` in Server strips the framing off a stored prompt so a run
    // can be gisted out loud. A build's prompt is framing to its last line, so
    // its gist comes from the spec path instead.
    public const string BuildBriefHead = "[Long build — no human present]";

    public static bool IsBuildPrompt(string? storedPrompt) =>
        (storedPrompt ?? "").StartsWith(BuildBriefHead, StringComparison.Ordinal);

    // --- Progress, read off the plan -----------------------------------------

    // `str.splitlines()` splits on TEN characters; a regex `.` excludes only "\n"
    // and `\s` matches all ten. This class is "any character that is not one of
    // the ten", so a line-oriented pattern cannot accept a hidden separator.
    public const string OnOneLine = @"[^\r\n\v\f\x1c\x1d\x1e\x85\u2028\u2029]";

    // `## Task N: title` is what the brief asks for; `###` is what several real
    // plans use, so both are read. Anchored with \A ... \z = Python fullmatch.
    public static readonly Regex TaskHeading = new(
        @"\A#{2,4}[ \t]+Task[ \t]+(\d+)[ \t]*[:.\-—][ \t]*(" + OnOneLine + @"+?)[ \t]*\z", Ci);

    public static readonly Regex Checkbox = new(
        @"\A[ \t]*[-*][ \t]+\[([ xX])\][ \t]*(" + OnOneLine + @"*?)[ \t]*\z", Ci);

    // Strip the bold/emphasis a plan's steps are written with, so a spoken step
    // is not read out as "star star Step 1 star star".
    public static readonly Regex Emphasis = new(@"[*_`]+", Ci);

    public class PlanTask
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public int StepsDone { get; set; }
        public int StepsTotal { get; set; }

        public PlanTask(int number, string title)
        {
            Number = number;
            Title = title;
        }

        /// <summary>
        /// Done when it HAS steps and every one is ticked. A task with no
        /// checkbox steps is never done: claiming a build finished because a
        /// heading had nothing under it is the lie the run pipeline prevents.
        /// </summary>
        public bool Done => StepsTotal > 0 && StepsDone == StepsTotal;

        public override string ToString() => $"<Task {Number} '{Title}' {StepsDone}/{StepsTotal}>";
    }

    /// <summary>
    /// The N in a `Task N: title` heading, or 0 if it is not one. Exposed so the
    /// review surface lines a plan's task numbers up with its section numbers
    /// without a second regex that could drift from this one.
    /// </summary>
    public static int TaskNumberOf(string? heading)
    {
        var match = TaskHeading.Match($"## {(heading ?? "").Trim()}");
        return match.Success ? ParseInt(match.Groups[1].Value) : 0;
    }

    /// <summary>
    /// The tasks in a plan and how many of each one's steps are ticked.
    /// Checkboxes above the first task heading belong to no task and are ignored.
    /// </summary>
    public static List<PlanTask> ParsePlan(string? text)
    {
        var tasks = new List<PlanTask>();
        PlanTask? current = null;
        foreach (var line in SplitLines(text))
        {
            var heading = TaskHeading.Match(line);
            if (heading.Success)
            {
                current = new PlanTask(ParseInt(heading.Groups[1].Value),
                    Emphasis.Replace(heading.Groups[2].Value, "").Trim());
                tasks.Add(current);
                continue;
            }
            var box = Checkbox.Match(line);
            if (box.Success && current is not null)
            {
                current.StepsTotal += 1;
                if (box.Groups[1].Value is "x" or "X")
                    current.StepsDone += 1;
            }
        }
        return tasks;
    }

    /// <summary>
    /// The plan file a build is working from: the most recently MODIFIED one.
    /// The session edits the plan every time it ticks a box, so the file being
    /// worked on is the file that just changed — not the latest-named one.
    /// </summary>
    public static string? LatestPlan(string projectPath)
    {
        var directory = Path.Combine(projectPath, PlanDir.Replace('/', Path.DirectorySeparatorChar));
        List<FileInfo> plans;
        try
        {
            plans = GlobMd(directory).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        if (plans.Count == 0) return null;
        return plans.MaxBy(p => p.LastWriteTimeUtc)!.FullName;
    }

    public class PlanProgress
    {
        public string Path { get; set; }
        public List<PlanTask> Tasks { get; set; }

        public PlanProgress(string path, List<PlanTask> tasks)
        {
            Path = path;
            Tasks = tasks;
        }

        public int Total => Tasks.Count;
        public int Done => Tasks.Count(t => t.Done);

        /// <summary>The first task that is not finished — what it is working on now.</summary>
        public PlanTask? Current => Tasks.FirstOrDefault(t => !t.Done);

        public bool Finished => Total > 0 && Done == Total;
    }

    /// <summary>
    /// Progress against the project's newest plan, or null if there isn't one.
    /// Null means "no plan file", which the caller says as *still planning*.
    /// </summary>
    /// <remarks>
    /// Python's `plan_progress` cannot be `Builds.PlanProgress(...)` in C#: the
    /// nested class `PlanProgress` owns that name. So it is `GetPlanProgress`,
    /// with `PlanProgressOf` as an alias.
    /// </remarks>
    public static PlanProgress? PlanProgressOf(string projectPath) => GetPlanProgress(projectPath);

    public static PlanProgress? GetPlanProgress(string projectPath)
    {
        var path = LatestPlan(projectPath);
        if (path is null) return null;
        string text;
        try
        {
            text = ReadTextUtf8(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var tasks = ParsePlan(text);
        if (tasks.Count == 0) return null;
        return new PlanProgress(path, tasks);
    }

    // --- What may be typed into a terminal window ----------------------------
    //
    // `run_command` runs a command in a VISIBLE terminal window in the project
    // directory, after reading it back and opening a cancel window. The
    // read-back is the real gate; this is the second, deliberately narrow,
    // because the string arrives from an LLM that has been reading READMEs:
    //   * a character allowlist — no shell to compose with at all;
    //   * a first-token allowlist of things that START a project, or a path to
    //     a file that actually exists inside the project;
    //   * a length cap, because it has to be spoken aloud before it runs.
    // Whether the project DOCUMENTS the command is `IsDocumented`, said out
    // loud rather than enforced.

    public const int CommandMaxChars = 160;

    // No shell metacharacter is in this set, and it is used as a full match
    // (\A...\z): `$` would match before a trailing newline, and a newline
    // chains a command just as well as a semicolon.
    public static readonly Regex CommandAllowed = new(@"\A[A-Za-z0-9 _\-./:=,+@]+\z", Ci);

    // Things that start a project. Verbs that change a machine are absent on
    // purpose — this list is the allowlist, not a starting point.
    public static readonly HashSet<string> Launchers = new(StringComparer.Ordinal)
    {
        "npm", "pnpm", "yarn", "bun", "npx", "node", "deno",
        "python", "python3", "uv", "uvicorn", "gunicorn", "flask", "streamlit",
        "make", "cargo", "go", "ruby", "rails", "bundle", "php", "dotnet",
        "hugo", "jekyll", "vite", "next", "serve", "http-server",
    };

    public static string FirstToken(string command)
    {
        var t = command.Trim();
        if (t.Length == 0) return "";
        var i = t.IndexOf(' ');
        return i < 0 ? t : t[..i];
    }

    /// <summary>
    /// Null if this may be run, or the sentence JARVIS should say instead.
    /// Refusals are spoken, so each says what is wrong in an actionable clause.
    /// </summary>
    public static string? CommandProblem(string? command, string projectPath)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0)
            return "There was nothing to run.";
        if (text.Length > CommandMaxChars)
            return "That command is too long to read back to you, sir — give me " +
                   "the short one the project actually starts with.";
        if (!CommandAllowed.IsMatch(text))
            return "I'll only run a single plain command, sir — no pipes, no " +
                   "semicolons, nothing chained together.";

        var token = FirstToken(text);
        if (Launchers.Contains(token))
            return null;

        // A path INSIDE the project that really exists (`.venv/Scripts/python.exe`,
        // `./scripts/dev.cmd`). Containment is proved by resolving both sides,
        // not by looking at the string — `../../` resolves out and is refused.
        if (token.Contains('/'))
        {
            try
            {
                var root = RepoRead.RealPath(projectPath);
                var candidate = RepoRead.RealPath(Path.Combine(root, token));
                if (File.Exists(candidate) && RepoRead.IsRelativeTo(candidate, root))
                    return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
            }
            return $"There's no {token} in that project, sir, so I've run " +
                   "nothing.";
        }

        return $"I don't start things with {token}, sir — I'll run the command " +
               "the project documents, and nothing else.";
    }

    public static HashSet<string> PackageScripts(string projectPath)
    {
        try
        {
            var raw = ReadTextUtf8(Path.Combine(projectPath, "package.json"));
            var node = Py.Loads(raw);
            if (node is not JsonObject obj) return [];
            if (obj["scripts"] is JsonObject scripts)
                return new HashSet<string>(scripts.Select(kv => kv.Key), StringComparer.Ordinal);
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    public const int ReadmeBytes = 60_000;

    /// <summary>
    /// Whether the project itself says to run this: its README, its package.json
    /// scripts, its Makefile targets. Colours what JARVIS says, never refuses —
    /// a project with no README is not a suspicious project.
    /// </summary>
    public static bool IsDocumented(string? command, string projectPath)
    {
        var text = string.Join(" ", SplitWs(command));
        if (text.Length == 0) return false;
        var root = projectPath;

        foreach (var name in new[] { "README.md", "README", "readme.md", "Readme.md", "README.txt" })
        {
            string body;
            try
            {
                body = ReadTextUtf8(Path.Combine(root, name));
                if (body.Length > ReadmeBytes) body = body[..ReadmeBytes];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (string.Join(" ", SplitWs(body)).ToLowerInvariant().Contains(text.ToLowerInvariant()))
                return true;
        }

        var parts = SplitWs(text);
        var scripts = PackageScripts(projectPath);
        if (scripts.Count > 0)
        {
            if (parts.Length >= 3 && parts[0] is "npm" or "pnpm" or "bun"
                && parts[1] == "run" && scripts.Contains(parts[2]))
                return true;
            if (parts.Length >= 2 && parts[0] == "yarn" && scripts.Contains(parts[1]))
                return true;
            if (parts.Length >= 2 && parts[0] is "npm" or "pnpm" or "bun" or "yarn"
                && parts[1] is "start" or "dev" or "test" or "build"
                && scripts.Contains(parts[1]))
                return true;
        }

        if (parts.Length >= 2 && parts[0] == "make")
        {
            foreach (var name in new[] { "Makefile", "makefile", "GNUmakefile" })
            {
                string body;
                try
                {
                    body = ReadTextUtf8(Path.Combine(root, name));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (Regex.IsMatch(body, "^" + Regex.Escape(parts[1]) + @"\s*:", RegexOptions.Multiline | Ci))
                    return true;
            }
        }
        return false;
    }

    // The spec path the brief points at, recovered from a stored build prompt.
    public static readonly Regex SpecLine = new(
        @"^\s*(" + Regex.Escape(SpecDir) + @"/\S+\.md)\s*$", RegexOptions.Multiline | Ci);

    /// <summary>
    /// A few words a person could hear, for a run whose prompt is all framing:
    /// the topic recovered from the spec filename, which is the topic the user named.
    /// </summary>
    public static string GistOfBuild(string? storedPrompt)
    {
        var match = SpecLine.Match(storedPrompt ?? "");
        if (!match.Success) return "";
        var stem = Stem(match.Groups[1].Value);           // YYYY-MM-DD-topic-design
        stem = Regex.Replace(stem, @"^\d{4}-\d{2}-\d{2}-", "", Ci);
        stem = Regex.Replace(stem, @"-design$", "", Ci);
        return stem.Replace("-", " ").Trim();
    }

    // --- small Python-semantics helpers shared by Builds / Specs / RepoRead ---

    private static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>`str.splitlines()` — the ten separators, `\r\n` as one, no trailing empty line.</summary>
    public static List<string> SplitLines(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        int start = 0, i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsLineBreak(c))
            {
                result.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                start = i;
            }
            else i++;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    /// <summary>`str.split()` with no arguments.</summary>
    public static string[] SplitWs(string? text) =>
        (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>`Path.read_text(encoding="utf-8", errors="replace")` — keeps a BOM as U+FEFF, like Python.</summary>
    public static string ReadTextUtf8(string path) => Py.Utf8NoBom.GetString(File.ReadAllBytes(path));

    /// <summary>pathlib `.suffix`: from the last dot, unless the dot leads or ends the name.</summary>
    public static string Suffix(string name)
    {
        var n = name.TrimEnd('/', '\\');
        var slash = n.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) n = n[(slash + 1)..];
        var i = n.LastIndexOf('.');
        return i > 0 && i < n.Length - 1 ? n[i..] : "";
    }

    /// <summary>pathlib `.stem`.</summary>
    public static string Stem(string path)
    {
        var n = path.TrimEnd('/', '\\');
        var slash = n.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) n = n[(slash + 1)..];
        var suffix = Suffix(n);
        return suffix.Length > 0 ? n[..^suffix.Length] : n;
    }

    /// <summary>`Path(dir).glob("*.md")` — case-insensitive, files only, hidden ones included.</summary>
    public static IEnumerable<FileInfo> GlobMd(string directory)
    {
        var di = new DirectoryInfo(directory);
        if (!di.Exists) return [];
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        return di.EnumerateFiles("*", opts)
            .Where(f => Suffix(f.Name).Equals(".md", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>`int()` of a `\d+` match: Unicode digits included, saturating instead of overflowing.</summary>
    public static int ParseInt(string digits)
    {
        long v = 0;
        foreach (var c in digits)
        {
            var d = (int)char.GetNumericValue(c);
            if (d < 0) continue;
            v = v * 10 + d;
            if (v > int.MaxValue) return int.MaxValue;
        }
        return (int)v;
    }
}
