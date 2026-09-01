using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The Radio settings "Modem presets" card — REBUILT in UI-tweaks round 9 on
/// the CHANNEL-EDITOR model (owner ruling 3), over the short-token wire
/// vocabulary (<see cref="ModemPresetVocabulary"/>).
///
/// <list type="bullet">
///   <item><b>Preset programming</b> (LEFT, the DEFAULT): the preset picker
///     (0-6 at an SSB or ALE prompt, 7-9 at a HOP one — F11) with the picked
///     preset's SINGLE LIST ROW beside
///     it — the same <see cref="ModemPresetRow"/> projection the list tab
///     renders, so two views of one preset cannot disagree (the BF2 contract)
///     — a Name entry, type-switched selections, the baud wheel, and ONE
///     Store. The wire takes a preset as ONE line, so the editor composes and
///     stores.</item>
///   <item><b>Preset list</b>: the stored presets as read-only rows — # and
///     Name parsed, Parameters VERBATIM.</item>
/// </list>
///
/// <para><b>Short tokens on the wire, words on screen (ruling 1).</b> Every
/// selection is stored as its WIRE token and rendered as its display word,
/// both from the one vocabulary class; a listing token that maps prefills the
/// selection, and one that does NOT leaves it empty (the AGC precedent) while
/// the read-back row shows the radio's own text verbatim.</para>
///
/// <para><b>Type-switched rows (ruling 2; round 11 §6 makes the maps
/// TYPE-SCOPED OFFERS).</b> Interleave renders only where the type really
/// takes it — 39-tone, and Serial below 4800 baud (VERIFIED 2026-08-16;
/// Serial at 4800 is the write-less <c>uncoded</c>) — and with the values that
/// type accepts, not all five. Mark/Space render at <c>fsk-v</c> ONLY (stored
/// on every FSK, displayed nowhere else, therefore unverifiable). The baud
/// wheel cycles the values the picked type really stores, because every
/// out-of-range baud is SILENTLY CLAMPED and the echo reports success either
/// way. A RE-SCOPED row does not clear its selection — Store refuses it by
/// name instead, because visibility is a rendering fact and a command is the
/// sending surface. CLONE ROUND 12 §9 A2 narrows that to a re-scope the
/// operator can still SEE: when the offer goes EMPTY the row disappears
/// entirely, so the pick and its dirty flag CLEAR (a pick cannot outlive its
/// offer); the Store refusal stays as the belt for a non-empty offer that does
/// not contain the pick.</para>
///
/// <para><b>READ PATH — round 11 §6, the two SOURCES.</b> FIELDS come from
/// TARGETED <c>MODEM PRE n</c> reads; ENABLED/DISABLED comes ONLY from the
/// sentinel-scoped bulk PRESENCE operation (the bulk listing names exactly the
/// enabled presets; the targeted read does not echo the state at all). So:</para>
/// <list type="bullet">
///   <item>The PRESET LIST tab is the LAZY tier: its once-per-session landing
///     runs the SEVEN-read BATCH (0-6, one operation, one sentinel) and then
///     queues the PRESENCE operation BEHIND it. Ordering is not a hope — the
///     §8 modem queue is single-slot, so presence cannot open its window until
///     the batch's sentinel has answered, which is what stops a targeted row
///     being counted as "listed by the bulk", i.e. as ENABLED.</item>
///   <item>The EDITOR is the fresh tier: the view's <see cref="EnsureLoaded"/>,
///     the Ready arrival under an open card, every picker spin and every
///     programming-tab landing send ONE targeted <c>MODEM PRE n</c> for the
///     PICKED preset. Seven reads to look at one preset was the compat seam's
///     cost, not the contract's.</item>
///   <item>CLONE ROUND 12 §9 A3: the PRESENCE operation is now whole-card and
///     runs ONCE per session behind its OWN gate, triggered by whichever tab
///     lands first — the editor's read-back row renders the state cell too, so
///     leaving presence to the list tab meant the person programming a preset
///     could never see whether the write took.</item>
///   <item>ROUND 13 B1 (item 7): that gate MOVED DOWN to
///     <see cref="ModemSurface.EnsurePresenceLoaded"/> — it is whole-SURFACE
///     now, because the operate wheel's disabled-preset skip needs the same
///     enabled set — and the presence store also PREFILLS the editor's
///     Enabled/Disabled segment. A prefill is a report, so only an operator
///     tap puts EN/DIS on the Store line; the wire is byte-identical to round
///     12's.</item>
/// </list>
/// <para>Reads and POPULATE GESTURES are orthogonal: a spin and a tab landing
/// clear typing, the Ready-arrival read does NOT (typing survives a
/// reconnect — the standing R7 pin). There is no Refresh button: every landing
/// re-reads.</para>
///
/// <para><b>After a write (round 11 §6, the round-10 verify doctrine).</b> The
/// Store echo is a read-back, but it is not the whole one: a silently clamped
/// baud and a state change are both invisible in it. So a sent Store re-reads
/// the written preset TARGETED, and a Store that carried EN/DIS additionally
/// re-runs the PRESENCE operation — the only thing that can tell the state
/// column what happened.</para>
///
/// <para><b>Gate: Ready AND a CONFIRMED MODE</b> (clone-field round 2 F11,
/// audit round 1 MAJOR 2). The card lives on the mode-free Radio settings page
/// — the radio-clock precedent, which is why the gate was Ready ALONE through
/// round 13 — but since F11 the card's SHAPE is a claim about which presets the
/// radio has, and that is a fact about the PROMPT. Unconfirmed it is dark, it
/// reads nothing and it refuses Store; the landing it owes is paid the moment
/// the radio names its mode. See <see cref="Editable"/>.</para>
/// </summary>
public partial class ModemPresetsViewModel : ObservableObject
{
    /// <summary>Presets 0-6 exist at an <c>SSB&gt;</c> or <c>ALE&gt;</c> prompt
    /// (<c>MODEM PRESET 7</c> answers <c>INVALID MODEM PRESET</c> there).
    /// <para>PROMPT-SCOPED since clone-field round 2 F11: at <c>HOP&gt;</c> the
    /// band is 7-9 instead. The live range is
    /// <see cref="ModemSurface.PresetRange"/>; this constant is the SSB/ALE
    /// count and is what the card's own SSB shape counts.</para></summary>
    public const int PresetCount = 7;

    private readonly ModemSurface _modem;
    private readonly RadioSession _session;

    /// <summary>
    /// CLONE-FIELD ROUND 2 F11 (owner ruling R-D, decision A-9) — <b>the card
    /// FOLLOWS THE CONFIRMED MODE</b>. Under a confirmed <c>HOP&gt;</c> it wears
    /// the HOP shape: presets 7-9, name / signalling / port / a three-value
    /// baud wheel, and NO type, interleave or mark-space rows at all (absent,
    /// not greyed — a HOP preset has no such fields, P5/P5b). Under SSB or ALE
    /// it is byte-for-byte the card it has always been.
    /// <para>The flag is what the markup's two field stacks bind to, and what
    /// every scope-sensitive branch below reads. It is false while the mode is
    /// UNCONFIRMED, matching the surface: a scope the radio has not reported is
    /// not one the card claims.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsbScope))]
    private bool isHopScope;

    /// <summary>The visible half of the pair (the house Has*/Is* idiom — this
    /// codebase carries no inverting converter).</summary>
    public bool IsSsbScope => !IsHopScope;

    /// <summary>The scope the editor last LANDED on — true HOP, false SSB/ALE,
    /// NULL while the mode is unconfirmed (audit round 1, MAJOR 2). A change
    /// re-lands the card (F11): the picked preset resets to the new band's
    /// first, dirty fields are discarded, and the new scope is read.</summary>
    private bool? _landedHopScope;

    /// <summary>
    /// A READY ARRIVAL that found the mode UNCONFIRMED owes a landing read, and
    /// pays it when the mode arrives (audit round 1, MAJOR 2). Without this the
    /// Ready-arrival landing would simply be LOST on every connect, because the
    /// prompt line that names the scope arrives after Ready on this transport.
    ///
    /// <para>It is what keeps ADOPTING a scope distinct from CHANGING one. A
    /// ViewModel constructed while the session is ALREADY Ready has had no
    /// landing gesture — the view's <c>Loaded</c> is its first, per the round-11
    /// doctrine — so adopting the confirmed scope in its constructor must not
    /// read. A genuine scope CHANGE always does.</para></summary>
    private bool _landingOwedOnModeConfirm;

    /// <summary>The PRESET LIST tab's lazy once-per-session gate — the SEVEN
    /// FIELD READS and nothing else. Round 11 §6 narrowed it to the LIST's own
    /// read; clone round 12 §9 A3 narrows it again, splitting the PRESENCE
    /// operation out into its own gate below (critic-12b F5: one shared flag
    /// either starves a later list landing or breaks once-per-session).</summary>
    private bool _listLoadedThisSession;

