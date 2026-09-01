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
/// The HOP pane (plan §4.3, restructured by plan-ui-tweaks-round3.md §R,
/// reflowed by plan-ui-tweaks-round4.md §AB and AGAIN by
/// plan-ui-tweaks-round11.md §7) — SELECT-ONLY, net editing is out of v1.
/// THREE sections, in this order: "Current net" (R1 — a ONE-ROW render of the
/// radio-CONFIRMED active net: its number, NETID, hop type and its frequencies
/// under column headings — round-5 BD1/BD2 heads that last column
/// "Frequencies (MHz)" and renders it through the shared
/// <see cref="HopNetDisplay"/> vocabulary, and the same lazy path additionally
/// reads a LIST current net's HOPLIST once per session (BC4); the picker's
/// position has no effect on any of it), "Status" (R2 — Hopnum, the generation
/// lifecycle indicator, the §7 no-net-id line, the 7-state sync chip and Send
/// Sync; the coupler tune outcome shows on the spine's TUNE chip), and
/// "Select net" (R3 — a net NUMBER PICKER, 0-9 wrapping, the §7 NET INFO VIEW
/// beside it, and the select action: a net change regenerates the hopset,
/// tunes the coupler, drops sync). The round-11 order puts the radio's own
/// state first and the operator's action last, with the PICKED net's read-back
/// where the pick is made. An unreported sync state renders "—", never a
/// default; SEND SYNC greys when there is no hopset (SY would be a silent
/// no-op, probe R9).
///
/// AB2/AB3 deletions: the green "Selected" chip is gone (the select gate's
/// disabled-with-reason on the current net carries that fact, and the Current
/// net row names it), and so is the whole Time section — the radio clock now
/// lives ONCE, on Mode settings → HOP, over DeviceSettingsViewModel's TOD
/// mirror. This VM therefore has no IsPickedNetSelected, no RadioTodText, no
/// SetTimeFromDevice, and its landing load no longer sends TI.
///
/// Wire behavior beyond one-tap-one-command (all visible in the Console):
/// - PER-NET lazy load (§M3, kept by round-3 V5): the picker is a VIEW
///   cursor. Landing on a net sends the cheap read-only `DIS n` for THAT net
///   — once per net per session (a re-visit re-renders from the mirror,
///   sending nothing). The same once-per-session read covers the CONFIRMED
///   current net, because the R1 row renders from that mirror entry. The
///   pane never sends `DIS`-all-nets. The first time a session shows the
///   pane (Ready + confirmed HOP) it requests `DIS n` + SH — and NOTHING
///   else: the landing `TI` went with the Time frame (AB3). There is no
///   manual Refresh any more (R4).
/// - VIEW vs SELECT (§M4, U7): spinning the picker NEVER sends `NET n` —
///   selecting a net regenerates the hopset and TUNES THE COUPLER
///   (transmits). Only the separate Select command sends `NET n`, through
///   the unchanged warning-then-Proceed flow. Which net is CURRENT comes
///   ONLY from the radio's confirmed NET report (no optimism) — it drives
///   the R1 row and closes the select gate on that net.
/// - Post-select re-read: after an app-initiated NET n, one SH goes out
///   when the generation lifecycle ends, because Hopnum and the sync state
///   arrive only in the SH block — the HOP-side analogue of
///   ChannelSurface.Select's CH nn + SH (app-initiated mutation, app
///   re-reads; the radio announces generation but never the new Hopnum).
/// </summary>
public partial class HopViewModel : ObservableObject
{
    /// <summary>Bounded escape for a select that never produces an outcome
    /// (Stage 8 deferred-ledger fix): a radio-REFUSED net select emits no
    /// generation lifecycle and no No-Hopset line, which previously left the
    /// SEND SYNC gate closed until the next select or reconnect. After this
    /// deadline the pending-select gates release and the one-shot SH re-read
    /// goes out anyway (visible in the Console), so the display recovers the
    /// radio's truth instead of waiting forever. The escape never fires while
    /// the radio has announced generation — that lifecycle always ends with
    /// a clearing line (Hopnum / No Hopset / TUNE terminal / HOP prompt).</summary>
    internal static readonly TimeSpan SelectRereadTimeout = TimeSpan.FromSeconds(10);

    // ---- Round 11 §7 -------------------------------------------------------

    /// <summary>The §7 net-info view's FIRST-line header, exact. A constant so
    /// the markup (x:Static) and the pins read one source.</summary>
    public const string NetInfoHeading = "Net · Net ID · Type";

