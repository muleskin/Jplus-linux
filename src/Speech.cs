using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// speech.py — the mouth. A sentence splitter for streamed text and a scheduler
/// that owns every utterance JARVIS makes (see spec §4).
///
/// The Python ran on one asyncio event loop, and the scheduler's correctness
/// (barge-in landing between two awaits of a send loop, flags flipped before a
/// frame goes out) depends on that: state only changes at await points. The
/// port keeps that model literally — every scheduler (and Brain) method runs on
/// <see cref="EventLoop.Default"/>, a single-threaded SynchronizationContext,
/// and public entry points called from any other thread marshal onto it.
/// </summary>
public static partial class Speech
{
    public static readonly ILogger Log = Py.Log("jarvis.speech");

    // ── the event loop ──────────────────────────────────────────────────────

    /// <summary>
    /// asyncio's event loop: one dedicated thread with a SynchronizationContext,
    /// so every `await` inside scheduler / brain code resumes on that thread and
    /// nothing interleaves except at an await — exactly the guarantee the
    /// Python relied on. Shared by Speech and BrainMod (one loop, as in Python).
    /// </summary>
    public sealed class EventLoop : SynchronizationContext
    {
        public static readonly EventLoop Default = new("jarvis-loop");

        private readonly BlockingCollection<(SendOrPostCallback cb, object? state)> _queue = new();
        private readonly Thread _thread;

        public EventLoop(string name)
        {
            _thread = new Thread(RunLoop) { IsBackground = true, Name = name };
            _thread.Start();
        }

        private void RunLoop()
        {
            SetSynchronizationContext(this);
            foreach (var (cb, state) in _queue.GetConsumingEnumerable())
            {
                try { cb(state); }
                catch (Exception e) { Log.LogError(e, "event loop callback failed"); }
            }
        }

        /// <summary>True when the caller is already on the loop thread.</summary>
        public bool IsOnLoop => Thread.CurrentThread == _thread;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (IsOnLoop) { d(state); return; }
            using var done = new ManualResetEventSlim(false);
            ExceptionDispatchInfo? err = null;
            Post(_ =>
            {
                try { d(state); }
                catch (Exception e) { err = ExceptionDispatchInfo.Capture(e); }
                finally { done.Set(); }
            }, null);
            done.Wait();
            err?.Throw();
        }

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>Post a plain action to run on the loop (`loop.call_soon`).</summary>
        public void Post(Action a) => Post(_ => a(), null);

        /// <summary>Run a synchronous function on the loop and wait for its result (inline when already on it).</summary>
        public T Invoke<T>(Func<T> f)
        {
            if (IsOnLoop) return f();
            T result = default!;
            Send(_ => result = f(), null);
            return result;
        }

        public void Invoke(Action a)
        {
            if (IsOnLoop) { a(); return; }
            Send(_ => a(), null);
        }

        /// <summary>`await coro` from anywhere: inline when already on the loop, else hopped onto it.</summary>
        public Task<T> Run<T>(Func<Task<T>> f) => IsOnLoop ? f() : CreateTask(f);

        public Task Run(Func<Task> f) => IsOnLoop ? f() : CreateTask(f);

