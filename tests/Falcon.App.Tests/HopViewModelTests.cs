using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The HOP pane (plan §4.3, SELECT-ONLY; restructured per
/// plan-ui-tweaks-round3.md §R): the R1 "Current net" row (the CONFIRMED
/// current net only, rendered from the VERBATIM R9/R9b DIS captures — the
/// picker cannot touch it), the net PICKER (a view cursor that only ever
/// sends the read-only `DIS n`, once per net per session — never `NET n`),
/// the current net only from the confirmed NET report, the
/// once-per-session net-change warning behind the separate Select action,
/// the 7-state sync chip (unreported renders "—" — the enum-default leak
/// class), SEND SYNC greyed-with-reason when SY would be a silent no-op, the
/// lazy once-per-session load (no manual Refresh any more — R4), the
/// post-select SH re-read, and the programmatic-write-sends-nothing
/// constitution pin. Round-4 AB: the green "Selected" chip and its
/// IsPickedNetSelected property are DELETED (the select gate + the R1 row
/// carry that confirmed fact — pinned below), and the Time section is gone
/// with the VM's clock members, so the pane sends NO TI at all.
///
/// <para>Round 11 §7 reflows the frames again — Current net → Status →
/// Select net (the ORDER is pinned in HopPaneMarkupGuardTests, where a
/// document order can actually be read) — and adds two projections this suite
/// owns: the NET INFO VIEW's two lines for the PICKED net, and the StatusText
/// rendering of HopSurface's generation-attempt refusal.</para>
/// </summary>
public class HopViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();

    private HopViewModel Vm() => new(new HopSurface(Radio), Session, _time);

    /// <summary>Ready session confirmed in HOP, with the lazy first-load
    /// traffic (DIS 0 + SH — no TI since round-4 AB3) already drained off the
    /// sent list.</summary>
    private HopViewModel HopReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
        return vm;
    }

    /// <summary>Spin the picker UP until it lands on a net (the operator's
    /// only way to move it — there is no settable property).</summary>
    private static void SpinTo(HopViewModel vm, int net)
    {
        for (int i = 0; i < 10 && vm.PickedNet != net; i++) vm.NetUpCommand.Execute(null);
        Assert.Equal(net, vm.PickedNet);
    }

    /// <summary>Verbatim R9/R9b programmed-net-0 DIS/SH lines.</summary>
    private void InjectProgrammedNet0()
    {
        Transport.InjectLine("NETID    00  12345678");
        Transport.InjectLine("Hoptype 00 NB  ");
        Transport.InjectLine("Center 00  11565 ");
    }

    [Fact]
    public void ValueColumnHeading_FollowsTheCurrentNetsConfirmedType()
    {
        // Round 7 (DD, owner): the Operate value header names what the cell
        // holds — Center / Band / Hoplist — and is generic until the current
        // net's type is confirmed.
        var vm = HopReadyVm();
        Assert.Equal("Frequencies (MHz)", vm.ValueColumnHeading);   // nothing confirmed

        InjectProgrammedNet0();
        Transport.InjectLine("NET  00");                            // NB current
        Assert.Equal("Center (MHz)", vm.ValueColumnHeading);

        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("NET  03");                            // LIST current
        Assert.Equal("Hoplist", vm.ValueColumnHeading);
    }

    private void InjectProgrammedNet3()
    {
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 NB  ");
        Transport.InjectLine("Center 03  11565 ");
    }

    // ---- Lazy load (plan Q4 once-per-session; §M3 PER NET) -------------------

    [Fact]
    public void FirstConfirmedHop_LoadsPaneDataOnce_PickedNetDisAndSh_NoTi()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);        // nothing before HOP confirms

        // §M3: the PICKED net's detail only — never the DIS-all-nets dump.
        Transport.InjectLine("HOP>");
        Assert.Equal(["DIS 0", "SH"], Transport.SentLines);   // AB3: no landing TI
        Assert.DoesNotContain(Transport.SentLines, l => l == "DIS");   // never DIS-all

        // Leaving and re-entering HOP does NOT re-load the NET DETAIL (the
        // `DIS n` cache is once per net per session). Round 15 §3.2: it DOES
        // re-read `SH` once the entry's generation ends — but a bare prompt
        // lap, with no generation, still sends nothing at all.
        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("HOP>");
        Assert.Empty(Transport.SentLines);
        _ = vm;
    }

    // ---- ROUND 15 N1 (§3.2): the lifecycle OBSERVER -------------------------

    [Fact]
    public void AModeLapThatRegenerates_ReReadsShOnce_AndTheChipStopsLying()
    {
        // THE DEFECT, end to end (gate 3a). The operator is in sync, laps
        // out to SSB and back; the radio regenerates on every HOP entry (P4),
        // which drops sync — and says nothing about it. Before round 15 the
        // pane never asked, so the chip kept reading "In sync" until the next
        // operator select.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  00");
        Transport.InjectLine("In_Sync");                  // from the landing SH block
        Assert.Equal("In sync", vm.SyncChipText);
        Transport.ClearSent();

        Transport.InjectLine("SSB>");                     // the lap out…
        Transport.InjectLine("HOP>");                     // …and back in
        Transport.InjectLine("Wait...");
        Transport.InjectLine("Generating Hopset...");     // the entry's own regeneration

        // Core unconfirms on the generation (§3.1): the chip is honest again
        // the instant the radio says it is regenerating.
        Assert.Equal("—", vm.SyncChipText);
        Assert.False(vm.IsSyncConfirmed);
        Assert.True(vm.IsGenerating);
        Assert.Empty(Transport.SentLines);                // …and nothing goes out mid-generation

        Transport.InjectLine("HOP>");                     // the closing prompt ends it
        Assert.Equal(["SH"], Transport.SentLines);        // exactly ONE, and no DIS

        Transport.InjectLine("No_Sync");                  // the answer's own sync line
        Assert.Equal("No sync", vm.SyncChipText);
        Assert.Equal(["SH"], Transport.SentLines);        // still one — the read is per lifecycle
    }

    [Fact]
    public void TheObserver_ReadsOncePerLifecycle_WhoeverStartedIt()
    {
        // Any producer: a settings-pane hopset write, a clone campaign's lap,
        // a front-panel-free re-entry. Two lifecycles, two reads; nothing in
        // between.
        var vm = HopReadyVm();
        Transport.ClearSent();

        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine("Generating Hopset...");      // a repeat is not a new lifecycle
        Assert.Empty(Transport.SentLines);
        Transport.InjectLine("Hopnum 0041");               // clears it
        Assert.Equal(["SH"], Transport.SentLines);

        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(["SH", "SH"], Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void TheObserver_IsIdleOutsideHop_AndAcrossASessionDrop()
    {
        // It arms only while the pane is HOP-ready (nothing reads from a mode
        // the operator is not in), and a session drop clears the flag with the
        // rest — a generation seen before the drop owes nothing after it.
        var vm = HopReadyVm();
        Transport.InjectLine("SSB>");                      // out of HOP
        Transport.ClearSent();

        Transport.InjectLine("Generating Hopset...");      // e.g. the settings pane's write
        Transport.InjectLine("Hopnum 0041");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("HOP>");
        Transport.ClearSent();
        Transport.InjectLine("Generating Hopset...");      // armed now…
        Session.Close();                                   // …but the session drops
        ConnectReady();
        Transport.InjectLine("HOP>");                      // a fresh session's landing load
        Transport.ClearSent();
        Transport.InjectLine("Hopnum 0041");               // the old lifecycle's clearing line
        Assert.Empty(Transport.SentLines);
        _ = vm;
    }

    /// <summary>
    /// A CONTEXT THAT QUEUES, like the phone's. Every Core notification is
    /// marshalled (Q10), so on a real device the pane's Refresh runs when the
    /// UI thread gets to it — not line by line. Draining once after a whole
    /// burst is what a busy UI thread does, and it is the only way to
    /// reproduce a lifecycle that finished before anything looked.
    /// </summary>
    private sealed class QueuingContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue) _queue.Add((d, state));
        }

        /// <summary>Run everything queued — and everything those runs queue —
        /// until the queue is empty.</summary>
        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State)[] batch;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    batch = [.. _queue];
                    _queue.Clear();
                }
                foreach (var (callback, state) in batch) callback(state);
            }
        }
    }

    /// <summary>
    /// AUDIT ROUND 1, MAJOR — THE SELECT MUST ALSO READ THE GENERATION COUNT.
    ///
    /// <para>With real queued marshalling the radio's whole answer — the NET
    /// echo, the generation and its clearing line — can be parsed before the
    /// pane runs ONCE. The observer survives that (it diffs a count), but the
    /// select flow used to SAMPLE <c>IsGeneratingHopset</c>: it never saw
    /// TRUE, stayed armed for the full 10-s escape, sent a SECOND `SH` when
    /// the escape fired, and left SEND SYNC greyed until then. Two reads for
    /// one lifecycle is exactly what I-3 forbids.</para>
    /// </summary>
    [Fact]
    public void ASelectWhoseWholeLifecycleLandsBeforeOneRefresh_SendsExactlyOneSh()
    {
        var context = new QueuingContext();
        var transport = new InjectingTransport();
        var radio = new Prc138Radio(transport, context);
        using var session = new RadioSession(radio, transport, context)
        { ReconnectIntervalMs = 3_600_000 };
        var vm = new HopViewModel(new HopSurface(radio), session, _time);

        session.Connect(TestSettings);
        transport.InjectLine("Battery Status FULL 31.4V");
        transport.InjectLine("Battery Status FULL 31.4V");
        context.Drain();
        Assert.Equal(SessionPhase.Ready, session.Phase);

        transport.InjectLine("HOP>");                       // HOP confirmed, pane lands
        transport.InjectLine("NETID    00  12345678");
        transport.InjectLine("Hoptype 00 NB  ");
        transport.InjectLine("Center 00  11565 ");
        transport.InjectLine("NET  01");                    // the radio is on ANOTHER net
        context.Drain();
        transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);            // the picker sits on net 0
        Assert.Equal(["NET 0"], transport.SentLines);

        // THE WHOLE LIFECYCLE, parsed while the UI thread is busy elsewhere.
        transport.InjectLine("NET  00");
        transport.InjectLine("HOP>");
        transport.InjectLine("Wait...");
        transport.InjectLine("Generating Hopset...");
        transport.InjectLine("Hopnum 0041");
        transport.InjectLine("In_Sync");
        context.Drain();                                    // …and NOW the pane runs

        Assert.Equal(["NET 0", "SH"], transport.SentLines); // exactly ONE read
        Assert.True(vm.CanSendSync);                        // …released on the outcome…
        Assert.DoesNotContain("selection in progress", vm.SendSyncDisabledReason);

        // …not by the escape, which must now find nothing to release.
        _time.Now += TimeSpan.FromSeconds(11);
        transport.InjectLine("No_Sync");
        context.Drain();
        Assert.Equal(["NET 0", "SH"], transport.SentLines);
    }

    [Fact]
    public void ASelectFlow_StillSendsExactlyOneSh_TheObserverYields()
    {
        // I-3: ONE pane-originated `SH` per generation lifecycle, whoever
        // started it. The select flow claims the lifecycle it started and the
        // observer must not add a second read on the same one.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NET  00");
        Transport.InjectLine("Wait...");
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");

        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
    }

    [Fact]
    public void ANoHopsetSelect_SendsOneSh_AndLeavesTheObserverIdle()
    {
        // A select that ends on No Hopset never sets generation TRUE, so the
        // observer never arms; the select flow's own path sends the one read.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NET  00");
        Transport.InjectLine(" Wait...");
        Transport.InjectLine("No Hopset");

        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
    }

    [Fact]
    public void NewSession_LoadsAgain_AndTheNetCacheResets()
    {
        var vm = HopReadyVm();
        SpinTo(vm, 2);
        Transport.ClearSent();

        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");
        // The picker cursor survives (it is view state); its per-net cache
        // does not — the new session re-queries the net on screen.
        Assert.Equal(["DIS 2", "SH"], Transport.SentLines);
    }

    // ---- §M3 per-net lazy load: once per net per session ---------------------

    [Fact]
    public void SpinningThePicker_SendsDisForThatNet_OncePerNetPerSession()
    {
        var vm = HopReadyVm();          // net 0 already queried by the load

        vm.NetUpCommand.Execute(null);
        Assert.Equal(1, vm.PickedNet);
        Assert.Equal("1", vm.PickedNetText);
        Assert.Equal(["DIS 1"], Transport.SentLines);

        vm.NetUpCommand.Execute(null);
        Assert.Equal(["DIS 1", "DIS 2"], Transport.SentLines);

        // Back to an already-queried net: re-render from the mirror, no wire.
        vm.NetDownCommand.Execute(null);
        vm.NetDownCommand.Execute(null);
        Assert.Equal(0, vm.PickedNet);
        Assert.Equal(["DIS 1", "DIS 2"], Transport.SentLines);

        // …and forward again over the cached nets: still nothing.
        vm.NetUpCommand.Execute(null);
        vm.NetUpCommand.Execute(null);
        Assert.Equal(["DIS 1", "DIS 2"], Transport.SentLines);
        Assert.Equal(1, Transport.CountSent("DIS 1"));
        Assert.Equal(1, Transport.CountSent("DIS 2"));
    }

    [Fact]
    public void ThePicker_Wraps_0To9_AndNeverSendsNet()
    {
        var vm = HopReadyVm();

        vm.NetDownCommand.Execute(null);          // 0 → 9 (wrap)
        Assert.Equal(9, vm.PickedNet);
        vm.NetUpCommand.Execute(null);            // 9 → 0 (wrap)
        Assert.Equal(0, vm.PickedNet);

        // A full lap of the dial (§M4): every net queried read-only, and NOT
        // ONE `NET n` — selecting regenerates the hopset and TUNES THE
        // COUPLER, so a spin must never do it.
        for (int i = 0; i < 10; i++) vm.NetUpCommand.Execute(null);
        Assert.Equal(0, vm.PickedNet);
        Assert.All(Transport.SentLines, line => Assert.StartsWith("DIS ", line));
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("NET", StringComparison.Ordinal));
        // Nine reads for nine nets: 0 was already cached by the lazy load.
        Assert.Equal(9, Transport.SentLines.Count);
    }

    [Fact]
    public void SpinningOutsideHop_SendsNothing_AndCatchesUpOnReturn()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("SSB>");              // HOP no longer confirmed
        Transport.ClearSent();

        SpinTo(vm, 4);
        Assert.Empty(Transport.SentLines);         // no wire while out of HOP

        Transport.InjectLine("HOP>");
        Assert.Equal(["DIS 4"], Transport.SentLines);
    }

    [Fact]
    public void ThePane_HasNoRefreshCommand_R4()
    {
        // R4: the Refresh button is gone from the pane, and with it the
        // command — the first-Ready lazy load, the per-net DIS on a picker
        // landing and the post-select SH re-read are the only reads left.
        Assert.Null(typeof(HopViewModel).GetProperty("RefreshNetsCommand"));
        Assert.DoesNotContain(typeof(HopViewModel).GetProperties(),
            p => p.Name.Contains("Refresh", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePane_HasNoSelectedMarker_AndNoClockMembers_R4()
    {
        // Round-4 AB2/AB3, the same absence idiom as the R4 pin above: the
        // Selected chip's property and the whole clock leg are DELETED, not
        // merely unbound — a re-added member would give the pane a second
        // source of truth for the current net (AB2) or a second radio clock
        // (AB3, whose one source is now DeviceSettingsViewModel).
        var t = typeof(HopViewModel);
        Assert.Null(t.GetProperty("IsPickedNetSelected"));
        Assert.Null(t.GetProperty("RadioTodText"));
        Assert.Null(t.GetProperty("SetTimeFromDeviceCommand"));
        Assert.DoesNotContain(t.GetProperties(),
            p => p.Name.Contains("Tod", StringComparison.OrdinalIgnoreCase)
              || p.Name.Contains("Time", StringComparison.OrdinalIgnoreCase));
    }

    // ---- §R1 "Current net" row: the CONFIRMED current net, from verbatim
    // ---- DIS fixtures — the picker never touches it --------------------------

    [Fact]
    public void ActiveNetRow_ProgrammedCurrentNet_RendersTheReportedFields()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();                                  // R9 capture
        Transport.InjectLine("NET  00");

        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.Equal("12345678", vm.ActiveNetIdText);
        Assert.Equal("NB", vm.ActiveTypeText);
        Assert.Equal("11.565", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_UnreportedCurrentNet_RendersDashes_NeverADefault()
    {
        var vm = HopReadyVm();
        Transport.ClearSent();

        // No NET report this session: the whole row is "—", including the
        // number — net 0 is the PICKER's home, never an assumed current net.
        Assert.Equal("—", vm.ActiveNetText);
        Assert.Equal("—", vm.ActiveNetIdText);
        Assert.Equal("—", vm.ActiveTypeText);
        Assert.Equal("—", vm.ActiveHopsetText);
        Assert.Empty(Transport.SentLines);       // programmatic writes send nothing
    }

    [Fact]
    public void ActiveNetRow_CurrentNetReportedButNoDisYet_NumberOnly()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NET  03");         // SH says net 3; no DIS 3 answer yet

        Assert.Equal("NET 3", vm.ActiveNetText);
        Assert.Equal("—", vm.ActiveNetIdText);   // unreported ≠ unprogrammed
        Assert.Equal("—", vm.ActiveTypeText);
        Assert.Equal("—", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_IgnoresThePicker()
    {
        // The R1 row is radio truth; the picker is app-side view state. Moving
        // the picker — even onto another REPORTED net — must not change one
        // cell of the row.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        InjectProgrammedNet3();
        Transport.InjectLine("NET  00");                   // net 0 is current

        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.Equal("12345678", vm.ActiveNetIdText);

        SpinTo(vm, 3);                                     // look at net 3
        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.Equal("12345678", vm.ActiveNetIdText);
        Assert.Equal("NB", vm.ActiveTypeText);
        Assert.Equal("11.565", vm.ActiveHopsetText);

        SpinTo(vm, 7);                                     // …and at an unreported net
        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.Equal("12345678", vm.ActiveNetIdText);

        // The row moves only when the RADIO says the net moved.
        Transport.InjectLine("NET  03");
        Assert.Equal("NET 3", vm.ActiveNetText);
        Assert.Equal("22334455", vm.ActiveNetIdText);
    }

    // ---- The three display states of the R1 row (round-4 Phase D) ------------
    // Unreported "—" per field · CONFIRMED unprogrammed (the radio's own
    // X-form) · reported. The middle state is read from the mirror's
    // IsReportedUnprogrammed marker and NEVER inferred from a null ID.

    [Fact]
    public void ActiveNetRow_ConfirmedUnprogrammedCurrentNet_SaysSo()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    05  XXXXXXXX");           // wiped form
        Transport.InjectLine("Hoptype 05 WB  ");
        Transport.InjectLine("Hopset 05  XXXXXX  XXXXXX");
        Transport.InjectLine("NET  05");

        Assert.Equal("NET 5", vm.ActiveNetText);
        Assert.Equal("XXXXXXXX", vm.ActiveNetIdText);
        Assert.Equal("WB", vm.ActiveTypeText);
        Assert.Equal("not programmed", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_TypeOnlyReport_LeavesTheUnheardIdAtDash_NeverClaimsUnprogrammed()
    {
        // The over-claim this round killed: a mirror record is created by
        // whichever DIS line arrives FIRST, so a Hoptype line alone leaves a
        // null NetId that NOBODY reported. The row used to render that null as
        // the radio's "XXXXXXXX" / "not programmed" — announcing a fact the
        // radio never stated. Unheard is "—", per field.
        var vm = HopReadyVm();
        Transport.InjectLine("Hoptype 06 NB  ");
        Transport.InjectLine("NET  06");

        Assert.Equal("NET 6", vm.ActiveNetText);
        Assert.Equal("—", vm.ActiveNetIdText);
        Assert.Equal("NB", vm.ActiveTypeText);       // the ONE thing reported
        Assert.Equal("—", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_UnprogrammedThenProgrammed_DropsTheUnprogrammedWords()
    {
        // The bench cycle: wipe, then program. The marker is cleared by the
        // real NETID report, so the row stops saying "not programmed".
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("NET  05");
        Assert.Equal("XXXXXXXX", vm.ActiveNetIdText);

        Transport.InjectLine("NETID    05  12345678");
        Transport.InjectLine("Hoptype 05 NB  ");
        Transport.InjectLine("Center 05  11565 ");

        Assert.Equal("12345678", vm.ActiveNetIdText);
        Assert.Equal("NB", vm.ActiveTypeText);
        Assert.Equal("11.565", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_ListNet_ShowsHoplistCount_Session16Capture()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Transport.InjectLine("NET  03");

        Assert.Equal("LIST", vm.ActiveTypeText);
        Assert.Equal("3 freqs", vm.ActiveHopsetText);
    }

    // ---- Round-5 BD2: the value cell's vocabulary ---------------------------
    // One header, "Frequencies (MHz)", over BARE numbers: an NB centre, a WB
    // band, a LIST count. The unit left the cell with round 5 (BD1), and the
    // WB placeholder "Wideband" is gone now that Core mirrors the edges.

    [Fact]
    public void ActiveNetRow_WidebandNet_ShowsTheBand_NotAPlaceholder()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    02  24680135");
        Transport.InjectLine("Hoptype 02 WB");
        Transport.InjectLine("Hopset 02  02000  08000");
        Transport.InjectLine("NET  02");

        Assert.Equal("WB", vm.ActiveTypeText);
        Assert.Equal("2.000–8.000", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_WidebandNet_WithNoEdgesReported_StaysUnreported()
    {
        // Type without edges is the pre-round-5 case (and the case a radio
        // whose Hopset line does not match the PROVISIONAL shape will produce).
        // It must read "—", never a half band and never the old placeholder.
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    02  24680135");
        Transport.InjectLine("Hoptype 02 WB");
        Transport.InjectLine("NET  02");

        Assert.Equal("WB", vm.ActiveTypeText);
        Assert.Equal("—", vm.ActiveHopsetText);
    }

    [Fact]
    public void ActiveNetRow_ListNet_WithNoHoplistYet_UsesTheFallbackWord()
    {
        // No DIS answer carries a hoplist, so between "the type is LIST" and
        // "the HOPLIST answer landed" the count is genuinely unknown.
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("NET  03");

        Assert.Equal("Frequency list", vm.ActiveHopsetText);
    }

    // ---- BC4: the lazy HOPLIST read, CURRENT net only -----------------------

    [Fact]
    public void ListCurrentNet_TriggersOneHopListRead_OncePerSession()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NET  03");
        Transport.ClearSent();                       // drain the DIS 3 that follows

        // The trigger is the CONFIRMED type, not the net number: nothing goes
        // out until DIS says net 3 is LIST.
        Transport.InjectLine("NETID    03  22334455");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);

        // Once per net per session — later mirror events re-render only.
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);
        Assert.Equal("3 freqs", vm.ActiveHopsetText);
    }

    [Fact]
    public void HopListRead_IsScopedToTheCurrentNet_NotThePicker()
    {
        // BC4 scope: this pane shows a value cell for the CURRENT net only, so
        // a LIST net the operator merely LOOKS at must not cost a read. (The
        // settings pane owns the all-ten trigger — it renders all ten.)
        var vm = HopReadyVm();
        SpinTo(vm, 4);
        Transport.ClearSent();

        Transport.InjectLine("NETID    04  22334455");
        Transport.InjectLine("Hoptype 04 LIST");
        Assert.Empty(Transport.SentLines);            // picked, not current

        Transport.InjectLine("NET  04");              // now the radio is on it
        Assert.Contains("HOPLIST 4", Transport.SentLines);
    }

    [Fact]
    public void NonListCurrentNet_NeverTriggersAHopListRead()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();                       // NB
        Transport.InjectLine("NET  00");
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("HOPLIST", StringComparison.Ordinal));
        _ = vm;
    }

    [Fact]
    public void NewSession_ReArmsTheHopListRead()
    {
        // The once-set clears ONLY on session reset (this pane has no manual
        // Refresh) — a new radio must be asked again.
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("NET  03");
        Assert.Contains("HOPLIST 3", Transport.SentLines);

        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("NET  03");
        Assert.Contains("HOPLIST 3", Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void ActiveNet_IsReadOncePerSession_ReadOnlyDisNeverNet()
    {
        // V5 feeds the R1 row: the CONFIRMED current net gets the same cheap
        // read-only `DIS n`, through the same per-session cache. It must never
        // become a `NET n` — that would regenerate the hopset and transmit.
        var vm = HopReadyVm();                             // net 0 read by the load
        Transport.InjectLine("NET  03");                   // radio reports net 3
        Assert.Equal(["DIS 3"], Transport.SentLines);

        Transport.InjectLine("NET  03");                   // re-report: cached
        Assert.Equal(["DIS 3"], Transport.SentLines);

        // Spinning onto it later does not re-read it (already cached).
        SpinTo(vm, 3);
        Assert.Equal(1, Transport.CountSent("DIS 3"));
        Assert.All(Transport.SentLines, line => Assert.StartsWith("DIS ", line));
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("NET", StringComparison.Ordinal));
    }

    // ---- Picker select gate (per-net, from the same mirror) ------------------

    [Fact]
    public void PickedNet_Programmed_EnablesSelect()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();                                  // R9 capture

        Assert.True(vm.CanSelectPickedNet);
        Assert.Equal("", vm.SelectDisabledReason);
    }

    [Fact]
    public void PickedNet_ConfirmedUnprogrammed_BlocksSelect()
    {
        var vm = HopReadyVm();
        SpinTo(vm, 5);
        Transport.InjectLine("NETID    05  XXXXXXXX");           // wiped form
        Transport.InjectLine("Hoptype 05 WB  ");
        Transport.InjectLine("Hopset 05  XXXXXX  XXXXXX");

        Assert.False(vm.CanSelectPickedNet);
        Assert.Contains("not programmed", vm.SelectDisabledReason);
    }

    [Fact]
    public void PickedNet_TypeReportedButIdUnheard_BlocksSelect_WithoutClaimingUnprogrammed()
    {
        // Same over-claim, second site: the gate blocks either way, but only
        // the radio's X-form licenses the words "is not programmed".
        var vm = HopReadyVm();
        SpinTo(vm, 6);
        Transport.InjectLine("Hoptype 06 WB  ");

        Assert.False(vm.CanSelectPickedNet);
        Assert.Contains("Waiting for the radio", vm.SelectDisabledReason);
        Assert.DoesNotContain("not programmed", vm.SelectDisabledReason);
    }

    [Fact]
    public void PickedNet_Unreported_BlocksSelect_AndSendsNothing()
    {
        var vm = HopReadyVm();
        SpinTo(vm, 7);
        Transport.ClearSent();

        // Unreported ≠ unprogrammed: no DIS line has covered net 7 yet.
        Assert.False(vm.CanSelectPickedNet);
        Assert.Contains("Waiting for the radio", vm.SelectDisabledReason);
        Assert.Empty(Transport.SentLines);       // programmatic writes send nothing
    }

    // ---- Which net is CURRENT: only from the confirmed NET report ------------
    // Round-4 AB2 deleted the green "Selected" chip and its
    // IsPickedNetSelected property. The same confirmed fact now shows in TWO
    // places, and this pins both: the R1 "Current net" row names the net, and
    // the select gate CLOSES on it with the "already the radio's current net"
    // reason. Nothing here may move before the radio's NET report.

    [Fact]
    public void CurrentNet_OnlyFromConfirmedNetReport_DrivesTheRowAndTheSelectGate()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Assert.Equal("—", vm.ActiveNetText);               // unreported = no current net
        Assert.True(vm.CanSelectPickedNet);                // net 0 is programmed, not current

        Transport.InjectLine("NET  01");                   // R9 SH-block form
        Assert.Equal("NET 1", vm.ActiveNetText);
        Assert.True(vm.CanSelectPickedNet);                // picker is on net 0, not current

        Transport.InjectLine("NET  00");                   // the radio moves to net 0
        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.False(vm.CanSelectPickedNet);               // picker IS on the current net
        Assert.Contains("already the radio's current net", vm.SelectDisabledReason);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("NET", StringComparison.Ordinal));
    }

    // ---- Select flow: IMMEDIATE (round 6, CD — owner deleted the warning) ---

    [Fact]
    public void Select_SendsNetImmediately_NoWarningStep()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);      // the press IS the send
    }

    [Fact]
    public void EverySelect_IsImmediate_NotJustTheFirst()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        InjectProgrammedNet3();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);
        Transport.InjectLine("NET  00");                   // radio confirms
        SpinTo(vm, 3);
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 3"], Transport.SentLines);
    }

    [Fact]
    public void UnprogrammedOrUnreportedNet_SelectDisabled_NothingSent()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");

        SpinTo(vm, 5);                                     // confirmed unprogrammed
        Transport.ClearSent();
        Assert.False(vm.CanSelectPickedNet);
        vm.SelectPickedNetCommand.Execute(null);

        SpinTo(vm, 7);                                     // 5 → … → 7, unreported
        Assert.False(vm.CanSelectPickedNet);
        vm.SelectPickedNetCommand.Execute(null);

        Assert.All(Transport.SentLines, line => Assert.StartsWith("DIS ", line));
    }

    [Fact]
    public void SelectCurrentNet_Guarded_NothingSent()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  00");                   // net 0 is current
        Transport.ClearSent();

        Assert.False(vm.CanSelectPickedNet);
        Assert.Contains("already the radio's current net", vm.SelectDisabledReason);
        vm.SelectPickedNetCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OutsideHop_SelectDisabled_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        InjectProgrammedNet0();
        Transport.ClearSent();

        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);
        Assert.False(vm.CanSelectPickedNet);
        vm.SelectPickedNetCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- No optimistic current-net move (audit F2, §2.4 constitution) --------

    [Fact]
    public void CurrentNet_DoesNotMove_BetweenNetSendAndConfirmingReport()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);      // command is on the wire

        // …but the radio has not confirmed: neither the R1 row nor the select
        // gate may move (the row still describes the net the radio is on).
        Assert.Equal("NET 1", vm.ActiveNetText);
        Assert.Equal("—", vm.ActiveNetIdText);             // no DIS 1 answer yet
        Assert.True(vm.CanSelectPickedNet);                // net 0 is still not current

        Transport.InjectLine("NET  00");                   // the confirming report
        Assert.Equal("NET 0", vm.ActiveNetText);
        Assert.Equal("12345678", vm.ActiveNetIdText);
        Assert.False(vm.CanSelectPickedNet);               // …now it is
    }

    // ---- Post-select SH re-read (Hopnum/sync live only in SH) ----------------

    [Fact]
    public void AfterSelect_GenerationLifecycleEnd_TriggersOneShReread()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);

        // Verbatim R9b lifecycle: no SH until the generation ends.
        Transport.InjectLine("NET  00");
        Transport.InjectLine("HOP>");
        Transport.InjectLine("Wait...");
        Transport.InjectLine("Generating Hopset...");
        Assert.Equal(["NET 0"], Transport.SentLines);

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");          // clears generation
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);

        // The SELECT flow's re-read is one-shot. What follows is a SECOND
        // generation lifecycle, and round 15's observer (§3.2) closes that one
        // with its own single `SH` — I-3 is one pane `SH` per LIFECYCLE, not
        // one per session.
        Transport.InjectLine("Generating Hopset...");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);          // …nothing while it runs
        Transport.InjectLine("Hopnum 0041");                          // clears generation
        Assert.Equal(["NET 0", "SH", "SH"], Transport.SentLines);
    }

    [Fact]
    public void SelectOfHopsetlessNet_NoHopsetOutcome_FiresOneRereadAndDisarms()
    {
        // Audit F4: a programmed-but-hopset-less select answers
        // NET echo → Wait... → No Hopset, with NO Generating line — the
        // No-Hopset outcome completes the select (one SH, flag disarmed).
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);

        // Verbatim hopset-less lifecycle (R9 found-state / Stage 5 gate shape).
        Transport.InjectLine("NET  00");
        Transport.InjectLine("HOP>");
        Transport.InjectLine(" Wait...");
        Transport.InjectLine("No Hopset");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);

        // Disarmed: a repeated No-Hopset report cannot re-fire the select's
        // re-read…
        Transport.InjectLine("No Hopset");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
        // …and the generation lifecycle that follows is a NEW one, closed by
        // round 15's observer with one `SH` of its own (§3.2).
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine("Hopnum 0041");
        Assert.Equal(["NET 0", "SH", "SH"], Transport.SentLines);
    }

    [Fact]
    public void StragglerNoHopset_ForTheOldNet_DoesNotCompleteTheSelect()
    {
        // Observed live (Stage 5 gate run 3): an SH answer queued BEFORE the
        // select can interleave its No_Hopset line after NET n goes out —
        // it describes the OLD net and must not complete the select.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);

        Transport.InjectLine("No_Hopset");                 // straggler: old net's SH tail
        Assert.Equal(["NET 0"], Transport.SentLines);      // no premature re-read

        Transport.InjectLine("NET  00");                   // now the select confirms
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
    }

    // ---- Stage 8: bounded escape for a refused select ------------------------

    [Fact]
    public void RefusedSelect_EscapesAfterDeadline_OneRereadAndGatesRelease()
    {
        // A radio-REFUSED net select answers ** ERROR ** — no NET change, no
        // generation, no No-Hopset line. Before Stage 8 the pending-select
        // gates (SEND SYNC among them) stayed closed until the next select
        // or reconnect; now the deadline releases them and the one-shot SH
        // re-read goes out anyway.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("Hopnum 0041");
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);

        Transport.InjectLine("** ERROR **");               // the refusal, verbatim
        Assert.False(vm.CanSendSync);
        Assert.Contains("selection in progress", vm.SendSyncDisabledReason);
        Assert.Equal(["NET 0"], Transport.SentLines);      // no premature re-read

        // Inside the window nothing releases, even on hop-domain chatter.
        _time.Now += TimeSpan.FromSeconds(5);
        Transport.InjectLine("Hopnum 0042");
        Assert.False(vm.CanSendSync);
        Assert.Equal(["NET 0"], Transport.SentLines);

        // Past the deadline: the next Refresh (any hop change, or the escape
        // timer's wake-up in production) releases the gates and re-reads.
        _time.Now += TimeSpan.FromSeconds(6);
        Transport.InjectLine("Hopnum 0043");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
        Assert.True(vm.CanSendSync);                       // gate released, Hopnum > 0
        Assert.DoesNotContain("selection in progress", vm.SendSyncDisabledReason);

        // The escape is one-shot too — the generation that follows is a NEW
        // lifecycle and belongs to round 15's observer (§3.2), which closes it
        // with exactly one `SH`.
        Transport.InjectLine("Generating Hopset...");
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
        Transport.InjectLine("Hopnum 0044");
        Assert.Equal(["NET 0", "SH", "SH"], Transport.SentLines);
    }

    [Fact]
    public void Escape_NeverFiresMidGeneration_LifecycleStillCompletesNormally()
    {
        // The escape must not release the gates (or send SH) while the radio
        // has announced generation — SY/SH mid-generation stays conservative
        // (audit F6); the generation lifecycle has its own clearing lines.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NET  00");
        Transport.InjectLine("Generating Hopset...");      // slow tune can exceed 10 s

        _time.Now += TimeSpan.FromSeconds(11);
        Transport.InjectLine("In_Sync");                   // hop change → Refresh, mid-generation
        Assert.Equal(["NET 0"], Transport.SentLines);      // NO escape, NO stray SH

        Transport.InjectLine(" TUNE COMPLETE  ");          // generation ends normally
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
    }

    // ---- Status block ----------------------------------------------------------

    [Fact]
    public void Hopnum_UnreportedRendersDash_ReportRenders4Digit()
    {
        var vm = HopReadyVm();
        Assert.Equal("Hopnum —", vm.HopnumText);

        Transport.InjectLine("Hopnum 0041");
        Assert.Equal("Hopnum 0041", vm.HopnumText);
    }

    [Fact]
    public void GenerationLifecycle_R9bCapture_DrivesTheIndicator()
    {
        var vm = HopReadyVm();
        Assert.False(vm.IsGenerating);

        Transport.InjectLine("Wait...");
        Transport.InjectLine("Generating Hopset...");
        Assert.True(vm.IsGenerating);

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.False(vm.IsGenerating);
    }

    // ---- Sync chip: 7 states + unreported "—" -----------------------------------

    // ROUND 13 §4 A2 — CONTRACT CHANGE (item 10, owner 2026-08-19). The chip
    // used to print the radio's own underscore tokens; it prints PROSE now.
    // Constitution §3.2: no raw wire tokens operator-facing on the surfaces
    // this round touches — the raw line is still in the Console feed, which is
    // where the evidence belongs. The mapping stays ONE-TO-ONE (all seven
    // states survive; the two-lamp lossiness is not coming back), so these
    // pins keep their shape and only their expected strings move. The WIRE
    // half of each InlineData is deliberately unchanged: it is what the radio
    // sends, and nothing about that moved.

    [Fact]
    public void SyncChip_UnreportedRendersDash_NotNoSync()
    {
        var vm = HopReadyVm();
        // Enum ordinal 0 is NoSync — a default leak would render "No sync".
        Assert.False(vm.IsSyncConfirmed);
        Assert.Equal("—", vm.SyncChipText);
    }

    [Theory]
    [InlineData("No_Sync", "No sync")]
    [InlineData("In_Sync", "In sync")]
    [InlineData("Awaiting_Sync", "Awaiting sync")]
    [InlineData("Sending_Sync_Req", "Sync request sent")]
    [InlineData("Sync_Req_Rcv", "Sync request received")]
    [InlineData("Sending_Sync_Rsp", "Sync response sent")]
    [InlineData("Sync_Failed", "Sync failed")]
    public void SyncChip_AllSevenStates_InProse(string line, string expected)
    {
        var vm = HopReadyVm();
        Transport.InjectLine(line);
        Assert.True(vm.IsSyncConfirmed);
        Assert.Equal(expected, vm.SyncChipText);
        Assert.Empty(Transport.SentLines);

        // …and the wire token itself never reaches the chip (§3.2). Pinned
        // generically rather than per-case so a single token creeping back
        // through a future edit fails here, not on the bench.
        Assert.DoesNotContain('_', vm.SyncChipText);
    }

    [Fact]
    public void SyncChip_TheSevenStates_AreSevenDISTINCTStrings()
    {
        // The humanization must not have COLLAPSED two states into one word:
        // the whole reason the chip carries seven states is that the old
        // two-lamp display hid Sync_Failed. Pinned as a set so a copy-paste
        // duplicate in the switch fails here — the per-case theory above would
        // still pass for six of them.
        string[] wire =
        [
            "No_Sync", "In_Sync", "Awaiting_Sync", "Sending_Sync_Req",
            "Sync_Req_Rcv", "Sending_Sync_Rsp", "Sync_Failed",
        ];

        var vm = HopReadyVm();
        var rendered = new List<string>();
        foreach (var line in wire)
        {
            Transport.InjectLine(line);
            rendered.Add(vm.SyncChipText);
        }

        Assert.Equal(7, rendered.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("—", rendered);      // no state falls through to the default
    }

    [Fact]
    public void SyncChipText_RaisesItsOwnPropertyChanged_OnEveryTransition()
    {
        // A2 audit round 1, MINOR 1 — the A1 lesson applied here. The chip's
        // Label BINDS SyncChipText, so a value that changes without raising
        // leaves the chip showing the PREVIOUS state while every value
        // assertion in this file still passes. The auditor proved exactly
        // that: swapping the observable property for a value-equivalent
        // non-notifying setter kept all 1,811 App tests green.
        var vm = HopReadyVm();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Transport.InjectLine("Sending_Sync_Req");
        Assert.Equal("Sync request sent", vm.SyncChipText);
        Assert.Contains(nameof(HopViewModel.SyncChipText), raised);

        // …and again on a LATER transition: a notification that fires only on
        // the first confirmation would strand the chip on every state after
        // it, which is the async Sync_Failed case — the one the operator most
        // needs to see.
        raised.Clear();
        Transport.InjectLine("Sync_Failed");
        Assert.Equal("Sync failed", vm.SyncChipText);
        Assert.Contains(nameof(HopViewModel.SyncChipText), raised);
    }

    [Fact]
    public void SyncLifecycle_R9bCapture_IncludingAsyncFailure()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("Sending_Sync_Req");
        Assert.Equal("Sync request sent", vm.SyncChipText);
        Transport.InjectLine("Awaiting_Sync");
        Assert.Equal("Awaiting sync", vm.SyncChipText);
        Transport.InjectLine("Sync_Failed");               // async, ~35 s later
        Assert.Equal("Sync failed", vm.SyncChipText);
        Assert.True(vm.IsSyncFailed);
    }

    // ---- SEND SYNC: greyed-with-reason when SY would be a silent no-op ----------

    [Fact]
    public void SendSync_UnreportedHopnum_DisabledWithReason()
    {
        var vm = HopReadyVm();
        Assert.False(vm.CanSendSync);
        Assert.Contains("Hopnum", vm.SendSyncDisabledReason);

        vm.SendSyncCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SendSync_HopnumZero_DisabledWithSilentNoOpReason()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("Hopnum 0000");
        Assert.False(vm.CanSendSync);
        Assert.Contains("silent no-op", vm.SendSyncDisabledReason);

        vm.SendSyncCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SendSync_WithHopset_SendsSy()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("Hopnum 0041");
        Assert.True(vm.CanSendSync);
        Assert.Equal("", vm.SendSyncDisabledReason);

        vm.SendSyncCommand.Execute(null);
        Assert.Equal(["SY"], Transport.SentLines);
    }

    [Fact]
    public void SendSync_DuringGeneration_DisabledWithReason()
    {
        // Audit F6: SY mid-generation is an UNPROBED wire fact — the UI
        // must not offer it while the radio reports a generation in flight.
        var vm = HopReadyVm();
        Transport.InjectLine("Hopnum 0041");
        Assert.True(vm.CanSendSync);

        Transport.InjectLine("Generating Hopset...");
        Assert.False(vm.CanSendSync);
        Assert.Contains("generation", vm.SendSyncDisabledReason);
        vm.SendSyncCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine(" TUNE COMPLETE  ");          // clears generation
        Assert.True(vm.CanSendSync);
    }

    [Fact]
    public void SendSync_WhileSelectAwaitsItsReread_DisabledWithReason()
    {
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("Hopnum 0041");
        Transport.InjectLine("NET  01");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Assert.False(vm.CanSendSync);
        Assert.Contains("selection in progress", vm.SendSyncDisabledReason);
        vm.SendSyncCommand.Execute(null);
        Assert.Equal(["NET 0"], Transport.SentLines);      // no SY leaked

        Transport.InjectLine("NET  00");
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine(" TUNE COMPLETE  ");          // outcome + one-shot SH
        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
        Assert.True(vm.CanSendSync);                       // Hopnum 0041 still confirmed
    }

    [Fact]
    public void SendSync_OutsideHop_Disabled()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("Hopnum 0041");
        Transport.ClearSent();

        Assert.False(vm.CanSendSync);
        vm.SendSyncCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- List_Invalid badge on the affected (current) net ------------------------

    [Fact]
    public void ListInvalid_BadgesTheCurrentNetOnly()
    {
        var vm = HopReadyVm();
        Transport.InjectLine("NETID    03  22334455");
        Transport.InjectLine("Hoptype 03 LIST");
        Transport.InjectLine("NET  03");
        Transport.InjectLine("List_Invalid");

        Assert.False(vm.IsPickedNetListInvalid);           // picker is on net 0
        SpinTo(vm, 3);
        Assert.True(vm.IsPickedNetListInvalid);
    }

    // ---- Time section: GONE from this VM (round-4 AB3) -----------------------
    // The radio clock moved to Mode settings → HOP over
    // DeviceSettingsViewModel, which is now the app's ONE clock state source
    // and already owns the equivalent pins (DeviceSettingsViewModelTests:
    // unreported "—" → verbatim TI report, and the zero-padded TIME+DAT+DAY
    // set). What this VM must pin is the ABSENCE: no clock read on landing.
    // The lazy-load pins above assert the exact sent list `DIS n` + `SH`, and
    // this one pins that no TI escapes the pane on ANY of its own traffic —
    // landing, picker spin or post-select re-read.

    [Fact]
    public void ThePaneNeverSendsTi_NotOnLanding_NorSpin_NorPostSelectReread()
    {
        var vm = HopReadyVm();          // landing traffic already drained
        InjectProgrammedNet0();
        SpinTo(vm, 3);                  // a picker landing (DIS 3)
        SpinTo(vm, 0);

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine("Hopnum 0041");               // lifecycle ends → SH re-read

        Assert.DoesNotContain(Transport.SentLines, l => l == "TI");
    }

    // ==== ROUND 11 SECTION 7 =================================================

    // ---- The StatusText projection -----------------------------------------
    // The state machine is HopSurface's (its triggers span both HOP view
    // models). What this VM owes is a FAITHFUL projection: the sentence when
    // the surface holds a refusal, nothing when it does not, and a redraw when
    // the surface says so - the refusal arrives on its OWN event, not through
    // the mirror's Changed, so a VM that only subscribed to Changed would
    // render the right words at the wrong time.

    [Fact]
    public void StatusText_IsEmptyUntilAGenerationAttemptIsRefused()
    {
        var vm = HopReadyVm();

        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasStatusText);

        // A NO NET ID with no attempt behind it is not this pane's business:
        // the surface refuses to call it a refusal, and so the pane says
        // nothing.
        Transport.InjectLine("NO NET ID");
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasStatusText);
    }

    [Fact]
    public void StatusText_RendersTheRefusal_AfterASelectThatGeneratesNothing()
    {
        // The captured sequence (docs/protocol.md, the `NET n` select echo):
        // selecting a net that HAS a hopset but NO net ID answers NET /
        // Wait... / NO NET ID and generates nothing at all.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  03");                  // some other net is current
        SpinTo(vm, 0);

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NET  00");
        Transport.InjectLine("Wait...");
        Transport.InjectLine("NO NET ID");

        Assert.Equal("No net ID — program a net ID first", vm.StatusText);
        Assert.Equal(HopViewModel.NoNetIdStatus, vm.StatusText);
        Assert.True(vm.HasStatusText);
    }

    [Fact]
    public void StatusText_ClearsOnTheNextTrigger_NotOnTheNextAnything()
    {
        // Section 7's clearing rule, rendered. The pane must not keep the last
        // attempt's answer on screen once a new attempt is on the wire - and
        // must not drop it merely because some unrelated line arrived.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  03");
        SpinTo(vm, 0);

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NO NET ID");
        Assert.True(vm.HasStatusText);

        // An unrelated report is NOT a trigger.
        Transport.InjectLine("Hopnum 0041");
        Assert.True(vm.HasStatusText);

        // The next select is.
        Transport.InjectLine("NET  05");
        SpinTo(vm, 0);
        vm.SelectPickedNetCommand.Execute(null);

        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasStatusText);
    }

    [Fact]
    public void StatusText_FollowsARefusalRaisedByTheOTHER_HopSurfaceConsumer()
    {
        // The whole reason section 7 put the machine on the surface: the
        // settings pane's hopset write and this pane's select share ONE
        // surface, so a refusal drawn by a write the operator made on the OTHER
        // pane still reaches this pane's status line - with no cross-VM
        // plumbing.
        var surface = new HopSurface(Radio);
        var vm = new HopViewModel(surface, Session, _time);
        ConnectReady();
        Transport.InjectLine("HOP>");

        surface.ProgramNetId(3, "12345678");        // the settings pane's trigger
        Transport.InjectLine("NO NET ID");

        Assert.Equal(HopViewModel.NoNetIdStatus, vm.StatusText);
    }

    // ---- The net info view --------------------------------------------------

    [Fact]
    public void NetInfoView_ReadsTheDASH_ForANetNobodyHasReported()
    {
        // State 1 of three, per FIELD: no line has covered this net, so the ID
        // and the type are unheard - never a default, and never the radio's
        // "unprogrammed" claim, which is a POSITIVE report the radio has not
        // made.
        var vm = HopReadyVm();

        Assert.Equal("00 · — · —", vm.PickedNetInfoText);
        Assert.Equal("—", vm.PickedNetValueHeading);
        Assert.Equal("—", vm.PickedNetValueText);
    }

    [Fact]
    public void NetInfoView_RendersTheTHIRD_State_TheRadiosOwnXForm()
    {
        // State 3: the radio REPORTED this net unprogrammed. Section 7 pins the
        // X-form verbatim - and pins that the wipe's Hoptype WB does NOT count
        // as a confirmed type, so line 2 stays in the no-type state rather than
        // offering band edges for a net that has nothing.
        var vm = HopReadyVm();
        SpinTo(vm, 5);
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");

        Assert.Equal("05 · XXXXXXXX · WB", vm.PickedNetInfoText);
        Assert.Equal("—", vm.PickedNetValueHeading);
        Assert.Equal("—", vm.PickedNetValueText);
    }

    [Fact]
    public void NetInfoView_Narrowband_HeadsTheCenter()
    {
        var vm = HopReadyVm();
        SpinTo(vm, 3);
        InjectProgrammedNet3();

        Assert.Equal("03 · 22334455 · NB", vm.PickedNetInfoText);
        Assert.Equal("Center (MHz)", vm.PickedNetValueHeading);
        Assert.Equal("11.565", vm.PickedNetValueText);
    }

    [Fact]
    public void NetInfoView_Wideband_HeadsTheBandEdges()
    {
        var vm = HopReadyVm();
        SpinTo(vm, 4);
        Transport.InjectLine("NETID    04  24680135");
        Transport.InjectLine("Hoptype 04 WB  ");
        Transport.InjectLine("Hopset 04  02000  08000");

        Assert.Equal("04 · 24680135 · WB", vm.PickedNetInfoText);
        Assert.Equal("Low–High (MHz)", vm.PickedNetValueHeading);
        Assert.Equal("2.000–8.000", vm.PickedNetValueText);
    }

    [Fact]
    public void NetInfoView_List_CountsTheStoredFrequencies_AndSaysNothingUntilTheyLand()
    {
        // Section 7 adds NO read tier: the only read behind this view is the
        // per-pick `DIS n` the pane already sends, and no captured DIS answer
        // carries a hoplist. So a LIST net whose HOPLIST has not landed is
        // UNKNOWN, and an unknown count renders the dash - never "0 stored",
        // which would be the app inventing a fact (invariant 6).
        var vm = HopReadyVm();
        SpinTo(vm, 6);
        Transport.InjectLine("NETID    06  13579246");
        Transport.InjectLine("Hoptype 06 LIST");

        Assert.Equal("06 · 13579246 · LIST", vm.PickedNetInfoText);
        Assert.Equal("Frequencies", vm.PickedNetValueHeading);
        Assert.Equal("—", vm.PickedNetValueText);

        Transport.InjectLine("HOPLIST 06   11010  11015  11020");
        Assert.Equal("3 stored", vm.PickedNetValueText);
    }

    [Fact]
    public void NetInfoView_FollowsThePICKER_NotTheRadiosCurrentNet()
    {
        // The positional contract's other half: the stack sits in the SELECT
        // frame because it describes what the operator is about to select. If
        // it tracked the current net it would be a third copy of the R1 row.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("NET  00");                  // net 0 is CURRENT
        InjectProgrammedNet3();
        SpinTo(vm, 3);                                    // ...the picker is on 3

        Assert.Equal("NET 0", vm.ActiveNetText);          // R1 row: the radio's
        Assert.Equal("03 · 22334455 · NB", vm.PickedNetInfoText);
    }

    [Fact]
    public void NetInfoView_AndTheSettingsNetListRow_RenderOneMirrorStateIDENTICALLY()
    {
        // The shared-vocabulary contract: line 1's ID and type come from the
        // SAME HopNetDisplay projection the settings net-list rows read, so one
        // mirror state cannot render two ways across the two panes.
        var vm = HopReadyVm();
        var settings = new HopSettingsViewModel(
            new HopSurface(Radio), Session, new FakeConfirmationPrompt());
        SpinTo(vm, 4);
        Transport.InjectLine("NETID    04  24680135");
        Transport.InjectLine("Hoptype 04 WB  ");
        Transport.InjectLine("Hopset 04  02000  08000");

        var row = settings.Rows[4];
        Assert.Equal($"04 · {row.NetIdText} · {row.TypeText}", vm.PickedNetInfoText);
    }

    // ====================================================================
    // CLONE ROUND 12 §9 B1 — the COMBINED one-SH pin.
    //
    // Round 12 gave Core a tune-terminal re-poll (a coupler tune UNCONFIRMS
    // the keyline, so the RX chip never came back on its own). §9 B1 left the
    // arbitration to the implementer and said the one-SH pin decides it — and
    // the first attempt failed exactly here: Core armed on EVERY tune terminal
    // and fired at the next SSB prompt, ON TOP OF the `SH` this view model had
    // already sent at the HOP prompt. Two re-reads, one tune.
    //
    // The tests below are the arbitration's evidence, and they are deliberately
    // COMBINED — the Core-only pin in Round12LockoutStoreTests cannot see this
    // view model's send, and the pin above cannot see Core's flag.
    // ====================================================================

    [Fact]
    public void AStandaloneHopRetune_StillGetsExactlyOneRePoll_AndTheKeylineComesBack()
    {
        // THE ISOLATING PIN, cut TWICE by the audits and now pinning the
        // RECOVERY rather than an omission.
        //
        // A coupler retune terminating at a HOP prompt with NO preceding net
        // SELECTION: this view model's post-select `SH` does not exist here
        // (there is no select window), so if Core does not re-poll, NOTHING
        // does — and the keyline the tune unconfirmed stays blank until some
        // unrelated read happens by. Round 2 proved the mode-conditional arm
        // did exactly that. Under true coalescing the arm is unconditional and
        // fires ONCE at the next SSB prompt.
        var vm = HopReadyVm();
        Transport.InjectLine("NET  00");                   // learn the net; no later change
        Transport.InjectLine("SSB>");                      // drain first-sight re-polls
        Transport.InjectLine("HOP>");
        Transport.InjectLine("KEY OFF ");
        Assert.True(Radio.State.Keyline.IsConfirmed);
        Transport.ClearSent();

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.False(Radio.State.Keyline.IsConfirmed);     // the tune says nothing about it
        Assert.Empty(Transport.SentLines);                 // queued for the SSB prompt

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines);         // EXACTLY ONE, and it exists

        // …and the re-read is what puts the chip back: the block's own KEY line
        // re-confirms the keyline, which is the whole point of the repair.
        Transport.InjectLine("KEY OFF ");
        Assert.True(Radio.State.Keyline.IsConfirmed);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AHopSelectFlow_SendsOneShAtTheHopPrompt_AndReReadsTheChannelDomainAtTheNextSsb()
    {
        // THE COMBINED PIN, both actors live — RE-BASED by the clone round-12
        // P4 FLAG-SPLIT amendment (plan §6 P4, 2026-08-19).
        //
        // Round 1's finding stays dead: the view model's post-select `SH` at
        // the HOP prompt is still the ONLY re-read in the tune's window, and
        // Core adds nothing there. What CHANGED is what happens at the next SSB
        // prompt. P1's any-SH-satisfies rule let that HOP-prompt `SH` dissolve
        // the WHOLE pending flag — but the HOP block carries the KEYLINE and
        // NOT the SSB channel domain, so a hop-net select (which silently moves
        // the SSB channel, probe R9b) left the channel values unconfirmed-but-
        // honest instead of re-read. That consequence was RECORDED in
        // Prc138Radio.SatisfyPendingRePoll and deferred as a plan amendment;
        // P4 splits the flag by domain and this is the restored behaviour.
        //
        // So: ONE `SH` for the tune half, at the HOP prompt, exactly as before —
        // and ONE more at the next SSB prompt for the channel domain nothing
        // else can re-read. Two reads, two different questions.
        var vm = HopReadyVm();
        InjectProgrammedNet0();
        Transport.InjectLine("Hopnum 0041");
        Transport.InjectLine("NET  01");
        Transport.InjectLine("SSB>");                      // drain row (c) for net 01
        Transport.InjectLine("HOP>");
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);
        Transport.InjectLine("NET  00");
        Transport.InjectLine("Generating Hopset...");
        Transport.InjectLine(" TUNE COMPLETE  ");

        Assert.Equal(["NET 0", "SH"], Transport.SentLines);
        Assert.False(Radio.State.OperatingChannel.IsConfirmed);   // the select staled it

        Transport.InjectLine("SSB>");
        Assert.Equal(["NET 0", "SH", "SH"], Transport.SentLines);

        // ANTI-VACUITY for the split: the second `SH` is the CHANNEL half, not
        // a second copy of the tune's. Another SSB prompt adds nothing, and the
        // block that comes back is what re-confirms the channel.
        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Assert.Empty(Transport.SentLines);
        Transport.InjectLine("CHAN 03");
        Assert.True(Radio.State.OperatingChannel.IsConfirmed);
    }

    [Fact]
    public void AnSsbTuneTerminal_StillGetsItsCoreRePoll()
    {
        // The other half: the arbitration must not have simply deleted the
        // repair. A tune that terminates in an SSB context — where no view
        // model sends anything — still re-reads, which is what puts the RX chip
        // back.
        var vm = HopReadyVm();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Transport.InjectLine("SSB>");

        Assert.Equal(["SH"], Transport.SentLines);
        Assert.NotNull(vm);
    }
}
