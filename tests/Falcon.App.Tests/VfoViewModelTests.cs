using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The VFO under the §2.4 constitution and the GUI-rejigger F1/F2/F3a/F6
/// rules: a chevron tap sends an ABSOLUTE frequency and the displayed digit
/// does NOT move until the radio answers; unconfirmed digits render "—"
/// (never a default); STEP comes only from the radio; repeat-fire inputs are
/// rate-limited (dropped, never queued); keyboard arming follows the old
/// VfoKnob contract re-anchored on the readout digits; the F2 split override
/// legs are each pinned (arm/disarm/merge/channel-change reset); and every
/// frequency edit is 00-gated (confirmed CH 00 only — unconfirmed counts as
/// not 00).
/// </summary>
public class VfoViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();

    private VfoViewModel Vm()
        => new(new SsbSurface(Radio), new ChannelSurface(Radio), Session, _time);

    /// <summary>Radio confirmed in SSB on CH 00 at 01600000 RX=TX (verbatim
    /// SH-shape lines). CHAN 00 is required: the channel-stored six
    /// (frequency among them) are editable only on a confirmed CH 00.</summary>
    private void ReportSsbBaseline()
    {
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 01600000");
        Transport.InjectLine("TxFr 01600000");
        Transport.ClearSent();
    }

    private void AdvancePastRateLimit()
        => _time.Now += VfoViewModel.RepeatInterval + TimeSpan.FromMilliseconds(5);

    // ---- display state ----------------------------------------------------

    [Fact]
    public void DigitChevrons_CarryPlaceBearingDescriptions()
    {
        // Stage 8 audit N1: a screen reader must announce WHICH digit a
        // chevron bumps ("10 MHz digit up"), not just "up".
        var vm = Vm();

        Assert.Equal("10 MHz digit up", vm.RxDigits[0].UpDescription);
        Assert.Equal("10 MHz digit down", vm.RxDigits[0].DownDescription);
        Assert.Equal("1 kHz digit up", vm.RxDigits[4].UpDescription);
        Assert.Equal("1 Hz digit down", vm.RxDigits[7].DownDescription);
        Assert.Equal("TX 100 kHz digit up", vm.TxDigits[2].UpDescription);
    }

    [Fact]
    public void UnconfirmedFrequency_RendersDashes_AndChevronsAreDead()
    {
        var vm = Vm();
        ConnectReady();

        Assert.All(vm.RxDigits, d => Assert.Equal("—", d.Text));
        Assert.All(vm.RxDigits, d => Assert.False(d.CanBump));

        vm.RxDigits[3].UpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ProgrammaticStateWrite_SendsNoCommand()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 01600000");
        Transport.InjectLine("TxFr 01600000");
        Transport.InjectLine("Step 00001000");

        Assert.Equal("01600000", string.Concat(vm.RxDigits.Select(d => d.Text)));
        Assert.Equal("1 kHz", vm.StepText);
        Assert.Empty(Transport.SentLines);
    }

    // ---- chevrons: absolute sends, no optimism ------------------------------

    [Fact]
    public void ChevronTap_SendsAbsoluteFr_DisplayUnchangedUntilAnswer()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        vm.RxDigits[2].UpCommand.Execute(null);   // 01600000 + 100000

        Assert.Equal(["FR 01700000"], Transport.SentLines);
        // NO optimistic update: the digit still shows the confirmed value.
        Assert.Equal("01600000", string.Concat(vm.RxDigits.Select(d => d.Text)));

        // The answer lines are the read-back — now the display moves.
        Transport.InjectLine("RxFr 01700000");
        Transport.InjectLine("TxFr 01700000");
        Assert.Equal("01700000", string.Concat(vm.RxDigits.Select(d => d.Text)));
    }

    [Fact]
    public void ChevronBeyondBandEdge_SendsNothing()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();   // 01600000

        vm.RxDigits[0].DownCommand.Execute(null);   // -10 MHz -> below 1.6 MHz
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>F5 (plan-clone-field-round2.md, D3) — the VFO's ceiling is
    /// <see cref="Falcon.Core.Protocol.Wire.MaxFrequencyHz"/> and nothing else.
    /// Probe P2 (bench/transcripts/p2-freq-range-20260821-175802.jsonl) put the
    /// real ceiling at 59 999 999 Hz; this VM carried its own 29 999 999 copy,
    /// so a chevron above 30 MHz used to send NOTHING on a radio that would
    /// have taken it. Both halves are pinned: what now goes out, and what is
    /// still refused one Hz-decade past the real edge.</summary>
    [Fact]
    public void ChevronAboveTheOldThirtyMegahertzCeiling_NowSends_AndStopsAtTheProbedEdge()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 29999999");
        Transport.InjectLine("TxFr 29999999");
        Transport.ClearSent();

        vm.RxDigits[1].UpCommand.Execute(null);          // +1 MHz -> 30 999 999
        Assert.Equal(["FR 30999999"], Transport.SentLines);

        // …and the real edge still stops it: 51.5 MHz + 10 MHz is 61.5 MHz.
        Transport.ClearSent();
        Transport.InjectLine("RxFr 51500000");
        Transport.InjectLine("TxFr 51500000");
        Transport.ClearSent();
        AdvancePastRateLimit();
        vm.RxDigits[0].UpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void HeldChevron_RateLimited_DropsRepeats_NeverQueues()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        vm.RxDigits[4].UpCommand.Execute(null);    // fires
        vm.RxDigits[4].UpCommand.Execute(null);    // within 125 ms — dropped
        vm.RxDigits[4].UpCommand.Execute(null);    // dropped
        Assert.Single(Transport.SentLines);

        AdvancePastRateLimit();
        vm.RxDigits[4].UpCommand.Execute(null);    // new interval — fires
        Assert.Equal(2, Transport.SentLines.Count);

        // Both sends were computed from the SAME confirmed value (no answer
        // arrived): absolute command, not an accumulated queue.
        Assert.Equal(["FR 01601000", "FR 01601000"], Transport.SentLines);
    }

    // ---- F6: the 00-gate on frequency edits -----------------------------------

    [Fact]
    public void UnconfirmedChannel_TreatedAsNotZero_ChevronsDead_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("RxFr 01600000");     // freq confirmed…
        Transport.InjectLine("TxFr 01600000");     // …but channel NOT reported
        Transport.ClearSent();

        Assert.False(vm.AreSsbControlsEnabled);    // conservative: not 00
        Assert.All(vm.RxDigits, d => Assert.False(d.CanBump));

        vm.RxDigits[4].UpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ChannelNotZero_FreqControlsGreyed_ChannelZeroRestores()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                       // CH 00 — editable
        Assert.True(vm.RxDigits[4].CanBump);

        Transport.InjectLine("CHAN 05");           // confirmed non-00 channel
        Assert.False(vm.AreSsbControlsEnabled);
        Assert.Contains("CH 00", vm.SsbDisabledReason);
        Assert.All(vm.RxDigits, d => Assert.False(d.CanBump));
        Assert.False(vm.ToggleSplitCommand.CanExecute(null));
        Assert.False(vm.IncrementCommand.CanExecute(null));

        vm.RxDigits[4].UpCommand.Execute(null);
        vm.ToggleSplitCommand.Execute(null);
        vm.IncrementCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 00");           // back on 00 — editable again
        Assert.True(vm.AreSsbControlsEnabled);
        AdvancePastRateLimit();
        vm.RxDigits[4].UpCommand.Execute(null);
        Assert.Equal(["FR 01601000"], Transport.SentLines);
    }

    // ---- F2 split: every leg -------------------------------------------------

    [Fact]
    public void SplitPress_NoRadioSplit_ArmsOverride_SendsNothing_TxChevronSendsTxf()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        Assert.False(vm.IsSplit);
        Assert.False(vm.TxDigits[2].CanBump);      // TX controls greyed un-split
        vm.TxDigits[2].UpCommand.Execute(null);    // dead TX chevron sends nothing
        Assert.Empty(Transport.SentLines);

        vm.ToggleSplitCommand.Execute(null);       // ARM: view state only
        Assert.True(vm.IsSplit);
        Assert.Empty(Transport.SentLines);

        // First TX chevron tap: TXF computed from the confirmed TxFr
        // (== RxFr while not yet split — TX digits are confirmed).
        Assert.True(vm.TxDigits[2].CanBump);
        vm.TxDigits[2].UpCommand.Execute(null);
        Assert.Equal(["TXF 01700000"], Transport.SentLines);

        Transport.ClearSent();
        AdvancePastRateLimit();
        vm.RxDigits[2].UpCommand.Execute(null);    // split: RX row sends RXF, not FR
        Assert.Equal(["RXF 01700000"], Transport.SentLines);
    }

    [Fact]
    public void SplitPress_WhileOverrideArmed_NoRadioSplit_Disarms_SendsNothing()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        vm.ToggleSplitCommand.Execute(null);       // arm
        Assert.True(vm.IsSplit);
        vm.ToggleSplitCommand.Execute(null);       // disarm — sends nothing
        Assert.False(vm.IsSplit);
        Assert.False(vm.TxDigits[2].CanBump);      // TX controls grey again
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void RadioReportedSplit_DisplaysSplit_PressMergesViaFr_UnhighlightsOnAnswer()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");
        Transport.ClearSent();

        Assert.True(vm.IsSplit);                   // the radio says RX != TX
        Assert.True(vm.TxDigits[2].CanBump);       // TX controls enabled

        vm.ToggleSplitCommand.Execute(null);       // the merge
        Assert.Equal(["FR 04123000"], Transport.SentLines);
        // Still split until the radio's answer collapses it — no optimism.
        Assert.True(vm.IsSplit);

        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 04123000");
        Assert.False(vm.IsSplit);
    }

    [Fact]
    public void SplitOverride_ClearsOnConfirmedChannelChange()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                       // CH 00

        vm.ToggleSplitCommand.Execute(null);       // arm the override
        Assert.True(vm.IsSplit);

        Transport.InjectLine("CHAN 07");           // confirmed channel CHANGE
        Assert.False(vm.IsSplit);                  // override cleared (not just gated)

        Transport.InjectLine("CHAN 00");           // back on 00: still cleared —
        Assert.False(vm.IsSplit);                  // the reset was real, not a grey
        Assert.Empty(Transport.SentLines);         // nothing sent throughout
    }

    [Fact]
    public void SplitOverride_ClearsOnReconnect_EqualFreqsRenderUnsplit()
    {
        // F2a (owner ruling): the override does not survive a session drop.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                       // CH 00, RX=TX

        vm.ToggleSplitCommand.Execute(null);       // arm the override
        Assert.True(vm.IsSplit);

        Session.Close();                           // session drop
        ConnectReady();                            // reconnect (fresh session)
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 01600000");     // freqs confirm EQUAL:
        Transport.InjectLine("TxFr 01600000");     // un-split renders

        Assert.False(vm.IsSplit);                  // button un-highlighted
        Assert.False(vm.TxDigits[2].CanBump);      // TX controls greyed
        Assert.Empty(Transport.SentLines);         // clearing sent nothing
    }

    [Fact]
    public void RadioSplit_OnReconnect_DisplaysSplit_OverrideIrrelevant()
    {
        // F2a companion: a reconnect that REPORTS a split renders as split —
        // radio-split display governs regardless of the cleared override.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();
        vm.ToggleSplitCommand.Execute(null);       // armed in session 1

        Session.Close();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");     // radio-reported split

        Assert.True(vm.IsSplit);
        Assert.True(vm.TxDigits[2].CanBump);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SplitOverride_SurvivesChannelRereport_SameValue()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                       // CH 00

        vm.ToggleSplitCommand.Execute(null);       // arm
        Transport.InjectLine("CHAN 00");           // re-report, NOT a change
        Assert.True(vm.IsSplit);                   // override stands
        Assert.Empty(Transport.SentLines);
    }

    // ---- R12: the split-flash fix, pinned as TRANSITION HISTORIES ---------------
    // Owner ruling R12 (round 11 §8). The defect was invisible to every
    // assertion above, because every one of them samples IsSplit AFTER the
    // whole answer block has landed — and the flash is a state the display
    // passes THROUGH. `FR` answers a separate `RxFr` line and a separate `TxFr`
    // line (docs/protocol.md), each committed and raised on its own, so between
    // the two raises RX held the new frequency and TX still held the old one:
    // IsSplit went true and back on EVERY frequency change, and the TX row
    // highlighted for a frame.
    //
    // The oracle is therefore the SEQUENCE of IsSplit changes, not its final
    // value: a "no-flicker" pin has to be able to see a transition that undoes
    // itself. Each history below is the plan's own (a)-(d).

    /// <summary>Every value IsSplit CHANGED TO while the script ran, in order.
    /// An empty history means the display never moved at all.</summary>
    private static List<bool> SplitHistory(VfoViewModel vm, Action script)
    {
        var history = new List<bool>();
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VfoViewModel.IsSplit)) history.Add(vm.IsSplit);
        }

        vm.PropertyChanged += OnChanged;
        try { script(); } finally { vm.PropertyChanged -= OnChanged; }
        return history;
    }

    [Fact]
    public void SplitHistoryOracle_SeesATransitionThatUndoesItself()
    {
        // Anti-vacuity for the four pins below: prove the recorder catches a
        // there-and-back move, which is exactly what a final-value assertion
        // cannot see. Driven through the F2 override, whose arm/disarm is a
        // real IsSplit round trip that sends nothing.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        var history = SplitHistory(vm, () =>
        {
            vm.ToggleSplitCommand.Execute(null);       // arm
            vm.ToggleSplitCommand.Execute(null);       // disarm
        });

        Assert.Equal([true, false], history);
        Assert.False(vm.IsSplit);                      // …and the FINAL value alone
    }                                                  //    would have said nothing

    [Fact]
    public void R12a_EqualFrSequence_MakesNoSplitTransitionAtAll()
    {
        // (a) The defect itself: three ordinary tunes through the chevrons,
        // each sending FR and each answering RxFr then TxFr on separate lines.
        // Before the fix this history was [true,false,true,false,true,false].
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                           // RX = TX = 01600000

        var history = SplitHistory(vm, () =>
        {
            foreach (var hz in new[] { "01601000", "01602000", "01603000" })
            {
                AdvancePastRateLimit();
                vm.RxDigits[4].UpCommand.Execute(null);    // FR — a two-line answer
                Transport.InjectLine($"RxFr {hz}");        // …TX is momentarily stale
                Transport.InjectLine($"TxFr {hz}");
            }
        });

        Assert.Empty(history);
        Assert.False(vm.IsSplit);
        Assert.Equal(["FR 01601000", "FR 01602000", "FR 01603000"], Transport.SentLines);
    }

    [Fact]
    public void R12a_IncAndDecAnswerLikeFr_AndAreHeldToo()
    {
        // protocol.md: "INC/DEC answer like FR: RxFr <new> + TxFr <new>". They
        // open the same window, so they must not flash either — and the window
        // is opened at the SEND site, so a builder that forgets one is a
        // regression this catches.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();
        Transport.InjectLine("Step 00001000");         // STEP confirmed: INC/DEC live

        var history = SplitHistory(vm, () =>
        {
            vm.IncrementCommand.Execute(null);
            Transport.InjectLine("RxFr 01601000");
            Transport.InjectLine("TxFr 01601000");

            AdvancePastRateLimit();
            vm.DecrementCommand.Execute(null);
            Transport.InjectLine("RxFr 01600000");
            Transport.InjectLine("TxFr 01600000");
        });

        Assert.Empty(history);
        Assert.False(vm.IsSplit);
    }

    [Fact]
    public void R12a_TheHeldTransition_SurvivesAnUnrelatedRaiseBetweenTheTwoLines()
    {
        // The hold is not "skip one event": it lasts until the TX half of the
        // answer arrives, so a CHAN line (or any other watched property)
        // landing between RxFr and TxFr must not release it early and
        // re-introduce the flash.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        var history = SplitHistory(vm, () =>
        {
            AdvancePastRateLimit();
            vm.RxDigits[4].UpCommand.Execute(null);    // FR 01601000
            Transport.InjectLine("RxFr 01601000");
            Transport.InjectLine("CHAN 00");           // unrelated raise, mid-answer
            Transport.InjectLine("TxFr 01601000");
        });

        Assert.Empty(history);
        Assert.False(vm.IsSplit);
    }

    [Fact]
    public void R12b_RealSplitThenMerge_IsOneTransitionEachWay_NoBounce()
    {
        // (b) The genuine article must still work, and must cost exactly one
        // transition in each direction: entering a real split, then the merge
        // press whose FR answers RxFr (unchanged) + TxFr (changed).
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        var history = SplitHistory(vm, () =>
        {
            Transport.InjectLine("RxFr 04123000");
            Transport.InjectLine("TxFr 05000000");     // a REAL split
            vm.ToggleSplitCommand.Execute(null);       // the merge (FR 04123000)
            Transport.InjectLine("RxFr 04123000");
            Transport.InjectLine("TxFr 04123000");
        });

        Assert.Equal([true, false], history);
        Assert.False(vm.IsSplit);
        Assert.Equal(["FR 04123000"], Transport.SentLines);
    }

    [Fact]
    public void R12c_ArmedOverrideRxfTxfSequence_NeverGoesFalseEarly()
    {
        // (c) With the F2 override armed the display is ALREADY split, so there
        // is no transition to hold — and the RXF/TXF answers that follow, each
        // a single-frequency line, must not knock it down between them.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        var history = SplitHistory(vm, () =>
        {
            vm.ToggleSplitCommand.Execute(null);       // arm (sends nothing)

            vm.TxDigits[4].UpCommand.Execute(null);    // TXF 01601000
            Transport.InjectLine("TxFr 01601000");     // now genuinely split

            AdvancePastRateLimit();
            vm.RxDigits[4].UpCommand.Execute(null);    // RXF 01601000 (split: RXF, not FR)
            Transport.InjectLine("RxFr 01601000");     // …back to equal, override holds it
        });

        Assert.Equal([true], history);                 // ONE change: the arm
        Assert.True(vm.IsSplit);
        Assert.Equal(["TXF 01601000", "RXF 01601000"], Transport.SentLines);
    }

    [Fact]
    public void R12d_ReconnectWithDifferingRxTx_EndsSplit_InOneTransition()
    {
        // (d) The hold must never swallow a real split. A reconnect reports RX
        // and TX on separate lines too, and the display has to end true.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        var history = SplitHistory(vm, () =>
        {
            Session.Close();
            ConnectReady();
            Transport.InjectLine("SSB>");
            Transport.InjectLine("CHAN 00");
            Transport.InjectLine("RxFr 04123000");
            Transport.InjectLine("TxFr 05000000");
        });

        Assert.Equal([true], history);
        Assert.True(vm.IsSplit);
        Assert.True(vm.TxDigits[2].CanBump);
    }

    // ---- Audit round 1, MAJOR-1: the two ways the first hold shape failed ----

    [Fact]
    public void R12_ARealSplitFromAnRxOnlyAnswer_SurfacesImmediately()
    {
        // AUDITOR REPLAY (a). An `RXF` typed at the Console answers ONE line and
        // never a TxFr, and the trailing prompt is the same mode — which Core
        // does not re-raise. The first hold shape therefore suppressed this
        // transition FOREVER and the UI hid a genuine split.
        //
        // The bound that fixes it: this ViewModel sent nothing, so no hold
        // window is open, so the line that proves the split is the line that
        // shows it. Held ONLY for the two-line answers this VM asked for.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                           // RX = TX, display un-split
        Assert.False(vm.IsSplit);

        Transport.InjectLine("RxFr 05000000");         // RXF's lone answer

        Assert.True(vm.IsSplit);                       // …and it is a REAL split
        Assert.True(vm.TxDigits[2].CanBump);           // TX controls live with it
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void R12_AnRxOnlyAnswerIsNotHeld_EvenWhileTheSameSessionTunesNormally()
    {
        // The window must close on the answer that opened it, not linger to
        // swallow the NEXT unrelated RX-only line. Tune first (opening and
        // closing a window), then replay the console RXF.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        AdvancePastRateLimit();
        vm.RxDigits[4].UpCommand.Execute(null);        // FR 01601000
        Transport.InjectLine("RxFr 01601000");
        Transport.InjectLine("TxFr 01601000");         // window closed
        Assert.False(vm.IsSplit);

        Transport.InjectLine("RxFr 05000000");         // a console RXF afterwards
        Assert.True(vm.IsSplit);
    }

    [Fact]
    public void R12_SplitPressDuringAHold_Arms_ItDoesNotMerge()
    {
        // AUDITOR REPLAY (b). Inside a hold window the MIRROR says RX != TX
        // while the DISPLAY still says un-split. The first shape branched on
        // the mirror, so a press there sent `FR <rx>` — a merge — to an
        // operator looking at a non-split readout and pressing to ARM.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        AdvancePastRateLimit();
        vm.RxDigits[4].UpCommand.Execute(null);        // FR 01601000
        Transport.ClearSent();
        Transport.InjectLine("RxFr 01601000");         // the hold window is OPEN:
        Assert.False(vm.IsSplit);                      // mirror split, display not

        vm.ToggleSplitCommand.Execute(null);           // the press

        Assert.Empty(Transport.SentLines);             // ARM sends nothing…
        Assert.True(vm.IsSplit);                       // …and highlights

        // And the answer that closes the window leaves the ARM standing, which
        // is what the operator asked for: RX == TX again, split by override.
        Transport.InjectLine("TxFr 01601000");
        Assert.True(vm.IsSplit);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void R12_ASingleLineRxfSend_OpensNoHoldWindow_SoTheNextRealSplitStillSurfaces()
    {
        // AUDIT ROUND 2, MAJOR (test coverage). The hold window is opened at
        // the SEND SITES, which makes "which sends open one" a load-bearing
        // fact with no pin on it: the auditor added ExpectTwoLineAnswer() to
        // Send's IsSplit branch and all 1120 tests stayed green.
        //
        // Why that branch must NOT open one, and why RXF specifically is the
        // dangerous half: a window is closed by the TxFr that completes its
        // answer. TXF's own answer IS a TxFr, so a stray window there would
        // close itself immediately — but RXF answers RxFr and nothing else, so
        // a window opened by an RXF is STRANDED. It then survives to swallow
        // the next genuine RX-only split, which is exactly the unbounded hold
        // MAJOR-1 was fixed to make impossible.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();

        // A real radio split, one 1-kHz step wide, displayed as split.
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 04124000");
        Assert.True(vm.IsSplit);
        Transport.ClearSent();

        // The RXF leg: while split, an RX chevron sends RXF — a ONE-LINE
        // answer — and here it happens to land on TX, collapsing the split.
        AdvancePastRateLimit();
        vm.RxDigits[4].UpCommand.Execute(null);
        Assert.Equal(["RXF 04124000"], Transport.SentLines);    // the single-line path

        Transport.InjectLine("RxFr 04124000");                  // RX == TX again
        Assert.False(vm.IsSplit);                               // …and no window is left behind

        // The auditor's regression shape: a genuine RX-only split arriving
        // afterwards (a console RXF) must surface on the line that proves it.
        // With a stranded window it would be held instead — invisibly.
        Transport.InjectLine("RxFr 05000000");

        Assert.True(vm.IsSplit);
        Assert.True(vm.TxDigits[2].CanBump);
        Assert.Equal(["RXF 04124000"], Transport.SentLines);    // nothing else was sent
    }

    [Fact]
    public void R12_SplitPress_OnARealDisplayedSplit_StillMerges()
    {
        // The other side of MAJOR-1b's fix: switching ToggleSplit onto the
        // DISPLAYED split must not cost the merge leg. A displayed split still
        // merges, and its FR opens a window like any other two-line answer.
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");
        Transport.ClearSent();
        Assert.True(vm.IsSplit);

        vm.ToggleSplitCommand.Execute(null);
        Assert.Equal(["FR 04123000"], Transport.SentLines);
    }

    // ---- STEP: radio state only ------------------------------------------------

    [Fact]
    public void Step_UnreportedRendersDash_AndControlsDead()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.Equal("—", vm.StepText);
        Assert.False(vm.StepUpCommand.CanExecute(null));

        vm.StepUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void StepChange_ComputedFromConfirmedStep_AnswerMovesDisplay()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("Step 00001000");
        Transport.ClearSent();
        Assert.Equal("1 kHz", vm.StepText);

        vm.StepUpCommand.Execute(null);
        Assert.Equal(["STEP 00010000"], Transport.SentLines);
        Assert.Equal("1 kHz", vm.StepText);          // no optimism

        Transport.InjectLine("Step 00010000");
        Assert.Equal("10 kHz", vm.StepText);
    }

    [Fact]
    public void FreqControls_GreyedWithReason_OutsideSsb()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");    // radio confirmed in ALE
        Transport.ClearSent();

        Assert.False(vm.AreSsbControlsEnabled);
        Assert.Contains("SSB", vm.SsbDisabledReason);
        Assert.False(vm.IncrementCommand.CanExecute(null));
        Assert.False(vm.DecrementCommand.CanExecute(null));

        vm.IncrementCommand.Execute(null);
        vm.DecrementCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void IncDec_SendDocumentedForms_RateLimited()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        vm.IncrementCommand.Execute(null);
        vm.IncrementCommand.Execute(null);           // dropped
        AdvancePastRateLimit();
        vm.DecrementCommand.Execute(null);

        Assert.Equal(["INC", "DEC"], Transport.SentLines);
    }

    // ---- keyboard arming (VfoKnob contract, re-anchored on the digits) -----------

    [Fact]
    public void Unarmed_Keys_DoNothing()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        Assert.False(vm.HandleKey(VfoKey.Up));
        Assert.Empty(Transport.SentLines);
    }

    // ---- digit-cursor keyboard model (armed cue: green background) ----------

    [Fact]
    public void ArmDefaultsCursorTo1kHz_BothRows_NonSplit()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                     // CH00, RX=TX (non-split)

        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));
        Assert.All(vm.TxDigits, d => Assert.False(d.IsCursor));

        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.IsVfoArmed);

        // Default place = index 4 (1 kHz); non-split highlights BOTH rows.
        Assert.True(vm.RxDigits[4].IsCursor);
        Assert.True(vm.TxDigits[4].IsCursor);
        for (int i = 0; i < 8; i++)
            if (i != 4) { Assert.False(vm.RxDigits[i].IsCursor); Assert.False(vm.TxDigits[i].IsCursor); }
    }

    [Fact]
    public void NonSplit_MoveCursor_SendsNothing_HighlightsBothRows_ClampsAtEnds()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();
        vm.ToggleArmCommand.Execute(null);       // index 4

        Assert.True(vm.HandleKey(VfoKey.Right));  // -> index 5, sends nothing
        Assert.True(vm.RxDigits[5].IsCursor);
        Assert.True(vm.TxDigits[5].IsCursor);
        Assert.False(vm.RxDigits[4].IsCursor);
        Assert.Empty(Transport.SentLines);

        vm.HandleKey(VfoKey.Right);               // 6
        vm.HandleKey(VfoKey.Right);               // 7
        vm.HandleKey(VfoKey.Right);               // clamp at 7 (no wrap in non-split)
        Assert.True(vm.RxDigits[7].IsCursor);
        Assert.True(vm.TxDigits[7].IsCursor);

        for (int i = 0; i < 10; i++) vm.HandleKey(VfoKey.Left);   // clamp at 0
        Assert.True(vm.RxDigits[0].IsCursor);
        Assert.True(vm.TxDigits[0].IsCursor);
        Assert.Empty(Transport.SentLines);        // moving never transmits
    }

    [Fact]
    public void NonSplit_EditCursor_SendsFr_AtCursorPlace()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                      // 01600000
        vm.ToggleArmCommand.Execute(null);        // cursor index 4 (1 kHz)

        Assert.True(vm.HandleKey(VfoKey.Up));     // +1 kHz -> FR (both move)
        Assert.Equal(["FR 01601000"], Transport.SentLines);

        Transport.ClearSent();
        AdvancePastRateLimit();
        vm.HandleKey(VfoKey.Right);               // move to index 5 (100 Hz), sends nothing
        Assert.True(vm.HandleKey(VfoKey.Up));     // +100 Hz -> FR at the new place
        Assert.Equal(["FR 01600100"], Transport.SentLines);
    }

    [Fact]
    public void ArmedEdits_RateLimited_DropRepeats()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();                      // confirmed freq: BumpDigit needs it
        vm.ToggleArmCommand.Execute(null);

        vm.HandleKey(VfoKey.Up);                  // fires: FR at the 1 kHz place
        vm.HandleKey(VfoKey.Up);                  // held repeat — dropped
        vm.HandleKey(VfoKey.Up);                  // dropped

        Assert.Equal(["FR 01601000"], Transport.SentLines);
    }

    [Fact]
    public void Split_SingleRowHighlight_EditsPointedRow_RxfThenTxf()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");    // radio-reported split
        Transport.ClearSent();
        Assert.True(vm.IsSplit);

        vm.ToggleArmCommand.Execute(null);        // cursor index 4, RX row
        Assert.True(vm.RxDigits[4].IsCursor);
        Assert.All(vm.TxDigits, d => Assert.False(d.IsCursor));   // split: pointed row only

        Assert.True(vm.HandleKey(VfoKey.Up));     // edit RX -> RXF at 1 kHz place
        Assert.Equal(["RXF 04124000"], Transport.SentLines);
        Transport.ClearSent();
        AdvancePastRateLimit();

        // Ring LEFT off RX[0] -> TX[7]: index 4 ->0 (4 lefts), then one more wraps.
        for (int i = 0; i < 4; i++) vm.HandleKey(VfoKey.Left);
        Assert.True(vm.RxDigits[0].IsCursor);
        vm.HandleKey(VfoKey.Left);                // RX[0] -> TX[7]
        Assert.True(vm.TxDigits[7].IsCursor);
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));
        Assert.Empty(Transport.SentLines);        // moving sends nothing

        Assert.True(vm.HandleKey(VfoKey.Up));     // edit TX -> TXF at 1 Hz place
        Assert.Equal(["TXF 05000001"], Transport.SentLines);
    }

    [Fact]
    public void Split_MoveCursor_16PositionRing_AllFourCrossRowTransitions()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");
        Transport.ClearSent();
        Assert.True(vm.IsSplit);
        vm.ToggleArmCommand.Execute(null);        // RX[4]

        // RIGHT off RX[7] -> TX[0].
        vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right);
        Assert.True(vm.RxDigits[7].IsCursor);
        vm.HandleKey(VfoKey.Right);
        Assert.True(vm.TxDigits[0].IsCursor);

        // RIGHT off TX[7] -> RX[0].
        for (int i = 0; i < 7; i++) vm.HandleKey(VfoKey.Right);
        Assert.True(vm.TxDigits[7].IsCursor);
        vm.HandleKey(VfoKey.Right);
        Assert.True(vm.RxDigits[0].IsCursor);

        // LEFT off RX[0] -> TX[7].
        vm.HandleKey(VfoKey.Left);
        Assert.True(vm.TxDigits[7].IsCursor);

        // LEFT off TX[0] -> RX[7].
        for (int i = 0; i < 7; i++) vm.HandleKey(VfoKey.Left);
        Assert.True(vm.TxDigits[0].IsCursor);
        vm.HandleKey(VfoKey.Left);
        Assert.True(vm.RxDigits[7].IsCursor);

        Assert.Empty(Transport.SentLines);        // pure movement, no sends
    }

    [Fact]
    public void SplitMerge_WhileArmed_PullsTxCursorToRx_SameIndex()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");    // radio split
        Transport.ClearSent();
        vm.ToggleArmCommand.Execute(null);        // RX[4]

        // Walk onto TX[4]: RX[4]->RX[7] (3), ->TX[0] (1), ->TX[4] (4).
        vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right);
        vm.HandleKey(VfoKey.Right);
        vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right); vm.HandleKey(VfoKey.Right);
        Assert.True(vm.TxDigits[4].IsCursor);
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));

        // Merge: the radio reports RX == TX -> non-split. Cursor pulls back to
        // RX at the SAME index, and non-split highlights BOTH rows there.
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 04123000");
        Assert.False(vm.IsSplit);
        Assert.True(vm.RxDigits[4].IsCursor);
        Assert.True(vm.TxDigits[4].IsCursor);
        for (int i = 0; i < 8; i++)
            if (i != 4) { Assert.False(vm.RxDigits[i].IsCursor); Assert.False(vm.TxDigits[i].IsCursor); }

        // The merge must have reset the ROW (state), not merely repainted the
        // display: re-split WITHOUT moving the cursor, and it lands on RX[4] —
        // an edit sends RXF, not a stale TXF. (Pins the `if (!IsSplit)
        // _cursorOnTx = false` reset; without it the cursor resumes TX[4].)
        Transport.InjectLine("RxFr 04123000");
        Transport.InjectLine("TxFr 05000000");    // radio re-splits
        Assert.True(vm.IsSplit);
        Assert.True(vm.RxDigits[4].IsCursor);
        Assert.All(vm.TxDigits, d => Assert.False(d.IsCursor));

        Transport.ClearSent();
        AdvancePastRateLimit();
        Assert.True(vm.HandleKey(VfoKey.Up));      // edits RX -> RXF (not TXF)
        Assert.Equal(["RXF 04124000"], Transport.SentLines);
    }

    [Fact]
    public void Cursor_Clears_OnDisarm_LeaveSsb_ChannelChange_SessionDrop()
    {
        var vm = Vm();
        ConnectReady();
        ReportSsbBaseline();
        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.RxDigits[4].IsCursor);

        vm.Disarm();                              // disarm clears
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));
        Assert.All(vm.TxDigits, d => Assert.False(d.IsCursor));

        vm.ToggleArmCommand.Execute(null);        // re-arm, then leave SSB
        Assert.True(vm.RxDigits[4].IsCursor);
        Transport.InjectLine("ALE>");
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));

        Transport.InjectLine("SSB>");             // back on CH00, arm, then CHAN != 00
        Transport.InjectLine("CHAN 00");
        Transport.InjectLine("RxFr 01600000");
        Transport.InjectLine("TxFr 01600000");
        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.RxDigits[4].IsCursor);
        Transport.InjectLine("CHAN 05");
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));

        Transport.InjectLine("CHAN 00");          // arm again, then session drop
        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.RxDigits[4].IsCursor);
        Session.Close();
        Assert.All(vm.RxDigits, d => Assert.False(d.IsCursor));
        Assert.All(vm.TxDigits, d => Assert.False(d.IsCursor));
    }

    [Fact]
    public void LeavingSsb_AutoDisarms()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.IsVfoArmed);

        Transport.InjectLine("ALE>");                // mode left SSB
        Assert.False(vm.IsVfoArmed);
    }

    [Fact]
    public void ChannelLeaves00_AutoDisarms_AndArmRefusedOffZero()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        vm.ToggleArmCommand.Execute(null);
        Assert.True(vm.IsVfoArmed);

        Transport.InjectLine("CHAN 03");             // F6 gate closes
        Assert.False(vm.IsVfoArmed);

        vm.ToggleArmCommand.Execute(null);           // arming refused off 00
        Assert.False(vm.IsVfoArmed);
        Transport.ClearSent();
        Assert.False(vm.HandleKey(VfoKey.Up));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ExplicitDisarm_ForFocusLoss()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        vm.ToggleArmCommand.Execute(null);

        vm.Disarm();                                 // page: focus loss / deactivate
        Assert.False(vm.IsVfoArmed);
        Transport.ClearSent();
        Assert.False(vm.HandleKey(VfoKey.Up));
        Assert.Empty(Transport.SentLines);
    }
}
