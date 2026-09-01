using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>
/// ROUND 14 C — the internal-coupler CONVERGENCE policy
/// (plan/plan-round14.md §4-C, owner rulings R3/R10/R11).
///
/// <para><b>Why it exists.</b> P-1 (2026-08-20) solved <c>WB_Invalid</c>:
/// a wide-band or list net will not generate while the INTERNAL antenna
/// coupler is enabled, and the restriction is remotely liftable —
/// <c>INTCOUPLER BYPASS</c> on the same tuple turned <c>WB_Invalid</c> into
/// <c>Hopnum 0101</c> (docs/protocol.md, the "SOLVED — <c>WB_Invalid</c>"
/// section). The radio does NOT bypass on its own (R6/R9), so the app does
/// it, or the operator meets a refusal with no visible cause.</para>
///
/// <para><b>CONVERGENCE, not latch-and-restore</b> (owner ruling R10). There
/// is no "we bypassed it, so we owe a restore" bit. There is a state the
/// operator wants when no net type demands otherwise —
/// <see cref="DesiredIdle"/> — and a state the CURRENT CONTEXT requires
/// (<see cref="Required"/>). Every trigger simply moves the radio from the
/// one to the other when they differ. The operator is never fought: an
/// explicit coupler press MOVES <see cref="DesiredIdle"/>
/// (<see cref="NotifyOperatorCouplerWrite"/>), so the policy's own target
/// follows the user instead of undoing them.</para>
///
/// <para><b>The R3 doctrine exception, and its bounds.</b> This is the ONE
/// place in the app licensed to emit an operator-UNGESTURED write, and the
/// licence is narrow (constitution §3.3): every write is console-visible
/// like any other send, the mirror confirms it normally, no state is
/// persisted, and nothing else inherits the licence. The writes are not
/// really ungestured either — each one RIDES an operator gesture (a Select
/// Net press, a mode press) and is enqueued immediately before that
/// gesture's own command. The seeding READ rides the connect-read tier.
/// <b>The clone flow never routes through here</b>: it calls the raw
/// <c>HopSurface.SelectNet</c> and the plain <c>ModeSurface.Select</c>,
/// which is why those two stayed.</para>
///
/// <para><b>No <c>RETUNE</c>, ever</b> (owner ruling R11). Re-enabling a
/// coupler may want a tune; <c>RETU</c> TRANSMITS, so the app does not send
/// it. If the radio wants a tune it runs its own lifecycle and the spine's
/// tune chip shows whatever lines arrive.</para>
///
/// <para><b>Ordering, not confirmation-gating</b> (constitution §3.4). The
/// transport is prompt-gated serial, so enqueue order IS wire order: the
/// coupler word goes out, then the gesture's command. Nothing here waits on
/// a round trip, opens a time window, or unconfirms a mirror.</para>
/// </summary>
public sealed class CouplerPolicy
{
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;

    private BypassEnable? _desiredIdle;
    private BypassEnable? _pendingWrite;

