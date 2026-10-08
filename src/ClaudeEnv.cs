using System.Runtime.InteropServices;
using System.Text;

namespace Jplus;

/// <summary>
/// The environment every Claude Code child of JARVIS is given (claude_env.py).
///
/// There is exactly one rule and it is a billing rule: JARVIS's Claude Code
/// children run on the user's **subscription**, never on an API key. The CLI
/// prefers an inherited `ANTHROPIC_API_KEY` over the login without saying so
/// (`claude auth status` still reports loggedIn while `apiKeySource` flips to
/// the env key), so a key in the environment silently moves every spawned run
/// onto paid API billing. `.env` is loaded into the process environment at boot
/// and a developer's `.env` may legitimately hold that key — this module is
/// where the scrub is written once, for the brain and the run pipeline alike.
/// </summary>
public static partial class ClaudeEnv
{
    // Every ANTHROPIC_* variable, not just the key: the base URL and the model
    // override redirect a child just as effectively as credentials do.
    public static readonly string[] ScrubbedEnvPrefixes = ["CLAUDE_CODE_", "ANTHROPIC_"];
    public static readonly HashSet<string> ScrubbedEnvKeys = new(StringComparer.OrdinalIgnoreCase) { "CLAUDECODE" };

    // The Python raised asyncio's 64 KiB default *line* buffer to this, because a
    // single stream-json line carrying a big tool result routinely exceeds 64 KiB
    // and an uncaught overrun killed a healthy run. In C# the line readers
    // (RunExecutor.LineReader) enforce this same ceiling: comfortably above any
    // real line (low single-digit MiB) while a runaway line still cannot balloon
    // memory without bound.
    public const int StreamLineLimit = 64 * 1024 * 1024;  // 64 MiB per line

    /// <summary>
    /// A copy of the environment with everything that would redirect or
    /// re-bill a Claude Code child removed. Everything else — PATH, USERPROFILE,
    /// CLAUDE_CONFIG_DIR, the user's own variables — passes through untouched.
    /// Windows environment names are case-insensitive, so the match is too.
    /// </summary>
    public static Dictionary<string, string?> ChildEnv(IDictionary<string, string?>? @base = null)
    {
        // Windows environment names are case-insensitive; Linux ones are not.
        var result = new Dictionary<string, string?>(Py.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        IEnumerable<KeyValuePair<string, string?>> source;
        if (@base is null)
        {
            var list = new List<KeyValuePair<string, string?>>();
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                list.Add(new((string)e.Key, e.Value as string));
            source = list;
        }
        else source = @base;

        foreach (var (k, v) in source)
        {
            if (ScrubbedEnvPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (ScrubbedEnvKeys.Contains(k)) continue;
            result[k] = v;
        }
        return result;
    }

    /// <summary>
    /// The argv for a configured `claude` command, which may carry arguments.
    /// Callers start `argv[0]` with `argv[1..]` (plus their own flags) as the
    /// argument list.
    ///
    /// A path to an existing file is taken whole: splitting would mangle the
    /// backslashes / spaces of a Windows path. Otherwise the string is split
    /// shell-style — quotes group, but a backslash is literal (POSIX `shlex`
    /// rules would eat the separators of `C:\Users\...\claude.exe`).
    ///
    /// Windows-only twist: `Process.Start` with UseShellExecute=false only
    /// launches real executables, so an npm `claude.cmd` / `.bat` shim is
    /// returned as `cmd.exe /c &lt;shim&gt; ...`. A shim path containing spaces
    /// is converted to its 8.3 short form when possible, because cmd.exe's
    /// `/c` quote-stripping would otherwise break as soon as any later
    /// argument is quoted too.
    /// </summary>
    public static List<string> SplitCommand(string command)
    {
        List<string> argv;
        if (File.Exists(command)) argv = [command];
        else argv = ShellSplit(command);
        if (argv.Count == 0) return argv;

        var exe = argv[0];
        string? resolved = File.Exists(exe) ? Path.GetFullPath(exe) : Py.Which(exe);
        if (resolved is null) return argv;   // let the spawn report "not found"

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (Py.IsWindows && ext is ".cmd" or ".bat")
        {
            var shim = resolved.Contains(' ') ? ShortPath(resolved) ?? resolved : resolved;
            var comspec = Py.Getenv("ComSpec");
            if (string.IsNullOrEmpty(comspec) || !File.Exists(comspec)) comspec = "cmd.exe";
            var wrapped = new List<string> { comspec, "/d", "/c", shim };
            wrapped.AddRange(argv.Skip(1));
            return wrapped;
        }
        argv[0] = resolved;
        return argv;
    }

    /// <summary>
    /// Split a command line into words: whitespace separates, '...' and "..."
    /// group (quotes removed), backslash is an ordinary character.
    /// </summary>
    public static List<string> ShellSplit(string command)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        foreach (var c in command ?? "")
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else cur.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; inWord = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (inWord) { words.Add(cur.ToString()); cur.Clear(); inWord = false; }
                continue;
            }
            cur.Append(c);
            inWord = true;
        }
        if (quote != '\0') throw new ArgumentException("No closing quotation");
        if (inWord) words.Add(cur.ToString());
        return words;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    /// <summary>The 8.3 short form of an existing path, or null when the volume has none.</summary>
    public static string? ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            if (n == 0 || n > sb.Capacity) return null;
            var s = sb.ToString();
            return s.Contains(' ') ? null : s;
        }
        catch { return null; }
    }
}
