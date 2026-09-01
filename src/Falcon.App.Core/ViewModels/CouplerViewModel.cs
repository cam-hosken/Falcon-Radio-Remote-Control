using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The TUNE COUPLER button (RETU) — spine chrome since the GUI rejigger
/// (plan S5/S6), next to the tune status chip. FAULT is a NORMAL outcome, not
/// an error flow: the button never disables on FAULT — recovery is the
/// operator pressing it again. It disables while a tune is already in
/// progress, outside a Ready session, and by CONFIRMED mode (S6): enabled in
/// SSB and ALE ("retune is valid in ALE" — owner), greyed in HOP and while
/// the mode is unconfirmed — always with a visible reason.
/// </summary>
public partial class CouplerViewModel : ObservableObject
{
    private readonly CouplerSurface _coupler;
    private readonly ModeSurface _mode;
    private readonly RadioSession _session;

    [ObservableProperty] private bool isTuning;
    [ObservableProperty] private bool canTune;
    [ObservableProperty] private string tuneDisabledReason = "";

    public CouplerViewModel(CouplerSurface coupler, ModeSurface mode, RadioSession session)
    {
        _coupler = coupler;
        _mode = mode;
        _session = session;
        coupler.Changed += (_, _) => Refresh();
        mode.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => Refresh();
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>S6: RETU is valid at the SSB and ALE prompts, refused for HOP
    /// and for an UNCONFIRMED mode (never act on a guessed prompt).</summary>
    private bool ModeAllowsTune =>
        _mode.Mode.IsConfirmed && _mode.Mode.Value is OperatingMode.Ssb or OperatingMode.Ale;

    private void Refresh()
    {
        IsTuning = _coupler.IsTuning;
        CanTune = Ready && ModeAllowsTune && !_coupler.IsTuning;
        TuneDisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_mode.Mode.IsConfirmed
                ? "Waiting for the radio to confirm its mode."
                : !ModeAllowsTune
                    ? "TUNE is not available in HOP."
                    : _coupler.IsTuning
                        ? "Tune in progress…"
                        : "";
        TuneCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecuteTune() => CanTune;

    /// <summary>RETU. The radio TRANSMITS during the tune; the outcome
    /// (COMPLETE / MARGINAL / FAULT) arrives async on the spine chip.
    /// In-body guard repeats CanExecute (ICommand.Execute bypasses it).</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteTune))]
    private void Tune()
    {
        if (!CanTune) return;
        _coupler.Retune();
    }
}
