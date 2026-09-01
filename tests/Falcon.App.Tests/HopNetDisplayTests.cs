using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The SHARED HOP value vocabulary (round-5 BD1/BD2 + contract K6). Both HOP
/// panes render nets through this one type, so its rules are pinned here once
/// rather than twice in two VM suites — and the "the panes agree" pins in
/// HopSettingsViewModelTests check that both really go through it.
///
/// K6, verbatim from the plan: wire kHz string ↔ MHz display/entry. Display:
/// "11565" → "11.565" (always three decimals; unparseable → verbatim wire text
/// + " kHz"). Entry: up to three decimals, range 1.600–29.995 MHz, converted
/// value must be a 5-digit kHz integer ending in 0 or 5 (01600–29995);
/// anything else blocks the send.
/// </summary>
public class HopNetDisplayTests
{
    // ---- K6 display: kHz → bare MHz -----------------------------------------

    [Theory]
    [InlineData("11565", "11.565")]      // the R9 capture
    [InlineData("02000", "2.000")]       // three decimals ALWAYS
    [InlineData("08000", "8.000")]
    [InlineData("01600", "1.600")]       // the low boundary
    [InlineData("29995", "29.995")]      // the high boundary
    public void MhzText_ConvertsTheWireForm(string kHz, string expected)
        => Assert.Equal(expected, HopNetDisplay.MhzText(kHz));

    [Fact]
    public void MhzText_UnparseableValue_ShowsTheWireTextWithItsOwnUnit()
    {
        // The round-4 fallback, kept: the column heading says MHz, so a value
        // that is NOT MHz has to carry its own unit or it would be mislabelled.
        Assert.Equal("XXXXXX kHz", HopNetDisplay.MhzText("XXXXXX"));
    }

    // (MhzEntry — the round-5/7 entry-prefill helper — was retired by round
    // 8's EA: reported values render in the blue read displays via MhzText,
    // and the entries are never seeded from a report at all.)

    // ---- K6 entry: MHz text → the 5-digit kHz the wire takes ------------------

    [Theory]
    [InlineData("1.600", "01600")]       // the LOW boundary, accepted
    [InlineData("29.995", "29995")]      // the HIGH boundary, accepted
    [InlineData("11.565", "11565")]
    [InlineData("2", "02000")]           // no decimals is "up to three"
    [InlineData("8.0", "08000")]
    [InlineData("11.01", "11010")]
    public void TryParseMhz_AcceptsLegalEntries(string entry, string kHz)
    {
        Assert.True(HopNetDisplay.TryParseMhz(entry, out string actual));
        Assert.Equal(kHz, actual);
    }

