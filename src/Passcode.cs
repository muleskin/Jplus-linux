using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// An optional passcode in front of everything, for when JARVIS is reachable
/// from somewhere other than this machine.
///
/// WebAuth keeps hostile *pages* out, but it trusts any browser that opens
/// JARVIS's own page — fine on loopback, not on a public address, where that
/// browser can be anyone's. With `JARVIS_PASSCODE` set, a browser must first
/// sign in at `/login`; it then carries a signed session cookie. Unset, this
/// does nothing and JARVIS behaves exactly as before.
///
/// The cookie is `expiry.HMAC(key, expiry)` with the key derived from the
/// per-install tool token AND the passcode, so sessions survive a restart and
/// changing the passcode signs every browser out. Callers holding the tool
/// token (the brain's MCP child) are let through as WebAuth lets them through.
/// </summary>
public static class Passcode
{
    public static readonly ILogger Log = Py.Log("jarvis.passcode");

    public const string CookieName = "jarvis_session";
    public const string EnvName = "JARVIS_PASSCODE";

    // Per client: this many wrong passcodes inside the window locks it out for the window.
    public const int MaxFailuresPerClient = 5;
    // Across all clients: a spray from many addresses locks sign-in for everyone.
    public const int MaxFailuresGlobal = 30;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    // Reachable without a session: the sign-in itself, and a liveness probe that says nothing.
    private static readonly HashSet<string> OpenPaths = new(StringComparer.Ordinal) { "/login", "/api/health" };

    private static byte[]? _passcodeHash;
    private static byte[]? _key;
    private static TimeSpan _sessionLength = TimeSpan.FromDays(30);

    private sealed class Failures { public int Count; public DateTime WindowStart; public DateTime LockedUntil; }
    private static readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private static readonly Failures _global = new();

    public static bool Enabled => _passcodeHash is not null;

