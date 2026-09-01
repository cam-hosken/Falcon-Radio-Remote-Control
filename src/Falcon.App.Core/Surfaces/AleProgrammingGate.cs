using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>What a programming operation ended as. Never invented: every
/// value below is derived from the radio's own lines (or from a Core
/// validation throw), per plan-ale-programming.md §4.3.</summary>
public enum AleProgrammingResult
{
    /// <summary>The bracket closed with no refusal inside it.</summary>
    Accepted,
    /// <summary>The radio refused inside the bracket — <c>Detail</c> is its
    /// own line, verbatim.</summary>
    Refused,
    /// <summary>The closing bracket was never answered: the radio did not
    /// confirm the write either way.</summary>
    Unverified,
    /// <summary>NOTHING, or only part, reached the wire — a Core validation
    /// throw, or a bracket the gate could not open cleanly. <c>Detail</c>
    /// says which.</summary>
    Faulted,
}

/// <summary>
/// WHY a <see cref="AleProgrammingResult.Faulted"/> outcome faulted, as a
/// TYPE (plan-clone-write-structural.md §5.3, invariant I-9).
///
/// <para><b>Why it exists.</b> The clone campaign's gated-leg policy treats a
/// standing sentinel DEBT differently from every other fault — one debt
/// abandons the leg, a queue-busy or a Core validation throw does not — and
/// the only alternative to a type was matching the gate's ENGLISH SENTENCE.
/// A consumer keyed on prose is a consumer that breaks silently the next time
/// the wording is trimmed (D9 is queued to do exactly that), and it breaks
/// into the WRONG behaviour rather than into a failing build.</para>
///
/// <para><see cref="None"/> is what every non-faulted outcome carries.</para>
/// </summary>
public enum AleProgrammingFaultKind
{
    /// <summary>Not a fault (or a fault this gate does not classify).</summary>
    None,
    /// <summary>The radio is behind on its sentinel answers: a standing
    /// <see cref="Prc138Radio.PingAnswerDebt"/> at the opening barrier, after
    /// campaign mode's own in-gate retry where one applied.</summary>
    SentinelDebt,
    /// <summary>Another producer kept a sentinel outstanding for the whole
    /// re-arm budget, so no clean dispatch slot ever came.</summary>
    QueueBusy,
    /// <summary>A throw out of the write or the closing read — a Core
    /// validation refusal, most often.</summary>
    Exception,
}

/// <summary>One programming operation's outcome. <see cref="Kind"/> is
/// populated on every <see cref="AleProgrammingResult.Faulted"/> and defaults
/// to <see cref="AleProgrammingFaultKind.None"/>, so every pre-existing
/// two-argument construction still means what it always did.</summary>
public readonly record struct AleProgrammingOutcome(
    AleProgrammingResult Result,
    string? Detail,
    AleProgrammingFaultKind Kind = AleProgrammingFaultKind.None);

