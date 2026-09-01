using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Cloning;

/// <summary>How much of a domain the read campaign actually got. Three
/// states, never conflated — the same doctrine every mirror in this app
/// carries (invariant 6), carried into the FILE so a transient read fault can
/// never become destructive loss at write time.</summary>
public enum CloneDomainState
{
    /// <summary>Never read (a fresh file, or a leg the campaign skipped).</summary>
    Unread = 0,
    /// <summary>Read and committed — the rows below are what the radio said,
    /// INCLUDING "the radio said nothing", which is an empty list.</summary>
    Read = 1,
    /// <summary>The leg's closing sentinel never answered. The rows are
    /// whatever arrived and are NOT trustworthy; the write preflight refuses
    /// a file in this state and names the domain.</summary>
    Faulted = 2,
}

/// <summary>One address-book row (self or individual).</summary>
public sealed class CloneAddress
{
    public string Name { get; set; } = "";
    public int Group { get; set; }
    /// <summary>Null for a self. For an individual, the self it hangs off.</summary>
    public string? AssociatedSelf { get; set; }
}

/// <summary>One net, with its membership in INSERTION order (the radio's own
/// ordering rule). A BLANK <see cref="AssociatedSelf"/> is the
/// primary-deletion artifact — legal to READ, impossible to REPLAY.</summary>
public sealed class CloneNet
{
    public string Name { get; set; } = "";
    public int Group { get; set; }
    public string? AssociatedSelf { get; set; }
    public List<string> Members { get; set; } = [];
}

/// <summary>One scan channel group (0-9) and its channels, radio order.</summary>
public sealed class CloneChannelGroup
{
    public int Group { get; set; }
    public List<int> Channels { get; set; } = [];
}

/// <summary>One queued LQA schedule row, verbatim from the bare-EXCH
/// listing. <see cref="Kind"/> is "EXCHANGE" or "SOUND".</summary>
public sealed class CloneSchedule
{
    public string Kind { get; set; } = "";
    public string Address { get; set; } = "";
    public string Interval { get; set; } = "";
    public string Start { get; set; } = "";
}

