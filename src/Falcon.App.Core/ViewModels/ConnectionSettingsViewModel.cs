using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.Core.Transport;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Connection settings page (GUI rejigger G1/G2/G4): port picker, baud
/// 2400/4800/9600/19200 (default 9600; 19200 is APP-SIDE only — owner ask
/// 2026-08-30 — the radio-port reconfigure flow keeps its own captured set),
/// and the app-side BITS / PARITY / STOP
/// pickers (G2 — <see cref="PortSettings"/> already carries them). All
/// pickers bind two-way to APP-side session settings — legal; the §2.4
/// two-way ban is on radio state.
///
/// G1: Connect/Disconnect is a separate small VM
/// (<see cref="ConnectToggleViewModel"/>), which reads the last-selected
/// settings here via <see cref="CreatePortSettings"/>. The auto-reconnect
/// toggle is gone (owner ruling: auto-reconnect is OFF; the session keeps
/// the machinery dormant). CLONE ROUND 12 §6 F2 moved that button's VIEW
/// onto this page, full width, under the last card — the VM split is
/// unchanged.
///
/// G4: the page is LOCKED OUT while connected — <see cref="IsEditable"/>
/// is true only in the Disconnected/Failed phases, and every input binds
/// its IsEnabled to it (port settings cannot change under a live session).
///
/// <para><b>CLONE ROUND 12 §6 F4 — the port poll.</b> While the session is
/// DOWN the page enumerates every <see cref="PollIntervalMs"/> (SendIt's 2 s
/// cadence; SendIt's Interlocked single-flight idiom) so plugging a cable in
/// populates the picker without a Refresh press — the list used to be EMPTY
/// until the operator found that button. Four rules make the poll safe:</para>
/// <list type="bullet">
///   <item><b>Permissionless.</b> The tick calls
///     <see cref="ISerialPort.GetAvailablePortsPassiveAsync"/>, never the
///     gesture path — on Android the gesture path raises the USB permission
///     dialog, and a dialog every two seconds with no gesture behind it is
///     not a feature. Refresh and Connect keep the gesture path.</item>
///   <item><b>Change-only, UI-dispatched.</b> Reconciliation adds and
///     removes IN PLACE (never Clear/rebuild — a Picker bound to a
///     collection that empties itself under an open dropdown is the classic
///     crash), and an unchanged scan raises ZERO collection events. The
///     timer marshals onto the captured <see cref="SynchronizationContext"/>
///     before touching anything.</item>
///   <item><b>Deferred while the operator is in the Picker.</b>
///     <see cref="BeginPortInteraction"/> /
///     <see cref="EndPortInteraction"/> (wired from the Picker's
///     focus/unfocus) queue the latest scan and apply it on End.</item>
///   <item><b>Stopped while the session is up.</b> Connecting / Ready /
///     Reconnecting belong to the open-session presence poller in
///     WindowsSerialPort; two enumerators on one port help nobody.</item>
/// </list>
///
/// <para><b>The selection model (F5).</b> A two-way <c>SelectedPort</c>
/// binding cannot say WHO set it, and the difference matters: a port the
/// OPERATOR chose must not be silently re-targeted when it disappears, while
/// a port the app auto-selected must yield to a real cable being plugged in.
/// So the user gesture has its own entry point —
/// <see cref="SelectPortByUser"/>, called from the view. It is one of exactly
/// TWO entry points that write <see cref="PreferredPort"/>, and both are
/// operator GESTURES funnelling through one recorder: the pick, and the
/// CONNECT press (<see cref="ClaimSelectedPortAsPreference"/>, owner ruling
/// 2026-08-21 — "the port you pick, or the port you connect to, is
/// remembered"). Nothing else writes it; the app's own reconciliation never
/// does. From there the selection is a PURE FUNCTION of the scan (see
/// <see cref="ResolveSelection"/>): with a preference, that port or NULL while
/// it is absent (restored the moment it comes back, reset only by another of
/// those two gestures); without one, the first REAL port, and DEMO only when
/// there is no real port at all. Auto-select never auto-connects — and the
/// claim runs on the press, so it never turns a connection into one.</para>
///
/// <para><b>ROUND 14 G (R18) — the pick is REMEMBERED, and it is actually
/// ATTRIBUTED.</b> Two halves of one owner report ("I set it to 20, but every
/// time open the app it's back to 10"). The pick now writes an
/// <see cref="ISettingsStore"/> in the same gesture that sets
/// <see cref="PreferredPort"/>, and the constructor seeds
/// <see cref="PreferredPort"/> back out of it, so a fresh launch resolves the
/// remembered port by the ordinary F5 rule instead of falling to first-real-
/// port. And attribution stopped depending on the view raising focus events —
/// see <see cref="SelectPortByUser"/> — because the field evidence was that it
/// does not. Neither half changes WHEN a port is opened: restoring and
/// re-selecting are SELECTION, and connecting stays a button press (round-12
/// manual-reconnect ruling).</para>
/// </summary>
public partial class ConnectionSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>SendIt's enumeration cadence, and the session's own reconnect
    /// period (plan §2.2) — one number for "how often this app looks at the
    /// port list".</summary>
    public const int DefaultPollIntervalMs = 2_000;

    /// <summary>ROUND 14 G: the <see cref="ISettingsStore"/> key under which
    /// the operator's chosen port is remembered across launches. Public
    /// because the tests that pin the launch-restore have to name the same key
    /// the VM writes — a test that invented its own would pin nothing.</summary>
    public const string PreferredPortKey = "connection.preferred-port";

    private readonly RadioSession _session;
    private readonly ISerialPort _ports;
    private readonly ISettingsStore _settings;
    private readonly SynchronizationContext? _syncContext;

    private Timer? _pollTimer;
    private int _pollIntervalMs;
    private int _scanInFlight;
    private bool _polling;
    private bool _disposed;

    // Picker interaction: the latest scan waits here until the operator lets
    // go of the control. Null means nothing is queued.
    private bool _interacting;
    private IReadOnlyList<string>? _deferredScan;

    // True while the VM is assigning SelectedPort itself. The view's
    // SelectedIndexChanged fires for programmatic assignment too (it cannot
    // tell the two apart), so a re-entrant SelectPortByUser raised by our own
    // write must not be recorded as an operator choice.
    private bool _applyingSelection;

    // ROUND 14 G. True while Reconcile is editing the bound collection — the
    // window in which a Picker re-derives its own index and the view echoes a
    // selection nobody made. Together with _appAssertedPort (what the app last
    // resolved), this is what tells an operator's tap from the app's own
    // churn WITHOUT depending on the view raising focus events.
    private bool _reconciling;
    private string? _appAssertedPort;

    public ObservableCollection<string> AvailablePorts { get; } = [];
    public IReadOnlyList<int> BaudRates { get; } = [2400, 4800, 9600, 19200];
    public IReadOnlyList<int> DataBitsOptions { get; } = [8, 7];
    public IReadOnlyList<PortParity> ParityOptions { get; } =
        [PortParity.None, PortParity.Even, PortParity.Odd];
    public IReadOnlyList<PortStopBits> StopBitsOptions { get; } =
        [PortStopBits.One, PortStopBits.Two];

    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private int selectedBaud = 9600;
    [ObservableProperty] private int selectedDataBits = 8;
    [ObservableProperty] private PortParity selectedParity = PortParity.None;
    [ObservableProperty] private PortStopBits selectedStopBits = PortStopBits.One;
    [ObservableProperty] private string statusText = "Disconnected";
    [ObservableProperty] private bool isEditable = true;

    /// <summary>The port the OPERATOR chose — by picking it, or by pressing
    /// CONNECT on it. Survives sessions, the port's own disappearance and —
    /// since round 14 G — the process itself; reset only by another such
    /// gesture. Null means "no preference — auto-select owns the
    /// picker".</summary>
    public string? PreferredPort { get; private set; }

    /// <summary>True while the down-session enumeration poll is armed.
    /// Test/diagnostic hook.</summary>
    public bool IsPolling => _polling;

    /// <summary>Poll period. Settable so tests can park the real timer and
    /// drive <see cref="PollPortsOnceAsync"/> deterministically; changing it
    /// re-arms an armed poll.</summary>
    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set
        {
            _pollIntervalMs = value;
            if (_polling) _pollTimer?.Change(value, value);
        }
    }

    public ConnectionSettingsViewModel(
        RadioSession session,
        ISerialPort ports,
        ISettingsStore settings,
        int pollIntervalMs = DefaultPollIntervalMs)
    {
        _session = session;
        _ports = ports;
        _settings = settings;
        _pollIntervalMs = pollIntervalMs;
        // ROUND 14 G (R18): seed the preference from the store BEFORE the
        // first scan, which RefreshPhase below fires. The remembered port is
        // then resolved by exactly the F5 rule a fresh pick would take — the
        // launch restores nothing on its own, it just starts the session with
        // the preference the operator left. An empty store leaves
        // PreferredPort null and the first-REAL-port rule untouched, and a
        // remembered port that is NOT plugged in resolves to NULL rather than
        // silently re-targeting whatever else is (F5).
        PreferredPort = settings.Get(PreferredPortKey);
        // Q10 threading, as RadioSession does it: the singleton resolves on
        // the MAUI main thread, so this is the UI dispatcher. Null in a plain
        // test host, where "dispatch" degrades to running inline.
        _syncContext = SynchronizationContext.Current;
        session.PhaseChanged += (_, _) => RefreshPhase();
        RefreshPhase();
    }

    /// <summary>The last-selected app-side port settings, as one
    /// <see cref="PortSettings"/> — what the Connect toggle sends.
    /// Null while no port is selected.</summary>
    public PortSettings? CreatePortSettings()
        => SelectedPort is null
            ? null
            : new PortSettings
            {
                PortName = SelectedPort,
                BaudRate = SelectedBaud,
                DataBits = SelectedDataBits,
                Parity = SelectedParity,
                StopBits = SelectedStopBits,
            };

    // ---- The user's own selection (F5) --------------------------------------

    /// <summary>
    /// The OPERATOR picked a port. One of the TWO operator gestures that
    /// write <see cref="PreferredPort"/> (the other is the CONNECT press,
    /// R21); both funnel through <see cref="RecordPreference"/>.
    ///
    /// <para><b>Attribution happens ONLY inside an interaction window</b>
    /// (audit round 1 — the plan's own F5 wording is "the view's USER GESTURE
    /// calls the dedicated VM method"). MAUI's Picker raises
    /// <c>SelectedIndexChanged</c> for its OWN recalculation as well as for a
    /// tap: adding or removing items re-derives the selected index and clamps
    /// it, so the view's handler fires with no human involved. Two failures
    /// were reproduced that way — a real port inserted ahead of an
    /// auto-selected DEMO made DEMO "preferred" (after which no cable could
    /// steal it), and a preferred port disappearing clamped onto its neighbour,
    /// which then became "preferred" instead of the selection reading null and
    /// restoring later. The view cannot tell the two apart; the VM can, because
    /// only a human is inside <see cref="BeginPortInteraction"/> …
    /// <see cref="EndPortInteraction"/>.</para>
    ///
    /// <para><b>ROUND 14 G (R18) — the window is no longer the ONLY channel.</b>
    /// The field defect was that the operator's pick never stuck: "I set it to
    /// 20 … 10 stays selected". Under the F5 model a preference that is absent
    /// from the scan reads NULL, so a picker sitting on another port PROVES
    /// there was no preference — the tap was refused, and the only refusal a
    /// human tap can hit is the closed window. Focus events are the VIEW's to
    /// raise and this one does not raise them around a tap, so attribution had
    /// to stop depending on them. It now has TWO channels, both conservative:</para>
    /// <list type="number">
    ///   <item>the INTERACTION WINDOW, when the platform gives us one — a
    ///     human inside it is a human even if they re-pick what is already
    ///     selected; and</item>
    ///   <item>DIVERGENCE: a selection that differs from
    ///     <c>_appAssertedPort</c> — the value this VM itself last resolved —
    ///     arriving outside the app's own list churn. A tap always diverges
    ///     (tapping the selected row raises nothing); an echo of our own
    ///     choice never does, which is what keeps a Picker's bind-time
    ///     re-assert from making an auto-selection "preferred".</item>
    /// </list>
    ///
    /// <para>Three gates, and the ORDERING that backs them up: a call while
    /// the VM is writing <see cref="SelectedPort"/> itself is refused, a call
    /// while <see cref="Reconcile"/> is editing the collection is refused —
    /// that is the whole class of index recalculations the window used to
    /// cover — and outside both, a call that only restates the app's own
    /// choice is refused. Reconciliation still only ever touches the
    /// collection with the window CLOSED (see
    /// <see cref="EndPortInteraction"/>), so the two channels never overlap.</para>
    /// </summary>
    public void SelectPortByUser(string? port)
    {
        if (_applyingSelection) return;
        if (_reconciling) return;
        if (!_interacting && string.Equals(port, _appAssertedPort, StringComparison.Ordinal)) return;
        RecordPreference(port);
    }

    /// <summary>
    /// OWNER RULING (2026-08-21, round 14 G audit): pressing CONNECT also
    /// claims the currently selected port — "the port you connect to is
    /// remembered". Called from <see cref="ConnectToggleViewModel"/>'s CONNECT
    /// branch, on the PRESS and never conditional on the connection
    /// succeeding: a failed connect on the right port is precisely when the
    /// operator needs it remembered.
    ///
    /// <para><b>The gesture is the button, never the phase.</b> This is
    /// deliberately not wired to <see cref="RadioSession.PhaseChanged"/>. A
    /// phase-driven claim would make any automatic path self-claiming — a
    /// restored port would confirm itself without a human — and the round-12
    /// manual-reconnect ruling exists precisely so that opening a port is
    /// always something a person did.</para>
    ///
    /// <para><b>Null claims NOTHING.</b> Recording a null would FORGET the
    /// stored key, so an inert press (no port selected) must leave the store
    /// exactly as it was.</para>
    ///
    /// <para><b>The knowing consequence</b>, accepted with the ruling:
    /// connecting to an AUTO-selected port — DEMO included — makes it sticky,
    /// so "a real cable steals from an auto-selected DEMO" stops applying to
    /// any port the operator has actually connected to. Mere auto-selection
    /// still claims nothing.</para>
    /// </summary>
    public void ClaimSelectedPortAsPreference()
    {
        if (SelectedPort is null) return;
        RecordPreference(SelectedPort);
    }

    /// <summary>The ONE place <see cref="PreferredPort"/> and the store are
    /// written by a gesture — both gestures (<see cref="SelectPortByUser"/>
    /// and <see cref="ClaimSelectedPortAsPreference"/>) funnel through here, so
    /// "the preference and what is remembered of it move together" is a
    /// property of one method rather than a habit at two call sites.</summary>
    private void RecordPreference(string? port)
    {
        PreferredPort = port;
        // R18's first half: the pick outlives the process, not just the
        // session. Same gesture, same instant — a preference the store never
        // heard about is the defect this phase exists to fix.
        _settings.Set(PreferredPortKey, port);
        ApplySelection(port);
    }

    // ---- The Picker's interaction window (F4) -------------------------------

    /// <summary>The operator opened/focused the port Picker: hold scan
    /// results back until they are done with it.</summary>
    public void BeginPortInteraction() => _interacting = true;

    /// <summary>The operator left the Picker: apply whatever the poll found
    /// while it was open (the LATEST scan only — the intermediate ones are
    /// stale by definition).
    ///
    /// <para>The window is CLOSED FIRST, deliberately (audit round 1): the
    /// reconciliation below mutates the bound collection, which makes the
    /// Picker re-raise its selection event, and that event must land outside
    /// the attribution window or the app's own list edit would be recorded as
    /// the operator's choice.</para></summary>
    public void EndPortInteraction()
    {
        _interacting = false;
        var pending = _deferredScan;
        _deferredScan = null;
        if (pending is not null) Reconcile(pending);
    }

    // ---- Enumeration --------------------------------------------------------

    /// <summary>The Refresh button: the GESTURE path, which is allowed to ask
    /// Android for USB permission. Kept exactly because the poll's path
    /// cannot — this button is how a grant gets requested at all.</summary>
    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            var found = await _ports.GetAvailablePortsAsync().ConfigureAwait(true);
            Reconcile(found);
        }
        catch (Exception ex)
        {
            StatusText = "Port scan failed: " + ex.Message;
        }
    }

    /// <summary>One poll tick: the PASSIVE path, single-flight. Public so the
    /// timer and the tests drive the same code.
    ///
    /// <para>A failure here is SILENT, unlike Refresh's: this runs every two
    /// seconds with nobody asking, and a transient enumeration hiccup must not
    /// overwrite the phase status line the operator is reading.</para></summary>
    public async Task PollPortsOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _scanInFlight, 1, 0) != 0) return;
        try
        {
            var found = await _ports.GetAvailablePortsPassiveAsync().ConfigureAwait(true);
            Reconcile(found);
        }
        catch
        {
            // Transient — the next tick tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _scanInFlight, 0);
        }
    }

    /// <summary>CHANGE-ONLY reconciliation: in-place adds and removes, never
    /// Clear/rebuild, so an unchanged scan raises no collection event and an
    /// open Picker never sees its ItemsSource emptied under it.</summary>
    private void Reconcile(IReadOnlyList<string> found)
    {
        if (_interacting)
        {
            _deferredScan = found;
            return;
        }

        // ROUND 14 G: everything below is the APP editing its own list, and a
        // bound Picker answers those edits with selection events of its own.
        // The latch is what makes them un-attributable — it covers the adds,
        // the removes AND the assertion that follows them.
        _reconciling = true;
        try
        {
            for (int i = AvailablePorts.Count - 1; i >= 0; i--)
                if (!found.Contains(AvailablePorts[i]))
                    AvailablePorts.RemoveAt(i);

            for (int i = 0; i < found.Count; i++)
                if (!AvailablePorts.Contains(found[i]))
                    AvailablePorts.Insert(Math.Min(i, AvailablePorts.Count), found[i]);

            ApplySelection(ResolveSelection(found));
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>What the picker should show for this scan — a pure function
    /// of the scan and <see cref="PreferredPort"/> (F5).</summary>
    private string? ResolveSelection(IReadOnlyList<string> found)
    {
        if (PreferredPort is not null)
            // Present → the operator's port. Absent → NULL, never a silent
            // re-target onto whatever else happens to be plugged in.
            return found.Contains(PreferredPort) ? PreferredPort : null;

        // No preference: the first REAL port wins, so inserting a cable
        // auto-selects it (stealing from an auto-selected DEMO, never from a
        // chosen port). DEMO only when there is nothing real to pick.
        return found.FirstOrDefault(p => p != DemoSerialPort.DemoPortName)
               ?? found.FirstOrDefault();
    }

    /// <summary>Assign <see cref="SelectedPort"/> as the APP, not the
    /// operator: no PropertyChanged when nothing moved, and the re-entrancy
    /// latch is held so the view's echo cannot be mistaken for a pick.
    ///
    /// <para>ROUND 14 G: the value is recorded as the app's own ASSERTION
    /// before the no-op check, not after it — a re-assert that changed nothing
    /// is still what the app believes, and that belief is what
    /// <see cref="SelectPortByUser"/> measures divergence against.</para></summary>
    private void ApplySelection(string? port)
    {
        _appAssertedPort = port;
        if (string.Equals(SelectedPort, port, StringComparison.Ordinal)) return;
        _applyingSelection = true;
        try { SelectedPort = port; }
        finally { _applyingSelection = false; }
    }

    // ---- Phase --------------------------------------------------------------

    private void RefreshPhase()
    {
        // G4 lockout: editable only while nothing is live or in flight.
        IsEditable = _session.Phase is SessionPhase.Disconnected or SessionPhase.Failed;
        StatusText = _session.Phase switch
        {
            SessionPhase.Disconnected => "Disconnected",
            SessionPhase.Connecting => $"Connecting to {_session.PortName}…",
            SessionPhase.Ready => $"Connected — {_session.PortName} {_session.BaudRate}",
            SessionPhase.Failed => "Connection failed — check power, cabling, and baud rate",
            SessionPhase.Reconnecting => $"Connection lost — reconnecting to {_session.PortName}…",
            _ => _session.Phase.ToString(),
        };

        // F4: the poll follows the lockout exactly — it runs while the page is
        // editable and stands down while the session owns the port.
        if (IsEditable) StartPolling();
        else StopPolling();
    }

    private void StartPolling()
    {
        if (_disposed || _polling) return;
        _polling = true;
        _pollTimer = new Timer(OnPollTick, null, _pollIntervalMs, _pollIntervalMs);
        // Scan NOW as well as on the cadence: the session just went down (or
        // the app just started on this page), and making the operator wait a
        // tick to see the port they are about to pick is the empty-list defect
        // the poll exists to fix. Armed-to-armed re-entry does NOT rescan —
        // a phase event that leaves the page editable changed nothing here.
        _ = PollPortsOnceAsync();
    }

    private void StopPolling()
    {
        _polling = false;
        var timer = _pollTimer;
        _pollTimer = null;
        timer?.Dispose();
    }

    /// <summary>Timer thread. Everything the tick touches is UI state, so it
    /// is marshalled first and the scan itself then runs on the UI thread.</summary>
    private void OnPollTick(object? _)
    {
        if (_syncContext is not null) _syncContext.Post(__ => _ = PollPortsOnceAsync(), null);
        else _ = PollPortsOnceAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        StopPolling();
        GC.SuppressFinalize(this);
    }
}
