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

/// <summary>
/// A transport fake for the campaign start whose MODE MIRROR IS UNCONFIRMED and
/// whose DISCOVERY SENTINEL IS ANSWERED — the one start branch that spends a
/// sentinel, and the one no other fixture can reach.
///
/// <para><b>Why it takes a fake at all.</b> A session reaches Ready with every
/// mirror unconfirmed only if the radio answers its two init sentinels and
/// says nothing else: the battery answer completes a sentinel, and it is the
/// PROMPT riding alongside that confirms a mode. So the port goes SILENT for
/// the connect (every write dropped, every answer injected by the test) and
/// then answers exactly ONE <c>BAT ST</c> — the campaign's discovery sentinel —
/// with a prompt-carrying battery line of the test's choosing. Everything after
/// that passes through to the demo, so the campaign runs to completion on a
/// real responder.</para>
///
/// <para>The armed answer is NOT forwarded to the demo: two battery answers to
/// one sentinel would make the second a stray (round-15 A0), and this port is
/// standing in for the radio, not adding to it.</para>
/// </summary>
internal sealed class LateModeReportPort : ISerialPort
{
    private readonly RecordingDemoPort _inner;
    private readonly object _lock = new();
    private readonly List<string> _sent = [];
    private readonly List<Timer> _timers = [];
    private bool _silent = true;
    private string? _armedPrompt;

    public LateModeReportPort(RecordingDemoPort inner)
    {
        _inner = inner;
        _inner.DataReceived += (_, e) => DataReceived?.Invoke(this, e);
    }

    /// <summary>Everything the CAMPAIGN wrote, dropped lines included.</summary>
    public IReadOnlyList<string> Sent { get { lock (_lock) return [.. _sent]; } }

    public void ClearSent() { lock (_lock) _sent.Clear(); }

    /// <summary>Anti-vacuity hook: did the armed answer actually go up?</summary>
    public bool AnsweredADiscoverySentinel { get; private set; }

    /// <summary>Arm ONE answer, and open the pass-through behind it: the next
    /// <c>BAT ST</c> is answered by this port at <paramref name="prompt"/> —
    /// which is what confirms the mode — and every write after it reaches the
    /// demo normally.</summary>
    public void AnswerNextSentinelAt(string prompt)
    {
        lock (_lock) { _armedPrompt = prompt; _silent = false; }
    }

    public void Inject(string text)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        string text = Encoding.ASCII.GetString(data.Span).Trim();
        lock (_lock)
        {
            _sent.Add(text);
            if (_armedPrompt is { } prompt && text == "BAT ST")
            {
                _armedPrompt = null;
                AnsweredADiscoverySentinel = true;
                // Posted rather than raised inline: the answer must not re-enter
                // the transport on its own writer thread.
                _timers.Add(new Timer(
                    _ => { if (_inner.IsOpen) Inject("\r\n" + prompt + "\r\nBattery Status FULL 31.4V\r\n"); },
                    null, 5, Timeout.Infinite));
                return Task.CompletedTask;
            }
            if (_silent) return Task.CompletedTask;      // the radio hears nothing
        }
        return _inner.WriteAsync(data, cancellationToken);
    }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public bool IsOpen => _inner.IsOpen;
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);

    public Task CloseAsync() { StopTimers(); return _inner.CloseAsync(); }
    public ValueTask DisposeAsync() { StopTimers(); return _inner.DisposeAsync(); }

    private void StopTimers()
    {
        lock (_lock)
        {
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
    }
}

/// <summary>
/// A transport fake that models THE SCAN DWELL — the one radio behaviour D8's
/// campaign-start ordering exists for, and the one the demo does not model.
///
/// <para>While the scan is running the radio's <c>SH</c> block reports whatever
/// channel the scan is DWELLING on, and it keeps moving; after <c>ST</c> it
/// reports the channel it parked on. So this port rewrites the <c>CHAN nn</c>
/// field of everything the radio says: <see cref="DwellChannel"/> until it sees
/// an <c>ST</c> go past, <see cref="ParkedChannel"/> afterwards. Nothing else
/// is touched — the rewrite is one field of one line, at the byte seam, which
/// is where a fake belongs.</para>
///
/// <para>ACCEPTED LIMITATION: a <c>CHAN nn</c> token split across two reads
/// would not be rewritten. The demo writes a whole block per answer, so it
/// never is; a test that stopped seeing the dwell would fail LOUDLY on its
/// anti-vacuity assertion rather than pass quietly.</para>
/// </summary>
internal sealed class ScanDwellPort : ISerialPort
{
    private static readonly Regex ChanField = new(@"CHAN \d\d", RegexOptions.Compiled);

    private readonly RecordingDemoPort _inner;
    private volatile bool _stopped;

    public ScanDwellPort(RecordingDemoPort inner)
    {
        _inner = inner;
        _inner.DataReceived += (_, e) => DataReceived?.Invoke(this, Rewrite(e));
    }

    /// <summary>What the SH block reports while the scan owns the channel.</summary>
    public int DwellChannel { get; init; } = 21;

    /// <summary>What it reports once the campaign has stopped the scan.</summary>
    public int ParkedChannel { get; init; } = 11;

    /// <summary>Anti-vacuity hook: did an <c>ST</c> actually go past?</summary>
    public bool SawStop => _stopped;

    /// <summary>Push bytes up as if the radio had said them — the only way to
    /// put an ANNOUNCED-only fact (its own <c>SCANNING</c> line) into the
    /// mirror, since the demo models no scan.</summary>
    public void Inject(string text)
        => DataReceived?.Invoke(this, new SerialDataEventArgs(Encoding.ASCII.GetBytes(text)));

