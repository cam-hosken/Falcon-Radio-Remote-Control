using System.Reflection;
using System.Windows.Input;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;

namespace Falcon.App.Tests;

/// <summary>
/// The HOP mode-settings pane — the net-programming card, rebuilt in UI-tweaks
/// round 5 (§BE/§BG) into a RADIO-NATIVE per-field editor. Round 4's Store
/// button and blue "Radio:" strip are gone; the Core scope-guard amendments
/// (X2 round 3, X6 round 5) govern which builders it may reach.
///
/// The load-bearing pins, in the order the round-5 gate names them:
///
/// - BG1 per-field writes: NETID / HOPTYPE / HOPSET / HOPLIST each go out as
///   their OWN command followed by `DIS n` — nothing batches, nothing else
///   goes out, and the HOPTYPE press is guarded against a re-click.
/// - BG2 type-switched values: which value controls exist follows the net's
///   CONFIRMED type, including the no-type-no-controls state. That is what
///   enforces the wire's HOPTYPE-before-HOPSET ordering.
/// - K6: every frequency in and out of this pane is MHz, and an entry outside
///   the contract blocks the send with a note (the radio silently ignores a
///   badly formed frequency, so the client must catch it).
/// - K5 carve-out: the entries PREFILL from confirmed reports; a populate
///   gesture (landing, picker spin, Refresh) resets them; a report never
///   overwrites a buffer the operator edited since that gesture.
/// - BG3 list UI: Add sends HOPLIST n ADD then re-reads BOTH the list and the
///   net; a row's Remove sends HOPLIST n DEL and re-reads the same two.
/// - BC4: any mirrored LIST net gets ONE `HOPLIST n` per session (the LIST
///   tab's lazy tier), and an editor landing re-reads the picked net's list
///   fresh.
/// - BG4 Clear net: opening the warning sends NOTHING, Proceed sends
///   `HOPSET n DEL` + `DIS n`, and the SECOND press this session still opens
///   the warning — no accepted latch.
/// - BD1/BD2: the value column is the shared "Frequencies (MHz)" vocabulary,
///   and the two panes render one mirror state identically.
/// - Read path (ROUND 9, the unified two-tier doctrine): the NET LIST tab
///   keeps its lazy once-per-session `DIS`-all; the EDITOR reads fresh per
///   landing — a picker spin and a programming-tab landing each send
///   `DIS n` (+ `HOPLIST n` for a confirmed LIST net). The round-5 "the
///   picker sends nothing" ruling is REVERSED, and the pane's Refresh button
///   and `RefreshNetsCommand` are DELETED.
/// - Gate: Ready + confirmed HOP (these are HOP-scoped writes).
/// </summary>
public class HopSettingsViewModelTests : SessionTestBase
{
    /// <summary>The §5 CONTROLLABLE fake: it records every (title, message,
    /// accept, cancel) and hands back a handle so the Clear-net prompt can be
    /// held OPEN while the session or the picker moves underneath it.</summary>
    private readonly FakeConfirmationPrompt _prompt = new();

    private HopSettingsViewModel Vm() => new(new HopSurface(Radio), Session, _prompt);

    /// <summary>Press Clear net and answer YES — the whole wipe, for the tests
    /// that are about what the wipe DOES rather than about the question.</summary>
    private void Wipe(HopSettingsViewModel vm)
    {
        _prompt.EnqueueAnswer(true);
        vm.RequestNetWipeCommand.Execute(null);
    }

    /// <summary>Ready + confirmed HOP, with the lazy load's DIS cleared.</summary>
    private void EnterHop()
    {
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
    }

    /// <summary>Park the picker on <paramref name="net"/>. Round 9: a landing
    /// READS the net it lands on (<c>DIS n</c>, plus <c>HOPLIST n</c> when the
    /// mirror already confirms it LIST), so the helper DRAINS those sends —
    /// the tests that are about the landing read spin the picker themselves.</summary>
    private void Pick(HopSettingsViewModel vm, int net)
    {
        while (vm.PickedNet != net) vm.NetUpCommand.Execute(null);
        Transport.ClearSent();
    }

    /// <summary>Report a programmed NB net, the R9 capture's shape.</summary>
    private void ReportNarrowband(int net, string id = "12345678", string centerKHz = "11565")
    {
        Transport.InjectLine($"NETID    0{net}  {id}");
        Transport.InjectLine($"Hoptype 0{net} NB  ");
        Transport.InjectLine($"Center 0{net}  {centerKHz} ");
    }

    private void ReportWideband(int net, string id = "24680135", string low = "02000", string high = "08000")
    {
        Transport.InjectLine($"NETID    0{net}  {id}");
        Transport.InjectLine($"Hoptype 0{net} WB  ");
        Transport.InjectLine($"Hopset 0{net}  {low}  {high}");
    }

    private void ReportList(int net, string id = "13579246")
    {
        Transport.InjectLine($"NETID    0{net}  {id}");
        Transport.InjectLine($"Hoptype 0{net} LIST");
    }

    // ---- Sub-tabs ------------------------------------------------------------

    [Fact]
    public void ProgrammingTab_IsTheDefault()
    {
        var vm = Vm();
        Assert.False(vm.IsListTabOpen);
    }

    [Fact]
    public void TabSwitching_EachTabLandsOnItsOwnTier()
    {
        // The two tiers, in one gesture pair: the LIST tab's FIRST landing
        // this session sends the whole-table DIS (nothing else in the pane
        // does any more), and landing back on the EDITOR re-reads the PICKED
        // net — DIS n, never DIS-all.
        var vm = Vm();
        EnterHop();

        vm.OpenListTabCommand.Execute(null);
        Assert.True(vm.IsListTabOpen);
        Assert.Equal(["DIS"], Transport.SentLines);

        Transport.ClearSent();
        vm.OpenProgrammingTabCommand.Execute(null);
        Assert.False(vm.IsListTabOpen);
        Assert.Equal(["DIS 0", "INTCOUPLER"], Transport.SentLines);
    }

    // ---- The picker ----------------------------------------------------------

    [Fact]
    public void Picker_Wraps_ZeroToNine()
    {
        var vm = Vm();
        Assert.Equal(0, vm.PickedNet);
        Assert.Equal("0", vm.PickedNetText);

        vm.NetDownCommand.Execute(null);             // 0 -> 9
        Assert.Equal(9, vm.PickedNet);
        Assert.Equal("9", vm.PickedNetText);

        vm.NetUpCommand.Execute(null);               // 9 -> 0
        Assert.Equal(0, vm.PickedNet);
        Assert.Equal("0", vm.PickedNetText);
    }

    [Fact]
    public void Picker_ReadsTheLandedNetFresh_EveryTime()
    {
        // ROUND 9 REVERSES the round-5 "moving the picker sends nothing"
        // ruling for this EDITOR, on the channel editor's rationale: a cached
        // record can be older than the last write from any source, and the
        // operator is about to edit from it.
        var vm = Vm();
        EnterHop();

        for (int i = 0; i < 10; i++) vm.NetUpCommand.Execute(null);

        Assert.Equal(
            ["DIS 1", "INTCOUPLER", "DIS 2", "INTCOUPLER", "DIS 3", "INTCOUPLER",
             "DIS 4", "INTCOUPLER", "DIS 5", "INTCOUPLER", "DIS 6", "INTCOUPLER",
             "DIS 7", "INTCOUPLER", "DIS 8", "INTCOUPLER", "DIS 9", "INTCOUPLER",
             "DIS 0", "INTCOUPLER"],
            Transport.SentLines);

        // …and it is FRESH, not once per net per session: coming back reads
        // again.
        Transport.ClearSent();
        vm.NetUpCommand.Execute(null);
        Assert.Equal(["DIS 1", "INTCOUPLER"], Transport.SentLines);
        Assert.Equal(1, vm.PickedNet);
    }

    [Fact]
    public void PickerLanding_AlsoReadsTheListOfAConfirmedListNet()
    {
        // No captured DIS answer carries a hoplist, so a LIST net needs its
        // own read — and the editor's landing takes it fresh, not once per
        // session.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);   // the BC4 lazy read
        Transport.ClearSent();

        while (vm.PickedNet != 3) vm.NetUpCommand.Execute(null);

        Assert.Equal(["DIS 1", "INTCOUPLER", "DIS 2", "INTCOUPLER", "DIS 3", "INTCOUPLER", "HOPLIST 3"], Transport.SentLines);
    }

    [Fact]
    public void PickerLanding_OnANonListNet_ReadsOnlyTheNet()
    {
        var vm = Vm();
        EnterHop();
        ReportNarrowband(2);
        Transport.ClearSent();

        while (vm.PickedNet != 2) vm.NetUpCommand.Execute(null);

        Assert.Equal(["DIS 1", "INTCOUPLER", "DIS 2", "INTCOUPLER"], Transport.SentLines);
    }

    [Fact]
    public void PickerLanding_NotReady_SendsNothing()
    {
        var vm = Vm();
        ConnectReady();                     // Ready, but HOP not confirmed
        Transport.ClearSent();

        vm.NetUpCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
    }

    // ---- BG1: per-field writes, each with its own DIS n re-read --------------

    [Fact]
    public void CommitNetId_SendsNetIdThenDisN_AndNothingElse()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.NetIdInput = "12345678";
        vm.CommitNetIdCommand.Execute(null);

