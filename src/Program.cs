using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Jplus;

/// <summary>
/// Entry point. One EXE, several roles:
///
///   Jplus [--host H] [--port P] [--ssl|--no-ssl]   run the server (console or as a service)
///   Jplus mcp                                        the brain's stdio MCP child (jarvis_mcp.py)
///   Jplus install ...                                Windows service (sc.exe) / systemd unit (Linux)
///   Jplus uninstall | start | stop
/// </summary>
public static class Program
{
    public const string ServiceName = "Jplus";
    public const string Version = "0.1.0";

    private static string[] _serverArgs = [];
    private static IHost? _host;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "mcp": return await JarvisMcp.RunMain(args[1..]);
                case "install": return Py.IsWindows ? ServiceInstaller.Install(args[1..]) : SystemdInstaller.Install(args[1..]);
                case "uninstall": return Py.IsWindows ? ServiceInstaller.Uninstall() : SystemdInstaller.Uninstall(args[1..]);
                case "start": return Py.IsWindows ? ServiceInstaller.Sc("start", ServiceName) : SystemdInstaller.Ctl(args[1..], "start");
                case "stop": return Py.IsWindows ? ServiceInstaller.Sc("stop", ServiceName) : SystemdInstaller.Ctl(args[1..], "stop");
                case "--help" or "-h" or "help": PrintHelp(); return 0;
            }
        }
        return await RunServer(args);
    }

    private static void PrintHelp()
    {
        if (!Py.IsWindows)
        {
            Console.WriteLine($"""
                Jplus {Version} — JARVIS for Claude Code, Linux edition

                  jplus [--host 127.0.0.1] [--port 8340] [--ssl | --no-ssl]
                      Run the server in this terminal (Ctrl+C to stop).
                  jplus install [--system --user NAME] [--desktop | --headless] [--host H] [--port P] [--no-ssl]
                      Install as the systemd service "{SystemdInstaller.UnitName}". Default: a per-user unit
                      (systemctl --user) for the account logged in to Claude Code. --desktop ties it to the
                      graphical session so it can open windows, screenshot and notify (the default when run
                      from a desktop terminal); --headless starts it at boot/login without a desktop.
                      --system writes a system unit running as --user NAME (needs root; headless).
                  jplus uninstall | start | stop [--system]
                  jplus mcp
                      The brain's stdio MCP child. Spawned by Claude Code, not by you.

                Data lives in JARVIS_DATA_DIR, default <binary dir>/data. Settings in <binary dir>/.env.
                """);
            return;
        }
        Console.WriteLine($"""
            Jplus {Version} — JARVIS for Claude Code, Windows edition

              Jplus.exe [--host 127.0.0.1] [--port 8340] [--ssl | --no-ssl]
                  Run the server in this console (Ctrl+C to stop).
              Jplus.exe install --account .\user [--password pw] [--host H] [--port P]
                  Install as the Windows service "{ServiceName}" running as that account
                  (it must be the account that is logged in to Claude Code).
              Jplus.exe uninstall | start | stop
              Jplus.exe mcp
                  The brain's stdio MCP child. Spawned by Claude Code, not by you.

            Data lives in JARVIS_DATA_DIR, default <exe dir>\data. Settings in <exe dir>\.env.
            """);
    }

    // ── the server ──────────────────────────────────────────────────────────

    private static async Task<int> RunServer(string[] args)
    {
        _serverArgs = args;
        string host = Flag(args, "--host") ?? WebAuth.JarvisDefaultHost;
        int port = int.TryParse(Flag(args, "--port"), out var p) ? p : 8340;
        bool noSsl = args.Contains("--no-ssl");

        // Services start in System32; everything relative is relative to the EXE.
        Directory.SetCurrentDirectory(Py.AppDir);

        // `.env` is read before anything else looks at the environment, exactly
        // like the module-level loader at the top of server.py.
        Server.BootLoadEnv();
        Passcode.Init();

        var cert = noSsl ? null : LoadOrCreateCertificate();
        var scheme = cert is null ? "http" : "https";

        // Recorded so the MCP config, the origin allowlist and the Host check
        // all agree with what we actually bound (server.py's __main__ block).
        Environment.SetEnvironmentVariable("JARVIS_PORT", port.ToString());
        Environment.SetEnvironmentVariable("JARVIS_SCHEME", scheme);
        Environment.SetEnvironmentVariable("JARVIS_BIND_HOST", host);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = Py.AppDir,
        });
        if (OperatingSystem.IsWindows()) builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        else builder.Host.UseSystemd();   // sd_notify READY/STOPPING under Type=notify; a no-op elsewhere
        builder.Logging.ClearProviders();
        if (!OperatingSystem.IsWindows() && SystemdHelpers.IsSystemdService())
            builder.Logging.AddSystemdConsole(o => o.TimestampFormat = null);   // journald adds its own
        else
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(DataPaths.DataDir(), "jplus.log")));
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) builder.Logging.AddEventLog(o => o.SourceName = ServiceName);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Information);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            void Listen(IPAddress ip) => k.Listen(ip, port, lo =>
            {
                lo.Protocols = HttpProtocols.Http1AndHttp2;
                if (cert is not null) lo.UseHttps(cert);
            });
            if (host is "localhost") { Listen(IPAddress.Loopback); Listen(IPAddress.IPv6Loopback); }
            else if (host is "0.0.0.0" or "*") Listen(IPAddress.Any);
            else if (host is "::") Listen(IPAddress.IPv6Any);
            else Listen(IPAddress.Parse(host.Trim('[', ']')));
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Py.Json.PropertyNamingPolicy;
            o.SerializerOptions.DictionaryKeyPolicy = null;
            o.SerializerOptions.Encoder = Py.Json.Encoder;
            o.SerializerOptions.NumberHandling = Py.Json.NumberHandling;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddHostedService<Lifespan>();

        var app = builder.Build();
        _host = app;
        Py.LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        Console.WriteLine();
        Console.WriteLine($"  J.A.R.V.I.S. Server v{Version} (Jplus)");
        Console.WriteLine($"  WebSocket: {(cert is null ? "ws" : "wss")}://{host}:{port}/ws/voice");
        Console.WriteLine($"  REST API:  {scheme}://{host}:{port}/api/");
        Console.WriteLine($"  Dashboard: {scheme}://{host}:{port}/dashboard");
        Console.WriteLine();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseMiddleware<WebAuth.OriginGuard>();
        app.UseMiddleware<Passcode.Gate>();

        Server.MapRoutes1(app);
        Server.MapRoutes2(app);
        Server.MapRoutes3(app);
        MapFrontend(app);

        await app.RunAsync();
        return 0;
    }

    /// <summary>Lifespan: FastAPI's startup/shutdown halves.</summary>
    private sealed class Lifespan : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Server.LifespanStart();
        public Task StopAsync(CancellationToken ct) => Server.LifespanStop();
    }

    /// <summary>The built Vite frontend, straight out of the EXE.</summary>
    private static void MapFrontend(WebApplication app)
    {
        var dist = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "frontend/dist");
        var types = new FileExtensionContentTypeProvider();
        IResult Serve(string path)
        {
            var f = dist.GetFileInfo(path);
            if (!f.Exists || f.IsDirectory) return Results.NotFound();
            types.TryGetContentType(path, out var ct);
            return Results.Stream(f.CreateReadStream(), ct ?? "application/octet-stream");
        }
        if (!dist.GetFileInfo("index.html").Exists) return;
        app.MapGet("/", () => Serve("index.html"));
        app.MapGet("/dashboard", () => Serve("dashboard.html"));
        app.MapGet("/assets/{**file}", (string file) => Serve("assets/" + file));
    }

    /// <summary>
    /// `/api/restart`. As a console app: spawn ourselves again with the same
    /// arguments, then exit. As a service: exit non-zero and let the SCM's
    /// recovery action / systemd's `Restart=on-failure` (both set by `install`)
    /// start us again.
    /// </summary>
    public static void Restart()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!SystemdHelpers.IsSystemdService())
            {
                // Same terminal, same arguments: the new process inherits our stdio.
                var lpsi = new ProcessStartInfo(Py.ExePath) { UseShellExecute = false, WorkingDirectory = Py.AppDir };
                foreach (var a in _serverArgs) lpsi.ArgumentList.Add(a);
                Process.Start(lpsi);
                Environment.Exit(0);
            }
            Environment.Exit(1);
        }
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            var psi = new ProcessStartInfo(Py.ExePath) { UseShellExecute = true, WorkingDirectory = Py.AppDir };
            foreach (var a in _serverArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Environment.Exit(0);
        }
        Environment.Exit(1);
    }

    public static string? Flag(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=")) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    // ── TLS ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// cert.pem / key.pem beside the EXE, as server.py used — and generated
    /// (self-signed, CN=localhost, SAN localhost/127.0.0.1/::1) when absent,
    /// since the Python README's openssl step is not optional either.
    /// </summary>
    private static X509Certificate2? LoadOrCreateCertificate()
    {
        var certFile = Path.Combine(Py.AppDir, "cert.pem");
        var keyFile = Path.Combine(Py.AppDir, "key.pem");
        try
        {
            if (!File.Exists(certFile) || !File.Exists(keyFile))
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                san.AddIpAddress(IPAddress.IPv6Loopback);
                req.CertificateExtensions.Add(san.Build());
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
                using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                File.WriteAllText(certFile, made.ExportCertificatePem());
                File.WriteAllText(keyFile, rsa.ExportPkcs8PrivateKeyPem());
                if (!Py.IsWindows) Posix.Chmod600(keyFile);
            }
            using var pem = X509Certificate2.CreateFromPemFile(certFile, keyFile);
            // Re-import so SChannel gets a persisted key (ephemeral PEM keys fail on Windows;
            // harmless on Linux, where OpenSSL takes either).
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  TLS disabled: {e.Message}");
            return null;
        }
    }
}

