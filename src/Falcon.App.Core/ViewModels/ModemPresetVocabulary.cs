namespace Falcon.App.Core.ViewModels;

/// <summary>
/// One value of one modem-preset field, in every form the app needs it
/// (UI-tweaks round 9 — the vocabulary seam; <see cref="HopNetDisplay"/> is
/// the display-vocabulary precedent).
///
/// <para><b>The plan's four columns, with one deliberate collapse.</b> The
/// round-9 table is <i>canonical ↔ wire short ↔ listing form(s) ↔ display
/// word</i>. The app carries ONE identifier, not two: the WIRE short IS the
/// canonical value, because it is already unique per field and it is what the
/// ViewModel stores, compares and hands to the builder. A second synonym would
/// be a name to keep in sync for no gain.</para>
/// </summary>
/// <param name="Wire">The token SENT (the canonical value). Short per the
/// radio's capital-letter abbreviation convention — session-07's
/// <c>HELP MODEM</c>: "capital letters denote acceptable abbreviation".</param>
/// <param name="ListingForms">The spellings the radio's LISTING line may use
/// for this value, matched case-insensitively. Empty = this value has never
/// been seen in a listing, so no LISTING-driven prefill can reach it. (Round
/// 13 B1: that is not the same as "never prefills" — the STATE column has no
/// listing forms and prefills from the presence store instead.)</param>
/// <param name="Display">The human-readable word shown on screen.</param>
public sealed record ModemPresetValue(
    string Wire,
    IReadOnlyList<string> ListingForms,
    string Display);

