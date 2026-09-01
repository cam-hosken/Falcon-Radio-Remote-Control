using System.IO.Ports;

namespace Falcon.Core.Transport;

// Provenance: lifted nearly verbatim from the owner's SendIt project
// (SendIt.Protocol/Transport/WindowsSerialPort.cs). It embeds two hard-won
// fixes that must not be re-learned:
//   1. The 1.5 s presence-poller — System.IO.Ports doesn't reliably fault
//      BaseStream.ReadAsync when a USB-serial adapter is pulled (the read can
//      stay parked indefinitely), so port enumeration is polled and
//      Disconnected fired when our port vanishes.
//   2. Dispose-BaseStream-before-Close teardown — SerialPort.Close() on
//      Windows deadlocks when BaseStream.ReadAsync is pending (Close waits
//      for the internal reader thread, parked in native I/O the token cannot
//      unblock); disposing the BaseStream first faults the read out.
// Stripped vs SendIt: CTS/RTS flow control, UART break, IRadioSettings
// indices (settings come in as PortSettings) — the PRC-138 remote port is
// 8N1, NO flow control.
//
// Lives in Falcon.Core (SendIt pattern: shared project, only DI registration
// is platform-conditional). Compiled ONLY for the non-android TFM — see
// Falcon.Core.csproj (net10.0-android removes this file and takes no
// System.IO.Ports reference).

/// <summary>
/// Windows COM port implementation of <see cref="ISerialPort"/>.
/// Uses <see cref="System.IO.Ports.SerialPort"/> with an async read loop.
/// </summary>
public sealed class WindowsSerialPort : ISerialPort
{
    private const int ReadBufferSize = 4096;

    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    // Tracks whether we have already raised Disconnected for the current open
    // session, so a subsequent failed write doesn't double-fire after the read
    // loop already noticed the unplug.
    private int _disconnectFired;

