using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The §2.4 constitution, pinned: commands and state are SEPARATE binding
/// paths. A programmatic state write (an injected radio line) must never
/// send a command — the WinForms checkbox-inversion class. And the active
/// highlight must never move before the radio confirms — no optimistic
/// updates. A two-way-bound toggle or an optimistic highlight fails these.
/// </summary>
public class ModeViewModelTests : SessionTestBase
{
    private ModeViewModel Vm()
        => new(new ModeSurface(Radio), Session);

    [Fact]
    public void ProgrammaticStateWrite_SendsNoCommand()
    {
        var vm = Vm();
        ConnectReady();

        // Radio reports ALE (verbatim prompt line). The VM updates its
        // display state; NOTHING may go out on the wire.
        Transport.InjectLine("ALE>");

        Assert.True(vm.IsAleActive);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SelectMode_NoOptimisticHighlight_UntilRadioConfirms()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");   // radio reports SSB
        Assert.True(vm.IsSsbActive);

        vm.SelectModeCommand.Execute("Ale");

        // Command went out; the highlight has NOT moved (radio-authoritative).
        Assert.Equal(1, Transport.CountSent("ALE"));
        Assert.False(vm.IsAleActive);
        Assert.True(vm.IsSsbActive);
        Assert.True(vm.IsSwitching);

        // The radio's ALE prompt confirms — now the highlight moves.
        Transport.InjectLine("ALE>");
        Assert.True(vm.IsAleActive);
        Assert.False(vm.IsSsbActive);
        Assert.False(vm.IsSwitching);
    }

    [Fact]
    public void NoModeReportedYet_NoSegmentActive()
    {
        var vm = Vm();
        ConnectReady();

        // Unconfirmed is "—"/nothing — never a default segment (enum
        // ordinal 0 is Ssb; an enum-default leak would light SSB here).
        Assert.False(vm.IsSsbActive);
        Assert.False(vm.IsAleActive);
        Assert.False(vm.IsHopActive);
    }

    [Fact]
    public void ReClickActiveSegment_Guarded_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.SelectModeCommand.Execute("Ssb");

        Assert.Empty(Transport.SentLines);
        Assert.False(vm.IsSwitching);
    }

    [Fact]
    public void WhileSwitchPending_FurtherSelects_Ignored()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.SelectModeCommand.Execute("Ale");
        vm.SelectModeCommand.Execute("Hop");     // busy — ignored
        vm.SelectModeCommand.Execute("Ale");     // busy — ignored

        Assert.Equal(1, Transport.CountSent("ALE"));
        Assert.Equal(0, Transport.CountSent("HO"));
    }

    [Fact]
    public void NotReady_CommandDisabled_NothingSent()
    {
        var vm = Vm();   // session Disconnected

        Assert.False(vm.SelectModeCommand.CanExecute("Ale"));
        vm.SelectModeCommand.Execute("Ale");
        Assert.Equal(0, Transport.CountSent("ALE"));
    }
}
