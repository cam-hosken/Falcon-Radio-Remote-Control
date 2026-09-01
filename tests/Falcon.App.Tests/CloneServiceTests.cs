using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>Records what the app SENT while forwarding everything to the demo
/// radio underneath — the only way to assert a campaign's LEG ORDER, which is
/// a property of the wire and not of any mirror.</summary>
internal sealed class RecordingDemoPort(DemoSerialPort inner) : ISerialPort
{
    private readonly List<string> _sent = [];
    private readonly object _lock = new();

    public DemoSerialPort Demo { get; } = inner;

    public IReadOnlyList<string> Sent
    {
        get { lock (_lock) return [.. _sent]; }
    }

    public void ClearSent() { lock (_lock) _sent.Clear(); }

    public bool IsOpen => Demo.IsOpen;

    public event EventHandler<SerialDataEventArgs>? DataReceived
    {
        add => Demo.DataReceived += value;
        remove => Demo.DataReceived -= value;
    }

    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => Demo.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => Demo.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => Demo.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() => Demo.CloseAsync();

    /// <summary>
    /// ROUND 15 A0 — opt-in, OFF by default: drop the <c>BAT ST</c> that
    /// CLOSES an ALE read operation (the one written immediately behind
    /// <c>SLFAD</c>/<c>INDAD</c>/<c>NETAD</c>/<c>CHG</c>/<c>EXCH</c>) and
    /// forward everything else. The radio really does swallow commands
    /// silently (R6, protocol.md "Command pacing"), and this is the
    /// DETERMINISTIC form of "an ALE read that gives up before the radio
    /// answers": the read's own sentinel can never be answered, so its
    /// timeout is the only possible outcome — no race against the answer, and
    /// no late answer left in flight to credit the NEXT leg early (the Q3
    /// late-answer doctrine, which is not that test's subject). Since round
    /// 15 A0 a ping's clock runs from the WIRE, so a merely SHORT timeout no
    /// longer outruns a fast answer by construction.
    /// </summary>
    public bool SwallowAleReadSentinels { get; set; }

    /// <summary>How many sentinels were actually dropped — the anti-vacuity
    /// for any test that arms the switch.</summary>
    public int SwallowedSentinels { get; private set; }

    /// <summary>
    /// AUDIT ROUND 1, and the OTHER half of the same fault class: instead of
    /// dropping the ALE read's closing <c>BAT ST</c>, HOLD IT BACK by this
    /// many milliseconds. The radio then answers it LATE — after the read's
    /// own timeout has already given up — which is the case that produces the
    /// documented stream SHIFT (that late answer credits the next queued
    /// ping). Nothing here is a radio behaviour: this is the port WRAPPER
    /// delaying bytes, exactly as a slow link would, and the demo radio is not
    /// taught anything (owner directive, 2026-08-22).
    /// </summary>
    public int DelayAleReadSentinelMs { get; set; }

    /// <summary>How many sentinels were actually held back — the anti-vacuity
    /// twin of <see cref="SwallowedSentinels"/>.</summary>
    public int DelayedSentinels { get; private set; }

    /// <summary>
    /// ROUND 17 (D10 pin c) — opt-in, null by default: DROP the first written
    /// line beginning with this prefix. It is the deterministic stand-in for a
    /// command the radio REFUSES to act on: the line is recorded as sent (so
    /// leg order is still observable) and the radio's state never moves, which
    /// is what the FULL VERIFY exists to notice.
    ///
    /// <para>Like every hook on this wrapper it lives on the PORT — the demo
    /// radio is taught nothing (owner directive I-11, 2026-08-22) — and a
    /// dropped line leaves no prompt behind, so a test that arms it should
    /// compress <c>SerialTransport.GateTimeoutMs</c>.</para>
    /// </summary>
    public string? SwallowFirstLineStartingWith { get; set; }

    /// <summary>True once that line was really dropped — the anti-vacuity
    /// check for any test that arms the switch.</summary>
    public bool SwallowedTheLine { get; private set; }

    /// <summary>
    /// AUDIT ROUND 2 (manager ruling) — the wrapper's third test-only timing
    /// hook, and the SYNCHRONOUS sibling of the two above: called with each
    /// line at the moment it has reached the wire, ON the transport's writer
    /// thread, before <c>WriteAsync</c> returns.
    ///
    /// <para><b>Why a hook and not a watcher.</b>
    /// <c>ASessionDropMidCampaign…</c> used to spawn a <c>Task.Run</c> that
    /// polled <see cref="Sent"/> every millisecond for the first <c>RXF</c>
    /// and then closed the session. Under a FULL-SUITE load that watcher was
    /// scheduled late, the drop landed after the write legs had finished, and
    /// the campaign reported "The session dropped during verification."
    /// instead of stopping mid-leg — red in every full run, green alone. A
    /// race cannot be tuned out with a shorter poll; it has to stop being a
    /// race. This hook fires INSIDE the write, so "the drop lands on the leg
    /// that wrote RXF" is a sequencing fact rather than a timing hope.</para>
    ///
    /// <para><b>Closing from inside a write is a SUPPORTED path</b>, not a
    /// trick: <c>SerialTransport.Close</c> carries an explicit WRITER
    /// SELF-JOIN GUARD (round 13 D2, repair 2a) precisely because a write
    /// fault tears the session down from the writer thread. The callback runs
    /// OUTSIDE this wrapper's lock, and AFTER the line has been forwarded, so
    /// the radio really did receive what the drop interrupts.</para>
    ///
    /// <para>It is on the WRAPPER, like its two siblings — the demo radio is
    /// taught nothing (owner directive I-11, 2026-08-22).</para>
    /// </summary>
    public Action<string>? OnLineWritten { get; set; }

    private static readonly string[] AleReadVerbs = ["SLFAD", "INDAD", "NETAD", "CHG", "EXCH"];
    private bool _sentinelArmed;
    private readonly List<Timer> _timers = [];

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var text = Encoding.ASCII.GetString(data.Span).Trim();
        bool forward = true;
        lock (_lock)
        {
            _sent.Add(text);
            if (SwallowFirstLineStartingWith is { } refused
                && text.StartsWith(refused, StringComparison.Ordinal))
            {
                SwallowFirstLineStartingWith = null;
                SwallowedTheLine = true;
                return Task.CompletedTask;          // the radio never acts on it
            }
            bool closesAleRead = _sentinelArmed && text == "BAT ST";
            if (SwallowAleReadSentinels || DelayAleReadSentinelMs > 0)
                _sentinelArmed = AleReadVerbs.Contains(text.Split(' ')[0]);

            if (closesAleRead && SwallowAleReadSentinels)
            {
                SwallowedSentinels++;
                forward = false;                    // the radio simply never hears it
            }
            else if (closesAleRead && DelayAleReadSentinelMs > 0)
            {
                DelayedSentinels++;
                var held = data.ToArray();
                _timers.Add(new Timer(
                    _ => { try { Demo.WriteAsync(held).GetAwaiter().GetResult(); } catch (InvalidOperationException) { } },
                    null, DelayAleReadSentinelMs, Timeout.Infinite));
                forward = false;                    // …it hears it, just not yet
            }
        }

        // The demo's write is SYNCHRONOUS (it returns Task.CompletedTask), so
        // this neither blocks nor moves when anything happens — and it is what
        // lets the hook below run at the moment the line reached the wire,
        // outside the lock, on the writer thread.
        if (forward) Demo.WriteAsync(data, cancellationToken).GetAwaiter().GetResult();
        OnLineWritten?.Invoke(text);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // A held sentinel firing into a closed demo would take the test host
        // down with it (the DeferredModeEntryPort lesson).
        lock (_lock)
        {
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
        return Demo.DisposeAsync();
    }
}

/// <summary>
/// ROUND 16 FIXES S4 — a <c>DI 0 99</c> ANSWER THAT LOST ROWS, reproduced at
/// the byte seam: every <c>CH nn …</c> row at or above
/// <see cref="DropChannelRowsFrom"/> is dropped on the way UP, and everything
/// else — the closing prompt, the sentinel and its answer, every other leg —
/// passes through untouched.
///
/// <para>Nothing here is a radio behaviour. This is the port WRAPPER deleting
/// bytes, exactly as a link that lost them would, and the demo radio is taught
/// nothing (owner directive I-11, 2026-08-22) — the
/// <see cref="RecordingDemoPort"/>/<see cref="DeferredModeEntryPort"/>
/// idiom.</para>
///
/// <para>It filters the COMPLETE LINES inside each chunk and forwards the
/// chunk's trailing fragment untouched — never across chunks. That matters
/// twice: a mode prompt has no terminator at all, so holding a tail back would
/// wedge the transport's write gate rather than truncate a dump; and the demo
/// frames one whole reply as one chunk (<c>DemoSerialPort.Frame</c>), so every
/// row arrives whole. If that framing ever changes, a split row passes through
/// unfiltered — which is why every test here asserts the exact drop COUNT.</para>
/// </summary>
internal sealed class TruncatingDemoPort : ISerialPort
{
    private static readonly Regex ChannelRow = new(@"^CH (?<n>\d{2}) ", RegexOptions.Compiled);

    private readonly RecordingDemoPort _inner;
    private readonly object _lock = new();

    public TruncatingDemoPort(RecordingDemoPort inner)
    {
        _inner = inner;
        _inner.DataReceived += OnData;
    }

    /// <summary>Drop every <c>CH nn</c> dump row with <c>nn</c> at or above
    /// this. Null (the default) forwards everything, so the wrapper is inert
    /// until a test arms it.</summary>
    public int? DropChannelRowsFrom { get; set; }

    /// <summary>How many rows were actually dropped — the anti-vacuity check
    /// for any test that arms the switch.</summary>
    public int DroppedChannelRows { get; private set; }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    private void OnData(object? sender, SerialDataEventArgs e)
    {
        if (DropChannelRowsFrom is not { } floor)
        {
            DataReceived?.Invoke(this, e);
            return;
        }

        var text = Encoding.ASCII.GetString(e.Data);
        int cut = text.LastIndexOf('\n');
        if (cut < 0)
        {
            DataReceived?.Invoke(this, e);       // no complete line here — pass it on
            return;
        }

        string forward;
        lock (_lock)
        {
            var kept = new StringBuilder();
            foreach (var line in text[..cut].Split('\n'))
            {
                var m = ChannelRow.Match(line.Trim('\r'));
                if (m.Success && int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) >= floor)
                {
                    DroppedChannelRows++;
                    continue;
                }
                kept.Append(line).Append('\n');
            }
            kept.Append(text[(cut + 1)..]);      // the trailing fragment, untouched
            forward = kept.ToString();
        }

        if (forward.Length > 0)
            DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(forward)));
    }

    public bool IsOpen => _inner.IsOpen;
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() => _inner.CloseAsync();
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(data, cancellationToken);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// ROUND 17 F6 — THE CAPTURED INTERLEAVE, replayed at the byte seam:
/// <c>bench/transcripts/r15-p1-wire-read-20260822-194203.jsonl</c>, records
/// t19697-t27360.
///
/// <para>The wire ordering this reproduces, verbatim from that capture:</para>
/// <list type="number">
/// <item><c>DI 0 99</c> is written (t19697) and the radio draws a BARE
/// <c>SSB&gt;</c> 14 ms later, at t19711 — BEFORE one row of the answer. That
/// prompt releases the write gate, so the leg's sentinel <c>BAT ST</c> goes out
/// (t19698 queued, written t21717) while the dump is still streaming. This
/// wrapper therefore moves the demo's TRAILING prompt to the FRONT: one prompt
/// per answer, exactly where the radio put it.</item>
/// <item>rows <c>CH 00</c> (t19887) through <c>CH 27</c> (t21915) stream at
/// ~72 ms each — <see cref="SplitAfterRow"/> of them.</item>
/// <item><c>Battery Status FULL 26.2V</c> lands at t21947, BETWEEN two rows:
/// the sentinel answers MID-DUMP. The wrapper forwards it in place and only
/// then starts the release clock, so the ordering is a sequencing FACT and not
/// a timing hope (the <see cref="RecordingDemoPort.OnLineWritten"/>
/// lesson).</item>
/// <item>the dump RESUMES and completes — <c>CH 99</c> at t27344 and the
/// <c>CHAN nn</c> trailer at t27360, 100 rows in all (counted in the
/// transcript: 100 <c>CH</c> lines between the write and the trailer).</item>
/// </list>
///
/// <para>Nothing here is a radio behaviour. This is the port WRAPPER
/// re-timing bytes the demo already produced, exactly as the real radio timed
/// them, and the demo is taught nothing (owner directive I-11,
/// 2026-08-22) — the <see cref="TruncatingDemoPort"/> idiom.</para>
///
/// <para><see cref="DripMs"/> serves the OTHER bound: rows released one at a
/// time keep the reported set GROWING, so the quiet window never expires and
/// only the hard cap can end the wait.</para>
/// </summary>
internal sealed class InterleavingDemoPort : ISerialPort
{
    private static readonly Regex ChannelRow = new(@"^CH \d{2} ", RegexOptions.Compiled);

    private readonly ISerialPort _inner;
    private readonly object _lock = new();
    private readonly List<Timer> _timers = [];
    private List<string>? _held;
    private bool _releaseScheduled;
    private bool _disposed;

    public InterleavingDemoPort(ISerialPort inner)
    {
        _inner = inner;
        _inner.DataReceived += OnData;
    }

    /// <summary>Forward this many <c>CH nn</c> rows, then HOLD the rest until
    /// the sentinel's battery answer has gone up. Null (the default) forwards
    /// everything, so the wrapper is inert until a test arms it.</summary>
    public int? SplitAfterRow { get; set; }

    /// <summary>How long after the interleaved battery answer the held rows
    /// start arriving.</summary>
    public int ReleaseAfterMs { get; set; } = 300;

    /// <summary>0 = the held rows arrive as ONE chunk. Above 0 = one row every
    /// this many milliseconds.</summary>
    public int DripMs { get; set; }

    /// <summary>Hold the rows until a test calls <see cref="ReleaseNow"/>, and
    /// start NO clock of its own when the battery answer goes by. The race pin
    /// needs the release to happen at a point IT chooses, inside the barrier's
    /// own poll, rather than at a moment the wall clock chooses.</summary>
    public bool ReleaseOnDemand { get; set; }

    /// <summary>Push held rows up RIGHT NOW, on the caller's thread, returning
    /// once the transport has taken them. <paramref name="count"/> null sends
    /// every one that is left; a number sends that many, which is how the race
    /// pin grows the set WITHOUT completing it.</summary>
    public void ReleaseNow(int? count = null)
    {
        List<string> rows;
        lock (_lock)
        {
            if (_disposed || _held is null) return;
            rows = _held;
        }
        int take = Math.Min(count ?? rows.Count, rows.Count);
        Push(string.Concat(rows.Take(take).Select(r => r + "\r\n")));
        bool finished;
        lock (_lock)
        {
            ReleasedRows += take;
            rows.RemoveRange(0, take);
            finished = rows.Count == 0;
            if (finished) _held = null;
        }
        ReleasedAfterBattery = true;
        if (finished && Trailer is not null) Push(Trailer + "\r\n");
    }

    /// <summary>The dump's own trailer, as the capture carries it
    /// (<c>CHAN 25</c> there; the demo's operating channel here). Null omits
    /// it.</summary>
    public string? Trailer { get; set; }

    /// <summary>How many dumps were actually split — the anti-vacuity check
    /// for any test that arms the switch.</summary>
    public int SplitDumps { get; private set; }

    /// <summary>How many rows were held back, and how many have since been let
    /// through — the anti-vacuity twins.</summary>
    public int HeldRows { get; private set; }

    public int ReleasedRows { get; private set; }

    /// <summary>True once the held rows were released BEHIND a battery answer,
    /// which is the captured ordering this fixture exists for.</summary>
    public bool ReleasedAfterBattery { get; private set; }

    private void Push(string text)
    {
        lock (_lock) { if (_disposed) return; }
        DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));
    }

    private void OnData(object? sender, SerialDataEventArgs e)
    {
        if (SplitAfterRow is not { } split) { DataReceived?.Invoke(this, e); return; }

        var text = Encoding.ASCII.GetString(e.Data);

        // The sentinel's answer, in the place the radio put it: forward it
        // first, THEN start the clock on the rest of the dump.
        if (_held is not null && !_releaseScheduled
            && text.Contains("Battery Status", StringComparison.Ordinal))
        {
            _releaseScheduled = true;
            DataReceived?.Invoke(this, e);
            if (!ReleaseOnDemand) Schedule(ReleaseAfterMs);
            return;
        }

        var parts = text.Split("\r\n");
        var rows = parts.Where(p => ChannelRow.IsMatch(p)).ToList();
        if (rows.Count <= split) { DataReceived?.Invoke(this, e); return; }

        lock (_lock)
        {
            SplitDumps++;
            _held = [.. rows.Skip(split)];
            HeldRows = _held.Count;
        }
        // The BARE PROMPT FIRST (t19711) — the demo hangs it off the END of the
        // answer, and moving it is the whole reason the sentinel can be written
        // into the middle of a dump at all.
        Push("\r\n" + parts[^1]);
        Push(string.Concat(rows.Take(split).Select(r => r + "\r\n")));
    }

    private void Schedule(int dueMs)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _timers.Add(new Timer(_ => Release(), null, dueMs, Timeout.Infinite));
        }
    }

    private void Release()
    {
        List<string> rows;
        lock (_lock)
        {
            if (_disposed || _held is null) return;
            rows = _held;
        }

        if (DripMs <= 0)
        {
            Push(string.Concat(rows.Select(r => r + "\r\n"))
                + (Trailer is null ? "" : Trailer + "\r\n"));
            lock (_lock) { ReleasedRows += rows.Count; _held = null; }
            ReleasedAfterBattery = true;
            return;
        }

        Push(rows[0] + "\r\n");
        lock (_lock)
        {
            ReleasedRows++;
            rows.RemoveAt(0);
            if (rows.Count == 0) _held = null;
        }
        ReleasedAfterBattery = true;
        if (rows.Count > 0) Schedule(DripMs);
        else if (Trailer is not null) Push(Trailer + "\r\n");
    }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public bool IsOpen => _inner.IsOpen;
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() => _inner.CloseAsync();
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(data, cancellationToken);

    public ValueTask DisposeAsync()
    {
        // A drip firing into a closed demo would take the test host down with
        // it (the DeferredModeEntryPort lesson).
        lock (_lock)
        {
            _disposed = true;
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
            _held = null;
        }
        return _inner.DisposeAsync();
    }
}

/// <summary>
/// THE FIELD RADIO'S MODE-ENTRY ORDERING (plan-round14.md §4 Phase F1),
/// reproduced at the byte seam: the radio ACCEPTS the mode command, answers
/// everything queued behind it AT THE OLD PROMPT, and only emits the new
/// mode's prompt when its entry lifecycle (generate → tune → …) finishes.
///
/// <para>Verbatim from
/// <c>bench/transcripts/field-clone-console-20260820-1738.txt</c>, attempt 2:
/// <c>HO</c> at 17:39:32.560 with its sentinel <c>BAT ST</c> queued 1 ms
/// behind it; <c>Battery Status</c> answered at <b>:32.811</b> followed by an
/// <c>ALE&gt;</c> prompt; the <c>HOP&gt;</c> prompt only at <b>:38.806</b>,
/// six seconds later, after two generate/TUNE-FAULT cycles. Attempt 1 has the
/// same shape (battery at :05.590, <c>HOP&gt;</c> at :08.522).</para>
///
/// <para>The demo radio answers a mode switch with its prompt IMMEDIATELY, so
/// it cannot show this on its own. This port holds the mode command back until
/// the next battery answer has been delivered — which is the whole of the
/// difference, and enough to convict.</para>
/// </summary>
internal sealed class DeferredModeEntryPort : ISerialPort
{
    private readonly RecordingDemoPort _inner;
    private readonly object _lock = new();
    private readonly List<Timer> _timers = [];
    private string? _command;
    private int? _lifecycleMs;
    private string _lifecycleLines = "";
    private string? _swallowAfterRelease;
    private bool _swallowArmed;

    public DeferredModeEntryPort(RecordingDemoPort inner)
    {
        _inner = inner;
        _inner.DataReceived += OnData;
    }

    /// <summary>Hold the FIRST write of <paramref name="command"/> back for
    /// <paramref name="lifecycleMs"/>, the way the live rig holds its new
    /// prompt back for the length of its entry lifecycle. NULL = never release
    /// it: the radio that accepts the switch and never reaches the new
    /// prompt.
    /// <para><paramref name="swallowAfterRelease"/> then DROPS the first write
    /// of that command issued after the release — the radio that reaches its
    /// new prompt and then stops answering, which is what the mode gate's
    /// closing sentinel exists to catch.</para></summary>
    public void Defer(string command, int? lifecycleMs, string lifecycleLines = "",
        string? swallowAfterRelease = null)
    {
        lock (_lock)
        {
            _command = command;
            _lifecycleMs = lifecycleMs;
            _lifecycleLines = lifecycleLines;
            _swallowAfterRelease = swallowAfterRelease;
            _swallowArmed = false;
        }
    }

    /// <summary>True once the swallowed command has actually been dropped —
    /// the anti-vacuity check for the sentinel pin.</summary>
    public bool Swallowed { get; private set; }

    /// <summary>True once the held command has been let through — the
    /// anti-vacuity check that the deferral actually happened.</summary>
    public bool Released { get; private set; }

