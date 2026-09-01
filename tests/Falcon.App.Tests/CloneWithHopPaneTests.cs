using Falcon.App.Core.Cloning;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// THE COMPOSITION TESTS (plan-round15.md §6.1 row A gate (3c) and §13.4
/// gate (5)) — the clone campaigns over the REAL stack with the app's own
/// HOP pane ALIVE underneath them: <c>DemoSerialPort</c> +
/// <c>SerialTransport</c> + <c>Prc138Radio</c> + <c>RadioSession</c> +
/// <c>CloneService</c>, exactly as <see cref="CloneServiceTests"/> builds
/// them, plus a live <c>HopViewModel</c> on its own <c>HopSurface</c>.
///
/// <para>Two facts live here that no narrower test can see. (1) The HOP
/// pane's generation OBSERVER (§3.2) fires while a campaign is lapping
/// through HOP, so its <c>SH</c> lands in the campaign's own prompt queue —
/// whether that is harmless is an ORDERING, not an assertion (critic
/// F7/F16), and the only way to know is to run both campaigns with the pane
/// alive and compare against the same run without it. (2) The ping queue's
/// sentinel timer runs from the WIRE, not from the enqueue (§13.4 H3): with
/// a radio slow enough that the first init sentinel's <c>BAT ST</c> reaches
/// the wire AFTER the 1 500 ms knob, the whole sentinel stream used to shift
/// by one and every accumulation read published EMPTY while marking its
/// domain <c>Read</c>.</para>
/// </summary>
public sealed class CloneWithHopPaneTests
{
    // THE DELAY IS THE INSTRUMENT (§13.4 seam facts): DemoSerialPort applies
    // ResponseDelayMs per reply, so the prompt-gated init burst takes ≈2 s and
    // the first sentinel's BAT ST is written well past the 1 500 ms
    // first-init-sentinel knob — the P8 bench shape (the write at 1 690 ms
    // against a knob at 1 500 ms) reproduced at the byte seam.
    private const int SlowDemoReplyMs = 300;

    /// <summary>
    /// One whole app-side stack over the demo radio, with the HOP pane
    /// optionally alive on it. Two rigs — one paned, one bare — are how the
    /// pane's contribution to the wire is MEASURED rather than guessed.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly object _logLock = new();
        private readonly List<(string Direction, string Line)> _log = [];

        public DemoSerialPort Demo { get; }
        public RecordingDemoPort Port { get; }
        public SerialTransport Transport { get; }
        public Prc138Radio Radio { get; }
        public RadioSession Session { get; }
        public FakeConfirmationPrompt Prompt { get; } = new();

        /// <summary>The campaign signal the whole rig shares — the CloneService
        /// raises it, the pane reads it. ONE instance, exactly as the
        /// composition root binds it.</summary>
        public CampaignWireCoordinator Wire { get; } = new();

        public CloneService Clone { get; }

        /// <summary>Null when the rig runs BARE — the control for every
        /// "what did the pane add?" measurement.</summary>
        public HopViewModel? Pane { get; }

