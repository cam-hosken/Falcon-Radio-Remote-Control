using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// UI-tweaks round 11 §8 — the FOUR new sentinel-scoped read stores and the
/// mirrors they publish: per-net MEMBERSHIP, the LQA SCHEDULE queue, the WB
/// EXCLUSION bands, and the modem preset PRESENCE set.
///
/// <para>All four are built on the round-10 read-store pattern already proven
/// by the book and channel-group queues (payload command + ONE closing
/// <c>BAT ST</c>, active/pending slots, atomic commit on the sentinel, prior
/// state kept when it is not answered) — <b>not</b> on the
/// <c>AleProgrammingGate</c> double-sentinel WRITE bracket, which is
/// writes-only and untouched by this round. These pins say so contract by
/// contract.</para>
///
/// <para>Doctrine: the transport NEVER answers, and every sentinel is answered
/// by injecting the captured BATTERY line. Fixture lines are VERBATIM captures
/// except where a constant is explicitly named DERIVED below — see the two
/// blocks, which name what they are patterned on and the §14 probe that will
/// settle them (audit round 1, MAJOR 6: the blanket "every line is verbatim"
/// claim this file used to make was FALSE).</para>
/// </summary>
public class Round11ReadStoreTests : RadioTestBase
{
    /// <summary>
    /// AUDIT ROUND 2 (clone-field round 2, MAJOR 1) — <b>connected AND at the
    /// SSB prompt.</b> A modem preset command now REFUSES while the mode is
    /// unconfirmed, because which presets exist is a fact about the prompt and
    /// the app may not guess it. Every modem pin in this file already MEANT
    /// "at SSB" — that is the band all of its fixture lines are in — so the
    /// prompt is confirmed once here rather than at two dozen call sites.
    /// (The prompt line itself sends nothing; it only confirms the mirror.)
    /// </summary>
    private new void ConnectReady()
    {
        base.ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
    }

    // ---- VERBATIM captures ----------------------------------------------

    // bench/transcripts/phase1-ale-membership-20260817.
    private const string NetRecord = "NETAD N1                CHGROUP 01   ASSOC SELF S1";
    private const string Member01 = "     MEMBER 01  I2";
    private const string Member02 = "     MEMBER 02  I1";
    private const string NoMembers = " NO MEMBERS PRGMD ";

    // bench/transcripts/phase2b-schedules-20260817.
    private const string SoundRow = "SOUND    S1              INTERVAL 03:00 START TIME 13:02";
    private const string ExchangeRow = "EXCHANGE I1              INTERVAL 01:00 START TIME 22:34";
    private const string NoSchedules = " NO LQA SCHEDULED ";

    // bench/transcripts/phase3-hop-channel-20260817 — the ONE captured
    // exclusion row (a single-band table).
    private const string ExcludeRow0 = "Exclude 00  02000   03000 ";

    // bench/transcripts/phase4-modem-20260817 (preset 2's found state), with
    // the preset number substituted; "PRESET" is stripped by the parser before
    // the line reaches the mirror.
    private static string PresetLine(int n) =>
        $"MODEM PRESET {n} T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ";

    // ---- DERIVED fixtures (NOT captured — say so) ------------------------

    /// <summary>
    /// A SECOND exclusion row. <b>DERIVED, not captured:</b> the bench has only
    /// ever read a table holding ONE band, so the multi-row layout is patterned
    /// on <see cref="ExcludeRow0"/>'s columns — same widths, different values.
    /// Round 11 §14 owns the probe that settles it ("the populated multi-band
    /// bulk `EXC` listing shape"); if the real listing differs, the parser will
    /// ignore the unknown shape (which the anchored-and-sized regex guarantees)
    /// and this fixture is what changes.
    /// </summary>
    private const string DerivedExcludeRow1 = "Exclude 01  11000   11500 ";

    /// <summary>Let a sentinel TIME OUT rather than answering it — the
    /// swallowed-listing quirk, which every store must survive by keeping what
    /// it already knew.</summary>
    private static void WaitForTimeout() => Thread.Sleep(300);

    // ====================================================================
    // A. MEMBERSHIP — the KEYED store
    // ====================================================================

    [Fact]
    public void Membership_ThreeStates_UnreadThenReadEmptyThenRows()
    {
        ConnectReady();

        // 1. UNREAD: the key is simply absent. Not an empty list — a net
        //    nobody has read must never render as "no members".
        Assert.False(Radio.State.Ale.NetMembers.ContainsKey("N1"));

        // 2. READ-EMPTY: the radio's own NO MEMBERS PRGMD marker.
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(NetRecord);
        Transport.InjectLine(NoMembers);
        AnswerSentinel();
        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N1"));
        Assert.Empty(Radio.State.Ale.NetMembers["N1"]);

        // 3. ROWS.
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(NetRecord);
        Transport.InjectLine(Member01);
        Transport.InjectLine(Member02);
        AnswerSentinel();
        Assert.Equal(["I2", "I1"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));
    }

    [Fact]
    public void Membership_KeepsTheRadiosINSERTIONOrder_NotAlphabetical()
    {
        // Member order is INSERTION order (2026-08-17) and the numbering is
        // the radio's own. Sorting the rows anywhere would be inventing an
        // order the radio does not have.
        ConnectReady();
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(NetRecord);
        Transport.InjectLine(Member01);      // 01  I2
        Transport.InjectLine(Member02);      // 02  I1
        AnswerSentinel();

        Assert.Equal(
            [(1, "I2"), (2, "I1")],
            Radio.State.Ale.NetMembers["N1"].Select(m => (m.Number, m.Address)));
    }

