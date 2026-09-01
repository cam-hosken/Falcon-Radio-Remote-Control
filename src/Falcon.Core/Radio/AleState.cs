using Falcon.Core.Protocol;

namespace Falcon.Core.Radio;

/// <summary>
/// ALE-domain reported state. Scalars are <see cref="Confirmed{T}"/>
/// (unconfirmed until the radio reports them); collections are COPY-ON-WRITE —
/// replaced whole under a lock, never mutated in place, so UI threads can
/// enumerate whichever snapshot they captured while the parse thread updates.
/// </summary>
public sealed class AleState
{
    private readonly Action<RadioProperty> _raise;
    internal AleState(Action<RadioProperty> raise) => _raise = raise;

    private Confirmed<AleLinkState> _linkState;
    public Confirmed<AleLinkState> LinkState => _linkState;
    internal void SetLinkState(AleLinkState v)
    {
        // ROUND 15 item I: Scanning and Stopped are the LQA's two terminators
        // (the bare SCANNING that ends a bare-STA run, P14c; the SCAN STOPPED
        // an ST abort draws, P14b). The run's station/channel belong to the run
        // that just ended, so they clear WITH the transition — mutate all, then
        // raise (the file's standing ordering rule), and clear even when the
        // state itself does not move, so a repeat terminator cannot leave a
        // stale channel behind.
        bool clearedLqa = (v is AleLinkState.Scanning or AleLinkState.Stopped)
            && ClearLqaProgressFields();

        bool stateMoved = !(_linkState.IsConfirmed && _linkState.Value == v);
        if (stateMoved) _linkState = Confirmed<AleLinkState>.Of(v);

        if (clearedLqa) _raise(RadioProperty.AleLqaProgress);
        if (stateMoved) _raise(RadioProperty.AleLinkState);
    }

    private string? _lqaStation;
    /// <summary>Station named by the last LQA PROGRESS line (round 15 item I,
    /// P14b/P14c: <c>SOUNDING &lt;self&gt; CHANNEL: nn</c> /
    /// <c>EXCHANGE &lt;ind&gt; CHANNEL: nn</c>) — for a sounding it is the
    /// radio's own self. Its OWN slot, never <see cref="LinkedStation"/>: a
    /// bare <c>LINKED</c> later keeps whatever station the call slot holds, and
    /// a sounding's self written there would render "LINKED &lt;self&gt;"
    /// (critic F73). Null until an LQA run reports one; cleared when the run
    /// ends.</summary>
    public string? LqaStation => _lqaStation;

    private string? _lqaChannel;
    /// <summary>Channel from the last LQA progress line ("CHANNEL: nn"). A run
    /// walks every channel of the target's group, so this moves every ~18 s
    /// (sounding) or ~30 s (exchange) — P14c.</summary>
    public string? LqaChannel => _lqaChannel;

    internal void SetLqaProgress(string? station, string? channel)
    {
        if (_lqaStation == station && _lqaChannel == channel) return;
        _lqaStation = station;
        _lqaChannel = channel;
        _raise(RadioProperty.AleLqaProgress);
    }

    /// <summary>Clears the LQA progress slot WITHOUT raising — the caller
    /// raises, so the terminator's two facts (state and slot) are mutated
    /// before either notification goes out.</summary>
    private bool ClearLqaProgressFields()
    {
        if (_lqaStation is null && _lqaChannel is null) return false;
        _lqaStation = null;
        _lqaChannel = null;
        return true;
    }

    private Confirmed<AleFillState> _fillState;
    public Confirmed<AleFillState> FillState => _fillState;
    internal void SetFillState(AleFillState v)
    {
        if (_fillState.IsConfirmed && _fillState.Value == v) return;
        _fillState = Confirmed<AleFillState>.Of(v);
        _raise(RadioProperty.AleFillState);
    }

    private string? _linkedStation;
    /// <summary>Station named by the last CALLING/SENDING/LINKED line.</summary>
    public string? LinkedStation => _linkedStation;

    private string? _linkedChannel;
    /// <summary>Channel reported by the last CALLING/SENDING line ("CHANNEL: nn").</summary>
    public string? LinkedChannel => _linkedChannel;

    internal void SetLinkedStation(string? station, string? channel)
    {
        if (_linkedStation == station && _linkedChannel == channel) return;
        _linkedStation = station;
        _linkedChannel = channel;
        _raise(RadioProperty.AleLinkedStation);
    }

    // ---- ALE settings (Phase R, plan-gui-rejigger.md round 4) -----------
    // All nine are reported in the ALE SH block (protocol.md "ALE SH block")
    // and confirmed query+set on the bench.

    private Confirmed<OnOff> _allCall, _anyCall, _amdDisplay, _keyToCall, _listenBeforeTx, _radioSilence;
    public Confirmed<OnOff> AllCall => _allCall;
    public Confirmed<OnOff> AnyCall => _anyCall;
    public Confirmed<OnOff> AmdDisplay => _amdDisplay;
    public Confirmed<OnOff> KeyToCall => _keyToCall;
    public Confirmed<OnOff> ListenBeforeTx => _listenBeforeTx;
    public Confirmed<OnOff> RadioSilence => _radioSilence;
    internal void SetAllCall(OnOff v) => Set(ref _allCall, v, RadioProperty.AleAllCall);
    internal void SetAnyCall(OnOff v) => Set(ref _anyCall, v, RadioProperty.AleAnyCall);
    internal void SetAmdDisplay(OnOff v) => Set(ref _amdDisplay, v, RadioProperty.AleAmdDisplay);
    internal void SetKeyToCall(OnOff v) => Set(ref _keyToCall, v, RadioProperty.AleKeyToCall);
    internal void SetListenBeforeTx(OnOff v) => Set(ref _listenBeforeTx, v, RadioProperty.AleListenBeforeTx);
    internal void SetRadioSilence(OnOff v) => Set(ref _radioSilence, v, RadioProperty.AleRadioSilence);

