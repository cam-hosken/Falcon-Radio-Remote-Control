using Falcon.Core.Protocol;

namespace Falcon.Core.Radio;

/// <summary>One hopping net as reported by DIS / SH lines.</summary>
public sealed record HopNet
{
    public required int Number { get; init; }
    /// <summary>8-digit net ID; null when the radio has not reported one —
    /// EITHER because no NETID line has covered this net yet OR because the
    /// line carried the unprogrammed "XXXXXXXX" form. The two are told apart
    /// by <see cref="IsReportedUnprogrammed"/>, never by the null alone.</summary>
    public string? NetId { get; init; }
    /// <summary>The radio REPORTED this net unprogrammed: a <c>NETID n
    /// XXXXXXXX</c> line arrived (the wire's own X-form). This is a POSITIVE
    /// report, not the absence of one — a record whose NETID line never
    /// arrived leaves it false, and so does a record created by a
    /// <c>Hoptype</c> line alone (protocol.md: a wiped net reports
    /// <c>NETID XXXXXXXX</c> AND a <c>Hoptype WB</c> line, so "only a type"
    /// is not an unprogrammed signature). Cleared by a real NETID report.
    /// <para>Consumers need it to tell the constitution's third display state
    /// (confirmed-unprogrammed → the radio's own "XXXXXXXX" / "not
    /// programmed") from the second (unreported → "—").</para></summary>
    public bool IsReportedUnprogrammed { get; init; }
    public HopType? Type { get; init; }
    /// <summary>Center (NB) frequency in kHz as reported; null until a Center
    /// line reports one (its X-form maps to null as well — an unprogrammed
    /// net is told by <see cref="IsReportedUnprogrammed"/>, not by this).</summary>
    public string? CenterKHz { get; init; }
    /// <summary>WB band LOW edge in kHz as reported by the DIS <c>Hopset</c>
    /// value line; null until one reports it (the captured wiped form
    /// <c>Hopset nn XXXXXX XXXXXX</c> maps to null as well — an unprogrammed
    /// net is told by <see cref="IsReportedUnprogrammed"/>, not by this).
    /// <para>The two edges are a PAIR: they are set together or not at all,
    /// so a consumer rendering "low–high" can never get half a band.</para>
    /// <para><b>PROVISIONAL shape</b> (docs/protocol.md, round-5 §2.1.3): only
    /// the placeholder form has ever been captured — the programmed form is
    /// patterned off it and settled by a bench capture.</para></summary>
    public string? WidebandLowKHz { get; init; }
    /// <summary>WB band HIGH edge in kHz — the pair of
    /// <see cref="WidebandLowKHz"/>; same reporting and PROVISIONAL rules.</summary>
    public string? WidebandHighKHz { get; init; }
}

/// <summary>
/// One WB exclusion band as the radio prints it
/// (<c>Exclude 00  02000   03000 </c> — captured 2026-08-17,
/// bench/transcripts/phase3-hop-channel). The SET command takes 8-digit Hz;
/// the ECHO and the listing come back in 5-digit kHz, which is what is stored.
/// <para><b>PROVISIONAL for the MULTI-band listing</b> (docs/protocol.md;
/// §14 probe): only a SINGLE-band table has been captured, so the row shape is
/// certain and the multi-row layout is patterned off it.</para>
/// </summary>
public sealed record HopExcludeBand(int Band, string LowKHz, string HighKHz);

/// <summary>
/// HOP-domain reported state. Scalars are <see cref="Confirmed{T}"/>;
/// collections are copy-on-write (see <see cref="AleState"/>).
/// </summary>
public sealed class HopState
{
    private readonly Action<RadioProperty> _raise;
    internal HopState(Action<RadioProperty> raise) => _raise = raise;

    private Confirmed<int> _currentNet;
    public Confirmed<int> CurrentNet => _currentNet;
    internal bool SetCurrentNet(int v)
    {
        if (_currentNet.IsConfirmed && _currentNet.Value == v) return false;
        bool isNetChange = _currentNet.IsConfirmed;   // confirmed → DIFFERENT confirmed
        _currentNet = Confirmed<int>.Of(v);

        // Stage 5 audit F1 (plan §0/§2.4 — display truth): sync state and
        // List_Invalid are properties OF the current net. On a confirmed
        // net CHANGE they are no longer reported facts about the net now
        // displayed — net A's Sync_Failed chip or List_Invalid badge must
        // not carry onto net B's row. The radio re-reports both when
        // still true (sync in the SH block, List_Invalid at generation).
        // First sight (unconfirmed → confirmed) is the app learning the
        // value, not a change — same convention as the trigger table.
        //
        // Stage 8 (deferred-ledger fix): ALL net-scoped fields mutate BEFORE
        // the first raise, so no Changed handler can ever observe the new
        // net together with the old net's sync chip or List_Invalid badge —
        // the mirror is never observably inconsistent, not merely
        // narrower-than-a-render-cycle inconsistent.
        bool syncUnconfirmed = false, listInvalidCleared = false;
        if (isNetChange)
        {
            if (_syncState.IsConfirmed) { _syncState = default; syncUnconfirmed = true; }
            if (_isHopListInvalid) { _isHopListInvalid = false; listInvalidCleared = true; }
        }

        _raise(RadioProperty.HopCurrentNet);
        if (syncUnconfirmed) _raise(RadioProperty.HopSyncState);
        if (listInvalidCleared) _raise(RadioProperty.HopListInvalid);
        return true;
    }

