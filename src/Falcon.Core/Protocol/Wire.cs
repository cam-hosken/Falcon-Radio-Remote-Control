namespace Falcon.Core.Protocol;

// Wire vocabulary for the PRC-138 (RF-5022-family) ASCII remote protocol.
// Spec: docs/protocol.md (bench-confirmed). Mapping between enum values and
// wire strings is GENERATED SWITCH code, not [Description] reflection —
// reflection-based mapping breaks under Android AOT/trimming (plan §2.3).
//
// Parse methods return null for unrecognized payloads; callers decide whether
// that is a payload error (recognized token, junk payload) or noise.

public enum OperatingMode { Ssb, Ale, Hop }

public enum PowerLevel { Low, Medium, High }

public enum OnOff { On, Off }

public enum YesNo { Yes, No }

public enum ModulationMode { Usb, Lsb, Ame, Cw, Fm }

public enum AgcSpeed { Off, Slow, Medium, Fast, Data }

/// <summary>Keyline as reported. OFF/MIC/AUX appear in SH blocks and tune
/// lifecycles; keying also emits async KEY ON/KEY OFF (owner knowledge, B7)
/// — ON is accepted so an async key report can never be a parse error.
/// There is deliberately no ToWire: the K SET command takes ON/OFF only and
/// lives behind the token-gated <c>SsbController.SetKeyline</c> (Phase R,
/// plan-gui-rejigger.md round 4 — it TRANSMITS, protocol.md hazard table).</summary>
public enum KeylineState { Off, Mic, Aux, On }

/// <summary>ENABLE/DISABLE-family settings (RWAS group, RETR). Sent as the
/// HELP minimum abbreviations ENA/DIS (bench: both short and full forms
/// accepted — protocol.md RWAS section); reported as ENABLED/DISABLED.</summary>
public enum EnabledDisabled { Enabled, Disabled }

/// <summary>SQ_L values per HELP "(LO/MEdium/HIgh)". The mirror stores the
/// report verbatim (string). Clone round 12 §9 B4: all three REPORT spellings
/// are now captured (r12-p2, 2026-08-19) and they are NOT the set tokens —
/// LO→LOW, MEDIUM→MED, HIGH→HIGH. Read a report through
/// <see cref="Wire.SquelchLevelFromReport"/>, never by comparing against
/// <see cref="Wire.ToWire(SquelchLevel)"/>.</summary>
public enum SquelchLevel { Low, Medium, High }

/// <summary>FMSQ_T values per HELP "(NOIse/TOne)". Never sent by this app;
/// the captured answer is "FMSQ_TYPE tone" (lowercase, mirrored verbatim).</summary>
public enum FmSquelchType { Noise, Tone }

/// <summary>ANTENNA port per HELP "(BNc/AUto/TUned)". Never sent by this
/// app; SH reports "ANTENNA   auto" (mirrored verbatim).</summary>
public enum AntennaPort { Bnc, Auto, Tuned }

/// <summary>PRE / INTCOUP values per HELP "(BYpass/ENable)". Never sent by
/// this app; no answer shape has ever been captured (bench item).</summary>
public enum BypassEnable { Bypass, Enable }

/// <summary>LIG values (old-app-derived: LIG is absent from the captured
/// HELP menus; the sentinel probe proved the command answers "LIGHT …" in
/// every mode). Never sent by this app; answer payload uncaptured.</summary>
public enum BacklightFunction { Off, Momentary }

/// <summary>PREPOST SCAN rate. The captured dump reports "PREPOST SCAN
/// SLOW" (session-20); set syntax is old-app-derived, never sent.</summary>
public enum PrePostScanRate { Slow, Fast }

/// <summary>Frequency step as reported by STEP ("Step 00001000" = 1 kHz).</summary>
public enum FrequencyStep { OneHz, TenHz, OneHundredHz, OneKHz, TenKHz, OneHundredKHz }

