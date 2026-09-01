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
/// The ALE settings pane's "Address programming" card
/// (plan-ale-programming.md §4.4) — the app's fourth programming surface, and
/// the first that EDITS the ALE fill (scope amendment X8).
///
/// <list type="bullet">
///   <item><b>Program</b> (LEFT, the DEFAULT): the kind segment row
///     (Self / Individual / Net / <b>Member</b> since round 15 E-1), then
///     either the ADDRESS fields — a Name entry, the associated-self Picker
///     and the channel-group ◀/▶ wheel — or, under Member, the net and member
///     Pickers and that net's membership table; and ONE action button whose
///     text reads Program or Add by kind.</item>
///   <item><b>Address book</b>: every stored address of every kind — selfs
///     included, unlike the Operate list — with a per-row Delete and the
///     guarded ERASE control at the bottom.</item>
/// </list>
///
/// <para><b>Every write goes through the ONE gate</b>
/// (<see cref="AleSurface.Programming"/>, shared with the scan-groups card):
/// the double-sentinel bracket is what makes the radio's refusal line
/// attributable to the write that drew it, and what keeps two programming
/// operations off the wire at once. A busy gate surfaces as this card's
/// InputError and sends NOTHING.</para>
///
/// <para><b>Verify = commit → re-read</b> (the HOP model, not the modem
/// echo-upsert one): every write closes with <c>RequestStationBook</c>. In the
/// R7 captures a fill write's response carried only gate-trailer lines, so the
/// re-read IS the proof — the book row appearing is what the operator sees.
/// <c>ADDM</c> closes with the same read even though membership can never be
/// read back: there it is the bracket's barrier, not a membership check, and
/// the member log says so.</para>
///
/// <para><b>No prefill anywhere on this card (X5, unchanged).</b> This is an
/// ADD-NEW form, not a slot editor: there is no "current value" to read back,
/// so the radio never writes an input buffer and confirmed state renders ONLY
/// as book rows. That is also why a tab landing here is NOT a populate gesture
/// — with nothing to prefill, clearing the operator's typing would destroy
/// work and restore nothing.</para>
///
/// <para><b>Read path — the round-9 two-tier doctrine (§6).</b> The EDITOR
/// reads its target fresh: the surface first becoming readable this session
/// (initial sight, and the reconnect after a drop — edge-detected in
/// <see cref="Refresh"/>, the HOP precedent) and every Program-tab landing send
/// one <c>RequestStationBook</c> (SLFAD + INDAD + NETAD + sentinel). The
/// ADDRESS BOOK tab is the LAZY tier: it reads on its FIRST landing per
/// session and then renders from the mirror. Switching KIND, and moving either
/// wheel, send NOTHING: they are view state over a form that has not been
/// submitted.</para>
///
/// <para><b>Gating is TWO-LEVEL (owner ruling 5).</b>
/// <see cref="AreControlsEnabled"/> = Ready + confirmed ALE governs the whole
/// card, so landings and reads still work while the radio scans (bench: address
/// listings during a scan come back clean). <see cref="CanWrite"/> additionally
/// requires NOT scanning and NOT calling/linked/sending, and governs the WRITE
/// commands alone — as their CanExecute and re-checked in every body, because
/// Execute ignores CanExecute.</para>
/// </summary>
public partial class AleProgrammingViewModel : ObservableObject
{
    /// <summary>Channel groups are 0-9 (AleController.ValidateChannelGroup).</summary>
    public const int ChannelGroupCount = 10;

    /// <summary>The client-side bound on EVERY address name, all kinds
    /// (round 10 §7, owner ruling 3: "allow 15, the radio decides"). It
    /// MIRRORS Core's <c>ValidateSelf</c>/<c>ValidateName</c> bound and is
    /// PROVISIONAL for selfs — the radio's true self maximum is unprobed
    /// (bench §12).</summary>
    public const int NameMaxLength = 15;

    /// <summary>The one message both kinds share now that both bounds are the
    /// same (round 10 §7).</summary>
    public const string NameLengthError = "An address is 1-15 characters.";

    /// <summary>The Name entry's placeholder (round 10 §7).</summary>
    public const string NamePlaceholder = "1-15 characters";

    // ---- The CONTEXTUAL self-gate hint (round 11 §5, owner ruling R2) ------
    // The round-10 standing two-line caption is DELETED. It stated the radio's
    // own gate token at an operator, it sat on the card whether or not it was
    // relevant, and R13 forbids the token anyway. What replaces it is ONE line
    // that appears only when the operator is actually about to hit the gate:
    // a SELF whose typed name is longer than three characters. Everything else
    // — the group-00 fact, the convention — was reference material, not a
    // decision the operator makes here.

    /// <summary>R2's exact wording. Visible only while
    /// <see cref="ShowSelfGateHint"/>.</summary>
    public const string SelfGateHint =
        "Stores, but only a 1-3 character self satisfies the scan gate.";

    public const string ScanningDisabledReason =
        "Stop the scan (Operate → STOP) to program";

    /// <summary>Why a WRITE is withheld while the radio is on air. REWORDED
    /// 2026-08-23 (manager ruling, on the phase-5 on-air sweep): the term this
    /// message explains is no longer "in a call" — it is the ONE on-air
    /// predicate, which a bare-STA LQA satisfies for MINUTES at a time (P14c),
    /// and "In a call" was then simply false on screen. E-4 house style: the
    /// radio's own situation in plain prose, no raw token. The CONST's NAME is
    /// deliberately unchanged (its two test files pin it by name; wording only,
    /// per the ruling).</summary>
    public const string InCallDisabledReason =
        "The radio is on the air — programming waits until it is idle.";

    /// <summary>The member table's READ-EMPTY caption — the radio's own
    /// <c>NO MEMBERS PRGMD</c>, in operator words (§5's three states). The
    /// UNREAD state is the hyphen ROW, not this.</summary>
    public const string NoMembersCaption = "No members programmed.";

    /// <summary>ROUND 15 E-1 — the Member picker's caption while no net is
    /// picked. The picker is empty and disabled until then, because
    /// "which addresses may join" is a property OF the net (E-Q1).</summary>
    public const string PickANetFirstCaption = "Pick a net first.";

    /// <summary>ROUND 15 E-1 (E-D1) — the absence, in prose. There is no
    /// remove-member verb on the wire, so the card cannot offer one and the
    /// operator is told what to do instead.</summary>
    public const string NoMemberRemovalCaption =
        "To remove a member, delete the address from the book — the radio has "
        + "no per-net removal.";

    // ---- The §5 prompt table (exact strings; {0} = the captured address,
    // {1} = the primary self's name) ---------------------------------------
    // Owned here rather than at the call site so a pin can assert the literal
    // the operator actually sees, and so the three DELETE prompts cannot drift
    // apart while only one of them is edited.
    //
    // ROUND 11 §5: the ONE self message is now THREE rows. The per-row prompt
    // the round-10 comment called impossible became possible the moment the
    // PRIMARY-SELF model landed (docs/protocol.md `DELAD`): the primary is the
    // FIRST `SLFAD` listing row, the book mirror preserves that order (§8's
    // pin), so the card can tell a primary from a secondary and say what each
    // one actually does. The old merged wording is deleted.

    public const string DeleteAddressTitleFormat = "Delete {0}?";

    public const string DeleteAddressMessage =
        "The radio removes this address from its book.";

    public const string DeleteSelfTitleFormat = "Delete self {0}?";

    /// <summary>The SECONDARY self: its dependants survive, re-pointed at the
    /// primary. <c>{1}</c> is the primary's name — the message names it,
    /// because "the primary self" is not something the operator can see from
    /// the row they pressed.</summary>
    public const string DeleteSecondarySelfMessageFormat =
        "The radio re-points its individuals and nets at the primary self {1}.";

    public const string DeletePrimarySelfTitleFormat = "Delete the primary self {0}?";

    /// <summary>The PRIMARY self: the destructive one. ROUND 15 E-4 CORRECTS
    /// THE DEVICE FACT (critic F46, <c>docs/protocol.md</c> the PRIMARY-SELF
    /// MODEL row): the individuals are NOT deleted — they are ORPHANED and go
    /// INVISIBLE while the book has no primary, and every one of them
    /// REAPPEARS, re-pointed, the moment a new 1–3 character self is
    /// programmed. The 2026-08-17 reading this message used to carry was
    /// wrong, and a prompt that overstates a deletion is the worst place to
    /// carry a disproved fact.</summary>
    public const string DeletePrimarySelfMessage =
        "The radio hides its individuals until a new 1–3 character self is "
        + "programmed, blanks its nets' self and stops scanning.";

    public const string DeleteAccept = "Delete";

