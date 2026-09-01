using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The SSB settings pane's "Channels" card (UI-tweaks round 4, §AK) — the
/// channel EDITOR, in the same two-sub-tab paradigm as the HOP net editor:
///
/// - <b>Channel programming</b> (LEFT, the DEFAULT): a channel PICKER
///   (00–99, two per-digit ▲/▼ spinners, wrapping — the Operate channel
///   card's idiom, as app-side view state), the picked channel's stored
///   READ-BACK, ONE set of edit controls for the stored six, and
///   <b>Store</b>.
/// - <b>Channel list</b> (RIGHT): all 100 channels, READ-ONLY, filled
///   LAZILY — a row's <c>DI n n</c> goes out at most once per session, when
///   the row comes into view (R4-Q2, owner: "agreed").
///
/// <para><b>Why this VM keeps its own row cache.</b>
/// <c>RadioState.ChannelList</c> holds RAW lines, and the row model this card
/// renders (typed fields, MHz text, dirty tracking) is the VM's own. Round 11
/// §8 made the Core mirror KEYED and accumulating — it no longer clears per
/// <c>DI</c> — so the cache is no longer compensating for a self-clearing
/// mirror; it stays because it is the app-side projection of what the radio
/// has reported THIS SESSION — the ALE
/// station-list precedent, and constitutional for the same reason: the cache
/// holds only lines the radio actually sent, is cleared on session drop, and
/// never invents a value.</para>
///
/// <para><b>Store (AK2) — the decided sequence, order-pinned.</b> Any channel
/// is writable: there is NO channel-write command on this radio, so
/// programming channel <i>n</i> means going to <i>n</i> and using the
/// ordinary live setters (confirmed from three sources, plan §AK). One press
/// sends, all Console-visible:
/// <code>
/// CH n                       (bare — the store excursion)
/// FR &lt;8d&gt;               when RX == TX, else RXF &lt;8d&gt; then TXF &lt;8d&gt;
/// MODE / BA / AG / RXON      the rest of the stored six
/// DI n n                     the read-back verify
/// CH &lt;found&gt; + SH        restore the operator's channel (ChannelSurface.Select)
/// </code>
/// <c>&lt;found&gt;</c> is the CONFIRMED current channel captured at the press,
/// which is why <b>Store is disabled while the current channel is
/// unconfirmed</b> — without a confirmed value there is nothing honest to
/// restore to, and the editor will not guess. The restore is unconditional
/// (even when found == target): the operator's radio ends the sequence
/// re-read, not left wherever the excursion put it.</para>
///
/// <para><b>Constitution.</b> Every read-back renders ONLY from <c>DI</c>
/// lines ("—" until then, fixed widths — rule K); the picker never sends
/// <c>CH n</c>; client-side validation blocks an invalid Store entirely (the
/// radio silently ignores malformed frequencies).</para>
///
/// <para><b>F6 is untouched.</b> The Operate pane's CH-00 lock stays exactly
/// as it is — that lock is an APP-SIDE policy protecting the operator's fill
/// mid-operation (R4-Q3), and this editor is its sanctioned bypass, on a
/// settings screen, behind an explicit Store.</para>
///
/// <para><b>UI tweaks round 5 (§BF) — the rework.</b> Three things changed and
/// the rest is carried verbatim:</para>
///
/// <list type="number">
///   <item><b>WIRE-READ gestures vs BUFFER-POPULATE gestures are now distinct
///     (BF3).</b> A fresh <c>DI n n</c> goes out on exactly three gestures —
///     every picker SPIN (<see cref="RequestChannelFresh"/>, UNCONDITIONAL:
///     on a programming surface staleness beats chattiness), the FIRST card
///     load of the session (<see cref="EnsureLoaded"/>) and "Refresh
///     channels". <b>Switching sub-tabs sends NOTHING</b> (the standing
///     tab-strip rule); returning to the programming tab re-populates the
///     buffers from the session cache instead. The once-per-session
///     <see cref="RequestChannelOnce"/> remains, for the LIST tab's lazy rows
///     only.</item>
///   <item><b>ONE read-back ROW replaces the six blue displays (BF2).</b> The
///     picked channel renders as an <see cref="SsbChannelRow"/> — the same
///     projection the list tab's rows use — so the two views of one channel
///     cannot disagree, and the row the operator is programming looks exactly
///     like the row they will see in the list.</item>
///   <item><b>Reported values live in the read-back row ONLY (round 8 EB,
///     owner).</b> The frequency ENTRIES are never written from a report, and
///     since round 8 their placeholders are plain FORMAT HINTS — the picked
///     channel's reported frequencies already render in the read-back row
///     beside the picker, and echoing them in the placeholders showed the
///     same value twice. The SEND-TIME rule is unchanged (round 7): an EMPTY
///     Rx at Store falls back to the picked channel's reported value; a blank
///     Tx means SAME AS RX always. A populate GESTURE (the sub-tab landing, a
///     picker spin, Refresh) CLEARS the entry text. The SEGMENTS keep the
///     round-5 per-segment dirty guard. Everywhere outside these two
///     surfaces, no-prefill stands.</item>
/// </list>
///
/// <para><b>Prefill honesty.</b> Frequencies prefill through the SAME
/// <see cref="FrequencyDisplay"/> the rows use, never the raw 8-digit Hz
/// string. Mode, bandwidth and receive-only map by DIRECT EQUALITY against
/// captured forms. AGC is the exception and is marked PROVISIONAL: the dump
/// prints its own abbreviations and only <c>SL</c>/<c>ME</c> are captured, so
/// the SEGMENT prefill goes through <see cref="Wire.ParseDumpAgc"/>'s
/// unique-prefix map and an unmatched token leaves the segment UNSELECTED —
/// which blocks Store until the operator picks one. The read-back ROW keeps
/// showing the dump's own text verbatim either way.</para>
/// </summary>
public partial class SsbChannelEditorViewModel : ObservableObject
{
    /// <summary>Channels are 00–99 (SsbController.ValidateChannel).</summary>
    public const int ChannelCount = 100;

