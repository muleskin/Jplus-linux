using System.Collections;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// JARVIS Notifier — Windows toast / Linux desktop-notification fallback (the
/// macOS original posted a Notification Center banner via osascript).
///
/// When JARVIS needs the user's attention but no browser tab is connected to
/// speak through, this posts a native toast instead.
///
/// Notification titles/messages surface a Claude Code session's title or its
/// last message — text that originates in someone else's transcript, not text
/// JARVIS wrote itself. So the PowerShell script never contains the untrusted
/// text at all: it is one fixed script, and the title/message/subtitle reach
/// it as environment variables, which PowerShell hands over as plain data —
/// never parsed as source. Inside, they become XML *text nodes* via
/// CreateTextNode, so they cannot be read as toast markup either. There is no
/// escaping step to get wrong because there is nothing to escape.
/// </summary>
public static partial class Notifier
{
    public static readonly Microsoft.Extensions.Logging.ILogger Log = Py.Log("jarvis.notifier");

    // A notification is a glance, not an essay — keep it short. These bound
    // what we pass to the toast regardless of how long the source text is.
    public const int TitleMax = 120;
    public const int SubtitleMax = 120;
    public const int MessageMax = 300;

    // A wedged process must not hang the caller. (osascript took 5s; a cold
    // Windows PowerShell start alone can take a few seconds, hence 10.)
    public const double TimeoutSeconds = 10.0;

    // The AppUserModelID toasts are posted under. Windows only shows toasts
    // from a registered app id; Windows PowerShell's own is always registered.
    public const string ToastAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    // Fixed script, no untrusted text ever enters this string. Title, message,
    // and subtitle arrive purely via the environment (see the class summary).
    public const string NotifyScript = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
        $theTitle = [string]$env:JARVIS_TOAST_TITLE
        $theMessage = [string]$env:JARVIS_TOAST_MESSAGE
        $theSubtitle = [string]$env:JARVIS_TOAST_SUBTITLE
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        if ($theSubtitle -eq '') {
            $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text/><text/></binding></visual></toast>')
            $lines = @($theTitle, $theMessage)
        } else {
            $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text/><text/><text/></binding></visual></toast>')
            $lines = @($theTitle, $theSubtitle, $theMessage)
        }
        $nodes = $xml.GetElementsByTagName('text')
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $nodes.Item($i).AppendChild($xml.CreateTextNode($lines[$i])) > $null
        }
        $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:JARVIS_TOAST_APPID).Show($toast)
        """;

    /// <summary>Bound text length for a glanceable notification, marking any cut.</summary>
    public static string Truncate(string text, int limit)
    {
        if (JarvisMemory.CpLen(text) <= limit) return text;
        return JarvisMemory.CpPrefix(text, Math.Max(0, limit - 1)).TrimEnd() + "…";
    }

    /// <summary>Windows PowerShell 5.1 — pwsh 7 has no WinRT projection, so it must be this one.</summary>
    public static string? PowerShellPath()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(sys) ? sys : Py.Which("powershell");
    }

    /// <summary>
    /// Whether posting a notification is plausible right now. Cheap and
    /// side-effect free: Windows, an interactive session (a service in session
    /// 0 has no desktop to toast on), and powershell.exe present. A
    /// precondition check, not a delivery guarantee — Focus Assist or
    /// per-app settings can still drop it silently.
    /// </summary>
    public static bool Available() =>
        !Py.IsServiceSession && (Py.IsWindows ? PowerShellPath() is not null : Py.Which("notify-send") is not null);

    /// <summary>
    /// Linux: a freedesktop notification via `notify-send`. The untrusted text
    /// travels as its own argv entries after `--` (no shell, never read as an
    /// option); the body is markup-escaped because notification servers that
    /// advertise body-markup render a subset of HTML in it.
    /// </summary>
    private static async Task<bool> NotifyLinux(string title, string message, string subtitle)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var body = subtitle.Length > 0 ? $"{Esc(subtitle)}\n{Esc(message)}" : Esc(message);
        Py.ProcResult result;
        try
        {
            result = await Py.Run(Py.Which("notify-send")!,
                ["--app-name=JARVIS", "--", title.Length > 0 ? title : "JARVIS", body],
                timeoutSeconds: TimeoutSeconds);
        }
        catch (FileNotFoundException e)
        {
            Log.LogWarning("notifier: failed to spawn notify-send: {Error}", e.Message);
            return false;
        }
        if (result.TimedOut)
        {
            Log.LogWarning("notifier: notify-send timed out, killing it");
            return false;
        }
        if (result.ReturnCode != 0)
        {
            Log.LogWarning("notifier: notify-send exited {Code}: {Stderr}", result.ReturnCode, result.Stderr.Trim());
            return false;
        }
        return true;
    }

    /// <summary>
    /// Post a Windows toast. Returns whether it was handed off successfully.
    ///
    /// A fallback path for when nobody is listening on the voice channel, so
    /// it must never throw: any failure is logged as a warning and reported
    /// back as false. Runs as a bounded subprocess so it never blocks the
    /// caller for long.
    /// </summary>
    public static async Task<bool> Notify(string? title, string? message, string subtitle = "")
    {
        try
        {
            if (!Available())
            {
                if (Py.IsServiceSession)
                    Log.LogWarning("notifier: notifications unavailable: {Reason}", Py.NoDesktopReason);
                else
                    Log.LogWarning("notifier: notifications unavailable on this platform");
                return false;
            }

            var safeTitle = Truncate(title ?? "", TitleMax);
            var safeMessage = Truncate(message ?? "", MessageMax);
            var safeSubtitle = Truncate(subtitle ?? "", SubtitleMax);
            if (!Py.IsWindows) return await NotifyLinux(safeTitle, safeMessage, safeSubtitle);

            // Py.Run replaces the environment wholesale, so start from ours.
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
                env[(string)kv.Key] = kv.Value as string;
            env["JARVIS_TOAST_TITLE"] = safeTitle;
            env["JARVIS_TOAST_MESSAGE"] = safeMessage;
            env["JARVIS_TOAST_SUBTITLE"] = safeSubtitle;
            env["JARVIS_TOAST_APPID"] = ToastAppId;

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(NotifyScript));
            Py.ProcResult result;
            try
            {
                result = await Py.Run(PowerShellPath()!,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
                    timeoutSeconds: TimeoutSeconds, env: env);
            }
            catch (FileNotFoundException e)
            {
                Log.LogWarning("notifier: failed to spawn powershell: {Error}", e.Message);
                return false;
            }

            if (result.TimedOut)
            {
                Log.LogWarning("notifier: powershell timed out, killing it");
                return false;
            }
            if (result.ReturnCode != 0)
            {
                Log.LogWarning("notifier: powershell exited {Code}: {Stderr}", result.ReturnCode, result.Stderr.Trim());
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            // Belt and suspenders: this path must never throw into the caller.
            Log.LogWarning("notifier: unexpected error posting notification: {Error}", e.Message);
            return false;
        }
    }
}
