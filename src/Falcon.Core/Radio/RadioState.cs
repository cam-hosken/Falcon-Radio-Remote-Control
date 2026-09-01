using System.Globalization;
using Falcon.Core.Protocol;

namespace Falcon.Core.Radio;

/// <summary>
/// The radio's last-reported state — a mirror, not a model (plan §0). The
/// radio is the only state machine: values change ONLY when the radio reports
/// them, every scalar is <see cref="Confirmed{T}"/> (unconfirmed until
/// reported this session, so enum defaults can never leak into a display),
/// and the trigger table (plan §2.3 Q5) can mark values unconfirmed again
/// when an observed line implies a silent change.
/// Setters are internal; consumers read properties and subscribe to
/// <see cref="Changed"/> (raised on the parse thread — Prc138Radio marshals).
/// </summary>
public sealed class RadioState
{
    /// <summary>Raised on the parse thread whenever a property is applied.</summary>
    public event Action<RadioProperty>? Changed;

    private void Raise(RadioProperty p) => Changed?.Invoke(p);

    private bool Set<T>(ref Confirmed<T> field, T value, RadioProperty prop)
    {
        bool changed = !field.IsConfirmed || !EqualityComparer<T>.Default.Equals(field.Value, value);
        field = Confirmed<T>.Of(value);
        if (changed) Raise(prop);
        return changed;
    }

    private void Unconfirm<T>(ref Confirmed<T> field, RadioProperty prop)
    {
        if (!field.IsConfirmed) return;
        field = Confirmed<T>.Unconfirmed;
        Raise(prop);
    }

    // ---- General ---------------------------------------------------------

    private Confirmed<OperatingMode> _operatingMode;
    public Confirmed<OperatingMode> OperatingMode => _operatingMode;
    internal bool SetOperatingMode(OperatingMode v) => Set(ref _operatingMode, v, RadioProperty.OperatingMode);

    private Confirmed<PowerLevel> _powerLevel;
    public Confirmed<PowerLevel> PowerLevel => _powerLevel;
    internal bool SetPowerLevel(PowerLevel v) => Set(ref _powerLevel, v, RadioProperty.PowerLevel);

    private Confirmed<bool> _powerCutback;
    /// <summary>PA thermal management: true after POWER CUTBACK, false after POWER RESTORED.</summary>
    public Confirmed<bool> PowerCutback => _powerCutback;
    internal bool SetPowerCutback(bool v) => Set(ref _powerCutback, v, RadioProperty.PowerCutback);

    private Confirmed<KeylineState> _keyline;
    public Confirmed<KeylineState> Keyline => _keyline;
    internal bool SetKeyline(KeylineState v)
    {
        bool changed = Set(ref _keyline, v, RadioProperty.Keyline);
        if (v == KeylineState.Off && _isTuning) SetTuning(false);
        return changed;
    }

    private Confirmed<string> _batteryStatus;
    /// <summary>Verbatim BATTERY payload ("Status FULL 31.4V").</summary>
    public Confirmed<string> BatteryStatus => _batteryStatus;
    internal bool SetBatteryStatus(string v) => Set(ref _batteryStatus, v, RadioProperty.BatteryStatus);

    // ---- Tune lifecycle (session transients, latched from async lines) ----

    private bool _isTuning, _isTuneComplete, _isTuneMarginal, _isTuneFail;
    public bool IsTuning => _isTuning;
    public bool IsTuneComplete => _isTuneComplete;
    /// <summary>Marginal is a QUALIFIER on a completed tune, not a fourth outcome.</summary>
    public bool IsTuneMarginal => _isTuneMarginal;
    /// <summary>"TUNE FAULT" (this radio) / "FAIL" (Harris doc). A routine,
    /// recoverable outcome on this radio's flaky coupler module — recovery is
    /// the operator commanding another tune.</summary>
    public bool IsTuneFail => _isTuneFail;

    internal void SetTuning(bool v)
    {
        if (_isTuning != v) { _isTuning = v; Raise(RadioProperty.Tuning); }
        if (v)
        {
            SetTuneFlags(complete: false, marginal: false, fail: false);
            // Tuning transmits, but the tune lines carry NO keyline report —
            // the radio's keyline state is now UNREPORTED, so any previously
            // confirmed value goes back to unconfirmed. Keyline is confirmed
            // ONLY by actual KEY lines (plan §0: no inference of unreported
            // state — audit round 1, F1).
            Unconfirm(ref _keyline, RadioProperty.Keyline);
        }
    }

    internal void SetTuneComplete() { SetTuneFlags(true, false, false); EndTune(); }
    internal void SetTuneMarginal() { SetTuneFlags(true, true, false); EndTune(); }
    internal void SetTuneFail() { SetTuneFlags(false, false, true); EndTune(); }

    private void EndTune()
    {
        if (_isTuning) { _isTuning = false; Raise(RadioProperty.Tuning); }
        // No fabricated KEY OFF: the tune terminal line says nothing about
        // the keyline (F1). It stays unconfirmed until a real KEY line.
        Unconfirm(ref _keyline, RadioProperty.Keyline);
    }

    private void SetTuneFlags(bool complete, bool marginal, bool fail)
    {
        if (_isTuneComplete != complete) { _isTuneComplete = complete; Raise(RadioProperty.TuneComplete); }
        if (_isTuneMarginal != marginal) { _isTuneMarginal = marginal; Raise(RadioProperty.TuneMarginal); }
        if (_isTuneFail != fail) { _isTuneFail = fail; Raise(RadioProperty.TuneFail); }
    }

    // ---- SSB / channel domain ---------------------------------------------

    private Confirmed<string> _rxFrequency;
    /// <summary>8-digit Hz string as reported ("01600000").</summary>
    public Confirmed<string> RxFrequency => _rxFrequency;
    internal bool SetRxFrequency(string v) => SetTunedFrequency(ref _rxFrequency, v, RadioProperty.RxFrequency);

    private Confirmed<string> _txFrequency;
    public Confirmed<string> TxFrequency => _txFrequency;
    internal bool SetTxFrequency(string v) => SetTunedFrequency(ref _txFrequency, v, RadioProperty.TxFrequency);

    /// <summary>
    /// D16 (owner 2026-08-30) — A FREQUENCY CHANGE RESETS THE TUNE STATE.
    /// The coupler's tune is valid for the frequency it tuned AT: the radio
    /// keeps a per-frequency TUNE MEMORY and retunes itself at the next key-up
    /// on a frequency it has not tuned recently (probes P6/P6b — `NET 1` drew
    /// `TUNING COUPLER`/`TUNE COMPLETE`, `NET 0` back drew neither; and the
    /// P14c sounding run tuned on only 4 of its 11 channels). So the moment a
    /// CONFIRMED frequency moves, the latched outcome describes a tune the
    /// coupler will redo — and the spine chip must stop claiming it.
    /// <para>Clears the three OUTCOME flags only, through the existing
    /// <see cref="SetTuneFlags"/> path so the per-flag Raise semantics hold
    /// (nothing is raised when they are already clear — which is also what
    /// coalesces a paired RxFr/TxFr move into ONE set of notifications).
    /// <c>_isTuning</c> is deliberately untouched: it is governed by its own
    /// lines, and a frequency report landing mid-tune must not blank the
    /// on-air indicator (§9 B1) — the tune in flight is FOR the new
    /// frequency.</para>
    /// <para>Only a TRANSITION clears: the first confirmation of a session has
    /// no prior value (and clears nothing), and a re-report of the SAME
    /// frequency — every `SH` block re-reads both — must never blank a valid
    /// tune.</para>
    /// <para>Deliberately NOT keyed on <see cref="OperatingChannel"/>: the
    /// coupler is FREQUENCY-keyed, so a channel change that moves frequency
    /// clears via the `RxFr`/`TxFr` lines it draws, and a same-frequency
    /// channel change keeps the tune. If this radio is ever observed retuning
    /// on a same-frequency channel change, this is the decision to revisit.
    /// </para>
    /// </summary>
    private bool SetTunedFrequency(ref Confirmed<string> field, string v, RadioProperty prop)
    {
        bool moved = field.IsConfirmed && !string.Equals(field.Value, v, StringComparison.Ordinal);
        bool changed = Set(ref field, v, prop);
        if (moved) SetTuneFlags(complete: false, marginal: false, fail: false);
        return changed;
    }

