using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Press one key in the console a Claude Code session is running in (port of dialog.py).
///
/// This is the most dangerous module in the project: it sends a synthetic
/// keystroke to a session on the user's machine. Aimed wrong, it types into
/// whatever the user is actually working in. Every decision here exists to make
/// that impossible.
///
/// **1. The target is found by identity, never by focus.** macOS mapped pid → tty
/// → Terminal.app tab. On Windows the identity is the session's own console:
/// a helper attaches to the console of that exact pid (AttachConsole) and reads
/// its console window handle; the key is then written straight into that
/// console's input buffer (WriteConsoleInput on CONIN$). No window is focused
/// or activated, so nothing the user is typing into can receive it. If the
/// console cannot be identified we return `not_found` / `no_tty` and press
/// NOTHING. There is deliberately no "send it to the front window" fallback.
///
/// **2. The vocabulary is closed.** Return, Escape and a single digit 1-9. That is
/// the whole set, and it is a safety boundary: anything else is refused before
/// any helper script is composed. This module never types text.
///
/// **3. Nothing here raises.** Every path returns an outcome string.
///
/// What can and cannot be reached, as verified on Windows 11 (2026-10):
/// * A classic conhost window: Return, Escape and digits all arrive (tested
///   against a throwaway console process reading keys).
/// * A pseudo console (ConPTY — Windows Terminal, VS Code): also reachable. The
///   key goes into that session's own conhost input buffer, where its client
///   reads it, exactly as tested with a private ConPTY harness; the terminal
///   window is never touched or focused.
/// * A session whose console has no window (the Claude desktop app starts its
///   sessions that way — verified) is `no_tty`.
/// * A console owned by an elevated process is expected to refuse AttachConsole
///   (ERROR_ACCESS_DENIED): `not_permitted`. Not exercised in testing.
/// * Running as a Windows service (session 0) there is no desktop: `failed`,
///   with Py.NoDesktopReason as the explanation.
///
/// AttachConsole is per process, and the server may own a console of its own, so
/// the attach/write is done in a short-lived `powershell.exe` helper rather than
/// in-process (detaching the server from its console would break its logging).
/// </summary>
public static partial class Dialog
{
    private static readonly ILogger Log = Py.Log("jarvis.dialog");

    // --- outcomes ---------------------------------------------------------------
    public const string Sent = "sent";
    public const string NoTty = "no_tty";              // the pid is dead, or has no console
    public const string NotFound = "not_found";        // the console window is gone, or changed before the press
    public const string NotPermitted = "not_permitted"; // Windows refused the attach (elevated target)
    public const string Failed = "failed";             // the helper died, timed out, or said something odd
    public const string BadKey = "bad_key";            // defensive: a key outside the closed vocabulary

    // How long a helper may run before we stop waiting and kill it. A hung helper
    // must not wedge the caller, which is a voice turn with a person waiting.
    public const double LookupTimeout = 10.0;
    public const double SendTimeout = 20.0;

    // The macOS `ps` ceiling. Kept for reference; the Windows lookup is a helper
    // process (PowerShell + a small compiled type), which needs LookupTimeout.
    public const double PsTimeout = 1.0;

    // The closed vocabulary as virtual-key codes, scan codes and characters —
    // a key code cannot be reinterpreted as text.
    public const ushort ReturnKeyCode = 0x0D;
    public const ushort EscapeKeyCode = 0x1B;

    public static readonly Dictionary<string, string> Aliases = new()
    {
        ["enter"] = "return",
        ["return"] = "return",
        ["yes"] = "return",       // "yes" answers a permission prompt with Return
        ["y"] = "return",
        ["escape"] = "escape",
        ["esc"] = "escape",
        ["cancel"] = "escape",
        ["no"] = "escape",
        ["n"] = "escape",
    };

    // Win32 errors that mean Windows is refusing us, not that the helper is broken.
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidHandle = 6;       // the process has no console
    public const int ErrorInvalidParameter = 87;   // the process does not exist

    /// <summary>
    /// Enough to re-find one console, plus the identity that found it — kept so
    /// the send can re-check the identity at press time. (macOS: window id + tab
    /// index + tty; here the console window handle plays both parts.)
    /// </summary>
    public sealed record TerminalTab(long WindowId, int TabIndex, string Tty);

