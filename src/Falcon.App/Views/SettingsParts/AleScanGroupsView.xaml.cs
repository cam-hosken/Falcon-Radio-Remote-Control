using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// The Scan channel groups card (plan-ale-programming.md §4.5) — Mode settings
/// → ALE. Same self-binding shape as AleProgrammingView: the DI SINGLETON is
/// resolved here (loud-fail) because the hosting pane's Body is bound to
/// AleSettingsViewModel, and Loaded runs the card's edge-detected initial-sight
/// read of the PICKED group. No Refresh button — every spin re-reads.
/// </summary>
public partial class AleScanGroupsView : ContentView
{
    private readonly AleScanGroupsViewModel _groups;

    public AleScanGroupsView()
    {
        InitializeComponent();
        _groups = IPlatformApplication.Current?.Services?
            .GetService(typeof(AleScanGroupsViewModel)) as AleScanGroupsViewModel
            ?? throw new InvalidOperationException(
                "AleScanGroupsViewModel is not resolvable — the scan channel groups card cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        BindingContext = _groups;
        Loaded += (_, _) => _groups.EnsureLoaded();
    }
}
