using Falcon.App.Core.Surfaces;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The X8 additions to the ALE surface (plan-ale-programming.md §4.3
/// projections clause): the group table, the refusal record, the three read
/// completions — and the ONE deliberate omission from the Changed-raising
/// watched set, pinned as a decision rather than left looking like a gap
/// (audit round 1, MAJOR 5 + owner ruling).
/// </summary>
public sealed class AleSurfaceTests : SessionTestBase
{
    private readonly AleSurface _ale;

    public AleSurfaceTests() => _ale = new AleSurface(Radio);

    [Fact]
    public void TheSurface_ProjectsTheGroupTable_TheRefusal_AndTheThreeCompletions()
    {
        ConnectReady();

        Assert.Equal(10, _ale.ChannelGroups.Count);
        Assert.All(_ale.ChannelGroups, g => Assert.Null(g.Channels));
        Assert.Equal(default, _ale.ProgrammingRefusal);
        Assert.Equal(default, _ale.LastBookRead);
        Assert.Equal(default, _ale.LastGroupRead);
        Assert.Equal(default, _ale.LastSync);

        long book = _ale.RequestStationBook();
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(book, true), _ale.LastBookRead);
        Assert.Equal(["ZZZ"], _ale.SelfAddresses.Select(a => a.Address));

        long group = _ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(group, true), _ale.LastGroupRead);
        Assert.Equal([0, 1], _ale.ChannelGroups[1].Channels);

        long sync = Radio.Ale.Synchronize();
        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(sync, true), _ale.LastSync);

        Transport.InjectLine(" ADDRESS EXISTS ");
        Assert.Equal("ADDRESS EXISTS", _ale.ProgrammingRefusal.Line);
        Assert.Equal(1, _ale.ProgrammingRefusal.Sequence);
    }

    [Fact]
    public void ABarrierCompletion_RaisesNoChanged_WhileAGroupCommitDoes()
    {
        // The deliberate omission (owner ruling): AleSync is READABLE through
        // LastSync but is NOT in the watched set — every programming
        // operation fires at least two barriers, and a card re-rendering on
        // each would be storming on an event carrying nothing it displays.
        // The gate, the one consumer that needs barrier completions, listens
        // to the radio's own StateChanged instead.
        ConnectReady();
        int changed = 0;
        _ale.Changed += (_, _) => changed++;

        long sync = Radio.Ale.Synchronize();
        AnswerSentinel();

        Assert.Equal(new AleReadCompletion(sync, true), _ale.LastSync);   // it DID complete…
        Assert.Equal(0, changed);                                         // …and raised nothing

        // Anti-vacuity: the surface's Changed wiring is otherwise live — a
        // group-read commit (an X8 property that IS watched) raises it.
        Radio.Ale.RequestChannelGroup(1);
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 ");
        AnswerSentinel();
        Assert.True(changed > 0, "a channel-group commit must raise Changed");
    }

    // ---- Round 11 §8: the two new read stores at the surface --------------

    [Fact]
    public void TheSurface_WrapsTheMembershipRead_AndProjectsItsMirrorAndCompletion()
    {
        ConnectReady();
        Assert.Empty(_ale.NetMembers);                 // unread: not "no members"
        Assert.Equal(default, _ale.LastMemberRead);

        long read = _ale.RequestNetMembers("N1");
        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("NETAD N1                CHGROUP 01   ASSOC SELF S1");
        Transport.InjectLine("     MEMBER 01  I2");
        AnswerSentinel();

        Assert.Equal(new AleReadCompletion(read, true), _ale.LastMemberRead);
        Assert.Equal(["I2"], _ale.NetMembers["N1"].Select(m => m.Address));
    }

    [Fact]
    public void TheSurface_WrapsTheScheduleRead_AndProjectsItsMirrorAndCompletion()
    {
        ConnectReady();
        Assert.Null(_ale.LqaSchedules);                // unread: not "none queued"
        Assert.Equal(default, _ale.LastScheduleRead);

        long read = _ale.RequestLqaSchedules();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("EXCHANGE I1              INTERVAL 01:00 START TIME 22:34");
        AnswerSentinel();

        Assert.Equal(new AleReadCompletion(read, true), _ale.LastScheduleRead);
        var row = Assert.Single(_ale.LqaSchedules!);
        Assert.Equal(new LqaSchedule(LqaScheduleKind.Exchange, "I1", "01:00", "22:34"), row);
    }

    // ---- The broadcast round (P20/P20b): the two widened intents and the
    // ONE channel source both Phase B pickers read -------------------------

    [Fact]
    public void TheWidenedCallAndSendAmd_CarryTheChannel_AndTheOldFormsAreUnchanged()
    {
        // plan-ale-broadcast-round.md §2: the channel rides the EXISTING
        // argument slots of `CAL`/`SE` — no new sender, ANY/ALL are ordinary
        // addresses. The channel-LESS overloads must keep compiling and
        // sending exactly what they always did (the book targets' path).
        ConnectReady();

        _ale.Call("ANY", "12");
        _ale.Call("ALL");
        _ale.Call("BOB");                              // the pre-round form
        Assert.Equal(["CAL ANY 12", "CAL ALL", "CAL BOB"], Transport.SentLines);

        Transport.ClearSent();
        bool? outcome = null;
        _ale.SendAmd("HI ALL", "ANY", "12", (ok, _) => outcome = ok);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("HI ALL");
        AnswerSentinel();
        Assert.Equal("SE 9 ANY 12", Transport.SentLines[^1]);
        Assert.True(outcome);

        Transport.ClearSent();
        _ale.SendAmd("HI BOB", "BOB", (_, _) => { });   // the three-argument overload
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("HI BOB");
        AnswerSentinel();
        Assert.Equal("SE 9 BOB", Transport.SentLines[^1]);
    }

    [Fact]
    public void BroadcastChannels_IsTheDistinctSortedUnionOfTheREPORTEDGroups()
    {
        // The ONE source both Phase B pickers consume, so the pinned ANY/ALL
        // rows and the compose picker cannot drift (plan §2). Mirror-honest:
        // an UNREAD group table offers nothing — owner ruling 4 forbids
        // offering the raw 0-99 range, and an ANY call with no channel is what
        // the radio refuses ` NO CHANS IN GRP ` (P20).
        ConnectReady();
        Assert.Empty(_ale.BroadcastChannels);          // never read ≠ no channels

        _ale.RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 02 CHANS 12 05 ");   // out of order on the wire
        Transport.InjectLine("CHGROUP 03 CHANS 05 29 ");   // 05 is in BOTH groups
        AnswerSentinel();

        // Distinct, numeric order, two-digit — the wire's own spelling.
        Assert.Equal(["05", "12", "29"], _ale.BroadcastChannels);

        // Anti-vacuity, and the three-state distinction the union rests on:
        // the eight groups the radio did not answer for are CONFIRMED EMPTY
        // after a whole-table read (an empty group answers nothing at all —
        // protocol.md `CHG`), so they are `[]`, not null, and they contribute
        // nothing either way. The NULL case — never read — is the assertion
        // at the top of this test, where the union is empty entirely.
        Assert.Equal(8, _ale.ChannelGroups.Count(g => g.Channels is { Count: 0 }));
    }

    [Fact]
    public void GroupTableFullyRead_NeedsALLTENSlots_NotMerelyANonEmptyUnion()
    {
        // AUDIT ROUND 1, MAJOR 1. The selection-lifetime rule (plan §3) prunes
        // only on a CONFIRMED-read mirror, and the union cannot express that: a
        // non-empty union proves only that SOME group answered. The three
        // states are walked in order on ONE fixture — never read, PARTIALLY
        // read, wholly read — because the middle one is the state the defect
        // lived in and the one a "is the union empty?" test cannot see.
        ConnectReady();
        Assert.False(_ale.GroupTableFullyRead);        // ten nulls: never read
        Assert.Empty(_ale.BroadcastChannels);

        // A TARGETED single-group read: group 0 answers, groups 1-9 stay null.
        _ale.RequestChannelGroup(0);
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.Equal(["05"], _ale.BroadcastChannels);  // the union is NON-EMPTY…
        Assert.False(_ale.GroupTableFullyRead);        // …and the table is still partial
        Assert.Equal(9, _ale.ChannelGroups.Count(g => g.Channels is null));

        // The whole-table read closes the remaining nine as confirmed-empty.
        _ale.RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.True(_ale.GroupTableFullyRead);
        Assert.DoesNotContain(_ale.ChannelGroups, g => g.Channels is null);

        // …and a reconnect blanks it back to "never read", which is what makes
        // the predicate a SESSION fact rather than a latch.
        Session.Close();
        ConnectReady();
        Assert.False(_ale.GroupTableFullyRead);
    }

    [Fact]
    public void TheSurface_RaisesChanged_ForBothNewStores()
    {
        // Completion surfaces as a store change notification — the seam the
        // LQA tab and the member section render from.
        ConnectReady();

        int changed = 0;
        _ale.Changed += (_, _) => changed++;
        _ale.RequestNetMembers("N1");
        Transport.InjectLine("     MEMBER 01  I2");
        AnswerSentinel();
        Assert.True(changed > 0, "a membership commit must raise Changed");

        changed = 0;
        _ale.RequestLqaSchedules();
        Transport.InjectLine("EXCHANGE I1              INTERVAL 01:00 START TIME 22:34");
        AnswerSentinel();
        Assert.True(changed > 0, "a schedule commit must raise Changed");
    }
}

