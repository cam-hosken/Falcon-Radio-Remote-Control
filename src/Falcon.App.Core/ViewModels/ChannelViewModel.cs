using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The Channel section (GUI rejigger F6/F7; round 15 N2): the two big digits
/// are the CURRENT channel (display only), the operator picks a new one by
/// typing 1–2 digits and pressing Select — which sends CH nn + SH via
/// <see cref="ChannelSurface.Select"/> (the re-read stays — trap note in
/// docs/software-architecture.md) — and the RXONLY YES/NO pair (channel-
/// stored, so 00-gated like the rest of the six; highlight only from the
/// confirmed RXONLY report).
/// <para>Round 15 N2 DELETED the per-digit ▲/▼ spinners (TensUp/TensDown/
/// UnitsUp/UnitsDown + their rate limiter) and the vestigial flyout members
/// (IsListOpen/RefreshList/CloseList/SelectChannel(StoredChannel)). The
/// entry replaces them: reaching CH 07 is two keystrokes, not seven spins.
/// <see cref="Channels"/> SURVIVES — it is the read-only DI projection the
/// Stage-4 bench harness logs, not flyout state (D2).</para>
/// </summary>
public partial class ChannelViewModel : ObservableObject
{
    private readonly ChannelSurface _channel;
    private readonly SsbSurface _ssb;
    private readonly RadioSession _session;

    [ObservableProperty] private string currentChannelText = "CH —";
    [ObservableProperty] private string tensText = "—";
    [ObservableProperty] private string unitsText = "—";
    [ObservableProperty] private IReadOnlyList<StoredChannel> channels = [];
    [ObservableProperty] private bool isRxOnlyYes;
    [ObservableProperty] private bool isRxOnlyNo;
    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    /// <summary>The app-side entry buffer (N2). NEVER written by the radio
    /// (I-5): the placeholder is the static range hint "00-99", exactly the
    /// RF-gain idiom, and the buffer survives a send.</summary>
    [ObservableProperty] private string channelInput = "";

    /// <summary>Why the last Select was refused, in prose (I-2), or "" for
    /// none. Cleared by the next valid Select and by a session drop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => InputError.Length > 0;

    public ChannelViewModel(ChannelSurface channel, SsbSurface ssb, RadioSession session)
    {
        _channel = channel;
        _ssb = ssb;
        _session = session;
        channel.Changed += (_, _) => Refresh();
        ssb.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => Refresh();
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool SsbReady => Ready && _ssb.IsSsbConfirmed;

    /// <summary>F6 00-gate: RXONLY is channel-stored — editable only on a
    /// CONFIRMED CH 00 (unconfirmed counts as NOT 00).</summary>
    private bool ChannelZero => _channel.Current.IsConfirmed && _channel.Current.Value == 0;

    private void Refresh()
    {
        var current = _channel.Current;
        CurrentChannelText = current.IsConfirmed ? $"CH {current.Value:00}" : "CH —";
        TensText = current.IsConfirmed ? (current.Value / 10).ToString() : "—";
        UnitsText = current.IsConfirmed ? (current.Value % 10).ToString() : "—";

        Channels = _channel.Channels;

        var rxOnly = _ssb.RxOnly;
        IsRxOnlyYes = rxOnly.IsConfirmed && rxOnly.Value == YesNo.Yes;
        IsRxOnlyNo = rxOnly.IsConfirmed && rxOnly.Value == YesNo.No;

        AreControlsEnabled = SsbReady;
        DisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed
                ? "Channel controls are SSB-domain — waiting for the radio to confirm SSB."
                : "";

        if (!Ready) InputError = "";       // a stale note dies with the session

        SelectEnteredCommand.NotifyCanExecuteChanged();
        SetRxOnlyCommand.NotifyCanExecuteChanged();
    }

    // ---- The entry + Select (N2) -------------------------------------------

    private bool CanSelectEntered() => SsbReady;

    // In-body guards repeat CanExecute: ICommand.Execute does not consult it.

    /// <summary>Select the typed channel: EXACTLY one or two ASCII digits,
    /// with no trim and no sign (D7 — an entry is not a free-text field, so
    /// " 7" is a refusal and not a 7). "7" selects 07. The send is CH nn +
    /// SH via <see cref="ChannelSurface.Select"/>; the digits move only on
    /// the CHAN answer. The buffer CLEARS on a valid selection (owner
    /// 2026-08-23 — this used to keep it, the RF-gain idiom; a refusal and
    /// the re-click still keep it, so a typo can be corrected in place).</summary>
    [RelayCommand(CanExecute = nameof(CanSelectEntered))]
    private void SelectEntered()
    {
        if (!SsbReady) return;
        var text = ChannelInput ?? "";
        if (text.Length is 0 or > 2 || !text.All(char.IsAsciiDigit))
        {
            InputError = "Channel must be a whole number 00-99.";
            return;
        }
        int n = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);   // 0..99 by construction
        InputError = "";
        var current = _channel.Current;
        if (current.IsConfirmed && current.Value == n) return;   // re-click guard
        _channel.Select(n);
        ChannelInput = "";   // owner 2026-08-23: the entry clears on selection
    }

    // ---- RXONLY (channel-stored — F6 00-gated) -----------------------------

    private bool CanSetRxOnly(string? _) => SsbReady && ChannelZero;

    [RelayCommand(CanExecute = nameof(CanSetRxOnly))]
    private void SetRxOnly(string? target)
    {
        if (!SsbReady || !ChannelZero) return;
        var value = Wire.ParseYesNo((target ?? "").ToUpperInvariant());
        if (value is null) return;
        var current = _ssb.RxOnly;
        if (current.IsConfirmed && current.Value == value) return;   // re-click guard
        _ssb.SetRxOnly(value.Value);
    }
}
