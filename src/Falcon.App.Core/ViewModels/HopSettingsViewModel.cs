using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The HOP mode-settings pane — the "Net programming" card. Round 4 (§AJ) gave
/// it TWO sub-tabs; round 5 (§BE/§BG, owner: "refactor net entry to match how
/// the radio works") rebuilds what is INSIDE the programming tab:
///
/// - <b>Net programming</b> (LEFT, the DEFAULT): the net-number picker, then
///   every field on ITS OWN ROW (BE1) — ID, type, value, actions — instead of
///   the packed side-by-side grid.
/// - <b>Net list</b> (RIGHT): one READ-ONLY row per net 0–9.
///
/// <para><b>The round-5 shape: RADIO-NATIVE, per-field writes (BG1).</b> The
/// <c>Store</c> button and the blue "Radio:" read-back strip are GONE. The wire
/// is natively per-field — <c>NETID n …</c>, <c>HOPTYPE n …</c>,
/// <c>HOPSET n …</c>, <c>HOPLIST n …</c> are four independent commands — so the
/// editor is too: each field commits its OWN command and then re-reads the net
/// with <c>DIS n</c>. Nothing batches, nothing is optimistic, and a partial
/// write is visible as exactly what the radio then reports.</para>
///
/// <para><b>Value controls are TYPE-SWITCHED off the CONFIRMED type (BG2).</b>
/// NB shows one "Center (MHz)" entry; WB shows "Low"/"High"; LIST shows the
/// list UI; a net whose type the radio has not reported shows NO value controls
/// at all. This is not decoration — it structurally enforces the wire's
/// ordering rule (protocol.md: HOPTYPE must be set BEFORE HOPSET/HOPLIST),
/// because the only way to reach a value control is through a type the radio
/// has already confirmed.</para>
///
/// <para><b>Prefill: the K5 carve-out (round-5 contract §2.4), not a
/// constitution breach.</b> This is a PROGRAMMING surface, so the ID, center
/// and edge entries INITIALIZE from the radio's confirmed reports and the type
/// segments highlight the REPORTED type (round 13 item 14, ruling 2026-08-20 —
/// the highlight is <see cref="ReportedType"/>, the GATES stay on
/// <see cref="ConfirmedType"/>). A populate GESTURE — tab landing,
/// picker spin, Refresh — resets the buffers to the reported values; a fresh
/// report populates only buffers the operator has NOT modified since that
/// gesture, so a half-typed frequency is never overwritten mid-edit. The
/// no-prefill rule stands unchanged everywhere else. (Round 3's X5 "no prefill
/// here either" is the rule K5 replaces, on the owner's ruling.)</para>
///
/// <para><b>MHz in, MHz out (K6/BD2).</b> Every frequency the operator sees or
/// types on this pane is MHz — the wire's kHz is an implementation detail of
/// <see cref="HopNetDisplay"/>, which owns both conversions and the shared
/// "Frequencies (MHz)" heading. A value outside K6 blocks the send with a note;
/// it must never reach the wire, because the radio SILENTLY IGNORES a badly
/// formed frequency (protocol.md).</para>
///
/// <para><b>Clear net (BG4).</b> <c>HOPSET n DEL</c> wipes the ENTIRE net
/// record — NETID included (probe R9b). It is surfaced behind a warning that
/// opens on EVERY press: this deliberately does NOT copy the Operate pane's
/// once-per-session accepted latch, because a destructive wipe re-confirms
/// every time. Opening the warning sends NOTHING.</para>
///
/// <para><b>READ PATH — the round-9 unified two-tier doctrine.</b></para>
/// <list type="bullet">
///   <item><b>The EDITOR reads its target FRESH on every landing.</b> An
///     editor landing is a picker SPIN, a programming-TAB landing, and the
///     surface first becoming READABLE this session (initial sight, and the
///     reconnect after a drop). Each sends <c>DIS n</c> for the PICKED net,
///     plus <c>HOPLIST n</c> when the mirror confirms that net is LIST (no
///     captured <c>DIS</c> answer carries a hoplist). This REVERSES the
///     round-5 "moving the picker sends nothing" ruling, on the channel
///     editor's rationale: a cached record can be older than the last write
///     from any source, and the operator is about to edit from it. The
///     wiped-net suppression still gates the list read.</item>
///   <item><b>The NET LIST tab is the LAZY tier.</b> Its FIRST landing this
///     session sends the whole-table <c>DIS</c> and re-arms the BC4 hoplist
///     once-set; after that it renders from the mirror. <b>No <c>DIS</c>-all
///     fires anywhere else</b> — in particular not on Ready, which is where
///     the round-5 load used to sit.</item>
///   <item>BC4 unchanged in kind: any net the mirror confirms LIST gets one
///     <c>HOPLIST n</c> per session, whichever read discovered it.</item>
/// </list>
/// <para><b>The pane's Refresh button is GONE</b> (round 9): every landing
/// re-reads, and Refresh buttons now exist only on expensive-bulk list
/// surfaces. READS and POPULATE GESTURES stay orthogonal — the
/// initial-sight/reconnect read is a READ, not a gesture, so typed text
/// survives it (the standing R7 pin).</para>
///
/// <para><b>Gate.</b> Ready + a CONFIRMED HOP mode — these are HOP-scoped
/// writes, sent at a <c>HOP&gt;</c> prompt.</para>
/// </summary>
public partial class HopSettingsViewModel : ObservableObject
{
    /// <summary>Nets are 0–9 (HopController.ValidateNet).</summary>
    public const int NetCount = 10;

    // ---- Round 14 A2: the hop-limits validation (plan/plan-round14.md §4-A2)
    // K6's entry domain (1.600–29.995 MHz) is the WHOLE pane's grammar and does
    // not move. This is the PER-FIELD tightening on top of it, and it is
    // deliberately two different KINDS of check:
    //
    //   REFUSAL — exactly ONE, on the WB band edges, because the refusal CLASS
    //   and the exact boundary are both BENCH-PROVEN (protocol.md: `HOPSET 9
    //   01995 03995` → the stored line + `** ERROR **`, record unchanged; the
    //   r13 `01700 08300` capture is the same shape). Constitution §3.1 allows
    //   a client refusal only where the wire refusal is proven — this is it,
    //   and nothing else on this pane qualifies.
    //
    //   ADVISORIES — everything MANUAL-DERIVED (and the one span fact whose
    //   boundary the bench located only to a range). They never block a send:
    //   they render in the note slot BESIDE a command that actually went out,
    //   so the radio stays the judge.
    //
    // The NB centre editor is deliberately NOT floor-checked: NB centres below
    // 2 MHz are manual-legal (the 1.6 MHz tier) and no bench capture refuses
    // one, so K6's 01600 domain is the honest bound there.

    /// <summary>The WB/LIST band floor in the wire's kHz — 2.000 MHz.
    /// BENCH-CONFIRMED at the boundary (protocol.md, P-1 run A S6).</summary>
    public const int HopBandFloorKHz = 2000;

    /// <summary>The WB span at and above which generation refuses, in kHz.
    /// The boundary is EXCLUSIVE on this radio: span exactly 2000 stored and
    /// then refused generation as <c>Bad_Hopset</c>, span 1000 generated
    /// (protocol.md, P-1 run A S5) — so the bench located it only to
    /// (1000, 2000], and 2000 itself is the one point that is measured.</summary>
    public const int SpanRefusesGenerationKHz = 2000;

    /// <summary>The manual's minimum WB bandwidth, in kHz — the HIGHER of the
    /// two numbers the manual gives itself (Table 1-5's "70 kHz to 2 MHz" vs
    /// §2.6.5.2(b)'s "at least 140 kHz wide"). Advising from the higher one is
    /// the conservative read of a conflict neither the bench nor the manual has
    /// settled.</summary>
    public const int MinimumSpanAdvisoryKHz = 140;

    /// <summary>The manual's LIST span limit, in kHz (lowest-to-highest
    /// ≤ 2.0 MHz). MANUAL-ONLY — no bench trial either way.</summary>
    public const int ListSpanAdvisoryKHz = 2000;

    /// <summary>The ONE new refusal, shared by BOTH WB edges (one class, one
    /// sentence). Carried through <see cref="Fail"/>, so it reaches the
    /// operator prefixed with the net it concerns, like every other refusal on
    /// this pane.</summary>
    public const string BelowHopFloorRefusal =
        "Below the hop band floor (2.000 MHz) — the radio refuses this at the write.";

    /// <summary>WB span ≥ 2 MHz. The one advisory that is NOT manual-derived —
    /// it is a bench capture whose boundary is only bracketed — so it carries
    /// no <c>Manual:</c> prefix and names its evidence instead.</summary>
    public const string SpanRefusesGenerationAdvisory =
        "Spans of 2 MHz and over refuse generation (Bad_Hopset — bench; manual Table 2-99).";

    /// <summary>WB span below the manual's minimum, which the manual states two
    /// ways — so the note says so rather than picking a winner.</summary>
    public const string MinimumSpanAdvisory =
        "Manual: minimum bandwidth 70–140 kHz (the manual self-conflicts) — the radio may refuse generation.";

    /// <summary>A LIST token below the floor. An ADVISORY, not the WB refusal:
    /// no bench trial has ever put a below-floor frequency into a HOPLIST, and
    /// §3.1 does not let the client refuse what the wire has not refused.</summary>
    public const string ListFloorAdvisory =
        "Manual: below the wide-band/list floor (2.000 MHz) — the radio may ignore or refuse it.";