/// <summary>One stored SSB channel, fields VERBATIM as the DI dump printed
/// them (the dump's own abbreviations are not re-mapped — ChannelSurface's
/// standing rule).</summary>
public sealed class CloneChannel
{
    public int Number { get; set; }
    public string RxFrequency { get; set; } = "";
    public string TxFrequency { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Agc { get; set; } = "";
    public string Bandwidth { get; set; } = "";
    public string RxOnly { get; set; } = "";

    /// <summary>
    /// Is this row the FACTORY DEFAULT (<see cref="Wire.DefaultChannel"/>) —
    /// the row a never-written slot prints and a ZEROIZE puts every slot back
    /// to? The one predicate behind D4: the read does not STORE such a row, the
    /// write does not SEND one, the elided-file rule refuses to hold one, and
    /// the verify supplies one for a slot the file omits.
    ///
    /// <para><b>A METHOD, not a property</b> — the
    /// <see cref="CloneModemPreset.NameToken"/> rule: a property would be
    /// serialized into the file as a derived second copy of what the six values
    /// already say, and walked by
    /// <see cref="CloneFileValidation.WalkFields(Type)"/> as if it were stored
    /// state. It is neither.</para>
    ///
    /// <para>CASE-INSENSITIVE, ordinal: the dump prints these tokens in upper
    /// case and that is what a read stores, but a hand-edited <c>usb</c> means
    /// the same slot and must not turn into ~90 pointless write sequences. No
    /// trimming — whitespace inside a stored value is a different value, and
    /// the rest of the file treats it that way.</para>
    /// </summary>
    public bool IsFactoryDefault()
    {
        var d = Wire.DefaultChannel;
        return Same(RxFrequency, d.RxFrequency) && Same(TxFrequency, d.TxFrequency)
            && Same(Mode, d.Mode) && Same(Agc, d.Agc)
            && Same(Bandwidth, d.Bandwidth) && Same(RxOnly, d.RxOnly);

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One HOP net (0-9). <see cref="Wiped"/> is the radio's own
/// reported-unprogrammed record (<c>NETID nn XXXXXXXX</c>) — replayed as a
/// bare <c>HOPSET n DEL</c> rather than as invented values.</summary>
public sealed class CloneHopNet
{
    public int Number { get; set; }
    public bool Wiped { get; set; }
    public string? NetId { get; set; }
    /// <summary>"NB" / "WB" / "LIST", or null when the radio reported none.</summary>
    public string? Type { get; set; }
    public string? CenterKHz { get; set; }
    public string? LowKHz { get; set; }
    public string? HighKHz { get; set; }
    public List<string> ListFrequencies { get; set; } = [];
}

/// <summary>One WB exclusion band slot (0-9), kHz as the listing prints it.</summary>
public sealed class CloneExcludeBand
{
    public int Band { get; set; }
    public string LowKHz { get; set; } = "";
    public string HighKHz { get; set; } = "";
}

/// <summary>One modem preset: the mirrored FIELDS row verbatim (minus the
/// leading number, which <see cref="Number"/> carries) plus the enabled flag
/// derived from the presence read — the only captured EN/DIS signal.</summary>
public sealed class CloneModemPreset
{
    public int Number { get; set; }
    public string Fields { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>
    /// The preset's NAME — the first token of the listing row, which is what
    /// the radio reports back when this preset is ENGAGED
    /// (<c>MODEM 1 T39</c>). Null when the row carries no tokens at all.
    ///
    /// <para>A METHOD rather than a property on purpose: a property would be
    /// serialized into the file as a second, derived copy of something the
    /// <see cref="Fields"/> row already says, and would be walked by the
    /// disposition enumeration as if it were stored state. It is neither — it
    /// is a projection, and the campaign's preset write reads the name the
    /// same way.</para>
    /// </summary>
    public string? NameToken()
        => Fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}

/// <summary>One stored TX message slot (0-9). A slot the listing did not
/// mention is ABSENT from the file, and the write campaign DELETES it on the
/// target — which is what lets the domain verify to exact equality.</summary>
public sealed class CloneTxMessage
{
    public int Slot { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>One manifest setting: the field key from
/// <see cref="CloneSettingsManifest"/> and its value in the file's storage
/// form. A setting whose mirror was unconfirmed is simply ABSENT — the file
/// never invents a value.</summary>
public sealed class CloneSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// One operator-lockout row, KEYED (family, section, item) — item names repeat
/// across sections, so nothing here is keyed by item alone (plan-clone-round12
/// §3, invariant 2).
///
/// <para>The three enum-valued cells are stored as the app's OWN ENUM NAMES,
/// the same rule <see cref="CloneFile.OperatingMode"/> follows: names
/// round-trip exactly, while the wire spellings differ between the report and
/// the set. Every one of them is parsed back with <c>Enum.IsDefined</c> at
/// load (plan §5), so an undefined numeric can never reach the write leg.</para>
/// </summary>
public sealed class CloneLockout
{
    /// <summary>"Program" or "Select" — <see cref="LockoutFamily"/>'s name.</summary>
    public string Family { get; set; } = "";
    /// <summary>"Ssb", "Hop" or "Eam" — <see cref="LockoutSection"/>'s name.</summary>
    public string Section { get; set; } = "";
    /// <summary>The item spelling, upper-case, from the closed inventory.</summary>
    public string Item { get; set; } = "";
    /// <summary>"Lock" or "Unlock" — <see cref="LockState"/>'s name.</summary>
    public string State { get; set; } = "";
}

/// <summary>
/// The operator-lockout DOMAIN: the read-state marker every domain carries,
/// plus the 22 keyed rows.
///
/// <para><b>Why it is its own object rather than two properties on
/// <see cref="CloneFile"/>.</b> R2 makes lockouts MANDATORY — a file without
/// them is malformed — and source-generated JSON makes an absent property
/// indistinguishable from a defaulted one. A NULLABLE object is
/// distinguishable BY CONSTRUCTION: absent ⇒ <c>null</c> ⇒ rejected naming
/// the domain; present-but-unread ⇒ an object carrying
/// <see cref="CloneDomainState.Unread"/>, which the write preflight refuses
/// for the ordinary reason. The two states can never be conflated.</para>
/// </summary>
public sealed class CloneLockouts
{
    public CloneDomainState State { get; set; }
    public List<CloneLockout> Rows { get; set; } = [];
}

/// <summary>Raised by <see cref="CloneFile.Load"/> when a file cannot be
/// trusted. The message NAMES the offender (plan §9A file hygiene).</summary>
public sealed class CloneFileFormatException(string message) : Exception(message);

/// <summary>
/// The clone file (plan round 11 §9A) — the radio's full state, versioned,
/// with a READ-STATE MARKER per domain.
///
/// <para><b>Serialization never invents values.</b> Every list holds exactly
/// what the radio reported; an unconfirmed setting is absent, an unreported
/// channel slot is absent, an unread domain is <see cref="CloneDomainState.Unread"/>.
/// That is what makes the write PREFLIGHT meaningful: a file with any
/// unread/faulted domain cannot be written, so a transient read fault never
/// becomes destructive loss on the target radio.</para>
///
/// <para><b>Identity matching everywhere is ORDINAL, uppercase-normalized</b>
/// (the radio's own lookup rule): a typed <c>cam</c> selects a stored
/// <c>CAM</c>.</para>
/// </summary>
public sealed class CloneFile
{
    /// <summary>The only version this app reads or writes.</summary>
    public const string CurrentVersion = "falconclone/1";

    public string Version { get; set; } = CurrentVersion;

    /// <summary>When the read campaign ran, round-trip UTC — informational
    /// only; nothing keys off it.</summary>
    public string? CapturedUtc { get; set; }

    // ---- Operating snapshot (captured FIRST, written LAST) -----------------

    public CloneDomainState OperatingState { get; set; }
    /// <summary>"Ssb" / "Ale" / "Hop" — the app's own enum name, not a wire
    /// token (the file is the app's format, and enum names round-trip).</summary>
    public string? OperatingMode { get; set; }
    public int? OperatingChannel { get; set; }
    public int? OperatingHopNet { get; set; }

    // ---- ALE fill ---------------------------------------------------------

    public CloneDomainState BookState { get; set; }
    /// <summary>Selfs in LISTING ORDER — index 0 is the PRIMARY.</summary>
    public List<CloneAddress> Selfs { get; set; } = [];
    public List<CloneAddress> Individuals { get; set; } = [];
    public List<CloneNet> Nets { get; set; } = [];

    public CloneDomainState GroupState { get; set; }
    public List<CloneChannelGroup> ChannelGroups { get; set; } = [];

    public CloneDomainState ScheduleState { get; set; }
    public List<CloneSchedule> Schedules { get; set; } = [];

    // ---- SSB / HOP / modem / messages / settings --------------------------

    public CloneDomainState ChannelState { get; set; }
    public List<CloneChannel> Channels { get; set; } = [];

    /// <summary>
    /// THE ELISION DISCRIMINATOR (plan-clone-write-structural.md D4/D6): does
    /// <see cref="Channels"/> deliberately omit the slots that hold
    /// <see cref="Wire.DefaultChannel"/>?
    ///
    /// <para><b>Why a marker and not a version bump.</b> The completeness rule
    /// below downgrades a <c>Read</c> channel domain that does not carry all
    /// 100 slots, because <c>DI 0 99</c> prints every slot and anything shorter
    /// is a dump that lost rows (round 17 F6 — the owner's 28-channel bench
    /// file). A SPARSE file is short for the opposite reason, and without a
    /// marker the two are indistinguishable. So:</para>
    /// <list type="bullet">
    /// <item><b>absent or false</b> (every file written before this round) —
    /// the 100-row rule applies EXACTLY as it always did, byte for byte;</item>
    /// <item><b>true</b> — the rule becomes: at most 100 rows, unique numbers
    /// 0-99, and NO row may equal the factory default, because an elided file
    /// carrying one is self-contradictory. The offender is named.</item>
    /// </list>
    /// <para>The version stays <see cref="CurrentVersion"/>: this is a
    /// backward-compatible FIELD addition, dispositioned through
    /// <c>CloneFileValidation</c>'s completeness pin like every other field.</para>
    /// </summary>
    public bool DefaultChannelsElided { get; set; }

    public CloneDomainState HopNetState { get; set; }
    public List<CloneHopNet> HopNets { get; set; } = [];

    public CloneDomainState ExcludeState { get; set; }
    public List<CloneExcludeBand> ExcludeBands { get; set; } = [];

    public CloneDomainState ModemState { get; set; }
    public List<CloneModemPreset> ModemPresets { get; set; } = [];

    public CloneDomainState MessageState { get; set; }
    public List<CloneTxMessage> Messages { get; set; } = [];

    public CloneDomainState SettingState { get; set; }
    public List<CloneSetting> Settings { get; set; } = [];

    /// <summary>
    /// The operator lockouts (clone round 12, owner ruling R2) — MANDATORY in
    /// <see cref="CurrentVersion"/>: always read, always written, always
    /// verified. Combined with "there are no old files" that means NO compat
    /// path and NO version bump: a file that does not carry this domain is
    /// MALFORMED, and <see cref="Load"/> says so naming it.
    ///
    /// <para>NULLABLE deliberately — see <see cref="CloneLockouts"/>.</para>
    /// </summary>
    public CloneLockouts? Lockouts { get; set; }

    /// <summary>The lockout domain's read state, with ABSENT reading as
    /// <see cref="CloneDomainState.Unread"/> so an in-memory graph that never
    /// went through <see cref="Load"/> still fails the write preflight rather
    /// than slipping past it.</summary>
    private CloneDomainState LockoutState => Lockouts?.State ?? CloneDomainState.Unread;

    /// <summary>Every domain the write PREFLIGHT requires, with the name the
    /// operator sees when it is missing. Closed manifest: a domain absent
    /// here would be writable from an unread file.</summary>
    public IReadOnlyList<(string Name, CloneDomainState State)> ManifestDomains =>
    [
        ("operating state", OperatingState),
        ("address book", BookState),
        ("channel groups", GroupState),
        ("LQA schedules", ScheduleState),
        ("SSB channels", ChannelState),
        ("HOP nets", HopNetState),
        ("exclusion bands", ExcludeState),
        ("modem presets", ModemState),
        ("stored messages", MessageState),
        ("settings", SettingState),
        ("operator lockouts", LockoutState),
    ];

    /// <summary>
    /// What <see cref="Load"/> had to say about this file beyond loading it —
    /// EMPTY for every file that needed nothing said (round 17 F6).
    ///
    /// <para>NOT SERIALIZED: it is a fact about THIS load, not a field of the
    /// format. A notice written into the file would be re-read as data and
    /// would outlive the condition that produced it.</para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> LoadNotices { get; private set; } = [];

    /// <summary>The domains that are NOT fully read — the write button's
    /// disable reason, in manifest order.</summary>
    public IReadOnlyList<string> IncompleteDomains =>
        [.. ManifestDomains.Where(d => d.State != CloneDomainState.Read).Select(d => d.Name)];

    /// <summary>Case-insensitive ordinal name lookup across ALL THREE kinds —
    /// radio names are globally unique, so this is the radio's own rule.</summary>
    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public string? Value(string settingKey)
    {
        foreach (var s in Settings)
            if (string.Equals(s.Key, settingKey, StringComparison.Ordinal)) return s.Value;
        return null;
    }

    // ---- Serialization ----------------------------------------------------

    public string Save() => JsonSerializer.Serialize(this, CloneJson.Default.CloneFile);

    /// <summary>
    /// Parse and VALIDATE. Rejects — naming the offender — an unknown version,
    /// a malformed row, or a cross-kind duplicate name. Nothing partially
    /// loads: either the whole file is trustworthy or none of it is.
    /// </summary>
    public static CloneFile Load(string json)
    {
        CloneFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CloneJson.Default.CloneFile);
        }
        catch (JsonException ex)
        {
            throw new CloneFileFormatException("This is not a clone file — " + ex.Message);
        }
        if (file is null) throw new CloneFileFormatException("This is not a clone file — it is empty.");

        if (!string.Equals(file.Version, CurrentVersion, StringComparison.Ordinal))
            throw new CloneFileFormatException(
                $"Unsupported clone-file version '{file.Version}' — this app reads {CurrentVersion}.");

        file.Validate();
        file.NoticeShortChannelDump();
        return file;
    }

    /// <summary>
    /// ROUND 17 F6, THE LOAD SIDE. A file written before the dump-completion
    /// fix can claim <see cref="CloneDomainState.Read"/> over a SHORT channel
    /// inventory: the campaign judged the dump at the moment the sentinel
    /// answered, which the radio does MID-DUMP for a heavy answer
    /// (bench/transcripts/r15-p1-wire-read-20260822-194203.jsonl — the
    /// sentinel answers after row 28 and the dump then completes to 100). The
    /// 8-22 <c>falconclone.json</c> on the owner's bench holds exactly 28
    /// channels and says Read.
    ///
    /// <para><c>DI 0 99</c> prints EVERY slot — a never-written one prints a
    /// default row (protocol.md, "There is no 'unprogrammed channel' shape") —
    /// so anything but 100 is a domain that was not wholly read, whatever the
    /// marker says. It is DOWNGRADED, which is exactly what the read campaign
    /// would have done, and the existing write preflight then refuses the file
    /// by name. Nothing is rejected: a short file still loads, still shows what
    /// it has, and says why it cannot be written.</para>
    ///
    /// <para>This is the ONE load rule of its kind (owner value ceiling, plan
    /// §6) — deliberately not the start of a validation program.</para>
    /// </summary>
    private void NoticeShortChannelDump()
    {
        // D6: an ELIDED file is short ON PURPOSE, and its own rule (in
        // Validate) is what holds it — this one would downgrade every clone
        // this app now writes.
        if (DefaultChannelsElided) return;
        if (ChannelState != CloneDomainState.Read || Channels.Count == 100) return;
        ChannelState = CloneDomainState.Faulted;
        LoadNotices =
        [
            .. LoadNotices,
            $"SSB channels: this file predates the dump-completion fix (only {Channels.Count} of 100 slots) "
                + "— re-read the radio.",
        ];
    }

    /// <summary>
    /// The SAME validation <see cref="Load"/> runs, callable on a file that
    /// did not come from disk.
    ///
    /// <para><b>Why it is exposed</b> (P6 audit round 1, BLOCKER): a file is
    /// only trustworthy where it entered — and the write campaign writes the
    /// TRANSFORMED file, which no load ever saw. An invalid graph reaching the
    /// wire surfaces mid-book, AFTER the ERASE, leaving a partially rewritten
    /// radio. So the write preflight re-runs this on the transform's OUTPUT,
    /// before the confirmation and before anything is sent: whatever produced
    /// the graph, and however future work produces it, an invalid one cannot
    /// get past.</para>
    /// </summary>
    internal void Validate()
    {
        // The read-state MARKERS first — an undefined one (JSON carries them as
        // numbers, so `"BookState": 99` deserializes happily) is the same r3
        // defect class as `OperatingMode: "99"`, and the domain it belongs to
        // is the only thing that can name it.
        foreach (var (name, state) in ManifestDomains)
            if (!Enum.IsDefined(state))
                throw new CloneFileFormatException(
                    $"Malformed file: the {name} read state '{(int)state}' is not one this app has.");

        foreach (var a in Selfs) ValidateName(a.Name, "self", a.Group, allowAssoc: false, a.AssociatedSelf);
        foreach (var a in Individuals) ValidateName(a.Name, "individual", a.Group, allowAssoc: true, a.AssociatedSelf);
        foreach (var n in Nets)
        {
            ValidateName(n.Name, "net", n.Group, allowAssoc: true, n.AssociatedSelf);
            foreach (var m in n.Members)
                if (string.IsNullOrWhiteSpace(m) || m.Length > 15)
                    throw new CloneFileFormatException(
                        $"Malformed row: net '{n.Name}' lists a member name that is empty or over 15 characters.");
        }

        // Names are GLOBAL across kinds on the radio, so a file holding the
        // same name twice could never be replayed — name the offender.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Selfs.Select(a => a.Name)
            .Concat(Individuals.Select(a => a.Name))
            .Concat(Nets.Select(n => n.Name)))
            if (!seen.Add(Normalize(name)))
                throw new CloneFileFormatException(
                    $"Duplicate address name '{name}' — radio names are unique across selfs, individuals and nets.");

        ValidateReferences();

        foreach (var g in ChannelGroups)
        {
            if (g.Group is < 0 or > 9)
                throw new CloneFileFormatException($"Malformed row: channel group {g.Group} is outside 0-9.");
            foreach (var c in g.Channels)
                if (c is < 0 or > 99)
                    throw new CloneFileFormatException(
                        $"Malformed row: channel group {g.Group} lists channel {c}, which is outside 0-99.");
        }
        RejectDuplicateKeys(ChannelGroups.Select(g => g.Group), "channel group");

        foreach (var s in Schedules)
        {
            if (s.Kind is not ("EXCHANGE" or "SOUND"))
                throw new CloneFileFormatException(
                    $"Malformed row: schedule kind '{s.Kind}' is neither EXCHANGE nor SOUND.");
            if (string.IsNullOrWhiteSpace(s.Address))
                throw new CloneFileFormatException("Malformed row: a schedule has no address.");
            // The interval and the start reach EXCH/SOU STA in leg 8 — AFTER
            // the erase. Core's own hh:mm validator THROWS on a bad one, and a
            // throw arriving there would abandon a half-rewritten radio, so the
            // shape is settled at the door instead.
            ValidateHhMm(s.Interval, s, "interval");
            ValidateHhMm(s.Start, s, "start time");
        }

        foreach (var c in Channels)
            if (c.Number is < 0 or > 99)
                throw new CloneFileFormatException($"Malformed row: SSB channel {c.Number} is outside 0-99.");
        // Unique numbers 0-99 — which is also what bounds an ELIDED file to at
        // most 100 rows, so the marker's rule adds no second count check.
        RejectDuplicateKeys(Channels.Select(c => c.Number), "SSB channel");
        // D4/D6: the ELIDED file's own rule. A file that says it dropped the
        // factory-default rows and then carries one is describing itself
        // wrongly, and the write leg would send a slot the wipe already set.
        if (DefaultChannelsElided)
            foreach (var c in Channels)
                if (c.IsFactoryDefault())
                    throw new CloneFileFormatException(
                        $"Malformed row: SSB channel {c.Number} holds the factory default values, and "
                        + "this file records that default channels were not stored.");

        foreach (var n in HopNets)
            if (n.Number is < 0 or > 9)
                throw new CloneFileFormatException($"Malformed row: HOP net {n.Number} is outside 0-9.");
        RejectDuplicateKeys(HopNets.Select(n => n.Number), "HOP net");

        foreach (var b in ExcludeBands)
            if (b.Band is < 0 or > 9)
                throw new CloneFileFormatException($"Malformed row: exclusion band {b.Band} is outside 0-9.");
        RejectDuplicateKeys(ExcludeBands.Select(b => b.Band), "exclusion band");

        // F9: the modem book is PROMPT-SPLIT — 0-6 at `SSB>`/`ALE>` and 7-9 at
        // `HOP>` (P5). One file carries both halves, so the LOAD bound is the
        // union; which half a preset belongs to decides which leg writes it,
        // and that is the campaign's business, not the file format's.
        foreach (var p in ModemPresets)
            if (p.Number is < Wire.ModemPresetMin or > ModemPresetScope.HopLast)
                throw new CloneFileFormatException(
                    $"Malformed row: modem preset {p.Number} is outside "
                    + $"{Wire.ModemPresetMin}-{ModemPresetScope.HopLast}.");
        RejectDuplicateKeys(ModemPresets.Select(p => p.Number), "modem preset");

        foreach (var m in Messages)
        {
            if (m.Slot is < 0 or > 9)
                throw new CloneFileFormatException($"Malformed row: message slot {m.Slot} is outside 0-9.");
            if (m.Text.Length is 0 or > 90)
                throw new CloneFileFormatException(
                    $"Malformed row: message slot {m.Slot} holds {m.Text.Length} characters (1-90 allowed).");
        }
        RejectDuplicateKeys(Messages.Select(m => m.Slot), "message slot");

        foreach (var s in Settings)
        {
            if (!CloneSettingsManifest.IsIncludedKey(s.Key))
                throw new CloneFileFormatException($"Malformed row: '{s.Key}' is not a clone-manifest setting.");
            // …AND ITS VALUE (P2 audit round 1, BLOCKER). Checking only the KEY
            // let a crafted `"DigitalVoice": "99"` through the door and through
            // the preflight; the row's own parser then refused it in leg 6 —
            // which runs AFTER the wipe, so the discovery was worthless. This
            // runs the SAME delegate the write leg runs, with no radio in
            // sight, so there is no second copy of the rules to keep in step.
            try
            {
                CloneSettingsManifest.CheckStoredValue(s.Key, s.Value);
            }
            catch (CloneValueException ex)
            {
                throw new CloneFileFormatException("Malformed row: " + ex.Message);
            }
        }
        var settingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Settings)
            if (!settingKeys.Add(s.Key))
                throw new CloneFileFormatException($"Duplicate setting '{s.Key}'.");

        ValidateLockouts();

        // LAST: the rules about a marker and its payload TOGETHER, or about one
        // domain projected into another. They run after every per-row shape has
        // been proven, so an operator meets the simple problem first.
        ValidateCrossFieldRules();
    }

    /// <summary>
    /// CROSS-FIELD RELATIONSHIPS — the rules no per-field disposition can hold,
    /// because they are about a MARKER and its PAYLOAD together (P2 audit round
    /// 1, BLOCKER).
    ///
    /// <para><b>The criterion, so the list is a decision and not a habit.</b> A
    /// marker/payload pair earns a rule when a missing payload makes the write
    /// campaign BEHAVE DIFFERENTLY — skip a leg, or send a different number of
    /// commands — rather than merely write fewer rows. Writing fewer rows is
    /// self-reporting: the exact verify names every one. A SKIPPED leg is not,
    /// and the skip is only discovered after the wipe.</para>
    ///
    /// <para><b>THE RULE: the operating snapshot.</b> <c>OperatingState ==
    /// Read</c> requires all THREE of mode, channel and HOP net. The read
    /// campaign already marks the domain <c>Read</c> only when it has all
    /// three, so a file claiming otherwise is crafted or corrupted — and each
    /// null makes the finals leg SILENTLY OMIT its write (the mode most
    /// dangerously, since the mode is what the whole campaign ends on).</para>
    ///
    /// <para><b>The other ten, examined and dispositioned.</b>
    /// <list type="bullet">
    /// <item><b>Operator lockouts</b> — HAS a rule, enforced with the domain's
    /// own checks below: <c>Read</c> requires all 22 inventory rows.</item>
    /// <item><b>Address book, channel groups, LQA schedules, exclusion bands,
    /// stored messages, modem presets</b> — NO RULE: an EMPTY payload is what a
    /// post-wipe read legitimately finds, so an emptiness rule would reject
    /// honest files. The headless case (no self, live references) is a
    /// READ-side fault and, on any other route, is caught before the wipe by
    /// the no-self preflight rejection and the swap's drop rules.</item>
    /// <item><b>SSB channels</b> — NO RULE: a real read always answers all 100
    /// slots, but an empty-and-Read file only writes nothing, and the exact
    /// verify then reports all 100 differences. Fewer rows, not a skipped
    /// leg.</item>
    /// <item><b>HOP nets</b> — NO RULE, for the same reason: the read campaign
    /// does require all ten, but a short file merely writes fewer nets and the
    /// verify names each.</item>
    /// <item><b>Settings</b> — NO RULE: an absent row is the DOCUMENTED
    /// representation of an unconfirmed mirror, and the write leg reports every
    /// one it does not find.</item>
    /// </list></para>
    /// </summary>
    private void ValidateCrossFieldRules()
    {
        ValidateOperatingSnapshot();
        ValidateEngagedModem();
    }

    private void ValidateOperatingSnapshot()
    {
        if (OperatingState != CloneDomainState.Read) return;

        foreach (var (what, missing) in new (string, bool)[]
        {
            ("operating mode", OperatingMode is not { Length: > 0 }),
            ("operating channel", OperatingChannel is null),
            ("operating HOP net", OperatingHopNet is null),
        })
            if (missing)
                throw new CloneFileFormatException(
                    $"Malformed file: the operating state is marked read but carries no {what}, "
                    + "so the write could not put the radio back where the file says it was.");
    }

    /// <summary>
    /// THE ENGAGED MODEM names a preset the file must hold, BY THE NAME THAT
    /// FILE'S OWN RECORD WILL GIVE IT (audit round 3 verification, BLOCKER).
    ///
    /// <para><b>Why the name cannot be checked as a spelling.</b> The row's own
    /// parser proves the SHAPE (<c>OFF</c>, or <c>n NAME</c>) and stops there,
    /// because nothing in a single value says which name is right. The name is
    /// a PROJECTION of another domain: the campaign writes preset <c>n</c> from
    /// <see cref="ModemPresets"/>, and the radio then reports the preset by the
    /// name it was just given. So <c>"1 t39"</c>, <c>"1 BAD"</c> and even a
    /// perfectly valid-looking <c>"1 SE"</c> all wrote successfully and then
    /// failed the byte-exact verify — the radio answered <c>1 T39</c>, because
    /// that is what the file's own preset 1 is called.</para>
    ///
    /// <para><b>The criterion, applied</b> (the same one the operating snapshot
    /// earns its rule under): a missing or mismatched name does not make the
    /// campaign write fewer rows — it makes the campaign write something the
    /// radio will REPORT DIFFERENTLY, and the difference is discovered by a
    /// verify that runs after the wipe. Derived, not duplicated: the name comes
    /// from <see cref="CloneModemPreset.NameToken"/>, which is how the preset
    /// write reads it too.</para>
    ///
    /// <para>Gated on <see cref="ModemState"/> being READ, because a file whose
    /// preset domain was never read cannot be written at all — the ordinary
    /// preflight refuses it, and rejecting it here would name the wrong
    /// problem.</para>
    /// </summary>
    private void ValidateEngagedModem()
    {
        if (ModemState != CloneDomainState.Read) return;
        if (Value("ActiveModem") is not { } engaged) return;
        if (string.Equals(engaged, "OFF", StringComparison.Ordinal)) return;

        // The shape is already proven by the row's parser; this reads it.
        var parts = engaged.Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out int slot))
            return;

        var record = ModemPresets.FirstOrDefault(p => p.Number == slot);
        if (record is null)
            throw new CloneFileFormatException(
                $"Malformed file: the engaged modem is recorded as '{engaged}', but this file holds no "
                + $"modem preset {slot} to engage.");

        if (record.NameToken() is not { Length: > 0 } name)
            throw new CloneFileFormatException(
                $"Malformed file: the engaged modem is recorded as '{engaged}', but this file's modem "
                + $"preset {slot} has no name, so the radio could not report it that way.");

        if (!string.Equals(parts[1], name, StringComparison.Ordinal))
            throw new CloneFileFormatException(
                $"Malformed file: the engaged modem is recorded as '{engaged}', but this file's modem "
                + $"preset {slot} is named '{name}' — after writing it the radio will report "
                + $"'{slot} {name}'.");
    }

