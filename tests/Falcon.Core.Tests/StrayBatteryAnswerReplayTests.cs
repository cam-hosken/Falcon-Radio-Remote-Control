using Falcon.Core.Radio;
using Falcon.Core.Tests.Transport;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// THE EXTRA <c>Battery Status</c> A MODE ENTRY PRINTS, replayed verbatim
/// through the REAL transport (bench 2026-08-22, round 15 — the wire runs
/// <c>bench/transcripts/r15-p1-wire-read-20260822-181146.jsonl</c> and
/// <c>…-181538.jsonl</c>, corroborated by the owner's own field console
/// <c>field-clone-console-20260820-1738.txt</c>: 19 <c>BAT ST</c> sent
/// against 21 answers).
///
/// <para>This is the second cause of the empty clone file, and the one A0's
/// wire clock did not remove. The radio answers ONE <c>BAT ST</c> at a mode
/// entry with TWO battery lines, around its <c>IN_PROG</c> chatter and before
/// the closing prompt. The surplus line used to credit whatever ping was next
/// in the queue — a ping still behind the prompt gate, i.e. one whose command
/// had not been sent at all — and from there every read completed one answer
/// early, so the address book, the schedules and the messages published EMPTY
/// while marking themselves <c>Read</c>.</para>
///
/// <para>The stack is the production one below Core: <see cref="SerialTransport"/>
/// over the byte-injecting port, with a gate that only a PROMPT releases —
/// because "the next sentinel has not been written yet" is exactly what the
/// rule turns on, and a fake that wrote at enqueue could not express it.</para>
/// </summary>
public sealed class StrayBatteryAnswerReplayTests : IDisposable
{
    private const string Battery = "Battery Status FULL 26.2V";

    private readonly FakeSerialPort _port = new();
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;

    public StrayBatteryAnswerReplayTests()
    {
        // Only a prompt releases a write (no timeout fallback inside the
        // test's lifetime): the wire order is then the test's to state.
        _transport = new SerialTransport(_port) { OpenSettleMs = 0, GateTimeoutMs = 60_000 };
        _radio = new Prc138Radio(_transport, new InlineContext());
    }

    public void Dispose() => _radio.Dispose();

    /// <summary>Bytes as the radio frames them (R1): payload lines, then a
    /// bare mode prompt — which is also what releases the write gate.</summary>
    private void Inject(params string[] lines)
    {
        foreach (var line in lines) _port.InjectBytes("\r\n" + line + "\r\n");
    }

    private void InjectPrompt() => _port.InjectBytes("\rALE> ");

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private int Written(string command)
        => _port.WrittenCommands.Count(c => c == command + "\r");

    /// <summary>Release one queued write with a prompt and wait for it to
    /// reach the port.</summary>
    private void ReleaseOneWrite(int expectedTotal)
    {
        InjectPrompt();
        Assert.True(_port.WaitForWrites(expectedTotal, 3_000),
            $"write #{expectedTotal} never reached the port");
    }

