using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// What the CLI last told us about the subscription's limits (usage_store.py).
///
/// JARVIS runs on a Claude Code subscription, not on an API key, so there is
/// no spend to report. What there IS — and what the user asks for out loud
/// ("what's my session limit") — is how much of the five-hour and seven-day
/// windows has been used. That number arrives in exactly one place: a
/// `rate_limit_event` on the brain's stdout. This module is where it is kept.
///
///   Record(rate_limit_info)   persist one observation (called by the brain)
///   Latest()                  the raw stored record, or null
///   Snapshot()                what /api/usage returns
///
/// THE ONE RULE: absence is a state, and it is preserved as one. A window
/// nobody has observed comes back with `utilization: null` — never 0, never a
/// full green gauge. Every reading is stamped with when it was taken, per window.
///
/// ENCODING: `utilization` is a FRACTION in the payload (0.78 means 78%). A
/// fraction cannot exceed 1, so a value above 1 is read as an already-percent
/// encoding rather than doubled up into 5500%.
/// </summary>
public static partial class UsageStore
{
    public static readonly ILogger Log = Py.Log("jarvis.usage_store");

    // How old a reading may be before the UI must say so. A five-hour window can
    // move a long way in half an hour of work, so half an hour is the line.
    public const int StaleAfterSec = 30 * 60;

    // The windows the CLI reports today, in the order a human cares about them.
    // Both are always present in a snapshot even when never observed.
    public static readonly string[] KnownWindows = ["five_hour", "seven_day"];

    public static readonly Dictionary<string, string> WindowLabels = new()
    {
        ["five_hour"] = "5-hour session",
        ["seven_day"] = "7-day week",
        ["seven_day_opus"] = "7-day Opus",
    };

    public static string WindowLabel(string key) =>
        WindowLabels.TryGetValue(key, out var l) ? l : key.Replace("_", " ");

    // ── coercion: anything that is not plainly a number stays null ──────────

