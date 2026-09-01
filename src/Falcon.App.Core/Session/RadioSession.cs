using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Core.Session;

/// <summary>
/// Owns the connect/disconnect lifecycle over <see cref="Prc138Radio"/>
/// (plan §2.2, Stage 3):
///
/// - <b>TransportError is CONNECTION-FATAL</b>, never advisory: the
///   <see cref="ITransport"/> contract says a write-path error may leave TX
///   permanently dead, so any transport error tears the connection down and
///   either arms auto-reconnect or surfaces Failed.
/// - <b>Auto-reconnect</b>: 2 s single-flight poller, armed ONLY on an
///   unexpected disconnect of an established (Ready) session, disarmed on
///   user <see cref="Close"/>. Every reconnect attempt runs the full connect
///   ritual (<see cref="Prc138Radio.Connect"/> — bare CRs, echo off, init
///   queries, sentinel).
/// - An <i>initial</i> connect that fails (port missing, radio silent) goes
///   to Failed without arming the poller — that is a failed attempt, not an
///   unexpected disconnect.
///
/// Threading mirrors Q10: the SynchronizationContext captured at
/// construction marshals <see cref="PhaseChanged"/>/<see cref="SessionError"/>;
/// internal state is lock-guarded because the poller and transport events
/// arrive on worker threads.
/// </summary>
public sealed class RadioSession : IDisposable
{
    private readonly Prc138Radio _radio;
    private readonly ITransport _transport;
    private readonly SynchronizationContext? _syncContext;
    private readonly object _lock = new();

    private Timer? _reconnectTimer;
    private bool _reconnectArmed;
    private bool _attemptInFlight;
    private bool _userClosing;
    private bool _tearingDown;
    private PortSettings? _lastSettings;
    private SessionPhase _phase = SessionPhase.Disconnected;
    private int _readySession;

    public SessionPhase Phase { get { lock (_lock) return _phase; } }

    /// <summary>ROUND 14 C — a monotonic identity for the CURRENT Ready
    /// session: 0 before the first Ready, then +1 on every ENTRY into Ready.
    /// Read synchronously, like <see cref="Phase"/>.
    ///
    /// <para><b>Why a counter and not just the phase.</b>
    /// <see cref="PhaseChanged"/> is MARSHALLED and carries no payload, so a
    /// subscriber sees only the phase that is current when its callback
    /// finally runs. If a drop and a reconnect both complete before the UI
    /// context drains, every queued callback observes Ready and no subscriber
    /// can tell that the session in between DIED — a leftover notification
    /// from the connect that first reached Ready is indistinguishable from a
    /// notification announcing a whole new session. Session-scoped state then
    /// leaks across the boundary. This number is the missing identity: equal
    /// means the same Ready session, different means a new one, whatever order
    /// the notifications arrive in.</para>
    ///
    /// <para>Added for <c>CouplerPolicy</c> (round-14-C audit round 2,
    /// BLOCKER), which clears its baseline and re-arms its seeding read on it.
    /// Additive and read-only: no existing behaviour, signature or subscriber
    /// changes.</para></summary>
    public int ReadySession { get { lock (_lock) return _readySession; } }

    /// <summary>App-side setting. Read when an unexpected disconnect happens;
    /// changing it never touches the radio. Default OFF (GUI rejigger G1,
    /// owner ruling): the poller never arms unless explicit code enables it —
    /// the machinery stays, dormant; there is no UI toggle.</summary>
    public bool AutoReconnectEnabled { get; set; }

    /// <summary>Poller period (plan §2.2: 2 s). Test hook.</summary>
    public int ReconnectIntervalMs { get; set; } = 2_000;

    public string? PortName { get { lock (_lock) return _lastSettings?.PortName; } }
    public int? BaudRate { get { lock (_lock) return _lastSettings?.BaudRate; } }

    public event EventHandler? PhaseChanged;
    public event EventHandler<SessionErrorEventArgs>? SessionError;

    /// <summary>True while the poller is armed. Test hook.</summary>
    internal bool IsReconnectArmed { get { lock (_lock) return _reconnectArmed; } }

    public RadioSession(Prc138Radio radio, ITransport transport, SynchronizationContext? syncContext = null)
    {
        _radio = radio;
        _transport = transport;
        _syncContext = syncContext ?? SynchronizationContext.Current;

        _radio.StateChanged += OnRadioStateChanged;
        // Session subscribes to the TRANSPORT directly: the radio's
        // ErrorOccurred wraps transport errors into display text, but the
        // fatal-teardown decision needs the raw channel.
        _transport.TransportError += OnTransportError;
    }

    private void Post(Action action)
    {
        if (_syncContext is not null) _syncContext.Post(_ => action(), null);
        else action();
    }

    // ---- User lifecycle ---------------------------------------------------

