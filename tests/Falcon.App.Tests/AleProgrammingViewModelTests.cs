using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE settings pane's "Address programming" card
/// (plan-ale-programming.md §4.4/§9 phase-2 clause 3), over the REAL stack:
/// AleSurface + AleProgrammingGate on a real Prc138Radio and the injecting
/// transport, so every assertion below is about bytes the app actually put on
/// the wire and lines the radio actually sent.
///
/// <para>The gate choreography every write test drives (the Phase-1 pins):
/// <code>
///   press             → BAT ST                        (OPENING bracket)
///   AnswerSentinel()  → the write + BAT ST + the closing book read
///   AnswerSentinel()  → the outcome is delivered       (CLOSING bracket)
/// </code>
/// The fixtures therefore DRAIN the landing read's sentinel first: with a
/// foreign sentinel outstanding the gate re-arms instead of writing, which is
/// correct and is pinned in AleProgrammingGateTests, but it is not what these
/// tests are about.</para>
/// </summary>
public class AleProgrammingViewModelTests : SessionTestBase
{
    private readonly AleSurface _ale;

    public AleProgrammingViewModelTests() => _ale = new AleSurface(Radio);

    /// <summary>The §5 CONTROLLABLE fake: it records every (title, message,
    /// accept, cancel) and hands back a handle so a prompt can be held OPEN
    /// while session or mode state changes underneath it.</summary>
    private readonly FakeConfirmationPrompt _prompt = new();

    private AleProgrammingViewModel Vm() => new(_ale, Session, _prompt);

    /// <summary>Verbatim R7-shape listing lines: 2 selfs (one group-0
    /// bootstrap), 2 individuals, 1 net.</summary>
    private void InjectStationBook()
    {
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("INDAD BBB               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
    }

    /// <summary>Ready + confirmed ALE, the card's initial-sight read answered
    /// with the canned book, and the wire drained.</summary>
    private AleProgrammingViewModel ReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");        // the initial-sight read fires here
        InjectStationBook();
        AnswerSentinel();                    // commit
        Transport.ClearSent();
        return vm;
    }

