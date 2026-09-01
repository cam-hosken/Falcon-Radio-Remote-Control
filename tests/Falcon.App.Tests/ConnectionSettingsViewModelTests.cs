using System.Collections.Specialized;
using System.ComponentModel;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Session;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// Connection settings rules (GUI rejigger G1/G2/G4): app-side pickers
/// (port, baud, bits, parity, stop) feed <c>CreatePortSettings</c> — what
/// the Connect toggle sends; the page is LOCKED OUT (IsEditable
/// false) unless the phase is Disconnected or Failed; there is no
/// Connect/Disconnect command and no auto-reconnect toggle here anymore.
///
/// <para><b>CLONE ROUND 12 §6 F4</b> adds the down-session port POLL and the
/// selection model it needs. The poll is the reason this file grew: a timer
/// reconciling a bound collection under a Picker, on a platform whose
/// enumeration can raise a permission dialog, has four separate ways to go
/// wrong, and each of them is a row below — permissionless ticks,
/// change-only reconciliation, the interaction deferral, and stopping while
/// the session owns the port. The selection rows pin the other half: which
/// port the picker shows is a PURE FUNCTION of the scan and the operator's
/// own pick, so "the operator's port vanished" reads null instead of
/// silently re-targeting.</para>
///
/// <para>The poll timer is PARKED in every test (an hour) and
/// <c>PollPortsOnceAsync</c> is driven by hand: a real 2 s tick landing
/// mid-assertion would make this file flaky for reasons that have nothing to
/// do with what it pins.</para>
/// </summary>
public class ConnectionSettingsViewModelTests : SessionTestBase
{
    private const int Parked = 3_600_000;

    private readonly FakePortEnumerator _enumerator = new();

    /// <summary>ROUND 14 G: the persistence seam every VM below is built on.
    /// One store per test class instance, so a test can construct a SECOND VM
    /// on it and have that stand in for the next launch.</summary>
    private readonly FakeSettingsStore _store = new();

    private ConnectionSettingsViewModel Vm() => new(Session, _enumerator, _store, pollIntervalMs: Parked);

    /// <summary>What the OPERATOR actually does: the Picker takes focus, a
    /// choice is made, focus leaves. Tests go through this rather than calling
    /// <c>SelectPortByUser</c> bare, because attribution only happens inside
    /// that window (audit round 1) — and because a bare call is not a gesture
    /// any view can produce.</summary>
    private static void OperatorPicks(ConnectionSettingsViewModel vm, string? port)
    {
        vm.BeginPortInteraction();
        vm.SelectPortByUser(port);
        vm.EndPortInteraction();
    }

    // ---- The pre-existing contract (unchanged by §6) ------------------------

    [Fact]
    public void Defaults_Baud9600_8N1_AllOptionsListed()
    {
        var vm = Vm();
        Assert.Equal(9600, vm.SelectedBaud);
        Assert.Equal(new[] { 2400, 4800, 9600, 19200 }, vm.BaudRates);
        Assert.Equal(8, vm.SelectedDataBits);
        Assert.Equal(PortParity.None, vm.SelectedParity);
        Assert.Equal(PortStopBits.One, vm.SelectedStopBits);
        Assert.Equal(new[] { 8, 7 }, vm.DataBitsOptions);
        Assert.Equal(new[] { PortParity.None, PortParity.Even, PortParity.Odd }, vm.ParityOptions);
        Assert.Equal(new[] { PortStopBits.One, PortStopBits.Two }, vm.StopBitsOptions);
    }