    /// <summary>Connect and drain BOTH init sentinels, one prompt-released
    /// write at a time, so the queue is empty and the stream is in step.</summary>
    private void ConnectReady()
    {
        _radio.Connect(new PortSettings { PortName = "FAKE", BaudRate = 9600 });

        // The ritual: "" "" PORT_R ECHO OFF ×2, SH, PORT_R, POW, BAT ST — EIGHT
        // writes since the F1 power read joined the init queries
        // (plan-ale-broadcast-round.md). The first goes immediately (nothing in
        // flight); the rest need a prompt each.
        Assert.True(_port.WaitForWrites(1, 3_000), "the ritual's first line never went out");
        for (int write = 2; write <= 8; write++) ReleaseOneWrite(write);
        Assert.Equal(1, Written("BAT ST"));

        Inject(Battery);                                   // init sentinel #1 answers
        Assert.True(WaitUntil(() => _radio.Connection == ConnectionState.Ready));

        ReleaseOneWrite(9);                                 // sentinel #2's own write
        Assert.Equal(2, Written("BAT ST"));
        Inject(Battery);                                    // …and its own answer
        Assert.True(WaitUntil(() => _radio.PendingPingCount == 0));
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.StrayBatteryAnswers);
    }

    [Fact]
    public void TheModeEntrysSECONDBatteryLine_CompletesNothing_AndTheNextPingWaitsForItsOwn()
    {
        ConnectReady();
        bool? gate = null, leg = null;

        // ---- THE CAPTURE (r15-p1-wire-read-…-181146.jsonl, the ALE entry at
        // 27 823-29 419 ms), in its load-bearing order: the mode gate's
        // sentinel is written, THEN the radio's chatter and TWO battery lines
        // arrive, and the closing prompt comes only after them. No prompt
        // falls between that write and the surplus line — which is precisely
        // why the next sentinel cannot have been written when it lands.
        _radio.Ping(ok => gate = ok, 0);                    // the mode gate's sentinel
        Inject(" IN_PROG");                                 // the entry's chatter…
        ReleaseOneWrite(10);                                // …whose prompt releases the write
        Assert.Equal(3, Written("BAT ST"));

        // The campaign's next leg queues its own sentinel behind it: not
        // dispatched yet (single outstanding), and once it is, the writer is
        // waiting for a prompt that has not come.
        _radio.Ping(ok => leg = ok, 0);
        Assert.Equal(2, _radio.PendingPingCount);

        Inject(" " + Battery);                              // (the capture's leading space, kept)
        Assert.True(WaitUntil(() => gate is not null));
        Assert.True(gate);                                  // the WRITTEN sentinel's own answer

        Inject(Battery);                                    // …and the radio's EXTRA one

        // The next leg's sentinel has been dispatched by that completion, but
        // the writer is still behind the gate — so the extra line cannot be
        // its answer, and is not treated as one.
        Assert.Null(leg);
        Assert.Equal(1, _radio.StrayBatteryAnswers);
        Assert.Equal(0, _radio.PingAnswerDebt);             // it owes nobody anything
        Assert.Equal(3, Written("BAT ST"));                 // …and nothing new was sent

        // The entry's closing prompt releases the leg's own command…
        ReleaseOneWrite(11);
        Assert.Equal(4, Written("BAT ST"));
        Assert.Null(leg);                                   // still waiting for ITS answer

        Inject(Battery);                                    // …which is this one.
        Assert.True(WaitUntil(() => leg is not null));
        Assert.True(leg);
        Assert.Equal(0, _radio.PingAnswerDebt);             // the stream stayed in step
        Assert.Equal(1, _radio.StrayBatteryAnswers);
    }

    /// <summary>
    /// AUDIT ROUND 2 (b) — A PORT WRITE THAT BLOCKS IS NOT THE SENTINEL'S
    /// FAULT. The Windows port may hold a write for up to 2 000 ms while the
    /// first init sentinel's budget is 1 500 ms: if the clock started when the
    /// line merely LEFT THE QUEUE, it could expire before the port had taken
    /// the bytes — A0's timeout-on-an-unwritten-command, rebuilt. The clock
    /// starts on the ACCEPTED write and nowhere else.
    /// </summary>
    [Fact]
    public void APortWriteThatBLOCKSPastTheBudget_DoesNotTimeOutTheSentinel()
    {
        ConnectReady();
        bool? pinged = null;

        using var gate = new ManualResetEventSlim(false);
        _port.WriteGate = gate;

        _radio.Ping(ok => pinged = ok, 200);          // a 200 ms budget…
        InjectPrompt();                                // …released for the wire…
        Assert.True(WaitUntil(() => _port.WritesStarted > 0), "the write never started");

        Thread.Sleep(700);                             // …and the PORT holds it, far past 200 ms
        Assert.Null(pinged);                           // no clock is running yet

        gate.Set();                                    // the port takes the bytes: NOW it runs
        Assert.True(WaitUntil(() => pinged is not null), "the accepted write never armed the clock");
        Assert.False(pinged);                          // and times out on its own budget
    }

    /// <summary>
    /// AUDIT ROUND 2 (e) — THE RACE THAT FORCED THE TWO STAGES. The far side
    /// can answer while the writer thread is still inside its port call: an
    /// in-process radio wins that race every time, and a real one can too. An
    /// answer that arrives after the line LEFT THE QUEUE is the head's own,
    /// even though the port has not reported back yet.
    /// </summary>
    [Fact]
    public void AnAnswerBetweenTheStartAndTheAcceptedWrite_IsTheHeadsOwn()
    {
        ConnectReady();
        bool? pinged = null;

        using var gate = new ManualResetEventSlim(false);
        _port.WriteGate = gate;

        _radio.Ping(ok => pinged = ok, 0);
        InjectPrompt();
        Assert.True(WaitUntil(() => _port.WritesStarted > 0), "the write never started");

        Inject(Battery);                               // answered mid-write
        Assert.True(WaitUntil(() => pinged is not null), "the head was treated as unasked");
        Assert.True(pinged);
        Assert.Equal(0, _radio.StrayBatteryAnswers);   // not a stray: it HAD been asked
        Assert.Equal(0, _radio.PingAnswerDebt);

        gate.Set();
    }

    /// <summary>
    /// AUDIT ROUND 2 (c) — A WRITE THAT THROWS AFTER IT STARTED. Nothing is
    /// reported written, so no clock is ever armed for a line the radio never
    /// heard; the terminal path (a close, here the radio's own Disconnect)
    /// completes it false exactly once, as it always has.
    /// </summary>
    [Fact]
    public void AWriteThatThrowsAfterItStarted_ArmsNoClock_AndTheCloseCompletesItFalseOnce()
    {
        ConnectReady();
        int calls = 0;
        bool? result = null;

        _port.FailWrites = new IOException("the port went away mid-write");
        _radio.Ping(ok => { calls++; result = ok; }, 200);
        InjectPrompt();                                // release it for the wire
        Assert.True(WaitUntil(() => _port.WritesStarted > 0), "the write never started");

        Thread.Sleep(600);                             // three budgets' worth
        Assert.Equal(0, calls);                        // no clock: the line never went out

        _radio.Disconnect();                           // the terminal path
        Assert.Equal(1, calls);
        Assert.False(result);

        Thread.Sleep(300);
        Assert.Equal(1, calls);                        // …exactly once
    }

    /// <summary>
    /// AUDIT ROUND 2 — and the production half of the dead-session story: a
    /// write parked inside the port when the transport is CLOSED is abandoned,
    /// not reported. So the old session cannot even deliver the late report
    /// the (session, sequence) pair exists to reject — the aliasing is barred
    /// twice, and the pair still guards the case a slower close would allow.
    /// </summary>
    [Fact]
    public void AWriteParkedInThePortWhenTheTransportCloses_IsNeverReportedWritten()
    {
        var written = new List<(long Session, long Sequence)>();
        _transport.LineWritten += (_, e) => { lock (written) written.Add((e.Session, e.Sequence)); };

        using var gate = new ManualResetEventSlim(false);
        _port.WriteGate = gate;
        _transport.Open(new PortSettings { PortName = "FAKE", BaudRate = 9600 });
        Assert.Equal(1, _transport.Session);

        _transport.SendLine("BAT ST");
        Assert.True(WaitUntil(() => _port.WritesStarted > 0), "the write never started");
        lock (written) Assert.Empty(written);

        _transport.Close();                            // cancels the writer under the parked write
        gate.Set();
        Thread.Sleep(200);
        lock (written) Assert.Empty(written);          // nothing was ever reported written

        _transport.Open(new PortSettings { PortName = "FAKE", BaudRate = 9600 });
        Assert.Equal(2, _transport.Session);           // …and the new session is a new number
    }

    [Fact]
    public void TheStrayNeverStartsAClock_NorEndsOne()
    {
        // A stray must not touch the head at all: not complete it, not arm it,
        // not disarm it. The head here has a SHORT timeout and its write is
        // still queued, so if a stray were treated as an answer the callback
        // would fire true instead of false.
        ConnectReady();
        bool? result = null;

        _radio.Ping(ok => result = ok, 200);
        Assert.Equal(1, _radio.PendingPingCount);

        Inject(Battery);                                    // arrives BEFORE the write
        Assert.Null(result);
        Assert.Equal(1, _radio.StrayBatteryAnswers);

        // The clock has not started either (A0): the ping is still waiting to
        // be asked, well past its own timeout.
        Thread.Sleep(500);
        Assert.Null(result);

        ReleaseOneWrite(10);                                // now it is asked…
        Assert.True(WaitUntil(() => result is not null), "the written sentinel never timed out");
        Assert.False(result);                               // …and times out on its own clock
    }
}
