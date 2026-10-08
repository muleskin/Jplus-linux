// server.py, lines 1–2620: boot .env loader, config, weather, project scan,
// speech text helpers, the voice/session client queues, brain + speech
// lifecycle and context rotation, connections + mcp.json, event
// announcements, preflight, the one-line-per-turn guard, _handle_utterance,
// usage log helpers, the lifespan, the REST routes in that range, the tool
// dispatch table and the /internal/tool endpoint.
//
// ── Conventions shared with Server.Part2.cs / Server.Part3.cs ───────────────
//
// Tool handlers. Every entry of `ToolHandlers` is a `ToolHandler`
// (= Func<JsonObject, Task<object?>>). The handlers themselves keep the
// async-ness of the Python, with these exact shapes:
//
//   sync Python tool   → public static string ToolX(JsonObject args)
//   async Python tool  → public static async Task<string> ToolX(JsonObject args)
//   tool_look_at_page / tool_look_at_screen (Python returns str OR ToolImage)
//                      → public static async Task<object> ToolX(JsonObject args)
//                        returning either a string or a `Server.ToolImage`.
//
// The WHOLE dispatch table and the WHOLE ACTING_TOOLS set live in this file
// (Python grew them with `.update()` calls spread through the module; C#
// static initialisation across partial files has no defined order, so they are
// written out once, here). Parts 2 and 3 must NOT register tools or add to
// `ActingTools` — and must not declare a static constructor for Server.
//
// Globals from this range that other parts use (Python name → C#):
//   log → Log                     brain_instance → BrainInstance
//   speech → SpeechInstance (NOT `Speech`: that is the speech module's class)
//   session_watcher → SessionWatcher         cached_projects → CachedProjects
//   _spawn(coro) → Spawn(() => Coro())        _enqueue → Enqueue(queue, msg)
//   _voice_emit → VoiceEmit                   voice_clients → VoiceClients / HasVoiceClients
//   run_executor_instance → RunExecutorInstance
//   See the report for the full list.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jplus;

public static partial class Server
{
    // ═══════════════════════════════════════════════════════════════════════
    // The .env boot loader
    // ═══════════════════════════════════════════════════════════════════════

    // The ONE definition of what a line of `.env` is. Both readers use it (this
    // boot loader and `ReadEnv`), and so does `EnvValueProblem`, which is what
    // the writer asks before it puts a value on a line. One function rather
    // than three copies because the copies disagreed: the writer forbade
    // "\n", "\r", "\0" while `str.splitlines()` splits on ten characters, so a
    // `\x0b` in a user name smuggled a JARVIS_CLAUDE_PATH line into `.env`.
    // Deriving the writer's rule from the reader's parser closes that shape.

