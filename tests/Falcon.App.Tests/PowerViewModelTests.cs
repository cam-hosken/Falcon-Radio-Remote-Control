using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

public class PowerViewModelTests : SessionTestBase
{
    private PowerViewModel Vm() => new(new PowerSurface(Radio), Session);

    [Fact]
    public void ProgrammaticStateWrite_SendsNoCommand()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("POWER low");   // verbatim SH-block line

        Assert.True(vm.IsLowActive);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SetPower_NoOptimisticHighlight_UntilAnswerArrives()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("POWER low");
        Transport.ClearSent();

        vm.SetPowerCommand.Execute("Medium");

        Assert.Equal(1, Transport.CountSent("POW MED"));
        Assert.True(vm.IsLowActive);          // highlight unchanged —
        Assert.False(vm.IsMedActive);         // the POW answer is the read-back

        Transport.InjectLine(" POWER med");   // captured async POWER shape
        Assert.True(vm.IsMedActive);
        Assert.False(vm.IsLowActive);
    }

    [Fact]
    public void NoPowerReportedYet_NoLevelActive()
    {
        var vm = Vm();
        ConnectReady();

        // Enum ordinal 0 is Low — an enum-default leak would light LOW here.
        Assert.False(vm.IsLowActive);
        Assert.False(vm.IsMedActive);
        Assert.False(vm.IsHiActive);
    }

    [Fact]
    public void ReClickActiveLevel_Guarded_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("POWER low");
        Transport.ClearSent();

        vm.SetPowerCommand.Execute("Low");

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Cutback_ShowsWarning_UntilRestored()
    {
        var vm = Vm();
        ConnectReady();
        Assert.False(vm.ShowCutback);

        Transport.InjectLine("POWER CUTBACK   ");
        Assert.True(vm.ShowCutback);

        Transport.InjectLine("POWER RESTORED   ");
        Assert.False(vm.ShowCutback);

        // No writes happened from any of it.
        Assert.Empty(Transport.SentLines);
    }
}
