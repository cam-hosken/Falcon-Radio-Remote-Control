using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// Line-injecting test transport (same doctrine as Falcon.Core.Tests': a
/// test transport only REPLAYS verbatim captured lines — it never answers a
/// command). Adds open/close bookkeeping the session tests assert on.
/// </summary>
public sealed class InjectingTransport : ITransport
{
    private readonly object _sentLock = new();
    private readonly List<string> _sent = [];

    public bool IsOpen { get; private set; }
    public string? PortName { get; private set; }
    public int OpenCount { get; private set; }
    public int CloseCount { get; private set; }
    public PortSettings? LastSettings { get; private set; }

    /// <summary>When set, the next Open throws (missing COM port).</summary>
    public Exception? ThrowOnOpen { get; set; }

    /// <summary>ROUND 14 G (audit round 2): when set, <see cref="Open"/> BLOCKS
    /// on it. Opt-in and null by default, so no existing test changes.
    ///
    /// <para>A real port open takes real time — it is the slowest thing the
    /// connect gesture does — but this fake returns instantly, which makes
    /// "before the attempt" and "after the attempt" indistinguishable to any
    /// test that simply awaits the command. Holding the open open is the only
    /// way to assert that something happened WHILE it was still
    /// running.</para></summary>
    public ManualResetEventSlim? OpenGate { get; set; }

    private int _openAttempts;

    /// <summary>Opens ATTEMPTED, counted before <see cref="OpenGate"/> is
    /// waited on — so a test can tell "the attempt has reached the transport
    /// and is stuck there" from "the attempt has not started yet".
    /// <see cref="OpenCount"/> counts opens that COMPLETED.</summary>
    public int OpenAttempts => Volatile.Read(ref _openAttempts);

    public event EventHandler<LineReceivedEventArgs>? LineReceived;
    public event EventHandler<TransportErrorEventArgs>? TransportError;
    public event EventHandler<LineWrittenEventArgs>? WriteStarted;
    public event EventHandler<LineWrittenEventArgs>? LineWritten;

    private long _sequence;
    private long _session;

    /// <summary>The transport's open session (round 15 A0): a fresh write
    /// stream under a new number on every Open, as production does it.</summary>
    public long Session { get { lock (_sentLock) return _session; } }

    public IReadOnlyList<string> SentLines
    {
        get { lock (_sentLock) return [.. _sent]; }
    }

    public int CountSent(string command)
    {
        lock (_sentLock)
        {
            int n = 0;
            foreach (var line in _sent) if (line == command) n++;
            return n;
        }
    }

    public void ClearSent() { lock (_sentLock) _sent.Clear(); }

    public void Open(PortSettings settings)
    {
        Interlocked.Increment(ref _openAttempts);
        OpenGate?.Wait();
        if (ThrowOnOpen is not null) throw ThrowOnOpen;
        OpenCount++;
        LastSettings = settings;
        PortName = settings.PortName;
        IsOpen = true;
        lock (_sentLock) { _sequence = 0; _session++; }
    }

    public void Close()
    {
        CloseCount++;
        IsOpen = false;
    }

    /// <summary>Enqueue IS the write here (round 15 A0): the sequence is
    /// assigned and <see cref="LineWritten"/> raised SYNCHRONOUSLY, so every
    /// existing session/ping test stays byte-identical across the seam.</summary>
    public long SendLine(string command)
    {
        long sequence, session;
        lock (_sentLock)
        {
            sequence = ++_sequence;
            session = _session;
            _sent.Add(command);
        }
        // Enqueue IS the write here: both stages, in order.
        WriteStarted?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));
        LineWritten?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));
        return sequence;
    }

    /// <summary>Inject one captured line, on the calling thread.</summary>
    public void InjectLine(string line) => LineReceived?.Invoke(this, new LineReceivedEventArgs(line));

    /// <summary>
    /// A byte-level transport error, in the REAL SEAM ORDERING (round 13 D2,
    /// repair 4): <see cref="IsOpen"/> flips false BEFORE the event, exactly
    /// as <c>ISerialPort</c> requires of every port implementation and as
    /// <c>FakeSerialPort.InjectDisconnect</c> already did.
    ///
    /// <para><b>Why the old ordering hid a production bug.</b> Leaving
    /// <c>IsOpen</c> true meant every yank test drove
    /// <c>RadioSession.OnTransportError</c> down its <c>_radio.Disconnect()</c>
    /// branch — the branch a real cable pull NEVER takes, because a real port
    /// is already closed by then. The suite therefore could not see that the
    /// production branch left Core in <c>Ready</c>. With the ordering fixed,
    /// these tests exercise the branch the device does.</para>
    /// </summary>
    public void InjectError(Exception ex)
    {
        IsOpen = false;
        TransportError?.Invoke(this, new TransportErrorEventArgs(ex));
    }
}

/// <summary>Runs posted callbacks inline so tests assert synchronously
/// after injecting a line (same rationale as Falcon.Core.Tests).</summary>
public sealed class InlineContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);
}