/// <summary>ALE link state from unsolicited/announced lines — only the
/// shapes actually captured. CAPTURE INVENTORY, corrected 2026-08-23 (round 15
/// item I, critic F75): SCANNING / SCAN STOPPED / CALLING / SENDING were the
/// single-station captures; LINKED is kept by Stage 1 audit decision, still
/// uncaptured, pending the two-station session; and probe P14 (2026-08-22,
/// transcripts <c>bench/transcripts/p14b-…</c> / <c>p14c-…</c>) captured the
/// bare-<c>STA</c> LQA lifecycle single-station — <c>SOUNDING &lt;self&gt;
/// CHANNEL: nn</c> → <see cref="Sounding"/>, <c>EXCHANGE &lt;ind&gt; CHANNEL:
/// nn</c> → <see cref="Exchanging"/>, and <c>SH</c>'s first line
/// <c>LQA/SOUND</c> → <see cref="Lqa"/>, which states only that an LQA is
/// running and NOT which kind (the honest kind-unknown mirror, I-D1). Still
/// two-station gated and NOT states here (plan §5.6): SIGNAL RECEIVED (one
/// P14 run-1 capture with scan stopped, no lifecycle), RECEIVING,
/// TERMINATING.</summary>
public enum AleLinkState
{
    Scanning, Stopped, Calling, Sending, Linked, Sounding, Exchanging, Lqa,
    // The inbound-call handshake, CAPTURED 2026-08-24 (field transcript
    // field-ale-first-contact-20260824-2144.txt, the first two-station
    // contact): ` SIGNAL RECEIVED ` then `RECEIVING CALL  `, resolving to
    // LINKED (21:56:39-55) or back to SCANNING when the call was not for
    // this station (22:01:41-44). Appended so no existing ordinal moves.
    SignalReceived, ReceivingCall,
}

/// <summary>The ONE on-air term (round 15 I-D2, critic F69). Every consumer
/// that must refuse to key the radio — Call/Scan, the AMD send, the clone
/// campaigns, the scan-group and address-book writes, LQA Now/Schedule — asked
/// this question with its OWN enum list, and the five lists had diverged.
/// TRUE for every state in which the radio has ANNOUNCED that it is
/// transmitting or holding a link: a call handshake (Calling/Sending/Linked)
/// or an LQA run (Sounding/Exchanging/Lqa — P14: a bare STA transmits on every
/// channel of the target's group for minutes). Scanning is NOT on air — the
/// radio is listening — and consumers that also refuse while scanning (the
/// clone campaigns, the programming writes) keep that as their own separate
/// term.</summary>
public static class AleLinkStateExtensions
{
    public static bool IsOnAir(this AleLinkState s) => s is
        AleLinkState.Calling or AleLinkState.Sending or AleLinkState.Linked
        or AleLinkState.Sounding or AleLinkState.Exchanging or AleLinkState.Lqa
        // The inbound handshake keys the radio to acknowledge (the field
        // capture shows KEY OFF between RECEIVING CALL and LINKED), so both
        // its states hold the on-air refusals. The ONE carve-out — the AMD
        // send while LINKED (manual §2.5.2.7(g): "linked or scanning") — is
        // the consumer's own question, not a change to this term.
        or AleLinkState.SignalReceived or AleLinkState.ReceivingCall;
}

/// <summary>
/// ALE fill-completeness gate, driven ONLY by the radio's own gate lines
/// ("PRG 1-3 CHAR SLF" → "IND NOT PROGRMD" → "NO CHANS TO SCAN").
/// IN_PROG is deliberately NOT a state here: probe R7 (2026-08-02) proved it
/// appears with complete, working fills too — it is informational noise.
/// Complete is inferred from SCANNING (the radio only auto-scans with a
/// complete fill — docs/protocol.md, "What ZERO actually does" corollary).
/// </summary>
public enum AleFillState { Unknown, NeedSelfAddress, NeedIndividual, NeedChannels, Complete }

public enum HopSyncState
{
    NoSync, InSync, AwaitingSync, SendingSyncRequest,
    SyncRequestReceived, SendingSyncResponse, SyncFailed
}

public enum HopType { Narrowband, Wideband, List }

/// <summary>
/// The SIGNALLING half of a <c>HOP&gt;</c> modem preset's mode phrase
/// (clone-field round 2 F9/F11). At <c>SSB&gt;</c> the two halves are welded
/// into one phrase token (<c>ASYNC REM</c> / <c>SYNC DAT</c>) because that is
/// how the SSB builder has always sent them; the <c>HOP&gt;</c> line was
/// captured as two independent words (P5b:
/// <c>MODEM PRESET 9 NAME TST9 ASYNC REMOTE BAUD 1200</c> and
/// <c>MODEM PRESET 9 SYNC DATA</c> were both applied), so the HOP builder
/// takes them as two arguments.
/// </summary>
public enum SyncMode { Async, Sync }

