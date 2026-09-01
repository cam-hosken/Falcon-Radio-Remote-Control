using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Settings → "Radio port (read-only)" (plan §4.5, deferred until Stage 11)
/// plus the guarded baud wizard (plan §7 decision 3, §3 guarded-flow
/// vocabulary).
///
/// - The dump section renders the radio's own PORT_R report verbatim, "—"
///   until reported this session (never a default).
/// - "Change radio baud…" opens the wizard: warning text naming the exact
///   consequence, an EXPLICIT target selection (no default), and a
///   confirmation the operator must TYPE, so the flow cannot be defaulted or
///   clicked through (§3: no accidental triggers). Round 10 (§5, owner
///   ruling 9) removed Core's matching token gate — the typed rate is no
///   longer a token Core checks, so <see cref="StartCommand"/>'s own body
///   guard is the only stop and stays. Progress renders one-way from
///   <see cref="BaudChangeFlow"/>; every gesture is a command.
/// - SelectedTarget/ConfirmationText are APP-side inputs (two-way binding is
///   legal — the §2.4 ban is on radio state).
/// </summary>
public partial class RadioPortViewModel : ObservableObject
{
    private readonly PortSurface _port;
    private readonly BaudChangeFlow _flow;
    private readonly RadioSession _session;

    public IReadOnlyList<int> TargetRates => BaudChangeFlow.SupportedRates;

    public string WarningText =>
        "The radio's remote port will be reconfigured; the session will drop " +
        "and reconnect at the new rate; if reconnection fails the radio may " +
        "need front-panel recovery.";

    // ---- PORT_R dump display (read-only, "—" until reported) ---------------
    [ObservableProperty] private string baudText = "—";
    [ObservableProperty] private string bitsText = "—";
    [ObservableProperty] private string parityText = "—";
    [ObservableProperty] private string stopText = "—";
    [ObservableProperty] private string echoText = "—";
    [ObservableProperty] private string xonXoffText = "—";

    // ---- Wizard state --------------------------------------------------------
    [ObservableProperty] private bool isWizardOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int? selectedTarget;                 // app-side input; NO default
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string confirmationText = "";        // app-side input; typed rate
    [ObservableProperty] private string startDisabledReason = "";

    // ---- Flow progress (one-way from the flow) ---------------------------------
    [ObservableProperty] private string flowStatusText = "";
    [ObservableProperty] private bool isFlowRunning;
    [ObservableProperty] private bool isFlowDone;
    [ObservableProperty] private bool isFlowFailed;

    public RadioPortViewModel(PortSurface port, BaudChangeFlow flow, RadioSession session)
    {
        _port = port;
        _flow = flow;
        _session = session;
        port.Changed += (_, _) => RefreshDump();
        flow.Changed += (_, _) => RefreshFlow();
        session.PhaseChanged += (_, _) => RefreshCommands();
        RefreshDump();
        RefreshFlow();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    private void RefreshDump()
    {
        BaudText = Text(_port.Baud);
        BitsText = Text(_port.Bits);
        ParityText = Text(_port.Parity);
        StopText = Text(_port.StopBits);
        XonXoffText = Text(_port.XonXoff);
        var echo = _port.Echo;
        EchoText = echo.IsConfirmed ? echo.Value.ToString().ToUpperInvariant() : "—";
    }

    private static string Text(Falcon.Core.Radio.Confirmed<string> value)
        => value.IsConfirmed ? value.Value! : "—";

    private void RefreshFlow()
    {
        FlowStatusText = _flow.StatusText;
        IsFlowRunning = _flow.IsRunning;
        IsFlowDone = _flow.State == BaudChangeState.Done;
        IsFlowFailed = _flow.State is BaudChangeState.Failed or BaudChangeState.NoOp;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        StartDisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : IsFlowRunning ? "Baud change in progress…"
            : SelectedTarget is null ? "Select the target rate."
            : !ConfirmationMatches() ? $"Type the target rate ({SelectedTarget}) to confirm — the change drops the session."
            : "";
        OpenWizardCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        CancelWizardCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(int? value) => RefreshCommands();
    partial void OnConfirmationTextChanged(string value) => RefreshCommands();

    private bool ConfirmationMatches()
        => SelectedTarget is int t
           && string.Equals(ConfirmationText.Trim(),
               t.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    // ---- Commands (bodies repeat their guards — Execute never consults CanExecute) ----

    private bool CanOpenWizard() => !IsWizardOpen;

    [RelayCommand(CanExecute = nameof(CanOpenWizard))]
    private void OpenWizard()
    {
        if (IsWizardOpen) return;
        // A fresh open never inherits a previous selection or confirmation —
        // the confirmation step cannot be defaulted through (§3).
        SelectedTarget = null;
        ConfirmationText = "";
        _flow.Reset();
        IsWizardOpen = true;
        RefreshCommands();
    }

    private bool CanCancelWizard() => IsWizardOpen && !IsFlowRunning;

    [RelayCommand(CanExecute = nameof(CanCancelWizard))]
    private void CancelWizard()
    {
        if (!IsWizardOpen || IsFlowRunning) return;   // no cancel mid-flight
        IsWizardOpen = false;
        SelectedTarget = null;
        ConfirmationText = "";
        _flow.Reset();
        RefreshCommands();
    }

    private bool CanStart()
        => IsWizardOpen && !IsFlowRunning && Ready
           && SelectedTarget is not null && ConfirmationMatches();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        // UI tweaks round 10 (§5, owner ruling 9): Core's token gate is gone —
        // "the back end does what the GUI tells it". This body guard is now
        // the ONLY stop, and it STAYS: CanStart() re-checks the typed
        // confirmation (ICommand.Execute never consults CanExecute), so a
        // bypassed Execute with a mismatched confirmation still sends
        // nothing. The wizard GUI itself does not currently exist (the
        // SettingsPage wizard was removed in the rejigger); this dormant
        // backend keeps its GUI-side gate until a wizard returns. Recorded,
        // not silent — plan §2 ruling 9.
        if (!CanStart()) return;
        _flow.Start(SelectedTarget!.Value);
    }
}
