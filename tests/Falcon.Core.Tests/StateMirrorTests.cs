using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// The reported-state mirror: unconfirmed-until-reported (enum defaults can
/// never leak as displayed values) and the Q5 trigger-table transitions —
/// observed lines implying silent changes mark values unconfirmed and
/// schedule an observable re-poll at the next SSB prompt.
/// </summary>
public class StateMirrorTests : RadioTestBase
{
    // ---- Unconfirmed until reported ----------------------------------------

    [Fact]
    public void FreshState_EverythingUnconfirmed()
    {
        var s = Radio.State;
        Assert.False(s.OperatingMode.IsConfirmed);
        Assert.False(s.PowerLevel.IsConfirmed);
        Assert.False(s.ModulationMode.IsConfirmed);
        Assert.False(s.Bandwidth.IsConfirmed);
        Assert.False(s.AgcSpeed.IsConfirmed);
        Assert.False(s.RxFrequency.IsConfirmed);
        Assert.False(s.OperatingChannel.IsConfirmed);
        Assert.False(s.AnalogSquelch.IsConfirmed);
        Assert.False(s.Ale.LinkState.IsConfirmed);
        Assert.False(s.Ale.FillState.IsConfirmed);
        Assert.False(s.Hop.SyncState.IsConfirmed);
        Assert.False(s.Hop.CurrentNet.IsConfirmed);

        // Phase R mirrors start unconfirmed too — enum defaults can never
        // display (spot set; the reconnect test covers the reset path).
        Assert.False(s.DigitalVoice.IsConfirmed);
        Assert.False(s.DigitalSquelch.IsConfirmed);
        Assert.False(s.SquelchLevel.IsConfirmed);
        Assert.False(s.FmSquelch.IsConfirmed);
        Assert.False(s.Rwas.IsConfirmed);
        Assert.False(s.Encryption.IsConfirmed);
        Assert.False(s.RfGain.IsConfirmed);
        Assert.False(s.Contrast.IsConfirmed);
        Assert.False(s.Beep.IsConfirmed);
        Assert.False(s.PrePostFilter.IsConfirmed);
        Assert.False(s.Ale.AllCall.IsConfirmed);
        Assert.False(s.Ale.MaxScanChannels.IsConfirmed);
    }

    [Fact]
    public void ReportedValue_BecomesConfirmed()
    {
        ConnectReady();
        Transport.InjectLine("POWER low");
        Assert.True(Radio.State.PowerLevel.IsConfirmed);
        Assert.Equal(PowerLevel.Low, Radio.State.PowerLevel.Value);
    }

    [Fact]
    public void Reconnect_RevertsEverythingToUnconfirmed()
    {
        ConnectReady();
        Transport.InjectLine("POWER hi ");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SLFAD CAM               CHGROUP 01");
        Transport.InjectLine("DV ON ");                  // Phase R mirror
        Transport.InjectLine("ALL_CALL    ON  ");        // Phase R ALE mirror
        Assert.True(Radio.State.PowerLevel.IsConfirmed);
        Assert.Single(Radio.State.Ale.SelfAddresses);
        Assert.True(Radio.State.DigitalVoice.IsConfirmed);
        Assert.True(Radio.State.Ale.AllCall.IsConfirmed);

        Radio.Disconnect();
        Connect();

        Assert.False(Radio.State.PowerLevel.IsConfirmed);
        Assert.False(Radio.State.ModulationMode.IsConfirmed);
        Assert.Empty(Radio.State.Ale.SelfAddresses);
        Assert.False(Radio.State.DigitalVoice.IsConfirmed);
        Assert.False(Radio.State.Ale.AllCall.IsConfirmed);
    }

    [Fact]
    public void Reconnect_ZeroesBOTH_HopRefusalCounters()
    {
        // Round 11 P4 (a P1 audit finding dispositioned to the phase that
        // consumes it). The two HOP refusal counters are twins by design — each
        // exists because its line carries no state change of its own, so
        // consumers DIFF the count — but ResetForConnect zeroed only one of
        // them, and the No-Net-ID total therefore carried across sessions.
        //
        // It is load-bearing now: the round-11 §7 generation-attempt state
        // machine snapshots this counter, and it recognises a fresh connection
        // by the count stepping BACK past what it has already seen. A counter
        // that never resets can never step back, so the surface could hold a
        // refusal raised by a radio that is gone.
        ConnectReady();
        Transport.InjectLine("No Hopset");
        Transport.InjectLine("NO NET ID");
        Assert.Equal(1, Radio.State.Hop.NoHopsetCount);
        Assert.Equal(1, Radio.State.Hop.NoNetIdCount);

        Radio.Disconnect();
        Connect();

        Assert.Equal(0, Radio.State.Hop.NoHopsetCount);
        Assert.Equal(0, Radio.State.Hop.NoNetIdCount);
    }