    private Confirmed<int> _maxScanChannels, _linkTimeoutMinutes, _tuneTimeSeconds;
    public Confirmed<int> MaxScanChannels => _maxScanChannels;
    /// <summary>TIME_OUT minutes; 0 is valid despite HELP's 1-60 (measured,
    /// session-18 — "TIME_OU 0" echoes "TIME_OUT 000").</summary>
    public Confirmed<int> LinkTimeoutMinutes => _linkTimeoutMinutes;
    public Confirmed<int> TuneTimeSeconds => _tuneTimeSeconds;
    internal void SetMaxScanChannels(int v) => Set(ref _maxScanChannels, v, RadioProperty.AleMaxScanChannels);
    internal void SetLinkTimeoutMinutes(int v) => Set(ref _linkTimeoutMinutes, v, RadioProperty.AleLinkTimeout);
    internal void SetTuneTimeSeconds(int v) => Set(ref _tuneTimeSeconds, v, RadioProperty.AleTuneTime);

    private void Set<T>(ref Confirmed<T> field, T value, RadioProperty prop)
    {
        if (field.IsConfirmed && EqualityComparer<T>.Default.Equals(field.Value, value)) return;
        field = Confirmed<T>.Of(value);
        _raise(prop);
    }

    // ---- Station list (flat: selfs + individuals + nets) ----------------

    public IReadOnlyList<AleAddress> SelfAddresses { get; private set; } = [];
    public IReadOnlyList<AleAddress> IndividualAddresses { get; private set; } = [];
    public IReadOnlyList<AleAddress> NetAddresses { get; private set; } = [];
    public IReadOnlyList<AmdMessage> TxMessages { get; private set; } = [];
    public IReadOnlyList<LqaScore> LqaReport { get; private set; } = [];

    // The book is written from the parse thread and cleared from user
    // threads (refresh/reconnect), so every read-modify-write is locked.
    // Reads stay lock-free via the copy-on-write snapshots. The SAME lock
    // guards the channel-group table and the read queue below: they are read
    // and written by the same two thread classes, and one mutex for the whole
    // ALE mirror makes a lock-ordering bug impossible.
    private readonly object _bookLock = new();

    // A station-list refresh ACCUMULATES here instead of clearing published
    // state up front: if the radio swallows a listing query (the documented
    // quirk), the last radio-confirmed book stays on display instead of an
    // empty one that matches nothing (old repo audit-6 D4 — the mechanism
    // survived triage because the failure it guards is bench-documented).
    private List<AleAddress>? _refreshSelf, _refreshIndividual, _refreshNet;

    // ====================================================================
    // Scan channel groups + the ONE-AT-A-TIME read queue
    // (plan-ale-programming.md §4.1 — the concurrency contract).
    //
    // Two STORES (book, groups). At most ONE read operation per store is on
    // the wire at a time; an operation covers a SLOT SET (the book's set is
    // always {book}; a group operation's is {g} or {0..9}). A request that
    // arrives while an operation is active becomes — or UNION-COALESCES
    // into — that store's single PENDING operation, and every coalesced
    // requester is handed the pending operation's id. The pending operation
    // begins only after the active one's sentinel completes.
    //
    // Two consequences the pins depend on:
    //   * every response line on the wire belongs to the single ACTIVE
    //     operation, so a stale line can never reach a LATER operation's
    //     accumulator — structurally, not by id filtering;
    //   * each operation runs to exactly ONE commit publishing exactly its
    //     slot set, so completion matching is id EQUALITY.
    // ====================================================================

    /// <summary>Ten entries, index == group number; see
    /// <see cref="AleChannelGroup"/> for the three-state contract.</summary>
    public IReadOnlyList<AleChannelGroup> ChannelGroups { get; private set; } = BlankChannelGroups();

    private static AleChannelGroup[] BlankChannelGroups()
    {
        var groups = new AleChannelGroup[10];
        for (int g = 0; g < groups.Length; g++) groups[g] = new AleChannelGroup(g, null);
        return groups;
    }

    /// <summary>One store's active/pending operations. Id 0 = none.</summary>
    private sealed class ReadQueue
    {
        public long ActiveId;
        public HashSet<int> ActiveSlots = [];
        public long PendingId;
        public HashSet<int> PendingSlots = [];
    }

    private readonly ReadQueue _groupQueue = new();
    private long _nextReadId;

    // ====================================================================
    // Round 11 §8 — TWO MORE stores on exactly this pattern.
    //
    //   * MEMBERS, keyed by NET NAME: one net's targeted `NETAD <name>` read
    //     is in flight at a time; requests arriving meanwhile UNION into the
    //     pending NAME SET (the slot-set coalescing precedent above, with
    //     names instead of group numbers) and dispatch one at a time after
    //     each commit.
    //   * SCHEDULES, single-slot and book-style: bare `EXCH` + one sentinel.
    //
    // Both commit ATOMICALLY on the sentinel and preserve prior state when it
    // is not answered — the swallowed-listing quirk again, and for the same
    // reason: a half-read membership matches nothing.
    //
    // THE BOOK AND MEMBER READS SHARE ONE QUEUE (audit round 1, MAJOR 5).
    // Both produce `NETAD` RECORD lines, and a record line carries nothing
    // that says which operation asked for it. With two independent queues a
    // targeted read's record could land in a concurrent book refresh's
    // accumulator and change the BOOK'S ORDER — which is load-bearing, because
    // the address book's PRIMARY self is derived from listing index 0. The fix
    // is by construction, not by convention: the two operations can never be
    // in flight together, so `_refreshNet` is non-null only while the BOOK
    // operation owns the wire and a targeted record has nowhere to leak. The
    // modem queue solves the identical problem the identical way.
    // ====================================================================

    private enum NetadOp { None, Book, Members }

