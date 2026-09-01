using Falcon.App.Core.Session;

namespace Falcon.App.Tests;

/// <summary>
/// The guarded baud wizard's engine (Stage 11, plan §7 decision 3), driven
/// end-to-end against the injecting transport: the session DROP is the
/// SUCCESS signal; a sentinel that gets ANSWERED means the radio ignored the
/// command; reopen failure retries BOTH rates (new first, then old) and
/// reports which answered; same-rate is a no-op that sends nothing; the
/// verify step trusts only the PORT_R dump in the mirror.
/// </summary>
public class BaudChangeFlowTests : SessionTestBase
{
    private BaudChangeFlow Flow()
    {
        // GUI rejigger G1: the session default flipped to OFF. These pins
        // verify the flow SUSPENDS auto-reconnect and RESTORES the value it
        // found, so give it a non-default value to restore.
        Session.AutoReconnectEnabled = true;
        return new(Radio, Session) { DropTimeoutMs = 100, VerifyTimeoutMs = 500, ReopenDelayMs = 25 };
    }

    /// <summary>Drive the reopened session to Ready with the PORT_R dump
    /// already mirrored (dump line first — command order on the real wire:
    /// PORT_R answers before the sentinel).</summary>
    private void AnswerReopen(string reportedBaud)
    {
        Transport.InjectLine($"PORT_REMOTE BAUD {reportedBaud}");
        AnswerSentinel();
    }

    // ---- Drop-as-success (the happy path) --------------------------------