/// <summary>
/// The modem-preset value vocabulary (UI-tweaks round 9): short tokens on the
/// wire, human-readable words on screen, and the listing spellings that map a
/// radio report back to a selection. ONE class owns all of it, so the editor,
/// the read-back and the Store line cannot drift apart.
///
/// <para><b>Evidence tiers</b> (docs/protocol.md "Modem"; the tokens come from
/// the VERBATIM <c>HELP MODEM</c> screen captured 2026-07-31,
/// falcon-reference session-07):</para>
/// <list type="bullet">
///   <item><b>ALL VERIFIED 2026-08-16</b> (bench/probe-modem-presets.ps1 and
///     its two follow-ups): every wire token below was sent to the real radio
///     and every listing spelling was read back off it. Short-token write
///     acceptance — the round-9 ASSUMPTION — HOLDS. The listing forms are
///     lowercase on the wire (<c>fskws</c>, <c>serial</c>, <c>zero</c>) and
///     the entries here stayed correct because matching is
///     case-insensitive.</item>
/// </list>
///
/// <para><b>The TYPE-SCOPED OFFERS (round 11 §6) — what the bench added, now
/// modelled.</b> These constraints are real, per-TYPE, and SILENT: the radio
/// does not error, it stores something else, so the app would report success
/// from the echo. The only defence is to never OFFER the value, which is what
/// <see cref="BaudsFor"/>, <see cref="InterleavesFor"/> and
/// <see cref="OffersMarkSpace"/> do:</para>
/// <list type="bullet">
///   <item><b>Baud has a per-type ceiling</b> (VERIFIED 2026-08-16) —
///     <c>fskns</c> 75, <c>fsk-a</c> 150, <c>fskws</c> 300, <c>fsk-v</c> 600,
///     <c>39tone</c> 75-2400, <c>serial</c> ≤4800; anything higher is silently
///     CLAMPED and the read-back is the only truth.</item>
///   <item><b>The interleave VALUE SETS differ by type</b> — <c>39TONE</c>
///     takes LO/SH/ALTS/ALTL and REFUSES ZE; <c>SE</c> takes LO/SH/ZE and
///     REFUSES ALTS/ALTL; the FSK types refuse INTERLEAV outright; and
///     <c>SE</c> at 4800 baud is <c>uncoded</c>, with no interleave to
///     choose.</item>
///   <item><c>BAUD VO</c> (Voice) exists at <c>39TONE</c> ONLY — at
///     <c>fsk-a</c> the same token is silently clamped to 150. At 39-tone it
///     also flips the stored DATA MODE to sync, which the app renders from the
///     READ-BACK and never predicts.</item>
///   <item><b>MARK/SPACE</b> are stored on every FSK type but LISTED only at
///     <c>fsk-v</c>, where the spelling is captured (<c>MARK 1500 SPACE
///     1700</c>) — written at <c>fskns</c>, invisible there, revealed intact
///     by flipping the type (2026-08-17). Accepted 350-3250 (the MEASURED
///     window, 2026-08-18 — see <see cref="MarkSpaceMinimum"/>); outside that
///     the radio SILENTLY keeps the old values. They are therefore offered at
///     <c>fsk-v</c> only: everywhere else the radio will not read the value
///     back, and this card's whole contract is that the read-back is the
///     truth.</item>
/// </list>
///
/// <para><b>Parse rule — the AGC precedent, unchanged.</b> A listing token
/// that maps prefills the selection; a token that does NOT map leaves the
/// selection EMPTY (which blocks Store until the operator picks) while the
/// read-back row shows the radio's own text verbatim. Nothing is ever
/// guessed into a selection.</para>
///
/// <para><b>Core validates its own wire lists.</b> <c>Falcon.Core</c> cannot
/// reference this assembly, so <c>SsbController.ProgramModemPreset</c> keeps
/// an independent copy of the WIRE column. The cross-check that the two agree
/// is a test (ModemPresetVocabularyTests), not a runtime dependency.</para>
/// </summary>
public static class ModemPresetVocabulary
{
    /// <summary>TYpe — <c>(39tone/FSKWs/FSKNs/FSK-A/FSK-V/SErial)</c>.
    /// <c>39tone</c> has no capitals, so it has NO abbreviation: the whole
    /// token is the wire form.</summary>
    public static IReadOnlyList<ModemPresetValue> Types { get; } =
    [
        // ROUND 11 §3 (P5): the DISPLAY words only — every wire token and
        // every listing form on this line is untouched. "FSK-A"/"FSK-V" were
        // the wire tokens shown verbatim; the radio's own manual reads them as
        // the ASCII and VFT waveforms, and a button is not the place to make
        // an operator decode a hyphenated abbreviation.
        new("39TONE", ["39TONE"], "39 tone"),          // listing "39tone"  — VERIFIED
        new("FSKW", ["FSKWS"], "FSK wide"),            // listing "fskws"   — VERIFIED 2026-08-16
        new("FSKN", ["FSKNS"], "FSK narrow"),          // listing "fskns"   — VERIFIED 2026-08-16
        new("FSK-A", ["FSK-A"], "FSK ASCII"),          // listing "fsk-a"   — VERIFIED 2026-08-16
        new("FSK-V", ["FSK-V"], "FSK VFT"),            // listing "fsk-v"   — VERIFIED 2026-08-16
        new("SE", ["SERIAL"], "Serial"),               // listing "serial"  — VERIFIED 2026-08-16
    ];

    /// <summary>ASync <c>(REMote/DATa)</c> + SYnc. The listing prints the data
    /// mode as TWO tokens ("ASYNC DATA"), so these match a PHRASE.</summary>
    /// <para>Round 10 §3/§8 (owner ruling): the DISPLAY words carry the full
    /// PORT phrase — the buttons are the row's label. ROUND 11 §3 (P5) makes
    /// the row label "Port" and leads each button with the PORT it selects,
    /// with the async/sync signalling in parentheses behind it: the operator
    /// picks a port first and a signalling mode second, and the round-10
    /// wording had that the wrong way round. Wire tokens and listing forms are
    /// untouched.</para>
    public static IReadOnlyList<ModemPresetValue> DataModes { get; } =
    [
        new("ASYNC REM", ["ASYNC REMOTE"], "Remote port (async)"),  // VERIFIED 2026-08-16
        new("ASYNC DAT", ["ASYNC DATA"], "Data port (async)"),      // VERIFIED (session-15)
        // The radio prints "SYNC  DATA" with TWO spaces (column padding); the
        // phrase is rebuilt from RemoveEmptyEntries tokens, so it arrives here
        // single-spaced and this one-space form matches. VERIFIED 2026-08-16.
        new("SYNC DAT", ["SYNC DATA"], "Data port (sync)"),
    ];

