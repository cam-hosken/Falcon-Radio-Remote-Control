using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Falcon.Core.Transport;

namespace Falcon.App.Core.Demo;

/// <summary>
/// The DEMO radio (plan/plan-demo-radio.md): a MINIMAL PRC-138 responder
/// behind the real byte seam, so the GUI can be explored with no radio
/// attached — the operator picks port "DEMO" in Settings and everything
/// above <see cref="ISerialPort"/> (SerialTransport, Prc138Radio,
/// RadioSession, surfaces, ViewModels) runs unmodified.
///
/// This is NOT a simulator and NOT a test layer (the doctrine stays replay,
/// not simulation — docs/tests.md; bench/ stays authoritative). The whole
/// script is six rules, response shapes verbatim from docs/protocol.md (and,
/// where the raw capture records decorative spaces, docs/probes.md):
///
///   1. `PORT_R ECHO OFF` → `PORT_REMOTE ECHO OFF` + prompt
///   2. `BAT ST` → `Battery Status FULL 29.7V` + prompt (the init sentinel —
///      this alone completes the connect ritual to Ready)
///   3. `SS` / `ALE` / `HO` → switch the demo-side mode; the NEW mode's
///      prompt is the confirmation that flips the pane and the highlight
///   4. `SH` → the current mode's SH block, RENDERED FROM STATE in the three
///      captured layouts (round 11 §9A — see the statefulness note below)
///   4b. `RETU` → the coupler-tune LIFECYCLE (plan-ui-tweaks.md §L): the
///      immediate answer carries ` TUNING COUPLER ` (so the spine chip
///      animates), then a SECOND, later chunk carries one terminal line —
///      rotating per press so every chip state is demonstrable without a
///      radio: complete → marginal → fault → complete… (the real coupler's
///      FAULT is a routine outcome, not an error flow)
///   4c. at the HOP prompt, `DIS` / `DIS n` / `HOPLIST n` / `EXC` → the net
///      table and the exclusion bands, and the HOP PROGRAMMING writes that
///      move them
///   4e. at the ALE prompt, the FILL (plan-ale-programming.md §4.6): the
///      address book, channel groups, membership and LQA schedules, served
///      through the captured listing shapes; the fill writes update them and
///      answer prompt-only; the captured refusal lines come out of the state
///   5. at the SSB prompt, the operational settings and the stored channels
///      → the CAPTURED read-back line for the value the operator set. A value
///      whose answer SHAPE was never captured (`SQ_L LO`/`MEDIUM`) falls
///      through to rule 6 rather than invent wire text (replay doctrine)
///   6. anything else → the current mode's prompt only (the press stays
///      visible in the Console; values honestly unconfirmed)
///
/// <para><b>ROUND 11 §9A — the STATEFUL upgrade, and its doctrine note.</b>
/// The radio-cloning gate is a demo ROUND TRIP: read the demo radio to a file,
/// swap the identity, PERTURB the demo's state, write the file back, and
/// verify the read comes back clean. That gate is worth nothing unless the
/// demo actually REMEMBERS what was written — a canned responder would "pass"
/// by never moving. So every domain the clone writes is now demo-side state:
/// stored channels, HOP nets and their lists, exclusion bands, modem preset
/// fields and their enabled set, the operating settings, the stored TX message
/// slots, the ALE book with ORDERED membership, the channel groups and the LQA
/// schedule queue. <b>Every LINE SHAPE is still the captured one</b> — the
/// state decides the VALUES, never the layout — and a command whose answer
/// shape was never captured still mutates state and answers PROMPT-ONLY rather
/// than fabricate an echo. State resets to the baseline on every port OPEN, so
/// each connect is a factory-fresh demo radio.</para>
///
/// Every command draws a prompt (protocol.md framing — the prompt-gated
/// writer depends on it). Responses are raised on a dedicated worker thread,
/// matching the seam contract ("raised on the implementation's read
/// thread") and the ordering of a real port.
/// </summary>
public sealed class DemoSerialPort : ISerialPort
{
    /// <summary>The reserved port name the Settings picker shows.</summary>
    public const string DemoPortName = "DEMO";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // R1 response shape: <LF> on receipt → payload lines → blank → prompt.
    private const string SsbPrompt = "SSB> ";
    private const string AlePrompt = "ALE> ";
    private const string HopPrompt = "HOP> ";

    private static readonly string[] NoPayload = [];

    // Coupler-tune lifecycle lines (plan-ui-tweaks.md §L). VERBATIM wire
    // forms: the two lifecycle lines are the raw R-session captures
    // (docs/probes.md — decorative spaces and all); the two rarer terminals
    // are as docs/protocol.md "Tuner states (confirmed)" records them. The
    // parser keys on the payload token (COMPLETE / MARGINAL / FAULT), and
    // MARGINAL is a QUALIFIER on a completed tune, not a fourth outcome.
    // The two ZEROIZE banners, VERBATIM (bench/transcripts/r11-zeroize-* and
    // r12-p1-*). The COMPLETE line was captured for the first time on
    // 2026-08-18 by the round-12 P-1 settle poll — nothing had ever polled the
    // same session long enough to see it.
    /// <summary>The radio's generic syntax reject, VERBATIM.</summary>
    private const string ErrorBanner = "** ERROR **";

    private const string ZeroizingBanner = "*** ZEROIZING RAM -- PLEASE WAIT ***";
    private const string ZeroizeCompleteBanner = "*** ZEROIZE COMPLETE ***";

    /// <summary>The ALE fill gate's own line, which an ALE-context <c>ZERO</c>
    /// emits on its way past (captured 2026-08-19). Same spelling the fill gate
    /// uses everywhere else — an empty book wants a 1-3 character self.</summary>
    private const string AleFillGateLine = "PRG 1-3 CHAR SLF";

    private const string TuningLine = " TUNING COUPLER ";
    private const string FaultTerminal = "TUNE FAULT";
    private static readonly string[] TuneTerminals =
    [
        " TUNE COMPLETE  ",     // → TuneChipState.Complete
        "TUNE MARGINAL",        // → TuneChipState.CompleteMarginal
        FaultTerminal,          // → TuneChipState.Fault (routine on this radio)
    ];

    private const string GeneratingLine = "Generating Hopset...";

    private readonly object _stateLock = new();
    private string _prompt = SsbPrompt;                 // demo-side mode
    private int _tuneTerminalIndex;                     // rotates per TUNE, whoever asked

    /// <summary>
    /// THE COUPLER'S TUNE MEMORY (round 15 N3, plan §3.5 / decision D3),
    /// modelled on P6b: `NET 1` — a frequency the coupler had not tuned
    /// recently — played ` TUNING COUPLER ` + a terminal, while `NET 0` back
    /// played the generation alone. The real memory is per FREQUENCY; nets are
    /// the demo's stand-in (the round-2 F1 precedent), which is a DEMO-MODELLED
    /// substitution.
    ///
    /// <para>Per session (cleared on open and by a zeroize). A net enters when
    /// its reply is BUILT, and only if the terminal that reply will deliver is
    /// COMPLETE or MARGINAL — a FAULT leaves it untuned so the next select
    /// retries, which is what the field rig did on every entry. Whether the
    /// real coupler remembers a faulted frequency is NEEDS-PROBING (§8 P9).</para>
    /// </summary>
    private readonly HashSet<int> _tunedNets = [];
    private BlockingCollection<QueuedResponse>? _responses;
    private Thread? _reader;

    /// <summary>Delay before each response is raised — a whiff of real-radio
    /// latency for the UI. Tests set 0.</summary>
    public int ResponseDelayMs { get; set; } = 30;

    /// <summary>Extra delay before the coupler tune's TERMINAL line, so the
    /// spine's Tuning animation is actually visible in the demo (a real tune
    /// takes seconds). Tests set 0.</summary>
    public int TuneTerminalDelayMs { get; set; } = 1_500;

    /// <summary>Extra delay before the ZEROIZE-COMPLETE chunk — the SILENCE a
    /// zeroize leaves behind. The real radio took 9.4 s (captured 2026-08-18,
    /// round-12 P-1: eight bare-CR polls before the prompt returned); the demo
    /// keeps a visible fraction of that so the settle gate is exercised rather
    /// than skipped. Tests set 0.</summary>
    public int ZeroizeSettleDelayMs { get; set; } = 2_000;

    /// <summary>One queued chunk plus the extra delay to hold it back —
    /// the mechanism behind the visible tune lifecycle.</summary>
    private readonly record struct QueuedResponse(byte[] Bytes, int ExtraDelayMs);

    public bool IsOpen { get; private set; }

    public event EventHandler<SerialDataEventArgs>? DataReceived;

    /// <summary>Never raised: the demo radio cannot be yanked.</summary>
    public event EventHandler<SerialDisconnectedEventArgs>? Disconnected { add { } remove { } }

    public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
        => Task.FromResult((IReadOnlyList<string>)[DemoPortName]);

    /// <summary>Round 12 §6 F4: the demo port is software — listing it costs
    /// nothing and prompts nobody — so the passive seam is the same list.</summary>
    public Task<IReadOnlyList<string>> GetAvailablePortsPassiveAsync() => GetAvailablePortsAsync();

