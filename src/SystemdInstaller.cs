using System.Diagnostics;
using System.Text;

namespace Jplus;

/// <summary>
/// `jplus install | uninstall | start | stop` on Linux: a systemd unit.
///
/// The default is a **per-user** unit (`systemctl --user`) for the account that
/// is logged in to Claude Code — its `~/.claude` holds the login and the session
/// roster, exactly why the Windows service must log on as that user.
///
/// * `--desktop` (the default when installed from a graphical terminal) binds the
///   unit to `graphical-session.target`, so it starts with the desktop and
///   inherits DISPLAY / WAYLAND_DISPLAY: windows, screenshots, notifications work.
/// * `--headless` starts it with the user manager (`default.target`); add
///   `loginctl enable-linger` to have it up at boot without a login.
/// * `--system --user NAME` writes `/etc/systemd/system/jplus.service` running
///   as NAME (root needed). Always headless.
///
/// `Type=notify` (Program calls UseSystemd) and `Restart=on-failure`, which is
/// what brings `/api/restart` back: as a service it exits non-zero.
/// </summary>
internal static class SystemdInstaller
{
    public const string UnitName = "jplus.service";

    private static bool IsSystem(string[] args) => args.Contains("--system");

    private static string UnitPath(bool system) => system
        ? Path.Combine("/etc/systemd/system", UnitName)
        : Path.Combine(Py.Getenv("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Py.Home, ".config"),
            "systemd", "user", UnitName);

    public static int Install(string[] args)
    {
        bool system = IsSystem(args);
        string? user = Program.Flag(args, "--user");
        if (system && string.IsNullOrWhiteSpace(user))
        {
            Console.Error.WriteLine("install --system needs --user NAME: the service must run as the account\n" +
                                    "that is logged in to Claude Code (its ~/.claude is where the login lives).");
            return 2;
        }
        bool desktop = !system && !args.Contains("--headless")
                       && (args.Contains("--desktop") || !Py.IsServiceSession);

        var exec = new List<string> { Py.ExePath };
        foreach (var f in new[] { "--host", "--port" })
            if (Program.Flag(args, f) is { } v) { exec.Add(f); exec.Add(v); }
        if (args.Contains("--no-ssl")) exec.Add("--no-ssl");

        var u = new StringBuilder();
        u.AppendLine("[Unit]");
        u.AppendLine("Description=Jplus (JARVIS for Claude Code)");
        if (system) u.AppendLine("After=network-online.target\nWants=network-online.target");
        if (desktop) u.AppendLine("PartOf=graphical-session.target\nAfter=graphical-session.target");
        u.AppendLine();
        u.AppendLine("[Service]");
        u.AppendLine("Type=notify");
        u.AppendLine("ExecStart=" + string.Join(" ", exec.Select(Quote)));
        u.AppendLine("WorkingDirectory=" + Py.AppDir.Replace("%", "%%"));   // a path, not a quoted word
        if (system) u.AppendLine("User=" + user);
        // The user manager's PATH rarely includes ~/.local/bin, nvm or npm-global,
        // where `claude` usually lives; the installing shell's PATH does.
        if (!system && Py.Getenv("PATH") is { Length: > 0 } path)
            u.AppendLine("Environment=" + Quote("PATH=" + path));
        u.AppendLine("Restart=on-failure");
        u.AppendLine("RestartSec=2");
        u.AppendLine("TimeoutStopSec=20");
        u.AppendLine();
        u.AppendLine("[Install]");
        u.AppendLine("WantedBy=" + (system ? "multi-user.target" : desktop ? "graphical-session.target" : "default.target"));

        var unitPath = UnitPath(system);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
            File.WriteAllText(unitPath, u.ToString(), Py.Utf8NoBom);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not write {unitPath}: {e.Message}" + (system ? " (run as root)" : ""));
            return 1;
        }
        Console.WriteLine($"Wrote {unitPath}");
        int rc = Systemctl(system, "daemon-reload");
        if (rc == 0) rc = Systemctl(system, "enable", UnitName);
        if (rc != 0) return rc;

        var ctl = system ? "systemctl" : "systemctl --user";
        Console.WriteLine($"Installed ({(system ? $"system unit, runs as {user}" : desktop ? "user unit, desktop session" : "user unit, headless")}).");
        Console.WriteLine($"Start it with: jplus start{(system ? " --system" : "")}   (or: {ctl} start {UnitName})");
        Console.WriteLine($"Logs:          journalctl {(system ? "" : "--user ")}-u {UnitName} -f");
        if (!system && !desktop)
            Console.WriteLine($"To run it at boot without logging in: loginctl enable-linger {Environment.UserName}");
        if (desktop)
            Console.WriteLine("Desktop features need DISPLAY / WAYLAND_DISPLAY in the user manager; GNOME and KDE export\n" +
                              "them automatically. Elsewhere add to your session startup:\n" +
                              "  systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XAUTHORITY XDG_RUNTIME_DIR");
        return 0;
    }

    public static int Uninstall(string[] args)
    {
        bool system = IsSystem(args);
        Systemctl(system, "disable", "--now", UnitName);
        var unitPath = UnitPath(system);
        try { if (File.Exists(unitPath)) File.Delete(unitPath); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not remove {unitPath}: {e.Message}");
            return 1;
        }
        return Systemctl(system, "daemon-reload");
    }

    public static int Ctl(string[] args, string verb) => Systemctl(IsSystem(args), verb, UnitName);

    private static int Systemctl(bool system, params string[] args)
    {
        var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false };
        if (!system) psi.ArgumentList.Add("--user");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"systemctl could not run: {e.Message}");
            return 1;
        }
    }

    /// <summary>One word for a systemd unit file: double-quoted, with `\`, `"` and `%` (specifiers) escaped.</summary>
    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";
}
