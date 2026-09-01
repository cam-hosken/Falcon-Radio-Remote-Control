using System.Globalization;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Cloning;

/// <summary>Where a clone campaign stands. Terminal: <see cref="Done"/>,
/// <see cref="Failed"/>.</summary>
public enum CloneState { Idle, Reading, Writing, Done, Failed }

/// <summary>
/// The radio-cloning orchestrator (plan round 11 §9A) — the
/// <see cref="BaudChangeFlow"/> precedent: a session-layer component that owns
/// the radio handle for its sentinels and mode switches, and sequences
/// EXISTING surface reads and writes for everything else. It adds no wire
/// vocabulary of its own beyond the three TXMSG builders §10 admits.
///
/// <para><b>Read campaign.</b> The operating mode/channel/net are captured
/// FIRST, so the campaign's own mode switching cannot contaminate them; then
/// each domain is read through its normal queue or gate, mode-switching per
/// leg. <b>Per-leg completion:</b> a domain with a sentinel-scoped read op is
/// completed by that op's own completion record; everything else is bracketed
/// by a trailing <c>BAT ST</c>. A leg whose sentinel never answers is FAULTED
/// — marked in the file and named in the summary — never silently empty.</para>
///
/// <para><b>Write campaign.</b> ONE confirmation (which covers the embedded
/// ERASE — the GUI owns confirmation, and a second popup for a step the first
/// one already described would be a lie about scope), then the §9A leg table
/// in order, then a FULL VERIFY that re-runs the read campaign and compares
/// against the TRANSFORMED file under <see cref="CloneCompare"/>'s rules.</para>
///
/// <para><b>Outcome accounting</b> (§9A, narrower than "every refusal"): the
/// summary reports every transform DROP, every ALE-gate-attributed REFUSAL,
/// every per-leg FAULT, every unwritable stored VALUE, and every VERIFY DIFF.
/// Non-ALE refusal lines are NOT individually attributed — no seam exists —
/// so the verify diff is their detector and the Console is their evidence.
/// A session drop aborts cleanly at the current leg and the summary names
/// where it stopped; resume = re-run, because every leg converges (keyed
/// writes, the clear-first HOP replay, the group reconcile).</para>
/// </summary>
public sealed class CloneService
{
    // ---- The ONE confirmation (§9A leg 1; round-10 lifecycle contract) -----

    public const string ConfirmTitle = "Write clone to radio?";

    /// <summary>
    /// The ONE confirmation's message (owner ruling R1: the campaign is
    /// UNCONDITIONALLY zeroize-first, so the question has to say so). It names
    /// the ZEROIZE by what it does rather than by its command name (R13), and
    /// it names the front-panel lockouts, because those are the one domain an
    /// operator would not expect a "clone" to move.
    /// <para>ROUND 15 E-4: WORDING ONLY. The unified vocabulary asks every
    /// confirmation's FIRST sentence to begin "The radio " and say what the
    /// radio does, so the wipe-and-rewrite leads and the inventory follows it.
    /// Nothing about the campaign changed.</para>
    /// </summary>
    public const string ConfirmMessage =
        "The radio will be zeroized, and this cannot be undone.";

    public const string ConfirmAccept = "Write";
    public const string ConfirmCancel = "Cancel";

    /// <summary>The preflight refusal when the transform leaves no self at
    /// all — the operator can fix it by choosing an identity, so it is a
    /// REFUSAL with an instruction, not a drop.</summary>
    public const string NoSelfRejection =
        "The file has no self — choose an identity for this radio before writing.";

