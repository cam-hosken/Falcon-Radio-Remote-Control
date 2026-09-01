using System.Text;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// A port that can make the radio DO NOTHING about a command it accepted —
/// the two shapes the closing restore has to survive.
///
/// <para><c>HoldForever</c> drops every write of a command outright: the radio
/// that takes the mode switch and never reaches the new prompt, which is how a
/// read campaign terminates EARLY with the operator's radio already moved.
/// <c>SwallowFirstAfter</c> drops ONE write of a command once a marker command
/// has gone past: the radio that takes a channel select and stays where it was,
/// which is how the restore's read-back comes back DISAGREEING.</para>
///
/// <para>It also records what the CAMPAIGN sent — held and swallowed lines
/// included — because the TX-order pins are about the campaign's intent, and a
/// recorder underneath the drop would be reading the fixture's own edits.</para>
/// </summary>
internal sealed class RestoreTestPort : ISerialPort
{
    private readonly RecordingDemoPort _inner;
    private readonly List<string> _sent = [];
    private readonly object _lock = new();
    private string? _hold;
    private string? _marker;
    private string? _victim;
    private bool _armed;

    /// <summary>The <c>DataReceived</c> event is OWNED here rather than
    /// forwarded, so the fixture can push bytes up itself — see
    /// <see cref="Inject"/>.</summary>
    public RestoreTestPort(RecordingDemoPort inner)
    {
        _inner = inner;
        _inner.DataReceived += (_, e) => DataReceived?.Invoke(this, e);
    }

