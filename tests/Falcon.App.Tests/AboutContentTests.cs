using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// CLONE ROUND 12 §6 F6 — the About page's carried content, BYTE-EXACT
/// against the old WinForms app.
///
/// <para><b>Why these particular sentences are pinned as strings.</b> They are
/// not copy; they are FACTS about hardware the operator will go and buy or
/// wire — a cable part number, a mating connector, three pin letters. Getting
/// one character wrong sends someone to the wrong plug, and nothing else in
/// this repo would notice. The old app's About box is the source (falcon-
/// reference <c>Falcon.Gui/About.cs</c>), and these assertions are the
/// transcription check.</para>
///
/// <para>What is NOT pinned here: the page's layout and its version line. The
/// version is the running app's own (<c>AppInfo</c>) precisely so that no
/// constant can go stale, and the page's structure is
/// ConnectionFlowSourceGuardTests' job — this file pins the WORDS.</para>
/// </summary>
public class AboutContentTests
{
    [Fact]
    public void TheCableGuidance_IsCarriedVerbatim()
    {
        // Both cables, each with the remote-port MODE it goes with. The mode
        // is half the fact: the same radio port is RS-232 or MIL-188
        // depending on how it is set, and the wrong pairing does not work.
        Assert.Contains(
            "FTDI USB-RS232-WE-XXXX cable with radio remote port set to RS-232",
            AboutContent.CableRecommended, StringComparison.Ordinal);

        Assert.Contains(
            "FTDI TTL-232RG-VSW5V-WE with radio remote port set to MIL-188",
            AboutContent.CableAlternate, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMatingConnector_IsCarriedVerbatim()
        => Assert.Equal("Radio side mating connector is PT06A 12-14P-SR.", AboutContent.MatingConnector);

    [Fact]
    public void ThePinout_IsCarriedVerbatim_AllThreePins()
    {
        // Direction words included: "To Radio" / "From Radio" is what stops a
        // reader wiring Tx to Tx.
        Assert.Equal("Gnd: Pin J", AboutContent.PinoutGround);
        Assert.Equal("Tx (To Radio): Pin K", AboutContent.PinoutTx);
        Assert.Equal("Rx (From Radio): Pin N", AboutContent.PinoutRx);
    }

    [Fact]
    public void TheFrequencyTip_DescribesThisAppsGesture_NotTheOldApps()
    {
        // OWNER 2026-08-23: the old box's "Step Size" tip is back, reworded to
        // the gesture this app actually has (click a digit to arm; ui.md "the
        // digit cursor"). Pinned so nobody "restores" the old wording, whose
        // Step Size +/- buttons do not exist here, and so the arming verb
        // ("click a frequency digit") stays the true one.
        Assert.Equal(
            "Tip: click a frequency digit to enable the arrow keys — ←/→ move along the digits, ↑/↓ tune the selected digit, Esc releases.",
            AboutContent.FrequencyTip);
        Assert.DoesNotContain("Step Size", AboutContent.FrequencyTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCreditPrefix_IsAByline_AndSpellsNoYearOfItsOwn()
    {
        // ROUND 13 C1 (backlog item 12, owner 2026-08-19). Two changes in one
        // line, and both are pinned because both are easy to undo by accident.
        //
        // WHO: the old wording was "Based on Falcon Radio Remote Control by
        // W6HOS (© 2020)" — a derivation notice. The owner IS W6HOS, so the
        // line is a BYLINE.
        //
        // WHEN: the constant now STOPS before the year. The year is composed
        // in the page's code-behind from the clock (the VersionPrefix pattern),
        // which is the only way a copyright line stays current — and the
        // trailing space is part of the contract, because the code-behind
        // concatenates rather than formats.
        Assert.Equal("By W6HOS (© ", AboutContent.CreditPrefix);

        // The staleness pin, stated independently of the exact string above so
        // that a REWORD (which the owner may do) cannot quietly bake a year
        // back in. YEAR-SHAPED, not digit-free: the callsign itself carries a
        // digit ("W6HOS"), so the thing to forbid is a four-digit run.
        Assert.DoesNotMatch(@"\d{4}", AboutContent.CreditPrefix);

        // …and the matcher's own control, so the line above cannot pass by
        // being unable to see a year at all.
        Assert.Matches(@"\d{4}", "By W6HOS (© 2020)");
    }

    [Fact]
    public void TheDescription_NamesTheRadioFamilyAndThePort()
    {
        // ADJUSTED, not carried (the ledger in AboutContent says why): the
        // original spells the family "Falon I". What must survive the
        // restatement is what the app is FOR.
        Assert.Contains("Falcon", AboutContent.Description, StringComparison.Ordinal);
        Assert.Contains("front-panel remote port", AboutContent.Description, StringComparison.Ordinal);

        // …and the typo is not carried with it.
        Assert.DoesNotContain("Falon", AboutContent.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDroppedKeyboardTip_IsNowhereInTheContent()
    {
        // §6 F6 DROPS the old "click the numbers above the Step Size +/-
        // buttons to enable arrow keys" tip: there are no Step Size +/-
        // buttons in this app, so the tip would send the operator looking for
        // a control that does not exist. The drop is RECORDED (AboutContent's
        // ledger and docs/ui.md) rather than silent — and pinned here, because
        // a later "restore the old About text wholesale" would bring it back.
        foreach (var text in new[]
        {
            AboutContent.Description,
            AboutContent.CableHeading,
            AboutContent.CableRecommended,
            AboutContent.CableAlternate,
            AboutContent.MatingConnector,
            AboutContent.PinoutGround,
            AboutContent.PinoutTx,
            AboutContent.PinoutRx,
            AboutContent.CreditPrefix,
            AboutContent.VersionPrefix,
        })
        {
            Assert.DoesNotContain("Step Size", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arrow keys", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoStringIsEmpty()
    {
        // Anti-vacuity for the whole file: a constant that had been emptied
        // would satisfy every DoesNotContain above.
        foreach (var text in new[]
        {
            AboutContent.Description, AboutContent.CableHeading, AboutContent.CableRecommended,
            AboutContent.CableAlternate, AboutContent.MatingConnector, AboutContent.PinoutGround,
            AboutContent.PinoutTx, AboutContent.PinoutRx, AboutContent.CreditPrefix,
            AboutContent.VersionPrefix,
        })
            Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