    /// <summary>The LIST span limit, computed over the CONFIRMED stored list
    /// UNION the tokens being added — and skipped entirely when the stored list
    /// is unconfirmed, because a span over a guessed list is a guess.</summary>
    public const string ListSpanAdvisory =
        "Manual: list span exceeds 2 MHz — the radio may refuse generation.";

    /// <summary>The LIST add box's hint (§7's exact placeholder). It names the
    /// delimiter because the delimiter is now SPACE and nothing else.</summary>
    public const string ListAddPlaceholder = "e.g. 5.320 7.450 (MHz, space-separated)";

    // ---- Exclusion bands (round 11 §7, owner ruling R11 / scope amendment X9)
    // A NEW section, in the LIST-editor idiom the net's own frequency list
    // already uses: headed read-only rows with a per-row Remove, then an add
    // row. The table is GLOBAL (the wire's `EXC` names no net), which is why it
    // is its own card rather than a row inside the net editor.
    //
    // ALL WIRE SHAPES PROVISIONAL (plan §14) except the captured single-band
    // echo: the multi-band listing layout, the DEL echo variants and the band
    // bounds are patterned, not observed.

    /// <summary>Exclusion band slots are 0–9 (HopController.ValidateBand).</summary>
    public const int ExcludeBandCount = 10;

    /// <summary>The section caption, exact — it says the two things the wire
    /// does that no control on screen shows: the bands apply to WB nets, and
    /// writing one REGENERATES the current hopset.</summary>
    public const string ExcludeCaption =
        "Applies to WB nets. Changes regenerate the current hopset.";

    /// <summary>State 2 of three: the radio's answered-and-empty table. The
    /// UNREAD state is the hyphen ROW, not this.</summary>
    public const string NoExcludeBandsCaption = "No exclusion bands programmed.";

    /// <summary>Why Add is disabled with all ten slots used.</summary>
    public const string ExcludeFullReason = "All 10 bands used.";

    /// <summary>Why Add is disabled before the table has been read. Adding into
    /// an unknown table would have to GUESS a free slot, and guessing wrong
    /// overwrites a band the operator cannot see (invariant 6: the UI renders
    /// markers, it never invents).</summary>
    public const string ExcludeUnreadReason =
        "Waiting for the radio to report the exclusion bands.";

    /// <summary>The UNREAD row's marker text.</summary>
    public const string ExcludePlaceholderText = "—";

    // ---- Internal coupler (round 14 B; plan/plan-round14.md §4-B, R2) -------
    // The owner's ask, verbatim: "copy the SSB settings screen's control
    // buttons". So this is a LITERAL COPY of that row — same markup shape, same
    // choice builder, same highlight contract — reading and writing the SAME
    // Core mirror the SSB pane uses (RadioState.InternalCoupler). Two
    // placements, one confirmed value: they cannot disagree.
    //
    // Why it belongs on a HOP pane at all: wide-band and list nets do not
    // generate while the internal coupler is enabled (manual Table 1-5;
    // BENCH-PROVEN by P-1 run B — the same WB tuple answered `WB_Invalid`
    // enabled and generated `Hopnum 0101` bypassed). The operator meets that
    // restriction here, on the pane where WB nets are programmed, not two
    // screens away.
    //
    // ROUND 15 H-1 (owner): the card moves to the TOP of the pane and its
    // one-line advisory `CouplerCaption` is DELETED. The restriction is real
    // and still documented (docs/ui.md), but a standing paragraph on a one-row
    // card is furniture — and the card now leads the pane, which is where an
    // operator meets it before programming anything.

    private readonly HopSurface _hop;
    private readonly RadioSession _session;

    /// <summary>Round 14 C: the coupler convergence policy, told about this
    /// pane's coupler press so the operator's value becomes the policy's
    /// baseline (plan §4-C, owner ruling R10). Optional for the same reason as
    /// on the surfaces — the app's composition always supplies it.</summary>
    private readonly CouplerPolicy? _coupler;

    /// <summary>The NET LIST tab's lazy once-per-session gate — and nothing
    /// else since round 9. The whole-table <c>DIS</c> belongs to that tab
    /// alone; it no longer fires on Ready, because the surface the operator
    /// actually lands on is the EDITOR, which reads its own picked net.</summary>
    private bool _listTabLoadedThisSession;

    /// <summary>Whether the EDITOR's initial-sight read has gone out this
    /// session. The surface first becoming READABLE — initial sight, and the
    /// reconnect after a drop — is an editor landing under the round-9
    /// doctrine, so it reads the PICKED net. It is edge-detected here rather
    /// than off the session phase because readability is Ready AND a confirmed
    /// HOP mode, and the mode arrives as a mirror event.</summary>
    private bool _sightReadThisSession;

    /// <summary>BC4: the nets whose <c>HOPLIST n</c> has gone out this session.
    /// Scoped to ANY mirrored LIST net, because the list tab renders all ten.
    /// Re-armed by the list tab's lazy load (which re-reads every net) and
    /// cleared by a session drop. The Operate pane owns its own once-set for
    /// the current net alone — the round-4 "owned separately" precedent; a
    /// duplicate read across panes is an accepted cheap read.</summary>
    private readonly HashSet<int> _hopListsQueried = [];

    /// <summary>The "Net list" tab's ten READ-ONLY rows. They carry no
    /// commands and no buffers — display only.</summary>
    public IReadOnlyList<HopNetListRow> Rows { get; }

    /// <summary>The PICKED net's stored LIST frequencies, one removable row
    /// each (BG3). Empty for every other type, and for a LIST net whose
    /// <c>HOPLIST</c> answer has not landed yet.</summary>
    public ObservableCollection<HopFrequencyRow> ListRows { get; } = [];

    // ---- Sub-tab view state -----------------------------------------------
    // APP-SIDE view state, the AleViewModel sub-tab idiom. Since round 9 a
    // tab LANDING also fires its tier's read (the unified doctrine): the
    // programming tab re-reads the picked net, the list tab's first landing
    // per session sends the whole-table DIS. The tab STATE itself is still
    // pure view state. Programming is the DEFAULT, so this starts false.

    [ObservableProperty] private bool isListTabOpen;

    /// <summary>R7-review MAJOR 1a: landing back on the programming tab IS a
    /// populate gesture (the SSB editor's rule) — typed text clears over the
    /// placeholders. Round 9: it is also an EDITOR LANDING, so it re-reads the
    /// picked net.</summary>
    [RelayCommand]
    private void OpenProgrammingTab()
    {
        IsListTabOpen = false;
        ReadPickedNet();
        Refresh(populateGesture: true);
    }

    /// <summary>The LIST tab is the LAZY tier (round 9): its FIRST landing
    /// this session sends the whole-table <c>DIS</c> and re-arms the BC4
    /// hoplist once-set, so every net the dump confirms LIST is fetched again;
    /// after that the tab renders from the mirror. Nothing else in this pane
    /// sends a <c>DIS</c>-all any more.
    /// <para>The wipe suppressions deliberately SURVIVE this load (C2 audit
    /// round 2): the <c>DIS</c>-all re-confirms every net's true type, and a
    /// wiped net un-suppresses on its OWN answer, not because the operator
    /// opened a tab.</para></summary>
    [RelayCommand]
    private void OpenListTab()
    {
        IsListTabOpen = true;
        if (!HopReady || _listTabLoadedThisSession) return;
        _listTabLoadedThisSession = true;
        // D1 QUIESCE (§4 SUPPRESSION SCOPE — tab opens defer too): the tab
        // opens and renders from the mirror; the DIS-all runs at campaign end.
        if (_campaign?.CampaignActive == true) { _allNetsReadOwed = true; Refresh(); return; }
        _hopListsQueried.Clear();
        _hop.RequestAllNets();
        Refresh();
    }

    /// <summary>Settle the deferred reads, once each, and ONLY while this pane's
    /// own gate allows them — otherwise they stay owed and the next HOP
    /// confirmation pays them (audit round 1). Called from
    /// <see cref="Refresh"/>, which is the pane's every-event recompute, so
    /// "the next moment this pane is readable" is exactly when it runs.
    /// <para>The picked-net landing goes out BEFORE
    /// <see cref="EnsureHopListsLoaded"/>'s scan for the reason
    /// <see cref="ReadPickedNet"/> documents: marking the net in the BC4
    /// once-set here is what stops a SECOND, identical <c>HOPLIST n</c>.</para></summary>
    private void PayWhatIsOwed()
    {
        if (_campaign?.CampaignActive == true || !HopReady) return;

        if (_allNetsReadOwed)
        {
            _allNetsReadOwed = false;
            _hopListsQueried.Clear();
            _hop.RequestAllNets();
        }
        if (_pickedNetReadOwed) ReadPickedNet();      // clears the latch itself
    }

    // ---- Net picker -------------------------------------------------------
    // An APP-SIDE view cursor (0–9, wrapping): which net the operator is
    // LOOKING at, and the net every write on this pane addresses. It IS a K5
    // populate gesture: landing on a net resets the buffers to that net's
    // reported values. Round 9 REVERSES the round-5 "the picker sends
    // nothing" ruling for this editor: a landing also reads the landed net
    // fresh (see ReadPickedNet).

    private int _pickedNet;
    public int PickedNet => _pickedNet;

    [ObservableProperty] private string pickedNetText = "0";

    [RelayCommand] private void NetUp() => MovePicker(+1);

    [RelayCommand] private void NetDown() => MovePicker(-1);

    private void MovePicker(int delta)
    {
        _pickedNet = (_pickedNet + delta + NetCount) % NetCount;
        PickedNetText = _pickedNet.ToString(CultureInfo.InvariantCulture);
        InputError = "";                 // the note named the net we just left
        // Round 10 §5: nothing to close — a moved picker does NOT retract an
        // open Clear-net prompt, because the prompt already NAMED its net and
        // the captured net is what the answer applies to.
        ReadPickedNet();                 // round 9: a landing reads the target
        Refresh(populateGesture: true);  // K5: landing repopulates the buffers
    }