    private Confirmed<int> _operatingChannel;
    public Confirmed<int> OperatingChannel => _operatingChannel;
    internal bool SetOperatingChannel(int v) => Set(ref _operatingChannel, v, RadioProperty.OperatingChannel);

    private Confirmed<ModulationMode> _modulationMode;
    public Confirmed<ModulationMode> ModulationMode => _modulationMode;
    internal bool SetModulationMode(ModulationMode v) => Set(ref _modulationMode, v, RadioProperty.ModulationMode);

    private Confirmed<string> _bandwidth;
    /// <summary>Bandwidth as reported by BAND lines, normalized to
    /// <see cref="Wire.BandwidthValues"/> form ("2.7").</summary>
    public Confirmed<string> Bandwidth => _bandwidth;
    internal bool SetBandwidth(string v) => Set(ref _bandwidth, v, RadioProperty.Bandwidth);

    private Confirmed<AgcSpeed> _agcSpeed;
    public Confirmed<AgcSpeed> AgcSpeed => _agcSpeed;
    internal bool SetAgcSpeed(AgcSpeed v) => Set(ref _agcSpeed, v, RadioProperty.AgcSpeed);

    private Confirmed<YesNo> _channelRxOnly;
    public Confirmed<YesNo> ChannelRxOnly => _channelRxOnly;
    internal bool SetChannelRxOnly(YesNo v) => Set(ref _channelRxOnly, v, RadioProperty.ChannelRxOnly);

    private Confirmed<FrequencyStep> _frequencyStep;
    /// <summary>STEP is RADIO state (INC/DEC tune by it); SSB-only.</summary>
    public Confirmed<FrequencyStep> FrequencyStep => _frequencyStep;
    internal bool SetFrequencyStep(FrequencyStep v) => Set(ref _frequencyStep, v, RadioProperty.FrequencyStep);

    private Confirmed<OnOff> _analogSquelch;
    /// <summary>Tracked for the FM-squelch compensation (Q5d) — armed only
    /// off a CONFIRMED On report, never a default.</summary>
    public Confirmed<OnOff> AnalogSquelch => _analogSquelch;
    internal bool SetAnalogSquelch(OnOff v) => Set(ref _analogSquelch, v, RadioProperty.AnalogSquelch);

    private Confirmed<string> _activeModem;
    /// <summary>Active modem as reported: "OFF" or the selection echo ("1 T39").
    /// Any change of a previously-reported value is trigger-table row (a):
    /// AGC and BAND were silently mutated (probe R8).</summary>
    public Confirmed<string> ActiveModem => _activeModem;
    internal bool SetActiveModem(string v) => Set(ref _activeModem, v, RadioProperty.ActiveModem);

    /// <summary>Trigger-table rows (a)–(c): mark the values an observed line
    /// says may have silently changed as unconfirmed until re-reported.</summary>
    internal void UnconfirmAgcAndBandwidth()
    {
        Unconfirm(ref _agcSpeed, RadioProperty.AgcSpeed);
        Unconfirm(ref _bandwidth, RadioProperty.Bandwidth);
    }

    internal void UnconfirmChannelDomain()
    {
        Unconfirm(ref _operatingChannel, RadioProperty.OperatingChannel);
        Unconfirm(ref _rxFrequency, RadioProperty.RxFrequency);
        Unconfirm(ref _txFrequency, RadioProperty.TxFrequency);
        UnconfirmAgcAndBandwidth();
    }

    /// <summary>
    /// CLONE ROUND 12 P4 — the DV SYNC unconfirm, from the graduated D1 matrix
    /// (protocol.md "Digital voice — the interaction matrix"). A `DV` change
    /// SILENTLY forces the modulation to USB (from AME/CW/FM), forces analog
    /// squelch ON, and moves the bandwidth in EVERY modulation — and the echo
    /// carries NO `MODE` line at all. So the moment a DV change is observed
    /// these three are values the radio has moved and nothing has reported:
    /// unconfirmed until the re-read lands.
    /// <para>DISPLAY-SCOPED. The FM-squelch compensation keeps its OWN
    /// last-reported analog-squelch memory in <c>Prc138Radio</c> — this method
    /// is about what the operator is SHOWN, and must never be read as "the
    /// radio's squelch state is unknown".</para>
    /// </summary>
    internal void UnconfirmDvForcedValues()
    {
        Unconfirm(ref _modulationMode, RadioProperty.ModulationMode);
        Unconfirm(ref _bandwidth, RadioProperty.Bandwidth);
        Unconfirm(ref _analogSquelch, RadioProperty.AnalogSquelch);
    }

    /// <summary>CLONE ROUND 12 P4, the other direction: modulation leaving
    /// USB/LSB silently AUTO-SUSPENDS digital voice, and returning silently
    /// AUTO-RESTORES it (probe R4) — neither is announced, so any changed
    /// `MODE` line makes the DV mirror unreported until it is re-read.</summary>
    internal void UnconfirmDigitalVoice() => Unconfirm(ref _digitalVoice, RadioProperty.DigitalVoice);

    // ---- Phase R settings mirrors (plan-gui-rejigger.md round 4) ----------
    // Only SETTINGs with CAPTURED answer shapes are mirrored; write-only
    // values (RWAS_KEY, FORCE_W, ENC_KEY slots) and uncaptured answers
    // (LIGHT, INTENSITY) deliberately are NOT — see
    // plan/phase-r-classification.md. Verbatim-string mirrors hold the
    // uppercased payload exactly as reported (display truth over re-mapping).
    // PREAMP/INTCOUPLER/KWATT joined the mirrored set in UI-tweaks round 3 on
    // OLD-APP-DERIVED (bench-unconfirmed) evidence — see the PROVISIONAL block
    // further down.

    private Confirmed<OnOff> _digitalVoice;
    /// <summary>DV as reported ("DV ON"/"DV OFF", probe R4). DV answers carry
    /// a DGT_SQUELCH rider line — mirrored separately (they are independent
    /// settings, protocol.md digital-squelch section).</summary>
    public Confirmed<OnOff> DigitalVoice => _digitalVoice;
    internal bool SetDigitalVoice(OnOff v) => Set(ref _digitalVoice, v, RadioProperty.DigitalVoice);

    private Confirmed<OnOff> _digitalSquelch;
    public Confirmed<OnOff> DigitalSquelch => _digitalSquelch;
    internal bool SetDigitalSquelch(OnOff v) => Set(ref _digitalSquelch, v, RadioProperty.DigitalSquelch);

    private Confirmed<string> _squelchLevel;
    /// <summary>SQ_LEVEL payload verbatim ("HIGH" is the only captured
    /// spelling — a string, not an enum, until LO/MED answers are captured).</summary>
    public Confirmed<string> SquelchLevel => _squelchLevel;
    internal bool SetSquelchLevel(string v) => Set(ref _squelchLevel, v, RadioProperty.SquelchLevel);

    private Confirmed<OnOff> _fmSquelch;
    public Confirmed<OnOff> FmSquelch => _fmSquelch;
    internal bool SetFmSquelch(OnOff v) => Set(ref _fmSquelch, v, RadioProperty.FmSquelch);

    private Confirmed<string> _fmSquelchType;
    /// <summary>FMSQ_TYPE payload verbatim (captured: "tone" → "TONE").</summary>
    public Confirmed<string> FmSquelchType => _fmSquelchType;
    internal bool SetFmSquelchType(string v) => Set(ref _fmSquelchType, v, RadioProperty.FmSquelchType);

