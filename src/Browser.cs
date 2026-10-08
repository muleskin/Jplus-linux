using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// Looking at a web page for JARVIS himself: `ReadPage` (the readable text) and
/// `CapturePage` (a viewport-sized PNG).
///
/// The Python original also carried `JarvisBrowser`, a visible Playwright
/// Chromium for "watch me browse" (search / visit / screenshot / research, plus
/// the SearchResult / PageContent / ResearchResult models). Nothing in the server
/// used it any more — showing the user a page is `open_in_browser`, in their own
/// browser — so it is deliberately NOT ported.
///
/// HEADLESS, deliberately. These exist so the BRAIN can read or see a page in the
/// middle of a spoken turn — for itself, not for the user. A second, visible,
/// cookie-less browser popping up and stealing focus every time JARVIS glanced at
/// a URL would be a bug: taking the user's focus is something only an ACTING tool
/// may do, and reading is not acting.
///
/// Windows port: no Playwright. One throwaway headless Microsoft Edge (or Chrome)
/// process per call on the command line — `--dump-dom` for the text,
/// `--screenshot` for the picture — with a private temporary profile that is
/// deleted afterwards, so a glance can never leave a window, a cookie or a
/// half-dead browser behind. Headless needs no desktop, so this works as a service.
/// </summary>
public static partial class Browser
{
    public static readonly ILogger Log = Py.Log("jarvis.browser");

    public const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public const int TimeoutMs = 30_000;

    // Every one of these must finish WELL inside `JarvisMcp.TimeoutSec` (20s). A
    // handler that outlives that tells the brain the server is unreachable while
    // the work carries on regardless. Launch + navigate (<=12s) + capture leaves
    // real headroom, and the caller puts its own hard deadline on top of this.
    public const int LookTimeoutMs = 12_000;

    // 1280x800 is a laptop window: wide enough that a site renders its desktop
    // layout rather than its mobile one, small enough that the PNG stays a few
    // hundred KB and the image costs the brain on the order of a thousand tokens.
    public static readonly (int Width, int Height) LookViewport = (1280, 800);

    // What the extractor may hand back. NOT the brain's budget — that is the
    // caller's, and far smaller. This only stops a pathological page from moving
    // megabytes around before anyone trims it.
    public const int PageTextChars = 20_000;

    // A viewport-sized PNG bigger than this will not fit through the tool channel.
    // Refuse it out loud rather than sending something the CLI will choke on.
    public const int MaxShotBytes = 4_000_000;

    /// <summary>A page JARVIS could not read or see. The message is speakable.</summary>
    public class PageError : Exception
    {
        public PageError(string message) : base(message) { }
        public PageError(string message, Exception inner) : base(message, inner) { }
    }

