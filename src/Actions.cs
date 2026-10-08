using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Jplus;

/// <summary>
/// JARVIS Action Executor — system actions. The macOS original drove
/// Terminal.app and Chrome through AppleScript; this drives Windows Terminal
/// (or a console window) and the browsers' own executables.
///
/// Execute actions IMMEDIATELY, before generating any LLM response.
/// Each function returns {"success": bool, "confirmation": str}.
///
/// No shell ever parses a value that came from the model: URLs and paths are
/// passed to the target program as their own argv entries, and a command for
/// the terminal is only ever handed to the terminal the user can see.
/// Every function here touches the user's desktop, so each one first refuses
/// when running as a service (session 0 has no desktop).
/// </summary>
public static partial class Actions
{
    public static readonly Microsoft.Extensions.Logging.ILogger Log = Py.Log("jarvis.actions");

    // The colour a JARVIS-opened Windows Terminal tab wears — the stand-in for
    // the original's temporary "Ocean" Terminal profile, so the user can see
    // which window JARVIS is driving.
    public const string JarvisTabColor = "#1E5AA8";

    private static Dictionary<string, object?> Refusal(string? editor = null)
    {
        var d = new Dictionary<string, object?> { ["success"] = false };
        if (editor is not null) d["editor"] = editor;
        d["confirmation"] = Py.NoDesktopReason;
        return d;
    }

    /// <summary>
    /// The original temporarily switched the front Terminal window to the
    /// "Ocean" profile and reverted it after `revertAfter` seconds. Windows
    /// Terminal has no scriptable per-window profile switch; the equivalent
    /// mark is applied at launch instead (`--tabColor`, see OpenTerminal), so
    /// this is a no-op kept for callers.
    /// </summary>
    public static async Task MarkTerminalAsJarvis(double revertAfter = 5.0) => await Task.CompletedTask;

    /// <summary>Counterpart of MarkTerminalAsJarvis; nothing to revert on Windows.</summary>
    public static async Task RevertTerminalTheme(string profileName) => await Task.CompletedTask;

    /// <summary>Escape a string for safe embedding in an AppleScript double-quoted string (kept for parity).</summary>
    public static string ApplescriptEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");

    /// <summary>
    /// Open a terminal and optionally run a command. Marks it blue for JARVIS.
    ///
    /// Callers (written for the POSIX original) pass `cd &lt;quoted dir&gt;` or
    /// `cd &lt;quoted dir&gt; &amp;&amp; &lt;command&gt;`. The leading `cd` is taken apart here and
    /// becomes the window's starting directory — the directory never passes
    /// through a shell — and whatever follows `&amp;&amp;` runs in cmd.exe inside
    /// the visible window. `cwd` may be given directly instead.
    /// </summary>
    public static async Task<Dictionary<string, object?>> OpenTerminal(string command = "", string? cwd = null)
    {
        if (Py.IsServiceSession) return Refusal();
        var (dir, rest) = SplitLeadingCd(command ?? "");
        dir ??= cwd;
        if (dir is not null && !Directory.Exists(dir))
        {
            Log.LogError("open_terminal failed: no such directory {Dir}", dir);
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["confirmation"] = "I had trouble opening Terminal, sir.",
            };
        }