    /// <summary>BAUd — <c>(75/150/300/600/1200/2400/4800/VOice)</c>. A
    /// DISCRETE set, not a range: the HELP screen lists exactly these eight.
    /// The wheel cycles them in this order.</summary>
    public static IReadOnlyList<ModemPresetValue> Bauds { get; } =
    [
        new("75", ["75"], "75"),
        new("150", ["150"], "150"),
        new("300", ["300"], "300"),
        new("600", ["600"], "600"),
        new("1200", ["1200"], "1200"),
        new("2400", ["2400"], "2400"),                 // VERIFIED (session-15)
        new("4800", ["4800"], "4800"),
        // Listing spelling is MIXED CASE ("BAUD Voice") — unlike every other
        // listing value, which is lowercase. Matching is case-insensitive so
        // this entry is unaffected. VERIFIED 2026-08-16 at the 39TONE type;
        // at fsk-a the same VO is silently clamped to 150 instead.
        new("VO", ["VOICE"], "Voice"),
    ];

    // ---- CLONE-FIELD ROUND 2 F9/F11: the `HOP>` preset columns ---------------
    // A `HOP>` preset (7-9) has FEWER fields than an SSB one and prints them in
    // a SHORTER line: `MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300` — no TYPE, no
    // INTER, no MARK/SPACE. The mode phrase is TWO INDEPENDENT WORDS there
    // (P5b applied `SYNC DATA` on its own and `ASYNC REMOTE` on its own), so it
    // is modelled as two columns rather than the SSB welded phrase.
    // Transcripts: p5-hop-modem-presets-20260821-180547.jsonl,
    // p5b-hop-modem-preset-write-20260821-181018.jsonl,
    // p5c-hop-modem-baud-20260821-182807.jsonl.

    /// <summary>The signalling column of a <c>HOP&gt;</c> preset. Wire tokens
    /// are the FULL words: the only captures are P5b's accepted lines and they
    /// spell them out.</summary>
    public static IReadOnlyList<ModemPresetValue> SyncModes { get; } =
    [
        new("ASYNC", ["ASYNC"], "Async"),      // CAPTURED (P5, P5b)
        new("SYNC", ["SYNC"], "Sync"),         // CAPTURED (P5b, `MODEM PRESET 9 SYNC DATA`)
    ];

    /// <summary>The PORT column of a <c>HOP&gt;</c> preset, spelled out as the
    /// line prints it. Separate from <see cref="DataModes"/>, which welds port
    /// and signalling into the one phrase the SSB builder sends.</summary>
    public static IReadOnlyList<ModemPresetValue> HopPorts { get; } =
    [
        new("DATA", ["DATA"], "Data port"),        // CAPTURED (P5b)
        new("REMOTE", ["REMOTE"], "Remote port"),  // CAPTURED (P5)
    ];

    /// <summary>The BAUD values a <c>HOP&gt;</c> preset really stores —
    /// MEASURED by set + read-back on preset 9 (P5c): exactly these three. 50,
    /// 100, 110, 200, 600, 1200, 2400, 4800 and 9600 are SILENTLY ignored (the
    /// line echoes the OLD value, no error), which is why the wheel must never
    /// offer them. Mirrors <see cref="Falcon.Core.Protocol.Wire.HopModemBauds"/>,
    /// the builder's own copy; a test cross-checks the two.</summary>
    public static IReadOnlyList<ModemPresetValue> HopBauds { get; } =
    [
        new("75", ["75"], "75"),
        new("150", ["150"], "150"),
        new("300", ["300"], "300"),
    ];