    /// <summary>Every (key, value) a reader of `.env` sees in `text`, in order.</summary>
    public static List<(string Key, string Value)> ParseEnvLines(string text)
    {
        var output = new List<(string, string)>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            {
                int i = line.IndexOf('=');
                var k = line[..i];
                var v = line[(i + 1)..];
                output.Add((k.Trim(), v.Trim().Trim('"').Trim('\'')));
            }
        }
        return output;
    }

    /// <summary>
    /// Python's `str.splitlines()`: splits on \n, \r, \r\n, \v, \f, \x1c, \x1d,
    /// \x1e, \x85, U+2028 and U+2029, with no trailing empty element.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            bool brk = c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e'
                or '\x85' or (char)0x2028 or (char)0x2029;
            if (!brk) { i++; continue; }
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            i++;
            start = i;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>
    /// The module-level loader at the top of server.py: `.env` beside the EXE,
    /// values only filling variables not already set. Program.cs calls this
    /// before anything else reads the environment.
    /// </summary>
    public static void BootLoadEnv()
    {
        // The same file the settings page writes (JARVIS_ENV_FILE, default <exe dir>/.env),
        // or what it saved would be ignored at the next start.
        var envPath = EnvFilePath();
        if (File.Exists(envPath))
        {
            foreach (var (k, v) in ParseEnvLines(File.ReadAllText(envPath)))
            {
                if (k.Length == 0) continue;
                if (Environment.GetEnvironmentVariable(k) is null)
                    Environment.SetEnvironmentVariable(k, v);
            }
        }
        // Python read its config constants at import, i.e. after this loader.
        LoadModuleConfig();
    }

    public static readonly ILogger Log = Py.Log("jarvis");

    // ═══════════════════════════════════════════════════════════════════════
    // Config
    // ═══════════════════════════════════════════════════════════════════════

    public static string FishApiKey = Py.Getenv("FISH_API_KEY", "")!;
    public static string FishVoiceId = Py.Getenv("FISH_VOICE_ID", "612b878b113047d9a770c069c8b4fdfe")!; // JARVIS (MCU)
    public const string FishApiUrl = "https://api.fish.audio/v1/tts";
    public static string UserName = Py.Getenv("USER_NAME", "sir")!;
    public static bool SkipPermissions = ReadSkipPermissions();

    public static string DesktopPath = Path.Combine(Py.Home, "Desktop");

    private static bool ReadSkipPermissions() =>
        (Py.Getenv("JARVIS_SKIP_PERMISSIONS", "true") ?? "true").ToLowerInvariant() is not ("0" or "false" or "no");

    /// <summary>Re-read every import-time constant (after `.env` has been loaded).</summary>
    public static void LoadModuleConfig()
    {
        FishApiKey = Py.Getenv("FISH_API_KEY", "")!;
        FishVoiceId = Py.Getenv("FISH_VOICE_ID", "612b878b113047d9a770c069c8b4fdfe")!;
        UserName = Py.Getenv("USER_NAME", "sir")!;
        SkipPermissions = ReadSkipPermissions();
        DesktopPath = Path.Combine(Py.Home, "Desktop");
        ScanBudgetSeconds = EnvDouble("JARVIS_SCAN_BUDGET", 20);
        ScanCacheSeconds = EnvDouble("JARVIS_SCAN_CACHE", 300);
        MuteMicDuringSpeech = ReadMuteMic();
        DebugDocs = ReadDebugDocs();
    }

    private static double EnvDouble(string name, double fallback)
    {
        var v = Py.Getenv(name);
        if (v is null) return fallback;
        return double.Parse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Weather
    // ═══════════════════════════════════════════════════════════════════════
    // Location is resolved from (in order): WEATHER_LATITUDE + WEATHER_LONGITUDE
    // env vars, a cached IP-geolocation lookup, or a fresh ipwho.is lookup.
    // Temperature unit defaults to Fahrenheit; override with WEATHER_UNIT=celsius.

    public static string? CachedWeather = null;
    public static bool WeatherFetched = false;
    public static Dictionary<string, object?>? CachedWeatherLocation = null;
    public static double WeatherLocationFetchedAt = 0.0;
    public const double WeatherLocationTtlSeconds = 60 * 15;

    /// <summary>One shared client for the few plain HTTP calls server.py makes itself.</summary>
    public static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string FormatLocationLabel(string city, string region, string country)
    {
        var parts = new[] { city, region }
            .Where(p => !string.IsNullOrEmpty(p) && p.Trim().Length > 0)
            .Select(p => p.Trim()).ToList();
        if (parts.Count > 0) return string.Join(", ", parts.Take(2));
        var c = (string.IsNullOrEmpty(country) ? "your area" : country).Trim();
        return c.Length > 0 ? c : "your area";
    }

    private static string HttpGetSync(string url, double timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = SharedHttp.GetAsync(url, cts.Token).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
    }

    /// <summary>Resolve weather location: env override → cached lookup → fresh IP lookup.</summary>
    public static Dictionary<string, object?>? GetWeatherLocation()
    {
        var latRaw = (Py.Getenv("WEATHER_LATITUDE", "") ?? "").Trim();
        var lonRaw = (Py.Getenv("WEATHER_LONGITUDE", "") ?? "").Trim();
        var labelOverride = (Py.Getenv("WEATHER_LOCATION_LABEL", "") ?? "").Trim();
        if (latRaw.Length > 0 && lonRaw.Length > 0)
        {
            if (double.TryParse(latRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(lonRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return new Dictionary<string, object?>
                {
                    ["latitude"] = lat,
                    ["longitude"] = lon,
                    ["label"] = labelOverride.Length > 0 ? labelOverride : "your area",
                };
            }
            Log.LogWarning("Invalid WEATHER_LATITUDE / WEATHER_LONGITUDE in environment");
        }

        if (CachedWeatherLocation is not null
            && (Py.Now() - WeatherLocationFetchedAt) < WeatherLocationTtlSeconds)
            return CachedWeatherLocation;

        try
        {
            var data = Py.Loads(HttpGetSync(
                "https://ipwho.is/?fields=success,city,region,country,latitude,longitude", 3)) as JsonObject;
            if (data is not null && data["success"] is JsonValue sv && sv.TryGetValue<bool>(out var ok) && ok)
            {
                var location = new Dictionary<string, object?>
                {
                    ["latitude"] = ToDouble(Py.ToClr(data["latitude"])) ?? throw new FormatException("latitude"),
                    ["longitude"] = ToDouble(Py.ToClr(data["longitude"])) ?? throw new FormatException("longitude"),
                    ["label"] = labelOverride.Length > 0 ? labelOverride : FormatLocationLabel(
                        PyStr(Py.ToClr(data["city"]) ?? ""),
                        PyStr(Py.ToClr(data["region"]) ?? ""),
                        PyStr(Py.ToClr(data["country"]) ?? "")),
                };
                CachedWeatherLocation = location;
                WeatherLocationFetchedAt = Py.Now();
                return location;
            }
        }
        catch (Exception e)
        {
            Log.LogDebug($"IP-geolocation lookup failed: {e.Message}");
        }
        return CachedWeatherLocation;
    }

    /// <summary>Sync weather fetch — safe to call from a threaded worker.</summary>
    public static string? FetchWeatherStringSync()
    {
        var location = GetWeatherLocation();
        if (location is null) return null;

        var unit = (Py.Getenv("WEATHER_UNIT", "fahrenheit") ?? "fahrenheit").Trim().ToLowerInvariant();
        if (unit is not ("fahrenheit" or "celsius")) unit = "fahrenheit";
        var unitSymbol = unit == "fahrenheit" ? "°F" : "°C";

        try
        {
            var url = "https://api.open-meteo.com/v1/forecast"
                      + $"?latitude={PyStr(location["latitude"])}&longitude={PyStr(location["longitude"])}"
                      + $"&current=temperature_2m,weathercode&temperature_unit={unit}";
            var body = Py.Loads(HttpGetSync(url, 3)) as JsonObject;
            var current = body?["current"] as JsonObject;
            var temp = current?["temperature_2m"];
            if (temp is null) return null;
            // The raw JSON number, as Python's f-string printed the parsed value.
            return $"Current weather in {location["label"]}: {temp.ToJsonString()}{unitSymbol}";
        }
        catch (Exception e)
        {
            Log.LogDebug($"Weather fetch failed: {e.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Project Scanner
    // ═══════════════════════════════════════════════════════════════════════

    // A user's ~/Desktop is not a small directory, and it may not be a fast one:
    // listing is instant but the per-entry stats can block for seconds each (one
    // entry alone blocked ~86s; 375 entries took 206s). So a scan is bounded by
    // a budget (checked between entries — a single pathological entry can still
    // overshoot it) and its answer cached so the dashboard's repeated calls do
    // not start a new one each time.
    public static double ScanBudgetSeconds = EnvDouble("JARVIS_SCAN_BUDGET", 20);
    public static double ScanCacheSeconds = EnvDouble("JARVIS_SCAN_CACHE", 300);

    /// <summary>
    /// Roots are overridable so a user whose Desktop is slow, huge or
    /// cloud-backed has somewhere to point this. Separated like PATH — on
    /// Windows that is `;` (Python used `:`, which would split a drive letter).
    /// </summary>
    public static List<string> ScanRoots()
    {
        var overrideRoots = (Py.Getenv("JARVIS_PROJECT_ROOTS", "") ?? "").Trim();
        if (overrideRoots.Length > 0)
            return overrideRoots.Split(Path.PathSeparator)
                .Where(r => r.Trim().Length > 0)
                .Select(r => Py.ExpandUser(r)).ToList();
        return [DesktopPath, ProjectMaker.ProjectsRoot()];
    }

    private static readonly object ScanCacheGate = new();
    public static double ScanCacheAt = 0.0;
    public static List<Dictionary<string, object?>> ScanCacheValue = [];

    /// <summary>
    /// The filesystem walk. Synchronous — call it on a worker thread.
    /// `deadline` (a Monotonic() value) is honoured between entries because the
    /// caller cannot cancel a worker; stopping ourselves is the only way to stop.
    /// </summary>
    public static (List<Dictionary<string, object?>> Projects, bool Complete) ScanProjectsBlocking(double deadline)
    {
        var projects = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();

        foreach (var root in ScanRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(root)
                    .OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var entry in entries)
                {
                    if (Py.Monotonic() > deadline) return (projects, false);
                    var name = Path.GetFileName(entry);
                    if (!Directory.Exists(entry) || name.StartsWith('.')) continue;
                    if (seen.Contains(entry)) continue;
                    var gitDir = Path.Combine(entry, ".git");
                    if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    {
                        var branch = "unknown";
                        try
                        {
                            var headContent = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                            if (headContent.StartsWith("ref: refs/heads/"))
                                branch = headContent.Replace("ref: refs/heads/", "");
                        }
                        catch { }

                        seen.Add(entry);
                        projects.Add(new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["path"] = entry,
                            ["branch"] = branch,
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }
        return (projects, true);
    }

    /// <summary>
    /// Quick scan for git repos (depth 1) in the places projects live: ~/Desktop
    /// and the projects root `create_project` writes into — without the second, a
    /// project JARVIS just created would vanish from `CachedProjects` on the next
    /// rescan. The walk runs on a worker thread so a slow Desktop cannot block
    /// every endpoint and the voice channel.
    /// </summary>
    public static async Task<List<Dictionary<string, object?>>> ScanProjects()
    {
        var now = Py.Monotonic();
        lock (ScanCacheGate)
        {
            if (ScanCacheValue.Count > 0 && now - ScanCacheAt < ScanCacheSeconds)
                return ScanCacheValue;
        }

        var deadline = now + ScanBudgetSeconds;
        var (projects, complete) = await Task.Run(() => ScanProjectsBlocking(deadline));

        if (complete)
        {
            lock (ScanCacheGate)
            {
                ScanCacheAt = Py.Monotonic();
                ScanCacheValue = projects;
            }
            return projects;
        }

        Log.LogWarning(
            $"project scan hit its {ScanBudgetSeconds:F0}s budget after {projects.Count} projects; serving those. " +
            "Set JARVIS_PROJECT_ROOTS to a faster directory, or raise JARVIS_SCAN_BUDGET.");
        // A partial answer beats none, but do not cache it as though it were the
        // whole picture — the next call should try again.
        if (projects.Count > 0) return projects;
        lock (ScanCacheGate) return ScanCacheValue;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Speech-to-Text Corrections
    // ═══════════════════════════════════════════════════════════════════════

    public static readonly (string Pattern, string Replacement)[] SttCorrections =
    [
        (@"\bcloud code\b", "Claude Code"),
        (@"\bclock code\b", "Claude Code"),
        (@"\bquad code\b", "Claude Code"),
        (@"\bclawed code\b", "Claude Code"),
        (@"\bclod code\b", "Claude Code"),
        (@"\bcloud\b", "Claude"),
        (@"\bquad\b", "Claude"),
        (@"\btravis\b", "JARVIS"),
        (@"\bjarves\b", "JARVIS"),
    ];

    /// <summary>Fix common speech-to-text errors before processing.</summary>
    public static string ApplySpeechCorrections(string text)
    {
        var result = text;
        foreach (var (pattern, replacement) in SttCorrections)
            result = Regex.Replace(result, pattern, replacement.Replace("$", "$$"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Markdown Stripping for TTS
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly string[] BannedPhrases =
    [
        "my apologies", "i apologize", "absolutely", "great question",
        "i'd be happy to", "of course", "how can i help",
        "is there anything else", "i should clarify", "let me know if",
        "feel free to",
    ];

    /// <summary>Strip ALL markdown from text before sending to TTS.</summary>
    public static string StripMarkdownForTts(string text)
    {
        const RegexOptions ci = RegexOptions.CultureInvariant;
        var result = text;
        // Remove code blocks (``` ... ```)
        result = Regex.Replace(result, @"```[\s\S]*?```", "", ci);
        // Remove inline code
        result = result.Replace("`", "");
        // Remove bold/italic markers
        result = result.Replace("**", "").Replace("*", "");
        // Remove headers
        result = Regex.Replace(result, @"^#{1,6}\s*", "", ci | RegexOptions.Multiline);
        // Convert [text](url) to just text
        result = Regex.Replace(result, @"\[([^\]]+)\]\([^\)]+\)", "$1", ci);
        // Remove bullet points
        result = Regex.Replace(result, @"^\s*[-*+]\s+", "", ci | RegexOptions.Multiline);
        // Remove numbered lists
        result = Regex.Replace(result, @"^\s*\d+\.\s+", "", ci | RegexOptions.Multiline);
        // Double newlines to period
        result = Regex.Replace(result, @"\n{2,}", ". ", ci);
        // Single newlines to space
        result = result.Replace("\n", " ");
        // Clean up multiple spaces
        result = Regex.Replace(result, @"\s{2,}", " ", ci);

        // Strip banned phrases
        var resultLower = result.ToLowerInvariant();
        foreach (var phrase in BannedPhrases)
        {
            int idx = resultLower.IndexOf(phrase, StringComparison.Ordinal);
            while (idx != -1)
            {
                // Remove the phrase and any trailing comma/dash
                int end = idx + phrase.Length;
                if (end < result.Length && " ,—-".Contains(result[end])) end += 1;
                result = result[..idx] + result[end..];
                resultLower = result.ToLowerInvariant();
                idx = resultLower.IndexOf(phrase, StringComparison.Ordinal);
            }
        }

        return result.Trim().Trim(',').Trim('—').Trim('-').Trim();
    }

    public const string RunsPromptHeader =
        "What I have running, and what has finished. The project names are mine; " +
        "the prompts and summaries beside them are the words of whoever asked " +
        "for the run:";

    /// <summary>
    /// Active and recent runs, formatted for the system prompt. The project name
    /// is an IDENTIFIER and goes through `RunProject`; the prompt and summary are
    /// PROSE and go inside `WrapUntrusted`, since no scrub makes prose safe in a
    /// system prompt. Nothing calls this today; it survives because it has its
    /// own tests and its name says where to wire it.
    /// </summary>
    public static string FormatRunsForPrompt()
    {
        var active = RunStore.ListRuns(status: RunStore.RunStatus.Active.ToList(), limit: 10);
        var recent = RunStore.ListRuns(status: [RunStore.RunStatus.Succeeded], limit: 3);

        var parts = new List<string>();
        if (active.Count > 0)
        {
            var lines = new List<string>();
            foreach (var r in active)
            {
                var elapsed = (long)(Py.Now() - (ToDouble(r.GetValueOrDefault("created_at")) ?? 0));
                var prompt = r.GetValueOrDefault("prompt") as string ?? "";
                lines.Add($"  - [{PyStr(r.GetValueOrDefault("status"))}] {RunProject(r)} " +
                          $"({elapsed}s ago): {Head(prompt, 80)}");
            }
            parts.Add("CURRENTLY WORKING ON:\n" + string.Join("\n", lines));
        }

        if (recent.Count > 0)
        {
            var lines = new List<string>();
            foreach (var r in recent.Take(2))
            {
                var summary = r.GetValueOrDefault("summary") as string;
                var detail = !string.IsNullOrEmpty(summary) ? Head(summary, 80) : "completed";
                lines.Add($"  - {RunProject(r)}: {detail}");
            }
            parts.Add("RECENTLY COMPLETED:\n" + string.Join("\n", lines));
        }

        if (parts.Count == 0) return "No active or recent runs.";
        return $"{RunsPromptHeader}\n" + WrapUntrusted(RunWrapName, string.Join("\n\n", parts));
    }

    // Smart greeting — track last greeting to avoid re-greeting on reconnect
    public static double LastGreetingTime = 0;

    // ═══════════════════════════════════════════════════════════════════════
    // TTS (Fish Audio)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Generate speech audio from text using Fish Audio TTS.</summary>
    public static async Task<byte[]?> SynthesizeSpeech(string text)
    {
        if (string.IsNullOrEmpty(FishApiKey))
        {
            Log.LogWarning("FISH_API_KEY not set, skipping TTS");
            return null;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Post, FishApiUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FishApiKey}");
            req.Content = new StringContent(Py.Dumps(new Dictionary<string, object?>
            {
                ["text"] = text,
                ["reference_id"] = FishVoiceId,
                ["format"] = "mp3",
            }), Encoding.UTF8, "application/json");
            using var response = await SharedHttp.SendAsync(req, cts.Token);
            if ((int)response.StatusCode == 200)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                CountTtsCall();
                AppendUsageEntry(0, 0, "tts");
                return bytes;
            }
            Log.LogError($"TTS error: {(int)response.StatusCode}");
            return null;
        }
        catch (Exception e)
        {
            Log.LogError($"TTS error: {e.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Brain + speech (milestone 1): one Claude Code process, one mouth
    // ═══════════════════════════════════════════════════════════════════════

    public static bool MuteMicDuringSpeech = ReadMuteMic();

    private static bool ReadMuteMic() =>
        (Py.Getenv("JARVIS_MUTE_MIC_DURING_SPEECH", "false") ?? "false").ToLowerInvariant() is "1" or "true" or "yes";

    public static BrainMod.Brain? BrainInstance = null;
    /// <summary>Python's global `speech` (renamed: `Speech` is the speech module's class).</summary>
    public static Speech.SpeechScheduler? SpeechInstance = null;
    public static SessionWatch.SessionWatcher? SessionWatcher = null;
    public static HttpClient? TtsClient = null;
    public static readonly Dictionary<string, double> BrainNoticeAt = new() { ["restarting"] = 0.0 };
    public static readonly string[] ContentFrames = ["audio", "text"];

    /// <summary>
    /// `_spawn`: fire-and-forget. .NET keeps a strong reference to running tasks
    /// itself, so the Python `_bg_tasks` set has no counterpart; failures are logged.
    /// </summary>
    public static Task Spawn(Func<Task> work, string what = "background task") => Py.Spawn(work, what);

    /// <summary>A content frame had nobody to play it.</summary>
    public class NoVoiceClient(string message) : IOException(message);

    // ── bounded per-client queues ───────────────────────────────────────────

    /// <summary>
    /// A client's outbound queue. Bounded, dropping its OLDEST frame to make
    /// room — the same policy /ws/runs uses: a client that has stopped reading
    /// costs memory bounded by the queue, and loses the stalest frame.
    /// </summary>
    public static Channel<Dictionary<string, object?>> NewClientQueue(int max) =>
        Channel.CreateBounded<Dictionary<string, object?>>(new BoundedChannelOptions(max)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>Put `msg` on a bounded queue, dropping its oldest to make room.</summary>
    public static bool Enqueue(Channel<Dictionary<string, object?>> queue, Dictionary<string, object?> msg) =>
        queue.Writer.TryWrite(msg);

    /// <summary>
    /// One writer per client: the only place a frame is actually sent. A send
    /// that never returns stalls this task and nothing else — it used to stall
    /// the speech scheduler, and therefore every listener.
    /// (.NET also forbids concurrent sends on one WebSocket, so ALL sends to a
    /// registered client must go through its queue.)
    /// </summary>
    public static async Task Pump(WebSocket ws, Channel<Dictionary<string, object?>> queue,
        Action<WebSocket> drop, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var msg = await queue.Reader.ReadAsync(ct);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(Py.Dumps(msg));
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    drop(ws);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    public const int VoiceQueueMax = 1000;              // matching /ws/runs
    private static readonly object VoiceGate = new();
    public static readonly HashSet<WebSocket> VoiceClients = [];
    public static readonly Dictionary<WebSocket, Channel<Dictionary<string, object?>>> VoiceQueues = [];
    public static readonly Dictionary<WebSocket, CancellationTokenSource> VoiceWriters = [];

    /// <summary>`bool(voice_clients)`.</summary>
    public static bool HasVoiceClients { get { lock (VoiceGate) return VoiceClients.Count > 0; } }

    /// <summary>Register a voice client and start its writer.</summary>
    public static Channel<Dictionary<string, object?>> AddVoiceClient(WebSocket ws)
    {
        var queue = NewClientQueue(VoiceQueueMax);
        var cts = new CancellationTokenSource();
        lock (VoiceGate)
        {
            VoiceQueues[ws] = queue;
            VoiceClients.Add(ws);
            VoiceWriters[ws] = cts;
        }
        Spawn(() => Pump(ws, queue, DropVoiceClient, cts.Token), "voice writer");
        return queue;
    }

    public static void DropVoiceClient(WebSocket ws)
    {
        bool none;
        CancellationTokenSource? cts;
        lock (VoiceGate)
        {
            VoiceClients.Remove(ws);
            VoiceQueues.Remove(ws);
            VoiceWriters.Remove(ws, out cts);
            none = VoiceClients.Count == 0;
        }
        try { cts?.Cancel(); } catch { }
        if (none && SpeechInstance is not null)
        {
            // `SpeechScheduler` is process-global and outlives any one tab. With
            // nobody listening there is no speaker for an echo to come from, so
            // everything still unacked is settled now — otherwise the next tab
            // inherits this one's unacked chunks and is heard as echoing them.
            SpeechInstance.TransportGone();
        }
    }

    /// <summary>
    /// Hand one protocol message to every connected voice client. Hand, not
    /// send: the frame goes on each client's own queue. A content frame (audio
    /// or its text fallback) that reaches NO queue still throws, so the
    /// scheduler abandons that utterance instead of waiting out its ack
    /// timeout. Status frames with nobody listening are simply lost.
    /// </summary>
    public static async Task VoiceEmit(Dictionary<string, object?> msg)
    {
        int delivered = 0;
        List<WebSocket> clients;
        lock (VoiceGate) clients = VoiceClients.ToList();
        foreach (var ws in clients)
        {
            Channel<Dictionary<string, object?>>? queue;
            lock (VoiceGate) VoiceQueues.TryGetValue(ws, out queue);
            if (queue is null)            // never registered, or already dropped
            {
                DropVoiceClient(ws);
                continue;
            }
            if (Enqueue(queue, msg)) delivered++;
        }
        if (delivered == 0 && msg.GetValueOrDefault("type") is string t && ContentFrames.Contains(t))
            throw new NoVoiceClient("no voice client connected");
    }

    public static async Task<byte[]?> SynthForSpeech(string text)
    {
        var r = await Tts.SynthesizeChunk(text, apiKey: FishApiKey, voiceId: FishVoiceId, client: TtsClient);
        if (r is null) return null;
        CountTtsCall();
        AppendUsageEntry(0, 0, "tts");
        Log.LogDebug($"tts: {text.Length} chars, first byte {r.FirstByteSec:F2}s, total {r.TotalSec:F2}s");
        return r.Audio;
    }

    /// <summary>
    /// A spoken reset time that names the day when it is not today: "10 AM"
    /// today, "tomorrow at 10 AM", "Monday at 10 AM" within the week,
    /// "Monday 8 September at 10 AM" beyond it.
    /// </summary>
    public static string FmtReset(object? ts)
    {
        DateTime when;
        try
        {
            var d = ToDouble(ts) ?? throw new FormatException();
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new OverflowException();
            when = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(d * 1000))).LocalDateTime;
        }
        catch (Exception)
        {
            return "later";
        }
        var inv = CultureInfo.InvariantCulture;
        var clock = when.ToString("h:mm tt", inv).Replace(":00 ", " ");   // "10:00 AM" -> "10 AM"
        var days = (when.Date - DateTime.Now.Date).Days;
        if (days <= 0) return clock;
        if (days == 1) return $"tomorrow at {clock}";
        if (days < 7) return $"{when.ToString("dddd", inv)} at {clock}";
        return $"{when.ToString("dddd d MMMM", inv)} at {clock}";
    }

    // True but useless: "down" names neither cause nor remedy. When the brain's
    // failure is classified "auth", speak something the user can act on. Shared
    // by OnBrainState and HandleUtterance so the two auth lines never drift apart.
    public const string AuthBrainDownLine = "Claude Code's login has expired, sir — run `claude` in a " +
                                            "terminal and log in, then restart me.";
    public const string AuthRemedyLogLine = "brain: giving up — Claude Code's OAuth login has expired. " +
                                            "Remedy: run `claude` in a terminal and log in, then restart JARVIS.";

    // "Say that again" before JARVIS has said anything this session: there is
    // nothing held to replay. Said, not silently ignored — the user asked.
    public const string NothingToReplayLine = "I'm afraid I've nothing to repeat yet, sir.";

    // Shown (never spoken) while a context rotation is in progress. The swap
    // takes seconds during which JARVIS answers nothing, and unexplained silence
    // reads as a crash. A banner and an orb state for the duration; nothing said.
    public const string RotationBusyLine = "Gathering my thoughts — one moment, sir.";

    // Said by the user. A memory writer is refused while anything foreign sits in
    // the generation that would compose it, and the refusal tells him to say it
    // again "in a fresh conversation" — this is that fresh conversation. It
    // discards the tainted generation rather than carrying anything across.
    public static readonly string[] FreshStartPhrases =
    [
        "start fresh", "start a fresh conversation", "fresh conversation",
        "start over", "clear your head", "clear your mind", "clear your context",
        "new conversation", "forget this conversation", "wipe your memory of this",
    ];
    public const string FreshStartLine = "Cleared, sir — nothing of that conversation left. Go ahead.";

    /// <summary>Whether the user just asked for a clean generation.</summary>
    public static bool IsFreshStart(string text)
    {
        var t = string.Join(" ", ActionWords(text));
        return FreshStartPhrases.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    public static List<string> ActionWords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value).ToList();

    public static async Task OnBrainState(string state, Dictionary<string, object?> info)
    {
        var reason = DGet(info, "failure_reason") as string;
        if (state == "failed" && reason == "auth")
        {
            // At ERROR level, visible in the terminal the user is already
            // looking at, regardless of whether speech itself is available.
            Log.LogError(AuthRemedyLogLine);
        }
        var speech = SpeechInstance;
        if (speech is null) return;
        if (state == "restarting")
        {
            var now = Py.Now();
            bool sayIt;
            lock (BrainNoticeAt)
            {
                sayIt = now - BrainNoticeAt["restarting"] > 60;
                if (sayIt) BrainNoticeAt["restarting"] = now;
            }
            if (sayIt)
                await speech.Say("Rebooting my language systems, one moment.", Speech.Priority.Normal);
        }
        else if (state == "failed")
        {
            var line = reason == "auth"
                ? AuthBrainDownLine
                : "My language systems are down, sir. Check the server log.";
            await speech.Say(line, Speech.Priority.Urgent, immediate: true);
        }
        else if (state == "rate_limited")
        {
            await speech.Say($"I've hit the usage limit until {FmtReset(DGet(info, "resets_at"))}, sir.",
                Speech.Priority.Normal);
        }
    }

    public static string Greeting()
    {
        var hour = DateTime.Now.Hour;
        if (hour < 12) return "Good morning, sir.";
        if (hour < 17) return "Good afternoon, sir.";
        return "Good evening, sir.";
    }

    /// <summary>
    /// Map the server's bind host to a host the MCP child can actually dial.
    /// `0.0.0.0` and `::` bind every interface but are not dialable; connect over
    /// loopback instead. An IPv6 literal must be bracketed to be a URL host.
    /// </summary>
    public static string ToolConnectHost(string bindHost)
    {
        if (bindHost == "0.0.0.0") return "127.0.0.1";
        if (bindHost is "::" or "::1") return "[::1]";
        return bindHost;
    }

    // --- the doorway: MCP servers the user declared themselves ----------------
    //
    // JARVIS ships connected to nothing. `--strict-mcp-config` means the brain
    // sees ONLY the config written here: ~/.claude.json, a project's .mcp.json
    // and Claude Desktop connectors are all ignored. Adopting one is a line in
    // `<data>/jarvis/connections.json`, and this is where that line is read.
    // Everything below refuses loudly: a server that quietly fails to appear is
    // the worst outcome for a feature whose selling point is "it's easy".

    // `jarvis` is ours. `mcp__jarvis__*` is how the brain reaches steer_session,
    // spawn_run and run_command, so a server that took that name would inherit
    // the entire acting surface without ever touching the origin gate.
    public const string ReservedServerName = "jarvis";

    // Tools arrive namespaced `mcp__<server>__<tool>`. A server name with a
    // space, a slash, or a `__` of its own makes that unparseable. Anchored with
    // `\z` (fullmatch): `$` would accept a trailing newline.
    public static readonly Regex ServerNameRe =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// What the user declared, and everything wrong with how they declared it.
    /// `Problems` are finished sentences: they are read aloud by the
    /// `connections` tool, not printed to a terminal nobody is watching.
    /// </summary>
    public sealed class ConnectionsReport
    {
        public Dictionary<string, JsonObject> Servers { get; set; } = new();
        public List<string> Problems { get; set; } = new();
    }

    /// <summary>The raw `mcpServers` block, plus anything wrong with the file itself.</summary>
    public static (JsonObject Block, List<string> Problems) ReadConnectionsFile()
    {
        var path = DataPaths.ConnectionsPath();
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new JsonObject(), []);          // nothing declared is not a problem
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new JsonObject(), [$"I could not read {path} ({e.Message})."]);
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(raw);
        }
        catch (Exception e)
        {
            return (new JsonObject(), [$"{path} is not valid JSON ({e.Message}), so nothing in it is " +
                                       "connected."]);
        }
        if (body is not JsonObject obj)
            return (new JsonObject(), [$"{path} must contain a JSON object."]);

        if (!obj.TryGetPropertyValue("mcpServers", out var block) || block is null)
        {
            // Almost always the inner half of a README's snippet pasted straight
            // in. It looks exactly like nothing happening, so name it.
            return (new JsonObject(), [$"{path} has no \"mcpServers\" block, so nothing in it is " +
                                       "connected — the servers go inside one."]);
        }
        if (block is not JsonObject blockObj)
            return (new JsonObject(), [$"The \"mcpServers\" entry in {path} must be an object."]);
        return (blockObj, []);
    }

    /// <summary>
    /// The user's own MCP servers, validated, with a sentence for each one
    /// refused. Never throws: a mangled file must not stop JARVIS starting.
    /// </summary>
    public static ConnectionsReport DeclaredConnections()
    {
        var (block, problems) = ReadConnectionsFile();
        var report = new ConnectionsReport { Problems = new List<string>(problems) };
        foreach (var (name, entry) in block)
        {
            var label = !string.IsNullOrEmpty(name) ? $"\"{name}\"" : "an unnamed entry";
            if (name == ReservedServerName)
            {
                report.Problems.Add(
                    $"{label} in your connections file is a name I use for my own " +
                    "tools, so I left it out — rename it and it will connect.");
                continue;
            }
            if (!ServerNameRe.IsMatch(name) || name.Contains("__"))
            {
                report.Problems.Add(
                    $"{label} is not a usable server name — letters, digits, dots, " +
                    "dashes and single underscores only — so I left it out.");
                continue;
            }
            if (entry is not JsonObject e)
            {
                report.Problems.Add($"{label} in your connections file is not an " +
                                    "object, so I left it out.");
                continue;
            }
            var hasCommand = !string.IsNullOrEmpty(Py.Str(e, "command"));
            var hasUrl = !string.IsNullOrEmpty(Py.Str(e, "url"));
            if (!hasCommand && !hasUrl)
            {
                report.Problems.Add(
                    $"{label} has neither a \"command\" nor a \"url\", so there is " +
                    "nothing for me to start — I left it out.");
                continue;
            }
            report.Servers[name] = (JsonObject)e.DeepClone();
        }
        return report;
    }

    // What the last `WriteMcpConfig` actually handed the brain. The `connections`
    // tool reports from THIS rather than re-reading the file: a file edited since
    // the brain started describes a JARVIS that does not exist yet.
    public static ConnectionsReport LastConnections = new();

    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Generate the brain's mcp.json: JARVIS's own tools, plus whatever the user
    /// declared in `<data>/jarvis/connections.json`. The brain's env is scrubbed,
    /// so the child gets the endpoint and the token path explicitly here, built
    /// from the server's ACTUAL bind scheme/host/port as recorded in the
    /// environment, not assumed defaults. The child is this same EXE in its
    /// `mcp` role.
    /// </summary>
    public static string WriteMcpConfig(string home)
    {
        var scheme = Py.Getenv("JARVIS_SCHEME", "http")!;
        var port = int.Parse(Py.Getenv("JARVIS_PORT", "8340")!, CultureInfo.InvariantCulture);
        var bindHost = Py.Getenv("JARVIS_BIND_HOST", "127.0.0.1")!;
        var connectHost = ToolConnectHost(bindHost);

        LastConnections = DeclaredConnections();
        foreach (var problem in LastConnections.Problems)
            Log.LogWarning($"connections: {problem}");
        if (LastConnections.Servers.Count > 0)
            Log.LogInformation($"connections: {string.Join(", ", LastConnections.Servers.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

        var servers = new JsonObject();
        foreach (var (name, entry) in LastConnections.Servers)
            servers[name] = entry.DeepClone();
        // Written LAST so it cannot be displaced whatever the file says. The
        // reserved-name check above is the message; this is the guarantee.
        servers.Remove(ReservedServerName);
        servers[ReservedServerName] = new JsonObject
        {
            ["command"] = Py.ExePath,
            ["args"] = new JsonArray("mcp"),
            ["env"] = new JsonObject
            {
                ["JARVIS_TOOL_URL"] = $"{scheme}://{connectHost}:{port}/internal/tool",
                ["JARVIS_TOOL_TOKEN_FILE"] = DataPaths.ToolTokenPath(),
            },
        };
        var config = new JsonObject
        {
            ["//"] = "Generated by JARVIS on every start — your edits here are lost. " +
                     $"Declare your own servers in {DataPaths.ConnectionsPath()}.",
            ["mcpServers"] = servers,
        };
        var path = Path.Combine(home, "mcp.json");
        // On POSIX this file is forced to 0600: it holds the tool token's path
        // and a verbatim copy of every `env` block out of connections.json (the
        // user's Notion/GitHub tokens). On Windows it inherits the ACL of the
        // per-user data directory, which is the equivalent.
        File.WriteAllText(path, config.ToJsonString(McpJsonOptions), Py.Utf8NoBom);
        if (!Py.IsWindows) Posix.Chmod600(path);
        return path;
    }

    /// <summary>
    /// Projects with a LIVE Claude Code session, for a new brain's prompt.
    /// `gone` and `fresh` sessions are excluded (a finished project is not
    /// active; a never-prompted window is not work in progress). Degrades to []
    /// when the watcher has not polled yet. Every name goes through
    /// `PlainName`: these land in `--append-system-prompt`, trusted operator
    /// prose, and a roster entry can claim any directory name at all. A refused
    /// name is DROPPED rather than replaced with a placeholder.
    /// </summary>
    public static List<string> ActiveProjectNames()
    {
        var snap = SnapshotOrEmpty();
        var dormant = new HashSet<string> { SessionWatch.Gone, SessionWatch.Fresh };
        var names = new HashSet<string>();
        foreach (var s in snap.Sessions)
        {
            if (string.IsNullOrEmpty(s.Project) || dormant.Contains(s.State)) continue;
            var ordinary = PlainName(s.Project, "");
            if (!string.IsNullOrEmpty(ordinary)) names.Add(ordinary);
        }
        return names.OrderBy(n => n, StringComparer.Ordinal).Take(BrainMod.MaxBootProjects).ToList();
    }

    public static async Task StartBrainAndSpeech()
    {
        TtsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        SpeechInstance = new Speech.SpeechScheduler(t => SynthForSpeech(t), VoiceEmit,
            prepare: StripMarkdownForTts, transportReady: () => HasVoiceClients);
        await SpeechInstance.Start();
        // EnsureLayout() rather than EnsureBrainHome(): the persona's
        // `@MEMORY.md` import needs the index to exist, and the memory tools
        // need their folders, from the very first boot.
        var home = JarvisMemory.EnsureLayout();
        DataPaths.EnsureToolToken();
        var mcpPath = WriteMcpConfig(home);
        var config = BrainMod.BrainConfig.FromEnv(home);
        config.McpConfig = mcpPath;
        // Exactly the servers `WriteMcpConfig` accepted — so what is merged into
        // the config and what the allowlist grants can never disagree.
        config.Connections = LastConnections.Servers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var brain = new BrainMod.Brain(config);
        BrainInstance = brain;
        brain.OnState(OnBrainState);
        // The other half of the handover: the brain reads the last real journal
        // entry itself, and asks us who is working right now. Called at spawn
        // time, so the watcher (started after us in lifespan) has had its chance.
        brain.ActiveProjects = () => ActiveProjectNames();
        if (Py.Getenv("JARVIS_BRAIN_AUTOSTART", "1") == "1")
            _ = Spawn(() => brain.Start(), "brain start");
        else
            Log.LogInformation("brain autostart disabled (JARVIS_BRAIN_AUTOSTART=0)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Context rotation: swapping the brain at a pause, with its own handover
    // ═══════════════════════════════════════════════════════════════════════

    public const string JournalRequest = "(system) Your context is about to be rotated. Write your handover " +
                                         "now: what you worked on, what the user decided, and what is " +
                                         "unfinished. Two or three sentences. Reply with the note itself and " +
                                         "nothing else.";

    // A brain that has gone quiet must not hold shutdown open. Its own turn
    // timeout is 90s, far too long to wait while the process is going down.
    public const double ShutdownJournalTimeout = 15.0;

    // One rotation at a time. `HandleUtterance` runs as a task per utterance, so
    // two of them can reach the pause together; without this, both would ask
    // the outgoing brain for a handover and swap the process from under each other.
    public static readonly SemaphoreSlim RotationLock = new(1, 1);

    // The handover already collected for the rotation currently pending, and
    // whether we have asked for it. `Brain.Rotate()` returns false and keeps the
    // old brain serving when the replacement will not start — without this we
    // would spend another brain turn, and another journal entry, at every pause.
    public static string? PendingHandover = null;
    public static bool HandoverCollected = false;

    /// <summary>
    /// What the brain generation writing this note has read that JARVIS did not
    /// write, or null. Never throws.
    /// </summary>
    public static string? GenerationUntrustedSource()
    {
        try { return BrainInstance?.GenerationUntrustedSource; }
        catch { return null; }
    }

    /// <summary>
    /// Persist one journal entry, reporting whether it landed. Never throws:
    /// journalling is bookkeeping, and a full disk must not stop a rotation or a
    /// shutdown. The untrusted source is recorded so the next generation is told
    /// where its author had been, across a process boundary.
    /// </summary>
    public static bool WriteJournal(string text, string reason)
    {
        try
        {
            JarvisMemory.WriteJournal(text, reason: reason, untrustedSource: GenerationUntrustedSource());
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning($"journal write ({reason}) failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ask the outgoing brain for its own handover, or null if it will not give
    /// one. Origin is `system`, NOT `user`, so the acting-tool gate in
    /// /internal/tool refuses any write the brain might attempt while answering.
    /// </summary>
    public static async Task<string?> AskForJournal(double? timeout = null)
    {
        var brain = BrainInstance;
        if (brain is null || !brain.Ready) return null;
        BrainMod.TurnResult result;
        try
        {
            var call = brain.Turn(JournalRequest, origin: "system");
            result = timeout is double t && t > 0
                ? await call.WaitAsync(TimeSpan.FromSeconds(t))
                : await call;
        }
        catch (Exception e)
        {
            Log.LogWarning($"journal request failed: {e.Message}");
            return null;
        }
        // A turn that ended in an error, a timeout or a rate limit may still
        // carry text — the CLI's error string. That is not a handover.
        if (result.StopReason != "result")
        {
            Log.LogWarning($"journal request ended in {result.StopReason}; no handover");
            return null;
        }
        var text = (result.Text ?? "").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Throw the current generation away at the user's word. No handover:
    /// carrying a summary across would carry the tainted text with it, which is
    /// exactly what the memory-writer gate exists to stop.
    /// </summary>
    public static async Task StartFresh()
    {
        var brain = BrainInstance;
        if (brain is null) return;
        await RotationLock.WaitAsync();
        try
        {
            PendingHandover = null;
            HandoverCollected = false;
            try
            {
                await brain.Rotate(handover: null);
            }
            catch (Exception e)
            {
                Log.LogError(e, $"fresh start failed: {e.Message}");
                if (SpeechInstance is not null)
                    await SpeechInstance.Say("I couldn't clear it, sir.", Speech.Priority.Normal);
                return;
            }
        }
        finally
        {
            RotationLock.Release();
        }
        Log.LogInformation("fresh start: generation discarded at the user's request");
        if (SpeechInstance is not null)
            await SpeechInstance.Say(FreshStartLine, Speech.Priority.Normal);
    }

    /// <summary>
    /// Rotate at a pause, never mid-conversation. The outgoing brain is asked for
    /// a handover first; if it will not answer the server writes a minimal entry
    /// itself, and the rotation proceeds regardless — a silent brain must not be
    /// able to pin the context window open forever.
    /// </summary>
    public static async Task MaybeRotate()
    {
        var brain = BrainInstance;
        if (brain is null || !brain.RotationPending) return;
        // Not actually a pause: another utterance is being served right now.
        // `RotationOverdue` is the escape hatch for a conversation that never pauses.
        if (brain.CurrentOrigin is not null && !brain.RotationOverdue) return;
        if (RotationLock.CurrentCount == 0) return;      // another pause got there first
        await RotationLock.WaitAsync();
        try
        {
            brain = BrainInstance;
            if (brain is null || !brain.RotationPending) return;
            // Say so before the pause, not after it.
            try
            {
                await VoiceEmit(new() { ["type"] = "notice", ["text"] = RotationBusyLine });
                // The orb dims and slows for the duration; see the "compacting"
                // state in frontend/src/orb.ts.
                await VoiceEmit(new() { ["type"] = "status", ["state"] = "compacting" });
            }
            catch (Exception) { }                       // never let a notice stop a rotation
            if (!HandoverCollected)
            {
                PendingHandover = await AskForJournal();
                HandoverCollected = true;
                if (!string.IsNullOrEmpty(PendingHandover))
                    WriteJournal(PendingHandover, reason: "rotation");
                else
                    WriteJournal("No handover was written — the outgoing brain did not answer.",
                        reason: "rotation-silent");
            }
            bool rotated;
            try
            {
                rotated = await brain.Rotate(handover: PendingHandover);
            }
            catch (Exception e)
            {
                Log.LogError(e, $"rotation failed: {e.Message}");
                rotated = false;
            }
            if (rotated)
            {
                PendingHandover = null;
                HandoverCollected = false;
            }
            try
            {
                await VoiceEmit(new() { ["type"] = "notice", ["text"] = "" });       // clear the banner
                await VoiceEmit(new() { ["type"] = "status", ["state"] = "idle" });  // orb back to normal
            }
            catch (Exception) { }
            // The Python's `else:` hung off the banner-clearing `try`, so it logged
            // this after every successful rotation too; it belongs to `rotated`.
            if (!rotated)
            {
                // The old brain is still serving; keep the handover we already
                // paid a turn for and try again at the next pause.
                Log.LogWarning("rotation did not happen; retrying at the next pause");
            }
        }
        finally
        {
            RotationLock.Release();
        }
    }

    /// <summary>
    /// Stop the brain first (no more turns), then the mouth, then the HTTP
    /// client; each step isolated. A generation never vanishes without a trace:
    /// with nothing to hand over the entry is a labelled TOMBSTONE
    /// (`shutdown-silent`) so the next cold start skips it rather than letting a
    /// placeholder displace a real handover written minutes earlier.
    /// </summary>
    public static async Task StopBrainAndSpeech()
    {
        try
        {
            var handover = await AskForJournal(timeout: ShutdownJournalTimeout);
            if (!string.IsNullOrEmpty(handover))
                WriteJournal(handover, reason: "shutdown");
            else
                WriteJournal("Session ended; the brain wrote no handover.", reason: "shutdown-silent");
        }
        catch (Exception e)
        {
            Log.LogWarning($"shutdown journal failed: {e.Message}");
        }
        var steps = new List<(string Label, Func<Task>? Stop)>
        {
            ("brain", BrainInstance is { } b ? (Func<Task>)(() => b.Stop()) : null),
            ("speech", SpeechInstance is { } s ? (Func<Task>)(() => s.Stop()) : null),
            ("tts client", TtsClient is { } c ? (Func<Task>)(() => { c.Dispose(); return Task.CompletedTask; }) : null),
        };
        foreach (var (label, stop) in steps)
        {
            if (stop is null) continue;
            try { await stop(); }
            catch (Exception e) { Log.LogWarning($"shutdown: {label} did not stop cleanly: {e.Message}"); }
        }
        BrainInstance = null;
        SpeechInstance = null;
        TtsClient = null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Announcements: sessions and runs
    // ═══════════════════════════════════════════════════════════════════════

    // Completions are held and spoken together at the next pause: the user
    // asked for "needs-you now, completions batched".
    private static readonly object PendingGate = new();
    public static readonly List<string> PendingCompletions = [];

    // The same batch for runs JARVIS started himself. Its own list because the
    // sentence differs, but drained by the same `AnnounceBatch` so the user
    // hears ONE sentence at the pause and not two.
    public static readonly List<string> PendingRunCompletions = [];

    // Small counts are spelled out for speech — a bare numeral mid-sentence
    // reads poorly through TTS. Double digits are fine as numerals.
    public static readonly Dictionary<long, string> NumberWords = new()
    {
        [1] = "one", [2] = "two", [3] = "three", [4] = "four", [5] = "five",
        [6] = "six", [7] = "seven", [8] = "eight", [9] = "nine",
    };

    public static string SayNumber(long n) => NumberWords.TryGetValue(n, out var w) ? w : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>'a', 'a and b', or 'a, b and c' — the Oxford-comma-free house style.</summary>
    public static string ListJoin(IList<string> items)
    {
        if (items.Count == 1) return items[0];
        return string.Join(", ", items.Take(items.Count - 1)) + $" and {items[^1]}";
    }

    /// <summary>
    /// Watcher callback. Scheduling with `Spawn` is thread-safe, so it does not
    /// matter which thread the watcher calls this on.
    /// </summary>
    public static void OnSessionEvent(Dictionary<string, object?> ev)
    {
        var kind = DGet(ev, "kind") as string;
        var session = DGet(ev, "session") ?? new Dictionary<string, object?>();
        var sid = DGet(session, "session_id") as string;
        if (sid is not null && JarvisRunSessionIds().Contains(sid))
        {
            // One of JARVIS's own runs, seen from the roster side. The run
            // pipeline already narrates it; announcing it here too would say the
            // same thing twice. Nor is it broadcast — `sessions-live.ts` patches
            // in the session an `event` names without reconciling, so a run event
            // would put a run ROW on the Sessions tab.
            return;
        }
        Spawn(() => BroadcastSessionEvent(ev), "session broadcast");
        if (kind == "needs_you")
        {
            Spawn(() => AnnounceNeedsYou(ev), "needs-you announcement");
        }
        else if (kind == "finished")
        {
            // Walled where it ENTERS the queue: a value already in module state
            // cannot be judged by `SessionBatchLine` when it reads it out.
            var name = SaidName(session);
            if (!string.IsNullOrEmpty(name))
                lock (PendingGate)
                    if (!PendingCompletions.Contains(name)) PendingCompletions.Add(name);
            Spawn(() => AnnounceBatch(), "completion batch");
        }
    }

    /// <summary>
    /// Interrupt for a session that has stopped and wants the user. Both
    /// variables are a roster file's own strings, so the name goes through
    /// `SaidName` (a voice name is a phrase — "hammer in Desktop" — so
    /// `PlainName`, which forbids a space, would erase it).
    /// </summary>
    public static async Task AnnounceNeedsYou(Dictionary<string, object?> ev)
    {
        var speech = SpeechInstance;
        if (speech is null) return;
        var s = DGet(ev, "session") ?? new Dictionary<string, object?>();
        var rawVoice = DGet(s, "voice_name");
        var rawName = IsTruthy(rawVoice) ? PyStr(rawVoice) : "a session";
        var name = SaidName(s, "A session");
        var needs = DGet(s, "needs");
        string line;
        if (IsTruthy(needs))
        {
            var reason = PhraseNeeds(PyStr(needs));
            line = IsTruthy(DGet(s, "needs_a_human_hand"))
                ? $"{name} is {reason}, sir — that one needs your own keystroke."
                : $"{name} is {reason}, sir.";
        }
        else
        {
            line = $"{name} has stopped and wants you, sir.";
        }
        try
        {
            await speech.Say(line, Speech.Priority.Urgent);
        }
        catch (Exception e)
        {
            Log.LogWarning($"needs-you announcement failed: {e.Message}");
        }
        // The RAW name to the notifier, deliberately: it renders to a human and
        // is passed as an argument, never as script source. The scrubbing above
        // is for the line JARVIS SAYS, which lands in his own context.
        await NotifyNeedsYou(rawName, line);
    }

    /// <summary>
    /// The desktop-notification fallback for a needs-you nobody was listening to.
    /// Fires ONLY when no voice client is connected — the user must never be
    /// notified about something he just heard. Text is passed to the notifier as
    /// arguments, never formatted into a command. Never throws.
    /// </summary>
    public static async Task NotifyNeedsYou(string name, string line)
    {
        if (HasVoiceClients || !Notifier.Available()) return;
        try
        {
            await Notifier.Notify("JARVIS", line, subtitle: name);
        }
        catch (Exception e)
        {
            Log.LogWarning($"needs-you notification failed: {e.Message}");
        }
    }

    /// <summary>At most three names, then a count of the rest.</summary>
    public static string CapListing(IList<string> items)
    {
        if (items.Count <= 3) return ListJoin(items);
        var remaining = items.Count - 3;
        var otherWord = remaining == 1 ? "other" : "others";
        return ListJoin(items.Take(3).ToList()) + $", and {SayNumber(remaining)} {otherWord}";
    }

    public static string SessionBatchLine(IList<string> names)
    {
        if (names.Count == 1) return $"{names[0]} has finished, sir.";
        return $"{Capitalize(SayNumber(names.Count))} conversations have " +
               $"finished, sir: {CapListing(names)}.";
    }

    /// <summary>
    /// What JARVIS started himself, and that it worked. Failures never reach
    /// here — they interrupt — so "is done" is an honest report of success.
    /// </summary>
    public static string RunBatchLine(IList<string> projects)
    {
        if (projects.Count == 1) return $"The work in {projects[0]} is done, sir.";
        return $"Work in {CapListing(projects)} is done, sir.";
    }

    /// <summary>
    /// Say what finished, in one sentence, at the next pause. Drains both
    /// queues: one call, one utterance.
    /// </summary>
    public static async Task AnnounceBatch()
    {
        var speech = SpeechInstance;
        if (speech is null) return;
        List<string> names, projects;
        lock (PendingGate)
        {
            if (PendingCompletions.Count == 0 && PendingRunCompletions.Count == 0) return;
            names = PendingCompletions.ToList();
            PendingCompletions.Clear();
            projects = PendingRunCompletions.ToList();
            PendingRunCompletions.Clear();
        }

        var parts = new List<string>();
        if (names.Count > 0) parts.Add(SessionBatchLine(names));
        if (projects.Count > 0) parts.Add(RunBatchLine(projects));
        var line = string.Join(" ", parts);
        try
        {
            await speech.Say(line, Speech.Priority.Low);
        }
        catch (Exception e)
        {
            Log.LogWarning($"completion announcement failed: {e.Message}");
            // Do not lose them — either queue.
            lock (PendingGate)
            {
                PendingCompletions.AddRange(names);
                PendingRunCompletions.AddRange(projects);
            }
        }
    }

    /// <summary>
    /// RunExecutor subscriber: the voice path's ear on the run pipeline. Only
    /// runs JARVIS himself started (origin "voice") are narrated. Never throws.
    /// </summary>
    public static void OnRunEvent(Dictionary<string, object?> message)
    {
        try
        {
            if (DGet(message, "type") as string != "run_finished") return;
            var runObj = DGet(message, "run");
            var run = runObj as Dictionary<string, object?> ?? ToDict(runObj);
            if (DGet(run, "origin") as string != "voice") return;
            var status = DGet(run, "status") as string;
            // Walled where it ENTERS the queue, not where `RunBatchLine` reads it.
            var project = RunProject(run);
            if (status == RunStore.RunStatus.Succeeded)
            {
                var outcome = RunOutcome(run);
                if (outcome != StreamParser.Ok)
                {
                    // Exit zero, but nothing was built. Batching this behind
                    // "the work in X is done" is exactly the lie this guards
                    // against, so it interrupts like a failure — because it is one.
                    Spawn(() => AnnounceRunStalled(run, outcome), "run stall announcement");
                    return;
                }
                lock (PendingGate)
                    if (!PendingRunCompletions.Contains(project)) PendingRunCompletions.Add(project);
                Spawn(() => AnnounceBatch(), "completion batch");
            }
            else if (status == RunStore.RunStatus.Failed || status == RunStore.RunStatus.TimedOut)
            {
                // Worth interrupting for. A batched failure is a failure the user
                // hears about ten minutes after it could have been fixed.
                Spawn(() => AnnounceRunFailure(run), "run failure announcement");
            }
            // CANCELLED is deliberately silent: the user asked for it and was
            // told at the time.
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "run completion announcement failed");
        }
    }

    /// <summary>A run that exited zero having built nothing.</summary>
    public static async Task AnnounceRunStalled(Dictionary<string, object?> run, string outcome)
    {
        var speech = SpeechInstance;
        if (speech is null) return;
        var project = RunProject(run);
        var line = outcome == StreamParser.Stalled
            ? $"The work in {project} stopped to ask a question, sir, so nothing was built."
            : $"The work in {project} finished, sir, but I can't see that it changed anything.";
        try
        {
            await speech.Say(line, Speech.Priority.Urgent);
        }
        catch (Exception e)
        {
            Log.LogWarning($"run stall announcement failed: {e.Message}");
        }
    }

    /// <summary>Interrupt for work of JARVIS's own that did not survive.</summary>
    public static async Task AnnounceRunFailure(Dictionary<string, object?> run)
    {
        var speech = SpeechInstance;
        if (speech is null) return;
        var project = RunProject(run);
        var line = DGet(run, "status") as string == RunStore.RunStatus.TimedOut
            ? $"The work in {project} ran out of time, sir."
            : $"The work in {project} failed, sir.";
        try
        {
            await speech.Say(line, Speech.Priority.Urgent);
        }
        catch (Exception e)
        {
            Log.LogWarning($"run failure announcement failed: {e.Message}");
        }
    }

    // ── /ws/sessions client registry ────────────────────────────────────────

    public const int SessionQueueMax = 1000;
    private static readonly object SessionGate = new();
    public static readonly HashSet<WebSocket> SessionClients = [];
    public static readonly Dictionary<WebSocket, Channel<Dictionary<string, object?>>> SessionQueues = [];
    public static readonly Dictionary<WebSocket, CancellationTokenSource> SessionWriters = [];

    public static Channel<Dictionary<string, object?>> AddSessionClient(WebSocket ws)
    {
        var queue = NewClientQueue(SessionQueueMax);
        var cts = new CancellationTokenSource();
        lock (SessionGate)
        {
            SessionQueues[ws] = queue;
            SessionClients.Add(ws);
            SessionWriters[ws] = cts;
        }
        Spawn(() => Pump(ws, queue, DropSessionClient, cts.Token), "session writer");
        return queue;
    }

    public static void DropSessionClient(WebSocket ws)
    {
        CancellationTokenSource? cts;
        lock (SessionGate)
        {
            SessionClients.Remove(ws);
            SessionQueues.Remove(ws);
            SessionWriters.Remove(ws, out cts);
        }
        try { cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// Same bounded per-client queue as the voice path. Called from the session
    /// watcher, which also feeds what JARVIS speaks — a dashboard tab on a
    /// sleeping laptop must not be able to hold up the watcher itself.
    /// </summary>
    public static async Task BroadcastSessionEvent(Dictionary<string, object?> ev)
    {
        List<WebSocket> clients;
        lock (SessionGate) clients = SessionClients.ToList();
        foreach (var ws in clients)
        {
            Channel<Dictionary<string, object?>>? queue;
            lock (SessionGate) SessionQueues.TryGetValue(ws, out queue);
            if (queue is null)
            {
                DropSessionClient(ws);
                continue;
            }
            var msg = new Dictionary<string, object?> { ["type"] = "event" };
            foreach (var (k, v) in ev) msg[k] = v;
            Enqueue(queue, msg);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Preflight and the session watcher
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run the first-run environment checks and say what is wrong. Speaking it
    /// matters more than logging it: a dead brain is exactly the case where the
    /// user cannot ask what is wrong, and TTS is a separate path that still
    /// works. Failures are spoken; warnings are logged only.
    /// </summary>
    public static async Task RunPreflight()
    {
        List<Preflight.Check> checks;
        try
        {
            checks = await Preflight.RunChecks();
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "preflight checks could not run");
            return;
        }

        foreach (var c in checks)
        {
            if (c.Ok) Log.LogInformation($"preflight {c.Name}: ok");
            else Log.LogWarning($"preflight {c.Name}: {c.Message} — {c.Remedy}");
        }

        var speech = SpeechInstance;
        if (speech is not null && checks.Any(c => c.Status == Preflight.StatusFail))
        {
            var summary = Preflight.SpokenSummary(checks);
            if (!string.IsNullOrEmpty(summary)) await speech.Say(summary);
        }
    }

    public static async Task StartSessionWatcher()
    {
        var watcher = new SessionWatch.SessionWatcher(
            interval: double.Parse(Py.Getenv("JARVIS_WATCH_INTERVAL", "1.0")!, NumberStyles.Float, CultureInfo.InvariantCulture));
        SessionWatcher = watcher;
        watcher.OnEvent(OnSessionEvent);
        await watcher.Start();
        Log.LogInformation("session watcher started");
    }

    public static async Task StopSessionWatcher()
    {
        var watcher = SessionWatcher;
        if (watcher is not null)
        {
            try { await watcher.Stop(); }
            catch (Exception e) { Log.LogWarning($"shutdown: session watcher did not stop cleanly: {e.Message}"); }
        }
        SessionWatcher = null;
    }

    // How long the staged-steer path waits for the turn utterance to finish
    // playing before it speaks — only so a silent client cannot pin the mouth.
    public const double TurnSettleTimeout = 120.0;

    /// <summary>
    /// A turn that uses a tool says exactly one thing, at the end.
    ///
    /// The brain narrates around its tools ("Will say that to the session." —
    /// tool — "Saying this to it now." — tool — "Passed that to chitauri, sir.")
    /// and asking it not to does not hold, so the mouth is closed here instead.
    /// No tool: the first line is held for `holdFor` to see whether a tool
    /// follows; when none does it is released and the rest streams. A tool:
    /// everything written before the LAST tool call is binned; what survives is
    /// what he writes after his final tool, spoken once when the turn ends.
    /// </summary>
    public sealed class OneLinePerTurn(Action<string> sink, double holdFor = 0.6)
    {
        private readonly object _gate = new();
        private readonly List<string> _held = [];
        private bool _streaming;      // released: everything now goes straight out
        private bool _toolSeen;
        private double? _deadline;

        public void Delta(string d)
        {
            lock (_gate)
            {
                if (_streaming)
                {
                    sink(d);
                    return;
                }
                _deadline ??= Py.Monotonic() + holdFor;
                _held.Add(d);
                // Only a turn that has NOT touched a tool may start streaming on
                // the timer. Once one has, the rest of the turn is held to the end.
                if (!_toolSeen && Py.Monotonic() >= _deadline)
                {
                    Flush();
                    _streaming = true;
                }
            }
        }

        /// <summary>A tool call: everything written up to here was an intention.</summary>
        public void ToolStarted()
        {
            lock (_gate)
            {
                if (_held.Count > 0)
                {
                    var joined = string.Concat(_held);
                    Log.LogInformation($"speech: dropped narration before a tool: {Head(joined, 60)}");
                }
                _held.Clear();
                _toolSeen = true;
                _streaming = false;     // hold again; more tools may follow
            }
        }

        /// <summary>End of turn: say the one thing that survived.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                Flush();
                _streaming = true;
            }
        }

        private void Flush()
        {
            if (_held.Count == 0) return;
            var text = string.Concat(_held);
            _held.Clear();
            if (text.Trim().Length > 0) sink(text);
        }
    }

    /// <summary>
    /// One user utterance → one brain turn → streamed speech. Runs as a task so
    /// the socket loop keeps receiving `played` acks and interim text meanwhile.
    /// </summary>
    public static async Task HandleUtterance(string text)
    {
        var brain = BrainInstance;
        var speech = SpeechInstance;
        if (brain is null || speech is null) return;
        var t0 = Py.Monotonic();
        await VoiceEmit(new() { ["type"] = "status", ["state"] = "thinking" });
        if (!brain.Ready)
        {
            string line;
            if (brain.Failed)
                line = brain.FailureReason == "auth" ? AuthBrainDownLine : "My language systems are down, sir.";
            else
                line = "One moment, sir — my language systems are still starting.";
            await speech.Say(line, Speech.Priority.Normal);
            return;
        }
        var utt = speech.BeginTurn();
        try
        {
            BrainMod.TurnResult result;
            try
            {
                try
                {
                    var hold = new OneLinePerTurn(d => speech.Feed(utt, d));
                    result = await brain.Turn(text, origin: "user",
                        onDelta: hold.Delta, onTool: hold.ToolStarted);
                    hold.Finish();    // the one line this turn is allowed
                }
                finally
                {
                    await speech.EndTurn(utt);  // a turn that never ends would wedge the mouth
                }
            }
            catch (Exception e)
            {
                Log.LogError(e, $"brain turn failed: {e.Message}");
                await speech.Say("I lost my train of thought, sir. Say that again?", Speech.Priority.Normal);
                return;
            }
            var resultText = result.Text ?? "";
            if (result.StopReason == "rate_limited")
            {
                var resets = DGet(result.RateLimit, "resetsAt");
                await speech.Say($"I've hit the usage limit until {FmtReset(resets)}, sir.", Speech.Priority.Normal);
            }
            else if (result.StopReason == "error")
            {
                Log.LogError($"brain error: {result.Error}");
                await speech.Say("My language systems returned an error, sir. Check the server log.",
                    Speech.Priority.Normal);
            }
            else if (result.StopReason is "timeout" or "died" or "not_running")
            {
                await speech.Say("I lost my train of thought, sir. Say that again?", Speech.Priority.Normal);
            }
            else if (resultText.Trim().Length == 0)
            {
                await VoiceEmit(new() { ["type"] = "status", ["state"] = "idle" });
            }
            Log.LogInformation($"JARVIS: {Head(resultText.Trim(), 300)}");
            // first_audio is only known once the scheduler has sent the first
            // chunk; wait for playback so the latency line is accurate.
            await speech.WaitFor(utt, timeout: 120.0);
            double? firstAudio = utt.FirstSentAt is double fs ? fs - t0 : null;
            var fd = result.FirstDeltaSec is double f ? $"{f:F2}s" : "none";
            var fa = firstAudio is double a ? $"{a:F2}s" : "none";
            var tools = "[" + string.Join(", ", ((IEnumerable<string>?)result.Tools ?? Array.Empty<string>()).Select(x => $"'{x}'")) + "]";
            Log.LogInformation($"latency: first_delta={fd} first_audio={fa} turn={result.DurationSec:F2}s " +
                               $"ctx={result.ContextTokens} out={result.OutputTokens} tools={tools}");
        }
        finally
        {
            // Anything the brain staged mid-turn (a steer) happens HERE, once the
            // turn utterance is genuinely done and the mouth is free — never
            // inside the tool call. It runs even when the turn ended badly.
            if (StagedSteers.Count > 0 || StagedDialogs.Count > 0)
            {
                await speech.WaitFor(utt, timeout: TurnSettleTimeout);
                await PerformStagedSteers();
                await PerformStagedDialogs();
            }
            // Last of all: the pause is genuine and nothing the user is waiting
            // on is queued behind this. Rotation is bookkeeping — it must not be
            // able to take down the turn it follows.
            try
            {
                await MaybeRotate();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Log.LogError(e, $"rotation at the pause failed: {e.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The one RunExecutor. Subscribed at construction, not in the lifespan: the
    /// voice path's only way of hearing that work it started has ended must not
    /// depend on a startup step a partial boot might skip.
    /// (Type name: the run_executor module is the static class `RunExecutor`,
    /// so its `RunExecutor` class cannot share the name — `RunExecutor`.)
    /// </summary>
    public static readonly RunExecutor RunExecutorInstance = MakeRunExecutor();

    private static RunExecutor MakeRunExecutor()
    {
        var ex = new RunExecutor(maxConcurrent: 3);
        ex.Subscribe(OnRunEvent);
        return ex;
    }

    public static List<Dictionary<string, object?>> CachedProjects = [];

    // Usage tracking — logs every call with timestamp, persists to disk
    public static readonly string UsageFile = Path.Combine(Py.AppDir, "data", "usage_log.jsonl");
    public static readonly double SessionStart = Py.Now();
    public static readonly Dictionary<string, long> SessionTokens = new()
    {
        ["input"] = 0, ["output"] = 0, ["api_calls"] = 0, ["tts_calls"] = 0,
    };
    private static readonly object UsageGate = new();

    private static void CountTtsCall()
    {
        lock (SessionTokens) SessionTokens["tts_calls"] += 1;
    }

    /// <summary>Append a usage entry with timestamp to the log file.</summary>
    public static void AppendUsageEntry(long inputTokens, long outputTokens, string callType = "api")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsageFile)!);
            var entry = new Dictionary<string, object?>
            {
                ["ts"] = Py.Now(),
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["type"] = callType,
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
            };
            lock (UsageGate) File.AppendAllText(UsageFile, Py.Dumps(entry) + "\n", Py.Utf8NoBom);
        }
        catch { }
    }

    /// <summary>Sum usage from the log file for a time period. null = all time.</summary>
    public static Dictionary<string, long> GetUsageForPeriod(double? seconds = null)
    {
        var totals = new Dictionary<string, long>
        {
            ["input_tokens"] = 0, ["output_tokens"] = 0, ["api_calls"] = 0, ["tts_calls"] = 0,
        };
        double cutoff = seconds is double s && s != 0 ? Py.Now() - s : 0;
        try
        {
            if (File.Exists(UsageFile))
            {
                string text;
                lock (UsageGate) text = File.ReadAllText(UsageFile);
                foreach (var line in text.Trim().Split('\n'))
                {
                    if (line.Length == 0) continue;
                    var entry = Py.Loads(line) as JsonObject ?? throw new FormatException();
                    var ts = Py.Num(entry, "ts") ?? throw new KeyNotFoundException("ts");
                    if (ts >= cutoff)
                    {
                        totals["input_tokens"] += (long)(Py.Num(entry, "input_tokens") ?? 0);
                        totals["output_tokens"] += (long)(Py.Num(entry, "output_tokens") ?? 0);
                        if (Py.Str(entry, "type") == "tts") totals["tts_calls"] += 1;
                        else totals["api_calls"] += 1;
                    }
                }
            }
        }
        catch { }
        return totals;
    }

    public static double CostFromTokens(long inputT, long outputT) =>
        (inputT / 1_000_000.0) * 0.80 + (outputT / 1_000_000.0) * 4.00;

    /// <summary>
    /// Adopt the detected bind and say anything the operator needs to hear.
    /// Logged AND printed: the log is what a service manager captures; the print
    /// is what the person watching the terminal reads.
    /// </summary>
    public static void AnnounceBind(WebAuth.Bind bind)
    {
        WebAuth.AdoptBind(bind);
        Log.LogInformation($"serving on {bind.Scheme}://{bind.Host}:{bind.Port} ({bind.Source})");
        Passcode.LogStatus();
        var lines = WebAuth.ExposureWarning(bind);
        if (lines is null || lines.Count == 0) return;
        Log.LogWarning(string.Join(" ", lines.Select(l => l.TrimStart('!', ' ').Trim())));
        Console.WriteLine();
        foreach (var line in lines) Console.WriteLine($"  {line}");
        Console.WriteLine();
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lifespan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The startup half of FastAPI's `lifespan`.</summary>
    public static async Task LifespanStart()
    {
        CachedProjects = [];

        // FIRST, before anything reads JARVIS_PORT / JARVIS_BIND_HOST — the
        // origin allowlist, the Host allowlist and the MCP child's URL all do.
        // Program.cs records those variables already; this is the backstop.
        AnnounceBind(WebAuth.DetectBind());

        RunStore.InitDb();
        try
        {
            var moved = Migrations.Migrate001();
            if (moved > 0) Log.LogInformation($"migrated {moved} legacy dispatch row(s)");
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "dispatch migration skipped");
        }
        RunStore.SweepStaleRuns();

        await StartBrainAndSpeech();
        await StartSessionWatcher();
        // Deliberately not awaited: every check is time-boxed to 5s, so running
        // them inline could hold the server closed before the UI can connect —
        // and the mic is the first thing the user reaches for.
        _ = Spawn(() => RunPreflight(), "preflight");
        Log.LogInformation("JARVIS server starting");
    }

    /// <summary>The shutdown half of FastAPI's `lifespan`.</summary>
    public static async Task LifespanStop()
    {
        await StopSessionWatcher();
        await StopBrainAndSpeech();
    }

    // The interactive OpenAPI console is a "Try it out" button on every route,
    // so in the Python it lived behind a debugging flag. The C# port has no
    // OpenAPI console at all; the flag is read for parity only.
    public static bool DebugDocs = ReadDebugDocs();

    private static bool ReadDebugDocs() =>
        (Py.Getenv("JARVIS_DEBUG_DOCS", "") ?? "").ToLowerInvariant() is "1" or "true" or "yes";

    // No CORS at all, deliberately: the frontend only ever fetches relative
    // paths and is same-origin, which needs no CORS headers — and sending none
    // is what stops a hostile page reading the GETs that cannot be gated.
    // WebAuth.OriginGuard (wired in Program.cs) is what replaces it.

    // ═══════════════════════════════════════════════════════════════════════
    // Per-session usage
    // ═══════════════════════════════════════════════════════════════════════
    //
    // `/api/usage/limits` is the SUBSCRIPTION's picture; who spent it is only
    // answerable from the CLI's own transcripts, which `UsageScan` reads. This
    // endpoint owns the set of run ids (so JARVIS's own runs are bucketed apart,
    // agreeing with the Sessions tab) and a TTL (a cold scan is ~3s of disk; a
    // warm one ~46ms), and runs the scan off the request thread.

    // Long enough that several tabs polling cost one scan; short enough that a
    // run which just finished shows up on the next refresh.
    public const double UsageScanTtlSec = 20.0;

    // The incremental cursor, held for the life of the process on purpose.
    public static readonly UsageScan.Cache UsageScanCache = new();
    private static readonly object UsageScanLock = new();
    public static (double Stamped, Dictionary<string, object?>? Body) UsageScanResult = (0.0, null);

    /// <summary>The cached per-session reading. Runs on a worker thread.</summary>
    public static Dictionary<string, object?> UsageScanSnapshot()
    {
        lock (UsageScanLock)
        {
            var (stamped, body) = UsageScanResult;
            var now = Py.Now();
            if (body is { Count: > 0 } && now - stamped < UsageScanTtlSec) return body;
            var fresh = UsageScan.Snapshot(cache: UsageScanCache, ownSessionIds: JarvisRunSessionIds());
            UsageScanResult = (now, fresh);
            return fresh;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Runs — request model (used by POST /api/runs in Part 3)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class RunRequest
    {
        public string? Prompt { get; set; }          // required: validate → 422 when missing
        public string ProjectPath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string? ResumeFrom { get; set; } = null;
        public double TimeoutSec { get; set; } = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Memory — the plain-Markdown folder, read-only over HTTP
    //
    // Nothing here writes, and nothing here creates the folder: a GET that
    // brought `jarvis/` into being would be a side effect nobody asked for, so
    // a brain that has never remembered anything reports empty lists instead.
    // ═══════════════════════════════════════════════════════════════════════

    public static readonly string[] MemoryDocKinds = ["memory", "project", "journal"];

    // ═══════════════════════════════════════════════════════════════════════
    // The review surface: /api/specs, /api/specs/doc, /ws/specs
    //
    // The SPECS tab is for READING: no write endpoint, no editor. Every
    // containment decision belongs to `Specs`, which resolves through
    // `RepoRead.ResolveWithin`; nothing here interprets a path itself.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The directory a project name means, or null. Reuses `ProjectCandidates`,
    /// the same map `start_build` resolves against. With no `root`, an ambiguous
    /// name resolves to nothing rather than to a guess. `root` (from a URL, not
    /// trusted) is only ever accepted as a member of the project's own known
    /// directories, never interpreted.
    /// </summary>
    public static string? ProjectPathOrNone(string reference, string root = "")
    {
        if (!ProjectCandidates().TryGetValue(reference, out var paths) || paths is null || paths.Count == 0)
            return null;
        if (!string.IsNullOrEmpty(root)) return paths.Contains(root) ? root : null;
        if (paths.Count > 1) return null;
        return paths.First();
    }

    /// <summary>
    /// Which COPY of a project this directory is, in a few words — Claude Code
    /// worktrees collapse to the repo name, and this label is what keeps two
    /// rows of one project distinguishable.
    /// </summary>
    public static string ProjectWhere(string path)
    {
        var branch = SessionWatch.WorktreeBranch(path);
        if (!string.IsNullOrEmpty(branch)) return $"worktree {branch}";
        var parentDir = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        var parent = parentDir is null ? "" : Path.GetFileName(parentDir);
        return !string.IsNullOrEmpty(parent) ? $"in {parent}" : path;
    }

    /// <summary>
    /// Every known project that has a spec or a plan, with its review state —
    /// EVERY DIRECTORY OF IT (dropping ambiguous names hid real specs behind
    /// "Nothing to review yet"). Blocking; callers wrap it. Projects with
    /// nothing to review are left out entirely.
    /// </summary>
    public static List<Dictionary<string, object?>> SpecsProjects()
    {
        var output = new List<Dictionary<string, object?>>();
        foreach (var (name, paths) in ProjectCandidates().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var found = new List<(string Path, Dictionary<string, object?> Review)>();
            foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                Dictionary<string, object?>? review;
                try
                {
                    review = Specs.ProjectReview(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (review is not null) found.Add((path, review));
            }
            foreach (var (path, review) in found)
            {
                var row = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["path"] = path,
                    // Only where it disambiguates: a label on a project that
                    // lives in one place is noise.
                    ["where"] = found.Count > 1 ? ProjectWhere(path) : "",
                };
                foreach (var (k, v) in review) row[k] = v;
                output.Add(row);
            }
        }
        // Whatever moved most recently is what the user is working on.
        return output.OrderByDescending(p => ToDouble(p.GetValueOrDefault("modified")) ?? 0).ToList();
    }

    // How often an open SPECS tab looks for a changed file. A spec is revised by
    // writing to disk and an approval is a file appearing beside it, so polling
    // is the honest mechanism, and it only runs while the tab is open.
    public const double SpecsPollDefault = 2.0;

    /// <summary>
    /// What the page is currently showing, reduced to a comparable string —
    /// including each project's DIRECTORY, since a worktree puts one name twice.
    /// </summary>
    public static string SpecsFingerprint()
    {
        var parts = new List<string>();
        foreach (var project in SpecsProjects())
        {
            var docs = DGet(project, "documents") as System.Collections.IEnumerable;
            if (docs is null || docs is string) continue;
            foreach (var doc in docs)
            {
                var progress = DGet(doc, "progress");
                var done = DGet(progress, "done");
                var total = DGet(progress, "total");
                var approval = DGet(doc, "approval");
                parts.Add(string.Join("|",
                    PyStr(DGet(project, "name")), PyStr(DGet(project, "path")), PyStr(DGet(doc, "path")),
                    (ToDouble(DGet(doc, "modified")) ?? 0).ToString("F3", CultureInfo.InvariantCulture),
                    PyStr(DGet(approval, "state")),
                    $"{(done is null ? "" : PyStr(done))}/{(total is null ? "" : PyStr(total))}"));
            }
        }
        return string.Join("\n", parts);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The tool channel
    // ═══════════════════════════════════════════════════════════════════════

    // The tool result cap is what keeps the brain's context under budget.
    public const int ToolResultCap = 1500;

    public static string CapToolResult(string text)
    {
        if (text.Length <= ToolResultCap) return text;
        return text[..(ToolResultCap - 40)].TrimEnd() + "\n… (truncated — ask for more)";
    }

    /// <summary>
    /// A tool result the brain must LOOK at, not merely read. The brain has no
    /// Read tool, so the one route an image has into it is an MCP `image`
    /// content block. The bytes travel in their own `image` field — base64 in
    /// `text` would be shredded by `ToolResultCap`. Only `text` is capped.
    /// </summary>
    public sealed class ToolImage(string text, byte[] png, string mime = "image/png")
    {
        public string Text { get; } = text;
        public byte[] Png { get; } = png;
        public string Mime { get; } = mime;
    }

    /// <summary>
    /// The single funnel every /internal/tool return goes through, so the
    /// 1,500-character cap cannot be skipped by a refusal, an unknown-tool
    /// message, or exception text.
    /// </summary>
    public static Dictionary<string, object?> ToolReply(bool ok, string? text, Dictionary<string, object?>? image = null)
    {
        var reply = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["text"] = CapToolResult(text ?? "None"),
        };
        if (image is not null) reply["image"] = image;
        return reply;
    }

    /// <summary>The uniform shape of every entry in `ToolHandlers`.</summary>
    public delegate Task<object?> ToolHandler(JsonObject args);

    /// <summary>
    /// name → handler, for every tool in all three parts. Sync Python tools are
    /// wrapped with `Sync`, async ones with `Async`; see the header comment.
    /// </summary>
    public static readonly Dictionary<string, ToolHandler> ToolHandlers = BuildToolHandlers();

    private static ToolHandler Sync(Func<JsonObject, object?> f) => a => Task.FromResult(f(a));

    private static Dictionary<string, ToolHandler> BuildToolHandlers() => new()
    {
        // Sessions, steering, runs (Part 2)
        ["list_sessions"] = Sync(a => ToolListSessions(a)),
        ["session_detail"] = Sync(a => ToolSessionDetail(a)),
        ["list_projects"] = Sync(a => ToolListProjects(a)),
        ["steer_session"] = async a => await ToolSteerSession(a),
        ["answer_dialog"] = async a => await ToolAnswerDialog(a),
        ["spawn_run"] = async a => await ToolSpawnRun(a),
        ["create_project"] = async a => await ToolCreateProject(a),
        ["run_status"] = Sync(a => ToolRunStatus(a)),
        ["cancel_run"] = async a => await ToolCancelRun(a),
        ["open_in_browser"] = async a => await ToolOpenInBrowser(a),
        ["open_in_terminal"] = async a => await ToolOpenInTerminal(a),
        ["enable_session_inbox"] = async a => await ToolEnableSessionInbox(a),
        // Builds and documents (Part 3)
        ["start_build"] = async a => await ToolStartBuild(a),
        ["build_status"] = Sync(a => ToolBuildStatus(a)),
        ["run_command"] = async a => await ToolRunCommand(a),
        ["review_document"] = Sync(a => ToolReviewDocument(a)),
        ["approve_document"] = Sync(a => ToolApproveDocument(a)),
        // Reading the code itself
        ["repo_overview"] = async a => await ToolRepoOverview(a),
        ["search_repo"] = async a => await ToolSearchRepo(a),
        ["read_file"] = async a => await ToolReadFile(a),
        ["open_in_editor"] = async a => await ToolOpenInEditor(a),
        // The web (look_at_page returns a string or a ToolImage)
        ["read_page"] = async a => await ToolReadPage(a),
        ["look_at_page"] = async a => await ToolLookAtPage(a),
        // The user's own screen (look_at_screen returns a string or a ToolImage)
        ["look_at_screen"] = async a => await ToolLookAtScreen(a),
        ["what_is_on_screen"] = async a => await ToolWhatIsOnScreen(a),
        // GitHub, usage, connections
        ["github_repo"] = async a => await ToolGithubRepo(a),
        ["usage_status"] = Sync(a => ToolUsageStatus(a)),
        ["connections"] = Sync(a => ToolConnections(a)),
        // Memory
        ["remember"] = Sync(a => ToolRemember(a)),
        ["recall"] = Sync(a => ToolRecall(a)),
        ["project_note"] = Sync(a => ToolProjectNote(a)),
        ["write_journal"] = Sync(a => ToolWriteJournal(a)),
    };

    /// <summary>Tools that may only run while the user is the one talking.</summary>
    public static readonly HashSet<string> ActingTools =
    [
        "steer_session",
        // It starts a process. Same gate as steer_session, for a stronger
        // reason: a steer lands in a window the user can see, a spawn does not.
        "spawn_run",
        // It types into a window on the user's machine and takes their focus.
        // The origin gate is the only thing between hostile text and a keystroke.
        "answer_dialog",
        // It writes to the user's own Claude Code configuration.
        "enable_session_inbox",
        // Both put a window on the user's screen and take their focus to do it.
        "open_in_browser", "open_in_terminal",
        // create_project writes a directory; cancel_run kills a process.
        // run_status is deliberately NOT here: it reads and says, nothing more.
        "create_project", "cancel_run",
        // start_build spawns an unattended process for hours; run_command puts a
        // command on a real shell; approve_document is the gate the whole build
        // process hangs off. build_status / review_document only read.
        "start_build", "run_command", "approve_document",
        // It opens an application window and takes the user's focus. The three
        // repo readers are deliberately NOT here.
        "open_in_editor",
        // They reach out to a network address built from a model's output. Only
        // the user may point JARVIS at a host.
        "read_page", "look_at_page",
        // A camera pointed at the user's life: it fires when he asks, never off
        // a watcher's turn.
        "look_at_screen", "what_is_on_screen",
        // It reaches out to GitHub with a query built from a model's output.
        "github_repo",
        // These three WRITE. A watcher-origin turn must never reach them, or text
        // from somebody else's transcript could plant a "fact".
        "remember", "project_note", "write_journal",
    ];

    // The acting tools JARVIS says out loud BEFORE they happen: each stages its
    // work, the staged-steer/dialog performer reads it back once the mouth is
    // free, and a cancel window follows. Every OTHER acting tool performs inside
    // its handler with nothing spoken first — those are what
    // `UntrustedContentRefusal` closes.
    public static readonly HashSet<string> ReadBackTools = ["steer_session", "answer_dialog", "run_command"];

    // ---------------------------------------------------------------------------
    // Which tools put somebody else's words in front of the brain
    // ---------------------------------------------------------------------------
    //
    // EVERY reader taints, and the value is what the user hears in the refusal.
    // A README is written by a stranger exactly as a web page is, so "what's in
    // that repo?" → `read_file` returns an attacker's README → same turn,
    // origin "user" → `spawn_run` was the shortest path to an unattended
    // `--dangerously-skip-permissions`. Marking happens in /internal/tool after
    // the handler returns, so a reader added later cannot forget to do it.
    public static readonly Dictionary<string, string> TaintingTools = new()
    {
        // Repository files. A source comment or a README can carry an instruction.
        ["read_file"] = "a file in one of your projects",
        ["search_repo"] = "a file in one of your projects",
        ["repo_overview"] = "a file in one of your projects",
        ["review_document"] = "a document in one of your projects",
        // Other people's conversations, and what they told their sessions.
        ["list_sessions"] = "another session's transcript",
        ["session_detail"] = "another session's transcript",
        // A run's own output: an unattended process that has been reading files.
        ["run_status"] = "the output of a run",
        ["build_status"] = "the output of a run",
        // The open web, and a repository description on GitHub.
        ["read_page"] = "a web page",
        ["look_at_page"] = "a web page",
        ["github_repo"] = "a GitHub repository",
        // The user's own desk — his words, a website's, another session's.
        ["look_at_screen"] = "what is on your screen",
        ["what_is_on_screen"] = "what is on your screen",
    };

    // The other half of the partition, each with the reason it is exempt. Held
    // exhaustive against ToolHandlers by the Python tests, so a new tool has to
    // make this decision on purpose instead of inheriting "clean".
    public static readonly Dictionary<string, string> TaintExemptTools = new()
    {
        ["list_projects"] =
            "it emits project names and directory paths off the session roster " +
            "and no file content, no transcript text and no page — and it is how " +
            "the brain resolves a project name before doing anything at all",
        ["usage_status"] =
            "it reports JARVIS's own subscription usage, computed here from " +
            "his own store; there is no foreign text in it",
        ["connections"] =
            "it reports the servers the USER declared in his own " +
            "connections.json, which is his file and not a stranger's",
        ["recall"] =
            "it reads JARVIS's own memory, which is already `@`-imported into " +
            "every turn as trusted system text — tainting on read would be " +
            "theatre, and what actually protects it is that the WRITERS are " +
            "gated",
        // The acting tools. They change something; they do not read.
        ["spawn_run"] = "it starts work, it does not read",
        ["steer_session"] = "it sends a message, it does not read",
        ["answer_dialog"] = "it presses one key, it does not read",
        ["cancel_run"] = "it stops a process, it does not read",
        ["create_project"] = "it makes a directory, it does not read",
        ["start_build"] = "it starts a build, it does not read",
        ["approve_document"] = "it records an approval, it does not read",
        ["run_command"] = "it runs a command, it does not read",
        ["open_in_browser"] = "it opens a window, it does not read",
        ["open_in_terminal"] = "it opens a window, it does not read",
        ["open_in_editor"] = "it opens a window, it does not read",
        ["enable_session_inbox"] = "it edits a settings file, it does not read",
        ["remember"] = "it writes a memory, it does not read",
        ["project_note"] = "it writes a note, it does not read",
        ["write_journal"] = "it writes the journal, it does not read",
    };

    // Acting tools that only ever bring back MORE content to read. Gated on
    // ORIGIN but not again once the turn is tainted: "search for it, then read
    // that page" is the entire feature, and WebFetch (the CLI's own) cannot be
    // gated here anyway. The two screen tools read the user's own desk.
    public static readonly HashSet<string> UntrustedReadingTools =
        ["read_page", "look_at_page", "github_repo", "look_at_screen", "what_is_on_screen"];

    // The one acting tool that survives a tainted turn. `answer_dialog`'s payload
    // is a single keystroke, so there is no attacker text for it to carry, and
    // refusing it would break "what's it asking?" → "… allow it".
    public static readonly HashSet<string> TaintExemptActing = ["answer_dialog"];

    // Writers whose output outlives the turn: memory files are `@`-imported into
    // every future turn as TRUSTED system text. These three, and only these, are
    // gated on the GENERATION rather than the turn — see WriterUntrustedSource.
    public static readonly HashSet<string> MemoryWriters = ["remember", "project_note", "write_journal"];

    /// <summary>
    /// What foreign text stands between this tool and a write, or null. For
    /// every acting tool but the memory writers this is the TURN's taint. A
    /// memory writer's output is loaded as trusted text in every LATER
    /// generation, so what matters is whether anything in the CONTEXT composing
    /// it came from outside — otherwise turn N reads a poisoned page and turn
    /// N+1's `remember` goes through. A generation that has read one web page
    /// will not write a memory until it rotates; the refusal says so.
    /// </summary>
    public static string? WriterUntrustedSource(string tool)
    {
        var brain = BrainInstance;
        string? source = null;
        try
        {
            source = brain?.TurnUntrustedSource;
            if (source is null && brain is not null && brain.TurnIsTainted)
                source = "a web page";      // a brain that only knows the boolean
        }
        catch { }
        if (source is null && MemoryWriters.Contains(tool))
            source = GenerationUntrustedSource();
        return source;
    }

    /// <summary>
    /// The sentence refusing an unsupervised action in a turn that has read
    /// something JARVIS did not write, or null if the call may proceed. A
    /// wrapper label is a warning, not a wall; this is the wall. Per-TURN (the
    /// user speaking again re-opens it) except for the memory writers, which are
    /// per-generation. `source` names what did it.
    /// </summary>
    public static string? UntrustedContentRefusal(string tool, bool readUntrusted, string source = "something I read")
    {
        if (!readUntrusted) return null;
        if (!ActingTools.Contains(tool)) return null;
        if (UntrustedReadingTools.Contains(tool) || TaintExemptActing.Contains(tool)) return null;
        if (MemoryWriters.Contains(tool))
            return $"untrusted_content_in_this_session — I've had {source} in " +
                   "front of me this session, sir, and what I write down I keep " +
                   "for good, so I'll not write that one. Say it again once " +
                   "I've tidied my context up, and I'll keep it.";
        return $"untrusted_content_in_this_turn — I've had {source} in front of " +
               "me this turn, sir, so I'll not act on it; ask me again and I " +
               "will.";
    }

    /// <summary>
    /// Tell the brain this turn now holds somebody else's words, and whose.
    /// Called from /internal/tool for every tool in `TaintingTools`, AFTER the
    /// handler has run. Never throws.
    /// </summary>
    public static void MarkTheTurnUntrusted(string tool)
    {
        if (!TaintingTools.TryGetValue(tool, out var source) || string.IsNullOrEmpty(source)) return;
        try
        {
            BrainInstance?.MarkUntrustedContent(source);
        }
        catch (Exception e)
        {
            Log.LogWarning($"could not mark the turn as having read {source}: {e.Message}");
        }
    }

    /// <summary>
    /// Constant-time bearer check. A malformed Authorization header must yield a
    /// clean 401, never a 500.
    /// </summary>
    public static bool BearerTokenMatches(string headerValue, string expected)
    {
        if (!headerValue.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        try
        {
            var supplied = Encoding.UTF8.GetBytes(headerValue[7..]);
            var want = Encoding.UTF8.GetBytes(expected);
            return CryptographicOperations.FixedTimeEquals(supplied, want);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Loopback-only tool channel for the brain's MCP child. Bound to the bearer
    /// token in &lt;data&gt;/jarvis/tool-token. Acting tools are gated here, in
    /// the server, not in the prompt: a hostile string in somebody else's
    /// transcript must not be able to make JARVIS act.
    /// </summary>
    public static async Task<IResult> InternalTool(HttpContext ctx)
    {
        var expected = DataPaths.EnsureToolToken();
        var supplied = ctx.Request.Headers.Authorization.ToString();
        if (!BearerTokenMatches(supplied, expected))
            return Detail(401, "bad token");

        JsonNode? body;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            body = JsonNode.Parse(await reader.ReadToEndAsync());
        }
        catch (Exception)
        {
            return Json(ToolReply(false, "Unreadable request."));
        }
        if (body is not JsonObject bodyObj)
            return Json(ToolReply(false, "Request body must be an object."));

        // str(body.get("tool", ""))
        string tool;
        if (!bodyObj.TryGetPropertyValue("tool", out var toolNode)) tool = "";
        else tool = PyStr(Py.ToClr(toolNode));

        // body.get("arguments") or {}
        bodyObj.TryGetPropertyValue("arguments", out var argsNode);
        JsonObject args;
        if (!IsTruthy(Py.ToClr(argsNode))) args = new JsonObject();
        else if (argsNode is JsonObject ao) args = (JsonObject)ao.DeepClone();
        else return Json(ToolReply(false, "Arguments must be an object."));

        if (!ToolHandlers.TryGetValue(tool, out var handler))
            return Json(ToolReply(false, $"Unknown tool: {tool}"));

        if (ActingTools.Contains(tool))
        {
            var origin = BrainInstance?.CurrentOrigin;
            if (origin != "user")
                return Json(ToolReply(false,
                    "not_allowed_from_event — I can only do that when you ask " +
                    "me to, sir, not off my own back."));
            // The origin gate is not enough: the poisoned README arrives DURING
            // the very turn the user asked about the repository, so its origin is
            // "user". And for a memory writer the TURN is not enough either.
            var source = WriterUntrustedSource(tool);
            var refusal = UntrustedContentRefusal(tool, source is not null, source: source ?? "something I read");
            if (refusal is not null)
            {
                Log.LogWarning($"refused {tool}: {source} was read in this " +
                               (MemoryWriters.Contains(tool) ? "session" : "turn"));
                return Json(ToolReply(false, refusal));
            }
        }

        object? result;
        try
        {
            result = await handler(args);
        }
        catch (Exception e)
        {
            Log.LogError(e, $"tool {tool} failed: {e.Message}");
            return Json(ToolReply(false, $"That tool failed: {e.Message}"));
        }
        // The turn now holds whatever that tool brought back. Marked HERE, once,
        // so `TaintingTools` is the entire decision, in one readable place.
        MarkTheTurnUntrusted(tool);
        if (result is ToolImage img)
            return Json(ToolReply(true, img.Text, image: new Dictionary<string, object?>
            {
                ["data"] = Convert.ToBase64String(img.Png),
                ["mimeType"] = img.Mime,
            }));
        return Json(ToolReply(true, PyStr(result)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Routes (server.py lines 1850–2620)
    // ═══════════════════════════════════════════════════════════════════════

    public static void MapRoutes1(WebApplication app)
    {
        app.MapGet("/api/health", () =>
            Json(new Dictionary<string, object?> { ["status"] = "online", ["name"] = "JARVIS", ["version"] = "0.1.0" }));

        // A POST, not a GET, because it spends the user's Fish Audio quota — and
        // a GET is the one method OriginGuard cannot cover.
        app.MapPost("/api/tts-test", async () =>
        {
            var audio = await SynthesizeSpeech("Testing audio, sir.");
            if (audio is { Length: > 0 })
                return Json(new Dictionary<string, object?> { ["audio"] = Convert.ToBase64String(audio) });
            return Json(new Dictionary<string, object?> { ["audio"] = null, ["error"] = "TTS failed" });
        });

        app.MapGet("/api/usage", () =>
        {
            var uptime = (long)(Py.Now() - SessionStart);
            Dictionary<string, object?> WithCost(Dictionary<string, long> t)
            {
                var d = t.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                d["cost_usd"] = Math.Round(CostFromTokens(t["input_tokens"], t["output_tokens"]), 4, MidpointRounding.ToEven);
                return d;
            }
            Dictionary<string, object?> session;
            lock (SessionTokens) session = SessionTokens.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            session["uptime_seconds"] = uptime;
            return Json(new Dictionary<string, object?>
            {
                ["session"] = session,
                ["today"] = WithCost(GetUsageForPeriod(86400)),
                ["week"] = WithCost(GetUsageForPeriod(86400 * 7)),
                ["month"] = WithCost(GetUsageForPeriod(86400 * 30)),
                ["all_time"] = WithCost(GetUsageForPeriod(null)),
            });
        });

        // How much of the subscription's windows is gone, and when we last
        // looked. `measured: false` and `utilization: null` are normal answers
        // before the brain has taken a turn, not errors.
        app.MapGet("/api/usage/limits", () => Json(UsageStore.Snapshot()));

        // What each conversation on this machine has spent. A failure is
        // answered AS a failure: `measured: false` with a 200 would be
        // indistinguishable from a machine that has never been used.
        app.MapGet("/api/usage/sessions", async () =>
        {
            try
            {
                return Json(await Task.Run(() => UsageScanSnapshot()));
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "usage scan failed");
                return Results.Json(new Dictionary<string, object?>
                {
                    ["measured"] = false, ["sessions"] = new List<object>(), ["daily"] = new List<object>(),
                    ["error"] = $"could not read the transcripts: {e.Message}",
                }, Py.Json, statusCode: 503);
            }
        });

        // -- Runs: the single source of truth for Claude Code executions --------

        app.MapGet("/api/runs/stats", (string? period) => Json(RunStore.Stats(period ?? "day")));

        app.MapGet("/api/runs", (string? status, string? project, int? limit, double? before) =>
        {
            var statuses = (status ?? "").Split(',').Where(s => s.Length > 0).ToList();
            // Clamp both ends: SQLite treats `LIMIT -1` as unlimited, so a
            // negative value must not reach the query unbounded.
            var lim = Math.Max(1, Math.Min(limit ?? 50, 200));
            return Json(new Dictionary<string, object?>
            {
                ["runs"] = RunStore.ListRuns(
                    status: statuses.Count > 0 ? statuses : null,
                    project: string.IsNullOrEmpty(project) ? null : project,
                    limit: lim, before: before),
            });
        });

        app.MapGet("/api/runs/{run_id}", (string run_id) =>
        {
            var run = RunStore.GetRun(run_id);
            if (run is null || run.Count == 0)
                return Results.Json(new Dictionary<string, object?> { ["error"] = "Run not found" }, Py.Json, statusCode: 404);
            return Json(new Dictionary<string, object?> { ["run"] = run });
        });

        // -- Memory: read-only window onto the user's folder ---------------------

        // Always 200. An absent folder is an empty memory, not a missing route.
        app.MapGet("/api/memory", () => Json(new Dictionary<string, object?>
        {
            ["path"] = DataPaths.BrainHome(),
            ["index"] = JarvisMemory.IndexEntries(),
            ["memories"] = JarvisMemory.MemoryEntries(),
            ["projects"] = JarvisMemory.ProjectEntries(),
            ["journal"] = JarvisMemory.JournalEntriesMeta(),
            ["latest_journal_slug"] = JarvisMemory.LatestJournalSlug(),
        }));

        // One file, raw. `slug` comes from a URL and is never trusted: every
        // containment decision is `JarvisMemory.DocPath`'s. A rejected slug is a
        // 404 like any other miss — which probes were traversal attempts is
        // information we do not hand out.
        app.MapGet("/api/memory/{kind}/{slug}", (string kind, string slug) =>
        {
            var path = MemoryDocKinds.Contains(kind) ? JarvisMemory.DocPath(kind, slug) : null;
            if (path is null)
                return Results.Json(new Dictionary<string, object?> { ["error"] = "Not found" }, Py.Json, statusCode: 404);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new Dictionary<string, object?> { ["error"] = "Not found" }, Py.Json, statusCode: 404);
            }
            return Json(new Dictionary<string, object?> { ["slug"] = slug, ["text"] = text });
        });

        // -- The review surface ---------------------------------------------------

        // The master list: projects with something to read, newest first.
        // Rescans the way /api/projects does, so a project created this session
        // appears without a restart.
        app.MapGet("/api/specs", async () =>
        {
            try
            {
                CachedProjects = await ScanProjects();
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "/api/specs project scan failed");
            }
            return Json(new Dictionary<string, object?> { ["projects"] = await Task.Run(() => SpecsProjects()) });
        });

        // One document, numbered — the same numbering JARVIS reads back.
        // `project`, `path` and `root` arrive from a URL and none is trusted; a
        // refusal is a 404 exactly like a miss.
        app.MapGet("/api/specs/doc", async (string? project, string? path, string? root) =>
        {
            project ??= "";
            var directory = ProjectPathOrNone(project, root ?? "");
            if (directory is null)
                return Results.Json(new Dictionary<string, object?> { ["error"] = "Not found" }, Py.Json, statusCode: 404);
            var document = await Task.Run(() => Specs.ReadDocument(directory, path ?? ""));
            if (document is null)
                return Results.Json(new Dictionary<string, object?> { ["error"] = "Not found" }, Py.Json, statusCode: 404);
            var reply = new Dictionary<string, object?> { ["project"] = project, ["root"] = directory };
            foreach (var (k, v) in document) reply[k] = v;
            return Json(reply);
        });

        // Live hints for the SPECS tab. The message carries NO content: "something
        // moved" is the whole payload and the client reconciles against
        // /api/specs, so a late, doubled or lost hint never leaves a stale
        // document on screen looking current.
        app.Map("/ws/specs", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            double interval;
            try
            {
                interval = double.Parse(Py.Getenv("JARVIS_SPECS_POLL") ?? SpecsPollDefault.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                interval = SpecsPollDefault;
            }
            interval = Math.Max(0.05, interval);
            using var closed = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            // Nothing is expected from the client; this only notices it leaving.
            _ = DrainUntilClosed(ws, closed);
            try
            {
                var previous = await Task.Run(() => SpecsFingerprint());
                await SendJson(ws, new Dictionary<string, object?> { ["type"] = "hello" }, closed.Token);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), closed.Token);
                    var current = await Task.Run(() => SpecsFingerprint());
                    if (current != previous)
                    {
                        previous = current;
                        await SendJson(ws, new Dictionary<string, object?> { ["type"] = "changed" }, closed.Token);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidOperationException)
            {
            }
            catch (Exception e)
            {
                Log.LogWarning($"/ws/specs error: {e.Message}");
            }
        });

        // Every Claude Code conversation on this machine. The snapshot is the
        // source of truth; /ws/sessions is only a hint. `SnapshotOrEmpty()`, never
        // the raw watcher snapshot: JARVIS's own `claude -p` runs register in the
        // roster too, and counting them made every tab disagree.
        app.MapGet("/api/sessions", (string? state) =>
        {
            var snap = SnapshotOrEmpty();
            var wanted = (state ?? "").Split(',').Where(s => s.Length > 0).ToHashSet();
            var rows = snap.Sessions
                .Where(s => wanted.Count == 0 || wanted.Contains(s.State))
                .Select(s => SessionWatch.SessionToDict(s)).ToList();
            var projects = new Dictionary<string, List<string>>();
            foreach (var row in rows)
            {
                var p = PyStr(DGet(row, "project"));
                if (!projects.TryGetValue(p, out var list)) projects[p] = list = [];
                list.Add(PyStr(DGet(row, "session_id")));
            }
            return Json(new Dictionary<string, object?>
            {
                ["sessions"] = rows, ["projects"] = projects, ["taken_at"] = snap.TakenAt,
            });
        });

        app.MapPost("/internal/tool", (Delegate)((HttpContext ctx) => InternalTool(ctx)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Small shared helpers (usable by every part)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>`return {...}` from a FastAPI handler.</summary>
    public static IResult Json(object? value, int statusCode = 200) =>
        Results.Json(value, Py.Json, statusCode: statusCode);

    /// <summary>`raise HTTPException(status_code, detail)`.</summary>
    public static IResult Detail(int statusCode, object? detail) =>
        Results.Json(new Dictionary<string, object?> { ["detail"] = detail }, Py.Json, statusCode: statusCode);

    /// <summary>`await ws.send_json(msg)` for a socket nobody else sends on.</summary>
    public static async Task SendJson(WebSocket ws, object? msg, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(Py.Dumps(msg));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Read (and discard) until the client closes, then cancel `closed`.</summary>
    public static async Task DrainUntilClosed(WebSocket ws, CancellationTokenSource closed)
    {
        var buf = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, closed.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        try { closed.Cancel(); } catch { }
    }

    /// <summary>
    /// `mapping.get(key)` over whatever shape a dict arrived in:
    /// Dictionary&lt;string, object?&gt;, any IDictionary, a JsonObject (converted
    /// with Py.ToClr), or an object with a matching PascalCase property.
    /// </summary>
    public static object? DGet(object? container, string key)
    {
        switch (container)
        {
            case null: return null;
            case IDictionary<string, object?> d: return d.TryGetValue(key, out var v) ? v : null;
            case JsonObject o: return o.TryGetPropertyValue(key, out var n) ? Py.ToClr(n) : null;
            case System.Collections.IDictionary id: return id.Contains(key) ? id[key] : null;
        }
        var prop = container.GetType().GetProperty(SnakeToPascal(key));
        return prop?.GetValue(container);
    }

    public static Dictionary<string, object?> ToDict(object? value) => value switch
    {
        Dictionary<string, object?> d => d,
        IDictionary<string, object?> d => new Dictionary<string, object?>(d),
        JsonObject o => Py.ToClr(o) as Dictionary<string, object?> ?? new(),
        _ => new Dictionary<string, object?>(),
    };

    public static string SnakeToPascal(string key) =>
        string.Concat(key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>Python truthiness.</summary>
    public static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        JsonValue jv => IsTruthy(Py.ToClr(jv)),
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    public static double? ToDouble(object? v)
    {
        switch (v)
        {
            case null: return null;
            case double d: return d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case decimal m: return (double)m;
            case JsonNode n: return ToDouble(Py.ToClr(n));
            case string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p): return p;
            default: return null;
        }
    }

    /// <summary>Python's `str(x)` for the values that cross this code.</summary>
    public static string PyStr(object? v) => v switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => PyFloat(d),
        float f => PyFloat(f),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        JsonNode n => PyStr(Py.ToClr(n)),
        _ => v.ToString() ?? "",
    };

    /// <summary>Python's float repr: "72.0", "40.7128", "inf", "nan".</summary>
    public static string PyFloat(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
        return s;
    }

    /// <summary>`s[:n]`.</summary>
    public static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>`str.capitalize()`.</summary>
    public static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
