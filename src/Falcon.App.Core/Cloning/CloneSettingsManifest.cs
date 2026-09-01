using System.Globalization;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Cloning;

/// <summary>The surfaces a manifest row reads and writes through. The clone
/// campaigns replay EXISTING families — no row reaches past a surface.
/// <para><see cref="Modem"/> joined in clone round 12 with the
/// <c>ActiveModem</c> row (§3): engagement is cross-mode state and lives on its
/// own surface, the power pattern.</para></summary>
public sealed record CloneSurfaces(
    SsbSurface Ssb, PowerSurface Power, DeviceSurface Device, AleSurface Ale, ModemSurface Modem);

/// <summary>A stored value the write leg cannot turn back into a setter
/// argument. Never silent: the campaign collects it and the summary says
/// which setting was skipped and why.</summary>
public sealed class CloneValueException(string message) : Exception(message);

/// <summary>
/// A stored setting value, parsed — the SETTER'S ARGUMENT and the value's
/// CANONICAL SPELLING, which is the radio's own storage form: byte for byte
/// what the read campaign writes into the file.
///
/// <para><b>Why the spelling travels with the value</b> (clone round-12 P2,
/// audit round 3 — the third form of the door-completeness defect, and the one
/// that tripped the circuit breaker). The door proved a value was ACCEPTABLE
/// and stopped there, so noncanonical spellings of perfectly valid values
/// loaded, wrote successfully — the wire normalizes them — and then failed the
/// byte-exact VERIFY as a spurious diff: <c>CwOffset "+0000"</c> went out as
/// <c>CWOFF 0000</c> and came back "expected +0000, radio reports 0000". The
/// operator is handed a difference that is not a difference, after the wipe.
/// Parsing and canonicalizing are the SAME knowledge, so they are produced
/// together and never drift.</para>
/// </summary>
/// <param name="Value">What <see cref="CloneSettingRow.Send"/> passes on.</param>
/// <param name="Canonical">The one spelling this value may be stored as.</param>
public sealed record CloneStoredValue(object Value, string Canonical);

/// <summary>One INCLUDED manifest row: the serialized field, where it is read
/// from, how it is written back, and the order it must be written in.</summary>
/// <param name="Key">The field name — the file's setting key AND the doc
/// table's row name.</param>
/// <param name="Prompt">The prompt its read and its write require.</param>
/// <param name="ReadOp">The read that carries it.</param>
/// <param name="Setter">The builder the write uses.</param>
/// <param name="Order">1 = cascading setters (they mutate other rows),
/// 2 = the rows a cascade mutates, 3 = order-free,
/// <see cref="CloneSettingsManifest.FinalsOrder"/> = written at the FINALS,
/// not in the settings leg at all.</param>
/// <param name="Note">Recorded side-effect / evidence note, "—" when none.</param>
/// <param name="Query">Issues the read when <see cref="ReadOp"/> is not an
/// <c>SH</c> block; null when the block already carries it.</param>
/// <param name="Read">The mirror value in the file's storage form, or null
/// when the mirror is UNCONFIRMED (the file then omits the row).</param>
/// <param name="Parse">Turn the STORED form into the setter's argument AND
/// its CANONICAL spelling. <b>It touches no surface and sends nothing</b>,
/// which is what lets LOAD run it — see <see cref="Send"/>. It throws
/// <see cref="CloneValueException"/> naming the key when the value is not one
/// this radio accepts.</param>
/// <param name="Send">Send the already-parsed value.
///
/// <para><b>Why the parse and the send are SEPARATE delegates</b> (clone
/// round-12 P2 audit round 1, BLOCKER). They used to be one
/// <c>Action&lt;CloneSurfaces, string&gt;</c>, so the only way to discover
/// that a stored value was unusable was to TRY TO SEND IT — which happens in
/// leg 6, AFTER the wipe. A crafted <c>"DigitalVoice": "99"</c> therefore
/// loaded, passed the preflight, and failed on a radio that had already been
/// erased. Splitting them lets <see cref="CloneFile"/> run the REAL parser at
/// LOAD, with no radio in sight and no second copy of the rules to keep in
/// agreement: the delegate the door runs is the delegate the wire runs.</para></param>
public sealed record CloneSettingRow(
    string Key,
    string Prompt,
    string ReadOp,
    string Setter,
    int Order,
    string Note,
    Action<CloneSurfaces>? Query,
    Func<CloneSurfaces, string?> Read,
    Func<string, CloneStoredValue> Parse,
    Action<CloneSurfaces, object> Send)
{
    /// <summary>Parse and send, the write leg's one-call form.</summary>
    public void Write(CloneSurfaces surfaces, string stored) => Send(surfaces, Parse(stored).Value);
}

/// <summary>One field the manifest deliberately does NOT carry, with the
/// binding reason.</summary>
public sealed record CloneExcludedField(string Field, string Reason);