/// <summary>
/// The single serialized coordinator for ALE programming WRITES
/// (plan-ale-programming.md §4.3) — owned and exposed by
/// <see cref="AleSurface"/> and shared by both programming cards, because
/// mutual exclusion between them is the point.
///
/// <para><b>The bracket.</b> An accepted operation is
/// <c>Synchronize()</c> (OPENING bracket) → the one write →
/// <c>Synchronize()</c> (CLOSING bracket) → the caller's closing READ. The
/// last three are issued back to back, synchronously, so no other producer's
/// command can land between them.</para>
///
/// <para><b>The bracket is grounded in DISPATCH order, not in completion
/// callbacks</b> (audit round 1, BLOCKERs 1 and 2 — completion order is NOT
/// wire order on this transport, because Core keeps only one <c>BAT ST</c> on
/// the wire and dispatches the next when the current one answers). Two rules
/// follow, and both are load-bearing:
/// <list type="number">
/// <item>The write is released only from an OPENING BRACKET THAT ANSWERED
/// (an unanswered one means the radio is not talking — programming blind on a
/// dead prompt is exactly what the thin-client constitution forbids), only
/// when <see cref="Prc138Radio.PingAnswerDebt"/> is 0 — proof that the
/// completion was this barrier's OWN answer and not a stray one shifting
/// every credit by a place — and only when
/// <see cref="Prc138Radio.PendingPingCount"/> is 0 at that moment. Zero
/// pending is what makes the closing <c>Synchronize()</c> DISPATCH
/// IMMEDIATELY, so the write and the closing <c>BAT ST</c> are adjacent on
/// the wire and nothing of anyone else's can sit between them. A busy queue
/// is waited out (the gate re-arms the barrier, up to
/// <see cref="MaxBarrierAttempts"/>, re-taking the watermark each time and
/// sending nothing meanwhile); a standing DEBT is not waited out but
/// refused, because holding a barrier in the queue is exactly what prevents
/// the stray answer being discarded — see the debt branch for why refusing
/// settles it.</item>
/// <item>Adjacency is then VERIFIED, not assumed: right after the closing
/// <c>Synchronize()</c> the gate checks that its own sentinel is the only one
/// outstanding. If some other thread raced one in, the bracket is not clean
/// and the operation resolves Unverified rather than risk a verdict — a
/// refusal inside a dirty bracket cannot be honestly attributed.</item>
/// </list></para>
///
/// <para><b>The refusal window IS the bracket.</b> A refusal counts as this
/// operation's when its mirror sequence is ABOVE the sequence standing when
/// the opening bracket completed, and it arrives at-or-before the closing
/// bracket's completion. Anything queued BEFORE the operation (an Operate
/// <c>CAL</c>, a settings write) has its output — refusal included —
/// delivered before the opening bracket answers, so it is never attributed;
/// anything a later producer draws arrives after the closing bracket, so it
/// is not either. The closing READ plays NO part in attribution: it is the
/// display refresh the operator sees, free to coalesce in the store queue.</para>
///
/// <para><b>DEVIATION from §4.3's three-call atomic enqueue, forced by
/// measured Core behavior</b> (pinned by
/// <c>Synchronize_NeverDefersIntoTheStoreQueues_AndCompletesInCallOrder</c>
/// and the gate's own bracket pins): Core's sentinel queue keeps only ONE
/// <c>BAT ST</c> on the wire at a time (Prc138Radio Q3). Enqueuing the write
/// immediately behind the opening <c>Synchronize()</c> therefore lets the
/// WRITE overtake the opening bracket whenever an earlier read's sentinel is
/// still outstanding — and a refusal the write then draws arrives BEFORE the
/// opening bracket completes, falling outside the window, so a refused write
/// would report Accepted. The gate instead issues the write from the OPENING
/// BRACKET'S COMPLETION, under the two dispatch rules above, which makes the
/// window behave exactly as §4.3 specifies in every queue state. The residual
/// is the gap between that completion and the write: on the app's marshalled
/// context the callback runs to completion on the UI thread, so no other UI
/// gesture can interleave, and the trigger table's own compensations are
/// SSB-prompt queries — while a programming write requires a confirmed ALE.</para>
///
/// <para><b>Known cost of the debt rule</b> (stated, not hidden): a sentinel
/// that is truly SWALLOWED — dispatched, never answered at all — leaves a
/// debt that nothing can pay off, because the discard that clears it needs an
/// answer to arrive. Programming then stays refused, with an honest message,
/// until the session is reconnected. That is the deliberate trade: the
/// alternative is programming over a link already dropping sentinels, and
/// guessing when a "late" answer has become a "never" answer would be a
/// timing invention this layer has no evidence for. A LATE answer (the
/// common case, and the one the audit reproduced) settles by itself, and the
/// operator's next press runs clean — pinned.</para>
///
/// <para><b>Exception safety.</b> Any throw out of the write or the closing
/// read releases the gate immediately and delivers Faulted — the gate can
/// never wedge.</para>
///
/// <para><b>Session drop.</b> <see cref="AbandonForSessionDrop"/> (the
/// consumer's PhaseChanged wiring) discards the in-flight operation with NO
/// callback, and so does a connection event that shows the radio gone;
/// delivery re-checks the connection and discards silently when the port has
/// already closed (Core releases its pending sentinels BEFORE the connection
/// events, so on the app's context the port is already shut by the time a
/// dropped bracket's callback runs).</para>
///
/// <para><b>CAMPAIGN MODE</b> (plan-clone-write-structural.md D3 / §5.3). The
/// debt rule above is right for a SINGLE PRESS and wrong for a campaign: the
/// discard that clears a debt needs an EMPTY ping queue, which the operator's
/// pause after a refusal supplies and a 32-operation clone campaign never
/// does — so in the field one debt cascaded through every book row of every
/// attempt. A caller that passes <c>campaign: true</c> to
/// <see cref="TryRun"/> therefore gets ONE in-gate retry on the debt branch
/// instead of an immediate fault: the gate resolves nothing, RETAINS
/// EXCLUSIVE OWNERSHIP (<see cref="IsBusy"/> stays true, so no other
/// programming operation can start in the gap), queues nothing of its own —
/// its own entry is gone, which is exactly what lets Core's queue drain and
/// the stray answer be discarded — and waits ONE
/// <see cref="DebtSettleMs"/> settle window. At the timer it re-arms the
/// barrier only when BOTH counters are clear; a nonzero pair there, or a
/// second debt at the re-armed barrier, is Faulted with
/// <see cref="AleProgrammingFaultKind.SentinelDebt"/>. Single-press
/// behaviour is byte-identical to what it always was, and pinned so.</para>
/// </summary>
public sealed class AleProgrammingGate
{
    /// <summary>What <see cref="TryRun"/> reports when it refuses.</summary>
    public const string BusyReason = "another programming operation is in progress";

