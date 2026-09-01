using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The LQA sub-tab (plan §4.5; rebuilt by UI tweaks round 11 §4).
///
/// <para>RAN report viewer (individuals only — the radio's own restriction;
/// passive read), report rows from the verbatim RANK continuation shape
/// INCLUDING an async-survivor interleave, each row's RX/TX filled from the
/// KEYED channel mirror by ONE targeted <c>DI</c> per named channel per session
/// (owner ruling R5).</para>
///
/// <para>Scheduling is a RADIO MIRROR now, not the app's session memory: every
/// landing on the tab re-reads the queue with a bare <c>EXCH</c>, every accepted
/// write re-reads it again, and the per-row Delete acts on the ROW's own captured
/// kind and address. Both tables render §4's three-state projection — the real
/// rows, or EXACTLY ONE hyphen placeholder — and the capacity gate reads the
/// MIRROR's count, never the display collection's.</para>
///
/// <para>ROUND 15 §16: the row button is <b>Delete</b>; <b>Now</b> sends the BARE
/// STA (the radio's own one-time immediate LQA — P14) behind a read-id latch;
/// <b>Schedule</b> refuses a blank interval; the compose rows' STO commands are
/// GONE; and <b>Refresh LQA</b> re-reads the queue with one bare
/// <c>EXCH</c>.</para>
/// </summary>
public class LqaViewModelTests : SessionTestBase
{
    private LqaViewModel Vm()
        => new(new AleSurface(Radio), new ChannelSurface(Radio), Session);

    private LqaViewModel AleReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("INDAD BBB               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        Transport.ClearSent();
        return vm;
    }

    /// <summary>Verbatim bare-<c>EXCH</c> listing shape (captured 2026-08-17,
    /// bench/transcripts/phase2b-schedules).</summary>
    private static string ScheduleLine(string kind, string address, string interval, string start)
        => $"{kind} {address}              INTERVAL {interval} START TIME {start}";

    /// <summary>Land a schedule read: the VM's bare EXCH is already on the
    /// wire, so inject the listing and answer its closing sentinel.</summary>
    private void LandSchedules(params string[] rows)
    {
        foreach (var row in rows) Transport.InjectLine(row);
        AnswerSentinel();
    }

    private static string DiLine(int channel, string rx, string tx)
        => $"CH {channel:00} RxFr {rx} TxFr {tx} MODE USB AGC SL BA 2.7  RXONLY NO";

    // ---- Pickers: the radio's address-type restrictions --------------------------

    [Fact]
    public void Pickers_RanIndividualsOnly_SouSelfsOnly_ExchFlat()
    {
        var vm = AleReadyVm();

        Assert.Equal(["AAA", "BBB"], vm.RankChoices.Select(c => c.Address));
        Assert.Equal(["AAA", "BBB", "NT1"], vm.ExchChoices.Select(c => c.Address));
        Assert.Equal(["ZZZ", "TST"], vm.SouChoices.Select(c => c.Address));
    }

    // ---- RAN report (passive read) --------------------------------------------------

    [Fact]
    public void RequestReport_SendsRan()
    {
        var vm = AleReadyVm();
        vm.PreselectRankStation("AAA");
        vm.RequestReportCommand.Execute(null);
        Assert.Equal(["RAN AAA"], Transport.SentLines);
    }

    [Fact]
    public void RequestReport_NoSelection_NothingSent()
    {
        var vm = AleReadyVm();
        Assert.False(vm.RequestReportCommand.CanExecute(null));
        vm.RequestReportCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ReportRows_VerbatimRankContinuation_WithAsyncSurvivorInterleave()
    {
        // Verbatim session-08 shape; scan chatter may interleave the report
        // without ending it (the parser's survivor set) — rows keep coming.
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Transport.InjectLine("SCANNING");        // async survivor mid-report
        Transport.InjectLine("CHAN: 01  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");

        Assert.Equal(2, vm.ReportRows.Count);
        Assert.Equal("00", vm.ReportRows[0].Channel);
        Assert.Equal("---", vm.ReportRows[0].Score);
        Assert.Equal("01", vm.ReportRows[1].Channel);
    }

    [Fact]
    public void NewRankReport_ReplacesTheOldRows()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Assert.Single(vm.ReportRows);

        Transport.InjectLine("RANK  BBB ");      // new header clears
        Assert.Empty(vm.ReportRows);
        Transport.InjectLine("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Assert.Single(vm.ReportRows);
    }

    // ---- §4: the report's three-state DISPLAY projection ----------------------------

    [Fact]
    public void ReportDisplayRows_NoReport_IsExactlyOneHyphenPlaceholder()
    {
        var vm = AleReadyVm();

        Assert.Empty(vm.ReportRows);
        var row = Assert.Single(vm.ReportDisplayRows);
        Assert.Same(LqaReportRowViewModel.Placeholder, row);
        Assert.Equal(["—", "—", "—", "—", "—", "—"],
            new[] { row.Channel, row.RxText, row.TxText, row.Score, row.MeasuredSnr, row.ReceivedSnr });
    }

    [Fact]
    public void ReportDisplayRows_WithRows_IsTheRealRows_AndReturnsToThePlaceholderWhenCleared()
    {
        // BOTH states of the projection, and the return trip: a new RANK header
        // clears the rows, and the table must go back to ONE placeholder rather
        // than render nothing at all (there is no EmptyView any more).
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");

        Assert.Same(vm.ReportRows, vm.ReportDisplayRows);
        Assert.Single(vm.ReportDisplayRows);
        Assert.Equal("00", vm.ReportDisplayRows[0].Channel);

        Transport.InjectLine("RANK  BBB ");
        Assert.Same(LqaReportRowViewModel.Placeholder, Assert.Single(vm.ReportDisplayRows));
    }

    // ---- R5: RX/TX through the KEYED channel mirror ---------------------------------

    [Fact]
    public void ReportLanding_SendsOneTargetedDiPerNamedChannel_CellsAreDashesUntilAnswered()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");
        Transport.InjectLine("CHAN: 07  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");

        // ONE targeted read per NAMED channel, through the existing builder.
        Assert.Equal(["DI 0 0", "DI 7 7"], Transport.SentLines);

        // Until the answers land the cells say "—": the row is real, the
        // frequency simply is not known, and a display never invents one.
        Assert.Equal(["—", "—"], vm.ReportRows.Select(r => r.RxText));
        Assert.Equal(["—", "—"], vm.ReportRows.Select(r => r.TxText));
    }