    /// <summary>Push bytes up without asking the radio anything. This is the one
    /// way to reach a session that is READY with every mirror still unconfirmed:
    /// the init sentinels complete on a BATTERY answer, and the demo frames a
    /// prompt onto everything it says — so only an answer nobody asked for can
    /// arrive without one.</summary>
    public void Inject(string text)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));

    /// <summary>Drop every write from now on: the radio that hears nothing and
    /// says nothing.</summary>
    public void SilenceEverything() { lock (_lock) _silent = true; }

    public IReadOnlyList<string> Sent { get { lock (_lock) return [.. _sent]; } }

    public void ClearSent() { lock (_lock) _sent.Clear(); }

    /// <summary>True once the held command really was dropped — anti-vacuity.</summary>
    public bool Held { get; private set; }

    /// <summary>True once the victim really was dropped — anti-vacuity.</summary>
    public bool Swallowed { get; private set; }

    public void HoldForever(string command) { lock (_lock) _hold = command; }

    /// <summary>On <paramref name="trigger"/>: drop it, drop EVERYTHING after
    /// it, and run <paramref name="then"/> off the write thread. The silence is
    /// what keeps the campaign waiting long enough for the SESSION event to be
    /// the thing it notices, rather than a sentinel that happened to answer
    /// first. The action is the fixture's: a user Close, or a port loss.</summary>
    public void SilenceAndThen(string trigger, Action then)
    {
        lock (_lock) { _closeTrigger = trigger; _close = then; }
    }

    /// <summary>The cable comes out: the port reports itself gone. With
    /// auto-reconnect ARMED this is the route to
    /// <see cref="SessionPhase.Reconnecting"/> — a `Close` only ever reaches
    /// `Disconnected`, so the two phases need two different events.</summary>
    public void LosePort()
        => Disconnected?.Invoke(this, new SerialDisconnectedEventArgs(new IOException("port lost")));

    private string? _closeTrigger;
    private Action? _close;
    private bool _silent;

    /// <summary>Drop the FIRST <paramref name="victim"/> written after
    /// <paramref name="marker"/> has gone past. An empty marker arms
    /// immediately — for a campaign that issues the victim exactly once.</summary>
    public void SwallowFirstAfter(string marker, string victim)
    {
        lock (_lock) { _marker = marker; _victim = victim; _armed = marker.Length == 0; }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var text = Encoding.ASCII.GetString(data.Span).Trim();
            _sent.Add(text);
            if (_silent) return Task.CompletedTask;
            if (_closeTrigger is { } trigger && text == trigger)
            {
                _silent = true;
                var close = _close;
                ThreadPool.QueueUserWorkItem(_ => close?.Invoke());
                return Task.CompletedTask;
            }
            if (_hold is { } held && text == held) { Held = true; return Task.CompletedTask; }
            if (_marker is { } marker && marker.Length > 0 && text == marker) _armed = true;
            else if (_armed && text == _victim)
            {
                _armed = false;
                Swallowed = true;
                return Task.CompletedTask;      // the radio simply never acts on it
            }
        }
        return _inner.WriteAsync(data, cancellationToken);
    }

    public bool IsOpen => _inner.IsOpen;

    public event EventHandler<SerialDataEventArgs>? DataReceived;

    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected;

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() => _inner.CloseAsync();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// F1 — THE CLOSING RESTORE (plan-clone-field-round2.md §3.4; owner decisions
/// D1 and D1').
///
/// <para><b>The field failure.</b> The clone read of 2026-08-21 produced a good
/// file and left the SOURCE radio on the wrong channel. The read campaign
/// issued no <c>CH</c> or <c>NET</c> at all, so the mode lap itself moved it: a
/// NET select silently changes the SSB channel (probe R9b) and HOP entry
/// regenerates on the current net (probe P4). The demo models that as a
/// LABELLED FACT (<c>DemoSerialPort.NoteHopEntry</c>) so these tests fail for
/// the real reason rather than for a fixture's convenience.</para>
///
/// <para><b>AMENDED by scan doctrine v2</b> (plan-clone-write-structural.md D8,
/// §6 pin (g)). The sentence above used to say the read campaign issues no
/// <c>ST</c> or <c>SCA</c> either. It does now: every ALE occupancy of every
/// campaign is preceded by an unconditional judged <c>ST</c>, and a campaign
/// that FOUND the radio scanning attempts one <c>SCA</c> at the end of this
/// very funnel. That does not weaken the diagnosis — neither command moves a
/// channel or a net — and the restore's own contract is unchanged. The scan
/// lifetime itself is pinned in <c>CloneScanDoctrineTests</c>.</para>
///
/// <para>Its own fixture because the interesting cases need a radio that
/// IGNORES a command it accepted — see <see cref="RestoreTestPort"/>.</para>
/// </summary>
public sealed class CloneClosingRestoreTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly RestoreTestPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly FakeConfirmationPrompt _prompt = new();
    private readonly CloneService _clone;

    public CloneClosingRestoreTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new RestoreTestPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0, GateTimeoutMs = 150 };
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
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }

    // ---- fixture helpers ----------------------------------------------------

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>Put the radio at a mode the way an operator would, and WAIT for
    /// the radio to confirm it.</summary>
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

    /// <summary>…and on a channel, confirmed. This is the operator's state the
    /// campaign is responsible for giving back.</summary>
    private async Task AtChannelAsync(int channel)
    {
        await AtModeAsync(OperatingMode.Ssb);
        var channels = new ChannelSurface(_radio);
        channels.Select(channel);
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(channels.Current.IsConfirmed && channels.Current.Value == channel))
            await Task.Delay(5);
        Assert.Equal(channel, channels.Current.Value);
    }

    /// <summary>The campaign's STATE-CHANGING verbs, in order. Everything else —
    /// the <c>SH</c> re-reads, the <c>BAT ST</c> brackets, every query — is
    /// deliberately excluded: the TX-order claim is about what MOVES the radio,
    /// and an allow-list of the rest would break on the next legitimate Core
    /// compensation.</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static List<string> StateChanging(IEnumerable<string> sent)
        => [.. sent.Where(l =>
        {
            var token = l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return token is "CH" or "NET" or "SS" or "ALE" or "HO";
        })];

    /// <summary>The restore's ONE summary line, whichever of the three shapes it
    /// took. <c>Assert.Single</c> is the point: two restores, or none, fails
    /// here rather than somewhere downstream.</summary>
    private static string RestoreLine(IReadOnlyList<string> summary)
        => Assert.Single(summary, l =>
            l.StartsWith("Left the radio on", StringComparison.Ordinal)
            || l.StartsWith("The radio did not return to", StringComparison.Ordinal)
            || l.StartsWith("The radio never reported its operating channel", StringComparison.Ordinal));

    // ---- the demo fact itself ----------------------------------------------

    /// <summary>ANTI-VACUITY FOR EVERY TEST BELOW. The demo radio really does
    /// move the SSB channel when HOP is entered on a programmed current net —
    /// the labelled fact standing in for whatever the field radio did. Without
    /// it the restore tests would be proving that a no-op is a no-op.</summary>
    [Fact]
    public async Task TheDemoRadio_MovesTheSsbChannel_WhenHopIsEnteredOnAProgrammedNet()
    {
        ConnectReady();
        await AtChannelAsync(9);
        Assert.Equal(0, _radio.State.Hop.CurrentNet.Value);      // net 0, programmed

        await AtModeAsync(OperatingMode.Hop);
        await AtModeAsync(OperatingMode.Ssb);
        new SsbSurface(_radio).RequestStatus();
        var deadline = Environment.TickCount64 + 3_000;
        while (Environment.TickCount64 < deadline && _radio.State.OperatingChannel.Value == 9)
            await Task.Delay(5);

        Assert.Equal(0, _radio.State.OperatingChannel.Value);    // the net's channel, not the operator's
    }

    // ---- (a) the standalone read -------------------------------------------

    /// <summary>GATE (a). The lap moves the channel; the campaign puts it back,
    /// and says so in ONE line.</summary>
    [Fact]
    public async Task TheStandaloneRead_EndsOnTheCapturedChannel_WithTheNotice()
    {
        ConnectReady();
        await AtChannelAsync(9);

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        Assert.Equal(9, _clone.File!.OperatingChannel);          // what it captured…
        Assert.Equal(9, _radio.State.OperatingChannel.Value);    // …is where it left the radio
        Assert.Equal(0, _radio.State.Hop.CurrentNet.Value);
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);

        Assert.Equal("Left the radio on channel 09, net 0, SSB.", RestoreLine(_clone.Summary));
        // The line is a NOTICE, not a problem: a read that put the radio back is
        // a read that WORKED, and the campaign is still Done.
        Assert.Equal(CloneState.Done, _clone.State);
        // The only other lines are D15's STORED INVENTORY (2026-08-30, owner),
        // which closes every completed read and REPLACED D4's elision notice.
        // Nothing else may appear.
        Assert.Equal(
            [
                "Left the radio on channel 09, net 0, SSB.",
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
    }

    // ---- (c) the TX order, over state-changing verbs only ------------------

    /// <summary>GATE (c). <c>NET</c> before <c>CH</c> — because selecting a net
    /// moves the channel — and the final mode switch LAST, with the <c>SH</c>
    /// and <c>BAT ST</c> brackets between them permitted.
    /// <para>It also pins I-1 on the way past: across a WHOLE read campaign
    /// there is exactly ONE channel select and ONE net select, and both belong
    /// to the restore. A read that acquired a state-changing verb anywhere else
    /// fails here.</para></summary>
    [Fact]
    public async Task TheRestore_SendsNetBeforeChannel_AndTheFinalModeSwitchLast()
    {
        ConnectReady();
        await AtChannelAsync(9);
        await AtModeAsync(OperatingMode.Ale);        // …so the final switch is VISIBLE on the wire
        _port.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var verbs = StateChanging(_port.Sent);
        Assert.Equal("CH 9", Assert.Single(verbs, v => v.StartsWith("CH ", StringComparison.Ordinal)));
        Assert.Equal("NET 0", Assert.Single(verbs, v => v.StartsWith("NET ", StringComparison.Ordinal)));
        Assert.True(verbs.IndexOf("NET 0") < verbs.IndexOf("CH 9"),
            $"the net was not selected before the channel: {string.Join(" ", verbs)}");
        Assert.Equal("ALE", verbs[^1]);

        // …and the brackets really are in between, which is what makes the
        // filter above the honest way to state the claim.
        var raw = _port.Sent.ToList();
        int net = raw.IndexOf("NET 0");
        int channel = raw.IndexOf("CH 9");
        Assert.Contains(raw.Skip(net).Take(channel - net), l => l == "BAT ST");
        Assert.Equal("SH", raw[channel + 1]);        // ChannelSurface.Select is CH + SH
    }

    // ---- (e) nothing restored that was never observed ----------------------

    /// <summary>GATE (e). A value the campaign never saw is not guessed at: no
    /// <c>CH</c> goes out at all, and the ONE line says which values were left
    /// where the campaign found them (I-3).</summary>
    [Fact]
    public async Task AChannelTheCampaignNeverCaptured_IsNotSent_AndTheLineSaysSo()
    {
        ConnectReady();
        await AtModeAsync(OperatingMode.Ale);
        _radio.ModeChangeTimeoutMs = 300;
        // The radio accepts the switch to SSB and never gets there, so the
        // campaign stops before it can read a channel at all.
        _port.HoldForever("SS");
        _port.ClearSent();

        Assert.False(await _clone.ReadAsync());
        Assert.True(_port.Held, "the SSB switch was never held, so nothing was reproduced");

        Assert.Null(_clone.File!.OperatingChannel);
        Assert.Null(_clone.File.OperatingHopNet);
        Assert.DoesNotContain(StateChanging(_port.Sent),
            v => v.StartsWith("CH ", StringComparison.Ordinal)
                || v.StartsWith("NET ", StringComparison.Ordinal));

        Assert.Equal(
            "Left the radio on ALE. The radio never reported its channel or HOP net, "
            + "so that was left as the read found it.",
            RestoreLine(_clone.Summary));
    }

    // ---- (g) early termination ---------------------------------------------

    /// <summary>GATE (g). The read stops at a mid-campaign mode gate with the
    /// session still Ready — and the state it HAD captured is still restored,
    /// exactly once. This is the single-funnel claim: before the try/finally,
    /// every early return skipped the restore on a radio the campaign had
    /// already moved.</summary>
    [Fact]
    public async Task AnEarlyTerminatedRead_StillRestoresWhatItCaptured_ExactlyOnce()
    {
        ConnectReady();
        await AtChannelAsync(9);
        _radio.ModeChangeTimeoutMs = 300;
        _port.HoldForever("HO");                     // the HOP leg never gets its prompt
        _port.ClearSent();

        Assert.False(await _clone.ReadAsync());
        Assert.True(_port.Held, "the HOP switch was never held, so nothing was reproduced");
        Assert.Equal(SessionPhase.Ready, _session.Phase);        // …and the radio is still there

        // The HOP net was never read, so it is not restored; the channel was.
        Assert.Null(_clone.File!.OperatingHopNet);
        Assert.Equal(9, _clone.File.OperatingChannel);
        Assert.Equal(9, _radio.State.OperatingChannel.Value);

        // EXACTLY ONCE — one select, one line.
        Assert.Single(_port.Sent, l => l == "CH 9");
        Assert.Equal(
            "Left the radio on channel 09, SSB. The radio never reported its HOP net, "
            + "so that was left as the read found it.",
            RestoreLine(_clone.Summary));
    }

    // ---- (d) the read-back disagrees ---------------------------------------

    /// <summary>GATE (d), read half. The radio takes the channel select and
    /// stays where it was. The restore does NOT retry and does not pretend: it
    /// writes ONE problem line naming both numbers. The FILE is untouched by
    /// this — a read that left the radio somewhere else still read the radio
    /// correctly (§3.4, "effect on completion").</summary>
    [Fact]
    public async Task AChannelSelectTheRadioIgnores_IsOneProblemLine_NotARetry()
    {
        ConnectReady();
        await AtChannelAsync(9);
        _port.SwallowFirstAfter("", "CH 9");         // the campaign's only channel select
        _port.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        Assert.True(_port.Swallowed, "the channel select went through, so nothing was reproduced");

        Assert.Equal("The radio did not return to channel 09 — it reports channel 00.", RestoreLine(_clone.Summary));
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal));
        // NOT a retry: one attempt, and it was the one that was swallowed.
        Assert.Single(_port.Sent, l => l == "CH 9");
        // The read still COMPLETED — the file is as good as it ever was.
        Assert.Equal(CloneState.Done, _clone.State);
        Assert.Equal(9, _clone.File!.OperatingChannel);
    }

    /// <summary>GATE (d), the NET step. The field write's summary carried
    /// exactly this shape — "Operating HOP net: expected 0, the radio reports
    /// 9". Reproduced honestly: the file asks for a net the demo has WIPED, so
    /// <c>NET 5</c> answers the captured "No Hopset" and the mirror never moves.
    /// The restore names both numbers in its own line rather than leaving the
    /// operator to infer it from the verify diff.</summary>
    [Fact]
    public async Task ANetTheRadioWillNotTake_IsNamedInTheRestoreLine()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        Assert.True(Assert.Single(file.HopNets, n => n.Number == 5).Wiped);   // anti-vacuity
        file.OperatingHopNet = 5;
        file.OperatingChannel = 7;
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        // Leg 11's own net select is the one the radio loses, so by the time the
        // restore runs the radio really is somewhere else — and a WIPED net's
        // select answers "No Hopset" with NO `NET` line, so nothing tells the
        // app it moved. That is exactly the pair of facts the read-back exists
        // to notice.
        _port.SwallowFirstAfter("", "NET 5");
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.True(_port.Swallowed, "leg 11's net select went through");

        // The CHANNEL half still worked, so the line names only what did not.
        Assert.Equal(7, _radio.State.OperatingChannel.Value);
        Assert.Equal("The radio did not return to net 5 — it reports net 0.",
            RestoreLine(_clone.Summary));
    }

    // ---- the funnel's two guards -------------------------------------------

    /// <summary>
    /// THE READY GUARD (audit round 2). <c>RunClosingRestoreAsync</c> returns
    /// immediately when the session is gone, and nothing pinned that: replacing
    /// the condition with <c>false</c> left all 2 005 App tests green.
    ///
    /// <para>What it is FOR: with the radio away, every step would only write
    /// "the session dropped" lines for a campaign that has already said so, and
    /// the restore's own abort would then take over the headline the failing leg
    /// had earned. So the pin is the campaign's REPORT, not just its traffic —
    /// the restore contributes no line, no state-changing command, and no
    /// rewrite of where the campaign stopped.</para>
    ///
    /// <para>The drop is placed at the channel-dump leg, so the campaign has
    /// already captured a mode and a channel: the restore would have had real
    /// work to do, which is what makes its silence meaningful.</para>
    ///
    /// <para><b>BOTH not-Ready phases, because the predicate is <c>!= Ready</c>
    /// and not "== Disconnected"</b> (audit round 3). A user <c>Close</c> only
    /// ever reaches <c>Disconnected</c>, so a version of this test that used one
    /// left the other half unguarded — broadening production to
    /// <c>Ready || Reconnecting</c> kept all 2 007 tests green. The two phases
    /// arrive by different events (a Close versus a port loss with
    /// auto-reconnect armed) and are therefore two rows here. RECONNECTING is
    /// the one that matters most: the radio is not there, but the session still
    /// intends to get it back, and a restore firing into that gap would write
    /// its lines against a port the transport has already closed.</para>
    /// </summary>
    [Theory]
    [InlineData(SessionPhase.Disconnected)]
    [InlineData(SessionPhase.Reconnecting)]
    public async Task ASessionGoneBeforeTheFunnel_RestoresNothing_AndKeepsTheCampaignsOwnReport(
        SessionPhase gone)
    {
        ConnectReady();
        await AtChannelAsync(9);
        if (gone == SessionPhase.Reconnecting)
        {
            _session.AutoReconnectEnabled = true;      // dormant by default (G1)
            _session.ReconnectIntervalMs = 3_600_000;  // park the poller: the phase must HOLD
            _port.SilenceAndThen("DI 0 99", _port.LosePort);
        }
        else
        {
            _port.SilenceAndThen("DI 0 99", () => _session.Close());
        }
        _port.ClearSent();

        Assert.False(await _clone.ReadAsync());

        Assert.True(WaitUntil(() => _session.Phase == gone),
            $"the session reached {_session.Phase}, not {gone}: {string.Join(" | ", _clone.Summary)}");
        // It got far enough that a restore WOULD have had values to send.
        Assert.Equal(9, _clone.File!.OperatingChannel);
        Assert.Equal("Ssb", _clone.File.OperatingMode);

        // The restore said nothing…
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal)
                || l.StartsWith("The radio did not return to", StringComparison.Ordinal)
                || l.StartsWith("The radio never reported its operating channel", StringComparison.Ordinal));
        // …and it did not add a line of its own under its leg name either, which
        // is what the guard removed would produce.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Operating state:", StringComparison.Ordinal));
        Assert.DoesNotContain("operating state", _clone.StatusText, StringComparison.Ordinal);

        // …and it sent nothing: after the drop the campaign attempts no channel,
        // net or mode command at all.
        var after = _port.Sent.SkipWhile(l => l != "DI 0 99").ToList();
        Assert.Empty(StateChanging(after));

        // The campaign's OWN account of where it stopped is intact.
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("SSB channels:", StringComparison.Ordinal));
    }

    /// <summary>
    /// NOTHING CAPTURED AT ALL, on a session that is still Ready (audit round 2
    /// — my "unreachable" claim was wrong).
    ///
    /// <para>The route: a session reaches Ready on the init sentinels' BATTERY
    /// answers, which is the only thing <c>CompleteInitialization</c> waits for
    /// — so a radio that answers those and nothing else leaves EVERY mirror
    /// unconfirmed, the operating mode among them. The read's leg 0 then sends
    /// its <c>SH</c>, times its sentinel out, finds no confirmed mode and
    /// returns before capturing a channel or a net. The funnel still runs,
    /// because the radio is reachable as far as the session can tell.</para>
    ///
    /// <para>What it must do: say so, ONCE, and touch nothing. Not the closing
    /// status read either — a question with no question in it, which on this
    /// very radio would time out and turn "there was nothing to put back" into a
    /// fault line.</para>
    /// </summary>
    [Fact]
    public async Task NothingCapturedOnAReadySession_SaysSoOnce_AndSendsNothing()
    {
        // A radio that hears nothing and answers its two init sentinels —
        // nothing else. ROUND 15: each answer is injected only once its own
        // `BAT ST` has actually gone out, because a battery line that
        // arrives before the app has asked is a STRAY now and completes
        // nothing (the radio's extra answer at a mode entry is what that
        // rule exists for). The ritual drains on the write gate's timeout
        // here, a silent radio sending no prompts either.
        _port.SilenceEverything();
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });

        Assert.True(WaitUntil(() => _port.Sent.Count(l => l == "BAT ST") >= 1),
            "the ritual's first sentinel never went out");
        _port.Inject("Battery Status FULL 31.4V\r\n");
        Assert.True(WaitUntil(() => _session.Phase == SessionPhase.Ready),
            $"the session never reached Ready (phase {_session.Phase})");

        Assert.True(WaitUntil(() => _port.Sent.Count(l => l == "BAT ST") >= 2),
            "the redundancy sentinel never went out");
        _port.Inject("Battery Status FULL 31.4V\r\n");
        Assert.True(WaitUntil(() => _radio.PendingPingCount == 0),
            "the redundancy sentinel never drained");
        // ANTI-VACUITY: Ready, and the radio has still never said where it is.
        Assert.False(_radio.State.OperatingMode.IsConfirmed);

        _clone.SentinelTimeoutMs = 200;
        _port.ClearSent();

        Assert.False(await _clone.ReadAsync());
        Assert.Equal(SessionPhase.Ready, _session.Phase);     // …and it never went away

        var file = _clone.File!;
        Assert.Null(file.OperatingMode);
        Assert.Null(file.OperatingChannel);
        Assert.Null(file.OperatingHopNet);

        // THE WHOLE SUMMARY, exactly — leg 0's ONE line, and the restore's ONE.
        // Exact rather than "contains", because what the closing read-back would
        // add here is a SECOND copy of leg 0's own timeout line plus an abort,
        // and only a count can see that. On this radio the wire cannot show it:
        // the transport's prompt gate holds every write for a radio that never
        // prompts, so a stuck `SH` and an unsent one look the same at the port.
        //
        // SCAN DOCTRINE v2 (D8, §5.4c) TOOK THE SECOND LINE, deliberately. The
        // read campaign now opens with a READ-ONLY DISCOVERY SENTINEL, whose
        // whole job is to let a radio found in ALE be stopped BEFORE its
        // operating channel is snapshotted. On a radio that answers nothing that
        // sentinel is the first thing to time out, and the plan says so in
        // terms: "a dead radio aborts here with the existing honest line". The
        // campaign therefore stops one step earlier than it used to, and
        // "operating state: the radio has not reported a mode this session." —
        // which the OLD order reached only after sending an `SH` this radio was
        // never going to answer — is no longer said twice over in different
        // words. The one line that is left is true, and it is the same sentence
        // it always was.
        Assert.Equal(
        [
            "Operating state: the radio stopped answering during this step.",
            "The radio never reported its operating channel, HOP net or mode, so the read "
                + "left it exactly as it found it.",
        ], _clone.Summary);

        // NOTHING was sent by the restore: no channel, no net, no mode switch.
        Assert.Empty(StateChanging(_port.Sent));

        // ---- D8 MATRIX ROW: abort BEFORE the campaign-start stop ran --------
        // This fixture is the ONLY one that reaches a Ready session with the
        // mode mirror UNCONFIRMED, which is the one branch of the start
        // sequence that spends a discovery sentinel (§5.4c, audit round 1). The
        // radio answers nothing, so the campaign aborts on that sentinel —
        // before any snapshot and before any `ST`. The restart licence is
        // therefore never taken, and the closing funnel makes no attempt:
        // nothing on the wire, nothing claimed in the summary.
        Assert.DoesNotContain("ST", _port.Sent);
        Assert.DoesNotContain("SCA", _port.Sent);
        Assert.DoesNotContain(CloneService.ScanStoppedNotice, _clone.Summary);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    // ---- A-10: the HOP final mode owns the channel -------------------------

    /// <summary>
    /// <b>THE AUDIT-ROUND-1 BLOCKER, pinned.</b> With a HOP final mode the old
    /// NET → CH → mode order put the channel back and then threw it away: the
    /// switch to <c>HOP&gt;</c> re-imposes the current net's channel (the
    /// labelled demo fact / P4+R9b), so the radio ended on the NET's channel
    /// while the summary said <i>"Left the radio on channel 07"</i> — read from
    /// the campaign's own echo, not from the radio.
    ///
    /// <para>Two things are pinned, and BOTH of them fail under the old design.
    /// (1) The ORDER: the channel select goes out BEFORE the HOP entry, and the
    /// net select AFTER it — mode-last still holds because nothing follows the
    /// net at its own prompt. (2) The CLAIM: the line names the net and the
    /// mode, and says out loud that the net set the channel, instead of
    /// asserting a channel the campaign cannot promise.</para>
    /// </summary>
    [Fact]
    public async Task AHopFinalMode_PutsTheChannelFirst_AndNeverClaimsIt()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.OperatingMode = "Hop";
        file.OperatingChannel = 7;      // the file's channel…
        file.OperatingHopNet = 2;       // …and a net whose entry imposes a DIFFERENT one
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        // NOT asserted clean, and the reason is the point of the test: leg 11
        // (untouched, I-10) writes net → channel → mode, so its own HOP entry
        // moves the channel to the net's before the verify reads it, and the
        // verify reports "Operating channel: expected 7, the radio reports 2".
        // That pairing — a HOP operating mode with a channel that is not the
        // net's — is one only a hand-edited file can hold; a real read under HOP
        // captures the channel the net imposed. The RESTORE's job here is to
        // stop claiming the file's number, which is what is pinned below.
        await _clone.WriteAsync(CloneSwapTests.Rows());
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("Operating channel:", StringComparison.Ordinal));

        // (1) THE ORDER, over the restore's OWN traffic. The delimiter is the
        //     bare `EXC` — the verify read's last leg, and a command the write
        //     body only ever sends with arguments — so the verify's own mode lap
        //     cannot be mistaken for the restore's.
        var sent = _port.Sent.ToList();
        var restore = StateChanging(sent.Skip(sent.LastIndexOf("EXC") + 1));
        Assert.Equal(["SS", "CH 7", "HO", "NET 2"], restore);

        // (2) THE CLAIM — D23: a WRITE claims nothing at all now; the not-
        // claimed channel is covered by the line's total absence.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal));

        // …and the radio really is on the NET's channel, not the file's 7 — the
        // fact the old order's read-back was blind to. Observed by going back to
        // SSB and asking, because the HOP SH block carries NET and not CHAN.
        Assert.Equal(2, _radio.State.Hop.CurrentNet.Value);
        Assert.Equal(OperatingMode.Hop, _radio.State.OperatingMode.Value);
        await AtModeAsync(OperatingMode.Ssb);
        new SsbSurface(_radio).RequestStatus();
        Assert.True(WaitUntil(() => _radio.State.OperatingChannel.Value == 2),
            $"the radio is on channel {_radio.State.OperatingChannel.Value}, not the net's");
    }

    /// <summary>
    /// The MECHANISM half of A-10: the restore closes with a STATUS READ OF ITS
    /// OWN, and compares what that answers.
    ///
    /// <para>Before the amendment the read-back was <c>_channel.Current</c> as
    /// the campaign's own <c>CH</c>+<c>SH</c> had left it — a question about the
    /// app, not about the radio. A radio that moved after that echo still
    /// produced a clean match, which is the false claim the audit reproduced.
    /// The consequence is pinned in
    /// <see cref="AHopFinalMode_PutsTheChannelFirst_AndNeverClaimsIt"/>; this
    /// pins the mechanism, so that deleting the closing read fails HERE even in
    /// a scenario where nothing happens to move.</para>
    ///
    /// <para>The tail is exact: the channel step's own <c>CH</c>+<c>SH</c> and
    /// its sentinel, and then a SECOND <c>SH</c> with a sentinel of its own —
    /// the fresh report. (An SSB final mode needs no closing mode switch: the
    /// channel step already stands at <c>SSB&gt;</c>.)</para>
    /// </summary>
    [Fact]
    public async Task TheRestore_ClosesWithAStatusReadOfItsOwn()
    {
        ConnectReady();
        await AtChannelAsync(9);
        _port.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent.ToList();
        var tail = sent.Skip(sent.LastIndexOf("CH 9")).ToList();
        Assert.Equal(["CH 9", "SH", "BAT ST", "SH", "BAT ST"], tail);
    }

    /// <summary>The SSB/ALE shapes are UNCHANGED by A-10 — the amendment is
    /// scoped to a HOP final mode, and this is what says so.</summary>
    [Theory]
    [InlineData(OperatingMode.Ssb)]
    [InlineData(OperatingMode.Ale)]
    public async Task ANonHopFinalMode_KeepsNetThenChannelThenMode(OperatingMode final)
    {
        ConnectReady();
        await AtChannelAsync(9);
        await AtModeAsync(final);
        _port.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var verbs = StateChanging(_port.Sent);
        Assert.True(verbs.IndexOf("NET 0") < verbs.IndexOf("CH 9"),
            $"the net was not selected before the channel: {string.Join(" ", verbs)}");
        // Mode LAST — and under an SSB final mode there is no switch to send at
        // all, because the channel step already stands at `SSB>`. Either way
        // nothing after this point moves the radio.
        Assert.Equal(final == OperatingMode.Ssb ? "CH 9" : "ALE", verbs[^1]);
        Assert.Equal($"Left the radio on channel 09, net 0, {final.ToString().ToUpperInvariant()}.",
            RestoreLine(_clone.Summary));
        Assert.Equal(9, _radio.State.OperatingChannel.Value);
    }

    // ---- A-12: the write's closing-restore funnel ---------------------------

    /// <summary>
    /// <b>THE SECOND AUDIT-ROUND-1 BLOCKER, pinned.</b> The verify read fails
    /// while the session is still Ready — the write used to `return false`
    /// straight past its restore, leaving the radio wherever the half-finished
    /// verify lap had parked it (the auditor found it at <c>ALE&gt;</c>). The
    /// restore now runs from a `finally`, so the operator's radio comes back
    /// even when the campaign does not.
    /// </summary>
    [Fact]
    public async Task AVerifyThatFailsWhileReady_StillRestores_ExactlyOnce()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.OperatingChannel = 7;
        file.OperatingHopNet = 2;
        _clone.Adopt(file);
        _radio.ModeChangeTimeoutMs = 300;
        _prompt.EnqueueAnswer(true);
        // The verify read's ALE leg never reaches its prompt. The marker is the
        // verify's own channel dump, so leg 5/7/8's ALE switches are untouched
        // and the WRITE body completes normally.
        _port.SwallowFirstAfter("DI 0 99", "ALE");
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.True(_port.Swallowed, "the verify's ALE switch went through");
        Assert.Equal(SessionPhase.Ready, _session.Phase);     // …and the radio never went away

        // The restore ran, exactly once, on the FILE's values — proven by
        // the mirror below; D23 removed the write's restore line, so its
        // absence is part of the pin.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal));
        Assert.Equal(7, _radio.State.OperatingChannel.Value);
        Assert.Equal(2, _radio.State.Hop.CurrentNet.Value);
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);
    }

    /// <summary>
    /// The same funnel from the other side: the WRITE BODY aborts at a leg,
    /// after the wipe. Leg 11 never runs, so the only net select on the whole
    /// wire is the restore's — which is what makes "exactly once" measurable
    /// here.
    ///
    /// <para>It is also the honest half of A-12's other clause: <b>a restore
    /// that cannot land is the PROBLEM line, never silent</b>. The campaign
    /// stopped at leg 5, so leg 9 never wrote the HOP nets and net 2 does not
    /// exist on the wiped radio — <c>NET 2</c> answers the captured "No Hopset"
    /// with no <c>NET</c> line, so nothing tells the app it moved. The CHANNEL
    /// half still lands, and the line names only what did not.</para>
    /// </summary>
    [Fact]
    public async Task AWriteBodyThatAbortsAfterTheWipe_StillRestores_ExactlyOnce()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.OperatingChannel = 7;
        file.OperatingHopNet = 2;
        _clone.Adopt(file);
        _radio.ModeChangeTimeoutMs = 300;
        _prompt.EnqueueAnswer(true);
        // Leg 5's move to ALE> never lands, so the campaign stops there — with
        // the radio wiped, half-written, and still perfectly reachable.
        _port.SwallowFirstAfter("ZERO", "ALE");
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.True(_port.Swallowed, "leg 5's ALE switch went through");
        Assert.Equal(SessionPhase.Ready, _session.Phase);
        Assert.DoesNotContain(_port.Sent, l => l.StartsWith("DI ", StringComparison.Ordinal));  // no verify ran

        // It RAN — exactly once, and that select is the restore's own.
        Assert.Single(_port.Sent, l => l == "NET 2");
        // The channel landed; the net could not, and the line says so rather
        // than going quiet about it.
        Assert.Equal(7, _radio.State.OperatingChannel.Value);
        Assert.Equal("The radio did not return to net 2 — it has not said where it is.",
            RestoreLine(_clone.Summary));
    }

    /// <summary>…and the funnel's OTHER guard: a campaign refused at preflight
    /// has touched nothing, so it restores nothing. The wipe is the line
    /// (A-12) — not the confirmation, and not entering WriteAsync.</summary>
    [Fact]
    public async Task APreflightRefusal_RestoresNothing_AndSendsNothing()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.HopNetState = CloneDomainState.Faulted;          // unwritable: refused at the door
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(false);                         // must never be consumed
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.Equal(0, _prompt.CallCount);
        Assert.Empty(_port.Sent);
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal)
                || l.StartsWith("The radio did not return to", StringComparison.Ordinal));
    }

    /// <summary>GATE, §3.4's last row: the session goes away MID-restore. The
    /// step that failed has already written the honest line, so the restore adds
    /// none of its own, takes the abort text and returns false. The READ's file
    /// is untouched by it — the legs had already finished.</summary>
    [Fact]
    public async Task ASessionDropMidRestore_AbortsWithoutClaimingAnything()
    {
        ConnectReady();
        await AtChannelAsync(9);
        // `NET 0` is the restore's FIRST command and the read campaign issues no
        // other, so this fires exactly once and exactly there.
        _port.SilenceAndThen("NET 0", () => _session.Close());
        _port.ClearSent();

        await _clone.ReadAsync();

        Assert.Contains("NET 0", _port.Sent);                    // anti-vacuity: it got that far
        Assert.True(WaitUntil(() => _session.Phase != SessionPhase.Ready),
            $"the session never dropped (phase {_session.Phase}): {string.Join(" | ", _clone.Summary)}");
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal)
                || l.StartsWith("The radio did not return to", StringComparison.Ordinal));
        // The step's own honest line stands in its place, and the channel select
        // that would have followed never went out.
        Assert.Contains(_clone.Summary,
            l => l.StartsWith("Operating state:", StringComparison.Ordinal));
        Assert.DoesNotContain("CH 9", _port.Sent);
    }

    // ---- (b) and (d), the write --------------------------------------------

    /// <summary>GATE (b). The write ends on the FILE's operating state, not on
    /// wherever its own verify read left the radio.</summary>
    [Fact]
    public async Task TheWrite_EndsOnTheFilesOperatingState_Silently()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.OperatingChannel = 7;                   // deliberately NOT the net's channel:
        file.OperatingHopNet = 2;                    // the verify's HOP lap moves it to 2
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        Assert.Equal(7, _radio.State.OperatingChannel.Value);
        Assert.Equal(2, _radio.State.Hop.CurrentNet.Value);
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);
        // D23: the write's restore is SILENT — the state above is the proof.
        Assert.DoesNotContain(_clone.Summary,
            l => l.StartsWith("Left the radio on", StringComparison.Ordinal));

        // The restore is an ADDITIONAL closing act (decision A-4): leg 11's own
        // finals still ran, before the verify.
        var sent = _port.Sent.ToList();
        var beforeVerify = sent.Take(sent.FindIndex(
            l => l.StartsWith("DI ", StringComparison.Ordinal))).ToList();
        Assert.Contains("NET 2", beforeVerify);
        Assert.Contains("CH 7", beforeVerify);
    }

    /// <summary>GATE (d), write half. A restore that could not put the radio
    /// back makes the campaign UNCLEAN through the existing problem count — the
    /// operator is told to look at the summary, and nothing is retried.</summary>
    [Fact]
    public async Task ARestoreTheRadioIgnores_MakesTheWriteUnclean()
    {
        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var file = _clone.File!;
        file.OperatingChannel = 7;
        file.OperatingHopNet = 2;
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        // The marker is the VERIFY read's channel dump: leg 11's own `CH 7`
        // goes out long before it and must not be the one that is dropped.
        _port.SwallowFirstAfter("DI 0 99", "CH 7");
        _port.ClearSent();

        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));
        Assert.True(_port.Swallowed, "the restore's channel select went through");

        Assert.Equal(CloneState.Failed, _clone.State);
        Assert.Equal("Write incomplete.", _clone.StatusText);
        Assert.Equal("The radio did not return to channel 07 — it reports channel 02.", RestoreLine(_clone.Summary));
    }

    // ---- (f) leg 11 is untouched -------------------------------------------

    /// <summary>
    /// GATE (f) / invariant I-10 — LEG 11'S BYTES, FROM <c>main</c>.
    ///
    /// <para>Captured from <c>main</c> (8f72d06) before one line of this round
    /// was written, over this exact scenario: the demo read back, its operating
    /// channel set to 7 and its HOP net to 2, written. The restore is an
    /// ADDITIONAL closing act AFTER the verify (decision A-4) — leg 11 itself
    /// does not change, and the verify's exact comparison of all three
    /// operating fields depends on that.</para>
    ///
    /// <para><b>THE WHOLE SLICE, not a prefix</b> (audit round 1). It used to
    /// assert a 13-command prefix and then only reject state-CHANGING tail
    /// commands, so an extra <c>SH</c> added to leg 11 sailed through it. The
    /// assertion is now exact equality over EVERY command from leg 10's last
    /// lockout set to the verify read's channel dump — any command added,
    /// removed or reordered anywhere in that span fails it.</para>
    ///
    /// <para>The far boundary is the dump (<c>DI 0 99</c>) rather than "the
    /// verify's first command", because those two are not distinguishable by
    /// content: leg 11's squelch step and the verify's leg 0 both end
    /// <c>SH</c> + <c>BAT ST</c>. Taking the dump means the slice also carries
    /// the verify's own opening — read-campaign traffic this round does not
    /// touch — which makes the pin STRICTER, not looser.</para>
    /// </summary>
    [Fact]
    public async Task Leg11_SendsExactlyTheBytesItSentOnMain()
    {
        string[] expected =
        [
            // ---- leg 11: net, channel, squelch, (mode — none: the file's is
            //      Ssb and the channel select already stands at SSB>) --------
            "BAT ST",
            "NET 2",
            "BAT ST",
            "SS",
            "SH",
            "BAT ST",
            "CH 7",
            "SH",
            "BAT ST",
            "SH",
            "BAT ST",
            "SQ OFF",
            "BAT ST",
            // ---- the verify read's opening, up to its channel dump ---------
            "SH",
            "BAT ST",
            "SH",
            "UNKEY_M",
            "STEP",
            "RF",
            "BEEP",
            "FMSQ_T",
            "FMTONE",
            "FMDE",
            "PREPOST FILTER",
            "PREPOST RXANTENNA",
            "PREPOST SCAN",
            "CONT",
            "COM",
            "BAT ST",
        ];

        ConnectReady();
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        var file = _clone.File!;
        file.OperatingChannel = 7;
        file.OperatingHopNet = 2;
        _clone.Adopt(file);
        _prompt.EnqueueAnswer(true);
        _port.ClearSent();

        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent.ToList();
        int verify = sent.FindIndex(l => l.StartsWith("DI ", StringComparison.Ordinal));
        Assert.True(verify > 0, "the verify read never started");
        var write = sent.Take(verify).ToList();
        int lastLockout = write.FindLastIndex(l =>
            l.StartsWith("PROGRAM ", StringComparison.Ordinal)
            || l.StartsWith("SELECT ", StringComparison.Ordinal));
        Assert.True(lastLockout > 0, "leg 10 never ran, so leg 11 cannot be delimited");

        // EXACT EQUALITY over the whole slice — not a prefix, so an added
        // command anywhere in it fails here.
        Assert.Equal(expected, write.Skip(lastLockout + 1));
    }
}