    /// <summary>The round-9 EDITOR LANDING read: <c>DIS n</c> for the picked
    /// net, plus <c>HOPLIST n</c> when the mirror already confirms that net is
    /// LIST (no captured <c>DIS</c> answer carries a hoplist). Fresh EVERY
    /// landing — the channel editor's rationale.
    /// <para>The wiped-net suppression still applies: a net whose wipe has not
    /// been reported yet is not asked for a list the radio has already
    /// erased.</para>
    /// <para>Called BEFORE <see cref="Refresh"/> on purpose: marking the net
    /// in the BC4 once-set here is what stops <see cref="EnsureHopListsLoaded"/>
    /// issuing a SECOND, identical <c>HOPLIST n</c> in the same
    /// gesture.</para></summary>
    private void ReadPickedNet()
    {
        // D1 QUIESCE (audit round 1): the debt is settled by this pane's own
        // gate, never by the campaign's edge. `!HopReady` leaves it OWED — a
        // campaign that ended in SSB must not consume a landing this pane
        // cannot perform — and the next HOP confirmation pays it.
        if (!HopReady) return;
        // Every caller of this landing — the sight edge, a picker spin — defers
        // here while a campaign owns the wire. Nothing greys out.
        if (_campaign?.CampaignActive == true) { _pickedNetReadOwed = true; return; }
        _pickedNetReadOwed = false;
        int net = _pickedNet;
        _hop.RequestNet(net);

        // The coupler rides the SAME editors-read-fresh tier (round 14 B) — no
        // new tier and no new gate. It is the pane's read, not the picker's,
        // exactly like `EXC` below. Without it the row would highlight only
        // after somebody had visited the SSB settings pane, and a control whose
        // state arrives from another screen reads "—" exactly when it matters.
        //
        // BEFORE the exclusion read, deliberately: `EXC` is SENTINEL-BRACKETED,
        // and slipping an unrelated query between the read and its closing
        // sentinel would put a line inside a bracket that exists to say where
        // the answer ended.
        _hop.QueryInternalCoupler();

        // §7/§10: the exclusion table rides the SAME editors-read-fresh tier.
        // It is not per-net (the wire's `EXC` names no net), but the tier is
        // the pane's, not the picker's — and the Core read coalesces, so a
        // request arriving while one is on the wire adds nothing to it.
        _hop.RequestExcludeBands();

        if (_hop.Nets.TryGetValue(net, out var picked)
            && picked is { IsReportedUnprogrammed: false, Type: HopType.List }
            && !_wipedNetsAwaitingReport.Contains(net))
        {
            _hopListsQueried.Add(net);
            _hop.RequestHopList(net);
        }
    }

    // ---- Internal coupler row (round 14 B) ---------------------------------

    /// <summary>Bypass / Enable, rebuilt on every <see cref="Refresh"/> — so a
    /// coupler report landing from ANY source (this row, the SSB row, or the
    /// radio answering somebody else's query) relights the buttons here: the
    /// surface WATCHES <c>RadioProperty.InternalCoupler</c>, and its Changed
    /// event is already wired to Refresh.</summary>
    [ObservableProperty] private IReadOnlyList<ChoiceItem> internalCouplerChoices = [];

    /// <summary>Enable/Bypass pair off the verbatim <c>INTCOUPLER</c> mirror.
    ///
    /// <para><b>A DELIBERATE DUPLICATE.</b> The other copy is
    /// <c>SsbSettingsViewModel.ProvisionalBypassChoices</c>
    /// (SsbSettingsViewModel.cs, the "TX / antenna" group), which also serves
    /// the PREAMP row. Round 14 B copied these four lines rather than extract a
    /// shared helper: the plan's minimal-scope rule keeps the SSB VM's surface
    /// exactly as wide as it was, and a two-line list literal is cheaper to
    /// read in both places than a third home for it. <b>If the report
    /// spellings ever change, BOTH copies change together</b> — that is the
    /// cost this comment exists to make visible, and the same note stands
    /// beside the SSB copy.</para>
    ///
    /// <para>The compare matches the REPORT form only. The SET spelling is
    /// <c>BYPASS</c>/<c>ENABLE</c> and the report is
    /// <c>BYPASSED</c>/<c>ENABLED</c> (P-1 captured both, docs/protocol.md);
    /// the parser uppercases the radio's mixed-case <c>INTCoupler Enabled</c>
    /// before dispatch, so the caps comparison is the right one. Anything else
    /// the radio might answer highlights NOTHING, which is the honest
    /// outcome.</para>
    /// <para>ROUND 15 H-2: <b>Enable before Bypass</b> — the affirmative on the
    /// LEFT (docs/ui.md's constitution rule, pinned by
    /// <c>ChoiceOrderGuardTests</c>). WIRE-NEUTRAL: the setter parses the
    /// BUTTON LABEL, so re-ordering the list changes which button is drawn
    /// first and nothing else. The SSB copy flips with it.</para>
    /// </summary>
    private static IReadOnlyList<ChoiceItem> ProvisionalBypassChoices(
        Confirmed<string> mirror, Action<string> select) =>
    [
        new ChoiceItem("Enable", mirror.IsConfirmed && mirror.Value == "ENABLED", select),
        new ChoiceItem("Bypass", mirror.IsConfirmed && mirror.Value == "BYPASSED", select),
    ];

    /// <summary>The row's press. NO RE-CLICK GUARD, inherited from the copied
    /// idiom (the ui.md provisional-trio contract): pressing the LIT button
    /// still sends. Suppressing a send because the mirror already agrees would
    /// be the app deciding the radio has nothing to say, and this is the one
    /// control an operator reaches for precisely when they doubt the state.
    /// <para>Gated on the pane's own gate and nothing more — the write is not
    /// prompt-scoped (the family is PROMPT-FREE, P-1 run C), but the pane it
    /// sits on is HOP-only, so the gate is <see cref="HopReady"/> like every
    /// other send here.</para></summary>
    private void SetInternalCoupler(string label)
    {
        if (!HopReady || !Enum.TryParse<BypassEnable>(label, ignoreCase: true, out var state)) return;
        // ROUND 14 C (plan §4-C, owner ruling R10): the same one-liner the SSB
        // row carries — an EXPLICIT operator set moves the policy's baseline,
        // so the policy converges toward what the operator chose instead of
        // undoing them. Reported, not mirror-inferred.
        _coupler?.NotifyOperatorCouplerWrite(state);
        _hop.SetInternalCoupler(state);
    }

    // ---- Gate + notes -----------------------------------------------------

    [ObservableProperty] private bool areControlsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    private string disabledReason = "";

    public bool HasDisabledReason => !string.IsNullOrEmpty(DisabledReason);

    /// <summary>Client-side validation note (the ALE numeric idiom), prefixed
    /// with the net it concerns. Cleared on a valid send. Radio-side
    /// rejections still surface via the error toast.
    /// <para>Round 14 A2 gives this ONE slot a second tenant: the MANUAL-DERIVED
    /// ADVISORIES. A REFUSAL always wins it (nothing went out, so the note is
    /// the whole story); an advisory renders only when the send was otherwise
    /// legal and therefore ACCOMPANIES a command that actually went out.
    /// Advisories carry their own <c>Manual:</c> prefix instead of the net
    /// prefix — they describe the VALUE, not the net.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    // ---- Input buffers (round-7 DB; round-8 EA: blue read displays) -------
    // Two-way is legal here: this is OPERATOR input — and since round 7 it is
    // ONLY ever operator input. Round 8 (EA, owner): the reported value moved
    // from the placeholder to an INERT accent ValueDisplay beside each entry
    // (the RF-gain idiom), so the placeholders are plain FORMAT HINTS (XAML
    // constants) and the reported value renders exactly ONCE per field. The
    // round-7 send-time rule is unchanged: an EMPTY field falls back to the
    // reported value backing its display; nothing reported refuses.

    [ObservableProperty] private string netIdInput = "";
    /// <summary>NB center, in MHz (K6).</summary>
    [ObservableProperty] private string centerInput = "";
    /// <summary>WB band low edge, in MHz (K6).</summary>
    [ObservableProperty] private string lowInput = "";
    /// <summary>WB band high edge, in MHz (K6).</summary>
    [ObservableProperty] private string highInput = "";
    /// <summary>LIST add box — one or more MHz frequencies, space separated.
    /// No placeholder VALUE and no fallback: there is nothing to re-send
    /// (the stored list renders as rows), so its placeholder is a hint.</summary>
    [ObservableProperty] private string listAddInput = "";

    /// <summary>EA: the blue per-field read displays — the CONFIRMED report in
    /// the shared display vocabulary (MhzText's verbatim-kHz fallback
    /// included), "—" until reported. Display only: what the FALLBACK may
    /// re-send stays the separately-validated backing below, so an odd
    /// reported value shows honestly here while backing nothing.</summary>
    [ObservableProperty] private string netIdDisplayText = "—";
    [ObservableProperty] private string centerDisplayText = "—";
    [ObservableProperty] private string lowDisplayText = "—";
    [ObservableProperty] private string highDisplayText = "—";

    // The reported values the placeholders are backed by, kept in the WIRE
    // form a fallback commit sends (ID digits; 5-digit kHz).
    private string? _netIdBacking, _centerBacking, _lowBacking, _highBacking;