    // ROUND 13 B1: the PRESENCE operation's own once-per-session flag is GONE.
    // The gate moved DOWN to ModemSurface.EnsurePresenceLoaded and is driven by
    // the presence STATE itself — this card is no longer the only consumer (the
    // operate wheel's skip needs the same enabled set), and a flag private to
    // one ViewModel could not be shared without inventing a session seam.
    // "Once per session" is emergent there: the mirror resets to Unknown on
    // every connect. See that method for the coalesce/retry contract.

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 1). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>
    /// THE DEDICATED DEFERRED-LANDING LATCH (§4 per-producer correction, critic
    /// pass 2).
    ///
    /// <para><b>Why this card needs its own and no other producer does.</b> The
    /// scope-edge branch in <see cref="Refresh"/> COMMITS <c>_landedHopScope</c>
    /// and clears <c>_landingOwedOnModeConfirm</c> BEFORE it calls
    /// <see cref="ReadForLanding"/>. The edge is therefore already absorbed by
    /// the time the read is attempted, so the usual "leave the latch unset and
    /// let the campaign-end Refresh re-enter" trick cannot work here — re-running
    /// edge detection finds no edge. This latch records the DEFERRAL itself, and
    /// the campaign-end handler pays it by calling the read directly.</para>
    /// </summary>
    private bool _landingDeferredByCampaign;

    public ModemPresetsViewModel(
        ModemSurface modem, RadioSession session, ICampaignSignal? campaign = null)
    {
        _modem = modem;
        _session = session;
        _campaign = campaign;
        // The campaign's END edge runs the recompute; Refresh settles the
        // deferred landing IF the card can read now, and leaves it owed if it
        // cannot — the next mode confirmation then pays it.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        modem.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _listLoadedThisSession = false;
                // Session-scoped like everything beside it: the landing this
                // campaign deferred was for the radio that left.
                _landingDeferredByCampaign = false;
                InputError = "";
                // A dead session takes the scope with it (the mirrors reset on
                // the next connect), so the card owes its landing again.
                _landingOwedOnModeConfirm = true;
            }
            else
            {
                // The surface first becoming readable in a session IS a
                // landing (initial sight, and the reconnect after a drop) — it
                // reads, on whichever tab is open. It is NOT a populate
                // gesture: the operator's typing survives a reconnect (the
                // standing pin).
                //
                // AUDIT ROUND 1, MAJOR 2: it can only READ once the radio has
                // named its mode — the scope IS the mode. When Ready arrives
                // first (which is the ordinary order on this radio: the connect
                // ritual's prompt lands with the `SH` answer), the landing is
                // OWED and paid the moment the mode confirms. It is not lost
                // and it is not doubled.
                _landingOwedOnModeConfirm = true;
                ReadForLanding();
                if (Editable) _landingOwedOnModeConfirm = false;
            }
            Refresh();
        };
        BuildChoiceRows();
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>
    /// AUDIT ROUND 1, MAJOR 2 — <b>the card is gated on a CONFIRMED MODE, not
    /// on Ready alone</b>, and it is the plan's own F11 table saying so
    /// ("unconfirmed → wheel disabled with today's reason").
    ///
    /// <para>The card used to enable on Ready and read the scope through
    /// <see cref="ModemSurface.PresetRange"/>, which fell back to the SSB/ALE
    /// band while the mode was unconfirmed (that fallback is GONE from Core
    /// too since audit round 2 — the family refuses instead). With it, between
    /// Ready and the first prompt line — and again while a session drop has
    /// the mirror unconfirmed — the card would
    /// wear the SSB shape and send `MODEM PRE 0` or a TYPE-bearing Store at
    /// whatever prompt the radio is really at, drawing `INVALID MODEM PRESET`
    /// or `** ERROR **`. A shape is a claim about the radio; the card does not
    /// make one it has not been told.</para>
    ///
    /// <para>Every path is gated on this: enablement, <c>CanStore</c>, both
    /// landing reads, the picker and Store itself. AUDIT ROUND 2 finished the
    /// job underneath: Core has NO unconfirmed fallback left either —
    /// <c>SsbController</c> REFUSES the whole preset family while the mode is
    /// unconfirmed, so an unscoped preset command is not something any caller
    /// can make, here or anywhere else.</para></summary>
    private bool Editable => Ready && _modem.ConfirmedMode is not null;

    /// <summary>The scope the card may act in, or null while unconfirmed.</summary>
    private (int First, int Last)? ScopeRange
        => _modem.ConfirmedMode is null ? null : _modem.PresetRange;

    /// <summary>The reason the controls are dark. Not-connected keeps today's
    /// sentence; the new one names the OTHER thing the card needs, in prose
    /// (I-5: the operator never meets a prompt token).</summary>
    public const string ModeUnconfirmedReason =
        "Waiting for the radio to report its mode — the presets it has depend on it.";

    // ---- The read path (the unified doctrine) -----------------------------

    /// <summary>A landing on whichever tab is open — the two tiers read
    /// different things (§6), so "the card was landed on" cannot be one
    /// read.</summary>
    private void ReadForLanding()
    {
        // D1 QUIESCE: a clone campaign owns the wire. BOTH tiers funnel through
        // here — the editor's targeted `MODEM PRE n`, the list's seven-preset
        // batch, and the presence operation behind either — so this one check
        // covers every autonomous read this card can issue.
        if (_campaign?.CampaignActive == true) { _landingDeferredByCampaign = true; return; }
        // …and the debt is CLEARED only where the card's own gate lets it read
        // (audit round 1). A campaign can end with no mode confirmed at all,
        // and a latch cleared there would throw the landing away for good.
        if (!Editable) return;
        _landingDeferredByCampaign = false;
        if (IsListTabOpen) ReadForListLanding();
        else ReadForEditorLanding();
    }

    /// <summary>ONE targeted <c>MODEM PRE n</c> for the PICKED preset — the
    /// editor's fresh-every-landing read. It is the only read that can see a
    /// DISABLED preset's fields.
    ///
    /// <para>CLONE ROUND 12 §9 A3: it now ALSO makes sure presence has been
    /// read once this session, behind its own gate. Round 11 left presence to
    /// the LIST tab alone, which is why the read-back row beside the picker
    /// could never show a real Enabled/Disabled — the operator programming a
    /// preset was the one person who could not see whether it took. The gate
    /// keeps this cheap: it is ONE extra operation per session, on whichever
    /// tab lands first, and never per landing.</para></summary>
    private void ReadForEditorLanding()
    {
        if (!Editable) return;
        // F11: the picked preset can be momentarily out of the CURRENT scope —
        // the mode report that changed the scope arrives before Refresh has
        // re-landed the picker. Reading it would be `MODEM PRE 0` at a `HOP>`
        // prompt, which the builder refuses outright. The re-land that follows
        // fixes the picker and reads again.
        var (first, last) = ScopeRange!.Value;
        if (_pickedPreset < first || _pickedPreset > last) return;
        _modem.QueryPreset(_pickedPreset);
        EnsurePresenceLoaded();
    }

    /// <summary>The LIST tab's lazy once-per-session read: the seven-preset
    /// FIELD batch, then the PRESENCE operation queued behind it. Order is
    /// enforced by the §8 single-slot modem queue, not by this call site — but
    /// the call ORDER is what puts fields first, and the state column is only
    /// meaningful beside rows that have fields.
    ///
    /// <para>§9 A3 splits the two gates: the field batch keeps
    /// <see cref="_listLoadedThisSession"/>, presence has its own. A list
    /// landing that follows an editor landing therefore sends the SEVEN
    /// field reads and no presence op (it is already loaded) — with one
    /// shared flag it would have sent neither.</para></summary>
    private void ReadForListLanding()
    {
        if (!Editable) return;
        if (!_listLoadedThisSession)
        {
            _listLoadedThisSession = true;
            _modem.RefreshPresetFields();
        }
        EnsurePresenceLoaded();
    }

    /// <summary>The bulk PRESENCE operation, ONCE per session (§9 A3) —
    /// ROUND 13 B1: delegated to <see cref="ModemSurface.EnsurePresenceLoaded"/>,
    /// which owns the gate for EVERY consumer. Presence was never per-tab or
    /// per-preset; §9 A3 had already widened it to whole-CARD, and the operate
    /// wheel's skip widened it again to whole-SURFACE. This call site stays
    /// because the LANDING is still this card's gesture — only the bookkeeping
    /// moved.</summary>
    private void EnsurePresenceLoaded() => _modem.EnsurePresenceLoaded();

    /// <summary>View-owned load trigger (the K2 clock pattern), a LANDING
    /// rather than a once-per-session latch: the card appearing means the
    /// operator is looking at a preset they may be about to program, and a
    /// cached listing can be older than the last write from any source.</summary>
    public void EnsureLoaded() => ReadForLanding();

    // ---- Sub-tab view state (switching sends nothing but the tier's read) --

    [ObservableProperty] private bool isListTabOpen;

    /// <summary>Landing back on the programming tab is an EDITOR LANDING (it
    /// re-reads) and a populate gesture (typed text clears, picks reset).</summary>
    [RelayCommand]
    private void OpenProgrammingTab()
    {
        IsListTabOpen = false;
        ReadForEditorLanding();
        Refresh(populateGesture: true);
    }

    /// <summary>The LIST tab is the lazy tier: it renders from the mirrors,
    /// and runs its batch-plus-presence read only once per session.</summary>
    [RelayCommand]
    private void OpenListTab()
    {
        IsListTabOpen = true;
        ReadForListLanding();
    }

    // ---- Preset picker (0-6, wrapping — view state) -----------------------

    private int _pickedPreset;
    public int PickedPreset => _pickedPreset;

    [ObservableProperty] private string pickedPresetText = "0";

    [RelayCommand] private void PresetUp() => MovePicker(+1);

    [RelayCommand] private void PresetDown() => MovePicker(-1);

    /// <summary>A spin is an EDITOR LANDING (it re-reads) and a populate
    /// gesture (typing clears, picks reset) — the two axes, both fired by one
    /// operator action.</summary>
    private void MovePicker(int delta)
    {
        // F11: the wheel wraps WITHIN THE CONFIRMED PROMPT'S band — 0-6 at
        // SSB/ALE, 7-9 at HOP. Unconfirmed there is no band to wrap in and the
        // controls are dark anyway (audit round 1, MAJOR 2).
        if (ScopeRange is not { } band) return;
        var (first, last) = band;
        int count = last - first + 1;
        _pickedPreset = first + (_pickedPreset - first + delta + count) % count;
        PickedPresetText = _pickedPreset.ToString(CultureInfo.InvariantCulture);
        InputError = "";
        ReadForEditorLanding();
        Refresh(populateGesture: true);
    }

    // ---- Gate + error note ------------------------------------------------

    [ObservableProperty] private bool areControlsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    private string disabledReason = "";

    public bool HasDisabledReason => !string.IsNullOrEmpty(DisabledReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    // ---- Name entry (the K5/X5 rule: a report never writes this buffer) ----

    [ObservableProperty] private string nameInput = "";
    [ObservableProperty] private string markInput = "";
    [ObservableProperty] private string spaceInput = "";

    /// <summary>What the round-7 empty-field fallback may re-send — the
    /// reported name, only when the write path could legally send it back.</summary>
    private string? _nameBacking;

    // ---- Selections (wire tokens; per-segment dirty guards) ---------------

    /// <summary>All four selections hold the WIRE token, never the display
    /// word: the buttons show words, the line carries shorts.</summary>
    [ObservableProperty] private string? selectedType;
    [ObservableProperty] private string? selectedDataMode;
    /// <summary>Optional (tap the lit segment to clear = omitted).</summary>
    [ObservableProperty] private string? selectedInterleave;
    /// <summary>Optional; EN / DIS. ROUND 13 B1 (item 7): it PREFILLS from the
    /// PRESENCE store — no LISTING has ever echoed a preset's state, but the
    /// bulk presence operation says it plainly, and that is what the read-back
    /// cell beside the picker has rendered since clone round 12 §9 A3. A
    /// prefill is a REPORT: only an operator TAP marks the field dirty, and
    /// only a dirty field puts EN/DIS on the Store line (see
    /// <see cref="PresenceStatePrefill"/> and the Store's dirty gate).</summary>
    [ObservableProperty] private string? selectedState;

    /// <summary>F11 — the HOP shape's SIGNALLING selection (<c>ASYNC</c> /
    /// <c>SYNC</c>). Unused under SSB/ALE, where the one welded phrase in
    /// <see cref="SelectedDataMode"/> carries both halves.</summary>
    [ObservableProperty] private string? selectedSync;

    /// <summary>F11 — the HOP shape's PORT selection (<c>DATA</c> /
    /// <c>REMOTE</c>).</summary>
    [ObservableProperty] private string? selectedPort;

    /// <summary>The baud WHEEL's pending selection (a wire token), null until
    /// prefilled or spun.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaudText))]
    private string? selectedBaud;

    /// <summary>The wheel's display: the selection's display word, "—" until
    /// there is one. Deliberately a COMPOSE control — see ui.md's baud-wheel
    /// deviation note; radio state lives on the read-back row.</summary>
    public string BaudText => SelectedBaud is null
        ? "—"
        : ModemPresetVocabulary.DisplayOf(OfferedBauds, SelectedBaud);

    /// <summary>The baud values the wheel offers: F11's HOP set {75, 150, 300}
    /// under a confirmed HOP (P5c — everything else is silently ignored), and
    /// the SSB card's TYPE-scoped offer otherwise.</summary>
    private IReadOnlyList<ModemPresetValue> OfferedBauds
        => IsHopScope ? ModemPresetVocabulary.HopBauds : ModemPresetVocabulary.BaudsFor(SelectedType);

    private bool _populating;
    private bool _typeDirty, _dataModeDirty, _interleaveDirty, _stateDirty, _baudDirty;
    private bool _syncDirty, _portDirty;

    partial void OnSelectedTypeChanged(string? value)
    {
        if (!_populating) _typeDirty = true;
        RebuildTypeRows();
        // ROUND 11 §6: MARK/SPACE at fsk-v ONLY. The other FSK types STORE
        // them and never display them back, so offering them there would be
        // offering a write this card could not verify.
        ShowMarkSpace = ModemPresetVocabulary.OffersMarkSpace(value);
        // The interleave OFFER is type-scoped, so a type change re-scopes it —
        // and an empty offer is what hides the row. What a re-scope does NOT do
        // is clear a selection: the round-9 rule is that visibility (now
        // scope) is a RENDERING fact and the command is the sending surface, so
        // an out-of-scope selection survives on screen and Store refuses it by
        // name. Clearing here would silently discard the operator's pick AND
        // leave both refusal paths unreachable.
        RebuildInterleaveRows();
    }

    partial void OnSelectedDataModeChanged(string? value)
    {
        if (!_populating) _dataModeDirty = true;
        RebuildDataModeRows();
    }

    partial void OnSelectedInterleaveChanged(string? value)
    {
        if (!_populating) _interleaveDirty = true;
        RebuildInterleaveRows();
    }

    partial void OnSelectedStateChanged(string? value)
    {
        if (!_populating) _stateDirty = true;
        RebuildStateRows();
    }

    partial void OnSelectedSyncChanged(string? value)
    {
        if (!_populating) _syncDirty = true;
        RebuildSyncRows();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        if (!_populating) _portDirty = true;
        RebuildPortRows();
    }

    partial void OnSelectedBaudChanged(string? value)
    {
        if (!_populating) _baudDirty = true;
        // Serial at 4800 is `uncoded` — the interleave row DISAPPEARS on a
        // baud change alone, so the offer is re-scoped here too.
        RebuildInterleaveRows();
    }

    /// <summary>MARK/SPACE render at <c>fsk-v</c> only (§6).</summary>
    [ObservableProperty] private bool showMarkSpace;

    /// <summary>The INTERLEAVE row applies — i.e. the picked type and baud
    /// offer at least one value (§6). Derived, never set by hand.</summary>
    [ObservableProperty] private bool showInterleave;

    // ---- Choice rows (the ChoiceItem idiom — IsActive = operator's pick) --
    // The button TEXT is the display word; the closure carries the WIRE token,
    // so nothing on screen has to be parsed back into a wire form.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeChoicesRow1))]
    [NotifyPropertyChangedFor(nameof(TypeChoicesRow2))]
    [NotifyPropertyChangedFor(nameof(TypeChoicesRow3))]
    private IReadOnlyList<ChoiceItem> typeChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataModeChoicesRow1))]
    [NotifyPropertyChangedFor(nameof(DataModeChoicesRow2))]
    private IReadOnlyList<ChoiceItem> dataModeChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InterleaveChoicesRow1))]
    [NotifyPropertyChangedFor(nameof(InterleaveChoicesRow2))]
    private IReadOnlyList<ChoiceItem> interleaveChoices = [];

    [ObservableProperty] private IReadOnlyList<ChoiceItem> stateChoices = [];

    /// <summary>F11 — the HOP shape's two segment rows. Both are REQUIRED
    /// (no clear-on-retap): the line has no form without them.</summary>
    [ObservableProperty] private IReadOnlyList<ChoiceItem> syncChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> portChoices = [];

    // ---- Round 10 §3/§8: the wide rows SPLIT across lines ------------------
    // The §3 phone budget takes three SegmentWidthWide buttons per line, so
    // Interleave renders 3+2. ROUND 11 §3 moves Type and Port to the wider
    // SegmentWidthPort, where two buttons per line is the budget: Type is
    // 2+2+2 and Port is 2+1. These are SLICES of the one list above — the SAME
    // ChoiceItem instances, so a pick still lights exactly one button and
    // there is no second copy of the row's state to keep in sync.

    public IReadOnlyList<ChoiceItem> TypeChoicesRow1 => [.. TypeChoices.Take(2)];
    public IReadOnlyList<ChoiceItem> TypeChoicesRow2 => [.. TypeChoices.Skip(2).Take(2)];
    public IReadOnlyList<ChoiceItem> TypeChoicesRow3 => [.. TypeChoices.Skip(4)];

    public IReadOnlyList<ChoiceItem> InterleaveChoicesRow1 => [.. InterleaveChoices.Take(3)];
    public IReadOnlyList<ChoiceItem> InterleaveChoicesRow2 => [.. InterleaveChoices.Skip(3)];

    public IReadOnlyList<ChoiceItem> DataModeChoicesRow1 => [.. DataModeChoices.Take(2)];
    public IReadOnlyList<ChoiceItem> DataModeChoicesRow2 => [.. DataModeChoices.Skip(2)];

    private void BuildChoiceRows()
    {
        RebuildTypeRows();
        RebuildDataModeRows();
        RebuildInterleaveRows();
        RebuildStateRows();
        RebuildSyncRows();
        RebuildPortRows();
    }

    private void RebuildSyncRows()
        => SyncChoices = [.. ModemPresetVocabulary.SyncModes.Select(
            v => new ChoiceItem(v.Display, SelectedSync == v.Wire, _ => SelectedSync = v.Wire))];

    private void RebuildPortRows()
        => PortChoices = [.. ModemPresetVocabulary.HopPorts.Select(
            v => new ChoiceItem(v.Display, SelectedPort == v.Wire, _ => SelectedPort = v.Wire))];

    private void RebuildTypeRows()
        => TypeChoices = [.. ModemPresetVocabulary.Types.Select(
            v => new ChoiceItem(v.Display, SelectedType == v.Wire, _ => SelectedType = v.Wire))];

    private void RebuildDataModeRows()
        => DataModeChoices = [.. ModemPresetVocabulary.DataModes.Select(
            v => new ChoiceItem(v.Display, SelectedDataMode == v.Wire, _ => SelectedDataMode = v.Wire))];

    // Interleave and State are OPTIONAL: tapping the lit segment clears it,
    // and a cleared row is omitted from the Store line.
    //
    // ROUND 11 §6: the interleave row offers the values the PICKED TYPE (and,
    // for Serial, the picked BAUD) actually accepts — 39-tone LO/SH/ALTS/ALTL,
    // Serial LO/SH/ZE below 4800 — and NOTHING otherwise, which is what hides
    // the row. Round 10 offered all five on both tone types, so two of five
    // drew ** ERROR ** on each.
    private void RebuildInterleaveRows()
    {
        var offered = ModemPresetVocabulary.InterleavesFor(SelectedType, SelectedBaud);

        // ---- CLONE ROUND 12 §9 A2: a pick cannot outlive its OFFER ---------
        //
        // The bench symptom: pick an interleave on 39-tone, switch to Serial
        // at 4800 — the row DISAPPEARS (empty offer is what hides it) — then
        // Store refuses, naming a value the UI is no longer showing. The
        // operator has no control to clear and no way to see what is wrong.
        //
        // An EMPTY offer is categorically different from a re-scoped one. A
        // re-scope still renders a row the operator can see and correct, so
        // round 9's rule holds there: visibility is a rendering fact, the
        // command is the sending surface, and the pick survives for Store to
        // refuse BY NAME. An empty offer renders NOTHING, so there is nothing
        // for the rule to protect — the pick becomes invisible state that can
        // only ever produce a confusing refusal. It clears, and its dirty flag
        // clears with it (a cleared pick is not an operator preference to
        // preserve against the next prefill).
        //
        // The Store refusal STAYS as the belt for the non-empty case (the
        // 39-tone → Serial subset), and both paths are pinned: this is a
        // NARROWING of the round-9 rule, not a reversal of it.
        if (offered.Count == 0 && SelectedInterleave is not null)
        {
            // SAVE AND RESTORE, not set-and-clear: this method is reached from
            // INSIDE PopulateEditor's own populating window (the type prefill
            // raises OnSelectedTypeChanged, which lands here). A plain
            // `finally { _populating = wasPopulating; }` would end that window early
            // and the prefills that follow — baud, interleave, state — would
            // each mark themselves DIRTY, permanently freezing the operator's
            // editor at one listing's values.
            bool wasPopulating = _populating;
            _populating = true;
            try { SelectedInterleave = null; }
            finally { _populating = wasPopulating; }
            _interleaveDirty = false;
        }

        InterleaveChoices = [.. offered.Select(
            v => new ChoiceItem(v.Display, SelectedInterleave == v.Wire,
                _ => SelectedInterleave = SelectedInterleave == v.Wire ? null : v.Wire))];
        ShowInterleave = offered.Count > 0;
    }

    private void RebuildStateRows()
        => StateChoices = [.. ModemPresetVocabulary.States.Select(
            v => new ChoiceItem(v.Display, SelectedState == v.Wire,
                _ => SelectedState = SelectedState == v.Wire ? null : v.Wire))];

    // ---- The baud wheel (ruling 4) ----------------------------------------

    [RelayCommand] private void BaudUp() => SpinBaud(+1);

    [RelayCommand] private void BaudDown() => SpinBaud(-1);

    /// <summary>◀ / display / ▶ over the values the PICKED TYPE really stores
    /// (§6 — every baud past a type's ceiling is SILENTLY CLAMPED, so it is
    /// never offered), wrapping. A spin from "—" lands on the REPORTED baud
    /// when the picked preset lists one the type still offers, else on the
    /// offer's first — it does not jump past the value the operator is most
    /// likely to keep.</summary>
    private void SpinBaud(int delta)
    {
        var bauds = OfferedBauds;
        if (bauds.Count == 0) return;
        if (SelectedBaud is null)
        {
            var reported = ReportedBaud();
            SelectedBaud = reported is not null && bauds.Any(v => v.Wire == reported)
                ? reported
                : bauds[0].Wire;
            return;
        }
        int i = 0;
        for (int n = 0; n < bauds.Count; n++)
            if (bauds[n].Wire == SelectedBaud) { i = n; break; }
        SelectedBaud = bauds[(i + delta + bauds.Count) % bauds.Count].Wire;
    }

    private string? ReportedBaud()
        => ModemPresetVocabulary.BaudFromListing(TokenAfter(PickedTokens(), "BAUD"));

    // ---- Rows -------------------------------------------------------------

    /// <summary>The LIST tab's rows: the stored presets, read-only.</summary>
    public ObservableCollection<ModemPresetRow> Rows { get; } = [];

    [ObservableProperty] private bool hasNoPresets = true;

    /// <summary>The read-back BESIDE THE PICKER (ruling 3): the picked
    /// preset's single list row, in the same projection the list tab renders,
    /// "—" while the radio has not listed it.</summary>
    [ObservableProperty] private ModemPresetRow pickedRow = ModemPresetRow.Unlisted(0);

    // ---- Refresh from the mirror ------------------------------------------

    private void Refresh(bool populateGesture = false)
    {
        AreControlsEnabled = Editable;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : _modem.ConfirmedMode is null ? ModeUnconfirmedReason
            : "";

        // ---- F11 (ruling R-D, decision A-9): THE SCOPE RE-LAND -------------
        // A mode change while the card is open moves it to the other shape.
        // Everything a re-land does is what a picker spin already does — the
        // picked preset resets (to the NEW band's first), typed text and picks
        // are discarded, and the new scope is read — because nothing carries
        // across: a HOP row has no TYPE to carry and an SSB row has no PORT
        // column to carry into.
        //
        // The flag is written BEFORE the read so the mirror changes the read
        // provokes cannot re-enter this branch.
        // THREE states, not two (audit round 1, MAJOR 2): unconfirmed is its
        // own, and it is NOT "the SSB shape". While the mode is unconfirmed the
        // card has landed on NOTHING — it keeps whichever stack was last shown
        // but every control is dark and no read goes out — and the moment the
        // radio reports a mode it lands on THAT scope, even if it is the one it
        // showed before, because it never read while it could not name it.
        bool? hop = _modem.ConfirmedMode is null ? null : _modem.IsHopPrompt;
        if (hop != _landedHopScope)
        {
            // A genuine CHANGE of confirmed scope is a landing; the FIRST
            // ADOPTION is only one if a Ready arrival is still owed one (see
            // _landingOwedOnModeConfirm). Adopting a scope always re-shapes the
            // card — that is display, not traffic.
            bool changed = _landedHopScope is not null;
            _landedHopScope = hop;
            if (hop is { } confirmed)
            {
                IsHopScope = confirmed;

                // THE PICKER AND THE BUFFERS reset on a real scope CHANGE —
                // nothing carries across scopes — and on an ADOPTION that finds
                // the picked preset outside the new band. They do NOT reset on
                // a reconnect into the SAME band: the Ready-arrival landing is
                // a READ, not a populate gesture, and the operator's typing
                // survives a dropped session (the standing pin).
                var (first, last) = _modem.PresetRange;
                if (changed || _pickedPreset < first || _pickedPreset > last)
                {
                    _pickedPreset = first;
                    PickedPresetText = _pickedPreset.ToString(CultureInfo.InvariantCulture);
                    InputError = "";
                    populateGesture = true;
                }

                if (changed || _landingOwedOnModeConfirm)
                {
                    _landingOwedOnModeConfirm = false;
                    ReadForLanding();
                }
            }
        }

        // THE DEFERRED LANDING, SETTLED (audit round 1). Placed AFTER the scope
        // block so the picker has already been re-landed — `ReadForLanding` is
        // what clears the latch, and only when the card can actually read.
        if (_landingDeferredByCampaign && _campaign?.CampaignActive != true) ReadForLanding();

        UpdateRows();
        PopulateEditor(populateGesture);

        StoreCommand.NotifyCanExecuteChanged();
    }

    private void UpdateRows()
    {
        var lines = _modem.Presets;
        HasNoPresets = lines.Count == 0;

        var rebuilt = lines
            .Select(line => new ModemPresetRow(line, PresenceTextFor(line)))
            .ToList();

        // Rebuild only on real change (the LIST-rows precedent — no visual
        // restarts under the operator's finger). The PRESENCE cell is part of
        // "real change" now: the state column moves without the fields line
        // moving at all, and a detector that watched only RawLine would leave
        // it stale for the rest of the session.
        if (Rows.Count == rebuilt.Count
            && Rows.Select(r => r.RawLine).SequenceEqual(rebuilt.Select(r => r.RawLine))
            && Rows.Select(r => r.PresenceText).SequenceEqual(rebuilt.Select(r => r.PresenceText)))
            return;

        Rows.Clear();
        foreach (var row in rebuilt) Rows.Add(row);
    }

    /// <summary>
    /// ROUND 11 §6 — the STATE column, from the presence store and nothing
    /// else. THREE renders, and the third is the honest one:
    /// <list type="bullet">
    ///   <item><c>Enabled</c> — a COMPLETED presence read listed this
    ///     preset.</item>
    ///   <item><c>Disabled</c> — a COMPLETED presence read did NOT list it,
    ///     AND a targeted read has given it fields. Absence from the bulk
    ///     listing is the ONLY captured disabled signal, and it only means
    ///     "disabled" for a preset that exists.</item>
    ///   <item><c>—</c> — anything else: no presence read has completed, one
    ///     is in flight (its answer may differ from the last), or the preset
    ///     has no fields for the absence to be about.</item>
    /// </list>
    /// </summary>
    private string PresenceTextFor(string line)
    {
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null
            || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return ModemPresetRow.AbsentText;

        return PresenceWireFor(number) is { } wire
            ? ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.States, wire)
            : ModemPresetRow.AbsentText;
    }

    /// <summary>ROUND 13 B1 — the same three-state as
    /// <see cref="PresenceTextFor"/>, in WIRE form (<c>EN</c> / <c>DIS</c> /
    /// null). Extracted so the READ-BACK CELL and the EDITOR's state PREFILL
    /// cannot drift: item 7 is precisely the complaint that the button and the
    /// cell disagreed, and two copies of this rule would be the way to
    /// reintroduce it. The caller supplies the "preset has fields" half — see
    /// <see cref="PresenceStatePrefill"/>.</summary>
    private string? PresenceWireFor(int number)
    {
        // AUDIT ROUND 1, MAJOR 1 — `Completed` IS NOT AUTHORITY. The bulk
        // listing names the enabled presets of ONE band, so a set read at the
        // other prompt says NOTHING about this preset: rendering "Disabled"
        // from it would be inventing a report out of a silence. A non-covering
        // completed set is UNKNOWN here, exactly like no read at all — the
        // third display state the card already has.
        var presence = _modem.PresetPresence;
        if (_modem.ConfirmedMode is not { } mode || !presence.Covers(mode)) return null;
        if (!ModemPresetScope.Covers(mode, number)) return null;
        return presence.Enabled.Contains(number) ? "EN" : "DIS";
    }

    /// <summary>ROUND 13 B1 (item 7) — the PICKED preset's state prefill:
    /// <c>EN</c> when a completed presence read listed it, <c>DIS</c> when a
    /// completed read did NOT list it and a targeted read has given it FIELDS,
    /// and null otherwise (no completed read, one in flight, or a preset with
    /// no fields for the absence to be about). Exactly the three-state
    /// <see cref="PresenceTextFor"/> renders — absence from the bulk listing
    /// only means "disabled" for a preset that exists.</summary>
    private string? PresenceStatePrefill()
        => PickedLine() is null ? null : PresenceWireFor(_pickedPreset);

    /// <summary>The picked preset's listing line, or null when the radio has
    /// not listed it (unstored, or the listing has not landed yet).</summary>
    private string? PickedLine()
        => _modem.Presets.FirstOrDefault(
            l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                 == _pickedPreset.ToString(CultureInfo.InvariantCulture));

    private string[] PickedTokens()
        => PickedLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    private void PopulateEditor(bool gesture)
    {
        if (gesture)
        {
            NameInput = "";
            MarkInput = "";
            SpaceInput = "";
            _typeDirty = false;
            _dataModeDirty = false;
            _interleaveDirty = false;
            _stateDirty = false;
            _baudDirty = false;
            _syncDirty = false;
            _portDirty = false;
            _populating = true;
            try
            {
                SelectedInterleave = null;
                SelectedState = null;
                SelectedBaud = null;
                SelectedSync = null;
                SelectedPort = null;
            }
            finally { _populating = false; }
        }

        var line = PickedLine();
        var tokens = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        // CLONE ROUND 12 §9 A3: the read-back row carries PRESENCE, like the
        // list tab's rows. Its State cell used to bind a listing-derived value
        // that was structurally ALWAYS "—" (no capture has ever put a preset's
        // enabled state on a listing line), so the operator programming a
        // preset was the one person who could not see whether the write took.
        // Presence is the only captured source there is, and it is now read on
        // an editor landing too (ReadForEditorLanding).
        PickedRow = line is null
            ? ModemPresetRow.Unlisted(_pickedPreset)
            : new ModemPresetRow(line, PresenceTextFor(line));

        // Name: token 2 of the listing ("1 T39  ASYNC …"). Backing only if the
        // write path could legally re-send it (the R7 rule: shown is not
        // necessarily sendable).
        string? name = tokens.Length > 1 ? tokens[1] : null;
        _nameBacking = name is { Length: >= 1 and <= 4 } n
            && n.All(char.IsAsciiLetterOrDigit)
            && n.ToUpperInvariant() is not ("OF" or "OFF" or "SH" or "SHOW" or "PRE")
            ? n : null;

        // Prefill every selection through the vocabulary, each behind its own
        // dirty guard (the round-5 idiom): an operator's pick survives every
        // later report until the next populate gesture. An unmapped listing
        // token prefills NOTHING — the AGC precedent — which blocks Store
        // until the operator picks, while the read-back row shows the radio's
        // own word verbatim.
        _populating = true;
        try
        {
            if (!_typeDirty)
                SelectedType = ModemPresetVocabulary.TypeFromListing(TokenAfter(tokens, "TYPE"));
            if (!_dataModeDirty)
                SelectedDataMode = ModemPresetVocabulary.DataModeFromListing(DataModePhrase(tokens));
            // BAUD BEFORE INTERLEAVE, deliberately (round 11 §6): Serial's
            // interleave row exists only below 4800 baud, so the interleave
            // prefill has to run against THIS listing's baud, not the previous
            // preset's.
            if (!_baudDirty)
                SelectedBaud = ModemPresetVocabulary.BaudFromListing(TokenAfter(tokens, "BAUD"));
            if (!_interleaveDirty)
            {
                // Only where the row applies: prefilling a hidden row would
                // hand the operator a value Store then refuses to send.
                var reported = ModemPresetVocabulary.InterleaveFromListing(
                    TokenAfter(tokens, "INTER") ?? TokenAfter(tokens, "INTERLEAV"));
                SelectedInterleave = ShowInterleave ? reported : null;
            }
            if (!_stateDirty)
            {
                // ROUND 13 B1 (item 7, owner 2026-08-19: "selecting a new
                // preset to program does not highlight the current
                // Enabled/Disabled button even though the state shows in the
                // list entry"). The prefill now comes from the PRESENCE
                // three-state — the SAME source PresenceTextFor renders into
                // the row beside the picker — so the button and the cell can
                // no longer disagree.
                //
                // It replaces a listing-token lookup that was DEAD BY
                // CONSTRUCTION: round 9 wrote it as a vocabulary lookup "so
                // the day the bench captures the spelling the fix is one
                // entry", but the bench established the opposite — a listing
                // NEVER carries a preset's enabled state — so the lookup could
                // only ever return null. The vocabulary's StateFromListing
                // half is deleted with it.
                SelectedState = PresenceStatePrefill();
            }
            // F11 — the HOP shape's two columns, prefilled from the SHORT line
            // (`9 DAT9 ASYNC REMOTE BAUD 300`): the ASYNC/SYNC word and the
            // word after it. Same dirty-guard rule as every other selection.
            if (!_syncDirty)
                SelectedSync = ModemPresetVocabulary.SyncModeFromListing(SyncToken(tokens));
            if (!_portDirty)
                SelectedPort = ModemPresetVocabulary.HopPortFromListing(PortToken(tokens));
        }
        finally { _populating = false; }

        RebuildTypeRows();
        RebuildDataModeRows();
        RebuildInterleaveRows();
        RebuildStateRows();
        RebuildSyncRows();
        RebuildPortRows();
    }

    // The two HOP columns' readers live on ModemPresetRow with the rest (§8's
    // ONE-parser rule): the editor's prefill and the read-back's projection
    // must read one line the same way, or they drift.
    private static string? SyncToken(string[] tokens) => ModemPresetRow.SyncToken(tokens);

    private static string? PortToken(string[] tokens) => ModemPresetRow.PortToken(tokens);

    // The listing readers live on ModemPresetRow (§8): the editor's prefill
    // and the read-back's projection must read the same line the same way, so
    // there is ONE parser, not two that can drift.
    private static string? DataModePhrase(string[] tokens) => ModemPresetRow.DataModePhrase(tokens);

    private static string? TokenAfter(string[] tokens, string key) => ModemPresetRow.TokenAfter(tokens, key);

    // ---- Store (ONE line, all value tokens SHORT) -------------------------

    private bool CanStore() => Editable;

    [RelayCommand(CanExecute = nameof(CanStore))]
    private void Store()
    {
        if (!Editable) return;
        int preset = _pickedPreset;

        // Name: typed wins; empty falls back to the reported name; nothing
        // reported refuses (the round-7 rule, field named).
        string name = (NameInput ?? "").Trim();
        if (name.Length == 0)
        {
            if (_nameBacking is null)
            {
                Fail(preset, "no name typed and none reported to fall back to.");
                return;
            }
            name = _nameBacking;
        }
        else if (name.Length > 4 || !name.All(char.IsAsciiLetterOrDigit))
        {
            Fail(preset, "name is 1-4 letters or digits (e.g. T39).");
            return;
        }
        else if (name.ToUpperInvariant() is "OF" or "OFF" or "SH" or "SHOW" or "PRE")
        {
            Fail(preset, "that name collides with a MODEM selector token — the preset could never be selected by name.");
            return;
        }

        // ---- F11: THE HOP SHAPE'S OWN STORE --------------------------------
        // A `HOP>` preset has no TYPE, no INTERLEAV and no MARK/SPACE, and its
        // mode phrase is two independent words — so none of the SSB
        // stale-pick family below applies to it and its line goes out through
        // its own builder (decision A-9: a separate builder keeps the SSB bytes
        // pinned untouched).
        if (IsHopScope)
        {
            StoreHopPreset(preset, name);
            return;
        }

        if (SelectedType is not { } type)
        {
            Fail(preset, "pick a type.");
            return;
        }
        if (SelectedDataMode is not { } dataMode)
        {
            Fail(preset, "pick a data mode.");
            return;
        }
        if (SelectedBaud is not { } baud)
        {
            Fail(preset, "pick a baud.");
            return;
        }

        // ---- THE STALE-PICK FAMILY (§6, re-checked at the SENDING SURFACE) --
        //
        // Every offer on this card is scoped by the picked TYPE (and, for
        // interleave, the picked BAUD), and a re-scope deliberately does NOT
        // clear a selection: scope is a RENDERING fact, a command is the
        // sending surface, and silently discarding the operator's pick is its
        // own defect. The consequence is that a selection can go STALE — still
        // held, no longer offered — and the ONLY thing standing between a stale
        // pick and the wire is a check here.
        //
        // These checks read the values being SENT (`type`, `baud`), never the
        // Show* rendering flags. A flag answers "is the row on screen"; the
        // question at this point is "is this value one the radio will store",
        // and those are different questions the moment a row stays visible with
        // its offer changed underneath — which is exactly the hole the round-1
        // audit found on the interleave path (39-tone ALTS surviving a switch
        // to Serial, whose row stays up offering LO/SH/ZE).

        // BAUD: an unoffered baud is SILENTLY CLAMPED by the radio, which then
        // echoes a success the app would report verbatim.
        if (!ModemPresetVocabulary.BaudsFor(type).Any(v => v.Wire == baud))
        {
            Fail(preset, $"{Word(ModemPresetVocabulary.Bauds, baud)} is not a baud this type "
                + "stores — pick one the wheel offers.");
            return;
        }

        // INTERLEAVE: two different wrongs, and the operator needs to be told
        // which. An EMPTY offer means the row does not apply at all; a
        // non-empty offer that does not contain the pick means the value
        // belongs to a different type (the radio answers ** ERROR ** to it).
        if (SelectedInterleave is { } interleave)
        {
            var offered = ModemPresetVocabulary.InterleavesFor(type, baud);
            if (offered.Count == 0)
            {
                Fail(preset, "interleave applies to 39 tone, and to Serial below 4800 baud.");
                return;
            }
            if (!offered.Any(v => v.Wire == interleave))
            {
                Fail(preset, $"{Word(ModemPresetVocabulary.Interleaves, interleave)} is not an "
                    + "interleave this type takes — pick one the row offers.");
                return;
            }
        }

        // THE ALE-PROMPT GUARD, RE-KEYED (clone round 12 §4). Round 11 keyed
        // this on INTERLEAV; the round-11 §14 bench session proved the swallow
        // keys on the **`DIS` TOKEN** instead (2026-08-18) — a write carrying
        // DIS is what the radio accepts-looking and does not store, and an
        // INTERLEAV-carrying write is fine. So the guard fires on a DISABLE,
        // and the wording follows the real cause (R13: no radio token reaches
        // the operator). The message is the whole error: this is a SESSION
        // condition, not something wrong with the preset, so it carries no
        // "Preset n:" prefix.
        //
        // ROUND 13 B1 scopes it to a DIRTY DIS, through `state` below: an
        // auto-prefilled DIS on an already-disabled preset is a REPORT, not a
        // disable request, and refusing a Store that was never going to carry
        // the token would be the app inventing a refusal the radio never made.
        // ROUND 13 B1 (item 7) — THE DIRTY GATE, and why the wire is unchanged.
        // Until this round SelectedState could ONLY be non-null because the
        // operator tapped a segment (the listing prefill was dead by
        // construction), so "non-null" and "operator asked for it" were the
        // same fact and the Store line carried EN/DIS exactly then. The prefill
        // now lands a value the operator did not choose, so the two facts have
        // come apart and the SENDING surface takes the one it always meant:
        // _stateDirty. Byte-identical wire output to before this round — a
        // Store that carries a state token the operator did not tap is a
        // DEFECT, not a feature (constitution §3.5).
        bool? state = _stateDirty
            ? SelectedState switch { "EN" => true, "DIS" => false, _ => (bool?)null }
            : null;
        if (state is false && _modem.IsAlePrompt)
        {
            InputError = AleDisableRefusal;
            return;
        }

        // MARK/SPACE: a pair or nothing (no capture says what one alone
        // means), and fsk-v only — the type whose stored values the radio
        // reads back (§6).
        string mark = (MarkInput ?? "").Trim();
        string space = (SpaceInput ?? "").Trim();
        string? markArg = null, spaceArg = null;
        if (mark.Length > 0 || space.Length > 0)
        {
            // Read from the TYPE being sent, like its two siblings above — not
            // from ShowMarkSpace. The two agree today, and the point of the
            // family rule is that the sending surface does not depend on that.
            if (!ModemPresetVocabulary.OffersMarkSpace(type))
            {
                Fail(preset, "MARK/SPACE apply to the FSK VFT type only.");
                return;
            }
            if (mark.Length == 0 || space.Length == 0)
            {
                Fail(preset, "MARK and SPACE are set together or not at all.");
                return;
            }
            if (!InBounds(mark) || !InBounds(space))
            {
                Fail(preset, $"MARK/SPACE are {ModemPresetVocabulary.MarkSpaceMinimum}-"
                    + $"{ModemPresetVocabulary.MarkSpaceMaximum}.");
                return;
            }
            markArg = mark;
            spaceArg = space;
        }

        InputError = "";
        _modem.ProgramPreset(preset, name, type, dataMode, baud,
            SelectedInterleave, markArg, spaceArg, state);

        // ROUND 11 §6 (the round-10 verify doctrine). The programming echo is a
        // listing line, but it is not the whole read-back: a clamped baud and a
        // state change are both invisible in it. So the written preset is
        // re-read TARGETED, and a line that carried EN/DIS additionally re-runs
        // the PRESENCE operation — the only source the state column has. The §8
        // queue serializes the two; the call order puts fields first.
        _modem.QueryPreset(preset);
        if (state is not null) _modem.QueryPresetPresence();
    }

    /// <summary>
    /// F11 — the <c>HOP&gt;</c> Store. The line is
    /// <c>MODEM PRESET n NAME x ASYNC|SYNC DATA|REMOTE BAUD b</c>, and the
    /// EN/DIS token follows on its OWN line when the operator TAPPED the state
    /// segment (the same dirty gate the SSB path uses — a prefilled state is a
    /// report, not a request). The state has to be last: any field write
    /// RE-ENABLES a disabled preset (P5b), so a disable sent first would be
    /// undone by the fields.
    ///
    /// <para>The read-back is the same doctrine as the SSB path: the echo is a
    /// listing line and cannot show a silently-ignored baud or a state change,
    /// so the preset is re-read TARGETED and a state-carrying Store also
    /// re-runs the PRESENCE operation.</para>
    /// </summary>
    private void StoreHopPreset(int preset, string name)
    {
        if (SelectedSync is not { } sync
            || Falcon.Core.Protocol.Wire.ParseSyncMode(sync) is not { } syncMode)
        {
            Fail(preset, "pick async or sync.");
            return;
        }
        if (SelectedPort is not { } port
            || Falcon.Core.Protocol.Wire.ParseDataMode(port) is not { } portMode)
        {
            Fail(preset, "pick a port.");
            return;
        }
        if (SelectedBaud is not { } baud)
        {
            Fail(preset, "pick a baud.");
            return;
        }
        // The stale-pick belt, in the one form this shape can go stale: the
        // baud wheel is scoped, and an out-of-vocabulary HOP baud is SILENTLY
        // ignored with the old value echoed back (P5c) — the one failure a
        // caller cannot see from the answer.
        if (!ModemPresetVocabulary.HopBauds.Any(v => v.Wire == baud))
        {
            Fail(preset, $"{Word(ModemPresetVocabulary.Bauds, baud)} is not a baud a hop preset "
                + "stores — pick one the wheel offers.");
            return;
        }

        bool? state = _stateDirty
            ? SelectedState switch { "EN" => true, "DIS" => false, _ => (bool?)null }
            : null;

        InputError = "";
        _modem.ProgramHopPreset(preset, name, syncMode, portMode, baud, state);
        _modem.QueryPreset(preset);
        if (state is not null) _modem.QueryPresetPresence();
    }

    /// <summary>The exact wording for the ALE-prompt DISABLE refusal
    /// (clone round 12 §4; R13: human-readable, no radio token). Round 11
    /// spelled this "Interleave changes are ignored…" on the strength of an
    /// unverified reading; the 2026-08-18 bench session proved the swallow
    /// keys on the DISABLE, so both the key and the sentence moved.</summary>
    public const string AleDisableRefusal =
        "Disabling a preset is ignored at an ALE prompt — leave ALE first.";

    /// <summary>The DISPLAY word for a wire token, for naming an offender in a
    /// refusal. R13: an operator message never carries a radio token, and the
    /// wire short (<c>ALTS</c>, <c>VO</c>) is exactly that.</summary>
    private static string Word(IReadOnlyList<ModemPresetValue> column, string wire)
        => ModemPresetVocabulary.DisplayOf(column, wire);

    private static bool InBounds(string digits)
        => digits.All(char.IsAsciiDigit)
           && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
           && value >= ModemPresetVocabulary.MarkSpaceMinimum
           && value <= ModemPresetVocabulary.MarkSpaceMaximum;

    private void Fail(int preset, string message)
        => InputError = $"Preset {preset}: {message}";
}

