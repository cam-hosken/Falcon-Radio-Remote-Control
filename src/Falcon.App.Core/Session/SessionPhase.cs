namespace Falcon.App.Core.Session;

/// <summary>
/// The session layer's connection phase — what the spine's status dot shows
/// (plan §4.1). Distinct from <see cref="Falcon.Core.Radio.ConnectionState"/>:
/// the radio only knows about one open port; the session also knows whether
/// the operator closed it on purpose and whether a reconnect poller is
/// working the problem.
/// </summary>
public enum SessionPhase
{
    Disconnected,
    /// <summary>User-initiated connect in flight (port open + connect ritual).</summary>
    Connecting,
    Ready,
    /// <summary>Connect failed or the connection died with auto-reconnect unavailable.</summary>
    Failed,
    /// <summary>Unexpected disconnect; the 2 s single-flight poller is armed.</summary>
    Reconnecting,
}
