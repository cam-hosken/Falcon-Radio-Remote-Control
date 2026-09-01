using Android.Hardware.Usb;
using Falcon.Core.Transport;
using UsbSerialForAndroid.Net;
using UsbSerialForAndroid.Net.Drivers;
using UsbSerialForAndroid.Net.Exceptions;
using UsbSerialForAndroid.Net.Helper;
using LibParity = UsbSerialForAndroid.Net.Enums.Parity;
using LibStopBits = UsbSerialForAndroid.Net.Enums.StopBits;

namespace Falcon.App.Platforms.Android;

/// <summary>
/// Android USB OTG implementation of <see cref="ISerialPort"/> over the
/// vendored UsbSerialForAndroid.Net fork (Stage 7, plan §2.2/§2.5).
///
/// Provenance: lifted-adapted from the owner's SendIt project
/// (SendIt/Platforms/Android/AndroidUsbSerialPort.cs). Stripped vs SendIt:
/// CTS polling, RTS/CTS flow control and UART break (TNC-specific — the
/// PRC-138 remote port is 8N1, NO flow control); Rx subjects replaced by
/// the seam's plain events. Target adapter is FTDI (owner decision, plan
/// §7 item 2) — the library's FTDI driver already sets latency timer 1 in
/// its OpenAsync; other supported chips (CP210x/CH340/Prolific/CDC-ACM)
/// work through the same factory but are untested here.
///
/// <para><b>Port identifiers</b> are <c>VID:PID[:Serial]</c>
/// (<see cref="UsbPortIdentifier"/> — parse/format/match rules live in
/// Falcon.Core so they are unit-tested; this class only ever runs on a
/// device). Android's per-session integer DeviceId is resolved fresh at
/// open time. The enumeration returns bare identifiers, no friendly-name
/// suffix: ConnectionSettingsViewModel round-trips the picked string
/// verbatim into <see cref="PortSettings.PortName"/>, so any decoration
/// would have to be parsed back out again.</para>
///
/// <para><b>Permission flow</b>: <see cref="GetAvailablePortsAsync"/>
/// requests permission for any unpermissioned (supported) device — that is
/// the Settings page's Refresh button, a user gesture. Without permission
/// Android masks the serial, so the listed identifier may degrade to bare
/// VID:PID until granted. <see cref="OpenAsync"/> on an unpermissioned
/// device re-requests and throws <see cref="UnauthorizedAccessException"/>:
/// the session layer surfaces Failed with the message, the operator taps
/// Allow on the system dialog and presses Connect again (the grant needs a
/// human tap — the session's user-initiated connect is the retry path).</para>
///
/// <para><b>Disconnects</b>: the read loop fault, the USB DETACHED
/// broadcast (identifier-compared so an unrelated unplug doesn't tear down
/// the session) and a failed write all funnel through
/// <see cref="RaiseDisconnect"/> — latched once per open session,
/// <see cref="IsOpen"/> flipped false before the event (seam contract).</para>
/// </summary>
internal sealed class AndroidUsbSerialPort : ISerialPort
{
    private UsbDriverBase? _driver;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private volatile bool _isOpen;
    // Identifier of the currently-open device, for the DETACHED comparison.
    private UsbPortIdentifier? _openIdentifier;
    // Latched so the read loop, the detach broadcast, and a failing write
    // don't all fire Disconnected for the same teardown.
    private int _disconnectFired;

    /// <summary>
    /// SESSION GENERATION (round 13 D2, repair 1). Incremented by every
    /// <see cref="OpenAsync"/>; the read loop is handed the generation it was
    /// born under and applies effects only while that generation is still
    /// current.
    ///
    /// <para><b>The race it closes</b> — and it is the one the device gate
    /// exercises: pull the cable, replug immediately, reconnect. The old
    /// read loop dereferenced the <c>_driver</c> FIELD and emitted
    /// unconditionally, so a read parked in the kernel from the dead session
    /// could unpark after the NEW session had opened and either push its
    /// stale bytes into the new stream or mark the new session disconnected.
    /// Cancellation alone does not prevent this: the loop can be inside an
    /// un-cancellable bulk transfer, and its own token is a different object
    /// from the new session's. A monotonically increasing generation is the
    /// check that survives both.</para>
    /// </summary>
    private int _generation;

