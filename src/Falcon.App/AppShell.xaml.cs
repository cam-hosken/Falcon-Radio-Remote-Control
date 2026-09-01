using Falcon.App.Core.Services;
using Falcon.App.Views;

namespace Falcon.App;

public partial class AppShell : Shell
{
    /// <summary>The About page's RELATIVE route. Registered here — the app's
    /// first <see cref="Routing.RegisterRoute(string, Type)"/>, because About
    /// is the first page that is pushed rather than being a tab.</summary>
    public const string AboutRoute = "about";

    private readonly INavigator _navigator;

    public AppShell(INavigator navigator, SessionNavigationCoordinator coordinator)
    {
        InitializeComponent();
        _navigator = navigator;

        // F6: About is a ROUTED page, not a fifth tab (the tab bar stays four).
        Routing.RegisterRoute(AboutRoute, typeof(AboutPage));

        // F1 (connection-first): the app OPENS on Connection settings. The tab
        // ORDER is untouched — Operate is still the first tab; only the
        // initially selected one changes.
        CurrentItem = ConnectionSettingsTab;

        // F3 activation: the coordinator is a singleton nothing else resolves,
        // so it must be pulled in explicitly or it never subscribes. Resolving
        // it is NOT enough — a constructor dependency is built before this body
        // runs, so subscribing in ITS constructor would have it listening while
        // the line above was still deciding which tab to open on (audit round
        // 1: an edge in that window is consumed against a Shell that does not
        // exist yet). Activate() is therefore an explicit call, and it happens
        // AFTER CurrentItem is assigned. It dispatches nothing — it only
        // baselines the phase it reads right now.
        coordinator.Activate();
    }

    private async void OnAboutClicked(object? sender, EventArgs e)
        => await _navigator.GoToAbout();
}
