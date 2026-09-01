using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>HOP slice (Stage 5, plan §4.3). Exposes the net table mirrored
/// from DIS/SH lines, the current-net / Hopnum / sync / generation slices,
/// the radio TOD, and the explicit intents: SelectNet, SendSync,
/// SetTimeOfDay, and the queries the panes' lazy loads and Refreshes use
/// (DIS n / DIS / SH / TI). The OperatingMode property is watched so
/// consumers can grey HOP-domain controls with a reason outside HOP.
///
/// UI-tweaks round 3 (X1/X2, owner-confirmed): the surface is no longer
/// SELECT-ONLY. It carries the five PROGRAMMING intents the "Net programming"
/// editor needs — net ID, hop type, and the three hopset writers.
///
/// UI-tweaks round 5 (X6, owner-confirmed): three more — the hoplist READ, the
/// per-frequency LIST remove, and the whole-net wipe behind "Clear net" — as
/// the editor became radio-native (per-field writes, a real list UI). Still
/// NOT here, and still GUI-out: DOIT (regeneration must never happen behind
/// the operator's back). The reasons are recorded in Falcon.Core.Tests
/// GuiOutScopeGuardTests.
///
/// UI-tweaks round 11 (X9, owner ruling R11): the WB EXCLUSION BANDS leave the
/// GUI-out list too — exactly three of them (the per-band set, the
/// sentinel-scoped bulk read, the per-band delete), for the HOP-settings pane's
/// new "Exclusion bands" section. <c>DeleteAllExcludeBands</c> stays guarded.
///
/// <para><b>Round 11 §7 — the generation-attempt state machine lives HERE.</b>
/// The triggers span BOTH HOP view models (net select is HopViewModel's, every
/// hopset-family write is HopSettingsViewModel's) and both resolve the SAME DI
/// singleton surface, so the surface is the only place the two can share a
/// window without cross-VM plumbing.</para></summary>
public sealed class HopSurface : RadioSurface
{
    /// <summary>The generation-attempt window: the <c>HopNoNetId</c> counter as
    /// it stood when the last generation-triggering wrapper was called, and
    /// whether a window is open at all. A COUNTER is what Core exposes (a
    /// repeat <c>NO NET ID</c> carries no state change of its own), so the
    /// state machine DIFFS it.</summary>
    private int _windowBaseline;
    private bool _windowOpen;

    /// <summary>The highest counter value this surface has observed. A live
    /// count BELOW it means Core zeroed the counter — i.e. a fresh connection
    /// (HopState.ResetForConnect) — so everything derived from the old count
    /// belongs to a radio that is gone.</summary>
    private int _highWaterCount;

    private bool _refused;

    /// <summary>Round 14 C: the coupler CONVERGENCE policy
    /// (plan/plan-round14.md §4-C, owner ruling R10), consulted by
    /// <see cref="SelectNetWithCouplerPolicy"/> and by nothing else here.
    ///
    /// <para><b>OPTIONAL on purpose.</b> The app's composition
    /// (<c>MauiProgram</c>) always supplies it. The compositions that do NOT
    /// are exactly the ones the policy must never reach: the clone campaign's
    /// test stacks and the <c>bench/</c> harnesses, both of which drive the
    /// RAW intents. A null policy therefore means "this composition has no
    /// policy", and <see cref="SelectNetWithCouplerPolicy"/> then behaves as
    /// plain <see cref="SelectNet"/> — a pinned contract, not an
    /// accident.</para></summary>
    private readonly CouplerPolicy? _coupler;

