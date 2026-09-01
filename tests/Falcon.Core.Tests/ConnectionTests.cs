using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// The connect ritual (Q2/Q8, re-validated at 9600 by probe R1): flush CRs,
/// echo off twice, init queries, BAT ST sentinel barrier; the baud-scaled
/// init watchdog; async-event immunity.
/// </summary>
public class ConnectionTests : RadioTestBase
{
    [Fact]
    public void ConnectRitual_SendsTheDocumentedSequence()
    {
        // THIRD init query since 2026-08-23 (plan-ale-broadcast-round.md F1):
        // bare `POW`. The ALE SH block carries no POWER line, so connecting in
        // ALE used to leave the power mirror unreported all session; `POW`
        // answers `POWER low` at `ALE>` (probe P20).
        Connect();
        Assert.Equal(
            ["", "", "PORT_R ECHO OFF", "PORT_R ECHO OFF", "SH", "PORT_R", "POW", "BAT ST"],
            Transport.SentLines);
    }

    [Fact]
    public void OnlyOneSentinelOutstanding_SecondDispatchedAfterTheFirstAnswers()
    {
        Connect();
        Assert.Equal(1, Transport.CountSent("BAT ST"));
        AnswerSentinel();
        Assert.Equal(2, Transport.CountSent("BAT ST"));
    }

    [Fact]
    public void InitCompletesOnTheFirstSentinelAnswer_NotBefore()
    {
        Connect();
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
        Assert.False(Radio.IsInitialized);

        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.True(Radio.IsInitialized);
    }

    [Fact]
    public void AsyncEvents_DoNotCompleteOrBreakInit()
    {
        Connect();

        // Every async event class observed on the bench, mid-init:
        Transport.InjectLine("POWER hi ");
        Transport.InjectLine("POWER CUTBACK   ");
        Transport.InjectLine("KEY MIC ");
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Transport.InjectLine("TUNE FAULT");
        Transport.InjectLine("Sending_Sync_Req");
        Transport.InjectLine("SCANNING");

        Assert.Equal(ConnectionState.Initializing, Radio.Connection);

        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
    }

    [Fact]
    public void SwallowedFirstSentinel_SecondCompletesInit()
    {
        // The radio swallows commands connecting outside SSB (bench). The
        // first sentinel times out (half the watchdog window); the second is
        // then dispatched and its answer completes init.
        Radio.InitializationTimeoutMs = 500;
        Connect();
        Assert.Equal(1, Transport.CountSent("BAT ST"));

        Thread.Sleep(350);      // > half (250 ms): first ping timed out
        Assert.Equal(2, Transport.CountSent("BAT ST"));
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);

        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
    }

    [Fact]
    public void DeadRadio_WatchdogFailsAndClosesThePort()
    {
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);
        Radio.InitializationTimeoutMs = 100;

        Connect();
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);

        Thread.Sleep(500);      // no lines at all → no retry → Failed

        Assert.Equal(ConnectionState.Failed, Radio.Connection);
        Assert.False(Radio.IsConnectionOpen);
        Assert.Contains(errors, m => m.Contains("not responding"));
    }

    [Fact]
    public void AliveButSwallowing_WatchdogRetriesInitOnce()
    {
        Radio.InitializationTimeoutMs = 500;
        Connect();
        Transport.InjectLine("SSB> ");       // radio is alive: it sent us a line

        Thread.Sleep(650);                    // first watchdog window elapses;
                                              // the retry re-arms a second one

        // Retry re-ran the idempotent init queries — ALL of them, which is
        // what "the retry path reuses IssueInitQueries by construction" means
        // (plan-ale-broadcast-round.md F1: the power read must not be a
        // connect-only query):
        Assert.True(Transport.CountSent("SH") >= 2, "expected a second SH from the init retry");
        Assert.True(Transport.CountSent("POW") >= 2, "expected a second POW from the init retry");
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);

        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
    }

    [Fact]
    public void WatchdogDisarmed_WhenTheSentinelArrivesInTime()
    {
        Radio.InitializationTimeoutMs = 200;
        Connect();
        AnswerSentinel();

        Thread.Sleep(500);      // watchdog window elapses after Ready

        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.True(Radio.IsConnectionOpen);
    }

    [Fact]
    public void WatchdogScalesWithBaud_UnlessOverridden()
    {
        Radio.Connect(new PortSettings { PortName = "TEST", BaudRate = 2400 });
        Assert.Equal(40_000, Radio.EffectiveInitializationTimeoutMs);
        Radio.Disconnect();

        Radio.InitializationTimeoutMs = 1234;
        Radio.Connect(new PortSettings { PortName = "TEST", BaudRate = 2400 });
        Assert.Equal(1234, Radio.EffectiveInitializationTimeoutMs);
    }

    [Fact]
    public void DisconnectDuringInit_DoesNotProduceFailed()
    {
        Radio.InitializationTimeoutMs = 150;
        Connect();
        Radio.Disconnect();
        Thread.Sleep(400);
        Assert.Equal(ConnectionState.Disconnected, Radio.Connection);
    }

    [Fact]
    public void Reconnect_RunsAFreshRitual()
    {
        ConnectReady();
        Radio.Disconnect();

        Connect();
        Assert.Equal(ConnectionState.Initializing, Radio.Connection);
        Assert.Equal(
            ["", "", "PORT_R ECHO OFF", "PORT_R ECHO OFF", "SH", "PORT_R", "POW", "BAT ST"],
            Transport.SentLines);
        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
    }

    [Fact]
    public void InitGarbage_NeverSurfacesAsErrors()
    {
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Connect();
        Transport.InjectLine("PORT_R ECHO OFF");      // our own echo, echo still on
        Transport.InjectLine("SH");                   // echoed command
        Transport.InjectLine("QQ%$#GARBAGE");         // stale buffer garbage
        Transport.InjectLine("** ERROR **");          // flush-CR rejection

        Assert.Empty(errors);
    }
}
