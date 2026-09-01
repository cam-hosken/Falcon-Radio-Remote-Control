namespace Falcon.App.Core.Services;

/// <summary>
/// THE CAMPAIGN SIGNAL (plan-clone-write-structural.md §5.2, decision D1) —
/// what a producer asks before it puts an autonomous read on the wire.
///
/// <para><b>Why an interface and not the CloneService.</b> The producers that
/// must fall silent during a clone campaign are ViewModels and the coupler
/// policy, and the policy is required by the surfaces the CloneService itself
/// requires (MauiProgram.cs:86-97). A producer taking the CloneService would
/// close that ring into a DI cycle. So the signal is its own dependency-free
/// seam: the CloneService raises it, everyone else only reads it (invariant
/// I-6 — producers depend on <see cref="ICampaignSignal"/>, never on
/// <c>CloneService</c>).</para>
///
/// <para><b>What it does NOT say.</b> Nothing about which campaign, which leg,
/// or how far along it is. A producer's only question is "may I read now?",
/// and the only answer it needs is yes or no.</para>
/// </summary>
public interface ICampaignSignal
{
    /// <summary>True while a clone campaign owns the wire. Every autonomous
    /// (non-clone-card) READ defers while this is true — mirror-event paths,
    /// <c>PhaseChanged</c> landings, view Loaded/OnAppearing hooks, tab opens
    /// AND explicit Refresh presses (plan §4, SUPPRESSION SCOPE: the press is
    /// accepted, the read runs at campaign end, and no button greys out).
    /// Operator WRITES are out of scope and keep their behaviour.</summary>
    bool CampaignActive { get; }

    /// <summary>Raised on every transition of <see cref="CampaignActive"/> —
    /// the campaign END edge is what a producer subscribes for, to run the one
    /// read it deferred. Raised AFTER the property has moved, so a handler
    /// reading <see cref="CampaignActive"/> sees the new value.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// The clone campaign's wire lease (plan-clone-write-structural.md §5.2).
///
/// <para><b>Why a lease and not the CloneService's own state</b> (critic pass
/// 1 blocker 2). <c>CloneService.State</c> reaches <c>Failed</c> BEFORE the
/// closing restore runs (CloneService.cs:1054-1092 / 2137-2140), so a producer
/// keyed on "is the state Reading or Writing" would wake up during the restore
/// lap and put traffic on the wire while the campaign is still moving the
/// radio. And an exception or an early return would leave a plain flag stuck
/// true forever. A scoped <see cref="IDisposable"/> taken in a
/// <c>using</c> at the top of the campaign body cannot do either: the lease
/// is released by the language, on every exit path, and the campaign's own
/// closing restore sits INSIDE it.</para>
///
/// <para><b>Re-entrancy.</b> A counter, not a flag: the write campaign's
/// nested verify is itself a read campaign, and it must not release the
/// outer lease when it finishes. Active while the count is &gt; 0; the
/// <see cref="Changed"/> edges fire only at 0→1 and 1→0, so a nested
/// <c>Enter()</c> is silent and producers see exactly one start and one end
/// per campaign.</para>
///
/// <para><b>Dependency-free by construction.</b> It takes nothing and knows
/// nothing. That is what breaks the DI cycle described on
/// <see cref="ICampaignSignal"/>, and it is why this is registered FIRST in
/// the composition root.</para>
/// </summary>
public sealed class CampaignWireCoordinator : ICampaignSignal
{
    private readonly object _gate = new();
    private int _depth;

    /// <inheritdoc/>
    public bool CampaignActive
    {
        get { lock (_gate) return _depth > 0; }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <summary>Take the wire for a campaign. Dispose the returned lease to
    /// release it; the OUTERMOST lease is the one that ends the campaign.
    /// A lease disposed twice releases once — <c>using</c> plus an explicit
    /// dispose must not decrement the count below the truth.</summary>
    public IDisposable Enter()
    {
        bool started;
        lock (_gate)
        {
            _depth++;
            started = _depth == 1;
        }
        // OUTSIDE the lock: a handler is free to read CampaignActive, and a
        // handler that took the lock again would deadlock on a non-reentrant
        // monitor path some future caller introduces.
        if (started) Changed?.Invoke(this, EventArgs.Empty);
        return new Lease(this);
    }

    private void Exit()
    {
        bool ended;
        lock (_gate)
        {
            if (_depth == 0) return;
            _depth--;
            ended = _depth == 0;
        }
        if (ended) Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Lease(CampaignWireCoordinator owner) : IDisposable
    {
        private CampaignWireCoordinator? _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.Exit();
        }
    }
}
