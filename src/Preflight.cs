using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// JARVIS preflight -- first-run environment checks.
///
/// Every one of these has already bitten this project: `claude` missing or too
/// old, not logged in (the voice brain runs on the user's *subscription*, never
/// an API key), `crossSessionInbound` not accepting steers into other sessions,
/// no way to send `answer_dialog`'s keystroke, and no Fish Audio key at all.
///
/// This module only *observes*. Nothing here writes a file, changes a setting,
/// or grants a permission (except `EnableCrossSessionInbound`, which only runs
/// after the user says yes). It runs at server startup, so the contract is:
/// **never throw**. A check that itself errors, hangs, or can't be parsed becomes
/// a `warn` Check carrying the error text -- this must not be able to prevent
/// the server from booting.
///
/// Windows port: the macOS Accessibility and Screen Recording permissions do not
/// exist on Windows. Their checks keep their names (the spoken summary keys off
/// them) but report the honest Windows situation instead; a `desktop_session`
/// check warns when running as a service, where no desktop is reachable at all.
/// </summary>
public static partial class Preflight
{
    public static readonly ILogger Log = Py.Log("jarvis.preflight");

    // CLAUDE.md: "2.1.224 or newer" -- cross-session messaging does not exist
    // below that. Compared as a tuple of ints, never as a string: "2.1.9" is
    // lexicographically GREATER than "2.1.224".
    public static readonly (int, int, int) MinClaudeVersion = (2, 1, 224);
    public const string MinClaudeVersionStr = "2.1.224";

    // A hung subprocess must not stall server startup. Each check gets its own
    // budget; they run concurrently in RunChecks() so the wall-clock cost is one
    // timeout, not the sum of them.
    public const double DefaultCheckTimeout = 5.0;

    public const string StatusOk = "ok";
    public const string StatusWarn = "warn";
    public const string StatusFail = "fail";

    /// <summary>
    /// One preflight result. `Remedy` is a concrete, user-actionable next step --
    /// it is null exactly when `Status` is "ok".
    /// </summary>
    public sealed record Check(string Name, string Status, string Message, string? Remedy = null)
    {
        public bool Ok => Status == StatusOk;
    }

    // ── the one subprocess boundary ─────────────────────────────────────────

