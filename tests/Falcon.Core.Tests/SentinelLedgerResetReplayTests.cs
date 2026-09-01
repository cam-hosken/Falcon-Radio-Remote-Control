using Falcon.Core.Radio;
using Falcon.Core.Tests.Transport;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// D20 (plan-clone-write-structural.md §2, owner report 2026-08-30 — "close and
/// open the app each time? I get 50% failures on both android and windows"):
/// <see cref="Prc138Radio.ResetSentinelLedger"/>, replayed through the SAME
/// stack the round-15 A0 pins and P0's window script use — the production
/// <see cref="SerialTransport"/> over the byte-injecting port, with a write gate
/// only a PROMPT releases.
///
/// <para><b>Why that stack and not a fake.</b> The whole subject is what the
/// ledger reads while a sentinel is QUEUED-BUT-UNWRITTEN, and a transport that
/// wrote at enqueue could not express it. These pins are deliberately the
/// <see cref="StrayBatteryAnswerReplayTests"/> idiom, line for line, so the
/// reset is measured against the same window shapes those files already
/// characterise — and neither of those files is touched.</para>
///
/// <para><b>What is NOT pinned here, and why.</b> The transport-REFUSED head
/// (<c>onWire == 0</c> with an entry still queued: the connection closed under a
/// waiting sentinel) needs a close mid-queue, which the terminal path
/// <c>ClearPendingPings</c> empties on its way through — so it is unreachable
/// from this seam and is carried by the derivation in the doc comment instead.
/// Everything else the derivation claims is below.</para>
/// </summary>
public sealed class SentinelLedgerResetReplayTests : IDisposable
{
    private const string Battery = "Battery Status FULL 26.2V";

    private readonly FakeSerialPort _port = new();
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;

    public SentinelLedgerResetReplayTests()
    {
        // Only a prompt releases a write (no timeout fallback inside the test's
        // lifetime): the wire order is then the test's to state.
        _transport = new SerialTransport(_port) { OpenSettleMs = 0, GateTimeoutMs = 60_000 };
        _radio = new Prc138Radio(_transport, new InlineContext());
    }

    public void Dispose() => _radio.Dispose();

    /// <summary>Bytes as the radio frames them (R1): payload lines, then a bare
    /// mode prompt — which is also what releases the write gate.</summary>
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

    /// <summary>Release one queued write with a prompt and wait for it to reach
    /// the port.</summary>
    private void ReleaseOneWrite(int expectedTotal)
    {
        InjectPrompt();
        Assert.True(_port.WaitForWrites(expectedTotal, 3_000),
            $"write #{expectedTotal} never reached the port");
    }