/// <summary>
/// One read-only row of a stored preset. Round 9 parsed # and Name (both
/// pinned by the confirmed listing shape) and kept everything after the name
/// VERBATIM; round 10 §8 (owner ruling 4) adds the ENRICHED PROJECTION beside
/// it, and the verbatim <see cref="ParametersText"/> stays exactly as it was —
/// the LIST tab is unchanged and still renders it.
///
/// <para><b>The parse rule, in full.</b> A row is PARSED iff the MANDATORY
/// TRIO — Type, Data mode and Baud — all map through
/// <see cref="ModemPresetVocabulary"/>'s LISTING forms. Interleave, Mark and
/// Space are OPTIONAL (absent → "—"). STATE is not parsed from the line AT
/// ALL: clone round 12 §9 A3 deleted the listing-derived cell, because the
/// bench established that a listing NEVER carries a preset's enabled state —
/// it comes from the bulk presence operation and nowhere else, and that is
/// what <see cref="ModemPresetRow.PresenceText"/> carries. ANY unmapped
/// MANDATORY token sets
/// <see cref="IsParsed"/> false and the read-back falls back to the ONE
/// verbatim cell — the AGC precedent: nothing is ever guessed into a column.</para>
///
/// <para><b>Which optional cell line 2 shows</b> follows the round-9 type map:
/// Interleave on the tone waveforms (39-tone / Serial), Mark+Space on the four
/// FSK types. The MARK/SPACE listing spelling IS captured — <c>MARK 1500
/// SPACE 1700</c>, off an <c>fsk-v</c> preset (2026-08-17). This paragraph
/// said "no capture has ever listed them" until round 11 P5; that was
/// round-9's tier, and the campaign had already retired it. What the capture
/// is SCOPED to matters and is the reason the editor offers the pair at
/// <c>fsk-v</c> alone: the values are stored on EVERY FSK type but the radio
/// LISTS them only at <c>fsk-v</c> (written at <c>fskns</c>, invisible there,
/// revealed intact by flipping the type).</para>
///
/// <para><b>This projection is UNCHANGED by round 11 §6, deliberately.</b> The
/// EDITOR's offers became type-scoped — MARK/SPACE at <c>fsk-v</c> alone,
/// interleave excluding Serial at 4800 — but a READ-BACK renders what the
/// radio SAID, and the radio stores MARK/SPACE on every FSK type. Narrowing
/// this map to match the editor's would hide a value the radio reported. The
/// two answer different questions: "what may I write" and "what did it
/// say".</para>
///
/// <para>Round 9's other contract is unchanged: the PICKED preset renders
/// through this same projection beside the picker, so the editor's read-back
/// and the list cannot disagree.</para>
/// </summary>
public sealed class ModemPresetRow
{
    /// <summary>The round's third-state placeholder — "the radio has not said".
    /// Public because the ViewModel's presence projection renders the same
    /// character for the same reason, and two literals would drift.</summary>
    public const string AbsentText = "—";

