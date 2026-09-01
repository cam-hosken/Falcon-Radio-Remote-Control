using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

public partial class AleSettingsPaneView : ContentView
{
    public AleSettingsPaneView()
    {
        InitializeComponent();
        // The ContentView root inherits the hosting page's ModeViewModel (so
        // the page's IsVisible="{Binding IsAleActive}" on this element keeps
        // resolving); only the inner Body rebinds to the ALE settings VM,
        // resolved from the app service provider (RadioSettingsPage's
        // ConsoleSection pattern; the VM is a DI singleton).
        // Loud-fail (fail-fast, matching the codebase): a null service
        // provider or a missing registration means the pane would silently
        // render inert — throw instead so the wiring bug is obvious.
        var vm = IPlatformApplication.Current?.Services?
            .GetService(typeof(AleSettingsViewModel)) as AleSettingsViewModel
            ?? throw new InvalidOperationException(
                "AleSettingsViewModel is not resolvable — the ALE settings pane cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        Body.BindingContext = vm;
    }
}
