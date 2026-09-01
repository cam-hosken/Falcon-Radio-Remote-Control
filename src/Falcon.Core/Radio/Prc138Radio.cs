using System.Globalization;
using Falcon.Core.Modes;
using Falcon.Core.Protocol;
using Falcon.Core.Transport;

namespace Falcon.Core.Radio;

/// <summary>
/// The PRC-138 backend: owns the transport, applies response lines to
/// <see cref="RadioState"/> via <see cref="ResponseParser"/> (standalone
/// parsing — Q1), runs the measured trigger table (Q5) and the connect
/// ritual (Q2/Q8), and exposes the v1 command surface. No UI dependencies;
/// every public event is marshalled through the SynchronizationContext
/// captured at construction (Q10) — no Core event ever raises on a
/// transport thread when a context is supplied.
/// </summary>
public sealed class Prc138Radio : IDisposable
{
    private readonly ITransport _transport;
    private readonly ResponseParser _parser;
    private readonly SynchronizationContext? _syncContext;
    private readonly TimeProvider _time;
    private readonly object _parseLock = new();
    private readonly object _connectionLock = new();

    public RadioState State { get; } = new();
    public SsbController Ssb { get; }
    public AleController Ale { get; }
    public HopController Hop { get; }

    public ConnectionState Connection { get; private set; } = ConnectionState.Disconnected;
    public bool IsInitialized => Connection == ConnectionState.Ready;
    public bool IsConnectionOpen => _transport.IsOpen;
    public string? RemotePort => _transport.PortName;

    public event EventHandler<RadioStateChangedEventArgs>? StateChanged;
    /// <summary>Every received line, verbatim (Console page RX log).</summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    /// <summary>Every sent command, verbatim (Console page TX log — nothing
    /// the app sends is ever invisible, plan §0 principle #4).</summary>
    public event EventHandler<LineSentEventArgs>? LineSent;
    public event EventHandler<RadioErrorEventArgs>? ErrorOccurred;
    /// <summary>Raised for every app-originated command burst the operator
    /// did not directly cause (trigger-table re-polls, FM-squelch cycle).</summary>
    public event EventHandler<CompensationAppliedEventArgs>? CompensationApplied;