    /// <summary>INTerleav — <c>(LOng/SHort/ALTS/ALTL/ZEro)</c>. The listing
    /// echoes <c>INTER long</c>, which IS the <c>LOng</c> token spelled out —
    /// the round-8 note that it "does not match any HELP token" was wrong and
    /// is corrected in docs/protocol.md.</summary>
    public static IReadOnlyList<ModemPresetValue> Interleaves { get; } =
    [
        new("LO", ["LONG"], "Long"),                   // VERIFIED (session-15)
        new("SH", ["SHORT"], "Short"),                 // VERIFIED 2026-08-16
        new("ALTS", ["ALTS"], "Alt short"),            // VERIFIED 2026-08-16 (39TONE only)
        new("ALTL", ["ALTL"], "Alt long"),             // VERIFIED 2026-08-16 (39TONE only)
        new("ZE", ["ZERO"], "Zero"),                   // VERIFIED 2026-08-16 (SE only)
    ];

    /// <summary>Interleave spellings the radio EMITS but no token produces —
    /// read-only, so a report maps to a display word instead of dropping the
    /// row to the verbatim fallback.
    /// <para><c>uncoded</c> appeared unprompted on 2026-08-16: writing
    /// <c>BAUD 4800</c> at the <c>SE</c> type, with no interleave argument on
    /// the line, replaced a stored <c>zero</c> with it. It is in no HELP list
    /// and nothing sends it, so it has NO wire token — mapping it here would
    /// let Store offer a value the radio has never accepted. It is matched for
    /// DISPLAY only, by <see cref="InterleaveDisplayFromListing"/>.</para>
    /// </summary>
    private static IReadOnlyList<ModemPresetValue> ReadOnlyInterleaves { get; } =
    [
        new("", ["UNCODED"], "Uncoded"),
    ];

    /// <summary>ENable / DISable — WRITE tokens and DISPLAY words only.
    ///
    /// <para>The empty <c>ListingForms</c> are the point, not an omission: no
    /// capture has ever shown a preset's enabled state on a listing line, and
    /// round 11 §6 established that it comes from the bulk PRESENCE operation
    /// and nowhere else. Round 13 B1 (item 7) therefore DELETED
    /// <c>StateFromListing</c> — a lookup that could only ever return null —
    /// and the editor prefills this column from the presence store instead
    /// (<c>ModemPresetsViewModel.PresenceStatePrefill</c>). <c>DisplayOf</c>
    /// stays: the read-back cell and the buttons both render these
    /// words.</para></summary>
    public static IReadOnlyList<ModemPresetValue> States { get; } =
    [
        new("EN", [], "Enabled"),
        new("DIS", [], "Disabled"),
    ];

    /// <summary>The FSK types — the only ones MArk/SPace apply to (HELP lists
    /// them as FSK parameters; the old app's builder comments say the
    /// same).</summary>
    public static IReadOnlyList<string> FskTypeWires { get; } =
        ["FSKW", "FSKN", "FSK-A", "FSK-V"];

    /// <summary>The types the INTERLEAV row applies to — the tone waveforms.
    /// VERIFIED 2026-08-16: the FSK types refuse INTERLEAV. This list is the
    /// READ-BACK projection's type map (<see cref="ModemPresetRow"/>); the
    /// EDITOR's offer is <see cref="InterleavesFor"/>, which additionally
    /// closes the serial-at-4800 hole.</summary>
    public static IReadOnlyList<string> InterleaveTypeWires { get; } =
        ["39TONE", "SE"];

    // ---- ROUND 11 §6: the TYPE-SCOPED OFFERS --------------------------------
    // Every rule here is a SILENT radio behaviour (clamp / substitute / ignore)
    // that no echo distinguishes from success, so the defence is to never put
    // the value on screen. Each is keyed by the WIRE token, because that is
    // what the ViewModel holds.

    /// <summary>The highest baud each type actually stores, as an INDEX into
    /// <see cref="Bauds"/>'s numeric prefix (75/150/300/600/1200/2400/4800).
    /// A type absent from this map is not one of ours and gets the whole
    /// set.</summary>
    private static readonly Dictionary<string, string> BaudCeilings =
        new(StringComparer.Ordinal)
        {
            ["FSKN"] = "75",        // listing fskns  — VERIFIED 2026-08-16
            ["FSK-A"] = "150",      // listing fsk-a  — VERIFIED 2026-08-16
            ["FSKW"] = "300",       // listing fskws  — VERIFIED 2026-08-16
            ["FSK-V"] = "600",      // listing fsk-v  — VERIFIED 2026-08-16
            ["39TONE"] = "2400",    // 75-2400        — VERIFIED 2026-08-16
            ["SE"] = "4800",        // listing serial — VERIFIED 2026-08-16
        };

