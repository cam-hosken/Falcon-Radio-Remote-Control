namespace Falcon.Core.Radio;

/// <summary>Which lockout FAMILY a row belongs to — the two global state
/// reports the radio answers from any one prompt (bare <c>PROGRAM</c> and bare
/// <c>SELECT</c>, captured 2026-08-18, bench/transcripts/r11-lockouts-*).</summary>
public enum LockoutFamily
{
    /// <summary>Bare <c>PROGRAM</c> — the programmable-parameter locks.</summary>
    Program,
    /// <summary>Bare <c>SELECT</c> — the selectable-parameter locks.</summary>
    Select,
}

/// <summary>Which MODE SECTION of a lockout report a row sits under. The
/// report prints one <c>&gt;&gt;&lt;section&gt;_Programmable_Parameters</c> /
/// <c>_Selectable_Parameters</c> header per section, and ITEM NAMES REPEAT
/// ACROSS SECTIONS (PROGRAM carries DATA twice and CFIG twice; SELECT carries
/// DATA and KEY three times each) — which is why every lockout is keyed
/// (family, section, item) and never by item alone.</summary>
public enum LockoutSection
{
    Ssb,
    Hop,
    /// <summary>EAM ≙ the ALE/EAM mode family (the radio's own section
    /// spelling in the report header).</summary>
    Eam,
}

/// <summary>A lockout row's state, exactly the two tokens the report and the
/// set echo carry.</summary>
public enum LockState
{
    Lock,
    Unlock,
}

/// <summary>One lockout row. The KEY is (family, section, item); the state is
/// the radio's own last word about it.</summary>
public sealed record LockoutRow(LockoutFamily Family, LockoutSection Section, string Item, LockState State);

/// <summary>How far a lockout READ has got — the three states every read store
/// in this project carries, so a display can say "—" until a read has actually
/// committed rather than call everything locked.</summary>
public enum LockoutReadState
{
    /// <summary>No lockout read has committed this session.</summary>
    Unknown,
    /// <summary>A read is on the wire; the previous rows (if any) still stand.</summary>
    InFlight,
    /// <summary>A read committed: <see cref="LockoutMirror.Rows"/> is the
    /// radio's answer as of that read.</summary>
    Completed,
}

/// <summary>The lockout mirror: a read state plus the committed rows.</summary>
public sealed record LockoutMirror(LockoutReadState State, IReadOnlyList<LockoutRow> Rows);

/// <summary>
/// The CLOSED 22-item lockout inventory, pinned in Core
/// (plan-clone-round12.md §3 / invariant 2). Captured 2026-08-18 from the
/// real radio: 13 PROGRAM items across three sections and 9 SELECT items
/// across three sections.
///
/// <para>The set is closed on purpose: an item line the radio emits that is
/// NOT in here is a LOUD PARSE FACT (the row is refused and the line surfaces
/// through the unrecognized path), never a silent twenty-third row. A radio
/// that grows an item is a bench discovery that edits this list.</para>
/// </summary>
public static class LockoutInventory
{
    /// <summary>The PROGRAM items per section, in the radio's own report
    /// order.</summary>
    private static readonly (LockoutSection Section, string[] Items)[] ProgramItems =
    [
        (LockoutSection.Ssb, ["CHAN", "FILL", "CFIG", "DATA", "KEYS"]),
        (LockoutSection.Hop, ["NET", "EXCLUDE", "TX_POWER", "DATA"]),
        (LockoutSection.Eam, ["ADDRESS", "CHGROUP", "CFIG", "LQA"]),
    ];

    /// <summary>The SELECT items per section, in the radio's own report
    /// order.</summary>
    private static readonly (LockoutSection Section, string[] Items)[] SelectItems =
    [
        (LockoutSection.Ssb, ["DATA", "KEY", "MODE", "TMP_CHAN", "BFO"]),
        (LockoutSection.Hop, ["DATA", "KEY"]),
        (LockoutSection.Eam, ["DATA", "KEY"]),
    ];

    /// <summary>Every lockout key, report order: the whole PROGRAM report then
    /// the whole SELECT report, each section in the captured order.</summary>
    public static IReadOnlyList<(LockoutFamily Family, LockoutSection Section, string Item)> All { get; } =
    [
        .. ProgramItems.SelectMany(s => s.Items.Select(i => (LockoutFamily.Program, s.Section, i))),
        .. SelectItems.SelectMany(s => s.Items.Select(i => (LockoutFamily.Select, s.Section, i))),
    ];

    /// <summary>Total rows in the closed set (13 PROGRAM + 9 SELECT).</summary>
    public const int Count = 22;

    /// <summary>True when (family, section, item) names a row of the closed
    /// inventory. The item is compared ORDINALLY against the captured
    /// upper-case spellings — the parser uppercases before dispatch.</summary>
    public static bool Contains(LockoutFamily family, LockoutSection section, string item)
    {
        foreach (var key in All)
            if (key.Family == family && key.Section == section
                && string.Equals(key.Item, item, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>True when the family carries this item in ANY section. Used
    /// where the SECTION is not yet known — a set ECHO names no section, but an
    /// item this family does not have at all is still a loud parse fact rather
    /// than an unattributable echo.</summary>
    public static bool ContainsItem(LockoutFamily family, string item)
    {
        foreach (var key in All)
            if (key.Family == family && string.Equals(key.Item, item, StringComparison.Ordinal))
                return true;
        return false;
    }
}
