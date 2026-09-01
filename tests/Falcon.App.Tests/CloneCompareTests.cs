using Falcon.App.Core.Cloning;
using Falcon.Core.Protocol;

namespace Falcon.App.Tests;

/// <summary>
/// The CANONICAL COMPARISON RULES (plan round 11 §9A, FULL VERIFY). Each
/// fixture pins ONE rule and its opposite, because the whole value of the rule
/// set is that it distinguishes "the radio reordered something it is entitled
/// to reorder" from "the radio does not hold what the file says".
/// </summary>
public class CloneCompareTests
{
    private static CloneFile Read() => CloneFileTests.Complete();

    [Fact]
    public void AFileComparedWithItself_IsClean()
    {
        // Anti-vacuity for everything below: the baseline really matches.
        Assert.Empty(CloneCompare.Diff(Read(), Read()));
    }

    // ---- ORDER-SENSITIVE: selfs and per-net members --------------------------

    [Fact]
    public void SelfOrder_IsADifference_BecauseTheFirstSelfIsThePrimary()
    {
        var expected = Read();
        expected.Selfs.Add(new CloneAddress { Name = "TST", Group = 1 });
        var actual = Read();
        actual.Selfs.Insert(0, new CloneAddress { Name = "TST", Group = 1 });

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("Self addresses", diff, StringComparison.Ordinal);
        Assert.Contains("first self is the primary", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberOrder_IsADifference_BecauseMembersListInInsertionOrder()
    {
        var expected = Read();
        expected.Nets[0].Members = ["BOB", "CAM"];
        var actual = Read();
        actual.Nets[0].Members = ["CAM", "BOB"];

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("NET2 members", diff, StringComparison.Ordinal);
    }

    // ---- SET-COMPARE: channel groups and HOP list frequencies ---------------

    [Fact]
    public void ChannelGroupOrder_IsNotADifference_TheRadioSortsThem()
    {
        var expected = Read();
        expected.ChannelGroups.Add(new CloneChannelGroup { Group = 1, Channels = [5, 1, 3] });
        var actual = Read();
        actual.ChannelGroups.Add(new CloneChannelGroup { Group = 1, Channels = [1, 3, 5] });

        Assert.Empty(CloneCompare.Diff(expected, actual));
    }

    [Fact]
    public void AMissingChannelInAGroup_IsADifference()
    {
        var expected = Read();
        expected.ChannelGroups.Add(new CloneChannelGroup { Group = 1, Channels = [1, 3, 5] });
        var actual = Read();
        actual.ChannelGroups.Add(new CloneChannelGroup { Group = 1, Channels = [1, 3] });

        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("Channel group 1", StringComparison.Ordinal));
    }

    [Fact]
    public void HopListFrequencyOrder_IsNotADifference_ButAMissingOneIs()
    {
        var expected = Read();
        expected.HopNets.Add(new CloneHopNet
        { Number = 3, NetId = "1", Type = "LIST", ListFrequencies = ["11010", "10125"] });
        var actual = Read();
        actual.HopNets.Add(new CloneHopNet
        { Number = 3, NetId = "1", Type = "LIST", ListFrequencies = ["10125", "11010"] });
        Assert.Empty(CloneCompare.Diff(expected, actual));

        actual.HopNets[0].ListFrequencies = ["10125"];
        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("HOP net 3 frequencies", StringComparison.Ordinal));
    }

    // ---- KEYED: channels, presets, HOP nets, bands, messages ---------------

    [Fact]
    public void SsbChannels_AreKeyedAndExact_AndATargetOnlySlotIsADifference()
    {
        // R10 makes the channel domain verbatim, so an EXTRA slot on the radio
        // is a real difference — this is the residual the §14 default-store
        // probe exists to close, and it must surface rather than hide.
        //
        // D4 changed only how the difference is WORDED. An absent file row is
        // no longer "nothing"; it is `Wire.DefaultChannel`, which is what the
        // radio really holds in an unwritten slot — so the diff names both
        // values, like every other changed field.
        var expected = Read();
        var actual = Read();
        actual.Channels.Add(new CloneChannel
        {
            Number = 7, RxFrequency = "09000000", TxFrequency = "09000000",
            Mode = "AME", Agc = "ME", Bandwidth = "3.0", RxOnly = "NO",
        });

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Equal(
            "SSB channel 07: expected rx 01600000 tx 01600000 USB agc SL bw 2.7 receive-only NO, "
                + "the radio reports rx 09000000 tx 09000000 AME agc ME bw 3.0 receive-only NO.",
            diff);
    }