    public Task OpenAsync(PortSettings settings, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (IsOpen) throw new InvalidOperationException("Port is already open.");
            _prompt = SsbPrompt;    // a fresh demo radio always starts in SSB
            _tuneTerminalIndex = 0; // …and its first tune completes
            _tunedNets.Clear();     // …with nothing tuned yet this session
            ResetToBaseline();      // …and carries the canned R7 fill again
            var responses = new BlockingCollection<QueuedResponse>();
            _responses = responses;
            _reader = new Thread(() => ReadLoop(responses))
            {
                IsBackground = true,
                Name = "falcon-demo-read",
            };
            IsOpen = true;
            _reader.Start();
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Thread? reader;
        lock (_stateLock)
        {
            IsOpen = false;
            _responses?.CompleteAdding();
            _responses = null;
            reader = _reader;
            _reader = null;
        }
        reader?.Join(2000);
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (!IsOpen) throw new InvalidOperationException("Port is not open.");
            // One command per write (SerialTransport's writer appends CR).
            var command = Encoding.ASCII.GetString(data.Span).Trim().ToUpperInvariant();
            // Mode state must advance in command order, so it mutates here
            // (send order) while the response queue preserves reply order.
            try
            {
                foreach (var response in BuildResponses(command))
                    _responses?.TryAdd(response);
            }
            catch (InvalidOperationException) { /* CompleteAdding raced a close */ }
        }
        return Task.CompletedTask;
    }

    /// <summary>Usually ONE chunk (the answer + its prompt); a coupler tune
    /// adds a second, delayed chunk carrying the async terminal line — which
    /// carries NO prompt, because on the real radio the tune terminal is
    /// unsolicited chatter, not a command answer (an extra prompt would
    /// release the write gate for whatever is in flight).
    /// Caller holds _stateLock.</summary>
    private List<QueuedResponse> BuildResponses(string command)
    {
        // CLONE ROUND 12 §3 leg 2 — `ZERO`, in the captured shape
        // (bench/transcripts/r12-p1-20260818-222442 for the SSB case,
        // r12-zero-prompts-20260819-061052 for the other two):
        //   1. per-prompt PRE-BANNER lines, where the radio emits any;
        //   2. the ZEROIZING banner, WITH NO PROMPT (the radio then goes
        //      silent for seconds — bare-CR polls answered NO BYTES),
        //   3. seconds later, `*** ZEROIZE COMPLETE ***` and the prompt.
        // The gap is the whole point: a campaign that assumed an immediate
        // prompt would send its first write into a radio still wiping RAM,
        // which is exactly what the settle machine exists to prevent.
        //
        // ACCEPTED AT EVERY PROMPT, AND IT ALWAYS COMES BACK AT `SSB>`
        // (captured 2026-08-19). This is what lets the clone campaign send the
        // wipe as its LITERAL first wire act from wherever the operator left
        // the radio, and still find itself where its next leg needs to be.
        //
        // THE ALE-CONTEXT PREAMBLE IS REAL AND IS REPRODUCED: the wipe empties
        // the book as it goes, so the fill gate re-reports on the way past —
        // `IN_PROG`, then the trailer `PRG 1-3 CHAR SLF`, EACH TERMINATED BY A
        // PROMPT, and only then the banner. Those prompts are the reason Core
        // gates its settle on the banner rather than on "any prompt": settling
        // on one of them would call the wipe finished before it had started.
        // (The `HOP>` echo the transcript shows in the HOP leg is NOT part of
        // this answer — it is the trailing prompt of the `HO` that preceded it,
        // which this demo already emits with that command. Modelling it here
        // too would be inventing a line the radio did not send.)
        if (command == "ZERO")
        {
            var chunks = new List<QueuedResponse>();
            // Built BEFORE the wipe, because they carry the OLD prompt.
            if (_prompt == AlePrompt)
            {
                chunks.Add(new(Frame(["IN_PROG"], withPrompt: true), 0));
                chunks.Add(new(Frame([AleFillGateLine], withPrompt: true), 0));
            }

            chunks.Add(new(Frame([ZeroizingBanner], withPrompt: false), 0));
            ZeroizeState();                        // …which parks the prompt at SSB>
            chunks.Add(new(Frame([ZeroizeCompleteBanner], withPrompt: true), ZeroizeSettleDelayMs));
            return chunks;
        }

        // ROUND 15 N3 (plan §3.5): a HOP-prompt `NET n` carries the TUNE LEG,
        // which is the only reply besides RETU's that needs a second chunk.
        if (_prompt == HopPrompt
            && command.StartsWith("NET ", StringComparison.Ordinal)
            && TryParseNet(command[4..].Trim(), out int selectNet))
            return HopNetSelectChunks(selectNet);

        if (command == "RETU")
        {
            // Rotate the terminal so every tune-chip state is demonstrable
            // (§L: complete → marginal → fault → complete…).
            var terminal = TuneTerminals[_tuneTerminalIndex];
            _tuneTerminalIndex = (_tuneTerminalIndex + 1) % TuneTerminals.Length;
            return
            [
                new(Frame([TuningLine], withPrompt: true), 0),
                new(Frame([terminal], withPrompt: false), TuneTerminalDelayMs),
            ];
        }
        return [new(BuildResponse(command), 0)];
    }

    /// <summary>
    /// `NET n` AT THE HOP PROMPT — the plan §3.5 reply table, one branch per
    /// row, every line tagged by provenance. The round-6 PROVISIONAL shape
    /// (`NET 0n` / `Generating Hopset...` / `Hopnum 0041`) is RETIRED by the
    /// P6b capture: no select window carries a Hopnum, and the tune leg is
    /// real (critic F3).
    ///
    /// <para><b>Prompt placement.</b> The wire carries TWO prompts — one right
    /// after the `NET  0n` echo and one closing the entry — but the demo's
    /// <see cref="Frame"/> contract is one prompt per chunk, so the block
    /// carries the CLOSING one only. That is the prompt the app's generation
    /// lifecycle ends on, which is what these replies exist to drive; the
    /// early-release interleave the closing one cannot show is covered by
    /// the parser replay in <c>SpineStatusViewModelTests</c> (rung 1(c)).</para>
    /// Caller holds _stateLock.
    /// </summary>
    private List<QueuedResponse> HopNetSelectChunks(int net)
    {
        int previous = _currentNet;
        _currentNet = net;      // the selection is REMEMBERED (round 11 §9A)

        // Existing, captured: a wiped net has no hopset to generate from.
        if (_hopNets[net].Wiped)
            return [new(Frame(["No Hopset"], withPrompt: true), 0)];

        // P6b `T3-net-same-0`: re-selecting the CURRENT net echoes and stops —
        // no generation, no tune.
        if (net == previous)
            return [new(Frame([NetEchoLine(net)], withPrompt: true), 0)];

        // P6b `T2-net-back-0`: a net the coupler has already tuned generates
        // and stops. DEMO-MODELLED as a whole for the BYPASSED coupler, whose
        // "generate only, no tune lines" comes from protocol.md's coupler
        // section rather than from a capture of a bypassed `NET` (critic F22).
        bool bypassed = _settings[InternalCouplerKey] == "Bypassed";
        if (bypassed || _tunedNets.Contains(net))
            return [new(Frame([NetEchoLine(net), WaitLine, GeneratingLine], withPrompt: true), 0)];

        // P6b `T1-net-1`: an untuned net with a live coupler tunes on the way
        // in. The terminal rotation is SHARED with RETU — one rotation per
        // tune, whoever asked (critic F13).
        var terminal = TuneTerminals[_tuneTerminalIndex];
        _tuneTerminalIndex = (_tuneTerminalIndex + 1) % TuneTerminals.Length;
        if (terminal != FaultTerminal) _tunedNets.Add(net);
        return
        [
            new(Frame([NetEchoLine(net), WaitLine, GeneratingLine, TuningLine], withPrompt: false), 0),
            new(Frame([terminal], withPrompt: true), TuneTerminalDelayMs),
        ];
    }

    /// <summary>The select echo, in the SH block's own column shape
    /// (P6b: `NET  01`).</summary>
    private static string NetEchoLine(int net) => "NET  " + Two(net);

    /// <summary>Caller holds _stateLock.</summary>
    private byte[] BuildResponse(string command)
    {
        string[] payload;
        switch (command)
        {
            case "SS": _prompt = SsbPrompt; payload = NoPayload; break;
            case "ALE": _prompt = AlePrompt; payload = NoPayload; break;
            // EVERY HOP ENTRY REGENERATES (P4 `A-ho-from-ssb`, protocol.md):
            // ONE chunk, ONE cycle, and NO tune leg — the leg rides on `NET`
            // only (round 15 D6), so `HO` can never shift a raw-reply consumer
            // or race a delayed chunk. A wiped current net imposes nothing and
            // has nothing to generate from.
            case "HO":
                _prompt = HopPrompt;
                NoteHopEntry();
                payload = _hopNets[_currentNet].Wiped ? NoPayload : [WaitLine, GeneratingLine];
                break;
            case "PORT_R ECHO OFF": payload = ["PORT_REMOTE ECHO OFF"]; break;
            case "BAT ST": payload = ["Battery Status FULL 29.7V"]; break;
            case "SH":
                payload = _prompt switch
                {
                    AlePrompt => AleShBlock(),
                    HopPrompt => HopShBlock(),
                    _ => SsbShBlock(),
                };
                break;
            // Rule 4c (HOP reads/writes), rule 4d (DI channel reads + the live
            // channel writes), rule 5 (SSB settings), the mode-free stored
            // message store, then rule 4e (ALE fill). Every helper gates on the
            // demo-side prompt where the real radio does, so the order between
            // them is immaterial. The coupler is the ONE helper with no prompt
            // gate at all (round 14 B) — see InternalCouplerReply.
            default:
                payload = InternalCouplerReply(command)
                    ?? LockoutReply(command)
                    ?? HopQueryReply(command) ?? HopProgramReply(command)
                    ?? SsbChannelReply(command) ?? SsbChannelWriteReply(command)
                    ?? SsbQueryReply(command) ?? SsbSettingReply(command)
                    ?? MessageStoreReply(command)
                    ?? AleFillReply(command) ?? AleSettingReply(command) ?? NoPayload;
                break;
        }
        return Frame(payload, withPrompt: true);
    }

    /// <summary>
    /// THE GENERATION LIFECYCLE'S FIRST LINE, as the wire carries it (P6b
    /// `T1-net-1`/`T2-net-back-0`/`enter-hop`, P4 `A-ho-from-ssb`): the entry
    /// answer's own prompt IMMEDIATELY followed by `Wait...`, with no line
    /// break between them. The probe's line log shows `HOP&gt; Wait...` for
    /// exactly this reason.
    ///
    /// <para>It is emitted as ONE wire string on purpose, not as a prompt
    /// chunk plus a line: <see cref="LineFramer"/> ends a line the instant the
    /// buffer equals a bare mode prompt, so the app sees `HOP&gt;` and then
    /// `Wait...` — which is what CONFIRMS THE MODE and RELEASES THE WRITE GATE
    /// before the generation lines arrive. Everything downstream depends on
    /// that order: the HOP pane's generation observer only arms while the pane
    /// is HOP-ready (§3.2), and the interleave a released gate allows is the
    /// named candidate mechanism behind the N3 tune-chip report (§1).</para>
    /// Caller holds _stateLock (the prompt is demo-side mode state).
    /// </summary>
    private string WaitLine => _prompt + "Wait...";

    /// <summary>R1 response shape: &lt;LF&gt; → payload lines → prompt.
    /// Caller holds _stateLock (the prompt is demo-side mode state).</summary>
    private byte[] Frame(string[] payload, bool withPrompt)
    {
        var sb = new StringBuilder("\r\n");
        foreach (var line in payload) sb.Append(line).Append("\r\n");
        if (withPrompt) sb.Append(_prompt);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // =====================================================================
    // STATE (round 11 §9A). Every field below is reset by ResetToBaseline.
    // =====================================================================

    private sealed record DemoEntry(string Name, int Group, string? AssociatedSelf);

    private readonly List<DemoEntry> _selfs = [];
    private readonly List<DemoEntry> _individuals = [];
    private readonly List<DemoEntry> _nets = [];
    private readonly Dictionary<int, List<int>> _channelGroups = [];

    /// <summary>Net name → member addresses in INSERTION order (the captured
    /// ordering rule).</summary>
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);

    /// <summary>The queued LQA schedules, as the bare-EXCH listing prints
    /// them: kind token, address, interval, start.</summary>
    private readonly List<(string Kind, string Address, string Interval, string Start)> _schedules = [];

    /// <summary>One stored SSB channel, in the DI dump's own vocabulary
    /// (<c>AGC SL</c>, not <c>SLOW</c> — the dump abbreviates).</summary>
    private sealed class DemoChannel
    {
        public string Rx = "01600000";
        public string Tx = "01600000";
        public string Mode = "USB";
        public string Agc = "SL";
        public string Bandwidth = "2.7";
        public string RxOnly = "NO";
    }

    private readonly Dictionary<int, DemoChannel> _channels = [];
    private int _currentChannel;

    private sealed class DemoHopNet
    {
        public bool Wiped = true;
        public string NetId = "XXXXXXXX";
        public string Type = "WB";
        public string Center = "";
        public string Low = "";
        public string High = "";
        public List<string> Frequencies = [];
    }

    private readonly Dictionary<int, DemoHopNet> _hopNets = [];
    private int _currentNet;

    private sealed class DemoPreset
    {
        public string Name = "";
        public string Type = "";        // wire token (39TONE / SE / FSKW …)
        public string DataMode = "";    // wire token (ASYNC DAT …)
        public string Baud = "";        // wire token
        public string? Interleave;      // LISTING spelling, or null
        public string? Mark;
        public string? Space;
    }

    private readonly Dictionary<int, DemoPreset> _presets = [];
    private readonly HashSet<int> _enabledPresets = [];

    /// <summary>Exclusion bands: slot → (low kHz, high kHz), 5-digit as the
    /// listing prints them.</summary>
    private readonly Dictionary<int, (string Low, string High)> _excludes = [];

    /// <summary>Stored TX message slots 0-9. A slot absent here is empty.</summary>
    private readonly Dictionary<int, string> _txMessages = [];

    /// <summary>Operating settings, keyed by the ANSWER token the radio prints
    /// (so the render sites and the set sites cannot drift).</summary>
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

    // ---- Operator lockouts (clone round 12 §3) ---------------------------
    // The CLOSED 22-item inventory, in the radio's own report order, with the
    // section headers it prints (captured 2026-08-18,
    // bench/transcripts/r11-lockouts-* and r12-p1-*). Held as strings because
    // this class is a BYTE responder: it renders lines, it does not model.

    /// <summary>(family, section, item) in report order — the demo's copy of
    /// the closed inventory Core pins. Keyed "FAMILY/SECTION/ITEM".</summary>
    private static readonly string[] LockoutKeys =
    [
        "PROGRAM/SSB/CHAN", "PROGRAM/SSB/FILL", "PROGRAM/SSB/CFIG",
        "PROGRAM/SSB/DATA", "PROGRAM/SSB/KEYS",
        "PROGRAM/HOP/NET", "PROGRAM/HOP/EXCLUDE", "PROGRAM/HOP/TX_POWER",
        "PROGRAM/HOP/DATA",
        "PROGRAM/EAM/ADDRESS", "PROGRAM/EAM/CHGROUP", "PROGRAM/EAM/CFIG",
        "PROGRAM/EAM/LQA",
        "SELECT/SSB/DATA", "SELECT/SSB/KEY", "SELECT/SSB/MODE",
        "SELECT/SSB/TMP_CHAN", "SELECT/SSB/BFO",
        "SELECT/HOP/DATA", "SELECT/HOP/KEY",
        "SELECT/EAM/DATA", "SELECT/EAM/KEY",
    ];

    /// <summary>Key → "LOCK"/"UNLOCK".</summary>
    private readonly Dictionary<string, string> _lockouts = new(StringComparer.Ordinal);

    // The captured refusal lines, VERBATIM (docs/protocol.md; the radio pads
    // them with a leading and trailing space).
    private const string AddressExistsLine = " ADDRESS EXISTS ";
    private const string InvalidAssocSelfLine = " INV ASSOC SELF ";
    private const string InvalidMemberLine = " INV MEMBER ADDR ";
    // Captured 2026-08-17 (bench/transcripts/phase1-ale-membership and
    // phase2b-schedules), padding included.
    private const string DuplicateMemberLine = " DUPLICATE MEMBER ";
    private const string NoMembersLine = " NO MEMBERS PRGMD ";
    private const string NoLqaScheduledLine = " NO LQA SCHEDULED ";
    private const string AlreadyQueuedLine = " ADR ALREADY QUED ";
    private const string LqaQueueFullLine = " LQA QUEUE FULL ";

    /// <summary>The LQA queue's captured capacity.</summary>
    private const int LqaQueueCapacity = 10;

    /// <summary>Caller holds _stateLock (or is opening the port).</summary>
    private void ResetToBaseline()
    {
        _selfs.Clear();
        _individuals.Clear();
        _nets.Clear();
        _channelGroups.Clear();
        _members.Clear();
        _schedules.Clear();
        _channels.Clear();
        _hopNets.Clear();
        _presets.Clear();
        _enabledPresets.Clear();
        _excludes.Clear();
        _txMessages.Clear();
        _settings.Clear();
        _currentChannel = 0;
        _currentNet = 0;

        // ---- The ALE fill: the R7 probe fill, in the order it was programmed
        // at the bench, plus the 2026-08-15 GUI-viewing extension (a third
        // self, varied groups, a long name, more nets and groups). Every LINE
        // SHAPE stays the captured one; only the content varies.
        _selfs.Add(new DemoEntry("ZZZ", 0, null));
        _selfs.Add(new DemoEntry("TST", 1, null));
        _selfs.Add(new DemoEntry("CAM", 2, null));
        _individuals.Add(new DemoEntry("AAA", 1, "TST"));
        _individuals.Add(new DemoEntry("BBB", 1, "TST"));
        _individuals.Add(new DemoEntry("BOB", 2, "CAM"));
        _individuals.Add(new DemoEntry("HQ", 2, "CAM"));
        _individuals.Add(new DemoEntry("BASECAMP1", 3, "CAM"));
        _nets.Add(new DemoEntry("NT1", 1, "TST"));
        _nets.Add(new DemoEntry("NET2", 2, "CAM"));
        _nets.Add(new DemoEntry("ALLCALL", 3, "CAM"));
        _channelGroups[0] = [0];
        _channelGroups[1] = [0, 1];
        _channelGroups[2] = [2, 3, 10];
        // PROVISIONAL count: six channels on ONE line extends the captured
        // 2-channel shape (whether a long CHANS line wraps is A7c).
        _channelGroups[3] = [5, 15, 25, 35, 45, 55];
        for (int group = 4; group <= 9; group++) _channelGroups[group] = [];
        // Membership in insertion order. NT1 carries two members (one of them
        // its OWN associated self, the only self the radio allows); NET2
        // carries one; ALLCALL carries none, so the empty-state marker is
        // reachable without any writing.
        _members["NT1"] = ["AAA", "TST"];
        _members["NET2"] = ["BOB"];
        // …and two queued schedules, so the LQA mirror renders populated.
        _schedules.Add(("SOUND", "CAM", "03:00", "13:02"));
        _schedules.Add(("EXCHANGE", "BOB", "01:00", "22:34"));

        // ---- The stored channels (round-6 CJ): the captured session-23 dump
        // shape with canned values. CH 01 simplex USB; CH 02 split LSB,
        // receive-only.
        //
        // RE-BASED clone round 12 P2 — THE FULL 100-SLOT INVENTORY. The
        // round-11 demo held only the programmed slots and let the dump OMIT
        // the rest, which made a target-only channel unremovable and forced the
        // clone to carry a "survivor" tolerance. The real radio does no such
        // thing: it answers the DEFAULT ROW for an unprogrammed slot
        // (`01600000 USB SL 2.7 RXONLY NO` — protocol.md, re-confirmed by the
        // 2026-08-18 zeroize capture, where `DI 50 50` answered a default row
        // on a freshly wiped radio). Every slot exists; a slot is "unprogrammed"
        // only in the sense that it still holds the factory values.
        for (int slot = 0; slot <= 99; slot++) _channels[slot] = new DemoChannel();
        _channels[1] = new DemoChannel
        { Rx = "14313500", Tx = "14313500", Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO" };
        _channels[2] = new DemoChannel
        { Rx = "07102000", Tx = "07215000", Mode = "LSB", Agc = "SL", Bandwidth = "2.7", RxOnly = "YES" };

        // ---- The HOP nets (round-5 BC1–BC3): net 0 NB, net 2 WB, net 3 LIST,
        // the other seven in probe R9b's observed wiped form.
        for (int net = 0; net <= 9; net++) _hopNets[net] = new DemoHopNet();
        _hopNets[0] = new DemoHopNet
        { Wiped = false, NetId = "12345678", Type = "NB", Center = "11565" };
        _hopNets[2] = new DemoHopNet
        { Wiped = false, NetId = "24680135", Type = "WB", Low = "02000", High = "08000" };
        _hopNets[3] = new DemoHopNet
        {
            Wiped = false, NetId = "13579246", Type = "LIST",
            Frequencies = ["10125", "11010", "12345", "13570", "15250", "17635", "19870", "22105"],
        };

        // ---- The exclusion bands (round 11, R11): the captured single-band
        // row shape; TWO rows is the PROVISIONAL multi-band layout (§14 probe).
        _excludes[0] = ("02000", "03000");
        _excludes[1] = ("11000", "11500");

        // ---- The modem presets: SEVEN (0-6), with preset 2 DISABLED so the
        // bulk listing omits it — presence in the bulk listing is the ONLY
        // captured enabled/disabled signal, and a demo whose bulk listed
        // everything could not demonstrate the derivation at all.
        _presets[0] = new DemoPreset
        { Name = "SER", Type = "SE", DataMode = "ASYNC DAT", Baud = "4800", Interleave = "uncoded" };
        _presets[1] = new DemoPreset
        { Name = "T39", Type = "39TONE", DataMode = "ASYNC DAT", Baud = "2400", Interleave = "long" };
        _presets[2] = new DemoPreset
        { Name = "DAT2", Type = "39TONE", DataMode = "ASYNC REM", Baud = "2400", Interleave = "long" };
        _presets[3] = new DemoPreset { Name = "FW", Type = "FSKW", DataMode = "ASYNC DAT", Baud = "300" };
        _presets[4] = new DemoPreset { Name = "FN", Type = "FSKN", DataMode = "ASYNC DAT", Baud = "75" };
        _presets[5] = new DemoPreset
        { Name = "FV", Type = "FSK-V", DataMode = "ASYNC DAT", Baud = "600", Mark = "1500", Space = "1700" };
        _presets[6] = new DemoPreset
        { Name = "T39B", Type = "39TONE", DataMode = "SYNC DAT", Baud = "1200", Interleave = "short" };

        // ---- The HOP-scoped presets 7-9 (clone-field round 2 F9/F10, CAPTURED
        // 2026-08-21 by probes P5/P5b/P5c/P5d/P5d2; transcripts
        // bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl,
        // p5b-hop-modem-preset-write-20260821-181018.jsonl,
        // p5c-hop-modem-baud-20260821-182807.jsonl,
        // p5d-hop-modem-select-20260821-183052.jsonl,
        // p5d2-hop-modem-select-enabled-20260821-183248.jsonl).
        //
        // They exist ONLY at a `HOP>` prompt, in a SHORTER line with no TYPE and
        // no INTER field, and their baud vocabulary is {75, 150, 300}. The
        // FOUND state on the real bench radio is exactly this — DAT7/DAT8/DAT9,
        // ASYNC REMOTE, BAUD 300, all three DISABLED — which is why the demo
        // seeds it: the HOP bulk listing then answers `NO PRESETS ENABLED`
        // (captured) and preset 9 is the one the select probes enabled by hand.
        // ONE is enabled here (9) so the demo can demonstrate both halves of the
        // select contract — `MODEM 9` → `MODEM 9 DAT9`, `MODEM 7` →
        // `PRESET DISABLED` — without a test having to program it first.
        _presets[7] = new DemoPreset { Name = "DAT7", DataMode = "ASYNC REM", Baud = "300" };
        _presets[8] = new DemoPreset { Name = "DAT8", DataMode = "ASYNC REM", Baud = "300" };
        _presets[9] = new DemoPreset { Name = "DAT9", DataMode = "ASYNC REM", Baud = "300" };

        foreach (int preset in _presets.Keys) _enabledPresets.Add(preset);
        _enabledPresets.Remove(DemoDisabledPreset);
        _enabledPresets.Remove(7);
        _enabledPresets.Remove(8);

        // ---- Stored TX messages: two slots, so the clone's store/delete legs
        // both have something to do.
        _txMessages[0] = "RENDEZVOUS AT NOON";
        _txMessages[4] = "RADIO CHECK";

        // ---- Operator lockouts. A radio in the field arrives with SOME
        // locked; the baseline mixes them so a read cannot pass by answering
        // one value everywhere. (A ZERO puts every row back to LOCK.)
        ResetLockouts("UNLOCK");
        _lockouts["PROGRAM/SSB/CHAN"] = "LOCK";
        _lockouts["PROGRAM/HOP/DATA"] = "LOCK";
        _lockouts["SELECT/EAM/KEY"] = "LOCK";

        ApplyDefaultSettings();
    }

    /// <summary>Every lockout row to one state. Caller holds _stateLock.</summary>
    private void ResetLockouts(string state)
    {
        _lockouts.Clear();
        foreach (var key in LockoutKeys) _lockouts[key] = state;
    }

    /// <summary>
    /// CLONE ROUND 12 §3 leg 2 — the WIPE, per the owner statement (§1): "it is
    /// safe to assume that zeroize clears everything except for the remote port
    /// baud rate". Every cloned domain empties and every lockout returns to
    /// LOCK (captured twice: r11-lockouts and r12-p1 both read 22/22 LOCK after
    /// a ZERO). The remote port's LINE SETTINGS are spared BY CONSTRUCTION here
    /// — they are not demo state at all, which is why the demo session, like
    /// the real one, survives the wipe.
    ///
    /// <para><b>The channel table returns to its DEFAULT ROWS, not to
    /// nothing</b> (re-based clone round 12 P2, together with the round-trip
    /// fixtures — plan §6 cross-phase ledger). The 2026-08-18 capture is
    /// explicit: two programmed channels read back as
    /// <c>01600000 USB SL 2.7 RXONLY NO</c> after the wipe, as did an
    /// unprogrammed <c>DI 50 50</c>. A wipe does not remove slots; it resets
    /// them.</para>
    /// Caller holds _stateLock.
    /// </summary>
    private void ZeroizeState()
    {
        _selfs.Clear();
        _individuals.Clear();
        _nets.Clear();
        _members.Clear();
        _schedules.Clear();
        _channels.Clear();
        for (int slot = 0; slot <= 99; slot++) _channels[slot] = new DemoChannel();
        _presets.Clear();
        _enabledPresets.Clear();
        _excludes.Clear();
        _txMessages.Clear();
        _channelGroups.Clear();
        for (int group = 0; group <= 9; group++) _channelGroups[group] = [];
        _hopNets.Clear();
        for (int net = 0; net <= 9; net++) _hopNets[net] = new DemoHopNet();
        _currentChannel = 0;
        _currentNet = 0;
        _tunedNets.Clear();     // a wiped radio has no hopsets, so nothing is tuned
        ResetLockouts("LOCK");
        ApplyDefaultSettings();
        // THE WIPE ENDS AT `SSB>`, WHEREVER IT STARTED (captured 2026-08-19,
        // r12-zero-prompts: an ALE-context and a HOP-context wipe both settled
        // with the radio answering `SSB>`, confirmed by the `BAT ST` that
        // followed each). It is a RADIO behaviour, and the clone campaign's
        // literal ZERO-first shape leans on it.
        _prompt = SsbPrompt;
    }

    /// <summary>The operating settings' factory values, in the captured SH
    /// spellings. Shared by the baseline and by the wipe — a zeroized radio
    /// reports defaults, not the values it held a moment earlier.
    /// Caller holds _stateLock.</summary>
    private void ApplyDefaultSettings()
    {
        ResetDigitalVoice();
        _settings["POWER"] = "hi";
        _settings["SQUELCH"] = "OFF";
        _settings["DGT_SQUELCH"] = "OFF";
        _settings["DV"] = "OFF";
        _settings["BFO"] = "+0000";
        _settings["CWOFFSET"] = "0000";
        _settings["ANTENNA"] = "auto";
        // Round 14 B: the coupler's factory value is the state the bench radio
        // was found in (P-1 step 1: `INTCOUPLER` -> `INTCoupler Enabled`), in
        // the radio's own mixed case.
        _settings[InternalCouplerKey] = "Enabled";
        _settings["RWAS"] = "DISABLED";
        _settings["UNKEY_M"] = "DISABLED";
        _settings["STEP"] = "00001000";
        _settings["RFG"] = "100";
        _settings["BEEP"] = "ON";
        _settings["CONTRAST"] = "05";
        _settings["FMSQ_TYPE"] = "tone";
        _settings["FMTONE"] = "ON";
        _settings["FMDEV"] = "8.0";
        _settings["FMSQUELCH"] = "OFF";
        _settings["COMPRESS"] = "OFF";
        _settings["PREPOST FILTER"] = "ENABLE";
        _settings["PREPOST RXANTENNA"] = "DISABLE";
        _settings["PREPOST SCAN"] = "SLOW";
        _settings["ALL_CALL"] = "ON";
        _settings["ANY_CALL"] = "ON";
        _settings["AMD_DISPLAY"] = "ON";
        _settings["KEY_TO_CALL"] = "OFF";
        _settings["LSTN"] = "OFF";
        _settings["RAD_SIL"] = "OFF";
        _settings["MAXCH"] = "100";
        _settings["TUNETIME"] = "015";
        _settings["TIME_OUT"] = "000";
    }

    /// <summary>The one canned DISABLED preset — present to a targeted read,
    /// ABSENT from the bulk listing (the captured lockout behavior).</summary>
    private const int DemoDisabledPreset = 2;

    /// <summary>
    /// The clone round-trip gate's SCRIPTED SECOND STATE (plan round 11 §11).
    /// Moves EVERY domain the clone writes, so a write campaign that changed
    /// nothing cannot pass the round trip by accident: a domain this misses is
    /// a domain the gate proves nothing about.
    /// </summary>
    internal void ApplyScriptedPerturbation()
    {
        lock (_stateLock)
        {
            // Channels: both stored slots edited, a slot the source radio left
            // at its DEFAULT ROW programmed, and the operating channel moved.
            //
            // That third one is new in clone round 12 and is the point of the
            // 100-slot re-base: it used to be the ONE thing a clone could not
            // undo (no channel-DELETE verb, and a dump that omitted
            // unprogrammed slots), so it lived outside the clean round trip as
            // a named "target-only survivor" residual. The campaign now wipes
            // first and the file carries every slot, so it is an ordinary
            // difference the write reverses like any other.
            _channels[1].Rx = "05000000";
            _channels[1].Mode = "LSB";
            _channels[1].RxOnly = "YES";
            _channels[2].Tx = "09000000";
            _channels[2].Agc = "ME";
            _channels[2].Bandwidth = "3.0";
            _channels[7].Rx = "09000000";
            _channels[7].Tx = "09000000";
            _currentChannel = 2;

            // The address book: a self, an individual and a net all move.
            _selfs.Add(new DemoEntry("QQQ", 4, null));
            _individuals.Add(new DemoEntry("ZULU", 5, "CAM"));
            _individuals.RemoveAll(e => e.Name == "HQ");
            _nets.Add(new DemoEntry("NET9", 6, "CAM"));

            // Membership order AND content.
            _members["NT1"] = ["TST", "AAA"];
            _members["NET2"] = [];
            _members["ALLCALL"] = ["BOB"];

            // Channel groups.
            _channelGroups[1] = [7, 8];
            _channelGroups[5] = [42];

            // LQA schedules.
            _schedules.Clear();
            _schedules.Add(("EXCHANGE", "AAA", "04:00", "05:00"));

            // HOP nets: one wiped, one retyped, one list edited, net selected.
            _hopNets[0] = new DemoHopNet();
            _hopNets[2] = new DemoHopNet
            { Wiped = false, NetId = "99887766", Type = "NB", Center = "15000" };
            _hopNets[3].Frequencies = ["10000", "10500", "11000"];
            _hopNets[5] = new DemoHopNet
            { Wiped = false, NetId = "11112222", Type = "WB", Low = "03000", High = "04000" };
            _currentNet = 5;

            // Exclusion bands: one removed, one moved, one added.
            _excludes.Remove(0);
            _excludes[1] = ("12000", "12500");
            _excludes[4] = ("20000", "21000");

            // Modem presets: fields AND the enabled set.
            _presets[1].Baud = "1200";
            _presets[1].Interleave = "short";
            _presets[3].Name = "FWX";
            _enabledPresets.Remove(1);
            _enabledPresets.Add(DemoDisabledPreset);
            // F9: the HOP band moves too, or a round trip that never wrote
            // presets 7-9 would pass by accident — a field, a baud inside the
            // HOP vocabulary, and both directions of the enabled set.
            _presets[8].Name = "HP8X";
            _presets[8].Baud = "75";
            _presets[8].DataMode = "SYNC DAT";
            _enabledPresets.Add(8);
            _enabledPresets.Remove(9);

            // Stored messages: one edited, one deleted, one added.
            _txMessages[0] = "PERTURBED MESSAGE";
            _txMessages.Remove(4);
            _txMessages[9] = "SCRATCH";

            // Settings, across both prompts.
            _settings["POWER"] = "low";
            _settings["SQUELCH"] = "ON";
            _settings["DGT_SQUELCH"] = "ON";
            _settings["DV"] = "ON";
            _settings["BFO"] = "+1000";
            _settings["CWOFFSET"] = "1000";
            _settings["ANTENNA"] = "bnc";
            _settings["RWAS"] = "ENABLED";
            _settings["UNKEY_M"] = "ENABLED";
            _settings["STEP"] = "00010000";
            _settings["RFG"] = "50";
            _settings["BEEP"] = "OFF";
            _settings["CONTRAST"] = "08";
            _settings["FMSQ_TYPE"] = "noise";
            _settings["FMTONE"] = "OFF";
            _settings["FMDEV"] = "5.0";
            _settings["PREPOST FILTER"] = "DISABLE";
            _settings["PREPOST RXANTENNA"] = "ENABLE";
            _settings["PREPOST SCAN"] = "FAST";
            _settings["ALL_CALL"] = "OFF";
            _settings["ANY_CALL"] = "OFF";
            _settings["AMD_DISPLAY"] = "OFF";
            _settings["KEY_TO_CALL"] = "ON";
            _settings["LSTN"] = "ON";
            _settings["RAD_SIL"] = "ON";
            _settings["MAXCH"] = "050";
            _settings["TUNETIME"] = "030";
            _settings["TIME_OUT"] = "010";

            // Operator lockouts (clone round 12): rows moved in BOTH families
            // and in all three sections, including one that goes the opposite
            // way from the baseline's — a write leg that only ever LOCKED, or
            // only ever touched one section, would still pass a same-direction
            // perturbation.
            _lockouts["PROGRAM/SSB/CHAN"] = "UNLOCK";
            _lockouts["PROGRAM/SSB/FILL"] = "LOCK";
            _lockouts["PROGRAM/EAM/LQA"] = "LOCK";
            _lockouts["PROGRAM/HOP/DATA"] = "UNLOCK";
            _lockouts["SELECT/SSB/BFO"] = "LOCK";
            _lockouts["SELECT/HOP/KEY"] = "LOCK";
            _lockouts["SELECT/EAM/KEY"] = "UNLOCK";

            // …and the operating mode itself.
            _prompt = HopPrompt;
        }
    }

    // =====================================================================
    // RENDERING — the captured layouts, filled from state.
    // =====================================================================

    private static string Two(int n) => n.ToString("00", Inv);

    private DemoChannel CurrentChannel()
        => _channels.TryGetValue(_currentChannel, out var channel) ? channel : new DemoChannel();

    /// <summary>The SH block's AGC spelling is the FULL word while the DI dump
    /// abbreviates (`SL`/`ME` captured, Stage 4 live gate). An uncaptured
    /// abbreviation is printed through verbatim rather than guessed at.</summary>
    private static string AgcFull(string dump) => dump.ToUpperInvariant() switch
    {
        "SL" => "SLOW",
        "ME" => "MED",
        var other => other,
    };

    /// <summary>SSB SH block — the verbatim capture's line set and order
    /// (protocol.md "SSB mode"), with the values taken from state.</summary>
    private string[] SsbShBlock()
    {
        var channel = CurrentChannel();
        return
        [
            "CHAN " + Two(_currentChannel), "KEY OFF",
            "RxFr " + channel.Rx, "TxFr " + channel.Tx,
            // The LIVE modulation and bandwidth: the stored row unless a DV
            // engagement is overlaying it (D1).
            "MODE " + LiveMode(channel), "AGC " + AgcFull(channel.Agc),
            "BAND " + LiveBandwidth(channel), "RXONLY " + channel.RxOnly,
            "BFO " + _settings["BFO"], "MODEM OFF",
            "DV " + _settings["DV"], "DGT_SQUELCH " + _settings["DGT_SQUELCH"],
            "AVS OFF", "ENCRYPT OFF",
            "SQ_LEVEL HIGH", "SQUELCH " + _settings["SQUELCH"],
            "POWER " + _settings["POWER"], "ANTENNA   " + _settings["ANTENNA"],
            "CWOFFSET " + _settings["CWOFFSET"], "RWAS " + _settings["RWAS"],
            "RETRANS DISABLED",
        ];
    }

    /// <summary>ALE SH block — verbatim capture (protocol.md "ALE SH block").
    /// IN_PROG is informational noise (probe R7), served for fidelity.</summary>
    private string[] AleShBlock()
    {
        var channel = CurrentChannel();
        return
        [
            "IN_PROG",
            "LSTN        " + _settings["LSTN"],
            "KEY_TO_CALL " + _settings["KEY_TO_CALL"],
            "RAD_SIL     " + _settings["RAD_SIL"],
            "ALL_CALL    " + _settings["ALL_CALL"],
            "ANY_CALL    " + _settings["ANY_CALL"],
            "MAXCH " + _settings["MAXCH"],
            "TUNETIME " + _settings["TUNETIME"],
            "TIME_OUT " + _settings["TIME_OUT"],
            "AMD_DISPLAY " + _settings["AMD_DISPLAY"],
            "CHAN " + Two(_currentChannel), "MODE " + LiveMode(channel),
            "RxFr " + channel.Rx, "TxFr " + channel.Tx,
            "KEY OFF", "MODEM OFF",
            "DV " + _settings["DV"], "DGT_SQUELCH " + _settings["DGT_SQUELCH"],
            "AVS OFF", "ENCRYPT OFF", "RWAS " + _settings["RWAS"],
        ];
    }

    /// <summary>HOP SH block — verbatim capture (protocol.md "HOP SH block"),
    /// with the CURRENT net's triplet.</summary>
    private string[] HopShBlock() =>
    [
        "NET  " + Two(_currentNet), "KEY OFF",
        .. DemoNetLines(_currentNet),
        "Hopnum 0041", "MODEM OFF", "ENCRYPT OFF", "POWER " + _settings["POWER"], "No_Sync",
    ];

    /// <summary>One net's DIS triplet, from state. Line shapes are the
    /// captures': the NB/WB forms all-caps, the LIST type echoing mixed-case
    /// "List", and a LIST net's VALUE line being the HOPLIST line itself
    /// (captured 2026-08-16).</summary>
    private string[] DemoNetLines(int net)
    {
        var record = _hopNets[net];
        if (record.Wiped)
            return
            [
                $"NETID    {Two(net)}  XXXXXXXX",
                $"Hoptype {Two(net)} WB",
                $"Hopset {Two(net)}  XXXXXX  XXXXXX",
            ];
        return record.Type switch
        {
            "NB" =>
            [
                $"NETID    {Two(net)}  {record.NetId}",
                $"Hoptype {Two(net)} NB",
                $"Center {Two(net)}  {record.Center}",
            ],
            "LIST" =>
            [
                $"NETID    {Two(net)}  {record.NetId}",
                $"Hoptype {Two(net)} List",
                HopListLine(net),
            ],
            _ =>
            [
                $"NETID    {Two(net)}  {record.NetId}",
                $"Hoptype {Two(net)} WB",
                $"Hopset {Two(net)}  {record.Low}  {record.High}",
            ],
        };
    }

    /// <summary>Session-16 HOPLIST shape ("HOPLIST 03   11010  11015").</summary>
    private string HopListLine(int net)
        => $"HOPLIST {Two(net)}   " + string.Join("  ", _hopNets[net].Frequencies);

    /// <summary>The captured single-band echo shape, trailing space and all
    /// ("Exclude 00  02000   03000 ").</summary>
    private static string ExcludeLine(int band, string low, string high)
        => $"Exclude {Two(band)}  {low}   {high} ";

    /// <summary>The captured session-15 listing columns: name in 4, the data
    /// mode phrase in 12, baud in 5, type in 7, interleave in 8.</summary>
    private string PresetLine(int number)
    {
        var preset = _presets[number];
        if (IsHopPreset(number)) return HopPresetLine(number, preset);
        var sb = new StringBuilder("MODEM PRESET ")
            .Append(number.ToString(Inv)).Append(' ')
            .Append(preset.Name.PadRight(4)).Append(' ')
            .Append(DataModeListing(preset.DataMode).PadRight(12))
            .Append(" BAUD ").Append(BaudListing(preset.Baud).PadRight(5))
            .Append(" TYPE ").Append(TypeListing(preset.Type).PadRight(7))
            .Append(' ');
        if (preset.Interleave is { Length: > 0 } interleave)
            sb.Append("INTER ").Append(interleave.PadRight(8));
        else if (preset.Mark is { Length: > 0 } mark && preset.Space is { Length: > 0 } space)
            sb.Append("MARK ").Append(mark).Append(" SPACE ").Append(space);
        return sb.ToString();
    }

    /// <summary>
    /// The SHORT <c>HOP&gt;</c> preset line, CAPTURED VERBATIM (P5, P5b, P5c) —
    /// name in a 4-column field, the mode phrase in 12, then <c>BAUD</c> and a
    /// 6-column value, and NOTHING after it: a HOP preset has no TYPE and no
    /// INTER field.
    /// <code>
    /// MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300···
    /// MODEM PRESET 9 TST9 SYNC  DATA   BAUD 75····
    /// </code>
    /// </summary>
    private static string HopPresetLine(int number, DemoPreset preset)
        => "MODEM PRESET " + number.ToString(Inv) + " "
           + preset.Name.PadRight(4) + " "
           + DataModeListing(preset.DataMode).PadRight(12) + " BAUD "
           + preset.Baud.PadRight(6);

    /// <summary>Presets 7-9 live at <c>HOP&gt;</c> and nowhere else (P5).</summary>
    private static bool IsHopPreset(int number) => number is >= 7 and <= 9;

    // The wire→LISTING spellings, as the radio prints them (docs/protocol.md
    // "Per-value evidence tiers"). The demo owns its own copy deliberately:
    // the app-layer vocabulary is the APP's column, and a demo that read it
    // would stop being an independent replay of the radio.
    private static string TypeListing(string wire) => wire switch
    {
        "39TONE" => "39tone", "FSKW" => "fskws", "FSKN" => "fskns",
        "FSK-A" => "fsk-a", "FSK-V" => "fsk-v", "SE" => "serial",
        _ => wire.ToLowerInvariant(),
    };

    private static string DataModeListing(string wire) => wire switch
    {
        "ASYNC REM" => "ASYNC REMOTE",
        "ASYNC DAT" => "ASYNC DATA",
        // The radio prints SYNC with TWO spaces (column padding, session-15).
        "SYNC DAT" => "SYNC  DATA",
        // F9: the HOP builder sends the words SPELLED OUT (P5b), so the stored
        // phrase can arrive in the long form too — and the radio still prints
        // SYNC padded (captured: `MODEM PRESET 9 TST9 SYNC  DATA   BAUD 300`).
        "SYNC DATA" => "SYNC  DATA",
        _ => wire,
    };

    private static string BaudListing(string wire) => wire == "VO" ? "Voice" : wire;

    private static string InterleaveListing(string wire) => wire switch
    {
        "LO" => "long", "SH" => "short", "ZE" => "zero",
        "ALTS" => "ALTS", "ALTL" => "ALTL",
        _ => wire.ToLowerInvariant(),
    };

    /// <summary>The captured listing column layout: the name sits in an
    /// 18-character field ("SLFAD ZZZ               CHGROUP 00", probe R7),
    /// and an associated self follows three spaces after the group.</summary>
    private static string ListingLine(string token, DemoEntry entry) =>
        entry.AssociatedSelf is { Length: > 0 } self
            ? $"{token} {entry.Name,-18}CHGROUP {entry.Group:00}   ASSOC SELF {self}"
            : $"{token} {entry.Name,-18}CHGROUP {entry.Group:00}";

    /// <summary>"CHGROUP 01 CHANS 00 01 " — probe R7, trailing space and
    /// all; one line, every channel on it.</summary>
    private static string ChannelGroupLine(int group, List<int> channels)
    {
        var sb = new StringBuilder("CHGROUP ").Append(Two(group)).Append(" CHANS");
        foreach (var channel in channels) sb.Append(' ').Append(Two(channel));
        return sb.Append(' ').ToString();
    }

    /// <summary>The session-23 DI dump line, decorative double space and
    /// all.</summary>
    private string ChannelLine(int number)
    {
        var c = _channels[number];
        return $"CH {Two(number)} RxFr {c.Rx} TxFr {c.Tx} MODE {c.Mode} AGC {c.Agc} BA {c.Bandwidth}  RXONLY {c.RxOnly}";
    }

    // =====================================================================
    // HOP (rule 4c) — reads and the programming writes
    // =====================================================================

    /// <summary>`DIS` dumps all ten nets, `DIS n` serves one, `HOPLIST n`
    /// serves that net's frequencies, `EXC` lists the exclusion bands, `NET n`
    /// selects. HOP-domain reads: at any other prompt this returns null (rule
    /// 6) rather than fabricate the real radio's reject.
    /// Caller holds _stateLock.</summary>
    private string[]? HopQueryReply(string command)
    {
        if (_prompt != HopPrompt) return null;      // HOP-domain reads only

        if (command == "DIS")
        {
            var all = new List<string>();
            for (int net = 0; net <= 9; net++) all.AddRange(DemoNetLines(net));
            return [.. all];
        }

        // Round 11 (R11/X9): the exclusion-band listing. An EMPTY table answers
        // NOTHING AT ALL (captured 2026-08-17) — the sentinel is what separates
        // read-empty from unread, so the demo must answer nothing too.
        if (command == "EXC")
            return [.. _excludes.OrderBy(kv => kv.Key).Select(kv => ExcludeLine(kv.Key, kv.Value.Low, kv.Value.High))];

        int space = command.IndexOf(' ');
        if (space <= 0) return null;
        var token = command[..space];
        var rest = command[(space + 1)..].Trim();
        if (!TryParseNet(rest, out int n)) return null;

        return token switch
        {
            "DIS" => DemoNetLines(n),
            "HOPLIST" when !_hopNets[n].Wiped && _hopNets[n].Type == "LIST" => [HopListLine(n)],
            // `NET n` never reaches here: it is a TWO-CHUNK reply and is served
            // by HopNetSelectChunks, above BuildResponse (round 15 §3.5).
            _ => null,
        };
    }

    /// <summary>
    /// <b>LABELLED DEMO FACT — HOP entry moves the SSB channel</b>
    /// (plan-clone-field-round2.md F1; owner report 2026-08-21, item 1).
    ///
    /// <para><b>Status: MODELLED, NOT CAPTURED.</b> What IS established is that
    /// the read campaign issues no <c>CH</c>, <c>NET</c>, <c>ST</c> or
    /// <c>SCA</c> at all — only queries and the mode lap <c>SSB&gt; → ALE&gt; →
    /// HOP&gt; → start</c> — and that the source radio came out of that lap on
    /// the wrong channel. Two captured behaviours explain it: a NET select
    /// SILENTLY CHANGES THE SSB CHANNEL (probe R9b, docs/probes.md) and HOP
    /// entry REGENERATES ON THE CURRENT NET (probe P4,
    /// <c>bench/transcripts/p4-hop-entry-route-20260821-180243.jsonl</c>: a bare
    /// <c>HO</c> runs the entry cycle once from SSB and twice from ALE). Which
    /// of the two imposes the channel was NOT settled — probe P1 (same P4
    /// transcript) ran three SSB→HOP→SSB and ALE→HOP→SSB laps on the bench radio
    /// at CH 00 and got CHAN 00 back every time, so the source radio's own state
    /// (CH 09 with net 0 current) was never reproduced.</para>
    ///
    /// <para>So this models the SIMPLEST shape consistent with both captures —
    /// entering HOP on a PROGRAMMED current net imposes that net's channel — and
    /// says out loud that the net→channel mapping is a demo STAND-IN (the net
    /// number), because no capture gives a real one. The fix under test is
    /// CAUSE-INDEPENDENT (it restores whatever moved), so the demo only has to
    /// move SOMETHING for the tests to fail for the real reason: without the
    /// closing restore, a read that started on CH 09 ends somewhere else.</para>
    ///
    /// <para>A wiped net has no hopset to generate from ("No Hopset" — the
    /// captured refusal), so it imposes nothing. Caller holds _stateLock.</para>
    /// </summary>
    private void NoteHopEntry()
    {
        if (!_hopNets.TryGetValue(_currentNet, out var net) || net.Wiped) return;
        _currentChannel = _currentNet;
    }

    /// <summary>The HOP PROGRAMMING writes (round 11 §9A — the clone replays
    /// them): the whole-record wipe, the type, the net id, the two hopset forms
    /// and the LIST surgery, plus the exclusion-band set/delete. Silent-success
    /// commands answer PROMPT-ONLY; the two with captured echoes emit them.
    /// Caller holds _stateLock.</summary>
    private string[]? HopProgramReply(string command)
    {
        if (_prompt != HopPrompt) return null;

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        switch (parts[0])
        {
            case "HOPSET" when parts.Length == 3 && parts[2] == "DEL" && TryParseNet(parts[1], out int wipe):
                // Wipes the ENTIRE net record, NETID included, and forces WB
                // (probe R9b).
                _hopNets[wipe] = new DemoHopNet();
                return NoPayload;

            case "HOPSET" when parts.Length == 3 && TryParseNet(parts[1], out int nb):
                _hopNets[nb].Wiped = false;
                _hopNets[nb].Type = "NB";
                _hopNets[nb].Center = parts[2];
                return NoPayload;

            case "HOPSET" when parts.Length == 4 && TryParseNet(parts[1], out int wb):
                _hopNets[wb].Wiped = false;
                _hopNets[wb].Type = "WB";
                _hopNets[wb].Low = parts[2];
                _hopNets[wb].High = parts[3];
                return NoPayload;

            case "HOPTYPE" when parts.Length == 3 && TryParseNet(parts[1], out int typeNet)
                                 && parts[2] is "NB" or "WB" or "LIST":
                _hopNets[typeNet].Wiped = false;
                _hopNets[typeNet].Type = parts[2];
                // The captured echo: NB/WB all-caps, LIST mixed-case "List".
                return [$"Hoptype {Two(typeNet)} {(parts[2] == "LIST" ? "List" : parts[2])}"];

            case "NETID" when parts.Length == 3 && TryParseNet(parts[1], out int idNet):
                _hopNets[idNet].Wiped = false;
                _hopNets[idNet].NetId = parts[2];
                return [$"NETID    {Two(idNet)}  {parts[2]}"];       // captured echo, session-16

            case "HOPLIST" when parts.Length >= 4 && TryParseNet(parts[1], out int addNet) && parts[2] == "ADD":
                _hopNets[addNet].Wiped = false;
                foreach (var frequency in parts[3..])
                    if (!_hopNets[addNet].Frequencies.Contains(frequency))
                        _hopNets[addNet].Frequencies.Add(frequency);
                _hopNets[addNet].Frequencies.Sort(StringComparer.Ordinal);
                return NoPayload;

            case "HOPLIST" when parts.Length == 4 && TryParseNet(parts[1], out int delNet) && parts[2] == "DEL":
                _hopNets[delNet].Frequencies.Remove(parts[3]);
                return NoPayload;

            case "EXC" when parts.Length == 3 && TryParseBand(parts[1], out int delBand) && parts[2] == "DEL":
                _excludes.Remove(delBand);
                return NoPayload;

            case "EXC" when parts.Length == 4 && TryParseBand(parts[1], out int setBand):
                // 8-DIGIT Hz in, kHz echo out (session-16).
                if (HzToKHz(parts[2]) is not { } low || HzToKHz(parts[3]) is not { } high) return null;
                _excludes[setBand] = (low, high);
                return [ExcludeLine(setBand, low, high)];

            default:
                return null;
        }
    }

    /// <summary>8-digit Hz → the 5-digit kHz the listing prints.</summary>
    private static string? HzToKHz(string hz)
    {
        if (hz.Length != 8 || !hz.All(char.IsAsciiDigit)) return null;
        return (long.Parse(hz, Inv) / 1000).ToString("00000", Inv);
    }

    // =====================================================================
    // SSB channels (rule 4d) — the dump read and the live programming writes
    // =====================================================================

    /// <summary>Round 6 (CJ): `DI a b` at the SSB prompt serves the stored
    /// channels that fall inside the requested range. No stored channel in
    /// range → rule 6 (prompt only) — which is exactly what an unprogrammed
    /// slot looks like. Caller holds _stateLock.</summary>
    private string[]? SsbChannelReply(string command)
    {
        if (_prompt != SsbPrompt) return null;      // DI is an SSB-domain read

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "DI") return null;
        if (!int.TryParse(parts[1], out int first) || !int.TryParse(parts[2], out int last)) return null;
        if (first > last || first < 0 || last > 99) return null;

        var lines = _channels.Keys
            .Where(n => n >= first && n <= last)
            .Order()
            .Select(ChannelLine)
            .ToArray();
        return lines.Length > 0 ? lines : null;
    }

    /// <summary>ROUND 11 §9A: the channel PROGRAMMING path — there is no
    /// channel-write command, so a channel is programmed by selecting it and
    /// setting the stored six live (protocol.md). `CH n` answers the captured
    /// `CHAN nn` and NOTHING else (Stage 4 live gate: the stored settings load
    /// SILENTLY); the field writes answer prompt-only, because no capture
    /// records what a store-excursion write echoes.
    /// Caller holds _stateLock.</summary>
    private string[]? SsbChannelWriteReply(string command)
    {
        if (_prompt != SsbPrompt) return null;

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var value = parts[1];

        if (parts[0] == "CH" && int.TryParse(value, out int channel) && channel is >= 0 and <= 99)
        {
            // D1 (iii) — THE CAPTURED MID-DV CHANNEL OBSERVATION. `CH nn`
            // answers `CHAN nn` ALONE, DV stays ON, and the newly loaded row
            // takes the same BAND shift the DV toggle itself makes. So the
            // overlay MIGRATES: the row being left goes back to what it stored,
            // and the row arriving is overlaid in its place.
            //
            // DEMO-CHOICE-UNCAPTURED — `DV OFF` after a mid-DV channel change.
            // Nothing captured says what a later `DV OFF` leaves behind. The
            // demo answers the CURRENT channel's stored row with the overlay
            // simply removed (which the overlay model gives for free), while
            // the displaced ANALOG SQUELCH rides across the re-seat, so it is
            // still the ENGAGEMENT's own value that comes back. Probe track:
            // engage DV, change channel, disengage, read both rows back.
            _currentChannel = channel;
            if (ActiveDv is not null) ReseatDvOverlay();
            return ["CHAN " + Two(channel)];
        }

        var target = _channels.TryGetValue(_currentChannel, out var stored) ? stored : null;
        switch (parts[0])
        {
            case "RXF" or "FR" or "TXF" or "MODE" or "AG" or "BA" or "RXON":
                // A field write on an unprogrammed slot PROGRAMS it — that is
                // what "there is no channel-write command" means.
                target ??= _channels[_currentChannel] = new DemoChannel();
                break;
            default:
                return null;
        }

        switch (parts[0])
        {
            case "FR": target.Rx = value; target.Tx = value; break;
            case "RXF": target.Rx = value; break;
            case "TXF": target.Tx = value; break;
            // D1 (ii) — the R4 excursion rides on the modulation write, and is
            // SILENT: the answer is still prompt-only.
            case "MODE": target.Mode = value; ApplyDvExcursion(value); break;
            case "AG": target.Agc = AgcDump(value); break;
            case "BA": target.Bandwidth = value; break;
            case "RXON": target.RxOnly = value; break;
        }
        return NoPayload;
    }

    /// <summary>The DI dump's own AGC abbreviations (`SL`/`ME` captured); an
    /// uncaptured spelling is stored verbatim rather than abbreviated by
    /// guesswork.</summary>
    private static string AgcDump(string wire) => wire switch
    {
        "SLOW" => "SL",
        "MED" => "ME",
        _ => wire,
    };

    private static bool TryParseNet(string arg, out int net)
    {
        net = -1;
        arg = arg.Trim();
        return arg.Length > 0 && int.TryParse(arg, out net) && net is >= 0 and <= 9;
    }

    private static bool TryParseBand(string arg, out int band) => TryParseNet(arg, out band);

    // =====================================================================
    // Operator lockouts (clone round 12 §3)
    // =====================================================================

    /// <summary>The section a SET lands in: the ACTIVE PROMPT's mode section.
    /// COPIED FROM THE P-1 CAPTURE (2026-08-18) — all six discrimination-matrix
    /// cells moved exactly their own prompt's section and nothing else, and the
    /// ALE prompt moved the EAM section, settling that the report's "EAM"
    /// heading is the ALE/EAM mode family. Caller holds _stateLock.</summary>
    private string PromptSection() => _prompt switch
    {
        HopPrompt => "HOP",
        AlePrompt => "EAM",
        _ => "SSB",
    };

    /// <summary>
    /// `PROGRAM` / `SELECT` — the bare GLOBAL STATE REPORTS and the per-item
    /// sets, answered at EVERY prompt (both reports are global from whichever
    /// prompt the radio happens to be at — captured).
    ///
    /// <para>A SET answers by ECHOING ITS OWN COMMAND VERBATIM, with no
    /// accept/reject semantics whatever — the state report is the only
    /// confirmation there is. The demo reproduces that exactly, including for
    /// an item this firmware does not have: the radio echoes, and the report
    /// afterwards is what tells you nothing moved.</para>
    /// Caller holds _stateLock.
    /// </summary>
    private string[]? LockoutReply(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is not ("PROGRAM" or "SELECT")) return null;
        var family = parts[0];

        if (parts.Length == 1) return LockoutReport(family);

        if (parts.Length != 3 || parts[2] is not ("LOCK" or "UNLOCK")) return null;
        var state = parts[2];

        if (parts[1] == "ALL")
        {
            foreach (var key in LockoutKeys)
                if (key.StartsWith(family + "/", StringComparison.Ordinal))
                    _lockouts[key] = state;
            return [command];                       // the captured verbatim echo
        }

        // PROMPT-SCOPED, per P-1. An item the active section does not carry
        // moves nothing — and still echoes, exactly as the radio does.
        var target = $"{family}/{PromptSection()}/{parts[1]}";
        if (_lockouts.ContainsKey(target)) _lockouts[target] = state;
        return [command];
    }

    /// <summary>One family's whole report, sectioned by the captured headers.
    /// Caller holds _stateLock.</summary>
    private string[] LockoutReport(string family)
    {
        var lines = new List<string>();
        string? section = null;
        var word = family == "PROGRAM" ? "Programmable" : "Selectable";
        foreach (var key in LockoutKeys)
        {
            var bits = key.Split('/');
            if (bits[0] != family) continue;
            if (bits[1] != section)
            {
                section = bits[1];
                lines.Add($">>{section}_{word}_Parameters");
            }
            lines.Add($"{family} {bits[2]} {_lockouts[key]}");
        }
        return [.. lines];
    }

    // =====================================================================
    // SSB settings (rule 5) — the bare QUERIES and the SETS
    // =====================================================================

    /// <summary>ROUND 14 B: <c>INTCOUPLER</c>, the internal antenna coupler —
    /// query and set, and the ONE settings helper here with NO PROMPT GATE.
    ///
    /// <para>That is a captured fact, not a convenience: P-1 (2026-08-20, runs
    /// A/B/C — docs/protocol.md "INTCOUPLER is FULLY GRADUATED") answered the
    /// query and accepted the set at <c>SSB&gt;</c>, <c>HOP&gt;</c> AND
    /// <c>ALE&gt;</c>, with the identical echo at each. The HOP settings pane's
    /// coupler row and its landing read both fire at <c>HOP&gt;</c>, so a demo
    /// that gated this on SSB would answer nothing exactly where the round-14
    /// row lives.</para>
    ///
    /// <para>Both shapes are CAPTURED, so both are answered (replay doctrine —
    /// nothing invented): the query answers <c>INTCoupler Enabled</c> in the
    /// radio's own MIXED CASE (the parser uppercases it into the mirror), and
    /// the SET echoes the NEW state in that same shape. The state PERSISTS for
    /// the demo session, so a query after a set reads the value that was set —
    /// which is what makes the row demonstrable offline at all.</para>
    /// Caller holds _stateLock.</summary>
    private string[]? InternalCouplerReply(string command)
    {
        if (command == "INTCOUPLER")
            return [CouplerAnswer];

        // The set spellings are the sent forms (Wire.cs BypassEnable.ToWire);
        // the REPORT spellings differ, which is the whole reason the echo is
        // built from the stored state rather than from the argument.
        string? state = command switch
        {
            "INTCOUPLER BYPASS" => "Bypassed",
            "INTCOUPLER ENABLE" => "Enabled",
            _ => null,
        };
        if (state is null) return null;

        _settings[InternalCouplerKey] = state;
        return [CouplerAnswer];
    }

    /// <summary>The captured line shape, filled from state — one spelling for
    /// the query answer and the set echo alike, because the radio gave one.</summary>
    private string CouplerAnswer => "INTCoupler " + _settings[InternalCouplerKey];

    /// <summary>The coupler's key in <see cref="_settings"/>. Its VALUE is the
    /// report word in the radio's mixed case ("Enabled"/"Bypassed"), not the
    /// set token.</summary>
    private const string InternalCouplerKey = "INTCOUPLER";

    /// <summary>ROUND 11 §9A: the manifest settings' BARE queries. Every line
    /// emitted is the captured answer shape for that token, filled from state.
    /// SSB-domain: outside SSB these stay at rule 6.
    /// Caller holds _stateLock.</summary>
    private string[]? SsbQueryReply(string command)
    {
        if (_prompt != SsbPrompt) return null;

        return command switch
        {
            "STEP" => ["STEP " + _settings["STEP"]],
            "RF" => ["RFG " + _settings["RFG"]],
            "BEEP" => ["BEEP " + _settings["BEEP"]],
            "UNKEY_M" => ["UNKEY_M " + _settings["UNKEY_M"]],
            "RWAS" => ["RWAS " + _settings["RWAS"]],
            "FMSQ_T" => ["FMSQ_TYPE " + _settings["FMSQ_TYPE"]],
            "FMTONE" => ["FMTONE " + _settings["FMTONE"]],
            "FMDE" => ["FMDEV " + _settings["FMDEV"]],
            "CWOFF" => ["CWOFFSET " + _settings["CWOFFSET"]],
            // CLONE ROUND 12 §9 B3, the PRIMARY branch: bare `COM` ANSWERS
            // (`COMPRESS ON`, captured 2026-08-18, bench/transcripts/r12-p2-*
            // step c). Until that capture nothing anywhere could read
            // compression back, so the demo could not answer it either.
            "COM" => ["COMPRESS " + _settings["COMPRESS"]],
            "ANTENNA" => ["ANTENNA   " + _settings["ANTENNA"]],
            "CONT" => ["CONTRAST " + _settings["CONTRAST"]],
            "PREPOST FILTER" => ["PREPOST FILTER " + _settings["PREPOST FILTER"]],
            "PREPOST RXANTENNA" => ["PREPOST RXANTENNA " + _settings["PREPOST RXANTENNA"]],
            "PREPOST SCAN" => ["PREPOST SCAN " + _settings["PREPOST SCAN"]],
            _ => null,
        };
    }

    /// <summary>Rule 5 (plan-gui-rejigger.md Wave 2, extended round 11 §9A):
    /// the SSB settings SETS. A set whose read-back shape IS captured answers
    /// with it (so the mirror moves and the button lights); a set whose echo
    /// was never captured mutates state and answers PROMPT-ONLY — never
    /// invented wire text (replay doctrine). These are SSB-mode answers:
    /// outside SSB the radio rejects them, so the demo stays at rule 6 rather
    /// than fabricate the reject shapes.
    /// Caller holds _stateLock.</summary>
    private string[]? SsbSettingReply(string command)
    {
        // MODEM IS NOT SSB-DOMAIN, and answering it from inside this method's
        // SSB gate made the demo silent at the other two prompts — a
        // fabricated behaviour, and the opposite of what the bench captured.
        // Probe R8 and the round-13 ALE run give `ALE>` full SSB parity, and
        // P5-P5d2 give `HOP>` its own preset band (7-9). The family is answered
        // BEFORE the gate; ModemReply owns the prompt scoping from there.
        if (command.StartsWith("MODEM ", StringComparison.Ordinal))
            return ModemReply(command["MODEM ".Length..].Trim());

        if (_prompt != SsbPrompt) return null;      // SSB-domain settings only

        int space = command.IndexOf(' ');
        if (space <= 0) return null;
        var token = command[..space];
        var arg = command[(space + 1)..].Trim();
        if (arg.Length == 0) return null;

        switch (token)
        {
            // Squelch family — three independent peers (protocol.md digital-
            // squelch section). Each answers its own token with the set value.
            case "SQ" when IsOnOff(arg):
                _settings["SQUELCH"] = arg;
                return ["SQUELCH " + arg];
            case "DGT_S" when IsOnOff(arg):
                _settings["DGT_SQUELCH"] = arg;
                return ["DGT_SQUELCH " + arg];
            case "FMSQ" when IsOnOff(arg):
                _settings["FMSQUELCH"] = arg;
                return ["FMSQUELCH " + arg];
            // Digital voice: the D1 matrix's own echo — `MODEM OFF` / `DV x` /
            // `DGT_SQUELCH x`, with NO `MODE` line (see the DIGITAL VOICE
            // region below; the silent-mutation shape is the point).
            //
            // The DGT_SQUELCH rider is a REPORT, NOT A MUTATION (protocol.md,
            // digital squelch section, bench-confirmed 2026-08-02: "DGT_S is NOT
            // gated on digital voice — it is settable and readable with DV OFF,
            // SURVIVES DV ON/DV OFF toggling, and KEEPS ITS VALUE"; the line is
            // merely "reported inside the DV response group, which is
            // presumably why the legacy GUI treated it as a digital-voice
            // sub-setting. It is not one."). The demo used to FORCE it OFF —
            // the legacy GUI's own mistake, re-implemented (P6 audit round 1,
            // MAJOR). It now reports whatever the digital squelch actually is.
            case "DV" when IsOnOff(arg):
                return DigitalVoiceReply(arg);
            // Squelch level: only "SQ_LEVEL HIGH" is a captured spelling; the
            // LO/MEDIUM answer spellings are uncaptured → rule 6.
            case "SQ_L" when arg == "HIGH":
                return ["SQ_LEVEL HIGH"];
            case "COM" when IsOnOff(arg):
                _settings["COMPRESS"] = arg;
                return ["COMPRESS " + arg];
            case "BF" when IsSignedFourDigit(arg):
                _settings["BFO"] = arg;
                return ["BFO " + arg];
            case "CWOFF" when arg.Length == 4 && arg.All(char.IsAsciiDigit):
                _settings["CWOFFSET"] = arg;
                return ["CWOFFSET " + arg];
            case "ANTENNA" when arg is "BNC" or "AUTO" or "TUNED":
                _settings["ANTENNA"] = arg.ToLowerInvariant();
                return NoPayload;                    // set echo uncaptured
            case "RWAS" when arg is "ENA" or "DIS":
                // ASYMMETRIC — RE-BASED, clone round 12 §4. BOTH directions
                // REPORT the four squelch lines alongside the RWAS line (so no
                // re-poll is ever needed either way), but only **`RWAS ENA`
                // FORCES** the three squelches ON. The 2026-08-18 §14 bench
                // session disproved the both-ways form the P6 audit had
                // installed here: `RWAS DIS`, issued with analog and digital
                // squelch OFF, answered `SQUELCH OFF` / `FMSQUELCH ON` /
                // `DGT_SQUELCH OFF` and left them exactly so, re-queried one by
                // one. A demo that forced on DISABLE would let the clone
                // campaign's ORDER column be wrong in the safe direction and
                // never notice.
                _settings["RWAS"] = arg == "ENA" ? "ENABLED" : "DISABLED";
                if (arg == "ENA")
                {
                    _settings["SQUELCH"] = "ON";
                    _settings["DGT_SQUELCH"] = "ON";
                    _settings["FMSQUELCH"] = "ON";
                }
                return
                [
                    "RWAS " + _settings["RWAS"],
                    "SQ_LEVEL HIGH",
                    "SQUELCH " + _settings["SQUELCH"],
                    "FMSQUELCH " + _settings["FMSQUELCH"],
                    "DGT_SQUELCH " + _settings["DGT_SQUELCH"],
                ];
            case "UNKEY_M" when arg is "ENA" or "DIS":
                _settings["UNKEY_M"] = arg == "ENA" ? "ENABLED" : "DISABLED";
                return ["UNKEY_M " + _settings["UNKEY_M"]];
            case "STEP" when arg.Length == 8 && arg.All(char.IsAsciiDigit):
                _settings["STEP"] = arg;
                return ["STEP " + arg];
            case "RF" when arg.All(char.IsAsciiDigit):
                _settings["RFG"] = arg;
                return ["RFG " + arg];
            case "BEEP" when IsOnOff(arg):
                _settings["BEEP"] = arg;
                return ["BEEP " + arg];
            case "CONT" when arg.All(char.IsAsciiDigit):
                _settings["CONTRAST"] = int.Parse(arg, Inv).ToString("00", Inv);
                return ["CONTRAST " + _settings["CONTRAST"]];
            case "FMSQ_T" when arg is "NOISE" or "TONE":
                _settings["FMSQ_TYPE"] = arg.ToLowerInvariant();
                return NoPayload;                    // set echo uncaptured
            case "FMTONE" when IsOnOff(arg):
                _settings["FMTONE"] = arg;
                return NoPayload;                    // set echo uncaptured
            case "FMDE" when arg is "5.0" or "6.5" or "8.0":
                _settings["FMDEV"] = arg;
                return NoPayload;                    // set echo uncaptured
            case "POW" when arg is "LOW" or "MED" or "HI":
                _settings["POWER"] = arg.ToLowerInvariant();
                return NoPayload;                    // set echo uncaptured
            case "PREPOST":
                return PrePostReply(arg);
            // (MODEM is handled above the SSB gate — it is cross-mode.)
            default:
                return null;
        }
    }

    // =====================================================================
    // DIGITAL VOICE — the D1 interaction matrix (clone round 12 P4)
    //
    // Modelled ENTIRELY from the CAPTURED sequences (docs/protocol.md "Digital
    // voice — the interaction matrix (D1)", captured r12-p2, plus the R4
    // excursion). Where a transition was never captured the demo takes a NAMED
    // CHOICE and says so — never invented behaviour dressed as a capture.
    //
    // WHAT THE CAPTURE ESTABLISHES:
    //   (i)  five per-modulation legs. `DV ON` stores the entry modulation /
    //        analog squelch / bandwidth tuple and forces USB (from AME, CW, FM
    //        — "silently forces USB", the echo carries NO MODE line), analog
    //        SQUELCH ON, and BAND 3.0, "in EVERY case, USB and LSB included".
    //        Same-channel `DV OFF` "reversed every one of them, restoring the
    //        entry modulation, bandwidth and squelch exactly".
    //   (ii) the excursion (probe R4): with DV ON, a modulation leaving
    //        USB/LSB AUTO-SUSPENDS DV (a query then reads `DV OFF`, silently),
    //        and returning AUTO-RESTORES it. No compensation is wanted; the
    //        radio manages it.
    //   (iii) the mid-DV channel observation: at `CH 02` with `DV ON`,
    //        selecting `CH 01` answered `CHAN 01` ALONE and left `DV ON`
    //        standing, "with the same BAND 2.7 → 3.0 shift the DV toggle
    //        itself makes" — i.e. the overlay follows the newly loaded row.
    //
    // THE ONE PROSE-SOURCED HALF, named because the table does not show it: the
    // USB and LSB rows list only the BAND move, and the headline paragraph is
    // what says the squelch forcing happens "in EVERY case, USB and LSB
    // included". The demo follows the prose; a capture that contradicts it is a
    // content fix here, not a redesign.
    // =====================================================================

    /// <summary>The bandwidth `DV ON` forces, in every captured leg.</summary>
    private const string DvBandwidth = "3.0";

    /// <summary>
    /// A standing DV engagement, held as a LIVE OVERLAY over the current
    /// channel's stored row rather than as a mutation of it.
    ///
    /// <para><b>Why an overlay and not a write-through.</b> The demo's live
    /// modulation and bandwidth ARE the current channel's stored row (round 11
    /// §9A: "there is no channel-write command"), and nothing captured says the
    /// DV forcing edits the stored TABLE — the `DI` dump was never read across
    /// a DV toggle. Writing through would therefore invent a mutation, and it
    /// would be visible: a clone campaign writes `DigitalVoice` after the
    /// channel leg, so a write-through would silently undo a channel it had
    /// just programmed. The overlay changes what the radio REPORTS, which is
    /// all the capture speaks to.</para>
    ///
    /// <para><paramref name="Sideband"/> is the sideband DV is operating on
    /// (USB unless the entry modulation was LSB, which the capture leaves
    /// alone) — the one the R4 excursion auto-restores on.
    /// <paramref name="Squelch"/> is the DISPLACED analog squelch, which IS a
    /// live setting and IS forced, so `DV OFF` can put it back exactly.</para>
    /// </summary>
    private sealed record DvOverlay(string Mode, string Bandwidth, string Squelch, string Sideband);

    private DvOverlay? _dvOverlay;
    private bool _dvSuspended;

    /// <summary>The overlay while it is actually in force — an auto-suspended
    /// engagement reports the radio's real modulation. Caller holds
    /// _stateLock.</summary>
    private DvOverlay? ActiveDv => _dvSuspended ? null : _dvOverlay;

    /// <summary>What the radio REPORTS for DV: engaged and not auto-suspended.
    /// Caller holds _stateLock.</summary>
    private string ReportedDv() => ActiveDv is not null ? "ON" : "OFF";

    /// <summary>The live modulation/bandwidth the SH blocks print: the stored
    /// row, with the DV overlay over it where one stands. Caller holds
    /// _stateLock.</summary>
    private string LiveMode(DemoChannel channel) => ActiveDv?.Mode ?? channel.Mode;

    private string LiveBandwidth(DemoChannel channel) => ActiveDv?.Bandwidth ?? channel.Bandwidth;

    /// <summary>Engage DV over the CURRENT channel: force USB (from anything
    /// that is not already a sideband), BAND 3.0, and analog SQUELCH ON.
    /// Caller holds _stateLock.</summary>
    private void ApplyDvOverlay()
    {
        var channel = CurrentChannel();
        bool sideband = channel.Mode is "USB" or "LSB";
        var forced = sideband ? channel.Mode : "USB";
        _dvOverlay = new DvOverlay(forced, DvBandwidth, _settings["SQUELCH"], forced);
        _settings["SQUELCH"] = "ON";
        _dvSuspended = false;
    }

    /// <summary>Re-seat a standing engagement over the row just loaded, keeping
    /// the DISPLACED squelch (which belongs to the engagement, not to the
    /// channel). Caller holds _stateLock.</summary>
    private void ReseatDvOverlay()
    {
        if (_dvOverlay is not { } overlay) return;
        var channel = CurrentChannel();
        var forced = channel.Mode is "USB" or "LSB" ? channel.Mode : "USB";
        _dvOverlay = overlay with { Mode = forced, Bandwidth = DvBandwidth, Sideband = forced };
    }

    /// <summary>Disengage DV: the overlay lifts (revealing the stored row
    /// exactly as it was) and the displaced squelch goes back.
    /// Caller holds _stateLock.</summary>
    private void RemoveDvOverlay()
    {
        if (_dvOverlay is not { } overlay) return;
        _settings["SQUELCH"] = overlay.Squelch;
        _dvOverlay = null;
    }

    /// <summary>
    /// `DV ON|OFF`. The echo is the captured one everywhere — `MODEM OFF`,
    /// `DV x`, `DGT_SQUELCH x`, and no `MODE` line however much the modulation
    /// moved.
    ///
    /// <para><b>DEMO-CHOICE-UNCAPTURED — repeated / idempotent commands.</b>
    /// `DV ON` while DV already reads ON, and `DV OFF` while DV is
    /// AUTO-SUSPENDED, were never captured. The demo answers the echo and
    /// MOVES NOTHING: re-engaging would overwrite the stored entry tuple with
    /// the forced values (so the eventual `DV OFF` could no longer restore it),
    /// and un-suspending would fight the operator's own `MODE` write. Probe
    /// track: two commands at the bench settle both. A capture that disagrees
    /// is a content fix here.</para>
    /// Caller holds _stateLock.
    /// </summary>
    private string[] DigitalVoiceReply(string arg)
    {
        bool engaged = _dvOverlay is not null || _settings["DV"] == "ON";
        if (arg == "ON")
        {
            if (!engaged) ApplyDvOverlay();
        }
        else if (_dvOverlay is not null && !_dvSuspended)
        {
            RemoveDvOverlay();                  // the captured leg: restore exactly
        }
        else
        {
            _dvOverlay = null;                  // uncaptured — drop it, restore nothing
        }
        if (arg == "OFF") _dvSuspended = false;

        _settings["DV"] = ReportedDv();
        return ["MODEM OFF", "DV " + _settings["DV"], "DGT_SQUELCH " + _settings["DGT_SQUELCH"]];
    }

    /// <summary>The R4 excursion, applied to a `MODE` write: leaving USB/LSB
    /// with DV engaged auto-SUSPENDS it, and returning to the sideband DV was
    /// operating on auto-RESTORES it — both silently, which is why the write
    /// still answers prompt-only. A move to the OTHER sideband is uncaptured
    /// and deliberately changes nothing. Caller holds _stateLock.</summary>
    private void ApplyDvExcursion(string modulation)
    {
        if (_dvOverlay is not { } overlay) return;
        if (modulation is not ("USB" or "LSB")) _dvSuspended = true;
        else if (modulation == overlay.Sideband) _dvSuspended = false;
        _settings["DV"] = ReportedDv();
    }

    /// <summary>The DV overlay's own reset, shared by the baseline and the
    /// wipe: a factory-fresh (or freshly wiped) radio has no engagement.
    /// Caller holds _stateLock.</summary>
    private void ResetDigitalVoice()
    {
        _dvOverlay = null;
        _dvSuspended = false;
    }

    /// <summary>`PREPOST FILTER|RXANTENNA|SCAN [value]` — the two-token form
    /// is the QUERY (answered by <see cref="SsbQueryReply"/>), the three-token
    /// form is the SET. Caller holds _stateLock.</summary>
    private string[]? PrePostReply(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var key = "PREPOST " + parts[0];
        if (!_settings.ContainsKey(key)) return null;
        if (parts[0] == "SCAN" ? parts[1] is not ("SLOW" or "FAST") : parts[1] is not ("ENABLE" or "DISABLE"))
            return null;
        _settings[key] = parts[1];
        return [key + " " + parts[1]];
    }

    // =====================================================================
    // Modem presets
    // =====================================================================

    // The stored-preset listing line the round-8 programming ECHO answers —
    // session-15, VERBATIM, unchanged.
    private const string CannedPresetListingLine =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long";

    // Round 9: the write the app composes for that same preset, in the short-
    // token vocabulary. It answers the SAME captured listing echo, so a T39
    // re-store still round-trips in DEMO — but this mapping is PROVISIONAL:
    // the app's line is no longer byte-identical to the session-15 capture
    // ("ASYNC DAT" vs the captured "ASYNC DATA"), because no short-token write
    // had ever been sent when it was written. Bench item A6d settles it.
    private const string CapturedPresetWrite =
        "PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400";

    /// <summary>§9 A1: the refusal for selecting a locked-out preset. Its
    /// spelling comes from the app's own verbatim "Unrecognized message"
    /// banner at the 2026-08-18 bench session — the defect WAS the capture.</summary>
    private const string PresetDisabledLine = "PRESET DISABLED";

    /// <summary>`MODEM …` at the SSB prompt: the select echoes, the bulk and
    /// targeted preset reads, and (round 11 §9A) the preset PROGRAMMING write,
    /// which now MOVES the stored preset instead of only echoing.
    /// Caller holds _stateLock.</summary>
    private string[]? ModemReply(string arg)
    {
        // ---- THE PROMPT SCOPE (clone-field round 2 F9/F10) -----------------
        // CAPTURED 2026-08-21 (P5-P5d2): the modem book is SPLIT BY PROMPT.
        // `SSB>`/`ALE>` own presets 0-6 and `HOP>` owns 7-9; each prompt answers
        // `INVALID MODEM PRESET` for the other's numbers, on reads, writes and
        // selects alike. Round 13's T1 probe read the HOP half as a WHOLESALE
        // refusal because it only ever asked for 0-6 — the half HOP does not
        // have.
        bool hop = _prompt == HopPrompt;
        bool InScope(int preset) => IsHopPreset(preset) == hop;

        switch (arg)
        {
            // At `HOP>` `MODEM OF` is SILENT (P5d: the answer is the prompt
            // alone, and a following `MODEM SH` reports `MODEM OFF`). The
            // `SSB>` echo is the long-standing one and is untouched.
            case "OF": return hop ? NoPayload : ["MODEM OFF"];
            // The BULK listing serves every stored preset OF THIS PROMPT'S BAND
            // except the disabled ones — presence in it is the ONLY captured
            // EN/DIS signal. `NO PRESETS ENABLED` for an empty HOP listing is
            // CAPTURED (P5, P5b, r13 T1); the empty `SSB>` answer has never been
            // captured, so that branch is left exactly as it was.
            case "PRE":
            {
                var listed = _presets.Keys.Where(InScope).Where(_enabledPresets.Contains)
                    .Order().Select(PresetLine).ToArray();
                return hop && listed.Length == 0 ? ["NO PRESETS ENABLED"] : listed;
            }
        }

        // SELECT by number or name (`MODEM 1` / `MODEM T39`, both bench-proven).
        //
        // CLONE ROUND 12 §9 A1 — selecting a DISABLED preset answers
        // `PRESET DISABLED`. The demo used to answer NOTHING off-script here,
        // which is why the app's own "Unrecognized message" banner at the bench
        // was the first anyone saw of the line. The spelling below IS that
        // banner's capture.
        if (arg.Length is >= 1 and <= 4 && arg.All(char.IsAsciiLetterOrDigit))
        {
            int? selected = null;
            foreach (var (number, preset) in _presets)
                if (arg == number.ToString(Inv)
                    || string.Equals(arg, preset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    selected = number;
                    break;
                }

            if (selected is { } n)
            {
                // OUT OF SCOPE: `MODEM 1` at `HOP>` answers `INVALID MODEM
                // PRESET` FOLLOWED BY a `MODEM OFF` state line (CAPTURED, P5d)
                // — the select is refused and the engagement is reported
                // unchanged. The mirror-image case (7-9 selected at `SSB>`) has
                // only ever been captured for the READ form, so the demo
                // answers the refusal alone there rather than inventing the
                // second line.
                if (!InScope(n)) return hop ? ["INVALID MODEM PRESET", "MODEM OFF"] : ["INVALID MODEM PRESET"];
                return _enabledPresets.Contains(n)
                    ? ["MODEM " + n.ToString(Inv) + " " + _presets[n].Name]
                    : [PresetDisabledLine];
            }
        }

        if (arg.StartsWith("PRE ", StringComparison.Ordinal))
        {
            // The TARGETED read serves ANY stored preset OF THIS PROMPT'S BAND,
            // disabled ones included — the only way to see a disabled preset's
            // fields. The other band answers `INVALID MODEM PRESET` (CAPTURED
            // both directions: P5 sent `MODEM PRE 7/8/9` at `SSB>` and `ALE>`
            // and `MODEM PRE 0` at `HOP>`).
            var number = arg[4..].Trim();
            if (!int.TryParse(number, out int preset)) return null;
            if (!InScope(preset)) return ["INVALID MODEM PRESET"];
            return _presets.ContainsKey(preset) ? [PresetLine(preset)] : null;
        }

        if (arg.StartsWith("PRESET ", StringComparison.Ordinal))
        {
            // A write to the other band's number is refused the same way
            // (CAPTURED: P5b sent `MODEM PRESET 9 BAUD 300` at `SSB>`).
            var head = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length > 1 && int.TryParse(head[1], out int target) && !InScope(target))
                return ["INVALID MODEM PRESET"];
            // A `TYPE` argument at `HOP>` draws `** ERROR **` and changes
            // NOTHING (CAPTURED, P5b: twice for a bare `TYPE 39tone`, three
            // times for the full SSB-form line). A HOP preset has no type field.
            if (hop && arg.Contains(" TYPE ", StringComparison.Ordinal))
                return [ErrorBanner, ErrorBanner];

            bool captured = arg == CapturedPresetWrite;
            var stored = ApplyPresetWrite(arg);
            if (stored is null) return null;
            // CAPTURED (P5b): at `HOP>` a bare `MODEM PRESET n DIS` answers
            // NOTHING, while `… EN` echoes the preset's line. Both still move
            // the enabled set — the listing is where the difference shows.
            if (hop && head.Length == 3 && head[2] == "DIS") return NoPayload;
            // The captured write keeps its VERBATIM session-15 echo; any other
            // write echoes the stored preset's listing line, which is the same
            // captured shape with this preset's values.
            return captured ? [CannedPresetListingLine] : [PresetLine(stored.Value)];
        }
        return null;
    }

    /// <summary>Apply `PRESET n NAME x TYPE t &lt;datamode&gt; BAUD b
    /// [INTERLEAV i] [MARK m SPACE s] [EN|DIS]`. Only the fields the LINE
    /// CARRIES change — the radio has no way to un-say a field the write left
    /// off, which is why a write without INTERLEAV keeps the stored one.
    /// Returns the preset number, or null when the line is not one this
    /// firmware would take. Caller holds _stateLock.</summary>
    private int? ApplyPresetWrite(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int number) || number is < 0 or > 9)
            return null;
        // F9: a HOP preset (7-9) has NO type field, and its baud vocabulary is
        // {75, 150, 300} — everything else the radio SILENTLY IGNORES, echoing
        // the line with the OLD value and no error (CAPTURED, P5c: nine values
        // swept by set + read-back on preset 9). The caller has already refused
        // a TYPE argument at this prompt; the baud filter is here because the
        // failure is invisible in the answer and the demo must reproduce that.
        bool hopPreset = IsHopPreset(number);

        // A write to a slot that holds NO record PROGRAMS it — the same thing
        // "there is no channel-write command" means for channels. It matters
        // after a `ZERO`, which clears the preset records: the clone's preset
        // leg has to be able to put them back, and on the real radio the seven
        // slots are always addressable.
        if (!_presets.TryGetValue(number, out var preset))
            preset = _presets[number] = new DemoPreset();
        bool wroteAField = false;
        bool? explicitState = null;
        for (int i = 2; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "NAME" when i + 1 < parts.Length: preset.Name = parts[++i]; wroteAField = true; break;
                case "TYPE" when i + 1 < parts.Length: preset.Type = parts[++i]; wroteAField = true; break;
                case "BAUD" when i + 1 < parts.Length:
                    var wantedBaud = parts[++i];
                    // Silently ignored outside the HOP vocabulary — the write
                    // still counts as a field write (P5b: `MODEM PRESET 9 BAUD
                    // 600` left the baud at 300 and RE-ENABLED the preset).
                    if (!hopPreset || Falcon.Core.Protocol.Wire.HopModemBauds.Contains(wantedBaud))
                        preset.Baud = wantedBaud;
                    wroteAField = true;
                    break;
                case "INTERLEAV" when i + 1 < parts.Length:
                    preset.Interleave = InterleaveListing(parts[++i]);
                    wroteAField = true;
                    break;
                case "MARK" when i + 1 < parts.Length: preset.Mark = parts[++i]; wroteAField = true; break;
                case "SPACE" when i + 1 < parts.Length: preset.Space = parts[++i]; wroteAField = true; break;
                case "ASYNC" or "SYNC" when i + 1 < parts.Length:
                    preset.DataMode = parts[i] + " " + parts[++i];
                    wroteAField = true;
                    break;
                case "EN": explicitState = true; break;
                case "DIS": explicitState = false; break;
            }
        }

        // CLONE ROUND 12 §9 A4 — **ANY FIELD WRITE RE-ENABLES A DISABLED
        // PRESET** (captured 2026-08-18, protocol.md modem table: `MODEM PRESET
        // 6 BAUD 1200`, sent to a preset ABSENT from the bulk listing, echoed
        // and put the row BACK in it). The lockout is not a field the operator
        // sets and leaves. This is exactly why the app writes the state token
        // LAST: an explicit EN/DIS on the same line still decides, and the
        // resolution below runs after the whole line is read so token ORDER
        // inside the line cannot change the outcome.
        if (explicitState is { } wanted)
        {
            if (wanted) _enabledPresets.Add(number); else _enabledPresets.Remove(number);
        }
        else if (wroteAField)
        {
            _enabledPresets.Add(number);
        }
        // CAPTURED 2026-08-16: writing BAUD 4800 at the SE type with no
        // interleave argument replaces the stored interleave with the
        // read-only spelling "uncoded" — which is why preset 0 round-trips.
        if (preset.Type == "SE" && preset.Baud == "4800") preset.Interleave = "uncoded";
        // The FSK types REFUSE interleave (VERIFIED 2026-08-16), so the field
        // simply does not exist on those rows.
        if (preset.Type is "FSKW" or "FSKN" or "FSK-A" or "FSK-V") preset.Interleave = null;
        return number;
    }

    // =====================================================================
    // Stored TX messages (round 11 §9A) — mode-free, like the AMD path
    // =====================================================================

    /// <summary>`TXMSG` lists, `TXMSG n &lt;text&gt;` stores, `TXMSG DEL n`
    /// deletes (SILENT on success — Stage 6 gate). Listed as a `TXMSG nn`
    /// header with the text on the NEXT line (protocol.md).
    /// <para><b>PROMPT SCOPE — <c>ALE&gt;</c>-ONLY, the radio-true shape
    /// (clone round 12 P2).</b> The TXMSG family answers <c>** ERROR **</c> at
    /// BOTH <c>SSB&gt;</c> and <c>HOP&gt;</c> (captured 2026-08-18). P1 kept a
    /// MARKED TEMPORARY infidelity — answering at <c>SSB&gt;</c> as well —
    /// because the clone campaign still issued its message leg there and P2's
    /// tests pinned that. This commit is the one that moves the leg to
    /// <c>ALE&gt;</c>, so the infidelity is RETIRED here, in the same change:
    /// the demo may be temporarily wrong where a real consumer depends on it,
    /// never wrong for free, and never wrong for longer than the consumer
    /// needs.</para>
    /// Caller holds _stateLock.</summary>
    private string[]? MessageStoreReply(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "TXMSG") return null;
        if (_prompt != AlePrompt) return [ErrorBanner];

        if (parts.Length == 1)
        {
            var lines = new List<string>();
            foreach (var slot in _txMessages.Keys.Order())
            {
                lines.Add("TXMSG " + Two(slot));
                lines.Add(_txMessages[slot]);
            }
            return [.. lines];
        }

        if (parts.Length == 3 && parts[1] == "DEL" && TryParseSlot(parts[2], out int deleteSlot))
        {
            _txMessages.Remove(deleteSlot);
            return NoPayload;                        // silent on success
        }

        if (parts.Length >= 3 && TryParseSlot(parts[1], out int storeSlot))
        {
            _txMessages[storeSlot] = string.Join(' ', parts[2..]);
            return NoPayload;                        // silent on success
        }
        return null;
    }

    private static bool TryParseSlot(string arg, out int slot)
        => int.TryParse(arg, out slot) && slot is >= 0 and <= 9;

    // =====================================================================
    // ALE fill (rule 4e) + the ALE settings
    // =====================================================================

    /// <summary>Rule 4e. ALE-domain: at any other prompt this returns null
    /// (rule 6) rather than fabricate the real radio's reject.
    /// <para>Honesty limits (replay doctrine, docs/tests.md): clean-prompt
    /// SUCCESS for SLFAD/INDAD/NETAD/ADDM/ADDC is PROVISIONAL (bench A7c); the
    /// demo emits no fill-gate trailer lines; a net whose associated self was
    /// deleted lists WITHOUT the ASSOC SELF segment (the captured SLFAD-shape,
    /// the least-invented option); DELAD of an unknown name and ADDM naming an
    /// unknown net have NO captured answer → rule 6.</para>
    /// Caller holds _stateLock.</summary>
    private string[]? AleFillReply(string command)
    {
        if (_prompt != AlePrompt) return null;

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        switch (parts[0])
        {
            // ---- Reads -------------------------------------------------
            case "SLFAD" when parts.Length == 1:
                return [.. _selfs.Select(e => ListingLine("SLFAD", e))];
            case "INDAD" when parts.Length == 1:
                return [.. _individuals.Select(e => ListingLine("INDAD", e))];
            case "NETAD" when parts.Length == 1:
                // BULK: records only — the captured listing hides members.
                return [.. _nets.Select(e => ListingLine("NETAD", e))];
            case "NETAD" when parts.Length == 2:
                return TargetedNetReply(parts[1]);
            case "EXCH" when parts.Length == 1:
                return ScheduleListing();
            case "CHG" when parts.Length == 2 && TryGroup(parts[1], out int readGroup):
                // An EMPTY group answers NOTHING at all (protocol.md) — the
                // captured silence, not an invented empty line.
                return _channelGroups.TryGetValue(readGroup, out var chans) && chans.Count > 0
                    ? [ChannelGroupLine(readGroup, chans)]
                    : NoPayload;

            // ---- Writes ------------------------------------------------
            case "SLFAD" when parts.Length == 3 && TryGroup(parts[2], out int selfGroup):
                if (KnownName(parts[1])) return [AddressExistsLine];
                _selfs.Add(new DemoEntry(parts[1], selfGroup, null));
                return NoPayload;

            case "INDAD" when parts.Length == 4 && TryGroup(parts[2], out int indGroup):
                return StoreLinkedEntry(_individuals, parts[1], indGroup, parts[3]);

            case "NETAD" when parts.Length == 4 && TryGroup(parts[2], out int netGroup):
                return StoreLinkedEntry(_nets, parts[1], netGroup, parts[3]);

            case "ADDM" when parts.Length == 3:
                // The member must EXIST; the net's own existence has no
                // captured answer, so an unknown net stays at rule 6.
                if (!KnownName(parts[2])) return [InvalidMemberLine];
                return AddMember(parts[1], parts[2]);

            case "DELAD" when parts.Length == 2:
                return DropEntry(parts[1]);

            case "ADDC" when parts.Length == 3 && TryGroup(parts[1], out int addGroup)
                             && TryChannel(parts[2], out int addChannel):
                var addTo = _channelGroups.TryGetValue(addGroup, out var existing) ? existing : [];
                _channelGroups[addGroup] = addTo;
                if (!addTo.Contains(addChannel)) addTo.Add(addChannel);   // duplicates silently ignored
                addTo.Sort();                                            // the radio sorts them
                return NoPayload;

            case "DELC" when parts.Length == 3 && TryGroup(parts[1], out int delGroup)
                             && TryChannel(parts[2], out int delChannel):
                if (_channelGroups.TryGetValue(delGroup, out var delFrom)) delFrom.Remove(delChannel);
                return NoPayload;

            case "EXCH" or "SOU" when parts.Length >= 3:
                return ScheduleWrite(parts[0] == "EXCH" ? "EXCHANGE" : "SOUND", parts);

            case "ERASE" when parts.Length == 1:
                // Addresses, MEMBERSHIP and SCHEDULES — channel groups, STORED
                // MESSAGES and settings survive (protocol.md hazard table).
                _selfs.Clear();
                _individuals.Clear();
                _nets.Clear();
                _members.Clear();
                _schedules.Clear();
                return NoPayload;

            default:
                return null;
        }
    }

    /// <summary>The nine ALE settings the SH block reports. No SET echo has
    /// ever been captured for any of them, so each mutates state and answers
    /// PROMPT-ONLY; the SH block is where the change becomes visible.
    /// Caller holds _stateLock.</summary>
    private string[]? AleSettingReply(string command)
    {
        if (_prompt != AlePrompt) return null;

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var value = parts[1];

        switch (parts[0])
        {
            case "ALL_C" when IsOnOff(value): _settings["ALL_CALL"] = value; return NoPayload;
            case "ANY_C" when IsOnOff(value): _settings["ANY_CALL"] = value; return NoPayload;
            case "AMD_D" when IsOnOff(value): _settings["AMD_DISPLAY"] = value; return NoPayload;
            case "KEY_T" when IsOnOff(value): _settings["KEY_TO_CALL"] = value; return NoPayload;
            case "LSTN" when IsOnOff(value): _settings["LSTN"] = value; return NoPayload;
            case "RAD_S" when IsOnOff(value): _settings["RAD_SIL"] = value; return NoPayload;
            case "MAXCH" when value.All(char.IsAsciiDigit):
                _settings["MAXCH"] = int.Parse(value, Inv).ToString("000", Inv).TrimStart('0') is { Length: > 0 } trimmed
                    ? trimmed : "0";
                return NoPayload;
            case "TIME_OU" when value.All(char.IsAsciiDigit):
                _settings["TIME_OUT"] = int.Parse(value, Inv).ToString("000", Inv);
                return NoPayload;
            case "TUNE" when value.All(char.IsAsciiDigit):
                _settings["TUNETIME"] = int.Parse(value, Inv).ToString("000", Inv);
                return NoPayload;
            default:
                return null;
        }
    }

    /// <summary>`EXCH|SOU STA &lt;addr&gt; [interval] [start]` and
    /// `EXCH|SOU STO &lt;addr&gt;`. Captured refusals: a repeat is
    /// ` ADR ALREADY QUED `, a full queue is ` LQA QUEUE FULL `; success is
    /// silent, and so is a STO for an address that is not queued (the
    /// ` INVALID ADDRESS ` line is the captured answer, and it IS emitted).
    /// Caller holds _stateLock.</summary>
    private string[] ScheduleWrite(string kind, string[] parts)
    {
        var verb = parts[1];
        var address = parts[2];
        if (verb == "STO")
        {
            int removed = _schedules.RemoveAll(s => s.Kind == kind && s.Address == address);
            return removed > 0 ? NoPayload : [" INVALID ADDRESS "];
        }
        if (verb != "STA") return NoPayload;
        if (_schedules.Any(s => s.Address == address)) return [AlreadyQueuedLine];
        if (_schedules.Count >= LqaQueueCapacity) return [LqaQueueFullLine];
        _schedules.Add((kind, address,
            parts.Length > 3 ? parts[3] : "00:00",
            parts.Length > 4 ? parts[4] : "00:00"));
        return NoPayload;
    }

    /// <summary>INDAD/NETAD share one shape: a globally unique name and an
    /// associated SELF that must already exist. Caller holds _stateLock.</summary>
    private string[] StoreLinkedEntry(List<DemoEntry> book, string name, int group, string associatedSelf)
    {
        if (KnownName(name)) return [AddressExistsLine];
        if (!_selfs.Any(e => e.Name == associatedSelf)) return [InvalidAssocSelfLine];
        book.Add(new DemoEntry(name, group, associatedSelf));
        return NoPayload;
    }

    /// <summary>The TARGETED net read (captured 2026-08-17): the record line,
    /// then this net's members as indented <c>MEMBER nn  addr</c> continuations —
    /// or the positive ` NO MEMBERS PRGMD ` marker when it has none. A net the
    /// book does not hold has no captured answer → rule 6.
    /// Caller holds _stateLock.</summary>
    private string[]? TargetedNetReply(string name)
    {
        var net = _nets.FirstOrDefault(e => e.Name == name);
        if (net is null) return null;

        var lines = new List<string> { ListingLine("NETAD", net) };
        var members = _members.TryGetValue(net.Name, out var list) ? list : [];
        if (members.Count == 0) lines.Add(NoMembersLine);
        else
            for (int i = 0; i < members.Count; i++)
                lines.Add($"     MEMBER {i + 1:00}  {members[i]}");
        return [.. lines];
    }

    /// <summary>ADDM. A repeat is REFUSED with the captured duplicate line and
    /// the list is unchanged; otherwise the member appends (insertion order).
    /// Caller holds _stateLock.</summary>
    private string[] AddMember(string netName, string member)
    {
        var members = _members.TryGetValue(netName, out var list) ? list : [];
        _members[netName] = members;
        if (members.Contains(member)) return [DuplicateMemberLine];
        members.Add(member);
        return NoPayload;
    }

    /// <summary>Bare EXCH — the schedule listing (identical to bare SOU). An
    /// empty queue answers the captured ` NO LQA SCHEDULED ` marker.
    /// Caller holds _stateLock.</summary>
    private string[] ScheduleListing()
        => _schedules.Count == 0
            ? [NoLqaScheduledLine]
            : [.. _schedules.Select(s => $"{s.Kind,-8} {s.Address,-15} INTERVAL {s.Interval} START TIME {s.Start}")];

    /// <summary>
    /// DELAD. Deleting a SELF is TWO-CASE (characterization campaign
    /// 2026-08-17 — this REPLACES the universal cascade the demo used to
    /// implement, which the campaign DISPROVED and which demoed behavior
    /// OPPOSITE to the shipped delete captions; sol-audit finding F3):
    /// <list type="bullet">
    /// <item>a SECONDARY self — its individuals and nets RE-POINT at the
    /// PRIMARY self (the first listing row); nothing else is destroyed;</item>
    /// <item>the PRIMARY self — its individuals ARE deleted and its nets keep
    /// their entry with the associated self gone.</item>
    /// </list>
    /// Deletion is also GLOBAL across MEMBERSHIP and takes the address's queued
    /// LQA schedule with it (captured 2026-08-17).
    /// Caller holds _stateLock.</summary>
    private string[]? DropEntry(string name)
    {
        int index = _selfs.FindIndex(e => e.Name == name);
        if (index >= 0)
        {
            bool isPrimary = index == 0;
            _selfs.RemoveAt(index);
            if (isPrimary)
            {
                foreach (var gone in _individuals.Where(e => e.AssociatedSelf == name).Select(e => e.Name).ToList())
                    DropFromMembershipAndSchedules(gone);
                _individuals.RemoveAll(e => e.AssociatedSelf == name);
                for (int i = 0; i < _nets.Count; i++)
                    if (_nets[i].AssociatedSelf == name)
                        _nets[i] = _nets[i] with { AssociatedSelf = null };
            }
            else
            {
                // The dependants survive and follow the PRIMARY — the first
                // remaining listing row (ASSUMED tier, plan §1: observed once,
                // unvaried).
                var primary = _selfs.Count > 0 ? _selfs[0].Name : null;
                for (int i = 0; i < _individuals.Count; i++)
                    if (_individuals[i].AssociatedSelf == name)
                        _individuals[i] = _individuals[i] with { AssociatedSelf = primary };
                for (int i = 0; i < _nets.Count; i++)
                    if (_nets[i].AssociatedSelf == name)
                        _nets[i] = _nets[i] with { AssociatedSelf = primary };
            }
            DropFromMembershipAndSchedules(name);
            return NoPayload;
        }
        if (_individuals.RemoveAll(e => e.Name == name) > 0)
        {
            DropFromMembershipAndSchedules(name);
            return NoPayload;
        }
        if (_nets.RemoveAll(e => e.Name == name) > 0)
        {
            _members.Remove(name);
            DropFromMembershipAndSchedules(name);
            return NoPayload;
        }
        return null;    // unknown name: no captured answer → rule 6
    }

    /// <summary>The GLOBAL half of DELAD: the address leaves EVERY net's
    /// member list (numbering compacts) and its queued schedule goes too.
    /// Caller holds _stateLock.</summary>
    private void DropFromMembershipAndSchedules(string name)
    {
        foreach (var list in _members.Values) list.Remove(name);
        _schedules.RemoveAll(s => s.Address == name);
    }

    /// <summary>Names are GLOBAL across selfs, individuals and nets — the
    /// radio refuses a reused one with " ADDRESS EXISTS ".
    /// Caller holds _stateLock.</summary>
    private bool KnownName(string name) =>
        _selfs.Any(e => e.Name == name)
        || _individuals.Any(e => e.Name == name)
        || _nets.Any(e => e.Name == name);

    private static bool TryGroup(string arg, out int group)
        => int.TryParse(arg, out group) && group is >= 0 and <= 9;

    private static bool TryChannel(string arg, out int channel)
        => int.TryParse(arg, out channel) && channel is >= 0 and <= 99;

    private static bool IsOnOff(string arg) => arg is "ON" or "OFF";

    private static bool IsSignedFourDigit(string arg)
    {
        if (arg.Length != 5 || (arg[0] != '+' && arg[0] != '-')) return false;
        for (int i = 1; i < 5; i++)
            if (!char.IsAsciiDigit(arg[i])) return false;
        return true;
    }

    private void ReadLoop(BlockingCollection<QueuedResponse> responses)
    {
        try
        {
            foreach (var response in responses.GetConsumingEnumerable())
            {
                int delay = ResponseDelayMs + response.ExtraDelayMs;
                if (delay > 0) Thread.Sleep(delay);
                DataReceived?.Invoke(this, new SerialDataEventArgs(response.Bytes));
            }
        }
        catch (ObjectDisposedException) { /* teardown race */ }
    }

    public ValueTask DisposeAsync()
    {
        CloseAsync().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }
}
