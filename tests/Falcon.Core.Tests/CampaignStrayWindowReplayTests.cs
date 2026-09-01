// =====================================================================
// P0 RECORDED VERDICT (plan-clone-write-structural.md §7 P0 step 3):
// **GREEN-ALL** on current code, 2026-08-29, all three enumerated
// interleavings. Every barrier point and every window end reads
// PingAnswerDebt == 0 and PendingPingCount == 0; the only ledger motion
// the producer collision produces is StrayBatteryAnswers == 1 — the mode
// entry's own doubled Battery Status, absorbed exactly as round 15's A0
// rule intends. No interleaving in the enumerated set reaches the gate's
// debt branch (AleProgrammingGate.cs:316) at all.
//
// Verbatim, `dotnet test tests/Falcon.Core.Tests/Falcon.Core.Tests.csproj
// --filter "FullyQualifiedName~CampaignStrayWindowReplayTests"`:
//
//   Test run for …\Falcon.Core.Tests.dll (.NETCoreApp,Version=v10.0)
//   A total of 1 test files matched the specified pattern.
//
//   Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 483 ms - Falcon.Core.Tests.dll (net10.0)
//
// Nothing is skipped: with no RED case there is no red evidence to park.
// Per §7's disposition, GREEN-ALL is a RECORDED DECISION (D2 dropped),
// not a stop — and these three stay as standing regression pins, because
// D1's quiesce and D3's gate hardening must not change what they assert.
//
// WHY it is green, and what that does NOT say (the arithmetic, verified
// by mutation on 2026-08-29 and reverted — production is byte-untouched):
//   * `_sentinelsSent` is incremented in `Prc138Radio.SendLine` (:1612),
//     i.e. at ENQUEUE, in the very call `DispatchHeadLocked` uses (:273).
//     §1's premise that it "counts only WRITTEN lines" is not what the
//     code does. The queued-but-unwritten head is therefore counted AND
//     subtracted as `inFlight` (:204), and the two cancel — which is
//     exactly why the derived debt does not skew in the stray window.
//     Mutating `inFlight` to the arithmetic §1 feared (subtract only for
//     a WRITTEN head) turns all three RED at the first queued-unwritten
//     step, debt 1 — so §1's worry was well-posed; the current
//     expression is what answers it.
//   * Because the ping queue is SINGLE-OUTSTANDING, a `BAT ST` is
//     enqueued only for the queue's head, so `_sentinelsSent` can never
//     run more than one ahead of the credits while pings alone are in
//     play. Debt in this ledger needs a send that is counted but whose
//     answer is never credited — i.e. a TIMED-OUT sentinel, or a BARE
//     `BAT ST` sent outside the ping queue (`QueryBatteryState` /
//     `RawCommand`). NONE of the three enumerated producers issues
//     either: the modem, settings and channel reads all sentinel through
//     `Prc138Radio.Ping` (SsbController.cs:430/436, AleController.cs:440)
//     and the `DI` fan-out sentinels not at all.
//   * So this GREEN is a statement about the ENUMERATED THREE, and not
//     about the whole §4 inventory: §4 row 13 (`DeviceSettingsViewModel`,
//     `LIG INT CONT BAT ST TI`) sends a BARE `BAT ST`, which §1 point 3
//     already names as the phase-shift widener. It is outside P0's set
//     and is NOT tested here.
// =====================================================================

