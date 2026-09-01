using System.Globalization;
using Falcon.Core.Protocol;

namespace Falcon.Core.Tests;

/// <summary>
/// The generated-switch wire mapping (no [Description] reflection — Android
/// AOT/trim, plan §2.3): every command form round-trips through its parse
/// counterpart, so a token can never be emitted that the parser would not
/// recognize.
/// </summary>
public class WireTests
{
    [Fact]
    public void PowerLevels_RoundTrip()
    {
        foreach (var v in Enum.GetValues<PowerLevel>())
            Assert.Equal(v, Wire.ParsePowerLevel(v.ToWire()));
    }

    [Fact]
    public void Modulations_RoundTrip()
    {
        foreach (var v in Enum.GetValues<ModulationMode>())
            Assert.Equal(v, Wire.ParseModulation(v.ToWire()));
    }

    [Fact]
    public void AgcSpeeds_RoundTrip()
    {
        foreach (var v in Enum.GetValues<AgcSpeed>())
            Assert.Equal(v, Wire.ParseAgcSpeed(v.ToWire()));
    }

    /// <summary>
    /// F5 (plan-clone-field-round2.md, decision D3) — the <c>DI</c> DUMP's AGC
    /// abbreviations, now the ONE mapping.
    ///
    /// <para>There used to be two: the channel editor's five-value prefix map,
    /// and a two-value <c>SL</c>/<c>ME</c> copy in <c>CloneService</c> that fell
    /// through to <see cref="Wire.ParseAgcSpeed"/> for everything else. The
    /// source radio's CH 09 stores <c>FA</c>, which the full-spelling parser
    /// does not know, so the field clone of 2026-08-21 reported the channel's
    /// AGC as a value the radio does not accept.</para>
    ///
    /// <para>All five values, plus the two-character prefix rule that lets the
    /// same reader take a full wire spelling — because only <c>SL</c> and
    /// <c>ME</c> have ever been CAPTURED in a dump and the rest are inferred
    /// from a unique prefix, never invented.</para>
    /// </summary>
    [Theory]
    [InlineData("OF", AgcSpeed.Off)]
    [InlineData("SL", AgcSpeed.Slow)]
    [InlineData("ME", AgcSpeed.Medium)]
    [InlineData("FA", AgcSpeed.Fast)]
    [InlineData("DA", AgcSpeed.Data)]
    [InlineData(" fa ", AgcSpeed.Fast)]          // trimmed and case-folded
    [InlineData("SLOW", AgcSpeed.Slow)]          // the full spellings share the prefix
    [InlineData("MEDIUM", AgcSpeed.Medium)]
    [InlineData("FAST", AgcSpeed.Fast)]
    public void DumpAgcAbbreviations_MapToTheEnum(string token, AgcSpeed expected)
        => Assert.Equal(expected, Wire.ParseDumpAgc(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("ZZ")]
    public void AnUnknownDumpAgcToken_IsNull_NeverGuessed(string? token)
        => Assert.Null(Wire.ParseDumpAgc(token));

    /// <summary>Every enum value is reachable from BOTH the dump abbreviation
    /// and the full wire spelling — so no value can quietly become unreadable
    /// the way <c>Fast</c> was.</summary>
    [Fact]
    public void EveryAgcSpeed_IsReachableFromItsDumpAbbreviationAndItsWireSpelling()
    {
        foreach (var v in Enum.GetValues<AgcSpeed>())
        {
            Assert.Equal(v, Wire.ParseDumpAgc(v.ToWire()));
            Assert.Equal(v, Wire.ParseDumpAgc(v.ToWire()[..2]));
        }
    }

    [Fact]
    public void FrequencySteps_RoundTrip()
    {
        foreach (var v in Enum.GetValues<FrequencyStep>())
            Assert.Equal(v, Wire.ParseFrequencyStep(v.ToWire()));
    }

    [Fact]
    public void OnOff_RoundTrips()
    {
        foreach (var v in Enum.GetValues<OnOff>())
            Assert.Equal(v, Wire.ParseOnOff(v.ToWire()));
    }

    [Fact]
    public void YesNo_RoundTrips()
    {
        foreach (var v in Enum.GetValues<YesNo>())
            Assert.Equal(v, Wire.ParseYesNo(v.ToWire()));
    }

    /// <summary>The per-modulation BW choice sets are the MEASURED matrix
    /// (probe R5) — notably FM {1.0..2.7}, wider than HELP's "(2.7)" — and
    /// every entry is a value the wire vocabulary already knows.</summary>
    [Fact]
    public void AllowedBandwidths_AreTheMeasuredR5Sets()
    {
        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"], Wire.AllowedBandwidths(ModulationMode.Usb));
        Assert.Equal(["1.5", "2.0", "2.4", "2.7", "3.0"], Wire.AllowedBandwidths(ModulationMode.Lsb));
        Assert.Equal(["3.0", "4.0", "5.0", "6.0"], Wire.AllowedBandwidths(ModulationMode.Ame));
        Assert.Equal(["0.35", "0.68", "1.0", "1.5"], Wire.AllowedBandwidths(ModulationMode.Cw));
        Assert.Equal(["1.0", "1.5", "2.0", "2.4", "2.7"], Wire.AllowedBandwidths(ModulationMode.Fm));

        foreach (var m in Enum.GetValues<ModulationMode>())
            foreach (var bw in Wire.AllowedBandwidths(m))
                Assert.Equal(bw, Wire.NormalizeBandwidth(bw));
    }

    [Fact]
    public void HopTypes_RoundTrip()
    {
        foreach (var v in Enum.GetValues<HopType>())
            Assert.Equal(v, Wire.ParseHopType(v.ToWire()));
    }

    /// <summary>EnabledDisabled deliberately does NOT round-trip: commands
    /// use the HELP minimum abbreviations (ENA/DIS — bench-accepted,
    /// protocol.md RWAS section) while answers use the full spellings.</summary>
    [Fact]
    public void EnabledDisabled_CommandAndReportForms()
    {
        Assert.Equal("ENA", EnabledDisabled.Enabled.ToWire());
        Assert.Equal("DIS", EnabledDisabled.Disabled.ToWire());
        Assert.Equal(EnabledDisabled.Enabled, Wire.ParseEnabledDisabled("ENABLED"));
        Assert.Equal(EnabledDisabled.Disabled, Wire.ParseEnabledDisabled("DISABLED"));
        Assert.Null(Wire.ParseEnabledDisabled("ENA"));   // answers never abbreviate
    }

    [Fact]
    public void PhaseRSettingVocabularies_AreTheDocumentedForms()
    {
        Assert.Equal("LO", SquelchLevel.Low.ToWire());          // the one bench-sent form
        Assert.Equal("MEDIUM", SquelchLevel.Medium.ToWire());
        Assert.Equal("HIGH", SquelchLevel.High.ToWire());
        Assert.Equal("NOISE", FmSquelchType.Noise.ToWire());
        Assert.Equal("TONE", FmSquelchType.Tone.ToWire());
        Assert.Equal("BNC", AntennaPort.Bnc.ToWire());
        Assert.Equal("AUTO", AntennaPort.Auto.ToWire());
        Assert.Equal("TUNED", AntennaPort.Tuned.ToWire());
        Assert.Equal("BYPASS", BypassEnable.Bypass.ToWire());
        Assert.Equal("ENABLE", BypassEnable.Enable.ToWire());
        Assert.Equal("OFF", BacklightFunction.Off.ToWire());
        Assert.Equal("MOMENTARY", BacklightFunction.Momentary.ToWire());
        Assert.Equal("SLOW", PrePostScanRate.Slow.ToWire());
        Assert.Equal("FAST", PrePostScanRate.Fast.ToWire());
        Assert.Equal(["5.0", "6.5", "8.0"], Wire.FmDeviationValues);
    }

    [Fact]
    public void ModeCommands_AreTheDocumentedAbbreviations()
    {
        Assert.Equal("SS", OperatingMode.Ssb.ToCommand());
        Assert.Equal("ALE", OperatingMode.Ale.ToCommand());
        Assert.Equal("HO", OperatingMode.Hop.ToCommand());
    }

    [Fact]
    public void UnknownWireStrings_ParseAsNull_NeverThrow()
    {
        Assert.Null(Wire.ParsePowerLevel("WTF"));
        Assert.Null(Wire.ParseModulation(""));
        Assert.Null(Wire.ParseAgcSpeed("MEDIUM"));
        Assert.Null(Wire.NormalizeBandwidth("7.5"));
    }

    // ---- Clone round 12 §9 B4: the SQ_LEVEL REPORT vocabulary ---------------

    /// <summary>The three captured report spellings map to the enum (r12-p2,
    /// 2026-08-19 — docs/protocol.md "SQ_LEVEL's three report spellings").
    /// The point of the helper is that TWO of the three differ from the SET
    /// token, so the pin asserts the DIFFERENCE explicitly rather than only
    /// the mapping: a "simplification" that routed the reader back through
    /// ToWire would pass a mapping-only test on HIGH and silently restore the
    /// defect on the other two.</summary>
    [Fact]
    public void SquelchLevelFromReport_MapsTheThreeCapturedSpellings()
    {
        Assert.Equal(SquelchLevel.Low, Wire.SquelchLevelFromReport("LOW"));
        Assert.Equal(SquelchLevel.Medium, Wire.SquelchLevelFromReport("MED"));
        Assert.Equal(SquelchLevel.High, Wire.SquelchLevelFromReport("HIGH"));

        Assert.NotEqual(SquelchLevel.Low.ToWire(), "LOW");
        Assert.NotEqual(SquelchLevel.Medium.ToWire(), "MED");
        Assert.Equal(SquelchLevel.High.ToWire(), "HIGH");   // the one coincidence
    }

    /// <summary>TRY-PARSE, not a total function (critic-12b F11): anything
    /// outside the three captured spellings is null, INCLUDING the app's own
    /// SET tokens — a report the radio never emitted must never be believed.
    /// Null is what makes all three highlights false.</summary>
    [Fact]
    public void SquelchLevelFromReport_ReturnsNull_ForEverythingElse()
    {
        Assert.Null(Wire.SquelchLevelFromReport("LO"));       // the SET token
        Assert.Null(Wire.SquelchLevelFromReport("MEDIUM"));   // the SET token
        Assert.Null(Wire.SquelchLevelFromReport("low"));      // casing is the radio's
        Assert.Null(Wire.SquelchLevelFromReport(""));
        Assert.Null(Wire.SquelchLevelFromReport("NOT INSTALLED"));
    }

    [Fact]
    public void BandwidthNormalization_AcceptsBothSubOneSpellings()
    {
        Assert.Equal("0.35", Wire.NormalizeBandwidth(".35"));
        Assert.Equal("0.35", Wire.NormalizeBandwidth("0.35"));
        Assert.Equal("2.7", Wire.NormalizeBandwidth(" 2.7 "));
    }

    /// <summary>
    /// THE FACTORY-DEFAULT STORED CHANNEL (plan-clone-write-structural.md D4),
    /// byte-pinned in the <c>DI</c> DUMP'S OWN SPELLINGS. It is the row a
    /// never-written slot prints and the row a ZEROIZE puts every slot back to
    /// (protocol.md, "There is no 'unprogrammed channel' shape"; the
    /// 2026-08-18 zeroize capture answered <c>DI 50 50</c> with exactly this on
    /// a freshly wiped radio), which is what lets the clone campaign skip it on
    /// both sides.
    ///
    /// <para>The ABBREVIATIONS are the point: the dump prints <c>SL</c>, not
    /// <c>SLOW</c>, and a constant carrying the full spelling would match no
    /// stored row at all.</para>
    /// </summary>
    [Fact]
    public void TheDefaultStoredChannel_IsTheDumpsOwnSpellings()
    {
        var d = Wire.DefaultChannel;
        Assert.Equal("01600000", d.RxFrequency);
        Assert.Equal("01600000", d.TxFrequency);
        Assert.Equal("USB", d.Mode);
        Assert.Equal("SL", d.Agc);          // the dump's abbreviation, not SLOW
        Assert.Equal("2.7", d.Bandwidth);
        Assert.Equal("NO", d.RxOnly);

        // The two frequencies are the radio's own LOWER BOUND, so the default
        // row is a value the radio would accept back — the same number the
        // acceptance window is built on, not a second copy of it.
        Assert.Equal(Wire.MinFrequencyHz.ToString("D8", CultureInfo.InvariantCulture), d.RxFrequency);
    }
}
