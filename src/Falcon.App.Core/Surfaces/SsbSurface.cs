using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>SSB operating slice (Stage 4): VFO frequencies, the radio's own
/// STEP, modulation/bandwidth/AGC, and the current channel's RXONLY flag —
/// plus the explicit intents for each. STEP is RADIO state (INC/DEC tune by
/// it) and SSB-only; the OperatingMode property is watched so consumers can
/// grey SSB-domain controls with a reason outside SSB.</summary>
public sealed class SsbSurface : RadioSurface
{
    public SsbSurface(Prc138Radio radio)
        : base(radio,
            RadioProperty.RxFrequency, RadioProperty.TxFrequency,
            RadioProperty.FrequencyStep, RadioProperty.ModulationMode,
            RadioProperty.Bandwidth, RadioProperty.AgcSpeed,
            RadioProperty.ChannelRxOnly, RadioProperty.OperatingMode,
            // Phase R / Wave 2 (GUI rejigger F8, E6, SSB settings pane): the
            // squelch peers, DV, compression, modem, BFO, the FM group, and
            // the whole settings-pane vocabulary — so the SSB VMs refresh when
            // any of these confirmed values changes. RadioProperty.Keyline is
            // deliberately NOT watched: the keyline control is DEFERRED this
            // wave (see the TX/antenna note below), so there is no keyline
            // display to refresh yet.
            // (RadioProperty.ActiveModem left for ModemSurface — round 8 ED:
            // the modem is cross-mode state, the power pattern.)
            RadioProperty.AnalogSquelch, RadioProperty.FmSquelch,
            RadioProperty.DigitalSquelch, RadioProperty.DigitalVoice,
            RadioProperty.SquelchLevel, RadioProperty.Compression,
            RadioProperty.BfoOffset,
            RadioProperty.FmSquelchType, RadioProperty.FmTone,
            RadioProperty.FmDeviation, RadioProperty.CwOffset,
            RadioProperty.Avs, RadioProperty.RfGain, RadioProperty.Antenna,
            RadioProperty.Retransmit, RadioProperty.Rwas,
            RadioProperty.UnkeyMask, RadioProperty.Beep,
            RadioProperty.PrePostFilter, RadioProperty.PrePostRxAntenna,
            RadioProperty.PrePostScanRate,
            // Round-3 V7: the three PROVISIONAL mirrors the old-app-derived
            // query set unlocked (PREAMP / INTCOUPLER / KWATT).
            RadioProperty.RxPreamp, RadioProperty.InternalCoupler,
            RadioProperty.OneKilowattPa,
            // Clone round 12 §9 C3: the FORCE WAKEUP session latch. It was in
            // NEITHER the watch list nor the read list before — the parser
            // discarded the line entirely — so the settings pane could never
            // highlight Enable even after the radio said it was enabled. The
            // watch is the bridge P3's highlight consumes.
            RadioProperty.ForceWakeup,
            // Clone round 12 §3: the operator lockout mirror and its read
            // completion, so a consumer re-renders when a read commits.
            RadioProperty.Lockouts, RadioProperty.LockoutRead,
            // Clone round 12 §3 leg 2: the zeroize settle observable.
            RadioProperty.ZeroizeSettle)
    {
        // The FM-squelch cycle flag is NOT a mirrored radio property (it is
        // Core's own compensation bookkeeping), so it cannot ride the watched
        // set — it gets its own forwarded event, marshalled by Core like every
        // other.
        radio.FmSquelchCyclePendingChanged += (_, _) => FmSquelchCyclePendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>8-digit Hz string as reported ("01600000").</summary>
    public Confirmed<string> RxFrequency => Radio.State.RxFrequency;
    public Confirmed<string> TxFrequency => Radio.State.TxFrequency;

    /// <summary>The radio's tuning step (STEP answer / "Step 00001000").</summary>
    public Confirmed<FrequencyStep> Step => Radio.State.FrequencyStep;

    public Confirmed<ModulationMode> Modulation => Radio.State.ModulationMode;

    /// <summary>Bandwidth exactly as the radio last reported it (BAND lines
    /// are the read-back — the radio never rejects BA, probe R5).</summary>
    public Confirmed<string> Bandwidth => Radio.State.Bandwidth;

    public Confirmed<AgcSpeed> Agc => Radio.State.AgcSpeed;

    /// <summary>Current channel's receive-only flag (RXONLY lines).</summary>
    public Confirmed<YesNo> RxOnly => Radio.State.ChannelRxOnly;

    /// <summary>True only when the radio has CONFIRMED it is in SSB this
    /// session — the gate for SSB-only controls (STEP/INC/DEC).</summary>
    public bool IsSsbConfirmed =>
        Radio.State.OperatingMode.IsConfirmed
        && Radio.State.OperatingMode.Value == OperatingMode.Ssb;

    // ---- Intents (each answer line is the read-back) ---------------------

    /// <summary>RX=TX (FR).</summary>
    public void SetFrequency(string frequency) => Radio.Ssb.SetFrequency(frequency);
    public void SetRxFrequency(string frequency) => Radio.Ssb.SetRxFrequency(frequency);
    public void SetTxFrequency(string frequency) => Radio.Ssb.SetTxFrequency(frequency);
    public void Increment() => Radio.Ssb.IncrementFrequency();
    public void Decrement() => Radio.Ssb.DecrementFrequency();
    public void SetStep(FrequencyStep step) => Radio.Ssb.SetStep(step);
    public void SetModulation(ModulationMode mode) => Radio.Ssb.SetModulation(mode);
    public void SetBandwidth(string bandwidth) => Radio.Ssb.SetBandwidth(bandwidth);
    public void SetAgc(AgcSpeed speed) => Radio.Ssb.SetAgc(speed);
    public void SetRxOnly(YesNo value) => Radio.Ssb.SetRxOnly(value);

    // ====================================================================
    // Phase R / Wave 2 settings vocabulary (GUI rejigger F8, E6, SSB
    // settings pane). Each read exposes the W1 mirror (Confirmed<T>);
    // settings with NO captured answer shape have NO read — the button sends
    // and the display stays "—" (render honestly, never fake confirmation —
    // plan/phase-r-classification.md). Each intent routes straight to the W1
    // SsbController builder.
    //
    // UPDATED round 3 (V7): that no-read set is now just FORCE_W and
    // RWAS_KEY, the two the radio genuinely cannot report. PRE / INTCOUP /
    // KWAT gained PROVISIONAL, old-app-derived mirrors and DO have reads —
    // see RxPreamp / InternalCoupler / OneKilowattPa below.
    // ====================================================================

    // ---- Confirmed reads (mirrors — W1) ---------------------------------

    /// <summary>Analog squelch (SQUELCH ON/OFF).</summary>
    public Confirmed<OnOff> AnalogSquelch => Radio.State.AnalogSquelch;
    /// <summary>FM squelch (FMSQUELCH ON/OFF).</summary>
    public Confirmed<OnOff> FmSquelch => Radio.State.FmSquelch;
    /// <summary>Digital squelch (DGT_SQUELCH ON/OFF — an independent peer;
    /// reported inside the DV response group but NOT gated on DV, protocol.md
    /// digital-squelch section).</summary>
    public Confirmed<OnOff> DigitalSquelch => Radio.State.DigitalSquelch;
    /// <summary>Digital voice (DV ON/OFF).</summary>
    public Confirmed<OnOff> DigitalVoice => Radio.State.DigitalVoice;
    /// <summary>SQ_LEVEL payload verbatim ("HIGH").</summary>
    public Confirmed<string> SquelchLevel => Radio.State.SquelchLevel;
    /// <summary>Voice compression (COMPRESS ON/OFF).</summary>
    public Confirmed<OnOff> Compression => Radio.State.Compression;
    /// <summary>BFO offset payload verbatim ("+0000").</summary>
    public Confirmed<string> BfoOffset => Radio.State.BfoOffset;
    /// <summary>FMSQ_TYPE payload verbatim ("TONE").</summary>
    public Confirmed<string> FmSquelchType => Radio.State.FmSquelchType;
    public Confirmed<OnOff> FmTone => Radio.State.FmTone;
    /// <summary>FMDEV payload verbatim ("8.0").</summary>
    public Confirmed<string> FmDeviation => Radio.State.FmDeviation;
    /// <summary>CWOFFSET payload verbatim ("0000"/"1000").</summary>
    public Confirmed<string> CwOffset => Radio.State.CwOffset;
    /// <summary>AVS payload verbatim ("OFF"/"ON"/"NOT INSTALLED").</summary>
    public Confirmed<string> Avs => Radio.State.Avs;
    public Confirmed<int> RfGain => Radio.State.RfGain;
    /// <summary>ANTENNA payload verbatim ("AUTO").</summary>
    public Confirmed<string> Antenna => Radio.State.Antenna;
    /// <summary>RETRANS payload verbatim ("DISABLED").</summary>
    public Confirmed<string> Retransmit => Radio.State.Retransmit;
    public Confirmed<EnabledDisabled> Rwas => Radio.State.Rwas;
    public Confirmed<EnabledDisabled> UnkeyMask => Radio.State.UnkeyMask;
    public Confirmed<OnOff> Beep => Radio.State.Beep;
    /// <summary>PREPOST dump values verbatim ("ENABLE"/"DISABLE"/"SLOW"/"FAST").</summary>
    public Confirmed<string> PrePostFilter => Radio.State.PrePostFilter;
    public Confirmed<string> PrePostRxAntenna => Radio.State.PrePostRxAntenna;
    public Confirmed<string> PrePostScanRate => Radio.State.PrePostScanRate;

    // Round-3 V7 PROVISIONAL mirrors (docs/protocol.md "Old-app-derived SSB
    // query set"): verbatim payloads, old-app-derived spellings — these three
    // used to have no read at all ("sends but never highlights").
    /// <summary>PREAMP payload verbatim (provisional: "ENABLED"/"BYPASSED").</summary>
    public Confirmed<string> RxPreamp => Radio.State.RxPreamp;
    /// <summary>INTCOUPLER payload verbatim (provisional: "ENABLED"/"BYPASSED").</summary>
    public Confirmed<string> InternalCoupler => Radio.State.InternalCoupler;
    /// <summary>KWATT payload verbatim (provisional: "YES"/"NO").</summary>
    public Confirmed<string> OneKilowattPa => Radio.State.OneKilowattPa;

    // ---- Intents (each answer line is the read-back) --------------------

    // Squelch peers + DV + compression (Operate pane F8).
    public void SetSquelch(OnOff state) => Radio.Ssb.SetSquelch(state);
    public void SetFmSquelch(OnOff state) => Radio.Ssb.SetFmSquelch(state);
    public void SetDigitalSquelch(OnOff state) => Radio.Ssb.SetDigitalSquelch(state);
    public void SetDigitalVoice(OnOff state) => Radio.Ssb.SetDigitalVoice(state);
    public void SetSquelchLevel(SquelchLevel level) => Radio.Ssb.SetSquelchLevel(level);
    public void SetCompression(OnOff state) => Radio.Ssb.SetCompression(state);
    // (SelectModem/ModemOff moved to ModemSurface — round 8 ED.)
    public void SetBfoOffset(int offsetHz) => Radio.Ssb.SetBfoOffset(offsetHz);

    // FM group + CW offset + AVS + RF + preamp (settings pane, Audio/RX).
    public void SetFmSquelchType(FmSquelchType type) => Radio.Ssb.SetFmSquelchType(type);
    public void SetFmTone(OnOff state) => Radio.Ssb.SetFmTone(state);
    public void SetFmDeviation(string deviation) => Radio.Ssb.SetFmDeviation(deviation);
    public void SetCwOffset(int offsetHz) => Radio.Ssb.SetCwOffset(offsetHz);
    public void SetAvs(OnOff state) => Radio.Ssb.SetAvs(state);
    public void SetRfGain(int gain) => Radio.Ssb.SetRfGain(gain);
    public void SetRxPreamp(BypassEnable state) => Radio.Ssb.SetRxPreamp(state);

    // TX / antenna (settings pane).
    // NOTE: the Keyline intent (K ON|OFF) is a DEFERRED skip this wave. The
    // Core guard GuiOutScopeGuardTests forbids the keying builder's name in
    // ANY app-layer source (even a comment — it is a raw-text scan) until the
    // keying UI "lands with its own confirm flow"; the confirm flow is
    // designed, but landing it needs the Core owner (W1) to drop that name
    // from the guard's GuiOutBuilderNames list — a Core-test edit outside this
    // wave's file ownership. Re-exposed once that amendment lands.
    public void SetAntenna(AntennaPort port) => Radio.Ssb.SetAntenna(port);
    public void SetInternalCoupler(BypassEnable state) => Radio.Ssb.SetInternalCoupler(state);
    public void SetOneKilowattPa(YesNo installed) => Radio.Ssb.SetOneKilowattPa(installed);
    public void SetRetransmit(EnabledDisabled state) => Radio.Ssb.SetRetransmit(state);
    public void SetPrePostFilter(OnOff enabled) => Radio.Ssb.SetPrePostFilter(enabled);
    public void SetPrePostRxAntenna(OnOff enabled) => Radio.Ssb.SetPrePostRxAntenna(enabled);
    public void SetPrePostScanRate(PrePostScanRate rate) => Radio.Ssb.SetPrePostScanRate(rate);

    // RWAS group (settings pane).
    public void SetRwas(EnabledDisabled state) => Radio.Ssb.SetRwas(state);
    public void SetForceWakeup(EnabledDisabled state) => Radio.Ssb.SetForceWakeup(state);
    public void SetRwasKey(int key) => Radio.Ssb.SetRwasKey(key);
    public void SetUnkeyMask(EnabledDisabled state) => Radio.Ssb.SetUnkeyMask(state);

    // Device (settings pane).
    public void SetBeep(OnOff state) => Radio.Ssb.SetBeep(state);

    /// <summary>
    /// Round-3 Y1: re-read the WHOLE SSB settings pane from the radio — the
    /// old-app-derived per-setting query set (V7). Seventeen reads, one per
    /// setting, in the order the pane lays them out; every one is visible in
    /// the Console like any other send. Nothing is written.
    ///
    /// There is no bulk alternative: `SH` carries only a SUBSET (AVS,
    /// ANTENNA, CWOFFSET, RWAS, RETRANS…), which is exactly why the pane
    /// previously showed "—" for the FM group, PREPOST, UNKEY_MASK and BEEP.
    /// The old app queries per setting too (BindingRegistry.cs:453-460).
    ///
    /// PROVISIONAL: no command here has ever been sent to this radio by this
    /// project — docs/protocol.md "Old-app-derived SSB query set", bench item
    /// A5b. Callers gate on a CONFIRMED SSB mode (four of the queries are
    /// mode-scoped: RWAS/UNKEY_M/ANTENNA are SSB-only, AVS is refused in HOP).
    /// FORCE_W and RWAS_KEY are absent by design — neither can be read back.
    /// </summary>
    public void RequestSettings()
    {
        // Audio / RX.
        Radio.Ssb.QueryFmSquelchType();
        Radio.Ssb.QueryFmTone();
        Radio.Ssb.QueryFmDeviation();
        Radio.Ssb.QueryCwOffset();
        Radio.Ssb.QueryAvs();
        Radio.Ssb.QueryRxPreamp();
        Radio.Ssb.QueryRfGain();
        // TX / antenna.
        Radio.Ssb.QueryAntenna();
        Radio.Ssb.QueryInternalCoupler();
        Radio.Ssb.QueryOneKilowattPa();
        Radio.Ssb.QueryRetransmit();
        Radio.Ssb.QueryPrePostFilter();
        Radio.Ssb.QueryPrePostRxAntenna();
        Radio.Ssb.QueryPrePostScanRate();
        // RWAS group. RWAS_KEY still has no read-back at all (a bare query
        // answers ** ERROR **), and FORCE_W is still ABSENT HERE for its own
        // reason: a bare `FORCE_W` query answers NOTHING, so asking would put a
        // silent command on the wire for no gain. Round-12 §9 C3 changes only
        // what happens when the radio VOLUNTEERS its ENABLED line — that is now
        // mirrored (see ForceWakeup below) — not whether it can be polled.
        Radio.Ssb.QueryRwas();
        Radio.Ssb.QueryUnkeyMask();
        // Device.
        Radio.Ssb.QueryBeep();
        // The EIGHTEENTH read (round-12 §9 B3): compression. Until the P-2c
        // capture the mirror had no read path anywhere and latched the app's
        // own last echo for the whole session.
        Radio.Ssb.QueryCompression();
    }

    // ====================================================================
    // Clone round 12 P1 — the new surface.
    // ====================================================================

    /// <summary>
    /// Force-wakeup burst on key — a BOUNDED SESSION LATCH (§9 C3). Confirmed
    /// only ever means <see cref="EnabledDisabled.Enabled"/>: the radio reports
    /// enabling and says NOTHING about disabling, so unconfirmed means "not
    /// known to be enabled" and NEVER "confirmed disabled". A consumer may
    /// highlight Enable on a confirmed value; it may not highlight Disable on
    /// the absence of one.
    /// </summary>
    public Confirmed<EnabledDisabled> ForceWakeup => Radio.State.ForceWakeup;

    /// <summary>
    /// True while Core still owes an FM-squelch OFF→ON cycle (§3 leg 6). The
    /// clone campaign must not write <c>AnalogSquelch</c> while it is up — the
    /// cycle would overwrite whatever was just written. Pure data; reading it
    /// sends nothing.
    /// </summary>
    public bool IsFmSquelchCyclePending => Radio.IsFmSquelchCyclePending;

    /// <summary>Raised (marshalled) when <see cref="IsFmSquelchCyclePending"/>
    /// changes. Separate from <see cref="RadioSurface.Changed"/> because it is
    /// a COMPENSATION fact, not a mirrored radio property.</summary>
    public event EventHandler? FmSquelchCyclePendingChanged;

    // ---- X12: operator lockouts ----------------------------------------
    // Guard-scoped: GuiOutScopeGuardTests' X12 block pins which app-layer files
    // may name these two wrappers, and no view model is among them.

    /// <summary>The lockout mirror — THREE states, verbatim from Core:
    /// <see cref="LockoutReadState.Unknown"/> = never read this session,
    /// <see cref="LockoutReadState.InFlight"/> = a read is on the wire,
    /// <see cref="LockoutReadState.Completed"/> = these are the radio's rows.
    /// Keyed (family, section, item) EVERYWHERE, because item names repeat
    /// across sections.</summary>
    public LockoutMirror Lockouts => Radio.State.Lockouts;

    /// <summary>Completion record of the last lockout read — the id equals the
    /// one <see cref="RequestLockouts"/> returned, and <c>Answered == false</c>
    /// means NOTHING was published and the prior mirror stands.</summary>
    public AleReadCompletion LastLockoutRead => Radio.State.LastLockoutRead;

    /// <summary>Bare <c>PROGRAM</c> + bare <c>SELECT</c> + one closing sentinel
    /// — the whole lockout table in ONE sentinel-bracketed read. Purely a read;
    /// coalesces. Returns the operation's READ ID.</summary>
    public long RequestLockouts() => Radio.Ssb.QueryLockouts();

    /// <summary>Set ONE lockout row. <b>The caller must already be standing at
    /// the section's prompt</b>: the radio scopes a set to the ACTIVE PROMPT's
    /// mode section (captured 2026-08-18, round-12 P-1 — all six matrix cells
    /// moved exactly their own prompt's section), and nothing here switches
    /// modes on a caller's behalf.</summary>
    public void SetLockout(LockoutFamily family, LockoutSection section, string item, LockState state)
        => Radio.Ssb.SetLockout(family, section, item, state);

    // ---- X13: zeroize ---------------------------------------------------

    /// <summary><b>WIPES THE RADIO — IRREVERSIBLE.</b> Sends <c>ZERO</c> and
    /// arms Core's settle machine; the caller AWAITS
    /// <see cref="ZeroizeSettled"/> / <see cref="ZeroizeFaulted"/> rather than
    /// sleeping. Guard-scoped to the clone campaign alone.</summary>
    public void ZeroizeRadio() => Radio.Ssb.ZeroizeRadio();

    /// <summary>A <c>ZERO</c> is on the wire and the prompt has not returned.</summary>
    public bool IsZeroizeSettling => Radio.IsZeroizeSettling;

    /// <summary>The radio answered a prompt again after the last <c>ZERO</c>
    /// (captured: 9.4 s, same session, no reconnect — round-12 P-1).</summary>
    public bool ZeroizeSettled => Radio.ZeroizeSettled;

    /// <summary>The settle bound expired. The campaign must fault.</summary>
    public bool ZeroizeFaulted => Radio.ZeroizeFaulted;

    // ---- Round 11 §9A: the clone campaign's per-setting reads ---------------
    // The clone reads exactly the settings its MANIFEST carries, not the whole
    // pane sweep above — a campaign that also queried AVS/PREAMP/INTCOUPLER/
    // KWATT/RETRANS would put five reads on the wire for values the manifest
    // deliberately excludes. Each of these routes to an EXISTING query builder
    // (reads are not guard-scoped, and round 11 adds no read builders).

    /// <summary>SH — the current mode's block. The SSB block carries nine of
    /// the manifest's rows in one read.</summary>
    public void RequestStatus() => Radio.Show();

    /// <summary>STEP — the tuning step.</summary>
    public void RequestStep() => Radio.Ssb.QueryStep();

    /// <summary>COM — voice compression. Round-12 §9 B3 PRIMARY branch: bare
    /// <c>COM</c> answers <c>COMPRESS ON</c> (captured 2026-08-18, P-2 step c),
    /// so a read path exists at last.</summary>
    public void RequestCompression() => Radio.Ssb.QueryCompression();

    public void RequestFmSquelchType() => Radio.Ssb.QueryFmSquelchType();
    public void RequestFmTone() => Radio.Ssb.QueryFmTone();
    public void RequestFmDeviation() => Radio.Ssb.QueryFmDeviation();
    public void RequestRfGain() => Radio.Ssb.QueryRfGain();
    public void RequestBeep() => Radio.Ssb.QueryBeep();
    public void RequestUnkeyMask() => Radio.Ssb.QueryUnkeyMask();
    public void RequestPrePostFilter() => Radio.Ssb.QueryPrePostFilter();
    public void RequestPrePostRxAntenna() => Radio.Ssb.QueryPrePostRxAntenna();
    public void RequestPrePostScanRate() => Radio.Ssb.QueryPrePostScanRate();
}