    /// <summary>How many times the gate will re-arm the opening barrier
    /// waiting for a clean dispatch slot before giving up and reporting
    /// Faulted. Bounded so a stranger's never-answered sentinel cannot leave
    /// an operation open forever — the gate must never wedge.</summary>
    private const int MaxBarrierAttempts = 4;

    /// <summary>
    /// How long CAMPAIGN MODE's one debt retry waits before re-taking the
    /// barrier — the window in which Core's queue drains and the stray answer
    /// that minted the debt is discarded against it.
    ///
    /// <para>GATE-OWNED and internal-settable, in
    /// <c>CloneService</c>'s test-hook idiom, because there is no shared Core
    /// sentinel-timeout constant to borrow: <c>AleController.RefreshTimeoutMs</c>
    /// and the campaign's own timeouts are separate mutable fields with
    /// separate jobs, and binding this window to either would make a test hook
    /// for one silently move the other.</para>
    /// </summary>
    internal int DebtSettleMs { get; set; } = 3_000;

    private sealed class Operation
    {
        public required Action SendWrite;
        public required Func<long> SendClosingRead;
        public required Action<AleProgrammingOutcome> OnOutcome;
        /// <summary>Is this the clone campaign's operation? Only a campaign
        /// gets the debt retry below (D3: single-press semantics unchanged).</summary>
        public bool Campaign;
        /// <summary>Has campaign mode already spent its ONE debt retry? A
        /// second debt observation faults.</summary>
        public bool DebtRetried;
        /// <summary>The settle window's timer — created per retry, disposed on
        /// resolution or abandonment, always under the gate's lock.</summary>
        public Timer? SettleTimer;
        /// <summary>The barrier currently being waited on (the opening
        /// bracket, possibly re-armed).</summary>
        public long BarrierId;
        public int BarrierAttempts;
        public bool WriteReleased;
        public long ClosingId;
        public long Watermark;
        /// <summary>Was the closing sentinel really dispatched adjacent to
        /// the write? False = no verdict may be drawn from this bracket.</summary>
        public bool BracketClean;
    }

    private readonly Prc138Radio _radio;
    private readonly object _lock = new();
    private Operation? _operation;
    private AleProgrammingRefusal _lastRefusal;

    internal AleProgrammingGate(Prc138Radio radio)
    {
        _radio = radio;
        _lastRefusal = radio.State.Ale.ProgrammingRefusal;
        radio.StateChanged += OnRadioStateChanged;
    }

    /// <summary>True while an operation is open (consumer/test hook).</summary>
    public bool IsBusy { get { lock (_lock) return _operation is not null; } }

