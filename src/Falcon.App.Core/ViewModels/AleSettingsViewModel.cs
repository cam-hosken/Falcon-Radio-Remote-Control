using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The ALE settings pane (Mode settings page, plan round-8 ALE row): the nine
/// bench-confirmed ALE query+set settings — the six ON/OFF toggles (ALL_C,
/// ANY_C, AMD_D, KEY_T, LSTN, RAD_S) and the three numeric fields (MAXCH,
/// TIME_OU, TUNE time). LQA scheduling is NOT here — it lives on the Operate
/// ALE pane's LQA sub-tab.
///
/// Constitution (plan §0 / §2.4): the display is ONE-WAY from the confirmed
/// mirror — an unreported value renders "—", the ON/OFF highlight moves only
/// on the radio's confirmed report, and a command never optimistically
/// updates the display. The numeric ENTRIES are app-side input (two-way is
/// legal there); their "Set" command validates the range client-side, then
/// sends through the surface → W1 AleController builder. Every setting is
/// mode-scoped on the wire (sent at an ALE&gt; prompt), so the controls are
/// gated on Ready + a CONFIRMED ALE mode.
///
/// Lazy first load (plan Q4 / N4): the first time a Ready session confirms
/// ALE, the pane sends its query set ONCE — a single SH, which carries all
/// nine settings in the ALE SH block. The mirror IS the cache; nothing
/// re-queries on a bare re-entry into the pane, and since round 10 §6 there is
/// no manual Refresh here either.
/// </summary>
public partial class AleSettingsViewModel : ObservableObject
{
    // Wire authority over HELP text (convention rule 6): MAXCH 0-100 and
    // TUNE 1-60 match HELP; TIME_OU is 0-60 because 0 is measured valid
    // (session-18 — "TIME_OU 0" echoes "TIME_OUT 000"), widening HELP's
    // "1-60". Client-side range checks mirror the W1 builder bounds so a
    // bad entry is caught before any send (the builders throw otherwise).
    private const int MaxChMin = 0, MaxChMax = 100;
    private const int TimeOutMin = 0, TimeOutMax = 60;
    private const int TuneMin = 1, TuneMax = 60;

    private readonly AleSurface _ale;
    private readonly RadioSession _session;

    private bool _loadedThisSession;

    // Confirmed-state displays (one-way from the mirror).
    [ObservableProperty] private string allCall = "—";
    [ObservableProperty] private string anyCall = "—";
    [ObservableProperty] private string amdDisplay = "—";
    [ObservableProperty] private string keyToCall = "—";
    [ObservableProperty] private string listenBeforeTx = "—";
    [ObservableProperty] private string radioSilence = "—";
    [ObservableProperty] private string maxScanChannelsText = "—";
    [ObservableProperty] private string linkTimeoutText = "—";
    [ObservableProperty] private string tuneTimeText = "—";

    // App-side numeric entries (two-way legal — operator input, not a mirror).
    [ObservableProperty] private string maxChannelsInput = "";
    [ObservableProperty] private string linkTimeoutInput = "";
    [ObservableProperty] private string tuneTimeInput = "";

    /// <summary>Shared client-side validation note for the numeric fields
    /// (cleared on a valid send). Radio-side rejections still surface via the
    /// error toast like every other command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 6). Null in every composition that has no clone campaign to wait for —
    /// suppression is then a no-op and this pane behaves exactly as it did.</summary>
    private readonly ICampaignSignal? _campaign;