    /// <summary>The generation-attempt refusal, in the operator's words (R13:
    /// no radio token). Projected from <c>HopSurface.GenerationRefusedNoNetId</c>
    /// — the SURFACE owns the state machine because its triggers span both HOP
    /// view models; this VM only renders.</summary>
    public const string NoNetIdStatus = "No net ID — program a net ID first";

    private readonly HopSurface _hop;
    private readonly RadioSession _session;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _syncContext;

    // Once-per-session flags (reset whenever the session leaves Ready).
    private bool _loadedThisSession;

    // Post-select SH re-read arming. Two completion signals (audit F4):
    // generation observed then ended, OR a No-Hopset report for the selected
    // net (a programmed-but-hopset-less select emits Wait.../No Hopset with
    // no Generating line at all). The No-Hopset counter baseline is
    // re-absorbed until the radio confirms the TARGET net, because straggler
    // answers to queries sent BEFORE the select can carry a No_Hopset line
    // for the OLD net (observed live: the Stage 5 gate's refresh-trio SH
    // answer interleaved the select exactly that way). Third completion
    // signal (Stage 8): the SelectRereadTimeout deadline, for refused
    // selects that produce no outcome at all.
    private bool _awaitingSelectReread;
    private bool _sawGenerating;

    /// <summary>The generation count when this select went out (audit round 1).
    /// The flag beside it is a SAMPLE, and <see cref="Refresh"/> is marshalled:
    /// the whole lifecycle a select provokes can be parsed between two of its
    /// runs, and then the flag never sees TRUE, the select stays armed for the
    /// full 10-s escape, and it eventually sends a SECOND `SH` — two reads for
    /// one lifecycle, which is exactly what I-3 forbids. A count advance with
    /// generation already false says the lifecycle happened and finished,
    /// whether or not anything watched it happen.</summary>
    private int _selectGenerationBaseline;

    /// <summary>
    /// ROUND 15 N1 (§3.2): the generation count this pane has already
    /// ACCOUNTED FOR — i.e. read the radio's state after. Deliberately
    /// SEPARATE state from the select flow's <see cref="_sawGenerating"/>: the
    /// select flow owns the lifecycle it started and its 10-s escape, and this
    /// observes everyone else's.
    ///
    /// <para>A COUNT rather than the "I saw generation go TRUE" flag the plan
    /// sketched (§3.2 pseudocode), because <see cref="Refresh"/> runs
    /// MARSHALLED: on the phone every notification hops to the UI thread, and
    /// a whole lifecycle — the <c>Generating Hopset...</c> line and its
    /// clearing line — can be parsed between two runs of this method. The flag
    /// then reads false at both ends and the pane never re-reads, which is the
    /// very silence N1 exists to end. A count only goes up, so it cannot be
    /// missed; two lifecycles that coalesce into one run cost one read rather
    /// than two, which is the honest floor here.</para>
    /// </summary>
    private int _accountedGenerations;
    private int _selectTarget = -1;
    private int _noHopsetBaseline;
    private DateTimeOffset _selectDeadline;
    // Intentionally never disposed: the VM is a DI singleton with app
    // lifetime (MauiProgram), so the one timer lives as long as the
    // process and is merely parked between selects. If this VM ever
    // becomes transient, it must gain IDisposable and dispose the timer.
    private ITimer? _selectEscapeTimer;

    // §M3 per-net lazy load: the nets whose `DIS n` has already gone out this
    // session. Cleared with the session.
    private readonly HashSet<int> _netsQueried = [];

    // BC4 (round 5): the nets whose `HOPLIST n` has already gone out this
    // session. SEPARATE from _netsQueried because the trigger is different —
    // a hoplist read only makes sense once DIS has confirmed the net is LIST.
    // This pane's scope is the CURRENT net alone (its row is the only display
    // that shows a count), and it has no manual Refresh, so the set clears
    // ONLY on session reset — like the DIS cache beside it. The settings
    // pane owns its own once-set for all ten nets (the round-4 "owned
    // separately" precedent; a duplicate read across panes is an accepted
    // cheap read).
    private readonly HashSet<int> _hopListsQueried = [];

    // §M1 net picker — an APP-SIDE view cursor (0-9), not radio state: it
    // says which net the operator is LOOKING at, never which net the radio
    // is on (that is the R1 Active* row and the select gate, confirmed-only).
    // Moved only by NetUp/NetDown, so no setter can send anything.
    private int _pickedNet;
    public int PickedNet => _pickedNet;

    [ObservableProperty] private string pickedNetText = "0";