    /// <summary>
    /// The closed vocabulary, or null. Null means REFUSE — never interpret.
    /// Returns "return", "escape", or a single digit "1".."9".
    /// </summary>
    public static string? NormalizeKey(object? key)
    {
        if (key is not string s) return null;
        var k = s.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(k, out var v)) return v;
        if (k.Length == 1 && "123456789".Contains(k[0])) return k;
        return null;
    }

    /// <summary>How the read-back names the key. Must match what actually gets sent.</summary>
    public static string SpokenKey(string normalized) => normalized switch
    {
        "return" => "Return",
        "escape" => "Escape",
        _ => normalized,
    };

    private static readonly Regex TtyRe = new("^(console|conpty):[0-9a-f]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The console identity in canonical form (`console:&lt;hwnd hex&gt;` for a classic
    /// conhost window, `conpty:&lt;hwnd hex&gt;` for a pseudo console), or null.
    /// Compared exactly — never a prefix test. Anything stranger is refused, which
    /// keeps unvetted text out of the helper script.
    /// </summary>
    public static string? NormalizeTty(string? tty)
    {
        if (string.IsNullOrEmpty(tty)) return null;
        if (!Py.IsWindows)
        {
            var lt = tty.Trim();
            return PtsRe.IsMatch(lt) ? lt : null;
        }
        var t = tty.Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "??") return null;
        return TtyRe.IsMatch(t) ? t : null;
    }

    /// <summary>
    /// The console identity for a live pid with a console, else null — a dead pid,
    /// a process without a console, a refused attach, or anything unparseable.
    /// Synchronous; anything async must use TtyForPidAsync.
    /// </summary>
    public static string? TtyForPid(long pid) => TtyForPidAsync(pid).GetAwaiter().GetResult();

    /// <summary>TtyForPid off the caller's thread: the helper runs as an async subprocess.</summary>
    public static async Task<string?> TtyForPidAsync(long pid)
    {
        if (!Py.IsWindows) return LinuxTtyForPid(pid);
        var (tty, _) = await Probe(pid);
        return tty;
    }

    /// <summary>(identity, Win32 error from the attach or 0).</summary>
    private static async Task<(string? tty, int error)> Probe(long pid)
    {
        if (pid <= 0 || pid > uint.MaxValue) return (null, ErrorInvalidParameter);
        if (Py.IsServiceSession) return (null, ErrorAccessDenied);
        if (!Py.PidAlive((int)Math.Min(pid, int.MaxValue))) return (null, ErrorInvalidParameter);
        try
        {
            var (code, stdout, stderr) = await RunHelper(HelperScript("probe", (uint)pid, "", "return"), LookupTimeout);
            var result = stdout.Trim();
            if (code != 0)
            {
                Log.LogWarning("console lookup for pid {Pid} failed ({Code}): {Err}", pid, code, stderr.Trim());
                return (null, -1);
            }
            // A console with no window at all (CREATE_NO_WINDOW — how the desktop
            // app starts its sessions) is no terminal anyone can see: no_tty.
            if (result == "tty:console:0") return (null, ErrorInvalidHandle);
            if (result.StartsWith("tty:", StringComparison.Ordinal)) return (NormalizeTty(result[4..]), 0);
            if (result.StartsWith("err:", StringComparison.Ordinal) && int.TryParse(result[4..], out var err))
                return (null, err);
            Log.LogWarning("unexpected console lookup result for pid {Pid}: {Result}", pid, result);
            return (null, -1);
        }
        catch (Exception e)
        {
            Log.LogWarning("console lookup for pid {Pid} failed: {Error}", pid, e.Message);
            return (null, -1);
        }
    }

    /// <summary>
    /// True only if some console host is running. The macOS original checked with
    /// pgrep so a lookup could never LAUNCH Terminal; nothing here launches
    /// anything, but the check keeps the same cheap early "no".
    /// </summary>
    public static bool TerminalIsRunning()
    {
        if (!Py.IsWindows) return LinuxHostAvailable();
        try
        {
            foreach (var name in new[] { "conhost", "OpenConsole" })
            {
                var procs = Process.GetProcessesByName(name);
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Run one helper script. Returns (returncode, stdout, stderr); a timeout is -1.</summary>
    public static async Task<(int code, string stdout, string stderr)> RunHelper(string script, double timeout)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var r = await Py.Run("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            timeoutSeconds: timeout);
        if (r.TimedOut)
        {
            Log.LogWarning("console helper timed out after {Timeout}s", timeout);
            return (-1, "", "timeout");
        }
        return (r.ReturnCode, r.Stdout, r.Stderr);
    }

    public static bool IsPermissionError(int win32Error) => win32Error == ErrorAccessDenied;

    /// <summary>
    /// The console window that owns `tty` (a classic conhost window or a pseudo
    /// console's hidden window), or null when that window no longer exists. Null
    /// stops this module from acting; it is never upgraded into a best guess.
    /// </summary>
    public static Task<TerminalTab?> FindTerminalTab(string? tty)
    {
        if (!Py.IsWindows) return LinuxFindTerminalTab(tty);
        var want = NormalizeTty(tty);
        if (want is null) return Task.FromResult<TerminalTab?>(null);
        if (!TerminalIsRunning()) return Task.FromResult<TerminalTab?>(null);
        long hwnd;
        try { hwnd = Convert.ToInt64(want[(want.IndexOf(':') + 1)..], 16); }
        catch { return Task.FromResult<TerminalTab?>(null); }
        if (hwnd == 0 || !IsWindow(new IntPtr(hwnd))) return Task.FromResult<TerminalTab?>(null);
        return Task.FromResult<TerminalTab?>(new TerminalTab(hwnd, 1, want));
    }

    /// <summary>
    /// The one helper script: attach to the pid's console, re-check identity,
    /// write a key-down/key-up pair into its input buffer, detach.
    ///
    /// Load-bearing: it re-reads the console identity and ABORTS ("moved") if it no
    /// longer matches — the pid may have exited and its console been reused
    /// between lookup and press. And only integer literals and the already
    /// validated identity are interpolated: `normalized` has been through
    /// NormalizeKey and `expect` through NormalizeTty, so nothing user-authored is
    /// ever substituted into this script.
    /// </summary>
    public static string HelperScript(string mode, uint pid, string expect, string normalized)
    {
        ushort vk, scan, ch;
        if (normalized == "return") { vk = ReturnKeyCode; scan = 0x1C; ch = 0x0D; }
        else if (normalized == "escape") { vk = EscapeKeyCode; scan = 0x01; ch = 0x1B; }
        else
        {
            int d = normalized[0] - '0';             // "1".."9" only (NormalizeKey)
            vk = (ushort)('0' + d); scan = (ushort)(0x01 + d); ch = (ushort)('0' + d);
        }
        if (mode != "probe" && mode != "send") throw new ArgumentException("mode");
        if (expect.Length > 0 && NormalizeTty(expect) != expect) throw new ArgumentException("expect");
        return HelperTemplate
            .Replace("__MODE__", mode)
            .Replace("__PID__", pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__EXPECT__", expect)
            .Replace("__VK__", vk.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SCAN__", scan.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__CH__", ch.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private const string HelperTemplate = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        using System.Text;
        public static class JplusConsoleKey {
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
            [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CreateFileW(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] r, uint n, out uint written);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
            [StructLayout(LayoutKind.Explicit, Size = 20)]
            public struct INPUT_RECORD {
                [FieldOffset(0)] public ushort EventType;
                [FieldOffset(4)] public int bKeyDown;
                [FieldOffset(8)] public ushort wRepeatCount;
                [FieldOffset(10)] public ushort wVirtualKeyCode;
                [FieldOffset(12)] public ushort wVirtualScanCode;
                [FieldOffset(14)] public ushort UnicodeChar;
                [FieldOffset(16)] public uint dwControlKeyState;
            }
            static string Identity() {
                IntPtr w = GetConsoleWindow();
                if (w == IntPtr.Zero) return "console:0";
                StringBuilder sb = new StringBuilder(256);
                GetClassNameW(w, sb, 256);
                string kind = sb.ToString() == "PseudoConsoleWindow" ? "conpty" : "console";
                return kind + ":" + w.ToInt64().ToString("x");
            }
            public static string Run(string mode, uint pid, string expect, ushort vk, ushort scan, ushort ch) {
                FreeConsole();
                if (!AttachConsole(pid)) return "err:" + Marshal.GetLastWin32Error();
                try {
                    string id = Identity();
                    if (mode == "probe") return "tty:" + id;
                    if (id != expect) return "moved";
                    IntPtr h = CreateFileW("CONIN$", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
                    if (h == new IntPtr(-1)) return "err:" + Marshal.GetLastWin32Error();
                    try {
                        INPUT_RECORD[] recs = new INPUT_RECORD[2];
                        for (int i = 0; i < 2; i++) {
                            recs[i].EventType = 1;
                            recs[i].bKeyDown = i == 0 ? 1 : 0;
                            recs[i].wRepeatCount = 1;
                            recs[i].wVirtualKeyCode = vk;
                            recs[i].wVirtualScanCode = scan;
                            recs[i].UnicodeChar = ch;
                        }
                        uint written;
                        if (!WriteConsoleInputW(h, recs, 2u, out written)) return "err:" + Marshal.GetLastWin32Error();
                        return written == 2u ? "ok" : "short:" + written;
                    } finally { CloseHandle(h); }
                } finally { FreeConsole(); }
            }
        }
        '@
        $r = [JplusConsoleKey]::Run('__MODE__', __PID__, '__EXPECT__', __VK__, __SCAN__, __CH__)
        [Console]::Out.WriteLine($r)
        """;

    /// <summary>
    /// Press one key in the console that owns `pid`. Never raises.
    /// Returns `sent`, `no_tty`, `not_found`, `not_permitted`, `failed`, or —
    /// defensively — `bad_key`. Only `sent` means the key reached that console's
    /// input buffer.
    /// </summary>
    public static async Task<string> Answer(long pid, string? key) => (await AnswerExplained(pid, key)).outcome;

    /// <summary>
    /// Answer, plus a plain-English reason for any outcome other than `sent` (for
    /// logs and for a caller that wants to say WHY — the Windows-specific limits
    /// listed on this class).
    /// </summary>
    public static async Task<(string outcome, string? reason)> AnswerExplained(long pid, string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized is null)
        {
            // Before any script is composed: nothing about the rejected key ever
            // reaches the helper.
            Log.LogWarning("refusing a key outside the vocabulary: {Key}", key);
            return (BadKey, "that key is outside the closed vocabulary (Return, Escape, 1-9)");
        }
        if (!Py.IsWindows) return await LinuxAnswerExplained(pid, normalized);
        if (Py.IsServiceSession)
        {
            Log.LogWarning("keystroke refused: {Reason}", Py.NoDesktopReason);
            return (Failed, Py.NoDesktopReason);
        }
        try
        {
            var (tty, err) = await Probe(pid);
            if (tty is null)
            {
                if (IsPermissionError(err))
                    return (NotPermitted, "Windows refused to attach to that session's console (it is probably running elevated)");
                if (err is ErrorInvalidHandle or ErrorInvalidParameter)
                    return (NoTty, "that process has no console of its own");
                return (Failed, "the console lookup failed");
            }
            var tab = await FindTerminalTab(tty);
            if (tab is null)
            {
                // The console window is gone. Press nothing.
                return (NotFound, "its console window could not be found");
            }
            var (code, stdout, stderr) = await RunHelper(
                HelperScript("send", (uint)pid, tab.Tty, normalized), SendTimeout);
            if (code != 0)
            {
                Log.LogWarning("keystroke helper failed ({Code}): {Err}", code, stderr.Trim());
                return (Failed, "the keystroke helper failed");
            }
            var result = stdout.Trim();
            if (result == "ok") return (Sent, null);
            if (result == "moved")
            {
                // The console changed between the lookup and the press. Nothing
                // was pressed; from the user's side that is not_found.
                Log.LogWarning("console for {Tty} was {Result} at press time", tty, result);
                return (NotFound, "the console changed between the lookup and the press");
            }
            if (result.StartsWith("err:", StringComparison.Ordinal) && int.TryParse(result[4..], out var e2))
            {
                if (IsPermissionError(e2))
                {
                    Log.LogWarning("keystroke refused by Windows: {Result}", result);
                    return (NotPermitted, "Windows refused to attach to that session's console");
                }
                if (e2 is ErrorInvalidHandle or ErrorInvalidParameter)
                    return (NotFound, "the session's console went away before the press");
            }
            Log.LogWarning("unexpected keystroke helper result: {Result}", result);
            return (Failed, "the keystroke helper said something unexpected");
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "answering a dialog for pid {Pid} failed", pid);
            return (Failed, "answering the dialog failed");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
