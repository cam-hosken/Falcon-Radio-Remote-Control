using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// HOP settings pane — the net-programming editor (UI-tweaks round 3, X1).
/// The ContentView root keeps the hosting page's INHERITED ModeViewModel (so
/// ModeSettingsPage's IsVisible="{Binding IsHopActive}" on this element keeps
/// resolving); only the inner Body rebinds to the editor's own VM, resolved
/// from the app service provider (the AleSettingsPaneView pattern; the VM is
/// a DI singleton).
///
/// Loud-fail (fail-fast, matching the codebase): a null service provider or a
/// missing registration would render the pane inert — throw instead so the
/// wiring bug is obvious.
/// </summary>
public partial class HopSettingsPaneView : ContentView
{
    public HopSettingsPaneView()
    {
        InitializeComponent();
        Body.BindingContext = IPlatformApplication.Current?.Services?
            .GetService(typeof(HopSettingsViewModel)) as HopSettingsViewModel
            ?? throw new InvalidOperationException(
                "HopSettingsViewModel is not resolvable — the HOP settings pane cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
    }
}
