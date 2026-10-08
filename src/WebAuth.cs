using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Who is allowed to reach JARVIS's HTTP and WebSocket surface.
///
/// JARVIS spawns `claude --dangerously-skip-permissions` on request and reads
/// every conversation on the machine, so anything that could open a TCP
/// connection could once do both. There are exactly two legitimate callers:
///
/// **The browser.** It has no secret and must not need one. But it is
/// same-origin (Vite proxies `/api` and `/ws` to the API port, and the built
/// frontend is served off the API port itself), so every state-changing
/// request and every WebSocket handshake carries an `Origin` header the browser
/// sets and no page can forge. That header admits it; a page on
/// `http://evil.example` — or `http://localhost:3000`, just as much a stranger
/// — is refused.
///
/// **A local non-browser client**: the brain's MCP child, a debugging script.
/// It sends no `Origin`, but it can read the tool token out of a file in the
/// data directory only this user can open. That token admits it.
///
/// An `Origin` header is only unforgeable when a browser sets it; against a
/// non-browser attacker the defence is the token and the loopback bind.
///
/// Reads (GET, HEAD) are not gated on `Origin` — a same-origin GET from fetch
/// carries none. What keeps a hostile page from *reading* them is that JARVIS
/// sends no CORS headers at all. The `Host` header IS checked on every request,
/// because DNS rebinding is the way around "no CORS". And every response says
/// it will not be framed, because framing (clickjacking) is the other way
/// around the Origin check and forges nothing — see SecurityHeaders.
/// </summary>
public static partial class WebAuth
{
    public static readonly Microsoft.Extensions.Logging.ILogger Log = Py.Log("jarvis.auth");

    // The methods that can change something. A GET cannot be gated (see above),
    // so anything with a side effect must not be one.
    public static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    // Every spelling of "this machine" a browser can put in an Origin.
    public static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    // Vite's dev server takes the next free port when 5173 is busy, so the
    // window is a handful of ports wide — deliberately not "any loopback port":
    // a page on http://localhost:3000 is somebody else's dev server.
    public static readonly int[] DevServerPorts = Enumerable.Range(5173, 8).ToArray();   // 5173..5180

    public const int DefaultApiPort = 8340;

    // Sent on EVERY response JARVIS writes — HTML, JSON, static asset, 404,
    // and the guard's own 403s. Not per-route: a header applied per route is a
    // header somebody forgets on the route added next month.
    //
    //   frame-ancestors / X-Frame-Options — the clickjacking fix. Both
    //       spellings: old browsers read X-Frame-Options, everything else
    //       frame-ancestors (which also covers <object>/<embed>).
    //   nosniff — JSON here contains attacker-influenced text; a browser must
    //       not decide it "looks like" HTML and run it.
    //   Referrer-Policy — the dashboard's URLs carry run ids and project paths.
    //
    // Deliberately NOT a full CSP: it would fail closed and silently the first
    // time somebody inlined a script. frame-ancestors does the security work.
    public static readonly (string name, string value)[] SecurityHeaders =
    [
        ("x-frame-options", "DENY"),
        ("content-security-policy", "frame-ancestors 'none'"),
        ("x-content-type-options", "nosniff"),
        ("referrer-policy", "no-referrer"),
    ];

    /// <summary>
    /// Our values, replacing rather than joining any already there. Appending
    /// would produce `X-Frame-Options: SAMEORIGIN, DENY`, which several
    /// browsers resolve by ignoring the header entirely. Ours win.
    /// </summary>
    public static void ApplySecurityHeaders(IHeaderDictionary headers)
    {
        foreach (var (name, value) in SecurityHeaders)
        {
            headers.Remove(name);
            headers[name] = value;
        }
    }

    // ---------------------------------------------------------------------
    // Where this process is actually listening
    // ---------------------------------------------------------------------
    //
    // Three things need to know: the origin allowlist, the Host allowlist, and
    // the URL the brain's MCP child dials. All three read JARVIS_PORT /
    // JARVIS_BIND_HOST / JARVIS_SCHEME, which Program.RunServer records before
    // the host starts. What is left otherwise is what the process was launched
    // with: the environment, then the command line, then the documented
    // defaults — and, when even that is not enough, saying so out loud.

