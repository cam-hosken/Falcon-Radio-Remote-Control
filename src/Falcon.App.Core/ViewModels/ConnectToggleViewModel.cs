using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The shell-level Connect ⇄ Disconnect control (GUI rejigger N2): one
/// toggle whose label follows the session phase — "Connect" while the
/// session is Disconnected/Failed, "Disconnect" otherwise (a Connecting or
/// Reconnecting session is live and the gesture that makes sense is
/// tearing it down). Connect uses the LAST-SELECTED app-side settings from
/// <see cref="ConnectionSettingsViewModel"/> (port, baud, bits, parity,
/// stop) and is greyed while no port has ever been selected (owner ruling,
/// round 2). Built over <see cref="RadioSession"/> directly — the spine's
/// RadioSessionViewModel is display-only and stays untouched (Wave-1
/// coordination ruling).
/// </summary>
public partial class ConnectToggleViewModel : ObservableObject
{
    private readonly RadioSession _session;
    private readonly ConnectionSettingsViewModel _settings;

    [ObservableProperty] private string label = "Connect";

    public ConnectToggleViewModel(RadioSession session, ConnectionSettingsViewModel settings)
    {
        _session = session;
        _settings = settings;
        session.PhaseChanged += (_, _) => Refresh();
        settings.PropertyChanged += OnSettingsPropertyChanged;
        Refresh();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionSettingsViewModel.SelectedPort))
            ToggleCommand.NotifyCanExecuteChanged();
    }

    private bool IsSessionDown => _session.Phase is SessionPhase.Disconnected or SessionPhase.Failed;

    private void Refresh()
    {
        Label = IsSessionDown ? "Connect" : "Disconnect";
        ToggleCommand.NotifyCanExecuteChanged();
    }

    // Greyed only when a CONNECT is impossible (no port ever selected);
    // a live session can always be disconnected.
    private bool CanToggle() => !IsSessionDown || _settings.CreatePortSettings() is not null;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        if (IsSessionDown)
        {
            var settings = _settings.CreatePortSettings();
            if (settings is null) return;   // guard re-check (constitution)
            // OWNER RULING (2026-08-21, round 14 G audit): the CONNECT press
            // also claims the selected port as the remembered preference —
            // "the port you connect to is remembered". Here and nowhere else:
            // this branch IS the button gesture, it runs BEFORE the attempt
            // (never conditional on success) and AFTER the null guard (an
            // inert press must not erase what is stored). The DISCONNECT
            // branch below claims nothing.
            _settings.ClaimSelectedPortAsPreference();
            // Task.Run: Connect blocks on the port open + first writes.
            await Task.Run(() => _session.Connect(settings)).ConfigureAwait(true);
        }
        else
        {
            await Task.Run(_session.Close).ConfigureAwait(true);
        }
    }
}