    [Fact]
    public async Task RefreshPorts_Populates_AndSelectsFirst()
    {
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();

        await vm.RefreshPortsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "COM3", "COM20" }, vm.AvailablePorts);
        Assert.Equal("COM3", vm.SelectedPort);
    }

    [Fact]
    public async Task RefreshPorts_KeepsSelection_WhenStillPresent()
    {
        // §6 F5: "keeps the selection" now means "keeps the operator's PICK".
        // The pick is made through the explicit API, because a bare
        // SelectedPort assignment is what the model can no longer attribute.
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();
        OperatorPicks(vm, "COM20");

        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.Equal("COM20", vm.SelectedPort);
    }

    [Fact]
    public void CreatePortSettings_NullUntilAPortIsSelected()
    {
        var vm = Vm();
        Assert.Null(vm.CreatePortSettings());
    }

    [Fact]
    public void CreatePortSettings_CarriesEveryPicker()
    {
        // G2 wiring: bits/parity/stop reach the PortSettings used on connect.
        _enumerator.Ports = ["COM7"];
        var vm = Vm();
        OperatorPicks(vm, "COM7");
        vm.SelectedBaud = 4800;
        vm.SelectedDataBits = 7;
        vm.SelectedParity = PortParity.Even;
        vm.SelectedStopBits = PortStopBits.Two;

        var s = vm.CreatePortSettings();

        Assert.NotNull(s);
        Assert.Equal("COM7", s.PortName);
        Assert.Equal(4800, s.BaudRate);
        Assert.Equal(7, s.DataBits);
        Assert.Equal(PortParity.Even, s.Parity);
        Assert.Equal(PortStopBits.Two, s.StopBits);
    }

    [Fact]
    public void Lockout_EditableOnlyWhileDisconnectedOrFailed()
    {
        var vm = Vm();
        Assert.True(vm.IsEditable);                     // Disconnected

        Session.Connect(TestSettings);
        Assert.False(vm.IsEditable);                    // Connecting

        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.False(vm.IsEditable);                    // Ready — locked

        Session.Close();
        Assert.True(vm.IsEditable);                     // back to Disconnected
    }

    [Fact]
    public void Lockout_FailedPhaseIsEditable_ReconnectingIsNot()
    {
        var vm = Vm();
        ConnectReady();

        // Default auto-reconnect is OFF → an unexpected drop lands in Failed.
        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.True(vm.IsEditable);                     // Failed — editable

        // With the (dormant) machinery explicitly enabled, a drop lands in
        // Reconnecting — a live session, so the page stays locked.
        Session.AutoReconnectEnabled = true;
        ConnectReady();
        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.False(vm.IsEditable);
        // Pin the Reconnecting status line too (audit round 1): the branch
        // is reachable whenever explicit code re-enables the dormant poller.
        Assert.Contains("reconnecting", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusText_FollowsPhase()
    {
        var vm = Vm();
        Assert.Equal("Disconnected", vm.StatusText);

        ConnectReady();
        Assert.Contains("Connected", vm.StatusText);
        Assert.Contains("COM7", vm.StatusText);

        // Default auto-reconnect OFF: a drop reads as failed, not reconnecting.
        Transport.InjectError(new IOException("drop"));
        Assert.Contains("failed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- §6 F4: the poll ----------------------------------------------------

    [Fact]
    public void ColdStart_ListsPortsWithoutARefreshPress()
    {
        // The reported defect the poll exists to fix: the picker was EMPTY
        // until the operator found the Refresh button. The page now scans as
        // soon as it can, and the app opens on this page.
        _enumerator.Ports = ["COM3"];
        var vm = Vm();

        Assert.Equal(new[] { "COM3" }, vm.AvailablePorts);
        Assert.True(vm.IsPolling);
    }

    [Fact]
    public async Task PollTicks_TakeThePassivePath_AndNeverRequestPermission()
    {
        // THE reason the seam split. On Android the gesture path requests USB
        // permission for every unpermissioned device; a 2 s timer on that path
        // is a permission dialog every 2 s with no gesture behind it.
        _enumerator.Ports = ["COM3"];
        var vm = Vm();                                   // one cold-start tick

        await vm.PollPortsOnceAsync();
        await vm.PollPortsOnceAsync();

        Assert.Equal(3, _enumerator.PassiveCalls);
        Assert.Equal(0, _enumerator.GestureCalls);
        Assert.Equal(0, _enumerator.PermissionRequests);

        // …and the GESTURE stays on the gesture path, which is the only way a
        // grant is ever requested at all.
        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.Equal(1, _enumerator.GestureCalls);
        Assert.Equal(1, _enumerator.PermissionRequests);
    }

    [Fact]
    public async Task Reconciliation_IsChangeOnly_AndAnUnchangedScanRaisesNoEvents()
    {
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();

        int events = 0;
        ((INotifyCollectionChanged)vm.AvailablePorts).CollectionChanged += (_, _) => events++;

        await vm.PollPortsOnceAsync();
        await vm.PollPortsOnceAsync();
        Assert.Equal(0, events);                         // nothing moved

        // One arrival = one event, and the surviving entries are the SAME
        // instances in place — never a Clear/rebuild, which is what makes an
        // open Picker safe.
        _enumerator.Ports = ["COM3", "COM9", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal(1, events);
        Assert.Equal(new[] { "COM3", "COM9", "COM20" }, vm.AvailablePorts);

        // One departure = one event.
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal(2, events);
        Assert.Equal(new[] { "COM3", "COM20" }, vm.AvailablePorts);
    }

    [Fact]
    public async Task ScansAreDeferredWhileThePickerIsOpen_AndTheLatestAppliesOnEnd()
    {
        _enumerator.Ports = ["COM3"];
        var vm = Vm();

        vm.BeginPortInteraction();

        _enumerator.Ports = ["COM3", "COM9"];
        await vm.PollPortsOnceAsync();
        _enumerator.Ports = ["COM3", "COM9", "COM20"];
        await vm.PollPortsOnceAsync();

        // Nothing moved under the operator's finger.
        Assert.Equal(new[] { "COM3" }, vm.AvailablePorts);

        vm.EndPortInteraction();

        // …and what lands is the LATEST scan, not a replay of both.
        Assert.Equal(new[] { "COM3", "COM9", "COM20" }, vm.AvailablePorts);
    }

    [Fact]
    public void EndingAnInteractionWithNothingQueued_ChangesNothing()
    {
        _enumerator.Ports = ["COM3"];
        var vm = Vm();

        int events = 0;
        ((INotifyCollectionChanged)vm.AvailablePorts).CollectionChanged += (_, _) => events++;

        vm.BeginPortInteraction();
        vm.EndPortInteraction();

        Assert.Equal(0, events);
        Assert.Equal(new[] { "COM3" }, vm.AvailablePorts);
    }

    [Fact]
    public async Task ThePollIsSingleFlight()
    {
        // SendIt's Interlocked idiom: a scan that is still out must not be
        // joined by the next tick. Held open with the fake's gate — with a
        // synchronous fake the two calls could never overlap at all.
        _enumerator.Ports = ["COM3"];
        var vm = Vm();                                   // cold-start tick, completed

        var gate = new TaskCompletionSource<IReadOnlyList<string>>();
        _enumerator.PassiveGate = gate;

        var first = vm.PollPortsOnceAsync();
        int callsWithOneInFlight = _enumerator.PassiveCalls;

        await vm.PollPortsOnceAsync();                    // must no-op
        Assert.Equal(callsWithOneInFlight, _enumerator.PassiveCalls);

        _enumerator.PassiveGate = null;
        gate.SetResult(["COM3", "COM9"]);
        await first;

        // …and the latch RELEASES: the next tick really scans.
        await vm.PollPortsOnceAsync();
        Assert.Equal(callsWithOneInFlight + 1, _enumerator.PassiveCalls);
    }

    [Fact]
    public void ThePollStopsWhileTheSessionIsUp_AndResumesWhenItGoesDown()
    {
        // The open-session presence poller owns the port while it is open;
        // two enumerators on one port help nobody.
        _enumerator.Ports = ["COM7"];
        var vm = Vm();
        Assert.True(vm.IsPolling);                       // Disconnected

        Session.Connect(TestSettings);
        Assert.False(vm.IsPolling);                      // Connecting

        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.False(vm.IsPolling);                      // Ready

        Session.AutoReconnectEnabled = true;
        Transport.InjectError(new IOException("drop"));
        Assert.Equal(SessionPhase.Reconnecting, Session.Phase);
        Assert.False(vm.IsPolling);                      // Reconnecting

        Session.Close();
        Assert.True(vm.IsPolling);                       // down again
    }

    /// <summary>A SynchronizationContext that only QUEUES. Posted work waits
    /// until a test drains it by hand, which is what makes "the timer's work
    /// arrived through the dispatcher" an observable fact rather than an
    /// implementation detail.</summary>
    private sealed class QueueingSyncContext : SynchronizationContext
    {
        private readonly object _lock = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PendingCount { get { lock (_lock) return _queue.Count; } }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_lock) _queue.Enqueue((d, state));
        }

        /// <summary>Blocking marshalling is NOT the seam under test, and a
        /// timer thread that used it would deadlock a real UI thread.</summary>
        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("the poll must POST, never Send");

        /// <summary>Run everything queued RIGHT NOW (snapshotted, so a timer
        /// still ticking cannot turn this into a spin).</summary>
        public void Drain()
        {
            (SendOrPostCallback Callback, object? State)[] batch;
            lock (_lock)
            {
                batch = [.. _queue];
                _queue.Clear();
            }
            foreach (var item in batch) item.Callback(item.State);
        }
    }

    [Fact]
    public void ATimerTick_ReachesTheBoundCollectionOnlyThroughTheCapturedContext()
    {
        // AUDIT ROUND 1 — the pin this file was MISSING. Every other test here
        // parks the timer and calls PollPortsOnceAsync by hand, so all of them
        // stayed green when the marshalling Post was deleted: the whole
        // "UI-DISPATCHED" half of §6 F4 was unguarded. A collection bound to a
        // visible Picker mutated from a timer thread is the crash this rule
        // exists to prevent, so the pin uses a REAL tick and a context that
        // refuses to run anything on its own.
        var context = new QueueingSyncContext();
        var previous = SynchronizationContext.Current;
        ConnectionSettingsViewModel vm;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // 40 ms so a real tick lands inside the wait below. The cold-start
            // scan runs INLINE on this thread (not posted) and finds nothing.
            vm = new ConnectionSettingsViewModel(Session, _enumerator, _store, pollIntervalMs: 40);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using var scope = vm;
        Assert.Empty(vm.AvailablePorts);

        // Now give the timer something to find.
        _enumerator.Ports = ["COM3"];

        Assert.True(WaitUntil(() => context.PendingCount > 0),
            "no work was POSTED to the captured context — the timer tick reached the ViewModel "
            + "on its own thread, which is exactly the defect this pins");

        // …and it is STILL only queued: nothing has touched the bound
        // collection, because nothing has run the dispatcher yet.
        Assert.Empty(vm.AvailablePorts);

        context.Drain();
        Assert.Equal(new[] { "COM3" }, vm.AvailablePorts);
    }

    [Fact]
    public async Task APollFailureIsSilent_WhileARefreshFailureIsReported()
    {
        // A background tick must not overwrite the phase line the operator is
        // reading; a button they pressed must say what happened.
        _enumerator.Ports = ["COM3"];
        var vm = Vm();

        _enumerator.ThrowOnEnumerate = new IOException("registry hiccup");
        await vm.PollPortsOnceAsync();
        Assert.Equal("Disconnected", vm.StatusText);

        _enumerator.ThrowOnEnumerate = new IOException("registry hiccup");
        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.Contains("Port scan failed", vm.StatusText, StringComparison.Ordinal);
    }

    // ---- §6 F5: the selection model -----------------------------------------

    [Fact]
    public void AutoSelection_TakesTheFirstRealPort_AndDemoOnlyWhenNothingIsReal()
    {
        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var demoOnly = Vm();
        Assert.Equal(DemoSerialPort.DemoPortName, demoOnly.SelectedPort);

        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        var withReal = Vm();
        Assert.Equal("COM3", withReal.SelectedPort);

        // Auto-selection is not a preference — nothing was chosen.
        Assert.Null(withReal.PreferredPort);
    }

    [Fact]
    public async Task ARealPortStealsFromAutoSelectedDemo_ButNeverFromTheOperatorsPick()
    {
        // The whole point of the split. Same arrival, two outcomes, decided
        // solely by whether a human chose the DEMO port.
        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var auto = Vm();
        Assert.Equal(DemoSerialPort.DemoPortName, auto.SelectedPort);

        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        await auto.PollPortsOnceAsync();
        Assert.Equal("COM3", auto.SelectedPort);         // the cable wins

        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var chosen = Vm();
        OperatorPicks(chosen, DemoSerialPort.DemoPortName);

        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        await chosen.PollPortsOnceAsync();
        Assert.Equal(DemoSerialPort.DemoPortName, chosen.SelectedPort);
    }

    [Fact]
    public async Task APreferredPortSurvivesDisappearance_ReadsNullWhileAbsent_AndRestores()
    {
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();
        OperatorPicks(vm, "COM20");

        // Gone: NULL, never a silent re-target onto COM3.
        _enumerator.Ports = ["COM3"];
        await vm.PollPortsOnceAsync();
        Assert.Null(vm.SelectedPort);
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Null(vm.CreatePortSettings());

        // Back: restored without the operator touching anything.
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM20", vm.SelectedPort);
    }

    [Fact]
    public void APreferenceSurvivesASession()
    {
        _enumerator.Ports = ["COM3", "COM7"];
        var vm = Vm();
        OperatorPicks(vm, "COM7");

        ConnectReady();
        Session.Close();

        // The down-transition rescans; the operator's port is still theirs.
        Assert.Equal("COM7", vm.PreferredPort);
        Assert.Equal("COM7", vm.SelectedPort);
    }

    [Fact]
    public async Task APreferenceResetsOnlyOnANewExplicitPick()
    {
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();
        OperatorPicks(vm, "COM20");

        // Scans do not reset it, present or absent.
        _enumerator.Ports = ["COM3"];
        await vm.PollPortsOnceAsync();
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM20", vm.PreferredPort);

        OperatorPicks(vm, "COM3");
        Assert.Equal("COM3", vm.PreferredPort);
        Assert.Equal("COM3", vm.SelectedPort);
    }

    [Fact]
    public async Task AProgrammaticSelectionIsNeverRecordedAsAUserPick()
    {
        // The view cannot tell the two apart: a Picker raises its selection
        // event for the VM's OWN write as well as for a tap, and the
        // code-behind calls SelectPortByUser either way. So the VM refuses the
        // re-entrant one — pinned by reproducing exactly that echo, on the
        // synchronous path the real binding takes.
        var vm = Vm();                                   // nothing plugged in
        vm.PropertyChanged += Echo;

        _enumerator.Ports = ["COM3"];
        await vm.PollPortsOnceAsync();

        Assert.Equal("COM3", vm.SelectedPort);           // auto-selected…
        Assert.Null(vm.PreferredPort);                   // …and NOT preferred

        vm.PropertyChanged -= Echo;

        // …while a real pick, arriving outside any write of ours, is recorded.
        OperatorPicks(vm, "COM3");
        Assert.Equal("COM3", vm.PreferredPort);

        void Echo(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConnectionSettingsViewModel.SelectedPort))
                vm.SelectPortByUser(vm.SelectedPort);
        }
    }

    // ---- Audit round 1: attribution belongs to the INTERACTION WINDOW ------
    //
    // MAUI's Picker raises SelectedIndexChanged for its OWN index
    // recalculation — adding or removing an item re-derives the selection and
    // clamps it — so the view's handler fires with no human involved. The
    // view cannot tell those apart; the VM can, because only a human is inside
    // Begin/EndPortInteraction. Both failures below were REPRODUCED before the
    // fix, and both are modelled here through a fake that echoes exactly what
    // the real Picker echoes.

    /// <summary>The view, faithfully: a Picker re-raises its selection event
    /// when the bound collection changes AND when the selection is written
    /// programmatically, and the code-behind calls
    /// <c>SelectPortByUser</c> for every one of them.</summary>
    private sealed class PickerEcho : IDisposable
    {
        private readonly ConnectionSettingsViewModel _vm;
        private readonly string? _clampTo;

        /// <summary>How many echoes were raised — the anti-vacuity counter, so
        /// a scenario that quietly stopped exercising the path cannot pass.</summary>
        public int Fired { get; private set; }

        /// <param name="clampTo">What the Picker reports when the selected row
        /// disappears (a real Picker clamps onto a surviving neighbour rather
        /// than reporting nothing). Null echoes the current selection.</param>
        public PickerEcho(ConnectionSettingsViewModel vm, string? clampTo = null)
        {
            _vm = vm;
            _clampTo = clampTo;
            ((INotifyCollectionChanged)vm.AvailablePorts).CollectionChanged += OnItemsChanged;
            vm.PropertyChanged += OnPropertyChanged;
        }

        /// <summary>ROUND 14 G — the operator TAPS a row. Two things reach the
        /// VM and NEITHER is a focus event: the two-way
        /// <c>SelectedItem="{Binding SelectedPort}"</c> binding writes the
        /// property itself, and the code-behind's <c>SelectedIndexChanged</c>
        /// handler calls <c>SelectPortByUser</c>
        /// (<c>SettingsPage.xaml.cs:35</c>). Focus events are the view's to
        /// raise, and the field report says the Windows Picker does not raise
        /// them around a tap — see the R18 diagnosis below.</summary>
        public void Tap(string? port)
        {
            _vm.SelectedPort = port;
            Fired++;
            _vm.SelectPortByUser(port);
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Fired++;
            _vm.SelectPortByUser(_clampTo ?? _vm.SelectedPort);
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ConnectionSettingsViewModel.SelectedPort)) return;
            Fired++;
            _vm.SelectPortByUser(_vm.SelectedPort);
        }

        public void Dispose()
        {
            ((INotifyCollectionChanged)_vm.AvailablePorts).CollectionChanged -= OnItemsChanged;
            _vm.PropertyChanged -= OnPropertyChanged;
        }
    }

    [Fact]
    public async Task ACollectionDrivenSelectionEvent_NeverMakesAutoSelectedDemoPreferred()
    {
        // REPRODUCED FAILURE 1: inserting a real port ahead of an
        // auto-selected DEMO shifted DEMO's index, the Picker re-raised, and
        // DEMO became PREFERRED — after which no cable could ever steal it,
        // which is the exact opposite of the rule the insert was implementing.
        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var vm = Vm();
        Assert.Equal(DemoSerialPort.DemoPortName, vm.SelectedPort);

        using var picker = new PickerEcho(vm);

        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        await vm.PollPortsOnceAsync();

        Assert.True(picker.Fired > 0, "the simulated Picker never echoed — this scenario is not being exercised");
        Assert.Null(vm.PreferredPort);
        Assert.Equal("COM3", vm.SelectedPort);
    }

    [Fact]
    public async Task APreferredPortDisappearing_IsNeverClampedOntoItsNeighbour()
    {
        // REPRODUCED FAILURE 2: when the operator's port vanished the Picker
        // clamped onto a neighbour and re-raised, so the NEIGHBOUR silently
        // became the preference — instead of the selection reading null and
        // restoring when the real port came back.
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();
        OperatorPicks(vm, "COM20");

        using var picker = new PickerEcho(vm, clampTo: "COM3");

        _enumerator.Ports = ["COM3"];
        await vm.PollPortsOnceAsync();

        Assert.True(picker.Fired > 0, "the simulated Picker never echoed — this scenario is not being exercised");
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Null(vm.SelectedPort);

        // …and the restore still works, which is what the preference was for.
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM20", vm.SelectedPort);
    }

    [Fact]
    public void AGenuinePickInsideTheWindow_IsStillAttributed()
    {
        // The other direction, with the SAME echoing view attached: the fix
        // must not have bought its safety by refusing real gestures too.
        _enumerator.Ports = ["COM3", "COM20"];
        var vm = Vm();
        using var picker = new PickerEcho(vm);

        vm.BeginPortInteraction();
        vm.SelectPortByUser("COM20");
        vm.EndPortInteraction();

        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Equal("COM20", vm.SelectedPort);
    }

    [Fact]
    public void ASelectionEventThatOnlyRestatesTheAppsOwnChoice_IsNotAPick()
    {
        // SUPERSEDES `ASelectionEventOutsideAnyWindow_IsNotAPick` (round 14 G,
        // R18 — plan §3.5: a guard the change invalidates breaks in the same
        // commit, with the reason).
        //
        // The old pin read: no window, no attribution, full stop. That rule is
        // what the owner's report refutes — on the platform they run, a real
        // tap arrives with no window around it, so "no window" was throwing
        // away the gesture along with the echoes. What ACTUALLY separates the
        // two is not the window but DIVERGENCE: an echo restates the value the
        // app itself just resolved, a tap names a different one. This pins the
        // echo half; the tap half is pinned in the R18 section at the end of
        // this file, and the app's-own-churn half by the two PickerEcho
        // scenarios above (which are the shapes that made the old rule
        // necessary in the first place).
        //
        // The scenario is a real one: binding a Picker's ItemsSource makes it
        // re-derive its index and raise, so the app's own auto-selection comes
        // straight back through the view's handler at page-construction time.
        // Attributing it would make an auto-selected DEMO "preferred" and no
        // cable could ever steal it.
        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var vm = Vm();
        Assert.Equal(DemoSerialPort.DemoPortName, vm.SelectedPort);   // auto-selected

        vm.SelectPortByUser(vm.SelectedPort);                         // the bind-time re-assert

        Assert.Null(vm.PreferredPort);
        Assert.Equal(0, _store.Writes);                               // and nothing was remembered

        // …and the auto-selection it was competing with is still auto: a real
        // port arriving still steals it, which is the behaviour the old pin
        // was protecting and this one keeps.
        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        vm.SelectPortByUser(vm.SelectedPort);                         // one more echo, then the scan
        _ = vm.PollPortsOnceAsync();
        Assert.Equal("COM3", vm.SelectedPort);
        Assert.Null(vm.PreferredPort);
    }

    [Fact]
    public void AutoSelectionNeverAutoConnects()
    {
        _enumerator.Ports = ["COM3"];
        var vm = Vm();

        Assert.Equal("COM3", vm.SelectedPort);
        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal(0, Transport.OpenCount);
    }

    // ---- §6 F4/F7: what the selection model does to the Connect toggle ------

    /// <summary>The five CONNECT-GUARD rows (§6 F4). They live here rather
    /// than with the toggle's own tests because every one of them is really a
    /// statement about the port lifecycle — the toggle just reads it.</summary>
    [Fact]
    public async Task TheConnectToggle_FollowsTheSelectionLifecycle()
    {
        var vm = Vm();
        var toggle = new ConnectToggleViewModel(Session, vm);

        // (1) no port at all → disabled.
        Assert.False(toggle.ToggleCommand.CanExecute(null));

        // (2) AUTO-selection is enough to enable it (it is a real selection).
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM3", vm.SelectedPort);
        Assert.True(toggle.ToggleCommand.CanExecute(null));

        // (3) the OPERATOR's port disappearing → disabled (there is nothing
        //     legitimate to connect to; re-targeting silently is what this
        //     model refuses).
        OperatorPicks(vm, "COM20");
        _enumerator.Ports = ["COM3"];
        await vm.PollPortsOnceAsync();
        Assert.Null(vm.SelectedPort);
        Assert.False(toggle.ToggleCommand.CanExecute(null));

        // (4) …and reappearing → enabled again.
        _enumerator.Ports = ["COM3", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.True(toggle.ToggleCommand.CanExecute(null));

        // (5) a LIVE session can always be disconnected — even with the port
        //     gone from the list, which is precisely when the operator needs
        //     the button most.
        ConnectReady();
        _enumerator.Ports = [];
        await vm.PollPortsOnceAsync();
        Assert.True(toggle.ToggleCommand.CanExecute(null));
        Assert.Equal("Disconnect", toggle.Label);
    }

    // =========================================================================
    // OWNER RULING (2026-08-21, round 14 G audit round 1): PRESSING CONNECT
    // ALSO CLAIMS THE SELECTED PORT — "the port you pick, or the port you
    // connect to, is remembered".
    //
    // The gap it answers: a real operator cannot claim the port the app
    // auto-selected, because re-picking the current row produces no changed
    // index and an echoed handler call is refused as a restatement. Connecting
    // to it is an unmistakable gesture, so the ruling makes THAT the claim.
    //
    // The rows below are the ruling's four boundaries — the claim happens on
    // the button and only there, it never erases, disconnect claims nothing —
    // plus the consequence the owner accepted with it.
    // =========================================================================

    [Fact]
    public async Task TheOwnerRuling_PressingConnect_ClaimsTheSelectedPort()
    {
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        var toggle = new ConnectToggleViewModel(Session, vm);
        Assert.Equal("COM10", vm.SelectedPort);          // auto-selected, unclaimed
        Assert.Null(vm.PreferredPort);

        // ON THE PRESS, AND THE ORDER IS THE POINT (audit round 2, MAJOR).
        // Asserting after the command completes cannot tell "before the
        // attempt" from "after it": moving the claim below the blocking
        // Task.Run survived all 1979 tests, because this fake's Open returns
        // instantly and the phase reads Connecting either way. A real port
        // open is the slowest thing the gesture does, so the pin HOLDS ONE
        // OPEN and asserts the claim has already landed while it is stuck —
        // the failure being pinned is a hung open delaying persistence past an
        // app exit, which forgets a press the operator made.
        using var openGate = new ManualResetEventSlim(false);
        Transport.OpenGate = openGate;
        var press = toggle.ToggleCommand.ExecuteAsync(null);
        try
        {
            Assert.True(WaitUntil(() => Transport.OpenAttempts > 0),
                "the connect attempt never reached the transport — the gate is pinning nothing");
            Assert.Equal(0, Transport.OpenCount);        // …and it is still stuck in there

            Assert.Equal("COM10", vm.PreferredPort);
            Assert.Equal("COM10", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
        }
        finally
        {
            openGate.Set();
        }
        await press;
        Transport.OpenGate = null;

        Assert.Equal("COM10", vm.PreferredPort);
        Assert.Equal("COM10", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
        // Never conditional on success: the session is only CONNECTING here,
        // and a connect that fails outright must still leave the port claimed.
        Assert.Equal(SessionPhase.Connecting, Session.Phase);
        // …and the next launch seeds itself from it. (PreferredPort, not
        // SelectedPort: a VM built while the session is live does not scan —
        // the G4 lockout stands the poll down — so there is nothing to resolve
        // against yet.)
        Assert.Equal("COM10", Vm().PreferredPort);
    }

    [Fact]
    public async Task TheOwnerRuling_PhaseTransitionsWithoutTheButton_ClaimNothing()
    {
        // THE CONSTRAINT THAT KEEPS THE RULING SAFE. The claim is wired to the
        // BUTTON, never to SessionPhase — a phase-driven claim would make every
        // automatic path self-claiming, and a restored port would confirm
        // itself with no human involved.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        _ = new ConnectToggleViewModel(Session, vm);

        ConnectReady();                                  // Connecting → Ready, no button
        Assert.Null(vm.PreferredPort);
        Transport.InjectError(new IOException("drop"));  // → Failed
        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.Null(vm.PreferredPort);

        // …and an auto-selection that merely happens, over and over, is still
        // not a gesture.
        _enumerator.Ports = ["COM20"];
        await vm.PollPortsOnceAsync();
        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();

        Assert.Null(vm.PreferredPort);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task TheOwnerRuling_AnInertPress_ClaimsNothing_AndNEVER_ErasesWhatIsStored()
    {
        // The null guard. Recording a null FORGETS the key, so a press with
        // nothing selected must leave the store exactly as it was — and the
        // one state where that is reachable is a remembered port that is not
        // plugged in, which is precisely when losing it would hurt.
        _store.Seed(ConnectionSettingsViewModel.PreferredPortKey, "COM20");
        _enumerator.Ports = ["COM10"];
        var vm = Vm();
        var toggle = new ConnectToggleViewModel(Session, vm);
        Assert.Null(vm.SelectedPort);                    // preferred port absent → NULL
        Assert.False(toggle.ToggleCommand.CanExecute(null));

        await toggle.ToggleCommand.ExecuteAsync(null);   // executed anyway (guard re-check)

        Assert.Equal("COM20", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Equal(0, _store.Writes);
        Assert.Equal(0, Transport.OpenCount);

        // …and the VM's OWN guard, not just the toggle's. The press above never
        // reaches the claim (ToggleAsync returns at its null settings check),
        // so deleting the guard inside ClaimSelectedPortAsPreference survived
        // the whole suite — mutation-derived. It is public API: called in this
        // state without the guard it records NULL, which FORGETS the key, and
        // the state it is reachable in is "the remembered port is unplugged" —
        // precisely when losing it would hurt most.
        vm.ClaimSelectedPortAsPreference();

        Assert.Equal("COM20", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task TheOwnerRuling_DisconnectingClaimsNothing()
    {
        // The other branch of the same button. Tearing a session down says
        // nothing about which port the operator wants next time.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        var toggle = new ConnectToggleViewModel(Session, vm);
        OperatorPicks(vm, "COM20");
        int writesAfterThePick = _store.Writes;

        ConnectReady();                                  // live, without the button
        Assert.Equal("Disconnect", toggle.Label);
        await toggle.ToggleCommand.ExecuteAsync(null);   // the DISCONNECT branch

        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal(writesAfterThePick, _store.Writes);
        Assert.Equal("COM20", vm.PreferredPort);
    }

    [Fact]
    public async Task TheOwnerRuling_ConnectingToAnAutoSelectedPort_IncludingDemo_ClaimsIt_SoARealCableNoLongerSteals()
    {
        // THE KNOWING CONSEQUENCE, named because the owner accepted it and not
        // because it is desirable in the abstract: connect to an auto-selected
        // port and it becomes STICKY, so the "a real cable steals from an
        // auto-selected DEMO" rule stops applying to it. The rule itself is
        // untouched for the NOT-connected case — see
        // ARealPortStealsFromAutoSelectedDemo_ButNeverFromTheOperatorsPick,
        // which is the same scenario with no press in it.
        _enumerator.Ports = [DemoSerialPort.DemoPortName];
        var vm = Vm();
        var toggle = new ConnectToggleViewModel(Session, vm);
        Assert.Equal(DemoSerialPort.DemoPortName, vm.SelectedPort);
        Assert.Null(vm.PreferredPort);                   // auto, not chosen

        await toggle.ToggleCommand.ExecuteAsync(null);   // the operator connects to DEMO
        Session.Close();

        Assert.Equal(DemoSerialPort.DemoPortName, vm.PreferredPort);

        // A real cable arrives — and does NOT steal, because DEMO is now the
        // operator's, exactly as if they had picked it from the list.
        _enumerator.Ports = ["COM3", DemoSerialPort.DemoPortName];
        await vm.PollPortsOnceAsync();

        Assert.Equal(DemoSerialPort.DemoPortName, vm.SelectedPort);
        Assert.Equal(DemoSerialPort.DemoPortName, Vm().SelectedPort);   // and next launch
    }

    // =========================================================================
    // ROUND 14 PHASE G1 (plan/plan-round14.md R18) — THE PORT IS REMEMBERED.
    //
    // "I set it to 20, but every time open the app it's back to 10." The
    // preference was memory-only, so every launch started with none and the
    // first-REAL-port rule landed on COM10. The pick now writes an
    // ISettingsStore and the constructor seeds itself back out of it; the F5
    // rules downstream are untouched, which is what these pins hold.
    // =========================================================================

    [Fact]
    public void TheRememberedPort_IsRestoredAtLaunch_AndSelectedWithNoGesture()
    {
        // The owner's first sentence, inverted. COM10 is present and would win
        // outright if the store were still empty.
        _store.Seed(ConnectionSettingsViewModel.PreferredPortKey, "COM20");
        _enumerator.Ports = ["COM10", "COM20"];

        var vm = Vm();

        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal("COM20", vm.CreatePortSettings()?.PortName);
        Assert.Equal(0, _enumerator.GestureCalls);       // no Refresh press
        Assert.Equal(0, _store.Writes);                  // reading a launch is not a write
    }

    [Fact]
    public void AnEmptyStore_LeavesTheFirstRealPortRuleExactlyAsItWas()
    {
        // The fallback is not "COM10 by default" — it is the UNCHANGED F5 rule,
        // reached only because nothing is remembered.
        _enumerator.Ports = ["COM10", "COM20"];

        var vm = Vm();

        Assert.Null(vm.PreferredPort);
        Assert.Equal("COM10", vm.SelectedPort);
    }

    [Fact]
    public async Task ARememberedPortThatIsNotPluggedIn_ReadsNull_AndRestoresWhenItArrives()
    {
        // F5 survives the launch boundary intact: a remembered port that is
        // ABSENT resolves to NULL, never a silent re-target onto the cable that
        // happens to be in. This is the rule that makes a restored preference
        // safe to seed before the first scan.
        _store.Seed(ConnectionSettingsViewModel.PreferredPortKey, "COM20");
        _enumerator.Ports = ["COM10"];

        var vm = Vm();
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Null(vm.SelectedPort);
        Assert.Null(vm.CreatePortSettings());

        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM20", vm.SelectedPort);
    }

    [Fact]
    public void TheOperatorsPick_WritesTheStore_InTheSameGesture()
    {
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        Assert.Equal(0, _store.Writes);                  // auto-selection remembers NOTHING

        OperatorPicks(vm, "COM20");

        Assert.Equal(1, _store.Writes);
        Assert.Equal("COM20", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));

        // …and the reset semantics persist too: a new pick REPLACES it.
        OperatorPicks(vm, "COM10");
        Assert.Equal("COM10", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
    }

    [Fact]
    public void TheDemoPort_PersistsLikeAnyOtherPick()
    {
        // DEMO is a pick like any other (plan §Phase G) — including the part
        // where a real cable does NOT steal it back on the next launch.
        _enumerator.Ports = ["COM10", DemoSerialPort.DemoPortName];
        var first = Vm();
        OperatorPicks(first, DemoSerialPort.DemoPortName);
        Assert.Equal(DemoSerialPort.DemoPortName, _store.Get(ConnectionSettingsViewModel.PreferredPortKey));

        var nextLaunch = Vm();

        Assert.Equal(DemoSerialPort.DemoPortName, nextLaunch.PreferredPort);
        Assert.Equal(DemoSerialPort.DemoPortName, nextLaunch.SelectedPort);
    }

    [Fact]
    public async Task ARestoredOrReSelectedPort_NEVER_ConnectsByItself()
    {
        // THE ROUND-12 MANUAL-RECONNECT RULING, across both of this phase's
        // paths. Remembering a port and re-selecting one are SELECTION;
        // connecting is an operator gesture and stays one. A restore that
        // opened the port would be an auto-connect on launch — the single
        // worst thing this phase could ship.
        _store.Seed(ConnectionSettingsViewModel.PreferredPortKey, "COM20");
        _enumerator.Ports = ["COM10", "COM20"];

        var vm = Vm();
        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal(0, Transport.OpenCount);

        // The re-select path: gone, then back, with nobody touching anything.
        _enumerator.Ports = ["COM10"];
        await vm.PollPortsOnceAsync();
        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();

        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal(SessionPhase.Disconnected, Session.Phase);
        Assert.Equal(0, Transport.OpenCount);
    }

    [Fact]
    public async Task TheReSelectAndThePick_BothRaisePropertyChangedForSelectedPort()
    {
        // The notification row. SelectedPort is BOUND, and every route that
        // moves it in this phase — the app's re-select after a replug, and an
        // attributed tap — has to tell the view, or the picker shows a port the
        // VM no longer believes in.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionSettingsViewModel.SelectedPort))
                raised.Add(vm.SelectedPort);
        };

        OperatorPicks(vm, "COM20");                      // the pick
        _enumerator.Ports = ["COM10"];
        await vm.PollPortsOnceAsync();                   // absent → null
        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();                   // back → re-selected

        Assert.Equal(["COM20", null, "COM20"], raised);
    }

    // =========================================================================
    // ROUND 14 PHASE G (plan/plan-round14.md R18) — "IT FORGETS COM20".
    //
    // The owner's report, verbatim: "I set it to 20, but every time open the
    // app it's back to 10. if I disconnect the cable and reconnect, I have to
    // refresh and select 20 again because 10 stays selected."
    //
    // The second sentence is the one that names the root cause, and it names
    // it by CONTRADICTING the F5 model. With a preference of COM20 and COM20
    // absent from the scan, ResolveSelection returns NULL — never COM10. A
    // picker sitting on COM10 after the yank is therefore proof that
    // PreferredPort was null, i.e. that the operator's pick was NEVER
    // ATTRIBUTED. SelectPortByUser has exactly two refusals, and a human tap
    // cannot be inside `_applyingSelection`, so the refusal was the CLOSED
    // INTERACTION WINDOW: on this platform the Picker's focus events do not
    // bracket the tap the way the round-12 design assumed.
    //
    // The two tests below are the diagnosis, in the F1 order: the same
    // sequence through the channel that DOES attribute (green as built, which
    // acquits the three candidates the plan named), then through the channel
    // the VIEW actually uses (red).
    // =========================================================================

    /// <summary>Connect on what the page currently offers, driven to Ready —
    /// the operator's own Connect press, which is the only thing that ever
    /// opens a port (round-12 manual-reconnect ruling).</summary>
    private void ConnectOn(ConnectionSettingsViewModel vm, string expectedPort)
    {
        var settings = vm.CreatePortSettings();
        Assert.Equal(expectedPort, settings?.PortName);
        Session.Connect(settings!);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.False(vm.IsPolling);                      // the session owns the port
        Transport.ClearSent();
    }

    [Fact]
    public async Task TheOwnersYankAndReplug_ReSelectsTheOperatorsPort_WithNoRefreshAndNoRePick()
    {
        // DIAGNOSIS STEP 1. The owner's exact sequence, driven through the
        // ATTRIBUTED channel. It passes AS BUILT, and that is the finding: all
        // three candidates the plan named are ACQUITTED — the poll's phase
        // gating re-arms on the Failed edge and rescans immediately, the G4
        // lockout treats Failed as editable, and nothing in session teardown
        // touches the preference. Kept as a permanent pin of the reconnect
        // half: re-SELECTING is automatic, re-CONNECTING stays a gesture.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        Assert.Equal("COM10", vm.SelectedPort);          // no preference: first REAL port

        OperatorPicks(vm, "COM20");
        ConnectOn(vm, "COM20");

        // The yank: the cable is out, so the scan loses COM20 and the session
        // fails (auto-reconnect is OFF).
        _enumerator.Ports = ["COM10"];
        Transport.InjectError(new IOException("cable yanked"));
        Assert.Equal(SessionPhase.Failed, Session.Phase);
        Assert.True(vm.IsPolling);                       // Failed is editable → the poll re-arms
        Assert.Null(vm.SelectedPort);                    // absent → NULL, never COM10

        // The replug, with nobody pressing anything.
        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();

        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.Equal(0, _enumerator.GestureCalls);       // no Refresh press
        Assert.Equal(SessionPhase.Failed, Session.Phase);   // and no auto-CONNECT
    }

    [Fact]
    public async Task TheOwnersSequence_ThroughTheViewsOwnTapPath_LosesThePick_AndFallsBackToCOM10()
    {
        // DIAGNOSIS STEP 2 — THE REPRODUCTION. Identical to the test above
        // except for HOW the pick arrives: through PickerEcho.Tap, which is
        // what SettingsPage.xaml.cs really delivers — the two-way SelectedItem
        // binding's own write plus the SelectedIndexChanged handler — with no
        // focus event around it.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        using var picker = new PickerEcho(vm);
        Assert.Equal("COM10", vm.SelectedPort);

        picker.Tap("COM20");
        Assert.Equal("COM20", vm.SelectedPort);          // it LOOKS like it took…

        ConnectOn(vm, "COM20");                          // …and it connects on COM20

        _enumerator.Ports = ["COM10"];
        Transport.InjectError(new IOException("cable yanked"));
        Assert.Equal(SessionPhase.Failed, Session.Phase);

        // THE REPORTED DEFECT: "10 stays selected". Under F5 a preferred port
        // that is absent reads NULL, so COM10 here IS the missing preference.
        Assert.Null(vm.SelectedPort);

        // …and the replug cannot restore what was never recorded, which is why
        // the owner has to "refresh and select 20 again".
        _enumerator.Ports = ["COM10", "COM20"];
        await vm.PollPortsOnceAsync();
        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal("COM20", vm.PreferredPort);
        Assert.True(picker.Fired > 0, "the simulated Picker never echoed — this scenario is not being exercised");
    }

    [Fact]
    public void ATapThroughTheViewsOwnPath_IsRemembered_ForTheNextLaunch()
    {
        // G1 AND G2 JOINED, which is the only combination that fixes the owner's
        // first sentence: persistence stores PreferredPort, and PreferredPort is
        // only ever written by an ATTRIBUTED pick. With attribution broken on
        // the view's real path, G1 alone would have persisted nothing and the
        // whole phase would have shipped dead.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        using var picker = new PickerEcho(vm);

        picker.Tap("COM20");

        Assert.Equal("COM20", _store.Get(ConnectionSettingsViewModel.PreferredPortKey));
        Assert.Equal("COM20", Vm().SelectedPort);        // the next launch

        // ONE GESTURE, ONE WRITE. A tap arrives with SelectedPort ALREADY set
        // by the two-way binding, so the assertion this VM records has to be
        // updated even when the assignment is a no-op — otherwise every later
        // echo of the same value still "diverges" from a stale assertion and
        // is re-attributed as a fresh pick, re-writing the store on each one.
        // (Mutation-derived: recording the assertion after the no-op check
        // survived every other pin in the suite.)
        vm.SelectPortByUser("COM20");                    // one more echo, no window
        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public async Task ATappedPort_SurvivesTheNextPollTick()
    {
        // The same root cause seen two seconds earlier, and the reason the
        // owner could still connect on COM20 at all: an unattributed tap wins
        // only until the next reconciliation re-asserts the app's own
        // auto-selection over it.
        _enumerator.Ports = ["COM10", "COM20"];
        var vm = Vm();
        using var picker = new PickerEcho(vm);

        picker.Tap("COM20");
        await vm.PollPortsOnceAsync();

        Assert.Equal("COM20", vm.SelectedPort);
        Assert.Equal("COM20", vm.PreferredPort);
    }
}