    // ---- The §5 IMPACT block (byte-exact, deterministic) ------------------
    // Zero to three lines, each preceded by "\n", appended to the message in
    // THIS order. Every line is a fact the MIRRORS hold — the prompt never
    // opens until the reads that fill them have landed, and a read that
    // FAULTED says so rather than letting silence read as "no impact".

    public const string ImpactMemberOfFormat = "\nMember of: {0}.";

    public const string ImpactScheduleLine = "\nIts queued LQA schedule is removed too.";

    public const string ImpactUnknownFormat = "\nImpact unknown ({0} read failed).";

    public const string ImpactMembershipWord = "membership";
    public const string ImpactSchedulesWord = "schedules";
    public const string ImpactBothWord = "membership and schedules";

    public const string EraseTitle = "Erase every ALE address?";

    /// <summary>ROUND 15 E-3 (E-Q3): the two-sentence form, naming net
    /// MEMBERSHIP — which the framed warning used to carry and the popup did
    /// not — and the three things that survive.</summary>
    public const string EraseMessage =
        "The radio clears every self, individual, net, net membership and LQA "
        + "schedule. Channel groups, stored messages and settings survive.";

    public const string EraseAccept = "Erase";

    /// <summary>The safe button, shared by every prompt on this card.</summary>
    public const string PromptCancel = "Cancel";

    /// <summary>The UNREAD member row's cells (the round's placeholder
    /// idiom).</summary>
    public const string MemberPlaceholderText = "—";

    /// <summary>ROUND 15 D — the book row's member line when the radio has
    /// CONFIRMED the net empty. The UNREAD state is
    /// <see cref="MemberPlaceholderText"/>, exactly as in the Program tab's
    /// table: three states, never conflated.</summary>
    public const string NoMembersRowText = "No members programmed";

    /// <summary>The book row's member-line prefix — the Program tab keeps the
    /// numbered table; a book row is a one-line summary, so it carries the
    /// names only, in the radio's own insertion order.</summary>
    public const string MembersRowPrefix = "Members: ";

    /// <summary>The tag the book listing's FIRST self row carries (R3).</summary>
    public const string PrimaryTag = "PRIMARY";

    private readonly AleSurface _ale;
    private readonly RadioSession _session;
    private readonly IConfirmationPrompt _prompt;

    /// <summary>Nets whose targeted <c>NETAD</c> has already gone out this
    /// session (§5's read tier: once per net per session). Entries are dropped
    /// again by the writes that INVALIDATE membership — an accepted ADDM, a
    /// net Program, a DELAD or an ERASE — which is what makes "the affected
    /// nets re-read" happen rather than being latched out.</summary>
    private readonly HashSet<string> _memberReadsThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every id <see cref="ReadMembersOnce"/> was handed this session
    /// → the net names it covered (round 16 fixes S5). Coalesced names share
    /// the pending id; the intermediate ids Core allocates for its
    /// one-name-at-a-time dispatch are never handed to this VM and are not
    /// retried — by construction.
    /// <para>IMPACT-WAIT ids are deliberately NOT mapped: a retry beside the
    /// delete write would sit a read inside the programming bracket, which is
    /// the deferred round-16 work's business. A faulted impact read stays a
    /// faulted prompt.</para></summary>
    private readonly Dictionary<long, HashSet<string>> _memberReadsById = [];

    /// <summary>Names already retried once this session (reset ONLY on the
    /// drop) — what caps the retry at exactly one per name.</summary>
    private readonly HashSet<string> _memberRetried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The delete press's outstanding PREREQUISITE reads, or null.
    /// One at a time: a second press while this is open returns immediately
    /// (§5's re-entrancy rule — one pending prompt-open, never two).</summary>
    private ImpactWait? _impactWait;

    /// <summary>True from the press until the prompt has been answered (or the
    /// press abandoned). The re-entrancy latch §5 names.</summary>
    private bool _deleteInFlight;

    /// <summary>Whether the EDITOR's initial-sight read has gone out this
    /// session. Edge-detected rather than taken off the session phase because
    /// readability is Ready AND a confirmed ALE, and the mode arrives as a
    /// mirror event (the HOP precedent).</summary>
    private bool _sightReadThisSession;

    /// <summary>The ADDRESS BOOK tab's lazy once-per-session gate. Set by that
    /// TAB's own landing and by nothing else (§6 gives the tab its own tier
    /// row): the editor's landings read the same command, but they are the
    /// EDITOR's tier, and folding the two would leave this branch unreachable
    /// — i.e. untestable — for the sake of one book read per session.</summary>
    private bool _bookTabLoadedThisSession;

