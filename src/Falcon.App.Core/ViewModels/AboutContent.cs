namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The About page's text (clone round 12 §6 F6), carried from the old
/// WinForms app's About box (falcon-reference <c>Falcon.Gui/About.cs</c> +
/// <c>Properties/AssemblyInfo.cs</c>).
///
/// <para><b>Why the strings live in App.Core and not in the XAML.</b> They are
/// the one part of the page that is a FACT about hardware — which cable, which
/// connector, which pins — and a fact that only exists as markup cannot be
/// pinned by the host test suite (there is no MAUI head here). Held as
/// constants, the page renders them and <c>AboutContentTests</c> asserts them
/// byte-exact against what the old app said.</para>
///
/// <para><b>Carried, adjusted, and dropped — the honest ledger:</b></para>
/// <list type="bullet">
///   <item><b>Carried verbatim:</b> both cable recommendations, the mating
///     connector, and the three pin assignments. These are the reason the old
///     About box was worth reading.</item>
///   <item><b>Adjusted:</b> the product description. The original reads
///     "Harris Falon I radios" — a typo, and this app targets the
///     Falcon series over the front-panel remote port, so the sentence is
///     restated rather than copied.</item>
///   <item><b>Added:</b> the credit line. The old box carried W6HOS as its
///     assembly Company/Copyright; this app is a clean-room rewrite that owes
///     that work its existence, so the attribution is explicit prose instead
///     of a hidden assembly attribute. <b>ROUND 13 C1 (backlog item 12, owner
///     2026-08-19):</b> the "Based on …" framing is GONE — the owner IS W6HOS,
///     so the line is a BYLINE, not a derivation notice, and its year is the
///     CURRENT one. Only the prefix is a constant
///     (<see cref="CreditPrefix"/>); the year and the closing paren are
///     composed in the page's code-behind, for the same reason the version is
///     (see below): a year in a constant is a year that goes stale.</item>
///   <item><b>DROPPED, then RESTORED REWORDED (owner 2026-08-23):</b> the old
///     box said "Tip: Click the numbers above the Step Size +/- buttons to
///     enable arrow keys for frequency scrolling." That UI does not exist here
///     (no Step Size +/- buttons), so round 12 dropped the line — but the
///     app's own equivalent gesture DOES exist (click a frequency digit to arm
///     the keyboard cursor, ui.md "the digit cursor"), and the owner asked for
///     the tip back. <see cref="FrequencyTip"/> carries the reworded line; the
///     page shows it on WINDOWS ONLY (Android has no keyboard path).</item>
/// </list>
///
/// <para>The VERSION is deliberately NOT here: it is the running app's own
/// (<c>AppInfo</c>), read in the page's code-behind, because a version
/// constant is a version that goes stale. Round 13 C1 puts the credit YEAR
/// under the same doctrine — <see cref="CreditPrefix"/> is the half that
/// cannot rot.</para>
/// </summary>
public static class AboutContent
{
    /// <summary>What the app is. Restated from the original's description
    /// (see the typo note above).</summary>
    public const string Description =
        "Remote control for Harris Falcon-series radios over the radio's front-panel remote port.";

    /// <summary>Heading over the cable/connector facts.</summary>
    public const string CableHeading = "Cable and wiring";

    public const string CableRecommended =
        "Recommend using an FTDI USB-RS232-WE-XXXX cable with radio remote port set to RS-232.";

    public const string CableAlternate =
        "Alternate cable FTDI TTL-232RG-VSW5V-WE with radio remote port set to MIL-188.";

    public const string MatingConnector = "Radio side mating connector is PT06A 12-14P-SR.";

    public const string PinoutGround = "Gnd: Pin J";
    public const string PinoutTx = "Tx (To Radio): Pin K";
    public const string PinoutRx = "Rx (From Radio): Pin N";

    /// <summary>The byline's fixed half — everything up to the year. The
    /// code-behind appends the CURRENT year and the closing paren
    /// (round 13 C1); no year is spelled here, deliberately.</summary>
    public const string CreditPrefix = "By W6HOS (© ";

    /// <summary>Prefix the code-behind puts in front of the running app's own
    /// version string.</summary>
    public const string VersionPrefix = "Version ";

    /// <summary>The keyboard-tuning tip (owner 2026-08-23 — the old box's
    /// "Step Size" tip, reworded to THIS app's gesture; see the class ledger).
    /// Rendered on Windows only: Android has no keyboard path (ui.md).</summary>
    public const string FrequencyTip =
        "Tip: click a frequency digit to enable the arrow keys — \u2190/\u2192 move along the digits, \u2191/\u2193 tune the selected digit, Esc releases.";
}