    /// <summary>
    /// D4's OTHER HALF, and the one that makes elision safe: a slot the file
    /// omits and the radio reports at the FACTORY DEFAULT matches SILENTLY —
    /// in both directions. Without this every clone of a mostly-default radio
    /// would verify with ~90 differences that are not differences.
    /// </summary>
    [Fact]
    public void AnAbsentSlot_MatchesTheFactoryDefaultRow_Silently_InEitherFile()
    {
        var expected = Read();
        var actual = Read();
        var d = Wire.DefaultChannel;
        // The radio reports slot 7 at the default; the file does not carry it.
        actual.Channels.Add(new CloneChannel
        {
            Number = 7, RxFrequency = d.RxFrequency, TxFrequency = d.TxFrequency,
            Mode = d.Mode, Agc = d.Agc, Bandwidth = d.Bandwidth, RxOnly = d.RxOnly,
        });
        // …and the mirror case: the FILE carries a default row (a legacy full
        // file) that the elided read-back does not.
        expected.Channels.Add(new CloneChannel
        {
            Number = 8, RxFrequency = d.RxFrequency, TxFrequency = d.TxFrequency,
            Mode = d.Mode, Agc = d.Agc, Bandwidth = d.Bandwidth, RxOnly = d.RxOnly,
        });

        Assert.Empty(CloneCompare.Diff(expected, actual));

        // ANTI-VACUITY: one field off the default on either side is still a
        // diff, so the silence above is about the DEFAULT and not about absence.
        actual.Channels.Single(c => c.Number == 7).RxOnly = "YES";
        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("SSB channel 07", diff, StringComparison.Ordinal);
        Assert.Contains("receive-only YES", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedChannelField_IsADifference_NamingBothValues()
    {
        var expected = Read();
        expected.Channels.Add(new CloneChannel
        { Number = 1, RxFrequency = "14313500", TxFrequency = "14313500", Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO" });
        var actual = Read();
        actual.Channels.Add(new CloneChannel
        { Number = 1, RxFrequency = "05000000", TxFrequency = "14313500", Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO" });

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("14313500", diff, StringComparison.Ordinal);
        Assert.Contains("05000000", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void ModemPresetPadding_IsNotADifference_ButAClampedValueIs()
    {
        // Column padding differs between a listing and an echo and carries no
        // meaning. A SILENTLY CLAMPED baud does carry meaning, and stays a
        // GENUINE difference — read-back is truth.
        var expected = Read();
        expected.ModemPresets.Add(new CloneModemPreset
        { Number = 1, Fields = "T39  ASYNC DATA   BAUD 2400  TYPE 39tone", Enabled = true });
        var actual = Read();
        actual.ModemPresets.Add(new CloneModemPreset
        { Number = 1, Fields = "T39 ASYNC DATA BAUD 2400 TYPE 39tone", Enabled = true });
        Assert.Empty(CloneCompare.Diff(expected, actual));

        actual.ModemPresets[0].Fields = "T39 ASYNC DATA BAUD 150 TYPE 39tone";
        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("Modem preset 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePresetEnabledFlag_IsPartOfTheComparison()
    {
        var expected = Read();
        expected.ModemPresets.Add(new CloneModemPreset { Number = 2, Fields = "DAT2", Enabled = false });
        var actual = Read();
        actual.ModemPresets.Add(new CloneModemPreset { Number = 2, Fields = "DAT2", Enabled = true });

        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("Modem preset 2", StringComparison.Ordinal));
    }

    [Fact]
    public void StoredMessages_AreKeyedBySlot_AndAnExtraSlotIsADifference()
    {
        // Leg 4 DELETES file-empty target slots, so this domain verifies to
        // EXACT equality — an extra slot means the delete did not happen.
        var expected = Read();
        var actual = Read();
        actual.Messages.Add(new CloneTxMessage { Slot = 9, Text = "SCRATCH" });

        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("Stored message 9", StringComparison.Ordinal));
    }

    [Fact]
    public void ExclusionBands_AreKeyedBySlot_WithExactEdges()
    {
        var expected = Read();
        expected.ExcludeBands.Add(new CloneExcludeBand { Band = 1, LowKHz = "11000", HighKHz = "11500" });
        var actual = Read();
        actual.ExcludeBands.Add(new CloneExcludeBand { Band = 1, LowKHz = "12000", HighKHz = "12500" });

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("Exclusion band 1", diff, StringComparison.Ordinal);
    }

    // ---- CLOCK-INDEPENDENT: LQA schedules -----------------------------------

    [Fact]
    public void ScheduleListOrder_IsIgnored_BecauseTheListingIsChronological()
    {
        var expected = Read();
        expected.Schedules.Add(new CloneSchedule { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });
        expected.Schedules.Add(new CloneSchedule { Kind = "EXCHANGE", Address = "BOB", Interval = "01:00", Start = "22:34" });
        var actual = Read();
        actual.Schedules.Add(new CloneSchedule { Kind = "EXCHANGE", Address = "BOB", Interval = "01:00", Start = "22:34" });
        actual.Schedules.Add(new CloneSchedule { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });

        Assert.Empty(CloneCompare.Diff(expected, actual));
    }

    [Fact]
    public void AScheduleStoredWithADifferentInterval_IsADifference()
    {
        var expected = Read();
        expected.Schedules.Add(new CloneSchedule { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });
        var actual = Read();
        actual.Schedules.Add(new CloneSchedule { Kind = "SOUND", Address = "CAM", Interval = "04:00", Start = "13:02" });

        Assert.Contains(CloneCompare.Diff(expected, actual),
            d => d.Contains("Schedule SOUND CAM", StringComparison.Ordinal));
    }

    // ---- The markers, the settings, the operating snapshot ------------------

    [Fact]
    public void AVerifyDomainThatDidNotComeBackRead_IsADifference()
    {
        var actual = Read();
        actual.HopNetState = CloneDomainState.Faulted;

        var diff = Assert.Single(CloneCompare.Diff(Read(), actual));
        Assert.Contains("HOP nets", diff, StringComparison.Ordinal);
        Assert.Contains("unverified", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingTheRadioDidNotTake_IsADifference()
    {
        var expected = Read();
        expected.Settings.Add(new CloneSetting { Key = "PowerLevel", Value = "High" });
        var actual = Read();
        actual.Settings.Add(new CloneSetting { Key = "PowerLevel", Value = "Low" });

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("Setting PowerLevel", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperatingSnapshot_IsCompared()
    {
        var actual = Read();
        actual.OperatingChannel = 5;
        actual.OperatingMode = "Hop";

        var diffs = CloneCompare.Diff(Read(), actual);
        Assert.Contains(diffs, d => d.Contains("Operating channel", StringComparison.Ordinal));
        Assert.Contains(diffs, d => d.Contains("Operating mode", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDiffLine_IsOperatorFacing_AndQuotesNoRadioToken()
    {
        // R13 across the whole comparison vocabulary.
        var actual = Read();
        actual.Selfs.Clear();
        actual.HopNetState = CloneDomainState.Faulted;
        actual.Messages.Add(new CloneTxMessage { Slot = 1, Text = "X" });

        var diffs = CloneCompare.Diff(Read(), actual);
        Assert.NotEmpty(diffs);
        foreach (var line in diffs)
        {
            Assert.DoesNotContain(" INV ", line, StringComparison.Ordinal);
            Assert.DoesNotContain("PRGMD", line, StringComparison.Ordinal);
            Assert.DoesNotContain("SLFAD", line, StringComparison.Ordinal);
        }
    }

    // ---- The LOCKOUT domain (clone round 12) --------------------------------

    [Fact]
    public void ALockoutStateThatDiffers_IsADifference_NamingTheKeyedRow()
    {
        var expected = Read();
        var actual = Read();
        actual.Lockouts!.Rows.First(r => r is { Family: "Program", Section: "Ssb", Item: "CHAN" })
            .State = "Unlock";

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("Lockout Program Ssb CHAN", diff, StringComparison.Ordinal);
        Assert.Contains("expected Lock", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void LockoutsAreKeyedOnFamilySectionAndItem_BecauseItemNamesREPEAT()
    {
        // The whole reason for the compound key: `KEY` exists in all three
        // SELECT sections. Moving ONE of them must be ONE diff naming THAT
        // section — a comparison keyed on the item alone would either miss it
        // or report three.
        var expected = Read();
        var actual = Read();
        actual.Lockouts!.Rows.First(r => r is { Family: "Select", Section: "Hop", Item: "KEY" })
            .State = "Lock";

        var diff = Assert.Single(CloneCompare.Diff(expected, actual));
        Assert.Contains("Lockout Select Hop KEY", diff, StringComparison.Ordinal);
        // Anti-vacuity: the other two same-named rows really are in the file.
        Assert.Equal(3, expected.Lockouts!.Rows.Count(r => r is { Family: "Select", Item: "KEY" }));
    }

    /// <summary>
    /// THE TARGET-ONLY-SURVIVOR RULE, DELETED (clone round 12) — pinned in its
    /// RETIRED direction. A row the radio holds and the file does not was once
    /// tolerated for the SSB channel domain, because nothing could remove it.
    /// The campaign now wipes first (owner statement §1) and the file carries
    /// every slot, so it is an ordinary DIFFERENCE in every domain, lockouts
    /// included.
    ///
    /// <para>D4 amends ONE domain's wording, not the rule: for SSB channels an
    /// absent file row means <c>Wire.DefaultChannel</c> rather than nothing, so
    /// the survivor surfaces as a value difference naming both sides. It still
    /// surfaces, which is the claim.</para>
    /// </summary>
    [Fact]
    public void ARowTheRadioHoldsAndTheFileDoesNot_IsADifference_InEveryDomain()
    {
        var expected = Read();
        var actual = Read();
        actual.Channels.Add(new CloneChannel
        {
            Number = 7, RxFrequency = "09000000", TxFrequency = "09000000",
            Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
        });
        actual.Messages.Add(new CloneTxMessage { Slot = 3, Text = "SURVIVOR" });
        actual.ExcludeBands.Add(new CloneExcludeBand { Band = 4, LowKHz = "1", HighKHz = "2" });

        var diffs = CloneCompare.Diff(expected, actual);
        Assert.Equal(3, diffs.Count);
        Assert.All(
            diffs.Where(d => !d.StartsWith("SSB channel", StringComparison.Ordinal)),
            d => Assert.Contains("the radio holds it and the file does not", d, StringComparison.Ordinal));
        var channel = Assert.Single(diffs, d => d.StartsWith("SSB channel", StringComparison.Ordinal));
        Assert.Contains("expected rx 01600000", channel, StringComparison.Ordinal);
        Assert.Contains("the radio reports rx 09000000", channel, StringComparison.Ordinal);
    }

    // ---- D3: the verify skips a domain the write never attempted -----------

    /// <summary>
    /// A domain whose WRITE LEG was abandoned for sentinel debt is not compared
    /// at all (plan-clone-write-structural.md §5.4): its per-row diffs and its
    /// read-state MARKER row both go, because the campaign says once, in its
    /// own words, that it did not attempt it. Everything else is compared
    /// exactly as before — the suppression is per domain, never global.
    /// </summary>
    [Fact]
    public void ADomainTheWriteNeverAttempted_IsNotComparedAtAll()
    {
        var expected = Read();
        var actual = Read();
        actual.Selfs.Clear();                       // the book never landed…
        actual.BookState = CloneDomainState.Faulted;
        expected.ChannelGroups.Add(                 // …nor the groups…
            new CloneChannelGroup { Group = 1, Channels = [5] });
        actual.Messages.Add(new CloneTxMessage { Slot = 3, Text = "SURVIVOR" });

        // ANTI-VACUITY: without the suppression all three are diffs.
        var everything = CloneCompare.Diff(expected, actual);
        Assert.Contains(everything, d => d.StartsWith("Self addresses", StringComparison.Ordinal));
        Assert.Contains(everything, d => d.StartsWith("Address book", StringComparison.Ordinal));
        Assert.Contains(everything, d => d.StartsWith("Channel group", StringComparison.Ordinal));

        var suppressed = CloneCompare.Diff(expected, actual,
            new HashSet<string>(StringComparer.Ordinal) { "address book", "channel groups" });

        Assert.DoesNotContain(suppressed, d => d.StartsWith("Self addresses", StringComparison.Ordinal));
        Assert.DoesNotContain(suppressed, d => d.StartsWith("Address book", StringComparison.Ordinal));
        Assert.DoesNotContain(suppressed, d => d.StartsWith("Channel group", StringComparison.Ordinal));
        // …and the domain nobody suppressed is still reported.
        Assert.Contains(suppressed, d => d.StartsWith("Stored message 3", StringComparison.Ordinal));
    }
}
