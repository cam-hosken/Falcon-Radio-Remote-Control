using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// Radio settings sub-tab state (GUI rejigger E4): pure app-side view
/// state — Settings is the default tab, the commands flip between the
/// Settings and Console sub-tabs, and nothing ever reaches the wire (the
/// VM has no radio access at all by construction).
/// </summary>
public class RadioSettingsViewModelTests
{
    [Fact]
    public void DefaultsToTheSettingsTab()
    {
        var vm = new RadioSettingsViewModel();
        Assert.False(vm.IsConsoleOpen);
    }

    [Fact]
    public void Commands_FlipBetweenTheTabs()
    {
        var vm = new RadioSettingsViewModel();

        vm.OpenConsoleCommand.Execute(null);
        Assert.True(vm.IsConsoleOpen);

        vm.OpenSettingsCommand.Execute(null);
        Assert.False(vm.IsConsoleOpen);
    }
}
