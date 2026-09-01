using Falcon.App.Core.Surfaces;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE programming gate (plan-ale-programming.md §4.3) over the REAL
/// stack: AleSurface on a real Prc138Radio and the injecting transport, so
/// the bracket's behavior is decided by Core's own sentinel queue and parser,
/// not by a fake.
///
/// The choreography every test below drives:
/// <code>
///   TryRun            → BAT ST                       (OPENING bracket)
///   AnswerSentinel()  → the write + BAT ST + the closing read go out
///   AnswerSentinel()  → the outcome is delivered     (CLOSING bracket)
/// </code>
/// </summary>
public sealed class AleProgrammingGateTests : SessionTestBase
{
    private readonly AleSurface _ale;
    private readonly List<AleProgrammingOutcome> _outcomes = [];

    public AleProgrammingGateTests() => _ale = new AleSurface(Radio);

    private bool RunProgramSelf(string address = "CAM", int group = 1)
        => _ale.Programming.TryRun(
            () => _ale.ProgramSelf(address, group),
            () => _ale.RequestStationBook(),
            _outcomes.Add,
            out _);

    // ---- Serialization ----------------------------------------------------

    [Fact]
    public void TryRun_WhileAnOperationIsOpen_IsRefusedWithAReason_AndSendsNothing()
    {
        ConnectReady();
        Assert.True(RunProgramSelf());
        Transport.ClearSent();

        bool accepted = _ale.Programming.TryRun(
            () => _ale.ProgramScanChannel(1, 0),
            () => _ale.RequestChannelGroup(1),
            _outcomes.Add,
            out string busyReason);

        Assert.False(accepted);
        Assert.Equal(AleProgrammingGate.BusyReason, busyReason);
        Assert.Empty(Transport.SentLines);          // the refused card sends NOTHING
        Assert.Empty(_outcomes);                    // …and gets no outcome either

        // …and the gate frees on delivery, so the second card can run next.
        AnswerSentinel();
        AnswerSentinel();
        Assert.Single(_outcomes);
        Assert.False(_ale.Programming.IsBusy);
        Assert.True(RunProgramSelf("BOB"));
    }

    [Fact]
    public void AnOperation_IsExactlyTheBracket_TheWrite_AndTheClosingRead()
    {
        ConnectReady();
        Assert.True(RunProgramSelf());

        // Nothing but the opening bracket until the radio answers it: the
        // write is issued FROM that answer, which is what keeps a refusal it
        // draws inside the window whatever Core's sentinel queue is doing.
        Assert.Equal(["BAT ST"], Transport.SentLines);

        AnswerSentinel();

        Assert.Equal(
            ["BAT ST", "SLFAD CAM 1", "BAT ST", "SLFAD", "INDAD", "NETAD"],
            Transport.SentLines);
    }

    // ---- Outcomes ---------------------------------------------------------

    [Fact]
    public void ARefusalInsideTheBracket_IsTheOperationsOutcome_Verbatim()
    {
        ConnectReady();
        RunProgramSelf();
        AnswerSentinel();                            // the write goes out

        Transport.InjectLine(" ADDRESS EXISTS ");    // the radio refuses it
        AnswerSentinel();                            // closing bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Refused, outcome.Result);
        Assert.Equal("ADDRESS EXISTS", outcome.Detail);
    }

