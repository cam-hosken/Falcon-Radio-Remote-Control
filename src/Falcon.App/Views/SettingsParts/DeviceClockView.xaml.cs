using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// The one shared radio-clock card (UI-tweaks round 4, AD / Contract K2),
/// used by BOTH Radio settings → Settings and Mode settings → HOP.
///
/// BindingContext is DeviceSettingsViewModel — the DI SINGLETON — resolved
/// here rather than inherited, so both placements read and write the same
/// clock state no matter which VM the host happens to be bound to (the
/// AleSettingsPaneView / ConsoleSection house pattern).
///
/// Load trigger (K2): the view owns it. On Loaded it calls the VM's
/// EnsureLoaded, which is already Ready-guarded, once-per-session and
/// idempotent — so the HOP-settings placement never sits at "—" waiting for
/// someone to visit Radio settings, and a second host (or a re-appearance)
/// re-queries nothing.
///
/// Loud-fail (house pattern): a null service provider or a missing
/// registration would render the card inert — throw instead so the wiring bug
/// is obvious.
/// </summary>
public partial class DeviceClockView : ContentView
{
    private readonly DeviceSettingsViewModel _device;

    public DeviceClockView()
    {
        InitializeComponent();
        _device = IPlatformApplication.Current?.Services?
            .GetService(typeof(DeviceSettingsViewModel)) as DeviceSettingsViewModel
            ?? throw new InvalidOperationException(
                "DeviceSettingsViewModel is not resolvable — the radio-clock card cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        BindingContext = _device;
        Loaded += (_, _) => _device.EnsureLoaded();
    }
}
