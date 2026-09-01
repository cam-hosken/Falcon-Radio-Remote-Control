using System.Reflection;
using System.Windows.Input;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The SSB settings pane's "Channels" card — the channel EDITOR (UI-tweaks
/// round 4, §AK). The load-bearing pins, in the order Gate C2 names them:
///
/// - Lazy read: <c>DI n n</c> goes out at most ONCE per channel per session,
///   through the VM's <c>RequestChannelOnce</c> API (the cache is the VM's,
///   never the view's virtualization). Round 5: that is the LIST tab's rule.
/// - Cache survival: the Core mirror CLEARS itself before every dump, so two
///   sequential single-channel dumps must still leave BOTH rows rendered.
/// - Store order (AK2): bare <c>CH n</c> → <c>FR</c> (or <c>RXF</c>+<c>TXF</c>)
///   → <c>MODE</c> → <c>BA</c> → <c>AG</c> → <c>RXON</c> → <c>DI n n</c> →
///   <c>CH found</c> + <c>SH</c>, exactly.
/// - Store is DISABLED while the radio has not confirmed its current channel
///   (without it there is nothing honest to restore to).
/// - kHz → 8-digit-Hz conversion, including both range boundaries.
/// - Validation blocks an invalid Store entirely, with an InputError.
/// - The Operate pane's F6 CH-00 lock is NOT this VM's business and stays
///   exactly where it is (its own tests pin it).
///
/// <para><b>UI tweaks round 5 (§BF) adds three families</b>, each of which
/// replaces a round-4 rule rather than sitting beside it — so the round-4 pins
/// they contradict are REWRITTEN here, not deleted:</para>
///
/// <list type="bullet">
///   <item><b>Wire-read vs buffer-populate.</b> Every picker SPIN sends
///     <c>DI n n</c> UNCONDITIONALLY (round 4: once per channel per session);
///     the first card load does one read via <c>EnsureLoaded</c>; Refresh does
///     one; and switching SUB-TABS sends NOTHING at all.</item>
///   <item><b>ONE read-back row (BF2)</b> replaces the six blue read-back
///     properties, which must be GONE — a pin asserts their absence, because
///     a leftover property is a leftover binding nobody sees fail.</item>
///   <item><b>Prefill under K5</b> — the exact inversion of round 4's
///     no-prefill pins on THIS surface: a report populates the buffers, a
///     populate gesture resets them, and a buffer the operator has modified
///     survives every later report until the next gesture.</item>
/// </list>
/// </summary>
public class SsbChannelEditorViewModelTests : SessionTestBase
{
    private SsbChannelEditorViewModel Vm()
        => new(new ChannelSurface(Radio), new SsbSurface(Radio), Session);