    [Fact]
    public void Membership_CommitIsAtomic_MidReadThePublishedRowsAreStillThePriorOnes()
    {
        ConnectReady();
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(Member01);
        AnswerSentinel();
        Assert.Equal(["I2"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));

        // A second read, with DIFFERENT rows accumulating: until its sentinel
        // answers the published list is EXACTLY the prior one — never a
        // mixture, never a half-list.
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine("     MEMBER 01  LOW");
        Transport.InjectLine("     MEMBER 02  M01");
        Assert.Equal(["I2"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));

        AnswerSentinel();
        Assert.Equal(["LOW", "M01"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));
    }

    [Fact]
    public void Membership_FaultPreservesPrior_EvenWithPartialRowsAlreadyIn()
    {
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(Member01);
        AnswerSentinel();
        Assert.Equal(["I2"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));

        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine("     MEMBER 01  LOW");
        WaitForTimeout();                      // the sentinel is swallowed

        // The partial accumulation is discarded WHOLE. A half-read membership
        // matches nothing, and publishing it would be a lie with rows in it.
        Assert.Equal(["I2"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));
        Assert.False(Radio.State.Ale.LastMemberRead.Answered);
    }

    [Fact]
    public void Membership_OneReadOnTheWire_TheRestUnionIntoThePendingNameSet()
    {
        ConnectReady();

        long first = Radio.Ale.ReadNetMembers("N1");
        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);