using Falcon.Core.Radio;
using Falcon.Core.Tests.Transport;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// P0 EVIDENCE SCRIPT (plan-clone-write-structural.md §7 P0 steps 2-3): the
/// WRITE CAMPAIGN's book-leg bracket with ONE campaign-blind producer read
/// interleaved, so a <c>Battery Status</c> line lands in the §1 STRAY WINDOW —
/// the moment the queue's head sentinel is QUEUED-BUT-UNWRITTEN.
///
/// <para><b>The three interleavings are the plan's enumerated set</b> (§7 P0
/// step 2, from the §4 producer inventory): the <c>MODEM PRE</c> burst
/// (<c>ModemPresetsViewModel</c>), the first-ALE <c>SH</c> settings read
/// (<c>AleSettingsViewModel</c>), and the <c>DI n n</c> fan-out
/// (<c>LqaViewModel</c>/<c>SsbChannelEditorViewModel</c>). Nothing outside
/// that set is scripted here.</para>
///
/// <para><b>Wire text is copied, the INTERLEAVING is synthesized</b> — which is
/// what §3a-0 says P0's fixture is. Every TX/RX line below comes from a
/// committed capture: <c>bench/transcripts/field-read-20260829-095608.txt</c>
/// (the 2026-08-29 Android read — the <c>MODEM PRE</c> burst at 09:54:22-23,
/// the first-ALE <c>SH</c>+<c>BAT ST</c> at 09:54:42.818, the <c>IN_PROG</c> /
/// <c>SCANNING</c> chatter), plan §3 windows A/B (the self-less
/// <c>PRG 1-3 CHAR SLF</c> banner and the DOUBLED <c>Battery Status</c>), and
/// <c>bench/transcripts/field-clone-console-20260820-1738.txt</c> (the
/// <c>DI</c> read and its <c>CH nn</c> answers). The gated write line is
/// §3a-0's instrumented <c>SLFAD STO HOS</c>.</para>
///
/// <para><b>Every <c>BAT ST</c> is mapped to its sender in the step comments</b>
/// — campaign barrier, campaign closing sentinel, or producer sentinel. A TX
/// line alone never decides that (§3a); the mapping is the script's, from the
/// app's send sites.</para>
///
/// <para><b>This is a CHARACTERIZATION script.</b> Each step asserts what the
/// CURRENT ledger actually does, not what the arithmetic "ought" to say. The
/// stack below Core is the production one — <see cref="SerialTransport"/> over
/// the byte-injecting port, released only by a PROMPT — because
/// "queued-but-unwritten" is the whole subject and a fake that wrote at enqueue
/// could not express it. Nothing here reflects into private state; the ledger
/// is read through <see cref="Prc138Radio.PingAnswerDebt"/>,
/// <see cref="Prc138Radio.PendingPingCount"/> and
/// <see cref="Prc138Radio.StrayBatteryAnswers"/> only.</para>
///
/// <para><b>THE §1 SUBTLETY, AS MEASURED (this is the round's finding).</b> §1
/// warns that <c>_sentinelsSent</c> "counts only WRITTEN lines" while
/// <c>PingAnswerDebt</c> subtracts <c>inFlight = _pings.Count > 0 ? 1 : 0</c>,
/// so a queued-but-unwritten head might skew the derived debt by one. The code
/// says otherwise: <c>_sentinelsSent</c> is incremented in
/// <c>Prc138Radio.SendLine</c> (:1612), i.e. at ENQUEUE — the same call in
/// which <c>DispatchHeadLocked</c> hands the line to the transport (:273) — so
/// the unwritten head is counted AND subtracted, and the two cancel. Every
/// assertion below records that measured behaviour: <c>PingAnswerDebt</c> is 0
/// at every step of all three interleavings.</para>
/// </summary>
public sealed class CampaignStrayWindowReplayTests : IDisposable
{
    private const string Battery = "Battery Status FULL 26.2V";

    /// <summary>The gated book-leg write §3a-0's instrument ran on COM20.</summary>
    private const string BookWrite = "SLFAD STO HOS";

    private readonly FakeSerialPort _port = new();
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;