    [Fact]
    public void HappyPath_DropIsTheSuccessSignal_ReopensVerifiesDone()
    {
        ConnectReady();
        var flow = Flow();

        flow.Start(4800);

        // The whitelisted builder's line went out, then the drop-probe
        // sentinel. (Round 10 §5: no token accompanies it any more.)
        Assert.Equal(["PORT_R BAUD 4800", "BAT ST"], Transport.SentLines);
        Assert.Equal(BaudChangeState.WaitingForDrop, flow.State);
        // Auto-reconnect is suspended for the duration (the poller would
        // re-dial the OLD rate mid-flow).
        Assert.False(Session.AutoReconnectEnabled);

        // No BATTERY answer arrives (the radio is gone at the old rate):
        // the ping times out → drop confirmed → close + reopen at 4800.
        Assert.True(WaitUntil(() => Transport.OpenCount == 2),
            $"no reopen; state={flow.State} '{flow.StatusText}'");
        Assert.Equal(4800, Transport.LastSettings?.BaudRate);
        Assert.Equal("COM7", Transport.LastSettings?.PortName);
        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Reopening));
        // Audit round 1, F3: the low-rate patience hint rides the
        // Reopening progress text.
        Assert.Contains("several minutes at low rates", flow.StatusText);

        AnswerReopen("4800");

        Assert.Equal(BaudChangeState.Done, flow.State);
        Assert.Contains("verified", flow.StatusText);
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Equal(4800, Session.BaudRate);
        Assert.Equal(4800, flow.AnsweredBaud);
        Assert.True(Session.AutoReconnectEnabled);   // restored
    }

    /// <summary>Audit round 1, F1: the reopen DELAY itself is pinned — the
    /// flow must NOT dial the port inside ReopenDelayMs of the drop verdict
    /// (live-gate run 1: an immediate reopen hits the Windows/FTDI
    /// access-denied handle-release latency). Removing the delay timer
    /// (dialing immediately) fails this test; nothing else pins it.</summary>
    [Fact]
    public void ReopenDelay_NoDialInsideTheDelayWindow()
    {
        ConnectReady();
        var flow = Flow();
        flow.ReopenDelayMs = 5_000;   // window far beyond the assert point

        flow.Start(4800);

        // Drive to the drop verdict: the probe times out (100 ms) and the
        // flow closes the port and enters Reopening.
        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Reopening),
            $"no drop verdict; state={flow.State} '{flow.StatusText}'");
        Assert.False(Transport.IsOpen);   // closed, awaiting the release delay

        // ~500 ms after the drop (10x margin against the 5 s window): the
        // port must NOT have been re-dialed yet.
        Thread.Sleep(500);
        Assert.Equal(1, Transport.OpenCount);
        Assert.Equal(BaudChangeState.Reopening, flow.State);
        // Deterministic end: the dial is still pending; the test ends here
        // (a late fire against the injecting transport is harmless).
    }

    // ---- Timeout-no-drop: the radio ANSWERED at the old rate ----------------

    [Fact]
    public void DropProbeAnswered_RadioIgnoredTheCommand_FailsWithoutTouchingTheSession()
    {
        ConnectReady();
        var flow = Flow();

        flow.Start(4800);
        AnswerSentinel();   // the radio still answers at the old rate

        Assert.Equal(BaudChangeState.Failed, flow.State);
        Assert.Contains("still answered", flow.StatusText);
        Assert.Equal(1, Transport.OpenCount);         // no reopen
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.True(Session.AutoReconnectEnabled);    // restored
    }

    // ---- Same-rate selection: no-op with reason, nothing sent -----------------

    [Fact]
    public void SameRate_NoOpWithReason_NothingSent()
    {
        ConnectReady();
        var flow = Flow();

        flow.Start(9600);

        Assert.Equal(BaudChangeState.NoOp, flow.State);
        Assert.Contains("already at 9600", flow.StatusText);
        Assert.Empty(Transport.SentLines);
        Assert.True(Session.AutoReconnectEnabled);    // never suspended
    }

    // ---- The Core token gate is GONE (round 10 §5) ---------------------------

    /// <summary>
    /// UI tweaks round 10 (§5, owner ruling 9): the typed-token parameters are
    /// removed from BOTH ends of this path — "the back end does what the GUI
    /// tells it". The old <c>WrongConfirmationToken_NothingSent_FlowFails</c>
    /// pin (a wrong token → nothing sent → Failed) is DELETED with the gate it
    /// tested; this SIGNATURE pin replaces it.
    ///
    /// <para>A behavioural test cannot pin the ABSENCE of a parameter. Nor can
    /// the public-surface whitelist in CommandSurfaceTests: it counts overload
    /// NAMES, so it does catch a token-taking overload ADDED beside the clean
    /// one (the name count goes to 2), but it is blind to an ARITY CHANGE that
    /// keeps the count — <c>(int)</c> rewritten back to <c>(int, string)</c> is
    /// still one method of that name. Reflection on the exact parameter lists
    /// is what catches that: <c>Prc138Radio.SetRemoteBaud</c> is EXACTLY one
    /// overload taking EXACTLY <c>(int)</c>, and so is
    /// <c>BaudChangeFlow.Start</c> — which no whitelist covers at all.</para>
    ///
    /// <para>This removal is scoped to the two destructive-DATA senders (§0.3).
    /// The three TRANSMIT-hazard token gates — <c>SetKeyline</c> (TRANSMIT),
    /// <c>SelfTest</c>, <c>VswrTest</c> — are untouched and keep their pins in
    /// CommandSurfaceTests.</para>
    /// </summary>
    [Fact]
    public void TheBaudPath_TakesNoConfirmationToken_AtEitherEnd()
    {
        AssertSingleOverloadTaking<Falcon.Core.Radio.Prc138Radio>(
            nameof(Falcon.Core.Radio.Prc138Radio.SetRemoteBaud), typeof(int));
        AssertSingleOverloadTaking<BaudChangeFlow>(
            nameof(BaudChangeFlow.Start), typeof(int));
    }

    /// <summary>Anti-vacuity partner for the pin above: the helper must be
    /// able to FAIL. A name that is not there, and a real method whose
    /// parameter list is not the asserted one, both have to be caught —
    /// otherwise "the signature is clean" would mean "the scan found
    /// nothing".</summary>
    [Fact]
    public void TheSignatureHelper_FailsOnAMissingNameAndOnAWrongParameterList()
    {
        Assert.ThrowsAny<Exception>(() =>
            AssertSingleOverloadTaking<BaudChangeFlow>("NoSuchMethod", typeof(int)));
        // Start(int) really exists — asserting the OLD (int, string) list must fail.
        Assert.ThrowsAny<Exception>(() =>
            AssertSingleOverloadTaking<BaudChangeFlow>(
                nameof(BaudChangeFlow.Start), typeof(int), typeof(string)));
    }

    private static void AssertSingleOverloadTaking<T>(string name, params Type[] parameters)
    {
        var overloads = typeof(T)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name)
            .ToList();

        var method = Assert.Single(overloads);
        Assert.Equal(parameters, method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void NotReady_RefusesWithReason_NothingSent()
    {
        var flow = Flow();          // never connected
        flow.Start(4800);
        Assert.Equal(BaudChangeState.Idle, flow.State);
        Assert.Contains("Not connected", flow.StatusText);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Reopen fails → dual-rate retry (new first, then old) -----------------

    [Fact]
    public void ReopenFails_RetriesNewRateFirst_NewRateAnswers_Done()
    {
        ConnectReady();
        Radio.InitializationTimeoutMs = 400;   // fast watchdog for the dead attempt
        var flow = Flow();

        flow.Start(4800);

        // Attempt 1 at 4800 (open #2): no answer → watchdog Failed.
        Assert.True(WaitUntil(() => Transport.OpenCount == 2));
        Assert.Equal(4800, Transport.LastSettings?.BaudRate);

        // Retry pass, new rate first: attempt 2 at 4800 (open #3) — answer it.
        Assert.True(WaitUntil(() => Transport.OpenCount == 3),
            $"no retry attempt; state={flow.State} '{flow.StatusText}'");
        Assert.Equal(4800, Transport.LastSettings?.BaudRate);
        AnswerReopen("4800");

        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Done));
        Assert.Equal(4800, flow.AnsweredBaud);
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.True(Session.AutoReconnectEnabled);
    }

    [Fact]
    public void ReopenFails_OldRateAnswers_ReportsWhichRateAnswered()
    {
        ConnectReady();
        Radio.InitializationTimeoutMs = 400;
        var flow = Flow();

        flow.Start(4800);

        // Attempts 1 and 2 (4800) die on the watchdog; attempt 3 dials the
        // OLD rate (open #4) — answer it there.
        Assert.True(WaitUntil(() => Transport.OpenCount == 4
                                    && Transport.LastSettings?.BaudRate == 9600, 5_000),
            $"never dialed the old rate; state={flow.State} '{flow.StatusText}'");
        AnswerReopen("9600");

        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Failed));
        // The wizard never leaves the operator guessing which rate answered.
        Assert.Equal(9600, flow.AnsweredBaud);
        Assert.Contains("answered at 9600", flow.StatusText);
        Assert.Equal(SessionPhase.Ready, Session.Phase);   // re-established at old
        Assert.Equal(9600, Session.BaudRate);
        Assert.True(Session.AutoReconnectEnabled);
    }

    [Fact]
    public void NeitherRateAnswers_FailsNamingFrontPanelRecovery()
    {
        ConnectReady();
        Radio.InitializationTimeoutMs = 300;
        var flow = Flow();

        flow.Start(4800);

        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Failed, 8_000),
            $"state={flow.State} '{flow.StatusText}'");
        Assert.Contains("neither", flow.StatusText);
        Assert.Contains("front-panel", flow.StatusText);
        Assert.True(Session.AutoReconnectEnabled);
    }

    // ---- Verify paths -----------------------------------------------------------

    [Fact]
    public void VerifyMismatch_DumpReportsAnotherRate_Fails()
    {
        ConnectReady();
        var flow = Flow();

        flow.Start(4800);
        Assert.True(WaitUntil(() => Transport.OpenCount == 2));

        AnswerReopen("9600");   // connected at 4800 but the dump says 9600 (!)

        Assert.Equal(BaudChangeState.Failed, flow.State);
        Assert.Contains("mismatch", flow.StatusText);
        Assert.Contains("9600", flow.StatusText);
    }

    [Fact]
    public void VerifyWithSwallowedDump_RequeriesPortR_ThenDone()
    {
        ConnectReady();
        var flow = Flow();

        flow.Start(4800);
        Assert.True(WaitUntil(() => Transport.OpenCount == 2));

        // Ready WITHOUT a dump line: the ritual's PORT_R was swallowed.
        AnswerSentinel();
        Assert.Equal(BaudChangeState.Verifying, flow.State);
        Assert.Contains("PORT_R", Transport.SentLines);   // the re-query went out

        // The re-query answers, then the verify sentinel completes.
        Transport.InjectLine("PORT_REMOTE BAUD 4800");
        AnswerSentinel();   // second init sentinel
        AnswerSentinel();   // the verify ping

        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Done),
            $"state={flow.State} '{flow.StatusText}'");
    }

    [Fact]
    public void VerifyDumpNeverAnswers_FailsHonestly()
    {
        ConnectReady();
        // Short init window so the reopen ritual's SECOND sentinel (queued
        // ahead of the verify ping) times out quickly — the verify ping only
        // dispatches once it is the queue head.
        Radio.InitializationTimeoutMs = 2_000;
        var flow = Flow();
        flow.VerifyTimeoutMs = 100;

        flow.Start(4800);
        Assert.True(WaitUntil(() => Transport.OpenCount == 2));

        AnswerSentinel();   // Ready, no dump ever
        Assert.True(WaitUntil(() => flow.State == BaudChangeState.Failed),
            $"state={flow.State} '{flow.StatusText}'");
        Assert.Contains("did not answer", flow.StatusText);
    }
}