        // Requests arriving mid-flight send NOTHING and share ONE pending
        // operation id; a repeated name unions away.
        long pendingA = Radio.Ale.ReadNetMembers("N2");
        long pendingB = Radio.Ale.ReadNetMembers("N3");
        long pendingC = Radio.Ale.ReadNetMembers("N2");
        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);
        Assert.NotEqual(first, pendingA);
        Assert.Equal(pendingA, pendingB);
        Assert.Equal(pendingA, pendingC);

        // The pending names dispatch ONE AT A TIME, each after the previous
        // commit — never two membership reads on the wire at once.
        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["NETAD N2", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["NETAD N3", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Empty(Transport.SentLines);          // the set is drained
    }

    [Fact]
    public void Membership_APendingOperationIsAbandonedAcrossASilence()
    {
        // The round-10 rule, unchanged: a pending operation may only be
        // promoted across an operation the radio ANSWERED. After a silence the
        // dead read's rows may still be in flight and nothing distinguishes
        // them from the next one's.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        Radio.Ale.ReadNetMembers("N1");
        long pending = Radio.Ale.ReadNetMembers("N2");
        Transport.ClearSent();

        WaitForTimeout();
        Assert.Empty(Transport.SentLines);                       // nothing promoted
        Assert.Equal(pending, Radio.State.Ale.LastMemberRead.ReadId);
        Assert.False(Radio.State.Ale.LastMemberRead.Answered);   // …and its requester was told
    }

    [Fact]
    public void MemberLine_OutsideAReadOperation_IsIgnored()
    {
        // A MEMBER continuation names no net, so with no read in flight there
        // is no honest attribution — unlike a CHGROUP line, which carries its
        // own slot and may take the unsolicited-upsert path.
        ConnectReady();
        Transport.InjectLine(NetRecord);
        Transport.InjectLine(Member01);

        Assert.Empty(Radio.State.Ale.NetMembers);
    }

    // ====================================================================
    // B. LQA SCHEDULES — the single-slot store
    // ====================================================================

    [Fact]
    public void Schedules_ThreeStates_UnreadThenReadEmptyThenRows()
    {
        ConnectReady();
        Assert.Null(Radio.State.Ale.LqaSchedules);              // 1. unread

        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(NoSchedules);
        AnswerSentinel();
        Assert.Empty(Radio.State.Ale.LqaSchedules!);            // 2. read-empty

        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(SoundRow);
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();
        Assert.Equal(2, Radio.State.Ale.LqaSchedules!.Count);   // 3. rows
    }

    [Fact]
    public void Schedules_KeepTheRadiosOrder_AndEveryReportedField()
    {
        // The listing is CHRONOLOGICAL by next start time; the mirror renders
        // it in the radio's order and carries kind + address + interval +
        // start exactly as printed (intervals are NOT radio-validated, so the
        // stored text is the only truth).
        ConnectReady();
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(SoundRow);
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();

        var rows = Radio.State.Ale.LqaSchedules!;
        Assert.Equal(new LqaSchedule(LqaScheduleKind.Sound, "S1", "03:00", "13:02"), rows[0]);
        Assert.Equal(new LqaSchedule(LqaScheduleKind.Exchange, "I1", "01:00", "22:34"), rows[1]);
    }

    [Fact]
    public void Schedules_AnLqaInProgress_WritesNoRow_EvenMidRead()
    {
        // ROUND 15 item I: the EXCHANGE token now carries TWO shapes, and the
        // schedule mirror is NEVER written by the progress one. The dangerous
        // instant is mid-read, where every EXCHANGE line is being accumulated
        // as a listing row — a bare `EXCH STA` fires on the operator's press
        // and its first progress line can land inside the re-read it triggers.
        ConnectReady();
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(ExchangeRow);
        Transport.InjectLine("EXCHANGE KC1HAS           CHANNEL: 30");   // P14b, verbatim
        Transport.InjectLine("SOUNDING W6HOS            CHANNEL: 30");   // P14c, verbatim
        AnswerSentinel();

        var row = Assert.Single(Radio.State.Ale.LqaSchedules!);
        Assert.Equal(new LqaSchedule(LqaScheduleKind.Exchange, "I1", "01:00", "22:34"), row);

        // …and the lines were not silently dropped either: they did what they
        // ARE, which is move the link state.
        Assert.Equal(AleLinkState.Sounding, Radio.State.Ale.LinkState.Value);
        Assert.Equal("W6HOS", Radio.State.Ale.LqaStation);
    }

    [Fact]
    public void Schedules_CommitIsAtomic_AndAFaultPreservesPrior()
    {
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();
        Assert.Single(Radio.State.Ale.LqaSchedules!);

        // Mid-read, with new rows accumulating: still exactly the old list.
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(SoundRow);
        Transport.InjectLine("EXCHANGE N1              INTERVAL 03:00 START TIME 21:00");
        Assert.Single(Radio.State.Ale.LqaSchedules!);
        Assert.Equal("I1", Radio.State.Ale.LqaSchedules![0].Address);

        WaitForTimeout();                       // swallowed sentinel
        Assert.Single(Radio.State.Ale.LqaSchedules!);
        Assert.Equal("I1", Radio.State.Ale.LqaSchedules![0].Address);
        Assert.False(Radio.State.Ale.LastScheduleRead.Answered);
    }

    [Fact]
    public void Schedules_OneReadOnTheWire_TheRestCoalesceIntoOnePendingOperation()
    {
        ConnectReady();

        long first = Radio.Ale.ReadLqaSchedules();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);

        long pendingA = Radio.Ale.ReadLqaSchedules();
        long pendingB = Radio.Ale.ReadLqaSchedules();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);   // nothing new sent
        Assert.NotEqual(first, pendingA);
        Assert.Equal(pendingA, pendingB);                        // one pending operation

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);   // …promoted after the commit
    }

    // ====================================================================
    // C. INVALIDATION — what a write does to the two ALE mirrors
    // ====================================================================

    /// <summary>Read N1 and N2's membership and the schedule queue, so every
    /// invalidation pin starts from three POPULATED mirrors.</summary>
    private void LoadBothNetsAndTheSchedules()
    {
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(Member01);
        AnswerSentinel();
        Radio.Ale.ReadNetMembers("N2");
        Transport.InjectLine("     MEMBER 01  I3");
        AnswerSentinel();
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();

        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N1"));
        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N2"));
        Assert.NotNull(Radio.State.Ale.LqaSchedules);
    }

    [Fact]
    public void AddingAMember_InvalidatesThatNetAlone()
    {
        ConnectReady();
        LoadBothNetsAndTheSchedules();

        Radio.Ale.AddNetMember("n1", "BOB");     // case-insensitive, like the radio

        Assert.False(Radio.State.Ale.NetMembers.ContainsKey("N1"));   // unread again
        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N2"));    // untouched
        Assert.NotNull(Radio.State.Ale.LqaSchedules);                 // untouched
    }

    [Fact]
    public void WritingANet_InvalidatesThatNetAlone()
    {
        ConnectReady();
        LoadBothNetsAndTheSchedules();

        Radio.Ale.SetNetAddress("N2", 1, "S1");

        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N1"));
        Assert.False(Radio.State.Ale.NetMembers.ContainsKey("N2"));
        Assert.NotNull(Radio.State.Ale.LqaSchedules);
    }

    [Fact]
    public void DeletingAnAddress_InvalidatesEVERYNetsMembership_AndTheScheduleQueue()
    {
        // DELAD is GLOBAL: the address leaves every net's member list (proven
        // on a two-net member) and its queued schedule goes with it. Nothing
        // on the wire says WHICH nets, so every one goes unread.
        ConnectReady();
        LoadBothNetsAndTheSchedules();

        Radio.Ale.DeleteAddress("I2");

        Assert.Empty(Radio.State.Ale.NetMembers);
        Assert.Null(Radio.State.Ale.LqaSchedules);
    }

    [Fact]
    public void Erase_InvalidatesBothMirrors()
    {
        // ERASE clears addresses, membership AND schedules (channel groups and
        // stored messages survive).
        ConnectReady();
        LoadBothNetsAndTheSchedules();

        Radio.Ale.EraseAllAddresses();

        Assert.Empty(Radio.State.Ale.NetMembers);
        Assert.Null(Radio.State.Ale.LqaSchedules);
    }

    [Fact]
    public void ChannelGroupWrites_DoNotTouchTheRound11Mirrors()
    {
        // The stay-out half: invalidation is a CLOSED list. A channel-group
        // edit cannot change membership or a schedule, so it must not blank
        // either mirror (which would cost a needless read and blink the
        // display through its third state).
        ConnectReady();
        LoadBothNetsAndTheSchedules();

        Radio.Ale.AddScanChannel(1, 5);
        Radio.Ale.DeleteScanChannel(1, 5);

        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N1"));
        Assert.True(Radio.State.Ale.NetMembers.ContainsKey("N2"));
        Assert.NotNull(Radio.State.Ale.LqaSchedules);
    }

    // ====================================================================
    // D. The BOOK-ORDER pin (§5's primary-self derivation rests on it)
    // ====================================================================

    [Fact]
    public void BookRefresh_PreservesTheRadiosListingOrderPerKind()
    {
        // SLFAD listing order IS creation order, so the FIRST self row is the
        // primary. Any sorting here would silently re-point the address book's
        // PRIMARY tag at the wrong station.
        ConnectReady();
        Radio.Ale.RefreshStationList();
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD ABC               CHGROUP 01");
        Transport.InjectLine("SLFAD MID               CHGROUP 02");
        Transport.InjectLine("INDAD ZED               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NZ                CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NA                CHGROUP 01   ASSOC SELF ZZZ");
        AnswerSentinel();

        Assert.Equal(["ZZZ", "ABC", "MID"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(["ZED", "AAA"], Radio.State.Ale.IndividualAddresses.Select(a => a.Address));
        Assert.Equal(["NZ", "NA"], Radio.State.Ale.NetAddresses.Select(a => a.Address));
    }

    [Fact]
    public void ATargetedMembershipRead_CannotCorruptTheBooksORDER()
    {
        // AUDIT ROUND 1, MAJOR 5 — the concurrent case, by construction.
        // Both operations emit `NETAD` RECORD lines and a record says nothing
        // about which asked for it, so two independent queues would let a
        // targeted read's record land in a book refresh's accumulator and
        // reorder the book — and §5 derives the PRIMARY self from listing
        // index 0. The two now share ONE queue: the membership read cannot
        // even start while the book read owns the wire.
        ConnectReady();

        Radio.Ale.RefreshStationList();
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        Radio.Ale.ReadNetMembers("N1");
        Assert.Empty(Transport.SentLines);          // it WAITS — nothing on the wire

        // The book's own listing arrives, N1 then N2, and commits in that
        // order. No targeted record could have jumped the queue into it.
        Transport.InjectLine("NETAD N1                CHGROUP 01   ASSOC SELF S1");
        Transport.InjectLine("NETAD N2                CHGROUP 01   ASSOC SELF S1");
        AnswerSentinel();
        Assert.Equal(["N1", "N2"], Radio.State.Ale.NetAddresses.Select(a => a.Address));

        // …and only NOW does the membership read go out.
        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);
        Transport.InjectLine("NETAD N1                CHGROUP 01   ASSOC SELF S1");
        Transport.InjectLine(Member01);
        AnswerSentinel();

        // The targeted record did not re-order the book (a naive re-append
        // would have made it N2, N1) and the membership committed.
        Assert.Equal(["N1", "N2"], Radio.State.Ale.NetAddresses.Select(a => a.Address));
        Assert.Equal(["I2"], Radio.State.Ale.NetMembers["N1"].Select(m => m.Address));
    }

    [Fact]
    public void ARepeatedAddressRecord_UpdatesItsFieldsAndKEEPSItsListingPosition()
    {
        // The other half of MAJOR 5, and the one §5 leans on hardest: listing
        // order IS creation order, so the FIRST self row is the PRIMARY. Any
        // re-report — a targeted read's record, a programming echo, an
        // unsolicited line — must update fields in place. Remove-then-append
        // silently re-pointed the primary tag at whoever spoke last.
        ConnectReady();
        Radio.Ale.RefreshStationList();
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        AnswerSentinel();
        Assert.Equal(["ZZZ", "TST"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));

        // ZZZ speaks again with a DIFFERENT channel group.
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 05");

        Assert.Equal(["ZZZ", "TST"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(5, Radio.State.Ale.SelfAddresses[0].ChannelGroup);   // fields DID update
    }

    [Fact]
    public void AnEchoArrivingBeforeTheListingRows_CannotFixItsPositionInTheAccumulation()
    {
        // AUDIT ROUND 2, MAJOR-A — the other side of the order rule, and the
        // path round 1's in-place-everywhere fix REGRESSED.
        //
        // Inside an accumulation the LISTING is the authority on order. A line
        // reaching the accumulator BEFORE the listing burst (a write echo, an
        // unsolicited re-report) is indistinguishable from a listing row on the
        // wire, so it cannot be filtered — but it must not FIX a position the
        // listing has not given yet. With in-place-everywhere the echo for an
        // existing SECONDARY self claimed index 0 and the commit published
        // SECONDARY, PRIMARY: the address book would have tagged the SECONDARY
        // as the primary self.
        ConnectReady();
        Radio.Ale.RefreshStationList();
        Transport.InjectLine("SLFAD PRIMARY           CHGROUP 01");
        Transport.InjectLine("SLFAD SECONDARY         CHGROUP 02");
        AnswerSentinel();
        Assert.Equal(["PRIMARY", "SECONDARY"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));

        // A second refresh. The echo for the EXISTING secondary arrives FIRST,
        // ahead of the listing burst.
        Radio.Ale.RefreshStationList();
        Transport.InjectLine("SLFAD SECONDARY         CHGROUP 07");   // the echo
        Transport.InjectLine("SLFAD PRIMARY           CHGROUP 01");   // …then the listing
        Transport.InjectLine("SLFAD SECONDARY         CHGROUP 07");
        AnswerSentinel();

        // The committed order is the LISTING's own order, not the echo's.
        Assert.Equal(["PRIMARY", "SECONDARY"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(7, Radio.State.Ale.SelfAddresses[1].ChannelGroup);   // fields still landed
    }

    [Fact]
    public void ABookRefreshRequestedDuringAMembershipRead_WaitsAndThenRuns()
    {
        // The mutual exclusion in the other direction, and the promotion order
        // (BOOK first — it is the coarser answer, and membership is read per
        // net once the nets are known).
        ConnectReady();
        Radio.Ale.ReadNetMembers("N1");
        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        Radio.Ale.RefreshStationList();
        Radio.Ale.ReadNetMembers("N2");
        Assert.Empty(Transport.SentLines);          // both wait

        AnswerSentinel();                            // N1's membership commits
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();                            // the book commits
        Assert.Equal(["NETAD N2", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ANetadSilence_CompletesBothPendingKinds()
    {
        // The BLOCKER-1 rule applied to the shared NETAD queue: a silence
        // abandons the pending book refresh AND the pending membership set,
        // and each requester is told rather than left waiting.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        Radio.Ale.ReadNetMembers("N1");             // active
        long pendingBook = Radio.Ale.RefreshStationList();
        long pendingMember = Radio.Ale.ReadNetMembers("N2");
        Assert.NotEqual(pendingBook, pendingMember);

        WaitForTimeout();

        Assert.Equal(pendingBook, Radio.State.Ale.LastBookRead.ReadId);
        Assert.False(Radio.State.Ale.LastBookRead.Answered);
        Assert.Equal(pendingMember, Radio.State.Ale.LastMemberRead.ReadId);
        Assert.False(Radio.State.Ale.LastMemberRead.Answered);
    }

    // ---- The COMPLETION ORDER (round 16 fixes S5, decision F-10) ---------
    // `CompleteNetMembersRead` used to raise `AleMemberRead` and THEN take the
    // lock to promote. Every Core event is delivered INLINE when no
    // SynchronizationContext was captured (and under these tests' InlineContext),
    // so a `ReadNetMembers` issued from INSIDE that handler ran while the silent
    // operation was still ACTIVE: it coalesced into the pending slot and the
    // very next statement abandoned it. A retry could therefore only ever reach
    // the wire under MAUI's queued context — behaviour that differs by context
    // cannot be pinned, and the App's member retry is built on this.
    //
    // The member path now RELEASES the queue before it RAISES. The BOOK path is
    // untouched.

    [Fact]
    public void AMemberRequestFromInsideAnUNANSWEREDCompletion_ReachesTheWire()
    {
        // Pin (i). This is the retry's whole seam: the handler of the silence
        // asks again, and the ask must DISPATCH rather than coalesce into the
        // operation the silence is in the middle of abandoning.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        Radio.Ale.ReadNetMembers("N1");
        long activeId = Radio.State.Ale.LastMemberRead.ReadId;   // 0 — nothing completed yet
        Transport.ClearSent();

        long retryId = 0;
        var lockObject = new object();
        Radio.State.Changed += p =>
        {
            if (p != RadioProperty.AleMemberRead) return;
            lock (lockObject)
            {
                if (retryId != 0) return;                        // once, like the VM's retry
                if (Radio.State.Ale.LastMemberRead.Answered) return;
                retryId = Radio.Ale.ReadNetMembers("N1");
            }
        };

        WaitForTimeout();

        Assert.Equal(["NETAD N1", "BAT ST"], Transport.SentLines);
        lock (lockObject)
        {
            Assert.NotEqual(0, retryId);
            Assert.NotEqual(activeId, retryId);                  // a FRESH active operation
        }
    }

    [Fact]
    public void AMemberRequestFromInsideAnANSWEREDCompletion_CoalescesBehindThePromotedBook()
    {
        // Pin (ii). The other side of the same seam, and the rule the reorder
        // must NOT break: across an ANSWER the queue promotes BOOK BEFORE
        // MEMBERS, so a request made from inside the completion finds the book
        // already owning the queue and waits behind it.
        ConnectReady();

        Radio.Ale.ReadNetMembers("N1");
        Radio.Ale.RefreshStationList();          // pending book
        Transport.ClearSent();

        long retryId = 0;
        Radio.State.Changed += p =>
        {
            if (p != RadioProperty.AleMemberRead || retryId != 0) return;
            retryId = Radio.Ale.ReadNetMembers("N2");
        };

        AnswerSentinel();                        // N1 commits; the book promotes

        Assert.NotEqual(0, retryId);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();                        // the book commits
        Assert.Equal(["NETAD N2", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ANetadSilence_RaisesTheActiveCompletionBeforeTheAbandonedOnes()
    {
        // Pin (iii). The VISIBLE sequence is unchanged by the reorder — which
        // is the point of pinning it: releasing the queue earlier must not
        // re-order what a consumer sees, or the App's own completion
        // bookkeeping (which id was whose) would shift under it.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;

        long activeId = Radio.Ale.ReadNetMembers("N1");
        long pendingBook = Radio.Ale.RefreshStationList();
        long pendingMember = Radio.Ale.ReadNetMembers("N2");

        var seen = new List<(RadioProperty Property, long ReadId)>();
        Radio.State.Changed += p =>
        {
            lock (seen)
            {
                if (p == RadioProperty.AleMemberRead)
                    seen.Add((p, Radio.State.Ale.LastMemberRead.ReadId));
                else if (p == RadioProperty.AleBookRead)
                    seen.Add((p, Radio.State.Ale.LastBookRead.ReadId));
            }
        };

        WaitForTimeout();

        lock (seen)
            Assert.Equal(
                [(RadioProperty.AleMemberRead, activeId),
                 (RadioProperty.AleBookRead, pendingBook),
                 (RadioProperty.AleMemberRead, pendingMember)],
                seen);
    }

    // ====================================================================
    // E. WB EXCLUSION BANDS — the answers-NOTHING trap, solved by the sentinel
    // ====================================================================

    [Fact]
    public void ExcludeBands_ThreeStates_ReadEmptyIsNoRowsBeforeAnAnsweredSentinel()
    {
        ConnectReady();
        Assert.Null(Radio.State.Hop.ExcludeBands);              // 1. unread

        // 2. READ-EMPTY. THE trap: an empty table answers NOTHING AT ALL, so
        //    only the answered sentinel separates this from a swallowed query.
        Radio.Hop.QueryExcludeBands();
        AnswerSentinel();
        Assert.Empty(Radio.State.Hop.ExcludeBands!);

        // 3. ROWS.
        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        Transport.InjectLine(DerivedExcludeRow1);
        AnswerSentinel();
        Assert.Equal([0, 1], Radio.State.Hop.ExcludeBands!.Select(b => b.Band));
    }

    [Fact]
    public void ExcludeBands_CommitIsAtomic_AndAFaultPreservesPrior()
    {
        ConnectReady();
        Radio.Hop.ReadTimeoutMs = 80;

        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        AnswerSentinel();
        Assert.Single(Radio.State.Hop.ExcludeBands!);

        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        Transport.InjectLine(DerivedExcludeRow1);
        Assert.Single(Radio.State.Hop.ExcludeBands!);        // still the prior table

        WaitForTimeout();
        Assert.Single(Radio.State.Hop.ExcludeBands!);        // …and the fault keeps it
        Assert.False(Radio.State.Hop.LastExcludeRead.Answered);
    }

    [Fact]
    public void ExcludeBands_OneReadOnTheWire_TheRestCoalesce()
    {
        ConnectReady();

        long first = Radio.Hop.QueryExcludeBands();
        Assert.Equal(["EXC", "BAT ST"], Transport.SentLines);

        long pendingA = Radio.Hop.QueryExcludeBands();
        long pendingB = Radio.Hop.QueryExcludeBands();
        Assert.Equal(["EXC", "BAT ST"], Transport.SentLines);
        Assert.NotEqual(first, pendingA);
        Assert.Equal(pendingA, pendingB);

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["EXC", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ExcludeBands_ASetEchoOutsideAReadUpsertsItsOwnSlot()
    {
        // The set ECHO names its own band, so with no read in flight it is the
        // radio's latest word about that slot — the standalone-line doctrine.
        ConnectReady();
        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        AnswerSentinel();

        Radio.Hop.SetExcludeBand(1, "11000000", "11500000");
        Transport.InjectLine(DerivedExcludeRow1);

        Assert.Equal([0, 1], Radio.State.Hop.ExcludeBands!.Select(b => b.Band));
    }

    // ====================================================================
    // F. MODEM PRESETS — the fields mirror, the presence store, the ONE queue
    // ====================================================================

    [Fact]
    public void PresetFields_UpsertByNumber_AndNoReadEverClearsThem()
    {
        // The defect this replaces: the old bulk read CLEARED this collection,
        // so a preset the bulk omits (i.e. a DISABLED one) lost its fields the
        // moment anything else was read.
        ConnectReady();
        Radio.Ssb.QueryModemPreset(2);
        Transport.InjectLine(PresetLine(2));
        AnswerSentinel();
        Assert.Single(Radio.State.ModemPresets);

        Radio.Ssb.QueryModemPreset(1);
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();
        Assert.Equal(2, Radio.State.ModemPresets.Count);            // preset 2 survived
        Assert.StartsWith("2 ", Radio.State.ModemPresets[0], StringComparison.Ordinal);
        Assert.StartsWith("1 ", Radio.State.ModemPresets[1], StringComparison.Ordinal);

        // A re-read of one preset REPLACES its row rather than appending.
        Radio.Ssb.QueryModemPreset(2);
        Transport.InjectLine("MODEM PRESET 2 SER  ASYNC DATA   BAUD 4800  TYPE serial  INTER uncoded");
        AnswerSentinel();
        Assert.Equal(2, Radio.State.ModemPresets.Count);
        Assert.Contains("serial", Radio.State.ModemPresets[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePresenceOperation_NeverTouchesTheFieldsMirror_NotEvenOnArrival()
    {
        // §8 is OPERATION-WIDE, not merely commit-wide (audit round 1,
        // BLOCKER 2). A bulk row contributes its NUMBER to the enabled set and
        // NOTHING to the fields mirror: fields have exactly one provenance,
        // the targeted read, whose window is unambiguous by construction.
        ConnectReady();
        Radio.Ssb.QueryModemPreset(2);
        Transport.InjectLine(PresetLine(2));
        AnswerSentinel();
        var before = Radio.State.ModemPresets;
        Assert.Single(before);

        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        Assert.Equal(before, Radio.State.ModemPresets);        // …not on arrival
        AnswerSentinel();
        Assert.Equal(before, Radio.State.ModemPresets);        // …nor on the commit
        Assert.Equal([1], Radio.State.ModemPresetPresence.Enabled);
    }

    [Fact]
    public void APresenceListingWhoseSentinelIsSwallowed_LeavesNoMixedTimeFields()
    {
        // The scenario BLOCKER 2 named: a bulk listing part-arrives and its
        // sentinel never lands. Nothing about it may reach the fields mirror,
        // or the display would carry a partial bulk snapshot mixed in beside
        // older targeted rows with nothing marking which came from when.
        ConnectReady();
        Radio.Ssb.ModemReadTimeoutMs = 80;
        Radio.Ssb.QueryModemPreset(2);
        Transport.InjectLine(PresetLine(2));
        AnswerSentinel();
        var before = Radio.State.ModemPresets;

        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        Transport.InjectLine(PresetLine(4));
        WaitForTimeout();

        Assert.Equal(before, Radio.State.ModemPresets);
        Assert.Equal(RadioState.PresenceState.Unknown, Radio.State.ModemPresetPresence.State);
    }

    [Fact]
    public void Presence_GoingInFlight_RaisesItsChangeNotification()
    {
        // Audit round 1, MAJOR 4: the InFlight transition used to assign the
        // state silently, so a display that had rendered Enabled/Disabled kept
        // rendering it through a window in which the app could no longer vouch
        // for either. Exactly ONE notification per transition.
        ConnectReady();
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);

        int raised = 0;
        Radio.State.Changed += p => { if (p == RadioProperty.ModemPresetPresence) raised++; };

        Radio.Ssb.QueryModemPresetPresence();
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Presence_PromotedFromTheQueue_AlsoRaisesItsChangeNotification()
    {
        // AUDIT ROUND 2, MAJOR-B. The round-1 fix covered only the IDLE-queue
        // request. A presence read that was QUEUED behind a targeted operation
        // opens exactly the same window when the targeted sentinel promotes it,
        // and owed exactly the same notification — without it every consumer
        // kept rendering the previous Enabled/Disabled for the whole window.
        ConnectReady();
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);

        Radio.Ssb.QueryModemPreset(3);                 // targeted read owns the queue
        int raised = 0;
        Radio.State.Changed += p => { if (p == RadioProperty.ModemPresetPresence) raised++; };

        Radio.Ssb.QueryModemPresetPresence();          // QUEUED — not dispatched, no window yet
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        Assert.Equal(0, raised);

        AnswerSentinel();                              // the targeted sentinel promotes it

        Assert.True(Radio.State.IsModemPresenceReadActive);
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);
        Assert.Equal(1, raised);                       // exactly one, on the promotion
    }

    [Fact]
    public void AModemSilence_CompletesEVERYPendingOperation_NotJustOne()
    {
        // Audit round 1, BLOCKER 1. This queue can hold a pending TARGETED
        // batch AND a pending PRESENCE read at once; a silence clears both, so
        // both requesters must be told. Reporting only one left the other
        // waiting for a completion that could never arrive.
        ConnectReady();
        Radio.Ssb.ModemReadTimeoutMs = 80;

        Radio.Ssb.QueryModemPreset(0);                  // active
        long pendingPresence = Radio.Ssb.QueryModemPresetPresence();
        long pendingTargeted = Radio.Ssb.QueryModemPreset(5);
        Assert.NotEqual(pendingPresence, pendingTargeted);

        var completed = new List<AleReadCompletion>();
        Radio.State.Changed += p =>
        {
            if (p == RadioProperty.ModemPresetRead)
                lock (completed) completed.Add(Radio.State.LastModemRead);
        };

        WaitForTimeout();

        lock (completed)
        {
            Assert.Contains(completed, c => c.ReadId == pendingTargeted && !c.Answered);
            Assert.Contains(completed, c => c.ReadId == pendingPresence && !c.Answered);
        }
    }

    [Fact]
    public void Presence_ThreeStates_UnknownThenInFlightThenCompleted()
    {
        ConnectReady();
        Assert.Equal(RadioState.PresenceState.Unknown, Radio.State.ModemPresetPresence.State);
        Assert.Empty(Radio.State.ModemPresetPresence.Enabled);

        Radio.Ssb.QueryModemPresetPresence();
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);

        Transport.InjectLine(PresetLine(1));
        Transport.InjectLine(PresetLine(4));
        // Still IN-FLIGHT until the sentinel: an enabled set built from a
        // half-arrived listing would call every late preset disabled.
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);

        AnswerSentinel();
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        Assert.Equal([1, 4], Radio.State.ModemPresetPresence.Enabled);
    }

    [Fact]
    public void Presence_CommitIsAtomicReplace_AndAFaultPreservesPrior()
    {
        ConnectReady();
        Radio.Ssb.ModemReadTimeoutMs = 80;

        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        Transport.InjectLine(PresetLine(4));
        AnswerSentinel();
        Assert.Equal([1, 4], Radio.State.ModemPresetPresence.Enabled);

        // A second read whose sentinel is swallowed, with a DIFFERENT partial
        // listing already in: the committed set must not move at all — least
        // of all shrink, which would render presets "Disabled" on a silence.
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(6));
        WaitForTimeout();

        Assert.Equal([1, 4], Radio.State.ModemPresetPresence.Enabled);
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        Assert.False(Radio.State.LastModemRead.Answered);
    }

    [Fact]
    public void ModemQueue_PresenceNeverDispatchesWhileATargetedReadIsActive()
    {
        // Targeted and bulk answers share an IDENTICAL line shape, so the two
        // windows must never overlap. THIS is what makes the enabled set
        // attributable at all.
        ConnectReady();
        Radio.Ssb.RefreshModemPresets();
        Assert.True(Radio.State.IsModemTargetedReadActive);

        Radio.Ssb.QueryModemPresetPresence();
        Assert.False(Radio.State.IsModemPresenceReadActive);
        Assert.Equal(8, Transport.SentLines.Count);            // 7 reads + 1 sentinel
        Assert.Equal(0, Transport.CountSent("MODEM PRE"));     // no bulk form yet

        Transport.ClearSent();
        AnswerSentinel();                                      // the batch's sentinel
        Assert.Equal(["MODEM PRE", "BAT ST"], Transport.SentLines);
        Assert.True(Radio.State.IsModemPresenceReadActive);
    }

    [Fact]
    public void ModemQueue_ATargetedReadNeverDispatchesWhilePresenceIsActive()
    {
        // …and the reverse. Without it a targeted answer could land inside the
        // presence window and be counted as "listed by the bulk", i.e. as
        // ENABLED.
        ConnectReady();
        Radio.Ssb.QueryModemPresetPresence();
        Assert.True(Radio.State.IsModemPresenceReadActive);

        Radio.Ssb.QueryModemPreset(3);
        Assert.False(Radio.State.IsModemTargetedReadActive);
        Assert.Equal(["MODEM PRE", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["MODEM PRE 3", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ATargetedPresetRow_CanNeverEnterTheEnabledSet()
    {
        // The consequence the serialization exists for, pinned as its own
        // fact: a preset whose FIELDS were read targeted is not thereby
        // enabled. Preset 2 here is the disabled one — read targeted, then a
        // presence read that does NOT list it.
        ConnectReady();
        Radio.Ssb.QueryModemPreset(2);
        Transport.InjectLine(PresetLine(2));
        AnswerSentinel();

        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();

        Assert.Equal([1], Radio.State.ModemPresetPresence.Enabled);
        Assert.DoesNotContain(2, Radio.State.ModemPresetPresence.Enabled);
        // …while its FIELDS are still there, which is the whole point of the
        // targeted read.
        Assert.Contains(Radio.State.ModemPresets, l => l.StartsWith("2 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ModemQueue_CoalescedTargetedRequests_UnionIntoOneBatch()
    {
        ConnectReady();
        Radio.Ssb.QueryModemPreset(0);
        long pendingA = Radio.Ssb.QueryModemPreset(5);
        long pendingB = Radio.Ssb.QueryModemPreset(3);
        long pendingC = Radio.Ssb.QueryModemPreset(5);      // a repeat unions away
        Assert.Equal(pendingA, pendingB);
        Assert.Equal(pendingA, pendingC);

        Transport.ClearSent();
        AnswerSentinel();
        Assert.Equal(["MODEM PRE 3", "MODEM PRE 5", "BAT ST"], Transport.SentLines);
    }

    // ====================================================================
    // G. The KEYED CHANNEL mirror — both paths
    // ====================================================================

    [Fact]
    public void TargetedChannelReads_Accumulate_TheUpsertKeepsSiblings()
    {
        // The LQA report reads one channel per named row; before round 11 each
        // DI cleared the mirror and the previous row vanished.
        ConnectReady();
        Radio.Ssb.DisplayChannels(4, 4);
        Transport.InjectLine("CH 04 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Radio.Ssb.DisplayChannels(9, 9);
        Transport.InjectLine("CH 09 RxFr 14313500 TxFr 14313500 MODE LSB AGC SL BA 3.0  RXONLY YES");

        Assert.Equal(2, Radio.State.ChannelList.Count);
    }

    [Fact]
    public void TheBulkRefreshPaths_StillStartClean()
    {
        // "Refresh" survives as a DELIBERATE gesture rather than a side effect
        // of every read: the bare-DI dump clears, and the explicit
        // forget-what-you-were-told call clears without sending anything.
        ConnectReady();
        Radio.Ssb.DisplayChannels(4, 4);
        Transport.InjectLine("CH 04 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Assert.Single(Radio.State.ChannelList);

        Radio.Ssb.DisplayAllChannels();
        Assert.Empty(Radio.State.ChannelList);

        Transport.InjectLine("CH 04 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Assert.Single(Radio.State.ChannelList);

        Transport.ClearSent();
        Radio.Ssb.ForgetStoredChannels();
        Assert.Empty(Radio.State.ChannelList);
        Assert.Empty(Transport.SentLines);           // a clear is not a command
    }

    // ====================================================================
    // H. Reconnect — nothing a previous radio said may survive
    // ====================================================================

    [Fact]
    public void Reconnect_ResetsAllFourNewStores()
    {
        ConnectReady();
        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(Member01);
        AnswerSentinel();
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();
        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        AnswerSentinel();
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();

        Radio.Disconnect();
        ConnectReady();

        Assert.Empty(Radio.State.Ale.NetMembers);
        Assert.Null(Radio.State.Ale.LqaSchedules);
        Assert.Null(Radio.State.Hop.ExcludeBands);
        Assert.Equal(RadioState.PresenceState.Unknown, Radio.State.ModemPresetPresence.State);
        Assert.Empty(Radio.State.ModemPresetPresence.Enabled);
        Assert.Empty(Radio.State.ModemPresets);
    }

    // ====================================================================
    // I. Change notifications — completion surfaces as a store event
    // ====================================================================

    [Fact]
    public void EveryNewStore_RaisesItsOwnMirrorAndCompletionProperties()
    {
        ConnectReady();
        var seen = new List<RadioProperty>();
        Radio.State.Changed += p => { lock (seen) seen.Add(p); };

        Radio.Ale.ReadNetMembers("N1");
        Transport.InjectLine(Member01);
        AnswerSentinel();
        Radio.Ale.ReadLqaSchedules();
        Transport.InjectLine(ExchangeRow);
        AnswerSentinel();
        Radio.Hop.QueryExcludeBands();
        Transport.InjectLine(ExcludeRow0);
        AnswerSentinel();
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine(PresetLine(1));
        AnswerSentinel();

        RadioProperty[] expected =
        [
            RadioProperty.AleNetMembers, RadioProperty.AleMemberRead,
            RadioProperty.AleLqaSchedules, RadioProperty.AleScheduleRead,
            RadioProperty.HopExcludeBands, RadioProperty.HopExcludeRead,
            RadioProperty.ModemPresetPresence, RadioProperty.ModemPresetRead,
        ];
        lock (seen)
            foreach (var property in expected)
                Assert.Contains(property, seen);
    }
}
