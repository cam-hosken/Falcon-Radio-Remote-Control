using Falcon.App.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Falcon.App.Views.SettingsParts;

/// <summary>
/// SSB settings pane (GUI rejigger Wave 2). The host page authored this
/// ContentView's IsVisible binding against its own ModeViewModel, so the
/// ContentView keeps that INHERITED BindingContext; only the INNER root
/// (Root) is re-pointed at the singleton SsbSettingsViewModel from DI. This
/// keeps Falcon.App.Core MAUI-free (the VM never sees a page) while letting
/// the pane's contents bind to their own VM.
///
/// UI-tweaks round 4 (AK): the "Channels" card at the top of the pane gets a
/// SECOND BindingContext — the channel editor's own VM — for the same reason
/// the ALE pane rebinds its Body: the editor owns a session channel cache and
/// a store flow that have nothing to do with the settings list.
///
/// The list's LAZY per-row read lives here because it is a VIEW fact: only
/// the rows a virtualizing CollectionView actually shows should be asked for.
/// The handlers translate "what is on screen" into the VM's visible-range
/// API; the once-per-session rule itself is the VM's (and is pinned there).
///
/// UI-tweaks round 5 (BF3): the Channels card's <c>Loaded</c> calls the VM's
/// <c>EnsureLoaded</c> — the round-4 DeviceClockView house pattern, and for
/// the same reason. The editor VM is a DI SINGLETON, so when the operator
/// reaches this pane on an already-Ready SSB session the VM was constructed
/// long ago and no phase transition is coming; without this hook the read-back
/// row and the prefilled buffers would sit empty until someone spun the
/// picker. <c>EnsureLoaded</c> is Ready-guarded and once-per-session, so the
/// hook firing on every re-appearance costs nothing.
/// </summary>
public partial class SsbSettingsPaneView : ContentView
{
    private readonly SsbChannelEditorViewModel _channelEditor;

    public SsbSettingsPaneView()
    {
        InitializeComponent();
        // Loud-fail (fail-fast, matching AleSettingsPaneView): a null service
        // provider or a missing registration would render the pane inert —
        // throw instead so the wiring bug is obvious.
        Root.BindingContext = IPlatformApplication.Current?.Services?.GetService<SsbSettingsViewModel>()
            ?? throw new InvalidOperationException(
                "SsbSettingsViewModel is not resolvable — the SSB settings pane cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");

        _channelEditor = IPlatformApplication.Current?.Services?.GetService<SsbChannelEditorViewModel>()
            ?? throw new InvalidOperationException(
                "SsbChannelEditorViewModel is not resolvable — the Channels card cannot bind "
                + "(check MauiProgram registration and that the app service provider is initialized).");
        ChannelsCard.BindingContext = _channelEditor;
        ChannelsCard.Loaded += (_, _) => _channelEditor.EnsureLoaded();
    }

    /// <summary>First paint of the list: ask for the rows it is showing.
    /// CollectionView reports no visible range until it has scrolled, so the
    /// opening window is derived from the control's height and the row
    /// pitch — deliberately generous rather than exact, because
    /// <c>RequestChannelOnce</c> is idempotent and a row scrolled to later
    /// simply asks then.</summary>
    private void OnChannelListLoaded(object? sender, EventArgs e)
        => _channelEditor.RequestChannelRange(0, InitialWindow);

    private void OnChannelListScrolled(object? sender, ItemsViewScrolledEventArgs e)
        => _channelEditor.RequestChannelRange(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);

    /// <summary>Rows visible in the list's fixed height at first paint
    /// (~420 dp over a ~40 dp two-line row), rounded up.</summary>
    private const int InitialWindow = 11;
}
