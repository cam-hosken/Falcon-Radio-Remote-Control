namespace Falcon.App.Core.ViewModels;

/// <summary>Composite BindingContext slice for the SSB pane (plan §4.2):
/// the four section ViewModels under one root. Holds no state of its own —
/// pane visibility is driven by the spine's confirmed mode
/// (ModeViewModel.IsSsbActive).</summary>
public sealed class SsbViewModel(
    VfoViewModel vfo,
    SignalViewModel signal,
    ChannelViewModel channel,
    CouplerViewModel coupler)
{
    public VfoViewModel Vfo { get; } = vfo;
    public SignalViewModel Signal { get; } = signal;
    public ChannelViewModel Channel { get; } = channel;
    public CouplerViewModel Coupler { get; } = coupler;
}
