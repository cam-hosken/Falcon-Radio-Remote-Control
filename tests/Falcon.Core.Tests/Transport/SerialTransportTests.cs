using System.Diagnostics;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests.Transport;

/// <summary>
/// The production SerialTransport over the byte-injecting fake port.
/// Pins the Q7 flow-control rule verbatim (protocol.md "Command pacing —
/// RESOLVED"): one command in flight; the next write released by (a) a
/// prompt observed since the last write or (b) the gate timeout — a
/// swallowed command must never latch the gate closed. Plus the Stage 1
/// smoke's wart: SendLine must be NON-BLOCKING because Core legally sends
/// from receive handlers (the ping queue dispatches inside the BATTERY
/// handler while holding its ping lock).
///
/// Timing assertions are one-sided where possible ("not yet written" windows
/// are short; "eventually written" windows are generous) so a slow CI runner
/// cannot flip a verdict.
/// </summary>
public class SerialTransportTests : IDisposable
{
    private readonly FakeSerialPort _port = new();
    private readonly SerialTransport _transport;
    private readonly List<Exception> _errors = [];
    private readonly List<string> _lines = [];
    private readonly object _rxLock = new();

    public SerialTransportTests()
    {
        _transport = new SerialTransport(_port);
        _transport.TransportError += (_, e) => { lock (_rxLock) _errors.Add(e.Error); };
        _transport.LineReceived += (_, e) => { lock (_rxLock) _lines.Add(e.Line); };
    }

    /// <summary>How long a teardown pin will wait for <c>Close</c> before
    /// declaring the bound gone. Comfortably above the transport's own 3000 ms
    /// backstop plus the 2000 ms writer join, so a loaded runner cannot flip
    /// the verdict — but FINITE, which is the whole point: a regressed
    /// backstop must produce a named assertion failure, not a hung run that
    /// only CI's workflow timeout ends.</summary>
    private const int TestDeadlineMs = 15_000;

    public void Dispose() => _transport.Dispose();

    private void Open(int settleMs = 0, int gateMs = 2_000)
    {
        _transport.OpenSettleMs = settleMs;
        _transport.GateTimeoutMs = gateMs;
        _transport.Open(new PortSettings { PortName = "FAKE" });
    }

    private List<string> Lines { get { lock (_rxLock) return [.. _lines]; } }
    private List<Exception> Errors { get { lock (_rxLock) return [.. _errors]; } }