    [Fact]
    public void TargetedDiAnswers_FillRxTxInTheMhzVocabulary_AndAccumulate()
    {
        // The keyed mirror is what makes this work: round 11 §8 made a targeted
        // answer UPSERT its channel, so the second answer does not evict the
        // first. Both rows must end up filled.
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");
        Transport.InjectLine("CHAN: 07  SCORE: 080    MEASURED SNR 15  RECEIVED SNR 14");

        Transport.InjectLine(DiLine(0, "04123000", "04123000"));
        Transport.InjectLine(DiLine(7, "07102000", "07215000"));

        Assert.Equal(["4.123 000", "7.102 000"], vm.ReportRows.Select(r => r.RxText));
        Assert.Equal(["4.123 000", "7.215 000"], vm.ReportRows.Select(r => r.TxText));
    }

    [Fact]
    public void ChannelAlreadyInTheMirror_IsNotAskedFor()
    {
        // "not in the KEYED channel mirror this session" is the R5 condition —
        // a channel some other tab already read is not re-read here.
        var vm = AleReadyVm();
        Transport.InjectLine(DiLine(0, "04123000", "04123000"));    // unsolicited/other tab
        Transport.ClearSent();

        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");

        Assert.Empty(Transport.SentLines);
        Assert.Equal("4.123 000", vm.ReportRows[0].RxText);
    }

    [Fact]
    public void TheSessionSet_PreventsReSends_EvenWhenTheAnswerNeverCame()
    {
        // The per-VM session set is the thing being pinned: the first report
        // spends one DI on channel 00, the answer never arrives, and a SECOND
        // report naming the same channel must not spend another.
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");
        Assert.Equal(["DI 0 0"], Transport.SentLines);
        Transport.ClearSent();

        Transport.InjectLine("RANK  BBB ");
        Transport.InjectLine("CHAN: 00  SCORE: 090    MEASURED SNR 20  RECEIVED SNR 18");

        Assert.Empty(Transport.SentLines);
        Assert.Equal("—", vm.ReportRows[0].RxText);
    }

    [Fact]
    public void TheSessionSet_IsSessionScoped_AndTheReadIsAsked_AgainAfterAReconnect()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");
        Assert.Equal(["DI 0 0"], Transport.SentLines);

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.ClearSent();

        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");
        Assert.Equal(["DI 0 0"], Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void OutsideAle_AReportLanding_SendsNoChannelReads()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: 095    MEASURED SNR 21  RECEIVED SNR 19");

        Assert.Empty(Transport.SentLines);
        _ = vm;
    }

    // ---- §4: the schedule MIRROR + its landing read ---------------------------------

    [Fact]
    public void LandingOnTheTab_ReadsTheQueueWithABareExch_EveryTime()
    {
        // The editors-read-fresh tier: EVERY landing re-reads. One cheap
        // command plus its closing sentinel — no lazy-once.
        var vm = AleReadyVm();

        vm.OnLqaTabOpened();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        Transport.ClearSent();

        vm.OnLqaTabOpened();
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void LandingOutsideAle_ReadsNothing()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.OnLqaTabOpened();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ScheduleRows_MirrorTheRadiosListing_InTheRadiosOrder()
    {
        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(
            ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"),
            ScheduleLine("SOUND", "TST", "02:00", "23:15"));

        Assert.Equal(["EXCH", "SOU"], vm.ScheduleRows.Select(r => r.KindText));
        Assert.Equal(["AAA", "TST"], vm.ScheduleRows.Select(r => r.Address));
        Assert.Equal(["01:00", "02:00"], vm.ScheduleRows.Select(r => r.IntervalText));
        Assert.Equal(["22:34", "23:15"], vm.ScheduleRows.Select(r => r.StartText));
    }

    [Fact]
    public void ScheduleDisplayRows_UnreadAndReadEmpty_BothRenderTheOneHyphenRow()
    {
        // Decided: the two states render IDENTICALLY, so the projection is
        // pinned in both — an "unread" that rendered differently would be a
        // second, unspecified display state.
        var vm = AleReadyVm();

        var unread = Assert.Single(vm.ScheduleDisplayRows);          // never read
        Assert.Same(LqaScheduleRowViewModel.Placeholder, unread);
        Assert.Empty(vm.ScheduleRows);

        vm.OnLqaTabOpened();
        Transport.InjectLine("NO LQA SCHEDULED");                    // read-EMPTY
        AnswerSentinel();

        var readEmpty = Assert.Single(vm.ScheduleDisplayRows);
        Assert.Same(LqaScheduleRowViewModel.Placeholder, readEmpty);
        Assert.Empty(vm.ScheduleRows);

        Assert.Equal(["—", "—", "—", "—"],
            new[] { readEmpty.KindText, readEmpty.Address, readEmpty.IntervalText, readEmpty.StartText });
        Assert.False(readEmpty.CanDelete);
    }

    [Fact]
    public void ScheduleDisplayRows_WithRows_AreTheRealRows()
    {
        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));

