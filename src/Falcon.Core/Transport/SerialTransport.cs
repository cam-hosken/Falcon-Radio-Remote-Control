using System.Collections.Concurrent;
using System.Text;

namespace Falcon.Core.Transport;

/// <summary>
/// The production line-level transport (plan §2.2): <see cref="ITransport"/>
/// over an <see cref="ISerialPort"/>, with <see cref="LineFramer"/> framing
/// and PROMPT-GATED write flow control (Q7, measured by probes R6/R10: the
/// radio silently swallows commands that arrive while it streams a heavy
/// response — at ANY fixed pace, including the old 125 ms; prompt-gated
/// sending scored 5/5).
///
/// The flow-control rule (protocol.md "Command pacing — RESOLVED"): one
/// command in flight; the next write is released by (a) a prompt line
/// observed since the last write, or (b) the gate timeout (~2 s) — a
/// swallowed command must NEVER latch the gate closed; on timeout the next
/// command is sent anyway and the sentinel/read-back layer catches the
/// swallow. An async event's prompt can release the gate early — rare,
/// harmless.
///
/// SendLine is NON-BLOCKING: it enqueues and returns immediately, never
/// sends inline. A single writer worker thread drains the queue. Core
/// legally sends from receive handlers — the ping queue dispatches the next
/// BAT ST from the BATTERY line handler while holding its ping lock — so a
/// blocking SendLine stalls the read path and ping processing; the Stage 1
/// smoke measured exactly that stall (docs/probes.md "Stage 1 bench smoke",
/// finding 3).
///
/// 400 ms open settle before the FIRST write (commands written within
/// ~100 ms of open are swallowed — bench 2026-08-01); reads start
/// immediately. Teardown exceptions are not transport errors (_closing
/// discipline). Byte-level disconnects surface as <see cref="TransportError"/>.
/// Auto-reconnect deliberately does NOT live here — it is session-layer
/// work (Stage 3).
/// </summary>
public sealed class SerialTransport : ITransport, IDisposable
{
    private readonly ISerialPort _port;
    private readonly LineFramer _framer = new();   // touched only on the port read thread (+ Open/Close reset)
    private readonly object _stateLock = new();

    /// <summary>Write gate. Set = clear to send (a prompt has been observed
    /// since the last write, or nothing has been written yet).</summary>
    private readonly ManualResetEventSlim _promptSeen = new(true);
    private long _gateDeadline;                    // TickCount64 after which a closed gate no longer holds
    private long _openedAt;                        // TickCount64 at open, for the settle

    private volatile BlockingCollection<(long Session, long Sequence, string Command)>? _queue;
    private Thread? _writer;
    private CancellationTokenSource? _writerCts;
    private volatile bool _closing;
    private string? _portName;

    /// <summary>Assigns the write SEQUENCE and enqueues under the same lock,
    /// so the numbers a caller gets back are in the order the writer will
    /// drain them. Numbering without the lock would let two sender threads
    /// interleave (A takes 1, B takes 2, B enqueues first) and the
    /// <see cref="LineWritten"/> stream would no longer be increasing.</summary>
    private readonly object _sequenceLock = new();
    private long _sequence;
    private long _session;

    /// <summary>Delay between port open and the FIRST write (bench-measured:
    /// commands written within ~100 ms of open are swallowed). Reads start
    /// immediately.</summary>
    public int OpenSettleMs { get; set; } = 400;

    /// <summary>Gate timeout: how long a missing prompt can hold the next
    /// write back (protocol.md "The rule" — then send anyway; the
    /// sentinel/read-back layer catches a genuinely swallowed command).</summary>
    public int GateTimeoutMs { get; set; } = 2_000;

    public bool IsOpen => _port.IsOpen;
    public string? PortName => _portName;
    public long Session { get { lock (_sequenceLock) return _session; } }

    public event EventHandler<LineReceivedEventArgs>? LineReceived;
    public event EventHandler<TransportErrorEventArgs>? TransportError;
    public event EventHandler<LineWrittenEventArgs>? WriteStarted;
    public event EventHandler<LineWrittenEventArgs>? LineWritten;

    /// <summary>Takes ownership of <paramref name="port"/>: Dispose disposes it.</summary>
    public SerialTransport(ISerialPort port)
    {
        _port = port;
        _port.DataReceived += OnDataReceived;
        _port.Disconnected += OnDisconnected;
    }