    [Fact]
    public void Reconnect_RevertsEveryPhaseRMirror_NoPerFieldLeak()
    {
        // ResetForConnect must clear EVERY new Phase R mirror — a per-field
        // omission would leak one radio's setting onto the next session
        // (auditor MINOR: the reconnect test above only pinned 2 of the 33
        // new fields). Drive a representative line into each new mirror,
        // then reconnect and assert the whole set is unconfirmed.
        ConnectReady();
        var s = Radio.State;

        foreach (var line in new[]
        {
            "DV ON ", "DGT_SQUELCH ON ", "SQ_LEVEL HIGH", "FMSQUELCH ON",
            "FMSQ_TYPE tone", "FMTONE ON ", "FMDEV 8.0", "BFO +1000",
            "CWOFFSET 1000", "COMPRESS ON", "ANTENNA auto", "RETRANS DISABLED",
            "RWAS ENABLED", "UNKEY_M ENABLED", "AVS OFF", "ENCRYPT ON",
            "ENCRYPTION NOT INSTALLED", "CUR_KEY none", "RFG 100 ",
            "CONTRAST 05", "BEEP ON ", "PREPOST FILTER ENABLE",
            "PREPOST RXANTENNA DISABLE", "PREPOST SCAN SLOW",
            // Round-3 V7 provisional mirrors (old-app-derived shapes).
            "PREAMP ENABLED", "INTCOUPLER BYPASSED", "KWATT NO",
            // Round-4 AC provisional mirrors (old-app-derived shapes).
            "LIGHT MOMENTARY", "INTENSITY 04",
            // ALE settings mirrors
            "ALL_CALL ON", "ANY_CALL OFF", "AMD_DISPLAY ON", "KEY_TO_CALL OFF",
            "LSTN ON", "RAD_SIL OFF", "MAXCH 100", "TIME_OUT 000", "TUNETIME 015",
        })
            Transport.InjectLine(line);

        // Sanity: a broad sample is actually confirmed before the reset.
        Assert.True(s.DigitalVoice.IsConfirmed);
        Assert.True(s.Rwas.IsConfirmed);
        Assert.True(s.PrePostScanRate.IsConfirmed);
        Assert.True(s.Ale.TuneTimeSeconds.IsConfirmed);

        Radio.Disconnect();
        Connect();

        // Every new mirror is back to unconfirmed — enum/int defaults can
        // never leak across sessions.
        Assert.False(s.DigitalVoice.IsConfirmed);
        Assert.False(s.DigitalSquelch.IsConfirmed);
        Assert.False(s.SquelchLevel.IsConfirmed);
        Assert.False(s.FmSquelch.IsConfirmed);
        Assert.False(s.FmSquelchType.IsConfirmed);
        Assert.False(s.FmTone.IsConfirmed);
        Assert.False(s.FmDeviation.IsConfirmed);
        Assert.False(s.BfoOffset.IsConfirmed);
        Assert.False(s.CwOffset.IsConfirmed);
        Assert.False(s.Compression.IsConfirmed);
        Assert.False(s.Antenna.IsConfirmed);
        Assert.False(s.Retransmit.IsConfirmed);
        Assert.False(s.Rwas.IsConfirmed);
        Assert.False(s.UnkeyMask.IsConfirmed);
        Assert.False(s.Avs.IsConfirmed);
        Assert.False(s.Encryption.IsConfirmed);
        Assert.False(s.EncryptionAvailability.IsConfirmed);
        Assert.False(s.CurrentEncryptionKey.IsConfirmed);
        Assert.False(s.RfGain.IsConfirmed);
        Assert.False(s.Contrast.IsConfirmed);
        Assert.False(s.Beep.IsConfirmed);
        Assert.False(s.PrePostFilter.IsConfirmed);
        Assert.False(s.PrePostRxAntenna.IsConfirmed);
        Assert.False(s.PrePostScanRate.IsConfirmed);
        Assert.False(s.RxPreamp.IsConfirmed);
        Assert.False(s.InternalCoupler.IsConfirmed);
        Assert.False(s.OneKilowattPa.IsConfirmed);
        Assert.False(s.BacklightFunction.IsConfirmed);
        Assert.False(s.BacklightIntensity.IsConfirmed);
        Assert.False(s.Ale.AllCall.IsConfirmed);
        Assert.False(s.Ale.AnyCall.IsConfirmed);
        Assert.False(s.Ale.AmdDisplay.IsConfirmed);
        Assert.False(s.Ale.KeyToCall.IsConfirmed);
        Assert.False(s.Ale.ListenBeforeTx.IsConfirmed);
        Assert.False(s.Ale.RadioSilence.IsConfirmed);
        Assert.False(s.Ale.MaxScanChannels.IsConfirmed);
        Assert.False(s.Ale.LinkTimeoutMinutes.IsConfirmed);
        Assert.False(s.Ale.TuneTimeSeconds.IsConfirmed);
    }