    /// <summary>The BAUD values <paramref name="typeWire"/> really stores, in
    /// wheel order. <c>VO</c> (Voice) rides ONLY at 39-tone — everywhere else
    /// the token is silently clamped to a number, so offering it would be
    /// offering a lie. A null/unknown type gets the FULL discrete set: nothing
    /// is known to be out of bounds until a type is picked, and the type is
    /// required before Store will send anything anyway.</summary>
    public static IReadOnlyList<ModemPresetValue> BaudsFor(string? typeWire)
    {
        if (typeWire is null || !BaudCeilings.TryGetValue(typeWire, out var ceiling))
            return Bauds;

        var offered = new List<ModemPresetValue>();
        foreach (var value in Bauds)
        {
            // VO is the LAST entry of the discrete set, not part of the
            // numeric run the ceiling truncates — so it is appended after the
            // run rather than filtered inside it (a `break` at 2400 would
            // never reach it).
            if (value.Wire == "VO") continue;
            offered.Add(value);
            if (value.Wire == ceiling) break;
        }
        if (typeWire == "39TONE")
            offered.Add(Bauds.Single(v => v.Wire == "VO"));
        return offered;
    }

    /// <summary>The INTERLEAVE values <paramref name="typeWire"/> accepts at
    /// <paramref name="baudWire"/>. EMPTY means the row does not apply and is
    /// HIDDEN — the FSK types (which refuse INTERLEAV), Serial at 4800 (which
    /// the radio reports as the write-less <c>uncoded</c>), and no type
    /// picked.</summary>
    public static IReadOnlyList<ModemPresetValue> InterleavesFor(string? typeWire, string? baudWire)
        => typeWire switch
        {
            "39TONE" => [.. Interleaves.Where(v => v.Wire is "LO" or "SH" or "ALTS" or "ALTL")],
            "SE" when baudWire != "4800" => [.. Interleaves.Where(v => v.Wire is "LO" or "SH" or "ZE")],
            _ => [],
        };

    /// <summary>MARK/SPACE are offered at <c>fsk-v</c> ONLY (see the class
    /// remarks: stored elsewhere, displayed nowhere else, so unverifiable).</summary>
    public static bool OffersMarkSpace(string? typeWire) => typeWire == "FSK-V";

    /// <summary>
    /// The MARK/SPACE bounds the CLIENT enforces, inclusive — the MEASURED
    /// window (docs/protocol.md "MARK / SPACE — the measured window",
    /// 2026-08-18, `bench/probe-r11-modem.ps1 -b`), NOT the 500-3200 this
    /// constant carried until clone round 12.
    ///
    /// <para><b>Why it moved, and why exactly here.</b> The old pair was
    /// INTERPOLATED from two probe values and was wrong at BOTH ends. The
    /// sweep then took the edges one field at a time: 300 and below is
    /// REFUSED, 350 stores; 3250 stores, 3290 and above is refused. The
    /// document's conclusion over both fields is the window
    /// <c>(300, ~3250…3289]</c>, and the constants below are its CAPTURED
    /// ACCEPTED EXTREMES — 350 the lowest value seen to store, 3250 the
    /// highest.</para>
    ///
    /// <para><b>Still deliberately tighter than the window's outer edge, and
    /// the reason is recorded</b> (the P5-round-11 idiom): the ceiling was
    /// bracketed to somewhere in 3251-3289 rather than pinned, and every
    /// high-edge attempt was made with the OTHER tone low — whether the
    /// ceiling is absolute or depends on the MARK/SPACE separation is unswept.
    /// The per-field sweeps also differ (MARK's low edge was walked down to
    /// 350, SPACE's only to 499; SPACE's high edge reached 3250, MARK's only
    /// 3201), so a single client pair takes the union the document itself
    /// draws and refuses beyond the last value actually observed to store.</para>
    ///
    /// <para><b>The failure mode on the other side is SILENT</b> — and that is
    /// what makes a client bound worth having at all: <b>a refused value
    /// answers NOTHING AT ALL</b>. No error line, no clamp; the stored value is
    /// simply left alone, so a client that did not read back could not tell a
    /// refusal from a no-op. A refusal naming the bound is better for an
    /// operator than a write that looks like success. (Baud is the opposite
    /// case: it CLAMPS silently and echoes the clamped value.) The UNITS are
    /// Hz — no longer an inference: the bench radio's hidden pair reads
    /// <c>MARK 1070 SPACE 1275</c>, Bell-103 audio tones, and the ceiling sits
    /// just above the 3.2 kHz SSB passband edge.</para>
    /// </summary>
    public const int MarkSpaceMinimum = 350;
    public const int MarkSpaceMaximum = 3250;

