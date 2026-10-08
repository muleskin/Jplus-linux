using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Jplus;

/// <summary>
/// Post a message into a running Claude Code session (port of session_steer.py).
///
/// The wire format is one JSON line on the session's inbox socket. It carries
/// PEER authority, not the user's: the session receives it as a new turn (or
/// queues it between tool calls if busy), but it cannot dismiss a permission
/// prompt or a modal dialog. Those are the `waitingFor` reasons that actually
/// occur, so the caller must check before promising a fix.
///
/// Windows: Claude Code publishes `messagingSocketPath` as a named pipe
/// (`\\.\pipe\LOCAL\cc-msg-…`, measured on this machine), so such a path is
/// opened as a NamedPipeClientStream; anything else is treated as an AF_UNIX
/// socket file (UnixDomainSocketEndPoint, Windows 10+). Same bytes either way.
/// </summary>
public static partial class SessionSteer
{
    public const string Sent = "sent";          // the bytes left over the socket — NOT that the target
                                                // session accepted or even received them; no reply is
                                                // ever read back to confirm that
    public const string NotLive = "not_live";
    public const string Refused = "refused";
    public const string Failed = "failed";

    private const string PipePrefix = @"\\.\pipe\";

    /// <summary>
    /// Deliver one prompt. Returns `sent`, `not_live`, `refused`, or `failed`.
    /// A missing socket and a stale one left by a dead process are both
    /// `not_live`: from the user's point of view there is nothing to talk to.
    /// Synchronous, like the Python; callers run it via Task.Run.
    /// </summary>
    public static string PostToSession(string? socketPath, string? prompt, double timeout = 5.0)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return Refused;
        if (string.IsNullOrEmpty(socketPath) || !SessionWatch.SocketExists(socketPath)) return NotLive;

        var lines = new List<string>();
        // What is known about this token, recorded so nobody "fixes" it into
        // something it cannot be: it is optional (the target may require none);
        // we can only ever send OUR OWN; tokens observably differ between
        // sessions. If the target validates a token and ours does not match, the
        // send can fail silently from our side — a successful write proves the
        // bytes left this process, not that the target accepted them.
        var token = Py.Getenv("CLAUDE_CODE_MESSAGING_TOKEN", "") ?? "";
        if (token.Length > 0)
            lines.Add(Py.Dumps(new Dictionary<string, object?> { ["type"] = "auth", ["token"] = token }));
        lines.Add(Py.Dumps(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = prompt.Trim() },
        }));
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        var ms = (int)Math.Max(1, timeout * 1000);

        return socketPath.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
            ? SendPipe(socketPath, payload, ms)
            : SendUnix(socketPath, payload, ms);
    }

    private static string SendPipe(string socketPath, byte[] payload, int timeoutMs)
    {
        var name = socketPath[PipePrefix.Length..];
        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);
        }
        catch (TimeoutException)
        {
            // A pipe that vanished while we waited is a session that has gone.
            return SessionWatch.SocketExists(socketPath) ? Failed : NotLive;
        }
        catch (FileNotFoundException) { return NotLive; }
        catch (Exception) { return Failed; }
        using (pipe)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                pipe.WriteAsync(payload, cts.Token).AsTask().GetAwaiter().GetResult();
                pipe.Flush();
            }
            catch (Exception) { return Failed; }
        }
        return Sent;
    }

    private static string SendUnix(string socketPath, byte[] payload, int timeoutMs)
    {
        Socket sock;
        try { sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); }
        catch (Exception) { return Failed; }
        using (sock)
        {
            sock.SendTimeout = timeoutMs;
            sock.ReceiveTimeout = timeoutMs;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused
                                               or SocketError.AddressNotAvailable)
            {
                return NotLive;           // a stale socket file from a process that has gone
            }
            catch (Exception)
            {
                return File.Exists(socketPath) ? Failed : NotLive;
            }
            try
            {
                int sent = 0;
                while (sent < payload.Length)
                    sent += sock.Send(payload, sent, payload.Length - sent, SocketFlags.None);
            }
            catch (Exception) { return Failed; }
            finally
            {
                try { sock.Shutdown(SocketShutdown.Both); } catch { }
            }
        }
        return Sent;
    }
}