    /// <summary>The single queue over both `NETAD`-producing operations.
    /// Active is one operation of either kind; pending holds at most one of
    /// EACH (a coalesced book refresh, and a name SET for membership), so no
    /// request is ever lost behind the other kind.</summary>
    private sealed class NetadQueue
    {
        public long ActiveId;
        public NetadOp ActiveKind;
        public string? ActiveName;                  // Members only
        public long PendingBookId;
        public long PendingMemberId;
        public HashSet<string> PendingNames = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly NetadQueue _netadQueue = new();
    private readonly ReadQueue _scheduleQueue = new();

    /// <summary>The active MEMBER operation's accumulator (null when no member
    /// read is in flight). An operation that commits an EMPTY accumulator
    /// publishes the READ-EMPTY state for that net — the radio's own
    /// <c>NO MEMBERS PRGMD</c> marker lands here as "no rows".</summary>
    private List<AleNetMember>? _memberAnswers;

    /// <summary>The active SCHEDULE operation's accumulator (null when no
    /// schedule read is in flight); empty at commit = READ-EMPTY, which is
    /// what <c>NO LQA SCHEDULED</c> means.</summary>
    private List<LqaSchedule>? _scheduleAnswers;

    private AleReadCompletion _lastMemberRead, _lastScheduleRead;

    /// <summary>Completion record of the last committed MEMBER read.</summary>
    public AleReadCompletion LastMemberRead => _lastMemberRead;
    /// <summary>Completion record of the last committed SCHEDULE read.</summary>
    public AleReadCompletion LastScheduleRead => _lastScheduleRead;

    /// <summary>
    /// Per-net membership, keyed by net name (ordinal-ignore-case, the radio's
    /// own lookup rule). THREE states, never conflated:
    /// <list type="bullet">
    /// <item>key ABSENT — never read this session, or invalidated by a write
    /// that could have changed it ("—").</item>
    /// <item>value <c>[]</c> — read and CONFIRMED empty (the targeted read
    /// carried <c>NO MEMBERS PRGMD</c>, or no member line at all).</item>
    /// <item>value <c>[..]</c> — the members in the radio's INSERTION order.</item>
    /// </list>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AleNetMember>> NetMembers { get; private set; } =
        new Dictionary<string, IReadOnlyList<AleNetMember>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The queued LQA schedules from the last committed bare-<c>EXCH</c> read,
    /// in the RADIO's order (chronological by next start time). Three states:
    /// <c>null</c> = never read/invalidated, <c>[]</c> = read and confirmed
    /// empty (<c>NO LQA SCHEDULED</c>), rows otherwise.
    /// </summary>
    public IReadOnlyList<LqaSchedule>? LqaSchedules { get; private set; }

    /// <summary>Request a MEMBER read for <paramref name="netName"/>.
    /// <paramref name="dispatchName"/> non-null means this call BEGAN the
    /// operation and the caller must send <c>NETAD &lt;name&gt;</c> + the
    /// sentinel; null means it coalesced into the pending name set, whose
    /// operation id is returned.</summary>
    internal long RequestNetMembersRead(string netName, out string? dispatchName)
    {
        lock (_bookLock)
        {
            if (_netadQueue.ActiveKind != NetadOp.None)
            {
                dispatchName = null;
                if (_netadQueue.PendingMemberId == 0) _netadQueue.PendingMemberId = NextReadIdLocked();
                _netadQueue.PendingNames.Add(netName);
                return _netadQueue.PendingMemberId;
            }

            _netadQueue.ActiveId = NextReadIdLocked();
            _netadQueue.ActiveKind = NetadOp.Members;
            _netadQueue.ActiveName = netName;
            _memberAnswers = [];
            dispatchName = netName;
            return _netadQueue.ActiveId;
        }
    }

    /// <summary>Commit the MEMBER operation <paramref name="readId"/>: an
    /// ANSWERED sentinel replaces that net's rows WHOLE (or publishes
    /// read-empty); an unanswered one publishes nothing and the prior rows
    /// stand. Then the shared NETAD queue promotes whatever is pending — which
    /// may be the BOOK refresh, so the caller is told which to dispatch.</summary>
    internal void CompleteNetMembersRead(long readId, bool answered,
        out long nextReadId, out bool dispatchBook, out string? nextName)
    {
        nextReadId = 0;
        dispatchBook = false;
        nextName = null;

        bool published = false;
        lock (_bookLock)
        {
            if (_netadQueue.ActiveId != readId || _netadQueue.ActiveKind != NetadOp.Members) return;

            if (answered && _netadQueue.ActiveName is { } net && _memberAnswers is { } rows)
            {
                var table = new Dictionary<string, IReadOnlyList<AleNetMember>>(
                    (IDictionary<string, IReadOnlyList<AleNetMember>>)NetMembers,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [net] = rows,
                };
                NetMembers = table;
                published = true;
            }
            _memberAnswers = null;
        }

        if (published) _raise(RadioProperty.AleNetMembers);

        // RELEASE BEFORE RAISE (round 16 fixes S5, decision F-10) — the MEMBER
        // path only; the book path above keeps today's order.
        //
        // Every Core event is delivered INLINE when no SynchronizationContext
        // was captured, so a `RequestNetMembersRead` issued from INSIDE the
        // AleMemberRead handler used to run while THIS operation was still
        // active: it coalesced into the pending slot, and PromoteNetadLocked —
        // the very next statement — abandoned it. A consumer that re-reads on
        // an unanswered completion could therefore only reach the wire under a
        // QUEUED context, i.e. behaviour that differs by context and cannot be
        // pinned. With the queue released first, such a request finds it IDLE
        // after a silence (it dispatches) or owned by the promoted operation
        // after an answer (it coalesces behind it, BOOK-before-MEMBERS kept).
        //
        // The completion is RECORDED here and RAISED after the promotion, so
        // the handler reads the same record either way; the VISIBLE raise
        // sequence is unchanged and pinned
        // (Round11ReadStoreTests.ANetadSilence_RaisesTheActiveCompletion…).
        _lastMemberRead = new AleReadCompletion(readId, answered);

        List<(long Id, RadioProperty Property)> abandoned = [];
        lock (_bookLock)
        {
            // Ownership re-check, as before — but a lost one must not skip the
            // raise the requester is waiting on.
            if (_netadQueue.ActiveId == readId)
                abandoned = PromoteNetadLocked(answered, out nextReadId, out dispatchBook, out nextName);
        }

        _raise(RadioProperty.AleMemberRead);
        RaiseAbandoned(abandoned);
    }