    // Name of the currently-open port, captured at OpenAsync. Used by the
    // presence-poller to detect device removal (see provenance note 1).
    private string? _openPortName;
    private Timer? _presenceTimer;
    private const int PresencePollMs = 1500;

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    public bool IsOpen => _port?.IsOpen ?? false;

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        var ports = (IReadOnlyList<string>)SerialPort.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(ports);
    }

    /// <summary>Identical to <see cref="GetAvailablePortsAsync"/>: COM
    /// enumeration is a registry lookup, it prompts nobody and it costs
    /// nothing — the presence-poller above already calls it every 1.5 s. The
    /// SEAM splits (round 12 §6 F4) because ANDROID's gesture path requests
    /// USB permission; Windows has no such split to make.</summary>
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => GetAvailablePortsAsync();

    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
            throw new InvalidOperationException("Port is already open.");

        var portName = settings.PortName
            ?? throw new ArgumentException("PortSettings.PortName is required.", nameof(settings));

        _port = new SerialPort(portName, settings.BaudRate)
        {
            DataBits = settings.DataBits,
            Parity = settings.Parity switch
            {
                PortParity.Even => Parity.Even,
                PortParity.Odd => Parity.Odd,
                _ => Parity.None,
            },
            StopBits = settings.StopBits == PortStopBits.Two ? StopBits.Two : StopBits.One,
            // The PRC-138 remote link has NO flow control (bench-confirmed:
            // PORT_REMOTE XON_XOFF disable, no RTS/CTS).
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            // No flow control means a write only stalls if the driver buffer
            // is full — impossible for our short command lines at 2400-9600.
            // 2 s is generous and keeps a wedged driver from hanging a caller.
            WriteTimeout = 2000,
        };

        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();

        // Reset disconnect-fired latch so the new session can raise it again
        // if this open also dies. CloseAsync clears it too, but resetting here
        // covers callers that re-open without an explicit close in between.
        Interlocked.Exchange(ref _disconnectFired, 0);

        _readCts = new CancellationTokenSource();
        _readLoop = RunReadLoopAsync(_readCts.Token);

        _openPortName = portName;
        _presenceTimer = new Timer(_ => CheckPortPresence(),
            null, PresencePollMs, PresencePollMs);

        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        // Suppress the read-loop's pending fault from being interpreted as a
        // disconnect: an explicit close means whoever called us already knows
        // the port is going away.
        Interlocked.Exchange(ref _disconnectFired, 1);

        _openPortName = null;
        var presence = _presenceTimer; _presenceTimer = null;
        presence?.Dispose();

        if (_readCts is not null)
            await _readCts.CancelAsync().ConfigureAwait(false);

        if (_port is not null)
        {
            var port = _port;
            _port = null;

            var closeTask = RunDisposeBeltAsync(port);

            // 2s is generous; normal close is <50ms. If we exceed it, the driver is wedged —
            // leaking the SerialPort instance is better than hanging the process.
            if (await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != closeTask)
            {
                // Fall through — close is abandoned. Process exit will reclaim the handle.
            }
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }           // read loop didn't observe the close in time
            catch (IOException) { }                // thrown when the port is closed under a pending read
            catch (ObjectDisposedException) { }    // BaseStream was disposed out from under it
            _readLoop = null;
        }

        if (_readCts is not null)
        {
            _readCts.Dispose();
            _readCts = null;
        }
    }

    /// <summary>
    /// THE TEARDOWN BELT — the one place a <see cref="SerialPort"/> this class
    /// opened is ever released, and the reason it is a method rather than an
    /// inline block (round 14 Phase D, plan §4-D item 1): the YANK path needs
    /// the very same disposal, and used to get none at all.
    ///
    /// <para>Order is the whole point. <c>SerialPort.Close()</c> on Windows
    /// deadlocks when <c>BaseStream.ReadAsync</c> is pending: Close waits for
    /// the internal reader thread, which is parked in native I/O that the
    /// CancellationToken cannot unblock. Disposing the BaseStream FIRST faults
    /// the pending read out and lets Close return (provenance note 2).</para>
    ///
    /// <para>It runs on the pool so a wedged driver blocks a pool thread and
    /// nothing else, and it swallows everything: we are tearing down, and a
    /// belt that can throw is a belt whose caller has to care. That is also
    /// why <see cref="RaiseDisconnect"/> may fire-and-forget it — the returned
    /// task cannot fault, so there is nothing to observe.</para>
    /// </summary>
    private static Task RunDisposeBeltAsync(SerialPort port) => Task.Run(() =>
    {
        try { port.BaseStream?.Dispose(); } catch { /* may already be faulted */ }
        try { port.Close(); }              catch { /* ignore — we're tearing down */ }
        try { port.Dispose(); }            catch { /* ignore */ }
    });

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var port = _port;
        if (port is null || !port.IsOpen)
            throw new InvalidOperationException("Port is not open.");

        // System.IO.Ports.SerialPort doesn't have a true async write path;
        // run synchronously on a thread-pool thread and respect the cancellation token.
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = data.ToArray();
        try
        {
            await Task.Run(() => port.Write(bytes, 0, bytes.Length), cancellationToken)
                      .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // With no flow control a write timeout means the driver is wedged.
            // Wrap as IOException so callers handle all write failures uniformly.
            throw new IOException("Serial write timed out.", ex);
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or InvalidOperationException
                                     or ObjectDisposedException)
        {
            // Fatal write failure — most often the COM device disappeared
            // (cable unplugged, USB-serial dongle removed). Surface it on the
            // Disconnected channel so the consumer can react even if no RX
            // read was outstanding to notice. Then rethrow as IOException.
            RaiseDisconnect(ex);
            throw new IOException("Serial write failed — port may have been disconnected.", ex);
        }
    }

    private async Task RunReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        var stream = _port!.BaseStream;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    var chunk = buffer[..bytesRead].ToArray();
                    DataReceived?.Invoke(this, new SerialDataEventArgs(chunk));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Any unhandled read fault (port removed, driver crash, USB
            // device unplugged) is treated as a hard disconnect. The data
            // channel is never faulted — it stays usable for the next
            // OpenAsync session (SendIt discipline).
            RaiseDisconnect(ex);
        }
    }

    /// <summary>
    /// Latched disconnect emitter. Fires <see cref="Disconnected"/> at most
    /// once per open session. Flips the cached _port reference so
    /// <see cref="IsOpen"/> returns false before subscribers see the event —
    /// callers reading IsOpen inside their handler get consistent state.
    /// Also tears down the presence-poller so it doesn't fire a duplicate.
    ///
    /// <para><b>ROUND 14 PHASE D — the yank handle leak.</b> This method used
    /// to drop the port on the floor: <c>_port = null</c> and nothing else, so
    /// every unplug orphaned an open <c>SerialPort</c> — its handle, its
    /// internal reader thread, its BaseStream — for the life of the process.
    /// Nobody else could clean up either, because clearing the field first is
    /// exactly what makes a later <see cref="CloseAsync"/> a no-op. The
    /// dropped port now goes through the SAME belt a deliberate close uses
    /// (<see cref="RunDisposeBeltAsync"/>), so a yank costs no handles.</para>
    ///
    /// <para>The belt is started BEFORE the event, and deliberately: on the
    /// presence-poller's path the read loop is still parked in
    /// <c>BaseStream.ReadAsync</c>, and disposing the stream is what faults it
    /// out — so a subscriber that tears the session down synchronously (the
    /// production shape) meets a read loop that is already unwinding instead
    /// of one it has to wait out. It is NOT awaited: this runs on the poller's
    /// timer thread and on the read loop's own fault path, neither of which
    /// may block for a driver.</para>
    ///
    /// <para>Ordering is preserved exactly: the interlocked single-fire still
    /// guards the whole body, and <c>_port</c> is captured-then-cleared, so
    /// <see cref="IsOpen"/> is false before any subscriber runs — the contract
    /// this summary promises. A concurrent <see cref="CloseAsync"/> could in
    /// principle capture the same port and run a second belt over it; that is
    /// harmless (every call is idempotent and swallowed) and the wider
    /// RaiseDisconnect-vs-CloseAsync field race is deferred by the plan
    /// (§1 ledger) rather than fixed here.</para>
    /// </summary>
    private void RaiseDisconnect(Exception ex)
    {
        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0) return;
        var port = _port;
        _port = null;
        _openPortName = null;
        var presence = _presenceTimer; _presenceTimer = null;
        presence?.Dispose();
        if (port is not null) _ = RunDisposeBeltAsync(port);
        Disconnected?.Invoke(this, new SerialDisconnectedEventArgs(ex));
    }

    /// <summary>
    /// Presence-poller tick. System.IO.Ports.SerialPort doesn't reliably
    /// fault BaseStream.ReadAsync on USB-serial removal — many drivers
    /// leave the read parked indefinitely — so we fall back to enumerating
    /// COM ports and firing Disconnected when our open port is gone from
    /// the list. SerialPort.GetPortNames() is a registry lookup; cheap to
    /// call every 1.5 s.
    /// </summary>
    private void CheckPortPresence()
    {
        var name = _openPortName;
        if (name is null) return;

        string[] current;
        try { current = SerialPort.GetPortNames(); }
        catch { return; } // transient registry hiccup — try again next tick

        bool stillThere = false;
        foreach (var p in current)
        {
            if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
            {
                stillThere = true;
                break;
            }
        }
        if (!stillThere)
            RaiseDisconnect(new IOException($"COM port '{name}' is no longer present."));
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
    }
}
