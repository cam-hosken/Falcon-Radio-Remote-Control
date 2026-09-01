using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// Settings → Radio port section (Stage 11): the read-only PORT_R dump
/// display truth, and the wizard's §3 guarded-flow rules — explicit target
/// selection with no default, a TYPED confirmation that cannot be defaulted
/// through, gestures via commands only, programmatic state writes send
/// nothing.
/// </summary>
public class RadioPortViewModelTests : SessionTestBase
{
    private readonly BaudChangeFlow _flow;

    public RadioPortViewModelTests()
        => _flow = new BaudChangeFlow(Radio, Session) { DropTimeoutMs = 100, ReopenDelayMs = 25 };

    private RadioPortViewModel Vm() => new(new PortSurface(Radio), _flow, Session);

    // ---- Dump display truth --------------------------------------------------

    [Fact]
    public void Dump_UnreportedRendersDashes_NeverADefault()
    {
        var vm = Vm();
        Assert.Equal("—", vm.BaudText);
        Assert.Equal("—", vm.BitsText);
        Assert.Equal("—", vm.ParityText);
        Assert.Equal("—", vm.StopText);
        Assert.Equal("—", vm.EchoText);
        Assert.Equal("—", vm.XonXoffText);
    }

    [Fact]
    public void Dump_RendersTheVerbatimR1Report()
    {
        ConnectReady();
        var vm = Vm();

        // Verbatim R1 capture (probes.md).
        Transport.InjectLine("PORT_REMOTE BAUD 9600");
        Transport.InjectLine("PORT_REMOTE BITS 8");
        Transport.InjectLine("PORT_REMOTE PARITY none");
        Transport.InjectLine("PORT_REMOTE STOP 1");
        Transport.InjectLine("PORT_REMOTE ECHO OFF");
        Transport.InjectLine("PORT_REMOTE XON_XOFF disable");

        Assert.Equal("9600", vm.BaudText);
        Assert.Equal("8", vm.BitsText);
        Assert.Equal("NONE", vm.ParityText);        // parser uppercases payloads
        Assert.Equal("1", vm.StopText);
        Assert.Equal("OFF", vm.EchoText);
        Assert.Equal("DISABLE", vm.XonXoffText);
    }

    [Fact]
    public void ProgrammaticStateWrite_SendsNothing()
    {
        ConnectReady();
        var vm = Vm();
        Transport.InjectLine("PORT_REMOTE BAUD 9600");
        Assert.Empty(Transport.SentLines);   // display repaint only
    }

    // ---- Wizard guarding (§3: no accidental triggers) ---------------------------

    [Fact]
    public void OpenWizard_NeverInheritsSelectionOrConfirmation()
    {
        ConnectReady();
        var vm = Vm();

        vm.OpenWizardCommand.Execute(null);
        vm.SelectedTarget = 4800;
        vm.ConfirmationText = "4800";
        vm.CancelWizardCommand.Execute(null);
        Assert.False(vm.IsWizardOpen);

        vm.OpenWizardCommand.Execute(null);
        Assert.True(vm.IsWizardOpen);
        Assert.Null(vm.SelectedTarget);          // no default target
        Assert.Equal("", vm.ConfirmationText);   // confirmation must be re-typed
        Assert.Empty(Transport.SentLines);       // opening sends nothing
    }

    [Fact]
    public void Start_DisabledUntilTheTypedConfirmationMatchesTheTarget()
    {
        ConnectReady();
        var vm = Vm();
        vm.OpenWizardCommand.Execute(null);

        Assert.False(vm.StartCommand.CanExecute(null));           // nothing selected
        vm.SelectedTarget = 4800;
        Assert.False(vm.StartCommand.CanExecute(null));           // not typed
        vm.ConfirmationText = "9600";
        Assert.False(vm.StartCommand.CanExecute(null));           // wrong rate typed
        vm.ConfirmationText = "4800";
        Assert.True(vm.StartCommand.CanExecute(null));

        // The guarded gesture itself has sent NOTHING yet.
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Start_BypassedGuard_ExecuteWithMismatchedConfirmation_SendsNothing()
    {
        // ICommand.Execute does not consult CanExecute — the body must
        // repeat the guard (constitution).
        //
        // ROUND 10 (§5, owner ruling 9): the Core token gate that used to
        // stand behind this guard is GONE — confirmation for the baud change
        // is a GUI concern now. So this VM guard is no longer belt-and-braces;
        // it is the ONLY stop, which is exactly why the dormant feature keeps
        // it (plan §2 ruling 9) and why this pin matters more than it did.
        ConnectReady();
        var vm = Vm();
        vm.OpenWizardCommand.Execute(null);
        vm.SelectedTarget = 4800;
        vm.ConfirmationText = "9600";

        vm.StartCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        // The bypassed gesture must not even REACH the flow. Round 10: with
        // the Core gate removed, a deleted body guard would now put
        // "PORT_R BAUD 4800" on the wire — the flow's own preflight has no
        // opinion about what the operator typed. Both halves are asserted:
        // nothing sent AND the flow untouched.
        Assert.Equal(BaudChangeState.Idle, _flow.State);
    }

    [Fact]
    public void Start_WithTypedConfirmation_DrivesTheWhitelistedBuilder()
    {
        ConnectReady();
        var vm = Vm();
        vm.OpenWizardCommand.Execute(null);
        vm.SelectedTarget = 4800;
        vm.ConfirmationText = " 4800 ";          // operator whitespace trimmed

        vm.StartCommand.Execute(null);

        Assert.Equal(["PORT_R BAUD 4800", "BAT ST"], Transport.SentLines);
        Assert.True(vm.IsFlowRunning);
        Assert.False(vm.CancelWizardCommand.CanExecute(null));   // no cancel mid-flight
    }

    [Fact]
    public void Start_NotReady_StaysDisabled()
    {
        var vm = Vm();                            // never connected
        vm.OpenWizardCommand.Execute(null);
        vm.SelectedTarget = 4800;
        vm.ConfirmationText = "4800";
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("Not connected", vm.StartDisabledReason);
    }

    [Fact]
    public void FlowProgress_FlowsOneWayIntoTheDisplay()
    {
        ConnectReady();
        var vm = Vm();
        vm.OpenWizardCommand.Execute(null);
        vm.SelectedTarget = 4800;
        vm.ConfirmationText = "4800";
        vm.StartCommand.Execute(null);

        Assert.Contains("drop", vm.FlowStatusText);   // WaitingForDrop text

        AnswerSentinel();   // the radio answered → the flow fails honestly
        Assert.False(vm.IsFlowRunning);
        Assert.True(vm.IsFlowFailed);
        Assert.Contains("still answered", vm.FlowStatusText);
    }
}