/// <summary>Manual clock for the toast rate-limit tests and the HOP time-set
/// test. LocalTimeZone is pinned to UTC so GetLocalNow() is deterministic on
/// any machine (the time-set test asserts exact zero-padded wire strings).</summary>
public sealed class TestTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Port-enumeration-only fake for ConnectionSettingsViewModel
/// (the VM uses ISerialPort solely for the two enumeration calls).
///
/// <para>CLONE ROUND 12 §6 F4: the seam now has TWO listing paths and the
/// whole point of the split is WHICH ONE a caller takes — the gesture path
/// may raise Android's USB permission dialog, the passive one may not. So
/// this double COUNTS each path separately (<see cref="GestureCalls"/> /
/// <see cref="PassiveCalls"/>) and can be told to raise
/// <see cref="PermissionRequests"/> on the gesture path only, which is what
/// makes "a timer tick never requests permission" an assertion about
/// behaviour rather than about a method name.</para></summary>
public sealed class FakePortEnumerator : ISerialPort
{
    /// <summary>What BOTH paths report unless <see cref="PassivePorts"/> is
    /// set. Assigning it clears any passive-only override.</summary>
    public IReadOnlyList<string> Ports { get; set; } = [];

    /// <summary>When set, what the PASSIVE path reports (the gesture path
    /// keeps <see cref="Ports"/>) — for the Android shape where an
    /// unpermissioned device lists less specifically without a grant.</summary>
    public IReadOnlyList<string>? PassivePorts { get; set; }

    public int GestureCalls { get; private set; }
    public int PassiveCalls { get; private set; }

    /// <summary>Stands in for Android's permission dialog: incremented by the
    /// GESTURE path only, exactly as AndroidUsbSerialPort requests permission
    /// only there.</summary>
    public int PermissionRequests { get; private set; }

    /// <summary>When set, the NEXT call on either path throws it.</summary>
    public Exception? ThrowOnEnumerate { get; set; }

    /// <summary>When set, the PASSIVE path returns this task instead of
    /// completing — the only way to hold a scan open long enough to prove the
    /// poll is single-flight.</summary>
    public TaskCompletionSource<IReadOnlyList<string>>? PassiveGate { get; set; }

    public event EventHandler<SerialDataEventArgs>? DataReceived { add { } remove { } }
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public bool IsOpen => false;

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        GestureCalls++;
        PermissionRequests++;
        return Throw() ?? Task.FromResult(Ports);
    }

    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync()
    {
        PassiveCalls++;
        return Throw() ?? PassiveGate?.Task ?? Task.FromResult(PassivePorts ?? Ports);
    }

    private Task<IReadOnlyList<string>>? Throw()
    {
        var ex = ThrowOnEnumerate;
        if (ex is null) return null;
        ThrowOnEnumerate = null;
        return Task.FromException<IReadOnlyList<string>>(ex);
    }

    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Enumeration-only fake.");
    public Task CloseAsync() => Task.CompletedTask;
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Enumeration-only fake.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>ROUND 14 G — the <see cref="ISettingsStore"/> double: a dictionary
/// with the real store's two contract rules baked in, so a test cannot pass
/// against a fake that is kinder than the platform.
///
/// <para>Rule one: an absent key and an empty value both read back as NULL.
/// Rule two: storing null or empty FORGETS the key. <see cref="Writes"/>
/// counts every <see cref="Set"/>, which is what lets a test say "the pick
/// wrote the store" rather than "the store happens to contain the right
/// value" — the seed makes those two indistinguishable otherwise.</para>
///
/// <para><see cref="Seed"/> is the LAUNCH: it fills the store the way a
/// previous process left it, without counting as a write.</para></summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>How many times <see cref="Set"/> was called, forgets included.</summary>
    public int Writes { get; private set; }

    /// <summary>What a previous process left behind. Not a write.</summary>
    public FakeSettingsStore Seed(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string? value)
    {
        Writes++;
        if (string.IsNullOrEmpty(value)) _values.Remove(key);
        else _values[key] = value;
    }
}

/// <summary>
/// The §5 confirmation seam's test double (UI tweaks round 10) — CONTROLLABLE,
/// not fire-and-forget.
///
/// <para><b>Why controllable.</b> The lifecycle contract the consumers must
/// obey is about what happens WHILE a prompt is open: the session drops, the
/// mode confirmation is lost, the picker moves, the prompt task faults. A fake
/// that answered immediately could not express any of that. So every call
/// returns a task that stays PENDING until the test drives it through the
/// <see cref="PendingPrompt"/> handle — <see cref="PendingPrompt.Complete"/>,
/// <see cref="PendingPrompt.Fault"/> or <see cref="PendingPrompt.Cancel"/> —
/// with <see cref="EnqueueAnswer"/> as the shorthand for the simple
/// answer-at-once cases.</para>
///
/// <para>Every call is recorded verbatim (title / message / accept / cancel)
/// so the §5 prompt table can be asserted as STRINGS, not as "a prompt
/// happened".</para>
/// </summary>
public sealed class FakeConfirmationPrompt : IConfirmationPrompt
{
    private readonly object _lock = new();
    private readonly List<PendingPrompt> _prompts = [];
    private readonly Queue<bool> _queuedAnswers = new();

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<PendingPrompt> Prompts
    {
        get { lock (_lock) return [.. _prompts]; }
    }

