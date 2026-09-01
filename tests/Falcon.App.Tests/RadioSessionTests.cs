using Falcon.App.Core.Session;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// Session-layer rules (plan §2.2 + the ITransport contract line):
/// TransportError is connection-fatal; auto-reconnect is armed ONLY on an
/// unexpected disconnect of an established session, disarmed on user Close;
/// the poller is single-flight and each attempt runs the full connect ritual.
/// GUI rejigger G1 (owner ruling): AutoReconnectEnabled defaults OFF — the
/// machinery stays and is exercised here by enabling it explicitly.
/// </summary>
public class RadioSessionTests : SessionTestBase
{
    [Fact]
    public void Connect_RunsRitual_AndReachesReady()
    {
        Session.Connect(TestSettings);
        Assert.Equal(SessionPhase.Connecting, Session.Phase);
        // The full ritual went out: two bare CRs, echo off twice, sentinel.
        Assert.Equal(2, Transport.CountSent(""));
        Assert.Equal(2, Transport.CountSent("PORT_R ECHO OFF"));
        Assert.True(Transport.CountSent("BAT ST") >= 1);

        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.False(Session.IsReconnectArmed);
    }

    [Fact]
    public void Connect_PortMissing_Failed_NotArmed()
    {
        string? error = null;
        Session.SessionError += (_, e) => error = e.Message;
        Transport.ThrowOnOpen = new IOException("port does not exist");

        Session.Connect(TestSettings);

        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.False(Session.IsReconnectArmed);
        Assert.Contains("COM7", error);
    }

    [Fact]
    public void TransportError_WhileReady_IsFatal_TearsDown_AndArms()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();

        Transport.InjectError(new IOException("write failed"));

        // CONNECTION-FATAL: the port is closed, not left half-dead.
        Assert.False(Transport.IsOpen);
        Assert.Equal(ConnectionState.Disconnected, Radio.Connection);
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.True(Session.IsReconnectArmed);
    }

    [Fact]
    public void TransportError_WhileReady_AutoReconnectOffByDefault_Failed_NotArmed()
    {
        // G1 pin: the session-level default is OFF — no UI toggle exists.
        Assert.False(Session.AutoReconnectEnabled);
        ConnectReady();

        Transport.InjectError(new IOException("USB yanked"));

        Assert.False(Transport.IsOpen);
        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.False(Session.IsReconnectArmed);
    }

    [Fact]
    public void TransportError_DuringInitialConnecting_Failed_NotArmed()
    {
        Session.Connect(TestSettings);   // no sentinel answer yet

        Transport.InjectError(new IOException("adapter vanished mid-init"));

        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.False(Session.IsReconnectArmed);
    }

    [Fact]
    public void UserClose_Disarms_AndLaterErrorsIgnored()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Assert.True(Session.IsReconnectArmed);

        Session.Close();

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.False(Session.IsReconnectArmed);

        // A straggler error after user Close must not resurrect anything.
        Transport.InjectError(new IOException("straggler"));
        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.False(Session.IsReconnectArmed);

        // And ticks are inert.
        int opens = Transport.OpenCount;
        Session.ReconnectTick();
        Assert.Equal(opens, Transport.OpenCount);
    }

    [Fact]
    public void UserClose_WhileReady_NoReconnect()
    {
        ConnectReady();
        Session.Close();

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.False(Session.IsReconnectArmed);
        Assert.False(Transport.IsOpen);
    }

    [Fact]
    public void ReconnectTick_SingleFlight_AndRunsFullRitual()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Assert.True(Session.IsReconnectArmed);
        Transport.ClearSent();

        Session.ReconnectTick();
        Assert.Equal(2, Transport.OpenCount);
        // The attempt runs the FULL connect ritual (plan §2.2).
        Assert.Equal(2, Transport.CountSent(""));
        Assert.Equal(2, Transport.CountSent("PORT_R ECHO OFF"));
        Assert.True(Transport.CountSent("BAT ST") >= 1);

        // Second tick while the attempt is unresolved: single-flight no-op.
        Session.ReconnectTick();
        Assert.Equal(2, Transport.OpenCount);

        // Attempt succeeds → Ready, poller disarmed.
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.False(Session.IsReconnectArmed);

        // Ticks after success are inert.
        Session.ReconnectTick();
        Assert.Equal(2, Transport.OpenCount);
    }

    [Fact]
    public void ReconnectTick_PortStillAbsent_KeepsPolling()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Transport.ThrowOnOpen = new IOException("port still absent");

        Session.ReconnectTick();
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.True(Session.IsReconnectArmed);

        // Port comes back: next tick reconnects.
        Transport.ThrowOnOpen = null;
        Session.ReconnectTick();
        Assert.Equal(2, Transport.OpenCount);
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        AnswerSentinel();
    }

    [Fact]
    public void ReconnectAttempt_WatchdogFailed_StaysArmed_NextTickRetries()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Assert.True(Session.IsReconnectArmed);

        // Make the radio's init watchdog fast for the failed attempt.
        Radio.InitializationTimeoutMs = 60;
        Session.ReconnectTick();
        Assert.Equal(2, Transport.OpenCount);

        // No lines arrive → watchdog declares Failed (real timer).
        Assert.True(WaitUntil(() => Radio.Connection == ConnectionState.Failed),
            "init watchdog never fired");
        Assert.True(WaitUntil(() => Session.Phase == SessionPhase.Reconnecting));
        Assert.True(Session.IsReconnectArmed);

        // Next tick tries again.
        Session.ReconnectTick();
        Assert.Equal(3, Transport.OpenCount);
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        AnswerSentinel();
    }

    [Fact]
    public void InitialConnect_WatchdogFailed_NotArmed()
    {
        Radio.InitializationTimeoutMs = 60;
        Session.Connect(TestSettings);   // radio alive check: zero lines → no retry

        Assert.True(WaitUntil(() => Session.Phase == SessionPhase.Failed),
            "session never reached Failed");
        Assert.False(Session.IsReconnectArmed);
        Assert.False(Transport.IsOpen);
    }

    [Fact]
    public void TransportError_DuringReconnectAttempt_KeepsPolling()
    {
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Session.ReconnectTick();            // attempt in flight (port open)

        Transport.InjectError(new IOException("died again mid-attempt"));

        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.True(Session.IsReconnectArmed);

        // Single-flight slot was released: the next tick attempts again.
        Session.ReconnectTick();
        Assert.Equal(3, Transport.OpenCount);
    }
}