    /// <summary>
    /// Run a subprocess, capturing stdout/stderr, bounded by `timeout`.
    /// `env == null` inherits this process's environment; a caller that needs a
    /// SPECIFIC environment passes one explicitly (it replaces, not merges).
    /// Never throws: a spawn failure or a timeout comes back as returncode -1
    /// with the problem described in stderr. `.cmd`/`.bat` shims (claude.cmd from
    /// npm) are launched through `cmd.exe /c`.
    /// </summary>
    public static async Task<(int ReturnCode, string Stdout, string Stderr)> RunSubprocess(
        IReadOnlyList<string> args, double timeout, IDictionary<string, string?>? env = null)
    {
        var first = args.Count > 0 ? args[0] : "?";
        if (args.Count == 0) return (-1, "", "failed to spawn ?: no command");
        string exe = args[0];
        var rest = args.Skip(1).ToList();
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            rest.Insert(0, exe);
            rest.Insert(0, "/c");
            exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }
        Py.ProcResult r;
        try
        {
            r = await Py.Run(exe, rest, timeout, env: env);
        }
        catch (Exception e)
        {
            return (-1, "", $"failed to spawn {first}: {e.Message}");
        }
        if (r.TimedOut) return (-1, "", $"{first} timed out after {PyFloat(timeout)}s");
        return (r.ReturnCode, r.Stdout, r.Stderr);
    }

    /// <summary>Python's `str(float)` for the simple values used here: 5.0 → "5.0".</summary>
    public static string PyFloat(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e16
            ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A rough `repr()` of a string or JSON value, for messages that quoted one.</summary>
    public static string PyRepr(object? value) => value switch
    {
        null => "None",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r") + "'",
        JsonValue v when v.TryGetValue<string>(out var s2) => PyRepr(s2),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonNode n => n.ToJsonString(),
        _ => value.ToString() ?? "",
    };

    /// <summary>Pull the first X.Y.Z out of e.g. '2.1.258 (Claude Code)'.</summary>
    public static (int, int, int)? ParseVersion(string text)
    {
        var m = Regex.Match(text ?? "", @"(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        try
        {
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        catch (OverflowException) { return null; }
    }

    private static int CompareVersion((int, int, int) a, (int, int, int) b)
    {
        int c = a.Item1.CompareTo(b.Item1);
        if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2);
        return c != 0 ? c : a.Item3.CompareTo(b.Item3);
    }

    private static string Strip(string? s) => (s ?? "").Trim();

    private static string OrIfEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    // ── individual checks ────────────────────────────────────────────────────

    /// <summary>`claude` is on PATH and is at least MinClaudeVersion.</summary>
    public static async Task<Check> CheckClaudeCli(double timeout = DefaultCheckTimeout)
    {
        var claude = Py.Which("claude");
        if (string.IsNullOrEmpty(claude))
        {
            return new Check(
                Name: "claude_cli",
                Status: StatusFail,
                Message: "`claude` is not on PATH.",
                Remedy: "Install Claude Code (npm install -g @anthropic-ai/claude-code, " +
                        $"{MinClaudeVersionStr} or newer) and make sure it's on PATH.");
        }

        var (rc, stdout, stderr) = await RunSubprocess([claude, "--version"], timeout);
        if (rc != 0)
        {
            return new Check(
                Name: "claude_cli",
                Status: StatusWarn,
                Message: $"`claude --version` failed: {OrIfEmpty(Strip(OrIfEmpty(stderr, stdout)), $"exit {rc}")}",
                Remedy: "Run `claude --version` yourself to see what's wrong.");
        }

        var version = ParseVersion(stdout) ?? ParseVersion(stderr);
        if (version is null)
        {
            return new Check(
                Name: "claude_cli",
                Status: StatusWarn,
                Message: $"Could not parse a version from `claude --version` output: {PyRepr(Strip(stdout))}",
                Remedy: $"Run `claude --version` yourself and confirm it's {MinClaudeVersionStr} or newer.");
        }

        var v = version.Value;
        var versionStr = $"{v.Item1}.{v.Item2}.{v.Item3}";
        if (CompareVersion(v, MinClaudeVersion) < 0)
        {
            return new Check(
                Name: "claude_cli",
                Status: StatusFail,
                Message: $"claude {versionStr} is older than the required {MinClaudeVersionStr}.",
                Remedy: "Update Claude Code: npm install -g @anthropic-ai/claude-code@latest");
        }

        return new Check("claude_cli", StatusOk, $"claude {versionStr} on PATH.");
    }

    /// <summary>
    /// Where `claude` reads its config from, under `env` -- honours
    /// CLAUDE_CONFIG_DIR exactly like the CLI does, falling back to ~/.claude.
    /// Takes an explicit env so a caller can point it at the SAME environment a
    /// subprocess was run under.
    /// </summary>
    public static string ConfigDirFromEnv(IDictionary<string, string?> env)
    {
        env.TryGetValue("CLAUDE_CONFIG_DIR", out var root);
        if (string.IsNullOrEmpty(root)) root = "~/.claude";
        return Path.GetFullPath(Py.ExpandUser(root));
    }

    /// <summary>
    /// The macOS Keychain service name `claude` stores OAuth credentials under for
    /// a given config dir: "Claude Code-credentials" for ~/.claude, otherwise with an
    /// 8-hex-char suffix from sha256(config_dir). Kept for parity; on Windows the
    /// CLI keeps credentials in a file instead (see ReadOauthRefreshExpiry).
    /// </summary>
    public static string KeychainServiceName(string configDir)
    {
        var dflt = Path.GetFullPath(Py.ExpandUser("~/.claude"));
        if (string.Equals(Path.GetFullPath(configDir), dflt, StringComparison.OrdinalIgnoreCase))
            return "Claude Code-credentials";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configDir))).ToLowerInvariant()[..8];
        return $"Claude Code-credentials-{digest}";
    }

    /// <summary>
    /// Best-effort read of the OAuth refresh token's expiry (unix seconds) for
    /// `configDir` -- WITHOUT ever surfacing the access or refresh token values,
    /// only the expiry timestamp.
    ///
    /// `claude auth status` reports only presence, and was seen reporting a
    /// session as logged in that a real turn then failed to authenticate with.
    /// The CLI decides whether a refresh will succeed from this same stored
    /// `refreshTokenExpiresAt`. On macOS that lives in the Keychain; on Windows
    /// Claude Code keeps it in `&lt;config dir&gt;\.credentials.json`.
    ///
    /// Returns null -- "unknown", never "not expired" -- when the probe can't be
    /// attempted or trusted: no file, unreadable, or not the expected shape.
    /// </summary>
    public static async Task<double?> ReadOauthRefreshExpiry(string configDir, double timeout)
    {
        try
        {
            var path = Path.Combine(configDir, ".credentials.json");
            if (!File.Exists(path)) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var text = await File.ReadAllTextAsync(path, cts.Token);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var data = Py.TryLoads(text) as JsonObject;
            if (data?["claudeAiOauth"] is not JsonObject oauth) return null;
            var expiresAtMs = Py.Num(oauth, "refreshTokenExpiresAt");
            if (expiresAtMs is null) return null;
            return expiresAtMs.Value / 1000.0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The voice brain runs on the user's subscription -- `claude` must be logged
    /// in, AND that login must actually be usable.
    ///
    /// Runs `claude auth status` under exactly `ClaudeEnv.ChildEnv()` -- the same
    /// environment the brain and run executor spawn `claude` with -- so a
    /// mismatched CLAUDE_CONFIG_DIR can't make this pass while the brain fails.
    /// The config dir in play is always named in the result.
    ///
    /// `loggedIn: true` alone is not enough: with authMethod "claude.ai" this also
    /// reads the stored refresh token's expiry and fails if it has passed. When
    /// that probe can't be trusted, the check stays OK but says so honestly.
    /// </summary>
    public static async Task<Check> CheckClaudeLogin(double timeout = DefaultCheckTimeout)
    {
        var claude = Py.Which("claude");
        if (string.IsNullOrEmpty(claude))
        {
            return new Check(
                Name: "claude_login",
                Status: StatusWarn,
                Message: "Can't check login: `claude` is not on PATH.",
                Remedy: "Install Claude Code and log in with `claude`.");
        }

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ClaudeEnv.ChildEnv()) env[kv.Key] = kv.Value;
        var configDir = ConfigDirFromEnv(env);
        var where = $"config dir: {configDir}";

        var (rc, stdout, stderr) = await RunSubprocess([claude, "auth", "status"], timeout, env);
        if (rc != 0)
        {
            return new Check(
                Name: "claude_login",
                Status: StatusFail,
                Message: $"`claude auth status` failed ({where}): {OrIfEmpty(Strip(OrIfEmpty(stderr, stdout)), $"exit {rc}")}",
                Remedy: "Run `claude` and log in.");
        }

        JsonObject? data = null;
        try { data = JsonNode.Parse(stdout) as JsonObject; } catch (JsonException) { }
        catch (ArgumentException) { }
        if (data is null)
        {
            return new Check(
                Name: "claude_login",
                Status: StatusWarn,
                Message: $"Could not parse `claude auth status` output ({where}): {PyRepr(Strip(stdout))}",
                Remedy: "Run `claude auth status` yourself to confirm you're logged in.");
        }

        if (!JsonTruthy(data["loggedIn"]))
        {
            return new Check(
                Name: "claude_login",
                Status: StatusFail,
                Message: $"Claude Code is not logged in ({where}).",
                Remedy: "Run `claude` and log in -- the voice brain runs on your subscription, not an API key.");
        }

        var email = Py.Str(data, "email");
        var message = !string.IsNullOrEmpty(email) ? $"Logged in as {email} ({where})." : $"Logged in ({where}).";

        if (Py.Str(data, "authMethod") == "claude.ai")
        {
            var expiresAt = await ReadOauthRefreshExpiry(configDir, timeout);
            if (expiresAt is null)
            {
                message += " Could not independently verify the OAuth session's refresh-token " +
                           "expiry from this process -- `claude auth status` alone can report " +
                           "\"logged in\" even when a real turn would fail to authenticate.";
            }
            else if (expiresAt.Value <= Py.Now())
            {
                var when = Py.FromEpoch(expiresAt.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                return new Check(
                    Name: "claude_login",
                    Status: StatusFail,
                    Message: $"Claude Code reports logged in ({where}), but its OAuth session's " +
                             $"refresh token expired on {when} and could not be refreshed -- this " +
                             "is the exact failure that silences the voice brain.",
                    Remedy: "Run `claude` in a terminal and log in again.");
            }
            else
            {
                message += " OAuth refresh token is current.";
            }
        }

        return new Check("claude_login", StatusOk, message);
    }

    /// <summary>Python truthiness of a parsed JSON value.</summary>
    public static bool JsonTruthy(JsonNode? n)
    {
        switch (n)
        {
            case null: return false;
            case JsonValue v when v.TryGetValue<bool>(out var b): return b;
            case JsonValue v when v.TryGetValue<double>(out var d): return d != 0;
            case JsonValue v when v.TryGetValue<string>(out var s): return s.Length > 0;
            case JsonObject o: return o.Count > 0;
            case JsonArray a: return a.Count > 0;
            default: return true;
        }
    }

    // ── keystroke access (macOS: Accessibility) ──────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private const uint DesktopReadObjects = 0x0001;

    /// <summary>
    /// On macOS: whether osascript has Accessibility, without prompting for it.
    /// On Windows there is no such permission. What actually decides whether
    /// `answer_dialog`'s keystroke lands is (a) whether this process can reach the
    /// interactive input desktop at all (not as a service; not while the
    /// workstation is locked or a UAC prompt is up), and (b) UIPI: a non-elevated
    /// process cannot send input to an elevated window. Both are reported; the
    /// check only observes and never sends a key.
    /// </summary>
    public static async Task<Check> CheckAccessibility(double timeout = DefaultCheckTimeout)
    {
        if (!Py.IsWindows)
        {
            // Linux: no permission to grant either. `answer_dialog` reaches a session
            // only through the tmux / WezTerm pane that owns its pty (Dialog.Linux.cs),
            // which needs no desktop at all.
            var hosts = new[] { "tmux", "wezterm" }.Where(h => Py.Which(h) is not null).ToList();
            if (hosts.Count > 0)
                return new Check("accessibility", StatusOk,
                    $"Linux has no Accessibility permission to grant; keystrokes reach sessions running in {string.Join(" or ", hosts)} panes.");
            return new Check(
                Name: "accessibility",
                Status: StatusWarn,
                Message: "Neither tmux nor WezTerm is installed, so answer_dialog cannot press keys in any session.",
                Remedy: "Install tmux and run your Claude Code sessions inside it (or use WezTerm).");
        }
        if (Py.IsServiceSession)
        {
            return new Check(
                Name: "accessibility",
                Status: StatusWarn,
                Message: $"Cannot check keystroke access: {Py.NoDesktopReason}");
        }

        IntPtr desk = IntPtr.Zero;
        try
        {
            desk = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desk == IntPtr.Zero)
            {
                return new Check(
                    Name: "accessibility",
                    Status: StatusWarn,
                    Message: "Could not determine keystroke access: the interactive input desktop " +
                             $"is not reachable right now (Win32 error {Marshal.GetLastWin32Error()}; " +
                             "locked workstation or a secure desktop such as a UAC prompt).");
            }
        }
        finally
        {
            if (desk != IntPtr.Zero) CloseDesktop(desk);
        }

        bool elevated;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { elevated = false; }

        var msg = "Windows has no Accessibility permission to grant; keystrokes can reach the desktop." +
                  (elevated
                      ? " JARVIS is running elevated, so elevated windows accept them too."
                      : " JARVIS is not elevated, so windows running as administrator will ignore them (UIPI).");
        return new Check("accessibility", StatusOk, msg);
    }

    /// <summary>
    /// Whether JARVIS may see the screen at all -- asked, never demonstrated.
    /// On macOS this is the Screen Recording permission. Windows has no such
    /// permission: any process in the interactive session may capture the desktop,
    /// and a service in session 0 can capture nothing. NEVER captures anything to
    /// find out -- a screenshot the user did not ask for, at every boot, is
    /// precisely what this capability must not do.
    /// </summary>
    public static Check CheckScreenRecordingSync()
    {
        if (Py.IsServiceSession)
        {
            return new Check(
                Name: "screen_recording", Status: StatusWarn,
                Message: $"Screen capture is unavailable: {Py.NoDesktopReason}");
        }
        if (!Py.IsWindows)
        {
            var tool = Screen.LinuxShotToolName();
            if (tool is not null)
                return new Check("screen_recording", StatusOk,
                    $"Linux needs no Screen Recording permission; JARVIS will capture the desktop with {tool}.");
            return new Check(
                Name: "screen_recording",
                Status: StatusWarn,
                Message: "No screenshot tool was found; look_at_screen will refuse.",
                Remedy: Py.IsWayland
                    ? "Install grim (sway/Hyprland), gnome-screenshot (GNOME) or spectacle (KDE)."
                    : "Install maim, scrot or ImageMagick (import).");
        }
        bool? granted;
        try
        {
            granted = Screen.ScreenRecordingGranted();
        }
        catch (Exception e)
        {
            return new Check("screen_recording", StatusWarn, $"Could not determine Screen Recording status: {e.Message}");
        }

        if (granted == true)
            return new Check("screen_recording", StatusOk,
                "Windows needs no Screen Recording permission; JARVIS can capture the desktop.");
        if (granted is null)
            return new Check("screen_recording", StatusWarn,
                "Could not determine Screen Recording status: the desktop could not be asked.");
        return new Check(
            Name: "screen_recording",
            Status: StatusFail,
            Message: "JARVIS has not been granted Screen Recording; look_at_screen will refuse.",
            Remedy: "Run JARVIS in your logged-in desktop session rather than as a service.");
    }

    /// <summary>
    /// Windows-only: running as a service (session 0) means there is no desktop to
    /// reach. Opening windows, screenshots, keystrokes and toasts all refuse.
    /// Reported as a warning: everything else (voice, Claude Code, the dashboard) works.
    /// </summary>
    public static Check CheckDesktopSessionSync()
    {
        if (!Py.IsServiceSession)
            return new Check("desktop_session", StatusOk, "Running in an interactive desktop session.");
        if (!Py.IsWindows)
            return new Check(
                Name: "desktop_session",
                Status: StatusWarn,
                Message: "Running without a desktop session (no DISPLAY, and no Wayland compositor of this user's): opening windows, " +
                         "screenshots, the window list and notifications are unavailable. Keystrokes into tmux " +
                         "sessions still work.",
                Remedy: "Run Jplus as a `systemctl --user` service started from your graphical login " +
                        "(see README: import-environment DISPLAY WAYLAND_DISPLAY), or from a terminal on your desktop.");
        return new Check(
            Name: "desktop_session",
            Status: StatusWarn,
            Message: "Running as a Windows service (session 0): desktop features -- opening windows, " +
                     "screenshots, keystrokes and notifications -- are unavailable.",
            Remedy: "Run Jplus.exe from a console in your logged-in session if you need JARVIS " +
                    "to see or act on your desktop.");
    }

    /// <summary>FISH_API_KEY must be set or JARVIS has no voice.</summary>
    public static Check CheckFishApiKeySync()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FISH_API_KEY")))
            return new Check("fish_api_key", StatusOk, "FISH_API_KEY is set.");
        return new Check(
            Name: "fish_api_key",
            Status: StatusFail,
            Message: "FISH_API_KEY is not set.",
            Remedy: "Get a Fish Audio API key from fish.audio and set FISH_API_KEY in .env.");
    }

    /// <summary>
    /// A leftover ANTHROPIC_* var signals a misconfigured .env. The brain already
    /// scrubs these from its child process, so it can no longer silently bill an
    /// API key -- but its presence means the rest of the setup may be off too.
    /// </summary>
    public static Check CheckAnthropicKeyLeftoverSync()
    {
        var leftover = Environment.GetEnvironmentVariables().Keys.Cast<object>()
            .Select(k => k.ToString() ?? "")
            .Where(k => k.StartsWith("ANTHROPIC_", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (leftover.Count == 0)
        {
            return new Check(
                Name: "anthropic_key_leftover",
                Status: StatusOk,
                Message: "No leftover ANTHROPIC_* variables in the environment.");
        }
        return new Check(
            Name: "anthropic_key_leftover",
            Status: StatusWarn,
            Message: $"{string.Join(", ", leftover)} set in the environment. brain.py scrubs these " +
                     "from the brain's child process, but this signals a misconfigured .env.",
            Remedy: "Remove ANTHROPIC_* variables from .env -- JARVIS's voice brain runs " +
                    "on your Claude subscription, not an API key.");
    }

    /// <summary>
    /// Where the CLI's user settings.json lives. Honours CLAUDE_CONFIG_DIR so this
    /// reports on the file `claude` will actually read; falls back to ~/.claude.
    /// </summary>
    public static string SettingsPath()
    {
        var root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrEmpty(root)) root = "~/.claude";
        return Path.Combine(Path.GetFullPath(Py.ExpandUser(root)), "settings.json");
    }

    /// <summary>
    /// Report -- never change -- what settings.json says about crossSessionInbound.
    /// The fix is `"crossSessionInbound": "accept"`, but this function must never
    /// write it: JARVIS offers and the user agrees, never a silent write.
    /// </summary>
    public static Check CheckCrossSessionInboundSync()
    {
        var path = SettingsPath();
        if (!File.Exists(path))
        {
            return new Check(
                Name: "cross_session_inbound",
                Status: StatusWarn,
                Message: $"No settings file at {path}; crossSessionInbound is unset, so " +
                         "steers into other sessions will be held for approval or dropped.",
                Remedy: "Offer to add \"crossSessionInbound\": \"accept\" to that settings.json " +
                        "-- only if the user agrees.");
        }

        JsonNode? data;
        try
        {
            data = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new Check(
                Name: "cross_session_inbound",
                Status: StatusWarn,
                Message: $"Could not read/parse {path}: {e.Message}",
                Remedy: $"Check that {path} is valid JSON.");
        }

        if (data is not JsonObject obj)
        {
            return new Check(
                Name: "cross_session_inbound",
                Status: StatusWarn,
                Message: $"{path} did not contain a JSON object.",
                Remedy: $"Check that {path} is valid JSON.");
        }

        var value = obj["crossSessionInbound"];
        if (Py.Str(obj, "crossSessionInbound") == "accept")
        {
            return new Check(
                Name: "cross_session_inbound",
                Status: StatusOk,
                Message: $"crossSessionInbound is \"accept\" in {path}.");
        }

        var detail = value is null
            ? $"crossSessionInbound is not set in {path}"
            : $"crossSessionInbound is {PyRepr(value)} in {path}, not \"accept\"";

        return new Check(
            Name: "cross_session_inbound",
            Status: StatusWarn,
            Message: $"{detail}; steers into other sessions will be held for approval or dropped.",
            Remedy: "Offer to set \"crossSessionInbound\": \"accept\" in that settings.json " +
                    "-- only if the user agrees; never write it silently.");
    }

    /// <summary>
    /// The settings object, or null if it is missing or not readable JSON. Null is
    /// deliberately NOT the same as `{}`: a caller about to write must be able to
    /// tell "no file" from "a file I could not parse", because overwriting the
    /// second would destroy the user's hooks, plugins and status line.
    /// </summary>
    public static JsonObject? ReadSettings(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True only when settings.json says `"crossSessionInbound": "accept"`. Cheap
    /// enough to call on the steer path. Anything else is false, because in every
    /// one of those cases the message really will be held for the user to approve.
    /// </summary>
    public static bool CrossSessionInboundAccepted()
    {
        var path = SettingsPath();
        if (!File.Exists(path)) return false;
        var data = ReadSettings(path);
        return data is not null && data.Count > 0 && Py.Str(data, "crossSessionInbound") == "accept";
    }

    /// <summary>
    /// Write `"crossSessionInbound": "accept"`, preserving everything else. Called
    /// ONLY after the user has said yes out loud. Read-modify-write on the parsed
    /// object; a file that exists but does not parse is REFUSED rather than replaced.
    /// </summary>
    public static (bool ok, string detail) EnableCrossSessionInbound()
    {
        var path = SettingsPath();
        JsonObject data;
        if (File.Exists(path))
        {
            var read = ReadSettings(path);
            if (read is null) return (false, $"{path} isn't readable JSON; I won't rewrite it.");
            data = read;
        }
        else
        {
            data = new JsonObject();
        }
        if (Py.Str(data, "crossSessionInbound") == "accept") return (true, "already set");

        data["crossSessionInbound"] = "accept";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written whole, then moved into place, so an interrupted write can
            // never leave a truncated settings.json behind.
            var tmp = path + ".jarvis-tmp";
            File.WriteAllText(tmp, data.ToJsonString(Py.JsonIndented) + "\n", Py.Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"could not write {path}: {e.Message}");
        }
        return (true, path);
    }

    // ── running them all ─────────────────────────────────────────────────────

    public static readonly (string Name, Func<double, Task<Check>> Fn)[] AsyncChecks =
    [
        ("_check_claude_cli", t => CheckClaudeCli(t)),
        ("_check_claude_login", t => CheckClaudeLogin(t)),
        ("_check_accessibility", t => CheckAccessibility(t)),
    ];

    public static readonly (string Name, Func<Check> Fn)[] SyncChecks =
    [
        ("_check_fish_api_key_sync", CheckFishApiKeySync),
        ("_check_anthropic_key_leftover_sync", CheckAnthropicKeyLeftoverSync),
        ("_check_cross_session_inbound_sync", CheckCrossSessionInboundSync),
        ("_check_screen_recording_sync", CheckScreenRecordingSync),
        ("_check_desktop_session_sync", CheckDesktopSessionSync),
    ];

    /// <summary>
    /// Run one check, bounded by `timeout`, and never let it throw or hang. A check
    /// that errors internally, or simply runs long, becomes a `warn` Check.
    /// </summary>
    public static async Task<Check> RunOne(string name, Func<Task<Check>> start, double timeout)
    {
        try
        {
            // A little slack over the inner subprocess timeout so a check that
            // honours its own `timeout` reports its own message instead of being
            // pre-empted by this outer guard.
            return await Task.Run(start).WaitAsync(TimeSpan.FromSeconds(timeout + 1.0));
        }
        catch (TimeoutException)
        {
            return new Check(name, StatusWarn, $"Check '{name}' timed out.");
        }
        catch (Exception e)   // belt and suspenders: this must never throw into the caller
        {
            Log.LogWarning("preflight: check '{Name}' raised: {Error}", name, e.Message);
            return new Check(name, StatusWarn, $"Check '{name}' raised: {e.Message}");
        }
    }

    /// <summary>
    /// Run every environment check concurrently, each individually time-boxed.
    /// Never throws. Safe to call at startup.
    /// </summary>
    public static async Task<List<Check>> RunChecks(double timeout = DefaultCheckTimeout)
    {
        var tasks = new List<Task<Check>>();
        foreach (var (name, fn) in AsyncChecks) tasks.Add(RunOne(name, () => fn(timeout), timeout));
        foreach (var (name, fn) in SyncChecks) tasks.Add(RunOne(name, () => Task.FromResult(fn()), timeout));
        return (await Task.WhenAll(tasks)).ToList();
    }

    // ── spoken summary ───────────────────────────────────────────────────────

    /// <summary>
    /// Short, voice-friendly phrases keyed by check name -- deliberately not the
    /// full `Message` (written for logs/UI), picked by a substring of the message
    /// so the phrase still fits the specific failure.
    /// </summary>
    public static string PhraseFor(Check check)
    {
        var (name, msg) = (check.Name, check.Message);
        switch (name)
        {
            case "claude_cli":
                if (msg.Contains("not on PATH")) return "Claude Code isn't installed";
                if (msg.Contains("older than")) return "Claude Code needs updating";
                return "Claude Code's version couldn't be checked";
            case "claude_login":
                if (msg.Contains("not logged in")) return "Claude Code isn't logged in";
                return "Claude Code's login couldn't be checked";
            case "accessibility":
                if (msg.Contains("not granted Accessibility")) return "I don't have Accessibility permission";
                return "Accessibility couldn't be checked";
            case "screen_recording":
                if (msg.Contains("not been granted")) return "I don't have Screen Recording permission";
                return "Screen Recording couldn't be checked";
            case "desktop_session":
                return "I'm running as a service, so I can't reach your desktop";
            case "fish_api_key":
                return "I have no Fish Audio key";
            case "anthropic_key_leftover":
                return "there's a leftover Anthropic API key in the environment";
            case "cross_session_inbound":
                return "cross-session steering isn't enabled";
        }
        return check.Message;
    }

    /// <summary>
    /// One or two spoken sentences naming what's wrong, or "" when all is well.
    /// Silence is the correct report for a healthy system.
    /// </summary>
    public static string SpokenSummary(IEnumerable<Check> checks)
    {
        var issues = checks.Where(c => c.Status != StatusOk).ToList();
        if (issues.Count == 0) return "";

        var phrases = issues.Select(PhraseFor).ToList();

        if (phrases.Count == 1) return $"One thing needs attention, sir: {phrases[0]}.";
        if (phrases.Count == 2) return $"Two things need attention, sir: {phrases[0]}, and {phrases[1]}.";
        return $"{phrases.Count} things need attention, sir: "
               + string.Join(", ", phrases.Take(phrases.Count - 1))
               + $", and {phrases[^1]}.";
    }
}