    /// <summary>
    /// Run ONE programming write inside the bracket. Returns false — with
    /// <paramref name="busyReason"/> set and nothing sent — when another
    /// operation is already open.
    /// </summary>
    /// <param name="sendWrite">The single write wrapper.</param>
    /// <param name="sendClosingRead">The display re-read; its read id is
    /// deliberately unused here (the read has no attribution role).</param>
    /// <param name="onOutcome">Runs exactly once per accepted operation,
    /// unless the session drops (then never).</param>
    /// <param name="busyReason">Why nothing was started, or "".</param>
    /// <param name="campaign">CALLER-SUPPLIED (§5.3): true only from a clone
    /// campaign, which is the one caller that cannot empty Core's ping queue
    /// between operations. It buys ONE debt retry inside the gate and changes
    /// nothing else; every other caller leaves it false and behaves exactly as
    /// it always has.</param>
    public bool TryRun(
        Action sendWrite,
        Func<long> sendClosingRead,
        Action<AleProgrammingOutcome> onOutcome,
        out string busyReason,
        bool campaign = false)
    {
        ArgumentNullException.ThrowIfNull(sendWrite);
        ArgumentNullException.ThrowIfNull(sendClosingRead);
        ArgumentNullException.ThrowIfNull(onOutcome);

        Operation operation;
        lock (_lock)
        {
            if (_operation is not null)
            {
                busyReason = BusyReason;
                return false;
            }
            operation = new Operation
            {
                SendWrite = sendWrite,
                SendClosingRead = sendClosingRead,
                OnOutcome = onOutcome,
                Campaign = campaign,
            };
            _operation = operation;
        }

        busyReason = "";
        ArmBarrier(operation);
        return true;
    }

    /// <summary>Put an opening barrier on the wire and wait for it. Called
    /// again (bounded) when the barrier answers while another producer's
    /// sentinel is still outstanding — the write may only be released from a
    /// clean dispatch slot.</summary>
    private void ArmBarrier(Operation operation)
    {
        try
        {
            operation.BarrierAttempts++;
            operation.BarrierId = _radio.Ale.Synchronize();
        }
        catch (Exception ex)
        {
            Fault(operation, ex);
            return;
        }

        // Late binding: with a closed port (or an inline context) the barrier
        // can complete INSIDE the call above, before its id was recorded —
        // that completion would otherwise match nothing and wedge the gate.
        // Re-offering the latest completion is idempotent.
        Advance(_radio.State.Ale.LastSync);
    }

    /// <summary>Discard the in-flight operation WITHOUT delivering an outcome
    /// (session drop: the radio that was going to answer is gone).
    /// <para>An operation waiting out campaign mode's settle window is
    /// discarded exactly like any other, and its timer is CANCELLED so no
    /// callback runs against a radio that is gone — the existing drop
    /// contract, extended to the retry state and pinned.</para></summary>
    public void AbandonForSessionDrop()
    {
        lock (_lock)
        {
            _operation?.SettleTimer?.Dispose();
            _operation = null;
        }
    }

    private bool IsCurrent(Operation operation)
    {
        lock (_lock) return ReferenceEquals(_operation, operation);
    }

