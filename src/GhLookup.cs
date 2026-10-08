using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// GitHub, through `gh`: the fast path for a question about a repository.
///
/// `gh api /repos/{owner}/{repo}/license` answers in ~0.5s, authoritatively,
/// where a web search through a `claude -p` turn takes 9-16s. So repositories
/// do not go to a web search.
///
/// Three rules hold this file together:
///
/// 1. **It takes what a person SAYS.** "the arcreactor repo", "my Arc Loop repo" —
///    not `owner/name`. Everything arrives through speech recognition.
/// 2. **It never picks.** "arcreactor" really does match five repositories from five
///    owners; answering a licence question about the wrong one is worse than
///    asking which. One match is an answer, several are a question.
/// 3. **`gh` is a subprocess taking user-derived input.** Every call is an
///    argument LIST — there is no shell anywhere in here — and every call has a deadline.
/// </summary>
public static partial class GhLookup
{
    public static readonly ILogger Log = Py.Log("jarvis.gh");

    // One `gh` call. An unauthenticated or rate-limited `gh` fails on its own in
    // well under a second, so this is not the path to a spoken error — it is the
    // guard against a call that never returns at all.
    public const double GhCallTimeout = 5.0;

    // How many repositories a search may come back with. Enough to hear the
    // ambiguity, few enough to say out loud.
    public const int SearchLimit = 5;

    // How many of them JARVIS names when he asks which one.
    public const int NamesWhenAsking = 3;

    // The top of a README, for "what IS this?". The tool result is capped at 1,500
    // characters and the untrusted body at 1,200, so a bigger number here would
    // only be cut off by `_wrap_untrusted` — the description and the licence must
    // never be the part that falls off the end.
    public const int ReadmeChars = 700;

    // GitHub's own grammar for the two halves of a full name. Everything that
    // reaches a header line is matched against this, so a name can never carry a
    // quote, a newline or an angle bracket into text the brain reads as JARVIS's own.
    //
    // The Python kept these unanchored and used them ONLY with `fullmatch`. In C#
    // the patterns are anchored with `\A ... \z` (never `$`, which matches before a
    // trailing newline), so `FullNameRe.IsMatch(x)` IS fullmatch.
    public const string Owner = @"[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})";
    public const string Name = @"[A-Za-z0-9._-]{1,100}";

    public static readonly Regex FullNameRe =
        new($@"\A(?:{Owner}/{Name})\z", RegexOptions.CultureInvariant);

    public static readonly Regex UrlRe =
        new($@"\A(?:(?:https?://)?(?:www\.)?github\.com/({Owner})/({Name}?)(?:\.git)?/?)\z",
            RegexOptions.CultureInvariant);

    /// <summary>`FULL_NAME_RE.fullmatch(text)` as a bool.</summary>
    public static bool FullMatch(string? text) => text is not null && FullNameRe.IsMatch(text);

    // Words a person says around a repository's name that are not part of it.
    // Deliberately short: "open" and "source" are NOT here, because "open SEO" is
    // the actual name of the thing he asked about.
    public static readonly HashSet<string> Filler = new()
    {
        "the", "a", "an", "my", "our", "your", "his", "her", "their", "that",
        "this", "repo", "repos", "repository", "repositories", "github",
        "called", "named", "please",
    };

    public class Repo
    {
        public string FullName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Licence { get; set; } = "";     // SPDX id, or "" when GitHub detects none
        public int Stars { get; set; }
        public string PushedAt { get; set; } = "";    // ISO 8601, as GitHub gives it
        public bool Archived { get; set; }
        public bool Private { get; set; }
        public string Readme { get; set; } = "";
    }