    /// <summary>Per-phase cap for <see cref="CloseAsync"/>. Two sequential
    /// phases ⇒ 2000 ms worst case, which is what leaves 1000 ms of slack
    /// under <c>SerialTransport.PortCloseTimeoutMs</c> (3000). Keep the two
    /// numbers in that relationship: an inner cap at or above the outer one
    /// means the transport abandons closes that were about to succeed.</summary>
    private const int TeardownPhaseMs = 1_000;

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    public bool IsOpen => _isOpen;

    public AndroidUsbSerialPort()
    {
        // Detach is the important callback — it notices an unplug instantly
        // instead of waiting for the read loop to fault on the next bulk
        // transfer. Attach stays a no-op: the session layer's 2 s reconnect
        // poller re-enumerates and picks the device up on its next tick
        // (uniform with Windows COM-port arrival). No-op lambdas instead of
        // null because the library forwards them into Action.Invoke.
        // Param names are `attached`/`detached` in this vendored version —
        // the `*Callback` names broke SendIt's build once (documented trap).
        UsbDriverFactory.RegisterUsbBroadcastReceiver(
            isShowToast: false,
            attached: _ => { },
            detached: OnUsbDetached,
            errorCallback: _ => { });
    }

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        var devices = SupportedDevices();

        // Request permission for any unpermissioned device (this runs from
        // the Settings Refresh gesture). Granting also unmasks the serial,
        // so the next refresh lists the more specific VID:PID:Serial form.
        foreach (var device in devices)
        {
            if (!UsbManagerHelper.HasPermission(device))
                UsbManagerHelper.RequestPermission(device);
        }

