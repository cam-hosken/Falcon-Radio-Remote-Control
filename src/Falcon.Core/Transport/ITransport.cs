namespace Falcon.Core.Transport;

/// <summary>
/// A line-oriented link to the radio (plan §2.2). Implementations own framing
/// (CR / LF / '>' terminators) and write flow control (prompt-gated — Q7);
/// consumers only ever see whole response lines. The production
/// SerialTransport over ISerialPort is Stage 2; Stage 1 consumers are the
/// unit-test line injector and the bench-smoke minimal serial transport.
/// </summary>
public interface ITransport
{
    bool IsOpen { get; }
    string? PortName { get; }

    /// <summary>Raised once per complete response line (terminators stripped, '>' kept).
    /// May be raised on a transport read thread — Core marshals.</summary>
    event EventHandler<LineReceivedEventArgs>? LineReceived;

    /// <summary>Transport faults, including byte-level disconnects. After a
    /// TransportError raised from the write path the transport's TX side may
    /// be permanently dead until Close/Open — the session layer (Stage 3)
    /// must treat TransportError as connection-fatal, not advisory.</summary>
    event EventHandler<TransportErrorEventArgs>? TransportError;

    void Open(PortSettings settings);
    void Close();

    /// <summary>
    /// The OPEN SESSION this transport is on: 0 before the first
    /// <see cref="Open"/>, and one higher on every open after that.
    ///
    /// <para>It exists because <see cref="SendLine"/>'s sequence restarts at 1
    /// on every open, so a sequence ALONE is not an identity: a line written
    /// by a session that is going away can be reported while the next session
    /// has already issued a line with the same number. Anything correlating a
    /// write with its report must compare the PAIR (session, sequence) — the
    /// pair is unique for the life of the transport.</para>
    /// </summary>
    long Session { get; }

    /// <summary>
    /// Raised once per line when it LEAVES THE QUEUE FOR THE WIRE — on the
    /// writer thread, in write order, immediately BEFORE the bytes are handed
    /// to the port. It means "this command is being asked NOW", nothing more:
    /// the port has not accepted it yet, and it may still fail.
    ///
    /// <para>It exists because the two facts are genuinely different and both
    /// are needed (audit round 2). A consumer deciding whether an answer can
    /// possibly belong to a command needs THIS one — the bytes can be on the
    /// wire, and the far side can answer, before the writer thread gets to
    /// report completion (an in-process radio wins that race every time). A
    /// consumer running a CLOCK against the command needs the other one, or it
    /// times a command the port has not accepted yet.</para>
    ///
    /// <para><b>A subscriber must not block</b>: on the production transport
    /// this is the writer worker, so a wait here stalls every later
    /// write.</para>
    /// </summary>
    event EventHandler<LineWrittenEventArgs>? WriteStarted;

    /// <summary>
    /// Raised once per line ACTUALLY WRITTEN — after the port has ACCEPTED the
    /// bytes, on the writer thread, in write order (plan-round15.md §13.4 H3
    /// / gate (4)). A line whose write threw is never reported here.
    ///
    /// <para>This is the instant a clock against the command may start: the
    /// gap A0 closes is the seconds a line can sit enqueued behind the prompt
    /// gate, and a port write that BLOCKS (the Windows port allows up to
    /// 2 000 ms) must not be counted against a sentinel's budget either.</para>
    ///
    /// <para><b>A subscriber must not block</b>: on the production transport
    /// this is the writer worker, so a wait here stalls every later
    /// write.</para>
    ///
    /// <para>The gap this closes: <see cref="SendLine"/> only ENQUEUES, and
    /// the prompt gate can hold a line back for seconds. Anything that runs a
    /// clock against a command it sent — the Q3 sentinel queue — has to start
    /// that clock from the WIRE, or it times out a command the radio has not
    /// been asked yet (bench-measured: P8, the first <c>BAT ST</c> of a
    /// connect is writable only 1 690 ms after the burst begins).</para>
    /// </summary>
    event EventHandler<LineWrittenEventArgs>? LineWritten;

    /// <summary>Queue a command; the transport appends the CR and applies
    /// prompt-gated flow control. Returns the line's SEQUENCE — monotonic
    /// from 1 per open session, in enqueue (= write) order — which, PAIRED
    /// WITH <see cref="Session"/>, is how a caller correlates its own line
    /// with the <see cref="LineWritten"/> that reports it (never by matching
    /// the text: two callers legitimately send the same command; and never by
    /// the sequence alone, which repeats across opens). 0 means the transport
    /// refused the line (closed or closing): nothing will ever be written, so
    /// nothing will ever be raised for it.</summary>
    long SendLine(string command);
}

public sealed class LineReceivedEventArgs(string line) : EventArgs
{
    public string Line { get; } = line;
}

public sealed class LineWrittenEventArgs(long session, long sequence, string line) : EventArgs
{
    /// <summary>The <see cref="ITransport.Session"/> this line was written
    /// under. Carried on the EVENT rather than read from the transport by the
    /// handler: by the time a delayed report is handled the transport may
    /// already be on the next session, and then the reader would attribute an
    /// old line to a new one.</summary>
    public long Session { get; } = session;

    /// <summary>The sequence <see cref="ITransport.SendLine"/> returned for
    /// this line — unique only WITHIN <see cref="Session"/>.</summary>
    public long Sequence { get; } = sequence;

    /// <summary>The command as sent, without the transport's CR.</summary>
    public string Line { get; } = line;
}

public sealed class TransportErrorEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}