    /// <summary>Ready + confirmed SSB, sends cleared.</summary>
    private void EnterSsb()
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
    }

    /// <summary>Ready + confirmed SSB + a CONFIRMED current channel — the
    /// full Store gate.</summary>
    private void EnterSsbOnChannel(int current)
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine($"CHAN {current:00}");
        Transport.ClearSent();
    }

    private static string DiLine(int n, string rx, string tx, string mode, string agc, string bw, string rxOnly)
        => $"CH {n:00} RXFR {rx} TXFR {tx} MODE {mode} AGC {agc} BA {bw} RXONLY {rxOnly}";

    /// <summary>Park the picker on a channel. Each landing fires that
    /// channel's <c>DI n n</c> (pinned separately in
    /// <see cref="Picker_EverySpinReadsFresh_AndNeverSendsCh"/>), so the
    /// walk's reads are cleared here and the caller asserts only on what its
    /// own action puts on the wire.</summary>
    private void Pick(SsbChannelEditorViewModel vm, int channel)
    {
        while (vm.PickedChannel != channel) vm.UnitsUpCommand.Execute(null);
        Transport.ClearSent();
    }

    private static void Fill(
        SsbChannelEditorViewModel vm, string rx, string tx,
        string mode = "USB", string bw = "2.7", string agc = "SLOW", string rxOnly = "NO")
    {
        vm.RxFrequencyInput = rx;
        vm.TxFrequencyInput = tx;
        Choose(vm.ModulationChoices, mode);
        Choose(vm.BandwidthChoices, bw);
        Choose(vm.AgcChoices, agc);
        Choose(vm.RxOnlyChoices, rxOnly);
    }

    private static void Choose(IReadOnlyList<ChoiceItem> choices, string value)
    {
        var item = choices.FirstOrDefault(c => c.Value == value);
        Assert.True(item is not null, $"choice '{value}' is not offered");
        item!.SelectCommand.Execute(null);
    }

    // ---- Shape / tabs --------------------------------------------------------

    [Fact]
    public void ProgrammingTab_IsTheDefault_AndSwitchingSendsNothing()
    {
        var vm = Vm();
        EnterSsb();
        Assert.False(vm.IsListTabOpen);

        vm.OpenListTabCommand.Execute(null);
        Assert.True(vm.IsListTabOpen);
        vm.OpenProgrammingTabCommand.Execute(null);
        Assert.False(vm.IsListTabOpen);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SubTabSwitch_SendsNothing_AndLandingClearsTypedText()
    {
        // BF3 + round-7 DB (placeholders retired by round-8 EB): a tab strip
        // switches a VIEW (nothing on the wire), and landing back on the
        // programming tab is a populate GESTURE - typed text clears; the
        // cached report stays visible in the read-back row.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        vm.RxFrequencyInput = "9999";                 // operator scribbles
        Transport.ClearSent();

        vm.OpenListTabCommand.Execute(null);
        vm.OpenProgrammingTabCommand.Execute(null);

        Assert.Empty(Transport.SentLines);            // NOTHING went out
        Assert.Equal("", vm.RxFrequencyInput);        // the gesture cleared the scribble
        Assert.Equal("4.123 000", vm.ReadBackRow.RxFrequencyText);  // the value lives in the row
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Usb, vm.SelectedModulation);
    }

    [Fact]
    public void HundredRows_NumberedZeroToNinetyNine()
    {
        var vm = Vm();
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 100), vm.Rows.Select(r => r.Number));
        Assert.Equal("00", vm.Rows[0].NumberText);
        Assert.Equal("99", vm.Rows[99].NumberText);
    }

    [Fact]
    public void ListRows_AreReadOnly_NoCommandsAndNothingToSendThrough()
    {
        var commands = typeof(SsbChannelRow).GetProperties()
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(commands);

        var collaborators = typeof(SsbChannelRow)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType.Namespace?.StartsWith("Falcon.", StringComparison.Ordinal) == true)
            .Select(f => f.Name)
            .ToList();
        Assert.Empty(collaborators);
    }

    // ---- Lazy per-channel read (R4-Q2) ---------------------------------------

    [Fact]
    public void RequestChannelOnce_SendsDiForThatChannel_OncePerSession()
    {
        var vm = Vm();
        EnterSsb();

        vm.RequestChannelOnce(7);
        Assert.Equal(["DI 7 7"], Transport.SentLines);

        Transport.ClearSent();
        vm.RequestChannelOnce(7);
        vm.RequestChannelOnce(7);
        Assert.Empty(Transport.SentLines);            // cached — asked already
    }

    [Fact]
    public void RequestChannelRange_AsksForEachChannelInTheWindow_AndNeverTwice()
    {
        var vm = Vm();
        EnterSsb();

        vm.RequestChannelRange(3, 6);
        Assert.Equal(["DI 3 3", "DI 4 4", "DI 5 5", "DI 6 6"], Transport.SentLines);

        Transport.ClearSent();
        vm.RequestChannelRange(4, 8);                 // overlapping scroll
        Assert.Equal(["DI 7 7", "DI 8 8"], Transport.SentLines);
    }

    [Fact]
    public void RequestChannelOnce_BeforeTheGateOpens_SendsNothing_AndDoesNotBurnTheCache()
    {
        // A row scrolled past while the radio has not confirmed SSB must still
        // be readable later — the queried set is only marked on a real send.
        var vm = Vm();
        ConnectReady();
        vm.RequestChannelOnce(2);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SSB>");
        Transport.ClearSent();
        vm.RequestChannelOnce(2);
        Assert.Equal(["DI 2 2"], Transport.SentLines);
    }

    [Fact]
    public void NewSession_ReArmsEveryChannel()
    {
        var vm = Vm();
        EnterSsb();
        vm.RequestChannelOnce(1);
        Transport.ClearSent();

        Session.Close();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.RequestChannelOnce(1);
        Assert.Equal(["DI 1 1"], Transport.SentLines);
    }

    [Fact]
    public void Picker_EverySpinReadsFresh_AndNeverSendsCh()
    {
        // BF3 (round 5): the picker's read is UNCONDITIONAL. Round 4 skipped a
        // revisited channel; on a surface whose purpose is to PROGRAM that
        // channel, the cached record can be older than the last write from the
        // front panel, another operator or this app's own Store — and the
        // operator is about to edit from it. Still never CH n: moving the
        // radio's channel is Store's job.
        var vm = Vm();
        EnterSsb();

        vm.UnitsUpCommand.Execute(null);              // 00 -> 01
        vm.TensUpCommand.Execute(null);               // 01 -> 11
        Assert.Equal(11, vm.PickedChannel);
        Assert.Equal("11", vm.PickedChannelText);
        Assert.Equal(["DI 1 1", "DI 11 11"], Transport.SentLines);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("CH"));

        Transport.ClearSent();
        vm.TensDownCommand.Execute(null);             // back to 01 — read AGAIN
        Assert.Equal(["DI 1 1"], Transport.SentLines);

        Transport.ClearSent();
        vm.TensUpCommand.Execute(null);
        vm.TensDownCommand.Execute(null);             // and again, every time
        Assert.Equal(["DI 11 11", "DI 1 1"], Transport.SentLines);
    }

    [Fact]
    public void ListRowReads_StayLazyOnce_EvenAfterThePickerHasReadTheSameChannelFresh()
    {
        // The two read paths coexist: the LIST is still once-per-session (a
        // hundred rows is a hundred commands), and a fresh picker read marks
        // the once-set so a row scrolled to afterwards does not duplicate it.
        var vm = Vm();
        EnterSsb();

        vm.UnitsUpCommand.Execute(null);              // fresh read of 01
        Assert.Equal(["DI 1 1"], Transport.SentLines);
        Transport.ClearSent();

        vm.RequestChannelRange(0, 2);                 // the list scrolls over 00-02
        Assert.Equal(["DI 2 2"], Transport.SentLines);   // 00 and 01 already asked
    }

    [Fact]
    public void Picker_WrapsAtBothEnds()
    {
        var vm = Vm();
        vm.UnitsDownCommand.Execute(null);            // 00 -> 99
        Assert.Equal(99, vm.PickedChannel);
        Assert.Equal("9", vm.PickedTensText);
        Assert.Equal("9", vm.PickedUnitsText);

        vm.UnitsUpCommand.Execute(null);              // 99 -> 00
        Assert.Equal(0, vm.PickedChannel);
        Assert.Equal("00", vm.PickedChannelText);

        vm.TensDownCommand.Execute(null);             // 00 -> 90
        Assert.Equal(90, vm.PickedChannel);
    }

    // ---- The cache vs the self-clearing mirror --------------------------------

    [Fact]
    public void RowCache_SurvivesTheMirrorsSelfClearingDump()
    {
        // THE pin for the whole data model: SsbController.DisplayChannels
        // clears RadioState.ChannelList before every DI, so a VM rendering
        // straight off the mirror would show ONE channel at a time. Two
        // sequential single-channel dumps must leave BOTH rows rendered.
        var vm = Vm();
        EnterSsb();

        vm.RequestChannelOnce(4);
        Transport.InjectLine(DiLine(4, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        Assert.Equal("4.123 000", vm.Rows[4].RxFrequencyText);

        vm.RequestChannelOnce(9);                     // this DI clears the mirror
        Assert.Equal("4.123 000", vm.Rows[4].RxFrequencyText);   // …the cache does not forget
        Transport.InjectLine(DiLine(9, "14313500", "14313500", "LSB", "FAST", "3.0", "YES"));

        Assert.Equal("4.123 000", vm.Rows[4].RxFrequencyText);
        Assert.Equal("USB", vm.Rows[4].ModeText);
        Assert.Equal("14.313 500", vm.Rows[9].RxFrequencyText);
        Assert.Equal("LSB", vm.Rows[9].ModeText);
        Assert.Equal("—", vm.Rows[5].RxFrequencyText);      // never reported
    }

    [Fact]
    public void Rows_RenderEveryStoredFieldFromTheDiLine()
    {
        var vm = Vm();
        EnterSsb();

        Transport.InjectLine(DiLine(12, "07100000", "07200500", "AME", "MED", "6.0", "YES"));

        var row = vm.Rows[12];
        Assert.Equal("7.100 000", row.RxFrequencyText);
        Assert.Equal("7.200 500", row.TxFrequencyText);
        Assert.Equal("AME", row.ModeText);
        Assert.Equal("6.0", row.BandwidthText);
        Assert.Equal("MED", row.AgcText);
        Assert.Equal("YES", row.RxOnlyText);
        Assert.Equal("Med", row.AgcWordText);          // full word (round-7 DA)
    }

    // ---- Family sweep (round-4 audit): the two HOP defect classes ------------
    // Class 1 — a partial/odd report making a row render MORE confirmed than
    // the radio actually said. Class 2 — a stale field surviving a report that
    // invalidates it. Both are absent here BY CONSTRUCTION (a channel record
    // is parsed all-or-nothing and replaced whole), which is exactly why they
    // are pinned: "absent by construction" is only true until someone changes
    // the construction.

    [Fact]
    public void PartialDiLine_LeavesTheRowUnreported_NeverHalfFilled()
    {
        // Class 1. A channel record is all-or-nothing: a line missing fields
        // does not parse, so it contributes NOTHING. The alternative — a row
        // showing the two fields that did arrive and "—" for the rest — would
        // read as a channel we know more about than we do.
        var vm = Vm();
        EnterSsb();

        Transport.InjectLine("CH 08 RXFR 04123000 TXFR 04123000 MODE USB");   // truncated

        Assert.Equal("—", vm.Rows[8].RxFrequencyText);
        Assert.Equal("—", vm.Rows[8].ModeText);
        Assert.Equal("—", vm.Rows[8].BandwidthText);
        Assert.Equal("—", vm.Rows[8].AgcWordText);
    }

    [Fact]
    public void OddFieldValues_RenderVerbatim_NeverPrettifiedIntoAGuess()
    {
        // Class 1, the other half: the radio's own words go through unchanged.
        // An unprogrammed-looking frequency is shown as the radio wrote it,
        // not converted into a plausible number.
        var vm = Vm();
        EnterSsb();

        Transport.InjectLine(DiLine(11, "XXXXXXXX", "XXXXXXXX", "USB", "SL", "2.7", "NO"));

        Assert.Equal("XXXXXXXX", vm.Rows[11].RxFrequencyText);
        Assert.Equal("XXXXXXXX", vm.Rows[11].TxFrequencyText);
        Assert.Equal("USB", vm.Rows[11].ModeText);
    }

    [Fact]
    public void ASecondReport_ReplacesEveryField_NoStaleSurvivor()
    {
        // Class 2. The cache stores whole records keyed by channel, so a new
        // report cannot leave a field behind from the old one — the HOP defect
        // (a centre surviving a type change) has no analogue here.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(3, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        Transport.InjectLine(DiLine(3, "07100000", "07200500", "LSB", "FAST", "3.0", "YES"));

        var row = vm.Rows[3];
        Assert.Equal("7.100 000", row.RxFrequencyText);
        Assert.Equal("7.200 500", row.TxFrequencyText);
        Assert.Equal("LSB", row.ModeText);
        Assert.Equal("3.0", row.BandwidthText);
        Assert.Equal("FAST", row.AgcText);
        Assert.Equal("YES", row.RxOnlyText);
    }

    [Fact]
    public void AnUnparseableReport_KeepsTheLastReportedValues()
    {
        // The deliberate edge of class 2: an unparseable line carries no new
        // fact, so the row keeps what the radio last actually said rather than
        // blanking on a line nobody could read (the same rule as "a query is a
        // request, not a fact"). The raw line is still Console-visible.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(3, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        Transport.InjectLine("CH 03 something the parser has never seen");

        Assert.Equal("4.123 000", vm.Rows[3].RxFrequencyText);
    }

    // ---- BF2: ONE read-back ROW, in the channel-list style -------------------

    [Fact]
    public void ReadBackRow_FollowsThePicker_AndIsDashUntilReported()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(3, "05000000", "05000000", "CW", "OFF", "0.35", "NO"));

        Assert.Equal("00", vm.ReadBackRow.NumberText);       // parked on 00
        Assert.Equal("—", vm.ReadBackRow.RxFrequencyText);
        Pick(vm, 3);

        Assert.Equal("03", vm.ReadBackRow.NumberText);
        Assert.Equal("5.000 000", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal("5.000 000", vm.ReadBackRow.TxFrequencyText);
        Assert.Equal("CW", vm.ReadBackRow.ModeText);
        Assert.Equal("0.35", vm.ReadBackRow.BandwidthText);
        Assert.Equal("OFF", vm.ReadBackRow.AgcText);
        Assert.Equal("NO", vm.ReadBackRow.RxOnlyText);
        Assert.Equal("Off", vm.ReadBackRow.AgcWordText);

        Pick(vm, 4);
        Assert.Equal("04", vm.ReadBackRow.NumberText);
        Assert.Equal("—", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal("—", vm.ReadBackRow.ModeText);
    }

    [Fact]
    public void ReadBackRow_RendersIdenticallyToTheListRowForTheSameChannel()
    {
        // BF2's reason for existing: ONE projection, so the row the operator
        // programs and the row they later read in the list cannot disagree.
        // Field-by-field, not "same type" — a divergent Apply would pass that.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(9, "14313500", "07200500", "LSB", "SL", "3.0", "YES"));
        Pick(vm, 9);

        var listRow = vm.Rows[9];
        Assert.Equal(listRow.NumberText, vm.ReadBackRow.NumberText);
        Assert.Equal(listRow.RxFrequencyText, vm.ReadBackRow.RxFrequencyText);
        Assert.Equal(listRow.TxFrequencyText, vm.ReadBackRow.TxFrequencyText);
        Assert.Equal(listRow.ModeText, vm.ReadBackRow.ModeText);
        Assert.Equal(listRow.BandwidthText, vm.ReadBackRow.BandwidthText);
        Assert.Equal(listRow.AgcText, vm.ReadBackRow.AgcText);
        Assert.Equal(listRow.RxOnlyText, vm.ReadBackRow.RxOnlyText);
        Assert.Equal(listRow.AgcWordText, vm.ReadBackRow.AgcWordText);
        Assert.Equal("SL", vm.ReadBackRow.AgcText);   // the dump's own word, unexpanded
    }

    [Fact]
    public void TheBlueReadBackProperties_AreGone()
    {
        // BF2 deletes them. A leftover property is a leftover XAML binding
        // nobody sees fail — MAUI swallows a missing path at runtime — so the
        // absence is pinned rather than assumed. The row that replaced them is
        // asserted present in the same breath, so this cannot pass vacuously
        // by the whole class having been renamed away.
        var properties = typeof(SsbChannelEditorViewModel).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("RxFrequencyReadBack", properties);
        Assert.DoesNotContain("TxFrequencyReadBack", properties);
        Assert.DoesNotContain("ModeReadBack", properties);
        Assert.DoesNotContain("BandwidthReadBack", properties);
        Assert.DoesNotContain("AgcReadBack", properties);
        Assert.DoesNotContain("RxOnlyReadBack", properties);
        // Round 8 (EB): the round-7 value-backed placeholder quartet is gone
        // too — the reported frequencies render in the read-back row ONLY,
        // and the entry placeholders are XAML-literal hints.
        Assert.DoesNotContain("RxPlaceholder", properties);
        Assert.DoesNotContain("TxPlaceholder", properties);
        Assert.DoesNotContain("IsRxValueBacked", properties);
        Assert.DoesNotContain("IsTxValueBacked", properties);
        Assert.Contains("ReadBackRow", properties);
    }

    [Fact]
    public void Refresh_ClearsWhatTheRadioSaid_AndReReadsThePickedChannelAndTheVisibleRows()
    {
        var vm = Vm();
        EnterSsb();
        vm.RequestChannelRange(0, 2);
        Transport.InjectLine(DiLine(1, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        Assert.Equal("4.123 000", vm.Rows[1].RxFrequencyText);
        Transport.ClearSent();

        vm.RefreshChannelsCommand.Execute(null);

        // "Clears + re-reads" (R4-Q2): rows drop to "—" — nothing has been
        // reported since the clear, which is the honest state — and the reads
        // go out again for the picked channel and the visible window.
        Assert.Equal("—", vm.Rows[1].RxFrequencyText);
        Assert.Equal(["DI 0 0", "DI 1 1", "DI 2 2"], Transport.SentLines);
    }

    // ---- Store (AK2) ----------------------------------------------------------

    [Fact]
    public void Store_Simplex_SendsTheExactAk2Sequence()
    {
        var vm = Vm();
        EnterSsbOnChannel(4);
        Pick(vm, 12);
        Fill(vm, "14.3135", "14.3135", "USB", "2.7", "SLOW", "NO");

        vm.StoreCommand.Execute(null);

        Assert.Equal(
            [
                "CH 12",                 // bare store-select — no SH
                "FR 14313500",           // RX == TX
                "MODE USB",
                "BA 2.7",
                "AG SLOW",
                "RXON NO",
                "DI 12 12",              // verify
                "CH 4", "SH",            // restore the operator's channel
            ],
            Transport.SentLines);
    }

    [Fact]
    public void Store_Split_SendsRxfThenTxfInsteadOfFr()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 5);
        Fill(vm, "7.1", "7.2005", "LSB", "3.0", "FAST", "YES");

        vm.StoreCommand.Execute(null);

        Assert.Equal(
            [
                "CH 5",
                "RXF 07100000", "TXF 07200500",
                "MODE LSB",
                "BA 3.0",
                "AG FAST",
                "RXON YES",
                "DI 5 5",
                "CH 0", "SH",
            ],
            Transport.SentLines);
    }

    [Fact]
    public void Store_WithABlankTx_TreatsItAsSameAsRx_AndTakesTheFrPath()
    {
        // Round 6 (CH, owner): "if the tx freq is left blank, assume it's the
        // same as rx". The blank is entry semantics only — the buffer stays
        // honestly blank, and the simplex FR path proves the equality reached
        // the wire.
        var vm = Vm();
        EnterSsbOnChannel(4);
        Pick(vm, 12);
        Fill(vm, "14.3135", "", "USB", "2.7", "SLOW", "NO");

        Assert.True(vm.StoreCommand.CanExecute(null));     // blank TX does not gate Store
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains("FR 14313500", Transport.SentLines);              // simplex, one frequency
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("RXF", StringComparison.Ordinal));
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("TXF", StringComparison.Ordinal));
        Assert.Equal("", vm.TxFrequencyInput);             // the buffer was never auto-filled
    }

    [Fact]
    public void Store_AcceptsTheDisplayGroupingAsTypedInput()
    {
        // The "14.313 500" a prefill writes (or an operator copies) must be
        // storable as-is — the group space round-trips through TryFrequency.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 12);
        Fill(vm, "14.313 500", "14.313 500", "USB", "2.7", "SLOW", "NO");

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains("FR 14313500", Transport.SentLines);
    }

    [Fact]
    public void Store_AcceptsACommaDecimalTyped_AndPutsTheSameHzOnTheWire()
    {
        // The end-to-end half of the locale fix: not just the helper, the
        // actual Store path an operator on a comma-decimal phone would use.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 12);
        Fill(vm, "14,3135", "14,3135", "USB", "2.7", "SLOW", "NO");

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains("FR 14313500", Transport.SentLines);
    }

    [Fact]
    public void Store_RestoresEvenWhenTheFoundChannelIsTheTarget()
    {
        // The sequence is deterministic: the operator's radio always ends the
        // excursion re-read, never left wherever the writes put it.
        var vm = Vm();
        EnterSsbOnChannel(3);
        Pick(vm, 3);
        Fill(vm, "5", "5");

        vm.StoreCommand.Execute(null);

        Assert.Equal("CH 3", Transport.SentLines[0]);
        Assert.Equal(["DI 3 3", "CH 3", "SH"], Transport.SentLines.TakeLast(3));
    }

    [Fact]
    public void Store_UsesTheFoundChannelConfirmedAtThePress()
    {
        var vm = Vm();
        EnterSsbOnChannel(2);
        Transport.InjectLine("CHAN 47");              // the operator moved since
        Transport.ClearSent();
        Pick(vm, 8);
        Fill(vm, "5", "5");

        vm.StoreCommand.Execute(null);

        Assert.Equal(["CH 47", "SH"], Transport.SentLines.TakeLast(2));
    }

    [Fact]
    public void Store_RendersNothingOptimistically_OnlyTheDiAnswerMovesTheReadBack()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 6);
        Fill(vm, "14.3135", "14.3135", "USB", "2.7", "SLOW", "NO");

        vm.StoreCommand.Execute(null);
        Assert.Equal("—", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal("—", vm.ReadBackRow.ModeText);

        Transport.InjectLine(DiLine(6, "14313500", "14313500", "USB", "SL", "2.7", "NO"));

        Assert.Equal("14.313 500", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal("USB", vm.ReadBackRow.ModeText);
        Assert.Equal("SL", vm.ReadBackRow.AgcText);   // the radio's own abbreviation, verbatim
    }

    [Fact]
    public void StoreVerifyReRead_UpdatesTheListRowToo_TheCacheStaysCurrent()
    {
        // BF5 (owner): the channel-list cache must stay current. It feeds from
        // the mirror's DI events, so the Store excursion's own verify re-read
        // updates the list row for free — pinned rather than rebuilt, because
        // "for free" is exactly the kind of property a refactor loses.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 6);
        Transport.InjectLine(DiLine(6, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        Assert.Equal("4.123 000", vm.Rows[6].RxFrequencyText);

        Fill(vm, "14.3135", "14.3135", "USB", "2.7", "SLOW", "NO");
        vm.StoreCommand.Execute(null);
        Transport.InjectLine(DiLine(6, "14313500", "14313500", "USB", "SL", "2.7", "NO"));

        Assert.Equal("14.313 500", vm.Rows[6].RxFrequencyText);
        Assert.Equal("14.313 500", vm.ReadBackRow.RxFrequencyText);
    }

    [Fact]
    public void PartialWrite_ShowsAsWhatTheRadioReports_NotWhatWasTyped()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 6);
        Fill(vm, "14.3135", "14.3135", "USB", "2.7", "SLOW", "NO");
        vm.StoreCommand.Execute(null);

        // The radio kept the frequency but not the modulation.
        Transport.InjectLine(DiLine(6, "14313500", "14313500", "LSB", "SL", "2.7", "NO"));

        Assert.Equal("LSB", vm.ReadBackRow.ModeText);
        // …and the operator's OWN choice is untouched by that report (K5's
        // dirty guard): the read-back tells the truth, the buffer stays theirs.
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Usb, vm.SelectedModulation);
    }

    // ---- Store gate -----------------------------------------------------------

    [Fact]
    public void Store_IsDisabled_WhileTheCurrentChannelIsUnconfirmed()
    {
        // AK2: <found> comes from the CONFIRMED current channel. Without one
        // the editor has nowhere honest to return the operator, so it refuses.
        var vm = Vm();
        EnterSsb();                                   // SSB confirmed, no CHAN yet
        Fill(vm, "5", "5");

        Assert.True(vm.AreControlsEnabled);           // the card itself is live
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.True(vm.HasStoreDisabledReason);

        vm.StoreCommand.Execute(null);                // Execute ignores CanExecute
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 05");
        Assert.True(vm.StoreCommand.CanExecute(null));
        Assert.False(vm.HasStoreDisabledReason);
    }

    [Fact]
    public void Gate_NotReady_NothingSent()
    {
        var vm = Vm();
        Fill(vm, "5", "5");

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.False(vm.RefreshChannelsCommand.CanExecute(null));
        vm.StoreCommand.Execute(null);
        vm.RefreshChannelsCommand.Execute(null);
        vm.RequestChannelOnce(0);
        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasDisabledReason);
    }

    [Fact]
    public void Gate_ReadyButNotSsb_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
        Fill(vm, "5", "5");

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.StoreCommand.CanExecute(null));
        vm.StoreCommand.Execute(null);
        vm.RefreshChannelsCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- kHz -> 8-digit Hz (AK1a) ---------------------------------------------

    [Theory]
    [InlineData("1.6", "01600000")]                   // low boundary
    [InlineData("59.999999", "59999999")]             // high boundary — 1 Hz resolution (F5: the P2 window)
    [InlineData("51.5", "51500000")]                  // F5: the field source radio's own CH 01
    [InlineData("29.999999", "29999999")]             // the OLD ceiling, still legal
    [InlineData("14.313 500", "14313500")]            // the display format round-trips
    [InlineData("14.3135", "14313500")]
    [InlineData("4.123", "04123000")]
    [InlineData("7.200005", "07200005")]
    [InlineData(" 5 ", "05000000")]                   // trimmed (and internal spaces are the display grouping)
    public void Frequency_ConvertsMhzToTheEightDigitHzWireForm(string input, string expected)
    {
        Assert.True(SsbChannelEditorViewModel.TryFrequency(input, out string hz, out _));
        Assert.Equal(expected, hz);
    }

    [Theory]
    [InlineData("14,3135", "14313500")]               // comma-decimal keyboard
    [InlineData("59,999999", "59999999")]
    [InlineData("1,6", "01600000")]
    public void Frequency_AcceptsTheCommaDecimalSeparator(string input, string expected)
    {
        // Round-4 audit, MAJOR 1: a localized numeric keyboard emits the
        // CULTURE's separator. Rejecting it made fractional-kHz programming
        // impossible on a comma-decimal locale.
        Assert.True(SsbChannelEditorViewModel.TryFrequency(input, out string hz, out _));
        Assert.Equal(expected, hz);
    }

    [Fact]
    public void Frequency_MeansTheSameThing_InEveryLocale()
    {
        // Both spellings, under both a dot-decimal and a comma-decimal
        // culture: four combinations, one frequency. Parsing must not depend
        // on the phone's language setting.
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            InCulture(culture, () =>
            {
                Assert.True(SsbChannelEditorViewModel.TryFrequency("14.3135", out string dot, out _));
                Assert.True(SsbChannelEditorViewModel.TryFrequency("14,3135", out string comma, out _));
                Assert.Equal("14313500", dot);
                Assert.Equal("14313500", comma);
            });
        }
    }

    [Theory]
    [InlineData("1.234,5")]                           // grouped, comma-decimal
    [InlineData("1,234.5")]                           // grouped, dot-decimal
    [InlineData("1.234.5")]                           // two dots — grouping
    [InlineData("14,313,5")]
    public void Frequency_RefusesAmbiguousSeparators_RatherThanGuessing(string input)
    {
        // "1.234,5" and "1,234.5" are the same number to different readers.
        // Guessing wrong puts the radio on the wrong frequency, so both are
        // refused with an InputError in every culture.
        //
        // The message is asserted, not just the refusal: an ambiguous input
        // also happens to fail the invariant parse and the range check, so
        // WITHOUT this the pin would still pass if the separator rule were
        // deleted (verified by mutation). Naming the separator is what proves
        // the operator is told the actual problem.
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            InCulture(culture, () =>
            {
                Assert.False(SsbChannelEditorViewModel.TryFrequency(input, out _, out string? error));
                Assert.Contains("decimal separator", error);
            });
        }
    }

    private static void InCulture(string name, Action body)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
            body();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("")]                                  // required
    [InlineData("abc")]
    [InlineData("1.599999")]                          // below the band
    [InlineData("60")]                                // above the band (F5: the P2 ceiling is 59.999999)
    [InlineData("14.3135001")]                        // finer than 1 Hz
    [InlineData("-5")]
    public void Frequency_RejectsWhatTheRadioWouldSilentlyIgnore(string input)
    {
        Assert.False(SsbChannelEditorViewModel.TryFrequency(input, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void FrequencyDisplay_RendersMhzInTheVfoGrouping()
    {
        Assert.Equal("14.313 500", SsbChannelEditorViewModel.FrequencyDisplay("14313500"));
        Assert.Equal("1.600 000", SsbChannelEditorViewModel.FrequencyDisplay("01600000"));
        Assert.Equal("29.999 999", SsbChannelEditorViewModel.FrequencyDisplay("29999999"));
        Assert.Equal("51.500 000", SsbChannelEditorViewModel.FrequencyDisplay("51500000"));
        Assert.Equal("59.999 999", SsbChannelEditorViewModel.FrequencyDisplay("59999999"));
        // Never prettified into a guess: an unparseable record shows verbatim.
        Assert.Equal("BADVALUE", SsbChannelEditorViewModel.FrequencyDisplay("BADVALUE"));
    }

    // ---- Validation: an invalid Store sends NOTHING ---------------------------

    [Theory]
    [InlineData("", "5")]                             // no RX (RX is never optional)
    [InlineData("1.599", "5")]                        // RX below band
    [InlineData("5", "60")]                           // TX above band (blank would mean =RX; "60" is a real reject — F5)
    [InlineData("5.0000001", "5")]                    // finer than 1 Hz
    public void InvalidFrequency_SendsNothing_AndNotesTheChannel(string rx, string tx)
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 7);
        Fill(vm, rx, tx);

        vm.StoreCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
        Assert.Contains("CH 07", vm.InputError);
    }

    [Fact]
    public void MissingChoice_SendsNothing_AndNamesWhatIsMissing()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 7);
        vm.RxFrequencyInput = "5";
        vm.TxFrequencyInput = "5";

        vm.StoreCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("modulation", vm.InputError);

        // Round 6 (CK): choosing a modulation also DEFAULTS the bandwidth, so
        // the old "bandwidth" step no longer exists — the gate walks straight
        // to AGC.
        Choose(vm.ModulationChoices, "USB");
        Assert.Equal("2.7", vm.SelectedBandwidth);
        vm.StoreCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("AGC", vm.InputError);

        Choose(vm.AgcChoices, "SLOW");
        vm.StoreCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
        Assert.Contains("receive-only", vm.InputError);

        Choose(vm.RxOnlyChoices, "NO");
        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError);
        Assert.NotEmpty(Transport.SentLines);
    }

    [Fact]
    public void BandwidthChoices_AreAlwaysVisible_AndSwitchingModulationSwapsToTheNewSetsDefault()
    {
        // The measured per-modulation sets (probe R5) keyed to the modulation
        // being WRITTEN — app-side, because this is a pre-send choice.
        // Round 6 (CK, owner): the row is NEVER empty — with no modulation
        // picked it shows the USB/LSB set — and an invalidated pending choice
        // swaps to the new set's DEFAULT rather than going null, so Store is
        // never blocked by bandwidth.
        var vm = Vm();
        Assert.NotEmpty(vm.BandwidthChoices);         // the USB/LSB set, before any pick
        Assert.Contains(vm.BandwidthChoices, c => c.Value == "2.7");

        Choose(vm.ModulationChoices, "USB");
        Choose(vm.BandwidthChoices, "2.7");
        Assert.Equal("2.7", vm.SelectedBandwidth);

        Choose(vm.ModulationChoices, "CW");
        Assert.DoesNotContain("2.7", Falcon.Core.Protocol.Wire.AllowedBandwidths(Falcon.Core.Protocol.ModulationMode.Cw));
        Assert.Equal(SsbChannelEditorViewModel.DefaultBandwidth(Falcon.Core.Protocol.ModulationMode.Cw),
                     vm.SelectedBandwidth);           // swapped to CW's default, never null, never a value CW refuses
        Assert.NotNull(vm.SelectedBandwidth);
    }

    // ---- K5 prefill (round 5) — the inversion of round 4's no-prefill rule --
    // Scope check, stated once for the whole family: K5 is a carve-out for the
    // two PROGRAMMING surfaces only. Everywhere else — the Operate panes, the
    // HOP net list, the settings rows — no-prefill stands and is pinned by
    // those files' own tests.

    [Fact]
    public void DiReport_PopulatesTheSegmentsAndRow_NeverTheEntryText()
    {
        var vm = Vm();
        EnterSsb();

        Transport.InjectLine(DiLine(0, "04123000", "07200500", "USB", "SL", "2.7", "NO"));

        Assert.Equal("", vm.RxFrequencyInput);        // X5 restored: no report writes a buffer
        Assert.Equal("", vm.TxFrequencyInput);
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Usb, vm.SelectedModulation);
        Assert.Equal("2.7", vm.SelectedBandwidth);
        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Slow, vm.SelectedAgc);
        Assert.Equal(Falcon.Core.Protocol.YesNo.No, vm.SelectedRxOnly);
        Assert.Equal("4.123 000", vm.ReadBackRow.RxFrequencyText);   // EB: the row is the
        Assert.Equal("7.200 500", vm.ReadBackRow.TxFrequencyText);   // ONLY value display
    }

    [Fact]
    public void EmptyEntries_StoreTheReportedFrequencies_ByteIdentical()
    {
        // The round-7 fallback, end to end: empty Rx sends the reported Rx;
        // blank Tx stays "same as Rx" (the ONE exception), so a fully
        // reported simplex channel stores with nothing typed at all.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 9);
        Transport.InjectLine(DiLine(9, "29999999", "29999999", "USB", "SL", "2.7", "NO"));
        Transport.ClearSent();

        Assert.True(vm.StoreCommand.CanExecute(null));    // nothing typed, all backed
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains("FR 29999999", Transport.SentLines);   // the reported Hz, unchanged
    }

    [Fact]
    public void EmptyRx_WithNothingReported_KeepsStoreDisabledAndRefuses()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 7);                                  // never reported
        Choose(vm.ModulationChoices, "USB");
        Choose(vm.AgcChoices, "SLOW");
        Choose(vm.RxOnlyChoices, "NO");
        Transport.ClearSent();

        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("receive frequency", vm.StoreDisabledReason);
        vm.StoreCommand.Execute(null);                // Execute ignores CanExecute
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void MovingThePicker_SwapsTheRow_AndAnUnreportedChannelShowsDashes()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(9, "14313500", "14313500", "LSB", "FAST", "3.0", "YES"));

        Pick(vm, 9);

        Assert.Equal("", vm.RxFrequencyInput);
        Assert.Equal("14.313 500", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Lsb, vm.SelectedModulation);
        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Fast, vm.SelectedAgc);
        Assert.Equal(Falcon.Core.Protocol.YesNo.Yes, vm.SelectedRxOnly);

        Pick(vm, 10);                                 // never reported

        Assert.Equal("", vm.RxFrequencyInput);
        Assert.Equal("", vm.TxFrequencyInput);
        Assert.Equal("—", vm.ReadBackRow.RxFrequencyText);  // EB: the row says unreported
        Assert.Null(vm.SelectedModulation);
        // Round 6 (CK): bandwidth keeps its default pick even here.
        Assert.Equal("2.7", vm.SelectedBandwidth);
        Assert.Null(vm.SelectedAgc);
        Assert.Null(vm.SelectedRxOnly);
    }

    [Fact]
    public void BandwidthPrefill_IsDropped_WhenTheReportedModulationWouldNotAcceptIt()
    {
        // A bandwidth the reported modulation does not take would arm a Store
        // that cannot work (the modulation-keyed choice row would not even
        // offer it). Round 6 (CK): instead of leaving the segment unselected,
        // the set's DEFAULT is picked — Store is never blocked by bandwidth —
        // while the radio's own word survives untouched on the read-back row.
        var vm = Vm();
        EnterSsb();

        Transport.InjectLine(DiLine(0, "04123000", "04123000", "CW", "SL", "9.9", "NO"));

        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Cw, vm.SelectedModulation);
        Assert.Equal(SsbChannelEditorViewModel.DefaultBandwidth(Falcon.Core.Protocol.ModulationMode.Cw),
                     vm.SelectedBandwidth);
        Assert.Equal("9.9", vm.ReadBackRow.BandwidthText);   // the radio's word survives on the row
    }

    // ---- K5 prefill: the AGC prefix map (PROVISIONAL) ------------------------

    [Theory]
    [InlineData("SL", Falcon.Core.Protocol.AgcSpeed.Slow)]    // the CAPTURED dump form
    [InlineData("ME", Falcon.Core.Protocol.AgcSpeed.Medium)]  // the other captured form
    [InlineData("SLOW", Falcon.Core.Protocol.AgcSpeed.Slow)]  // the full wire spelling
    [InlineData("OFF", Falcon.Core.Protocol.AgcSpeed.Off)]
    [InlineData("FAST", Falcon.Core.Protocol.AgcSpeed.Fast)]
    [InlineData("DATA", Falcon.Core.Protocol.AgcSpeed.Data)]
    [InlineData("me", Falcon.Core.Protocol.AgcSpeed.Medium)]  // case is the radio's business
    public void AgcPrefill_MapsByUniqueTwoCharacterPrefix(string token, Falcon.Core.Protocol.AgcSpeed expected)
    {
        // PROVISIONAL (plan §BF3): the DI dump prints its own abbreviations and
        // only SL and ME are captured. All five values differ in their first
        // two characters, in BOTH the abbreviated and the full spelling, so a
        // prefix match covers whichever the radio turns out to print without
        // inventing a spelling. Bench item captures the rest.
        Assert.Equal(expected, Falcon.Core.Protocol.Wire.ParseDumpAgc(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("XX")]
    [InlineData("AUTO")]     // no known value starts "AU"
    [InlineData("NONE")]
    [InlineData("—")]
    public void AgcPrefill_LeavesAnUnmappedTokenUnselected(string token)
        => Assert.Null(Falcon.Core.Protocol.Wire.ParseDumpAgc(token));

    [Fact]
    public void AgcPrefill_MatchesTheTwoCharacterPREFIX_NotTheWholeToken()
    {
        // Naming the mechanism, not just its outputs: the map is a PREFIX map
        // precisely because the dump's own spellings are not all captured, so
        // an unseen longer form ("SLO", "MEDIUM") must still land. The cost is
        // that a token merely STARTING with a known prefix also lands — stated
        // here rather than discovered by an auditor, and harmless because the
        // radio is the only source of these tokens.
        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Slow, Falcon.Core.Protocol.Wire.ParseDumpAgc("SLO"));
        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Medium, Falcon.Core.Protocol.Wire.ParseDumpAgc("MEDIUM"));
    }

    [Fact]
    public void AnUnmappedAgcToken_LeavesTheSegmentUnselected_AndBlocksStoreUntilTheOperatorPicks()
    {
        // The honest end of the PROVISIONAL map. A guess here writes the wrong
        // AGC to a stored channel, so the editor refuses to guess and the
        // existing all-six validation does the rest. The read-back ROW still
        // shows the radio's token verbatim — the fact is not hidden, only
        // un-guessed.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "ZZ", "2.7", "NO"));

        Assert.Null(vm.SelectedAgc);
        Assert.Equal("ZZ", vm.ReadBackRow.AgcText);
        Transport.ClearSent();

        // The button is GREY, not merely unhelpful when pressed (round-5 audit
        // fix): the operator can see the write is unavailable before trying.
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.True(vm.HasStoreDisabledReason);
        Assert.Contains("AGC", vm.StoreDisabledReason);

        vm.StoreCommand.Execute(null);            // Execute ignores CanExecute
        Assert.Empty(Transport.SentLines);
        Assert.Contains("AGC", vm.InputError);

        Choose(vm.AgcChoices, "SLOW");
        Assert.True(vm.StoreCommand.CanExecute(null));
        Assert.False(vm.HasStoreDisabledReason);
        vm.StoreCommand.Execute(null);
        Assert.NotEmpty(Transport.SentLines);
    }

    // ---- Store's completeness gate (round-5 audit) ---------------------------

    [Fact]
    public void Store_IsDisabled_UntilEverySixIsPresentAndValid()
    {
        // Store writes the FULL set, so a blank buffer is not a partial write
        // — it is a write that cannot be made. Round 4 left the button enabled
        // through every one of these states and only complained on press.
        var vm = Vm();
        EnterSsbOnChannel(3);                     // gate 1 and 2 satisfied
        Pick(vm, 7);                              // an unreported channel: buffers blank

        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("receive frequency", vm.StoreDisabledReason);

        // Round 6: a BLANK Tx is legal (= same as Rx), and the bandwidth
        // always carries a default (CK) — so after Rx the gate goes straight
        // to modulation, and after modulation straight to AGC.
        vm.RxFrequencyInput = "5";
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("modulation", vm.StoreDisabledReason);

        Choose(vm.ModulationChoices, "USB");
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("AGC", vm.StoreDisabledReason);

        Choose(vm.AgcChoices, "SLOW");
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("receive-only", vm.StoreDisabledReason);

        Choose(vm.RxOnlyChoices, "NO");
        Assert.True(vm.StoreCommand.CanExecute(null));    // …and now it is live
        Assert.False(vm.HasStoreDisabledReason);
    }

    [Fact]
    public void Store_IsDisabled_WhenAPopulateLeavesADirtyBandwidthTheNewModulationRefuses()
    {
        // R6 review MAJOR 1. The one path that can produce an invalid
        // modulation+bandwidth PAIR: the operator picks a bandwidth (dirty),
        // then a later report moves the UNTOUCHED modulation from under it.
        // PickModulation cannot help (the operator didn't press it), so the
        // gate must catch membership, not just null — otherwise Store sends
        // MODE CW + BA 2.7 and the radio silently ignores the BA.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "3.0", "NO"));
        Choose(vm.BandwidthChoices, "2.7");           // a REAL edit (≠ the populated 3.0) — dirty
        Assert.True(vm.StoreCommand.CanExecute(null));

        Transport.InjectLine(DiLine(0, "04123000", "04123000", "CW", "SL", "1.0", "NO"));

        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Cw, vm.SelectedModulation);
        Assert.Equal("2.7", vm.SelectedBandwidth);    // the dirty buffer survived, as K5 says…
        Assert.False(vm.StoreCommand.CanExecute(null));   // …but Store must grey on the pair
        Assert.Contains("bandwidth", vm.StoreDisabledReason);

        Transport.ClearSent();
        vm.StoreCommand.Execute(null);                // Execute ignores CanExecute
        Assert.Empty(Transport.SentLines);
        Assert.Contains("not valid for CW", vm.InputError);
    }

    [Fact]
    public void Frequency_RejectsAStrayInternalSpace_AcceptingOnlyTheDisplayGrouping()
    {
        // R6 review MAJOR 2: stripping every space parsed " 1 4" as 14 MHz —
        // a paste or keyboard typo silently became a valid, unintended
        // frequency. Only the display's own "14.313 500" shape is space-legal.
        Assert.False(SsbChannelEditorViewModel.TryFrequency(" 1 4", out _, out string? error));
        Assert.Contains("space", error);
        Assert.False(SsbChannelEditorViewModel.TryFrequency("14.31 35", out _, out _));
        Assert.False(SsbChannelEditorViewModel.TryFrequency("14 .3135", out _, out _));
        Assert.False(SsbChannelEditorViewModel.TryFrequency("1 4313500", out _, out _));

        Assert.True(SsbChannelEditorViewModel.TryFrequency("14.313 500", out string hz, out _));
        Assert.Equal("14313500", hz);
        Assert.True(SsbChannelEditorViewModel.TryFrequency("  14.313 500  ", out hz, out _));
        Assert.Equal("14313500", hz);                 // outer trim still fine
        Assert.True(SsbChannelEditorViewModel.TryFrequency("14,313 500", out hz, out _));
        Assert.Equal("14313500", hz);                 // comma-decimal display shape too
    }

    [Fact]
    public void Store_IsDisabled_ByAnOutOfRangeFrequency_NotJustAnEmptyOne()
    {
        // "Present" is not enough — the gate runs the SAME validator Store
        // does, so a typed-but-impossible frequency greys the button too.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 7);
        Fill(vm, "5", "5");
        Assert.True(vm.StoreCommand.CanExecute(null));

        vm.RxFrequencyInput = "60";               // above the band (F5)
        Assert.False(vm.StoreCommand.CanExecute(null));

        vm.RxFrequencyInput = "5.0000001";        // finer than 1 Hz
        Assert.False(vm.StoreCommand.CanExecute(null));

        vm.RxFrequencyInput = "5";
        Assert.True(vm.StoreCommand.CanExecute(null));
    }

    [Fact]
    public void Store_IsDisabled_WhileRefreshHasBlankedTheBuffers()
    {
        // Refresh is a populate gesture: it empties the cache AND the buffers,
        // so there is a real window with nothing to write. Store must be grey
        // across it and come back on its own when the answers land.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        Assert.True(vm.StoreCommand.CanExecute(null));     // prefilled from the report

        vm.RefreshChannelsCommand.Execute(null);

        Assert.Equal("", vm.RxFrequencyInput);
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.True(vm.HasStoreDisabledReason);

        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        Assert.True(vm.StoreCommand.CanExecute(null));
        Assert.False(vm.HasStoreDisabledReason);
    }

    [Fact]
    public void StoreGate_AndStoresOwnValidation_NeverNameDifferentFields()
    {
        // The caption on a greyed button and the error a forced Execute
        // produces come from the same ordered rules. If they ever drifted, the
        // operator would be told to fix one thing and then told to fix another
        // — pinned on the field the two most easily disagree about.
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 7);
        vm.RxFrequencyInput = "5";
        vm.TxFrequencyInput = "5";
        Choose(vm.ModulationChoices, "USB");

        // Round 6 (CK): bandwidth self-defaults, so AGC is now the field the
        // two paths most easily disagree about.
        Assert.False(vm.StoreCommand.CanExecute(null));
        Assert.Contains("AGC", vm.StoreDisabledReason);

        vm.StoreCommand.Execute(null);
        Assert.Contains("AGC", vm.InputError);
    }

    // ---- K5 dirty guard: a modified buffer is never overwritten --------------

    [Fact]
    public void TypedText_SurvivesEveryReport_AndTheNextSpinClearsIt()
    {
        // Round 7 makes the survival STRUCTURAL: a report has no path to the
        // Text. The untouched segments still follow the radio, and a spin -
        // a populate gesture - clears the typing rather than replacing it.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        vm.RxFrequencyInput = "14.3135";              // the operator edits

        Transport.InjectLine(DiLine(0, "05000000", "05000000", "USB", "FAST", "2.7", "NO"));

        Assert.Equal("14.3135", vm.RxFrequencyInput);                       // untouched by the report
        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Fast, vm.SelectedAgc);   // the segment follows
        Assert.Equal("5.000 000", vm.ReadBackRow.RxFrequencyText);          // the row always tells the truth

        Pick(vm, 1);                                  // a populate GESTURE...
        Pick(vm, 0);
        Assert.Equal("", vm.RxFrequencyInput);        // ...clears the typing
        Assert.Equal("5.000 000", vm.ReadBackRow.RxFrequencyText);
    }

    [Fact]
    public void AModifiedSegment_SurvivesALaterReport()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        Choose(vm.AgcChoices, "DATA");                // operator's pre-send choice

        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "FAST", "2.7", "NO"));

        Assert.Equal(Falcon.Core.Protocol.AgcSpeed.Data, vm.SelectedAgc);
        Assert.Equal("FAST", vm.ReadBackRow.AgcText);
    }

    [Fact]
    public void RefreshChannels_ClearsTypedTextAndTheRow_UntilAnswersLand()
    {
        // Refresh means "start from the radio again": the cache empties, so
        // the read-back row honestly drops to "—", the typing clears, and
        // the answers repopulate the row when they arrive.
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        vm.RxFrequencyInput = "9999";

        vm.RefreshChannelsCommand.Execute(null);

        Assert.Equal("", vm.RxFrequencyInput);
        Assert.Equal("—", vm.ReadBackRow.RxFrequencyText);
        Assert.Null(vm.SelectedModulation);

        Transport.InjectLine(DiLine(0, "05000000", "05000000", "LSB", "SL", "3.0", "NO"));
        Assert.Equal("", vm.RxFrequencyInput);        // still the operator's (empty)
        Assert.Equal("5.000 000", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Lsb, vm.SelectedModulation);
    }

    // ---- BF3 landing: EnsureLoaded -------------------------------------------

    [Fact]
    public void EnsureLoaded_ReadsThePickedChannelOnce_AndIsIdempotentForTheSession()
    {
        // The first navigation must populate WITHOUT a spin (BF3). The VM is a
        // DI singleton, so on an already-Ready session no phase transition is
        // coming and the card's Loaded hook is the only trigger left.
        var vm = Vm();
        EnterSsb();                                   // clears the constructor-path load
        Transport.ClearSent();

        vm.EnsureLoaded();
        vm.EnsureLoaded();
        vm.EnsureLoaded();

        Assert.Empty(Transport.SentLines);            // already loaded this session
    }

    [Fact]
    public void EnsureLoaded_IsReadyGuarded_AndReArmsWithTheSession()
    {
        var vm = Vm();

        vm.EnsureLoaded();                            // disconnected
        Assert.Empty(Transport.SentLines);

        ConnectReady();
        Transport.ClearSent();
        vm.EnsureLoaded();                            // Ready but SSB unconfirmed
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SSB>");                 // the gate opens: the recompute loads
        Assert.Equal(["DI 0 0"], Transport.SentLines);

        Transport.ClearSent();
        vm.EnsureLoaded();
        Assert.Empty(Transport.SentLines);            // still once per session

        Session.Close();
        ConnectReady();
        Transport.InjectLine("SSB>");                 // a NEW session re-arms it
        Assert.Contains("DI 0 0", Transport.SentLines);
    }

    [Fact]
    public void FirstLoad_PopulatesTheRow_WithoutASpin()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(0, "04123000", "04123000", "USB", "SL", "2.7", "NO"));

        Assert.Equal("4.123 000", vm.ReadBackRow.RxFrequencyText);
        Assert.Equal("", vm.RxFrequencyInput);
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Usb, vm.SelectedModulation);
    }

    [Fact]
    public void SuccessfulStore_DoesNotClearOrRewriteTheBuffers()
    {
        var vm = Vm();
        EnterSsbOnChannel(0);
        Pick(vm, 6);
        Fill(vm, "14.3135", "14.3135", "USB", "2.7", "SLOW", "NO");

        vm.StoreCommand.Execute(null);
        Transport.InjectLine(DiLine(6, "14313500", "14313500", "USB", "SL", "2.7", "NO"));

        Assert.Equal("14.3135", vm.RxFrequencyInput);
        Assert.Equal(Falcon.Core.Protocol.ModulationMode.Usb, vm.SelectedModulation);
        Assert.Equal("2.7", vm.SelectedBandwidth);
    }

    [Fact]
    public void SessionDrop_ForgetsTheRadiosAnswers_ButNotTheOperatorsBuffers()
    {
        var vm = Vm();
        EnterSsb();
        Transport.InjectLine(DiLine(2, "04123000", "04123000", "USB", "SL", "2.7", "NO"));
        Assert.Equal("4.123 000", vm.Rows[2].RxFrequencyText);
        vm.RxFrequencyInput = "5";

        Session.Close();

        Assert.Equal("—", vm.Rows[2].RxFrequencyText);   // the next radio may be a different one
        Assert.Equal("5", vm.RxFrequencyInput);           // still the operator's
    }
}
