using System.ComponentModel;
using Falcon.App.Core.Services;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

public class ConsoleViewModelTests : SessionTestBase
{
    private ConsoleViewModel Vm() => new(new ConsoleFeed(Radio, Session));

    /// <summary>D18: the same view model WITH the campaign signal — the real
    /// coordinator, because the thing under test is its edges and a fake would
    /// only re-implement them.</summary>
    private ConsoleViewModel Vm(CampaignWireCoordinator wire)
        => new(new ConsoleFeed(Radio, Session), wire);

    [Fact]
    public void TxAndRxLines_AppearWithBadges()
    {
        var vm = Vm();
        ConnectReady();
        Radio.Show();                          // TX "SH"
        Transport.InjectLine("CHAN 00 ");      // RX line

        Assert.Contains(vm.Entries, l => l.Badge == "TX" && l.Text == "SH");
        Assert.Contains(vm.Entries, l => l.Badge == "RX" && l.Text == "CHAN 00 ");
    }

    [Fact]
    public void CompensationWrites_AreVisible()
    {
        var vm = Vm();
        ConnectReady();

        // Trigger-table row (a), real path: a MODEM change silently alters
        // AGC/BAND; the re-poll fires at the next SSB prompt — and must be
        // visible in the Console (principle #4: no silent writes).
        Transport.InjectLine("MODEM OFF");     // first sight — no trigger
        Transport.InjectLine("MODEM 1 T39");   // change — arms the re-poll
        Transport.InjectLine("SSB>");          // re-poll fires here

        Assert.Contains(vm.Entries, l => l.Badge == "AUTO" && l.Text.Contains("MODEM"));
        Assert.Contains(vm.Entries, l => l.Badge == "TX");   // the re-poll commands themselves
    }

    [Fact]
    public void ErrorLines_AreLogged()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectLine("** ERROR **");