    private SerialDataEventArgs Rewrite(SerialDataEventArgs e)
    {
        var text = Encoding.ASCII.GetString(e.Data);
        if (!ChanField.IsMatch(text)) return e;
        int channel = _stopped ? ParkedChannel : DwellChannel;
        return new SerialDataEventArgs(Encoding.ASCII.GetBytes(
            ChanField.Replace(text, "CHAN " + channel.ToString("00", System.Globalization.CultureInfo.InvariantCulture))));
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Encoding.ASCII.GetString(data.Span).Trim() == "ST") _stopped = true;
        return _inner.WriteAsync(data, cancellationToken);
    }

    public event EventHandler<SerialDataEventArgs>? DataReceived;
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public bool IsOpen => _inner.IsOpen;
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync() => _inner.GetAvailablePortsAsync();
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => _inner.GetAvailablePortsPassiveAsync();
    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(settings, cancellationToken);
    public Task CloseAsync() => _inner.CloseAsync();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// SCAN DOCTRINE v2 — D8 (plan-clone-write-structural.md §5.4c, §6 pins
/// (a)-(j), invariant I-11).
///
/// <para><b>The rule.</b> No campaign leg runs at the ALE prompt with the scan
/// running. The unit of enforcement is the ALE OCCUPANCY — a maximal run of
/// consecutive campaign legs at <c>ALE&gt;</c> with no intervening mode switch
/// — and every occupancy is preceded by an UNCONDITIONAL judged <c>ST</c>. The
/// one exemption is the write's PRE-ZEROIZE occupancy, where <c>ZERO</c> is
/// still the campaign's first wire command and the wipe itself is the stop.</para>
///
/// <para><b>Why per-occupancy and not once.</b> Auto-scan resume is
/// ENTRY-scoped (protocol.md, ALE section): a filled radio restarts its own
/// scan at every fresh ALE entry. The 2026-08-29 field read console caught it
/// three times in one campaign (09:54:04, :20, :41 — three entries, three
/// <c>SCANNING</c> announcements), which is why round 14's single stop before
/// the messages leg could not cover the read campaign, the verify lap or the
/// restore lap.</para>
///
/// <para><b>Why the stop precedes the found-state snapshot.</b> A running scan
/// OWNS the operating channel, so a read campaign that snapshots first captures
/// a scan DWELL and its own closing restore then faithfully puts back a number
/// the operator never chose (same console: the restored <c>CH 11</c> confirmed
/// twice, then the resumed scan on <c>CHAN 21</c>).</para>
///
/// <para>Every pin here drives the production stack over the demo responder
/// through a RECORDING port (D7/I-1: no demo-radio development — the demo is a
/// transport fake here, never edited). The link states the demo does not model
/// are INJECTED as the radio's own announced lines.</para>
/// </summary>
public sealed class CloneScanDoctrineTests : IDisposable
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
    private readonly ModeSurface _modes;
    private readonly AleSurface _ale;

    public CloneScanDoctrineTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new DeferredModeEntryPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _modes = new ModeSurface(_radio);
        _ale = new AleSurface(_radio);
        _clone = new CloneService(
            _radio, _session, _prompt,
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            _ale, new HopSurface(_radio), new ChannelSurface(_radio),
            new ModemSurface(_radio), _modes, new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
        };
        _transport.GateTimeoutMs = 150;
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }

    // ---- Fixture helpers ---------------------------------------------------

    private void ConnectReady()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready) Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);
    }

    /// <summary>Put the radio at a prompt BEFORE any campaign runs — the
    /// "found in" state D8's start sequence is about.</summary>
    private async Task FoundInAsync(OperatingMode mode)
    {
        _modes.Select(mode);
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(_modes.Mode.IsConfirmed && _modes.Mode.Value == mode))
            await Task.Delay(5);
        Assert.True(_modes.Mode.IsConfirmed && _modes.Mode.Value == mode,
            $"the radio never confirmed {mode}");
    }

    /// <summary>Put an ANNOUNCED-only fact into the link mirror. The line is
    /// bare — no prompt prefix — precisely so it moves the link state and
    /// nothing else; the demo models no scan of its own.</summary>
    private async Task AnnounceAsync(string line, AleLinkState expected)
    {
        _port.Inject("\r\n" + line + "\r\n");
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(_ale.LinkState.IsConfirmed && _ale.LinkState.Value == expected))
            await Task.Delay(5);
        Assert.True(_ale.LinkState.IsConfirmed && _ale.LinkState.Value == expected,
            "the announced link state never reached the mirror");
    }

    private Task AnnounceScanningAsync() => AnnounceAsync("SCANNING", AleLinkState.Scanning);

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

    /// <summary>Read a file the write campaign can replay, from a radio found in
    /// SSB — so the read's own scan lifetime cannot colour the write's.</summary>
    private async Task ReadAFileAsync()
    {
        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));
        Assert.Empty(_clone.File!.IncompleteDomains);
    }

    // ======================= (a) the READ campaign stops =====================

    /// <summary>
    /// <b>PIN (a).</b> A READ campaign against a radio scripted <c>SCANNING</c>
    /// stops the scan before the book leg's first read.
    ///
    /// <para>This INVERTS the deleted round-14 pin
    /// <c>TheReadCampaign_NeverStopsTheScan_EvenWhileScanning</c>, deliberately
    /// (§6 pin (g)). That pin's reasoning — one field transcript in which the
    /// book answered while <c>SCANNING</c> was live — is not the doctrine any
    /// more: D8 says a clone campaign owns the radio for its duration.</para>
    /// </summary>
    [Fact]
    public async Task TheReadCampaign_StopsTheScan_BeforeTheBookLegsFirstRead()
    {
        ConnectReady();
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        int stop = IndexOf(sent, l => l == "ST");
        int book = IndexOf(sent, l => l == "SLFAD");
        Assert.True(stop >= 0, "the read campaign sent no stop at all");
        Assert.True(book > stop, "the book leg's first read did not follow a stop");
    }

    // ======================= (b) the VERIFY lap stops ========================

    /// <summary>
    /// <b>PIN (b).</b> The write's nested VERIFY is a read campaign that
    /// re-enters ALE after every one of the write's own occupancies. Its ALE
    /// entry issues its OWN stop, and no <c>SCA</c> appears before its book
    /// reads — which is exactly why the restart moved out of the channel-groups
    /// leg and into the closing-restore funnel.
    /// </summary>
    [Fact]
    public async Task TheVerifyLap_StopsTheScanAtItsOwnAleEntry_AndNoRestartPrecedesItsBookReads()
    {
        ConnectReady();
        await ReadAFileAsync();
        await AnnounceScanningAsync();

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        // The verify begins at the channel DUMP — the write leg writes channels
        // with `CH nn`/`RXF`, never `DI 0 99`.
        int verifyStart = IndexOf(sent, l => l == "DI 0 99");
        Assert.True(verifyStart > 0, "the verify's channel dump never went out");

        var verify = sent.Skip(verifyStart).ToList();
        int stop = IndexOf(verify, l => l == "ST");
        int book = IndexOf(verify, l => l == "SLFAD");
        Assert.True(stop >= 0, "the verify lap sent no stop of its own");
        Assert.True(book > stop, "the verify's book reads did not follow its own stop");

        int restart = IndexOf(verify, l => l == "SCA");
        Assert.True(restart < 0 || restart > book,
            "a scan restart went out before the verify's book reads");
    }

    // ======================= (c) the restart at the END ======================

    /// <summary>
    /// <b>PIN (c) and matrix row "standalone read, success".</b> A read campaign
    /// that FOUND the radio scanning ends with exactly one <c>SCA</c>, after the
    /// restore lap, and says so exactly once.
    /// </summary>
    [Fact]
    public async Task AFoundScanningReadCampaign_RestartsOnce_AfterTheRestoreLap_AndSaysSoOnce()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        Assert.Single(sent, l => l == "SCA");

        // AFTER THE RESTORE LAP: the funnel's own read-back `SH` is the last
        // thing the restore does, and the restart follows it.
        int restart = IndexOf(sent, l => l == "SCA");
        int lastStatus = LastIndexOf(sent, l => l == "SH");
        Assert.True(restart > lastStatus,
            "the restart did not follow the closing restore's read-back");

        Assert.Single(_clone.Summary, s => s == CloneService.ScanRestartedNotice);
    }

    // ======================= (d) UNCONDITIONAL ==============================

    /// <summary>
    /// <b>PIN (d) — the ruling's teeth.</b> The radio's mirror says
    /// <c>LINKED</c>, which under round 14's R13(b) branch meant "send nothing,
    /// an <c>ST</c> would end an exchange nobody asked to end". The owner
    /// RETIRED that branch for campaigns on 2026-08-29: a clone campaign owns
    /// the radio for its duration, so the stop goes out regardless of what the
    /// mirror says. The mirror is consulted for NOTHING on the stop side.
    ///
    /// <para>This replaces the deleted
    /// <c>TheAleWriteLegs_SendNoStop_WhenTheRadioSaidItWasNotScanning</c>
    /// (§6 pin (g)), pinned POSITIVELY so the ruling cannot be quietly
    /// reverted.</para>
    /// </summary>
    [Fact]
    public async Task TheOccupancyStop_GoesOutUnconditionally_EvenWhenTheMirrorSaysLinked()
    {
        ConnectReady();
        await AnnounceAsync("LINKED", AleLinkState.Linked);
        Assert.Equal(AleLinkState.Linked, _ale.LinkState.Value);
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        int stop = IndexOf(sent, l => l == "ST");
        int book = IndexOf(sent, l => l == "SLFAD");
        Assert.True(stop >= 0, "the mirror's Linked state suppressed the stop — D8 says it must not");
        Assert.True(book > stop, "the book leg's first read did not follow the stop");

        // …and NOTHING is claimed about a scan that was never running: the
        // notice reports an outcome, and the licence was never taken.
        Assert.DoesNotContain(CloneService.ScanStoppedNotice, _clone.Summary);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
        Assert.DoesNotContain("SCA", sent);
    }

    // ======================= (e) OCCUPANCY DEDUP ============================

    /// <summary>
    /// <b>PIN (e).</b> The read campaign's ALE occupancy covers the messages,
    /// book, group and schedule legs with no mode switch between them. They get
    /// ONE stop between them, not one each: the dedup is keyed off the mode
    /// surface's confirmed transitions, never off a leg count.
    /// </summary>
    [Fact]
    public async Task ConsecutiveAleLegs_ShareOneOccupancy_AndGetExactlyOneStop()
    {
        ConnectReady();
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        int aleEntry = IndexOf(sent, l => l == "ALE");
        int leaveAle = IndexOf(sent, l => l == "HO");
        Assert.True(aleEntry >= 0 && leaveAle > aleEntry,
            "the campaign never entered and left ALE");

        // ANTI-VACUITY: the occupancy really did carry several ALE legs.
        var occupancy = sent.Skip(aleEntry).Take(leaveAle - aleEntry).ToList();
        Assert.Contains("SLFAD", occupancy);       // the book leg
        Assert.Contains("TXMSG", occupancy);       // the stored-messages leg
        Assert.Contains("CHG 0", occupancy);       // the channel-group leg

        Assert.Single(occupancy, l => l == "ST");
    }

    // ======================= (f) NOTICES AT MOST ONCE =======================

    /// <summary>
    /// <b>PIN (f).</b> A found-scanning campaign stops the scan at EVERY
    /// occupancy — the book lap, the closing restore's ALE entry — but the
    /// summary carries each notice at most once. Repeat occupancy stops are
    /// Console-only wire traffic, not new summary lines.
    /// </summary>
    [Fact]
    public async Task TheScanNotices_AppearAtMostOncePerCampaign_HoweverManyOccupanciesStopped()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // ANTI-VACUITY: more than one occupancy really was stopped.
        Assert.True(_recorder.Sent.Count(l => l == "ST") > 1,
            "only one occupancy stopped, so 'at most once' proves nothing here");

        Assert.Single(_clone.Summary, s => s == CloneService.ScanStoppedNotice);
        Assert.Single(_clone.Summary, s => s == CloneService.ScanRestartedNotice);
    }

    // ======================= (h) CAMPAIGN-START ORDERING ====================

    /// <summary>
    /// <b>PIN (h), READ half.</b> A read campaign that finds the radio in ALE
    /// stops the scan BEFORE it reads the operating state — so the channel it
    /// stores is the POST-STOP one rather than a scan dwell.
    ///
    /// <para>The wire order IS the claim. Against the pre-D8 code the first
    /// line after the campaign started was the operating-state <c>SH</c>, with
    /// no <c>ST</c> anywhere before it; the found snapshot therefore preceded
    /// any stop, which is the defect the 2026-08-29 console showed.</para>
    ///
    /// <para><b>No discovery sentinel here</b> (audit round 1). The mode mirror
    /// is already CONFIRMED ALE, which is the only question the discovery
    /// sentinel exists to answer, so asking it would be wire spent for nothing.
    /// The unconfirmed-mirror branch that does spend it is pinned by
    /// <c>CloneClosingRestoreTests.NothingCapturedOnAReadySession_…</c>.</para>
    /// </summary>
    [Fact]
    public async Task TheReadCampaignStart_FoundInAleScanning_StopsBeforeItReadsTheOperatingState()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // The stop, the stop's own judged sentinel, and THEN the operating-state
        // read. Nothing between them, and nothing before them.
        Assert.Equal(
            ["ST", "BAT ST", "SH"],
            [.. _recorder.Sent.Take(3)]);

        // The found snapshot recorded SCANNING — proved by the end restart,
        // which is licensed by nothing else.
        Assert.Contains("SCA", _recorder.Sent);
        Assert.Contains(CloneService.ScanRestartedNotice, _clone.Summary);

        // …and the campaign really was found in ALE, so this is the branch the
        // start sequence exists for.
        Assert.Equal("Ale", _clone.File!.OperatingMode);
    }

    /// <summary>
    /// <b>PIN (h), THE SSB/HOP HALF — the start is UNTOUCHED</b> (audit round 1
    /// BLOCKER; plan §5.4c: "a campaign found in SSB/HOP is untouched at
    /// start").
    ///
    /// <para><b>Why this needs its own pin.</b> The first version of D8 ran the
    /// discovery sentinel UNCONDITIONALLY, which put an extra <c>BAT ST</c> on
    /// the commonest start there is. That is not merely wasteful: leg 0's
    /// budget is the campaign's first timeout, so on a marginal radio the extra
    /// sentinel moves WHERE the campaign gives up — a start that used to abort
    /// on the operating-state read would abort one step earlier, under a
    /// different summary line. The wire prefix here is therefore pinned to be
    /// byte-identical to the pre-D8 campaign: the operating-state <c>SH</c>
    /// first, its sentinel second, and no scan traffic at all before them.</para>
    /// </summary>
    [Fact]
    public async Task TheReadCampaignStart_FoundInSsb_SendsNoDiscoverySentinelAndNoStop()
    {
        ConnectReady();
        // ANTI-VACUITY: the mode really is confirmed, and it is not ALE — this
        // is the branch that must add nothing.
        Assert.True(_modes.Mode.IsConfirmed);
        Assert.Equal(OperatingMode.Ssb, _modes.Mode.Value);
        // …and the radio is announcing SCANNING, so a stop would fire if the
        // branch order were wrong about anything except the mode.
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        // THE PRE-D8 PREFIX, verbatim: leg 0's `SH` and its sentinel.
        Assert.Equal(["SH", "BAT ST"], [.. sent.Take(2)]);

        // No stop precedes the operating-state read…
        int firstStop = IndexOf(sent, l => l == "ST");
        Assert.True(firstStop > 1, "a scan stop went out at an SSB campaign start");
        // …and the one that DOES come later is the ALE book leg's own occupancy
        // stop, which is the funnel's job and not this method's.
        int aleEntry = IndexOf(sent, l => l == "ALE");
        Assert.True(aleEntry >= 0 && firstStop > aleEntry,
            "the first stop did not belong to an ALE occupancy");

        // The found licence was never taken — found in SSB means no restart.
        Assert.DoesNotContain("SCA", sent);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// <b>PIN (h), WRITE half — THE PRE-ZEROIZE EXEMPTION</b> (owner ruling
    /// 2026-08-29, resolving critic c2p1 F1). Found in ALE, scanning, and
    /// <c>ZERO</c> is STILL the write campaign's first wire command: no
    /// discovery sentinel, no <c>ST</c>. The wipe itself is the stop, and the
    /// restart licence is a MIRROR read that costs no wire — which is proved
    /// below by the stopped notice appearing at the first ALE occupancy AFTER
    /// leg 2, where D8 does apply.
    ///
    /// <para>The byte pin
    /// <c>TheWriteCampaign_SendsZEROAsItsVeryFirstCommand_FromAnyPrompt</c> is
    /// unchanged and still owns the general rule; this one adds the
    /// found-in-ALE-scanning case specifically.</para>
    /// </summary>
    [Fact]
    public async Task TheWriteCampaignStart_FoundInAleScanning_StillSendsZeroFirst_AndNoStopPrecedesIt()
    {
        ConnectReady();
        await ReadAFileAsync();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        Assert.Equal("ZERO", sent[0]);
        int zero = 0;
        int stop = IndexOf(sent, l => l == "ST");
        Assert.True(stop > zero, "a stop went out before the wipe — the pre-ZERO exemption is gone");

        // The licence WAS taken from the mirror, without wire: the first
        // post-wipe ALE occupancy's stop is a LICENSED one and says so.
        Assert.Contains(CloneService.ScanStoppedNotice, _clone.Summary);
    }

    // ======================= (i) THE STOP BRACKET'S LEDGER ==================

    /// <summary>
    /// <b>PIN (i).</b> The <c>ST</c> answers (<c>KEY OFF</c>,
    /// <c>SCAN STOPPED</c>) touch only the link mirror: they are not sentinel
    /// answers, they credit nothing, and they leave the ping ledger exactly
    /// where it was. The <c>Battery Status</c> that follows is the sole answer
    /// to the stop's own sentinel — and both counters are ZERO before the first
    /// gated operation of the book leg.
    ///
    /// <para>This is what makes the round-15 A0 stray rule and the D8 stop
    /// bracket compatible: an unconditional <c>ST</c> at every occupancy adds
    /// no debt for the ALE programming gate to fault on.</para>
    /// </summary>
    [Fact]
    public async Task TheStopBracketsAnswers_LeaveThePingLedgerUntouched_AndTheGateStartsAtZero()
    {
        ConnectReady();
        await AnnounceScanningAsync();

        // THE DIRECT HALF: the two answer lines, injected while the ledger is
        // quiet, move nothing.
        Assert.Equal(0, _radio.PendingPingCount);
        int debtBefore = _radio.PingAnswerDebt;
        _port.Inject("\r\nKEY OFF\r\nSCAN STOPPED\r\n");
        await Task.Delay(50);
        Assert.Equal(0, _radio.PendingPingCount);
        Assert.Equal(debtBefore, _radio.PingAnswerDebt);

        // THE CAMPAIGN HALF: sampled at every received line for a whole write
        // campaign — stops, sentinels, gated book operations and all.
        await ReadAFileAsync();
        int worstDebt = 0;
        int? pendingAtFirstGatedOp = null;
        int? debtAtFirstGatedOp = null;
        _transport.LineWritten += (_, e) =>
        {
            // `SLFAD <ADDR> <group>` is the gated STORE; a bare `SLFAD` is the
            // listing read the gate brackets it with.
            if (e.Line.StartsWith("SLFAD ", StringComparison.Ordinal)
                && pendingAtFirstGatedOp is null)
            {
                pendingAtFirstGatedOp = _radio.PendingPingCount;
                debtAtFirstGatedOp = _radio.PingAnswerDebt;
            }
        };
        _transport.LineReceived += (_, _) =>
        {
            int debt = _radio.PingAnswerDebt;
            if (debt > worstDebt) worstDebt = debt;
        };

        _prompt.EnqueueAnswer(true);
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        Assert.Equal(0, worstDebt);
        Assert.Equal(0, _radio.PingAnswerDebt);
        Assert.Equal(0, _radio.PendingPingCount);

        // ANTI-VACUITY: a gated book operation really was written, and the
        // ledger really was read at that moment.
        Assert.NotNull(pendingAtFirstGatedOp);
        Assert.Equal(0, debtAtFirstGatedOp);
    }

    // ======================= (j) THE RESTART MATRIX =========================
    //
    // One pin per §5.4c matrix row. The rows "standalone read, success" and
    // "file mode SSB/HOP → no attempt" live elsewhere and are named here so the
    // matrix reads as one table:
    //   * standalone read, success → AFoundScanningReadCampaign_RestartsOnce_…
    //   * write, file mode SSB → CloneRound14FieldHardeningTests
    //       .TheAleWriteLegs_StopTheScan_AndClaimNoRestart_WhenTheFileEndsOutsideAle

    // MATRIX: abort BEFORE the campaign-start stop ran → the licence is never
    // taken and the funnel makes no attempt. With the corrected branch order
    // (audit round 1) that row has exactly ONE instance — a start whose mode
    // mirror is UNCONFIRMED, so the discovery sentinel leads and a radio that
    // answers nothing aborts on it. That needs a Ready session with every
    // mirror unconfirmed, which is `CloneClosingRestoreTests`' fixture, and the
    // row is pinned there:
    //   NothingCapturedOnAReadySession_SaysSoOnce_AndSendsNothing.
    // (A confirmed-SSB start never reaches a stop to abort before, and a
    // confirmed-ALE start's first wire act IS the stop.)

    /// <summary>
    /// <b>MATRIX: session drop.</b> Nothing reaches the wire and nothing is
    /// claimed — the closing-restore funnel's Ready guard is what stops the
    /// restart, exactly as it stops the operating-state restore.
    /// </summary>
    [Fact]
    public async Task ASessionDrop_MakesNoRestartAttempt_AndClaimsNothing()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();
        _recorder.ClearSent();
        _session.Close();

        Assert.False(await _clone.ReadAsync());

        Assert.DoesNotContain("SCA", _recorder.Sent);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// <b>MATRIX: the funnel ends short of ALE.</b> A read campaign found in
    /// SSB restores to SSB, so the mode surface is not confirming ALE at the
    /// funnel's end and no <c>SCA</c> is attempted — even though the campaign
    /// did stop a scan at its ALE legs. <c>SCA</c> only ever goes out at
    /// <c>ALE&gt;</c>.
    /// </summary>
    [Fact]
    public async Task ACampaignEndingOutsideAle_StopsTheScan_ButAttemptsNoRestart()
    {
        ConnectReady();
        await AnnounceScanningAsync();
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // Found in SSB, restored to SSB…
        Assert.Equal("Ssb", _clone.File!.OperatingMode);
        // …the ALE legs still stopped the scan…
        Assert.Contains("ST", _recorder.Sent);
        // …and no restart was attempted or claimed.
        Assert.DoesNotContain("SCA", _recorder.Sent);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// <b>MATRIX: the write's file mode is ALE → attempt.</b> The write
    /// campaign restores the FILE's recorded state, so a file captured in ALE
    /// ends at <c>ALE&gt;</c> and the licence fires there.
    ///
    /// <para>And the NOTICE half of the same row: this campaign's <c>ZERO</c>
    /// reset every mirror, so nothing confirms <c>Scanning</c> inside the
    /// restart's own sentinel bracket. The attempt is MADE — the operator's
    /// Console carries the <c>SCA</c> — and NOTHING is claimed. That is the
    /// same shape as an <c>SCA</c> the radio refuses for an incomplete fill.</para>
    /// </summary>
    [Fact]
    public async Task AWriteWhoseFileEndsInAle_AttemptsTheRestart_AndClaimsNothingUnconfirmed()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await ReadAFileAsync();
        Assert.Equal("Ale", _clone.File!.OperatingMode);
        await AnnounceScanningAsync();

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        // THE ATTEMPT: exactly one, at the very end.
        Assert.Single(_recorder.Sent, l => l == "SCA");
        int restart = IndexOf(_recorder.Sent, l => l == "SCA");
        int lastStatus = LastIndexOf(_recorder.Sent, l => l == "SH");
        Assert.True(restart > lastStatus, "the restart did not follow the closing restore");

        // THE NOTICE: none — the wipe left the link mirror unconfirmed, so no
        // outcome was observed and none is reported.
        Assert.False(_ale.LinkState.IsConfirmed && _ale.LinkState.Value == AleLinkState.Scanning);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// <b>MATRIX: the nested VERIFY is not a campaign end.</b> It makes no
    /// restart attempt of its own and it does not destroy the outer write's
    /// licence — the scan context is created only by the two public campaign
    /// entries, and the verify SHARES the write's.
    ///
    /// <para>Proved by counting: exactly ONE <c>SCA</c> in the whole write, and
    /// it falls after the verify's last book read rather than inside it.</para>
    /// </summary>
    [Fact]
    public async Task TheVerifyInsideAWrite_MakesNoRestartAttempt_AndTheOuterLicenceSurvivesIt()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await ReadAFileAsync();
        await AnnounceScanningAsync();

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.True(await _clone.WriteAsync(CloneSwapTests.Rows()), string.Join(" | ", _clone.Summary));

        var sent = _recorder.Sent;
        Assert.Single(sent, l => l == "SCA");

        // The verify's own ALE occupancy is the LAST `SLFAD` of the run (the
        // write leg issues `SLFAD STO`, never a bare listing read after it).
        int lastVerifyBookRead = LastIndexOf(sent, l => l == "SLFAD");
        int restart = IndexOf(sent, l => l == "SCA");
        Assert.True(lastVerifyBookRead > 0, "the verify never read the book");
        Assert.True(restart > lastVerifyBookRead,
            "the restart landed inside the verify — the verify is not a campaign end");

        // …and the outer licence really did survive the verify: it is the only
        // thing that could have licensed that `SCA`.
        Assert.True(restart > 0);
    }

    /// <summary>
    /// <b>MATRIX: a WRITE LEG ABORTS.</b> The funnel runs on every exit (A-12),
    /// so an aborted write still restores the file's operating state — and the
    /// restart is attempted there iff the four conditions hold, which they do:
    /// the licence was taken from the mirror before <c>ZERO</c>, the scan was
    /// stopped at the messages leg, and the file's mode is ALE.
    ///
    /// <para><b>This is the pin that kills the "skip the restart on a failed
    /// campaign" mutation</b> (audit round 1). <c>Aborted()</c> drives
    /// <c>State</c> to <c>Failed</c> BEFORE the funnel runs, so an
    /// implementation that keyed the restart on a clean outcome would leave a
    /// radio the campaign deliberately stopped scanning — silently, and exactly
    /// on the runs where the operator is least able to check. A licensed abort
    /// must still put the scan back.</para>
    ///
    /// <para>It carries the NOTICE half of the refused-<c>SCA</c> row too: the
    /// wipe reset the link mirror, so nothing confirms <c>Scanning</c> inside
    /// the restart's bracket. The attempt is on the wire and in the Console;
    /// the summary claims nothing.</para>
    ///
    /// <para>The abort is scripted at leg 6's SSB settings lap: the mode
    /// command is let through, its closing sentinel is swallowed, and the leg
    /// takes its existing abort path.</para>
    /// </summary>
    [Fact]
    public async Task AWriteThatAbortsMidLeg_StillAttemptsTheRestart_AndClaimsNothing()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await ReadAFileAsync();
        Assert.Equal("Ale", _clone.File!.OperatingMode);
        await AnnounceScanningAsync();

        // Leg 6's SSB lap is the write's first `SS`: released, then its judged
        // sentinel swallowed.
        _clone.SentinelTimeoutMs = 400;
        _port.Defer("SS", lifecycleMs: 0, swallowAfterRelease: "BAT ST");

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        // ANTI-VACUITY: the campaign really did abort, and really did abort at
        // a LEG rather than in the preflight.
        Assert.True(_port.Swallowed, "no sentinel was swallowed, so nothing was tested");
        Assert.Equal(CloneState.Failed, _clone.State);
        Assert.Contains(_clone.Summary,
            s => s == "Settings: the radio stopped answering during this step.");
        // …and the licence really was held: the scan was stopped for programming.
        Assert.Contains(CloneService.ScanStoppedNotice, _clone.Summary);

        // THE PIN: the funnel still attempted the restart.
        Assert.Single(_recorder.Sent, l => l == "SCA");

        // …and claimed nothing, because the wipe left the link mirror with
        // nothing to confirm.
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
    }

    /// <summary>
    /// <b>MATRIX: the VERIFY lap aborts.</b> Same rule from the other side —
    /// the verify is not a campaign end and makes no attempt of its own, but
    /// its failure still exits through the write's funnel, where the attempt is
    /// made. <c>State</c> is <c>Failed</c> here too ("Verification stopped
    /// early"), so this row kills the same mutation independently.
    ///
    /// <para>Scripted by RE-ARMING the port once the verify's channel dump has
    /// gone out — <c>DI 0 99</c> is a verify-only command, so it is an
    /// unambiguous marker for "the write legs are finished".</para>
    /// </summary>
    [Fact]
    public async Task AVerifyThatAborts_StillAttemptsTheRestart_AtTheWritesFunnel()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await ReadAFileAsync();
        await AnnounceScanningAsync();

        _clone.SentinelTimeoutMs = 400;
        bool armed = false;
        _transport.LineWritten += (_, e) =>
        {
            // The verify's own HOP leg: let `HO` through, swallow the judged
            // sentinel behind it, and the read campaign takes its abort path.
            if (e.Line == "DI 0 99" && !armed)
            {
                armed = true;
                _port.Defer("HO", lifecycleMs: 0, swallowAfterRelease: "BAT ST");
            }
        };

        _prompt.EnqueueAnswer(true);
        _recorder.ClearSent();
        Assert.False(await _clone.WriteAsync(CloneSwapTests.Rows()));

        Assert.True(armed, "the verify never ran, so nothing was tested");
        Assert.True(_port.Swallowed, "no sentinel was swallowed, so nothing was tested");
        Assert.Equal(CloneState.Failed, _clone.State);

        // THE PIN: the write's funnel still attempted the restart.
        Assert.Single(_recorder.Sent, l => l == "SCA");
    }

    /// <summary>
    /// <b>MATRIX: the read-back DISAGREES, after the mode confirmed ALE.</b>
    /// The attempt is still made. §5.4c is explicit about why: the scan owns
    /// the operating channel once it is running, so a channel diff is neither
    /// evidence against the restart nor the restart's business — the funnel's
    /// own line reports it.
    ///
    /// <para>Scripted by holding the restore's <c>CH nn</c> forever: the radio
    /// takes the select and stays where it was, so the closing read-back finds
    /// a channel that disagrees with what was asked.</para>
    /// </summary>
    [Fact]
    public async Task AChannelReadBackDisagreement_DoesNotStopTheRestart()
    {
        ConnectReady();
        await FoundInAsync(OperatingMode.Ale);
        await AnnounceScanningAsync();

        // The channel the closing restore will try to put back is the one the
        // campaign captured; hold exactly that select.
        var probe = new ChannelSurface(_radio);
        Assert.True(probe.Current.IsConfirmed, "the radio never reported a channel");
        int held = probe.Current.Value;
        _port.Defer("CH " + held.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lifecycleMs: null);
        _recorder.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        // ANTI-VACUITY: the select really was withheld, and the restore really
        // did report the disagreement in its own line.
        Assert.DoesNotContain("CH " + held.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _recorder.Sent);

        // THE PIN: the restart happened anyway.
        Assert.Single(_recorder.Sent, l => l == "SCA");
        Assert.Contains(CloneService.ScanRestartedNotice, _clone.Summary);
    }
}

