using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jplus;

/// <summary>
/// Pure parsing of `claude -p --output-format stream-json` output (stream_parser.py).
///
/// No I/O. The CLI's event vocabulary is not a stable contract, so
/// unrecognized events are preserved verbatim rather than interpreted.
/// Every extractor is defensive: a non-object where an object was expected,
/// missing keys and non-numeric values all yield defaults — a throw here
/// would kill a live run.
/// </summary>
public static partial class StreamParser
{
    public const int SummaryMax = 160;

    /// <summary>Parse one JSONL line. Returns null for blank or malformed lines, or non-objects.</summary>
    public static JsonObject? ParseLine(string? line)
    {
        line = (line ?? "").Trim();
        if (line.Length == 0) return null;
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException
                                   or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The event's top-level `type`, stored verbatim.</summary>
    public static string EventKind(JsonNode? ev)
    {
        var t = Get(ev, "type");
        return PyTruthy(t) ? PyStr(t) : "unknown";
    }

    public static Dictionary<string, object?> ExtractInitMetadata(JsonNode? ev) => new()
    {
        ["model"] = OrEmpty(Get(ev, "model")),
        ["cwd"] = OrEmpty(Get(ev, "cwd")),
    };

    /// <summary>Python `float(value)` with a fallback for anything it would reject.</summary>
    public static double AsFloat(JsonNode? value, double @default = 0.0)
    {
        if (value is not JsonValue v) return @default;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return v.TryGetValue<double>(out var d) ? d : @default;
            case JsonValueKind.True: return 1.0;
            case JsonValueKind.False: return 0.0;
            case JsonValueKind.String:
                var s = v.GetValue<string>().Trim();
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
                return s.ToLowerInvariant() switch
                {
                    "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
                    "-inf" or "-infinity" => double.NegativeInfinity,
                    "nan" or "+nan" or "-nan" => double.NaN,
                    _ => @default,
                };
            default: return @default;
        }
    }