    /// <summary>Edge detector for LOSING the ALE confirmation while still
    /// Ready (a mode switch away): that clears the pending confirm and the
    /// gate display, without touching a typed buffer.</summary>
    private bool _aleWasConfirmed;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 8). Null where there is no campaign to wait for.
    /// <para><b>Scope note, deliberate:</b> the DELETE press's prerequisite
    /// reads (<see cref="LoadImpactMirrorsAsync"/>) are NOT deferred. They are
    /// part of an operator WRITE gesture — the plan's suppression scope covers
    /// autonomous READS and leaves writes with today's behaviour — and
    /// deferring them would wedge the confirmation prompt that waits on
    /// them.</para></summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>A book read deferred to the campaign's end — the sight edge, a
    /// Program-tab landing, the Book-tab's first landing. One flag for one wire
    /// act, so however many were deferred, one book read goes out.</summary>
    private bool _bookReadOwed;

    /// <summary>Member reads deferred to the campaign's end. Nothing joined
    /// <c>_memberReadsThisSession</c>, so re-running the once-path over the
    /// mirrored nets asks for exactly the ones still owed.</summary>
    private bool _memberReadsOwed;

    public AleProgrammingViewModel(
        AleSurface ale, RadioSession session, IConfirmationPrompt prompt,
        ICampaignSignal? campaign = null)
    {
        _ale = ale;
        _session = session;
        _prompt = prompt;
        _campaign = campaign;

        // The campaign's END edge runs the recompute; Refresh settles what is
        // owed if this card can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };

        // The prerequisite-read observer runs BEFORE the render: a delete press
        // waiting on its impact mirrors resumes on the completion that made it
        // possible, and the prompt it opens reads mirrors Core has already
        // committed.
        ale.Changed += (_, _) => ObserveImpactReads();
        ale.Changed += (_, _) => RetryMemberReadAfterSilence();
        ale.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                // Radio-derived caches, latches and gate state go; the
                // operator's TYPED BUFFERS AND SELECTIONS never do (the
                // standing reconnect pin, plan §5/§7.10).
                _sightReadThisSession = false;
                _bookTabLoadedThisSession = false;
                _aleWasConfirmed = false;
                // Session-scoped: reads deferred for a radio that has gone are
                // not owed to the next one.
                _bookReadOwed = false;
                _memberReadsOwed = false;
                _memberReadsThisSession.Clear();
                _memberReadsById.Clear();
                _memberRetried.Clear();
                InputError = "";
                OperationStatus = "";
                // A delete press waiting on prerequisite reads must not WEDGE
                // on a drop: it resumes, finds the gate gone in its post-await
                // re-check, and sends nothing.
                AbandonImpactWait();
                _ale.Programming.AbandonForSessionDrop();
            }
            Refresh();
        };

        BuildKindChoices();
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>Ready AND the radio has CONFIRMED ALE this session: these are
    /// ALE-scoped writes, sent at an <c>ALE&gt;</c> prompt.</summary>
    private bool AleReady => Ready && _ale.IsAleConfirmed;

    // ---- The read path (§6) ------------------------------------------------

    /// <summary>One <c>RequestStationBook</c> (SLFAD + INDAD + NETAD +
    /// sentinel) for an EDITOR landing — fresh, every time.</summary>
    private void ReadBook()
    {
        // D1 QUIESCE (audit round 1): `!AleReady` leaves the debt OWED. A
        // campaign that ended in SSB must not consume a book read this card
        // cannot perform; the next ALE confirmation pays it.
        if (!AleReady) return;
        // Every caller of this funnel — the sight edge, both tab landings —
        // defers here while a campaign owns the wire. Nothing greys out.
        if (_campaign?.CampaignActive == true) { _bookReadOwed = true; return; }
        _bookReadOwed = false;
        _ale.RequestStationBook();
    }

    /// <summary>Settle the deferred reads, once each, and ONLY while this card
    /// can read. Called from <see cref="Refresh"/> — the card's every-event
    /// recompute — so "the next moment this card is readable" is when it
    /// runs.</summary>
    private void PayWhatIsOwed()
    {
        if (_campaign?.CampaignActive == true || !AleReady) return;

        if (_bookReadOwed) ReadBook();                // clears the latch itself
        if (_memberReadsOwed)
        {
            _memberReadsOwed = false;
            foreach (var net in _ale.NetAddresses) ReadMembersOnce(net.Address);
        }
    }

    /// <summary>View-owned load trigger (the card's <c>Loaded</c>). The
    /// INITIAL-SIGHT read is edge-detected inside <see cref="Refresh"/>, so
    /// whichever comes first — the view appearing or the radio confirming ALE
    /// — reads once, and the reconnect after a drop reads again.</summary>
    public void EnsureLoaded() => Refresh();

    // ---- Sub-tab view state ------------------------------------------------

    [ObservableProperty] private bool isBookTabOpen;

    /// <summary>Landing back on the Program tab is an EDITOR LANDING: it
    /// re-reads the book. It is NOT a populate gesture — this card prefills
    /// nothing, so there is nothing a gesture could restore.</summary>
    [RelayCommand]
    private void OpenProgramTab()
    {
        IsBookTabOpen = false;
        InputError = "";
        ReadBook();
        Refresh();
    }

    /// <summary>The book tab is the LAZY tier: its FIRST landing this session
    /// reads, and only if nothing has loaded the book yet.
    /// <para>ROUND 11 §5: that landing ALSO queues one targeted
    /// <c>NETAD &lt;name&gt;</c> per MIRRORED net. This is the tab the per-row
    /// Delete lives on, and its prompt has to say what deleting an address
    /// takes with it — so the membership the impact block reads is loaded by
    /// the landing rather than by the press wherever possible. Core's keyed
    /// member queue coalesces them into one operation at a time.</para></summary>
    [RelayCommand]
    private void OpenBookTab()
    {
        IsBookTabOpen = true;
        InputError = "";
        if (AleReady && !_bookTabLoadedThisSession)
        {
            _bookTabLoadedThisSession = true;
            ReadBook();
            foreach (var net in _ale.NetAddresses) ReadMembersOnce(net.Address);
        }
        Refresh();
    }

    // ---- Kind (view state — switching sends NOTHING) -----------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelfKind))]
    [NotifyPropertyChangedFor(nameof(ShowAddressFields))]
    [NotifyPropertyChangedFor(nameof(ShowAssociatedSelf))]
    [NotifyPropertyChangedFor(nameof(ShowMemberSection))]
    [NotifyPropertyChangedFor(nameof(ShowSelfGateHint))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private AleProgramKind kind = AleProgramKind.Self;

    public bool IsSelfKind => Kind == AleProgramKind.Self;

    /// <summary>ROUND 15 E-1: the ADDRESS fields — Name, the channel-group
    /// wheel and the associated-self picker — belong to the three kinds that
    /// program an ADDRESS. Member programs a RELATIONSHIP between two
    /// addresses that already exist, so none of them is on screen for it.</summary>
    public bool ShowAddressFields => Kind != AleProgramKind.Member;

    /// <summary>The Core wire kind for the three ADDRESS kinds. Member is not
    /// an address kind at all — it is the ADDM relationship — so asking for
    /// its wire kind is a programming error, not a fourth case.</summary>
    private AleAddressKind AddressKind() => Kind switch
    {
        AleProgramKind.Self => AleAddressKind.Self,
        AleProgramKind.Individual => AleAddressKind.Individual,
        AleProgramKind.Net => AleAddressKind.Net,
        _ => throw new InvalidOperationException("the Member kind programs no address"),
    };

    /// <summary>R2's CONTEXTUAL hint: on screen only while the operator is
    /// composing a SELF whose name is longer than the three characters the
    /// radio's scan gate accepts. Both halves are required — an Individual can
    /// be fifteen characters with no consequence at all.</summary>
    public bool ShowSelfGateHint => IsSelfKind && (NameInput ?? "").Trim().Length > 3;

    /// <summary>The associated-self row exists for Individual and Net only —
    /// and the Self write path calls a TWO-ARGUMENT wrapper, so a stale
    /// selection cannot ride along even if the row were somehow reachable
    /// (structural hidden-never-sent, invariant §7.3).</summary>
    public bool ShowAssociatedSelf => Kind is AleProgramKind.Individual or AleProgramKind.Net;

    /// <summary>ROUND 15 E-1/E-Q2: the member section belongs to the MEMBER
    /// kind. The Net kind programs the net and nothing else — membership was
    /// never part of creating one, and pinning the section to the typed name
    /// meant the section only worked for a net that already existed.</summary>
    public bool ShowMemberSection => Kind == AleProgramKind.Member;

    partial void OnKindChanged(AleProgramKind value)
    {
        _ = value;
        InputError = "";
        BuildKindChoices();
        UpdateMemberChoices();
        UpdateMemberSection();
        ActionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The kind segment row (the ChoiceItem idiom — the button text
    /// is the display word, the closure carries the kind). FOUR segments since
    /// round 15 E-1; the arithmetic still fits the phone budget
    /// (4×90 + 3×6 = 378 ≤ 394), which StyleVocabularyGuardTests pins.</summary>
    [ObservableProperty] private IReadOnlyList<ChoiceItem> kindChoices = [];

    private void BuildKindChoices()
        => KindChoices =
        [
            new ChoiceItem("Self", Kind == AleProgramKind.Self, _ => Kind = AleProgramKind.Self),
            new ChoiceItem("Individual", Kind == AleProgramKind.Individual, _ => Kind = AleProgramKind.Individual),
            new ChoiceItem("Net", Kind == AleProgramKind.Net, _ => Kind = AleProgramKind.Net),
            new ChoiceItem("Member", Kind == AleProgramKind.Member, _ => Kind = AleProgramKind.Member),
        ];

    // ---- Name entry (X5: a report never writes this buffer) ----------------

    /// <summary>The name of the address being programmed. ROUND 15 E-1: it is
    /// no longer the member section's target identity — the Member kind picks
    /// its net from the mirror, so a half-typed name drives nothing at
    /// all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelfGateHint))]
    private string nameInput = "";

    /// <summary>The name as it would go on the wire.</summary>
    private string TypedName() => (NameInput ?? "").Trim().ToUpperInvariant();

    // ---- The channel-group wheel (pending selection; sends nothing) --------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupText))]
    private int groupSelection = 1;

    public string GroupText => GroupSelection.ToString(CultureInfo.InvariantCulture);

    [RelayCommand] private void GroupUp() => SpinGroup(+1);

    [RelayCommand] private void GroupDown() => SpinGroup(-1);

    private void SpinGroup(int delta)
    {
        GroupSelection = (GroupSelection + delta + ChannelGroupCount) % ChannelGroupCount;
        InputError = "";
    }

    // ---- The associated-self PICKER (over the mirror's OWN selfs) ---------
    // ROUND 15 E-5: the ◀/▶ wheel became a Picker and moved ABOVE the channel
    // group. Picking a self also SETS the group to that self's — a DEFAULT,
    // not a lock: the wheel still spins freely afterwards and the radio stores
    // whatever group is sent. The card keeps exactly ONE wheel (channel group).

    /// <summary>The selfs the radio has reported, in ITS listing order — the
    /// Picker's whole offer. Empty book, empty picker.</summary>
    [ObservableProperty] private IReadOnlyList<string> selfChoices = [];

    /// <summary>The VM seat the associated-self Picker binds. Null until the
    /// operator picks.
    /// <para>AUDIT ROUND 2: a selection the mirror no longer holds is DROPPED
    /// now, not kept. The round-11 "kept" rule belonged to a ◀/▶ WHEEL over a
    /// label, which could display any string; a Picker cannot render a value
    /// outside its ItemsSource, so keeping it would leave the seat holding
    /// something the control shows as blank — and the two-way binding would
    /// write its own null back anyway. Invariant §7.4 is untouched: the app
    /// still runs no existence PRE-CHECK on a send. <see cref="Program"/>
    /// refuses with "Pick an associated self." and sends nothing.</para></summary>
    [ObservableProperty] private string? associatedSelfSelection;

    partial void OnAssociatedSelfSelectionChanged(string? value)
    {
        InputError = "";
        if (value is not { Length: > 0 }) return;

        // E-5: the group FOLLOWS the pick. Read from the book mirror — the
        // radio's own record of that self — never invented.
        foreach (var self in _ale.SelfAddresses)
            if (string.Equals(self.Address, value, StringComparison.OrdinalIgnoreCase))
            {
                GroupSelection = self.ChannelGroup;
                return;
            }
    }

    // ---- The Member kind's two pickers (E-1) ------------------------------

    /// <summary>The nets the radio has reported, in ITS listing order.</summary>
    [ObservableProperty] private IReadOnlyList<string> netChoices = [];

    /// <summary>Which net the member is being added TO. Picking one reads that
    /// net's membership once per session — the ONLY wire effect of any pick on
    /// this card (F49).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPickMember))]
    [NotifyPropertyChangedFor(nameof(ShowPickANetFirst))]
    private string? netPick;

    partial void OnNetPickChanged(string? value)
    {
        _ = value;
        InputError = "";
        // The offer is a property OF the net (its own associated self is the
        // only self the radio accepts), so the candidate list is rebuilt here.
        UpdateMemberChoices();
        // …and the section renders THIS net's rows, firing its targeted read
        // if this session has not asked for it yet.
        UpdateMemberSection();
        ActionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The candidates for the picked net: every individual, plus
    /// EXACTLY that net's own associated self (E-Q1) — see
    /// <see cref="MemberCandidates"/>. Empty until a net is picked.</summary>
    [ObservableProperty] private IReadOnlyList<AleMemberCandidate> memberChoices = [];

    [ObservableProperty]
    private AleMemberCandidate? memberPick;

    partial void OnMemberPickChanged(AleMemberCandidate? value)
    {
        _ = value;
        InputError = "";
        ActionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The member Picker is dead until a net is picked: with no net
    /// there is no offer to make, and the caption says so rather than leaving
    /// an empty control to be interpreted.</summary>
    public bool CanPickMember => NetPick is { Length: > 0 };

    /// <summary>The caption that stands in for the empty offer. The house has
    /// no boolean-inverting converter and does not want one: a view asks the
    /// VM what to show.</summary>
    public bool ShowPickANetFirst => !CanPickMember;

    /// <summary>
    /// Rebuild the three offers from the mirror — but ONLY on a real change,
    /// and RE-SELECT the operator's picks into whatever the rebuild produced.
    /// This is the <c>MessagesViewModel.Targets</c> idiom, verbatim in shape:
    /// keep the pick, assign the list, then find the pick's EQUAL in the new
    /// list.
    ///
    /// <para><b>Two distinct failures, both about a Picker's
    /// <c>SelectedItem</c>, and each needs its own half.</b>
    /// <list type="number">
    ///   <item>REBUILDING WHEN NOTHING CHANGED. <c>Refresh</c> runs on every
    ///     mirror event; a list of fresh INSTANCES would not contain the pick
    ///     by reference, so the Picker would null it and Add would go dead
    ///     under the operator's finger every time any ALE line landed. The
    ///     value comparison below is what stops that.</item>
    ///   <item>REBUILDING WHEN SOMETHING ELSE changed (audit round 2). A book
    ///     re-read that adds an UNRELATED individual legitimately rebuilds the
    ///     candidate list — and the operator's still-valid pick then points at
    ///     an object that is no longer IN the ItemsSource, which the two-way
    ///     Picker resolves by clearing the visible selection and disabling
    ///     Add. Keeping the old instance is not "keeping the selection"; the
    ///     selection has to be re-pointed at the equal candidate.</item>
    /// </list></para>
    ///
    /// <para>Equality is by VALUE — <c>Address</c> AND <c>ChannelGroup</c> for
    /// a candidate, the name for the two string pickers — never by reference.
    /// A pick whose value is GONE from the new offer becomes null: it would
    /// otherwise send an address the radio no longer reports, and (round 15
    /// E-5/E-1) a Picker cannot render a value outside its ItemsSource anyway,
    /// so the VM decides that deterministically instead of letting the view
    /// write back a null of its own. The write paths still refuse with their
    /// own prose rather than sending anything.</para>
    ///
    /// <para><b>AN EMPTY OFFER RE-SELECTS NOTHING</b>, and that exception is
    /// the STANDING RECONNECT PIN (plan §5/§7.10): a session drop clears the
    /// whole ALE mirror, so every offer empties at once — and the operator's
    /// typed buffers and selections survive a reconnect, by rule. An empty
    /// offer carries NO information about a pick: it means the radio has told
    /// us nothing, not that it says the name is gone. That is the same
    /// unread-is-not-empty distinction the member table renders in three
    /// states. When the re-read lands and the book really no longer holds the
    /// name, the offer is non-empty and the pick drops then.</para></summary>
    private void UpdateMemberChoices()
    {
        var nets = _ale.NetAddresses.Select(a => a.Address).ToList();
        if (!nets.SequenceEqual(NetChoices, StringComparer.Ordinal))
        {
            string? keptNet = NetPick;
            NetChoices = nets;
            NetPick = Reselect(nets, keptNet);
        }

        var selfs = _ale.SelfAddresses.Select(a => a.Address).ToList();
        if (!selfs.SequenceEqual(SelfChoices, StringComparer.Ordinal))
        {
            string? keptSelf = AssociatedSelfSelection;
            SelfChoices = selfs;
            AssociatedSelfSelection = Reselect(selfs, keptSelf);
        }

        var candidates = MemberCandidates();
        if (!candidates.Select(c => c.Display).SequenceEqual(
                MemberChoices.Select(c => c.Display), StringComparer.Ordinal))
        {
            var keptMember = MemberPick;
            MemberChoices = candidates;
            if (candidates.Count > 0)                    // an empty offer re-selects nothing
                MemberPick = keptMember is null ? null
                    : candidates.FirstOrDefault(c =>
                        string.Equals(c.Address, keptMember.Address, StringComparison.Ordinal)
                        && c.ChannelGroup == keptMember.ChannelGroup);
        }
    }

    /// <summary>The string pickers' half of the same rule: the picked NAME if
    /// the new offer still holds it, else null — and the pick UNTOUCHED when
    /// the offer is empty, because an empty offer is the radio saying nothing
    /// rather than saying the name is gone (the reconnect pin).</summary>
    private static string? Reselect(List<string> offer, string? picked)
    {
        if (offer.Count == 0) return picked;
        return picked is not null && offer.Contains(picked, StringComparer.Ordinal) ? picked : null;
    }

    /// <summary>ROUND 11 §5, re-keyed to the picked net by round 15 E-1 — the
    /// CONSTRAINED offer: every individual, plus EXACTLY the picked net's own
    /// associated self and no other. The radio refuses any other self
    /// (<c> INV SELF MEMBER </c>, bench 2026-08-17), so offering the whole self
    /// list was offering refusals.
    ///
    /// <para>ROUND 16 FIXES S7 — the offer is FILTERED TO THE NET'S CHANNEL
    /// GROUP (owner policy 2026-08-23, strict): a candidate is offered iff its
    /// group equals the picked net's, and the net's associated self likewise.
    /// The radio itself ACCEPTS a cross-group member (bench 2026-08-01,
    /// negative controlled — <c>protocol.md</c>'s ADDM row refuses by KIND
    /// only), so a mismatched member can still be added from the FRONT PANEL
    /// and will show in the membership table; what this filter says is that a
    /// member in another group will not scan with the net, so the app does not
    /// offer one. The candidate shows the NAME ALONE (owner 2026-08-23): with
    /// the strict filter every offer is in the net's group, so the two-digit
    /// suffix said the same thing on every line and was dropped.</para></summary>
    private IReadOnlyList<AleMemberCandidate> MemberCandidates()
    {
        if (PickedNet() is not { } net) return [];

        // The picked net's own row carries the filter. A net picked but not
        // mirrored offers nothing — defensive: PickedNet() already returns only
        // a mirrored net.
        var netRow = _ale.NetAddresses.FirstOrDefault(
            n => string.Equals(n.Address, net, StringComparison.OrdinalIgnoreCase));
        if (netRow is null) return [];
        int group = netRow.ChannelGroup;

        List<AleMemberCandidate> candidates =
            [.. _ale.IndividualAddresses
                .Where(a => a.ChannelGroup == group)
                .Select(a => new AleMemberCandidate(a.Address, a.ChannelGroup))];

        if (netRow.AssociatedSelf is { Length: > 0 } self)
            foreach (var address in _ale.SelfAddresses)
                if (string.Equals(address.Address, self, StringComparison.OrdinalIgnoreCase)
                    && address.ChannelGroup == group)
                    candidates.Add(new AleMemberCandidate(address.Address, address.ChannelGroup));

        return candidates;
    }

    /// <summary>The MIRROR's own spelling of the PICKED net, or null when the
    /// Member kind is not on screen or nothing is picked. Every membership
    /// read this card fires is keyed on this, so no half-typed name can reach
    /// Core's address validation. ROUND 15 E-1: the typed name is no longer
    /// consulted for membership at all — the Net kind programs the net, and
    /// the Member kind picks from what the radio has actually reported.</summary>
    private string? PickedNet()
    {
        if (Kind != AleProgramKind.Member) return null;
        if (NetPick is not { Length: > 0 } picked) return null;
        foreach (var net in _ale.NetAddresses)
            if (string.Equals(net.Address, picked, StringComparison.OrdinalIgnoreCase))
                return net.Address;
        return null;
    }

    // ---- Gate, notes and the operation status ------------------------------

    [ObservableProperty] private bool areControlsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    private string disabledReason = "";

    public bool HasDisabledReason => !string.IsNullOrEmpty(DisabledReason);

    /// <summary>Why the WRITE commands are greyed while the card itself is
    /// live (owner ruling 5's wording). Empty when writes are allowed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWriteDisabledReason))]
    private string writeDisabledReason = "";

    public bool HasWriteDisabledReason => !string.IsNullOrEmpty(WriteDisabledReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    /// <summary>The last programming operation's outcome, in operator words.
    /// EMPTY on Accepted — the re-read row appearing is the visible proof.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationStatus))]
    private string operationStatus = "";

    public bool HasOperationStatus => !string.IsNullOrEmpty(OperationStatus);

    private bool IsScanning
    {
        get
        {
            var link = _ale.LinkState;
            return link.IsConfirmed && link.Value == AleLinkState.Scanning;
        }
    }

    /// <summary>ROUND 15 item I (F69): THE on-air term. It was this file's
    /// private <c>Calling|Sending|Linked</c> list; a write issued during an LQA
    /// would have queued behind a minutes-long transmission (P14c).</summary>
    private bool InCallOrSending => _ale.IsOnAir;

    /// <summary>Level TWO of the gate: the WRITE commands only. Reads, tab
    /// landings and wheel spins stay alive above it.</summary>
    private bool CanWrite() => AleReady && !IsScanning && !InCallOrSending;

    // ---- Rows --------------------------------------------------------------

    /// <summary>The book tab's rows — ALL kinds INCLUDING selfs (unlike the
    /// Operate station list, which is a call target list).</summary>
    public ObservableCollection<AleBookRow> BookRows { get; } = [];

    [ObservableProperty] private bool hasNoBookRows = true;

    /// <summary>
    /// The PICKED net's membership, straight from the §8 mirror — the display
    /// projection of its three states (invariant 6):
    /// <list type="bullet">
    ///   <item>UNREAD (key absent) — EXACTLY ONE hyphen row.</item>
    ///   <item>READ-EMPTY — no rows, and <see cref="HasNoMembers"/> puts
    ///     <see cref="NoMembersCaption"/> on screen.</item>
    ///   <item>rows — the radio's own <c>MEMBER nn</c> numbers and addresses,
    ///     in INSERTION order.</item>
    /// </list>
    /// This REPLACES the round-10 session send-log: membership became readable
    /// on 2026-08-17, so the honest display is the radio's answer, not the
    /// app's memory of what it asked for. An ADDM's outcome renders where every
    /// other write's does — <see cref="OperationStatus"/>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<AleMemberRow> memberDisplayRows = [AleMemberRow.Placeholder];

    /// <summary>The READ-EMPTY state only — never the unread one.</summary>
    [ObservableProperty] private bool hasNoMembers;

    // ---- Refresh from the mirror -------------------------------------------

    private void Refresh()
    {
        // The EDITOR's initial-sight read: the surface first becoming READABLE
        // this session — initial sight, and the reconnect after a drop. NOT a
        // populate gesture (it fires on reconnect, and typing survives that).
        if (AleReady && !_sightReadThisSession)
        {
            _sightReadThisSession = true;
            ReadBook();
        }

        // …and whatever a campaign deferred, settled on the same recompute.
        PayWhatIsOwed();

        // Losing the ALE confirmation while still Ready (a mode switch away)
        // retires anything that was about to act on the radio. A prompt that
        // is OPEN at this moment needs nothing here: every confirm body
        // re-checks the same gate its send path uses, AFTER the await (§5's
        // lifecycle contract), so a lost confirmation sends nothing.
        if (_aleWasConfirmed && !AleReady) OperationStatus = "";
        _aleWasConfirmed = AleReady;

        AreControlsEnabled = AleReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ale.IsAleConfirmed ? "Address programming is ALE-scoped — waiting for the radio to confirm ALE."
            : "";

        WriteDisabledReason =
            !AleReady ? ""
            : IsScanning ? ScanningDisabledReason
            : InCallOrSending ? InCallDisabledReason
            : "";

        UpdateBookRows();
        // ROUND 15 E-1: the pickers' offers are MIRROR projections, so they are
        // rebuilt with everything else the mirror drives.
        UpdateMemberChoices();
        UpdateMemberSection();

        // ROUND 15 D — THE COLD-LANDING RULE (§14.2). OpenBookTab queues one
        // targeted read per MIRRORED net, but on a COLD session the book read
        // it just issued has not landed, so the mirror is empty and zero reads
        // go out; nothing re-armed them and the new member lines would sit at
        // the hyphen until some other gesture read them. So: while the book tab
        // is open AND has loaded this session, every net the mirror holds gets
        // its read here too. It is NOT a new read kind (I-D1) — it is the same
        // once-per-session targeted NETAD, now also fired when the book LANDS
        // under an open tab. Idempotent by the existing per-net latch, and
        // Core's keyed member queue coalesces the pending names into one
        // operation, so N nets cost exactly N reads, once.
        if (IsBookTabOpen && _bookTabLoadedThisSession)
            foreach (var net in _ale.NetAddresses) ReadMembersOnce(net.Address);

        ActionCommand.NotifyCanExecuteChanged();
        RequestDeleteCommand.NotifyCanExecuteChanged();
        EraseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Render the picked net's membership from the mirror, then —
    /// LAST, because it SENDS — fire that net's targeted read if this session
    /// has not asked for it yet (§5's read tier). The read's answer re-enters
    /// through <c>Changed</c>, by which time the key is present and the tier
    /// cannot recur.</summary>
    private void UpdateMemberSection()
    {
        string? net = PickedNet();
        IReadOnlyList<AleNetMember>? members = null;
        if (net is not null) _ale.NetMembers.TryGetValue(net, out members);

        if (members is null)
        {
            MemberDisplayRows = [AleMemberRow.Placeholder];
            HasNoMembers = false;
        }
        else if (members.Count == 0)
        {
            MemberDisplayRows = [];
            HasNoMembers = true;
        }
        else
        {
            MemberDisplayRows = [.. members.Select(m => new AleMemberRow(m.Number, m.Address))];
            HasNoMembers = false;
        }

        if (ShowMemberSection && net is not null) ReadMembersOnce(net);
    }

    /// <summary>One targeted <c>NETAD &lt;name&gt;</c> for a net whose members
    /// are UNREAD, at most once per net per session. The latch exists for the
    /// FAULT case: an unanswered read leaves the key absent, and without it
    /// every subsequent mirror event would re-fire the same read forever.
    /// <para>ROUND 16 FIXES S5: once per session, PLUS ONE RETRY after a
    /// silence — see <see cref="RetryMemberReadAfterSilence"/>. The id is
    /// mapped to the name so that retry knows whose silence it is.</para></summary>
    private void ReadMembersOnce(string netName)
    {
        if (!AleReady) return;
        if (_ale.NetMembers.ContainsKey(netName)) return;
        // D1 QUIESCE: a clone campaign owns the wire. NOTHING joins the
        // once-per-session set, so the name stays owed and the campaign-end
        // handler re-runs this path over the mirrored nets.
        if (_campaign?.CampaignActive == true) { _memberReadsOwed = true; return; }
        if (!_memberReadsThisSession.Add(netName)) return;
        Map(_ale.RequestNetMembers(netName), netName);
    }

    /// <summary>Record which names a read id covers. Coalesced requests share
    /// one pending id, so an id maps to a SET.</summary>
    private void Map(long readId, string netName)
    {
        if (!_memberReadsById.TryGetValue(readId, out var names))
            _memberReadsById[readId] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.Add(netName);
    }

    /// <summary>
    /// ROUND 16 FIXES S5 — a member read that got SILENCE is tried once more.
    ///
    /// <para>It UNLATCHES the name rather than re-requesting it directly
    /// (decision F-15): <see cref="AleReady"/> requires a confirmed ALE, so a
    /// direct re-request would simply be skipped if the mode had dropped before
    /// the silence was observed, and the name would stay latched forever.
    /// Unlatching makes the retry the SAME path as the first read — it fires
    /// now if ALE is ready, or when ALE comes back and the net is shown.</para>
    ///
    /// <para><see cref="_memberRetried"/> is what caps it at one: after the
    /// retry's own silence the name is not unlatched again. Retry accounting
    /// counts UNLATCHES, not wire writes — a retry that coalesced and was then
    /// abandoned is not retried again.</para>
    /// </summary>
    private void RetryMemberReadAfterSilence()
    {
        var completion = _ale.LastMemberRead;
        // Not ours, or already handled — the removal is what makes this
        // idempotent across repeated Changed deliveries.
        if (!_memberReadsById.Remove(completion.ReadId, out var names)) return;
        if (completion.Answered) return;

        bool unlatched = false;
        foreach (var name in names)
        {
            if (_ale.NetMembers.ContainsKey(name)) continue;   // the mirror has it after all
            if (!_memberRetried.Add(name)) continue;           // one retry per name per session
            _memberReadsThisSession.Remove(name);
            unlatched = true;
        }
        if (unlatched) UpdateMemberSection();
    }

    /// <summary>A write INVALIDATED membership (Core clears the mirror key at
    /// send time): drop the latch so §5's "the affected nets re-read" happens,
    /// and re-fire for the net on screen. <paramref name="netName"/> null =
    /// the GLOBAL invalidations (DELAD, ERASE).
    ///
    /// <para>ROUND 16 FIXES S5, AUDIT ROUND 1 — this deliberately does NOT
    /// scrub <see cref="_memberReadsById"/>. The plan asked for the invalidated
    /// name to be dropped from every mapped set "so a stale id cannot retry
    /// it", and the audit found that loop unpinned; it is unpinned because no
    /// sequence can reach it. This method runs ONLY from an
    /// <c>AleProgrammingGate</c> outcome, and the gate cannot produce one while
    /// a member read is still outstanding: it refuses to release a write unless
    /// <c>PendingPingCount</c> and <c>PingAnswerDebt</c> are both zero, and
    /// Core keeps ONE sentinel on the wire at a time — so every mapped id that
    /// existed when the press happened has already completed, and
    /// <see cref="RetryMemberReadAfterSilence"/> removed it (it removes on the
    /// id, before it even looks at Answered). The one way an entry outlives its
    /// completion is a queued <c>SynchronizationContext</c> coalescing two
    /// deliveries, and such an entry can never fire a retry either, because
    /// read ids are monotonic and no later completion will ever carry it. The
    /// map is bounded by the session and cleared on the drop.</para></summary>
    private void ReReadMembersAfterWrite(string? netName)
    {
        if (netName is null) _memberReadsThisSession.Clear();
        else _memberReadsThisSession.Remove(netName);
        UpdateMemberSection();
    }

    private void UpdateBookRows()
    {
        // R3: the PRIMARY tag is the FIRST self row — mirror index 0 among
        // selfs, which §8's order pin makes the radio's own listing order.
        // Position, not identity: after a primary deletion the next first row
        // carries it, which is the ASSUMED-tier reading (plan §1) and is true
        // of the DISPLAY either way.
        var selfs = _ale.SelfAddresses;
        List<AleBookRow> wanted =
        [
            .. selfs.Select((a, i) =>
                new AleBookRow(AleAddressKind.Self, a, RequestDeleteCommand, isPrimarySelf: i == 0)),
            .. _ale.IndividualAddresses.Select(a => new AleBookRow(AleAddressKind.Individual, a, RequestDeleteCommand)),
            .. _ale.NetAddresses.Select(a =>
                new AleBookRow(AleAddressKind.Net, a, RequestDeleteCommand,
                    membersText: MembersLine(a.Address))),
        ];

        HasNoBookRows = wanted.Count == 0;

        // Rebuild only on real change — the rows carry a command each, and
        // re-creating them on every mirror event restarts the list under the
        // operator's finger (the LIST-rows precedent).
        if (BookRows.Count == wanted.Count
            && BookRows.Select(r => r.Signature).SequenceEqual(wanted.Select(r => r.Signature),
                StringComparer.Ordinal))
            return;

        BookRows.Clear();
        foreach (var row in wanted) BookRows.Add(row);
    }

    /// <summary>ROUND 15 D — one NET row's member line, DERIVED from the §8
    /// mirror at row-build time (critic F34) rather than held on the row: the
    /// row is immutable, so the line can only be right if the rebuild that
    /// carries it is driven by the same mirror event the membership landed on.
    /// <see cref="AleBookRow.Signature"/> includes it, so a member read
    /// landing rebuilds the rows and the line appears.
    ///
    /// <para>THE SAME THREE STATES the Program tab's table renders, in one
    /// line: no entry = UNREAD = the hyphen; an entry with zero members =
    /// the radio's own <c>NO MEMBERS PRGMD</c> in operator words; rows = the
    /// names, in the radio's INSERTION order. An <c>ADDM</c>'s
    /// <c>InvalidateNetMembers</c> REMOVES the key, so the line goes back to
    /// the hyphen the instant the write goes out and commits the new list on
    /// the re-read — both instants are honest, and both are pinned.</para></summary>
    private string MembersLine(string netName)
    {
        if (!_ale.NetMembers.TryGetValue(netName, out var members)) return MemberPlaceholderText;
        if (members.Count == 0) return NoMembersRowText;
        return MembersRowPrefix + string.Join(", ", members.Select(m => m.Address));
    }

    // ---- The card's ONE action button (E-D2) -------------------------------
    // ROUND 15 E-1/E-D2 (critic F50): the button's TEXT switches with the kind,
    // so it needs ONE command seat — two commands behind one button is how a
    // stale CanExecute lights a button that dispatches the other body.
    // `ProgramCommand` and `AddMemberCommand` are DELETED.

    /// <summary>What the one action button says. "Add" under Member — it adds
    /// an existing address to an existing net; "Program" otherwise.</summary>
    public string ActionText => Kind == AleProgramKind.Member ? "Add" : "Program";

    /// <summary>Member needs BOTH picks before it can send; the three address
    /// kinds carry today's Program gate unchanged.</summary>
    private bool CanAct()
        => Kind == AleProgramKind.Member
            ? CanWrite() && NetPick is { Length: > 0 } && MemberPick is not null
            : CanWrite();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Action()
    {
        if (Kind == AleProgramKind.Member) AddMember();
        else Program();
    }

    // ---- Program (the one address write) -----------------------------------

    private void Program()
    {
        // Execute ignores CanExecute, so the gate is re-checked here and says
        // WHY — a silent no-op on a programming surface is indistinguishable
        // from a swallowed write.
        if (!AleReady) return;
        if (!CanWrite()) { InputError = WriteDisabledReason; return; }

        string name = (NameInput ?? "").Trim().ToUpperInvariant();
        int group = GroupSelection;
        var kind = AddressKind();

        // Client bounds MIRROR Core's — ONE bound, 1-15, for EVERY kind since
        // round 10 §7 (owner ruling 3: "allow 15, the radio decides"). The
        // self bound was 3 because the radio's gate line says
        // "PRG 1-3 CHAR SLF"; that line is about the FILL GATE, and nothing
        // has ever measured what a longer self does (bench §12), so the client
        // stops pre-refusing it. Still not a uniqueness pre-check: the radio
        // remains the authority on whether a name may exist.
        if (name.Length is 0 or > NameMaxLength)
        {
            InputError = NameLengthError;
            return;
        }

        string? assocSelf = null;
        if (kind != AleAddressKind.Self)
        {
            if (_ale.SelfAddresses.Count == 0)
            {
                InputError =
                    "The radio holds no self addresses — program a self first: "
                    + "an individual or net needs an associated self that already exists.";
                return;
            }
            if (AssociatedSelfSelection is not { Length: > 0 } picked)
            {
                InputError = "Pick an associated self.";
                return;
            }
            assocSelf = picked;
        }

        // Structural hidden-never-sent: the Self path calls a TWO-argument
        // wrapper, so a stale associated self cannot ride along.
        Action write = kind switch
        {
            AleAddressKind.Self => () => _ale.ProgramSelf(name, group),
            AleAddressKind.Individual => () => _ale.ProgramIndividual(name, group, assocSelf!),
            _ => () => _ale.ProgramNet(name, group, assocSelf!),
        };

        RunWrite(write, outcome =>
        {
            OperationStatus = DescribeOutcome(outcome);
            // A NET write invalidates that net's membership in Core (a
            // re-created net has no members): re-read it (§5).
            if (kind == AleAddressKind.Net) ReReadMembersAfterWrite(name);
        });
    }

    // ---- Net membership (add-only on the wire, but READABLE since 2026-08-17)

    /// <summary>ADDM, then the re-read that shows it. There is NO remove-member
    /// verb anywhere on this card (§5's absence pin): removal is a global
    /// <c>DELAD</c> of the address itself, which the book tab already offers
    /// behind its own prompt.
    /// <para>ROUND 15 E-1: both operands are PICKS from the mirror now — the
    /// net from <see cref="NetPick"/>, the member's wire identity from the
    /// typed candidate's <c>Address</c>, never its display text (E-D3). The
    /// gate, the <c>ADDM</c>, the re-read and the refusal vocabulary are
    /// unchanged.</para></summary>
    private void AddMember()
    {
        if (!AleReady) return;
        if (!CanWrite()) { InputError = WriteDisabledReason; return; }
        if (Kind != AleProgramKind.Member) return;

        if (PickedNet() is not { } net)
        {
            InputError = "Pick a net.";
            return;
        }
        if (MemberPick is not { } candidate)
        {
            InputError = "Pick a member address.";
            return;
        }
        string member = candidate.Address;

        RunWrite(
            () => _ale.ProgramNetMember(net, member),
            outcome =>
            {
                OperationStatus = DescribeOutcome(outcome);
                // Core invalidated this net's membership at send: re-read it,
                // and the rows appearing are the operator's verify (§5).
                ReReadMembersAfterWrite(net);
            });
    }

    // ---- The book tab's per-row Delete (POPUP, every press) ----------------
    // Round 10 §5: the inline pending-confirm state is GONE — the question is
    // a two-button popup raised through IConfirmationPrompt (the VM never
    // touches MAUI UI, invariant 2). The §5 LIFECYCLE CONTRACT, line by line:
    //   * the target is captured at PRESS (the row's address), so the send
    //     goes to what the operator pointed at, not to whatever the mirror
    //     holds when they answer;
    //   * cancel sends NOTHING;
    //   * accept sends ONCE, against the CAPTURED target;
    //   * the WRITE GATE is re-checked in the BODY after the await — a
    //     session drop or a lost ALE confirmation while the prompt is open
    //     sends nothing;
    //   * a faulted or cancelled prompt task sends nothing and does not wedge;
    //   * there is no accepted latch, so every completed press re-prompts.

    /// <summary>
    /// PREREQUISITE-LOADED (round 11 §5): the press FIRST makes sure the impact
    /// mirrors are loaded — one targeted <c>NETAD</c> for every net whose
    /// membership is unread, plus the bare <c>EXCH</c> when the schedule queue
    /// is — and the prompt opens ON COMPLETION of those reads, carrying what
    /// the deletion actually takes with it. Read-your-writes immediacy makes
    /// that sub-second; a read that FAULTS still opens the prompt, with the
    /// impact-unknown line, because silence here would read as "no impact".
    ///
    /// <para>The reads fire FROM the book tab the press is on (§10's tier
    /// rule), and one press is pending at a time: a second press while the
    /// first is still loading or still asking does nothing at all.</para>
    ///
    /// <para>THREE prompts, not two: individual/net, SECONDARY self, PRIMARY
    /// self. Which one is CAPTURED AT PRESS along with the address, so a book
    /// that re-reports while the question is on screen cannot change either the
    /// target or what the operator was told about it.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RequestDelete(string? address)
    {
        if (!AleReady || !CanWrite()) return;
        if (string.IsNullOrWhiteSpace(address)) return;
        if (_deleteInFlight) return;            // one pending open at a time

        _deleteInFlight = true;
        try
        {
            // CAPTURED AT PRESS — the address, its kind, and (for a self)
            // whether it is the primary plus who the primary is.
            string target = address;
            bool self = IsSelf(target);
            string? primary = PrimarySelf();
            bool isPrimary = self && primary is not null
                && string.Equals(primary, target, StringComparison.Ordinal);

            var faults = await LoadImpactMirrorsAsync();

            // The gate, re-checked BEFORE the question as well as after it: a
            // session drop or a lost ALE confirmation while the reads were in
            // flight must not put a destructive question on screen that could
            // never be acted on.
            if (!AleReady) return;
            if (!CanWrite()) { InputError = WriteDisabledReason; return; }

            string title = string.Format(CultureInfo.InvariantCulture,
                isPrimary ? DeletePrimarySelfTitleFormat
                : self ? DeleteSelfTitleFormat
                : DeleteAddressTitleFormat,
                target);
            string message = string.Format(CultureInfo.InvariantCulture,
                isPrimary ? DeletePrimarySelfMessage
                : self ? DeleteSecondarySelfMessageFormat
                : DeleteAddressMessage,
                target, primary ?? "")
                + ImpactBlock(target, faults);

            if (!await Confirm(title, message, DeleteAccept)) return;

            // The same gate the send path uses, re-checked AFTER the await.
            if (!AleReady) return;
            if (!CanWrite()) { InputError = WriteDisabledReason; return; }

            RunWrite(() => _ale.RemoveAddress(target), outcome =>
            {
                OperationStatus = DescribeOutcome(outcome);
                // DELAD is GLOBAL: the address leaves EVERY net, so Core
                // invalidated all of them and all of them may re-read.
                ReReadMembersAfterWrite(null);
            });
        }
        finally { _deleteInFlight = false; }
    }

    private bool IsSelf(string address)
        => _ale.SelfAddresses.Any(a => string.Equals(a.Address, address, StringComparison.Ordinal));

    /// <summary>The PRIMARY self — mirror index 0 among selfs (§8's order pin
    /// makes that the radio's own first <c>SLFAD</c> listing row). Null when
    /// the book holds no self at all.</summary>
    private string? PrimarySelf()
        => _ale.SelfAddresses.Count > 0 ? _ale.SelfAddresses[0].Address : null;

    // ---- The delete prerequisite: load the impact mirrors, then ask -------

    /// <summary>One press's outstanding prerequisite reads. Ids are matched by
    /// EQUALITY against the completion records (they complete exactly once);
    /// coalesced member requests share an id, which the set absorbs.</summary>
    private sealed class ImpactWait
    {
        public readonly HashSet<long> MemberIds = [];
        public readonly HashSet<long> ScheduleIds = [];
        public bool MembershipFaulted;
        public bool SchedulesFaulted;
        public readonly TaskCompletionSource<ImpactFaults> Done = new();

        public bool IsSettled => MemberIds.Count == 0 && ScheduleIds.Count == 0;

        public ImpactFaults Faults => new(MembershipFaulted, SchedulesFaulted);
    }

    /// <summary>Which prerequisite reads did NOT answer.</summary>
    private readonly record struct ImpactFaults(bool Membership, bool Schedules);

    /// <summary>Queue every prerequisite read the mirrors still need and hand
    /// back a task that completes when they have all landed — immediately when
    /// nothing needed reading.</summary>
    private Task<ImpactFaults> LoadImpactMirrorsAsync()
    {
        var wait = new ImpactWait();

        foreach (var net in _ale.NetAddresses)
        {
            if (_ale.NetMembers.ContainsKey(net.Address)) continue;
            _memberReadsThisSession.Add(net.Address);
            wait.MemberIds.Add(_ale.RequestNetMembers(net.Address));
        }
        if (_ale.LqaSchedules is null) wait.ScheduleIds.Add(_ale.RequestLqaSchedules());

        if (wait.IsSettled) return Task.FromResult(wait.Faults);

        _impactWait = wait;
        return wait.Done.Task;
    }

    /// <summary>Watch the two completion records for the ids this press is
    /// waiting on. An UNANSWERED completion is a fault: the prompt still
    /// opens, and says which half of the impact it could not establish.</summary>
    private void ObserveImpactReads()
    {
        if (_impactWait is not { } wait) return;

        var member = _ale.LastMemberRead;
        if (wait.MemberIds.Remove(member.ReadId) && !member.Answered) wait.MembershipFaulted = true;

        var schedule = _ale.LastScheduleRead;
        if (wait.ScheduleIds.Remove(schedule.ReadId) && !schedule.Answered) wait.SchedulesFaulted = true;

        if (!wait.IsSettled) return;
        _impactWait = null;
        wait.Done.TrySetResult(wait.Faults);
    }

    /// <summary>A session drop resolves a pending wait as BOTH-faulted rather
    /// than leaving the press awaiting a read that can never land. What the
    /// operator sees is not the fault prompt: the post-await gate re-check
    /// fires first, so nothing is asked and nothing is sent.</summary>
    private void AbandonImpactWait()
    {
        if (_impactWait is not { } wait) return;
        _impactWait = null;
        wait.Done.TrySetResult(new ImpactFaults(true, true));
    }

    /// <summary>§5's IMPACT grammar, byte-exact: zero to three lines, each
    /// preceded by <c>\n</c>, in THIS order — membership, schedule, fault.</summary>
    private string ImpactBlock(string target, ImpactFaults faults)
    {
        string block = "";

        var nets = NetsHolding(target);
        if (nets.Count > 0)
            block += string.Format(CultureInfo.InvariantCulture,
                ImpactMemberOfFormat, string.Join(", ", nets));

        if (HasQueuedSchedule(target)) block += ImpactScheduleLine;

        string? what =
            faults.Membership && faults.Schedules ? ImpactBothWord
            : faults.Membership ? ImpactMembershipWord
            : faults.Schedules ? ImpactSchedulesWord
            : null;
        if (what is not null)
            block += string.Format(CultureInfo.InvariantCulture, ImpactUnknownFormat, what);

        return block;
    }

    /// <summary>The nets whose MIRRORED membership holds this address, in
    /// MIRROR ORDER (the book's own net order — the radio's listing order).
    /// A net whose members are unread contributes nothing; the fault line is
    /// what says so.</summary>
    private List<string> NetsHolding(string address)
    {
        List<string> nets = [];
        foreach (var net in _ale.NetAddresses)
        {
            if (!_ale.NetMembers.TryGetValue(net.Address, out var members)) continue;
            if (members.Any(m => string.Equals(m.Address, address, StringComparison.OrdinalIgnoreCase)))
                nets.Add(net.Address);
        }
        return nets;
    }

    private bool HasQueuedSchedule(string address)
        => _ale.LqaSchedules is { } rows
           && rows.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase));

    // ---- ERASE (book tab, bottom) ------------------------------------------
    // Round 10 §5: the typed-token Entry is GONE from the card AND the token
    // parameter is gone from Core. What guards the wire now is the POPUP —
    // one deliberate answer, asked every press.

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Erase()
    {
        if (!AleReady || !CanWrite()) return;

        if (!await Confirm(EraseTitle, EraseMessage, EraseAccept)) return;

        if (!AleReady) return;
        if (!CanWrite()) { InputError = WriteDisabledReason; return; }

        RunWrite(() => _ale.EraseAddressBook(), outcome =>
        {
            OperationStatus = DescribeOutcome(outcome);
            // ERASE takes membership and the schedule queue with the book
            // (Core invalidates both): nothing is latched out afterwards.
            ReReadMembersAfterWrite(null);
        });
    }

    /// <summary>The ONE path to the popup. A faulted or cancelled prompt task
    /// answers NO — nothing is sent and nothing wedges, which is exactly what
    /// §5 asks of a prompt that never produced an answer.</summary>
    private async Task<bool> Confirm(string title, string message, string accept)
    {
        try
        {
            return await _prompt.ConfirmAsync(title, message, accept, PromptCancel);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- The one path to the wire ------------------------------------------

    /// <summary>Every write on this card: through the ONE gate, with the
    /// station book as the closing read. False = the gate was busy (the other
    /// card's operation), nothing was sent, and the reason is the InputError.</summary>
    private bool RunWrite(Action write, Action<AleProgrammingOutcome> onOutcome)
    {
        InputError = "";
        OperationStatus = "";

        if (_ale.Programming.TryRun(write, () => _ale.RequestStationBook(), onOutcome, out string busyReason))
            return true;

        InputError = busyReason;
        return false;
    }

    private static string DescribeOutcome(AleProgrammingOutcome outcome) => outcome.Result switch
    {
        // Accepted says nothing: the book row appearing (or going) is the proof.
        AleProgrammingResult.Accepted => "",
        AleProgrammingResult.Refused => AleRefusalVocabulary.Describe(outcome.Detail),
        AleProgrammingResult.Unverified =>
            "Unverified — " + (outcome.Detail ?? "the radio did not answer") + ".",
        _ => "Failed — " + (outcome.Detail ?? "nothing reached the wire") + ".",
    };
}

/// <summary>
/// ROUND 15 E-1 — the address programming card's own kind, which is NOT the
/// wire's. <c>AleAddressKind</c> stays Core's truth about the three ADDRESS
/// kinds; the card has a FOURTH thing to program — a net MEMBERSHIP — and it
/// is a relationship between two addresses that already exist, not an address.
/// The three address kinds map 1:1 onto the Core enum.
/// </summary>
public enum AleProgramKind
{
    Self,
    Individual,
    Net,
    Member,
}

/// <summary>
/// ROUND 15 E-D3 (critic F51) — one offer in the Member picker: the address's
/// WIRE identity and its channel group; the display text is the address
/// alone. <c>ADDM</c> takes <see cref="Address"/>; nothing on the wire ever
/// sees <see cref="Display"/>. <see cref="ChannelGroup"/> stays on the type:
/// kept-selection equality is by Address AND ChannelGroup.
///
/// <para>ROUND 16 FIXES S7 — the offer is FILTERED to the picked net's channel
/// group (owner policy 2026-08-23): a member in another group will not scan
/// with the net, so the app does not offer one. The radio itself ACCEPTS a
/// cross-group member (bench 2026-08-01, negative controlled—
/// <c>protocol.md</c>'s ADDM row refuses by KIND only), so a mismatched member
/// can still be added from the FRONT PANEL and will show in the membership
/// table — the wire fact is unchanged, the app's OFFER policy is what moved.
/// The group is NOT shown beside the name (owner 2026-08-23): under the strict
/// filter it is always the net's, so the suffix repeated one fact on every
/// line.</para>
/// </summary>
public sealed class AleMemberCandidate
{
    public AleMemberCandidate(string address, int channelGroup)
    {
        Address = address;
        ChannelGroup = channelGroup;
        Display = address;
    }

    public string Address { get; }
    public int ChannelGroup { get; }

    /// <summary>What the picker shows: the address alone (the group suffix
    /// was dropped 2026-08-23 — under the strict S7 filter it repeated the
    /// net's group on every line).</summary>
    public string Display { get; }

    /// <summary>The Picker renders items by <c>ToString()</c> unless it is
    /// given an item-display path; making the two agree means a template
    /// change can never silently send the display text.</summary>
    public override string ToString() => Display;
}

/// <summary>
/// One row of the ADDRESS BOOK tab: the radio's own report about one stored
/// address, of any kind. Immutable — the mirror is the truth, and a changed
/// book rebuilds the rows.
/// </summary>
public sealed class AleBookRow
{
    public AleBookRow(AleAddressKind kind, AleAddress address, ICommand delete,
        bool isPrimarySelf = false, string membersText = "")
    {
        KindText = kind switch
        {
            AleAddressKind.Self => "SELF",
            AleAddressKind.Individual => "IND",
            _ => "NET",
        };
        NameText = address.Address;
        GroupText = address.ChannelGroup.ToString("00", CultureInfo.InvariantCulture);
        // A net whose associated self was cascaded away lists WITHOUT the
        // segment — the third display state, not an invented blank.
        AssociatedSelfText = string.IsNullOrEmpty(address.AssociatedSelf) ? "—" : address.AssociatedSelf!;
        IsPrimarySelf = isPrimarySelf;
        PrimaryTagText = isPrimarySelf ? AleProgrammingViewModel.PrimaryTag : "";
        // ROUND 15 D: NET rows only. A self or an individual has no membership
        // to show, so the line is not merely hidden — it is EMPTY, and the flag
        // is the kind, so no future caller can hand a self a member list.
        MembersText = kind == AleAddressKind.Net ? membersText : "";
        HasMembersText = kind == AleAddressKind.Net;
        Delete = delete;
    }

    public string KindText { get; }
    public string NameText { get; }
    public string GroupText { get; }
    public string AssociatedSelfText { get; }

    /// <summary>R3: this is the book listing's FIRST self row. The tag is a
    /// LISTING-POSITION fact, and the Operate selfs card does not carry
    /// it.</summary>
    public bool IsPrimarySelf { get; }

    /// <summary>The tag's text, empty on every other row.</summary>
    public string PrimaryTagText { get; }

    /// <summary>ROUND 15 D — the indented member line under a NET row: the
    /// hyphen while unread, the read-empty caption, or
    /// <c>Members: A, B, C</c> in the radio's insertion order. Empty string on
    /// every self and individual row.</summary>
    public string MembersText { get; }

    /// <summary>Whether the row HAS a member line at all — i.e. it is a NET
    /// row. False on selfs and individuals, which is what hides the second
    /// row's label.</summary>
    public bool HasMembersText { get; }

    /// <summary>DELAD, opened behind the confirm — takes
    /// <see cref="NameText"/> as its parameter. Owned by the VM; the row only
    /// holds the handle (the HopFrequencyRow precedent).</summary>
    public ICommand Delete { get; }

    /// <summary>The change detector for the rebuild-only-on-change rule. The
    /// tag is IN it: a primary that moved must rebuild the rows, or the tag
    /// would sit on a row that no longer leads the listing.
    /// <para>ROUND 15 D: <see cref="MembersText"/> is IN it — a member read
    /// landing changes nothing else about the row, so without it the rebuild
    /// short-circuits and the line never appears.
    /// <see cref="HasMembersText"/> is NOT: it is the kind, which
    /// <see cref="KindText"/> already carries.</para></summary>
    internal string Signature
        => $"{KindText}|{NameText}|{GroupText}|{AssociatedSelfText}|{PrimaryTagText}|{MembersText}";
}

/// <summary>
/// One row of the picked net's membership, as the radio reported it: its own
/// printed <c>MEMBER nn</c> number and the member address, in INSERTION order.
/// Immutable — the mirror is the truth, and a re-read rebuilds the rows.
/// </summary>
public sealed class AleMemberRow
{
    public AleMemberRow(int number, string address)
    {
        NumberText = number.ToString("00", CultureInfo.InvariantCulture);
        AddressText = address;
    }

    private AleMemberRow(string numberText, string addressText)
    {
        NumberText = numberText;
        AddressText = addressText;
    }

    public string NumberText { get; }
    public string AddressText { get; }

    /// <summary>The UNREAD state's single hyphen row (the round's placeholder
    /// idiom — a static, so the projection cannot be mistaken for data).</summary>
    public static AleMemberRow Placeholder { get; } =
        new(AleProgrammingViewModel.MemberPlaceholderText,
            AleProgrammingViewModel.MemberPlaceholderText);
}
