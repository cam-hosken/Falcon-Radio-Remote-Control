using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 9 — the modem-preset vocabulary seam
/// (<see cref="ModemPresetVocabulary"/>): short tokens on the wire, words on
/// screen, radio-cased listing spellings that prefill a selection, and the
/// AGC precedent for a token that maps to nothing.
///
/// <para>The last test in this file is the one that matters most: it walks
/// EVERY wire token through the real Core builder, so the app's column and
/// Falcon.Core's independent copy of it cannot drift apart. Core cannot
/// reference the app layer, so that agreement has no compiler to enforce it —
/// only this.</para>
/// </summary>
public class ModemPresetVocabularyTests : SessionTestBase
{
    // ---- Column shape --------------------------------------------------------

    [Fact]
    public void EveryColumnIsTheHelpScreensValueSet()
    {
        // session-07's verbatim HELP MODEM capture, column by column.
        Assert.Equal(
            ["39TONE", "FSKW", "FSKN", "FSK-A", "FSK-V", "SE"],
            ModemPresetVocabulary.Types.Select(t => t.Wire));
        Assert.Equal(
            ["ASYNC REM", "ASYNC DAT", "SYNC DAT"],
            ModemPresetVocabulary.DataModes.Select(t => t.Wire));
        Assert.Equal(
            ["75", "150", "300", "600", "1200", "2400", "4800", "VO"],
            ModemPresetVocabulary.Bauds.Select(t => t.Wire));
        Assert.Equal(
            ["LO", "SH", "ALTS", "ALTL", "ZE"],
            ModemPresetVocabulary.Interleaves.Select(t => t.Wire));
        Assert.Equal(["EN", "DIS"], ModemPresetVocabulary.States.Select(t => t.Wire));
    }

