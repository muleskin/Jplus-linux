using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Spawns Claude Code runs and records everything they do (run_executor.py).
///
/// NOTE ON SHAPE: the Python module and its class share the name
/// `RunExecutor`, which C# cannot nest (a member may not share its enclosing
/// type's name). So this one module is a NON-static class: the module-level
/// constants and functions are its static members (`RunExecutor.MaxBoundSec`,
/// `RunExecutor.WallClock(...)`), and `new RunExecutor(...)` is the Python
/// `RunExecutor(run_store, ...)`. The `store` argument is optional; it
/// defaults to the RunStore module.
///
/// Invariant: every state transition is written to the store *before* it is
/// published to subscribers. The WebSocket is a cache-invalidation hint, never
/// a source of truth.
///
/// Second invariant: a run ALWAYS reaches a terminal state. Every exit path out
/// of `Drive` — success, non-zero exit, timeout, spawn failure, cancellation,
/// or an unexpected exception anywhere in the pipeline — writes a terminal
/// status. A run stuck in `running` or `queued` is the failure this module
/// exists to eliminate.
///
/// THREADING: unlike asyncio, the continuations here run on thread-pool
/// threads, so subscribers are called from arbitrary threads and must be
/// thread-safe (the Python ran them on the event loop).
/// </summary>
public sealed partial class RunExecutor
{
    public static readonly ILogger Log = Py.Log("jarvis.run_executor");

    // Same default and env var as work_mode and server. Read live rather than
    // once at type-init so a value from `.env` is always seen.
    public static bool SkipPermissions =>
        (Py.Getenv("JARVIS_SKIP_PERMISSIONS", "true") ?? "true").ToLowerInvariant() is not ("0" or "false" or "no");

    // Same precedent as BrainConfig.FromEnv: never rely on the CLI's own
    // default, always pass --model explicitly.
    public const string DefaultModel = "sonnet";

    // Events are written in batches so a chatty build does not put thousands of
    // synchronous SQLite writes in the read loop.
    public const int EventBatch = 25;
    public const double EventFlushSec = 0.5;

    // stderr is drained concurrently with stdout — a child that fills the stderr
    // pipe buffer blocks forever otherwise — keeping only the tail.
    public const int StderrChars = 2000;
    public const int StderrBytes = StderrChars * 4;
    public const double StderrDrainSec = 5.0;

    // How long the child may take to EXIT after it has closed stdout. EOF means
    // the CLI has nothing left to say; all that remains is teardown (flushing its
    // transcript, reaping its MCP children). Thirty seconds is far more than that
    // needs and still frees a parked child's permit within half a minute. This
    // only applies to a child that reaches EOF, which is rarer than it sounds —
    // see IdleOutputSec.
    public const double EofExitGraceSec = 30.0;

    // How long the child may say NOTHING while still holding stdout open.
    //
    // Almost no way a run goes quiet produces EOF: a child that stops writing
    // with stdout still open, one that exits while a grandchild (an MCP server)
    // holds the pipe, one that is suspended — none of them reach EOF, so the
    // grace above never fires and only the six-hour wall clock is left while the
    // run holds one of three permits.
    //
    // Thirty minutes is chosen against the longest silence a *working* run can
    // legitimately produce: one Bash tool call (the CLI allows up to ten
    // minutes), one thinking turn, or the CLI's own 429/529 backoff. That gives
    // roughly threefold headroom while still being a twelfth of the wall clock.
    // Override with JARVIS_RUN_IDLE_SEC; there is deliberately no "no bound".
    public const double IdleOutputSec = 30 * 60;

    // How often the read loop wakes up to ask "has anything happened". Only
    // reached when the child is silent — a ready line is returned at once — so
    // this is the resolution of the idle bound and of the exit check.
    public const double ReadPollSec = 1.0;

    // The wall-clock bound every run gets when its caller names none. Longer
    // than any real start_build, shorter than a working day, so a wedged run is
    // reclaimed the same day. Override with JARVIS_RUN_TIMEOUT_SEC; a caller's
    // own timeout_sec still wins. There is deliberately no way to ask for none.
    public const double DefaultTimeoutSec = 6 * 3600;

    // The ceiling on every bound, whoever asks and however they ask. `> 0` is
    // not "is a wall clock": infinity and 1e30 pass it, and
    // JARVIS_RUN_TIMEOUT_SEC=inf once removed the bound entirely. A day is
    // longer than any real build and short enough to reclaim a wedged run.
    public const double MaxBoundSec = 24 * 3600;

    /// <summary>
    /// `value` as a bound that a run can actually hit, or null if it is not one —
    /// zero, negative, NaN, or infinite. Capped rather than refused at the top:
    /// an operator who asked for a week meant "a long time", and a day is long.
    /// </summary>
    public static double? WallClock(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return null;
        return Math.Min(value, MaxBoundSec);
    }

    /// <summary>`WallClock` for a raw string (an env var), as Python's `float(raw)`.</summary>
    public static double? WallClock(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim().ToLowerInvariant();
        double value;
        if (s is "inf" or "+inf" or "infinity" or "+infinity" or "-inf" or "-infinity" or "nan") return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return null;
        return WallClock(value);
    }