        bool success;
        try
        {
            if (!Py.IsWindows)
            {
                success = StartLinuxTerminal(dir ?? Py.Home, rest);
                if (success) await MarkTerminalAsJarvis();
                return new Dictionary<string, object?>
                {
                    ["success"] = success,
                    ["confirmation"] = success ? "Terminal is open, sir." : "I had trouble opening Terminal, sir.",
                };
            }
            var wt = Py.Which("wt");
            ProcessStartInfo psi;
            if (wt is not null)
            {
                // Windows Terminal: new tab in `dir`, coloured as JARVIS's.
                // `;` separates wt sub-commands, so it is escaped in the command.
                var args = new List<string>();
                if (dir is not null) { args.Add("-d"); args.Add(dir); }
                args.Add("--tabColor"); args.Add(JarvisTabColor);
                args.Add("--title"); args.Add("JARVIS");
                var line = JoinArgs(args);
                // The command is appended verbatim (not argv-quoted): wt hands
                // the rest of its command line to cmd.exe, which has its own
                // quoting rules and would choke on \" escapes.
                if (rest.Length > 0) line += " cmd.exe /K " + rest.Replace(";", "\\;");
                psi = new ProcessStartInfo(wt) { UseShellExecute = false, Arguments = line };
            }
            else
            {
                // A plain console window. UseShellExecute gives it a console of
                // its own instead of sharing ours.
                psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = true,
                    Arguments = rest.Length > 0 ? "/K " + rest : "/K title JARVIS",
                };
            }
            psi.WorkingDirectory = dir ?? Py.Home;
            using var proc = Process.Start(psi);
            success = proc is not null;
        }
        catch (Exception e)
        {
            Log.LogError("open_terminal failed: {Error}", e.Message);
            success = false;
        }
        if (success) await MarkTerminalAsJarvis();
        return new Dictionary<string, object?>
        {
            ["success"] = success,
            ["confirmation"] = success ? "Terminal is open, sir." : "I had trouble opening Terminal, sir.",
        };
    }

    // ── Linux terminals ─────────────────────────────────────────────────────
    //
    // There is no one terminal on Linux. JARVIS_TERMINAL names one explicitly
    // (a name from the table below, or any program that takes `-e cmd args`);
    // otherwise the first installed one, desktop defaults first. Each entry
    // says how to give it a starting directory and a command to run; the
    // directory is also set as the child's working directory, so a terminal
    // whose flag we don't know still opens in the right place.

    private sealed record TermSpec(string Exe, Func<string, string[]> DirArgs, string[] ExecPrefix);

    private static readonly TermSpec[] LinuxTerminals =
    [
        new("gnome-terminal", d => [$"--working-directory={d}", "--title=JARVIS"], ["--"]),
        new("ptyxis", d => ["--new-window", $"--working-directory={d}"], ["--"]),
        new("konsole", d => ["--workdir", d], ["-e"]),
        new("xfce4-terminal", d => [$"--working-directory={d}", "--title=JARVIS"], ["-x"]),
        new("kitty", d => ["--directory", d, "--title", "JARVIS"], []),
        new("alacritty", d => ["--working-directory", d, "--title", "JARVIS"], ["-e"]),
        new("wezterm", d => ["start", "--cwd", d], ["--"]),
        new("foot", d => ["--working-directory", d, "--title", "JARVIS"], []),
        new("tilix", d => ["--working-directory", d], ["-e"]),
        new("terminator", d => ["--working-directory", d, "--title", "JARVIS"], ["-x"]),
        new("x-terminal-emulator", _ => [], ["-e"]),
        new("xterm", _ => ["-T", "JARVIS"], ["-e"]),
    ];

    /// <summary>
    /// Open a terminal window in `dir`, running `rest` (a POSIX shell command
    /// line, already vetted by the caller) and then staying open in the
    /// user's shell — the `cmd /K` behaviour. Detached: the window outlives us.
    /// </summary>
    public static bool StartLinuxTerminal(string dir, string rest)
    {
        TermSpec? spec = null;
        string? exe = null;
        var wanted = Py.Getenv("JARVIS_TERMINAL");
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            exe = Py.Which(wanted.Trim());
            spec = LinuxTerminals.FirstOrDefault(t => t.Exe == Path.GetFileName(wanted.Trim()))
                   ?? new TermSpec(wanted.Trim(), _ => [], ["-e"]);
        }
        else
        {
            foreach (var t in LinuxTerminals)
                if (Py.Which(t.Exe) is { } found) { spec = t; exe = found; break; }
        }
        if (spec is null || exe is null)
        {
            Log.LogError("open_terminal failed: no terminal emulator found (set JARVIS_TERMINAL)");
            return false;
        }

        var shell = Py.Getenv("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = Py.Which("bash") ?? "/bin/sh";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in spec.DirArgs(dir)) psi.ArgumentList.Add(a);
        if (rest.Length > 0)
        {
            foreach (var a in spec.ExecPrefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(shell);
            psi.ArgumentList.Add("-c");
            // Run the command, then hand the window to an interactive shell ($0 = the shell).
            psi.ArgumentList.Add(rest + "; exec \"$0\" -i");
            psi.ArgumentList.Add(shell);
        }
        using var proc = Process.Start(psi);
        return proc is not null;
    }

    /// <summary>
    /// Split `cd &lt;dir&gt; [&amp;&amp; rest]` into (dir, rest). `dir` is POSIX-shell
    /// quoted (shlex.quote) by the original's callers, or cmd-style
    /// (`cd /d "C:\x"`). A command with no leading cd comes back as (null, command).
    /// </summary>
    public static (string? dir, string rest) SplitLeadingCd(string command)
    {
        var s = command.TrimStart();
        if (!(s.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || s.StartsWith("cd\t", StringComparison.OrdinalIgnoreCase)))
            return (null, command.Trim());
        int i = 2;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (s.Length >= i + 2 && s.Substring(i, 2).Equals("/d", StringComparison.OrdinalIgnoreCase)
            && (s.Length == i + 2 || char.IsWhiteSpace(s[i + 2])))
        {
            i += 2;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
        // One shell word: '...' (literal), "..." (with \ escapes), or bare.
        var word = new StringBuilder();
        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            char c = s[i];
            if (c == '\'')
            {
                int close = s.IndexOf('\'', i + 1);
                if (close < 0) return (null, command.Trim());
                word.Append(s, i + 1, close - i - 1);
                i = close + 1;
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    // Backslash only escapes " here: Windows paths are full of them.
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"') i++;
                    word.Append(s[i]);
                    i++;
                }
                if (i >= s.Length) return (null, command.Trim());
                i++;
            }
            else if (c == '&') break;
            else
            {
                word.Append(c);
                i++;
            }
        }
        var remainder = s[i..].TrimStart();
        string rest;
        if (remainder.Length == 0) rest = "";
        else if (remainder.StartsWith("&&", StringComparison.Ordinal)) rest = remainder[2..].Trim();
        else return (null, command.Trim());   // not a shape we understand: leave it whole
        return (word.Length > 0 ? word.ToString() : null, rest);
    }

    /// <summary>
    /// Open URL in user's browser (Chrome or Firefox).
    ///
    /// The URL is handed to the browser as one argv entry, after a `--`
    /// end-of-switches marker for Chrome; a URL that starts with `-` is
    /// refused outright, since a browser would read it as a switch. The URL
    /// arrives from a model, out of speech, possibly echoing a page or a
    /// README, so nothing about it may be interpreted.
    /// </summary>
    public static async Task<Dictionary<string, object?>> OpenBrowser(string url, string browser = "chrome")
    {
        bool firefox = (browser ?? "").ToLowerInvariant() == "firefox";
        string appName = firefox ? "Firefox" : "Chrome";
        if (Py.IsServiceSession) return Refusal();

        bool success = false;
        try
        {
            var exe = firefox ? FindFirefox() : FindChrome();
            if (exe is null)
            {
                Log.LogError("open_browser ({App}) failed: {App} is not installed", appName, appName);
            }
            else if (string.IsNullOrEmpty(url) || url.StartsWith('-'))
            {
                Log.LogError("open_browser ({App}) failed: refusing a URL that reads as a switch", appName);
            }
            else
            {
                var args = firefox ? new List<string> { "-new-tab", url } : new List<string> { "--", url };
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (Py.IsWindows) psi.Arguments = JoinArgs(args);
                else foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                success = proc is not null;
            }
        }
        catch (Exception e)
        {
            Log.LogError("open_browser ({App}) failed: {Error}", appName, e.Message);
            success = false;
        }
        await Task.CompletedTask;
        return new Dictionary<string, object?>
        {
            ["success"] = success,
            ["confirmation"] = success ? $"Pulled that up in {appName}, sir." : $"{appName} ran into a problem, sir.",
        };
    }

    // Keep backward compat
    public static async Task<Dictionary<string, object?>> OpenChrome(string url) => await OpenBrowser(url, "chrome");

    /// <summary>
    /// The current Chrome tab's title and URL. Windows exposes no scripting
    /// dictionary for Chrome, so this reads the front Chrome window's title
    /// (" - Google Chrome" removed); the URL is not reachable this way and
    /// comes back empty. {} when Chrome has no window.
    /// </summary>
    public static async Task<Dictionary<string, object?>> GetChromeTabInfo()
    {
        if (Py.IsServiceSession) return new Dictionary<string, object?>();
        if (!Py.IsWindows)
        {
            // Linux: the window list (Screen.ListWindows) carries the titles.
            try
            {
                foreach (var w in await Screen.ListWindows())
                {
                    foreach (var suffix in new[] { " - Google Chrome", " - Chromium", " – Google Chrome", " – Chromium" })
                        if (w.Title.EndsWith(suffix, StringComparison.Ordinal))
                            return new Dictionary<string, object?> { ["title"] = w.Title[..^suffix.Length], ["url"] = "" };
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("get_chrome_tab_info failed: {Error}", e.Message);
            }
            return new Dictionary<string, object?>();
        }
        try
        {
            foreach (var p in Process.GetProcessesByName("chrome"))
            {
                using (p)
                {
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;
                    const string suffix = " - Google Chrome";
                    if (title.EndsWith(suffix, StringComparison.Ordinal)) title = title[..^suffix.Length];
                    return new Dictionary<string, object?> { ["title"] = title, ["url"] = "" };
                }
            }
            return new Dictionary<string, object?>();
        }
        catch (Exception e)
        {
            Log.LogWarning("get_chrome_tab_info failed: {Error}", e.Message);
            return new Dictionary<string, object?>();
        }
    }

    // --- Opening code where the user actually reads it ---------------------
    //
    // The user's words: "maybe he should be able to open code files in VS Code
    // or text editor." VS Code first when it is installed, the system default
    // otherwise.
    //
    // No shell: Code.exe is started with the path as its own argv entry, so a
    // filename cannot be quoted out of anything. (`code` on PATH is a .cmd
    // shim, which would put the path through cmd.exe — so the shim is only
    // used to locate Code.exe beside it.) Containment and the sensitive-file
    // wall are the CALLER's job and have already run by the time this is
    // reached — see Server.ToolOpenInEditor.

    /// <summary>Where VS Code is usually installed (per-user, then machine-wide).</summary>
    public static readonly string[] VscodeApp = !Py.IsWindows
    ? [
        "/usr/share/code/bin/code", "/usr/bin/code", "/snap/bin/code",
        "/var/lib/flatpak/exports/bin/com.visualstudio.code",
        Path.Combine(Py.Home, ".local", "share", "flatpak", "exports", "bin", "com.visualstudio.code"),
    ]
    : [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
    ];

    /// <summary>The argv that opens `path` in VS Code, or null if it is not installed.</summary>
    public static List<string>? VscodeCommand(string path)
    {
        var shim = Py.Which("code");
        if (!Py.IsWindows)
        {
            // Linux `code` is a launcher script that passes "$@" through untouched.
            if (shim is not null) return [shim, path];
            foreach (var exe in VscodeApp)
                if (Py.IsExecutableFile(exe)) return [exe, path];
            return null;
        }
        if (shim is not null)
        {
            var ext = Path.GetExtension(shim).ToLowerInvariant();
            if (ext == ".exe") return [shim, path];
            // <install>\bin\code.cmd → <install>\Code.exe
            var beside = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shim)!, "..", "Code.exe"));
            if (File.Exists(beside)) return [beside, path];
        }
        foreach (var exe in VscodeApp)
            if (File.Exists(exe)) return [exe, path];
        return null;
    }

    /// <summary>Open a file or directory in VS Code, else in the system default.</summary>
    public static async Task<Dictionary<string, object?>> OpenInEditor(string path)
    {
        var argv = VscodeCommand(path);
        var editor = "VS Code";
        if (argv is null) editor = "your editor";
        if (Py.IsServiceSession) return Refusal(editor);

        bool success;
        try
        {
            if (argv is not null)
            {
                // Not awaited: Code.exe stays running as the editor itself
                // when no VS Code window was open yet.
                var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
                if (Py.IsWindows) psi.Arguments = JoinArgs(argv.Skip(1));
                else foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                success = proc is not null;
            }
            else
            {
                success = Py.ShellOpen(path);
            }
        }
        catch (Exception e)
        {
            Log.LogError("open_in_editor could not launch: {Error}", e.Message);
            return new Dictionary<string, object?>
            {
                ["success"] = false, ["editor"] = editor,
                ["confirmation"] = "I couldn't open an editor, sir.",
            };
        }

        if (!success) Log.LogError("open_in_editor failed: {Path}", path);
        await Task.CompletedTask;
        return new Dictionary<string, object?>
        {
            ["success"] = success,
            ["editor"] = editor,
            ["confirmation"] = success ? $"Opened that in {editor}, sir." : $"{editor} wouldn't open that, sir.",
        };
    }

    /// <summary>Generate a kebab-case project folder name from the prompt.</summary>
    public static string GenerateProjectName(string prompt)
    {
        const RegexOptions o = RegexOptions.CultureInvariant;
        // First: check for a quoted name like "tiktok-analytics-dashboard"
        var quoted = Regex.Match(prompt, "\"([^\"]+)\"", o);
        if (quoted.Success)
        {
            var name = JarvisMemory.PyStrip(quoted.Groups[1].Value);
            // Already kebab-case or close to it
            name = JarvisMemory.PyStrip(Regex.Replace(name, @"[^a-zA-Z0-9\s-]", "", o));
            if (name.Length > 0) return Regex.Replace(name.ToLowerInvariant(), @"[\s]+", "-", o);
        }

        // Second: check for "called X" or "named X" pattern
        var called = Regex.Match(prompt, @"(?:called|named)\s+(\S+(?:[-_]\S+)*)", o | RegexOptions.IgnoreCase);
        if (called.Success)
        {
            var name = Regex.Replace(called.Groups[1].Value, "[^a-zA-Z0-9-]", "", o);
            if (name.Length > 3) return name.ToLowerInvariant();
        }

        // Fallback: extract meaningful words
        var words = JarvisMemory.PySplit(Regex.Replace(prompt.ToLowerInvariant(), @"[^a-zA-Z0-9\s]", "", o));
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "the", "an", "me", "build", "create", "make", "for", "with", "and",
            "to", "of", "i", "want", "need", "new", "project", "directory", "called",
            "on", "desktop", "that", "application", "app", "full", "stack", "simple",
            "web", "page", "site", "named",
        };
        var meaningful = words.Where(w => !skip.Contains(w) && w.Length > 2).Take(4).ToList();
        return meaningful.Count > 0 ? string.Join("-", meaningful) : "jarvis-project";
    }

    // ── Windows helpers ─────────────────────────────────────────────────────

    /// <summary>Chrome: on Linux the usual launcher names on PATH; on Windows App Paths, then the install folders.</summary>
    public static string? FindChrome() => !Py.IsWindows
        ? new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" }.Select(Py.Which).FirstOrDefault(p => p is not null)
        : AppPath("chrome.exe") ?? FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));

    /// <summary>firefox.exe: App Paths registration, then the usual install folders.</summary>
    public static string? FindFirefox() => !Py.IsWindows
        ? Py.Which("firefox") ?? Py.Which("firefox-esr")
        : AppPath("firefox.exe") ?? FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox", "firefox.exe"));

    private static string? AppPath(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string p)
                {
                    p = p.Trim().Trim('"');
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>Join argv into one command line using the MSVCRT quoting rules (what ArgumentList does).</summary>
    public static string JoinArgs(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteArg));

    public static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', backslashes); sb.Append(c); }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