    public class Candidate
    {
        public Candidate() { }
        public Candidate(string fullName, string description = "")
        {
            FullName = fullName;
            Description = description;
        }
        public string FullName { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// One repository, or several to choose between, or a reason for neither.
    /// `Problem` is a short machine-readable cause the caller turns into a
    /// sentence: no_gh, auth, rate_limited, not_found, timeout, unavailable.
    /// </summary>
    public class Lookup
    {
        public Repo? Repo { get; set; }
        public List<Candidate> Candidates { get; set; } = new();
        public string Problem { get; set; } = "";
        public string Query { get; set; } = "";
    }

    /// <summary>Where `gh` is, or null.</summary>
    public static string? GhPath() => Py.Which("gh");

    /// <summary>
    /// (returncode, stdout, stderr) for one `gh` call. THE seam. An argument
    /// list, never a command string: `args` carries a repository name that came
    /// out of speech recognition by way of a language model.
    /// Throws FileNotFoundException when `gh` is missing and TimeoutException on timeout.
    /// </summary>
    public static async Task<(int ReturnCode, string Stdout, string Stderr)> RunGh(List<string> args, double timeout)
    {
        var binary = GhPath();
        if (binary is null) throw new FileNotFoundException("gh");
        var r = await Py.Run(binary, args, timeout);
        if (r.TimedOut) throw new TimeoutException($"gh timed out after {timeout}s");
        return (r.ReturnCode, r.Stdout, r.Stderr);
    }

    /// <summary>
    /// What `gh` actually prints, in the shapes it actually prints them:
    /// `gh: Not Found (HTTP 404)`, `gh: Bad credentials (HTTP 401)`,
    /// `gh: API rate limit exceeded ... (HTTP 403)`.
    /// </summary>
    public static string ProblemFrom(string? stderr)
    {
        var text = (stderr ?? "").ToLowerInvariant();
        if (text.Contains("rate limit")) return "rate_limited";
        if (text.Contains("401") || text.Contains("bad credentials") || text.Contains("gh auth login")
            || text.Contains("authentication"))
            return "auth";
        if (text.Contains("404") || text.Contains("not found")) return "not_found";
        return "unavailable";
    }

    private static readonly char[] StripChars = ['.', ',', '\'', '"'];

    /// <summary>What to search for, out of what the user said.</summary>
    public static string SpokenToQuery(string spoken)
    {
        var words = Regex.Split((spoken ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToList();
        var kept = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var bare = word.ToLowerInvariant().Trim(StripChars);
            if (Filler.Contains(bare)) continue;
            // "on github", "from GitHub" — the preposition goes with the word it
            // belongs to. Bare "on" and "in" stay: a repository may be called `on-the-fly`.
            var nxt = i + 1 < words.Count ? words[i + 1].ToLowerInvariant().Trim(StripChars) : "";
            if (bare is "on" or "in" or "from" or "at" && nxt == "github") continue;
            kept.Add(word);
        }
        return string.Join(" ", kept.Count > 0 ? kept : words);
    }

    /// <summary>`owner/name` when the user (or the brain) already had it — including
    /// as a github.com address — otherwise null.</summary>
    public static string? FullNameIn(string spoken)
    {
        var text = (spoken ?? "").Trim();
        var m = UrlRe.Match(text);
        if (m.Success) return $"{m.Groups[1].Value}/{m.Groups[2].Value}";
        return FullNameRe.IsMatch(text) ? text : null;
    }

    /// <summary>The forms a spoken name could take as a repository name: "Arc Loop" is
    /// `arcloop`, `arc-loop` or `arc_loop` on GitHub.</summary>
    public static HashSet<string> Slugs(string query)
    {
        var low = query.ToLowerInvariant().Trim();
        var squashed = Regex.Replace(low, @"\s+", "");
        return new HashSet<string>
        {
            low, squashed, Regex.Replace(low, @"\s+", "-"), Regex.Replace(low, @"\s+", "_"),
        };
    }

    /// <summary>One lookup. Holds the login so it is fetched at most once.</summary>
    public class Session
    {
        public string? Login { get; set; }
        public string Failed { get; set; } = "";

        /// <summary>One `gh` call, or null with `Failed` set.</summary>
        public async Task<(int ReturnCode, string Stdout, string Stderr)?> Call(List<string> args)
        {
            (int rc, string output, string err) answer;
            try
            {
                answer = await RunGh(args, GhCallTimeout);
            }
            catch (FileNotFoundException)
            {
                Failed = "no_gh";
                return null;
            }
            catch (TimeoutException)
            {
                Log.LogWarning("gh {Args} timed out", string.Join(" ", args.Take(2)));
                Failed = "timeout";
                return null;
            }
            catch (Exception e)
            {
                Log.LogWarning("gh {Args} failed: {Error}", string.Join(" ", args.Take(2)), e.Message);
                Failed = "unavailable";
                return null;
            }
            if (answer.rc != 0)
            {
                Failed = ProblemFrom(answer.err);
                return null;
            }
            return answer;
        }

        public async Task<List<Candidate>> Search(string query, bool own)
        {
            var args = new List<string>
            {
                "search", "repos", "--limit", SearchLimit.ToString(), "--json", "fullName,description",
            };
            if (own)
            {
                var login = await Who();
                if (string.IsNullOrEmpty(login)) return new();
                args.AddRange(["--owner", login]);
            }
            // `--` LAST, and the query after it. The query is text a model produced
            // out of speech; one beginning with a dash would otherwise be read by `gh`
            // as a flag. `Describe`'s full name needs no such thing: `FullNameRe`
            // requires an alphanumeric first character.
            args.AddRange(["--", query]);
            var answer = await Call(args);
            if (answer is null)
            {
                // A search that finds nothing is not a failure to report as one.
                if (Failed == "not_found") Failed = "";
                return new();
            }
            JsonNode? rows;
            try
            {
                rows = JsonNode.Parse(string.IsNullOrEmpty(answer.Value.Stdout) ? "[]" : answer.Value.Stdout);
            }
            catch (JsonException)
            {
                return new();
            }
            var outList = new List<Candidate>();
            if (rows is JsonArray arr)
            {
                foreach (var row in arr)
                {
                    var full = Py.Str(row, "fullName") ?? "";
                    if (FullNameRe.IsMatch(full))
                        outList.Add(new Candidate(full, Py.Str(row, "description") ?? ""));
                }
            }
            return outList;
        }

        public async Task<string?> Who()
        {
            if (Login is null)
            {
                var answer = await Call(["api", "user", "--jq", ".login"]);
                Login = answer is not null ? answer.Value.Stdout.Trim() : "";
            }
            return string.IsNullOrEmpty(Login) ? null : Login;
        }

        /// <summary>Metadata and README together — two calls, one wait. The README is
        /// what answers "what IS this?" when the description is a line long.</summary>
        public async Task<Repo?> Describe(string fullName)
        {
            var metaCall = Call(["api", $"repos/{fullName}"]);
            var readmeCall = Call(["api", $"repos/{fullName}/readme", "-H", "Accept: application/vnd.github.raw"]);
            await Task.WhenAll(metaCall, readmeCall);
            var meta = metaCall.Result;
            var readme = readmeCall.Result;
            if (meta is null) return null;
            // A missing README is not a missing repository: plenty have none.
            Failed = "";
            JsonObject data;
            try
            {
                var node = JsonNode.Parse(string.IsNullOrEmpty(meta.Value.Stdout) ? "{}" : meta.Value.Stdout);
                data = node as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                Failed = "unavailable";
                return null;
            }
            var licence = Py.Str(data["license"], "spdx_id") ?? "";
            if (licence.ToUpperInvariant() is "NOASSERTION" or "NULL") licence = "";
            return new Repo
            {
                FullName = NonEmpty(Py.Str(data, "full_name")) ?? fullName,
                Description = Py.Str(data, "description") ?? "",
                Licence = licence,
                Stars = (int)(Py.Num(data, "stargazers_count") ?? 0),
                PushedAt = Py.Str(data, "pushed_at") ?? "",
                Archived = IsTrue(data["archived"]),
                Private = IsTrue(data["private"]),
                Readme = ReadmeText(readme is not null ? readme.Value.Stdout : ""),
            };
        }

        private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        private static bool IsTrue(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>
    /// `Accept: raw` gives the file itself. Older gh versions (and the plain
    /// endpoint) give base64 in a JSON envelope — decode that rather than reading
    /// a wall of base64 out loud.
    /// </summary>
    public static string ReadmeText(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                var blob = Py.Str(JsonNode.Parse(text), "content") ?? "";
                // Python's b64decode discards non-alphabet chars (GitHub wraps at 60 cols).
                var clean = new string(blob.Where(c => !char.IsWhiteSpace(c)).ToArray());
                text = Encoding.UTF8.GetString(Convert.FromBase64String(clean));
            }
            catch
            {
                return "";
            }
        }
        return text.Length > ReadmeChars ? text[..ReadmeChars] : text;
    }

    /// <summary>The whole question: what the user said in, one repository or a choice
    /// out. Never throws.</summary>
    public static async Task<Lookup> LookUp(string spoken)
    {
        var query = SpokenToQuery(spoken);
        var result = new Lookup { Query = query };
        if (string.IsNullOrEmpty(query))
        {
            result.Problem = "not_found";
            return result;
        }
        // Asked before anything is spawned: a machine without `gh` should hear the
        // reason instantly, not after a failed exec.
        if (GhPath() is null)
        {
            result.Problem = "no_gh";
            return result;
        }

        var session = new Session();
        var named = FullNameIn(spoken);
        if (named is not null)
        {
            var r = await session.Describe(named);
            result.Repo = r;
            result.Problem = r is not null ? "" : (NonEmptyOr(session.Failed, "not_found"));
            return result;
        }

        // His own first: "my Arc Loop repo" is a repository he owns, and his
        // account is a far smaller haystack than GitHub.
        var hard = new[] { "no_gh", "auth", "rate_limited", "timeout" };
        var mine = await session.Search(query, own: true);
        if (hard.Contains(session.Failed))
        {
            result.Problem = session.Failed;
            return result;
        }
        var chosen = Choose(mine, query);
        if (chosen is null)
        {
            if (mine.Count > 1)
            {
                result.Candidates = mine.Take(NamesWhenAsking).ToList();
                return result;
            }
            var theirs = await session.Search(query, own: false);
            if (hard.Contains(session.Failed))
            {
                result.Problem = session.Failed;
                return result;
            }
            chosen = Choose(theirs, query);
            if (chosen is null)
            {
                if (theirs.Count > 1) result.Candidates = theirs.Take(NamesWhenAsking).ToList();
                else result.Problem = "not_found";
                return result;
            }
        }

        var repo = await session.Describe(chosen.FullName);
        result.Repo = repo;
        if (repo is null) result.Problem = NonEmptyOr(session.Failed, "not_found");
        return result;
    }

    private static string NonEmptyOr(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;

    /// <summary>
    /// The one obvious answer, or null to ask. One candidate is the answer. Several
    /// are a question UNLESS exactly one of them is named the thing that was asked
    /// for — five repositories called `arcreactor` are still five.
    /// </summary>
    public static Candidate? Choose(List<Candidate> candidates, string query)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        var wanted = Slugs(query);
        var exact = candidates
            .Where(c => { var i = c.FullName.IndexOf('/'); return wanted.Contains(c.FullName[(i + 1)..].ToLowerInvariant()); })
            .ToList();
        return exact.Count == 1 ? exact[0] : null;
    }
}