    /// <summary>
    /// Read the passcode (after `.env` is loaded) and drop it from the
    /// environment, so no child process — the brain included — inherits it.
    /// </summary>
    public static void Init()
    {
        var passcode = Environment.GetEnvironmentVariable(EnvName);
        Environment.SetEnvironmentVariable(EnvName, null);
        if (string.IsNullOrEmpty(passcode)) return;
        var raw = JarvisMemory.PyStrip(Py.Getenv("JARVIS_SESSION_DAYS", "") ?? "");
        if (raw.Length > 0)
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0)
                _sessionLength = TimeSpan.FromDays(days);
            else
                Log.LogWarning("ignoring JARVIS_SESSION_DAYS that is not a positive number: {Raw}", raw);
        }
        _passcodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(passcode));
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(DataPaths.EnsureToolToken()),
                                   Encoding.UTF8.GetBytes("jarvis-session-v1|" + passcode));
        _short = passcode.Length < 8;
    }

    private static bool _short;

    /// <summary>Logged with the bind (Init runs before logging is set up).</summary>
    public static void LogStatus()
    {
        if (!Enabled) return;
        Log.LogInformation("passcode sign-in enabled (sessions last {Days:0.##} days)", _sessionLength.TotalDays);
        if (_short)
            Log.LogWarning("JARVIS_PASSCODE is shorter than 8 characters; use a longer one if JARVIS is reachable from the internet");
    }

    // ── session cookie ──────────────────────────────────────────────────────

    private static string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewSession(DateTimeOffset expires)
    {
        var payload = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return payload + "." + Sign(payload);
    }

    public static bool SessionValid(string? cookie)
    {
        if (_key is null || string.IsNullOrEmpty(cookie)) return false;
        var dot = cookie.IndexOf('.');
        if (dot <= 0) return false;
        var payload = cookie[..dot];
        if (!long.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry) return false;
        var a = Encoding.ASCII.GetBytes(cookie[(dot + 1)..]);
        var b = Encoding.ASCII.GetBytes(Sign(payload));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool PasscodeMatches(string? supplied) =>
        supplied is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), _passcodeHash!);

    // Behind a TLS-terminating proxy (Traefik) the request reaching us is plain HTTP.
    private static bool IsHttps(HttpContext ctx) =>
        ctx.Request.IsHttps ||
        string.Equals(WebAuth.Header(ctx, "x-forwarded-proto"), "https", StringComparison.OrdinalIgnoreCase);

    private static CookieOptions CookieOpts(HttpContext ctx, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = IsHttps(ctx),
        // Lax, not Strict: Strict would drop the cookie when following a link to JARVIS.
        // Cross-site writes are WebAuth's Origin check's job either way.
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
    };

    // ── brute-force limits ──────────────────────────────────────────────────

    /// <summary>
    /// The client's address. Behind a proxy on this machine or a private
    /// network, the proxy's own address is everyone's, so the last hop it
    /// appended to X-Forwarded-For is used instead. Otherwise the header is
    /// the client's to forge and is ignored.
    /// </summary>
    private static string ClientKey(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is not null && IsPrivate(remote))
        {
            var xff = WebAuth.Header(ctx, "x-forwarded-for");
            var last = xff?.Split(',').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0);
            if (last is not null && IPAddress.TryParse(last, out var fwd)) return fwd.ToString();
        }
        return remote?.ToString() ?? "?";
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    private static bool Locked(Failures f, DateTime now)
    {
        lock (f) return f.LockedUntil > now;
    }

    private static void RecordFailure(Failures f, int max, DateTime now)
    {
        lock (f)
        {
            if (now - f.WindowStart > FailureWindow) { f.WindowStart = now; f.Count = 0; }
            if (++f.Count >= max) { f.LockedUntil = now + FailureWindow; f.Count = 0; f.WindowStart = now; }
        }
    }

    // ── the gate ────────────────────────────────────────────────────────────

    /// <summary>Sits after WebAuth.OriginGuard, so the Host and Origin checks still come first.</summary>
    public sealed class Gate
    {
        private readonly RequestDelegate _next;

        public Gate(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? "/";
            var method = (ctx.Request.Method ?? "").ToUpperInvariant();
            // Lets the dashboard decide whether to show "Sign out". Says no more
            // than the /login redirect already does.
            if (path == "/api/auth" && method is "GET" or "HEAD")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(Py.Dumps(new Dictionary<string, object?> { ["enabled"] = Enabled }));
                return;
            }
            if (!Enabled) { await _next(ctx); return; }

            if (path == "/login")
            {
                if (method == "POST") await SignIn(ctx);
                else await LoginPage(ctx, SafeNext(ctx.Request.Query["next"]), null, 200);
                return;
            }
            if (path == "/logout" && method == "POST")
            {
                ctx.Response.Cookies.Delete(CookieName, CookieOpts(ctx, DateTimeOffset.UnixEpoch));
                ctx.Response.Redirect("/login");
                return;
            }

            if (OpenPaths.Contains(path)
                || SessionValid(ctx.Request.Cookies[CookieName])
                || WebAuth.TokenMatches(WebAuth.Bearer(WebAuth.Header(ctx, "authorization"))))
            {
                await _next(ctx);
                return;
            }

            if (!ctx.WebSockets.IsWebSocketRequest && method is "GET" or "HEAD" && (path is "/" or "/dashboard"))
            {
                ctx.Response.Redirect("/login?next=" + Uri.EscapeDataString(path));
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            if (ctx.WebSockets.IsWebSocketRequest) return;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(Py.Dumps(new Dictionary<string, object?> { ["error"] = "Sign in at /login first." }));
        }

        private static async Task SignIn(HttpContext ctx)
        {
            var now = DateTime.UtcNow;
            var client = ClientKey(ctx);
            var mine = _failures.GetOrAdd(client, _ => new Failures());
            string? next = "/";
            string? supplied = null;
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync();
                supplied = form["passcode"];
                next = SafeNext(form["next"]);
            }

            if (Locked(_global, now) || Locked(mine, now))
            {
                Log.LogWarning("sign-in refused while locked out, from {Client}", client);
                await LoginPage(ctx, next, "Too many wrong attempts. Try again later.", 429);
                return;
            }
            if (!PasscodeMatches(supplied))
            {
                RecordFailure(mine, MaxFailuresPerClient, now);
                RecordFailure(_global, MaxFailuresGlobal, now);
                Log.LogWarning("wrong passcode from {Client}", client);
                await Task.Delay(TimeSpan.FromSeconds(1));
                await LoginPage(ctx, next, "That passcode is not right.", 401);
                return;
            }

            _failures.TryRemove(client, out _);
            var expires = DateTimeOffset.UtcNow + _sessionLength;
            ctx.Response.Cookies.Append(CookieName, NewSession(expires), CookieOpts(ctx, expires));
            Log.LogInformation("signed in from {Client}", client);
            ctx.Response.Redirect(next);
        }

        /// <summary>Only a path on this server, never `//elsewhere` (an open redirect).</summary>
        private static string SafeNext(string? next) =>
            next is { Length: > 0 } && next[0] == '/' && !next.StartsWith("//") && !next.Contains('\\') ? next : "/";

        private static async Task LoginPage(HttpContext ctx, string next, string? error, int status)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            var err = error is null ? "" : $"<p class=\"err\">{WebUtility.HtmlEncode(error)}</p>";
            await ctx.Response.WriteAsync($$"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>J.A.R.V.I.S. sign-in</title>
                <style>
                  body { margin: 0; min-height: 100vh; display: grid; place-items: center;
                         background: #05080d; color: #cfe8ff; font: 16px system-ui, sans-serif; }
                  form { width: min(320px, calc(100vw - 32px)); display: grid; gap: 14px; }
                  h1 { margin: 0 0 6px; font-size: 20px; letter-spacing: .3em; text-align: center; color: #6fd3ff; }
                  input, button { font: inherit; padding: 12px; border-radius: 8px; border: 1px solid #1f4a66; }
                  input { background: #0b1520; color: inherit; }
                  button { background: #1c6e9c; color: #fff; border: 0; cursor: pointer; }
                  .err { margin: 0; color: #ff8a8a; text-align: center; }
                </style></head>
                <body><form method="post" action="/login">
                  <h1>J.A.R.V.I.S.</h1>
                  {{err}}
                  <input type="password" name="passcode" placeholder="Passcode" autocomplete="current-password" autofocus required>
                  <input type="hidden" name="next" value="{{WebUtility.HtmlEncode(next)}}">
                  <button type="submit">Sign in</button>
                </form></body></html>
                """);
        }
    }
}