    [Fact]
    public void EveryValueHasADisplayWord_AndTheAbbreviatedOnesAreSpelledOut()
    {
        // Ruling 1: human-readable words on screen. A display column that
        // silently reverted to the wire tokens would render "FSKW" at the
        // operator — this fails if any ABBREVIATED value stops being spelled
        // out. (FSK-A / FSK-V / the numeric bauds are their own display
        // words: the HELP token has nothing abbreviated to expand.)
        foreach (var v in ModemPresetVocabulary.Types
            .Concat(ModemPresetVocabulary.DataModes)
            .Concat(ModemPresetVocabulary.Bauds)
            .Concat(ModemPresetVocabulary.Interleaves)
            .Concat(ModemPresetVocabulary.States))
            Assert.False(string.IsNullOrWhiteSpace(v.Display));

        Assert.Equal("Long", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Interleaves, "LO"));
        Assert.Equal("Alt short", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Interleaves, "ALTS"));
        Assert.Equal("Zero", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Interleaves, "ZE"));
        Assert.Equal("Enabled", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.States, "EN"));
        Assert.Equal("Disabled", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.States, "DIS"));

        // The numeric bauds ARE their own display words; only VOice gets one.
        Assert.Equal("Voice", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Bauds, "VO"));
        Assert.Equal("2400", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Bauds, "2400"));
    }

    /// <summary>ROUND 11 §3 — the TYPE and PORT display words, byte-exact and
    /// as a CLOSED SET. Byte-exact because these are the words the owner chose
    /// and the width arithmetic was sized against; closed because a display
    /// rename is exactly the change that gets half-applied, leaving one button
    /// speaking round 10's vocabulary next to five speaking round 11's.</summary>
    [Fact]
    public void TheTypeAndPortWords_AreTheRoundElevenOnes_AndNothingElseSurvives()
    {
        Assert.Equal(
            ["39 tone", "FSK wide", "FSK narrow", "FSK ASCII", "FSK VFT", "Serial"],
            ModemPresetVocabulary.Types.Select(t => t.Display));
        Assert.Equal(
            ["Remote port (async)", "Data port (async)", "Data port (sync)"],
            ModemPresetVocabulary.DataModes.Select(t => t.Display));

        // The round-10 words are GONE — from the display column, and only from
        // it: every wire token and listing form on those same rows is
        // untouched (invariant 4), which the next two assertions state.
        foreach (var dead in new[]
        {
            "39-tone", "FSK-A", "FSK-V",
            "Async remote port", "Async data port", "Sync data port",
        })
            Assert.DoesNotContain(
                ModemPresetVocabulary.Types.Concat(ModemPresetVocabulary.DataModes),
                v => v.Display == dead);

        Assert.Equal(
            ["39TONE", "FSKW", "FSKN", "FSK-A", "FSK-V", "SE"],
            ModemPresetVocabulary.Types.Select(t => t.Wire));
        Assert.Equal("FSK-A", ModemPresetVocabulary.TypeFromListing("fsk-a"));
    }

    [Fact]
    public void DisplayOf_FallsBackToTheTokenItself_RatherThanInventingAWord()
    {
        // The H2 rule: an odd value renders as the radio wrote it.
        Assert.Equal("WHATEVER", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Types, "WHATEVER"));
        Assert.Equal("—", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.Types, null));
    }

    // ---- Listing → canonical -------------------------------------------------

    [Fact]
    public void TheVerifiedListingSpellingsMapToTheirWireTokens()
    {
        // The four forms session-15 actually captured.
        Assert.Equal("39TONE", ModemPresetVocabulary.TypeFromListing("39tone"));
        Assert.Equal("ASYNC DAT", ModemPresetVocabulary.DataModeFromListing("ASYNC DATA"));
        Assert.Equal("2400", ModemPresetVocabulary.BaudFromListing("2400"));
        // The round-8 note said "INTER long does not match any HELP token".
        // It does: "long" IS the LOng token spelled out.
        Assert.Equal("LO", ModemPresetVocabulary.InterleaveFromListing("long"));
    }

    [Fact]
    public void EveryColumnRoundTripsFromItsListingFormBackToItsWireToken()
    {
        foreach (var v in ModemPresetVocabulary.Types)
            foreach (var form in v.ListingForms)
                Assert.Equal(v.Wire, ModemPresetVocabulary.TypeFromListing(form));
        foreach (var v in ModemPresetVocabulary.DataModes)
            foreach (var form in v.ListingForms)
                Assert.Equal(v.Wire, ModemPresetVocabulary.DataModeFromListing(form));
        foreach (var v in ModemPresetVocabulary.Bauds)
            foreach (var form in v.ListingForms)
                Assert.Equal(v.Wire, ModemPresetVocabulary.BaudFromListing(form));
        foreach (var v in ModemPresetVocabulary.Interleaves)
            foreach (var form in v.ListingForms)
                Assert.Equal(v.Wire, ModemPresetVocabulary.InterleaveFromListing(form));
    }

    [Fact]
    public void ListingParsingIsCaseInsensitive_AndTrims()
    {
        // The radio's casing is its own business ("39tone", "INTER long").
        Assert.Equal("39TONE", ModemPresetVocabulary.TypeFromListing("39TONE"));
        Assert.Equal("SE", ModemPresetVocabulary.TypeFromListing("serial"));
        Assert.Equal("SE", ModemPresetVocabulary.TypeFromListing("  SeRiAl  "));
        Assert.Equal("ASYNC DAT", ModemPresetVocabulary.DataModeFromListing("async data"));
        Assert.Equal("VO", ModemPresetVocabulary.BaudFromListing("voice"));
        Assert.Equal("SH", ModemPresetVocabulary.InterleaveFromListing("SHORT"));
    }

    [Fact]
    public void AnUnmappedListingToken_YieldsNoCanonical_TheAgcPrecedent()
    {
        // A shape nobody has captured must not be guessed into a selection —
        // it leaves the row empty (which blocks Store) while the read-back
        // shows the radio's own text.
        Assert.Null(ModemPresetVocabulary.TypeFromListing("57tone"));
        Assert.Null(ModemPresetVocabulary.TypeFromListing(""));
        Assert.Null(ModemPresetVocabulary.TypeFromListing(null));
        Assert.Null(ModemPresetVocabulary.DataModeFromListing("ASYNC"));
        Assert.Null(ModemPresetVocabulary.BaudFromListing("1000"));
        Assert.Null(ModemPresetVocabulary.InterleaveFromListing("medium"));
    }

    [Fact]
    public void TheStateColumn_HasNoListingForm_AndNoListingReader_R13B1()
    {
        // REWRITTEN in round 13 B1 (plan §4 B1, owner ruling 2026-08-20). The
        // OLD pin asserted that StateFromListing("ENABLE"/"EN") returns null —
        // which was true for the vacuous reason that the column has no listing
        // forms at all, so the reader could not return anything else. The
        // reader is DELETED; what survives is the FACT it was reading, pinned
        // directly.
        //
        // The empty ListingForms are still the load-bearing claim: no capture
        // has ever echoed a preset's enabled state on a listing line (round 11
        // §6 — it comes from the bulk PRESENCE operation and nowhere else), so
        // a listing-driven prefill would be an invention. The editor prefills
        // from presence instead (ModemPresetsViewModelTests covers that end).
        Assert.All(ModemPresetVocabulary.States, s => Assert.Empty(s.ListingForms));
        Assert.Null(typeof(ModemPresetVocabulary).GetMethod("StateFromListing"));

        // Anti-vacuity, both directions. (a) The DISPLAY half is untouched —
        // the read-back cell and the segment buttons both render these words,
        // so an over-eager deletion of the whole column fails here.
        Assert.Equal("Enabled", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.States, "EN"));
        Assert.Equal("Disabled", ModemPresetVocabulary.DisplayOf(ModemPresetVocabulary.States, "DIS"));
        // (b) The sibling columns DO carry listing forms and DO keep their
        // readers — so "empty forms, no reader" is a fact about STATE alone,
        // not something true of every column in this class.
        Assert.NotNull(typeof(ModemPresetVocabulary).GetMethod("TypeFromListing"));
        Assert.Equal("39TONE", ModemPresetVocabulary.TypeFromListing("39tone"));
    }

    // ---- The type-switch map's two type sets --------------------------------

    [Fact]
    public void TheFskAndInterleaveTypeSets_AreDisjointAndCoverTheTypeColumn()
    {
        var fsk = ModemPresetVocabulary.FskTypeWires;
        var tone = ModemPresetVocabulary.InterleaveTypeWires;

        Assert.Equal(["FSKW", "FSKN", "FSK-A", "FSK-V"], fsk);
        Assert.Equal(["39TONE", "SE"], tone);        // ASSUMED — bench item A6d
        Assert.Empty(fsk.Intersect(tone));
        // Every type is in exactly one set: no type renders both rows, and
        // none renders neither.
        Assert.Equal(
            ModemPresetVocabulary.Types.Select(t => t.Wire).Order(),
            fsk.Concat(tone).Order());
    }

    // ==== ROUND 11 §6: the TYPE-SCOPED OFFERS ================================
    // Every rule here defends against a SILENT radio behaviour — a clamp, a
    // substitution, an ignore — that the echo reports as success. The pins are
    // therefore two-sided on purpose: what IS offered, and what is NOT.

    /// <summary>The per-type baud ceilings, exactly as the bench measured them
    /// on 2026-08-16. Stated as the full offered LIST rather than as a maximum,
    /// because "the wheel offers these" is what the operator meets.</summary>
    public static TheoryData<string, string[]> BaudOffers => new()
    {
        { "FSKN", ["75"] },                                                     // fskns  ≤ 75
        { "FSK-A", ["75", "150"] },                                             // fsk-a  ≤ 150
        { "FSKW", ["75", "150", "300"] },                                       // fskws  ≤ 300
        { "FSK-V", ["75", "150", "300", "600"] },                               // fsk-v  ≤ 600
        { "39TONE", ["75", "150", "300", "600", "1200", "2400", "VO"] },        // 75-2400 + Voice
        { "SE", ["75", "150", "300", "600", "1200", "2400", "4800"] },          // serial ≤ 4800
    };

    [Theory]
    [MemberData(nameof(BaudOffers))]
    public void BaudsFor_OffersExactlyWhatTheTypeStores(string type, string[] offered)
    {
        Assert.Equal(offered, ModemPresetVocabulary.BaudsFor(type).Select(v => v.Wire));

        // The other half: everything the type does NOT store is absent, and
        // absent is the only defence — the radio would take the write, clamp
        // it, and echo a success.
        foreach (var wire in ModemPresetVocabulary.Bauds.Select(v => v.Wire).Except(offered))
            Assert.DoesNotContain(ModemPresetVocabulary.BaudsFor(type), v => v.Wire == wire);
    }

    [Fact]
    public void VoiceBaud_IsOfferedAt39ToneOnly_AndIsTheWheelsLastStopThere()
    {
        // At fsk-a the same VO token is silently clamped to 150 (VERIFIED
        // 2026-08-16), so offering it anywhere else would be offering a lie.
        Assert.Contains(ModemPresetVocabulary.BaudsFor("39TONE"), v => v.Wire == "VO");
        Assert.Equal("VO", ModemPresetVocabulary.BaudsFor("39TONE")[^1].Wire);

        foreach (var type in new[] { "FSKW", "FSKN", "FSK-A", "FSK-V", "SE" })
            Assert.DoesNotContain(ModemPresetVocabulary.BaudsFor(type), v => v.Wire == "VO");
    }

    [Fact]
    public void BaudsFor_NoTypePicked_OffersTheWholeDiscreteSet()
    {
        // Nothing is known to be out of bounds until a type is picked, and a
        // type is REQUIRED before Store sends anything — so the wheel is not
        // dead in the interim, and the check is re-made at the sending surface.
        Assert.Equal(
            ModemPresetVocabulary.Bauds.Select(v => v.Wire),
            ModemPresetVocabulary.BaudsFor(null).Select(v => v.Wire));
        Assert.Equal(
            ModemPresetVocabulary.Bauds.Select(v => v.Wire),
            ModemPresetVocabulary.BaudsFor("NOT-A-TYPE").Select(v => v.Wire));
    }

    /// <summary>The interleave offer per type — and the two EMPTY cases, which
    /// are what HIDES the row.</summary>
    public static TheoryData<string, string, string[]> InterleaveOffers => new()
    {
        { "39TONE", "2400", ["LO", "SH", "ALTS", "ALTL"] },   // refuses ZE
        { "39TONE", "VO", ["LO", "SH", "ALTS", "ALTL"] },
        { "SE", "2400", ["LO", "SH", "ZE"] },                 // refuses ALTS/ALTL
        { "SE", "4800", [] },                                 // `uncoded` — row hides
        { "FSKW", "300", [] },
        { "FSKN", "75", [] },
        { "FSK-A", "150", [] },
        { "FSK-V", "600", [] },
    };

    [Theory]
    [MemberData(nameof(InterleaveOffers))]
    public void InterleavesFor_OffersOnlyWhatTheTypeAndBaudAccept(
        string type, string baud, string[] offered)
        => Assert.Equal(offered, ModemPresetVocabulary.InterleavesFor(type, baud).Select(v => v.Wire));

    [Fact]
    public void InterleavesFor_NoTypePicked_OffersNothing()
    {
        // Unlike baud, an EMPTY interleave offer is meaningful: it is how the
        // row hides. With no type there is no row.
        Assert.Empty(ModemPresetVocabulary.InterleavesFor(null, null));
        Assert.Empty(ModemPresetVocabulary.InterleavesFor("NOT-A-TYPE", "2400"));
    }

    [Fact]
    public void MarkSpace_IsOfferedAtFskVftOnly_WithTheCapturedBounds()
    {
        // Stored on every FSK type, DISPLAYED only at fsk-v — so everywhere
        // else the app could not verify what it wrote, and this card's whole
        // contract is that the read-back is the truth.
        Assert.True(ModemPresetVocabulary.OffersMarkSpace("FSK-V"));
        foreach (var type in new[] { "FSKW", "FSKN", "FSK-A", "39TONE", "SE" })
            Assert.False(ModemPresetVocabulary.OffersMarkSpace(type));
        Assert.False(ModemPresetVocabulary.OffersMarkSpace(null));

        // THE MEASURED WINDOW (2026-08-18; clone round 12 P2). The old
        // 500-3200 pair was INTERPOLATED from two probe values and was wrong at
        // BOTH ends; the sweep walked the edges one field at a time and found
        // 300 refused / 350 stored at the bottom and 3250 stored / 3290 refused
        // at the top. These constants are the captured accepted extremes, and
        // the units are Hz — no longer an inference.
        Assert.Equal(350, ModemPresetVocabulary.MarkSpaceMinimum);
        Assert.Equal(3250, ModemPresetVocabulary.MarkSpaceMaximum);

        // …and the direction of the correction is itself pinned: the window
        // WIDENED at both ends, so a client bound that quietly drifted back to
        // the interpolated pair — refusing values the radio accepts — fails
        // here rather than silently costing the operator two tones.
        Assert.True(ModemPresetVocabulary.MarkSpaceMinimum < 500);
        Assert.True(ModemPresetVocabulary.MarkSpaceMaximum > 3200);
    }

    [Fact]
    public void TheScopedOffers_AreSUBSETS_OfTheColumnsTheyScope()
    {
        // Anti-vacuity, and the invariant that keeps the Core builder in step:
        // a scoped offer can only ever REMOVE values. If one of these helpers
        // ever invented a token, the builder would throw at the operator's
        // press — and EveryVocabularyWireToken_IsAcceptedByTheCoreBuilder
        // (below) only walks the FULL columns.
        var bauds = ModemPresetVocabulary.Bauds.Select(v => v.Wire).ToHashSet();
        var interleaves = ModemPresetVocabulary.Interleaves.Select(v => v.Wire).ToHashSet();

        foreach (var type in ModemPresetVocabulary.Types.Select(t => t.Wire))
        {
            var offeredBauds = ModemPresetVocabulary.BaudsFor(type);
            Assert.NotEmpty(offeredBauds);
            Assert.All(offeredBauds, v => Assert.Contains(v.Wire, bauds));

            foreach (var baud in offeredBauds)
                Assert.All(ModemPresetVocabulary.InterleavesFor(type, baud.Wire),
                    v => Assert.Contains(v.Wire, interleaves));
        }
    }

    // ---- The seam: Core's independent wire lists agree with ours ------------

    [Fact]
    public void EveryVocabularyWireToken_IsAcceptedByTheCoreBuilder()
    {
        // Falcon.Core cannot reference Falcon.App.Core, so its copy of the
        // wire column is validated independently. This is the only thing that
        // keeps the two in step: a token added here and not there would
        // throw at the operator's press, and vice versa.
        ConnectReady();
        Transport.InjectLine("SSB>");        // the SSB band's builder needs its prompt (round 2)
        Transport.ClearSent();

        foreach (var type in ModemPresetVocabulary.Types)
            Radio.Ssb.ProgramModemPreset(1, "T39", type.Wire, "ASYNC DAT", "2400");
        foreach (var mode in ModemPresetVocabulary.DataModes)
            Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", mode.Wire, "2400");
        foreach (var baud in ModemPresetVocabulary.Bauds)
            Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", baud.Wire);
        foreach (var ilv in ModemPresetVocabulary.Interleaves)
            Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400", interleave: ilv.Wire);

        int expected = ModemPresetVocabulary.Types.Count
            + ModemPresetVocabulary.DataModes.Count
            + ModemPresetVocabulary.Bauds.Count
            + ModemPresetVocabulary.Interleaves.Count;
        Assert.Equal(expected, Transport.SentLines.Count);

        // …and the STATE column reaches the wire through the builder's bool.
        Transport.ClearSent();
        Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400", enabled: true);
        Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400", enabled: false);
        Assert.Equal(
        [
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400 " + ModemPresetVocabulary.States[0].Wire,
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400 " + ModemPresetVocabulary.States[1].Wire,
        ], Transport.SentLines);
    }

    /// <summary>
    /// CLONE-FIELD ROUND 2 F9/F11 — the same seam for the <c>HOP&gt;</c>
    /// columns. Core owns <see cref="Falcon.Core.Protocol.Wire.HopModemBauds"/>
    /// and the two mode enums; this vocabulary owns the display words. A value
    /// offered here that the builder refuses is a button that throws at the
    /// operator's press, and a value the builder takes that is never offered is
    /// a capability the card silently hides.
    /// </summary>
    [Fact]
    public void EveryHopVocabularyWireToken_IsAcceptedByTheCoreHopBuilder()
    {
        ConnectReady();
        Transport.InjectLine("HOP>");            // the builder is prompt-guarded
        Transport.ClearSent();

        foreach (var sync in ModemPresetVocabulary.SyncModes)
            foreach (var port in ModemPresetVocabulary.HopPorts)
                foreach (var baud in ModemPresetVocabulary.HopBauds)
                    Radio.Ssb.ProgramHopModemPreset(
                        9, "DAT9",
                        Wire.ParseSyncMode(sync.Wire)!.Value,
                        Wire.ParseDataMode(port.Wire)!.Value,
                        baud.Wire);

        Assert.Equal(
            ModemPresetVocabulary.SyncModes.Count
                * ModemPresetVocabulary.HopPorts.Count
                * ModemPresetVocabulary.HopBauds.Count,
            Transport.SentLines.Count);

        // The BAUD sets are the SAME set, value for value and in the same order
        // — the one number both layers must agree on, because the radio's
        // refusal of everything else is SILENT (P5c).
        Assert.Equal(Wire.HopModemBauds, ModemPresetVocabulary.HopBauds.Select(v => v.Wire));

        // …and the app's tokens really are the words the wire carries: the
        // two mode columns round-trip through Core's own parsers.
        Assert.Equal(
            ModemPresetVocabulary.SyncModes.Select(v => v.Wire),
            ModemPresetVocabulary.SyncModes.Select(v => Wire.ParseSyncMode(v.Wire)!.Value.ToWire()));
        Assert.Equal(
            ModemPresetVocabulary.HopPorts.Select(v => v.Wire),
            ModemPresetVocabulary.HopPorts.Select(v => Wire.ParseDataMode(v.Wire)!.Value.ToWire()));
    }

    // ---- The bench-captured LISTING spellings (2026-08-16) -------------------

    [Theory]
    // Every listing spelling the radio actually printed, in the radio's own
    // lowercase. These were the round-9 ASSUMED tier; matching is
    // case-insensitive, which is why the vocabulary entries survived unchanged.
    [InlineData("39tone", "39TONE")]
    [InlineData("fskws", "FSKW")]
    [InlineData("fskns", "FSKN")]
    [InlineData("fsk-a", "FSK-A")]
    [InlineData("fsk-v", "FSK-V")]
    [InlineData("serial", "SE")]
    public void TypeFromListing_MapsTheRadiosOwnLowercaseSpellings(string listing, string wire)
        => Assert.Equal(wire, ModemPresetVocabulary.TypeFromListing(listing));

    [Theory]
    [InlineData("long", "LO")]
    [InlineData("short", "SH")]
    [InlineData("alts", "ALTS")]
    [InlineData("altl", "ALTL")]
    [InlineData("zero", "ZE")]
    public void InterleaveFromListing_MapsTheCapturedSpellings(string listing, string wire)
        => Assert.Equal(wire, ModemPresetVocabulary.InterleaveFromListing(listing));

    [Fact]
    public void BaudFromListing_MapsTheMixedCaseVoiceSpelling()
    {
        // "BAUD Voice" is the one MIXED-CASE listing value on this radio; every
        // other is lowercase. Case-insensitive matching covers it, and this pin
        // is what fails if someone tightens the matcher to ordinal.
        Assert.Equal("VO", ModemPresetVocabulary.BaudFromListing("Voice"));
        Assert.Equal("2400", ModemPresetVocabulary.BaudFromListing("2400"));
    }

    [Fact]
    public void SyncDataPhrase_MapsDespiteTheRadiosDoubleSpace()
    {
        // The radio prints "SYNC  DATA" with two spaces (column padding). The
        // phrase reaches the vocabulary rebuilt from RemoveEmptyEntries tokens,
        // i.e. single-spaced — this pins that the single-space form is the one
        // the vocabulary must carry.
        Assert.Equal("SYNC DAT", ModemPresetVocabulary.DataModeFromListing("SYNC DATA"));
        Assert.Equal("ASYNC REM", ModemPresetVocabulary.DataModeFromListing("ASYNC REMOTE"));
    }

    [Fact]
    public void UncodedInterleave_DisplaysButHasNoWireToken()
    {
        // "uncoded" appeared unprompted on the bench (writing BAUD 4800 at the
        // serial type replaced a stored "zero" with it). It is in no HELP list
        // and nothing sends it — so it must render as a WORD without ever
        // becoming a selection the operator could Store.
        Assert.Equal("Uncoded", ModemPresetVocabulary.InterleaveDisplayFromListing("uncoded"));
        Assert.Null(ModemPresetVocabulary.InterleaveFromListing("uncoded"));
        Assert.DoesNotContain(ModemPresetVocabulary.Interleaves, v =>
            v.ListingForms.Any(f => string.Equals(f, "UNCODED", StringComparison.OrdinalIgnoreCase)));

        // The sendable values still round-trip through the display lookup.
        Assert.Equal("Long", ModemPresetVocabulary.InterleaveDisplayFromListing("long"));
        Assert.Null(ModemPresetVocabulary.InterleaveDisplayFromListing("nonsense"));
    }
}
