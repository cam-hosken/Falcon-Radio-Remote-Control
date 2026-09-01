using Falcon.App.Core.Session;

namespace Falcon.App.Core.Services;

/// <summary>
/// Clone round 12 §6 F3 — the connection-first flow's one decision-maker:
/// which session PHASE EDGE moves the operator where.
///
/// <para><b>Activation is an explicit CALL, not construction</b> (audit
/// round 1). A singleton nothing else references would never run, so the
/// shell pulls it in — but a constructor dependency is built BEFORE the
/// constructor body runs, so subscribing in the constructor would have this
/// listening while the shell was still deciding which tab to open on: an edge
/// arriving in that window would be consumed against a Shell that does not
/// exist yet (the navigator no-ops on a null <c>Shell.Current</c>) while the
/// baseline advanced past it, losing the edge silently. So subscription lives
/// in <see cref="Activate"/>, which the shell calls AFTER the default tab is
/// assigned. Until then this object is inert: edges before activation are not
/// consumed, and the baseline is the phase read AT activation.</para>
///
/// <para>Activation takes its previous-phase baseline from the CURRENT phase
/// and dispatches NOTHING: a cold start already lands on the default tab, and
/// a navigator call during startup would fight it.</para>
///
/// <para><b>The edge table</b> (the whole contract — every row pinned):</para>
/// <list type="table">
///   <item><term>Connecting → Ready</term><description>Operate. The link came
///     up; the operator wants the radio.</description></item>
///   <item><term>Reconnecting → Ready</term><description>Operate. Same
///     arrival, different history.</description></item>
///   <item><term>Ready → Reconnecting</term><description>NOTHING. The link is
///     fighting for itself and usually wins; yanking the operator off the
///     screen they are working on helps nobody.</description></item>
///   <item><term>any → Failed</term><description>Connection settings — the
///     page that can do something about it.</description></item>
///   <item><term>any → Disconnected</term><description>Connection settings,
///     SYMMETRICALLY: a deliberate Close returns to where connecting
///     happens (decided).</description></item>
///   <item><term>anything else</term><description>NOTHING — including
///     Connecting itself, and including a repeated arrival at a phase we are
///     already in.</description></item>
/// </list>
///
/// <para><b>What it deliberately does not do.</b> It acts on EDGES only, so a
/// tab the operator picked during a stable phase is never touched. DEMO
/// sessions behave identically — the phases are the same. Nothing here
/// watches the cable: a pull becomes Failed through the transport's own
/// presence poller and arrives as an ordinary edge.</para>
/// </summary>
public sealed class SessionNavigationCoordinator : IDisposable
{
    private readonly RadioSession _session;
    private readonly INavigator _navigator;
    private SessionPhase _previous;
    private bool _active;
    private bool _disposed;

    public SessionNavigationCoordinator(RadioSession session, INavigator navigator)
    {
        _session = session;
        _navigator = navigator;
        // Construction subscribes to NOTHING (see the Activation remarks).
    }

    /// <summary>Start listening. Called by the shell once the default tab is
    /// set; idempotent, so a second call cannot double-subscribe or re-baseline
    /// over an edge already seen.</summary>
    public void Activate()
    {
        if (_active || _disposed) return;
        _active = true;
        // BASELINE, not a decision: whatever phase we start in is where the
        // operator already is.
        _previous = _session.Phase;
        _session.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>True once <see cref="Activate"/> has run. Test/diagnostic
    /// hook.</summary>
    public bool IsActive => _active;

    /// <summary>The phase this coordinator believes it has already acted on.
    /// Test/diagnostic hook.</summary>
    public SessionPhase PreviousPhase => _previous;

    private void OnPhaseChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;

        var previous = _previous;
        var current = _session.Phase;
        // Idempotence: a repeated arrival is not an edge. (A failed connect
        // attempt made ON the settings page is Connecting→Failed, which is an
        // edge — it just navigates to the page already showing.)
        if (current == previous) return;
        _previous = current;

        switch (current)
        {
            case SessionPhase.Ready
                when previous is SessionPhase.Connecting or SessionPhase.Reconnecting:
                _ = _navigator.GoToOperate();
                break;

            case SessionPhase.Failed:
            case SessionPhase.Disconnected:
                _ = _navigator.GoToConnectionSettings();
                break;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _active = false;
        _session.PhaseChanged -= OnPhaseChanged;
    }
}