    /// <summary>
    /// Release the active NETAD operation and start whatever is pending.
    /// Caller holds <see cref="_bookLock"/> and has verified ownership.
    ///
    /// <para>BOOK BEFORE MEMBERS, deliberately: membership is read PER NET, so
    /// the book — which says what the nets are — is the coarser answer and the
    /// one a caller is most likely waiting on.</para>
    ///
    /// <para>A silence abandons EVERY pending operation of BOTH kinds (the
    /// promote-only-across-an-answer rule, and the modem queue's BLOCKER-1
    /// lesson that each abandoned id must still complete). The returned list
    /// is raised by the caller, outside the lock.</para>
    /// </summary>
    private List<(long Id, RadioProperty Property)> PromoteNetadLocked(
        bool answered, out long nextReadId, out bool dispatchBook, out string? nextName)
    {
        nextReadId = 0;
        dispatchBook = false;
        nextName = null;

        _netadQueue.ActiveId = 0;
        _netadQueue.ActiveKind = NetadOp.None;
        _netadQueue.ActiveName = null;

        var abandoned = new List<(long, RadioProperty)>(2);
        if (!answered)
        {
            if (_netadQueue.PendingBookId != 0)
                abandoned.Add((_netadQueue.PendingBookId, RadioProperty.AleBookRead));
            if (_netadQueue.PendingMemberId != 0)
                abandoned.Add((_netadQueue.PendingMemberId, RadioProperty.AleMemberRead));
            _netadQueue.PendingBookId = 0;
            _netadQueue.PendingMemberId = 0;
            _netadQueue.PendingNames.Clear();
            return abandoned;
        }

        if (_netadQueue.PendingBookId != 0)
        {
            _netadQueue.ActiveId = _netadQueue.PendingBookId;
            _netadQueue.ActiveKind = NetadOp.Book;
            _netadQueue.PendingBookId = 0;
            BeginBookAccumulationLocked();
            nextReadId = _netadQueue.ActiveId;
            dispatchBook = true;
        }
        else if (_netadQueue.PendingMemberId != 0)
        {
            // One name leaves the pending set per promotion; the rest keep the
            // SAME pending id, so every coalesced requester still matches a
            // completion it was handed (the last name to go carries it).
            var name = FirstOrdered(_netadQueue.PendingNames);
            _netadQueue.PendingNames.Remove(name);
            _netadQueue.ActiveId = _netadQueue.PendingNames.Count == 0
                ? _netadQueue.PendingMemberId
                : NextReadIdLocked();
            if (_netadQueue.PendingNames.Count == 0) _netadQueue.PendingMemberId = 0;
            _netadQueue.ActiveKind = NetadOp.Members;
            _netadQueue.ActiveName = name;
            _memberAnswers = [];
            nextReadId = _netadQueue.ActiveId;
            nextName = name;
        }
        return abandoned;
    }

    private void RaiseAbandoned(List<(long Id, RadioProperty Property)> abandoned)
    {
        foreach (var (id, property) in abandoned)
        {
            if (property == RadioProperty.AleBookRead) _lastBookRead = new AleReadCompletion(id, false);
            else _lastMemberRead = new AleReadCompletion(id, false);
            _raise(property);
        }
    }

    private static string FirstOrdered(HashSet<string> names)
    {
        string? first = null;
        foreach (var n in names)
            if (first is null || string.CompareOrdinal(n, first) < 0) first = n;
        return first!;
    }

    /// <summary>Apply one <c>MEMBER nn  &lt;addr&gt;</c> continuation line. It
    /// carries NO net name, so the ONLY honest attribution is the ACTIVE
    /// member operation's own net (the store-queue doctrine: every response
    /// line on the wire belongs to the single active operation). With no
    /// member read in flight the line is unattributable and is IGNORED —
    /// unlike a CHGROUP line, which names its own slot and can therefore take
    /// the unsolicited-upsert path.</summary>
    internal void ApplyNetMember(int number, string address)
    {
        lock (_bookLock) _memberAnswers?.Add(new AleNetMember(number, address));
    }

    /// <summary>The positive empty-state marker <c>NO MEMBERS PRGMD</c>. The
    /// accumulator is already empty in that case, so this asserts rather than
    /// changes the outcome — but it is the radio SAYING "none", which is a
    /// different fact from "nothing arrived", and the pin follows it.</summary>
    internal void NoteNoMembersProgrammed()
    {
        lock (_bookLock) _memberAnswers?.Clear();
    }

    /// <summary>Request the SCHEDULE read (bare <c>EXCH</c>). Same
    /// dispatch/coalesce contract as <see cref="RequestBookRead"/>.</summary>
    internal long RequestScheduleRead(out bool dispatch)
    {
        lock (_bookLock)
        {
            if (_scheduleQueue.ActiveId != 0)
            {
                dispatch = false;
                if (_scheduleQueue.PendingId == 0) _scheduleQueue.PendingId = NextReadIdLocked();
                return _scheduleQueue.PendingId;
            }
            _scheduleQueue.ActiveId = NextReadIdLocked();
            _scheduleAnswers = [];
            dispatch = true;
            return _scheduleQueue.ActiveId;
        }
    }

