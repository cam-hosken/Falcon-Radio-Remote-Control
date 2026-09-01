using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;

namespace Falcon.App.Tests;

/// <summary>
/// The Signal section under the §2.4 constitution: unreported MODE lights
/// NOTHING (the enum-default leak — ordinal 0 is Usb); the BW choice list
/// follows the CONFIRMED modulation only (empty/disabled while unconfirmed)
/// using the MEASURED R5 sets; the no-reject rule — the radio's BA answer
/// is the read-back, displayed verbatim, never an error; and the F6 00-gate
/// (MODE/BW/AGC are channel-stored — editable only on a confirmed CH 00,
/// unconfirmed channel counts as NOT 00).
/// </summary>
public class SignalViewModelTests : SessionTestBase
{
    private SignalViewModel Vm()
        => new(new SsbSurface(Radio), new ChannelSurface(Radio), Session);

    [Fact]
    public void UnreportedMode_LightsNoSegment_NeverUsb()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        // Enum ordinal 0 is Usb — an enum-default leak would light USB here.
        Assert.False(vm.IsUsbActive);
        Assert.False(vm.IsLsbActive);
        Assert.False(vm.IsAmeActive);
        Assert.False(vm.IsCwActive);
        Assert.False(vm.IsFmActive);
    }

    [Fact]
    public void ProgrammaticStateWrite_SendsNoCommand()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("AGC SLOW");

        Assert.True(vm.IsUsbActive);
        Assert.Equal("2.7", vm.BandwidthText);
        Assert.Equal("SLOW", vm.AgcText);
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>F8 (plan-clone-field-round2.md): a session that has never had a
    /// modulation confirmed shows the USB set — PRESENT, but disabled, unlit and
    /// unsendable. The row is a menu; the highlight is the report.</summary>
    [Fact]
    public void BandwidthChoices_AreTheUsbSetAndDisabled_WhenNothingWasEverConfirmed()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"],
            vm.BandwidthChoices.Select(c => c.Value));
        Assert.DoesNotContain(vm.BandwidthChoices, c => c.IsActive);
        Assert.False(vm.IsBandwidthEnabled);
        Assert.NotEqual("", vm.BandwidthDisabledReason);

        vm.SetBandwidthCommand.Execute("2.7");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void BandwidthChoices_FollowTheConfirmedModulation_MeasuredSets()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODE USB");
        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"],
            vm.BandwidthChoices.Select(c => c.Value));

        Transport.InjectLine("MODE FM");
        // The MEASURED FM set (R5) — wider than HELP's "(2.7)".
        Assert.Equal(["1.0", "1.5", "2.0", "2.4", "2.7"],
            vm.BandwidthChoices.Select(c => c.Value));

        Transport.InjectLine("MODE CW");
        Assert.Equal(["0.35", "0.68", "1.0", "1.5"],
            vm.BandwidthChoices.Select(c => c.Value));
    }

    // ---- F8: the bandwidth row survives a Digital Voice toggle --------------
    // Field report, 2026-08-21: "the Operate bandwidth buttons vanish while
    // Digital Voice toggles". Mechanism: a confirmed DV line silently forces
    // USB, analog squelch ON and a bandwidth move, so Core unconfirms the
    // modulation mirror until the radio re-reports it (round-13 D1,
    // RadioState.UnconfirmDvForcedValues) — and the chip row was keyed straight
    // to that mirror.

    /// <summary>THE CONVICTION TEST. Across the whole DV window the list is
    /// never empty and never lights anything, and the re-report puts the
    /// highlight back.</summary>
    [Fact]
    public void BandwidthChoices_SurviveADigitalVoiceToggle_UnlitButNeverEmpty()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("BAND 2.4");
        Assert.Equal(["1.0", "1.5", "2.0", "2.4", "2.7"],
            vm.BandwidthChoices.Select(c => c.Value));
        Assert.Equal("2.4", Assert.Single(vm.BandwidthChoices, c => c.IsActive).Value);
        Assert.True(vm.IsBandwidthEnabled);

        // The DV change. Core unconfirms the forced values…
        Transport.InjectLine("DV ON");
        Assert.False(Radio.State.ModulationMode.IsConfirmed);   // anti-vacuity: the window is real

        // …and the row is STILL the last confirmed modulation's measured set —
        // retained across the unconfirm, exactly as F8 requires.
        Assert.Equal(["1.0", "1.5", "2.0", "2.4", "2.7"],
            vm.BandwidthChoices.Select(c => c.Value));
        // …with NOTHING lit, because the radio has not said where it is (I-7)…
        Assert.DoesNotContain(vm.BandwidthChoices, c => c.IsActive);
        // …and the row disabled with the existing reason.
        Assert.False(vm.IsBandwidthEnabled);
        Assert.Equal("Bandwidth choices wait for the radio to report the modulation.",
            vm.BandwidthDisabledReason);

        // The radio re-reports: the forced USB, and the bandwidth it moved to.
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"],
            vm.BandwidthChoices.Select(c => c.Value));
        Assert.Equal("2.7", Assert.Single(vm.BandwidthChoices, c => c.IsActive).Value);
        Assert.True(vm.IsBandwidthEnabled);
        Assert.Equal("", vm.BandwidthDisabledReason);
    }

    /// <summary>The memory is SESSION-scoped: the phase leaving Ready clears it,
    /// so the next session starts on the USB set rather than inheriting the last
    /// radio's menu. (RadioState.ResetForConnect is silent and keeps the
    /// mirrors, so the mirror alone could not have said this.)</summary>
    [Fact]
    public void TheRememberedModulation_IsClearedWhenThePhaseLeavesReady()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE CW");
        Assert.Equal(["0.35", "0.68", "1.0", "1.5"],
            vm.BandwidthChoices.Select(c => c.Value));

        Session.Close();
        Assert.NotEqual(SessionPhase.Ready, Session.Phase);
        // ANTI-VACUITY: the mirror SURVIVES the close, so a VM that still read
        // it would keep showing the CW set here.
        Assert.True(Radio.State.ModulationMode.IsConfirmed);
        Assert.Equal(ModulationMode.Cw, Radio.State.ModulationMode.Value);

        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"],
            vm.BandwidthChoices.Select(c => c.Value));
        Assert.DoesNotContain(vm.BandwidthChoices, c => c.IsActive);
        Assert.False(vm.IsBandwidthEnabled);
    }

    /// <summary>The rewired properties really NOTIFY — a list rebuilt in place
    /// with no PropertyChanged would leave the old chips on screen and this
    /// whole fix invisible.</summary>
    [Fact]
    public void TheBandwidthRow_RaisesPropertyChanged_AcrossTheDvWindow()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Transport.InjectLine("DV ON");
        Assert.Contains(nameof(vm.BandwidthChoices), raised);
        Assert.Contains(nameof(vm.IsBandwidthEnabled), raised);
        Assert.Contains(nameof(vm.BandwidthDisabledReason), raised);

        raised.Clear();
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 3.0");
        Assert.Contains(nameof(vm.BandwidthChoices), raised);
        Assert.Contains(nameof(vm.IsBandwidthEnabled), raised);
    }

    [Fact]
    public void BandwidthAnswer_IsTheDisplayTruth_NoRejectNoError()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Transport.ClearSent();

        // Send BA 4.0 in USB. The radio never rejects BA — it keeps a valid
        // value and reports what it kept (probe R5: USB caps at 3.0).
        vm.SetBandwidthCommand.Execute("4.0");
        Assert.Equal(["BA 4.0"], Transport.SentLines);
        Assert.Equal("2.7", vm.BandwidthText);          // no optimism

        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Transport.InjectLine("BAND 3.0");               // the radio's answer

        Assert.Equal("3.0", vm.BandwidthText);          // display = the answer
        Assert.Empty(errors);                            // FAULT-free, error-free
        var active = vm.BandwidthChoices.Where(c => c.IsActive).ToList();
        Assert.Equal("3.0", Assert.Single(active).Value);
    }

    [Fact]
    public void ModeCommand_SendsDocumentedForm_NoOptimisticHighlight()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("MODE CW");
        Transport.ClearSent();

        vm.SetModulationCommand.Execute("Usb");

        Assert.Equal(["MODE USB"], Transport.SentLines);
        Assert.True(vm.IsCwActive);                     // highlight has not moved
        Assert.False(vm.IsUsbActive);

        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");               // the trailing BAND rider
        Assert.True(vm.IsUsbActive);
        Assert.Equal("2.7", vm.BandwidthText);
    }

    [Fact]
    public void ReClickActiveModeOrBandwidthOrAgc_Guarded_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("AGC SLOW");
        Transport.ClearSent();

        vm.SetModulationCommand.Execute("Usb");
        vm.SetBandwidthCommand.Execute("2.7");
        vm.SetAgcCommand.Execute("SLOW");

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OutsideSsb_ControlsGreyedWithReason_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);

        vm.SetModulationCommand.Execute("Usb");
        vm.SetAgcCommand.Execute("MED");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ChannelGate_NotZeroOrUnconfirmed_ModeBwAgcGreyed_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("AGC SLOW");
        Transport.ClearSent();

        // Channel UNREPORTED counts as NOT 00 — conservative default (F6).
        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.IsBandwidthEnabled);
        vm.SetModulationCommand.Execute("Lsb");
        vm.SetBandwidthCommand.Execute("2.0");
        vm.SetAgcCommand.Execute("MED");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 05");               // confirmed non-00
        Assert.False(vm.AreControlsEnabled);
        Assert.Contains("CH 00", vm.DisabledReason);
        vm.SetModulationCommand.Execute("Lsb");
        vm.SetBandwidthCommand.Execute("2.0");
        vm.SetAgcCommand.Execute("MED");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 00");               // the gate opens
        Assert.True(vm.AreControlsEnabled);
        Assert.True(vm.IsBandwidthEnabled);
        vm.SetModulationCommand.Execute("Lsb");
        Assert.Equal(["MODE LSB"], Transport.SentLines);
    }

    [Fact]
    public void AgcCommand_SendsDocumentedForm_AnswerMovesDisplay()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        vm.SetAgcCommand.Execute("MED");
        Assert.Equal(["AG MED"], Transport.SentLines);
        Assert.Equal("—", vm.AgcText);                  // unconfirmed until answered

        Transport.InjectLine("AGC MED");
        Transport.InjectLine("RFG 100");                // rider line (probe R4)
        Assert.Equal("MED", vm.AgcText);
    }

    // ==== F8 / E6 operational controls (global state — NOT 00-gated) =======

    [Fact]
    public void OperationalControls_AreNotChannelZeroGated_SendOnNonZeroChannel()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("CHAN 05");                // confirmed NON-00
        Transport.ClearSent();

        // MODE/BW/AGC would be greyed here (F6), but squelch is global state.
        Assert.True(vm.AreOperationalControlsEnabled);
        vm.SetSquelchCommand.Execute("On");
        Assert.Equal(["SQ ON"], Transport.SentLines);
    }

    [Fact]
    public void SquelchButtonVisibility_FollowsConfirmedModulation_E6()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        // Unconfirmed modulation shows NO squelch/BFO row.
        Assert.False(vm.ShowAnalogSquelch);
        Assert.False(vm.ShowFmSquelch);
        Assert.False(vm.ShowBfo);

        Transport.InjectLine("MODE USB");
        Assert.True(vm.ShowAnalogSquelch);
        Assert.False(vm.ShowFmSquelch);
        Assert.False(vm.ShowBfo);

        Transport.InjectLine("MODE FM");
        Assert.False(vm.ShowAnalogSquelch);
        Assert.True(vm.ShowFmSquelch);

        Transport.InjectLine("MODE CW");
        Assert.True(vm.ShowAnalogSquelch);
        Assert.False(vm.ShowFmSquelch);
        Assert.True(vm.ShowBfo);                        // BFO is CW-only
    }

    [Fact]
    public void DigitalSquelchVisibility_FollowsConfirmedDv_E6()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");

        Assert.False(vm.ShowDigitalSquelch);            // DV unconfirmed
        Transport.InjectLine("DV ON");
        Assert.True(vm.ShowDigitalSquelch);
        Transport.InjectLine("DV OFF");
        Assert.False(vm.ShowDigitalSquelch);
    }

    // ---- CLONE ROUND 12 §9 B5: DV hides the ANALOG squelch row -------------

    /// <summary>The bench report: enabling DV left the analog squelch row on
    /// screen. DV confirmed ON hides it exactly as FM does — and only
    /// CONFIRMED-ON does, because unreported is never a default.
    /// <para>RE-BASED for clone round 12 P4 (the DV state sync): a changed
    /// <c>DV</c> line now UNCONFIRMS the modulation, because the D1 matrix says
    /// the radio silently moved it and nothing reported the move. The row's
    /// visibility follows the modulation, so the sequence below re-reads it
    /// where the real flow does — from the compensating <c>SH</c> block Core
    /// puts on the wire. The B5 CLAUSE itself is untouched: it is still
    /// confirmed-DV-ON, and only that, which takes the row away.</para></summary>
    [Fact]
    public void AnalogSquelchVisibility_AlsoHidesWhileDvIsConfirmedOn_B5()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Assert.True(vm.ShowAnalogSquelch);              // analog modulation, DV unreported

        Transport.InjectLine("DV ON");
        Assert.False(vm.ShowAnalogSquelch);             // the §9 B5 clause
        Assert.True(vm.ShowDigitalSquelch);             // its DV peer takes the row

        // The P4 round trip: Core's `SH` goes out at the prompt, and the block
        // it answers re-confirms the modulation (its DV/MODE lines arm nothing —
        // the in-flight suppression) and closes on its own prompt.
        Transport.InjectLine("SSB>");
        Assert.Contains("SH", Transport.SentLines);
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        Assert.False(vm.ShowAnalogSquelch);             // the clause, on a whole mirror
        Assert.True(vm.ShowDigitalSquelch);

        Transport.InjectLine("DV OFF");
        Transport.InjectLine("SSB>");                   // …and the same round trip back
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("DV OFF");
        Transport.InjectLine("SSB>");
        Assert.True(vm.ShowAnalogSquelch);              // confirmed OFF restores it
    }

    /// <summary>The other half of B5, RECORDED not changed: the squelch-LEVEL
    /// row has no visibility binding at all, so it survives BOTH the existing
    /// FM case and the new DV one. Written as a pin because "DV hides the
    /// squelch row" is exactly the instruction that would spread to SQ_L.
    /// <para>There is no ShowSquelchLevel property to assert — its absence IS
    /// the contract — so the pin asserts the absence structurally, which is
    /// also what makes it fail if someone adds one.</para></summary>
    [Fact]
    public void TheSquelchLevelRow_StaysVisibleInBothFmAndDv_B5Recorded()
    {
        Assert.Null(typeof(SignalViewModel).GetProperty("ShowSquelchLevel"));

        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        // FM — the existing precedent: the analog row goes, the level stays.
        Transport.InjectLine("MODE FM");
        Assert.False(vm.ShowAnalogSquelch);
        Transport.InjectLine("SQ_LEVEL MED");
        Assert.True(vm.IsSquelchLevelMedium);           // still rendering in FM

        // DV — the new case, same answer.
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("DV ON");
        Assert.False(vm.ShowAnalogSquelch);
        Assert.True(vm.IsSquelchLevelMedium);           // still rendering under DV
    }

    // ---- CLONE ROUND 12 §9 B4: the SQ_LEVEL report spellings ---------------

    /// <summary>The bench defect: only HIGH ever highlighted, because the
    /// compare read the mirror against the app's SET tokens. All three
    /// captured REPORT spellings now light exactly one button (r12-p2,
    /// 2026-08-19).</summary>
    [Theory]
    [InlineData("LOW", true, false, false)]
    [InlineData("MED", false, true, false)]
    [InlineData("HIGH", false, false, true)]
    public void SquelchLevelHighlight_ReadsTheReportSpellings_B4(
        string reported, bool low, bool medium, bool high)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");

        Transport.InjectLine("SQ_LEVEL " + reported);

        Assert.Equal(low, vm.IsSquelchLevelLow);
        Assert.Equal(medium, vm.IsSquelchLevelMedium);
        Assert.Equal(high, vm.IsSquelchLevelHigh);
    }

    /// <summary>The try-parse half (critic-12b F11): a payload outside the
    /// three captured spellings highlights NOTHING — including the app's own
    /// SET tokens, which the radio has never been observed to report back.
    /// The verbatim mirror still holds them; the DISPLAY refuses to guess.</summary>
    [Theory]
    [InlineData("MEDIUM")]
    [InlineData("LO")]
    [InlineData("NOT INSTALLED")]
    public void AnUnknownSquelchLevelSpelling_HighlightsNothing_B4(string reported)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");

        Transport.InjectLine("SQ_LEVEL HIGH");          // a real one first…
        Assert.True(vm.IsSquelchLevelHigh);

        Transport.InjectLine("SQ_LEVEL " + reported);   // …then the unknown one
        Assert.False(vm.IsSquelchLevelLow);
        Assert.False(vm.IsSquelchLevelMedium);
        Assert.False(vm.IsSquelchLevelHigh);            // the stale HIGH does not survive
    }

    /// <summary>The re-click guard moved to the same reader (§9 B4). Before,
    /// a confirmed MED compared false against the SET token "MEDIUM" and the
    /// re-click re-sent; now it guards, and a genuine change still goes.</summary>
    [Fact]
    public void SquelchLevelReClick_IsGuardedOnTheReportSpelling_B4()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SQ_LEVEL MED");           // confirmed MEDIUM
        Transport.ClearSent();

        vm.SetSquelchLevelCommand.Execute("MED");       // re-click — guarded
        Assert.Empty(Transport.SentLines);

        vm.SetSquelchLevelCommand.Execute("LO");        // a real change sends
        Assert.Equal(["SQ_L LO"], Transport.SentLines);
        Assert.True(vm.IsSquelchLevelMedium);           // no optimism — still MED
    }

    [Fact]
    public void Squelch_ProgrammaticWrite_SendsNothing_AndReClickGuarded()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SQUELCH ON");             // confirmed ON — no send
        Assert.True(vm.IsSquelchOn);
        Assert.Empty(Transport.SentLines);

        vm.SetSquelchCommand.Execute("On");             // re-click active — guarded
        Assert.Empty(Transport.SentLines);

        vm.SetSquelchCommand.Execute("Off");            // the real change sends
        Assert.Equal(["SQ OFF"], Transport.SentLines);
        Assert.True(vm.IsSquelchOn);                    // no optimism — still ON
    }

    [Fact]
    public void OperationalCommands_SendDocumentedWireForms()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE FM");                // so FMSQ shows
        Transport.ClearSent();

        vm.SetFmSquelchCommand.Execute("On");
        vm.SetDigitalSquelchCommand.Execute("On");
        vm.SetDigitalVoiceCommand.Execute("On");
        vm.SetCompressionCommand.Execute("On");
        vm.SetSquelchLevelCommand.Execute("HI");

        Assert.Equal(["FMSQ ON", "DGT_S ON", "DV ON", "COM ON", "SQ_L HIGH"], Transport.SentLines);
    }

    // ---- Round 8 (ED): the modem cluster moved to ModemViewModelTests ------
    // (cross-mode state, the power pattern — the wheel, wrap, K7 transform
    // and gate pins all live there now, plus the new ALE/HOP-prompt pins.)

    [Fact]
    public void Bfo_StepsFromConfirmedValue_ClampsAtEdge()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE CW");
        Transport.InjectLine("BFO +1000");              // confirmed BFO
        Assert.True(vm.CanStepBfo);
        Assert.Equal("+1000", vm.BfoText);
        Transport.ClearSent();

        vm.BfoUpCommand.Execute(null);
        Assert.Equal(["BF +2000"], Transport.SentLines);  // +1 kHz decade
        Transport.ClearSent();

        Transport.InjectLine("BFO +4000");              // at the ceiling
        vm.BfoUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);              // clamped — nothing sent
    }

    [Fact]
    public void OperationalControls_OutsideSsb_Greyed_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Assert.False(vm.AreOperationalControlsEnabled);
        vm.SetSquelchCommand.Execute("On");
        vm.SetDigitalVoiceCommand.Execute("On");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OperatorSquelchPress_DuringPendingFmCompensationCycle_NoHarmfulDoubleSend()
    {
        // Plan F5's named race pin: an operator SQ press while the Core's
        // FM-squelch OFF→ON compensation cycle is mid-flight must not
        // double-send a contradictory/duplicate SQ ON. The confirmed-state
        // re-click guard is what prevents it — during the cycle analog squelch
        // is still confirmed ON, so an operator ON-press returns early.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SQUELCH ON");             // arms the cycle's precondition
        Transport.InjectLine("MODE FM");                // FM change → cycle armed
        Transport.InjectLine("MODE USB");               // back to USB → Core sends SQ OFF

        // We are now in the pending/await window: the compensation's SQ OFF
        // has gone out, analog squelch still reports ON (no SQUELCH OFF yet).
        Assert.Contains("SQ OFF", Transport.SentLines);
        Assert.True(vm.IsSquelchOn);
        Transport.ClearSent();

        // Operator presses SQ ON mid-cycle — the guard blocks the harmful
        // duplicate/contradictory send.
        vm.SetSquelchCommand.Execute("On");
        Assert.Empty(Transport.SentLines);

        // And the compensation still completes normally: the SQUELCH OFF
        // report drives the Core's SQ ON restore, undisturbed by the press.
        Transport.InjectLine("SQUELCH OFF");
        Assert.Equal(["SQ ON"], Transport.SentLines);
    }

    // ---- CLONE ROUND 12 P4: the DV STATE SYNC, seen from the display -------

    /// <summary>
    /// THE DESYNC THE OWNER REPORTED, now unrepresentable. `DV ON` from FM
    /// SILENTLY forces USB (D1 matrix) and the echo carries NO `MODE` line at
    /// all, so the segment used to keep lighting FM until something unrelated
    /// happened to re-read. Core's trigger row now unconfirms the modulation
    /// the moment the DV change lands and re-reads at the next prompt, so the
    /// highlight goes: FM → NOTHING (honest, for the transit) → USB.
    ///
    /// <para>SYNC, NOT GATE — and no view-model change: this pane is
    /// mirror-driven already, and the pin is here to prove the mirror is what
    /// carries the repair.</para>
    /// </summary>
    [Fact]
    public void TogglingDvFromANonUsbModulation_EndsWithTheHighlightOnUsb_P4()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("DV OFF");
        Transport.InjectLine("SSB>");                   // the first-sight re-poll goes out
        Transport.InjectLine("MODE FM");                // …and the block that answers it
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("DV OFF");
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
        Assert.True(vm.IsFmActive);

        // The operator engages DV. The whole echo, verbatim from the capture —
        // and there is no MODE line in it.
        Transport.InjectLine("MODEM OFF");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("DGT_SQUELCH OFF");

        // THE TRANSIENT: nothing lit. Not the stale FM, and not an optimistic
        // USB either — the radio has not said yet.
        Assert.False(vm.IsFmActive);
        Assert.False(vm.IsUsbActive);
        Assert.False(vm.IsLsbActive);
        Assert.False(vm.IsAmeActive);
        Assert.False(vm.IsCwActive);
        Assert.Equal("—", vm.BandwidthText);            // unconfirmed reads as the em dash

        // The re-read goes out at the prompt and finds what the radio forced.
        Transport.InjectLine("SSB>");
        Assert.Contains("SH", Transport.SentLines);
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 3.0");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("SSB>");

        Assert.True(vm.IsUsbActive);
        Assert.False(vm.IsFmActive);
        Assert.Equal("3.0", vm.BandwidthText);
    }
}