    /// <summary>
    /// Explicit argument, else the environment, else the module default — and
    /// never anything that is not a wall clock. One function so a third bound
    /// added later cannot quietly get the old `> 0` test.
    /// </summary>
    public static double ResolveBound(double? explicitValue, string envVar, double @default, string what)
    {
        var bounded = explicitValue is double e ? WallClock(e) : null;
        if (bounded is not null) return bounded.Value;
        var raw = Py.Getenv(envVar);
        if (!string.IsNullOrEmpty(raw))
        {
            bounded = WallClock(raw);
            if (bounded is not null) return bounded.Value;
            Log.LogWarning("{EnvVar}='{Raw}' is not a length of time {What} can be bounded by; using {Default}s",
                envVar, raw, what, @default);
        }
        return @default;
    }

    /// <summary>Explicit argument, else JARVIS_RUN_TIMEOUT_SEC, else six hours.</summary>
    public static double ResolveDefaultTimeout(double? explicitValue) =>
        ResolveBound(explicitValue, "JARVIS_RUN_TIMEOUT_SEC", DefaultTimeoutSec, "a run");

    /// <summary>Explicit argument, else JARVIS_RUN_IDLE_SEC, else thirty minutes.</summary>
    public static double ResolveIdle(double? explicitValue) =>
        ResolveBound(explicitValue, "JARVIS_RUN_IDLE_SEC", IdleOutputSec, "a silent run");

    /// <summary>
    /// The child held stdout open and said nothing for too long. Thrown out of
    /// `Consume` rather than returned, so it cannot be mistaken for EOF.
    /// </summary>
    public class IdleTimeout(string message) : Exception(message);

    /// <summary>A single stdout line exceeded ClaudeEnv.StreamLineLimit; its bytes were discarded.</summary>
    public sealed class LineTooLongException(long length)
        : Exception($"Separator is not found, and chunk exceed the limit ({length} bytes)");

    // ── the store seam (the Python passed the run_store module) ─────────────

    public interface IStore
    {
        string CreateRun(string prompt, string projectName, string projectPath, string origin, string? resumeFrom);
        Dictionary<string, object?>? GetRun(string runId);
        void UpdateRun(string runId, IEnumerable<KeyValuePair<string, object?>> fields);
        int NextSeq(string runId);
        void AppendEvents(string runId, IEnumerable<(int Seq, string Kind, string Payload)> rows);
    }

    /// <summary>The RunStore module as an IStore.</summary>
    public sealed class ModuleStore : IStore
    {
        public static readonly ModuleStore Instance = new();
        public string CreateRun(string prompt, string projectName, string projectPath, string origin, string? resumeFrom) =>
            RunStore.CreateRun(prompt, projectName, projectPath, origin, resumeFrom);
        public Dictionary<string, object?>? GetRun(string runId) => RunStore.GetRun(runId);
        public void UpdateRun(string runId, IEnumerable<KeyValuePair<string, object?>> fields) =>
            RunStore.UpdateRun(runId, fields);
        public int NextSeq(string runId) => RunStore.NextSeq(runId);
        public void AppendEvents(string runId, IEnumerable<(int Seq, string Kind, string Payload)> rows) =>
            RunStore.AppendEvents(runId, rows);
    }

    private sealed record Driver(Task Task, CancellationTokenSource Cts);

    // ── instance state ──────────────────────────────────────────────────────

    private readonly IStore _store;
    private readonly Lazy<string> _claudePath;
    private readonly int _maxConcurrent;
    private readonly double _graceSec;
    private readonly double _eofGraceSec;
    private readonly Lazy<double> _idleSec;
    private readonly double _pollSec;
    private readonly Lazy<double> _defaultTimeout;
    private readonly ConcurrentDictionary<string, Process> _procs = new();
    private readonly ConcurrentDictionary<string, Driver> _tasks = new();
    private readonly List<Action<Dictionary<string, object?>>> _subscribers = new();
    private readonly object _subscribersGate = new();
    // Concurrency gate. A counting semaphore, not a poll over ActiveCount(): a
    // burst of tasks reaching the gate together each counted the others as
    // "active" and slept forever.
    private readonly SemaphoreSlim _slots;
    // Run ids whose cancellation has been *requested*. Recorded before the
    // signal is sent, so whichever path reaches the terminal write first still
    // records CANCELLED and not "failed, exit -1".
    private readonly ConcurrentDictionary<string, byte> _cancelling = new();
    // Makes "first terminal writer wins" atomic across threads.
    private readonly object _finishGate = new();