/// <summary>Plain append-only log file beside the data, so a service has somewhere to say things.</summary>
internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string category) => new L(this, category);
    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 10_000_000) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }
    }

    private sealed class L(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            owner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-11} {category}: {fmt(state, ex)}{(ex is null ? "" : " | " + ex)}");
        }
    }
}

/// <summary>`install` / `uninstall` via sc.exe — the service must log on as the Claude Code user.</summary>
internal static class ServiceInstaller
{
    public static int Install(string[] args)
    {
        var account = Program.Flag(args, "--account");
        if (string.IsNullOrWhiteSpace(account))
        {
            Console.Error.WriteLine("install needs --account .\\<user>: the service must run as the Windows account\n" +
                                    "that is logged in to Claude Code (its ~/.claude is where the login lives).");
            return 2;
        }
        var password = Program.Flag(args, "--password");
        if (password is null)
        {
            Console.Write($"Password for {account}: ");
            password = ReadMasked();
        }
        var passthrough = new List<string>();
        foreach (var f in new[] { "--host", "--port" })
            if (Program.Flag(args, f) is { } v) { passthrough.Add(f); passthrough.Add(v); }
        if (args.Contains("--no-ssl")) passthrough.Add("--no-ssl");

        var binPath = $"\"{Py.ExePath}\"" + (passthrough.Count > 0 ? " " + string.Join(" ", passthrough) : "");
        int rc = Sc("create", Program.ServiceName, "binPath=", binPath, "start=", "auto",
                    "obj=", account, "password=", password, "DisplayName=", "Jplus (JARVIS for Claude Code)");
        if (rc != 0) return rc;
        Sc("description", Program.ServiceName, "Voice-first assistant driving Claude Code; dashboard on https://localhost:8340/dashboard");
        // /api/restart exits non-zero as a service; these bring it back.
        Sc("failure", Program.ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000/restart/30000");
        Sc("failureflag", Program.ServiceName, "1");
        Console.WriteLine($"Installed. Start it with: sc start {Program.ServiceName}   (or: Jplus.exe start)");
        return 0;
    }

    public static int Uninstall()
    {
        Sc("stop", Program.ServiceName);
        return Sc("delete", Program.ServiceName);
    }

    public static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string ReadMasked()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