/// <summary>
/// PIN (h)'s THIRD START BRANCH (audit round 2): the mode mirror is
/// UNCONFIRMED, the discovery sentinel IS ANSWERED, and its prompt names a
/// NON-ALE mode — so the sequence must do nothing further.
///
/// <para><b>Why this needed its own fixture.</b> The other two branches are
/// covered elsewhere: a confirmed SSB/HOP start never spends a sentinel
/// (<c>CloneScanDoctrineTests.TheReadCampaignStart_FoundInSsb_…</c>) and a
/// discovery sentinel that gets NO answer aborts the campaign
/// (<c>CloneClosingRestoreTests.NothingCapturedOnAReadySession_…</c>). Neither
/// reaches the line that decides what to do once the discovery sentinel has
/// come back. Deleting that decision — treating a confirmed SSB or HOP exactly
/// like ALE — survived the whole App suite, which is what this fixture
/// closes.</para>
///
/// <para>Reaching the branch takes a radio that answers its two init sentinels
/// and says nothing else, then answers exactly one more. See
/// <see cref="LateModeReportPort"/>.</para>
/// </summary>
public sealed class CloneUnconfirmedStartTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly LateModeReportPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly CloneService _clone;
    private readonly AleSurface _ale;

    public CloneUnconfirmedStartTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new LateModeReportPort(_recorder);
        _transport = new SerialTransport(_port) { OpenSettleMs = 0, GateTimeoutMs = 150 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _ale = new AleSurface(_radio);
        _clone = new CloneService(
            _radio, _session, new FakeConfirmationPrompt(),
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            _ale, new HopSurface(_radio), new ChannelSurface(_radio),
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

    private static bool WaitUntil(Func<bool> condition, int budgetMs = 5_000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <summary>Ready, with every mirror still unconfirmed: the init sentinels
    /// are completed by BARE battery answers, and only a PROMPT confirms a
    /// mode. Each answer is injected only once its own <c>BAT ST</c> has gone
    /// out — a battery line the app has not asked for is a stray (round-15 A0)
    /// and completes nothing.</summary>
    private void ConnectReadyWithNothingConfirmed()
    {
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
    }

    /// <summary>
    /// <b>THE PIN.</b> The campaign opens with its one discovery sentinel; the
    /// radio answers it at a NON-ALE prompt; and from there the start sequence
    /// is finished. The next thing on the wire is leg 0's operating-state
    /// <c>SH</c> — byte-identical to the legacy start — with no <c>ST</c>
    /// between them, no <c>ST</c> at all until a genuine ALE occupancy much
    /// later, and no restart licence taken.
    ///
    /// <para><b>What goes red.</b> Deleting the post-discovery non-ALE return
    /// treats a confirmed SSB or HOP as if it were ALE: an <c>ST</c> lands
    /// immediately behind the discovery sentinel, and — because this radio is
    /// announcing <c>SCANNING</c> — the snapshot arms a restart licence that
    /// fires an <c>SCA</c> at the campaign's end. Both are asserted, so the
    /// mutation dies twice.</para>
    ///
    /// <para>The <c>HOP&gt;</c> case is the same branch with a different value:
    /// the radio names HOP at the discovery sentinel, and leg 0's own <c>SH</c>
    /// then re-confirms whatever the demo is really at. Only the start decision
    /// is under test.</para>
    /// </summary>
    [Theory]
    [InlineData("SSB>")]
    [InlineData("HOP>")]
    public async Task AnUnconfirmedStart_WhoseDiscoverySentinelNamesANonAleMode_DoesNothingFurther(string prompt)
    {
        ConnectReadyWithNothingConfirmed();

        // ANTI-VACUITY 1: Ready, and the radio has still never said where it is.
        // This is the ONLY branch of the start sequence that spends a sentinel.
        Assert.False(_radio.State.OperatingMode.IsConfirmed);

        // The radio IS announcing a running scan, so a wrongly-taken snapshot
        // would arm a real restart licence rather than an empty one.
        _port.Inject("\r\nSCANNING\r\n");
        Assert.True(WaitUntil(() => _ale.LinkState.IsConfirmed
                && _ale.LinkState.Value == AleLinkState.Scanning),
            "the announced link state never reached the mirror");

        _port.AnswerNextSentinelAt(prompt);
        _port.ClearSent();

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        var sent = _port.Sent;

        // ANTI-VACUITY 2: the discovery sentinel really was answered, by this
        // port, at the prompt the case named.
        Assert.True(_port.AnsweredADiscoverySentinel,
            "no discovery sentinel was answered, so the branch under test never ran");

        // THE PIN: the discovery sentinel, then the operating-state read.
        // Nothing between them. Under the mutation the second line is `ST`.
        Assert.Equal(["BAT ST", "SH"], [.. sent.Take(2)]);

        // …and no stop at all until a GENUINE ALE occupancy, which is the mode
        // funnel's job and not the start sequence's.
        int firstStop = IndexOf(sent, l => l == "ST");
        int aleEntry = IndexOf(sent, l => l == "ALE");
        Assert.True(aleEntry >= 0, "the campaign never entered ALE, so this proves nothing");
        Assert.True(firstStop > aleEntry,
            "a scan stop went out before the campaign's first ALE entry");

        // …and THE SNAPSHOT WAS NEVER ARMED: no licence, so no restart attempt
        // and nothing claimed, even though the radio said it was scanning.
        Assert.DoesNotContain("SCA", sent);
        Assert.DoesNotContain(CloneService.ScanRestartedNotice, _clone.Summary);
        Assert.DoesNotContain(CloneService.ScanStoppedNotice, _clone.Summary);
    }

    private static int IndexOf(IReadOnlyList<string> lines, Func<string, bool> match)
    {
        for (int i = 0; i < lines.Count; i++) if (match(lines[i])) return i;
        return -1;
    }
}

/// <summary>
/// PIN (h)'s VALUE half (audit round 1): the read campaign stores the
/// POST-STOP operating channel, not the scan dwell it would have captured
/// before D8 reordered the start.
///
/// <para>Its own fixture because it needs a radio that MOVES while it scans —
/// see <see cref="ScanDwellPort"/>. The wire ORDER pin next door proves the
/// stop precedes the read; this proves the read is worth having.</para>
/// </summary>
public sealed class CloneScanDwellCaptureTests : IDisposable
{
    private readonly DemoSerialPort _demo = new()
    { ResponseDelayMs = 0, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
    private readonly RecordingDemoPort _recorder;
    private readonly ScanDwellPort _port;
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly CloneService _clone;
    private readonly ModeSurface _modes;
    private readonly AleSurface _ale;
    private readonly ChannelSurface _channels;

    public CloneScanDwellCaptureTests()
    {
        _recorder = new RecordingDemoPort(_demo);
        _port = new ScanDwellPort(_recorder) { DwellChannel = 21, ParkedChannel = 11 };
        _transport = new SerialTransport(_port) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _modes = new ModeSurface(_radio);
        _ale = new AleSurface(_radio);
        _channels = new ChannelSurface(_radio);
        _clone = new CloneService(
            _radio, _session, new FakeConfirmationPrompt(),
            new SsbSurface(_radio), new PowerSurface(_radio), new DeviceSurface(_radio),
            _ale, new HopSurface(_radio), _channels,
            new ModemSurface(_radio), _modes, new CampaignWireCoordinator())
        {
            SentinelTimeoutMs = 5_000,
            GateTimeoutMs = 10_000,
        };
        _transport.GateTimeoutMs = 150;
    }

    public void Dispose()
    {
        _session.Close();
        _transport.Dispose();
        _demo.DisposeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// A read campaign found in ALE, scanning. The radio reports channel 21
    /// while the scan owns it and 11 once the campaign stops it. The FILE must
    /// record 11.
    ///
    /// <para><b>What goes red.</b> A regression that snapshots before the stop
    /// — which is what the code did before D8, and what a refactor could
    /// restore without touching a single wire ORDER — keeps every ordering pin
    /// green and stores 21. The operator then gets a closing restore that
    /// faithfully puts back a channel they never chose, which is exactly the
    /// 2026-08-29 field report.</para>
    /// </summary>
    [Fact]
    public async Task AReadCampaignFoundScanning_StoresThePostStopChannel_NotTheDwell()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && _session.Phase != SessionPhase.Ready)
            Thread.Sleep(5);
        Assert.Equal(SessionPhase.Ready, _session.Phase);

        _modes.Select(OperatingMode.Ale);
        deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(_modes.Mode.IsConfirmed && _modes.Mode.Value == OperatingMode.Ale))
            await Task.Delay(5);
        Assert.True(_modes.Mode.IsConfirmed && _modes.Mode.Value == OperatingMode.Ale);

        _port.Inject("\r\nSCANNING\r\n");
        deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline
            && !(_ale.LinkState.IsConfirmed && _ale.LinkState.Value == AleLinkState.Scanning))
            await Task.Delay(5);
        Assert.True(_ale.LinkState.IsConfirmed && _ale.LinkState.Value == AleLinkState.Scanning);

        // ANTI-VACUITY: the DWELL is what the mirror holds when the campaign
        // starts. A snapshot taken before the stop would capture exactly this.
        Assert.True(_channels.Current.IsConfirmed, "the radio never reported a channel");
        Assert.Equal(21, _channels.Current.Value);
        Assert.False(_port.SawStop);

        Assert.True(await _clone.ReadAsync(), string.Join(" | ", _clone.Summary));

        Assert.True(_port.SawStop, "the campaign never stopped the scan");
        // THE PIN: the file holds the PARKED channel.
        Assert.Equal(11, _clone.File!.OperatingChannel);
    }
}
