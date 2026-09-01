using Falcon.Core.Transport;

namespace Falcon.App.Core.Demo;

/// <summary>
/// The one DI seam change for demo mode (plan/plan-demo-radio.md): wraps the
/// platform <see cref="ISerialPort"/>, appends "DEMO" to the port list, and
/// routes an open of that name to <see cref="DemoSerialPort"/> — every other
/// port name goes to the real platform port untouched. Demo mode is only
/// ever entered by explicitly picking "DEMO" in Settings.
/// </summary>
public sealed class DemoCapableSerialPort : ISerialPort
{
    private readonly ISerialPort _platform;
    private readonly DemoSerialPort _demo = new();
    private volatile ISerialPort? _active;

    /// <summary>Takes ownership of <paramref name="platform"/>: Dispose
    /// disposes it (SerialTransport ownership convention).</summary>
    public DemoCapableSerialPort(ISerialPort platform)
    {
        _platform = platform;
        _platform.DataReceived += (_, e) => DataReceived?.Invoke(this, e);
        _demo.DataReceived += (_, e) => DataReceived?.Invoke(this, e);
        // The demo port never raises Disconnected; forwarding the platform's
        // preserves the yank path exactly.
        _platform.Disconnected += (_, e) => Disconnected?.Invoke(this, e);
    }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    public bool IsOpen => _active?.IsOpen ?? false;

    /// <summary>Platform ports first, "DEMO" always last.</summary>
    public async Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        var real = await _platform.GetAvailablePortsAsync().ConfigureAwait(false);
        return [.. real, DemoSerialPort.DemoPortName];
    }

    /// <summary>The passive half (round 12 §6 F4), same shape: the PLATFORM's
    /// permissionless listing with "DEMO" appended last. The demo port is
    /// always present — it is software — so the wrapper's own contribution is
    /// identical on both paths and only the platform call differs.</summary>
    public async Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync()
    {
        var real = await _platform.GetAvailablePortsPassiveAsync().ConfigureAwait(false);
        return [.. real, DemoSerialPort.DemoPortName];
    }

    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
    {
        // Port names round-trip verbatim from enumeration (Stage 7 note in
        // docs/software-architecture.md), so the match is ordinal.
        var target = settings.PortName == DemoSerialPort.DemoPortName ? (ISerialPort)_demo : _platform;
        _active = target;
        return target.OpenAsync(settings, cancellationToken);
    }

    public Task CloseAsync() => _active?.CloseAsync() ?? Task.CompletedTask;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => (_active ?? throw new InvalidOperationException("Port is not open."))
            .WriteAsync(data, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _demo.DisposeAsync().ConfigureAwait(false);
        await _platform.DisposeAsync().ConfigureAwait(false);
    }
}
