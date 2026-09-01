using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The SSB mode-settings pane VM under the §2.4 constitution: settings render
/// one-way from the CONFIRMED mirror (no optimism); every choice sends the
/// documented wire form; a programmatic mirror write sends nothing; re-click
/// of the active choice is guarded; the modulation-visibility matrix greys the
/// FM group outside FM and CW offset outside CW; and the settings are NOT
/// channel-00-gated (gate = Ready + confirmed SSB only). Settings with no
/// captured answer shape (FORCE_W, RWAS_KEY) send but never highlight.
/// (Keyline is DEFERRED this wave — the Core scope guard forbids the
/// SetKeyline builder in app-layer source; see the report.)
///
/// Round-3 Y1/V7: the pane gained a full Refresh + lazy first load over the
/// old-app-derived query set, and preamp / internal coupler / 1 kW PA gained
/// PROVISIONAL mirrors, so they now highlight — off unconfirmed spellings,
/// which the last test here pins deliberately.
/// </summary>
public class SsbSettingsViewModelTests : SessionTestBase
{
    private SsbSettingsViewModel Vm() => new(new SsbSurface(Radio), Session);

    private ChoiceItem Choice(IEnumerable<ChoiceItem> list, string value)
        => list.Single(c => c.Value == value);

    private void EnterSsb(string? modulation = null)
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        if (modulation is not null) Transport.InjectLine($"MODE {modulation}");
        Transport.ClearSent();
    }

    [Fact]
    public void Disabled_UntilSsbConfirmed()
    {
        var vm = Vm();
        ConnectReady();
        Assert.False(vm.AreSettingsEnabled);
        Assert.NotEqual("", vm.SettingsDisabledReason);

        Transport.InjectLine("SSB>");
        Assert.True(vm.AreSettingsEnabled);
    }

    [Fact]
    public void Settings_AreNotChannelZeroGated()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine("CHAN 07");                // confirmed NON-00
        Transport.ClearSent();

        Assert.True(vm.AreSettingsEnabled);             // global — not 00-gated
        Choice(vm.BeepChoices, "On").SelectCommand.Execute(null);
        Assert.Equal(["BEEP ON"], Transport.SentLines);
    }

    // ---- CLONE ROUND 12 §9 C4: the modulation gates are DELETED ------------
    //
    // These two tests asserted the OPPOSITE from Wave 2 to round 11: the FM
    // group greyed outside FM and CW offset greyed outside CW, in both the VM
    // and the markup. The r12-p2 probe disproved the premise — at a confirmed
    // USB with the modulation HELD CONSTANT (protocol.md's own methodology
    // warning), FMSQ_T NOISE / FMTONE / FMDE 8.0 / CWOFF 1000 were ALL echoed
    // as accepted. The gates were UI policy inventing a radio constraint, the
    // second such gate this app has had to retire (the DGT_S precedent).

    [Fact]
    public void TheFmTrio_SendsOutsideFm_C4()
    {
        var vm = Vm();
        EnterSsb("USB");                                // NOT FM

        Choice(vm.FmSquelchTypeChoices, "Noise").SelectCommand.Execute(null);
        Choice(vm.FmToneChoices, "On").SelectCommand.Execute(null);
        Choice(vm.FmDeviationChoices, "8.0").SelectCommand.Execute(null);

        // Exactly the three commands the capture sent, and in FM the behaviour
        // is unchanged — there is no longer any per-modulation branch at all.
        Assert.Equal(["FMSQ_T NOISE", "FMTONE ON", "FMDE 8.0"], Transport.SentLines);
    }

    [Fact]
    public void CwOffset_SendsOutsideCw_C4()
    {
        var vm = Vm();
        EnterSsb("USB");                                // NOT CW

        Choice(vm.CwOffsetChoices, "1000").SelectCommand.Execute(null);

        Assert.Equal(["CWOFF 1000"], Transport.SentLines);
    }

    /// <summary>Both gate LAYERS came out, not just the command bodies. The
    /// VM half is pinned by the two sends above; this is the other half —
    /// the enabled properties are GONE, so no markup binding can resurrect the
    /// grey, and the XAML containers that read them are pinned separately by
    /// the markup guard.</summary>
    [Fact]
    public void NeitherModulationGateProperty_SurvivesOnTheViewModel_C4()
    {
        Assert.Null(typeof(SsbSettingsViewModel).GetProperty("IsFmGroupEnabled"));
        Assert.Null(typeof(SsbSettingsViewModel).GetProperty("IsCwOffsetEnabled"));
        Assert.Null(typeof(SsbSettingsViewModel).GetProperty("FmGroupDisabledReason"));
        Assert.Null(typeof(SsbSettingsViewModel).GetProperty("CwOffsetDisabledReason"));

        // Anti-vacuity: the PANE gate is untouched and still exists, so a
        // reflection helper that simply found nothing cannot pass this.
        Assert.NotNull(typeof(SsbSettingsViewModel)
            .GetProperty(nameof(SsbSettingsViewModel.AreSettingsEnabled)));
    }

    /// <summary>The PANE gate still holds all four. C4 removed a MODULATION
    /// gate, not the Ready+SSB one — a change that took both would have made
    /// every test above pass while letting the four commands out of a
    /// disconnected session.</summary>
    [Fact]
    public void TheFourUngatedSettings_StillObeyThePaneGate_C4()
    {
        var vm = Vm();
        ConnectReady();                                 // Ready, but SSB NOT confirmed

        Choice(vm.FmSquelchTypeChoices, "Noise").SelectCommand.Execute(null);
        Choice(vm.FmToneChoices, "On").SelectCommand.Execute(null);
        Choice(vm.FmDeviationChoices, "8.0").SelectCommand.Execute(null);
        Choice(vm.CwOffsetChoices, "1000").SelectCommand.Execute(null);

        Assert.False(vm.AreSettingsEnabled);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void MirroredControls_SendDocumentedWireForms()
    {
        var vm = Vm();
        EnterSsb("FM");                                 // FM so the FM group is live

        // H1: choice Values are title-case display text; the WIRE forms
        // below must stay byte-identical.
        Choice(vm.FmSquelchTypeChoices, "Tone").SelectCommand.Execute(null);
        Choice(vm.FmDeviationChoices, "8.0").SelectCommand.Execute(null);
        Choice(vm.AvsChoices, "On").SelectCommand.Execute(null);
        Choice(vm.AntennaChoices, "Auto").SelectCommand.Execute(null);
        Choice(vm.RetransmitChoices, "Enable").SelectCommand.Execute(null);
        Choice(vm.PrePostFilterChoices, "Enable").SelectCommand.Execute(null);
        Choice(vm.PrePostScanChoices, "Fast").SelectCommand.Execute(null);
        Choice(vm.RwasChoices, "Enable").SelectCommand.Execute(null);
        Choice(vm.UnkeyMaskChoices, "Disable").SelectCommand.Execute(null);

        Assert.Equal(
            ["FMSQ_T TONE", "FMDE 8.0", "AVS ON", "ANTENNA AUTO", "RETR ENA",
             "PREPOST FILTER ENABLE", "PREPOST SCAN FAST", "RWAS ENA", "UNKEY_M DIS"],
            Transport.SentLines);
    }

    [Fact]
    public void UnmirroredControls_Send_ButNeverHighlight()
    {
        var vm = Vm();
        EnterSsb("USB");

        // Nothing has been REPORTED, so nothing is active — including the
        // round-3 provisional trio, which highlights only off an answer.
        // CLONE ROUND 12 §9 C3 re-pin: FORCE_W is no longer "no read-back at
        // all" — P1 gave it a bounded session latch. It still highlights
        // NOTHING here, because the radio has reported nothing; the difference
        // is that Enable CAN now light, which the C3 tests below pin.
        Assert.All(vm.PreampChoices, c => Assert.False(c.IsActive));
        Assert.All(vm.ForceWakeupChoices, c => Assert.False(c.IsActive));
        Choice(vm.PreampChoices, "Enable").SelectCommand.Execute(null);
        Choice(vm.OneKilowattChoices, "Yes").SelectCommand.Execute(null);
        Choice(vm.ForceWakeupChoices, "Enable").SelectCommand.Execute(null);
        Assert.Equal(["PRE ENABLE", "KWAT YES", "FORCE_W ENA"], Transport.SentLines);
    }

    // ---- CLONE ROUND 12 §9 C3: FORCE_W's asymmetric highlight --------------

    /// <summary>ENABLE highlights on the P1 mirror. The parser used to discard
    /// <c>FORCE WAKEUP ENABLED</c> deliberately (DIS is silent and a bare
    /// query answers nothing, so a naive mirror could latch stale); P1 turned
    /// that into a bounded session latch, and this is the display half.</summary>
    [Fact]
    public void ForceWakeupEnable_HighlightsOnTheConfirmedMirror_C3()
    {
        var vm = Vm();
        EnterSsb("USB");
        Assert.False(Choice(vm.ForceWakeupChoices, "Enable").IsActive);

        Transport.InjectLine("FORCE WAKEUP ENABLED");

        Assert.True(Choice(vm.ForceWakeupChoices, "Enable").IsActive);
    }

    /// <summary>DISABLE NEVER highlights — the recorded asymmetry. The radio
    /// says nothing when force-wakeup is disabled, so "not confirmed enabled"
    /// is not the same fact as "confirmed disabled", and lighting Disable on
    /// the absence of a report would be the app claiming state the radio has
    /// never reported. Pinned in all three positions the latch can be in.</summary>
    [Fact]
    public void ForceWakeupDisable_NeverHighlights_InAnyState_C3()
    {
        var vm = Vm();
        EnterSsb("USB");

        // 1. Never reported.
        Assert.False(Choice(vm.ForceWakeupChoices, "Disable").IsActive);

        // 2. Confirmed ENABLED — Enable lights, Disable still does not.
        Transport.InjectLine("FORCE WAKEUP ENABLED");
        Assert.True(Choice(vm.ForceWakeupChoices, "Enable").IsActive);
        Assert.False(Choice(vm.ForceWakeupChoices, "Disable").IsActive);

        // 3. After the app SENDS a disable: P1 unconfirms the mirror, so
        //    Enable goes dark — and Disable STILL does not light, because a
        //    silent direction can never be confirmed.
        Transport.ClearSent();
        Choice(vm.ForceWakeupChoices, "Disable").SelectCommand.Execute(null);
        Assert.Equal(["FORCE_W DIS"], Transport.SentLines);
        Assert.False(Choice(vm.ForceWakeupChoices, "Enable").IsActive);
        Assert.False(Choice(vm.ForceWakeupChoices, "Disable").IsActive);
    }

    [Fact]
    public void ProgrammaticWrite_SendsNothing_AndReClickGuarded()
    {
        var vm = Vm();
        EnterSsb("USB");

        Transport.InjectLine("BEEP ON");                // confirmed ON
        Assert.True(Choice(vm.BeepChoices, "On").IsActive);
        Assert.Empty(Transport.SentLines);              // mirror write sent nothing

        Choice(vm.BeepChoices, "On").SelectCommand.Execute(null);   // re-click active
        Assert.Empty(Transport.SentLines);

        Choice(vm.BeepChoices, "Off").SelectCommand.Execute(null);
        Assert.Equal(["BEEP OFF"], Transport.SentLines);
    }

    [Fact]
    public void RfGain_InputApply_SendsWireForm_DisplayFollowsOnlyTheAnswer()
    {
        // I1: confirmed DISPLAY left, app-side Entry + Set right. Typing
        // sends nothing; Set sends RF <n>; the display moves on RFG only.
        var vm = Vm();
        EnterSsb("USB");
        Assert.Equal("—", vm.RfGainText);               // unreported = dash

        vm.RfGainInput = "60";
        Assert.Empty(Transport.SentLines);              // typing sends nothing
        vm.ApplyRfGainCommand.Execute(null);
        Assert.Equal(["RF 60"], Transport.SentLines);
        Assert.Equal("—", vm.RfGainText);               // no optimism
        Assert.False(vm.HasInputError);

        Transport.InjectLine("RFG 60");
        Assert.Equal("60", vm.RfGainText);
    }

    [Fact]
    public void RfGain_InputBuffer_IsNeverWrittenByTheRadio()
    {
        // I: the Entry is an app-side buffer the radio NEVER writes — a
        // report landing mid-edit must not clobber the operator's text.
        var vm = Vm();
        EnterSsb("USB");

        vm.RfGainInput = "25";
        Transport.InjectLine("RFG 80");
        Assert.Equal("25", vm.RfGainInput);
        Assert.Equal("80", vm.RfGainText);              // display follows the radio
    }

    [Fact]
    public void RfGain_InvalidInput_NotedAndNothingSent()
    {
        var vm = Vm();
        EnterSsb("USB");

        vm.RfGainInput = "150";
        vm.ApplyRfGainCommand.Execute(null);
        Assert.True(vm.HasInputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void RwasKey_InputApply_SendsOnDemand_WriteOnly()
    {
        // I2: write-only — no confirmed display to move; Set sends the
        // two-digit wire form; typing sends nothing.
        var vm = Vm();
        EnterSsb("USB");

        vm.RwasKeyInput = "3";
        Assert.Empty(Transport.SentLines);              // typing sends nothing
        vm.ApplyRwasKeyCommand.Execute(null);
        Assert.Equal(["RWAS_KEY 03"], Transport.SentLines);
        Transport.ClearSent();

        vm.RwasKeyInput = "123";                        // out of range 0-99
        vm.ApplyRwasKeyCommand.Execute(null);
        Assert.True(vm.HasInputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Avs_OddRadioValue_ShowsOnDisplayElement_NoButtonHighlight()
    {
        // E2/H2: AVS is tri-state on the wire; NOT INSTALLED renders as
        // "Not installed" (radio's wording, our case) on the display element
        // with both buttons un-highlighted.
        var vm = Vm();
        EnterSsb("USB");
        Assert.False(vm.HasAvsOddValue);

        Transport.InjectLine("AVS NOT INSTALLED");
        Assert.True(vm.HasAvsOddValue);
        Assert.Equal("Not installed", vm.AvsOddText);
        Assert.All(vm.AvsChoices, c => Assert.False(c.IsActive));

        Transport.InjectLine("AVS ON");                 // a normal report again
        Assert.False(vm.HasAvsOddValue);
        Assert.True(Choice(vm.AvsChoices, "On").IsActive);
    }

    [Fact]
    public void OutsideSsb_AllControlsGreyed_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Assert.False(vm.AreSettingsEnabled);
        Choice(vm.BeepChoices, "On").SelectCommand.Execute(null);
        Choice(vm.AntennaChoices, "Auto").SelectCommand.Execute(null);
        vm.RfGainInput = "50";
        vm.ApplyRfGainCommand.Execute(null);
        vm.RwasKeyInput = "3";
        vm.ApplyRwasKeyCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Round-3 Y1: full Refresh + lazy first load (V7 query set) ---------
    // The pane no longer depends on the connect-SH subset. Every query is
    // old-app-derived and PROVISIONAL (docs/protocol.md) — these pins are
    // where a bench correction lands on the app side.

    /// <summary>The exact query set, in the order the surface sends it. This
    /// is the mutation-catching pin: dropping or reordering a query fails
    /// here, and a query that grew an argument (a SET in query clothing)
    /// fails the equality too.</summary>
    /// <summary>The pane's per-setting read set. SEVENTEEN reads until clone
    /// round 12 §9 B3 added the EIGHTEENTH: bare <c>COM</c> answers
    /// <c>COMPRESS ON</c> (captured 2026-08-18, bench/transcripts/r12-p2-*
    /// step c), so compression finally has a read path — before that capture
    /// the mirror latched the app's own last echo for the whole session.
    /// <para><c>FORCE_W</c> and <c>RWAS_KEY</c> are still absent, and still for
    /// their own reasons: a bare <c>FORCE_W</c> answers nothing and a bare
    /// <c>RWAS_KEY</c> answers <c>** ERROR **</c>.</para></summary>
    private static readonly string[] ExpectedQuerySet =
    [
        "FMSQ_T", "FMTONE", "FMDE", "CWOFF", "AVS", "PRE", "RF",
        "ANTENNA", "INTCOUPLER", "KWAT", "RETR", "PREPOST FILTER",
        "PREPOST RXANTENNA", "PREPOST SCAN", "RWAS", "UNKEY_M", "BEEP",
        "COM",
    ];

    [Fact]
    public void LazyFirstLoad_SendsTheQuerySetOncePerSession()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);          // nothing before SSB is confirmed

        Transport.InjectLine("SSB>");
        Assert.Equal(ExpectedQuerySet, Transport.SentLines);

        // A second confirmation (re-entering the mode, another surface event)
        // must NOT re-query: the mirror IS the cache.
        //
        // The lines that DO go out are not this pane's, they are Core's
        // trigger table's: clone round 12 §9 B3 queues a single `COM` after a
        // confirmed MODE change (a modulation change can move compression and
        // nothing reports it), and round-12 P4 adds the DV state-sync row —
        // a changed `MODE` line silently auto-suspends or auto-restores digital
        // voice (probe R4), so one `SH` re-reads the block. Asserted EXACTLY —
        // those two and nothing else — so the pane's own re-query still fails
        // this pin if it comes back.
        Transport.ClearSent();
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);

        Assert.NotNull(vm);
    }

    [Fact]
    public void LazyFirstLoad_RunsAgainOnANewSession()
    {
        // A dropped session may be a DIFFERENT radio — the next Ready+SSB
        // session must read fresh rather than trust the last one's answers.
        var vm = Vm();
        EnterSsb();                                  // first load ran, sent cleared

        Radio.Disconnect();
        ConnectReady();
        Transport.ClearSent();
        Transport.InjectLine("SSB>");

        Assert.Equal(ExpectedQuerySet, Transport.SentLines);
        Assert.NotNull(vm);
    }

    [Fact]
    public void RefreshSettings_SendsTheWholeQuerySet_AndNothingElse()
    {
        var vm = Vm();
        EnterSsb("USB");

        vm.RefreshSettingsCommand.Execute(null);
        Assert.Equal(ExpectedQuerySet, Transport.SentLines);
    }

    [Fact]
    public void RefreshSettings_MovesNoDisplay_UntilTheAnswersLand()
    {
        // Constitution: a query is a request, not a fact. Nothing on the pane
        // may move because a Refresh was sent.
        var vm = Vm();
        EnterSsb("USB");

        Assert.All(vm.PreampChoices, c => Assert.False(c.IsActive));
        Assert.Equal("—", vm.RfGainText);

        vm.RefreshSettingsCommand.Execute(null);
        Assert.All(vm.PreampChoices, c => Assert.False(c.IsActive));
        Assert.Equal("—", vm.RfGainText);

        // Only the ANSWERS move the display.
        Transport.InjectLine("PREAMP ENABLED");
        Transport.InjectLine("RFG 42");
        Assert.True(Choice(vm.PreampChoices, "Enable").IsActive);
        Assert.Equal("42", vm.RfGainText);
    }

    [Fact]
    public void RefreshSettings_GatedOnReadyAndConfirmedSsb()
    {
        var vm = Vm();
        ConnectReady();
        Assert.False(vm.RefreshSettingsCommand.CanExecute(null));

        // Execute ignores CanExecute — the body must re-check and send nothing.
        vm.RefreshSettingsCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("ALE>");
        Transport.ClearSent();
        Assert.False(vm.RefreshSettingsCommand.CanExecute(null));
        vm.RefreshSettingsCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SSB>");
        Assert.True(vm.RefreshSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void ProvisionalMirrors_HighlightOnlyOnTheReportedSpelling()
    {
        // Round-3 V7: preamp / internal coupler / 1 kW PA now highlight — off
        // PROVISIONAL, bench-unconfirmed spellings. An answer in a DIFFERENT
        // spelling must light nothing rather than guess (and the value is
        // still mirrored, so the bench capture is not lost).
        var vm = Vm();
        EnterSsb("USB");

        Transport.InjectLine("PREAMP BYPASSED");
        Transport.InjectLine("INTCOUPLER ENABLED");
        Transport.InjectLine("KWATT NO");
        Assert.True(Choice(vm.PreampChoices, "Bypass").IsActive);
        Assert.False(Choice(vm.PreampChoices, "Enable").IsActive);
        Assert.True(Choice(vm.InternalCouplerChoices, "Enable").IsActive);
        Assert.True(Choice(vm.OneKilowattChoices, "No").IsActive);

        // The set spelling ("BYPASS", what we SEND) is not the report
        // spelling — an echo in that form must not be treated as a report.
        Transport.InjectLine("PREAMP BYPASS");
        Assert.All(vm.PreampChoices, c => Assert.False(c.IsActive));
    }
}