    /// <summary>
    /// The ONE confirmation's message, with what the identity table is about to
    /// do to the book beside it (plan-clone-field-round2 §3.2, I-6): every ROLE
    /// CHANGE and every DROP. A clone that renames this radio and loses two
    /// nets says so in the question, not only in the summary afterwards.
    ///
    /// <para><b>ORDER: the BASE SENTENCE FIRST</b> (round 15 E-4, manager
    /// ruling, audit round 1). The structured lines used to be PREPENDED, so
    /// the rendered question opened "KC1HAS is now a self in ALPHA's place…"
    /// — a name, with no statement of what the radio is about to do — and the
    /// vocabulary rule ("the FIRST sentence begins 'The radio ' and says what
    /// the radio does") was satisfied only by the constant, never by the
    /// message anyone actually read. The base leads now and the lines follow
    /// it, in their existing order and wording: the operator learns the ACT
    /// first and its consequences for the book second, which is the order the
    /// delete prompts already used.</para>
    /// </summary>
    public static string ConfirmMessageFor(IReadOnlyList<string> roleChanges, IReadOnlyList<string> drops)
    {
        var lines = roleChanges.Concat(drops).ToList();
        return lines.Count == 0
            ? ConfirmMessage
            : ConfirmMessage + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly IConfirmationPrompt _prompt;
    private readonly CloneSurfaces _s;
    private readonly HopSurface _hop;
    private readonly ChannelSurface _channel;
    private readonly ModeSurface _mode;

    /// <summary>THE WIRE LEASE (plan-clone-write-structural.md §5.2, D1). The
    /// campaign takes it for the whole of its post-start body — closing restore
    /// included — and the §4 producers fall silent for exactly that span. The
    /// campaign's OWN surface calls are untouched by the lease (I-10): only
    /// producers consult the signal.</summary>
    private readonly CampaignWireCoordinator _wire;

    private readonly List<string> _summary = [];

    /// <summary>Domains that have already written their OWN reason for being
    /// incomplete, so the read's generic close-out loop must not add its
    /// "the radio stopped answering" sentence on top of a truer one (round 16
    /// fixes S4).</summary>
    private readonly HashSet<string> _explainedDomains = new(StringComparer.Ordinal);

    /// <summary>
    /// How many of <see cref="_summary"/>'s lines are NOTICES rather than
    /// things that went wrong.
    ///
    /// <para><b>Why the distinction exists</b> (owner ruling R3). Every other
    /// summary line reports something the campaign could not do: a dropped row,
    /// a refusal, a leg fault, an unwritable value, a verify diff. The
    /// MARK/SPACE omission is none of those — it is a RADIO limitation that
    /// fires on almost every preset of almost every radio (the tones are stored
    /// on every FSK type and LISTED only at <c>fsk-v</c>), and R3 forbids the
    /// read campaign the type flip that would reveal them. It MUST be reported,
    /// and it must not make an otherwise-perfect clone read as failed.</para>
    ///
    /// <para>The operator sees notices exactly like every other line — one
    /// list, one place — and the OUTCOME simply does not count them.</para>
    /// </summary>
    private int _notices;

    public CloneService(
        Prc138Radio radio, RadioSession session, IConfirmationPrompt prompt,
        SsbSurface ssb, PowerSurface power, DeviceSurface device, AleSurface ale,
        HopSurface hop, ChannelSurface channel, ModemSurface modem, ModeSurface mode,
        CampaignWireCoordinator wire)
    {
        _radio = radio;
        _session = session;
        _prompt = prompt;
        _s = new CloneSurfaces(ssb, power, device, ale, modem);
        _hop = hop;
        _channel = channel;
        _mode = mode;
        _wire = wire;
    }

    /// <summary>How long a leg's closing sentinel is given. Test hook.</summary>
    public int SentinelTimeoutMs { get; set; } = 10_000;

    /// <summary>How long a domain's OWN sentinel-scoped operation is given to
    /// publish its completion record. Longer than a bare sentinel because Core
    /// runs its own timeout underneath (<c>AleController.RefreshTimeoutMs</c>)
    /// and this waits for THAT verdict, not for a second one. Test hook.</summary>
    public int ReadCompletionTimeoutMs { get; set; } = 30_000;

    /// <summary>Poll interval while waiting on a completion record. Test hook.</summary>
    public int ReadPollMs { get; set; } = 5;

    /// <summary>How long one gated ALE programming operation is given before
    /// the campaign stops waiting for an outcome the gate will never deliver
    /// (it delivers none at all when the session drops). Test hook.</summary>
    public int GateTimeoutMs { get; set; } = 30_000;

    /// <summary>How long the campaign waits for the ZEROIZE settle observable.
    /// DELIBERATELY LONGER than Core's own settle bound, so the verdict is
    /// always Core's fault report rather than a second competing timeout — the
    /// only measured same-session settle was 9.4 s over eight polls, and the
    /// silence has been seen to vary. Test hook.</summary>
    public int ZeroizeSettleTimeoutMs { get; set; } = 45_000;

    /// <summary>How long the FinalsOrder row waits for Core's FM-squelch cycle
    /// to complete after the SSB-context modulation read (plan §3 leg 6: five
    /// seconds, else SKIP and report). Test hook.</summary>
    public int AnalogSquelchSettleMs { get; set; } = 5_000;

    /// <summary>How often the channel leg re-reads the mirror while it waits
    /// for the <c>DI 0 99</c> dump to finish (F6). Test hook.</summary>
    public int ChannelDumpPollMs { get; set; } = 250;

    /// <summary>The channel dump's QUIET WINDOW: how long the reported set may
    /// stay the size it is before the leg stops waiting for more rows. The
    /// captured pace is ~72 ms a row (r15-p1, `CH 00` t19887 → `CH 99`
    /// t27344), so four seconds is ~55 rows of slack — long enough that a
    /// merely slow radio is not cut off, short enough that a dump the radio
    /// abandoned does not hold the campaign. Test hook.</summary>
    public int ChannelDumpQuietMs { get; set; } = 4_000;

    /// <summary>The channel dump's HARD CAP, whatever the rows are doing. The
    /// whole captured dump took 7.5 s; a minute is eight of those. Test
    /// hook.</summary>
    public int ChannelDumpTimeoutMs { get; set; } = 60_000;

    public CloneState State { get; private set; } = CloneState.Idle;
    public string StatusText { get; private set; } = "";
    public bool IsRunning => State is CloneState.Reading or CloneState.Writing;

    /// <summary>The file in hand: what the last read campaign produced, or
    /// what was loaded from disk. Null until one of those happens.</summary>
    public CloneFile? File { get; private set; }

    /// <summary>The last campaign's outcome accounting, in report order.</summary>
    public IReadOnlyList<string> Summary => [.. _summary];

    public event EventHandler? Changed;

    // ---- Gating (§9A) ------------------------------------------------------

    /// <summary>Why Read is unavailable, or null when it is available. Read
    /// needs connected + Ready; the scan/call level is the caller's standing
    /// two-level policy.</summary>
    public string? ReadBlockedReason =>
        _session.Phase != SessionPhase.Ready ? "Not connected." : null;

    /// <summary>Why Write is unavailable, or null when it is available. Write
    /// needs connected + Ready + a file whose every manifest domain was READ —
    /// a transient read fault must never become destructive loss.</summary>
    public string? WriteBlockedReason
    {
        get
        {
            if (_session.Phase != SessionPhase.Ready) return "Not connected.";
            if (File is null) return "No clone file loaded.";
            var missing = File.IncompleteDomains;
            return missing.Count == 0
                ? null
                : "The file is incomplete — " + string.Join(", ", missing) + " were not read.";
        }
    }

    /// <summary>Client-side identity validation (R9): alphanumeric, 1-15.</summary>
    public static bool IsValidIdentity(string? identity)
    {
        var name = (identity ?? "").Trim();
        return name.Length is > 0 and <= 15 && name.All(char.IsAsciiLetterOrDigit);
    }

    // ---- File in / out -----------------------------------------------------

    /// <summary>Load and VALIDATE a file. A rejection throws
    /// <see cref="CloneFileFormatException"/> naming the offender, and the
    /// service keeps whatever file it already had.</summary>
    public void LoadJson(string json)
    {
        File = CloneFile.Load(json);
        _summary.Clear();
        _notices = 0;
        // ROUND 17 F6: whatever the LOAD had to say rides into the summary the
        // operator already reads, and into the status line — a downgrade the
        // operator only met later, as a greyed-out Write button, would be a
        // silent one. The clear above is what these are added AFTER, so a
        // previous campaign's lines cannot be mistaken for this file's.
        foreach (var notice in File.LoadNotices) _summary.Add(notice);
        var loaded = $"Loaded a clone file captured {File.CapturedUtc ?? "at an unrecorded time"}.";
        Set(CloneState.Idle, File.LoadNotices.Count == 0
            ? loaded
            : loaded + " " + string.Join(" ", File.LoadNotices));
    }

    public string SaveJson() => (File ?? throw new InvalidOperationException("No clone file to save.")).Save();

    /// <summary>Adopt a file directly (the read campaign's own result, and the
    /// seam the fixtures drive).</summary>
    public void Adopt(CloneFile file)
    {
        File = file;
        Set(State, StatusText);
    }

    // ======================= READ CAMPAIGN =================================

    public async Task<bool> ReadAsync()
    {
        if (IsRunning) return false;
        _summary.Clear();
        _explainedDomains.Clear();
        _notices = 0;
        if (ReadBlockedReason is { } blocked)
        {
            Set(CloneState.Failed, blocked);
            return false;
        }

        Set(CloneState.Reading, "Reading the radio…");
        // THE SCAN CONTEXT (§5.4c) — created here and nowhere else for this
        // campaign. `StartPending` asks for the campaign-start sequence
        // (discovery sentinel → snapshot → `ST` → stop sentinel) that a READ
        // owes and the write's nested verify must never re-run.
        _scan = new ScanContext { StartPending = true };
        bool completed;
        CloneFile file;
        // THE WIRE LEASE (§5.2), OUTERMOST. Every producer falls silent from
        // here until this using-block exits — the closing restore inside
        // RunReadCampaignAsync's own finally included, which is exactly the lap
        // a State-keyed signal would have let them wake up during.
        using (_wire.Enter())
        {
            // D20: THE CAMPAIGN STARTS WITH A CLEAN SENTINEL LEDGER. Inside the
            // lease and before the campaign's first own sentinel — the
            // discovery/start sequence below is the first thing that pings — so
            // no attempt can INHERIT a previous one's standing debt. See
            // Prc138Radio.ResetSentinelLedger for the arithmetic; a ping still in
            // flight here completes normally.
            _radio.ResetSentinelLedger();
            // THE STANDALONE READ OWNS ITS RESTORE (plan-clone-field-round2.md
            // F1, owner decision D1'): it is the campaign that MOVED the
            // operator's radio, and the values it puts back are the ones it
            // captured. The write's verify read runs with `restore: false` —
            // see WriteAsync.
            (file, completed) = await RunReadCampaignAsync(restore: true).ConfigureAwait(true);
        }

        // The PARTIAL file is kept even when the campaign stopped early. It can
        // never be written — its unread domains fail the preflight — and it is
        // what tells the operator which legs did get through. Throwing it away
        // would only hide that.
        File = file;
        if (!completed)
        {
            Set(CloneState.Failed, StatusText);
            return false;
        }

        var faulted = file.IncompleteDomains;
        foreach (var domain in faulted)
        {
            // A domain that has ALREADY said why it is incomplete keeps its own
            // sentence: this loop's generic one ("the radio stopped answering")
            // would be FALSE beside it — S4's short dump is a domain whose every
            // sentinel WAS answered.
            if (_explainedDomains.Contains(domain)) continue;
            _summary.Add($"{domain}: the radio stopped answering, so this domain is incomplete.");
        }

        // D15: THE STORED INVENTORY, LAST — after every fault line, so the
        // report reads "here is what went wrong" and then "here is what is in
        // the file". Plain summary lines, not notices: see StoredInventory.
        foreach (var row in StoredInventory(file)) _summary.Add(row);

        Set(faulted.Count == 0 ? CloneState.Done : CloneState.Failed,
            faulted.Count == 0
                // D9 CATEGORY B (owner ruling 2026-08-29): the STATUS LINE
                // CARRIES THE VERDICT ONLY. The gap domains are not lost by
                // dropping them from here — the close-out loop just above
                // names every faulted domain in its own summary line, and the
                // one domain it skips (`_explainedDomains`) named itself in a
                // truer sentence earlier.
                ? "Read complete."
                : "Read incomplete.");
        return faulted.Count == 0;
    }

    /// <summary>
    /// D15 (plan-clone-write-structural §2, owner 2026-08-30) — WHAT WAS
    /// STORED, one line per domain, in the order the owner named them: "instead
    /// of that message, give a line by line of what WAS stored. x channels, x
    /// chan groups, x nets etc, basically the info from the status line that's
    /// shown when the file is loaded into the app by the open file button."
    ///
    /// <para><b>The vocabulary is <see cref="CloneViewModel"/>'s FileLine
    /// idiom</b> — <c>"{n} self(s)"</c>, <c>"{n} channel(s)"</c> — because that
    /// is the status line the owner is pointing at, and the two surfaces have to
    /// read as ONE vocabulary. Counts only, no prose tail (D9's register).</para>
    ///
    /// <list type="bullet">
    /// <item><b>Zero is OMITTED.</b> The line answers "what WAS stored", and a
    /// row saying nothing was is not an answer to that.</item>
    /// <item><b>A FAULTED domain gets no row</b> — it is already named, in its
    /// own sentence, by the close-out loop above (or by the truer sentence it
    /// wrote for itself). A row of partial counts beside that fault line would
    /// invite the operator to read a broken domain as a stored one. This is not
    /// hypothetical: the address book keeps its rows when the headless-book
    /// preflight faults it, and the settings/lockout legs fill as far as they
    /// get.</item>
    /// <item><b>The counts come from the FILE</b> this campaign just built —
    /// the same object that was handed to <see cref="File"/> — never from a
    /// surface mirror, which is upsert-only and can outlive the read.</item>
    /// <item><b>PLAIN summary lines, never <see cref="Notice"/>s.</b> The
    /// notice count exists for exactly one arithmetic — WriteAsync's
    /// <c>problems = _summary.Count - _notices</c> — and these lines are added
    /// HERE, in the standalone read's close-out, which that arithmetic never
    /// reaches: the write's verify runs <see cref="RunReadCampaignAsync"/>
    /// directly and never enters <see cref="ReadAsync"/>. So the inventory can
    /// neither become a write warning nor be counted as a write problem, and
    /// the read's own verdict is the faulted-domain count, which no summary
    /// line moves.</item>
    /// </list>
    /// </summary>
    private static List<string> StoredInventory(CloneFile file)
    {
        List<string> rows = [];
        void Row(CloneDomainState state, int count, string noun)
        {
            if (state == CloneDomainState.Read && count > 0) rows.Add($"{count} {noun}");
        }

        Row(file.ChannelState, file.Channels.Count, "channel(s)");
        Row(file.GroupState, file.ChannelGroups.Count, "channel group(s)");
        Row(file.BookState, file.Selfs.Count, "self(s)");
        Row(file.BookState, file.Individuals.Count, "individual(s)");
        Row(file.BookState, file.Nets.Count, "net(s)");
        Row(file.MessageState, file.Messages.Count, "message(s)");
        Row(file.ScheduleState, file.Schedules.Count, "schedule(s)");
        Row(file.HopNetState, file.HopNets.Count, "HOP net(s)");
        Row(file.ExcludeState, file.ExcludeBands.Count, "exclusion band(s)");
        Row(file.ModemState, file.ModemPresets.Count, "modem preset(s)");
        Row(file.SettingState, file.Settings.Count, "setting(s)");
        Row(file.Lockouts?.State ?? CloneDomainState.Unread,
            file.Lockouts?.Rows.Count ?? 0, "lockout(s)");
        return rows;
    }

    /// <summary>
    /// The campaign proper, with the CLOSING RESTORE'S ONE FUNNEL around it.
    ///
    /// <para><b>Why a try/finally and not a line at the end of the legs</b>
    /// (plan-clone-field-round2.md §3.4, critic pass 2). The leg body has many
    /// direct early returns — a faulted mode gate, a session drop, a mode the
    /// radio never reported. Every one of them is a moment at which the campaign
    /// has ALREADY moved the operator's radio, and a restore written as the last
    /// statement of the body would be skipped by all of them. So it runs from
    /// exactly one place, on every exit path, guarded only by "is the radio still
    /// reachable" — I-2: restore-last, exactly once per campaign.</para>
    ///
    /// <para>The restore's own verdict does NOT change <c>Completed</c>: the file
    /// the legs produced is as complete as it ever was, and the restore's failure
    /// is reported in the summary (§3.4, "effect on completion"). The WRITE's
    /// verify passes <c>restore: false</c> and performs its own restore
    /// afterwards, from the FILE's values — see <see cref="WriteAsync"/>.</para>
    /// </summary>
    private async Task<(CloneFile File, bool Completed)> RunReadCampaignAsync(bool restore)
    {
        var file = new CloneFile
        {
            CapturedUtc = DateTime.UtcNow.ToString("O", Inv),
        };
        OperatingMode? startMode = null;
        try
        {
            return (file, await RunReadLegsAsync(file, m => startMode = m).ConfigureAwait(true));
        }
        finally
        {
            if (restore)
                await RunClosingRestoreAsync(
                    startMode, file.OperatingChannel, file.OperatingHopNet,
                    announceRestore: true).ConfigureAwait(true);
        }
    }

    /// <summary>The read legs. Returns false only when the campaign stopped
    /// early — a domain the radio simply stopped answering comes back with its
    /// FAULTED marker set instead. <paramref name="noteStartMode"/> hands the
    /// captured operating mode to the restore funnel above the moment it is
    /// known, so an early return below can never lose it.</summary>
    private async Task<bool> RunReadLegsAsync(CloneFile file, Action<OperatingMode> noteStartMode)
    {
        // ---- Leg 0: the operating MODE, FIRST -----------------------------
        // Taken from the mirror BEFORE the campaign switches anything, so the
        // file records where the OPERATOR was, not where the campaign went.
        // The channel and the net are captured at their own prompts below —
        // an SH only carries CHAN in SSB/ALE and NET only in HOP, and a mode
        // switch changes neither of them (a NET SELECT would, and the read
        // campaign never selects one).
        if (!await AtPromptAsync(null, "operating state").ConfigureAwait(true)) return false;
        // D8's CAMPAIGN-START SEQUENCE, ahead of the SH below and of everything
        // else: a read campaign that finds the radio scanning in ALE must stop
        // the scan BEFORE it captures the operating channel, or it captures a
        // dwell. No-op for the write campaign and for its verify.
        if (!await RunCampaignStartScanSequenceAsync("operating state").ConfigureAwait(true)) return false;
        // One SH at WHATEVER prompt the radio is at — a read, not a switch. It
        // refreshes the mode from the radio's own prompt rather than trusting a
        // mirror that could be stale (the front panel can move the radio, and
        // then everything downstream would be captured against the wrong mode).
        _s.Ssb.RequestStatus();
        bool modeOk = await SentinelAsync("operating state").ConfigureAwait(true);
        var startingMode = _mode.Mode;
        if (!startingMode.IsConfirmed)
        {
            _summary.Add("operating state: the radio has not reported a mode this session.");
            return false;
        }
        file.OperatingMode = startingMode.Value.ToString();
        // Handed to the restore funnel HERE, not returned at the end: every
        // early return below happens with the radio already moved.
        noteStartMode(startingMode.Value);

        // ---- SSB-prompt legs ----------------------------------------------
        if (!await AtPromptAsync(OperatingMode.Ssb, "SSB settings").ConfigureAwait(true)) return false;
        bool ssbSettingsOk = await ReadSettingsLegAsync(file, "SSB>").ConfigureAwait(true);
        // The SSB SH block the leg above just read is what carries CHAN.
        file.OperatingChannel = _channel.Current.IsConfirmed ? _channel.Current.Value : null;

        Status("Reading the stored channels…");
        _channel.ForgetReportedChannels();
        _channel.RequestDump();
        bool channelsAnswered = await SentinelAsync("SSB channels").ConfigureAwait(true);
        // ROUND 17 F6 — THE LEG'S OWN BARRIER, because the SENTINEL IS NOT ONE
        // for a heavy dump. Captured on the wire
        // (bench/transcripts/r15-p1-wire-read-20260822-194203.jsonl): `DI 0 99`
        // written at t19697 draws a BARE `SSB>` 14 ms later — BEFORE any row —
        // which releases the prompt gate, so the sentinel's `BAT ST` goes out
        // while the dump is still streaming (~72 ms a row). After row 28
        // (`CH 27`, t21915) the radio INTERLEAVES `Battery Status FULL 26.2V`
        // (t21947) — the sentinel answers MID-DUMP — and the dump then RESUMES
        // and completes: `CH 99` at t27344 and the `CHAN 25` trailer at t27360,
        // 100 rows in all. The S4 check below therefore ran against 28 slots and
        // faulted a domain the radio had answered in full; the operator's file
        // carried no channels at all. Deterministic (~2.2 s in ⇒ row 28,
        // reproduced 8-22 and 8-24).
        //
        // So the leg WAITS FOR THE DUMP ITSELF before judging it. The sentinel
        // is KEPT and unchanged — it is the is-alive check, and a radio that
        // stopped answering is still faulted by it. Nothing about the transport,
        // the parser or the sentinel/queue semantics moves (round 15's rules
        // stand); this is one wait, on the campaign's own context, between the
        // two.
        if (channelsAnswered) await AwaitChannelDumpAsync().ConfigureAwait(true);
        // ROUND 16 FIXES S4. The sentinel says the radio is STILL ANSWERING; it
        // does NOT say the dump was whole. `DI 0 99` prints every slot — a
        // never-written one prints a default row (protocol.md; P17 record 6
        // carries exactly 100 `CH nn RxFr` rows) — so the REPORTED SET is the
        // proof. A dump that lost rows under load (P17: heavy answers lose rows
        // at every fixed pace ≤ 750 ms) must not be serialised as `Read` and
        // later written into a radio.
        var reported = _channel.Channels.Select(c => c.Number).ToHashSet();
        bool channelsWhole = reported.SetEquals(Enumerable.Range(0, 100));
        if (channelsAnswered && !channelsWhole)
        {
            _summary.Add($"SSB channels: the radio reported {reported.Count} of 100 slots, so this domain is incomplete.");
            _explainedDomains.Add("SSB channels");
        }
        file.ChannelState = Mark(channelsAnswered && channelsWhole);
        if (file.ChannelState == CloneDomainState.Read)
        {
            // ---- D4: DEFAULT-CHANNEL ELISION, ON THE STORAGE SIDE ONLY -----
            // S4's whole-set judgment above is UNTOUCHED (invariant I-4): the
            // domain is still judged on all 100 reported slots, and a dump that
            // lost rows is still faulted. What changes is what gets STORED. A
            // slot nobody programmed prints the factory row (Wire.DefaultChannel)
            // and a ZEROIZE puts every slot back to it, so storing those rows
            // buys nothing and costs the write ~90 on-channel sequences per
            // clone on these radios. The marker says the file is sparse ON
            // PURPOSE, which is what keeps the 100-row completeness rule
            // meaningful for every file that predates it (D6).
            var reportedRows = _channel.Channels.Select(c => new CloneChannel
            {
                Number = c.Number, RxFrequency = c.RxFrequency, TxFrequency = c.TxFrequency,
                Mode = c.Mode, Agc = c.Agc, Bandwidth = c.Bandwidth, RxOnly = c.RxOnly,
            }).OrderBy(c => c.Number).ToList();
            var stored = reportedRows.Where(c => !c.IsFactoryDefault()).ToList();
            file.DefaultChannelsElided = true;
            file.Channels = stored;
            // D15 (2026-08-30, owner): the leg SAYS NOTHING about the elision.
            // It used to end with a notice counting the default rows it had
            // dropped — a report of an ABSENCE — and the owner asked for the
            // opposite: "a line by line of what WAS stored". That inventory is
            // built once, at the READ CLOSE-OUT (see StoredInventory), from the
            // finished file, and its `{n} channel(s)` row conveys the dropped
            // count implicitly. The elision BEHAVIOUR above (D4/D6) is
            // untouched; only the line went.
        }

        Status("Reading the modem presets…");
        bool ssbModemOk = await ReadModemPresetLegAsync(file, OperatingMode.Ssb).ConfigureAwait(true);
        // Truthful on every early return between here and the HOP leg: the SSB
        // half is what has been read so far. The HOP leg re-marks it.
        file.ModemState = Mark(ssbModemOk);

        // ---- The operator lockouts (clone round 12, R2) --------------------
        // ONE sentinel-bracketed read op for the WHOLE table: bare PROGRAM and
        // bare SELECT are GLOBAL state reports, answered from whichever prompt
        // the radio happens to be at (captured), so this leg needs no prompt of
        // its own and takes the one it finds.
        Status("Reading the operator lockouts…");
        file.Lockouts = await ReadLockoutsAsync().ConfigureAwait(true);

        // ---- ALE-prompt legs ----------------------------------------------
        if (!await AtPromptAsync(OperatingMode.Ale, "address book").ConfigureAwait(true)) return false;
        bool aleSettingsOk = await ReadSettingsLegAsync(file, "ALE>").ConfigureAwait(true);
        file.SettingState = Mark(ssbSettingsOk && aleSettingsOk);

        // The stored messages read at the ALE prompt, for the same captured
        // reason the WRITE leg moved there: the TXMSG family is ALE-only and
        // answers `** ERROR **` at SSB> and HOP>. `ForgetStoredMessages` runs
        // FIRST every time because the mirror is UPSERT-ONLY — without it a
        // slot deleted since the last read would linger as a phantom row and
        // the verify would report a diff that is not on the radio.
        Status("Reading the stored messages…");
        _s.Ale.ForgetReportedMessages();
        _s.Ale.RequestStoredMessages();
        file.MessageState = Mark(await SentinelAsync("stored messages").ConfigureAwait(true));
        if (file.MessageState == CloneDomainState.Read)
            file.Messages = [.. _s.Ale.StoredMessages
                .Where(m => m.Text.Length > 0)
                .Select(m => new CloneTxMessage { Slot = m.Slot, Text = m.Text })
                .OrderBy(m => m.Slot)];

        Status("Reading the address book…");
        bool bookOk = await AwaitReadAsync(
            _s.Ale.RequestStationBook(), () => _s.Ale.LastBookRead, "address book").ConfigureAwait(true);
        if (bookOk)
        {
            file.Selfs = [.. _s.Ale.SelfAddresses.Select(ToAddress)];
            file.Individuals = [.. _s.Ale.IndividualAddresses.Select(ToAddress)];
            file.Nets = [.. _s.Ale.NetAddresses.Select(a => new CloneNet
            {
                Name = a.Address, Group = a.ChannelGroup, AssociatedSelf = a.AssociatedSelf,
            })];
            foreach (var net in file.Nets)
            {
                Status($"Reading net {net.Name}'s members…");
                if (!await AwaitReadAsync(
                        _s.Ale.RequestNetMembers(net.Name), () => _s.Ale.LastMemberRead,
                        "address book").ConfigureAwait(true))
                { bookOk = false; break; }
                if (_s.Ale.NetMembers.TryGetValue(net.Name, out var members))
                    net.Members = [.. members.Select(m => m.Address)];
                else bookOk = false;
            }
        }
        file.BookState = Mark(bookOk);
        NoteFillGateRefusal(file);

        Status("Reading the channel groups…");
        bool groupsOk = await AwaitReadAsync(
            _s.Ale.RequestAllChannelGroups(), () => _s.Ale.LastGroupRead, "channel groups").ConfigureAwait(true);
        var groups = _s.Ale.ChannelGroups;
        file.GroupState = Mark(groupsOk && groups.All(g => g.Channels is not null));
        if (file.GroupState == CloneDomainState.Read)
            file.ChannelGroups = [.. groups.Select(g => new CloneChannelGroup
            {
                Group = g.Group, Channels = [.. g.Channels!],
            })];

        Status("Reading the LQA schedules…");
        bool schedulesOk = await AwaitReadAsync(
            _s.Ale.RequestLqaSchedules(), () => _s.Ale.LastScheduleRead, "LQA schedules").ConfigureAwait(true);
        var schedules = _s.Ale.LqaSchedules;
        file.ScheduleState = Mark(schedulesOk && schedules is not null);
        if (file.ScheduleState == CloneDomainState.Read)
            file.Schedules = [.. schedules!.Select(s => new CloneSchedule
            {
                Kind = s.Kind == LqaScheduleKind.Exchange ? "EXCHANGE" : "SOUND",
                Address = s.Address, Interval = s.Interval, Start = s.StartTime,
            })];

        // ---- The HEADLESS-BOOK PREFLIGHT (plan §3) -------------------------
        // Runs HERE because it needs the schedules too. Deleting the PRIMARY
        // self ORPHANS its individuals: they vanish from bulk AND targeted
        // reads while still being NAMED in member lines, and they reappear
        // re-pointed the moment any new self is created. So a book that
        // reports NO self while other rows still reference one is SILENTLY
        // INCOMPLETE — the read cannot see what it is missing. It is FAULTED
        // rather than serialized as complete, and a faulted domain can never
        // be written. (A genuinely empty radio — no selfs, no individuals, no
        // members, no schedules — is not headless; it is blank, which is
        // exactly what a post-ZERO read finds.)
        if (file.BookState == CloneDomainState.Read && IsHeadlessBook(file))
        {
            file.BookState = CloneDomainState.Faulted;
            _summary.Add("Address book: the radio lists no self address but other rows still name one, "
                + "so some addresses cannot be read at all — this domain is incomplete.");
        }

        // ---- HOP-prompt legs ----------------------------------------------
        if (!await AtPromptAsync(OperatingMode.Hop, "HOP nets").ConfigureAwait(true)) return false;
        Status("Reading the HOP nets…");
        _hop.RequestStatus();
        bool hopStatusOk = await SentinelAsync("operating state").ConfigureAwait(true);
        file.OperatingHopNet = _hop.CurrentNet.IsConfirmed ? _hop.CurrentNet.Value : null;
        file.OperatingState = Mark(modeOk && ssbSettingsOk && hopStatusOk
            && file.OperatingMode is not null && file.OperatingChannel is not null
            && file.OperatingHopNet is not null);

        _hop.RequestAllNets();
        bool hopOk = await SentinelAsync("HOP nets").ConfigureAwait(true);
        if (hopOk)
        {
            var nets = new List<CloneHopNet>();
            for (int n = 0; n <= 9; n++)
            {
                if (!_hop.Nets.TryGetValue(n, out var net)) { hopOk = false; break; }
                var row = new CloneHopNet
                {
                    Number = n,
                    Wiped = net.IsReportedUnprogrammed,
                    NetId = net.IsReportedUnprogrammed ? null : net.NetId,
                    // The WIRE token (NB/WB/LIST), not the enum's own name:
                    // the file's value has to be one the write can send back.
                    Type = net.Type is { } hopType ? hopType.ToWire() : null,
                    // TYPE-SCOPED, deliberately: the HOP mirror upserts a net
                    // from whichever value line arrived, so a net retyped from
                    // NB to WB keeps its old CENTRE in the mirror forever (no
                    // line ever un-says it). A WB net HAS no centre, so the
                    // file carries only the values its type calls for.
                    CenterKHz = net.Type == HopType.Narrowband ? net.CenterKHz : null,
                    LowKHz = net.Type == HopType.Wideband ? net.WidebandLowKHz : null,
                    HighKHz = net.Type == HopType.Wideband ? net.WidebandHighKHz : null,
                };
                nets.Add(row);
            }
            foreach (var row in nets.Where(r => r.Type == "LIST" && !r.Wiped))
            {
                Status($"Reading HOP net {row.Number}'s frequencies…");
                _hop.RequestHopList(row.Number);
                if (!await SentinelAsync("HOP nets").ConfigureAwait(true)) { hopOk = false; break; }
                if (_hop.HopLists.TryGetValue(row.Number, out var freqs))
                    row.ListFrequencies = [.. freqs];
            }
            if (hopOk) file.HopNets = nets;
        }
        file.HopNetState = Mark(hopOk);

        // ---- F9: THE HOP-SCOPED MODEM PRESETS (7-9) ------------------------
        // The modem book is PROMPT-SPLIT: 0-6 live at `SSB>` (read above) and
        // 7-9 live at `HOP>` and NOWHERE ELSE (P5, transcript
        // bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl —
        // `MODEM PRE 7` answers `INVALID MODEM PRESET` at `SSB>` and `ALE>`).
        // Until this round the campaign only ever asked at `SSB>`, so a clone
        // silently dropped three presets and the verify could not see the loss.
        // This leg is here rather than beside the SSB one for the only reason
        // that matters: it needs the `HOP>` prompt, and the campaign is already
        // standing at it.
        Status("Reading the HOP modem presets…");
        bool hopModemOk = await ReadModemPresetLegAsync(file, OperatingMode.Hop).ConfigureAwait(true);
        file.ModemState = Mark(ssbModemOk && hopModemOk);

        Status("Reading the exclusion bands…");
        bool excludeOk = await AwaitReadAsync(
            _hop.RequestExcludeBands(), () => _hop.LastExcludeRead, "exclusion bands").ConfigureAwait(true);
        var bands = _hop.ExcludeBands;
        file.ExcludeState = Mark(excludeOk && bands is not null);
        if (file.ExcludeState == CloneDomainState.Read)
            file.ExcludeBands = [.. bands!.Select(b => new CloneExcludeBand
            {
                Band = b.Band, LowKHz = b.LowKHz, HighKHz = b.HighKHz,
            }).OrderBy(b => b.Band)];

        // Leave the radio where the operator had it: the campaign's mode
        // switching is its own business, not a change to the radio's state.
        await AtPromptAsync(startingMode.Value, "operating state").ConfigureAwait(true);
        return true;
    }

    // ======================= THE CLOSING RESTORE ===========================

    /// <summary>
    /// Put the radio back on the operating state a campaign is responsible for
    /// (plan-clone-field-round2.md F1 / §3.4; owner decisions D1 and D1',
    /// amended by A-10 and A-11 after phase-1 audit round 1).
    ///
    /// <para><b>Why this exists.</b> The read campaign issues only queries and
    /// mode switches, yet the field read of 2026-08-21 left its SOURCE radio on
    /// the wrong channel. The lap itself did it: a NET select silently changes
    /// the SSB channel (probe R9b) and HOP entry regenerates on the CURRENT net,
    /// so a campaign that merely returns to the operator's MODE has still moved
    /// the operator's CHANNEL. Which act does it was left un-probed on purpose
    /// (P1 could not reproduce it on the bench radio's own state) — the fix is
    /// CAUSE-INDEPENDENT: whatever moved, this puts it back.</para>
    ///
    /// <para><b>THE ORDER DEPENDS ON THE FINAL MODE</b> (A-10). Mode goes last
    /// in both shapes, for the reason leg 11 writes it last — nothing after it
    /// may move anything — but WHICH steps precede it is not the same:</para>
    /// <list type="bullet">
    /// <item>final mode <b>SSB or ALE</b>: NET (at <c>HOP&gt;</c>) → CH (at
    /// <c>SSB&gt;</c>) → the mode. Net before channel because <c>NET n</c> moves
    /// the SSB channel, so the other order hands the net a chance to undo the
    /// channel.</item>
    /// <item>final mode <b>HOP</b>: CH (at <c>SSB&gt;</c>) FIRST → the mode
    /// switch to <c>HOP&gt;</c> → NET (already at the final prompt, and nothing
    /// follows it). Entering HOP RE-IMPOSES the current net's channel, so the
    /// old NET → CH → mode order put the channel back and then threw it away on
    /// the way to <c>HOP&gt;</c>; a CH before the entry is the best that can be
    /// done, and the CHANNEL READ-BACK IS SKIPPED because the radio owns that
    /// value now. The line says so rather than claiming a match.</item>
    /// </list>
    ///
    /// <para><b>The read-back is a FRESH REPORT, never the mirror's own restore
    /// value</b> (A-10, and the audit reproduced the alternative). The mirror is
    /// written by the steps' own echoes, so comparing it against what those
    /// steps ASKED FOR answered a question about the app, not about the radio: a
    /// radio that moved after the last echo still reported a clean match. So the
    /// helper closes with one <c>SH</c> at the FINAL prompt, behind its own
    /// sentinel, and compares what that re-confirmed.
    /// (<c>HopSurface.RequestStatus</c> and <c>SsbSurface.RequestStatus</c> are
    /// the SAME wire act — <c>Radio.Show()</c> — and the block the radio answers
    /// is the one its current prompt owns: <c>CHAN</c> at <c>SSB&gt;</c>/
    /// <c>ALE&gt;</c>, <c>NET</c> at <c>HOP&gt;</c>. One read, whichever shape
    /// ran; it is named through the surface that owns the answer so the intent
    /// reads correctly.)</para>
    ///
    /// <para><b>Nothing is restored that was not observed</b> (I-3): a null value
    /// sends NOTHING and is named in the one summary line. Each non-null step
    /// carries its OWN closing sentinel, because <see cref="AtPromptAsync"/>
    /// sends none at all when the radio is already at the prompt it wants — the
    /// bracket has to belong to the step, not to the navigation.</para>
    ///
    /// <para><b>Exactly one summary line</b> (§3.4), in PROSE (I-5, A-11 — the
    /// plan's own <c>CH 09</c> examples were the violation and are struck):
    /// "channel 09", "net 0", and the mode NAME. A NOTICE when every non-null,
    /// non-skipped value read back equal, a PROBLEM naming what disagreed
    /// otherwise. A session drop mid-restore takes the <see cref="Aborted"/>
    /// text — the step that failed has already written the honest line — and
    /// returns false. For the READ that verdict leaves <c>Completed</c> alone;
    /// for the WRITE a false return makes the summary unclean through the
    /// existing problem count. It is never a retry: a radio that would not take
    /// the value once is not more likely to take it twice, and this runs after
    /// everything else.</para>
    /// </summary>
    private async Task<bool> RestoreOperatingStateAsync(
        OperatingMode? mode, int? channel, int? hopNet, string leg, bool announceRestore = true)
    {
        // NOTHING OBSERVED, NOTHING TO PUT BACK — and therefore nothing to read
        // back either (I-3, and phase-1 audit round 2). A campaign can reach the
        // funnel with all three values null on a session that is still perfectly
        // Ready: the read's leg-0 `SH` and its sentinel can time out against a
        // radio that swallowed them, and if the mode mirror is unconfirmed too
        // (a session that reached Ready on a battery answer without ever seeing
        // a prompt) the legs return before capturing anything. Falling through
        // to the closing read-back there would ask a question with no question
        // in it, and on the quiet radio that produced the case it would turn
        // "there was nothing to put back" into a fault line.
        if (mode is null && channel is null && hopNet is null)
        {
            if (announceRestore) Notice(RestoredNotice(null, null, null, hopFinal: false));
            return true;
        }

        // A-10: a HOP final mode owns the channel, so the channel goes FIRST and
        // is not read back; every other final mode keeps NET → CH → mode.
        bool hopFinal = mode == OperatingMode.Hop;

        if (hopFinal)
        {
            if (!await RestoreChannelAsync(channel, leg).ConfigureAwait(true)) return Aborted(leg);
            if (!await AtPromptAsync(OperatingMode.Hop, leg).ConfigureAwait(true)) return Aborted(leg);
            if (!await RestoreNetAsync(hopNet, leg).ConfigureAwait(true)) return Aborted(leg);
        }
        else
        {
            if (!await RestoreNetAsync(hopNet, leg).ConfigureAwait(true)) return Aborted(leg);
            if (!await RestoreChannelAsync(channel, leg).ConfigureAwait(true)) return Aborted(leg);
            if (mode is { } wantedMode
                && !await AtPromptAsync(wantedMode, leg).ConfigureAwait(true)) return Aborted(leg);
        }

        // ---- THE FRESH READ-BACK ------------------------------------------
        if (hopFinal) _hop.RequestStatus();
        else _s.Ssb.RequestStatus();
        if (!await SentinelAsync(leg).ConfigureAwait(true)) return Aborted(leg);

        var wrong = new List<string>();
        // The channel is compared only when the radio is NOT the one choosing it
        // (A-10): under a HOP final mode the net sets it, and a comparison there
        // would report a difference the campaign deliberately allowed.
        if (!hopFinal && channel is { } expectedChannel) Disagreement(
            _channel.Current, expectedChannel,
            $"channel {expectedChannel:00}", v => $"channel {v:00}", wrong);
        if (hopNet is { } expectedNet) Disagreement(
            _hop.CurrentNet, expectedNet, $"net {expectedNet}", v => $"net {v}", wrong);
        if (mode is { } expectedMode) Disagreement(
            _mode.Mode, expectedMode, ModeWord(expectedMode), ModeWord, wrong);

        if (wrong.Count > 0)
        {
            _summary.Add("The radio did not return to " + string.Join("; ", wrong) + ".");
            return false;
        }
        if (announceRestore) Notice(RestoredNotice(mode, channel, hopNet, hopFinal));
        return true;
    }

    /// <summary>The NET step: at <c>HOP&gt;</c>, <c>NET n</c>, the helper's own
    /// sentinel. A null net sends nothing and is not a failure.</summary>
    private async Task<bool> RestoreNetAsync(int? hopNet, string leg)
    {
        if (hopNet is not { } net) return true;
        if (!await AtPromptAsync(OperatingMode.Hop, leg).ConfigureAwait(true)) return false;
        Status($"Putting HOP net {net} back…");
        _hop.SelectNet(net);
        return await SentinelAsync(leg).ConfigureAwait(true);
    }

    /// <summary>The CHANNEL step: at <c>SSB&gt;</c>, <c>CH nn</c> + <c>SH</c>
    /// (<c>CH nn</c> answers only <c>CHAN nn</c> — the stored six move without
    /// being reported, Stage 4), the helper's own sentinel.</summary>
    private async Task<bool> RestoreChannelAsync(int? channel, string leg)
    {
        if (channel is not { } wanted) return true;
        if (!await AtPromptAsync(OperatingMode.Ssb, leg).ConfigureAwait(true)) return false;
        Status($"Putting channel {wanted:00} back…");
        _channel.Select(wanted);
        return await SentinelAsync(leg).ConfigureAwait(true);
    }

    /// <summary>
    /// The ONE closing restore, run from a campaign's funnel (I-2).
    ///
    /// <para>The Ready guard is not politeness: with the radio gone every step
    /// would only write "the session dropped" lines for a campaign that has
    /// already said so. And the restore is a CLOSING act, not the campaign's
    /// headline — a campaign that stopped early reports the status its LEGS
    /// left, so the progress text here is transient unless the restore itself
    /// aborted and set its own.</para>
    /// </summary>
    private async Task RunClosingRestoreAsync(OperatingMode? mode, int? channel, int? hopNet, bool announceRestore)
    {
        if (_session.Phase != SessionPhase.Ready) return;
        var standingState = State;
        var standing = StatusText;
        await RestoreOperatingStateAsync(mode, channel, hopNet, "operating state", announceRestore).ConfigureAwait(true);

        // THE SCAN RESTART (D8, §5.4c) — the campaign's true end, after the
        // operating-state restore and before the headline is put back. It is
        // ONE ATTEMPT under four conditions; RestoreScanAsync owns them all.
        await RestoreScanAsync("operating state").ConfigureAwait(true);

        // A campaign that has ALREADY named the step it died at keeps that
        // headline: the restore is the last thing that happens, not the reason
        // anything failed, and "Stopped at the operating state step" in place of
        // "Stopped at the zeroize step" would send the operator to the wrong
        // part of the summary. Its own outcome is the summary LINE, which is
        // where the plan puts it. Otherwise the transient progress text is
        // rolled back, unless the restore itself aborted and named that.
        if (standingState == CloneState.Failed) Set(standingState, standing);
        else if (State != CloneState.Failed) Status(standing);
    }

    /// <summary>One read-back comparison. An UNCONFIRMED mirror is a
    /// disagreement of its own kind and says so — "it reports X" would be a lie
    /// about a radio that reported nothing (I-7).</summary>
    private static void Disagreement<T>(
        Confirmed<T> mirror, T expected, string wantedText, Func<T, string> describe, List<string> into)
        where T : struct
    {
        if (!mirror.IsConfirmed) into.Add($"{wantedText} — it has not said where it is");
        else if (!EqualityComparer<T>.Default.Equals(mirror.Value, expected))
            into.Add($"{wantedText} — it reports {describe(mirror.Value)}");
    }

    /// <summary>The success line (§3.4) — PROSE, no wire tokens (I-5 / A-11):
    /// the Console keeps the traffic, this says what the operator will find on
    /// the radio when they pick it up. It names what could NOT be put back,
    /// because a value the campaign never saw is one it must not invent (I-3),
    /// and it does NOT name a channel under a HOP final mode, because the radio
    /// chose that one and the campaign cannot promise it.</summary>
    private static string RestoredNotice(
        OperatingMode? mode, int? channel, int? hopNet, bool hopFinal)
    {
        var kept = new List<string>();
        if (!hopFinal && channel is { } ch) kept.Add($"channel {ch:00}");
        if (hopNet is { } net) kept.Add($"net {net}");
        if (mode is { } m) kept.Add(ModeWord(m));

        var missing = new List<string>();
        if (channel is null) missing.Add("channel");
        if (hopNet is null) missing.Add("HOP net");
        if (mode is null) missing.Add("mode");

        if (kept.Count == 0)
            return "The radio never reported its operating channel, HOP net or mode, so the read "
                + "left it exactly as it found it.";

        // Under a HOP final mode the entry re-imposes the net's channel, so the
        // campaign asked for the captured one BEFORE switching and then stopped
        // claiming it. Said out loud rather than quietly dropped.
        var aside = hopFinal && channel is not null ? " — the net sets the channel" : "";
        var line = $"Left the radio on {string.Join(", ", kept)}{aside}.";
        return missing.Count == 0
            ? line
            : line + $" The radio never reported its {string.Join(" or ", missing)}, "
                + "so that was left as the read found it.";
    }

    private static string ModeWord(OperatingMode mode) => mode.ToString().ToUpperInvariant();

    /// <summary>The FILE's operating-mode name as an enum, or null when it
    /// carries none or one this build does not know. Deliberately a separate
    /// reader from leg 11's own inline parse: leg 11 is byte-for-byte untouched
    /// this round (invariant I-10 / decision A-4), and a shared helper would
    /// have meant editing it.</summary>
    private static OperatingMode? ParseOperatingMode(string? name)
        => name is not null
            && Enum.TryParse<OperatingMode>(name, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
            ? mode
            : null;

    /// <summary>
    /// The lockout READ leg. ONE sentinel-bracketed operation covers the whole
    /// table — bare <c>PROGRAM</c> and bare <c>SELECT</c> are GLOBAL state
    /// reports from whichever prompt the radio is at.
    ///
    /// <para>A read that did not COMMIT, or that committed fewer than the
    /// closed inventory's 22 rows, is FAULTED rather than serialized short: the
    /// write leg sets every row it is given, so a short file would silently
    /// leave the rest at whatever the wipe left them.</para>
    /// </summary>
    private async Task<CloneLockouts> ReadLockoutsAsync()
    {
        bool answered = await AwaitReadAsync(
            _s.Ssb.RequestLockouts(), () => _s.Ssb.LastLockoutRead, "operator lockouts").ConfigureAwait(true);

        var mirror = _s.Ssb.Lockouts;
        bool committed = answered && mirror.State == LockoutReadState.Completed;
        if (committed && mirror.Rows.Count != LockoutInventory.Count)
        {
            _summary.Add($"Operator lockouts: the radio reported {mirror.Rows.Count} of "
                + $"{LockoutInventory.Count} settings, so this domain is incomplete.");
            committed = false;
        }

        return new CloneLockouts
        {
            State = Mark(committed),
            Rows = committed
                ? [.. mirror.Rows.Select(r => new CloneLockout
                {
                    Family = r.Family.ToString(),
                    Section = r.Section.ToString(),
                    Item = r.Item,
                    State = r.State.ToString(),
                })]
                : [],
        };
    }

    /// <summary>
    /// THE FILL-GATE LINE (D5a, plan-clone-write-structural.md §5.4) — the
    /// operator-facing difference between "the radio refused" and "the radio is
    /// empty".
    ///
    /// <para><b>Why it is keyed on OBSERVED STATE and not on a refusal.</b>
    /// There is no per-read refusal signal to key on: <c>PRG 1-3 CHAR SLF</c>
    /// routes to <see cref="AleFillState.NeedSelfAddress"/>
    /// (ResponseParser.cs:570) and never through refusal routing, and
    /// <c>AleReadCompletion</c> carries only <c>(ReadId, Answered)</c>. So the
    /// line reports what the radio SAID about itself, worded as state — which
    /// is also the honest sentence, because the same fact explains every
    /// listing in the family at once.</para>
    ///
    /// <para>ONE line for the whole fill family, not one per domain: TXMSG's
    /// fill-dependence is not established, and claiming it would be an
    /// invention. Domain MARKING rules are deliberately unchanged (a book that
    /// answered nothing is still <c>Read</c>-and-empty) — marking semantics are
    /// a future-round question, recorded here rather than decided in passing.
    /// No raw radio token appears (R13 / I-3).</para>
    /// </summary>
    internal const string FillGateNotice =
        "ALE fill: the radio reports no self address is programmed, so its address, message and "
        + "group listings answer nothing.";

    private void NoteFillGateRefusal(CloneFile file)
    {
        var fill = _s.Ale.FillState;
        if (!fill.IsConfirmed || fill.Value != AleFillState.NeedSelfAddress) return;
        if (file.Selfs.Count > 0 || file.Individuals.Count > 0 || file.Nets.Count > 0) return;
        _summary.Add(FillGateNotice);
    }

    /// <summary>A book that lists NO self while other rows still name one — the
    /// primary-deletion signature, and the one book state a read cannot see the
    /// whole of. A genuinely blank book (which is what a post-wipe read finds)
    /// is not headless.
    /// <para>INTERNAL for its pin: the DEMO cannot reach this state — it drops
    /// an orphan from the member lines, where the real radio KEEPS naming it
    /// (§1) — so the rule is pinned against the file shapes directly rather
    /// than against a demo that would have to invent the very behaviour the
    /// rule exists for.</para></summary>
    internal static bool IsHeadlessBook(CloneFile file)
        => file.Selfs.Count == 0
        && (file.Individuals.Count > 0
            || file.Nets.Any(n => n.Members.Count > 0)
            || file.Schedules.Count > 0);

    /// <summary>One prompt's worth of manifest settings: issue each row's
    /// read (the SH block once, plus the distinct targeted queries), close
    /// with a sentinel, then take whatever the mirrors confirmed.</summary>
    private async Task<bool> ReadSettingsLegAsync(CloneFile file, string prompt)
    {
        Status($"Reading the {(prompt == "ALE>" ? "ALE" : "SSB")} settings…");
        var rows = CloneSettingsManifest.Rows.Where(r => r.Prompt == prompt).ToList();

        if (rows.Any(r => r.ReadOp == "SH"))
        {
            if (prompt == "ALE>") _s.Ale.RequestSettings();
            else _s.Ssb.RequestStatus();
        }
        var issued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Query is not null))
            if (issued.Add(row.ReadOp)) row.Query!(_s);

        if (!await SentinelAsync("settings").ConfigureAwait(true)) return false;

        foreach (var row in rows)
        {
            if (row.Read(_s) is not { Length: > 0 } value)
            {
                // Serialization never invents: an unconfirmed mirror simply
                // has no row, and the omission is reported.
                _summary.Add($"Setting {row.Key}: the radio did not report it, so the file does not carry it.");
                continue;
            }
            file.Settings.Add(new CloneSetting { Key = row.Key, Value = value });
        }
        return true;
    }

