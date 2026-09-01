using System.Collections.Concurrent;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// Q3: the single-outstanding-ping queue that replaced the old orphan-answer
/// credit ledger. The race tests inject captured BATTERY lines from multiple
/// test threads through the ITransport fake — they exercise OUR locking and
/// callback contracts, not a simulated radio (the fake never answers; every
/// answer is an explicit injection of a verbatim captured line).
/// Outcome recorded in docs/tests.md ("Q3 redesign outcome").
/// </summary>
public class Q3PingQueueTests : RadioTestBase
{
    // ---- Sequential contracts -----------------------------------------------

    [Fact]
    public void Callbacks_RunInFifoOrder()
    {
        ConnectReady();
        var order = new List<int>();
        Radio.Ping(() => order.Add(1));
        Radio.Ping(() => order.Add(2));
        Radio.Ping(() => order.Add(3));

        Assert.Equal(1, Transport.CountSent("BAT ST"));   // single outstanding

        AnswerSentinel();
        Assert.Equal([1], order);
        Assert.Equal(2, Transport.CountSent("BAT ST"));

        AnswerSentinel();
        Assert.Equal([1, 2], order);

        AnswerSentinel();
        Assert.Equal([1, 2, 3], order);
        Assert.Equal(3, Transport.CountSent("BAT ST"));
    }

    [Fact]
    public void DuplicateInitSentinel_DoesNotCompleteALaterPing()
    {
        // Init queues TWO sentinels; a later user ping needs its own answer —
        // the init redundancy can never complete it early.
        Connect();
        AnswerSentinel();                       // init answer #1 → Ready
        bool userDone = false;
        Radio.Ping(() => userDone = true);      // queued behind init sentinel #2

        AnswerSentinel();                       // completes init sentinel #2
        Assert.False(userDone);

        AnswerSentinel();                       // completes the user ping
        Assert.True(userDone);
    }

    [Fact]
    public void TimedOutHead_DoesNotBlockLaterPings()
    {
        // The swallowed-sentinel case (R6: the radio silently swallows
        // commands): the head times out with false and the next entry is
        // dispatched — no head-of-line starvation, no spurious failure of
        // the later ping.
        ConnectReady();
        var results = new ConcurrentQueue<(int Id, bool Ok)>();
        Radio.Ping(ok => results.Enqueue((1, ok)), 100);
        Radio.Ping(ok => results.Enqueue((2, ok)), 0);

        Thread.Sleep(400);      // head times out
        Assert.Single(results);
        Assert.Equal((1, false), results.First());
        Assert.Equal(2, Transport.CountSent("BAT ST"));   // second dispatched

        AnswerSentinel();
        Assert.Equal(2, results.Count);
        Assert.Contains((2, true), results);
    }

    [Fact]
    public void LateAnswerAfterTimeout_CompletesTheNextPingEarly_NeverFailsIt()
    {
        // THE deliberate design difference vs the old credit ledger, on the
        // record: a late answer to a timed-out sentinel completes the NEXT
        // ping early (true). The old ledger's own analysis (audit-6 H1)
        // established early completion as strictly the lesser harm — the
        // failure it punished (spuriously failing a healthy radio's ping) is
        // structurally impossible here.
        ConnectReady();
        bool? first = null, second = null;
        Radio.Ping(ok => first = ok, 80);
        Radio.Ping(ok => second = ok, 0);

        Thread.Sleep(300);              // first times out; second's BAT ST dispatched
        Assert.False(first);
        Assert.Null(second);

        AnswerSentinel();               // the LATE answer to the first sentinel
        Assert.True(second);            // completes the second early — accepted
    }