    private Confirmed<int> _hopNum;
    /// <summary>Number of generated hop frequencies; 0 = no hopset.</summary>
    public Confirmed<int> HopNum => _hopNum;
    internal void SetHopNum(int v)
    {
        if (_hopNum.IsConfirmed && _hopNum.Value == v) return;
        _hopNum = Confirmed<int>.Of(v);
        _raise(RadioProperty.HopNum);
    }

    private Confirmed<HopSyncState> _syncState;
    public Confirmed<HopSyncState> SyncState => _syncState;
    internal void SetSyncState(HopSyncState v)
    {
        if (_syncState.IsConfirmed && _syncState.Value == v) return;
        _syncState = Confirmed<HopSyncState>.Of(v);
        _raise(RadioProperty.HopSyncState);
    }

    private bool _isGeneratingHopset;
    /// <summary>True between "Generating Hopset..." and a clearing line
    /// (Hopnum / No Hopset / TUNE terminal / HOP prompt — the EXCLUDE path
    /// emits none of the usual clearing lines, bench session-16).</summary>
    public bool IsGeneratingHopset => _isGeneratingHopset;
    internal void SetGeneratingHopset(bool v)
    {
        if (_isGeneratingHopset == v) return;

        // ROUND 15 N1 (plan §3.1, owner ruling Q1 = a): a GENERATION STARTS AN
        // UNREPORTED SYNC EPOCH. Sync is a property of the hopset the net is
        // running on, and generating a new one drops it — the owner observes
        // exactly that in the field when a mode lap re-enters HOP. The radio
        // does not announce the drop (no capture shows an async sync line on
        // re-entry; P7 is the probe), so the honest mirror is UNREPORTED —
        // "—" until the next sync report, async or from an `SH` block. Core
        // UNCONFIRMS; it never infers a `NoSync` (I-4).
        //
        // Only on the FALSE→TRUE edge, and only from a CONFIRMED value: a
        // second `Generating` while already generating raises nothing (the
        // equality guard above), and the TRUE→FALSE clearers touch no sync.
        // Hopnum is deliberately NOT unconfirmed (Q1; §11).
        //
        // Stage-8 ordering, as `SetCurrentNet` does it: mutate everything,
        // THEN raise — and `HopSyncState` AFTER `HopGeneratingHopset`, so no
        // handler can see "generating" alongside the old net's sync chip.
        bool syncUnconfirmed = false;
        if (v && _syncState.IsConfirmed) { _syncState = default; syncUnconfirmed = true; }

        _isGeneratingHopset = v;
        if (v) _generationCount++;

        _raise(RadioProperty.HopGeneratingHopset);
        if (syncUnconfirmed) _raise(RadioProperty.HopSyncState);
    }

    private int _generationCount;
    /// <summary>
    /// Number of hopset generations STARTED this session — every FALSE→TRUE
    /// edge of <see cref="IsGeneratingHopset"/>, whoever caused it (round 15
    /// §3.2).
    ///
    /// <para>A COUNTER, not the flag, for the same reason as
    /// <see cref="NoHopsetCount"/> beside it: a consumer whose notifications
    /// are MARSHALLED sees the mirror as it is when it finally runs, not as it
    /// was when the line arrived. A whole generation lifecycle — the
    /// <c>Generating Hopset...</c> and its clearing line — can be parsed
    /// between two runs of a UI-thread handler, and then the flag reads false
    /// at both ends and the generation is invisible. The count cannot be
    /// missed that way: it only ever goes up, so a consumer diffs it. Reset by
    /// <see cref="ResetForConnect"/> with the rest of the session's
    /// counters.</para>
    /// </summary>
    public int GenerationCount => _generationCount;