    // ---- R1 "Current net" row — the CONFIRMED current net ONLY ------------
    // Every field renders from the radio's reports about the net the radio
    // says it is on; the picker cannot move any of them. "—" until reported.
    /// <summary>"NET 3" / "—" (the radio has reported no current net).</summary>
    [ObservableProperty] private string activeNetText = "—";
    /// <summary>"12345678" / the wire's own "XXXXXXXX" / "—" (unreported).</summary>
    [ObservableProperty] private string activeNetIdText = "—";
    [ObservableProperty] private string activeTypeText = "—";
    /// <summary>The BD2 value cell, under the shared "Frequencies (MHz)"
    /// heading and so a BARE number: the center (NB, "11.565") / the band
    /// (WB, "2.000–8.000") / the frequency count (LIST, "8 freqs", with
    /// "Frequency list" until the HOPLIST answer lands) / "not programmed" /
    /// "—". Built by <see cref="HopNetDisplay"/>, which the settings pane's
    /// rows use too.</summary>
    [ObservableProperty] private string activeHopsetText = "—";

    /// <summary>Round 7 (DD): the value column's HEADER, following the
    /// current net's confirmed type (Center / Band / Hoplist), generic
    /// otherwise. The settings net-list keeps the generic constant.</summary>
    [ObservableProperty] private string valueColumnHeading = HopNetDisplay.ValueHeading;

    /// <summary>"List_Invalid": the radio refuses to sync on this (current,
    /// LIST-type) net — hoplist too short.</summary>
    [ObservableProperty] private bool isPickedNetListInvalid;
    [ObservableProperty] private bool canSelectPickedNet;
    [ObservableProperty] private string selectDisabledReason = "";

    // Round 6 (CD): the select-confirmation view state (IsWarningOpen /
    // WarningText / PendingNetLabel / _pendingNet) is GONE with the warning
    // flow — Select Net sends immediately.

    [ObservableProperty] private string hopnumText = "Hopnum —";
    [ObservableProperty] private bool isGenerating;

    [ObservableProperty] private string syncChipText = "—";
    [ObservableProperty] private bool isSyncConfirmed;
    [ObservableProperty] private bool isInSync;
    [ObservableProperty] private bool isSyncFailed;

    [ObservableProperty] private bool canSendSync;
    [ObservableProperty] private string sendSyncDisabledReason = "";

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    // ---- §7 net info view — the PICKED net, beside the picker --------------
    // The picker is a VIEW cursor, so this stack describes the net the operator
    // is LOOKING at (the R1 "Current net" frame is the radio's own net and is
    // untouched by it). Data is the SAME mirror projection the settings
    // net-list rows read, through the same shared vocabulary, and the read is
    // the per-pick `DIS n` this pane already sends — §7 adds no tier.

    /// <summary>Line 1's value: <c>{0n} · {netid} · {type}</c>, where the net
    /// ID renders the mirror's THIRD state (the radio's own "XXXXXXXX" when
    /// reported-unprogrammed, "—" when unreported).</summary>
    [ObservableProperty] private string pickedNetInfoText = "00 · — · —";

    /// <summary>Line 2's header, by CONFIRMED type: Center (MHz) / Low–High
    /// (MHz) / Frequencies / "—".</summary>
    [ObservableProperty] private string pickedNetValueHeading = "—";

    /// <summary>Line 2's value: the center / the band / "{count} stored" /
    /// "—".</summary>
    [ObservableProperty] private string pickedNetValueText = "—";

    // ---- §7 status projection ----------------------------------------------

    /// <summary><see cref="NoNetIdStatus"/> while the surface's
    /// generation-attempt state machine holds a refusal, "" otherwise. Rendered
    /// in the Status frame in the StatusText style.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string statusText = "";

