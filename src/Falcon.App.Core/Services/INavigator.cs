namespace Falcon.App.Core.Services;

/// <summary>
/// The GUI-owned NAVIGATION seam (clone round 12 §6 F3), built on the
/// <see cref="IConfirmationPrompt"/> template: the interface lives in the
/// MAUI-free Falcon.App.Core so App.Core components can move the operator
/// between screens without touching a page (invariant 2), the Shell
/// implementation lives in the app head, and the tests take a recording fake.
///
/// <para><b>Why the app navigates itself at all.</b> The connection-first
/// flow (§6): the app opens on Connection settings, and the moments that
/// change what the operator needs to look at are SESSION PHASE EDGES, not
/// taps. A link that comes up wants the Operate screen; a link that fails or
/// is closed wants the page that can fix it. <see cref="SessionNavigationCoordinator"/>
/// owns which edge means what; this interface owns nothing but the three
/// destinations.</para>
///
/// <para><b>Absolute vs relative, and why it is part of the contract.</b>
/// <see cref="GoToOperate"/> and <see cref="GoToConnectionSettings"/> are
/// ABSOLUTE tab navigations: they must land on the tab even when the About
/// page is pushed on top, and an absolute Shell route clears the pushed
/// stack on the way. <see cref="GoToAbout"/> is the opposite — a RELATIVE
/// push, so the platform's own back gesture returns the operator to
/// whatever they were doing.</para>
/// </summary>
public interface INavigator
{
    /// <summary>Show the Operate screen (absolute — clears a pushed page).</summary>
    Task GoToOperate();

    /// <summary>Show the Connection settings screen (absolute — clears a
    /// pushed page).</summary>
    Task GoToConnectionSettings();

    /// <summary>PUSH the About page over the current screen; platform back
    /// returns.</summary>
    Task GoToAbout();
}
