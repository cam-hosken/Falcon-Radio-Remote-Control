using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>One selectable value chip (bandwidth / AGC): the value string,
/// whether it is the radio's CONFIRMED current value, and the select
/// intent (routed to the parent VM's guarded command method — the pattern
/// XAML DataTemplates can bind without ancestor references).</summary>
public sealed class ChoiceItem
{
    public string Value { get; }
    public bool IsActive { get; }
    public IRelayCommand SelectCommand { get; }

    internal ChoiceItem(string value, bool isActive, Action<string> select)
    {
        Value = value;
        IsActive = isActive;
        SelectCommand = new RelayCommand(() => select(value));
    }
}

/// <summary>
/// The Signal section (plan §4.2): MODE segmented control, the bandwidth
/// chip row, and AGC. Constitution: highlights come ONLY from confirmed
/// reports (unreported MODE lights NOTHING — the enum-default leak class);
/// the BW choices are the MEASURED per-modulation sets (probe R5) keyed to
/// the CONFIRMED modulation — and, since F8 (plan-clone-field-round2.md), to
/// the LAST confirmed one while the mirror is unconfirmed, so the row is
/// disabled and unlit in that window rather than empty. The radio never
/// rejects BA: the answer line is the
/// read-back and the display always shows it (no-reject rule).
/// F6 (GUI rejigger): MODE/BW/AGC are channel-stored — editable only while
/// the CONFIRMED channel is 00 (unconfirmed channel counts as NOT 00).
/// F8/E6 (GUI rejigger Wave 2): this VM also owns the SSB Operate pane's
/// OPERATIONAL controls — the squelch peers (SQ/FMSQ/DGT_S), squelch level,
/// DV, compression and BFO offset (the modem picker moved to the cross-mode
/// ModemViewModel in round 8, ED). These are GLOBAL radio
/// state, NOT channel-stored, so they are NOT 00-gated (gate = Ready + SSB
/// confirmed only). Which squelch button shows is the MODULATION-VISIBILITY
/// matrix (E6), an explicit set of Show* properties keyed to the CONFIRMED
/// modulation (and DV for DGT_S) — NOT scattered XAML triggers, so it is
/// headless-testable: analog SQ in USB/LSB/AME/CW, FMSQ in FM, DGT_S while
/// DV is confirmed ON, BFO only in CW. (protocol.md establishes the three
/// squelches are independent peers on the wire; showing one at a time by
/// modulation is UI policy, which protocol.md explicitly sanctions.)
/// </summary>
public partial class SignalViewModel : ObservableObject
{
    private readonly SsbSurface _ssb;
    private readonly ChannelSurface _channel;
    private readonly RadioSession _session;

    [ObservableProperty] private bool isUsbActive;
    [ObservableProperty] private bool isLsbActive;
    [ObservableProperty] private bool isAmeActive;
    [ObservableProperty] private bool isCwActive;
    [ObservableProperty] private bool isFmActive;

    [ObservableProperty] private string bandwidthText = "—";
    [ObservableProperty] private IReadOnlyList<ChoiceItem> bandwidthChoices = [];
    [ObservableProperty] private bool isBandwidthEnabled;
    [ObservableProperty] private string bandwidthDisabledReason = "";

    [ObservableProperty] private string agcText = "—";
    [ObservableProperty] private IReadOnlyList<ChoiceItem> agcChoices = [];

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    // ---- F8/E6 operational controls (global state — NOT 00-gated) --------

    [ObservableProperty] private bool areOperationalControlsEnabled;
    [ObservableProperty] private string operationalDisabledReason = "";

    // Modulation-visibility matrix (E6): which squelch/BFO row is SHOWN.
    [ObservableProperty] private bool showAnalogSquelch;
    [ObservableProperty] private bool showFmSquelch;
    [ObservableProperty] private bool showDigitalSquelch;
    [ObservableProperty] private bool showBfo;

