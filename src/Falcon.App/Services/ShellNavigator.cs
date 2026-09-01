using Falcon.App.Core.Services;

namespace Falcon.App.Services;

/// <summary>
/// The MAUI half of the <see cref="INavigator"/> seam (clone round 12 §6 F3),
/// on the <see cref="ConfirmationPrompt"/> template: the interface is
/// MAUI-free in Falcon.App.Core, this implementation is the only place that
/// knows Shell exists.
///
/// <para><b>ABSOLUTE routes for the two tabs.</b> <c>"//operate"</c> and
/// <c>"//settings"</c> are absolute Shell navigations: a phase edge must land
/// on the target tab even when the About page is pushed over it, rather than
/// leaving the operator in an undefined "which page is on top" state.
/// <c>GoToAbout</c> is the deliberate opposite: a relative push, so the
/// platform back gesture (Windows shell back arrow, Android hardware back)
/// returns where it came from.</para>
///
/// <para><b>The explicit pop is a BELT, and its status is honest.</b> §6 F3's
/// premise is that absolute navigation ALONE clears a pushed page. That could
/// not be verified on the dev box (see docs/ui.md's outstanding checks: the
/// only gesture that produces a phase edge lives on the very page About
/// covers, and without a radio a connect attempt parks in Connecting rather
/// than reaching an edge at all). Rather than ship a contract resting on an
/// unverified premise, the pushed stack is emptied EXPLICITLY before every
/// absolute navigation. If Shell would have cleared it anyway the pop is a
/// no-op on an empty stack; if it would not, the contract still holds. What
/// is NOT claimed is a measurement — nobody has watched this happen.</para>
///
/// <para><b>Why failures are swallowed.</b> Every caller here is a
/// fire-and-forget response to a session phase edge or a title-bar tap.
/// <c>GoToAsync</c> can throw when the shell is mid-teardown or a route is
/// momentarily unresolvable; the honest response is "the operator stays where
/// they are", never an unobserved task exception during shutdown.</para>
/// </summary>
public sealed class ShellNavigator : INavigator
{
    public Task GoToOperate() => GoAsync("//operate");

    public Task GoToConnectionSettings() => GoAsync("//settings");

    public Task GoToAbout() => GoAsync(AppShell.AboutRoute);

    /// <summary>True for the tab navigations, false for the About push — the
    /// one property that decides whether a pushed page is cleared first.</summary>
    private static bool IsAbsolute(string route) => route.StartsWith("//", StringComparison.Ordinal);

    private static async Task GoAsync(string route)
    {
        var shell = Shell.Current;
        if (shell is null) return;

        // The belt (see the class remarks). Its OWN try/catch: an empty stack
        // or a shell mid-teardown must not cost the navigation that follows.
        // Unanimated — this is a consequence of a phase edge, not a gesture,
        // and a page sliding away by itself reads as a glitch.
        if (IsAbsolute(route))
        {
            try { await shell.Navigation.PopToRootAsync(animated: false); }
            catch { /* nothing pushed, or nothing left to pop */ }
        }

        try
        {
            await shell.GoToAsync(route);
        }
        catch
        {
            // The operator stays where they are (see the class remarks).
        }
    }
}