    [Fact]
    public void Reconnect_ForgetsTheReportedUnprogrammedMarker_WithTheNetRecord()
    {
        // Round-4 Phase D: the marker is a REPORT about a net, so it lives and
        // dies with that net's record — one radio's wiped net must never read
        // as "not programmed" on the next session's radio.
        ConnectReady();
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Assert.True(Radio.State.Hop.Nets[5].IsReportedUnprogrammed);

        Radio.Disconnect();
        Connect();

        Assert.Empty(Radio.State.Hop.Nets);
    }

    // ---- Q4: no switch-driven re-reads ---------------------------------------

    [Fact]
    public void ModePrompts_NeverTriggerReads()
    {
        // A bare mode switch mutates nothing (R3); re-reads are event-driven
        // ONLY. Entering and re-entering modes sends NOTHING.
        ConnectReady();
        Transport.InjectLine("ALE> ");
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("HOP> ");
        Transport.InjectLine("SSB> ");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ChanLine_DoesNotTriggerAShow()
    {
        // The old CHAN→SHOW auto re-read was switch-thinking; a CHAN line is
        // an announced value, not a staleness event (Q4; trigger table Q5
        // does not list it).
        ConnectReady();
        Transport.InjectLine("CHAN 05 ");
        Assert.Empty(Transport.SentLines);
        Assert.Equal(5, Radio.State.OperatingChannel.Value);
    }

    // ---- Trigger row (a): MODEM engage/disengage (R8) --------------------------

    private void EstablishSsbBaseline()
    {
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("AGC SLOW");
        Transport.InjectLine("BAND 2.7 ");
        Transport.InjectLine("MODEM OFF");     // first sight: learning, not a mutation
        Transport.ClearSent();
    }

    [Fact]
    public void ModemChange_MarksAgcAndBandUnconfirmed_RepollsAtNextSsbPrompt()
    {
        ConnectReady();
        EstablishSsbBaseline();

        var compensations = new List<CompensationAppliedEventArgs>();
        Radio.CompensationApplied += (_, e) => compensations.Add(e);

        Transport.InjectLine("MODEM 1 T39 ");     // engage: silently drags AGC+BAND (R8)

        Assert.False(Radio.State.AgcSpeed.IsConfirmed);
        Assert.False(Radio.State.Bandwidth.IsConfirmed);
        Assert.Empty(Transport.SentLines);         // re-poll waits for an SSB prompt

        Transport.InjectLine("SSB> ");
        Assert.Equal(["AG", "BA"], Transport.SentLines);

        var comp = Assert.Single(compensations);
        Assert.Equal(["AG", "BA"], comp.Commands);
        Assert.Contains("R8", comp.Reason);

        // The answers re-confirm (the response IS the read-back).
        Transport.InjectLine("AGC MED ");
        Transport.InjectLine("RFG 100 ");
        Transport.InjectLine("BAND 3.0 ");
        Assert.Equal(AgcSpeed.Medium, Radio.State.AgcSpeed.Value);
        Assert.Equal("3.0", Radio.State.Bandwidth.Value);
    }

    [Fact]
    public void ModemFirstReport_IsLearningNotAMutation()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("MODEM 1 T39 ");      // first sight of the value
        Transport.InjectLine("SSB> ");
        Assert.Empty(Transport.SentLines);
        Assert.True(Radio.State.AgcSpeed.IsConfirmed == false);   // never was confirmed
    }

    [Fact]
    public void RepeatedModemLine_InShowBlocks_DoesNotRetrigger()
    {
        ConnectReady();
        EstablishSsbBaseline();
        Transport.InjectLine("MODEM OFF");          // same value again (SH block)
        Transport.InjectLine("SSB> ");
        Assert.Empty(Transport.SentLines);
        Assert.True(Radio.State.AgcSpeed.IsConfirmed);
    }

    [Fact]
    public void ModemPresetListingLine_NeverTriggers()
    {
        ConnectReady();
        EstablishSsbBaseline();
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        Transport.InjectLine("SSB> ");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void RepollDoesNotFireAtAlePrompt()
    {
        // SSB-domain commands are rejected at ALE>/HOP> prompts (session-18):
        // the re-poll must wait for SSB.
        ConnectReady();
        EstablishSsbBaseline();
        Transport.InjectLine("MODEM 1 T39 ");
        Transport.InjectLine("ALE> ");
        Assert.Empty(Transport.SentLines);
        Transport.InjectLine("SSB> ");
        Assert.Equal(["AG", "BA"], Transport.SentLines);
    }

    // ---- Trigger row (b): ALE call changes the channel (R7) ----------------------

    [Fact]
    public void CallingWithChannel_MarksChannelDomainUnconfirmed_RepollsShAtSsb()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("CHAN 00 ");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 04123000");
        Transport.InjectLine("AGC MED ");
        Transport.InjectLine("BAND 2.7 ");
        Transport.ClearSent();

        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");

        Assert.False(Radio.State.OperatingChannel.IsConfirmed);
        Assert.False(Radio.State.RxFrequency.IsConfirmed);
        Assert.False(Radio.State.TxFrequency.IsConfirmed);
        Assert.False(Radio.State.AgcSpeed.IsConfirmed);
        Assert.False(Radio.State.Bandwidth.IsConfirmed);

        Transport.InjectLine("SSB> ");
        Assert.Equal(["SH"], Transport.SentLines);
    }

