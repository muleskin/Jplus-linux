using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jplus;

/// <summary>
/// The small runtime the Python original took for granted: time as epoch
/// seconds, environment lookups, `~` expansion, `shutil.which`, an async
/// subprocess with a timeout, JSON options, logging and embedded resources.
/// Every ported module uses these instead of rolling its own.
/// </summary>
public static class Py
{
    // ── time ────────────────────────────────────────────────────────────────

    /// <summary>`time.time()` — seconds since the Unix epoch, as a double.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>`time.monotonic()`.</summary>
    public static double Monotonic() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static DateTimeOffset FromEpoch(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));

    public static double ToEpoch(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0;

    // ── environment ─────────────────────────────────────────────────────────

    /// <summary>`os.getenv(name, default)`.</summary>
    public static string? Getenv(string name, string? fallback = null)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v ?? fallback;
    }

    /// <summary>The usual "1/true/yes/on" reading of an env flag.</summary>
    public static bool EnvFlag(string name, bool fallback = false)
    {
        var v = Getenv(name);
        if (v is null) return fallback;
        return Truthy(v);
    }

    public static bool Truthy(string? v) =>
        v is not null && v.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>`Path(p).expanduser()`.</summary>
    public static string ExpandUser(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path == "~") return Home;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) return Path.Combine(Home, path[2..]);
        return path;
    }

    /// <summary>Directory the EXE lives in — the stand-in for `Path(__file__).parent`.</summary>
    public static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>The EXE itself (used to spawn `Jplus mcp` and to restart).</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppDir, IsWindows ? "Jplus.exe" : "Jplus");

    /// <summary>
    /// True when there is no desktop to reach. Windows: a service in session 0.
    /// Linux: neither an X11 display in our environment nor a Wayland
    /// compositor of our own (see WaylandDisplay) — a systemd system service,
    /// a headless box, nobody logged in to the desktop. Re-evaluated on every
    /// call on Linux: a user service can start before the desktop does.
    /// </summary>
    public static bool IsServiceSession => IsWindows
        ? _isWindowsService.Value
        : string.IsNullOrEmpty(Getenv("DISPLAY")) && WaylandDisplay() is null;

    private static readonly Lazy<bool> _isWindowsService = new(() => Process.GetCurrentProcess().SessionId == 0);

    /// <summary>Refusal text for desktop-only features when running as a service.</summary>
    public static readonly string NoDesktopReason = IsWindows
        ? "I am running as a Windows service, so I have no desktop to reach from here."
        : "I am running without a desktop session (no X11 or Wayland display), so I have no desktop to reach from here.";

    /// <summary>True on a Wayland session (X11-only tools such as xdotool cannot see its windows).</summary>
    public static bool IsWayland => !IsWindows && WaylandDisplay() is not null;

    private static readonly object WaylandGate = new();
    private static double _waylandCheckedAt = double.NegativeInfinity;
    private static string? _waylandFound;
    private static string? _waylandExported;   // the value WE put in WAYLAND_DISPLAY, if any

    /// <summary>
    /// The Wayland display to talk to, or null. WAYLAND_DISPLAY when set;
    /// otherwise this user's own compositor socket, found in the runtime dir.
    ///
    /// Started over SSH, or as a `systemctl --user` service before the desktop
    /// exported its environment, JARVIS has no WAYLAND_DISPLAY although the
    /// owner's desktop is right there — and then grim, the browser, the
    /// terminal and notify-send all fail. So the socket is looked up directly:
    /// `$XDG_RUNTIME_DIR` (else `/run/user/&lt;euid&gt;`) must be a directory we own,
    /// closed to "other" (and to any group but our own), and `wayland-N` a
    /// socket we own — not a symlink, not a plain file. Only ever
    /// our own session, never another user's. A found display is exported to
    /// our environment (WAYLAND_DISPLAY, and XDG_RUNTIME_DIR if unset) so every
    /// child inherits it; if that socket later disappears (logout) the export
    /// is withdrawn and the lookup runs again.
    /// </summary>
    public static string? WaylandDisplay()
    {
        if (IsWindows) return null;
        var env = Getenv("WAYLAND_DISPLAY");
        lock (WaylandGate)
        {
            if (!string.IsNullOrEmpty(env))
            {
                // A value someone else set is theirs to trust. Our own export is
                // withdrawn when the compositor it pointed at has gone.
                if (env != _waylandExported || OwnWaylandSocket(RuntimeDir(), env)) return env;
                Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
                _waylandExported = null;
                _waylandCheckedAt = double.NegativeInfinity;
            }
            var now = Monotonic();
            if (now - _waylandCheckedAt < 5) return _waylandFound;
            _waylandCheckedAt = now;
            _waylandFound = FindOwnWaylandSocket();
            if (_waylandFound is not null)
            {
                Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", _waylandFound);
                if (string.IsNullOrEmpty(Getenv("XDG_RUNTIME_DIR")))
                    Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", RuntimeDir());
                _waylandExported = _waylandFound;
                Log("jarvis").LogInformation("no WAYLAND_DISPLAY in the environment; using this user's compositor socket {Display}",
                    Path.Combine(RuntimeDir(), _waylandFound));
            }
            return _waylandFound;
        }
    }

    private static string RuntimeDir() =>
        Getenv("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : $"/run/user/{Posix.Geteuid()}";

    private static readonly System.Text.RegularExpressions.Regex WaylandSocketName =
        new(@"^wayland-[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? FindOwnWaylandSocket()
    {
        try
        {
            var dir = RuntimeDir();
            var me = Posix.Geteuid();
            // Ours, nothing for "other", and group access only for our own primary
            // group: systemd makes this 0700, Raspberry Pi OS 0770 jedd:jedd (+ACL).
            if (Posix.LStat(dir) is not { IsDir: true } d || d.Uid != me || (d.Mode & 0x7) != 0) return null;
            if ((d.Mode & 0x38) != 0 && d.Gid != Posix.Getegid()) return null;
            return Directory.EnumerateFileSystemEntries(dir, "wayland-*")
                .Select(Path.GetFileName)
                .Where(n => n is not null && WaylandSocketName.IsMatch(n))
                .OrderBy(n => int.Parse(n!.AsSpan(8), System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault(n => OwnWaylandSocket(dir, n!));
        }
        catch { return null; }
    }

    private static bool OwnWaylandSocket(string dir, string name) =>
        WaylandSocketName.IsMatch(name) && Posix.LStat(Path.Combine(dir, name)) is { IsSocket: true } s && s.Uid == Posix.Geteuid();

    /// <summary>
    /// How file paths compare: case-insensitively on Windows, exactly on Linux,
    /// where `Proj` and `proj` are two different directories (a containment
    /// check that ignored case there could be walked around).
    /// </summary>
    public static StringComparison PathComparison =>
        IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // ── processes ───────────────────────────────────────────────────────────

    /// <summary>`shutil.which` — PATH (+ PATHEXT on Windows) search. Absolute paths are checked as-is.</summary>
    public static string? Which(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!IsWindows)
        {
            // POSIX: no extensions; a hit must be an executable regular file.
            if (name.Contains('/')) return IsExecutableFile(name) ? Path.GetFullPath(name) : null;
            foreach (var dir in (Getenv("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir, name);
                if (IsExecutableFile(p)) return p;
            }
            return null;
        }
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = (Getenv("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasExt = Path.HasExtension(name);
        foreach (var dir in (Getenv("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string d;
            try { d = dir.Trim('"'); } catch { continue; }
            if (hasExt)
            {
                var p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            foreach (var ext in exts)
            {
                var p = Path.Combine(d, name + ext.ToLowerInvariant());
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>A regular file with an execute bit (POSIX), or just an existing file (Windows).</summary>
    public static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (IsWindows) return true;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    public sealed record ProcResult(int ReturnCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// `asyncio.create_subprocess_exec(...)` + `communicate()` with an optional
    /// timeout. On timeout the process tree is killed and TimedOut is true
    /// (ReturnCode -1). A missing executable throws FileNotFoundException.
    /// </summary>
    public static async Task<ProcResult> Run(string exe, IEnumerable<string> args, double? timeoutSeconds = null,
        string? cwd = null, IDictionary<string, string?>? env = null, string? stdin = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd is not null) psi.WorkingDirectory = cwd;
        if (env is not null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in env) if (v is not null) psi.Environment[k] = v;
        }
        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new FileNotFoundException(exe);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new FileNotFoundException($"{exe}: {e.Message}", exe, e);
        }
        using (proc)
        {
            if (stdin is not null)
            {
                await proc.StandardInput.WriteAsync(stdin);
                proc.StandardInput.Close();
            }
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutSeconds is double t) cts.CancelAfter(TimeSpan.FromSeconds(t));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return new ProcResult(-1, "", "", true);
            }
            return new ProcResult(proc.ExitCode, await outTask, await errTask, false);
        }
    }

    /// <summary>`os.kill(pid, 0)` — is a process with this id alive?</summary>
    public static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>Open a URL or file with the shell's default handler (`open` on macOS, `xdg-open` on Linux).</summary>
    public static bool ShellOpen(string target)
    {
        try
        {
            if (!IsWindows)
            {
                // Explicit xdg-open (argv, no shell) rather than .NET's
                // UseShellExecute guesswork; detached so it outlives nothing of ours.
                var opener = Which("xdg-open") ?? Which("gio");
                if (opener is null) return false;
                var psi = new ProcessStartInfo(opener) { UseShellExecute = false };
                if (Path.GetFileName(opener) == "gio") psi.ArgumentList.Add("open");
                psi.ArgumentList.Add(target);
                Process.Start(psi)?.Dispose();
                return true;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch { return false; }
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wire options: classes serialize with snake_case property names (a
    /// Python dataclass field `session_id` is C# `SessionId`); dictionary keys
    /// are written exactly as given. Matches the Python API's JSON.
    /// </summary>
    public static readonly JsonSerializerOptions Json = MakeJson(false);
    public static readonly JsonSerializerOptions JsonIndented = MakeJson(true);

    private static JsonSerializerOptions MakeJson(bool indent) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = indent,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>`json.dumps(obj)`.</summary>
    public static string Dumps(object? value, bool indent = false) =>
        JsonSerializer.Serialize(value, indent ? JsonIndented : Json);

    /// <summary>`json.loads(text)` as a node tree; throws JsonException on bad input.</summary>
    public static JsonNode? Loads(string text) => JsonNode.Parse(text);

    /// <summary>`json.loads` that returns null instead of throwing.</summary>
    public static JsonNode? TryLoads(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    /// <summary>A JsonNode's string value, or null when absent / not a string.</summary>
    public static string? Str(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>A JsonNode's numeric value as double, or null.</summary>
    public static double? Num(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<int>(out var i)) return i;
        }
        return null;
    }

    /// <summary>Convert a JsonNode tree to plain CLR values (Dictionary / List / string / double / long / bool / null).</summary>
    public static object? ToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => ToClr(kv.Value)),
        JsonArray a => a.Select(ToClr).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToString(),
    };

    // ── logging ─────────────────────────────────────────────────────────────

    public static ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>`logging.getLogger(name)`.</summary>
    public static ILogger Log(string name) => new LazyLogger(name);

    private sealed class LazyLogger(string name) : ILogger
    {
        private ILogger Inner => LoggerFactory.CreateLogger(name);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
        public bool IsEnabled(LogLevel level) => Inner.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Inner.Log(level, id, state, ex, fmt);
    }

    // ── embedded resources ──────────────────────────────────────────────────

    /// <summary>Everything embedded in the EXE: `frontend/dist/**` and `Resources/**`.</summary>
    public static readonly IFileProvider Embedded =
        new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly());

    /// <summary>Read an embedded text resource, e.g. "Resources/jarvis_home/CLAUDE.md". Null if absent.</summary>
    public static string? ReadResource(string path)
    {
        var f = Embedded.GetFileInfo(path);
        if (!f.Exists) return null;
        using var s = f.CreateReadStream();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>
    /// Materialise an embedded resource as a real file under
    /// `&lt;data_dir&gt;/.templates/` (rewritten when it differs) and return
    /// the path — for code that the Python original pointed at a file beside
    /// the source.
    /// </summary>
    public static string ResourceFile(string resourcePath, string dataDir)
    {
        var text = ReadResource(resourcePath) ?? "";
        var dest = Path.Combine(dataDir, ".templates", resourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest) || File.ReadAllText(dest) != text) File.WriteAllText(dest, text);
        return dest;
    }

    // ── misc ────────────────────────────────────────────────────────────────

    public static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Atomic text write (temp file + replace), like the Python tempfile + os.replace dance.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, text, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>`asyncio.create_task(coro)` fire-and-forget with exceptions logged, not lost.</summary>
    public static Task Spawn(Func<Task> work, string what = "background task")
    {
        var t = Task.Run(work);
        t.ContinueWith(x => Log("jarvis").LogError(x.Exception, "{What} failed", what),
            TaskContinuationOptions.OnlyOnFaulted);
        return t;
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>`shlex.quote(s)`: safe as one POSIX shell word.</summary>
    public static string ShQuote(string s)
    {
        if (s.Length == 0) return "''";
        if (s.All(c => char.IsAsciiLetterOrDigit(c) || "@%+=:,./-_".Contains(c))) return s;
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }
}