    [Fact]
    public void PingAnswerDebt_CountsWhatTheCreditShift_CostsTheStream()
    {
        // Audit round 2, BLOCKER: the early completion above is accepted, but
        // it leaves the stream SHIFTED — every later completion is somebody
        // else's answer until the extra one is discarded. This is the fact an
        // outside caller needs to tell "my sentinel answered" from "a stray
        // answer completed my sentinel", and the ALE programming bracket
        // refuses to open while it stands.
        ConnectReady();
        Assert.Equal(0, Radio.PingAnswerDebt);          // a drained connect is in step

        bool? first = null, second = null;
        Radio.Ping(ok => first = ok, 80);
        // A sentinel legitimately ON THE WIRE is not a debt — only the head
        // is dispatched, and its answer is still owed to nobody else.
        Assert.Equal(0, Radio.PingAnswerDebt);

        Radio.Ping(ok => second = ok, 0);
        Thread.Sleep(300);                              // first times out; second dispatched
        Assert.False(first);
        Assert.Equal(1, Radio.PingAnswerDebt);          // the dead sentinel's answer is owed

        AnswerSentinel();                               // the late answer credits the second
        Assert.True(second);
        Assert.Equal(1, Radio.PingAnswerDebt);          // …so the shift is still outstanding

        AnswerSentinel();                               // the second's own answer, discarded
        Assert.Equal(0, Radio.PingAnswerDebt);          // the stream is back in step

        // A bare BAT ST from any other sender counts too: it consumes an
        // answer, so it shifts credits exactly the same way.
        Radio.QueryBatteryState();
        Assert.Equal(1, Radio.PingAnswerDebt);
        AnswerSentinel();
        Assert.Equal(0, Radio.PingAnswerDebt);
    }

    [Fact]
    public void PingAnswerDebt_ResetsWithTheSession()
    {
        ConnectReady();
        Radio.Ping(_ => { }, 80);
        Radio.Ping(_ => { }, 0);
        Thread.Sleep(300);
        Assert.Equal(1, Radio.PingAnswerDebt);

        Radio.Disconnect();
        ConnectReady();                                 // a fresh sentinel stream
        Assert.Equal(0, Radio.PingAnswerDebt);
    }

    [Fact]
    public void AnswerWithEmptyQueue_IsDiscarded()
    {
        ConnectReady();
        AnswerSentinel();               // nothing pending — must not throw
        Assert.Equal(0, Radio.PendingPingCount);

        // And a subsequent ping still needs its own answer:
        bool done = false;
        Radio.Ping(() => done = true);
        Assert.False(done);
        AnswerSentinel();
        Assert.True(done);
    }

    [Fact]
    public void PingOnClosedPort_CallsBackFalseImmediately()
    {
        bool? result = null;
        Radio.Ping(ok => result = ok, 0);
        Assert.False(result);
    }

    [Fact]
    public void Disconnect_DropsPendingPingsWithFalse_ExactlyOnce()
    {
        ConnectReady();
        var results = new List<bool>();
        Radio.Ping(ok => results.Add(ok), 0);
        Radio.Ping(ok => results.Add(ok), 0);

        Radio.Disconnect();
        Assert.Equal([false, false], results);

        Thread.Sleep(150);              // no timer may fire a second callback
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void TimedOutPing_CallbackRunsExactlyOnce_EvenIfAnswered()
    {
        ConnectReady();
        int calls = 0;
        Radio.Ping(_ => Interlocked.Increment(ref calls), 80);
        Thread.Sleep(300);
        AnswerSentinel();               // late answer: queue is empty, discarded
        Assert.Equal(1, calls);
    }

    // ---- Line-injection race tests --------------------------------------------

    [Fact]
    public void Race_PingsAndAnswersFromManyThreads_EveryCallbackExactlyOnce()
    {
        // 4 producer threads queue pings while an injector thread fires
        // captured BATTERY lines whenever one is outstanding. Exercises
        // the queue lock under contention.
        ConnectReady();
        const int PerThread = 25;
        const int Producers = 4;
        const int Total = Producers * PerThread;

        var callbackCounts = new ConcurrentDictionary<int, int>();
        int completed = 0;

        var producers = new List<Thread>();
        for (int t = 0; t < Producers; t++)
        {
            int baseId = t * PerThread;
            producers.Add(new Thread(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    int id = baseId + i;
                    Radio.Ping(ok =>
                    {
                        callbackCounts.AddOrUpdate(id, 1, (_, n) => n + 1);
                        if (ok) Interlocked.Increment(ref completed);
                    }, 0);
                }
            }));
        }