    private Confirmed<OnOff> _fmTone;
    public Confirmed<OnOff> FmTone => _fmTone;
    internal bool SetFmTone(OnOff v) => Set(ref _fmTone, v, RadioProperty.FmTone);

    private Confirmed<string> _fmDeviation;
    /// <summary>FMDEV payload verbatim ("8.0").</summary>
    public Confirmed<string> FmDeviation => _fmDeviation;
    internal bool SetFmDeviation(string v) => Set(ref _fmDeviation, v, RadioProperty.FmDeviation);

    private Confirmed<string> _bfoOffset;
    /// <summary>BFO payload verbatim ("+0000").</summary>
    public Confirmed<string> BfoOffset => _bfoOffset;
    internal bool SetBfoOffset(string v) => Set(ref _bfoOffset, v, RadioProperty.BfoOffset);

    private Confirmed<string> _cwOffset;
    /// <summary>CWOFFSET payload verbatim ("0000").</summary>
    public Confirmed<string> CwOffset => _cwOffset;
    internal bool SetCwOffset(string v) => Set(ref _cwOffset, v, RadioProperty.CwOffset);

    private Confirmed<OnOff> _compression;
    public Confirmed<OnOff> Compression => _compression;
    internal bool SetCompression(OnOff v) => Set(ref _compression, v, RadioProperty.Compression);

    private Confirmed<string> _antenna;
    /// <summary>ANTENNA payload verbatim (captured: "auto" → "AUTO").</summary>
    public Confirmed<string> Antenna => _antenna;
    internal bool SetAntenna(string v) => Set(ref _antenna, v, RadioProperty.Antenna);

    private Confirmed<string> _retransmit;
    /// <summary>RETRANS payload verbatim ("DISABLED" is the only captured
    /// spelling — the ENABLED answer has never been captured).</summary>
    public Confirmed<string> Retransmit => _retransmit;
    internal bool SetRetransmit(string v) => Set(ref _retransmit, v, RadioProperty.Retransmit);

    private Confirmed<EnabledDisabled> _rwas;
    /// <summary>RWAS ENABLED/DISABLED (both answers bench-documented).
    /// Enabling/disabling forces all three squelches ON and the radio
    /// REPORTS them back alongside — the mirror stays truthful with no
    /// compensation (protocol.md RWAS section).</summary>
    public Confirmed<EnabledDisabled> Rwas => _rwas;
    internal bool SetRwas(EnabledDisabled v) => Set(ref _rwas, v, RadioProperty.Rwas);

    private Confirmed<EnabledDisabled> _unkeyMask;
    public Confirmed<EnabledDisabled> UnkeyMask => _unkeyMask;
    internal bool SetUnkeyMask(EnabledDisabled v) => Set(ref _unkeyMask, v, RadioProperty.UnkeyMask);

    private Confirmed<string> _avs;
    /// <summary>AVS payload verbatim ("OFF", "ON", or "NOT INSTALLED").
    /// The SH block prints "AVS OFF" even on a cardless radio — only the
    /// direct query reports availability (protocol.md COMSEC section); the
    /// mirror shows whichever the radio last said.</summary>
    public Confirmed<string> Avs => _avs;
    internal bool SetAvs(string v) => Set(ref _avs, v, RadioProperty.Avs);

    private Confirmed<OnOff> _encryption;
    /// <summary>ENCRYPT ON/OFF (SH + ENCR answers).</summary>
    public Confirmed<OnOff> Encryption => _encryption;
    internal bool SetEncryption(OnOff v) => Set(ref _encryption, v, RadioProperty.Encryption);

    private Confirmed<string> _encryptionAvailability;
    /// <summary>ENCRYPTION payload verbatim ("INSTALLED"/"NOT INSTALLED" —
    /// the direct-query availability line, session-14).</summary>
    public Confirmed<string> EncryptionAvailability => _encryptionAvailability;
    internal bool SetEncryptionAvailability(string v) => Set(ref _encryptionAvailability, v, RadioProperty.EncryptionAvailability);

    private Confirmed<string> _currentEncryptionKey;
    /// <summary>CUR_KEY payload verbatim (slot number or "none").</summary>
    public Confirmed<string> CurrentEncryptionKey => _currentEncryptionKey;
    internal bool SetCurrentEncryptionKey(string v) => Set(ref _currentEncryptionKey, v, RadioProperty.CurrentEncryptionKey);

    private Confirmed<int> _rfGain;
    /// <summary>RF gain 0-100 (RFG lines — they ride along with AGC answers,
    /// probe R4).</summary>
    public Confirmed<int> RfGain => _rfGain;
    internal bool SetRfGain(int v) => Set(ref _rfGain, v, RadioProperty.RfGain);

    private Confirmed<int> _contrast;
    /// <summary>CONTRAST 0-8 (answer shape "CONTRAST nn", protocol.md
    /// sentinel table).</summary>
    public Confirmed<int> Contrast => _contrast;
    internal bool SetContrast(int v) => Set(ref _contrast, v, RadioProperty.Contrast);

    private Confirmed<OnOff> _beep;
    public Confirmed<OnOff> Beep => _beep;
    internal bool SetBeep(OnOff v) => Set(ref _beep, v, RadioProperty.Beep);

    private Confirmed<string> _prePostFilter, _prePostRxAntenna, _prePostScanRate;
    /// <summary>PREPOST dump values verbatim (session-20: FILTER ENABLE /
    /// RXANTENNA DISABLE / SCAN SLOW).</summary>
    public Confirmed<string> PrePostFilter => _prePostFilter;
    public Confirmed<string> PrePostRxAntenna => _prePostRxAntenna;
    public Confirmed<string> PrePostScanRate => _prePostScanRate;
    internal bool SetPrePostFilter(string v) => Set(ref _prePostFilter, v, RadioProperty.PrePostFilter);
    internal bool SetPrePostRxAntenna(string v) => Set(ref _prePostRxAntenna, v, RadioProperty.PrePostRxAntenna);
    internal bool SetPrePostScanRate(string v) => Set(ref _prePostScanRate, v, RadioProperty.PrePostScanRate);

    // ---- UI-tweaks round-3 V7: PROVISIONAL mirrors (old-app-derived) ------
    // PREAMP / INTCOUPLER / KWATT answer shapes come from the WinForms
    // Falcon-Radio-Remote-Control's parser table (src/Falcon.Core/Protocol/
    // ResponseParser.cs:272-274), NOT from this project's bench — see
    // docs/protocol.md "Old-app-derived SSB query set (PROVISIONAL)" and the
    // matching CONFIRM items in docs/bench-checklist.md. Stored VERBATIM
    // (uppercased payload) exactly like ANTENNA/RETRANS/AVS: the old app maps
    // them onto enums whose spellings ("ENABLED"/"BYPASSED"/"YES"/"NO") are
    // themselves unconfirmed on this radio, so mapping here would invent a
    // fact. Whatever the radio says is what the display shows.

    private Confirmed<string> _rxPreamp, _internalCoupler, _oneKilowattPa;
    /// <summary>PREAMP payload verbatim (old-app-derived: "ENABLED"/"BYPASSED").</summary>
    public Confirmed<string> RxPreamp => _rxPreamp;
    /// <summary>INTCOUPLER payload verbatim (old-app-derived: "ENABLED"/"BYPASSED").</summary>
    public Confirmed<string> InternalCoupler => _internalCoupler;
    /// <summary>KWATT payload verbatim (old-app-derived: "YES"/"NO").</summary>
    public Confirmed<string> OneKilowattPa => _oneKilowattPa;
    internal bool SetRxPreamp(string v) => Set(ref _rxPreamp, v, RadioProperty.RxPreamp);
    internal bool SetInternalCoupler(string v) => Set(ref _internalCoupler, v, RadioProperty.InternalCoupler);
    internal bool SetOneKilowattPa(string v) => Set(ref _oneKilowattPa, v, RadioProperty.OneKilowattPa);