    public void Open(PortSettings settings)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen) throw new InvalidOperationException("Transport is already open.");

            CleanUpWriterLocked();      // leftovers from a disconnect-ended session

            _framer.Reset();
            _promptSeen.Set();          // clear-to-send: nothing in flight yet
            _gateDeadline = 0;
            _closing = false;

            _port.OpenAsync(settings).GetAwaiter().GetResult();
            _portName = settings.PortName;
            _openedAt = Environment.TickCount64;

            // A new session is a fresh write stream (the ITransport
            // contract: monotonic from 1 per open session) under a NEW SESSION
            // NUMBER — that restart is exactly what makes a sequence alone
            // ambiguous across a reconnect, so the number that disambiguates it
            // has to move here (audit round 2: it never did, so every event
            // carried session 0 and the aliasing was live in production while
            // the fake's pin passed).
            lock (_sequenceLock) { _sequence = 0; _session++; }

            var queue = new BlockingCollection<(long Session, long Sequence, string Command)>();
            var cts = new CancellationTokenSource();
            _queue = queue;
            _writerCts = cts;
            _writer = new Thread(() => WriterLoop(queue, cts.Token))
            {
                IsBackground = true,
                Name = "falcon-serial-writer",
            };
            _writer.Start();
        }
    }

    /// <summary>
    /// TRANSPORT-LEVEL BACKSTOP on the port's close (round 13 D2, repair 2).
    /// Every <see cref="ISerialPort"/> is supposed to bound its own teardown;
    /// this bounds the ones that don't, whatever the platform, so a wedged
    /// driver can never hang <see cref="Close"/> — and through it the session
    /// teardown and the UI thread that may be driving it.
    ///
    /// <para><b>Why 3000 and not 2000.</b> It has to sit ABOVE the worst case
    /// of the port implementations underneath it, or it would abandon closes
    /// that were about to succeed. Android's bounded close is two sequential
    /// 1000 ms phases (cancel+join, then driver close) = 2000 ms worst case,
    /// so 3000 leaves 1000 ms of scheduling slack: under normal overhead the
    /// port finishes and this never fires. Worst-case teardown is therefore
    /// 3000 here + 2000 for the writer join (skipped on a self-join) = the
    /// 5 s device gate.</para>
    /// </summary>
    public const int PortCloseTimeoutMs = 3_000;

    /// <summary>How long <see cref="Close"/> waits for the writer worker to
    /// exit. NEVER paid when Close is called ON the writer thread — see the
    /// self-join guard.</summary>
    public const int WriterJoinTimeoutMs = 2_000;

    public void Close()
    {
        Thread? writer;
        lock (_stateLock)
        {
            _closing = true;
            _writerCts?.Cancel();
            _queue?.CompleteAdding();   // queued-but-unsent commands are dropped: the session is over
            writer = _writer;
            _writer = null;
        }

        // Teardown exceptions are not transport errors (_closing discipline).
        ClosePortBounded();

        // WRITER SELF-JOIN GUARD (round 13 D2, repair 2a). Close legitimately
        // runs ON the writer thread: a write fault raises TransportError from
        // WriterLoop, the session's handler tears down synchronously, and the
        // call lands back here on the very thread being joined. Joining
        // yourself cannot succeed — it just burns the full timeout and then
        // gives up — so on that path the join is 2 s of dead wait added to a
        // teardown the operator is watching. The thread is exiting anyway:
        // its loop has already thrown out of GetConsumingEnumerable.
        if (writer is not null && !ReferenceEquals(writer, Thread.CurrentThread))
            writer.Join(WriterJoinTimeoutMs);

        lock (_stateLock)
        {
            CleanUpWriterLocked();
            _framer.Reset();
        }
    }

    /// <summary>Close the port, but never wait longer than
    /// <see cref="PortCloseTimeoutMs"/>. On timeout the close is ABANDONED,
    /// not cancelled: the port keeps running it, and this returns so teardown
    /// can finish. Best-effort is the right contract — by the time a close
    /// wedges, the hardware is usually already gone.</summary>
    private void ClosePortBounded()
    {
        Task closeTask;
        try { closeTask = _port.CloseAsync(); }
        catch { return; }               // threw synchronously — already down

        // An abandoned close's failure must not resurface as an
        // UnobservedTaskException on the finalizer thread.
        _ = closeTask.ContinueWith(static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var finished = Task.WhenAny(closeTask, Task.Delay(PortCloseTimeoutMs))
                .GetAwaiter().GetResult();
            if (!ReferenceEquals(finished, closeTask)) return;   // abandoned, deliberately
            closeTask.GetAwaiter().GetResult();                  // observe a fast failure
        }
        catch { /* tearing down */ }
    }

    /// <summary>Queue a command (CR appended by the writer) and return
    /// IMMEDIATELY — never blocks, never sends inline. Callers on receive
    /// handlers are the normal case. Dropped silently if the transport is
    /// closed or closing, which is the 0 return.</summary>
    public long SendLine(string command)
    {
        var queue = _queue;
        if (queue is null || _closing) return 0;
        lock (_sequenceLock)
        {
            long sequence = _sequence + 1;
            try
            {
                if (!queue.TryAdd((_session, sequence, command))) return 0;
            }
            catch (InvalidOperationException) { return 0; }   // CompleteAdding or Dispose raced us — closing
            _sequence = sequence;
            return sequence;
        }
    }

    // ---- Writer worker ----------------------------------------------------

    private void WriterLoop(BlockingCollection<(long Session, long Sequence, string Command)> queue, CancellationToken ct)
    {
        try
        {
            foreach (var (session, sequence, command) in queue.GetConsumingEnumerable(ct))
            {
                // Open settle: no write earlier than OpenSettleMs after open.
                long settleLeft = _openedAt + OpenSettleMs - Environment.TickCount64;
                if (settleLeft > 0 && ct.WaitHandle.WaitOne((int)settleLeft))
                    break;      // cancelled during the settle

                // Prompt gate: wait for a prompt observed since the last
                // write — but only until the gate deadline. Past the
                // deadline the previous command counts as swallowed and the
                // gate MUST NOT latch: send anyway.
                if (!_promptSeen.IsSet)
                {
                    long gateLeft = Interlocked.Read(ref _gateDeadline) - Environment.TickCount64;
                    if (gateLeft > 0) _promptSeen.Wait((int)gateLeft, ct);
                }

                _promptSeen.Reset();
                Interlocked.Exchange(ref _gateDeadline, Environment.TickCount64 + GateTimeoutMs);
                var bytes = Encoding.ASCII.GetBytes(command + "\r");

                // TWO STAGES, because "being asked" and "written" are
                // different facts and consumers need both (audit round 2).
                //
                // (i) The line has left the queue for the wire. An answer can
                //     arrive from here on — the far side may be faster than
                //     this thread's next instruction — so anything deciding
                //     whether an answer CAN belong to a command keys off this.
                WriteStarted?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));

                _port.WriteAsync(bytes, ct).GetAwaiter().GetResult();

                // (ii) The port ACCEPTED the bytes. Only now may a clock start
                //      against the command: a Windows write is allowed to
                //      block for up to 2 000 ms, and a sentinel budget of
                //      1 500 ms must not be spent on it. A write that threw
                //      never reaches this line, so no clock is ever armed for
                //      a line that did not go out.
                LineWritten?.Invoke(this, new LineWrittenEventArgs(session, sequence, command));
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (ObjectDisposedException) { /* teardown race */ }
        catch (Exception ex)
        {
            if (!_closing) RaiseError(ex);
        }
    }

    // ---- Receive path (port read thread) -----------------------------------

    private void OnDataReceived(object? sender, SerialDataEventArgs e)
    {
        foreach (var line in _framer.Feed(e.Data, e.Data.Length))
        {
            // Exact-prompt rule (matches LineFramer's terminator rule): only
            // a bare mode prompt releases the gate. Released BEFORE the line
            // is surfaced, so a consumer that reacts to the prompt by sending
            // finds the gate already open.
            var trimmed = line.Trim();
            if (trimmed is "SSB>" or "ALE>" or "HOP>")
                _promptSeen.Set();

            LineReceived?.Invoke(this, new LineReceivedEventArgs(line));
        }
    }

    private void OnDisconnected(object? sender, SerialDisconnectedEventArgs e)
    {
        if (_closing) return;
        lock (_stateLock)
        {
            // Stop the writer quietly — its pending/queued writes can only
            // fault against a dead port and would double-report.
            _writerCts?.Cancel();
            _queue?.CompleteAdding();
        }
        RaiseError(e.Reason);
    }

    private void RaiseError(Exception ex)
        => TransportError?.Invoke(this, new TransportErrorEventArgs(ex));

    // ---- Cleanup ------------------------------------------------------------

    /// <summary>Caller holds _stateLock; the writer thread must already have
    /// exited (or been cancelled — it never touches a disposed queue because
    /// GetConsumingEnumerable observes the cancellation first).</summary>
    private void CleanUpWriterLocked()
    {
        _writerCts?.Cancel();
        _writerCts?.Dispose();
        _writerCts = null;
        var queue = _queue;
        _queue = null;
        if (queue is not null)
        {
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            queue.Dispose();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _port.DataReceived -= OnDataReceived;
        _port.Disconnected -= OnDisconnected;
        _port.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _promptSeen.Dispose();
    }
}