        Assert.Equal(["NETID 3 12345678", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void SetType_SendsHopTypeThenDisN_AndNothingElse()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.SetTypeCommand.Execute("WB");

        Assert.Equal(["HOPTYPE 3 WB", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void SetType_ReClickOnTheConfirmedType_SendsNothing()
    {
        // BG1's re-click guard: on a CONFIRMED type, pressing the lit segment
        // is a no-op — not a re-write of a command that invalidates the net's
        // stored value. ROUND 13 (ruling 2026-08-20) narrowed the "lit" half of
        // that sentence: the guard reads the CONFIRMED type and the highlight
        // now reads the REPORTED one, so the two coincide here (a programmed
        // net) but not on a wiped one — the divergence has its own pin below.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        Assert.True(vm.IsListConfirmed);
        vm.SetTypeCommand.Execute("LIST");
        Assert.Empty(Transport.SentLines);

        // …and a DIFFERENT type still goes out.
        vm.SetTypeCommand.Execute("NB");
        Assert.Equal(["HOPTYPE 3 NB", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void SetType_OnALitButUNCONFIRMEDType_StillSends()
    {
        // The accepted consequence of the round-13 ruling, pinned so it stays
        // deliberate: the re-click guard is a GATE (confirmed type), not a
        // highlight. A wiped net reports WB, so WB is LIT while ConfirmedType
        // is null — and pressing WB therefore still sends. The duplicate
        // HOPTYPE is harmless (it re-asserts a type the radio already holds),
        // and the alternative — widening the guard to the reported type —
        // would make the segment un-pressable on exactly the net the operator
        // is trying to program.
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);

        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");
        Transport.ClearSent();

        Assert.True(vm.IsWidebandReported);            // lit…
        Assert.Null(vm.ConfirmedType);                 // …but not confirmed

        vm.SetTypeCommand.Execute("WB");
        Assert.Equal(["HOPTYPE 5 WB", "DIS 5"], Transport.SentLines);
    }

    [Fact]
    public void CommitCenter_SendsTheNarrowbandHopsetInKHz_ThenDisN()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        // The net must be CONFIRMED NB: a centre is an NB value, and the
        // command refuses to send one at a net of any other type. (This test
        // used to set up LIST with the comment "any confirmed type" — which is
        // precisely the hole the C2 audit found, encoded as a fixture.)
        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 NB  ");
        Transport.ClearSent();

        vm.CenterInput = "11.565";                    // MHz in…
        vm.CommitCenterCommand.Execute(null);

        Assert.Equal(["HOPSET 3 11565", "DIS 3"], Transport.SentLines);   // …kHz out
    }

    [Fact]
    public void CommitBandEdges_SendsOneHopsetCarryingBothEdges_ThenDisN()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 1);
        Transport.InjectLine("NETID    01  24680135");
        Transport.InjectLine("Hoptype 01 WB  ");      // band edges need a WB net
        Transport.ClearSent();

        vm.LowInput = "2.000";
        vm.HighInput = "8.000";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal(["HOPSET 1 02000 08000", "DIS 1"], Transport.SentLines);
    }

    [Fact]
    public void PerFieldWrites_TouchOnlyThePickedNet()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 7);

        vm.NetIdInput = "12345678";
        vm.CommitNetIdCommand.Execute(null);
        vm.SetTypeCommand.Execute("NB");

        Assert.All(Transport.SentLines, line => Assert.EndsWith("7", line.Split(' ')[1]));
        Assert.Equal(
            ["NETID 7 12345678", "DIS 7", "HOPTYPE 7 NB", "DIS 7"], Transport.SentLines);
    }

    [Fact]
    public void EveryWrite_RendersNothingOptimistically_OnlyTheRadiosReport()
    {
        // X3 survives the reshape: the row and the type highlight move ONLY on
        // the report that follows, never on the send.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);

        vm.NetIdInput = "12345678";
        vm.CommitNetIdCommand.Execute(null);
        vm.SetTypeCommand.Execute("NB");

        Assert.Equal("—", vm.Rows[2].NetIdText);
        Assert.Null(vm.ConfirmedType);
        Assert.False(vm.IsNarrowbandConfirmed);

        ReportNarrowband(2);
        Assert.Equal("12345678", vm.Rows[2].NetIdText);
        Assert.True(vm.IsNarrowbandConfirmed);
    }

    [Fact]
    public void PartialWrite_ShowsAsWhatTheRadioReports_NotWhatWasTyped()
    {
        // The radio answered the NETID but ignored the hopset: the row shows
        // exactly that, so the operator can SEE the write was partial.
        var vm = Vm();
        EnterHop();
        Pick(vm, 4);

        vm.NetIdInput = "12345678";
        vm.CommitNetIdCommand.Execute(null);
        Transport.InjectLine("NETID    04  12345678");
        Transport.InjectLine("Hoptype 04 NB  ");

        Assert.Equal("12345678", vm.Rows[4].NetIdText);
        Assert.Equal("NB", vm.Rows[4].TypeText);
        Assert.Equal("—", vm.Rows[4].ValueText);      // no Center line came back
    }

    // ---- BG2: a value write REQUIRES its own confirmed type -----------------
    // C2 audit round 1, MAJOR: the value commands checked session readiness and
    // input syntax but never the net's type, so CommitCenter would send
    // `HOPSET n <centre>` at a net the radio had just confirmed as LIST. The
    // XAML hid the control, but a command is the SENDING surface — visibility
    // is a rendering fact, and the wire's type-before-value rule has to be
    // carried by the thing that sends.

    /// <summary>Report a type for net 3 and park the picker there.</summary>
    private void PickWithType(HopSettingsViewModel vm, string wireType)
    {
        Pick(vm, 3);
        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine($"Hoptype 03 {wireType}");
        Transport.ClearSent();
    }

    /// <summary>The refusal note has to SAY WHICH refusal it is (C2 audit round
    /// 2, MINOR): the tests asserted only the "Net 3:" prefix, so collapsing
    /// both branches into one generic sentence left every test green. The two
    /// cases need different operator actions — "the radio has not told me what
    /// this net is" vs "this net is something else" — so they are pinned on
    /// distinguishing content, and on NOT reading like each other.</summary>
    private static void AssertWrongTypeNote(string note, string confirmedType)
    {
        Assert.StartsWith("Net 3:", note);
        Assert.Contains("confirmed " + confirmedType, note, StringComparison.Ordinal);
        Assert.DoesNotContain("has not reported", note, StringComparison.Ordinal);
    }

    private static void AssertNoTypeNote(string note)
    {
        Assert.StartsWith("Net 3:", note);
        Assert.Contains("has not reported", note, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmed", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LIST")]
    [InlineData("WB")]
    public void CommitCenter_UnderTheWrongConfirmedType_SendsNothing(string wireType)
    {
        var vm = Vm();
        EnterHop();
        PickWithType(vm, wireType);

        vm.CenterInput = "11.565";                   // a perfectly legal K6 value
        Assert.False(vm.CommitCenterCommand.CanExecute(null));
        vm.CommitCenterCommand.Execute(null);        // Execute ignores CanExecute

        Assert.Empty(Transport.SentLines);
        AssertWrongTypeNote(vm.InputError, wireType);
    }

    [Theory]
    [InlineData("LIST")]
    [InlineData("NB")]
    public void CommitBandEdges_UnderTheWrongConfirmedType_SendsNothing(string wireType)
    {
        var vm = Vm();
        EnterHop();
        PickWithType(vm, wireType);

        vm.LowInput = "2.000";
        vm.HighInput = "8.000";
        Assert.False(vm.CommitBandEdgesCommand.CanExecute(null));
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        AssertWrongTypeNote(vm.InputError, wireType);
    }

    [Theory]
    [InlineData("NB")]
    [InlineData("WB")]
    public void ListEdits_UnderTheWrongConfirmedType_SendNothing(string wireType)
    {
        var vm = Vm();
        EnterHop();
        PickWithType(vm, wireType);

        vm.ListAddInput = "11.010";
        Assert.False(vm.AddListFrequenciesCommand.CanExecute(null));
        vm.AddListFrequenciesCommand.Execute(null);
        AssertWrongTypeNote(vm.InputError, wireType);

        vm.InputError = "";
        Assert.False(vm.RemoveListFrequencyCommand.CanExecute("11010"));
        vm.RemoveListFrequencyCommand.Execute("11010");
        AssertWrongTypeNote(vm.InputError, wireType);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ValueCommands_WithNoConfirmedTypeAtAll_SendNothing_AndSaySoDifferently()
    {
        // The unreported case is distinct from the mismatched one, and it is
        // the one a fresh session lands in. Each command is checked on its own
        // note, so a single collapsed message fails four times over.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.CenterInput = "11.565";
        vm.LowInput = "2.000";
        vm.HighInput = "8.000";
        vm.ListAddInput = "11.010";

        Assert.Null(vm.ConfirmedType);
        Assert.False(vm.CommitCenterCommand.CanExecute(null));
        Assert.False(vm.CommitBandEdgesCommand.CanExecute(null));
        Assert.False(vm.AddListFrequenciesCommand.CanExecute(null));
        Assert.False(vm.RemoveListFrequencyCommand.CanExecute("11010"));

        foreach (var invoke in new Action[]
        {
            () => vm.CommitCenterCommand.Execute(null),
            () => vm.CommitBandEdgesCommand.Execute(null),
            () => vm.AddListFrequenciesCommand.Execute(null),
            () => vm.RemoveListFrequencyCommand.Execute("11010"),
        })
        {
            vm.InputError = "";
            invoke();
            AssertNoTypeNote(vm.InputError);
        }

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheTwoRefusals_ReadDifferently()
    {
        // The distinction pinned as ONE fact rather than inferred from two
        // separate tests: collapsing the branches makes these equal, and this
        // is the pin that says they must not be.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.CenterInput = "11.565";
        vm.CommitCenterCommand.Execute(null);
        var noType = vm.InputError;

        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 LIST");
        vm.CommitCenterCommand.Execute(null);
        var wrongType = vm.InputError;

        Assert.NotEqual(noType, wrongType);
        AssertNoTypeNote(noType);
        AssertWrongTypeNote(wrongType, "LIST");
    }

    [Fact]
    public void ValueCommands_ReEvaluate_WhenTheTypeReportLands()
    {
        // The gate must GREY rather than error, which means CanExecute has to
        // move with the mirror — not just be consulted once.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        Assert.False(vm.CommitCenterCommand.CanExecute(null));

        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 NB  ");
        Assert.True(vm.CommitCenterCommand.CanExecute(null));
        Assert.False(vm.CommitBandEdgesCommand.CanExecute(null));

        Transport.InjectLine("Hoptype 03 WB  ");      // the radio changes its mind
        Assert.False(vm.CommitCenterCommand.CanExecute(null));
        Assert.True(vm.CommitBandEdgesCommand.CanExecute(null));
    }

    [Fact]
    public void NetIdAndTypeWrites_AreNotTypeGated()
    {
        // Anti-vacuity for the gate: it must not have been applied to the two
        // commands that legitimately run BEFORE a type is known. NETID is
        // type-independent, and HOPTYPE is how a type gets confirmed at all —
        // gating either would make the pane unusable on a wiped net.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        Assert.Null(vm.ConfirmedType);

        Assert.True(vm.CommitNetIdCommand.CanExecute(null));
        Assert.True(vm.SetTypeCommand.CanExecute(null));

        vm.NetIdInput = "13579246";
        vm.CommitNetIdCommand.Execute(null);
        vm.SetTypeCommand.Execute("NB");

        Assert.Equal(
            ["NETID 3 13579246", "DIS 3", "HOPTYPE 3 NB", "DIS 3"], Transport.SentLines);
    }

    // ---- BG2: the value controls follow the CONFIRMED type -------------------

    [Fact]
    public void NoConfirmedType_MeansNoValueControls()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);

        Assert.Null(vm.ConfirmedType);
        Assert.True(vm.HasNoConfirmedType);
        Assert.False(vm.HasConfirmedType);
        Assert.False(vm.IsNarrowbandConfirmed);
        Assert.False(vm.IsWidebandConfirmed);
        Assert.False(vm.IsListConfirmed);
    }

    [Theory]
    [InlineData("NB", true, false, false)]
    [InlineData("WB", false, true, false)]
    [InlineData("LIST", false, false, true)]
    public void ConfirmedType_ShowsExactlyOneValueSection(
        string wire, bool nb, bool wb, bool list)
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 6);

        Transport.InjectLine("NETID    06  12345678");
        Transport.InjectLine($"Hoptype 06 {wire}");