    [ObservableProperty] private bool isSquelchOn;
    [ObservableProperty] private bool isSquelchOff;
    [ObservableProperty] private bool isFmSquelchOn;
    [ObservableProperty] private bool isFmSquelchOff;
    [ObservableProperty] private bool isDigitalSquelchOn;
    [ObservableProperty] private bool isDigitalSquelchOff;
    [ObservableProperty] private bool isDvOn;
    [ObservableProperty] private bool isDvOff;
    [ObservableProperty] private bool isCompressionOn;
    [ObservableProperty] private bool isCompressionOff;

    [ObservableProperty] private bool isSquelchLevelLow;
    [ObservableProperty] private bool isSquelchLevelMedium;
    [ObservableProperty] private bool isSquelchLevelHigh;

    // Round 8 (ED): the modem picker cluster moved to ModemViewModel — MODEM
    // is cross-mode global state (the power pattern), not SSB state.

    [ObservableProperty] private string bfoText = "—";
    [ObservableProperty] private bool canStepBfo;

    /// <summary>BFO steps in 1 kHz decades over ±4 kHz (old-app range).</summary>
    private const int BfoStepHz = 1000;
    private const int BfoLimitHz = 4000;

    public SignalViewModel(SsbSurface ssb, ChannelSurface channel, RadioSession session)
    {
        _ssb = ssb;
        _channel = channel;
        _session = session;
        ssb.Changed += (_, _) => Refresh();
        channel.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>
    /// F8 (plan-clone-field-round2.md) — THE MODULATION THIS SESSION LAST
    /// CONFIRMED, and nothing else.
    ///
    /// <para><b>Why it exists.</b> A Digital Voice toggle silently forces USB,
    /// analog squelch ON and a bandwidth move, so Core UNCONFIRMS the modulation
    /// mirror until the radio re-reports it (round-13 D1,
    /// <c>RadioState.UnconfirmDvForcedValues</c>). The bandwidth chip row was
    /// keyed straight to that mirror, so for the width of that window the whole
    /// row VANISHED and came back — which is what the operator reported from the
    /// field on 2026-08-21. The choices are a MENU, not a report: keeping the
    /// last confirmed modulation's measured set on screen tells the operator
    /// nothing the radio has not said, while an empty row tells them the radio
    /// has no bandwidths at all.</para>
    ///
    /// <para><b>What it is NOT.</b> It never lights anything (I-7): while the
    /// modulation is unconfirmed NO chip is active, and the row stays disabled
    /// with the existing reason. It is scoped to the SESSION — cleared the
    /// moment the phase leaves Ready, so a new radio never inherits the old
    /// one's menu (<c>RadioState.ResetForConnect</c> is silent and could not be
    /// the hook; <c>PhaseChanged</c>, which this VM already observes, is).
    /// Nothing confirmed yet — a fresh session, or a session whose radio has
    /// never reported a modulation — shows the USB set.</para>
    /// </summary>
    private ModulationMode? _lastConfirmedModulation;

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool SsbReady => Ready && _ssb.IsSsbConfirmed;

    /// <summary>F6 00-gate: MODE/BW/AGC are channel-stored — editable only
    /// on a CONFIRMED CH 00 (unconfirmed counts as NOT 00).</summary>
    private bool ChannelZero => _channel.Current.IsConfirmed && _channel.Current.Value == 0;

    private bool Editable => SsbReady && ChannelZero;

    /// <summary>F8: the operational controls are global radio state — gated on
    /// Ready + confirmed SSB ONLY, never the F6 channel-00 gate.</summary>
    private bool OperationalEditable => SsbReady;

    private void Refresh()
    {
        var mod = _ssb.Modulation;
        IsUsbActive = mod.IsConfirmed && mod.Value == ModulationMode.Usb;
        IsLsbActive = mod.IsConfirmed && mod.Value == ModulationMode.Lsb;
        IsAmeActive = mod.IsConfirmed && mod.Value == ModulationMode.Ame;
        IsCwActive = mod.IsConfirmed && mod.Value == ModulationMode.Cw;
        IsFmActive = mod.IsConfirmed && mod.Value == ModulationMode.Fm;

        var bw = _ssb.Bandwidth;
        BandwidthText = bw.IsConfirmed ? bw.Value! : "—";

        // F8: the choice LIST follows the last CONFIRMED modulation (see
        // _lastConfirmedModulation) so a DV toggle's unconfirm window cannot
        // empty the row; the HIGHLIGHT still follows the mirrors alone, so an
        // unconfirmed modulation lights nothing. Not Ready = no confirmed truth
        // to follow at all: the memory is dropped and the row falls back to the
        // USB set, exactly as a fresh session's does.
        if (!Ready) _lastConfirmedModulation = null;
        else if (mod.IsConfirmed) _lastConfirmedModulation = mod.Value;

        var listModulation = Ready && mod.IsConfirmed
            ? mod.Value
            : _lastConfirmedModulation ?? ModulationMode.Usb;
        BandwidthChoices = [.. Wire.AllowedBandwidths(listModulation).Select(v =>
            new ChoiceItem(v, mod.IsConfirmed && bw.IsConfirmed && bw.Value == v, SetBandwidth))];
        IsBandwidthEnabled = Editable && mod.IsConfirmed;
        BandwidthDisabledReason = mod.IsConfirmed ? "" :
            "Bandwidth choices wait for the radio to report the modulation.";

        var agc = _ssb.Agc;
        AgcText = agc.IsConfirmed ? agc.Value.ToWire() : "—";
        // H1: display title-case ("Slow"); SetAgc upper-cases back to wire.
        AgcChoices = [.. Enum.GetValues<AgcSpeed>().Select(s =>
            new ChoiceItem(TitleCase(s.ToWire()), agc.IsConfirmed && agc.Value == s, SetAgc))];

        AreControlsEnabled = Editable;
        DisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed
                ? "Signal controls are SSB-domain — waiting for the radio to confirm SSB."
                : !ChannelZero
                    ? "Channel-stored settings are editable on CH 00 only."
                    : "";

        RefreshOperational(mod);

        SetModulationCommand.NotifyCanExecuteChanged();
        SetBandwidthCommand.NotifyCanExecuteChanged();
        SetAgcCommand.NotifyCanExecuteChanged();
        SetSquelchCommand.NotifyCanExecuteChanged();
        SetFmSquelchCommand.NotifyCanExecuteChanged();
        SetDigitalSquelchCommand.NotifyCanExecuteChanged();
        SetDigitalVoiceCommand.NotifyCanExecuteChanged();
        SetCompressionCommand.NotifyCanExecuteChanged();
        SetSquelchLevelCommand.NotifyCanExecuteChanged();
        BfoUpCommand.NotifyCanExecuteChanged();
        BfoDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>F8/E6: the operational controls and the modulation-visibility
    /// matrix. Rendered only from CONFIRMED state — an unconfirmed modulation
    /// shows NO squelch/BFO row (we cannot know which applies).</summary>
    private void RefreshOperational(Confirmed<ModulationMode> mod)
    {
        AreOperationalControlsEnabled = OperationalEditable;
        OperationalDisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed
                ? "SSB operational controls wait for the radio to confirm SSB."
                : "";

        // E6 visibility matrix — keyed to the CONFIRMED modulation (and DV).
        bool analogMod = mod.IsConfirmed && mod.Value is
            ModulationMode.Usb or ModulationMode.Lsb or ModulationMode.Ame or ModulationMode.Cw;
        var dv = _ssb.DigitalVoice;
        bool dvOn = dv.IsConfirmed && dv.Value == OnOff.On;
        // CLONE ROUND 12 §9 B5: DV hides the ANALOG squelch row exactly as FM
        // does. The row is the analog peer's control, and while DV is
        // CONFIRMED ON the radio is not running analog squelch — leaving it on
        // screen invited the bench report. CONFIRMED-ON only: an unconfirmed
        // or absent DV mirror leaves the row exactly where the modulation put
        // it (unreported is never a default).
        //
        // The squelch-LEVEL row below has no IsVisible binding at all
        // (SsbPaneView.xaml) and therefore stays visible in the DV case just
        // as it already does in the FM case. That FM precedent is RECORDED,
        // not changed, here: SQ_L is a peer setting of its own and no capture
        // scopes it to a modulation.
        ShowAnalogSquelch = analogMod && !dvOn;
        ShowFmSquelch = mod.IsConfirmed && mod.Value == ModulationMode.Fm;
        ShowBfo = mod.IsConfirmed && mod.Value == ModulationMode.Cw;

        ShowDigitalSquelch = dv.IsConfirmed && dv.Value == OnOff.On;
        IsDvOn = dv.IsConfirmed && dv.Value == OnOff.On;
        IsDvOff = dv.IsConfirmed && dv.Value == OnOff.Off;

        var sq = _ssb.AnalogSquelch;
        IsSquelchOn = sq.IsConfirmed && sq.Value == OnOff.On;
        IsSquelchOff = sq.IsConfirmed && sq.Value == OnOff.Off;

        var fmsq = _ssb.FmSquelch;
        IsFmSquelchOn = fmsq.IsConfirmed && fmsq.Value == OnOff.On;
        IsFmSquelchOff = fmsq.IsConfirmed && fmsq.Value == OnOff.Off;

        var dgt = _ssb.DigitalSquelch;
        IsDigitalSquelchOn = dgt.IsConfirmed && dgt.Value == OnOff.On;
        IsDigitalSquelchOff = dgt.IsConfirmed && dgt.Value == OnOff.Off;

        var com = _ssb.Compression;
        IsCompressionOn = com.IsConfirmed && com.Value == OnOff.On;
        IsCompressionOff = com.IsConfirmed && com.Value == OnOff.Off;

        // SQ_LEVEL is a verbatim string mirror. CLONE ROUND 12 §9 B4: the
        // compare is ENUM-TO-ENUM through the report reader, because the
        // REPORT vocabulary (LOW/MED/HIGH) is not the SET vocabulary
        // (LO/MEDIUM/HIGH) — they coincide on HIGH alone, which is exactly
        // why LOW and MED never highlighted. An unrecognized payload reads
        // null and lights NOTHING.
        var lvl = ReportedSquelchLevel();
        IsSquelchLevelLow = lvl == SquelchLevel.Low;
        IsSquelchLevelMedium = lvl == SquelchLevel.Medium;
        IsSquelchLevelHigh = lvl == SquelchLevel.High;

        var bfo = _ssb.BfoOffset;
        BfoText = bfo.IsConfirmed ? bfo.Value! : "—";
        // Stepping needs a confirmed value to compute the next absolute BFO.
        CanStepBfo = OperationalEditable && ShowBfo && TryParseBfo(bfo, out _);
    }

    /// <summary>The CONFIRMED squelch level as an enum, or null — unconfirmed,
    /// or a payload outside the three captured report spellings (§9 B4's
    /// try-parse contract). The single reader for both the highlight row and
    /// the re-click guard, so the two cannot drift apart again.</summary>
    private SquelchLevel? ReportedSquelchLevel()
    {
        var lvl = _ssb.SquelchLevel;
        return lvl.IsConfirmed && lvl.Value is { } payload
            ? Wire.SquelchLevelFromReport(payload)
            : null;
    }

    /// <summary>H1 display casing: "SLOW" → "Slow" (wire casing is restored
    /// by the select parsers).</summary>
    private static string TitleCase(string wire)
        => wire.Length == 0 ? wire : char.ToUpperInvariant(wire[0]) + wire[1..].ToLowerInvariant();

    private static bool TryParseBfo(Confirmed<string> bfo, out int hz)
    {
        hz = 0;
        return bfo.IsConfirmed && bfo.Value is { } v
            && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out hz);
    }

    private bool CanSend(string? _) => Editable;

    // In-body guards repeat CanExecute: ICommand.Execute does not consult it.

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SetModulation(string? target)
    {
        if (!Editable) return;
        if (!Enum.TryParse<ModulationMode>(target, ignoreCase: true, out var mode)) return;
        var current = _ssb.Modulation;
        if (current.IsConfirmed && current.Value == mode) return;   // re-click guard
        _ssb.SetModulation(mode);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SetBandwidth(string? value)
    {
        if (!Editable) return;
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!_ssb.Modulation.IsConfirmed) return;   // choices only exist under a confirmed modulation
        var current = _ssb.Bandwidth;
        if (current.IsConfirmed && current.Value == value) return;  // re-click guard
        _ssb.SetBandwidth(value);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SetAgc(string? target)
    {
        if (!Editable) return;
        var speed = Wire.ParseAgcSpeed((target ?? "").ToUpperInvariant());
        if (speed is null) return;
        var current = _ssb.Agc;
        if (current.IsConfirmed && current.Value == speed) return;  // re-click guard
        _ssb.SetAgc(speed.Value);
    }

    // ---- F8 operational commands (gated on Ready+SSB, NOT channel-00) -----

    private bool CanOperate(string? _) => OperationalEditable;

    private static OnOff? ParseOnOff(string? s) =>
        string.Equals(s, "On", StringComparison.OrdinalIgnoreCase) ? OnOff.On :
        string.Equals(s, "Off", StringComparison.OrdinalIgnoreCase) ? OnOff.Off : null;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetSquelch(string? target)
    {
        if (!OperationalEditable || ParseOnOff(target) is not { } state) return;
        var c = _ssb.AnalogSquelch;
        if (c.IsConfirmed && c.Value == state) return;   // re-click guard
        _ssb.SetSquelch(state);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetFmSquelch(string? target)
    {
        if (!OperationalEditable || ParseOnOff(target) is not { } state) return;
        var c = _ssb.FmSquelch;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetFmSquelch(state);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetDigitalSquelch(string? target)
    {
        if (!OperationalEditable || ParseOnOff(target) is not { } state) return;
        var c = _ssb.DigitalSquelch;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetDigitalSquelch(state);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetDigitalVoice(string? target)
    {
        if (!OperationalEditable || ParseOnOff(target) is not { } state) return;
        var c = _ssb.DigitalVoice;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetDigitalVoice(state);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetCompression(string? target)
    {
        if (!OperationalEditable || ParseOnOff(target) is not { } state) return;
        var c = _ssb.Compression;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetCompression(state);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SetSquelchLevel(string? target)
    {
        if (!OperationalEditable) return;
        SquelchLevel? level = target switch
        {
            "LO" => SquelchLevel.Low,
            "MED" => SquelchLevel.Medium,
            "HI" => SquelchLevel.High,
            _ => null,
        };
        if (level is null) return;
        // Re-click guard, enum-to-enum for the §9 B4 reason: comparing the
        // REPORT payload against this SET token matched on HIGH alone, so a
        // re-click on LOW or MED re-sent a command the radio was already
        // obeying. An unreadable report reads null and guards nothing — the
        // send goes out, which is the honest direction to fail.
        if (ReportedSquelchLevel() == level.Value) return;
        _ssb.SetSquelchLevel(level.Value);
    }

    private bool CanStepBfoNow() => CanStepBfo;

    [RelayCommand(CanExecute = nameof(CanStepBfoNow))]
    private void BfoUp() => StepBfo(+BfoStepHz);

    [RelayCommand(CanExecute = nameof(CanStepBfoNow))]
    private void BfoDown() => StepBfo(-BfoStepHz);

    private void StepBfo(int delta)
    {
        if (!OperationalEditable || !ShowBfo) return;
        if (!TryParseBfo(_ssb.BfoOffset, out int current)) return;
        int target = Math.Clamp(current + delta, -BfoLimitHz, BfoLimitHz);
        if (target == current) return;   // at the edge — nothing to send
        _ssb.SetBfoOffset(target);
    }
}