    private const string Absent = AbsentText;

    /// <param name="presenceText">ROUND 11 §6 — the ENABLED/DISABLED cell, from
    /// the presence store, computed by the ViewModel because it is the only
    /// thing that can see the store. NOT parsed from
    /// <paramref name="rawLine"/>: the line never carries it. Defaults to the
    /// third state, so every other construction site (the read-back row, the
    /// §8 fixtures) is unchanged.</param>
    public ModemPresetRow(string rawLine, string presenceText = AbsentText)
    {
        RawLine = rawLine;
        PresenceText = presenceText;
        var tokens = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        NumberText = tokens.Length > 0 ? tokens[0] : Absent;
        NameText = tokens.Length > 1 ? tokens[1] : Absent;
        // R8-review MAJOR 4: locate the NAME from after the number token —
        // a bare IndexOf found the first occurrence, so a numeric name equal
        // to the preset number ("1 1  ASYNC …") split the line wrong.
        int afterName = 0;
        if (tokens.Length > 1)
        {
            int numberEnd = rawLine.IndexOf(tokens[0], StringComparison.Ordinal) + tokens[0].Length;
            afterName = rawLine.IndexOf(tokens[1], numberEnd, StringComparison.Ordinal) + tokens[1].Length;
        }
        ParametersText = tokens.Length > 2 ? rawLine[afterName..].Trim() : Absent;

        // ---- The enriched projection (§8) ----------------------------------
        // The MANDATORY trio.
        string? type = ModemPresetVocabulary.TypeFromListing(TokenAfter(tokens, "TYPE"));
        string? dataMode = ModemPresetVocabulary.DataModeFromListing(DataModePhrase(tokens));
        string? baud = ModemPresetVocabulary.BaudFromListing(TokenAfter(tokens, "BAUD"));

        // ---- THE SHAPE (audit round 1, MAJOR 3) ---------------------------
        // There are TWO listing forms and the line says which it is: an
        // `SSB>` row carries a TYPE COLUMN, a `HOP>` row has none at all
        // (P5 — `MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300`). The mandatory
        // set is therefore SHAPE-SCOPED, not loosened:
        //   * TYPE column present → the round-9 TRIO, type · port · baud,
        //     unchanged, so an SSB row with an unmapped TYPE still falls to
        //     the verbatim cell;
        //   * TYPE column absent  → the PAIR, port · baud.
        // Before this, every HOP row was "unparsed" and rendered its raw
        // `ASYNC REMOTE BAUD 300` in one cell — a narrowed read-back AND raw
        // wire text in front of the operator (I-5).
        bool hasTypeColumn = Array.Exists(tokens,
            t => string.Equals(t, "TYPE", StringComparison.OrdinalIgnoreCase));

        IsHopShape = !hasTypeColumn && dataMode is not null && baud is not null;
        IsParsed = hasTypeColumn
            ? type is not null && dataMode is not null && baud is not null
            : IsHopShape;

        // The HOP shape's own two columns — the same two the editor offers, so
        // the read-back and the buttons cannot disagree about one line.
        string? sync = ModemPresetVocabulary.SyncModeFromListing(SyncToken(tokens));
        string? port = ModemPresetVocabulary.HopPortFromListing(PortToken(tokens));
        SyncText = sync is null ? Absent : ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.SyncModes, sync);
        PortText = port is null ? Absent : ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.HopPorts, port);

        TypeText = type is null ? Absent : ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Types, type);
        DataModeText = dataMode is null
            ? Absent : ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.DataModes, dataMode);
        BaudText = baud is null ? Absent : ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Bauds, baud);

        // The OPTIONALS. An unmapped optional is simply absent — it never
        // costs the row its parse.
        // Display-side lookup, so the read-only "uncoded" spelling the radio
        // emits unprompted (2026-08-16) renders as a word rather than as
        // "absent" — it has no wire token and so never prefills the editor.
        InterleaveText = ModemPresetVocabulary.InterleaveDisplayFromListing(
            TokenAfter(tokens, "INTER") ?? TokenAfter(tokens, "INTERLEAV")) ?? Absent;

        MarkText = TokenAfter(tokens, "MARK") ?? Absent;
        SpaceText = TokenAfter(tokens, "SPACE") ?? Absent;

        // CLONE ROUND 12 §9 A3: the listing-derived STATE cell is DELETED.
        // Round 9 wrote it as a vocabulary lookup so that "the day the bench
        // captures the spelling, the fix is one entry in the vocabulary" — but
        // the bench instead established that the listing NEVER carries the
        // state (it comes from the bulk presence operation and nowhere else),
        // so the lookup was a structurally-always-"—" cell dressed as a read.
        // PresenceText is the real answer and every consumer now binds it.

        ShowsInterleave = type is not null && ModemPresetVocabulary.InterleaveTypeWires.Contains(type);
        ShowsMarkSpace = type is not null && ModemPresetVocabulary.FskTypeWires.Contains(type);
    }

    private ModemPresetRow(int preset, bool _)
    {
        RawLine = "";
        PresenceText = Absent;
        NumberText = preset.ToString(CultureInfo.InvariantCulture);
        NameText = Absent;
        ParametersText = Absent;
        TypeText = Absent;
        DataModeText = Absent;
        BaudText = Absent;
        InterleaveText = Absent;
        MarkText = Absent;
        SpaceText = Absent;
        SyncText = Absent;
        PortText = Absent;
        IsParsed = false;
        IsHopShape = false;
    }

    /// <summary>A preset the radio has NOT listed: its number, and "—" for
    /// everything the radio has not said (the third display state).</summary>
    public static ModemPresetRow Unlisted(int preset) => new(preset, false);

    /// <summary>The mirror's line, "PRESET" stripped — the change detector.
    /// Empty for an unlisted preset.</summary>
    public string RawLine { get; }

    /// <summary>ROUND 11 §6 — the LIST tab's STATE cell: "Enabled",
    /// "Disabled", or "—". It is the ONLY cell on this row that does not come
    /// from the line, because the enabled state is not on any line: it comes
    /// from the presence operation's atomically-committed enabled set.</summary>
    public string PresenceText { get; }

    public string NumberText { get; }
    public string NameText { get; }

    /// <summary>Everything after the name, VERBATIM. Unchanged by §8: the LIST
    /// tab renders only this, and it is the read-back's fallback cell.</summary>
    public string ParametersText { get; }

    // The §8 projection. Every one of these is "—" when the radio did not say
    // it — no column is ever invented.
    public string TypeText { get; }
    public string DataModeText { get; }
    public string BaudText { get; }
    public string InterleaveText { get; }
    public string MarkText { get; }
    public string SpaceText { get; }

    /// <summary>This row is the SHORT <c>HOP&gt;</c> form — no TYPE column and
    /// no INTER column, its mode phrase split into
    /// <see cref="SyncText"/> + <see cref="PortText"/> (audit round 1,
    /// MAJOR 3). False on every SSB row and on an unlisted one.</summary>
    public bool IsHopShape { get; }

    /// <summary>The SSB shape's own cells apply — i.e. the row parsed AND it
    /// is not the HOP form. What the read-back's Type and welded Port cells
    /// render on; the HOP form uses the two cells above instead.</summary>
    public bool ShowsType => IsParsed && !IsHopShape;

    /// <summary>The HOP form's SIGNALLING word (Async / Sync), "—" when the
    /// line does not carry one.</summary>
    public string SyncText { get; }

    /// <summary>The HOP form's PORT word (Data port / Remote port).</summary>
    public string PortText { get; }

    /// <summary>The shape's MANDATORY set all mapped — the trio type · port ·
    /// baud on an SSB row, the pair port · baud on a HOP one. False → the
    /// read-back renders ONE verbatim cell instead of the two parsed lines.</summary>
    public bool IsParsed { get; }

    /// <summary>The visible half of the fallback state (the house Has*/Is*
    /// pair — this codebase carries no inverting converter).</summary>
    public bool IsNotParsed => !IsParsed;

    /// <summary>Which optional cell line 2 carries, by the round-9 type map.
    /// Both false on an unparsed row, which renders no line 2 at all.</summary>
    public bool ShowsInterleave { get; }
    public bool ShowsMarkSpace { get; }

    /// <summary>The token following <paramref name="key"/>, case-insensitively.
    /// Shared with the editor's prefill so ONE reader parses the listing.</summary>
    internal static string? TokenAfter(string[] tokens, string key)
    {
        int i = Array.FindIndex(tokens, t => string.Equals(t, key, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < tokens.Length ? tokens[i + 1] : null;
    }

    /// <summary>The listing's data mode is TWO tokens ("ASYNC DATA"), keyed on
    /// whichever of ASYNC/SYNC appears.</summary>
    internal static string? DataModePhrase(string[] tokens)
    {
        int i = SyncIndex(tokens);
        return i >= 0 && i + 1 < tokens.Length ? tokens[i] + " " + tokens[i + 1] : null;
    }

    /// <summary>The SIGNALLING half of that phrase, on its own — the HOP form
    /// carries the two words as two independent fields (P5b), and the editor
    /// offers them as two rows, so the read-back reads them as two.</summary>
    internal static string? SyncToken(string[] tokens)
    {
        int i = SyncIndex(tokens);
        return i >= 0 ? tokens[i] : null;
    }

    /// <summary>The PORT half — the token after the ASYNC/SYNC one.</summary>
    internal static string? PortToken(string[] tokens)
    {
        int i = SyncIndex(tokens);
        return i >= 0 && i + 1 < tokens.Length ? tokens[i + 1] : null;
    }

    private static int SyncIndex(string[] tokens) => Array.FindIndex(tokens, t =>
        string.Equals(t, "ASYNC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "SYNC", StringComparison.OrdinalIgnoreCase));
}