        Assert.True(vm.HasConfirmedType);
        Assert.Equal(nb, vm.IsNarrowbandConfirmed);
        Assert.Equal(wb, vm.IsWidebandConfirmed);
        Assert.Equal(list, vm.IsListConfirmed);
    }

    [Fact]
    public void TypeSwitching_FollowsTheRadio_NotThePress()
    {
        // The structural half of "HOPTYPE before HOPSET": pressing WB does not
        // reveal the band-edge rows — the radio's answer does.
        var vm = Vm();
        EnterHop();
        Pick(vm, 6);
        ReportNarrowband(6);
        Assert.True(vm.IsNarrowbandConfirmed);

        vm.SetTypeCommand.Execute("WB");
        Assert.True(vm.IsNarrowbandConfirmed);        // still, until the report
        Assert.False(vm.IsWidebandConfirmed);

        Transport.InjectLine("Hoptype 06 WB  ");
        Assert.True(vm.IsWidebandConfirmed);
        Assert.False(vm.IsNarrowbandConfirmed);
    }

    [Fact]
    public void ReportedUnprogrammedNet_OffersNoValueControls()
    {
        // A wiped net reports "Hoptype WB" (protocol.md) — that WB is a
        // property of the wipe, not a programmed band. Offering band-edge
        // entries for it would invent a state the radio never described.
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);

        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");
        Transport.InjectLine("Hopset 05  XXXXXX  XXXXXX");

        Assert.Null(vm.ConfirmedType);
        Assert.False(vm.IsWidebandConfirmed);
        Assert.Equal("XXXXXXXX", vm.Rows[5].NetIdText);      // …but the row still says so
        Assert.Equal("not programmed", vm.Rows[5].ValueText);

        // ROUND 13 item 14 — CONTRACT CHANGE, owner ruling 2026-08-20
        // (plan/plan-round13.md §2): the HIGHLIGHT is decoupled from the gates
        // and always shows the type the RADIO REPORTED. The wipe's own
        // `Hoptype WB` therefore LIGHTS WB here — the ruling names that as its
        // accepted downside, in exchange for a type press lighting on the echo
        // on any net. The gate half above is unchanged and stays asserted in
        // the same test on purpose: the two signals must be able to disagree.
        Assert.Equal(HopType.Wideband, vm.ReportedType);
        Assert.True(vm.IsWidebandReported);
        Assert.False(vm.IsNarrowbandReported);
        Assert.False(vm.IsListReported);
    }

    [Fact]
    public void TypePress_OnAnUnprogrammedNet_LightsOnTheEcho_BeforeAnyNetIdLands()
    {
        // The owner's item-14 report, exactly: programming a net's type did not
        // highlight the segment. On a net the radio has reported UNPROGRAMMED
        // the Hoptype echo is the only thing that changes — no NETID line
        // follows, so ConfirmedType stays null forever and the old
        // confirmed-type highlight never lit. The reported-type highlight lands
        // on the echo (ruling 2026-08-20), while the GATES stay shut.
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);

        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");
        Assert.True(vm.IsWidebandReported);

        vm.SetTypeCommand.Execute("LIST");
        Assert.Equal(["HOPTYPE 5 LIST", "DIS 5"], Transport.SentLines);
        Assert.False(vm.IsListReported);                 // not until the radio says so

        Transport.InjectLine("Hoptype 05 LIST");         // the echo, and nothing else

        Assert.True(vm.IsListReported);
        Assert.False(vm.IsWidebandReported);
        // …and the gates did NOT open with it: no net ID means no confirmed
        // type, so no value controls and no list write.
        Assert.Null(vm.ConfirmedType);
        Assert.False(vm.HasConfirmedType);
        Assert.False(vm.IsListConfirmed);
        Assert.False(vm.AddListFrequenciesCommand.CanExecute(null));
    }

    [Fact]
    public void TheReportedHighlight_FollowsTheRePick()
    {
        // The other half of item 14 (same family as item 7's picker-change
        // repopulation): the highlight describes the PICKED net, so spinning
        // the picker re-points it — it must never be left showing the previous
        // net's type.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportNarrowband(3);
        ReportList(4);

        Assert.True(vm.IsNarrowbandReported);

        Pick(vm, 4);
        Assert.True(vm.IsListReported);
        Assert.False(vm.IsNarrowbandReported);

        Pick(vm, 7);                                     // a net nothing has reported
        Assert.Null(vm.ReportedType);
        Assert.False(vm.IsListReported);
        Assert.False(vm.IsNarrowbandReported);
        Assert.False(vm.IsWidebandReported);
    }

    [Fact]
    public void TheReportedHighlight_RaisesEveryFlagItMoves()
    {
        // The FAN-OUT itself, not just the values (A1 audit round 1, MINOR).
        // The three Is*Reported flags are computed, so a XAML DataTrigger only
        // re-evaluates when the VM RAISES their names. Dropping one
        // [NotifyPropertyChangedFor] leaves every value assertion in this file
        // green while that one segment's highlight silently goes stale on
        // screen — precisely the defect class item 14 exists to kill (the
        // auditor proved it: swapping the IsWidebandReported attribute for a
        // duplicate IsNarrowbandReported passed all 1,801 App tests). So each
        // name is pinned on a transition that MOVES it.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        void Transition(string report, string flag, Func<bool> lit)
        {
            raised.Clear();
            Transport.InjectLine(report);
            Assert.True(lit(), flag + " should be lit after " + report);
            Assert.Contains(nameof(HopSettingsViewModel.ReportedType), raised);
            Assert.Contains(flag, raised);
        }

        Transition("Hoptype 03 NB  ",
            nameof(HopSettingsViewModel.IsNarrowbandReported), () => vm.IsNarrowbandReported);
        Transition("Hoptype 03 WB  ",
            nameof(HopSettingsViewModel.IsWidebandReported), () => vm.IsWidebandReported);
        Transition("Hoptype 03 LIST",
            nameof(HopSettingsViewModel.IsListReported), () => vm.IsListReported);

        // …and the way BACK to nothing lit: spinning onto a net the radio has
        // never described must re-raise ALL THREE, or the previous net's
        // segment stays lit under a picker that has moved on.
        raised.Clear();
        vm.NetUpCommand.Execute(null);

        Assert.Null(vm.ReportedType);
        Assert.Contains(nameof(HopSettingsViewModel.ReportedType), raised);
        foreach (var flag in new[]
        {
            nameof(HopSettingsViewModel.IsNarrowbandReported),
            nameof(HopSettingsViewModel.IsWidebandReported),
            nameof(HopSettingsViewModel.IsListReported),
        })
            Assert.Contains(flag, raised);
    }

    // ---- K6: MHz in, kHz on the wire, illegal values never sent --------------

    [Theory]
    [InlineData("1.599")]
    [InlineData("30.000")]
    [InlineData("11.567")]
    [InlineData("11.5651")]
    [InlineData("11565")]
    [InlineData("abc")]
    [InlineData("")]
    public void IllegalCentre_SendsNothing_AndNotesTheNet(string entry)
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.CenterInput = entry;
        vm.CommitCenterCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
        Assert.StartsWith("Net 3:", vm.InputError);
    }

    [Fact]
    public void IllegalBandEdge_SendsNothing_EvenWhenTheOtherEdgeIsFine()
    {
        var vm = Vm();
        EnterHop();

        vm.LowInput = "2.000";
        vm.HighInput = "8.001";                       // off the 5 kHz step
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
    }

    [Fact]
    public void IllegalNetId_SendsNothing_AndNotesTheNet()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.NetIdInput = "1234567";                    // seven digits
        vm.CommitNetIdCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.StartsWith("Net 3:", vm.InputError);
    }

    [Fact]
    public void ValidSend_ClearsAPreviousNote()
    {
        var vm = Vm();
        EnterHop();

        vm.NetIdInput = "123";
        vm.CommitNetIdCommand.Execute(null);
        Assert.True(vm.HasInputError);

        vm.NetIdInput = "12345678";
        vm.CommitNetIdCommand.Execute(null);
        Assert.False(vm.HasInputError);
    }

    // ---- Round 7 (DB) as reshaped by round 8 (EA) ----------------------------
    // Reported values render in the blue READ DISPLAYS beside the entries,
    // never as entered Text; a populate gesture CLEARS the text; an empty
    // field at commit falls back to the value backing its display.

    [Fact]
    public void ConfirmedReport_FillsTheReadDisplays_InMHz_AndNeverTheText()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportWideband(2);

        Assert.Equal("", vm.NetIdInput);              // the X5 pin, restored
        Assert.Equal("", vm.LowInput);
        Assert.Equal("", vm.HighInput);
        Assert.Equal("24680135", vm.NetIdDisplayText);
        Assert.Equal("2.000", vm.LowDisplayText);     // kHz mirror -> MHz display
        Assert.Equal("8.000", vm.HighDisplayText);
    }

    [Fact]
    public void EmptyField_CommitsTheDisplayedValue_TheRound7Fallback()
    {
        // "if the user only changes one field and another has the default
        // value, that should be sent" (owner). The empty entry sends the
        // reported value backing its read display, byte-identical.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportNarrowband(2);
        Transport.ClearSent();

        Assert.Equal("", vm.CenterInput);
        vm.CommitCenterCommand.Execute(null);
        Assert.False(vm.HasInputError);
        Assert.Equal(["HOPSET 2 11565", "DIS 2"], Transport.SentLines);
    }

    [Fact]
    public void WidebandPair_EditOneEdge_TheOtherFallsBackToTheReportedValue()
    {
        // THE owner case for the fallback: type a new low, leave high empty,
        // one HOPSET carries the typed low and the reported high.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportWideband(2);
        Transport.ClearSent();

        vm.LowInput = "3.500";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal(["HOPSET 2 03500 08000", "DIS 2"], Transport.SentLines);

        // ROUND 14 A2, and the assertion is STRONGER than the `Assert.False`
        // it replaces: this pair spans 4.5 MHz, so it now carries the bench
        // span ADVISORY — which is not a refusal, and the send above proves
        // it did not block. Pinning the exact note rather than "no note" is
        // what keeps this test able to fail if the advisory ever turns into a
        // refusal (the send list would empty) or drifts into another sentence.
        Assert.Equal(
            HopSettingsViewModel.SpanRefusesGenerationAdvisory, vm.InputError);
    }

    [Fact]
    public void EmptyField_WithNothingReported_RefusesAndNamesItself()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 4);
        Transport.InjectLine("NETID    04  11112222");   // programmed, but NB value never reported
        Transport.InjectLine("Hoptype 04 NB");
        Transport.ClearSent();

        vm.CommitCenterCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("none reported", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TypedText_SurvivesEveryReport_AndTheReadDisplayMovesInstead()
    {
        // Round 7 makes this structural: a report has no path to the Text.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportNarrowband(2);

        vm.CenterInput = "12.000";                    // the operator types
        ReportNarrowband(2, centerKHz: "13570");      // a fresh report lands

        Assert.Equal("12.000", vm.CenterInput);        // untouched, always
        Assert.Equal("13.570", vm.CenterDisplayText);  // the read display follows
        Assert.Equal("13.570", vm.Rows[2].ValueText);  // …and the row too
    }

    [Fact]
    public void TabLanding_IsAPopulateGesture_ItClearsTypedText()
    {
        // R7-review MAJOR 1a: returning to the programming tab clears typing
        // (the SSB editor's rule), while switching still sends nothing.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportNarrowband(2);
        vm.CenterInput = "12.000";
        Transport.ClearSent();

        vm.OpenListTabCommand.Execute(null);
        vm.OpenProgrammingTabCommand.Execute(null);

        // Round 9, both tiers in order: the list tab's first landing dumps,
        // the editor landing re-reads the picked net.
        Assert.Equal(["DIS", "DIS 2", "INTCOUPLER"], Transport.SentLines);
        Assert.Equal("", vm.CenterInput);
        Assert.Equal("11.565", vm.CenterDisplayText);
    }

    [Fact]
    public void Reconnect_PreservesTypedText_TheLazyLoadIsNotAGesture()
    {
        // R7-review MAJOR 1b: the session-lazy first load fires on RECONNECT,
        // and the standing pin says a drop (and the reconnect after it)
        // preserves the operator's typed text.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        vm.CenterInput = "12.000";

        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");                 // lazy load re-fires here

        Assert.Equal("12.000", vm.CenterInput);       // still the operator's
    }

    [Fact]
    public void MalformedReportedValues_BackNothing_AndTheFallbackRefusesInsteadOfThrowing()
    {
        // R7-review MAJOR 2: a backing is only a backing if the fallback
        // could legally send it. A garbage center still SHOWS honestly (the
        // read display keeps MhzText's verbatim-kHz fallback) but backs no
        // fallback — an empty commit refuses with a note, never an
        // ArgumentException out of the Core builder.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        Transport.InjectLine("NETID    02  24680135");
        Transport.InjectLine("Hoptype 02 NB");
        Transport.InjectLine("Center 02  GARBAGE");
        Transport.ClearSent();

        Assert.Equal("GARBAGE kHz", vm.CenterDisplayText);  // shown, not sendable

        vm.CommitCenterCommand.Execute(null);         // empty text, no backing
        Assert.True(vm.HasInputError);
        Assert.Contains("none reported", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void PickerSpin_IsAPopulateGesture_ItClearsTheTextAndSwapsTheReadDisplays()
    {
        var vm = Vm();
        EnterHop();
        ReportNarrowband(2);
        ReportNarrowband(3, id: "87654321", centerKHz: "13570");

        Pick(vm, 2);
        vm.CenterInput = "12.000";                    // typing in progress
        Pick(vm, 3);
        Assert.Equal("", vm.CenterInput);             // never carries over
        Assert.Equal("13.570", vm.CenterDisplayText);

        Pick(vm, 2);
        Assert.Equal("", vm.CenterInput);
        Assert.Equal("11.565", vm.CenterDisplayText);
    }

    [Fact]
    public void UnprogrammedNet_PrefillsNothing_TheXFormLivesInTheReadDisplayOnly()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");

        Assert.Equal("", vm.NetIdInput);
        Assert.Equal("", vm.LowInput);
        Assert.Equal("", vm.HighInput);
        // EA: the display renders the shared vocabulary's unprogrammed ID
        // cell — the radio's own X-form — while backing no fallback.
        Assert.Equal("XXXXXXXX", vm.NetIdDisplayText);
    }

    // ---- BG3: the LIST UI ----------------------------------------------------

    [Fact]
    public void AddListFrequencies_SendsTheAdd_ThenReReadsBothTheListAndTheNet()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();                        // drain the BC4 HOPLIST 3

        vm.ListAddInput = "11.010 11.015";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Equal(
            ["HOPLIST 3 ADD 11010 11015", "HOPLIST 3", "DIS 3"], Transport.SentLines);
        Assert.Equal("", vm.ListAddInput);            // the box is spent
    }

    [Fact]
    public void AddListFrequencies_OneIllegalEntry_SendsNothing()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        vm.ListAddInput = "11.010 11.017";            // the second is off-step
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
        Assert.Equal("11.010 11.017", vm.ListAddInput);   // nothing was consumed
    }

    [Fact]
    public void ListRows_RenderTheReportedFrequencies_InMHz()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");

        Assert.Equal(["11.010", "11.015", "11.020"], vm.ListRows.Select(r => r.MhzText));
        Assert.False(vm.HasNoListFrequencies);
    }

    [Fact]
    public void ListRows_AreEmptyUntilTheHopListAnswerLands()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);

        Assert.Empty(vm.ListRows);
        Assert.True(vm.HasNoListFrequencies);
        Assert.Equal("Frequency list", vm.Rows[3].ValueText);
    }

    [Fact]
    public void RemoveListFrequency_SendsTheWireValue_ThenReReadsBoth()
    {
        // The row's command carries the kHz the radio reported, NOT the MHz on
        // screen — a removal must not depend on a display round trip.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Transport.ClearSent();

        var row = vm.ListRows[1];
        Assert.Equal("11015", row.FrequencyKHz);
        Assert.Equal("11.015", row.MhzText);
        row.Remove.Execute(row.FrequencyKHz);

        Assert.Equal(
            ["HOPLIST 3 DEL 11015", "HOPLIST 3", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void ListRows_BelongToThePickedNet_Only()
    {
        var vm = Vm();
        EnterHop();
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");

        Pick(vm, 3);
        Assert.Equal(3, vm.ListRows.Count);

        Pick(vm, 4);
        Assert.Empty(vm.ListRows);
    }

    // ---- BC4: one HOPLIST per mirrored LIST net per session ------------------

    [Fact]
    public void MirroredListNet_TriggersOneHopListRead_ForAnyNet()
    {
        // This pane's list tab renders all ten, so its scope is ANY LIST net —
        // unlike the Operate pane, which reads the CURRENT net only.
        var vm = Vm();
        EnterHop();

        ReportList(3);
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);

        ReportList(7, id: "24680135");
        Assert.Equal(["HOPLIST 3", "HOPLIST 7"], Transport.SentLines);

        // Once per net per session.
        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Equal(["HOPLIST 3", "HOPLIST 7"], Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void NonListNets_TriggerNoHopListRead()
    {
        var vm = Vm();
        EnterHop();

        ReportNarrowband(2);
        ReportWideband(4);

        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("HOPLIST", StringComparison.Ordinal));
        _ = vm;
    }

    [Fact]
    public void TheBc4OnceSet_StillGovernsTheListTabsLazyTier()
    {
        // Round 9 changed the EDITOR's reads, not the list tab's: a net the
        // dump confirms LIST is still read ONCE per session off the mirror,
        // and a repeat report does not fire a second read.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);

        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);
        _ = vm;
    }

    // ---- BG4 / round 10 §5: Clear net, behind the POPUP ----------------------

    [Fact]
    public void ClearNet_AsksTheExactPromptTableStrings_AndSendsNothingYet()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.RequestNetWipeCommand.Execute(null);

        var prompt = _prompt.Last;
        Assert.Equal("Clear net 3?", prompt.Title);
        // ROUND 11 §7 moved this message pin: the wipe also RESETS THE TYPE TO
        // WB (protocol.md), which the round-10 wording left the operator to
        // discover. The lifecycle cells below are UNCHANGED — only the words
        // moved.
        Assert.Equal(
            "The radio wipes this net's ID, type and frequencies and resets the type to WB.",
            prompt.Message);
        Assert.Equal(HopSettingsViewModel.ClearNetMessage, prompt.Message);
        Assert.Equal("Clear", prompt.AcceptText);
        Assert.Equal("Cancel", prompt.CancelText);
        Assert.Empty(Transport.SentLines);

        // Every raised prompt is ANSWERED before the test ends. xunit waits
        // for the async-void continuation ICommand.Execute starts, so leaving
        // one open wedges the RUN — which is also the cleanest proof that the
        // command really is awaiting this task.
        prompt.Complete(false);
    }

    // ==== The §5 LIFECYCLE MATRIX — Clear net, leg by leg ==================
    //
    // This card has exactly ONE popup consumer, so its matrix is written out
    // rather than driven by a theory (the ALE card, with three, uses one). The
    // legs and their pins, so the coverage is checkable at a glance:
    //
    //   1 accept sends once, against the CAPTURED net .. ClearNet_Accept_…
    //   2 cancel sends nothing ....................... ClearNet_Cancel_…
    //   3 session drops while open ................... ClearNet_SessionDrops…
    //   4a mode (HOP confirmation) lost while open ... ClearNet_HopConfirmation…
    //   4b write-gate lost while open ................ N/A — see below
    //   5 faulted / cancelled prompt, no wedge ....... ClearNet_AFaultedOr…
    //   6 every completed press re-prompts ........... ClearNet_SecondPress…
    //   7 target captured at press ................... ClearNet_TheCapturedNet…
    //
    // 4b is STRUCTURALLY EMPTY for this consumer, deliberately: unlike the ALE
    // programming card — whose two-level gate keeps the card live while the
    // radio scans and greys only the WRITES — this pane has ONE gate,
    // `HopReady` (Ready AND a confirmed HOP mode). There is no second level to
    // lose independently, so 4a IS the whole gate check here. Pinned as a fact
    // rather than left as a hole: TheClearNetGate_IsSingleLevel_… below.

    [Fact]
    public void ClearNet_Accept_SendsTheWipeOnce_ThenDisN()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        // Driven through the PENDING HANDLE rather than a queued answer, so
        // "asking sends nothing" and "answering sends once" are two separate
        // observations rather than one indivisible call.
        vm.RequestNetWipeCommand.Execute(null);
        Assert.Equal(1, _prompt.CallCount);
        Assert.Empty(Transport.SentLines);

        _prompt.Last.Complete(true);

        Assert.Equal(["HOPSET 3 DEL", "DIS 3"], Transport.SentLines);
        Assert.Equal(1, _prompt.CallCount);           // ONCE — no second ask
    }

    [Fact]
    public void TheClearNetGate_IsSingleLevel_SoThereIsNoSecondGateToLoseWhileOpen()
    {
        // The justification for matrix cell 4b being empty, pinned so it stays
        // a FACT rather than an assumption: this pane greys as ONE unit. If a
        // second-level write gate is ever added here (the ALE card's shape),
        // AreControlsEnabled and the command's CanExecute stop agreeing and
        // this fails — which is the prompt to fill cell 4b.
        var vm = Vm();
        EnterHop();

        Assert.True(vm.AreControlsEnabled);
        Assert.True(vm.RequestNetWipeCommand.CanExecute(null));

        // Leaving HOP closes BOTH at once — one gate, not two.
        Transport.InjectLine("SSB>");
        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.RequestNetWipeCommand.CanExecute(null));

        // …and back again, so this is not asserting a permanently-dead card.
        Transport.InjectLine("HOP>");
        Assert.True(vm.AreControlsEnabled);
        Assert.True(vm.RequestNetWipeCommand.CanExecute(null));
    }

    [Fact]
    public void ClearNet_DropsTheWipedNetsListRows_Immediately()
    {
        // C2 audit round 1, MAJOR: the wipe erases the hoplist on the RADIO,
        // but nothing erases it in the Core mirror — no HOPLIST answer follows
        // a wipe — so the erased frequencies kept rendering, each with a live
        // Remove button. They go the moment the wipe is sent.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Assert.Equal(3, vm.ListRows.Count);

        Wipe(vm);

        Assert.Empty(vm.ListRows);
        Assert.True(vm.HasNoListFrequencies);
    }

    [Fact]
    public void ClearNet_ThenReprogramAsList_ReReadsInsteadOfShowingTheErasedList()
    {
        // The full bench cycle the audit described: wipe net 3, make it LIST
        // again in the SAME session. The pane must ASK for the list again
        // rather than serve the pre-wipe mirror entry.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Assert.Equal(3, vm.ListRows.Count);

        Wipe(vm);
        Transport.ClearSent();

        // The wipe's own DIS answer: the radio reports the net unprogrammed.
        Transport.InjectLine("NETID    03  XXXXXXXX");
        Transport.InjectLine("Hoptype 03 WB  ");
        Assert.Empty(vm.ListRows);
        Assert.Empty(Transport.SentLines);            // and no HOPLIST for a wiped net

        // Re-programmed as LIST: the BC4 once-set was cleared by the wipe, so
        // a FRESH HOPLIST 3 goes out.
        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Contains("HOPLIST 3", Transport.SentLines);

        // …and the radio's new answer is what renders.
        Transport.InjectLine("HOPLIST 03   12345  13570");
        Assert.Equal(["12.345", "13.570"], vm.ListRows.Select(r => r.MhzText));
    }

    [Fact]
    public void TwoOverlappingWipes_SuppressBothNets_Independently()
    {
        // C2 audit round 2, MAJOR: the suppression was a single scalar, so
        // wiping net 3 then net 4 before 3's answer came back OVERWROTE 3's
        // suppression — the auditor watched ["HOPSET 4 DEL","DIS 4","HOPLIST 3"]
        // go out, re-querying the list net 3 had just erased. Wipes overlap in
        // practice: the operator is faster than the radio.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        ReportList(4, id: "24680135");
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Transport.InjectLine("HOPLIST 04   12345  13570");

        Pick(vm, 3);
        Wipe(vm);
        Pick(vm, 4);
        Wipe(vm);
        Transport.ClearSent();

        // Any mirror event now re-runs the BC4 sweep with BOTH nets still
        // reading LIST. Neither may be re-queried.
        Transport.InjectLine("NETID    07  87654321");
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("HOPLIST", StringComparison.Ordinal));

        // Net 3's own answer lands: 3 un-suppresses, 4 does NOT.
        Transport.InjectLine("NETID    03  XXXXXXXX");
        Transport.InjectLine("Hoptype 03 WB  ");
        Transport.InjectLine("NETID    03  13579246");
        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);

        // …and net 4 only when ITS report arrives.
        Transport.InjectLine("NETID    04  XXXXXXXX");
        Transport.InjectLine("Hoptype 04 WB  ");
        Transport.InjectLine("NETID    04  24680135");
        Transport.InjectLine("Hoptype 04 LIST");
        Assert.Equal(["HOPLIST 3", "HOPLIST 4"], Transport.SentLines);
    }

    [Fact]
    public void ALandingRightAfterAWipe_DoesNotReQueryTheWipedNetsList()
    {
        // The round-9 shape of the same finding: the editor's landing read is
        // an operator gesture, and an operator gesture must NOT end the wipe
        // suppression — only the net's own report does. Spinning off the
        // wiped net and back must not ask for a list the radio just erased.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Pick(vm, 3);
        Wipe(vm);
        Transport.ClearSent();

        vm.NetUpCommand.Execute(null);                  // off to net 4…
        vm.NetDownCommand.Execute(null);                // …and back onto the wiped 3

        Assert.Equal(["DIS 4", "INTCOUPLER", "DIS 3", "INTCOUPLER"], Transport.SentLines);   // no HOPLIST 3
        Assert.Empty(vm.ListRows);

        // Everything else still reads: a DIFFERENT LIST net is unaffected.
        Transport.ClearSent();
        ReportList(6, id: "24680135");
        Assert.Equal(["HOPLIST 6"], Transport.SentLines);
    }

    [Fact]
    public void SessionDrop_ClearsTheWipeSuppression()
    {
        // The one thing besides a net's own report that ends suppression — a
        // new session is a new radio, and nothing is pending against it.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        Pick(vm, 3);
        Wipe(vm);

        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();

        ReportList(3);
        Assert.Equal(["HOPLIST 3"], Transport.SentLines);
        _ = vm;
    }

    [Fact]
    public void ListRows_NeverRenderForANetThisPaneHasNotQueried()
    {
        // The render condition is "have we asked since?", not "is there
        // something in the mirror" — the mirror can hold a list this pane
        // knows to be stale. A LIST net whose list was mirrored by ANOTHER
        // pane's read (the Operate pane owns its own once-set) must not
        // short-circuit this pane's own read.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Assert.Equal(3, vm.ListRows.Count);

        Wipe(vm);

        // The mirror STILL holds the pre-wipe list — proven by the settings
        // row, which reads the mirror directly — yet no rows render.
        Assert.Empty(vm.ListRows);
    }

    [Fact]
    public void ClearNet_Cancel_SendsNothing()
    {
        var vm = Vm();
        EnterHop();

        vm.RequestNetWipeCommand.Execute(null);
        _prompt.Last.Complete(false);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ClearNet_SecondPressThisSession_StillAsks()
    {
        // BG4's explicit deviation from the Operate pane's net-change warning:
        // NO once-per-session accepted latch. A destructive, irreversible wipe
        // re-confirms EVERY time — §5 keeps that exactly as it was.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        Wipe(vm);
        Transport.ClearSent();

        vm.RequestNetWipeCommand.Execute(null);
        Assert.Equal(2, _prompt.CallCount);
        Assert.Empty(Transport.SentLines);            // still nothing until answered

        _prompt.Last.Complete(true);
        Assert.Equal(["HOPSET 3 DEL", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void ClearNet_TheCapturedNetIsWiped_EvenIfThePickerMovedWhileTheQuestionWasOpen()
    {
        // ROUND 10 §5, the deliberate change of behavior from the deleted
        // strip: the popup NAMES the net, so the answer is about THAT net.
        // Capturing at press is what makes the question and the action agree —
        // the old strip closed itself on a picker move instead, which is the
        // right answer for an inline box and the wrong one for a modal that
        // already said "Clear net 3?".
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.RequestNetWipeCommand.Execute(null);
        Assert.Equal("Clear net 3?", _prompt.Last.Title);

        Pick(vm, 4);                                   // the picker moves…
        Transport.ClearSent();                         // …its landing read drained

        _prompt.Last.Complete(true);

        Assert.Equal(["HOPSET 3 DEL", "DIS 3"], Transport.SentLines);
        Assert.Equal(4, vm.PickedNet);                 // …and the picker stayed where it went
    }

    [Fact]
    public void ClearNet_SessionDropsWhileThePromptIsOpen_SendsNothing()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.RequestNetWipeCommand.Execute(null);
        Assert.False(_prompt.Last.IsResolved);

        Session.Close();
        Transport.ClearSent();

        _prompt.Last.Complete(true);                   // answered YES, too late

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ClearNet_HopConfirmationLostWhileThePromptIsOpen_SendsNothing()
    {
        // The same gate the send path uses, re-checked after the await: these
        // are HOP-scoped writes and the radio has left HOP.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.RequestNetWipeCommand.Execute(null);
        Assert.False(_prompt.Last.IsResolved);

        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        _prompt.Last.Complete(true);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ClearNet_AFaultedOrCancelledPrompt_SendsNothing_AndDoesNotWedge()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);

        vm.RequestNetWipeCommand.Execute(null);
        _prompt.Last.Fault();
        Assert.Empty(Transport.SentLines);

        vm.RequestNetWipeCommand.Execute(null);
        _prompt.Last.Cancel();
        Assert.Empty(Transport.SentLines);

        // NOT WEDGED: the next press still asks, and still wipes.
        Wipe(vm);
        Assert.Equal(["HOPSET 3 DEL", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void TheInlineWipeStripState_IsGone_AndTheWipeMachineryIsNot()
    {
        // §5's deletion, as an ABSENCE pin with an anti-vacuity partner: the
        // strip's view state and its two commands are gone, while the ONE
        // command that raises the popup — and the wipe suppression the strip
        // never owned — are untouched.
        var type = typeof(HopSettingsViewModel);

        Assert.Null(type.GetProperty("IsWipeWarningOpen"));
        Assert.Null(type.GetProperty("PendingWipeNetLabel"));
        Assert.Null(type.GetProperty("WipeWarningText"));
        Assert.Null(type.GetProperty("ConfirmNetWipeCommand"));
        Assert.Null(type.GetProperty("CancelNetWipeCommand"));

        Assert.NotNull(type.GetProperty("RequestNetWipeCommand"));
    }

    // ---- BD1/BD2: the shared value vocabulary --------------------------------

    [Fact]
    public void ValueColumn_RendersTheBD2Forms()
    {
        var vm = Vm();
        EnterHop();

        ReportNarrowband(2);
        ReportWideband(4);
        ReportList(6);
        Transport.InjectLine("HOPLIST 06   11010  11015  11020");

        Assert.Equal("11.565", vm.Rows[2].ValueText);
        Assert.Equal("2.000–8.000", vm.Rows[4].ValueText);
        Assert.Equal("3 freqs", vm.Rows[6].ValueText);
    }

    [Fact]
    public void BothPanes_RenderOneMirrorStateIdentically()
    {
        // The shared-vocabulary property, now covering the VALUE column too:
        // round 4 left the two panes deliberately different there (MHz on the
        // Operate row, raw kHz here), and round 5's one header over one
        // vocabulary closes that. Every cell must match, in all three display
        // states and all three types.
        var settings = Vm();
        var operate = new HopViewModel(new HopSurface(Radio), Session, new TestTime());
        EnterHop();

        void AssertAgree(int net)
        {
            Pick(settings, net);
            Transport.InjectLine($"NET  0{net}");
            Assert.Equal(settings.Rows[net].NetIdText, operate.ActiveNetIdText);
            Assert.Equal(settings.Rows[net].TypeText, operate.ActiveTypeText);
            Assert.Equal(settings.Rows[net].ValueText, operate.ActiveHopsetText);
        }

        // (a) UNHEARD — a record created by a Hoptype line alone.
        Transport.InjectLine("Hoptype 01 NB  ");
        AssertAgree(1);
        Assert.Equal("—", operate.ActiveNetIdText);

        // (b) CONFIRMED UNPROGRAMMED — the radio's own X-form.
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");
        AssertAgree(5);
        Assert.Equal("not programmed", operate.ActiveHopsetText);

        // (c) REPORTED, all three types.
        ReportNarrowband(2);
        AssertAgree(2);
        Assert.Equal("11.565", operate.ActiveHopsetText);

        ReportWideband(4);
        AssertAgree(4);
        Assert.Equal("2.000–8.000", operate.ActiveHopsetText);

        ReportList(6);
        Transport.InjectLine("HOPLIST 06   11010  11015  11020");
        AssertAgree(6);
        Assert.Equal("3 freqs", operate.ActiveHopsetText);
    }

    [Fact]
    public void Value_RendersFromTheCurrentlyConfirmedType_AndAStaleValueIsDropped()
    {
        // Round-4 audit, MAJOR 2, carried: the value column means a different
        // thing per hop type, so a centre must not survive the net becoming WB.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        ReportNarrowband(2);
        Assert.Equal("11.565", vm.Rows[2].ValueText);

        Transport.InjectLine("Hoptype 02 WB  ");

        Assert.Equal("WB", vm.Rows[2].TypeText);
        Assert.Equal("—", vm.Rows[2].ValueText);      // no band edges reported
    }

    [Fact]
    public void Value_StaysUnreported_WhileTheTypeItself_IsUnreported()
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);

        Transport.InjectLine("Center 05  11565 ");

        Assert.Equal("—", vm.Rows[5].TypeText);
        Assert.Equal("—", vm.Rows[5].ValueText);
    }

    [Fact]
    public void HoptypeOnlyReport_LeavesTheUnheardFieldsUnreported()
    {
        var vm = Vm();
        EnterHop();

        Transport.InjectLine("Hoptype 06 NB  ");

        Assert.Equal("—", vm.Rows[6].NetIdText);
        Assert.Equal("NB", vm.Rows[6].TypeText);
        Assert.Equal("—", vm.Rows[6].ValueText);
    }

    // ---- The round-4 members this round DELETED ------------------------------

    [Fact]
    public void StoreAndTheBlueReadBackStrip_AreGone_NoOrphanedBindings()
    {
        // BG1 deleted the batching Store and the "Radio:" read-back displays;
        // round 8 (EA) then retired round 7's value-backed placeholder
        // properties — the reported values render in the blue per-field
        // displays now, and the placeholders are XAML-literal hints.
        // Pinned on the TYPE so a XAML binding left behind fails the build's
        // sibling here rather than silently rendering nothing at runtime.
        var names = typeof(HopSettingsViewModel).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var gone in new[]
        {
            "StoreCommand", "NetIdReadBack", "TypeReadBack", "ValueReadBack",
            "ValueInput", "ValuePlaceholder", "SelectedType",
            "IsNarrowbandSelected", "IsWidebandSelected", "IsListSelected",
            "SelectTypeCommand",
            // round-8 EA retirements (round 7's DB placeholder model)
            "NetIdPlaceholder", "CenterPlaceholder", "LowPlaceholder",
            "HighPlaceholder", "IsNetIdValueBacked", "IsCenterValueBacked",
            "IsLowValueBacked", "IsHighValueBacked",
        })
            Assert.DoesNotContain(gone, names);

        // …and the per-field replacements really exist (anti-vacuity: a typo in
        // the list above would otherwise pass forever).
        foreach (var present in new[]
        {
            "CommitNetIdCommand", "SetTypeCommand", "CommitCenterCommand",
            "CommitBandEdgesCommand", "AddListFrequenciesCommand",
            "RemoveListFrequencyCommand", "RequestNetWipeCommand",
            // ROUND 10 §5: ConfirmNetWipeCommand left with the inline strip —
            // RequestNetWipeCommand is the ONE path now, and its own absence
            // pin (TheInlineWipeStripState_IsGone_...) covers the departure.
            "IsNarrowbandConfirmed",
            // round-8 EA read displays
            "NetIdDisplayText", "CenterDisplayText",
            "LowDisplayText", "HighDisplayText",
            // ROUND 13 item 14 (ruling 2026-08-20): the highlight signal, added
            // to this closed list DELIBERATELY (plan §3.4) — the Type row's
            // DataTriggers bind all four, so a rename that silently stopped the
            // segments lighting must fail here rather than at runtime.
            "ReportedType", "IsNarrowbandReported", "IsWidebandReported",
            "IsListReported",
        })
            Assert.Contains(present, names);
    }

    // ---- Gate ----------------------------------------------------------------

    [Fact]
    public void Gate_NotReady_NothingSent()
    {
        var vm = Vm();
        vm.NetIdInput = "12345678";

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.CommitNetIdCommand.CanExecute(null));
        vm.CommitNetIdCommand.Execute(null);         // Execute ignores CanExecute
        vm.SetTypeCommand.Execute("NB");
        Wipe(vm);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasDisabledReason);
    }

    [Fact]
    public void Gate_ReadyButNotHop_NothingSent()
    {
        // Net programming is HOP-scoped on the wire (protocol.md HOP
        // programming table) — at an SSB> prompt these commands do not apply.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.NetIdInput = "12345678";
        vm.CenterInput = "11.565";

        Assert.False(vm.AreControlsEnabled);
        Assert.False(vm.CommitNetIdCommand.CanExecute(null));
        vm.CommitNetIdCommand.Execute(null);
        vm.CommitCenterCommand.Execute(null);
        vm.AddListFrequenciesCommand.Execute(null);
        vm.NetUpCommand.Execute(null);                 // the landing read is gated too
        vm.OpenProgrammingTabCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Gate_OpensOnConfirmedHop()
    {
        var vm = Vm();
        ConnectReady();
        Assert.False(vm.AreControlsEnabled);

        Transport.InjectLine("HOP>");
        Assert.True(vm.AreControlsEnabled);
        Assert.Equal("", vm.DisabledReason);
        Assert.True(vm.CommitNetIdCommand.CanExecute(null));
    }

    // ---- Read path: the lazy DIS-all tier + the round-9 deletions -----------

    [Fact]
    public void TheRefreshCommand_IsGone()
    {
        // Round 9: the pane-bottom Refresh button and its command are
        // DELETED — an editor landing re-reads, so a manual "read the radio"
        // button was a second answer to one question. Reflection-pinned so a
        // later "restore the Refresh" cannot slip past review, and the markup
        // guard pins that no button was left bound to it.
        Assert.Null(typeof(HopSettingsViewModel).GetProperty("RefreshNetsCommand"));
        // Anti-vacuity: the commands that DID survive are still visible here.
        Assert.NotNull(typeof(HopSettingsViewModel).GetProperty("CommitNetIdCommand"));
        Assert.NotNull(typeof(HopSettingsViewModel).GetProperty("NetUpCommand"));
    }

    [Fact]
    public void InitialSight_ReadsThePickedNet_NOT_EveryNet()
    {
        // The surface first becoming READABLE is an EDITOR landing, so it
        // reads the picked net. The round-5 DIS-all that used to fire here
        // belongs to the LIST tab now and must NOT appear.
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);            // Ready alone is not readable

        Transport.InjectLine("HOP>");                 // …confirmed HOP is
        // ROUND 11 §7: the editors-read-fresh tier now carries the exclusion
        // table too — one sentinel-bracketed `EXC` beside the net's own read.
        // ROUND 14 B adds the third: `INTCOUPLER`, on the SAME tier and under
        // the same gate (plan/plan-round14.md §4-B — "no new tier"), because a
        // coupler row whose state only arrives when somebody visits the SSB
        // settings pane reads "—" exactly when it matters.
        // The pin is the WHOLE tier, in order, so a read added or lost here is
        // visible rather than absorbed.
        Assert.Equal(["DIS 0", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("NETID    00  12345678");
        Assert.Empty(Transport.SentLines);            // once per session, not per event

        Assert.NotNull(vm);
    }

    [Fact]
    public void InitialSight_ReadsWhicheverNetIsPicked()
    {
        // …and it is the PICKED net, not net 0 by luck of the default.
        var vm = Vm();
        EnterHop();
        Pick(vm, 6);

        Session.Close();
        ConnectReady();
        Transport.ClearSent();
        Transport.InjectLine("HOP>");

        Assert.Equal(["DIS 6", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void Reconnect_ReadsThePickedNetAgain_AndStillPreservesTyping()
    {
        // The initial-sight read re-arms on a new session (the next radio may
        // be a different one) — and it is a READ, not a populate gesture.
        var vm = Vm();
        EnterHop();
        Pick(vm, 2);
        vm.CenterInput = "12.000";

        Session.Close();
        ConnectReady();
        Transport.ClearSent();
        Transport.InjectLine("HOP>");

        Assert.Equal(["DIS 2", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);
        Assert.Equal("12.000", vm.CenterInput);       // still the operator's
    }

    [Fact]
    public void ListTabLanding_IsTheLazyTier_OneDisAllOncePerSession()
    {
        var vm = Vm();
        EnterHop();

        vm.OpenListTabCommand.Execute(null);
        Assert.Equal(["DIS"], Transport.SentLines);

        // Second landing renders from the mirror.
        Transport.ClearSent();
        vm.OpenProgrammingTabCommand.Execute(null);
        Transport.ClearSent();
        vm.OpenListTabCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        // A NEW session re-arms it.
        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
        vm.OpenListTabCommand.Execute(null);
        Assert.Equal(["DIS"], Transport.SentLines);
    }

    [Fact]
    public void ListTabLanding_ReArmsTheHopListOnceSet_ButNotTheWipeSuppression()
    {
        // The lazy load re-reads every net, so the BC4 once-set is re-armed
        // with it — but the wipe suppressions deliberately SURVIVE (C2 audit
        // round 2): a wiped net un-suppresses on its OWN answer, not because
        // the operator opened a tab.
        var vm = Vm();
        EnterHop();
        ReportList(3);
        ReportList(6, id: "24680135");
        Transport.ClearSent();

        Pick(vm, 3);
        Wipe(vm);
        Transport.ClearSent();

        vm.OpenListTabCommand.Execute(null);
        // The dump, plus the re-armed read for the net the mirror ALREADY
        // knows to be LIST (one round trip fewer than waiting for the dump to
        // say LIST again) — and NOT for the wiped net.
        Assert.Equal(["DIS", "HOPLIST 6"], Transport.SentLines);

        // …and net 3 stays suppressed even when the dump re-reports it LIST
        // before its own wipe answer has landed.
        Transport.ClearSent();
        Transport.InjectLine("Hoptype 03 LIST");
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NothingButTheListTab_EverSendsADisAll()
    {
        // The mis-tiering the round-1 audit caught, pinned from the other
        // direction: exercise every EDITOR gesture and the whole-table read
        // must never appear.
        var vm = Vm();
        EnterHop();

        vm.NetUpCommand.Execute(null);
        vm.NetDownCommand.Execute(null);
        vm.OpenProgrammingTabCommand.Execute(null);
        Transport.InjectLine("HOP>");
        ReportNarrowband(4);

        Assert.DoesNotContain("DIS", Transport.SentLines);
        Assert.NotNull(vm);
    }

    [Fact]
    public void ALandingRead_MovesNothingUntilTheAnswerLands()
    {
        var vm = Vm();
        EnterHop();
        Transport.InjectLine("NETID    01  12345678");
        Assert.Equal("12345678", vm.Rows[1].NetIdText);

        vm.NetUpCommand.Execute(null);                 // lands on 1, reads DIS 1
        // A query is a request, not a fact: the row keeps the LAST reported
        // value until a new report arrives — it does not blank or guess.
        Assert.Equal(["DIS 1", "INTCOUPLER"], Transport.SentLines);
        Assert.Equal("12345678", vm.Rows[1].NetIdText);
    }

    // ---- The Net list tab ----------------------------------------------------

    [Fact]
    public void TenRows_NumberedZeroToNine()
    {
        var vm = Vm();
        Assert.Equal(10, vm.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 10), vm.Rows.Select(r => r.Number));
    }

    [Fact]
    public void ListRows_AreReadOnly_NoCommandsAndNothingToSendThrough()
    {
        // The list tab has NO controls. Pinned on the TYPE, two ways, so a
        // later "just one little row action" cannot slip past a XAML review:
        // the row exposes no ICommand, and it holds no collaborator (surface or
        // owning VM) it could send anything through in the first place. The
        // round-5 LIST-editor rows are a DIFFERENT type, deliberately — this
        // pin still says the read-only table stays read-only.
        var commands = typeof(HopNetListRow).GetProperties()
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(commands);

        var collaborators = typeof(HopNetListRow)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType.Namespace?.StartsWith("Falcon.", StringComparison.Ordinal) == true)
            .Select(f => f.Name + " : " + f.FieldType.FullName)
            .ToList();
        Assert.Empty(collaborators);
    }

    [Fact]
    public void ListRows_RenderTheOneDumpsAnswers_AndReadDashUntilThen()
    {
        var vm = Vm();
        EnterHop();

        Assert.All(vm.Rows, r =>
        {
            Assert.Equal("—", r.NetIdText);
            Assert.Equal("—", r.TypeText);
            Assert.Equal("—", r.ValueText);
        });

        ReportNarrowband(4);

        Assert.Equal("4", vm.Rows[4].NumberText);
        Assert.Equal("12345678", vm.Rows[4].NetIdText);
        Assert.Equal("NB", vm.Rows[4].TypeText);
        Assert.Equal("11.565", vm.Rows[4].ValueText);

        Assert.Equal("—", vm.Rows[5].NetIdText);      // untouched neighbour
    }

    [Fact]
    public void ReportedUnprogrammedNet_SaysSo()
    {
        var vm = Vm();
        EnterHop();
        Transport.InjectLine("NETID    05  XXXXXXXX");
        Transport.InjectLine("Hoptype 05 WB  ");

        Assert.Equal("XXXXXXXX", vm.Rows[5].NetIdText);
        Assert.Equal("WB", vm.Rows[5].TypeText);
        Assert.Equal("not programmed", vm.Rows[5].ValueText);

        Transport.InjectLine("NETID    05  12345678");
        Assert.Equal("12345678", vm.Rows[5].NetIdText);
    }

    // ---- Session lifecycle ---------------------------------------------------

    [Fact]
    public void SessionDrop_ClearsTheStaleNote_AndRetiresAnOpenWipeQuestion()
    {
        // The note names a net on a radio that is gone. Round 10 §5: there is
        // no wipe-warning STATE to clear any more — a prompt open across the
        // drop is retired by the body's own gate re-check, so the answer
        // arrives and nothing is sent.
        var vm = Vm();
        EnterHop();

        vm.NetIdInput = "123";
        vm.CommitNetIdCommand.Execute(null);
        Assert.True(vm.HasInputError);
        vm.RequestNetWipeCommand.Execute(null);

        Session.Close();
        Transport.ClearSent();
        _prompt.Last.Complete(true);

        Assert.False(vm.HasInputError);
        Assert.Empty(Transport.SentLines);
    }

    // ==== ROUND 11 SECTION 7 =================================================

    // ---- The LIST add box's delimiter closes to SPACE -----------------------
    //
    // The grammar family P3 closed on the ALE twin, closed here on the box that
    // twin was modelled on. A comma inside a frequency is a DECIMAL COMMA to
    // most of the world, and this box takes MHz WITH decimals - so a comma in
    // the delimiter set was not generosity, it was a second reading of the
    // operator's text that the app silently preferred.

    [Fact]
    public void ListAdd_ACommaSeparatedPair_SendsNOTHING_AndNamesTheTokenAsTyped()
    {
        // THE case the widened delimiter got wrong and no note reported: "5,7"
        // split into "5" and "7", BOTH of which parse, so 05000 and 07000 went
        // at the wire as two frequencies where the operator had typed one.
        // Nothing on screen said so. As ONE token it cannot parse, and the note
        // quotes what was actually typed.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        vm.ListAddInput = "5,7";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Contains("'5,7'", vm.InputError, StringComparison.Ordinal);
        Assert.Equal("5,7", vm.ListAddInput);        // the box is NOT spent
    }

    [Theory]
    [InlineData("5,320")]          // a decimal comma
    [InlineData("5.320,7.450")]    // a comma between two well-formed values
    [InlineData("5.320;7.450")]    // a semicolon
    [InlineData("5.320\t7.450")]   // a TAB between them
    public void ListAdd_AnyDelimiterButSpace_RefusesWithTheOffender_AndSendsNothing(string typed)
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        vm.ListAddInput = typed;
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasInputError);
        Assert.Contains("'" + typed + "'", vm.InputError, StringComparison.Ordinal);
    }

    [Fact]
    public void ListAdd_SPACE_StillSeparates_TheAntiVacuityHalf()
    {
        // The refusals above are only meaningful if the ONE remaining delimiter
        // still works - a box that refused everything would satisfy all four.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        vm.ListAddInput = "5.320 7.450";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Equal(
            ["HOPLIST 3 ADD 05320 07450", "HOPLIST 3", "DIS 3"], Transport.SentLines);
        Assert.False(vm.HasInputError);
        Assert.Equal("", vm.ListAddInput);
    }

    [Fact]
    public void TheAddBoxPlaceholder_IsTheExactSectionSevenString()
        => Assert.Equal("e.g. 5.320 7.450 (MHz, space-separated)",
            HopSettingsViewModel.ListAddPlaceholder);

    // ---- The exclusion-bands section (R11 / X9) -----------------------------

    private static string ExcludeRow(int band, string lowKHz, string highKHz)
        => $"Exclude 0{band}  {lowKHz}   {highKHz} ";

    /// <summary>Ready + confirmed HOP with the landing reads drained AND the
    /// exclusion read's sentinel ANSWERED - so the mirror leaves the unread
    /// state and the next request dispatches instead of coalescing into a read
    /// still on the wire.</summary>
    private void EnterHopWithExcludeTable(params string[] rows)
    {
        EnterHop();
        foreach (var row in rows) Transport.InjectLine(row);
        AnswerSentinel();
        Transport.ClearSent();
    }

    [Fact]
    public void ExcludeSection_ThreeStates_UnreadIsAHyphenRow_ReadEmptyIsTheCaption()
    {
        var vm = Vm();
        EnterHop();

        // 1. UNREAD - exactly ONE hyphen row, and the caption is NOT up: "we
        //    have not asked" and "the radio says there are none" are different
        //    facts, and only the sentinel tells them apart.
        var placeholder = Assert.Single(vm.ExcludeDisplayRows);
        Assert.Equal("—", placeholder.BandText);
        Assert.Equal("—", placeholder.LowText);
        Assert.Equal("—", placeholder.HighText);
        Assert.False(vm.HasNoExcludeBands);

        // 2. READ-EMPTY - no rows at all, and the caption instead. THE trap:
        //    an empty table answers NOTHING, so this state exists only because
        //    the sentinel answered.
        AnswerSentinel();
        Assert.Empty(vm.ExcludeDisplayRows);
        Assert.True(vm.HasNoExcludeBands);
        Assert.Equal("No exclusion bands programmed.",
            HopSettingsViewModel.NoExcludeBandsCaption);

        // 3. ROWS - in the radio's own listing order, in the MHz vocabulary.
        Transport.ClearSent();
        vm.NetUpCommand.Execute(null);                   // an editor landing re-reads
        Assert.Contains("EXC", Transport.SentLines);
        Transport.InjectLine(ExcludeRow(0, "02000", "03000"));
        Transport.InjectLine(ExcludeRow(1, "11000", "11500"));
        AnswerSentinel();

        Assert.Equal(["0", "1"], vm.ExcludeDisplayRows.Select(r => r.BandText));
        Assert.Equal(["2.000", "11.000"], vm.ExcludeDisplayRows.Select(r => r.LowText));
        Assert.Equal(["3.000", "11.500"], vm.ExcludeDisplayRows.Select(r => r.HighText));
        Assert.False(vm.HasNoExcludeBands);
    }

    [Fact]
    public void ExcludeSection_TheUnreadPlaceholder_CarriesNoRemove()
    {
        // A Remove button beside a hyphen would offer to delete the marker.
        var vm = Vm();
        EnterHop();

        var placeholder = Assert.Single(vm.ExcludeDisplayRows);
        Assert.False(placeholder.CanRemove);
        Assert.Null(placeholder.RemoveBand);

        // …and a real row does carry one (anti-vacuity for the flag).
        AnswerSentinel();
        Transport.ClearSent();
        vm.NetUpCommand.Execute(null);
        Transport.InjectLine(ExcludeRow(0, "02000", "03000"));
        AnswerSentinel();

        var row = Assert.Single(vm.ExcludeDisplayRows);
        Assert.True(row.CanRemove);
        Assert.NotNull(row.RemoveBand);
    }

    [Fact]
    public void ExcludeRead_RidesTheEditorsReadFreshTier_EveryLanding()
    {
        // Section 10 names the tier: the pane's editors-read-fresh landings -
        // a picker spin, a programming-tab landing, and the surface first
        // becoming readable. Each one asks again; the table is global, but the
        // TIER is the pane's.
        var vm = Vm();
        EnterHopWithExcludeTable();

        vm.NetUpCommand.Execute(null);
        Assert.Equal(["DIS 1", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);
        AnswerSentinel();

        Transport.ClearSent();
        vm.OpenProgrammingTabCommand.Execute(null);
        Assert.Equal(["DIS 1", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ExcludeRead_CoalescesRatherThanStackingUp()
    {
        // The other half of the tier: landings can outrun the wire, and the
        // Core store is single-slot. Three spins in a row must not put three
        // reads on the wire.
        var vm = Vm();
        EnterHopWithExcludeTable();

        vm.NetUpCommand.Execute(null);
        vm.NetUpCommand.Execute(null);
        vm.NetUpCommand.Execute(null);

        Assert.Equal(1, Transport.CountSent("EXC"));
    }

    [Fact]
    public void AddExcludeBand_SendsTheNextFreeSlot_InEIGHT_DigitHz_ThenReReads()
    {
        var vm = Vm();
        EnterHopWithExcludeTable(ExcludeRow(0, "02000", "03000"));

        vm.ExcludeLowInput = "11.000";
        vm.ExcludeHighInput = "11.500";
        vm.AddExcludeBandCommand.Execute(null);

        // Band 1 is the next free slot; the edges are 8-DIGIT Hz, never the
        // 5-digit kHz the rest of the pane speaks.
        Assert.Equal(["EXC 1 11000000 11500000", "EXC", "BAT ST"], Transport.SentLines);
        Assert.False(vm.HasExcludeError);
        Assert.Equal("", vm.ExcludeLowInput);          // the add row is spent
        Assert.Equal("", vm.ExcludeHighInput);
    }

    [Fact]
    public void AddExcludeBand_NextFreeIsTheLOWEST_Gap_NotTheEnd()
    {
        // Deterministic slot choice: removing band 1 from a table of 0,1,2 puts
        // the next Add back in 1 rather than appending at 3.
        var vm = Vm();
        EnterHopWithExcludeTable(
            ExcludeRow(0, "02000", "03000"), ExcludeRow(2, "12000", "12500"));

        vm.ExcludeLowInput = "5.000";
        vm.ExcludeHighInput = "6.000";
        vm.AddExcludeBandCommand.Execute(null);

        Assert.Equal("EXC 1 05000000 06000000", Transport.SentLines[0]);
    }

    [Fact]
    public void AddExcludeBand_DisablesAtTEN_WithTheExactReason_AndTheBoundaryIsNineToTen()
    {
        var vm = Vm();
        EnterHopWithExcludeTable(
            [.. Enumerable.Range(0, 9).Select(b => ExcludeRow(b, "02000", "03000"))]);

        // NINE rows: still available.
        Assert.True(vm.AddExcludeBandCommand.CanExecute(null));
        Assert.Equal("", vm.AddExcludeBandDisabledReason);
        Assert.False(vm.HasAddExcludeBandDisabledReason);

        // …the tenth closes it.
        vm.NetUpCommand.Execute(null);
        foreach (int b in Enumerable.Range(0, 10))
            Transport.InjectLine(ExcludeRow(b, "02000", "03000"));
        AnswerSentinel();
        Transport.ClearSent();

        Assert.Equal(10, vm.ExcludeDisplayRows.Count);
        Assert.False(vm.AddExcludeBandCommand.CanExecute(null));
        Assert.Equal("All 10 bands used.", vm.AddExcludeBandDisabledReason);
        Assert.True(vm.HasAddExcludeBandDisabledReason);

        // Execute ignores CanExecute, so the body repeats the rule - and says
        // so rather than no-opping silently.
        vm.ExcludeLowInput = "5.000";
        vm.ExcludeHighInput = "6.000";
        vm.AddExcludeBandCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Equal("All 10 bands used.", vm.ExcludeError);
    }

    [Fact]
    public void AddExcludeBand_RefusesWhileTheTableIsUNREAD_RatherThanGuessingASlot()
    {
        // Adding into an unknown table would have to GUESS a free slot, and
        // guessing band 0 wrong OVERWRITES a band the operator cannot see. The
        // read is one landing away, so the honest answer is to wait and say so.
        var vm = Vm();
        EnterHop();                                    // the read is in flight, unanswered

        Assert.Null(vm.ExcludeDisplayRows[0].RemoveBand);
        Assert.False(vm.AddExcludeBandCommand.CanExecute(null));
        Assert.Equal("Waiting for the radio to report the exclusion bands.",
            vm.AddExcludeBandDisabledReason);

        vm.ExcludeLowInput = "5.000";
        vm.ExcludeHighInput = "6.000";
        vm.AddExcludeBandCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Equal(HopSettingsViewModel.ExcludeUnreadReason, vm.ExcludeError);
    }

    [Theory]
    [InlineData("1.100", "6.000")]      // the padding trap value, low edge
    [InlineData("5.000", "1.100")]      // …and high edge
    [InlineData("5.000", "30.000")]     // above the band
    [InlineData("5,320", "6.000")]      // a decimal comma - one token, refused
    [InlineData("5.0001", "6.000")]     // four decimals
    public void AddExcludeBand_AnIllegalEdge_SendsNothing_AndNamesItAsTyped(
        string low, string high)
    {
        var vm = Vm();
        EnterHopWithExcludeTable();

        vm.ExcludeLowInput = low;
        vm.ExcludeHighInput = high;
        vm.AddExcludeBandCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.True(vm.HasExcludeError);
        // The offender is quoted exactly as typed - and the entries are NOT
        // spent, so the operator can fix the one that was wrong.
        Assert.Equal(low, vm.ExcludeLowInput);
        Assert.Equal(high, vm.ExcludeHighInput);
    }

    [Fact]
    public void AddExcludeBand_AnEmptyEdge_RefusesAndSaysWhich()
    {
        var vm = Vm();
        EnterHopWithExcludeTable();

        vm.ExcludeLowInput = "5.000";
        vm.AddExcludeBandCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Contains("high edge", vm.ExcludeError, StringComparison.Ordinal);
    }

    [Fact]
    public void AddExcludeBand_EveryLegalEdge_ReachesTheWireAsEXACTLY_EightDigits()
    {
        // The 8-digit sibling of the 5-digit rule, at the SEND site rather than
        // at the converter: the radio silently ignores a short value, so a
        // seven-digit edge would not be refused - it would be misread.
        var vm = Vm();

        foreach (var (low, high) in new[]
        {
            ("1.600", "1.605"),       // the bottom of the band, where padding bites
            ("9.995", "10.000"),      // the decade boundary
            ("29.990", "29.995"),     // the top
        })
        {
            Session.Close();
            EnterHopWithExcludeTable();

            vm.ExcludeLowInput = low;
            vm.ExcludeHighInput = high;
            vm.AddExcludeBandCommand.Execute(null);

            var parts = Transport.SentLines[0].Split(' ');
            Assert.Equal("EXC", parts[0]);
            Assert.Equal(8, parts[2].Length);
            Assert.Equal(8, parts[3].Length);
        }
    }

    [Fact]
    public void RemoveExcludeBand_SendsTheDelete_ThenReReads_AndNeverAsksFirst()
    {
        // PER-ROW REMOVES STAY UNCONFIRMED - pinned deliberate. The round-10
        // section 5 popup matrix covers whole-record destruction (Clear net,
        // Delete address, Erase); a per-row Remove is the same class as the
        // hop-frequency and group-channel Removes, and adding a prompt here
        // would be a silent policy change in the other direction.
        var vm = Vm();
        EnterHopWithExcludeTable(
            ExcludeRow(0, "02000", "03000"), ExcludeRow(4, "11000", "11500"));

        var row = vm.ExcludeDisplayRows[1];
        Assert.Equal("4", row.BandText);
        row.RemoveBand!.Execute(row.BandText);

        Assert.Equal(["EXC 4 DEL", "EXC", "BAT ST"], Transport.SentLines);
        Assert.Equal(0, _prompt.CallCount);           // NOTHING was asked
    }

    [Fact]
    public void RemoveExcludeBand_TakesTheBandSLOT_NeverADisplayedFrequency()
    {
        // The hop-frequency row's rule, applied: the command's parameter is the
        // WIRE value, so a removal cannot be lost in a round trip through the
        // MHz conversion.
        var vm = Vm();
        EnterHopWithExcludeTable(ExcludeRow(7, "11000", "11500"));

        var row = Assert.Single(vm.ExcludeDisplayRows);
        Assert.Equal("7", row.BandText);

        // A displayed MHz value is not a band and sends nothing.
        vm.RemoveExcludeBandCommand.Execute("11.000");
        Assert.Empty(Transport.SentLines);

        vm.RemoveExcludeBandCommand.Execute("7");
        Assert.Equal(["EXC 7 DEL", "EXC", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ExcludeSection_IsGatedLikeTheRestOfThePane()
    {
        // HOP-scoped writes at a HOP> prompt. Outside the gate nothing on this
        // section reaches the wire, including the read.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.ExcludeLowInput = "5.000";
        vm.ExcludeHighInput = "6.000";
        Assert.False(vm.AddExcludeBandCommand.CanExecute(null));
        vm.AddExcludeBandCommand.Execute(null);
        vm.RemoveExcludeBandCommand.Execute("0");
        vm.NetUpCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ExcludeSection_HasItsOwnNote_NotTheNetEditorsNetPrefixedOne()
    {
        // InputError prefixes the picked NET; the exclusion table is global and
        // has no net to name. Keeping them separate also means a net-editor
        // note cannot be wiped by an unrelated exclusion action.
        var vm = Vm();
        EnterHopWithExcludeTable();

        vm.NetIdInput = "123";
        vm.CommitNetIdCommand.Execute(null);
        Assert.StartsWith("Net ", vm.InputError, StringComparison.Ordinal);

        vm.ExcludeLowInput = "nonsense";
        vm.ExcludeHighInput = "6.000";
        vm.AddExcludeBandCommand.Execute(null);

        Assert.True(vm.HasExcludeError);
        Assert.DoesNotContain("Net ", vm.ExcludeError, StringComparison.Ordinal);
        Assert.True(vm.HasInputError);                // …and the net note survives
    }

    [Fact]
    public void SessionDrop_ClearsTheExclusionNote_AndTheTableGoesBackToUNREAD()
    {
        var vm = Vm();
        EnterHopWithExcludeTable(ExcludeRow(0, "02000", "03000"));
        Assert.Single(vm.ExcludeDisplayRows);

        vm.ExcludeLowInput = "nonsense";
        vm.ExcludeHighInput = "6.000";
        vm.AddExcludeBandCommand.Execute(null);
        Assert.True(vm.HasExcludeError);

        Session.Close();

        Assert.False(vm.HasExcludeError);
        // Core's own ResetForConnect puts the mirror back to unread, so the
        // section renders the hyphen row again - the previous radio's bands
        // never linger.
        ConnectReady();
        var placeholder = Assert.Single(vm.ExcludeDisplayRows);
        Assert.False(placeholder.CanRemove);
    }

    // ==== ROUND 14 A2 — the hop-limits validation ============================
    //
    // plan/plan-round14.md §4-A2. TWO strengths, and the difference between
    // them is the whole point of the section:
    //
    //   ONE REFUSAL, on the WB band edges, because the wire refusal is
    //   BENCH-PROVEN at the exact boundary (`HOPSET 9 01995 03995` → the stored
    //   line + `** ERROR **`, record unchanged). Constitution §3.1 permits a
    //   client refusal only there.
    //
    //   FOUR ADVISORIES, which never block. Every one of them is pinned as
    //   "the command WENT OUT and the note is beside it" rather than as a note
    //   alone — an advisory that silently started blocking would otherwise pass
    //   a test that only looked at InputError.
    //
    // Comparisons below are written in the wire's 5-digit kHz where the code
    // compares kHz, and in the MHz entry vocabulary where the operator types.

    /// <summary>A confirmed-WB net with the picker parked on it and the sends
    /// drained — the fixture every band-edge case starts from.</summary>
    private HopSettingsViewModel WidebandNet(
        int net, string low = "02000", string high = "08000")
    {
        var vm = Vm();
        EnterHop();
        Pick(vm, net);
        ReportWideband(net, low: low, high: high);
        Transport.ClearSent();
        return vm;
    }

    [Theory]
    [InlineData("1.995", "8.000")]      // the LOW edge, one 5 kHz step below
    [InlineData("2.000", "1.995")]      // …and the HIGH edge, same sentence
    public void BelowTheFloor_TypedOnEitherWidebandEdge_RefusesWithTheOneSentence(
        string low, string high)
    {
        // The refusal boundary's RED half, on each edge in turn. ONE string is
        // shared by both (one refusal class, one sentence), carried through the
        // pane's net-prefixed note idiom.
        var vm = WidebandNet(2);

        vm.LowInput = low;
        vm.HighInput = high;
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);

        // THE GATE (audit round 1, MAJOR): the EXACT COMPOSED message, by full
        // equality. The two-part StartsWith + Contains pair below is kept for
        // what it says out loud, but it is not the pin — the auditor inserted
        // text BETWEEN the net prefix and the sentence and watched all 1,872
        // App tests stay green, because "starts with A" and "contains B" say
        // nothing whatever about what sits between A and B. What reaches the
        // operator is ONE string, so ONE string is what gets asserted.
        Assert.Equal(
            "Net 2: " + HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError);

        Assert.StartsWith("Net 2:", vm.InputError, StringComparison.Ordinal);
        Assert.Contains(
            HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.000", "3.995", "HOPSET 2 02000 03995")]   // typed low AT the floor
    [InlineData("2.005", "2.000", "HOPSET 2 02005 02000")]   // typed high AT the floor
    public void AtTheFloor_Sends_TheGreenHalfOfTheBoundary(
        string low, string high, string expected)
    {
        // The boundary's other half, and the anti-vacuity for the pair above: a
        // refusal that fired one step LOWER as well would pass every red case
        // and quietly cost the operator the whole 2.000 MHz edge.
        var vm = WidebandNet(2);

        vm.LowInput = low;
        vm.HighInput = high;
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal([expected, "DIS 2"], Transport.SentLines);
        Assert.DoesNotContain(
            HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError, StringComparison.Ordinal);
    }

    [Fact]
    public void ABelowFloorEdge_RESOLVED_FROM_THE_RADIOS_OWN_REPORT_IsNeverRefused()
    {
        // Constitution §3.1's last sentence, pinned: values sourced VERBATIM
        // from the radio's own report are never client-refused. The operator
        // edits the HIGH edge; the blank low resolves to the reported backing,
        // which here is below the floor. Refusing it would mean the client
        // outranking the radio — and would make the pane unable to re-send a
        // pair the radio itself is holding.
        //
        // (The radio write-refuses such a pair, so in practice it should never
        // come to report one. The rule is pinned rather than assumed, which is
        // exactly why the fixture has to construct the impossible case.)
        var vm = WidebandNet(2, low: "01995", high: "03995");

        Assert.Equal("1.995", vm.LowDisplayText);       // the radio's own word
        Assert.Equal("", vm.LowInput);                  // nothing typed there

        vm.HighInput = "3.995";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal(["HOPSET 2 01995 03995", "DIS 2"], Transport.SentLines);
        Assert.DoesNotContain(
            HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankEdge_WithNoConfirmedBacking_KeepsTheExistingIncompletePairRefusal()
    {
        // The pass-through above must not have become "a blank edge is always
        // fine". With NOTHING reported to fall back to, the round-7 refusal is
        // unchanged, word for word — this is the case round 14 deliberately did
        // not touch.
        var vm = Vm();
        EnterHop();
        Pick(vm, 4);
        Transport.InjectLine("NETID    04  24680135");
        Transport.InjectLine("Hoptype 04 WB  ");        // type confirmed, no Hopset line
        Transport.ClearSent();

        vm.HighInput = "3.995";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Contains("none reported", vm.InputError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusal_WINS_TheNoteSlot_OverEveryAdvisory()
    {
        // Advisory precedence: a refusal takes the slot outright, because
        // NOTHING went out and an advisory describing a command that was never
        // sent would be a lie. This pair trips both — span 2000 (the bench
        // advisory) and a below-floor low.
        var vm = WidebandNet(2);

        vm.LowInput = "1.995";
        vm.HighInput = "3.995";                          // span 2000 kHz
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Empty(Transport.SentLines);

        // Full equality, same reason as the boundary pair above: the slot holds
        // the refusal and NOTHING ELSE, which is the actual claim — a
        // Contains/DoesNotContain pair would pass on a note that had both
        // sentences in it as long as the advisory came second.
        Assert.Equal(
            "Net 2: " + HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError);
    }

    [Fact]
    public void AnINVERTED_Pair_MeasuresTheSpanByWIDTH_NotBySubtractionOrder()
    {
        // AUDIT ROUND 1, MINOR. Both edges are independently K6-legal, so
        // nothing stops a low ABOVE the high — and a raw `high - low` then goes
        // NEGATIVE, which is below every threshold, so this 2.005 MHz-wide pair
        // drew the under-140-kHz note. Exactly backwards: it is the WIDEST kind
        // of band, not the narrowest. The width of a band does not depend on
        // which edge was typed first.
        //
        // It still SENDS, and gets no refusal of its own: what the wire does
        // with an inverted HOPSET has never been captured (the EXC sibling
        // normalises low/high, but that is a different command and §3.1 does
        // not travel by analogy), and an advisory never blocks.
        var vm = WidebandNet(2);

        vm.LowInput = "4.005";
        vm.HighInput = "2.000";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal(["HOPSET 2 04005 02000", "DIS 2"], Transport.SentLines);
        Assert.Equal(
            HopSettingsViewModel.SpanRefusesGenerationAdvisory, vm.InputError);
    }

    [Fact]
    public void AnINVERTED_NarrowPair_StillDrawsTheNarrowNote_TheOtherSideOfTheOrdering()
    {
        // The ordering fix's other half, and its anti-vacuity: `Math.Abs` must
        // not have turned every inverted pair into the WIDE note. A 135 kHz
        // band typed high-edge-first is still a narrow band.
        var vm = WidebandNet(2);

        vm.LowInput = "2.135";
        vm.HighInput = "2.000";
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.Equal(["HOPSET 2 02135 02000", "DIS 2"], Transport.SentLines);
        Assert.Equal(HopSettingsViewModel.MinimumSpanAdvisory, vm.InputError);
    }

    [Theory]
    [InlineData("2.000", "4.000", true)]     // span EXACTLY 2000 kHz — the bench point
    [InlineData("2.000", "3.995", false)]    // one 5 kHz step under it
    public void TheSpanAdvisory_TurnsOnAtExactlyTwoMegahertz_AndNeverBlocks(
        string low, string high, bool advises)
    {
        // The boundary is EXCLUSIVE on this radio (P-1 run A S5: span 2000
        // stored then refused generation as Bad_Hopset; span 1000 generated),
        // so 2000 itself advises and 1995 does not. BOTH cases send — the
        // advisory rides beside a command that actually went out.
        var vm = WidebandNet(3);

        vm.LowInput = low;
        vm.HighInput = high;
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.NotEmpty(Transport.SentLines);
        Assert.Equal(
            advises ? HopSettingsViewModel.SpanRefusesGenerationAdvisory : "", vm.InputError);
    }

    [Theory]
    [InlineData("2.000", "2.135", true)]     // span 135 kHz — under the manual's minimum
    [InlineData("2.000", "2.140", false)]    // span 140 kHz — at it
    public void TheMinimumSpanAdvisory_TurnsOnBelow140kHz_AndNeverBlocks(
        string low, string high, bool advises)
    {
        // MANUAL-DERIVED and the manual disagrees with itself (Table 1-5's
        // "70 kHz to 2 MHz" vs §2.6.5.2(b)'s "at least 140 kHz wide"), so the
        // note says so and the send goes out regardless — an unresolved
        // conflict is not something a client may enforce.
        var vm = WidebandNet(3);

        vm.LowInput = low;
        vm.HighInput = high;
        vm.CommitBandEdgesCommand.Execute(null);

        Assert.NotEmpty(Transport.SentLines);
        Assert.Equal(advises ? HopSettingsViewModel.MinimumSpanAdvisory : "", vm.InputError);
    }

    [Fact]
    public void ABelowFloorListToken_SENDS_WithTheAdvisory_NotTheRefusal()
    {
        // LIST is advisory-only: no bench trial has ever put a below-floor
        // frequency into a HOPLIST, and §3.1 does not let the client refuse
        // what the wire has not been seen to refuse. The WB refusal's own
        // sentence must be absent — the two are deliberately different.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.ClearSent();

        vm.ListAddInput = "1.995";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Equal(
            ["HOPLIST 3 ADD 01995", "HOPLIST 3", "DIS 3"], Transport.SentLines);
        Assert.Equal(HopSettingsViewModel.ListFloorAdvisory, vm.InputError);
        Assert.DoesNotContain(
            HopSettingsViewModel.BelowHopFloorRefusal, vm.InputError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListSpanAdvisory_IsComputedOverTheSTOREDListUnionTheAddedTokens()
    {
        // The span is a property of the WHOLE list, and adding one frequency to
        // a stored list is exactly how a span gets exceeded — so the added
        // token alone (05000, spanning nothing) must not be what is measured.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");   // the CONFIRMED list
        Transport.ClearSent();

        vm.ListAddInput = "14.000";                  // 14000 - 11010 = 2990 kHz
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Equal(
            ["HOPLIST 3 ADD 14000", "HOPLIST 3", "DIS 3"], Transport.SentLines);
        Assert.Equal(HopSettingsViewModel.ListSpanAdvisory, vm.InputError);
    }

    [Fact]
    public void TheListSpanAdvisory_StaysSilentWhenTheStoredListIsUNCONFIRMED()
    {
        // "Skipped ENTIRELY", not "computed over the added tokens instead": the
        // pane has not been told what is stored, so any span it computed would
        // be a guess about a list it cannot see. The added pair here spans
        // 2990 kHz all by itself, so a span check that fell back to the tokens
        // alone would fire — and this test would catch it.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);                                // LIST confirmed…
        Transport.ClearSent();                        // …but no HOPLIST answer

        vm.ListAddInput = "11.010 14.000";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.Equal(
            ["HOPLIST 3 ADD 11010 14000", "HOPLIST 3", "DIS 3"], Transport.SentLines);
        Assert.Equal("", vm.InputError);
    }

    [Fact]
    public void AListAddWithinTheSpan_CarriesNoNoteAtAll_TheAntiVacuityHalf()
    {
        // Both LIST advisories are only meaningful if a legal add is silent. A
        // note slot that filled on every add would satisfy the two tests above
        // without either rule existing.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010  11015  11020");
        Transport.ClearSent();

        vm.ListAddInput = "11.500";                  // 11500 - 11010 = 490 kHz
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.NotEmpty(Transport.SentLines);
        Assert.Equal("", vm.InputError);
        Assert.False(vm.HasInputError);
    }

    [Fact]
    public void NoListCountAdvisory_Exists_TheManualsFifteenIsBenchOverruled()
    {
        // Explicitly NOT built (plan §4-A2, and ui.md's "the radio is the judge
        // there" ruling stands untouched): the manual's 15-frequency minimum is
        // bench-overruled on this radio — three works. A one-frequency add is
        // as far below fifteen as the box goes, and it says nothing.
        var vm = Vm();
        EnterHop();
        Pick(vm, 3);
        ReportList(3);
        Transport.InjectLine("HOPLIST 03   11010");
        Transport.ClearSent();

        vm.ListAddInput = "11.015";
        vm.AddListFrequenciesCommand.Execute(null);

        Assert.NotEmpty(Transport.SentLines);
        Assert.Equal("", vm.InputError);
    }

    [Fact]
    public void TheNarrowbandCentre_KeepsK6sOwnDomain_016000IsStillLegal()
    {
        // The floor refusal is WB-SCOPED. NB centres below 2 MHz are
        // manual-legal (the 1.6 MHz tier) and no capture refuses one, so K6's
        // 01600 domain is still the honest bound on this entry — a refusal that
        // had leaked across would take 400 kHz of legal centres with it.
        var vm = Vm();
        EnterHop();
        Pick(vm, 5);
        ReportNarrowband(5);
        Transport.ClearSent();

        vm.CenterInput = "1.600";
        vm.CommitCenterCommand.Execute(null);

        Assert.Equal(["HOPSET 5 01600", "DIS 5"], Transport.SentLines);
        Assert.False(vm.HasInputError);
    }

    [Fact]
    public void BothNoteKinds_RAISE_TheBoundNames_NotJustTheBackingField()
    {
        // NOTIFICATION pin. InputError and HasInputError are what the pane
        // BINDS; a note that changed the field without raising the names would
        // leave every value assertion above green while nothing appeared on
        // screen. Pinned on a REFUSAL and on an ADVISORY separately, because
        // they take different paths into the slot (Fail() vs a direct set).
        var vm = WidebandNet(2);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.LowInput = "1.995";
        vm.HighInput = "8.000";
        vm.CommitBandEdgesCommand.Execute(null);      // the REFUSAL path
        Assert.Contains(nameof(HopSettingsViewModel.InputError), raised);
        Assert.Contains(nameof(HopSettingsViewModel.HasInputError), raised);

        raised.Clear();
        vm.LowInput = "2.000";
        vm.HighInput = "4.000";
        vm.CommitBandEdgesCommand.Execute(null);      // the ADVISORY path
        Assert.Equal(HopSettingsViewModel.SpanRefusesGenerationAdvisory, vm.InputError);
        Assert.Contains(nameof(HopSettingsViewModel.InputError), raised);
        Assert.Contains(nameof(HopSettingsViewModel.HasInputError), raised);
    }

    [Fact]
    public void APopulateGesture_ClearsAnAdvisory_LikeAnyOtherNote()
    {
        // Advisories are notes, and notes name the net they were raised on. A
        // picker spin is a populate gesture, so the note goes with the net the
        // operator just left — the advisory inherits that rule rather than
        // inventing a stickier one.
        var vm = WidebandNet(2);

        vm.LowInput = "2.000";
        vm.HighInput = "4.000";
        vm.CommitBandEdgesCommand.Execute(null);
        Assert.True(vm.HasInputError);

        vm.NetUpCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Equal("", vm.InputError);
    }

    // ====================================================================
    // ROUND 14 B — the internal-coupler row (plan/plan-round14.md §4-B, R2).
    //
    // The owner's ask was "copy the SSB settings screen's control buttons", so
    // the CONTRACT is copied too: highlight only on the radio's confirmed
    // report, in the report's own spelling; nothing highlights on anything
    // else; and NO re-click guard — the buttons always send.
    // ====================================================================

    private static ChoiceItem Choice(IEnumerable<ChoiceItem> list, string value)
        => list.Single(c => c.Value == value);

    [Fact]
    public void TheCouplerRow_HighlightsTheCONFIRMEDStateOnly_EitherWay()
    {
        var vm = Vm();
        EnterHop();

        // Nothing reported: nothing lit. Two buttons, both dark — the
        // unreported state has a rendering, and it is not a guess.
        // ROUND 15 H-2: the AFFIRMATIVE is on the left — Enable, then Bypass.
        // Wire-neutral (the setter parses the button LABEL); the click-to-wire
        // pins below are untouched.
        Assert.Equal(["Enable", "Bypass"], vm.InternalCouplerChoices.Select(c => c.Value));
        Assert.All(vm.InternalCouplerChoices, c => Assert.False(c.IsActive));

        // The radio's own MIXED-CASE line; ResponseParser uppercases it before
        // dispatch, so the mirror holds "ENABLED" and the caps compare is the
        // right one (docs/protocol.md; plan §5).
        Transport.InjectLine("INTCoupler Enabled");
        Assert.True(Choice(vm.InternalCouplerChoices, "Enable").IsActive);
        Assert.False(Choice(vm.InternalCouplerChoices, "Bypass").IsActive);

        Transport.InjectLine("INTCoupler Bypassed");
        Assert.True(Choice(vm.InternalCouplerChoices, "Bypass").IsActive);
        Assert.False(Choice(vm.InternalCouplerChoices, "Enable").IsActive);
    }

    [Fact]
    public void TheCouplerRow_HighlightsNOTHING_OnAnUnexpectedSpelling()
    {
        // The honest outcome, inherited from the copied row: an answer in any
        // other spelling is still MIRRORED (the capture is not lost) and lights
        // nothing, rather than being mapped onto a button by guesswork.
        var vm = Vm();
        EnterHop();

        Transport.InjectLine("INTCoupler ON");

        Assert.All(vm.InternalCouplerChoices, c => Assert.False(c.IsActive));
    }

    [Fact]
    public void TheCouplerRow_NotifiesWhenTheMirrorMoves_SoTheButtonsRelight()
    {
        // The NOTIFICATION row. The choices are rebuilt in Refresh, which hangs
        // off the surface's Changed event — which only fires for the coupler
        // because HopSurface WATCHES RadioProperty.InternalCoupler. Without the
        // watch the list would be correct and the screen stale.
        var vm = Vm();
        EnterHop();

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        var before = vm.InternalCouplerChoices;

        Transport.InjectLine("INTCoupler Bypassed");

        Assert.Contains(nameof(HopSettingsViewModel.InternalCouplerChoices), raised);
        Assert.NotSame(before, vm.InternalCouplerChoices);
    }

    [Fact]
    public void TheCouplerButtons_SendTheirSetCommand_AndNothingElse()
    {
        var vm = Vm();
        EnterHop();

        Choice(vm.InternalCouplerChoices, "Bypass").SelectCommand.Execute(null);
        Assert.Equal(["INTCOUPLER BYPASS"], Transport.SentLines);

        Transport.ClearSent();
        Choice(vm.InternalCouplerChoices, "Enable").SelectCommand.Execute(null);
        Assert.Equal(["INTCOUPLER ENABLE"], Transport.SentLines);
    }

    [Fact]
    public void TheCouplerButtons_HaveNO_ReClickGuard()
    {
        // The provisional-trio contract (docs/ui.md), copied whole: pressing
        // the LIT button still sends. This is the one control an operator
        // reaches for precisely when they doubt the reported state, and
        // suppressing the send would be the app deciding the radio has nothing
        // to say. Contrast the type segments on this same pane, which ARE
        // re-click guarded — so this is a decision, not an omission.
        var vm = Vm();
        EnterHop();
        Transport.InjectLine("INTCoupler Enabled");
        Transport.ClearSent();

        Assert.True(Choice(vm.InternalCouplerChoices, "Enable").IsActive);
        Choice(vm.InternalCouplerChoices, "Enable").SelectCommand.Execute(null);

        Assert.Equal(["INTCOUPLER ENABLE"], Transport.SentLines);
    }

    [Fact]
    public void TheCouplerButtons_ObeyThePaneGate_LikeEveryOtherSendHere()
    {
        // Ready-but-not-HOP: the pane is greyed and the press sends nothing.
        // (The COMMAND is not prompt-scoped on the wire — the family is
        // prompt-free, P-1 run C — but the pane is, so the gate is the pane's.)
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Choice(vm.InternalCouplerChoices, "Bypass").SelectCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheCouplerQuery_RidesTheLandingTier_AndNotOneMomentBefore()
    {
        // The read half of the gate: nothing at Ready alone, the whole tier at
        // confirmed HOP, and the query goes out AGAIN on the NEXT landing — it
        // is a per-landing read, not a once-per-session one, because the
        // coupler can be changed from the front panel between landings.
        //
        // It sits BEFORE `EXC` deliberately: that read is sentinel-bracketed
        // (`EXC` … `BAT ST`), and a query slipped between the two would land
        // inside a bracket whose whole job is to say where the answer ended.
        // The second landing shows the other half of that — `EXC` COALESCES
        // while its sentinel is unanswered, and the coupler query does not.
        var vm = Vm();
        ConnectReady();
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("HOP>");
        Assert.Equal(["DIS 0", "INTCOUPLER", "EXC", "BAT ST"], Transport.SentLines);

        Transport.ClearSent();
        vm.NetUpCommand.Execute(null);
        Assert.Equal(["DIS 1", "INTCOUPLER"], Transport.SentLines);
    }
}
