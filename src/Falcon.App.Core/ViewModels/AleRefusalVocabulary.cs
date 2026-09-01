namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The ALE programming refusal vocabulary (plan-ale-programming.md §4.4, owner
/// ruling 3): the radio's own refusal line → a HUMAN-READABLE status. The
/// <see cref="ModemPresetVocabulary"/> seam pattern — one static map class, so
/// the two programming cards and their tests cannot drift apart on what a
/// refusal means.
///
/// <para><b>OWNER RULING R13 (2026-08-17, round 11):</b> a refusal message is
/// written for the OPERATOR and NEVER exposes the raw radio token — no
/// parenthesized token anywhere. This REPLACES the earlier "keep the token
/// visible" house style: the seven original wordings dropped their
/// <c> (TOKEN)</c> parentheticals (prefix wording otherwise unchanged) and the
/// round-11 ten were written token-free from the start. The house-style pin in
/// <c>AleRefusalVocabularyTests</c> flipped with it, from token-VISIBLE to
/// token-ABSENT, and it is structural: it scans every mapped wording for any
/// mapped token and for any parenthesized upper-case token shape, so a
/// reinstated parenthetical fails wherever it is written.</para>
///
/// <para><b>The keys are TRIMMED radio lines</b>, exactly as
/// <c>AleState.ProgrammingRefusal.Line</c> carries them: the parser routes
/// <c> ADDRESS EXISTS </c> and the <c> INV … </c> family through
/// <c>HandleRefusal</c> (which stores the trimmed raw line) and the
/// <c>**</c>-prefix branch notes the literal <c>** ERROR **</c>. Every one of
/// those seven lines has a NON-VERBATIM mapping here — that is the owner
/// ruling, and <c>AleRefusalVocabularyTests</c> cross-checks the key set
/// against what the REAL parser produces from the verbatim captures.
/// <b>Round 11 §8 adds the characterization campaign's TEN</b> — membership,
/// schedule and channel-group refusals — with the plan's own byte-exact
/// wordings (see the block comment beside them).</para>
///
/// <para><b>UNKNOWN tokens render the GENERIC, not the raw line</b> (R13,
/// amended). The earlier rule was the opposite — an uncaptured shape rendered
/// VERBATIM, "honesty over prettiness" — and R13 supersedes it, invariant
/// §7.6's verbatim half with it: a raw radio token is not operator language
/// whatever drew it. Nothing is lost, because the raw line is still on the
/// CONSOLE, which is where the evidence has always lived; the status area now
/// says only what an operator can act on. So <see cref="Describe"/> has
/// exactly two outcomes: a mapped wording, or the generic — never wire
/// text.</para>
/// </summary>
public static class AleRefusalVocabulary
{
    /// <summary>Trimmed radio line → operator-readable status. Evidence: every
    /// key is a VERBATIM capture (plan §1 "Refusal/error lines observed";
    /// docs/protocol.md ALE section).</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // Names are GLOBAL across selfs/individuals/nets/members.
        ["ADDRESS EXISTS"] =
            "Refused — that name is already in use",
        // INDAD/NETAD: the associated self must already exist.
        ["INV ASSOC SELF"] =
            "Refused — the associated self does not exist on the radio",
        // ADDM: the member must already exist.
        ["INV MEMBER ADDR"] =
            "Refused — that member address does not exist on the radio",
        ["INV SELF ADDRESS"] =
            "Refused — the radio rejected that self address",
        ["INV IND ADDRESS"] =
            "Refused — the radio rejected that individual address",
        ["INV ADDRESS"] =
            "Refused — the radio rejected that address",
        // R13 COLLAPSE, deliberate and THREE-WAY: stripped of its
        // "(** ERROR **)" this wording is the generic, so the bare error
        // banner, a blank detail and an unknown token all render one string.
        // Honest rather than accidental — "** ERROR **" carries no detail
        // beyond "the radio rejected it", which is exactly what the generic
        // says. Held as ONE constant so they cannot drift apart (and so the
        // trailing period R13 gave the generic applies here too).
        ["** ERROR **"] = GenericRefusal,

        // ---- UI-tweaks round 11 §8: the characterization campaign's ten ----
        // The wordings below are the PLAN'S OWN TABLE, byte for byte
        // (plan-ui-tweaks-round11.md §8, owner-approved). They are plain
        // operator sentences that say what to DO — the register R13 then made
        // the rule for the whole vocabulary. AleRefusalVocabularyTests pins all
        // seventeen byte-for-byte, plus the structural token-absent scan.
        ["DUPLICATE MEMBER"] = "Already a member of this net.",
        ["INV SELF MEMBER"] = "Only this net's own associated self can be a member.",
        ["ADR ALREADY QUED"] = "Already queued — stop its schedule first.",
        ["LQA QUEUE FULL"] = "The schedule queue is full (10).",
        ["INDIV CHANS REQD"] = "The individual's channel group has no channels.",
        ["SELF CHANS REQD"] = "The self's channel group has no channels.",
        ["NET CHANS REQD"] = "The net's channel group has no channels.",
        ["INV CHAN NUMBER"] = "Channel must be 0-99.",
        ["INV NET ADDRESS"] = "Not a programmed net.",
        ["INVALID ADDRESS"] = "Nothing is queued for that address.",
    };

    /// <summary>The mapped lines, for the cross-check test.</summary>
    public static IReadOnlyCollection<string> MappedLines => Map.Keys;

    /// <summary>What a refusal with no detail, a bare <c>** ERROR **</c>, and
    /// an UNCAPTURED refusal shape all say (R13). One constant, one
    /// string.</summary>
    private const string GenericRefusal = "Refused — the radio rejected the command.";

    /// <summary>The operator-readable status for one refusal line: the mapped
    /// wording when the vocabulary knows the line, the generic otherwise. It
    /// NEVER returns wire text — a line nobody has captured is still a refusal
    /// the operator can do nothing with, and the raw line is on the Console
    /// (R13).</summary>
    public static string Describe(string? refusalLine)
    {
        var line = (refusalLine ?? "").Trim();
        if (line.Length == 0) return GenericRefusal;
        return Map.TryGetValue(line, out var friendly) ? friendly : GenericRefusal;
    }
}