    // ---- UI-tweaks round-4 AC: PROVISIONAL mirrors (old-app-derived) ------
    // LIGHT / INTENSITY answer payloads are mined from the WinForms
    // Falcon-Radio-Remote-Control's parser table (old repo
    // src/Falcon.Core/Protocol/ResponseParser.cs:269 and :271, spellings in
    // its Wire.cs:182-186 BacklightFunctions OFF|MOMENTARY and Wire.cs:187-197
    // Intensities "00".."08"), NOT from this project's bench — see
    // docs/protocol.md's round-4 provisional subsection and the matching
    // CONFIRM items in docs/bench-checklist.md. Stored VERBATIM, the round-3
    // discipline: the old app maps both onto enums whose spellings are exactly
    // what the bench has to confirm (INTENSITY's zero-padding especially), so
    // mapping here would invent a fact.

    private Confirmed<string> _backlightFunction, _backlightIntensity;
    /// <summary>LIGHT payload verbatim (old-app-derived: "OFF"/"MOMENTARY").</summary>
    public Confirmed<string> BacklightFunction => _backlightFunction;
    /// <summary>INTENSITY payload verbatim (old-app-derived: "00".."08").</summary>
    public Confirmed<string> BacklightIntensity => _backlightIntensity;
    internal bool SetBacklightFunction(string v) => Set(ref _backlightFunction, v, RadioProperty.BacklightFunction);
    internal bool SetBacklightIntensity(string v) => Set(ref _backlightIntensity, v, RadioProperty.BacklightIntensity);

    private Confirmed<string> _radioTimeOfDay;
    /// <summary>The radio clock's time of day, verbatim TIME payload
    /// ("20:37:12") — from TI answers and TIME/DAT/DAY set echoes (each
    /// answers the full DAY/DATE/TIME triplet). DATE/DAY stay unmirrored:
    /// v1 displays TOD only (Stage 5 HOP pane Time section).</summary>
    public Confirmed<string> RadioTimeOfDay => _radioTimeOfDay;
    internal bool SetRadioTimeOfDay(string v) => Set(ref _radioTimeOfDay, v, RadioProperty.RadioTimeOfDay);

    // ---- Channel dump (DI) — copy-on-write -------------------------------

    /// <summary>
    /// Raw DI lines ("00 RxFr 04123000 TxFr … RXONLY NO"), one per channel,
    /// UPSERT-KEYED on the leading channel number.
    ///
    /// <para><b>Round 11 §8 — the keyed change.</b> This list used to be
    /// APPEND-only with the builder CLEARING it before every <c>DI</c>, so a
    /// targeted <c>DI n n</c> left the mirror holding exactly one channel and
    /// every consumer needing more than one had to accumulate app-side. The
    /// LQA report's per-channel RX/TX reads made that untenable: they fire one
    /// targeted read per named channel and every answer wiped the last one.
    /// A targeted answer now REPLACES that channel's row and keeps its
    /// siblings; the BULK refresh (<c>SsbController.DisplayAllChannels</c>)
    /// clears EXPLICITLY first, so "Refresh starts clean" survives as a
    /// deliberate gesture instead of an accident of the read path.</para>
    /// </summary>
    public IReadOnlyList<string> ChannelList { get; private set; } = [];
    private readonly object _channelListLock = new();

    internal void UpsertChannelLine(string line)
    {
        var number = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        lock (_channelListLock)
        {
            int existing = number is null ? -1 : IndexOfKeyed(ChannelList, number);
            if (existing >= 0)
            {
                var copy = ChannelList.ToArray();
                copy[existing] = line;
                ChannelList = copy;
            }
            else
            {
                ChannelList = [.. ChannelList, line];
            }
        }
        Raise(RadioProperty.ChannelList);
    }

