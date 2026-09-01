namespace Falcon.App.Core.Session;

/// <summary>What the platform layer should do with the radio-link
/// foreground service after a lifecycle input.</summary>
public enum LinkServiceAction
{
    None,
    Start,
    Stop,
}

/// <summary>
/// Decides when the Android radio-link foreground service starts and stops
/// (Stage 7, plan §2.5). Pure state machine — extracted from MainActivity so
/// the rules are unit-testable on net10.0; the activity feeds it lifecycle
/// facts and executes the returned action.
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item><b>Ready → Start</b> (once — a second Ready while the service is
///         believed running is a no-op).</item>
///   <item><b>Disconnected / Failed → Stop.</b> Both mean the link is down
///         with no poller working the problem (user close, initial-connect
///         failure, or a loss with auto-reconnect off).</item>
///   <item><b>Connecting / Reconnecting → nothing.</b> During Reconnecting
///         the service (and its partial wake lock) deliberately stays up:
///         the 2 s reconnect poller needs the process privileged and the CPU
///         awake while the screen is off, and the eventual re-Ready then
///         needs no service start at all — which is what makes the
///         Android 14 background-start restriction unreachable in the
///         auto-reconnect flow (see software-architecture.md).</item>
///   <item><b>Background deferral</b> (SendIt's Android 14 lesson, adapted):
///         Android 12+ refuses to START a foreground service from the
///         background. If Ready lands while the activity is paused, the
///         start is deferred and completed on the next foreground. The
///         pending flag is cleared on Disconnected/Failed but retained
///         (inert) through Connecting/Reconnecting — it is the
///         consume-time guard (last phase must still be Ready) that makes
///         a deferred start behave as dropped once the session leaves
///         Ready (Stage 7 audit F2, test-pinned).</item>
/// </list>
///
/// <para>Registered as a DI singleton: its believed-running state must
/// survive activity recreation (under SingleTask, MainActivity instances
/// can come and go while the process, session and service live on).
/// No locking — every input arrives on the UI thread (session events are
/// marshalled per Q10; activity lifecycle callbacks are UI-thread).</para>
/// </summary>
public sealed class ForegroundLinkPolicy
{
    private bool _serviceRunning;
    private bool _startPending;
    private bool _activityForegrounded;
    private SessionPhase _lastPhase = SessionPhase.Disconnected;

    /// <summary>Believed service state. Test/diagnostic hook.</summary>
    public bool IsServiceRunning => _serviceRunning;

    public LinkServiceAction OnPhaseChanged(SessionPhase phase)
    {
        _lastPhase = phase;
        switch (phase)
        {
            case SessionPhase.Ready:
                if (_serviceRunning) return LinkServiceAction.None;
                if (!_activityForegrounded)
                {
                    _startPending = true;
                    return LinkServiceAction.None;
                }
                _startPending = false;
                _serviceRunning = true;
                return LinkServiceAction.Start;

            case SessionPhase.Disconnected:
            case SessionPhase.Failed:
                _startPending = false;
                if (!_serviceRunning) return LinkServiceAction.None;
                _serviceRunning = false;
                return LinkServiceAction.Stop;

            default: // Connecting, Reconnecting — link lifecycle still live
                return LinkServiceAction.None;
        }
    }

    public LinkServiceAction OnActivityForegroundChanged(bool foregrounded)
    {
        _activityForegrounded = foregrounded;
        if (!foregrounded || !_startPending || _lastPhase != SessionPhase.Ready)
            return LinkServiceAction.None;
        _startPending = false;
        _serviceRunning = true;
        return LinkServiceAction.Start;
    }
}
