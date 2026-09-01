using Falcon.App.Core.Session;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// The Connect ⇄ Disconnect toggle (GUI rejigger N2): the label is
/// phase-driven ("Connect" while Disconnected/Failed, "Disconnect" while
/// the session is live in any way); the control is greyed only while no
/// port is selected; Connect sends the LAST-SELECTED app-side
/// settings (port, baud, bits, parity, stop) from the Connection settings
/// VM; Disconnect closes the session.
///
/// <para>CLONE ROUND 12 §6 F2 moved this button's VIEW from the shell title
/// bar onto the Connection settings page. The VM is untouched — which is
/// what these tests keep saying. The one thing that DID change under them is
/// how a port becomes selected (§6 F5): selection is now a function of the
/// scan and the operator's explicit pick, so the fixture stocks the
/// enumerator and picks through <c>SelectPortByUser</c> instead of assigning
/// <c>SelectedPort</c>, and the poll timer is parked.</para>
/// </summary>
public class ConnectToggleViewModelTests : SessionTestBase
{
    private readonly FakePortEnumerator _enumerator = new();
    private readonly ConnectionSettingsViewModel _settings;

    public ConnectToggleViewModelTests()
        => _settings = new ConnectionSettingsViewModel(
            Session, _enumerator, new FakeSettingsStore(), pollIntervalMs: 3_600_000);

    private ConnectToggleViewModel Vm() => new(Session, _settings);

    /// <summary>Plug a port in and have the OPERATOR choose it — the gesture
    /// the toggle's enablement is really about.</summary>
    private void OperatorPicksCom7()
    {
        _enumerator.Ports = ["COM7"];
        // Through the INTERACTION WINDOW, because that is the only place a
        // pick is attributed (audit round 1) — and the only shape a real view
        // can produce.
        _settings.BeginPortInteraction();
        _settings.SelectPortByUser("COM7");
        _settings.EndPortInteraction();
    }

    [Fact]
    public void Initially_LabelConnect_GreyedWithoutAPort()
    {
        var vm = Vm();
        Assert.Equal("Connect", vm.Label);
        Assert.False(vm.ToggleCommand.CanExecute(null));   // no port ever selected
    }

    [Fact]
    public void SelectingAPort_EnablesTheToggle()
    {
        var vm = Vm();
        OperatorPicksCom7();
        Assert.True(vm.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public async Task Toggle_Connects_WithTheLastSelectedSettings()
    {
        var vm = Vm();
        OperatorPicksCom7();
        _settings.SelectedBaud = 4800;
        _settings.SelectedDataBits = 7;
        _settings.SelectedParity = PortParity.Even;
        _settings.SelectedStopBits = PortStopBits.Two;

        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(SessionPhase.Connecting, Session.Phase);
        Assert.Equal("COM7", Transport.LastSettings?.PortName);
        Assert.Equal(4800, Transport.LastSettings?.BaudRate);
        Assert.Equal(7, Transport.LastSettings?.DataBits);
        Assert.Equal(PortParity.Even, Transport.LastSettings?.Parity);
        Assert.Equal(PortStopBits.Two, Transport.LastSettings?.StopBits);
    }

    [Fact]
    public void Label_FollowsThePhase()
    {
        var vm = Vm();
        OperatorPicksCom7();

        Session.Connect(TestSettings);
        Assert.Equal("Disconnect", vm.Label);              // Connecting = live

        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Equal("Disconnect", vm.Label);

        Session.Close();
        Assert.Equal("Connect", vm.Label);
    }

    [Fact]
    public async Task Toggle_WhileReady_Disconnects()
    {
        var vm = Vm();
        OperatorPicksCom7();
        ConnectReady();

        Assert.True(vm.ToggleCommand.CanExecute(null));
        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.False(Transport.IsOpen);
        Assert.Equal("Connect", vm.Label);
    }

    [Fact]
    public void Failed_LabelConnect_EnabledWithAPort()
    {
        var vm = Vm();
        OperatorPicksCom7();
        ConnectReady();

        // Default auto-reconnect OFF → unexpected drop lands in Failed.
        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Failed, Session.Phase);

        Assert.Equal("Connect", vm.Label);
        Assert.True(vm.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public async Task Toggle_GreyedAndInert_WithNoPortEverSelected()
    {
        var vm = Vm();

        // Even if executed despite CanExecute (guard re-check), nothing opens.
        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal(0, Transport.OpenCount);
        Assert.Empty(Transport.SentLines);
    }
}