        public Rig(bool withHopPane, int responseDelayMs = 0)
        {
            Demo = new DemoSerialPort
            { ResponseDelayMs = responseDelayMs, TuneTerminalDelayMs = 0, ZeroizeSettleDelayMs = 0 };
            Port = new RecordingDemoPort(Demo);
            Transport = new SerialTransport(Port) { OpenSettleMs = 0 };
            // INLINE marshalling, as every other test in this suite composes
            // it (SessionTestBase). The pane's contract — one read per
            // lifecycle it observes — is a property of the APP, and under the
            // runner's own queuing context it would be measured against the
            // runner's scheduling instead: a lifecycle the pane is only told
            // about after the campaign has already left HOP is one it never
            // observed, and the count would wobble with machine load. The
            // MARSHALLED case has its own pin, on the ViewModel, where it can
            // be driven deterministically (HopViewModelTests' QueuingContext).
            var context = new InlineContext();
            Radio = new Prc138Radio(Transport, context);
            Session = new RadioSession(Radio, Transport, context);
            Clone = new CloneService(
                Radio, Session, Prompt,
                new SsbSurface(Radio), new PowerSurface(Radio), new DeviceSurface(Radio),
                new AleSurface(Radio), new HopSurface(Radio), new ChannelSurface(Radio),
                new ModemSurface(Radio), new ModeSurface(Radio), Wire);

            // These rigs run alongside two thousand other tests, and every
            // budget below measures a RADIO'S SILENCE — never anything this
            // file asserts. A loaded runner's thread pool is not a radio, so
            // they are given room rather than left to convert a scheduling
            // hiccup into a false "the radio never came back".
            Radio.ZeroizeSettleTimeoutMs = 120_000;
            Clone.ZeroizeSettleTimeoutMs = 120_000;
            Clone.SentinelTimeoutMs = 60_000;
            Clone.ReadCompletionTimeoutMs = 120_000;
            Clone.GateTimeoutMs = 120_000;

            // ONE ordered log of both directions, taken at the TRANSPORT and
            // not at Core's events: the (3c) order pin is about where a SENT
            // line falls among RECEIVED ones, and Core's `LineSent` /
            // `MessageReceived` are MARSHALLED (Q10) — under a test
            // synchronization context they can be delivered out of order,
            // which would make an ordering pin measure the harness. These two
            // are raised on the writer and reader threads at the moment the
            // bytes move: `LineWritten` in write order (round 15 A0) and
            // `LineReceived` in arrival order.
            Transport.LineWritten += (_, e) => Append("TX", e.Line);
            Transport.LineReceived += (_, e) => Append("RX", e.Line.Trim());

            // THE CAMPAIGN WINDOW'S BOUNDS, taken BEFORE the pane subscribes.
            // Handler order is registration order, so this runs first on both
            // edges: at the END edge the campaign's own last bytes are already
            // logged (its closing restore awaited a sentinel) and no producer
            // has yet been told it may read. That is what makes "inside the
            // window" a decidable question.
            Wire.Changed += (_, _) =>
            {
                lock (_logLock)
                {
                    if (Wire.CampaignActive) CampaignStartLogIndex = _log.Count;
                    else CampaignEndLogIndex = _log.Count;
                }
            };

            // Constructed BEFORE the connect, exactly as DI does it: the pane
            // is alive for the whole session, including the landing — and wired
            // to the SAME campaign signal the composition root gives it
            // (plan-clone-write-structural.md D1): the pane is a §4 producer,
            // and its quiesce is a property of the app, not of a fixture.
            if (withHopPane)
                Pane = new HopViewModel(new HopSurface(Radio), Session, TimeProvider.System, Wire);
        }

        /// <summary>Index into <see cref="Log"/> of the campaign's START edge —
        /// -1 when no campaign has run.</summary>
        public int CampaignStartLogIndex { get; private set; } = -1;

        /// <summary>Index into <see cref="Log"/> of the campaign's END edge.</summary>
        public int CampaignEndLogIndex { get; private set; } = -1;

        /// <summary>The lines WRITTEN inside the campaign's lease.</summary>
        public List<string> SentInsideCampaign =>
        [.. Log.Take(CampaignEndLogIndex).Skip(CampaignStartLogIndex)
               .Where(e => e.Direction == "TX").Select(e => e.Line)];

        /// <summary>The lines WRITTEN after the campaign let go of the wire.</summary>
        public List<string> SentAfterCampaign =>
        [.. Log.Skip(CampaignEndLogIndex).Where(e => e.Direction == "TX").Select(e => e.Line)];

        private void Append(string direction, string line)
        {
            lock (_logLock) _log.Add((direction, line));
        }

        public IReadOnlyList<(string Direction, string Line)> Log
        {
            get { lock (_logLock) return [.. _log]; }
        }

        public List<string> Sent => [.. Log.Where(e => e.Direction == "TX").Select(e => e.Line)];

        public void ConnectReady()
        {
            Session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
            var deadline = Environment.TickCount64 + 20_000;
            while (Environment.TickCount64 < deadline && Session.Phase != SessionPhase.Ready)
                Thread.Sleep(5);
            Assert.Equal(SessionPhase.Ready, Session.Phase);
        }

