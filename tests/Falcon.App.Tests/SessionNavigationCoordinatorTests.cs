using Falcon.App.Core.Services;
using Falcon.App.Core.Session;

namespace Falcon.App.Tests;

/// <summary>Records the navigator calls, in order — the coordinator's whole
/// observable output. Deliberately dumb: nothing about it can pass or fail a
/// test by itself.</summary>
public sealed class RecordingNavigator : INavigator
{
    public List<string> Calls { get; } = [];

    public Task GoToOperate() { Calls.Add(nameof(GoToOperate)); return Task.CompletedTask; }
    public Task GoToConnectionSettings() { Calls.Add(nameof(GoToConnectionSettings)); return Task.CompletedTask; }
    public Task GoToAbout() { Calls.Add(nameof(GoToAbout)); return Task.CompletedTask; }
}

/// <summary>
/// CLONE ROUND 12 §6 F3 — the connection-first flow's EDGE TABLE, row by row.
///
/// <para><b>What this file can and cannot pin.</b> The host suite has no MAUI
/// head, so it cannot execute Shell routing. What it CAN pin — and what
/// actually carries the decisions — is which navigator method fires for which
/// phase edge. The other half (routes exist, the About push is relative, the
/// tab navigations are absolute) is a SOURCE GUARD in
/// ConnectionFlowSourceGuardTests, and the runtime behaviours (push/back, a
/// phase edge clearing a pushed About) are RECORDED MANUAL CHECKS on both
/// platforms — see docs/ui.md. That split is deliberate: a test that claimed
/// to prove Shell navigation from here would be claiming something this host
/// cannot know.</para>
/// </summary>
public class SessionNavigationCoordinatorTests : SessionTestBase
{
    private readonly RecordingNavigator _navigator = new();

    /// <summary>Constructed AND activated — the shell's own sequence. Every
    /// edge-table test below starts here, because an unactivated coordinator
    /// deliberately hears nothing.</summary>
    private SessionNavigationCoordinator Coordinator()
    {
        var coordinator = new SessionNavigationCoordinator(Session, _navigator);
        coordinator.Activate();
        return coordinator;
    }

    // ---- Activation ---------------------------------------------------------

    [Fact]
    public void ColdStart_NavigatesNowhere()
    {
        // The shell has just set the default tab. A navigator call here would
        // fight it — so construction BASELINES the phase and dispatches
        // nothing.
        var coordinator = Coordinator();

        Assert.Empty(_navigator.Calls);
        Assert.Equal(SessionPhase.Disconnected, coordinator.PreviousPhase);
    }

    [Fact]
    public void BeforeActivation_TheCoordinatorHearsNothing_AndBaselinesAtActivationTime()
    {
        // AUDIT ROUND 1: the shell resolves this through its CONSTRUCTOR, and
        // constructor arguments are built BEFORE the constructor body sets the
        // default tab. Subscribing at construction would therefore listen
        // during shell startup — consuming an edge against a Shell that does
        // not exist yet (the navigator no-ops on a null Shell.Current) while
        // the baseline advanced past it, losing the edge with no trace.
        var coordinator = new SessionNavigationCoordinator(Session, _navigator);
        Assert.False(coordinator.IsActive);

        // A whole session happens in the pre-activation window.
        ConnectReady();
        Session.Close();
        Assert.Empty(_navigator.Calls);

        // Activation baselines the phase it reads NOW…
        Session.Connect(TestSettings);
        coordinator.Activate();
        Assert.True(coordinator.IsActive);
        Assert.Equal(SessionPhase.Connecting, coordinator.PreviousPhase);
        Assert.Empty(_navigator.Calls);

        // …and the very next edge is acted on normally.
        AnswerSentinel();
        Assert.Equal([nameof(INavigator.GoToOperate)], _navigator.Calls);
    }

    [Fact]
    public void ActivationIsIdempotent()
    {
        // The shell calls it once, but a second call must not double-subscribe
        // (two navigations per edge) or re-baseline over an edge already seen.
        var coordinator = new SessionNavigationCoordinator(Session, _navigator);
        coordinator.Activate();
        coordinator.Activate();

        ConnectReady();

        Assert.Equal([nameof(INavigator.GoToOperate)], _navigator.Calls);
    }

    [Fact]
    public void AttachingMidSession_BaselinesTheCurrentPhase_WithoutNavigating()
    {
        // The activation path is "eagerly resolved in AppShell's constructor",
        // and a shell can in principle be built with a session already up
        // (activity recreation on Android keeps the process and the session).
        ConnectReady();

        var coordinator = Coordinator();

        Assert.Empty(_navigator.Calls);
        Assert.Equal(SessionPhase.Ready, coordinator.PreviousPhase);
    }

    // ---- The edge table, row by row -----------------------------------------