/// <summary>
/// The PORT half of a <c>HOP&gt;</c> modem preset's mode phrase — HELP's
/// <c>ASync (REMote/DATa)</c> options, standing on their own. Captured spelled
/// out in full on the wire (<c>REMOTE</c> / <c>DATA</c>), unlike the SSB
/// builder's abbreviated <c>REM</c>/<c>DAT</c>: P5b's accepted lines are the
/// only capture there is and they are the long forms.
/// </summary>
public enum DataMode { Data, Remote }

public static class Wire
{
    // ---- command strings (what we send) --------------------------------

    public static string ToCommand(this OperatingMode m) => m switch
    {
        OperatingMode.Ssb => "SS",
        OperatingMode.Ale => "ALE",
        OperatingMode.Hop => "HO",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    public static string ToWire(this PowerLevel v) => v switch
    {
        PowerLevel.Low => "LOW",
        PowerLevel.Medium => "MED",
        PowerLevel.High => "HI",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this OnOff v) => v switch
    {
        OnOff.On => "ON",
        OnOff.Off => "OFF",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this YesNo v) => v switch
    {
        YesNo.Yes => "YES",
        YesNo.No => "NO",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this ModulationMode v) => v switch
    {
        ModulationMode.Usb => "USB",
        ModulationMode.Lsb => "LSB",
        ModulationMode.Ame => "AME",
        ModulationMode.Cw => "CW",
        ModulationMode.Fm => "FM",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this AgcSpeed v) => v switch
    {
        AgcSpeed.Off => "OFF",
        AgcSpeed.Slow => "SLOW",
        AgcSpeed.Medium => "MED",
        AgcSpeed.Fast => "FAST",
        AgcSpeed.Data => "DATA",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this EnabledDisabled v) => v switch
    {
        EnabledDisabled.Enabled => "ENA",
        EnabledDisabled.Disabled => "DIS",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this SquelchLevel v) => v switch
    {
        SquelchLevel.Low => "LO",
        SquelchLevel.Medium => "MEDIUM",
        SquelchLevel.High => "HIGH",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this FmSquelchType v) => v switch
    {
        FmSquelchType.Noise => "NOISE",
        FmSquelchType.Tone => "TONE",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this AntennaPort v) => v switch
    {
        AntennaPort.Bnc => "BNC",
        AntennaPort.Auto => "AUTO",
        AntennaPort.Tuned => "TUNED",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this BypassEnable v) => v switch
    {
        BypassEnable.Bypass => "BYPASS",
        BypassEnable.Enable => "ENABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this BacklightFunction v) => v switch
    {
        BacklightFunction.Off => "OFF",
        BacklightFunction.Momentary => "MOMENTARY",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string ToWire(this PrePostScanRate v) => v switch
    {
        PrePostScanRate.Slow => "SLOW",
        PrePostScanRate.Fast => "FAST",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>HOPTYPE argument (round-trips with ParseHopType — the
    /// "Hoptype 00 NB" report spelling).</summary>
    public static string ToWire(this HopType v) => v switch
    {
        HopType.Narrowband => "NB",
        HopType.Wideband => "WB",
        HopType.List => "LIST",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>The <c>HOP&gt;</c> preset line's signalling word (P5b).</summary>
    public static string ToWire(this SyncMode v) => v switch
    {
        SyncMode.Async => "ASYNC",
        SyncMode.Sync => "SYNC",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>The <c>HOP&gt;</c> preset line's port word, spelled out (P5b).</summary>
    public static string ToWire(this DataMode v) => v switch
    {
        DataMode.Data => "DATA",
        DataMode.Remote => "REMOTE",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>Listing/report word → the signalling half. Null for anything
    /// else (the AGC precedent: no match, no value).</summary>
    public static SyncMode? ParseSyncMode(string? s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "ASYNC" => SyncMode.Async,
        "SYNC" => SyncMode.Sync,
        _ => null,
    };

    /// <summary>Listing/report word → the port half. Both the long forms the
    /// <c>HOP&gt;</c> line prints and the SSB builder's abbreviations are
    /// accepted, because a clone file may hold either spelling.</summary>
    public static DataMode? ParseDataMode(string? s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "DATA" or "DAT" => DataMode.Data,
        "REMOTE" or "REM" => DataMode.Remote,
        _ => null,
    };

    /// <summary>The BAUD values a <c>HOP&gt;</c> preset really stores —
    /// MEASURED by set + read-back on preset 9 (P5c, transcript
    /// <c>bench/transcripts/p5c-hop-modem-baud-20260821-182807.jsonl</c>):
    /// exactly 75, 150 and 300. 50, 100, 110, 200, 600, 1200, 2400, 4800 and
    /// 9600 are SILENTLY IGNORED — the line is echoed with the OLD value and
    /// no error, so a client that does not read back cannot tell.</summary>
    public static readonly IReadOnlyList<string> HopModemBauds = ["75", "150", "300"];

    /// <summary>FMDE values per HELP "(5.0/6.5/8.0) KHz". The captured
    /// answer token is FMDEV ("FMDEV 8.0").</summary>
    public static readonly IReadOnlyList<string> FmDeviationValues = ["5.0", "6.5", "8.0"];

    // ====================================================================
    // ACCEPTANCE BOUNDS — the numeric ranges and value sets the builders
    // refuse outside of, PUBLISHED so there is exactly ONE copy of each.
    //
    // WHY THEY ARE PUBLIC (clone round 12 P2, audit round 2, BLOCKER). A
    // builder that keeps its bound to itself can only say no ON THE WIRE, and
    // for the clone campaign the wire is the far side of a destructive wipe: a
    // crafted `"RfGain": "101"` used to load, pass the preflight, and be
    // refused in leg 6 — on a radio already erased. The clone's DOOR must
    // therefore prove a value is one the builder will take, and the only
    // honest way to do that is for both to read the SAME number. The pattern
    // is <see cref="FmDeviationValues"/>'s, which the FMDE builder has always
    // validated against; these finish the job.
    //
    // Each bound's evidence lives with the builder that sends it.
    // ====================================================================

    /// <summary>
    /// <c>RXF</c> / <c>TXF</c> / <c>FR</c> — the ACCEPTED frequency window, in
    /// Hz. <b>MEASURED 2026-08-21</b> (probe P2,
    /// <c>bench/transcripts/p2-freq-range-20260821-175802.jsonl</c>): set +
    /// <c>SH</c> read-back on CH 00, RX and TX swept separately.
    /// <c>01600000</c> through <c>59999999</c> store and read back on BOTH
    /// sides; <c>60000000</c>, <c>79999999</c>, <c>99999999</c> and
    /// <c>01599999</c> each answer <c>** ERROR **</c> and leave the value
    /// unchanged.
    ///
    /// <para><b>The ceiling was 29 999 999 until this round, and it was
    /// wrong</b> (plan-clone-field-round2.md F5, decision D3). It was a
    /// band-plan assumption, not a measurement — and a real source radio
    /// stored 51.5 / 51.0 / 50.25 MHz channels, which the clone campaign then
    /// refused to write. The bound is RADIO-WIDE and lives here ONCE: the
    /// controller's own <c>ValidateFrequency</c> and both frequency
    /// ViewModels read these two constants and nothing else.</para>
    /// </summary>
    public const int MinFrequencyHz = 1_600_000;
    public const int MaxFrequencyHz = 59_999_999;

    /// <summary>BF — a signed 4-digit value per HELP "(+/- xxxx)". The
    /// accepted range is a bench item; ±9999 is the representable span.</summary>
    public const int BfoOffsetMinHz = -9999;
    public const int BfoOffsetMaxHz = 9999;

    /// <summary>CWOFF — the two 4-digit forms matching the report shape.</summary>
    public static readonly IReadOnlyList<int> CwOffsetValuesHz = [0, 1000];

    /// <summary>RF — HELP-derived "RF 0-100".</summary>
    public const int RfGainMin = 0;
    public const int RfGainMax = 100;

    /// <summary>CONT and INT — both "0-8", both sent zero-padded to two
    /// digits.</summary>
    public const int ZeroToEightMin = 0;
    public const int ZeroToEightMax = 8;

    /// <summary>MAXCH — "0-100".</summary>
    public const int MaxScanChannelsMin = 0;
    public const int MaxScanChannelsMax = 100;

    /// <summary>TIME_OU — 0-60 minutes. <b>0 is VALID despite HELP's
    /// "1-60"</b>: measured (session-18, `TIME_OU 0` echoes `TIME_OUT 000`).
    /// The radio wins.</summary>
    public const int LinkTimeoutMinMinutes = 0;
    public const int LinkTimeoutMaxMinutes = 60;

    /// <summary>TUNE — 1-60 seconds. Note the floor is ONE, unlike the two
    /// bounds above: a zero tune time is refused.</summary>
    public const int TuneTimeMinSeconds = 1;
    public const int TuneTimeMaxSeconds = 60;

    /// <summary>
    /// THE FACTORY-DEFAULT STORED CHANNEL, in the <c>DI</c> DUMP'S OWN
    /// SPELLINGS (plan-clone-write-structural.md D4).
    ///
    /// <para><b>Why it is a wire fact and lives here.</b> <c>DI 0 99</c> prints
    /// EVERY slot — a never-written one prints this row, and a ZEROIZE resets
    /// every slot to it (protocol.md, "There is no 'unprogrammed channel'
    /// shape"; re-confirmed by the 2026-08-18 zeroize capture, where
    /// <c>DI 50 50</c> answered <c>01600000 USB SL 2.7 RXONLY NO</c> on a
    /// freshly wiped radio). It is therefore the one row a clone never has to
    /// store and never has to send: the radio already holds it after the wipe.
    /// ONE copy, in the dump's abbreviations (<c>SL</c>, not <c>SLOW</c>) so a
    /// stored row can be compared against it verbatim.</para>
    /// </summary>
    public static readonly StoredChannel DefaultChannel =
        new("01600000", "01600000", "USB", "SL", "2.7", "NO");

    /// <summary>One stored SSB channel's six values, as the <c>DI</c> dump
    /// prints them. Only <see cref="DefaultChannel"/> is expressed this way in
    /// Core: it exists so that constant has named fields rather than six loose
    /// strings.</summary>
    public readonly record struct StoredChannel(
        string RxFrequency, string TxFrequency, string Mode, string Agc, string Bandwidth, string RxOnly);

    /// <summary>The stored modem presets this radio has AT AN <c>SSB&gt;</c> OR
    /// <c>ALE&gt;</c> PROMPT. The SELECTOR the engage command takes is one of
    /// these numbers (it also takes a NAME, which the clone never sends — the
    /// file stores the mirror string, whose engagement form leads with the
    /// number).
    /// <para><b>PROMPT-SCOPED since clone-field round 2 F9</b> — see
    /// <see cref="ModemPresetScope"/>. This pair is the <c>SSB&gt;</c>/<c>ALE&gt;</c>
    /// band, not the whole book.</para></summary>
    public const int ModemPresetMin = 0;
    public const int ModemPresetMax = 6;

    public static string ToWire(this FrequencyStep v) => v switch
    {
        FrequencyStep.OneHz => "00000001",
        FrequencyStep.TenHz => "00000010",
        FrequencyStep.OneHundredHz => "00000100",
        FrequencyStep.OneKHz => "00001000",
        FrequencyStep.TenKHz => "00010000",
        FrequencyStep.OneHundredKHz => "00100000",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>
    /// Every BA value this radio knows across all modulations (probe R5).
    /// The radio never rejects BA — out-of-range values are ignored and the
    /// answer reports the kept value, so the response IS the read-back.
    /// </summary>
    public static readonly IReadOnlyList<string> BandwidthValues =
        new[] { "0.35", "0.68", "1.0", "1.5", "2.0", "2.4", "2.7", "3.0", "4.0", "5.0", "6.0" };

    /// <summary>
    /// The MEASURED per-modulation bandwidth acceptance sets (probe R5,
    /// 2026-08-02) — not the HELP menu's claims: FM accepts 1.0–2.7, wider
    /// than HELP's "(2.7)". LSB was not separately probed; HELP groups it
    /// with USB. These drive the BW choice list; the radio's answer line
    /// remains the display authority either way (no-reject rule).
    /// </summary>
    public static IReadOnlyList<string> AllowedBandwidths(ModulationMode m) => m switch
    {
        ModulationMode.Usb or ModulationMode.Lsb => ["1.5", "2.0", "2.4", "2.7", "3.0"],
        ModulationMode.Ame => ["3.0", "4.0", "5.0", "6.0"],
        ModulationMode.Cw => ["0.35", "0.68", "1.0", "1.5"],
        ModulationMode.Fm => ["1.0", "1.5", "2.0", "2.4", "2.7"],
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    // ---- response parsing (what the radio says) -------------------------
    // Inputs are already uppercased/trimmed by the parser.

    public static PowerLevel? ParsePowerLevel(string s) => s switch
    {
        "LOW" => PowerLevel.Low,
        "MED" => PowerLevel.Medium,
        "HI" => PowerLevel.High,
        _ => null,
    };

    public static OnOff? ParseOnOff(string s) => s switch
    {
        "ON" => OnOff.On,
        "OFF" => OnOff.Off,
        _ => null,
    };

    public static YesNo? ParseYesNo(string s) => s switch
    {
        "YES" => YesNo.Yes,
        "NO" => YesNo.No,
        _ => null,
    };

    public static ModulationMode? ParseModulation(string s) => s switch
    {
        "USB" => ModulationMode.Usb,
        "LSB" => ModulationMode.Lsb,
        "AME" => ModulationMode.Ame,
        "CW" => ModulationMode.Cw,
        "FM" => ModulationMode.Fm,
        _ => null,
    };

    public static AgcSpeed? ParseAgcSpeed(string s) => s switch
    {
        "OFF" => AgcSpeed.Off,
        "SLOW" => AgcSpeed.Slow,
        "MED" => AgcSpeed.Medium,
        "FAST" => AgcSpeed.Fast,
        "DATA" => AgcSpeed.Data,
        _ => null,
    };

    /// <summary>
    /// The <c>DI</c> DUMP's own AGC abbreviation → the enum. <b>PROVISIONAL</b>
    /// (protocol.md, "Channel dump and diagnostics"): only <c>SL</c> and
    /// <c>ME</c> have ever been captured; the OFF / FAST / DATA dump forms are
    /// unknown. Both the abbreviations and the full wire spellings share a
    /// UNIQUE two-character prefix across the five values (OF · SL · ME · FA ·
    /// DA), so a prefix match covers whichever the radio turns out to print
    /// without inventing a spelling. An unmatched token returns null — the
    /// caller reports it rather than guessing.
    ///
    /// <para>ONE mapping, ONE home (plan-clone-field-round2.md F5, decision D3).
    /// It used to exist twice: the channel editor's prefix map, and a
    /// two-entry <c>SL</c>/<c>ME</c> copy inside <c>CloneService</c> that fell
    /// through to <see cref="ParseAgcSpeed"/>. The clone therefore could not
    /// read the source radio's own CH 09 — dump token <c>FA</c> — and reported
    /// it as a value the radio does not accept (field summary, 2026-08-21).
    /// The duplicate is DELETED and both callers read this.</para>
    /// </summary>
    public static AgcSpeed? ParseDumpAgc(string? token)
    {
        var t = (token ?? "").Trim().ToUpperInvariant();
        if (t.Length < 2) return null;
        return t[..2] switch
        {
            "OF" => AgcSpeed.Off,
            "SL" => AgcSpeed.Slow,
            "ME" => AgcSpeed.Medium,
            "FA" => AgcSpeed.Fast,
            "DA" => AgcSpeed.Data,
            _ => null,
        };
    }

    /// <summary>
    /// The <c>SQ_LEVEL</c> REPORT spelling → the enum. Clone round 12 §9 B4.
    ///
    /// <para>The set vocabulary and the report vocabulary are DIFFERENT
    /// (<see cref="ToWire(SquelchLevel)"/> emits <c>LO</c>/<c>MEDIUM</c>/
    /// <c>HIGH</c>; the radio answers <c>LOW</c>/<c>MED</c>/<c>HIGH</c>) —
    /// captured 2026-08-19, transcript r12-p2, graduated to
    /// docs/protocol.md "SQ_LEVEL's three report spellings — CAPTURED". They
    /// coincide on HIGH alone, which is why only HIGH ever highlighted.</para>
    ///
    /// <para><b>TRY-PARSE contract.</b> Exactly the three captured spellings
    /// map; ANY other payload returns null, and a null must light NOTHING.
    /// The verbatim string mirror is untouched — the parser still stores what
    /// the radio said, and this is the DISPLAY-side reader of it.</para>
    /// </summary>
    public static SquelchLevel? SquelchLevelFromReport(string s) => s switch
    {
        "LOW" => SquelchLevel.Low,
        "MED" => SquelchLevel.Medium,
        "HIGH" => SquelchLevel.High,
        _ => null,
    };

    public static KeylineState? ParseKeyline(string s) => s switch
    {
        "OFF" => KeylineState.Off,
        "MIC" => KeylineState.Mic,
        "AUX" => KeylineState.Aux,
        "ON" => KeylineState.On,
        _ => null,
    };

    public static FrequencyStep? ParseFrequencyStep(string s) => s switch
    {
        "00000001" => FrequencyStep.OneHz,
        "00000010" => FrequencyStep.TenHz,
        "00000100" => FrequencyStep.OneHundredHz,
        "00001000" => FrequencyStep.OneKHz,
        "00010000" => FrequencyStep.TenKHz,
        "00100000" => FrequencyStep.OneHundredKHz,
        _ => null,
    };

    /// <summary>Report-side ENABLED/DISABLED (RWAS / UNKEY_M answers —
    /// both values bench-documented, protocol.md RWAS section).</summary>
    public static EnabledDisabled? ParseEnabledDisabled(string s) => s switch
    {
        "ENABLED" => EnabledDisabled.Enabled,
        "DISABLED" => EnabledDisabled.Disabled,
        _ => null,
    };

    public static HopType? ParseHopType(string s) => s switch
    {
        "NB" => HopType.Narrowband,
        "WB" => HopType.Wideband,
        "LIST" => HopType.List,
        _ => null,
    };

    /// <summary>
    /// Normalize a BAND payload to the canonical form in
    /// <see cref="BandwidthValues"/>. Accepts both "0.35" and ".35" spellings
    /// (HELP prints "0.35"; the exact SH spelling for sub-1 values has not
    /// been captured, so both are tolerated). Returns null if unrecognized.
    /// </summary>
    public static string? NormalizeBandwidth(string s)
    {
        var v = s.Trim();
        if (v.StartsWith('.')) v = "0" + v;
        foreach (var known in BandwidthValues)
            if (v == known) return known;
        return null;
    }
}

/// <summary>
/// WHICH MODEM PRESET NUMBERS EXIST AT WHICH PROMPT (clone-field round 2 F9 /
/// F10 / F11, decision A-8). The "presets are 0-6 on this firmware" doctrine
/// was PROMPT-SCOPED all along, and nobody had asked the other prompt:
///
/// <list type="bullet">
///   <item><c>SSB&gt;</c> and <c>ALE&gt;</c> own <b>0-6</b>. 7, 8 and 9 each
///     answer <c>INVALID MODEM PRESET</c> there — read, write and select
///     alike (P5, P5b; transcripts
///     <c>bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl</c> and
///     <c>p5b-hop-modem-preset-write-20260821-181018.jsonl</c>).</item>
///   <item><c>HOP&gt;</c> owns <b>7-9</b>, in a SHORTER line form with no
///     <c>TYPE</c> and no <c>INTER</c> field:
///     <c>MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300</c>. 0-6 answer
///     <c>INVALID MODEM PRESET</c> there (P5).</item>
/// </list>
///
/// <para>This is what round 13's T1 probe read as "HOP refuses modem presets
/// WHOLESALE": it only ever asked for 0-6, which is the half HOP does not
/// have. protocol.md carries the correction.</para>
///
/// <para>The helper is in <c>Falcon.Core</c> because BOTH layers need the same
/// answer — the controller's builders guard on it and the app's wheel, card
/// and clone campaign range over it.</para>
/// </summary>
public static class ModemPresetScope
{
    /// <summary>The lowest and highest preset number the radio accepts at
    /// <paramref name="mode"/>'s prompt.</summary>
    public static (int First, int Last) Range(OperatingMode mode) => mode switch
    {
        OperatingMode.Hop => (HopFirst, HopLast),
        OperatingMode.Ssb or OperatingMode.Ale => (Wire.ModemPresetMin, Wire.ModemPresetMax),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>The presets <paramref name="mode"/>'s prompt owns, ascending.</summary>
    public static int[] Presets(OperatingMode mode)
    {
        var (first, last) = Range(mode);
        return [.. Enumerable.Range(first, last - first + 1)];
    }

    /// <summary><paramref name="preset"/> is one <paramref name="mode"/>'s
    /// prompt will answer for.</summary>
    public static bool Covers(OperatingMode mode, int preset)
    {
        var (first, last) = Range(mode);
        return preset >= first && preset <= last;
    }

    /// <summary>Two modes share a preset band (SSB and ALE do; HOP stands
    /// alone). What makes the presence store's scope key comparable without a
    /// second enum.</summary>
    public static bool SameScope(OperatingMode a, OperatingMode b) => Range(a) == Range(b);

    /// <summary>The <c>HOP&gt;</c> band (P5).</summary>
    public const int HopFirst = 7;
    public const int HopLast = 9;
}
