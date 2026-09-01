using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// Line-injecting test transport. Doctrine (plan §5, docs/tests.md): a test
/// transport may only REPLAY/INJECT verbatim captured lines — it never
/// answers a command. There is no scripted command→response double anywhere
/// in this suite; tests inject captured lines explicitly, from whatever
/// thread the test chooses, which is exactly what makes the Q3 race tests
/// meaningful (they exercise OUR locking, not a pretend radio).
/// </summary>
public sealed class InjectingTransport : ITransport
{
    private readonly object _sentLock = new();
    private readonly List<string> _sent = [];

    public bool IsOpen { get; private set; }
    public string? PortName { get; private set; }

    public event EventHandler<LineReceivedEventArgs>? LineReceived;
    public event EventHandler<TransportErrorEventArgs>? TransportError;
    public event EventHandler<LineWrittenEventArgs>? WriteStarted;
    public event EventHandler<LineWrittenEventArgs>? LineWritten;

    private long _sequence;
    private long _session;
    private readonly List<(long Session, long Sequence, string Command)> _held = [];

    /// <summary>The transport's open session (round 15 A0, audit round 1).
    /// PRODUCTION RESTARTS ITS SEQUENCE ON EVERY OPEN, so a fake that numbered
    /// straight through could not reproduce the number REUSE that makes a
    /// sequence alone ambiguous — and the reconnect race would be untestable
    /// here.</summary>
    public long Session { get { lock (_sentLock) return _session; } }

    /// <summary>
    /// ROUND 15 A0 (§13.4 gates (1)–(3)): when set and NOT signalled, a line
    /// is still enqueued and counted, but its <see cref="LineWritten"/> is
    /// HELD until <see cref="ReleaseWrites"/> — the fake's stand-in for the
    /// production transport's prompt gate holding a command back behind the
    /// answers ahead of it. Opt-in and NULL by default, so every existing
    /// test keeps the "enqueue IS the write" behaviour that makes it
    /// byte-identical (critic F29).
    ///
    /// <para>It defers rather than BLOCKS on purpose: <c>SendLine</c> is
    /// contractually non-blocking (Core sends from receive handlers while
    /// holding the ping lock), so a fake that blocked inside it would model
    /// a transport that cannot exist.</para>
    /// </summary>
    public ManualResetEventSlim? WriteGate { get; set; }

    /// <summary>Let every held line reach "the wire", in write order. Call it
    /// from a <c>finally</c>: a test that asserts between the hold and the
    /// release must not leave the radio waiting on a clock that never
    /// starts.</summary>
    public void ReleaseWrites()
    {
        (long Session, long Sequence, string Command)[] held;
        lock (_sentLock)
        {
            held = [.. _held];
            _held.Clear();
        }
        WriteGate?.Set();
        foreach (var (session, sequence, command) in held) Report(session, sequence, command);
    }

    /// <summary>Let exactly the NEXT held line reach the wire, with the gate
    /// still shut behind it — the only way to put one command on the wire and
    /// another still in the queue, which is what separates "this line started
    /// the clock" from "the line ahead of it did".</summary>
    public void ReleaseOneWrite()
    {
        (long Session, long Sequence, string Command) next;
        lock (_sentLock)
        {
            if (_held.Count == 0) return;
            next = _held[0];
            _held.RemoveAt(0);
        }
        Report(next.Session, next.Sequence, next.Command);
    }

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
        PortName = settings.PortName;
        IsOpen = true;
        // Per the ITransport contract: a fresh write stream, under a NEW
        // session number.
        lock (_sentLock) { _sequence = 0; _session++; }
    }

    public void Close() => IsOpen = false;

    /// <summary>Enqueue IS the write here (unless <see cref="WriteGate"/>
    /// holds it): the sequence is assigned and <see cref="LineWritten"/> is
    /// raised SYNCHRONOUSLY, which is what keeps every existing ping/queue
    /// test byte-identical across the round-15 A0 seam.</summary>
    public long SendLine(string command)
    {
        long sequence, session;
        bool held;
        lock (_sentLock)
        {
            sequence = ++_sequence;
            session = _session;
            _sent.Add(command);
            held = WriteGate is { IsSet: false };
            if (held) _held.Add((session, sequence, command));
        }
        if (!held) Report(session, sequence, command);
        return sequence;
    }

    /// <summary>Both stages, in order: this fake's enqueue IS its write, so
    /// the line starts and completes in the same breath.</summary>
    private void Report(long session, long sequence, string command)
    {
        WriteStarted?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));
        LineWritten?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));
    }

    /// <summary>Report a line as WRITTEN under an arbitrary (session,
    /// sequence) — the only way to stage a report that OUTLIVED the session
    /// that produced it, which is what the reconnect race is made of.</summary>
    public void InjectLineWritten(long session, long sequence, string command)
        => Report(session, sequence, command);

    /// <summary>Inject one captured line, on the calling thread.</summary>
    public void InjectLine(string line) => LineReceived?.Invoke(this, new LineReceivedEventArgs(line));

    public void InjectError(Exception ex) => TransportError?.Invoke(this, new TransportErrorEventArgs(ex));
}

/// <summary>
/// Runs posted callbacks inline, on the calling thread. Tests assert
/// synchronously after injecting a line, which requires synchronous
/// marshalling — passing this explicitly makes that a stated requirement
/// rather than an accident of SynchronizationContext.Current being null on
/// the xUnit worker thread.
/// </summary>
public sealed class InlineContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);
}
