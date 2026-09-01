using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE settings pane (plan round-8 ALE row): the six ON/OFF toggles and
/// three numeric fields, all through AleSurface → the W1 AleController
/// builders. Pins the exact wire per command, confirmed-state rendering
/// (unreported → "—", no enum/int default leak), the constitution
/// programmatic-write-sends-nothing invariant, the mode gate (Ready +
/// confirmed ALE), the lazy once-per-session SH load, and the manual Refresh.
/// </summary>
public class AleSettingsViewModelTests : SessionTestBase
{
    private AleSettingsViewModel Vm() => new(new AleSurface(Radio), Session);

    /// <summary>Ready session confirmed in ALE, with the lazy first-load SH
    /// already drained off the sent list.</summary>
    private AleSettingsViewModel AleReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();
        return vm;
    }

    // ---- Lazy first load (plan Q4: once per session, lazily) ---------------

    [Fact]
    public void FirstConfirmedAle_LoadsSettingsOnce_Sh()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);            // nothing before ALE confirms

        Transport.InjectLine("ALE>");
        Assert.Equal(["SH"], Transport.SentLines);    // the SH block carries all nine

        // Leaving and re-entering ALE does NOT re-load (once per session).
        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("ALE>");
        Assert.Empty(Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void NewSession_LoadsAgain()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Assert.Equal(["SH"], Transport.SentLines);
        _ = vm;
    }

    // ---- ON/OFF confirmed rendering (no default leak) ----------------------

    [Fact]
    public void OnOff_UnreportedRendersDash_ThenConfirmedValue()
    {
        var vm = AleReadyVm();
        Assert.Equal("—", vm.AllCall);                // enum ordinal 0 is On — a leak would show "ON"

        Transport.InjectLine("ALL_CALL    ON  ");
        Assert.Equal("ON", vm.AllCall);
        Transport.InjectLine("ALL_CALL    OFF ");
        Assert.Equal("OFF", vm.AllCall);
        Assert.Empty(Transport.SentLines);            // programmatic writes send nothing
    }

    [Fact]
    public void AllSixOnOff_ConfirmedFromShBlockLines()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("ALL_CALL    ON  ");
        Transport.InjectLine("ANY_CALL    OFF ");
        Transport.InjectLine("AMD_DISPLAY ON  ");
        Transport.InjectLine("KEY_TO_CALL OFF ");
        Transport.InjectLine("LSTN        ON  ");
        Transport.InjectLine("RAD_SIL     OFF ");

        Assert.Equal("ON", vm.AllCall);
        Assert.Equal("OFF", vm.AnyCall);
        Assert.Equal("ON", vm.AmdDisplay);
        Assert.Equal("OFF", vm.KeyToCall);
        Assert.Equal("ON", vm.ListenBeforeTx);
        Assert.Equal("OFF", vm.RadioSilence);
        Assert.Empty(Transport.SentLines);
    }

    // ---- ON/OFF set: exact wire per builder --------------------------------

    [Theory]
    [InlineData("ON", "ALL_C ON")]
    [InlineData("OFF", "ALL_C OFF")]
    public void SetAllCall_SendsAllC(string value, string wire)
    {
        var vm = AleReadyVm();
        vm.SetAllCallCommand.Execute(value);
        Assert.Equal([wire], Transport.SentLines);
    }

    [Fact]
    public void EachOnOffToggle_SendsItsOwnToken()
    {
        var vm = AleReadyVm();
        vm.SetAnyCallCommand.Execute("ON");
        vm.SetAmdDisplayCommand.Execute("OFF");
        vm.SetKeyToCallCommand.Execute("ON");
        vm.SetListenBeforeTxCommand.Execute("OFF");
        vm.SetRadioSilenceCommand.Execute("ON");
        Assert.Equal(
            ["ANY_C ON", "AMD_D OFF", "KEY_T ON", "LSTN OFF", "RAD_S ON"],
            Transport.SentLines);
    }

    // ---- Numeric fields: exact wire + range validation ---------------------

    [Fact]
    public void SetMaxScanChannels_ValidInput_SendsMaxch()
    {
        var vm = AleReadyVm();
        vm.MaxChannelsInput = "20";
        vm.ApplyMaxScanChannelsCommand.Execute(null);
        Assert.Equal(["MAXCH 20"], Transport.SentLines);
        Assert.False(vm.HasInputError);
    }

    [Fact]
    public void SetLinkTimeout_ZeroIsValid_SendsTimeOu()
    {
        // 0 is measured valid (session-18), widening HELP's "1-60".
        var vm = AleReadyVm();
        vm.LinkTimeoutInput = "0";
        vm.ApplyLinkTimeoutCommand.Execute(null);
        Assert.Equal(["TIME_OU 0"], Transport.SentLines);
    }

    [Fact]
    public void SetTuneTime_ValidInput_SendsTune()
    {
        var vm = AleReadyVm();
        vm.TuneTimeInput = "3";
        vm.ApplyTuneTimeCommand.Execute(null);
        Assert.Equal(["TUNE 3"], Transport.SentLines);
    }

    [Theory]
    [InlineData("200")]   // above MAXCH max
    [InlineData("-1")]    // below min
    [InlineData("x")]     // not a number
    [InlineData("")]      // empty
    public void SetMaxScanChannels_BadInput_SendsNothing_WithError(string input)
    {
        var vm = AleReadyVm();
        vm.MaxChannelsInput = input;
        vm.ApplyMaxScanChannelsCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
        Assert.Contains("MAXCH", vm.InputError);
    }

    [Fact]
    public void SetTuneTime_ZeroRejected_SendsNothing()
    {
        // TUNE floor is 1 (unlike TIME_OU); 0 must not reach the wire.
        var vm = AleReadyVm();
        vm.TuneTimeInput = "0";
        vm.ApplyTuneTimeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
    }

    [Fact]
    public void Numeric_UnreportedRendersDash_ThenConfirmedValue()
    {
        var vm = AleReadyVm();
        Assert.Equal("—", vm.MaxScanChannelsText);

        Transport.InjectLine("MAXCH       20  ");
        Assert.Equal("20", vm.MaxScanChannelsText);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void LinkTimeout_ConfirmedDisplay_IsItsOwnSource_NotTuneTime()
    {
        // Pins TIME_OUT → LinkTimeoutText (not TuneTimeText): a source swap in
        // the VM must fail here — only the link-timeout value is reported, so
        // the tune-time display stays "—".
        var vm = AleReadyVm();
        Transport.InjectLine("TIME_OUT 007");
        Assert.Equal("7", vm.LinkTimeoutText);
        Assert.Equal("—", vm.TuneTimeText);
        Assert.Equal("—", vm.MaxScanChannelsText);
    }

    [Fact]
    public void TuneTime_ConfirmedDisplay_IsItsOwnSource_NotLinkTimeout()
    {
        // The reverse pin: TUNETIME → TuneTimeText only.
        var vm = AleReadyVm();
        Transport.InjectLine("TUNETIME 45");
        Assert.Equal("45", vm.TuneTimeText);
        Assert.Equal("—", vm.LinkTimeoutText);
        Assert.Equal("—", vm.MaxScanChannelsText);
    }

    // ---- Mode gate: Ready + confirmed ALE ----------------------------------

    [Fact]
    public void OutsideAle_ControlsDisabled_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);

        vm.SetAllCallCommand.Execute("ON");           // guarded in-body
        vm.MaxChannelsInput = "20";
        vm.ApplyMaxScanChannelsCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NotConnected_ControlsDisabled_NothingSent()
    {
        var vm = Vm();
        Assert.False(vm.AreControlsEnabled);
        vm.SetAnyCallCommand.Execute("OFF");
        Assert.Empty(Transport.SentLines);
    }

    // ---- Manual refresh: DELETED (UI tweaks round 10 §6) --------------------

    /// <summary>The ABSENCE pin for §6's deletion. A command that is simply
    /// gone leaves nothing to assert about, which is exactly how a deletion
    /// gets quietly undone — so the absence is asserted by NAME, over the
    /// generated command properties, with an anti-vacuity partner proving the
    /// same reflection really does see the commands that SURVIVED.</summary>
    [Fact]
    public void TheManualRefreshCommand_IsGone_AndTheSettingSettersAreNot()
    {
        var type = typeof(AleSettingsViewModel);

        Assert.Null(type.GetProperty("RefreshSettingsCommand"));

        // Anti-vacuity: the reader finds real commands on this very type.
        Assert.NotNull(type.GetProperty("SetAllCallCommand"));
        Assert.NotNull(type.GetProperty("ApplyMaxScanChannelsCommand"));
        Assert.NotNull(type.GetProperty("ApplyTuneTimeCommand"));
    }

    /// <summary>…and the READ itself is untouched: the lazy once-per-session
    /// SH still goes out on entering ALE. §6 deleted a BUTTON, not a read —
    /// the settings still arrive, they just arrive without being asked twice.</summary>
    [Fact]
    public void TheLazyOncePerSessionSh_StillGoesOut_WithoutTheButton()
    {
        // Driven by hand: AleReadyVm's trailing ClearSent would wipe the very
        // send under test.
        _ = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Assert.Contains("SH", Transport.SentLines);
    }
}
