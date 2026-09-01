using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE pane (plan §4.4): link banner ONLY from the radio's announced
/// lines (unreported renders "—" — enum ordinal 0 is Scanning, the leak
/// class), STOP doubling as Disconnect during Calling/Linked/Sending, SCAN
/// disabled-with-reason until the radio reports a COMPLETE fill, the flat
/// station lists from verbatim R7 fixtures (round 15 §17: NETS and
/// INDIVIDUALS split out of the one flat projection, each with its associated
/// self; selfs excluded from targets; LQA action only on individuals), the
/// read-only fill strip, the lazy once-per-session station-list load (the
/// manual Refresh is DELETED, §17 G-D1), the AMD ▸ / LQA ▸
/// preselect-and-switch-in-pane row actions (GUI-rejigger N1: Messages on
/// the main tab, LQA a sub-tab; the navigation delegate is vestigial and
/// NEVER invoked), the sub-tab view-state switch (sends nothing), and the
/// programmatic-write-sends-nothing constitution pin. The app NEVER
/// auto-sends SCA or ST.
/// </summary>
public class AleViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();
    private readonly List<string> _navigations = [];
    private MessagesViewModel _messages = null!;
    private LqaViewModel _lqa = null!;

    private AleViewModel Vm()
    {
        var surface = new AleSurface(Radio);
        _messages = new MessagesViewModel(surface, Session, _time);
        _lqa = new LqaViewModel(surface, new ChannelSurface(Radio), Session);
        return new AleViewModel(surface, Session, _messages, _lqa, _navigations.Add);
    }

    /// <summary>Ready session confirmed in ALE with the lazy load already
    /// committed (verbatim R7 fill fixtures) and the sent list drained.
    ///
    /// <para>BROADCAST ROUND (plan-ale-broadcast-round.md §2, critic F3): the
    /// lazy tier reads the CHANNEL GROUPS too, so it opens TWO operations and
    /// owes TWO sentinels. Both are landed here — otherwise every later
    /// assertion about a read's own `BAT ST` would be reading a queue that
    /// still had the group read's in front of it.</para></summary>
    private AleViewModel AleReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");           // lazy load fires here
        InjectStationBook();                    // accumulates into the refresh
        AnswerSentinel();                       // commit the book
        InjectChannelGroups();                  // …and the groups the pickers read
        AnswerSentinel();                       // commit the groups
        Transport.ClearSent();
        return vm;
    }

    /// <summary>The lazy tier's ten `CHG g` reads (`SLFAD`…, then `CHG 0`…
    /// `CHG 9`) as the wire carries them — the book's three queries, its
    /// sentinel, then the whole group sweep behind it (the group read's own
    /// sentinel waits in the ping queue).</summary>
    private static string[] LazyLoadWire =>
    [
        "SLFAD", "INDAD", "NETAD", "BAT ST",
        "CHG 0", "CHG 1", "CHG 2", "CHG 3", "CHG 4",
        "CHG 5", "CHG 6", "CHG 7", "CHG 8", "CHG 9",
        // LINKED-AMD ROUND (Stage 9 closed 2026-08-24): the Inbox landing
        // read joined the tier — the default-open Inbox reads the received
        // store once per session (bare RXMSG; listing shape PROVISIONAL).
        "RXMSG",
    ];

    /// <summary>Verbatim CHG answer shape (docs/protocol.md): two groups with
    /// overlapping channels, so the union's DISTINCT + numeric sort + "00"
    /// formatting are all visible in what the pickers offer.</summary>
    private void InjectChannelGroups()
    {
        Transport.InjectLine("CHGROUP 01 CHANS 12 05 ");
        Transport.InjectLine("CHGROUP 02 CHANS 05 29 ");
    }

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

    // ---- Lazy first load (plan Q4 — the HOP pane's pattern) ------------------

    [Fact]
    public void FirstConfirmedAle_LoadsStationListOnce_NeverScaOrSt()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);      // nothing before ALE confirms

        Transport.InjectLine("ALE>");
        // The three listing queries + the commit sentinel, then the ten group
        // reads — and nothing else: the app never auto-sends SCA or ST on mode
        // entry (owner decision). BROADCAST ROUND (plan §2, critic F3): the
        // group sweep joined this tier because the CHG mirror the pinned rows'
        // pickers read is populated NOWHERE else on the operate path. Its own
        // sentinel waits in the ping queue behind the book's.
        Assert.Equal(LazyLoadWire, Transport.SentLines);

        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("ALE>");           // re-entry: no re-load
        Assert.Empty(Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void NewSession_LoadsAgain()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Assert.Equal(LazyLoadWire, Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void TheManualRefresh_IsGone_TheWritesOwnReReadFeedsBothCards()
    {
        // §17 G-D1 (owner 2026-08-22). The button answered a question nothing
        // was still asking: every app-side write closes with the bulk book
        // re-read through the programming gate, into the SAME mirror these
        // cards render from. Absence pin by reflection — a deleted command
        // cannot be referenced from a compiling test, and a binding left
        // pointing at one resolves to nothing SILENTLY in MAUI.
        Assert.Null(typeof(AleViewModel).GetProperty("RefreshStationsCommand"));

        // Anti-vacuity: the same lookup finds the commands that survived…
        Assert.NotNull(typeof(AleViewModel).GetProperty(nameof(AleViewModel.ScanCommand)));
        Assert.NotNull(typeof(AleViewModel).GetProperty(nameof(AleViewModel.StopCommand)));

        // …and the book read the deleted command wrapped still fills the cards
        // when something else asks for it.
        var vm = AleReadyVm();
        new AleSurface(Radio).RefreshStationList();
        InjectStationBook();
        AnswerSentinel();
        Assert.Equal(2, vm.StationRows.Count);
        Assert.Single(vm.NetRows);
    }

    // ---- Banner: only from announced lines; "—" until reported ---------------

    [Fact]
    public void Banner_UnreportedRendersDash_NotScanning()
    {
        // Enum ordinal 0 is Scanning — a default leak would render SCANNING
        // on a radio that never said so.
        var vm = AleReadyVm();
        Assert.False(vm.IsBannerConfirmed);
        Assert.Equal("—", vm.BannerText);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void Banner_FollowsTheAnnouncedLifecycle_R7Shapes()
    {
        var vm = AleReadyVm();

        Transport.InjectLine("SCANNING");
        Assert.Equal("Scanning", vm.BannerText);
        Assert.True(vm.IsScanning);

        Transport.InjectLine("SCAN STOPPED");
        Assert.Equal("Scan stopped", vm.BannerText);

        // The inbound handshake (field capture 2026-08-24) — COMPOSED prose,
        // never the wire token verbatim (owner ruling the same day).
        Transport.InjectLine(" SIGNAL RECEIVED ");
        Assert.Equal("Signal received", vm.BannerText);
        Assert.True(vm.IsIncomingCall);
        Transport.InjectLine("RECEIVING CALL  ");
        Assert.Equal("Receiving a call", vm.BannerText);
        Assert.True(vm.IsIncomingCall);
        Transport.InjectLine("LINKED KC1HAS1           CHANNEL: 29");
        Assert.Equal("Linked to KC1HAS1 — CH 29", vm.BannerText);

        // Verbatim R7 call announcement, incl. the channel change.
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Assert.Equal("Calling AAA — CH 01", vm.BannerText);
        Assert.True(vm.IsCalling);

        Transport.InjectLine("SCAN STOPPED");
        Transport.InjectLine("SENDING  AAA              CHANNEL: 00");
        Assert.Equal("Sending to AAA — CH 00", vm.BannerText);
        Assert.True(vm.IsSending);

        Transport.InjectLine("LINKED AAA");
        // The link's channel shows in the SAME "— CH nn" form as the other
        // messages (owner 2026-08-24): the LINKED line carried none, so the
        // slot still holds the send's own channel — the link's either way.
        Assert.Equal("Linked to AAA — CH 00", vm.BannerText);
        Assert.True(vm.IsLinked);

        Assert.Empty(Transport.SentLines);      // display only — nothing sent
    }

    // ---- The LQA lifecycle on the banner (round 15 item I; probes P14b/P14c) --

    [Fact]
    public void Banner_FollowsTheLqaLifecycle_P14Shapes()
    {
        var vm = AleReadyVm();

        // Verbatim P14c: the bare `SOU STA W6HOS` answer, then the walk down
        // the group. The banner MOVES with the channel even though the link
        // state does not — the run is one state and many channels.
        Transport.InjectLine("SOUNDING W6HOS            CHANNEL: 30");
        Assert.Equal("Sounding as W6HOS — CH 30", vm.BannerText);
        Assert.True(vm.IsLqa);
        Assert.False(vm.IsScanning);
        Assert.False(vm.IsCalling);

        Transport.InjectLine("SOUNDING W6HOS            CHANNEL: 28");
        Assert.Equal("Sounding as W6HOS — CH 28", vm.BannerText);

        // A mid-run `SH` — the whole block, verbatim from P14c's own step
        // record (JSONL line 39), which is what an operator's Console read or
        // any pane's status read puts on the wire during a run. MANAGER RULING
        // 2026-08-23 (the phase-5 wire leg measured the defect): the block's
        // kind-unknown `LQA/SOUND` first line must NOT downgrade the banner
        // the radio's own progress line earned.
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AleViewModel.BannerText)) seen.Add(vm.BannerText);
        };
        foreach (var line in ShBlockDuringAnLqa) Transport.InjectLine(line);

        Assert.Equal("Sounding as W6HOS — CH 28", vm.BannerText);
        Assert.True(vm.IsLqa);
        Assert.DoesNotContain("LQA IN PROGRESS", seen);      // not even for an instant
        Assert.Equal(26, Radio.State.OperatingChannel.Value); // the block WAS read

        // Verbatim P14b: an exchange names the INDIVIDUAL it is exchanging with.
        Transport.InjectLine("EXCHANGE KC1HAS           CHANNEL: 30");
        Assert.Equal("LQA exchange with KC1HAS — CH 30", vm.BannerText);
        Assert.True(vm.IsLqa);

        // The terminator (P14c: the run ends and scan resumes).
        Transport.InjectLine("SCANNING");
        Assert.Equal("Scanning", vm.BannerText);
        Assert.False(vm.IsLqa);
        Assert.True(vm.IsScanning);

        Assert.Empty(Transport.SentLines);      // display only — nothing sent
    }

    [Fact]
    public void AnShTakenBeforeAnyProgressLine_ReadsTheKindUnknownProse()
    {
        // The other half of the ruling: when `LQA/SOUND` is ALL the app has
        // heard — a queued schedule fired while the operator was elsewhere and
        // the first status read is what finds it — the banner says exactly
        // that, in prose, because the radio named no kind (I-5).
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");
        foreach (var line in ShBlockDuringAnLqa) Transport.InjectLine(line);

        Assert.Equal("LQA in progress", vm.BannerText);
        Assert.True(vm.IsLqa);
        Assert.False(vm.CanScan);
        Assert.Equal(AleViewModel.LqaInProgressReason, vm.ScanDisabledReason);

        // …and the first progress line REPLACES the prose with the kind.
        Transport.InjectLine("SOUNDING W6HOS            CHANNEL: 30");
        Assert.Equal("Sounding as W6HOS — CH 30", vm.BannerText);
    }

    /// <summary>The ALE `SH` block a running LQA answers, VERBATIM from
    /// <c>bench/transcripts/p14c-sounding-clean-20260822-132151.jsonl</c> (the
    /// `SH` step record at JSONL line 39): `LQA/SOUND` stands in the seat
    /// `SCANNING` holds otherwise, and `CHAN 26` is the channel the run had
    /// reached.</summary>
    private static readonly string[] ShBlockDuringAnLqa =
    [
        "LQA/SOUND",
        "LSTN        ON  ", "KEY_TO_CALL ON  ", "RAD_SIL     OFF ",
        "ALL_CALL    ON  ", "ANY_CALL    ON  ",
        "MAXCH 020", "TUNETIME 010", "TIME_OUT 006", "AMD_DISPLAY ON  ",
        "CHAN 26 ", "MODE USB", "RxFr 21432500", "TxFr 21432500",
        "MODEM OFF", "DV OFF", "DGT_SQUELCH OFF", "AVS OFF", "ENCRYPT OFF",
        "RWAS DISABLED", "ALE> ",
    ];

    [Fact]
    public void AnLqaThatEnds_LeavesNoStaleSelfOnALaterLink()
    {
        // Critic F73 at the banner: the sounding's "station" is this radio's
        // OWN self. If it had gone into the call slot the next bare LINKED
        // would read "LINKED W6HOS" — a link to ourselves.
        var vm = AleReadyVm();
        Transport.InjectLine("SOUNDING W6HOS            CHANNEL: 30");
        Transport.InjectLine("SCANNING");
        Transport.InjectLine("LINKED");

        Assert.Equal("Linked", vm.BannerText);
        Assert.True(vm.IsLinked);
    }

    [Theory]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30")]
    [InlineData("LQA/SOUND")]
    public void DuringAnLqa_ScanCallAndAmdAreWithheld_WithTheLqaReason(string announcement)
    {
        // §19 I-2 / F69: the ONE on-air term. An LQA is a minutes-long
        // TRANSMISSION (P14c), so everything that would key the radio greys —
        // and the Scan reason names what ends it rather than talking about a
        // call the operator is not in.
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");                  // fill Complete, Scan live
        Transport.InjectLine("SCAN STOPPED");
        Assert.True(vm.CanScan);
        Assert.True(vm.StationRows[0].CanCall);
        Assert.True(vm.StationRows[0].CanAmd);

        Transport.InjectLine(announcement);
        Transport.ClearSent();

        Assert.False(vm.CanScan);
        Assert.Equal(AleViewModel.LqaInProgressReason, vm.ScanDisabledReason);
        Assert.False(vm.StationRows[0].CanCall);
        Assert.False(vm.StationRows[0].CanAmd);
        Assert.False(vm.NetRows[0].CanCall);

        // The bodies are dead too — Execute never consults CanExecute.
        vm.ScanCommand.Execute(null);
        vm.StationRows[0].CallCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // STOP is what aborts an LQA (ST, P14b), and it says STOP — the radio
        // is not in a call, so "DISCONNECT" would name the wrong gesture.
        Assert.Equal("STOP", vm.StopButtonText);
        Assert.True(vm.CanStop);

        // …and it all lifts when the radio says the run ended.
        Transport.InjectLine("SCAN STOPPED");
        Assert.True(vm.CanScan);
        Assert.Equal("", vm.ScanDisabledReason);
        Assert.True(vm.StationRows[0].CanCall);
        Assert.True(vm.StationRows[0].CanAmd);
    }

    [Fact]
    public void ALink_IsOnAirToo_TheWidenedTerm()
    {
        // The on-air sweep WIDENED this pane's term: Calling|Sending was its
        // private list, and a held LINK is on air by the same reasoning.
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");
        Transport.InjectLine("SCAN STOPPED");
        Assert.True(vm.CanScan);

        Transport.InjectLine("LINKED AAA");
        Assert.False(vm.CanScan);
        Assert.False(vm.IsLqa);
        Assert.Equal(AleViewModel.OnAirDisabledReason, vm.ScanDisabledReason);   // not the LQA one
        Assert.False(vm.StationRows[0].CanCall);
        // THE CARVE-OUT (owner 2026-08-24, linked-amd round; manual
        // §2.5.2.7(g)): an established link ACCEPTS an AMD, so the AMD door
        // stays open while CALL stays gated.
        Assert.True(vm.StationRows[0].CanAmd);
    }

    // ---- STOP doubles as Disconnect (ST also terminates calls) ----------------

    [Fact]
    public void StopButton_SaysDisconnect_DuringCallingLinkedSending()
    {
        var vm = AleReadyVm();
        Assert.Equal("STOP", vm.StopButtonText);

        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Assert.Equal("DISCONNECT", vm.StopButtonText);

        Transport.InjectLine("LINKED AAA");
        Assert.Equal("DISCONNECT", vm.StopButtonText);

        Transport.InjectLine("SCAN STOPPED");
        Assert.Equal("STOP", vm.StopButtonText);
    }

    [Fact]
    public void Stop_SendsSt_IncludingDuringACall()
    {
        // B10 is open (no call-failure line within 25 s) — the Calling state
        // clears only via ST/LINKED, so Disconnect MUST be offered while
        // Calling.
        var vm = AleReadyVm();
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Transport.ClearSent();

        Assert.True(vm.CanStop);
        vm.StopCommand.Execute(null);
        Assert.Equal(["ST"], Transport.SentLines);

        // Post-gesture pin (audit round 1, F1): ST is on the wire but the
        // radio has NOT answered — Calling must still display (the banner
        // clears only via the radio's SCAN STOPPED / LINKED lines).
        Assert.Equal("Calling AAA — CH 01", vm.BannerText);
        Assert.True(vm.IsCalling);
        Assert.Equal("DISCONNECT", vm.StopButtonText);

        Transport.InjectLine("SCAN STOPPED");            // the radio's answer moves it
        Assert.Equal("Scan stopped", vm.BannerText);
        Assert.False(vm.IsCalling);
    }

    [Fact]
    public void Stop_OutsideAle_Disabled()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.False(vm.CanStop);
        vm.StopCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- SCAN: disabled-with-reason until the radio reports a complete fill ----

    [Fact]
    public void Scan_UnreportedFill_DisabledWithReason()
    {
        var vm = AleReadyVm();
        Assert.False(vm.CanScan);
        Assert.Contains("fill state", vm.ScanDisabledReason);

        vm.ScanCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("PRG 1-3 CHAR SLF", "self address")]
    [InlineData("IND NOT PROGRMD", "individual")]
    [InlineData("NO CHANS TO SCAN", "scan channels")]
    public void Scan_IncompleteFillGateLines_DisabledWithTheSpecificReason(string gateLine, string expected)
    {
        var vm = AleReadyVm();
        Transport.InjectLine(gateLine);
        Assert.False(vm.CanScan);
        Assert.Contains(expected, vm.ScanDisabledReason);
        Assert.Contains(expected, vm.FillStateText);

        vm.ScanCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Scan_CompleteFillAndStopped_SendsSca()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");       // fill Complete inferred (R7)
        Transport.InjectLine("SCAN STOPPED");
        Transport.ClearSent();

        Assert.True(vm.IsFillComplete);
        Assert.Equal("✓ Complete", vm.FillStateText);
        Assert.True(vm.CanScan);
        vm.ScanCommand.Execute(null);
        Assert.Equal(["SCA"], Transport.SentLines);

        // Post-gesture pin (audit round 1, F1): SCA is on the wire but the
        // radio has NOT answered — the banner must not have moved.
        Assert.Equal("Scan stopped", vm.BannerText);
        Assert.False(vm.IsScanning);

        Transport.InjectLine("SCANNING");                // the radio's answer moves it
        Assert.Equal("Scanning", vm.BannerText);
        Assert.True(vm.IsScanning);
    }

    [Fact]
    public void Scan_WhileAlreadyScanning_ReclickGuarded()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");
        Transport.ClearSent();

        Assert.False(vm.CanScan);
        Assert.Contains("Already scanning", vm.ScanDisabledReason);
        vm.ScanCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Station list: flat, selfs excluded, LQA individuals-only --------------

    [Fact]
    public void TheRowsSplitIntoNetsAndStations_EachInTheRadiosListingOrder()
    {
        // §17: ONE mirror, TWO projections. The split is what lets each card
        // head itself — which is what let the Type column go.
        var vm = AleReadyVm();

        Assert.Equal(["AAA", "BBB"], vm.StationRows.Select(r => r.Address));
        Assert.Equal(["IND", "IND"], vm.StationRows.Select(r => r.KindText));
        Assert.Equal("01", vm.StationRows[0].GroupText);

        Assert.Equal(["NT1"], vm.NetRows.Select(r => r.Address));
        Assert.Equal("NET", vm.NetRows[0].KindText);

        // Neither list holds the other's kind — the failure a "rows appear
        // twice" or "nets fell through" regression would show as.
        Assert.DoesNotContain(vm.StationRows, r => r.IsNet);
        Assert.All(vm.NetRows, r => Assert.True(r.IsNet));

        // Selfs are NOT call targets — they live in the selfs table.
        Assert.DoesNotContain(vm.StationRows.Concat(vm.NetRows),
            r => r.Address is "TST" or "ZZZ");

        // LQA (RAN) is individuals-only — the radio's own restriction, and it
        // survives the split unchanged.
        Assert.True(vm.StationRows[0].CanLqa);
        Assert.False(vm.NetRows[0].CanLqa);
        Assert.True(vm.NetRows[0].CanAmd);
        Assert.True(vm.NetRows[0].CanCall);

        Assert.Empty(Transport.SentLines);      // rendering sent nothing
    }

    [Fact]
    public void EveryRow_CarriesItsAssociatedSelf_AndDashesWhenTheRadioReportsNone()
    {
        // §17's new column. The BLANK case is real, not defensive: deleting a
        // PRIMARY self blanks its nets' associated self (docs/protocol.md, the
        // primary-deletion artifact), and the third state is DISPLAYED rather
        // than defaulted away (invariant I-1).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT2               CHGROUP 01");      // orphaned net
        AnswerSentinel();

        Assert.Equal("TST", vm.StationRows[0].AssociatedSelfText);
        Assert.Equal(["TST", "—"], vm.NetRows.Select(r => r.AssociatedSelfText));
    }

    [Fact]
    public void StationGroupText_IsTheZeroPaddedChanGrp_OnBothKinds()
    {
        // The station table's half of ruling 1, on a fixture that can tell a
        // padded cell from an unpadded one: a group-0 individual and a
        // group-0 net render "00", never "0" and never "grp 0". Padding is
        // what keeps the fixed 64-dp Chan-grp column reading as a column.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("INDAD CCC               CHGROUP 00   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT0               CHGROUP 00   ASSOC SELF TST");
        AnswerSentinel();

        Assert.Equal("00", Assert.Single(vm.StationRows).GroupText);   // IND CCC
        Assert.Equal("00", Assert.Single(vm.NetRows).GroupText);       // NET NT0
        Assert.Equal("NET", vm.NetRows[0].KindText);          // KindText unchanged by §17
    }

    [Fact]
    public void CallRow_SendsCal_BannerMovesOnlyOnTheRadiosAnswer()
    {
        var vm = AleReadyVm();
        vm.StationRows[0].CallCommand.Execute(null);
        Assert.Equal(["CAL AAA"], Transport.SentLines);

        // Post-gesture pin (audit round 1, F1): CAL is on the wire but the
        // radio has NOT answered — no optimistic Calling banner (the live
        // radio SILENTLY IGNORES CAL in the never-scanned state, so an
        // optimistic banner would display a call that never existed).
        Assert.False(vm.IsBannerConfirmed);
        Assert.Equal("—", vm.BannerText);
        Assert.False(vm.IsCalling);
        Assert.Equal("STOP", vm.StopButtonText);

        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Assert.Equal("Calling AAA — CH 01", vm.BannerText);
        Assert.True(vm.IsCalling);
    }

    [Fact]
    public void CallRow_DuringACall_Guarded()
    {
        // CAL during an in-flight call/send is unprobed wire behavior — the
        // UI does not offer it; ST (Disconnect) is the way out.
        var vm = AleReadyVm();
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Transport.ClearSent();

        Assert.False(vm.StationRows[1].CanCall);
        vm.StationRows[1].CallCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void CallRow_OutsideAle_NothingSent()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.StationRows[0].CallCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- AMD ▸ / LQA ▸ row actions: preselect + switch in-pane, sends nothing ----

    [Fact]
    public void AmdRow_PreselectsTargetOnMessages_ShowsMainTabCompose_SendsNothing()
    {
        var vm = AleReadyVm();
        vm.OpenLqaTabCommand.Execute(null);              // start on the LQA sub-tab
        Transport.ClearSent();                           // (its landing read — see below)
        _messages.OpenInboxCommand.Execute(null);        // and with Inbox open
        vm.NetRows[0].AmdCommand.Execute(null);          // NT1 (net — AMD is legal)

        Assert.Equal("NT1", _messages.SelectedTarget?.Address);
        Assert.False(vm.IsLqaTabOpen);                   // Messages is on the main tab
        Assert.False(_messages.IsInboxOpen);             // Compose area shown
        Assert.Empty(_navigations);                      // delegate is vestigial — never invoked
        Assert.Empty(Transport.SentLines);               // view state only
    }

    [Fact]
    public void LqaRow_PreselectsIndividualOnLqa_OpensLqaSubTab_AndLands()
    {
        // Round 11 §4: the row action LANDS on the LQA sub-tab, so it carries
        // the landing's one read — the same bare EXCH the strip's own button
        // fires. The PRESELECT is still pure view state.
        var vm = AleReadyVm();
        vm.StationRows[1].LqaCommand.Execute(null);      // BBB (individual)

        Assert.Equal("BBB", _lqa.SelectedRankStation?.Address);
        Assert.True(vm.IsLqaTabOpen);                    // switched to the LQA sub-tab
        Assert.Empty(_navigations);                      // delegate is vestigial — never invoked
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void LqaRow_OnANet_NoOp()
    {
        var vm = AleReadyVm();
        vm.NetRows[0].LqaCommand.Execute(null);          // NT1 (net)

        Assert.Null(_lqa.SelectedRankStation);
        Assert.False(vm.IsLqaTabOpen);                   // tab did not switch
        Assert.Empty(_navigations);
        Assert.Empty(Transport.SentLines);
    }

    // ---- LQA sub-tab: view state + the ONE landing read (round 11 §4) -----------

    [Fact]
    public void SubTab_DefaultsToMainTab()
    {
        var vm = AleReadyVm();
        Assert.False(vm.IsLqaTabOpen);
    }

    [Fact]
    public void SubTab_SwitchesBothWays_AndOnlyTheLqaLandingReads()
    {
        // The switch is still view state; what changed in round 11 §4 is that
        // LANDING on LQA carries that tab's read (bare EXCH + its sentinel).
        // Returning to the main tab carries none — round-9 doctrine: a read
        // belongs to the tab the operator is on.
        var vm = AleReadyVm();

        vm.OpenLqaTabCommand.Execute(null);
        Assert.True(vm.IsLqaTabOpen);
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        Transport.ClearSent();

        vm.OpenMainTabCommand.Execute(null);
        Assert.False(vm.IsLqaTabOpen);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void EveryLqaLanding_ReReads_ItIsNotLazyOnce()
    {
        // The editors-read-fresh tier: the schedule queue is cheap and the
        // operator is looking at it, so EVERY arrival re-asks.
        var vm = AleReadyVm();

        vm.OpenLqaTabCommand.Execute(null);
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        AnswerSentinel();                                // land it
        Transport.ClearSent();

        vm.OpenMainTabCommand.Execute(null);
        vm.OpenLqaTabCommand.Execute(null);
        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void LqaLanding_OutsideAle_ReadsNothing()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.OpenLqaTabCommand.Execute(null);
        Assert.True(vm.IsLqaTabOpen);                    // the view state still moves
        Assert.Empty(Transport.SentLines);               // but nothing is read
    }

    [Fact]
    public void AleConfirming_WhileTheLqaTabIsAlreadyOpen_CountsAsALanding()
    {
        // AUDIT ROUND 1, MAJOR-3. The operator opens LQA before the radio has
        // confirmed ALE: that landing is refused, and nothing retries while
        // they stand there — so §4's placeholder would present an UNREAD radio
        // queue as read-empty for the rest of the session. Confirmation IS the
        // landing they never got.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        vm.OpenLqaTabCommand.Execute(null);              // landing refused: no ALE
        Transport.ClearSent();
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("ALE>");                    // …ALE confirms

        Assert.Contains("EXCH", Transport.SentLines);    // the recovery read
    }

    [Fact]
    public void AleConfirming_WithTheMainTabCurrent_ReadsNothingForLqa()
    {
        // The other side: the recovery is a LANDING, not a session hook. If the
        // LQA tab is not the current one, confirmation reads nothing for it —
        // no read fires from a tab the operator is not on.
        var vm = Vm();
        ConnectReady();
        Transport.ClearSent();

        Transport.InjectLine("ALE>");                    // main tab is current

        Assert.False(vm.IsLqaTabOpen);
        Assert.DoesNotContain("EXCH", Transport.SentLines);
    }

    [Fact]
    public void TheRecoveryLanding_FiresOnTheTransitionOnly_NotOnEveryRefresh()
    {
        // It rides the CONFIRMATION EDGE. Refreshes that follow while ALE stays
        // confirmed must not each re-read: the every-landing tier belongs to
        // arrivals, and this is not one.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        vm.OpenLqaTabCommand.Execute(null);
        Transport.ClearSent();

        Transport.InjectLine("ALE>");
        int reads = Transport.CountSent("EXCH");
        Assert.Equal(1, reads);

        AnswerSentinel();                                // land it, then churn
        Transport.InjectLine("SCANNING");
        Transport.InjectLine("SCAN STOPPED");
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");

        Assert.Equal(1, Transport.CountSent("EXCH"));
    }

    // ---- Selfs table (round 10 §4) — read-only display ---------------------------

    [Fact]
    public void SelfRows_AreATable_WithTheChanGrpVocabulary()
    {
        // §4 replaced the one-line "Self: ZZZ (grp 0), TST (grp 1)" summary
        // with a Self | Chan grp table. Both halves of the owner's ruling-1
        // vocabulary are pinned on the SAME fixture: no "grp" word, no
        // parentheses, and a ZERO-PADDED two-digit group — the group-0
        // bootstrap self is what makes the padding visible ("00", not "0").
        var vm = AleReadyVm();

        Assert.Equal(2, vm.SelfRows.Count);
        Assert.Equal("ZZZ", vm.SelfRows[0].Address);
        Assert.Equal("00", vm.SelfRows[0].GroupText);
        Assert.Equal("TST", vm.SelfRows[1].Address);
        Assert.Equal("01", vm.SelfRows[1].GroupText);

        Assert.DoesNotContain(vm.SelfRows, r => r.GroupText.Contains("grp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vm.SelfRows, r => r.GroupText.Contains('('));

        Assert.Empty(Transport.SentLines);      // rendering sent nothing
    }

    [Fact]
    public void SelfSummaryText_IsGone_TheTableReplacedIt()
    {
        // Absence pin (invariant 5): the deleted property must not come back
        // beside the table it was replaced BY — two renderings of the same
        // fact is exactly what §4 removed. Reflection, because a deleted
        // member cannot be referenced from a compiling test.
        Assert.Null(typeof(AleViewModel).GetProperty("SelfSummaryText"));

        // Anti-vacuity for the reflection: the same lookup finds what IS there.
        Assert.NotNull(typeof(AleViewModel).GetProperty(nameof(AleViewModel.SelfRows)));
        Assert.NotNull(typeof(AleViewModel).GetProperty(nameof(AleViewModel.FillStateText)));
    }

    [Fact]
    public void SelfsTable_UnreportedIsEmpty_AndFillStateDashes()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();                       // commit the (empty) lazy load

        // Empty, NOT a placeholder row: the view owns the "No self addresses
        // reported yet." line, so the VM never invents a row the radio did
        // not report.
        Assert.Empty(vm.SelfRows);
        Assert.Equal("—", vm.FillStateText);
        Assert.False(vm.IsFillComplete);
    }

    // =========================================================================
    // BROADCAST ROUND F3/F5 — the pinned ANY/ALL rows and the Stop verb branch
    // (plan-ale-broadcast-round.md §2/§3; probes P20/P20b)
    // =========================================================================

    // ---- The pickers' contents: the ONE union, mirror-honest ----------------

    [Fact]
    public void TheChannelPickers_OfferOnlyTheRadioReportedChannels_AutoFirstOnALL()
    {
        // Owner ruling 4: ONLY channels present in the reported CHG groups —
        // never the raw 0-99 range, which would be inventing a fact the radio
        // never reported. The fixture's two groups OVERLAP on 05 and arrive out
        // of numeric order, so distinct + sort + "00" are all visible.
        var vm = AleReadyVm();

        Assert.Equal(["05", "12", "29"], vm.AnyChannelChoices);
        Assert.Equal(["Auto", "05", "12", "29"], vm.AllChannelChoices);

        // Owner ruling 3's defaults: ANY starts UNPICKED (there is no honest
        // default — the radio reports no preference), ALL starts on Auto.
        Assert.Null(vm.SelectedAnyChannel);
        Assert.Equal("Auto", vm.SelectedAllChannel);
        Assert.Empty(Transport.SentLines);        // rendering sent nothing
    }

    [Fact]
    public void AnUnreadGroupMirror_LeavesTheAnyPickerEmpty_AndItsCallWithheld()
    {
        // Mirror-honest: no CHG answer, no choices — and CALL ANY stays
        // withheld, which is the only correct offer (the radio refuses a
        // channel-less ANY with ` NO CHANS IN GRP `, P20).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();                          // book commits, groups unanswered
        AnswerSentinel();                          // the group read closes EMPTY
        Transport.ClearSent();

        Assert.Empty(vm.AnyChannelChoices);
        Assert.Equal(["Auto"], vm.AllChannelChoices);
        Assert.False(vm.CallAnyCommand.CanExecute(null));
        vm.CallAnyCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // ALL is still callable: its bare form takes no channel (P20).
        Assert.True(vm.CallAllCommand.CanExecute(null));
    }

    // ---- CALL ANY / CALL ALL: the exact wire text ---------------------------

    [Fact]
    public void CallAny_WithAChannel_SendsCalAnyNn_BannerMovesOnlyOnTheRadiosAnswer()
    {
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";

        Assert.True(vm.CallAnyCommand.CanExecute(null));
        vm.CallAnyCommand.Execute(null);
        Assert.Equal(["CAL ANY 12"], Transport.SentLines);   // P20b's captured form

        // Post-gesture pin: the command is on the wire, the radio has not
        // answered — no optimistic banner.
        Assert.False(vm.IsBannerConfirmed);
        Assert.Equal("—", vm.BannerText);

        Transport.InjectLine("CALLING  ANY              CHANNEL: 12");
        Assert.Equal("Calling ANY — CH 12", vm.BannerText);
        Assert.True(vm.IsCalling);
    }

    [Fact]
    public void CallAny_WithNoChannelPicked_DisabledAndTheBodyIsDeadToo()
    {
        // The wire REFUSES a bare `CAL ANY` (` NO CHANS IN GRP `, twice, no TX
        // — P20), so the app does not offer it. Both halves: the guard says no,
        // and the body repeats it (Execute never consults CanExecute).
        var vm = AleReadyVm();
        Assert.Null(vm.SelectedAnyChannel);

        Assert.False(vm.CallAnyCommand.CanExecute(null));
        vm.CallAnyCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // …and picking one lifts it.
        vm.SelectedAnyChannel = "05";
        Assert.True(vm.CallAnyCommand.CanExecute(null));
    }

    [Fact]
    public void CallAll_OnAuto_SendsTheBareCalAll_AndOnAPick_SendsTheChannelForm()
    {
        var vm = AleReadyVm();

        vm.CallAllCommand.Execute(null);
        Assert.Equal(["CAL ALL"], Transport.SentLines);      // P20: the radio picks
        Transport.ClearSent();

        vm.SelectedAllChannel = "05";
        vm.CallAllCommand.Execute(null);
        Assert.Equal(["CAL ALL 05"], Transport.SentLines);   // P20b's twin form
    }

    [Theory]
    [InlineData("CALLING  AAA              CHANNEL: 01")]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    public void TheBroadcastActions_AreWithheldOnAir_LikeEveryOtherRowAction(string announcement)
    {
        // Invariant: CAL during any on-air state is unprobed wire behaviour, and
        // the pinned rows are not an exception to the term the station rows use.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        Assert.True(vm.CallAnyCommand.CanExecute(null));

        Transport.InjectLine(announcement);
        Transport.ClearSent();

        Assert.False(vm.CallAnyCommand.CanExecute(null));
        Assert.False(vm.CallAllCommand.CanExecute(null));
        Assert.False(vm.AmdAnyCommand.CanExecute(null));
        Assert.False(vm.AmdAllCommand.CanExecute(null));

        vm.CallAnyCommand.Execute(null);
        vm.CallAllCommand.Execute(null);
        vm.AmdAnyCommand.Execute(null);
        vm.AmdAllCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Null(_messages.SelectedTarget);              // AMD did not preselect either
    }

    [Fact]
    public void WhileLINKED_TheBroadcastAmdDoorOpens_AndCallStaysGated()
    {
        // The carve-out (owner 2026-08-24, linked-amd round; manual
        // §2.5.2.7(g): an AMD "may be sent when the R/T is either linked or
        // scanning"). CALL keeps the full on-air gate — a CAL from inside a
        // link is unprobed wire behaviour; SCA (Disconnect) is the way out.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        Transport.InjectLine("LINKED AAA");
        Transport.ClearSent();

        Assert.False(vm.CallAnyCommand.CanExecute(null));
        Assert.False(vm.CallAllCommand.CanExecute(null));
        Assert.True(vm.AmdAnyCommand.CanExecute(null));
        Assert.True(vm.AmdAllCommand.CanExecute(null));

        vm.AmdAnyCommand.Execute(null);
        Assert.Empty(Transport.SentLines);                  // the door only NAVIGATES
        Assert.Equal("ANY", _messages.SelectedTarget?.Address);
    }

    [Fact]
    public void TheBroadcastActions_OutsideAle_AreWithheld()
    {
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.False(vm.CallAnyCommand.CanExecute(null));
        Assert.False(vm.CallAllCommand.CanExecute(null));
        vm.CallAnyCommand.Execute(null);
        vm.CallAllCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- AMD ▸ on the pinned rows: preselect + switch, sends nothing --------

    [Fact]
    public void AmdAny_PreselectsAnyWithItsChannel_ShowsCompose_SendsNothing()
    {
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.OpenLqaTabCommand.Execute(null);              // start on the LQA sub-tab
        Transport.ClearSent();                           // (its landing read)
        _messages.OpenInboxCommand.Execute(null);

        vm.AmdAnyCommand.Execute(null);

        Assert.Equal("ANY", _messages.SelectedTarget?.Address);
        Assert.Equal("12", _messages.SelectedComposeChannel);
        Assert.True(_messages.IsChannelPickerVisible);
        Assert.False(vm.IsLqaTabOpen);                   // Messages is on the main tab
        Assert.False(_messages.IsInboxOpen);             // Compose shown
        Assert.Empty(_navigations);
        Assert.Empty(Transport.SentLines);               // view state only
    }

    [Fact]
    public void AmdAny_WithNoChannelPicked_StillOpensCompose_TheComposePickerGatesTheSend()
    {
        // Plan §2, explicitly: an unpicked ANY still opens compose. Refusing to
        // open it would hide the very control that fixes the problem.
        var vm = AleReadyVm();
        vm.AmdAnyCommand.Execute(null);

        Assert.Equal("ANY", _messages.SelectedTarget?.Address);
        Assert.Null(_messages.SelectedComposeChannel);
        Assert.False(_messages.IsInboxOpen);
        Assert.False(_messages.CanSend);
        Assert.Equal(MessagesViewModel.AnyNeedsChannelReason, _messages.SendDisabledReason);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AmdAll_CarriesAutoAsNoChannel_AndAPickAsThePick()
    {
        var vm = AleReadyVm();

        vm.AmdAllCommand.Execute(null);
        Assert.Equal("ALL", _messages.SelectedTarget?.Address);
        Assert.Equal("Auto", _messages.SelectedComposeChannel);   // Auto -> the compose default

        vm.SelectedAllChannel = "29";
        vm.AmdAllCommand.Execute(null);
        Assert.Equal("29", _messages.SelectedComposeChannel);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Selection lifetime (plan §3) ---------------------------------------

    [Fact]
    public void ABlankGroupRebuild_KEEPSTheSelection_ItIsNotEvidenceTheChannelIsGone()
    {
        // Plan §3, verbatim: the picks are app-side INPUT state. A reconnect
        // BLANKS the group mirror, and "the radio has not told us yet" is not
        // "the channel is gone" — pruning there would silently disarm the row
        // the operator had set up.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        Session.Close();
        ConnectReady();
        Transport.InjectLine("ALE>");                    // fresh session: mirror blank
        AnswerSentinel();
        AnswerSentinel();

        Assert.Empty(vm.AnyChannelChoices);              // the ItemsSource IS empty…
        Assert.Equal("12", vm.SelectedAnyChannel);       // …and the pick survives it
        Assert.Equal("29", vm.SelectedAllChannel);
    }

    [Fact]
    public void AConfirmedNonBlankGroupReadThatLacksThePick_PRUNESIt()
    {
        // The other side of the same rule: the radio HAS reported its groups and
        // the picked channel is not among them, so the pick is stale and goes —
        // ANY to null (nothing to fall back on), ALL to Auto (its own default).
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 05 ");     // 12 and 29 are gone
        AnswerSentinel();

        Assert.Equal(["05"], vm.AnyChannelChoices);
        Assert.Null(vm.SelectedAnyChannel);
        Assert.Equal("Auto", vm.SelectedAllChannel);
    }

    [Fact]
    public void APARTIALGroupTable_KEEPSTheSelection_EvenWhenItsUnionLacksIt()
    {
        // AUDIT ROUND 1, MAJOR 1 — the defect, reproduced. A single-group read
        // after a reconnect leaves a union that is NON-EMPTY and yet lacks the
        // pick, because nine groups have not been read at all. The old
        // predicate treated "union non-empty" as "mirror confirmed" and erased
        // the operator's channel; the rule (plan §3) is the WHOLE table.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        Session.Close();
        ConnectReady();                                  // the mirror blanks
        var surface = new AleSurface(Radio);
        surface.RequestChannelGroup(0);                  // ONE group answers…
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.Equal(["05"], vm.AnyChannelChoices);      // non-empty union…
        Assert.False(surface.GroupTableFullyRead);       // …partial table
        Assert.Equal("12", vm.SelectedAnyChannel);       // …so the picks STAND
        Assert.Equal("29", vm.SelectedAllChannel);

        // …and the WHOLE-table read that follows is what finally prunes them.
        surface.RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 00 CHANS 05 ");
        AnswerSentinel();

        Assert.True(surface.GroupTableFullyRead);
        Assert.Null(vm.SelectedAnyChannel);
        Assert.Equal("Auto", vm.SelectedAllChannel);
    }

    [Fact]
    public void AConfirmedGroupReadThatSTILLLISTSThePick_LeavesItAlone()
    {
        // Anti-vacuity for the prune: a rebuild that still contains the channel
        // must NOT reset the picker under the operator.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 12 29 ");
        AnswerSentinel();

        Assert.Equal("12", vm.SelectedAnyChannel);
        Assert.Equal("29", vm.SelectedAllChannel);
    }

    // ---- The Picker null-write refusal (audit round 1, MAJOR 2) -------------

    [Fact]
    public void ABindingOriginatedNullWrite_DoesNOTClobberAStoredSelection()
    {
        // A real MAUI Picker CLEARS its SelectedItem when its ItemsSource is
        // rebuilt blank or shorter, and the TwoWay binding writes that null
        // straight in — bypassing every prune guard, on exactly the reconnect
        // this app does routinely. A person cannot UNSELECT from a Picker, so
        // an incoming null is never an operator gesture and is REFUSED. The VM
        // refresh tests cannot see this: they have no live Picker, which is
        // why they were green over the defect.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        vm.SelectedAnyChannel = null;                    // the Picker's clear
        vm.SelectedAllChannel = null!;

        Assert.Equal("12", vm.SelectedAnyChannel);
        Assert.Equal("29", vm.SelectedAllChannel);
        Assert.True(vm.CallAnyCommand.CanExecute(null)); // …and the guard is unmoved
    }

    [Fact]
    public void TheAPPSIDEPaths_STILLClearTheSelection_TheRefusalIsBindingOnly()
    {
        // The other side, and the one a blanket "never accept null" would
        // break: the PRUNE must still do its documented transition (ANY → null,
        // ALL → "Auto"). It writes through the private path deliberately.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 05 ");
        AnswerSentinel();

        Assert.Null(vm.SelectedAnyChannel);
        Assert.Equal("Auto", vm.SelectedAllChannel);
    }

    [Fact]
    public void AChoiceListRebuild_ReAnnouncesTheSELECTION_SoALivePickerReAdoptsIt()
    {
        // The refusal alone is not enough: a Picker that dropped its own
        // SelectedItem on a blank ItemsSource has to be TOLD to re-adopt the
        // value the VM kept, and it only can once its items are back. So every
        // real rebuild of the choice list re-raises the selection property.
        var vm = AleReadyVm();
        vm.SelectedAnyChannel = "12";

        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        new AleSurface(Radio).RequestAllChannelGroups();
        Transport.InjectLine("CHGROUP 01 CHANS 12 29 ");   // a DIFFERENT list
        AnswerSentinel();

        Assert.Contains(nameof(AleViewModel.AnyChannelChoices), seen);
        Assert.Contains(nameof(AleViewModel.SelectedAnyChannel), seen);
        Assert.Contains(nameof(AleViewModel.SelectedAllChannel), seen);
        Assert.Equal("12", vm.SelectedAnyChannel);         // still held

        // …and a refresh that does NOT change the list stays quiet, so the
        // re-announcement is a signal rather than per-refresh noise.
        seen.Clear();
        Transport.InjectLine("SCANNING");
        Assert.DoesNotContain(nameof(AleViewModel.SelectedAnyChannel), seen);
    }

    // ---- F5: the Stop verb branch -------------------------------------------

    [Fact]
    public void Stop_OnAnEstablishedLink_SendsSca_TheCapturedTerminator()
    {
        // F5 (owner ruling 5). `ST` does NOT end a link — P20b's ALL link
        // survived two of them AND a serial session close/reopen. `SCA` is what
        // the radio answers with `ALE> TERMINATING LINK` → `SCANNING`. Same
        // button, no confirm, and the command is visible in the console like
        // every other send.
        var vm = AleReadyVm();
        Transport.InjectLine("LINKED ALL               CHANNEL: 29");
        Transport.ClearSent();

        Assert.Equal("DISCONNECT", vm.StopButtonText);   // the LABEL does not change
        vm.StopCommand.Execute(null);
        Assert.Equal(["SCA"], Transport.SentLines);

        // Post-gesture: the banner moves only on the radio's own answer.
        Assert.True(vm.IsLinked);
        Transport.InjectLine("TERMINATING LINK");
        Transport.InjectLine("SCANNING");
        Assert.False(vm.IsLinked);
        Assert.Equal("Scanning", vm.BannerText);
    }

    [Theory]
    [InlineData("CALLING  AAA              CHANNEL: 01")]
    [InlineData("SENDING  ALL              CHANNEL: 29")]
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]
    [InlineData("SCANNING")]
    public void Stop_EverywhereELSE_KeepsSt_TheCapturedAbort(string announcement)
    {
        // The branch is ONE state wide. Calling, Sending and an LQA all keep ST
        // — the captured abort — and so does a plain scan.
        var vm = AleReadyVm();
        Transport.InjectLine(announcement);
        Transport.ClearSent();

        vm.StopCommand.Execute(null);
        Assert.Equal(["ST"], Transport.SentLines);
    }

    [Fact]
    public void TheStopBranch_ReadsTheRADIOSLinkState_NotAppSideMemory()
    {
        // Invariant 1: the app's ONE behavioural branch branches on the
        // radio-reported state the banner reads. Pressing CALL ALL does NOT
        // make the next Stop send SCA — only the radio saying LINKED does.
        var vm = AleReadyVm();
        vm.CallAllCommand.Execute(null);
        Transport.ClearSent();

        Assert.False(vm.IsLinked);
        vm.StopCommand.Execute(null);
        Assert.Equal(["ST"], Transport.SentLines);       // still the abort
        Transport.ClearSent();

        Transport.InjectLine("LINKED ALL               CHANNEL: 29");
        vm.StopCommand.Execute(null);
        Assert.Equal(["SCA"], Transport.SentLines);      // …now the terminator
    }

    // ---- NOTIFICATION pins: every new bound property actually raises --------

    [Fact]
    public void EveryNewBoundProperty_RaisesPropertyChanged()
    {
        // A binding is only as live as its notification. These four are bound
        // from the pinned rows' markup, and MAUI would render the first value
        // forever without a word of complaint if any of them stopped raising.
        var vm = Vm();
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        ConnectReady();
        Transport.InjectLine("ALE>");
        AnswerSentinel();
        InjectChannelGroups();
        AnswerSentinel();

        Assert.Contains(nameof(AleViewModel.AnyChannelChoices), seen);
        Assert.Contains(nameof(AleViewModel.AllChannelChoices), seen);

        seen.Clear();
        vm.SelectedAnyChannel = "12";
        vm.SelectedAllChannel = "29";
        Assert.Contains(nameof(AleViewModel.SelectedAnyChannel), seen);
        Assert.Contains(nameof(AleViewModel.SelectedAllChannel), seen);
    }

    [Fact]
    public void PickingAnAnyChannel_ReArmsTheCallCommandsCanExecute()
    {
        // The picker's OWN notification path: SelectedAnyChannel is the
        // command's guard, and a pick that did not re-raise CanExecuteChanged
        // would leave the button grey with a channel sitting in it.
        var vm = AleReadyVm();
        int raised = 0;
        vm.CallAnyCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedAnyChannel = "12";

        Assert.True(raised > 0, "picking an ANY channel did not re-arm CallAny");
        Assert.True(vm.CallAnyCommand.CanExecute(null));
    }

    // ---- Constitution: programmatic writes send nothing ---------------------------

    [Fact]
    public void InjectedAleChatter_SendsNothing()
    {
        var vm = AleReadyVm();
        Transport.InjectLine("SCANNING");
        Transport.InjectLine("CALLING  AAA              CHANNEL: 01");
        Transport.InjectLine("SCAN STOPPED");
        Transport.InjectLine("PRG 1-3 CHAR SLF");
        Transport.InjectLine("LINKED AAA");

        Assert.Empty(Transport.SentLines);
        _ = vm;
    }
}
