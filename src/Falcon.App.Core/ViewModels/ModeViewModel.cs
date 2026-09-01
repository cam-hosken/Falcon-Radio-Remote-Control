using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Mode segmented control (SS/ALE/HO). Constitution (§2.4): the active
/// highlight comes ONLY from the radio's confirmed report — SelectMode never
/// touches the highlight (no optimistic updates); intent flows through the
/// explicit command, state flows one-way from the surface. Re-click on the
/// active segment is guarded (no unlock event ever comes), and only one
/// switch is in flight at a time (30 s deadline busy state).
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly ModeSurface _mode;
    private readonly RadioSession _session;

    [ObservableProperty] private bool isSsbActive;
    [ObservableProperty] private bool isAleActive;
    [ObservableProperty] private bool isHopActive;
    [ObservableProperty] private bool isSwitching;

    public ModeViewModel(ModeSurface mode, RadioSession session)
    {
        _mode = mode;
        _session = session;
        mode.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => SelectModeCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    private void Refresh()
    {
        var m = _mode.Mode;
        IsSsbActive = m.IsConfirmed && m.Value == OperatingMode.Ssb;
        IsAleActive = m.IsConfirmed && m.Value == OperatingMode.Ale;
        IsHopActive = m.IsConfirmed && m.Value == OperatingMode.Hop;
        IsSwitching = _mode.IsChangePending;
    }

    // Disabled-with-reason: the reason caption is bound to Session.IsReady
    // on the Operate page.
    private bool CanSelectMode(string? _) => _session.Phase == SessionPhase.Ready;

    [RelayCommand(CanExecute = nameof(CanSelectMode))]
    private void SelectMode(string? target)
    {
        if (!Enum.TryParse<OperatingMode>(target, ignoreCase: true, out var mode)) return;
        if (_mode.IsChangePending) return;                                   // one switch in flight
        if (_mode.Mode.IsConfirmed && _mode.Mode.Value == mode) return;      // re-click guard
        // ROUND 14 C (plan §4-C, owner ruling R10): an operator mode press is a
        // coupler-convergence TRIGGER — entering HOP the coupler must match the
        // CURRENT net's type before `HO` regenerates it, and leaving HOP it
        // converges back to whatever the operator's baseline is. This is the
        // gesture wrapper's ONLY caller; the clone campaign keeps plain Select.
        _mode.SelectAsOperatorGesture(mode);
    }
}