    public HopSurface(Prc138Radio radio, CouplerPolicy? coupler = null)
        : base(radio,
            RadioProperty.HopCurrentNet, RadioProperty.HopNets,
            RadioProperty.HopNum, RadioProperty.HopSyncState,
            RadioProperty.HopGeneratingHopset, RadioProperty.HopListInvalid,
            RadioProperty.HopLists, RadioProperty.HopNoHopset,
            RadioProperty.HopExcludeBands,
            // Round 14 B (plan/plan-round14.md §4-B, owner ruling R2): the
            // internal coupler is watched HERE TOO. It is not HOP state — it is
            // the SAME RadioState mirror SsbSurface projects (SsbSurface.cs:41
            // watched set, :160 projection) — and that shared mirror is exactly
            // what makes the coupler row safe in TWO places at once: both panes
            // read one confirmed value, so they can never disagree.
            RadioProperty.InternalCoupler,
            RadioProperty.RadioTimeOfDay, RadioProperty.OperatingMode)
    {
        _coupler = coupler;

        // Deliberately NOT routed through the base's watched set: the base
        // raises Changed from its OWN handler, which was subscribed first, so a
        // consumer would read the flag one event before this handler had
        // updated it. Hooking the raise directly keeps "the flag changed" and
        // "the notification fired" the same moment. Events arrive already
        // marshalled (Prc138Radio Q10), so this one is marshalled too.
        radio.StateChanged += (_, e) =>
        {
            if (Radio.State.Hop.NoNetIdCount < _highWaterCount) AbandonWindow();
            if (e.PropertyChanged == RadioProperty.HopNoNetId) NoteNoNetIdReport();
        };
    }

    /// <summary>The selected net as reported (NET lines — the row marker
    /// comes ONLY from this, never from a click).</summary>
    public Confirmed<int> CurrentNet => Radio.State.Hop.CurrentNet;

    /// <summary>Net table mirrored from DIS/SH lines, keyed by net number.
    /// A net ABSENT here is unreported ("—"); so is a present entry whose
    /// NetId is null. The third display state — CONFIRMED unprogrammed — is
    /// carried by <see cref="HopNet.IsReportedUnprogrammed"/> (the wire's own
    /// <c>NETID n XXXXXXXX</c>), never inferred from the null.</summary>
    public IReadOnlyDictionary<int, HopNet> Nets => Radio.State.Hop.Nets;

    /// <summary>LIST-type hop frequencies per net (HOPLIST lines).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> HopLists => Radio.State.Hop.HopLists;

    /// <summary>Generated hop frequency count (Hopnum lines); 0 = no hopset
    /// — the SEND SYNC gate (SY is a silent no-op without a hopset, R9).</summary>
    public Confirmed<int> HopNum => Radio.State.Hop.HopNum;

    public Confirmed<HopSyncState> SyncState => Radio.State.Hop.SyncState;

    /// <summary>True between "Generating Hopset..." and a clearing line.</summary>
    public bool IsGeneratingHopset => Radio.State.Hop.IsGeneratingHopset;

    /// <summary>Generations STARTED this session, whoever caused them (round
    /// 15 §3.2). The flag above is a sample; this is the fact a MARSHALLED
    /// consumer can still see after a whole lifecycle went past between two of
    /// its runs. Consumers diff the count.</summary>
    public int GenerationCount => Radio.State.Hop.GenerationCount;

    /// <summary>No-Hopset report counter — the only reliable signal that a
    /// net select ended WITHOUT generation (audit F4; HopNum may already be
    /// a confirmed 0, which re-raises nothing). Consumers diff the count.</summary>
    public int NoHopsetCount => Radio.State.Hop.NoHopsetCount;

    /// <summary>"List_Invalid" reported: the CURRENT net's hoplist is too
    /// short to sync on (needs ≥3 frequencies).</summary>
    public bool IsHopListInvalid => Radio.State.Hop.IsHopListInvalid;