    public class PageText
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Text { get; set; } = "";
        public int CharCount { get; set; }        // the extracted length BEFORE the caller's budget
        public bool Truncated { get; set; }       // PageTextChars clipped it
    }

    public class PageShot
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public byte[] Png { get; set; } = [];
    }

    // ── the headless browser ───────────────────────────────────────────────────

    /// <summary>
    /// Every installed chrome.exe / msedge.exe from the usual install locations, in
    /// the order they are tried. Chrome first: on some machines headless Edge exits 0
    /// having done nothing (measured on the dev box), so Edge is the fallback.
    /// </summary>
    public static List<string> FindBrowsers()
    {
        if (!Py.IsWindows)
        {
            // Linux: the launcher names distros and vendors install on PATH.
            return new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser",
                           "microsoft-edge", "microsoft-edge-stable" }
                .Select(Py.Which).Where(p => p is not null).Select(p => p!)
                .Append("/snap/bin/chromium").Where(Py.IsExecutableFile).Distinct().ToList();
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        foreach (var root in new[] { pf, pf86, local })
            if (!string.IsNullOrEmpty(root))
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        foreach (var root in new[] { pf86, pf, local })
            if (!string.IsNullOrEmpty(root))
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
        if (Py.Which("chrome") is { } c1) candidates.Add(c1);
        if (Py.Which("msedge") is { } c2) candidates.Add(c2);
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The first browser FindBrowsers would try, or null.</summary>
    public static string? FindBrowser() => FindBrowsers().FirstOrDefault();

    /// <summary>
    /// One throwaway headless browser run per installed browser until `worked`
    /// says one delivered, each guaranteed gone afterwards: the process tree is
    /// killed on timeout and its temporary profile is deleted on every path out.
    /// A leaked browser here would outlive JARVIS himself. A timeout is final —
    /// there is no budget left to try another browser.
    /// </summary>
    public static async Task<Py.ProcResult> RunHeadless(IEnumerable<string> extraArgs, string url,
        Func<Py.ProcResult, bool>? worked = null)
    {
        SweepStaleProfiles();
        var browsers = FindBrowsers();
        if (browsers.Count == 0) throw new FileNotFoundException(Py.IsWindows ? "no Google Chrome or Microsoft Edge found" : "no Google Chrome, Chromium or Microsoft Edge found");
        var extra = extraArgs.ToList();
        var deadline = Py.Monotonic() + (LookTimeoutMs + 2_000) / 1000.0;
        Py.ProcResult? last = null;
        foreach (var exe in browsers)
        {
            var left = deadline - Py.Monotonic();
            if (left <= 0.5) break;
            last = await RunOneBrowser(exe, extra, url, left);
            if (last.TimedOut || worked is null || worked(last)) return last;
            Log.LogInformation("headless {Browser} produced nothing; trying the next one", Path.GetFileName(exe));
        }
        return last ?? new Py.ProcResult(-1, "", "", true);
    }

    private static async Task<Py.ProcResult> RunOneBrowser(string exe, List<string> extraArgs, string url, double timeout)
    {
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-headless-" + Guid.NewGuid().ToString("N"));
        if (Py.IsWindows) Directory.CreateDirectory(profile);
        else Directory.CreateDirectory(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var args = new List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-sync",
            "--mute-audio",
            "--hide-scrollbars",
            $"--user-data-dir={profile}",
            $"--user-agent={UserAgent}",
            $"--window-size={LookViewport.Width},{LookViewport.Height}",
        };
        // Chrome refuses to start its sandbox as root on Linux (a root-run service).
        if (!Py.IsWindows && Posix.Geteuid() == 0) args.Add("--no-sandbox");
        args.AddRange(extraArgs);
        // `--` last: the URL is model-derived data and must never be read as a switch.
        args.Add("--");
        args.Add(url);
        try
        {
            // Launch on top of the navigation budget, so a page that honours the
            // budget is not pre-empted by this outer guard.
            return await Py.Run(exe, args, timeout);
        }
        finally
        {
            await DeleteProfile(profile);
        }
    }

    private static async Task DeleteProfile(string dir)
    {
        // The browser's helper processes can hold files for a moment after the
        // main process exits.
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }
        // Still held: keep trying in the background rather than holding up the turn.
        _ = Py.Spawn(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Log.LogWarning("headless teardown: could not remove {Dir}", dir);
        }, "headless profile cleanup");
    }

    /// <summary>
    /// Remove throwaway profiles an earlier run could not (a crash, a kill, a file
    /// held past the retries). Anything of ours older than ten minutes is dead.
    /// </summary>
    public static void SweepStaleProfiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var d in Directory.EnumerateDirectories(Path.GetTempPath(), "jarvis-headless-*")
                         .Concat(Directory.EnumerateDirectories(Path.GetTempPath(), "jarvis-shot-*")))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(d) < cutoff) Directory.Delete(d, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── reading ────────────────────────────────────────────────────────────────

    /// <summary>The readable text of one page. Throws PageError if it cannot be had.</summary>
    public static async Task<PageText> ReadPage(string url)
    {
        string dom;
        try
        {
            var r = await RunHeadless(["--dump-dom"], url, x => !string.IsNullOrWhiteSpace(x.Stdout));
            if (r.TimedOut) throw new TimeoutException($"timed out after {LookTimeoutMs}ms");
            dom = r.Stdout ?? "";
            if (dom.Trim().Length == 0)
                throw new InvalidOperationException($"no DOM (exit {r.ReturnCode}): {Clip(r.Stderr, 200)}");
            // A navigation failure still dumps a DOM: the browser's own error page.
            if (IsErrorPage(dom)) throw new InvalidOperationException("navigation failed (browser error page)");
        }
        catch (PageError) { throw; }
        catch (Exception e)
        {
            Log.LogWarning("read_page failed for {Url}: {Error}", url, e.Message);
            throw new PageError("that page wouldn't load", e);
        }

        var (title, full, text) = ExtractText(dom, PageTextChars);
        if (text.Trim().Length == 0) throw new PageError("that page had no readable text on it");
        // `--dump-dom` does not report where redirects landed, so the URL asked for
        // is the URL reported (the Python fell back to the same when it had none).
        return new PageText { Title = title, Url = url, Text = text, CharCount = full, Truncated = full > text.Length };
    }

    /// <summary>
    /// A viewport-sized PNG of one page. Throws PageError if it cannot be had.
    /// Viewport, NOT full page: what the user means by "look at this with me" is
    /// the screenful in front of him, not a tall thin strip of tokens.
    /// </summary>
    public static async Task<PageShot> CapturePage(string url)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-shot-" + Guid.NewGuid().ToString("N"));
        byte[] png;
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "page.png");
            // Let web fonts, images and any late layout settle. A screenshot of a
            // half-painted page is worse than no screenshot at all.
            var r = await RunHeadless([$"--screenshot={file}", "--virtual-time-budget=700"], url, _ => File.Exists(file));
            if (r.TimedOut) throw new TimeoutException($"timed out after {LookTimeoutMs}ms");
            png = File.Exists(file) ? await File.ReadAllBytesAsync(file) : [];
        }
        catch (PageError) { throw; }
        catch (Exception e)
        {
            Log.LogWarning("capture_page failed for {Url}: {Error}", url, e.Message);
            throw new PageError("I couldn't get a picture of that page", e);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        if (png.Length == 0 || Screen.PngSize(png) is null) throw new PageError("I couldn't get a picture of that page");
        if (png.Length > MaxShotBytes) throw new PageError("that page's picture came out far too large to send");
        // The title would need a second browser run; the caller deliberately never
        // uses it (it is the site's own text), so it is left empty.
        return new PageShot { Title = "", Url = url, Png = png };
    }

    private static string Clip(string? s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s[..n] : s; }

    /// <summary>Chromium's own "This site can't be reached" page.</summary>
    public static bool IsErrorPage(string dom) =>
        dom.Contains("id=\"main-frame-error\"", StringComparison.Ordinal)
        || Regex.IsMatch(dom, @"<body[^>]*class=""[^""]*\bneterror\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── DOM → text, in C# ──────────────────────────────────────────────────────
    //
    // The Python ran this in the page:
    //   main = querySelector('main') || 'article' || '[role="main"]' || body;
    //   remove 'script, style, noscript, nav, header, footer, aside, .sidebar,
    //           .menu, .ad, .advertisement, iframe' from it;
    //   text = main.innerText, [ \t]+ → ' ', \n{3,} → \n\n, trim;
    //   return {title, full: text.length, text: text.substring(0, limit)}.
    // Here the serialized DOM (well-formed: the browser wrote it) is parsed into a
    // small tree and the same steps run over it, with innerText approximated by
    // its rendering rules: blocks break lines, <p> breaks two, <br> one, cells tab.

    public sealed class Node
    {
        public string Name = "";                                   // "" for a text node, "#root"
        public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
        public List<Node> Children = new();
        public string? Text;                                       // text nodes only (already decoded)
        public Node? Parent;
    }

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "textarea", "title", "xmp", "iframe", "noembed", "noframes",
    };

    private static readonly HashSet<string> NoiseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "nav", "header", "footer", "aside", "iframe",
        // Never rendered, so never in innerText either.
        "template", "head",
    };

    private static readonly HashSet<string> NoiseClasses = new(StringComparer.Ordinal)
    {
        "sidebar", "menu", "ad", "advertisement",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "body", "center", "dd", "details", "dialog", "dir",
        "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hgroup", "hr", "html", "legend", "li", "listing", "main", "menu", "nav",
        "ol", "optgroup", "option", "pre", "section", "summary", "table", "caption", "thead", "tbody",
        "tfoot", "ul", "plaintext", "xmp", "video", "audio", "canvas",
    };

    private static readonly Regex TagRe = new(
        @"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<![^>]*>|<\?[^>]*>|</\s*([A-Za-z][\w:-]*)\s*>|<([A-Za-z][\w:-]*)((?:\s+[^\s""'>/=]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*(/?)>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AttrRe = new(
        @"([^\s""'>/=]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Parse serialized HTML into a lightweight tree.</summary>
    public static Node ParseHtml(string html)
    {
        var root = new Node { Name = "#root" };
        var cur = root;
        int pos = 0;
        while (pos < html.Length)
        {
            var m = TagRe.Match(html, pos);
            if (!m.Success)
            {
                AddText(cur, html[pos..]);
                break;
            }
            if (m.Index > pos) AddText(cur, html[pos..m.Index]);
            pos = m.Index + m.Length;

            if (m.Groups[1].Success)                         // end tag
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                // Close up to the nearest open element of that name; ignore strays.
                for (var n = cur; n is not null && n != root; n = n.Parent)
                {
                    if (n.Name == name) { cur = n.Parent ?? root; break; }
                }
                continue;
            }
            if (!m.Groups[2].Success) continue;              // comment, doctype, CDATA, PI

            var tag = m.Groups[2].Value.ToLowerInvariant();
            var el = new Node { Name = tag, Parent = cur };
            foreach (Match a in AttrRe.Matches(m.Groups[3].Value))
            {
                var key = a.Groups[1].Value;
                var val = a.Groups[2].Success ? a.Groups[2].Value
                        : a.Groups[3].Success ? a.Groups[3].Value
                        : a.Groups[4].Success ? a.Groups[4].Value : "";
                el.Attrs.TryAdd(key, WebUtility.HtmlDecode(val));
            }
            cur.Children.Add(el);
            bool selfClosing = m.Groups[4].Value == "/";
            if (VoidTags.Contains(tag) || selfClosing) continue;

            if (RawTextTags.Contains(tag))
            {
                // Raw text runs to the matching close tag, whatever it contains.
                var close = html.IndexOf("</" + tag, pos, StringComparison.OrdinalIgnoreCase);
                var end = close < 0 ? html.Length : close;
                var raw = html[pos..end];
                el.Children.Add(new Node { Text = tag is "title" or "textarea" ? WebUtility.HtmlDecode(raw) : raw, Parent = el });
                if (close < 0) { pos = html.Length; break; }
                var gt = html.IndexOf('>', close);
                pos = gt < 0 ? html.Length : gt + 1;
                continue;
            }
            cur = el;
        }
        return root;
    }

    private static void AddText(Node parent, string raw)
    {
        if (raw.Length == 0) return;
        parent.Children.Add(new Node { Text = WebUtility.HtmlDecode(raw), Parent = parent });
    }

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (var c in n.Children)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static Node? First(Node root, Func<Node, bool> pred) =>
        Descendants(root).FirstOrDefault(n => n.Text is null && pred(n));

    private static bool HasNoiseClass(Node n) =>
        n.Attrs.TryGetValue("class", out var cls)
        && cls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(NoiseClasses.Contains);

    private static bool IsHidden(Node n)
    {
        if (n.Attrs.ContainsKey("hidden")) return true;
        if (n.Attrs.TryGetValue("aria-hidden", out var ah) && ah == "true" && n.Name is "svg") return true;
        if (n.Attrs.TryGetValue("style", out var st))
        {
            var s = st.Replace(" ", "").ToLowerInvariant();
            if (s.Contains("display:none") || s.Contains("visibility:hidden")) return true;
        }
        return false;
    }

    /// <summary>
    /// (title, full length, text clipped to `limit`) — the C# counterpart of the
    /// Python's in-page extractor.
    /// </summary>
    public static (string title, int full, string text) ExtractText(string dom, int limit)
    {
        var root = ParseHtml(dom);

        var titleNode = First(root, n => n.Name == "title");
        var title = titleNode is null ? "" : Regex.Replace(string.Concat(titleNode.Children.Select(c => c.Text ?? "")), @"\s+", " ").Trim();

        var main = First(root, n => n.Name == "main")
                   ?? First(root, n => n.Name == "article")
                   ?? First(root, n => n.Attrs.TryGetValue("role", out var r) && r == "main")
                   ?? First(root, n => n.Name == "body")
                   ?? root;

        var parts = new List<object>();   // string = text, int = required line breaks
        Render(main, parts, pre: false, isRoot: true);
        var text = Assemble(parts);

        text = Regex.Replace(text, @"[ \t]+", " ");
        // innerText drops the collapsible spaces at the start and end of each line.
        text = Regex.Replace(text, @" ?\n ?", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();
        var full = text.Length;
        return (title, full, full > limit ? text[..limit] : text);
    }

    private static void Render(Node n, List<object> parts, bool pre, bool isRoot = false)
    {
        if (n.Text is not null)
        {
            if (n.Parent is { } p && RawTextTags.Contains(p.Name) && p.Name != "textarea") return;
            var t = pre ? n.Text.Replace("\r\n", "\n") : Regex.Replace(n.Text, @"[ \t\r\n\f]+", " ");
            if (t.Length > 0) parts.Add(t);
            return;
        }
        if (!isRoot)
        {
            // The Python removed these from the chosen element; the element itself stays.
            if (NoiseTags.Contains(n.Name) || HasNoiseClass(n) || IsHidden(n)) return;
        }
        switch (n.Name)
        {
            case "br":
                parts.Add("\n");
                return;
            case "img" or "input" or "select" or "svg" or "math" or "object" or "embed" or "video" or "audio" or "canvas":
                return;
        }

        bool block = BlockTags.Contains(n.Name);
        bool para = n.Name == "p";
        bool isPre = pre || n.Name is "pre" or "textarea" or "listing" or "plaintext" or "xmp";

        if (para) parts.Add(2);
        else if (block || n.Name == "tr") parts.Add(1);

        var cells = n.Name == "tr" ? n.Children.Where(c => c.Name is "td" or "th").ToList() : null;
        foreach (var c in n.Children)
        {
            Render(c, parts, isPre);
            if (cells is not null && cells.Count > 0 && c.Name is "td" or "th" && c != cells[^1]) parts.Add("\t");
        }

        if (para) parts.Add(2);
        else if (block || n.Name == "tr") parts.Add(1);
    }

    /// <summary>innerText's final step: leading/trailing breaks dropped, a run of
    /// required breaks becomes the largest of them.</summary>
    private static string Assemble(List<object> parts)
    {
        var sb = new StringBuilder();
        int pending = 0;
        bool started = false;
        foreach (var p in parts)
        {
            if (p is int k)
            {
                if (started) pending = Math.Max(pending, k);
                continue;
            }
            var s = (string)p;
            if (s.Length == 0) continue;
            if (pending > 0)
            {
                sb.Append('\n', pending);
                pending = 0;
            }
            sb.Append(s);
            started = true;
        }
        return sb.ToString();
    }
}
