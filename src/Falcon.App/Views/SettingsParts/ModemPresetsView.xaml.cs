using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// The Modem presets card (round 8 EE) — Radio settings → Settings tab.
///
/// BindingContext is ModemPresetsViewModel — the DI SINGLETON — resolved here
/// rather than inherited (the DeviceClockView / ConsoleSection house
/// pattern), because the host tab is bound to DeviceSettingsViewModel.
///
/// Load trigger: the view owns it (the K2 clock pattern). On Loaded it calls
/// EnsureLoaded, which under the ROUND-9 read doctrine is an EDITOR LANDING —
/// Ready-guarded and FRESH every time, not a once-per-session latch — so the
/// card populates on first sight and re-reads whenever it comes back into
/// view. There is no Refresh button: every landing re-reads.
///
/// Loud-fail (house pattern): a missing registration renders the card inert —
/// throw instead so the wiring bug is obvious.
/// </summary>
public partial class ModemPresetsView : ContentView
{
    private readonly ModemPresetsViewModel _presets;

    public ModemPresetsView()
    {
        InitializeComponent();
        _presets = IPlatformApplication.Current?.Services?
            .GetService(typeof(ModemPresetsViewModel)) as ModemPresetsViewModel
            ?? throw new InvalidOperationException(
                "ModemPresetsViewModel is not resolvable — the modem-presets card cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        BindingContext = _presets;
        Loaded += (_, _) => _presets.EnsureLoaded();
    }
}
