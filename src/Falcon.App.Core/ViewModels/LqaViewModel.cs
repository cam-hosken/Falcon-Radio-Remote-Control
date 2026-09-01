using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>One RANK report row, verbatim wire values ("---"/"--" are real
/// displayed outcomes — unsounded channels), plus the channel's stored RX/TX
/// frequencies in the MHz vocabulary.
///
/// <para>Round 11 §4 (owner ruling R5): the report names channels, not
/// frequencies, so each named channel's stored record is read with ONE
/// targeted <c>DI n n</c> and the cells bind through the KEYED channel mirror.
/// Until that answer lands the two cells read "—" — the report row is real,
/// the frequency simply is not known yet, and a display never invents
/// one.</para></summary>
public sealed class LqaReportRowViewModel
{
    public string Channel { get; }
    public string Score { get; }
    public string MeasuredSnr { get; }
    public string ReceivedSnr { get; }
    /// <summary>Stored receive frequency, MHz vocabulary; "—" until the
    /// channel's targeted <c>DI</c> answer has landed.</summary>
    public string RxText { get; }
    /// <summary>Stored transmit frequency, MHz vocabulary; "—" until the
    /// channel's targeted <c>DI</c> answer has landed.</summary>
    public string TxText { get; }

    internal LqaReportRowViewModel(string channel, string score, string measuredSnr,
        string receivedSnr, string rxText, string txText)
    {
        Channel = channel;
        Score = score;
        MeasuredSnr = measuredSnr;
        ReceivedSnr = receivedSnr;
        RxText = rxText;
        TxText = txText;
    }

    /// <summary>The ONE placeholder row (§4's three-state display projection):
    /// every cell a hyphen. "No report this session" and "the report was
    /// empty" render IDENTICALLY — decided — so there is one template and no
    /// empty view, and the table always occupies its own height.</summary>
    public static LqaReportRowViewModel Placeholder { get; } =
        new("—", "—", "—", "—", "—", "—");
}

/// <summary>One Heard-stations row (owner design 2026-08-24): the station,
/// the channels of its CURRENT sounding pass (numeric order, space-joined),
/// and the app-clock time it was last heard.</summary>
public sealed class HeardStationRowViewModel
{
    public string Station { get; }
    public string ChannelsText { get; }
    public string LastHeardText { get; }

    internal HeardStationRowViewModel(string station, string channelsText, string lastHeardText)
    {
        Station = station;
        ChannelsText = channelsText;
        LastHeardText = lastHeardText;
    }
}

/// <summary>One queued LQA schedule row, mirrored from the radio's own bare
/// <c>EXCH</c> listing (round 11 §4).
///
/// <para><b>This replaces the session CARD.</b> The card existed because "the
/// radio has no schedule query"; it has one — bare <c>EXCH</c> (≡ bare
/// <c>SOU</c>) lists every sounding and exchange with its interval and start,
/// and <c>EXCH|SOU STO &lt;addr&gt;</c> deletes one. So the display is a
/// MIRROR of radio state: it survives a reconnect and shows schedules this app
/// never set.</para>
///
/// <para><see cref="DeleteCommand"/> acts on the row's OWN captured kind and
/// address, never on a picker selection, and is UNCONFIRMED — the per-row
/// Removes precedent (round 10 §5's popup matrix deliberately does not extend
/// to it). ROUND 15 F-1: the button reads <b>Delete</b>, not Stop — the row
/// action REMOVES a queued schedule (<c>STO</c>), and "Stop" read as though it
/// halted a running LQA, which is what the pane's own STOP (<c>ST</c>) does.
/// The command body is unchanged; this is the wording and the rename.</para></summary>
public sealed class LqaScheduleRowViewModel
{
    public string KindText { get; }          // "EXCH" / "SOU"
    public string Address { get; }
    public string IntervalText { get; }
    public string StartText { get; }
    /// <summary>False on the placeholder row — it has nothing to delete.</summary>
    public bool CanDelete { get; }
    public IRelayCommand DeleteCommand { get; }

    internal LqaScheduleRowViewModel(string kindText, string address, string intervalText,
        string startText, Action<LqaScheduleRowViewModel>? delete)
    {
        KindText = kindText;
        Address = address;
        IntervalText = intervalText;
        StartText = startText;
        CanDelete = delete is not null;
        DeleteCommand = new RelayCommand(() => delete?.Invoke(this), () => delete is not null);
    }

    /// <summary>The ONE hyphen placeholder row: the mirror is unread OR the
    /// radio answered <c>NO LQA SCHEDULED</c> — decided to render
    /// identically.</summary>
    public static LqaScheduleRowViewModel Placeholder { get; } =
        new("—", "—", "—", "—", null);
}

/// <summary>
/// The LQA sub-tab (plan §4.5; rebuilt by UI tweaks round 11 §4).
///
/// <para><b>Top — the RANK report viewer.</b> RAN per INDIVIDUAL (the one
/// address-type-restricted read; passive, does NOT transmit — SOUnd/EXCHange
/// are the gatherers), rows verbatim from the radio's CHAN:/SCORE report, each
/// carrying its channel's stored RX/TX from the keyed channel mirror
/// (R5).</para>
///
/// <para><b>Bottom — scheduling, now a RADIO MIRROR.</b> The queue is
/// READABLE: every landing on this tab re-reads it with one bare <c>EXCH</c>
/// (the editors-read-fresh tier — one cheap command), and every accepted write
/// re-reads it again. Rows render in the radio's own order. The builders write
/// through <c>EXCH|SOU STA</c> with hh:mm validated CLIENT-SIDE before
/// anything reaches the wire, because those commands answer NOTHING on
/// success — client validation is the only defence.</para>
///
/// <para><b>ROUND 15 §16 — Now, Schedule, Delete.</b> The SAME <c>STA</c> with
/// NO interval and no start is a ONE-TIME, IMMEDIATE LQA (P14 runs 1–3,
/// 2026-08-22): it is accepted silently, writes NO row into the queue, walks
/// every channel of the target's group announcing
/// <c>SOUNDING|EXCHANGE &lt;addr&gt; CHANNEL: nn</c>, and ends with a bare
/// <c>SCANNING</c>. That form is now its OWN control — <b>Now</b> — and the
/// scheduling buttons (<b>Schedule</b>) REQUIRE an interval, because a blank
/// one was silently the immediate form. The per-row button reads
/// <b>Delete</b>; the compose rows no longer carry <c>STO</c> at all (the row
/// that owns a schedule is what removes it). <b>Refresh LQA</b> re-reads the
/// schedule mirror — one bare <c>EXCH</c>, nothing else.</para>
///
/// <para>Both tables follow §4's three-state display rule: the real rows when
/// there are rows, otherwise EXACTLY ONE hyphen placeholder row. Add/queue
/// logic reads the MIRROR count, never the display collection — a projection
/// must never contaminate a mirror's count.</para>
/// </summary>
public partial class LqaViewModel : ObservableObject
{
    /// <summary>The radio's LQA queue capacity, measured 2026-08-17
    /// (` LQA QUEUE FULL ` at the eleventh row).</summary>
    public const int ScheduleCapacity = 10;

    private static readonly Regex HhMm = new(@"^\d{2}:\d{2}$", RegexOptions.Compiled);

    /// <summary>§16 F-2: a blank interval used to mean "the immediate form"
    /// implicitly. It is now refused on the SCHEDULE buttons and the message
    /// names the control that does mean it.</summary>
    internal const string IntervalRequired = "Interval required — use Now for a one-time LQA.";

    private readonly AleSurface _ale;
    private readonly ChannelSurface _channels;
    private readonly RadioSession _session;

    /// <summary>Channels this VM has already spent a targeted <c>DI</c> on
    /// THIS session (R5: once per session per channel, never a re-send).
    /// Cleared when the session leaves Ready — the mirror is cleared with
    /// it.</summary>
    private readonly HashSet<int> _channelReadsThisSession = [];

    /// <summary>The MIRROR's row count — the number the capacity gate reads.
    /// Never <c>ScheduleDisplayRows.Count</c>, which is 1 when the mirror is
    /// empty or unread.</summary>
    private int _mirroredScheduleCount;

    private bool _refreshing;

    /// <summary>The READ ID of the schedule re-read the in-flight Now is
    /// waiting on (0 = none). <c>SendLine</c> ENQUEUES behind the prompt gate,
    /// so two fast presses would queue two TRANSMISSIONS; the latch is what
    /// makes the second press impossible until the first press's own re-read
    /// has landed or been abandoned. Correlated BY ID (critic F71) because
    /// other schedule reads are outstanding at the same time — the tab's own
    /// landing read, and Refresh — and releasing on "a read landed" would free
    /// the button while the STA was still on the wire.</summary>
    private long _nowReadId;

    // ---- RAN report viewer ----
    [ObservableProperty] private IReadOnlyList<AleTargetChoice> rankChoices = [];
    [ObservableProperty] private AleTargetChoice? selectedRankStation;

    /// <summary>The REAL report rows (empty when there is no report). The
    /// table binds <see cref="ReportDisplayRows"/>; this is the honest count
    /// for anything that must tell "no rows" from "one placeholder".</summary>
    [ObservableProperty] private IReadOnlyList<LqaReportRowViewModel> reportRows = [];

    /// <summary>§4's display projection: the real rows, else EXACTLY ONE
    /// <see cref="LqaReportRowViewModel.Placeholder"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<LqaReportRowViewModel> reportDisplayRows = [LqaReportRowViewModel.Placeholder];

    // ---- EXCH builder ----
    [ObservableProperty] private IReadOnlyList<AleTargetChoice> exchChoices = [];
    [ObservableProperty] private AleTargetChoice? selectedExchTarget;
    [ObservableProperty] private string exchIntervalText = "";
    [ObservableProperty] private string exchStartText = "";
    [ObservableProperty] private string exchError = "";

    // ---- SOU builder ----
    [ObservableProperty] private IReadOnlyList<AleTargetChoice> souChoices = [];
    [ObservableProperty] private AleTargetChoice? selectedSouSelf;
    [ObservableProperty] private string souIntervalText = "";
    [ObservableProperty] private string souStartText = "";
    [ObservableProperty] private string souError = "";

    // ---- The schedule MIRROR ----
    /// <summary>The REAL mirrored rows (empty when the mirror is unread or the
    /// radio answered <c>NO LQA SCHEDULED</c>).</summary>
    [ObservableProperty] private IReadOnlyList<LqaScheduleRowViewModel> scheduleRows = [];

    /// <summary>§4's display projection: the real rows, else EXACTLY ONE
    /// <see cref="LqaScheduleRowViewModel.Placeholder"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<LqaScheduleRowViewModel> scheduleDisplayRows = [LqaScheduleRowViewModel.Placeholder];

    /// <summary>Why both STA buttons are dead: the MIRROR holds the radio's
    /// ten. Empty otherwise (§4's exact wording).</summary>
    [ObservableProperty] private string queueFullReason = "";

    /// <summary>A <b>Now</b> press is on the wire and its re-read has not
    /// landed yet (§16 F-2's in-flight latch). Both Now commands observe it;
    /// Delete, Refresh and the passive RANK read do NOT — they stay live
    /// through an LQA (P14b: a STO during a running exchange was accepted and
    /// removed its row).</summary>
    [ObservableProperty] private bool isNowInFlight;

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    // ---- Heard stations (owner design 2026-08-24; field capture #2) ----------
    // ONE row per station: a heard event within PassGapMinutes of the row's
    // previous event ADDS its channel; a longer gap starts the channel list
    // FRESH (a new sounding pass — hour-old channels should not imply current
    // propagation). Timestamps are the APP's clock, the sent-log precedent.
    // Session-scoped; newest-heard station first.

    internal const int PassGapMinutes = 10;

    private readonly TimeProvider _time;
    private sealed class HeardEntry
    {
        public required List<string> Channels;
        public required DateTimeOffset LastEvent;
    }
    private readonly Dictionary<string, HeardEntry> _heard = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Novelty by REFERENCE: Core raises a NEW AleHeard instance per
    /// wire line, so an already-consumed instance is a re-render, not news.
    /// (Two lines landing between renders would coalesce to the newest —
    /// accepted: heard lines arrive ~18 s apart on the wire.)</summary>
    private AleHeard? _consumedHeard;

    [ObservableProperty] private IReadOnlyList<HeardStationRowViewModel> heardRows = [];

    /// <summary>Empty the heard table (owner ask 2026-08-24) — VIEW state
    /// only, sends nothing. The consumed-event marker survives so the
    /// already-seen line does not resurrect a row on the next render.</summary>
    [RelayCommand]
    private void ClearHeard()
    {
        _heard.Clear();
        HeardRows = [];
    }

    private void ConsumeHeard()
    {
        var heard = _ale.LastHeard;
        if (heard is null || ReferenceEquals(heard, _consumedHeard)) return;
        _consumedHeard = heard;

        var now = _time.GetLocalNow();
        if (!_heard.TryGetValue(heard.Station, out var entry))
            _heard[heard.Station] = entry = new HeardEntry { Channels = [], LastEvent = now };
        else if ((now - entry.LastEvent) > TimeSpan.FromMinutes(PassGapMinutes))
            entry.Channels.Clear();                     // a NEW pass
        if (!entry.Channels.Contains(heard.Channel)) entry.Channels.Add(heard.Channel);
        entry.LastEvent = now;

        HeardRows = [.. _heard
            .OrderByDescending(kv => kv.Value.LastEvent)
            .Select(kv => new HeardStationRowViewModel(
                kv.Key,
                string.Join(" ", kv.Value.Channels.OrderBy(ch => ch, StringComparer.Ordinal)),
                kv.Value.LastEvent.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)))];
    }

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 10). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>A schedule read (bare <c>EXCH</c>) deferred to the campaign's
    /// end — from the tab landing or from an explicit Refresh press. One flag
    /// for the wire act, so however many were deferred, one goes out.</summary>
    private bool _schedulesReadOwed;

    public LqaViewModel(AleSurface ale, ChannelSurface channels, RadioSession session,
        TimeProvider? time = null, ICampaignSignal? campaign = null)
    {
        _ale = ale;
        _channels = channels;
        _session = session;
        _time = time ?? TimeProvider.System;
        _campaign = campaign;
        // The campaign's END edge runs the recompute; Rebuild settles what is
        // owed if this tab can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Rebuild(); };
        ale.Changed += (_, _) => Rebuild();
        // R5: the RX/TX cells bind through the KEYED channel mirror, so a
        // landing DI answer must re-render this tab.
        channels.Changed += (_, _) => Rebuild();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _channelReadsThisSession.Clear();
                // Session-scoped: a read deferred for a radio that has gone is
                // not owed to the next one.
                _schedulesReadOwed = false;
                ExchError = "";
                SouError = "";
                // The re-read the latch waits on can no longer land: the
                // session that owned it is gone.
                IsNowInFlight = false;
                _nowReadId = 0;
                // Heard stations are session-scoped, like the sent log.
                // The consumed-event marker SURVIVES the drop: the mirror
                // still holds the last event until the next connect resets
                // it, and nulling the marker here made this handler's own
                // Rebuild re-consume it straight back into the table.
                _heard.Clear();
                HeardRows = [];
            }
            Rebuild();
        };
        Rebuild();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool AleReady => Ready && _ale.IsAleConfirmed;

    /// <summary>The radio is ON AIR by its own announced line — the ONE call
    /// site for that term (§16 F-2's truth table, critic F70). Now and
    /// Schedule are withheld here; Delete, Refresh and RANK are not.
    ///
    /// <para>ROUND 15 item I (phase 5) landed the widening this comment
    /// promised: the term is now Core's <c>AleLinkState.IsOnAir()</c> through
    /// <see cref="AleSurface.IsOnAir"/>, so the three LQA states join
    /// <c>Calling|Sending|Linked</c>. A bare STA transmits for MINUTES (P14c),
    /// which is exactly what Now and Schedule must not queue behind.</para></summary>
    private bool IsOnAir => _ale.IsOnAir;

    /// <summary>The MIRROR is full (§4's capacity gate). Reads the mirror's
    /// own count — a display projection never contaminates it.</summary>
    private bool IsQueueFull => _mirroredScheduleCount >= ScheduleCapacity;

    /// <summary>Row-action entry point (ALE pane "LQA ▸", individuals only).</summary>
    public void PreselectRankStation(string address)
    {
        foreach (var c in RankChoices)
            if (string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                SelectedRankStation = c;
                return;
            }
    }

    /// <summary>The operator LANDED on the LQA sub-tab (§4's editors-read-fresh
    /// tier): re-read the schedule queue, EVERY landing, with the one bare
    /// <c>EXCH</c>. The read fires only from this landing — round-9 doctrine
    /// forbids reading from a tab the operator is not on.</summary>
    public void OnLqaTabOpened()
    {
        if (!AleReady) return;
        // D1 QUIESCE (§4 SUPPRESSION SCOPE — tab opens defer too): the landing
        // stands, the read is owed to the campaign's end.
        if (_campaign?.CampaignActive == true) { _schedulesReadOwed = true; return; }
        _ale.RequestLqaSchedules();
    }

    /// <summary>Settle the deferred schedule read, once, and ONLY while this tab
    /// can read — otherwise it stays owed and the next ALE confirmation pays it
    /// (audit round 1). Called from <see cref="Rebuild"/>, this tab's
    /// every-event recompute, which is also what re-runs the channel fan-out
    /// for every score the deferral deliberately left out of
    /// <c>_channelReadsThisSession</c>.</summary>
    private void PayWhatIsOwed()
    {
        if (!_schedulesReadOwed || !AleReady) return;
        if (_campaign?.CampaignActive == true) return;
        _schedulesReadOwed = false;
        _ale.RequestLqaSchedules();
    }

    partial void OnSelectedRankStationChanged(AleTargetChoice? value)
    { if (!_refreshing) Rebuild(); }
    partial void OnSelectedExchTargetChanged(AleTargetChoice? value)
    { if (!_refreshing) Rebuild(); }
    partial void OnSelectedSouSelfChanged(AleTargetChoice? value)
    { if (!_refreshing) Rebuild(); }

    /// <summary>Re-project everything this tab displays and re-evaluate every
    /// command. NAMED <c>Rebuild</c> rather than <c>Refresh</c> since round 15
    /// F-5: <c>Refresh</c> is now the OPERATOR's Refresh-LQA command, and the
    /// [RelayCommand] generator takes the command's name from its method.</summary>
    private void Rebuild()
    {
        // Whatever a campaign deferred, settled on the same recompute that
        // renders everything else — before the projection, so an answer that
        // lands during it re-renders normally.
        PayWhatIsOwed();
        ConsumeHeard();

        IReadOnlyList<LqaScore> report;
        Dictionary<int, StoredChannel> mirror;

        _refreshing = true;
        try
        {
            // RAN takes an INDIVIDUAL; SOU takes a SELF; EXCH takes any
            // address (flat list) — the radio's own type restrictions.
            RankChoices = RebuildChoices(RankChoices, individuals: true, nets: false, selfs: false);
            SelectedRankStation = Reselect(RankChoices, SelectedRankStation);
            ExchChoices = RebuildChoices(ExchChoices, individuals: true, nets: true, selfs: false);
            SelectedExchTarget = Reselect(ExchChoices, SelectedExchTarget);
            SouChoices = RebuildChoices(SouChoices, individuals: false, nets: false, selfs: true);
            SelectedSouSelf = Reselect(SouChoices, SelectedSouSelf);

            report = _ale.LqaReport;
            mirror = ChannelMirror();
            var rows = new List<LqaReportRowViewModel>(report.Count);
            foreach (var s in report) rows.Add(BuildReportRow(s, mirror));
            ReportRows = rows;
            ReportDisplayRows = rows.Count > 0 ? rows : [LqaReportRowViewModel.Placeholder];

            // The schedule MIRROR: null = never read/invalidated, [] = the
            // radio's own NO LQA SCHEDULED. §4 renders both as the ONE hyphen
            // row — but the CAPACITY gate reads the mirror, where the two
            // states agree anyway (nothing queued).
            var schedules = _ale.LqaSchedules;
            var scheduleRows = new List<LqaScheduleRowViewModel>(schedules?.Count ?? 0);
            if (schedules is not null)
                foreach (var s in schedules)
                    scheduleRows.Add(new LqaScheduleRowViewModel(
                        s.Kind == LqaScheduleKind.Exchange ? "EXCH" : "SOU",
                        s.Address, s.Interval, s.StartTime, DeleteRow));
            ScheduleRows = scheduleRows;
            ScheduleDisplayRows = scheduleRows.Count > 0
                ? scheduleRows
                : [LqaScheduleRowViewModel.Placeholder];
            _mirroredScheduleCount = schedules?.Count ?? 0;
            QueueFullReason = IsQueueFull ? "Queue full (10)" : "";

            // The latch releases on the completion of the read THIS Now
            // requested — answered or abandoned, both carry that id. Any OTHER
            // schedule read's completion (the tab's landing read, a Refresh)
            // carries a different id and leaves the button held.
            if (IsNowInFlight && _nowReadId != 0 && _ale.LastScheduleRead.ReadId == _nowReadId)
            {
                IsNowInFlight = false;
                _nowReadId = 0;
            }

            AreControlsEnabled = AleReady;
            DisabledReason =
                !Ready ? "Not connected — open Settings → Connection to connect."
                : !_ale.IsAleConfirmed ? "LQA commands are ALE-domain — waiting for the radio to confirm ALE."
                : "";

            RequestReportCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
            StartExchangeCommand.NotifyCanExecuteChanged();
            NowExchangeCommand.NotifyCanExecuteChanged();
            StartSoundingCommand.NotifyCanExecuteChanged();
            NowSoundingCommand.NotifyCanExecuteChanged();
        }
        finally { _refreshing = false; }

        // LAST, and outside the guard: this SENDS. Each targeted DI answer
        // re-enters through channels.Changed, and by then the channel is in
        // the mirror and in the session set, so the fan-out cannot recur.
        RequestMissingChannelReads(report, mirror);
    }

    /// <summary>The keyed channel mirror as a lookup — round 11 §8 made a
    /// targeted <c>DI n n</c> answer UPSERT its channel instead of clearing
    /// the list, so sequential targeted reads accumulate here.</summary>
    private Dictionary<int, StoredChannel> ChannelMirror()
    {
        var mirror = new Dictionary<int, StoredChannel>();
        foreach (var c in _channels.Channels) mirror[c.Number] = c;
        return mirror;
    }

    private static LqaReportRowViewModel BuildReportRow(
        LqaScore score, IReadOnlyDictionary<int, StoredChannel> mirror)
    {
        string rx = "—", tx = "—";
        if (TryChannelNumber(score.Channel, out int number)
            && mirror.TryGetValue(number, out var stored))
        {
            rx = SsbChannelEditorViewModel.FrequencyDisplay(stored.RxFrequency);
            tx = SsbChannelEditorViewModel.FrequencyDisplay(stored.TxFrequency);
        }
        return new LqaReportRowViewModel(
            score.Channel, score.Score, score.MeasuredSnr, score.ReceivedSnr, rx, tx);
    }

    /// <summary>R5, exactly: on each report landing, for each NAMED channel not
    /// already in the keyed mirror this session, send ONE <c>DI n n</c> through
    /// the existing targeted builder. The session set is what makes it once —
    /// a report that re-lands with the same channels sends nothing.</summary>
    private void RequestMissingChannelReads(
        IReadOnlyList<LqaScore> report, IReadOnlyDictionary<int, StoredChannel> mirror)
    {
        if (!AleReady) return;
        // D1 QUIESCE: a clone campaign owns the wire. Nothing is added to
        // `_channelReadsThisSession`, so every missing channel stays owed and
        // the campaign-end Rebuild re-enters here and asks for them once.
        if (_campaign?.CampaignActive == true) return;
        foreach (var score in report)
        {
            if (!TryChannelNumber(score.Channel, out int number)) continue;
            if (mirror.ContainsKey(number)) continue;             // already known
            if (!_channelReadsThisSession.Add(number)) continue;  // already asked
            _channels.RequestChannel(number);
        }
    }

    private static bool TryChannelNumber(string text, out int number)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            && number is >= 0 and <= 99;

    private IReadOnlyList<AleTargetChoice> RebuildChoices(
        IReadOnlyList<AleTargetChoice> current,
        bool individuals, bool nets, bool selfs)
    {
        var choices = new List<AleTargetChoice>();
        if (individuals)
            foreach (var a in _ale.IndividualAddresses) choices.Add(new AleTargetChoice(a.Address, "IND"));
        if (nets)
            foreach (var a in _ale.NetAddresses) choices.Add(new AleTargetChoice(a.Address, "NET"));
        if (selfs)
            foreach (var a in _ale.SelfAddresses) choices.Add(new AleTargetChoice(a.Address, "SELF"));

        bool changed = choices.Count != current.Count;
        if (!changed)
            for (int i = 0; i < choices.Count; i++)
                if (choices[i].Address != current[i].Address) { changed = true; break; }
        return changed ? choices : current;
    }

    /// <summary>Keep a selection across a list rebuild if the address is
    /// still present (assignments run under the _refreshing guard).</summary>
    private static AleTargetChoice? Reselect(IReadOnlyList<AleTargetChoice> choices, AleTargetChoice? selected)
    {
        if (selected is null) return null;
        foreach (var c in choices)
            if (c.Address == selected.Address) return c;
        return null;
    }

    // ---- The schedule mirror's own Refresh (§16 F-5) --------------------------

    /// <summary>Re-read the QUEUED SCHEDULES: one bare <c>EXCH</c> and nothing
    /// else. It is deliberately NOT the RANK report — that has its own per-
    /// station Read button — and it does not re-poll the pickers: the answer's
    /// mirror change re-renders the rows through the existing subscription.
    ///
    /// <para>A press during a Now in flight is ALLOWED: this is the same read
    /// the latch is waiting on, and Core's single outstanding queue serialises
    /// them (a request made while one is active coalesces).</para></summary>
    [RelayCommand(CanExecute = nameof(AreControlsEnabled))]
    private void Refresh()
    {
        if (!AleReady) return;           // the body repeats its own gate
        // D1 QUIESCE (§4 SUPPRESSION SCOPE): the press is ACCEPTED — the button
        // never greys — and the read runs at the campaign's end.
        if (_campaign?.CampaignActive == true) { _schedulesReadOwed = true; return; }
        _ale.RequestLqaSchedules();
    }

    // ---- RAN report (passive read) -------------------------------------------

    private bool CanRequestReport() => AreControlsEnabled && SelectedRankStation is not null;

    [RelayCommand(CanExecute = nameof(CanRequestReport))]
    private void RequestReport()
    {
        if (!AleReady || SelectedRankStation is null) return;
        _ale.RequestRank(SelectedRankStation.Address);
    }

    // ---- Scheduling (radio mirror) -------------------------------------------

    /// <summary>hh:mm or blank (omitted). Shape AND range (audit round 1,
    /// F3): EXCH/SOU answer NOTHING on the wire (Stage 6 wire fact), so
    /// client-side validation is the only defense — 99:99 must never reach
    /// the radio. Core's builders repeat the same check (defense in depth).</summary>
    private static bool TryHhMm(string text, string what, out string? value, out string error)
    {
        value = null;
        error = "";
        var t = text.Trim();
        if (t.Length == 0) return true;                 // optional — omitted
        // Owner 2026-08-30: four digits without the colon are accepted and
        // normalized ("0130" → "01:30") — the range check below still rules,
        // so "9999" is refused as ever. Exactly four digits, nothing shorter:
        // "130" stays ambiguous (1:30 or 13:0?) and stays refused.
        if (t.Length == 4 && t.All(char.IsAsciiDigit)) t = t[..2] + ":" + t[2..];
        if (!HhMm.IsMatch(t))
        {
            error = $"{what} must be hh:mm (e.g. 00:30) or blank.";
            return false;
        }
        int hours = int.Parse(t[..2], CultureInfo.InvariantCulture);
        int minutes = int.Parse(t[3..], CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59)
        {
            error = $"{what} must be within 00:00-23:59.";
            return false;
        }
        value = t;
        return true;
    }

    /// <summary>An INTERVAL carries the same hh:mm bounds plus §4's floor: a
    /// zero repeat period is not a schedule, and the radio does not validate
    /// intervals at all (2026-08-17) — it would simply store 00:00.</summary>
    private static bool TryInterval(string text, out string? value, out string error)
    {
        if (!TryHhMm(text, "Interval", out value, out error)) return false;
        if (value == "00:00")
        {
            value = null;
            error = "Interval must be at least 00:01.";
            return false;
        }
        return true;
    }

    /// <summary>Per-row Delete: sends <c>STO</c> against the ROW's OWN captured
    /// kind and address (never a picker selection), then re-reads the queue.
    /// UNCONFIRMED by decision — the per-row Removes precedent. This is the
    /// ONLY <c>STO</c> the app sends now (§16 F-3): the compose rows lost
    /// theirs, because the row that owns a schedule is what removes it.</summary>
    private void DeleteRow(LqaScheduleRowViewModel row)
    {
        if (!AleReady) return;
        if (row.KindText == "EXCH") _ale.StopExchange(row.Address);
        else _ale.StopSounding(row.Address);
        _ale.RequestLqaSchedules();
        Rebuild();
    }

    // ---- Schedule (STA with an interval) and Now (the bare STA) ---------------
    // §16 F-2's truth table, and the ONE place each term appears:
    //   Schedule ⇔ controls enabled ∧ target picked ∧ ¬queue full ∧ ¬on air
    //   Now      ⇔ controls enabled ∧ target picked ∧ ¬in flight  ∧ ¬on air
    // Queue-full is NOT a Now term: the bare STA writes no row (P14 run 1), so
    // a full queue cannot refuse it. Already-queued is not a term either — the
    // radio accepted it silently. What the radio DOES refuse (` … CHANS REQD `,
    // ` INV ADDRESS TYPE `) surfaces through the existing row-error path.

    private bool CanStartExchange()
        => AreControlsEnabled && SelectedExchTarget is not null && !IsQueueFull && !IsOnAir;

    private bool CanNowExchange()
        => AreControlsEnabled && SelectedExchTarget is not null && !IsNowInFlight && !IsOnAir;

    [RelayCommand(CanExecute = nameof(CanStartExchange))]
    private void StartExchange()
    {
        if (!AleReady || SelectedExchTarget is null || IsQueueFull || IsOnAir) return;
        if (!TryInterval(ExchIntervalText, out var interval, out var e1)) { ExchError = e1; return; }
        if (interval is null) { ExchError = IntervalRequired; return; }
        if (!TryHhMm(ExchStartText, "Start", out var start, out var e2)) { ExchError = e2; return; }
        ExchError = "";
        _ale.StartExchange(SelectedExchTarget.Address, interval, start);
        _ale.RequestLqaSchedules();      // freshness: an accepted STA re-reads
        Rebuild();
    }

    /// <summary>The BARE <c>EXCH STA &lt;addr&gt;</c> — a one-time, immediate
    /// LQA exchange. The two entries are IGNORED by design (this control is
    /// what "no interval, no start" means now), and the row's error is CLEARED
    /// first: a stale "Interval required" must not survive the deliberate
    /// press that answers it (critic F60).</summary>
    [RelayCommand(CanExecute = nameof(CanNowExchange))]
    private void NowExchange()
    {
        if (!AleReady || SelectedExchTarget is null || IsNowInFlight || IsOnAir) return;
        ExchError = "";
        _ale.StartExchange(SelectedExchTarget.Address, null, null);
        // The flag goes up FIRST and the id lands before the Rebuild that
        // reads it: the answer can arrive on the reader thread the instant the
        // read is dispatched, and that Rebuild is what releases the latch if
        // it already has (the release also requires a NON-ZERO id, so a
        // Rebuild racing in between cannot free the button against
        // LastScheduleRead's own zero).
        IsNowInFlight = true;
        _nowReadId = _ale.RequestLqaSchedules();
        Rebuild();
    }

    private bool CanStartSounding()
        => AreControlsEnabled && SelectedSouSelf is not null && !IsQueueFull && !IsOnAir;

    private bool CanNowSounding()
        => AreControlsEnabled && SelectedSouSelf is not null && !IsNowInFlight && !IsOnAir;

    [RelayCommand(CanExecute = nameof(CanStartSounding))]
    private void StartSounding()
    {
        if (!AleReady || SelectedSouSelf is null || IsQueueFull || IsOnAir) return;
        if (!TryInterval(SouIntervalText, out var interval, out var e1)) { SouError = e1; return; }
        if (interval is null) { SouError = IntervalRequired; return; }
        if (!TryHhMm(SouStartText, "Start", out var start, out var e2)) { SouError = e2; return; }
        SouError = "";
        _ale.StartSounding(SelectedSouSelf.Address, interval, start);
        _ale.RequestLqaSchedules();
        Rebuild();
    }

    /// <summary>The BARE <c>SOU STA &lt;self&gt;</c> — sound now, once. Same
    /// shape as <see cref="NowExchange"/>: clear the error, send the bare form
    /// ignoring the entries, re-read, and latch until THAT re-read lands.</summary>
    [RelayCommand(CanExecute = nameof(CanNowSounding))]
    private void NowSounding()
    {
        if (!AleReady || SelectedSouSelf is null || IsNowInFlight || IsOnAir) return;
        SouError = "";
        _ale.StartSounding(SelectedSouSelf.Address, null, null);
        IsNowInFlight = true;
        _nowReadId = _ale.RequestLqaSchedules();
        Rebuild();
    }
}