    /// <summary>When the command was withheld, so a test can measure the gate's
    /// wait from the switch itself rather than from the whole campaign.</summary>
    public long HeldAtTicks { get; private set; }

    /// <summary>Push bytes up as if the radio had said them, without asking it
    /// anything. Used to put an ANNOUNCED-only fact (the radio's own
    /// <c>SCANNING</c> line) into the mirror; the demo models no scan.</summary>
    public void Inject(string text)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    private void OnData(object? sender, SerialDataEventArgs e) => DataReceived?.Invoke(this, e);

    public bool IsOpen => _inner.IsOpen;
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() { StopTimers(); return _inner.CloseAsync(); }
    public ValueTask DisposeAsync() { StopTimers(); return _inner.DisposeAsync(); }

    /// <summary>The deferral's timers are the only thing here that outlives a
    /// test method, so they are killed with the port — a release that fired
    /// into a closed demo would take the whole test host down with it.</summary>
    private void StopTimers()
    {
        lock (_lock)
        {
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
    }

    private void WhileOpen(Action action)
    {
        if (!_inner.IsOpen) return;
        try { action(); }
        catch (InvalidOperationException) { /* the port closed under the timer */ }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var text = Encoding.ASCII.GetString(data.Span).Trim();
            if (_swallowArmed && text == _swallowAfterRelease)
            {
                _swallowArmed = false;
                Swallowed = true;
                return Task.CompletedTask;      // the radio simply never answers
            }
            if (_command is { } wanted && text == wanted)
            {
                _command = null;
                HeldAtTicks = Environment.TickCount64;
                // The entry lifecycle's own lines go up FIRST and carry NO
                // prompt — which is what leaves the transport's write gate shut
                // until it times out, exactly as the field capture shows.
                if (_lifecycleLines.Length > 0)
                    _timers.Add(new Timer(
                        _ => WhileOpen(() => Inject(_lifecycleLines)), null, 20, Timeout.Infinite));
                if (_lifecycleMs is not { } after) return Task.CompletedTask;   // never arrives
                var held = data.ToArray();
                _timers.Add(new Timer(
                    _ => WhileOpen(() =>
                    {
                        lock (_lock) { Released = true; _swallowArmed = _swallowAfterRelease is not null; }
                        _inner.WriteAsync(held).GetAwaiter().GetResult();
                    }),
                    null, after, Timeout.Infinite));
                return Task.CompletedTask;
            }
        }
        return _inner.WriteAsync(data, cancellationToken);
    }
}

