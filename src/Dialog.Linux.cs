using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Dialog on Linux. Same three rules as the Windows/macOS versions (identity
/// not focus, a closed vocabulary, nothing raises); different plumbing.
///
/// **Identity** is the session's controlling terminal, read from
/// `/proc/&lt;pid&gt;/stat` (tty_nr) — what `ps -o tty` printed on macOS — in the
/// canonical form `/dev/pts/N`.
///
/// **Delivery** goes through a terminal multiplexer / terminal that can name
/// the pane owning that exact pty and inject input into it without focusing
/// anything:
/// * tmux — `tmux list-panes -a` gives every pane's `pane_tty`; the key is sent
///   with `tmux send-keys -t %id`.
/// * WezTerm — `wezterm cli list --format json` gives every pane's `tty_name`;
///   the key is sent with `wezterm cli send-text --no-paste --pane-id id`.
///
/// A pty no such host owns (a GNOME Terminal tab, an SSH session without tmux,
/// …) is `not_found`: there is no way to type into it without focusing a window,
/// and there is deliberately no "send it to the front window" fallback.
/// (TIOCSTI is not one either: since Linux 6.2 it needs CAP_SYS_ADMIN for any
/// tty that is not the caller's own.)
///
/// Unlike the other platforms this needs no desktop: a tmux pane is reachable
/// from a headless systemd service just as well, so IsServiceSession is not
/// consulted here.
/// </summary>
public static partial class Dialog
{
    public const int HostTmux = 0;
    public const int HostWezterm = 1;

    private static readonly Regex PtsRe = new(@"^/dev/(pts/[0-9]+|tty[0-9]+)$", RegexOptions.CultureInvariant);

    /// <summary>`/dev/pts/N` (or `/dev/ttyN`) for a pid's controlling terminal, or null.</summary>
    public static string? LinuxTtyForPid(long pid)
    {
        if (pid <= 0 || pid > int.MaxValue) return null;
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch { return null; }
        // comm can contain spaces and parentheses; fields resume after the LAST ')'.
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0]=state [1]=ppid [2]=pgrp [3]=session [4]=tty_nr
        if (fields.Length < 5 || !long.TryParse(fields[4], out var ttyNr) || ttyNr == 0) return null;
        long major = (ttyNr >> 8) & 0xfff;
        long minor = (ttyNr & 0xff) | ((ttyNr >> 12) & 0xfff00);
        string? dev = major switch
        {
            >= 136 and <= 143 => $"/dev/pts/{(major - 136) * 256 + minor}",   // UNIX98 ptys
            4 when minor < 64 => $"/dev/tty{minor}",                         // virtual consoles
            _ => null,
        };
        return dev is not null && PtsRe.IsMatch(dev) ? dev : null;
    }

    /// <summary>Every (host, pane id, tty) the running tmux / WezTerm instances report.</summary>
    private static async Task<List<(int Host, long Pane, string Tty)>> LinuxPanes()
    {
        var panes = new List<(int, long, string)>();
        if (Py.Which("tmux") is { } tmux)
        {
            try
            {
                var r = await Py.Run(tmux, ["list-panes", "-a", "-F", "#{pane_id}\t#{pane_tty}"], timeoutSeconds: LookupTimeout);
                if (r.ReturnCode == 0)
                    foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Trim().Split('\t');
                        if (parts.Length == 2 && parts[0].StartsWith('%') && long.TryParse(parts[0][1..], out var id)
                            && PtsRe.IsMatch(parts[1]))
                            panes.Add((HostTmux, id, parts[1]));
                    }
            }
            catch (Exception e) { Log.LogDebug("tmux list-panes failed: {Error}", e.Message); }
        }
        if (Py.Which("wezterm") is { } wez)
        {
            try
            {
                var r = await Py.Run(wez, ["cli", "list", "--format", "json"], timeoutSeconds: LookupTimeout);
                if (r.ReturnCode == 0 && Py.TryLoads(r.Stdout) is JsonArray arr)
                    foreach (var p in arr)
                    {
                        var tty = Py.Str(p, "tty_name");
                        if (Py.Num(p, "pane_id") is double id && tty is not null && PtsRe.IsMatch(tty))
                            panes.Add((HostWezterm, (long)id, tty));
                    }
            }
            catch (Exception e) { Log.LogDebug("wezterm cli list failed: {Error}", e.Message); }
        }
        return panes;
    }

    /// <summary>True if any pane host (tmux / WezTerm) is installed at all — the cheap early "no".</summary>
    private static bool LinuxHostAvailable() => Py.Which("tmux") is not null || Py.Which("wezterm") is not null;

    /// <summary>The pane that owns `tty`, or null. Exactly one: two hosts claiming one pty is refused.</summary>
    private static async Task<TerminalTab?> LinuxFindTerminalTab(string? tty)
    {
        var want = NormalizeTty(tty);
        if (want is null || !LinuxHostAvailable()) return null;
        var hits = (await LinuxPanes()).Where(p => p.Tty == want).ToList();
        if (hits.Count != 1) return null;
        return new TerminalTab(hits[0].Pane, hits[0].Host, want);
    }

    private static async Task<(string outcome, string? reason)> LinuxAnswerExplained(long pid, string normalized)
    {
        try
        {
            var tty = LinuxTtyForPid(pid);
            if (tty is null) return (NoTty, "that process has no terminal of its own");
            var tab = await LinuxFindTerminalTab(tty);
            if (tab is null)
                return (NotFound, "its terminal is not a tmux or WezTerm pane, so it cannot be typed into without focusing a window");

            // Load-bearing re-check: the pid may have exited and its pty been
            // reused between the lookup and the press.
            if (LinuxTtyForPid(pid) != tab.Tty)
                return (NotFound, "the terminal changed between the lookup and the press");

            Py.ProcResult r;
            if (tab.TabIndex == HostTmux)
            {
                var keyName = normalized switch { "return" => "Enter", "escape" => "Escape", _ => normalized };
                r = await Py.Run(Py.Which("tmux")!, ["send-keys", "-t", $"%{tab.WindowId}", keyName], timeoutSeconds: SendTimeout);
            }
            else
            {
                var text = normalized switch { "return" => "\r", "escape" => "\u001b", _ => normalized };
                r = await Py.Run(Py.Which("wezterm")!,
                    ["cli", "send-text", "--no-paste", "--pane-id", tab.WindowId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    timeoutSeconds: SendTimeout, stdin: text);
            }
            if (r.TimedOut) return (Failed, "the keystroke helper timed out");
            if (r.ReturnCode != 0)
            {
                Log.LogWarning("keystroke helper failed ({Code}): {Err}", r.ReturnCode, r.Stderr.Trim());
                var err = r.Stderr.ToLowerInvariant();
                if (err.Contains("permission denied") || err.Contains("access denied"))
                    return (NotPermitted, "the terminal refused the connection");
                if (err.Contains("can't find pane") || err.Contains("no such pane") || err.Contains("not found"))
                    return (NotFound, "the pane went away before the press");
                return (Failed, "the keystroke helper failed");
            }
            return (Sent, null);
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "answering a dialog for pid {Pid} failed", pid);
            return (Failed, "answering the dialog failed");
        }
    }
}