    /// <summary>The programmed WB exclusion bands from the last committed
    /// <c>EXC</c> read — THREE states, verbatim from the Core mirror:
    /// <c>null</c> = never read this session, <c>[]</c> = read and confirmed
    /// empty, rows otherwise. The empty-vs-unread distinction exists ONLY
    /// because the read is sentinel-bracketed (an empty table answers nothing
    /// at all), so a consumer must render the two differently, never guess.
    /// <para><b>WIRE SHAPES PROVISIONAL</b> (plan round 11 §14): only a
    /// SINGLE-band listing has ever been captured. The row shape is certain;
    /// the multi-row layout, the DEL echo variants and the band bounds are
    /// patterned off it and settled by the §14 probes.</para></summary>
    public IReadOnlyList<HopExcludeBand>? ExcludeBands => Radio.State.Hop.ExcludeBands;

    // ---- §7: the generation-attempt state machine ---------------------------

    /// <summary>The radio answered a generation attempt with <c>NO NET ID</c>:
    /// the net carries a hopset but no net ID, so nothing was generated. Set
    /// when the Core counter increments inside an open trigger window; cleared
    /// by the NEXT trigger. HopViewModel renders the operator's sentence from
    /// it — no cross-VM plumbing, because both HOP view models share this
    /// surface.</summary>
    public bool GenerationRefusedNoNetId
        // The guard makes the read CONSISTENT even before the handler below has
        // run: a live count under the high-water mark is Core's reset, and a
        // refusal from the previous session must not be readable for even one
        // event. (The handler still does the real cleanup and the raise.)
        => _refused && Radio.State.Hop.NoNetIdCount >= _highWaterCount;

    /// <summary>Raised (marshalled) when <see cref="GenerationRefusedNoNetId"/>
    /// changes. Separate from <see cref="RadioSurface.Changed"/> because it is
    /// a SURFACE-derived fact, not a mirrored radio property.</summary>
    public event EventHandler? GenerationRefusedNoNetIdChanged;

    /// <summary>Snapshot the counter and open a window — called at the ENTRY of
    /// every generation-triggering wrapper, before the send. Opening a window
    /// is also what CLEARS a previous window's refusal: the operator has just
    /// asked for something new, so last time's answer is no longer on screen.
    /// <para>The manifest is the §7 enumeration and is CLOSED: the net select
    /// plus the seven hopset-family writes the editor uses. The <c>EXC</c>
    /// family also regenerates (protocol.md) but is NOT enumerated there, and a
    /// no-net-id answer to an exclusion write has never been observed — widening
    /// this manifest is a plan amendment, not an implementation choice.</para></summary>
    private void OpenGenerationWindow()
    {
        _windowBaseline = Radio.State.Hop.NoNetIdCount;
        _highWaterCount = _windowBaseline;
        _windowOpen = true;
        SetRefused(false);
    }

    /// <summary>A <c>NO NET ID</c> report landed. It only means something
    /// INSIDE a window: an unsolicited report (or a straggler answering a query
    /// sent before any trigger) is not a refusal of anything this app asked
    /// for.</summary>
    private void NoteNoNetIdReport()
    {
        int count = Radio.State.Hop.NoNetIdCount;
        if (count > _highWaterCount) _highWaterCount = count;
        if (!_windowOpen || count == _windowBaseline) return;
        SetRefused(true);
    }

    /// <summary>Core zeroed the counter — a fresh connection. The open window
    /// and any refusal it produced described the previous radio.
    /// <para>This can raise a change event for a value the GETTER's guard was
    /// already reporting as false. That is deliberate and harmless: consumers
    /// re-render idempotently, and the alternative — staying silent — would
    /// leave a consumer that had cached the old value with no reason to look
    /// again.</para></summary>
    private void AbandonWindow()
    {
        _windowOpen = false;
        _windowBaseline = 0;
        _highWaterCount = 0;
        SetRefused(false);
    }