    internal void CompleteScheduleRead(long readId, bool answered, out long nextReadId, out bool dispatchNext)
    {
        nextReadId = 0;
        dispatchNext = false;

        bool published = false;
        lock (_bookLock)
        {
            if (_scheduleQueue.ActiveId != readId) return;

            if (answered && _scheduleAnswers is { } rows)
            {
                LqaSchedules = rows;
                published = true;
            }
            _scheduleAnswers = null;
        }

        if (published) _raise(RadioProperty.AleLqaSchedules);

        _lastScheduleRead = new AleReadCompletion(readId, answered);
        _raise(RadioProperty.AleScheduleRead);

        long abandonedId;
        lock (_bookLock)
        {
            if (_scheduleQueue.ActiveId != readId) return;
            _scheduleQueue.ActiveId = 0;
            if (_scheduleQueue.PendingId == 0) return;

            abandonedId = AbandonPendingAfterSilenceLocked(_scheduleQueue, answered);
            if (abandonedId == 0)
            {
                _scheduleQueue.ActiveId = _scheduleQueue.PendingId;
                _scheduleQueue.PendingId = 0;
                _scheduleAnswers = [];
                nextReadId = _scheduleQueue.ActiveId;
                dispatchNext = true;
            }
        }
        if (abandonedId != 0) RaiseAbandoned(ref _lastScheduleRead, abandonedId, RadioProperty.AleScheduleRead);
    }

    /// <summary>Apply one <c>EXCHANGE</c>/<c>SOUND</c> listing row. Attributed
    /// to the active schedule operation only — the listing is a whole-table
    /// answer, so a row arriving outside one belongs to no readable snapshot
    /// (there is no per-row unsolicited report on this wire).</summary>
    internal void ApplyLqaSchedule(LqaSchedule row)
    {
        lock (_bookLock) _scheduleAnswers?.Add(row);
    }

    /// <summary>The positive empty-state marker <c>NO LQA SCHEDULED</c> —
    /// the schedule twin of <see cref="NoteNoMembersProgrammed"/>.</summary>
    internal void NoteNoLqaScheduled()
    {
        lock (_bookLock) _scheduleAnswers?.Clear();
    }

    // ---- Invalidation (round 11 §8) -------------------------------------
    // A write that can change a mirror puts it back to UNREAD rather than
    // guessing the new value: the display then shows the third state and the
    // owning tab re-reads. Invalidation happens at SEND time, which is a SAFE
    // SUPERSET of the plan's "accepted" rule — Core has no acceptance signal
    // (attribution is the app-layer gate's job), and a REFUSED write leaves the
    // radio unchanged, so the worst case is one extra read of the same rows.
    // The opposite error — keeping a mirror that the write may have moved — is
    // the one that puts a lie on screen.

    internal void InvalidateNetMembers(string netName)
    {
        bool changed;
        lock (_bookLock)
        {
            if (!NetMembers.ContainsKey(netName)) return;
            var table = new Dictionary<string, IReadOnlyList<AleNetMember>>(
                (IDictionary<string, IReadOnlyList<AleNetMember>>)NetMembers,
                StringComparer.OrdinalIgnoreCase);
            changed = table.Remove(netName);
            NetMembers = table;
        }
        if (changed) _raise(RadioProperty.AleNetMembers);
    }

    /// <summary>Every net's membership goes unread: <c>DELAD</c> removes the
    /// address from EVERY net's member list (proven on a two-net member,
    /// 2026-08-17) and <c>ERASE</c> clears the whole book.</summary>
    internal void InvalidateAllNetMembers()
    {
        bool changed;
        lock (_bookLock)
        {
            changed = NetMembers.Count > 0;
            if (changed)
                NetMembers = new Dictionary<string, IReadOnlyList<AleNetMember>>(StringComparer.OrdinalIgnoreCase);
        }
        if (changed) _raise(RadioProperty.AleNetMembers);
    }

    /// <summary>The schedule mirror goes unread: <c>DELAD</c> removes the
    /// address's queued schedule with it, and <c>ERASE</c> clears them all.</summary>
    internal void InvalidateLqaSchedules()
    {
        bool changed;
        lock (_bookLock)
        {
            changed = LqaSchedules is not null;
            LqaSchedules = null;
        }
        if (changed) _raise(RadioProperty.AleLqaSchedules);
    }

    /// <summary>The active GROUP operation's answers, one entry per slot the
    /// radio actually answered for. A slot in the operation's set with no
    /// entry at commit is queried-and-EMPTY.</summary>
    private readonly Dictionary<int, int[]> _groupAnswers = [];

    private AleReadCompletion _lastBookRead, _lastGroupRead, _lastSync;

    /// <summary>Completion record of the last committed BOOK read.</summary>
    public AleReadCompletion LastBookRead => _lastBookRead;
    /// <summary>Completion record of the last committed GROUP read.</summary>
    public AleReadCompletion LastGroupRead => _lastGroupRead;
    /// <summary>Completion record of the last bare sentinel barrier.</summary>
    public AleReadCompletion LastSync => _lastSync;

    /// <summary>Caller holds <see cref="_bookLock"/>.</summary>
    private long NextReadIdLocked() => ++_nextReadId;

    /// <summary>Request a BOOK read. <paramref name="dispatch"/> true means
    /// this call BEGAN the operation and the caller must put the three
    /// listings + the sentinel on the wire; false means it coalesced into
    /// the pending operation (whose id is returned) and sends nothing.</summary>
    internal long RequestBookRead(out bool dispatch)
    {
        lock (_bookLock)
        {
            // Round 11: "already busy" now means EITHER NETAD-producing
            // operation, so a book refresh also waits behind a targeted
            // membership read (and vice versa) — that mutual exclusion is what
            // keeps a targeted record out of this accumulator.
            if (_netadQueue.ActiveKind != NetadOp.None)
            {
                dispatch = false;
                if (_netadQueue.PendingBookId == 0) _netadQueue.PendingBookId = NextReadIdLocked();
                return _netadQueue.PendingBookId;
            }
            _netadQueue.ActiveId = NextReadIdLocked();
            _netadQueue.ActiveKind = NetadOp.Book;
            BeginBookAccumulationLocked();
            dispatch = true;
            return _netadQueue.ActiveId;
        }
    }

