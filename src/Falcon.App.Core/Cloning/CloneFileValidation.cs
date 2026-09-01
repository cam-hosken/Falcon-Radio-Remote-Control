using System.Collections;
using System.Reflection;

namespace Falcon.App.Core.Cloning;

/// <summary>What PROVES a clone-file field cannot hurt the radio
/// (plan/plan-clone-file-validation.md §2).</summary>
public enum CloneFieldDisposition
{
    /// <summary>Checked at LOAD, offender named (R13). Proof = a load-rejection
    /// pin per rule.</summary>
    Validated,
    /// <summary>The type or its consumer cannot hold/pass an unsafe value — a
    /// range-checked int, an enum parsed WITH <c>Enum.IsDefined</c>, or a value
    /// whose setter validates it and whose refusal the campaign REPORTS rather
    /// than throws. Proof = the bounding check, cited.</summary>
    Bounded,
    /// <summary>The transform drops the row and says so (the unreplayable-state
    /// rules). Proof = the drop pin, cited.</summary>
    SwapDropped,
    /// <summary>Never reaches the wire: metadata, a read-state marker consumed
    /// only by the preflight, or a drop report. Proof = the non-consumption,
    /// cited.</summary>
    Inert,
}

/// <summary>One field of the clone file's type graph, with its disposition and
/// the proof obligation that disposition carries.</summary>
/// <param name="Field">"DeclaringType.Property" — the key
/// <see cref="CloneFileValidation.WalkFields(Type)"/> produces and the doc table's
/// row name.</param>
public sealed record CloneFieldRule(string Field, CloneFieldDisposition Disposition, string Proof);