    /// <param name="store">null → the RunStore module.</param>
    public RunExecutor(IStore? store = null, string? claudePath = null,
                       int maxConcurrent = 3, double graceSec = 10.0,
                       double eofGraceSec = EofExitGraceSec,
                       double? defaultTimeoutSec = null,
                       double? idleSec = null,
                       double pollSec = ReadPollSec)
    {
        _store = store ?? ModuleStore.Instance;
        // Resolved lazily: this instance may be built during static
        // initialisation, before `.env` has been loaded into the environment.
        _claudePath = new Lazy<string>(() =>
            !string.IsNullOrEmpty(claudePath) ? claudePath : Py.Which("claude") ?? "claude");
        _maxConcurrent = maxConcurrent;
        _graceSec = graceSec;
        _eofGraceSec = eofGraceSec;
        _idleSec = new Lazy<double>(() => ResolveIdle(idleSec));
        _pollSec = pollSec > 0 ? pollSec : ReadPollSec;
        _defaultTimeout = new Lazy<double>(() => ResolveDefaultTimeout(defaultTimeoutSec));
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public string ClaudePath => _claudePath.Value;
    public int MaxConcurrent => _maxConcurrent;
    public double IdleSec => _idleSec.Value;
    public double DefaultTimeout => _defaultTimeout.Value;

    // -- pub/sub ----------------------------------------------------------

    public void Subscribe(Action<Dictionary<string, object?>> cb)
    {
        lock (_subscribersGate)
            if (!_subscribers.Contains(cb)) _subscribers.Add(cb);
    }

    public void Unsubscribe(Action<Dictionary<string, object?>> cb)
    {
        lock (_subscribersGate)
            _subscribers.Remove(cb);
    }

    /// <summary>Never let a bad subscriber take down a run.</summary>
    public void Publish(Dictionary<string, object?> message)
    {
        Action<Dictionary<string, object?>>[] subs;
        lock (_subscribersGate) subs = _subscribers.ToArray();
        foreach (var cb in subs)
        {
            try { cb(message); }
            catch (Exception e) { Log.LogWarning(e, "run subscriber raised; continuing"); }
        }
    }

    // -- lifecycle --------------------------------------------------------

    /// <summary>
    /// How many driver tasks are still in flight. Informational only —
    /// concurrency is gated by the semaphore. This counts queued tasks too,
    /// which is exactly why it must not gate.
    /// </summary>
    public int ActiveCount() => _tasks.Values.Count(d => !d.Task.IsCompleted);

    /// <summary>
    /// Explicit argument, else JARVIS_RUN_MODEL, else 'sonnet'. Never empty:
    /// the CLI's own default must never be relied on.
    /// </summary>
    public string ResolveModel(string? model)
    {
        if (!string.IsNullOrEmpty(model)) return model;
        var env = Py.Getenv("JARVIS_RUN_MODEL");
        return !string.IsNullOrEmpty(env) ? env : DefaultModel;
    }

    /// <summary>Drive a run row that was created by the caller.</summary>
    public async Task<string> StartExisting(string runId, string prompt, string projectPath,
                                            string? resumeFrom = null, double timeoutSec = 0,
                                            string? model = null)
    {
        // Everything between the row existing and its driver owning it is
        // guarded: a store write raising here used to leave the row QUEUED with
        // no driver behind it and no path to a terminal state.
        try
        {
            var resolvedModel = ResolveModel(model);
            // A caller that names no bound (no production caller does) gets the
            // default one. See ResolveDefaultTimeout.
            var bound = timeoutSec > 0 ? timeoutSec : _defaultTimeout.Value;
            // Persisted immediately — before the process even spawns — so a
            // still-queued run already shows what it will run on.
            await Task.Run(() => _store.UpdateRun(runId,
                [new KeyValuePair<string, object?>("requested_model", resolvedModel)]));
            var cts = new CancellationTokenSource();
            // Not Task.Run(..., cts.Token): a token cancelled before start would
            // skip Drive entirely. Drive always runs; the token stops it.
            var task = Task.Run(() => Drive(runId, prompt, projectPath, resumeFrom, bound, resolvedModel, cts.Token));
            var driver = new Driver(task, cts);
            _tasks[runId] = driver;
            // Drop the reference once finished: the dict would otherwise grow for
            // the life of the server. WaitFor() falls back to the store.
            _ = task.ContinueWith(_ =>
            {
                _tasks.TryRemove(new KeyValuePair<string, Driver>(runId, driver));
                cts.Dispose();
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            FailUndriven(runId, e);
            throw;
        }
        return runId;
    }

    /// <summary>
    /// Mark a run terminal when nothing will ever drive it. Best effort: the
    /// store is usually the very thing that just failed, and this must never
    /// throw over the original exception.
    /// </summary>
    public void FailUndriven(string runId, Exception exc)
    {
        Log.LogError(exc, "run {RunId} could not be handed to a driver", runId);
        try
        {
            FinishBlocking(runId, RunStore.RunStatus.Failed, ("error", Cut(Repr(exc), StderrChars)));
        }
        catch (Exception e)
        {
            Log.LogError(e, "run {RunId} could not be marked terminal either", runId);
        }
    }

    public async Task<string> Spawn(string prompt, string projectName, string projectPath,
                                    string origin, string? resumeFrom = null,
                                    double timeoutSec = 0, string? model = null)
    {
        var runId = await Task.Run(() => _store.CreateRun(prompt, projectName, projectPath, origin, resumeFrom));
        return await StartExisting(runId, prompt, projectPath, resumeFrom, timeoutSec, model);
    }

    /// <summary>
    /// Cancel a run, queued or running. False only for an unknown run id or one
    /// already in a terminal state.
    ///
    /// True is a promise, not a status write: the child was either killed and
    /// confirmed dead, or confirmed never to have been started. The one
    /// exception is a child not visibly reaped within Terminate's second grace
    /// period — logged at ERROR and reported as cancelled anyway, because
    /// waiting forever is the worse of the two.
    /// </summary>
    public async Task<bool> Cancel(string runId)
    {
        var run = await Task.Run(() => _store.GetRun(runId));
        if (run is null || RunStore.RunStatus.Terminal.Contains(run["status"] as string ?? ""))
            return false;

        // Record the intent BEFORE signalling: Drive can reach its terminal
        // write before this method does; it consults this set, so the first
        // writer still writes CANCELLED.
        _cancelling[runId] = 0;

        if (_procs.TryGetValue(runId, out var proc))
        {
            if (ReturnCode(proc) is null)
                await Terminate(proc);
            // Either way the child is now reaped (or demonstrably unreapable).
            await Finish(runId, RunStore.RunStatus.Cancelled, ("exit_code", ReturnCode(proc)));
            return true;
        }

        // No process at all: still queued behind the concurrency gate, or the
        // driver has already reaped and unregistered it. Mark it terminal now
        // and make sure a pending task never spawns anything.
        try
        {
            await Finish(runId, RunStore.RunStatus.Cancelled);
            if (_tasks.TryGetValue(runId, out var driver) && !driver.Task.IsCompleted
                && (run["status"] as string) == RunStore.RunStatus.Queued)
            {
                try { driver.Cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            if (!_tasks.ContainsKey(runId))
                // No driver left to consult the intent.
                _cancelling.TryRemove(runId, out _);
        }
        return true;
    }

    /// <summary>
    /// Kill the child's whole process tree, wait up to the grace period, kill
    /// again and wait once more — then give up the wait. Windows has no SIGTERM
    /// for a console child, so both steps are TerminateProcess on the tree (the
    /// tree matters: an npm `claude.cmd` is cmd.exe → node, and the CLI has MCP
    /// children of its own).
    ///
    /// The second bound is a real trade: an unbounded wait could park Cancel()
    /// and Drive forever and the permit would never come back. Bounded, the
    /// caller may in principle be told a child is gone that is not; that is
    /// logged at ERROR, and it is strictly better than wedging the pipeline.
    /// </summary>
    public async Task Terminate(Process proc)
    {
        if (ReturnCode(proc) is not null) return;
        // Linux: SIGTERM first, as the Python did, so the CLI can take its MCP
        // children down itself; the tree kill is the second step.
        if (!Py.IsWindows) { try { Posix.Kill(proc.Id, Posix.SigTerm); } catch { KillTree(proc); } }
        else KillTree(proc);
        if (await WaitExit(proc, _graceSec)) return;
        KillTree(proc);
        if (!await WaitExit(proc, _graceSec))
            Log.LogError("run child {Pid} was not reaped within {Grace}s of the kill; " +
                         "giving up the wait rather than holding its permit", SafePid(proc), _graceSec);
    }

    public async Task<Dictionary<string, object?>?> WaitFor(string runId, double timeout = 30)
    {
        if (_tasks.TryGetValue(runId, out var driver))
        {
            // WaitAsync never cancels the run itself — a caller's timeout only
            // stops the caller waiting (TimeoutException, like asyncio's).
            try
            {
                await driver.Task.WaitAsync(TimeSpan.FromSeconds(timeout));
            }
            catch (OperationCanceledException)
            {
                // The *run* was cancelled (queued-cancel path) — report its
                // recorded terminal state.
                if (!driver.Task.IsCanceled) throw;
            }
        }
        return await Task.Run(() => _store.GetRun(runId));
    }

    // -- internals --------------------------------------------------------

    public List<string> Command(string runId, string? resumeFrom, string? model = null)
    {
        var cmd = ClaudeEnv.SplitCommand(_claudePath.Value);
        cmd.AddRange(["-p", "--output-format", "stream-json", "--verbose", "--session-id", runId]);
        if (!string.IsNullOrEmpty(resumeFrom))
            // --session-id is rejected alongside --resume unless --fork-session
            // is also passed. Forking is also the semantics we want — the retry
            // inherits context but owns its own id.
            cmd.AddRange(["--resume", resumeFrom, "--fork-session"]);
        // Always explicit, never the CLI's own default.
        cmd.AddRange(["--model", ResolveModel(model)]);
        if (SkipPermissions)
            // Without this a run blocks on a permission prompt it has no TTY to
            // answer, and hangs forever.
            cmd.Add("--dangerously-skip-permissions");
        return cmd;
    }

    /// <summary>
    /// Announce a field change on a still-running run (model from system/init,
    /// accumulating token usage, cost from result) — not per streamed event,
    /// which `run_event` already covers. Carries the full row so the client
    /// never has to merge a delta.
    /// </summary>
    public async Task PublishRunUpdated(string runId)
    {
        var run = await Task.Run(() => _store.GetRun(runId));
        if (run is not null)
            Publish(new Dictionary<string, object?> { ["type"] = "run_updated", ["run"] = run });
    }

    /// <summary>The RUNNING transition, as one unit.</summary>
    public Dictionary<string, object?>? StartWrite(string runId, int pid)
    {
        _store.UpdateRun(runId, Fields(("status", RunStore.RunStatus.Running), ("pid", pid),
                                       ("started_at", Py.Now())));
        return _store.GetRun(runId);
    }

    /// <summary>
    /// The store calls behind a terminal transition, as one unit. Returns the
    /// finished row, or null when the run was already terminal and nothing was
    /// written — the first writer wins, which keeps a cancel racing Drive to a
    /// single status.
    /// </summary>
    public Dictionary<string, object?>? FinishWrite(string runId, string status,
                                                    params (string Key, object? Value)[] fields)
    {
        lock (_finishGate)
        {
            var run = _store.GetRun(runId);
            if (run is not null && RunStore.RunStatus.Terminal.Contains(run["status"] as string ?? ""))
                return null;
            var all = new List<(string, object?)> { ("status", status), ("ended_at", Py.Now()) };
            all.AddRange(fields);
            _store.UpdateRun(runId, Fields(all.ToArray()));
            return _store.GetRun(runId);
        }
    }

    /// <summary>Terminal transition: written off the caller's thread, then published.</summary>
    public async Task Finish(string runId, string status, params (string Key, object? Value)[] fields)
    {
        var row = await Task.Run(() => FinishWrite(runId, status, fields));
        if (row is not null)
            Publish(new Dictionary<string, object?> { ["type"] = "run_finished", ["run"] = row });
    }

    /// <summary>
    /// `Finish` for an error path that must not depend on an await completing
    /// (the driver may already be cancelled). Blocking one write on a failure
    /// path is the lesser evil.
    /// </summary>
    public void FinishBlocking(string runId, string status, params (string Key, object? Value)[] fields)
    {
        var row = FinishWrite(runId, status, fields);
        if (row is not null)
            Publish(new Dictionary<string, object?> { ["type"] = "run_finished", ["run"] = row });
    }

    /// <summary>
    /// Keep the stderr pipe empty so the child never blocks writing to it. Only
    /// the tail is kept: a run that dumps megabytes to stderr must not grow
    /// this buffer without bound.
    /// </summary>
    public static async Task DrainStderr(Stream stream, ByteTail sink)
    {
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
                if (n == 0) return;
                sink.Append(buf, n);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went away under us (process disposed): nothing more to read.
        }
    }

    /// <summary>Await the drain task (bounded) and decode whatever it captured.</summary>
    public static async Task<string> CollectStderr(Task? task, ByteTail sink)
    {
        if (task is not null && !task.IsCompleted)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(StderrDrainSec));
            }
            catch (TimeoutException)
            {
                // Something inherited the pipe and is holding it open; take what
                // we have rather than hanging the run.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogDebug(e, "stderr drain failed");
            }
        }
        var text = Encoding.UTF8.GetString(sink.ToArray());
        return text.Length > StderrChars ? text[^StderrChars..] : text;
    }

    public async Task Drive(string runId, string prompt, string projectPath, string? resumeFrom,
                            double timeoutSec, string? model, CancellationToken ct = default)
    {
        Process? proc = null;
        bool slotHeld = false;
        Task? stderrTask = null;
        var stderrSink = new ByteTail(StderrBytes);
        try
        {
            await _slots.WaitAsync(ct);
            slotHeld = true;

            if (_cancelling.ContainsKey(runId))
            {
                // Cancelled while queued: never spawn anything.
                await Finish(runId, RunStore.RunStatus.Cancelled);
                return;
            }

            try
            {
                proc = StartProcess(Command(runId, resumeFrom, model), projectPath);
            }
            catch (Exception e) when (e is Win32Exception or FileNotFoundException
                                          or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                await Finish(runId, RunStore.RunStatus.Failed, ("error", $"could not start claude: {e.Message}"));
                return;
            }

            _procs[runId] = proc;
            // Drain stderr from the first byte: nothing else reads it until the
            // process has exited, and a chatty run fills the pipe buffer and
            // deadlocks long before that.
            var errStream = proc.StandardError.BaseStream;
            stderrTask = Task.Run(() => DrainStderr(errStream, stderrSink));

            var started = await Task.Run(() => StartWrite(runId, proc.Id));
            Publish(new Dictionary<string, object?> { ["type"] = "run_started", ["run"] = started });

            try
            {
                await proc.StandardInput.WriteAsync(prompt);
                await proc.StandardInput.FlushAsync();
                proc.StandardInput.Close();
            }
            catch (IOException) { }   // the child already went away: broken pipe
            catch (ObjectDisposedException) { }

            var reader = new LineReader(proc.StandardOutput.BaseStream, ClaudeEnv.StreamLineLimit);
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Clamp to what CancelAfter accepts; WallClock already bounds the
                // default to a day, an explicit caller value is used as given.
                if (double.IsFinite(timeoutSec) && timeoutSec * 1000 < int.MaxValue - 1)
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    await Consume(runId, proc, reader, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Terminate(proc);
                    // Keep the stderr: a run that had to be killed is the one
                    // most in need of the context.
                    var stderrT = await CollectStderr(stderrTask, stderrSink);
                    var errorT = $"exceeded timeout of {PyFloat(timeoutSec)}s";
                    if (stderrT.Length > 0) errorT = $"{errorT}\n{stderrT}";
                    await Finish(runId, RunStore.RunStatus.TimedOut, ("error", errorT));
                    return;
                }
                catch (IdleTimeout e)
                {
                    // Alive, holding stdout, and silent past the idle bound. EOF is
                    // never coming, so the EOF grace below would never have run.
                    Log.LogWarning("run {RunId}: child {Pid} {Why}; killing it", runId, SafePid(proc), e.Message);
                    await Terminate(proc);
                    var stderrI = await CollectStderr(stderrTask, stderrSink);
                    // TIMED_OUT rather than FAILED: it used up a time budget. The
                    // budget is named in the error so the two are never confused.
                    var errorI = $"claude produced no output for {PyFloat(_idleSec.Value)}s " +
                                 "and was still running; killed";
                    if (stderrI.Length > 0) errorI = $"{errorI}\n{stderrI}";
                    await Finish(runId, RunStore.RunStatus.TimedOut, ("error", errorI));
                    return;
                }
            }
            // NOT removed from _procs here: Cancel() signals through it, so the
            // process must stay until it is actually gone. The finally removes it.

            // EOF on stdout is not the same as the process being over. A child
            // that closes stdout and keeps running used to park this on an
            // unbounded wait: the run stayed `running` and its permit was gone.
            bool eofHang = false;
            if (!await WaitExit(proc, _eofGraceSec, ct))
            {
                eofHang = true;
                Log.LogWarning("run {RunId}: child {Pid} closed stdout but did not exit within {Grace}s; killing it",
                    runId, SafePid(proc), _eofGraceSec);
                await Terminate(proc);
            }
            var stderr = await CollectStderr(stderrTask, stderrSink);

            var run = await Task.Run(() => _store.GetRun(runId));
            if (run is not null && RunStore.RunStatus.Terminal.Contains(run["status"] as string ?? ""))
                return;

            var rc = ReturnCode(proc);
            if (_cancelling.ContainsKey(runId))
            {
                // We won the race with Cancel(). The exit code is whatever the
                // kill produced; the *intent* was cancellation.
                await Finish(runId, RunStore.RunStatus.Cancelled, ("exit_code", rc));
            }
            else if (eofHang)
            {
                // The process misbehaved and we killed it, so its exit status says
                // nothing. FAILED rather than TIMED_OUT: it refused to exit.
                var error = $"claude closed stdout but did not exit within {PyFloat(_eofGraceSec)}s; killed";
                if (stderr.Length > 0) error = $"{error}\n{stderr}";
                await Finish(runId, RunStore.RunStatus.Failed, ("exit_code", rc), ("error", error));
            }
            else if (rc == 0 && run is not null && RowTruthy(run.GetValueOrDefault("is_error")))
            {
                // Exit 0 is not a verdict. The CLI reports an auth failure as
                // `subtype: "success"` with `is_error: true` and still exits 0, so
                // a run that never did anything was recorded `succeeded`. A run
                // whose own final event says it errored is FAILED.
                var resultText = run.GetValueOrDefault("result_text") as string;
                var error = !string.IsNullOrEmpty(resultText) ? resultText
                          : stderr.Length > 0 ? stderr
                          : "claude reported is_error on its result event";
                await Finish(runId, RunStore.RunStatus.Failed, ("exit_code", 0), ("error", Cut(error, StderrChars)));
            }
            else if (rc == 0)
            {
                await Finish(runId, RunStore.RunStatus.Succeeded, ("exit_code", 0));
            }
            else
            {
                await Finish(runId, RunStore.RunStatus.Failed, ("exit_code", rc),
                             ("error", stderr.Length > 0 ? stderr : $"exit code {rc}"));
            }
        }
        catch (Exception e)
        {
            // Anything unexpected — a store write raising "database is locked", a
            // decode error, cancellation on shutdown — must still leave the run
            // terminal and the child reaped, and must be logged, not swallowed.
            bool cancelled = _cancelling.ContainsKey(runId);
            if (cancelled && e is OperationCanceledException)
                // Cancel() cancels the queued driver on purpose: a normal user
                // action, not a driver failure.
                Log.LogInformation("run {RunId} cancelled while queued", runId);
            else
                Log.LogError(e, "run {RunId} driver failed", runId);
            var status = cancelled ? RunStore.RunStatus.Cancelled : RunStore.RunStatus.Failed;
            try
            {
                FinishBlocking(runId, status, ("error", Cut(Repr(e), StderrChars)));
            }
            catch (Exception e2)
            {
                Log.LogError(e2, "run {RunId} could not be marked terminal", runId);
            }
            if (proc is not null && ReturnCode(proc) is null)
            {
                KillTree(proc);
                try { await WaitExit(proc, _graceSec); }
                catch (Exception e3) { Log.LogDebug(e3, "run {RunId} could not be reaped", runId); }
            }
            if (e is OperationCanceledException)
                // The run is terminal and the child reaped; cancellation must
                // still propagate.
                throw;
        }
        finally
        {
            _procs.TryRemove(runId, out _);
            _cancelling.TryRemove(runId, out _);
            if (slotHeld)
                // Released only after the child has been reaped, and on every
                // exit path: a leaked permit permanently shrinks capacity.
                _slots.Release();
            if (proc is not null)
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Read the JSONL stream, persisting in batches off the read loop.
    ///
    /// The reader is never simply awaited. EOF is only one of the ways a child
    /// stops producing output and the rarest (see IdleOutputSec), so every wait
    /// for a line is also a chance to ask: has the process exited, and has it
    /// been quiet for too long. `ct` is the wall-clock bound (and shutdown).
    /// </summary>
    public async Task Consume(string runId, Process proc, LineReader reader, CancellationToken ct = default)
    {
        int seq = await Task.Run(() => _store.NextSeq(runId));
        var pending = new List<(int Seq, string Kind, string Payload)>();
        double lastFlush = Py.Monotonic();
        // Tokens accumulated from the per-turn usage on `assistant` events, so
        // the dashboard can watch them climb. Dollars are NOT derived from these:
        // cost is recorded from `result`, never estimated.
        var totals = new Dictionary<string, long>
        {
            ["input_tokens"] = 0, ["output_tokens"] = 0,
            ["cache_read_tokens"] = 0, ["cache_creation_tokens"] = 0,
        };
        // Sticky: a stream carrying is_error=true and then is_error=false must
        // not come back `succeeded`. Once true, true.
        bool sawError = false;

        async Task Flush()
        {
            if (pending.Count > 0)
            {
                var batch = pending;
                pending = new List<(int Seq, string Kind, string Payload)>();
                await Task.Run(() => _store.AppendEvents(runId, batch));
            }
            lastFlush = Py.Monotonic();
        }

        Task<byte[]>? lineTask = null;
        double lastLine = Py.Monotonic();
        // Consecutive quiet ticks during which the child has already exited. Two
        // rather than one so anything it wrote just before exiting has a full
        // poll interval to arrive.
        int exitedTicks = 0;
        try
        {
            while (true)
            {
                lineTask ??= reader.ReadLineAsync();
                // WhenAny rather than a cancellable read: a timeout must not
                // cancel the read, or a line completing in the same instant (the
                // `result` event the verdict is read from) would be lost.
                using (var delayCts = new CancellationTokenSource())
                {
                    var delay = Task.Delay(TimeSpan.FromSeconds(_pollSec), delayCts.Token);
                    await Task.WhenAny(lineTask, delay);
                    delayCts.Cancel();
                }
                ct.ThrowIfCancellationRequested();

                if (!lineTask.IsCompleted)
                {
                    if (ReturnCode(proc) is not null)
                    {
                        // The child is GONE and the pipe is still open, so a
                        // descendant inherited it (the CLI spawns MCP children).
                        // That never reaches EOF.
                        exitedTicks++;
                        if (exitedTicks >= 2)
                        {
                            Log.LogWarning("run {RunId}: child {Pid} exited ({Rc}) but something still holds " +
                                           "its stdout; not waiting for an EOF that cannot come",
                                runId, SafePid(proc), ReturnCode(proc));
                            return;
                        }
                        continue;
                    }
                    var idle = Py.Monotonic() - lastLine;
                    if (idle >= _idleSec.Value)
                        throw new IdleTimeout($"no output for {idle:F0}s while still running");
                    continue;
                }

                exitedTicks = 0;
                var finished = lineTask;
                lineTask = null;
                // An oversized line is the child talking, so it counts as
                // liveness even though it is dropped.
                lastLine = Py.Monotonic();
                byte[] raw;
                try
                {
                    raw = await finished;
                }
                catch (LineTooLongException e)
                {
                    // Its bytes were already discarded up to the next newline, so
                    // the stream is still aligned: log and keep reading.
                    Log.LogWarning("run {RunId}: skipping oversized stdout line ({Why})", runId, e.Message);
                    continue;
                }
                if (raw.Length == 0) break;
                var line = Encoding.UTF8.GetString(raw);
                var ev = StreamParser.ParseLine(line);
                if (ev is null) continue;

                var kind = StreamParser.EventKind(ev);
                // Capped, not stored verbatim: see StreamParser.CapPayload — it
                // shrinks long strings, so the payload still parses and still
                // carries the tool names AssessOutcome reads.
                pending.Add((seq, kind, StreamParser.CapPayload(line.Trim(), ev)));

                if (kind == "system" && StreamParser.IsString(StreamParser.Get(ev, "subtype"), out var sub)
                    && sub == "init")
                {
                    var meta = StreamParser.ExtractInitMetadata(ev);
                    var m = meta["model"] as string ?? "";
                    if (m.Length > 0)
                    {
                        await Flush();
                        await Task.Run(() => _store.UpdateRun(runId, Fields(("model", m))));
                        await PublishRunUpdated(runId);
                    }
                }
                else if (kind == "assistant")
                {
                    // Usage on EVERY assistant turn, cost only in `result`: these
                    // accumulate into the same columns the dashboard renders, and
                    // the `result` below overwrites them with the CLI's totals.
                    var usage = StreamParser.ExtractAssistantUsage(ev);
                    if (usage.Values.Any(v => v != 0))
                    {
                        foreach (var (key, value) in usage) totals[key] += value;
                        await Flush();
                        var snapshot = totals.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList();
                        await Task.Run(() => _store.UpdateRun(runId, snapshot));
                        await PublishRunUpdated(runId);
                    }
                }
                else if (kind == "result")
                {
                    var metrics = StreamParser.ExtractResultMetrics(ev);
                    sawError = sawError || (bool)metrics["is_error"]!;
                    await Flush();
                    var resultText = (string)metrics["result_text"]!;
                    var fields = Fields(
                        ("cost_usd", metrics["cost_usd"]),
                        ("input_tokens", metrics["input_tokens"]),
                        ("output_tokens", metrics["output_tokens"]),
                        ("cache_read_tokens", metrics["cache_read_tokens"]),
                        ("cache_creation_tokens", metrics["cache_creation_tokens"]),
                        ("num_turns", metrics["num_turns"]),
                        ("result_text", Cut(resultText, 20000)),
                        // The CLI's own verdict on the turn — `sawError`, so a later
                        // clean result cannot un-say an earlier failure.
                        ("is_error", sawError ? 1 : 0));
                    await Task.Run(() => _store.UpdateRun(runId, fields));
                    await PublishRunUpdated(runId);
                }

                if (pending.Count >= EventBatch || Py.Monotonic() - lastFlush >= EventFlushSec)
                    await Flush();

                Publish(new Dictionary<string, object?>
                {
                    ["type"] = "run_event", ["run_id"] = runId, ["seq"] = seq,
                    ["kind"] = kind, ["payload"] = ev,
                });
                seq++;
            }
        }
        finally
        {
            if (lineTask is not null)
                // Only ever pending on a path that is ending the run anyway
                // (idle, exited-but-held, timeout): nothing a line could change.
                Observe(lineTask);
            // Never lose buffered events, even on cancellation or a read error.
            await Flush();
        }
    }

    // ── process plumbing ────────────────────────────────────────────────────

    /// <summary>
    /// Start `argv` with all three pipes, in `cwd`, under the scrubbed
    /// environment — NOT the inherited one: `.env` may hold ANTHROPIC_API_KEY,
    /// which the CLI silently prefers over the subscription login (see ClaudeEnv).
    /// </summary>
    public static Process StartProcess(List<string> argv, string? cwd)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Py.Utf8NoBom,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(cwd))
        {
            if (!Directory.Exists(cwd))
                throw new DirectoryNotFoundException(Py.IsWindows ? $"[WinError 267] The directory name is invalid: '{cwd}'" : $"[Errno 2] No such file or directory: '{cwd}'");
            psi.WorkingDirectory = cwd;
        }
        psi.Environment.Clear();
        foreach (var (k, v) in ClaudeEnv.ChildEnv())
            if (v is not null) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new FileNotFoundException($"could not start {argv[0]}", argv[0]);
    }