        public void Dispose()
        {
            Session.Close();
            Transport.Dispose();
            Demo.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    // ---- §13.4 gate (5): the read seam, over a SLOW radio --------------------

    /// <summary>
    /// §13.4 GATE (5) — the composition READ pin, and the one test in the
    /// suite that separates "the sentinel was enqueued" from "the sentinel was
    /// written".
    ///
    /// <para>RED on the pre-A0 code by construction: the first init sentinel's
    /// timer started at ENQUEUE, expired before its <c>BAT ST</c> reached the
    /// wire, and the late answer then completed the NEXT queued sentinel early
    /// — after which every accumulation leg of the campaign was completed by
    /// the PREVIOUS leg's answer, i.e. before its own data had arrived. The
    /// domains still came back <c>Read</c>: silently EMPTY. Exactly the
    /// owner's file.</para>
    /// </summary>
    [Fact]
    public async Task AReadCampaignOverASlowRadio_FillsTheBook_NotAnEmptyOneMarkedRead()
    {
        using var rig = new Rig(withHopPane: true, responseDelayMs: SlowDemoReplyMs);
        rig.ConnectReady();

        Assert.True(await rig.Clone.ReadAsync(), string.Join(" | ", rig.Clone.Summary));
        var file = rig.Clone.File!;

        // The demo's book is 3 selfs / 5 individuals / 3 nets — the numbers
        // are the anti-vacuity: an empty book marked Read passes any state
        // check on its own.
        Assert.Equal(CloneDomainState.Read, file.BookState);
        Assert.Equal(3, file.Selfs.Count);
        Assert.Equal(5, file.Individuals.Count);
        Assert.Equal(3, file.Nets.Count);
        Assert.NotEmpty(file.Schedules);
        Assert.NotEmpty(file.Messages);
        Assert.Empty(file.IncompleteDomains);
    }

    // ---- A gate (3c): the pane and the campaign share one wire --------------

    /// <summary>
    /// GATE (3c), REWRITTEN BY D1 — <b>THE INTEGRATION QUIESCE PIN</b>
    /// (plan-clone-write-structural.md §6 "Quiesce", §5.2's positive half).
    ///
    /// <para><b>What this test used to say, and why it is inverted.</b> It
    /// MEASURED the pane's contribution to the campaign's wire — one <c>SH</c>
    /// per generation lifecycle, inside each HOP entry — and asserted the
    /// campaign survived it. The 2026-08-28 field failure is what that
    /// measurement was: fourteen campaign-blind producers firing on exactly the
    /// events a campaign lap generates. D1 removes the collision source instead
    /// of managing it, so the number this test measures is now ZERO, and the
    /// pane's owed read lands AFTER the campaign has let go of the wire.</para>
    ///
    /// <para>Two rigs in ONE test, exactly as before: each is a whole app stack
    /// with its own threads, and the pins are statements about the DIFFERENCE
    /// between them.</para>
    ///
    /// <para><b>The positive half (I-10).</b> Suppression lives only in
    /// producers. The campaign's own reads must still be on the wire inside the
    /// very window in which the pane is silent — asserted below by naming them,
    /// so a "quiesce" that had accidentally silenced the campaign itself could
    /// not pass.</para>
    /// </summary>
    [Fact]
    public async Task TheReadCampaign_WithTheHopPaneAlive_GetsNoPaneTraffic_AndPaysThePanesReadAtTheEnd()
    {
        using var paned = new Rig(withHopPane: true);
        using var bare = new Rig(withHopPane: false);
        paned.ConnectReady();
        bare.ConnectReady();

        Assert.True(await paned.Clone.ReadAsync(), string.Join(" | ", paned.Clone.Summary));
        Assert.True(await bare.Clone.ReadAsync(), string.Join(" | ", bare.Clone.Summary));

        Assert.Empty(paned.Clone.File!.IncompleteDomains);
        Assert.Equal(bare.Clone.Summary, paned.Clone.Summary);

        // THE FILE IS THE PRODUCT: whatever the pane does, it may not change one
        // byte of what the campaign captured.
        Assert.Empty(CloneCompare.Diff(bare.Clone.File!, paned.Clone.File!));

        Assert.True(paned.CampaignStartLogIndex >= 0, "the campaign never took the wire");
        Assert.True(paned.CampaignEndLogIndex > paned.CampaignStartLogIndex,
            "the campaign never let the wire go");

        var insidePaned = paned.SentInsideCampaign;
        var insideBare = bare.SentInsideCampaign;

        // ---- THE PIN: ZERO PRODUCER TRAFFIC INSIDE THE CAMPAIGN -------------
        // The pane's landing read is `DIS 0` and nothing else in this rig sends
        // it, so its absence is decisive on its own.
        Assert.DoesNotContain("DIS 0", insidePaned);
        // …and its `SH` reads — which the campaign ALSO sends, so only the
        // difference against the bare run can see them — number zero.
        Assert.Equal(insideBare.Count(l => l == "SH"), insidePaned.Count(l => l == "SH"));

        // ANTI-VACUITY, three ways.
        //
        // (1) The campaign really did lap through HOP — twice, the leg itself
        // and the closing restore's — and really did make the radio generate
        // twice. Those are the exact events this pane's landing load and its
        // generation observer fire on; before D1 they produced the reads this
        // test used to count.
        var panedEntries = HopEntries(paned.Log);
        Assert.Equal(2, panedEntries.Count);
        Assert.Equal(
            panedEntries.Count,
            paned.Log.Count(e => e is { Direction: "RX", Line: "Generating Hopset..." }));

        // (2) The pane is ALIVE and its read was DEFERRED, not lost. The
        // campaign restored this radio to SSB — its found mode — so the pane's
        // own gate ("nothing may be read from a mode the operator is not in")
        // is what holds the read after the campaign lets go. The moment the
        // radio confirms HOP again, the landing load the campaign deferred goes
        // out, exactly once. A pane whose latch had been WRONGLY consumed
        // during the campaign would stay silent here for the rest of the
        // session, which is the failure this half catches.
        Assert.DoesNotContain("DIS 0", paned.Sent);
        new ModeSurface(paned.Radio).Select(OperatingMode.Hop);
        Assert.True(
            WaitUntil(() => paned.Sent.Count(l => l == "DIS 0") == 1),
            "the pane's deferred landing read never arrived once the radio confirmed HOP");

        // (3) …and it is the PANE's read: the bare rig, same campaign, same
        // mode switch, never sends it.
        new ModeSurface(bare.Radio).Select(OperatingMode.Hop);
        Assert.False(
            WaitUntil(() => bare.Sent.Contains("DIS 0"), budgetMs: 500),
            "the bare rig sent DIS 0, so this is not the pane's read");

        // ---- THE POSITIVE HALF (I-10): the campaign's OWN reads still flow --
        // Named individually rather than counted, because "the wire was busy"
        // is not the claim: these are the campaign's own leg commands, and a
        // suppression that reached past producers into the campaign would drop
        // them.
        Assert.Contains("DI 0 99", insidePaned);      // the SSB channel dump
        Assert.Contains("SLFAD", insidePaned);        // the ALE book
        Assert.Contains("DIS", insidePaned);          // the HOP nets (DIS-all)
        Assert.Contains("SH", insidePaned);           // the operating-state reads
        Assert.Contains("BAT ST", insidePaned);       // every leg's sentinel
    }

    /// <summary>Poll a condition on the wire within a bounded budget. Used both
    /// ways: TRUE to prove something arrived, FALSE to prove nothing did.</summary>
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

    /// <summary>Every HOP ENTRY in a recorded stream: the `HO` that starts it,
    /// the CLOSING `HOP>` that ends its generation (the second prompt of the
    /// entry — the first one confirms the mode), and the `SS` that leaves HOP
    /// again.</summary>
    private static List<(int Ho, int Closing, int Leave)> HopEntries(
        IReadOnlyList<(string Direction, string Line)> log)
    {
        var entries = new List<(int Ho, int Closing, int Leave)>();
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i] is not { Direction: "TX", Line: "HO" }) continue;
            int closing = NthIndex(log, "RX", "HOP>", 2, i);
            int leave = FirstIndexFrom(log, "TX", "SS", i);
            Assert.True(closing > i, "an entry never reached its closing prompt");
            Assert.True(leave > closing, "an entry never left HOP");
            entries.Add((i, closing, leave));
        }
        return entries;
    }

    /// <summary>`SH` writes strictly between two points of one stream.</summary>
    private static int CountSh(
        IReadOnlyList<(string Direction, string Line)> log, int from, int to)
    {
        int n = 0;
        for (int i = from + 1; i < to && i < log.Count; i++)
            if (log[i] is { Direction: "TX", Line: "SH" }) n++;
        return n;
    }

    /// <summary>
    /// The WRITE campaign with the pane alive — and a FINDING this gate is the
    /// first test in the suite able to see.
    ///
    /// <para><b>An ALE programming row can come back UNVERIFIED when a pane
    /// shares the wire.</b> <c>AleProgrammingGate</c> refuses to draw a
    /// verdict unless the closing sentinel was the ONLY ping in flight at the
    /// write instant (<c>BracketClean</c>), and a live pane is another reader:
    /// the row is then reported as "not written — another read was in flight",
    /// whatever actually reached the radio. Measured 2026-08-22: 3 of 6 runs
    /// on this rig, naming a different row each time. It is NOT round 15's
    /// observer — with the observer's `SH` suppressed the same rows still came
    /// back unverified in 3 of 6 runs, so the pane's PRE-EXISTING landing read
    /// (`DIS n` + `SH`, round-4 AB3) is enough. The repair is a decision about
    /// the programming gate and is out of this round's scope.</para>
    ///
    /// <para><b>What this pin therefore allows, and nothing wider</b> (manager
    /// ruling, audit round 1): the campaign's VERDICT may be withheld for rows
    /// where a pane read overlapped the write — and that is the ONLY
    /// tolerance. The campaign's result is asserted; every value it wrote must
    /// be found on the radio by a FRESH READ afterwards; an abort is a failure
    /// and is never retried.</para>
    /// </summary>
    [Fact]
    public async Task TheWriteCampaign_WithTheHopPaneAlive_LosesNoValue_OnlyTheOddVerdict()
    {
        using var paned = new Rig(withHopPane: true);
        paned.ConnectReady();

        Assert.True(await paned.Clone.ReadAsync(), string.Join(" | ", paned.Clone.Summary));
        var written = CloneFile.Load(paned.Clone.File!.Save());   // what the write will replay
        paned.Demo.ApplyScriptedPerturbation();                   // move the radio out from under it

        paned.Prompt.EnqueueAnswer(true);
        bool clean = await paned.Clone.WriteAsync(CloneSwapTests.Rows());

        // THE RESULT IS ASSERTED. A campaign that came back false may only
        // have done so for the withheld-verdict class above; every other line
        // — a diff, an abort, a refusal — fails here.
        var withheld = paned.Clone.Summary
            .Where(l => l.Contains("another read was in flight", StringComparison.Ordinal))
            .ToList();
        var unexplained = paned.Clone.Summary
            // D14 (2026-08-30): the mark/space notice is GONE from the write
            // summary, so this filter no longer excuses one — a line about the
            // tones arriving here again is an unexplained line and fails.
            .Where(l => !l.Contains("another read was in flight", StringComparison.Ordinal)
                     && !l.StartsWith("Left the radio", StringComparison.Ordinal)
                     // D4's elision count is a NOTICE from the verify's own read
                     // leg — it reports something that worked, like the two above.
                     && !l.StartsWith("SSB channels: ", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(unexplained);
        Assert.True(clean || withheld.Count > 0,
            "the campaign came back false with no withheld verdict to explain it: "
            + string.Join(" | ", paned.Clone.Summary));

        // NO VALUE IS LOST, whatever the brackets could say: a fresh read must
        // find the file that was written.
        var probe = new CloneService(
            paned.Radio, paned.Session, paned.Prompt,
            new SsbSurface(paned.Radio), new PowerSurface(paned.Radio), new DeviceSurface(paned.Radio),
            new AleSurface(paned.Radio), new HopSurface(paned.Radio), new ChannelSurface(paned.Radio),
            new ModemSurface(paned.Radio), new ModeSurface(paned.Radio), new CampaignWireCoordinator());
        Assert.True(await probe.ReadAsync(), string.Join(" | ", probe.Summary));
        Assert.Empty(CloneCompare.Diff(written, probe.File!));

        // ANTI-VACUITY: the campaign really did lap through HOP with the pane
        // watching, so the generation lifecycles really were observed.
        Assert.Contains(paned.Log, e => e is { Direction: "RX", Line: "Generating Hopset..." });
    }

    private static int FirstIndex(
        IReadOnlyList<(string Direction, string Line)> log, string direction, string line)
        => FirstIndexFrom(log, direction, line, -1);

    private static int FirstIndexFrom(
        IReadOnlyList<(string Direction, string Line)> log, string direction, string line, int from)
    {
        for (int i = from + 1; i < log.Count; i++)
            if (log[i].Direction == direction && log[i].Line == line) return i;
        return -1;
    }

    private static int NthIndex(
        IReadOnlyList<(string Direction, string Line)> log, string direction, string line,
        int n, int from)
    {
        int seen = 0;
        for (int i = from + 1; i < log.Count; i++)
        {
            if (log[i].Direction != direction || log[i].Line != line) continue;
            if (++seen == n) return i;
        }
        return -1;
    }
}