    // ---- Trigger row (c): hop net select silently changes the SSB channel (R9b) ---

    [Fact]
    public void NetChange_MarksChannelDomainUnconfirmed()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("CHAN 01 ");
        Transport.InjectLine("NET  01");           // learning
        Transport.ClearSent();

        Transport.InjectLine("NET  00");           // actual selection change

        Assert.False(Radio.State.OperatingChannel.IsConfirmed);
        Transport.InjectLine("SSB> ");
        Assert.Equal(["SH"], Transport.SentLines);
    }

    [Fact]
    public void NetFirstReport_IsLearningNotAMutation()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("CHAN 01 ");
        Transport.ClearSent();
        Transport.InjectLine("NET  01");
        Transport.InjectLine("SSB> ");
        Assert.Empty(Transport.SentLines);
        Assert.True(Radio.State.OperatingChannel.IsConfirmed);
    }

    [Fact]
    public void GeneratingHopset_AlwaysTriggersChannelDomainRepoll()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("CHAN 01 ");
        Transport.ClearSent();

        Transport.InjectLine("Generating Hopset...");
        Assert.False(Radio.State.OperatingChannel.IsConfirmed);

        Transport.InjectLine("SSB> ");
        Assert.Equal(["SH"], Transport.SentLines);
    }

    // ---- Trigger row (d): FM-squelch cycle (kept; observable) ---------------------

    [Fact]
    public void FmSquelchCycle_FiresOnReturnToUsb_AndIsObservable()
    {
        ConnectReady();
        var compensations = new List<CompensationAppliedEventArgs>();
        Radio.CompensationApplied += (_, e) => compensations.Add(e);

        Transport.InjectLine("SSB> ");
        Transport.InjectLine("SQUELCH ON ");        // radio-confirmed ON
        Transport.InjectLine("MODE FM ");           // FM excursion arms the cycle
        Transport.InjectLine("FMDEV 8.0");
        Transport.ClearSent();

        Transport.InjectLine("MODE USB");           // back on USB: cycle fires
        Assert.Equal(["SQ OFF"], Transport.SentLines);
        Assert.Single(compensations);

        Transport.InjectLine("SQUELCH OFF");        // radio confirms off → restore
        Assert.Equal(["SQ OFF", "SQ ON"], Transport.SentLines);
        Assert.Equal(2, compensations.Count);
        Assert.All(compensations, c => Assert.Contains("FM-squelch", c.Reason));
    }

    [Fact]
    public void FmSquelchCycle_NeverArmsOffUnreportedSquelch()
    {
        // The old app was burned arming this off the enum DEFAULT (On) when
        // the radio actually had squelch off. Structurally impossible now:
        // arming requires a CONFIRMED On report.
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("MODE FM ");           // no squelch report this session
        Transport.InjectLine("FMDEV 8.0");
        Transport.ClearSent();

        Transport.InjectLine("MODE USB");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void FmSquelchCycle_DoesNotArmWhenSquelchReportedOff()
    {
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("SQUELCH OFF");
        Transport.InjectLine("MODE FM ");
        Transport.InjectLine("FMDEV 8.0");
        Transport.ClearSent();

        Transport.InjectLine("MODE USB");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void FmSquelchCycle_DoesNotFireAtAlePrompt()
    {
        // ALE SH blocks contain MODE lines; the SQ write is SSB-only.
        ConnectReady();
        Transport.InjectLine("SSB> ");
        Transport.InjectLine("SQUELCH ON ");
        Transport.InjectLine("MODE FM ");
        Transport.InjectLine("ALE> ");              // now at an ALE prompt
        Transport.ClearSent();
        Transport.InjectLine("MODE USB");           // MODE line inside an ALE SH block
        Assert.Empty(Transport.SentLines);
    }

    // ---- Mode-change deadline (30 s, not a Ping) -----------------------------------

    [Fact]
    public void ModeChange_PendingUntilThePromptArrives()
    {
        ConnectReady();
        Radio.SelectAle();
        Assert.Equal(["ALE"], Transport.SentLines);
        Assert.True(Radio.IsModeChangePending);

        Transport.InjectLine("ALE_INST  rf5122");
        Assert.True(Radio.IsModeChangePending);      // banner is not the prompt

        Transport.InjectLine("ALE> ");
        Assert.False(Radio.IsModeChangePending);
    }

    [Fact]
    public void ModeChange_TimesOutWithAnError()
    {
        ConnectReady();
        Radio.ModeChangeTimeoutMs = 100;
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Radio.SelectHop();
        Assert.True(Radio.IsModeChangePending);

        Thread.Sleep(400);

        Assert.False(Radio.IsModeChangePending);
        Assert.Contains(errors, m => m.Contains("Mode change"));
    }

    [Fact]
    public void ModeChange_WrongPromptDoesNotComplete()
    {
        ConnectReady();
        Radio.SelectHop();
        Transport.InjectLine("SSB> ");               // still in SSB
        Assert.True(Radio.IsModeChangePending);
        Transport.InjectLine("HOP> ");
        Assert.False(Radio.IsModeChangePending);
    }

    // ---- Error surfacing ---------------------------------------------------------

    [Fact]
    public void ErrorBanner_RaisesError_AfterReadyOnly()
    {
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Connect();
        Transport.InjectLine("** ERROR **");        // init flush garbage: expected
        Assert.Empty(errors);

        AnswerSentinel();
        AnswerSentinel();
        Transport.InjectLine("** ERROR **");
        Assert.Single(errors);
    }

    /// <summary>
    /// F3 (plan-clone-field-round2.md, decision A-5) — THE ZEROIZE BANNERS ARE
    /// NOT ERRORS.
    ///
    /// <para>Field report, 2026-08-21 item 3: the Operate screen showed an ERROR
    /// toast reading "zeroize complete", during a clone the operator had just
    /// authorised. Mechanism: both banners are <c>**</c>-fenced, the generic
    /// <c>**</c> arm raised every such line through <c>ErrorOccurred</c>,
    /// <c>ConsoleFeed</c> logged that as an Error entry and
    /// <c>RadioSessionViewModel</c> toasts every Error entry.</para>
    ///
    /// <para>Both halves are pinned here, because suppressing the wrong thing
    /// would be worse than the toast: the two banners raise NOTHING, and every
    /// other <c>**</c> line — <c>** ERROR **</c> first among them — still
    /// raises. The raw lines are still fed to the Console; that half is pinned
    /// on the feed itself (<c>MessageReceived</c>), which is where it lives.</para>
    /// </summary>
    [Fact]
    public void ZeroizeBanners_RaiseNoError_WhileEveryOtherBannerStillDoes()
    {
        ConnectReady();
        var errors = new List<string>();
        var received = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);
        Radio.MessageReceived += (_, e) => received.Add(e.Message);

        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("*** ZEROIZE COMPLETE ***");
        Assert.Empty(errors);

        // …and the EVIDENCE is untouched: the Console's Rx feed still has both
        // lines verbatim, which is why no new entry kind was needed (A-5).
        Assert.Contains("*** ZEROIZING RAM -- PLEASE WAIT ***", received);
        Assert.Contains("*** ZEROIZE COMPLETE ***", received);

        // ANTI-VACUITY / the other half of the contract: the arm names two
        // lines, not the family.
        Transport.InjectLine("** ERROR **");
        Assert.Equal("The radio rejected that command.", Assert.Single(errors));

        Transport.InjectLine("*** SOMETHING ELSE ***");
        Assert.Equal(2, errors.Count);
        Assert.Contains("SOMETHING ELSE", errors[1], StringComparison.Ordinal);
    }

    /// <summary>The suppression is a TOAST decision, not a state-machine one:
    /// the ZEROIZE settle window still opens on the banner and still closes on
    /// the next prompt (clone round 12 leg 2, X13). Same two lines, same
    /// silence, and the campaign's go-ahead still arrives.</summary>
    [Fact]
    public void ZeroizeBanners_StillDriveTheSettleStateMachine()
    {
        ConnectReady();
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Radio.Ssb.ZeroizeRadio();
        Assert.True(Radio.IsZeroizeSettling);

        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Assert.False(Radio.ZeroizeSettled);           // the banner opens the window
        Transport.InjectLine("*** ZEROIZE COMPLETE ***");
        Transport.InjectLine("SSB> ");                // the first prompt AFTER it settles
        Assert.True(Radio.ZeroizeSettled);
        Assert.False(Radio.ZeroizeFaulted);
        Assert.Empty(errors);
    }

    [Fact]
    public void RejectionLines_SurfaceAsErrors()
    {
        ConnectReady();
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Transport.InjectLine(" INV SELF ADDRESS ");
        Transport.InjectLine(" ADDRESS EXISTS ");
        Transport.InjectLine("Invalid In Hopping");
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void UnrecognizedLine_RaisesErrorWithTheLine()
    {
        ConnectReady();
        var errors = new List<RadioErrorEventArgs>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e);

        Transport.InjectLine("GIBBERISH 42");
        var err = Assert.Single(errors);
        Assert.Contains("GIBBERISH", err.Line);
    }

    // ---- Console visibility ---------------------------------------------------------

    [Fact]
    public void EverySend_RaisesLineSent()
    {
        ConnectReady();
        var sent = new List<string>();
        Radio.LineSent += (_, e) => sent.Add(e.Line);

        Radio.SetPowerLevel(PowerLevel.High);
        Radio.Ssb.SetFrequency("14234500");

        Assert.Equal(["POW HI", "FR 14234500"], sent);
    }

    // ====================================================================
    // X8 (plan-ale-programming.md §4.1): the one-at-a-time read queue.
    // These pin the CONCURRENCY contract the programming cards stand on —
    // one operation per store on the wire, union-coalescing requests, one
    // commit per operation publishing exactly its slot set, and completion
    // ids that equal what the requester was handed.
    // ====================================================================

    /// <summary>Every group-read completion, in order — the "commits exactly
    /// once" evidence.</summary>
    private List<AleReadCompletion> WatchGroupReads()
    {
        var seen = new List<AleReadCompletion>();
        Radio.StateChanged += (_, e) =>
        {
            if (e.PropertyChanged == RadioProperty.AleGroupRead)
                seen.Add(Radio.State.Ale.LastGroupRead);
        };
        return seen;
    }

    [Fact]
    public void GroupRead_CommitsItsSlotOnce_AndASilentSlotIsConfirmedEmpty()
    {
        ConnectReady();
        var completions = WatchGroupReads();

        long readId = Radio.Ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        Assert.Null(Radio.State.Ale.ChannelGroups[1].Channels);   // uncommitted: still "—"

        AnswerSentinel();

        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);
        // Request-return id == completion id (the matching contract).
        Assert.Equal([new AleReadCompletion(readId, true)], completions);

        // A group the radio stays SILENT on (the captured empty-group
        // behavior) commits as confirmed-EMPTY, not as never-queried.
        Radio.Ale.RequestChannelGroup(2);
        AnswerSentinel();
        Assert.NotNull(Radio.State.Ale.ChannelGroups[2].Channels);
        Assert.Empty(Radio.State.Ale.ChannelGroups[2].Channels!);
    }

    [Fact]
    public void GroupRead_UnansweredSentinel_PublishesNothing_AndStillCompletes()
    {
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        var completions = WatchGroupReads();

        Radio.Ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        Thread.Sleep(300);                                // sentinel swallowed

        // Prior state stands (here: never queried) — a half-read group is
        // never published…
        Assert.Null(Radio.State.Ale.ChannelGroups[1].Channels);
        // …and the operation still COMPLETES, so no caller waits forever.
        Assert.NotEmpty(completions);
        Assert.False(completions[0].Answered);
    }

    [Fact]
    public void GroupRead_UnansweredSentinel_KeepsAPreviouslyConfirmedGroup()
    {
        // Anti-vacuity for the pin above: "keeps prior state" must hold when
        // the slot ALREADY carries radio-confirmed channels.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        Radio.Ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        AnswerSentinel();
        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);

        Radio.Ale.RequestChannelGroup(1);
        Thread.Sleep(300);                                // this one is swallowed

        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);
    }

    [Fact]
    public void RapidPickerSpins_ActiveCommitsItsSlot_TheRestCoalesceIntoOneUnion()
    {
        // The exact §4.1 sequence: a spin to 3 begins the operation, spins to
        // 4 and 5 arrive while it is on the wire and become ONE pending
        // operation over {4,5}. Two commits total, each publishing exactly
        // its own slot set — no partial commit, nothing suppressed.
        ConnectReady();
        var completions = WatchGroupReads();

        long active = Radio.Ale.RequestChannelGroup(3);
        Assert.Equal(["CHG 3", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        long pending = Radio.Ale.RequestChannelGroup(4);
        long alsoPending = Radio.Ale.RequestChannelGroup(5);

        Assert.Empty(Transport.SentLines);                // the pending op sends NOTHING yet
        Assert.NotEqual(active, pending);
        Assert.Equal(pending, alsoPending);               // both spins share the union's id

        Transport.InjectLine("CHGROUP 03 CHANS 00 ");
        AnswerSentinel();                                 // active commits {3}

        Assert.Equal([new AleReadCompletion(active, true)], completions);
        Assert.Equal([0], Radio.State.Ale.ChannelGroups[3].Channels);
        Assert.Null(Radio.State.Ale.ChannelGroups[4].Channels);
        // …and only NOW does the union go on the wire, in slot order.
        Assert.Equal(["CHG 4", "CHG 5", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("CHGROUP 04 CHANS 07 ");
        Transport.InjectLine("CHGROUP 05 CHANS 08 09 ");
        AnswerSentinel();                                 // union commits {4,5} — once

        Assert.Equal(
            [new AleReadCompletion(active, true), new AleReadCompletion(pending, true)],
            completions);
        Assert.Equal([7], Radio.State.Ale.ChannelGroups[4].Channels);
        Assert.Equal([8, 9], Radio.State.Ale.ChannelGroups[5].Channels);
    }

    [Fact]
    public void StaleGroupLine_CannotReachALaterOperationsAccumulator()
    {
        // CONTAMINATION pin. A late "CHGROUP 03" line — the answer to an
        // operation that has already committed — arrives while the NEXT
        // operation (over {4}) is active. It must not enter that operation's
        // accumulator, and it must not sneak into its commit either.
        ConnectReady();
        Radio.Ale.RequestChannelGroup(3);
        Radio.Ale.RequestChannelGroup(4);          // pending {4}

        Transport.InjectLine("CHGROUP 03 CHANS 00 ");
        AnswerSentinel();                          // {3} commits, {4} begins
        Assert.Equal([0], Radio.State.Ale.ChannelGroups[3].Channels);

        // The stale line, mid-{4}-operation.
        Transport.InjectLine("CHGROUP 03 CHANS 05 06 ");
        Transport.InjectLine("CHGROUP 04 CHANS 07 ");
        AnswerSentinel();

        Assert.Equal([7], Radio.State.Ale.ChannelGroups[4].Channels);
        Assert.Equal([0], Radio.State.Ale.ChannelGroups[3].Channels);   // NOT 05 06
    }

    [Fact]
    public void APendingRead_IsNeverPromotedAcrossASilentOperation()
    {
        // AUDITOR'S BLOCKER 3, byte for byte. A confirmed group 1, then the
        // active group-1 read TIMES OUT with a same-slot read pending. The
        // old behavior promoted the pending read, whose accumulator could not
        // tell the dead operation's delayed lines from its own — so a late
        // "CHGROUP 01 CHANS 05" was committed as the pending read's own
        // snapshot. An unanswered sentinel means the radio never said where
        // it is in the command stream, so the pending read is ABANDONED
        // instead: nothing is dispatched, nothing is published, and its
        // requesters are told (Answered == false) rather than left waiting.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        var completions = WatchGroupReads();

        Radio.Ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        AnswerSentinel();
        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);
        completions.Clear();

        long active = Radio.Ale.RequestChannelGroup(1);
        long pending = Radio.Ale.RequestChannelGroup(1);      // coalesced, same slot
        Transport.ClearSent();
        Thread.Sleep(300);                                    // the active sentinel is swallowed

        // The pending read was never put on the wire…
        Assert.Empty(Transport.SentLines);
        // …and BOTH operations completed unanswered, publishing nothing.
        Assert.Equal(
            [new AleReadCompletion(active, false), new AleReadCompletion(pending, false)],
            completions);
        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);

        // The dead operation's delayed answer arrives with the store idle: it
        // is the radio's own latest word about that slot, so it upserts
        // (standalone-line doctrine) — it is NOT attributed to any read, and
        // the next read overwrites it.
        Transport.InjectLine("CHGROUP 01 CHANS 05 ");
        Assert.Equal([5], Radio.State.Ale.ChannelGroups[1].Channels);
        Assert.Equal(2, completions.Count);                   // no third commit appeared

        long fresh = Radio.Ale.RequestChannelGroup(1);
        Assert.Equal(["CHG 1", "BAT ST"], Transport.SentLines);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        AnswerSentinel();
        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);   // self-corrects
        Assert.Equal(new AleReadCompletion(fresh, true), Radio.State.Ale.LastGroupRead);
    }

    [Fact]
    public void APendingBookRead_IsNeverPromotedAcrossASilentOperation()
    {
        // The book store's half of the same rule.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        var completions = new List<AleReadCompletion>();
        Radio.StateChanged += (_, e) =>
        {
            if (e.PropertyChanged == RadioProperty.AleBookRead)
                completions.Add(Radio.State.Ale.LastBookRead);
        };

        long active = Radio.Ale.RefreshStationList();
        long pending = Radio.Ale.RefreshStationList();
        Transport.ClearSent();
        Thread.Sleep(300);

        Assert.Empty(Transport.SentLines);                    // no promotion, no listings
        Assert.Equal(
            [new AleReadCompletion(active, false), new AleReadCompletion(pending, false)],
            completions);
    }

    [Fact]
    public void ASingleSlotRequest_DuringABulkRead_StillWaitsItsTurn()
    {
        // §9 clause 4's bulk/single same-slot ordering. The mutation this
        // kills: letting a request bypass the pending queue when the ACTIVE
        // operation already covers its slot ({3} ⊆ {0..9}). That would put a
        // second CHG 3 on the wire inside another operation's window, and the
        // single read's own commit would then be built from whichever answer
        // happened to arrive first.
        ConnectReady();
        var completions = WatchGroupReads();

        long bulk = Radio.Ale.RefreshChannelGroups();
        Transport.ClearSent();

        long single = Radio.Ale.RequestChannelGroup(3);
        Assert.Empty(Transport.SentLines);                    // NOTHING extra on the wire
        Assert.NotEqual(bulk, single);                        // …and it is its own operation

        Transport.InjectLine("CHGROUP 03 CHANS 00 ");
        AnswerSentinel();                                     // the bulk commits {0..9}

        Assert.Equal([new AleReadCompletion(bulk, true)], completions);
        Assert.Equal([0], Radio.State.Ale.ChannelGroups[3].Channels);
        // …and ONLY now does the single read go out, as its own operation.
        Assert.Equal(["CHG 3", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("CHGROUP 03 CHANS 01 02 ");
        AnswerSentinel();

        Assert.Equal(
            [new AleReadCompletion(bulk, true), new AleReadCompletion(single, true)],
            completions);
        Assert.Equal([1, 2], Radio.State.Ale.ChannelGroups[3].Channels);
        // The single read published ONLY its own slot: the bulk's other nine
        // slots are untouched by it.
        Assert.Empty(Radio.State.Ale.ChannelGroups[4].Channels!);
    }

    [Fact]
    public void BulkGroupRead_IsTheSameMachinery_OverTheWholeSlotSet()
    {
        ConnectReady();
        var completions = WatchGroupReads();

        long readId = Radio.Ale.RefreshChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        Transport.InjectLine("CHGROUP 07 CHANS 12 ");
        AnswerSentinel();

        Assert.Equal([new AleReadCompletion(readId, true)], completions);   // ONE commit
        Assert.Equal([0, 1], Radio.State.Ale.ChannelGroups[1].Channels);
        Assert.Equal([12], Radio.State.Ale.ChannelGroups[7].Channels);
        // Every OTHER slot in the set was queried and answered with silence.
        foreach (int g in new[] { 0, 2, 3, 4, 5, 6, 8, 9 })
        {
            Assert.NotNull(Radio.State.Ale.ChannelGroups[g].Channels);
            Assert.Empty(Radio.State.Ale.ChannelGroups[g].Channels!);
        }
    }

    [Fact]
    public void UnsolicitedGroupLine_OutsideAnyOperation_UpsertsThePublishedSlot()
    {
        // The standalone-line doctrine every address line already follows.
        ConnectReady();
        Transport.InjectLine("CHGROUP 06 CHANS 03 04 ");
        Assert.Equal([3, 4], Radio.State.Ale.ChannelGroups[6].Channels);
        Assert.Null(Radio.State.Ale.ChannelGroups[5].Channels);   // nothing else moved
    }

    [Fact]
    public void BookRead_RequestsDuringAnActiveOne_CoalesceIntoOnePendingOperation()
    {
        ConnectReady();
        var completions = new List<AleReadCompletion>();
        Radio.StateChanged += (_, e) =>
        {
            if (e.PropertyChanged == RadioProperty.AleBookRead)
                completions.Add(Radio.State.Ale.LastBookRead);
        };

        long active = Radio.Ale.RefreshStationList();
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        long pending = Radio.Ale.RefreshStationList();
        long alsoPending = Radio.Ale.RefreshStationList();
        Assert.Empty(Transport.SentLines);              // nothing until the active one commits
        Assert.Equal(pending, alsoPending);
        Assert.NotEqual(active, pending);

        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        AnswerSentinel();

        Assert.Equal([new AleReadCompletion(active, true)], completions);
        Assert.Equal(["ZZZ"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        AnswerSentinel();

        Assert.Equal(
            [new AleReadCompletion(active, true), new AleReadCompletion(pending, true)],
            completions);
        Assert.Equal(["TST"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
    }

    [Fact]
    public void ReconnectResets_TheGroupTable_TheRefusal_AndTheCompletions()
    {
        ConnectReady();
        Transport.InjectLine("CHGROUP 06 CHANS 03 04 ");
        Transport.InjectLine(" ADDRESS EXISTS ");
        Radio.Ale.RequestChannelGroup(1);
        AnswerSentinel();

        Assert.NotEqual(default, Radio.State.Ale.LastGroupRead);
        Assert.Equal(1, Radio.State.Ale.ProgrammingRefusal.Sequence);

        Radio.Disconnect();
        ConnectReady();

        Assert.All(Radio.State.Ale.ChannelGroups, g => Assert.Null(g.Channels));
        Assert.Equal(default, Radio.State.Ale.ProgrammingRefusal);
        Assert.Equal(default, Radio.State.Ale.LastGroupRead);
        Assert.Equal(default, Radio.State.Ale.LastBookRead);
    }
}