/// <summary>
/// Round 11 §8 at the MODEM surface: the targeted field read, the seven-read
/// batch, the presence operation, and the two mirrors they feed.
/// </summary>
public sealed class ModemSurfaceRound11Tests : SessionTestBase
{
    private readonly ModemSurface _modem;

    public ModemSurfaceRound11Tests() => _modem = new ModemSurface(Radio);

    /// <summary>AUDIT ROUND 2 (clone-field round 2, MAJOR 1) — connected AND
    /// at the SSB prompt. A modem preset read now REFUSES while the mode is
    /// unconfirmed, because which presets exist is a fact about the prompt;
    /// every pin here already MEANT the 0-6 band (its fixture lines are in
    /// it).</summary>
    private new void ConnectReady()
    {
        base.ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
    }

    private const string Preset1 =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long";

    [Fact]
    public void TheSurface_WrapsAllThreeReads_WithTheirWireForms()
    {
        ConnectReady();

        _modem.QueryPreset(2);
        Assert.Equal(["MODEM PRE 2", "BAT ST"], Transport.SentLines);
        AnswerSentinel();

        Transport.ClearSent();
        _modem.RefreshPresetFields();
        Assert.Equal(
            ["MODEM PRE 0", "MODEM PRE 1", "MODEM PRE 2", "MODEM PRE 3",
             "MODEM PRE 4", "MODEM PRE 5", "MODEM PRE 6", "BAT ST"],
            Transport.SentLines);
        AnswerSentinel();

        Transport.ClearSent();
        _modem.QueryPresetPresence();
        Assert.Equal(["MODEM PRE", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void TheSurface_ProjectsThePresenceStore_AndRaisesChangedOnItsCommit()
    {
        ConnectReady();
        Assert.Equal(Falcon.Core.Radio.RadioState.PresenceState.Unknown, _modem.PresetPresence.State);

        int changed = 0;
        _modem.Changed += (_, _) => changed++;

        long read = _modem.QueryPresetPresence();
        Transport.InjectLine(Preset1);
        AnswerSentinel();

        Assert.Equal(Falcon.Core.Radio.RadioState.PresenceState.Completed, _modem.PresetPresence.State);
        Assert.Equal([1], _modem.PresetPresence.Enabled);
        Assert.Equal(new AleReadCompletion(read, true), _modem.LastPresetRead);
        Assert.True(changed > 0, "a presence commit must raise Changed");

        // …and the FIELDS mirror is not touched by the presence operation at
        // all (§8 is operation-wide): fields have ONE provenance, the targeted
        // read. A bulk row contributes its NUMBER and nothing else.
        Assert.Empty(_modem.Presets);
    }
}