    public int CallCount
    {
        get { lock (_lock) return _prompts.Count; }
    }

    /// <summary>The most recent call — the usual assertion target.</summary>
    public PendingPrompt Last
    {
        get
        {
            lock (_lock)
            {
                if (_prompts.Count == 0)
                    throw new InvalidOperationException("no confirmation prompt has been raised");
                return _prompts[^1];
            }
        }
    }

    /// <summary>Answer the NEXT call immediately (FIFO). For the cases that do
    /// not need a prompt held open; queued answers are consumed one per call
    /// and a call with none queued stays pending.</summary>
    public void EnqueueAnswer(bool answer)
    {
        lock (_lock) _queuedAnswers.Enqueue(answer);
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        PendingPrompt prompt;
        bool answerNow;
        bool answer;
        lock (_lock)
        {
            prompt = new PendingPrompt(title, message, accept, cancel);
            _prompts.Add(prompt);
            answerNow = _queuedAnswers.TryDequeue(out answer);
        }
        // Completed OUTSIDE the lock: the continuation runs on this thread
        // (inline marshalling), and it must not re-enter the fake holding it.
        if (answerNow) prompt.Complete(answer);
        return prompt.Task;
    }
}

/// <summary>One recorded confirmation call, plus the handle that decides how
/// its task ends. Answering twice throws — a consumer that re-answers a
/// prompt it already resolved is a defect, not a no-op.
///
/// <para>The recorded button words are <c>AcceptText</c>/<c>CancelText</c>
/// rather than Accept/Cancel: the §5 handle's own verbs are
/// <see cref="Complete"/>/<see cref="Fault"/>/<see cref="Cancel"/>, and a
/// <c>Cancel</c> property cannot coexist with the <c>Cancel()</c> the plan
/// names. The METHOD keeps the plan's word.</para></summary>
public sealed class PendingPrompt(string title, string message, string accept, string cancel)
{
    private readonly TaskCompletionSource<bool> _source = new();

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string AcceptText { get; } = accept;
    public string CancelText { get; } = cancel;

    public Task<bool> Task => _source.Task;

    /// <summary>True once the task has any outcome (answered, faulted or
    /// cancelled) — "still open" is <c>!IsResolved</c>.</summary>
    public bool IsResolved => _source.Task.IsCompleted;

    /// <summary>The operator answered: true = accept, false = cancel.</summary>
    public void Complete(bool answer) => _source.SetResult(answer);

    /// <summary>The prompt failed (a platform alert that threw). Consumers
    /// must send nothing and must not wedge.
    ///
    /// <para>PARAMETERLESS, exactly as §5 names it. The exception TYPE is the
    /// fake's business, not the caller's: what the lifecycle contract asks of
    /// a consumer is "a faulted prompt task sends nothing", which is true of
    /// any exception — letting each test choose one would invite pins that
    /// depend on the particular type and drift away from the contract.</para>
    /// </summary>
    public void Fault()
        => _source.SetException(new InvalidOperationException("confirmation prompt failed"));

    /// <summary>The prompt was cancelled out from under the caller (page
    /// teardown). Same requirement as <see cref="Fault"/>.</summary>
    public void Cancel() => _source.SetCanceled();
}

/// <summary>
/// Shared stack: REAL Prc138Radio + RadioSession over the injecting
/// transport, inline marshalling, surfaces on top — exactly the app's
/// wiring minus the platform port.
/// </summary>
public abstract class SessionTestBase : IDisposable
{
    protected readonly InjectingTransport Transport = new();
    protected readonly Prc138Radio Radio;
    protected readonly RadioSession Session;

    protected SessionTestBase()
    {
        var context = new InlineContext();
        Radio = new Prc138Radio(Transport, context);
        Session = new RadioSession(Radio, Transport, context)
        {
            // Tests drive ticks deterministically via ReconnectTick();
            // park the real timer out of the way.
            ReconnectIntervalMs = 3_600_000,
        };
    }

    protected static PortSettings TestSettings => new() { PortName = "COM7", BaudRate = 9600 };

    /// <summary>Verbatim BATTERY answer (R1 capture) — completes the sentinel.</summary>
    protected void AnswerSentinel() => Transport.InjectLine("Battery Status FULL 31.4V");

    /// <summary>Session connect driven to Ready (both ritual sentinels drained).</summary>
    protected void ConnectReady()
    {
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Transport.ClearSent();
    }

    /// <summary>Poll until <paramref name="condition"/> or timeout — for the
    /// radio's real timers (init watchdog), which fire on timer threads.</summary>
    protected static bool WaitUntil(Func<bool> condition, int timeoutMs = 3_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    public void Dispose()
    {
        Session.Dispose();
        Radio.Dispose();
        GC.SuppressFinalize(this);
    }
}