    /// <summary>
    /// The operator-lockout domain (R2) — MANDATORY, and validated against the
    /// CLOSED 22-item inventory Core pins.
    ///
    /// <para><b>Absent is malformed, and it is distinguishable.</b> A missing
    /// <c>Lockouts</c> object deserializes to null (never to a defaulted one),
    /// so the rejection here can name the domain instead of silently writing a
    /// radio's front panel from an empty list. A file that carries the object
    /// but has not READ it is a different thing entirely: legal to hold, and
    /// refused by the ordinary write preflight, which names it like any other
    /// unread domain.</para>
    ///
    /// <para><b>Why the rows are checked against the inventory.</b> The write
    /// leg sends one set per row at that section's prompt. A row naming an item
    /// this radio does not have would be ECHOED and move nothing (the set form
    /// has no accept/reject semantics at all — captured), so the campaign would
    /// report a success the radio never performed. The only place that can be
    /// caught is here.</para>
    /// </summary>
    private void ValidateLockouts()
    {
        if (Lockouts is not { } lockouts)
            throw new CloneFileFormatException(
                "Malformed file: it carries no operator lockouts, and this app's clone files always do.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in lockouts.Rows)
        {
            var family = ParseDefined<LockoutFamily>(row.Family, "lockout family");
            var section = ParseDefined<LockoutSection>(row.Section, "lockout section");
            ParseDefined<LockState>(row.State, $"lockout state for {row.Family} {row.Section} {row.Item}");

            if (!LockoutInventory.Contains(family, section, row.Item ?? ""))
                throw new CloneFileFormatException(
                    $"Malformed row: '{row.Family} {row.Section} {row.Item}' is not an operator lockout "
                    + "this radio has.");
            if (!seen.Add($"{family}/{section}/{row.Item}"))
                throw new CloneFileFormatException(
                    $"Duplicate operator lockout '{row.Family} {row.Section} {row.Item}'.");
        }

        // A domain marked READ claims to be the radio's whole answer, and the
        // whole answer is the closed set. A short one would let the write leg
        // leave rows at whatever the ZEROIZE left them (all LOCK) while the
        // summary said nothing — so a partial read is FAULTED by the campaign
        // and a partial file is malformed here.
        if (lockouts.State == CloneDomainState.Read && seen.Count != LockoutInventory.Count)
            throw new CloneFileFormatException(
                $"Malformed file: the operator lockouts are marked read but carry {seen.Count} of "
                + $"{LockoutInventory.Count} rows.");
    }

    /// <summary>Parse an enum NAME with <c>Enum.IsDefined</c> — the r3 BLOCKER's
    /// rule (plan/plan-clone-file-validation.md §2/§3), applied to every enum
    /// this file parses. <c>Enum.TryParse</c> alone accepts undefined NUMERIC
    /// text ("99"), which then reaches the wire in a leg that runs after the
    /// wipe.</summary>
    private static T ParseDefined<T>(string? stored, string what) where T : struct, Enum
    {
        if (Enum.TryParse<T>((stored ?? "").Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
            return parsed;
        throw new CloneFileFormatException(
            $"Malformed row: '{stored}' is not a {what} this app has.");
    }

    /// <summary>
    /// REFERENTIAL INTEGRITY, and the operating snapshot's bounds (P6 audit
    /// round 2, BLOCKER).
    ///
    /// <para><b>The defect this closes.</b> Validation checked every row's own
    /// SHAPE and the global uniqueness of names, but nothing checked that a
    /// row NAMING ANOTHER ROW named one that exists. A file with a net
    /// associated to a self the file does not hold loaded cleanly, passed the
    /// transformed-graph revalidation, and was refused BY THE RADIO — at
    /// <c>NETAD</c>, in leg 8, which is AFTER the leg-6 <c>ERASE</c>. The
    /// operator's radio would have been wiped and half rewritten before
    /// anything said no.</para>
    ///
    /// <para><b>Why load-rejection is the right verdict for these.</b> A
    /// FAITHFUL radio read can never produce a nonblank-but-missing
    /// association: <c>DELAD</c> either re-points the dependants at the primary
    /// or blanks the nets, and the book listing is read whole. A file holding
    /// one is therefore crafted or corrupted, and refusing it at the door is
    /// the only place the refusal costs nothing. A <b>BLANK</b> net
    /// association stays LEGAL — it is the documented primary-deletion
    /// artifact, and the swap drops it with a report.</para>
    ///
    /// <para><b>The rest of the reference family</b> — a net's MEMBER rows and
    /// a schedule's TARGET — is deliberately NOT duplicated here: the swap
    /// transform already drops both with a named reason, it runs on EVERY
    /// write path (an empty identity still applies the unreplayable-state
    /// rules), and there is no write path that skips it. Adding a second rule
    /// would mean two places to keep in agreement about the same fact.</para>
    /// </summary>
    private void ValidateReferences()
    {
        var selfs = Selfs.Select(s => Normalize(s.Name)).ToHashSet(StringComparer.Ordinal);

        foreach (var individual in Individuals)
        {
            // INDAD cannot be sent without one, so an individual with no
            // associated self is unwritable by construction. (No capture has
            // ever shown one: the primary-deletion case DELETES individuals
            // rather than blanking them. If the bench ever produces one, this
            // moves to the swap as a drop-and-report, the way blank NETS are.)
            if (string.IsNullOrWhiteSpace(individual.AssociatedSelf))
                throw new CloneFileFormatException(
                    $"Malformed row: individual '{individual.Name}' has no associated self, "
                    + "and the radio cannot store an individual without one.");
            if (!selfs.Contains(Normalize(individual.AssociatedSelf)))
                throw new CloneFileFormatException(
                    $"Dangling reference: individual '{individual.Name}' is associated to "
                    + $"'{individual.AssociatedSelf}', which this file does not hold as a self.");
        }

        foreach (var net in Nets)
        {
            if (string.IsNullOrWhiteSpace(net.AssociatedSelf)) continue;   // the primary-deletion artifact
            if (!selfs.Contains(Normalize(net.AssociatedSelf)))
                throw new CloneFileFormatException(
                    $"Dangling reference: net '{net.Name}' is associated to "
                    + $"'{net.AssociatedSelf}', which this file does not hold as a self.");
        }

        // The operating snapshot is written LAST (leg 10) — also after the
        // erase — and its values reach CH / NET / the mode switch directly.
        // Out-of-range ones would throw there rather than here.
        if (OperatingChannel is { } channel && channel is < 0 or > 99)
            throw new CloneFileFormatException(
                $"Malformed row: the operating channel {channel} is outside 0-99.");
        if (OperatingHopNet is { } hopNet && hopNet is < 0 or > 9)
            throw new CloneFileFormatException(
                $"Malformed row: the operating HOP net {hopNet} is outside 0-9.");
        // THE r3 BLOCKER (plan/plan-clone-file-validation.md §3), closed here.
        // `Enum.TryParse` alone SUCCEEDS on undefined numeric text: a crafted
        // `"OperatingMode": "99"` used to load, pass the transformed-graph
        // revalidation, and throw at the finals leg — which runs after the
        // wipe, on a half-rewritten radio. `Enum.IsDefined` is what makes the
        // check mean what it always claimed to.
        if (OperatingMode is { Length: > 0 } mode
            && !(Enum.TryParse<Falcon.Core.Protocol.OperatingMode>(mode, ignoreCase: true, out var parsedMode)
                 && Enum.IsDefined(parsedMode)))
            throw new CloneFileFormatException(
                $"Malformed row: '{mode}' is not an operating mode this radio has.");
    }

    /// <summary>
    /// The hh:mm shape Core's own EXCH/SOU validator demands, checked at the
    /// door because Core checks it on the wire — in a leg that runs after the
    /// erase.
    ///
    /// <para><b>The parameter is NULLABLE deliberately</b> (P6 audit round 3,
    /// the user-facing fix; plan/plan-clone-file-validation.md §4). The
    /// property's non-null annotation is a promise the DESERIALIZER does not
    /// keep: <c>"Interval": null</c> in the JSON lands here as null, and the
    /// old length check dereferenced it — so a user opening a corrupted or
    /// hand-damaged file got an APP CRASH instead of the contracted "malformed
    /// row, offender named" rejection. A missing time is now simply not a
    /// time, and takes the same message every other bad one takes; because it
    /// is a <see cref="CloneFileFormatException"/> like the rest, the write
    /// preflight's existing catch covers the adopted-graph path too, where the
    /// NullReferenceException used to escape.</para>
    /// </summary>
    private static void ValidateHhMm(string? value, CloneSchedule row, string what)
    {
        bool shaped = value is not null
            && value.Length == 5 && value[2] == ':'
            && value[..2].All(char.IsAsciiDigit) && value[3..].All(char.IsAsciiDigit)
            && int.Parse(value[..2], CultureInfo.InvariantCulture) <= 23
            && int.Parse(value[3..], CultureInfo.InvariantCulture) <= 59;
        if (!shaped)
            throw new CloneFileFormatException(
                $"Malformed row: the {row.Kind} schedule for '{row.Address}' has {what} "
                + $"'{value ?? "(missing)"}', which is not a time between 00:00 and 23:59.");
    }

    private static void ValidateName(string name, string kind, int group, bool allowAssoc, string? assoc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 15)
            throw new CloneFileFormatException(
                $"Malformed row: a {kind} name is empty or over 15 characters ('{name}').");
        if (group is < 0 or > 9)
            throw new CloneFileFormatException(
                $"Malformed row: {kind} '{name}' has channel group {group}, which is outside 0-9.");
        if (!allowAssoc && assoc is not null)
            throw new CloneFileFormatException(
                $"Malformed row: self '{name}' carries an associated self, which selfs never have.");
        if (assoc is not null && assoc.Length > 15)
            throw new CloneFileFormatException(
                $"Malformed row: {kind} '{name}' names an associated self over 15 characters.");
    }

    private static void RejectDuplicateKeys(IEnumerable<int> keys, string what)
    {
        var seen = new HashSet<int>();
        foreach (var key in keys)
            if (!seen.Add(key))
                throw new CloneFileFormatException($"Duplicate {what} {key}.");
    }
}

/// <summary>Source-generated JSON context — no reflection serializer, so the
/// MAUI heads stay trim/AOT-clean.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloneFile))]
internal sealed partial class CloneJson : JsonSerializerContext;