        var injector = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref completed) < Total && DateTime.UtcNow < deadline)
            {
                if (Radio.PendingPingCount > 0) AnswerSentinel();
                else Thread.Yield();
            }
        });

        foreach (var p in producers) p.Start();
        injector.Start();
        foreach (var p in producers) p.Join();
        injector.Join();

        Assert.Equal(Total, completed);
        Assert.Equal(Total, callbackCounts.Count);
        Assert.All(callbackCounts.Values, n => Assert.Equal(1, n));
        Assert.Equal(Total, Transport.CountSent("BAT ST"));   // one wire command per ping
        Assert.Equal(0, Radio.PendingPingCount);
    }

    [Fact]
    public void Race_ConcurrentInjection_NeverDoubleCompletes()
    {
        // All pings queued first, then TWO threads inject answers
        // concurrently: each answer may complete at most one ping; excess
        // answers are discarded, never double-delivered.
        ConnectReady();
        const int Total = 50;
        var callbackCounts = new ConcurrentDictionary<int, int>();
        int completed = 0;

        for (int i = 0; i < Total; i++)
        {
            int id = i;
            Radio.Ping(ok =>
            {
                callbackCounts.AddOrUpdate(id, 1, (_, n) => n + 1);
                if (ok) Interlocked.Increment(ref completed);
            }, 0);
        }

        var injectors = new List<Thread>();
        for (int t = 0; t < 2; t++)
        {
            injectors.Add(new Thread(() =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (Volatile.Read(ref completed) < Total && DateTime.UtcNow < deadline)
                {
                    if (Radio.PendingPingCount > 0) AnswerSentinel();
                    else Thread.Yield();
                }
            }));
        }
        foreach (var t in injectors) t.Start();
        foreach (var t in injectors) t.Join();

        Assert.Equal(Total, completed);
        Assert.All(callbackCounts.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void Race_TimeoutsRacingAnswers_ExactlyOnceContractHolds()
    {
        // Short-timeout pings racing injected answers: whatever interleaving
        // occurs, every callback runs exactly once and the queue drains.
        ConnectReady();
        const int Total = 40;
        var callbackCounts = new ConcurrentDictionary<int, int>();
        int done = 0;

        var producer = new Thread(() =>
        {
            for (int i = 0; i < Total; i++)
            {
                int id = i;
                Radio.Ping(_ =>
                {
                    callbackCounts.AddOrUpdate(id, 1, (_, n) => n + 1);
                    Interlocked.Increment(ref done);
                }, 5 + i % 3);          // aggressive timeouts race the injector
                Thread.Sleep(1);
            }
        });

        var injector = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref done) < Total && DateTime.UtcNow < deadline)
            {
                if (Radio.PendingPingCount > 0) AnswerSentinel();
                Thread.Sleep(2);
            }
        });

        producer.Start(); injector.Start();
        producer.Join(); injector.Join();

        Assert.Equal(Total, done);
        Assert.Equal(Total, callbackCounts.Count);
        Assert.All(callbackCounts.Values, n => Assert.Equal(1, n));
        Assert.Equal(0, Radio.PendingPingCount);
    }

    [Fact]
    public void Race_StateLinesInterleavedWithAnswers_ParserAndQueueStayConsistent()
    {
        // Captured SH-block and async lines fired from a second thread while
        // pings complete — the parse lock and ping lock must compose without
        // deadlock or corruption.
        ConnectReady();
        const int Total = 30;
        int completed = 0;

        var chatter = new Thread(() =>
        {
            string[] lines =
            [
                "CHAN 00 ", "RxFr 01600000", "MODE CW ", "AGC MED ", "BAND 1.0 ",
                "POWER CUTBACK   ", "POWER RESTORED   ", "SCANNING", "SCAN STOPPED",
                "SSB> ", "ALE> ",
            ];
            for (int i = 0; i < 200; i++)
                Transport.InjectLine(lines[i % lines.Length]);
        });

        var injector = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref completed) < Total && DateTime.UtcNow < deadline)
            {
                if (Radio.PendingPingCount > 0) AnswerSentinel();
                else Thread.Yield();
            }
        });

        for (int i = 0; i < Total; i++)
            Radio.Ping(ok => { if (ok) Interlocked.Increment(ref completed); }, 0);

        chatter.Start(); injector.Start();
        chatter.Join(); injector.Join();

        Assert.Equal(Total, completed);
        // The interleaved chatter still landed in the mirror:
        Assert.Equal("01600000", Radio.State.RxFrequency.Value);
    }

    [Fact]
    public void Race_CopyOnWriteCollections_SafeToEnumerateWhileTheParserWrites()
    {
        // Verbatim listing lines injected from one thread while another
        // enumerates the snapshots — copy-on-write means no exception and
        // no torn reads.
        ConnectReady();
        Exception? readerError = null;
        bool stop = false;

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    foreach (var a in Radio.State.Ale.SelfAddresses) _ = a.Address;
                    foreach (var a in Radio.State.Ale.IndividualAddresses) _ = a.ChannelGroup;
                    foreach (var line in Radio.State.ChannelList) _ = line.Length;
                }
            }
            catch (Exception ex) { readerError = ex; }
        });

        reader.Start();
        for (int i = 0; i < 500; i++)
        {
            Transport.InjectLine("SLFAD TST               CHGROUP 01");
            Transport.InjectLine($"SLFAD Z{i % 10:00}               CHGROUP 00");
            Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
            Transport.InjectLine("CH 00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        }
        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.Null(readerError);
        Assert.Equal(11, Radio.State.Ale.SelfAddresses.Count);   // TST + Z00..Z09
    }

    // ---- F7: the FIRST init sentinel's own, shorter timeout -----------------
    //
    // plan-clone-field-round2.md §3.5, decision A-3. Three rows, one test each —
    // the plan's accounting table, verbatim, so a future change to the knob has
    // to face all three cases rather than the convenient one. The EXISTING Q3
    // tests above are untouched: this is a smaller number on the timeout path
    // they already describe, not a new mechanism.

    /// <summary>ROW 1 — the first sentinel is answered promptly. Identical to
    /// before the knob existed: Ready at that answer, and the stream in step.</summary>
    [Fact]
    public void F7Row1_FirstInitSentinelAnsweredPromptly_ReadyAtTheAnswer_NoDebt()
    {
        Radio.InitializationTimeoutMs = 20_000;      // half = 10 s, so the knob is what bounds the head
        Connect();
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
        Assert.Equal(0, Radio.PingAnswerDebt);

        var started = Environment.TickCount64;
        AnswerSentinel();

        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.True(Environment.TickCount64 - started < 500,
            "Ready did not arrive at the answer itself");
        // The head answered and the SECOND sentinel is now on the wire — a
        // sentinel legitimately in flight is not a debt.
        Assert.Equal(0, Radio.PingAnswerDebt);
        Assert.Equal(2, Transport.CountSent("BAT ST"));
    }

    /// <summary>ROW 2 — the first sentinel is truly SWALLOWED (the captured
    /// failure: connecting outside SSB loses one init command, protocol.md).
    /// The second dispatches on the SHORT knob rather than on half the init
    /// window, so Ready arrives at ~1.5 s + RTT instead of ~5 s. The debt
    /// sequence is the doctrine's, unchanged: the dead sentinel's answer is
    /// owed until something discards it.</summary>
    [Fact]
    public void F7Row2_FirstInitSentinelSwallowed_ReadyOnTheShortKnob_WithTheDoctrineDebt()
    {
        Radio.InitializationTimeoutMs = 20_000;      // half = 10 s — the OLD behaviour's bound
        Radio.FirstInitSentinelTimeoutMs = 300;      // the knob, compressed for the test
        var started = Environment.TickCount64;
        Connect();

        // Nothing answers. The head times out on the KNOB and the second
        // sentinel goes out — anti-vacuity for the whole test.
        Assert.True(WaitUntil(() => Transport.CountSent("BAT ST") == 2, 3_000),
            "the second init sentinel was never dispatched");
        long dispatched = Environment.TickCount64 - started;
        Assert.True(dispatched < 5_000,
            $"the second sentinel waited {dispatched} ms — half the init window, not the knob");
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
        Assert.Equal(1, Radio.PingAnswerDebt);       // the swallowed sentinel's answer is owed

        AnswerSentinel();                            // the SECOND sentinel's own answer
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.True(Environment.TickCount64 - started < 5_000,
            "Ready still took as long as half the init window");
        Assert.Equal(1, Radio.PingAnswerDebt);       // …and the shift still stands
    }

    /// <summary>ROW 3 — the first sentinel answers LATE, after the knob but
    /// before half the init window. The head has already timed out, so the late
    /// answer credits the SECOND sentinel and Ready arrives AT THAT MOMENT —
    /// exactly when it would have without the knob. What it costs is the
    /// transient debt Q3 already documents: <c>0 → 1 → 1 → 0</c>, self-clearing
    /// when the second sentinel's own answer is discarded.
    /// <para><b>Qualification, stated because the accounting depends on it</b>
    /// (plan F7 table, critic pass 3): this sequence is the one with NO
    /// INTERVENING SENTINEL. If another producer queues one between the head's
    /// timeout and the late answer, that sentinel inherits the shifted credit
    /// and the debt clears only when the eventual surplus answer arrives — the
    /// chain <c>PingAnswerDebt_CountsWhatTheCreditShift_CostsTheStream</c>
    /// already describes. Ready arrives at the late answer either way.</para></summary>
    [Fact]
    public void F7Row3_FirstInitSentinelAnsweredLate_ReadyAtTheLateAnswer_DebtZeroOneOneZero()
    {
        Radio.InitializationTimeoutMs = 20_000;      // half = 10 s: the late answer is well inside it
        Radio.FirstInitSentinelTimeoutMs = 200;
        Connect();
        Assert.Equal(0, Radio.PingAnswerDebt);                       // 0

        Assert.True(WaitUntil(() => Transport.CountSent("BAT ST") == 2, 3_000),
            "the head never timed out, so nothing late can be reproduced");
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
        Assert.Equal(1, Radio.PingAnswerDebt);                       // → 1

        AnswerSentinel();       // the LATE answer to the head — credits the second
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.Equal(1, Radio.PingAnswerDebt);                       // → 1 (still shifted)

        AnswerSentinel();       // the second's own answer, now surplus — discarded
        Assert.Equal(0, Radio.PingAnswerDebt);                       // → 0, back in step
    }

    /// <summary>
    /// <b>THE SHIPPED NUMBER.</b> Every row above compresses the knob so the
    /// test can run in milliseconds, which means none of them can tell whether
    /// the PRODUCTION default shortens anything at all — audit round 1 raised it
    /// to 5 000 and the whole file stayed green.
    ///
    /// <para>So this one leaves the knob alone. It asserts the default is
    /// 1 500 ms, that at 9600 baud that is strictly less than the half-window
    /// the second sentinel keeps (the automatic init timeout is 10 s there), and
    /// — the part no arithmetic can fake — that the second sentinel really is
    /// DISPATCHED on the short bound: it goes out well inside the half-window,
    /// against a radio that answers nothing. At the old shared timeout it would
    /// not appear for five seconds.</para>
    /// </summary>
    [Fact]
    public void F7_TheProductionDefault_IsFifteenHundred_AndReallyShortensTheConnect()
    {
        Assert.Equal(1_500, Radio.FirstInitSentinelTimeoutMs);

        Radio.InitializationTimeoutMs = 0;          // automatic: 10 s at 9600
        var started = Environment.TickCount64;
        Radio.Connect(new PortSettings { PortName = "TEST", BaudRate = 9600 });

        Assert.Equal(10_000, Radio.EffectiveInitializationTimeoutMs);
        int half = Radio.EffectiveInitializationTimeoutMs / 2;
        Assert.True(Radio.FirstInitSentinelTimeoutMs < half,
            $"the shipped knob ({Radio.FirstInitSentinelTimeoutMs} ms) does not shorten "
            + $"the first sentinel's {half} ms half-window");

        // Nothing answers. The second sentinel is the observable.
        Assert.True(WaitUntil(() => Transport.CountSent("BAT ST") == 2, half),
            "the second init sentinel never went out inside half the init window");
        long dispatched = Environment.TickCount64 - started;
        Assert.True(dispatched < half,
            $"the second sentinel waited {dispatched} ms — the half-window, not the shipped knob");
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
    }

    /// <summary>The knob can only make the FIRST sentinel quicker. A shortened
    /// init window keeps both sentinels inside it — Math.Min, not a raw
    /// substitution — so a caller who asked for a fast failure still gets one.</summary>
    [Fact]
    public void F7_TheKnobNeverLengthensASHORTENEDInitWindow()
    {
        Radio.InitializationTimeoutMs = 600;          // half = 300 ms, shorter than the 1.5 s default
        Radio.FirstInitSentinelTimeoutMs = 1_500;
        var started = Environment.TickCount64;
        Connect();

        Assert.True(WaitUntil(() => Transport.CountSent("BAT ST") == 2, 2_000),
            "the second init sentinel was never dispatched");
        Assert.True(Environment.TickCount64 - started < 1_400,
            "the head waited on the knob rather than on the shorter half-window");
    }

    // ---- ROUND 15 A0: the sentinel's clock runs from the WIRE ---------------
    //
    // plan-round15.md §13.4 H3, gates (1)-(3). SendLine only ENQUEUES, and
    // behind the prompt gate the head's BAT ST can sit for seconds: P8
    // measured the first one of a connect writable at 2 251 ms against the
    // 1 500 ms knob. Rows 1-3 above cannot see any of this, because their fake
    // writes at enqueue — so these three rows drive the fake's WriteGate,
    // which is the only thing in the suite that separates enqueue time from
    // wire time.

    /// <summary>ROW 4 — the first init sentinel's BAT ST is held behind the
    /// SH and PORT_R answers, exactly as the bench measured it. It must NOT
    /// time out while it is still queued: nothing has asked the radio
    /// anything yet. Ready arrives on its own answer, the stream stays in
    /// step, and a third sentinel queued meanwhile completes on ITS OWN
    /// answer rather than on somebody else's.</summary>
    [Fact]
    public void F7Row4_TheHeldFirstInitSentinel_DoesNotTimeOutWhileItIsStillQueued()
    {
        Radio.InitializationTimeoutMs = 20_000;      // half = 10 s
        Radio.FirstInitSentinelTimeoutMs = 300;      // the knob, compressed as rows 2-3 compress it
        bool? third = null;

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Connect();
            Assert.Equal(1, Transport.CountSent("BAT ST"));   // single outstanding: the head only
            Radio.Ping(ok => third = ok, 0);                  // queued while the wire is still busy

            // THREE knobs' worth of a radio that has not been asked anything.
            Thread.Sleep(900);
            Assert.Equal(ConnectionState.Initializing, Radio.Connection);
            Assert.Equal(1, Transport.CountSent("BAT ST"));
            Assert.Equal(0, Radio.PingAnswerDebt);
        }
        finally { Transport.ReleaseWrites(); }

        // The head's BAT ST is on the wire NOW — and only now does its clock
        // start. Its own answer is what makes Ready.
        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.Equal(0, Radio.PingAnswerDebt);
        Assert.Null(third);

        AnswerSentinel();                 // init sentinel #2's own answer
        Assert.Null(third);
        AnswerSentinel();                 // …and the third's own
        Assert.True(third);
        Assert.Equal(0, Radio.PingAnswerDebt);
    }

    /// <summary>GATE (2), critic F25 — CORRELATION BY SEQUENCE, not by line
    /// text. A bare <c>BAT ST</c> (the status card's, the Console's) written
    /// AHEAD of the head is the same string on the wire; if the head's clock
    /// started on it, the fix would have moved the off-by-one rather than
    /// removed it.</summary>
    [Fact]
    public void AForeignBareBatSt_WrittenAheadOfTheHead_StartsNothing()
    {
        ConnectReady();
        bool? pinged = null;

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Radio.QueryBatteryState();              // the bare line, enqueued first
            Radio.Ping(ok => pinged = ok, 600);     // …then the sentinel
            Assert.Equal(2, Transport.CountSent("BAT ST"));

            Transport.ReleaseOneWrite();            // ONLY the bare line reaches the wire
            Thread.Sleep(900);                      // well past the ping's own 600 ms
            Assert.Null(pinged);                    // so it never started the ping's clock

            Transport.ReleaseOneWrite();            // the ping's OWN write — the clock starts here
            AnswerSentinel();
            Assert.True(pinged);                    // completed by its answer, not timed out
        }
        finally { Transport.ReleaseWrites(); }
    }

    /// <summary>
    /// THE STRAY RULE, in the suite the rest of the ping queue lives in
    /// (bench 2026-08-22): an answer that arrives while the head is still
    /// ENQUEUED cannot be its answer — the radio has not been asked yet — so
    /// it completes nothing, credits nothing and owes nothing. The head then
    /// waits for its OWN answer, after its own write.
    /// </summary>
    [Fact]
    public void AnAnswerBeforeTheHeadsWrite_IsSTRAY_AndTheHeadStillWaitsForItsOwn()
    {
        ConnectReady();
        bool? pinged = null;

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Radio.Ping(ok => pinged = ok, 0);        // enqueued, unwritten
            AnswerSentinel();                        // …and an answer arrives anyway

            Assert.Null(pinged);                     // it cannot be this ping's
            Assert.Equal(1, Radio.StrayBatteryAnswers);
            Assert.Equal(0, Radio.PingAnswerDebt);   // …and it owes nobody anything
        }
        finally { Transport.ReleaseWrites(); }       // now the ping is really asked

        AnswerSentinel();                            // …and this is ITS answer
        Assert.True(pinged);
        Assert.Equal(0, Radio.PingAnswerDebt);
        Assert.Equal(1, Radio.StrayBatteryAnswers);
    }

    /// <summary>
    /// AUDIT ROUND 1 — THE LATE-ANSWER SHIFT, UNDER THE WIRE CLOCK.
    ///
    /// <para>A0 removed the shift a QUEUED sentinel used to manufacture (row 4
    /// above). It does not — and must not — remove the one the doctrine
    /// accepts: a sentinel that really was ASKED and really was not answered
    /// in time still times out, and its late answer still credits the next
    /// ping early, with <c>PingAnswerDebt</c> saying so the whole time. That
    /// is the case a slow radio produces, and the case every caller that
    /// judges a read by its own sentinel has to survive (the campaign-level
    /// half of this lives in <c>CloneServiceTests</c>).</para>
    ///
    /// <para>The write gate is what separates the two: the clock starts only
    /// when the line is released, so the timeout here is measured from the
    /// wire and nothing about it is a race.</para>
    /// </summary>
    [Fact]
    public void ALateAnswerToAWRITTENSentinel_StillCreditsTheNextPing_AndTheDebtSaysSo()
    {
        ConnectReady();
        bool? first = null, second = null;

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Radio.Ping(ok => first = ok, 200);       // its BAT ST is HELD…
            Radio.Ping(ok => second = ok, 0);        // …and this one waits behind it
            Thread.Sleep(500);
            Assert.Null(first);                      // A0: a queued line has no clock

            Transport.ReleaseOneWrite();             // NOW it reaches the wire
        }
        finally { Transport.ReleaseWrites(); }

        // The radio says nothing, so the head times out — measured from the
        // write, which is the only thing that changed about it.
        Assert.True(WaitUntil(() => first is not null, 3_000), "the written head never timed out");
        Assert.False(first);
        Assert.Equal(1, Radio.PingAnswerDebt);       // the dead sentinel's answer is owed

        AnswerSentinel();                            // …and arrives LATE
        Assert.True(second);                         // crediting the NEXT ping early — accepted
        Assert.Equal(1, Radio.PingAnswerDebt);       // …at the cost of a standing shift
        AnswerSentinel();                            // the second's own answer, now surplus
        Assert.Equal(0, Radio.PingAnswerDebt);       // discarded against an empty queue: in step
    }

    /// <summary>
    /// AUDIT ROUND 1, BLOCKER — A REPORT FROM A DEAD SESSION ARMS NOTHING.
    ///
    /// <para>The write sequence restarts at 1 on every open, so a sequence
    /// ALONE is not an identity: the previous session's writer can report a
    /// line while the new session has already issued one with the SAME
    /// number. Correlating on the number alone would arm the new head's clock
    /// before its <c>BAT ST</c> had been written at all — which is exactly the
    /// premature timeout A0 exists to remove, reintroduced through the back
    /// door of a reconnect.</para>
    /// </summary>
    [Fact]
    public void AWriteReportFromTheDEADSession_DoesNotArmTheNewSessionsHead()
    {
        Radio.InitializationTimeoutMs = 20_000;      // half = 10 s
        Radio.FirstInitSentinelTimeoutMs = 300;      // the knob, compressed
        ConnectReady();                              // session 1, drained
        Radio.Disconnect();
        Transport.ClearSent();

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Connect();                               // session 2, nothing written yet
            Assert.Equal(1, Transport.CountSent("BAT ST"));

            // The head's own number, taken from the fake rather than assumed:
            // every enqueued line takes the next sequence, so the last line of
            // the connect ritual — the first init sentinel — carries this one.
            long headSequence = Transport.SentLines.Count;

            // The DEAD session's writer, draining, reports the same number.
            Transport.InjectLineWritten(session: 1, sequence: headSequence, "BAT ST");
            Thread.Sleep(700);                       // more than twice the knob

            Assert.Equal(ConnectionState.Initializing, Radio.Connection);
            Assert.Equal(1, Transport.CountSent("BAT ST"));   // no timeout: nothing armed

            // ANTI-VACUITY: the SAME number under the LIVE session does arm
            // it, and then the knob expires and the second sentinel goes out.
            Transport.InjectLineWritten(session: 2, sequence: headSequence, "BAT ST");
            Assert.True(WaitUntil(() => Transport.CountSent("BAT ST") == 2, 3_000),
                "the live session's own report did not arm the head's clock");
        }
        finally { Transport.ReleaseWrites(); }
    }

    /// <summary>GATE (3), critic F27 — the never-written entry. A sentinel
    /// whose BAT ST is still queued when the session closes has no clock and
    /// never will have one, so <see cref="Prc138Radio.Disconnect"/>'s
    /// terminal path is what owes it its callback — exactly once. A
    /// LineWritten arriving afterwards (the writer loop draining) finds no
    /// entry and starts nothing.</summary>
    [Fact]
    public void APingWhoseWriteNeverHappened_IsCompletedFalseOnce_ByTheClose()
    {
        ConnectReady();
        int calls = 0;
        bool? result = null;

        Transport.WriteGate = new ManualResetEventSlim(false);
        try
        {
            Radio.Ping(ok => { calls++; result = ok; }, 300);
            Thread.Sleep(700);              // held: no wire, no clock, no timeout
            Assert.Equal(0, calls);

            Radio.Disconnect();
            Assert.Equal(1, calls);
            Assert.False(result);
        }
        finally { Transport.ReleaseWrites(); }

        Thread.Sleep(400);
        Assert.Equal(1, calls);             // the late write completed nothing, started nothing
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