    /// <summary>`proc.returncode`: the exit code once exited, else null.</summary>
    public static int? ReturnCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode : null; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public static void KillTree(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException
                                      or AggregateException) { }
    }

    /// <summary>Wait up to `seconds` for exit without ever cancelling anything else. True when exited.</summary>
    public static async Task<bool> WaitExit(Process proc, double seconds, CancellationToken ct = default)
    {
        try
        {
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds), ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;   // no process associated any more: it is gone
        }
    }

    private static object SafePid(Process proc)
    {
        try { return proc.Id; } catch { return "?"; }
    }

    private static void Observe(Task t) =>
        t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private static List<KeyValuePair<string, object?>> Fields(params (string Key, object? Value)[] f) =>
        f.Select(x => new KeyValuePair<string, object?>(x.Key, x.Value)).ToList();

    private static bool RowTruthy(object? v) => v switch
    {
        null => false,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        bool b => b,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>Python `repr(exc)`-ish: `TypeName('message')`.</summary>
    public static string Repr(Exception e) => $"{e.GetType().Name}('{e.Message.Replace("'", "\\'")}')";

    /// <summary>Python `str(float)`: 1800.0, 21600.0, 2.5.</summary>
    public static string PyFloat(double d)
    {
        if (double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e16)
            return d.ToString("0", CultureInfo.InvariantCulture) + ".0";
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Cut(string s, int n) => StreamParser.Cut(s, n);

    // ── helper types ────────────────────────────────────────────────────────

    /// <summary>A thread-safe byte buffer that keeps only the last `capacity` bytes.</summary>
    public sealed class ByteTail(int capacity)
    {
        private readonly List<byte> _bytes = new();
        private readonly object _gate = new();

        public void Append(byte[] buf, int n)
        {
            lock (_gate)
            {
                _bytes.AddRange(buf.AsSpan(0, n).ToArray());
                if (_bytes.Count > capacity) _bytes.RemoveRange(0, _bytes.Count - capacity);
            }
        }

        public byte[] ToArray()
        {
            lock (_gate) return _bytes.ToArray();
        }
    }

    /// <summary>
    /// `StreamReader.readline()` with asyncio's semantics: returns the line
    /// including its '\n', a final partial line at EOF, and an empty array at
    /// EOF. A line longer than `limit` is discarded up to and including its
    /// newline (keeping the stream aligned) and reported as LineTooLongException.
    /// One call at a time.
    /// </summary>
    public sealed class LineReader(Stream stream, int limit)
    {
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _start, _end;
        private bool _eof;

        public async Task<byte[]> ReadLineAsync(CancellationToken ct = default)
        {
            var acc = new MemoryStream();
            bool overflow = false;
            long seen = 0;
            while (true)
            {
                if (_start < _end)
                {
                    int idx = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                    int take = idx >= 0 ? idx - _start + 1 : _end - _start;
                    seen += take;
                    if (!overflow)
                    {
                        if (seen > limit) { overflow = true; acc.SetLength(0); }
                        else acc.Write(_buf, _start, take);
                    }
                    _start += take;
                    if (idx >= 0)
                    {
                        if (overflow) throw new LineTooLongException(seen);
                        return acc.ToArray();
                    }
                    continue;
                }
                if (_eof)
                {
                    if (overflow) throw new LineTooLongException(seen);
                    return acc.ToArray();
                }
                int n = await stream.ReadAsync(_buf.AsMemory(0, _buf.Length), ct);
                _start = 0;
                _end = n;
                if (n == 0) _eof = true;
            }
        }
    }
}
