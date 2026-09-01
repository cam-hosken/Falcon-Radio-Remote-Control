using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// The Address programming card (plan-ale-programming.md §4.5) — Mode settings
/// → ALE.
///
/// BindingContext is AleProgrammingViewModel — the DI SINGLETON — resolved here
/// rather than inherited (the ModemPresetsView / DeviceClockView house
/// pattern), because the hosting pane's Body is bound to AleSettingsViewModel.
///
/// Load trigger: the view owns it (the K2 clock pattern). On Loaded it calls
/// EnsureLoaded, which under the round-9 read doctrine runs the card's
/// edge-detected INITIAL-SIGHT read — whichever comes first, the card
/// appearing or the radio confirming ALE, reads the station book once, and the
/// reconnect after a drop reads again. There is no Refresh button.
///
/// Loud-fail (house pattern): a missing registration renders the card inert —
/// throw instead so the wiring bug is obvious.
/// </summary>
public partial class AleProgrammingView : ContentView
{
    private readonly AleProgrammingViewModel _programming;

    public AleProgrammingView()
    {
        InitializeComponent();
        _programming = IPlatformApplication.Current?.Services?
            .GetService(typeof(AleProgrammingViewModel)) as AleProgrammingViewModel
            ?? throw new InvalidOperationException(
                "AleProgrammingViewModel is not resolvable — the address programming card cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        BindingContext = _programming;
        Loaded += (_, _) => _programming.EnsureLoaded();
    }
}