    public AleSettingsViewModel(AleSurface ale, RadioSession session, ICampaignSignal? campaign = null)
    {
        _ale = ale;
        _session = session;
        _campaign = campaign;
        ale.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready) ResetSessionFlags();
            Refresh();
        };
        // THE ONE OWED READ (D1): the campaign's END edge re-runs the same
        // lazy-load path, which is still owed because the deferral left
        // `_loadedThisSession` unset.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool AleReady => Ready && _ale.IsAleConfirmed;

    private void ResetSessionFlags()
    {
        _loadedThisSession = false;
        InputError = "";
    }

    private void Refresh()
    {
        // Lazy first load (once per session): a single SH carries all nine
        // settings (ALE SH block) — a query, visible in the Console.
        //
        // D1 QUIESCE: while a clone campaign owns the wire this read DEFERS —
        // the latch is left unset, so the campaign-end handler owes it and runs
        // it exactly once. Everything below (the display shaping) is unaffected:
        // suppression is about the WIRE, not about the pane.
        if (AleReady && !_loadedThisSession && _campaign?.CampaignActive != true)
        {
            _loadedThisSession = true;
            _ale.RequestSettings();
        }

        AllCall = OnOffText(_ale.AllCall);
        AnyCall = OnOffText(_ale.AnyCall);
        AmdDisplay = OnOffText(_ale.AmdDisplay);
        KeyToCall = OnOffText(_ale.KeyToCall);
        ListenBeforeTx = OnOffText(_ale.ListenBeforeTx);
        RadioSilence = OnOffText(_ale.RadioSilence);
        MaxScanChannelsText = IntText(_ale.MaxScanChannels);
        LinkTimeoutText = IntText(_ale.LinkTimeoutMinutes);
        TuneTimeText = IntText(_ale.TuneTimeSeconds);

        AreControlsEnabled = AleReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ale.IsAleConfirmed ? "ALE settings are mode-scoped — waiting for the radio to confirm ALE."
            : "";

        SetAllCallCommand.NotifyCanExecuteChanged();
        SetAnyCallCommand.NotifyCanExecuteChanged();
        SetAmdDisplayCommand.NotifyCanExecuteChanged();
        SetKeyToCallCommand.NotifyCanExecuteChanged();
        SetListenBeforeTxCommand.NotifyCanExecuteChanged();
        SetRadioSilenceCommand.NotifyCanExecuteChanged();
        ApplyMaxScanChannelsCommand.NotifyCanExecuteChanged();
        ApplyLinkTimeoutCommand.NotifyCanExecuteChanged();
        ApplyTuneTimeCommand.NotifyCanExecuteChanged();
    }

    private static string OnOffText(Falcon.Core.Radio.Confirmed<OnOff> c)
        => c.IsConfirmed ? (c.Value == OnOff.On ? "ON" : "OFF") : "—";

    private static string IntText(Falcon.Core.Radio.Confirmed<int> c)
        => c.IsConfirmed ? c.Value.ToString(CultureInfo.InvariantCulture) : "—";

    private bool CanUseAle() => AleReady;

    // ---- ON/OFF toggles (explicit value, no optimism) ----------------------
    // The value comes from the pressed button ("ON"/"OFF"); the highlight
    // moves only when the radio's confirmed report lands (Refresh).

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetAllCall(string? value) => ApplyOnOff(_ale.SetAllCall, value);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetAnyCall(string? value) => ApplyOnOff(_ale.SetAnyCall, value);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetAmdDisplay(string? value) => ApplyOnOff(_ale.SetAmdDisplay, value);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetKeyToCall(string? value) => ApplyOnOff(_ale.SetKeyToCall, value);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetListenBeforeTx(string? value) => ApplyOnOff(_ale.SetListenBeforeTx, value);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void SetRadioSilence(string? value) => ApplyOnOff(_ale.SetRadioSilence, value);

    private void ApplyOnOff(Action<OnOff> setter, string? value)
    {
        if (!AleReady) return;                       // re-check: Execute ignores CanExecute
        var parsed = (value?.Trim().ToUpperInvariant()) switch
        {
            "ON" => (OnOff?)OnOff.On,
            "OFF" => OnOff.Off,
            _ => null,
        };
        if (parsed is null) return;
        setter(parsed.Value);
    }

    // ---- Numeric fields (client-validated app-side input) ------------------

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void ApplyMaxScanChannels()
        => ApplyInt(MaxChannelsInput, MaxChMin, MaxChMax, "MAXCH", _ale.SetMaxScanChannels);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void ApplyLinkTimeout()
        => ApplyInt(LinkTimeoutInput, TimeOutMin, TimeOutMax, "TIME_OU", _ale.SetLinkTimeout);

    [RelayCommand(CanExecute = nameof(CanUseAle))]
    private void ApplyTuneTime()
        => ApplyInt(TuneTimeInput, TuneMin, TuneMax, "TUNE time", _ale.SetTuneTime);

    private void ApplyInt(string? input, int min, int max, string name, Action<int> setter)
    {
        if (!AleReady) return;
        if (!int.TryParse((input ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            || v < min || v > max)
        {
            InputError = $"{name} must be a whole number {min}-{max}.";
            return;
        }
        InputError = "";
        setter(v);
    }

    // ---- Manual refresh: DELETED (UI tweaks round 10 §6) -------------------
    // The pane's "Refresh ALE settings" button and this VM's
    // RefreshSettingsCommand are GONE. Under §6's rationalization a Refresh
    // survives only where a read is genuinely expensive; the nine settings
    // arrive in ONE SH, already sent lazily once per session, and the four
    // cards on the pane each re-read their own target on every landing. The
    // lazy load (Refresh's `_loadedThisSession` path above) still calls
    // `_ale.RequestSettings()` — the SURFACE is untouched, only the manual
    // button is gone. Absence is pinned (AleSettingsViewModelTests +
    // AleProgrammingMarkupGuardTests) so the deletion stays deliberate.
}