        /// <summary>`asyncio.create_task(coro)`: always scheduled for a later loop iteration.</summary>
        public Task<T> CreateTask<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { tcs.TrySetResult(await f()); }
                catch (OperationCanceledException) { tcs.TrySetCanceled(); }
                catch (Exception e) { tcs.TrySetException(e); }
            }, null);
            return tcs.Task;
        }

        public Task CreateTask(Func<Task> f) => CreateTask<bool>(async () => { await f(); return true; });

        /// <summary>`loop.call_later(delay, cb)`.</summary>
        public void CallLater(double delaySeconds, Action cb)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.0, delaySeconds)))
                .ContinueWith(_ => Post(cb), TaskScheduler.Default);
        }
    }

    /// <summary>`asyncio.Event`. Use it from the loop thread.</summary>
    public sealed class AsyncEvent
    {
        private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSet => _tcs.Task.IsCompleted;
        public void Set() => _tcs.TrySetResult();
        public void Clear()
        {
            if (_tcs.Task.IsCompleted) _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task Wait() => _tcs.Task;
    }

    /// <summary>Python's `repr()` of a str, for log lines.</summary>
    public static string Repr(string? s)
    {
        if (s is null) return "None";
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == q) sb.Append('\\').Append(c);
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append(q);
        return sb.ToString();
    }

    private static string Left(string? s, int n) => s is null ? "" : (s.Length <= n ? s : s[..n]);

    // ── sentence splitting ──────────────────────────────────────────────────

    public static readonly HashSet<string> Abbreviations = new()
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "inc", "ltd",
        "co", "no", "approx", "fig", "dept", "e.g", "i.e", "a.m", "p.m", "u.s", "u.k",
    };
    public const string Closers = "\"')]";
    public static readonly string[] ClauseBreaks = [", ", "; ", " — ", " – ", ": "];
    public static readonly string[] StrongBreaks = [" — ", " – ", "; ", ": "];   // a pause a listener already expects

    public static string WordBefore(string text, int i)
    {
        int j = i;
        while (j > 0 && !char.IsWhiteSpace(text[j - 1])) j--;
        return text[j..i];
    }

    public class SentenceSplitter
    {
        private string _buf = "";
        public int Emitted;
        private readonly int _firstChunkMax;
        private readonly int _firstChunkMin;

        public SentenceSplitter(int firstChunkMax = 160, int firstChunkMin = 40)
        {
            _firstChunkMax = firstChunkMax;
            _firstChunkMin = firstChunkMin;
        }

        public List<string> Feed(string delta)
        {
            _buf += delta;
            var output = new List<string>();
            while (true)
            {
                var cut = Boundary(_buf);
                if (cut is null) break;
                output.Add(Take(cut.Value));
            }
            if (Emitted == 0)
            {
                // Get the first audio out early: the moment a STRONG break (dash,
                // semicolon, colon) appears past `first_chunk_min`, cut there — it is
                // a pause the listener expects anyway. Past `first_chunk_max` with no
                // sentence end, fall back to the last break of any kind.
                var cut = FirstStrongBreak(_buf, _firstChunkMin);
                if (cut is null && _buf.Length >= _firstChunkMax)
                    cut = LastClauseBreak(_buf);
                if (cut is int c && c != 0)
                    output.Add(Take(c));
            }
            return output.Where(c => c.Length > 0).ToList();
        }

        public static int? FirstStrongBreak(string text, int minPos)
        {
            int? best = null;
            foreach (var sep in StrongBreaks)
            {
                int pos = minPos <= text.Length ? text.IndexOf(sep, Math.Max(0, minPos), StringComparison.Ordinal) : -1;
                if (pos >= minPos)
                {
                    int end = pos + sep.TrimEnd().Length;
                    if (best is null || end < best) best = end;
                }
            }
            return best;
        }

        public string? Flush()
        {
            var chunk = _buf.Trim();
            _buf = "";
            if (chunk.Length > 0)
            {
                Emitted++;
                return chunk;
            }
            return null;
        }

        public string Take(int cut)
        {
            var chunk = _buf[..cut].Trim();
            _buf = _buf[cut..].TrimStart();
            if (chunk.Length > 0) Emitted++;
            return chunk;
        }

        public int? Boundary(string text)
        {
            int i = 0, n = text.Length;
            while (i < n)
            {
                if (".!?".IndexOf(text[i]) >= 0)
                {
                    int j = i;
                    while (j < n && ".!?".IndexOf(text[j]) >= 0) j++;
                    int k = j;
                    while (k < n && Closers.IndexOf(text[k]) >= 0) k++;
                    if (k >= n) return null;                 // need to see what follows
                    if (!char.IsWhiteSpace(text[k]))
                    {
                        i = k;                               // "3.5", "server.py"
                        continue;
                    }
                    if (text[i] == '.' && j - i == 1)
                    {
                        var word = WordBefore(text, i);
                        if (Abbreviations.Contains(word.ToLowerInvariant())
                            || (word.Length == 1 && char.IsUpper(word[0]))
                            || (word.Length > 0 && word.All(char.IsDigit)))   // "Mr.", initials "J.", list markers "1."
                        {
                            i = k;
                            continue;
                        }
                    }
                    return k;
                }
                i++;
            }
            return null;
        }

        public static int? LastClauseBreak(string text)
        {
            int? best = null;
            foreach (var sep in ClauseBreaks)
            {
                int pos = text.LastIndexOf(sep, StringComparison.Ordinal);
                if (pos > 0)
                {
                    int end = pos + sep.TrimEnd().Length;
                    best = best is null || end > best ? end : best;
                }
            }
            return best;
        }
    }

    // ── scheduler constants ─────────────────────────────────────────────────

    public enum Priority
    {
        Low = 0,
        Normal = 1,
        Urgent = 2,
    }

    public static readonly string[] Bridges = ["As I was saying —", "Back to it —", "Where was I —"];
    public static readonly string[] CancelWords = ["wait", "no", "stop", "cancel", "hold on"];
    public const string UnreadPrefix = "Before I forget —";
    // The last resort, and only that: the bound on a `Spoken` entry nothing else
    // is watching. The ack watchdog, `Orphan`, `BargeIn`, `Abandon` and
    // `TransportGone` settle every live path on `ack_timeout` or sooner; if
    // anything reaches this, ask why rather than lowering it.
    public const double RecentBackstopSec = 120.0;   // a sent chunk is forgotten after this even if never acked
    public const int UtteranceHistory = 64;          // finished utterances kept for late acks
    public const int MaxResumes = 3;                 // an utterance interrupted more often than this is dropped

    // An ack says a chunk FINISHED playing. It cannot have finished before the
    // previous chunk finished plus its own length, so an ack that arrives
    // earlier than that is held until the moment it could be true — otherwise
    // `high_sent` was walkable (each ack unlocks a send, which raises the bound)
    // and `wait_for` opened a steer's cancel window over audio nobody had heard.
    // The length comes from the mp3 itself, scaled DOWN so it is a floor, and
    // capped well under `ack_timeout`. A blob that is not mp3 has no floor.
    public const double AckFloorFactor = 0.9;
    public const double AckFloorMaxSec = 30.0;
    public const double ResumeAckGraceSec = 1.0;     // after a kept chunk's floor, how long a resume waits for its ack

    public static readonly Dictionary<int, int> Mp3KbpsV1 = new()
    {
        [1] = 32, [2] = 40, [3] = 48, [4] = 56, [5] = 64, [6] = 80, [7] = 96, [8] = 112,
        [9] = 128, [10] = 160, [11] = 192, [12] = 224, [13] = 256, [14] = 320,
    };
    public static readonly Dictionary<int, int> Mp3KbpsV2 = new()
    {
        [1] = 8, [2] = 16, [3] = 24, [4] = 32, [5] = 40, [6] = 48, [7] = 56, [8] = 64,
        [9] = 80, [10] = 96, [11] = 112, [12] = 128, [13] = 144, [14] = 160,
    };

    /// <summary>How long this MPEG Layer III audio plays, from its first frame header
    /// (constant bitrate, which is what Fish Audio emits). 0.0 for anything
    /// that is not one — no header, no floor.</summary>
    public static double Mp3Seconds(byte[]? data)
    {
        if (data is null || data.Length == 0) return 0.0;
        long i = 0;
        if (data.Length >= 10 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
            i = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9]);
        long end = Math.Min(data.Length, i + 4096);
        while (i + 4 <= end)
        {
            int b1 = data[i], b2 = data[i + 1], b3 = data[i + 2];
            if (b1 == 0xFF && (b2 & 0xE0) == 0xE0)
            {
                int version = (b2 >> 3) & 0x3, layer = (b2 >> 1) & 0x3;
                int kbpsIndex = b3 >> 4, rateIndex = (b3 >> 2) & 0x3;
                if (version != 1 && layer == 1 && kbpsIndex > 0 && kbpsIndex < 15 && rateIndex != 3)
                {
                    var table = version == 3 ? Mp3KbpsV1 : Mp3KbpsV2;
                    return (data.Length - i) * 8 / (table[kbpsIndex] * 1000.0);
                }
            }
            i++;
        }
        return 0.0;
    }

    public static double AckFloorSeconds(byte[]? audio) =>
        Math.Min(Mp3Seconds(audio) * AckFloorFactor, AckFloorMaxSec);

    // A one- or two-word utterance whose every token JARVIS just said is only an
    // echo if it arrived while that speech was still coming out of the speaker.
    // Chrome's endpointer hands the user's FIRST word over as a final on its own
    // ("now", "run"), measured 1-2s after his last chunk was acked — the ordinary
    // turn-taking gap. A true echo lands within its own playback; dropping this
    // eats the start of their sentence. Longer utterances keep the 6s window.
    public const double ShortEchoGraceSec = 0.75;

    // While his audio is audible, how much of a phrase may be made of HIS OWN
    // words before it is treated as his voice coming back rather than the user's.
    // A speaker-mangled echo ('found it guitar an' for "Found it — chitauri is
    // idle on ...") carries as many new words as a real interruption ("actually
    // hold"); the proportion separates them where a count cannot.
    public const double EchoShareWhileAudible = 0.5;
    public const int ShortUtteranceTokens = 2;

    public static readonly Regex TokenRe = new(@"[a-z0-9']+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static List<string> Tokens(string? text) =>
        TokenRe.Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value).ToList();

    // "Say that again" must be replayed, not answered — regenerating costs a
    // brain turn AND comes back as different words. Matched narrowly on the
    // tokenized text with filler trimmed off BOTH ends only (never the middle).
    // Deliberately NOT matched: real questions ("what did you mean by that",
    // "can you explain that again", "say more about that").
    public static readonly HashSet<string> ReplayFiller = ["sorry", "jarvis", "please", "um", "uh", "i'm"];
    // Token tuples, joined with a single space (tokens never contain one).
    public static readonly HashSet<string> ReplayTriggers =
    [
        "say that again",
        "say it again",
        "repeat",
        "repeat that",
        "repeat it",
        "what did you say",
        "what was that",
        "come again",
        "one more time",
        "pardon",
        "pardon me",
    ];

    public static bool IsReplayRequest(List<string> toks)
    {
        int i = 0, j = toks.Count;
        while (i < j && ReplayFiller.Contains(toks[i])) i++;
        while (j > i && ReplayFiller.Contains(toks[j - 1])) j--;
        if (i >= j) return false;   // the empty tuple is not a trigger
        return ReplayTriggers.Contains(string.Join(" ", toks.GetRange(i, j - i)));
    }

    // ── data ────────────────────────────────────────────────────────────────

    /// <summary>One speakable piece of an utterance. Synthesis writes to the object, never
    /// to an index, so chunks can be inserted (the resume bridge) while synthesis
    /// for later chunks is still in flight.</summary>
    public class Chunk
    {
        public string Text { get; set; }
        public byte[]? Audio { get; set; }
        public bool Ready { get; set; }
        public bool Notice { get; set; }          // this chunk's failure was the third in a row: warn after it
        public double EarliestAck { get; set; }   // no honest ack can arrive before this (see AckFloorFactor)

        public Chunk(string text, byte[]? audio = null, bool ready = false, bool notice = false, double earliestAck = 0.0)
        {
            Text = text;
            Audio = audio;
            Ready = ready;
            Notice = notice;
            EarliestAck = earliestAck;
        }
    }

    public class Utterance
    {
        public int Id { get; set; }
        public Priority Priority { get; set; }
        public string Kind { get; set; }                      // "turn" | "say" | "batched"
        public double Created { get; set; }
        public bool Immediate { get; set; } = true;           // may start while the user is talking
        public List<Chunk> Chunks { get; set; } = new();
        public int Sent { get; set; } = -1;                   // highest chunk index sent to the client
        // Highest index EVER sent; only ever grows. `Sent` is rolled BACKWARDS by
        // a preemption and by a resume bridge, so it is not a bound on what the
        // client may legitimately ack.
        public int HighSent { get; set; } = -1;
        public int Played { get; set; } = -1;                 // highest chunk index the client acked
        public int HeldAck { get; set; } = -1;                // an ack that arrived before its chunk could have finished
        public bool Closed { get; set; }                      // no more chunks coming
        public bool Cancelled { get; set; }
        public bool Abandoned { get; set; }                   // set only by Abandon(): a transport failure, never a user cancel
        public double? PausedAt { get; set; }
        public int Resumes { get; set; }
        public double? FirstSentAt { get; set; }
        public double? LastProgressAt { get; set; }           // last send or ack; the ack watchdog reads it
        public Utterance? Alias { get; set; }                 // a batched LOW item points at the merged utterance
        public SentenceSplitter Splitter { get; set; } = new();

        public Utterance(int id, Priority priority, string kind, double created, bool immediate = true)
        {
            Id = id;
            Priority = priority;
            Kind = kind;
            Created = created;
            Immediate = immediate;
        }

        public bool Done
        {
            get
            {
                if (Alias is not null) return Alias.Done;
                return Cancelled || (Closed && Played >= Chunks.Count - 1 && Sent >= Chunks.Count - 1);
            }
        }

        public bool WasCancelled => (Alias ?? this).Cancelled;

        /// <summary>True only for a transport failure (Abandon()), never a genuine
        /// cancel word or barge-in — so a caller checking `WasCancelled` can tell a
        /// dead client from the user's own decision.</summary>
        public bool WasAbandoned => (Alias ?? this).Abandoned;

        public string RemainingText() => string.Join(" ", Chunks.Skip(Math.Max(0, Played + 1)).Select(c => c.Text));

        public List<string> Texts() => Chunks.Select(c => c.Text).ToList();
    }

    public class Spoken
    {
        public int Utt { get; set; }
        public int Idx { get; set; }
        public HashSet<string> Tokens { get; set; }
        public double SentAt { get; set; }
        public double? AckedAt { get; set; }
        public double? ExpiresAt { get; set; }   // nobody will ever ack this; believe it is playing only until here

        public Spoken(int utt, int idx, HashSet<string> tokens, double sentAt)
        {
            Utt = utt;
            Idx = idx;
            Tokens = tokens;
            SentAt = sentAt;
        }
    }

    // ── the scheduler ───────────────────────────────────────────────────────

    /// <summary>Owns the mouth. Everything JARVIS says goes through here (spec §4).
    ///
    /// `synthesize(text)` returns mp3 bytes or null; `emit(msg)` hands one
    /// protocol frame (a dict with "type") to the transport and may throw when
    /// nobody can receive a content frame. All state lives on the event loop;
    /// public methods may be called from any thread.</summary>
    public class SpeechScheduler
    {
        private readonly Func<double> _clock;
        public double AckTimeout;                    // a chunk unacked this long = the client is gone
        private readonly Func<bool> _transportReady;
        private readonly Func<string, Task<byte[]?>> _synth;
        private readonly Func<Dictionary<string, object?>, Task> _emitRaw;
        // One protocol frame at a time, in order. Not re-entrant: the transport
        // passed as `emit` must never call back into BargeIn()/Say() from inside a send.
        private readonly SemaphoreSlim _emitLock = new(1, 1);
        public double BatchSettle;                   // LOW items collect this long before flushing
        private readonly Func<string, string> _prepare;
        public int Prefetch;
        public double BatchInterval;
        public double PauseAfter;
        public double StaleAfter;
        public double EchoWindow;
        public string[] Bridges;
        public string[] CancelWords;
        private readonly EventLoop _loop;

        private int _nextId = 1;
        private Utterance? _current;
        private Utterance? _paused;
        private List<Utterance> _pending = new();
        private List<Utterance> _batch = new();
        private readonly List<string> _unread = new();
        private List<Spoken> _recent = new();
        private Utterance? _lastSpoken;              // for "say that again"
        private double? _lastUserSpeech;
        private double _lastBatchFlush;
        private double? _batchSince;
        private bool _cancelWindowOpen;
        private AsyncEvent _cancelEvent = new();
        private bool _speaking;
        private readonly AsyncEvent _wake = new();
        private Task? _tickTask;
        private CancellationTokenSource? _tickCts;
        private readonly HashSet<Task> _tasks = new();
        private CancellationTokenSource _tasksCts = new();
        private int _ttsFailures;
        public Dictionary<int, Utterance> Utterances { get; } = new();

        /// <summary>`clock` drives scheduling decisions only; `WaitFor` and
        /// `OpenCancelWindow` sleep in real time. `transportReady` says whether
        /// anyone can hear us — proactive (unread/batched) speech waits for it.</summary>
        public SpeechScheduler(Func<string, Task<byte[]?>> synthesize, Func<Dictionary<string, object?>, Task> emit,
            Func<string, string>? prepare = null, int prefetch = 2,
            double batchInterval = 20.0, double pauseAfter = 3.0,
            double staleAfter = 60.0, double echoWindow = 6.0,
            double batchSettle = 2.0, double ackTimeout = 45.0,
            Func<bool>? transportReady = null,
            string[]? bridges = null, string[]? cancelWords = null,
            Func<double>? clock = null, EventLoop? loop = null)
        {
            _clock = clock ?? Py.Monotonic;
            AckTimeout = ackTimeout;
            _transportReady = transportReady ?? (() => true);
            _synth = synthesize;
            _emitRaw = emit;
            BatchSettle = batchSettle;
            _prepare = prepare ?? (s => s);
            Prefetch = prefetch;
            BatchInterval = batchInterval;
            PauseAfter = pauseAfter;
            StaleAfter = staleAfter;
            EchoWindow = echoWindow;
            Bridges = bridges ?? Speech.Bridges;
            CancelWords = (cancelWords ?? Speech.CancelWords).ToArray();
            _loop = loop ?? EventLoop.Default;
            _lastBatchFlush = _clock() - batchInterval;   // the first flush needs no wait
        }

        /// <summary>The loop this scheduler lives on.</summary>
        public EventLoop Loop => _loop;

        // ── lifecycle ──────────────────────────────────────────────────

        public Task Start()
        {
            return _loop.Run(() =>
            {
                if (_tickTask is null)
                {
                    _tickCts = new CancellationTokenSource();
                    var ct = _tickCts.Token;
                    _tickTask = _loop.CreateTask(() => Tick(ct));
                }
                return Task.CompletedTask;
            });
        }

        public async Task Stop()
        {
            if (!_loop.IsOnLoop) { await _loop.Run(() => Stop()); return; }
            var tasks = _tasks.ToList();
            _tasksCts.Cancel();
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); } catch { }   // return_exceptions=True
            }
            _tasksCts = new CancellationTokenSource();
            if (_tickTask is not null)
            {
                _tickCts?.Cancel();
                try { await _tickTask; } catch (OperationCanceledException) { }
                _tickTask = null;
                _tickCts = null;
            }
        }

        public async Task Tick(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.WhenAny(_wake.Wait(), Task.Delay(250, ct));
                }
                catch (OperationCanceledException) { }
                if (ct.IsCancellationRequested) break;
                _wake.Clear();
                try
                {
                    await Pump();
                }
                catch (Exception e)   // the mouth must never die
                {
                    Log.LogError(e, "speech pump failed: {Error}", e.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        public void Kick()
        {
            if (_loop.IsOnLoop) _wake.Set();
            else _loop.Post(() => _wake.Set());
        }

        /// <summary>Wake the pump when a timed gate (batch settle, user pause) elapses.</summary>
        public void KickLater(double delay) => _loop.CallLater(Math.Max(0.0, delay) + 0.01, Kick);

        public async Task Emit(Dictionary<string, object?> msg)
        {
            await _emitLock.WaitAsync();
            try { await _emitRaw(msg); }
            finally { _emitLock.Release(); }
        }

        public void Spawn(Func<Task> work)
        {
            var task = _loop.CreateTask(work);
            _tasks.Add(task);
            task.ContinueWith(t => _loop.Post(() => _tasks.Remove(t)), TaskScheduler.Default);
        }

        public bool IsSpeaking => _speaking;

        // ── inputs ─────────────────────────────────────────────────────

        public Utterance New(Priority priority, string kind, bool immediate)
        {
            var u = new Utterance(_nextId, priority, kind, _clock(), immediate);
            _nextId++;
            Utterances[u.Id] = u;
            EvictHistory();
            return u;
        }

        public void EvictHistory()
        {
            if (Utterances.Count <= UtteranceHistory) return;
            foreach (var uid in Utterances.Keys.OrderBy(k => k).ToList())   // insertion order: ids only grow
            {
                var u = Utterances[uid];
                // `_lastSpoken` is exempt: it is what "say that again" replays, and
                // freeing its audio would silently turn a free replay into a re-synthesis.
                if (u.Done && u != _current && u != _paused && u != _lastSpoken)
                {
                    foreach (var c in u.Chunks) c.Audio = null;   // free the synthesized bytes
                    Utterances.Remove(uid);
                }
                if (Utterances.Count <= UtteranceHistory) break;
            }
        }

        /// <summary>A streaming reply to the user's own words: starts immediately.</summary>
        public Utterance BeginTurn(Priority priority = Priority.Normal)
        {
            if (!_loop.IsOnLoop) return _loop.Invoke(() => BeginTurn(priority));
            var u = New(priority, "turn", immediate: true);
            _pending.Add(u);
            Kick();
            return u;
        }

        public void Feed(Utterance utt, string delta)
        {
            if (!_loop.IsOnLoop) { _loop.Invoke(() => Feed(utt, delta)); return; }
            if (utt.Cancelled || utt.Closed) return;
            foreach (var text in utt.Splitter.Feed(delta))
                AddChunk(utt, text);
        }

        public async Task EndTurn(Utterance utt)
        {
            if (!_loop.IsOnLoop) { await _loop.Run(() => EndTurn(utt)); return; }
            if (!utt.Cancelled)
            {
                var tail = utt.Splitter.Flush();
                if (tail is not null) AddChunk(utt, tail);
            }
            utt.Closed = true;
            Kick();
        }

        public void Fill(Utterance u, string text)
        {
            foreach (var chunk in u.Splitter.Feed(text))
                AddChunk(u, chunk);
            var tail = u.Splitter.Flush();
            if (tail is not null) AddChunk(u, tail);
            u.Closed = true;
        }

        /// <summary>A complete utterance. NORMAL starts at once; LOW/URGENT wait for a pause
        /// unless `immediate` says otherwise. LOW is batched: the returned utterance
        /// becomes done when the merged announcement it joined has been spoken.</summary>
        public async Task<Utterance> Say(string text, Priority priority = Priority.Normal, bool? immediate = null)
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => Say(text, priority, immediate));
            if (priority == Priority.Low && immediate != true)
            {
                var b = New(priority, "batched", immediate: false);
                b.Chunks.Add(new Chunk(text.Trim()));
                b.Closed = true;
                _batch.Add(b);
                if (_batchSince is null || _batchSince == 0.0) _batchSince = _clock();   // Python's `or`
                KickLater(BatchSettle);
                return b;
            }
            immediate ??= priority == Priority.Normal;
            var u = New(priority, "say", immediate: immediate.Value);
            Fill(u, text);
            _pending.Add(u);
            Kick();
            return u;
        }

        /// <summary>True when the utterance has been fully played; False on cancel/timeout.</summary>
        public async Task<bool> WaitFor(Utterance utt, double timeout = 60.0)
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => WaitFor(utt, timeout));
            var deadline = _clock() + timeout;
            while (_clock() < deadline)
            {
                if (utt.Done) return !utt.WasCancelled;
                await Task.Delay(20);
            }
            return false;
        }

        /// <summary>Listen for a cancel word. True if the user cancelled.</summary>
        public async Task<bool> OpenCancelWindow(double seconds)
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => OpenCancelWindow(seconds));
            var evt = new AsyncEvent();
            _cancelEvent = evt;
            _cancelWindowOpen = true;
            try
            {
                var w = evt.Wait();
                var first = await Task.WhenAny(w, Task.Delay(TimeSpan.FromSeconds(Math.Max(0.0, seconds))));
                return first == w;
            }
            finally
            {
                _cancelWindowOpen = false;
            }
        }

        /// <summary>'echo' (JARVIS hearing himself), 'cancel', 'replay' ("say that
        /// again" — see `IsReplayRequest`), or 'speech'.</summary>
        public string Classify(string text, bool allowCancel = true, bool allowReplay = true)
        {
            if (!_loop.IsOnLoop) return _loop.Invoke(() => Classify(text, allowCancel, allowReplay));
            var toks = Tokens(text);
            if (toks.Count == 0) return "echo";
            // DELIBERATELY before `_recent` is consulted: a cancel word heard while
            // the window is open cancels, even if JARVIS himself has just said it.
            // The window opens only after the read-back has finished PLAYING, it is
            // short (two seconds), and the costs are not symmetric: a false cancel
            // costs one repeated sentence, a missed one sends a message the user
            // audibly tried to stop. Here, failing toward NOT acting is right.
            if (allowCancel && _cancelWindowOpen && CancelWords.Contains(string.Join(" ", toks)))
                return "cancel";
            if (allowReplay && IsReplayRequest(toks))
                return "replay";
            var recent = RecentTokens();
            if (recent.Count > 0)
            {
                int matching = toks.Count(t => recent.Contains(t));
                if ((double)matching / toks.Count >= 0.7 && toks.Count - matching < 2)
                {
                    if (toks.Count <= ShortUtteranceTokens && SinceLastAck() > ShortEchoGraceSec)
                        return "speech";   // his word, but the user's mouth: see ShortEchoGraceSec
                    return "echo";
                }
            }
            return "speech";
        }

        /// <summary>For the log, and for the log only: how long since the client
        /// finished playing anything. Infinity when nothing has been acked.
        /// Deliberately NOT `SinceLastAck` — the classifier asks a different question.</summary>
        public double SecondsSinceLastPlayed()
        {
            if (!_loop.IsOnLoop) return _loop.Invoke(() => SecondsSinceLastPlayed());
            var acked = _recent.Where(s => s.AckedAt is not null).Select(s => s.AckedAt!.Value).ToList();
            if (acked.Count == 0) return double.PositiveInfinity;
            return _clock() - acked.Max();
        }

        /// <summary>How long the room has been quiet, for the short-utterance grace.
        ///
        /// The client acks when a chunk FINISHES playing, so a chunk sent and unacked
        /// is coming out of the speaker RIGHT NOW — exactly when its echo is loudest.
        /// While anything is sent and unacked the answer is 0.0 and the echo rule
        /// applies in full; Infinity only when nothing in `_recent` could echo.</summary>
        public double SinceLastAck()
        {
            if (_recent.Any(s => s.AckedAt is null)) return 0.0;
            return SecondsSinceLastPlayed();
        }

        public int Nonmatching(string text)
        {
            var recent = RecentTokens();
            return Tokens(text).Count(t => !recent.Contains(t));
        }

        public async Task<string> UserInterim(string text)
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => UserInterim(text));
            // Replay is decided only on the FINAL transcript (a partial "say that"
            // is not yet "say that again"); interim barge-in stays driven by "speech".
            var verdict = Classify(text, allowCancel: false, allowReplay: false);
            if (verdict == "speech")
            {
                _lastUserSpeech = _clock();
                KickLater(PauseAfter);
                if (_speaking && ShouldInterrupt(text))
                    await BargeIn(reason: $"heard {Repr(Left(text, 40))}");
            }
            return verdict;
        }

        /// <summary>Whether an interim heard mid-sentence should stop him talking.
        ///
        /// Two new words is the general bar, so a stray echo fragment cannot cut him
        /// off. But people interrupt with ONE word — "stop", "wait", "no" — so a
        /// cancel word counts on its own, provided it is not him being heard back
        /// (`Nonmatching` >= 1) and not a clipped STEM of something he is saying
        /// ("Stopped the work" heard as "stop").</summary>
        public bool ShouldInterrupt(string text)
        {
            int novel = Nonmatching(text);
            // While his audio is coming out of the speaker the recogniser garbles it
            // into words he never said ("found it guitar an"), which token matching
            // cannot catch. So the bar rises while he is audibly speaking —
            // `SinceLastAck` is 0.0 for exactly as long as a chunk is sent and
            // unacked. (`SecondsSinceLastPlayed` would leave this off for the whole
            // first utterance.)
            if (SinceLastAck() == 0.0 && EchoShare(text) >= EchoShareWhileAudible)
                return false;
            if (novel >= 2)
            {
                // The general bar is untouched, stems included: "delete the file"
                // over "Deleting the staging files" must still cut him off.
                return true;
            }
            var toks = Tokens(text);
            if (!CancelWords.Contains(string.Join(" ", toks)) || novel < 1)
                return false;
            return !IsStemEcho(toks);
        }

        /// <summary>What proportion of these words are ones he is saying right now.
        /// Whole-token, like `Nonmatching`: a stem rule here would swallow real
        /// follow-ups that share roots with him.</summary>
        public double EchoShare(string text)
        {
            var toks = Tokens(text);
            if (toks.Count == 0) return 1.0;   // nothing said is not the user talking
            var recent = RecentTokens();
            if (recent.Count == 0) return 0.0;
            return (double)toks.Count(t => recent.Contains(t)) / toks.Count;
        }

        /// <summary>Is every one of these words just a clipped form of something he is
        /// currently saying? Deliberately narrow: four characters is the minimum stem,
        /// and the words may differ by at most four characters (-s, -ed, -ing, -ping,
        /// -led). Used ONLY by the one-word cancel branch.</summary>
        public bool IsStemEcho(List<string> toks)
        {
            var recent = RecentTokens();
            if (recent.Count == 0) return false;
            return toks.All(t => recent.Any(r =>
                t.Length >= 4 && Math.Abs(r.Length - t.Length) <= 4
                && (r.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(r, StringComparison.Ordinal))));
        }

        public async Task<string> UserFinal(string text)
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => UserFinal(text));
            var verdict = Classify(text);
            if (verdict == "echo") return verdict;
            _lastUserSpeech = _clock();
            KickLater(PauseAfter);
            bool isCancelWord = CancelWords.Contains(string.Join(" ", Tokens(text)));
            if (verdict == "cancel") _cancelEvent.Set();
            if (_speaking && (isCancelWord || verdict == "replay" || Nonmatching(text) >= 2))
            {
                // A cancel — or a replay request — is an answer to what was being
                // said: it was heard, so it must not come back as "Before I forget —".
                // A one-word replay trigger would not clear the >=2 bar, so it is named.
                await BargeIn(keepUnread: verdict != "cancel", reason: $"{verdict}: {Repr(Left(text, 40))}");
            }
            return verdict;
        }

        /// <summary>"Say that again": resend the most recently completed utterance.
        ///
        /// Reuses its synthesized audio VERBATIM whenever every chunk still has it —
        /// free and exact, where a fresh brain turn comes back as different words.
        /// If the audio is gone (a failed synthesis, or aged out of history) the same
        /// words are re-synthesized. Returns False when nothing has been said yet.</summary>
        public async Task<bool> ReplayLast()
        {
            if (!_loop.IsOnLoop) return await _loop.Run(() => ReplayLast());
            var u = _lastSpoken;
            if (u is null || u.Chunks.Count == 0) return false;
            var texts = u.Chunks.Select(c => c.Text).ToList();
            if (u.Chunks.All(c => c.Audio is not null))
            {
                var replay = New(Priority.Normal, "say", immediate: true);
                foreach (var c in u.Chunks)
                    replay.Chunks.Add(new Chunk(c.Text, audio: c.Audio, ready: true));
                replay.Closed = true;
                _pending.Add(replay);
                Kick();
            }
            else
            {
                await Say(string.Join(" ", texts), priority: Priority.Normal, immediate: true);
            }
            return true;
        }

        /// <summary>The client finished playing chunk `idx` of utterance `uttId`.
        ///
        /// An ack for a chunk that was never sent is not an ack: it would make `Done`
        /// true and `WaitFor` report "heard" before the read-back played, defeat the
        /// prefetch gate and drop URGENT text from `_unread`. Only the page can send
        /// this (Origin gate), so a bad one is a buggy tab — dropped and logged.
        /// The bound is `HighSent`, not `Sent`, because preemption and resume roll
        /// `Sent` backwards while an honest ack may be in the air.</summary>
        public async Task Played(int uttId, int idx)
        {
            if (!_loop.IsOnLoop) { await _loop.Run(() => Played(uttId, idx)); return; }
            Utterances.TryGetValue(uttId, out var u);
            var now = _clock();
            if (u is not null && idx > u.HighSent)
            {
                Log.LogWarning("speech: ignoring ack beyond what was sent (utterance {Utt}, chunk {Idx}, sent up to {High})",
                    uttId, idx, u.HighSent);
                return;
            }
            if (u is not null && idx >= 0 && idx < u.Chunks.Count && now < u.Chunks[idx].EarliestAck)
            {
                // Too soon to be true (see AckFloorFactor). Not dropped: the honest
                // client acks a chunk it FAILED TO DECODE at once, and a dropped ack
                // would wedge the prefetch gate. Held, and applied by `Step`.
                u.HeldAck = Math.Max(u.HeldAck, idx);
                u.LastProgressAt = now;                 // the client is alive
                KickLater(u.Chunks[idx].EarliestAck - now);
                return;
            }
            AcceptAck(u, uttId, idx, now);
        }

        public void AcceptAck(Utterance? u, int uttId, int idx, double now)
        {
            if (u is not null && idx > u.Played)
            {
                u.Played = idx;
                u.LastProgressAt = now;
            }
            foreach (var s in _recent)
                if (s.Utt == uttId && s.Idx <= idx && s.AckedAt is null)
                    s.AckedAt = now;
            Kick();
        }

        public void ApplyHeldAcks(double now)
        {
            foreach (var u in new[] { _current, _paused })
            {
                if (u is null || u.HeldAck < 0) continue;
                int idx = u.HeldAck;
                if (idx >= u.Chunks.Count || now >= u.Chunks[idx].EarliestAck)
                {
                    u.HeldAck = -1;
                    AcceptAck(u, u.Id, idx, now);
                }
            }
        }

        /// <summary>The user is talking: stop everything now.</summary>
        public async Task BargeIn(bool keepUnread = true, string reason = "")
        {
            if (!_loop.IsOnLoop) { await _loop.Run(() => BargeIn(keepUnread, reason)); return; }
            var cur = _current;
            // Barging in and resuming are the only things that make him say the
            // same sentence twice; record what cut him off and what was left unsaid.
            if (cur is not null && !cur.Done)
            {
                Log.LogInformation("speech: barge-in on utterance {Id}{Reason}; {N} chunk(s) unplayed",
                    cur.Id, string.IsNullOrEmpty(reason) ? "" : $" ({reason})",
                    Math.Max(0, cur.Chunks.Count - cur.Played - 1));
            }
            cur = _current;
            // Flag first, emit second: a send loop suspended in an await sees the
            // flag before it can emit anything after our `stop`.
            if (cur is not null)
            {
                if (keepUnread && cur.Priority == Priority.Urgent && !cur.Done)
                {
                    var rest = cur.RemainingText();
                    if (rest.Length > 0) _unread.Add(rest);
                }
                cur.Cancelled = true;
                _current = null;
            }
            if (_paused is not null)
            {
                _paused.Cancelled = true;
                _paused = null;
            }
            foreach (var u in _pending)
            {
                if (u.Priority == Priority.Normal) u.Cancelled = true;
                else u.Immediate = false;          // anything that survives waits for a pause
            }
            _pending = _pending.Where(u => !u.Cancelled).ToList();
            SettleInFlight();
            if (_cancelWindowOpen) _cancelEvent.Set();
            await Emit(new Dictionary<string, object?> { ["type"] = "stop" });
            await SetSpeaking(false);
            Kick();
        }

        // ── internals ──────────────────────────────────────────────────

        public void AddChunk(Utterance utt, string text)
        {
            var chunk = new Chunk(text);
            utt.Chunks.Add(chunk);
            Spawn(() => Synthesize(utt, chunk));
        }

        public async Task Synthesize(Utterance utt, Chunk chunk)
        {
            byte[]? audio = null;
            var ct = _tasksCts.Token;
            try
            {
                audio = await _synth(_prepare(chunk.Text)).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // Stop() cancelled us, as task.cancel() did
            }
            catch (Exception e)
            {
                Log.LogError("synth failed: {Error}", e.Message);
            }
            if (utt.Cancelled) return;
            chunk.Audio = audio;
            chunk.Ready = true;
            if (audio is null)
            {
                _ttsFailures++;
                if (_ttsFailures == 3)
                    chunk.Notice = true;              // the send loop warns right after this chunk
            }
            else
            {
                _ttsFailures = 0;
            }
            Kick();
        }

        public bool UserSilent(double now) => _lastUserSpeech is null || now - _lastUserSpeech.Value >= PauseAfter;

        public bool Startable(Utterance u, double now) => !u.Cancelled && (u.Immediate || UserSilent(now));

        /// <summary>Chunks the client will never play (dropped or cancelled) count as heard now.</summary>
        public void Settle(int uttId, int above = -1)
        {
            var now = _clock();
            foreach (var s in _recent)
                if (s.Utt == uttId && s.Idx > above && s.AckedAt is null)
                    s.AckedAt = now;
        }

        /// <summary>This utterance is gone from the scheduler with a chunk still
        /// unacked: give that chunk a deadline of its own.
        ///
        /// The dropped-preemption path settles `above: Played + 1` on purpose — the
        /// kept chunk may still be playing — but its `Spoken` then belongs to an
        /// utterance the ack watchdog never looks at, leaving only the 120s backstop
        /// (two minutes of `Classify` eating the user's words). `AckTimeout` is the
        /// scheduler's own answer to "nobody is going to ack this".</summary>
        public void Orphan(int uttId)
        {
            var deadline = _clock() + AckTimeout;
            foreach (var s in _recent)
                if (s.Utt == uttId && s.AckedAt is null)
                    s.ExpiresAt = s.ExpiresAt is null ? deadline : Math.Min(s.ExpiresAt.Value, deadline);
        }

        /// <summary>Nobody is listening any more: settle everything in flight.
        ///
        /// The scheduler is process-global and outlives any one tab; with no voice
        /// client there is no speaker for an echo to come from, and a reloaded tab
        /// would otherwise inherit the previous tab's unacked entries. Settled as
        /// `BargeIn` settles: acked *now*, echo-relevant for `EchoWindow` only.</summary>
        public void TransportGone()
        {
            if (!_loop.IsOnLoop) { _loop.Invoke(() => TransportGone()); return; }
            SettleInFlight();
            Kick();
        }

        public void SettleInFlight()
        {
            var now = _clock();
            foreach (var s in _recent)
                if (s.AckedAt is null)
                    s.AckedAt = now;
        }

        /// <summary>Could this chunk still be reaching the microphone? Acked (then
        /// `EchoWindow` of room reverb), orphaned (`Orphan`'s deadline decides), or
        /// simply in flight (the backstop).</summary>
        public bool StillRelevant(Spoken s, double now)
        {
            if (s.AckedAt is not null) return now - s.AckedAt.Value < EchoWindow;
            if (s.ExpiresAt is not null && now >= s.ExpiresAt.Value) return false;
            return now - s.SentAt < RecentBackstopSec;
        }

        public HashSet<string> RecentTokens()
        {
            var now = _clock();
            _recent = _recent.Where(s => StillRelevant(s, now)).ToList();
            var output = new HashSet<string>();
            foreach (var s in _recent) output.UnionWith(s.Tokens);
            return output;
        }

        public bool NothingInFlight()
        {
            RecentTokens();                    // prune
            return _recent.All(s => s.AckedAt is not null);
        }

        public async Task SetSpeaking(bool on)
        {
            if (on != _speaking)
            {
                _speaking = on;
                await Emit(new Dictionary<string, object?> { ["type"] = "status", ["state"] = on ? "speaking" : "idle" });
            }
        }

        public void ReleaseGated(double now)
        {
            if (!UserSilent(now) || !_transportReady()) return;
            if (_unread.Count > 0)
            {
                var text = $"{UnreadPrefix} " + string.Join(" ", _unread);
                _unread.Clear();
                var u = New(Priority.Urgent, "say", immediate: true);
                Fill(u, text);
                _pending.Add(u);
            }
            if (_batch.Count > 0 && _current is null
                && now - _lastBatchFlush >= BatchInterval
                && now - (_batchSince is double bs && bs != 0.0 ? bs : now) >= BatchSettle)
            {
                var members = _batch;
                _batch = new List<Utterance>();
                _batchSince = null;
                _lastBatchFlush = now;
                var u = New(Priority.Low, "say", immediate: true);
                Fill(u, string.Join(" ", members.SelectMany(m => m.Chunks).Select(c => c.Text)));
                foreach (var m in members) m.Alias = u;
                _pending.Add(u);
            }
        }

        public Utterance? NextPending(double now, Priority minPriority = Priority.Low)
        {
            var candidates = _pending.Where(u => u.Priority >= minPriority && Startable(u, now)).ToList();
            if (candidates.Count == 0) return null;
            // Stable, like Python's sort: equal keys keep their queue order.
            return candidates.OrderByDescending(u => (int)u.Priority).ThenBy(u => u.Created).First();
        }

        public async Task Pump()
        {
            for (int n = 0; n < 16; n++)
                if (!await Step()) break;
        }

        public async Task<bool> Step()
        {
            var now = _clock();
            ApplyHeldAcks(now);
            ReleaseGated(now);
            _pending = _pending.Where(u => !u.Cancelled).ToList();

            // URGENT preempts a lower-priority speaker at a chunk boundary.
            var cur = _current;
            if (cur is not null && _paused is null && cur.Priority < Priority.Urgent && !cur.Done)
            {
                var urgent = NextPending(now, Priority.Urgent);
                if (urgent is not null)
                {
                    cur.PausedAt = now;
                    cur.Sent = Math.Min(cur.Sent, cur.Played + 1);   // the client keeps only the playing chunk
                    Settle(cur.Id, above: cur.Played + 1);            // the dropped ones will never be acked
                    cur.HeldAck = -1;                                 // ...and neither will a held one for them
                    await Emit(new Dictionary<string, object?> { ["type"] = "drop_queued" });
                    _paused = cur;
                    _pending.Remove(urgent);
                    _current = urgent;
                    return true;
                }
            }

            if (_current is null || _current.Done)
            {
                if (_current is not null && _current.Done)
                {
                    var finished = _current;
                    _current = null;
                    // What "say that again" replays: the last utterance heard in
                    // full, never one interrupted or lost to a dead transport.
                    if (!finished.WasCancelled && !finished.WasAbandoned && finished.Chunks.Count > 0)
                        _lastSpoken = finished;
                }
                if (_paused is not null)
                {
                    // Another URGENT waiting? Speak it before resuming, or the
                    // resumed utterance would be preempted again and bridged twice.
                    var urgent = NextPending(now, Priority.Urgent);
                    if (urgent is not null)
                    {
                        _pending.Remove(urgent);
                        _current = urgent;
                        return true;
                    }
                    var p = _paused;
                    bool stale = now - (p.PausedAt ?? now) > StaleAfter;
                    var kept = p.Sent >= 0 && p.Sent < p.Chunks.Count ? p.Chunks[p.Sent] : null;
                    if (kept is not null && p.Sent > p.Played && !p.Cancelled
                        && !stale && AckFloorSeconds(kept.Audio) > 0
                        && now < kept.EarliestAck + ResumeAckGraceSec)
                    {
                        // The chunk the client kept is still playing, and its ack will
                        // name the index it was SENT under. The bridge shifts every
                        // index above `Played`, so inserting it now would let that ack
                        // land on the bridge. Wait for the ack — at most the audio's
                        // own length plus a grace. A blob with no floor resumes at once.
                        KickLater(kept.EarliestAck + ResumeAckGraceSec - now);
                        return false;
                    }
                    _paused = null;
                    bool spoke = _lastUserSpeech is not null && p.PausedAt is not null
                                 && _lastUserSpeech.Value > p.PausedAt.Value;
                    if (p.Cancelled || stale || spoke || p.Resumes >= MaxResumes)
                    {
                        p.Cancelled = true;
                        Settle(p.Id, above: p.Played + 1);   // the kept chunk may still be playing
                        Orphan(p.Id);                         // ...but not for two minutes
                    }
                    else
                    {
                        InsertBridge(p);
                        _current = p;
                        return true;
                    }
                }
                var nxt = NextPending(now);
                if (nxt is not null)
                {
                    _pending.Remove(nxt);
                    _current = nxt;
                    return true;
                }
                if (_speaking && NothingInFlight())
                    await SetSpeaking(false);
                return false;
            }

            cur = _current;
            if (cur.Sent > cur.Played && cur.LastProgressAt is not null
                && now - cur.LastProgressAt.Value > AckTimeout)
            {
                // No ack for far longer than any chunk takes to play: the client is
                // gone or wedged. Never let that pin the mouth forever.
                Log.LogWarning("speech: no ack for {Timeout:F0}s on utterance {Id}; abandoning", AckTimeout, cur.Id);
                Abandon(cur, keepUnread: true);
                return true;
            }
            return await SendReady(cur, now);
        }

        /// <summary>Resume after a preemption: a bridge phrase goes in front of the first
        /// unplayed chunk. Chunks own their audio, so shifting positions is safe even
        /// while synthesis for later chunks is still running.</summary>
        public void InsertBridge(Utterance u)
        {
            var bridge = new Chunk(Bridges[u.Resumes % Bridges.Length]);
            u.Resumes++;
            Log.LogInformation("speech: resuming utterance {Id} (resume {N} of {Max}) from chunk {From}",
                u.Id, u.Resumes, MaxResumes, u.Played + 1);
            int pos = u.Played + 1;
            u.Chunks.Insert(pos, bridge);
            if (u.HighSent >= pos)
                u.HighSent++;        // every frame that went out at >= pos now sits one higher
            u.Sent = u.Played;
            // A hold taken while paused is an INDEX, and the bridge just moved every
            // index from `pos` up by one: discharged now, it would mark the bridge
            // played before its audio exists. Those chunks are re-sent and re-acked.
            u.HeldAck = -1;
            Spawn(() => Synthesize(u, bridge));
        }

        public async Task<bool> SendReady(Utterance u, double now)
        {
            bool progressed = false;
            while (!u.Cancelled && u.Sent - u.Played < Prefetch)
            {
                int idx = u.Sent + 1;
                if (idx >= u.Chunks.Count) break;
                var chunk = u.Chunks[idx];
                if (!chunk.Ready) break;
                if (chunk.Audio is null)
                {
                    // Nothing to play, so nothing to ack — but it still takes its
                    // TURN. Without a link here the next chunk's chain restarted at
                    // `now`, and one failed synthesis let `WaitFor` say "heard in
                    // full" 22 ms into ten seconds of audio.
                    var prev = idx > 0 ? u.Chunks[idx - 1].EarliestAck : 0.0;
                    chunk.EarliestAck = Math.Max(prev, now);
                    u.Sent = idx;
                    u.HighSent = Math.Max(u.HighSent, idx);
                    if (now >= chunk.EarliestAck)
                    {
                        u.Played = Math.Max(u.Played, idx);
                    }
                    else
                    {
                        u.HeldAck = Math.Max(u.HeldAck, idx);
                        KickLater(chunk.EarliestAck - now);
                    }
                    if (!await Send(new Dictionary<string, object?> { ["type"] = "text", ["text"] = chunk.Text }, u))
                        return false;
                    if (chunk.Notice)
                        await Send(new Dictionary<string, object?> { ["type"] = "text", ["text"] = "My voice is failing, sir." }, u);
                    progressed = true;
                    continue;
                }
                await SetSpeaking(true);
                await _emitLock.WaitAsync();
                try
                {
                    if (u.Cancelled)                 // a barge-in landed while we waited
                        break;
                    var spoken = new Spoken(u.Id, idx, new HashSet<string>(Tokens(chunk.Text)), now);
                    _recent.Add(spoken);
                    // Sequential playback: this chunk cannot end before the one
                    // before it could, plus its own length.
                    var previous = idx > 0 ? u.Chunks[idx - 1].EarliestAck : 0.0;
                    chunk.EarliestAck = Math.Max(previous, now) + AckFloorSeconds(chunk.Audio);
                    u.Sent = idx;
                    u.FirstSentAt ??= now;
                    u.LastProgressAt = now;
                    try
                    {
                        await _emitRaw(new Dictionary<string, object?>
                        {
                            ["type"] = "audio",
                            ["utt"] = u.Id,
                            ["idx"] = idx,
                            ["data"] = Convert.ToBase64String(chunk.Audio),
                            ["text"] = chunk.Text,
                        });
                        u.HighSent = Math.Max(u.HighSent, idx);   // it really did go out
                    }
                    catch (Exception e)
                    {
                        // The transport is broken: nothing we send will play. Drop
                        // this utterance cleanly rather than wedge the mouth.
                        Log.LogError("speech transport failed: {Error}", e.Message);
                        _recent.Remove(spoken);
                        u.Sent = idx - 1;
                        Abandon(u);
                        return false;
                    }
                }
                finally
                {
                    _emitLock.Release();
                }
                progressed = true;
            }
            return progressed;
        }

        /// <summary>Emit a non-audio frame on behalf of `u`; on transport failure abandon `u`.</summary>
        public async Task<bool> Send(Dictionary<string, object?> msg, Utterance u)
        {
            await _emitLock.WaitAsync();
            try
            {
                if (u.Cancelled) return false;
                try
                {
                    await _emitRaw(msg);
                    return true;
                }
                catch (Exception e)
                {
                    Log.LogError("speech transport failed: {Error}", e.Message);
                    Abandon(u);
                    return false;
                }
            }
            finally
            {
                _emitLock.Release();
            }
        }

        /// <summary>The client cannot hear this. An URGENT item is kept as unread so it is
        /// re-raised once someone is listening; everything else is simply lost.</summary>
        public void Abandon(Utterance u, bool keepUnread = true)
        {
            if (keepUnread && u.Priority == Priority.Urgent && !u.Done)
            {
                var rest = u.RemainingText();
                if (rest.Length > 0) _unread.Add(rest);
            }
            u.Cancelled = true;
            u.Abandoned = true;
            Settle(u.Id);
            if (_current == u) _current = null;
            _speaking = false;       // the client never heard the `idle`; do not pretend it did
            Kick();
        }
    }
}