    // ---- The picked net's CONFIRMED type (BG2/BG5) ------------------------
    // Radio state, not input: it drives every GATE on this pane — which value
    // controls exist, what the value commands will let out, and the SetType
    // re-click guard. Null until a Hoptype line has covered this net AND the
    // net is not reported-unprogrammed. Since round 13 (item 14, ruling
    // 2026-08-20) it no longer drives the segment HIGHLIGHT: that is
    // ReportedType below, deliberately unsuppressed.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNarrowbandConfirmed))]
    [NotifyPropertyChangedFor(nameof(IsWidebandConfirmed))]
    [NotifyPropertyChangedFor(nameof(IsListConfirmed))]
    [NotifyPropertyChangedFor(nameof(HasConfirmedType))]
    [NotifyPropertyChangedFor(nameof(HasNoConfirmedType))]
    private HopType? confirmedType;

    public bool IsNarrowbandConfirmed => ConfirmedType == HopType.Narrowband;
    public bool IsWidebandConfirmed => ConfirmedType == HopType.Wideband;
    public bool IsListConfirmed => ConfirmedType == HopType.List;

    /// <summary>False → NO value controls at all (BG2): the radio has not said
    /// what this net is, so there is no legal value to enter.</summary>
    public bool HasConfirmedType => ConfirmedType is not null;

    /// <summary>The visible half of the no-type state (the codebase carries no
    /// inverting converter — the Has* pair is the house idiom).</summary>
    public bool HasNoConfirmedType => ConfirmedType is null;

    // ---- The picked net's REPORTED type (round 13 item 14) ----------------
    // The HIGHLIGHT signal, and ONLY that — deliberately separate from
    // ConfirmedType above. Owner ruling 2026-08-20 (plan/plan-round13.md §2):
    // the segments ALWAYS show the type the radio reported for the picked net,
    // net ID or not, so programming a net's type lights a segment the moment
    // the Hoptype echo lands instead of waiting for a NETID report that may
    // never come. It carries NO IsReportedUnprogrammed suppression — which the
    // ruling names as its accepted downside: a wiped net reports WB, so a
    // wiped net shows WB lit.
    //
    // Nothing else moves. ConfirmedType keeps gating which value controls
    // exist, what CanWriteValueOfType lets out, and the SetType re-click
    // guard, so on a net whose type is reported-but-unconfirmed a press on the
    // LIT segment still sends (a harmless duplicate HOPTYPE) — the behaviour
    // change is highlight-only, by design.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNarrowbandReported))]
    [NotifyPropertyChangedFor(nameof(IsWidebandReported))]
    [NotifyPropertyChangedFor(nameof(IsListReported))]
    private HopType? reportedType;

    public bool IsNarrowbandReported => ReportedType == HopType.Narrowband;
    public bool IsWidebandReported => ReportedType == HopType.Wideband;
    public bool IsListReported => ReportedType == HopType.List;

    /// <summary>True when a LIST net has no stored frequencies to show yet —
    /// the caption that stops the list UI reading as an empty box.</summary>
    [ObservableProperty] private bool hasNoListFrequencies = true;

    // ---- Exclusion-band section state (§7) --------------------------------

    /// <summary>The exclusion table, straight from the Core mirror — the
    /// display projection of its three states (invariant 6):
    /// <list type="bullet">
    ///   <item>UNREAD (mirror null) — EXACTLY ONE hyphen row.</item>
    ///   <item>READ-EMPTY — no rows, and <see cref="HasNoExcludeBands"/> puts
    ///     <see cref="NoExcludeBandsCaption"/> on screen.</item>
    ///   <item>rows — the radio's own band slots, in its listing order.</item>
    /// </list>
    /// The unread/read-empty distinction exists ONLY because the read is
    /// sentinel-bracketed: an empty table answers nothing at all.</summary>
    [ObservableProperty]
    private IReadOnlyList<HopExcludeRow> excludeDisplayRows = [HopExcludeRow.Placeholder];

    /// <summary>The READ-EMPTY state only — never the unread one.</summary>
    [ObservableProperty] private bool hasNoExcludeBands;

    /// <summary>Add-row low edge, in MHz (the same K6 grammar as every other
    /// frequency on this pane).</summary>
    [ObservableProperty] private string excludeLowInput = "";

    /// <summary>Add-row high edge, in MHz.</summary>
    [ObservableProperty] private string excludeHighInput = "";

    /// <summary>The section's own client-validation note. SEPARATE from
    /// <see cref="InputError"/>, which prefixes the picked NET — a global table
    /// has no net to name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExcludeError))]
    private string excludeError = "";

    public bool HasExcludeError => !string.IsNullOrEmpty(ExcludeError);

    /// <summary>Why Add is unavailable — "" when it is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddExcludeBandDisabledReason))]
    private string addExcludeBandDisabledReason = "";

    public bool HasAddExcludeBandDisabledReason
        => !string.IsNullOrEmpty(AddExcludeBandDisabledReason);

    // ---- Clear net confirm flow (BG4; round 10 §5 — a POPUP) --------------
    // The inline Proceed/Cancel strip and its warning box are DELETED. The
    // question is now a two-button popup raised through IConfirmationPrompt
    // (the VM never touches MAUI UI — invariant 2), and the §5 lifecycle
    // contract applies in full: the NET is captured at PRESS and is what gets
    // wiped even if the picker moved while the prompt was open (the popup
    // NAMES the net, so the operator answered about that one); cancel sends
    // nothing; accept sends once; the send gate is re-checked in the body
    // after the await; a faulted or cancelled prompt sends nothing and does
    // not wedge; and there is still no accepted latch, so every press asks.

    /// <summary>The §5 prompt table's Clear-net row, exact.</summary>
    public const string ClearNetTitleFormat = "Clear net {0}?";

    /// <summary>Round 11 §7 moves the round-10 message pin HERE: the wipe also
    /// RESETS THE TYPE TO WB (protocol.md — a wiped net reports
    /// <c>Hoptype WB</c>), which the old wording left the operator to discover.
    /// The §5 lifecycle contract around the prompt is unchanged.
    /// <para>ROUND 15 E-4: WORDING ONLY. The unified confirmation vocabulary
    /// (ui.md's constitution) makes the message one or two sentences that begin
    /// "The radio " and state what the radio does; the em-dashed aside became
    /// a plain conjunction. Nothing about the prompt or the wipe changed.</para></summary>
    public const string ClearNetMessage =
        "The radio wipes this net's ID, type and frequencies and resets the type to WB.";

    public const string ClearNetAccept = "Clear";

    public const string PromptCancel = "Cancel";

    /// <summary>Nets whose wipe has gone out but whose <c>DIS n</c> answer has
    /// not landed: the mirror still calls each one LIST, and re-querying its
    /// (now erased) hoplist in that window is what re-armed the stale rows.
    /// <para>A SET, not a scalar (C2 audit round 2): wipes overlap in practice
    /// — the operator can wipe two nets faster than the radio answers one — and
    /// a scalar let the second wipe un-suppress the first. Each net leaves this
    /// set on ITS OWN report; nothing else clears it but a session drop.</para></summary>
    private readonly HashSet<int> _wipedNetsAwaitingReport = [];