    private static double? Number(JsonNode? raw)
    {
        if (raw is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d))
            return d;
        return null;
    }

    /// <summary>A utilisation fraction as a 0–100 percentage, or null if it isn't one.</summary>
    public static double? Percent(JsonNode? raw)
    {
        if (Number(raw) is not double value) return null;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return null;
        var pct = value <= 1.0 ? value * 100.0 : value;
        return Math.Round(Math.Min(pct, 100.0), 1);
    }

    /// <summary>
    /// A percentage that Record has ALREADY normalised, validated but never
    /// rescaled. Percent() reads a value &lt;= 1 as a fraction, which is wrong
    /// for anything read back from the store: a window truthfully at 1.0% would
    /// become a full 100% gauge.
    /// </summary>
    public static double? StoredPercent(JsonNode? raw)
    {
        if (Number(raw) is not double value) return null;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return null;
        return Math.Round(Math.Min(value, 100.0), 1);
    }

    /// <summary>
    /// An epoch-seconds timestamp, or null. Milliseconds are rescaled so a
    /// future CLI change reads as a time rather than as the year 58000.
    /// </summary>
    public static double? Epoch(JsonNode? raw)
    {
        if (Number(raw) is not double value) return null;
        if (double.IsNaN(value) || value <= 0 || double.IsInfinity(value)) return null;
        if (value > 1e11) value /= 1000.0;
        return value;
    }

    public static string Text(JsonNode? raw) => StreamParser.IsString(raw, out var s) ? s : "";

    // ── reading and writing the file ────────────────────────────────────────

    /// <summary>
    /// The stored observation, or null if there has never been one. A missing,
    /// unreadable or corrupt file is "no observation" — the dashboard must
    /// degrade to "not measured", never to an error or to zeroes.
    /// </summary>
    public static JsonObject? Latest()
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(File.ReadAllText(DataPaths.UsagePath(), Encoding.UTF8));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e)
        {
            Log.LogWarning("usage_store: unreadable observation ignored ({Error})", e.Message);
            return null;
        }
        if (body is not JsonObject o || o["windows"] is not JsonObject)
        {
            Log.LogWarning("usage_store: observation has the wrong shape; ignoring it");
            return null;
        }
        return o;
    }

    /// <summary>
    /// Replace the file atomically (indent 2, sorted keys). A half-written file
    /// would read as corrupt on the very next request.
    /// </summary>
    public static void Write(JsonObject record)
    {
        var path = DataPaths.UsagePath();
        try
        {
            var text = SortKeys(record)!.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            Py.WriteAtomic(path, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a usage reading must never take a turn down with it.
            Log.LogWarning("usage_store: could not persist the observation ({Error})", e.Message);
        }
    }

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, SortKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(SortKeys).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonNode? Val(double? d) => d is double x ? JsonValue.Create(x) : null;

    /// <summary>
    /// Persist one `rate_limit_info` payload. Returns the stored record.
    ///
    /// Windows are merged, newest reading per window wins, and each keeps the
    /// timestamp of the event it actually came from — an event that names only
    /// `five_hour` must not re-stamp last hour's `seven_day` reading as current.
    /// </summary>
    public static Dictionary<string, object?>? Record(JsonNode? info, double? now = null)
    {
        if (info is not JsonObject inf) return null;
        var when = now ?? Py.Now();

        var stored = Latest() ?? new JsonObject();
        var windows = new JsonObject();
        if (stored["windows"] is JsonObject sw)
            foreach (var (k, v) in sw)
                if (v is JsonObject) windows[k] = v.DeepClone();

        var status = Text(inf["status"]);
        var named = Text(inf["rateLimitType"]);

        var incoming = new List<(string Key, JsonObject Window)>();
        if (inf["unifiedWindows"] is JsonObject unified)
        {
            foreach (var (key, raw) in unified)
            {
                if (raw is not JsonObject r) continue;
                var ws = Text(r["status"]);
                incoming.Add((key, new JsonObject
                {
                    ["utilization"] = Val(Percent(r["utilization"])),
                    ["resets_at"] = Val(Epoch(r["resetsAt"])),
                    ["status"] = ws.Length > 0 ? ws : status,
                    ["observed_at"] = when,
                }));
            }
        }

        // A rejection often names its window at the top level and sends no
        // unifiedWindows at all. That is still a real observation about that
        // window — its status and its reset time — so it is kept.
        if (named.Length > 0 && !incoming.Any(i => i.Key == named))
        {
            incoming.Add((named, new JsonObject
            {
                ["utilization"] = Val(Percent(inf["utilization"])),
                ["resets_at"] = Val(Epoch(inf["resetsAt"])),
                ["status"] = status,
                ["observed_at"] = when,
            }));
        }

        foreach (var (key, w) in incoming) windows[key] = w;

        var storedRecord = new JsonObject
        {
            ["observed_at"] = when,
            ["status"] = status,
            ["rate_limit_type"] = named,
            ["windows"] = windows,
        };
        Write(storedRecord);
        return Py.ToClr(storedRecord) as Dictionary<string, object?>;
    }

    // ── the shape the dashboard reads ───────────────────────────────────────

    public static Dictionary<string, object?> WindowView(string key, JsonNode? raw, double now)
    {
        var r = raw as JsonObject ?? new JsonObject();
        var observedAt = Epoch(r["observed_at"]);
        var resetsAt = Epoch(r["resets_at"]);
        double? age = observedAt is null ? null : Math.Max(0.0, now - observedAt.Value);
        return new Dictionary<string, object?>
        {
            ["key"] = key,
            ["label"] = WindowLabel(key),
            ["utilization"] = StoredPercent(r["utilization"]),
            ["resets_at"] = resetsAt,
            ["status"] = Text(r["status"]),
            ["observed_at"] = observedAt,
            ["age_sec"] = age,
            ["stale"] = age is not null && age > StaleAfterSec,
            // The window rolled over after we last looked: whatever we hold
            // describes a window that no longer exists.
            ["expired"] = resetsAt is not null && resetsAt <= now,
        };
    }

    /// <summary>The usage picture, honest about everything it does not know.</summary>
    public static Dictionary<string, object?> Snapshot(double? now = null)
    {
        var at = now ?? Py.Now();
        var stored = Latest() ?? new JsonObject();
        var windows = stored["windows"] as JsonObject ?? new JsonObject();

        var keys = KnownWindows.ToList();
        keys.AddRange(windows.Select(kv => kv.Key).Where(k => !KnownWindows.Contains(k)));
        var views = keys.Select(k => WindowView(k, windows[k], at)).ToList();

        var observed = views.Select(v => v["observed_at"] as double?).Where(x => x is not null)
            .Select(x => x!.Value).ToList();
        double? newest = observed.Count > 0 ? observed.Max() : null;
        double? age = newest is null ? null : Math.Max(0.0, at - newest.Value);

        return new Dictionary<string, object?>
        {
            ["measured"] = newest is not null,
            ["observed_at"] = newest,
            ["age_sec"] = age,
            ["stale"] = age is not null && age > StaleAfterSec,
            ["stale_after_sec"] = StaleAfterSec,
            ["status"] = Text(stored["status"]),
            ["windows"] = views,
        };
    }
}