    // ---- Listing → canonical (the AGC precedent: no match, no selection) ----

    public static string? TypeFromListing(string? token) => FromListing(Types, token);

    public static string? DataModeFromListing(string? phrase) => FromListing(DataModes, phrase);

    public static string? BaudFromListing(string? token) => FromListing(Bauds, token);

    public static string? InterleaveFromListing(string? token) => FromListing(Interleaves, token);

    /// <summary>The <c>HOP&gt;</c> line's signalling word → its wire token (F11).</summary>
    public static string? SyncModeFromListing(string? token) => FromListing(SyncModes, token);

    /// <summary>The <c>HOP&gt;</c> line's port word → its wire token (F11).</summary>
    public static string? HopPortFromListing(string? token) => FromListing(HopPorts, token);

    /// <summary>The DISPLAY word for a reported interleave, covering the
    /// read-only spellings the radio emits with no matching write token (see
    /// <see cref="ReadOnlyInterleaves"/>). Null when nothing maps.
    /// <para>Separate from <see cref="InterleaveFromListing"/> on purpose: that
    /// one answers "which token would I SEND to get this", and for a read-only
    /// spelling the honest answer is "none". This one answers "what do I SHOW",
    /// so a report the app cannot reproduce is still rendered in words instead
    /// of costing the row its parse.</para></summary>
    public static string? InterleaveDisplayFromListing(string? token)
    {
        var wire = FromListing(Interleaves, token);
        if (wire is not null) return DisplayOf(Interleaves, wire);
        var reported = (token ?? "").Trim();
        if (reported.Length == 0) return null;
        foreach (var value in ReadOnlyInterleaves)
            foreach (var form in value.ListingForms)
                if (string.Equals(form, reported, StringComparison.OrdinalIgnoreCase))
                    return value.Display;
        return null;
    }

    // ROUND 13 B1 (item 7): StateFromListing is DELETED. It read the STATE
    // column's listing forms, and that column has none — it could only ever
    // return null. The editor's state prefill now comes from the presence
    // store (the only captured source), so nothing is lost but a dead lookup.

    /// <summary>The one matcher: case-insensitive over the radio-cased listing
    /// spellings, whitespace-trimmed, and null for anything unmapped.</summary>
    private static string? FromListing(IReadOnlyList<ModemPresetValue> column, string? reported)
    {
        var token = (reported ?? "").Trim();
        if (token.Length == 0) return null;
        foreach (var value in column)
            foreach (var form in value.ListingForms)
                if (string.Equals(form, token, StringComparison.OrdinalIgnoreCase))
                    return value.Wire;
        return null;
    }

    // ---- Canonical → display -------------------------------------------------

    /// <summary>The display word for a wire token, or the token itself if it
    /// is not one of ours (honesty over prettiness — the H2 rule).</summary>
    public static string DisplayOf(IReadOnlyList<ModemPresetValue> column, string? wire)
    {
        foreach (var value in column)
            if (string.Equals(value.Wire, wire, StringComparison.Ordinal))
                return value.Display;
        return wire ?? "—";
    }
}
