using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// UI tweaks round 10 (§3) — the display constitution's ADOPTIONS on the
/// SETTINGS surfaces (phase P3: SsbSettingsPaneView, HopSettingsPaneView,
/// RadioSettingsPage, SettingsPage, DeviceClockView, AleProgrammingView,
/// AleScanGroupsView, AleSettingsPaneView, ModemPresetsView), pinned against
/// the markup. The P2 counterpart is OperateStyleAdoptionGuardTests; this file
/// is the same idiom over the files P3 owns.
///
/// <para><b>Why both halves are needed.</b> StyleVocabularyGuardTests pins
/// that the keys EXIST and say the right things; a promoted key with no
/// consumers is a comment. The failure mode is not deletion — it is DRIFT: an
/// edit re-adds <c>FontFamily="Consolas"</c> beside the style, or swaps
/// <c>WidthRequest="{StaticResource ValueWidthWide}"</c> back to a literal 96,
/// and nothing notices because the pixels are identical THAT DAY. The next
/// change to the key then silently misses those sites, which is exactly the
/// drift §3 exists to end.</para>
///
/// <para><b>Structural, never substring.</b> Every check reads a named XML
/// attribute or its property-element twin, and the last test proves the reader
/// sees both spellings and reports "unset" as null.</para>
///
/// <para><b>The manifests here are CLOSED</b> (plan invariant 3): these sites
/// and no others. A control that MATCHES a §3 rule but is not LISTED stays
/// untouched — so the inline-copy scan carries an explicit LEDGER of the
/// copies §3 deliberately did not list, and asserts the survivors are exactly
/// that ledger. Both directions fail: a new inline copy, and a ledger entry
/// that quietly disappeared.</para>
///
/// <para>ACCEPTED LIMITATION, as everywhere in this house style: a value
/// supplied indirectly (implicit style, trigger, code-behind, platform
/// override) is invisible. Accidents are caught; adversarial construction is
/// backstopped by review and the bench width checks.</para>
/// </summary>
public class SettingsStyleAdoptionGuardTests
{
    private static readonly string SsbSettings =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");
    private static readonly string HopSettings =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "HopSettingsPaneView.xaml");
    private static readonly string AleSettings =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleSettingsPaneView.xaml");
    private static readonly string AddressCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleProgrammingView.xaml");
    private static readonly string GroupsCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleScanGroupsView.xaml");
    private static readonly string ModemCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "ModemPresetsView.xaml");
    private static readonly string ClockView =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "DeviceClockView.xaml");
    private static readonly string RadioPage =
        Path.Combine("src", "Falcon.App", "Views", "RadioSettingsPage.xaml");
    private static readonly string ConnectionPage =
        Path.Combine("src", "Falcon.App", "Views", "SettingsPage.xaml");

    /// <summary>The nine files phase P3 owns. The inline-copy scan runs over
    /// exactly these — the Operate surfaces had their copies deleted in P2
    /// with their own owners, and a scan reaching into them would be asserting
    /// another phase's work.</summary>
    private static IEnumerable<string> P3Files =>
        [SsbSettings, HopSettings, AleSettings, AddressCard, GroupsCard,
         ModemCard, ClockView, RadioPage, ConnectionPage];

    // ---- §3: the ValueWidth* / NumericEntryWidth consumer manifest ----------

    /// <summary>Every §3 value display P3 owns: file · the BINDING that
    /// identifies it · the width key it must now carry. Keyed by the binding,
    /// which is the one thing about a display that cannot be edited by
    /// accident.</summary>
    public static TheoryData<string, string, string> ValueDisplayWidths => new()
    {
        // Narrow 64.
        { SsbSettings, "{Binding RfGainText}", "ValueWidthNarrow" },
        { RadioPage, "{Binding BacklightIntensityText}", "ValueWidthNarrow" },
        { RadioPage, "{Binding ContrastText}", "ValueWidthNarrow" },
        // Wide 96.
        { HopSettings, "{Binding CenterDisplayText}", "ValueWidthWide" },
        { HopSettings, "{Binding LowDisplayText}", "ValueWidthWide" },
        { HopSettings, "{Binding HighDisplayText}", "ValueWidthWide" },
        // CLONE ROUND 12 §9 C1: the RadioPage BacklightFunctionText row is
        // DELETED — the chip it sized is gone, replaced by a highlighted
        // ChoiceItem row off the same mirror. The two INTENSITY/CONTRAST
        // narrow displays above are untouched: §9 C2 changed their CONTROL
        // (Entry+Set → chevrons), not their readout.
        { ClockView, "{Binding RadioTodText}", "ValueWidthWide" },
        // XWide 110.
        { SsbSettings, "{Binding AvsOddText}", "ValueWidthXWide" },
        { HopSettings, "{Binding NetIdDisplayText}", "ValueWidthXWide" },
    };

    [Theory]
    [MemberData(nameof(ValueDisplayWidths))]
    public void EachValueDisplay_CarriesItsNamedWidth(string file, string binding, string key)
    {
        // The WIDTH sits on the ValueDisplay BORDER, the binding on the Label
        // inside it — so the pin walks from the value to the frame that sizes
        // it, which is also the only honest way to say "this display".
        var label = Load(file).Descendants().Single(e =>
            e.Name.LocalName == "Label" && PropertyValue(e, "Text") == binding);

        var border = label.Ancestors().First(a =>
            (PropertyValue(a, "Style") ?? "").Contains("ValueDisplay", StringComparison.Ordinal));

        Assert.Equal($"{{StaticResource {key}}}", PropertyValue(border, "WidthRequest"));

        // The other half of an adoption: the inline FontFamily the promoted
        // ValueDisplayText style now supplies is GONE from the label.
        Assert.Null(PropertyValue(label, "FontFamily"));
        Assert.Equal("{StaticResource ValueDisplayText}", PropertyValue(label, "Style"));
    }

    [Fact]
    public void TheRwasKeyDisplay_TheOneWithALiteralDash_AlsoTookTheKey()
    {
        // RWAS key is write-only, so its display holds a literal "—" rather
        // than a binding — which is exactly why it needs its own line here:
        // the binding-keyed theory above cannot see it.
        var label = Load(SsbSettings).Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && PropertyValue(e, "Text") == "—"
            && (PropertyValue(e, "Style") ?? "").Contains("ValueDisplayText", StringComparison.Ordinal));

        var border = label.Ancestors().First(a =>
            (PropertyValue(a, "Style") ?? "").Contains("ValueDisplay", StringComparison.Ordinal));

        Assert.Equal("{StaticResource ValueWidthNarrow}", PropertyValue(border, "WidthRequest"));
        Assert.Null(PropertyValue(label, "FontFamily"));
    }

    /// <summary>The number-entry columns §3 names, keyed by the buffer each
    /// entry binds. CLONE ROUND 12 §9 C2 retired the RadioSettingsPage pair:
    /// backlight intensity and contrast are chevrons now, so there is no Entry
    /// left to size. §3 named four; two remain, both on
    /// SsbSettingsPaneView.</summary>
    public static TheoryData<string, string> NumericEntries => new()
    {
        { SsbSettings, "RfGainInput" },
        { SsbSettings, "RwasKeyInput" },
    };

    [Theory]
    [MemberData(nameof(NumericEntries))]
    public void EachNumericEntryColumn_CarriesTheNamedEntryWidth(string file, string buffer)
    {
        // The width sits on the ENTRY, not in the ColumnDefinitions string: a
        // StaticResource cannot live inside a GridLengthCollection (the same
        // constraint §4's station grids hit), so the column is Auto and the
        // entry's own WidthRequest sizes it. That is a genuine adoption of the
        // key — and it is what a literal 90 in the grid string could not be.
        var entry = Load(file).Descendants().Single(e =>
            e.Name.LocalName == "Entry"
            && (PropertyValue(e, "Text") ?? "").Contains(buffer, StringComparison.Ordinal));

        Assert.Equal("{StaticResource NumericEntryWidth}", PropertyValue(entry, "WidthRequest"));

        // …and its grid no longer spells the old literal 90 column.
        var grid = entry.Ancestors().First(a =>
            a.Name.LocalName == "Grid" && PropertyValue(a, "ColumnDefinitions") is not null);
        Assert.DoesNotContain("90", PropertyValue(grid, "ColumnDefinitions")!);
    }

    // ---- §3: the font-role manifest ----------------------------------------

    /// <summary>Every §3 label site P3 adopts: file · the Text that identifies
    /// it · the style it must now carry.</summary>
    public static TheoryData<string, string, string> AdoptedLabels => new()
    {
        // Caption row labels — HopSettings' six W=86 labels…
        { HopSettings, "Net ID", "Caption" },
        { HopSettings, "Type", "Caption" },
        { HopSettings, "Center (MHz)", "Caption" },
        { HopSettings, "Low (MHz)", "Caption" },
        { HopSettings, "High (MHz)", "Caption" },
        { HopSettings, "Add", "Caption" },
        // …RadioSettingsPage ×4…
        { RadioPage, "Backlight", "Caption" },
        { RadioPage, "Backlight intensity", "Caption" },
        { RadioPage, "Contrast", "Caption" },
        // CLONE FIELD ROUND 2 (R-A): the R9 picker + Entry pair became the
        // identity TABLE, so the two labels the round-11 rows named are gone.
        // Their replacements are the table's per-row labels, which carry the
        // same Caption style the retired rows did.
        //
        // CLONE PANE CLEANUP: the table's old "ALE selfs for write" CAPTION is
        // retired with it — the Write tab's three sections are named by
        // CardHeadings now (below), and a heading plus a caption saying the
        // same thing is two things to keep in sync.
        { RadioPage, "Swap with", "Caption" },
        { RadioPage, "…or a new name", "Caption" },
        { RadioPage, "{Binding Title}", "Caption" },
        // …and DeviceClockView's.
        { ClockView, "Radio TOD", "Caption" },
        // CardHeading + StatusText on the battery card.
        { RadioPage, "Battery status", "CardHeading" },
        { RadioPage, "{Binding BatteryText}", "StatusText" },
        // CLONE PANE CLEANUP: the Cloning card's three Write-tab section
        // headings, and the two per-tab status lines. The card's own title
        // ("Cloning") is a CardHeading like every other card's.
        { RadioPage, "Cloning", "CardHeading" },
        { RadioPage, "Clone file", "CardHeading" },
        { RadioPage, "ALE identity", "CardHeading" },
        { RadioPage, "Write", "CardHeading" },
        { RadioPage, "{Binding ReadStatusText}", "StatusText" },
        { RadioPage, "{Binding WriteStatusText}", "StatusText" },
        { RadioPage, "{Binding FileLine}", "StatusText" },
        // SubHeading: the HOP frequencies SECTION head (its table's column
        // heading is a separate, still-CellHeading label).
        { HopSettings, "Frequencies (MHz)", "SubHeading" },
        // SpinnerDigit: the two digit sites P3 owns.
        { HopSettings, "{Binding PickedNetText}", "SpinnerDigit" },
        { GroupsCard, "{Binding PickedGroupText}", "SpinnerDigit" },
    };

    [Theory]
    [MemberData(nameof(AdoptedLabels))]
    public void EachAdoptedSite_CarriesItsStyle_AndNoInlineFontCopyBesideIt(
        string file, string text, string style)
    {
        var labels = Load(file).Descendants()
            .Where(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == text)
            .Where(e => (PropertyValue(e, "Style") ?? "").Contains(style, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.Equal($"{{StaticResource {style}}}", PropertyValue(l, "Style")));

        // The inline properties the style now supplies are GONE. Leaving them
        // is how a site stops following the style it appears to use.
        foreach (var label in labels)
            foreach (var property in new[] { "FontSize", "FontFamily", "FontAttributes" })
                Assert.Null(PropertyValue(label, property));
    }

    [Fact]
    public void TheHopSectionHead_AndItsTableColumnHeading_AreDifferentTiers()
    {
        // Anti-vacuity for the SubHeading row above: "Frequencies (MHz)"
        // appears TWICE in this pane and the two are deliberately different
        // tiers — the LIST editor's section head (SubHeading) and the net-list
        // TABLE's column heading (CellHeading). A sweep that unified them
        // would satisfy one pin and break the shared-vocabulary contract
        // HopPaneMarkupGuardTests keeps.
        var labels = Load(HopSettings).Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && PropertyValue(e, "Text") == "Frequencies (MHz)")
            .Select(e => PropertyValue(e, "Style"))
            .ToList();

        Assert.Equal(2, labels.Count);
        Assert.Contains("{StaticResource SubHeading}", labels);
        Assert.Contains("{StaticResource CellHeading}", labels);
    }

    // ---- §3: the console toolbar joins the ACTION class --------------------

    [Fact]
    public void TheConsoleToolbarButtons_TakeTheSegmentStyle_WhileTheTerminalAreaStaysExempt()
    {
        // §3 narrows the console exemption to the TERMINAL AREA (rows,
        // typography, the 220-dp filter, no headers). Its toolbar buttons were
        // style-less and are ACTION class now. Both halves pinned together,
        // because the exemption is only meaningful if something asserts where
        // it stops.
        //
        // D17 (2026-08-30) makes them FOUR: the single platform-split export
        // press splits into the Cloning card's "Store file…" / "Share…" pair,
        // and both join the class the press they replace was already in.
        //
        // D18 (2026-08-30) makes them SIX and the toolbar's Entries TWO: the
        // gated raw-command input's "Enable input" toggle and "Send" are
        // ACTION class like every other press here, and its command box takes
        // the SAME 220 dp the filter has — one exempt terminal-area width, not
        // two different ones.
        var page = Load(RadioPage);

        var toolbar = page.Descendants().Single(e => e.Name.LocalName == "FlexLayout");
        var buttons = toolbar.Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(6, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("{StaticResource Segment}", PropertyValue(b, "Style")));
        Assert.All(buttons, b => Assert.Null(PropertyValue(b, "WidthRequest")));

        // EXEMPT, and pinned so: BOTH toolbar entries keep the 220, and the log
        // rows keep their own 12-pt Consolas typography.
        var entries = toolbar.Elements().Where(e => e.Name.LocalName == "Entry").ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("220", PropertyValue(e, "WidthRequest")));

        // D18(b): the log's TEXT cell is a ConsoleLogLabel now (the selection
        // scoping), so the typography sweep reads Label AND that subclass —
        // otherwise the cell whose font this pin exists for would drop out of
        // it silently.
        var logCells = page.Descendants()
            .Where(e => e.Name.LocalName is "Label" or "ConsoleLogLabel"
                        && PropertyValue(e, "FontSize") == "12")
            .ToList();
        Assert.Equal(3, logCells.Count);
        Assert.All(logCells, l => Assert.Equal("Consolas", PropertyValue(l, "FontFamily")));
        Assert.Contains(logCells, l => l.Name.LocalName == "ConsoleLogLabel");
    }

    [Fact]
    public void TheConnectionSettingsRefresh_TookTheSegmentStyle_AndKeptItsRowScope()
    {
        // §6's one style change on this page: the style-less button joins the
        // ACTION class. It stays row-scoped beside the picker it refreshes —
        // the placement was already right, only the style was missing.
        var button = Load(ConnectionPage).Descendants().Single(e =>
            e.Name.LocalName == "Button"
            && (PropertyValue(e, "Command") ?? "").Contains("RefreshPortsCommand", StringComparison.Ordinal));

        Assert.Equal("{StaticResource Segment}", PropertyValue(button, "Style"));
        Assert.Null(PropertyValue(button, "WidthRequest"));
        Assert.Equal("1", PropertyValue(button, "Grid.Column"));

        // EXEMPT and untouched (ledger): the Picker Titles. The other half of
        // that round-10 ledger entry — the page's 560-dp content cap — is
        // RETIRED by round 11 §9: the Windows window is fixed at
        // WindowFixedWidth, which leaves less content than the cap ever bound,
        // so the cap could only mislead a later reader. Pinned absent here and
        // arithmetically in StyleVocabularyGuardTests.
        Assert.Contains(Load(ConnectionPage).Descendants(),
            e => e.Name.LocalName == "Picker" && PropertyValue(e, "Title") == "Serial port");
        Assert.DoesNotContain(Load(ConnectionPage).Descendants(),
            e => e.Attributes().Any(a => a.Name.LocalName == "MaximumWidthRequest"));
    }

    /// <summary>CLONE ROUND 12 §6 F2 — the Connect toggle ARRIVES on this page
    /// carrying the same ACTION-class vocabulary it had in the title bar.
    /// Re-scoped rather than re-invented: a moved control that quietly loses
    /// its style is the failure mode a move has, and this page's §3 region is
    /// where that would show.
    /// <para>The width half is deliberately split: this pin says the button
    /// takes no width of its own (ACTION class), and the FULL-WIDTH decision
    /// lives with the placement pin in ConnectionFlowSourceGuardTests.</para></summary>
    [Fact]
    public void TheConnectToggle_ArrivedWithTheSegmentStyle_AndNoWidthOfItsOwn()
    {
        var button = Load(ConnectionPage).Descendants().Single(e =>
            e.Name.LocalName == "Button"
            && (PropertyValue(e, "Command") ?? "").Contains("ToggleCommand", StringComparison.Ordinal));

        Assert.Equal("{StaticResource Segment}", PropertyValue(button, "Style"));
        Assert.Null(PropertyValue(button, "WidthRequest"));
        Assert.Equal("44", PropertyValue(button, "MinimumHeightRequest"));
    }

    /// <summary>CLONE ROUND 12 §9 C1 — the backlight row is a HIGHLIGHTING
    /// choice row now, and the chip beside it is gone. Round 11 pinned two
    /// literal Segment buttons carrying wire tokens as CommandParameters; the
    /// row's defect was precisely that neither could ever highlight, so the
    /// re-pin asserts the SHAPE THAT CAN: a BindableLayout over the VM's
    /// choices, driven by the shared ChoiceButton template.
    /// <para>The OFF/MOM labels and the wire tokens both moved into the VM
    /// (pinned there by DeviceSettingsViewModelTests), which is why this test
    /// no longer reads either out of the markup — there is nothing here left
    /// for a rename to break.</para></summary>
    [Fact]
    public void TheBacklightRow_IsAHighlightingChoiceRow_AndTheChipIsGone_C1()
    {
        var page = Load(RadioPage);

        // The row binds the VM's choices through the shared template.
        var layout = page.Descendants().Single(e =>
            (e.Attributes().FirstOrDefault(a => a.Name.LocalName == "BindableLayout.ItemsSource")?.Value ?? "")
                .Contains("BacklightFunctionChoices", StringComparison.Ordinal));
        Assert.Equal("{StaticResource ChoiceButton}",
            layout.Attributes().FirstOrDefault(a => a.Name.LocalName == "BindableLayout.ItemTemplate")?.Value);

        // …and that template really is the highlighting one.
        var template = page.Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "ChoiceButton"));
        var trigger = template.Descendants().Single(e => e.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding IsActive}",
            trigger.Attributes().FirstOrDefault(a => a.Name.LocalName == "Binding")?.Value);

        // The CHIP is deleted: no ValueDisplay on this page shows the backlight
        // function any more, and no literal OFF/MOM Segment survives beside it.
        Assert.DoesNotContain(page.Descendants(),
            e => (PropertyValue(e, "Text") ?? "").Contains("BacklightFunctionText", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Descendants(),
            e => e.Name.LocalName == "Button"
                && (PropertyValue(e, "Command") ?? "").Contains("SetBacklightCommand", StringComparison.Ordinal));
    }

    /// <summary>CLONE ROUND 12 §9 C2 — the intensity and contrast rows are
    /// CHEVRON PAIRS, and the Entry+Set pairs they replaced are gone. The
    /// Option-B confirmed readouts STAY (that is the half C2 deliberately did
    /// not change), so the pin asserts both the arrival and the departure —
    /// a chevron pair added BESIDE a surviving Entry would be the plausible
    /// half-done state.</summary>
    [Fact]
    public void TheIntensityAndContrastRows_AreChevronPairs_WithNoEntriesLeft_C2()
    {
        var page = Load(RadioPage);

        foreach (var (command, readout) in new[]
        {
            ("BacklightIntensity", "BacklightIntensityText"),
            ("Contrast", "ContrastText"),
        })
        {
            var chevrons = page.Descendants()
                .Where(e => e.Name.LocalName == "Button")
                .Where(e => (PropertyValue(e, "Command") ?? "")
                    .Contains(command + "UpCommand", StringComparison.Ordinal)
                    || (PropertyValue(e, "Command") ?? "")
                        .Contains(command + "DownCommand", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(2, chevrons.Count);
            var glyphs = chevrons.Select(c => PropertyValue(c, "Text")).ToList();
            Assert.Contains("◀", glyphs);
            Assert.Contains("▶", glyphs);

            // The confirmed readout is still there, still narrow.
            Assert.Contains(page.Descendants(), e =>
                (PropertyValue(e, "Text") ?? "").Contains(readout, StringComparison.Ordinal));
        }

        // The retired plumbing. Scoped to the two buffers by NAME rather than
        // to "no Entry on this page" — the Cloning card below owns an Entry of
        // its own (the typed ALE self) and is another phase's region.
        foreach (var buffer in new[] { "BacklightIntensityInput", "ContrastInput" })
            Assert.DoesNotContain(page.Descendants(),
                e => (PropertyValue(e, "Text") ?? "").Contains(buffer, StringComparison.Ordinal));

        // …and the client-side error caption those Entries fed, which belonged
        // to DeviceSettingsViewModel and had no other producer.
        Assert.DoesNotContain(page.Descendants(),
            e => (PropertyValue(e, "IsVisible") ?? "") == "{Binding HasInputError}");
    }

    /// <summary>ROUND 13 C2 (backlog item 8, owner 2026-08-19) — the intensity
    /// and contrast chevrons FLANK their readout: label · ◀ · [value] · ▶.
    ///
    /// <para><b>Why this pin is new.</b> The round-12 sibling above reads the
    /// glyphs as an unordered SET, which was right for what it asserted (the
    /// pair arrived, the Entries left) and is exactly the shape that cannot see
    /// an ORDER regress: put ◀▶ back together on the right and every assertion
    /// in it still passes. The owner asked for the flanking arrangement
    /// specifically, so the arrangement is what gets held — positionally, by
    /// column, in both directions.</para></summary>
    [Fact]
    public void TheIntensityAndContrastChevrons_FlankTheirReadout_C2_Round13()
    {
        var page = Load(RadioPage);

        foreach (var (command, readout) in new[]
        {
            ("BacklightIntensity", "BacklightIntensityText"),
            ("Contrast", "ContrastText"),
        })
        {
            var row = page.Descendants()
                .Where(e => e.Name.LocalName == "Grid")
                .Single(g => g.Elements().Any(c =>
                    (PropertyValue(c, "Command") ?? "").Contains(command + "UpCommand", StringComparison.Ordinal)));

            XElement Child(Func<XElement, bool> match) => row.Elements().Single(match);

            var label = Child(c => c.Name.LocalName == "Label");
            var down = Child(c => (PropertyValue(c, "Command") ?? "")
                .Contains(command + "DownCommand", StringComparison.Ordinal));
            var value = Child(c => c.Name.LocalName == "Border");
            var up = Child(c => (PropertyValue(c, "Command") ?? "")
                .Contains(command + "UpCommand", StringComparison.Ordinal));

            // label · ◀ · [value] · ▶ — the ORDER, by column.
            Assert.Equal("0", PropertyValue(label, "Grid.Column"));
            Assert.Equal("1", PropertyValue(down, "Grid.Column"));
            Assert.Equal("2", PropertyValue(value, "Grid.Column"));
            Assert.Equal("3", PropertyValue(up, "Grid.Column"));

            // The glyphs sit the way round they read: ◀ steps down, ▶ steps up.
            // Column order alone would be satisfied by a swapped pair.
            Assert.Equal("◀", PropertyValue(down, "Text"));
            Assert.Equal("▶", PropertyValue(up, "Text"));

            // ANTI-VACUITY: the Border this row wraps is the readout itself,
            // not some other Border that happened to be the only one — and the
            // grid still has its four columns, so "column 3" means the right
            // edge rather than an overflow the layout silently ignores.
            Assert.Contains(value.Descendants(),
                e => (PropertyValue(e, "Text") ?? "").Contains(readout, StringComparison.Ordinal));
            Assert.Equal("*,Auto,Auto,Auto", PropertyValue(row, "ColumnDefinitions"));
        }
    }

    // ---- The inline-copy scan, with its explicit ledger ---------------------

    /// <summary>The inline copies §3 deliberately did NOT list in P3, each
    /// with the reason it survives. The scan below asserts the survivors are
    /// EXACTLY this set — a new copy fails, and so does a ledger entry that
    /// quietly disappeared.</summary>
    public static IReadOnlyList<(string File, string Text, string Why)> InlineCopyLedger =>
    [
        // The three ◀/▶ wheel displays on the address card and the two book
        // tables' cells are CellValue-styled, and CellValue has carried
        // Consolas since round 4 — these are PRE-EXISTING redundancies beside
        // a style §3 did not promote this round, and the closed-manifest rule
        // says an unlisted control stays untouched.
        (AddressCard, "{Binding GroupText}", "CellValue's own Consolas, unlisted by §3"),
        // ROUND 11 §5: the member table's two cells. They are the round's
        // DATA-ROW idiom — Consolas/Bold/16, spelled inline exactly as the LQA
        // report and schedule rows spell it (§4) — and 16 is not what any
        // promoted style carries (CellValue is 18). Ledgered rather than
        // styled, so the two round-11 tables stay one typography.
        (AddressCard, "{Binding NumberText}", "round 11 §5 member row: the Consolas/Bold/16 data idiom"),
        (AddressCard, "{Binding AddressText}", "round 11 §5 member row: the Consolas/Bold/16 data idiom"),
        // The modem card's preset picker digit: §3's SpinnerDigit manifest
        // names the channel, HOP-net, ALE-group and VFO digits — not this one.
        // Recorded as a manifest omission, eligible for a later round.
        (ModemCard, "{Binding PickedPresetText}", "not in §3's SpinnerDigit manifest"),
        // The SSB channel editor's two digit spinners: same omission.
        (SsbSettings, "{Binding PickedTensText}", "not in §3's P3 file manifest"),
        (SsbSettings, "{Binding PickedUnitsText}", "not in §3's P3 file manifest"),
        // The console TERMINAL AREA — the ledger's own exemption (§3 narrows
        // the console carve-out to the rows, their typography, the filter and
        // the absence of headers; only the TOOLBAR buttons were brought in).
        (RadioPage, "paused", "console terminal area — ledger exemption"),
        (RadioPage, "{Binding Timestamp}", "console terminal area — ledger exemption"),
        (RadioPage, "{Binding Badge}", "console terminal area — ledger exemption"),
        // D18(b): this cell is a ConsoleLogLabel now (the text-selection
        // scoping). The exemption is unchanged — and the sweep below reads the
        // subclass too, so changing the TYPE could not be used to slip a log
        // cell out of the scan.
        (RadioPage, "{Binding Text}", "console terminal area — ledger exemption"),
    ];

    /// <summary>The ValueDisplay widths §3's CLOSED consumer manifest did NOT
    /// list, with the reason each survives as a literal. Same discipline as
    /// the font ledger: the scan asserts the survivors are exactly these.</summary>
    public static IReadOnlyList<(string File, string Why)> LiteralWidthLedger =>
    [
        // The ALE settings pane's three numeric displays (64 each) were never
        // in §3's consumer manifest — its P3 entry is the Refresh deletion
        // alone. Recorded as a manifest omission, eligible for a later round.
        (AleSettings, "three 64-dp displays, absent from §3's closed manifest"),
        // The modem card's baud wheel display (72): §3 assigns ValueWidthStd
        // to SsbPaneView's BFO and to nothing else this round.
        (ModemCard, "baud wheel display 72, absent from §3's closed manifest"),
    ];

    [Fact]
    public void TheOnlyInlineFontCopiesLeftInTheP3Files_AreTheLedgeredOnes()
    {
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in P3Files)
            // D18(b): "Label" AND the console log's Label SUBCLASS — a derived
            // label is still a label for this scan, and a type change must not
            // be a way out of it.
            foreach (var label in Load(file).Descendants()
                         .Where(e => e.Name.LocalName is "Label" or "ConsoleLogLabel"))
            {
                bool inline = new[] { "FontSize", "FontFamily", "FontAttributes" }
                    .Any(p => PropertyValue(label, p) is not null);
                if (!inline) continue;

                var text = PropertyValue(label, "Text") ?? "(no text)";
                if (InlineCopyLedger.Any(l => l.File == file && l.Text == text))
                {
                    seen.Add(file + "|" + text);
                    continue;
                }
                offenders.Add($"{Path.GetFileName(file)}: '{text}' still sets a font inline");
            }

        Assert.Empty(offenders);

        // The ledger is not a wish list: every entry must still be REAL, or a
        // deleted exemption would silently widen the scan's blind spot.
        foreach (var (file, text, why) in InlineCopyLedger)
            Assert.Contains(file + "|" + text, seen);
        _ = InlineCopyLedger.Select(l => l.Why).ToList();
    }

    [Fact]
    public void TheInlineCopyScanner_ActuallySeesACopy_AndACleanSite()
    {
        // Anti-vacuity for the scan above: a reader that saw no inline
        // properties would report "no offenders" forever. Proven on a
        // synthetic document containing one of each, in both spellings.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <Label Text="dirty" FontFamily="Consolas" />
                <Label Text="alsoDirty"><Label.FontSize>30</Label.FontSize></Label>
                <Label Text="clean" Style="{StaticResource Caption}" />
              </VerticalStackLayout>
            </ContentView>
            """);

        var labels = markup.Root!.Descendants().Where(e => e.Name.LocalName == "Label").ToList();
        Assert.Equal("Consolas", PropertyValue(labels[0], "FontFamily"));
        Assert.Equal("30", PropertyValue(labels[1], "FontSize"));
        Assert.Null(PropertyValue(labels[2], "FontFamily"));
        Assert.Null(PropertyValue(labels[2], "FontSize"));
        Assert.Null(PropertyValue(labels[2], "FontAttributes"));
    }

    [Fact]
    public void NoP3File_StillSpellsAPromotedWidthAsALiteral()
    {
        // The width half of the same drift: a ValueDisplay in a P3 file must
        // reference a KEY, never re-spell 64 / 96 / 110. (SegmentWidth's
        // literals are §4's station-grid problem and live in the Operate
        // files, which this scan does not reach.)
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in P3Files)
            foreach (var border in Load(file).Descendants().Where(e =>
                (PropertyValue(e, "Style") ?? "").Contains("ValueDisplay", StringComparison.Ordinal)))
            {
                var width = PropertyValue(border, "WidthRequest");
                if (width is null) continue;
                if (width.StartsWith("{StaticResource", StringComparison.Ordinal)) continue;
                if (LiteralWidthLedger.Any(l => l.File == file)) { seen.Add(file); continue; }
                offenders.Add($"{Path.GetFileName(file)}: ValueDisplay width is the literal {width}");
            }

        Assert.Empty(offenders);

        // The ledger is not a wish list: a stale entry would silently widen
        // the scan's blind spot, so every exemption must still be real.
        foreach (var (file, _) in LiteralWidthLedger) Assert.Contains(file, seen);

        // Anti-vacuity: the scan really found ValueDisplays to check.
        Assert.NotEmpty(P3Files.SelectMany(f => Load(f).Descendants())
            .Where(e => (PropertyValue(e, "Style") ?? "")
                .Contains("ValueDisplay", StringComparison.Ordinal)));
    }

    [Fact]
    public void ThePropertyReader_SeesBothWaysAPropertyCanBeSet()
    {
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Border WidthRequest="96" />
              <Border><Border.WidthRequest>96</Border.WidthRequest></Border>
              <Border />
            </ContentView>
            """);

        var borders = markup.Descendants().Where(e => e.Name.LocalName == "Border").ToList();
        Assert.Equal("96", PropertyValue(borders[0], "WidthRequest"));
        Assert.Equal("96", PropertyValue(borders[1], "WidthRequest"));
        Assert.Null(PropertyValue(borders[2], "WidthRequest"));
    }

    // ---- readers -------------------------------------------------------------

    private static string? PropertyValue(XElement element, string property)
        => element.Attribute(property)?.Value
           ?? element.Elements()
               .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)
               ?.Value;

    private static readonly Dictionary<string, XDocument> Parsed = [];

    private static XDocument Load(string relative)
    {
        lock (Parsed)
        {
            if (Parsed.TryGetValue(relative, out var cached)) return cached;
            var path = Path.Combine(FindRepoRoot(), relative);
            Assert.True(File.Exists(path), "markup missing: " + relative);
            var document = XDocument.Load(path);
            Parsed[relative] = document;
            return document;
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