        Assert.Contains(vm.Entries, l => l.Badge == "ERR");
    }

    [Fact]
    public void Buffer_IsBounded()
    {
        var vm = Vm();
        ConnectReady();

        for (int i = 0; i < ConsoleViewModel.MaxEntries + 50; i++)
            Transport.InjectLine("KEY OFF ");

        Assert.Equal(ConsoleViewModel.MaxEntries, vm.Entries.Count);
    }

    [Fact]
    public void Pause_FreezesView_ResumeCatchesUp()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("KEY OFF ");
        int before = vm.Entries.Count;

        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Transport.InjectLine("CHAN 00 ");
        Transport.InjectLine("POWER low");
        Assert.Equal(before, vm.Entries.Count);          // view frozen

        vm.TogglePauseCommand.Execute(null);
        Assert.False(vm.IsPaused);
        Assert.Equal(before + 2, vm.Entries.Count);      // nothing lost
        Assert.Contains(vm.Entries, l => l.Text == "POWER low");
    }

    // ---- Stage 8: text filter (view-only; backing buffer keeps everything) --

    [Fact]
    public void Filter_NarrowsTheView_ClearingRestoresIt()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("CHAN 00 ");
        Transport.InjectLine("POWER low");
        Transport.InjectLine("KEY OFF ");

        vm.FilterText = "power";                            // case-blind substring
        Assert.Single(vm.Entries, l => l.Text.Contains("POWER"));
        Assert.DoesNotContain(vm.Entries, l => l.Text.Contains("CHAN"));

        vm.FilterText = "";
        Assert.Contains(vm.Entries, l => l.Text.Contains("CHAN"));
        Assert.Contains(vm.Entries, l => l.Text.Contains("KEY OFF"));
    }

    [Fact]
    public void Filter_MatchesBadges_SoTxFiltersByKind()
    {
        var vm = Vm();
        ConnectReady();
        Radio.Show();                                       // TX SH
        Transport.InjectLine("CHAN 00 ");                   // RX
        Transport.InjectLine("POWER low");                  // RX

        // Badge match: "TX" keeps only sent lines (none of the injected
        // texts contains "TX"). A text filter like "ERR" would also match
        // RX lines whose TEXT contains ERROR — text OR badge, by design.
        vm.FilterText = "TX";
        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, l => Assert.Equal("TX", l.Badge));
    }

    [Fact]
    public void Filter_AppliesToNewLines_AsTheyArrive()
    {
        var vm = Vm();
        ConnectReady();
        vm.FilterText = "POWER";

        Transport.InjectLine("CHAN 00 ");
        Transport.InjectLine("POWER low");

        Assert.Single(vm.Entries, l => l.Badge == "RX");
        Assert.Contains(vm.Entries, l => l.Text == "POWER low");
    }

    [Fact]
    public void Filter_WhilePaused_RefiltersTheFrozenSetOnly()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("POWER low");

        vm.TogglePauseCommand.Execute(null);
        Transport.InjectLine("POWER med");                  // pending, not shown

        vm.FilterText = "POWER";
        Assert.Single(vm.Entries, l => l.Text.StartsWith("POWER"));   // frozen set only

        vm.TogglePauseCommand.Execute(null);                // resume catches up
        Assert.Equal(2, vm.Entries.Count(l => l.Text.StartsWith("POWER")));
    }

    // ======================================================================
    // D19 — the FULL-SESSION EXPORT
    // (plan-clone-write-structural.md §2, 2026-08-30; from the breaker: the
    // 2026-08-30 live gate's failing write could not be diagnosed because the
    // export drew from the same 500-line store as the display and the failure
    // window had already scrolled out of it)
    // ======================================================================

    /// <summary>
    /// D19, THE LOAD-BEARING PIN. A session longer than the display's cap
    /// exports WHOLE while the display still holds its 500. Under the old
    /// source (<c>GetFullLogText</c>, the display's own store) this cannot
    /// pass: the first 200 lines were gone from that store by the end, and
    /// <c>note-0000</c> — asserted absent from the VISIBLE log and present at
    /// the head of the export — is exactly one of them.
    /// </summary>
    [Fact]
    public void TheExport_CarriesTheWholeSession_WhileTheDisplayHolds500_D19()
    {
        // The VM (and therefore its feed) is built AFTER the connect ritual so
        // the session log starts at this test's own first line.
        ConnectReady();
        var vm = Vm();

        // A RECOGNISED line with a unique payload, so each injection is
        // exactly ONE console row (an unparseable line would also raise an ERR
        // row and there would be two rows per line to reason about).
        const int Lines = 700;                              // 200 past the cap
        for (int i = 0; i < Lines; i++)
            Transport.InjectLine($"BFO +{1000 + i}");

        // THE DISPLAY IS UNTOUCHED: still exactly 500, still missing its head.
        Assert.Equal(ConsoleViewModel.MaxEntries, vm.Entries.Count);
        Assert.DoesNotContain("BFO +1000", vm.GetLogText());

        // THE EXPORT carries all 700, oldest first.
        var exported = vm.GetSessionLogText().Split(Environment.NewLine);
        Assert.Equal(Lines, exported.Length);
        for (int i = 0; i < Lines; i++)
            Assert.Contains($"BFO +{1000 + i}", exported[i], StringComparison.Ordinal);
    }

    /// <summary>D19: lines that arrived while the display was PAUSED are in the
    /// export. The standing incident workflow is Pause → Store, and the
    /// incident lines are exactly the ones held behind the frozen view.
    /// (Audit F1's property, re-pinned against the new source.)</summary>
    [Fact]
    public void TheExport_WhilePaused_CarriesTheHeldLines_D19()
    {
        ConnectReady();
        var vm = Vm();
        Transport.InjectLine("CHAN 00 ");

        vm.TogglePauseCommand.Execute(null);
        Transport.InjectLine("POWER low");                  // held, not shown
        Transport.InjectLine("KEY OFF ");                   // held, not shown

        Assert.DoesNotContain("POWER low", vm.GetLogText());  // the view IS frozen
        var text = vm.GetSessionLogText();
        Assert.Contains("CHAN 00", text);
        Assert.Contains("POWER low", text);
        Assert.Contains("KEY OFF", text);
        // Arrival order, which is the feed's own order and not the display's.
        Assert.True(text.IndexOf("CHAN 00", StringComparison.Ordinal)
                    < text.IndexOf("POWER low", StringComparison.Ordinal));
        Assert.True(text.IndexOf("POWER low", StringComparison.Ordinal)
                    < text.IndexOf("KEY OFF", StringComparison.Ordinal));
    }

    /// <summary>D19: lines FILTERED OUT of the display are in the export — a
    /// leftover filter must never silently narrow a bench report. Copy stays
    /// what-you-see.</summary>
    [Fact]
    public void TheExport_IgnoresTheFilter_D19()
    {
        ConnectReady();
        var vm = Vm();
        Transport.InjectLine("CHAN 00 ");
        Transport.InjectLine("POWER low");

        vm.FilterText = "POWER";
        Assert.DoesNotContain("CHAN", vm.GetLogText());     // Copy = what you see
        var text = vm.GetSessionLogText();
        Assert.Contains("CHAN", text);                      // Store/Share = everything
        Assert.Contains("POWER", text);
    }

    /// <summary>
    /// D19: THE FORMAT DID NOT MOVE, only the source. The export renders every
    /// entry through the same <c>ConsoleLine</c> the display builds, so one
    /// line's exported text is byte-identical — timestamp, badge padding and
    /// all — to what the display shows and what the old export wrote. The
    /// committed field captures and the diagnosis tooling compare formats.
    /// </summary>
    [Fact]
    public void TheExportedLine_IsByteIdenticalToTheDisplaysOwnRendering_D19()
    {
        ConnectReady();
        var vm = Vm();
        Transport.InjectLine("CHAN 00 ");

        var shown = Assert.Single(vm.Entries);
        Assert.Equal(shown.ToString(), vm.GetSessionLogText());
    }

    /// <summary>
    /// D19: the rolling cap DROPS THE OLDEST. A log that dropped the newest
    /// would throw away the failure it exists to record. Pinned through the
    /// <c>SessionCap</c> test hook rather than by allocating 100k lines.
    /// </summary>
    [Fact]
    public void TheSessionLog_RollsFromTheFront_D19()
    {
        ConnectReady();
        var feed = new ConsoleFeed(Radio, Session) { SessionCap = 3 };
        var vm = new ConsoleViewModel(feed);

        for (int i = 1; i <= 5; i++) Transport.InjectLine($"BFO +{1000 + i}");

        var exported = vm.GetSessionLogText().Split(Environment.NewLine);
        Assert.Equal(3, exported.Length);
        Assert.Contains("BFO +1003", exported[0], StringComparison.Ordinal);
        Assert.Contains("BFO +1004", exported[1], StringComparison.Ordinal);
        Assert.Contains("BFO +1005", exported[2], StringComparison.Ordinal);

        // ANTI-VACUITY: the DISPLAY has its own, separate cap and still holds
        // all five — the two budgets are independent.
        Assert.Equal(5, vm.Entries.Count);
    }

    /// <summary>D19: the shipped cap is the plan's 100k, and it is what an
    /// un-hooked feed actually uses — the pin above must not be measuring a
    /// default that ships at 3.</summary>
    [Fact]
    public void TheShippedSessionCap_Is100k_D19()
    {
        Assert.Equal(100_000, ConsoleFeed.MaxSessionEntries);
        Assert.Equal(ConsoleFeed.MaxSessionEntries, new ConsoleFeed(Radio, Session).SessionCap);
    }

    [Fact]
    public void GetLogText_ContainsTimestampedLines()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("KEY OFF ");

        var text = vm.GetLogText();
        Assert.Contains("RX", text);
        Assert.Contains("KEY OFF", text);
    }

    // ======================================================================
    // D18 — the GATED RAW-COMMAND INPUT
    // (plan-clone-write-structural.md §2, 2026-08-30; owner "do it")
    // ======================================================================

    /// <summary>D18: THE DEFAULT. A fresh view model is DISARMED, and there is
    /// nothing anywhere that could have made it otherwise — the gate is never
    /// persisted, so "off at every app run" is a property of construction and
    /// nothing else.</summary>
    [Fact]
    public void TheInputGate_StartsClosed_OnAFreshViewModel()
    {
        Assert.False(Vm().InputEnabled);
        Assert.False(Vm(new CampaignWireCoordinator()).InputEnabled);

        // …and the box starts empty, so CanSend is false for TWO reasons on a
        // fresh VM and neither one is doing all the work.
        Assert.Equal("", Vm().InputText);
        Assert.False(Vm().CanSend);
    }

    /// <summary>D18: THE TRUTH TABLE, cell by cell. Send is possible only when
    /// the gate is armed, the box holds something that is not whitespace, and
    /// no clone campaign owns the wire.</summary>
    [Theory]
    // armed  text            campaign  → CanSend
    [InlineData(false, "SH", false, false)]     // disarmed: the gate is shut
    [InlineData(true, "", false, false)]        // armed, empty box
    [InlineData(true, "   ", false, false)]     // armed, whitespace only
    [InlineData(true, "SH", false, true)]       // armed, text, quiet wire → YES
    [InlineData(true, "SH", true, false)]       // armed, text, campaign running
    [InlineData(false, "", true, false)]        // nothing true at all
    public void CanSend_IsArmedAndNonBlankAndNoCampaign(
        bool armed, string text, bool campaign, bool expected)
    {
        var wire = new CampaignWireCoordinator();
        var vm = Vm(wire);

        // The lease is taken BEFORE arming, so the disarm-on-start edge (below)
        // cannot be what this cell is measuring.
        IDisposable? lease = campaign ? wire.Enter() : null;
        vm.InputEnabled = armed;
        vm.InputText = text;

        Assert.Equal(expected, vm.CanSend);
        Assert.Equal(expected, vm.SendCommand.CanExecute(null));
        lease?.Dispose();
    }

    /// <summary>D18: ONE SEND, trimmed, through the queue, and the box clears.
    /// The TX row is the feed's own — no echo of the typed text, so the log
    /// shows what reached the wire.</summary>
    [Fact]
    public void Send_PutsTheTrimmedLineOnTheWireOnce_AndClearsTheBox()
    {
        // The VM is built AFTER the connect ritual on purpose: the ritual's own
        // TX rows would otherwise sit in the log and the "one TX row" half of
        // this pin would be counting them.
        ConnectReady();
        var vm = Vm();

        vm.InputEnabled = true;
        vm.InputText = "  MODEM PRE  ";
        vm.SendCommand.Execute(null);

        Assert.Equal(1, Transport.CountSent("MODEM PRE"));       // trimmed, once
        Assert.DoesNotContain("  MODEM PRE  ", Transport.SentLines);
        Assert.Equal("", vm.InputText);
        Assert.False(vm.CanSend);                       // the box emptied itself

        // ONE console row for the one line — the TX row from LineSent, and no
        // second row echoing what was typed.
        Assert.Single(vm.Entries, l => l.Badge == "TX" && l.Text == "MODEM PRE");
        Assert.Single(vm.Entries.Where(l => l.Badge == "TX"));
    }

    /// <summary>D18: the gate is the VIEW MODEL'S, not the view's.
    /// <c>RelayCommand.Execute</c> does not consult <c>CanExecute</c>, so a
    /// press that arrives anyway — a stale enabled state, a test, a future
    /// caller — must still be refused HERE.</summary>
    [Theory]
    [InlineData(false, "SH")]        // disarmed
    [InlineData(true, "   ")]        // whitespace only
    public void Send_WhenTheGateIsShut_PutsNothingOnTheWire(bool armed, string text)
    {
        var vm = Vm();
        ConnectReady();

        vm.InputEnabled = armed;
        vm.InputText = text;
        vm.SendCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Equal(text, vm.InputText);               // nothing consumed, either
    }

    [Fact]
    public void Send_DuringACampaign_PutsNothingOnTheWire()
    {
        var wire = new CampaignWireCoordinator();
        var vm = Vm(wire);
        ConnectReady();

        vm.InputEnabled = true;
        vm.InputText = "SH";
        Assert.True(vm.CanSend);                        // the control: it WOULD send

        using (wire.Enter())
        {
            vm.SendCommand.Execute(null);
            Assert.Empty(Transport.SentLines);
        }
    }

    /// <summary>
    /// D18, THE BELT — and the half a CanSend pin cannot prove: the campaign
    /// START drops the TOGGLE itself, so the button reads "Enable input" again
    /// and the operator has to press it. A gate that merely blocked and then
    /// silently re-opened at campaign end is a gate nobody is watching.
    /// </summary>
    [Fact]
    public void ACampaignStart_DisarmsTheToggle_AndTheEndDoesNotReArmIt()
    {
        var wire = new CampaignWireCoordinator();
        var vm = Vm(wire);
        ConnectReady();

        vm.InputEnabled = true;
        vm.InputText = "SH";

        var lease = wire.Enter();
        Assert.False(vm.InputEnabled);                  // the TOGGLE dropped
        Assert.False(vm.CanSend);
        Assert.Equal("SH", vm.InputText);               // the typed line survives

        lease.Dispose();
        Assert.False(vm.InputEnabled);                  // …and stays down
        Assert.False(vm.CanSend);

        // Re-arming is a deliberate press, and it works.
        vm.ToggleInputCommand.Execute(null);
        Assert.True(vm.InputEnabled);
        Assert.True(vm.CanSend);
    }

    [Fact]
    public void ToggleInput_FlipsTheGateBothWays()
    {
        var vm = Vm();
        vm.ToggleInputCommand.Execute(null);
        Assert.True(vm.InputEnabled);
        vm.ToggleInputCommand.Execute(null);
        Assert.False(vm.InputEnabled);
    }

    /// <summary>D18: every BOUND property raises. A gate the view never hears
    /// about is a gate that is only true in the view model.</summary>
    [Fact]
    public void TheGateProperties_RaisePropertyChanged()
    {
        var wire = new CampaignWireCoordinator();
        var vm = Vm(wire);
        var raised = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.InputEnabled = true;
        Assert.Contains(nameof(ConsoleViewModel.InputEnabled), raised);
        Assert.Contains(nameof(ConsoleViewModel.CanSend), raised);

        raised.Clear();
        vm.InputText = "SH";
        Assert.Contains(nameof(ConsoleViewModel.InputText), raised);
        Assert.Contains(nameof(ConsoleViewModel.CanSend), raised);

        // The campaign edges move CanSend with no property of ours changing on
        // the way in, so they raise it by hand — both directions.
        raised.Clear();
        var lease = wire.Enter();
        Assert.Contains(nameof(ConsoleViewModel.InputEnabled), raised);
        Assert.Contains(nameof(ConsoleViewModel.CanSend), raised);

        raised.Clear();
        lease.Dispose();
        Assert.Contains(nameof(ConsoleViewModel.CanSend), raised);
    }

    /// <summary>D18: the send is a NORMAL send — it goes through the transport
    /// write queue like every other line, so a closed port swallows it exactly
    /// the way it swallows the rest and nothing pretends otherwise in the
    /// log.</summary>
    [Fact]
    public void Send_BeforeTheSessionIsOpen_ReachesNoWireAndLogsNoTxRow()
    {
        var vm = Vm();                                  // NOT connected

        vm.InputEnabled = true;
        vm.InputText = "SH";
        vm.SendCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.DoesNotContain(vm.Entries, l => l.Badge == "TX");
        Assert.Equal("", vm.InputText);                 // the box still clears
    }
}