    /// <summary>Lowest programmable frequency, MHz. Round 6 (CH, owner): the
    /// editor's unit is MHz, matching the Operate VFO readout, not kHz.
    /// <para>F5 (plan-clone-field-round2.md, decision D3): DERIVED from
    /// <see cref="Wire.MinFrequencyHz"/> rather than written out again — the
    /// band bound is radio-wide and has ONE definition. <c>static readonly</c>
    /// rather than <c>const</c> for exactly that reason: a const could not be
    /// computed from Core's number and would be a second copy.</para></summary>
    public static readonly decimal MinMHz = Wire.MinFrequencyHz / 1_000_000m;

    /// <summary>Highest programmable frequency, MHz — <see cref="Wire.MaxFrequencyHz"/>
    /// at the editor's 1 Hz resolution. MEASURED by probe P2 (transcript
    /// <c>bench/transcripts/p2-freq-range-20260821-175802.jsonl</c>); it read
    /// 29.999999 until this round, which is why a source radio's real 51.5 MHz
    /// channels could not be written.</summary>
    public static readonly decimal MaxMHz = Wire.MaxFrequencyHz / 1_000_000m;

    private readonly ChannelSurface _channel;
    private readonly SsbSurface _ssb;
    private readonly RadioSession _session;

    /// <summary>What the radio has told us about each channel THIS SESSION.
    /// Accumulated across dumps because the Core mirror self-clears.</summary>
    private readonly Dictionary<int, StoredChannel> _cache = [];

    /// <summary>Channels whose <c>DI n n</c> has already gone out this
    /// session (the HOP per-net idiom). Cleared with the session and by
    /// Refresh. Round 5: this is the LIST tab's lazy-once record only — the
    /// programming surface's reads are unconditional
    /// (<see cref="RequestChannelFresh"/>), which also marks this set so a row
    /// scrolled to later does not re-ask for a channel just read.</summary>
    private readonly HashSet<int> _queried = [];

    /// <summary>BF3's landing latch: the card's FIRST load this session reads
    /// the picked channel once, so the first navigation populates without a
    /// spin. The HopSettingsViewModel <c>_loadedThisSession</c> idiom.</summary>
    private bool _loadedThisSession;

    /// <summary>The list's last reported visible range, so Refresh re-reads
    /// what the operator is actually looking at. -1 = the list has never been
    /// shown.</summary>
    private int _visibleFirst = -1;
    private int _visibleLast = -1;

    public IReadOnlyList<SsbChannelRow> Rows { get; }

    // ---- Sub-tab view state (AK1) -----------------------------------------
    // App-side view state, the AleViewModel/HOP-editor idiom: switching sends
    // nothing. Programming is the DEFAULT, so this starts false.

    [ObservableProperty] private bool isListTabOpen;

    /// <summary>Returning to the programming tab is a POPULATE gesture and
    /// nothing more (BF3): the buffers are reset from the session cache /
    /// mirror and <b>no command goes on the wire</b> — the tab-strip rule is
    /// that switching a view never touches the radio. The data is already
    /// here; if it is stale, that is what Refresh and the picker are for.
    /// </summary>
    [RelayCommand]
    private void OpenProgrammingTab()
    {
        IsListTabOpen = false;
        BeginPopulateGesture();
        Refresh();
    }

    [RelayCommand] private void OpenListTab() => IsListTabOpen = true;

    // ---- Channel picker (AK1) ---------------------------------------------
    // Two per-digit spinners over 00–99, wrapping modulo 100 — the Operate
    // channel card's shape (F7/F7a), but here a pure APP-SIDE cursor: it says
    // which channel the operator is editing. It NEVER sends CH n; the only
    // thing a landing can send is the cheap read-only DI n n, once per
    // channel per session.

    private int _pickedChannel;
    public int PickedChannel => _pickedChannel;

    [ObservableProperty] private string pickedChannelText = "00";
    [ObservableProperty] private string pickedTensText = "0";
    [ObservableProperty] private string pickedUnitsText = "0";

    [RelayCommand] private void TensUp() => MovePicker(+10);
    [RelayCommand] private void TensDown() => MovePicker(-10);
    [RelayCommand] private void UnitsUp() => MovePicker(+1);
    [RelayCommand] private void UnitsDown() => MovePicker(-1);

    /// <summary>Move the cursor, re-render, and read the landed channel FRESH
    /// (BF3). The ONLY thing a landing puts on the wire is the cheap read-only
    /// <c>DI n n</c> — it NEVER sends <c>CH n</c>: moving the radio's channel
    /// is Store's job, behind an explicit press, and the picker is a view
    /// cursor.
    /// <para><b>Why unconditional (round 5).</b> Round 4 read each channel at
    /// most once per session. On a surface whose whole purpose is to PROGRAM a
    /// channel, a cached record can be older than the last write from any
    /// source — the front panel, another operator, this app's own Store — and
    /// the operator is about to edit from it. Staleness beats chattiness here:
    /// one short read per spin.</para>
    /// <para>The spin is also a POPULATE gesture (K5): it resets the buffers
    /// to what the radio has reported for the newly picked channel, so a
    /// half-typed entry never silently carries over onto a DIFFERENT
    /// channel.</para></summary>
    private void MovePicker(int delta)
    {
        _pickedChannel = (_pickedChannel + delta + ChannelCount) % ChannelCount;
        BeginPopulateGesture();
        Refresh();
        RequestChannelFresh(_pickedChannel);
    }

    // ---- Gate + notes -----------------------------------------------------

    [ObservableProperty] private bool areControlsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    private string disabledReason = "";