        Assert.Same(vm.ScheduleRows, vm.ScheduleDisplayRows);
        Assert.True(Assert.Single(vm.ScheduleDisplayRows).CanDelete);
    }

    [Fact]
    public void AnInvalidatedMirror_ReturnsTheTableToThePlaceholder()
    {
        // The tab re-renders from mirror events: Core puts the schedule mirror
        // back to UNREAD on DELAD/ERASE (§8), and this display must follow it
        // rather than keep showing rows the radio may no longer hold.
        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        Assert.Single(vm.ScheduleRows);

        new AleSurface(Radio).RemoveAddress("AAA");                  // DELAD invalidates

        Assert.Empty(vm.ScheduleRows);
        Assert.Same(LqaScheduleRowViewModel.Placeholder, Assert.Single(vm.ScheduleDisplayRows));
    }

    // ---- §4: per-row Delete (captured row; unconfirmed, deliberately) ---------------

    [Fact]
    public void RowDelete_SendsAgainstTheRowsOwnKindAndAddress_ThenReReads()
    {
        // The picker points somewhere ELSE on purpose: a row action that read
        // the selection would stop the wrong schedule, and both rows look the
        // same in the markup.
        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(
            ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"),
            ScheduleLine("SOUND", "TST", "02:00", "23:15"));
        vm.SelectedExchTarget = vm.ExchChoices[1];                   // BBB
        vm.SelectedSouSelf = vm.SouChoices[0];                       // ZZZ
        var exchangeRow = vm.ScheduleRows[0];
        var soundRow = vm.ScheduleRows[1];
        Transport.ClearSent();

        soundRow.DeleteCommand.Execute(null);
        Assert.Equal(["SOU STO TST", "EXCH", "BAT ST"], Transport.SentLines);

        // Land that re-read (Core serializes the store queue — a second read
        // requested while one is in flight COALESCES and sends nothing).
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        Transport.ClearSent();

        // The CAPTURED row still knows its own kind and address, even though
        // the mirror has been replaced under it since.
        exchangeRow.DeleteCommand.Execute(null);
        Assert.Equal(["EXCH STO AAA", "EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void RowDelete_IsUnconfirmed_ByDecision()
    {
        // PINNED DELIBERATE (§4): per-row Delete follows the round-10 per-row
        // Removes precedent, so the §5 popup matrix does NOT extend to it. The
        // structural proof is that this VM has no confirmation seam AT ALL —
        // it cannot ask, so no future edit can quietly start asking without
        // failing here.
        Assert.DoesNotContain(
            typeof(LqaViewModel).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(Falcon.App.Core.Services.IConfirmationPrompt));

        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        Transport.ClearSent();

        vm.ScheduleRows[0].DeleteCommand.Execute(null);
        Assert.Equal("EXCH STO AAA", Transport.SentLines[0]);        // straight to the wire
    }

    [Fact]
    public void ThePlaceholderRow_HasNothingToDelete()
    {
        var vm = AleReadyVm();
        var placeholder = Assert.Single(vm.ScheduleDisplayRows);

        Assert.False(placeholder.CanDelete);
        Assert.False(placeholder.DeleteCommand.CanExecute(null));
        placeholder.DeleteCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Builders: send, then re-read ----------------------------------------------

    [Fact]
    public void ExchSta_SendsAndReReadsTheQueue()
    {
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA
        vm.ExchIntervalText = "00:30";

        vm.StartExchangeCommand.Execute(null);
        Assert.Equal(["EXCH STA AAA 00:30", "EXCH", "BAT ST"], Transport.SentLines);
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "00:30", "22:34"));
        Assert.Equal("AAA", Assert.Single(vm.ScheduleRows).Address);
        Transport.ClearSent();

        vm.ScheduleRows[0].DeleteCommand.Execute(null);
        Assert.Equal(["EXCH STO AAA", "EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void SouSta_SendsAndReReadsTheQueue()
    {
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[1];           // TST
        vm.SouIntervalText = "02:00";
        vm.SouStartText = "12:30";

        vm.StartSoundingCommand.Execute(null);
        Assert.Equal(["SOU STA TST 02:00 12:30", "EXCH", "BAT ST"], Transport.SentLines);
        LandSchedules(ScheduleLine("SOUND", "TST", "02:00", "12:30"));
        Assert.Equal("TST", Assert.Single(vm.ScheduleRows).Address);
        Transport.ClearSent();

        vm.ScheduleRows[0].DeleteCommand.Execute(null);
        Assert.Equal(["SOU STO TST", "EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ARereadRequestedWhileOneIsInFlight_Coalesces_ItDoesNotDoubleTheWire()
    {
        // Core's store queue serializes reads; the VM asks freely after every
        // write and Core folds the request into the operation already open.
        // Pinned so the "re-read after every write" rule cannot be read as
        // "one EXCH per write, always".
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.ExchIntervalText = "01:00";

        vm.StartExchangeCommand.Execute(null);
        Assert.Equal(["EXCH STA AAA 01:00", "EXCH", "BAT ST"], Transport.SentLines);
        Transport.ClearSent();

        // ROUND 15 F-5: the second asker is the Refresh button — the same read,
        // requested while one is open, and Core folds it in.
        vm.RefreshCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void BlankIntervalAndStart_AreOmitted_ByTheNowCommand()
    {
        // RE-KEYED (round 15 F-2): the bare form is what Now sends. On the
        // SCHEDULE buttons a blank interval is now REFUSED — the pin for that
        // half is Schedule_RefusesABlankInterval_AndNamesNow below.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[1];       // BBB
        vm.NowExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA BBB", Transport.SentLines[0]);
    }

    [Fact]
    public void AlreadyQueuedTarget_IsNotPreBlocked_TheWireRefusesIt()
    {
        // §4: "No pre-block on queued targets" — ` ADR ALREADY QUED ` maps loud
        // through the P1 vocabulary, and the app does not invent a client-side
        // rule the radio never asked for.
        var vm = AleReadyVm();
        vm.OnLqaTabOpened();
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA — already queued
        vm.ExchIntervalText = "01:00";
        Transport.ClearSent();

        Assert.True(vm.StartExchangeCommand.CanExecute(null));
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 01:00", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
    }

    // ---- ROUND 15 §16 F-2/F-3/F-5: Now, Schedule, the latch, Refresh ---------------

    [Fact]
    public void Schedule_RefusesABlankInterval_AndNamesNow_NothingReachesTheWire()
    {
        // The behaviour change F-2 makes: a blank interval USED to be the bare
        // immediate STA, silently. It is now refused on both compose rows, and
        // the message names the control that does mean it.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA
        vm.SelectedSouSelf = vm.SouChoices[1];           // TST
        vm.ExchStartText = "22:34";                      // a start alone is not a schedule

        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Equal("Interval required — use Now for a one-time LQA.", vm.ExchError);

        vm.StartSoundingCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Equal("Interval required — use Now for a one-time LQA.", vm.SouError);

        // …and a blank START is still legal — that one means clock + interval.
        vm.ExchIntervalText = "01:00";
        vm.ExchStartText = "";
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 01:00", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
    }

    [Fact]
    public void Now_SendsTheBareForm_IgnoringBothEntries_ThenReReads()
    {
        // P14: the bare STA is the radio's own one-time immediate LQA. The two
        // entries are IGNORED on purpose — this control IS "no interval, no
        // start", so a half-typed schedule cannot ride out on it.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA
        vm.ExchIntervalText = "01:00";                   // deliberately filled…
        vm.ExchStartText = "22:34";                      // …and deliberately ignored

        vm.NowExchangeCommand.Execute(null);
        Assert.Equal(["EXCH STA AAA", "EXCH", "BAT ST"], Transport.SentLines);
        Assert.Equal("", vm.ExchError);
        LandSchedules();
        Transport.ClearSent();

        vm.SelectedSouSelf = vm.SouChoices[1];           // TST
        vm.SouIntervalText = "02:00";
        vm.NowSoundingCommand.Execute(null);
        Assert.Equal(["SOU STA TST", "EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void Now_ClearsAStaleRowError_BeforeItSends()
    {
        // Critic F60: the operator reads "Interval required — use Now", presses
        // Now, and the error that told them to must not still be on screen.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.StartExchangeCommand.Execute(null);           // blank interval -> the error
        Assert.Contains("Interval required", vm.ExchError);
        Transport.ClearSent();

        vm.NowExchangeCommand.Execute(null);
        Assert.Equal("", vm.ExchError);
        Assert.Equal("EXCH STA AAA", Transport.SentLines[0]);
        Assert.Equal("", vm.SouError);                   // the other row untouched
    }

    [Theory]
    [InlineData("CALLING  AAA              CHANNEL: 01")]
    [InlineData("SENDING  AAA              CHANNEL: 01")]
    [InlineData("LINKED AAA")]
    // ROUND 15 item I: the three LQA states this VM's term was WIDENED to
    // (P14b/P14c). A Now pressed during a running LQA would queue a second
    // transmission behind a minutes-long one.
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30")]
    [InlineData("LQA/SOUND")]
    public void OnAir_WithholdsNowAndSchedule_ButNeverDeleteRefreshOrRank(string announcement)
    {
        // §16's truth table (critic F70). The transmit controls are withheld
        // while the radio says it is on air; the ones that get the operator OUT
        // of that state — or that only read — are not.
        var vm = AleReadyVm();
        vm.PreselectRankStation("AAA");
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.SelectedSouSelf = vm.SouChoices[1];
        vm.ExchIntervalText = "01:00";
        vm.SouIntervalText = "01:00";
        vm.OnLqaTabOpened();
        LandSchedules(ScheduleLine("EXCHANGE", "AAA", "01:00", "22:34"));
        Assert.True(vm.NowExchangeCommand.CanExecute(null));      // before the line

        Transport.InjectLine(announcement);
        Transport.ClearSent();

        Assert.False(vm.NowExchangeCommand.CanExecute(null));
        Assert.False(vm.NowSoundingCommand.CanExecute(null));
        Assert.False(vm.StartExchangeCommand.CanExecute(null));
        Assert.False(vm.StartSoundingCommand.CanExecute(null));

        Assert.True(vm.ScheduleRows[0].CanDelete);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.RequestReportCommand.CanExecute(null));

        // The bodies are dead too — Execute does not consult CanExecute.
        vm.NowExchangeCommand.Execute(null);
        vm.NowSoundingCommand.Execute(null);
        vm.StartExchangeCommand.Execute(null);
        vm.StartSoundingCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TwoNowPresses_BeforeTheReReadLands_PutExactlyOneStaOnTheWire()
    {
        // Critic F57: SendLine ENQUEUES behind the prompt gate, so two fast
        // presses would queue two TRANSMISSIONS. The latch is what makes the
        // second press impossible, and the re-read's answer is what lifts it.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.SelectedSouSelf = vm.SouChoices[1];           // TST

        vm.NowSoundingCommand.Execute(null);
        Assert.Equal(["SOU STA TST", "EXCH", "BAT ST"], Transport.SentLines);
        Assert.True(vm.IsNowInFlight);
        Assert.False(vm.NowSoundingCommand.CanExecute(null));
        Assert.False(vm.NowExchangeCommand.CanExecute(null));     // BOTH observe it
        Transport.ClearSent();

        vm.NowSoundingCommand.Execute(null);             // the second press
        Assert.Empty(Transport.SentLines);

        LandSchedules();                                 // its own re-read answers
        Assert.False(vm.IsNowInFlight);
        Assert.True(vm.NowSoundingCommand.CanExecute(null));
    }

    [Fact]
    public void AnOlderReadsLanding_DoesNotReleaseTheLatch_OnlyNowsOwnReadDoes()
    {
        // Critic F71: correlated by READ ID, not by "a schedule read landed".
        // Here the tab's landing read is still open when Now presses, so Now's
        // re-read is a DIFFERENT, queued operation — and the older one's answer
        // must leave the button held.
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[1];           // TST

        vm.OnLqaTabOpened();                             // read #1 — active
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        Transport.ClearSent();

        vm.NowSoundingCommand.Execute(null);             // its re-read coalesces behind #1
        Assert.Equal(["SOU STA TST"], Transport.SentLines);
        Assert.True(vm.IsNowInFlight);
        Transport.ClearSent();

        LandSchedules(ScheduleLine("SOUND", "TST", "01:00", "17:30"));   // #1 answers
        Assert.True(vm.IsNowInFlight);                   // NOT the read Now is waiting on
        Assert.False(vm.NowSoundingCommand.CanExecute(null));

        // …and that landing dispatches read #2 — the one Now is waiting on.
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        LandSchedules(ScheduleLine("SOUND", "TST", "01:00", "17:30"));
        Assert.False(vm.IsNowInFlight);
    }

    [Fact]
    public void ARefreshPressedWhileNowIsInFlight_IsAllowed_AndDoesNotReleaseTheLatch()
    {
        // F-5: Refresh is never latched — it is the same read the latch waits
        // on, and Core's single outstanding queue serialises them.
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[1];

        vm.NowSoundingCommand.Execute(null);             // Now's read is ACTIVE
        Transport.ClearSent();

        Assert.True(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null);
        Assert.Empty(Transport.SentLines);               // coalesced
        Assert.True(vm.IsNowInFlight);                   // and NOT released
        Assert.False(vm.NowSoundingCommand.CanExecute(null));

        LandSchedules();                                 // Now's own read answers
        Assert.False(vm.IsNowInFlight);
    }

    [Fact]
    public void ASessionDrop_ReleasesTheNowLatch()
    {
        // The re-read the latch waits on can never land now — the session that
        // owned it is gone. A latch that survived would leave Now dead for the
        // whole of the NEXT session.
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[1];
        vm.NowSoundingCommand.Execute(null);
        Assert.True(vm.IsNowInFlight);

        Session.Close();

        Assert.False(vm.IsNowInFlight);
    }

    [Fact]
    public void Refresh_SendsExactlyOneBareExch_AndTheAnswerRendersTheRows()
    {
        // F-5's whole body: ONE bare EXCH (plus the sentinel every read is
        // bracketed with) — no RAN, no picker re-poll, no write.
        var vm = AleReadyVm();

        Assert.True(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null);
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);

        LandSchedules(ScheduleLine("SOUND", "TST", "01:00", "17:30"));
        Assert.Equal("TST", Assert.Single(vm.ScheduleRows).Address);
    }

    [Fact]
    public void Refresh_IsDeadUntilTheRadioConfirmsAle()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.False(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null);                 // Execute skips CanExecute
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("ALE>");
        Transport.ClearSent();
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void TheComposeRowsStopCommands_AreGone()
    {
        // F-3's absence pin (invariant 5). Reflection, because a deleted member
        // cannot be referenced from a compiling test — and a binding left
        // pointing at one resolves to NOTHING silently in MAUI.
        Assert.Null(typeof(LqaViewModel).GetProperty("StopExchangeCommand"));
        Assert.Null(typeof(LqaViewModel).GetProperty("StopSoundingCommand"));
        Assert.Null(typeof(LqaScheduleRowViewModel).GetProperty("StopCommand"));
        Assert.Null(typeof(LqaScheduleRowViewModel).GetProperty("CanStop"));

        // Anti-vacuity: the same lookups find what replaced them.
        Assert.NotNull(typeof(LqaViewModel).GetProperty(nameof(LqaViewModel.NowExchangeCommand)));
        Assert.NotNull(typeof(LqaViewModel).GetProperty(nameof(LqaViewModel.NowSoundingCommand)));
        Assert.NotNull(typeof(LqaViewModel).GetProperty(nameof(LqaViewModel.RefreshCommand)));
        Assert.NotNull(typeof(LqaScheduleRowViewModel)
            .GetProperty(nameof(LqaScheduleRowViewModel.DeleteCommand)));
    }

    // ---- Client bounds: both sides of every bound ----------------------------------

    [Fact]
    public void BadHhMm_InlineError_NothingReachesTheWire()
    {
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.ExchIntervalText = "9:5";

        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("hh:mm", vm.ExchError);

        vm.ExchIntervalText = "00:30";
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 00:30", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
    }

    /// <summary>Owner 2026-08-30: four digits without the colon normalize to
    /// hh:mm — in EVERY time field (interval and start, both kinds), on the
    /// WIRE as the colon form. The range check still rules the normalized
    /// value, and anything shorter than four digits stays refused (ambiguous).</summary>
    [Fact]
    public void FourDigitsWithoutTheColon_NormalizeToHhMm_OnTheWire()
    {
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA

        vm.ExchIntervalText = "0130";
        vm.ExchStartText = "2145";
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 01:30 21:45", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
        Transport.ClearSent();

        vm.SelectedSouSelf = vm.SouChoices[0];
        vm.SouIntervalText = "0100";
        vm.SouStartText = "0930";
        vm.StartSoundingCommand.Execute(null);
        Assert.EndsWith(" 01:00 09:30", Transport.SentLines[0], StringComparison.Ordinal);
        Assert.Equal("", vm.SouError);
        Transport.ClearSent();

        vm.ExchIntervalText = "9999";                    // normalized, then range-refused
        vm.ExchStartText = "";
        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("00:00-23:59", vm.ExchError);

        vm.ExchIntervalText = "130";                     // three digits: ambiguous, refused
        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("hh:mm", vm.ExchError);
    }

    [Fact]
    public void HourBound_23IsLegal_24IsRefused_NothingSent()
    {
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA

        vm.ExchIntervalText = "23:59";                   // boundary: legal
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 23:59", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
        Transport.ClearSent();

        vm.ExchIntervalText = "24:00";                   // boundary: refused
        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("00:00-23:59", vm.ExchError);
    }

    [Fact]
    public void MinuteBound_59IsLegal_60IsRefused_NothingSent()
    {
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[1];           // TST

        vm.SouIntervalText = "01:00";
        vm.SouStartText = "23:59";                       // boundary: legal
        vm.StartSoundingCommand.Execute(null);
        Assert.Equal("SOU STA TST 01:00 23:59", Transport.SentLines[0]);
        Assert.Equal("", vm.SouError);
        Transport.ClearSent();

        vm.SouStartText = "23:60";                       // boundary: refused
        vm.StartSoundingCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("00:00-23:59", vm.SouError);
    }

    [Fact]
    public void IntervalFloor_0001IsLegal_0000IsRefused_NothingSent()
    {
        // §4's own bound: an interval of zero is not a schedule, and the radio
        // does not validate intervals at all — it would simply store 00:00.
        // The START field keeps the full 00:00-23:59 range: midnight is a time.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];       // AAA

        vm.ExchIntervalText = "00:01";                   // boundary: legal
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 00:01", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
        Transport.ClearSent();

        vm.ExchIntervalText = "00:00";                   // boundary: refused
        vm.StartExchangeCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("at least 00:01", vm.ExchError);

        vm.ExchIntervalText = "01:00";
        vm.ExchStartText = "00:00";                      // a START of 00:00 is fine
        vm.StartExchangeCommand.Execute(null);
        Assert.Equal("EXCH STA AAA 01:00 00:00", Transport.SentLines[0]);
        Assert.Equal("", vm.ExchError);
    }

    [Fact]
    public void SouBounds_AreEnforcedOnItsOwnRow()
    {
        var vm = AleReadyVm();
        vm.SelectedSouSelf = vm.SouChoices[0];           // ZZZ
        vm.SouIntervalText = "99:99";

        vm.StartSoundingCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("00:00-23:59", vm.SouError);
        Assert.Equal("", vm.ExchError);                  // the other row is untouched
    }

    // ---- §4: the capacity gate reads the MIRROR ------------------------------------

    private void LandQueueOf(LqaViewModel vm, int rows)
    {
        vm.OnLqaTabOpened();
        LandSchedules([.. Enumerable.Range(0, rows)
            .Select(i => ScheduleLine("EXCHANGE", $"Q{i:00}", "01:00", "22:34"))]);
    }

    [Fact]
    public void QueueCapacity_NineAllowsAdd_TenDisablesBothStaButtons_WithTheReason()
    {
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];
        vm.SelectedSouSelf = vm.SouChoices[0];

        LandQueueOf(vm, 9);                              // the 9 -> 10 boundary
        Assert.Equal(9, vm.ScheduleRows.Count);
        Assert.True(vm.StartExchangeCommand.CanExecute(null));
        Assert.True(vm.StartSoundingCommand.CanExecute(null));
        Assert.Equal("", vm.QueueFullReason);

        Transport.ClearSent();
        LandQueueOf(vm, 10);
        Assert.Equal(10, vm.ScheduleRows.Count);
        Assert.False(vm.StartExchangeCommand.CanExecute(null));
        Assert.False(vm.StartSoundingCommand.CanExecute(null));
        Assert.Equal("Queue full (10)", vm.QueueFullReason);

        // The disabled command is dead in the BODY too (Execute does not
        // consult CanExecute).
        Transport.ClearSent();
        vm.StartExchangeCommand.Execute(null);
        vm.StartSoundingCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // NOW is never capacity-gated: the bare STA writes no row at all
        // (P14 run 1), so a full queue cannot refuse it. Nor are Delete and
        // Refresh — a full queue is exactly when the operator needs to empty
        // it and to see what is in it.
        Assert.True(vm.NowExchangeCommand.CanExecute(null));
        Assert.True(vm.NowSoundingCommand.CanExecute(null));
        Assert.True(vm.ScheduleRows[0].CanDelete);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void TheMirrorAndTheDisplay_DivergeOnlyAtZero_WhichIsWhyTheNextPinIsStructural()
    {
        // The OBSERVABLE half, and an honest statement of its limit. The
        // projection puts ONE placeholder row on screen when the mirror is
        // empty, so mirror and display differ at 0/1 and AGREE everywhere
        // above it — including at the 9->10 capacity boundary. That is why no
        // behavioural test can distinguish a gate reading the mirror from one
        // reading the display (audit round 1, MAJOR-2: the auditor swapped
        // them and all 36 tests stayed green). The dependency itself is pinned
        // STRUCTURALLY below.
        var vm = AleReadyVm();
        vm.SelectedExchTarget = vm.ExchChoices[0];

        vm.OnLqaTabOpened();
        Transport.InjectLine("NO LQA SCHEDULED");
        AnswerSentinel();

        Assert.Single(vm.ScheduleDisplayRows);           // the display says 1…
        Assert.Empty(vm.ScheduleRows);                   // …the mirror says 0
        Assert.Equal("", vm.QueueFullReason);
        Assert.True(vm.StartExchangeCommand.CanExecute(null));

        LandQueueOf(vm, LqaViewModel.ScheduleCapacity);
        Assert.Equal(vm.ScheduleRows.Count, vm.ScheduleDisplayRows.Count);
        Assert.Equal("Queue full (10)", vm.QueueFullReason);
    }

    [Fact]
    public void TheCapacityGate_StructurallyReadsTheMirrorField_NeverADisplayCollection()
    {
        // INVARIANT 6, pinned where it actually lives. "Display projections
        // never contaminate mirror counts" is a fact about which VALUE the gate
        // reads, and at the capacity boundary the two values are equal in every
        // reachable state — so the only pin that can falsify it is one that
        // reads the gate's own source.
        //
        // The scan is STRUCTURAL, not raw-text: comments and string/char
        // literals are stripped first, so a doc comment naming
        // ScheduleDisplayRows (there is one, right above the field) cannot fool
        // it in either direction, and the expression is located by its
        // DECLARATION rather than by any mention of the name.
        var source = StrippedSource(LqaViewModelPath);
        var gate = MemberExpression(source, "bool IsQueueFull");

        Assert.Contains("_mirroredScheduleCount", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Rows", gate, StringComparison.Ordinal);

        // …and the field it reads is itself fed from the MIRROR, so the
        // dependency cannot be laundered one level up instead.
        var assignments = Assignments(source, "_mirroredScheduleCount");
        Assert.Single(assignments);
        Assert.DoesNotContain("Rows", assignments[0], StringComparison.Ordinal);
        Assert.Contains("schedules", assignments[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheStructuralScan_KillsTheExactMutation_AndIsNotFooledByCommentsOrStrings()
    {
        // ANTI-VACUITY for the pin above, driven on synthetic sources: the
        // reader must ACCEPT the real shape, REJECT the auditor's exact
        // mutation (_mirroredScheduleCount -> ScheduleDisplayRows.Count), and
        // reach neither verdict from a comment or a string literal that merely
        // mentions the names.
        const string good = """
            /// <summary>Never ScheduleDisplayRows.Count — see invariant 6.</summary>
            private bool IsQueueFull => _mirroredScheduleCount >= ScheduleCapacity;
            """;
        const string mutated = """
            /// <summary>Reads the mirror: _mirroredScheduleCount.</summary>
            private bool IsQueueFull => ScheduleDisplayRows.Count >= ScheduleCapacity;
            """;
        const string laundered = """
            private bool IsQueueFull => _mirroredScheduleCount >= ScheduleCapacity;
            void R() { _mirroredScheduleCount = ScheduleDisplayRows.Count; }
            """;

        // The real shape passes.
        var goodGate = MemberExpression(StripCommentsAndLiterals(good), "bool IsQueueFull");
        Assert.Contains("_mirroredScheduleCount", goodGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Rows", goodGate, StringComparison.Ordinal);

        // The auditor's mutation fails BOTH halves — and its doc comment,
        // which names the mirror field, does not rescue it.
        var mutatedGate = MemberExpression(StripCommentsAndLiterals(mutated), "bool IsQueueFull");
        Assert.DoesNotContain("_mirroredScheduleCount", mutatedGate, StringComparison.Ordinal);
        Assert.Contains("Rows", mutatedGate, StringComparison.Ordinal);

        // Laundering it through the field fails the assignment half.
        var launderedAssignment = Assignments(StripCommentsAndLiterals(laundered), "_mirroredScheduleCount");
        Assert.Contains("Rows", Assert.Single(launderedAssignment), StringComparison.Ordinal);

        // The stripper itself: comments and every literal form this file uses
        // (quoted, verbatim, char) leave no text behind to match on.
        var stripped = StripCommentsAndLiterals("""
            var a = "ScheduleDisplayRows";      // ScheduleDisplayRows
            var b = @"^\d{2}:\d{2}$";           /* ScheduleDisplayRows */
            var c = '"'; var d = "esc\"aped ScheduleDisplayRows"; var e = KEEP;
            """);
        Assert.DoesNotContain("ScheduleDisplayRows", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\d{2}", stripped, StringComparison.Ordinal);
        Assert.Contains("KEEP", stripped, StringComparison.Ordinal);
    }

    // ---- the structural reader (comment- and literal-stripped, never raw) -------

    private static string LqaViewModelPath => Path.Combine(
        RepoRoot(), "src", "Falcon.App.Core", "ViewModels", "LqaViewModel.cs");

    private static string StrippedSource(string path)
    {
        Assert.True(File.Exists(path), "source missing: " + path);
        return StripCommentsAndLiterals(File.ReadAllText(path));
    }

    /// <summary>An expression-bodied member's RIGHT-HAND SIDE, located by its
    /// DECLARATION (<c>"bool IsQueueFull"</c>) rather than by the name alone —
    /// the name also appears at every call site, and the first of those would
    /// be a different expression entirely.</summary>
    private static string MemberExpression(string strippedSource, string declaration)
    {
        int at = strippedSource.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(at >= 0, $"declaration not found: {declaration}");
        int arrow = strippedSource.IndexOf("=>", at, StringComparison.Ordinal);
        Assert.True(arrow > 0, $"{declaration} is not expression-bodied");
        int end = strippedSource.IndexOf(';', arrow);
        Assert.True(end > arrow, $"{declaration} has no terminator");
        return strippedSource[(arrow + 2)..end];
    }

    /// <summary>Every plain assignment to <paramref name="field"/>, as its
    /// right-hand side. Compound and comparison operators do not match: the
    /// search is for the field followed by exactly <c>" = "</c>.</summary>
    private static List<string> Assignments(string strippedSource, string field)
    {
        var found = new List<string>();
        string needle = field + " =";
        for (int i = strippedSource.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = strippedSource.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            if (strippedSource[i + needle.Length] == '=') continue;      // "==" is a read
            int end = strippedSource.IndexOf(';', i);
            found.Add(strippedSource[(i + needle.Length)..end]);
        }
        return found;
    }

    /// <summary>Remove comments and string/char literals so a scan matches
    /// CODE. Handles the three literal forms this codebase uses: quoted (with
    /// escapes), verbatim <c>@"…"</c> (where <c>""</c> is an escaped quote),
    /// and char.</summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                sb.Append(' ');
                continue;
            }
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] != '"') { i++; continue; }
                    if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                    i++;
                    break;
                }
                sb.Append("\"\"");
                continue;
            }
            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;                                    // past the closing quote
                sb.Append(quote).Append(quote);
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }

    // ---- Gating --------------------------------------------------------------------

    [Fact]
    public void OutsideAle_ControlsDisabledWithReason_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.ClearSent();

        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);

        vm.PreselectRankStation("AAA");
        vm.RequestReportCommand.Execute(null);
        vm.SelectedExchTarget = vm.ExchChoices.Count > 0 ? vm.ExchChoices[0] : null;
        vm.StartExchangeCommand.Execute(null);
        vm.NowExchangeCommand.Execute(null);
        vm.RefreshCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Constitution: nothing but the R5 reads leaves on an injected report --------

    [Fact]
    public void InjectedReportAndChatter_SendOnlyTheTargetedChannelReads()
    {
        // The constitution pin, updated for R5: a report landing is allowed to
        // spend exactly its targeted DI reads and NOTHING else — no RAN, no
        // schedule read, no write.
        var vm = AleReadyVm();
        Transport.InjectLine("RANK  AAA ");
        Transport.InjectLine("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Transport.InjectLine("SCANNING");

        Assert.Equal(["DI 0 0"], Transport.SentLines);
        _ = vm;
    }
    // ---- Heard stations (owner design 2026-08-24; field capture #2) ----------

    private (LqaViewModel Vm, TestTime Time) HeardVm()
    {
        var time = new TestTime { Now = new DateTimeOffset(2026, 8, 24, 23, 12, 0, TimeSpan.Zero) };
        var vm = new LqaViewModel(new AleSurface(Radio), new ChannelSurface(Radio), Session, time);
        ConnectReady();
        Transport.InjectLine("ALE>");
        return (vm, time);
    }

    [Fact]
    public void AHeardSounding_BecomesARow_StationChannelsTime()
    {
        var (vm, _) = HeardVm();
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        var row = Assert.Single(vm.HeardRows);
        Assert.Equal("KC1HAS1", row.Station);
        Assert.Equal("27", row.ChannelsText);
        Assert.Equal("23:12", row.LastHeardText);
    }

    [Fact]
    public void ChannelsCoalescePerStation_SortedNumerically_AndDuplicatesDont()
    {
        var (vm, time) = HeardVm();
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        time.Now = time.Now.AddMinutes(1);
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 25");
        time.Now = time.Now.AddMinutes(1);
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");   // again
        var row = Assert.Single(vm.HeardRows);
        Assert.Equal("25 27", row.ChannelsText);
        Assert.Equal("23:14", row.LastHeardText);
    }

    [Fact]
    public void AGapBeyondTenMinutes_StartsTheChannelListFresh()
    {
        var (vm, time) = HeardVm();
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        time.Now = time.Now.AddMinutes(LqaViewModel.PassGapMinutes + 1);
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 25");
        var row = Assert.Single(vm.HeardRows);
        Assert.Equal("25", row.ChannelsText);              // the old pass's 27 is gone
    }

    [Fact]
    public void ResponsesFeedTheSameTable_AndNewestHeardStationSortsFirst()
    {
        var (vm, time) = HeardVm();
        Transport.InjectLine("SOUND FROM:   AAA             CHANNEL: 11");
        time.Now = time.Now.AddMinutes(2);
        Transport.InjectLine("RESP  FROM:   KC1HAS1         CHANNEL: 29");
        Assert.Equal(2, vm.HeardRows.Count);
        Assert.Equal("KC1HAS1", vm.HeardRows[0].Station);  // newest first
        Assert.Equal("AAA", vm.HeardRows[1].Station);
    }

    [Fact]
    public void Clear_EmptiesTheTable_AndTheConsumedLineDoesNotResurrect()
    {
        var (vm, _) = HeardVm();
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        Assert.Single(vm.HeardRows);
        vm.ClearHeardCommand.Execute(null);
        Assert.Empty(vm.HeardRows);
        Transport.InjectLine("SCANNING");                  // any re-render
        Assert.Empty(vm.HeardRows);
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 25");
        Assert.Equal("25", Assert.Single(vm.HeardRows).ChannelsText);   // a NEW line does
    }

    [Fact]
    public void ASessionDrop_ClearsTheHeardTable()
    {
        var (vm, _) = HeardVm();
        Transport.InjectLine("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        Assert.Single(vm.HeardRows);
        Session.Close();
        Assert.Empty(vm.HeardRows);
    }

}
