using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

public class SpineStatusViewModelTests : SessionTestBase
{
    private SpineStatusViewModel Vm() => new(new StatusSurface(Radio), Session);

    [Fact]
    public void Unreported_ShowsDashes_NeverDefaults()
    {
        // Rejigger S7: there is NO "Idle" state — until a tune lifecycle
        // line has been seen this session the chip renders "—" (never a
        // default). This test dies if "Idle" (or any other placeholder
        // text) returns.
        var vm = Vm();
        ConnectReady();

        Assert.Equal("—", vm.KeylineText);
        Assert.False(vm.IsKeylineConfirmed);
        Assert.False(vm.IsTransmitting);
        Assert.Equal(TuneChipState.None, vm.TuneChip);
        Assert.Equal("—", vm.TuneChipText);
    }

    [Fact]
    public void Keyline_RxAndTx_FromReportedLines()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("KEY OFF ");     // verbatim SH-block shape
        Assert.Equal("RX", vm.KeylineText);
        Assert.False(vm.IsTransmitting);

        Transport.InjectLine("KEY ON");       // async keying report (B7)
        Assert.Equal("TX", vm.KeylineText);
        Assert.True(vm.IsTransmitting);
    }

    [Fact]
    public void TuneLifecycle_TuningThenComplete()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("KEY OFF ");

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.Equal(TuneChipState.Tuning, vm.TuneChip);
        Assert.True(vm.IsTuning);
        // CLONE ROUND 12 §9 B1 re-pin. This asserted "—" through round 11: the
        // tune lines carry no keyline report, so the MIRROR goes unconfirmed
        // and the chip blanked. Tuning TRANSMITS, and blanking the indicator
        // mid-transmission is the defect the bench reported. Display policy
        // now reads the confirmed tuning flag as TX; the mirror is unchanged.
        Assert.Equal("TX", vm.KeylineText);
        Assert.True(vm.IsTransmitting);

        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);
        Assert.False(vm.IsTuning);
    }

    [Fact]
    public void TuneMarginal_IsQualifierOnComplete()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine("TUNE MARGINAL");

        Assert.Equal(TuneChipState.CompleteMarginal, vm.TuneChip);
        Assert.Equal("Tune Marginal", vm.TuneChipText);
    }

    [Fact]
    public void Reconnect_ClearsTuneChipAndKeyline_BackToDash()
    {
        // S7 says "this session": ResetForConnect clears the tune/keyline
        // state SILENTLY (no events), so the VM must re-read on a phase
        // change — otherwise a fresh session keeps showing the previous
        // session's outcome (audit round 1, W2).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE ");
        Transport.InjectLine("KEY OFF ");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);
        Assert.Equal("RX", vm.KeylineText);

        Session.Close();
        ConnectReady();                     // fresh session, nothing reported

        Assert.Equal(TuneChipState.None, vm.TuneChip);
        Assert.Equal("—", vm.TuneChipText);
        Assert.Equal("—", vm.KeylineText);
        Assert.False(vm.IsKeylineConfirmed);
    }

    // ---- CLONE ROUND 12 §9 B1 — the display half ---------------------------

    /// <summary>The whole chip SEQUENCE across a tune, which is what the bench
    /// actually watched: RX → TX for the duration → back to unconfirmed the
    /// instant the tune terminal lands, and RX again only when a REAL keyline
    /// line arrives (P1's re-poll is what fetches one).
    /// <para>The last leg is the point of the design: the display policy is
    /// scoped to the tuning flag and expires with it. If it ever leaked into
    /// the mirror, the chip would sit on a fabricated TX/RX after the
    /// tune.</para></summary>
    [Fact]
    public void TheChipSequenceAcrossATune_IsRxThenTxThenUnconfirmed_B1()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("KEY OFF ");
        Assert.Equal("RX", vm.KeylineText);

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.Equal("TX", vm.KeylineText);
        Assert.True(vm.IsTransmitting);
        Assert.True(vm.IsKeylineConfirmed);          // the chip has something to say

        Transport.InjectLine(" TUNE COMPLETE  ");
        // The tune is over and NO keyline line has landed: honest "—", not a
        // fabricated RX. Display policy expires with the flag it read.
        Assert.Equal("—", vm.KeylineText);
        Assert.False(vm.IsTransmitting);
        Assert.False(vm.IsKeylineConfirmed);

        Transport.InjectLine("KEY OFF ");             // the re-poll's answer
        Assert.Equal("RX", vm.KeylineText);
    }

    /// <summary>The mirror is NOT fabricated — asserted against Core, not just
    /// stated in a comment. Mid-tune the chip reads TX while
    /// <c>StatusSurface.Keyline</c> is still UNCONFIRMED; a fix that "simply"
    /// wrote a KEY ON into RadioState would pass every display assertion above
    /// and fail here.</summary>
    [Fact]
    public void TheMidTuneTx_IsDisplayPolicyOnly_TheKeylineMirrorStaysUnconfirmed_B1()
    {
        var status = new StatusSurface(Radio);
        var vm = new SpineStatusViewModel(status, Session);
        ConnectReady();

        Transport.InjectLine("KEY OFF ");
        Assert.True(status.Keyline.IsConfirmed);

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.Equal("TX", vm.KeylineText);           // the display says TX…
        Assert.False(status.Keyline.IsConfirmed);     // …and Core says nothing
        Assert.Empty(Transport.SentLines);            // no display policy on the wire
    }

    /// <summary>Every tune TERMINAL ends the TX display, not just COMPLETE —
    /// the three outcomes share one flag, and a fault must not leave the chip
    /// claiming transmission.</summary>
    [Theory]
    [InlineData("TUNE COMPLETE")]
    [InlineData("TUNE MARGINAL")]
    [InlineData("TUNE FAULT")]
    public void EveryTuneTerminal_EndsTheTxDisplay_B1(string terminal)
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.True(vm.IsTransmitting);

        Transport.InjectLine(terminal);
        Assert.False(vm.IsTransmitting);
        Assert.Equal("—", vm.KeylineText);
    }

    [Fact]
    public void TuneFault_IsANormalDisplayedOutcome()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine("TUNE FAULT");

        Assert.Equal(TuneChipState.Fault, vm.TuneChip);
        Assert.Equal("Tune Fail", vm.TuneChipText);
        // FAULT is display state, not an error flow: nothing was sent, and a
        // fresh tune simply overwrites the chip.
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.Equal(TuneChipState.Tuning, vm.TuneChip);
    }

    // ---- ROUND 15 N3, RUNG 1 (plan §3.6): Core → the SPINE chip, IN HOP -----
    //
    // Every tune test above runs with an UNCONFIRMED mode, so none of them can
    // see a HOP-specific drop. These three replay captured HOP entries through
    // the real parser, mirror, StatusSurface and VM with the mode CONFIRMED —
    // the first rung of the reproduction ladder for the owner's report that
    // the spine chip stayed "—" through a HOP tune (Q3 = H-B). DIAGNOSTIC:
    // they pin what the code DOES, and a red rung is an escalation, not a
    // repair (D5/I-10).

    /// <summary>The P6b `NET 1` window, verbatim (bench 2026-08-21,
    /// `p6b-hop-net-switch-tune-20260821-202625.jsonl`, `T1-net-1`): the app's
    /// framer splits the capture's `HOP&gt; Wait...` into a bare prompt and
    /// the busy line, so this is the exact line sequence the app sees.</summary>
    private static readonly string[] P6bNetOneWindow =
    [
        "NET  01", "HOP>", "Wait...", "Generating Hopset...",
        " TUNING COUPLER ", " TUNE COMPLETE  ", "HOP>",
    ];

    /// <summary>The P6b HOP `SH` block, verbatim (`sh-after-other`).</summary>
    private static readonly string[] P6bHopShBlock =
    [
        "NET  01", "KEY OFF ", "NETID    01  12341234", "Hoptype 01 NB  ",
        "Center 01  28500 ", "Hopnum 0061", "MODEM OFF", "ENCRYPT OFF",
        "POWER low", "No_Sync", "HOP>",
    ];

    [Fact]
    public void Rung1a_TheP6bNetSelectWindow_ShowsTuningThenComplete_AndTheOutcomeSURVIVES()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");                       // the operator is IN HOP
        Assert.Equal(TuneChipState.None, vm.TuneChip);

        foreach (var line in P6bNetOneWindow)
        {
            Transport.InjectLine(line);
            if (line == " TUNING COUPLER ")
            {
                Assert.True(vm.IsTuning);
                Assert.Equal(TuneChipState.Tuning, vm.TuneChip);
                Assert.Equal("Tuning…", vm.TuneChipText);
            }
            if (line == " TUNE COMPLETE  ")
            {
                Assert.False(vm.IsTuning);
                Assert.Equal(TuneChipState.Complete, vm.TuneChip);
                Assert.Equal("Tune Complete", vm.TuneChipText);
            }
        }

        // The closing prompt does not clear it…
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);

        // …nor does the whole HOP SH block the pane reads afterwards…
        foreach (var line in P6bHopShBlock) Transport.InjectLine(line);
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);

        // …nor a SECOND one, which is what the round-15 observer's re-read
        // brings back (§3.2). The tune outcome is LATCHED: nothing re-reads it,
        // so nothing may quietly drop it either.
        foreach (var line in P6bHopShBlock) Transport.InjectLine(line);
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);
        Assert.Equal("Tune Complete", vm.TuneChipText);
    }

    [Fact]
    public void Rung1b_TheFieldEntryShape_EndsOnTuneFail_AndSTAYSThere()
    {
        // The owner's own console, verbatim
        // (bench/transcripts/field-clone-console-20260820-1738.txt:44-53): an
        // ALE→HOP entry running TWO generate/tune cycles, both ending
        // ` TUNE FAULT `, with the battery answer interleaved and the `HOP>`
        // prompt only at the very end. Spacing is the radio's.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("HOP>");                       // the entry confirms HOP

        foreach (var line in new[]
        {
            "Wait...", "Generating Hopset...", " TUNING COUPLER ", "   TUNE FAULT   ",
            "Battery Status FULL 27.4V",
            "Wait...", "Generating Hopset...", " TUNING COUPLER ", "   TUNE FAULT   ",
        })
            Transport.InjectLine(line);

        Assert.Equal(TuneChipState.Fault, vm.TuneChip);
        Assert.Equal("Tune Fail", vm.TuneChipText);

        // The closing prompt and the landing reads that follow it leave the
        // outcome exactly where the radio put it.
        Transport.InjectLine("HOP>");
        Assert.Equal(TuneChipState.Fault, vm.TuneChip);
        foreach (var line in P6bHopShBlock) Transport.InjectLine(line);
        Assert.Equal(TuneChipState.Fault, vm.TuneChip);
        Assert.Equal("Tune Fail", vm.TuneChipText);
    }

    [Fact]
    public void Rung1c_AKeyOffLandingMidTune_ENDS_theTuningTransient_TheTerminalStillLands()
    {
        // CRITIC F20 — THE NAMED CANDIDATE MECHANISM for a chip that never
        // visibly shows "Tuning…". `SetKeyline(Off)` clears the tuning flag
        // (RadioState.cs:55-59), every HOP `SH` block carries `KEY OFF ` as
        // its second line (P6b), and the write gate opens at the entry's FIRST
        // prompt — before the tune lines — so a read released into that window
        // can answer right through the transient.
        //
        // EXISTING BEHAVIOUR, PINNED AS A FACT, NOT CHANGED (I-10): this round
        // is diagnostic for N3. Whether a `KEY OFF` may end the transient (vs
        // only a tune terminal) is the OWNER's decision, taken on the
        // escalation if rungs 2-4 implicate it (§11).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");

        foreach (var line in new[] { "NET  01", "HOP>", "Wait...", "Generating Hopset...", " TUNING COUPLER " })
            Transport.InjectLine(line);
        Assert.Equal(TuneChipState.Tuning, vm.TuneChip);

        // The interleaved SH block lands INSIDE the tune window.
        foreach (var line in P6bHopShBlock) Transport.InjectLine(line);
        Assert.False(vm.IsTuning);
        Assert.Equal(TuneChipState.None, vm.TuneChip);
        Assert.Equal("—", vm.TuneChipText);          // the transient is gone, mid-tune

        // …and the terminal still lands normally, so the OUTCOME is never lost.
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);
        Transport.InjectLine("HOP>");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);
    }

    [Fact]
    public void D16_AFrequencyChange_TurnsTheChipNEUTRAL_ARereadDoesNot()
    {
        // D16 (owner 2026-08-30): the coupler's tune belongs to the frequency
        // it tuned at, so the chip may not go on claiming "Tune Complete" at a
        // frequency the coupler will retune at the next key-up. NO VM CHANGE
        // BACKS THIS — the existing mapping already renders cleared flags as
        // TuneChipState.None, which is what this pin asserts end-to-end
        // (transport → parser → RadioState → StatusSurface → chip).
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("RxFr 01600000");
        Transport.InjectLine("TxFr 01600000");
        Transport.InjectLine(" TUNING COUPLER ");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);

        // An `SH` re-read of the SAME frequency leaves the tune standing.
        Transport.InjectLine("RxFr 01600000");
        Transport.InjectLine("TxFr 01600000");
        Assert.Equal(TuneChipState.Complete, vm.TuneChip);

        // A MOVE blanks it.
        Transport.InjectLine("RxFr 03596000");
        Transport.InjectLine("TxFr 03596000");
        Assert.Equal(TuneChipState.None, vm.TuneChip);
        Assert.Equal("—", vm.TuneChipText);
    }
}