    public bool HasStatusText => StatusText.Length > 0;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 3). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    public HopViewModel(
        HopSurface hop, RadioSession session, TimeProvider time,
        ICampaignSignal? campaign = null)
    {
        _hop = hop;
        _session = session;
        _time = time;
        _campaign = campaign;
        // Q10 convention: constructed on the UI thread (DI first resolution);
        // the escape timer's wake-up is the ONE input that does not arrive
        // pre-marshalled, so it posts through the captured context.
        _syncContext = SynchronizationContext.Current;
        hop.Changed += (_, _) => Refresh();
        // §7: the refusal is a SURFACE-derived fact with its own (marshalled)
        // notification — it is not a mirrored radio property, so it does not
        // arrive through Changed.
        hop.GenerationRefusedNoNetIdChanged += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready) ResetSessionFlags();
            Refresh();
        };
        // THE ONE OWED READ (D1). Every deferral point in this pane leaves its
        // own state BEHIND — the load latch unset, the per-net cache unfilled,
        // the generation account unabsorbed, the select outcome unclaimed — so
        // one Refresh at the campaign's end pays all of them. This subscription
        // is also the ESCAPE TIMER's re-arm: a timer that fired during a
        // campaign found nothing to do, and campaign end is the moment its work
        // becomes possible (§4 per-producer correction).
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool HopReady => Ready && _hop.IsHopConfirmed;

    private void ResetSessionFlags()
    {
        _loadedThisSession = false;
        _netsQueried.Clear();          // per-net DIS cache is session-scoped
        _hopListsQueried.Clear();      // …and so is the BC4 hoplist once-set
        _awaitingSelectReread = false;
        _sawGenerating = false;
        // §3.2: nothing is owed across a session drop. The mirror's own count
        // resets with the connect, so both accounts reset with it.
        _accountedGenerations = 0;
        _selectGenerationBaseline = 0;
        _selectTarget = -1;
        _noHopsetBaseline = 0;
        ParkEscapeTimer();
    }

    private void Refresh()
    {
        // Lazy first load (once per session): the pane's data — the PICKED
        // net's detail (DIS n, §M3 — never DIS-all) and current net/Hopnum/
        // sync (SH) — both queries, both visible in the Console. No TI: the
        // clock left this pane (AB3).
        //
        // D1 QUIESCE: a clone campaign owns the wire. Each deferral point below
        // leaves its OWN state behind, so nothing is lost and nothing needs a
        // second bookkeeping flag — the campaign-end Refresh meets exactly the
        // same conditions this one did.
        bool campaignActive = _campaign?.CampaignActive == true;
        if (HopReady && !_loadedThisSession && !campaignActive)
        {
            _loadedThisSession = true;
            RequestPaneData();
        }

        // §M3: catch the picked net (and the confirmed current net) up
        // whenever the pane becomes usable — the operator may have spun the
        // picker while the radio was out of HOP, and the radio may have
        // reported a current net since. Idempotent: the per-net cache makes
        // every later call a no-op.
        EnsureNetDetailLoaded();

        // Post-select re-read: Hopnum/sync live only in the SH block, so one
        // SH goes out when the select outcome arrives — either the generation
        // lifecycle ends (Generating Hopset... observed, then cleared by a
        // terminal line) or the radio answers No Hopset for the selected net
        // (audit F4: a hopset-less select never emits a Generating line).
        bool generating = _hop.IsGeneratingHopset;
        bool selectFlowSentSh = false;
        // D1 QUIESCE, the PARKED ESCAPE (§4 per-producer correction): while a
        // campaign owns the wire this whole block stands down. `_awaiting-
        // SelectReread`, `_sawGenerating` and the baselines are left exactly as
        // they are, and the escape timer is NOT parked — so the campaign-end
        // Refresh re-evaluates the same conditions (the deadline has only got
        // further past) and the one `SH` goes out then.
        if (_awaitingSelectReread && !campaignActive)
        {
            if (generating) _sawGenerating = true;

            bool targetConfirmed = _hop.CurrentNet.IsConfirmed
                && _hop.CurrentNet.Value == _selectTarget;
            if (!targetConfirmed)
                _noHopsetBaseline = _hop.NoHopsetCount;   // absorb pre-select stragglers

            // Either the pane WATCHED the lifecycle (the flag) or it can see
            // that one HAPPENED (the count) — both mean "the select's outcome
            // has arrived", and the second is the only one that survives a
            // marshalled Refresh (audit round 1).
            bool generationEnded = !generating
                && (_sawGenerating || _hop.GenerationCount != _selectGenerationBaseline);
            bool noHopsetOutcome = targetConfirmed && !generationEnded
                && _hop.NoHopsetCount != _noHopsetBaseline;

            // Stage 8 escape: a refused select never generates and never
            // reports No-Hopset — after the deadline, release the gates and
            // re-read anyway. Never while the radio is (or was) generating:
            // that lifecycle has its own guaranteed clearing lines.
            bool escapeTimedOut = !generating && !_sawGenerating
                && _hop.GenerationCount == _selectGenerationBaseline
                && _time.GetUtcNow() >= _selectDeadline;

            if (generationEnded || noHopsetOutcome || escapeTimedOut)
            {
                _awaitingSelectReread = false;
                _sawGenerating = false;
                _selectGenerationBaseline = _hop.GenerationCount;
                _selectTarget = -1;
                ParkEscapeTimer();
                if (Ready) { _hop.RequestStatus(); selectFlowSentSh = true; }
            }
        }

        // ROUND 15 N1 (plan §3.2) — THE LIFECYCLE OBSERVER, whoever started it.
        // (Contract note: the pseudocode's "_observedGenerating" flag is a
        // generation COUNT here — see the field. Same semantics, minus the
        // lifecycles a marshalled Refresh would otherwise never see.)
        // The block above only closes a lifecycle THIS PANE started. Every
        // other producer — a mode re-entry, a settings-pane hopset write, a
        // clone campaign lapping through HOP — regenerates too, and after a
        // generation the sync state is unreported (§3.1) and Hopnum may have
        // moved, both of which live ONLY in the `SH` block. So the pane reads
        // once at the END of any generation it observed, which is what makes
        // the chip stop lying across a mode lap.
        //
        // I-3, EXACTLY ONE PANE `SH` PER LIFECYCLE: the select flow runs
        // first and claims the lifecycle it started, and the observer yields
        // to it via selectFlowSentSh. (Core's own channel-domain re-poll `SH`
        // at the next `SSB>` is separate, pre-existing and expected — I-3 is a
        // PANE-PRODUCER count, not a wire total.) The observer arms nothing
        // and parks nothing: the 10-s escape belongs to the select flow, and a
        // session drop clears this flag with the rest.
        //
        // D1 QUIESCE: the observer defers by NOT ABSORBING —
        // `_accountedGenerations` stays behind, so the lifecycles a campaign's
        // own HOP lap generated are still owed and the campaign-end Refresh
        // reads for them.
        int generations = _hop.GenerationCount;
        if (campaignActive)
        {
            // nothing absorbed, nothing read
        }
        else if (!HopReady)
        {
            // Out of HOP (or not connected): nothing is owed. Whatever the
            // radio generated while the operator was elsewhere is covered by
            // the landing read of the next entry — and nothing may be read
            // from a mode the operator is not in.
            _accountedGenerations = generations;
        }
        else if (generations != _accountedGenerations && !generating)
        {
            // ONE READ PER LIFECYCLE (I-3), not one per catch-up. If a
            // marshalled Refresh missed two of them, two lifecycles were
            // observed and each owes its read — the count is what makes that
            // knowable at all. The select flow's own read covers one.
            int owed = generations - _accountedGenerations;
            _accountedGenerations = generations;
            if (selectFlowSentSh) owed--;
            for (int i = 0; i < owed && Ready; i++) _hop.RequestStatus();
        }

        IsGenerating = generating;

        var hopnum = _hop.HopNum;
        HopnumText = hopnum.IsConfirmed
            ? $"Hopnum {hopnum.Value.ToString("0000", CultureInfo.InvariantCulture)}"
            : "Hopnum —";

        var sync = _hop.SyncState;
        IsSyncConfirmed = sync.IsConfirmed;
        SyncChipText = sync.IsConfirmed ? SyncText(sync.Value) : "—";
        IsInSync = sync.IsConfirmed && sync.Value == HopSyncState.InSync;
        IsSyncFailed = sync.IsConfirmed && sync.Value == HopSyncState.SyncFailed;

        // Audit F6: also gated while a hopset is generating or a select is
        // awaiting its re-read — SY mid-generation is an UNPROBED wire fact,
        // so the UI must not offer it (conservative until a probe says
        // otherwise).
        CanSendSync = HopReady && hopnum.IsConfirmed && hopnum.Value > 0
            && !generating && !_awaitingSelectReread;
        SendSyncDisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_hop.IsHopConfirmed ? "SEND SYNC is HOP-domain — waiting for the radio to confirm HOP."
            : generating ? "Hopset generation in progress — SY mid-generation is unprobed; wait for the lifecycle to finish."
            : _awaitingSelectReread ? "Net selection in progress — waiting for the radio's outcome and re-read (SH; releases after 10 s if no outcome arrives)."
            : !hopnum.IsConfirmed ? "Waiting for the radio to report Hopnum (SH)."
            : hopnum.Value == 0 ? "No hopset on the current net — SY would be a silent no-op. Select a programmed net first."
            : "";

        AreControlsEnabled = HopReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_hop.IsHopConfirmed ? "HOP controls wait for the radio to confirm HOP mode."
            : "";

        // §7: the surface owns the state machine (its triggers span both HOP
        // view models); this is the whole of the VM's part in it.
        StatusText = _hop.GenerationRefusedNoNetId ? NoNetIdStatus : "";

        UpdateNetDisplays();

        SendSyncCommand.NotifyCanExecuteChanged();
    }

    private void RequestPaneData()
    {
        EnsureNetDetailLoaded();    // DIS n — per net, never DIS-all (§M3)
        _hop.RequestStatus();       // SH
    }

    /// <summary>§M3/V5 — the read-only per-net detail query (`DIS n`), sent
    /// AT MOST ONCE per net per session. Cheap and read-only: unlike `NET n`
    /// it neither regenerates the hopset nor tunes the coupler, so it is safe
    /// to fire on every picker landing. Two nets want it: the PICKED one
    /// (it gates Select on programmed/unprogrammed) and the radio's CONFIRMED
    /// current one (the R1 "Current net" row renders from that mirror entry,
    /// so without this it would sit at "—" until the operator happened to
    /// spin onto the active net).</summary>
    private void EnsureNetDetailLoaded()
    {
        if (!HopReady) return;
        RequestNetOnce(_pickedNet);
        var current = _hop.CurrentNet;
        if (current.IsConfirmed) RequestNetOnce(current.Value);
        EnsureCurrentNetHopListLoaded();
    }

    private void RequestNetOnce(int net)
    {
        // D1 QUIESCE: nothing joins the per-net cache while a campaign owns the
        // wire, so the net stays owed and the campaign-end Refresh asks once.
        if (_campaign?.CampaignActive == true) return;
        if (!_netsQueried.Add(net)) return;          // cached — re-render only
        _hop.RequestNet(net);
    }

    /// <summary>BC4 — the lazy HOPLIST read, scoped to the CURRENT net ONLY.
    /// The "Current net" row is the only place this pane shows a value for a
    /// LIST net, and NO captured <c>DIS</c> answer carries a hoplist, so
    /// <c>HOPLIST n</c> is the only way that cell can say "8 freqs" instead of
    /// the "Frequency list" fallback. Sent at most ONCE per net per session,
    /// and only after <c>DIS</c> has CONFIRMED the net is LIST — the trigger
    /// is a reported type, never a guess. The picked net deliberately does not
    /// trigger it: the picker is a view cursor here and shows no value cell.
    /// <para>This is the ONE Core-write-family wrapper this file may name
    /// (round-5 X6; <c>GuiOutScopeGuardTests</c> pins it).</para></summary>
    private void EnsureCurrentNetHopListLoaded()
    {
        var current = _hop.CurrentNet;
        if (!current.IsConfirmed) return;
        if (!_hop.Nets.TryGetValue(current.Value, out var net)
            || net?.Type != HopType.List) return;
        // D1 QUIESCE: the once-set is left untouched, so this stays owed.
        if (_campaign?.CampaignActive == true) return;
        if (!_hopListsQueried.Add(current.Value)) return;
        _hop.RequestHopList(current.Value);
    }

    // ---- Net picker (§M1) — VIEW only; it never sends NET n (§M4) ----------

    [RelayCommand]
    private void NetUp() => MovePicker(+1);

    [RelayCommand]
    private void NetDown() => MovePicker(-1);

    private void MovePicker(int delta)
    {
        _pickedNet = (_pickedNet + delta + 10) % 10;      // 0-9, wrapping
        PickedNetText = _pickedNet.ToString(CultureInfo.InvariantCulture);
        Refresh();      // re-renders the select gate and fires the per-net DIS
    }

    /// <summary>Renders the two net displays: the R1 "Current net" row (the
    /// CONFIRMED current net — the picker cannot touch it) and the R3
    /// picker's select gate + markers.</summary>
    private void UpdateNetDisplays()
    {
        var current = _hop.CurrentNet;

        // ---- R1: the ACTIVE net, confirmed-only ----------------------------
        if (current.IsConfirmed)
        {
            ActiveNetText = NetLabel(current.Value);
            _hop.Nets.TryGetValue(current.Value, out var active);
            (ActiveNetIdText, ActiveTypeText, ActiveHopsetText) =
                NetDisplay(current.Value, active);
            // Round 7 (DD, owner): the value HEADER names what the cell holds
            // for the confirmed type — Center / Band / Hoplist — generic until
            // a type is reported, and generic for a reported-unprogrammed net
            // (its Hoptype WB line is a wipe artifact, not a programmed band).
            ValueColumnHeading =
                active is null || active.IsReportedUnprogrammed
                    ? HopNetDisplay.ValueHeading
                    : HopNetDisplay.ValueHeadingFor(active.Type);
        }
        else
        {
            // The radio has reported no current net this session.
            ActiveNetText = "—";
            ActiveNetIdText = "—";
            ActiveTypeText = "—";
            ActiveHopsetText = "—";
            ValueColumnHeading = HopNetDisplay.ValueHeading;
        }

        // ---- R3: the picker's markers and select gate ----------------------
        int n = _pickedNet;
        bool isSelected = current.IsConfirmed && current.Value == n;

        IsPickedNetListInvalid = isSelected && _hop.IsHopListInvalid;

        _hop.Nets.TryGetValue(n, out var net);

        // ---- §7: the net info view, over the SAME mirror projection --------
        var hopList = _hop.HopLists.TryGetValue(n, out var freqs) ? freqs : null;
        var (netId, type, _) = HopNetDisplay.Describe(net, hopList);
        PickedNetInfoText = string.Join(
            " · ", n.ToString("00", CultureInfo.InvariantCulture), netId, type);
        (PickedNetValueHeading, PickedNetValueText) =
            HopNetDisplay.InfoValueLine(net, hopList);

        bool programmed = net?.NetId is not null;
        // The gate blocks on anything but a reported ID, but the REASON must
        // not over-claim either: only the marker (the radio's own X-form)
        // licenses "is not programmed" — an ID nobody has mentioned is still
        // the waiting case (round-4 Phase D).
        bool idReported = programmed || net?.IsReportedUnprogrammed == true;

        CanSelectPickedNet = HopReady && programmed && !isSelected;
        SelectDisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_hop.IsHopConfirmed ? "Net selection is HOP-domain — waiting for the radio to confirm HOP."
            : isSelected ? NetLabel(n) + " is already the radio's current net."
            : !idReported ? "Waiting for the radio to report " + NetLabel(n) + " (DIS)."
            : !programmed ? NetLabel(n) + " is not programmed — there is nothing to select."
            : "";

        SelectPickedNetCommand.NotifyCanExecuteChanged();
    }

    private static string NetLabel(int net)
        => "NET " + net.ToString(CultureInfo.InvariantCulture);

    /// <summary>A net's three displayed fields from the mirror, in the
    /// constitution's three display states: unreported ("—", PER FIELD — no
    /// line has covered this net, or none covered that field), CONFIRMED
    /// unprogrammed (the radio's own "XXXXXXXX" + "not programmed"), and
    /// reported.
    ///
    /// <para>The unprogrammed state comes ONLY from the mirror's
    /// <see cref="HopNet.IsReportedUnprogrammed"/> marker — the radio's own
    /// <c>NETID n XXXXXXXX</c> line. It used to be inferred from a null ID,
    /// which OVER-CLAIMED: a record created by a <c>Hoptype</c> line alone has
    /// a null ID that nobody reported, and this row announced it as
    /// confirmed-unprogrammed (round-4 Phase D). A markerless null is now an
    /// unheard field and reads "—", exactly as the HOP-settings pane renders
    /// it — both panes read the same mirror fields through the same shared
    /// vocabulary, so they cannot disagree.</para>
    ///
    /// <para>Round 5 (BD1/BD2) moved the VALUE cell into that shared
    /// vocabulary too: the column is headed "Frequencies (MHz)" and the cell
    /// is a bare number — the center, the WB band "low–high" (from the newly
    /// mirrored edges; it used to read the placeholder "Wideband"), or a LIST
    /// net's frequency COUNT. This method is now the whole of it: one call to
    /// <see cref="HopNetDisplay.Describe"/>, the same call the settings pane
    /// makes.</para></summary>
    private (string NetId, string Type, string Hopset) NetDisplay(int net, HopNet? entry)
        => HopNetDisplay.Describe(
            entry, _hop.HopLists.TryGetValue(net, out var freqs) ? freqs : null);

    /// <summary>All seven sync states, in PROSE (the old two-lamp display was
    /// lossy and hid Sync_Failed; plan §3 kept every state but printed the
    /// radio's own underscore tokens).
    /// <para>ROUND 13 §4 A2 (item 10, owner 2026-08-19): the chip reads as
    /// English now. Constitution §3.2 — no raw wire tokens operator-facing on
    /// the surfaces this round touches; the raw <c>Sync_Failed</c> line is
    /// still in the Console feed, which is where the evidence belongs, and the
    /// prose replaces it on the chip only. The mapping is one-to-one: no state
    /// was merged, so nothing became less visible. The <c>SYNC: </c> prefix
    /// the XAML used to prepend died with the tokens — these strings are
    /// self-identifying.</para>
    /// <para>BYTE-EXACT, pinned in HopViewModelTests: the owner saw this draft
    /// and did not object, and the chip's fixed width was budgeted against the
    /// longest of them.</para></summary>
    private static string SyncText(HopSyncState s) => s switch
    {
        HopSyncState.NoSync => "No sync",
        HopSyncState.InSync => "In sync",
        HopSyncState.AwaitingSync => "Awaiting sync",
        HopSyncState.SendingSyncRequest => "Sync request sent",
        HopSyncState.SyncRequestReceived => "Sync request received",
        HopSyncState.SendingSyncResponse => "Sync response sent",
        HopSyncState.SyncFailed => "Sync failed",
        _ => "—",
    };

    // ---- Select flow (IMMEDIATE — UI tweaks round 6, CD) -------------------
    // The round-3 once-per-session warning was deleted by owner ruling
    // (2026-08-13): Select Net sends `NET n` on the press. The GATING is
    // unchanged — Ready + a reported-programmed net + not the current net —
    // and the disabled button with its reason is the remaining friction.
    // In-body guards repeat CanExecute: ICommand.Execute does not consult it.

    private bool CanExecuteSelectPickedNet() => CanSelectPickedNet;

    /// <summary>§M4 — the ONLY path that sends `NET n`. Selecting a net
    /// regenerates the hopset and TUNES THE COUPLER (transmits); the picker
    /// itself never comes near this.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteSelectPickedNet))]
    private void SelectPickedNet()
    {
        if (!HopReady || !CanSelectPickedNet) return;
        int net = _pickedNet;
        var current = _hop.CurrentNet;
        if (current.IsConfirmed && current.Value == net) return;   // re-click guard
        IssueSelect(net);
    }

    private void IssueSelect(int net)
    {
        _awaitingSelectReread = true;
        _sawGenerating = false;
        _selectGenerationBaseline = _hop.GenerationCount;
        _selectTarget = net;
        _noHopsetBaseline = _hop.NoHopsetCount;
        _selectDeadline = _time.GetUtcNow() + SelectRereadTimeout;
        StartEscapeTimer();
        // ROUND 14 C (plan §4-C, owner ruling R10): the operator's select is a
        // coupler-convergence TRIGGER. The target net's REPORTED type comes off
        // the same mirror projection the picker renders — null when the radio
        // has not said, which is exactly the case the policy declines to guess
        // at. The clone campaign keeps the raw `SelectNet`.
        _hop.SelectNetWithCouplerPolicy(net, ReportedTypeOf(net));
        Refresh();   // reflect the pending-select gates (F6) immediately
    }

    /// <summary>The net's type AS REPORTED, straight off the surface's mirror
    /// projection — the two unknowns (the net is absent from the table; it is
    /// present with no type reported) collapse to null, which the coupler
    /// policy reads as "no opinion".</summary>
    private HopType? ReportedTypeOf(int net)
        => _hop.Nets.TryGetValue(net, out var entry) ? entry.Type : null;

    /// <summary>One-shot wake-up for the Stage 8 escape: a refused select
    /// produces no hop-domain change event, so nothing would run Refresh()
    /// and check the deadline. The deadline (checked in Refresh) is the
    /// authority; the timer only guarantees a Refresh happens after it.</summary>
    private void StartEscapeTimer()
    {
        var due = SelectRereadTimeout + TimeSpan.FromMilliseconds(50);
        if (_selectEscapeTimer is null)
            _selectEscapeTimer = _time.CreateTimer(
                _ => Post(Refresh), null, due, Timeout.InfiniteTimeSpan);
        else
            _selectEscapeTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void ParkEscapeTimer()
        => _selectEscapeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void Post(Action action)
    {
        if (_syncContext is not null) _syncContext.Post(_ => action(), null);
        else action();
    }

    // ---- Sync ---------------------------------------------------------------

    private bool CanExecuteSendSync() => CanSendSync;

    [RelayCommand(CanExecute = nameof(CanExecuteSendSync))]
    private void SendSync()
    {
        if (!CanSendSync) return;
        _hop.SendSync();
    }

    // ---- Time ----------------------------------------------------------------
    // GONE (round-4 AB3): the radio clock left this pane for Mode settings →
    // HOP, and DeviceSettingsViewModel is now the app's ONE clock state source
    // (it already mirrors the TI-reported TOD and owns the TIME+DAT+DAY set).
    // So this VM no longer carries RadioTodText / SetTimeFromDevice, and the
    // once-per-session pane load no longer sends TI — the clock component
    // loads itself where it lives.

    // R4 (round 3): the pane has NO manual refresh any more. The first-Ready
    // lazy load and the post-select SH re-read are the only reads the pane
    // issues on its own; the per-net `DIS n` still fires on picker landings.
}