    /// <summary>The same, with the radio answering the book read with NOTHING
    /// — a confirmed EMPTY book, which is the fill ROOT case.</summary>
    private AleProgrammingViewModel EmptyBookVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();
        Transport.ClearSent();
        return vm;
    }

    private static void Pick(AleProgrammingViewModel vm, string kind)
        => vm.KindChoices.Single(c => c.Value == kind).SelectCommand.Execute(null);

    private static void SpinTo(AleProgrammingViewModel vm, int group)
    {
        for (int i = 0; i < AleProgrammingViewModel.ChannelGroupCount && vm.GroupSelection != group; i++)
            vm.GroupUpCommand.Execute(null);
    }

    /// <summary>ROUND 15 E-5: the associated-self wheel is a PICKER, so the
    /// fixture picks instead of spinning. The pick also SETS the group wheel to
    /// that self's group — a default, not a lock — so a test that wants a
    /// particular group spins AFTER picking.</summary>
    private static void PickAssociatedSelf(AleProgrammingViewModel vm, string self)
        => vm.AssociatedSelfSelection = self;

    /// <summary>ROUND 15 E-1/E-D3: the member wheel is a PICKER over TYPED
    /// candidates, so the fixture picks the candidate whose WIRE address is the
    /// one named — never its display text.</summary>
    private static void PickMember(AleProgrammingViewModel vm, string member)
        => vm.MemberPick = vm.MemberChoices.Single(c => c.Address == member);

    // ---- Round 11 §5 fixtures: the two IMPACT mirrors ---------------------

    /// <summary>Verbatim member continuation (bench 2026-08-17 shape).</summary>
    private static string MemberLine(int number, string address)
        => $"     MEMBER {number:00}  {address}";

    private const string NoMembersProgrammed = " NO MEMBERS PRGMD ";
    private const string NoLqaScheduled = " NO LQA SCHEDULED ";

    /// <summary>Verbatim bare-EXCH listing row.</summary>
    private static string ExchangeRow(string address)
        => $"EXCHANGE {address}              INTERVAL 01:00 START TIME 22:34";

    /// <summary>The book tab's LAZY landing, answered: the book read commits
    /// the canned listing, then the per-net targeted reads it queued commit
    /// <paramref name="members"/> against NT1 (the fixture book's only
    /// net).</summary>
    private void LandOnBookTab(AleProgrammingViewModel vm, params string[] members)
    {
        vm.OpenBookTabCommand.Execute(null);
        InjectStationBook();
        AnswerSentinel();                    // the book commits; NT1's read dispatches
        foreach (var line in members) Transport.InjectLine(line);
        AnswerSentinel();                    // NT1's membership commits
    }

    /// <summary>Load the schedule mirror directly through the surface — the
    /// LQA tab owns that landing, and this card only ever reads it as a delete
    /// PREREQUISITE.</summary>
    private void LoadSchedules(params string[] rows)
    {
        _ale.RequestLqaSchedules();
        foreach (var line in rows) Transport.InjectLine(line);
        AnswerSentinel();
    }

    /// <summary>Ready + the canned book + BOTH impact mirrors loaded, so a
    /// Delete press has nothing left to read and asks at once.</summary>
    private AleProgrammingViewModel ImpactLoadedVm(
        string[]? members = null, string[]? schedules = null)
    {
        var vm = ReadyVm();
        LandOnBookTab(vm, members ?? [NoMembersProgrammed]);
        LoadSchedules(schedules ?? [NoLqaScheduled]);
        Transport.ClearSent();
        return vm;
    }

    // ==== Read path — the §6 table, gesture by gesture ======================

    [Fact]
    public void ReadyInAle_SendsExactlyOneStationBookRead_AndNothingElse()
    {
        // The EDITOR's initial sight. Driven by hand because ReadyVm()'s
        // trailing ClearSent would wipe the very send under test.
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);          // nothing before ALE confirms

        Transport.InjectLine("ALE>");

        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);
        Assert.True(vm.AreControlsEnabled);
    }

    [Fact]
    public void EveryProgramTabLanding_ReadsTheBookFresh_NotOncePerSession()
    {
        var vm = ReadyVm();

        vm.OpenProgramTabCommand.Execute(null);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        // The read's sentinel is drained first so the NEXT landing dispatches
        // immediately. Core runs ONE book operation per store at a time
        // (§4.1), so a landing under an already-open read would be DEFERRED
        // into the pending operation and go out on its completion instead —
        // still one read per landing, just not on the same tick. That is the
        // queue's contract, not this card's tier, and pinning it here would be
        // pinning Core.
        AnswerSentinel();
        Transport.ClearSent();

        vm.OpenProgramTabCommand.Execute(null);     // …and again, fresh
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void AddressBookTab_IsTheLazyTier_ItReadsOnceThenRendersFromTheMirror()
    {
        var vm = ReadyVm();

        vm.OpenBookTabCommand.Execute(null);
        Assert.True(vm.IsBookTabOpen);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        AnswerSentinel();
        Transport.ClearSent();
        vm.OpenBookTabCommand.Execute(null);        // renders from the mirror
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SessionDrop_ReArmsBothLatches_AndTheReconnectReadsAgain()
    {
        var vm = ReadyVm();
        vm.OpenBookTabCommand.Execute(null);
        AnswerSentinel();

        Session.Close();
        Transport.ClearSent();
        ConnectReady();
        Transport.InjectLine("ALE>");                // sight read, re-armed

        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);
        AnswerSentinel();
        Transport.ClearSent();

        vm.OpenBookTabCommand.Execute(null);         // …and so is the tab's
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void TheOperateStationsCard_GainsNoSends_TheTIERSJustRunInTurn()
    {
        // The §6 promise that the OTHER pane is untouched — asserted as the
        // COMPLETE designed transcript, end to end, rather than as a snapshot
        // taken before the first sentinel answers (audit round 1, MAJOR: the
        // earlier version stopped there and mis-claimed ONE book read).
        //
        // THREE ALE consumers share the surface, and THREE tiers therefore
        // fire on entry: the Operate pane's lazy-once station list, the
        // address card's initial-sight book landing, and the groups card's
        // initial-sight CHG. The book ones are TWO SEPARATE §6 tiers, so Core
        // runs them one at a time — active, then pending — which is exactly
        // its one-operation-per-store contract, NOT a coalesce. Two book reads
        // is the DESIGNED behavior; this plan simply added the second, and
        // took nothing from Operate.
        var operate = new AleViewModel(
            _ale, Session,
            new MessagesViewModel(_ale, Session, TimeProvider.System),
            new LqaViewModel(_ale, new ChannelSurface(Radio), Session),
            _ => { });
        var addresses = Vm();
        var groups = new AleScanGroupsViewModel(_ale, Session);

        ConnectReady();
        Transport.InjectLine("ALE>");

        // Wave 1 — the ACTIVE book operation (Operate's lazy-once load) plus
        // the group sweep that BROADCAST ROUND F3 added to the SAME tier
        // (plan-ale-broadcast-round.md §2, critic F3: the pinned ANY/ALL rows'
        // channel pickers read the CHG mirror, which nothing on the operate
        // path populated). It takes the group store first, so the groups card's
        // own single `CHG 0` becomes the PENDING group operation; both group
        // sentinels queue behind the book's.
        string[] wave1 =
        [
            "SLFAD", "INDAD", "NETAD", "BAT ST",
            "CHG 0", "CHG 1", "CHG 2", "CHG 3", "CHG 4",
            "CHG 5", "CHG 6", "CHG 7", "CHG 8", "CHG 9",
            // LINKED-AMD ROUND (Stage 9 closed 2026-08-24): the Inbox landing
            // read rides the same entry - a FOURTH consumer, one bare RXMSG.
            "RXMSG",
        ];
        Assert.Equal(wave1, Transport.SentLines);

        // Wave 2 — the group sweep's sentinel dispatches, and the PENDING book
        // operation (the address card's sight landing) begins.
        string[] wave2 = [.. wave1, "BAT ST", "SLFAD", "INDAD", "NETAD"];
        AnswerSentinel();
        Assert.Equal(wave2, Transport.SentLines);

        // Wave 3 — the second book read's sentinel, and the groups card's own
        // PENDING `CHG 0` behind it.
        string[] wave3 = [.. wave2, "BAT ST", "CHG 0"];
        AnswerSentinel();
        Assert.Equal(wave3, Transport.SentLines);

        // Wave 4 — that last read's own sentinel, and nothing more.
        string[] whole = [.. wave3, "BAT ST"];
        AnswerSentinel();
        Assert.Equal(whole, Transport.SentLines);

        // …and with every sentinel answered the wire is genuinely QUIET: no
        // third book read, no third group read, nothing deferred still to
        // escape.
        AnswerSentinel();
        Assert.Equal(whole, Transport.SentLines);

        // The whole entry, counted: exactly TWO book reads (one per
        // book-reading tier) and TWO group reads — Operate's ten-slot sweep and
        // the groups card's own single slot, which is why `CHG 0` appears
        // twice while `CHG 1` appears once.
        Assert.Equal(2, Transport.CountSent("SLFAD"));
        Assert.Equal(2, Transport.CountSent("INDAD"));
        Assert.Equal(2, Transport.CountSent("NETAD"));
        Assert.Equal(2, Transport.CountSent("CHG 0"));
        Assert.Equal(1, Transport.CountSent("CHG 1"));

        // RE-ENTERING ALE sends nothing at all — asserted only now that every
        // sentinel has been answered, so "silence" cannot be a queue still
        // holding something back. Operate keeps its once-per-session latch,
        // and neither new card re-arms it or adds a read to the mode edge.
        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("ALE>");
        Assert.Empty(Transport.SentLines);

        Assert.NotNull(operate);
        Assert.NotNull(addresses);
        Assert.NotNull(groups);
    }

    [Fact]
    public void KindSwitchAndEveryWheelMove_SendNOTHING()
    {
        // The negative half of the §6 table: these are view state over a form
        // that has not been submitted.
        var vm = ReadyVm();

        Pick(vm, "Individual");
        Pick(vm, "Net");
        Pick(vm, "Self");
        vm.GroupUpCommand.Execute(null);
        vm.GroupDownCommand.Execute(null);

        // ROUND 15 E-5/E-1 (critic F49): the two wheels became PICKERS, so the
        // pin extends to every pick. NO pick sends a PROGRAMMING command, and
        // the ONLY wire effect of any pick on this card is the Member kind's
        // net pick — its once-per-session targeted read, pinned separately.
        vm.AssociatedSelfSelection = "TST";
        vm.AssociatedSelfSelection = "ZZZ";
        Pick(vm, "Member");
        vm.MemberPick = null;

        Assert.Empty(Transport.SentLines);

        // …and the net pick's ONE read is the whole of its traffic: picking it
        // again sends nothing at all.
        vm.NetPick = "NT1";
        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);
        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();
        Transport.ClearSent();

        vm.NetPick = null;
        vm.NetPick = "NT1";
        vm.MemberPick = vm.MemberChoices[0];
        Pick(vm, "Self");
        Pick(vm, "Member");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Reconnect_PreservesTypedTextAndSelections_TheStandingPin()
    {
        var vm = ReadyVm();
        Pick(vm, "Individual");
        vm.NameInput = "AAA";
        // ROUND 15 E-5: picking a self SETS the group to that self's, so a
        // deliberate group is chosen AFTER the pick — which is also the pin
        // that the pick is a DEFAULT and not a lock.
        PickAssociatedSelf(vm, "TST");
        SpinTo(vm, 4);

        Session.Close();
        Transport.ClearSent();
        ConnectReady();
        Transport.InjectLine("ALE>");

        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);   // it read
        Assert.Equal("AAA", vm.NameInput);                                          // …and kept
        Assert.Equal(4, vm.GroupSelection);
        Assert.Equal("TST", vm.AssociatedSelfSelection);
        Assert.Equal(AleProgramKind.Individual, vm.Kind);
    }

    [Fact]
    public void NotReady_TheCardIsInert_AndNothingSends()
    {
        var vm = Vm();
        vm.NameInput = "CAM";

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.ActionCommand.CanExecute(null));

        vm.ActionCommand.Execute(null);
        vm.OpenBookTabCommand.Execute(null);
        vm.OpenProgramTabCommand.Execute(null);
        vm.GroupUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ReadyButNotInAle_TheCardIsInert_AndSaysWhy()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Assert.False(vm.AreControlsEnabled);
        Assert.Contains("ALE-scoped", vm.DisabledReason);
        Assert.Empty(Transport.SentLines);
    }

    // ==== Program: the exact write, inside the bracket ======================

    [Fact]
    public void ProgramSelf_SendsTheTwoArgumentWrite_TrimmedAndUppercased()
    {
        var vm = ReadyVm();
        vm.NameInput = "  cam ";
        SpinTo(vm, 1);

        vm.ActionCommand.Execute(null);
        Assert.Equal(["BAT ST"], Transport.SentLines);      // the opening bracket alone
        AnswerSentinel();

        Assert.Equal(
            ["BAT ST", "SLFAD CAM 1", "BAT ST", "SLFAD", "INDAD", "NETAD"],
            Transport.SentLines);
    }

    [Fact]
    public void ProgramSelf_NeverCarriesAnAssociatedSelf_TheStructuralPin()
    {
        // Hidden-never-sent, structurally: the Self path calls a TWO-argument
        // wrapper, so a selection made under another kind cannot ride along
        // even though switching kind does not clear it.
        var vm = ReadyVm();
        Pick(vm, "Individual");
        PickAssociatedSelf(vm, "TST");
        Assert.Equal("TST", vm.AssociatedSelfSelection);

        Pick(vm, "Self");
        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);
        AnswerSentinel();

        var write = Assert.Single(Transport.SentLines, l => l.StartsWith("SLFAD ", StringComparison.Ordinal));
        Assert.Equal("SLFAD CAM 1", write);
        Assert.DoesNotContain("TST", write, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramIndividual_SendsTheThreeArgumentWrite_WithThePickedSelf()
    {
        var vm = ReadyVm();
        Pick(vm, "Individual");
        vm.NameInput = "CCC";
        SpinTo(vm, 1);
        PickAssociatedSelf(vm, "TST");

        vm.ActionCommand.Execute(null);
        AnswerSentinel();

        Assert.Equal(
            ["BAT ST", "INDAD CCC 1 TST", "BAT ST", "SLFAD", "INDAD", "NETAD"],
            Transport.SentLines);
    }

    [Fact]
    public void ProgramNet_SendsTheThreeArgumentWrite()
    {
        var vm = ReadyVm();
        Pick(vm, "Net");
        vm.NameInput = "NT2";
        SpinTo(vm, 1);
        PickAssociatedSelf(vm, "TST");

        vm.ActionCommand.Execute(null);
        AnswerSentinel();

        Assert.Contains("NETAD NT2 1 TST", Transport.SentLines);
    }

    [Fact]
    public void AnEmptyBook_RefusesTheProgram_NamingTheDependency()
    {
        var vm = EmptyBookVm();
        Pick(vm, "Individual");
        vm.NameInput = "AAA";
        Assert.Null(vm.AssociatedSelfSelection);            // nothing picked…
        Assert.Empty(vm.SelfChoices);                       // …and nothing to pick

        vm.ActionCommand.Execute(null);

        Assert.Contains("program a self first", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>ROUND 10 §7: the SELF cases used to expect "1-3" — the client
    /// mirrored a Core bound that has since moved to 15 for every kind. The
    /// theory keeps its shape (a client bound refuses BEFORE the wire) and its
    /// cases move to the new bound; the §7 block further down pins the change
    /// itself, per kind, in both directions.</summary>
    [Theory]
    [InlineData("Self", "ABCDEFGHIJKLMNOP")]
    [InlineData("Self", "")]
    [InlineData("Individual", "ABCDEFGHIJKLMNOP")]
    [InlineData("Net", "")]
    public void ClientBounds_RefuseBeforeTheWire(string kind, string name)
    {
        var vm = ReadyVm();
        Pick(vm, kind);
        PickAssociatedSelf(vm, "TST");
        vm.NameInput = name;

        vm.ActionCommand.Execute(null);

        Assert.Equal(AleProgrammingViewModel.NameLengthError, vm.InputError);
        Assert.Contains("1-15", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    // ==== Outcomes =========================================================

    [Fact]
    public void ARefusalInsideTheBracket_RendersThroughTheVocabulary_NeverVerbatim()
    {
        var vm = ReadyVm();
        vm.NameInput = "TST";                                // already in the book
        vm.ActionCommand.Execute(null);
        AnswerSentinel();

        Transport.InjectLine(" ADDRESS EXISTS ");            // the radio's own line
        AnswerSentinel();

        Assert.Equal(AleRefusalVocabulary.Describe("ADDRESS EXISTS"), vm.OperationStatus);
        Assert.NotEqual("ADDRESS EXISTS", vm.OperationStatus);
        Assert.Contains("already in use", vm.OperationStatus, StringComparison.Ordinal);
        // R13: the status is operator language ONLY — the radio's token is not
        // in it (it is on the Console). This assertion was the reverse before.
        Assert.DoesNotContain("ADDRESS EXISTS", vm.OperationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepted_SaysNothing_TheReReadRowIsTheProof()
    {
        var vm = ReadyVm();
        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);
        AnswerSentinel();
        AnswerSentinel();

        Assert.Equal("", vm.OperationStatus);
        Assert.False(vm.HasOperationStatus);

        // The closing read's own answer is what the operator sees.
        Transport.InjectLine("SLFAD CAM               CHGROUP 01");
        AnswerSentinel();
        Assert.Contains(vm.BookRows, r => r is { KindText: "SELF", NameText: "CAM" });
    }

    [Fact]
    public void ABusyGate_RefusesTheSecondPress_WithoutSendingAnything()
    {
        var vm = ReadyVm();
        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);            // one operation is now open
        Transport.ClearSent();

        vm.NameInput = "BOB";
        vm.ActionCommand.Execute(null);

        Assert.Equal(AleProgrammingGate.BusyReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    // ==== Net membership: the RADIO MIRROR (round 11 §5) ===================
    // The round-10 session send-log is gone: membership became readable on
    // 2026-08-17, so what the section renders is the radio's own answer, in
    // three states, and an ADDM's outcome renders where every other write's
    // does.

    /// <summary>Put the card on the MEMBER kind (round 15 E-1) with NT1
    /// picked, its membership already read.</summary>
    private AleProgrammingViewModel MemberSectionVm(params string[] members)
    {
        var vm = ImpactLoadedVm(members.Length > 0 ? members : [NoMembersProgrammed]);
        Pick(vm, "Member");
        vm.NetPick = "NT1";
        Transport.ClearSent();
        return vm;
    }

    [Fact]
    public void MemberDisplay_UNREAD_IsExactlyOneHyphenRow()
    {
        // State 1 of three. The net exists in the book, but nothing has read
        // its membership this session, so the projection is the round's single
        // placeholder row — never an empty table, which would read as "none".
        var vm = ReadyVm();
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        var row = Assert.Single(vm.MemberDisplayRows);
        Assert.Equal("—", row.NumberText);
        Assert.Equal("—", row.AddressText);
        Assert.False(vm.HasNoMembers);
    }

    [Fact]
    public void MemberDisplay_READEMPTY_IsTheCaption_AndNoRows()
    {
        // State 2: the radio SAID none (its own NO MEMBERS PRGMD).
        var vm = MemberSectionVm(NoMembersProgrammed);

        Assert.Empty(vm.MemberDisplayRows);
        Assert.True(vm.HasNoMembers);
        Assert.Equal("No members programmed.", AleProgrammingViewModel.NoMembersCaption);
    }

    [Fact]
    public void MemberDisplay_ROWS_AreTheRadiosNumbersAndAddresses_InInsertionOrder()
    {
        // State 3. The ORDER is the radio's insertion order, not a sort: the
        // fixture's numbers descend by address on purpose, so a tidy-up sort
        // would show.
        var vm = MemberSectionVm(MemberLine(1, "BBB"), MemberLine(2, "AAA"));

        Assert.Equal(["01 BBB", "02 AAA"],
            vm.MemberDisplayRows.Select(r => $"{r.NumberText} {r.AddressText}"));
        Assert.False(vm.HasNoMembers);
    }

    [Fact]
    public void TheMemberSection_ReadsItsNetOnce_PerSession()
    {
        // §5's read tier: FIRST SIGHT of a net whose members are unread fires
        // that net's targeted read, once. Driven from a card that has NOT
        // landed on the book tab, so this read can only be the member tier's.
        var vm = ReadyVm();
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();
        Transport.ClearSent();

        // Re-picking the same net renders from the mirror and sends NOTHING.
        vm.NetPick = null;
        vm.NetPick = "NT1";
        Assert.Empty(Transport.SentLines);
        Assert.Equal(["01 AAA"], vm.MemberDisplayRows.Select(r => $"{r.NumberText} {r.AddressText}"));
    }

    // ==== The member read's ONE RETRY on a silence (round 16 fixes S5) =====
    // The once-per-session latch was added FOR the fault case: an unanswered
    // read leaves the key absent, and without the latch every mirror event
    // would re-fire the read forever. The cost was that ONE silence left the
    // member section on its placeholder until the session dropped or a write
    // invalidated the net.
    //
    // The rule now: each id `ReadMembersOnce` is handed maps to the names it
    // covered; an UNANSWERED completion of a mapped id UNLATCHES each still
    // absent name ONCE, so the ordinary once-per-session path re-reads it —
    // now if ALE is ready, or when ALE comes back and the net is shown. A
    // second silence latches it for good.
    //
    // Every silence below is a real PING TIMEOUT, never an injected answer.

    [Fact]
    public void MemberRead_Silence_RetriesExactlyOnce()
    {
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;
        Pick(vm, "Member");
        vm.NetPick = "NT1";
        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);   // the first read

        Transport.ClearSent();
        Assert.True(WaitUntil(() => Transport.CountSent("NETAD NT1") == 1, 10_000),
            "an unanswered member read must be tried once more");
        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);

        // …and the section is still honest while the retry is on the wire.
        Assert.Equal("—", Assert.Single(vm.MemberDisplayRows).AddressText);
    }

    [Fact]
    public void MemberRead_SecondSilence_NoThirdRead()
    {
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        Assert.True(WaitUntil(() => Transport.CountSent("NETAD NT1") == 2, 10_000),
            "the retry did not go out");

        // The retry's OWN silence must not unlatch again. Drive the card the
        // way the operator would — re-picking the net is the gesture that
        // re-enters UpdateMemberSection.
        Assert.False(WaitUntil(() => Transport.CountSent("NETAD NT1") > 2, 1_000),
            "a third read went out — the retry is not capped at one");
        vm.NetPick = null;
        vm.NetPick = "NT1";
        Assert.Equal(2, Transport.CountSent("NETAD NT1"));
    }

    [Fact]
    public void MemberRead_RetryAnswered_MirrorShown()
    {
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        Assert.True(WaitUntil(() => Transport.CountSent("NETAD NT1") == 2, 10_000));

        // The retry ANSWERS — which is the whole point of trying again.
        Radio.Ale.RefreshTimeoutMs = 10_000;
        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();

        Assert.Equal(["01 AAA"], vm.MemberDisplayRows.Select(r => $"{r.NumberText} {r.AddressText}"));
        Assert.False(vm.HasNoMembers);
    }

    [Fact]
    public void MemberRead_Answered_NoRetry()
    {
        // The negative that keeps the read tier a TIER: an answered read is
        // read, and nothing re-asks. (Green before this round and after.)
        var vm = ReadyVm();
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();

        Assert.Equal(1, Transport.CountSent("NETAD NT1"));
        Assert.False(WaitUntil(() => Transport.CountSent("NETAD NT1") > 1, 500));
    }

    [Fact]
    public void MemberRead_InvalidatedByAWrite_ReReadsOnce_AndTheRetryAddsNothing()
    {
        // A write INVALIDATES the net's membership, and its own re-read is the
        // tier's — the retry must not add a read on top of it.
        //
        // SCOPE NOTE (audit round 1): "invalidated BETWEEN the request and its
        // completion", which the plan names, is NOT REACHABLE through this VM.
        // `ReReadMembersAfterWrite` runs only from a programming-gate outcome,
        // and the gate will not release a write while another sentinel is on
        // the wire (`PendingPingCount`/`PingAnswerDebt` must both be zero),
        // while Core keeps ONE sentinel outstanding at a time — so every mapped
        // id is already completed and unmapped by the time an invalidation can
        // happen. The plan's map-scrubbing loop was therefore DELETED rather
        // than pinned; the reasoning is on `ReReadMembersAfterWrite`. What is
        // observable, and what this pins, is the COUNT.
        var vm = ReadyVm();
        Pick(vm, "Member");
        vm.NetPick = "NT1";
        Assert.Equal(1, Transport.CountSent("NETAD NT1"));

        Transport.InjectLine(NoMembersProgrammed);   // read one ANSWERS, read-empty
        AnswerSentinel();
        Assert.True(vm.HasNoMembers);

        PickMember(vm, "AAA");
        vm.ActionCommand.Execute(null);              // the gate opens
        AnswerSentinel();                            // ADDM goes out; Core invalidates NT1
        AnswerSentinel();                            // the bracket closes
        // The re-read QUEUES behind the bracket's closing book read (one NETAD
        // operation at a time) and goes out when that one commits.
        InjectStationBook();
        AnswerSentinel();

        Assert.Equal(2, Transport.CountSent("NETAD NT1"));
        Assert.False(WaitUntil(() => Transport.CountSent("NETAD NT1") > 2, 500),
            "a third member read went out after the write's own re-read");
    }

    [Fact]
    public void MemberRead_ImpactFault_NoRetry()
    {
        // IMPACT-WAIT ids are deliberately NOT mapped: a retry beside the
        // delete write would sit a read inside the programming bracket, which
        // is the deferred round-16 work's business. A faulted impact read stays
        // a faulted prompt, exactly as before.
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;
        Transport.ClearSent();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.True(WaitUntil(() => _prompt.CallCount == 1, 10_000));
        Assert.Contains("Impact unknown", _prompt.Last.Message, StringComparison.Ordinal);

        int reads = Transport.CountSent("NETAD NT1");
        Assert.Equal(1, reads);                      // the impact read itself, once
        Assert.False(WaitUntil(() => Transport.CountSent("NETAD NT1") > reads, 1_000),
            "an impact-wait member read was retried");
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void MemberRead_ModeLostBeforeSilence_RetriesOnReturn()
    {
        // Why the retry UNLATCHES rather than re-requesting directly: if the
        // mode dropped before the silence was observed, a direct re-request
        // would be skipped by the AleReady check and the name would stay
        // latched forever. Unlatching makes the retry the SAME path as the
        // first read — it fires when ALE is back and the net is shown.
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;
        Pick(vm, "Member");
        vm.NetPick = "NT1";
        Assert.Equal(1, Transport.CountSent("NETAD NT1"));

        Transport.InjectLine("SSB> ");               // ALE is no longer confirmed
        Assert.False(WaitUntil(() => Transport.CountSent("NETAD NT1") > 1, 1_000),
            "nothing may be sent while the card is off-mode");

        Transport.InjectLine("ALE> ");               // …and ALE comes back
        Assert.True(WaitUntil(() => Transport.CountSent("NETAD NT1") == 2, 10_000),
            "the unlatched name must be re-read when ALE returns");
    }

    [Fact]
    public void TheMemberTier_NeverQueriesANameTheBookDoesNotHold()
    {
        // ROUND 15 E-1 re-key. The typed name no longer reaches membership at
        // all — the net is PICKED from the mirror, so a name the book does not
        // hold cannot be picked in the first place. What survives of this
        // theory is its assertion: TYPING sends nothing, on any kind, and a
        // pick that is not a mirrored net queries nothing either.
        var vm = ReadyVm();
        Pick(vm, "Member");

        vm.NameInput = "N";
        vm.NameInput = "NT";
        vm.NameInput = "AAA";               // an INDIVIDUAL, not a net
        vm.NameInput = "NOPE";
        vm.NetPick = "NOPE";                // not a net the radio reported
        vm.NetPick = "AAA";                 // an individual, not a net

        Assert.Empty(Transport.SentLines);

        // Anti-vacuity: the real net still reads.
        vm.NetPick = "NT1";
        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void TheMemberTier_DoesNotFireOffTheMemberKind()
    {
        // The section is MEMBER-kind only (E-Q2 moved it off Net), and so is
        // its read: a net picked while another kind is on screen queries
        // nothing.
        var vm = ReadyVm();
        vm.NetPick = "NT1";                 // still on Self

        Assert.Empty(Transport.SentLines);

        Pick(vm, "Member");
        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void TheBookTabLanding_QueuesOneTargetedReadPerMirroredNet()
    {
        // §5's other tier. The book read goes out first and the member read
        // queues behind it (Core runs one NETAD operation at a time), so the
        // targeted query appears when the book's sentinel is answered.
        var vm = ReadyVm();

        vm.OpenBookTabCommand.Execute(null);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        InjectStationBook();
        AnswerSentinel();

        Assert.Equal(
            ["SLFAD", "INDAD", "NETAD", "BAT ST", "NETAD NT1", "BAT ST"],
            Transport.SentLines);

        // …and the landing is LAZY-ONCE: a second landing reads nothing.
        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();
        Transport.ClearSent();
        vm.OpenBookTabCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AddMember_SendsAddmInsideTheBracket_ThenReReadsTheNetsMembership()
    {
        // The ADDM verify is the member ROWS now, not a log line: the write
        // invalidates the net's mirror in Core and the card re-reads it.
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");

        vm.ActionCommand.Execute(null);
        AnswerSentinel();
        Assert.Equal(
            ["BAT ST", "ADDM NT1 AAA", "BAT ST", "SLFAD", "INDAD", "NETAD"],
            Transport.SentLines);

        AnswerSentinel();                    // the bracket closes → Accepted
        Assert.Equal("", vm.OperationStatus);

        // The re-read QUEUES behind the closing book read (one NETAD operation
        // at a time in Core) and goes out when that one commits.
        InjectStationBook();
        AnswerSentinel();
        Assert.Contains("NETAD NT1", Transport.SentLines);

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();

        Assert.Equal(["01 AAA"], vm.MemberDisplayRows.Select(r => $"{r.NumberText} {r.AddressText}"));
    }

    [Fact]
    public void ANetProgram_InvalidatesTHATNetsMembership_AndItReReads()
    {
        // §5's third invalidation trigger. A re-created net has no members, so
        // the mirror goes UNREAD at the write and the card must not keep
        // rendering yesterday's rows — nor latch the re-read out.
        var vm = MemberSectionVm(MemberLine(1, "AAA"));
        Assert.Equal(["01 AAA"], vm.MemberDisplayRows.Select(r => $"{r.NumberText} {r.AddressText}"));

        // ROUND 15 E-Q2 re-key: the NET kind programs the net (its member
        // section moved to the Member kind), so the write is composed there.
        Pick(vm, "Net");
        vm.NameInput = "NT1";
        PickAssociatedSelf(vm, "TST");
        Transport.ClearSent();

        vm.ActionCommand.Execute(null);
        AnswerSentinel();                            // NETAD NT1 1 TST goes out
        Assert.Contains("NETAD NT1 1 TST", Transport.SentLines);

        // …and the membership the mirror held for that net went UNREAD at the
        // write, which is what the Member kind renders when it comes back.
        Pick(vm, "Member");
        Assert.Single(vm.MemberDisplayRows);
        Assert.Equal("—", vm.MemberDisplayRows[0].AddressText);

        AnswerSentinel();                            // the bracket closes
        InjectStationBook();
        AnswerSentinel();                            // the closing book read commits

        Assert.Contains("NETAD NT1", Transport.SentLines);
    }

    [Fact]
    public void AnAcceptedDelete_InvalidatesEVERYNetsMembership_AndTheyReRead()
    {
        // DELAD is GLOBAL — the address leaves every net — so the latch that
        // makes the member read once-per-session must be dropped for all of
        // them, or the card would render a membership the radio no longer has.
        var vm = MemberSectionVm(MemberLine(1, "AAA"));

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");
        _prompt.Last.Complete(true);
        AnswerSentinel();                            // DELAD AAA goes out
        Assert.Contains("DELAD AAA", Transport.SentLines);
        Assert.Single(vm.MemberDisplayRows);
        Assert.Equal("—", vm.MemberDisplayRows[0].AddressText);

        AnswerSentinel();                            // the bracket closes
        InjectStationBook();
        AnswerSentinel();                            // the closing book read commits

        Assert.Contains("NETAD NT1", Transport.SentLines);
    }

    [Fact]
    public void AddMember_ARefusedSend_CarriesTheVocabularyTextExactlyOnce()
    {
        // R13: the vocabulary's own wording, no radio token, and exactly ONE
        // "Refused — " prefix (the doubling defect the round-10 log had).
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");

        vm.ActionCommand.Execute(null);
        AnswerSentinel();
        Transport.InjectLine(" INV MEMBER ADDR ");
        AnswerSentinel();

        Assert.Equal(AleRefusalVocabulary.Describe("INV MEMBER ADDR"), vm.OperationStatus);
        Assert.Equal("Refused — that member address does not exist on the radio", vm.OperationStatus);
        Assert.DoesNotContain("INV MEMBER ADDR", vm.OperationStatus, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(vm.OperationStatus, "Refused — "));
    }

    /// <summary>Occurrences of <paramref name="needle"/> — the double-prefix
    /// detector (a StartsWith cannot see a second prefix behind the
    /// first).</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void AddMember_AMappedRefusal_RendersItsTabledString()
    {
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");

        vm.ActionCommand.Execute(null);
        AnswerSentinel();
        Transport.InjectLine(" DUPLICATE MEMBER ");
        AnswerSentinel();

        Assert.Equal("Already a member of this net.", vm.OperationStatus);
    }

    [Fact]
    public void AddMember_WhenTheGateIsBusy_SendsNothing_AndNamesTheReason()
    {
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");
        vm.ActionCommand.Execute(null);             // the gate is now open
        Assert.Equal("", vm.InputError);             // …the Add really ran
        Transport.ClearSent();

        vm.ActionCommand.Execute(null);

        Assert.Equal(AleProgrammingGate.BusyReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheMemberPicker_OffersIndividualsAndTHISNetsOwnSelf_AndNoOtherSelf()
    {
        // §5's picker constraint, unchanged by round 15 E-1. The radio refuses
        // any other self (" INV SELF MEMBER "), so the offer must not carry
        // one: the fixture book has TWO selfs and NT1's own is TST, so ZZZ is
        // the one that proves the constraint rather than an accident of
        // ordering.
        var vm = MemberSectionVm(NoMembersProgrammed);

        Assert.Equal(["AAA", "BBB", "TST"], vm.MemberChoices.Select(c => c.Address));
        Assert.DoesNotContain(vm.MemberChoices, c => c.Address == "ZZZ");

        // E-D3: each candidate carries its WIRE identity and its group;
        // the DISPLAY is the name alone (owner 2026-08-23 — under the strict
        // S7 filter the group suffix repeated the net's group on every line).
        // (Every fixture address here is in group 01, NT1's own, so the S7
        // filter changes nothing about this pin.)
        Assert.Equal(["AAA", "BBB", "TST"], vm.MemberChoices.Select(c => c.Display));
        Assert.All(vm.MemberChoices, c => Assert.Equal(1, c.ChannelGroup));
    }

    // ---- The picker's CHANNEL-GROUP filter (round 16 fixes S7) ------------
    // Owner policy 2026-08-23, STRICT: a candidate is offered iff its channel
    // group equals the PICKED NET's. The radio itself still ACCEPTS a
    // cross-group member (bench 2026-08-01, negative controlled) — that is
    // unchanged wire fact; what changed is what the app OFFERS, because a
    // member in another group will not scan with the net.

    [Fact]
    public void TheMemberPicker_OffersOnlyTheNetsChannelGroup()
    {
        var vm = ImpactLoadedVm();
        RereadBook(vm,
            "SLFAD TST               CHGROUP 01",
            "INDAD A01               CHGROUP 01   ASSOC SELF TST",
            "INDAD B02               CHGROUP 02   ASSOC SELF TST",
            "INDAD C02               CHGROUP 02   ASSOC SELF TST",
            "NETAD NT1               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT2               CHGROUP 02   ASSOC SELF TST");

        Pick(vm, "Member");
        vm.NetPick = "NT2";                          // the net is GROUP 02

        // Only the 02 individuals, in BOOK ORDER, displayed by NAME alone.
        Assert.Equal(["B02", "C02"], vm.MemberChoices.Select(c => c.Address));
        Assert.Equal(["B02", "C02"], vm.MemberChoices.Select(c => c.Display));
        Assert.DoesNotContain(vm.MemberChoices, c => c.Address == "A01");

        // RE-PICKING the net re-keys the whole offer: a member picked under the
        // 02 net is not a candidate for the 01 one, so the selection clears
        // exactly as a vanished candidate's does.
        vm.MemberPick = vm.MemberChoices.Single(c => c.Address == "B02");
        vm.NetPick = "NT1";

        Assert.Equal(["A01", "TST"], vm.MemberChoices.Select(c => c.Address));
        Assert.Null(vm.MemberPick);
    }

    [Fact]
    public void TheMemberPicker_OffersTheAssociatedSelfOnlyInTheNetsGroup()
    {
        // BOTH halves of the self rule, in one fact. A net whose associated
        // self sits in ANOTHER group offers no self at all — that self will not
        // scan with the net either — while the group's individuals still are.
        var vm = ImpactLoadedVm();
        RereadBook(vm,
            "SLFAD S01               CHGROUP 01",
            "INDAD B02               CHGROUP 02   ASSOC SELF S01",
            "NETAD NT2               CHGROUP 02   ASSOC SELF S01");

        Pick(vm, "Member");
        vm.NetPick = "NT2";
        Assert.Equal(["B02"], vm.MemberChoices.Select(c => c.Address));

        // Drain the targeted read the pick fired, so the re-read below is not
        // credited to it (one NETAD operation at a time).
        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();

        // …and the SAME self, now in the net's own group, is offered — LAST,
        // after the individuals, as it always has been.
        RereadBook(vm,
            "SLFAD S01               CHGROUP 02",
            "INDAD B02               CHGROUP 02   ASSOC SELF S01",
            "NETAD NT2               CHGROUP 02   ASSOC SELF S01");

        Assert.Equal(["B02", "S01"], vm.MemberChoices.Select(c => c.Address));
        Assert.Equal(["B02", "S01"], vm.MemberChoices.Select(c => c.Display));
    }

    [Fact]
    public void EveryNewBoundProperty_NOTIFIES_WhenTheThingItRendersMoves()
    {
        // The NOTIFICATION matrix for round 15 E's new bindings. A computed
        // property that never raises renders once and then lies: the card
        // would keep the previous kind's sections and button text on screen,
        // and no other test in this file can see it (the VM's VALUES are all
        // correct either way).
        var vm = MemberSectionVm(NoMembersProgrammed);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Pick(vm, "Individual");
        foreach (var name in new[]
        {
            nameof(AleProgrammingViewModel.Kind),
            nameof(AleProgrammingViewModel.ActionText),
            nameof(AleProgrammingViewModel.IsSelfKind),
            nameof(AleProgrammingViewModel.ShowAddressFields),
            nameof(AleProgrammingViewModel.ShowAssociatedSelf),
            nameof(AleProgrammingViewModel.ShowMemberSection),
            nameof(AleProgrammingViewModel.ShowSelfGateHint),
        })
            Assert.Contains(name, raised);

        // E-5: picking a self SETS the group wheel to that self's group, and
        // both the seat and its display have to say so. ZZZ is group 00 and
        // the wheel starts at 1, so the move is real.
        raised.Clear();
        Assert.Equal(1, vm.GroupSelection);
        vm.AssociatedSelfSelection = "ZZZ";
        Assert.Equal(0, vm.GroupSelection);
        Assert.Contains(nameof(AleProgrammingViewModel.AssociatedSelfSelection), raised);
        Assert.Contains(nameof(AleProgrammingViewModel.GroupSelection), raised);
        Assert.Contains(nameof(AleProgrammingViewModel.GroupText), raised);

        raised.Clear();
        Pick(vm, "Member");
        vm.NetPick = null;
        foreach (var name in new[]
        {
            nameof(AleProgrammingViewModel.NetPick),
            nameof(AleProgrammingViewModel.CanPickMember),
            nameof(AleProgrammingViewModel.ShowPickANetFirst),
            nameof(AleProgrammingViewModel.MemberChoices),
        })
            Assert.Contains(name, raised);

        raised.Clear();
        vm.NetPick = "NT1";
        vm.MemberPick = vm.MemberChoices.Single(c => c.Address == "AAA");
        Assert.Contains(nameof(AleProgrammingViewModel.MemberPick), raised);
    }

    /// <summary>A book RE-READ that lands whatever lines are given, on the
    /// Program tab's editor tier — the gesture that legitimately rebuilds the
    /// pickers' offers.</summary>
    private void RereadBook(AleProgrammingViewModel vm, params string[] lines)
    {
        vm.OpenProgramTabCommand.Execute(null);
        foreach (var line in lines) Transport.InjectLine(line);
        AnswerSentinel();
    }

    [Fact]
    public void AStillValidPick_IsREMAPPED_IntoTheRebuiltOffer()
    {
        // AUDIT ROUND 2 (MINOR). When the offer changes for a reason that has
        // NOTHING to do with the pick — a re-read introduces an unrelated
        // individual — the list is rebuilt, and the operator's still-valid
        // pick must be re-pointed at the EQUAL candidate in the new list. An
        // instance that is no longer IN the ItemsSource is not a selection:
        // the two-way Picker resolves it by clearing the visible one and
        // disabling Add. The house idiom is MessagesViewModel.Targets'.
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");
        var before = vm.MemberPick;
        Assert.True(vm.ActionCommand.CanExecute(null));

        // CCC is new and irrelevant to the pick; AAA is still offered. (CCC is
        // in NT1's OWN group 01 — round 16 fixes S7: this test is about an
        // unrelated arrival REBUILDING the offer, so its newcomer has to be one
        // the strictly filtered picker still offers. One fixture literal;
        // every assertion below is unchanged.)
        RereadBook(vm,
            "SLFAD ZZZ               CHGROUP 00",
            "SLFAD TST               CHGROUP 01",
            "INDAD AAA               CHGROUP 01   ASSOC SELF TST",
            "INDAD BBB               CHGROUP 01   ASSOC SELF TST",
            "INDAD CCC               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT1               CHGROUP 01   ASSOC SELF TST");

        Assert.Contains(vm.MemberChoices, c => c.Address == "CCC");   // it really rebuilt
        Assert.NotSame(before, vm.MemberPick);                        // …with new instances
        Assert.NotNull(vm.MemberPick);
        Assert.Equal("AAA", vm.MemberPick!.Address);

        // THE POINT: the pick is the object the Picker's ItemsSource holds.
        Assert.Same(vm.MemberChoices.Single(c => c.Address == "AAA"), vm.MemberPick);
        Assert.Contains(vm.MemberPick, vm.MemberChoices);
        Assert.True(vm.ActionCommand.CanExecute(null));

        // …and the net pick and the associated-self pick get the same
        // treatment against their own offers.
        Assert.Equal("NT1", vm.NetPick);
        Assert.Contains(vm.NetPick!, vm.NetChoices);
    }

    [Fact]
    public void APickTheRereadDELETED_GoesNull_AndTheActionIsDisabled()
    {
        // The negative half. A remap that "found" something for a pick the
        // radio no longer reports would send an address that is gone.
        var vm = MemberSectionVm(NoMembersProgrammed);
        PickMember(vm, "AAA");
        Assert.True(vm.ActionCommand.CanExecute(null));

        RereadBook(vm,
            "SLFAD ZZZ               CHGROUP 00",
            "SLFAD TST               CHGROUP 01",
            "INDAD BBB               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT1               CHGROUP 01   ASSOC SELF TST");

        Assert.DoesNotContain(vm.MemberChoices, c => c.Address == "AAA");
        Assert.Null(vm.MemberPick);
        Assert.False(vm.ActionCommand.CanExecute(null));

        // Execute ignores CanExecute, so the body refuses too — and sends
        // nothing at all.
        Transport.ClearSent();
        vm.ActionCommand.Execute(null);
        Assert.Equal("Pick a member address.", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheNetAndSelfPicks_FollowTheSameRule_AgainstTheirOwnOffers()
    {
        // The two STRING pickers. Same rule, same reason: a Picker cannot
        // render a value outside its ItemsSource, so a pick the radio stopped
        // reporting is dropped rather than left pointing at nothing.
        var vm = ReadyVm();
        Pick(vm, "Individual");
        PickAssociatedSelf(vm, "TST");
        Pick(vm, "Member");
        vm.NetPick = "NT1";
        Assert.Equal("TST", vm.AssociatedSelfSelection);

        // A re-read that keeps BOTH picked names alive, while changing the
        // offers around them, remaps rather than drops.
        RereadBook(vm,
            "SLFAD ZZZ               CHGROUP 00",
            "SLFAD TST               CHGROUP 01",
            "SLFAD QQQ               CHGROUP 04",
            "INDAD AAA               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT1               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT2               CHGROUP 01   ASSOC SELF TST");

        Assert.Contains("QQQ", vm.SelfChoices);
        Assert.Contains("NT2", vm.NetChoices);
        Assert.Equal("TST", vm.AssociatedSelfSelection);
        Assert.Equal("NT1", vm.NetPick);

        // …and a re-read that REMOVES them drops both.
        RereadBook(vm,
            "SLFAD ZZZ               CHGROUP 00",
            "INDAD AAA               CHGROUP 01   ASSOC SELF ZZZ",
            "NETAD NT2               CHGROUP 01   ASSOC SELF ZZZ");

        Assert.Null(vm.AssociatedSelfSelection);
        Assert.Null(vm.NetPick);
        Assert.False(vm.ActionCommand.CanExecute(null));
    }

    [Fact]
    public void AnEMPTYOffer_ReSelectsNOTHING_TheReconnectExemption()
    {
        // The rule's one exception, pinned where the rule lives. A session
        // drop clears the whole ALE mirror, so every offer empties at once —
        // and an empty offer carries NO information about a pick: it means the
        // radio has told us nothing, not that it says the name is gone. The
        // standing pin is that typed buffers and SELECTIONS survive a
        // reconnect (plan §5/§7.10), and a remap that fired on emptiness would
        // silently break it. (Reconnect_PreservesTypedTextAndSelections is the
        // same contract from the other end; it caught this while it was
        // wrong.)
        var vm = ReadyVm();
        Pick(vm, "Individual");
        PickAssociatedSelf(vm, "TST");
        Pick(vm, "Member");
        vm.NetPick = "NT1";

        // The drop, then the reconnect that CLEARS the mirror (Core's
        // ResetForConnect) — the window before the re-read has answered.
        Session.Close();
        ConnectReady();

        Assert.Empty(vm.SelfChoices);          // …every offer is empty…
        Assert.Empty(vm.NetChoices);
        Assert.Empty(vm.MemberChoices);
        Assert.Equal("TST", vm.AssociatedSelfSelection);   // …and the picks stand
        Assert.Equal("NT1", vm.NetPick);

        // …and when the re-read ANSWERS, the offers are real again and the
        // picks are still there — the rule resumes on information, not on
        // absence.
        Transport.InjectLine("ALE>");
        InjectStationBook();
        AnswerSentinel();

        Assert.Contains("TST", vm.SelfChoices);
        Assert.Equal("TST", vm.AssociatedSelfSelection);
        Assert.Equal("NT1", vm.NetPick);
    }

    [Fact]
    public void ThePickersOffers_SurviveAMirrorEventThatChangedNothing()
    {
        // The rebuild-only-on-change rule, and WHY it is not cosmetic here: a
        // Picker re-evaluates its SelectedItem against a new ItemsSource, so a
        // candidate list rebuilt with fresh INSTANCES on every mirror event
        // would null the operator's member pick — the Add button going dead
        // under their finger every time any ALE line landed.
        //
        // AUDIT ROUND 1 (MAJOR) fixed this pin's PREMISE. Its first version
        // injected `ALE>` and a battery line, neither of which raises a
        // property THIS surface watches — so `Refresh` never ran,
        // `UpdateMemberChoices` was never reached, and the churn mutation the
        // test exists to catch stayed green. Every line below is chosen
        // because it DOES reach the real path: `SCAN STOPPED` raises
        // `AleLinkState`, and a membership answer raises `AleNetMembers` and
        // `AleMemberRead` — three watched properties, three real `Refresh`
        // laps, and none of them changes a self, an individual or a net.
        // A net with a MEMBER, deliberately: the read-empty projection assigns
        // the same cached empty array every lap, so it cannot witness a
        // refresh — the rows branch builds a fresh list and can.
        var vm = MemberSectionVm(MemberLine(1, "AAA"));
        PickMember(vm, "AAA");
        Assert.True(vm.ActionCommand.CanExecute(null));

        var nets = vm.NetChoices;
        var selfs = vm.SelfChoices;
        var members = vm.MemberChoices;
        var pick = vm.MemberPick;

        // The WITNESS that the path ran. `UpdateMemberSection` — the line
        // immediately after `UpdateMemberChoices` in `Refresh` — assigns
        // `MemberDisplayRows` a fresh collection on every lap, so its
        // notification counts REFRESHES. A "nothing changed" pin is worthless
        // unless the code it guards actually executed, and this is what the
        // audit found missing.
        int laps = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AleProgrammingViewModel.MemberDisplayRows)) laps++;
        };

        // (1) A LINK-STATE lap — a watched property, no book content at all.
        Transport.InjectLine("SCAN STOPPED");

        // (2) A MEMBERSHIP lap: NT1 re-read and answered with what it already
        // held. The offers are a function of selfs/individuals/nets, so this
        // is the sharpest "changed nothing about the offers" event there is.
        _ale.RequestNetMembers("NT1");
        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();

        // The path really ran — otherwise every assertion below is vacuous.
        Assert.True(laps >= 2, $"the VM refreshed {laps} times — the pin needs the real path");

        Assert.Same(nets, vm.NetChoices);
        Assert.Same(selfs, vm.SelfChoices);
        Assert.Same(members, vm.MemberChoices);
        Assert.Same(pick, vm.MemberPick);
        Assert.Equal("AAA", vm.MemberPick!.Address);
        Assert.Equal("NT1", vm.NetPick);
        Assert.True(vm.ActionCommand.CanExecute(null));

        // …and a book that really DOES change the offer rebuilds it, and drops
        // a pick the radio no longer holds.
        vm.OpenProgramTabCommand.Execute(null);   // an editor landing re-reads
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD BBB               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        AnswerSentinel();                         // …and AAA is not in it

        Assert.Equal(["BBB", "TST"], vm.MemberChoices.Select(c => c.Address));
        Assert.Null(vm.MemberPick);               // AAA is gone from the book
    }

    [Fact]
    public void ACrossGroupCandidate_IsNotOffered_AndNothingIsSent()
    {
        // ROUND 16 FIXES S7, replacing this pin's opposite (owner policy
        // 2026-08-23, STRICT). The RECORDED device fact is unchanged — the
        // radio ACCEPTS a cross-group member (bench 2026-08-01, negative
        // controlled; protocol.md's ADDM row refuses by KIND only) — and a
        // mismatched member added from the FRONT PANEL still shows in the
        // membership table. What changed is the app's OFFER policy: a member in
        // another channel group will not scan with the net, so it is not
        // offered at all.
        var vm = ImpactLoadedVm();
        vm.OpenProgramTabCommand.Execute(null);
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD XG9               CHGROUP 07   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        AnswerSentinel();

        Pick(vm, "Member");
        vm.NetPick = "NT1";                          // the net is GROUP 01
        Transport.ClearSent();

        // Not offered — so there is nothing to select…
        Assert.DoesNotContain(vm.MemberChoices, c => c.Address == "XG9");
        Assert.Equal(["TST"], vm.MemberChoices.Select(c => c.Address));   // only the net's own self is in 01

        // …and the press with nothing picked writes nothing.
        vm.MemberPick = null;
        Assert.False(vm.ActionCommand.CanExecute(null));
        vm.ActionCommand.Execute(null);

        Assert.DoesNotContain("ADDM NT1 XG9", Transport.SentLines);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheNetKind_ShowsNOMemberSection()
    {
        // E-Q2: the section MOVED to the Member kind. The Net kind programs
        // the net and nothing else — and its picks reach no membership at all.
        var vm = ReadyVm();

        Pick(vm, "Net");
        Assert.False(vm.ShowMemberSection);
        Assert.True(vm.ShowAssociatedSelf);
        Assert.True(vm.ShowAddressFields);
        Assert.Equal("Program", vm.ActionText);

        Pick(vm, "Member");
        Assert.True(vm.ShowMemberSection);
        Assert.False(vm.ShowAssociatedSelf);
        Assert.False(vm.ShowAddressFields);          // no Name, no group wheel
        Assert.Equal("Add", vm.ActionText);
    }

    [Fact]
    public void TheStandingEraseWarning_IsGONEFromTheVm()
    {
        // E-3: the framed warning is deleted, and so is the constant that fed
        // it — a static left behind would be an invitation to re-hang it.
        // Anti-vacuity: the popup's own strings are still here.
        var type = typeof(AleProgrammingViewModel);

        Assert.Null(type.GetField("EraseWarningText"));
        Assert.NotNull(type.GetField("EraseTitle"));
        Assert.NotNull(type.GetField("EraseMessage"));
        Assert.NotNull(type.GetField("EraseAccept"));
    }

    [Fact]
    public void TheMemberPicker_WithNoNetPicked_IsEmptyAndTheAddIsDisabled()
    {
        // E-1: the offer is a property OF the net, so there is nothing to
        // offer until one is picked — and the caption says so rather than
        // leaving an empty control to be interpreted.
        var vm = ReadyVm();
        Pick(vm, "Member");

        Assert.Empty(vm.MemberChoices);
        Assert.False(vm.CanPickMember);
        Assert.True(vm.ShowPickANetFirst);
        Assert.False(vm.ActionCommand.CanExecute(null));

        // Execute ignores CanExecute, so the body refuses too — and sends
        // nothing.
        vm.ActionCommand.Execute(null);
        Assert.Equal("Pick a net.", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheMemberPicker_WithANetButNoMemberPicked_RefusesAndSendsNothing()
    {
        var vm = MemberSectionVm(NoMembersProgrammed);

        Assert.True(vm.CanPickMember);
        Assert.False(vm.ShowPickANetFirst);
        Assert.Null(vm.MemberPick);
        Assert.False(vm.ActionCommand.CanExecute(null));

        vm.ActionCommand.Execute(null);
        Assert.Equal("Pick a member address.", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheMemberPicker_OnAnEmptyBook_OffersNothingAtAll()
    {
        // No book = no nets to pick and no addresses to offer. Nothing is
        // invented on either side.
        var vm = EmptyBookVm();
        Pick(vm, "Member");

        Assert.Empty(vm.NetChoices);
        Assert.Empty(vm.MemberChoices);
        Assert.False(vm.ActionCommand.CanExecute(null));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NoRemoveMember_SurvivesAnywhereOnTheVm()
    {
        // §5's absence pin, with its anti-vacuity partner: there is no
        // remove-member verb on the wire, and the ADD that does exist is
        // still here.
        var type = typeof(AleProgrammingViewModel);

        Assert.Null(type.GetProperty("RemoveMemberCommand"));
        Assert.Null(type.GetProperty("DeleteMemberCommand"));
        Assert.Null(type.GetProperty("MemberLog"));            // the log is gone too
        Assert.Null(type.GetField("MemberLogCaption"));
        Assert.Null(type.GetField("SelfGateCaption"));
        Assert.Null(type.GetField("SelfGateCaptionLine1"));
        Assert.Null(type.GetField("SelfGateCaptionLine2"));

        // ROUND 15 E-D2: the two write commands became ONE seat, so the
        // anti-vacuity half moves with them — the card can still ADD, and the
        // deleted names are gone rather than shadowed.
        Assert.Null(type.GetProperty("AddMemberCommand"));
        Assert.Null(type.GetProperty("ProgramCommand"));
        Assert.NotNull(type.GetProperty("ActionCommand"));
        Assert.NotNull(type.GetProperty("MemberDisplayRows"));
        Assert.Null(Type.GetType("Falcon.App.Core.ViewModels.AleMemberLogRow, Falcon.App.Core"));
        Assert.NotNull(Type.GetType("Falcon.App.Core.ViewModels.AleMemberRow, Falcon.App.Core"));
    }

    // ==== The book tab =====================================================

    [Fact]
    public void BookRows_RenderEveryKind_SelfsIncluded_WithChipsAndDashes()
    {
        var vm = ReadyVm();

        Assert.Equal(
            ["SELF ZZZ 00 —", "SELF TST 01 —", "IND AAA 01 TST", "IND BBB 01 TST", "NET NT1 01 TST"],
            vm.BookRows.Select(r => $"{r.KindText} {r.NameText} {r.GroupText} {r.AssociatedSelfText}"));
        Assert.False(vm.HasNoBookRows);
    }

    [Fact]
    public void ANetWhoseAssociatedSelfWasCascadedAway_RendersADash()
    {
        var vm = ReadyVm();
        vm.OpenBookTabCommand.Execute(null);
        Transport.InjectLine("NETAD NT1               CHGROUP 01");   // no ASSOC SELF segment
        AnswerSentinel();

        var net = Assert.Single(vm.BookRows, r => r.NameText == "NT1");
        Assert.Equal("—", net.AssociatedSelfText);
    }

    // ==== §5 — the POPUP confirmations, per caller, BOTH answers ===========
    // Round 10 §5 replaced the inline confirm boxes with two-button popups
    // through IConfirmationPrompt. Every pin below is the LIFECYCLE CONTRACT:
    // capture at press · cancel sends nothing · accept sends ONCE against the
    // CAPTURED target · a gate lost while the prompt is open sends nothing ·
    // a faulted or cancelled prompt sends nothing and does not wedge · every
    // completed press re-prompts.

    // ---- §5's THREE prompt rows, byte for byte ---------------------------
    // The fixture book lists ZZZ FIRST, so ZZZ is the PRIMARY self and TST is
    // a secondary — the distinction the round-10 single self prompt could not
    // make and this round's PRIMARY-SELF model does.

    [Fact]
    public void DeleteAnIndividual_AsksTheExactPromptTableStrings()
    {
        var vm = ImpactLoadedVm();
        var row = vm.BookRows.Single(r => r.NameText == "AAA");

        row.Delete.Execute(row.NameText);

        var prompt = _prompt.Last;
        Assert.Equal("Delete AAA?", prompt.Title);
        Assert.Equal("The radio removes this address from its book.", prompt.Message);
        Assert.Equal("Delete", prompt.AcceptText);
        Assert.Equal("Cancel", prompt.CancelText);
        Assert.Empty(Transport.SentLines);          // asking sends NOTHING

        // Every raised prompt is ANSWERED before the test ends. xunit waits
        // for the async-void continuation ICommand.Execute starts, so leaving
        // one open wedges the RUN — which is also the cleanest proof that the
        // command really is awaiting this task and not fire-and-forgetting it.
        prompt.Complete(false);
    }

    [Fact]
    public void DeleteANet_AsksTheSameRowAsAnIndividual()
    {
        // §5's table puts individuals and nets on ONE row: neither has
        // dependants of its own.
        var vm = ImpactLoadedVm();

        vm.BookRows.Single(r => r.NameText == "NT1").Delete.Execute("NT1");

        Assert.Equal("Delete NT1?", _prompt.Last.Title);
        Assert.Equal("The radio removes this address from its book.", _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void DeleteASECONDARYSelf_NamesThePrimaryItsDependantsMoveTo()
    {
        var vm = ImpactLoadedVm();

        vm.BookRows.Single(r => r.NameText == "TST").Delete.Execute("TST");

        Assert.Equal("Delete self TST?", _prompt.Last.Title);
        Assert.Equal("The radio re-points its individuals and nets at the primary self ZZZ.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void DeleteThePRIMARYSelf_IsTheDestructiveRow_AndClaimsNoRecovery()
    {
        var vm = ImpactLoadedVm();

        vm.BookRows.Single(r => r.NameText == "ZZZ").Delete.Execute("ZZZ");

        Assert.Equal("Delete the primary self ZZZ?", _prompt.Last.Title);

        // ROUND 15 E-4 (critic F46) CORRECTS THE DEVICE FACT. The individuals
        // are NOT deleted — they are orphaned and INVISIBLE until a new 1–3
        // character self is programmed, at which point every one of them comes
        // back re-pointed (docs/protocol.md, the PRIMARY-SELF MODEL row,
        // corrected 2026-08-18). The message used to say "Destructive: its
        // individuals are deleted", which was the disproved 2026-08-17 reading.
        Assert.Equal(
            "The radio hides its individuals until a new 1–3 character self is "
            + "programmed, blanks its nets' self and stops scanning.",
            _prompt.Last.Message);
        Assert.DoesNotContain("deleted", _prompt.Last.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Destructive", _prompt.Last.Message, StringComparison.Ordinal);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void TheThreePromptRows_AreGenuinelyDifferent_TitleAndMessage()
    {
        // Anti-vacuity for the three pins above: a mutation that collapsed two
        // rows into one would satisfy each pin's own literal only if the
        // literals were equal — this says they are not.
        var vm = ImpactLoadedVm();
        var seen = new List<(string Title, string Message)>();

        foreach (var name in new[] { "AAA", "TST", "ZZZ" })
        {
            vm.BookRows.Single(r => r.NameText == name).Delete.Execute(name);
            seen.Add((_prompt.Last.Title, _prompt.Last.Message));
            _prompt.Last.Complete(false);
        }

        Assert.Equal(3, seen.Select(p => p.Title).Distinct().Count());
        Assert.Equal(3, seen.Select(p => p.Message).Distinct().Count());
    }

    // ---- §5's IMPACT grammar: all five rendered examples ------------------

    [Fact]
    public void Impact_NONE_AppendsNothingAtAll()
    {
        var vm = ImpactLoadedVm(members: [NoMembersProgrammed], schedules: [NoLqaScheduled]);

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Equal("The radio removes this address from its book.", _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_MEMBERONLY_ListsTheNetsInMirrorOrder()
    {
        var vm = ImpactLoadedVm(members: [MemberLine(1, "AAA")], schedules: [NoLqaScheduled]);

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Equal(
            "The radio removes this address from its book.\nMember of: NT1.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_SCHEDULEONLY_SaysTheQueuedRowGoesToo()
    {
        var vm = ImpactLoadedVm(members: [NoMembersProgrammed], schedules: [ExchangeRow("AAA")]);

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Equal(
            "The radio removes this address from its book."
            + "\nIts queued LQA schedule is removed too.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_BOTH_RendersMembershipFirst_ThenTheSchedule()
    {
        // The ORDER is part of the grammar, not an accident of which mirror
        // answered first.
        var vm = ImpactLoadedVm(members: [MemberLine(1, "AAA")], schedules: [ExchangeRow("AAA")]);

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Equal(
            "The radio removes this address from its book."
            + "\nMember of: NT1."
            + "\nIts queued LQA schedule is removed too.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_MemberOf_JoinsEVERYHoldingNet_InTheBooksOwnOrder()
    {
        // Two nets, listed in the RADIO's order (NT2 before NT1 here, which is
        // not alphabetical — a sort would show).
        var vm = ReadyVm();
        vm.OpenBookTabCommand.Execute(null);
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NT2               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF ZZZ");
        AnswerSentinel();                            // the book commits

        // ROUND 15 D re-key. The landing queued a read for the net the OLD
        // mirror held (NT1); the book it just committed introduced a SECOND
        // net, and until D that one was loaded by the DELETE PRESS. The
        // cold-landing rule (§14.2) now reads every net the book answer
        // introduces, so NT2 loads HERE — the press's prerequisite tier is
        // unchanged and simply finds nothing left to read. What this test is
        // about — the composed "Member of" line over EVERY holding net, in the
        // BOOK's order — is untouched.
        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();                            // NT1 committed → NT2 dispatches
        Assert.Contains("NETAD NT2", Transport.SentLines);

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();                            // NT2 committed
        LoadSchedules(NoLqaScheduled);
        Transport.ClearSent();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");
        Assert.Empty(Transport.SentLines);           // both mirrors already loaded
        Assert.Equal(1, _prompt.CallCount);          // …so the question opens at once

        Assert.Equal(
            "The radio removes this address from its book.\nMember of: NT2, NT1.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_FAULTEDSCHEDULEREAD_SaysSo_AndTheQuestionStillOpens()
    {
        // §5: "a FAULTED prerequisite read produces the fault variant, never
        // silence". The membership mirror is loaded, so the fault names the
        // half that failed and nothing else.
        var vm = ReadyVm();
        LandOnBookTab(vm, NoMembersProgrammed);      // membership: loaded
        Radio.Ale.RefreshTimeoutMs = 80;             // …schedules: never answered
        Transport.ClearSent();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.True(WaitUntil(() => _prompt.CallCount == 1, 10_000),
            "a faulted prerequisite read must still open the prompt");
        Assert.Equal(
            "The radio removes this address from its book."
            + "\nImpact unknown (schedules read failed).",
            _prompt.Last.Message);
        Assert.Contains("EXCH", Transport.SentLines);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void Impact_BOTHREADSFAULTED_NamesBothHalves()
    {
        var vm = ReadyVm();
        Radio.Ale.RefreshTimeoutMs = 80;             // nothing will answer
        Transport.ClearSent();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.True(WaitUntil(() => _prompt.CallCount == 1, 10_000));
        Assert.Equal(
            "The radio removes this address from its book."
            + "\nImpact unknown (membership and schedules read failed).",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void TheImpactWords_AreTheThreeTabledOnes()
    {
        // The {what} vocabulary, as a closed set — so a fourth spelling cannot
        // arrive without this failing.
        Assert.Equal("membership", AleProgrammingViewModel.ImpactMembershipWord);
        Assert.Equal("schedules", AleProgrammingViewModel.ImpactSchedulesWord);
        Assert.Equal("membership and schedules", AleProgrammingViewModel.ImpactBothWord);
        Assert.Equal("\nMember of: {0}.", AleProgrammingViewModel.ImpactMemberOfFormat);
        Assert.Equal("\nIts queued LQA schedule is removed too.",
            AleProgrammingViewModel.ImpactScheduleLine);
        Assert.Equal("\nImpact unknown ({0} read failed).",
            AleProgrammingViewModel.ImpactUnknownFormat);

        // {1} is the primary's name — the secondary message is the ONE row
        // that interpolates it, and it must not lose the placeholder.
        Assert.Contains("{1}", AleProgrammingViewModel.DeleteSecondarySelfMessageFormat,
            StringComparison.Ordinal);
    }

    // ---- §5's PREREQUISITE-LOADED press ----------------------------------

    [Fact]
    public void ADeletePress_LOADSTheImpactMirrorsFIRST_AndAsksOnlyOnCompletion()
    {
        // The press fires the reads and does NOT ask; the question appears
        // when they land, carrying what they said.
        var vm = ReadyVm();               // neither mirror loaded

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Equal(0, _prompt.CallCount);                 // nothing asked yet…
        Assert.Equal(["NETAD NT1", "BAT ST", "EXCH"], Transport.SentLines);
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("DELAD", StringComparison.Ordinal));

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();                                   // membership lands
        Assert.Equal(0, _prompt.CallCount);                 // …still not: EXCH is out

        Transport.InjectLine(ExchangeRow("AAA"));
        AnswerSentinel();                                   // schedules land

        Assert.Equal(1, _prompt.CallCount);
        Assert.Equal(
            "The radio removes this address from its book."
            + "\nMember of: NT1."
            + "\nIts queued LQA schedule is removed too.",
            _prompt.Last.Message);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void ADeletePress_ReadsNothingItAlreadyHas()
    {
        // The other half of the tier: mirrors already loaded means the press
        // sends NOTHING and asks at once.
        var vm = ImpactLoadedVm();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");

        Assert.Empty(Transport.SentLines);
        Assert.Equal(1, _prompt.CallCount);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void ASecondDeletePress_WhileTheFirstIsStillLoading_DoesNothing()
    {
        // §5's re-entrancy rule: ONE pending open at a time. A second press
        // must not queue a second read pass, and must not produce a second
        // question when the reads land.
        var vm = ReadyVm();
        var aaa = vm.BookRows.Single(r => r.NameText == "AAA");
        var bbb = vm.BookRows.Single(r => r.NameText == "BBB");

        aaa.Delete.Execute("AAA");
        var afterFirst = Transport.SentLines.ToList();

        bbb.Delete.Execute("BBB");                          // ignored entirely
        Assert.Equal(afterFirst, Transport.SentLines);

        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();
        Transport.InjectLine(NoLqaScheduled);
        AnswerSentinel();

        Assert.Equal(1, _prompt.CallCount);
        Assert.Equal("Delete AAA?", _prompt.Last.Title);    // the FIRST press's target
        _prompt.Last.Complete(false);

        // …and the latch really does release: the next press asks again.
        bbb.Delete.Execute("BBB");
        Assert.Equal(2, _prompt.CallCount);
        Assert.Equal("Delete BBB?", _prompt.Last.Title);
        _prompt.Last.Complete(false);
    }

    [Fact]
    public void ADeletePressWaitingOnItsReads_DoesNotWEDGEWhenTheSessionDrops()
    {
        // A drop kills the prerequisite reads. §5 says a faulted prerequisite
        // produces the FAULT VARIANT, never silence — so the question still
        // arrives, saying what it could not establish — and §5's lifecycle
        // contract does the rest: answering it sends nothing, and the press
        // latch releases so the next one asks again.
        var vm = ReadyVm();

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");
        Assert.Equal(0, _prompt.CallCount);          // still loading

        Session.Close();

        Assert.Equal(1, _prompt.CallCount);
        Assert.Contains("Impact unknown (membership and schedules read failed).",
            _prompt.Last.Message, StringComparison.Ordinal);

        Transport.ClearSent();
        _prompt.Last.Complete(true);                 // answered YES, too late
        Assert.Empty(Transport.SentLines);           // the post-await gate held

        // …and the latch went with it: after a reconnect the card asks again.
        ConnectReady();
        Transport.InjectLine("ALE>");
        InjectStationBook();
        AnswerSentinel();
        Transport.ClearSent();

        Press(vm, "delete-individual");
        Assert.Equal(2, _prompt.CallCount);
        _prompt.Last.Complete(false);
    }

    // ==== ERASE ============================================================

    [Fact]
    public void Erase_AsksTheExactPromptTableStrings()
    {
        var vm = ReadyVm();

        vm.EraseCommand.Execute(null);

        var prompt = _prompt.Last;
        Assert.Equal("Erase every ALE address?", prompt.Title);
        // ROUND 15 E-3/E-Q3: the two-sentence form. It names net MEMBERSHIP —
        // which only the deleted framed warning used to carry — and the three
        // things that survive.
        Assert.Equal(
            "The radio clears every self, individual, net, net membership and LQA "
            + "schedule. Channel groups, stored messages and settings survive.",
            prompt.Message);
        Assert.Equal("Erase", prompt.AcceptText);
        Assert.Equal("Cancel", prompt.CancelText);
        Assert.Empty(Transport.SentLines);
        prompt.Complete(false);
    }

    // ==== The §5 LIFECYCLE MATRIX — every consumer × every leg =============
    //
    // Audit round 1 found the ad-hoc version of these pins covered the legs
    // UNEVENLY: the SELF-delete branch pinned only its prompt text and its
    // cancellation, so a mutation that accepted the SELF prompt and then
    // returned WITHOUT SENDING survived all 1007 tests; and the erase family
    // never drove a PENDING prompt while the ALE WRITE gate was lost, so
    // dropping Erase's post-await CanWrite re-check could have put ERASE on
    // the wire in a prohibited mode with the suite green.
    //
    // The fix is the FAMILY, not the two instances: this card's popup
    // consumers are driven through EVERY leg of the §5 contract by one theory
    // set, so a new consumer is one row and cannot arrive half-covered. (The
    // other VM-level consumer, HOP's Clear net, has the same matrix over its
    // own VM in HopSettingsViewModelTests.)
    //
    // The while-open legs drive the PENDING HANDLE — never EnqueueAnswer —
    // because the whole point is that state changes BETWEEN the press and the
    // answer.

    /// <summary>The FIVE popup consumers on this card, each as "identity · the
    /// wire line an accepted press must produce". ROUND 11 §5 split the one
    /// self row into PRIMARY and SECONDARY and gave nets their own cell, so the
    /// matrix grew from three rows to five — every new prompt joins it, which
    /// is the invariant-2 requirement.</summary>
    public static TheoryData<string, string> PopupConsumers => new()
    {
        { "delete-individual", "DELAD AAA" },
        { "delete-net", "DELAD NT1" },
        { "delete-secondary-self", "DELAD TST" },
        { "delete-primary-self", "DELAD ZZZ" },
        { "erase", "ERASE" },
    };

    /// <summary>The book row each delete consumer presses; erase has none.</summary>
    private static string? RowOf(string consumer) => consumer switch
    {
        "delete-individual" => "AAA",
        "delete-net" => "NT1",
        "delete-secondary-self" => "TST",
        "delete-primary-self" => "ZZZ",
        "erase" => null,
        _ => throw new InvalidOperationException("unknown consumer: " + consumer),
    };

    /// <summary>Press the named consumer and settle its PREREQUISITE reads, so
    /// the question is genuinely on screen when the leg under test begins.
    /// <para>Round 11 §5 made the delete press read BEFORE it asks; that tier
    /// is pinned on its own above. Here it is only choreography — the helper
    /// asserts that nothing the settling drained was a WRITE, so "the press
    /// sent nothing" keeps its meaning.</para></summary>
    private void Press(AleProgrammingViewModel vm, string consumer)
    {
        int before = _prompt.CallCount;

        if (RowOf(consumer) is { } name)
        {
            var row = vm.BookRows.Single(r => r.NameText == name);
            row.Delete.Execute(row.NameText);
        }
        else
        {
            vm.EraseCommand.Execute(null);
        }

        for (int i = 0; i < 6 && _prompt.CallCount == before; i++) AnswerSentinel();
        Assert.True(_prompt.CallCount > before, "the press never raised its prompt: " + consumer);

        Assert.DoesNotContain(Transport.SentLines, l =>
            l.StartsWith("DELAD", StringComparison.Ordinal)
            || l.StartsWith("ERASE", StringComparison.Ordinal));
        Transport.ClearSent();          // the prerequisite READS are not the write
    }

    /// <summary>The complete transcript an ACCEPTED press puts on the wire: the
    /// gate's opening bracket, the write itself, then the closing book read
    /// that is the operator's verify.</summary>
    private static string[] AcceptedTranscript(string write)
        => ["BAT ST", write, "BAT ST", "SLFAD", "INDAD", "NETAD"];

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg1_Accept_SendsExactlyOnce_AgainstTheCapturedTarget(string consumer, string write)
    {
        var vm = ReadyVm();

        Press(vm, consumer);
        Assert.Equal(1, _prompt.CallCount);
        Assert.Empty(Transport.SentLines);            // …and asking sent nothing
        _prompt.Last.Complete(true);
        AnswerSentinel();

        Assert.Equal(AcceptedTranscript(write), Transport.SentLines);
        Assert.Equal(1, _prompt.CallCount);           // ONCE — no second ask, no second send
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg2_Cancel_SendsNothing(string consumer, string write)
    {
        _ = write;
        var vm = ReadyVm();

        Press(vm, consumer);
        _prompt.Last.Complete(false);

        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg3_SessionDropsWhileThePromptIsOpen_SendsNothing(string consumer, string write)
    {
        _ = write;
        var vm = ReadyVm();

        Press(vm, consumer);
        Assert.False(_prompt.Last.IsResolved);        // genuinely still open

        Session.Close();
        Transport.ClearSent();

        _prompt.Last.Complete(true);                  // answered YES, too late

        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg4a_AleModeLostWhileThePromptIsOpen_SendsNothing(string consumer, string write)
    {
        // The session is still Ready — what went is the CONFIRMED ALE mode, and
        // these are ALE-scoped writes. This is the body re-checking the same
        // gate the send path uses, after the await.
        _ = write;
        var vm = ReadyVm();

        Press(vm, consumer);
        Assert.False(_prompt.Last.IsResolved);

        Transport.InjectLine("SSB>");                 // the radio leaves ALE
        Transport.ClearSent();

        _prompt.Last.Complete(true);

        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);   // …still connected
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg4b_WriteGateLostWhileThePromptIsOpen_SendsNothing_AndNamesTheReason(
        string consumer, string write)
    {
        // Level TWO of the two-level gate: the card stays LIVE while the radio
        // scans, but the WRITE commands do not. A prompt opened before the scan
        // started must not smuggle a write past it.
        _ = write;
        var vm = ReadyVm();

        Press(vm, consumer);
        Assert.False(_prompt.Last.IsResolved);

        Transport.InjectLine("SCANNING");
        Transport.ClearSent();

        _prompt.Last.Complete(true);

        Assert.True(vm.AreControlsEnabled);           // level ONE is untouched…
        Assert.Empty(Transport.SentLines);            // …and level TWO held
        Assert.Equal(AleProgrammingViewModel.ScanningDisabledReason, vm.InputError);
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg5_AFaultedOrCancelledPrompt_SendsNothing_AndDoesNotWedge(
        string consumer, string write)
    {
        var vm = ReadyVm();

        Press(vm, consumer);
        _prompt.Last.Fault();
        Assert.Empty(Transport.SentLines);

        Press(vm, consumer);
        _prompt.Last.Cancel();
        Assert.Empty(Transport.SentLines);

        // NOT WEDGED: the very next press still asks, and still sends.
        Press(vm, consumer);
        _prompt.Last.Complete(true);
        AnswerSentinel();
        Assert.Equal(AcceptedTranscript(write), Transport.SentLines);
    }

    [Theory]
    [MemberData(nameof(PopupConsumers))]
    public void Leg6_EveryCompletedPress_RePrompts_WithNoAcceptedLatch(string consumer, string write)
    {
        var vm = ReadyVm();

        // A completed CANCEL re-arms…
        Press(vm, consumer);
        _prompt.Last.Complete(false);
        Press(vm, consumer);
        Assert.Equal(2, _prompt.CallCount);
        Assert.Empty(Transport.SentLines);

        // …and so does a completed ACCEPT: a destructive gesture never latches.
        _prompt.Last.Complete(true);
        AnswerSentinel();
        Assert.Equal(AcceptedTranscript(write), Transport.SentLines);
        Transport.ClearSent();

        Press(vm, consumer);
        Assert.Equal(3, _prompt.CallCount);
        Assert.Empty(Transport.SentLines);
        _prompt.Last.Complete(false);
    }

    /// <summary>Leg 7, the DELETE consumers only: the target is captured at
    /// PRESS, so what gets sent is what the operator pointed at — even if the
    /// radio re-reported the book while the question was on screen. For ERASE
    /// this cell is structurally EMPTY: it takes no target, so there is nothing
    /// a mid-flight mirror change could redirect.</summary>
    [Theory]
    [InlineData("delete-individual", "DELAD AAA")]
    [InlineData("delete-net", "DELAD NT1")]
    [InlineData("delete-secondary-self", "DELAD TST")]
    [InlineData("delete-primary-self", "DELAD ZZZ")]
    public void Leg7_TheTargetIsCapturedAtPress_AndSurvivesAMirrorChange(
        string consumer, string write)
    {
        var vm = ReadyVm();

        Press(vm, consumer);
        Assert.False(_prompt.Last.IsResolved);

        // The radio re-reports a DIFFERENT book while the prompt is open — the
        // rows the press came from are rebuilt out from under it.
        vm.OpenBookTabCommand.Execute(null);
        Transport.InjectLine("SLFAD QQQ               CHGROUP 02");
        Transport.InjectLine("INDAD ZZZ               CHGROUP 02   ASSOC SELF QQQ");
        AnswerSentinel();
        Transport.ClearSent();
        Assert.DoesNotContain(vm.BookRows, r => r.NameText is "AAA" or "TST" or "NT1");
        // ZZZ survives the re-read as an INDIVIDUAL — so a card that re-derived
        // the target (or its prompt row) at ANSWER time would now be pointing at
        // a different kind of address entirely.
        Assert.Contains(vm.BookRows, r => r.NameText == "ZZZ" && r.KindText == "IND");

        _prompt.Last.Complete(true);
        AnswerSentinel();

        Assert.Equal(AcceptedTranscript(write), Transport.SentLines);
    }

    [Fact]
    public void TheMatrixDrivesEveryConsumerOnThisCard_AndTheHelperReallyPresses()
    {
        // Anti-vacuity for the whole matrix, both halves. (1) The consumer list
        // is the card's COMPLETE set of popup callers — a fourth added to the
        // VM without a row here would leave a consumer untested, which is the
        // gap audit round 1 found. (2) Press() genuinely raises a prompt for
        // each of them: a helper whose switch fell through would make every
        // "sends nothing" leg pass for the wrong reason.
        var consumers = PopupConsumers.Select(row => (string)row[0]!).ToList();
        Assert.Equal(
            ["delete-individual", "delete-net", "delete-secondary-self",
             "delete-primary-self", "erase"],
            consumers);

        // One VM, one session: the fake's CallCount is cumulative across the
        // loop, so each press is checked as a DELTA of exactly one.
        var vm = ReadyVm();
        foreach (var consumer in consumers)
        {
            int before = _prompt.CallCount;
            Press(vm, consumer);
            Assert.Equal(before + 1, _prompt.CallCount);
            _prompt.Last.Complete(false);
        }

        // …and the FOUR delete consumers really are four different targets, or
        // a row would be testing another row's path twice.
        var writes = PopupConsumers.Select(r => (string)r[1]!).ToList();
        Assert.Equal(5, writes.Distinct().Count());
    }

    [Fact]
    public void ABusyGate_StillRefusesTheErase_AndNothingWasSent()
    {
        // The gate is orthogonal to the popup: the operator can answer YES and
        // still have the OTHER card's operation hold the wire. Nothing goes
        // out, and the reason is named.
        var vm = ReadyVm();
        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);              // the gate is now open
        Transport.ClearSent();

        _prompt.EnqueueAnswer(true);
        vm.EraseCommand.Execute(null);

        Assert.Equal(AleProgrammingGate.BusyReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NoTypedEraseToken_SurvivesAnywhereOnTheCard()
    {
        // §5's removal, pinned as an ABSENCE with an anti-vacuity partner: the
        // typed-token buffer and its constant are gone from the VM, while the
        // command that replaced them is still there.
        var type = typeof(AleProgrammingViewModel);

        Assert.Null(type.GetProperty("EraseInput"));
        Assert.Null(type.GetField("EraseToken"));
        Assert.Null(type.GetProperty("IsDeleteConfirmOpen"));
        Assert.Null(type.GetProperty("ConfirmDeleteCommand"));
        Assert.Null(type.GetProperty("CancelDeleteCommand"));

        Assert.NotNull(type.GetProperty("EraseCommand"));
        Assert.NotNull(type.GetProperty("RequestDeleteCommand"));
    }

    // ==== The two-level gate (both directions) =============================

    [Fact]
    public void DuringAScan_LandingsStillREAD_ButProgramRefusesNamingTheReason()
    {
        var vm = ReadyVm();
        Transport.InjectLine("SCANNING");

        // Level ONE is untouched: the card is live and a landing reads.
        Assert.True(vm.AreControlsEnabled);
        vm.OpenProgramTabCommand.Execute(null);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);
        Transport.ClearSent();

        // Level TWO: the WRITE commands are the only thing that greys.
        Assert.Equal(AleProgrammingViewModel.ScanningDisabledReason, vm.WriteDisabledReason);
        Assert.False(vm.ActionCommand.CanExecute(null));
        Assert.False(vm.EraseCommand.CanExecute(null));

        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);             // Execute ignores CanExecute
        Assert.Equal(AleProgrammingViewModel.ScanningDisabledReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30")]
    [InlineData("LQA/SOUND")]
    public void DuringAnLqa_TheWriteCommandsGrey_TheSameWayACallGreysThem(string announcement)
    {
        // ROUND 15 item I (F69): the on-air term is Core's one predicate now,
        // so a bare-STA LQA — minutes of transmission the operator may not have
        // started (a queued schedule fires on its own) — withholds the writes
        // exactly as a call does. Reads, tab landings and wheel spins stay live
        // above it, which is the level-two rule this file already pins.
        var vm = ReadyVm();
        Transport.InjectLine(announcement);

        Assert.True(vm.AreControlsEnabled);
        Assert.False(vm.ActionCommand.CanExecute(null));
        Assert.False(vm.EraseCommand.CanExecute(null));

        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // …and it lifts on the run's own terminator (P14b's ST abort).
        Transport.InjectLine("SCAN STOPPED");
        Assert.Equal("", vm.WriteDisabledReason);
        Assert.True(vm.ActionCommand.CanExecute(null));
    }

    [Fact]
    public void InACall_TheWriteCommandsGrey_WithTheRulingFiveWording()
    {
        var vm = ReadyVm();
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");

        Assert.True(vm.AreControlsEnabled);
        Assert.Equal(AleProgrammingViewModel.InCallDisabledReason, vm.WriteDisabledReason);
        Assert.False(vm.ActionCommand.CanExecute(null));

        vm.NameInput = "CAM";
        vm.ActionCommand.Execute(null);
        Assert.Equal(AleProgrammingViewModel.InCallDisabledReason, vm.InputError);
        Assert.Empty(Transport.SentLines);

        // …and it lifts when the radio says the call ended.
        Transport.InjectLine("SCAN STOPPED");
        Assert.Equal("", vm.WriteDisabledReason);
        Assert.True(vm.ActionCommand.CanExecute(null));
    }

    // ==== Losing the ALE confirmation while Ready ==========================

    [Fact]
    public void LosingAleConfirmation_ClearsTheGateDisplay_NotTheTyping()
    {
        var vm = ReadyVm();
        vm.NameInput = "CAM";

        vm.ActionCommand.Execute(null);
        AnswerSentinel();
        Transport.InjectLine(" INV SELF ADDRESS ");
        AnswerSentinel();
        Assert.True(vm.HasOperationStatus);

        Transport.InjectLine("SSB>");                 // the radio leaves ALE

        Assert.Equal("", vm.OperationStatus);
        Assert.Equal("CAM", vm.NameInput);            // typing is NOT a radio cache
    }

    // ==== §7 — the self-length correction ==================================

    [Theory]
    [InlineData("Self")]
    [InlineData("Individual")]
    [InlineData("Net")]
    public void EveryKind_TakesUpToFifteenCharacters_WithOneMessage(string kind)
    {
        // Round 10 §7 (owner ruling 3): the client's self bound was 3, mirroring
        // a Core bound that has since moved to 15. The radio's "PRG 1-3 CHAR
        // SLF" line is about the FILL GATE, and nothing has measured what a
        // longer self does — so the client stops pre-refusing it and lets the
        // radio decide. ONE message for every kind now.
        var vm = ReadyVm();
        Pick(vm, kind);
        if (kind != "Self") PickAssociatedSelf(vm, "TST");

        vm.NameInput = new string('A', 16);
        vm.ActionCommand.Execute(null);
        Assert.Equal(AleProgrammingViewModel.NameLengthError, vm.InputError);
        Assert.Equal("An address is 1-15 characters.", vm.InputError);
        Assert.Empty(Transport.SentLines);

        vm.NameInput = "";
        vm.ActionCommand.Execute(null);
        Assert.Equal(AleProgrammingViewModel.NameLengthError, vm.InputError);
        Assert.Empty(Transport.SentLines);

        // …and 15 is ACCEPTED, for a self too.
        vm.NameInput = new string('A', 15);
        vm.ActionCommand.Execute(null);
        Assert.Equal("", vm.InputError);
        Assert.NotEmpty(Transport.SentLines);
    }

    [Fact]
    public void AFourCharacterSelf_IsNoLongerRefusedByTheClient()
    {
        // The regression the §7 change exists to end, pinned on its own: the
        // OLD bound refused this before anything reached the wire.
        var vm = ReadyVm();
        Pick(vm, "Self");
        vm.NameInput = "CAMX";

        vm.ActionCommand.Execute(null);

        Assert.Equal("", vm.InputError);
        Assert.Equal(["BAT ST"], Transport.SentLines);   // the gate's opening bracket
    }

    [Fact]
    public void TheContextualGateHint_IsRuleTwosStringByteForByte()
    {
        // R13's survey note: the caption that stated the radio's own
        // "PRG 1-3 CHAR SLF" token at the operator dies here, and its
        // replacement is token-free.
        Assert.Equal(
            "Stores, but only a 1-3 character self satisfies the scan gate.",
            AleProgrammingViewModel.SelfGateHint);
        Assert.DoesNotContain("PRG", AleProgrammingViewModel.SelfGateHint, StringComparison.Ordinal);

        // …and the captions it REPLACED are gone (absence + anti-vacuity).
        var type = typeof(AleProgrammingViewModel);
        Assert.Null(type.GetField("GroupZeroCaption"));
        Assert.Null(type.GetField("SelfGateCaption"));
        Assert.NotNull(type.GetField("SelfGateHint"));
    }

    [Theory]
    // A SELF longer than three characters — the ONE case the hint exists for.
    [InlineData("Self", "ABCD", true)]
    [InlineData("Self", "  ABCD  ", true)]        // trimmed, so this is 4 too
    [InlineData("Self", "ABC", false)]            // exactly the gate's bound
    [InlineData("Self", "", false)]
    [InlineData("Self", "   ", false)]            // whitespace is not a name
    // Every other kind stores fifteen characters with no gate consequence.
    [InlineData("Individual", "ABCD", false)]
    [InlineData("Net", "ABCDEFG", false)]
    public void TheGateHint_IsVisibleOnlyForALongSELF(string kind, string typed, bool visible)
    {
        var vm = ReadyVm();
        Pick(vm, kind);
        vm.NameInput = typed;

        Assert.Equal(visible, vm.ShowSelfGateHint);
    }

    [Fact]
    public void TheGateHint_TracksBOTHInputs_Live()
    {
        // Both halves are live: typing past the bound turns it on, switching
        // kind turns it off, switching back turns it on again. A hint that
        // only re-evaluated on one of them would be stale exactly when it
        // matters.
        var vm = ReadyVm();
        Pick(vm, "Self");

        vm.NameInput = "ABC";
        Assert.False(vm.ShowSelfGateHint);
        vm.NameInput = "ABCD";
        Assert.True(vm.ShowSelfGateHint);
        Pick(vm, "Individual");
        Assert.False(vm.ShowSelfGateHint);
        Pick(vm, "Self");
        Assert.True(vm.ShowSelfGateHint);
        vm.NameInput = "AB";
        Assert.False(vm.ShowSelfGateHint);
    }

    // ==== R3: the PRIMARY tag on the book listing ==========================

    [Fact]
    public void ThePrimaryTag_IsOnTheFIRSTSelfRow_AndNoOther()
    {
        // Derivation = mirror index 0 among selfs (§8's order pin makes that
        // the radio's own first SLFAD row). The fixture book has TWO selfs, so
        // "the first" is a real choice; every other row of every kind is
        // untagged, including the second self.
        var vm = ReadyVm();

        Assert.Equal(
            ["SELF ZZZ PRIMARY", "SELF TST ", "IND AAA ", "IND BBB ", "NET NT1 "],
            vm.BookRows.Select(r => $"{r.KindText} {r.NameText} {r.PrimaryTagText}"));

        Assert.Single(vm.BookRows, r => r.IsPrimarySelf);
        Assert.Equal("ZZZ", vm.BookRows.Single(r => r.IsPrimarySelf).NameText);
        Assert.Equal("PRIMARY", AleProgrammingViewModel.PrimaryTag);
    }

    [Fact]
    public void ThePrimaryTag_FollowsTheLISTINGPOSITION_NotTheName()
    {
        // The ASSUMED-tier reading (plan §1): after the primary is deleted the
        // first REMAINING row is the primary. The tag renders listing position
        // either way, which is what makes it true whichever way the bench
        // eventually answers.
        var vm = ReadyVm();
        vm.OpenBookTabCommand.Execute(null);
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        AnswerSentinel();

        Assert.Equal("TST", vm.BookRows.Single(r => r.IsPrimarySelf).NameText);
        Assert.DoesNotContain(vm.BookRows, r => r.NameText == "ZZZ" && r.IsPrimarySelf);
    }

    [Fact]
    public void AnEmptyBook_HasNoPrimaryRowAtAll()
    {
        var vm = EmptyBookVm();

        Assert.Empty(vm.BookRows);
        Assert.True(vm.HasNoBookRows);
    }

    // ==== ROUND 15 D — the Address book shows net membership (§14) ==========
    // The mirror was ALREADY loaded on this tab (the landing reads every net's
    // membership for Delete's impact lines) and simply not displayed. So the
    // work is a derivation and a row, plus the one gap the architect's
    // pre-check found: on a COLD session the landing's own loop sees no nets.

    /// <summary>The same R7-shape listing with THREE nets, so the three
    /// membership states can be on screen at once.</summary>
    private void InjectThreeNetBook()
    {
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("INDAD BBB               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT2               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT3               CHGROUP 01   ASSOC SELF TST");
    }

    private AleProgrammingViewModel ThreeNetVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        InjectThreeNetBook();
        AnswerSentinel();
        Transport.ClearSent();
        return vm;
    }

    private static AleBookRow NetRow(AleProgrammingViewModel vm, string name)
        => vm.BookRows.Single(r => r.KindText == "NET" && r.NameText == name);

    [Fact]
    public void TheBookRows_ShowEachNetsMembership_InTheMirrorsOwnThreeStates()
    {
        // The three states, side by side at ONE instant: NT1 answered with
        // members, NT2 answered EMPTY, NT3's targeted read still on the wire.
        // The reads serialise (Core runs one NETAD operation at a time), which
        // is what makes that instant reachable at all.
        var vm = ThreeNetVm();

        vm.OpenBookTabCommand.Execute(null);
        InjectThreeNetBook();
        AnswerSentinel();                       // book commits → NT1 dispatches
        Transport.InjectLine(MemberLine(1, "AAA"));
        Transport.InjectLine(MemberLine(2, "TST"));
        AnswerSentinel();                       // NT1 commits → NT2 dispatches
        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();                       // NT2 commits EMPTY → NT3 dispatches

        Assert.Equal("Members: AAA, TST", NetRow(vm, "NT1").MembersText);
        Assert.Equal("No members programmed", NetRow(vm, "NT2").MembersText);
        Assert.Equal("—", NetRow(vm, "NT3").MembersText);

        // Every NET row HAS the line; no self and no individual does — and
        // theirs is EMPTY, not merely hidden.
        foreach (var row in vm.BookRows)
        {
            if (row.KindText == "NET") { Assert.True(row.HasMembersText); continue; }
            Assert.False(row.HasMembersText);
            Assert.Equal("", row.MembersText);
        }

        // …and NT3 fills when its own answer lands: a member read arriving
        // AFTER the rows were built must REBUILD them (the Signature clause).
        Transport.InjectLine(MemberLine(1, "BBB"));
        AnswerSentinel();
        Assert.Equal("Members: BBB", NetRow(vm, "NT3").MembersText);
    }

    [Fact]
    public void TheMemberLine_KeepsTheRadiosInsertionOrder_AndNamesOnly()
    {
        // The Program tab's table keeps the radio's printed MEMBER nn numbers;
        // the book row is a one-line summary, so it carries names in the order
        // the radio listed them — NOT sorted, and no count.
        var vm = ReadyVm();
        LandOnBookTab(vm, MemberLine(1, "TST"), MemberLine(2, "AAA"), MemberLine(3, "BBB"));

        Assert.Equal("Members: TST, AAA, BBB", NetRow(vm, "NT1").MembersText);
    }

    [Fact]
    public void AnAddm_TakesTheLineUnread_ThenCommitsTheNewListOnTheReRead()
    {
        // BOTH instants (critic F35). Core's InvalidateNetMembers REMOVES the
        // key at send time, so the honest line is the hyphen until the re-read
        // answers — an app that kept yesterday's list on screen would be
        // claiming a membership the radio has not confirmed.
        var vm = ReadyVm();
        LandOnBookTab(vm, MemberLine(1, "AAA"));
        Assert.Equal("Members: AAA", NetRow(vm, "NT1").MembersText);

        Pick(vm, "Member");
        vm.NetPick = "NT1";
        PickMember(vm, "BBB");
        Transport.ClearSent();

        vm.ActionCommand.Execute(null);
        AnswerSentinel();                       // the ADDM goes out
        Assert.Contains("ADDM NT1 BBB", Transport.SentLines);
        Assert.Equal("—", NetRow(vm, "NT1").MembersText);

        AnswerSentinel();                       // the bracket closes
        InjectStationBook();
        AnswerSentinel();                       // closing book read → NT1 re-reads
        Transport.InjectLine(MemberLine(1, "AAA"));
        Transport.InjectLine(MemberLine(2, "BBB"));
        AnswerSentinel();

        Assert.Equal("Members: AAA, BBB", NetRow(vm, "NT1").MembersText);
    }

    [Fact]
    public void TheColdLanding_TheBookAnswerAloneArmsOneTargetedReadPerNet()
    {
        // THE GAP THE RULE CLOSES (§14.2, critic F33/F38). The tab is opened
        // while the mirror holds NO nets, so the landing's own loop queues
        // nothing at all. Without the Refresh rule the member lines would sit
        // at the hyphen until some other gesture read them.
        //
        // This is an EXPLICIT TRANSCRIPT, not a before/after diff: N nets cost
        // exactly N targeted reads, serialised behind the book read, once.
        var vm = EmptyBookVm();

        vm.OpenBookTabCommand.Execute(null);
        Assert.Equal(["SLFAD", "INDAD", "NETAD", "BAT ST"], Transport.SentLines);

        // The book ANSWER introduces all three nets…
        InjectThreeNetBook();
        AnswerSentinel();
        Assert.Equal(
            ["SLFAD", "INDAD", "NETAD", "BAT ST", "NETAD NT1", "BAT ST"],
            Transport.SentLines);

        Transport.InjectLine(MemberLine(1, "AAA"));
        AnswerSentinel();
        Assert.Equal(
            ["SLFAD", "INDAD", "NETAD", "BAT ST",
             "NETAD NT1", "BAT ST", "NETAD NT2", "BAT ST"],
            Transport.SentLines);

        Transport.InjectLine(NoMembersProgrammed);
        AnswerSentinel();
        Assert.Equal(
            ["SLFAD", "INDAD", "NETAD", "BAT ST",
             "NETAD NT1", "BAT ST", "NETAD NT2", "BAT ST", "NETAD NT3", "BAT ST"],
            Transport.SentLines);

        Transport.InjectLine(MemberLine(1, "BBB"));
        AnswerSentinel();

        // Exactly three targeted reads — nothing re-fires once every net has
        // answered, and the lines are filled.
        Assert.Equal(3, Transport.SentLines.Count(l => l.StartsWith("NETAD NT", StringComparison.Ordinal)));
        Assert.Equal("Members: AAA", NetRow(vm, "NT1").MembersText);
        Assert.Equal("No members programmed", NetRow(vm, "NT2").MembersText);
        Assert.Equal("Members: BBB", NetRow(vm, "NT3").MembersText);

        // …and the LAZY tier is untouched: a re-open sends nothing.
        Transport.ClearSent();
        vm.OpenBookTabCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheColdLandingRule_IsScopedToTheOpenBookTab()
    {
        // Anti-over-reach: the rule fires only while the book tab is OPEN and
        // has loaded this session. A book landing with the PROGRAM tab on
        // screen reads no membership — the Program tab's own tier reads the
        // one net the operator picked, and nothing else.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");            // the initial-sight read fires
        Transport.ClearSent();
        InjectThreeNetBook();
        AnswerSentinel();                        // the book lands, tab CLOSED

        Assert.Empty(Transport.SentLines);
        Assert.Equal("—", NetRow(vm, "NT1").MembersText);
    }
}

/// <summary>
/// Phase-2 gate clause 5 — the refusal vocabulary cross-check. The "routed
/// set" is not a list anywhere in the parser (it is a dispatch table on the
/// first token plus the <c>**</c> branch), so this drives the REAL parser with
/// the VERBATIM captured lines and compares what the mirror records against
/// what the vocabulary maps. A routing change in either direction fails here.
/// </summary>
public class AleRefusalVocabularyTests : SessionTestBase
{
    /// <summary>The refusal lines observed on the bench (plan §1), byte for
    /// byte — leading and trailing spaces included.</summary>
    private static readonly string[] LegacyCapturedRefusalLines =
    [
        " ADDRESS EXISTS ",
        " INV ASSOC SELF ",
        " INV MEMBER ADDR ",
        " INV SELF ADDRESS ",
        " INV IND ADDRESS ",
        " INV ADDRESS      ",
        "** ERROR **",
    ];

    /// <summary>
    /// EIGHT of the ten the 2026-08-17 characterization campaign added
    /// (round 11 §8), byte for byte from bench/transcripts (phase 1
    /// membership, phase 2/2b schedules and groups) — the radio's own padding,
    /// which is why the widths differ from one another.
    /// </summary>
    private static readonly string[] Round11CapturedRefusalLines =
    [
        " DUPLICATE MEMBER ",
        " INV SELF MEMBER  ",
        " ADR ALREADY QUED ",
        " LQA QUEUE FULL   ",
        " INDIV CHANS REQD ",
        " SELF CHANS REQD  ",
        " NET CHANS REQD   ",
        " INV CHAN NUMBER  ",
    ];

    /// <summary>
    /// The other two, whose PADDING is <b>DERIVED, not captured</b> (audit
    /// round 1, MAJOR 6 — this file used to fold them in under a blanket
    /// "byte for byte" claim). No raw transcript holds either line; both are
    /// recorded in docs/probes.md §S5 and docs/protocol.md with a single
    /// leading space, which is what is written here.
    /// <para>Padding is immaterial to the contract in any case — the parser
    /// stores the TRIMMED line, and the trimmed forms ARE captured, so the map
    /// keys and every assertion below are unaffected by what the real spacing
    /// turns out to be. Named rather than hidden.</para>
    /// </summary>
    private static readonly string[] Round11DerivedPaddingRefusalLines =
    [
        " INV NET ADDRESS ",
        " INVALID ADDRESS ",
    ];

    private static readonly string[] Round11RefusalLines =
        [.. Round11CapturedRefusalLines, .. Round11DerivedPaddingRefusalLines];

    private static readonly string[] CapturedRefusalLines =
        [.. LegacyCapturedRefusalLines, .. Round11RefusalLines];

    /// <summary>What the mirror records for one injected line — i.e. exactly
    /// what a gate outcome's Detail carries.</summary>
    private string RoutedLine(string capture)
    {
        Transport.InjectLine(capture);
        return Radio.State.Ale.ProgrammingRefusal.Line ?? "";
    }

    [Fact]
    public void EveryParserRoutedRefusalLine_IsRoutedAndMappedNonVerbatim()
    {
        // The half that holds for ALL SEVENTEEN: the parser really routes the
        // line into the refusal mirror, and the vocabulary really maps it (an
        // unmapped line would render verbatim, which is the fallback path and
        // must never be reached by a captured token).
        ConnectReady();

        foreach (var capture in CapturedRefusalLines)
        {
            string routed = RoutedLine(capture);
            Assert.NotEqual("", routed);                       // it really routed
            Assert.Equal(capture.Trim(), routed);              // …trimmed, verbatim

            string described = AleRefusalVocabulary.Describe(routed);
            Assert.NotEqual(routed, described);                // …and is NOT verbatim
        }
    }

    /// <summary>
    /// OWNER RULING R13 — the house-style pin, FLIPPED. It used to require the
    /// radio's token INSIDE each of the original seven wordings; the ruling is
    /// that a refusal message is written for the operator and never exposes a
    /// raw token, so the assertion now says the opposite and covers the WHOLE
    /// vocabulary, not just the seven.
    ///
    /// <para>STRUCTURAL, not a list of literals: it scans every mapped wording
    /// for (a) any mapped radio token as a substring and (b) any parenthesized
    /// UPPER-CASE token shape — so a token reinstated on a line nobody thought
    /// to enumerate still fails. The byte-exact wording pins live separately
    /// (<see cref="TheOriginalSeven_AreTheExactPostR13Wordings"/> and
    /// <see cref="TheRound11Ten_AreTheExactPlannedWordings"/>), so a rewrite
    /// that merely avoids tokens does not slip through either.</para>
    /// </summary>
    [Fact]
    public void NoDescribeOutput_EverExposesARadioToken()
    {
        var tokens = AleRefusalVocabulary.MappedLines.ToArray();
        Assert.NotEmpty(tokens);

        // (a) every MAPPED wording.
        foreach (var token in tokens)
        {
            var described = AleRefusalVocabulary.Describe(token);
            Assert.False(ExposesAToken(described, tokens),
                $"the wording for '{token}' exposes a radio token: {described}");
        }

        // (b) the whole OUTPUT surface, including inputs nobody mapped: an
        //     UNCAPTURED line must not come back as itself (R13 amended — the
        //     Console is the raw-line evidence, not the status area).
        string[] unmapped =
        [
            " INV WHATEVER ", "** SOMETHING **", " ENCRYPTION NOT INSTALLED ",
            "SOME UNCAPTURED REFUSAL",
        ];
        foreach (var line in unmapped)
        {
            var described = AleRefusalVocabulary.Describe(line);
            Assert.False(ExposesAToken(described, tokens));
            Assert.NotEqual(line.Trim(), described);                            // not verbatim…
            Assert.DoesNotContain(line.Trim(), described, StringComparison.Ordinal);
            Assert.Equal(AleRefusalVocabulary.Describe(null), described);       // …it is the generic
        }

        // (c) and the no-detail generic itself.
        Assert.False(ExposesAToken(AleRefusalVocabulary.Describe(null), tokens));

        // ANTI-VACUITY: the detector must actually catch what R13 outlawed —
        // the pre-R13 shapes, both the plain parenthetical and the ** ERROR **
        // one, and a bare embedded token — and must not fire on a legitimate
        // numeric parenthetical.
        Assert.True(ExposesAToken("Refused — that name is already in use (ADDRESS EXISTS)", tokens));
        Assert.True(ExposesAToken("Refused — the radio rejected the command (** ERROR **)", tokens));
        Assert.True(ExposesAToken("Already a member of this net. DUPLICATE MEMBER", tokens));
        Assert.False(ExposesAToken("The schedule queue is full (10).", tokens));
    }

    /// <summary>A wording exposes a token if it CONTAINS one of the mapped
    /// radio lines, or carries a parenthetical that looks like a radio token
    /// (upper case, possibly with the <c>** ERROR **</c> stars) — a purely
    /// numeric parenthetical such as "(10)" is ordinary prose.</summary>
    private static bool ExposesAToken(string wording, IReadOnlyList<string> tokens)
        => tokens.Any(t => wording.Contains(t, StringComparison.Ordinal))
           || System.Text.RegularExpressions.Regex.IsMatch(
                  wording, @"\([A-Z0-9 *_\-]*[A-Z][A-Z0-9 *_\-]*\)");

    /// <summary>The seven original wordings after R13's parenthetical strip,
    /// byte-exact. Their prefix wording is otherwise UNCHANGED, which is what
    /// this table is here to hold down.</summary>
    [Fact]
    public void TheOriginalSeven_AreTheExactPostR13Wordings()
    {
        ConnectReady();

        (string Capture, string Wording)[] table =
        [
            (" ADDRESS EXISTS ", "Refused — that name is already in use"),
            (" INV ASSOC SELF ", "Refused — the associated self does not exist on the radio"),
            (" INV MEMBER ADDR ", "Refused — that member address does not exist on the radio"),
            (" INV SELF ADDRESS ", "Refused — the radio rejected that self address"),
            (" INV IND ADDRESS ", "Refused — the radio rejected that individual address"),
            (" INV ADDRESS      ", "Refused — the radio rejected that address"),
            // THE DELIBERATE R13 COLLAPSE, three-way: stripped of
            // "(** ERROR **)" this is the no-detail generic, which the
            // amendment also made the UNKNOWN-token fallback — so one string
            // serves all three, trailing period included. "** ERROR **" says
            // nothing beyond "the radio rejected it".
            ("** ERROR **", "Refused — the radio rejected the command."),
        ];

        Assert.Equal(7, table.Length);
        foreach (var (capture, wording) in table)
            Assert.Equal(wording, AleRefusalVocabulary.Describe(RoutedLine(capture)));

        Assert.Equal(
            LegacyCapturedRefusalLines.Order(StringComparer.Ordinal),
            table.Select(t => t.Capture).Order(StringComparer.Ordinal));

        // The collapse, stated as its own fact rather than left implicit.
        Assert.Equal(
            AleRefusalVocabulary.Describe(null),
            AleRefusalVocabulary.Describe("** ERROR **"));
    }

    /// <summary>
    /// Round 11 §8's ten, BYTE-EXACT against the plan's own table: plain
    /// operator sentences, no radio token — the register R13 then made the
    /// rule for the whole vocabulary. Re-wording any of them (including
    /// "improving" one back into the "Refused — … (TOKEN)" shape) fails here.
    /// </summary>
    [Fact]
    public void TheRound11Ten_AreTheExactPlannedWordings()
    {
        ConnectReady();

        (string Capture, string Wording)[] table =
        [
            (" DUPLICATE MEMBER ", "Already a member of this net."),
            (" INV SELF MEMBER  ", "Only this net's own associated self can be a member."),
            (" ADR ALREADY QUED ", "Already queued — stop its schedule first."),
            (" LQA QUEUE FULL   ", "The schedule queue is full (10)."),
            (" INDIV CHANS REQD ", "The individual's channel group has no channels."),
            (" SELF CHANS REQD  ", "The self's channel group has no channels."),
            (" NET CHANS REQD   ", "The net's channel group has no channels."),
            (" INV CHAN NUMBER  ", "Channel must be 0-99."),
            (" INV NET ADDRESS ", "Not a programmed net."),
            (" INVALID ADDRESS ", "Nothing is queued for that address."),
        ];

        Assert.Equal(10, table.Length);
        foreach (var (capture, wording) in table)
            Assert.Equal(wording, AleRefusalVocabulary.Describe(RoutedLine(capture)));

        // …and the table really covers the round-11 capture list, so neither
        // can drift without the other.
        Assert.Equal(
            Round11RefusalLines.Order(StringComparer.Ordinal),
            table.Select(t => t.Capture).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheVocabularyKeySet_EqualsTheParsersRoutedSet()
    {
        ConnectReady();

        var routed = CapturedRefusalLines.Select(RoutedLine).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            routed,
            AleRefusalVocabulary.MappedLines.Order(StringComparer.Ordinal));
        Assert.Equal(17, AleRefusalVocabulary.MappedLines.Count);   // anti-vacuity: a real set
    }

    /// <summary>
    /// The stay-out half of the INVALID-ADDRESS routing (round 11 §8): the
    /// parser routes EXACTLY ` INVALID ADDRESS ` into the ALE refusal mirror
    /// and leaves the other two INVALID families where they were — on the
    /// Noise path, because they are other domains' rejects and an ALE
    /// programming gate must never attribute one to its own write.
    /// </summary>
    [Fact]
    public void TheOtherInvalidFamilies_StayOutOfTheAleRefusalMirror()
    {
        ConnectReady();
        long before = Radio.State.Ale.ProgrammingRefusal.Sequence;

        Transport.InjectLine("INVALID ENCR KEY");
        Transport.InjectLine("INVALID MODEM PRESET");
        Assert.Equal(before, Radio.State.Ale.ProgrammingRefusal.Sequence);   // nothing routed

        // …and the one that DOES route proves the scan is not simply inert.
        Transport.InjectLine(" INVALID ADDRESS ");
        Assert.Equal(before + 1, Radio.State.Ale.ProgrammingRefusal.Sequence);
        Assert.Equal("INVALID ADDRESS", Radio.State.Ale.ProgrammingRefusal.Line);
    }

    [Fact]
    public void AnUnknownToken_RendersTheGeneric_NEVERVerbatim()
    {
        // R13 (amended) REVERSES the old rule. This test used to be
        // "AnUnknownToken_IsTheONLYThingThatRendersVerbatim" and asserted the
        // raw line came back; the ruling is that no operator-facing status
        // shows wire text at all, so an uncaptured shape renders the generic
        // and the CONSOLE carries the raw line.
        Assert.DoesNotContain("INV WHATEVER", AleRefusalVocabulary.MappedLines);
        Assert.Equal("Refused — the radio rejected the command.",
            AleRefusalVocabulary.Describe(" INV WHATEVER "));
        Assert.NotEqual("INV WHATEVER", AleRefusalVocabulary.Describe(" INV WHATEVER "));

        // A blank line is not a refusal shape at all, and a bare "** ERROR **"
        // carries no detail either: all three render the ONE generic (the
        // deliberate three-way collapse, pinned in
        // TheOriginalSeven_AreTheExactPostR13Wordings).
        Assert.Equal("Refused — the radio rejected the command.", AleRefusalVocabulary.Describe(null));
        Assert.Equal(AleRefusalVocabulary.Describe(null), AleRefusalVocabulary.Describe("** ERROR **"));
    }
}