/// <summary>
/// The clone CAMPAIGNS (plan round 11 §9A) over the REAL stack — the stateful
/// demo radio under the production SerialTransport, Prc138Radio, RadioSession
/// and surfaces. No fakes above the byte seam except the confirmation prompt,
/// which is the seam the §5 lifecycle contract exists to make controllable.
///
/// <para>The centrepiece is the ROUND TRIP WITH PERTURBATION: read the demo to
/// a file, move EVERY stateful domain out from under it, write the file back,
/// and require the verify to come back clean. Without the perturbation the
/// gate would pass on a campaign that did nothing at all.</para>
/// </summary>
public sealed class CloneServiceTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly FakeConfirmationPrompt _prompt = new();
    private readonly CloneService _clone;

    /// <summary>The SAME surface the campaign holds — kept so a test can reach
    /// its <see cref="AleProgrammingGate"/> (each <c>AleSurface</c> owns one, so
    /// a second instance would be a second gate and its test hook would move
    /// nothing).</summary>
    private readonly AleSurface _ale;

    public CloneServiceTests()
    {
        _port = new RecordingDemoPort(_demo);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _ale = new AleSurface(_radio);
        _clone = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            _ale, new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
        };
    }

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    // ---- The read campaign ---------------------------------------------------

    [Fact]
    public async Task TheReadCampaign_FillsEveryManifestDomain()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        Assert.Empty(file.IncompleteDomains);

        // Anti-vacuity, domain by domain: a campaign that marked everything
        // read and carried nothing would pass the marker check alone.
        Assert.Equal(["ZZZ", "TST", "CAM"], file.Selfs.Select(s => s.Name));
        Assert.Equal(5, file.Individuals.Count);
        Assert.Equal(["AAA", "TST"], Assert.Single(file.Nets, n => n.Name == "NT1").Members);
        Assert.Equal(10, file.ChannelGroups.Count);
        Assert.Equal(2, file.Schedules.Count);
        // D4 (round 17) — THE ELIDED CHANNEL DOMAIN. The radio still answers a
        // DEFAULT ROW for every slot nobody programmed, and S4 still judges the
        // whole 100-slot set (below); what the FILE carries is only the rows
        // that differ from Wire.DefaultChannel, with the marker that says so.
        Assert.True(file.DefaultChannelsElided);
        Assert.Equal([1, 2], file.Channels.Select(c => c.Number));
        Assert.DoesNotContain(file.Channels, c => c.IsFactoryDefault());
        // ANTI-VACUITY for the judgment half: the domain is READ, not faulted —
        // the leg saw all 100 slots and only then dropped the 98 default ones.
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        Assert.Equal(10, file.HopNets.Count);
        Assert.Equal(2, file.ExcludeBands.Count);
        // F9: BOTH prompt-scoped bands — 0-6 read at `SSB>` and 7-9 read at
        // `HOP>`, where they are the only presets that exist (P5). The
        // campaign carried seven until this round and could not see the other
        // three at all.
        Assert.Equal([.. Enumerable.Range(0, 10)], file.ModemPresets.Select(p => p.Number));
        // The HOP rows are the SHORT line: no TYPE and no INTER column…
        var hop9 = Assert.Single(file.ModemPresets, p => p.Number == 9);
        Assert.Equal("DAT9 ASYNC REMOTE BAUD 300", hop9.Fields);
        Assert.DoesNotContain("TYPE", hop9.Fields, StringComparison.Ordinal);
        Assert.DoesNotContain("INTER", hop9.Fields, StringComparison.Ordinal);
        // …and their ENABLED flags come from the HOP listing, not the SSB one:
        // the demo's found state is 9 enabled, 7 and 8 not (the bench radio's).
        Assert.True(hop9.Enabled);
        Assert.False(Assert.Single(file.ModemPresets, p => p.Number == 7).Enabled);
        Assert.False(Assert.Single(file.ModemPresets, p => p.Number == 8).Enabled);
        // …while the SSB band's own flags SURVIVED the second listing (the
        // presence store is single and the HOP read replaces it — each leg
        // folds in its own band with the set that was read for it).
        Assert.True(Assert.Single(file.ModemPresets, p => p.Number == 1).Enabled);
        Assert.False(Assert.Single(file.ModemPresets, p => p.Number == 2).Enabled);
        Assert.Equal([0, 4], file.Messages.Select(m => m.Slot));
        Assert.Equal("Ssb", file.OperatingMode);
        Assert.Equal(0, file.OperatingChannel);
        Assert.Equal(0, file.OperatingHopNet);
        // Every INCLUDED manifest setting was confirmed and carried — an
        // omission would have put a line in the summary.
        Assert.Equal(CloneSettingsManifest.Rows.Count, file.Settings.Count);
        // …so a clean read's WHOLE report is: the closing restore's NOTICE
        // (plan-clone-field-round2.md F1 — the campaign says where it left the
        // radio), and then D15's STORED INVENTORY — one line per domain that
        // stored something, LAST, in the owner's order. Anything else here is
        // still a defect.
        //
        // D15 (2026-08-30, owner) REPLACED D4's elision notice with these
        // twelve rows: "instead of that message, give a line by line of what
        // WAS stored". `2 channel(s)` is the same fact the elision line used to
        // report from the other side — 100 slots reported, 98 at the factory
        // row, 2 carried — and the reported-set claim is pinned by
        // `file.ChannelState == Read` above, which S4 only grants for all 100.
        Assert.Equal(
            [
                "Left the radio on channel 00, net 0, SSB.",
                "2 channel(s)",
                "10 channel group(s)",
                "3 self(s)",
                "5 individual(s)",
                "3 net(s)",
                "2 message(s)",
                "2 schedule(s)",
                "10 HOP net(s)",
                "2 exclusion band(s)",
                "10 modem preset(s)",
                "30 setting(s)",
                "22 lockout(s)",
            ],
            _clone.Summary);
        // THE ABSENCE PIN: the retired sentence is byte-dead, in whole and in
        // the fragment a re-add would have to carry.
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("factory default", StringComparison.Ordinal));

        // The LOCKOUT domain (owner ruling R2): the closed 22-item inventory,
        // keyed, with the demo's MIXED baseline carried faithfully — a read
        // that answered one value everywhere would pass a count check alone.
        Assert.Equal(CloneDomainState.Read, file.Lockouts!.State);
        Assert.Equal(22, file.Lockouts.Rows.Count);
        Assert.Equal(3, file.Lockouts.Rows.Count(r => r.State == "Lock"));
        Assert.Single(file.Lockouts.Rows,
            r => r is { Family: "Program", Section: "Ssb", Item: "CHAN", State: "Lock" });
        Assert.Single(file.Lockouts.Rows,
            r => r is { Family: "Select", Section: "Eam", Item: "KEY", State: "Lock" });

        // The disabled preset is carried WITH its state — the presence read is
        // the only thing that can say so.
        Assert.False(Assert.Single(file.ModemPresets, p => p.Number == 2).Enabled);
        Assert.True(Assert.Single(file.ModemPresets, p => p.Number == 1).Enabled);
    }

    [Fact]
    public async Task TheReadCampaign_CapturesTheOperatingSnapshotBeforeItsOwnModeSwitching()
    {
        // The campaign visits ALE and HOP. What the file records must be where
        // the OPERATOR was, not where the campaign went.
        ConnectReady();
        Assert.True(await _clone.ReadAsync());

        Assert.Equal("Ssb", _clone.File!.OperatingMode);
        // …and it puts the radio back, rather than leaving it in HOP.
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);
    }

    /// <summary>
    /// P6 AUDIT ROUND 1, BLOCKER-1 — the exact scenario, pinned.
    ///
    /// <para>A domain whose OWN sentinel-scoped operation faults must come back
    /// FAULTED, never <c>Read</c>. The trap is that Core's timeout on that
    /// operation is precisely what DISPATCHES the next queued ping: a campaign
    /// that judged its own trailing <c>BAT ST</c> would see that one answered
    /// perfectly happily, mark the domain <c>Read</c> while Core had preserved
    /// the STALE prior mirror, pass the write preflight, and later ERASE a
    /// radio in order to replay yesterday's fill onto it.</para>
    ///
    /// <para>Reproduced under the ROUND-15 A0 clock (audit round 1): the demo
    /// answers with a 40 ms latency, the ALE reads are given a 200 ms
    /// sentinel, and each read's own closing <c>BAT ST</c> is HELD BACK by the
    /// port wrapper — so every one of those reads times out and
    /// then gets its answer LATE, which is the case that produces the
    /// documented stream shift. The campaign's own sentinels and the modem
    /// queue's separate timeout keep answering perfectly happily throughout.
    /// That is the trap in its natural habitat.</para>
    ///
    /// <para>The delay is a WIRE delay in the port wrapper — bytes held, then
    /// delivered. The demo radio is not taught anything (owner directive,
    /// 2026-08-22).</para>
    /// </summary>
    [Fact]
    public async Task ADomainWhoseOwnSentinelFaults_IsMarkedFaulted_NotRead()
    {
        ConnectReady();
        _demo.ResponseDelayMs = 40;             // …a radio that answers, but not instantly
        _radio.Ale.RefreshTimeoutMs = 200;      // …an ALE read that gives up before it does…
        // …because its closing sentinel is held back FAR longer than the read
        // waits: every leg then faults on its OWN timer rather than on the
        // previous leg's late answer arriving first (the margin is ~1.3 s,
        // measured against the widest leg — the ten CHG reads).
        _port.DelayAleReadSentinelMs = 2_000;
        _transport.GateTimeoutMs = 300;         // no prompt comes back while it is held

        Assert.False(await _clone.ReadAsync());
        var file = _clone.File!;

        Assert.True(_port.DelayedSentinels >= 3,
            $"only {_port.DelayedSentinels} ALE read sentinels were held back — "
            + "the fixture did not reproduce the fault it is named for");
        // ANTI-VACUITY for the LATE half: the held answers really did arrive,
        // so the stream really was shifted while the campaign ran on.
        Assert.True(_radio.State.BatteryStatus.IsConfirmed);

        // The four ALE domains own a completion idiom, and all four must show
        // the fault rather than a stale-but-plausible mirror — even though a
        // late answer credited somebody's sentinel along the way.
        Assert.Equal(CloneDomainState.Faulted, file.BookState);
        Assert.Equal(CloneDomainState.Faulted, file.GroupState);
        Assert.Equal(CloneDomainState.Faulted, file.ScheduleState);
        Assert.Empty(file.Selfs);
        Assert.Empty(file.ChannelGroups);

        // ANTI-VACUITY, and the whole point: a domain WITHOUT its own idiom —
        // the channel dump, bounded by a trailing BAT ST — still reads fine
        // over the same connection. So this is not "everything faulted", it is
        // "the faulted operations were judged by their own verdict". (D4: the
        // domain is READ over all 100 reported slots; the FILE carries the two
        // that are not at the factory default.)
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        Assert.Equal([1, 2], file.Channels.Select(c => c.Number));

        // …and the file that results can NEVER be written.
        _clone.Adopt(file);
        Assert.Contains("address book", _clone.WriteBlockedReason!, StringComparison.Ordinal);
        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    /// <summary>
    /// THE OTHER HALF OF THE SAME CLASS (audit round 1): a sentinel the radio
    /// NEVER HEARS AT ALL (R6 — the radio silently swallows commands). The
    /// test above pins the LATE answer, whose credit shifts the stream; this
    /// one pins the answer that never comes, where nothing is owed to anybody
    /// and the domain must still be judged by its own verdict. Same
    /// conclusion, different wire fault — and neither may publish an empty
    /// domain as <c>Read</c>.
    /// </summary>
    [Fact]
    public async Task ADomainWhoseSentinelIsNeverHeard_IsMarkedFaulted_NotRead()
    {
        ConnectReady();
        _demo.ResponseDelayMs = 40;
        _radio.Ale.RefreshTimeoutMs = 200;
        _port.SwallowAleReadSentinels = true;
        // A swallowed command leaves no prompt behind, so the transport's
        // write gate falls back to its timeout for the next command; compress
        // it so three swallowed sentinels do not cost six seconds.
        _transport.GateTimeoutMs = 300;

        Assert.False(await _clone.ReadAsync());
        var file = _clone.File!;

        Assert.True(_port.SwallowedSentinels >= 3,
            $"only {_port.SwallowedSentinels} ALE read sentinels were swallowed — "
            + "the fixture did not reproduce the fault it is named for");

        Assert.Equal(CloneDomainState.Faulted, file.BookState);
        Assert.Equal(CloneDomainState.Faulted, file.GroupState);
        Assert.Equal(CloneDomainState.Faulted, file.ScheduleState);
        Assert.Empty(file.Selfs);
        Assert.Empty(file.ChannelGroups);
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        Assert.Equal([1, 2], file.Channels.Select(c => c.Number));   // D4: the non-default slots
    }

    // ---- Write preflight -----------------------------------------------------

    [Fact]
    public async Task WritingAFileWithAnUnreadDomain_IsRefused_NamingIt_AndAsksNothing()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();
        file.HopNetState = CloneDomainState.Faulted;
        file.MessageState = CloneDomainState.Unread;
        _clone.Adopt(file);

        Assert.Contains("HOP nets", _clone.WriteBlockedReason!, StringComparison.Ordinal);
        Assert.Contains("stored messages", _clone.WriteBlockedReason!, StringComparison.Ordinal);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows(CloneSwapTests.Replace("CAM", "NEWSELF"))));
        // A transient read fault must NEVER become destructive loss: nothing
        // was asked and nothing was sent.
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    [Fact]
    public async Task AFileWithNoSelfAfterTheTransform_IsRejectedAtPreflight()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();
        file.Selfs.Clear();                  // a post-ERASE read legitimately has none
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(CloneService.NoSelfRejection, _clone.StatusText);
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);

        // …and CHOOSING an identity is what fixes it — the rejection is an
        // instruction, not a dead end.
        Assert.Null(_clone.WriteBlockedReason);
    }

    /// <summary>
    /// P6 AUDIT ROUND 1, BLOCKER-2 LAYER 1 — the friendly early refusal. The
    /// auditor's probe: a typed name that already belongs to a NET. Refused
    /// before the confirmation and before one byte is sent, naming the
    /// offender.
    /// </summary>
    [Fact]
    public async Task AnIdentityThatCollidesWithAnExistingNet_IsRefusedBeforeAnythingIsSent()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();      // holds the net NET2
        // A self that is NOT the scan-gate one, so the collision is what the
        // refusal is about rather than D2's length rule.
        file.Selfs.Add(new CloneAddress { Name = "BASECAMP", Group = 1 });
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows(CloneSwapTests.Replace("BASECAMP", " net2 "))));

        Assert.Contains("NET2 is already a net", _clone.StatusText, StringComparison.Ordinal);
        Assert.Contains("unique across selfs, individuals and nets",
            _clone.StatusText, StringComparison.Ordinal);
        // The whole point: no question asked, nothing on the wire, and above
        // all no ERASE.
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    /// <summary>
    /// P6 AUDIT ROUND 1, BLOCKER-2 LAYER 2 — defence in depth. The campaign
    /// writes the TRANSFORMED file, which no LOAD ever validated, so the
    /// preflight re-runs the SAME validation on it. This pin deliberately
    /// reaches the service by a route the layer-1 check does not cover — a
    /// file adopted in memory, already invalid — so it would still catch a
    /// FUTURE transform that produced a bad graph some other way.
    /// </summary>
    [Fact]
    public async Task AnInvalidTransformedGraph_IsRefusedByRevalidation_BeforeTheConfirm()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();
        // An invalid graph that never went through Load: the same name as both
        // an individual and a net. (Adopt is the in-memory route; the read
        // campaign is the other one, and a future transform would be a third.)
        file.Nets.Add(new CloneNet { Name = "BOB", Group = 3, AssociatedSelf = "CAM" });
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Contains("The clone cannot be written", _clone.StatusText, StringComparison.Ordinal);
        Assert.Contains("BOB", _clone.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    /// <summary>
    /// P6 AUDIT ROUND 2, BLOCKER — the auditor's GHOST fixture on the WRITE
    /// path, reaching the service by a route that bypasses <c>Load</c>
    /// entirely (an in-memory file, adopted). This is the backstop layer: the
    /// preflight revalidates the TRANSFORMED graph through the same rules, so
    /// a dangling association cannot reach the wire no matter how the graph
    /// was produced.
    ///
    /// <para>Before the fix this file loaded, transformed and wrote — and the
    /// radio's refusal arrived at <c>NETAD</c>, after the <c>ERASE</c>.</para>
    /// </summary>
    [Fact]
    public async Task ADanglingAssociationInAnAdoptedGraph_IsRefusedPreConfirm_NothingErased()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();
        file.Nets[0].AssociatedSelf = "GHOST";      // never went through Load
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Contains("The clone cannot be written", _clone.StatusText, StringComparison.Ordinal);
        Assert.Contains("GHOST", _clone.StatusText, StringComparison.Ordinal);
        // The whole point of the finding: no question, no wire, and above all
        // no ERASE.
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
        Assert.DoesNotContain("ERASE", _port.Sent);
    }

    [Fact]
    public async Task AScheduleTimeCoreWouldThrowOn_IsRefusedPreConfirm_RatherThanMidCampaign()
    {
        // The same family: EXCH/SOU STA runs after the erase and Core's
        // validator throws, so a bad time has to be caught here.
        ConnectReady();
        var file = CloneFileTests.Complete();
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "CAM", Interval = "9:99", Start = "13:02" });
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Contains("The clone cannot be written", _clone.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    /// <summary>
    /// P6 AUDIT ROUND 3, the user-facing fix's OTHER path
    /// (plan/plan-clone-file-validation.md §4): the same missing time on an
    /// ADOPTED graph, which never went through <c>Load</c>. The
    /// NullReferenceException it used to raise is not a
    /// <c>CloneFileFormatException</c>, so it ESCAPED the preflight's catch and
    /// propagated out of the campaign. It must refuse cleanly instead —
    /// no prompt, no wire, no erase.
    /// </summary>
    [Fact]
    public async Task AScheduleTimeThatIsMissingEntirely_IsRefusedCleanly_NotPropagated()
    {
        ConnectReady();
        var file = CloneFileTests.Complete();
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "CAM", Interval = null!, Start = "13:02" });
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();

        // The refusal is a RETURN, not a throw: an exception here would reach
        // the ViewModel's command and take the app with it.
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Contains("The clone cannot be written", _clone.StatusText, StringComparison.Ordinal);
        Assert.Contains("CAM", _clone.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    [Fact]
    public async Task AnIdentityThatIsNotAnAleName_IsRefusedBeforeAnythingIsSent()
    {
        ConnectReady();
        _clone.Adopt(CloneFileTests.Complete());

        _prompt.EnqueueAnswer(false);
        // A queued CANCEL that must never be consumed: if a preflight ever
        // stops refusing, this pin FAILS on CallCount instead of hanging on a
        // prompt nobody answers — a pin that hangs hides the very defect it
        // exists to show.
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows(CloneSwapTests.Replace("CAM", "BAD NAME!"))));
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    // ---- The identity TABLE's disposition (R-A, §3.2) -------------------------

    /// <summary>
    /// I-6: the confirmation names what the table is about to DO to the book —
    /// every role change and every drop — before the wipe sentence. The 2026-08-21
    /// clone demoted <c>HOS</c> and said nothing anywhere; now the operator is
    /// told before the question, not after the erase.
    /// </summary>
    [Fact]
    public async Task TheConfirm_OpensWithTheWipeSentence_ThenListsTheRoleChangesAndTheDrops()
    {
        ConnectReady();
        var file = CloneSwapTests.Roster();
        // A net whose associated self was deleted on the source radio — the
        // primary-deletion artifact, which cannot be programmed and is dropped.
        Assert.Single(file.Nets, n => n.Name == "NETB").AssociatedSelf = null;
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(false);
        // ROUND 15 C-1: the swap counterpart must be one of the ROW'S OWN
        // individuals, so this is KC1HAS (ALPHA's) rather than KG6KMJ (BASE's,
        // which the card no longer offers on this row).
        var rows = CloneSwapTests.Rows(CloneSwapTests.Swap("ALPHA", "KC1HAS"));
        Assert.False(await _clone.WriteAsync(rows));

        Assert.Equal(1, _prompt.CallCount);
        var message = _prompt.Last.Message;
        // ROUND 15 E-4 ORDER (manager ruling, audit round 1): the WIPE SENTENCE
        // FIRST — byte-identical to the one the doc quotes — so the question
        // opens by saying what the radio does…
        Assert.StartsWith(
            CloneService.ConfirmMessage + Environment.NewLine + Environment.NewLine,
            message, StringComparison.Ordinal);
        Assert.StartsWith("The radio ", message, StringComparison.Ordinal);
        // …then the role changes, in row order…
        Assert.Contains(
            "KC1HAS is now a self in ALPHA's place." + Environment.NewLine
            + "ALPHA is now an individual of KC1HAS." + Environment.NewLine,
            message, StringComparison.Ordinal);
        // …then the drops, LAST.
        Assert.Contains("Net NETB", message, StringComparison.Ordinal);
        Assert.Contains("no associated self", message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf("KC1HAS is now a self", StringComparison.Ordinal)
                < message.IndexOf("Net NETB", StringComparison.Ordinal),
            "the role changes must precede the drops:" + Environment.NewLine + message);

        // ANTI-VACUITY: over the SAME book without the dropped net, a table that
        // changes nothing asks the bare question.
        _clone.Adopt(CloneSwapTests.Roster());
        _prompt.EnqueueAnswer(false);
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(CloneService.ConfirmMessage, _prompt.Last.Message);
    }

    /// <summary>
    /// I-6's other half: role changes are NOTICES — they must be reported and
    /// must not make an otherwise-perfect clone read as failed — while drops
    /// stay PROBLEMS.
    /// </summary>
    [Fact]
    public async Task TheSummary_CarriesRoleChangesAsNotices_AndTheWriteStaysClean()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The demo's selfs are all 1-3 characters, so the scan-gate rule (D2)
        // makes this a REPLACE with a 1-3 character name.
        _prompt.EnqueueAnswer(true);
        Assert.True(
            await _clone.WriteAsync(CloneSwapTests.Rows(CloneSwapTests.Replace("CAM", "NW1"))),
            string.Join(" | ", _clone.Summary));

        // ROUND 15 C-2 (owner rule 3): CAM is the SCAN-GATE self, so its
        // replacement reports ONE role change and creates NO individual — "for
        // the 3-letter self there should NOT be an individual created that is
        // associated with it".
        Assert.Contains("NW1 replaces CAM as the scan-gate self.", _clone.Summary);
        Assert.DoesNotContain(_clone.Summary, l =>
            l.Contains("CAM is now an individual", StringComparison.Ordinal));
        // CLEAN, with the role changes counted as notes rather than faults —
        // D9 category B: the status carries the verdict, the notes are the
        // summary lines pinned above.
        Assert.Equal("Write complete with warnings.", _clone.StatusText);
        // (D23: the role changes above are now the ONLY notices on a clean
        // write — the restore no longer contributes one.)
        // ANTI-VACUITY: the transform really reached the wire — the new name
        // was programmed as a SELF — and the verify against the transformed
        // file came back clean.
        Assert.Contains(_port.Sent, l => l.StartsWith("SLFAD NW1", StringComparison.Ordinal));
        // …and the old scan-gate name was NOT programmed as an individual.
        Assert.DoesNotContain(_port.Sent, l => l.StartsWith("INDAD CAM ", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE AUDIT ROUND-1 BLOCKER (A-13), end to end and on the WIRE. A net that
    /// listed both the self being swapped out and the individual being swapped
    /// in used to collapse to one name twice — and leg 7 then sent the member
    /// write TWICE for the same net, which the radio refuses as a duplicate,
    /// AFTER the wipe, on a half-written radio. The names EXCHANGE SLOTS now, so
    /// the list keeps its count, order and spelling and the wire carries each
    /// member exactly once.
    /// </summary>
    [Fact]
    public async Task ASwapOnANetListingBothNames_SendsEachMemberOnce_A13()
    {
        ConnectReady();
        var file = CloneSwapTests.Roster();
        var neta = Assert.Single(file.Nets, n => n.Name == "NETA");
        neta.Members.Clear();
        neta.Members.AddRange(["KC1HAS", "ALPHA"]);
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        // The campaign's own verdict is not the subject here (an adopted book
        // with no channels cannot verify against a demo radio that has 100);
        // what is pinned is the BYTES leg 7 put on the wire.
        await _clone.WriteAsync(CloneSwapTests.Rows(CloneSwapTests.Swap("ALPHA", "KC1HAS")));

        var members = _port.Sent
            .Where(l => l.StartsWith("ADDM NETA ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["ADDM NETA ALPHA", "ADDM NETA KC1HAS"], members);
        // …and the radio never answered the refusal the duplicate used to earn.
        Assert.DoesNotContain(_clone.Summary, l =>
            l.Contains("Already a member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSummary_CarriesDropsAsProblems_AndTheWriteIsUnclean()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The primary-deletion artifact: a net whose associated self is blank
        // cannot be programmed at all, so the transform drops it — loudly.
        var file = _clone.File!;
        file.Nets[0].AssociatedSelf = null;
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(true);
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Contains(_clone.Summary, l => l.Contains("no associated self", StringComparison.Ordinal));
        Assert.Equal("Write incomplete.", _clone.StatusText);
    }

    // ---- The ONE confirmation (§9A leg 1) ------------------------------------

    [Fact]
    public async Task TheWriteConfirm_IsAskedOnce_WithTheExactStrings()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);

        await _clone.WriteAsync(CloneSwapTests.Rows());

        // ONE prompt for the WHOLE campaign — the embedded WIPE is covered by
        // it (GUI-owned confirmation), so there is no second popup.
        Assert.Equal(1, _prompt.CallCount);
        Assert.Equal("Write clone to radio?", _prompt.Last.Title);
        Assert.Equal(
            "The radio will be zeroized, and this cannot be undone.",
            _prompt.Last.Message);
        Assert.Equal("Write", _prompt.Last.AcceptText);
        Assert.Equal("Cancel", _prompt.Last.CancelText);
        // …and the campaign really did run the WIPE the prompt describes — the
        // question would otherwise be describing a campaign nobody runs.
        Assert.Contains("ZERO", _port.Sent);
        // The lockouts the message names are really written, too.
        Assert.Contains(_port.Sent, l => l.StartsWith("PROGRAM ", StringComparison.Ordinal));
    }

    /// <summary>
    /// LEG 2, LITERAL (owner ruling R1 + invariant 3; the literal reading ruled
    /// 2026-08-19) — the campaign's FIRST WIRE ACT after the ONE confirmation
    /// is the wipe. Not "the first act that programs anything", not "the first
    /// act after navigating": <b>the first byte</b>.
    ///
    /// <para>An earlier version of this pin allowed a navigation prefix
    /// (<c>SS</c>/<c>SH</c>/<c>BAT ST</c>/…) because the campaign switched to
    /// <c>SSB&gt;</c> before sending <c>ZERO</c>. That allowance is DEAD: the
    /// bench settled the radio side
    /// (<c>bench/transcripts/r12-zero-prompts-20260819-061052.jsonl</c>) —
    /// <c>ZERO</c> is ACCEPTED at <c>ALE&gt;</c> and <c>HOP&gt;</c> as well as
    /// <c>SSB&gt;</c>, so the invariant needed no amendment and the campaign
    /// needed no prefix. The literal form is RADIO-VERIFIED, not asserted.</para>
    ///
    /// <para>Pinned from ALL THREE starting prompts, because the whole reason
    /// the prefix existed was the prompt the operator happened to leave the
    /// radio at.</para>
    ///
    /// <para><b>PLAN-FACING RECORD (plan-clone-round12.md invariant 3):</b> the
    /// invariant needed NO amendment. The literal reading it always carried —
    /// "the write campaign's first wire act after the ONE confirm is `ZERO`" —
    /// is RADIO-VERIFIED as of 2026-08-19 and is what the code now does. The
    /// navigation-only-prefix amendment that was drafted against it is
    /// unnecessary and was not taken.</para>
    /// </summary>
    [Theory]
    [InlineData(OperatingMode.Ssb)]
    [InlineData(OperatingMode.Ale)]
    [InlineData(OperatingMode.Hop)]
    public async Task TheWriteCampaign_SendsZEROAsItsVeryFirstCommand_FromAnyPrompt(OperatingMode from)
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());

        // Leave the radio where an operator might have: the read campaign puts
        // it back in SSB, so anything else has to be asked for.
        await AtModeAsync(from);

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        // THE PIN: index ZERO. Nothing precedes it — not a mode switch, not a
        // status read, not a sentinel.
        Assert.Equal("ZERO", _port.Sent[0]);
    }

    /// <summary>
    /// …and the OTHER half of what the capture bought: from every starting
    /// prompt the settle ends with the radio at <c>SSB&gt;</c>, which is where
    /// leg 3 needs it. The campaign performs NO navigation of its own between
    /// the wipe and the first channel write — that is the radio's behaviour,
    /// and this is what would notice if it stopped being true.
    /// </summary>
    [Theory]
    [InlineData(OperatingMode.Ale)]
    [InlineData(OperatingMode.Hop)]
    public async Task AfterAWipeFromAnyPrompt_TheRadioIsAtSSB_WithNoNavigationFromTheCampaign(
        OperatingMode from)
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        await AtModeAsync(from);

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = WritePortion(_port.Sent);

        int zero = IndexOf(sent, l => l == "ZERO");
        int firstWrite = IndexOf(sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));
        Assert.True(zero == 0 && firstWrite > zero);

        // NO MODE SWITCH between the wipe and the first channel write. That is
        // the precise claim — the radio put itself at `SSB>` — and it is
        // deliberately not an allow-list of everything else: Core's own
        // compensations (a `COM` re-read at the returning SSB prompt) are its
        // business, and a pin that forbade them would be pinning the wrong
        // thing and would break on the next legitimate one.
        foreach (var line in sent.Skip(zero + 1).Take(firstWrite - zero - 1))
            Assert.False(line is "SS" or "ALE" or "HO",
                $"the campaign navigated ('{line}') between the wipe and the first write");

        // …and the fresh mode query really did confirm SSB, so leg 3's own
        // AtPrompt call has nothing left to do.
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);
    }

    /// <summary>Put the radio at a mode the way the operator would, and wait
    /// for the radio to confirm it — the campaign under test must find a real
    /// starting prompt, not a hopeful one.</summary>
    private async Task AtModeAsync(OperatingMode mode)
    {
        var modes = new ModeSurface(_radio);
        if (modes.Mode.IsConfirmed && modes.Mode.Value == mode) return;

        modes.Select(mode);
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(modes.Mode.IsConfirmed && modes.Mode.Value == mode))
            await Task.Delay(5);
        Assert.Equal(mode, modes.Mode.Value);
    }

    /// <summary>
    /// The SETTLE GATE, both halves. GREEN: the campaign waits out the silence
    /// and continues at the prompt. RED: nothing comes back, the campaign
    /// FAULTS LOUDLY and — the part that matters — says the radio has been
    /// wiped and not rewritten.
    /// </summary>
    [Fact]
    public async Task TheZeroizeSettle_IsAwaited_NotSlept()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        // A visible silence, the shape the bench measured (9.4 s over eight
        // bare-CR polls); the campaign must ride it out rather than send into
        // a radio still wiping RAM.
        _demo.ZeroizeSettleDelayMs = 250;
        _radio.ZeroizeSettlePollMs = 20;
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent;
        int zero = IndexOf(sent, l => l == "ZERO");
        int firstWrite = IndexOf(sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));
        Assert.True(firstWrite > zero, "the first channel write did not follow the wipe");
        // Core polled the prompt itself, with bare CRs, over its internal send
        // path — the campaign never sleeps and never pings.
        Assert.Contains("", sent.Skip(zero).Take(firstWrite - zero));
    }

    /// <summary>
    /// LEG 2's closing half — the FRESH MODE QUERY (plan §3: "a fresh mode
    /// query before any AtPromptAsync trust", the stale-confirmed-mode trap).
    ///
    /// <para>The settle boundary RESETS every mirror, deliberately, so nothing
    /// downstream reads a value from before the wipe. That leaves the confirmed
    /// operating mode gone — and `AtPromptAsync` consults exactly that. Without
    /// this query the campaign would either believe an unconfirmed mirror or
    /// re-send a mode switch it could not verify; with it, the first thing
    /// after the wipe is an `SH` and its sentinel, before ANY leg asks to be at
    /// a prompt.</para>
    ///
    /// <para>Its own pin because the leg-order landmarks pass without it (P2
    /// audit round 1 — only the AnalogSquelch RED case failed, and indirectly).</para>
    /// </summary>
    [Fact]
    public async Task TheSettleIsFollowedByAFreshModeQuery_BeforeAnyLegTrustsThePrompt()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = WritePortion(_port.Sent);

        int zero = IndexOf(sent, l => l == "ZERO");
        int firstWrite = IndexOf(sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));
        Assert.True(zero >= 0 && firstWrite > zero);

        // Between the wipe and the first programming: an SH, and its sentinel.
        var between = sent.Skip(zero + 1).Take(firstWrite - zero - 1).ToList();
        Assert.Contains("SH", between);
        Assert.True(between.IndexOf("SH") < between.LastIndexOf("BAT ST"),
            "the fresh mode query was not bracketed by a sentinel");

        // …and the mirror really is re-confirmed by it, so the legs below are
        // trusting the radio's own answer rather than a pre-wipe memory.
        Assert.True(_radio.State.OperatingMode.IsConfirmed);
    }

    [Fact]
    public async Task AZeroizeThatNeverSettles_FaultsTheCampaign_AndSaysTheRadioIsWiped()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        // The radio takes the command and never comes back.
        _demo.ZeroizeSettleDelayMs = 60_000;
        _radio.ZeroizeSettleTimeoutMs = 150;
        _radio.ZeroizeSettlePollMs = 20;
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Equal(CloneState.Failed, _clone.State);
        Assert.Contains("Stopped at the zeroize step", _clone.StatusText, StringComparison.Ordinal);
        var fault = Assert.Single(_clone.Summary, l => l.Contains("wiped and NOT rewritten", StringComparison.Ordinal));
        Assert.Contains("Zeroize", fault, StringComparison.Ordinal);
        // …and nothing was programmed into a radio nobody can talk to.
        Assert.DoesNotContain(_port.Sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE ABSENCE PINS (plan §3, invariant 3). Every reconcile/clear/delete leg
    /// the round-11 campaign carried existed to converge onto an UNKNOWN target.
    /// Leg 2 makes the target guaranteed blank — the owner statement: "it's safe
    /// to assume that zeroize clears everything except for the remote port baud
    /// rate" — so each one is DELETED, and its absence is asserted rather than
    /// left to be re-added by someone who does not know why it went.
    /// </summary>
    [Fact]
    public async Task EveryDeletedReconcileLeg_IsReallyAbsentFromTheWire()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = WritePortion(_port.Sent);

        // The ALE fill ERASE — subsumed by the wipe.
        Assert.DoesNotContain("ERASE", sent);
        // The channel-group reconcile's removals — every group is already empty.
        Assert.DoesNotContain(sent, l => l.StartsWith("DELC", StringComparison.Ordinal));
        // The HOP clear-first wipe — every net is already wiped.
        Assert.DoesNotContain(sent, l => l.StartsWith("HOPSET ", StringComparison.Ordinal)
            && l.EndsWith(" DEL", StringComparison.Ordinal));
        // The exclusion-band reconcile — no per-band delete, AND no read-back
        // to reconcile against: the table is already empty.
        Assert.DoesNotContain(sent, l => l.StartsWith("EXC ", StringComparison.Ordinal)
            && l.EndsWith(" DEL", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, l => l == "EXC");
        // The per-slot message DELETE — every slot is already empty.
        Assert.DoesNotContain(sent, l => l.StartsWith("TXMSG DEL", StringComparison.Ordinal));

        // ANTI-VACUITY: the campaign really did write those domains, so the
        // absences above are "the reconcile went", not "the leg went".
        Assert.Contains(sent, l => l.StartsWith("ADDC ", StringComparison.Ordinal));
        Assert.Contains(sent, l => l.StartsWith("HOPTYPE ", StringComparison.Ordinal));
        Assert.Contains(sent, l => l.StartsWith("EXC 1 ", StringComparison.Ordinal));
        Assert.Contains(sent, l => l.StartsWith("TXMSG 0 ", StringComparison.Ordinal));
        Assert.Contains(sent, l => l.StartsWith("SLFAD ", StringComparison.Ordinal));
        // …and the VERIFY read really does still read the exclusion table back,
        // so "no EXC in the write" is a statement about the reconcile and not
        // about the domain going unchecked.
        Assert.Contains("EXC", _port.Sent);
    }

    /// <summary>
    /// LEG 5 — the stored-message leg MOVED to the `ALE&gt;` prompt. The TXMSG
    /// family is ALE-only and answers `** ERROR **` at SSB&gt; and HOP&gt;
    /// (captured 2026-08-18), so the round-11 leg would have been refused line
    /// by line on the real radio.
    /// </summary>
    [Fact]
    public async Task TheStoredMessageLeg_RunsAtTheAlePrompt()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = _port.Sent;

        int store = IndexOf(sent, l => l.StartsWith("TXMSG 0 ", StringComparison.Ordinal));
        Assert.True(store > 0, "the message leg never ran");
        // The last mode switch BEFORE the store put the radio at ALE.
        var switches = sent.Take(store).Where(l => l is "SS" or "ALE" or "HO").ToList();
        Assert.Equal("ALE", switches[^1]);
    }

    /// <summary>
    /// LEG 10 — the operator lockouts, written per SECTION at that section's own
    /// prompt. A set names no section on the wire: it scopes to the ACTIVE
    /// PROMPT's mode section (captured, all six discrimination cells), so this
    /// leg's whole correctness is where it stands when it sends.
    /// </summary>
    [Fact]
    public async Task TheLockoutLeg_WritesEachSectionAtItsOwnPrompt_AfterAllProgramming()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = WritePortion(_port.Sent);

        // All 22 rows are written — every one, not just the ones that differ:
        // no leg may assume anything about target state (invariant 3), and a
        // set has no accept/reject semantics to read an answer from.
        Assert.Equal(22, sent.Count(IsLockoutSet));

        // Each row landed at the prompt for its section.
        foreach (var (command, mode) in new[]
        {
            ("PROGRAM CHAN LOCK", "SS"), ("SELECT BFO UNLOCK", "SS"),
            ("PROGRAM ADDRESS UNLOCK", "ALE"), ("SELECT KEY LOCK", "ALE"),
            ("PROGRAM TX_POWER UNLOCK", "HO"),
        })
        {
            int at = IndexOf(sent, l => l == command);
            Assert.True(at > 0, "never sent: " + command);
            var switches = sent.Take(at).Where(l => l is "SS" or "ALE" or "HO").ToList();
            Assert.Equal(mode, switches[^1]);
        }

        // AFTER all the programming: the file's lockout state must land last,
        // so no earlier leg can be blamed for it.
        int firstLockout = IndexOf(sent, IsLockoutSet);
        foreach (var programming in new[] { "SLFAD ", "ADDC ", "HOPTYPE ", "EXC 1 ", "TXMSG 0 ", "MODEM PRESET " })
        {
            int last = LastIndexOf(sent, l => l.StartsWith(programming, StringComparison.Ordinal));
            Assert.True(last >= 0, programming + "was never written at all");
            Assert.True(last < firstLockout, programming + "was written after the lockouts");
        }
    }

    /// <summary>
    /// LEG 11's FinalsOrder row, GREEN PATH (owner ruling R4): with no
    /// FM-squelch cycle owed, the analog squelch is written after the final
    /// channel selection and BEFORE the operating mode.
    /// </summary>
    [Fact]
    public async Task TheAnalogSquelch_IsWrittenAtTheFinals_BeforeTheModeButAfterTheChannel()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        var file = _clone.File!;
        // The file's mode is deliberately NOT the demo's current one, so leg 11
        // really emits its final mode switch. It did not before, and the pin
        // still passed: `sent` was the WHOLE campaign, so the "mode last" index
        // was the VERIFY read's own `HO`, several thousand bytes later. Found
        // when the closing restore (plan-clone-field-round2.md F1) added traffic
        // after the verify and the delimiter had to become honest.
        file.OperatingMode = "Hop";
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        // The WRITE portion: this is a claim about LEG 11's internal order, and
        // since F1 the campaign selects a channel again AFTER the verify —
        // outside leg 11 entirely, and pinned on its own in
        // CloneClosingRestoreTests.
        var sent = WritePortion(_port.Sent);

        int squelch = LastIndexOf(sent, l => l.StartsWith("SQ ", StringComparison.Ordinal));
        int channel = LastIndexOf(sent, l => l.StartsWith("CH ", StringComparison.Ordinal));
        int mode = LastIndexOf(sent, l => l is "SS" or "ALE" or "HO");
        Assert.True(squelch > channel, "the squelch was not written after the final channel selection");
        Assert.True(mode > squelch, "the operating mode was not written last");

        // …and it was NOT written with the other settings: the FM group is
        // written in leg 6, and a squelch written there would be overwritten by
        // Core's compensating cycle.
        int fm = LastIndexOf(sent, l => l.StartsWith("FMDE ", StringComparison.Ordinal));
        Assert.True(squelch > fm, "the squelch was written with the settings leg");
    }

    /// <summary>
    /// LEG 11's FinalsOrder row, RED PATH (owner ruling R4's safety net). When
    /// Core still owes an FM-squelch cycle at the settle bound, the row is
    /// SKIPPED and NAMED — never written into a cycle that would overwrite it.
    ///
    /// <para>REACHED THROUGH THE REAL STACK, not by poking a flag: a file whose
    /// RWAS is ENABLED forces the three squelches ON in leg 6, and the FM
    /// writes that follow then arm Core's cycle. The cycle fires only on a
    /// CHANGED modulation report, and the finals read reports the modulation
    /// the radio has had all along — so the flag is still up when the bound
    /// expires. Both paths are reachable; this is the one that is easy to
    /// believe is not.</para>
    /// </summary>
    [Fact]
    public async Task AnAnalogSquelchTheRadioStillOwesACycleFor_IsSkippedAndNamed()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        var file = _clone.File!;
        file.Settings.First(s => s.Key == "Rwas").Value = "Enabled";
        _clone.Adopt(file);
        _clone.AnalogSquelchSettleMs = 150;
        _prompt.EnqueueAnswer(true);

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        var skip = Assert.Single(_clone.Summary,
            l => l.Contains("automatic squelch cycle", StringComparison.Ordinal));
        Assert.Contains("Setting AnalogSquelch", skip, StringComparison.Ordinal);
        Assert.Contains("not written", skip, StringComparison.Ordinal);
        // The RED path is a SKIP, not a silent success: the verify then reports
        // the row as a difference too, which is what makes the skip visible
        // even to an operator who does not read the notes.
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("Setting AnalogSquelch: expected", StringComparison.Ordinal));
    }

    // ---- D9 CATEGORY B: the WRITE's verdict lines ---------------------------
    //
    // Owner ruling 2026-08-29: the STATUS LINE CARRIES THE VERDICT ONLY, and
    // the evidence lives in the summary lines below it. These pin the three
    // write end-state texts byte-for-byte, against the SAME triggers the
    // service has always used (notice count / problem count) — the counts
    // themselves are gone from the words, not from the accounting.

    /// <summary>D23 (owner 2026-08-30): the ordinary clean write says
    /// "Write complete." — the closing restore no longer writes its
    /// "Left the radio on…" NOTICE into the WRITE summary (it made every
    /// clean write read "with warnings"). Zero problems, zero notices,
    /// EMPTY summary. B4's "with warnings" stays reachable through a
    /// role-change notice — pinned by
    /// TheSummary_CarriesRoleChangesAsNotices_AndTheWriteStaysClean.</summary>
    [Fact]
    public async Task AKeepAllCleanWrite_SaysWriteComplete_WithAnEmptySummary()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        Assert.Equal(CloneState.Done, _clone.State);
        Assert.Equal("Write complete.", _clone.StatusText);
        // D23: the restore still RUNS (the closing-restore suite proves it by
        // state); it just claims nothing here.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal));
        Assert.Empty(_clone.Summary);
    }

    /// <summary>
    /// B3's text — the zero-notice clean write — pinned as SOURCE.
    ///
    /// <para><b>Why source and not a campaign.</b> Every write that reaches the
    /// verdict CLEAN has run the closing restore, and the restore's success is
    /// itself a NOTICE (<c>RestoreOperatingStateAsync</c>, both of its success
    /// returns), so <c>_notices</c> is never zero on a path this suite can
    /// drive. The branch survives for the one arrangement that would produce it
    /// — a session that drops after a clean verify, which skips the restore
    /// entirely — and P6 changed its WORDS, not its trigger. A byte pin is
    /// still owed, so it is taken where the string actually lives.</para>
    ///
    /// <para>ACCEPTED LIMITATION, per the house style for source pins: this
    /// reads the literal, not the branch that selects it.</para>
    /// </summary>
    [Fact]
    public void TheZeroNoticeCleanWriteVerdict_IsTheTrimmedText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
            dir = dir.Parent!;
        Assert.NotNull(dir);

        var code = File.ReadAllText(Path.Combine(
            dir.FullName, "src", "Falcon.App.Core", "Cloning", "CloneService.cs"));

        Assert.Contains("? \"Write complete.\"", code, StringComparison.Ordinal);
        // …and the pre-trim sentence is gone, not merely shadowed.
        Assert.DoesNotContain("reads back exactly as the file", code, StringComparison.Ordinal);
    }

    /// <summary>A write with a PROBLEM line — B5's text. The problem count that
    /// used to ride the status line is the summary's own length now.</summary>
    [Fact]
    public async Task AWriteWithProblems_SaysWriteIncomplete()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        var file = _clone.File!;
        file.Settings.First(s => s.Key == "Rwas").Value = "Enabled";
        _clone.Adopt(file);
        _clone.AnalogSquelchSettleMs = 150;
        _prompt.EnqueueAnswer(true);

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Equal(CloneState.Failed, _clone.State);
        Assert.Equal("Write incomplete.", _clone.StatusText);
        // …and the problems are still all there, by name.
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("Setting AnalogSquelch: expected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingTheConfirm_SendsNothing()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());

        // THE ONE CANCEL THAT IS MEANT TO BE CONSUMED — this test legitimately
        // reaches the confirmation, so the boilerplate "a queued CANCEL that
        // must never be consumed" the refusal tripwires carry does NOT belong
        // here (plan/plan-clone-file-validation.md §3, the recorded NIT: the
        // label made a count of tripwires wrong by one). The SECOND queued
        // cancel is the tripwire: a campaign that asked twice would consume it,
        // and the CallCount assertion below is what catches that.
        _prompt.EnqueueAnswer(false);
        _prompt.EnqueueAnswer(false);
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(1, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    [Fact]
    public async Task AFaultedConfirmTask_SendsNothing()
    {
        // The §5 lifecycle contract: a platform alert that throws must leave
        // the campaign sending nothing rather than wedging or proceeding.
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _port.ClearSent();

        var write = _clone.WriteAsync(CloneSwapTests.Rows());
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline && _prompt.CallCount == 0) Thread.Sleep(5);
        _prompt.Last.Fault();

        Assert.False(await write);
        Assert.Empty(_port.Sent);
    }

    // =====================================================================
    // ROUND 17 — plan-clone-write-structural.md P3
    // =====================================================================

    /// <summary>
    /// MANUFACTURE A STANDING SENTINEL DEBT, deterministically and at the byte
    /// seam: one ALE read's closing <c>BAT ST</c> is SWALLOWED (R6 — the radio
    /// really does drop commands), so Core times that sentinel out and the
    /// answer it counted is owed for ever. Nothing can pay it: the discard that
    /// clears a debt needs an ANSWER to arrive, and this one never will.
    ///
    /// <para>That is exactly the state the 2026-08-28 field write was in when
    /// all 32 of its book operations faulted — and the ledger survives the
    /// wipe (only <c>Connect</c> resets it), so the debt is still standing when
    /// the write campaign reaches its first gated operation.</para>
    /// </summary>
    private async Task ManufactureStandingDebtAsync()
    {
        _radio.Ale.RefreshTimeoutMs = 150;
        _port.SwallowAleReadSentinels = true;
        _ale.RefreshStationList();
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
               && !(_radio.PingAnswerDebt == 1 && _radio.PendingPingCount == 0))
            await Task.Delay(5);
        _port.SwallowAleReadSentinels = false;
        _radio.Ale.RefreshTimeoutMs = 10_000;

        Assert.Equal(1, _port.SwallowedSentinels);
        Assert.Equal(1, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);
    }

    /// <summary>The book leg's operations, FLATTENED in the order
    /// <c>RunWriteCampaignAsync</c> builds them: selfs, individuals, then each
    /// net followed by its own members.</summary>
    private static int BookOperationCount(CloneFile file)
        => file.Selfs.Count + file.Individuals.Count
            + file.Nets.Count + file.Nets.Sum(n => n.Members.Count);

    /// <summary>
    /// D20, THE HEADLINE PIN (plan-clone-write-structural §2, owner report
    /// 2026-08-30 — "close and open the app each time? I get 50% failures on
    /// both android and windows"): <b>A CAMPAIGN NEVER INHERITS A PREVIOUS
    /// ATTEMPT'S DEBT.</b> The fixture is unchanged — a swallowed sentinel
    /// leaves a debt nothing can pay — but the write campaign now re-baselines
    /// the ledger at its own start (inside the lease, before its first own
    /// sentinel), so the gated book leg opens on a clean accounting and runs.
    ///
    /// <para><b>RE-BASED from D3's abandonment pin, and this is the whole
    /// change.</b> Until D20 this same fixture produced ONE abandonment line
    /// naming the first book row; that sentence is asserted ABSENT below,
    /// byte-for-byte, so a reset that stopped running fails here rather than
    /// quietly restoring the old behaviour. RED-CHECK (run 2026-08-30, then
    /// reverted): delete <c>_radio.ResetSentinelLedger()</c> from
    /// <c>CloneService.WriteAsync</c> and this pin fails on that exact line
    /// coming back.</para>
    ///
    /// <para><b>What it does NOT say.</b> D20 removes debt INHERITANCE, not
    /// debt: a debt minted DURING a campaign is still the campaign's own and
    /// still faults its gate (the leg-abandonment machinery is untouched, and
    /// the gate's typed <c>SentinelDebt</c> fault keeps its own pins in
    /// <c>AleProgrammingGateTests</c>).</para>
    /// </summary>
    [Fact]
    public async Task ACampaignStartingWithStandingDebt_ClearsItAtItsStart_AndTheGatedLegRuns_D20()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var file = _clone.File!;
        int bookOps = BookOperationCount(file);
        // The demo's book is a real one: this pin would be worth nothing over a
        // leg of one or two rows.
        Assert.True(bookOps >= 10, $"the demo's book flattens to only {bookOps} operations");

        _ale.Programming.DebtSettleMs = 50;      // the settle window, compressed
        await ManufactureStandingDebtAsync();    // …and its own asserts prove debt 1 stands
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());

        // THE ABANDONMENT LINE IS GONE, byte-for-byte — the sentence D3 wrote
        // when this fixture's debt reached the gate.
        Assert.DoesNotContain(_clone.Summary,
            l => l == $"ALE book: the radio's sentinel accounting did not settle at "
                    + $"'self {file.Selfs[0].Name}' — this and the remaining {bookOps - 1} "
                    + "book operations were not attempted.");
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("the radio's sentinel accounting did not settle", StringComparison.Ordinal));

        // …nor its verify half, which only an abandoned domain produces.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("ALE book: not compared", StringComparison.Ordinal));

        // …and no per-row debt fault either: the ledger really was clean, not
        // merely quiet.
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("behind on its sentinel answers", StringComparison.Ordinal));

        // ANTI-VACUITY: the book leg REACHED THE WIRE. An absence pin over a
        // campaign that never got there would pass for the wrong reason.
        Assert.Contains(WritePortion(_port.Sent),
            l => l.StartsWith("SLFAD ", StringComparison.Ordinal));
        Assert.Equal(0, _radio.PingAnswerDebt);
    }

    /// <summary>
    /// D20's other half, over §5.4d's two gated legs: the groups leg (8) opens
    /// on the SAME cleared ledger the book leg did, so BOTH run — and leg 8b's
    /// ungated schedules, which a debt never had a hold over, still reach the
    /// wire beside them.
    ///
    /// <para>RE-BASED from D3's interaction pin (an abandoned leg abandons only
    /// itself), whose premise — a debt standing at the campaign's start — is
    /// exactly what D20 removes. The groups leg's own abandonment sentence is
    /// asserted ABSENT byte-for-byte, so the same red-check applies: remove
    /// <c>ResetSentinelLedger</c> and it comes back.</para>
    /// </summary>
    [Fact]
    public async Task ACampaignStartingWithStandingDebt_RunsBOTHGatedLegs_AndTheSchedules_D20()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var file = _clone.File!;
        var firstGroup = file.ChannelGroups.First(g => g.Channels.Count > 0);
        int groupOps = file.ChannelGroups.Sum(g => g.Channels.Count);
        Assert.True(file.Schedules.Count > 0, "the demo carries no schedule to attempt");

        _ale.Programming.DebtSettleMs = 50;
        await ManufactureStandingDebtAsync();
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());

        // Neither gated leg is abandoned — the groups leg's own sentence,
        // byte-for-byte, is absent.
        Assert.DoesNotContain(_clone.Summary,
            l => l == "Channel groups: the radio's sentinel accounting did not settle at "
                    + $"'add channel {firstGroup.Channels.Order().First():00} to group {firstGroup.Group}' — "
                    + $"this and the remaining {groupOps - 1} channel group operations were not attempted.");
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Channel groups: not compared", StringComparison.Ordinal));

        // …and all three legs really reached the wire: the book (7), the groups
        // (8) and the UNGATED schedules (8b).
        var written = WritePortion(_port.Sent);
        Assert.Contains(written, l => l.StartsWith("SLFAD ", StringComparison.Ordinal));
        Assert.Contains(written, l => l.StartsWith("CHG ", StringComparison.Ordinal));
        Assert.Contains(_port.Sent, l => l.StartsWith("SOU STA", StringComparison.Ordinal));
    }

    /// <summary>
    /// D20 at the READ campaign's start, which takes the same call in the same
    /// place — inside the lease, before the discovery sentinel. A read that
    /// followed a failed write used to inherit its debt too.
    /// </summary>
    [Fact]
    public async Task AReadStartingWithStandingDebt_ClearsItAtItsStart_D20()
    {
        ConnectReady();
        await ManufactureStandingDebtAsync();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The inherited debt is gone and the read is COMPLETE — the campaign
        // ran on a clean ledger and nothing it owed survived it.
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal("Read complete.", _clone.StatusText);
    }

    /// <summary>
    /// D20's STRUCTURAL half: BOTH public campaigns take the reset, and each
    /// takes it INSIDE its own wire lease — before anything of the campaign's
    /// reaches the wire, and after the lease has silenced every producer that
    /// could otherwise mint a debt between the reset and the first leg.
    /// </summary>
    [Fact]
    public void BothPublicCampaigns_ResetTheSentinelLedgerInsideTheirLease_D20()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Falcon.App.Core", "Cloning", "CloneService.cs"));

        // The seam exists on the radio, so the two call sites below are not
        // names that compile to nothing.
        Assert.NotNull(typeof(Falcon.Core.Radio.Prc138Radio)
            .GetMethod(nameof(Falcon.Core.Radio.Prc138Radio.ResetSentinelLedger)));

        // EXACTLY TWO call sites — one per public campaign. A third would mean
        // some inner leg is re-baselining mid-campaign, which is precisely the
        // masking D20 must not do. Anchored at STATEMENT START, so a commented-out
        // call (the red-check's own mutation) is not counted as one.
        var calls = Regex.Matches(source, @"(?m)^\s*_radio\.ResetSentinelLedger\(\)\s*;");
        Assert.Equal(2, calls.Count);

        // …and each one sits after its own `using (_wire.Enter())` and before
        // the campaign body it guards.
        foreach (Match call in calls)
        {
            int lease = source.LastIndexOf("using (_wire.Enter())", call.Index, StringComparison.Ordinal);
            Assert.True(lease >= 0, "a ResetSentinelLedger call is outside the wire lease");
            Assert.DoesNotContain("RunReadCampaignAsync", source[lease..call.Index], StringComparison.Ordinal);
            Assert.DoesNotContain("RunWriteCampaignAsync", source[lease..call.Index], StringComparison.Ordinal);
        }
    }

    // ---- D4: the write side of the elision ---------------------------------

    /// <summary>
    /// The write leg SKIPS a row equal to <c>Wire.DefaultChannel</c> — in ANY
    /// file, elided or legacy — because leg 2 has just left every slot holding
    /// exactly that. Pinned over a LEGACY FULL FILE, which is the case that
    /// proves the skip is the WRITE's rule and not a side effect of the read
    /// storing fewer rows.
    /// </summary>
    [Fact]
    public async Task ALegacyFullFile_LoadsUnchanged_AndWritesOnlyItsNonDefaultChannels()
    {
        ConnectReady();
        var legacy = CloneFileTests.Complete();
        CloneFileTests.FillChannels(legacy);                 // 100 rows, all default…
        legacy.Channels[7] = new CloneChannel
        {
            Number = 7, RxFrequency = "09000000", TxFrequency = "09000000",
            Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
        };
        _clone.LoadJson(legacy.Save());

        // The LOAD is byte-unchanged: no marker, so the 100-row completeness
        // rule applies and the domain stays Read.
        var loaded = _clone.File!;
        Assert.False(loaded.DefaultChannelsElided);
        Assert.Equal(100, loaded.Channels.Count);
        Assert.Equal(CloneDomainState.Read, loaded.ChannelState);
        Assert.Empty(loaded.LoadNotices);

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        // ONE channel's field batch, and it is slot 07's — the other 99 rows
        // are the factory default and the wipe already set them. The 99
        // skipped rows would each have carried an `RXF 01600000`.
        Assert.Equal(["RXF 09000000"],
            sent.Where(l => l.StartsWith("RXF ", StringComparison.Ordinal)));
        // …and only ONE store selection ran in the channel leg. The leg is
        // bounded by the campaign's first move to `ALE>` (the messages leg), so
        // the finals' own `CH` is outside the window.
        int toAle = IndexOf(sent, l => l == "ALE");
        Assert.True(toAle > 0, "the campaign never reached the ALE prompt");
        Assert.Equal(["CH 7"],
            sent.Take(toAle).Where(l => l.StartsWith("CH ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// THE WRITE SIDE of the no-trim rule (audit round 1). A LEGACY full file
    /// whose row differs from the factory default only by surrounding
    /// whitespace is a DIFFERENT stored value, so the write leg does NOT skip
    /// it: the slot is selected and its fields go out. Trimming the comparison
    /// would silently drop a row the operator's file really carries.
    /// </summary>
    [Fact]
    public async Task AChannelDifferingFromTheDefaultOnlyByWhitespace_IsWritten_NotSkipped()
    {
        ConnectReady();
        var legacy = CloneFileTests.Complete();
        CloneFileTests.FillChannels(legacy);                 // 100 rows, all default…
        legacy.Channels[7].Mode = " USB";                    // …except one, by a space
        Assert.False(legacy.Channels[7].IsFactoryDefault());
        _clone.LoadJson(legacy.Save());

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        // The row was NOT skipped: slot 07 is selected and its field batch goes
        // out — a skipped row would send neither line.
        int toAle = IndexOf(sent, l => l == "ALE");
        Assert.True(toAle > 0, "the campaign never reached the ALE prompt");
        Assert.Equal(["CH 7"],
            sent.Take(toAle).Where(l => l.StartsWith("CH ", StringComparison.Ordinal)));
        Assert.Equal(["RXF 01600000"],
            sent.Where(l => l.StartsWith("RXF ", StringComparison.Ordinal)));
    }

    // ---- D10: leg 8b, the schedule reorder ---------------------------------

    /// <summary>
    /// D10 (§5.4d), ON THE WIRE: the schedule queue writes go out AFTER the
    /// last <c>ADDC</c> of the channel-group leg. Before this round they
    /// preceded it, and on a zeroize-first campaign that ordering could NEVER
    /// land a SOUND schedule — the named self's channel group was still empty,
    /// so the radio refused it <c>SELF CHANS REQD</c> (2026-08-28 instrument
    /// write, tMs 219786; the owner's 2026-08-29 read then answered
    /// <c>NO LQA SCHEDULED</c>).
    ///
    /// <para>Same ALE occupancy, so no new mode lap: the assertion below is
    /// that no mode command separates the last group add from the schedules.</para>
    /// </summary>
    [Fact]
    public async Task TheScheduleLeg_GoesOutAfterTheLastGroupAdd_InTheSameAleOccupancy()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        int lastAdd = LastIndexOf(sent, l => l.StartsWith("ADDC ", StringComparison.Ordinal));
        int firstSchedule = IndexOf(sent, l => l.StartsWith("SOU STA", StringComparison.Ordinal));
        Assert.True(lastAdd >= 0, "the fixture wrote no channel groups at all");
        Assert.True(firstSchedule > lastAdd,
            $"the schedule went out at {firstSchedule}, before the last group add at {lastAdd}");

        // SAME OCCUPANCY: no mode command between them, so the leg costs no
        // extra lap and needs no prompt of its own.
        Assert.DoesNotContain(
            sent.Skip(lastAdd + 1).Take(firstSchedule - lastAdd - 1),
            l => l is "ALE" or "SS" or "HO");
    }

    /// <summary>
    /// §5.4d's COMPLETION-POINT RULE, at its limit: "after the groups leg"
    /// means after leg 8's completion point, and a file with NO channel groups
    /// has no <c>ADDC</c> to follow — the position is defined by LEG ORDER, not
    /// by a preceding wire byte. So a file holding schedules and no groups at
    /// all still writes them, in the same ALE occupancy.
    ///
    /// <para>Audit round 1 found this uncovered: a build that dropped the
    /// schedules whenever the group leg was empty stayed green across the whole
    /// suite, because every other schedule pin runs over the demo's ten
    /// populated groups.</para>
    /// </summary>
    [Fact]
    public async Task TheScheduleLeg_StillRuns_WhenTheFileHasNoChannelGroupsAtAll()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var file = _clone.File!;
        file.ChannelGroups.Clear();
        Assert.NotEmpty(file.Schedules);          // …there is something to lose
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        // The group leg really had nothing to do — so the schedules follow a
        // leg that wrote not one byte.
        Assert.DoesNotContain(sent, l => l.StartsWith("ADDC ", StringComparison.Ordinal));
        int firstSchedule = IndexOf(sent, l => l.StartsWith("SOU STA", StringComparison.Ordinal));
        Assert.True(firstSchedule >= 0, "the schedule leg was skipped when the groups leg was empty");
        Assert.Contains(sent, l => l.StartsWith("EXCH STA", StringComparison.Ordinal));

        // …and still in the BOOK's own ALE occupancy: no mode command between
        // the last book write and the schedules.
        int lastBookWrite = LastIndexOf(sent,
            l => l.StartsWith("SLFAD ", StringComparison.Ordinal)
              || l.StartsWith("INDAD ", StringComparison.Ordinal)
              || l.StartsWith("NETAD ", StringComparison.Ordinal)
              || l.StartsWith("ADDM ", StringComparison.Ordinal));
        Assert.True(lastBookWrite >= 0 && lastBookWrite < firstSchedule);
        Assert.DoesNotContain(
            sent.Skip(lastBookWrite).Take(firstSchedule - lastBookWrite),
            l => l is "ALE" or "SS" or "HO");
    }

    /// <summary>
    /// D10's UNCHANGED half. The leg keeps its closing sentinel bracket, the
    /// schedules stay UNGATED (no programming bracket around them), and a
    /// refusal stays non-fatal — the campaign runs on to the HOP legs and the
    /// finals, exactly as it did when the block sat at the tail of leg 7.
    /// </summary>
    [Fact]
    public async Task TheScheduleLeg_KeepsItsSentinelBracket_AndStaysUngated()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        int lastSchedule = LastIndexOf(sent,
            l => l.StartsWith("SOU STA", StringComparison.Ordinal)
              || l.StartsWith("EXCH STA", StringComparison.Ordinal));
        Assert.True(lastSchedule >= 0);
        // The closing bracket: a BAT ST immediately behind the last schedule.
        Assert.Equal("BAT ST", sent[lastSchedule + 1]);
        // UNGATED: no opening bracket in front of the first one either — the
        // gate's shape is BAT ST, write, BAT ST, and a schedule has none of it.
        int firstSchedule = IndexOf(sent, l => l.StartsWith("SOU STA", StringComparison.Ordinal));
        Assert.NotEqual("BAT ST", sent[firstSchedule + 1]);
        // …and the campaign carried on past the leg to the HOP legs and finals.
        Assert.Contains(sent.Skip(lastSchedule), l => l.StartsWith("HOPTYPE ", StringComparison.Ordinal));
    }

    /// <summary>
    /// D10's THIRD half: a schedule the radio REFUSES is still reported by the
    /// VERIFY and by nothing else — no new reporting path was added with the
    /// move. The refusal is scripted at the byte seam (the demo answers
    /// <c>SELF CHANS REQD</c> to nothing of its own), so what is pinned is the
    /// campaign's handling of it.
    /// </summary>
    [Fact]
    public async Task AScheduleTheRadioRefuses_IsNamedByTheVerify_AndIsNotFatal()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        _prompt.EnqueueAnswer(true);
        // The refusal, SCRIPTED at the byte seam: the `SOU STA` line is
        // recorded as sent and the radio never acts on it, which is what a
        // `SELF CHANS REQD` refusal amounts to for the file's purposes.
        _port.SwallowFirstLineStartingWith = "SOU STA";
        // A swallowed command leaves no prompt behind, so compress the
        // transport's write gate rather than wait out its full budget.
        _transport.GateTimeoutMs = 300;

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.True(_port.SwallowedTheLine, "the fixture never dropped the schedule line");
        // The EXISTING diff sentence, from the EXISTING comparison path — the
        // move added no reporting path of its own.
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("Schedule SOUND ", StringComparison.Ordinal)
              && l.Contains("the radio does not hold it", StringComparison.Ordinal));
        // NON-FATAL: the leg reported nothing itself and the campaign ran on to
        // the end — the closing restore happened.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("LQA schedules:", StringComparison.Ordinal));
        Assert.DoesNotContain(_clone.Summary, l => l.StartsWith("Left the radio on", StringComparison.Ordinal));   // D23
    }

    // ---- Leg order and prompts -----------------------------------------------

    [Fact]
    public async Task TheWriteCampaign_RunsTheLegTableInOrder_AtTheRightPrompts()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        // One landmark per leg, in the ROUND-12 §3 order. Indexes, not
        // adjacency: the legs carry many commands each, and what is pinned is
        // their SEQUENCE.
        int zeroize = IndexOf(sent, l => l == "ZERO");
        int channels = IndexOf(sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));
        int presets = IndexOf(sent, l => l.StartsWith("MODEM PRESET ", StringComparison.Ordinal));
        int messages = IndexOf(sent, l => l.StartsWith("TXMSG 0 ", StringComparison.Ordinal));
        int settings = IndexOf(sent, l => l.StartsWith("RWAS ", StringComparison.Ordinal));
        int book = IndexOf(sent, l => l.StartsWith("SLFAD ZZZ", StringComparison.Ordinal));
        int members = IndexOf(sent, l => l.StartsWith("ADDM ", StringComparison.Ordinal));
        // D10 (round 17): the schedule leg MOVED — it is now leg 8b, AFTER the
        // channel groups, because `SOU STA`/`EXC STA` are refused
        // `SELF CHANS REQD` while the named station's group is still empty and
        // a zeroize-first campaign leaves every group empty until leg 8 has run.
        int groups = IndexOf(sent, l => l.StartsWith("CHG ", StringComparison.Ordinal));
        int schedules = IndexOf(sent, l => l.StartsWith("SOU STA", StringComparison.Ordinal));
        int hopNets = IndexOf(sent, l => l.StartsWith("HOPTYPE ", StringComparison.Ordinal));
        int bands = IndexOf(sent, l => l.StartsWith("EXC 1 ", StringComparison.Ordinal));
        int lockouts = IndexOf(sent, IsLockoutSet);
        int finalNet = LastIndexOf(sent, l => l.StartsWith("NET ", StringComparison.Ordinal));
        int finalChannel = LastIndexOf(sent, l => l.StartsWith("CH ", StringComparison.Ordinal));
        int finalSquelch = LastIndexOf(sent, l => l.StartsWith("SQ ", StringComparison.Ordinal));

        int[] order = [zeroize, channels, presets, messages, settings, book, members,
            groups, schedules, hopNets, bands, lockouts, finalNet, finalChannel, finalSquelch];
        Assert.DoesNotContain(-1, order);
        for (int i = 1; i < order.Length; i++)
            Assert.True(order[i] > order[i - 1],
                $"leg {i} landed at {order[i]}, before leg {i - 1} at {order[i - 1]}");

        // The prompt each leg needs: the message and fill legs are bracketed by
        // an ALE mode switch, the HOP legs by a HOP one, and the channel leg
        // runs before the app has left SSB at all.
        int toAle = IndexOf(sent, l => l == "ALE");
        int toHop = IndexOf(sent, l => l == "HO");
        Assert.True(toAle < messages && toAle > channels, "the message leg did not run at an ALE prompt");
        Assert.True(toHop < hopNets && toHop > book, "the HOP nets did not run at a HOP prompt");
    }

    /// <summary>
    /// §3 leg 11 — the operating MODE is written LAST of all, after the net,
    /// the channel and the finals squelch.
    ///
    /// <para>Pinned on a file whose mode DIFFERS from where the campaign leaves
    /// the radio, because that is the only way the switch is observable at all:
    /// with a matching mode the finals send nothing and the assertion would
    /// pass on a campaign that never ordered anything.</para>
    /// </summary>
    [Fact]
    public async Task TheOperatingMode_IsWrittenLastOfAll()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        var file = _clone.File!;
        file.OperatingMode = "Hop";
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());
        var sent = WritePortion(_port.Sent);

        // The finals, in order: net, channel, squelch…
        int net = LastIndexOf(sent, l => l.StartsWith("NET ", StringComparison.Ordinal));
        int channel = LastIndexOf(sent, l => l.StartsWith("CH ", StringComparison.Ordinal));
        int squelch = LastIndexOf(sent, l => l.StartsWith("SQ ", StringComparison.Ordinal));
        Assert.True(net >= 0 && net < channel && channel < squelch,
            "the finals did not run net → channel → squelch");

        // …and the VERY NEXT command after them, sentinels aside, is the mode
        // switch. Not "somewhere later": the whole point of writing the mode
        // last is that nothing the campaign does can move it afterwards.
        int mode = -1;
        for (int i = squelch + 1; i < sent.Count; i++)
        {
            if (sent[i] is "BAT ST" or "") continue;
            mode = i;
            break;
        }
        Assert.True(mode > 0, "the campaign sent nothing after the finals squelch");
        Assert.Equal("HO", sent[mode]);
    }

    [Fact]
    public async Task TheHopLeg_IsPureWrites_BecauseTheWipeAlreadyClearedEveryNet()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));
        var sent = _port.Sent;

        // ABSENCE PIN. The clear-first wipe led every net so the replay was
        // idempotent over an UNKNOWN record; the campaign now zeroizes first
        // (owner statement §1), so not one `HOPSET n DEL` may go out.
        for (int net = 0; net <= 9; net++)
            Assert.DoesNotContain($"HOPSET {net} DEL", sent);

        // …and the WIPED nets are simply not written, rather than written as
        // an invented blank record.
        Assert.DoesNotContain(sent, l => l.StartsWith("HOPTYPE 1 ", StringComparison.Ordinal));

        // The programmed ones keep their captured field ORDER: type before the
        // hopset, and the net id before anything generates from it.
        int type = IndexOf(sent, l => l == "HOPTYPE 3 LIST");
        int id = IndexOf(sent, l => l.StartsWith("NETID 3 ", StringComparison.Ordinal));
        int list = IndexOf(sent, l => l.StartsWith("HOPLIST 3 ADD", StringComparison.Ordinal));
        Assert.True(type >= 0 && type < id && id < list,
            "the LIST net was not written type → id → values");
    }

    // ---- Abort ---------------------------------------------------------------

    [Fact]
    public async Task ASessionDropMidCampaign_AbortsCleanly_AndTheSummarySaysWhere()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);

        // Drop the session ON the write that reaches the wire — DETERMINISTIC
        // (audit round 2, manager ruling). The previous version polled
        // `_port.Sent` from a `Task.Run` every millisecond; under full-suite
        // load that watcher was scheduled late, the drop landed after the
        // write legs, and the campaign reported "The session dropped during
        // verification." — red in every full run and green alone. The port
        // wrapper's `OnLineWritten` hook fires INSIDE the write, so the drop
        // cannot arrive anywhere but on the leg that wrote `RXF`. No Task.Run,
        // no delay, no deadline.
        int drops = 0;
        _port.OnLineWritten = line =>
        {
            if (!line.StartsWith("RXF ", StringComparison.Ordinal)) return;
            _port.OnLineWritten = null;          // exactly once, on the FIRST one
            drops++;
            _session.Close();
        };

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        // ANTI-VACUITY: the drop really happened, and it happened ON the wire
        // rather than before the campaign got there.
        Assert.Equal(1, drops);
        Assert.Contains(_port.Sent, l => l.StartsWith("RXF ", StringComparison.Ordinal));

        Assert.Equal(CloneState.Failed, _clone.State);
        Assert.Contains("Stopped at the", _clone.StatusText, StringComparison.Ordinal);
        Assert.NotEmpty(_clone.Summary);
    }

    // ---- F9: THE HOP-SCOPED MODEM PRESETS (7-9) ------------------------------
    // The modem book is PROMPT-SPLIT (probe P5): 0-6 live at `SSB>` and 7-9 at
    // `HOP>`, each prompt answering `INVALID MODEM PRESET` for the other's
    // numbers. The campaign only ever asked at `SSB>`, so a clone silently
    // dropped three presets and the verify could not see the loss.

    [Fact]
    public async Task TheReadCampaign_ReadsTheHopPresets_AT_THE_HOP_PROMPT()
    {
        ConnectReady();
        _port.ClearSent();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent;
        // The three targeted HOP reads and the bulk listing behind them go out
        // AFTER the campaign has entered HOP and BEFORE it leaves.
        int hopEntry = sent.ToList().FindIndex(l => l == "HO");
        int read7 = sent.ToList().FindIndex(l => l == "MODEM PRE 7");
        Assert.True(hopEntry >= 0, "the campaign never entered HOP");
        Assert.True(read7 > hopEntry,
            "MODEM PRE 7 went out before the HOP prompt — it would answer INVALID MODEM PRESET there");
        foreach (var line in new[] { "MODEM PRE 7", "MODEM PRE 8", "MODEM PRE 9" })
            Assert.Contains(line, sent);

        // …and the SSB band's own reads still went out at the SSB prompt, i.e.
        // BEFORE the HOP entry — the two legs are separate, not merged.
        Assert.True(sent.ToList().FindIndex(l => l == "MODEM PRE 6") < hopEntry,
            "the SSB band's reads moved to the HOP leg");

        // NOTHING out of band, either direction: no `MODEM PRE 7` at SSB>, no
        // `MODEM PRE 0` at HOP>.
        Assert.DoesNotContain(sent.Skip(hopEntry), l => l == "MODEM PRE 0");
    }

    [Fact]
    public async Task TheWriteCampaign_WritesTheHopPresets_AT_THE_HOP_PROMPT_StateTokenLast()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        _demo.ApplyScriptedPerturbation();

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent.ToList();

        // Leg 4 (SSB>) carries the 0-6 band and NOTHING of the 7-9 one…
        int leg4 = sent.FindIndex(l => l.StartsWith("MODEM PRESET 0 NAME", StringComparison.Ordinal));
        Assert.True(leg4 >= 0, "the SSB preset leg did not run");

        // …and the 7-9 lines are the SHORT form, sent after a HOP entry that
        // FOLLOWS the SSB leg (leg 9), with the EN/DIS token on its OWN line
        // immediately after each field line (P5b: any field write re-enables).
        // The FILE was read BEFORE the perturbation, so it holds the demo's
        // FOUND state: 9 enabled, 7 and 8 not. Each state token follows its own
        // field line, and the two differ — which is what proves the token is
        // the file's value and not a constant.
        int field9 = sent.FindIndex(l => l == "MODEM PRESET 9 NAME DAT9 ASYNC REMOTE BAUD 300");
        Assert.True(field9 > leg4, "the HOP preset leg did not run after the SSB one");
        Assert.Equal("MODEM PRESET 9 EN", sent[field9 + 1]);

        int field8 = sent.FindIndex(l => l == "MODEM PRESET 8 NAME DAT8 ASYNC REMOTE BAUD 300");
        Assert.True(field8 >= 0, "preset 8's field line never went out");
        Assert.Equal("MODEM PRESET 8 DIS", sent[field8 + 1]);

        // The HOP lines carry NO TYPE — the prompt answers `** ERROR **` to one.
        foreach (var line in sent.Where(l => l.StartsWith("MODEM PRESET 7", StringComparison.Ordinal)
                                          || l.StartsWith("MODEM PRESET 8", StringComparison.Ordinal)
                                          || l.StartsWith("MODEM PRESET 9", StringComparison.Ordinal)))
            Assert.DoesNotContain(" TYPE ", line, StringComparison.Ordinal);

        // A prompt switch to HOP separates the two legs.
        Assert.True(sent.Take(field8).Any(l => l == "HO"),
            "the HOP preset leg was not preceded by a HOP entry");
    }

    [Fact]
    public async Task AHandEditedHopPreset_WithAnUnstorableBaud_IsREPORTED_AtTheWrite_NotWritten()
    {
        // THE VALIDATION IDIOM, named: `CloneModemPreset.Fields` is BOUNDED, not
        // Validated (CloneFileValidation) — a row is re-parsed AT THE WRITE and
        // one this app cannot re-send is reported per preset and skipped, rather
        // than refused at LOAD. A hand-edited `BAUD 1200` on a hop preset is
        // exactly that case: the radio SILENTLY ignores it and echoes the old
        // value (P5c), so this refusal is the only place the operator learns.
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        Assert.Single(file.ModemPresets, p => p.Number == 9).Fields = "DAT9 ASYNC REMOTE BAUD 1200";
        // The FILE still loads — the bound is the preset NUMBER, not the baud.
        var reloaded = CloneFile.Load(file.Save());
        Assert.Equal("DAT9 ASYNC REMOTE BAUD 1200",
            Assert.Single(reloaded.ModemPresets, p => p.Number == 9).Fields);

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        await _clone.WriteAsync(CloneSwapTests.Rows());

        Assert.Contains(_clone.Summary,
            l => l.Contains("Modem preset 9", StringComparison.Ordinal)
                 && l.Contains("not one this app can re-send", StringComparison.Ordinal));
        Assert.DoesNotContain(_port.Sent,
            l => l.StartsWith("MODEM PRESET 9", StringComparison.Ordinal));
        // ANTI-VACUITY: its siblings DID go out, so this is a per-preset skip
        // and not a leg that failed to run.
        Assert.Contains("MODEM PRESET 8 NAME DAT8 ASYNC REMOTE BAUD 300", _port.Sent);
    }

    [Fact]
    public async Task ChannelsAboveThirtyMegahertz_AreWritten_F5()
    {
        // F5, the field defect: the source radio's CH 01/02/03 hold 51.5 / 51.0
        // / 50.25 MHz and every one of them was REFUSED app-side, on a bound
        // nobody had measured. Probe P2 measured it (59 999 999 Hz), and a file
        // holding those channels now reaches the wire.
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        var channel = Assert.Single(file.Channels, c => c.Number == 1);
        channel.RxFrequency = "51500000";
        channel.TxFrequency = "51500000";

        _prompt.EnqueueAnswer(true);
        _port.ClearSent();
        await _clone.WriteAsync(CloneSwapTests.Rows());

        Assert.Contains("RXF 51500000", _port.Sent);
        Assert.Contains("TXF 51500000", _port.Sent);
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("channel 01 receive frequency", StringComparison.Ordinal));
    }

    // ---- THE ROUND TRIP, WITH PERTURBATION -----------------------------------

    [Fact]
    public async Task TheDemoRoundTrip_WithEveryDomainPerturbed_VerifiesClean()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var before = _clone.File!.Save();

        // Move the radio out from under the file — every stateful domain.
        _demo.ApplyScriptedPerturbation();

        // PROVE the perturbation landed: a second read must differ from the
        // first. Without this the round trip could pass on a demo that never
        // moved, which is the false green the gate exists to kill.
        var probe = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        { SentinelTimeoutMs = 5_000 };
        Assert.True(await probe.ReadAsync(), string.Join(" | ", probe.Summary));
        var perturbedDiffs = CloneCompare.Diff(CloneFile.Load(before), probe.File!);
        Assert.True(perturbedDiffs.Count > 15,
            "the perturbation moved too little to prove anything: " + string.Join(" | ", perturbedDiffs));
        // F9 anti-vacuity: the perturbation moved the HOP BAND too (preset 8's
        // name, port and baud, and the enabled set at both 8 and 9), so a round
        // trip that never wrote presets 7-9 cannot pass this by accident.
        Assert.Contains(perturbedDiffs, d => d.Contains("Modem preset 8", StringComparison.Ordinal));
        Assert.Contains(perturbedDiffs, d => d.Contains("Modem preset 9", StringComparison.Ordinal));

        foreach (var domain in new[]
        {
            "SSB channel", "Self addresses", "Individual", "Net ", "Channel group", "Schedule",
            "HOP net", "Exclusion band", "Modem preset", "Stored message", "Setting", "Operating",
            // Clone round 12: the lockout domain joins the gate. Without this
            // the round trip would prove nothing at all about it.
            "Lockout ",
        })
            Assert.Contains(perturbedDiffs, d => d.Contains(domain, StringComparison.Ordinal));

        // …now write the file back and require a CLEAN verify.
        _prompt.EnqueueAnswer(true);
        bool clean = await _clone.WriteAsync(CloneSwapTests.Rows());

        Assert.True(clean, string.Join(" | ", _clone.Summary));
        Assert.Equal(CloneState.Done, _clone.State);

        // The summary is EXACTLY ONE NOTICE, and nothing else.
        //
        // D14 (2026-08-30, owner): the SIX MARK/SPACE notices this pin used to
        // count are GONE. Six of the demo's seven presets are not `fsk-v`, so
        // their tones are invisible to every read the campaign is ALLOWED to
        // make (R3 forbids the type flip) — and a warning about something the
        // read was never permitted to capture is not news about this write:
        // "we aren't capturing it on read, so we shouldn't whine about it on
        // write". The write BEHAVIOR is unchanged (tones are still written only
        // where the file carries them); only the line is gone, so the count
        // fell from eight to two.
        //
        // D15 (2026-08-30, owner) TOOK THE SECOND. D4's elision notice used to
        // ride out of the VERIFY's own read leg and into this write report; the
        // line is deleted, and what replaced it — the stored inventory — is
        // built at the STANDALONE READ'S CLOSE-OUT (`ReadAsync`), which the
        // verify never enters: it drives `RunReadCampaignAsync` directly. So a
        // write reports the write, and the count falls from two to one.
        //
        // D23 (owner 2026-08-30) TOOK THE LAST ONE TOO: the closing restore
        // still runs (the state assertions above prove it) but claims no line
        // in the WRITE summary. A flawless round trip now reports NOTHING.
        Assert.Empty(_clone.Summary);
        // THE D14 ABSENCE PIN: not one preset line survives, on a run that used
        // to produce six of them. It doubles as D15's NO-POLLUTION pin — the
        // inventory's own `10 modem preset(s)` row would trip it.
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("mark and space", StringComparison.Ordinal));
        Assert.DoesNotContain(_clone.Summary, l => l.Contains("preset", StringComparison.Ordinal));
        // D15's NO-POLLUTION pin proper: not one inventory row reaches a write
        // report, so the verify cannot emit the inventory a second time and the
        // rows can never be counted as write problems.
        Assert.DoesNotContain(_clone.Summary,
            l => l.EndsWith("(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("factory default", StringComparison.Ordinal));
        // D9 category B: the status line carries the VERDICT only — and D23
        // (owner 2026-08-30) took the last notice (the restore line), so a
        // flawless round trip finally says the clean verdict.
        Assert.Equal("Write complete.", _clone.StatusText);
    }

    /// <summary>
    /// <b>THE DECISIVE INVARIANT (audit round 3 — the pin whose absence let the
    /// breaker trip).</b> Over EVERY manifest row: a value the door ADMITS can
    /// never produce a verify diff BY SPELLING ALONE.
    ///
    /// <para>Two halves, and they meet in the middle. (a) What the READ
    /// campaign writes is what the door admits — so an honest file is never
    /// refused. (b) What the door admits is byte-identical to what a
    /// write-then-read-back produces — so an admitted file never reads back as
    /// a difference it did not ask for. Together they say the canonical form
    /// really IS the radio's storage form, which is the claim the whole rule
    /// rests on and the one thing no unit test of the parser could establish:
    /// only the radio can say how it stores a value.</para>
    ///
    /// <para>This is also what checks the FORMATTERS. Where a canonical
    /// spelling had to be inferred — the payload rows' letter case — this is
    /// the pin that would fail if the inference were wrong.</para>
    /// </summary>
    [Fact]
    public async Task EverySettingTheDoorAdmits_ReadsBackByteIdentical_SoSpellingCanNeverBeADiff()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var beforeWrite = _clone.File!.Settings
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

        // ANTI-VACUITY: every row really is present, so "all of them agree" is
        // a statement about 29 values and not about an empty set.
        Assert.Equal(CloneSettingsManifest.Rows.Count, beforeWrite.Count);

        // (a) THE READ'S OWN OUTPUT IS CANONICAL. Every value the campaign
        // wrote into the file passes the door unchanged — an honest file, from
        // this app's own read, can never be refused for its spelling.
        foreach (var (key, value) in beforeWrite)
            CloneSettingsManifest.CheckStoredValue(key, value);

        // (b) …AND SURVIVES A ROUND TRIP BYTE FOR BYTE.
        _prompt.EnqueueAnswer(true);
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var probe = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        { SentinelTimeoutMs = 5_000 };
        Assert.True(await probe.ReadAsync(), string.Join(" | ", probe.Summary));

        foreach (var after in probe.File!.Settings)
            Assert.Equal(beforeWrite[after.Key], after.Value);

        // …and the verify agrees, which is the operator-facing consequence:
        // no Setting line in the diff, for any row, for any spelling reason.
        Assert.DoesNotContain(
            CloneCompare.Diff(_clone.File!, probe.File!),
            d => d.StartsWith("Setting ", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>PIN 1, SHARPENED (audit round 3 verification, MAJOR).</b> The version
    /// above round-trips the demo's OWN baseline values, which every parser
    /// admits whether or not the canonical check exists — so it could not tell
    /// a working door from a deleted one. This one drives values that DIFFER
    /// from the baseline, and pairs each with the noncanonical spelling the
    /// door must refuse.
    ///
    /// <para><b>MUTATION-CHECKED:</b> with the canonical-equality check removed
    /// from <c>CheckStoredValue</c>, this test FAILS — the refusal half stops
    /// refusing. That is the sensitivity the previous version lacked.</para>
    /// </summary>
    [Fact]
    public async Task ChangedSettings_RoundTripByteIdentical_AndTheirNoncanonicalTwinsAreRefused()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // A DIFFERENT canonical value per parser family — enum name, payload
        // token, plain int, padded int, signed int, discrete set — every one of
        // them unlike what the demo currently holds, so the write really moves
        // the radio and the read really has something new to bring back.
        (string Key, string Canonical, string Noncanonical)[] changes =
        [
            ("PowerLevel", "Low", "LOW"),
            ("DigitalVoice", "On", "1"),              // "1" is a NUMERIC ALIAS for Off
            ("Beep", "Off", "off"),
            ("FrequencyStep", "TenKHz", "tenkhz"),
            ("Antenna", "BNC", "bnc"),
            ("FmSquelchType", "NOISE", "noise"),
            ("FmDeviation", "5.0", " 5.0 "),
            ("PrePostFilter", "DISABLE", "DISABLED"),
            ("PrePostScanRate", "FAST", "Fast"),
            ("BfoOffset", "+1000", "1000"),
            ("CwOffset", "1000", "+1000"),
            ("RfGain", "50", "+050"),
            ("Contrast", "8", "08"),
            ("AleAllCall", "Off", "OFF"),
            ("AleMaxScanChannels", "50", "+50"),
            ("AleLinkTimeout", "30", "030"),
            ("AleTuneTime", "45", "045"),
        ];

        var file = _clone.File!;
        foreach (var (key, canonical, _) in changes)
        {
            var row = Assert.Single(file.Settings, s => s.Key == key);
            Assert.NotEqual(canonical, row.Value);          // it really is a CHANGE
            row.Value = canonical;
        }
        _clone.Adopt(file);

        _prompt.EnqueueAnswer(true);
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var probe = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        { SentinelTimeoutMs = 5_000 };
        Assert.True(await probe.ReadAsync(), string.Join(" | ", probe.Summary));

        // BYTE-IDENTICAL: what the door admitted is what the radio reports.
        foreach (var (key, canonical, _) in changes)
            Assert.Equal(canonical, Assert.Single(probe.File!.Settings, s => s.Key == key).Value);
        Assert.DoesNotContain(
            CloneCompare.Diff(file, probe.File!),
            d => d.StartsWith("Setting ", StringComparison.Ordinal));

        // THE SENSITIVITY: each of those values has a spelling the radio would
        // normalize away, and the door refuses every one. Delete the canonical
        // check and this is the half that stops holding.
        foreach (var (key, _, noncanonical) in changes)
            Assert.Throws<CloneValueException>(
                () => CloneSettingsManifest.CheckStoredValue(key, noncanonical));
    }

    /// <summary>
    /// F5 (plan-clone-field-round2.md, decision D3) — a channel storing the
    /// dump's <c>FA</c> is WRITTEN, not refused.
    ///
    /// <para>The source radio's CH 09 stores AGC <c>FA</c>
    /// (<c>falcon-clone-20260821-165147.falconclone.json</c>), and the field
    /// write summary of 2026-08-21 carried an AGC refusal. The campaign had its
    /// OWN two-value map — <c>SL</c> and <c>ME</c> — falling through to the
    /// FULL-spelling parser for everything else, so <c>FA</c> matched nothing
    /// and the channel's AGC was reported as a value the radio does not accept.
    /// The map is now <see cref="Wire.ParseDumpAgc"/>'s, once.</para>
    ///
    /// <para>The pin stops at the SEND, deliberately: the demo stores an
    /// uncaptured AGC spelling VERBATIM rather than abbreviating it by guesswork
    /// (<c>DemoSerialPort.AgcDump</c>), so a full round trip of <c>FA</c> would
    /// be measuring the demo's refusal to invent a dump form, not the app. What
    /// this round fixed is exactly the refusal — and that it is gone, and that
    /// the value really reached the wire on the right channel, is the whole
    /// claim.</para>
    /// </summary>
    [Fact]
    public async Task AChannelStoringTheDumpsFastAgc_IsWritten_NotRefused()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        // D4: slot 5 sat at the factory default, so the elided file does not
        // carry it. The row is ADDED — which is also the honest shape for this
        // pin, because a channel storing `FA` is by definition not a default
        // one and only a non-default row is ever written.
        file.Channels.Add(new CloneChannel
        {
            Number = 5, RxFrequency = "09000000", TxFrequency = "09000000",
            Mode = "USB", Agc = "FA", Bandwidth = "2.7", RxOnly = "NO",
        });
        file.Channels = [.. file.Channels.OrderBy(c => c.Number)];
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        await _clone.WriteAsync(CloneSwapTests.Rows());

        // The field summary's line is GONE — on this or any other channel.
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("AGC", StringComparison.Ordinal)
                && l.Contains("not one this radio accepts", StringComparison.Ordinal));

        // …and the value reached the wire, on channel 05, in the SET vocabulary.
        var sent = _port.Sent.ToList();
        int select = sent.IndexOf("CH 5");
        Assert.True(select >= 0, "channel 05 was never selected for programming");
        int nextChannel = sent.FindIndex(select + 1, l => l.StartsWith("CH ", StringComparison.Ordinal));
        int agc = sent.FindIndex(select, l => l == "AG FAST");
        Assert.True(agc > select && (nextChannel < 0 || agc < nextChannel),
            "AG FAST did not go out while channel 05 was the selected one");
    }

    /// <summary>
    /// The other half of the R3 carry rule: a preset whose row DOES carry the
    /// tones has them WRITTEN, and gets no notice.
    /// </summary>
    [Fact]
    public async Task AnFskVPresetsTones_AreWritten_AndOnlyTheInvisibleOnesAreReported()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var write = Assert.Single(_port.Sent,
            l => l.StartsWith("MODEM PRESET 5 ", StringComparison.Ordinal));
        Assert.Contains("MARK 1500 SPACE 1700", write, StringComparison.Ordinal);
        // …and every OTHER preset write carries neither token, because the file
        // does not carry them: nothing is invented.
        foreach (var other in _port.Sent.Where(l =>
            l.StartsWith("MODEM PRESET ", StringComparison.Ordinal) && l != write))
            Assert.DoesNotContain("MARK", other, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TARGET-ONLY-SURVIVOR RULE, DELETED — the round-11 residual this
    /// round closes, pinned in its RETIRED direction.
    ///
    /// <para>It used to be that a channel the file did not carry could not be
    /// removed: there is no channel-delete verb, and the demo's dump omitted
    /// unprogrammed slots, so the honest thing was to report the survivor as a
    /// verify diff. Two facts killed it together — the radio answers a DEFAULT
    /// ROW for every slot (so the file carries all 100), and the campaign
    /// zeroizes first (owner statement §1, so nothing survives at all). The
    /// same scenario now verifies CLEAN.</para>
    /// </summary>
    [Fact]
    public async Task AChannelProgrammedAfterTheRead_IsUndoneByTheWipe_NotReportedAsASurvivor()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync());

        // Program a channel the source radio held at its DEFAULT row, the only
        // way a radio can: select it and set a value live.
        var channels = new ChannelSurface(_radio);
        var ssb = new SsbSurface(_radio);
        channels.SelectForStore(7);
        ssb.SetRxFrequency("09000000");
        await Task.Delay(50);

        _prompt.EnqueueAnswer(true);
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        // No survivor line — and the slot really is back at the file's value,
        // so this is "the write undid it", not "the compare stopped looking".
        // Under D4 the file's value for slot 7 is EXPRESSED BY ABSENCE (it is
        // the factory default), so the proof is that the verify read found
        // nothing to store there and the elided marker is set — an absent row
        // that the radio still held at 09000000 would have been a diff.
        Assert.DoesNotContain(_clone.Summary, l => l.Contains("SSB channel 07", StringComparison.Ordinal));
        Assert.True(_clone.File!.DefaultChannelsElided);
        Assert.DoesNotContain(_clone.File.Channels, c => c.Number == 7);
    }

    /// <summary>
    /// THE HEADLESS-BOOK PREFLIGHT (plan §3). Deleting the primary self ORPHANS
    /// its individuals — invisible to bulk AND targeted reads, still named in
    /// MEMBER lines — so a book read that finds no self while other rows name
    /// one is SILENTLY INCOMPLETE. It is FAULTED rather than serialized as
    /// complete, and a faulted domain can never be written.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeadlessBooks))]
    public void ABookWithNoSelfButLiveReferences_IsHeadless_AndABlankOneIsNot(
        Action<CloneFile> shape, bool headless)
    {
        var file = CloneFileTests.Complete();
        shape(file);
        Assert.Equal(headless, CloneService.IsHeadlessBook(file));
    }

    public static TheoryData<Action<CloneFile>, bool> HeadlessBooks() => new()
    {
        // A POPULATED book is not headless — anti-vacuity for the whole rule.
        { _ => { }, false },
        // A GENUINELY BLANK book is not headless either, and this is the case
        // the rule must not fire on: it is exactly what a post-wipe read finds,
        // and faulting it would make the verify leg unable to complete.
        {
            f =>
            {
                f.Selfs.Clear();
                f.Individuals.Clear();
                f.Nets.Clear();
                f.Schedules.Clear();
            },
            false
        },
        // No self, but an individual that must hang off one.
        { f => { f.Selfs.Clear(); f.Nets.Clear(); f.Schedules.Clear(); }, true },
        // No self, but a net still lists members.
        { f => { f.Selfs.Clear(); f.Individuals.Clear(); f.Schedules.Clear(); }, true },
        // No self, but a schedule still targets an address.
        {
            f =>
            {
                f.Selfs.Clear();
                f.Individuals.Clear();
                f.Nets.Clear();
                f.Schedules.Add(new CloneSchedule
                { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });
            },
            true
        },
    };

    [Fact]
    public async Task AFaultedBook_CanNeverBeWritten_WhateverFaultedIt()
    {
        // The consequence half: the headless rule's whole value is that a
        // domain it faults cannot become destructive loss on the target.
        ConnectReady();
        Assert.True(await _clone.ReadAsync());

        var file = _clone.File!;
        file.BookState = CloneDomainState.Faulted;
        _clone.Adopt(file);

        Assert.Contains("address book", _clone.WriteBlockedReason!, StringComparison.Ordinal);
        _prompt.EnqueueAnswer(false);
        _port.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
    }

    // ---- Gating --------------------------------------------------------------

    [Fact]
    public void BothCampaigns_AreBlockedWhileDisconnected_WithAReason()
    {
        Assert.Equal("Not connected.", _clone.ReadBlockedReason);
        Assert.Equal("Not connected.", _clone.WriteBlockedReason);
    }

    [Fact]
    public void WriteIsBlockedWithNoFile_AndReadIsNot()
    {
        ConnectReady();
        Assert.Null(_clone.ReadBlockedReason);
        Assert.Equal("No clone file loaded.", _clone.WriteBlockedReason);
    }

    /// <summary>
    /// The commands the WRITE campaign sent, with the VERIFY read campaign that
    /// follows it cut off.
    ///
    /// <para>The boundary is <c>DI</c>: the channel DUMP is a read the write
    /// campaign never issues (it programs channels by selecting and setting
    /// them live), and the verify is the only read campaign inside a
    /// <c>WriteAsync</c>. Without the cut, every "…and nothing after this"
    /// assertion below would be reading the verify's own traffic — which is how
    /// a leg-order pin quietly stops meaning anything.</para>
    /// </summary>
    private static IReadOnlyList<string> WritePortion(IReadOnlyList<string> sent)
    {
        int verify = IndexOf(sent, l => l.StartsWith("DI ", StringComparison.Ordinal));
        Assert.True(verify > 0, "the verify read never started, so the write portion cannot be delimited");
        return [.. sent.Take(verify)];
    }

    /// <summary>The repo root, for the source-shape pins (D20's two call sites).
    /// A private copy, like this file's other per-class helpers — the classes in
    /// here do not share a base.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }

    private static bool IsLockoutSet(string line) =>
        line.StartsWith("PROGRAM ", StringComparison.Ordinal)
        || line.StartsWith("SELECT ", StringComparison.Ordinal);

    private static int IndexOf(IReadOnlyList<string> lines, Func<string, bool> match)
    {
        for (int i = 0; i < lines.Count; i++) if (match(lines[i])) return i;
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<string> lines, Func<string, bool> match)
    {
        for (int i = lines.Count - 1; i >= 0; i--) if (match(lines[i])) return i;
        return -1;
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// ROUND 14, PHASE F — the campaign hardening the owner's first live clone
/// forced (plan-round14.md §4 Phase F; ruling R13).
///
/// <para>Same real stack as <see cref="CloneServiceTests"/>, plus one
/// interception at the byte seam: <see cref="DeferredModeEntryPort"/>, which
/// reproduces the ONE thing the demo and the dummy-load bench cannot — a radio
/// that accepts a mode command, answers everything queued behind it AT THE OLD
/// PROMPT, and only reaches the new prompt when its entry lifecycle is
/// done.</para>
/// </summary>
public sealed class CloneRound14FieldHardeningTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly DeferredModeEntryPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly FakeConfirmationPrompt _prompt = new();
    private readonly CloneService _clone;

    public CloneRound14FieldHardeningTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new DeferredModeEntryPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _clone = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
        };
        // The transport's write gate, compressed. It is the reason the field's
        // sentinel reached the wire at all: the entry lifecycle carries no
        // prompt, so the gate holds the next write only until it times out and
        // then sends anyway (SerialTransport "the rule"). 2 s of real waiting
        // per case buys nothing here; the ORDER is the whole fixture.
        _transport.GateTimeoutMs = 150;
    }

    /// <summary>The live rig's HOP entry, verbatim line inventory from
    /// <c>bench/transcripts/field-clone-console-20260820-1738.txt</c>
    /// (17:39:33.033–:37.927): two full generate cycles, each ending
    /// <c>TUNE FAULT</c>, and NOT ONE PROMPT among them.</summary>
    private const string HopEntryLifecycle =
        "\r\nWait...\r\nGenerating Hopset...\r\n TUNING COUPLER \r\n   TUNE FAULT   "
        + "\r\nWait...\r\nGenerating Hopset...\r\n TUNING COUPLER \r\n   TUNE FAULT   \r\n";

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    // ---- F1: the HOP-entry mode gate -----------------------------------------

    /// <summary>
    /// THE CONVICTION TEST (F1). It reproduces the T2 field failure and names
    /// the mechanism: <b>the leg sentinel raced the mode confirmation and won</b>.
    ///
    /// <para>Field evidence
    /// (<c>bench/transcripts/field-clone-console-20260820-1738.txt</c>): the
    /// campaign sent <c>HO</c> and the sentinel <c>BAT ST</c> queued 1 ms
    /// behind it; the radio answered the battery query FIRST — at 17:38:05.590
    /// in attempt 1 and 17:39:32.811 in attempt 2 — and only reached
    /// <c>HOP&gt;</c> at :08.522 and :38.806 respectively, after two
    /// generate/TUNE-FAULT cycles. The old gate read the confirmed mode when
    /// the sentinel answered, found <c>Ale</c>, and aborted with "did not
    /// confirm the HOP prompt". Nothing else was ever sent — which is exactly
    /// what the owner saw and reported.</para>
    ///
    /// <para>The parser is NOT implicated: the same stream is replayed
    /// byte-faithfully in
    /// <c>FramerParserIntegrationTests.R14FieldClone_Attempt2_…</c> and
    /// confirms <c>Hop</c>. The defect is <c>CloneService.AtPromptAsync</c>'s
    /// single-sentinel gate, and the fix is to wait for the MODE.</para>
    /// </summary>
    [Fact]
    public async Task TheHopLeg_WhenTheSentinelIsAnsweredBeforeTheModePrompt_WaitsForThePrompt_AndReadsTheHopNets()
    {
        ConnectReady();
        // The live rig's shape: the switch is accepted, and the new prompt
        // only arrives when the entry lifecycle (generate → tune, twice) ends.
        _port.Defer("HO", lifecycleMs: 600, lifecycleLines: HopEntryLifecycle);

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // ANTI-VACUITY: the deferral really happened — the mode command was
        // held until after a battery answer had already gone up.
        Assert.True(_port.Released, "the HO was never deferred, so nothing was reproduced");

        // The message the field failure produced must NOT be in this summary…
        Assert.DoesNotContain(_clone.Summary,
            s => s.Contains("did not confirm the HOP prompt", StringComparison.Ordinal));
        // …and the HOP legs really ran.
        Assert.Equal(10, _clone.File!.HopNets.Count);
        Assert.Empty(_clone.File.IncompleteDomains);
    }

    /// <summary>The wait is bounded by the RADIO'S OWN mode-change budget, not
    /// by the leg sentinel's. A radio that accepts the switch and never reaches
    /// the prompt aborts — after the full budget, with the honest message.</summary>
    [Fact]
    public async Task TheModeGate_WhenTheModeNeverConfirms_AbortsOnTheRadiosOwnBudget_WithTheHonestMessage()
    {
        ConnectReady();
        _radio.ModeChangeTimeoutMs = 300;        // the budget under test, shortened
        _port.Defer("HO", lifecycleMs: null, lifecycleLines: HopEntryLifecycle);

        Assert.False(await _clone.ReadAsync());

        Assert.Contains(_clone.Summary,
            s => s == "HOP nets: the radio did not confirm the HOP prompt, so this operation stopped here.");
        // Measured from the SWITCH, not from the campaign: the budget really
        // was waited out. An abort at the sentinel returns in a fraction of it
        // (the write gate opens after 150 ms and the demo answers at once).
        long waited = Environment.TickCount64 - _port.HeldAtTicks;
        Assert.True(waited >= _radio.ModeChangeTimeoutMs,
            $"the gate gave up {waited} ms after the switch, inside its {_radio.ModeChangeTimeoutMs} ms budget");
    }

    /// <summary>
    /// …and the wait is NOT the sentinel's budget (the F1 fix's whole point).
    /// The sentinel is given 400 ms here while the mode prompt is withheld for
    /// 900 ms past the battery answer: the old gate could not have survived
    /// that at ANY sentinel budget, because it read the mode the moment the
    /// sentinel answered.
    /// </summary>
    /// <summary>
    /// THE CLOSING SENTINEL IS A BARRIER, AND IT IS JUDGED (audit round 1,
    /// MAJOR). The mode confirms — the radio really did reach <c>HOP&gt;</c> —
    /// and then it stops answering. The leg's first command must NOT go out
    /// into that silence: the gate stops, on the honest stopped-answering line
    /// the sentinel itself writes.
    ///
    /// <para>This is the pin that was missing: with the sentinel's Boolean
    /// discarded, DELETING the sentinel outright changed nothing anywhere in
    /// the suite. Delete it now and this test goes red.</para>
    /// </summary>
    [Fact]
    public async Task TheModeGate_WhenTheRadioStopsAnsweringAfterConfirmingThePrompt_StopsAtTheBarrier()
    {
        ConnectReady();
        _clone.SentinelTimeoutMs = 300;
        _port.Defer("HO", lifecycleMs: 200, lifecycleLines: HopEntryLifecycle,
            swallowAfterRelease: "BAT ST");

        Assert.False(await _clone.ReadAsync());

        // THE CONTRACT: the leg stopped at the barrier, and said why.
        Assert.Contains(_clone.Summary,
            s => s == "HOP nets: the radio stopped answering during this step.");
        // …and the leg's own first command never went out behind the silence.
        Assert.DoesNotContain("DIS", _recorder.Sent);

        // ANTI-VACUITY: the mode gate got PAST the confirmation — this is not
        // the prompt-never-arrived case in disguise — and the sentinel really
        // was the thing that went unanswered.
        Assert.DoesNotContain(_clone.Summary,
            s => s.Contains("did not confirm the HOP prompt", StringComparison.Ordinal));
        Assert.True(_port.Released, "the mode command was never let through");
        Assert.True(_port.Swallowed, "no sentinel was swallowed, so nothing was tested");
    }

    [Fact]
    public async Task TheModeGate_OutlastsTheSentinelBudget()
    {
        ConnectReady();
        _clone.SentinelTimeoutMs = 300;
        _port.Defer("HO", lifecycleMs: 900, lifecycleLines: HopEntryLifecycle);

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        Assert.True(_port.Released);
        Assert.Empty(_clone.File!.IncompleteDomains);
    }

    // ---- F2: the ALE scan stop, RE-TARGETED to D8's funnel site --------------
    //
    // plan-clone-write-structural.md §6 pin (g) DISPOSITIONS. The two pins that
    // D8 deliberately INVERTS are DELETED and replaced by their opposites in
    // CloneScanDoctrineTests:
    //   * TheAleWriteLegs_SendNoStop_WhenTheRadioSaidItWasNotScanning
    //     → D8 pin (d), TheOccupancyStop_GoesOutUnconditionally_EvenWhenTheMirrorSaysLinked
    //   * TheReadCampaign_NeverStopsTheScan_EvenWhileScanning
    //     → D8 pin (a), TheReadCampaign_StopsTheScan_BeforeTheBookLegsFirstRead
    // The two POSITIVE stop-before-the-first-ALE-write pins below survive and
    // simply re-target the funnel: the stop is no longer issued by a leg-5 call
    // but by AtPromptAsync's ALE occupancy check, and both pins still assert the
    // property that mattered — no ALE write goes out behind a running scan. The
    // restart half of the second pin now reads the D8 matrix: the write's final
    // mode is the FILE's, and this file's is SSB, so no `SCA` is attempted.

    /// <summary>
    /// The FOUND-UNCONFIRMED branch: the radio never announced a link state, so
    /// the campaign RESTORES NOTHING — no <c>SCA</c>, and no summary claim about
    /// a scan it never observed. The STOP still goes out (D8 made it
    /// unconditional), now from the mode funnel.
    /// </summary>
    [Fact]
    public async Task TheAleWriteLegs_StopTheScanBeforeTheFirstWrite_AndRestoreNothingOnAGuess()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        Assert.False(new AleSurface(_radio).LinkState.IsConfirmed,
            "the demo announced a link state, so this is no longer the unconfirmed branch");

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = WritePortion(_recorder.Sent);
        int stop = IndexOf(sent, l => l == "ST");
        int firstAleWrite = IndexOf(sent, l => l.StartsWith("TXMSG ", StringComparison.Ordinal));
        Assert.True(stop >= 0, "no scan stop was sent");
        Assert.True(firstAleWrite > stop, "the scan stop did not precede the first ALE write");

        Assert.DoesNotContain("SCA", sent);
        Assert.DoesNotContain(CloneService.ScanStoppedNotice, _clone.Summary);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// The FOUND-SCANNING branch: the radio announced <c>SCANNING</c>, so the
    /// campaign stops the scan before the first ALE write and SAYS SO ONCE —
    /// prose, no wire tokens (§3.2).
    ///
    /// <para>The found state is read BEFORE leg 2. It has to be: the zeroize
    /// boundary resets every mirror, so by the ALE write leg there is no link
    /// state left to read. That read costs NO WIRE, which is what lets `ZERO`
    /// stay the write's first command (D8's pre-zeroize exemption).</para>
    ///
    /// <para><b>RE-TARGETED by D8, restart half.</b> The restart is no longer
    /// fired after the channel-groups leg — it moved to the closing-restore
    /// funnel, and it is attempted only when the FINAL mode is confirmed ALE.
    /// This file's operating mode is SSB, so the campaign deliberately makes no
    /// attempt and claims nothing: the operator asked for the FILE's state, and
    /// a non-ALE final mode has no scan to run (§5.4c matrix, per-campaign
    /// row). The found-Scanning-with-an-ALE-file case is pinned in
    /// CloneScanDoctrineTests.</para>
    /// </summary>
    [Fact]
    public async Task TheAleWriteLegs_StopTheScan_AndClaimNoRestart_WhenTheFileEndsOutsideAle()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        await AnnounceScanningAsync();
        // ANTI-VACUITY: the file this write is about to replay ends OUTSIDE
        // ALE, which is the whole reason no restart may be attempted.
        Assert.Equal("Ssb", _clone.File!.OperatingMode);

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = WritePortion(_recorder.Sent);
        int stop = IndexOf(sent, l => l == "ST");
        int firstAleWrite = IndexOf(sent, l => l.StartsWith("TXMSG ", StringComparison.Ordinal));

        Assert.True(stop >= 0 && firstAleWrite > stop, "the stop did not precede the first ALE write");
        // The whole campaign, verify and restore included — not just the write
        // portion — makes no restart attempt.
        Assert.DoesNotContain("SCA", _recorder.Sent);

        Assert.Single(_clone.Summary, s => s == CloneService.ScanStoppedNotice);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    internal Task AnnounceScanningAsync() => AnnounceLinkStateAsync("SCANNING", AleLinkState.Scanning);

    /// <summary>Put an ANNOUNCED-only fact into the mirror. The line is bare —
    /// no prompt prefix — precisely so it moves the link state and nothing
    /// else; the demo models no scan of its own.</summary>
    private async Task AnnounceLinkStateAsync(string line, AleLinkState expected)
    {
        var ale = new AleSurface(_radio);
        _port.Inject("\r\n" + line + "\r\n");
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(ale.LinkState.IsConfirmed && ale.LinkState.Value == expected))
            await Task.Delay(5);
        Assert.True(ale.LinkState.IsConfirmed && ale.LinkState.Value == expected,
            "the announced link state never reached the mirror");
    }

    private static IReadOnlyList<string> WritePortion(IReadOnlyList<string> sent)
    {
        int verify = IndexOf(sent, l => l.StartsWith("DI ", StringComparison.Ordinal));
        Assert.True(verify > 0, "the verify read never started, so the write portion cannot be delimited");
        return [.. sent.Take(verify)];
    }

    private static int IndexOf(IReadOnlyList<string> lines, Func<string, bool> match)
    {
        for (int i = 0; i < lines.Count; i++) if (match(lines[i])) return i;
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<string> lines, Func<string, bool> match)
    {
        for (int i = lines.Count - 1; i >= 0; i--) if (match(lines[i])) return i;
        return -1;
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// ROUND 16 FIXES S4 — a TRUNCATED channel dump must not serialise as
/// <c>Read</c>.
///
/// <para>Same real stack as <see cref="CloneServiceTests"/>, plus one
/// interception at the byte seam: <see cref="TruncatingDemoPort"/> drops rows
/// out of the <c>DI 0 99</c> answer on the way UP. The sentinel behind the
/// dump is still answered — which is the whole point: an answered sentinel says
/// the radio is still talking, it does not say the dump was whole, and P17
/// measured heavy answers losing rows under load.</para>
/// </summary>
public sealed class CloneRound16TruncatedDumpTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly TruncatingDemoPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly FakeConfirmationPrompt _prompt = new();
    private readonly CloneService _clone;

    public CloneRound16TruncatedDumpTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new TruncatingDemoPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _clone = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
            // ROUND 17 F6: the leg now WAITS for the dump before judging it, and
            // a dump that stops never grows again — so these two runs would
            // otherwise sleep out the shipped four-second quiet window apiece.
            // The verdict below is unchanged; only the waiting is shortened.
            ChannelDumpPollMs = 25,
            ChannelDumpQuietMs = 300,
        };
    }

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>
    /// THE CONVICTION TEST. Half the dump never arrives; the sentinel behind it
    /// is answered normally. Before S4 the leg read
    /// <c>file.ChannelState = Mark(answered)</c> and the campaign serialised
    /// FIFTY channels as a complete <c>Read</c> — a file that would later be
    /// written into a radio, filling it from a half inventory.
    ///
    /// <para><c>DI 0 99</c> prints EVERY slot, a never-written one included
    /// (protocol.md; P17 record 6 carries exactly 100 <c>CH nn RxFr</c> rows),
    /// so the REPORTED SET is the proof and a short dump is a fault.</para>
    /// </summary>
    [Fact]
    public async Task ATruncatedChannelDump_IsFaulted_AndSaysHowManySlotsCameBack()
    {
        ConnectReady();
        _port.DropChannelRowsFrom = 50;

        Assert.False(await _clone.ReadAsync());

        // The fixture really did what it is named for.
        Assert.Equal(50, _port.DroppedChannelRows);

        var file = _clone.File!;
        Assert.Equal(CloneDomainState.Faulted, file.ChannelState);
        Assert.Empty(file.Channels);
        Assert.Contains("SSB channels", file.IncompleteDomains);

        // The summary says what actually happened — and NOT the generic
        // close-out sentence, which would be false here: the radio answered
        // every sentinel it was asked.
        Assert.Contains(
            "SSB channels: the radio reported 50 of 100 slots, so this domain is incomplete.",
            _clone.Summary);
        Assert.DoesNotContain(
            "SSB channels: the radio stopped answering, so this domain is incomplete.",
            _clone.Summary);

        // …and SSB channels is the ONLY gap: every other leg read fine over the
        // same connection, so this is not "everything faulted". The STATUS LINE
        // carries the verdict only (D9 category B) — the gap is named in the
        // summary line pinned above, which is where the operator reads it.
        Assert.Equal("Read incomplete.", _clone.StatusText);
        Assert.DoesNotContain("SSB channels", _clone.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONSEQUENCE, and why S4 is worth a fault: the truncated file can
    /// never be written. Adopted for a WRITE it fails the preflight by name,
    /// asks the operator nothing, and puts not one line on the wire.
    /// </summary>
    [Fact]
    public async Task TheTruncatedFile_CanNeverBeWritten_AndAsksNothing()
    {
        ConnectReady();
        _port.DropChannelRowsFrom = 50;
        Assert.False(await _clone.ReadAsync());

        var file = _clone.File!;
        Assert.Contains("SSB channels", file.IncompleteDomains);

        _clone.Adopt(file);
        Assert.Contains("SSB channels", _clone.WriteBlockedReason!, StringComparison.Ordinal);

        // A queued CANCEL that must never be consumed: if the preflight ever
        // stops refusing, this fails on CallCount instead of hanging on a
        // prompt nobody answers (the house idiom).
        _prompt.EnqueueAnswer(false);
        _recorder.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_recorder.Sent);
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// ROUND 17 F6 — THE DUMP-COMPLETION BARRIER.
///
/// <para>Same real stack as <see cref="CloneServiceTests"/>, plus the two
/// byte-seam wrappers this fix is judged by: <see cref="InterleavingDemoPort"/>
/// replays the CAPTURED ordering (the sentinel answering after row 28 and the
/// dump resuming to 100), and <see cref="TruncatingDemoPort"/> supplies the
/// other case the barrier must NOT paper over — a stream that really stops at
/// 28 and never resumes.</para>
///
/// <para>The two are the same on the wire for the first 2.2 s, which is the
/// whole difficulty: round 16's S4 check could not tell them apart because it
/// looked at the moment the sentinel answered. It now looks after the leg has
/// waited, and the answers differ.</para>
/// </summary>
public sealed class CloneRound17DumpCompletionTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly TruncatingDemoPort _truncator;
    private readonly InterleavingDemoPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly FakeConfirmationPrompt _prompt = new();
    private readonly CloneService _clone;

    public CloneRound17DumpCompletionTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _truncator = new TruncatingDemoPort(_recorder);
        _port = new InterleavingDemoPort(_truncator);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _clone = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
            // The barrier's bounds are HOOKS for exactly this reason: a test
            // must be able to say WHICH of them fired without sleeping out the
            // shipped four seconds (the AmdVerifyTimeoutMs idiom).
            ChannelDumpPollMs = 25,
        };
    }

    /// <summary>The file's ASYNC readiness pump (the idiom at
    /// <c>AwaitModeAsync</c>'s and <c>AwaitLinkStateAsync</c>'s call sites): it
    /// YIELDS rather than blocking a pool thread, which matters here because
    /// every test in this class is async and one of them drives the barrier's
    /// own poll.</summary>
    private async Task ConnectReadyAsync()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready)
            await Task.Delay(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>
    /// THE CONVICTION TEST. The captured ordering, replayed: 28 rows, the
    /// sentinel's own answer BETWEEN two of them, then the remaining 72 and the
    /// trailer. Before this round the leg judged the dump at the sentinel and
    /// found 28 slots, so the domain FAULTED and the operator's file carried no
    /// channels at all — on a radio that had answered every row.
    /// </summary>
    [Fact]
    public async Task TheCapturedInterleave_StillProducesTheWhole100SlotInventory()
    {
        await ConnectReadyAsync();
        _port.SplitAfterRow = 28;          // `CH 00`..`CH 27`, then the battery answer
        _port.Trailer = "CHAN 00";         // the demo's operating channel, as `DI` trails it
        _clone.ChannelDumpQuietMs = 2_000;
        _clone.ChannelDumpTimeoutMs = 30_000;

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The fixture really did what it is named for: one dump split, 72 rows
        // held, and they went up BEHIND the sentinel's answer.
        Assert.Equal(1, _port.SplitDumps);
        Assert.Equal(72, _port.HeldRows);
        Assert.Equal(72, _port.ReleasedRows);
        Assert.True(_port.ReleasedAfterBattery);

        var file = _clone.File!;
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        // The BARRIER's claim is about the reported SET, which is still all 100
        // slots — D4 then elides the 98 default rows on the way into the file.
        //
        // D15 took the elision LINE that used to carry that proof, so the proof
        // is read off the MARKER instead, and it is the same proof: S4 grants
        // `Read` only to a domain whose reported set is exactly slots 0-99, so
        // the assertion above is already "the barrier saw all 100" — a dump that
        // had really stopped at 28 would be FAULTED here, which is the sibling
        // test below.
        Assert.Equal([1, 2], file.Channels.Select(c => c.Number));
        Assert.DoesNotContain("SSB channels", file.IncompleteDomains);
        // …and the campaign is otherwise the clean one: the closing restore's
        // notice and D15's stored inventory, and nothing else, so the barrier
        // bought the channels without costing anything.
        Assert.Equal(
            [
                "Left the radio on channel 00, net 0, SSB.",
                "2 channel(s)",
                "10 channel group(s)",
                "3 self(s)",
                "5 individual(s)",
                "3 net(s)",
                "2 message(s)",
                "2 schedule(s)",
                "10 HOP net(s)",
                "2 exclusion band(s)",
                "10 modem preset(s)",
                "30 setting(s)",
                "22 lockout(s)",
            ],
            _clone.Summary);
        Assert.Equal("Read complete.", _clone.StatusText);
    }

    /// <summary>
    /// THE OTHER HALF, and what stops the barrier from being a way of waiting
    /// until any dump looks whole: a stream that really stops at 28 is still
    /// FAULTED, with round 16's own sentence, once the QUIET WINDOW expires.
    ///
    /// <para>The window is what fires here and nothing else: the hard cap is
    /// set to thirty seconds, so a read that finished in a fraction of that
    /// cannot have been ended by it.</para>
    /// </summary>
    [Fact]
    public async Task ARealTruncationAt28_StillFaults_OnceTheQuietWindowExpires()
    {
        await ConnectReadyAsync();
        _truncator.DropChannelRowsFrom = 28;
        _clone.ChannelDumpQuietMs = 300;
        _clone.ChannelDumpTimeoutMs = 30_000;

        long started = Environment.TickCount64;
        Assert.False(await _clone.ReadAsync());
        long elapsed = Environment.TickCount64 - started;

        Assert.Equal(72, _truncator.DroppedChannelRows);

        var file = _clone.File!;
        Assert.Equal(CloneDomainState.Faulted, file.ChannelState);
        Assert.Empty(file.Channels);
        Assert.Contains("SSB channels", file.IncompleteDomains);
        // ROUND 16'S SENTENCE, UNCHANGED — the S4 check is still the judge.
        Assert.Contains(
            "SSB channels: the radio reported 28 of 100 slots, so this domain is incomplete.",
            _clone.Summary);
        Assert.DoesNotContain(
            "SSB channels: the radio stopped answering, so this domain is incomplete.",
            _clone.Summary);
        Assert.Equal("Read incomplete.", _clone.StatusText);
        // The QUIET WINDOW is what ended the wait: the 30 s cap is nowhere near.
        Assert.True(elapsed < 10_000, $"the read took {elapsed} ms");
    }

    /// <summary>
    /// D15 (2026-08-30, owner) — A FAULTED DOMAIN GETS NO INVENTORY ROW. The
    /// stored inventory answers "what WAS stored"; a domain the read could not
    /// finish has stored NOTHING it can stand behind, and it is already named,
    /// in its own sentence, by the close-out. A row beside that fault line would
    /// invite the operator to read a broken domain as a captured one.
    ///
    /// <para>The OTHER rows are still there, which is what makes this a
    /// discrimination rather than a report that gives up whole.</para>
    /// </summary>
    [Fact]
    public async Task AFaultedDomain_GetsNoInventoryRow_WhileTheOthersKeepTheirs()
    {
        await ConnectReadyAsync();
        _truncator.DropChannelRowsFrom = 28;
        _clone.ChannelDumpQuietMs = 300;
        _clone.ChannelDumpTimeoutMs = 30_000;

        Assert.False(await _clone.ReadAsync());

        // ANTI-VACUITY: the domain really faulted, and it really said so.
        Assert.Equal(CloneDomainState.Faulted, _clone.File!.ChannelState);
        Assert.Contains(
            "SSB channels: the radio reported 28 of 100 slots, so this domain is incomplete.",
            _clone.Summary);

        // No channel row — not even "0 channel(s)".
        Assert.DoesNotContain(_clone.Summary,
            l => l.EndsWith(" channel(s)", StringComparison.Ordinal));
        // …and every other domain still reports what it stored, so the
        // inventory survives a partial read rather than vanishing with it.
        Assert.Equal("10 channel group(s)", Assert.Single(_clone.Summary,
            l => l.EndsWith(" channel group(s)", StringComparison.Ordinal)));
        Assert.Equal("3 self(s)", Assert.Single(_clone.Summary,
            l => l.EndsWith(" self(s)", StringComparison.Ordinal)));
        Assert.Equal("22 lockout(s)", Assert.Single(_clone.Summary,
            l => l.EndsWith(" lockout(s)", StringComparison.Ordinal)));
    }

    /// <summary>
    /// THE HARD CAP, on its own. Rows dribble in one at a time, so the reported
    /// set never stops GROWING and the quiet window — ten seconds here, longer
    /// than the whole campaign — can never expire. Only the cap can end this
    /// wait, and the two outcomes are cleanly different: with the cap the domain
    /// FAULTS short, without it the drip finishes and the file carries all 100.
    /// </summary>
    [Fact]
    public async Task ARadioThatDripsRowsForever_IsEndedByTheHardCap_NotTheQuietWindow()
    {
        await ConnectReadyAsync();
        _port.SplitAfterRow = 28;
        _port.DripMs = 25;                 // 72 rows ⇒ ~1.8 s of steady growth
        _clone.ChannelDumpQuietMs = 10_000;
        _clone.ChannelDumpTimeoutMs = 400;

        Assert.False(await _clone.ReadAsync());

        Assert.Equal(1, _port.SplitDumps);
        Assert.True(_port.ReleasedAfterBattery);       // the drip really was live

        var file = _clone.File!;
        Assert.Equal(CloneDomainState.Faulted, file.ChannelState);
        Assert.Empty(file.Channels);
        Assert.Contains("SSB channels", file.IncompleteDomains);
        // It gave up MID-DRIP: more than the 28 it started with, fewer than the
        // 100 a completed dump would have shown.
        var reported = Assert.Single(_clone.Summary,
            l => l.StartsWith("SSB channels: the radio reported ", StringComparison.Ordinal));
        int slots = int.Parse(reported.Split(' ')[5], CultureInfo.InvariantCulture);
        Assert.InRange(slots, 28, 99);
    }

    /// <summary>
    /// THE CONTROLLED RACE (audit round 1, MAJOR). The rows are parsed on the
    /// PORT thread, so the set can grow between the barrier's snapshot of the
    /// mirror and its comparison of the clock against the quiet deadline. A
    /// barrier that decided expiry on the snapshot would abandon a dump that
    /// HAD resumed — the very failure it exists to prevent, one moment later.
    ///
    /// <para><b>Why a seam and not a sleep.</b> That gap is microseconds wide;
    /// a test that tried to land a row in it by timing would be racing, not
    /// pinning. So the growth is injected FROM INSIDE the gap, through
    /// <c>DumpPollObserved</c>, and the ordering is a fact rather than a hope:
    /// the poll at which it happens is chosen by a counter, and the rows go up
    /// synchronously on the campaign's own thread.</para>
    ///
    /// <para><b>The clock is taken out of it entirely.</b> A quiet window of
    /// −1 ms is ALREADY expired the instant the barrier computes its deadline,
    /// at every poll, so the expiry branch is reached on poll 1 and on every
    /// poll after it no matter what <c>TickCount64</c>'s ~15 ms granularity
    /// does. What the pin then measures is only WHICH READ that branch decides
    /// on — the stale snapshot from the top of the poll, or a fresh one.</para>
    ///
    /// <para><b>The injection is PARTIAL, and it has to be.</b> S4 re-reads the
    /// mirror after the barrier returns, so a growth all the way to 100 would
    /// leave the FILE identical whether the barrier noticed or not — the pin
    /// would prove nothing (the first draft of this test did exactly that and
    /// survived the mutant). Growing to 70 separates them: a barrier that
    /// re-reads finds growth, resets the deadline and polls again, so the last
    /// 30 rows are released and the file is whole; a barrier deciding on the
    /// stale 28 returns on the spot, never reaches the second injection, and
    /// leaves S4 looking at 70 of 100 — FAULTED.</para>
    /// </summary>
    [Fact]
    public async Task RowsArrivingINSIDETheExpiryDecision_AreSeen_AndTheBarrierRunsOnTo100()
    {
        await ConnectReadyAsync();
        _port.SplitAfterRow = 28;
        _port.ReleaseOnDemand = true;      // no clock of the fixture's own either
        _port.Trailer = "CHAN 00";
        _clone.ChannelDumpQuietMs = -1;    // expired at every poll, by construction
        _clone.ChannelDumpPollMs = 5;
        _clone.ChannelDumpTimeoutMs = 30_000;

        int polls = 0;
        _clone.DumpPollObserved = () =>
        {
            // Poll 1's gap grows the set to 70 — NOT to 100. Poll 2 is reached
            // ONLY by a barrier that saw that growth at its expiry decision,
            // and it is what releases the rest.
            if (++polls == 1) _port.ReleaseNow(42);
            else if (polls == 2) _port.ReleaseNow();
        };

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The injection really happened, and the barrier really carried on past
        // the poll it was injected in.
        Assert.True(polls >= 2, $"the barrier only polled {polls} time(s)");
        Assert.Equal(72, _port.ReleasedRows);
        Assert.True(_port.ReleasedAfterBattery);

        var file = _clone.File!;
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        // As above: the reported SET is all 100 (the `Read` marker says so —
        // S4 grants it to nothing shorter, and D15 retired the elision line
        // that used to say it a second way), and the file carries the two rows
        // that are not at factory default.
        Assert.Equal("2 channel(s)", Assert.Single(_clone.Summary,
            l => l.EndsWith(" channel(s)", StringComparison.Ordinal)));
        Assert.Equal([1, 2], file.Channels.Select(c => c.Number));
        Assert.DoesNotContain("SSB channels", file.IncompleteDomains);
    }

    /// <summary>
    /// THE RESTORE DOCTRINE IS UNTOUCHED (I-2): the barrier lives INSIDE the
    /// campaign flow, so even the run that gives up on the dump still leaves
    /// the radio where it found it, through the same one funnel.
    /// </summary>
    [Fact]
    public async Task GivingUpOnTheDump_StillRunsTheClosingRestore()
    {
        await ConnectReadyAsync();
        _truncator.DropChannelRowsFrom = 28;
        _clone.ChannelDumpQuietMs = 300;
        _clone.ChannelDumpTimeoutMs = 30_000;

        Assert.False(await _clone.ReadAsync());

        Assert.Contains("Left the radio on channel 00, net 0, SSB.", _clone.Summary);
    }

    // ---- The LOAD side (F6): the pre-fix file on disk ----------------------

    /// <summary>
    /// LOADING the file the old campaign wrote. The downgrade happens in
    /// <c>CloneFile.Load</c>; what the SERVICE owes is telling the operator —
    /// so the notice rides in the SUMMARY the clone page already shows and in
    /// the status line, rather than surfacing later as a Write button that has
    /// silently gone grey.
    /// </summary>
    [Fact]
    public async Task LoadingAPreFixFile_CarriesTheNotice_IntoTheSummaryAndTheStatusLine()
    {
        await ConnectReadyAsync();               // so the WRITE gate's answer is about the FILE
        var file = CloneFileTests.Complete();
        file.CapturedUtc = "2026-08-22T19:42:03.0000000Z";
        for (int n = 0; n < 28; n++) file.Channels.Add(new CloneChannel { Number = n });

        _clone.LoadJson(file.Save());

        const string notice =
            "SSB channels: this file predates the dump-completion fix (only 28 of 100 slots) "
            + "— re-read the radio.";
        Assert.Equal(notice, Assert.Single(_clone.Summary));
        Assert.Equal("Loaded a clone file captured 2026-08-22T19:42:03.0000000Z. " + notice,
            _clone.StatusText);
        // …and the file is kept, unwritable, refused BY THE EXISTING PREFLIGHT.
        Assert.Equal(CloneDomainState.Faulted, _clone.File!.ChannelState);
        Assert.Contains("SSB channels", _clone.WriteBlockedReason!, StringComparison.Ordinal);
    }

    /// <summary>THE ANTI-VACUITY TWIN: a whole file loads silently, exactly as
    /// it always did — the summary stays empty and the status line says only
    /// what it said before.</summary>
    [Fact]
    public void LoadingAWholeFile_SaysNothingNew()
    {
        var file = CloneFileTests.Complete();
        file.CapturedUtc = "2026-08-24T00:00:00.0000000Z";
        CloneFileTests.FillChannels(file);

        _clone.LoadJson(file.Save());

        Assert.Empty(_clone.Summary);
        Assert.Equal("Loaded a clone file captured 2026-08-24T00:00:00.0000000Z.", _clone.StatusText);
        Assert.Equal(CloneDomainState.Read, _clone.File!.ChannelState);
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// D5a (plan-clone-write-structural.md §5.4) — THE FILL-GATE LINE: the read
/// report's difference between "the radio refused" and "the radio is empty".
///
/// <para>Same real stack as <see cref="CloneServiceTests"/>, plus
/// <see cref="DeferredModeEntryPort"/> — used here only for its
/// <c>Inject</c>, because the fill-gate report is a line the radio emits on
/// its own and the demo emits it in exactly one place (an ALE-context
/// <c>ZERO</c>), which the zeroize boundary then wipes back out of the mirror.
/// Nothing is deferred and the demo is taught nothing (owner directive I-11).</para>
/// </summary>
public sealed class CloneFillGateReportTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly DeferredModeEntryPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly AleSurface _ale;
    private readonly SsbSurface _ssb;
    private readonly CloneService _clone;

    public CloneFillGateReportTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new DeferredModeEntryPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _ale = new AleSurface(_radio);
        _ssb = new SsbSurface(_radio);
        _clone = new CloneService(
            _radio, _session, new FakeConfirmationPrompt(),
            _ssb, new PowerSurface(_radio), new DeviceSurface(_radio),
            _ale, new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        { SentinelTimeoutMs = 5_000, GateTimeoutMs = 10_000 };
    }

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>Empty the book the honest way — the wipe — then put the radio's
    /// own fill-gate report back on the wire, which is what a self-less radio
    /// answers its ALE listings with. (The zeroize boundary resets every
    /// mirror, the fill state among them, so the order matters.)</summary>
    private async Task AtAnEmptyBookAsync()
    {
        _ssb.ZeroizeRadio();
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && !_ssb.ZeroizeSettled) await Task.Delay(5);
        Assert.True(_ssb.ZeroizeSettled);
    }

    private async Task AtASelfLessRadioAsync()
    {
        await AtAnEmptyBookAsync();

        _port.Inject("\r\n PRG 1-3 CHAR SLF \r\n");
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline && !_ale.FillState.IsConfirmed) await Task.Delay(5);
        Assert.Equal(AleFillState.NeedSelfAddress, _ale.FillState.Value);
    }

    [Fact]
    public async Task AReadOfASelfLessRadio_SaysWhyItsListingsAnsweredNothing()
    {
        ConnectReady();
        await AtASelfLessRadioAsync();

        await _clone.ReadAsync();

        // ONE line for the whole fill family, byte-exact, and no raw radio
        // token in it (R13 / I-3).
        Assert.Equal(
            "ALE fill: the radio reports no self address is programmed, so its address, message and "
                + "group listings answer nothing.",
            Assert.Single(_clone.Summary,
                l => l.StartsWith("ALE fill:", StringComparison.Ordinal)));
        Assert.DoesNotContain(_clone.Summary, l => l.Contains("PRG", StringComparison.Ordinal));

        // The domain MARKING is deliberately unchanged: an empty book that
        // answered is still Read, and the file is still writable-shaped.
        var file = _clone.File!;
        Assert.Empty(file.Selfs);
        Assert.Equal(CloneDomainState.Read, file.BookState);
    }

    [Fact]
    public async Task AReadOfAFilledRadio_CarriesNoFillGateLine()
    {
        // ANTI-VACUITY, both halves at once: a radio with a book says nothing
        // (the fill state is never NeedSelfAddress), so the line reports an
        // observed condition rather than firing on every read.
        ConnectReady();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        Assert.NotEmpty(_clone.File!.Selfs);
        Assert.DoesNotContain(_clone.Summary, l => l.StartsWith("ALE fill:", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE OTHER TERM'S discrimination: a radio whose book really IS empty but
    /// which has NOT reported the fill gate says nothing. A blank book is not
    /// evidence of a missing self — the operator may simply have erased the
    /// fill — and the sentence claims the radio REPORTED something, so it may
    /// only appear when the radio did.
    /// </summary>
    [Fact]
    public async Task AnEmptyBookWithNoFillGateReport_SaysNothing()
    {
        ConnectReady();
        await AtAnEmptyBookAsync();
        Assert.False(_ale.FillState.IsConfirmed);   // the wipe reset every mirror

        await _clone.ReadAsync();

        Assert.Empty(_clone.File!.Selfs);           // …and the book really is empty
        Assert.DoesNotContain(_clone.Summary, l => l.StartsWith("ALE fill:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFillGateReportOverANONEmptyBook_SaysNothing()
    {
        // The OTHER discrimination, and the reason the rule takes BOTH terms:
        // the radio reporting the gate while its book still lists rows is not
        // the case this sentence describes, and claiming it would be a lie
        // about a book the read really did get.
        ConnectReady();
        _port.Inject("\r\n PRG 1-3 CHAR SLF \r\n");
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline && !_ale.FillState.IsConfirmed) await Task.Delay(5);
        Assert.Equal(AleFillState.NeedSelfAddress, _ale.FillState.Value);

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        Assert.NotEmpty(_clone.File!.Selfs);
        Assert.DoesNotContain(_clone.Summary, l => l.StartsWith("ALE fill:", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// D15 (plan-clone-write-structural.md §2, owner 2026-08-30) — THE READ
/// SUMMARY'S STORED INVENTORY: "instead of that message, give a line by line of
/// what WAS stored. x channels, x chan groups, x nets etc, basically the info
/// from the status line that's shown when the file is loaded into the app by
/// the open file button."
///
/// <para>The family in one place: the twelve rows on a full fill, the ZEROS
/// that are omitted rather than shown, and the byte-death of the elision notice
/// the inventory replaced. The FAULTED-domain half is pinned where the fault
/// fixture lives (<see cref="CloneRound16TruncatedDumpTests"/>), and the
/// WRITE-VERIFY half where the round trip lives (<see cref="CloneServiceTests"/>
/// — a write report carries no inventory row at all).</para>
///
/// <para>Same real stack as <see cref="CloneServiceTests"/>; the empty case is
/// reached by the radio's OWN WIPE, so the demo is taught nothing (owner
/// directive I-11).</para>
/// </summary>
public sealed class CloneStoredInventoryTests : IDisposable
{
    /// <summary>The notice D15 deleted, held verbatim so the absence pins are
    /// about the RETIRED STRING rather than a paraphrase of it — re-adding the
    /// real sentence has to trip them.</summary>
    private const string RetiredElisionNotice = "SSB channels: 98 at factory default — not stored.";

    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly SsbSurface _ssb;
    private readonly CloneService _clone;

    public CloneStoredInventoryTests()
    {
        _port = new RecordingDemoPort(_demo);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _ssb = new SsbSurface(_radio);
        _clone = new CloneService(
            _radio, _session, new FakeConfirmationPrompt(),
            _ssb, new PowerSurface(_radio), new DeviceSurface(_radio),
            new AleSurface(_radio), new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), new ModeSurface(_radio), new CampaignWireCoordinator())
        { SentinelTimeoutMs = 5_000, GateTimeoutMs = 10_000 };
    }

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>Empty the radio the honest way — its own wipe. Every domain
    /// still READS afterwards (a wiped radio answers all of them); what changes
    /// is that most of them now hold nothing, which is exactly the case the
    /// omission rule is about.</summary>
    private async Task AtAWipedRadioAsync()
    {
        _ssb.ZeroizeRadio();
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && !_ssb.ZeroizeSettled) await Task.Delay(5);
        Assert.True(_ssb.ZeroizeSettled);
    }

    /// <summary>(a) THE FULL FILL — every one of the twelve rows, with the right
    /// count, in D15's order, LAST in the report.</summary>
    [Fact]
    public async Task AFullRead_EndsWithTheTwelveInventoryRows_InOrder()
    {
        ConnectReady();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        Assert.Equal(
            [
                "Left the radio on channel 00, net 0, SSB.",
                "2 channel(s)",
                "10 channel group(s)",
                "3 self(s)",
                "5 individual(s)",
                "3 net(s)",
                "2 message(s)",
                "2 schedule(s)",
                "10 HOP net(s)",
                "2 exclusion band(s)",
                "10 modem preset(s)",
                "30 setting(s)",
                "22 lockout(s)",
            ],
            _clone.Summary);

        // EVERY ROW AGAINST THE FILE IT CLAIMS TO DESCRIBE (self-audit 1). The
        // inventory is read off the file this campaign just built, and these
        // are that file's own collections — so a row that drifted from its
        // collection fails here even if the twelve strings above had been
        // updated to match the drift.
        var file = _clone.File!;
        Assert.Equal(2, file.Channels.Count);
        Assert.Equal(10, file.ChannelGroups.Count);
        Assert.Equal(3, file.Selfs.Count);
        Assert.Equal(5, file.Individuals.Count);
        Assert.Equal(3, file.Nets.Count);
        Assert.Equal(2, file.Messages.Count);
        Assert.Equal(2, file.Schedules.Count);
        Assert.Equal(10, file.HopNets.Count);
        Assert.Equal(2, file.ExcludeBands.Count);
        Assert.Equal(10, file.ModemPresets.Count);
        Assert.Equal(30, file.Settings.Count);
        Assert.Equal(22, file.Lockouts!.Rows.Count);

        // …and the CHANNEL row counts the STORED rows, not the 100 reported:
        // D4's elision still happens, and the row conveys its count implicitly.
        Assert.True(file.DefaultChannelsElided);
        Assert.Equal("Read complete.", _clone.StatusText);
    }

    /// <summary>(b) THE ZEROS ARE OMITTED, and the retired sentence is dead. A
    /// wiped radio stores no channel that differs from the factory row — the
    /// very case the elision notice used to shout about — so the inventory says
    /// nothing about channels at all, and the report is that much shorter
    /// rather than a wall of "0 …".</summary>
    [Fact]
    public async Task AWipedRadio_ShowsNoZeroRows_AndNoElisionSentence()
    {
        ConnectReady();
        await AtAWipedRadioAsync();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // ANTI-VACUITY: the domains below really were READ and really are
        // empty, so their missing rows are OMISSIONS and not faults.
        var file = _clone.File!;
        Assert.Empty(file.IncompleteDomains);
        Assert.Equal(CloneDomainState.Read, file.ChannelState);
        Assert.Empty(file.Channels);
        Assert.Empty(file.Selfs);
        Assert.Empty(file.Individuals);
        Assert.Empty(file.Nets);
        Assert.Empty(file.Messages);
        Assert.Empty(file.Schedules);
        Assert.Empty(file.ExcludeBands);
        Assert.Empty(file.ModemPresets);

        // THE WHOLE REPORT: the restore notice, and the FOUR domains a wiped
        // radio still holds something for. Eight rows are simply not there.
        Assert.Equal(
            [
                "Left the radio on channel 00, net 0, SSB.",
                "10 channel group(s)",
                "10 HOP net(s)",
                "30 setting(s)",
                "22 lockout(s)",
            ],
            _clone.Summary);

        foreach (var noun in new[]
        {
            "channel(s)", "self(s)", "individual(s)", "net(s)", "message(s)",
            "schedule(s)", "exclusion band(s)", "modem preset(s)",
        })
            Assert.False(HasRow(noun), $"an empty domain still produced a '{noun}' row");
        // No "0 anything", anywhere.
        Assert.DoesNotContain(_clone.Summary, l => l.StartsWith("0 ", StringComparison.Ordinal));
        // THE BYTE-DEATH PIN: the retired notice in whole, and the two
        // fragments any re-wording of it would still carry.
        Assert.DoesNotContain(RetiredElisionNotice, _clone.Summary);
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("factory default", StringComparison.Ordinal));
        Assert.DoesNotContain(_clone.Summary,
            l => l.Contains("not stored", StringComparison.Ordinal));

        // …while the domains that DO hold something still say so, so the
        // omission rule did not simply empty the inventory.
        Assert.True(HasRow("channel group(s)"));
        Assert.True(HasRow("HOP net(s)"));
        Assert.True(HasRow("setting(s)"));
        Assert.True(HasRow("lockout(s)"));
    }

    /// <summary>Is there an inventory row for this exact noun? Split at the
    /// first space rather than matched by suffix, because <c>"HOP net(s)"</c>
    /// ENDS WITH <c>" net(s)"</c> — a suffix test would report the HOP row as a
    /// net row and quietly pass an omission pin that should fail.</summary>
    private bool HasRow(string noun) =>
        _clone.Summary.Any(l => l.Split(' ', 2) is [var count, var rest]
            && rest.Equals(noun, StringComparison.Ordinal)
            && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    /// <summary>The retired sentence is dead in the SOURCE too, not merely
    /// unsaid at run time on the fixtures these tests happen to drive.</summary>
    [Fact]
    public void TheElisionNotice_IsByteDeadInTheAppSource()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("at factory default", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }
}