/// <summary>
/// <b>DELIVERABLE 1 of plan round 11 P6 — the CLONE SETTINGS MANIFEST</b>,
/// derived from the W1 classification (plan/phase-r-classification.md, the
/// SOURCE — this is the manifest).
///
/// <para><b>Inclusion criteria, applied mechanically</b> (plan §9A: "include
/// exactly the W1 values with BOTH a captured read shape AND a whitelisted
/// setter"). A W1 value is INCLUDED when all four hold:</para>
/// <list type="number">
/// <item>it has a MIRROR fed by a CAPTURED answer shape;</item>
/// <item>a whitelisted, non-gated, non-GUI-out setter exists;</item>
/// <item>an EXISTING read reaches it — an <c>SH</c> block or an existing
/// query builder (round 11 adds NO read builders: invariant 1 admits exactly
/// the three TXMSG ones);</item>
/// <item>the mirror value maps DETERMINISTICALLY back to a setter argument,
/// so the write can be verified rather than hoped at.</item>
/// </list>
///
/// <para><b>Binding exclusions, named</b> (§9A): <c>SetKeyline</c> (transmit
/// hazard, token-gated), the port baud and its line settings (session-ending),
/// encryption/crypto values (not reliably readable), and all read-only
/// status/telemetry. <see cref="ExcludedFields"/> names each one and every
/// other field the criteria above rejected, with its reason.</para>
///
/// <para><b>Unavailable representation, uniform:</b> a setting whose mirror is
/// UNCONFIRMED at read time is simply ABSENT from the file (serialization never
/// invents values). The write leg skips an absent row and the completion
/// summary names it — an omission is reported, never silent.</para>
///
/// <para><b>The ORDER column</b> comes from the W1-recorded setter cascades:
/// cascading setters write BEFORE the values they mutate. <c>RWAS</c> forces
/// analog, FM and digital squelch ON — <b>on ENABLE ONLY</b> (protocol.md RWAS
/// table, RE-BASED 2026-08-18: <c>RWAS DIS</c> REPORTS the three squelches and
/// forces NONE of them) — so it is order 1 and the digital squelch is order 2.
/// <c>DV</c> is order 1 on the GRADUATED D1 MATRIX (RE-GROUNDED round 13 D1,
/// protocol.md "Digital voice — the interaction matrix (D1)"): it SILENTLY
/// forces <c>MODE USB</c> from AME/CW/FM, forces the ANALOG squelch ON and
/// moves <c>BAND</c> in every case, and <c>DV OFF</c> reverses all of it. The
/// old "PRECAUTION over a disputed old-repo AGC/BAND/RFG capture" reason is
/// RETIRED — the matrix measured it. Its <c>DGT_SQUELCH</c> line is still a
/// RIDER (a report, not a mutation), so the DIGITAL squelch's order 2 is
/// <c>RWAS</c>'s doing, not DV's.
/// Everything else is order 3 and writes in table order — except
/// <c>AnalogSquelch</c>, which is <see cref="FinalsOrder"/> and is not written
/// in the settings leg at all.</para>
///
/// <para><b>MARK/SPACE and this manifest (owner ruling R3).</b> The FSK tone
/// pair is NOT a manifest setting and never can be: it belongs to the modem
/// PRESET domain, it is stored on every FSK type but LISTED only at
/// <c>fsk-v</c>, and revealing it anywhere else needs a TYPE FLIP — a
/// mutation, which R3 forbids the read campaign. So the carry rule lives in
/// the preset domain: a preset's tones ride the RAW <c>Fields</c> string
/// (the single source of truth), a row carrying no MARK/SPACE tokens is one
/// whose tones were unreadable at capture, the write summary reports that
/// omission per preset, and <b>the verify cannot detect the loss</b> because
/// the field it would compare is invisible on both sides. The flip-reveal-
/// restore technique stays on record as a FUTURE opt-in deep read.</para>
/// </summary>
public static class CloneSettingsManifest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The ORDER value meaning "not in the settings leg — written at the
    /// FINALS" (plan-clone-round12 §3 leg 6, owner ruling 2026-08-18).
    ///
    /// <para>Exactly one row carries it: <c>AnalogSquelch</c>. Core's
    /// FM-squelch compensation owes an OFF→ON cycle after any FM-property
    /// write, and that cycle fires on a LATER modulation report — so a squelch
    /// written in the settings leg would be overwritten by the compensation
    /// afterwards. The campaign therefore writes the FM group in the settings
    /// leg, and at the finals — after the final channel selection, with the
    /// radio in SSB context — requests ONE modulation read to FIRE any pending
    /// cycle, then writes this row once the cycle has completed. The operating
    /// MODE is still set after it.</para>
    /// </summary>
    public const int FinalsOrder = 4;

    /// <summary>PROVISIONAL marker for the rows whose SET form has never been
    /// sent to this radio (W1 "wire-query" evidence tier) — the read shape is
    /// captured, the write is old-app-derived. The first live clone (§14)
    /// exercises them.</summary>
    private const string SetNeverSent = "PROVISIONAL: the SET form has never been sent (W1 wire-query tier).";

    public static IReadOnlyList<CloneSettingRow> Rows { get; } =
    [
        // ---- Order 1: the cascading setters --------------------------------
        new("Rwas", "SSB>", "SH", "Ssb.SetRwas", 1,
            "ORDER CORRECTED clone round 12 (§3 leg 6, critic F17): **ENABLING forces analog, FM and "
            + "digital squelch ON. DISABLING forces NOTHING** — the 2026-08-18 bench session sent "
            + "`RWAS DIS` with analog and digital squelch OFF and they stayed OFF, disproving the "
            + "both-ways form this note carried. BOTH directions still REPORT the squelch lines, so "
            + "no re-poll is needed either way. It stays order 1 because the ENABLE path really does "
            + "cascade into the squelch rows, and a manifest that ordered on the disproved half would "
            + "be right only by accident.",
            null,
            s => Token(s.Ssb.Rwas),
            v => ParseEnum<EnabledDisabled>(v, "Rwas"),
            (s, v) => s.Ssb.SetRwas((EnabledDisabled)v)),
        new("DigitalVoice", "SSB>", "SH", "Ssb.SetDigitalVoice", 1,
            "RE-GROUNDED round 13 (backlog item 1, phase D1) on the GRADUATED D1 MATRIX "
            + "(protocol.md, \"Digital voice — the interaction matrix (D1)\"). The old wording called "
            + "order 1 a PRECAUTION resting on two captures that DISAGREED about an AGC/BAND/RFG "
            + "cascade; the D1 bench matrix replaced that dispute with measurement, so that reason is "
            + "RETIRED and the order is now a MEASURED FACT, not a hedge. What the matrix pins: `DV "
            + "ON` SILENTLY FORCES `MODE "
            + "USB` from AME, CW and FM — the echo carries no MODE line at all — and it forces the "
            + "ANALOG squelch ON and moves BAND in EVERY case, USB and LSB included; `DV OFF` reverses "
            + "every one of them. So DV really is a cascading setter and writes FIRST. The values it "
            + "moves are then re-imposed by the rows that own them: MODE, BAND and AGC are "
            + "CHANNEL-SCOPED (the SSB channel domain replays the stored DI record) and AnalogSquelch "
            + "is written at the FINALS. The mirror stays honest for the verify leg through the P4 "
            + "sync layer — a DV line reporting a value it had not last reported marks MODE, BAND and "
            + "analog squelch unconfirmed, and a changed MODE line marks DV unconfirmed — plus the "
            + "DV/MODE ArmCompressionRepoll compensator, which re-reads `COM` for the Compression row.",
            null,
            s => Token(s.Ssb.DigitalVoice),
            v => ParseEnum<OnOff>(v, "DigitalVoice"),
            (s, v) => s.Ssb.SetDigitalVoice((OnOff)v)),

        // ---- Order 2: what those cascades mutate ---------------------------
        new("DigitalSquelch", "SSB>", "SH", "Ssb.SetDigitalSquelch", 2,
            "Forced ON by `RWAS ENA` — written after it. (CORRECTED clone round 12: the "
            + "DISABLE direction REPORTS this row and forces NOTHING, so the both-ways wording this "
            + "note carried was disproved at the 2026-08-18 bench.) RE-GROUNDED round 13 (backlog "
            + "item 1, phase D1) on the GRADUATED D1 MATRIX (protocol.md, \"Digital voice — the "
            + "interaction matrix (D1)\"): DV is now a MEASURED cascading setter — it silently forces "
            + "`MODE USB` from AME/CW/FM and moves BAND every time — but the DIGITAL squelch is not "
            + "among the values it moves. `DGT_SQUELCH ON` is a RIDER on the DV echo — a REPORT, not a "
            + "mutation; the matrix's "
            + "\"what actually moved\" column carries MODE, BAND and the ANALOG squelch, never this "
            + "row, and `DV OFF` restores the analog one exactly. So DV's order-1 position is not what "
            + "puts this row at 2 — `RWAS ENA` is.",
            null,
            s => Token(s.Ssb.DigitalSquelch),
            v => ParseEnum<OnOff>(v, "DigitalSquelch"),
            (s, v) => s.Ssb.SetDigitalSquelch((OnOff)v)),

        // ---- Order 3: order-free, SSB prompt -------------------------------
        new("PowerLevel", "SSB>", "SH", "SetPowerLevel", 3, "—",
            null,
            s => Token(s.Power.Level),
            v => ParseEnum<PowerLevel>(v, "PowerLevel"),
            (s, v) => s.Power.Set((PowerLevel)v)),
        new("BfoOffset", "SSB>", "SH", "Ssb.SetBfoOffset", 3, SetNeverSent,
            null,
            s => Text(s.Ssb.BfoOffset),
            v => ParseSignedFourDigit(v, "BfoOffset", Wire.BfoOffsetMinHz, Wire.BfoOffsetMaxHz),
            (s, v) => s.Ssb.SetBfoOffset((int)v)),
        new("CwOffset", "SSB>", "SH", "Ssb.SetCwOffset", 3, SetNeverSent,
            null,
            s => Text(s.Ssb.CwOffset),
            v => ParseFourDigit(v, "CwOffset", Wire.CwOffsetValuesHz),
            (s, v) => s.Ssb.SetCwOffset((int)v)),
        new("Antenna", "SSB>", "SH", "Ssb.SetAntenna", 3, SetNeverSent,
            null,
            s => Text(s.Ssb.Antenna),
            ParseAntenna,
            (s, v) => s.Ssb.SetAntenna((AntennaPort)v)),
        new("UnkeyMask", "SSB>", "UNKEY_M", "Ssb.SetUnkeyMask", 3, "—",
            s => s.Ssb.RequestUnkeyMask(),
            s => Token(s.Ssb.UnkeyMask),
            v => ParseEnum<EnabledDisabled>(v, "UnkeyMask"),
            (s, v) => s.Ssb.SetUnkeyMask((EnabledDisabled)v)),
        new("FrequencyStep", "SSB>", "STEP", "Ssb.SetStep", 3, "—",
            s => s.Ssb.RequestStep(),
            s => Token(s.Ssb.Step),
            v => ParseEnum<FrequencyStep>(v, "FrequencyStep"),
            (s, v) => s.Ssb.SetStep((FrequencyStep)v)),
        new("RfGain", "SSB>", "RF", "Ssb.SetRfGain", 3, SetNeverSent,
            s => s.Ssb.RequestRfGain(),
            s => Number(s.Ssb.RfGain),
            v => ParseBoundedInt(v, "RfGain", Wire.RfGainMin, Wire.RfGainMax),
            (s, v) => s.Ssb.SetRfGain((int)v)),
        new("Beep", "SSB>", "BEEP", "Ssb.SetBeep", 3, "—",
            s => s.Ssb.RequestBeep(),
            s => Token(s.Ssb.Beep),
            v => ParseEnum<OnOff>(v, "Beep"),
            (s, v) => s.Ssb.SetBeep((OnOff)v)),
        new("FmSquelchType", "SSB>", "FMSQ_T", "Ssb.SetFmSquelchType", 3, SetNeverSent,
            s => s.Ssb.RequestFmSquelchType(),
            s => Text(s.Ssb.FmSquelchType),
            ParseFmSquelchType,
            (s, v) => s.Ssb.SetFmSquelchType((FmSquelchType)v)),
        new("FmTone", "SSB>", "FMTONE", "Ssb.SetFmTone", 3, SetNeverSent,
            s => s.Ssb.RequestFmTone(),
            s => Token(s.Ssb.FmTone),
            v => ParseEnum<OnOff>(v, "FmTone"),
            (s, v) => s.Ssb.SetFmTone((OnOff)v)),
        new("FmDeviation", "SSB>", "FMDE", "Ssb.SetFmDeviation", 3, SetNeverSent,
            s => s.Ssb.RequestFmDeviation(),
            s => Text(s.Ssb.FmDeviation),
            v => ParseOneOfText(v, "FmDeviation", Wire.FmDeviationValues),
            (s, v) => s.Ssb.SetFmDeviation((string)v)),
        new("PrePostFilter", "SSB>", "PREPOST FILTER", "Ssb.SetPrePostFilter", 3, SetNeverSent,
            s => s.Ssb.RequestPrePostFilter(),
            s => Text(s.Ssb.PrePostFilter),
            v => ParseEnableDisable(v, "PrePostFilter"),
            (s, v) => s.Ssb.SetPrePostFilter((OnOff)v)),
        new("PrePostRxAntenna", "SSB>", "PREPOST RXANTENNA", "Ssb.SetPrePostRxAntenna", 3, SetNeverSent,
            s => s.Ssb.RequestPrePostRxAntenna(),
            s => Text(s.Ssb.PrePostRxAntenna),
            v => ParseEnableDisable(v, "PrePostRxAntenna"),
            (s, v) => s.Ssb.SetPrePostRxAntenna((OnOff)v)),
        new("PrePostScanRate", "SSB>", "PREPOST SCAN", "Ssb.SetPrePostScanRate", 3, SetNeverSent,
            s => s.Ssb.RequestPrePostScanRate(),
            s => Text(s.Ssb.PrePostScanRate),
            ParsePrePostScanRate,
            (s, v) => s.Ssb.SetPrePostScanRate((PrePostScanRate)v)),
        new("Contrast", "SSB>", "CONT", "SetContrast", 3, "—",
            s => s.Device.RequestContrast(),
            s => Number(s.Device.Contrast),
            v => ParseBoundedInt(v, "Contrast", Wire.ZeroToEightMin, Wire.ZeroToEightMax),
            (s, v) => s.Device.SetContrast((int)v)),
        new("Compression", "SSB>", "COM", "Ssb.SetCompression", 3,
            "INCLUDED round 13 (backlog item 2, phase D1) — the round-12 §9 B3 deferral's UNLOCK "
            + "CONDITION IS MET. It was held out \"pending the D1 DV-matrix graduation\", because a "
            + "verify leg cannot trust a value the MODE/DV cascade might have moved underneath it. "
            + "That matrix is now GRADUATED (protocol.md, \"Digital voice — the interaction matrix "
            + "(D1)\"), and all four inclusion criteria hold: the mirror is fed by a captured "
            + "`COMPRESS` answer shape; the setter is whitelisted and field-proven from the SSB pane "
            + "(`COM <ON|OFF>`); an EXISTING read reaches it (`QueryCompression`, the round-12 P1 "
            + "builder over the 2026-08-18 bare-`COM` capture — `COM  ->  COMPRESS ON`); and OnOff "
            + "round-trips deterministically. ORDER 3, and the position matters: it is written AFTER "
            + "the DV row (order 1), so the DV cascade can never post-date this write. Core's DV/MODE "
            + "ArmCompressionRepoll compensator re-reads `COM` whenever either moves, which is what "
            + "keeps the mirror honest for the verify leg.",
            s => s.Ssb.RequestCompression(),
            s => Token(s.Ssb.Compression),
            v => ParseEnum<OnOff>(v, "Compression"),
            (s, v) => s.Ssb.SetCompression((OnOff)v)),
        new("ActiveModem", "SSB>", "SH", "Modem.Select / Modem.Off", 3,
            "REJOINED clone round 12 (§1, §3 critic F10). It was excluded as a CASCADE CONFLICT: "
            + "engaging a modem silently mutates AGC and bandwidth (probe R8), and the SSB-channel "
            + "domain owns those. The 2026-08-18 bench session settled the open half of that probe — "
            + "`DI 00 00` is BYTE-IDENTICAL across `MODEM 1` and `MODEM OF`, so engagement moves LIVE "
            + "state and leaves the STORED record alone. The channel domain replays stored records "
            + "and the finals leg re-selects the operating channel afterwards, so there is no "
            + "conflict left to order around. DOCUMENTED-FORM-ONLY (audit round 3): the file's value "
            + "is a MIRROR STRING, not a command argument, so the door admits only what the radio "
            + "itself reports. The engage builder would take a bare NAME ('T39') — this row REFUSES "
            + "it, because a name-only file would write correctly and then read back as '1 T39', "
            + "which is a diff that is not a difference. The file stores the RAW MIRROR STRING (\"OFF\", or an "
            + "engagement echo like \"1 T39\"); the write parses the LEADING DIGITS as the selector "
            + "and sends the existing engage builder; the verify compares the re-read mirror string. "
            + "PROMPT SPLIT (clone-field round 2 F9): this row is read and written at `SSB>`, so its "
            + "selector is one of the 0-6 presets that prompt owns. The radio keeps a SEPARATE modem "
            + "engagement at `HOP>` over presets 7-9 (P5/P5d, transcripts "
            + "p5-hop-modem-presets-20260821-180547.jsonl and "
            + "p5d-hop-modem-select-20260821-183052.jsonl) — the PRESETS 7-9 are cloned (their own "
            + "read and write legs at `HOP>`), the HOP ENGAGEMENT is not: no row here reads it.",
            null,
            s => Text(s.Modem.ActiveModem),
            ParseActiveModem,
            (s, v) => SendActiveModem(s, (string)v)),
        new("AnalogSquelch", "SSB>", "SH", "Ssb.SetSquelch", FinalsOrder,
            "REJOINED clone round 12 (owner ruling R4) and written at the FINALS, not here — see "
            + "CloneSettingsManifest.FinalsOrder. Core's FM-squelch compensation (a deliberate, "
            + "documented autonomous `SQ OFF`/`SQ ON` after an FM-property change: the analog squelch "
            + "is audibly broken until it is cycled) is NOT touched (R4), and this manifest writes "
            + "three FM properties, so the cycle can always be owed. GREEN PATH: at the finals, after "
            + "the final channel selection, ONE modulation read fires any pending cycle; once the "
            + "cycle has completed the row is written and verified. RED PATH: the cycle does not "
            + "complete inside the settle bound, and the row is SKIPPED and NAMED in the summary — "
            + "never written into a cycle that would overwrite it.",
            null,
            s => Token(s.Ssb.AnalogSquelch),
            v => ParseEnum<OnOff>(v, "AnalogSquelch"),
            (s, v) => s.Ssb.SetSquelch((OnOff)v)),

        // ---- Order 3, ALE prompt (the nine the ALE SH block reports) -------
        new("AleAllCall", "ALE>", "SH", "Ale.SetAllCall", 3, "—",
            null, s => Token(s.Ale.AllCall),
            v => ParseEnum<OnOff>(v, "AleAllCall"),
            (s, v) => s.Ale.SetAllCall((OnOff)v)),
        new("AleAnyCall", "ALE>", "SH", "Ale.SetAnyCall", 3, "—",
            null, s => Token(s.Ale.AnyCall),
            v => ParseEnum<OnOff>(v, "AleAnyCall"),
            (s, v) => s.Ale.SetAnyCall((OnOff)v)),
        new("AleAmdDisplay", "ALE>", "SH", "Ale.SetAmdDisplay", 3, "—",
            null, s => Token(s.Ale.AmdDisplay),
            v => ParseEnum<OnOff>(v, "AleAmdDisplay"),
            (s, v) => s.Ale.SetAmdDisplay((OnOff)v)),
        new("AleKeyToCall", "ALE>", "SH", "Ale.SetKeyToCall", 3, "—",
            null, s => Token(s.Ale.KeyToCall),
            v => ParseEnum<OnOff>(v, "AleKeyToCall"),
            (s, v) => s.Ale.SetKeyToCall((OnOff)v)),
        new("AleListenBeforeTx", "ALE>", "SH", "Ale.SetListenBeforeTx", 3, "—",
            null, s => Token(s.Ale.ListenBeforeTx),
            v => ParseEnum<OnOff>(v, "AleListenBeforeTx"),
            (s, v) => s.Ale.SetListenBeforeTx((OnOff)v)),
        new("AleRadioSilence", "ALE>", "SH", "Ale.SetRadioSilence", 3, "—",
            null, s => Token(s.Ale.RadioSilence),
            v => ParseEnum<OnOff>(v, "AleRadioSilence"),
            (s, v) => s.Ale.SetRadioSilence((OnOff)v)),
        new("AleMaxScanChannels", "ALE>", "SH", "Ale.SetMaxScanChannels", 3, "—",
            null, s => Number(s.Ale.MaxScanChannels),
            v => ParseBoundedInt(v, "AleMaxScanChannels", Wire.MaxScanChannelsMin, Wire.MaxScanChannelsMax),
            (s, v) => s.Ale.SetMaxScanChannels((int)v)),
        new("AleLinkTimeout", "ALE>", "SH", "Ale.SetLinkTimeout", 3, "—",
            null, s => Number(s.Ale.LinkTimeoutMinutes),
            v => ParseBoundedInt(v, "AleLinkTimeout", Wire.LinkTimeoutMinMinutes, Wire.LinkTimeoutMaxMinutes),
            (s, v) => s.Ale.SetLinkTimeout((int)v)),
        new("AleTuneTime", "ALE>", "SH", "Ale.SetTuneTime", 3, "—",
            null, s => Number(s.Ale.TuneTimeSeconds),
            v => ParseBoundedInt(v, "AleTuneTime", Wire.TuneTimeMinSeconds, Wire.TuneTimeMaxSeconds),
            (s, v) => s.Ale.SetTuneTime((int)v)),
    ];

    /// <summary>Every W1 field the manifest does NOT carry, with the binding
    /// reason. Named rather than silently missing — an unexplained omission is
    /// exactly the kind of hole a clone hides until the bench finds it.</summary>
    public static IReadOnlyList<CloneExcludedField> ExcludedFields { get; } =
    [
        new("Keyline", "Hazard — K ON TRANSMITS and the radio stays keyed; the setter is token-gated (binding exclusion §9A)."),
        new("SelfTest / VswrTest", "Hazard — both TRANSMIT; token-gated and GUI-out (E5)."),
        new("PortBaud", "Session — changing the remote-port baud ends the session; the guarded wizard owns it (binding exclusion §9A)."),
        new("PortBits / PortParity / PortStopBits / PortXonXoff", "Session — remote-port line settings, W1 OUT OF SCOPE; no setter exists."),
        new("PortRemoteEcho", "Session — the connect ritual sets it; a clone rewriting it would break the framing contract mid-campaign."),
        new("Encryption / EncryptionAvailability", "Crypto — `ENCRYPTION NOT INSTALLED` on this radio, so it is not reliably readable (binding exclusion §9A); GUI-out (E1)."),
        new("CurrentEncryptionKey", "Crypto — read-only status, and the key slots themselves are write-only."),
        new("EncryptionKeySlots (ENC_KEY / USE_KEY)", "Crypto — write-only, no read-back exists (binding exclusion §9A)."),
        new("ForceWakeup", "Write-only — the DISABLED state has no read-back at all, so a clone could only latch a stale ENABLED."),
        new("RwasKey", "Write-only — a bare query answers `** ERROR **`; there is nothing to read."),
        new("SquelchLevel", "Not round-trippable — only the HIGH read-back spelling is captured, so a LO/MEDIUM source radio could not be written or verified."),
        new("Retransmit", "Not round-trippable — only the DISABLED read-back spelling is captured (W1)."),
        new("Avs", "Not round-trippable — the mirror conflates the value with the availability marker (`AVS NOT INSTALLED`, captured here), so a read cannot be classified as a settable state."),
        new("FmSquelch", "No read builder — the bare FMSQ query is captured but UNBUILT. (It is no longer \"same as Compression\": round-12 P1 gave compression a read builder, and round 13 D1 CLONED compression once the D1 matrix graduated — this row still has no read at all, so it did not follow.)"),
        new("RxPreamp", "Uncaptured — no captured answer shape (W1 bench item); the parser keeps PREAMP as noise."),
        new("InternalCoupler", "Uncaptured — no captured answer shape (W1 bench item)."),
        new("OneKilowattPa", "Uncaptured — no captured answer shape (W1 bench item)."),
        new("BacklightFunction", "Uncaptured — the LIGHT answer payload was never archived (W1 bench item); the mirror is old-app-derived and PROVISIONAL."),
        new("BacklightIntensity", "Uncaptured — the INTENSITY answer payload was never archived (W1 bench item)."),
        new("RadioTimeOfDay", "Clock — a live device clock, not configuration; replaying a captured timestamp would set the target radio to a stale time."),
        new("RxFrequency / TxFrequency", "Channel-scoped — carried VERBATIM by the SSB-channel domain (R10); a second writer would re-program the selected channel from a second source of truth."),
        new("ModulationMode / Bandwidth / AgcSpeed / ChannelRxOnly", "Channel-scoped — same reason: the DI record carries all four and the channel leg writes them."),
    ];

    /// <summary>
    /// EVERY W1 row, dispositioned. The key is the row's first cell in
    /// plan/phase-r-classification.md, VERBATIM; the value either points at the
    /// manifest field(s) it became ("→ Field, Field") or states why it carries
    /// none. The completeness pin walks the W1 tables and requires a key here
    /// for each row — a W1 row nobody thought about fails the suite.
    /// </summary>
    public static IReadOnlyDictionary<string, string> W1Dispositions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- A. SSB HELP -------------------------------------------------
            ["`AGc`"] = "Channel-scoped — the SSB channel domain carries AGC.",
            ["`ALE`"] = "Mode switch — the operating mode is a FINAL (write leg 11).",
            ["`BAndwidth`"] = "Channel-scoped — the SSB channel domain carries the bandwidth.",
            ["`BFo`"] = "→ BfoOffset",
            ["`CHan`"] = "Operating — the file's channel is a FINAL (write leg 11).",
            ["`DGT_sq`"] = "→ DigitalSquelch",
            ["`DIsplay`"] = "Read — it IS the SSB channel domain's read (DI 0 99).",
            ["`DV`"] = "→ DigitalVoice",
            ["`ENCRypt`"] = "Excluded — crypto (see Encryption).",
            ["`ENC_KEY`"] = "Excluded — crypto, write-only (see EncryptionKeySlots).",
            ["`FReq`"] = "Channel-scoped — the SSB channel domain carries RX/TX frequency.",
            ["`HOp`"] = "Mode switch — the operating mode is a FINAL (write leg 11).",
            ["`Keyline`"] = "Excluded — transmit hazard (see Keyline).",
            ["`MODE`"] = "Channel-scoped — the SSB channel domain carries the modulation.",
            ["`MODEM <n\\|name>`"] = "→ ActiveModem",
            ["`POWer`"] = "→ PowerLevel",
            ["`PORT_Re`"] = "Excluded — session-ending port settings (see PortBaud).",
            ["`PORT_Da`"] = "Excluded — W1 OUT OF SCOPE; no builder, no plan item.",
            ["`RWAS`"] = "→ Rwas",
            ["`RXFreq`"] = "Channel-scoped — the SSB channel domain carries RX frequency.",
            ["`SHow`"] = "Read — SH is the read op several manifest rows use.",
            ["`SQuelch`"] = "→ AnalogSquelch",
            ["`SQ_Level`"] = "Excluded — not round-trippable (see SquelchLevel).",
            ["`TEst`"] = "Excluded — transmit hazard, GUI-out (see SelfTest / VswrTest).",
            ["`TEst 3`"] = "Status — a firmware-version dump; nothing to write.",
            ["`TEst 4`"] = "Excluded — transmit hazard, GUI-out (see SelfTest / VswrTest).",
            ["`TIme`"] = "Excluded — device clock (see RadioTimeOfDay).",
            ["`TXFreq`"] = "Channel-scoped — the SSB channel domain carries TX frequency.",
            ["`USE_KEy`"] = "Excluded — crypto, write-only (see EncryptionKeySlots).",
            ["`ZERO`"] = "Campaign — SUPERSEDED by owner ruling R1 (clone round 12): the write campaign's FIRST wire act after the ONE confirm is `ZERO`, so the round-11 \"no builder exists and none may\" is retired. The X13 builder is whitelist-narrowed to the clone campaign alone; the GUI can never reach it.",

            // ---- B. SSB HELP MORE -------------------------------------------
            ["`ANTENNA`"] = "→ Antenna",
            ["`AVS`"] = "Excluded — not round-trippable (see Avs).",
            ["`BATtery`"] = "Status — battery telemetry; no setter can exist.",
            ["`COMpression`"] = "→ Compression",
            ["`CONTrast`"] = "→ Contrast",
            ["`CWOFFset`"] = "→ CwOffset",
            ["`DATe` / `DAY`"] = "Excluded — device clock (see RadioTimeOfDay).",
            ["`INTCoup`"] = "Excluded — uncaptured answer shape (see InternalCoupler).",
            ["`INTensity`"] = "Excluded — uncaptured answer payload (see BacklightIntensity).",
            ["`FMDEviation`"] = "→ FmDeviation",
            ["`FMTone`"] = "→ FmTone",
            ["`FMSQuelch`"] = "Excluded — no read builder (see FmSquelch).",
            ["`FMSQ_Type`"] = "→ FmSquelchType",
            ["`KWATt`"] = "Excluded — uncaptured answer shape (see OneKilowattPa).",
            ["`PASSWord`"] = "Excluded — W1 OUT OF SCOPE (lockout risk).",
            ["`PREamp`"] = "Excluded — uncaptured answer shape (see RxPreamp).",
            ["`PREPost`"] = "→ PrePostFilter, PrePostRxAntenna, PrePostScanRate",
            ["`PROGram` / `SELect`"] = "Domain — the LOCKOUTS domain (owner ruling R2, clone round 12): no longer out of scope. Read as ONE sentinel-bracketed op (both bare reports are global from one prompt), written per section at that section's prompt in write leg 10, and verified keyed-exact.",
            ["`RETRansmit`"] = "Excluded — not round-trippable (see Retransmit).",
            ["`RETUne`"] = "Operation — a tune command, not a stored setting.",
            ["`RFgain`"] = "→ RfGain",
            ["`RXONly`"] = "Channel-scoped — the DI record carries RXONLY.",

            // ---- C. SSB HELP RWAS -------------------------------------------
            ["`FORCE_Wakeup`"] = "Excluded — write-only (see ForceWakeup).",
            ["`RWAS_KEY`"] = "Excluded — write-only (see RwasKey).",
            ["`UNKEY_Mask`"] = "→ UnkeyMask",

            // ---- D. ALE HELP PROG -------------------------------------------
            ["`ADDCh`"] = "Domain — the channel-groups leg (write leg 8).",
            ["`DELCh`"] = "Domain — DELETED clone round 12: after `ZERO` every channel group is empty, so the groups leg is PURE `ADDC` writes with nothing to reconcile away (owner statement §1).",
            ["`CHGroup <g> <ch…>` (whole-list set)"] = "No builder — W1 named skip; the reconcile uses ADDC/DELC instead.",
            ["`ADDMemb`"] = "Domain — the ALE book leg's membership rows (write leg 7).",
            ["`ALL_Call`"] = "→ AleAllCall",
            ["`ANY_Call`"] = "→ AleAnyCall",
            ["`AMD_Display`"] = "→ AleAmdDisplay",
            ["`DELADdr`"] = "Domain — not needed by the clone: `ZERO` (write leg 2) clears the book wholesale.",
            ["`ERASE`"] = "Domain — DELETED clone round 12: `ZERO` (write leg 2) subsumes it, per the owner statement that zeroize clears everything except the remote port baud rate. The standalone ALE-erase card keeps its own confirm and its own leg; the CLONE no longer sends `ERASE` at all.",
            ["`INDADdr`"] = "Domain — the ALE book leg (write leg 7).",
            ["`KEY_To_call`"] = "→ AleKeyToCall",
            ["`LSTNbeforetx`"] = "→ AleListenBeforeTx",
            ["`MAXCH`"] = "→ AleMaxScanChannels",
            ["`NETADdr`"] = "Domain — the ALE book leg (write leg 7); its targeted form is the membership READ.",
            ["`RXMsg DEL`"] = "Excluded — W1 OUT OF SCOPE; the received-message store is Stage-9 gated and its payload format is unverified.",
            ["`RAD_silence`"] = "→ AleRadioSilence",
            ["`SLFADdr`"] = "Domain — the ALE book leg (write leg 7); the R9 swap decides the order.",
            ["`TIME_OUt`"] = "→ AleLinkTimeout",
            ["`TUNEtime`"] = "→ AleTuneTime",
            ["`TXMsg`"] = "Domain — the stored-message leg (write leg 5), at the `ALE>` prompt (CORRECTED clone round 12: the family is ALE-only and answers `** ERROR **` at SSB> and HOP>). Store-only via the X10 builders — the per-slot DELETE is DELETED, because `ZERO` leaves every slot empty (owner statement §1).",

            // ---- E. ALE HELP OPER -------------------------------------------
            ["`SCAn` / `STop`"] = "Operation — scan control, not stored state.",
            ["`CALl` / `SEnd`"] = "Operation — transmits; never part of a clone.",
            ["`INC` / `DEC` (scan list)"] = "Operation — W1 named skip; no builder.",
            ["`EOWDest`"] = "Operation — W1 named skip; no builder.",
            ["`RANk`"] = "Status — a passive LQA score read.",
            ["`EXCHange` / `SOUnd`"] = "Domain — the LQA schedule leg (write leg 7).",
            ["`SSB`"] = "Mode switch — the operating mode is a FINAL (write leg 11).",

            // ---- F. HOP programming ------------------------------------------
            ["`NETID`"] = "Domain — the HOP nets leg (write leg 9).",
            ["`HOPTYPE`"] = "Domain — the HOP nets leg (write leg 9).",
            ["`HOPSET` NB / WB"] = "Domain — the HOP nets leg (write leg 9).",
            ["`HOPSET <n> DEL`"] = "Domain — DELETED clone round 12: the CLEAR-FIRST replay existed to make the leg idempotent over an unknown target. After `ZERO` every net is already wiped, so the leg is PURE writes (owner statement §1).",
            ["`HOPLIST ADD/DEL/query`"] = "Domain — the HOP nets leg's LIST frequencies (write leg 9).",
            ["`EXCLUDE` set/query/DEL"] = "Domain — the exclusion bands (write leg 9). The RECONCILE is DELETED clone round 12: after `ZERO` the table is empty, so the leg is PURE sets and never a DEL (owner statement §1).",
            ["`DOIT`"] = "Excluded — GUI-out; regeneration must never happen behind the operator's back, and the leg's own writes regenerate anyway.",
            ["`NET`"] = "Operating — the file's HOP net is a FINAL (write leg 11).",
            ["`TIME`/`DAT`/`DAY`"] = "Excluded — device clock (see RadioTimeOfDay).",
            ["`SYNC`"] = "Operation — transmits a sync request.",
            ["`ENC_KEY` / `USE_KEY`"] = "Excluded — crypto, write-only (see EncryptionKeySlots).",
            ["`DIS`"] = "Read — it IS the HOP nets domain's read.",

            // ---- G. Device / diagnostics / COMSEC ----------------------------
            ["`LIG`"] = "Excluded — uncaptured answer payload (see BacklightFunction).",
            ["`INT`"] = "Excluded — uncaptured answer payload (see BacklightIntensity).",
            ["`CONT`"] = "→ Contrast",
            ["`BEEP`"] = "→ Beep",
            ["`ENCR_ST`"] = "Excluded — W1 named skip; the answer format is unconfirmed and no builder exists.",
            ["`FG_KEY`"] = "Excluded — front panel only; no builder.",
            ["`LEVEL`"] = "Excluded — W1 OUT OF SCOPE (session-ending).",

            // ---- H. Mirrored values -----------------------------------------
            ["RadioState.OperatingMode"] = "Operating — captured first, written LAST (write leg 11).",
            [".PowerLevel"] = "→ PowerLevel",
            [".PowerCutback"] = "Status — PA thermal chatter; no setter can exist.",
            [".Keyline"] = "Excluded — transmit hazard (see Keyline).",
            [".BatteryStatus"] = "Status — telemetry.",
            [".IsTuning/TuneComplete/Marginal/Fail"] = "Status — tune lifecycle flags.",
            [".RxFrequency / .TxFrequency"] = "Channel-scoped (see RxFrequency / TxFrequency).",
            [".OperatingChannel"] = "Operating — the file's channel is a FINAL (write leg 11).",
            [".ModulationMode / .Bandwidth / .AgcSpeed"] = "Channel-scoped (see ModulationMode / Bandwidth / AgcSpeed / ChannelRxOnly).",
            [".ChannelRxOnly"] = "Channel-scoped (see ModulationMode / Bandwidth / AgcSpeed / ChannelRxOnly).",
            [".FrequencyStep"] = "→ FrequencyStep",
            [".AnalogSquelch"] = "→ AnalogSquelch",
            [".ActiveModem"] = "→ ActiveModem",
            [".RadioTimeOfDay"] = "Excluded — device clock (see RadioTimeOfDay).",
            [".ChannelList"] = "Domain — the SSB channel domain (write leg 3, all reported slots verbatim).",
            [".PortRemoteEcho"] = "Excluded — session-critical port setting (see PortRemoteEcho).",
            [".PortBaud"] = "Excluded — session-ending (see PortBaud).",
            [".PortBits/.PortParity/.PortStopBits/.PortXonXoff"] = "Excluded — session-ending, W1 OUT OF SCOPE (see PortBits / PortParity / PortStopBits / PortXonXoff).",
            ["NEW .DigitalVoice / .DigitalSquelch"] = "→ DigitalVoice, DigitalSquelch",
            ["NEW .SquelchLevel (verbatim)"] = "Excluded — not round-trippable (see SquelchLevel).",
            ["NEW .FmSquelch / .FmTone (OnOff), .FmSquelchType / .FmDeviation (verbatim)"] =
                "→ FmTone, FmSquelchType, FmDeviation (FmSquelch is excluded — no read builder).",
            ["NEW .BfoOffset / .CwOffset (verbatim)"] = "→ BfoOffset, CwOffset",
            // STALE ENTRY CORRECTED round 13 D1: it still read "no read builder"
            // long after round-12 P1 built QueryCompression, contradicting both
            // the exclusion note and the `COMpression` disposition above. Now it
            // agrees with them — and with the row.
            ["NEW .Compression (OnOff)"] = "→ Compression",
            ["NEW .Antenna (verbatim)"] = "→ Antenna",
            ["NEW .Retransmit (verbatim)"] = "Excluded — not round-trippable (see Retransmit).",
            ["NEW .Rwas / .UnkeyMask (EnabledDisabled)"] = "→ Rwas, UnkeyMask",
            ["NEW .Avs (verbatim)"] = "Excluded — not round-trippable (see Avs).",
            ["NEW .Encryption (OnOff) + .EncryptionAvailability (verbatim)"] = "Excluded — crypto (see Encryption / EncryptionAvailability).",
            ["NEW .CurrentEncryptionKey (verbatim)"] = "Excluded — crypto status (see CurrentEncryptionKey).",
            ["NEW .RfGain (int)"] = "→ RfGain",
            ["NEW .Contrast (int)"] = "→ Contrast",
            ["NEW .Beep (OnOff)"] = "→ Beep",
            ["NEW .PrePostFilter/.PrePostRxAntenna/.PrePostScanRate (verbatim)"] =
                "→ PrePostFilter, PrePostRxAntenna, PrePostScanRate",
            ["(no mirror) FORCE_W, RWAS_KEY, ENC_KEY slots, LQA schedules, net membership"] =
                "Split: FORCE_W / RWAS_KEY / ENC_KEY are excluded write-only values; LQA schedules and net membership are DOMAINS the clone reads through the round-11 §8 mirrors (write leg 7).",
            ["(no mirror) LIGHT, INTENSITY, PREAMP, INTCOUPLER, KWATT"] =
                "Excluded — uncaptured answer shapes (see BacklightFunction, BacklightIntensity, RxPreamp, InternalCoupler, OneKilowattPa).",
            ["AleState.LinkState/.FillState/.LinkedStation/.LinkedChannel"] = "Status — link lifecycle.",
            [".SelfAddresses/.IndividualAddresses/.NetAddresses"] = "Domain — the ALE book (write leg 7).",
            [".TxMessages"] = "Domain — the stored-message leg (write leg 5).",
            [".LqaReport"] = "Status — RANK is a passive read.",
            ["NEW AleState .AllCall/.AnyCall/.AmdDisplay/.KeyToCall/.ListenBeforeTx/.RadioSilence (OnOff), .MaxScanChannels/.LinkTimeoutMinutes/.TuneTimeSeconds (int)"] =
                "→ AleAllCall, AleAnyCall, AleAmdDisplay, AleKeyToCall, AleListenBeforeTx, AleRadioSilence, AleMaxScanChannels, AleLinkTimeout, AleTuneTime",
            ["HopState.CurrentNet"] = "Operating — the file's HOP net is a FINAL (write leg 11).",
            [".Nets/.HopLists"] = "Domain — the HOP nets leg (write leg 9).",
            [".HopNum/.SyncState/.IsGeneratingHopset/.IsHopListInvalid/.NoHopsetCount"] = "Status — generation and sync lifecycle.",
        };

    public static bool IsIncludedKey(string key) => Rows.Any(r => r.Key == key);

    /// <summary>
    /// Prove a stored setting value AT LOAD, without a radio — the seam
    /// <see cref="CloneFile"/> calls for every setting in the file.
    ///
    /// <para>THREE questions, and the third is the one that tripped the
    /// breaker (audit round 3). (1) Is it a value this radio ACCEPTS? — the
    /// row's own parser answers, and it is the same parser the write leg runs.
    /// (2) Is it inside the bounds the BUILDER enforces? — the parser reads
    /// those from <see cref="Wire"/>, where the builder reads them too.
    /// (3) <b>Is it spelled the way the radio STORES it?</b></para>
    ///
    /// <para><b>Why (3) is not pedantry.</b> A noncanonical spelling of a
    /// perfectly valid value passes (1) and (2), WRITES successfully — the
    /// wire normalizes it — and then fails the byte-exact VERIFY as a
    /// difference that is not a difference: <c>"+0000"</c> goes out as
    /// <c>CWOFF 0000</c> and reads back <c>0000</c>. The operator is shown a
    /// diff, on an already-wiped radio, for a value the radio stored exactly
    /// as asked. So the canonical spelling — the radio's own storage form,
    /// byte for byte what the READ campaign writes — is the only spelling the
    /// door admits.</para>
    ///
    /// <para><b>And it REJECTS rather than normalizes.</b> Silently rewriting
    /// the operator's file would make the app the author of a value nobody
    /// typed; refusing it names the offender and leaves the file theirs.</para>
    /// </summary>
    /// <exception cref="CloneValueException">the value is not one this radio
    /// accepts, or is not spelled the way this radio stores it.</exception>
    public static void CheckStoredValue(string key, string value)
    {
        if (Rows.FirstOrDefault(r => r.Key == key) is not { } row) return;

        var parsed = row.Parse(value);
        if (!string.Equals(parsed.Canonical, value, StringComparison.Ordinal))
            throw new CloneValueException(
                $"{key}: the file's value '{value}' is spelled differently from the way this radio "
                + $"stores it ('{parsed.Canonical}'), so writing it would read back as a difference.");
    }

    // ---- storage-form helpers ------------------------------------------------
    // Enum-typed mirrors store the app's OWN enum name, not a wire token: names
    // round-trip exactly, while wire spellings differ per command and some are
    // uncaptured. Verbatim mirrors store what the radio printed.

    /// <summary>
    /// The <c>ActiveModem</c> PARSE. The file's value is the RAW MIRROR
    /// STRING, which is the only form that round-trips: <c>OFF</c> is the
    /// modem-off echo, and an engagement echo leads with the preset NUMBER and
    /// carries its name — <c>1 T39</c>.
    ///
    /// <para><b>DOCUMENTED-FORM-ONLY, and the asymmetry is deliberate.</b> The
    /// engage builder would happily take <c>T39</c> — it accepts a name as
    /// well as a number — but this row REFUSES it, because the file's value is
    /// not a command argument: it is a MIRROR STRING, and the verify compares
    /// it against the mirror the radio reports back. A name-only file would
    /// write correctly and then read back as <c>1 T39</c>, which is a diff
    /// that is not a difference. The row admits only what the radio itself
    /// prints.</para>
    ///
    /// <para><b>THE TWO MIRROR SHAPES, and nothing else</b> (captured; the SSB
    /// <c>SH</c> block's short form is <c>MODEM 1 T39</c> and the modem-off
    /// echo is <c>MODEM OFF</c>). The engaged form ALWAYS carries the name, so
    /// a BARE SELECTOR — <c>"1"</c> — is refused as a shape the radio never
    /// reports: a file carrying it would write correctly and then read back as
    /// <c>1 T39</c>, which is a diff that is not a difference.</para>
    ///
    /// <para><b>The NAME is not a spelling — it is a PROJECTION of the file's
    /// own modem-preset domain</b>, so this parser cannot settle it alone. The
    /// campaign writes preset <c>n</c> from the file's own record and the radio
    /// then names it accordingly, which makes the expected name derivable:
    /// <see cref="CloneFile"/>'s CROSS-FIELD rules check it against
    /// <c>ModemPresets[n]</c>. Here the shape is proven; there the name is.</para>
    /// </summary>
    private static CloneStoredValue ParseActiveModem(string stored)
    {
        static CloneValueException Bad(string stored) => new(
            $"ActiveModem: the file's value '{stored}' is not a shape this radio reports "
            + $"(OFF, or a preset {Wire.ModemPresetMin}-{Wire.ModemPresetMax} WITH its name, "
            + "as in '1 T39').");

        if (string.Equals(stored, "OFF", StringComparison.Ordinal)) return new("OFF", "OFF");

        var parts = stored.Split(' ');
        if (parts.Length != 2) throw Bad(stored);
        if (!int.TryParse(parts[0], NumberStyles.None, Inv, out int preset)
            || preset < Wire.ModemPresetMin || preset > Wire.ModemPresetMax)
            throw Bad(stored);
        if (parts[1].Length == 0) throw Bad(stored);

        // The name's SHAPE only — whether it is the RIGHT name is a question
        // about the preset domain, and the cross-field rules ask it there.
        return new(preset.ToString(Inv), preset.ToString(Inv) + " " + parts[1]);
    }

    private static void SendActiveModem(CloneSurfaces s, string parsed)
    {
        if (parsed == "OFF") s.Modem.Off();
        else s.Modem.Select(parsed);
    }

    private static string? Token<T>(Confirmed<T> value) where T : struct
        => value.IsConfirmed ? value.Value.ToString() : null;

    private static string? Text(Confirmed<string> value)
        => value.IsConfirmed && value.Value is { Length: > 0 } v ? v.Trim() : null;

    private static string? Number(Confirmed<int> value)
        => value.IsConfirmed ? value.Value.ToString(Inv) : null;

    /// <summary>
    /// Parse an enum-valued stored setting — with <c>Enum.IsDefined</c>
    /// (audit round 1) and its CANONICAL spelling (audit round 3).
    ///
    /// <para><c>Enum.TryParse</c> alone SUCCEEDS on undefined NUMERIC text, so
    /// a crafted <c>"DigitalVoice": "99"</c> parsed to <c>(OnOff)99</c> and was
    /// handed to the builder — in a leg the wipe has already preceded. It also
    /// succeeds on ALIASES the file never contains: case variants, and — the
    /// trap for a hand-editor — the NUMERIC forms, where <c>"0"</c> means On
    /// and <c>"1"</c> means Off. The canonical spelling is the enum's own NAME,
    /// which is exactly what the read campaign writes.</para>
    /// </summary>
    private static CloneStoredValue ParseEnum<T>(string stored, string key) where T : struct, Enum
        => Enum.TryParse<T>(stored, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? new(parsed, parsed.ToString()!)
            : throw new CloneValueException($"{key}: the file's value '{stored}' is not one this radio accepts.");

    /// <summary>
    /// <c>ANTENNA</c> — parsed as the port, stored as the MIRROR's spelling,
    /// which is the wire token in UPPER case.
    ///
    /// <para><b>Corrected by the round-trip pin, which is what it is for.</b>
    /// The radio PRINTS lower case (<c>ANTENNA   auto</c>) and the obvious
    /// inference was that the file would carry that — but the PARSER
    /// uppercases a payload before dispatch, so the mirror holds <c>AUTO</c>
    /// and <c>AUTO</c> is what the read campaign writes. The formatter follows
    /// the MIRROR, not the wire, because the mirror is what the file carries
    /// and what the verify compares.</para>
    /// </summary>
    private static CloneStoredValue ParseAntenna(string stored)
    {
        var port = ParseEnum<AntennaPort>(stored, "Antenna");
        return new(port.Value, ((AntennaPort)port.Value).ToWire());
    }

    /// <summary><c>FMSQ_TYPE</c> — the same shape as the antenna, and the same
    /// correction: the radio prints <c>tone</c>, the parser uppercases it, and
    /// the file stores <c>TONE</c>.</summary>
    private static CloneStoredValue ParseFmSquelchType(string stored)
    {
        var type = ParseEnum<FmSquelchType>(stored, "FmSquelchType");
        return new(type.Value, ((FmSquelchType)type.Value).ToWire());
    }

    /// <summary><c>PREPOST SCAN</c> — the payload spelling is the wire token
    /// itself, UPPER case (<c>PREPOST SCAN SLOW</c>).</summary>
    private static CloneStoredValue ParsePrePostScanRate(string stored)
    {
        var rate = ParseEnum<PrePostScanRate>(stored, "PrePostScanRate");
        return new(rate.Value, ((PrePostScanRate)rate.Value).ToWire());
    }

    /// <summary>PREPOST reports ENABLE/DISABLE and its setter takes ON/OFF —
    /// the one place a mirror's vocabulary differs from its setter's. The
    /// stored form is the REPORT's, so that is the canonical one.
    /// <para>The aliases are RECOGNISED and then refused by the canonical
    /// check, deliberately: a file saying <c>ENABLED</c> or <c>ON</c> meant
    /// something perfectly clear, and the operator is better served by
    /// "this radio stores it as ENABLE" than by "not a value this radio
    /// accepts".</para></summary>
    private static CloneStoredValue ParseEnableDisable(string stored, string key)
        => stored.Trim().ToUpperInvariant() switch
        {
            "ENABLE" or "ENABLED" or "ON" => new(OnOff.On, "ENABLE"),
            "DISABLE" or "DISABLED" or "OFF" => new(OnOff.Off, "DISABLE"),
            _ => throw new CloneValueException($"{key}: the file's value '{stored}' is not one this radio accepts."),
        };

    /// <summary>
    /// A number the builder will TAKE — shape, range AND spelling.
    ///
    /// <para>The bounds come from <see cref="Wire"/>, which is where the
    /// BUILDER reads them too, so there is no second copy to drift: the door
    /// proves exactly what the wire will accept. The canonical spelling is the
    /// PLAIN invariant form the mirror stores — no sign, no padding — so
    /// <c>"+8"</c>, <c>"08"</c> and <c>"8 "</c> are all refused for a value of
    /// eight, each of which would otherwise read back as a spurious diff.</para>
    /// </summary>
    private static CloneStoredValue ParseBoundedInt(string stored, string key, int min, int max)
    {
        int value = BoundedInt(stored, key, min, max);
        return new(value, value.ToString(Inv));
    }

    private static int BoundedInt(string stored, string key, int min, int max)
    {
        if (!int.TryParse(stored, NumberStyles.Integer | NumberStyles.AllowLeadingSign, Inv, out int parsed))
            throw new CloneValueException($"{key}: the file's value '{stored}' is not a number.");
        if (parsed < min || parsed > max)
            throw new CloneValueException(
                $"{key}: the file's value '{stored}' is outside {min}-{max}, which is what this radio accepts.");
        return parsed;
    }

    /// <summary>The BFO offset: a signed FOUR-DIGIT payload, stored exactly as
    /// the report prints it (<c>BFO +0000</c>). The sign is part of the
    /// spelling, so an unsigned <c>"9999"</c> is refused.</summary>
    private static CloneStoredValue ParseSignedFourDigit(string stored, string key, int min, int max)
    {
        int value = BoundedInt(stored, key, min, max);
        return new(value, (value < 0 ? "-" : "+") + Math.Abs(value).ToString("D4", Inv));
    }

    /// <summary>The CW offset: a DISCRETE set (a range would admit values
    /// between the two forms that the radio refuses), stored zero-padded to
    /// four digits and UNSIGNED — <c>CWOFFSET 0000</c>.</summary>
    private static CloneStoredValue ParseFourDigit(string stored, string key, IReadOnlyList<int> allowed)
    {
        if (!int.TryParse(stored, NumberStyles.Integer | NumberStyles.AllowLeadingSign, Inv, out int parsed))
            throw new CloneValueException($"{key}: the file's value '{stored}' is not a number.");
        if (!allowed.Contains(parsed))
            throw new CloneValueException(
                $"{key}: the file's value '{stored}' is not one this radio accepts "
                + $"({string.Join(", ", allowed)}).");
        return new(parsed, parsed.ToString("D4", Inv));
    }

    /// <summary>A TEXT value from a discrete set, matched the way the builder
    /// matches it — against the same list. The MATCHED MEMBER is the canonical
    /// spelling, so a padded <c>" 8.0 "</c> is recognised and then refused for
    /// its spelling rather than dismissed as an unknown value: the operator
    /// meant 8.0 and is told so.</summary>
    private static CloneStoredValue ParseOneOfText(string stored, string key, IReadOnlyList<string> allowed)
    {
        var member = allowed.FirstOrDefault(a => string.Equals(a, stored.Trim(), StringComparison.Ordinal));
        if (member is null)
            throw new CloneValueException(
                $"{key}: the file's value '{stored}' is not one this radio accepts "
                + $"({string.Join(", ", allowed)}).");
        return new(member, member);
    }
}