    [Theory]
    [InlineData("1.599")]                // below the range
    [InlineData("1.595")]                // below the range even on a 5 kHz step
    [InlineData("30.000")]               // above the range
    [InlineData("29.996")]               // above the range (and off the step)
    [InlineData("11.567")]               // not on a 5 kHz step
    [InlineData("11.5651")]              // more than three decimals
    [InlineData("11,565")]               // comma is not the invariant separator
    [InlineData("-11.565")]
    [InlineData("+11.565")]
    [InlineData("1e4")]
    [InlineData("11565")]                // kHz typed into an MHz entry: out of range
    [InlineData("abc")]
    [InlineData(".565")]
    [InlineData("11.")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseMhz_BlocksEverythingElse_AndYieldsNoWireValue(string? entry)
    {
        Assert.False(HopNetDisplay.TryParseMhz(entry, out string kHz));
        Assert.Equal("", kHz);
    }

    [Fact]
    public void EntryAndDisplay_RoundTrip()
    {
        // The two directions are one contract: what the pane shows must be
        // re-sendable as-is, or the K5 prefill would hand the operator a value
        // their own Set button rejects.
        foreach (var kHz in new[] { "01600", "02000", "11565", "29995" })
        {
            Assert.True(HopNetDisplay.TryParseMhz(HopNetDisplay.MhzText(kHz), out string back));
            Assert.Equal(kHz, back);
        }
    }

    // ---- BD2 cell text -------------------------------------------------------

    private static HopNet Net(
        HopType? type = null, string? id = "12345678", bool unprogrammed = false,
        string? center = null, string? low = null, string? high = null)
        => new()
        {
            Number = 0,
            NetId = id,
            IsReportedUnprogrammed = unprogrammed,
            Type = type,
            CenterKHz = center,
            WidebandLowKHz = low,
            WidebandHighKHz = high,
        };

    [Fact]
    public void ValueText_Narrowband_IsTheBareCentre()
        => Assert.Equal("11.565",
            HopNetDisplay.ValueText(Net(HopType.Narrowband, center: "11565"), null));

    [Fact]
    public void ValueText_Wideband_IsTheBand()
        => Assert.Equal("2.000–8.000",
            HopNetDisplay.ValueText(Net(HopType.Wideband, low: "02000", high: "08000"), null));

    [Fact]
    public void ValueText_Wideband_HalfAnEdge_IsUnreported()
    {
        // The edges are a pair in Core, but the display must not depend on
        // that: half a band is not a band.
        Assert.Equal("—", HopNetDisplay.ValueText(Net(HopType.Wideband, low: "02000"), null));
        Assert.Equal("—", HopNetDisplay.ValueText(Net(HopType.Wideband, high: "08000"), null));
        Assert.Equal("—", HopNetDisplay.ValueText(Net(HopType.Wideband), null));
    }

    [Fact]
    public void ValueText_List_IsTheCount_OrTheFallbackUntilTheListLands()
    {
        Assert.Equal("3 freqs",
            HopNetDisplay.ValueText(Net(HopType.List), ["11010", "11015", "11020"]));
        Assert.Equal("Frequency list", HopNetDisplay.ValueText(Net(HopType.List), null));
        Assert.Equal("Frequency list", HopNetDisplay.ValueText(Net(HopType.List), []));
    }

    [Fact]
    public void ValueText_UnreportedType_IsADash()
        => Assert.Equal("—", HopNetDisplay.ValueText(Net(center: "11565"), null));

    [Fact]
    public void Describe_TheThreeDisplayStates()
    {
        Assert.Equal(("—", "—", "—"), HopNetDisplay.Describe(null, null));

        Assert.Equal(("XXXXXXXX", "WB", "not programmed"),
            HopNetDisplay.Describe(Net(HopType.Wideband, id: null, unprogrammed: true), null));

        Assert.Equal(("12345678", "NB", "11.565"),
            HopNetDisplay.Describe(Net(HopType.Narrowband, center: "11565"), null));
    }

    [Fact]
    public void TheHeading_CarriesTheUnit_SoCellsCanBeBare()
    {
        // BD1: one header for both panes. The C2 gate greps each XAML for this
        // literal; this pin is what makes the two greps mean the same thing.
        Assert.Equal("Frequencies (MHz)", HopNetDisplay.ValueHeading);
    }

    // ==== ROUND 11 SECTION 7: the padding traps, at the conversion seam ======
    //
    // THE TRAP, once, because both halves below exist for it. The radio does
    // not REJECT a wrongly-shaped frequency - it SILENTLY IGNORES it
    // (docs/protocol.md). A value one digit short is therefore not a refused
    // command, it is a command that appears to work and does something else,
    // or nothing, with no line on the wire to say so. The conversions are the
    // only thing standing between an operator's keystrokes and that, so their
    // WIDTH is pinned rather than left as a property of the format string.

    [Fact]
    public void TheFiveDigitRule_TheTrapValue_CanNeverReachTheWireAsFourDigits()
    {
        // THE fixture: 1100 kHz. It is the shape that would break - four
        // digits - and it is barred twice over, which is why both halves are
        // asserted rather than one.
        //
        // (a) 1.100 MHz is BELOW the radio's band (01600), so it never converts
        //     at all. Nothing is produced to be mis-sent.
        Assert.False(HopNetDisplay.TryParseMhz("1.100", out string refused));
        Assert.Equal("", refused);

        // (b) …and the values that DO convert in that decade - everything from
        //     1.600 to 9.995 - come out ZERO-PADDED to five, so the four-digit
        //     shape cannot appear by the front door either.
        Assert.True(HopNetDisplay.TryParseMhz("1.600", out string low));
        Assert.Equal("01600", low);
        Assert.True(HopNetDisplay.TryParseMhz("9.995", out string high));
        Assert.Equal("09995", high);
    }

    [Fact]
    public void TheFiveDigitRule_HoldsForEVERY_LegalValue_NotJustTheBoundaries()
    {
        // A boundary pair proves the boundaries. This proves the RULE: every
        // value the entry grammar accepts converts to exactly five digits, all
        // of them digits. A format-string edit that dropped the padding would
        // pass a spot check on 11.565 and fail here on the whole low decade.
        for (int kHz = 1600; kHz <= 29995; kHz += 5)
        {
            var typed = (kHz / 1000m).ToString("0.000",
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(HopNetDisplay.TryParseMhz(typed, out string wire), typed);
            Assert.Equal(5, wire.Length);
            Assert.All(wire, c => Assert.True(char.IsAsciiDigit(c), typed));
            Assert.Equal(kHz, int.Parse(wire,
                System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void TheEIGHT_DigitSibling_IsTheExclusionBandsWireForm()
    {
        // Section 7's sibling rule: EXC takes 8-digit Hz while every control on
        // the pane speaks MHz. The captured band edges are the fixture.
        Assert.True(HopNetDisplay.TryParseMhzToHz("2.000", out string low));
        Assert.Equal("02000000", low);
        Assert.True(HopNetDisplay.TryParseMhzToHz("3.000", out string high));
        Assert.Equal("03000000", high);

        // The Hz form is the kHz form times a thousand, so the SAME padding
        // question arises one decade lower - and gets the same answer.
        Assert.True(HopNetDisplay.TryParseMhzToHz("1.600", out string bottom));
        Assert.Equal("01600000", bottom);
        Assert.True(HopNetDisplay.TryParseMhzToHz("29.995", out string top));
        Assert.Equal("29995000", top);
    }

    [Fact]
    public void TheEIGHT_DigitSibling_HoldsForEVERY_LegalValue()
    {
        // The same whole-domain sweep as the 5-digit rule, because "eight by
        // construction" is exactly the kind of claim a later edit invalidates
        // in silence.
        for (int kHz = 1600; kHz <= 29995; kHz += 5)
        {
            var typed = (kHz / 1000m).ToString("0.000",
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(HopNetDisplay.TryParseMhzToHz(typed, out string wire), typed);
            Assert.Equal(8, wire.Length);
            Assert.All(wire, c => Assert.True(char.IsAsciiDigit(c), typed));
            Assert.Equal(kHz * 1000, int.Parse(wire,
                System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Theory]
    [InlineData("1.100")]        // below the band - THE trap value
    [InlineData("0.320")]        // …and further below
    [InlineData("30.000")]       // above it
    [InlineData("11.5655")]      // four decimals
    [InlineData("11.567")]       // off the 5 kHz step
    [InlineData("5,320")]        // a DECIMAL COMMA - one token, not two numbers
    [InlineData("2.000 3.000")]  // two values in one edge
    [InlineData("")]
    [InlineData("abc")]
    public void BothConversions_RefuseTheSameThings_AndEmitNothingWhenTheyDo(string typed)
    {
        // The two grammars are deliberately ONE grammar: an edge the net editor
        // would refuse must not be acceptable to the exclusion editor beside
        // it. Pinned as a pair so they cannot drift apart.
        Assert.False(HopNetDisplay.TryParseMhz(typed, out string kHz));
        Assert.Equal("", kHz);

        Assert.False(HopNetDisplay.TryParseMhzToHz(typed, out string hz));
        Assert.Equal("", hz);
    }

    // ---- Section 7: the net info view's second line -------------------------

    [Fact]
    public void InfoValueLine_NarrowbandHeadsTheCenter_WidebandTheEdges()
    {
        Assert.Equal(("Center (MHz)", "11.565"),
            HopNetDisplay.InfoValueLine(Net(HopType.Narrowband, center: "11565"), null));

        Assert.Equal(("Low–High (MHz)", "2.000–8.000"),
            HopNetDisplay.InfoValueLine(
                Net(HopType.Wideband, low: "02000", high: "08000"), null));
    }

    [Fact]
    public void InfoValueLine_ListCountsWhatTheMirrorHOLDS_AndDashesWhatItDoesNot()
    {
        Assert.Equal(("Frequencies", "3 stored"),
            HopNetDisplay.InfoValueLine(Net(HopType.List), ["11010", "11015", "11020"]));

        // Invariant 6: an unread list is UNKNOWN. "0 stored" would be the app
        // asserting something the radio never said.
        Assert.Equal(("Frequencies", "—"),
            HopNetDisplay.InfoValueLine(Net(HopType.List), null));

        // …and a list the radio answered as genuinely empty says so, because
        // the mirror really does hold that answer.
        Assert.Equal(("Frequencies", "0 stored"),
            HopNetDisplay.InfoValueLine(Net(HopType.List), []));
    }

    [Fact]
    public void InfoValueLine_HalfAReportedBand_IsNotHalfARenderedOne()
    {
        // The edges are a PAIR in the mirror; a consumer rendering "low-high"
        // must never get one of them.
        Assert.Equal(("Low–High (MHz)", "—"),
            HopNetDisplay.InfoValueLine(Net(HopType.Wideband, low: "02000"), null));
    }

    [Fact]
    public void InfoValueLine_NoConfirmedType_IsTheDoubleDash_AndSoIsAWipedNet()
    {
        Assert.Equal(("—", "—"), HopNetDisplay.InfoValueLine(null, null));
        Assert.Equal(("—", "—"), HopNetDisplay.InfoValueLine(Net(), null));

        // A REPORTED-unprogrammed net reports Hoptype WB (protocol.md), which
        // is a property of the WIPE, not a programmed band. Treating it as a
        // confirmed type would head the line "Low-High (MHz)" for a net that
        // has no band at all - the exact over-claim the round-4 Phase D fix
        // removed from the other display.
        Assert.Equal(("—", "—"),
            HopNetDisplay.InfoValueLine(
                Net(HopType.Wideband, id: null, unprogrammed: true), null));
    }
}
