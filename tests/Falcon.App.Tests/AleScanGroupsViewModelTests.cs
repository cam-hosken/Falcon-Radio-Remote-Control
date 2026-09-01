using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE settings pane's "Channel groups" card (plan-ale-programming.md
/// §4.4/§9 phase-2 clause 3; heading renamed by round 11 §3), over the REAL stack —
/// the same surface, gate, Core read queue and parser the app runs.
///
/// <para>The captured wire shapes these tests replay:
/// <c>CHGROUP 01 CHANS 00 01 </c> (probe R7, trailing space and all) for a
/// populated group, and NOTHING AT ALL for an empty one — the captured
/// silence, which the read's own sentinel turns into the confirmed-empty
/// state.</para>
/// </summary>
public class AleScanGroupsViewModelTests : SessionTestBase
{
    private const string Group1Listing = "CHGROUP 01 CHANS 00 01 ";

    private readonly AleSurface _ale;

    public AleScanGroupsViewModelTests() => _ale = new AleSurface(Radio);

    private AleScanGroupsViewModel Vm() => new(_ale, Session);

    /// <summary>Ready + confirmed ALE, group 0 answered with the captured
    /// SILENCE (so it is confirmed EMPTY), then parked on group 1 with the
    /// captured two-channel listing. The wire is drained.</summary>
    private AleScanGroupsViewModel ReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");            // the initial-sight read: CHG 0
        AnswerSentinel();                        // …commits group 0 as EMPTY
        vm.GroupUpCommand.Execute(null);         // → group 1, CHG 1
        Transport.InjectLine(Group1Listing);
        AnswerSentinel();
        Transport.ClearSent();
        return vm;
    }

    /// <summary>Land on a group ONE spin at a time, answering each landing
    /// read's sentinel — the unhurried operator. (Spinning faster than the
    /// radio answers is a different thing entirely, and has its own pin: Core
    /// coalesces the spins into one pending operation.)</summary>
    private void LandOn(AleScanGroupsViewModel vm, int group)
    {
        for (int i = 0; i < AleScanGroupsViewModel.GroupCount && vm.PickedGroup != group; i++)
        {
            vm.GroupUpCommand.Execute(null);
            AnswerSentinel();
        }
    }

    // ==== Read path — the §6 table ==========================================

    [Fact]
    public void ReadyInAle_SendsExactlyOneGroupRead_ForThePickedGroup()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("ALE>");

        Assert.Equal(["CHG 0", "BAT ST"], Transport.SentLines);
        Assert.Equal(0, vm.PickedGroup);
        Assert.True(vm.AreControlsEnabled);
    }

    [Fact]
    public void ASpinIsAnEditorLanding_ItReadsTheGroupItLandsOn()
    {
        var vm = ReadyVm();

        vm.GroupUpCommand.Execute(null);

        Assert.Equal(2, vm.PickedGroup);
        Assert.Equal(["CHG 2", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void TheSpinnerWraps_BothWays()
    {
        var vm = ReadyVm();

        vm.GroupDownCommand.Execute(null);            // 1 → 0
        Assert.Equal(0, vm.PickedGroup);
        AnswerSentinel();
        Transport.ClearSent();

        vm.GroupDownCommand.Execute(null);            // 0 → 9, wrapped back
        Assert.Equal(9, vm.PickedGroup);
        Assert.Equal(["CHG 9", "BAT ST"], Transport.SentLines);
        AnswerSentinel();
        Transport.ClearSent();

        vm.GroupUpCommand.Execute(null);              // 9 → 0, wrapped forward
        Assert.Equal(0, vm.PickedGroup);
        Assert.Equal(["CHG 0", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void RapidSpins_CoalesceIntoONEPendingOperation_NothingIsSuppressed()
    {
        // The §4.1 queue contract, through the card: the ACTIVE {1} operation
        // commits its slot once, the spins during it coalesce into ONE pending
        // operation that commits its union once, and the card renders the slot
        // the picker is on. Nothing partial publishes and nothing is dropped.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();                        // group 0 settles
        Transport.ClearSent();

        vm.GroupUpCommand.Execute(null);         // → 1: the ACTIVE operation
        Assert.Equal(["CHG 1", "BAT ST"], Transport.SentLines);

        vm.GroupUpCommand.Execute(null);         // → 2 …
        vm.GroupUpCommand.Execute(null);         // → 3, both COALESCED
        Assert.Equal(["CHG 1", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        AnswerSentinel();                        // the active op commits…

        // …and the pending union {2,3} begins, as ONE operation.
        Assert.Equal(["CHG 2", "CHG 3", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("CHGROUP 03 CHANS 05 ");
        AnswerSentinel();
        Assert.Equal(3, vm.PickedGroup);
        Assert.Equal(["05"], vm.ChannelRows.Select(r => r.ChannelText));
    }

    [Fact]
    public void GroupsTab_IsTheLazyTier_TheWholeTableOncePerSession()
    {
        var vm = ReadyVm();

        vm.OpenGroupsTabCommand.Execute(null);
        Assert.True(vm.IsGroupsTabOpen);
        Assert.Equal(
            ["CHG 0", "CHG 1", "CHG 2", "CHG 3", "CHG 4", "CHG 5", "CHG 6", "CHG 7", "CHG 8", "CHG 9", "BAT ST"],
            Transport.SentLines);

        AnswerSentinel();
        Transport.ClearSent();
        vm.OpenGroupsTabCommand.Execute(null);   // renders from the mirror
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OnlyTheGroupsTabLanding_EverReadsAGroupThePickerIsNotOn()
    {
        // The negative half of the §6 table. Every OTHER gesture on this card
        // reads exactly the picked group — a bulk read anywhere else would be
        // the doctrine's one forbidden send.
        var vm = ReadyVm();
        Assert.Equal(1, vm.PickedGroup);

        vm.OpenProgramTabCommand.Execute(null);
        Transport.InjectLine(Group1Listing);
        AnswerSentinel();

        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);
        AnswerSentinel();                         // opening bracket → ADDC out
        AnswerSentinel();                         // closing bracket → outcome
        Transport.InjectLine("CHGROUP 01 CHANS 00 01 07 ");
        AnswerSentinel();                         // the closing read commits

        var row = vm.ChannelRows.First();
        row.Remove.Execute(row.ChannelText);
        AnswerSentinel();
        AnswerSentinel();
        Transport.InjectLine(Group1Listing);
        AnswerSentinel();

        var groupReads = Transport.SentLines
            .Where(l => l.StartsWith("CHG ", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.Equal(["CHG 1"], groupReads);      // anti-vacuity: reads DID happen
    }

    [Fact]
    public void ProgramTabLanding_ReadsThePickedGroupFresh()
    {
        var vm = ReadyVm();
        vm.OpenGroupsTabCommand.Execute(null);
        AnswerSentinel();
        Transport.ClearSent();

        vm.OpenProgramTabCommand.Execute(null);
        Assert.Equal(["CHG 1", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void SessionDrop_ReArmsBothLatches()
    {
        var vm = ReadyVm();
        vm.OpenGroupsTabCommand.Execute(null);
        AnswerSentinel();

        Session.Close();
        Transport.ClearSent();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Assert.Equal(["CHG 1", "BAT ST"], Transport.SentLines);   // sight read, on the KEPT group

        AnswerSentinel();
        Transport.ClearSent();
        vm.OpenGroupsTabCommand.Execute(null);
        Assert.Equal(11, Transport.SentLines.Count);              // …and the bulk load re-armed
    }

    [Fact]
    public void NotReady_TheCardIsInert_AndNothingSends()
    {
        var vm = Vm();
        vm.AddChannelInput = "05";

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.AddChannelCommand.CanExecute(null));

        vm.AddChannelCommand.Execute(null);
        vm.GroupUpCommand.Execute(null);
        vm.OpenGroupsTabCommand.Execute(null);
        vm.OpenProgramTabCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ==== The three states ==================================================

    [Fact]
    public void TheThreeStates_AreNeverConflated()
    {
        var vm = ReadyVm();

        // Group 1: CONFIRMED membership, the radio's own order.
        Assert.False(vm.IsPickedGroupUnqueried);
        Assert.False(vm.IsPickedGroupEmpty);
        Assert.Equal(["00", "01"], vm.ChannelRows.Select(r => r.ChannelText));

        // Group 0: queried, and the radio answered NOTHING — confirmed empty.
        Assert.Equal(AleScanGroupsViewModel.EmptyGroupCaption, vm.GroupRows[0].ChannelsText);
        Assert.Equal("00 01", vm.GroupRows[1].ChannelsText);

        // Groups 5 and 9: never asked about. NOT "empty" — unknown.
        Assert.Equal(AleScanGroupsViewModel.UnqueriedText, vm.GroupRows[5].ChannelsText);
        Assert.Equal(AleScanGroupsViewModel.UnqueriedText, vm.GroupRows[9].ChannelsText);

        // …and landing on one shows the unknown state until its read commits.
        vm.GroupDownCommand.Execute(null);       // → 0
        AnswerSentinel();
        vm.GroupDownCommand.Execute(null);       // → 9, never queried
        Assert.True(vm.IsPickedGroupUnqueried);
        Assert.False(vm.IsPickedGroupEmpty);
        Assert.Empty(vm.ChannelRows);

        AnswerSentinel();                        // the captured silence commits
        Assert.False(vm.IsPickedGroupUnqueried);
        Assert.True(vm.IsPickedGroupEmpty);
        Assert.Equal(AleScanGroupsViewModel.EmptyGroupCaption, vm.GroupRows[9].ChannelsText);
        Assert.Equal(AleScanGroupsViewModel.UnqueriedText, vm.GroupRows[5].ChannelsText);
    }

    // ==== Add / remove ======================================================

    [Fact]
    public void AddChannel_SendsAddcInsideTheBracket_ThenTheClosingGroupRead()
    {
        var vm = ReadyVm();
        vm.AddChannelInput = "07";

        vm.AddChannelCommand.Execute(null);
        Assert.Equal(["BAT ST"], Transport.SentLines);          // the opening bracket alone
        AnswerSentinel();

        Assert.Equal(["BAT ST", "ADDC 1 07", "BAT ST", "CHG 1"], Transport.SentLines);
        Assert.Equal("", vm.AddChannelInput);                   // the box is spent
    }

    [Fact]
    public void ADuplicateAdd_SendsTheSameTraffic_AndAnUnchangedListIsNotAnError()
    {
        // The vacuity the plan's critic flagged: the EXACT outbound traffic is
        // asserted BEFORE the radio's unchanged listing is injected, so this
        // cannot pass by the app quietly having sent nothing.
        var vm = ReadyVm();
        vm.AddChannelInput = "01";                              // already in the group

        vm.AddChannelCommand.Execute(null);
        AnswerSentinel();

        Assert.Equal(["BAT ST", "ADDC 1 01", "BAT ST", "CHG 1"], Transport.SentLines);

        AnswerSentinel();                                       // the bracket closes → Accepted
        Transport.InjectLine(Group1Listing);                    // the radio's UNCHANGED list
        AnswerSentinel();

        Assert.Equal(["00", "01"], vm.ChannelRows.Select(r => r.ChannelText));
        Assert.Equal("", vm.OperationStatus);                   // Accepted says nothing
        Assert.Equal("", vm.InputError);                        // …and nothing is invented
    }

    [Fact]
    public void RemoveChannel_SendsDelcForTheRowsOwnValue()
    {
        var vm = ReadyVm();
        var row = vm.ChannelRows.Single(r => r.ChannelText == "01");

        row.Remove.Execute(row.ChannelText);
        AnswerSentinel();

        Assert.Equal(["BAT ST", "DELC 1 01", "BAT ST", "CHG 1"], Transport.SentLines);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("-1")]
    [InlineData("ab")]
    public void AddChannel_ClientBounds_RefuseBeforeTheWire(string typed)
    {
        var vm = ReadyVm();
        vm.AddChannelInput = typed;

        vm.AddChannelCommand.Execute(null);

        Assert.Equal($"'{typed}' — channels are 0-99.", vm.InputError);
        Assert.Empty(Transport.SentLines);
        Assert.Equal(typed, vm.AddChannelInput);                // a refused entry is not spent
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddChannel_WithNothingTyped_RefusesAndSaysSo(string typed)
    {
        var vm = ReadyVm();
        vm.AddChannelInput = typed;

        vm.AddChannelCommand.Execute(null);

        Assert.Equal(AleScanGroupsViewModel.NoChannelsError, vm.InputError);
        Assert.Equal("Type at least one channel to add, 0-99.", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    // ==== ROUND 11 §5: MULTI-ADD ===========================================

    [Fact]
    public void MultiAdd_SendsONEAddcPerChannel_SEQUENTIALLY_InTypedOrder()
    {
        // N tokens, N commands — the wire takes one channel per ADDC, so the
        // batch runs through the ONE gate one write at a time, each closing
        // with its own CHG read.
        var vm = ReadyVm();
        vm.AddChannelInput = "5 12 47";

        vm.AddChannelCommand.Execute(null);
        Assert.Equal(["BAT ST"], Transport.SentLines);           // the first opening bracket
        Assert.Equal("", vm.AddChannelInput);                    // the box is spent

        AnswerSentinel();
        Assert.Equal(["BAT ST", "ADDC 1 05", "BAT ST", "CHG 1"], Transport.SentLines);

        // THREE sentinels per write cycle: the opening bracket, the closing
        // bracket that delivers the outcome, and the closing READ's own
        // barrier — Core keeps one BAT ST on the wire, so the next write's
        // bracket dispatches only after that one answers.
        for (int i = 0; i < 8; i++) AnswerSentinel();

        Assert.Equal(
            ["ADDC 1 05", "ADDC 1 12", "ADDC 1 47"],
            Transport.SentLines.Where(l => l.StartsWith("ADDC", StringComparison.Ordinal)));
        Assert.Equal(3, Transport.CountSent("CHG 1"));
        Assert.Equal("", vm.OperationStatus);                    // all accepted: nothing to say
        Assert.Equal("", vm.InputError);
    }

    [Fact]
    public void MultiAdd_OneInvalidToken_RefusesTheWHOLEBatch_NamingTheOffender()
    {
        // CLIENT-SIDE and all-or-nothing: half a batch on the wire because
        // token two was a typo is exactly what this prevents.
        var vm = ReadyVm();
        vm.AddChannelInput = "5 1x 47";

        vm.AddChannelCommand.Execute(null);

        Assert.Equal("'1x' — channels are 0-99.", vm.InputError);
        Assert.Empty(Transport.SentLines);                       // NOTHING was sent
        Assert.Equal("5 1x 47", vm.AddChannelInput);             // …and nothing is spent
    }

    [Fact]
    public void MultiAdd_AnOutOfRangeTokenIsNamedToo_EvenAtTheEnd()
    {
        var vm = ReadyVm();
        vm.AddChannelInput = "5 12 100";

        vm.AddChannelCommand.Execute(null);

        Assert.Equal("'100' — channels are 0-99.", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void MultiAdd_DUPLICATESAreNotPreFiltered_TheWireIgnoresThem()
    {
        // Wire semantics: the radio silently ignores a repeat, so the app
        // sends what the operator asked for and invents no error. A
        // pre-filter would make the app disagree with the radio about what
        // was requested.
        var vm = ReadyVm();
        vm.AddChannelInput = "7 7";

        vm.AddChannelCommand.Execute(null);
        for (int i = 0; i < 6; i++) AnswerSentinel();

        Assert.Equal(
            ["ADDC 1 07", "ADDC 1 07"],
            Transport.SentLines.Where(l => l.StartsWith("ADDC", StringComparison.Ordinal)));
        Assert.Equal("", vm.OperationStatus);
        Assert.Equal("", vm.InputError);
    }

    [Fact]
    public void MultiAdd_PerChannelOutcomes_NameTheChannelThatDrewTheRefusal()
    {
        // The batch's REFUSALS are reported per channel, in operator words
        // (R13 — no radio token), and the accepted ones say nothing: their
        // proof is the rows the closing read brings back.
        var vm = ReadyVm();
        vm.AddChannelInput = "5 12";

        vm.AddChannelCommand.Execute(null);
        AnswerSentinel();                                        // #1's write goes out
        AnswerSentinel();                                        // #1 closes: Accepted
        AnswerSentinel();                                        // #1's closing read's barrier
        AnswerSentinel();                                        // #2's write goes out

        Assert.Contains("ADDC 1 12", Transport.SentLines);
        Transport.InjectLine(" INV CHAN NUMBER ");
        AnswerSentinel();                                        // #2 closes: Refused

        Assert.Equal("Channel 12: Channel must be 0-99.", vm.OperationStatus);
        Assert.DoesNotContain("INV CHAN NUMBER", vm.OperationStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Channel 05", vm.OperationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiAdd_ABusyGate_AbandonsTheRemainder_WithoutSendingIt()
    {
        // The gate is shared with the address card. If it is busy when the
        // batch starts, NOTHING goes out and the reason is named — the
        // remainder is not queued up behind it.
        var vm = ReadyVm();
        var addresses = new AleProgrammingViewModel(_ale, Session, new FakeConfirmationPrompt());
        AnswerSentinel();                                        // drain ITS sight read
        Transport.ClearSent();

        addresses.NameInput = "CAM";
        addresses.ActionCommand.Execute(null);                  // the gate is now open
        Transport.ClearSent();

        vm.AddChannelInput = "5 12 47";
        vm.AddChannelCommand.Execute(null);

        Assert.Equal(AleProgrammingGate.BusyReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    // ---- AUDIT ROUND 1, MAJOR-1: a second press never touches a RUNNING batch

    [Fact]
    public void MultiAdd_ASecondPressMIDBATCH_LeavesTheRunningBatchIntact()
    {
        // The auditor's repro: press Add on "5 12 47", press again before
        // channel 05 resolves. The batch state is SHARED, so the second press
        // used to clear the first batch's remainder — 05 went out and 12/47
        // vanished SILENTLY. Now the press is refused and the first batch
        // completes every one of its sends.
        var vm = ReadyVm();
        vm.AddChannelInput = "5 12 47";
        vm.AddChannelCommand.Execute(null);

        vm.AddChannelInput = "9";
        Assert.False(vm.AddChannelCommand.CanExecute(null));      // greyed…
        vm.AddChannelCommand.Execute(null);                       // …and Execute ignores that

        // The second press is EXPLICIT, not silent, and sent nothing of its own.
        Assert.Equal(AleScanGroupsViewModel.BatchRunningError, vm.InputError);
        Assert.Equal("9", vm.AddChannelInput);                    // its entry is not spent

        for (int i = 0; i < 8; i++) AnswerSentinel();

        // …and the FIRST batch is whole: all three channels, in order, and no
        // fourth write smuggled in by the second press.
        Assert.Equal(
            ["ADDC 1 05", "ADDC 1 12", "ADDC 1 47"],
            Transport.SentLines.Where(l => l.StartsWith("ADDC", StringComparison.Ordinal)));

        // The button comes back when the batch is done.
        Assert.True(vm.AddChannelCommand.CanExecute(null));
    }

    [Fact]
    public void MultiAdd_TheBatchLatch_ReleasesOnAGateAbandonedBatch_AndOnADrop()
    {
        // The latch must not outlive the batch by either exit: a gate that
        // refused to open, and a session drop. A stuck latch would grey the
        // Add button for the rest of the session.
        var vm = ReadyVm();
        var addresses = new AleProgrammingViewModel(_ale, Session, new FakeConfirmationPrompt());
        AnswerSentinel();                                         // drain ITS sight read
        addresses.NameInput = "CAM";
        addresses.ActionCommand.Execute(null);                   // the gate is now open
        Transport.ClearSent();

        vm.AddChannelInput = "5 12";
        vm.AddChannelCommand.Execute(null);                       // refused by the busy gate

        Assert.Equal(AleProgrammingGate.BusyReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
        Assert.True(vm.AddChannelCommand.CanExecute(null));       // …and NOT latched

        // The drop route: start a batch, drop mid-flight, reconnect.
        for (int i = 0; i < 6; i++) AnswerSentinel();
        vm.AddChannelInput = "5 12 47";
        vm.AddChannelCommand.Execute(null);
        Assert.False(vm.AddChannelCommand.CanExecute(null));

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();

        Assert.True(vm.AddChannelCommand.CanExecute(null));
    }

    // ---- AUDIT ROUND 1, MAJOR-2: the delimiter is a SPACE, and only a space

    [Theory]
    [InlineData("5,12")]          // comma — the surviving production behaviour
    [InlineData("5;12")]          // semicolon — the auditor's widening mutation
    [InlineData("5|12")]
    [InlineData("5\t12")]
    public void MultiAdd_AnyNonSpaceDelimiter_IsONEInvalidToken_RefusedByName(string typed)
    {
        // §5's grammar is SPACE-separated. A comma or semicolon is part of the
        // token, so the whole thing is one offender — refused client-side, by
        // name, with nothing on the wire. (The card used to split on commas
        // and tabs too, which quietly defined a grammar the plan does not.)
        var vm = ReadyVm();
        vm.AddChannelInput = typed;

        vm.AddChannelCommand.Execute(null);

        Assert.Equal($"'{typed}' — channels are 0-99.", vm.InputError);
        Assert.Empty(Transport.SentLines);
        Assert.Equal(typed, vm.AddChannelInput);
    }

    [Fact]
    public void MultiAdd_TheSpaceGrammar_StillSplitsRunsOfSpaces()
    {
        // The other side of the same pin: spaces DO separate, and a run of
        // them is not an empty token.
        var vm = ReadyVm();
        vm.AddChannelInput = "  5   12  ";

        vm.AddChannelCommand.Execute(null);
        for (int i = 0; i < 6; i++) AnswerSentinel();

        Assert.Equal(
            ["ADDC 1 05", "ADDC 1 12"],
            Transport.SentLines.Where(l => l.StartsWith("ADDC", StringComparison.Ordinal)));
        Assert.Equal("", vm.InputError);
    }

    [Fact]
    public void TheAddPlaceholder_IsTheSectionFiveString()
        => Assert.Equal("e.g. 5 12 47 (space-separated)",
            AleScanGroupsViewModel.AddChannelsPlaceholder);

    // ==== Outcomes and the shared gate ======================================

    [Fact]
    public void ARefusalInsideTheBracket_RendersThroughTheVocabulary()
    {
        var vm = ReadyVm();
        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);
        AnswerSentinel();

        Transport.InjectLine("** ERROR **");
        AnswerSentinel();

        Assert.Equal(AleRefusalVocabulary.Describe("** ERROR **"), vm.OperationStatus);
        Assert.NotEqual("** ERROR **", vm.OperationStatus);
        // R13: operator language only — the banner token is NOT in the status
        // (it is on the Console). This assertion was the reverse before.
        Assert.DoesNotContain("** ERROR **", vm.OperationStatus, StringComparison.Ordinal);
        Assert.Equal("Refused — the radio rejected the command.", vm.OperationStatus);
    }

    [Fact]
    public void BothCardsShareTheONEGate_SoOneOperationBlocksTheOther()
    {
        // Mutual exclusion between the two cards is the point of the shared
        // gate: the second card surfaces the refusal as its own InputError and
        // sends NOTHING.
        var groups = ReadyVm();
        var addresses = new AleProgrammingViewModel(_ale, Session, new FakeConfirmationPrompt());
        AnswerSentinel();                             // drain ITS initial-sight read
        Transport.ClearSent();

        groups.AddChannelInput = "07";
        groups.AddChannelCommand.Execute(null);       // the gate is now open
        Transport.ClearSent();

        addresses.NameInput = "CAM";
        addresses.ActionCommand.Execute(null);

        Assert.Equal(AleProgrammingGate.BusyReason, addresses.InputError);
        Assert.Empty(Transport.SentLines);

        // …and it frees on delivery, so the other card runs next. (The third
        // answer drains the CLOSING READ's own sentinel: Core keeps one BAT ST
        // on the wire, so the next barrier would otherwise queue rather than
        // dispatch — the gate's clean-slot rule, pinned in its own suite.)
        AnswerSentinel();
        AnswerSentinel();
        Assert.False(_ale.Programming.IsBusy);
        AnswerSentinel();
        Transport.ClearSent();
        addresses.ActionCommand.Execute(null);
        Assert.Equal(["BAT ST"], Transport.SentLines);
    }

    // ==== The two-level gate (both directions) =============================

    [Fact]
    public void DuringAScan_ASpinStillSendsChg_ButAddAndRemoveRefuse()
    {
        var vm = ReadyVm();
        Transport.InjectLine("SCANNING");

        // Level ONE untouched: the spin READS (bench: listings during a scan
        // come back clean).
        vm.GroupUpCommand.Execute(null);
        Assert.Equal(["CHG 2", "BAT ST"], Transport.SentLines);
        Transport.ClearSent();

        // Level TWO: only the writes grey.
        Assert.True(vm.AreControlsEnabled);
        Assert.Equal(AleProgrammingViewModel.ScanningDisabledReason, vm.WriteDisabledReason);
        Assert.False(vm.AddChannelCommand.CanExecute(null));

        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);           // Execute ignores CanExecute
        Assert.Equal(AleProgrammingViewModel.ScanningDisabledReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30")]
    [InlineData("LQA/SOUND")]
    public void DuringAnLqa_TheWriteCommandsGrey_TheSameWayACallGreysThem(string announcement)
    {
        // ROUND 15 item I (F69): the same ONE on-air term the programming card
        // reads — an ADDC issued into a minutes-long transmission (P14c) is the
        // case this file's own private list could not see.
        var vm = ReadyVm();
        Transport.InjectLine(announcement);

        Assert.True(vm.AreControlsEnabled);
        Assert.False(vm.AddChannelCommand.CanExecute(null));

        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SCAN STOPPED");
        Assert.Equal("", vm.WriteDisabledReason);
        Assert.True(vm.AddChannelCommand.CanExecute(null));
    }

    [Fact]
    public void InACall_TheWriteCommandsGrey_WithTheRulingFiveWording()
    {
        var vm = ReadyVm();
        Transport.InjectLine("LINKED AAA");

        Assert.True(vm.AreControlsEnabled);
        Assert.Equal(AleProgrammingViewModel.InCallDisabledReason, vm.WriteDisabledReason);
        Assert.False(vm.AddChannelCommand.CanExecute(null));

        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);
        Assert.Equal(AleProgrammingViewModel.InCallDisabledReason, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    // ==== Lifecycle ========================================================

    [Fact]
    public void Reconnect_PreservesTypingAndThePickedGroup()
    {
        var vm = ReadyVm();
        LandOn(vm, 6);
        vm.AddChannelInput = "42";

        Session.Close();
        Transport.ClearSent();
        ConnectReady();
        Transport.InjectLine("ALE>");

        Assert.Equal(["CHG 6", "BAT ST"], Transport.SentLines);   // it read the KEPT group
        Assert.Equal(6, vm.PickedGroup);
        Assert.Equal("42", vm.AddChannelInput);
    }

    [Fact]
    public void LosingAleConfirmation_ClearsTheGateDisplay_ButNotTheTyping()
    {
        var vm = ReadyVm();
        vm.AddChannelInput = "07";
        vm.AddChannelCommand.Execute(null);
        AnswerSentinel();
        Transport.InjectLine(" INV ADDRESS      ");
        AnswerSentinel();
        Assert.True(vm.HasOperationStatus);

        vm.AddChannelInput = "42";
        Transport.InjectLine("SSB>");

        Assert.Equal("", vm.OperationStatus);
        Assert.Equal("42", vm.AddChannelInput);
        Assert.False(vm.AreControlsEnabled);
    }
}