/// <summary>
/// <b>VALIDATION CLOSED BY ENUMERATION</b> — plan/plan-clone-file-validation.md
/// §2, executed by clone round 12 P2 (plan-clone-round12.md §5).
///
/// <para><b>Why this file exists.</b> Three consecutive P6 audit rounds each
/// found ONE route by which bad FILE CONTENT slipped through load and preflight
/// and failed AFTER the erase — each fix correct for its scope, each increment
/// carrying one fresh pitfall (r3's hole was inside r2's fix). The failure
/// pattern was the METHOD: the validation surface was built incrementally under
/// audit pressure. The cure is enumeration, not another discovery round: stop
/// asking "what hole did the auditor find" and ask "what does EVERY field
/// prove".</para>
///
/// <para><b>What makes it hold.</b> <see cref="WalkFields(Type)"/> walks the file's
/// real type graph by reflection, and the completeness pin requires a
/// disposition for every field it finds — so a field ADDED LATER fails the
/// suite until somebody dispositions it. The same table is emitted into
/// docs/software-architecture.md and pinned against this one, so the document
/// cannot describe a validation surface the app does not have.</para>
///
/// <para><b>Accepted limitation, stated:</b> a machine can hold COMPLETENESS
/// and AGREEMENT. It cannot hold that a disposition is RIGHT — that stays a
/// review property, which is why every row carries its proof in words.</para>
/// </summary>
public static class CloneFileValidation
{
    /// <summary>
    /// Every field of <see cref="CloneFile"/>'s type graph, dispositioned.
    ///
    /// <para>Read with <see cref="WalkFields(Type)"/>: the two must agree exactly,
    /// in both directions (nothing undispositioned, nothing stale).</para>
    /// </summary>
    public static IReadOnlyList<CloneFieldRule> Dispositions { get; } =
    [
        // ---- CloneFile: metadata and the operating snapshot -----------------
        new("CloneFile.Version", CloneFieldDisposition.Validated,
            "Load rejects any value but `falconclone/1`, naming the one it found."),
        new("CloneFile.CapturedUtc", CloneFieldDisposition.Inert,
            "Informational. No leg reads it, and the verify comparison copies the read-back's own "
            + "value into the expected file before comparing, so it can never be a diff."),
        new("CloneFile.LoadNotices", CloneFieldDisposition.Inert,
            "Round 17 F6. Written BY Load, never read from a file: it carries `[JsonIgnore]`, so it "
            + "is neither serialized nor deserializable, and its only consumer is the summary line "
            + "CloneService.LoadJson shows the operator. No leg reads it and nothing reaches the wire."),
        new("CloneFile.OperatingMode", CloneFieldDisposition.Validated,
            "`Enum.TryParse` AND `Enum.IsDefined` at load — the r3 BLOCKER: TryParse alone accepts "
            + "undefined numeric text, which then throws at the finals leg, after the wipe."),
        new("CloneFile.OperatingChannel", CloneFieldDisposition.Validated,
            "0-99 at load; the finals leg sends it straight to `CH`."),
        new("CloneFile.OperatingHopNet", CloneFieldDisposition.Validated,
            "0-9 at load; the finals leg sends it straight to `NET`."),

        // ---- CloneFile: the eleven read-state markers -----------------------
        // One rule, eleven fields: `Enum.IsDefined` at load (JSON carries them
        // as NUMBERS, so an undefined one deserializes happily), and the write
        // preflight refuses anything but Read, naming the domain.
        new("CloneFile.OperatingState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.BookState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.GroupState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.ScheduleState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.ChannelState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.HopNetState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.ExcludeState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.ModemState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.MessageState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneFile.SettingState", CloneFieldDisposition.Validated, MarkerProof),
        new("CloneLockouts.State", CloneFieldDisposition.Validated, MarkerProof),

        // ---- CloneFile: the domain collections ------------------------------
        new("CloneFile.Selfs", CloneFieldDisposition.Validated,
            "Every row through ValidateName, plus the cross-kind uniqueness rule. ORDER is meaning "
            + "(the first self is the primary), so the swap orders the list and the campaign writes it "
            + "in order."),
        new("CloneFile.Individuals", CloneFieldDisposition.Validated,
            "ValidateName plus the reference rule: an individual with no associated self, or one "
            + "naming a self the file does not hold, is rejected at the door."),
        new("CloneFile.Nets", CloneFieldDisposition.Validated,
            "ValidateName plus the reference rule. A BLANK association stays LEGAL — it is the "
            + "documented primary-deletion artifact — and the swap drops it with a reason."),
        new("CloneFile.ChannelGroups", CloneFieldDisposition.Validated,
            "Group 0-9, every channel 0-99, no duplicate group."),
        new("CloneFile.Schedules", CloneFieldDisposition.Validated,
            "Kind, address and both hh:mm times at load — Core's own validator THROWS on the wire, in "
            + "a leg that runs after the wipe."),
        new("CloneFile.Channels", CloneFieldDisposition.Validated,
            "Number 0-99, no duplicate slot; the six field values are dispositioned on CloneChannel."),
        new("CloneFile.DefaultChannelsElided", CloneFieldDisposition.Bounded,
            "A bool cannot hold an unsafe value, and BOTH of its values are safe by construction "
            + "(plan-clone-write-structural.md D4/D6). TRUE selects the sparse rule — at most 100 rows, "
            + "unique 0-99, and NO row equal to `Wire.DefaultChannel`, the offender named at load. FALSE "
            + "(and ABSENT, which is every file written before this round) selects the round-17 F6 "
            + "100-row rule byte for byte, so a legacy file behaves identically. A file that lies either "
            + "way is caught rather than trusted: a sparse file marked false is DOWNGRADED to Faulted and "
            + "refused by the write preflight, and a full file marked true is REJECTED naming the first "
            + "default row. Nothing reaches the wire from this field — it only decides whether the write "
            + "leg's own default-row skip has anything left to skip."),
        new("CloneFile.HopNets", CloneFieldDisposition.Validated,
            "Number 0-9, no duplicate net."),
        new("CloneFile.ExcludeBands", CloneFieldDisposition.Validated,
            "Band 0-9, no duplicate band."),
        new("CloneFile.ModemPresets", CloneFieldDisposition.Validated,
            "Number 0-6, no duplicate preset."),
        new("CloneFile.Messages", CloneFieldDisposition.Validated,
            "Slot 0-9, no duplicate slot."),
        new("CloneFile.Settings", CloneFieldDisposition.Validated,
            "Every key must be a CloneSettingsManifest key; no duplicate key."),
        new("CloneFile.Lockouts", CloneFieldDisposition.Validated,
            "MANDATORY (owner ruling R2). The property is NULLABLE so an ABSENT domain deserializes "
            + "to null and cannot be confused with a defaulted one; Load rejects null naming the "
            + "domain, and a present-but-unread domain is refused by the ordinary preflight."),

        // ---- CloneAddress (selfs and individuals) ---------------------------
        new("CloneAddress.Name", CloneFieldDisposition.Validated,
            "Non-empty, at most 15 characters, and unique across selfs, individuals AND nets."),
        new("CloneAddress.Group", CloneFieldDisposition.Validated,
            "Channel group 0-9 at load; it reaches `SLFAD`/`INDAD` as the trailing argument."),
        new("CloneAddress.AssociatedSelf", CloneFieldDisposition.Validated,
            "At most 15 characters; FORBIDDEN on a self; required and resolvable to a self the file "
            + "holds on an individual."),

        // ---- CloneNet --------------------------------------------------------
        new("CloneNet.Name", CloneFieldDisposition.Validated,
            "Non-empty, at most 15 characters, globally unique across the three kinds."),
        new("CloneNet.Group", CloneFieldDisposition.Validated,
            "Channel group 0-9 at load; it reaches `NETAD` as the trailing argument."),
        new("CloneNet.AssociatedSelf", CloneFieldDisposition.Validated,
            "At most 15 characters; BLANK is legal (the primary-deletion artifact) and the swap drops "
            + "the net; a non-blank one must name a self the file holds."),
        new("CloneNet.Members", CloneFieldDisposition.SwapDropped,
            "Shape at load (non-empty, at most 15 characters each); the REFERENCE half is the swap's: "
            + "a member the book no longer holds, or a self that is not this net's own associated "
            + "self, is dropped and listed — the radio would refuse it on the wire."),

        // ---- CloneChannelGroup ----------------------------------------------
        new("CloneChannelGroup.Group", CloneFieldDisposition.Validated,
            "Group 0-9 at load, and no duplicate group in the file."),
        new("CloneChannelGroup.Channels", CloneFieldDisposition.Validated,
            "Every channel number 0-99 at load; each reaches `ADDC` through the gate."),

        // ---- CloneSchedule ---------------------------------------------------
        new("CloneSchedule.Kind", CloneFieldDisposition.Validated,
            "Exactly \"EXCHANGE\" or \"SOUND\" at load — it chooses which builder the row reaches."),
        new("CloneSchedule.Address", CloneFieldDisposition.Validated,
            "Non-blank at load; a target that left the book, or changed kind under the swap, is "
            + "dropped and listed."),
        new("CloneSchedule.Interval", CloneFieldDisposition.Validated,
            "hh:mm between 00:00 and 23:59 — JSON null included, which is the crash the round-3 "
            + "user-facing fix closed."),
        new("CloneSchedule.Start", CloneFieldDisposition.Validated,
            "hh:mm between 00:00 and 23:59, JSON null included."),

        // ---- CloneChannel ----------------------------------------------------
        new("CloneChannel.Number", CloneFieldDisposition.Validated,
            "Slot 0-99 at load, and no duplicate slot in the file."),
        new("CloneChannel.RxFrequency", CloneFieldDisposition.Bounded,
            "The setter validates the 8-digit form and throws ArgumentException; the campaign CATCHES "
            + "it and reports the value as unwritten. Nothing escapes into the wire."),
        new("CloneChannel.TxFrequency", CloneFieldDisposition.Bounded,
            "The setter validates the 8-digit form and throws; reported unwritten."),
        new("CloneChannel.Mode", CloneFieldDisposition.Bounded,
            "`Wire.ParseModulation`; an unrecognised spelling is REPORTED unwritten, never guessed."),
        new("CloneChannel.Agc", CloneFieldDisposition.Bounded,
            "The DI dump's own abbreviations then `Wire.ParseAgcSpeed`; unrecognised is reported "
            + "unwritten."),
        new("CloneChannel.Bandwidth", CloneFieldDisposition.Bounded,
            "The setter validates against its own captured list and throws; reported unwritten."),
        new("CloneChannel.RxOnly", CloneFieldDisposition.Bounded,
            "`Wire.ParseYesNo`; unrecognised is reported unwritten."),

        // ---- CloneHopNet -----------------------------------------------------
        new("CloneHopNet.Number", CloneFieldDisposition.Validated,
            "Net 0-9 at load, and no duplicate net in the file."),
        new("CloneHopNet.Wiped", CloneFieldDisposition.Bounded,
            "A bool cannot hold an unsafe value. A wiped record is simply NOT written — the ZEROIZE "
            + "already left every net blank."),
        new("CloneHopNet.NetId", CloneFieldDisposition.Bounded,
            "The builder validates the 8-digit id and throws; reported unwritten."),
        new("CloneHopNet.Type", CloneFieldDisposition.Bounded,
            "`Wire.ParseHopType`; unrecognised is reported unwritten and the net's values are skipped "
            + "with it."),
        new("CloneHopNet.CenterKHz", CloneFieldDisposition.Bounded,
            "Sent only for an NB net; the builder validates the 5-digit kHz form and throws; reported "
            + "unwritten."),
        new("CloneHopNet.LowKHz", CloneFieldDisposition.Bounded,
            "Sent only for a WB net; the builder validates and throws; reported unwritten."),
        new("CloneHopNet.HighKHz", CloneFieldDisposition.Bounded,
            "Sent only for a WB net; the builder validates and throws; reported unwritten."),
        new("CloneHopNet.ListFrequencies", CloneFieldDisposition.Bounded,
            "Sent only for a LIST net; the builder validates every entry and throws; reported "
            + "unwritten."),

        // ---- CloneExcludeBand ------------------------------------------------
        new("CloneExcludeBand.Band", CloneFieldDisposition.Validated,
            "Band 0-9 at load, and no duplicate band in the file."),
        new("CloneExcludeBand.LowKHz", CloneFieldDisposition.Bounded,
            "`KHzToHz` asserts exactly eight digits BEFORE anything is sent; a value that fails is "
            + "reported unwritten, naming the edges."),
        new("CloneExcludeBand.HighKHz", CloneFieldDisposition.Bounded,
            "`KHzToHz` asserts exactly eight digits before anything is sent; reported unwritten."),

        // ---- CloneModemPreset ------------------------------------------------
        new("CloneModemPreset.Number", CloneFieldDisposition.Validated,
            "Preset 0-9 at load — the UNION of the two prompt-scoped bands (F9: 0-6 live at `SSB>` "
            + "and 7-9 at `HOP>`) — and no duplicate preset in the file. Which band a number is in "
            + "decides which write leg sends it, not whether the file may hold it."),
        new("CloneModemPreset.Fields", CloneFieldDisposition.Bounded,
            "The raw listing row is re-parsed through ModemPresetVocabulary before the write; a row "
            + "this app cannot re-send is reported unwritten and the preset is skipped. It is also "
            + "the ONLY representation of MARK/SPACE (R3): a row carrying no MARK/SPACE tokens is "
            + "one whose tones were unreadable at capture, and the omission line derives from that "
            + "absence rather than from a second nullable field. F9 adds the HOP shape to the same "
            + "rule: a 7-9 row is the SHORT line (no TYPE, no INTER) and its baud must be one of "
            + "75/150/300 — anything else the radio SILENTLY ignores while echoing the old value, "
            + "so a hand-edited baud is reported unwritten at the write rather than refused at load."),
        new("CloneModemPreset.Enabled", CloneFieldDisposition.Bounded,
            "A bool cannot hold an unsafe value; it is written as the trailing EN/DIS state token, "
            + "LAST, because any field write re-enables a disabled preset."),

        // ---- CloneTxMessage --------------------------------------------------
        new("CloneTxMessage.Slot", CloneFieldDisposition.Validated,
            "Slot 0-9 at load, and no duplicate slot in the file."),
        new("CloneTxMessage.Text", CloneFieldDisposition.Validated,
            "1-90 characters at load — the captured store limit."),

        // ---- CloneSetting ----------------------------------------------------
        new("CloneSetting.Key", CloneFieldDisposition.Validated,
            "Must be a key the clone settings manifest carries; unknown keys are rejected at load."),
        new("CloneSetting.Value", CloneFieldDisposition.Validated,
            "RE-DISPOSITIONED from BOUNDED by the P2 audit (round 1, BLOCKER). Bounded was too weak: "
            + "the value's only check was the row's setter lambda, which runs in leg 6 — AFTER the "
            + "wipe — so a crafted \"99\" got a radio erased and then failed. The manifest row's write "
            + "is now split into a surface-free `Parse` and a `Send`, and LOAD runs the row's REAL "
            + "`Parse` (`Enum.TryParse` WITH `Enum.IsDefined`, or a bounded int), naming the offender. "
            + "One delegate, so the door and the wire cannot drift."),

        // ---- The lockout domain ----------------------------------------------
        new("CloneLockouts.Rows", CloneFieldDisposition.Validated,
            "Every row keyed (family, section, item) against Core's CLOSED 22-item inventory; "
            + "duplicates rejected; a domain marked READ must carry all 22, because a short one would "
            + "silently leave rows at whatever the ZEROIZE left them (all LOCK)."),
        new("CloneLockout.Family", CloneFieldDisposition.Validated,
            "The enum NAME, parsed with `Enum.IsDefined` (the r3 rule)."),
        new("CloneLockout.Section", CloneFieldDisposition.Validated,
            "The enum NAME, parsed with `Enum.IsDefined`."),
        new("CloneLockout.Item", CloneFieldDisposition.Validated,
            "Must name a row of the closed inventory for that (family, section). A set naming an item "
            + "the radio does not have is ECHOED and moves nothing, so this is the only place it can "
            + "be caught."),
        new("CloneLockout.State", CloneFieldDisposition.Validated,
            "The enum NAME, parsed with `Enum.IsDefined`."),
    ];