    private void BeginBookAccumulationLocked()
    {
        _refreshSelf = [];
        _refreshIndividual = [];
        _refreshNet = [];
    }

    /// <summary>Commit the BOOK operation <paramref name="readId"/>: publish
    /// the accumulation when the sentinel answered, keep the last confirmed
    /// book when it did not (the documented swallowed-listing quirk), fire
    /// the completion either way, then promote any pending operation —
    /// returned via <paramref name="nextReadId"/>/<paramref name="dispatchNext"/>
    /// for the caller to put on the wire.</summary>
    internal void CompleteBookRead(long readId, bool answered,
        out long nextReadId, out bool dispatchNext, out string? nextMemberName)
    {
        nextReadId = 0;
        dispatchNext = false;
        nextMemberName = null;

        List<AleAddress>? self, individual, net;
        lock (_bookLock)
        {
            // A completion for an operation this store no longer owns (a
            // reconnect reset the queue under it) commits nothing.
            if (_netadQueue.ActiveId != readId || _netadQueue.ActiveKind != NetadOp.Book) return;

            self = _refreshSelf; individual = _refreshIndividual; net = _refreshNet;
            _refreshSelf = _refreshIndividual = _refreshNet = null;
            if (answered && self is not null)
            {
                SelfAddresses = self;
                IndividualAddresses = individual!;
                NetAddresses = net!;
            }
        }

        if (answered && self is not null)
        {
            _raise(RadioProperty.AleSelfAddresses);
            _raise(RadioProperty.AleIndividualAddresses);
            _raise(RadioProperty.AleNetAddresses);
        }

        _lastBookRead = new AleReadCompletion(readId, answered);
        _raise(RadioProperty.AleBookRead);

        // The queue is released only AFTER the raises above, so a consumer
        // that requests another read from its Changed handler coalesces into
        // the pending operation instead of racing a second active one.
        List<(long Id, RadioProperty Property)> abandoned;
        lock (_bookLock)
        {
            if (_netadQueue.ActiveId != readId) return;
            abandoned = PromoteNetadLocked(answered, out nextReadId, out dispatchNext, out nextMemberName);
        }
        RaiseAbandoned(abandoned);
    }

    /// <summary>
    /// A pending operation may only be PROMOTED across an operation the radio
    /// ANSWERED. An unanswered sentinel means the radio never told us where
    /// it is in the command stream: the dead operation's answers may still be
    /// in flight, and nothing on the wire distinguishes them from the pending
    /// read's own — so promoting would build a snapshot out of a mixture and
    /// publish it as the pending read's own (audit round 1, BLOCKER 3).
    /// The pending operation is therefore ABANDONED: it publishes nothing,
    /// keeps prior state, and completes UNANSWERED so its requesters are
    /// told rather than left waiting. Any delayed line that then arrives with
    /// the store idle takes the unsolicited-upsert path — the radio's own
    /// latest word about that slot, which the next read overwrites.
    /// Returns the abandoned id, or 0 when the pending op may proceed.
    /// Caller holds <see cref="_bookLock"/>.
    /// </summary>
    private static long AbandonPendingAfterSilenceLocked(ReadQueue queue, bool answered)
    {
        if (answered) return 0;
        long abandoned = queue.PendingId;
        queue.PendingId = 0;
        queue.PendingSlots = [];
        return abandoned;
    }

    private void RaiseAbandoned(ref AleReadCompletion last, long readId, RadioProperty property)
    {
        last = new AleReadCompletion(readId, false);
        _raise(property);
    }

    /// <summary>Request a GROUP read for <paramref name="slots"/>.
    /// <paramref name="dispatchSlots"/> non-null means this call BEGAN the
    /// operation and the caller must send one <c>CHG</c> per slot (ascending)
    /// + the sentinel; null means the request coalesced into the pending
    /// operation, whose id is returned.</summary>
    internal long RequestGroupRead(IReadOnlyList<int> slots, out int[]? dispatchSlots)
    {
        lock (_bookLock)
        {
            if (_groupQueue.ActiveId != 0)
            {
                dispatchSlots = null;
                if (_groupQueue.PendingId == 0)
                {
                    _groupQueue.PendingId = NextReadIdLocked();
                    _groupQueue.PendingSlots = [];
                }
                foreach (var g in slots) _groupQueue.PendingSlots.Add(g);
                return _groupQueue.PendingId;
            }

            _groupQueue.ActiveId = NextReadIdLocked();
            _groupQueue.ActiveSlots = [.. slots];
            _groupAnswers.Clear();
            dispatchSlots = Ascending(_groupQueue.ActiveSlots);
            return _groupQueue.ActiveId;
        }
    }

    private static int[] Ascending(HashSet<int> slots)
    {
        var ordered = new int[slots.Count];
        slots.CopyTo(ordered);
        Array.Sort(ordered);
        return ordered;
    }

