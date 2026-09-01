using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The Messages page (plan §4.5): compose cap + live counter, targets from
/// the flat station book (selfs excluded), Send driving CORE'S REAL
/// scratch-slot flow over the injecting transport (TXMSG 9 write →
/// read-back verify → SE 9 — never sent unverified), the sent-log outcome
/// surfacing on the verified / verify-fail / timeout paths, the Stage 9
/// inbox gate as pure view state, and the constitution pins.
/// </summary>
public class MessagesViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();

    private MessagesViewModel Vm() => new(new AleSurface(Radio), Session, _time);

    private MessagesViewModel AleReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        InjectStationBook();                    // no refresh pending → publishes directly
        Transport.ClearSent();
        return vm;
    }

    private void InjectStationBook()
    {
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("INDAD BBB               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
    }

    private void SelectTarget(MessagesViewModel vm, string address)
    {
        vm.PreselectTarget(address);
        Assert.Equal(address, vm.SelectedTarget?.Address);
    }

    /// <summary>Land a CHG group read into the mirror the broadcast pickers read
    /// (this page has no lazy tier of its own — the ALE pane owns that read,
    /// plan §2 critic F3). Two OVERLAPPING groups, out of numeric order, so the
    /// union's distinct + sort + "00" formatting are all visible.</summary>
    private void LandChannelGroups()
    {
        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 12 05 ");
        Transport.InjectLine("CHGROUP 02 CHANS 05 29 ");
        AnswerSentinel();
        Transport.ClearSent();
    }

    /// <summary>Ready in ALE, book landed, group mirror landed — the state the
    /// compose channel picker is designed against.</summary>
    private MessagesViewModel BroadcastReadyVm()
    {
        var vm = AleReadyVm();
        LandChannelGroups();
        return vm;
    }

    private void SelectBroadcast(MessagesViewModel vm, string address)
    {
        vm.PreselectBroadcast(address, null);
        Assert.Equal(address, vm.SelectedTarget?.Address);
    }

    // ---- Compose: live counter + hard 90 cap -----------------------------------

    [Fact]
    public void Counter_TracksTheComposeText()
    {
        var vm = Vm();
        Assert.Equal("0/90", vm.CounterText);

        vm.ComposeText = "TEST MSG STAGE6";
        Assert.Equal("15/90", vm.CounterText);
    }

    [Fact]
    public void Compose_HardCapAt90_ClampsAtTheViewModel()
    {
        // The view's MaxLength is a convenience; the VM is the guard.
        var vm = Vm();
        vm.ComposeText = new string('X', 120);

        Assert.Equal(90, vm.ComposeText.Length);
        Assert.Equal("90/90", vm.CounterText);
    }

    // ---- Targets: flat list, selfs excluded --------------------------------------

    [Fact]
    public void Targets_IndividualsAndNets_SelfsExcluded_ThenThePinnedBroadcastTail()
    {
        // BROADCAST ROUND (plan-ale-broadcast-round.md §2): the book's three
        // entries, then the two PINNED literals — permanently, at the TAIL, in
        // the order the pane pins their rows. Asserted as the WHOLE list rather
        // than as a count, so neither the book's order nor the tail's position
        // can drift unnoticed.
        var vm = AleReadyVm();

        Assert.Equal(["AAA", "BBB", "NT1", "ANY", "ALL"], vm.Targets.Select(t => t.Address));
        Assert.Equal(["IND", "IND", "NET", "broadcast", "broadcast"],
            vm.Targets.Select(t => t.KindText));
        Assert.DoesNotContain(vm.Targets, t => t.Address == "TST");

        // The kind word reaches the picker line the operator reads.
        Assert.Equal("ANY  (broadcast)", vm.Targets[3].Display);
    }

    [Fact]
    public void TheBroadcastTail_SurvivesAnEmptyBook_AndEveryRebuild()
    {
        // Invariant 2: the pinned entries are APP FURNITURE, not book records.
        // A radio that has reported nothing still offers them — which is the
        // whole reason they are a fixed tail rather than mirror rows.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Assert.Equal(["ANY", "ALL"], vm.Targets.Select(t => t.Address));

        // …and a book landing later APPENDS to them rather than replacing them.
        InjectStationBook();
        Assert.Equal(["AAA", "BBB", "NT1", "ANY", "ALL"], vm.Targets.Select(t => t.Address));

        // The pick survives the rebuild by IDENTITY: the tail choices are the
        // same instances every time, so a book refresh cannot silently drop the
        // operator's selected broadcast target.
        var any = vm.Targets[3];
        vm.SelectedTarget = any;
        InjectStationBook();
        Assert.Same(any, vm.SelectedTarget);
    }

    // ---- Send gating (disabled-with-reason) ----------------------------------------

    [Fact]
    public void Send_RequiresTargetAndText_WithReasons()
    {
        var vm = AleReadyVm();
        Assert.False(vm.CanSend);
        Assert.Contains("target", vm.SendDisabledReason);

        SelectTarget(vm, "AAA");
        Assert.False(vm.CanSend);
        Assert.Contains("1–90", vm.SendDisabledReason);

        vm.ComposeText = "TEST MSG STAGE6";
        Assert.True(vm.CanSend);
        Assert.Equal("", vm.SendDisabledReason);
    }

    [Fact]
    public void Send_OutsideAle_DisabledWithReason()
    {
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        Transport.InjectLine("SSB>");

        Assert.False(vm.CanSend);
        Assert.Contains("ALE", vm.SendDisabledReason);
        vm.SendCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Send_DuringACallOrSend_DisabledWithReason()
    {
        // SE during an in-flight call/send handshake is unprobed — not offered.
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        Transport.InjectLine("CALLING  BBB              CHANNEL: 01");

        Assert.False(vm.CanSend);
        Assert.Equal(MessagesViewModel.OnAirDisabledReason, vm.SendDisabledReason);
        vm.SendCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30")]
    [InlineData("LQA/SOUND")]
    public void Send_DuringAnLqaOrALink_DisabledWithReason(string announcement)
    {
        // ROUND 15 item I (F69): this file's private Calling|Sending list is
        // now Core's ONE on-air term, so an SE can no longer be queued behind
        // a bare-STA transmission that runs for MINUTES (P14c). A held LINK
        // left this theory 2026-08-24 (the linked-amd carve-out below): the
        // manual says an AMD may be sent "linked or scanning", and the first
        // two-station contact proved the old gate forced an SCA first.
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        Assert.True(vm.CanSend);

        Transport.InjectLine(announcement);
        Transport.ClearSent();

        Assert.False(vm.CanSend);
        Assert.Equal(MessagesViewModel.OnAirDisabledReason, vm.SendDisabledReason);
        vm.SendCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // …and it lifts on the radio's own end-of-run line.
        Transport.InjectLine("SCANNING");
        Assert.True(vm.CanSend);
    }

    [Fact]
    public void Send_WhileLINKED_IsEnabled_TheManualsCarveOut()
    {
        // Owner 2026-08-24 (linked-amd round), manual §2.5.2.7(g): an AMD
        // "may be sent when the R/T is either linked or scanning". The send
        // itself while linked is UNCAPTURED wire behaviour (the field
        // transcript's SE went out scanning) — a refusal would surface as
        // the radio's own line; the app no longer forecloses it.
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        Transport.InjectLine("LINKED KC1HAS1           CHANNEL: 29");
        Assert.True(vm.CanSend);

        // …while every actively-transmitting state still refuses (the theory
        // above); Calling is the fourth, pinned here for the pair.
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Assert.False(vm.CanSend);
        Assert.Equal(MessagesViewModel.OnAirDisabledReason, vm.SendDisabledReason);
    }

    // ---- The Inbox (Stage 9 closed 2026-08-24, linked-amd round) -------------

    [Fact]
    public void TheInboxLandingRead_FiresOncePerSession_BareRxmsg()
    {
        // The Inbox is the DEFAULT tab, so ALE readiness fires its one read
        // (the LQA-landing precedent); a second refresh of anything sends no
        // second RXMSG. Listing shape PROVISIONAL — only the arrival form is
        // captured, so an unrecognized listing falls to the console honestly.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");           // ALE confirms with Inbox open
        Assert.Equal(1, Transport.SentLines.Count(l => l == "RXMSG"));
        Transport.InjectLine("SCANNING");
        vm.OpenInboxCommand.Execute(null);      // re-landing is NOT a re-read
        Assert.Equal(1, Transport.SentLines.Count(l => l == "RXMSG"));
    }

    [Fact]
    public void AnArrivedAmd_RendersAsAnInboxRow_NewestFirstBySlot()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("RXMSG 01   FROM N7BOI            DATE: 24-AUG-26  TIME: 21:00");
        Transport.InjectLine("  COPY  ");
        Transport.InjectLine("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        Transport.InjectLine("  TESTING  ");

        Assert.Equal(2, vm.InboxRows.Count);
        Assert.Equal("KC1HAS1", vm.InboxRows[0].From);       // slot 00 first
        Assert.Equal("TESTING", vm.InboxRows[0].Text);
        Assert.Equal("24-AUG-26 22:06", vm.InboxRows[0].WhenText);
        Assert.Equal("N7BOI", vm.InboxRows[1].From);
    }

    [Fact]
    public void RefreshInbox_ClearsTheMirror_ThenListsAgain()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        Transport.InjectLine("  TESTING  ");
        Transport.ClearSent();

        vm.RefreshInboxCommand.Execute(null);
        Assert.Equal(["RXMSG"], Transport.SentLines);
        Assert.Empty(vm.InboxRows);          // cleared; rows return as answers land
    }

    [Fact]
    public void DeleteRow_SendsRxmsgDel_ThenRelists()
    {
        // PROVISIONAL like TXMSG DEL: silent on success, verified by the
        // re-listing that follows (which the clear precedes, so a fully
        // emptied store honestly renders empty).
        var vm = AleReadyVm();
        Transport.InjectLine("RXMSG 04   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        Transport.InjectLine("  TESTING  ");
        Transport.ClearSent();

        vm.InboxRows[0].DeleteCommand.Execute(null);
        Assert.Equal(["RXMSG DEL 4", "RXMSG"], Transport.SentLines);
        Assert.Empty(vm.InboxRows);
    }

    [Fact]
    public void ASameSlotSameTextResend_AtALaterTime_UpdatesTheRow()
    {
        // Audit MINOR (2026-08-24): the row diff must include the timestamp —
        // KC1HAS1 sending "TESTING" again an hour later is news.
        var vm = AleReadyVm();
        Transport.InjectLine("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        Transport.InjectLine("  TESTING  ");
        Assert.Equal("24-AUG-26 22:06", vm.InboxRows[0].WhenText);

        Transport.InjectLine("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 23:06");
        Transport.InjectLine("  TESTING  ");
        Assert.Equal("24-AUG-26 23:06", vm.InboxRows[0].WhenText);
    }

    // ---- The AMD flow, driving Core's REAL paths ------------------------------------

    [Fact]
    public void Send_VerifiedPath_WriteReadbackSe_LogsSent_ClearsCompose()
    {
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "TEST MSG STAGE6";
        _time.Now = new DateTimeOffset(2026, 8, 3, 10, 15, 0, TimeSpan.Zero);

        vm.SendCommand.Execute(null);

        // Core's visible short sequence: write + listing + sentinel; SE only
        // after the verified read-back.
        Assert.Equal(["TXMSG 9 TEST MSG STAGE6", "TXMSG", "BAT ST"], Transport.SentLines);
        Assert.Single(vm.SentRows);
        Assert.True(vm.SentRows[0].IsPending);

        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("TEST MSG STAGE6");
        AnswerSentinel();

        Assert.Equal("SE 9 AAA", Transport.SentLines[^1]);
        Assert.False(vm.SentRows[0].IsPending);
        Assert.False(vm.SentRows[0].IsFailed);
        Assert.Contains("sent", vm.SentRows[0].StatusText);
        Assert.Equal("", vm.ComposeText);                 // cleared on success
    }

    [Fact]
    public void Send_VerifyFailPath_NoSe_LogsFailureWithReason()
    {
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "TEST MSG STAGE6";

        vm.SendCommand.Execute(null);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("STALE OLD MESSAGE");        // the radio kept something else
        AnswerSentinel();

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.Single(vm.SentRows);
        Assert.True(vm.SentRows[0].IsFailed);
        Assert.Contains("read back", vm.SentRows[0].StatusText);
        Assert.Equal("TEST MSG STAGE6", vm.ComposeText);  // kept for retry
    }

    [Fact]
    public void Send_TimeoutPath_NoSe_LogsFailure()
    {
        var vm = AleReadyVm();
        Radio.Ale.AmdVerifyTimeoutMs = 80;
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";

        vm.SendCommand.Execute(null);
        Assert.True(WaitUntil(() => vm.SentRows.Count == 1 && !vm.SentRows[0].IsPending));

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.True(vm.SentRows[0].IsFailed);
        Assert.Contains("did not answer", vm.SentRows[0].StatusText);
    }

    [Fact]
    public void Send_OneInFlight_SecondSendGuarded()
    {
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        vm.SendCommand.Execute(null);
        Transport.ClearSent();

        vm.ComposeText = "SECOND";
        Assert.False(vm.CanSend);
        Assert.Contains("already in progress", vm.SendDisabledReason);
        vm.SendCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Sent log is session-scoped ---------------------------------------------------

    [Fact]
    public void SentLog_ClearsWithTheSession()
    {
        var vm = AleReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";
        vm.SendCommand.Execute(null);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("HELLO");
        AnswerSentinel();
        Assert.Single(vm.SentRows);

        Session.Close();
        Assert.Empty(vm.SentRows);
    }

    // ---- Inbox tab: view state only (Stage 9 gate) --------------------------------------

    /// <summary>UI tweaks round 3 (U1): Inbox is the DEFAULT view of the
    /// Messages card — and opening it still sends nothing (Stage 9 gate).</summary>
    [Fact]
    public void InboxTab_IsTheDefaultView_AndOpeningItSendsNothing()
    {
        var vm = AleReadyVm();
        Assert.True(vm.IsInboxOpen);             // U1: Inbox opens first
        Assert.Empty(Transport.SentLines);       // landing on it queries nothing
    }

    [Fact]
    public void InboxTab_PureViewState_SendsNothing()
    {
        var vm = AleReadyVm();
        vm.OpenComposeCommand.Execute(null);
        Assert.False(vm.IsInboxOpen);
        vm.OpenInboxCommand.Execute(null);
        Assert.True(vm.IsInboxOpen);
        vm.OpenComposeCommand.Execute(null);
        Assert.False(vm.IsInboxOpen);
        Assert.Empty(Transport.SentLines);       // no RXMSG, nothing — gated
    }

    // =========================================================================
    // BROADCAST ROUND F4 — the compose channel picker
    // (plan-ale-broadcast-round.md §2/§3; probes P20/P20b)
    // =========================================================================

    // ---- The picker's visibility and contents, per target kind --------------

    [Fact]
    public void TheChannelPicker_IsHiddenForABookTarget_AndEmpty()
    {
        // A book send has never taken a channel argument, so the row is not
        // there at all rather than there-and-inert.
        var vm = BroadcastReadyVm();
        SelectTarget(vm, "AAA");

        Assert.False(vm.IsChannelPickerVisible);
        Assert.Empty(vm.ComposeChannelChoices);
        Assert.Null(vm.SelectedComposeChannel);
    }

    [Fact]
    public void TheChannelPicker_OnANY_ListsChannelsOnly_AndStartsUNPICKED()
    {
        // Owner ruling 3: ANY's picker is channels only and the pick is
        // REQUIRED — the radio refuses a channel-less ANY (` NO CHANS IN GRP `,
        // P20). There is no honest default to start it on.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");

        Assert.True(vm.IsChannelPickerVisible);
        Assert.Equal(["05", "12", "29"], vm.ComposeChannelChoices);
        Assert.Null(vm.SelectedComposeChannel);
        Assert.DoesNotContain("Auto", vm.ComposeChannelChoices);
    }

    [Fact]
    public void TheChannelPicker_OnALL_PutsAutoFirst_AndStartsThere()
    {
        // ALL's bare form is a real choice — the radio picks its own channel
        // and the call auto-links (P20) — so "Auto" leads the list and is the
        // default.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ALL");

        Assert.True(vm.IsChannelPickerVisible);
        Assert.Equal(["Auto", "05", "12", "29"], vm.ComposeChannelChoices);
        Assert.Equal("Auto", vm.SelectedComposeChannel);
    }

    // ---- The target-change rule (plan §2, critic F4) ------------------------

    [Fact]
    public void ChangingTheTargetKIND_ResetsTheChannel_WithNoCarryOver()
    {
        // The defect this rule exists for: ALL's "Auto" carried onto ANY would
        // build a bare `SE 9 ANY`, which the radio refuses and never transmits.
        // Every kind transition, in one pass.
        var vm = BroadcastReadyVm();

        SelectBroadcast(vm, "ALL");
        vm.SelectedComposeChannel = "29";

        SelectBroadcast(vm, "ANY");                     // ALL -> ANY
        Assert.Null(vm.SelectedComposeChannel);

        vm.SelectedComposeChannel = "12";
        SelectBroadcast(vm, "ALL");                     // ANY -> ALL
        Assert.Equal("Auto", vm.SelectedComposeChannel);

        vm.SelectedComposeChannel = "05";
        SelectTarget(vm, "AAA");                        // ALL -> book
        Assert.Null(vm.SelectedComposeChannel);
        Assert.False(vm.IsChannelPickerVisible);

        SelectBroadcast(vm, "ANY");                     // book -> ANY
        Assert.Null(vm.SelectedComposeChannel);
    }

    [Fact]
    public void RePickingTheSAMEKind_LeavesTheChannelAlone()
    {
        // The rule is about the KIND, not the pick: re-selecting the target the
        // operator is already on must not throw away the channel they just set.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";

        vm.SelectedTarget = vm.Targets.Single(t => t.Address == "ANY");
        Assert.Equal("12", vm.SelectedComposeChannel);

        // …and a book rebuild is not a kind change either.
        InjectStationBook();
        Assert.Equal("12", vm.SelectedComposeChannel);
    }

    // ---- Send gating: ANY needs a channel -----------------------------------

    [Fact]
    public void Send_OnANYWithNoChannel_DisabledWithTheExactReason()
    {
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.ComposeText = "HELLO";

        Assert.False(vm.CanSend);
        Assert.Equal(MessagesViewModel.AnyNeedsChannelReason, vm.SendDisabledReason);
        vm.SendCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // …and picking a channel lifts it.
        vm.SelectedComposeChannel = "12";
        Assert.True(vm.CanSend);
        Assert.Equal("", vm.SendDisabledReason);
    }

    [Fact]
    public void Send_OnALL_NeedsNoChannel_AutoIsAChoice()
    {
        // The asymmetry is the radio's, not the app's: `SE 9 ALL` works (P20).
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ALL");
        vm.ComposeText = "HELLO";

        Assert.True(vm.CanSend);
        Assert.Equal("", vm.SendDisabledReason);
    }

    // ---- The wire, through Core's real verified flow ------------------------

    [Fact]
    public void Send_ToANY_PutsSe9AnyNnOnTheWire_AndLogsTheChannel()
    {
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";
        vm.ComposeText = "BROADCAST TEST";

        vm.SendCommand.Execute(null);
        Assert.Equal(["TXMSG 9 BROADCAST TEST", "TXMSG", "BAT ST"], Transport.SentLines);

        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("BROADCAST TEST");
        AnswerSentinel();

        Assert.Equal("SE 9 ANY 12", Transport.SentLines[^1]);   // P20b's captured form
        Assert.False(vm.SentRows[0].IsFailed);

        // The log names the channel the send actually carried.
        Assert.Equal("ANY  (broadcast) — CH 12", vm.SentRows[0].Target);
    }

    [Fact]
    public void Send_ToALLOnAuto_PutsTheBareSe9AllOnTheWire_AndLogsNoChannel()
    {
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ALL");
        vm.ComposeText = "BROADCAST TEST";

        vm.SendCommand.Execute(null);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("BROADCAST TEST");
        AnswerSentinel();

        Assert.Equal("SE 9 ALL", Transport.SentLines[^1]);      // P20's captured form
        Assert.Equal("ALL  (broadcast)", vm.SentRows[0].Target);   // no " — CH " suffix
    }

    [Fact]
    public void Send_ToALLOnAPickedChannel_PutsSe9AllNnOnTheWire()
    {
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ALL");
        vm.SelectedComposeChannel = "05";
        vm.ComposeText = "BROADCAST TEST";

        vm.SendCommand.Execute(null);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("BROADCAST TEST");
        AnswerSentinel();

        Assert.Equal("SE 9 ALL 05", Transport.SentLines[^1]);   // P20b's twin form
        Assert.Equal("ALL  (broadcast) — CH 05", vm.SentRows[0].Target);
    }

    [Fact]
    public void Send_ToABookTarget_IsUNCHANGED_NoChannelArgument()
    {
        // The widening must not reach the existing callers: a book send is
        // byte-identical to what it was before the broadcast round.
        var vm = BroadcastReadyVm();
        SelectTarget(vm, "AAA");
        vm.ComposeText = "HELLO";

        vm.SendCommand.Execute(null);
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("HELLO");
        AnswerSentinel();

        Assert.Equal("SE 9 AAA", Transport.SentLines[^1]);
        Assert.Equal("AAA  (IND)", vm.SentRows[0].Target);
    }

    // ---- PreselectBroadcast: view state only --------------------------------

    [Fact]
    public void PreselectBroadcast_PrefillsBothPinnedRowsChannels_AndSendsNothing()
    {
        // The pane's two AMD ▸ buttons arrive here (AleViewModelTests pins the
        // caller half). A supplied channel lands in the picker; a null one
        // leaves the KIND's own default standing.
        var vm = BroadcastReadyVm();

        vm.PreselectBroadcast("ANY", "12");
        Assert.Equal("ANY", vm.SelectedTarget?.Address);
        Assert.Equal("12", vm.SelectedComposeChannel);

        vm.PreselectBroadcast("ALL", "29");
        Assert.Equal("ALL", vm.SelectedTarget?.Address);
        Assert.Equal("29", vm.SelectedComposeChannel);

        vm.PreselectBroadcast("ANY", null);
        Assert.Null(vm.SelectedComposeChannel);           // ANY's default: unpicked

        vm.PreselectBroadcast("ALL", null);
        Assert.Equal("Auto", vm.SelectedComposeChannel);  // ALL's default: the bare form

        Assert.Empty(Transport.SentLines);
    }

    // ---- Selection lifetime (plan §3) ---------------------------------------

    [Fact]
    public void TheComposeChannel_SurvivesABlankRebuild_ButAConfirmedNonBlankOneWithoutItPrunes()
    {
        // Plan §3, both directions on one fixture. A mirror that has not
        // reported is not evidence the channel is gone; one that HAS reported
        // and lacks it is.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");                     // fresh session: mirror blank
        Assert.Empty(vm.ComposeChannelChoices.Where(c => c != "Auto"));
        Assert.Equal("12", vm.SelectedComposeChannel);    // kept

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 05 ");     // reported, and 12 is not in it
        AnswerSentinel();

        Assert.Equal(["05"], vm.ComposeChannelChoices);
        Assert.Null(vm.SelectedComposeChannel);           // pruned to ANY's default
    }

    [Fact]
    public void APARTIALGroupTable_KEEPSTheComposeChannel_EvenWhenItsUnionLacksIt()
    {
        // AUDIT ROUND 1, MAJOR 1 — the same defect on the compose picker, and
        // the reason the predicate lives on the SURFACE: a single-group read
        // leaves a NON-EMPTY union that lacks the pick only because nine groups
        // have not been read. Plan §3's "confirmed-read" is the whole table.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");
        var surface = new AleSurface(Radio);
        surface.RequestChannelGroup(0);                   // ONE group answers
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.Equal(["05"], vm.ComposeChannelChoices);   // non-empty union…
        Assert.False(surface.GroupTableFullyRead);        // …partial table
        Assert.Equal("12", vm.SelectedComposeChannel);    // …so the pick STANDS

        // …and the whole-table read is what finally prunes it.
        surface.RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.True(surface.GroupTableFullyRead);
        Assert.Null(vm.SelectedComposeChannel);
    }

    // ---- The Picker null-write refusal (audit round 1, MAJOR 2) -------------

    [Fact]
    public void ABindingOriginatedNullWrite_DoesNOTClobberTheComposeChannel()
    {
        // A real MAUI Picker CLEARS its SelectedItem when its ItemsSource is
        // rebuilt blank or shorter and the TwoWay binding writes that null
        // straight in, walking past the selection-lifetime rule. A person
        // cannot UNSELECT from a Picker, so the null is refused.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";
        vm.ComposeText = "HELLO";

        vm.SelectedComposeChannel = null;                 // the Picker's clear

        Assert.Equal("12", vm.SelectedComposeChannel);
        Assert.True(vm.CanSend);                          // …and Send stays armed
    }

    [Fact]
    public void AWriteFromTheHIDDENPicker_IsRefusedToo()
    {
        // The collapsed row has no business speaking: on a book target the
        // channel row is not shown, so any write reaching the VM from it is the
        // control talking about itself, not the operator.
        var vm = BroadcastReadyVm();
        SelectTarget(vm, "AAA");
        Assert.False(vm.IsChannelPickerVisible);

        vm.SelectedComposeChannel = "12";

        Assert.Null(vm.SelectedComposeChannel);
    }

    [Fact]
    public void TheAPPSIDEPaths_STILLSetAndClearTheComposeChannel()
    {
        // The other side, and the one a blanket refusal would break: the KIND
        // reset, the PRUNE and the row-action PREFILL all still do their
        // documented transitions — they write through the private path.
        var vm = BroadcastReadyVm();

        SelectBroadcast(vm, "ALL");                       // kind reset -> "Auto"
        Assert.Equal("Auto", vm.SelectedComposeChannel);

        vm.PreselectBroadcast("ANY", "12");               // prefill, while hidden-to-visible
        Assert.Equal("12", vm.SelectedComposeChannel);

        SelectTarget(vm, "AAA");                          // kind reset -> null (book)
        Assert.Null(vm.SelectedComposeChannel);

        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "29";
        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 05 ");     // prune -> null
        AnswerSentinel();
        Assert.Null(vm.SelectedComposeChannel);
    }

    [Fact]
    public void AChoiceListRebuild_ReAnnouncesTheSELECTION_SoALivePickerReAdoptsIt()
    {
        // The refusal alone is not enough: a Picker that dropped its own
        // SelectedItem has to be TOLD to re-adopt the kept value, and it only
        // can once its items are back.
        var vm = BroadcastReadyVm();
        SelectBroadcast(vm, "ANY");
        vm.SelectedComposeChannel = "12";

        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 12 29 ");  // a DIFFERENT list
        AnswerSentinel();

        Assert.Contains(nameof(MessagesViewModel.ComposeChannelChoices), seen);
        Assert.Contains(nameof(MessagesViewModel.SelectedComposeChannel), seen);
        Assert.Equal("12", vm.SelectedComposeChannel);    // still held

        // …and a refresh that does NOT change the list stays quiet.
        seen.Clear();
        Transport.InjectLine("SCANNING");
        Assert.DoesNotContain(nameof(MessagesViewModel.SelectedComposeChannel), seen);
    }

    // ---- NOTIFICATION pins: every new bound property actually raises --------

    [Fact]
    public void EveryNewBoundProperty_RaisesPropertyChanged()
    {
        // The compose channel row binds all three; a binding is only as live as
        // its notification, and MAUI would render the first value forever
        // without a word of complaint.
        var vm = BroadcastReadyVm();
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        SelectBroadcast(vm, "ALL");

        Assert.Contains(nameof(MessagesViewModel.IsChannelPickerVisible), seen);
        Assert.Contains(nameof(MessagesViewModel.ComposeChannelChoices), seen);
        Assert.Contains(nameof(MessagesViewModel.SelectedComposeChannel), seen);

        seen.Clear();
        vm.SelectedComposeChannel = "05";
        Assert.Contains(nameof(MessagesViewModel.SelectedComposeChannel), seen);
    }

    // ---- Constitution: programmatic writes send nothing -----------------------------------

    [Fact]
    public void InjectedBookAndChatter_SendNothing()
    {
        var vm = AleReadyVm();
        InjectStationBook();
        Transport.InjectLine("SCANNING");
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("SOME STORED TEXT");
        Assert.Empty(Transport.SentLines);
        _ = vm;
    }
}