    public CampaignStrayWindowReplayTests()
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
    /// write at a time, so the queue is empty and the stream is in step.
    /// Sender map for the ritual: <c>BAT ST</c> #1 and #2 are Core's own INIT
    /// sentinels (<c>QueueInitSentinels</c>) — no producer and no campaign has
    /// spoken yet.</summary>
    private void ConnectReady()
    {
        _radio.Connect(new PortSettings { PortName = "FAKE", BaudRate = 9600 });

        // The ritual: "" "" PORT_R ECHO OFF ×2, SH, PORT_R, POW, BAT ST — EIGHT
        // writes. The first goes immediately (nothing in flight); the rest need
        // a prompt each.
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

    /// <summary>
    /// INTERLEAVING (a) — the <c>MODEM PRE</c> BURST (<c>ModemPresetsViewModel</c>,
    /// §4 row 1) landing inside the book leg's gate bracket.
    ///
    /// <para>Shape from <c>field-read-20260829-095608.txt</c> 09:54:22-23: the
    /// campaign enters ALE, the card's landing fires its PRESENCE read (bare
    /// <c>MODEM PRE</c> + sentinel) and then, on that sentinel's completion, its
    /// TARGETED read (<c>MODEM PRE 0</c> + sentinel) — the
    /// <c>CompleteModemRead</c> → <c>DispatchModemTargetedRead</c> chain
    /// (SsbController.cs:426-445). The self-less entry's DOUBLED
    /// <c>Battery Status</c> (plan §3 windows A/B) is injected while the
    /// producer's presence sentinel is queued-but-unwritten.</para>
    /// </summary>
    [Fact]
    public void TheModemPresetBurst_InsideTheBookLegsBracket_LeavesTheLedgerInStep()
    {
        ConnectReady();
        bool? barrier1 = null, barrier2 = null, closing = null;
        bool? presence = null, targeted = null;

        // ---- the book leg's row opens its gate bracket ----------------------
        // BAT ST #3 = the CAMPAIGN's OPENING BARRIER (AleProgrammingGate's
        // ArmBarrier → AleController.Synchronize → Prc138Radio.Ping).
        _radio.Ping(ok => barrier1 = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(2, Written("BAT ST"));                 // still queued behind the gate
        // CHARACTERIZATION: the queued-but-unwritten head is COUNTED at enqueue
        // (SendLine :1612) and subtracted as inFlight (:204) — they cancel.
        Assert.Equal(0, _radio.PingAnswerDebt);

        ReleaseOneWrite(10);                                // its own write
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the producer rides in (09:54:22.052 TX MODEM PRE / .054 BAT ST)
        // BAT ST #4 = the PRODUCER's PRESENCE sentinel. It is NOT dispatched:
        // the queue is single-outstanding, so no line is enqueued for it yet.
        _radio.RawCommand("MODEM PRE");
        _radio.Ping(ok => presence = ok, 0);
        Assert.Equal(2, _radio.PendingPingCount);
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(10, _port.WriteCount);                 // MODEM PRE is queued, not written
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the self-less ALE entry's chatter -----------------------------
        Inject("IN_PROG");                                  // 09:54:22.054

        // The FIRST battery line is the campaign barrier's own answer
        // (09:54:22.225, the capture's leading space kept). It completes the
        // barrier and DISPATCHES the producer's sentinel — whose BAT ST is now
        // queued behind the prompt gate, unwritten.
        Inject(" " + Battery);
        Assert.True(WaitUntil(() => barrier1 is not null));
        Assert.True(barrier1);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(3, Written("BAT ST"));                 // #4 is queued, not written

        // *** BARRIER POINT 1 — what AleProgrammingGate reads at :316/:338. ***
        // debt 0 → the "behind on its sentinel answers" branch is NOT taken;
        // pending 1 → the gate takes the BUSY branch and re-arms (:345-356).
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- THE §1 STRAY WINDOW -------------------------------------------
        // The mode entry's EXTRA Battery Status (plan §3 windows A/B: one
        // BAT ST, two answers). The head — the producer's sentinel — has not
        // been written, so it cannot be its answer.
        Inject(Battery);
        Assert.True(WaitUntil(() => _radio.StrayBatteryAnswers == 1));
        Assert.Null(presence);                              // credited nobody
        Assert.Equal(3, Written("BAT ST"));                 // and sent nothing new
        Assert.Equal(0, _radio.PingAnswerDebt);             // …and owes nobody anything

        // ---- the entry's prompts release the producer's own traffic --------
        ReleaseOneWrite(11);                                // MODEM PRE
        Inject("MODEM PRESET 0 DAT0 ASYNC DATA   BAUD 2400  TYPE serial  INTER short");
        Assert.Equal(11, _port.WriteCount);                 // the listing sends nothing
        ReleaseOneWrite(12);                                // BAT ST #4
        Assert.Equal(4, Written("BAT ST"));

        Inject(Battery);                                    // 09:54:23.112 — #4's own answer
        Assert.True(WaitUntil(() => presence is not null));
        Assert.True(presence);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the producer's TARGETED follow-up (09:54:23.086 MODEM PRE 0) --
        // BAT ST #5 = the PRODUCER's TARGETED sentinel.
        _radio.RawCommand("MODEM PRE 0");
        _radio.Ping(ok => targeted = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);

        ReleaseOneWrite(13);                                // MODEM PRE 0
        ReleaseOneWrite(14);                                // BAT ST #5
        Assert.Equal(5, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => targeted is not null));
        Assert.True(targeted);
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the gate's RE-ARMED barrier ----------------------------------
        // BAT ST #6 = the CAMPAIGN's re-armed barrier (the busy branch above).
        _radio.Ping(ok => barrier2 = ok, 0);
        ReleaseOneWrite(15);
        Assert.Equal(6, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => barrier2 is not null));
        Assert.True(barrier2);

        // *** BARRIER POINT 2 — both counters clear, so the gate releases the
        // write (AleProgrammingGate.cs:358-374). ***
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);

