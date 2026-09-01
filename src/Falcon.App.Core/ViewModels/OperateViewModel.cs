namespace Falcon.App.Core.ViewModels;

/// <summary>Composite BindingContext for the Operate page: the spine's four
/// per-concern ViewModels plus the mode-pane composites (SSB — Stage 4,
/// HOP — Stage 5, ALE — Stage 6) under one root, so the page has a single
/// x:DataType. Holds no state of its own.</summary>
public sealed class OperateViewModel(
    RadioSessionViewModel session,
    ModeViewModel mode,
    PowerViewModel power,
    SpineStatusViewModel status,
    SsbViewModel ssb,
    HopViewModel hop,
    AleViewModel ale,
    ModemViewModel modem)
{
    public RadioSessionViewModel Session { get; } = session;
    public ModeViewModel Mode { get; } = mode;
    public PowerViewModel Power { get; } = power;
    public SpineStatusViewModel Status { get; } = status;
    public SsbViewModel Ssb { get; } = ssb;
    public HopViewModel Hop { get; } = hop;
    public AleViewModel Ale { get; } = ale;

    /// <summary>Round 8 (ED): the cross-mode modem picker — ONE instance
    /// behind the modem row on all three panes.</summary>
    public ModemViewModel Modem { get; } = modem;
}