    /// <summary>Commit the GROUP operation <paramref name="readId"/>,
    /// publishing EXACTLY its slot set: a slot the radio answered for gets
    /// those channels, a slot it stayed silent on gets <c>[]</c> (the
    /// captured empty-group silence). An unanswered sentinel publishes
    /// nothing and keeps prior state. Same promote-after-raise discipline as
    /// <see cref="CompleteBookRead"/>.</summary>
    internal void CompleteGroupRead(long readId, bool answered, out long nextReadId, out int[]? nextSlots)
    {
        nextReadId = 0;
        nextSlots = null;

        bool published = false;
        lock (_bookLock)
        {
            if (_groupQueue.ActiveId != readId) return;

            if (answered)
            {
                var table = ChannelGroups.ToArray();
                foreach (var slot in _groupQueue.ActiveSlots)
                    table[slot] = new AleChannelGroup(
                        slot, _groupAnswers.TryGetValue(slot, out var channels) ? channels : []);
                ChannelGroups = table;
                published = true;
            }
            _groupAnswers.Clear();
        }

        if (published) _raise(RadioProperty.AleChannelGroups);

        _lastGroupRead = new AleReadCompletion(readId, answered);
        _raise(RadioProperty.AleGroupRead);

        long abandonedId;
        lock (_bookLock)
        {
            if (_groupQueue.ActiveId != readId) return;
            _groupQueue.ActiveId = 0;
            if (_groupQueue.PendingId == 0) return;

            abandonedId = AbandonPendingAfterSilenceLocked(_groupQueue, answered);
            if (abandonedId == 0)
            {
                _groupQueue.ActiveId = _groupQueue.PendingId;
                _groupQueue.ActiveSlots = _groupQueue.PendingSlots;
                _groupQueue.PendingId = 0;
                _groupQueue.PendingSlots = [];
                _groupAnswers.Clear();
                nextReadId = _groupQueue.ActiveId;
                nextSlots = Ascending(_groupQueue.ActiveSlots);
            }
        }
        if (abandonedId != 0) RaiseAbandoned(ref _lastGroupRead, abandonedId, RadioProperty.AleGroupRead);
    }

    /// <summary>A bare sentinel barrier: no accumulator, no queue, no
    /// deferral (that independence is what makes the app-layer programming
    /// bracket atomic). Returns the barrier's id.</summary>
    internal long BeginSync()
    {
        lock (_bookLock) return NextReadIdLocked();
    }

    internal void CompleteSync(long readId, bool answered)
    {
        _lastSync = new AleReadCompletion(readId, answered);
        _raise(RadioProperty.AleSync);
    }

    /// <summary>Apply one <c>CHGROUP … CHANS …</c> line. Inside a group
    /// operation it ACCUMULATES for that operation's slots; a line for a slot
    /// OUTSIDE the active operation's set is ignored (it belongs to no
    /// operation this store can honestly attribute, and publishing it would
    /// be a partial commit). With no operation in flight the line is an
    /// unsolicited report and upserts the published slot directly — the
    /// standalone-line doctrine every address line already follows.</summary>
    internal void ApplyChannelGroup(int group, IReadOnlyList<int> channels)
    {
        bool published = false;
        lock (_bookLock)
        {
            if (_groupQueue.ActiveId != 0)
            {
                if (_groupQueue.ActiveSlots.Contains(group))
                    _groupAnswers[group] = [.. channels];
            }
            else
            {
                var table = ChannelGroups.ToArray();
                table[group] = new AleChannelGroup(group, [.. channels]);
                ChannelGroups = table;
                published = true;
            }
        }
        if (published) _raise(RadioProperty.AleChannelGroups);
    }

    // ---- Programming refusals ------------------------------------------

    private AleProgrammingRefusal _programmingRefusal;

    /// <summary>The last refusal line the radio sent (" ADDRESS EXISTS ",
    /// " INV ASSOC SELF ", "** ERROR **", …) with a monotone session
    /// sequence. The mirror records EVERY refusal regardless of what drew it
    /// — attribution to a programming operation is the app-layer gate's job,
    /// which is why the sequence is public.</summary>
    public AleProgrammingRefusal ProgrammingRefusal => _programmingRefusal;

    internal void NoteProgrammingRefusal(string line)
    {
        lock (_bookLock)
            _programmingRefusal = new AleProgrammingRefusal(_programmingRefusal.Sequence + 1, line);
        _raise(RadioProperty.AleProgrammingRefusal);
    }

    /// <summary>
    /// Apply one <c>SLFAD</c>/<c>INDAD</c>/<c>NETAD</c> record. Inside a BOOK
    /// refresh it accumulates; outside one it upserts the published listing.
    ///
    /// <para><b>THE TWO PATHS ORDER DIFFERENTLY, AND MUST</b> (audit round 1
    /// MAJOR 5, corrected by round 2 MAJOR-A). Listing order IS creation order,
    /// and §5 derives the PRIMARY self from index 0 — so what "position" means
    /// depends on whether a listing is currently establishing it:</para>
    /// <list type="bullet">
    /// <item><b>Inside an accumulation the LISTING is the authority</b>, so a
    /// repeated report RE-ESTABLISHES position at the end. Any line reaching
    /// the accumulator before the listing burst — a write echo, an unsolicited
    /// re-report — is indistinguishable from a listing row on the wire, so it
    /// cannot be filtered out; what it must not do is FIX a position the
    /// listing has not given yet. Letting the listing's own later row move the
    /// address to its true place is what makes the committed order the
    /// listing's order whatever arrived first (round 2 MAJOR-A: with
    /// in-place-everywhere, an echo for an existing SECONDARY self before the
    /// listing burst committed SECONDARY, PRIMARY — and the book would have
    /// tagged the secondary as primary).</item>
    /// <item><b>Outside an accumulation nothing is establishing order</b>, so a
    /// re-report updates FIELDS and keeps its INDEX. Here remove-then-append is
    /// the defect: a targeted <c>NETAD &lt;name&gt;</c> read's record line is
    /// exactly such a re-report and would otherwise move that net to the end of
    /// the published book (round 1 MAJOR 5).</item>
    /// </list>
    /// </summary>
    internal void UpsertAddress(AleAddressKind kind, AleAddress addr)
    {
        lock (_bookLock)
        {
            var refreshing = kind switch
            {
                AleAddressKind.Self => _refreshSelf,
                AleAddressKind.Individual => _refreshIndividual,
                _ => _refreshNet,
            };
            if (refreshing is not null)
            {
                // Listing-authoritative: last report wins the position.
                refreshing.RemoveAll(a => a.Address == addr.Address);
                refreshing.Add(addr);
                return;
            }

            var current = kind switch
            {
                AleAddressKind.Self => SelfAddresses,
                AleAddressKind.Individual => IndividualAddresses,
                _ => NetAddresses,
            };
            var book = new List<AleAddress>(current);
            UpsertInPlace(book, addr);

            if (kind == AleAddressKind.Self) SelfAddresses = book;
            else if (kind == AleAddressKind.Individual) IndividualAddresses = book;
            else NetAddresses = book;
        }

        _raise(kind switch
        {
            AleAddressKind.Self => RadioProperty.AleSelfAddresses,
            AleAddressKind.Individual => RadioProperty.AleIndividualAddresses,
            _ => RadioProperty.AleNetAddresses,
        });
    }