    private const string MarkerProof =
        "Read-state marker. `Enum.IsDefined` at load (JSON carries these as NUMBERS, so an undefined "
        + "one would deserialize happily), and the write preflight refuses any state but Read, naming "
        + "the domain.";

    /// <summary>
    /// Every STORED field of <see cref="CloneFile"/>'s type graph, as
    /// "DeclaringType.Property", in walk order.
    ///
    /// <para><b>The walk's rules, stated because they decide what the
    /// completeness pin covers:</b></para>
    /// <list type="number">
    /// <item>A field is a public instance property that can be both READ and
    /// WRITTEN. A computed projection (<c>ManifestDomains</c>,
    /// <c>IncompleteDomains</c>) has no setter, holds nothing, and is not a
    /// field — it is a view OF fields that are already dispositioned.</item>
    /// <item>A property whose type is one of the file's own row types, or a
    /// list of them, is an EDGE: it gets its own row (its shape and its
    /// per-row rules are a disposition) and the walk RECURSES into the type.</item>
    /// <item>Everything else is a LEAF: scalars, strings, nullables, enums, and
    /// lists of scalars.</item>
    /// <item>Types are visited once, so a shared row type
    /// (<c>CloneAddress</c> serves both selfs and individuals) yields ONE set
    /// of rows, keyed by its declaring type.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> WalkFields(Type root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var fields = new List<string>();
        var visited = new HashSet<Type>();
        Walk(root, fields, visited);
        return fields;
    }

    /// <summary>The walk the completeness pin uses: the clone file itself.</summary>
    public static IReadOnlyList<string> WalkFields() => WalkFields(typeof(CloneFile));

    private static void Walk(Type type, List<string> fields, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.GetIndexParameters().Length > 0) continue;

            fields.Add($"{property.DeclaringType!.Name}.{property.Name}");

            if (RowTypeOf(property.PropertyType) is { } rowType) Walk(rowType, fields, visited);
        }
    }

    /// <summary>The file's OWN row type behind a property, or null when the
    /// property is a leaf. "Own" is decided by ASSEMBLY AND NAMESPACE, not by a
    /// hand-kept list — a row type added later is followed automatically, which
    /// is the whole point of a reflection walk.</summary>
    private static Type? RowTypeOf(Type propertyType)
    {
        if (IsRowType(propertyType)) return propertyType;
        if (!typeof(IEnumerable).IsAssignableFrom(propertyType) || !propertyType.IsGenericType) return null;
        var element = propertyType.GetGenericArguments()[0];
        return IsRowType(element) ? element : null;
    }

    private static bool IsRowType(Type type)
        => type.IsClass
        && !type.IsPrimitive
        && type != typeof(string)
        && type.Assembly == typeof(CloneFile).Assembly
        && type.Namespace == typeof(CloneFile).Namespace;
}
