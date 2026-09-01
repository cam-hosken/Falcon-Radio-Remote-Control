using System.Globalization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The words BOTH HOP net displays use — the Operate pane's "Current net" row
/// (<see cref="HopViewModel"/>) and the HOP-settings pane's net list + editor
/// (<see cref="HopSettingsViewModel"/>).
///
/// <para>Round 4 put the THREE display states here (unreported "—",
/// CONFIRMED UNPROGRAMMED, reported) because two lists of literals cannot be
/// trusted to stay in step. Round 5 (BD1/BD2 + contract K6) moves the whole
/// VALUE vocabulary in as well: the panes used to render the same net
/// differently — the Operate row said "11.565 MHz" and "Wideband", the settings
/// list said "11565" and "—" — and one shared header, "Frequencies (MHz)", now
/// covers both. The cell text and the kHz↔MHz conversions therefore live here
/// ONCE, and the panes agree by construction.</para>
///
/// <para><b>K6, verbatim (plan-ui-tweaks-round5.md §2.4).</b> Wire kHz string ↔
/// MHz display/entry. Display: <c>"11565"</c> → <c>"11.565"</c> (always three
/// decimals; unparseable → verbatim wire text + " kHz", the round-4 fallback).
/// Entry: accepts up to three decimals, range 1.600–29.995 MHz, converted value
/// must be a 5-digit kHz integer ending in 0 or 5 (<c>01600</c>–<c>29995</c>);
/// anything else blocks the send with InputError. The header carries the unit;
/// cells and entries are bare numbers.</para>
/// </summary>
internal static class HopNetDisplay
{
    /// <summary>The radio's own unprogrammed net ID, shown verbatim.</summary>
    internal const string UnprogrammedId = "XXXXXXXX";

    /// <summary>The value cell for a net the radio REPORTED unprogrammed.</summary>
    internal const string UnprogrammedValue = "not programmed";

    /// <summary>The radio's own type vocabulary; "—" until a Hoptype line
    /// reports one.</summary>
    internal static string TypeText(HopType? type) => type switch
    {
        HopType.Narrowband => "NB",
        HopType.Wideband => "WB",
        HopType.List => "LIST",
        _ => "—",
    };

    /// <summary>The three cells of a CONFIRMED-unprogrammed net: the wire's
    /// X-form as its ID, whatever type was reported (a wiped net reports
    /// <c>Hoptype WB</c> — protocol.md), and "not programmed" as its value.
    /// Reached ONLY from <see cref="Falcon.Core.Radio.HopNet
    /// .IsReportedUnprogrammed"/>, never from a null ID.</summary>
    internal static (string NetId, string Type, string Value) Unprogrammed(HopType? type)
        => (UnprogrammedId, TypeText(type), UnprogrammedValue);

    // ---- K6: the MHz vocabulary (round-5 contract) -------------------------

    /// <summary>The GENERIC value-column heading. It carries the unit, so
    /// every cell and every entry below it is a BARE number (BD1/BD2). Since
    /// round 7 (DD) this is the settings NET-LIST tab's heading (its ten rows
    /// mix types) and the Operate row's FALLBACK while no type is confirmed —
    /// the Operate header itself follows the current net's type via
    /// <see cref="ValueHeadingFor"/>.</summary>
    internal const string ValueHeading = "Frequencies (MHz)";

    /// <summary>Round 7 (DD, owner): the Operate Current-net value header
    /// names what the cell actually holds for the confirmed type — a center,
    /// a band, or a hoplist — falling back to the generic heading until the
    /// radio has said which.</summary>
    internal static string ValueHeadingFor(HopType? type) => type switch
    {
        HopType.Narrowband => "Center (MHz)",
        HopType.Wideband => "Band (MHz)",
        HopType.List => "Hoplist",
        _ => ValueHeading,
    };

    /// <summary>Lowest legal hop frequency, MHz (protocol.md: 01600 kHz).</summary>
    internal const decimal MinMhz = 1.600m;

    /// <summary>Highest legal hop frequency, MHz (protocol.md: 29995 kHz).</summary>
    internal const decimal MaxMhz = 29.995m;

    /// <summary>The one sentence every K6 entry rejection uses, so the four
    /// entries cannot describe the same rule three different ways.</summary>
    internal const string EntryRule =
        "frequencies are MHz with up to three decimals, 1.600–29.995, and must "
        + "land on a 5 kHz step (e.g. 11.565).";

    /// <summary>DISPLAY conversion: wire kHz → bare MHz with three decimals.
    /// An unparseable value is shown VERBATIM in the wire's own kHz form (the
    /// round-4 fallback) rather than silently dropped — the header's "(MHz)"
    /// would otherwise mislabel it, so the fallback keeps its unit.</summary>
    internal static string MhzText(string kHz)
        => int.TryParse(kHz, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? (v / 1000m).ToString("0.000", CultureInfo.InvariantCulture)
            : kHz + " kHz";

    /// <summary>ENTRY conversion: operator MHz text → the 5-digit kHz string
    /// the wire takes. Returns false — send nothing — for anything outside K6:
    /// a non-number, more than three decimals, outside 1.600–29.995, or a value
    /// that does not land on a 5 kHz step. The radio SILENTLY IGNORES a badly
    /// formed frequency (protocol.md), so this must catch it first.</summary>
    internal static bool TryParseMhz(string? text, out string kHz)
    {
        kHz = "";
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        // Reject anything that is not a plain decimal number BEFORE parsing:
        // decimal.TryParse would happily accept "+1,600" / "1e3" / whitespace.
        int dot = s.IndexOf('.');
        if (dot == 0 || dot == s.Length - 1) return false;
        foreach (char c in s)
            if (!char.IsAsciiDigit(c) && c != '.') return false;
        if (dot >= 0 && (s.IndexOf('.', dot + 1) >= 0 || s.Length - dot - 1 > 3)) return false;

        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal mhz))
            return false;
        if (mhz < MinMhz || mhz > MaxMhz) return false;