    private int _noHopsetCount;
    /// <summary>Number of No-Hopset reports this session (async "No Hopset"
    /// and SH-block "No_Hopset"). A COUNTER, not a flag, because the line is
    /// the only reliable signal that a net select ended WITHOUT generation
    /// (Stage 5 audit F4): HopNum may already be a confirmed 0, in which case
    /// re-reporting raises no change event — consumers diff the count.</summary>
    public int NoHopsetCount => _noHopsetCount;
    internal void NotifyNoHopset()
    {
        _noHopsetCount++;
        _raise(RadioProperty.HopNoHopset);
    }

    private int _noNetIdCount;
    /// <summary>Number of No-Net-ID reports this session (async
    /// <c>NO NET ID</c> and SH-block <c>No_Net_ID</c>). A COUNTER for the same
    /// reason as <see cref="NoHopsetCount"/>: it is the only signal that a net
    /// select ended without generation, and a repeat carries no state change of
    /// its own for a consumer to diff.
    /// <para>CAPTURED 2026-08-16 (docs/protocol.md, "<c>NET &lt;n&gt;</c> select
    /// echo"): selecting a net that HAS a hopset but NO net ID answers
    /// <c>NET  09</c> / <c>Wait...</c> / <c>NO NET ID</c>, and the SH block then
    /// carries <c>No_Net_ID</c> where a generated net would carry a hopnum.
    /// This is a THIRD refusal state alongside No-Hopset and List_Invalid, and
    /// it went unhandled until then — the async form fell into the parser's
    /// <c>NO</c> handler and matched none of its branches, so the app could not
    /// say why generation did nothing. The round-5 net editor makes the state
    /// reachable: program a hopset, never set an ID.</para></summary>
    public int NoNetIdCount => _noNetIdCount;
    internal void NotifyNoNetId()
    {
        _noNetIdCount++;
        _raise(RadioProperty.HopNoNetId);
    }

    private bool _isHopListInvalid;
    /// <summary>The radio answered "List_Invalid": a LIST-type net whose
    /// hoplist it refuses to use (needs ≥3 frequencies — bench 2026-08-01).</summary>
    public bool IsHopListInvalid => _isHopListInvalid;
    internal void SetHopListInvalid(bool v)
    {
        if (_isHopListInvalid == v) return;
        _isHopListInvalid = v;
        _raise(RadioProperty.HopListInvalid);
    }

    // ---- Net table (copy-on-write) --------------------------------------

    /// <summary>Net table from DIS/SH responses, keyed by net number.</summary>
    public IReadOnlyDictionary<int, HopNet> Nets { get; private set; } =
        new Dictionary<int, HopNet>();

    /// <summary>LIST-type hop frequencies (kHz strings) per net, from HOPLIST lines.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> HopLists { get; private set; } =
        new Dictionary<int, IReadOnlyList<string>>();

    private readonly object _tableLock = new();

    internal void UpdateNet(int number, Func<HopNet, HopNet> mutate)
    {
        lock (_tableLock)
        {
            var nets = new Dictionary<int, HopNet>((IDictionary<int, HopNet>)Nets);
            var net = nets.TryGetValue(number, out var existing) ? existing : new HopNet { Number = number };
            nets[number] = mutate(net);
            Nets = nets;
        }
        _raise(RadioProperty.HopNets);
    }

    internal void SetHopList(int net, IReadOnlyList<string> freqs)
    {
        lock (_tableLock)
        {
            var lists = new Dictionary<int, IReadOnlyList<string>>((IDictionary<int, IReadOnlyList<string>>)HopLists)
            {
                [net] = freqs,
            };
            HopLists = lists;
        }
        _raise(RadioProperty.HopLists);
    }

    // ---- WB exclusion bands (round 11 §8, R11/X9) -----------------------
    // THE TRAP THIS SOLVES: an EMPTY exclusion table answers NOTHING AT ALL
    // (captured 2026-08-17 — bare `EXC` on an empty table returned the prompt
    // alone). Silence therefore cannot be told from a swallowed query by
    // looking at the wire, so the read is SENTINEL-BRACKETED like every other
    // store here: rows arriving before the sentinel commit atomically, NO rows
    // before an ANSWERED sentinel is the READ-EMPTY state, and an unanswered
    // sentinel keeps whatever was known before.

    /// <summary>The programmed exclusion bands from the last committed
    /// <c>EXC</c> read, in the radio's listing order (band slots 0-9, so at
    /// most ten rows). THREE states: <c>null</c> = never read this session,
    /// <c>[]</c> = read and confirmed empty, rows otherwise.</summary>
    public IReadOnlyList<HopExcludeBand>? ExcludeBands { get; private set; }

    private long _excludeActiveId, _excludePendingId, _nextExcludeReadId;
    private List<HopExcludeBand>? _excludeAnswers;
    private AleReadCompletion _lastExcludeRead;