    private readonly IConfirmationPrompt _prompt;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 4). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>The picked-net landing read (<c>DIS n</c> + <c>INTCOUPLER</c> +
    /// <c>EXC</c> + a LIST net's <c>HOPLIST n</c>) deferred to the campaign's
    /// end. One flag for the gesture, so however many landings were deferred,
    /// one read goes out.</summary>
    private bool _pickedNetReadOwed;

    /// <summary>The List tab's <c>DIS</c>-all deferred to the campaign's
    /// end.</summary>
    private bool _allNetsReadOwed;

    public HopSettingsViewModel(
        HopSurface hop, RadioSession session, IConfirmationPrompt prompt,
        CouplerPolicy? coupler = null, ICampaignSignal? campaign = null)
    {
        _hop = hop;
        _session = session;
        _prompt = prompt;
        _coupler = coupler;
        _campaign = campaign;
        Rows = [.. Enumerable.Range(0, NetCount).Select(n => new HopNetListRow(n))];

        // The campaign's END edge runs the recompute; Refresh settles what is
        // owed if this pane can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        hop.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            // A dropped session forgets the load flags AND the stale note; the
            // next Ready session may be a different radio.
            if (_session.Phase != SessionPhase.Ready)
            {
                _listTabLoadedThisSession = false;
                _sightReadThisSession = false;
                // Session-scoped like the flags beside them: reads deferred for
                // a radio that has gone are not owed to the next one.
                _pickedNetReadOwed = false;
                _allNetsReadOwed = false;
                _hopListsQueried.Clear();
                _wipedNetsAwaitingReport.Clear();
                InputError = "";
                // …and the exclusion section's note, for the same reason: it
                // describes a radio that is gone. The ROWS need no clearing —
                // Core's own ResetForConnect puts the mirror back to unread,
                // and UpdateExcludeSection renders the hyphen row from that.
                ExcludeError = "";
            }
            Refresh();
        };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool HopReady => Ready && _hop.IsHopConfirmed;

    private void Refresh(bool populateGesture = false)
    {
        // The EDITOR's initial-sight read (round 9): the surface first
        // becoming READABLE this session — initial sight, and the reconnect
        // after a drop — is an editor landing, so it reads the PICKED net,
        // not every net. Edge-detected here because readability is Ready AND
        // a confirmed HOP mode, and the mode arrives as a mirror event.
        // R7-review MAJOR 1b: this is NOT a populate gesture — it fires on
        // RECONNECT, and the standing pin says a session drop (and therefore
        // the reconnect that follows) preserves the operator's typed text.
        // Only the operator's own gestures (spin, tab landing) clear it.
        if (HopReady && !_sightReadThisSession)
        {
            _sightReadThisSession = true;
            ReadPickedNet();
        }

        // …and whatever a campaign deferred, settled on the same recompute.
        PayWhatIsOwed();

        AreControlsEnabled = HopReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_hop.IsHopConfirmed ? "Net programming is HOP-scoped — waiting for the radio to confirm HOP."
            : "";

        var nets = _hop.Nets;
        var lists = _hop.HopLists;

        EnsureHopListsLoaded(nets);

        foreach (var row in Rows)
            row.Apply(Describe(nets, lists, row.Number));

        nets.TryGetValue(_pickedNet, out var picked);
        // A net the radio REPORTED unprogrammed has no usable type: its
        // Hoptype line reads WB on a wiped record (protocol.md), which is a
        // property of the wipe, not a programmed band. Treating it as WB would
        // offer band-edge entries for a net that has nothing — so the value
        // controls stay hidden until a real type report.
        ConfirmedType = picked is { IsReportedUnprogrammed: false } ? picked.Type : null;
        // …and the HIGHLIGHT signal, unsuppressed (round 13 item 14, ruling
        // 2026-08-20): whatever the radio last said this net's type is. Null
        // only when there is no picked row at all, or none of its lines has
        // carried a type yet.
        ReportedType = picked?.Type;

        PopulateBuffers(picked, populateGesture);
        UpdateListRows(lists);
        UpdateExcludeSection();

        // Round 14 B: the coupler row, off the shared mirror. Rebuilt here so
        // the highlight follows the radio's echo whichever pane (or which
        // query) produced it.
        InternalCouplerChoices = ProvisionalBypassChoices(_hop.InternalCoupler, SetInternalCoupler);

        CommitNetIdCommand.NotifyCanExecuteChanged();
        AddExcludeBandCommand.NotifyCanExecuteChanged();
        RemoveExcludeBandCommand.NotifyCanExecuteChanged();
        SetTypeCommand.NotifyCanExecuteChanged();
        CommitCenterCommand.NotifyCanExecuteChanged();
        CommitBandEdgesCommand.NotifyCanExecuteChanged();
        AddListFrequenciesCommand.NotifyCanExecuteChanged();
        RemoveListFrequencyCommand.NotifyCanExecuteChanged();
        RequestNetWipeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>BC4 — one <c>HOPLIST n</c> per mirrored LIST net per session.
    /// The trigger is the CONFIRMED type from the dump, never a guess, and it
    /// covers all ten nets because the list tab renders all ten.</summary>
    private void EnsureHopListsLoaded(IReadOnlyDictionary<int, HopNet> nets)
    {
        if (!HopReady) return;

        // A net just wiped is still LIST in the mirror until its DIS answer
        // lands. Re-querying it in that window would ask for a list the radio
        // has already erased — and put the net back in the once-set, which is
        // what let the erased rows render.
        //
        // PER NET, and each on its OWN report (C2 audit round 2, MAJOR). This
        // was a single scalar, which failed two ways: a second wipe overwrote
        // the first net's suppression (the auditor wiped 3 then 4 and watched
        // HOPLIST 3 go back out), and Refresh cleared it outright. Suppression
        // ends for one net when THAT net's mirror entry stops reporting LIST —
        // its wipe has been reported — and for no other reason short of a
        // session drop. Refresh deliberately does NOT clear the set: its
        // DIS-all re-confirms every net's true type, and each net then
        // un-suppresses on its own answer.
        if (_wipedNetsAwaitingReport.Count > 0)
        {
            _wipedNetsAwaitingReport.RemoveWhere(
                n => !nets.TryGetValue(n, out var wiped) || wiped.Type != HopType.List);
        }

        // D1 QUIESCE (§4 per-producer correction — the INDEPENDENT hoplist path
        // gets its own check): nothing joins the once-set while a campaign owns
        // the wire, so every LIST net stays owed and the campaign-end Refresh
        // re-enters here.
        if (_campaign?.CampaignActive == true) return;

        foreach (var (number, net) in nets)
        {
            if (net.Type != HopType.List) continue;
            if (_wipedNetsAwaitingReport.Contains(number)) continue;
            if (!_hopListsQueried.Add(number)) continue;
            _hop.RequestHopList(number);
        }
    }

    /// <summary>Round 7 (DB) / round 8 (EA): the reported values go to the
    /// blue READ DISPLAYS — entries are never written from a report. A
    /// populate GESTURE (the picker spin) additionally CLEARS the entry text,
    /// so typing never carries over onto a different net; a report alone
    /// updates only the displays, which hold nothing of the operator's.</summary>
    private void PopulateBuffers(HopNet? net, bool gesture)
    {
        if (gesture)
        {
            NetIdInput = "";
            CenterInput = "";
            LowInput = "";
            HighInput = "";
            ListAddInput = "";
        }

        // An unprogrammed net has no values to offer; its X-form ID is a
        // report, not something a commit could send back.
        bool programmed = net is { IsReportedUnprogrammed: false };

        // R7-review MAJOR 2: a backing is only a backing if the fallback
        // could legally SEND it — a malformed report (garbage center, odd ID)
        // must render as a hint, not a value-tinted emptiness that throws in
        // the Core builder when committed.
        _netIdBacking = programmed ? SendableNetId(net!.NetId) : null;
        _centerBacking = programmed ? SendableKHz(net!.CenterKHz) : null;
        _lowBacking = programmed ? SendableKHz(net!.WidebandLowKHz) : null;
        _highBacking = programmed ? SendableKHz(net!.WidebandHighKHz) : null;

        // EA: the displays render the REPORT through the shared display
        // vocabulary (the NetId cell handles the unreported and unprogrammed
        // states; MhzText keeps its verbatim-kHz fallback) — not the sendable
        // backing, so an odd reported value still shows.
        NetIdDisplayText = HopNetDisplay.Describe(net, null).NetId;
        CenterDisplayText = net?.CenterKHz is { } c ? HopNetDisplay.MhzText(c) : "—";
        LowDisplayText = net?.WidebandLowKHz is { } lo ? HopNetDisplay.MhzText(lo) : "—";
        HighDisplayText = net?.WidebandHighKHz is { } hi ? HopNetDisplay.MhzText(hi) : "—";
    }

    /// <summary>A reported net ID the fallback may re-send: exactly 8 digits
    /// (the same rule CommitNetId applies to typed text). Anything else —
    /// including a shape the parser let through — backs nothing.</summary>
    private static string? SendableNetId(string? reported)
        => reported is { Length: 8 } id && id.All(char.IsAsciiDigit) ? id : null;

    /// <summary>A reported kHz value the fallback may re-send: a parseable
    /// integer in the wire's legal band (protocol.md 01600–29995, 5 kHz
    /// step) — the same constraints the typed path enforces via
    /// TryParseMhz.</summary>
    private static string? SendableKHz(string? reported)
        => reported is not null
            && int.TryParse(reported, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kHz)
            && kHz is >= 1600 and <= 29995 && kHz % 5 == 0
            ? reported : null;


    /// <summary>The picked net's LIST rows — but ONLY for a net this pane has
    /// actually queried this session (C2 audit round 1, MAJOR). The mirror is
    /// the radio's last word, and after a wipe that word is known-stale: the
    /// wipe erases the list radio-side and produces no HOPLIST answer to
    /// replace the mirrored one. Gating on the BC4 once-set makes "have we
    /// asked since?" the render condition, so erased frequencies can never
    /// appear with a Remove button beside them.
    /// <para>ACCEPTED, and small: between the re-armed <c>HOPLIST n</c> going
    /// out and its answer landing, the net IS in the once-set again while the
    /// mirror still holds the pre-wipe list, so those rows can show for that
    /// round trip. The answer replaces the whole list atomically
    /// (<c>HopState.SetHopList</c>), so it self-corrects — and Remove against
    /// a frequency the radio no longer has is a no-op the following re-read
    /// settles. Closing the window properly needs a Core "list unconfirmed"
    /// marker, which is an §2.2.4 escalation, not a local fix.</para></summary>
    private void UpdateListRows(IReadOnlyDictionary<int, IReadOnlyList<string>> lists)
    {
        var freqs = ConfirmedType == HopType.List
            && _hopListsQueried.Contains(_pickedNet)
            && lists.TryGetValue(_pickedNet, out var stored) ? stored : [];

        HasNoListFrequencies = freqs.Count == 0;

        // Rebuild only when the content actually changed: the rows carry a
        // Remove command each, and re-creating them on every mirror event
        // would restart the list UI under the operator's finger.
        if (ListRows.Count == freqs.Count)
        {
            bool same = true;
            for (int i = 0; i < freqs.Count; i++)
                if (ListRows[i].FrequencyKHz != freqs[i]) { same = false; break; }
            if (same) return;
        }

        ListRows.Clear();
        foreach (var f in freqs) ListRows.Add(new HopFrequencyRow(f, RemoveListFrequencyCommand));
    }

    /// <summary>Render one net from the mirror through the SHARED vocabulary
    /// (<see cref="HopNetDisplay.Describe"/>) — the same call the Operate
    /// pane's "Current net" row makes, so one mirror state cannot render two
    /// ways. Round 5 moved the value forms in there too (BD2): the column is
    /// headed "Frequencies (MHz)" and shows bare MHz numbers — a center, a WB
    /// band "low–high", or a LIST net's frequency count.</summary>
    private static (string NetId, string Type, string Value) Describe(
        IReadOnlyDictionary<int, HopNet> nets,
        IReadOnlyDictionary<int, IReadOnlyList<string>> lists,
        int number)
        => HopNetDisplay.Describe(
            nets.TryGetValue(number, out var net) ? net : null,
            lists.TryGetValue(number, out var freqs) ? freqs : null);

    // ---- Per-field writes (BG1) -------------------------------------------
    // Each command is its own radio command plus the DIS n re-read, and
    // NOTHING else. In-body guards repeat CanExecute: Execute ignores it.

    private bool CanWrite() => HopReady;

    /// <summary>The gate every VALUE write needs (C2 audit round 1, MAJOR).
    /// Session readiness and input syntax were checked; the net's CONFIRMED
    /// TYPE was not — so <c>CommitCenter</c> would send <c>HOPSET n 11565</c>
    /// at a net the radio had just confirmed as LIST. The wire's rule is
    /// type-before-value (protocol.md), and BG2's whole point is that the
    /// value controls are REACHABLE only through a confirmed type; the XAML
    /// enforced that by visibility alone, which is a rendering fact, not a
    /// sending one. A command is the sending surface, so it carries the rule
    /// itself: no confirmed type, or the wrong one, and nothing goes out.
    /// <para>Used as each value command's CanExecute, so a mismatched control
    /// GREYS rather than erroring — and re-evaluated on every mirror change
    /// (Refresh() notifies all of them), so a type report enables or disables
    /// them the moment it lands.</para></summary>
    private bool CanWriteValueOfType(HopType type) => HopReady && ConfirmedType == type;

    private bool CanCommitCenter() => CanWriteValueOfType(HopType.Narrowband);

    private bool CanCommitBandEdges() => CanWriteValueOfType(HopType.Wideband);

    private bool CanEditHopList() => CanWriteValueOfType(HopType.List);

    /// <summary>The in-body half of the type gate. Execute ignores CanExecute,
    /// so every value command repeats it — and says so when refusing, because
    /// a silent no-op on a programming surface is indistinguishable from a
    /// swallowed write.</summary>
    private bool RefusesWrongType(int net, HopType required)
    {
        if (ConfirmedType == required) return false;

        Fail(net, ConfirmedType is null
            ? "the radio has not reported this net's hop type yet — set a type first."
            : $"this net is confirmed {HopNetDisplay.TypeText(ConfirmedType)}; "
              + $"{HopNetDisplay.TypeText(required)} values do not apply to it.");
        return true;
    }

    /// <summary>NETID n &lt;8-digit id&gt;, then <c>DIS n</c>. Commits on entry
    /// completion (or its Set button) — there is no batching Store any
    /// more.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void CommitNetId()
    {
        if (!HopReady) return;
        int net = _pickedNet;

        // Round 7 (DB): typed wins; an empty field falls back to the reported
        // value backing its placeholder; a bare hint refuses.
        string netId = (NetIdInput ?? "").Trim();
        if (netId.Length == 0)
        {
            if (_netIdBacking is null)
            {
                Fail(net, "no net ID typed and none reported to fall back to.");
                return;
            }
            netId = _netIdBacking;
        }
        else if (netId.Length != 8 || !netId.All(char.IsAsciiDigit))
        {
            Fail(net, "net ID must be exactly 8 digits.");
            return;
        }

        InputError = "";
        _hop.ProgramNetId(net, netId);
        _hop.RequestNet(net);
    }

    /// <summary>HOPTYPE n NB|WB|LIST, then <c>DIS n</c>. RE-CLICK GUARD: if the
    /// radio has already CONFIRMED this type, the press sends NOTHING — not a
    /// re-write of a value-invalidating command.
    /// <para>The guard reads <see cref="ConfirmedType"/>; the highlight reads
    /// <see cref="ReportedType"/> (round 13 item 14, ruling 2026-08-20), so the
    /// two coincide on a programmed net but NOT on a reported-unprogrammed one:
    /// there the lit segment still sends, a harmless duplicate HOPTYPE. Widening
    /// the guard to the reported type would make the segment un-pressable on
    /// exactly the net the operator is trying to program.</para></summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void SetType(string? type)
    {
        if (!HopReady) return;
        var wanted = type switch
        {
            "NB" => (HopType?)HopType.Narrowband,
            "WB" => HopType.Wideband,
            "LIST" => HopType.List,
            _ => null,
        };
        if (wanted is not { } t) return;
        if (ConfirmedType == t) return;              // re-click guard

        int net = _pickedNet;
        InputError = "";
        _hop.ProgramHopType(net, t);
        _hop.RequestNet(net);
    }

    /// <summary>HOPSET n &lt;center&gt; (NB), then <c>DIS n</c>. Entered in MHz
    /// (K6); an illegal value sends nothing and posts the note.</summary>
    [RelayCommand(CanExecute = nameof(CanCommitCenter))]
    private void CommitCenter()
    {
        if (!HopReady) return;
        int net = _pickedNet;
        if (RefusesWrongType(net, HopType.Narrowband)) return;

        // Round 7 (DB): typed wins; empty falls back to the reported center.
        string kHz;
        if (string.IsNullOrWhiteSpace(CenterInput))
        {
            if (_centerBacking is null)
            {
                Fail(net, "no center typed and none reported to fall back to.");
                return;
            }
            kHz = _centerBacking;
        }
        else if (!HopNetDisplay.TryParseMhz(CenterInput, out kHz))
        {
            Fail(net, "center — " + HopNetDisplay.EntryRule);
            return;
        }

        InputError = "";
        _hop.ProgramNarrowbandHopset(net, kHz);
        _hop.RequestNet(net);
    }

    /// <summary>HOPSET n &lt;low&gt; &lt;high&gt; (WB), then <c>DIS n</c>. The
    /// wire takes the band as ONE command, so completing EITHER edge commits
    /// the pair — and both must be legal before anything goes out.
    /// <para><b>Round 14 A2 — validation runs on the RESOLVED OUTGOING PAIR.</b>
    /// The fallback below substitutes a reported backing for a blank entry, so
    /// the pair the operator can SEE is not necessarily the pair that goes out.
    /// The floor refusal and the span advisories therefore evaluate what will
    /// actually be sent, never just the entry that was touched — otherwise
    /// editing one edge would validate half a band.</para></summary>
    [RelayCommand(CanExecute = nameof(CanCommitBandEdges))]
    private void CommitBandEdges()
    {
        if (!HopReady) return;
        int net = _pickedNet;
        if (RefusesWrongType(net, HopType.Wideband)) return;

        // Round 7 (DB): each edge independently — typed wins, empty falls
        // back to the reported edge (THE owner case: edit one edge, the
        // other still sends), a bare hint refuses.
        //
        // The `…Typed` flags are round 14 A2's: they record WHERE the resolved
        // value came from, which is what the radio-sourced pass-through below
        // turns on. They are not a second copy of "was the entry blank" — a
        // future edit that adds another resolution source has to answer the
        // same question for it.
        string low;
        bool lowTyped = false;
        if (string.IsNullOrWhiteSpace(LowInput))
        {
            if (_lowBacking is null)
            {
                Fail(net, "no low edge typed and none reported to fall back to.");
                return;
            }
            low = _lowBacking;
        }
        else if (!HopNetDisplay.TryParseMhz(LowInput, out low))
        {
            Fail(net, "low edge — " + HopNetDisplay.EntryRule);
            return;
        }
        else lowTyped = true;

        string high;
        bool highTyped = false;
        if (string.IsNullOrWhiteSpace(HighInput))
        {
            if (_highBacking is null)
            {
                Fail(net, "no high edge typed and none reported to fall back to.");
                return;
            }
            high = _highBacking;
        }
        else if (!HopNetDisplay.TryParseMhz(HighInput, out high))
        {
            Fail(net, "high edge — " + HopNetDisplay.EntryRule);
            return;
        }
        else highTyped = true;

        // Round 14 A2: the ONE refusal. Checked on BOTH resolved edges, and
        // the LOW edge first only so a pair that is wrong twice names one
        // offender rather than two — the sentence is identical either way.
        if (RefusesBelowHopFloor(net, low, lowTyped)) return;
        if (RefusesBelowHopFloor(net, high, highTyped)) return;

        _hop.ProgramWidebandHopset(net, low, high);
        _hop.RequestNet(net);

        // AFTER the send, deliberately: an advisory's whole meaning is "this
        // went out, and here is what the radio may do with it". Assigning it
        // before the send would leave a note on screen describing a command
        // that a later guard could still have stopped.
        InputError = WidebandSpanAdvisory(low, high);
    }

    /// <summary>The round-14 A2 floor refusal for ONE resolved WB edge.
    ///
    /// <para><b>The radio-sourced pass-through (constitution §3.1).</b> An edge
    /// resolved VERBATIM from the radio's own report is NEVER refused — the
    /// radio outranks both the manual and this client, and re-sending a value
    /// the radio itself reported must always be possible. Only operator-TYPED
    /// values are refused. In practice a below-floor reported edge should be
    /// impossible (the radio write-refuses them, so it can never come to hold
    /// one), but the rule is pinned rather than assumed.</para>
    ///
    /// <para>Comparison is in the wire's 5-digit kHz, like every other numeric
    /// comparison on the send path — the MHz vocabulary is a display form.</para>
    /// </summary>
    private bool RefusesBelowHopFloor(int net, string kHzText, bool operatorTyped)
    {
        if (!operatorTyped) return false;
        if (!TryKHz(kHzText, out int kHz) || kHz >= HopBandFloorKHz) return false;

        Fail(net, BelowHopFloorRefusal);
        return true;
    }

    /// <summary>The WB span notes, over the RESOLVED pair. Mutually exclusive by
    /// construction (a span cannot be both ≥ 2000 and &lt; 140), so the order is
    /// documentation rather than precedence. An unparseable edge advises
    /// nothing: the send already happened on values K6 accepted, and inventing
    /// a note from a value this method could not read would be a guess.
    ///
    /// <para><b>The span is measured on the ORDERED pair</b> (audit round 1,
    /// MINOR). Both edges are independently K6-legal, so nothing stops an
    /// operator typing a low ABOVE the high — and a raw <c>high - low</c> then
    /// goes NEGATIVE, which is below every threshold and drew the
    /// under-140-kHz note on a pair that is actually 2 MHz wide. The WIDTH of a
    /// band does not depend on which edge was typed first, so the note is
    /// computed from the width.</para>
    ///
    /// <para>An inverted pair still SENDS, and deliberately gets no refusal of
    /// its own: what the wire does with an inverted <c>HOPSET</c> has never
    /// been captured (the <c>EXC</c> sibling NORMALISES low/high, but that is a
    /// different command and §3.1 does not travel by analogy), and an advisory
    /// never blocks. The radio stays the judge.</para></summary>
    private static string WidebandSpanAdvisory(string low, string high)
    {
        if (!TryKHz(low, out int lo) || !TryKHz(high, out int hi)) return "";

        int span = Math.Abs(hi - lo);
        if (span >= SpanRefusesGenerationKHz) return SpanRefusesGenerationAdvisory;
        if (span < MinimumSpanAdvisoryKHz) return MinimumSpanAdvisory;
        return "";
    }

    /// <summary>The wire's 5-digit kHz string as a number. Every round-14 limit
    /// compares in kHz, because that is the form the command carries.</summary>
    private static bool TryKHz(string? wire, out int kHz)
        => int.TryParse(wire, NumberStyles.Integer, CultureInfo.InvariantCulture, out kHz);

    /// <summary>HOPLIST n ADD &lt;f&gt; … (one or more, space separated), then
    /// <c>HOPLIST n</c> AND <c>DIS n</c>. Both re-reads, because the list and
    /// the net record are two different answers.
    /// <para>ADD is APPEND (protocol.md) and that is now FINE: the appended
    /// frequencies appear as rows the operator can remove one by one, so an
    /// accidental double-add is visible and reversible. The round-3 caution —
    /// wipe from the console first — is retired.</para></summary>
    [RelayCommand(CanExecute = nameof(CanEditHopList))]
    private void AddListFrequencies()
    {
        if (!HopReady) return;
        int net = _pickedNet;
        if (RefusesWrongType(net, HopType.List)) return;

        // SPACE-separated, and ONLY space (round 11, closing the grammar to
        // match the ALE twin P3 closed and the §7 placeholder's own words).
        //
        // THE DEFECT THIS REMOVES. A comma inside a frequency is a
        // DECIMAL COMMA to most of the world, and this box takes MHz with
        // decimals. With ',' in the delimiter set "5,7" — one frequency to
        // whoever typed it — split into "5" and "7", BOTH of which parse
        // cleanly, and 05000 + 07000 went at the wire with nothing said. Even
        // where the split half-fails ("5,320" → "5" parses, "320" is out of
        // range) the note names "320", a token the operator never typed. As
        // ONE token it is one offender, named as typed, and nothing is sent.
        // A frequency is not a place to be generous about grammar.
        var entries = (ListAddInput ?? "").Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            Fail(net, "type at least one frequency to add, in MHz.");
            return;
        }

        var kHz = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            if (!HopNetDisplay.TryParseMhz(entries[i], out kHz[i]))
            {
                Fail(net, $"'{entries[i]}' — " + HopNetDisplay.EntryRule);
                return;
            }
        }

        // Round 14 A2: LIST gets ADVISORIES only, never the WB refusal — no
        // bench trial has put a below-floor frequency into a HOPLIST, and the
        // radio's own LIST validation is mostly SILENT-IGNORE (protocol.md), so
        // the client has nothing proven to refuse on.
        var advisory = ListAdvisory(net, kHz);

        _hop.ProgramHopList(net, kHz);
        _hop.RequestHopList(net);
        _hop.RequestNet(net);
        ListAddInput = "";      // the add box is spent; the rows are the truth
        InputError = advisory;  // …and it accompanies a send that went out
    }

    /// <summary>The two LIST notes, in precedence order. Both are advisory and
    /// only one slot exists, so the FLOOR note wins: it points at a concrete
    /// token the operator typed, where the span note describes an aggregate.
    ///
    /// <para><b>The span is computed over the UNION</b> of the CONFIRMED stored
    /// list and the tokens being added — a list's span is a property of the
    /// whole list, and adding one frequency to a stored list is exactly how a
    /// span gets exceeded. <b>It is SKIPPED ENTIRELY when the stored list is
    /// unconfirmed</b> (§3.1: the app never validates against a list it has not
    /// been told). "Confirmed" is the same condition the rows render under — a
    /// <c>HOPLIST n</c> asked this session AND an answer in the mirror — so the
    /// note can never disagree with what is on screen.</para>
    ///
    /// <para>No LIST-COUNT note exists, deliberately: the manual's 15-frequency
    /// minimum is BENCH-OVERRULED on this radio (protocol.md — two is too few,
    /// three works), and ui.md's "the radio is the judge there" ruling on the
    /// LIST minimum stands untouched.</para></summary>
    private string ListAdvisory(int net, IReadOnlyList<string> added)
    {
        foreach (var token in added)
            if (TryKHz(token, out int k) && k < HopBandFloorKHz) return ListFloorAdvisory;

        if (!_hopListsQueried.Contains(net)
            || !_hop.HopLists.TryGetValue(net, out var stored))
            return "";

        int? low = null, high = null;
        foreach (var token in stored.Concat(added))
        {
            if (!TryKHz(token, out int k)) continue;
            low = low is { } lo ? Math.Min(lo, k) : k;
            high = high is { } hi ? Math.Max(hi, k) : k;
        }

        return low is { } l && high is { } h && h - l > ListSpanAdvisoryKHz
            ? ListSpanAdvisory : "";
    }

    /// <summary>HOPLIST n DEL &lt;f&gt;, then <c>HOPLIST n</c> AND <c>DIS n</c>.
    /// The parameter is the row's WIRE value (kHz) — never the displayed MHz —
    /// so a removal cannot be lost in a round trip through the conversion.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHopList))]
    private void RemoveListFrequency(string? frequencyKHz)
    {
        if (!HopReady) return;
        if (string.IsNullOrWhiteSpace(frequencyKHz)) return;

        int net = _pickedNet;
        if (RefusesWrongType(net, HopType.List)) return;

        InputError = "";
        _hop.RemoveHopListFrequency(net, frequencyKHz);
        _hop.RequestHopList(net);
        _hop.RequestNet(net);
    }

    // ---- Exclusion bands (§7) ---------------------------------------------

    /// <summary>Project the Core mirror's three states onto the section, then
    /// compute the Add gate. Rows are rebuilt only when the CONTENT changed —
    /// each carries a Remove command, and re-creating them on every mirror
    /// event would restart the list under the operator's finger (the LIST
    /// editor's rule, same reason).</summary>
    private void UpdateExcludeSection()
    {
        var bands = _hop.ExcludeBands;

        if (bands is null)
        {
            if (ExcludeDisplayRows is not [{ CanRemove: false }])
                ExcludeDisplayRows = [HopExcludeRow.Placeholder];
            HasNoExcludeBands = false;
        }
        else if (bands.Count == 0)
        {
            if (ExcludeDisplayRows.Count != 0) ExcludeDisplayRows = [];
            HasNoExcludeBands = true;
        }
        else
        {
            if (!SameRows(ExcludeDisplayRows, bands))
                ExcludeDisplayRows =
                    [.. bands.Select(b => new HopExcludeRow(b, RemoveExcludeBandCommand))];
            HasNoExcludeBands = false;
        }

        AddExcludeBandDisabledReason =
            !HopReady ? ""                               // the pane gate already says why
            : bands is null ? ExcludeUnreadReason
            : bands.Count >= ExcludeBandCount ? ExcludeFullReason
            : "";
    }

    private static bool SameRows(IReadOnlyList<HopExcludeRow> rows, IReadOnlyList<HopExcludeBand> bands)
    {
        if (rows.Count != bands.Count) return false;
        for (int i = 0; i < bands.Count; i++)
            if (rows[i].BandText != HopExcludeRow.BandTextOf(bands[i])
                || rows[i].LowText != HopNetDisplay.MhzText(bands[i].LowKHz)
                || rows[i].HighText != HopNetDisplay.MhzText(bands[i].HighKHz))
                return false;
        return true;
    }

    /// <summary>The NEXT FREE band slot — the lowest 0–9 the radio's own table
    /// does not hold. Null when all ten are used. Deterministic, so removing
    /// band 3 from a full table puts the next Add back in 3.</summary>
    private static int? NextFreeBand(IReadOnlyList<HopExcludeBand> bands)
    {
        for (int slot = 0; slot < ExcludeBandCount; slot++)
            if (!bands.Any(b => b.Band == slot)) return slot;
        return null;
    }

    private bool CanAddExcludeBand()
        => HopReady && _hop.ExcludeBands is { } bands && bands.Count < ExcludeBandCount;

    /// <summary>EXC &lt;next free band&gt; &lt;low&gt; &lt;high&gt;, then a
    /// re-read of the whole table (round-10 verify doctrine: what the radio
    /// then reports is the only thing that renders).
    ///
    /// <para>The wire takes <b>8-DIGIT Hz</b> while every control here speaks
    /// MHz, so the conversion is the seam — and it ASSERTS its own width before
    /// the send. The radio silently ignores a wrongly-shaped frequency, so a
    /// seven-digit value would not be refused, it would be MISREAD.</para></summary>
    [RelayCommand(CanExecute = nameof(CanAddExcludeBand))]
    private void AddExcludeBand()
    {
        if (!HopReady) return;

        var bands = _hop.ExcludeBands;
        if (bands is null) { ExcludeError = ExcludeUnreadReason; return; }
        if (NextFreeBand(bands) is not { } slot) { ExcludeError = ExcludeFullReason; return; }

        if (!TryEdge(ExcludeLowInput, "low edge", out string lowHz)) return;
        if (!TryEdge(ExcludeHighInput, "high edge", out string highHz)) return;

        ExcludeError = "";
        _hop.ProgramExcludeBand(slot, lowHz, highHz);
        _hop.RequestExcludeBands();
        ExcludeLowInput = "";
        ExcludeHighInput = "";
    }

    /// <summary>One add-row edge: MHz in, 8-digit Hz out, or a note naming the
    /// offender exactly as it was typed and NOTHING sent.</summary>
    private bool TryEdge(string? typed, string which, out string hz)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0)
        {
            hz = "";
            ExcludeError = $"type a {which}, in MHz.";
            return false;
        }
        if (!HopNetDisplay.TryParseMhzToHz(text, out hz))
        {
            ExcludeError = $"'{text}' — " + HopNetDisplay.EntryRule;
            return false;
        }
        // Belt AND braces: the converter guarantees eight digits, and this
        // says so at the SEND site, where a future edit would break it.
        if (hz.Length != 8)
        {
            ExcludeError = $"'{text}' — " + HopNetDisplay.EntryRule;
            hz = "";
            return false;
        }
        return true;
    }

    /// <summary>EXC &lt;band&gt; DEL, then a re-read. UNCONFIRMED, deliberately:
    /// the round-10 §5 popup matrix covers whole-record destruction (Clear net,
    /// Delete address, Erase) and explicitly does NOT extend to per-row
    /// Removes — this is the same class as the hop-frequency and group-channel
    /// Removes beside it.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void RemoveExcludeBand(string? band)
    {
        if (!HopReady) return;
        if (!int.TryParse(band, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
            || slot is < 0 or >= ExcludeBandCount) return;

        ExcludeError = "";
        _hop.RemoveExcludeBand(slot);
        _hop.RequestExcludeBands();
    }

    // ---- Clear net (BG4) --------------------------------------------------

    /// <summary>Ask, then wipe — <c>HOPSET n DEL</c> followed by <c>DIS n</c>,
    /// the ONLY path that wipes. It asks on EVERY press: no once-per-session
    /// accepted latch, unlike the Operate pane's net-change warning, because a
    /// wipe is destructive and irreversible from the app.
    /// <para>NAMING, deliberate: the members say "NetWipe", not "ClearNet",
    /// even though the button reads "Clear net". The X6 scope guard scans
    /// app-layer source for the SUBSTRING <c>ClearNet</c> and allows it only
    /// in this file and the surface — a <c>ConfirmClearNetCommand</c> in the
    /// XAML would trip it. Do not "tidy" these names back. (The §5 prompt
    /// CONSTANTS above do carry the operator's words, which is why they live
    /// in this allow-listed file.)</para></summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RequestNetWipe()
    {
        if (!HopReady) return;

        // CAPTURED AT PRESS. The prompt names this net, so this net is what
        // gets wiped even if the picker moves while the question is open.
        int net = _pickedNet;

        bool accepted;
        try
        {
            accepted = await _prompt.ConfirmAsync(
                string.Format(CultureInfo.InvariantCulture, ClearNetTitleFormat,
                    net.ToString(CultureInfo.InvariantCulture)),
                ClearNetMessage, ClearNetAccept, PromptCancel);
        }
        catch (Exception)
        {
            // A prompt that faulted or was cancelled produced no answer:
            // send nothing, wedge nothing.
            return;
        }

        if (!accepted) return;

        // The same gate the send path uses, re-checked AFTER the await.
        if (!HopReady) return;

        InputError = "";

        // C2 audit round 1, MAJOR: the wipe erases the net's hoplist on the
        // RADIO, but the Core mirror keeps the pre-wipe list (no HOPLIST
        // answer arrives to replace it — the wipe's only answer is DIS) and
        // this net stayed in the BC4 once-set. Re-programming it as LIST in
        // the same session therefore showed the ERASED frequencies, with a
        // Remove button against each. Dropping the net from the once-set
        // re-arms the BC4 trigger, so the next confirmed LIST report fetches
        // the real list; UpdateListRows additionally refuses to render a net
        // that is not currently queried, so the stale mirror entry cannot
        // surface in the gap. (No Core change: §2.2.4 stands — the mirror is
        // still the radio's last word, this pane simply knows that word is
        // out of date for this net until it asks again.)
        _hopListsQueried.Remove(net);
        _wipedNetsAwaitingReport.Add(net);

        _hop.ClearNet(net);
        _hop.RequestNet(net);
        Refresh();      // the rows go NOW, not when the answer gets back
    }

    private void Fail(int net, string message)
        => InputError = $"Net {net}: {message}";

    // ---- Manual refresh: DELETED (round 9) --------------------------------
    // The pane-bottom Refresh button and RefreshNetsCommand are GONE. Under
    // the unified read doctrine an editor landing re-reads the picked net
    // every time, so a "read the radio" button here was a second answer to a
    // question the picker already answers. Refresh buttons survive ONLY on
    // expensive-bulk list surfaces — the channel list tab's DI ×100 is the one
    // genuinely heavy read in the app, and it KEEPS its Refresh.
}