        int v = (int)(mhz * 1000m);          // exact: at most three decimals
        if (v % 5 != 0) return false;        // 5 kHz step — "last digit 0 or 5"
        kHz = v.ToString("00000", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>ENTRY conversion for the round-11 EXCLUSION BANDS (§7): the
    /// operator's MHz text → the <b>8-DIGIT Hz</b> string <c>EXC</c> takes.
    /// The set command is the one place on this pane that speaks Hz — its ECHO
    /// and its listing come back in the same 5-digit kHz everything else uses.
    ///
    /// <para><b>The 5-digit rule's SIBLING, and the same trap.</b> The radio
    /// SILENTLY IGNORES a wrongly-shaped frequency, so a short value is not
    /// rejected — it is MISREAD. The kHz path pads to five (<c>1600</c> would
    /// be a 4-digit send); the Hz path is that same 5-digit string times a
    /// thousand, so it is eight digits BY CONSTRUCTION — and the length is
    /// asserted here anyway, because "by construction" is exactly the kind of
    /// claim a later edit invalidates in silence.</para>
    ///
    /// <para>Grammar: deliberately the SAME K6 grammar as every other frequency
    /// on this pane (<see cref="EntryRule"/>), so the section does not invent a
    /// second way to type a number. The exclusion-band bounds are a §14 probe —
    /// until it runs, the radio's own hop-frequency domain is the honest
    /// bound.</para></summary>
    internal static bool TryParseMhzToHz(string? text, out string hz)
    {
        hz = "";
        if (!TryParseMhz(text, out string kHz)) return false;

        var candidate = kHz + "000";                       // kHz → Hz
        if (candidate.Length != 8 || !candidate.All(char.IsAsciiDigit)) return false;
        hz = candidate;
        return true;
    }

    /// <summary>The round-11 §7 net-info view's SECOND line — the header over
    /// the value, chosen by the net's CONFIRMED type. "Confirmed" is the
    /// settings editor's rule verbatim: a net the radio REPORTED unprogrammed
    /// has no usable type (its <c>Hoptype WB</c> is a wipe artifact, not a
    /// programmed band), so it renders the no-type state.
    ///
    /// <para>A LIST net whose <c>HOPLIST</c> answer has not landed reads "—",
    /// not "0 stored": this pane's only read is the per-pick <c>DIS n</c> (§7
    /// adds no tier), so an unmirrored list is UNKNOWN, and invariant 6 forbids
    /// rendering unknown as zero.</para></summary>
    internal static (string Header, string Value) InfoValueLine(
        HopNet? net, IReadOnlyList<string>? hopList)
    {
        var type = net is { IsReportedUnprogrammed: false } ? net.Type : null;
        return type switch
        {
            HopType.Narrowband => ("Center (MHz)",
                net!.CenterKHz is { } c ? MhzText(c) : "—"),
            HopType.Wideband => ("Low–High (MHz)",
                net!.WidebandLowKHz is { } lo && net.WidebandHighKHz is { } hi
                    ? MhzText(lo) + "–" + MhzText(hi)
                    : "—"),
            HopType.List => ("Frequencies",
                hopList is null
                    ? "—"
                    : hopList.Count.ToString(CultureInfo.InvariantCulture) + " stored"),
            _ => ("—", "—"),
        };
    }

    /// <summary>The BD2 value cell for one net, in the vocabulary BOTH panes
    /// render: NB → the center ("11.565"); WB → the band ("2.000–8.000", from
    /// the round-5 mirrored edges); LIST → the frequency COUNT ("8 freqs",
    /// real once a HOPLIST answer lands — "Frequency list" until then, because
    /// no DIS answer carries a list). A field the radio has not reported is
    /// "—"; the CONFIRMED-unprogrammed state is <see cref="Unprogrammed"/>'s
    /// job and is checked by the caller before this.</summary>
    internal static string ValueText(HopNet net, IReadOnlyList<string>? hopList) => net.Type switch
    {
        HopType.Narrowband => net.CenterKHz is null ? "—" : MhzText(net.CenterKHz),
        HopType.Wideband => net.WidebandLowKHz is null || net.WidebandHighKHz is null
            ? "—"
            : MhzText(net.WidebandLowKHz) + "–" + MhzText(net.WidebandHighKHz),
        HopType.List => hopList is { Count: > 0 }
            ? hopList.Count.ToString(CultureInfo.InvariantCulture) + " freqs"
            : "Frequency list",
        _ => "—",
    };

    /// <summary>The three cells of one net, from the mirror, in the
    /// constitution's three display states. THE shared entry point: the
    /// Operate row, the settings net list and the editor all call this, so a
    /// single mirror state cannot render two ways.</summary>
    internal static (string NetId, string Type, string Value) Describe(
        HopNet? net, IReadOnlyList<string>? hopList)
    {
        if (net is null) return ("—", "—", "—");
        if (net.IsReportedUnprogrammed) return Unprogrammed(net.Type);
        return (net.NetId ?? "—", TypeText(net.Type), ValueText(net, hopList));
    }
}