        return Task.FromResult(Identifiers(devices));
    }

    /// <summary>
    /// The PASSIVE half of the seam (round 12 §6 F4): the same supported-device
    /// listing with the <see cref="UsbManagerHelper.RequestPermission"/> loop
    /// LEFT OUT. This is the one the connection page's 2 s poll calls, and the
    /// reason the seam exists at all — a permission dialog raised from a timer
    /// has no gesture behind it and would land on whatever screen the operator
    /// was using.
    ///
    /// <para>Consequence, accepted: an unpermissioned device lists under its
    /// bare VID:PID (Android masks the serial number without permission), so
    /// the poll can show a port whose identifier gains its serial suffix once
    /// Refresh or Connect obtains the grant. That is a less specific name for
    /// the same cable, not a wrong one.</para>
    /// </summary>
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync()
        => Task.FromResult(Identifiers(SupportedDevices()));

    /// <summary>Only devices the vendored library can actually drive: listing a
    /// hub-attached mouse (and popping a permission dialog for it) helps
    /// nobody. SendIt listed everything; deliberate narrowing here.</summary>
    private static List<UsbDevice> SupportedDevices()
        => UsbManagerHelper.GetAllUsbDevices()
            .Where(d => UsbDriverFactory.HasSupportedDriver(d.VendorId, d.ProductId))
            .ToList();

    private static IReadOnlyList<string> Identifiers(List<UsbDevice> devices)
        => devices.Select(d => BuildIdentifier(d).ToString()).ToList();

    public async Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_isOpen)
            throw new InvalidOperationException("Port is already open.");

        // THE PUBLICATION ORDER IS A CONTRACT, and it starts HERE — before any
        // observable action, the driver open included (D2 audit rounds 1 and 2,
        // MAJOR 1). Four ordered steps:
        //
        //   1. GENERATION ↑  — every previous session's read loop becomes
        //                      STALE at this instruction, before this method
        //                      touches the device at all.
        //   2. LATCH RESET   — arm the disconnect latch for the new session
        //                      (only once the open has actually succeeded).
        //   3. IsOpen = true — the session becomes OBSERVABLE.
        //   4. LOOP START    — the new loop begins, under its own generation.
        //
        // WHY THE VERY TOP, and not just before the latch reset. An open can
        // FAIL — no device, permission refused, the driver's own open throws —
        // and a failed open is exactly what a reconnect attempt against a
        // half-present adapter produces. With the increment further down, an
        // abandoned read from the dead session still compared CURRENT for the
        // whole duration of that attempt, so it could surface stale lines as
        // if they were live traffic while the operator watched a reconnect
        // fail.
        //
        // FAILURE-PATH INVARIANT, which is what makes doing this first safe:
        // if anything below throws, the object is left with old loops STALE
        // (they can no longer emit or disconnect anything), the latch
        // UN-RESET, IsOpen FALSE, no loop started — and one generation number
        // consumed but unused. That last is harmless: the counter is compared,
        // never accounted, and the next open advances it again.
        //
        // It must stay AFTER the already-open guard above, though: rejecting a
        // double-open must not invalidate the LIVE session's loop.
        int generation = Interlocked.Increment(ref _generation);

        var portName = settings.PortName
            ?? throw new ArgumentException("PortSettings.PortName is required.", nameof(settings));

        if (!UsbPortIdentifier.TryParse(portName, out var want))
            throw new ArgumentException(
                $"Invalid USB port identifier '{portName}'. Expected VID:PID or VID:PID:Serial " +
                "(hex, e.g. '0403:6001'). Use Refresh on the Connection page to list devices.",
                nameof(settings));

        // Resolve the saved identifier to the device's CURRENT DeviceId —
        // Android assigns a fresh one on every replug, so we match by
        // VID/PID(/Serial) instead of storing it.
        UsbDevice? device = UsbManagerHelper.GetAllUsbDevices()
            .FirstOrDefault(d => want.Matches(d.VendorId, d.ProductId, TryReadSerial(d)));

        if (device is null)
            throw new InvalidOperationException(
                $"USB device matching '{portName}' is not connected. " +
                "Plug in the USB-serial adapter and try again.");

        if (!UsbManagerHelper.HasPermission(device))
        {
            UsbManagerHelper.RequestPermission(device);
            throw new UnauthorizedAccessException(
                $"USB permission for '{portName}' has not been granted. " +
                "Approve the permission dialog, then press Connect again.");
        }

        var parity = settings.Parity switch
        {
            PortParity.Even => LibParity.Even,
            PortParity.Odd => LibParity.Odd,
            _ => LibParity.None,
        };
        var stopBits = settings.StopBits == PortStopBits.Two ? LibStopBits.Two : LibStopBits.One;

        var driver = UsbDriverFactory.CreateUsbDriver(device.DeviceId);
        await driver.OpenAsync(settings.BaudRate, (byte)settings.DataBits, stopBits, parity)
            .ConfigureAwait(false);
        // No flow control on the PRC-138 link; DTR asserted like the
        // Windows implementation's default line state (SendIt-proven).
        driver.SetDtrEnabled(true);
        _driver = driver;

        // STEP 2 of the publication contract stated at the top of this method.
        // The latch reset happens only HERE, after the open has succeeded, so
        // a failure partway through cannot leave it armed for the next try —
        // while the generation (step 1) has been current since method entry.
        Interlocked.Exchange(ref _disconnectFired, 0);
        _openIdentifier = BuildIdentifier(device);
        _isOpen = true;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoopCts = cts;
        // The driver and the generation are passed IN, not read from fields:
        // a loop must keep working against the port it was started for even
        // after CloseAsync has cleared those fields for the next session.
        _readLoopTask = Task.Run(
            () => RunReadLoopAsync(driver, generation, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// BOUNDED teardown (round 13 D2, repair 1). Every step here could block
    /// forever against a device that has physically gone: the cancellation's
    /// own callbacks, the read loop's parked bulk transfer, and the driver's
    /// close. The old version awaited all three unbounded, on the thread the
    /// session teardown was running on — which on Android is how a cable pull
    /// froze the app.
    ///
    /// <para><b>Shape.</b> CAPTURE the session's three handles into locals and
    /// CLEAR the fields first, so a concurrent reopen builds its own state
    /// against a clean object and nothing here can touch the new session's.
    /// Then two explicit <see cref="TeardownPhaseMs"/> phases: phase 1 is
    /// cancellation INITIATION, its COMPLETION and the read-loop join
    /// together (<c>CancelAsync</c> runs registered callbacks and is itself
    /// potentially unbounded, so it goes INSIDE the cap, not before it);
    /// phase 2 is the driver close. A phase that times out is ABANDONED and
    /// we proceed — best-effort is correct when the hardware is already
    /// gone, and the process, not this method, is what finally reclaims a
    /// wedged handle.</para>
    ///
    /// <para>Contract preserved: <see cref="IsOpen"/> is false before anyone
    /// can observe anything, and the disconnect latch is taken first so the
    /// read loop's inevitable fault on driver close does NOT surface as a
    /// spontaneous disconnect — an explicit close means the caller knows.</para>
    /// </summary>
    public async Task CloseAsync()
    {
        // Capture-then-clear, in that order: everything below works on locals.
        var readLoopTask = _readLoopTask;
        var readLoopCts = _readLoopCts;
        var driver = _driver;
        _readLoopTask = null;
        _readLoopCts = null;
        _driver = null;

        if (driver is null && !_isOpen)
        {
            readLoopCts?.Dispose();
            return;
        }

        // Latch first so the read loop's pending fault on driver close
        // doesn't surface as a disconnect (explicit close = caller knows).
        Interlocked.Exchange(ref _disconnectFired, 1);
        _isOpen = false;
        _openIdentifier = null;

        // PHASE 1 — cancel, wait for the cancellation to complete, and join
        // the read loop, all inside ONE cap.
        if (readLoopCts is not null)
        {
            await RunBoundedAsync(async () =>
            {
                try { await readLoopCts.CancelAsync().ConfigureAwait(false); }
                catch (Exception) { /* tearing down */ }

                if (readLoopTask is not null)
                {
                    try { await readLoopTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception) { /* suppress: tearing down */ }
                }
            }).ConfigureAwait(false);

            // Safe even if the join was abandoned: a stale loop's token
            // source being disposed only makes its next check throw, and its
            // effects are already gated off by generation.
            try { readLoopCts.Dispose(); } catch (Exception) { }
        }

        // PHASE 2 — the driver close, in its own cap.
        if (driver is not null)
        {
            await RunBoundedAsync(async () =>
            {
                try { await driver.CloseAsync().ConfigureAwait(false); }
                catch (Exception) { /* best-effort close */ }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Run <paramref name="work"/> on the pool and wait at most
    /// <see cref="TeardownPhaseMs"/> for it. The pool hop is load-bearing:
    /// the work's FIRST synchronous stretch (cancellation callbacks, a
    /// blocking driver call) would otherwise run on the caller's thread and
    /// blow the deadline before anything got a chance to race it.</summary>
    private static async Task RunBoundedAsync(Func<Task> work)
    {
        var task = Task.Run(work);
        await Task.WhenAny(task, Task.Delay(TeardownPhaseMs)).ConfigureAwait(false);
        // No await of `task` on the timeout path: abandoning it is the point.
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var driver = _driver;
        if (!_isOpen || driver is null)
            throw new InvalidOperationException("Port is not open.");

        // Just the bulk transfer: SerialTransport's writer worker already
        // serializes writes and keeps them off the read path.
        var array = data.ToArray();
        try
        {
            await driver.WriteAsync(array, 0, array.Length, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BulkTransferException ex)
        {
            // Bulk transfer failure = cable pulled mid-write. Surface as a
            // disconnect so the session tears down without waiting for the
            // read loop to also notice.
            RaiseDisconnect(ex);
            throw new IOException("USB serial write failed — adapter may be disconnected.", ex);
        }
        catch (Exception ex)
        {
            // Anything else non-cancellation from the driver's write path
            // (faulted internal tasks after an unplug, dead connection) is
            // fatal for the session the same way (WindowsSerialPort parity).
            RaiseDisconnect(ex);
            throw new IOException("USB serial write failed — adapter may be disconnected.", ex);
        }
    }

    /// <summary>
    /// The read loop, owned by ONE session. Both of its inputs are arguments
    /// rather than fields: <paramref name="driver"/> is the port it was
    /// started for, and <paramref name="generation"/> is the session it
    /// belongs to. Every effect it can have on the outside world — emitting
    /// bytes, marking a disconnect — is gated on that generation still being
    /// current, so a loop that unparks after a reopen exits quietly instead
    /// of corrupting the session that replaced it.
    /// </summary>
    private async Task RunReadLoopAsync(UsbDriverBase driver, int generation, CancellationToken ct)
    {
        var buffer = new byte[UsbDriverBase.DefaultBufferLength];
        while (!ct.IsCancellationRequested && IsCurrentGeneration(generation))
        {
            int bytesRead;
            try
            {
                bytesRead = await driver.ReadAsync(buffer, 0, buffer.Length, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Read fault during normal operation = device gone. Funnel
                // through RaiseDisconnect — never fault the data event
                // channel (seam contract; SendIt discipline).
                //
                // …but ONLY for our own session. A stale loop's fault is the
                // EXPECTED consequence of its port having been closed, and
                // reporting it would disconnect whatever session is live now.
                if (!IsCurrentGeneration(generation)) return;
                RaiseDisconnect(ex);
                return;
            }

            // Re-checked after the await, not just at the top: the generation
            // can advance while a read is parked, which is precisely the
            // pull-then-immediately-replug window.
            if (bytesRead > 0 && IsCurrentGeneration(generation))
                DataReceived?.Invoke(this, new SerialDataEventArgs(buffer.AsSpan(0, bytesRead).ToArray()));
        }
    }

    /// <summary>Is the caller's session still the live one?</summary>
    private bool IsCurrentGeneration(int generation) => Volatile.Read(ref _generation) == generation;

    /// <summary>
    /// Latched disconnect emitter: fires at most once per open session and
    /// flips <see cref="IsOpen"/> BEFORE the event so a subscriber reading
    /// it inside the handler sees consistent state (seam contract).
    /// </summary>
    private void RaiseDisconnect(Exception ex)
    {
        if (!TryLatchDisconnect()) return;
        Disconnected?.Invoke(this, new SerialDisconnectedEventArgs(ex));
    }

    /// <summary>The single once-per-open-session latch point (every
    /// disconnect source — read fault, failed write, detach broadcast —
    /// funnels through it): flips <see cref="IsOpen"/> false before anyone
    /// can observe the event. Returns false if already latched.</summary>
    private bool TryLatchDisconnect()
    {
        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0) return false;
        _isOpen = false;
        _openIdentifier = null;
        return true;
    }

    /// <summary>
    /// USB DETACHED broadcast: tear down only if the unplugged device is
    /// the one we have open. Tolerant compare (a masked serial on the
    /// broadcast side still matches on VID:PID): worst case is a spurious
    /// disconnect for a same-model sibling adapter — which the session's
    /// reconnect poller heals — versus silently missing our own detach.
    ///
    /// Stage 8 (deferred-ledger fix, Stage 7 audit F4): the broadcast
    /// arrives on the MAIN thread, and the Disconnected event chain runs
    /// the session's full synchronous teardown (transport error → radio
    /// Disconnect/transport Close — worst case ~2 s of port reaping).
    /// Bounded and under the 5 s ANR threshold, but pointless jank — so the
    /// latch is taken HERE (synchronously: IsOpen is false and duplicate
    /// sources are suppressed before the broadcast returns) and only the
    /// event dispatch + teardown move to the thread pool, where the read
    /// loop's own faults already raise from. Once-latch semantics are
    /// unchanged: one Interlocked latch point, at most one event per open
    /// session.
    /// </summary>
    private void OnUsbDetached(UsbDevice device)
    {
        var open = _openIdentifier;
        if (!_isOpen || open is null) return;
        if (!open.Value.Matches(device.VendorId, device.ProductId, TryReadSerial(device))) return;
        if (!TryLatchDisconnect()) return;
        Task.Run(() => Disconnected?.Invoke(
            this, new SerialDisconnectedEventArgs(new IOException("USB device detached."))));
    }

    public async ValueTask DisposeAsync()
    {
        // Singleton with process lifetime (DI) — the broadcast receiver
        // registration is deliberately left in place.
        await CloseAsync().ConfigureAwait(false);
    }

    private static UsbPortIdentifier BuildIdentifier(UsbDevice device)
        => new(device.VendorId, device.ProductId, TryReadSerial(device));

    private static string? TryReadSerial(UsbDevice device)
    {
        // SerialNumber throws without USB permission on some Android
        // versions; treat any failure as "unknown serial" (masked).
        try { return device.SerialNumber; }
        catch { return null; }
    }
}