    /// <summary>Index of the row whose FIRST whitespace-separated token equals
    /// <paramref name="key"/> — the shared keying rule for the two raw-line
    /// mirrors (channels by channel number, presets by preset number).</summary>
    private static int IndexOfKeyed(IReadOnlyList<string> lines, string key)
    {
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == key)
                return i;
        return -1;
    }

    internal void ClearChannelList()
    {
        lock (_channelListLock) { ChannelList = []; }
        Raise(RadioProperty.ChannelList);
    }

    // ---- Modem preset listing (MODEM PRE) — copy-on-write (round-8 EE) ---

    /// <summary>Raw stored-preset listing lines, "PRESET" stripped — one per
    /// stored preset, e.g. "1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone
    /// INTER long". UPSERT-keyed on the leading preset number (unlike the
    /// append-only ChannelList): the programming ECHO is the same listing
    /// form, so a Store's read-back replaces that preset's row instead of
    /// duplicating it.</summary>
    public IReadOnlyList<string> ModemPresets { get; private set; } = [];
    private readonly object _modemPresetsLock = new();

    /// <summary>
    /// Apply one stored-preset line, keyed on the preset number.
    ///
    /// <para><b>Round 11 §8 — the fields mirror NEVER CLEARS.</b> It used to be
    /// wiped by the bulk read before every <c>MODEM PRE</c>, which made the
    /// three preset states unrepresentable: a preset absent from the bulk
    /// listing is DISABLED, not unknown, and the targeted read
    /// (<c>MODEM PRE n</c>) — the only way to see a disabled preset's fields —
    /// had its answers thrown away by the next bulk read. The fields mirror is
    /// now a pure keyed upsert; ENABLED/DISABLED lives in its own
    /// <see cref="ModemPresetPresence"/> store, committed atomically by the
    /// sentinel-scoped presence operation.</para>
    /// </summary>
    internal void UpsertModemPresetLine(string line)
    {
        var number = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        bool published = false;
        lock (_modemPresetsLock)
        {
            // THE PRESENCE WINDOW TOUCHES NOTHING BUT THE ENABLED SET (§8,
            // operation-wide — audit round 1, BLOCKER 2). A row arriving here
            // inside a presence window is the bulk listing's own, and it
            // contributes its NUMBER and nothing else.
            //
            // Why the fields it also carries are DROPPED rather than upserted:
            // the presence read is not atomic until its sentinel, so a listing
            // whose sentinel is swallowed would otherwise leave a PARTIAL set
            // of bulk field rows published beside older targeted ones — a
            // mirror of mixed-time data with nothing marking which row came
            // from when. Fields have exactly ONE provenance, the targeted read
            // (`MODEM PRE n`), whose window is unambiguous by construction.
            // ---- THE BAND FILTER (audit round 2, MAJOR 2) ------------------
            // A READ WINDOW ADMITS ONLY ROWS OF THE BAND IT WAS ASKED FOR.
            // Every modem read now carries the prompt it went out at, and the
            // two bands are disjoint (0-6 at `SSB>`/`ALE>`, 7-9 at `HOP>`), so
            // a row naming the OTHER band's preset cannot be an answer to THIS
            // question — it is a straggler from the previous window, and the
            // radio has already proved it can deliver one late.
            //
            // Left in, it was a FALSE REPORT rather than a stale one: a late
            // `MODEM PRESET 1 …` inside a `HOP>` listing committed
            // `Enabled = [1]` under `Scope = Hop`, which every consumer then
            // read as "7, 8 and 9 are all disabled" — the card rendered it and
            // the wheel obeyed it by going OFF-only.
            //
            // The row is DISCARDED FROM THE WINDOW, not from the world: it
            // already reached the Console through the raw `Rx` line, which is
            // where the evidence belongs (constitution §3.2).
            if (_modemPresence.ActiveScope is { } windowScope
                && number is not null
                && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowPreset)
                && !ModemPresetScope.Covers(windowScope, rowPreset))
            {
                // Out of band, either kind of window: no enabled-set entry and
                // no fields upsert.
            }
            else if (_modemPresence.ActiveKind == ModemReadKind.Presence)
            {
                if (number is not null
                    && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int enabled))
                    _modemPresenceAnswers?.Add(enabled);
            }
            else
            {
                int existing = number is null ? -1 : IndexOfKeyed(ModemPresets, number);
                if (existing >= 0)
                {
                    var copy = ModemPresets.ToArray();
                    copy[existing] = line;
                    ModemPresets = copy;
                }
                else
                {
                    ModemPresets = [.. ModemPresets, line];
                }
                published = true;
            }
        }
        if (published) Raise(RadioProperty.ModemPresets);
    }

    // ---- Modem preset PRESENCE + the ONE serialized modem read queue ------
    // (round 11 §8). Targeted and bulk answers share an IDENTICAL line shape,
    // so their windows must never overlap — otherwise a targeted row could be
    // counted as "listed by the bulk", i.e. as ENABLED. All modem reads
    // therefore run as operations on ONE single-slot store queue (the AleState
    // idiom): the list tab's seven-read batch is itself ONE sentinel-completed
    // operation, and the presence operation dispatches only after the active
    // operation's sentinel answers — and vice versa.

    /// <summary>Where a preset presence read has got to.</summary>
    public enum PresenceState
    {
        /// <summary>No presence read has completed this session.</summary>
        Unknown,
        /// <summary>A presence read is on the wire; the previous set (if any)
        /// still stands.</summary>
        InFlight,
        /// <summary>A presence read committed: <see cref="Presence.Enabled"/>
        /// is the radio's enabled set as of that read.</summary>
        Completed,
    }

    /// <summary>The bulk <c>MODEM PRE</c> listing's meaning: the presets it
    /// lists are ENABLED, and a preset it omits is DISABLED — the ONLY captured
    /// enabled/disabled signal on this radio (the targeted read does not echo
    /// EN/DIS at all). Three states, so a display can say "—" until a read has
    /// actually completed rather than calling every preset disabled.</summary>
    /// <param name="State">How far the last presence read got.</param>
    /// <param name="Enabled">The preset numbers the listing named.</param>
    /// <param name="Scope">CLONE-FIELD ROUND 2 F10 — <b>the PROMPT the listing
    /// was read at</b>, or null when nothing has committed. The bulk listing is
    /// PROMPT-SCOPED: at <c>SSB&gt;</c>/<c>ALE&gt;</c> it names the enabled
    /// presets among 0-6, at <c>HOP&gt;</c> the enabled ones among 7-9 (P5,
    /// transcript
    /// <c>bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl</c>).
    /// Without this key a HOP listing's empty set reads as "0-6 are all
    /// disabled" — which is exactly the misreading round 13's T1 probe
    /// recorded. Consumers ask <see cref="Covers"/>, never
    /// <see cref="PresenceState.Completed"/> on its own.</param>
    public sealed record Presence(
        PresenceState State, IReadOnlyList<int> Enabled, OperatingMode? Scope = null)
    {
        /// <summary>This committed set is ABOUT <paramref name="mode"/>'s
        /// preset band — the only condition under which <see cref="Enabled"/>
        /// answers a question about it. SSB and ALE share one band, so a
        /// listing read at either covers both.</summary>
        public bool Covers(OperatingMode mode)
            => State == PresenceState.Completed
               && Scope is { } scope
               && ModemPresetScope.SameScope(scope, mode);
    }

    private Presence _modemPresetPresence = new(PresenceState.Unknown, []);

    /// <summary>The last COMMITTED presence — what a faulted read restores.
    /// Held separately so "fault preserves prior" is a restore of a recorded
    /// value rather than an inference from the in-flight one.</summary>
    private Presence _committedModemPresence = new(PresenceState.Unknown, []);

    /// <summary>The preset presence store — see <see cref="Presence"/>.</summary>
    public Presence ModemPresetPresence => _modemPresetPresence;

    private enum ModemReadKind { None, Targeted, Presence }

    private sealed class ModemQueue
    {
        public long ActiveId;
        public ModemReadKind ActiveKind;
        /// <summary>Pending TARGETED presets, unioned across coalesced
        /// requests (nothing pending when empty).</summary>
        public SortedSet<int> PendingPresets = [];
        public long PendingTargetedId;
        /// <summary>A presence read is queued behind the active operation.</summary>
        public long PendingPresenceId;

        /// <summary>
        /// THE SCOPE OF THE ACTIVE OPERATION, whichever kind it is (audit round
        /// 2, MAJOR 2). Every modem read is now asked FOR a prompt band, and
        /// the window it opens ADMITS ONLY THAT BAND'S ROWS: a late
        /// <c>MODEM PRESET 1 …</c> arriving inside a <c>HOP&gt;</c> listing
        /// used to commit <c>Enabled = [1]</c> under <c>Scope = Hop</c>, which
        /// reads as "7, 8 and 9 are all disabled" — a false report the card
        /// rendered and the wheel obeyed by going OFF-only.
        /// </summary>
        public OperatingMode? ActiveScope;

        /// <summary>The queued TARGETED batch's own scope — same ownership rule
        /// as <see cref="PendingPresenceScope"/>.</summary>
        public OperatingMode? PendingTargetedScope;
        /// <summary>THE QUEUED PRESENCE READ OWNS ITS SCOPE (audit round 1,
        /// MAJOR 1). The prompt a queued read was asked FOR has to travel with
        /// it: promotion used to leave <c>_modemPresenceScope</c> holding
        /// whatever the LAST DIRECTLY-DISPATCHED read had set, so a HOP listing
        /// queued behind a targeted read committed with the previous SSB scope
        /// — a set labelled as covering a band it says nothing about, which is
        /// worse than no set at all.</summary>
        public OperatingMode? PendingPresenceScope;
    }

    private readonly ModemQueue _modemPresence = new();
    private readonly object _modemQueueLock = new();
    private List<int>? _modemPresenceAnswers;
    private long _nextModemReadId;
    private AleReadCompletion _lastModemRead;

    /// <summary>Completion record of the last modem read operation (targeted
    /// batch or presence) — matched to a request by id equality.</summary>
    public AleReadCompletion LastModemRead => _lastModemRead;

    /// <summary>
    /// The prompt band the presence listing the radio will answer NEXT was
    /// asked for — the QUEUED one if there is one, else the ACTIVE one, else
    /// null (audit round 2, MAJOR 1). It is what lets a consumer tell "a read
    /// for MY band is already on its way" from "one is on its way for the
    /// OTHER band, and mine still has to be asked for".
    /// </summary>
    public OperatingMode? ModemPresenceReadScope
    {
        get
        {
            lock (_modemQueueLock)
            {
                if (_modemPresence.PendingPresenceId != 0) return _modemPresence.PendingPresenceScope;
                return _modemPresence.ActiveKind == ModemReadKind.Presence
                    ? _modemPresence.ActiveScope
                    : null;
            }
        }
    }

    /// <summary>Request a TARGETED field read of <paramref name="presets"/>
    /// (one <c>MODEM PRE n</c> per preset + ONE closing sentinel — the whole
    /// batch is a single operation). <paramref name="dispatchPresets"/>
    /// non-null means this call began the operation.</summary>
    internal long RequestModemTargetedRead(
        OperatingMode scope, IReadOnlyList<int> presets, out int[]? dispatchPresets)
    {
        lock (_modemQueueLock)
        {
            if (_modemPresence.ActiveKind != ModemReadKind.None)
            {
                dispatchPresets = null;
                if (_modemPresence.PendingTargetedId == 0)
                    _modemPresence.PendingTargetedId = ++_nextModemReadId;
                // COALESCE WITHIN A BAND, REPLACE ACROSS ONE (audit round 2,
                // MAJOR 2's family). Two requests for the same prompt union
                // their presets — that is the round-11 coalescing this queue
                // was built for. Two requests for DIFFERENT prompts cannot:
                // the earlier band's numbers are not askable at the later
                // band's prompt, and unioning them would send `MODEM PRE 0`
                // at `HOP>`. The later request is the current one, so it wins
                // outright.
                if (_modemPresence.PendingTargetedScope is { } pending
                    && !ModemPresetScope.SameScope(pending, scope))
                    _modemPresence.PendingPresets.Clear();
                foreach (var p in presets) _modemPresence.PendingPresets.Add(p);
                _modemPresence.PendingTargetedScope = scope;
                return _modemPresence.PendingTargetedId;
            }

            _modemPresence.ActiveId = ++_nextModemReadId;
            _modemPresence.ActiveKind = ModemReadKind.Targeted;
            _modemPresence.ActiveScope = scope;
            dispatchPresets = [.. presets];
            return _modemPresence.ActiveId;
        }
    }

    /// <summary>Request the PRESENCE read (bare <c>MODEM PRE</c> + sentinel).
    /// <paramref name="dispatch"/> true means this call began it.</summary>
    internal long RequestModemPresenceRead(OperatingMode scope, out bool dispatch)
    {
        long readId;
        lock (_modemQueueLock)
        {
            if (_modemPresence.ActiveKind != ModemReadKind.None)
            {
                dispatch = false;
                if (_modemPresence.PendingPresenceId == 0)
                    _modemPresence.PendingPresenceId = ++_nextModemReadId;
                // The scope travels with the QUEUED entry, and a later
                // requester's scope wins: the read has not gone out yet, so the
                // freshest statement about the prompt is the one closest to the
                // dispatch that will actually carry it. (Coalescing keeps ONE
                // wire line and ONE id — only the label moves.)
                _modemPresence.PendingPresenceScope = scope;
                return _modemPresence.PendingPresenceId;
            }

            _modemPresence.ActiveId = ++_nextModemReadId;
            _modemPresence.ActiveKind = ModemReadKind.Presence;
            _modemPresenceAnswers = [];
            // F10: the scope is the prompt at the moment the bare MODEM PRE
            // goes out — which is when the question is actually asked. It is a
            // NON-NULLABLE argument since audit round 2 (MAJOR 1): an unscoped
            // read is no longer a thing this seam can be asked for.
            _modemPresence.ActiveScope = scope;
            BeginPresenceLocked();
            dispatch = true;
            readId = _modemPresence.ActiveId;
        }
        Raise(RadioProperty.ModemPresetPresence);   // Completed/Unknown → InFlight
        return readId;
    }

    /// <summary>Caller holds <see cref="_modemQueueLock"/>. Marking IN-FLIGHT
    /// tells a consumer a fresher answer is coming, which is what makes the
    /// display say "—" instead of an enabled/disabled it can no longer vouch
    /// for. The caller MUST raise <see cref="RadioProperty.ModemPresetPresence"/>
    /// once it is outside the lock — a state change nobody is told about is a
    /// display stuck on the old answer (audit round 1, MAJOR 4); the two call
    /// sites do exactly that.</summary>
    private void BeginPresenceLocked()
        => _modemPresetPresence = _modemPresetPresence with { State = PresenceState.InFlight };

    /// <summary>Commit the modem operation <paramref name="readId"/>. A
    /// PRESENCE operation's answered sentinel commits its enabled set
    /// ATOMICALLY and touches nothing else; an unanswered one restores the
    /// PRIOR presence exactly. A TARGETED operation publishes nothing here —
    /// its rows already upserted the fields mirror on arrival — it only
    /// completes, releasing the queue. A silence abandons EVERY pending
    /// operation and completes each of them unanswered.</summary>
    internal void CompleteModemRead(long readId, bool answered,
        out long nextReadId, out int[]? nextPresets, out bool dispatchPresence)
    {
        nextReadId = 0;
        nextPresets = null;
        dispatchPresence = false;

        bool presenceChanged = false;
        lock (_modemQueueLock)
        {
            if (_modemPresence.ActiveId != readId) return;

            if (_modemPresence.ActiveKind == ModemReadKind.Presence)
            {
                if (answered)
                    _committedModemPresence = new Presence(
                        PresenceState.Completed, [.. (_modemPresenceAnswers ?? []).Distinct().Order()],
                        _modemPresence.ActiveScope);
                // Answered: the new set, atomically. Faulted: EXACTLY the last
                // committed value — a swallowed listing must never be read as
                // "nothing is enabled".
                _modemPresetPresence = _committedModemPresence;
                _modemPresenceAnswers = null;
                presenceChanged = true;
            }
        }

        if (presenceChanged) Raise(RadioProperty.ModemPresetPresence);

        _lastModemRead = new AleReadCompletion(readId, answered);
        Raise(RadioProperty.ModemPresetRead);

        // EVERY abandoned operation is completed, not just one (audit round 1,
        // BLOCKER 1): this queue can hold a pending TARGETED batch and a
        // pending PRESENCE read at the same time, and a silence clears BOTH.
        // Reporting only one left the other's requester waiting forever for a
        // completion that could never come — the exact failure the
        // abandon-rather-than-promote rule exists to prevent.
        var abandonedIds = new List<long>(2);
        bool promotedToInFlight = false;
        lock (_modemQueueLock)
        {
            if (_modemPresence.ActiveId != readId) return;
            _modemPresence.ActiveId = 0;
            _modemPresence.ActiveKind = ModemReadKind.None;
            // The window is shut, so its band no longer filters anything: a row
            // arriving with NO window open has no question to be an answer to
            // and takes the unfiltered path, as it always did (the `MODEM SH`
            // and programming-echo route).
            _modemPresence.ActiveScope = null;

            if (!answered)
            {
                // Same abandon rule as the ALE stores: an unanswered sentinel
                // means the dead operation's rows may still be in flight, and
                // a bulk row is indistinguishable from a targeted one — which
                // is exactly the attribution this queue exists to protect.
                if (_modemPresence.PendingTargetedId != 0) abandonedIds.Add(_modemPresence.PendingTargetedId);
                if (_modemPresence.PendingPresenceId != 0) abandonedIds.Add(_modemPresence.PendingPresenceId);
                _modemPresence.PendingTargetedId = 0;
                _modemPresence.PendingPresenceId = 0;
                _modemPresence.PendingPresenceScope = null;
                _modemPresence.PendingTargetedScope = null;
                _modemPresence.PendingPresets.Clear();
            }
            else if (_modemPresence.PendingTargetedId != 0)
            {
                // FIELDS BEFORE PRESENCE, deliberately: the list tab queues the
                // batch and then the presence read, and the state column is
                // only meaningful beside rows that have fields.
                _modemPresence.ActiveId = _modemPresence.PendingTargetedId;
                _modemPresence.ActiveKind = ModemReadKind.Targeted;
                // The promoted batch commits the scope IT was queued with
                // (audit round 1's rule, extended to the targeted kind in
                // round 2 so its window can band-filter too).
                _modemPresence.ActiveScope = _modemPresence.PendingTargetedScope;
                _modemPresence.PendingTargetedScope = null;
                nextPresets = [.. _modemPresence.PendingPresets];
                nextReadId = _modemPresence.ActiveId;
                _modemPresence.PendingTargetedId = 0;
                _modemPresence.PendingPresets.Clear();
            }
            else if (_modemPresence.PendingPresenceId != 0)
            {
                _modemPresence.ActiveId = _modemPresence.PendingPresenceId;
                _modemPresence.ActiveKind = ModemReadKind.Presence;
                _modemPresenceAnswers = [];
                // THE PROMOTED READ COMMITS THE SCOPE IT WAS QUEUED WITH, never
                // the field's leftover (audit round 1, MAJOR 1).
                _modemPresence.ActiveScope = _modemPresence.PendingPresenceScope;
                _modemPresence.PendingPresenceScope = null;
                BeginPresenceLocked();
                // A PROMOTED presence read opens exactly the same window a
                // directly-requested one does, so it owes exactly the same
                // notification (audit round 2, MAJOR-B): without it a consumer
                // kept rendering the previous Enabled/Disabled for the whole
                // new window. Raised OUTSIDE the lock, like the direct path.
                promotedToInFlight = true;
                nextReadId = _modemPresence.ActiveId;
                dispatchPresence = true;
                _modemPresence.PendingPresenceId = 0;
            }
        }

        if (promotedToInFlight) Raise(RadioProperty.ModemPresetPresence);

        foreach (var abandoned in abandonedIds)
        {
            _lastModemRead = new AleReadCompletion(abandoned, false);
            Raise(RadioProperty.ModemPresetRead);
        }
    }

    /// <summary>True while a TARGETED modem read owns the queue — the pin's
    /// observable: a presence read can never be dispatched here, so a targeted
    /// row can never join the enabled set.</summary>
    public bool IsModemTargetedReadActive
    {
        get { lock (_modemQueueLock) return _modemPresence.ActiveKind == ModemReadKind.Targeted; }
    }

    /// <summary>True while the PRESENCE read owns the queue.</summary>
    public bool IsModemPresenceReadActive
    {
        get { lock (_modemQueueLock) return _modemPresence.ActiveKind == ModemReadKind.Presence; }
    }

    // ====================================================================
    // OPERATOR LOCKOUTS (clone round 12 §3) — the sentinel-bracketed
    // read-store, keyed (family, section, item).
    //
    // WHY A READ OPERATION AND NOT LINE-BY-LINE UPSERTS: the two reports are
    // GLOBAL dumps answered from ONE prompt (captured 2026-08-18,
    // bench/transcripts/r11-lockouts-*), and the SET ECHO has exactly the same
    // line shape as a report row — but the echo carries NO section header, so
    // its section is unattributable from the line alone. Rows therefore only
    // mirror INSIDE a read window; a line outside one marks the mirror UNREAD
    // (something changed and nothing can say what), which the campaign's
    // re-read then resolves. Nothing is ever inferred from an echo.
    // ====================================================================

    private LockoutMirror _lockouts = new(LockoutReadState.Unknown, []);

    /// <summary>The last COMMITTED lockout mirror — what a faulted read
    /// restores. Held separately so "fault preserves prior" is a restore of a
    /// recorded value, not an inference from the in-flight one.</summary>
    private LockoutMirror _committedLockouts = new(LockoutReadState.Unknown, []);

    /// <summary>The operator lockout mirror. THREE states:
    /// <see cref="LockoutReadState.Unknown"/> (never read this session),
    /// <see cref="LockoutReadState.InFlight"/> (a read is on the wire; the
    /// previous rows still stand), and <see cref="LockoutReadState.Completed"/>
    /// with the radio's rows.</summary>
    public LockoutMirror Lockouts => _lockouts;

    private long _lockoutActiveId, _lockoutPendingId, _nextLockoutReadId;
    private List<LockoutRow>? _lockoutAnswers;
    private AleReadCompletion _lastLockoutRead;
    private readonly object _lockoutLock = new();

    /// <summary>Completion record of the last lockout read operation.</summary>
    public AleReadCompletion LastLockoutRead => _lastLockoutRead;

    /// <summary>True while a lockout read owns the window — the observable the
    /// "an echo outside a window does not mirror" pin needs.</summary>
    public bool IsLockoutReadActive { get { lock (_lockoutLock) return _lockoutActiveId != 0; } }

    /// <summary>Request the lockout read (bare <c>PROGRAM</c> + bare
    /// <c>SELECT</c> + a closing sentinel — ONE operation, because both reports
    /// answer from the same prompt). <paramref name="dispatch"/> true means this
    /// call BEGAN it; false means it coalesced into the single pending
    /// operation, whose id is returned.</summary>
    internal long RequestLockoutRead(out bool dispatch)
    {
        long readId;
        lock (_lockoutLock)
        {
            if (_lockoutActiveId != 0)
            {
                dispatch = false;
                if (_lockoutPendingId == 0) _lockoutPendingId = ++_nextLockoutReadId;
                return _lockoutPendingId;
            }
            _lockoutActiveId = ++_nextLockoutReadId;
            _lockoutAnswers = [];
            _lockouts = _lockouts with { State = LockoutReadState.InFlight };
            dispatch = true;
            readId = _lockoutActiveId;
        }
        Raise(RadioProperty.Lockouts);      // → InFlight, outside the lock
        return readId;
    }

    /// <summary>Commit the lockout operation. An ANSWERED sentinel publishes the
    /// accumulated rows atomically; an unanswered one restores EXACTLY the last
    /// committed mirror (a swallowed dump must never read as "no rows").</summary>
    internal void CompleteLockoutRead(long readId, bool answered, out long nextReadId, out bool dispatchNext)
    {
        nextReadId = 0;
        dispatchNext = false;

        lock (_lockoutLock)
        {
            if (_lockoutActiveId != readId) return;
            if (answered && _lockoutAnswers is { } rows)
                _committedLockouts = new LockoutMirror(LockoutReadState.Completed, rows);
            _lockouts = _committedLockouts;
            _lockoutAnswers = null;
        }
        Raise(RadioProperty.Lockouts);

        _lastLockoutRead = new AleReadCompletion(readId, answered);
        Raise(RadioProperty.LockoutRead);

        long abandonedId = 0;
        lock (_lockoutLock)
        {
            if (_lockoutActiveId != readId) return;
            _lockoutActiveId = 0;
            if (_lockoutPendingId == 0) return;

            // The standing rule for every read store here: a pending operation
            // may only be promoted across an ANSWERED one — an unanswered
            // sentinel leaves the dead operation's rows possibly still in
            // flight, and nothing distinguishes them from the next read's own.
            if (!answered)
            {
                abandonedId = _lockoutPendingId;
                _lockoutPendingId = 0;
            }
            else
            {
                _lockoutActiveId = _lockoutPendingId;
                _lockoutPendingId = 0;
                _lockoutAnswers = [];
                nextReadId = _lockoutActiveId;
                dispatchNext = true;
            }
        }
        if (abandonedId != 0)
        {
            _lastLockoutRead = new AleReadCompletion(abandonedId, false);
            Raise(RadioProperty.LockoutRead);
        }
    }

    /// <summary>
    /// Apply one lockout report row. Returns FALSE when the row is outside the
    /// CLOSED 22-item inventory — the caller then surfaces the line through the
    /// unrecognized path, so a twenty-third item is a loud fact rather than a
    /// silently grown mirror (invariant 2).
    /// <para>Inside a read window the row ACCUMULATES. Outside one the line is
    /// a SET ECHO whose section cannot be attributed, so it publishes nothing
    /// and instead marks the mirror UNREAD.</para>
    /// </summary>
    internal bool ApplyLockoutRow(LockoutFamily family, LockoutSection section, string item, LockState state)
    {
        if (!LockoutInventory.Contains(family, section, item)) return false;

        bool invalidated = false;
        lock (_lockoutLock)
        {
            if (_lockoutAnswers is { } accumulating)
            {
                accumulating.RemoveAll(r => r.Family == family && r.Section == section
                                            && string.Equals(r.Item, item, StringComparison.Ordinal));
                accumulating.Add(new LockoutRow(family, section, item, state));
            }
            else if (_lockouts.State != LockoutReadState.Unknown)
            {
                _committedLockouts = new LockoutMirror(LockoutReadState.Unknown, []);
                _lockouts = _committedLockouts;
                invalidated = true;
            }
        }
        if (invalidated) Raise(RadioProperty.Lockouts);
        return true;
    }

    /// <summary>Drop everything the radio has said about lockouts, sending
    /// NOTHING — the explicit gesture the zeroize boundary and a set of unknown
    /// scope both need.</summary>
    internal void InvalidateLockouts()
    {
        bool changed;
        lock (_lockoutLock)
        {
            changed = _lockouts.State != LockoutReadState.Unknown || _lockouts.Rows.Count > 0;
            _committedLockouts = new LockoutMirror(LockoutReadState.Unknown, []);
            _lockouts = _committedLockouts;
        }
        if (changed) Raise(RadioProperty.Lockouts);
    }

    // ---- FORCE WAKEUP — the bounded session latch (§9 C3) -----------------
    // ASYMMETRIC BY CONSTRUCTION (protocol.md RWAS table): enabling answers
    // "FORCE WAKEUP ENABLED"; DISABLING IS SILENT and a bare query answers
    // nothing at all. So the ONLY honest mirror is a latch that can be
    // CONFIRMED-ENABLED and nothing else: the report confirms it, the DIS send
    // marks it unconfirmed (never confirmed-disabled — unconfirmed ≠
    // confirmed-off), and reconnect clears it like every mirror.

    private Confirmed<EnabledDisabled> _forceWakeup;

    /// <summary>Force-wakeup burst on key. Only ever
    /// <see cref="EnabledDisabled.Enabled"/> when confirmed — see the note
    /// above; unconfirmed means "not known to be enabled", NOT "disabled".</summary>
    public Confirmed<EnabledDisabled> ForceWakeup => _forceWakeup;

    /// <summary>The <c>FORCE WAKEUP ENABLED</c> line arrived.</summary>
    internal bool SetForceWakeupEnabled()
        => Set(ref _forceWakeup, EnabledDisabled.Enabled, RadioProperty.ForceWakeup);

    /// <summary>A <c>FORCE_W DIS</c> went out: the radio answers NOTHING, so
    /// the latch can only go back to unconfirmed.</summary>
    internal void UnconfirmForceWakeup() => Unconfirm(ref _forceWakeup, RadioProperty.ForceWakeup);

    // ---- Remote port (PORT_R dump — read-only diagnostics) ----------------

    private Confirmed<OnOff> _portRemoteEcho;
    public Confirmed<OnOff> PortRemoteEcho => _portRemoteEcho;
    internal bool SetPortRemoteEcho(OnOff v) => Set(ref _portRemoteEcho, v, RadioProperty.PortRemoteEcho);

    private Confirmed<string> _portBaud, _portBits, _portParity, _portStop, _portXonXoff;
    public Confirmed<string> PortBaud => _portBaud;
    public Confirmed<string> PortBits => _portBits;
    public Confirmed<string> PortParity => _portParity;
    public Confirmed<string> PortStopBits => _portStop;
    public Confirmed<string> PortXonXoff => _portXonXoff;
    internal void SetPortConfig(string field, string value)
    {
        switch (field)
        {
            case "BAUD": Set(ref _portBaud, value, RadioProperty.PortConfig); break;
            case "BITS": Set(ref _portBits, value, RadioProperty.PortConfig); break;
            case "PARITY": Set(ref _portParity, value, RadioProperty.PortConfig); break;
            case "STOP": Set(ref _portStop, value, RadioProperty.PortConfig); break;
            case "XON_XOFF": Set(ref _portXonXoff, value, RadioProperty.PortConfig); break;
        }
    }

    // ---- Sub-domains -------------------------------------------------------

    public AleState Ale { get; }
    public HopState Hop { get; }

    public RadioState()
    {
        Ale = new AleState(Raise);
        Hop = new HopState(Raise);
    }

    /// <summary>Silent reset for a fresh connection: EVERYTHING reverts to
    /// unconfirmed — a different radio must not inherit a previous session's
    /// reported values (no events; the new radio's lines repopulate).</summary>
    internal void ResetForConnect()
    {
        _operatingMode = default;
        _powerLevel = default;
        _powerCutback = default;
        _keyline = default;
        _batteryStatus = default;
        _isTuning = _isTuneComplete = _isTuneMarginal = _isTuneFail = false;
        _rxFrequency = default;
        _txFrequency = default;
        _operatingChannel = default;
        _modulationMode = default;
        _bandwidth = default;
        _agcSpeed = default;
        _channelRxOnly = default;
        _frequencyStep = default;
        _analogSquelch = default;
        _activeModem = default;
        _digitalVoice = default;
        _digitalSquelch = default;
        _squelchLevel = default;
        _fmSquelch = default;
        _fmSquelchType = default;
        _fmTone = default;
        _fmDeviation = default;
        _bfoOffset = default;
        _cwOffset = default;
        _compression = default;
        _antenna = default;
        _retransmit = default;
        _rwas = default;
        _unkeyMask = default;
        _avs = default;
        _encryption = default;
        _encryptionAvailability = default;
        _currentEncryptionKey = default;
        _rfGain = default;
        _contrast = default;
        _beep = default;
        _prePostFilter = _prePostRxAntenna = _prePostScanRate = default;
        _rxPreamp = _internalCoupler = _oneKilowattPa = default;
        _backlightFunction = _backlightIntensity = default;
        _radioTimeOfDay = default;
        _portRemoteEcho = default;
        _portBaud = _portBits = _portParity = _portStop = _portXonXoff = default;
        _forceWakeup = default;
        _lastLockoutRead = default;
        lock (_lockoutLock)
        {
            _lockouts = _committedLockouts = new LockoutMirror(LockoutReadState.Unknown, []);
            _lockoutAnswers = null;
            _lockoutActiveId = _lockoutPendingId = 0;
        }
        lock (_channelListLock) { ChannelList = []; }
        lock (_modemPresetsLock) { ModemPresets = []; }
        _lastModemRead = default;
        lock (_modemQueueLock)
        {
            _modemPresetPresence = _committedModemPresence = new Presence(PresenceState.Unknown, []);
            _modemPresenceAnswers = null;
            _modemPresence.ActiveScope = null;
            _modemPresence.PendingPresenceScope = null;
            _modemPresence.PendingTargetedScope = null;
            _modemPresence.ActiveId = _modemPresence.PendingTargetedId = _modemPresence.PendingPresenceId = 0;
            _modemPresence.ActiveKind = ModemReadKind.None;
            _modemPresence.PendingPresets.Clear();
        }
        Ale.ResetForConnect();
        Hop.ResetForConnect();
    }

    // ====================================================================
    // THE ZEROIZE BOUNDARY (clone round 12 §3 leg 2).
    //
    // `ZERO` wipes every domain on the radio (owner statement, §1). The
    // session SURVIVES it, so unlike a reconnect there is no natural moment
    // at which consumers stop trusting the mirror — which is exactly why this
    // reset is LOUD: every store goes back to unread AND says so, so a surface
    // re-renders its unread markers instead of showing a pre-ZERO value the
    // radio no longer holds.
    //
    // The TRANSPORT is deliberately untouched (queue, parser, ping
    // accounting): the session is alive and the settle poll owns sequencing.
    // ====================================================================

    /// <summary>The properties a zeroize notifies. Complete BY CONSTRUCTION —
    /// every <see cref="RadioProperty"/> except the four that describe the
    /// SESSION rather than the radio's contents (a wipe changes neither the
    /// connection nor a pending mode change nor the settle machine's own
    /// state). A property added to the enum joins this sweep automatically,
    /// which is what makes "every reset store is notified" true without a
    /// hand-maintained list.</summary>
    public static IReadOnlyList<RadioProperty> ZeroizeNotifiedProperties { get; } =
    [
        .. Enum.GetValues<RadioProperty>().Where(p => p
            is not RadioProperty.ConnectionOpen
            and not RadioProperty.ConnectionState
            and not RadioProperty.ModeChangePending
            and not RadioProperty.ZeroizeSettle),
    ];

    /// <summary>Reset every mirrored store after a settled <c>ZERO</c> and
    /// RAISE a notification for each — see the boundary note above.</summary>
    internal void ResetAfterZeroize()
    {
        ResetForConnect();                  // silent: the same field sweep
        foreach (var property in ZeroizeNotifiedProperties) Raise(property);
    }
}