    /// <summary>Replace the row for this address if the list already holds it,
    /// keeping its INDEX; append otherwise. The PUBLISHED-book rule — see
    /// <see cref="UpsertAddress"/> for why the accumulator's is the opposite.
    /// Caller holds <see cref="_bookLock"/>.</summary>
    private static void UpsertInPlace(List<AleAddress> book, AleAddress addr)
    {
        int existing = book.FindIndex(a => a.Address == addr.Address);
        if (existing >= 0) book[existing] = addr;
        else book.Add(addr);
    }

    internal void UpsertTxMessage(AmdMessage message)
    {
        lock (_bookLock)
        {
            var messages = new List<AmdMessage>(TxMessages);
            messages.RemoveAll(m => m.Slot == message.Slot);
            messages.Add(message);
            messages.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            TxMessages = messages;
        }
        _raise(RadioProperty.AleTxMessages);
    }

    /// <summary>Forget every stored TX message the radio has reported.
    /// <para>Round 11 §9A: the message mirror is UPSERT-ONLY (a listing that
    /// omits a slot cannot un-say it), so a re-listing after the clone's
    /// slot DELETES would still show the deleted rows. The clone campaign
    /// clears explicitly before each listing — the same gesture, and the same
    /// reason, as <c>RadioState.ClearChannelList</c>. Sends nothing.</para></summary>
    internal void ClearTxMessages()
    {
        lock (_bookLock) { TxMessages = []; }
        _raise(RadioProperty.AleTxMessages);
    }

    /// <summary>Received AMDs, UPSERT-BY-SLOT like <see cref="TxMessages"/>,
    /// sorted by slot (the radio stores newest at 00 and shifts down, so slot
    /// order reads newest-first). An async arrival upserts slot 00 — the
    /// shift it implies for older slots is NOT narrated on the wire, so those
    /// rows go stale until the next listing; the refresh's clear-then-relist
    /// is the correction, the same reason <see cref="ClearTxMessages"/>
    /// exists.</summary>
    public IReadOnlyList<RxAmdMessage> RxMessages { get; private set; } = [];

    internal void AppendRxMessage(RxAmdMessage message)
    {
        lock (_bookLock)
        {
            var messages = new List<RxAmdMessage>(RxMessages);
            messages.RemoveAll(m => m.Slot == message.Slot);
            messages.Add(message);
            messages.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            RxMessages = messages;
        }
        _raise(RadioProperty.AleRxMessages);
    }

    /// <summary>The last heard-on-air event (see <see cref="AleHeard"/>);
    /// null until one arrives this session.</summary>
    public AleHeard? LastHeard { get; private set; }

    internal void SetLastHeard(AleHeard heard)
    {
        LastHeard = heard;
        _raise(RadioProperty.AleLastHeard);
    }

    internal void ClearRxMessages()
    {
        lock (_bookLock) { RxMessages = []; }
        _raise(RadioProperty.AleRxMessages);
    }

    internal void ClearLqaReport()
    {
        lock (_bookLock) { LqaReport = []; }
        _raise(RadioProperty.AleLqaReport);
    }

    internal void AppendLqaScore(LqaScore score)
    {
        lock (_bookLock) { LqaReport = [.. LqaReport, score]; }
        _raise(RadioProperty.AleLqaReport);
    }

    /// <summary>Silent reset for a fresh connection: nothing a previous radio
    /// reported may survive; everything is unconfirmed until the new radio
    /// speaks.</summary>
    internal void ResetForConnect()
    {
        _linkState = default;
        _fillState = default;
        _linkedStation = null;
        _linkedChannel = null;
        LastHeard = null;
        _lqaStation = null;
        _lqaChannel = null;
        _allCall = _anyCall = _amdDisplay = _keyToCall = _listenBeforeTx = _radioSilence = default;
        _maxScanChannels = _linkTimeoutMinutes = _tuneTimeSeconds = default;
        _lastBookRead = default;
        _lastGroupRead = default;
        _lastSync = default;
        _lastMemberRead = default;
        _lastScheduleRead = default;
        lock (_bookLock)
        {
            SelfAddresses = [];
            IndividualAddresses = [];
            NetAddresses = [];
            TxMessages = [];
            RxMessages = [];
            LqaReport = [];
            NetMembers = new Dictionary<string, IReadOnlyList<AleNetMember>>(StringComparer.OrdinalIgnoreCase);
            LqaSchedules = null;
            _memberAnswers = null;
            _scheduleAnswers = null;
            _scheduleQueue.ActiveId = _scheduleQueue.PendingId = 0;
            _refreshSelf = _refreshIndividual = _refreshNet = null;
            // Nothing a previous radio reported may survive, and no operation
            // it was answering may commit: both queues go idle, so a late
            // completion for a pre-reset operation finds no owner and does
            // nothing (Prc138Radio.Connect resets BEFORE dropping pings).
            ChannelGroups = BlankChannelGroups();
            _netadQueue.ActiveId = _netadQueue.PendingBookId = _netadQueue.PendingMemberId = 0;
            _netadQueue.ActiveKind = NetadOp.None;
            _netadQueue.ActiveName = null;
            _netadQueue.PendingNames.Clear();
            _groupQueue.ActiveId = _groupQueue.PendingId = 0;
            _groupQueue.ActiveSlots = [];
            _groupQueue.PendingSlots = [];
            _groupAnswers.Clear();
            _programmingRefusal = default;
        }
    }
}