    private bool WaitForLines(int count, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_rxLock) { if (_lines.Count >= count) return true; }
            Thread.Sleep(5);
        }
        lock (_rxLock) return _lines.Count >= count;
    }

    // ---- Writer worker & prompt gate --------------------------------------

    [Fact]
    public void FirstCommand_WritesImmediately_AndAppendsCr()
    {
        Open();
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        Assert.Equal("SH\r", _port.WrittenCommands[0]);
    }

    [Fact]
    public void SecondCommand_IsHeldUntilAPromptArrives()
    {
        Open(gateMs: 60_000);           // timeout may not release it in this test
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));

        _transport.SendLine("BAT ST");
        Assert.False(_port.WaitForWrites(2, 250));      // gate closed: held

        _port.InjectBytes("\rSSB> ");                   // prompt (R1 framing)
        Assert.True(_port.WaitForWrites(2, 2_000));
        Assert.Equal("BAT ST\r", _port.WrittenCommands[1]);
    }

    [Fact]
    public void GateTimeout_ReleasesTheNextCommand_WhenThePromptNeverComes()
    {
        Open(gateMs: 500);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));

        _transport.SendLine("BAT ST");                  // previous command "swallowed"
        Assert.False(_port.WaitForWrites(2, 100));      // still gated…
        Assert.True(_port.WaitForWrites(2, 5_000));     // …released by the timeout
    }

    [Fact]
    public void ExpiredGate_DoesNotLatch_ALateCommandGoesOutImmediately()
    {
        // "A swallowed command must never latch the gate closed"
        // (protocol.md, The rule). The deadline is measured from the WRITE,
        // not from the next send: once it has passed, a command queued later
        // must not serve a fresh 2 s sentence.
        Open(gateMs: 100);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));

        Thread.Sleep(400);                              // deadline long past, no prompt ever
        var sw = Stopwatch.StartNew();
        _transport.SendLine("BAT ST");
        Assert.True(_port.WaitForWrites(2, 2_000));
        Assert.True(sw.ElapsedMilliseconds < 1_000,
            $"expired gate held the write for {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SendLine_NeverBlocks_WhileTheGateIsClosed()
    {
        Open(gateMs: 60_000);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));

        var sw = Stopwatch.StartNew();
        _transport.SendLine("BAT ST");
        _transport.SendLine("PORT_R");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"SendLine blocked for {sw.ElapsedMilliseconds} ms with the gate closed");
        Assert.Equal(1, _port.WriteCount);              // both still queued
    }

    [Fact]
    public void SendFromInsideAReceiveHandler_DoesNotStallTheReadPath()
    {
        // The Stage 1 stall class, pinned dead (probes.md "Stage 1 bench
        // smoke", finding 3): the ping queue legally dispatches the next
        // BAT ST from inside the BATTERY line handler. With a blocking
        // SendLine that stalls the read loop for the whole gate timeout;
        // with the writer worker it must return immediately and the next
        // line must arrive without delay.
        Open(gateMs: 60_000);
        _transport.SendLine("SH");                      // close the gate
        Assert.True(_port.WaitForWrites(1, 2_000));

        _transport.LineReceived += (_, e) =>
        {
            if (e.Line.StartsWith("Battery"))
                _transport.SendLine("BAT ST");          // send from the receive handler
        };

        var sw = Stopwatch.StartNew();
        _port.InjectBytes("Battery Status FULL 31.4V\r\n");     // verbatim R1 answer
        _port.InjectBytes("IN_PROG\r\n");                        // next line right behind it
        sw.Stop();

        Assert.True(WaitForLines(2, 2_000));
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"read path stalled {sw.ElapsedMilliseconds} ms behind a send from the handler");
        Assert.Equal(1, _port.WriteCount);              // the handler's send is queued, not inline
    }

    // ---- LineWritten: the wire's own clock (round 15 A0) --------------------

    /// <summary>
    /// §13.4 gate (4). Everything that runs a clock against a command it sent
    /// starts that clock HERE, so this event has to mean exactly what it says:
    /// once per line ACTUALLY WRITTEN, AFTER the gate released it, in write
    /// order, carrying the sequence <see cref="SerialTransport.SendLine"/>
    /// handed back. The held command is the anti-vacuity — a "LineWritten"
    /// raised at enqueue would pass every other clause of this test.
    /// </summary>
    [Fact]
    public void LineWritten_FiresOncePerWrittenLine_AfterTheGate_InOrder_WithSendLinesSequences()
    {
        var written = new List<(long Sequence, string Line)>();
        _transport.LineWritten += (_, e) => { lock (_rxLock) written.Add((e.Sequence, e.Line)); };
        List<(long Sequence, string Line)> Written() { lock (_rxLock) return [.. written]; }

        Open(gateMs: 60_000);           // the timeout may not release anything here

        long first = _transport.SendLine("SH");
        Assert.Equal(1, first);         // monotonic from 1 per open session
        Assert.True(_port.WaitForWrites(1, 2_000));
        Assert.Equal([(1L, "SH")], Written());

        long second = _transport.SendLine("BAT ST");
        Assert.Equal(2, second);
        Assert.False(_port.WaitForWrites(2, 250));      // gate shut: not written…
        Assert.Equal([(1L, "SH")], Written());          // …and NOT reported written

        _port.InjectBytes("\rSSB> ");                   // the prompt releases it
        Assert.True(_port.WaitForWrites(2, 2_000));
        long deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline && Written().Count < 2) Thread.Sleep(5);

        Assert.Equal([(1L, "SH"), (2L, "BAT ST")], Written());
    }

    [Fact]
    public void TheWriteSequence_RestartsAtOne_ForEachOpenSession_AndTheSESSIONMovesWithIt()
    {
        // "Per open session" is what makes a stored sequence safe to compare
        // after a reconnect: a stale one can never match a fresh line — but
        // ONLY because the SESSION moves when the sequence restarts. Audit
        // round 2 found `_session` never assigned in production (CS0649), so
        // every event carried session 0 and the aliasing the pair exists to
        // prevent was live. Pinned here, on the production transport.
        var seen = new List<(long Session, long Sequence, string Line)>();
        _transport.LineWritten += (_, e) => { lock (_rxLock) seen.Add((e.Session, e.Sequence, e.Line)); };
        var started = new List<(long Session, long Sequence, string Line)>();
        _transport.WriteStarted += (_, e) => { lock (_rxLock) started.Add((e.Session, e.Sequence, e.Line)); };

        Assert.Equal(0, _transport.Session);            // never opened
        Open();
        Assert.Equal(1, _transport.Session);
        Assert.Equal(1, _transport.SendLine("SH"));
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.Close();

        Open();
        Assert.Equal(2, _transport.Session);            // the number that disambiguates
        Assert.Equal(1, _transport.SendLine("SH"));     // …while the sequence restarts
        Assert.True(_port.WaitForWrites(2, 2_000));

        lock (_rxLock)
        {
            Assert.Equal([(1L, 1L, "SH"), (2L, 1L, "SH")], seen);
            Assert.Equal(seen, started);                // both stages carry it
        }
    }

    [Fact]
    public void WriteStarted_PrecedesTheWrite_AndLineWritten_FollowsIt()
    {
        // AUDIT ROUND 2 — the two stages are different facts and the gap
        // between them is real: a Windows write may hold the bytes for up to
        // 2 000 ms. "Being asked" is what tells an answer it may belong to a
        // command; "written" is the only thing a CLOCK may start from.
        var started = new List<long>();
        var written = new List<long>();
        _transport.WriteStarted += (_, e) => { lock (_rxLock) started.Add(e.Sequence); };
        _transport.LineWritten += (_, e) => { lock (_rxLock) written.Add(e.Sequence); };

        using var gate = new ManualResetEventSlim(false);
        _port.WriteGate = gate;
        Open();

        _transport.SendLine("SH");
        Assert.True(WaitUntil(() => _port.WritesStarted == 1), "the write never started");

        lock (_rxLock)
        {
            Assert.Equal([1L], started);                // the line has left the queue…
            Assert.Empty(written);                      // …and the port still has not taken it
        }
        Assert.Equal(0, _port.WriteCount);

        gate.Set();                                     // the port accepts the bytes
        Assert.True(_port.WaitForWrites(1, 2_000));
        Assert.True(WaitUntil(() =>
        {
            lock (_rxLock) return written.Count == 1;
        }), "the accepted write was never reported");
        lock (_rxLock) Assert.Equal([1L], written);
    }

    [Fact]
    public void AWriteThatTHROWS_IsNeverReportedWritten()
    {
        // Gate (4)'s other half: a line that did not go out must not be
        // reported as one, or a clock would be started against a command the
        // radio never heard.
        var started = new List<string>();
        var written = new List<string>();
        _transport.WriteStarted += (_, e) => { lock (_rxLock) started.Add(e.Line); };
        _transport.LineWritten += (_, e) => { lock (_rxLock) written.Add(e.Line); };

        Open();
        _port.FailWrites = new IOException("port gone");
        _transport.SendLine("SH");

        Assert.True(WaitUntil(() => Errors.Count > 0), "the write fault never surfaced");
        lock (_rxLock)
        {
            Assert.Equal(["SH"], started);              // it was attempted…
            Assert.Empty(written);                      // …and never written
        }
    }

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

    [Fact]
    public void SendLine_OnAClosedTransport_ReturnsZero_AndIsNeverReportedWritten()
    {
        // 0 is the contract's "nothing was queued, so nothing will ever be
        // raised" — the case a caller must fall back on rather than wait for
        // a write that cannot happen.
        var written = new List<long>();
        _transport.LineWritten += (_, e) => { lock (_rxLock) written.Add(e.Sequence); };

        Assert.Equal(0, _transport.SendLine("SH"));      // never opened
        Open();
        _transport.Close();
        Assert.Equal(0, _transport.SendLine("SH"));

        lock (_rxLock) Assert.Empty(written);
    }

    // ---- Open settle -------------------------------------------------------

    [Fact]
    public void OpenSettle_HoldsTheFirstWrite_ButReadsStartImmediately()
    {
        Open(settleMs: 600);
        _transport.SendLine("SH");

        _port.InjectBytes("SSB> ");                     // stale-buffer chatter right after open
        Assert.True(WaitForLines(1, 1_000));            // reads live during the settle
        Assert.False(_port.WaitForWrites(1, 150));      // write still held by the settle
        Assert.True(_port.WaitForWrites(1, 5_000));     // released after ~600 ms
    }

    // ---- Framer handoff -----------------------------------------------------

    [Fact]
    public void GreaterThan_ReleasesTheGate_OnlyForExactModePrompts()
    {
        Open(gateMs: 60_000);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.SendLine("BAT ST");

        // '>' inside payload (e.g. stored AMD text) is NOT a prompt: the
        // framer keeps buffering and the gate stays shut.
        _port.InjectBytes("MSG 1 -> MEET AT 0900\r\n");
        Assert.False(_port.WaitForWrites(2, 250));
        Assert.Equal("MSG 1 -> MEET AT 0900", Lines.Single());

        _port.InjectBytes("\rALE> ");                   // a real prompt, any mode
        Assert.True(_port.WaitForWrites(2, 2_000));
    }

    [Fact]
    public void CrTerminatedLineEndingInGreaterThan_DoesNotReleaseTheGate()
    {
        // Audit round 1, F1: pins the TRANSPORT's gate predicate, not just
        // the framer. A CR-terminated payload line whose last character is
        // '>' (legal stored-AMD text) reaches the gate check as a complete
        // line ending in '>' — an EndsWith('>') predicate would release the
        // gate on it; only the exact mode prompts may.
        Open(gateMs: 60_000);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.SendLine("BAT ST");

        _port.InjectBytes("MSG 1 ->\r\n");              // payload line ENDING in '>'
        Assert.True(WaitForLines(1, 1_000));
        Assert.Equal("MSG 1 ->", Lines.Single());       // surfaced as one payload line
        Assert.False(_port.WaitForWrites(2, 300));      // gate still shut

        _port.InjectBytes("\rSSB> ");                   // the real prompt releases it
        Assert.True(_port.WaitForWrites(2, 2_000));
        Assert.Equal("BAT ST\r", _port.WrittenCommands[1]);
    }

    [Fact]
    public void PromptPrefixedAsyncLine_SplitsIntoPromptAndPayload_AndReleasesTheGate()
    {
        // Async lines arrive prompt-prefixed ("<CR>ALE> SCANNING" — R7).
        Open(gateMs: 60_000);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.SendLine("BAT ST");

        _port.InjectBytes("\rALE> SCANNING\r\n");

        Assert.True(_port.WaitForWrites(2, 2_000));     // the embedded prompt released the gate
        Assert.True(WaitForLines(2, 1_000));
        Assert.Equal("ALE>", Lines[0].Trim());
        Assert.Equal("SCANNING", Lines[1].Trim());
    }

    /// <summary>Verbatim R1 SSB SH capture (docs/probes.md), byte-faithful
    /// including the bare-LF RXONLY quirk and the CR-prefixed prompt.</summary>
    private const string R1ShCapture =
        "\nCHAN 00 \r\nKEY OFF \r\nRxFr 01600000\r\nTxFr 01600000\r\nMODE CW \r\n" +
        "AGC MED \r\nBAND 1.0 \r\nRXONLY NO \n\rBFO +0000\r\nMODEM OFF\r\nDV OFF\r\n" +
        "DGT_SQUELCH OFF\r\nAVS OFF\r\nENCRYPT OFF\r\nSQ_LEVEL HIGH\r\nSQUELCH OFF\r\n" +
        "POWER low\r\nANTENNA   auto \r\nCWOFFSET 0000\r\nRWAS DISABLED\r\n" +
        "RETRANS DISABLED\r\n\r\n\rSSB> ";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(64)]
    public void NoLinesLost_AcrossChunkBoundaries(int chunkSize)
    {
        // The whole captured SH block fed in one chunk is the reference…
        Open();
        _port.InjectBytes(R1ShCapture);
        var expected = Lines;
        Assert.Equal(22, expected.Count);               // 21 payload lines + the prompt
        Assert.Equal("SSB>", expected[^1]);

        // …and the same bytes split at awkward offsets must produce the
        // identical line sequence.
        using var port2 = new FakeSerialPortScope(out var fake);
        var got = new List<string>();
        port2.Transport.LineReceived += (_, e) => got.Add(e.Line);
        port2.Transport.Open(new PortSettings { PortName = "FAKE2" });

        var bytes = System.Text.Encoding.ASCII.GetBytes(R1ShCapture);
        for (int i = 0; i < bytes.Length; i += chunkSize)
            fake.InjectBytes(bytes[i..Math.Min(i + chunkSize, bytes.Length)]);

        Assert.Equal(expected, got);
    }

    /// <summary>Second transport+port pair with deterministic disposal.</summary>
    private sealed class FakeSerialPortScope : IDisposable
    {
        public SerialTransport Transport { get; }
        public FakeSerialPortScope(out FakeSerialPort port)
        {
            port = new FakeSerialPort();
            Transport = new SerialTransport(port) { OpenSettleMs = 0 };
        }
        public void Dispose() => Transport.Dispose();
    }

    // ---- Close / disconnect --------------------------------------------------

    [Fact]
    public void CloseDuringAPendingWrite_DropsItQuietly()
    {
        Open(gateMs: 60_000);
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.SendLine("BAT ST");                  // pending behind the closed gate

        var sw = Stopwatch.StartNew();
        _transport.Close();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5_000, "Close hung behind the gate");
        Thread.Sleep(150);                              // give a leaked writer time to misbehave
        Assert.Equal(1, _port.WriteCount);              // the pending command never went out
        Assert.Empty(Errors);                           // teardown is not a transport error
        Assert.False(_transport.IsOpen);
    }

    [Fact]
    public void ByteLevelDisconnect_SurfacesAsTransportError()
    {
        Open();
        var yank = new IOException("COM port 'FAKE' is no longer present.");
        _port.InjectDisconnect(yank);

        Assert.Single(Errors);
        Assert.Same(yank, Errors[0]);
        Assert.False(_transport.IsOpen);                // port flipped before emitting
    }

    [Fact]
    public void DisconnectDuringClose_IsNotAnError()
    {
        Open();
        _transport.Close();
        _port.InjectDisconnect(new IOException("late teardown fault"));
        Assert.Empty(Errors);
    }

    [Fact]
    public void WriteFailure_WhileOpen_SurfacesAsTransportError()
    {
        Open();
        var boom = new IOException("write failed");
        _port.FailWrites = boom;
        _transport.SendLine("SH");

        long deadline = Environment.TickCount64 + 2_000;
        while (Errors.Count == 0 && Environment.TickCount64 < deadline) Thread.Sleep(5);
        Assert.Same(boom, Errors.Single());
    }

    [Fact]
    public void SendLine_AfterClose_IsDroppedNotThrown()
    {
        Open();
        _transport.Close();
        _transport.SendLine("SH");                      // no-op, no exception
        Thread.Sleep(100);
        Assert.Equal(0, _port.WriteCount);
    }

    [Fact]
    public void Reopen_AfterClose_SendsAgain()
    {
        Open();
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));
        _transport.Close();

        _transport.Open(new PortSettings { PortName = "FAKE" });
        _transport.SendLine("PORT_R");
        Assert.True(_port.WaitForWrites(2, 2_000));
        Assert.Equal("PORT_R\r", _port.WrittenCommands[1]);
    }

    // ---- Bounded teardown (round 13 D2, repair 2) ----------------------------

    /// <summary>
    /// A WEDGED PORT CANNOT HANG THE TRANSPORT. The fake's close never
    /// completes; <see cref="SerialTransport.Close"/> must still return, via
    /// its own deadline.
    ///
    /// <para>This is the host-testable half of the D2 teardown work. It pins
    /// the TRANSPORT-LEVEL backstop only — it does NOT claim to test
    /// Android's internal per-phase caps, which compile for the android TFM
    /// alone and are carried by source-guard pins plus the owner's device
    /// check.</para>
    /// </summary>
    [Fact]
    public void Close_AgainstAStuckPort_ReturnsOnTheBackstop_RatherThanHanging()
    {
        Open();
        _port.StuckClose = true;

        // RUN THE CLOSE UNDER THE TEST'S OWN DEADLINE (D2 audit round 1,
        // MINOR 3). If the backstop regresses, Close blocks FOREVER — and a
        // bare call here would hang the whole run until CI's workflow timeout
        // killed it, which reads as an infrastructure failure rather than as
        // this pin catching a bug. Racing it against a generous test-side
        // deadline turns that regression into a clean, named assertion
        // failure.
        //
        // …and the `finally` is the other half of that (round 2): the class
        // fixture disposes the transport, Dispose calls Close a SECOND time,
        // and that one has no deadline around it. Releasing the port here
        // means teardown always meets a port that closes instantly — so a
        // regression fails cleanly through disposal too, instead of wedging
        // the testhost after the verdict was already in.
        try
        {
            var sw = Stopwatch.StartNew();
            bool returned = Task.Run(_transport.Close).Wait(TestDeadlineMs);
            sw.Stop();

            Assert.True(returned,
                $"Close did not return within {TestDeadlineMs} ms against a stuck port — the backstop is gone");

            // …and it really was the backstop, not an accidental fast path: a
            // close that never completes cannot return earlier than the deadline.
            Assert.True(sw.ElapsedMilliseconds >= SerialTransport.PortCloseTimeoutMs - 250,
                $"Close returned after only {sw.ElapsedMilliseconds} ms — it did not wait on the port at all");
            Assert.Equal(1, _port.CloseCalls);
        }
        finally
        {
            _port.ReleaseCloses();
        }
    }

    /// <summary>
    /// The nastier half: the abandoned close COMPLETES LATER, while a new
    /// session is already running on the same port object. This is the
    /// host-side stand-in for the device gate's pull → immediate replug →
    /// traffic-flows window — an old teardown finishing underneath a new
    /// session must not break it.
    /// </summary>
    [Fact]
    public void Reopen_AfterAnAbandonedClose_SendsAndReceivesNormally()
    {
        Open();
        _transport.SendLine("SH");
        Assert.True(_port.WaitForWrites(1, 2_000));

        // Completes AFTER the transport gives up waiting for it.
        _port.CloseDelayMs = SerialTransport.PortCloseTimeoutMs + 1_500;

        // Same bounded shape AND the same finally-release as the stuck pin
        // (round 2): this pin shares the class fixture, so leaving a delay
        // armed would make disposal's second Close pay the backstop again —
        // and would leave the same wedge hazard behind if the fake's delay or
        // Close's shape ever changed. The wait is bounded for the same reason.
        try
        {
            Assert.True(Task.Run(_transport.Close).Wait(TestDeadlineMs),
                $"Close did not return within {TestDeadlineMs} ms");
            Assert.False(_port.DelayedCloseCompleted, "the close should still be pending — it was abandoned, not awaited");

            // Immediate reopen, exactly as a replug drives it.
            Open();
            _transport.SendLine("PORT_R");
            Assert.True(_port.WaitForWrites(2, 2_000));
            Assert.Equal("PORT_R\r", _port.WrittenCommands[1]);

            // Receive works too, and the framer was reset by the close: a partial
            // line from the old session cannot glue itself onto the new one.
            _port.InjectBytes("\nBattery Status FULL 31.4V\r\n");
            Assert.True(WaitForLines(1, 2_000));
            Assert.Contains(Lines, l => l.Contains("Battery Status", StringComparison.Ordinal));

            // And when the old close finally lands it changes nothing.
            long deadline = Environment.TickCount64 + 4_000;
            while (!_port.DelayedCloseCompleted && Environment.TickCount64 < deadline) Thread.Sleep(20);
            Assert.True(_port.DelayedCloseCompleted, "the delayed close never completed — the fixture is not exercising the race");
            _transport.SendLine("SH");
            Assert.True(_port.WaitForWrites(3, 2_000));
        }
        finally
        {
            _port.ReleaseCloses();
        }
    }

    /// <summary>
    /// THE WRITER SELF-JOIN. A write fault raises TransportError from the
    /// writer thread; a handler that tears down synchronously lands back in
    /// <see cref="SerialTransport.Close"/> ON that thread. Joining yourself
    /// can only burn the timeout, so the guard must skip it — the thread is
    /// exiting anyway.
    /// </summary>
    [Fact]
    public void Close_CalledFromTheWriterThread_DoesNotJoinItself()
    {
        Open();
        var closeReturned = new ManualResetEventSlim(false);
        var elapsed = 0L;

        // Tear down from inside the transport-error handler, which the writer
        // raises on its own thread — the production RadioSession shape.
        _transport.TransportError += (_, _) =>
        {
            var sw = Stopwatch.StartNew();
            _transport.Close();
            elapsed = sw.ElapsedMilliseconds;
            closeReturned.Set();
        };

        _port.FailWrites = new IOException("write failed");
        _transport.SendLine("SH");

        Assert.True(closeReturned.Wait(8_000), "Close never returned from the writer thread");

        // MEASURED, so the threshold is not a guess: Thread.Join(2000) on the
        // CURRENT thread does not throw and does not return early — it waits
        // the FULL timeout and then gives up (1997 ms on this bench). So a
        // bound of "< WriterJoinTimeoutMs" would be satisfied BY the bug and
        // catch nothing. The guarded path does no waiting at all, so half the
        // timeout separates the two cases with enormous margin.
        Assert.True(elapsed < SerialTransport.WriterJoinTimeoutMs / 2,
            $"Close took {elapsed} ms on the writer thread — it joined itself "
            + $"(a self-join burns the full {SerialTransport.WriterJoinTimeoutMs} ms)");
    }

    // ---- Production stack end-to-end (replayed capture) -----------------------

    [Fact]
    public void ProductionStack_ConnectRitual_ReachesReady_OnReplayedCapture()
    {
        // Prc138Radio → SerialTransport → fake port, driven to Ready by
        // injecting the R1 session transcript (replay, never an answering
        // fake). Gate at 1 ms so the writer free-runs; the test waits for
        // the ritual's writes to drain, then plays the capture.
        var port = new FakeSerialPort();
        var transport = new SerialTransport(port) { OpenSettleMs = 0, GateTimeoutMs = 1 };
        using var radio = new Prc138Radio(transport, new InlineContext());

        radio.Connect(new PortSettings { PortName = "FAKE" });

        // Ritual: 2 bare CRs, ECHO OFF ×2, SH, PORT_R, POW, BAT ST (sentinel
        // #1) — POW is the F1 power read (plan-ale-broadcast-round.md).
        Assert.True(port.WaitForWrites(8, 5_000));
        Assert.Equal("POW\r", port.WrittenCommands[6]);
        Assert.Equal("BAT ST\r", port.WrittenCommands[7]);

        // Replayed R1 capture: echo-off answer, prompt, battery answer ×2.
        port.InjectBytes("\nPORT_REMOTE ECHO OFF\r\n\r\n\rSSB> ");
        port.InjectBytes("\nBattery Status FULL 31.4V\r\n\r\n\rSSB> ");
        Assert.Equal(ConnectionState.Ready, radio.Connection);      // sentinel #1 completed init

        Assert.True(port.WaitForWrites(9, 5_000));                  // sentinel #2 dispatched
        Assert.Equal("BAT ST\r", port.WrittenCommands[8]);
        port.InjectBytes("\nBattery Status FULL 31.4V\r\n\r\n\rSSB> ");
        Assert.Equal(0, radio.PendingPingCount);
    }
}