    /// <summary>Release <paramref name="operation"/> if it is still the open
    /// one; false means something already resolved or abandoned it.</summary>
    private bool Release(Operation operation)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_operation, operation)) return false;
            operation.SettleTimer?.Dispose();
            operation.SettleTimer = null;
            _operation = null;
            return true;
        }
    }

    /// <summary>Release FIRST, then report: the gate must be reusable even if
    /// the callback itself throws.</summary>
    private void Fault(Operation operation, Exception ex)
    {
        if (Release(operation))
            operation.OnOutcome(new AleProgrammingOutcome(
                AleProgrammingResult.Faulted, ex.Message, AleProgrammingFaultKind.Exception));
    }

    private void OnRadioStateChanged(object? sender, RadioStateChangedEventArgs e)
    {
        switch (e.PropertyChanged)
        {
            case RadioProperty.AleProgrammingRefusal:
                // Tracked through the SAME marshalled event stream the
                // sentinel completions arrive on, so "before the closing
                // bracket" is decided by real delivery order, not by a
                // snapshot sampled at some other moment.
                lock (_lock) _lastRefusal = _radio.State.Ale.ProgrammingRefusal;
                break;

            case RadioProperty.AleSync:
                Advance(_radio.State.Ale.LastSync);
                break;

            case RadioProperty.ConnectionState:
            case RadioProperty.ConnectionOpen:
                if (!_radio.IsConnectionOpen || _radio.Connection != ConnectionState.Ready)
                    AbandonForSessionDrop();
                break;
        }
    }

    /// <summary>Feed one sentinel completion to the open operation: an
    /// answered OPENING barrier in a clean dispatch slot sets the refusal
    /// watermark and releases the write; the CLOSING bracket resolves the
    /// outcome. Anything else is ignored.</summary>
    private void Advance(AleReadCompletion completion)
    {
        Operation operation;
        AleProgrammingOutcome outcome = default;
        bool resolved = false;
        bool rearm = false;
        bool release = false;

        lock (_lock)
        {
            if (_operation is null || completion.ReadId == 0) return;
            operation = _operation;

            if (!operation.WriteReleased)
            {
                if (completion.ReadId != operation.BarrierId) return;

                if (!completion.Answered)
                {
                    // BLOCKER 2: an unanswered barrier says the radio is not
                    // talking. Sending the write anyway would be programming
                    // blind — and a LATE answer to this very barrier could
                    // then credit the closing sentinel early and call a
                    // refused write Accepted. Nothing is sent.
                    outcome = new AleProgrammingOutcome(
                        AleProgrammingResult.Unverified, "the radio did not answer");
                    _operation = null;
                    resolved = true;
                }
                else if (_radio.PingAnswerDebt > 0)
                {
                    // AUDIT ROUND 2, BLOCKER: "my barrier completed and
                    // nothing is pending" does NOT prove the answer was MINE.
                    // A previously timed-out sentinel whose answer arrives
                    // late completes the next queued sentinel EARLY, and from
                    // then on every completion is shifted by one — so a
                    // bracket opened here would sit one answer ahead of
                    // itself and the write's own refusal would land outside
                    // its window (a refused write reporting Accepted).
                    // Re-arming cannot help: another barrier keeps the queue
                    // non-empty, which is precisely what stops the stray
                    // answer being discarded. Refusing SETTLES it — with
                    // nothing of ours queued the stray answer is discarded
                    // against an empty queue, the debt clears, and the
                    // operator's next press runs clean.
                    //
                    // CAMPAIGN MODE (D3) takes the SAME insight and spends the
                    // settle window itself rather than handing it to the
                    // operator: a campaign has no next press, so refusing here
                    // cascades through every remaining row. Ownership is
                    // RETAINED (_operation stays set, IsBusy stays true) and
                    // nothing of ours is queued, which is the condition the
                    // discard needs. ONE retry only.
                    if (operation.Campaign && !operation.DebtRetried)
                    {
                        operation.DebtRetried = true;
                        StartSettleTimer(operation);
                        return;
                    }
                    outcome = new AleProgrammingOutcome(
                        AleProgrammingResult.Faulted,
                        "the radio is behind on its sentinel answers — nothing was sent; try again",
                        AleProgrammingFaultKind.SentinelDebt);
                    _operation = null;
                    resolved = true;
                }
                else if (_radio.PendingPingCount > 0)
                {
                    // BLOCKER 1: another producer's sentinel is on the wire,
                    // so OUR closing sentinel would queue behind it and a
                    // foreign command could land between the write and the
                    // bracket. Wait for a clean slot instead; nothing is sent
                    // meanwhile, and the watermark is re-taken each time.
                    if (operation.BarrierAttempts >= MaxBarrierAttempts)
                    {
                        outcome = new AleProgrammingOutcome(
                            AleProgrammingResult.Faulted,
                            "the radio is busy answering another read — nothing was sent",
                            AleProgrammingFaultKind.QueueBusy);
                        _operation = null;
                        resolved = true;
                    }
                    else
                    {
                        rearm = true;
                    }
                }
                else
                {
                    // Reached only with BOTH counters clear — answered
                    // barrier, PingAnswerDebt == 0 (the completion was our
                    // own answer) and PendingPingCount == 0 (our closing
                    // sentinel will dispatch immediately). The same
                    // both-counters test guards the closing bracket in
                    // SendWriteStage; those are the only two places in this
                    // gate that read a counter at all.
                    //
                    // Every refusal drawn by anything queued before this
                    // operation is recorded by now — so this is the line
                    // above which a refusal belongs to THIS write.
                    operation.Watermark = _lastRefusal.Sequence;
                    operation.WriteReleased = true;
                    release = true;
                }
            }
            else
            {
                if (operation.ClosingId == 0 || completion.ReadId != operation.ClosingId) return;

                outcome = !operation.BracketClean
                    ? new AleProgrammingOutcome(AleProgrammingResult.Unverified,
                        "another read was in flight when the write went out — the radio did not confirm it")
                    : _lastRefusal.Sequence > operation.Watermark
                        ? new AleProgrammingOutcome(AleProgrammingResult.Refused, _lastRefusal.Line)
                        : completion.Answered
                            ? new AleProgrammingOutcome(AleProgrammingResult.Accepted, null)
                            : new AleProgrammingOutcome(AleProgrammingResult.Unverified,
                                "the radio did not answer");
                _operation = null;
                resolved = true;
            }
        }

        if (rearm) { ArmBarrier(operation); return; }
        if (release) { SendWriteStage(operation); return; }
        if (!resolved) return;

        // A session that dropped between the sentinel's release and this
        // delivery gets no callback (§4.3): the answer can no longer be
        // trusted, and the consumer clears its display on the same drop.
        // Delivered outside the lock — a consumer callback must never run
        // with the gate held.
        if (!_radio.IsConnectionOpen) return;
        operation.OnOutcome(outcome);
    }

    /// <summary>
    /// CAMPAIGN MODE'S SETTLE WINDOW (§5.3). Called with the gate's lock HELD
    /// and the operation still owned: the gate sends nothing, queues nothing,
    /// and simply waits, so Core's ping queue can empty and the stray answer
    /// that minted the debt can be discarded against it.
    /// </summary>
    private void StartSettleTimer(Operation operation)
    {
        operation.SettleTimer?.Dispose();
        operation.SettleTimer = new Timer(
            _ => OnSettleElapsed(operation), null, DebtSettleMs, Timeout.Infinite);
    }

    /// <summary>
    /// The settle window expired. Re-arm the barrier ONLY when BOTH counters
    /// are clear — a debt that is still standing (or a queue that has filled
    /// again) means the settle did not settle anything, and re-arming into it
    /// would just spend the operation's budget arriving at the same answer.
    /// </summary>
    private void OnSettleElapsed(Operation operation)
    {
        bool rearm = false;
        AleProgrammingOutcome outcome = default;

        lock (_lock)
        {
            if (!ReferenceEquals(_operation, operation)) return;   // resolved or abandoned
            operation.SettleTimer?.Dispose();
            operation.SettleTimer = null;

            if (_radio.PendingPingCount == 0 && _radio.PingAnswerDebt == 0)
            {
                rearm = true;
            }
            else
            {
                outcome = new AleProgrammingOutcome(
                    AleProgrammingResult.Faulted,
                    "the radio is behind on its sentinel answers — nothing was sent; try again",
                    AleProgrammingFaultKind.SentinelDebt);
                _operation = null;
            }
        }

        // Outside the lock, exactly as Advance resolves: the re-armed barrier
        // re-takes the watermark when it completes, and a consumer callback
        // never runs with the gate held.
        if (rearm) { ArmBarrier(operation); return; }
        if (!_radio.IsConnectionOpen) return;
        operation.OnOutcome(outcome);
    }

    /// <summary>The write, the closing bracket and the display read — issued
    /// back to back from a clean, ANSWERED opening barrier, so the write and
    /// the closing sentinel are adjacent on the wire.</summary>
    private void SendWriteStage(Operation operation)
    {
        if (!IsCurrent(operation)) return;      // abandoned while we unwound

        try
        {
            operation.SendWrite();
            operation.ClosingId = _radio.Ale.Synchronize();
            // Adjacency VERIFIED, on BOTH counters (audit round 3, BLOCKER —
            // the last member of the crediting family):
            //   PendingPingCount == 1 — our closing sentinel is the only
            //     QUEUED one, so Core dispatched its BAT ST immediately,
            //     right behind the write;
            //   PingAnswerDebt == 0   — and no stray answer is in flight that
            //     could complete it EARLY. A bare BAT ST (another producer's
            //     QueryBatteryState) raced in just before the write has no
            //     queue entry at all, so the pending count alone still read
            //     "clean" while its answer credited our closing sentinel and
            //     the write's own refusal landed after the verdict.
            // Either term failing means no verdict may be drawn from this
            // bracket.
            operation.BracketClean =
                _radio.PendingPingCount == 1 && _radio.PingAnswerDebt == 0;
            _ = operation.SendClosingRead();
        }
        catch (Exception ex)
        {
            Fault(operation, ex);
            return;
        }

        Advance(_radio.State.Ale.LastSync);     // late binding, as in ArmBarrier
    }
}