    /// <summary>Completion record of the last committed EXCLUDE read.</summary>
    public AleReadCompletion LastExcludeRead => _lastExcludeRead;

    /// <summary>Request the exclusion-band read. <paramref name="dispatch"/>
    /// true means this call BEGAN the operation and the caller must send
    /// <c>EXC</c> + the sentinel; false means it coalesced into the single
    /// pending operation, whose id is returned.</summary>
    internal long RequestExcludeRead(out bool dispatch)
    {
        lock (_tableLock)
        {
            if (_excludeActiveId != 0)
            {
                dispatch = false;
                if (_excludePendingId == 0) _excludePendingId = ++_nextExcludeReadId;
                return _excludePendingId;
            }
            _excludeActiveId = ++_nextExcludeReadId;
            _excludeAnswers = [];
            dispatch = true;
            return _excludeActiveId;
        }
    }

    internal void CompleteExcludeRead(long readId, bool answered, out long nextReadId, out bool dispatchNext)
    {
        nextReadId = 0;
        dispatchNext = false;

        bool published = false;
        lock (_tableLock)
        {
            if (_excludeActiveId != readId) return;
            if (answered && _excludeAnswers is { } rows)
            {
                ExcludeBands = rows;
                published = true;
            }
            _excludeAnswers = null;
        }

        if (published) _raise(RadioProperty.HopExcludeBands);

        _lastExcludeRead = new AleReadCompletion(readId, answered);
        _raise(RadioProperty.HopExcludeRead);

        long abandonedId = 0;
        lock (_tableLock)
        {
            if (_excludeActiveId != readId) return;
            _excludeActiveId = 0;
            if (_excludePendingId == 0) return;

            // Same rule the ALE stores follow: a pending operation may only be
            // promoted across an operation the radio ANSWERED — an unanswered
            // sentinel leaves the dead operation's rows possibly still in
            // flight, and nothing distinguishes them from the next read's own.
            if (!answered)
            {
                abandonedId = _excludePendingId;
                _excludePendingId = 0;
            }
            else
            {
                _excludeActiveId = _excludePendingId;
                _excludePendingId = 0;
                _excludeAnswers = [];
                nextReadId = _excludeActiveId;
                dispatchNext = true;
            }
        }
        if (abandonedId != 0)
        {
            _lastExcludeRead = new AleReadCompletion(abandonedId, false);
            _raise(RadioProperty.HopExcludeRead);
        }
    }

    /// <summary>Apply one <c>Exclude nn  &lt;low&gt;   &lt;high&gt;</c> line.
    /// Inside a read operation it ACCUMULATES; outside one it is the ECHO of a
    /// just-sent set (which also triggers regeneration) and UPSERTS the
    /// published table by band slot — the standalone-line doctrine, and the
    /// only honest reading of a line that names its own slot.</summary>
    internal void ApplyExcludeBand(int band, string lowKHz, string highKHz)
    {
        bool published = false;
        lock (_tableLock)
        {
            var row = new HopExcludeBand(band, lowKHz, highKHz);
            if (_excludeAnswers is { } accumulating)
            {
                accumulating.RemoveAll(b => b.Band == band);
                accumulating.Add(row);
            }
            else
            {
                var table = ExcludeBands is null ? [] : new List<HopExcludeBand>(ExcludeBands);
                table.RemoveAll(b => b.Band == band);
                table.Add(row);
                table.Sort((a, b) => a.Band.CompareTo(b.Band));
                ExcludeBands = table;
                published = true;
            }
        }
        if (published) _raise(RadioProperty.HopExcludeBands);
    }

    /// <summary>Silent reset for a fresh connection.</summary>
    internal void ResetForConnect()
    {
        _currentNet = default;
        _hopNum = default;
        _syncState = default;
        _isGeneratingHopset = false;
        _isHopListInvalid = false;
        _generationCount = 0;
        _noHopsetCount = 0;
        // Round 11 P4 (P1 audit finding, dispositioned here): the No-Net-ID
        // counter was the ONE session counter this reset forgot, so it carried
        // a previous radio's total into the next session. Both HOP refusal
        // counters now zero alike — and the round-11 §7 generation-attempt
        // state machine (HopSurface) DEPENDS on that: it detects a session
        // restart by the counter stepping BACKWARDS past its snapshot, which
        // a counter that never resets can never do.
        _noNetIdCount = 0;
        _lastExcludeRead = default;
        lock (_tableLock)
        {
            Nets = new Dictionary<int, HopNet>();
            HopLists = new Dictionary<int, IReadOnlyList<string>>();
            ExcludeBands = null;
            _excludeAnswers = null;
            _excludeActiveId = _excludePendingId = 0;
        }
    }
}