    /// <summary>Connect and drain BOTH init sentinels, one prompt-released write
    /// at a time, so the queue is empty and the stream is in step — the shared
    /// opening of the two replay files this one sits beside.</summary>
    private void ConnectReady()
    {
        _radio.Connect(new PortSettings { PortName = "FAKE", BaudRate = 9600 });

        // The ritual: "" "" PORT_R ECHO OFF ×2, SH, PORT_R, POW, BAT ST — EIGHT
        // writes. The first goes immediately (nothing in flight); the rest need a
        // prompt each.
        Assert.True(_port.WaitForWrites(1, 3_000), "the ritual's first line never went out");
        for (int write = 2; write <= 8; write++) ReleaseOneWrite(write);
        Assert.Equal(1, Written("BAT ST"));

        Inject(Battery);                                    // init sentinel #1 answers
        Assert.True(WaitUntil(() => _radio.Connection == ConnectionState.Ready));

        ReleaseOneWrite(9);                                 // sentinel #2's own write
        Assert.Equal(2, Written("BAT ST"));
        Inject(Battery);                                    // …and its own answer
        Assert.True(WaitUntil(() => _radio.PendingPingCount == 0));
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.StrayBatteryAnswers);
    }

    /// <summary>
    /// A STANDING DEBT, manufactured at the wire: a sentinel is WRITTEN and the
    /// radio never answers it (R6 — it really does swallow commands), so it times
    /// out on its own accepted-write clock and the answer it counted is owed for
    /// ever. Nothing can pay it: the discard that clears a debt needs an answer to
    /// arrive, and this one never will. That is the state a failed campaign left
    /// behind, and the state every in-session retry used to inherit.
    /// </summary>
    private void ManufactureStandingDebt(int writeNumber)
    {
        bool? swallowed = null;
        _radio.Ping(ok => swallowed = ok, 150);
        ReleaseOneWrite(writeNumber);                       // its own BAT ST goes out…
        Assert.True(WaitUntil(() => swallowed is not null),
            "the written sentinel never timed out — the fixture did not manufacture a debt");
        Assert.False(swallowed);                            // …and nothing ever answers it

        Assert.Equal(1, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
    }

    /// <summary>
    /// D20 (a) — THE INHERITED DEBT IS GONE, AND NOTHING ELSE MOVED. A standing
    /// debt is manufactured, the ledger is re-baselined with an EMPTY queue
    /// (<c>onWire == 0</c>: <c>0 - 0 - 0 = 0</c>), and the very next sentinel runs
    /// clean — which is what a retry could not do before.
    /// </summary>
    [Fact]
    public void AStandingDebt_IsClearedByTheReset_AndTheNextSentinelRunsClean()
    {
        ConnectReady();
        ManufactureStandingDebt(10);
        Assert.Equal(3, Written("BAT ST"));

        _radio.ResetSentinelLedger();

        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.StrayBatteryAnswers);
        Assert.Equal(0, _radio.PendingPingCount);           // preserved: it was empty
        Assert.Equal(3, Written("BAT ST"));                 // the reset SENDS nothing

        // The retry the owner had to restart the app for.
        bool? retry = null;
        _radio.Ping(ok => retry = ok, 0);
        ReleaseOneWrite(11);
        Assert.Equal(4, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => retry is not null));
        Assert.True(retry);
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
    }

    /// <summary>
    /// D20 (b) — A PING IN FLIGHT ACROSS THE RESET COMPLETES NORMALLY. This is the
    /// constraint the arithmetic exists for: the campaign's own opening barrier can
    /// already be on the wire when the reset runs, and it must neither be cancelled
    /// nor double-counted. <c>onWire == 1</c>, so the ledger reads
    /// <c>1 - 0 - 1 = 0</c> at the call and <c>1 - 1 - 0 = 0</c> at its answer.
    /// </summary>
    [Fact]
    public void APingOnTheWireAcrossTheReset_StillAnswersItsOwnCallback_AndOwesNothingAfter()
    {
        ConnectReady();
        ManufactureStandingDebt(10);                        // the inherited debt…

        bool? inFlight = null;
        _radio.Ping(ok => inFlight = ok, 0);
        ReleaseOneWrite(11);                                // …and a sentinel now ON THE WIRE
        Assert.Equal(4, Written("BAT ST"));
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(1, _radio.PingAnswerDebt);             // the swallowed one is still owed

        _radio.ResetSentinelLedger();

        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(1, _radio.PendingPingCount);           // NOT cancelled…
        Assert.Null(inFlight);                              // …and NOT completed
        Assert.Equal(4, Written("BAT ST"));                 // …and nothing re-sent

        Inject(Battery);                                    // its own answer, as if nothing happened
        Assert.True(WaitUntil(() => inFlight is not null));
        Assert.True(inFlight);
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(0, _radio.StrayBatteryAnswers);

        // THE ANTI-MASK CHECK, and the mutation discriminator for `onWire`
        // (verified by mutation 2026-08-30 and reverted — production is
        // byte-untouched). `PingAnswerDebt` CLAMPS at 0, so a reset that failed
        // to count the head's own send would read 0 here just the same and only
        // show up later, as an un-owed CREDIT that silently absorbs the next real
        // debt. Manufacturing one proves the ledger is level rather than ahead:
        // with `_sentinelsSent = 0` at the reset this reads 0 and the pin fails.
        ManufactureStandingDebt(12);
    }

    /// <summary>
    /// D20 (b), the SECOND-ENTRY arm: the reset lands with TWO entries queued —
    /// the head on the wire, one waiting behind it. Only the head is counted
    /// (single-outstanding), so the waiting entry's own dispatch counts its own
    /// send and the ledger walks <c>1 - 0 - 1</c> → <c>2 - 1 - 1</c> → <c>2 - 2 -
    /// 0</c>, reading 0 at every step. Both callbacks run, in order, exactly once.
    /// </summary>
    [Fact]
    public void TwoQueuedPingsAcrossTheReset_BothAnswerInOrder_AndTheLedgerStaysInStep()
    {
        ConnectReady();
        bool? head = null, waiting = null;

        _radio.Ping(ok => head = ok, 0);
        _radio.Ping(ok => waiting = ok, 0);                 // queued behind: nothing enqueued for it
        ReleaseOneWrite(10);                                // the HEAD's own write
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(2, _radio.PendingPingCount);

        _radio.ResetSentinelLedger();
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(2, _radio.PendingPingCount);

        Inject(Battery);                                    // the head's answer dispatches the next
        Assert.True(WaitUntil(() => head is not null));
        Assert.True(head);
        Assert.Null(waiting);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);             // 2 - 1 - 1

        ReleaseOneWrite(11);                                // the waiting entry's own write
        Assert.Equal(4, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => waiting is not null));
        Assert.True(waiting);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);             // 2 - 2 - 0
        Assert.Equal(0, _radio.StrayBatteryAnswers);

        // THE ANTI-MASK CHECK (see the in-flight pin): the ledger must be LEVEL,
        // not one credit ahead — which only a real debt can show, because the
        // debt reading clamps at 0.
        ManufactureStandingDebt(12);
    }

    /// <summary>
    /// D20 (c) — THE ROUND-15 A0 STRAY RULE STILL HOLDS ACROSS THE RESET, and
    /// THE <c>Sequence</c> ARM OF <c>onWire</c> IS WHAT MAKES IT ADD UP.
    ///
    /// <para><b>The window, precisely</b> (audit round 1 corrected this summary,
    /// which used to claim <c>onWire</c> was 0 here). The head has been ACCEPTED
    /// by <see cref="SerialTransport"/> — <c>DispatchHeadLocked</c> got a non-zero
    /// sequence back and stored it, and <c>SendLine</c> counted the send — but the
    /// writer is still behind the prompt gate, so <c>WriteStarted</c> is FALSE.
    /// That is the ONE state in which the two arms of <c>onWire</c> disagree:
    /// <c>WriteStarted</c> says no, <c>Sequence != 0</c> says yes, and
    /// <c>Sequence</c> is right, because the ledger already counted that line.
    /// So <c>onWire</c> is 1 and the ledger reads <c>1 - 0 - 1 = 0</c>.</para>
    ///
    /// <para>The mode entry's EXTRA <c>Battery Status</c> then arrives before that
    /// head's <c>BAT ST</c> has been written: it credits nobody, raises no debt,
    /// and the head still waits for its OWN answer — which is exactly what the
    /// empty clone file came from.</para>
    ///
    /// <para><b>The tail is not decoration</b> (audit round 1, MINOR): without it
    /// this pin passed with the <c>Sequence != 0</c> arm DELETED, because dropping
    /// a counted send only ever makes the ledger run one credit AHEAD, and
    /// <see cref="Prc138Radio.PingAnswerDebt"/> clamps at 0. The masked debt is
    /// the observable, so the pin has to go and manufacture one.</para>
    /// </summary>
    [Fact]
    public void AResetWhileTheHeadIsUnwritten_LeavesTheStrayRuleIntact()
    {
        ConnectReady();
        bool? leg = null;

        _radio.Ping(ok => leg = ok, 0);                     // its BAT ST is behind the prompt gate
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(2, Written("BAT ST"));                 // queued, NOT written

        // 1 - 0 - 1: the accepted-but-unwritten head is counted by the
        // `Sequence != 0` arm, exactly as SendLine already counted its line.
        _radio.ResetSentinelLedger();
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(1, _radio.PendingPingCount);

        Inject(Battery);                                    // the entry's EXTRA line
        Assert.True(WaitUntil(() => _radio.StrayBatteryAnswers == 1));
        Assert.Null(leg);                                   // credited nobody…
        Assert.Equal(0, _radio.PingAnswerDebt);             // …and owes nobody anything
        Assert.Equal(2, Written("BAT ST"));                 // …and sent nothing new

        ReleaseOneWrite(10);                                // now the head is really asked…
        Assert.Equal(3, Written("BAT ST"));
        Assert.Null(leg);                                   // …still waiting for ITS answer

        Inject(Battery);                                    // …which is this one
        Assert.True(WaitUntil(() => leg is not null));
        Assert.True(leg);
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(1, _radio.StrayBatteryAnswers);        // the double, absorbed as always

        // THE ANTI-MASK CHECK, and the mutation discriminator for the
        // `Sequence != 0` arm (audit round 1, MINOR — verified by mutation
        // 2026-08-30 and reverted; production is byte-untouched). Everything
        // above passes with that arm deleted, because the only damage it does is
        // to leave the ledger one credit AHEAD, and the debt reading clamps at 0.
        // A subsequent SWALLOWED sentinel is what makes the difference visible:
        // level → debt 1, ahead → the un-owed credit absorbs it and debt reads 0,
        // which is the masked debt this pin now refuses.
        ManufactureStandingDebt(11);
    }

    /// <summary>
    /// D20's NON-GOAL, pinned so it cannot be quietly widened (the plan's own
    /// NOTE): the reset removes debt INHERITANCE, not debt. A sentinel swallowed
    /// AFTER the reset mints its debt exactly as before — the root 50% race is
    /// still visible to the gate that has to refuse it.
    /// </summary>
    [Fact]
    public void ADebtMintedAFTERTheReset_StillStands()
    {
        ConnectReady();
        ManufactureStandingDebt(10);

        _radio.ResetSentinelLedger();
        Assert.Equal(0, _radio.PingAnswerDebt);

        ManufactureStandingDebt(11);                        // …and the next one is the campaign's own
        Assert.Equal(1, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
    }
}
