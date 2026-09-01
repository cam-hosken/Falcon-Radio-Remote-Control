using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The TUNE button (spine chrome since the rejigger — S5/S6): RETU on press;
/// disabled (with reason) while a tune is in progress, disconnected, in a
/// CONFIRMED HOP mode, or while the mode is unconfirmed; ENABLED in confirmed
/// SSB and ALE. And the load-bearing rule — FAULT is a NORMAL outcome, the
/// button re-enables and recovery is pressing it again.
/// </summary>
public class CouplerViewModelTests : SessionTestBase
{
    private CouplerViewModel Vm() => new(new CouplerSurface(Radio), new ModeSurface(Radio), Session);

    [Fact]
    public void Tune_InConfirmedSsb_SendsRetu()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");                 // radio confirms SSB

        vm.TuneCommand.Execute(null);
        Assert.Equal(["RETU"], Transport.SentLines);
    }

    [Fact]
    public void Tune_InConfirmedAle_SendsRetu()
    {
        // S6 (owner: "retune is valid in ALE") — a test that dies if ALE is
        // refused again.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");                 // radio confirms ALE

        Assert.True(vm.CanTune);
        Assert.Equal("", vm.TuneDisabledReason);
        vm.TuneCommand.Execute(null);
        Assert.Equal(["RETU"], Transport.SentLines);
    }

    [Fact]
    public void InConfirmedHop_Disabled_NothingSent()
    {
        // S6 — a test that dies if HOP is ever accepted.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");                 // radio confirms HOP

        Assert.False(vm.CanTune);
        Assert.NotEqual("", vm.TuneDisabledReason);
        vm.TuneCommand.Execute(null);                 // in-body guard re-check
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ModeUnconfirmed_Disabled_NothingSent()
    {
        // Ready but no prompt seen yet: never act on a guessed mode (the
        // Confirmed display doctrine applied to an intent gate).
        var vm = Vm();
        ConnectReady();

        Assert.False(vm.CanTune);
        Assert.NotEqual("", vm.TuneDisabledReason);
        vm.TuneCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void WhileTuning_ButtonDisabledWithReason()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        vm.TuneCommand.Execute(null);
        Transport.ClearSent();

        Transport.InjectLine(" TUNING COUPLER ");     // verbatim async line
        Assert.True(vm.IsTuning);
        Assert.False(vm.CanTune);
        Assert.NotEqual("", vm.TuneDisabledReason);

        vm.TuneCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AfterFault_ButtonStaysEnabled_FaultIsANormalOutcome()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        vm.TuneCommand.Execute(null);
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.ClearSent();

        Transport.InjectLine("TUNE FAULT");           // this radio's real token

        Assert.False(vm.IsTuning);
        Assert.True(vm.CanTune);                      // recovery = press again
        vm.TuneCommand.Execute(null);
        Assert.Equal(["RETU"], Transport.SentLines);
    }

    [Fact]
    public void AfterComplete_ButtonEnabledAgain()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        vm.TuneCommand.Execute(null);
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE ");
        Transport.ClearSent();

        Assert.True(vm.CanTune);
        vm.TuneCommand.Execute(null);
        Assert.Equal(["RETU"], Transport.SentLines);
    }

    [Fact]
    public void NotConnected_Disabled_NothingSent()
    {
        var vm = Vm();      // session Disconnected

        Assert.False(vm.CanTune);
        Assert.NotEqual("", vm.TuneDisabledReason);
        vm.TuneCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ProgrammaticStateWrite_SendsNoCommand()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine("TUNE FAULT");

        Assert.Empty(Transport.SentLines);
    }
}