    /// <summary>Python `int(value)` (floats truncate, strings must be integers) with a fallback.</summary>
    public static long AsInt(JsonNode? value, long @default = 0)
    {
        if (value is not JsonValue v) return @default;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d) && double.IsFinite(d)
                    && d < 9.2e18 && d > -9.2e18) return (long)Math.Truncate(d);
                return @default;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.String:
                var s = v.GetValue<string>().Trim().Replace("_", "");
                return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p)
                    ? p : @default;
            default: return @default;
        }
    }

    public static Dictionary<string, object?> ExtractResultMetrics(JsonNode? ev)
    {
        var usage = Get(ev, "usage") as JsonObject ?? new JsonObject();
        var result = Get(ev, "result");
        return new Dictionary<string, object?>
        {
            ["cost_usd"] = AsFloat(Get(ev, "total_cost_usd"), 0.0),
            ["input_tokens"] = AsInt(Get(usage, "input_tokens"), 0),
            ["output_tokens"] = AsInt(Get(usage, "output_tokens"), 0),
            ["cache_read_tokens"] = AsInt(Get(usage, "cache_read_input_tokens"), 0),
            ["cache_creation_tokens"] = AsInt(Get(usage, "cache_creation_input_tokens"), 0),
            ["num_turns"] = AsInt(Get(ev, "num_turns"), 0),
            ["result_text"] = IsString(result, out var rs) ? rs : "",
            ["is_error"] = PyTruthy(Get(ev, "is_error")),
        };
    }

    /// <summary>
    /// Per-turn token usage from one `assistant` event.
    ///
    /// The CLI reports usage on every assistant turn but the dollar cost only
    /// once, in the terminal `result` event — so the executor accumulates these
    /// to show tokens climbing live, and never estimates a price from them.
    /// </summary>
    public static Dictionary<string, long> ExtractAssistantUsage(JsonNode? ev)
    {
        var message = Get(ev, "message") as JsonObject ?? new JsonObject();
        var usage = Get(message, "usage") as JsonObject ?? new JsonObject();
        return new Dictionary<string, long>
        {
            ["input_tokens"] = AsInt(Get(usage, "input_tokens"), 0),
            ["output_tokens"] = AsInt(Get(usage, "output_tokens"), 0),
            ["cache_read_tokens"] = AsInt(Get(usage, "cache_read_input_tokens"), 0),
            ["cache_creation_tokens"] = AsInt(Get(usage, "cache_creation_input_tokens"), 0),
        };
    }

    /// <summary>One line describing what the agent is doing, for the live feed.</summary>
    public static string SummarizeAssistant(JsonNode? ev)
    {
        var message = Get(ev, "message") as JsonObject ?? new JsonObject();
        var content = Get(message, "content") as JsonArray ?? new JsonArray();
        foreach (var block in content)
        {
            if (block is not JsonObject b) continue;
            var type = Get(b, "type");
            if (IsString(type, out var ts) && ts == "text")
            {
                var raw = Get(b, "text");
                var text = CollapseWs(PyTruthy(raw) ? PyStr(raw) : "");
                if (text.Length > 0) return Cut(text, SummaryMax);
            }
            else if (IsString(type, out ts) && ts == "tool_use")
            {
                var nameNode = Get(b, "name");
                var name = PyTruthy(nameNode) ? PyStr(nameNode) : "tool";
                var input = Get(b, "input") as JsonObject ?? new JsonObject();
                JsonNode? target = null;
                foreach (var key in new[] { "file_path", "command", "pattern" })
                {
                    var t = Get(input, key);
                    if (PyTruthy(t)) { target = t; break; }
                }
                var targetText = CollapseWs(target is null ? "" : PyStr(target));
                return Cut($"{name}: {targetText}".Trim().TrimEnd(':'), SummaryMax);
            }
        }
        return "";
    }

    // --- Did the run actually do anything, or did it stop to ask? -------------
    //
    // A spawned run is one-shot and non-interactive: nobody is on the other end.
    // A run that ends its turn with a question has therefore stalled, and the
    // pipeline's exit-0 reading of that is "succeeded" — which is how "the site's
    // ready, sir" was said about an empty directory. The signal is conservative
    // in ONE direction: a run is only downgraded on POSITIVE evidence that it
    // changed nothing.

    // Tools that read, look, or think. Anything NOT in this set — including every
    // tool a future CLI adds, and every MCP tool — counts as the run having done
    // something, because the uncertain direction has to be "it worked".
    public static readonly HashSet<string> ReadOnlyTools =
    [
        "read", "glob", "grep", "ls", "todowrite", "todoread",
        "websearch", "webfetch", "skill", "notebookread",
        "listmcpresources", "readmcpresource", "exitplanmode",
        "askuserquestion", "bashoutput", "killshell", "slashcommand",
    ];

    public const string Ok = "ok";                  // it worked
    public const string Stalled = "stalled";        // it ended by asking a question, having changed nothing
    public const string NoChanges = "no_changes";   // it ended cleanly, but nothing shows it changed anything

    // Trailing decoration a model puts after the question mark.
    public const string Trailing = " \t\r\n*_`\"')】]>";

    /// <summary>(all text in this assistant turn, the tool names it invoked).</summary>
    public static (string Text, List<string> Tools) AssistantParts(JsonNode? ev)
    {
        var message = Get(ev, "message") as JsonObject ?? new JsonObject();
        var content = Get(message, "content") as JsonArray ?? new JsonArray();
        var texts = new List<string>();
        var tools = new List<string>();
        foreach (var block in content)
        {
            if (block is not JsonObject b) continue;
            var type = Get(b, "type");
            if (IsString(type, out var ts) && ts == "text")
            {
                var t = Get(b, "text");
                texts.Add(PyTruthy(t) ? PyStr(t) : "");
            }
            else if (IsString(type, out ts) && ts == "tool_use")
            {
                var n = Get(b, "name");
                tools.Add(PyTruthy(n) ? PyStr(n) : "");
            }
        }
        return (string.Join("\n", texts), tools);
    }

    /// <summary>
    /// True when the last thing said was a question. Only the END counts:
    /// "What is X? It is Y." is an answer, not a request for one.
    /// </summary>
    public static bool EndsWithQuestion(string? text) =>
        (text ?? "").TrimEnd(Trailing.ToCharArray()).EndsWith('?');

    /// <summary>True if any assistant turn used a tool that is not purely read-only.</summary>
    public static bool ChangedAnything(IEnumerable<JsonNode?> events)
    {
        foreach (var ev in events)
        {
            if (ev is not JsonObject || EventKind(ev) != "assistant") continue;
            var (_, tools) = AssistantParts(ev);
            foreach (var name in tools)
                if (!ReadOnlyTools.Contains(name.ToLowerInvariant())) return true;
        }
        return false;
    }

    /// <summary>
    /// Ok, Stalled or NoChanges for a run that exited zero.
    ///
    /// `events` must be the WHOLE stream: a partial view could miss the Write
    /// that proves work happened. A caller that cannot supply all of it passes
    /// nothing and takes Ok.
    /// </summary>
    public static string AssessOutcome(IEnumerable<JsonNode?> events, string? resultText = "")
    {
        var assistantEvents = events.Where(e => e is JsonObject && EventKind(e) == "assistant").ToList();
        if (assistantEvents.Count == 0)
            // No evidence either way. Never downgrade on an absence of data.
            return Ok;
        if (ChangedAnything(assistantEvents))
            return Ok;

        var final = (resultText ?? "").Trim();
        if (final.Length == 0)
        {
            for (int i = assistantEvents.Count - 1; i >= 0; i--)
            {
                var (text, _) = AssistantParts(assistantEvents[i]);
                if (text.Trim().Length > 0) { final = text.Trim(); break; }
            }
        }
        if (final.Length > 0 && EndsWithQuestion(final))
            return Stalled;
        return NoChanges;
    }

    // --- capping what goes into run_events.payload ----------------------------
    //
    // A single stream-json line can be several MiB (StreamLineLimit is 64 MiB)
    // and jarvis.db is permanent. 256 KiB is far above anything a person would
    // read and far below where one run grows the database by gigabytes.
    //
    // What gets shrunk is the long STRINGS inside the event, not the line: the
    // stored payload is re-parsed downstream (the dashboard, and AssessOutcome,
    // which reads tool names to decide whether a run did anything), so it must
    // still parse and keep every key, type and tool name. 8 KiB per string is
    // more than the dashboard renders in one block.
    public const int PayloadMaxChars = 256 * 1024;
    public const int PayloadStringMax = 8 * 1024;

    public static JsonNode? Shrink(JsonNode? value, int budget)
    {
        switch (value)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                var s = v.GetValue<string>();
                if (s.Length <= budget) return JsonValue.Create(s);
                return JsonValue.Create($"{Cut(s, budget)}… [truncated, {s.Length} chars]");
            case JsonObject o:
                var no = new JsonObject();
                foreach (var (k, child) in o) no[k] = Shrink(child, budget);
                return no;
            case JsonArray a:
                var na = new JsonArray();
                foreach (var child in a) na.Add(Shrink(child, budget));
                return na;
            case null:
                return null;
            default:
                return value.DeepClone();
        }
    }

    /// <summary>
    /// The JSONL line to persist for `ev`, bounded in size. Returns `line`
    /// unchanged below the cap — the overwhelmingly common case.
    /// </summary>
    public static string CapPayload(string line, JsonNode? ev)
    {
        if (line.Length <= PayloadMaxChars) return line;
        string shrunk;
        try
        {
            shrunk = Shrink(ev, PayloadStringMax)?.ToJsonString() ?? "";
        }
        catch (Exception)
        {
            shrunk = "";
        }
        if (shrunk.Length > 0 && shrunk.Length <= PayloadMaxChars) return shrunk;
        // Long from sheer element count, not from any one string — there is
        // nothing left to shrink. Keep the type (every downstream reader keys
        // off it) and say plainly what happened.
        return new JsonObject
        {
            ["type"] = EventKind(ev),
            ["jarvis_truncated"] = true,
            ["jarvis_original_chars"] = line.Length,
            ["jarvis_preview"] = Cut(line, PayloadStringMax),
        }.ToJsonString();
    }

    // ── small Python-semantics helpers ──────────────────────────────────────

    /// <summary>`d.get(key)` that tolerates a non-object.</summary>
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool IsString(JsonNode? node, out string value)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>Python truthiness of a JSON value.</summary>
    public static bool PyTruthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetValue<double>(out var d) && d != 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>Python `str(value)` for a JSON value.</summary>
    public static string PyStr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };

    /// <summary>`value or ""` as a string.</summary>
    public static string OrEmpty(JsonNode? node) => PyTruthy(node) ? PyStr(node) : "";

    /// <summary>`" ".join(s.split())`.</summary>
    public static string CollapseWs(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>`s[:n]` that never leaves half a surrogate pair at the end.</summary>
    public static string Cut(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }
}