    [Fact]
    public void NoRefusal_AndAnAnsweredClosingBracket_IsAccepted()
    {
        ConnectReady();
        RunProgramSelf();
        AnswerSentinel();
        AnswerSentinel();

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, outcome.Result);
        Assert.Null(outcome.Detail);
    }

    [Fact]
    public void AnUnansweredClosingBracket_IsUnverified_NeverAccepted()
    {
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        RunProgramSelf();
        AnswerSentinel();                            // opening answered; closing is not

        Assert.True(WaitUntil(() => _outcomes.Count == 1, 10_000));
        Assert.Equal(AleProgrammingResult.Unverified, _outcomes[0].Result);
        Assert.Contains("did not answer", _outcomes[0].Detail);
    }

    [Fact]
    public void ACoreValidationThrow_ReleasesTheGate_AndReportsFaulted()
    {
        // A control character that passed the client bounds: Core refuses it
        // and NOTHING of the write reaches the wire. The gate must not wedge.
        ConnectReady();
        Assert.True(_ale.Programming.TryRun(
            () => _ale.ProgramSelf("A\rZERO", 1),
            () => _ale.RequestStationBook(),
            _outcomes.Add,
            out _));

        AnswerSentinel();                            // the write stage throws here

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Faulted, outcome.Result);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Detail));
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SLFAD", StringComparison.Ordinal));

        // Released immediately — the next operation runs normally.
        Assert.False(_ale.Programming.IsBusy);
        Assert.True(RunProgramSelf("BOB"));
        AnswerSentinel();
        Assert.Contains("SLFAD BOB 1", Transport.SentLines);
    }

    // ---- The bracket's attribution window ---------------------------------

    [Fact]
    public void ARefusalDrawnByACommandQueuedBEFORETheOperation_IsNeverAttributed()
    {
        // The pass-2 race, closed by the opening bracket: an Operate CAL is
        // already on the wire when the operator programs. Its refusal arrives
        // before the opening bracket answers — i.e. at or below the
        // watermark — so this operation is Accepted, and the bad CAL's line
        // never surfaces on a programming card.
        ConnectReady();
        Radio.Ale.Call("BOB");
        RunProgramSelf();

        Transport.InjectLine(" ADDRESS EXISTS ");    // the CAL's refusal
        AnswerSentinel();                            // opening bracket
        Assert.Contains("SLFAD CAM 1", Transport.SentLines);
        AnswerSentinel();                            // closing bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, outcome.Result);
        Assert.Null(outcome.Detail);
        // The mirror still RECORDS the refusal — recording and attributing
        // are different jobs, and this is what makes the pin non-vacuous.
        Assert.Equal("ADDRESS EXISTS", Radio.State.Ale.ProgrammingRefusal.Line);
    }

    [Fact]
    public void ARefusalDrawnByAForeignCommand_AfterTheBracket_IsNeverAttributed()
    {
        // The pass-3 gap, closed by dropping the closing READ out of
        // attribution entirely: another producer's command goes out after our
        // bracket, and its refusal cannot reach this operation's outcome.
        ConnectReady();
        RunProgramSelf();

        AnswerSentinel();                            // opening bracket → write out
        Assert.Contains("SLFAD CAM 1", Transport.SentLines);

        Radio.Ale.Call("BOB");                       // a foreign command, after ours
        AnswerSentinel();                            // closing bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, outcome.Result);

        // The foreign refusal lands after the bracket closed: no outcome is
        // revised, and none is invented.
        Transport.InjectLine(" ADDRESS EXISTS ");
        Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, _outcomes[0].Result);
    }

    // ---- Dispatch-order pins (audit round 1, BLOCKERs 1 and 2) ------------
    // Completion order is NOT wire order on this transport: Core keeps one
    // BAT ST on the wire and dispatches the next when the current answers.
    // These reproduce the auditor's wire orders exactly.

    [Fact]
    public void WithAForeignSentinelOnTheWire_TheWriteWaits_AndTheBracketStaysAdjacent()
    {
        // AUDITOR'S BLOCKER 1, byte for byte. Old behavior: the opening
        // bracket answers, Core dispatches the queued book BAT ST first, the
        // gate sends the write, its closing sentinel queues BEHIND the book
        // one — and a later CAL goes out BEFORE that sentinel, so the CAL's
        // refusal lands inside the window and the operation reports a FALSE
        // Refused. Now the gate re-arms instead of writing into that slot.
        ConnectReady();
        RunProgramSelf();
        _ale.RefreshStationList();                   // a foreign sentinel queues behind ours
        Transport.ClearSent();

        AnswerSentinel();                            // opening barrier answers…
        // …and NOTHING of the operation is on the wire: Core dispatched the
        // book read's BAT ST into the slot, so this is not a clean one. The
        // gate re-armed (its new barrier is QUEUED, not dispatched) instead
        // of writing into a slot where its closing sentinel would queue
        // behind a stranger's.
        Assert.Equal(["BAT ST"], Transport.SentLines);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SLFAD ", StringComparison.Ordinal));

        AnswerSentinel();                            // the book read's sentinel
        Transport.ClearSent();
        AnswerSentinel();                            // the re-armed barrier — clean slot now

        // The write and the closing sentinel are ADJACENT on the wire.
        Assert.Equal("SLFAD CAM 1", Transport.SentLines[0]);
        Assert.Equal("BAT ST", Transport.SentLines[1]);

        // The auditor's CAL now goes out AFTER the closing sentinel, so its
        // refusal cannot be attributed to this write.
        Radio.Ale.Call("BOB");
        AnswerSentinel();                            // closing bracket
        Transport.InjectLine(" INV ADDRESS      ");  // the CAL's refusal, after the bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, outcome.Result);
    }

    [Fact]
    public void ASlotThatNeverComesClean_EndsTheOperation_WithoutSendingTheWrite()
    {
        // The bound on the re-arm loop: a producer that keeps a sentinel
        // outstanding round after round must not leave the gate open — and
        // must never trick it into writing into a dirty slot. Nothing is
        // written, and the outcome says exactly that.
        ConnectReady();
        RunProgramSelf();

        for (int round = 0; round < 8 && _outcomes.Count == 0; round++)
        {
            Radio.Ale.Synchronize();                 // a foreign sentinel, every round
            AnswerSentinel();                        // …completes ours, dispatches theirs
            AnswerSentinel();                        // …completes theirs
        }

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Faulted, outcome.Result);
        Assert.Contains("nothing was sent", outcome.Detail);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SLFAD ", StringComparison.Ordinal));
        Assert.False(_ale.Programming.IsBusy);
    }

    [Fact]
    public void ALateAnswerToATimedOutSentinel_NeverOpensTheBracket_AndSettlesByItself()
    {
        // AUDITOR'S ROUND-2 BLOCKER, byte for byte. A station-book read is on
        // the wire; a programming operation starts behind it; the BOOK
        // sentinel TIMES OUT (so our barrier dispatches and nothing is
        // pending); the book's LATE BATTERY then completes OUR barrier early
        // under Core's documented credit. "Completed + nothing pending" is
        // therefore NOT proof the answer was ours — opening the bracket here
        // put the write one answer outside its own window, and a refused
        // write reported Accepted. The debt says so, and the gate refuses.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        _ale.RefreshStationList();                   // its BAT ST is on the wire
        Radio.Ale.RefreshTimeoutMs = 10_000;         // our barrier must NOT time out
        RunProgramSelf();

        Assert.True(WaitUntil(() => Radio.PendingPingCount == 1 && Radio.PingAnswerDebt == 1, 10_000),
            "the book sentinel should time out, leaving its answer owed while our barrier dispatches");
        Transport.ClearSent();

        AnswerSentinel();                            // the book's LATE answer, crediting ours

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Faulted, outcome.Result);
        Assert.Contains("nothing was sent", outcome.Detail);
        Assert.Empty(Transport.SentLines);           // the write never went out
        Assert.False(_ale.Programming.IsBusy);

        // The delayed refusal the auditor used cannot be turned into a
        // verdict either: no operation is open to attribute it to.
        Transport.InjectLine(" ADDRESS EXISTS ");
        Assert.Single(_outcomes);

        // …and refusing SETTLES the stream: our barrier's own answer is now
        // discarded against an empty queue, the debt clears, and the
        // operator's next press runs clean.
        AnswerSentinel();
        Assert.Equal(0, Radio.PingAnswerDebt);
        Assert.True(RunProgramSelf("BOB"));
        AnswerSentinel();
        Assert.Contains("SLFAD BOB 1", Transport.SentLines);
    }

    [Fact]
    public void ASentinelRacedInBeforeTheClosingBracket_ResolvesUnverified_NotAVerdict()
    {
        // The dirty-bracket detector's own pin (audit round 2, MAJOR — the
        // round-1 claim that this was mutation-checked was wrong: no pin had
        // landed). The write action itself queues a foreign sentinel, which
        // is the deterministic, in-process stand-in for another producer
        // racing one in from another thread between our write and our closing
        // bracket: our closing sentinel is then NOT dispatched adjacent to
        // the write, so a refusal inside the bracket cannot honestly be
        // attributed to it — and the gate says Unverified instead of
        // inventing either verdict.
        ConnectReady();
        Assert.True(_ale.Programming.TryRun(
            () => { _ale.ProgramSelf("CAM", 1); Radio.Ale.Synchronize(); },
            () => _ale.RequestStationBook(),
            _outcomes.Add,
            out _));

        AnswerSentinel();                            // opening barrier → write stage
        Assert.Contains("SLFAD CAM 1", Transport.SentLines);

        Transport.InjectLine(" ADDRESS EXISTS ");    // a refusal INSIDE the dirty bracket
        AnswerSentinel();                            // the raced-in sentinel
        AnswerSentinel();                            // our closing bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Unverified, outcome.Result);
        Assert.Contains("another read was in flight", outcome.Detail);
    }

    [Fact]
    public void ABareSentinelRacedInBeforeTheWrite_ResolvesUnverified_NotAVerdict()
    {
        // AUDITOR'S ROUND-3 BLOCKER, byte for byte. A bare BAT ST (another
        // producer's QueryBatteryState) goes out immediately before the
        // write, so the wire reads: bare BAT ST, write, closing BAT ST. That
        // bare sentinel has NO ping-queue entry, so the pending count alone
        // still says "clean" — while its BATTERY answer credits our closing
        // sentinel EARLY and the write's own refusal lands after the verdict
        // (reported Accepted). Cleanliness now takes BOTH counters, and the
        // debt is what sees this one.
        ConnectReady();
        Assert.True(_ale.Programming.TryRun(
            () => { Radio.QueryBatteryState(); _ale.ProgramSelf("CAM", 1); },
            () => _ale.RequestStationBook(),
            _outcomes.Add,
            out _));

        AnswerSentinel();                            // opening barrier → write stage
        // The wire, in order: our opening barrier, the BARE query, the write,
        // our closing sentinel.
        Assert.Equal(
            ["BAT ST", "BAT ST", "SLFAD CAM 1", "BAT ST"],
            Transport.SentLines.Where(l => l == "BAT ST" || l.StartsWith("SLFAD ", StringComparison.Ordinal)));
        Assert.Equal(1, Radio.PingAnswerDebt);       // the bare sentinel's answer is owed

        AnswerSentinel();                            // the BARE query's answer, crediting ours
        Transport.InjectLine(" ADDRESS EXISTS ");    // the write's refusal, after the credit

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Unverified, outcome.Result);
        Assert.Contains("another read was in flight", outcome.Detail);

        // …and the stream settles, so the operator's retry runs clean.
        for (int i = 0; i < 6 && Radio.PingAnswerDebt > 0; i++) AnswerSentinel();
        Assert.Equal(0, Radio.PingAnswerDebt);
        Transport.ClearSent();
        Assert.True(RunProgramSelf("BOB"));
        AnswerSentinel();
        Assert.Equal(["BAT ST", "SLFAD BOB 1", "BAT ST"], Transport.SentLines.Take(3));
    }

    [Fact]
    public void AnUnansweredOpeningBracket_SendsNoWriteAtAll_AndReportsUnverified()
    {
        // AUDITOR'S BLOCKER 2. Old behavior: the opening BAT ST timed out,
        // the write and closing sentinel went out anyway, and a LATE opening
        // BATTERY satisfied the closing sentinel under Core's documented
        // late-answer credit — so the write's refusal arrived afterwards and
        // the operation reported a FALSE Accepted. Programming blind on a
        // dead prompt is what the constitution forbids: nothing is sent.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        RunProgramSelf();
        Assert.Equal(["BAT ST"], Transport.SentLines);

        Assert.True(WaitUntil(() => _outcomes.Count == 1, 10_000));
        Assert.Equal(AleProgrammingResult.Unverified, _outcomes[0].Result);
        Assert.Contains("did not answer", _outcomes[0].Detail);
        Assert.Equal(["BAT ST"], Transport.SentLines);          // the write never went out

        // The late answer the auditor exploited arrives now — with nothing
        // outstanding to credit and no operation open, it changes nothing.
        AnswerSentinel();
        Transport.InjectLine(" ADDRESS EXISTS ");
        Assert.Single(_outcomes);
        Assert.False(_ale.Programming.IsBusy);
    }

    [Fact]
    public void TheClosingRead_IsDisplayOnly_AndPlaysNoPartInTheOutcome()
    {
        // The outcome resolves at the closing BRACKET; the closing read's own
        // answers (and its own sentinel, here never answered) change nothing.
        ConnectReady();
        RunProgramSelf();
        AnswerSentinel();
        AnswerSentinel();

        Assert.Equal(AleProgrammingResult.Accepted, Assert.Single(_outcomes).Result);
        Assert.Equal(
            ["BAT ST", "SLFAD CAM 1", "BAT ST", "SLFAD", "INDAD", "NETAD", "BAT ST"],
            Transport.SentLines);

        // The re-read arriving afterwards is DISPLAY: it feeds the mirror on
        // its OWN sentinel and never revisits the verdict.
        Transport.InjectLine("SLFAD CAM               CHGROUP 01");
        AnswerSentinel();
        Assert.Single(_outcomes);
        Assert.Equal(["CAM"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
    }

    // ---- CAMPAIGN MODE (D3, plan-clone-write-structural.md §5.3) ----------
    // The debt branch is the ONLY branch the flag touches. Every pin below is
    // the SAME wire choreography as its single-press twin above, with
    // `campaign: true` — which is what makes the comparison a discrimination
    // rather than two unrelated tests.

    private bool RunCampaignProgramSelf(string address = "CAM", int group = 1)
        => _ale.Programming.TryRun(
            () => _ale.ProgramSelf(address, group),
            () => _ale.RequestStationBook(),
            _outcomes.Add,
            out _,
            campaign: true);

    /// <summary>THE DEBT WINDOW, reproduced exactly as the single-press pin
    /// above reproduces it: a station-book read's sentinel TIMES OUT, our
    /// barrier dispatches, and the book's LATE battery completes ours early —
    /// so "completed and nothing pending" is not proof the answer was ours, and
    /// <c>PingAnswerDebt</c> is 1 when the barrier completes.</summary>
    private void OpenABarrierIntoStandingDebt(Func<bool> start)
    {
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        _ale.RefreshStationList();                   // its BAT ST is on the wire
        Radio.Ale.RefreshTimeoutMs = 10_000;         // our barrier must NOT time out
        Assert.True(start());

        Assert.True(WaitUntil(() => Radio.PendingPingCount == 1 && Radio.PingAnswerDebt == 1, 10_000),
            "the book sentinel should time out, leaving its answer owed while our barrier dispatches");
        Transport.ClearSent();

        AnswerSentinel();                            // the book's LATE answer, crediting ours
    }

    [Fact]
    public void InCampaignMode_ADebtAtTheBarrier_RetriesOnce_AndTheRetryIsAccepted()
    {
        // THE FIELD FAILURE'S FIX, on the wire. A campaign cannot empty Core's
        // ping queue between operations, so refusing here cascades through
        // every remaining row. The gate instead waits ONE settle window with
        // nothing of its own queued — which is exactly the condition Core's
        // stray discard needs — and then re-arms.
        _ale.Programming.DebtSettleMs = 400;
        OpenABarrierIntoStandingDebt(() => RunCampaignProgramSelf());

        // NOTHING is resolved and NOTHING is sent: the gate is waiting.
        Assert.Empty(_outcomes);
        Assert.Empty(Transport.SentLines);

        // The settle window does its job: the owed answer arrives against an
        // EMPTY queue (our own entry is gone) and is discarded, clearing the
        // debt — the mechanic the single-press rule hands to the operator's
        // next press, spent by the gate itself here.
        AnswerSentinel();
        Assert.Equal(0, Radio.PingAnswerDebt);

        // …and the timer then re-arms the barrier, which is the first thing to
        // reach the wire since the debt was seen.
        Assert.True(WaitUntil(() => Transport.SentLines.Contains("BAT ST"), 10_000),
            "the settle timer never re-armed the barrier");
        AnswerSentinel();                            // the re-armed barrier — clean slot now
        Assert.Contains("SLFAD CAM 1", Transport.SentLines);
        AnswerSentinel();                            // the closing bracket

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Accepted, outcome.Result);
        Assert.Equal(AleProgrammingFaultKind.None, outcome.Kind);
        Assert.False(_ale.Programming.IsBusy);
    }

    [Fact]
    public void InCampaignMode_ADebtThatDoesNotSettle_FaultsOnce_TypedSentinelDebt()
    {
        // The other half: the settle window expires with the debt STILL
        // standing (nobody ever answers), so the gate faults — once, with the
        // single-press sentence, and with the TYPED kind the clone campaign's
        // leg policy keys on.
        _ale.Programming.DebtSettleMs = 400;
        OpenABarrierIntoStandingDebt(() => RunCampaignProgramSelf());

        // NOT YET — and this is the discrimination against the single-press
        // branch, which resolves in the same breath as the observation. The
        // window really runs before the fault.
        Assert.Empty(_outcomes);
        Assert.True(_ale.Programming.IsBusy);

        Assert.True(WaitUntil(() => _outcomes.Count == 1, 10_000),
            "the settle timer never resolved the operation");
        var outcome = Assert.Single(_outcomes);
        Assert.Equal(AleProgrammingResult.Faulted, outcome.Result);
        Assert.Equal(AleProgrammingFaultKind.SentinelDebt, outcome.Kind);
        Assert.Contains("nothing was sent", outcome.Detail);
        Assert.Empty(Transport.SentLines);           // the write never went out
        Assert.False(_ale.Programming.IsBusy);       // …and the gate is free again
    }

    [Fact]
    public void InCampaignMode_TheSettleWindow_RETAINSOwnership_SoNothingElseCanStart()
    {
        // THE OWNERSHIP RULE (critic pass 2). The gate resolves nothing during
        // the settle, so `IsBusy` must stay TRUE — otherwise the other
        // programming card could open an operation into the very queue state
        // this window exists to let drain.
        _ale.Programming.DebtSettleMs = 5_000;       // long enough to observe the gap
        OpenABarrierIntoStandingDebt(() => RunCampaignProgramSelf());

        Assert.Empty(_outcomes);
        Assert.True(_ale.Programming.IsBusy);
        bool accepted = _ale.Programming.TryRun(
            () => _ale.ProgramScanChannel(1, 0), () => _ale.RequestChannelGroup(1),
            _outcomes.Add, out string busyReason);
        Assert.False(accepted);
        Assert.Equal(AleProgrammingGate.BusyReason, busyReason);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void InCampaignMode_ASessionDropDuringTheSettle_CancelsTheTimer_WithNoCallback()
    {
        // The existing drop contract, extended to the retry state: the radio
        // that was going to answer is gone, so no outcome is delivered and no
        // barrier is re-armed when the window would have expired.
        _ale.Programming.DebtSettleMs = 120;
        OpenABarrierIntoStandingDebt(() => RunCampaignProgramSelf());
        Assert.True(_ale.Programming.IsBusy);

        _ale.Programming.AbandonForSessionDrop();

        Assert.False(_ale.Programming.IsBusy);
        Assert.False(WaitUntil(() => _outcomes.Count > 0, 600), "an abandoned operation was reported");
        Assert.Empty(Transport.SentLines);           // no re-armed barrier either
    }

    [Fact]
    public void WithoutTheFlag_TheDebtBranch_IsByteIdenticalToWhatItAlwaysWas()
    {
        // I-2's opt-in half, stated as a discrimination against the two pins
        // above: the SAME wire choreography, the flag left at its default,
        // resolves IMMEDIATELY — no settle window, no re-arm, and the operator
        // gets the sentence and the free gate they always did. The typed Kind
        // is additive: it is populated here too, and nothing keys on prose.
        _ale.Programming.DebtSettleMs = 5_000;       // would be observable if it ran
        OpenABarrierIntoStandingDebt(() => RunProgramSelf());

        var outcome = Assert.Single(_outcomes);      // resolved already, not after a window
        Assert.Equal(AleProgrammingResult.Faulted, outcome.Result);
        Assert.Equal(AleProgrammingFaultKind.SentinelDebt, outcome.Kind);
        Assert.Equal(
            "the radio is behind on its sentinel answers — nothing was sent; try again",
            outcome.Detail);
        Assert.Empty(Transport.SentLines);
        Assert.False(_ale.Programming.IsBusy);
    }

    [Fact]
    public void TheOtherTwoFaults_CarryTheirOwnKinds_NeverSentinelDebt()
    {
        // The discrimination that makes I-9 worth having: a busy queue and a
        // Core validation throw are BOTH Faulted, and neither may look like a
        // debt to a consumer that keys on the type.
        ConnectReady();
        RunCampaignProgramSelf();
        for (int round = 0; round < 8 && _outcomes.Count == 0; round++)
        {
            Radio.Ale.Synchronize();                 // a foreign sentinel, every round
            AnswerSentinel();
            AnswerSentinel();
        }
        Assert.Equal(AleProgrammingFaultKind.QueueBusy, Assert.Single(_outcomes).Kind);

        _outcomes.Clear();
        Assert.True(_ale.Programming.TryRun(
            () => _ale.ProgramSelf("A\rZERO", 1), () => _ale.RequestStationBook(),
            _outcomes.Add, out _, campaign: true));
        AnswerSentinel();                            // the write stage throws here
        Assert.Equal(AleProgrammingFaultKind.Exception, Assert.Single(_outcomes).Kind);
    }

    // ---- Session lifecycle ------------------------------------------------

    [Fact]
    public void ASessionDrop_DiscardsTheOperation_WithNoCallback()
    {
        ConnectReady();
        RunProgramSelf();
        AnswerSentinel();                            // mid-operation

        _ale.Programming.AbandonForSessionDrop();    // the consumer's PhaseChanged wiring

        AnswerSentinel();                            // the closing bracket answers anyway
        Assert.Empty(_outcomes);                     // …and nobody is told
        Assert.False(_ale.Programming.IsBusy);       // …and the gate is free again
    }

    [Fact]
    public void ATeardownRacingAnOpenOperation_ReportsUnverified_RatherThanInventing()
    {
        // RECORDED behavior, not a silent gap (see AleProgrammingGate): Core
        // releases its pending sentinels BEFORE the connection events, so a
        // teardown that races an open operation completes the bracket
        // unanswered first. On the app's marshalled context the port is
        // already closed by the time that callback runs and the outcome is
        // discarded; on this test's INLINE context it is delivered — as
        // Unverified, which is the honest reading of "the radio never
        // answered". The consumer clears gate display on the same drop.
        ConnectReady();
        RunProgramSelf();
        AnswerSentinel();

        Session.Close();

        var outcome = Assert.Single(_outcomes);      // non-vacuous: it IS delivered here
        Assert.Equal(AleProgrammingResult.Unverified, outcome.Result);
        Assert.False(_ale.Programming.IsBusy);       // never wedged, either way
    }
}