/// <summary>
/// One READ-ONLY row of the "Net list" tab: the radio's own report about one
/// net, in the headed-cell columns (Net · ID · Type · Frequencies (MHz)) the
/// Operate pane's "Current net" row uses. It holds NO input buffers and NO
/// commands — the list tab is display only. Every cell reads "—" until the
/// radio has reported that net this session.
/// </summary>
public partial class HopNetListRow : ObservableObject
{
    public HopNetListRow(int number)
    {
        Number = number;
        NumberText = number.ToString(CultureInfo.InvariantCulture);
    }

    public int Number { get; }
    public string NumberText { get; }

    [ObservableProperty] private string netIdText = "—";
    [ObservableProperty] private string typeText = "—";
    [ObservableProperty] private string valueText = "—";

    internal void Apply((string NetId, string Type, string Value) net)
    {
        NetIdText = net.NetId;
        TypeText = net.Type;
        ValueText = net.Value;
    }
}

/// <summary>
/// One WB exclusion band as the section renders it (round 11 §7): the radio's
/// own band SLOT, and its two edges in the pane's MHz vocabulary. Immutable —
/// the mirror is the truth, and a changed table rebuilds the rows.
/// <para>The row carries the VM's remove command rather than the template
/// reaching out for it (the <see cref="HopFrequencyRow"/> precedent), and the
/// command's parameter is the BAND SLOT as the wire takes it, never the
/// displayed MHz.</para>
/// </summary>
public sealed class HopExcludeRow
{
    public HopExcludeRow(HopExcludeBand band, ICommand removeBand)
    {
        BandText = BandTextOf(band);
        LowText = HopNetDisplay.MhzText(band.LowKHz);
        HighText = HopNetDisplay.MhzText(band.HighKHz);
        RemoveBand = removeBand;
        CanRemove = true;
    }