    /// <summary>The <c>RadioSession.ReadySession</c> this policy has already
    /// seeded — null before the first. Identity, not a flag: see
    /// <see cref="SeedReadIfReady"/>.</summary>
    private int? _seededSession;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 14). This policy is the reason the signal is its OWN dependency-free
    /// seam rather than the CloneService: the policy is required by the
    /// surfaces the CloneService requires, so a policy taking the CloneService
    /// would close a DI cycle (MauiProgram.cs). Null where there is no campaign
    /// to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    public CouplerPolicy(Prc138Radio radio, RadioSession session, ICampaignSignal? campaign = null)
    {
        _radio = radio;
        _session = session;
        _campaign = campaign;

        // THE LAZY-SEED RULE (§5.2): DI resolves singletons lazily, so this
        // policy can first be CONSTRUCTED in the middle of a campaign — by a
        // surface the campaign itself asked for. The seeding read below defers
        // like any other autonomous read, which leaves `_seededSession` unpaid,
        // and this subscription seeds once when the campaign lets go.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) SeedReadIfReady(); };

        // The RadioSurface.cs:20-28 idiom, used DIRECTLY rather than by
        // subclassing a surface: this is not a display slice, it wants one
        // property and no Changed event of its own. Events arrive already
        // marshalled (Prc138Radio Q10).
        radio.StateChanged += (_, e) =>
        {
            if (e.PropertyChanged == RadioProperty.InternalCoupler) OnCouplerMirrorMoved();
        };
        session.PhaseChanged += (_, _) => OnPhaseChanged();

        // Belt for RESOLUTION ORDER, not a second rule. DI singletons resolve
        // lazily, so a policy first constructed after the session reached
        // Ready would never see the PhaseChanged that seeds it. The rule is
        // unchanged — ONE seeding read per Ready session, keyed on
        // RadioSession.ReadySession — and this is simply the other moment at
        // which "the policy can observe Ready" becomes true.
        SeedReadIfReady();
    }

    // ---- State ---------------------------------------------------------------

    /// <summary>The coupler state the OPERATOR wants when no net type demands
    /// otherwise. Seeded from the FIRST confirmed mirror value of the session
    /// (the state the radio was FOUND in) and thereafter moved ONLY by an
    /// explicit operator press (<see cref="NotifyOperatorCouplerWrite"/>) —
    /// never by this policy's own writes, and never by a later mirror change,
    /// including the confirmation of a policy write. Cleared on any transition
    /// out of Ready; never persisted.
    /// <para>Internal because nothing binds to it: it is the policy's own
    /// baseline, and the operator sees the coupler's TRUE state on the two
    /// settings rows, which render the mirror.</para></summary>
    internal BypassEnable? DesiredIdle => _desiredIdle;

    /// <summary>The last coupler word enqueued this session — by this policy
    /// or reported by a settings row — that the mirror has not yet REACHED.
    /// Test hook; see <see cref="Effective"/> for why it exists, and
    /// <see cref="OnCouplerMirrorMoved"/> for why "reached" and not "answered
    /// by any movement".</summary>
    internal BypassEnable? PendingWrite => _pendingWrite;

    /// <summary>What the coupler will BE once the wire drains:
    /// <c>PendingWrite ?? confirmed-mirror</c>, and null when neither is
    /// known.
    ///
    /// <para><b>Why a decision cannot read the mirror alone.</b> Two gestures
    /// can be queued before the first echo lands. A WB select enqueues
    /// <c>BYPASS</c> + <c>NET w</c>; an immediate NB select reading only the
    /// still-standing <c>ENABLED</c> confirmation would decide it has nothing
    /// to do and land the NB net with the coupler bypassed. The transport
    /// serializes the WIRE; this serializes the DECISIONS.</para>
    ///
    /// <para><b>Accepted corner (R10 licence).</b> A write the radio REFUSES
    /// is never answered, so its <see cref="PendingWrite"/> is stranded until
    /// the mirror reaches that value from some other cause. Refusal of
    /// <c>INTCOUPLER</c> is bench-DISPROVEN at <c>SSB&gt;</c>, <c>HOP&gt;</c>
    /// and <c>ALE&gt;</c> (P-1 runs A/B/C — the family is prompt-free), so the
    /// path is theoretical, and the plan enumerates it as a non-goal rather
    /// than paying for a retry loop or a queue watchdog. (The plan's own
    /// wording — "the next confirmation from any source clears it" — is
    /// CORRECTED here by the round-1 audit: see
    /// <see cref="OnCouplerMirrorMoved"/>. Clearing on any confirmation is the
    /// blocker that fix repaired; the residue is that a stranded word is
    /// stickier than the plan imagined, which costs a redundant coupler word
    /// at worst and never a wrong one.)</para></summary>
    internal BypassEnable? Effective => _pendingWrite ?? MirrorState;

    /// <summary>The mirror, mapped to the SET vocabulary. The wire's report
    /// form is <c>BYPASSED</c>/<c>ENABLED</c> and the set form is
    /// <c>BYPASS</c>/<c>ENABLE</c> (docs/protocol.md, both captured); the
    /// parser has already uppercased the radio's mixed-case
    /// <c>INTCoupler Enabled</c>. Anything else the radio might answer maps to
    /// NULL — an unrecognised report is not a state this policy may act on
    /// (§3.1), and null here simply means the policy stays silent.</summary>
    private BypassEnable? MirrorState
    {
        get
        {
            var mirror = _radio.State.InternalCoupler;
            if (!mirror.IsConfirmed) return null;
            return mirror.Value switch
            {
                "BYPASSED" => BypassEnable.Bypass,
                "ENABLED" => BypassEnable.Enable,
                _ => null,
            };
        }
    }

    /// <summary>The CURRENT net's reported type, read straight off the Core
    /// hop mirror (<c>HopState</c>) — the mode-entry trigger's input. Null
    /// when the current net is unconfirmed, absent from the table, or present
    /// with no type reported: three different unknowns, one honest answer.
    /// Read-only; no Core change.</summary>
    private HopType? CurrentNetType
    {
        get
        {
            var current = _radio.State.Hop.CurrentNet;
            if (!current.IsConfirmed) return null;
            return _radio.State.Hop.Nets.TryGetValue(current.Value, out var net) ? net.Type : null;
        }
    }

    // ---- The required state per context --------------------------------------

    /// <summary>What the coupler must be for a context, verbatim from the R10
    /// table:
    /// <list type="table">
    /// <item><term>in HOP, net type WB or LIST</term><description><c>BYPASS</c> — the coupler is what refuses the generation.</description></item>
    /// <item><term>in HOP, net type NB</term><description><see cref="DesiredIdle"/> — nothing demands otherwise.</description></item>
    /// <item><term>in HOP, net type unreported</term><description>NO OPINION — the app never writes on a guess (§3.1).</description></item>
    /// <item><term>any other mode</term><description><see cref="DesiredIdle"/>.</description></item>
    /// </list>
    /// A null answer means "no opinion", which is also what an
    /// unestablished <see cref="DesiredIdle"/> produces — and that is the
    /// whole of the "no baseline yet" corner: no baseline, no convergence.
    /// </summary>
    internal BypassEnable? Required(OperatingMode mode, HopType? netType)
    {
        if (mode != OperatingMode.Hop) return _desiredIdle;
        return netType switch
        {
            HopType.Wideband or HopType.List => BypassEnable.Bypass,
            HopType.Narrowband => _desiredIdle,
            _ => null,
        };
    }

    // ---- The triggers --------------------------------------------------------

    /// <summary>TRIGGER 1 — a Select Net press, called from
    /// <c>HopSurface.SelectNetWithCouplerPolicy</c> with the TARGET net's
    /// reported type, immediately before <c>NET n</c> is enqueued.
    /// <para>The context is HOP by construction: <c>NET n</c> is a HOP-domain
    /// command and its only operator route (<c>HopViewModel.SelectPickedNet</c>)
    /// is gated on a CONFIRMED <c>HOP&gt;</c>.</para></summary>
    public void OnNetSelect(HopType? reportedType) => Converge(OperatingMode.Hop, reportedType);

    /// <summary>TRIGGER 2 — an operator mode press, called from
    /// <c>ModeSurface.SelectAsOperatorGesture</c> immediately before the mode
    /// command.
    /// <para><b>Entering HOP</b> converges on the CURRENT net's type, and it
    /// must happen BEFORE <c>HO</c> goes out: mode entry REGENERATES the
    /// current net's hopset (docs/protocol.md, both P-1 runs), so arriving in
    /// HOP with the coupler enabled on a WB net regenerates straight into
    /// <c>WB_Invalid</c>.</para>
    /// <para><b>Leaving HOP</b> — and any other mode-to-mode move — converges
    /// on <see cref="DesiredIdle"/>: outside HOP no net type has an
    /// opinion.</para></summary>
    public void OnModeSelect(OperatingMode target)
        => Converge(target, target == OperatingMode.Hop ? CurrentNetType : null);

    /// <summary>An operator set the coupler EXPLICITLY from either settings
    /// row. Reported rather than inferred from the mirror (the mirror also
    /// moves for this policy's own writes and for anything the radio does on
    /// its own, and those must NOT move the baseline).
    ///
    /// <para>This is R10's "if the user overrides it, don't set it back",
    /// and it needs no fighting logic: the operator's value BECOMES the
    /// baseline, so every later convergence outside a WB/LIST context targets
    /// what they chose.</para></summary>
    public void NotifyOperatorCouplerWrite(BypassEnable value)
    {
        _desiredIdle = value;
        RecordEnqueued(value);
    }

    // ---- The convergence decision --------------------------------------------

    private void Converge(OperatingMode mode, HopType? netType)
    {
        if (Required(mode, netType) is not { } required) return;   // no opinion
        if (Effective is not { } effective) return;                // nothing confirmed to act on
        if (effective == required) return;                         // already going there

        // ORDERED ENQUEUE and nothing else (§3.4): the caller enqueues its own
        // command next, and the prompt-gated transport keeps them in that
        // order on the wire. PendingWrite moves FIRST so a second gesture
        // queued behind this one decides from where the radio is headed.
        RecordEnqueued(required);
        _radio.Ssb.SetInternalCoupler(required);
    }

    /// <summary>Remember the word just put on the wire, so the NEXT decision
    /// sees where the radio is headed rather than where it last was.
    ///
    /// <para><b>The one exception</b> — a word enqueued while nothing of ours
    /// is outstanding, asking for the state the mirror ALREADY reports (the
    /// operator pressing the lit button; the row has no re-click guard by
    /// design). Its echo carries no value CHANGE, so no mirror movement will
    /// ever arrive to retire it, and a <see cref="PendingWrite"/> that can
    /// never retire would go on standing in for the mirror after some LATER,
    /// unrelated change — reporting a destination the radio is not heading to.
    /// Nothing is pending in that case, so nothing is recorded.</para>
    ///
    /// <para>The guard is deliberately narrow: it applies ONLY when
    /// <see cref="_pendingWrite"/> is already null. While a word IS
    /// outstanding the mirror is stale by definition, so "it already says
    /// that" proves nothing and the new word must be recorded.</para></summary>
    private void RecordEnqueued(BypassEnable value)
    {
        if (_pendingWrite is null && MirrorState == value) return;
        _pendingWrite = value;
    }

    // ---- Session lifecycle ---------------------------------------------------

    /// <summary>The mirror moved. Two consequences: the pending word retires
    /// IF the mirror has now REACHED it, and a still-null
    /// <see cref="DesiredIdle"/> takes its seed from the first value the
    /// session confirms.
    ///
    /// <para><b>Why "reached", not "any movement"</b> (round-1 audit, BLOCKER
    /// — this method retired the pending word on every movement, and that was
    /// wrong). Several words can be in flight at once, and each echo answers
    /// the OLDEST of them. From a confirmed <c>ENABLED</c>, a WB select then an
    /// NB select queue <c>BYPASS, NET w, ENABLE, NET n</c>; the <c>BYPASS</c>
    /// echo then arrives while <c>ENABLE</c> is still outstanding. Retiring on
    /// it left the mirror — now <c>BYPASSED</c> — speaking for a radio already
    /// on its way back to <c>ENABLED</c>, so a third gesture selecting a WB net
    /// found "already bypassed", sent nothing, and regenerated straight into
    /// <c>WB_Invalid</c>: precisely the defect this policy exists to prevent.
    /// Retiring only when the mirror reaches the value the LAST word asked for
    /// keeps <see cref="Effective"/> pointing at the destination for as long as
    /// the radio is still travelling to it.</para>
    ///
    /// <para><b>An UNCONFIRM</b> (the mirror losing its value) is now a
    /// movement that retires nothing: our word is still outstanding, and the
    /// destination remains the best knowledge there is.</para></summary>
    private void OnCouplerMirrorMoved()
    {
        if (_pendingWrite is { } pending && MirrorState == pending) _pendingWrite = null;
        if (_desiredIdle is null && MirrorState is { } found) _desiredIdle = found;
    }

    /// <summary>Session-scoped, per constitution §3.3. Anything out of Ready
    /// discards the baseline and the pending write: the next Ready session may
    /// be a different radio, found in a different state.
    /// <para>No restore is owed across a dead session (R10's enumerated
    /// non-goal): if the session dropped while the policy had the coupler
    /// bypassed, it STAYS bypassed on the radio, both settings rows show it,
    /// and one press fixes it.</para>
    /// <para>This branch handles the DIRECTLY OBSERVED drop — the notification
    /// arrives while the session is still down. The drop this handler cannot
    /// see is the one that has already been repaired by delivery time, and
    /// <see cref="SeedReadIfReady"/>'s session-identity check is what catches
    /// that one.</para></summary>
    private void OnPhaseChanged()
    {
        if (_session.Phase == SessionPhase.Ready) { SeedReadIfReady(); return; }
        DiscardSessionState();
    }

    /// <summary>Everything this policy knows that belonged to one Ready
    /// session. The seeded-session identity is deliberately NOT reset here: it
    /// names a session that is over, and no future session can share its
    /// number.</summary>
    private void DiscardSessionState()
    {
        _desiredIdle = null;
        _pendingWrite = null;
    }

    /// <summary>THE SEEDING READ — one bare <c>INTCOUPLER</c> per Ready
    /// session, at whatever prompt the radio happens to be at.
    ///
    /// <para><b>Why the policy owns a read at all.</b> The baseline must not
    /// depend on the operator having visited a settings pane: without a
    /// confirmed mirror the policy is silent, so a WB select would meet
    /// <c>WB_Invalid</c> exactly as before.</para>
    ///
    /// <para><b>Why no mode gate and no deferral machinery.</b> The
    /// <c>INTCOUPLER</c> family is PROMPT-FREE — P-1 run C
    /// (<c>bench/transcripts/r14-coupler-c-20260820-170918.jsonl</c>) added
    /// <c>ALE&gt;</c> to the <c>SSB&gt;</c>/<c>HOP&gt;</c> captures, all three
    /// answering the identical echo. So the read simply goes out.</para>
    ///
    /// <para>It is a READ on the established connect-read tier (the connect
    /// <c>SH</c> precedent), console-visible like every send. If it is never
    /// answered the policy stays silent — honest (§3.1). The settings panes'
    /// own landing reads remain and simply arrive first sometimes; the seed
    /// takes the first confirmation whatever produced it.</para>
    ///
    /// <para><b>ONE read per Ready SESSION, keyed on the session's identity
    /// rather than on a flag</b> (audit rounds 1 and 2, both BLOCKER/MAJOR
    /// here). <c>PhaseChanged</c> is marshalled and carries no payload, so a
    /// callback sees only the phase current when it finally runs, and there
    /// are TWO moments at which "the policy can observe Ready" becomes true —
    /// the constructor (DI resolves singletons lazily) and the notification.
    /// Two delivery orders defeat any flag-and-current-phase scheme:
    /// <list type="bullet">
    /// <item><description>a notification POSTED BEFORE this policy existed,
    /// delivered after its constructor already seeded — must NOT seed again
    /// (round 1);</description></item>
    /// <item><description>a drop AND a reconnect that both complete before the
    /// context drains — every queued callback observes Ready, so nothing looks
    /// like a drop at all, and the dead session's baseline and pending write
    /// would survive into the new one, suppressing the very
    /// <c>INTCOUPLER BYPASS</c> a WB select needs (round 2).</description></item>
    /// </list>
    /// <c>RadioSession.ReadySession</c> resolves both: it is read
    /// SYNCHRONOUSLY, so it always names the session that is Ready NOW,
    /// whatever order the notifications arrive in. Equal to
    /// <see cref="_seededSession"/> means "already seeded THIS session" —
    /// leftover notifications are no-ops. Different means a session boundary
    /// was crossed, seen or unseen, so the old session's state is discarded
    /// before the new seed goes out.</para></summary>
    private void SeedReadIfReady()
    {
        if (_session.Phase != SessionPhase.Ready) return;

        var session = _session.ReadySession;
        if (_seededSession == session) return;      // already seeded this one

        // D1 QUIESCE: a clone campaign owns the wire. The identity latch is
        // left UNPAID — nothing is discarded and no session is claimed — so the
        // seed stays owed and the campaign-end handler runs it once, still
        // against whichever Ready session is current then.
        if (_campaign?.CampaignActive == true) return;

        // A boundary was crossed. If OnPhaseChanged saw the drop this is a
        // no-op; if the drop and its repair coalesced, this is the only place
        // that ever notices.
        DiscardSessionState();
        _seededSession = session;
        _radio.Ssb.QueryInternalCoupler();
    }
}