        // ---- the write stage: the write and its closing sentinel -----------
        // BAT ST #7 = the CAMPAIGN's CLOSING sentinel.
        _radio.RawCommand(BookWrite);
        _radio.Ping(ok => closing = ok, 0);
        // The gate's adjacency test (SendWriteStage, :431-432).
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);

        ReleaseOneWrite(16);                                // SLFAD STO HOS
        ReleaseOneWrite(17);                                // BAT ST #7
        Assert.Equal(7, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => closing is not null));
        Assert.True(closing);

        // ---- the window's end (plan §6's contract) -------------------------
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(1, _radio.StrayBatteryAnswers);        // the entry's double, absorbed
    }

    /// <summary>
    /// INTERLEAVING (b) — the FIRST-ALE <c>SH</c> SETTINGS READ
    /// (<c>AleSettingsViewModel</c>, §4 row 6) landing inside the book leg's
    /// gate bracket.
    ///
    /// <para>Shape from <c>field-read-20260829-095608.txt</c> 09:54:41-44: the
    /// campaign enters ALE, the entry prints <c>IN_PROG</c>, the campaign's
    /// sentinel is answered, and the settings card's first-ALE landing puts
    /// <c>SH</c>+<c>BAT ST</c> out at 09:54:42.818/.819 — the same pair plan
    /// §3 window A shows at 17:04:24.838 on the SELF-LESS radio, where the
    /// <c>PRG 1-3 CHAR SLF</c> banner and the DOUBLED battery line appear. The
    /// settings block the <c>SH</c> draws is RX-only and ledger-neutral, so it
    /// is not injected.</para>
    /// </summary>
    [Fact]
    public void TheFirstAleSettingsRead_InsideTheBookLegsBracket_LeavesTheLedgerInStep()
    {
        ConnectReady();
        bool? barrier1 = null, barrier2 = null, closing = null, settings = null;

        // ---- the book leg's row opens its gate bracket ----------------------
        // BAT ST #3 = the CAMPAIGN's OPENING BARRIER.
        _radio.Ping(ok => barrier1 = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);
        ReleaseOneWrite(10);
        Assert.Equal(3, Written("BAT ST"));

        // ---- the producer rides in (09:54:42.818 TX SH / .819 TX BAT ST) ---
        // BAT ST #4 = the PRODUCER's first-ALE settings sentinel — queued
        // behind the campaign's, so nothing is enqueued for it yet.
        _radio.RawCommand("SH");
        _radio.Ping(ok => settings = ok, 0);
        Assert.Equal(2, _radio.PendingPingCount);
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(10, _port.WriteCount);                 // SH is queued, not written
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the self-less ALE entry's chatter (window A, 17:04:24) --------
        Inject("IN_PROG");
        Inject(" PRG 1-3 CHAR SLF");                        // the self-less banner

        // The campaign barrier's own answer (window A 17:04:24.794, the
        // capture's leading space kept). It dispatches the producer's sentinel,
        // whose BAT ST is now queued-but-unwritten.
        Inject(" " + Battery);
        Assert.True(WaitUntil(() => barrier1 is not null));
        Assert.True(barrier1);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(3, Written("BAT ST"));

        // *** BARRIER POINT 1: debt 0 (no fault branch); pending 1 (re-arm). ***
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- THE §1 STRAY WINDOW -------------------------------------------
        Inject(Battery);                                    // window A's SECOND battery line
        Assert.True(WaitUntil(() => _radio.StrayBatteryAnswers == 1));
        Assert.Null(settings);                              // credited nobody
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the entry's prompts release the producer's own traffic --------
        ReleaseOneWrite(11);                                // SH
        ReleaseOneWrite(12);                                // BAT ST #4
        Assert.Equal(4, Written("BAT ST"));
        Inject(Battery);                                    // 09:54:44.918 — #4's own answer
        Assert.True(WaitUntil(() => settings is not null));
        Assert.True(settings);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the gate's RE-ARMED barrier ----------------------------------
        // BAT ST #5 = the CAMPAIGN's re-armed barrier.
        _radio.Ping(ok => barrier2 = ok, 0);
        ReleaseOneWrite(13);
        Assert.Equal(5, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => barrier2 is not null));
        Assert.True(barrier2);

        // *** BARRIER POINT 2 — both counters clear: the write is released. ***
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);

        // ---- the write stage ----------------------------------------------
        // BAT ST #6 = the CAMPAIGN's CLOSING sentinel.
        _radio.RawCommand(BookWrite);
        _radio.Ping(ok => closing = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);           // the gate's adjacency test
        Assert.Equal(0, _radio.PingAnswerDebt);

        ReleaseOneWrite(14);                                // SLFAD STO HOS
        ReleaseOneWrite(15);                                // BAT ST #6
        Assert.Equal(6, Written("BAT ST"));
        Inject(Battery);
        Assert.True(WaitUntil(() => closing is not null));
        Assert.True(closing);

        // ---- the window's end (plan §6's contract) -------------------------
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(1, _radio.StrayBatteryAnswers);
    }

    /// <summary>
    /// INTERLEAVING (c) — the <c>DI n n</c> FAN-OUT (<c>LqaViewModel</c> §4 row
    /// 10 / <c>SsbChannelEditorViewModel</c> §4 row 12) landing inside the book
    /// leg's gate bracket.
    ///
    /// <para><b>MEASURED DEVIATION from the P0 brief's "DI n n reads +
    /// sentinel".</b> Neither fan-out sends a sentinel: <c>LqaViewModel</c>
    /// :486-497 and <c>SsbChannelEditorViewModel</c> :584-605 both call
    /// <c>ChannelSurface.RequestChannel</c> (:114) → <c>SsbController</c>
    /// <c>DisplayChannels</c> (:102-109), which sends <c>DI n n</c> and pings
    /// NOTHING. So the fan-out is scripted as it really is — bare command lines
    /// — and the head sentinel sitting queued-but-unwritten in the stray window
    /// is the CAMPAIGN's OWN closing sentinel, wedged behind the producer's
    /// three unwritten <c>DI</c> lines. That is the §1 window with the campaign
    /// itself as the victim, which is the shape this interleaving can actually
    /// produce.</para>
    ///
    /// <para>Wire form from <c>field-clone-console-20260820-1738.txt</c>
    /// 17:39:17.578 (<c>DI</c> and its <c>CH nn</c> answers); the channel
    /// numbers are the fan-out's own parameters.</para>
    /// </summary>
    [Fact]
    public void TheDiFanOut_InsideTheBookLegsBracket_LeavesTheLedgerInStep()
    {
        ConnectReady();
        bool? barrier1 = null, closing = null;

        // ---- the book leg's row opens its gate bracket ----------------------
        // BAT ST #3 = the CAMPAIGN's OPENING BARRIER.
        _radio.Ping(ok => barrier1 = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);
        Assert.Equal(0, _radio.PingAnswerDebt);
        ReleaseOneWrite(10);
        Assert.Equal(3, Written("BAT ST"));

        // ---- the producer rides in: one DI per named LQA channel, no
        // sentinel of its own. All three queue behind the prompt gate.
        _radio.RawCommand("DI 0 0");
        _radio.RawCommand("DI 1 1");
        _radio.RawCommand("DI 2 2");
        Assert.Equal(10, _port.WriteCount);                 // three lines queued, none written
        Assert.Equal(1, _radio.PendingPingCount);           // the fan-out adds NO sentinel
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the self-less ALE entry's chatter -----------------------------
        Inject("IN_PROG");                                  // 09:54:22.054
        Inject("SCANNING");                                 // 09:54:23.106

        // The campaign barrier's own answer. Its queue is now EMPTY — the
        // fan-out queued no sentinel — so the gate sees both counters clear.
        Inject(" " + Battery);
        Assert.True(WaitUntil(() => barrier1 is not null));
        Assert.True(barrier1);

        // *** BARRIER POINT 1 — both counters clear, so the gate releases the
        // write EVEN THOUGH three foreign command lines sit ahead of it in the
        // transport queue. (Characterization, and the §1 collision surface: the
        // ledger cannot see the transport's queue at all.) ***
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);

        // ---- the write stage, issued behind the producer's backlog ---------
        // BAT ST #4 = the CAMPAIGN's CLOSING sentinel — enqueued FIFO behind
        // DI 0 0, DI 1 1, DI 2 2 and the write itself.
        _radio.RawCommand(BookWrite);
        _radio.Ping(ok => closing = ok, 0);
        Assert.Equal(1, _radio.PendingPingCount);           // the gate's adjacency test
        Assert.Equal(0, _radio.PingAnswerDebt);             // …reads CLEAN here
        Assert.Equal(3, Written("BAT ST"));                 // #4 is queued, not written

        // ---- THE §1 STRAY WINDOW -------------------------------------------
        // The entry's EXTRA Battery Status arrives while the campaign's own
        // closing sentinel is queued-but-unwritten.
        Inject(Battery);
        Assert.True(WaitUntil(() => _radio.StrayBatteryAnswers == 1));
        Assert.Null(closing);                               // credited nobody
        Assert.Equal(3, Written("BAT ST"));
        Assert.Equal(0, _radio.PingAnswerDebt);

        // ---- the prompts drain the backlog, in FIFO order -------------------
        ReleaseOneWrite(11);                                // DI 0 0
        Inject("CH 00 RxFr 03967000 TxFr 03967000 MODE LSB AGC ME BA 2.7  RXONLY NO");
        ReleaseOneWrite(12);                                // DI 1 1
        Inject("CH 01 RxFr 51500000 TxFr 51500000 MODE FM  AGC SL BA 2.7  RXONLY NO");
        ReleaseOneWrite(13);                                // DI 2 2
        Inject("CH 02 RxFr 51000000 TxFr 51000000 MODE FM  AGC SL BA 2.7  RXONLY NO");
        Assert.Equal(13, _port.WriteCount);                 // the answers send nothing
        Assert.Equal(3, Written("BAT ST"));

        ReleaseOneWrite(14);                                // SLFAD STO HOS
        ReleaseOneWrite(15);                                // BAT ST #4
        Assert.Equal(4, Written("BAT ST"));
        Assert.Null(closing);                               // still waiting for ITS answer
        Assert.Equal(0, _radio.PingAnswerDebt);

        Inject(Battery);                                    // …which is this one
        Assert.True(WaitUntil(() => closing is not null));
        Assert.True(closing);

        // ---- the window's end (plan §6's contract) -------------------------
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(1, _radio.StrayBatteryAnswers);
    }
}
