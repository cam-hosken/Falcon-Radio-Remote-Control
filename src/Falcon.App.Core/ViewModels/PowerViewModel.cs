using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Power segmented control (LOW/MED/HI). Same constitution as the mode
/// control: highlight only from the confirmed report (the POW answer is the
/// read-back), re-click on the active level guarded. POWER CUTBACK shows the
/// ⚠ note until POWER RESTORED.
/// </summary>
public partial class PowerViewModel : ObservableObject
{
    private readonly PowerSurface _power;
    private readonly RadioSession _session;

    [ObservableProperty] private bool isLowActive;
    [ObservableProperty] private bool isMedActive;
    [ObservableProperty] private bool isHiActive;
    [ObservableProperty] private bool showCutback;

    public PowerViewModel(PowerSurface power, RadioSession session)
    {
        _power = power;
        _session = session;
        power.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => SetPowerCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    private void Refresh()
    {
        var level = _power.Level;
        IsLowActive = level.IsConfirmed && level.Value == PowerLevel.Low;
        IsMedActive = level.IsConfirmed && level.Value == PowerLevel.Medium;
        IsHiActive = level.IsConfirmed && level.Value == PowerLevel.High;
        ShowCutback = _power.Cutback.IsConfirmed && _power.Cutback.Value;
    }

    private bool CanSetPower(string? _) => _session.Phase == SessionPhase.Ready;

    [RelayCommand(CanExecute = nameof(CanSetPower))]
    private void SetPower(string? target)
    {
        if (!Enum.TryParse<PowerLevel>(target, ignoreCase: true, out var level)) return;
        if (_power.Level.IsConfirmed && _power.Level.Value == level) return;  // re-click guard
        _power.Set(level);
    }
}
