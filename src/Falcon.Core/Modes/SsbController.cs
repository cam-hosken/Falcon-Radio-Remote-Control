using System.Globalization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Modes;

/// <summary>
/// SSB operations: VFO (absolute set, INC/DEC, STEP), split RX/TX,
/// MODE/BW/AGC, channel select + DI dump, coupler tune, plus the Phase R
/// settings vocabulary (plan-gui-rejigger.md round 4 — setters for every
/// SSB setting; wire syntax per docs/protocol.md HELP listings, builders
/// whose forms were never bench-sent are flagged inline).
/// Channel PROGRAMMING is just these same live commands while on the channel
/// (there is no channel-write command).
/// </summary>
public sealed class SsbController
{
    private readonly Prc138Radio _radio;
    internal SsbController(Prc138Radio radio) => _radio = radio;

    // ---- VFO ------------------------------------------------------------

    /// <summary>Set RX=TX frequency (8-digit Hz string, 1.6–60 MHz — the
    /// MEASURED window, <see cref="Wire.MinFrequencyHz"/>/<see cref="Wire.MaxFrequencyHz"/>).</summary>
    public void SetFrequency(string frequency) => _radio.Send("FR", ValidateFrequency(frequency));
    public void SetRxFrequency(string frequency) => _radio.Send("RXF", ValidateFrequency(frequency));
    public void SetTxFrequency(string frequency) => _radio.Send("TXF", ValidateFrequency(frequency));

    /// <summary>Tune up/down by the RADIO's step (STEP is radio state; the
    /// answer to INC/DEC reports the new frequency).</summary>
    public void IncrementFrequency() => _radio.Send("INC");
    public void DecrementFrequency() => _radio.Send("DEC");

    public void QueryFrequency() => _radio.Send("FR");
    public void QueryStep() => _radio.Send("STEP");
    public void SetStep(FrequencyStep step) => _radio.Send("STEP", step.ToWire());

