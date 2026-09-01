using Falcon.App.Core.Session;

namespace Falcon.App.Tests;

/// <summary>
/// Stage 7: the Android foreground-service start/stop rules (plan §2.5),
/// pinned headless. MainActivity only executes what this policy returns, so
/// these tests ARE the lifecycle contract: start once on Ready, keep running
/// through Reconnecting (the wake lock must cover the reconnect poller),
/// stop on Disconnected/Failed, and never start from the background
/// (Android 12+/14 restriction — defer to the next foreground instead).
/// </summary>
public class ForegroundLinkPolicyTests
{
    private static ForegroundLinkPolicy Foregrounded()
    {
        var policy = new ForegroundLinkPolicy();
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(true));
        return policy;
    }

    [Fact]
    public void Ready_WhileForegrounded_Starts()
    {
        var policy = Foregrounded();
        Assert.Equal(LinkServiceAction.Start, policy.OnPhaseChanged(SessionPhase.Ready));
        Assert.True(policy.IsServiceRunning);
    }

    [Fact]
    public void Ready_Twice_StartsOnce()
    {
        var policy = Foregrounded();
        Assert.Equal(LinkServiceAction.Start, policy.OnPhaseChanged(SessionPhase.Ready));
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(SessionPhase.Ready));
    }

    [Theory]
    [InlineData(SessionPhase.Disconnected)]
    [InlineData(SessionPhase.Failed)]
    public void LinkDown_AfterStart_Stops(SessionPhase down)
    {
        var policy = Foregrounded();
        policy.OnPhaseChanged(SessionPhase.Ready);
        Assert.Equal(LinkServiceAction.Stop, policy.OnPhaseChanged(down));
        Assert.False(policy.IsServiceRunning);
    }

    [Theory]
    [InlineData(SessionPhase.Disconnected)]
    [InlineData(SessionPhase.Failed)]
    public void LinkDown_WhenNotRunning_NoAction(SessionPhase down)
    {
        // Initial connect failure / user close before Ready: nothing to stop.
        var policy = Foregrounded();
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(down));
    }

    [Fact]
    public void Reconnecting_KeepsServiceRunning_AndReReadyIsNoStart()
    {
        // The unexpected-disconnect flow: the service (and wake lock) must
        // survive Reconnecting so the poller runs screen-off, and the
        // re-Ready needs no service start (already running) — that is what
        // keeps the Android 14 background-start restriction out of the
        // auto-reconnect path.
        var policy = Foregrounded();
        policy.OnPhaseChanged(SessionPhase.Ready);
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(SessionPhase.Reconnecting));
        Assert.True(policy.IsServiceRunning);
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(SessionPhase.Ready));
    }

    [Fact]
    public void Connecting_NoAction()
    {
        var policy = Foregrounded();
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(SessionPhase.Connecting));
    }

    [Fact]
    public void Ready_WhileBackgrounded_Defers_ThenStartsOnForeground()
    {
        var policy = new ForegroundLinkPolicy(); // never foregrounded
        Assert.Equal(LinkServiceAction.None, policy.OnPhaseChanged(SessionPhase.Ready));
        Assert.False(policy.IsServiceRunning);
        Assert.Equal(LinkServiceAction.Start, policy.OnActivityForegroundChanged(true));
        Assert.True(policy.IsServiceRunning);
    }

    [Fact]
    public void DeferredStart_DroppedWhenSessionLeavesReady()
    {
        var policy = new ForegroundLinkPolicy();
        policy.OnPhaseChanged(SessionPhase.Ready);            // deferred
        policy.OnPhaseChanged(SessionPhase.Disconnected);     // link gone
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(true));
        Assert.False(policy.IsServiceRunning);
    }

    [Fact]
    public void DeferredStart_NotConsumedWhileReconnecting()
    {
        // Stage 7 audit F2: Reconnecting does NOT clear the pending flag
        // (it stays retained-but-inert) — the consume-time guard (last
        // phase must still be Ready) is what must refuse the start when
        // the activity foregrounds mid-reconnect. Without it, this path
        // would start an FGS for a link that is currently down.
        var policy = new ForegroundLinkPolicy();
        policy.OnPhaseChanged(SessionPhase.Ready);            // deferred (backgrounded)
        policy.OnPhaseChanged(SessionPhase.Reconnecting);     // link dropped, poller armed
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(true));
        Assert.False(policy.IsServiceRunning);
    }

    [Fact]
    public void Foreground_WithoutPendingStart_NoAction()
    {
        var policy = Foregrounded();
        policy.OnPhaseChanged(SessionPhase.Ready);
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(false));
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(true));
    }

    [Fact]
    public void BackgroundTransition_NeverStartsOrStops()
    {
        var policy = Foregrounded();
        policy.OnPhaseChanged(SessionPhase.Ready);
        // Pausing the activity must not touch the service — the whole point
        // is that the link survives backgrounding.
        Assert.Equal(LinkServiceAction.None, policy.OnActivityForegroundChanged(false));
        Assert.True(policy.IsServiceRunning);
    }
}