    /// <param name="transport">The wire.</param>
    /// <param name="syncContext">Marshalling target for every public event
    /// (Q10); <c>SynchronizationContext.Current</c> when omitted.</param>
    /// <param name="time">The clock the RX-only bounce window measures against
    /// (round 13 D1). OPTIONAL and LAST on purpose: every existing positional
    /// call site — 18 of them across src, tests and bench at the time it was
    /// added — compiles unchanged. <c>TimeProvider.System</c> when omitted;
    /// the Core suite passes a manual-advance fake so a 500 ms window can be
    /// pinned without a 500 ms test.</param>
    public Prc138Radio(ITransport transport, SynchronizationContext? syncContext = null, TimeProvider? time = null)
    {
        _transport = transport;
        _syncContext = syncContext ?? SynchronizationContext.Current;
        _time = time ?? TimeProvider.System;
        _parser = new ResponseParser(State);

        Ssb = new SsbController(this);
        Ale = new AleController(this);
        Hop = new HopController(this);

        State.Changed += p => Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(p)));
        _transport.LineReceived += (_, e) => HandleLine(e.Line);
        _transport.WriteStarted += (_, e) => OnWriteStarted(e.Session, e.Sequence);
        _transport.LineWritten += (_, e) => OnLineWritten(e.Session, e.Sequence);
        _transport.TransportError += (_, e) => Post(() =>
            ErrorOccurred?.Invoke(this, new RadioErrorEventArgs("Serial port error: " + e.Error.Message, null)));
    }

    /// <summary>Marshal an event/callback onto the captured context. Every
    /// public Core event goes through here (Q10).</summary>
    internal void Post(Action action)
    {
        if (_syncContext is not null) _syncContext.Post(_ => action(), null);
        else action();
    }

    // ====================================================================
    // Q3 — sentinel barrier: SINGLE-OUTSTANDING-PING QUEUE.
    //
    // BAT ST is the completion barrier (Q2: answered in every mode; command
    // responses return in command order). Pings are queued; only the HEAD's
    // BAT ST is ever on the wire.
    //
    // Q2's OTHER half - "BATTERY is never unsolicited" - IS FALSE, and the
    // whole of the empty-clone-file defect lived in it (bench 2026-08-22,
    // round 15): at a MODE ENTRY one BAT ST draws TWO Battery Status lines
    // around the radio's IN_PROG chatter, and the owner's field console shows
    // 19 sends against 21 answers. That surplus line is what used to credit
    // the next queued ping. What keeps the stream aligned now is the STRAY
    // RULE in OnBatteryAnswer: an answer may only complete a sentinel whose
    // BAT ST has already left the queue for the wire.
    // When a BATTERY line arrives it completes the head; the next entry's
    // BAT ST goes out only then. A head that times out (the radio DOES
    // swallow commands — R6) is completed false and the next entry is
    // dispatched.
    //
    // This replaces the old orphan-answer credit ledger (~40 lines of
    // bookkeeping). The one behavior difference, deliberate and documented:
    // if a timed-out sentinel's answer arrives LATE, it completes the next
    // queued ping EARLY (true). The old design's own analysis (audit-6 H1)
    // already concluded early completion is strictly the lesser harm versus
    // spuriously failing a healthy ping — the ledger existed to protect a
    // case whose punishment is now benign. Proven by the line-injection race
    // tests in Q3PingQueueTests; outcome recorded in docs/tests.md.
    // ====================================================================

    private sealed class PingEntry
    {
        public required Action<bool> Callback;
        public int TimeoutMs;
        public Timer? Timer;
        public bool Completed;

        /// <summary>The transport (session, sequence) of THIS entry's own
        /// <c>BAT ST</c> (round 15 A0) — the handle its write is recognised
        /// by. Sequence 0 until the dispatch has a number for it, and forever
        /// if the transport refused the line. The SESSION half is not
        /// optional: the sequence restarts at 1 on every open, so after a
        /// reconnect an old session's report can carry the same number as this
        /// entry's line and would otherwise arm its clock before its
        /// <c>BAT ST</c> had been written at all — the very timeout A0
        /// exists to remove (audit round 1).</summary>
        public long Session;
        public long Sequence;

        /// <summary>Set when this entry's own line LEFT THE QUEUE for the
        /// wire. From that moment an answer can legitimately belong to it —
        /// the radio may answer before the writer thread reports completion —
        /// so this, not the flag below, is what the stray rule tests.</summary>
        public bool WriteStarted;

        /// <summary>Set once the port has ACCEPTED this entry's line — the
        /// only thing that arms its clock, and what makes the arming
        /// idempotent. A write that threw never sets it.</summary>
        public bool WriteObserved;
    }

    private readonly object _pingLock = new();
    private readonly List<PingEntry> _pings = [];

    /// <summary>The entry whose <c>BAT ST</c> is inside its
    /// <see cref="DispatchHeadLocked"/> call right now. Guarded by
    /// <c>_pingLock</c>. A test transport whose enqueue IS its write reports
    /// the line re-entrantly, BEFORE the dispatch has a number to store — this
    /// is how that report is recognised as the head's own rather than a
    /// stranger's. Null everywhere else, so a bare <c>BAT ST</c> written
    /// between dispatches can never claim it.</summary>
    private PingEntry? _dispatchingHead;

    /// <summary>
    /// Outstanding sentinel entries; the HEAD has its <c>BAT ST</c> on the
    /// wire and the rest are waiting for it. Test hook — and, since the ALE
    /// programming bracket (plan-ale-programming.md §4.3), a COORDINATION
    /// fact the app layer needs: only when this is 0 does a freshly queued
    /// sentinel dispatch IMMEDIATELY, which is what lets a caller put a write
    /// and its closing sentinel on the wire back to back with nothing of
    /// anyone else's in between. Pure data — reading it sends nothing.
    /// </summary>
    public int PendingPingCount { get { lock (_pingLock) return _pings.Count; } }

    // Sentinel ACCOUNTING (audit round 2, BLOCKER): every BAT ST this session
    // put on the wire, and every BATTERY line that came back — including the
    // ones the queue discards. The queue's documented late-answer credit
    // (above) means a timed-out sentinel whose answer arrives LATE completes
    // the NEXT queued sentinel early, and from then on every completion is
    // shifted by one until the extra answer is finally discarded against an
    // empty queue. Counting both sides is the only way an outside caller can
    // tell "my sentinel answered" from "somebody else's answer completed my
    // sentinel".
    private long _sentinelsSent;
    private long _sentinelAnswers;
    private long _strayAnswers;

    /// <summary>The exact <c>BAT ST</c> line, in one place — the accounting
    /// below counts THIS string, so every sender is counted, including a bare
    /// <see cref="QueryBatteryState"/> and an operator's console line.</summary>
    private const string SentinelLine = "BAT ST";

    /// <summary>
    /// How many dispatched sentinels are owed an answer BEYOND the one
    /// legitimately in flight (the queue head's). Zero means the sentinel
    /// stream is in step: the next BATTERY line belongs to the sentinel now
    /// on the wire. Non-zero means a previously timed-out (or bare) sentinel's
    /// answer is still out there and will complete somebody's sentinel EARLY —
    /// so no caller may treat a completion as proof of its own answer until
    /// this returns to 0.
    /// <para>Exposed for the ALE programming bracket (plan-ale-programming.md
    /// §4.3, audit round 2): it refuses to open a bracket while a debt stands,
    /// because a shifted credit would put the write outside its own window.
    /// Pure data — reading it sends nothing.</para>
    /// </summary>
    public int PingAnswerDebt
    {
        get
        {
            lock (_pingLock)
            {
                long inFlight = _pings.Count > 0 ? 1 : 0;   // only the head is on the wire
                return (int)Math.Max(0, _sentinelsSent - _sentinelAnswers - inFlight);
            }
        }
    }

    /// <summary>
    /// BATTERY lines this session that answered NOTHING: they arrived while
    /// the queue head was still enqueued-but-unwritten, so they cannot have
    /// been its answer (see <see cref="OnBatteryAnswer"/>). Pure data —
    /// reading it sends nothing.
    ///
    /// <para>On the bench radio this is the count of the extra answers a mode
    /// entry prints. Non-zero is NORMAL and means the rule did its job; it is
    /// the number that used to become a shifted sentinel stream instead.</para>
    /// </summary>
    public int StrayBatteryAnswers { get { lock (_pingLock) return (int)_strayAnswers; } }

    /// <summary>
    /// D20 (plan-clone-write-structural.md §2, owner report 2026-08-30 — "close
    /// and open the app each time? I get 50% failures on both android and
    /// windows"): RE-BASELINE the sentinel ledger, so <see cref="PingAnswerDebt"/>
    /// and <see cref="StrayBatteryAnswers"/> both read 0 at the instant of the
    /// call.
    ///
    /// <para><b>Why it exists.</b> These counters reset on <see cref="Connect"/>
    /// and nowhere else, so a failed campaign's standing debt outlived it and
    /// every in-session retry met that same debt at its first gated operation —
    /// which is why closing and reopening the app was the owner's workaround.
    /// This is that restart, without the restart. It removes debt INHERITANCE
    /// between attempts and NOTHING else: a debt minted DURING a campaign is
    /// that campaign's own and still faults its gate.</para>
    ///
    /// <para><b>THE ARITHMETIC, derived against the formula.</b>
    /// <see cref="PingAnswerDebt"/> is
    /// <c>max(0, _sentinelsSent - _sentinelAnswers - inFlight)</c> with
    /// <c>inFlight = _pings.Count &gt; 0 ? 1 : 0</c> — only the HEAD is ever on
    /// the wire (single-outstanding queue). Set <c>_sentinelAnswers = 0</c>,
    /// <c>_strayAnswers = 0</c> and <c>_sentinelsSent = onWire</c>, where
    /// <c>onWire</c> is 1 exactly when the head's own <c>BAT ST</c> really was
    /// handed to the transport — <see cref="PingEntry.WriteStarted"/>, or a
    /// stored non-zero <see cref="PingEntry.Sequence"/> — and 0 otherwise:</para>
    /// <list type="bullet">
    /// <item><b>Empty queue</b> — <c>0 - 0 - 0 = 0</c>. Nothing pending, nothing
    /// owed.</item>
    /// <item><b>ONE ping in flight across the reset</b> — <c>1 - 0 - 1 = 0</c> at
    /// the call. Its answer then credits it (<c>_sentinelAnswers</c> 1, the entry
    /// leaves the queue) and the ledger reads <c>1 - 1 - 0 = 0</c>: the callback
    /// runs normally, the reset neither cancelled it nor double-counted it. With a
    /// SECOND entry waiting, that completion dispatches it and counts its own send
    /// (<c>2 - 1 - 1 = 0</c>). If the in-flight ping TIMES OUT instead, the ledger
    /// reads <c>1 - 0 - 0 = 1</c> — a REAL debt, and the correct one: that sentinel
    /// was on the wire and its late answer is still out there (the Q3 late-answer
    /// doctrine).</item>
    /// <item><b>A head the transport REFUSED</b> (closed or closing —
    /// <see cref="SendLine"/> returns before it counts the send, so the ledger
    /// never counted that line) — <c>onWire</c> is 0, giving <c>0 - 0 - 1</c>,
    /// clamped to 0 now and still 0 when that entry times out. Counting it would
    /// mint a debt for a line the radio was never asked.</item>
    /// </list>
    ///
    /// <para><b>What it deliberately forgets.</b> A LATE answer to a sentinel sent
    /// before the reset is credited against a send this call zeroed, so it lands as
    /// an un-owed credit and can mask exactly one later debt. That is inherent to
    /// re-baselining — declaring bygones is the point — and is the same whichever
    /// way the assignments are written (holding <c>_sentinelsSent</c> and moving
    /// <c>_sentinelAnswers</c> to <c>sent - onWire</c> gives an identical ledger).
    /// The stray count is zeroed for the same reason: it is a per-session tally,
    /// and the A0 rule that produces it is untouched.</para>
    ///
    /// <para>It sends nothing, cancels nothing and completes nothing — no entry is
    /// read or written, so every pending callback still runs exactly once.</para>
    /// </summary>
    public void ResetSentinelLedger()
    {
        lock (_pingLock)
        {
            long onWire = _pings.Count > 0 && (_pings[0].WriteStarted || _pings[0].Sequence != 0) ? 1 : 0;
            _sentinelsSent = onWire;
            _sentinelAnswers = 0;
            _strayAnswers = 0;
        }
    }

    /// <summary>Queue a sentinel; the callback runs (marshalled) when its
    /// BATTERY answer arrives — i.e. once every command queued before it has
    /// been processed by the radio.</summary>
    public void Ping(Action onComplete) => Ping(ok => { if (ok) onComplete(); }, 0);

    /// <summary>Sentinel with a timeout: callback(true) on the BATTERY
    /// answer, callback(false) if it does not arrive within
    /// <paramref name="timeoutMs"/> (0 = wait forever). The callback ALWAYS
    /// runs exactly once — answered, timed out, or dropped.</summary>
    public void Ping(Action<bool> onComplete, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(onComplete);

        if (!IsConnectionOpen)
        {
            // Nothing can answer it — fail immediately rather than never.
            Post(() => onComplete(false));
            return;
        }

        var entry = new PingEntry { Callback = onComplete, TimeoutMs = timeoutMs };
        lock (_pingLock)
        {
            _pings.Add(entry);
            if (_pings.Count == 1) DispatchHeadLocked();
        }
    }

    /// <summary>
    /// Put the head entry's BAT ST on the wire. Caller holds _pingLock.
    ///
    /// <para><b>It starts NO timer</b> (round 15 A0, §13.4 H3). SendLine only
    /// ENQUEUES: behind the prompt gate the head's line can sit for seconds —
    /// bench-measured (P8, protocol.md), the first <c>BAT ST</c> of a connect
    /// is writable only 1 690 ms after the burst begins, while the first init
    /// sentinel's knob is 1 500 ms. A clock
    /// started here therefore timed out a command the radio had not been
    /// asked yet, and the late answer then completed the NEXT sentinel early,
    /// shifting the whole stream by one for the rest of the session. The
    /// clock starts in <see cref="OnLineWritten"/>, when the PORT ACCEPTS
    /// this entry's OWN write, correlated by (session, sequence) — never by
    /// matching the line text, since
    /// a bare <c>BAT ST</c> from the status card or the Console is the same
    /// string.</para>
    /// </summary>
    private void DispatchHeadLocked()
    {
        var head = _pings[0];

        long sequence;
        _dispatchingHead = head;
        try { sequence = Send("BAT", "ST"); }
        finally { _dispatchingHead = null; }

        if (head.Completed || head.WriteStarted) return;    // its own report got here first
        head.Session = _transport.Session;                  // read INSIDE the ping lock: a reconnect
        head.Sequence = sequence;                           // cannot interleave (Connect clears first)

        // 0 = the transport REFUSED the line (closed or closing). Nothing will
        // ever be written, so no LineWritten can ever start this entry's
        // clock: fall back to the enqueue-time timer rather than leave the
        // entry with none (ClearPendingPings is still the terminal path).
        if (sequence == 0) StartHeadTimerLocked(head);
    }

    /// <summary>Arm one entry's timeout. Caller holds _pingLock.</summary>
    private void StartHeadTimerLocked(PingEntry head)
    {
        if (head.TimeoutMs > 0 && head.Timer is null)
            head.Timer = new Timer(_ => TimeoutPing(head), null, head.TimeoutMs, Timeout.Infinite);
    }

    /// <summary>The head's line has LEFT THE QUEUE for the wire: from now on
    /// an answer can legitimately be its own (audit round 2). No clock starts
    /// here — the port has not accepted the bytes yet.</summary>
    private void OnWriteStarted(long session, long sequence)
    {
        lock (_pingLock)
        {
            var head = MatchHeadLocked(session, sequence);
            if (head is null) return;
            head.Session = session;
            head.Sequence = sequence;
            head.WriteStarted = true;
        }
    }

    /// <summary>The head's line has been ACCEPTED by the port: its timeout
    /// starts here and nowhere else — this is the whole of the A0 fix, and
    /// why a port write that BLOCKS is not charged to the sentinel's budget.
    ///
    /// <para>Correlation is by (SESSION, SEQUENCE), never by line text and
    /// never by the sequence alone (critic F24/F25; audit round 1). A bare
    /// <c>BAT ST</c> from <see cref="QueryBatteryState"/>, the Console's
    /// <see cref="RawCommand"/> or a settings card, written AHEAD of the head,
    /// carries a different sequence and arms nothing; a report from a session
    /// that has since been replaced carries a different SESSION and arms
    /// nothing, however long it took to arrive. A report that lands after
    /// <see cref="ClearPendingPings"/> finds no entry and arms nothing
    /// (critic F27).</para>
    ///
    /// <para>Handled INLINE, on the transport's writer thread, deliberately:
    /// it is a two-field compare and a timer arm under a lock nothing holds
    /// for long, and deferring it to the pool was what let a report outlive
    /// the session that produced it.</para>
    /// </summary>
    private void OnLineWritten(long session, long sequence)
    {
        lock (_pingLock)
        {
            var head = MatchHeadLocked(session, sequence);
            if (head is null || head.WriteObserved) return;
            head.Session = session;
            head.Sequence = sequence;
            head.WriteStarted = true;      // a fake may report both in one breath
            head.WriteObserved = true;
            StartHeadTimerLocked(head);
        }
    }

    /// <summary>
    /// The head, if this report is ITS line. Caller holds _pingLock.
    ///
    /// <para>Correlation is by (SESSION, SEQUENCE), never by line text and
    /// never by the sequence alone (critic F24/F25; audit round 1). A bare
    /// <c>BAT ST</c> from <see cref="QueryBatteryState"/>, the Console's
    /// <see cref="RawCommand"/> or a settings card, written AHEAD of the head,
    /// carries a different sequence and matches nothing; a report from a
    /// session that has since been replaced carries a different SESSION and
    /// matches nothing, however long it took to arrive. A report that lands
    /// after <see cref="ClearPendingPings"/> finds no entry at all (critic
    /// F27).</para>
    /// </summary>
    private PingEntry? MatchHeadLocked(long session, long sequence)
    {
        if (_pings.Count == 0) return null;                 // cleared, or nothing outstanding
        var head = _pings[0];
        if (head.Completed) return null;

        if (head.Sequence != 0)
            return head.Session == session && head.Sequence == sequence ? head : null;

        // No number stored yet: this can only be the head's own line if we are
        // inside its own dispatch (a test transport whose enqueue IS its write
        // reports re-entrantly, before the dispatch has a number to store).
        return ReferenceEquals(_dispatchingHead, head) ? head : null;
    }

    private void TimeoutPing(PingEntry entry)
    {
        lock (_pingLock)
        {
            if (entry.Completed) return;
            entry.Completed = true;
            entry.Timer?.Dispose();
            _pings.Remove(entry);
            if (_pings.Count > 0) DispatchHeadLocked();
        }
        Post(() => entry.Callback(false));
    }

    /// <summary>
    /// A BATTERY line arrived: complete the head — but only if the head has
    /// actually BEEN ASKED.
    ///
    /// <para><b>THE STRAY RULE (bench-measured 2026-08-22, round 15).</b> An
    /// answer can only belong to a sentinel whose <c>BAT ST</c> has already
    /// been WRITTEN. The radio prints an EXTRA <c>Battery Status</c> at a mode
    /// entry — one <c>BAT ST</c> drew two of them around the <c>IN_PROG</c>
    /// chatter in both wire runs (protocol.md, "BAT ST at a mode entry draws
    /// TWO Battery Status lines"), and the owner's own field console shows 19
    /// sends against 21 answers. Without this rule that surplus line credits
    /// whatever ping is next in the queue; because the next one is still
    /// behind the prompt gate, it is credited BEFORE ITS OWN COMMAND IS EVEN
    /// SENT, and from there every read completes one answer early — the
    /// address book, the schedules and the messages publish EMPTY and mark
    /// themselves <c>Read</c>. That is the owner's empty clone file, and A0's
    /// wire clock alone did not stop it (bench 2026-08-22, both runs).</para>
    ///
    /// <para>A stray completes nothing, credits nothing and raises no debt: it
    /// is not an answer to anything this queue asked for. Verified against the
    /// captures before it was written — in every recorded excess the surplus
    /// line arrives with NO sentinel written since the previous answer, so the
    /// rule separates them cleanly (no capture shows the excess landing after
    /// the next sentinel's write; that case would need a different rule and
    /// does not exist in the evidence).</para>
    ///
    /// <para>With an EMPTY queue the answer is still counted and discarded,
    /// exactly as before — that discard is what pays off a standing debt, and
    /// the late-answer doctrine above is untouched for any sentinel that
    /// really is on the wire.</para>
    /// </summary>
    private void OnBatteryAnswer()
    {
        PingEntry? head = null;
        lock (_pingLock)
        {
            if (_pings.Count > 0 && !_pings[0].WriteStarted)
            {
                // The head is still QUEUED — its command has not left for the
                // wire — so this line cannot be its answer, and nothing else is
                // owed one. (The test is "has it been ASKED", not "has the port
                // accepted it": the radio can answer while the writer thread is
                // still inside its port call, and an in-process one always
                // does — audit round 2.)
                _strayAnswers++;
                return;
            }

            // Counted BEFORE the credit: an answer that finds an empty queue
            // is discarded, and that discard is exactly what pays off a
            // standing debt.
            _sentinelAnswers++;
            if (_pings.Count > 0)
            {
                head = _pings[0];
                head.Completed = true;
                head.Timer?.Dispose();
                _pings.RemoveAt(0);
                if (_pings.Count > 0) DispatchHeadLocked();
            }
        }
        if (head is not null) Post(() => head.Callback(true));
    }

    /// <summary>Drop all outstanding sentinels, invoking their callbacks with
    /// false (exactly-once contract) — used on disconnect and watchdog retry,
    /// where no answers are coming.</summary>
    private void ClearPendingPings()
    {
        List<PingEntry> dropped;
        lock (_pingLock)
        {
            dropped = [.. _pings];
            foreach (var entry in dropped)
            {
                entry.Completed = true;
                entry.Timer?.Dispose();
            }
            _pings.Clear();
        }
        // Posted after the lock is released so a callback that pings again
        // cannot bind inside this clear.
        foreach (var entry in dropped)
        {
            var e = entry;
            Post(() => e.Callback(false));
        }
    }

    // ---- Connection -----------------------------------------------------

    private Timer? _initWatchdog;
    private bool _initRetried;
    private int _linesSinceConnect;

    /// <summary>How long to wait for the init sentinel before declaring the
    /// connection Failed. 0 (default) = automatic: 10 s at 9600 baud, scaled
    /// up for slower rates (the SHOW block alone takes ~2.5 s at 2400).</summary>
    public int InitializationTimeoutMs { get; set; }

    /// <summary>
    /// How long the FIRST of the two init sentinels waits before the second is
    /// dispatched (plan-clone-field-round2.md F7 / §3.5, decision A-3).
    ///
    /// <para><b>Why it is separate, and short.</b> Connecting outside SSB
    /// intermittently loses one init command (protocol.md, "connecting at
    /// <c>ALE&gt;</c>"), which is why <see cref="QueueInitSentinels"/> queues
    /// two. With the single-outstanding queue the second dispatches only when
    /// the first answers OR TIMES OUT, and both used to carry HALF the init
    /// window — 5 s at 9600. So a swallowed first sentinel cost the operator
    /// five seconds of a radio that had already gone quiet, which is exactly the
    /// shape reported from the field (2026-08-21, item 6: the data stops, then
    /// several seconds, then Operate).</para>
    ///
    /// <para><b>What it does not change.</b> The second sentinel keeps
    /// <c>half</c> — it is the outer fallback and the watchdog behind it is
    /// untouched. Nothing re-sends, nothing reads the parse path, and no ping
    /// entry gains a flag: a smaller number on the existing timeout is the whole
    /// mechanism. When the first sentinel answers promptly — the common case —
    /// Ready arrives at the answer exactly as before; when it answers LATE, the
    /// late answer credits the second sentinel and Ready still arrives at that
    /// same moment, at the cost of a transient, self-clearing
    /// <see cref="PingAnswerDebt"/> (Q3's documented late-answer doctrine).</para>
    /// </summary>
    public int FirstInitSentinelTimeoutMs { get; set; } = 1_500;

    /// <summary>The timeout actually used by the last Connect.</summary>
    public int EffectiveInitializationTimeoutMs { get; private set; }

    /// <summary>Connect ritual (Q2/Q8, re-validated live at 9600, probe R1):
    /// two bare CRs flush stale garbage from the radio's input buffer, echo
    /// off twice (the first can be eaten by the merged garbage), init
    /// queries, then the BAT ST sentinel — twice, because the radio
    /// occasionally swallows a command when connecting outside SSB.</summary>
    public void Connect(PortSettings settings)
    {
        if (IsConnectionOpen) return;

        _initRetried = false;
        Interlocked.Exchange(ref _linesSinceConnect, 0);
        ResetTriggerFlags();
        State.ResetForConnect();
        lock (_parseLock) { _parser.Reset(); }
        ClearPendingPings();
        // A new session is a fresh sentinel stream: nothing the previous one
        // dispatched can be answered into this one.
        lock (_pingLock) { _sentinelsSent = 0; _sentinelAnswers = 0; _strayAnswers = 0; }

        _transport.Open(settings);

        SetConnection(ConnectionState.Initializing);

        EffectiveInitializationTimeoutMs = InitializationTimeoutMs > 0
            ? InitializationTimeoutMs
            : (int)Math.Min(10_000L * 9600 / Math.Max(settings.BaudRate, 1), 300_000L);

        _initWatchdog ??= new Timer(InitWatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
        _initWatchdog.Change(EffectiveInitializationTimeoutMs, Timeout.Infinite);

        SendLine("");
        SendLine("");
        SetRemoteEcho(OnOff.Off);
        SetRemoteEcho(OnOff.Off);

        IssueInitQueries();

        // Sentinel twice (Q8: the radio occasionally swallows a command when
        // connecting outside SSB). With the single-outstanding queue the
        // second is dispatched only after the first answers or times out, so
        // each gets half the watchdog window; BOTH complete init (idempotent)
        // — the first answer wins, and a swallowed first sentinel still
        // leaves room for the second.
        QueueInitSentinels();

        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionOpen)));
    }

    /// <summary>Init parameter download — queries only: the SH block (whatever
    /// mode the radio is in) populates the mirror; PORT_R dumps the remote
    /// port configuration for the Settings page; POW reads the power level
    /// the ALE SH block does NOT carry.</summary>
    private void IssueInitQueries()
    {
        Show();
        QueryPortConfig();
        // F1 (plan-ale-broadcast-round.md §1): bare `POW` answers `POWER low`
        // at `ALE>` (captured: bench/transcripts/p20-amd-broadcast-20260823-233550.jsonl
        // step 8), and the ALE SH block carries no POWER line at all — so
        // connecting in ALE left the power mirror unreported for the session.
        // Unconditional: SSB answers twice (its SH already carried POWER —
        // an idempotent re-write of the same value) and HOP's SH carries
        // POWER too, so the extra read is belt-and-braces there. A refusal
        // at `HOP>` (UNPROBED, plan §9) is harmless — it lands on the
        // existing refusal path and nothing depends on the answer.
        QueryPowerLevel();
    }

    private void QueueInitSentinels()
    {
        int half = Math.Max(EffectiveInitializationTimeoutMs / 2, 1);
        // F7 (§3.5): the FIRST sentinel gets the short knob, the second keeps
        // `half`. Math.Min so a deliberately shortened init window is never
        // LENGTHENED by the knob — the first sentinel can only be the quicker of
        // the two. The watchdog's retry path calls this same method, so it
        // inherits the pair without a second place to keep in step.
        Ping(ok => { if (ok) CompleteInitialization(); }, Math.Min(FirstInitSentinelTimeoutMs, half));
        Ping(ok => { if (ok) CompleteInitialization(); }, half);
    }

    private void CompleteInitialization()
    {
        lock (_connectionLock)
        {
            if (Connection != ConnectionState.Initializing) return;
            _initWatchdog?.Change(Timeout.Infinite, Timeout.Infinite);
            Connection = ConnectionState.Ready;
        }
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionState)));
    }

    private void InitWatchdogTick(object? _)
    {
        // The radio occasionally swallows a command (bench-documented). If it
        // is clearly alive — it has sent us lines — re-run the idempotent
        // init once before giving up.
        lock (_connectionLock)
        {
            if (Connection != ConnectionState.Initializing) return;

            if (!_initRetried && Volatile.Read(ref _linesSinceConnect) > 0)
            {
                _initRetried = true;
                _initWatchdog?.Change(EffectiveInitializationTimeoutMs, Timeout.Infinite);
                ClearPendingPings();
                IssueInitQueries();
                QueueInitSentinels();
                return;
            }

            Connection = ConnectionState.Failed;
        }

        // Clean up FIRST: a throwing event subscriber must not leave the
        // port dangling open.
        var port = RemotePort;
        _transport.Close();
        ClearPendingPings();

        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionState)));
        RaiseError("Radio not responding on " + port + " — check power, cabling, and baud rate.", null);
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionOpen)));
    }

    private void SetConnection(ConnectionState value)
    {
        lock (_connectionLock)
        {
            if (Connection == value) return;
            Connection = value;
        }
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionState)));
    }

    /// <summary>
    /// The transport is ALREADY closed and its port is dead — bring Core's own
    /// state down to match, WITHOUT touching the port (round 13 D2, repair 3).
    ///
    /// <para><b>The hole it fills.</b> On a real cable yank the port flips
    /// <c>IsOpen</c> false BEFORE it emits <c>Disconnected</c> (the
    /// <see cref="Falcon.Core.Transport.ISerialPort"/> seam contract), so by
    /// the time the session's transport-error handler runs,
    /// <see cref="IsConnectionOpen"/> is already false and
    /// <see cref="Disconnect"/> returns at its first line. Core therefore
    /// stayed <c>Ready</c> after the cable was gone: the init watchdog and
    /// mode deadline still armed, pending sentinels still owed callbacks that
    /// nothing could ever answer. The session reported Failed while Core
    /// believed it was connected — the "clean disconnect" half of the
    /// 2026-08-19 ruling is exactly this gap.</para>
    ///
    /// <para><b>Why it is not just <c>Disconnect()</c>.</b> That one CLOSES
    /// the transport, and here the transport has already been closed by the
    /// caller; calling it again would reap a second time. This does the state
    /// half only, and deliberately has no <c>IsConnectionOpen</c> guard —
    /// that property being false is the very situation it exists for.</para>
    ///
    /// <para>Idempotent: the cleanup is safe to repeat and the events fire
    /// only on the transition, so a doubled call cannot storm subscribers.</para>
    /// </summary>
    public void NotifyTransportClosed()
    {
        _initWatchdog?.Change(Timeout.Infinite, Timeout.Infinite);
        CancelModeDeadline();
        ClearPendingPings();

        bool moved;
        lock (_connectionLock) moved = Connection != ConnectionState.Disconnected;

        // State first, exactly as Disconnect orders it: an in-flight watchdog
        // tick past its Change() must see a non-Initializing state and bail.
        SetConnection(ConnectionState.Disconnected);
        if (moved)
            Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionOpen)));
    }

    public void Disconnect()
    {
        if (!IsConnectionOpen) return;
        _initWatchdog?.Change(Timeout.Infinite, Timeout.Infinite);
        CancelModeDeadline();
        ClearPendingPings();
        // State first: an in-flight watchdog tick past its Change() must see
        // a non-Initializing state and bail (no spurious Failed).
        SetConnection(ConnectionState.Disconnected);
        _transport.Close();
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ConnectionOpen)));
    }

    // ---- Receive path ----------------------------------------------------

    private void HandleLine(string line)
    {
        Interlocked.Increment(ref _linesSinceConnect);
        Post(() => MessageReceived?.Invoke(this, new MessageReceivedEventArgs(line)));

        ParseResult result;
        lock (_parseLock)
        {
            // Snapshot the SH counter BEFORE the line is applied. A consumer
            // reacting to this very line (the HOP pane's post-select `SH`
            // fires from a state-changed handler DURING Parse, before the
            // reaction below runs) has already put the re-read on the wire —
            // so the arm must see it. See ArmChannelDomainRePoll.
            _shSentAtLineStart = Interlocked.Read(ref _shSent);
            result = _parser.Parse(line);
            ApplyReactions(result);
        }

        bool initializing = Connection == ConnectionState.Initializing;

        if (result.PayloadError is not null)
        {
            // During init, garbage from the dirty-buffer flush and our own
            // echoed commands are expected — keep them off the error surface
            // (still visible via MessageReceived / the Console page).
            if (!initializing) RaiseError(result.PayloadError.Message, line);
        }
        else if (!result.Handled)
        {
            if (!initializing) RaiseError("Unrecognized message: '" + line.Trim() + "'", line);
        }
    }

    private void RaiseError(string message, string? line)
        => Post(() => ErrorOccurred?.Invoke(this, new RadioErrorEventArgs(message, line)));

    /// <summary>Error surfacing for the mode controllers (marshalled).</summary>
    internal void RaiseControllerError(string message) => RaiseError(message, null);

    // ====================================================================
    // Q5 — the MEASURED trigger table (B8b, probes R7/R8/R9, 2026-08-02).
    // Re-reads are EVENT-DRIVEN only (Q4: a bare mode switch mutates
    // nothing — R3). Each row: an observed line implies a silent change →
    // mark the affected values unconfirmed → re-poll at the next SSB
    // prompt (SSB-domain commands are rejected at ALE>/HOP> prompts).
    // Every autonomous send is surfaced via CompensationApplied.
    // ====================================================================

    private string? _lastModemState;       // (a) MODEM engage/disengage
    private int? _lastNet;                 // (c) hop net select
    private bool _repollAgcBand;

    // THE PENDING RE-POLL, SPLIT BY DOMAIN (clone round 12 P4, from the P1
    // round-3 recorded consequence — see SatisfyPendingRePoll). One `SH`
    // answers both halves when it is asked at an SSB prompt; a `HOP>`-prompt
    // `SH` answers only the KEYLINE half, because the HOP block carries the
    // keyline and NOT the SSB channel domain. Producers arm the half (or
    // halves) their trigger actually stales.
    private bool _repollKeyline;
    private bool _repollChannelDomain;
    private string? _repollReason;

    // Serializes the arm's commit against a concurrent SH satisfaction (the P1
    // round-3 audit MINOR, deferred to P4). A LEAF lock: nothing else is ever
    // taken inside it, so it cannot participate in a cycle with _parseLock or
    // _pingLock (SendLine runs OUTSIDE _parseLock on caller threads, and
    // DispatchHeadLocked sends while holding _pingLock).
    private readonly object _repollLock = new();

    // (d) FM-squelch cycle — audio-only defect, unfalsifiable over serial;
    // compensation kept (owner decision), always visible via events.
    private bool _fmSquelchCyclePending;
    private bool _fmSquelchAwaitingOffReport;

    // (e) compression re-read (§9 B3 PRIMARY branch, capture 2026-08-18): a
    // confirmed MODE or DV line outside init means the value MAY have moved and
    // nothing reports it, so one `COM` is queued for the next SSB prompt.
    private bool _repollCompression;

    // ---- (f) DV STATE-SYNC (clone round 12 P4, the graduated D1 matrix) ----

    /// <summary>
    /// ONE OPEN SYNC WINDOW: an SSB-context <c>SH</c> whose answer block has
    /// not been closed by a prompt yet. Producer ARMING is deferred while any
    /// window stands — but every line still parses, still moves the mirrors and
    /// still moves the memories; the window merely COLLECTS, and the decision
    /// is taken once, at close.
    ///
    /// <para><b>Why a queue and not a flag</b> (audit round 1, finding 3).
    /// <see cref="SendLine"/> ENQUEUES — the transport's prompt-gated writer
    /// (probe R10) may hold several commands — so two reads can be outstanding
    /// at once. One boolean let the first block's prompt release the second
    /// block's suppression, and the second block then armed a read nobody
    /// needed. Windows are correlated one-to-one with the reads that opened
    /// them, in order, and each is evaluated on its own evidence.</para>
    /// </summary>
    /// <param name="ModeUnconfirmed">Was the modulation mirror already
    /// unconfirmed when this read went out? If so, the block re-confirming it
    /// is the SYNC LANDING — exactly what the read was for — not news.</param>
    /// <param name="DvUnconfirmed">The same question for DV.</param>
    /// <param name="Mode">The modulation memory as it stood at open.</param>
    /// <param name="Dv">The DV memory as it stood at open.</param>
    private sealed record SyncWindow(
        bool ModeUnconfirmed, bool DvUnconfirmed, ModulationMode? Mode, OnOff? Dv)
    {
        /// <summary>How many <c>MODE</c> / <c>DV</c> lines have landed inside
        /// the window. An <c>SH</c> block carries exactly ONE of each, so a
        /// SECOND report of the same token is something the block did not
        /// answer for — an ASYNC line — and that is the one signal that
        /// separates "the read told us" from "the radio told us something
        /// mid-read". See <see cref="CloseSyncWindow"/>.</summary>
        public int ModeReports { get; set; }
        public int DvReports { get; set; }
    }

    private readonly Queue<SyncWindow> _syncWindows = new();

    /// <summary>The COMPENSATION-SCOPED analog-squelch memory: the last value a
    /// `SQUELCH` line actually reported, which the DISPLAY unconfirm above must
    /// not erase. Never rendered — its one consumer is
    /// <see cref="ArmFmSquelchCycle"/>, which has to know squelch was ON when
    /// `MODE FM` arrives AHEAD of `SQUELCH` inside a compensating SH block (the
    /// SH block orders MODE before SQUELCH). Cleared only where the mirror
    /// itself is: connect and the zeroize boundary.</summary>
    private OnOff? _lastReportedAnalogSquelch;

    /// <summary>The same compensation memory for the MODULATION, and for the
    /// same reason. Row (d) fires its OFF→ON cycle on a modulation the radio
    /// REPORTED as different; <c>ParseResult.Changed</c> stopped being that
    /// question the moment P4 began unconfirming <c>ModulationMode</c> for
    /// display, because the setter counts every post-unconfirm re-confirm as a
    /// change. Reset exactly where the mirror is (connect, zeroize), so it
    /// reproduces the pre-P4 trigger — first sight included — line for
    /// line.</summary>
    private ModulationMode? _lastReportedModulation;

    /// <summary>And the same for DV, for the same reason — plus one of its own.
    /// P4's producers UNCONFIRM each other's mirror by design, so
    /// <c>ParseResult.Changed</c> is true for every re-confirm that follows,
    /// and an arm keyed on it re-arms off its own compensating block FOREVER.
    /// The in-flight window is what makes the count exactly one; THIS is what
    /// makes it terminate at all if the window is ever missed. Both producers
    /// therefore ask the trigger table's own question — "did the radio report a
    /// value DIFFERENT from the one it last reported" — which is also the
    /// question rows (a) and (c) have always asked.</summary>
    private OnOff? _lastReportedDv;

    private void ResetTriggerFlags()
    {
        _lastModemState = null;
        _lastNet = null;
        _repollAgcBand = false;
        lock (_repollLock)
        {
            _repollKeyline = false;
            _repollChannelDomain = false;
            _repollReason = null;
            _syncWindows.Clear();
            _lastReportedAnalogSquelch = null;
            _lastReportedModulation = null;
            _lastReportedDv = null;
        }
        _repollCompression = false;
        _repollCompressionReason = null;
        _fmSquelchCyclePending = false;
        _fmSquelchAwaitingOffReport = false;
        // The RX-only bounce memory is per-session recognizer state, like every
        // other memory here: a refusal from the previous session must not
        // swallow the first refusal of this one.
        _rxOnlySeen = false;
        CancelModeDeadline();
    }

    /// <summary>True while an FM-squelch OFF→ON cycle is owed — armed by an
    /// FM-property report with analog squelch confirmed ON, cleared when the
    /// cycle completes (or by the zeroize boundary). Projected to the app layer
    /// by <c>SsbSurface.IsFmSquelchCyclePending</c>: the clone campaign must
    /// not write <c>AnalogSquelch</c> while a cycle is still owed, because the
    /// cycle would overwrite it (plan-clone-round12 §3 leg 6). Pure data —
    /// reading it sends nothing.</summary>
    public bool IsFmSquelchCyclePending
        => Volatile.Read(ref _fmSquelchCyclePending) || Volatile.Read(ref _fmSquelchAwaitingOffReport);

    /// <summary>Raised (marshalled) when <see cref="IsFmSquelchCyclePending"/>
    /// changes — the campaign waits on the flag CLEARING, and a flag nobody is
    /// told about is a campaign that waits forever.</summary>
    public event EventHandler? FmSquelchCyclePendingChanged;

    /// <summary>Snapshot the flag, run <paramref name="mutate"/>, and raise the
    /// change event if the pair moved. Callers hold <see cref="_parseLock"/>
    /// (every call site is inside the parse path) — the event is POSTED, so it
    /// never fires under the lock.</summary>
    private void MutateFmSquelchPending(Action mutate)
    {
        bool before = _fmSquelchCyclePending || _fmSquelchAwaitingOffReport;
        mutate();
        bool after = _fmSquelchCyclePending || _fmSquelchAwaitingOffReport;
        if (before != after) Post(() => FmSquelchCyclePendingChanged?.Invoke(this, EventArgs.Empty));
    }

    private bool InSsb => State.OperatingMode.IsConfirmed && State.OperatingMode.Value == OperatingMode.Ssb;

    // ---- The RX-only keying refusal (round 13 D1) ------------------------

    /// <summary>The refusal's payload, EXACTLY as the radio prints it inside
    /// its asterisk fence — <c>***RX Only***</c> gives RawPayload
    /// <c>RX Only</c>. Case is the capture's; matched ordinally, because a
    /// case-insensitive match would also claim spellings no radio has
    /// printed.</summary>
    private const string RxOnlyPayload = "RX Only";

    /// <summary>The two ZEROIZE banners' payloads as the parser hands them up:
    /// the `*`-fenced line stripped of its asterisks and UPPER-CASED
    /// (<c>ParseResult.Payload</c>). Wire forms verbatim —
    /// <c>*** ZEROIZING RAM -- PLEASE WAIT ***</c> and
    /// <c>*** ZEROIZE COMPLETE ***</c> (bench/transcripts/r11-zeroize-* and
    /// r12-p1-*). Recognized here for ONE purpose: neither is an error, so
    /// neither is raised as one (plan-clone-field-round2.md F3, decision A-5).
    /// The settle machine keys on <c>RawPayload</c> separately and is not
    /// affected.</summary>
    private const string ZeroizingPayload = "ZEROIZING RAM -- PLEASE WAIT";
    private const string ZeroizeCompletePayload = "ZEROIZE COMPLETE";

    /// <summary>The operator's sentence for the refusal. Byte-exact and
    /// pinned: it carries NO wire token (plan §3.2 — the raw
    /// <c>***RX Only***</c> stays in the Console feed, where the evidence
    /// belongs).</summary>
    internal const string RxOnlyRefusalMessage = "Channel is receive-only — transmit key refused.";

    /// <summary>The bounce window: a recognized RX-only arriving within this
    /// many milliseconds of the previous one is the SAME edge, doubled by a
    /// chattering PTT contact, and is swallowed.
    ///
    /// <para><b>ASSUMED tier, and deliberately so</b> (plan round 13 §9). The
    /// refusal is EDGE-TRIGGERED — one per keyline edge, proven with an
    /// ELECTRONIC key that has no contacts to bounce — and the owner's handset
    /// observation is that a key-up sometimes doubles. Bounce doubling is a
    /// tens-of-milliseconds phenomenon and real edges are SECONDS apart, so
    /// 500 ms sits in a very wide gap; but no transcript captured a bounce
    /// pair byte-exactly (the ptt3 handset legs captured zero chunks), so the
    /// number itself is chosen, not measured. It is a const so the choice has
    /// exactly one home when the bench narrows it.</para></summary>
    internal const int RxOnlyBounceWindowMs = 500;

    private bool _rxOnlySeen;
    private long _rxOnlyLastTimestamp;

    /// <summary>Record a recognized refusal; true when it is a NEW event and
    /// false when it is a bounce duplicate of the previous one.
    ///
    /// <para>Called from <see cref="ApplyReactions"/>, which runs under
    /// <c>_parseLock</c> — the same lock every other recognizer's memory is
    /// mutated under, so the pair needs no lock of its own.</para></summary>
    private bool NoteRxOnlyRefusal()
    {
        long now = _time.GetTimestamp();
        bool bounce = _rxOnlySeen
            && _time.GetElapsedTime(_rxOnlyLastTimestamp, now).TotalMilliseconds < RxOnlyBounceWindowMs;

        // The window slides even on a swallowed duplicate: a bounce BURST is a
        // run of duplicates each close to the one before it, and anchoring on
        // the first would let the tail of a long burst through.
        _rxOnlySeen = true;
        _rxOnlyLastTimestamp = now;
        return !bounce;
    }

    private void ApplyReactions(ParseResult r)
    {
        // THE WIPE'S OWN BANNER OPENS THE SETTLE WINDOW; a prompt CLOSES it.
        //
        // CORRECTED 2026-08-19 (clone round 12 P2, the literal ZERO-first
        // ruling; bench/transcripts/r12-zero-prompts-20260819-061052.jsonl).
        // "ANY prompt settles a zeroize" was true of the only capture that
        // existed — an SSB-context wipe, whose banner arrives with NO prompt
        // before it. It is FALSE from the other two prompts, and the ruling is
        // exactly what made those reachable: an ALE-context `ZERO` answers
        // `IN_PROG`, a prompt, `PRG 1-3 CHAR SLF`, another prompt, and only
        // THEN the banner. Settling on the first of those prompts would have
        // declared the wipe done while the radio had not begun it — and the
        // campaign's next act would go out into a radio about to fall silent
        // for nine seconds.
        //
        // So the banner is the gate: prompts before it are the tail of
        // whatever came before, and the first prompt AFTER it is the settle.
        if (r.Token == "**" && r.RawPayload is { } banner
            && banner.StartsWith("ZEROIZING", StringComparison.Ordinal))
            NoteZeroizeStarted();

        // Checked before the switch so it cannot be missed by a case that
        // returns early.
        if (r.Token is "SSB>" or "ALE>" or "HOP>") NoteZeroizePrompt();

        switch (r.Token)
        {
            case "**":
                // §9 B2 — THE BANNER DISCRIMINATION. `**` is not one message:
                // `** ERROR **` is the generic syntax reject, and the radio
                // ALSO emits other `**`-fenced banners whose PAYLOAD is the
                // whole content. Rebadging them all as "** ERROR **" (which is
                // what the parser used to do to the refusal mirror) threw that
                // payload away and told the operator nothing. Only the exact
                // banner raises the generic line; anything else carries its own
                // payload VERBATIM — the radio's words, never invented ones.
                //
                // R13 (audit round 1, finding 8): the operator's sentence
                // carries NO radio token. It used to quote "** ERROR **"
                // verbatim; the raw line is still in the Console feed, which is
                // where the evidence belongs.

                // ROUND 13 D1 — THE RX-ONLY KEYING REFUSAL, recognized.
                // Contract: protocol.md "The RX-only keying refusal (CAPTURED
                // 2026-08-19…)", corrected to EDGE-TRIGGERED the same day.
                //
                // The refusal is `***RX Only***` — three asterisks each side,
                // mixed case, NO `** ERROR **` companion — so the parser hands
                // it here as a `**` banner whose RawPayload is exactly
                // "RX Only". Checked BEFORE the generic arms: the generic
                // "The radio answered: …" would put a wire spelling in front
                // of the operator, and this line has an operator sentence.
                //
                // ASYNC, and that is a contract (plan §3.6). The refusal is
                // elicited by a KEYLINE EDGE, not by anything the app sent, so
                // it must not complete, fail or perturb the single-outstanding
                // sentinel queue. It cannot: this whole `**` case only raises
                // errors — the ping queue is touched by `case "BATTERY"` alone
                // (OnBatteryAnswer) — so there is no pairing to bypass, and
                // the fixture suite pins that the queue is untouched across a
                // replay of the captured refusals.
                //
                // BOUNCE TOLERANCE, not a rate limiter. The electronic-key
                // control settled it: ONE refusal per keyline edge, and a
                // six-second hold produces one, not thirty. The handset's
                // occasional key-up double is CONTACT BOUNCE, so consecutive
                // duplicates inside a short window are ONE event. The window
                // SLIDES, so a whole bounce burst collapses rather than only
                // its first pair; at the measured edge spacing (seconds) two
                // real edges are never collapsed.
                //
                // AHEAD OF THE INIT GUARD, deliberately (D1 audit round 1,
                // MINOR 3). The `Initializing` suppression below exists for
                // ONE reason — the connect ritual's buffer-flush CRs turn
                // stale bytes into rejected commands, and that flood must not
                // reach the operator. A keyline refusal is not in that class:
                // no CR elicits one, only a KEY EDGE does, and an operator
                // holding the handset while the app connects is exactly the
                // case that produces one. The contract is ONE EVENT PER EDGE
                // with no session-start exception, so this arm runs first and
                // the guard keeps every OTHER banner class.
                //
                // THE ACCEPTED TRADE-OFF, recorded rather than left implicit
                // (audit round 2, 2026-08-20, manager ruling). SUSPECTED — no
                // capture shows it — a STALE `***RX Only***` sitting in a
                // queued receive buffer could replay during connect init and
                // raise one spurious toast, which the old ordering would have
                // eaten. That is the cheaper error: a swallowed REAL refusal
                // during init was a CONFIRMED contract violation, a single
                // possibly-spurious toast at connect is not, and no capture
                // proves the replay scenario exists at all. If one ever does,
                // the fix belongs here — not in a blanket init suppression.
                if (string.Equals(r.RawPayload, RxOnlyPayload, StringComparison.Ordinal))
                {
                    if (NoteRxOnlyRefusal()) RaiseError(RxOnlyRefusalMessage, null);
                    break;
                }

                // THE ZEROIZE BANNERS ARE NOT ERRORS (plan-clone-field-round2.md
                // F3, decision A-5). The wipe announces itself with
                // `*** ZEROIZING RAM -- PLEASE WAIT ***` and closes with
                // `*** ZEROIZE COMPLETE ***`. Both are `**`-fenced, so the
                // generic arm below raised them through ErrorOccurred, ConsoleFeed
                // logged them as Error entries and RadioSessionViewModel toasted
                // every Error entry — which is why the operator watched a clone
                // they had just authorised report "zeroize complete" as a fault
                // (field report, 2026-08-21, item 3).
                //
                // They are simply NOT RAISED. No new ConsoleEntryKind and no new
                // channel: the RAW LINE already reaches the Console through
                // MessageReceived (ConsoleFeed logs it as Rx), so nothing is lost
                // — the evidence was never on the error path. (`Auto` is the
                // COMPENSATION-send kind and would have been a lie.)
                //
                // `** ERROR **` still raises, and so does every other banner:
                // this arm names exactly two lines, matched on the payload the
                // parser hands up (`*`-trimmed, upper-cased).
                //
                // The SETTLE STATE MACHINE is untouched and runs ABOVE this
                // switch — NoteZeroizeStarted() has already seen the ZEROIZING
                // banner and NoteZeroizePrompt() still closes the window on the
                // next prompt. Suppressing the toast cannot desynchronise it.
                if (r.Payload is ZeroizingPayload or ZeroizeCompletePayload) break;

                // Expected during init: the buffer-flush CRs deliberately turn
                // stale garbage into one rejected command.
                if (Connection == ConnectionState.Initializing) break;

                if (r.Payload is null or "ERROR")
                    RaiseError("The radio rejected that command.", null);
                else
                    RaiseError("The radio answered: " + r.RawPayload, null);
                break;

            // §9 A1 — `PRESET DISABLED`: the answer to selecting a modem
            // preset the radio has locked out. It had NO dispatch key, so it
            // surfaced through the "Unrecognized message" path — the banner
            // that gave us its spelling. Operator-worded per R13: no wire token
            // reaches the operator, the Console keeps the evidence.
            case "PRESET":
                if (r.Payload == "DISABLED")
                    RaiseError("That modem preset is disabled — enable it on the Modem presets card first.", null);
                break;

            case "INV":
            case "INVALID":
                RaiseError("Radio rejected the value: " + r.Token + " " + (r.Payload ?? ""), null);
                break;

            case "ADDRESS":
                // " ADDRESS EXISTS " — ALE address names are global.
                if (r.Payload is not null && r.Payload.StartsWith("EXISTS"))
                    RaiseError("That ALE address name is already in use.", null);
                break;

            case "BATTERY":
                OnBatteryAnswer();
                break;

            // --- Trigger row (a): MODEM engage/disengage silently drags
            // AGC and BAND (R8: response says only "MODEM 1 T39"; SH diff
            // showed AGC SLOW→MED, BAND 2.7→3.0; MODEM OF restores exactly).
            // Only a CHANGE of a previously-reported value triggers: every
            // SSB/ALE SH block carries a MODEM line, and first sight is the
            // app learning the value, not a mutation.
            case "MODEM" when r.Payload is not null && !r.Payload.StartsWith("PRESET"):
                var modemNow = State.ActiveModem.IsConfirmed ? State.ActiveModem.Value : null;
                if (modemNow is not null)
                {
                    if (_lastModemState is not null && _lastModemState != modemNow)
                    {
                        State.UnconfirmAgcAndBandwidth();
                        _repollAgcBand = true;
                        _repollReason = "MODEM change silently alters AGC/bandwidth (probe R8)";
                    }
                    _lastModemState = modemNow;
                }
                break;

            // --- Trigger row (b): an ALE call changes the current channel,
            // announced ONLY by the CALLING/SENDING line's "CHANNEL: nn";
            // the channel-stored freq/AGC/BW change with it unreported (R7).
            case "CALLING":
            case "SENDING":
                if (r.Payload is not null && r.Payload.Contains("CHANNEL:"))
                {
                    State.UnconfirmChannelDomain();
                    ArmChannelDomainRePoll("ALE call changed the channel; channel-stored values unreported (probe R7)");
                }
                break;

            // --- Trigger row (c): selecting a hop net silently changes the
            // SSB current channel (R9b — the one truly silent mutation).
            // NET lines also appear in every HOP SH block, so only a CHANGE
            // of a previously-reported net triggers; "Generating Hopset..."
            // (which only fires on an actual selection) always does.
            case "NET":
                if (State.Hop.CurrentNet.IsConfirmed)
                {
                    int net = State.Hop.CurrentNet.Value;
                    if (_lastNet is not null && _lastNet != net)
                    {
                        State.UnconfirmChannelDomain();
                        ArmChannelDomainRePoll("hop net select silently changes the SSB channel (probe R9b)");
                    }
                    _lastNet = net;
                }
                break;

            case "GENERATING":
                State.UnconfirmChannelDomain();
                ArmChannelDomainRePoll("hopset generation implies net selection; SSB channel domain unconfirmed (probe R9b)");
                break;

            // --- §9 B1: the coupler tune blanks the RX chip. TUNING and every
            // tune-TERMINAL line UNCONFIRM the keyline (deliberately — the tune
            // lines carry no keyline report), so the chip never returned to RX
            // on its own. The honest repair is a RE-READ, never a fabricated
            // KEY OFF, and it arms the SAME shared flag rows (b)/(c) use: two
            // producers therefore COALESCE into ONE `SH` at the next SSB prompt
            // by construction. Nothing is unconfirmed here — a tune changes no
            // channel value; the SH block simply re-reports the keyline.
            //
            // ARBITRATION — TRUE COALESCING (§9 B1's own mechanism statement:
            // "the flag COALESCES; the Core arm is skipped or the VM send
            // subsumed"). Two audit rounds cut this twice:
            //
            //   round 1 found that arming unconditionally put a SECOND `SH` on
            //   the wire for one HOP tune (HopViewModel sends its own at the
            //   HOP prompt, Core fired again at the next SSB prompt);
            //   round 2 found that the fix — skipping the arm outside SSB —
            //   left a STANDALONE HOP retune with NO re-poll at all, because
            //   the view model only sends inside a SELECT flow. The RX chip
            //   stayed blank until some unrelated read, which is exactly what
            //   §9 B1 forbids ("re-confirm → RX regardless of outcome").
            //
            // So the arm is unconditional again, and the DISSOLUTION moved to
            // where it belongs: ANY issued `SH` satisfies the pending re-poll
            // (see SatisfyPendingRePoll / ArmChannelDomainRePoll). Whoever
            // sends first wins — if a consumer sends, Core's arm dissolves; if
            // nobody does, Core fires its one `SH` at the next SSB prompt.
            // ONE flag, ONE clearing rule, no per-caller special cases.
            //
            // FLAG-SPLIT (P4): a tune stales the KEYLINE and nothing else, so
            // it arms the keyline half — which ANY `SH` satisfies, the HOP
            // pane's post-select one included. That is what keeps the
            // standalone-HOP-tune and select-flow one-SH counts green.
            case "TUNE" when r.Payload is "COMPLETE" or "MARGINAL" or "FAULT" or "FAIL":
                ArmKeylineRePoll("coupler tune reports no keyline state; re-reading to re-confirm it");
                break;

            // --- Trigger row (d) arming: any FM-domain report while analog
            // squelch is CONFIRMED on (never a default — the old app was
            // burned arming this off enum defaults).
            case "FMDEV":
            case "FMSQ_TYPE":
            case "FMSQUELCH":
            case "FMTONE":
                ArmFmSquelchCycle();
                break;

            case "SQUELCH":
                // THE COMPENSATION MEMORY, updated from the report itself and
                // never from the display mirror — the P4 DV producer unconfirms
                // AnalogSquelch for DISPLAY, and the FM-squelch cycle still has
                // to know what the radio last said.
                if (State.AnalogSquelch.IsConfirmed) _lastReportedAnalogSquelch = State.AnalogSquelch.Value;
                if (_fmSquelchAwaitingOffReport && _lastReportedAnalogSquelch == OnOff.Off)
                {
                    MutateFmSquelchPending(() =>
                    {
                        _fmSquelchAwaitingOffReport = false;
                        _fmSquelchCyclePending = false;
                    });
                    Compensate("FM-squelch cycle: restoring analog squelch", "SQ ON");
                }
                break;

            // --- §9 B3 (PRIMARY branch, capture 2026-08-18 P-2 step c): a
            // confirmed DV line means compression MAY have moved and nothing
            // says so. One `COM` is queued for the next SSB prompt.
            case "DV":
                if (r.Changed) ArmCompressionRepoll("a digital-voice change can move compression");
                if (!State.DigitalVoice.IsConfirmed) break;
                // The MEMORY moves whether or not the producer is deferred: a
                // window COLLECTS, it never blinds anything.
                bool dvReallyMoved = RememberDv(State.DigitalVoice.Value);
                // --- Trigger row (f), P4: the DV half of the state sync.
                if (dvReallyMoved && IsInitialized && ArmDvSync(
                        "a digital-voice change silently forces USB, analog squelch ON and a bandwidth move (D1 matrix)"))
                    State.UnconfirmDvForcedValues();
                break;

            case "MODE":
                if (!State.ModulationMode.IsConfirmed) break;
                // The COMPENSATION-SCOPED modulation memory (P4), taken before
                // anything below can move the display mirror: "the radio
                // reported a modulation different from the one it last
                // reported". Reproduces the pre-P4 `r.Changed` question for row
                // (d) exactly, and is immune to P4's own display unconfirm.
                var modulationNow = State.ModulationMode.Value;
                bool modulationReallyMoved = RememberModulation(modulationNow);
                if (!r.Changed) break;
                ArmCompressionRepoll("a modulation change can move compression (probe R5: the FM cascade carries a COMPRESS line)");
                // --- Trigger row (f), P4: the MODE half. EVERY changed MODE,
                // with no DV-ON condition — the R4 auto-RESTORE means a mode
                // change can flip DV in EITHER direction (departure from
                // USB/LSB suspends a confirmed-ON; return to USB/LSB silently
                // restores a suspension the mirror cannot see).
                // Keyed on the MEMORY for the same reason the DV half is: a
                // re-confirm of the value already standing is not a change the
                // radio made, and an arm that treated it as one would re-arm
                // off its own compensating block.
                if (modulationReallyMoved && IsInitialized && ArmDvSync(
                        "a modulation change silently auto-suspends or auto-restores digital voice (probe R4)"))
                    State.UnconfirmDigitalVoice();
                // Row (d) keys on the MEMORY, not on `r.Changed`: a re-confirm
                // of the value the radio already reported (P4's display
                // unconfirm is the only way to reach one) must not fabricate
                // this trigger.
                if (modulationReallyMoved && modulationNow == ModulationMode.Fm)
                {
                    ArmFmSquelchCycle();
                }
                else if (modulationReallyMoved && InSsb && _fmSquelchCyclePending
                         && modulationNow is ModulationMode.Usb or ModulationMode.Lsb)
                {
                    // Back on USB/LSB after an FM-property change: squelch is
                    // (audibly) broken until cycled while still reporting ON.
                    MutateFmSquelchPending(() => _fmSquelchAwaitingOffReport = true);
                    Compensate("FM-squelch cycle: analog squelch needs an OFF→ON cycle after FM changes", "SQ OFF");
                }
                break;

            // --- Re-polls fire at an SSB prompt: SSB-domain commands are
            // rejected at ALE>/HOP> prompts (session-18).
            case "SSB>":
                // THIS prompt closes the oldest open sync window, and closing is
                // UNCONDITIONAL — see CloseSyncWindow. Done FIRST so anything
                // the evaluation arms goes out on THIS prompt, and so a re-poll
                // fired below opens its own fresh window.
                CloseSyncWindow();
                bool wantsShow, wantsAgcBand;
                string reason;
                lock (_repollLock)
                {
                    wantsShow = IsInitialized && (_repollKeyline || _repollChannelDomain);
                    wantsAgcBand = IsInitialized && !wantsShow && _repollAgcBand;
                    reason = _repollReason ?? "trigger-table re-poll";
                    if (wantsShow || wantsAgcBand)
                    {
                        _repollKeyline = false;
                        _repollChannelDomain = false;
                        _repollAgcBand = false;
                        _repollReason = null;
                    }
                }
                // Sent outside the leaf lock; the SH's own SatisfyPendingRePoll
                // takes it again (and is what opens the in-flight window).
                if (wantsShow) Compensate(reason, "SH");        // SH re-confirms the whole channel domain
                else if (wantsAgcBand) Compensate(reason, "AG", "BA");  // queries; answers are the read-back
                if (IsInitialized && _repollCompression)
                {
                    // Its own send: `SH` does NOT carry a COMPRESS line
                    // (protocol.md SSB SH block), so folding it into the row
                    // above would leave the mirror exactly as stale as before.
                    _repollCompression = false;
                    Compensate(_repollCompressionReason ?? "compression re-read", "COM");
                    _repollCompressionReason = null;
                }
                CompleteModeChange(OperatingMode.Ssb);
                break;

            // PROMPT-FAMILY HYGIENE: the radio is answering out of another
            // mode's family now, so no outstanding SSB `SH` block can still
            // complete. Retire the windows rather than leave them standing over
            // a whole excursion — see RetireSyncWindows.
            case "ALE>":
                RetireSyncWindows();
                CompleteModeChange(OperatingMode.Ale);
                break;

            case "HOP>":
                RetireSyncWindows();
                CompleteModeChange(OperatingMode.Hop);
                break;
        }
    }

    /// <summary>Row (d): arm the FM-squelch cycle if analog squelch was last
    /// REPORTED on. Reads the compensation memory rather than the display
    /// mirror (P4): the mirror can be unconfirmed by the DV producer at the
    /// exact moment the compensating SH block delivers `MODE FM` ahead of its
    /// `SQUELCH` line, and the cycle would then silently stop arming. Still
    /// never a default — the memory is null until a `SQUELCH` line reports
    /// one.</summary>
    private void ArmFmSquelchCycle()
    {
        if (_lastReportedAnalogSquelch == OnOff.On)
            MutateFmSquelchPending(() => _fmSquelchCyclePending = true);
    }

    private string? _repollCompressionReason;

    /// <summary>Queue ONE <c>COM</c> for the next SSB prompt. Init is excluded
    /// for the same reason every other row is: the connect ritual's own answers
    /// are the app LEARNING the values, not the radio mutating them.</summary>
    private void ArmCompressionRepoll(string reason)
    {
        if (Connection == ConnectionState.Initializing) return;
        _repollCompression = true;
        _repollCompressionReason ??= reason;
    }

    /// <summary>Send app-originated commands, visibly: each goes through the
    /// normal send path (LineSent) AND is announced via CompensationApplied.</summary>
    private void Compensate(string reason, params string[] commands)
    {
        foreach (var command in commands) SendLine(command);
        Post(() => CompensationApplied?.Invoke(this, new CompensationAppliedEventArgs(reason, commands)));
    }

    // ====================================================================
    // X13 — THE ZEROIZE SETTLE STATE MACHINE (clone round 12 §3 leg 2).
    //
    // WHY IT LIVES IN CORE. `ZERO` answers a banner and then the radio goes
    // SILENT for seconds while it wipes RAM (captured 2026-08-18,
    // bench/transcripts/r12-p1-*: the banner, then three NO-BYTES polls, then
    // "*** ZEROIZE COMPLETE ***", then the prompt — 9.4 s in all, in the SAME
    // SESSION with no reconnect). A campaign cannot poll this itself: `Send`
    // is internal and `RawCommand("")` rejects whitespace, so there is no
    // callable seam for a bare CR. Core owns the poll and the campaign AWAITS.
    //
    // WHY BARE CRs AND NOT `Ping()`. A sentinel carries LATE-ANSWER DEBT: a
    // timed-out BAT ST whose answer arrives late completes the NEXT sentinel
    // early (see the Q3 note above), and inside a multi-second silence that is
    // precisely the failure mode. A bare CR asks for nothing but the prompt.
    // ====================================================================

    /// <summary>The settle bound. Exceeding it FAULTS the campaign loudly
    /// rather than leaving it waiting forever.</summary>
    public int ZeroizeSettleTimeoutMs { get; set; } = 30_000;

    /// <summary>How often the bare-CR prompt poll goes out while settling.</summary>
    public int ZeroizeSettlePollMs { get; set; } = 1_000;

    private readonly object _zeroizeLock = new();
    private Timer? _zeroizePoll;
    private Timer? _zeroizeDeadline;
    private bool _zeroizeSettling, _zeroizeSettled, _zeroizeFaulted;

    /// <summary>The <c>ZEROIZING</c> banner has arrived, so the radio really is
    /// wiping and the NEXT prompt is the settle. Until then a prompt is the
    /// tail of whatever the radio was doing when the wipe was sent — see the
    /// note in <see cref="ApplyReactions"/>.</summary>
    private bool _zeroizeStarted;

    /// <summary>A <c>ZERO</c> has been sent and the prompt has not come back
    /// yet.</summary>
    public bool IsZeroizeSettling { get { lock (_zeroizeLock) return _zeroizeSettling; } }

    /// <summary>The radio answered a prompt again after the last
    /// <c>ZERO</c> — the campaign's go-ahead.</summary>
    public bool ZeroizeSettled { get { lock (_zeroizeLock) return _zeroizeSettled; } }

    /// <summary>The settle bound expired with no prompt. The campaign must
    /// FAULT: nothing below this can be trusted.</summary>
    public bool ZeroizeFaulted { get { lock (_zeroizeLock) return _zeroizeFaulted; } }

    /// <summary>Arm the settle machine. Called by
    /// <see cref="Modes.SsbController.ZeroizeRadio"/> immediately after the
    /// <c>ZERO</c> goes out — never by the app layer, which has no way to reach
    /// it except through that one guarded builder.</summary>
    internal void BeginZeroizeSettle()
    {
        lock (_zeroizeLock)
        {
            StopZeroizeTimersLocked();
            _zeroizeSettling = true;
            _zeroizeSettled = false;
            _zeroizeFaulted = false;
            _zeroizeStarted = false;
            _zeroizePoll = new Timer(_ => PollZeroizePrompt(), null, ZeroizeSettlePollMs, ZeroizeSettlePollMs);
            _zeroizeDeadline = new Timer(_ => FaultZeroize(), null, ZeroizeSettleTimeoutMs, Timeout.Infinite);
        }
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ZeroizeSettle)));
    }

    private void PollZeroizePrompt()
    {
        lock (_zeroizeLock) { if (!_zeroizeSettling) return; }
        SendLine("");   // the internal send path — the only actor allowed to poll here
    }

    private void FaultZeroize()
    {
        lock (_zeroizeLock)
        {
            if (!_zeroizeSettling) return;
            _zeroizeSettling = false;
            _zeroizeFaulted = true;
            StopZeroizeTimersLocked();
        }
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ZeroizeSettle)));
        RaiseError(
            $"The radio did not answer within {ZeroizeSettleTimeoutMs / 1000} s of the wipe — the clone cannot continue.",
            null);
    }

    /// <summary>The wipe's own banner arrived: the radio has BEGUN, so the next
    /// prompt is a settle rather than an echo.</summary>
    private void NoteZeroizeStarted()
    {
        lock (_zeroizeLock)
        {
            if (_zeroizeSettling) _zeroizeStarted = true;
        }
    }

    /// <summary>A prompt arrived. If a zeroize was settling AND the radio has
    /// said it started, THIS is the settle: run the boundary and publish.</summary>
    private void NoteZeroizePrompt()
    {
        lock (_zeroizeLock)
        {
            if (!_zeroizeSettling || !_zeroizeStarted) return;
            _zeroizeSettling = false;
            _zeroizeSettled = true;
            StopZeroizeTimersLocked();
        }
        OnZeroized();
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ZeroizeSettle)));
    }

    /// <summary>
    /// THE ZEROIZE BOUNDARY, defined exactly (plan §3 leg 2): every mirrored
    /// store resets AND says so, and every trigger/compensation flag resets —
    /// including a tune re-poll armed before the wipe, which must never fire
    /// into the settle window (§9 B1).
    /// <para>The TRANSPORT is deliberately untouched: the queue, the parser and
    /// the ping accounting all belong to a session that is still alive, and the
    /// settle poll — not a reconnect — owns the sequencing.</para>
    /// </summary>
    private void OnZeroized()
    {
        MutateFmSquelchPending(ResetTriggerFlags);
        State.ResetAfterZeroize();
    }

    /// <summary>Caller holds <see cref="_zeroizeLock"/>.</summary>
    private void StopZeroizeTimersLocked()
    {
        _zeroizePoll?.Dispose();
        _zeroizePoll = null;
        _zeroizeDeadline?.Dispose();
        _zeroizeDeadline = null;
    }

    // ---- Mode selection (30 s deadline — deliberately NOT a Ping) ---------

    private Timer? _modeDeadline;
    private OperatingMode? _pendingModeTarget;
    private readonly object _modeLock = new();

    /// <summary>Mode-change confirmation deadline (ms). The switch itself is
    /// a single command; the radio confirms with the new mode's prompt.</summary>
    public int ModeChangeTimeoutMs { get; set; } = 30_000;

    public bool IsModeChangePending { get { lock (_modeLock) return _pendingModeTarget is not null; } }

    /// <summary>Send ONLY the mode command — no re-read (Q4: bare switches
    /// mutate nothing, probe R3; re-reads are event-driven via the trigger
    /// table).</summary>
    public void SelectMode(OperatingMode mode)
    {
        lock (_modeLock)
        {
            _pendingModeTarget = mode;
            _modeDeadline ??= new Timer(ModeDeadlineTick, null, Timeout.Infinite, Timeout.Infinite);
            _modeDeadline.Change(ModeChangeTimeoutMs, Timeout.Infinite);
        }
        Send(mode.ToCommand());
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ModeChangePending)));
    }

    public void SelectSsb() => SelectMode(OperatingMode.Ssb);
    public void SelectAle() => SelectMode(OperatingMode.Ale);
    public void SelectHop() => SelectMode(OperatingMode.Hop);

    private void CompleteModeChange(OperatingMode arrived)
    {
        bool completed = false;
        lock (_modeLock)
        {
            if (_pendingModeTarget == arrived)
            {
                _pendingModeTarget = null;
                _modeDeadline?.Change(Timeout.Infinite, Timeout.Infinite);
                completed = true;
            }
        }
        if (completed)
            Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ModeChangePending)));
    }

    private void ModeDeadlineTick(object? _)
    {
        OperatingMode? target;
        lock (_modeLock)
        {
            target = _pendingModeTarget;
            _pendingModeTarget = null;
        }
        if (target is null) return;
        Post(() => StateChanged?.Invoke(this, new RadioStateChangedEventArgs(RadioProperty.ModeChangePending)));
        RaiseError($"Mode change to {target} was not confirmed within {ModeChangeTimeoutMs / 1000} s.", null);
    }

    private void CancelModeDeadline()
    {
        lock (_modeLock)
        {
            _pendingModeTarget = null;
            _modeDeadline?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    // ---- Send path ---------------------------------------------------------

    /// <summary>Returns the transport's write SEQUENCE for the line (0 when
    /// nothing was queued) — the ping queue's correlation handle (A0).</summary>
    internal long Send(params string?[] parts) => SendLine(CommandFactory.Build(parts));

    private long SendLine(string command)
    {
        if (!IsConnectionOpen) return 0;
        long sequence = _transport.SendLine(command);
        SatisfyPendingRePoll(command);
        // Counted here, not at the ping queue's dispatch, so EVERY sentinel on
        // the wire is accounted for — the queue's own, a bare BAT ST from the
        // status card, an operator's console line. Each of them consumes a
        // BATTERY answer, so each of them can shift a credit.
        if (command == SentinelLine) lock (_pingLock) _sentinelsSent++;
        Post(() => LineSent?.Invoke(this, new LineSentEventArgs(command)));
        return sequence;
    }

    /// <summary>
    /// THE COALESCING RULE (§9 B1). The pending channel-domain re-poll asks
    /// the radio ONE question — "re-read the block" — and an <c>SH</c> IS that
    /// read, whoever sent it. So any issued <c>SH</c> SATISFIES the pending
    /// flag and dissolves it: the HOP pane's post-select <c>SH</c>, the channel
    /// select's <c>CH</c>+<c>SH</c> pair, an operator's console line, and
    /// Core's own compensation alike.
    ///
    /// <para>Placed on SEND and in ONE place rather than per caller: a
    /// caller-by-caller rule is what produced two <c>SH</c> commands for one
    /// HOP tune in audit round 1, and a mode-conditional arm is what starved a
    /// standalone HOP retune in round 2.</para>
    ///
    /// <para><b>The consequence, recorded.</b> A <c>HOP&gt;</c>-prompt
    /// <c>SH</c> answers the HOP block, which carries the KEYLINE (so the tune
    /// half of the flag really is satisfied) but NOT the SSB channel domain. A
    /// hop-net select that dissolves the flag this way therefore leaves the
    /// channel values UNCONFIRMED rather than re-read — honest, and rendered
    /// as "—", but less prompt than a second read would be. That is the price
    /// of one flag and one rule; splitting the flag by producer is a plan
    /// amendment, not an implementation choice.</para>
    ///
    /// <para><b>THE CONSEQUENCE, AND ITS REPAIR (clone round 12 P4).</b> The
    /// paragraph above used to end "splitting the flag by producer is a plan
    /// amendment, not an implementation choice" — the amendment landed
    /// (plan-clone-round12 §6 P4, 2026-08-19) and this is it. The pending
    /// re-poll is TWO halves over ONE rule:
    /// <list type="bullet">
    /// <item><c>_repollKeyline</c> — satisfied by ANY <c>SH</c>. Every SH
    /// block, in every mode, carries a KEY line, so whoever asks answers it.</item>
    /// <item><c>_repollChannelDomain</c> — satisfied only by an SH asked in an
    /// SSB CONTEXT, because only the SSB block carries the channel domain. A
    /// hop-net select whose pane sends its own <c>SH</c> at the <c>HOP&gt;</c>
    /// prompt therefore still re-reads the channel values at the next
    /// <c>SSB&gt;</c> — the pre-round-12 promptness, restored — while the
    /// standalone-HOP-tune and select-flow counts stay at one SH apiece,
    /// because a tune arms only the keyline half.</item>
    /// </list>
    /// The DV/MODE sync producers arm BOTH halves.</para>
    ///
    /// <para><b>The SYNC WINDOW opens here.</b> Every SSB-context <c>SH</c>
    /// opens one, correlated to itself, and the next <c>SSB&gt;</c> closes it —
    /// see <see cref="SyncWindow"/> and <see cref="CloseSyncWindow"/>.</para>
    ///
    /// <para><c>_repollAgcBand</c> is deliberately NOT cleared here: different
    /// flag, different producer (row (a), the MODEM drag), its own
    /// <c>AG</c>/<c>BA</c> re-poll.</para>
    /// </summary>
    private void SatisfyPendingRePoll(string command)
    {
        if (!string.Equals(command, "SH", StringComparison.Ordinal)) return;
        // The counter bump and the clear are ONE critical section against the
        // arm's commit (see ArmRePoll): a satisfaction that lands between an
        // arm's check and its write must not leave a resurrected flag behind.
        lock (_repollLock)
        {
            Interlocked.Increment(ref _shSent);
            _repollKeyline = false;
            if (InSsb)
            {
                _repollChannelDomain = false;
                // …and an SSB-context SH block is now owed: open a window,
                // correlated to THIS read, snapshotting what the read already
                // expects to have moved and where the memories stand.
                _syncWindows.Enqueue(new SyncWindow(
                    !State.ModulationMode.IsConfirmed,
                    !State.DigitalVoice.IsConfirmed,
                    _lastReportedModulation,
                    _lastReportedDv));
            }
            if (!_repollKeyline && !_repollChannelDomain) _repollReason = null;
        }
    }

    /// <summary>Every <c>SH</c> this session has put on the wire, and the count
    /// as it stood when the line now being applied arrived. The pair closes the
    /// ORDERING half of the rule: a consumer's <c>SH</c> may be sent from a
    /// state-changed handler DURING the parse of the very line that is about to
    /// arm the flag, so clearing on issuance alone would clear something not
    /// yet set and the arm would then duplicate the read.</summary>
    private long _shSent;
    private long _shSentAtLineStart;

    /// <summary>Rows (b)/(c) and hopset generation: the SSB channel values are
    /// stale, and only an SSB-context <c>SH</c> re-reads them.</summary>
    private void ArmChannelDomainRePoll(string reason)
        => ArmRePoll(reason, keyline: false, channelDomain: true);

    /// <summary>§9 B1's tune terminals: the KEYLINE is unreported and any
    /// <c>SH</c> block re-reports it, whichever prompt it is asked at.</summary>
    private void ArmKeylineRePoll(string reason)
        => ArmRePoll(reason, keyline: true, channelDomain: false);

    /// <summary>Move the modulation memory and say whether the radio reported
    /// something DIFFERENT from what it last reported. Under the leaf lock: the
    /// memories are written on the parse path and READ on a caller thread when
    /// a window opens (<see cref="SatisfyPendingRePoll"/>).</summary>
    private bool RememberModulation(ModulationMode value)
    {
        lock (_repollLock)
        {
            bool moved = _lastReportedModulation != value;
            _lastReportedModulation = value;
            // The HEAD window only: command responses return in COMMAND ORDER
            // (protocol.md, behaviour 0), so the block arriving now belongs to
            // the OLDEST outstanding read. Crediting every open window would
            // make a second read inherit the first block's lines and evaluate
            // on evidence that was never its own.
            if (_syncWindows.Count > 0) _syncWindows.Peek().ModeReports++;
            return moved;
        }
    }

    private bool RememberDv(OnOff value)
    {
        lock (_repollLock)
        {
            bool moved = _lastReportedDv != value;
            _lastReportedDv = value;
            if (_syncWindows.Count > 0) _syncWindows.Peek().DvReports++;   // the head only — see above
            return moved;
        }
    }

    /// <summary>
    /// THE CLOSE, run on every <c>SSB&gt;</c> prompt and UNCONDITIONAL (audit
    /// round 1, finding 2: a close that waited for evidence of the block
    /// LATCHED OPEN whenever the radio truncated or swallowed one — R6 says it
    /// does — and the next genuine change was then consumed with nothing
    /// armed, the exact mirror image of the loop the window exists to prevent).
    ///
    /// <para><b>What it evaluates.</b> A window collects; the decision is taken
    /// here, once, per value:</para>
    /// <list type="bullet">
    /// <item>The value was already UNCONFIRMED when the read went out — the
    /// block re-confirming it IS the sync landing, which is what the read was
    /// for. Never arms.</item>
    /// <item>The token was reported ONCE — that one report is the block's own
    /// line, and an <c>SH</c> is itself a full sync of the domain a DV/MODE
    /// mutation touches. Never arms.</item>
    /// <item>The token was reported MORE THAN ONCE and the memory ends
    /// somewhere other than where the window opened — the block carries exactly
    /// one line per token, so a second one is an ASYNC report that landed
    /// mid-read and the block cannot have accounted for it. ARMS, once, and
    /// unconfirms what that value silently moves (audit round 1, finding 1: a
    /// real <c>DV ON</c> landing inside an ordinary read used to be swallowed,
    /// leaving a stale modulation confirmed and nothing owed).</item>
    /// </list>
    ///
    /// <para><b>The residual, recorded — and its repair bound, stated exactly.</b>
    /// A block truncated BEFORE its own line for the moved token, while an
    /// async report of that token lands, is indistinguishable at the line level
    /// from a block reporting its own moved value: one report either way, so it
    /// does not arm. The WINDOW side of that is bounded — it closes on the very
    /// next <c>SSB&gt;</c> whatever arrives, and a non-SSB prompt retires it
    /// outright (see <see cref="RetireSyncWindows"/>; audit round 2 closed the
    /// unbounded case, where a truncated block used to leave a window standing
    /// across an entire mode excursion). The DISPLAY side is NOT repaired by
    /// just any later traffic: a later STANDALONE report of the same value does
    /// not move the memory and therefore arms nothing. Repair comes from a
    /// later full SSB <c>SH</c> block (which re-reports the whole domain
    /// directly) or from a genuine SUBSEQUENT change of the value. Until one of
    /// those arrives the display can hold one stale reading — which is the
    /// honest cost of not being able to tell the two one-report cases
    /// apart.</para>
    /// </summary>
    private void CloseSyncWindow()
    {
        bool modeMoved, dvMoved;
        lock (_repollLock)
        {
            if (_syncWindows.Count == 0) return;
            var window = _syncWindows.Dequeue();
            modeMoved = !window.ModeUnconfirmed
                && window.ModeReports > 1
                && _lastReportedModulation != window.Mode;
            dvMoved = !window.DvUnconfirmed
                && window.DvReports > 1
                && _lastReportedDv != window.Dv;
        }

        if (!IsInitialized || (!modeMoved && !dvMoved)) return;

        if (dvMoved) State.UnconfirmDvForcedValues();
        if (modeMoved) State.UnconfirmDigitalVoice();
        ArmRePoll(
            "a digital-voice or modulation report landed DURING a re-read, so that read cannot have carried it",
            keyline: true, channelDomain: true);
    }

    /// <summary>
    /// PROMPT-FAMILY HYGIENE (audit round 2). An <c>ALE&gt;</c> or <c>HOP&gt;</c>
    /// prompt says the radio is answering out of another mode's family, so no
    /// outstanding SSB <c>SH</c> block can still complete — its window would
    /// otherwise stand for the WHOLE excursion, suppressing every genuine DV or
    /// MODE report that lands in it and then closing, on the eventual
    /// <c>SSB&gt;</c>, against evidence that was never its own. Reproduced: a
    /// truncated read, a mode switch, a real <c>DV ON</c> at the <c>ALE&gt;</c>
    /// prompt, and the return to SSB arming NOTHING while the radio had silently
    /// forced USB.
    ///
    /// <para><b>Retirement is CONSERVATIVE, deliberately.</b> The
    /// one-report-vs-two discrimination <see cref="CloseSyncWindow"/> relies on
    /// needs the block to be accounted for, and here it never will be — so the
    /// report counts are ignored and ENDPOINT movement decides: a memory that
    /// ENDS somewhere other than the window's opening snapshot ARMS. A spurious
    /// extra <c>SH</c> on this rare path costs one read; a lost change costs a
    /// display that is silently wrong until something unrelated re-reads. The
    /// cheap side is the right side.</para>
    ///
    /// <para><b>Endpoint, not trajectory — the limit named.</b> A value that
    /// moved AWAY and back to its opening value inside one abandoned window
    /// ends where it started and therefore does not arm. No capture records
    /// such an away-and-back double transition inside a single read, and this
    /// phase implements only captured sequences (the D1 doctrine), so the
    /// endpoint test is what ships; a capture that produces one is a content
    /// fix here, not a redesign.</para>
    ///
    /// <para>Afterwards the queue is EMPTY, so every later line meets the
    /// ordinary memory-keyed producers with nothing deferring them.</para>
    /// </summary>
    private void RetireSyncWindows()
    {
        bool modeMoved = false, dvMoved = false;
        lock (_repollLock)
        {
            if (_syncWindows.Count == 0) return;
            while (_syncWindows.Count > 0)
            {
                // ENDPOINT against the window's own opening snapshot — see the
                // away-and-back note above for what that deliberately misses.
                var window = _syncWindows.Dequeue();
                modeMoved |= _lastReportedModulation != window.Mode;
                dvMoved |= _lastReportedDv != window.Dv;
            }
        }

        if (!IsInitialized || (!modeMoved && !dvMoved)) return;

        if (dvMoved) State.UnconfirmDvForcedValues();
        if (modeMoved) State.UnconfirmDigitalVoice();
        // Fires at the next SSB prompt: SSB-domain commands are rejected here.
        ArmRePoll(
            "a digital-voice or modulation report landed during a re-read the mode change abandoned",
            keyline: true, channelDomain: true);
    }

    private bool SyncWindowOpen { get { lock (_repollLock) return _syncWindows.Count > 0; } }

    /// <summary>
    /// Trigger row (f) — the P4 DV/MODE state sync. Arms BOTH halves: the
    /// silent mutations span the keyline-free channel domain and ride in the
    /// same block.
    ///
    /// <para><b>DEFERRED, NOT DISCARDED, INSIDE A WINDOW.</b> Returns FALSE —
    /// arming nothing, and telling the caller not to unconfirm either — while a
    /// sync window stands. The changed-line guard alone provably cannot deliver
    /// one SH: <see cref="RadioState"/>'s setter counts a post-unconfirm
    /// RE-confirm as Changed, and a compensating block legitimately reports the
    /// genuinely-changed MODE the DV toggle caused. So the block's lines still
    /// parse, still re-confirm every mirror and still move the memories — they
    /// simply do not decide anything. <see cref="CloseSyncWindow"/> decides, on
    /// the whole window's evidence, exactly once.</para>
    ///
    /// <para><b>WHY THE WINDOW IS EVERY SSB <c>SH</c>, NOT ONLY THE ONE THIS
    /// ROW ARMED</b> (widened during P4 execution; the plan's clause is the
    /// subset). The argument is a property of the BLOCK, not of who asked for
    /// it: the SSB <c>SH</c> block reports MODE, BAND, SQUELCH and DV together,
    /// so it is a complete snapshot of exactly the values a DV mutation moves.
    /// Restricting the window to Core's own compensation left every OTHER
    /// <c>SH</c> (a settings read, an operator refresh, the clone campaign's
    /// read legs) arming a redundant re-poll off its own DV line AND leaving
    /// MODE/BAND unconfirmed for the rest of the block — the block reports MODE
    /// before DV, so nothing re-confirms them. The ALE/HOP blocks are
    /// deliberately excluded: they carry no BAND or SQUELCH, so they are not
    /// the snapshot this argument needs.</para>
    /// </summary>
    /// <returns>True when the producer FIRED (i.e. was not deferred), which is
    /// also when its display unconfirm must run.</returns>
    private bool ArmDvSync(string reason)
    {
        if (SyncWindowOpen) return false;
        ArmRePoll(reason, keyline: true, channelDomain: true);
        return true;
    }

    /// <summary>
    /// Arm the pending re-poll — the ONE entry point for every producer (rows
    /// (b)/(c)/(f), hopset generation, and the §9 B1 tune terminals), so the
    /// coalescing rule cannot be applied to one and forgotten for another.
    ///
    /// <para>Skips when an <c>SH</c> has already gone out while this line was
    /// being applied: the read is on the wire and its answer re-confirms the
    /// block, so a second one would ask the same question twice.</para>
    ///
    /// <para><b>THE VERSIONED COMMIT</b> (P1 round-3 audit MINOR, deferred to
    /// P4). The check above and the write below used to be non-atomic against
    /// a concurrent <see cref="SatisfyPendingRePoll"/>: a satisfaction landing
    /// between them cleared a flag this method then RESURRECTED, costing one
    /// redundant <c>SH</c>. The counter is now re-read INSIDE the same critical
    /// section the satisfaction uses, so an <c>SH</c> that raced is seen and
    /// the arm stands down. <see cref="ArmRaceHook"/> is the scheduling probe
    /// the audit asked for — it fires exactly in the window that used to be
    /// open.</para>
    /// </summary>
    private void ArmRePoll(string reason, bool keyline, bool channelDomain)
    {
        long seen = Interlocked.Read(ref _shSent);
        if (seen != _shSentAtLineStart) return;

        ArmRaceHook?.Invoke();

        lock (_repollLock)
        {
            if (Interlocked.Read(ref _shSent) != seen) return;
            if (keyline) _repollKeyline = true;
            if (channelDomain) _repollChannelDomain = true;
            _repollReason = reason;
        }
    }

    /// <summary>TEST SEAM (Core tests only): invoked inside the arm's
    /// check-then-write window so a satisfaction can be raced into exactly the
    /// gap the versioned commit closes. Null in every real session.</summary>
    internal Action? ArmRaceHook { get; set; }

    /// <summary>Raw command passthrough for the Console page.</summary>
    public void RawCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        SendLine(command);
    }

    // ---- v1 general command surface ----------------------------------------

    public void Show() => Send("SH");
    public void QueryBatteryState() => Send("BAT", "ST");
    public void SetPowerLevel(PowerLevel level) => Send("POW", level.ToWire());
    public void QueryPowerLevel() => Send("POW");

    /// <summary>Remote-port configuration dump (read-only Settings display).</summary>
    public void QueryPortConfig() => Send("PORT_R");

    /// <summary>Radio clock query (TI — answered in every mode, side-effect
    /// free; bench-pinned in the sentinel table). The DAY/DATE/TIME triplet
    /// answer carries the TOD the HOP pane's Time section displays.</summary>
    public void QueryTime() => Send("TI");

    public void SetRemoteEcho(OnOff state) => Send("PORT_R", "ECHO", state.ToWire());

    // ---- Phase R device settings (answered in every mode — sentinel table) --

    /// <summary>Backlight function (LIG OFF|MOMENTARY). Values are old-app-
    /// derived — LIG is absent from the captured HELP menus; never sent to
    /// this radio, and the LIGHT answer payload is uncaptured (bench item).</summary>
    public void SetBacklightFunction(BacklightFunction function) => Send("LIG", function.ToWire());

    /// <summary>Backlight intensity (INT 0-8 per HELP MORE). Mode-free
    /// ASSUMED with LIG (plan round 8, unprobed); never sent to this radio;
    /// the INTENSITY answer payload is uncaptured (bench item).</summary>
    public void SetBacklightIntensity(int intensity) => SendZeroToEight("INT", intensity, nameof(intensity));

    /// <summary>Display contrast (CONT 0-8; answer "CONTRAST nn" — sentinel
    /// table). The set form has never been sent to this radio.</summary>
    public void SetContrast(int contrast) => SendZeroToEight("CONT", contrast, nameof(contrast));

    // ---- UI-tweaks round-4 AC: the device READ set (R4-Q1 mining) ----------
    // The bare-query forms come from the WinForms Falcon-Radio-Remote-Control,
    // whose Configuration window queries all three when it opens
    // (src/Falcon.Gui/Configuration.cs:41-43 -> Prc138Radio.cs:997-999).
    // `LIG` and `CONT` are additionally THIS project's own bench facts — the
    // 2026-08-01 sentinel probe answered both in SSB, ALE and HOP
    // (docs/protocol.md "Commands answered in every mode"). `INT` is
    // old-app-derived only; docs/protocol.md's round-4 provisional subsection
    // and its bench-checklist item carry that distinction.

    /// <summary>LIG (bare) — reads the backlight function; answers
    /// "LIGHT &lt;fn&gt;" (probed answered in every mode; the PAYLOAD spelling
    /// is old-app-derived and PROVISIONAL).</summary>
    public void QueryBacklightFunction() => Send("LIG");

    /// <summary>INT (bare) — reads the backlight intensity; answers
    /// "INTENSITY nn" per the old app. PROVISIONAL: never sent by this
    /// project, and INT was not in the sentinel probe, so its mode-freeness is
    /// assumed with LIG (bench item).</summary>
    public void QueryBacklightIntensity() => Send("INT");

    /// <summary>CONT (bare) — reads the display contrast; answers
    /// "CONTRAST nn" (sentinel table: answered in every mode). The answer
    /// shape is bench-confirmed here; only this app had no read for it.</summary>
    public void QueryContrast() => Send("CONT");

    /// <summary>
    /// The shared 0–8 device setter. <b>ZERO-PADDED to two digits (§9 C2).</b>
    ///
    /// <para>The unpadded form was silently ineffective for <c>INT</c> —
    /// OWNER-VERIFIED at the bench: the backlight only moves when the value is
    /// two digits. That made the padding UNCONDITIONAL for <c>INT</c>, and put
    /// <c>CONT</c> (which shares this helper) under suspicion of failing the
    /// same way. P-2 step b settled it on the real radio
    /// (bench/transcripts/r12-p2-*, 2026-08-18): <c>CONT 05</c> echoed
    /// <c>CONTRAST 05</c> and read back <c>05</c> — the GREEN branch — so the
    /// helper stays SHARED and both commands pad. Had CONT refused, the helper
    /// would have split per command; it did not, and this note records which
    /// branch the capture chose so the choice is not mistaken for an
    /// oversight.</para>
    /// </summary>
    private void SendZeroToEight(string command, int value, string name)
    {
        if (value < Wire.ZeroToEightMin || value > Wire.ZeroToEightMax)
            throw new ArgumentOutOfRangeException(name, command + " is 0-8.");
        Send(command, value.ToString("D2", CultureInfo.InvariantCulture));
    }

    // ---- Phase R crypto (valid in all modes — protocol.md COMSEC; backend
    // in, GUI OUT per plan round 4 E1: the app layer must never call these,
    // enforced by the GuiOutScopeGuardTests source scan) --------------------

    /// <summary>Encryption on/off (ENCR ON|OFF — bench session-14; with no
    /// stored key the radio answers "NO VALID KEY").</summary>
    public void SetEncryption(OnOff state) => Send("ENCR", state.ToWire());

    /// <summary>Program a key slot (ENC_KEY &lt;1-6&gt; &lt;12 digits&gt;).
    /// WRITE-ONLY — keys can never be read back.</summary>
    public void SetEncryptionKey(int slot, string key12Digits)
    {
        ValidateKeySlot(slot);
        if (key12Digits is null || key12Digits.Length != 12 || !key12Digits.All(char.IsAsciiDigit))
            throw new ArgumentException("Encryption key is exactly 12 digits.", nameof(key12Digits));
        Send("ENC_KEY", slot.ToString(CultureInfo.InvariantCulture), key12Digits);
    }

    /// <summary>Delete a key slot (ENC_KEY &lt;slot&gt; CLEAR — clearing the
    /// active key answers "NO KEY, ENCR OFF").</summary>
    public void ClearEncryptionKey(int slot)
    {
        ValidateKeySlot(slot);
        Send("ENC_KEY", slot.ToString(CultureInfo.InvariantCulture), "CLEAR");
    }

    /// <summary>Select the active key slot (USE_KEY &lt;1-6&gt; — does NOT
    /// enable encryption; a bad slot answers "INVALID ENCR KEY" +
    /// "CUR_KEY XX").</summary>
    public void SelectEncryptionKey(int slot)
    {
        ValidateKeySlot(slot);
        Send("USE_KEY", slot.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateKeySlot(int slot)
    {
        if (slot is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(slot), "Encryption key slot is 1-6.");
    }

    /// <summary>Remote-port baud rates the app supports (plan §1: 2400/4800/9600).</summary>
    public static readonly IReadOnlyList<int> SupportedRemoteBaudRates = [2400, 4800, 9600];

    /// <summary>
    /// Reconfigure the RADIO's remote-port baud: sends <c>PORT_R BAUD n</c>,
    /// which <b>ENDS THE SESSION IMMEDIATELY</b> (protocol.md hazard table —
    /// the radio answers nothing intelligible at the old rate afterwards;
    /// recovery is reconnecting at the new rate, or the front panel).
    ///
    /// This is the ONE deliberate, whitelisted exception to the
    /// session-ending-commands exclusion (plan §7 decision 3: the guarded
    /// baud wizard is IN scope; `LEVEL` and the other PORT_R setters stay
    /// out).
    ///
    /// <para><b>UI tweaks round 10 (§5, owner ruling 9):</b> the typed
    /// CONFIRMATION TOKEN parameter is REMOVED — "the back end does what the
    /// GUI tells it". Confirmation for this destructive-DATA sender is a GUI
    /// concern now (the wizard's own typed-match guard, and the app-wide
    /// <c>IConfirmationPrompt</c> seam); Core executes. The command is still
    /// whitelisted-and-swept, still the only PORT_R setter that exists, and
    /// the wire prefix "PORT_R BAUD" stays FORBIDDEN for every other sender.
    /// This scoping does NOT touch the three TRANSMIT-hazard token gates
    /// (SetKeyline TRANSMIT / SelfTest / VswrTest), which are unchanged.</para>
    /// </summary>
    /// <param name="baud">Target rate — must be one of
    /// <see cref="SupportedRemoteBaudRates"/>.</param>
    public void SetRemoteBaud(int baud)
    {
        if (!SupportedRemoteBaudRates.Contains(baud))
            throw new ArgumentOutOfRangeException(nameof(baud),
                $"Unsupported remote baud {baud} — supported: {string.Join("/", SupportedRemoteBaudRates)}.");

        Send("PORT_R", "BAUD", baud.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _initWatchdog?.Dispose();
        _modeDeadline?.Dispose();
        lock (_zeroizeLock) { _zeroizeSettling = false; StopZeroizeTimersLocked(); }
        ClearPendingPings();
        (_transport as IDisposable)?.Dispose();
    }
}