    // Uvicorn's own CLI defaults (kept for fidelity with the Python launcher detection).
    public const string UvicornDefaultHost = "127.0.0.1";
    public const int UvicornDefaultPort = 8000;

    // JARVIS's, per the server's own argument defaults.
    public const string JarvisDefaultHost = "127.0.0.1";
    public const int JarvisDefaultPort = DefaultApiPort;

    // Every spelling of "only this machine" that can be *bound* to. Wider than
    // LoopbackHosts: 127.0.0.0/8 is all loopback.
    public static readonly HashSet<string> LoopbackNames = new(StringComparer.Ordinal) { "localhost" };

    // Flags that name a listening socket this cannot reason about as an address.
    public static readonly string[] OpaqueBindFlags = ["--uds", "--fd"];

    /// <summary>
    /// Where the server is listening, and how confident we are about it.
    /// `Source` is part of the value on purpose: "unknown" must be able to
    /// travel, because adopting a guessed bind would lock the operator out.
    /// </summary>
    public sealed record Bind(string Host, int Port, string Scheme, string Source);

    /// <summary>Does binding here mean "only this machine can reach it"?</summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = JarvisMemory.PyStrip(host).Trim('[', ']').ToLowerInvariant();
        if (LoopbackNames.Contains(name)) return true;
        var ip = ParseIpStrict(name);
        return ip is not null && IPAddress.IsLoopback(ip);
    }

    /// <summary>`--name value` or `--name=value`; the last occurrence wins.</summary>
    public static string? Flag(IReadOnlyList<string> argv, string name)
    {
        string? found = null;
        for (int i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg == name && i + 1 < argv.Count) found = argv[i + 1];
            else if (arg.StartsWith(name + "=", StringComparison.Ordinal)) found = arg[(name.Length + 1)..];
        }
        return found;
    }

    /// <summary>
    /// The address this process is listening on, as well as it can be known:
    /// the environment first (Program records its parsed arguments there),
    /// then the command line, then the launcher's defaults.
    /// </summary>
    public static Bind DetectBind(IReadOnlyList<string>? argv = null, IDictionary<string, string?>? environ = null)
    {
        argv ??= Environment.GetCommandLineArgs();
        string? Env(string key)
        {
            if (environ is not null) return environ.TryGetValue(key, out var v) ? v : null;
            return Environment.GetEnvironmentVariable(key);
        }

        var launcher = (argv.Count > 0 ? argv[0] : "").Split('/', '\\')[^1].ToLowerInvariant();
        bool uvicornCli = launcher.StartsWith("uvicorn", StringComparison.Ordinal);
        var defaultHost = uvicornCli ? UvicornDefaultHost : JarvisDefaultHost;
        var defaultPort = uvicornCli ? UvicornDefaultPort : JarvisDefaultPort;

        var envHost = JarvisMemory.PyStrip(Env("JARVIS_BIND_HOST") ?? "");
        var envPort = JarvisMemory.PyStrip(Env("JARVIS_PORT") ?? "");
        var envScheme = JarvisMemory.PyStrip(Env("JARVIS_SCHEME") ?? "");

        var argvHost = Flag(argv, "--host");
        var argvPort = Flag(argv, "--port");
        bool sslFlagged = argv.Any(a => new[] { "--ssl-keyfile", "--ssl-certfile", "--ssl" }
            .Any(f => a == f || a.StartsWith(f + "=", StringComparison.Ordinal)));

        var host = envHost.Length > 0 ? envHost : !string.IsNullOrEmpty(argvHost) ? argvHost : defaultHost;
        var scheme = envScheme.Length > 0 ? envScheme : (sslFlagged ? "https" : "http");

        int port = defaultPort;
        // First that parses wins, in the same order as the host: environment,
        // then command line, then the launcher's default.
        foreach (var candidate in new[] { envPort, argvPort })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (TryPyInt(candidate, out var parsed))
            {
                port = parsed;
                break;
            }
            Log.LogWarning("ignoring a port that is not a number: {Candidate}", candidate);
        }

        string source;
        if (envHost.Length > 0 || envPort.Length > 0)
            source = "environment";
        else if (argv.Any(a => OpaqueBindFlags.Any(f => a == f || a.StartsWith(f + "=", StringComparison.Ordinal))))
            // A unix socket or an inherited fd. Not an address, and pretending
            // otherwise would build the wrong allowlist in silence.
            source = "unknown";
        else if (!string.IsNullOrEmpty(argvHost) || !string.IsNullOrEmpty(argvPort))
            source = "command line";
        else if (uvicornCli)
            source = "uvicorn default";
        else
            source = "default";

        return new Bind(host, port, scheme, source);
    }

    /// <summary>
    /// Publish a detected bind so the allowlists and the MCP URL see it.
    /// Set-if-absent, never overwrite: the launcher that knows for certain
    /// writes these first. An "unknown" bind is not published at all.
    /// </summary>
    public static void AdoptBind(Bind bind)
    {
        if (bind.Source == "unknown") return;
        SetDefault("JARVIS_BIND_HOST", bind.Host);
        SetDefault("JARVIS_PORT", bind.Port.ToString(CultureInfo.InvariantCulture));
        SetDefault("JARVIS_SCHEME", bind.Scheme);
    }

    private static void SetDefault(string key, string value)
    {
        if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>
    /// What to tell the operator, or [] when there is nothing to tell.
    /// Everything on this surface acts with the user's full authority; there
    /// is no answer for a LAN client except not being on the LAN.
    /// </summary>
    public static List<string> ExposureWarning(Bind bind)
    {
        if (bind.Source == "unknown")
            return
            [
                "! JARVIS could not work out what address it is listening on",
                "  from how it was launched, so its own origin and Host checks",
                "  are using defaults that may be wrong — the browser may be",
                "  refused. Set JARVIS_BIND_HOST and JARVIS_PORT to the address",
                "  you are actually serving from.",
            ];
        if (IsLoopback(bind.Host) || Passcode.Enabled) return [];
        return
        [
            "! No JARVIS_PASSCODE is set. Set one to require sign-in.",
            $"! Bound to {bind.Host}, not loopback. Anything that can",
            "  reach this port can read every conversation on this",
            "  machine; only the tool token stands in front of the",
            "  endpoints that act. Bind 127.0.0.1 unless you mean it —",
            "  and if you do, set JARVIS_ALLOWED_ORIGINS to the address",
            "  you will open the page at, or the browser will be",
            "  refused too.",
        ];
    }

    /// <summary>The port this server is actually bound to.</summary>
    public static int ApiPort()
    {
        var raw = Py.Getenv("JARVIS_PORT", "") ?? "";
        if (raw.Length == 0) return DefaultApiPort;
        return TryPyInt(raw, out var p) ? p : DefaultApiPort;
    }

    /// <summary>
    /// The origins JARVIS serves its own pages from — and nothing else.
    /// Recomputed per call: the port is only known once Program has parsed it.
    /// </summary>
    public static HashSet<string> AllowedOrigins()
    {
        var ports = new HashSet<int>(DevServerPorts) { ApiPort() };
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scheme in new[] { "http", "https" })
            foreach (var host in LoopbackHosts)
                foreach (var port in ports)
                    origins.Add($"{scheme}://{host}:{port}");
        foreach (var raw in (Py.Getenv("JARVIS_ALLOWED_ORIGINS", "") ?? "").Split(','))
        {
            var extra = JarvisMemory.PyStrip(raw).TrimEnd('/');
            if (extra.Length > 0) origins.Add(extra);
        }
        return origins;
    }

    /// <summary>
    /// Exact match only. Never a prefix or suffix test:
    /// `http://localhost:5173.evil.example` and `http://localhost:51730` both
    /// start with an allowed origin, and `null` is not this machine.
    /// </summary>
    public static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        return AllowedOrigins().Contains(origin);
    }

    /// <summary>
    /// Host names an operator has said this server answers to: the bind host,
    /// and every host named in JARVIS_ALLOWED_ORIGINS.
    /// </summary>
    public static HashSet<string> AllowedHosts()
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        var bind = JarvisMemory.PyStrip(Py.Getenv("JARVIS_BIND_HOST", "") ?? "").Trim('[', ']').ToLowerInvariant();
        if (bind.Length > 0) hosts.Add(bind);
        foreach (var raw in (Py.Getenv("JARVIS_ALLOWED_ORIGINS", "") ?? "").Split(','))
        {
            var origin = JarvisMemory.PyStrip(raw);
            if (origin.Length == 0) continue;
            string? name;
            try { name = UrlHostname(origin); }
            catch (FormatException) { continue; }
            if (!string.IsNullOrEmpty(name)) hosts.Add(name.ToLowerInvariant());
        }
        return hosts;
    }

    /// <summary>
    /// Refuse a `Host` that names a domain. This is the rebinding check.
    ///
    /// DNS rebinding: evil.example resolves to 127.0.0.1, the browser sends
    /// `Host: evil.example`, and page and response now share an origin. A
    /// rebinding attack has to name a domain it controls in public DNS, which
    /// has a dot in it. An address literal has nothing to rebind; a
    /// single-label host cannot be a public domain. Anything dotted has to be
    /// declared in JARVIS_ALLOWED_ORIGINS.
    /// </summary>
    public static bool HostAllowed(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return true;   // HTTP/1.0, and some local clients
        string hostname;
        try { hostname = UrlHostname("//" + hostHeader) ?? ""; }
        catch (FormatException) { return false; }
        if (hostname.Length == 0) return false;
        hostname = hostname.ToLowerInvariant();
        if (AllowedHosts().Contains(hostname)) return true;
        if (ParseIpStrict(hostname.Trim('[', ']')) is not null) return true;
        return !hostname.Contains('.');
    }

    /// <summary>The token out of an `Authorization: Bearer &lt;token&gt;` header.</summary>
    public static string? Bearer(string? headerValue)
    {
        if (headerValue is null || headerValue.Length < 8) return null;
        if (!headerValue[..7].Equals("bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = JarvisMemory.PyStrip(headerValue[7..]);
        return token.Length > 0 ? token : null;
    }

    /// <summary>
    /// Constant-time compare against the per-install tool token. The token
    /// file is only touched when a caller actually offered one, so the common
    /// path — a browser with a good origin — does no disk I/O.
    /// </summary>
    public static bool TokenMatches(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        string expected;
        try
        {
            expected = DataPaths.EnsureToolToken();
        }
        catch (Exception e)
        {
            Log.LogError("could not read the tool token: {Error}", e.Message);
            return false;
        }
        try
        {
            var a = new UTF8Encoding(false, false).GetBytes(supplied);
            var b = Encoding.UTF8.GetBytes(expected);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool RequestAuthorized(string? origin, string? authorization) =>
        OriginAllowed(origin) || TokenMatches(Bearer(authorization));

    /// <summary>One request header, or null when absent.</summary>
    public static string? Header(HttpContext ctx, string name)
    {
        var v = ctx.Request.Headers[name];
        return v.Count > 0 ? v[0] : null;
    }

    public static bool ScopeAuthorized(HttpContext ctx) =>
        RequestAuthorized(Header(ctx, "origin"), Header(ctx, "authorization"));

    /// <summary>
    /// One gate in front of everything, rather than a check per route — a
    /// route added later is covered the day it is written. Sits above the
    /// router and sees WebSocket handshakes as well as plain requests.
    /// </summary>
    public sealed class OriginGuard
    {
        private readonly RequestDelegate _next;

        public OriginGuard(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext ctx)
        {
            bool isWebSocket = ctx.WebSockets.IsWebSocketRequest;
            // Registered before anything else so the refusals written below
            // carry the headers too — they never reach the router.
            if (!isWebSocket)
            {
                var resp = ctx.Response;
                resp.OnStarting(() =>
                {
                    ApplySecurityHeaders(resp.Headers);
                    return Task.CompletedTask;
                });
            }

            // The Host check covers every method, reads included: it is the
            // only evidence of a DNS rebind.
            if (!HostAllowed(Header(ctx, "host")))
            {
                LogRefusal(ctx, $"{(isWebSocket ? "websocket" : "http")} (host)");
                if (isWebSocket)
                    // Refusing before accept: the socket never opens (an HTTP 403).
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                else
                    await WriteJson(ctx, 403, "Refused: that Host is not one JARVIS answers to.");
                return;
            }
            if (isWebSocket)
            {
                if (!ScopeAuthorized(ctx))
                {
                    LogRefusal(ctx, "websocket");
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            else if (MutatingMethods.Contains((ctx.Request.Method ?? "").ToUpperInvariant()))
            {
                if (!ScopeAuthorized(ctx))
                {
                    LogRefusal(ctx, string.IsNullOrEmpty(ctx.Request.Method) ? "?" : ctx.Request.Method);
                    await WriteJson(ctx, 403,
                        "Refused: this request did not come from a page JARVIS serves, and carried no tool token.");
                    return;
                }
            }
            await _next(ctx);
        }

        private static async Task WriteJson(HttpContext ctx, int status, string error)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(Py.Dumps(new Dictionary<string, object?> { ["error"] = error }));
        }

        private static void LogRefusal(HttpContext ctx, string what)
        {
            var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value : "?";
            Log.LogWarning("refused {What} {Path} from origin {Origin}", what, path, Header(ctx, "origin") ?? "<none>");
        }
    }

    // ── urlsplit / ipaddress / int() semantics ──────────────────────────────

    private static readonly Regex SchemeRe = new(@"\A[A-Za-z][A-Za-z0-9+\-.]*:", RegexOptions.CultureInvariant);

    /// <summary>
    /// `urllib.parse.urlsplit(url).hostname`: null when there is no netloc,
    /// lower-cased, brackets and port and userinfo removed. Throws
    /// FormatException where urlsplit raises ValueError (bad brackets).
    /// </summary>
    public static string? UrlHostname(string url)
    {
        var rest = url;
        var m = SchemeRe.Match(rest);
        if (m.Success) rest = rest[m.Length..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) return null;
        rest = rest[2..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        var netloc = end >= 0 ? rest[..end] : rest;
        if ((netloc.Contains('[') && !netloc.Contains(']')) || (netloc.Contains(']') && !netloc.Contains('[')))
            throw new FormatException("Invalid IPv6 URL");
        var hostinfo = netloc[(netloc.LastIndexOf('@') + 1)..];
        string host;
        if (hostinfo.StartsWith('['))
        {
            int close = hostinfo.IndexOf(']');
            if (close < 0) throw new FormatException("Invalid IPv6 URL");
            host = hostinfo[1..close];
            // Newer Pythons refuse a bracketed host that is not an IPv6 literal.
            var ip = ParseIpStrict(host);
            if (ip is null || ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Invalid IPv6 URL");
        }
        else
        {
            int colon = hostinfo.IndexOf(':');
            host = colon >= 0 ? hostinfo[..colon] : hostinfo;
        }
        return host.Length > 0 ? host.ToLowerInvariant() : null;
    }

    /// <summary>
    /// `ipaddress.ip_address(s)` — strict: IPv4 only as a dotted quad of plain
    /// decimals (no "127.1", no hex, no leading zeros), unlike IPAddress.TryParse.
    /// </summary>
    public static IPAddress? ParseIpStrict(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Contains(':'))
            return IPAddress.TryParse(s, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : null;
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Length > 3 || !p.All(c => c is >= '0' and <= '9')) return null;
            if (p.Length > 1 && p[0] == '0') return null;
            if (int.Parse(p, CultureInfo.InvariantCulture) > 255) return null;
        }
        return IPAddress.Parse(s);
    }

    /// <summary>`int(s)` for a port string: surrounding whitespace allowed, optional sign, digits.</summary>
    private static bool TryPyInt(string s, out int value) =>
        int.TryParse(JarvisMemory.PyStrip(s).Replace("_", ""), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
}