    // ======================= WRITE CAMPAIGN ================================

    /// <summary><paramref name="rows"/> is the identity TABLE (R-A): one
    /// <see cref="SelfDisposition"/> per self the operator changed. An EMPTY
    /// table is all-Keep, which is byte-identical to the old no-identity
    /// write.</summary>
    public async Task<bool> WriteAsync(IReadOnlyList<SelfDisposition> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (IsRunning) return false;
        _summary.Clear();
        _notices = 0;
        _wiped = false;
        // THE SCAN CONTEXT (§5.4c), created by this public entry and by no
        // other path. `StartPending` is FALSE: the write's pre-zeroize
        // occupancy is D8's one EXEMPTION — `ZERO` is still the campaign's
        // first wire command from any prompt, and the wipe itself is the stop.
        // The restart licence is taken from the mirror at RunWriteCampaignAsync,
        // which costs no wire.
        _scan = new ScanContext { StartPending = false };

        if (WriteBlockedReason is { } blocked)
        {
            Set(CloneState.Failed, blocked);
            return false;
        }
        // ---- Preflight: the identity table, the transform, then the GRAPH --
        // LAYER 1 — the friendly early refusal, and the whole input contract in
        // one place (§3.2). Every reason a table cannot be applied — a name
        // that is already a net, a swap on the scan-gate self, a replacement
        // that collides with a demoted self, a file with no self and no name
        // chosen for it — is caught here, before anything is asked or sent, and
        // named in one sentence.
        if (CloneSwap.Refusal(File!, rows) is { } refusal)
        {
            Set(CloneState.Failed, refusal);
            return false;
        }

        // Accepted input, so this cannot throw (I-4).
        var swap = CloneSwap.Apply(File!, rows);
        var target = swap.File;
        // ROLE CHANGES are NOTICES and DROPS are PROBLEMS (I-6): a role change
        // is something the campaign DID, at the operator's instruction, and it
        // must not make an otherwise-perfect clone read as failed — but it must
        // never be silent either, which is exactly what cost HOS.
        foreach (var change in swap.RoleChanges) Notice(change);
        foreach (var drop in swap.Drops) _summary.Add(drop);

        // LAYER 2 — defence in depth. The campaign writes the TRANSFORMED
        // file, which no LOAD ever validated. Whatever produced this graph —
        // the transform, a future transform, an in-memory file adopted by some
        // other route — it goes through the SAME validation a loaded file does,
        // BEFORE the confirmation and before one byte reaches the wire. An
        // invalid graph discovered mid-book would be discovered AFTER the
        // erase, on a half-rewritten radio.
        try
        {
            target.Validate();
        }
        catch (CloneFileFormatException ex)
        {
            Set(CloneState.Failed, "The clone cannot be written — " + ex.Message);
            return false;
        }

        // ---- Leg 1: the ONE confirmation ----------------------------------
        bool accepted;
        try
        {
            accepted = await _prompt.ConfirmAsync(
                    ConfirmTitle, ConfirmMessageFor(swap.RoleChanges, swap.Drops), ConfirmAccept, ConfirmCancel)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A faulted or cancelled prompt task sends NOTHING (round-10
            // lifecycle contract).
            Set(CloneState.Idle, "Write cancelled.");
            return false;
        }
        if (!accepted)
        {
            Set(CloneState.Idle, "Write cancelled.");
            return false;
        }
        // Re-check the send gate AFTER the await — the session may have gone
        // while the popup was open (round-10 lifecycle contract).
        if (WriteBlockedReason is { } reblocked)
        {
            Set(CloneState.Failed, reblocked);
            return false;
        }

        Set(CloneState.Writing, "Writing the clone…");
        IReadOnlyList<string> diffs = [];
        // ---- THE WRITE'S CLOSING-RESTORE FUNNEL (A-12) --------------------
        // Once leg 2's `ZERO` has gone out the radio HAS been touched, and every
        // exit from here on is an exit from a radio this campaign moved: the
        // write body aborting at a leg, the verify read failing, or the whole
        // thing succeeding. All three used to `return` straight past the
        // restore, and the audit reproduced the residue — a Ready-phase verify
        // failure that left the radio sitting at `ALE>`. So the restore runs
        // from ONE `finally`, guarded only by "did the wipe go out" and "is the
        // radio still reachable". Everything ABOVE this point — the preflight
        // refusals, the confirm, a cancel — has touched nothing and restores
        // nothing.
        //
        // THE WIRE LEASE (§5.2) IS OUTERMOST, and the restore `finally` sits
        // INSIDE it: an exception, an Aborted() path or any of the early
        // returns below releases the lease by the language, and no producer can
        // wake up during the restore lap. The nested VERIFY read campaign runs
        // INSIDE this one lease — it is not a campaign of its own and takes
        // none, which is why the coordinator's Changed edges fire exactly once
        // each per write.
        using (_wire.Enter())
        {
            // D20: THE CAMPAIGN STARTS WITH A CLEAN SENTINEL LEDGER — the read's
            // rule, at the write's own start. Inside the lease and before the
            // campaign's first own sentinel: leg 1 was the confirmation, which
            // touches no wire, and leg 2's `ZERO` is the first line out. A retry
            // after a failed write therefore never inherits that write's standing
            // debt (Prc138Radio.ResetSentinelLedger carries the arithmetic).
            _radio.ResetSentinelLedger();
            try
            {
                if (!await RunWriteCampaignAsync(target).ConfigureAwait(true)) return false;

                // ---- Leg 12: FULL VERIFY ----------------------------------
                // `restore: false` (§3.4, decision A-4): the verify is a READ
                // of the radio leg 11 just finished with, and its comparison of
                // all three operating fields depends on finding exactly what
                // leg 11 left. A restore inside it would be comparing the radio
                // against a state this campaign had just re-imposed. The
                // write's own restore is an ADDITIONAL closing act, in the
                // finally — one per campaign (I-2).
                Status("Verifying — reading the radio back…");
                var (after, verified) = await RunReadCampaignAsync(restore: false).ConfigureAwait(true);
                if (!verified)
                {
                    // The verify is a READ CAMPAIGN, and it can stop for the
                    // same reasons any of them can — a mode gate that never
                    // confirms is one, and the phase-1 audit reproduced exactly
                    // that. Saying "the session dropped" while the radio is
                    // still answering would send the operator looking for a
                    // cable fault; the leg that stopped has already written its
                    // own line.
                    Set(CloneState.Failed, _session.Phase == SessionPhase.Ready
                        ? "Verification stopped early — see the summary."
                        : "The session dropped during verification.");
                    return false;
                }
                // D3: a domain whose WRITE LEG was abandoned is not compared at
                // all — tens of expected diffs for rows nothing ever sent would
                // bury the one line that says why.
                diffs = CloneCompare.Diff(Expected(target, after), after, _notAttempted);
                foreach (var diff in diffs) _summary.Add(diff);
                NoteUnattemptedDomains();
            }
            finally
            {
                // The FILE's values, not the verify's: leg 11 already put them
                // on the radio, and the only thing that moved them again is the
                // verify's own mode lap.
                if (_wiped)
                    // The WRITE claims no restore line (owner 2026-08-30: the "Left the
                    // radio on…" notice made every clean write read "with warnings").
                    await RunClosingRestoreAsync(
                        ParseOperatingMode(target.OperatingMode),
                        target.OperatingChannel, target.OperatingHopNet,
                        announceRestore: false).ConfigureAwait(true);
            }
        }

        // NOTICES do not make a campaign unclean — see the _notices field.
        // A restore that DISAGREED wrote a problem line instead, and that is
        // what makes the campaign unclean here — never a retry.
        int problems = _summary.Count - _notices;
        bool clean = diffs.Count == 0 && problems == 0;
        Set(clean ? CloneState.Done : CloneState.Failed,
            clean
                ? _notices == 0
                    // D9 CATEGORY B (owner ruling 2026-08-29): the STATUS LINE
                    // CARRIES THE VERDICT ONLY — the note count and the
                    // problem count are read off the summary lines below it,
                    // which are unchanged.
                    ? "Write complete."
                    : "Write complete with warnings."
                : "Write incomplete.");
        return clean;
    }

    /// <summary>
    /// The expected post-write state: the TRANSFORMED file, with the two
    /// bookkeeping fields the radio cannot echo taken from the verify read —
    /// the capture timestamp (informational) and the read-state markers (the
    /// comparison checks those separately, against READ, in
    /// <see cref="CloneCompare"/>).
    /// </summary>
    private static CloneFile Expected(CloneFile target, CloneFile after)
    {
        var expected = CloneSwap.Apply(target, []).File;   // all-Keep: a pure deep copy
        expected.CapturedUtc = after.CapturedUtc;
        expected.OperatingState = after.OperatingState;
        expected.BookState = after.BookState;
        expected.GroupState = after.GroupState;
        expected.ScheduleState = after.ScheduleState;
        expected.ChannelState = after.ChannelState;
        expected.HopNetState = after.HopNetState;
        expected.ExcludeState = after.ExcludeState;
        expected.ModemState = after.ModemState;
        expected.MessageState = after.MessageState;
        expected.SettingState = after.SettingState;
        if (expected.Lockouts is not null)
            expected.Lockouts.State = after.Lockouts?.State ?? CloneDomainState.Unread;
        return expected;
    }

    private async Task<bool> RunWriteCampaignAsync(CloneFile target)
    {
        // D3: no leg has been abandoned yet. Cleared HERE, at the one place a
        // write's leg table begins, so the verify below can never inherit a
        // previous campaign's suppression.
        _notAttempted.Clear();

        // THE FOUND SCAN STATE (round 14 F2), taken before leg 2 destroys it.
        NoteFoundScanState();

        // ====================================================================
        // THE ROUND-12 LEG TABLE (plan-clone-round12.md §3). Leg 1 is the ONE
        // confirmation, already asked by the caller.
        //
        // EVERY DELETED RECONCILE LEG'S ABSENCE IS PINNED, and each pin cites
        // the same owner statement: "it's safe to assume that zeroize clears
        // everything except for the remote port baud rate." Leg 2 makes the
        // target GUARANTEED BLANK, so nothing below may reconcile, clear-first
        // or delete: those legs existed to converge onto an UNKNOWN target, and
        // there is no longer such a thing. Invariant 3 states the other half —
        // no leg assumes anything about target state either.
        // ====================================================================

        // ---- Leg 2: ZEROIZE, then the SETTLE GATE (X13) --------------------
        // THE FIRST WIRE ACT AFTER THE CONFIRMATION, LITERALLY (owner ruling
        // R1 + invariant 3; the literal reading ruled 2026-08-19). No checkbox,
        // no condition — and NO NAVIGATION: the wipe goes out from whatever
        // prompt is live.
        //
        // That is affordable because the radio says so, not because it is
        // convenient (bench/transcripts/r12-zero-prompts-20260819-061052.jsonl):
        // `ZERO` is ACCEPTED at `ALE>` and at `HOP>` as well as `SSB>` — each
        // answering the same `*** ZEROIZING RAM -- PLEASE WAIT ***` — and from
        // EVERY starting prompt the settle ends with the radio answering at
        // `SSB>`. The campaign lands exactly where leg 3 needs it with zero
        // navigation of its own. (An ALE-context wipe interleaves `IN_PROG`, a
        // prompt echo and `PRG 1-3 CHAR SLF` BEFORE the banner — which is why
        // Core gates its settle on the banner and not on any prompt. A
        // HOP-context wipe adds nothing of its own: the bare `HOP>` at the head
        // of that leg in the transcript is the TRAILING PROMPT of the `HO` that
        // preceded it, arriving late. Both streams are parser fixtures.)
        if (!await AtPromptAsync(null, "zeroize").ConfigureAwait(true)) return Aborted("zeroize");
        Status("Wiping the radio…");
        _s.Ssb.ZeroizeRadio();
        // A-12: from this byte on, every exit owes the operator a closing
        // restore. Set immediately after the send and never cleared here — the
        // radio has been wiped whether or not the settle below comes back.
        _wiped = true;
        if (!await AwaitZeroizeSettleAsync().ConfigureAwait(true)) return Aborted("zeroize");

        // A FRESH MODE QUERY before anything trusts a prompt again — BELT, not
        // navigation. The settle boundary reset every mirror, including the
        // confirmed operating mode, precisely so nothing downstream reads a
        // value from before the wipe; the stale-confirmed-mode trap is that
        // AtPromptAsync would otherwise believe an SSB leg was already at its
        // prompt. The radio is at `SSB>` by its own behaviour; this is what
        // makes the app KNOW it.
        _s.Ssb.RequestStatus();
        if (!await SentinelAsync("zeroize").ConfigureAwait(true)) return Aborted("zeroize");

        // ---- Leg 3 (SSB>): the SSB channels, every NON-DEFAULT slot ---------
        // D4: leg 2 has just left every slot holding Wire.DefaultChannel, so a
        // file row equal to that tuple is a sequence that would set the radio to
        // what it already is. The skip is applied to ANY file — an elided one
        // has no such rows to begin with, and a LEGACY full file gets the same
        // ~90 sequences saved without being rewritten.
        if (!await AtPromptAsync(OperatingMode.Ssb, "SSB channels").ConfigureAwait(true)) return Aborted("SSB channels");
        foreach (var channel in target.Channels)
        {
            if (channel.IsFactoryDefault()) continue;
            Status($"Writing channel {channel.Number:00}…");
            _channel.SelectForStore(channel.Number);
            WriteChannelFields(channel);
            if (!await SentinelAsync("SSB channels").ConfigureAwait(true)) return Aborted("SSB channels");
        }

        // ---- Leg 4 (SSB>): the modem presets 0-6 --------------------------
        // Preset writes are issued at SSB> by every caller in this app, and for
        // a captured reason: at an ALE> prompt a DIS-carrying write applies the
        // disable SILENTLY and DISCARDS its field arguments.
        //
        // F9: SCOPED to 0-6. Presets 7-9 belong to the `HOP>` prompt, which
        // refuses this line shape (and this prompt refuses those numbers), so
        // they are written in leg 9 instead.
        foreach (var preset in target.ModemPresets.Where(
                     p => ModemPresetScope.Covers(OperatingMode.Ssb, p.Number)))
        {
            Status($"Writing modem preset {preset.Number}…");
            if (!WritePreset(preset)) continue;
            if (!await SentinelAsync("modem presets").ConfigureAwait(true)) return Aborted("modem presets");
        }

        // ---- Leg 5 (ALE>): the stored messages ----------------------------
        // MOVED to the ALE prompt (clone round 12): the TXMSG family is
        // ALE-ONLY and answers `** ERROR **` at SSB> and HOP> (captured
        // 2026-08-18). The round-11 leg issued it at SSB>, where the real radio
        // would have refused every line of it.
        //
        // ABSENCE PIN — the per-slot DELETE is DELETED. It existed so a
        // target-only slot could not survive; after leg 2 every slot is already
        // empty (owner statement §1), so this leg is STORE-ONLY and the domain
        // still verifies to EXACT equality.
        if (!await AtPromptAsync(OperatingMode.Ale, "stored messages").ConfigureAwait(true))
            return Aborted("stored messages");
        // THE SCAN STOP is no longer issued here: D8 moved it INTO the mode
        // funnel above, which is the only place that sees every ALE occupancy —
        // this leg's entry, the settings lap's re-entry, the book, the groups,
        // the verify's own laps. AtPromptAsync has already sent it.
        Status("Writing the stored messages…");
        foreach (var message in target.Messages)
            _s.Ale.ProgramStoredMessage(message.Slot, message.Text);
        if (target.Messages.Count > 0
            && !await SentinelAsync("stored messages").ConfigureAwait(true))
            return Aborted("stored messages");

        // ---- Leg 6 (SSB> then ALE>): the manifest settings, in ORDER ------
        // AnalogSquelch is NOT here: it is the one FinalsOrder row, written at
        // leg 11 after the FM-squelch cycle this leg can arm has completed.
        if (!await WriteSettingsLegAsync(target, "SSB>", OperatingMode.Ssb).ConfigureAwait(true)) return false;
        if (!await WriteSettingsLegAsync(target, "ALE>", OperatingMode.Ale).ConfigureAwait(true)) return false;

        // ---- Leg 7 (ALE>): the book, in dependency order ------------------
        // ABSENCE PIN — there is NO ERASE leg. The round-11 campaign erased the
        // ALE fill here because it was replaying onto an unknown book; leg 2
        // already cleared it (owner statement §1), and the standalone ALE-erase
        // card keeps its own confirm and its own leg, untouched.
        // Selfs FIRST and in post-swap order (the chosen primary leads —
        // the radio makes the first-created self the primary), then
        // individuals, then nets, then each net's members.
        //
        // FLATTENED UP FRONT (D3): the leg's operations are one ordered list,
        // because that is what lets the abandonment line say how many rows were
        // not attempted without a second copy of the ordering rule.
        var bookOps = new List<GatedOperation>();
        foreach (var self in target.Selfs)
            bookOps.Add(new GatedOperation(
                $"Writing self {self.Name}…", $"self {self.Name}",
                () => _s.Ale.ProgramSelf(self.Name, self.Group),
                () => _s.Ale.RequestStationBook()));
        foreach (var individual in target.Individuals)
            bookOps.Add(new GatedOperation(
                $"Writing individual {individual.Name}…", $"individual {individual.Name}",
                () => _s.Ale.ProgramIndividual(individual.Name, individual.Group, individual.AssociatedSelf ?? ""),
                () => _s.Ale.RequestStationBook()));
        foreach (var net in target.Nets)
        {
            bookOps.Add(new GatedOperation(
                $"Writing net {net.Name}…", $"net {net.Name}",
                () => _s.Ale.ProgramNet(net.Name, net.Group, net.AssociatedSelf ?? ""),
                () => _s.Ale.RequestStationBook()));
            foreach (var member in net.Members)
                bookOps.Add(new GatedOperation(
                    $"Writing net {net.Name}…", $"member {member} of net {net.Name}",
                    () => _s.Ale.ProgramNetMember(net.Name, member),
                    () => _s.Ale.RequestNetMembers(net.Name)));
        }
        if (!await RunGatedLegAsync(bookOps, "ALE fill", BookDomain, "ALE book", "book")
                .ConfigureAwait(true))
            return Aborted("ALE fill");

        // ---- Leg 8 (ALE>): the channel groups, PURE ADDC writes -----------
        // ABSENCE PIN — the RECONCILE is DELETED. It read each group back and
        // removed the channels the file did not carry; after leg 2 every group
        // is empty (owner statement §1), so there is nothing to remove and the
        // read that fed the removal is gone with it.
        //
        // D3's INTERACTION RULE: an abandoned gated leg abandons ONLY ITSELF.
        // An abandoned BOOK leg returns true above, so this leg runs with its
        // own debt budget and its own abandonment record.
        var groupOps = new List<GatedOperation>();
        foreach (var group in target.ChannelGroups)
            foreach (int channel in group.Channels.Order())
                groupOps.Add(new GatedOperation(
                    $"Writing channel group {group.Group}…",
                    $"add channel {channel:00} to group {group.Group}",
                    () => _s.Ale.ProgramScanChannel(group.Group, channel),
                    () => _s.Ale.RequestChannelGroup(group.Group)));
        if (!await RunGatedLegAsync(groupOps, "channel groups", GroupDomain, "Channel groups",
                "channel group").ConfigureAwait(true))
            return Aborted("channel groups");

        // ---- Leg 8b (ALE>): the LQA schedules ------------------------------
        // MOVED HERE FROM THE TAIL OF LEG 7 (D10, §5.4d), same `ALE>`
        // occupancy, no new mode lap. `SOU STA`/`EXC STA` are REFUSED
        // (`SELF/INDIV/NET CHANS REQD`) while the named station's channel group
        // is empty, and a zeroize-first campaign leaves every group empty until
        // leg 8 above has run — so the old order could NEVER land a schedule.
        // Proven on the wire: the 2026-08-28 instrument write's
        // `SOU STA W6HOS1 01:00 21:30` was refused at tMs 219786, and the
        // owner's 2026-08-29 read answered `NO LQA SCHEDULED`.
        //
        // Everything else about the leg is UNCHANGED: schedules are not
        // gate-scoped writes (EXCH/SOU are operational commands), so they go out
        // directly, a refusal stays non-fatal, and the FULL VERIFY remains their
        // check. It runs whichever gated leg above was abandoned (§5.4d).
        foreach (var row in target.Schedules)
        {
            Status($"Queuing {row.Kind.ToLowerInvariant()} for {row.Address}…");
            if (row.Kind == "SOUND") _s.Ale.StartSounding(row.Address, row.Interval, row.Start);
            else _s.Ale.StartExchange(row.Address, row.Interval, row.Start);
        }
        if (target.Schedules.Count > 0
            && !await SentinelAsync("LQA schedules").ConfigureAwait(true))
            return Aborted("LQA schedules");

        // THE SCAN RESTART used to fire here. D8 moved it to the campaign's
        // TRUE end — the closing-restore funnel — because the verify lap
        // re-enters ALE after this point, and a scan restarted here (or resumed
        // by that entry) would run underneath the verify's own book reads.

        // ---- Leg 9 (HOP>): the nets, PURE writes, then the bands ----------
        // ABSENCE PIN — the CLEAR-FIRST wipe is DELETED. `HOPSET n DEL` led
        // every net so the replay was idempotent over an unknown record; leg 2
        // leaves every net already wiped (owner statement §1). A net the file
        // records as wiped is simply not written.
        if (!await AtPromptAsync(OperatingMode.Hop, "HOP nets").ConfigureAwait(true)) return Aborted("HOP nets");
        foreach (var net in target.HopNets)
        {
            if (net.Wiped) continue;
            Status($"Writing HOP net {net.Number}…");
            WriteHopNet(net);
            if (!await SentinelAsync("HOP nets").ConfigureAwait(true)) return Aborted("HOP nets");
        }

        // ---- Leg 9b (HOP>): the modem presets 7-9 -------------------------
        // F9: the half of the modem book that only exists at this prompt (P5).
        // The line has no TYPE and the EN/DIS token rides its own line LAST,
        // because any field write RE-ENABLES a disabled preset (P5b) — both are
        // the builder's business (SsbController.ProgramHopModemPreset); this
        // leg's business is being at `HOP>` when it goes out.
        foreach (var preset in target.ModemPresets.Where(
                     p => ModemPresetScope.Covers(OperatingMode.Hop, p.Number)))
        {
            Status($"Writing modem preset {preset.Number}…");
            if (!WriteHopPreset(preset)) continue;
            if (!await SentinelAsync("modem presets").ConfigureAwait(true)) return Aborted("modem presets");
        }

        // ABSENCE PIN — the exclusion-band RECONCILE is DELETED: no read-back,
        // no removal of target-only bands, no same-slot comparison. After leg 2
        // the table is empty (owner statement §1), so every band in the file is
        // a PURE set.
        Status("Writing the exclusion bands…");
        foreach (var band in target.ExcludeBands)
        {
            if (KHzToHz(band.LowKHz) is { } low && KHzToHz(band.HighKHz) is { } high)
                _hop.ProgramExcludeBand(band.Band, low, high);
            else
                _summary.Add($"Exclusion band {band.Band}: the file's edges "
                    + $"({band.LowKHz}-{band.HighKHz}) are not values this radio accepts, so it was not written.");
        }
        if (target.ExcludeBands.Count > 0
            && !await SentinelAsync("exclusion bands").ConfigureAwait(true))
            return Aborted("exclusion bands");

        // ---- Leg 10: the operator lockouts, per section --------------------
        if (!await WriteLockoutsLegAsync(target).ConfigureAwait(true)) return Aborted("operator lockouts");

        // ---- Leg 11: the finals — net, channel, squelch, then MODE LAST ----
        if (target.OperatingHopNet is { } hopNet)
        {
            if (!await AtPromptAsync(OperatingMode.Hop, "operating state").ConfigureAwait(true))
                return Aborted("operating state");
            Status($"Selecting HOP net {hopNet}…");
            _hop.SelectNet(hopNet);
            if (!await SentinelAsync("operating state").ConfigureAwait(true)) return Aborted("operating state");
        }
        if (target.OperatingChannel is { } operatingChannel)
        {
            if (!await AtPromptAsync(OperatingMode.Ssb, "operating state").ConfigureAwait(true))
                return Aborted("operating state");
            Status($"Selecting channel {operatingChannel:00}…");
            _channel.Select(operatingChannel);
            if (!await SentinelAsync("operating state").ConfigureAwait(true)) return Aborted("operating state");
        }

        // The FinalsOrder row, in SSB context and after the channel selection.
        if (!await WriteAnalogSquelchAsync(target).ConfigureAwait(true)) return Aborted("settings");

        // `Enum.IsDefined` here too (P2 audit round 1's sweep). Validation
        // already refuses an undefined mode at the door, so this can only ever
        // be belt — but a TryParse without it is the r3 shape, and leaving the
        // shape around is how the defect came back the first time.
        if (target.OperatingMode is { } modeName
            && Enum.TryParse<OperatingMode>(modeName, ignoreCase: true, out var finalMode)
            && Enum.IsDefined(finalMode))
        {
            if (!await AtPromptAsync(finalMode, "operating state").ConfigureAwait(true))
                return Aborted("operating state");
        }
        return true;
    }

    /// <summary>
    /// Leg 2's settle gate. Core owns the polling — bare CRs over its INTERNAL
    /// send path, because a `Ping()` would carry late-answer debt into a
    /// multi-second silence — and publishes a settled/faulted observable; this
    /// only AWAITS it. The bound here is deliberately LONGER than Core's, so
    /// the verdict is always Core's fault report and never a second, competing
    /// timeout. Test hook: <see cref="ZeroizeSettleTimeoutMs"/>.
    /// </summary>
    private async Task<bool> AwaitZeroizeSettleAsync()
    {
        long deadline = Environment.TickCount64 + ZeroizeSettleTimeoutMs;
        while (!_s.Ssb.ZeroizeSettled && !_s.Ssb.ZeroizeFaulted)
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _summary.Add("Zeroize: the session dropped while the radio was wiping, so the write "
                    + "stopped here. The radio has been wiped and NOT rewritten.");
                return false;
            }
            if (Environment.TickCount64 > deadline)
            {
                _summary.Add("Zeroize: the radio never came back after the wipe, so the write stopped "
                    + "here. The radio has been wiped and NOT rewritten.");
                return false;
            }
            await Task.Delay(ReadPollMs).ConfigureAwait(true);
        }
        if (_s.Ssb.ZeroizeFaulted)
        {
            _summary.Add("Zeroize: the radio did not answer within the wipe's settle window, so the "
                + "write stopped here. The radio has been wiped and NOT rewritten.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Leg 10 — the operator lockouts, written LAST of all the programming.
    ///
    /// <para><b>Per SECTION, at that section's own prompt.</b> A set names no
    /// section on the wire: it scopes to the ACTIVE PROMPT's mode section
    /// (captured 2026-08-18, all six discrimination cells), so writing a
    /// section's lockouts means STANDING at its prompt. Nothing in Core does
    /// that for a caller; the orchestrator owns prompt positioning.</para>
    ///
    /// <para><b>Every row is written, not just the ones that differ.</b> The
    /// wipe left all 22 at LOCK, but invariant 3 forbids a leg from assuming
    /// anything about target state — and a set has NO accept/reject semantics
    /// at all, so the state report is the only confirmation there could be.
    /// The verify leg is that report.</para>
    /// </summary>
    private async Task<bool> WriteLockoutsLegAsync(CloneFile target)
    {
        var rows = target.Lockouts?.Rows ?? [];
        if (rows.Count == 0) return true;

        foreach (var (mode, section) in new[]
        {
            (OperatingMode.Ssb, LockoutSection.Ssb),
            (OperatingMode.Ale, LockoutSection.Eam),
            (OperatingMode.Hop, LockoutSection.Hop),
        })
        {
            var forSection = rows
                .Where(r => string.Equals(r.Section, section.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (forSection.Count == 0) continue;

            if (!await AtPromptAsync(mode, "operator lockouts").ConfigureAwait(true)) return false;
            Status($"Writing the {mode.ToString().ToUpperInvariant()} lockouts…");
            foreach (var row in forSection)
            {
                if (!Enum.TryParse<LockoutFamily>(row.Family, ignoreCase: true, out var family)
                    || !Enum.IsDefined(family)
                    || !Enum.TryParse<LockState>(row.State, ignoreCase: true, out var state)
                    || !Enum.IsDefined(state))
                {
                    _summary.Add($"Lockout {row.Family} {row.Section} {row.Item}: the file's value "
                        + "is not one this radio accepts, so it was not written.");
                    continue;
                }
                Try($"lockout {row.Family} {row.Section} {row.Item}",
                    () => _s.Ssb.SetLockout(family, section, row.Item, state));
            }
            if (!await SentinelAsync("operator lockouts").ConfigureAwait(true)) return false;
        }
        return true;
    }

    /// <summary>
    /// The <see cref="CloneSettingsManifest.FinalsOrder"/> row — AnalogSquelch
    /// (owner ruling R4; plan §3 leg 6, owner-adopted sequencing 2026-08-18).
    ///
    /// <para><b>Why it cannot be written with the other settings.</b> Core owes
    /// an <c>SQ OFF</c>/<c>SQ ON</c> cycle after any FM-property change — a
    /// deliberate, documented compensation for an AUDIO defect, and NOT touched
    /// (R4) — and that cycle fires on a LATER modulation report. A squelch
    /// written in leg 6 would simply be overwritten by it.</para>
    ///
    /// <para><b>The sequencing.</b> After the final channel selection the radio
    /// is in SSB context, so ONE modulation read (the <c>SH</c> block) is what
    /// FIRES any pending cycle. GREEN: the cycle completes, the flag clears,
    /// the row is written and the verify checks it. RED: the flag is still up
    /// when the settle bound expires, and the row is SKIPPED and NAMED — never
    /// written into a cycle that would overwrite it. Both paths are reachable
    /// and both are pinned.</para>
    /// </summary>
    private async Task<bool> WriteAnalogSquelchAsync(CloneFile target)
    {
        var row = CloneSettingsManifest.Rows
            .FirstOrDefault(r => r.Order == CloneSettingsManifest.FinalsOrder);
        if (row is null) return true;
        if (target.Value(row.Key) is not { } stored)
        {
            _summary.Add($"Setting {row.Key}: the file does not carry it, so nothing was written.");
            return true;
        }
        if (!await AtPromptAsync(OperatingMode.Ssb, "settings").ConfigureAwait(true)) return false;

        Status("Settling the squelch…");
        _s.Ssb.RequestStatus();
        if (!await SentinelAsync("settings").ConfigureAwait(true)) return false;

        long deadline = Environment.TickCount64 + AnalogSquelchSettleMs;
        while (_s.Ssb.IsFmSquelchCyclePending)
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _summary.Add($"Setting {row.Key}: the session dropped before it could be written.");
                return false;
            }
            if (Environment.TickCount64 > deadline) break;
            await Task.Delay(ReadPollMs).ConfigureAwait(true);
        }
        if (_s.Ssb.IsFmSquelchCyclePending)
        {
            _summary.Add($"Setting {row.Key}: the radio still owed an automatic squelch cycle after the "
                + "FM settings, so it was not written — the cycle would have overwritten it.");
            return true;
        }

        try
        {
            row.Write(_s, stored);
        }
        catch (CloneValueException ex) { _summary.Add(ex.Message); return true; }
        catch (ArgumentException ex) { _summary.Add($"Setting {row.Key}: {ex.Message}"); return true; }
        return await SentinelAsync("settings").ConfigureAwait(true);
    }

    private async Task<bool> WriteSettingsLegAsync(CloneFile target, string prompt, OperatingMode mode)
    {
        var rows = CloneSettingsManifest.Rows
            .Where(r => r.Prompt == prompt && r.Order != CloneSettingsManifest.FinalsOrder)
            .OrderBy(r => r.Order)
            .ToList();
        if (rows.Count == 0) return true;
        if (!await AtPromptAsync(mode, "settings").ConfigureAwait(true)) { Aborted("settings"); return false; }

        Status($"Writing the {(prompt == "ALE>" ? "ALE" : "SSB")} settings…");
        bool wrote = false;
        foreach (var row in rows)
        {
            if (target.Value(row.Key) is not { } stored)
            {
                _summary.Add($"Setting {row.Key}: the file does not carry it, so nothing was written.");
                continue;
            }
            try
            {
                row.Write(_s, stored);
                wrote = true;
            }
            catch (CloneValueException ex) { _summary.Add(ex.Message); }
            catch (ArgumentException ex) { _summary.Add($"Setting {row.Key}: {ex.Message}"); }
        }
        if (wrote && !await SentinelAsync("settings").ConfigureAwait(true)) { Aborted("settings"); return false; }
        return true;
    }

    // ---- write helpers -----------------------------------------------------

    private void WriteChannelFields(CloneChannel channel)
    {
        Try($"channel {channel.Number:00} receive frequency", () => _s.Ssb.SetRxFrequency(channel.RxFrequency));
        Try($"channel {channel.Number:00} transmit frequency", () => _s.Ssb.SetTxFrequency(channel.TxFrequency));
        if (Wire.ParseModulation(channel.Mode.ToUpperInvariant()) is { } modulation)
            Try($"channel {channel.Number:00} modulation", () => _s.Ssb.SetModulation(modulation));
        else Unwritable($"channel {channel.Number:00} modulation", channel.Mode);
        if (Wire.ParseDumpAgc(channel.Agc) is { } agc)
            Try($"channel {channel.Number:00} AGC", () => _s.Ssb.SetAgc(agc));
        else Unwritable($"channel {channel.Number:00} AGC", channel.Agc);
        Try($"channel {channel.Number:00} bandwidth", () => _s.Ssb.SetBandwidth(channel.Bandwidth));
        if (Wire.ParseYesNo(channel.RxOnly.ToUpperInvariant()) is { } rxOnly)
            Try($"channel {channel.Number:00} receive-only", () => _s.Ssb.SetRxOnly(rxOnly));
        else Unwritable($"channel {channel.Number:00} receive-only", channel.RxOnly);
    }

    // The campaign's own two-entry AGC map is DELETED (F5, decision D3): it knew
    // SL and ME and fell through to the full-spelling parser for everything
    // else, so the source radio's CH 09 — dump token `FA` — read as a value
    // this radio does not accept and turned up in the field summary as a
    // refusal. The one mapping now lives in `Wire.ParseDumpAgc`, which the
    // channel editor already had in its own copy.

    private bool WritePreset(CloneModemPreset preset)
    {
        var tokens = preset.Fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? name = tokens.Length > 0 ? tokens[0] : null;
        string? type = ModemPresetVocabulary.TypeFromListing(ModemPresetRow.TokenAfter(tokens, "TYPE"));
        string? dataMode = ModemPresetVocabulary.DataModeFromListing(ModemPresetRow.DataModePhrase(tokens));
        string? baud = ModemPresetVocabulary.BaudFromListing(ModemPresetRow.TokenAfter(tokens, "BAUD"));
        if (name is null || type is null || dataMode is null || baud is null)
        {
            _summary.Add($"Modem preset {preset.Number}: the file's row is not one this app can re-send, "
                + "so the preset was not written.");
            return false;
        }
        string? interleave = ModemPresetVocabulary.InterleaveFromListing(
            ModemPresetRow.TokenAfter(tokens, "INTER") ?? ModemPresetRow.TokenAfter(tokens, "INTERLEAV"));
        string? mark = ModemPresetRow.TokenAfter(tokens, "MARK");
        string? space = ModemPresetRow.TokenAfter(tokens, "SPACE");
        // MARK/SPACE ARE WRITTEN ONLY WHERE THE FILE CARRIES THEM (owner ruling
        // R3) — UNCHANGED. The raw Fields row is the single source of truth:
        // the tones are stored on every FSK type but LISTED only at `fsk-v`, so
        // a row with no MARK/SPACE tokens (`mark`/`space` null here) is one
        // whose tones were invisible at capture, and the write leaves the
        // target's own tones alone rather than guessing at them.
        //
        // D14 (plan-clone-write-structural §2, owner 2026-08-30) DELETED THE
        // NOTICE that used to ride this branch. The read is not ALLOWED to
        // capture the tones — revealing them needs a TYPE FLIP, which R3 forbids
        // the read campaign — so their absence is the design, not an incident of
        // this write, and reporting it on every write of a non-`fsk-v` preset
        // made a routine clone read as six warnings: "we aren't capturing it on
        // read, so we shouldn't whine about it on write". Nothing about WHAT IS
        // SENT changed; only the line about it is gone.
        return Try($"modem preset {preset.Number}",
            () => _s.Modem.ProgramPreset(preset.Number, name, type, dataMode, baud,
                interleave, mark, space, preset.Enabled));
    }

    /// <summary>
    /// F9 — the <c>HOP&gt;</c> preset write (7-9). The file's row is the SHORT
    /// listing line (<c>DAT9 ASYNC REMOTE BAUD 300</c>): name, the two mode
    /// words, and a baud from the three-value HOP vocabulary.
    ///
    /// <para><b>The validation idiom is the SSB path's, deliberately</b>
    /// (<c>CloneModemPreset.Fields</c> is <c>Bounded</c> in
    /// <c>CloneFileValidation</c>): a row is RE-PARSED here, at the write, and
    /// one that does not re-parse is REPORTED PER PRESET and not written —
    /// rather than refused at LOAD. A hand-edited <c>BAUD 1200</c> is exactly
    /// that case: the radio would SILENTLY ignore it (P5c) and echo the old
    /// value, so refusing it here is the only place the operator can be told
    /// the truth, and refusing the whole FILE for one unwritable preset row
    /// would be a heavier answer than the domain's own contract asks for.</para>
    /// </summary>
    private bool WriteHopPreset(CloneModemPreset preset)
    {
        var tokens = preset.Fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? name = tokens.Length > 0 ? tokens[0] : null;
        int mode = Array.FindIndex(tokens, t => string.Equals(t, "ASYNC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "SYNC", StringComparison.OrdinalIgnoreCase));
        SyncMode? sync = mode >= 0 ? Wire.ParseSyncMode(tokens[mode]) : null;
        DataMode? port = mode >= 0 && mode + 1 < tokens.Length ? Wire.ParseDataMode(tokens[mode + 1]) : null;
        string? baud = ModemPresetRow.TokenAfter(tokens, "BAUD");

        if (name is null || sync is null || port is null
            || baud is null || !Wire.HopModemBauds.Contains(baud))
        {
            _summary.Add($"Modem preset {preset.Number}: the file's row is not one this app can re-send, "
                + "so the preset was not written.");
            return false;
        }

        return Try($"modem preset {preset.Number}",
            () => _s.Modem.ProgramHopPreset(
                preset.Number, name, sync.Value, port.Value, baud, preset.Enabled));
    }

    private void WriteHopNet(CloneHopNet net)
    {
        // Order is protocol.md's: type BEFORE the hopset, and the net id
        // before generation has anything to generate from.
        if (net.Type is { Length: > 0 } type && Wire.ParseHopType(type) is { } hopType)
            Try($"HOP net {net.Number} type", () => _hop.ProgramHopType(net.Number, hopType));
        else Unwritable($"HOP net {net.Number} type", net.Type ?? "none");

        if (net.NetId is { Length: > 0 } netId)
            Try($"HOP net {net.Number} net ID", () => _hop.ProgramNetId(net.Number, netId));

        switch (net.Type?.ToUpperInvariant())
        {
            case "NB" when net.CenterKHz is { Length: > 0 } centre:
                Try($"HOP net {net.Number} centre", () => _hop.ProgramNarrowbandHopset(net.Number, centre));
                break;
            case "WB" when net.LowKHz is { Length: > 0 } low && net.HighKHz is { Length: > 0 } high:
                Try($"HOP net {net.Number} band", () => _hop.ProgramWidebandHopset(net.Number, low, high));
                break;
            case "LIST" when net.ListFrequencies.Count > 0:
                Try($"HOP net {net.Number} frequencies",
                    () => _hop.ProgramHopList(net.Number, [.. net.ListFrequencies]));
                break;
        }
    }

    /// <summary>The exclusion wire takes 8-DIGIT Hz while the listing prints
    /// kHz — the conversion asserts exactly eight digits before anything is
    /// sent (the 5-digit rule's sibling).</summary>
    internal static string? KHzToHz(string kHz)
    {
        var digits = kHz.Trim();
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        if (!long.TryParse(digits, NumberStyles.Integer, Inv, out long value)) return null;
        long hz = value * 1000;
        var text = hz.ToString("D8", Inv);
        return text.Length == 8 ? text : null;
    }

    // ---- D3: THE GATED-LEG FAULT POLICY ------------------------------------

    /// <summary>What a gated operation's outcome means for the LEG it belongs
    /// to (plan-clone-write-structural.md D3 / §5.4).</summary>
    private enum GateVerdict
    {
        /// <summary>Whatever happened, the next row is still worth trying —
        /// including a refusal, which the operator still wants the rest of the
        /// fill after.</summary>
        Continue,
        /// <summary>The gate reported a standing sentinel DEBT after its own
        /// in-gate retry. This leg is abandoned; the CAMPAIGN carries on.</summary>
        AbandonLeg,
        /// <summary>The campaign itself cannot continue (the session went).</summary>
        Stop,
    }

    /// <summary>One row of a gated leg: the progress text, the title the report
    /// uses, the single write, and the display re-read.</summary>
    private sealed record GatedOperation(string Status, string What, Action Write, Func<long> ClosingRead);

    /// <summary>The manifest domain names the two gated legs verify under —
    /// the keys <see cref="_notAttempted"/> holds, and the same strings
    /// <see cref="CloneFile.ManifestDomains"/> uses, so the verify suppression
    /// is keyed structurally rather than by matching a report sentence.</summary>
    private const string BookDomain = "address book";

    private const string GroupDomain = "channel groups";

    /// <summary>Domains whose WRITE LEG was abandoned for sentinel debt (D3).
    /// The verify skips their per-row comparison and says so once, instead of
    /// producing tens of expected diffs for rows nothing ever sent.</summary>
    private readonly HashSet<string> _notAttempted = new(StringComparer.Ordinal);

    /// <summary>
    /// ONE GATED LEG, under D3's fault policy.
    ///
    /// <para>The FIRST operation whose outcome is <c>Faulted</c> with
    /// <see cref="AleProgrammingFaultKind.SentinelDebt"/> — i.e. after the
    /// gate's OWN in-gate retry has already failed to settle it — abandons the
    /// leg with exactly ONE summary line naming the row it stopped at and how
    /// many operations went unattempted. Nothing else changes: a refusal, a
    /// queue-busy fault, a Core validation throw and a gate timeout all keep
    /// their per-row handling exactly as they had it.</para>
    ///
    /// <para><b>Why abandoning is right and cascading is not</b> (the
    /// 2026-08-28 field failure): one debt used to fault all 32 book rows, so
    /// the operator read 32 identical lines describing ONE condition, and the
    /// campaign spent 32 gate brackets discovering it again. The leg's
    /// remaining rows cannot succeed while the accounting is out, and the
    /// verify names what is missing either way.</para>
    ///
    /// <para>Returning TRUE for an abandoned leg is deliberate (§5.4d): an
    /// abandoned leg abandons only ITSELF, so the groups and schedule legs
    /// still run after an abandoned book.</para>
    /// </summary>
    private async Task<bool> RunGatedLegAsync(
        IReadOnlyList<GatedOperation> operations, string leg, string domain, string label, string noun)
    {
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            Status(operation.Status);
            var verdict = await GatedAsync(operation.Write, operation.ClosingRead, operation.What)
                .ConfigureAwait(true);
            if (verdict == GateVerdict.Stop) return false;
            if (verdict != GateVerdict.AbandonLeg) continue;

            // N counts the operations STRICTLY AFTER the failing row: the
            // failing one is named, and "this and the remaining N" covers it.
            _summary.Add($"{label}: the radio's sentinel accounting did not settle at "
                + $"'{operation.What}' — this and the remaining {operations.Count - i - 1} "
                + $"{noun} operations were not attempted.");
            _notAttempted.Add(domain);
            return true;
        }
        return true;
    }

    /// <summary>Run one write through the ALE programming gate and collect its
    /// attributed outcome. A refusal is NEVER fatal: it is recorded and the
    /// campaign carries on, because the operator wants the rest of the fill.
    /// <para>The gate runs in CAMPAIGN MODE (D3): a debt at the opening barrier
    /// buys ONE in-gate settle-and-retry before it becomes a fault, because a
    /// campaign never empties Core's ping queue between operations and so can
    /// never get the clean press the single-press rule assumes.</para>
    /// <para>The DEBT fault is recognised by its TYPED
    /// <see cref="AleProgrammingFaultKind"/> and never by its sentence
    /// (invariant I-9): the wording is scheduled to be trimmed, and a consumer
    /// keyed on prose would break into the wrong behaviour rather than into a
    /// failing build.</para></summary>
    private async Task<GateVerdict> GatedAsync(Action write, Func<long> closingRead, string what)
    {
        var tcs = new TaskCompletionSource<AleProgrammingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_s.Ale.Programming.TryRun(write, closingRead, o => tcs.TrySetResult(o), out var busy, campaign: true))
        {
            _summary.Add($"{Cap(what)}: not written — {busy}.");
            return GateVerdict.Continue;
        }
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(GateTimeoutMs)).ConfigureAwait(true);
        if (finished != tcs.Task)
        {
            _summary.Add($"{Cap(what)}: the radio never answered, so it is unverified.");
            return Verdict(_session.Phase == SessionPhase.Ready);
        }
        var outcome = tcs.Task.Result;
        switch (outcome.Result)
        {
            case AleProgrammingResult.Accepted:
                return GateVerdict.Continue;
            case AleProgrammingResult.Refused:
                _summary.Add($"{Cap(what)}: the radio refused it — "
                    + AleRefusalVocabulary.Describe(outcome.Detail));
                return GateVerdict.Continue;
            default:
                // The ONE new branch, and the ONLY one keyed on the fault kind:
                // the leg's own line is written by RunGatedLegAsync, so nothing
                // per-row is added here.
                if (outcome.Kind == AleProgrammingFaultKind.SentinelDebt) return GateVerdict.AbandonLeg;
                _summary.Add($"{Cap(what)}: not written — {outcome.Detail ?? "the radio did not confirm it"}.");
                return Verdict(_session.Phase == SessionPhase.Ready);
        }
    }

    private static GateVerdict Verdict(bool canContinue)
        => canContinue ? GateVerdict.Continue : GateVerdict.Stop;

    /// <summary>D3's verify half: exactly one line per abandoned domain, in
    /// place of the tens of "the radio does not hold it" diffs the comparison
    /// would otherwise produce for rows nothing ever sent. The
    /// <c>_explainedDomains</c> idiom, applied to the write side.</summary>
    private void NoteUnattemptedDomains()
    {
        foreach (var (domain, label) in new[] { (BookDomain, "ALE book"), (GroupDomain, "Channel groups") })
            if (_notAttempted.Contains(domain))
                _summary.Add($"{label}: not compared — the write did not attempt it.");
    }

    private bool Try(string what, Action send)
    {
        try
        {
            send();
            return true;
        }
        catch (ArgumentException ex)
        {
            _summary.Add($"{Cap(what)}: not written — {ex.Message}");
            return false;
        }
    }

    /// <summary>Report a NOTICE — a line the operator must see that does not
    /// make the campaign unclean. See <see cref="_notices"/>.</summary>
    private void Notice(string text)
    {
        _summary.Add(text);
        _notices++;
    }

    private void Unwritable(string what, string value)
        => _summary.Add($"{Cap(what)}: the file's value '{value}' is not one this radio accepts, "
            + "so it was not written.");

    // ---- campaign plumbing --------------------------------------------------

    /// <summary>
    /// Wait for a domain's OWN sentinel-scoped operation to complete, and
    /// report whether it COMMITTED.
    ///
    /// <para><b>Why this exists, and why a trailing <c>BAT ST</c> will not do</b>
    /// (P6 audit round 1, BLOCKER): a domain with its own completion idiom
    /// answers a question no other sentinel can. If the book operation's
    /// sentinel times out, Core records the fault and PRESERVES THE PRIOR
    /// MIRROR — and that timeout is exactly what dispatches the next queued
    /// ping. A trailing <c>BAT ST</c> of ours would then answer perfectly
    /// happily, and the campaign would serialize the STALE mirror as
    /// <c>Read</c>, pass the write preflight, and later ERASE a radio to
    /// replay yesterday's fill onto it. So a leg with an operation ID is
    /// judged by ITS OWN completion record, matched by id, and by nothing
    /// else — which is what §9A's leg-completion contract says: the domain's
    /// existing idiom governs where one exists, and the trailing-sentinel form
    /// is only for legs WITHOUT one.</para>
    /// </summary>
    private async Task<bool> AwaitReadAsync(long readId, Func<AleReadCompletion> completion, string leg)
    {
        long deadline = Environment.TickCount64 + ReadCompletionTimeoutMs;
        while (completion().ReadId < readId)
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _summary.Add($"{Cap(leg)}: the session dropped before the radio answered this step.");
                return false;
            }
            if (Environment.TickCount64 > deadline)
            {
                _summary.Add($"{Cap(leg)}: the radio never finished this step.");
                return false;
            }
            await Task.Delay(ReadPollMs).ConfigureAwait(true);
        }