    /// <summary>Reject blank/short input before it reaches the radio.</summary>
    internal static string ValidateFrequency(string frequency)
    {
        var digits = (frequency ?? string.Empty).Trim();
        if (digits.Length != 8 || !digits.All(char.IsAsciiDigit))
            throw new ArgumentException("Frequency must be 8 digits in Hz (e.g. 01600000).", nameof(frequency));

        int hz = int.Parse(digits, CultureInfo.InvariantCulture);
        // THE BOUND LIVES IN `Wire` (plan-clone-field-round2.md F5, D3 —
        // measured by probe P2, transcript
        // bench/transcripts/p2-freq-range-20260821-175802.jsonl). It used to be
        // written here AND twice in the app layer, at a ceiling nobody had
        // measured; the source radio's own 51.5 MHz channels were refused by
        // all three.
        if (hz < Wire.MinFrequencyHz || hz > Wire.MaxFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be 1.6–60 MHz.");
        return digits;
    }

    // ---- Signal ------------------------------------------------------------

    public void SetModulation(ModulationMode mode) => _radio.Send("MODE", mode.ToWire());
    public void QueryModulation() => _radio.Send("MODE");

    /// <summary>Set bandwidth. The radio NEVER rejects BA (probe R5): invalid
    /// values are ignored and the answer reports the kept value — the
    /// response is the read-back; display exactly what comes back.</summary>
    public void SetBandwidth(string bandwidth)
    {
        var normalized = Wire.NormalizeBandwidth(bandwidth)
            ?? throw new ArgumentException(
                $"Unknown bandwidth '{bandwidth}' — valid values: {string.Join(", ", Wire.BandwidthValues)}.",
                nameof(bandwidth));
        _radio.Send("BA", normalized);
    }
    public void QueryBandwidth() => _radio.Send("BA");

    public void SetAgc(AgcSpeed speed) => _radio.Send("AG", speed.ToWire());
    public void QueryAgc() => _radio.Send("AG");

    /// <summary>Set the current channel's receive-only flag (HELP MORE:
    /// "RXONly - (YEs/NO)"; min abbreviation RXON). Added for Stage 4
    /// channel Program mode; the RXONLY answer line is the read-back.</summary>
    public void SetRxOnly(YesNo value) => _radio.Send("RXON", value.ToWire());

    // ---- Channels ------------------------------------------------------------

    public void SelectChannel(int channel)
    {
        ValidateChannel(channel, nameof(channel));
        _radio.Send("CH", channel.ToString(CultureInfo.InvariantCulture));
    }

    public void QueryChannel() => _radio.Send("CH");

    /// <summary>
    /// Dump stored channel data for a range (<c>DI a b</c>). Round 11 §8: this
    /// no longer CLEARS the mirror — each answered channel UPSERTS its own row
    /// and its siblings stand. That is what lets sequential targeted reads
    /// (the LQA report's per-channel RX/TX loads) accumulate instead of
    /// overwriting each other one at a time.
    /// <para>The whole-book refresh that deliberately starts clean is
    /// <see cref="DisplayAllChannels"/>.</para>
    /// </summary>
    public void DisplayChannels(int fromChannel, int toChannel)
    {
        ValidateChannel(fromChannel, nameof(fromChannel));
        ValidateChannel(toChannel, nameof(toChannel));
        if (toChannel < fromChannel)
            throw new ArgumentException("End channel must not be below the start channel.");
        _radio.Send("DI", fromChannel.ToString(CultureInfo.InvariantCulture), toChannel.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The BULK refresh (bare <c>DI</c>): clears the mirrored list
    /// EXPLICITLY and lets the dump repopulate it. The clear is now a
    /// deliberate gesture of this one command, not a side effect every
    /// targeted read carried (round 11 §8).</summary>
    public void DisplayAllChannels()
    {
        ForgetStoredChannels();
        _radio.Send("DI");
    }

    /// <summary>
    /// Drop everything the radio has said about stored channels, SENDING
    /// NOTHING — the explicit half of round 11 §8's keyed-mirror change.
    /// <para>The channel-list tab's Refresh means "forget what you were told
    /// and read again"; it re-reads only the rows in view, so it cannot rely
    /// on a dump overwriting the rest. Before round 11 the clear rode inside
    /// every <c>DI</c> and this gesture was invisible; now it is a call, and a
    /// caller that wants accumulation simply does not make it.</para>
    /// </summary>
    public void ForgetStoredChannels() => _radio.State.ClearChannelList();

    private static void ValidateChannel(int channel, string name)
    {
        if (channel is < 0 or > 99)
            throw new ArgumentOutOfRangeException(name, "Channel is 0-99.");
    }

    // ---- Coupler ------------------------------------------------------------

    /// <summary>Retune the antenna coupler. TRANSMITS during the tune. Both
    /// TUNE COMPLETE and TUNE FAULT are routine outcomes on this radio
    /// (flaky module); recovery from FAULT is simply tuning again.</summary>
    public void Retune() => _radio.Send("RETU");

    // ====================================================================
    // Phase R settings vocabulary (plan-gui-rejigger.md round 4). Each
    // builder follows the RXON pattern: HELP-derived syntax, client-side
    // validation for what the radio silently ignores, the answer line is
    // the read-back. Builders marked "never sent to this radio" carry
    // HELP/old-app-derived syntax awaiting first live use (bench items —
    // plan/phase-r-classification.md).
    // ====================================================================

    // ---- Squelch family (all three are independent peers — protocol.md) ----

    /// <summary>Analog squelch (SQ ON|OFF — proven on the wire by the
    /// FM-squelch compensation and the DGT_S probes). Plan F5.</summary>
    public void SetSquelch(OnOff state) => _radio.Send("SQ", state.ToWire());

    /// <summary>Digital voice (DV ON|OFF). The answer carries a DGT_SQUELCH
    /// rider. SET-response shape is a named bench item (plan F5).</summary>
    public void SetDigitalVoice(OnOff state) => _radio.Send("DV", state.ToWire());

    /// <summary>Digital squelch (DGT_S ON|OFF — bench-confirmed 2026-08-02;
    /// independent of DV and of modulation).</summary>
    public void SetDigitalSquelch(OnOff state) => _radio.Send("DGT_S", state.ToWire());

    /// <summary>Squelch level (SQ_L LO|MEDIUM|HIGH). Only "SQ_L LO" has been
    /// bench-sent; MEDIUM/HIGH forms are HELP-derived.</summary>
    public void SetSquelchLevel(SquelchLevel level) => _radio.Send("SQ_L", level.ToWire());

    /// <summary>FM squelch (FMSQ ON|OFF — query bench-proven).</summary>
    public void SetFmSquelch(OnOff state) => _radio.Send("FMSQ", state.ToWire());

    /// <summary>FM squelch type (FMSQ_T NOISE|TONE). BENCH-ACCEPTED as a set
    /// 2026-08-19 (clone round 12 §9 C4, transcript r12-p2) — sent at a
    /// confirmed USB with the modulation held constant and echoed as accepted,
    /// which is what retired the app's FM-modulation gate. The comment used to
    /// read "never sent to this radio as a set".</summary>
    public void SetFmSquelchType(FmSquelchType type) => _radio.Send("FMSQ_T", type.ToWire());

    /// <summary>FM 150 Hz TX tone (FMTONE ON|OFF). BENCH-ACCEPTED as a set
    /// 2026-08-19 (§9 C4, r12-p2), at USB, modulation held constant. Was
    /// documented "never sent to this radio as a set".</summary>
    public void SetFmTone(OnOff state) => _radio.Send("FMTONE", state.ToWire());

    /// <summary>FM deviation (FMDE 5.0|6.5|8.0 kHz). BENCH-ACCEPTED as a set
    /// 2026-08-19 (§9 C4, r12-p2) — `FMDE 8.0` at USB, modulation held
    /// constant. Was documented "never sent to this radio as a set".</summary>
    public void SetFmDeviation(string deviation)
    {
        if (!Wire.FmDeviationValues.Contains(deviation))
            throw new ArgumentException(
                $"Unknown FM deviation '{deviation}' — valid values: {string.Join(", ", Wire.FmDeviationValues)}.",
                nameof(deviation));
        _radio.Send("FMDE", deviation);
    }

    // ---- Audio / RX ---------------------------------------------------------

    /// <summary>BFO offset (BF, sign + 4 digits per HELP "(+/- xxxx)"; the
    /// report shape is "BFO +0000"). Never sent to this radio as a set; the
    /// accepted range is a bench item — ±9999 is the representable span.</summary>
    public void SetBfoOffset(int offsetHz)
    {
        if (offsetHz < Wire.BfoOffsetMinHz || offsetHz > Wire.BfoOffsetMaxHz)
            throw new ArgumentOutOfRangeException(nameof(offsetHz), "BFO offset is a signed 4-digit value (±9999).");
        _radio.Send("BF", (offsetHz < 0 ? "-" : "+") + Math.Abs(offsetHz).ToString("D4", CultureInfo.InvariantCulture));
    }

    /// <summary>CW offset (CWOFF 0000|1000 — 4-digit form matching the
    /// report shape). BENCH-ACCEPTED as a set 2026-08-19 (§9 C4, r12-p2) —
    /// `CWOFF 1000` at USB, i.e. OUTSIDE CW, which is what retired the app's
    /// CW-modulation gate. Was documented "never sent to this radio as a
    /// set".</summary>
    public void SetCwOffset(int offsetHz)
    {
        if (!Wire.CwOffsetValuesHz.Contains(offsetHz))
            throw new ArgumentOutOfRangeException(nameof(offsetHz), "CW offset is 0 or 1000 Hz.");
        _radio.Send("CWOFF", offsetHz.ToString("D4", CultureInfo.InvariantCulture));
    }

    /// <summary>Voice compression (COM ON|OFF). Query bench-proven; the set
    /// form has never been sent to this radio.</summary>
    public void SetCompression(OnOff state) => _radio.Send("COM", state.ToWire());

    /// <summary>RF gain (RF 0-100). Never sent to this radio — HELP-derived
    /// syntax; the read-back is the RFG line that rides with AGC answers.</summary>
    public void SetRfGain(int gain)
    {
        if (gain < Wire.RfGainMin || gain > Wire.RfGainMax)
            throw new ArgumentOutOfRangeException(nameof(gain), "RF gain is 0-100.");
        _radio.Send("RF", gain.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>RX preamp (PRE BYPASS|ENABLE). Never sent to this radio; the
    /// answer shape is PROVISIONAL (old-app-derived — round-3 V7), mirrored
    /// verbatim. Note the SET spelling (HELP "BYpass/ENable") and the old
    /// app's REPORT spelling (ENABLED/BYPASSED) differ — both unconfirmed.</summary>
    public void SetRxPreamp(BypassEnable state) => _radio.Send("PRE", state.ToWire());

    /// <summary>Analog voice security (AVS ON|OFF — bench session-14; a
    /// cardless radio answers "AVS NOT INSTALLED"). Rejected in HOP.</summary>
    public void SetAvs(OnOff state) => _radio.Send("AVS", state.ToWire());

    // ---- TX / antenna -------------------------------------------------------

    /// <summary>Antenna port (ANTENNA BNC|AUTO|TUNED). Never sent to this
    /// radio as a set — HELP-derived syntax.</summary>
    public void SetAntenna(AntennaPort port) => _radio.Send("ANTENNA", port.ToWire());

    /// <summary>Internal antenna coupler (INTCOUPLER BYPASS|ENABLE, old-app
    /// token form). Never sent to this radio; answer shape PROVISIONAL
    /// (old-app-derived — round-3 V7), mirrored verbatim.</summary>
    public void SetInternalCoupler(BypassEnable state) => _radio.Send("INTCOUPLER", state.ToWire());

    /// <summary>1 kW PA installed flag (KWAT YES|NO). Never sent to this
    /// radio; answer shape PROVISIONAL (old-app-derived — round-3 V7),
    /// mirrored verbatim; old-repo note: rejected in ALE.</summary>
    public void SetOneKilowattPa(YesNo installed) => _radio.Send("KWAT", installed.ToWire());

    /// <summary>Retransmit (RETR ENA|DIS). Never sent to this radio as a
    /// set; only the "RETRANS DISABLED" report is captured.</summary>
    public void SetRetransmit(EnabledDisabled state) => _radio.Send("RETR", state.ToWire());

    /// <summary>PREPOST filter (PREPOST FILTER ENABLE|DISABLE, old-app
    /// syntax — never sent by this app; the dump answer IS captured,
    /// session-20).</summary>
    public void SetPrePostFilter(OnOff enabled) => _radio.Send("PREPOST", "FILTER", PrePostState(enabled));

    /// <summary>PREPOST RX antenna (PREPOST RXANTENNA ENABLE|DISABLE —
    /// same evidence tier as the filter).</summary>
    public void SetPrePostRxAntenna(OnOff enabled) => _radio.Send("PREPOST", "RXANTENNA", PrePostState(enabled));

    /// <summary>PREPOST scan rate (PREPOST SCAN SLOW|FAST).</summary>
    public void SetPrePostScanRate(PrePostScanRate rate) => _radio.Send("PREPOST", "SCAN", rate.ToWire());

    private static string PrePostState(OnOff enabled) => enabled == OnOff.On ? "ENABLE" : "DISABLE";

    /// <summary>
    /// Keyline (K ON|OFF). <b>K ON TRANSMITS and the radio STAYS KEYED until
    /// turned off</b> (protocol.md hazard table) — gated like Zeroize was in
    /// the old repo: keying requires the literal confirmation token
    /// "TRANSMIT" or the method throws and nothing is sent. This is one of the
    /// three TRANSMIT-hazard gates round 10 §5 deliberately left ALONE (the
    /// removals were the two destructive-DATA senders). OFF (un-key) is
    /// ungated. Never sent to this radio by any app ("K" form is old-app
    /// usage, dated).
    /// </summary>
    public void SetKeyline(OnOff state, string? confirmation = null)
    {
        if (state == OnOff.On && !string.Equals(confirmation, "TRANSMIT", StringComparison.Ordinal))
            throw new ArgumentException(
                "Keyline ON transmits and stays keyed — it requires the literal confirmation token 'TRANSMIT'.",
                nameof(confirmation));
        _radio.Send("K", state.ToWire());
    }

    // ---- RWAS group (SSB only — protocol.md HELP RWAS) ----------------------

    /// <summary>RWAS (RWAS ENA|DIS). Side effect: enabling OR disabling
    /// forces all three squelches ON — the radio reports them back alongside,
    /// so the mirror stays truthful with no re-poll.</summary>
    public void SetRwas(EnabledDisabled state) => _radio.Send("RWAS", state.ToWire());

    /// <summary>Force wakeup burst on key (FORCE_W ENA|DIS). Asymmetric:
    /// enabling answers "FORCE WAKEUP ENABLED"; disabling is SILENT and the
    /// disabled state cannot be read back (protocol.md write-only table).
    /// <para>ROUND 12 §9 C3 — there IS a mirror now, but a deliberately
    /// one-sided one: the ENABLED line confirms it, and DISABLING can only mark
    /// it UNCONFIRMED, because the radio says nothing at all. Marked HERE, at
    /// the send, rather than off a response — there is no response to react to,
    /// and that is exactly the asymmetry being modelled. RE-CONFIRMED
    /// 2026-08-18 (P-2 step e, bench/transcripts/r12-p2-*): a second
    /// <c>FORCE_W ENA</c> re-answers, and <c>FORCE_W DIS</c> answers
    /// nothing.</para></summary>
    public void SetForceWakeup(EnabledDisabled state)
    {
        if (state == EnabledDisabled.Disabled) _radio.State.UnconfirmForceWakeup();
        _radio.Send("FORCE_W", state.ToWire());
    }

    /// <summary>2-digit RWAS key (RWAS_KEY nn, default 00). WRITE-ONLY: no
    /// echo on set and a bare query answers ** ERROR ** — "set, not
    /// readable" (protocol.md).</summary>
    public void SetRwasKey(int key)
    {
        if (key is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(key), "RWAS key is 00-99.");
        _radio.Send("RWAS_KEY", key.ToString("D2", CultureInfo.InvariantCulture));
    }

    /// <summary>Ignore unkey postamble (UNKEY_M ENA|DIS — query and set both
    /// answer UNKEY_M ENABLED/DISABLED).</summary>
    public void SetUnkeyMask(EnabledDisabled state) => _radio.Send("UNKEY_M", state.ToWire());

    // ---- Modem preset select + programming (round-8 EE, amendment X7) -------
    // The owner's round-8 ruling took the modem family OUT of the scope
    // guard: MODEM PRE (read) and MODEM PRESET (program) gained builders and
    // left the wire sweep's forbidden list. MODEM SH stays builder-less —
    // redundant (the active preset is learned from the selection echo / the
    // SH-block short form) and known to be swallowed during init bursts.

    /// <summary>Select a modem preset by number or name (MODEM 1 / MODEM T39
    /// — both bench-proven, session-15). Engagement silently mutates AGC and
    /// bandwidth (probe R8) — handled by the trigger table when the MODEM
    /// answer line arrives. The argument is validated so "MODEM PRESET …"/
    /// "MODEM SH"/"MODEM PRE" cannot be smuggled through the SELECT path —
    /// the listing and programming forms have their own builders below.</summary>
    public void SelectModem(string presetNumberOrName)
    {
        var preset = (presetNumberOrName ?? "").Trim();
        if (preset.Length is < 1 or > 4 || !preset.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Modem preset is 1-4 alphanumeric characters (e.g. '1' or 'T39').", nameof(presetNumberOrName));
        var upper = preset.ToUpperInvariant();
        if (upper is "OF" or "OFF" or "SH" or "SHOW" or "PRE")
            throw new ArgumentException("Not a preset selector — use ModemOff for OF, the preset read operations for PRE; MODEM SH is not built.", nameof(presetNumberOrName));
        _radio.Send("MODEM", upper);
    }

    /// <summary>Disengage the modem (MODEM OF — bench form). Restores the
    /// silently-dragged AGC/bandwidth exactly (probe R8).</summary>
    public void ModemOff() => _radio.Send("MODEM", "OF");

    // ---- The modem preset READ SEAM (round 11 §8) -------------------------
    // REPLACES the old clear-then-bulk QueryModemPresets, whose single
    // collection could not tell a targeted answer from a bulk one and whose
    // clear threw away every disabled preset's fields.
    //
    // TWO SOURCES, because the radio has two and they say different things:
    //   * FIELDS come from the TARGETED read `MODEM PRE n` — the only way to
    //     see a DISABLED preset at all. It does NOT echo EN/DIS.
    //   * ENABLED/DISABLED comes ONLY from the BULK listing, which lists
    //     exactly the enabled presets.
    // Their answer LINES ARE IDENTICAL IN SHAPE, so the two windows must never
    // overlap: every modem read is an operation on ONE single-slot store queue
    // and each carries its own closing sentinel.

    /// <summary>How long to wait for a modem read operation to settle.</summary>
    public int ModemReadTimeoutMs { get; set; } = 10_000;

    /// <summary>Targeted read of ONE preset's fields (<c>MODEM PRE n</c> +
    /// sentinel). PROMPT-SCOPED (F9): 7-9 are refused unless the radio has
    /// confirmed <c>HOP&gt;</c>, and 0-6 are refused when it has.</summary>
    public long QueryModemPreset(int preset)
    {
        ValidatePreset(preset);
        return RequestTargetedModemRead([preset]);
    }

    /// <summary>The list tab's batch: the CONFIRMED PROMPT'S presets' fields as
    /// ONE sentinel-completed operation (<c>MODEM PRE 0</c> … <c>MODEM PRE 6</c>
    /// at <c>SSB&gt;</c>/<c>ALE&gt;</c>, <c>MODEM PRE 7</c> … <c>MODEM PRE 9</c>
    /// at <c>HOP&gt;</c> — F9) plus ONE closing sentinel. One operation, one
    /// completion — so the presence read that follows it cannot open its window
    /// early.</summary>
    public long RefreshModemPresets() => RequestTargetedModemRead(ScopedPresets());

    /// <summary>
    /// The PRESENCE read (bare <c>MODEM PRE</c> + sentinel): the preset numbers
    /// the radio lists between the command and the sentinel are the ENABLED
    /// set, committed ATOMICALLY on the sentinel. A faulted read preserves the
    /// prior set exactly, and the commit never clears or replaces the FIELDS
    /// mirror — it publishes the enabled-number set and nothing else.
    /// <para>F9/F10: the committed set is KEYED BY THE PROMPT it was read at.
    /// The listing names the enabled presets among 0-6 at <c>SSB&gt;</c>/
    /// <c>ALE&gt;</c> and among 7-9 at <c>HOP&gt;</c>, so an unkeyed set read at
    /// one prompt would be a false statement about the other's band.</para>
    /// </summary>
    public long QueryModemPresetPresence()
    {
        long readId = _radio.State.RequestModemPresenceRead(
            ConfirmedPromptOrRefuse(), out bool dispatch);
        if (dispatch) DispatchModemPresenceRead(readId);
        return readId;
    }

    private long RequestTargetedModemRead(int[] presets)
    {
        long readId = _radio.State.RequestModemTargetedRead(
            ConfirmedPromptOrRefuse(), presets, out var dispatchPresets);
        if (dispatchPresets is not null) DispatchModemTargetedRead(readId, dispatchPresets);
        return readId;
    }

    private void DispatchModemTargetedRead(long readId, int[] presets)
    {
        foreach (var preset in presets)
            _radio.Send("MODEM", "PRE", preset.ToString(CultureInfo.InvariantCulture));
        _radio.Ping(answered => CompleteModemRead(readId, answered), ModemReadTimeoutMs);
    }

    private void DispatchModemPresenceRead(long readId)
    {
        _radio.Send("MODEM", "PRE");
        _radio.Ping(answered => CompleteModemRead(readId, answered), ModemReadTimeoutMs);
    }

    private void CompleteModemRead(long readId, bool answered)
    {
        _radio.State.CompleteModemRead(readId, answered,
            out long nextReadId, out var nextPresets, out bool dispatchPresence);
        if (nextPresets is not null) DispatchModemTargetedRead(nextReadId, nextPresets);
        else if (dispatchPresence) DispatchModemPresenceRead(nextReadId);
    }

    // ---- THE PROMPT SCOPE (clone-field round 2 F9/F10/F11, decision A-8) ----
    //
    // "Presets are 0-6 on this firmware" was never a firmware fact — it was a
    // fact about the prompt nobody had left. `MODEM PRE 7` answers
    // `INVALID MODEM PRESET` at `SSB>` and `ALE>`, and answers a stored preset
    // at `HOP>`, where 0-6 are the INVALID half instead (P5, transcript
    // bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl).
    //
    // The scope is read from the radio's OWN confirmed mode, not from a caller
    // argument: the prompt is the radio's state, and a caller that could pass
    // its own idea of it could send `MODEM PRE 7` at `SSB>` by believing
    // wrongly. UNCONFIRMED REFUSES (audit round 2, MAJOR 1): it used to fall
    // back to the SSB/ALE band, which does not narrow anything — it guesses,
    // and the guess reaches the wire.

    /// <summary>
    /// The CONFIRMED prompt, or a REFUSAL (audit round 2, MAJOR 1).
    ///
    /// <para>This used to FALL BACK to the SSB/ALE band when the mode was
    /// unconfirmed, on the reasoning that a caller who had not been told the
    /// prompt should not have its band widened. That reasoning was right about
    /// the band and wrong about the answer: the fallback did not narrow
    /// anything, it GUESSED — and a guess here puts <c>MODEM PRE 0</c> on the
    /// wire at a prompt nobody has identified, which at <c>HOP&gt;</c> is
    /// simply <c>INVALID MODEM PRESET</c>.</para>
    ///
    /// <para>The sweep that settled it (audit round 2): <c>ProgramModemPreset</c>,
    /// <c>QueryModemPreset</c>, <c>RefreshModemPresets</c> and
    /// <c>QueryModemPresetPresence</c> have NO caller in <c>src/</c> or
    /// <c>bench/</c> that can reach them with the mode unconfirmed — the wheel
    /// and the card both gate on a confirmed mode, and the clone campaign only
    /// reaches its modem legs through <c>AtPromptAsync</c>. So refusing costs
    /// nothing real and closes the path structurally instead of by
    /// convention.</para>
    /// </summary>
    private OperatingMode ConfirmedPromptOrRefuse()
    {
        var mode = _radio.State.OperatingMode;
        if (!mode.IsConfirmed)
            throw new InvalidOperationException(
                "The radio has not reported its mode, so which modem presets exist is unknown — "
                + "a preset command cannot be sent at an unidentified prompt.");
        return mode.Value;
    }

    /// <summary>The preset band the radio's CONFIRMED prompt owns.</summary>
    private (int First, int Last) PresetScope() => ModemPresetScope.Range(ConfirmedPromptOrRefuse());

    private int[] ScopedPresets() => ModemPresetScope.Presets(ConfirmedPromptOrRefuse());

    private void ValidatePreset(int preset)
    {
        var (first, last) = PresetScope();
        if (preset < first || preset > last)
            throw new ArgumentOutOfRangeException(nameof(preset),
                $"Modem presets are {first}-{last} at this radio's current prompt.");
    }

    // Round 9: the value tokens are the HELP screen's ABBREVIATIONS (the
    // capital letters of session-07's verbatim `HELP MODEM` capture —
    // "capital letters denote acceptable abbreviation"). These four lists are
    // Falcon.Core's OWN copy of the wire column: the app layer's
    // ModemPresetVocabulary owns the display/listing columns and cannot be
    // referenced from here, so the builder validates independently and a test
    // cross-checks that the two agree.
    // ABBREVIATED-WRITE ACCEPTANCE IS ASSUMED — no short token has ever been
    // sent to this radio (docs/protocol.md; bench item A6d).

    /// <summary>TYpe — (39tone/FSKWs/FSKNs/FSK-A/FSK-V/SErial). "39tone" has
    /// no capitals and therefore no abbreviation.</summary>
    private static readonly string[] ModemPresetTypes =
        ["39TONE", "FSKW", "FSKN", "FSK-A", "FSK-V", "SE"];

    /// <summary>ASync (REMote/DATa) and SYnc, as the one phrase the line
    /// carries between TYPE and BAUD.</summary>
    private static readonly string[] ModemPresetDataModes =
        ["ASYNC REM", "ASYNC DAT", "SYNC DAT"];

    /// <summary>BAUd — (75/150/300/600/1200/2400/4800/VOice). A DISCRETE set,
    /// not a range: the round-8 "75-4800" integer range was an over-read of
    /// the HELP line and let 1000 or 2401 onto the wire.</summary>
    private static readonly string[] ModemPresetBauds =
        ["75", "150", "300", "600", "1200", "2400", "4800", "VO"];

    /// <summary>INTerleav — (LOng/SHort/ALTS/ALTL/ZEro).</summary>
    private static readonly string[] ModemPresetInterleaves =
        ["LO", "SH", "ALTS", "ALTL", "ZE"];

    /// <summary>Program one stored modem preset as ONE line (round-8 EE,
    /// amendment X7; REWRITTEN round 9 to the short-token vocabulary):
    /// <c>MODEM PRESET n NAME x TYPE t ASYNC REM|ASYNC DAT|SYNC DAT BAUD b
    /// [INTERLEAV i] [MARK f SPACE f] [EN|DIS]</c>.
    /// <para>The ARGUMENT NAMES (NAME/TYPE/BAUD/INTERLEAV/MARK/SPACE) are
    /// carried in the session-15 form; the VALUE tokens are now the HELP
    /// screen's abbreviations (session-07's verbatim capture). Preset numbers
    /// are 0-6 AT THIS PROMPT (7 answers INVALID MODEM PRESET at `SSB&gt;` and
    /// `ALE&gt;`; the `HOP&gt;` band is 7-9 and has its own builder —
    /// <see cref="ProgramHopModemPreset"/>, F9). An omitted
    /// optional leaves its argument off the line. The echo is the stored
    /// preset's listing-form line and UPSERTS the mirror — but it is NOT the
    /// whole read-back (round 11 §6 retired that round-9 rule): it cannot show
    /// a SILENTLY CLAMPED baud, and it never carries EN/DIS at all. A caller
    /// that needs to know what was really stored re-reads the preset TARGETED
    /// (<see cref="QueryModemPreset"/>) and, for a state write, runs
    /// <see cref="QueryModemPresetPresence"/>.</para>
    /// <para><b>VERIFIED 2026-08-16</b> (this paragraph read "ASSUMED,
    /// bench-routed" until round 11 P5): abbreviated-token write acceptance
    /// HOLDS. Every short token in the four lists above was sent to the real
    /// radio and round-tripped — round 9's assumption is settled, and the
    /// listing spellings that come back are recorded in docs/protocol.md's
    /// per-value evidence table.</para></summary>
    public void ProgramModemPreset(
        int preset, string name, string type, string dataMode, string baud,
        string? interleave = null, string? mark = null, string? space = null,
        bool? enabled = null)
    {
        // PROMPT-SCOPED (F9). Two different refusals, because they are two
        // different mistakes: a preset number the prompt does not own, and the
        // TYPE-carrying SSB LINE SHAPE sent at a prompt that refuses it. P5b
        // captured the second answering `** ERROR **` twice or three times at
        // `HOP>` and leaving the preset unchanged — `ProgramHopModemPreset` is
        // the builder for that prompt.
        RefuseHopPrompt("The stored-preset line with a TYPE field is refused at a HOP prompt");
        ValidatePreset(preset);
        var storedName = (name ?? "").Trim().ToUpperInvariant();
        if (storedName.Length is < 1 or > 4 || !storedName.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Preset name is 1-4 alphanumeric characters (e.g. 'T39').", nameof(name));
        if (storedName is "OF" or "OFF" or "SH" or "SHOW" or "PRE")
            throw new ArgumentException("Preset name collides with a MODEM selector token — it could never be selected by name.", nameof(name));
        var storedType = (type ?? "").Trim().ToUpperInvariant();
        if (!ModemPresetTypes.Contains(storedType))
            throw new ArgumentException("Modem type is one of " + string.Join("/", ModemPresetTypes) + ".", nameof(type));
        var storedDataMode = (dataMode ?? "").Trim().ToUpperInvariant();
        if (!ModemPresetDataModes.Contains(storedDataMode))
            throw new ArgumentException("Data mode is one of " + string.Join(" / ", ModemPresetDataModes) + ".", nameof(dataMode));
        var storedBaud = (baud ?? "").Trim().ToUpperInvariant();
        if (!ModemPresetBauds.Contains(storedBaud))
            throw new ArgumentException("Baud is one of " + string.Join("/", ModemPresetBauds) + " (HELP MODEM — a discrete set).", nameof(baud));

        var line = new System.Text.StringBuilder();
        line.Append("PRESET ").Append(preset.ToString(CultureInfo.InvariantCulture))
            .Append(" NAME ").Append(storedName)
            .Append(" TYPE ").Append(storedType)
            .Append(' ').Append(storedDataMode)
            .Append(" BAUD ").Append(storedBaud);

        if (interleave is not null)
        {
            var ilv = interleave.Trim().ToUpperInvariant();
            if (!ModemPresetInterleaves.Contains(ilv))
                throw new ArgumentException("Interleave is one of " + string.Join("/", ModemPresetInterleaves) + " (HELP MODEM).", nameof(interleave));
            line.Append(" INTERLEAV ").Append(ilv);
        }

        // MARK/SPACE ride together or not at all — HELP shows them as a pair
        // and no capture says what one without the other would mean.
        if (mark is not null || space is not null)
        {
            if (mark is null || space is null)
                throw new ArgumentException("MARK and SPACE are set together or not at all.", nameof(space));
            var m = mark.Trim();
            var s = space.Trim();
            if (m.Length is < 1 or > 6 || !m.All(char.IsAsciiDigit)
                || s.Length is < 1 or > 6 || !s.All(char.IsAsciiDigit))
                throw new ArgumentException("MARK/SPACE are bare digit frequencies (HELP MODEM — provisional; units unverified).", nameof(mark));
            line.Append(" MARK ").Append(m).Append(" SPACE ").Append(s);
        }

        // ENable / DISable, abbreviated like every other value token.
        if (enabled is { } en)
            line.Append(en ? " EN" : " DIS");

        _radio.Send("MODEM", line.ToString());
    }

    /// <summary>
    /// Program one stored <c>HOP&gt;</c> modem preset (7-9) — a SHORTER line
    /// than its SSB sibling and a SEPARATE builder, so the SSB bytes stay
    /// pinned untouched (clone-field round 2 decision A-9):
    /// <c>MODEM PRESET n NAME x ASYNC|SYNC DATA|REMOTE BAUD b</c>, followed —
    /// on its OWN line, LAST — by <c>MODEM PRESET n EN|DIS</c> when
    /// <paramref name="enabled"/> is given.
    ///
    /// <para><b>Every rule here is CAPTURED</b> (P5b/P5c, transcripts
    /// <c>bench/transcripts/p5b-hop-modem-preset-write-20260821-181018.jsonl</c>
    /// and <c>p5c-hop-modem-baud-20260821-182807.jsonl</c>, both on preset 9):
    /// <list type="bullet">
    ///   <item>a <c>TYPE</c> argument draws <c>** ERROR **</c> and changes
    ///     nothing, so this builder HAS no type parameter;</item>
    ///   <item>BAUD is exactly {75, 150, 300} — everything else is SILENTLY
    ///     ignored and the line is echoed with the OLD value, which is the one
    ///     failure a caller cannot see, so it is refused here
    ///     (<see cref="Wire.HopModemBauds"/>);</item>
    ///   <item><b>ANY field write RE-ENABLES a disabled preset</b> (the SSB
    ///     rule, re-confirmed at <c>HOP&gt;</c>), which is precisely why EN/DIS
    ///     goes out LAST and on its own line: the field line's re-enable must
    ///     not outrank the state the caller asked for;</item>
    ///   <item>at <c>SSB&gt;</c> the same line answers
    ///     <c>INVALID MODEM PRESET</c> — hence the prompt guard.</item>
    /// </list></para>
    ///
    /// <para><b>The four <c>SYNC</c>×<c>PORT</c> combinations are all OFFERED;
    /// exactly TWO are CAPTURED and the other two are UNPROBED.</b> P5b applied
    /// <c>ASYNC REMOTE</c> and <c>SYNC DATA</c> — those two are known accepted.
    /// <c>SYNC REMOTE</c> and <c>ASYNC DATA</c> have never been sent at a
    /// <c>HOP&gt;</c> prompt, so NOTHING is claimed about them: they are offered
    /// because the app does not invent a constraint it has not read
    /// (constitution §3.1), not because they are known to work, and the
    /// read-back is what will say. Settling the pair is a bench item.</para>
    /// </summary>
    public void ProgramHopModemPreset(
        int preset, string name, SyncMode sync, DataMode data, string baud, bool? enabled = null)
    {
        if (preset is < ModemPresetScope.HopFirst or > ModemPresetScope.HopLast)
            throw new ArgumentOutOfRangeException(nameof(preset),
                $"HOP modem presets are {ModemPresetScope.HopFirst}-{ModemPresetScope.HopLast}.");
        RefuseNonHopPrompt("The HOP stored-preset line is refused at an SSB or ALE prompt");

        var storedName = (name ?? "").Trim().ToUpperInvariant();
        if (storedName.Length is < 1 or > 4 || !storedName.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Preset name is 1-4 alphanumeric characters (e.g. 'DAT9').", nameof(name));
        if (storedName is "OF" or "OFF" or "SH" or "SHOW" or "PRE")
            throw new ArgumentException("Preset name collides with a MODEM selector token — it could never be selected by name.", nameof(name));

        var storedBaud = (baud ?? "").Trim();
        if (!Wire.HopModemBauds.Contains(storedBaud))
            throw new ArgumentException(
                "Baud is one of " + string.Join("/", Wire.HopModemBauds)
                + " on a HOP preset (measured — everything else is silently ignored).", nameof(baud));

        var number = preset.ToString(CultureInfo.InvariantCulture);
        _radio.Send("MODEM", $"PRESET {number} NAME {storedName} {sync.ToWire()} {data.ToWire()} BAUD {storedBaud}");

        // LAST, and on its own line: the field write above has already
        // re-enabled the preset (P5b), so an intended DISABLE has to follow it.
        if (enabled is { } en)
            _radio.Send("MODEM", $"PRESET {number} " + (en ? "EN" : "DIS"));
    }

    /// <summary>Refuse a builder whose LINE SHAPE the <c>HOP&gt;</c> prompt
    /// rejects. Unconfirmed mode does not fire it — the guard only speaks about
    /// a prompt the radio has actually reported.</summary>
    private void RefuseHopPrompt(string what)
    {
        var mode = _radio.State.OperatingMode;
        if (mode.IsConfirmed && mode.Value == OperatingMode.Hop)
            throw new ArgumentException(what + " — the HOP preset builder is the one for that prompt.");
    }

    /// <summary>The mirror image: a <c>HOP&gt;</c>-only line, which needs a
    /// CONFIRMED <c>HOP&gt;</c> prompt — not merely "not known to be
    /// elsewhere".
    /// <para>AUDIT ROUND 2, MAJOR 1: this used to let an UNCONFIRMED mode
    /// through, on the same "do not fire on a prompt the radio has not
    /// reported" reasoning as its sibling. But the two are not symmetric. The
    /// sibling's refusal is a NARROWING of a command that is otherwise fine;
    /// this one guards a line that is INVALID at two of the three prompts, so
    /// "we do not know where we are" has to mean no, or the app is gambling
    /// with a two-in-three chance of <c>INVALID MODEM PRESET</c>.</para></summary>
    private void RefuseNonHopPrompt(string what)
    {
        if (ConfirmedPromptOrRefuse() != OperatingMode.Hop)
            throw new ArgumentException(what + " — those prompts answer INVALID for presets 7-9.");
    }

    // ---- Device (SSB-scoped by probe) ---------------------------------------

    /// <summary>Front-panel error beep (BEEP ON|OFF — bench session-20/23).
    /// SSB-scoped: probed REJECTED in ALE, mixed in HOP (sentinel table).</summary>
    public void SetBeep(OnOff state) => _radio.Send("BEEP", state.ToWire());

    // ====================================================================
    // Settings QUERIES — OLD-APP-DERIVED, BENCH-UNCONFIRMED
    // (plan-ui-tweaks-round3.md V7; docs/protocol.md "Old-app-derived SSB
    // query set (PROVISIONAL — bench-unconfirmed)").
    //
    // Provenance: every command below is the exact form the WinForms
    // Falcon-Radio-Remote-Control sends, mined from its
    // src/Falcon.Core/Radio/Prc138Radio.cs "Queries" block (line numbers in
    // each summary) — an app that WORKS on SSB against this radio family.
    // It queries them per setting, not through a bulk dump: its binding
    // registry derives the query set from the bound controls and runs them
    // one by one (old repo src/Falcon.Gui/Binding/BindingRegistry.cs:453-460;
    // the settings window's open burst is Configuration.cs:41-62). So the
    // per-setting shape here follows the old app's reality, not an invention.
    //
    // NONE of these has been sent to a radio by THIS project. They are
    // PROVISIONAL until the bench-checklist "SSB settings queries" items run.
    // They are also SSB-scoped (the old app greys ANTENNA/RWAS outside SSB
    // and skips them at ALE/HOP prompts) — callers gate on confirmed SSB.
    //
    // Deliberately ABSENT (the old app queries neither, and documents why):
    //   FORCE_W  — disabling is silent, a bare query returns nothing
    //              (old repo src/Falcon.Gui/Settings/SettingsWindow.cs:359-361)
    //   RWAS_KEY — write-only; a bare query answers ** ERROR **
    //              (old repo SettingsWindow.cs:302-313)
    // ====================================================================

    /// <summary>FM squelch type (old app: <c>FMSQ_T</c>, Prc138Radio.cs:987)
    /// → answer token <c>FMSQ_TYPE NOISE|TONE</c>.</summary>
    public void QueryFmSquelchType() => _radio.Send("FMSQ_T");

    /// <summary>FM 150 Hz TX tone (<c>FMTONE</c>, :988) → <c>FMTONE ON|OFF</c>.</summary>
    public void QueryFmTone() => _radio.Send("FMTONE");

    /// <summary>FM deviation (<c>FMDE</c>, :989) → <c>FMDEV 5.0|6.5|8.0</c>.</summary>
    public void QueryFmDeviation() => _radio.Send("FMDE");

    /// <summary>CW offset (<c>CWOFF</c>, :991) → <c>CWOFFSET 0000|1000</c>.</summary>
    public void QueryCwOffset() => _radio.Send("CWOFF");

    /// <summary>Analog voice security (<c>AVS</c>, :1063) → <c>AVS ON|OFF</c>
    /// or <c>AVS NOT INSTALLED</c>. The one query in this set whose answer
    /// shape IS bench-confirmed here (protocol.md COMSEC); the QUERY form is
    /// old-app-derived like the rest.</summary>
    public void QueryAvs() => _radio.Send("AVS");

    /// <summary>RX preamp (<c>PRE</c>, :1000) → <c>PREAMP …</c> (payload
    /// PROVISIONAL: the old app maps ENABLED/BYPASSED).</summary>
    public void QueryRxPreamp() => _radio.Send("PRE");

    /// <summary>RF gain (<c>RF</c>, :1009) → <c>RFG 0-100</c> (the RFG line
    /// shape IS bench-confirmed — it rides with AGC answers, probe R4).</summary>
    public void QueryRfGain() => _radio.Send("RF");

    /// <summary>Antenna port (<c>ANTENNA</c>, :996) → <c>ANTENNA AUTO|TUNED|BNC</c>.</summary>
    public void QueryAntenna() => _radio.Send("ANTENNA");

    /// <summary>Internal coupler (<c>INTCOUPLER</c>, :1001) →
    /// <c>INTCOUPLER …</c> (payload PROVISIONAL: ENABLED/BYPASSED).</summary>
    public void QueryInternalCoupler() => _radio.Send("INTCOUPLER");

    /// <summary>1 kW PA installed (<c>KWAT</c>, :1002) → <c>KWATT YES|NO</c>
    /// (payload PROVISIONAL). Old-repo note: rejected at an ALE prompt.</summary>
    public void QueryOneKilowattPa() => _radio.Send("KWAT");

    /// <summary>Retransmit (<c>RETR</c>, :995) → <c>RETRANS ENABLED|DISABLED</c>.</summary>
    public void QueryRetransmit() => _radio.Send("RETR");

    /// <summary>PREPOST filter (<c>PREPOST FILTER</c>, :1003) →
    /// <c>PREPOST FILTER ENABLE|DISABLE</c>.</summary>
    public void QueryPrePostFilter() => _radio.Send("PREPOST", "FILTER");

    /// <summary>PREPOST RX antenna (<c>PREPOST RXANTENNA</c>, :1004).</summary>
    public void QueryPrePostRxAntenna() => _radio.Send("PREPOST", "RXANTENNA");

    /// <summary>PREPOST scan rate (<c>PREPOST SCAN</c>, :1005) →
    /// <c>PREPOST SCAN SLOW|FAST</c> (the old app also knows BYPASS).</summary>
    public void QueryPrePostScanRate() => _radio.Send("PREPOST", "SCAN");

    /// <summary>RWAS (<c>RWAS</c>, :1006) → <c>RWAS ENABLED|DISABLED</c>.
    /// SSB-only: the old app skips it at ALE/HOP prompts (Configuration.cs:53).</summary>
    public void QueryRwas() => _radio.Send("RWAS");

    /// <summary>Unkey mask (<c>UNKEY_M</c>, :1140) →
    /// <c>UNKEY_M ENABLED|DISABLED</c>. SSB-only, same as RWAS.</summary>
    public void QueryUnkeyMask() => _radio.Send("UNKEY_M");

    /// <summary>Front-panel beep (<c>BEEP</c>, :1050) → <c>BEEP ON|OFF</c>.
    /// Present on the old app's Core surface but bound to no old-GUI control,
    /// so it carries one evidence tier LESS than the rest of this set.</summary>
    public void QueryBeep() => _radio.Send("BEEP");

    /// <summary>Voice compression (<c>COM</c>) → <c>COMPRESS ON|OFF</c>.
    /// <para><b>CAPTURED 2026-08-18</b> (round-12 P-2 step c,
    /// bench/transcripts/r12-p2-*): bare <c>COM</c> answers
    /// <c>COMPRESS ON</c> — the §9 B3 PRIMARY branch. Until this capture the
    /// compression mirror had NO read path at all and latched the app's own
    /// last echo forever.</para></summary>
    public void QueryCompression() => _radio.Send("COM");

    // ====================================================================
    // X12 — OPERATOR LOCKOUTS (clone round 12 §3). A NEW command family
    // (`PROGRAM` / `SELECT`), whitelist-narrowed to the clone campaign:
    // GuiOutScopeGuardTests' X12 block says which app-layer files may name
    // the surface wrappers, and no view model is among them.
    //
    // WIRE TRUTH (captured 2026-08-18, bench/transcripts/r11-lockouts-* and
    // r12-p1-*): bare `PROGRAM` and bare `SELECT` are GLOBAL STATE REPORTS
    // answered from ONE prompt — sectioned by `>>SSB_…` / `>>HOP_…` /
    // `>>EAM_…` headers — and a set echoes its own command verbatim with no
    // accept/reject semantics, so the STATE REPORT is the only confirmation.
    // ====================================================================

    /// <summary>How long to wait for a lockout read operation to settle.</summary>
    public int LockoutReadTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// The lockout READ: bare <c>PROGRAM</c> + bare <c>SELECT</c> + ONE closing
    /// sentinel — a single operation, because both reports are global and
    /// answer from whatever prompt the radio happens to be at.
    /// <para>The sentinel is load-bearing for the same reason it is everywhere
    /// else here: the rows commit ATOMICALLY on an answered sentinel, and an
    /// unanswered one preserves the prior mirror exactly rather than
    /// publishing a half-read table.</para>
    /// <para>Returns the operation's READ ID; match it against
    /// <see cref="RadioState.LastLockoutRead"/> to know whether THIS read
    /// committed. Coalesces — a request arriving while one is on the wire
    /// sends nothing.</para>
    /// </summary>
    public long QueryLockouts()
    {
        long readId = _radio.State.RequestLockoutRead(out bool dispatch);
        if (dispatch) DispatchLockoutRead(readId);
        return readId;
    }

    private void DispatchLockoutRead(long readId)
    {
        _radio.Send("PROGRAM");
        _radio.Send("SELECT");
        _radio.Ping(answered => CompleteLockoutRead(readId, answered), LockoutReadTimeoutMs);
    }

    private void CompleteLockoutRead(long readId, bool answered)
    {
        _radio.State.CompleteLockoutRead(readId, answered, out long nextReadId, out bool dispatchNext);
        if (dispatchNext) DispatchLockoutRead(nextReadId);
    }

    /// <summary>
    /// Set ONE lockout: <c>PROGRAM &lt;ITEM&gt; LOCK|UNLOCK</c> /
    /// <c>SELECT &lt;ITEM&gt; LOCK|UNLOCK</c>.
    ///
    /// <para><b>THE SECTION IS NOT ON THE WIRE.</b> The radio scopes a set to
    /// the ACTIVE PROMPT's mode section (captured 2026-08-18, round-12 P-1),
    /// so <paramref name="section"/> is a CONTRACT with the caller, not an
    /// argument: it is validated against the closed inventory — sending
    /// <c>TX_POWER</c> as an SSB row is a programming error, not a wire event —
    /// and the ORCHESTRATOR is responsible for standing at that section's
    /// prompt before calling. Nothing here positions the prompt, because a
    /// builder that switched modes behind a caller would be exactly the silent
    /// send this project forbids.</para>
    /// </summary>
    public void SetLockout(LockoutFamily family, LockoutSection section, string item, LockState state)
    {
        var storedItem = (item ?? "").Trim().ToUpperInvariant();
        if (!LockoutInventory.Contains(family, section, storedItem))
            throw new ArgumentException(
                $"'{storedItem}' is not a {family}/{section} lockout item — the 22-item inventory is closed.",
                nameof(item));

        _radio.Send(family == LockoutFamily.Program ? "PROGRAM" : "SELECT",
            storedItem, state == LockState.Lock ? "LOCK" : "UNLOCK");
    }

    // ====================================================================
    // X13 — ZEROIZE (clone round 12 §3 leg 2, owner ruling R1).
    // ====================================================================

    /// <summary>
    /// <b>ZEROIZE THE RADIO — IRREVERSIBLE.</b> Wipes every stored domain
    /// (channels, HOP nets, EXCLUDE, the ALE fill, groups, schedules, stored
    /// messages, modem presets, settings, and every lockout back to LOCK),
    /// sparing only the remote port's line settings — which is why the 9600
    /// session survives it (owner statement + capture, §1).
    ///
    /// <para>Guarded like the baud change: the app layer can reach this only
    /// through <c>SsbSurface.ZeroizeRadio</c>, and GuiOutScopeGuardTests' X13
    /// block narrows THAT to the clone campaign's file alone. The GUI can never
    /// reach it.</para>
    ///
    /// <para>SENDING IT ARMS THE SETTLE MACHINE (<see cref="Prc138Radio"/>):
    /// the radio answers a ZEROIZING banner and then goes quiet for seconds, so
    /// the caller AWAITS the settle observable rather than sleeping. Core is
    /// the only actor that polls, and it polls with bare CRs over its internal
    /// send path.</para>
    /// </summary>
    public void ZeroizeRadio()
    {
        _radio.Send("ZERO");
        _radio.BeginZeroizeSettle();
    }

    // ---- Diagnostics (GUI-out — plan round 4, E5) ---------------------------

    /// <summary>Firmware versions per module (TE 3 — safe, no TX; bench
    /// session-23). Answer lines are "Module 01A  Revision 8214B".</summary>
    public void QueryFirmwareVersions() => _radio.Send("TE", "3");

    /// <summary>Radio self test (TE). <b>TRANSMITS — antenna or dummy load
    /// required</b> (protocol.md hazard table). Gated: requires the literal
    /// confirmation token "TRANSMIT" or the method throws and nothing is
    /// sent. Never sent to this radio.</summary>
    public void SelfTest(string confirmation)
    {
        RequireTransmitToken(confirmation, "TE (self test)");
        _radio.Send("TE");
    }

    /// <summary>VSWR test (TE 4). <b>TRANSMITS</b> — same gate as
    /// <see cref="SelfTest"/>. Never sent to this radio.</summary>
    public void VswrTest(string confirmation)
    {
        RequireTransmitToken(confirmation, "TE 4 (VSWR test)");
        _radio.Send("TE", "4");
    }

    private static void RequireTransmitToken(string confirmation, string what)
    {
        if (!string.Equals(confirmation, "TRANSMIT", StringComparison.Ordinal))
            throw new ArgumentException(
                $"{what} transmits and requires the literal confirmation token 'TRANSMIT'.",
                nameof(confirmation));
    }
}
