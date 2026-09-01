using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 5 (BA, and K7's fixed display width) — the digit-strip
/// GEOMETRY, pinned against the markup.
///
/// <para><b>Why a source pin and not just the UIA gate.</b> The UIA measurement
/// proves the geometry ONCE, on one machine, at one window size. These numbers
/// are load-bearing beyond that: 40 dp is a deliberate, owner-approved
/// deviation from the app's ≥44 dp touch-target rule (one dimension only —
/// the height stays 44), and the 4 dp cell spacing is what the narrowing paid
/// for. A later edit that "restores" 44 for consistency, or that quietly drops
/// the spacing, would be a reasonable-looking change that undoes a decision.
/// It should have to update this file to happen.</para>
///
/// <para><b>Scope is asserted, not assumed.</b> BA2 confines the narrowing to
/// the RX/TX frequency strips: the channel spinners on the same pane, the
/// SSB-settings channel picker, the modem chevrons and the BFO ± all stay 44.
/// Half of this file exists to pin that boundary, because "narrow the
/// chevrons" is exactly the kind of instruction that spreads.</para>
///
/// <para>XML, not regex, for the reason RefreshButtonWidthGuardTests
/// documents: a property can be set as an attribute or as a property element,
/// and only a parser sees both. Same accepted limitation as every scan here —
/// a value supplied from a style or from code-behind is invisible.</para>
/// </summary>
public class ChevronGeometryGuardTests
{
    private const int DigitChevronWidth = 40;
    private const int ChevronHeight = 44;
    private const int CellSpacing = 4;
    private const int StandardChevron = 44;

    /// <summary>K7: sized to the widest legal modem text, "6: XXXX"-class.</summary>
    private const int ModemDisplayWidth = 96;

    private static readonly string SsbPane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "SsbPaneView.xaml");

    private static readonly string SsbSettingsPane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");

    [Fact]
    public void DigitStripChevrons_Are40WideAnd44Tall()
    {
        // BA1. Both strips, all four chevrons (▲/▼ × RX/TX templates).
        var buttons = DigitStripChevrons().ToList();
        Assert.Equal(4, buttons.Count);

        Assert.All(buttons, b =>
        {
            Assert.Equal(DigitChevronWidth, Property(b, "WidthRequest"));
            Assert.Equal(ChevronHeight, Property(b, "HeightRequest"));
        });
    }

    [Fact]
    public void DigitStrips_Put4DpBetweenTheCells()
    {
        // BA1's other half — the air the narrowing bought. The spacing lives on
        // the strip layout, so each of the eight items (an optional decimal
        // point plus one cell) is separated from the next by exactly this.
        var strips = Strips().ToList();
        Assert.Equal(2, strips.Count);       // RxDigitStrip and TxDigitStrip

        Assert.All(strips, s => Assert.Equal(CellSpacing, Property(s, "Spacing")));
    }

    [Fact]
    public void TheRowArithmeticStillFitsTheAh1Budget()
    {
        // Not a measurement — the ARITHMETIC the measurement is checked
        // against, kept next to the numbers it is about. Round 4 measured the
        // row at 431.5 dp with 8 cells of 44 and no spacing; round 5 spends
        // 8×40 + 7×4 = 348 where round 4 spent 352, so the row gets 4 dp BACK.
        // If someone changes a constant above without thinking, this says what
        // it costs.
        int roundFour = 8 * StandardChevron;
        int roundFive = (8 * DigitChevronWidth) + (7 * CellSpacing);

        Assert.True(roundFive <= roundFour,
            $"BA1 must not widen the row: {roundFive} dp of cells vs round 4's {roundFour} dp "
            + "(the AH1 no-clip budget is 437 dp of desktop card content and 448 dp on the bench phone)");
    }

    [Fact]
    public void EveryOtherChevronInTheAppStays44_AndNoneMayOmitItsWidth()
    {
        // BA2, pinned as a boundary in BOTH directions.
        //
        // A MISSING WidthRequest is an offence, not a pass (round-5 audit).
        // The first version skipped it, which meant deleting the BFO ±
        // button's width — the one chevron whose Segment style would otherwise
        // impose a much wider minimum — left the guard green while the button
        // silently drifted to segment size. "No width set" is exactly the
        // regression this file exists to catch, so it cannot be the quiet
        // case.
        var offenders = new List<string>();

        foreach (var (file, button) in AllChevronButtons())
        {
            if (IsDigitStripChevron(button)) continue;
            int? width = Property(button, "WidthRequest");
            if (width is null)
                offenders.Add($"{Path.GetFileName(file)}: chevron '{Text(button)}' sets NO {nameof(width)}Request — "
                            + $"it will take whatever its Style imposes, not the {StandardChevron} dp chevron size");
            else if (width != StandardChevron)
                offenders.Add($"{Path.GetFileName(file)}: chevron '{Text(button)}' is {width} dp wide, not {StandardChevron}");
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheChannelSpinnersOnTheSettingsEditor_AreStill44()
    {
        // The owner called these out explicitly ("agreed" — the spinners
        // already have 8 dp between them and stay 44). This is the
        // anti-vacuity pin for the rule above: it names controls the scan MUST
        // be finding, so an empty offender list cannot mean "found nothing".
        //
        // ROUND 15 N2 re-scoped this from BOTH panes to the SSB-SETTINGS one:
        // the Operate pane's channel spinners are DELETED (the card takes a
        // typed 1-2 digit entry + Select now), so asking for four channel
        // chevrons there would fail on a pane that is correct. The settings
        // editor's spinners — a PROGRAMMING surface, untouched by N2 — still
        // carry the pin, and the absence half is asserted below.
        var spinners = ChevronButtons(Load(SsbSettingsPane))
            .Where(b => !IsDigitStripChevron(b))
            .Where(b => (Description(b) ?? "").Contains("channel", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(spinners.Count >= 4, "channel spinner chevrons not found in " + SsbSettingsPane);
        Assert.All(spinners, b => Assert.Equal(StandardChevron, Property(b, "WidthRequest")));
    }

    [Fact]
    public void TheOperatePane_HasNoChannelSpinnerChevronsLeft_N2()
    {
        // The other half of the re-scope: N2's card replaced them, so a chevron
        // that came BACK on the Operate pane means the deleted commands came
        // back with it.
        var strays = ChevronButtons(Load(SsbPane))
            .Where(b => !IsDigitStripChevron(b))
            .Where(b => (Description(b) ?? "").Contains("channel", StringComparison.OrdinalIgnoreCase))
            .Select(b => Description(b) ?? "")
            .ToList();

        Assert.Empty(strays);
    }

    [Fact]
    public void TheModemDisplay_KeepsItsFixed96DpWidth()
    {
        // K7: the display is sized to its widest legal text ("6: XXXX"-class)
        // and never moves with its value — rule K, and the reason the round-5
        // colon could be added without the field twitching.
        //
        // The VALUE is asserted, not merely its presence (round-5 audit): a
        // width that exists but has shrunk clips "6: XXXX" just as badly as no
        // width lets it resize, and K7 names 96 as the number it checked.
        var pane = Load(SsbPane);

        var display = pane.Descendants()
            .Where(e => e.Name.LocalName == "Border")
            .Single(e => (Description(e) ?? "") == "Active modem preset");

        Assert.Equal(ModemDisplayWidth, Property(display, "WidthRequest"));
    }

    // ---- element selection ---------------------------------------------------

    /// <summary>The frequency digit chevrons: the ONLY chevrons bound to a
    /// per-digit VfoDigitViewModel command. Identified by that binding rather
    /// than by position, so the pin follows the control if the markup moves.
    /// </summary>
    private static bool IsDigitStripChevron(XElement button)
    {
        var command = button.Attribute("Command")?.Value ?? "";
        return command is "{Binding UpCommand}" or "{Binding DownCommand}";
    }

    private static IEnumerable<XElement> DigitStripChevrons()
        => ChevronButtons(Load(SsbPane)).Where(IsDigitStripChevron);

    private static IEnumerable<XElement> Strips()
        => Load(SsbPane).Descendants()
            .Where(e => e.Name.LocalName == "HorizontalStackLayout")
            .Where(e => (e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? "")
                .EndsWith("DigitStrip", StringComparison.Ordinal));

    /// <summary>Every ▲/▼/◀/▶/±-style button in the app layer.</summary>
    private static IEnumerable<(string File, XElement Button)> AllChevronButtons()
    {
        foreach (var file in AppXamlFiles(FindRepoRoot()))
            foreach (var b in ChevronButtons(XDocument.Load(file)))
                yield return (file, b);
    }

    private static IEnumerable<XElement> ChevronButtons(XDocument document)
        => document.Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Where(e => Text(e) is "▲" or "▼" or "◀" or "▶" or "−" or "+");

    // ---- attribute reading (both forms) --------------------------------------

    private static int? Property(XElement e, string name)
    {
        var raw = e.Attribute(name)?.Value
                  ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + name)?.Value;
        return int.TryParse(raw, out int value) ? value : null;
    }

    private static string? Text(XElement e)
        => e.Attribute("Text")?.Value
           ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + ".Text")?.Value;

    private static string? Description(XElement e)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "SemanticProperties.Description")?.Value
           ?? e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Description")?.Value;

    private static XDocument Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path);
    }

    private static IEnumerable<string> AppXamlFiles(string root)
    {
        var layer = Path.Combine(root, "src", "Falcon.App");
        foreach (var file in Directory.EnumerateFiles(layer, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            yield return file;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Falcon-Radio-Controller.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }
}
