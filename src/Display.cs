using System.Text.Json.Nodes;

namespace Jplus;

/// <summary>
/// `show_results`: put what JARVIS found in front of the user, ON THE JARVIS
/// PAGE they are talking to — not in a new window on whatever machine the
/// server runs on (`open_in_browser`, which on a server is nobody's screen).
///
/// Everything here is shown, never interpreted: the page renders it with
/// `textContent`, links open in a new tab with `noopener noreferrer`, and only
/// http(s) addresses survive. It reads nothing and runs nothing, which is why it
/// may follow a web search in the same turn (see TaintExemptActing).
/// </summary>
public static partial class Server
{
    public const int DisplayMaxItems = 10;
    public const int DisplayMaxTitle = 200;
    public const int DisplayMaxSnippet = 600;
    public const int DisplayMaxText = 6000;
    public const int DisplayMaxUrl = 2000;

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>An absolute http(s) URL, or null. Nothing else is ever a link.</summary>
    public static string? SafeWebUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > DisplayMaxUrl) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(u.Host)) return null;
        return u.AbsoluteUri;
    }

    /// <summary>The display frame for `args`, or a refusal sentence.</summary>
    public static (Dictionary<string, object?>? Frame, string? Refusal) BuildDisplayFrame(JsonObject args)
    {
        var title = Clip(P2.Arg(args, "title").Trim(), DisplayMaxTitle);
        var text = Clip(P2.Arg(args, "text").Trim(), DisplayMaxText);

        var items = new JsonArray();
        if (args.TryGetPropertyValue("items", out var raw) && raw is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (items.Count >= DisplayMaxItems) break;
                if (node is not JsonObject o) continue;
                var url = SafeWebUrl(Py.Str(o, "url"));
                var itemTitle = Clip((Py.Str(o, "title") ?? "").Trim(), DisplayMaxTitle);
                var snippet = Clip((Py.Str(o, "snippet") ?? "").Trim(), DisplayMaxSnippet);
                if (itemTitle.Length == 0 && url is null && snippet.Length == 0) continue;
                if (itemTitle.Length == 0) itemTitle = url is not null ? new Uri(url).Host : "Result";
                var item = new JsonObject { ["title"] = itemTitle, ["snippet"] = snippet };
                if (url is not null)
                {
                    item["url"] = url;
                    item["host"] = new Uri(url).Host;
                }
                items.Add(item);
            }
        }

        if (items.Count == 0 && text.Length == 0)
            return (null, "There is nothing to show, sir — give me some results or some text.");
        if (title.Length == 0) title = "Results";

        return (new Dictionary<string, object?>
        {
            ["type"] = "display",
            ["title"] = title,
            ["text"] = text,
            ["items"] = items,
        }, null);
    }

    public static async Task<string> ToolShowResults(JsonObject args)
    {
        var (frame, refusal) = BuildDisplayFrame(args);
        if (refusal is not null) return refusal;
        if (!HasVoiceClients)
            return "no_screen — nobody has the JARVIS page open, sir, so there is nowhere to show it.";
        await VoiceEmit(frame!);
        var n = ((JsonArray)frame!["items"]!).Count;
        return n > 0
            ? $"Shown on the JARVIS page: {n} result{(n == 1 ? "" : "s")}. Do not read them all out — a sentence will do."
            : "Shown on the JARVIS page. Do not read it all out — a sentence will do.";
    }
}