    [Fact]
    public void ConnectingToReady_GoesToOperate()
    {
        Coordinator();

        Session.Connect(TestSettings);
        Assert.Equal(SessionPhase.Connecting, Session.Phase);
        Assert.Empty(_navigator.Calls);                  // Connecting itself: nothing

        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Equal([nameof(INavigator.GoToOperate)], _navigator.Calls);
    }

    [Fact]
    public void ReconnectingToReady_AlsoGoesToOperate()
    {
        Coordinator();
        Session.AutoReconnectEnabled = true;
        ConnectReady();
        _navigator.Calls.Clear();

        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);

        // The link wins its own fight: the poller re-runs the connect ritual.
        Session.ReconnectTick();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);

        Assert.Equal([nameof(INavigator.GoToOperate)], _navigator.Calls);
    }

    [Fact]
    public void ReadyToReconnecting_DoesNothing()
    {
        // The link is fighting for itself and usually wins. Yanking the
        // operator off the screen they are working on helps nobody — and if
        // the fight is lost, the Failed edge moves them.
        Coordinator();
        Session.AutoReconnectEnabled = true;
        ConnectReady();
        _navigator.Calls.Clear();

        Transport.InjectError(new IOException("drop"));

        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.Empty(_navigator.Calls);
    }

    [Fact]
    public void ConnectingToFailed_GoesToConnectionSettings()
    {
        // The failed FIRST attempt: the operator is already on the settings
        // page, and landing there again is a no-op they never see.
        Coordinator();
        Transport.ThrowOnOpen = new IOException("no such port");

        Session.Connect(TestSettings);

        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.Equal([nameof(INavigator.GoToConnectionSettings)], _navigator.Calls);
    }

    [Fact]
    public void ReadyToFailed_GoesToConnectionSettings()
    {
        // The cable-pull path with auto-reconnect OFF (the shipped default):
        // the transport's own presence detector faults the session and the
        // operator lands on the page that can fix it.
        Coordinator();
        ConnectReady();
        _navigator.Calls.Clear();

        Transport.InjectError(new IOException("cable pulled"));

        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.Equal([nameof(INavigator.GoToConnectionSettings)], _navigator.Calls);
    }

    [Fact]
    public void ReadyToDisconnected_GoesToConnectionSettings_Symmetrically()
    {
        // DECIDED (§6 F3): a deliberate Close is treated the same as a
        // failure. Disconnecting is how an operator says "I am done with the
        // radio", and connection is what they do next.
        Coordinator();
        ConnectReady();
        _navigator.Calls.Clear();

        Session.Close();

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal([nameof(INavigator.GoToConnectionSettings)], _navigator.Calls);
    }

    [Fact]
    public void ReconnectingToDisconnected_GoesToConnectionSettings()
    {
        // The reconnect fight ENDS. THIS is the edge that moves the operator,
        // which is what makes the Ready→Reconnecting no-op above affordable.
        Coordinator();
        Session.AutoReconnectEnabled = true;
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        _navigator.Calls.Clear();

        Session.Close();     // user gives up mid-reconnect → Disconnected
        Assert.Equal([nameof(INavigator.GoToConnectionSettings)], _navigator.Calls);
    }

    [Fact]
    public void RepeatedArrivalsAtTheSamePhase_AreNoOps()
    {
        // PhaseChanged can fire without the phase moving; the coordinator acts
        // on EDGES only, so a repeat must not re-navigate.
        Coordinator();
        ConnectReady();
        _navigator.Calls.Clear();

        Session.Close();
        Assert.Single(_navigator.Calls);

        Session.Close();
        Session.Close();
        Assert.Single(_navigator.Calls);
    }

    [Fact]
    public void AStablePhase_IsNeverTouched()
    {
        // The operator's own tab changes happen between edges. The coordinator
        // has no timer, no polling and no opinion about where they are — the
        // only proof of that a unit test can offer is that a session sitting
        // still produces nothing at all.
        Coordinator();
        ConnectReady();
        _navigator.Calls.Clear();

        // Time passes; the radio talks; nothing about the PHASE moves.
        Transport.InjectLine("Battery Status FULL 31.4V");
        Transport.InjectLine("Battery Status FULL 31.4V");

        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Empty(_navigator.Calls);
    }

    [Fact]
    public void ADisposedCoordinator_StopsNavigating()
    {
        var coordinator = Coordinator();
        ConnectReady();
        Assert.NotEmpty(_navigator.Calls);

        coordinator.Dispose();
        _navigator.Calls.Clear();

        Session.Close();
        Assert.Empty(_navigator.Calls);
    }

    [Fact]
    public void TheCoordinator_NeverPushesAbout()
    {
        // About is a title-bar gesture, never a consequence of the link. A
        // phase edge that pushed a page would leave the operator somewhere
        // they cannot explain.
        Coordinator();
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Session.Connect(TestSettings);
        AnswerSentinel();
        Session.Close();

        Assert.NotEmpty(_navigator.Calls);
        Assert.DoesNotContain(nameof(INavigator.GoToAbout), _navigator.Calls);
    }
}