    /// <summary>User-initiated connect. Runs the full connect ritual via
    /// <see cref="Prc138Radio.Connect"/>; Ready arrives via the sentinel.</summary>
    public void Connect(PortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            _userClosing = false;
            DisarmLocked();
            _lastSettings = settings;
        }
        SetPhase(SessionPhase.Connecting);
        try
        {
            _radio.Connect(settings);
        }
        catch (Exception ex)
        {
            SetPhase(SessionPhase.Failed);
            RaiseError($"Could not open {settings.PortName}: {ex.Message}");
        }
    }

    /// <summary>User-initiated close: disarms auto-reconnect (plan §2.2) and
    /// tears the connection down.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _userClosing = true;
            DisarmLocked();
        }
        _radio.Disconnect();
        // Belt for the port-already-dead case (radio.Disconnect no-ops when
        // the port is closed, but the transport's writer still needs reaping).
        _transport.Close();
        SetPhase(SessionPhase.Disconnected);
    }

    // ---- Radio connection-state mapping ------------------------------------

    private void OnRadioStateChanged(object? sender, RadioStateChangedEventArgs e)
    {
        if (e.PropertyChanged != RadioProperty.ConnectionState) return;

        switch (_radio.Connection)
        {
            case ConnectionState.Ready:
                lock (_lock)
                {
                    _attemptInFlight = false;
                    if (_reconnectArmed) DisarmLocked();   // reconnect succeeded
                }
                SetPhase(SessionPhase.Ready);
                break;

            case ConnectionState.Failed:
                // Init watchdog gave up (the radio already closed the port).
                bool armed;
                lock (_lock)
                {
                    _attemptInFlight = false;
                    armed = _reconnectArmed;
                }
                // A failed reconnect ATTEMPT keeps polling; a failed initial
                // connect is terminal until the user acts.
                SetPhase(armed ? SessionPhase.Reconnecting : SessionPhase.Failed);
                break;

            // Disconnected transitions are driven by Close()/the fatal path
            // directly — the radio event carries no was-it-deliberate signal.
        }
    }

    // ---- TransportError: CONNECTION-FATAL (ITransport contract) ------------

    private void OnTransportError(object? sender, TransportErrorEventArgs e)
    {
        bool wasReady, reconnectCycle;
        lock (_lock)
        {
            if (_userClosing || _tearingDown || _phase == SessionPhase.Disconnected) return;
            _tearingDown = true;
            wasReady = _phase == SessionPhase.Ready;
            reconnectCycle = _reconnectArmed;
        }

        // Tear down first: after a write-path error TX may be dead even
        // though the port still reads (ITransport contract line).
        try
        {
            if (_radio.IsConnectionOpen)
            {
                _radio.Disconnect();
            }
            else
            {
                // THE REAL YANK PATH (round 13 D2, repair 3). The port flips
                // IsOpen false BEFORE emitting Disconnected, so this is the
                // branch a cable pull actually takes — and it used to reap the
                // writer and stop, leaving Core believing it was still Ready
                // with its watchdog armed and sentinels owed. Close the
                // transport, then bring Core's state down to match it.
                _transport.Close();      // port already gone — reap the writer
                _radio.NotifyTransportClosed();
            }
        }
        finally
        {
            lock (_lock) _tearingDown = false;
        }

        if (wasReady && AutoReconnectEnabled)
        {
            lock (_lock) { if (!_userClosing) ArmLocked(); }
            SetPhase(SessionPhase.Reconnecting);
            RaiseError($"Connection lost ({e.Error.Message}) — reconnecting.");
        }
        else if (reconnectCycle)
        {
            // Error during a reconnect attempt: stay armed, next tick retries.
            lock (_lock) _attemptInFlight = false;
            SetPhase(SessionPhase.Reconnecting);
        }
        else
        {
            SetPhase(SessionPhase.Failed);
            RaiseError($"Connection lost: {e.Error.Message}");
        }
    }

    // ---- Reconnect poller (2 s, single-flight) ------------------------------

    /// <summary>Caller holds _lock.</summary>
    private void ArmLocked()
    {
        _reconnectArmed = true;
        _attemptInFlight = false;
        _reconnectTimer ??= new Timer(_ => ReconnectTick(), null, Timeout.Infinite, Timeout.Infinite);
        _reconnectTimer.Change(ReconnectIntervalMs, ReconnectIntervalMs);
    }

    /// <summary>Caller holds _lock.</summary>
    private void DisarmLocked()
    {
        _reconnectArmed = false;
        _attemptInFlight = false;
        _reconnectTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>One poller tick. Single-flight: while an attempt is between
    /// Connect and its Ready/Failed/error outcome, further ticks no-op.
    /// Internal so tests drive ticks deterministically.</summary>
    internal void ReconnectTick()
    {
        PortSettings? settings;
        lock (_lock)
        {
            if (!_reconnectArmed || _attemptInFlight || _userClosing) return;
            settings = _lastSettings;
            if (settings is null) { DisarmLocked(); return; }
            _attemptInFlight = true;
        }

        try
        {
            _radio.Connect(settings);    // the FULL connect ritual, every attempt
        }
        catch
        {
            // Port still absent — the next tick retries. Deliberately quiet:
            // one error per 2 s tick would flood the rate-limited toast.
            lock (_lock) _attemptInFlight = false;
        }
    }

    // ---- Plumbing -----------------------------------------------------------

    private void SetPhase(SessionPhase value)
    {
        lock (_lock)
        {
            if (_phase == value) return;
            _phase = value;
            if (value == SessionPhase.Ready) _readySession++;
        }
        Post(() => PhaseChanged?.Invoke(this, EventArgs.Empty));
    }

    private void RaiseError(string message)
        => Post(() => SessionError?.Invoke(this, new SessionErrorEventArgs(message)));

    public void Dispose()
    {
        lock (_lock) DisarmLocked();
        _reconnectTimer?.Dispose();
        _radio.StateChanged -= OnRadioStateChanged;
        _transport.TransportError -= OnTransportError;
    }
}

public sealed class SessionErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