        var record = completion();
        if (record.ReadId > readId)
        {
            // Another operation's completion overtook ours. Nothing here can
            // say whether OURS committed, so it is a fault, not a guess.
            _summary.Add($"{Cap(leg)}: the radio's answers arrived out of order, so this step is unverified.");
            return false;
        }
        if (!record.Answered)
        {
            _summary.Add($"{Cap(leg)}: the radio stopped answering during this step.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// WAIT FOR THE <c>DI 0 99</c> DUMP TO FINISH (round 17 F6) — the channel
    /// leg's own barrier, sitting between its sentinel and the S4 whole-set
    /// check. See the citation at the call site for the captured mechanism.
    ///
    /// <para><b>It returns no verdict on purpose.</b> The judgment is still S4's
    /// whole-set check, byte-for-byte as round 16 wrote it: this only decides
    /// WHEN that check gets to look. A second opinion here would be a second
    /// place to keep the completeness rule.</para>
    ///
    /// <para><b>Three ways out</b>, and the ONLY one that is not a give-up is
    /// the first: the reported set is {0..99} — every slot, which is what
    /// <c>DI 0 99</c> always prints (protocol.md "There is no 'unprogrammed
    /// channel' shape"; P17 record 6 carries exactly 100 rows). Otherwise the
    /// QUIET WINDOW expires — the set has not GROWN for
    /// <see cref="ChannelDumpQuietMs"/>, so no more rows are coming — or the
    /// HARD CAP does, which bounds a radio that dribbles rows forever and would
    /// otherwise reset the quiet window for as long as it liked. A session that
    /// drops stops the wait immediately: the legs after this one abort on their
    /// own drop guards, and the S4 line below tells the truth about what did
    /// arrive.</para>
    ///
    /// <para><b>THE QUIET WINDOW RE-READS AT THE MOMENT OF DECIDING</b> (audit
    /// round 1, MAJOR). The rows are parsed on the PORT thread, so the set can
    /// grow at any instant — including between this loop's snapshot and the
    /// comparison of the clock against the deadline. The first shape gave up on
    /// that stale snapshot, which is the very failure the barrier exists to
    /// prevent: a dump that HAD resumed, abandoned because the evidence was one
    /// moment old. So expiry is never decided on the snapshot. A FRESH read is
    /// taken at the decision, and the leg gives up only if THAT still shows no
    /// growth since the last-growth epoch — growth found there resets the
    /// deadline and the wait continues, exactly as growth found at the top of a
    /// poll does.</para>
    ///
    /// <para>The HARD CAP is deliberately NOT re-read: it is a wall-clock bound
    /// on the whole wait, not a statement about the rows, and a re-read there
    /// would let a dribbling radio postpone it forever — which is the one thing
    /// the cap exists to stop.</para>
    ///
    /// <para>The poll runs on the campaign's own context
    /// (<c>ConfigureAwait(true)</c>, like every other wait in this file) — no
    /// new threading, and the mirror is read where every other leg reads
    /// it.</para>
    /// </summary>
    private async Task AwaitChannelDumpAsync()
    {
        int grown = ReportedSlots().Count;
        long quietDeadline = Environment.TickCount64 + ChannelDumpQuietMs;
        long hardDeadline = Environment.TickCount64 + ChannelDumpTimeoutMs;

        while (true)
        {
            var reported = ReportedSlots();
            if (reported.SetEquals(Enumerable.Range(0, 100))) return;
            if (reported.Count > grown)
            {
                grown = reported.Count;
                quietDeadline = Environment.TickCount64 + ChannelDumpQuietMs;
            }
            if (_session.Phase != SessionPhase.Ready) return;

            DumpPollObserved?.Invoke();

            if (Environment.TickCount64 > hardDeadline) return;
            if (Environment.TickCount64 > quietDeadline)
            {
                // THE FRESH READ. Anything the port parsed while this iteration
                // was running counts, and it counts HERE rather than a poll too
                // late to matter.
                var fresh = ReportedSlots();
                if (fresh.SetEquals(Enumerable.Range(0, 100))) return;
                if (fresh.Count <= grown) return;          // really quiet: give up
                grown = fresh.Count;
                quietDeadline = Environment.TickCount64 + ChannelDumpQuietMs;
            }
            await Task.Delay(ChannelDumpPollMs).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// TEST SEAM (audit round 1) — called by <see cref="AwaitChannelDumpAsync"/>
    /// between reading the mirror and deciding whether a deadline expired: the
    /// exact gap in which a row parsed on the PORT thread used to be missed.
    /// <para>Null in production, and nothing but the race pin may set it. It
    /// exists because that gap is microseconds wide on the wall clock — a test
    /// that tried to hit it by sleeping would be racing, not pinning.</para>
    /// </summary>
    internal Action? DumpPollObserved { get; set; }

    /// <summary>The slots the radio has reported this dump — the same set S4
    /// judges, read from the same mirror.</summary>
    private HashSet<int> ReportedSlots() => [.. _channel.Channels.Select(c => c.Number)];

    /// <summary>The trailing <c>BAT ST</c> that bounds a leg WITHOUT a
    /// completion idiom of its own (§9A). FALSE means the radio never answered
    /// it — the leg is FAULTED, and the caller marks the domain rather than
    /// publishing a silently empty one.</summary>
    private Task<bool> SentinelAsync(string leg)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _radio.Ping(answered =>
        {
            if (!answered) _summary.Add($"{Cap(leg)}: the radio stopped answering during this step.");
            tcs.TrySetResult(answered);
        }, SentinelTimeoutMs);
        return tcs.Task;
    }

    /// <summary>
    /// Put the radio at the prompt a leg needs. A null <paramref name="mode"/>
    /// only checks the session is still there. Returns false when the campaign
    /// must abort.
    ///
    /// <para><b>THE MODE GATE WAITS FOR THE MODE, NOT FOR A SENTINEL</b>
    /// (round 14 F1 — the T2 field failure,
    /// <c>bench/transcripts/field-clone-console-20260820-1738.txt</c>). The
    /// old shape sent the mode command and then ran ONE closing sentinel,
    /// reading the confirmed mode when that sentinel answered. On the bench
    /// that works, because the demo and the dummy-load rig answer a mode
    /// switch with the new prompt immediately. <b>On a live rig it loses a
    /// race it cannot win.</b> The radio accepts <c>HO</c>, starts its entry
    /// lifecycle (generate → tune, twice in the capture, both ending
    /// <c>TUNE FAULT</c>), answers the <c>BAT ST</c> queued behind the switch
    /// AT THE OLD PROMPT, and only emits <c>HOP&gt;</c> when the lifecycle
    /// finishes — six seconds later. Both field attempts answered the
    /// sentinel first (17:38:05.590 vs the prompt at :08.522; 17:39:32.811 vs
    /// the prompt at :38.806), so both aborted with "did not confirm the HOP
    /// prompt" and the campaign sent nothing more. That is exactly what the
    /// owner reported.</para>
    ///
    /// <para>So the gate now WAITS ON THE MODE SURFACE for
    /// <c>IsConfirmed &amp;&amp; Value == wanted</c>, on the radio's OWN
    /// mode-change budget (<see cref="Prc138Radio.ModeChangeTimeoutMs"/> — one
    /// budget, not a second invented constant), and only THEN runs the
    /// sentinel to re-establish the prompt gate before the leg's first
    /// command. Generation and tune lines are expected traffic, never errors,
    /// and the honest abort message is reserved for the full budget expiring.
    /// This is the ONE confirmation-gated wait in the campaign (plan §3.4);
    /// everything else still sequences by ordered enqueue.</para>
    /// </summary>
    private async Task<bool> AtPromptAsync(OperatingMode? mode, string leg)
    {
        if (_session.Phase != SessionPhase.Ready)
        {
            _summary.Add($"{Cap(leg)}: the session dropped, so this operation stopped here.");
            return false;
        }
        // D8 (§5.4c): the occupancy bookkeeping is sampled at EVERY leg gate,
        // before anything decides. A confirmed mode that is not ALE means the
        // previous ALE occupancy has ended and the next ALE leg owes a stop.
        NoteOccupancyBoundary();
        if (mode is not { } wanted) return true;
        if (_mode.Mode.IsConfirmed && _mode.Mode.Value == wanted)
            // ALREADY-ALE (the funnel's early return) — the "on ALE mode" half
            // of the owner's sentence: `ST` → stop sentinel → leg traffic.
            return wanted != OperatingMode.Ale
                || await StopScanForOccupancyAsync(leg).ConfigureAwait(true);

        Status($"Switching to {wanted.ToString().ToUpperInvariant()}…");
        _mode.Select(wanted);

        if (!await AwaitModeAsync(wanted).ConfigureAwait(true))
            return NoPrompt(wanted, leg);

        // The confirmation is an ASYNC prompt and the entry lifecycle can still
        // be draining lines behind it, so the leg's first command still goes out
        // behind a sentinel — the same bracket every other leg gets, and JUDGED
        // like every other leg's (audit round 1, MAJOR): a barrier whose result
        // is discarded is not a barrier. FALSE means the radio stopped answering
        // and the leg's first command would go out blind; SentinelAsync has
        // already written the honest line, so this only stops.
        if (!await SentinelAsync(leg).ConfigureAwait(true)) return false;

        if (!(_mode.Mode.IsConfirmed && _mode.Mode.Value == wanted)) return NoPrompt(wanted, leg);

        // ENTERED ALE (D8): the stop goes out AFTER the navigation sentinel
        // above and BEFORE the leg's first command — `ALE` → mode confirm →
        // navigation sentinel → `ST` → stop sentinel → leg traffic.
        return wanted != OperatingMode.Ale
            || await StopScanForOccupancyAsync(leg).ConfigureAwait(true);
    }

    /// <summary>The mode gate's abort: the session going away is named as
    /// itself, and everything else is the honest prompt line.</summary>
    private bool NoPrompt(OperatingMode wanted, string leg)
    {
        _summary.Add(_session.Phase != SessionPhase.Ready
            ? $"{Cap(leg)}: the session dropped, so this operation stopped here."
            : $"{Cap(leg)}: the radio did not confirm the {wanted.ToString().ToUpperInvariant()} prompt, "
                + "so this operation stopped here.");
        return false;
    }

    /// <summary>Wait for the mode surface to CONFIRM <paramref name="wanted"/>.
    /// The wake-up is the surface's own <c>Changed</c> event; the poll around
    /// it is only the session-drop and deadline guard, exactly as
    /// <see cref="AwaitReadAsync"/> guards its completion record. The budget is
    /// the radio's, read at wait time so a test hook moves both together.</summary>
    private async Task<bool> AwaitModeAsync(OperatingMode wanted)
    {
        bool Arrived() => _mode.Mode.IsConfirmed && _mode.Mode.Value == wanted;
        if (Arrived()) return true;

        var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e) { if (Arrived()) confirmed.TrySetResult(true); }

        _mode.Changed += OnChanged;
        try
        {
            // Re-checked AFTER subscribing: the prompt can land in the gap
            // between the first check and the subscription.
            OnChanged(this, EventArgs.Empty);
            long deadline = Environment.TickCount64 + _radio.ModeChangeTimeoutMs;
            while (!confirmed.Task.IsCompleted)
            {
                if (_session.Phase != SessionPhase.Ready) return false;
                if (Environment.TickCount64 > deadline) return false;
                await Task.WhenAny(confirmed.Task, Task.Delay(ReadPollMs)).ConfigureAwait(true);
            }
            return true;
        }
        finally { _mode.Changed -= OnChanged; }
    }

    // ---- D8: SCAN DOCTRINE v2 (plan-clone-write-structural.md §5.4c) --------

    /// <summary>The clone summary's two scan lines — prose, no wire tokens
    /// (constitution §3.2); the raw commands stay in the Console. UNCHANGED as
    /// strings by D8; what moved is WHEN they fire (§5.4c, "Notices").</summary>
    public const string ScanStoppedNotice = "Stopped the scan for programming.";

    public const string ScanRestartedNotice = "Restarted the scan.";

    /// <summary>
    /// THE SCAN CONTEXT — one owner for everything D8 remembers, created and
    /// reset ONLY by the two public campaign entries
    /// (<see cref="ReadAsync"/>, <see cref="WriteAsync"/>).
    ///
    /// <para><b>Why not loose fields</b> (critic c2p1 F2). The write's nested
    /// VERIFY is itself a read campaign, and loose <c>_foundLinkState</c> /
    /// <c>_scanStopped</c> fields would let it re-snapshot a wiped radio's
    /// mirror and destroy the outer write's restart licence half-way through.
    /// The verify SHARES this object: it never re-snapshots (its
    /// <see cref="StartPending"/> is already false), it never restarts (it runs
    /// no closing-restore funnel), and its own ALE occupancies still get their
    /// stops through the same <see cref="OccupancyStopped"/> latch.</para>
    /// </summary>
    private sealed class ScanContext
    {
        /// <summary>Does this campaign still owe the START SEQUENCE — the
        /// read-only discovery sentinel, the found snapshot, and the stop —
        /// before its first leg? TRUE for a standalone READ; FALSE for a WRITE
        /// (D8's one exemption: `ZERO` is the write's first wire command and
        /// the wipe is the stop) and therefore FALSE for the verify that
        /// inherits the write's context.</summary>
        public bool StartPending;

        /// <summary>What the ALE link mirror said when the campaign FOUND the
        /// radio — the RESTART LICENCE, and nothing else. D8 consults the
        /// mirror for NOTHING on the stop side.</summary>
        public Confirmed<AleLinkState> FoundLinkState;

        /// <summary>Was the radio found SCANNING? The licence, and the
        /// condition on the stopped NOTICE: an `ST` sent into a radio that was
        /// not scanning stopped nothing, and claiming otherwise in the summary
        /// would be a sentence about an act, not an outcome.</summary>
        public bool FoundScanning =>
            FoundLinkState.IsConfirmed && FoundLinkState.Value == AleLinkState.Scanning;

        /// <summary>True once this campaign's own <c>ST</c> has gone out, so
        /// the restart cannot fire without one.</summary>
        public bool Stopped;

        /// <summary>Has the CURRENT ALE occupancy already had its stop? Cleared
        /// whenever the confirmed mode is observed to be anything but ALE — the
        /// occupancy is a run of consecutive ALE legs with no mode change
        /// between them, so the dedup is keyed off the mode surface's confirmed
        /// transitions and never off a leg count.</summary>
        public bool OccupancyStopped;

        /// <summary>Each notice appears AT MOST ONCE per campaign.</summary>
        public bool StoppedNoticeShown;

        public bool RestartNoticeShown;

        /// <summary>The restart is ONE ATTEMPT at the campaign's true end.</summary>
        public bool RestartAttempted;
    }

    /// <summary>The campaign in progress's scan state. Never null so no path
    /// can silently opt out of D8; replaced wholesale at each public entry.</summary>
    private ScanContext _scan = new();

    /// <summary>Has leg 2's <c>ZERO</c> gone out this campaign? The write's
    /// closing-restore funnel's only condition besides a live session (A-12):
    /// before the wipe the campaign has touched nothing and owes nothing.</summary>
    private bool _wiped;

    /// <summary>
    /// SNAPSHOT THE FOUND SCAN STATE — the FIRST thing the write campaign does,
    /// before leg 2's <c>ZERO</c>. It costs NO WIRE (a mirror read), which is
    /// what lets the write keep D8's pre-zeroize exemption: the licence is
    /// taken and `ZERO` is still the campaign's first wire command.
    ///
    /// <para><b>Why here and nowhere later.</b> The zeroize boundary resets
    /// every mirror, loudly and deliberately (<c>RadioState.ResetAfterZeroize</c>)
    /// — the ALE link state with them. Read at the ALE write leg the mirror is
    /// therefore ALWAYS unconfirmed, and "restore-to-found" would be a branch
    /// that could never run. The found state is the one the campaign inherited,
    /// and this is the last moment it exists.</para>
    /// </summary>
    private void NoteFoundScanState() => _scan.FoundLinkState = _s.Ale.LinkState;

    /// <summary>
    /// THE CAMPAIGN-START SEQUENCE (D8 EXTENDED, owner 2026-08-29): the stop
    /// PRECEDES the found-state snapshot of the operating channel.
    ///
    /// <para><b>Why.</b> A running scan OWNS the operating channel, so a read
    /// campaign that snapshots first captures a scan DWELL, and its own closing
    /// restore then faithfully puts back a number the operator never chose
    /// (2026-08-29 field console: the restored <c>CH 11</c> confirmed twice,
    /// then the final ALE entry's auto-resumed scan moved the radio to
    /// <c>CHAN 21</c> — the restore executed correctly against a moving
    /// target).</para>
    ///
    /// <para><b>THE BRANCH ORDER — the mode mirror decides, and the discovery
    /// sentinel is the LAST RESORT</b> (audit round 1, BLOCKER). Leg 1 requests
    /// no mode, so <see cref="AtPromptAsync"/>'s funnel check alone would not
    /// fire until the first ALE-REQUESTING leg — after the operating state was
    /// already read. What this method adds is the ALE case only, and it must
    /// add NOTHING anywhere else:</para>
    /// <list type="number">
    /// <item><b>Confirmed SSB or HOP → nothing at all.</b> The plan is explicit
    /// ("a campaign found in SSB/HOP is untouched at start") and the funnel
    /// covers its later ALE entries. A discovery sentinel here would be
    /// PROHIBITED extra wire on the commonest start of all — and worse than
    /// wasteful: it moves the campaign's first timeout budget, so a marginal
    /// radio would abort at a different step than it used to. This branch keeps
    /// the start byte-identical to the pre-D8 campaign.</item>
    /// <item><b>Confirmed ALE → straight to the snapshot and the stop.</b> The
    /// mirror has already answered the only question the discovery sentinel
    /// exists to ask, so asking it again would be the same prohibited extra
    /// wire. The licence is snapshotted BEFORE the stop destroys the
    /// evidence.</item>
    /// <item><b>UNCONFIRMED → ONE READ-ONLY discovery sentinel, then branch on
    /// what it confirmed.</b> Its prompt lines move the mode mirror, which
    /// resolves "found in ALE with an unconfirmed mirror" without any new wire
    /// vocabulary; a dead radio aborts here on the existing honest line. This
    /// is the ONLY branch that spends a sentinel, and it spends it on a
    /// campaign that had no other way to know where it was standing.</item>
    /// </list>
    ///
    /// <para>The WRITE campaign never runs this: its <c>StartPending</c> is
    /// false (the pre-zeroize exemption), and the verify inherits that.</para>
    /// </summary>
    private async Task<bool> RunCampaignStartScanSequenceAsync(string leg)
    {
        if (!_scan.StartPending) return true;
        _scan.StartPending = false;

        var mode = _mode.Mode;
        if (mode.IsConfirmed && mode.Value != OperatingMode.Ale) return true;

        if (!mode.IsConfirmed)
        {
            if (!await SentinelAsync(leg).ConfigureAwait(true)) return false;
            mode = _mode.Mode;
            if (!mode.IsConfirmed || mode.Value != OperatingMode.Ale) return true;
        }

        NoteFoundScanState();
        return await StopScanForOccupancyAsync(leg).ConfigureAwait(true);
    }

    /// <summary>
    /// THE OCCUPANCY STOP (D8, §5.4c) — <c>ST</c> plus its own JUDGED sentinel,
    /// once per ALE occupancy, before any of that occupancy's leg traffic.
    ///
    /// <para><b>UNCONDITIONAL</b> (owner ruling 2026-08-29, resolving A-D8).
    /// No mirror read gates the send: the mirror is consulted for NOTHING on
    /// the stop side. R13(b)'s "the radio said it was Linked, so send nothing"
    /// branch is RETIRED for campaigns — the owner accepts that a campaign's
    /// <c>ST</c> terminates an in-progress exchange, because a clone campaign
    /// owns the radio for its duration. The one exemption is the write's
    /// PRE-ZEROIZE occupancy, and that one is structural: leg 2 asks
    /// <see cref="AtPromptAsync"/> for a NULL mode, so no funnel check runs and
    /// <c>ZERO</c> is still the write's first wire command from any prompt.</para>
    ///
    /// <para><b>JUDGED, and never silent.</b> A failed stop sentinel returns
    /// false, which takes the leg's existing abort path — the honest "stopped
    /// answering" line <see cref="SentinelAsync"/> has already written. No leg
    /// traffic goes out behind an unjudged stop.</para>
    ///
    /// <para><b>The notice</b> fires at most once per campaign, and only on a
    /// LICENSED stop (the radio was found scanning). An <c>ST</c> into a radio
    /// that was not scanning stopped nothing, and the summary reports outcomes,
    /// not acts; the command itself is in the Console either way. Repeat
    /// occupancy stops are Console-only wire traffic.</para>
    /// </summary>
    private async Task<bool> StopScanForOccupancyAsync(string leg)
    {
        if (_scan.OccupancyStopped) return true;
        _scan.OccupancyStopped = true;

        Status("Stopping the scan…");
        _s.Ale.Stop();
        _scan.Stopped = true;
        if (!await SentinelAsync(leg).ConfigureAwait(true)) return false;

        if (_scan.FoundScanning && !_scan.StoppedNoticeShown)
        {
            _scan.StoppedNoticeShown = true;
            Notice(ScanStoppedNotice);
        }
        return true;
    }

    /// <summary>THE OCCUPANCY BOUNDARY. An ALE occupancy is a maximal run of
    /// consecutive campaign legs at the ALE prompt with no intervening mode
    /// switch, so the dedup latch is cleared whenever the CONFIRMED mode is
    /// observed to be anything but ALE — including unconfirmed, where a fresh
    /// stop is the conservative answer. Sampled at every leg gate, which is the
    /// only place the latch is ever read.</summary>
    private void NoteOccupancyBoundary()
    {
        var mode = _mode.Mode;
        if (!mode.IsConfirmed || mode.Value != OperatingMode.Ale) _scan.OccupancyStopped = false;
    }

    /// <summary>
    /// THE RESTART — ONE ATTEMPT at the campaign's TRUE END (D8, §5.4c), from
    /// inside the closing-restore funnel and after the operating-state restore.
    ///
    /// <para><b>Why not at the end of the ALE write legs</b> (where round 14
    /// put it). The verify lap RE-ENTERS ALE after that point, and its
    /// entry-scoped auto-resume — or the restart itself — would put a scanning
    /// radio under the verify's own book reads. The funnel runs on every exit
    /// (A-12), so moving it here covers the abort paths by the same rule.</para>
    ///
    /// <para><b>The four attempt conditions</b>: a live session (the funnel's
    /// own guard), this campaign's licence (it stopped the scan AND found the
    /// radio scanning), a mode surface CONFIRMING ALE after the restore
    /// (<c>SCA</c> only ever goes out at <c>ALE&gt;</c>), and this point. A
    /// channel or net read-back disagreement does NOT block it — the scan owns
    /// the channel once running, and the funnel's own lines report the diff.
    /// The write ending in SSB/HOP simply fails the mode condition, which is
    /// the deliberate answer: the operator asked for the FILE's state.</para>
    ///
    /// <para><b>The notice reports an OUTCOME, never an attempt.</b> It fires
    /// only when the link mirror confirms <c>Scanning</c> inside the restart's
    /// own sentinel bracket. An <c>SCA</c> the radio refuses — an incomplete
    /// fill after an abandoned book leg — produces no notice and no summary
    /// line at all; the Console carries the refusal, and nothing is claimed
    /// that did not happen.</para>
    /// </summary>
    private async Task RestoreScanAsync(string leg)
    {
        if (_scan.RestartAttempted) return;
        if (!_scan.Stopped || !_scan.FoundScanning) return;
        if (_session.Phase != SessionPhase.Ready) return;
        var mode = _mode.Mode;
        if (!mode.IsConfirmed || mode.Value != OperatingMode.Ale) return;
        _scan.RestartAttempted = true;

        Status("Restarting the scan…");
        _s.Ale.StartScan();
        await SentinelAsync(leg).ConfigureAwait(true);

        var link = _s.Ale.LinkState;
        if (link.IsConfirmed && link.Value == AleLinkState.Scanning && !_scan.RestartNoticeShown)
        {
            _scan.RestartNoticeShown = true;
            Notice(ScanRestartedNotice);
        }
    }

    private bool Aborted(string leg)
    {
        Set(CloneState.Failed, $"Stopped at the {leg} step — see the summary.");
        return false;
    }

    private static CloneDomainState Mark(bool answered)
        => answered ? CloneDomainState.Read : CloneDomainState.Faulted;

    private static CloneAddress ToAddress(AleAddress a)
        => new() { Name = a.Address, Group = a.ChannelGroup, AssociatedSelf = a.AssociatedSelf };

    /// <summary>
    /// ONE PROMPT'S modem-preset leg (F9): the scoped targeted FIELD batch
    /// (<c>MODEM PRE n</c> per preset, ONE sentinel), then the bulk PRESENCE
    /// listing, then the rows for THAT BAND ONLY folded into the file.
    ///
    /// <para>The caller must already be standing at <paramref name="prompt"/> —
    /// the scope comes from the radio's own confirmed mode inside Core, so a
    /// mismatch between what this argument says and where the radio is would be
    /// a leg reading the wrong band. The <see cref="RadioState.Presence.Covers"/>
    /// check is what makes that structural rather than trusted: the listing has
    /// to have been committed AT this prompt's band before its enabled set is
    /// allowed to decide anything.</para>
    ///
    /// <para>The band FILTER matters as much as the read. The Core fields
    /// mirror is upsert-only and never clears (round 11 §8), so by the time the
    /// HOP leg runs it holds 0-6 as well — and 0-6's enabled flags came from
    /// the SSB listing, which the HOP listing has since replaced. Each leg
    /// therefore folds in only its own numbers, with the presence set that was
    /// read for them.</para>
    /// </summary>
    private async Task<bool> ReadModemPresetLegAsync(CloneFile file, OperatingMode prompt)
    {
        var (first, last) = ModemPresetScope.Range(prompt);
        bool fieldsOk = await AwaitReadAsync(
            _s.Modem.RefreshPresetFields(), () => _s.Modem.LastPresetRead, "modem presets").ConfigureAwait(true);
        bool presenceOk = await AwaitReadAsync(
            _s.Modem.QueryPresetPresence(), () => _s.Modem.LastPresetRead, "modem presets").ConfigureAwait(true);
        if (!fieldsOk || !presenceOk) return false;

        var presence = _s.Modem.PresetPresence;
        if (!presence.Covers(prompt)) return false;

        var rows = file.ModemPresets.Where(p => p.Number < first || p.Number > last).ToList();
        foreach (var line in _s.Modem.Presets)
        {
            if (ParsePresetLine(line) is not { } parsed) continue;
            if (parsed.Number < first || parsed.Number > last) continue;
            rows.Add(new CloneModemPreset
            {
                Number = parsed.Number,
                Fields = parsed.Fields,
                Enabled = presence.Enabled.Contains(parsed.Number),
            });
        }
        file.ModemPresets = [.. rows.OrderBy(p => p.Number)];
        return true;
    }

    /// <summary>"0 SER  ASYNC DATA …" → (0, "SER  ASYNC DATA …").</summary>
    private static (int Number, string Fields)? ParsePresetLine(string line)
    {
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || !int.TryParse(token, NumberStyles.Integer, Inv, out int number)) return null;
        int after = line.IndexOf(token, StringComparison.Ordinal) + token.Length;
        return (number, line[after..].Trim());
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void Status(string text)
    {
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Set(CloneState state, string text)
    {
        State = state;
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