    private HopExcludeRow(string marker)
    {
        BandText = marker;
        LowText = marker;
        HighText = marker;
    }

    internal static string BandTextOf(HopExcludeBand band)
        => band.Band.ToString(CultureInfo.InvariantCulture);

    /// <summary>The band slot — displayed, and the remove command's
    /// parameter.</summary>
    public string BandText { get; }

    public string LowText { get; }

    public string HighText { get; }

    /// <summary>EXC &lt;band&gt; DEL — takes <see cref="BandText"/>. Null on the
    /// placeholder: an UNREAD table has no row to remove, and a Remove button
    /// beside a hyphen would offer to delete the marker.</summary>
    public ICommand? RemoveBand { get; }

    /// <summary>False on the placeholder — the flag the template hides the
    /// Remove button by.</summary>
    public bool CanRemove { get; }

    /// <summary>The UNREAD state's single hyphen row (the round's placeholder
    /// idiom — a static, so the projection cannot be mistaken for data).</summary>
    public static HopExcludeRow Placeholder { get; } =
        new(HopSettingsViewModel.ExcludePlaceholderText);
}

/// <summary>
/// One stored LIST frequency (BG3): the wire's kHz value, which the remove
/// command sends back verbatim, and the MHz text the operator reads. Immutable
/// — the mirror is the truth, and a changed list rebuilds the rows.
/// <para>The row carries the VM's remove command rather than the template
/// reaching out for it: a compiled <c>{Binding Remove}</c> inside the item
/// template beats a RelativeSource walk, and the row still owns no state.</para>
/// </summary>
public sealed class HopFrequencyRow
{
    public HopFrequencyRow(string frequencyKHz, ICommand remove)
    {
        FrequencyKHz = frequencyKHz;
        MhzText = HopNetDisplay.MhzText(frequencyKHz);
        Remove = remove;
    }

    /// <summary>HOPLIST n DEL — takes <see cref="FrequencyKHz"/> as its
    /// parameter. Owned by the VM; the row only holds the handle.</summary>
    public ICommand Remove { get; }

    /// <summary>The wire form, as reported — what <c>HOPLIST n DEL</c> takes.</summary>
    public string FrequencyKHz { get; }

    /// <summary>The displayed form (K6), a bare MHz number.</summary>
    public string MhzText { get; }
}