    public bool HasDisabledReason => !string.IsNullOrEmpty(DisabledReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    /// <summary>Why Store is unavailable while the rest of the card is live —
    /// most often "the radio has not reported which channel it is on", which
    /// is the AK2 restore precondition.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoreDisabledReason))]
    private string storeDisabledReason = "";

    public bool HasStoreDisabledReason => !string.IsNullOrEmpty(StoreDisabledReason);

    // ---- Edit buffers + the K5 dirty guard (round 5) -----------------------
    // Two-way is legal: these are OPERATOR input. Round 5's K5 carve-out lets
    // a CONFIRMED report INITIALIZE them on this programming surface — and
    // the six flags below are what keep that from ever overwriting the
    // operator: a buffer the operator has touched since the last populate
    // gesture is off limits to every subsequent report.
    //
    // Why a flag per buffer rather than one "editing" bit: the operator may
    // have retyped the frequency while leaving the AGC segment alone, and the
    // AGC segment should still follow the radio. Per-buffer is also what makes
    // the rule testable headlessly — no focus, no view.

    // Round 7 (DB, owner): the ENTRY dirty flags are GONE — reported
    // frequencies render as PLACEHOLDERS now, never as entered Text, so a
    // report cannot clobber typed text by construction. The flags below
    // remain for the SEGMENTS only (highlights have no placeholder concept).
    private bool _modulationDirty, _bandwidthDirty, _agcDirty, _rxOnlyDirty;

    /// <summary>True while THIS class is writing the buffers from a report, so
    /// the change hooks below can tell a populate from an operator edit. The
    /// hooks are the only writers of the dirty flags, which means every path
    /// that can set a buffer — including a future one — is covered.</summary>
    private bool _populating;

    // Every buffer change does two things: it records the edit for the K5
    // guard, and it re-evaluates Store's completeness gate (below). Both live
    // here so no path that writes a buffer can skip either.

    partial void OnRxFrequencyInputChanged(string value) => BufferChanged();
    partial void OnTxFrequencyInputChanged(string value) => BufferChanged();
    partial void OnSelectedModulationChanged(ModulationMode? value) { if (!_populating) _modulationDirty = true; BufferChanged(); }
    partial void OnSelectedBandwidthChanged(string? value) { if (!_populating) _bandwidthDirty = true; BufferChanged(); }
    partial void OnSelectedAgcChanged(AgcSpeed? value) { if (!_populating) _agcDirty = true; BufferChanged(); }
    partial void OnSelectedRxOnlyChanged(YesNo? value) { if (!_populating) _rxOnlyDirty = true; BufferChanged(); }

    /// <summary>Re-run the Store gate after a buffer moves. Kept off the
    /// <see cref="Refresh"/> path deliberately: a buffer can change without any
    /// radio traffic at all (the operator typing), and Store must grey and
    /// un-grey as they do it, not on the next report.</summary>
    private void BufferChanged()
    {
        StoreDisabledReason = ComputeStoreDisabledReason();
        StoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>K5's "populate gesture": the picker spin, the programming
    /// sub-tab landing and Refresh all declare that the operator is starting
    /// from the radio's values again, so every buffer becomes eligible for the
    /// next populate. It clears the RECORD of editing, not the buffers — the
    /// values themselves are replaced by <see cref="PopulateBuffers"/> on the
    /// following recompute, from the cache if it holds this channel and to
    /// EMPTY if it does not (an unreported channel prefills nothing; the
    /// previous channel's values must never linger under a new number).
    /// </summary>
    private void BeginPopulateGesture()
    {
        _modulationDirty = _bandwidthDirty = _agcDirty = _rxOnlyDirty = false;
        // Round 7 (DB): a populate gesture CLEARS the entry text — typed text
        // must never carry over onto a different channel (Store writes to
        // whatever the picker is on), and "start from the radio again" now
        // means empty boxes over value-bearing placeholders.
        _populating = true;
        try { RxFrequencyInput = ""; TxFrequencyInput = ""; }
        finally { _populating = false; }
    }

    [ObservableProperty] private string rxFrequencyInput = "";
    [ObservableProperty] private string txFrequencyInput = "";

    // ---- Round 7 (DB) / round 8 (EB): the send-time fallback --------------
    // The Text buffer is ALWAYS the operator's, and the placeholders are
    // plain format hints (XAML constants) — the reported frequencies render
    // in the read-back row beside the picker, nowhere else (EB, owner: no
    // duplicated display). At send time an EMPTY Rx falls back to the
    // reported value below — except Tx, whose blank means SAME AS RX always
    // (the round-6 owner rule; a simplex retune stays simplex).

    /// <summary>The reported 8-digit-Hz value the empty-field fallback may
    /// send, or null when the picked channel is unreported or its record
    /// does not parse (an X-form frequency backs nothing).</summary>
    private static string? BackingHz(StoredChannel? picked, bool rx)
    {
        var raw = rx ? picked?.RxFrequency : picked?.TxFrequency;
        return raw is not null
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? raw : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModulationChoices))]
    [NotifyPropertyChangedFor(nameof(BandwidthChoices))]
    private ModulationMode? selectedModulation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandwidthChoices))]
    private string? selectedBandwidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgcChoices))]
    private AgcSpeed? selectedAgc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxOnlyChoices))]
    private YesNo? selectedRxOnly;

    /// <summary>The five modulations, highlighted from the buffer — which
    /// under K5 STARTS at the radio's reported MODE for the picked channel and
    /// then belongs to the operator. What the radio actually said is always
    /// visible, unedited, on <see cref="ReadBackRow"/>.</summary>
    public IReadOnlyList<ChoiceItem> ModulationChoices =>
    [
        .. new[]
        {
            ModulationMode.Usb, ModulationMode.Lsb, ModulationMode.Ame,
            ModulationMode.Cw, ModulationMode.Fm,
        }
        .Select(m => new ChoiceItem(m.ToWire(), SelectedModulation == m, PickModulation)),
    ];

    /// <summary>The MEASURED per-modulation bandwidth set (probe R5) keyed to
    /// the PENDING modulation. Round 6 (CK, owner): the row is ALWAYS
    /// populated — with no modulation chosen it shows the USB/LSB set (the
    /// radio's most common), so the buttons never vanish mid-edit.</summary>
    public IReadOnlyList<ChoiceItem> BandwidthChoices =>
        [.. Wire.AllowedBandwidths(SelectedModulation ?? ModulationMode.Usb)
            .Select(b => new ChoiceItem(b, SelectedBandwidth == b, PickBandwidth))];

    /// <summary>CK's default pick for a bandwidth set: the radio's own USB
    /// default 2.7 when the set carries it, else the set's first entry. An
    /// app-side INPUT default (A2) — it never renders as radio state; the
    /// read-back row keeps showing what the radio actually said.</summary>
    internal static string DefaultBandwidth(ModulationMode modulation)
    {
        var set = Wire.AllowedBandwidths(modulation);
        return set.Contains("2.7") ? "2.7" : set[0];
    }

    public IReadOnlyList<ChoiceItem> AgcChoices =>
    [
        .. new[]
        {
            AgcSpeed.Off, AgcSpeed.Slow, AgcSpeed.Medium, AgcSpeed.Fast, AgcSpeed.Data,
        }
        .Select(a => new ChoiceItem(a.ToWire(), SelectedAgc == a, PickAgc)),
    ];

    public IReadOnlyList<ChoiceItem> RxOnlyChoices =>
    [
        .. new[] { YesNo.Yes, YesNo.No }
            .Select(v => new ChoiceItem(v.ToWire(), SelectedRxOnly == v, PickRxOnly)),
    ];

    private void PickModulation(string wire)
    {
        SelectedModulation = Wire.ParseModulation(wire);
        // A modulation change can invalidate the pending bandwidth: swap it
        // for the NEW set's default (CK — the row always has a pick) rather
        // than send a value this modulation does not accept.
        if (SelectedModulation is { } m && !Wire.AllowedBandwidths(m).Contains(SelectedBandwidth ?? ""))
            SelectedBandwidth = DefaultBandwidth(m);
    }

    private void PickBandwidth(string value) => SelectedBandwidth = value;
    private void PickAgc(string wire) => SelectedAgc = Wire.ParseAgcSpeed(wire);
    private void PickRxOnly(string wire) => SelectedRxOnly = Wire.ParseYesNo(wire);

    // ---- The PICKED channel's read-back — DI lines only, "—" until then ---

    /// <summary>
    /// BF2: <b>ONE read-back row, shaped exactly like a row out of the channel
    /// list</b> — the same <see cref="SsbChannelRow"/> projection, so the
    /// headed cells and the AGC / receive-only second line are literally the
    /// same rendering the list tab shows (owner: "display a single row that
    /// looks like a row out of the channel list"). It REPLACES round 4's six
    /// blue Option-B displays, which said the same six things in a vocabulary
    /// nothing else on the pane used.
    /// <para>The row is REPLACED, not mutated, when the picker moves, because
    /// a row's number is part of its identity — a row must never render one
    /// channel's number over another channel's values. Its cells read "—"
    /// until a <c>DI</c> line for that channel has arrived this session, and
    /// they show the radio's own words (the dump's <c>SL</c> stays
    /// <c>SL</c>).</para>
    /// </summary>
    [ObservableProperty] private SsbChannelRow readBackRow = new(0);

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 12). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>A FRESH read (picker spin, first load, Refresh press) deferred
    /// to the campaign's end, and the channel it wanted. Last one wins — the
    /// operator's most recent gesture is the one the wire should answer.</summary>
    private bool _freshReadOwed;

    private int _owedFreshChannel;

    /// <summary>A LAZY list-row read deferred to the campaign's end. The rows
    /// were never added to <c>_queried</c>, so re-running the remembered visible
    /// range asks for exactly the ones still owed.</summary>
    private bool _onceReadOwed;

    public SsbChannelEditorViewModel(
        ChannelSurface channel, SsbSurface ssb, RadioSession session,
        ICampaignSignal? campaign = null)
    {
        _channel = channel;
        _ssb = ssb;
        _session = session;
        _campaign = campaign;
        Rows = [.. Enumerable.Range(0, ChannelCount).Select(n => new SsbChannelRow(n))];

        // The campaign's END edge runs the recompute; Refresh settles what is
        // owed if this card can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        channel.Changed += (_, _) => Refresh();
        ssb.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            // A dropped session forgets everything the radio told us: the next
            // Ready session may be a different radio. The operator's typed
            // buffers are deliberately NOT cleared — they are the operator's.
            if (_session.Phase != SessionPhase.Ready)
            {
                _cache.Clear();
                _queried.Clear();
                _loadedThisSession = false;
                // Session-scoped: reads deferred for a radio that has gone are
                // not owed to the next one.
                _freshReadOwed = false;
                _onceReadOwed = false;
                _visibleFirst = _visibleLast = -1;
                InputError = "";
            }
            Refresh();
        };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool SsbReady => Ready && _ssb.IsSsbConfirmed;

    /// <summary>AK2's restore precondition: the editor must know which channel
    /// the operator is on before it moves off it.</summary>
    private bool CurrentKnown => _channel.Current.IsConfirmed;

    private void Refresh()
    {
        // Accumulate whatever the radio has reported. Round 11 §8: the mirror
        // is KEYED and no longer self-clears per DI, so this MERGE is now
        // belt-and-braces rather than load-bearing — and the Refresh gesture
        // clears BOTH sides explicitly.
        if (Ready)
            foreach (var reported in _channel.Channels)
                _cache[reported.Number] = reported;

        // BF3's landing mechanism, re-attempted on every recompute: this is
        // the trigger that fires when the card was constructed BEFORE the
        // radio confirmed SSB (its Loaded hook has already been and gone), so
        // the first load still happens the moment the gate opens.
        EnsureLoaded();

        // …and whatever a campaign deferred, settled on the same recompute.
        PayWhatIsOwed();

        AreControlsEnabled = SsbReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed ? "Channel programming is SSB-scoped — waiting for the radio to confirm SSB."
            : "";

        StoreDisabledReason = ComputeStoreDisabledReason();

        foreach (var row in Rows)
            row.Apply(_cache.TryGetValue(row.Number, out var c) ? c : null);

        PickedChannelText = _pickedChannel.ToString("00", CultureInfo.InvariantCulture);
        PickedTensText = (_pickedChannel / 10).ToString(CultureInfo.InvariantCulture);
        PickedUnitsText = (_pickedChannel % 10).ToString(CultureInfo.InvariantCulture);

        var picked = _cache.TryGetValue(_pickedChannel, out var p) ? p : null;

        // BF2: the read-back row. Replaced (not mutated) when the picker moves,
        // so its number and its values are always the same channel's.
        if (ReadBackRow.Number != _pickedChannel) ReadBackRow = new SsbChannelRow(_pickedChannel);
        ReadBackRow.Apply(picked);

        PopulateBuffers(picked);

        StoreCommand.NotifyCanExecuteChanged();
        RefreshChannelsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// K5's populate: initialize the edit buffers from the picked channel's
    /// CONFIRMED record, skipping every buffer the operator has modified since
    /// the last populate gesture. Called on every recompute, which is what
    /// makes "a report for the picked channel populates" true whenever the
    /// report arrives — landing, spin, Refresh or the Store verify.
    ///
    /// <para><b>An unreported channel populates NOTHING</b> (empty entries, no
    /// segment selected) rather than leaving the previous channel's values
    /// under the new number. That is the constitution's second display state
    /// applied to an input: no value is a truthful starting point, a
    /// neighbour's value is not.</para>
    ///
    /// <para><b>Conversions.</b> Frequencies go through the SAME
    /// <see cref="FrequencyDisplay"/> the rows use (the mirror holds raw
    /// 8-digit Hz; the entries are kHz), so a prefilled entry round-trips back
    /// through <see cref="TryFrequency"/> to the Hz the radio reported. Mode
    /// and receive-only map by direct equality on captured forms. Bandwidth is
    /// taken only if the REPORTED modulation actually accepts it — a
    /// mismatched pair is the radio's to explain, and prefilling a bandwidth
    /// this modulation would refuse would arm a Store that cannot work. AGC is
    /// the PROVISIONAL prefix map (<see cref="Wire.ParseDumpAgc"/>).</para>
    /// </summary>
    private void PopulateBuffers(StoredChannel? picked)
    {
        // Round 7 (DB) / round 8 (EB): the frequency ENTRIES are never
        // written from a report, and the placeholders are constant hints —
        // the reported values render only in the read-back row. Nothing to
        // populate here for the frequencies; the fallback reads the cache at
        // Store time.

        _populating = true;
        try
        {
            var modulation = picked is null ? null : Wire.ParseModulation(picked.Mode.Trim().ToUpperInvariant());
            if (!_modulationDirty) SelectedModulation = modulation;

            if (!_bandwidthDirty)
                SelectedBandwidth =
                    picked is not null && modulation is { } m
                    && Wire.AllowedBandwidths(m).Contains(picked.Bandwidth.Trim())
                        ? picked.Bandwidth.Trim()
                        // CK (round 6): a populate that has no usable reported
                        // bandwidth still leaves a DEFAULT picked — the row is
                        // never selection-less. App-side input default (A2).
                        : DefaultBandwidth(modulation ?? ModulationMode.Usb);

            if (!_agcDirty) SelectedAgc = picked is null ? null : Wire.ParseDumpAgc(picked.Agc);
            if (!_rxOnlyDirty) SelectedRxOnly = picked is null ? null : Wire.ParseYesNo(picked.RxOnly.Trim().ToUpperInvariant());
        }
        finally
        {
            _populating = false;
        }
    }

    // The AGC SEGMENT PREFILL's map is DELETED here and lives once, as
    // `Wire.ParseDumpAgc` (plan-clone-field-round2.md F5, decision D3). The
    // clone campaign carried a SECOND, shorter copy that knew only `SL` and
    // `ME`; the field read of 2026-08-21 met a channel storing `FA` and
    // reported it as unwritable. What the editor's own contract adds is
    // unchanged and stated at the call sites: an unmatched token leaves the
    // segment UNSELECTED, so Store's all-six validation refuses until the
    // operator picks one — a wrong guess would silently write the wrong AGC to
    // a stored channel — while the read-back ROW still shows the dump's token
    // verbatim, always.

    // ---- Lazy per-channel read (R4-Q2) ------------------------------------

    /// <summary>
    /// The list's lazy read: send <c>DI n n</c> for this channel AT MOST ONCE
    /// per session. The view calls it as rows come into view; the cache — not
    /// the view's virtualization — is what makes it once-only, which is why
    /// the pins are written against this method.
    /// <para>Nothing is added to the queried set while the gate is shut, so a
    /// row scrolled past before the radio confirmed SSB still reads later.</para>
    /// </summary>
    public void RequestChannelOnce(int channel)
    {
        if (!SsbReady) return;
        if (channel is < 0 or >= ChannelCount) return;
        // D1 QUIESCE: a clone campaign owns the wire. NOTHING joins the queried
        // set, so every row scrolled past stays owed, and the campaign-end
        // handler re-runs the remembered visible range once.
        if (_campaign?.CampaignActive == true) { _onceReadOwed = true; return; }
        if (!_queried.Add(channel)) return;          // already asked this session
        _channel.RequestChannel(channel);
    }

    /// <summary>
    /// BF3's WIRE-READ: an UNCONDITIONAL <c>DI n n</c>, used by the three
    /// gestures that mean "tell me about this channel NOW" — every picker
    /// spin, the first card load (<see cref="EnsureLoaded"/>) and Refresh.
    /// Sub-tab switching deliberately does not call it.
    /// <para>It also marks the once-set, so a list row scrolled to afterwards
    /// does not re-ask for a channel this just read.</para>
    /// </summary>
    public void RequestChannelFresh(int channel)
    {
        if (!SsbReady) return;
        if (channel is < 0 or >= ChannelCount) return;
        // D1 QUIESCE (§4 SUPPRESSION SCOPE — spins, first load and the Refresh
        // press all defer): the gesture stands, the channel is remembered, and
        // one `DI n n` goes out when the campaign lets go of the wire.
        if (_campaign?.CampaignActive == true)
        {
            _freshReadOwed = true;
            _owedFreshChannel = channel;
            return;
        }
        _freshReadOwed = false;
        _queried.Add(channel);
        _channel.RequestChannel(channel);
    }

    /// <summary>Settle the deferred reads, once each, and ONLY while this card
    /// can read (audit round 1): `!SsbReady` leaves them OWED, because a
    /// campaign that ended in HOP must not consume a read this card cannot
    /// perform. Called from <see cref="Refresh"/>, the card's every-event
    /// recompute, so the next confirmed SSB pays them.</summary>
    private void PayWhatIsOwed()
    {
        if (_campaign?.CampaignActive == true || !SsbReady) return;

        if (_freshReadOwed) RequestChannelFresh(_owedFreshChannel);   // clears its own latch
        if (_onceReadOwed)
        {
            _onceReadOwed = false;
            if (_visibleFirst >= 0) RequestChannelRange(_visibleFirst, _visibleLast);
        }
    }

    /// <summary>
    /// BF3's LANDING mechanism: the first load of the Channels card this
    /// session reads the picked channel, so arriving on the pane populates the
    /// read-back row and the edit buffers without the operator having to spin
    /// the picker off and back.
    ///
    /// <para>Session-idempotent and gate-guarded (Ready + confirmed SSB), so
    /// the two triggers are safe to both fire: the card's own
    /// <c>Loaded</c> hook in code-behind (the round-4 DeviceClockView house
    /// pattern — the only trigger that fires when the DI singleton is
    /// constructed after the session is already up) and every recompute in
    /// <see cref="Refresh"/> (the HopSettingsViewModel idiom — the only one
    /// that fires when the card loaded BEFORE the radio confirmed SSB).</para>
    ///
    /// <para><b>It is not a populate GESTURE.</b> It performs the read; the
    /// buffers then fill through the ordinary un-dirty populate path. Making
    /// it clear the dirty flags would let a reconnect discard buffers the
    /// operator typed while disconnected — and those are deliberately theirs,
    /// not the radio's (pinned by
    /// <c>SessionDrop_ForgetsTheRadiosAnswers_ButNotTheOperatorsBuffers</c>).
    /// On a genuine first load nothing is dirty anyway, so the populate
    /// happens either way.</para>
    /// </summary>
    public void EnsureLoaded()
    {
        if (!SsbReady || _loadedThisSession) return;
        _loadedThisSession = true;
        RequestChannelFresh(_pickedChannel);
    }

    /// <summary>The visible-range form the list view calls on scroll: every
    /// channel in [first, last] gets its once-per-session read. The range is
    /// remembered so Refresh re-reads what is actually on screen.</summary>
    public void RequestChannelRange(int first, int last)
    {
        if (first < 0 || last < first) return;
        _visibleFirst = Math.Max(0, first);
        _visibleLast = Math.Min(ChannelCount - 1, last);
        for (int n = _visibleFirst; n <= _visibleLast; n++) RequestChannelOnce(n);
    }

    private bool CanRead() => SsbReady;

    /// <summary>
    /// Refresh (R4-Q2, owner: "clears + re-reads"): forget everything the
    /// radio said about channels this session and read again — the picked
    /// channel plus whatever the list is showing. Rows the operator has not
    /// looked at re-read lazily as they scroll back into view.
    /// <para>Deliberately DIFFERENT from the HOP settings pane, whose one
    /// <c>DIS</c> re-reads all ten at once: a hundred channels are a hundred
    /// commands, so this pane re-reads what is being looked at. Rows drop to
    /// "—" in between, which is the honest state — nothing has been reported
    /// since the clear.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private void RefreshChannels()
    {
        if (!SsbReady) return;
        // ROUND 11 §8: the Core mirror is KEYED and accumulates now, so the
        // clear that used to ride inside every DI has to be asked for. This IS
        // the "Refresh starts clean" gesture — without it the re-render below
        // would immediately re-absorb the previous session's rows into the
        // cache it just emptied.
        _channel.ForgetReportedChannels();
        _cache.Clear();
        _queried.Clear();
        // Refresh is a POPULATE gesture (K5): the operator asked to start from
        // the radio again, so the buffers become eligible — and, with the cache
        // just emptied, they blank until the answers arrive. Honest: nothing
        // has been reported since the clear.
        BeginPopulateGesture();
        // Re-read BEFORE re-rendering: the Core builder clears the transient
        // mirror as its first act, so issuing the reads first is what stops
        // this Refresh from immediately re-absorbing the PREVIOUS dump's lines
        // into the cache it just emptied.
        RequestChannelFresh(_pickedChannel);
        if (_visibleFirst >= 0)
            for (int n = _visibleFirst; n <= _visibleLast; n++) RequestChannelOnce(n);
        Refresh();
    }

    // ---- Store (AK2) -------------------------------------------------------

    /// <summary>
    /// Store's gate. Three preconditions, all of them things the operator can
    /// SEE rather than discover by pressing:
    ///
    /// <list type="number">
    ///   <item>the card's own gate (Ready + confirmed SSB);</item>
    ///   <item><b>AK2's restore precondition</b> — the radio must have said
    ///     which channel it is on, or there is nowhere honest to return the
    ///     operator to;</item>
    ///   <item><b>all six values present and valid</b> (round-5 audit fix).
    ///     Store writes the full set, so a blank buffer is not a partial write
    ///     — it is a write that cannot be made. The two states this really
    ///     guards are ordinary: an unmapped AGC token leaves that segment
    ///     unselected (the PROVISIONAL prefix map, by design), and Refresh
    ///     blanks every buffer until the answers land. Round 4 left Store
    ///     enabled through both and only complained on press.</item>
    /// </list>
    ///
    /// <para>Store's in-body validation is UNCHANGED and still runs: it
    /// produces the specific per-field InputError text, and
    /// <c>ICommand.Execute</c> ignores <c>CanExecute</c> anyway. This gate is
    /// the same rules read one step earlier, to grey the button rather than
    /// let it be pressed.</para>
    /// </summary>
    private bool CanStore() => SsbReady && CurrentKnown && IncompleteReason() is null;

    private string ComputeStoreDisabledReason()
        => !SsbReady ? ""                        // the whole card is greyed anyway
        : !CurrentKnown ? "Waiting for the radio to report its current channel — Store returns to it when the write is done."
        : IncompleteReason() ?? "";

    /// <summary>What is still missing before the stored six can be written, or
    /// null when nothing is. Checked in the SAME order as
    /// <see cref="Store"/>'s validation, so the greyed-button caption and the
    /// error an Execute would produce can never name different fields.</summary>
    /// <summary>Round 7 (DB): what Store would send for the receive
    /// frequency — the typed text when there is any, else the reported value
    /// backing the placeholder, else nothing (refuse).</summary>
    private bool TryResolveRx(out string hz, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(RxFrequencyInput))
            return TryFrequency(RxFrequencyInput, out hz, out error);
        var backing = BackingHz(_cache.TryGetValue(_pickedChannel, out var p) ? p : null, rx: true);
        hz = backing ?? "";
        error = backing is null
            ? "frequency is required — nothing typed and nothing reported to fall back to."
            : null;
        return backing is not null;
    }

    private string? IncompleteReason()
    {
        if (!TryResolveRx(out _, out _)) return "Store needs a receive frequency — type one (the radio has not reported this channel's).";
        // Round 6 (CH, owner): a BLANK transmit frequency means "same as
        // receive" — simplex, the FR path — even when a Tx is reported (the
        // round-7 fallback rule's ONE exception). Only a non-blank entry
        // must parse.
        if (!string.IsNullOrWhiteSpace(TxFrequencyInput)
            && !TryFrequency(TxFrequencyInput, out _, out _)) return "Store needs a valid transmit frequency in MHz (or blank = same as receive).";
        if (SelectedModulation is null) return "Store needs a modulation.";
        if (SelectedBandwidth is null) return "Store needs a bandwidth.";
        // R6 review MAJOR 1: non-null is not enough. A populate can move an
        // UNTOUCHED modulation under a DIRTY bandwidth (the operator's 2.7
        // surviving a CW report), leaving a pair the radio would silently
        // ignore — membership in the selected modulation's measured set is
        // part of "valid".
        if (!Wire.AllowedBandwidths(SelectedModulation.Value).Contains(SelectedBandwidth))
            return "Store needs a bandwidth this modulation accepts — pick one from the row.";
        if (SelectedAgc is null) return "Store needs an AGC speed — pick one (the radio's reported value could not be matched to a setting).";
        if (SelectedRxOnly is null) return "Store needs receive-only set to Yes or No.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanStore))]
    private void Store()
    {
        if (!SsbReady || !CurrentKnown) return;      // Execute ignores CanExecute

        int target = _pickedChannel;
        int found = _channel.Current.Value;          // captured BEFORE the excursion

        // Round 7 (DB): typed text wins; an empty Rx falls back to the
        // reported value backing its placeholder.
        if (!TryResolveRx(out string rxHz, out string? error))
        {
            Fail(target, "receive " + error);
            return;
        }
        // Round 6 (CH): blank TX = same as RX (simplex — the FR path below),
        // ALWAYS — the deliberate exception to the round-7 fallback rule.
        string txHz;
        if (string.IsNullOrWhiteSpace(TxFrequencyInput))
        {
            txHz = rxHz;
        }
        else if (!TryFrequency(TxFrequencyInput, out txHz, out error))
        {
            Fail(target, "transmit " + error);
            return;
        }
        if (SelectedModulation is not { } modulation)
        {
            Fail(target, "pick a modulation first.");
            return;
        }
        if (SelectedBandwidth is not { } bandwidth)
        {
            Fail(target, "pick a bandwidth first.");
            return;
        }
        if (!Wire.AllowedBandwidths(modulation).Contains(bandwidth))
        {
            Fail(target, $"bandwidth {bandwidth} is not valid for {modulation.ToWire()} — pick one from the row.");
            return;
        }
        if (SelectedAgc is not { } agc)
        {
            Fail(target, "pick an AGC speed first.");
            return;
        }
        if (SelectedRxOnly is not { } rxOnly)
        {
            Fail(target, "pick receive-only Yes or No first.");
            return;
        }

        InputError = "";

        // AK2, in this exact order. There is no channel-write command: the
        // editor goes TO the channel and uses the ordinary live setters.
        _channel.SelectForStore(target);                     // bare CH n
        if (rxHz == txHz)
        {
            _ssb.SetFrequency(rxHz);                         // FR — simplex
        }
        else
        {
            _ssb.SetRxFrequency(rxHz);                       // RXF then TXF — split
            _ssb.SetTxFrequency(txHz);
        }
        _ssb.SetModulation(modulation);                      // MODE
        _ssb.SetBandwidth(bandwidth);                        // BA
        _ssb.SetAgc(agc);                                    // AG
        _ssb.SetRxOnly(rxOnly);                              // RXON

        // Verify from the radio's own record, then put the operator back on
        // the channel they were using — Select's CH + SH is the honest full
        // re-read after the excursion.
        _channel.RequestChannel(target);                     // DI n n
        _channel.Select(found);                              // CH found + SH
    }

    private void Fail(int channel, string message)
        => InputError = $"CH {channel.ToString("00", CultureInfo.InvariantCulture)}: {message}";

    /// <summary>
    /// Round 6 (CH, owner — supersedes AK1a's kHz): frequencies are typed in
    /// <b>MHz</b> with up to SIX decimals — the radio's full 1 Hz resolution —
    /// in the Operate VFO's own vocabulary. The display's group space
    /// ("14.313 500") is accepted on input, as are the compact "14.3135" and
    /// "14.313500". Converted here to the 8-digit Hz wire form, so nothing
    /// malformed can reach the builder — or the radio, which SILENTLY IGNORES
    /// a wrong frequency format.
    ///
    /// <para><b>Locale (round-4 audit, MAJOR 1).</b> A localized numeric
    /// keyboard emits the CULTURE's decimal separator, so both the invariant
    /// dot and the comma (and the current culture's own separator) are
    /// accepted and normalized before the invariant parse — the SAME input
    /// means the same frequency in every locale.</para>
    /// </summary>
    internal static bool TryFrequency(string? input, out string hz, out string? error)
    {
        hz = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "frequency is required — MHz, e.g. 14.313 500.";
            return false;
        }
        // The display format's own group space ("14.313 500") must round-trip
        // back through entry — but ONLY that shape (R6 review MAJOR 2:
        // stripping every space parsed " 1 4" as 14 MHz, letting a paste or
        // keyboard typo program a valid-but-unintended frequency). An
        // internal space is legal exactly where the display puts one: a
        // single space splitting the six fractional digits 3+3.
        if (text.Contains(' '))
        {
            if (!IsDisplayGrouping(text))
            {
                error = $"frequency '{text}' has a space where none belongs — "
                      + "spaces are only legal as the display's own grouping, e.g. 14.313 500.";
                return false;
            }
            text = text.Replace(" ", "");
        }
        if (!TryNormalizeDecimal(text, out string normalized))
        {
            error = $"frequency '{text}' is not a number in MHz — digits and ONE "
                  + "decimal separator, with no thousands grouping.";
            return false;
        }
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal mhz))
        {
            error = $"frequency '{text}' is not a number in MHz.";
            return false;
        }
        if (decimal.Round(mhz, 6) != mhz)
        {
            error = $"frequency '{text}' is finer than 1 Hz — at most six decimals in MHz.";
            return false;
        }
        if (mhz < MinMHz || mhz > MaxMHz)
        {
            error = $"frequency '{text}' is outside {MinMHz.ToString("0.######", CultureInfo.InvariantCulture)}"
                  + $"-{MaxMHz.ToString("0.######", CultureInfo.InvariantCulture)} MHz.";
            return false;
        }

        long hertz = (long)(mhz * 1_000_000m);
        hz = hertz.ToString("00000000", CultureInfo.InvariantCulture);
        error = null;
        return true;
    }

    /// <summary>The one space-bearing shape entry accepts: the display's own
    /// grouping — digits, a decimal separator (dot or comma; the locale rule
    /// below owns which), exactly three digits, ONE space, exactly three
    /// digits ("14.313 500" / "14,313 500"). Anything else with a space is
    /// refused, never repaired.</summary>
    private static bool IsDisplayGrouping(string text)
    {
        int sep = text.IndexOfAny(['.', ',']);
        if (sep <= 0) return false;
        var fraction = text[(sep + 1)..];
        if (fraction.Length != 7 || fraction[3] != ' ') return false;
        return text[..sep].All(char.IsAsciiDigit)
            && fraction[..3].All(char.IsAsciiDigit)
            && fraction[4..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Rewrite a typed number to the invariant "digits[.digits]" form, or
    /// refuse it. Accepted separators: the invariant dot, the plain comma, and
    /// the current culture's own — a localized keyboard emits whichever the
    /// locale uses, and all of them mean "decimal point" to an operator typing
    /// a frequency.
    /// <para>REFUSED rather than guessed at: anything with TWO separators.
    /// "1.234,5" and "1,234.5" are the same number to different readers and
    /// "1.234.5" is thousands grouping — a frequency is not a spreadsheet
    /// cell, and mis-parsing one puts the radio on the wrong frequency, so the
    /// ambiguous forms get an InputError instead of a best guess.</para>
    /// </summary>
    private static bool TryNormalizeDecimal(string text, out string normalized)
    {
        normalized = "";

        var cultureSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var separators = new HashSet<char> { '.', ',' };
        if (cultureSeparator.Length == 1) separators.Add(cultureSeparator[0]);

        char used = '\0';
        foreach (char ch in text)
        {
            if (char.IsAsciiDigit(ch)) continue;
            if (!separators.Contains(ch)) return false;     // letters, signs, spaces, group marks
            if (used != '\0') return false;                 // a second separator: mixed or grouping
            used = ch;
        }

        normalized = used == '\0' ? text : text.Replace(used, '.');
        return true;
    }

    /// <summary>An 8-digit Hz record rendered in the editor's own MHz units,
    /// in the Operate VFO's grouping — decimal after the MHz digits, a space
    /// before the final Hz triplet: "14313500" → "14.313 500" (round 6, CG).
    /// Always six fractional digits in two groups of three, so every row and
    /// entry lines up. Anything unparseable shows verbatim — the radio's
    /// report is never prettified into a guess.</summary>
    internal static string FrequencyDisplay(string hz)
    {
        if (!long.TryParse(hz, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hertz))
            return hz;
        long mhz = hertz / 1_000_000;
        long kHzGroup = hertz / 1_000 % 1_000;
        long hzGroup = hertz % 1_000;
        return string.Create(CultureInfo.InvariantCulture,
            $"{mhz}.{kHzGroup:000} {hzGroup:000}");
    }
}

/// <summary>
/// One READ-ONLY row of the "Channel list" tab: the radio's own stored record
/// for one channel, in the headed-cell columns. It holds NO input buffers and
/// NO commands — the list is display only; all editing happens on the
/// programming tab through the picker. Every cell reads "—" until a
/// <c>DI</c> line for that channel has arrived this session.
/// </summary>
public partial class SsbChannelRow : ObservableObject
{
    public SsbChannelRow(int number)
    {
        Number = number;
        NumberText = number.ToString("00", CultureInfo.InvariantCulture);
    }

    public int Number { get; }
    public string NumberText { get; }

    [ObservableProperty] private string rxFrequencyText = "—";
    [ObservableProperty] private string txFrequencyText = "—";
    [ObservableProperty] private string modeText = "—";
    [ObservableProperty] private string bandwidthText = "—";
    [ObservableProperty] private string agcText = "—";
    [ObservableProperty] private string rxOnlyText = "—";

    /// <summary>Round 7 (DA, owner): AGC displays as the FULL WORD — the
    /// captions moved into column HEADERS, so the cells are bare values. The
    /// word comes through the same PROVISIONAL prefix map the segment
    /// prefill uses (the dump's abbreviations are only partly captured); an
    /// unmapped token falls back to the dump's own text VERBATIM — readable
    /// where possible, honest always. `AgcText` keeps the raw token for the
    /// tests and any consumer that needs the wire form.</summary>
    [ObservableProperty] private string agcWordText = "—";

    /// <summary>H1 display casing over the wire spelling ("SLOW" → "Slow").</summary>
    private static string AgcWord(string? dumpToken)
        => Wire.ParseDumpAgc(dumpToken) is { } speed
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                speed.ToWire().ToLowerInvariant())
            : dumpToken ?? "—";

    internal void Apply(StoredChannel? channel)
    {
        RxFrequencyText = channel is null ? "—" : SsbChannelEditorViewModel.FrequencyDisplay(channel.RxFrequency);
        TxFrequencyText = channel is null ? "—" : SsbChannelEditorViewModel.FrequencyDisplay(channel.TxFrequency);
        ModeText = channel?.Mode ?? "—";
        BandwidthText = channel?.Bandwidth ?? "—";
        AgcText = channel?.Agc ?? "—";
        RxOnlyText = channel?.RxOnly ?? "—";
        AgcWordText = channel is null ? "—" : AgcWord(channel.Agc);
    }
}
