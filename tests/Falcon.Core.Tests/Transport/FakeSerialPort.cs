using System.Text;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests.Transport;

/// <summary>
/// Byte-injecting fake ISerialPort. Doctrine (plan §5, docs/tests.md): REPLAY
/// only — it records writes and injects captured bytes when the test says so;
/// it NEVER answers a command. Tests inject from whatever thread they choose.
/// </summary>
internal sealed class FakeSerialPort : ISerialPort
{
    private readonly object _lock = new();
    private readonly List<byte[]> _writes = [];
    private readonly List<TaskCompletionSource> _stuckCloses = [];

    public bool IsOpen { get; private set; }
    public PortSettings? OpenedWith { get; private set; }
    public int CloseCalls { get; private set; }

    /// <summary>When set, WriteAsync throws this instead of recording.</summary>
    public Exception? FailWrites { get; set; }

    /// <summary>
    /// STUCK MODE (round 13 D2): <see cref="CloseAsync"/> returns a task that
    /// NEVER completes — a driver wedged against hardware that is physically
    /// gone. It models the failure the transport-level backstop exists for,
    /// and nothing else in the suite could produce it: every other fake path
    /// either completes or throws, and a throw is not a hang.
    /// </summary>
    public bool StuckClose { get; set; }

    /// <summary>
    /// DELAYED-CLOSE MODE (round 13 D2): <see cref="CloseAsync"/> completes,
    /// but only after this many milliseconds. Set it ABOVE the transport's
    /// backstop to model the nastier case — the close the transport gave up
    /// waiting for finishes LATER, while a new session is already running on
    /// the same port object.
    /// </summary>
    public int CloseDelayMs { get; set; }

    /// <summary>Set by the delayed close when it finally completes, so a test
    /// can tell "abandoned and still pending" from "abandoned and since
    /// finished" without sleeping on a guess.</summary>
    public bool DelayedCloseCompleted { get; private set; }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    public IReadOnlyList<string> WrittenCommands
    {
        get
        {
            lock (_lock) return _writes.Select(w => Encoding.ASCII.GetString(w)).ToList();
        }
    }

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
        => Task.FromResult((IReadOnlyList<string>)["FAKE"]);

    /// <summary>Round 12 §6 F4's passive seam. This double exists for the
    /// TRANSPORT tests, which never enumerate — both paths return the same
    /// single name so neither can be told apart by accident here; the
    /// path-discrimination pins live with the connection page's poll.</summary>
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => GetAvailablePortsAsync();

    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
    {
        IsOpen = true;
        OpenedWith = settings;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        IsOpen = false;
        CloseCalls++;

        // A wedged driver: the caller is left holding a task that never
        // completes. Only the transport's own deadline — or ReleaseCloses —
        // can end the wait.
        if (StuckClose)
        {
            var parked = new TaskCompletionSource();
            lock (_lock) _stuckCloses.Add(parked);
            return parked.Task;
        }

        if (CloseDelayMs > 0)
            return Task.Delay(CloseDelayMs).ContinueWith(_ => DelayedCloseCompleted = true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// UN-STICK the port: stop parking closes, complete every close already
    /// parked, and drop any delay. A stuck-close test MUST call this in a
    /// `finally` (D2 audit round 2).
    ///
    /// <para><b>Why the fixture depends on it.</b> The test class disposes its
    /// transport, and <c>SerialTransport.Dispose</c> calls <c>Close</c> — a
    /// SECOND close, with no test-side deadline around it. If the port were
    /// still stuck at that point, a regressed backstop would wedge the
    /// testhost during teardown even though the test itself had already failed
    /// cleanly, turning a precise assertion failure back into a hung run. The
    /// release makes disposal meet a port that closes instantly, whatever the
    /// verdict was.</para>
    /// </summary>
    public void ReleaseCloses()
    {
        StuckClose = false;
        CloseDelayMs = 0;

        TaskCompletionSource[] parked;
        lock (_lock)
        {
            parked = [.. _stuckCloses];
            _stuckCloses.Clear();
        }
        foreach (var p in parked) p.TrySetResult();
    }

    /// <summary>ROUND 15 (audit round 2): when set, every write BLOCKS on it
    /// until the test releases it. A real port may hold a write for up to
    /// 2 000 ms (Windows), and the difference between "the line left the
    /// queue" and "the port took the bytes" is only observable while one is
    /// in flight. Opt-in and null by default, so no existing test changes.</summary>
    public ManualResetEventSlim? WriteGate { get; set; }

    /// <summary>Writes that have STARTED (entered WriteAsync) — including one
    /// parked on <see cref="WriteGate"/>, which <see cref="WriteCount"/>
    /// cannot show because it counts writes that finished.</summary>
    public int WritesStarted => Volatile.Read(ref _writesStarted);

    private int _writesStarted;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new InvalidOperationException("Port is not open.");
        Interlocked.Increment(ref _writesStarted);
        if (FailWrites is not null) throw FailWrites;
        WriteGate?.Wait(cancellationToken);
        lock (_lock) _writes.Add(data.ToArray());
        return Task.CompletedTask;
    }

    /// <summary>Inject captured bytes on the calling thread (the fake's
    /// "read thread" is whatever thread the test calls this from).</summary>
    public void InjectBytes(string text)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));

    public void InjectBytes(byte[] chunk)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(chunk));

    /// <summary>Byte-level disconnect (USB yank). Seam contract: IsOpen flips
    /// false BEFORE the event is emitted.</summary>
    public void InjectDisconnect(Exception reason)
    {
        IsOpen = false;
        Disconnected?.Invoke(this, new SerialDisconnectedEventArgs(reason));
    }

    /// <summary>Poll until at least <paramref name="count"/> writes have been
    /// recorded. True if reached within the timeout.</summary>
    public bool WaitForWrites(int count, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_lock) { if (_writes.Count >= count) return true; }
            Thread.Sleep(5);
        }
        lock (_lock) return _writes.Count >= count;
    }

    public int WriteCount { get { lock (_lock) return _writes.Count; } }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}