    private void SetRefused(bool value)
    {
        if (_refused == value) return;
        _refused = value;
        GenerationRefusedNoNetIdChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Radio TOD, verbatim TIME payload (TI answer / set echo).</summary>
    public Confirmed<string> RadioTimeOfDay => Radio.State.RadioTimeOfDay;

    /// <summary>INTCOUPLER payload verbatim ("ENABLED"/"BYPASSED" — the parser
    /// uppercases the radio's mixed-case <c>INTCoupler Enabled</c> before
    /// dispatch, ResponseParser.cs:134, so the mirror stores the caps form).
    /// <para>The SAME mirror <see cref="SsbSurface.InternalCoupler"/> projects
    /// — deliberately, and it is the whole reason round 14 B could put the
    /// coupler row on TWO panes without extracting a component or inventing a
    /// singleton: one confirmed value, two placements, no way to
    /// disagree.</para></summary>
    public Confirmed<string> InternalCoupler => Radio.State.InternalCoupler;

    /// <summary>True only when the radio has CONFIRMED it is in HOP this
    /// session — the gate for HOP-domain controls.</summary>
    public bool IsHopConfirmed =>
        Radio.State.OperatingMode.IsConfirmed
        && Radio.State.OperatingMode.Value == OperatingMode.Hop;

    // ---- Intents ----------------------------------------------------------

    /// <summary>NET n — the radio regenerates the hopset, TUNES THE COUPLER
    /// (transmits), drops sync, and silently changes the SSB channel (trigger
    /// row (c) in Core handles the SSB-side re-poll). §7 TRIGGER.</summary>
    public void SelectNet(int net)
    {
        OpenGenerationWindow();
        Radio.Hop.SelectNet(net);
    }

    /// <summary>ROUND 14 C — the OPERATOR's route to <c>NET n</c>
    /// (plan/plan-round14.md §4-C, owner ruling R10). Asks the coupler
    /// convergence policy first, with the TARGET net's REPORTED type, then
    /// sends the select. The policy enqueues at most one <c>INTCOUPLER</c>
    /// word, so the wire reads <c>INTCOUPLER BYPASS</c> then <c>NET n</c> —
    /// ordered enqueue, no confirmation gating (§3.4).
    ///
    /// <para><b>Why a second wrapper instead of a flag on
    /// <see cref="SelectNet"/>.</b> The clone campaign selects nets too
    /// (<c>CloneService</c>), and constitution §3.3 says the clone paths never
    /// route through the policy. Two named methods make that structural: the
    /// raw one STAYS, and a caller is one or the other by which name it
    /// typed.</para>
    ///
    /// <para>It IS a §7 generation trigger — it sends the same <c>NET n</c> —
    /// so the closed manifest above gains it deliberately; the coupler word is
    /// not a trigger and never was (see the <c>INTCOUPLER</c> pins in
    /// HopSurfaceTests).</para>
    ///
    /// <para>With no policy in this composition (the clone/bench stacks) this
    /// is exactly <see cref="SelectNet"/>.</para></summary>
    public void SelectNetWithCouplerPolicy(int net, HopType? reportedType)
    {
        _coupler?.OnNetSelect(reportedType);
        SelectNet(net);
    }

    /// <summary>SY — sends a sync request (TRANSMITS). Callers must gate on
    /// a confirmed Hopnum &gt; 0: without a hopset SY is a silent no-op.</summary>
    public void SendSync() => Radio.Hop.Sync();

    /// <summary>TIME + DAT + DAY from the device clock, zero-padded — all
    /// three, because DAT does not recompute DAY (Stage 2 smoke).</summary>
    public void SetTimeOfDay(DateTime now) => Radio.Hop.SetTimeOfDay(now);

    /// <summary>
    /// The DIS-at-SSB TRAP (clone round 12 §4, captured 2026-08-18). At an
    /// <c>SSB&gt;</c> prompt, <c>DIS 0</c> is NOT refused — it answers a
    /// CHANNEL DUMP (<c>DI</c> is the minimum abbreviation of the channel
    /// command, and <c>DIS</c> matches it). The rows then parse perfectly, as
    /// CHANNELS, and a caller expecting HOP nets files channel data as net
    /// data with nothing anywhere saying so.
    ///
    /// <para>So the two DIS wrappers REFUSE off-prompt rather than send: a read
    /// that cannot be attributed is worse than a read that did not happen. The
    /// refusal is SILENT ON THE WIRE by design — nothing goes out, so there is
    /// nothing for the operator's Console to show, and consumers already have
    /// <see cref="IsHopConfirmed"/> to grey the control with a reason BEFORE
    /// the press. This is the belt, not the braces.</para>
    /// <para>Both wrappers return TRUE when they sent and FALSE when they
    /// refused, so a caller that must know (the clone campaign) can fault
    /// instead of waiting for an answer that is not coming.</para>
    /// </summary>
    private bool AtHopPrompt() => IsHopConfirmed;

    /// <summary>DIS n — ONE net's read-only detail (plan-ui-tweaks.md §M3:
    /// the pane lazy-loads per picked net instead of dumping all ten). Purely
    /// a read: unlike <see cref="SelectNet"/> it does not regenerate the
    /// hopset or tune the coupler.
    /// <para>GUARDED on a CONFIRMED <c>HOP&gt;</c> prompt — see
    /// <see cref="AtHopPrompt"/> for the trap this closes.</para></summary>
    public bool RequestNet(int net)
    {
        if (!AtHopPrompt()) return false;
        Radio.Hop.QueryNet(net);
        return true;
    }

    /// <summary>SH — current net / Hopnum / sync (the HOP SH block).</summary>
    public void RequestStatus() => Radio.Show();

    /// <summary>TI — radio clock (DAY/DATE/TIME triplet answer).</summary>
    public void RequestTime() => Radio.QueryTime();

    /// <summary>DIS — the WHOLE net table in one read (NETID/Hoptype/Center
    /// triplets for nets 0-9). The HOP-settings editor's lazy load and
    /// Refresh: ten rows want ten nets, and one command reads them all.
    /// Purely a read — it neither selects a net nor generates a hopset.
    /// <para>GUARDED on a CONFIRMED <c>HOP&gt;</c> prompt — bare <c>DIS</c> at
    /// <c>SSB&gt;</c> answers the CHANNEL table, see
    /// <see cref="AtHopPrompt"/>.</para></summary>
    public bool RequestAllNets()
    {
        if (!AtHopPrompt()) return false;
        Radio.Hop.QueryAllNets();
        return true;
    }

    // ---- Net PROGRAMMING intents (round-3 X1; scope guard amended in X2) ---
    // Writes. Each is an explicit operator send from the editor's row-level
    // Program button, in the order protocol.md requires (HOPTYPE must precede
    // HOPSET/HOPLIST), and the row re-reads with DIS n afterwards (X3) — what
    // the radio then reports is the only thing that renders.
    //
    // Arguments are validated CLIENT-SIDE by the caller before they get here
    // (the builders throw, and the radio silently ignores wrong frequency
    // formats — protocol.md).

    /// <summary>NETID n &lt;8-digit id&gt; — echoes "NETID    00  12345678".
    /// §7 TRIGGER.</summary>
    public void ProgramNetId(int net, string netId)
    {
        OpenGenerationWindow();
        Radio.Hop.SetNetId(net, netId);
    }

    /// <summary>HOPTYPE n NB|WB|LIST — must precede the hopset write.
    /// §7 TRIGGER.</summary>
    public void ProgramHopType(int net, HopType type)
    {
        OpenGenerationWindow();
        Radio.Hop.SetHopType(net, type);
    }

    /// <summary>HOPSET n &lt;center&gt; (NB). §7 TRIGGER.</summary>
    public void ProgramNarrowbandHopset(int net, string centerKHz)
    {
        OpenGenerationWindow();
        Radio.Hop.SetNarrowbandHopset(net, centerKHz);
    }

    /// <summary>HOPSET n &lt;low&gt; &lt;high&gt; (WB). §7 TRIGGER.</summary>
    public void ProgramWidebandHopset(int net, string lowKHz, string highKHz)
    {
        OpenGenerationWindow();
        Radio.Hop.SetWidebandHopset(net, lowKHz, highKHz);
    }

    /// <summary>HOPLIST n ADD &lt;f&gt; … (LIST). Fewer than three total is
    /// refused radio-side with "List_Invalid" — the radio reports it, so
    /// neither the builder nor the editor second-guesses the count.
    /// <para><b>APPEND, not replace</b> (protocol.md HOPLIST row): sending
    /// this twice on one net ACCUMULATES frequencies. Round 5 makes that
    /// harmless rather than hidden — <see cref="RequestHopList"/> reads the
    /// stored list back and <see cref="RemoveHopListFrequency"/> takes single
    /// entries off it, so an append is visible and reversible. PROVISIONAL
    /// until bench item A6b observes the append live.</para>
    /// <para>§7 TRIGGER.</para></summary>
    public void ProgramHopList(int net, params string[] frequenciesKHz)
    {
        OpenGenerationWindow();
        Radio.Hop.AddHopListFrequencies(net, frequenciesKHz);
    }

    // ---- Round-5 X6 additions (list read, list surgery, whole-net wipe) ----
    // Three more names left the GUI-out list (GuiOutScopeGuardTests X6) because
    // round 5 gives them a real operator surface: the LIST net editor renders
    // stored frequencies as removable rows, and "Clear net" surfaces the wipe.

    /// <summary>HOPLIST n — read ONE net's stored LIST frequencies
    /// ("HOPLIST 03   11010  11015  11020", session-16; mirrored into
    /// <see cref="HopLists"/>). Purely a read. It is not a "second source of
    /// truth" for the row: NO captured <c>DIS</c> answer carries a hoplist at
    /// all, so this is the ONLY way to learn a LIST net's frequencies.</summary>
    public void RequestHopList(int net) => Radio.Hop.QueryHopList(net);

    /// <summary>HOPLIST n DEL &lt;f&gt; — remove ONE stored frequency. The
    /// round-3 accumulation caution dissolves with this: a LIST net's rows are
    /// now individually removable, so an append is visible and reversible.
    /// Callers re-read <c>HOPLIST n</c> and <c>DIS n</c> afterwards.
    /// §7 TRIGGER.</summary>
    public void RemoveHopListFrequency(int net, string freqKHz)
    {
        OpenGenerationWindow();
        Radio.Hop.DeleteHopListFrequency(net, freqKHz);
    }

    /// <summary>HOPSET n DEL — <b>wipes the ENTIRE net record</b>, NETID
    /// included (probe R9b), not just the frequencies. Destructive: callers
    /// put it behind a confirm that opens EVERY time (no once-per-session
    /// accepted latch) and re-read <c>DIS n</c> after. §7 TRIGGER.</summary>
    public void ClearNet(int net)
    {
        OpenGenerationWindow();
        Radio.Hop.DeleteHopset(net);
    }

    // ---- Round-11 X9 additions (the WB exclusion-band section) --------------
    // Three more names left the GUI-out list (GuiOutScopeGuardTests X9) because
    // round 11 gives them a real operator surface: the HOP-settings pane's
    // "Exclusion bands" section, in the LIST-editor idiom. The whole-table wipe
    // (DeleteAllExcludeBands) did NOT move — a per-row Remove is what the
    // section offers instead.
    //
    // ALL WIRE SHAPES HERE ARE PROVISIONAL (plan round 11 §14) except the
    // captured single-band echo `Exclude 00  02000   03000 `.

    /// <summary>EXC + one closing sentinel — the WHOLE exclusion table in one
    /// sentinel-bracketed read. Purely a read.
    /// <para>The sentinel is load-bearing: an EMPTY table answers NOTHING AT
    /// ALL (captured 2026-08-17), byte-identical to a swallowed query, so only
    /// the answered sentinel separates read-empty from unread. Coalesces —
    /// a request arriving while one is on the wire sends nothing.</para>
    /// <para>Returns the operation's READ ID (round 11 §9A): a caller that
    /// must know whether THIS read committed matches the id against
    /// <see cref="LastExcludeRead"/>. Judging it by any other sentinel is
    /// judging a different question.</para></summary>
    public long RequestExcludeBands() => Radio.Hop.QueryExcludeBands();

    /// <summary>Completion record of the last committed exclusion-band read —
    /// the id equals the one <see cref="RequestExcludeBands"/> returned, and
    /// <c>Answered == false</c> means NOTHING was published and the prior
    /// mirror stands.</summary>
    public AleReadCompletion LastExcludeRead => Radio.State.Hop.LastExcludeRead;

    /// <summary>EXC &lt;band&gt; &lt;lowHz&gt; &lt;highHz&gt; — 8-DIGIT Hz in,
    /// kHz echo out. The radio REGENERATES the current hopset on this write
    /// (protocol.md), which the section's caption tells the operator rather
    /// than hiding. Callers convert from the pane's MHz vocabulary and re-read
    /// the table after.
    /// <para>Deliberately NOT a §7 generation trigger: the §7 enumeration is a
    /// closed manifest of the net-select and hopset-family writes, and a
    /// no-net-id answer to an exclusion write has never been observed.</para></summary>
    public void ProgramExcludeBand(int band, string lowHz, string highHz)
        => Radio.Hop.SetExcludeBand(band, lowHz, highHz);

    /// <summary>EXC &lt;band&gt; DEL — the section's per-row Remove. NOT silent
    /// when regeneration has anything to attempt (protocol.md: it answers
    /// <c>Wait...</c> / <c>No Hopset</c> with no current hopset). Callers
    /// re-read the table after.</summary>
    public void RemoveExcludeBand(int band) => Radio.Hop.DeleteExcludeBand(band);

    // ---- Round-14 B: the internal coupler (plan/plan-round14.md §4-B) -------
    // Owner ruling R2: Enable/Bypass appears on the HOP settings pane, copied
    // from the SSB settings row. The BUILDERS DO NOT MOVE — they stay on
    // Falcon.Core's SsbController (SsbController.cs:248 / :611), which is a
    // COMMAND-FAMILY home, not a mode gate: P-1 (2026-08-20, runs A/B/C,
    // docs/protocol.md "INTCOUPLER is FULLY GRADUATED") proved the family
    // PROMPT-FREE — `SSB>`, `HOP>` and `ALE>` all accept the query AND the set
    // with the identical `INTCoupler …` echo, and the state verifiably flips at
    // `HOP>`. So these two wrappers are the HOP-pane's route to the same
    // builders, not a second implementation of them.
    //
    // NEITHER is a §7 generation trigger: the coupler is not the net's hopset,
    // and no probe has ever seen a NO NET ID answer to an `INTCOUPLER` of
    // either form. That is a RECORDED DECISION, pinned in HopSurfaceTests
    // beside the exclusion wrappers' identical one.

    /// <summary>INTCOUPLER BYPASS|ENABLE — the internal antenna coupler. An
    /// operator gesture from the HOP settings pane's coupler row; the echo
    /// (<c>INTCoupler Bypassed</c> / <c>INTCoupler Enabled</c>) is the
    /// read-back that moves <see cref="InternalCoupler"/>.
    /// <para>Unguarded by prompt on purpose — the family is PROMPT-FREE
    /// (P-1 run C). The pane it lives on is HOP-gated anyway
    /// (ModeSettingsPage), so in practice this always goes out at
    /// <c>HOP&gt;</c>.</para></summary>
    public void SetInternalCoupler(BypassEnable state) => Radio.Ssb.SetInternalCoupler(state);

    /// <summary>INTCOUPLER — the bare query, answered <c>INTCoupler
    /// Enabled</c>. Purely a read; it rides the pane's landing-read tier
    /// beside <c>DIS n</c> and <c>EXC</c>.</summary>
    public void QueryInternalCoupler() => Radio.Ssb.QueryInternalCoupler();
}
