namespace Falcon.Core.Transport;

// Provenance: lifted from the owner's SendIt project
// (SendIt.Protocol/Transport/ISerialPort.cs), with the TNC-specific surface
// removed (UART break, RTS/CTS flow control — the PRC-138 remote port is 8N1,
// NO flow control) and the Rx observables replaced by plain .NET events so
// Falcon.Core takes no System.Reactive dependency.

/// <summary>
/// Platform-agnostic byte-level serial seam (plan §2.2).
/// Implementations emit raw received chunks; <see cref="SerialTransport"/>
/// owns framing and flow control above this seam.
/// </summary>
public interface ISerialPort : IAsyncDisposable
{
    /// <summary>Raw byte chunks as received from the port, in arrival order.
    /// Raised on the implementation's read thread.</summary>
    event EventHandler<SerialDataEventArgs>? DataReceived;

    /// <summary>
    /// SEPARATE disconnect channel (SendIt discipline: never fault the
    /// long-lived data stream). Fires when the port goes away WITHOUT an
    /// explicit <see cref="CloseAsync"/> — USB cable yanked, COM device
    /// removed, driver fault. When the TX queue is idle there is no write to
    /// fail on, so without this signal a consumer would stay "connected"
    /// forever. Implementations must flip <see cref="IsOpen"/> to false
    /// BEFORE emitting (consistent state inside the handler) and emit at
    /// most once per open session.
    /// </summary>
    event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    bool IsOpen { get; }

    /// <summary>Available port names on the current platform (e.g. "COM3").
    /// This is the GESTURE path: an implementation is allowed to do whatever
    /// listing costs on its platform, including asking the operator for
    /// permission (Android's USB dialog assumes a user gesture).</summary>
    Task<IReadOnlyList<string>> GetAvailablePortsAsync();

    /// <summary>
    /// The same list, PASSIVELY: enumeration that never prompts the operator
    /// and never assumes a user gesture (clone round 12 §6 F4).
    ///
    /// <para>Why the seam splits at all: the connection page polls for ports
    /// every 2 s while the session is down, and on Android
    /// <see cref="GetAvailablePortsAsync"/> requests USB permission for any
    /// unpermissioned device — from a TIMER that would be a permission dialog
    /// every two seconds, on whatever screen the operator was looking at.
    /// Permission requests stay tied to the Refresh and Connect gestures; the
    /// poll takes this path.</para>
    ///
    /// <para>On platforms where listing costs nothing and prompts nobody
    /// (Windows COM enumeration is a registry read) the two are the same
    /// call. The RESULT may legitimately be less specific than the gesture
    /// path's — Android masks a device's serial number until permission is
    /// granted — so a caller must treat this as "what is plugged in", not as
    /// "what can be opened right now".</para>
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync();

    Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default);
    Task CloseAsync();
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}

public sealed class SerialDataEventArgs(byte[] data) : EventArgs
{
    public byte[] Data { get; } = data;
}

public sealed class SerialDisconnectedEventArgs(Exception reason) : EventArgs
{
    public Exception Reason { get; } = reason;
}
