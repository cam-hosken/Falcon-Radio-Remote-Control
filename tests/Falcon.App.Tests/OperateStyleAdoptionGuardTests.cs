using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// UI tweaks round 10 (§3) — the display constitution's ADOPTIONS on the
/// OPERATE surfaces (phase P2: SpineView, SsbPaneView, HopPaneView,
/// AlePaneView, OperatePage, ModeSettingsPage, AppShell), pinned against the
/// markup. StyleVocabularyGuardTests pins that the styles EXIST and say the
/// right things; this pins that these views actually USE them.
///
/// <para><b>Why both halves are needed.</b> A promoted style with no consumers
/// is a comment. The failure mode is not deletion — it is drift: a later edit
/// re-adds <c>FontAttributes="Bold"</c> beside the style, or replaces
/// <c>Style="{StaticResource SpinnerDigit}"</c> with the four inline
/// properties it stands for, and nothing notices because the pixels are
/// identical THAT DAY. The next change to the style then silently misses
/// those sites, which is exactly the drift §3 exists to end.</para>
///
/// <para><b>Structural, never substring.</b> Every check reads a named XML
/// attribute (or its property-element twin). This matters more than usual
/// here: the new style key <c>StatusText</c> is spelled the same as several
/// ViewModel properties bound as <c>Text="{Binding StatusText}"</c>. A
/// text-matching guard would count those as adoptions and pass while nothing
/// had been adopted at all — so <c>Style</c> is read as <c>Style</c>, and the
/// last test in this file proves the distinction on a synthetic document that
/// contains both.</para>
///
/// <para><b>The manifests here are CLOSED</b> (plan invariant 3): these sites
/// and no others. A control that MATCHES a §3 rule but is not LISTED stays
/// untouched — so the inline-copy scan below carries a small, explicit LEDGER
/// of the copies §3 deliberately did not list, and asserts the set of survivors
/// is exactly that ledger. Both directions fail: a new inline copy, and a
/// ledger entry that quietly disappeared.</para>
///
/// <para>ACCEPTED LIMITATION, as everywhere in this house style: a value
/// supplied indirectly (implicit style, trigger, code-behind, platform
/// override) is invisible. Accidents are caught; adversarial construction is
/// backstopped by review and the bench.</para>
/// </summary>
public class OperateStyleAdoptionGuardTests
{
    private static readonly string Spine =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "SpineView.xaml");
    private static readonly string SsbPane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "SsbPaneView.xaml");
    private static readonly string HopPane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "HopPaneView.xaml");
    private static readonly string AlePane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "AlePaneView.xaml");
    private static readonly string OperatePage =
        Path.Combine("src", "Falcon.App", "Views", "OperatePage.xaml");
    private static readonly string ModeSettingsPage =
        Path.Combine("src", "Falcon.App", "Views", "ModeSettingsPage.xaml");
    private static readonly string AppShell =
        Path.Combine("src", "Falcon.App", "AppShell.xaml");

    /// <summary>The seven files phase P2 owns. The inline-copy scan runs over
    /// exactly these — the settings surfaces keep their copies until P3 deletes
    /// them with their own owners, and a scan that reached into them would fail
    /// on work this phase is not allowed to do.</summary>
    private static IEnumerable<string> P2Files =>
        [Spine, SsbPane, HopPane, AlePane, OperatePage, ModeSettingsPage, AppShell];

    // ---- §3: the adoption manifest (closed) ------------------------------------

    /// <summary>Every §3 site this phase adopts: file · the Text that
    /// identifies the control · the style it must now carry. The Text is the
    /// raw attribute value, so a bound label is identified by its BINDING —
    /// the one thing about it that does not change with wording.</summary>
    public static TheoryData<string, string, string> AdoptedLabels => new()
    {
        // SpineView — the STATUS tier.
        { Spine, "{Binding Session.PortDisplay}", "StatusText" },
        { Spine, "{Binding Session.PhaseText}", "StatusText" },
        { Spine, "{Binding Status.TuneChipText}", "StatusText" },
        { Spine, "{Binding Status.KeylineText}", "StatusText" },
        { Spine, "⚠ thermal cutback", "StatusText" },
        // SpineView + ModeSettingsPage — the row headings.
        { Spine, "MODE", "SubHeading" },
        { Spine, "PWR", "SubHeading" },
        { ModeSettingsPage, "MODE", "SubHeading" },
        // AppShell — the title.
        { AppShell, "FalconRC", "StatusText" },
        // The two placeholder twins.
        { OperatePage, "Mode pane", "EmptyPaneHint" },
        { ModeSettingsPage, "Mode settings", "EmptyPaneHint" },
        // SsbPaneView — the digit sites and the unit labels.
        { SsbPane, "{Binding Ssb.Channel.TensText}", "SpinnerDigit" },
        { SsbPane, "{Binding Ssb.Channel.UnitsText}", "SpinnerDigit" },
        // HopPaneView.
        { HopPane, "{Binding Hop.PickedNetText}", "SpinnerDigit" },
        { HopPane, "{Binding Hop.HopnumText}", "CellValue" },
        // ROUND 13 §4 A2 (item 10 + List_Invalid, owner 2026-08-19): both HOP
        // chip entries move. The sync label lost its `StringFormat='SYNC: {0}'`
        // — the prose is self-identifying — and the badge lost the raw wire
        // token (constitution §3.2: prose on the chip, the raw line stays in
        // the Console). The STYLE half of each entry is unchanged; only the
        // identifying Text moved, which is exactly what this manifest pins.
        { HopPane, "{Binding Hop.SyncChipText}", "StatusText" },
        { HopPane, "Generating Hopset…", "StatusText" },
        { HopPane, "Net list invalid", "StatusText" },
    };

    [Theory]
    [MemberData(nameof(AdoptedLabels))]
    public void EachAdoptedSite_CarriesItsStyle_AndNoInlineFontCopyBesideIt(
        string file, string text, string style)
    {
        var label = Load(file).Descendants()
            .Single(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == text);

        Assert.Equal($"{{StaticResource {style}}}", PropertyValue(label, "Style"));

        // The other half of an adoption: the inline properties the style now
        // supplies are GONE. Leaving them is how a site stops following the
        // style it appears to use.
        foreach (var property in new[] { "FontSize", "FontFamily", "FontAttributes" })
            Assert.Null(PropertyValue(label, property));
    }

    [Fact]
    public void TheStateColours_StayInline_WhereAStateDrivesThem()
    {
        // §3 is explicit that StatusText is the FONT role only: the warn and
        // on-chip colours are this control's STATE and stay at the use site.
        // Pinned so "finish the adoption" cannot be read as "move the colour
        // into the style", which would flatten three distinct states into one.
        var cutback = LabelWithText(Spine, "⚠ thermal cutback");
        Assert.Contains("WarnTextLight", PropertyValue(cutback, "TextColor") ?? "");
        Assert.Contains("WarnTextDark", PropertyValue(cutback, "TextColor") ?? "");

        var generating = LabelWithText(HopPane, "Generating Hopset…");
        Assert.Contains("WarnTextLight", PropertyValue(generating, "TextColor") ?? "");

        // ROUND 13 §4 A2: the badge's TEXT is humanized; its on-chip COLOR is
        // the thing this pin is about and did NOT move.
        var invalid = LabelWithText(HopPane, "Net list invalid");
        Assert.Contains("OnAccentColor", PropertyValue(invalid, "TextColor") ?? "");

        var keyline = LabelWithText(Spine, "{Binding Status.KeylineText}");
        Assert.Contains("SecondaryText", PropertyValue(keyline, "TextColor") ?? "");
    }

    [Fact]
    public void TheMhzUnitLabels_AreCaptionTier_BothOfThem()
    {
        // §3: SsbPane "MHz" ×2, SecondaryLabel → Caption. Asserted as a SET
        // with its count, because "both of them" is the contract — the RX and
        // TX rows are meant to stay identical, and fixing one is the drift.
        var labels = Load(SsbPane).Descendants()
            .Where(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == "MHz")
            .ToList();

        Assert.Equal(2, labels.Count);
        Assert.All(labels, l => Assert.Equal("{StaticResource Caption}", PropertyValue(l, "Style")));
    }

    [Fact]
    public void TheVfoDigitCellsAndDecimalGlyphs_AreSpinnerDigit_InBothStrips()
    {
        // Four elements, two per strip: the digit cell and the decimal glyph.
        // Identified by their template bindings rather than position, so the
        // pin survives a markup reshuffle.
        var root = Load(SsbPane);

        var cells = root.Descendants()
            .Where(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == "{Binding Text}")
            .ToList();
        Assert.Equal(2, cells.Count);       // RX and TX item templates

        var points = root.Descendants()
            .Where(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == ".")
            .ToList();
        Assert.Equal(2, points.Count);

        foreach (var label in cells.Concat(points))
        {
            Assert.Equal("{StaticResource SpinnerDigit}", PropertyValue(label, "Style"));
            foreach (var property in new[] { "FontSize", "FontFamily", "FontAttributes" })
                Assert.Null(PropertyValue(label, property));
        }
    }

    [Fact]
    public void TheSpinnerWidths_StayAtTheUseSites()
    {
        // §3: "WIDTHS stay at the use sites — the digit strips size their own
        // columns." The style is the font role only, so adopting it must not
        // have taken a width with it. The HOP net digit is the one spinner
        // that carries an explicit width.
        Assert.Equal("44", PropertyValue(LabelWithText(HopPane, "{Binding Hop.PickedNetText}"), "WidthRequest"));
    }

    // ---- §3: the ValueWidth* consumer manifest (this phase's share) ------------

    [Fact]
    public void TheBfoDisplay_TakesTheNamedStdWidth_AndDropsItsInlineConsolas()
    {
        var display = BorderWithDescription(SsbPane, "BFO offset value");
        Assert.Equal("{StaticResource ValueWidthStd}", PropertyValue(display, "WidthRequest"));

        var value = display.Elements().Single(e => e.Name.LocalName == "Label");
        Assert.Equal("{StaticResource ValueDisplayText}", PropertyValue(value, "Style"));
        Assert.Null(PropertyValue(value, "FontFamily"));      // the style carries it now
    }

    // ROUND 14 A3 removed the HOP pane's modem row and replaced its width pin
    // with an ABSENCE pin. CLONE-FIELD ROUND 2 F10 (owner ruling R-C,
    // 2026-08-21) puts the row BACK, so the absence pin is INVERTED below into
    // a PRESENCE pin. The removal's grounding was not wrong, it was
    // INCOMPLETE: round 13's T1 probe asked `HOP>` for presets 0-6 — the half
    // that prompt does not have — and read the refusal as wholesale. Probes
    // P5-P5d2 asked for 7-9 and got a working modem surface
    // (bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl,
    // p5d2-hop-modem-select-enabled-20260821-183248.jsonl).

    [Fact]
    public void TheHopPane_CARRIES_TheModemRow_BackInItsOriginalPosition()
    {
        // The INVERSE of round 14 A3's absence pin (F10). Structural, on the
        // same two needles that identified the row when it was deleted: the
        // accessibility description its display carries and the two
        // ModemViewModel command bindings its chevrons carry.
        var hop = Load(HopPane).Descendants().ToList();

        Assert.Contains(hop, e => (PropertyValue(e, "SemanticProperties.Description") ?? "")
            == "Active modem preset");
        foreach (var command in new[] { "{Binding Modem.ModemDownCommand}", "{Binding Modem.ModemUpCommand}" })
            Assert.Contains(hop, e => (PropertyValue(e, "Command") ?? "") == command);

        // ITS ORIGINAL POSITION, by SIBLING INDEX — the plan's own wording, and
        // the only structural way to say "back where it was" without pinning
        // the whole file. In `673c526^` the modem row was the LAST child of the
        // HOP status card's VerticalStackLayout, directly after the sync row; a
        // row re-added anywhere else (the top of the card, a different card)
        // satisfies the three assertions above and fails here.
        var (hopIndex, hopCount) = ModemRowSiblingIndex(HopPane);
        Assert.Equal(hopCount - 1, hopIndex);
        Assert.Equal("{Binding Hop.SendSyncCommand}", CommandInSiblingBefore(HopPane));

        // ANTI-VACUITY, AGAINST THE SSB PANE'S ROW: the same reader finds the
        // SSB pane's modem row at a REAL index, and there it is NOT last (the
        // BFO row follows it). So the reader distinguishes positions rather
        // than answering "last" to everything, and a rename that made the row
        // unfindable would fail here instead of turning the pin into a
        // formality.
        var (ssbIndex, ssbCount) = ModemRowSiblingIndex(SsbPane);
        Assert.True(ssbIndex >= 0, "the SSB pane's modem row was not found by the same reader");
        Assert.True(ssbIndex < ssbCount - 1,
            "the SSB pane's modem row is now last too, so 'last' no longer distinguishes anything");
    }

    /// <summary>The modem row's index among its parent's children, and the
    /// child count. (−1, 0) when the pane has no modem row. The row is FOUND by
    /// the display's accessibility description — the same needle the round-14
    /// absence pin used, so a rename breaks both together — and walked UP to the
    /// <c>Grid</c> that is a direct child of the card's stack.</summary>
    private static (int Index, int Count) ModemRowSiblingIndex(string pane)
    {
        var row = ModemRowElement(pane);
        if (row?.Parent is not { } parent) return (-1, 0);
        var siblings = parent.Elements().ToList();
        return (siblings.IndexOf(row), siblings.Count);
    }

    /// <summary>The <c>Command</c> of the first descendant of the sibling
    /// immediately BEFORE the modem row — what pins "directly after the sync
    /// row" without quoting the markup.</summary>
    private static string? CommandInSiblingBefore(string pane)
    {
        var row = ModemRowElement(pane);
        if (row?.Parent is not { } parent) return null;
        var siblings = parent.Elements().ToList();
        int i = siblings.IndexOf(row);
        return i <= 0
            ? null
            : siblings[i - 1].Descendants()
                .Select(e => PropertyValue(e, "Command"))
                .LastOrDefault(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>The modem ROW: the ancestor <c>Grid</c> of the "Active modem
    /// preset" display that sits directly inside the card's stack.</summary>
    private static XElement? ModemRowElement(string pane)
    {
        var node = Load(pane).Descendants().FirstOrDefault(
            e => (PropertyValue(e, "SemanticProperties.Description") ?? "") == "Active modem preset");
        while (node is not null
               && !(node.Name.LocalName == "Grid"
                    && node.Parent is { } p && p.Name.LocalName == "VerticalStackLayout"))
            node = node.Parent;
        return node;
    }

    [Fact]
    public void TheSsbModemDisplay_KEEPS_ItsLiteral96_RecordedDeviation()
    {
        // RECORDED DEVIATION (§3's ValueWidthWide manifest lists this display).
        // ChevronGeometryGuardTests.TheModemDisplay_KeepsItsFixed96DpWidth
        // reads THIS attribute with int.TryParse, and that suite is pinned
        // "green UNTOUCHED" for this phase — a StaticResource reference reads
        // back as no-width and fails it. The literal therefore stays, and this
        // pin says so OUT LOUD so the survivor is a decision on the record
        // rather than an oversight. It also fixes the value: 96 is
        // ValueWidthWide's value, so the two cannot drift apart while they are
        // spelled differently. Resolving the conflict (teaching that pin to
        // resolve resource keys) retires this test with it.
        var display = BorderWithDescription(SsbPane, "Active modem preset");
        Assert.Equal("96", PropertyValue(display, "WidthRequest"));
    }

    // ---- "No inline copies of the promoted styles" (P2 files) ------------------

    /// <summary>The copies §3's CLOSED manifests deliberately left in place —
    /// each one a control that MATCHES a promoted style's property set but is
    /// NOT on that style's site list, and therefore stays untouched under
    /// invariant 3. Named here so the survivors are a decision, not a
    /// leftover.</summary>
    private static readonly string[] LedgeredSurvivors =
    [
        // CellValue's set (18 / Bold / Consolas): the ALE link BANNER. §3's
        // CellValue list is exactly "HopPaneView Hopnum" — the banner is not a
        // table cell, it is the pane's headline, and it was not listed.
        "AlePaneView: {Binding Ale.BannerText}",
        // The Stage-9 INBOX placeholder ("AlePaneView: Inbox") left this
        // ledger 2026-08-24: Stage 9 closed (linked-amd round) and the stub
        // became the real received-message list.
    ];

    [Fact]
    public void NoInlineCopiesOfThePromotedStyles_SurviveInTheP2Files_BeyondTheLedger()
    {
        var found = new List<string>();

        foreach (var file in P2Files)
            foreach (var element in Load(file).Descendants())
                if (InlineCopyOf(element) is not null)
                    found.Add($"{NameOf(file)}: {PropertyValue(element, "Text") ?? "(no Text)"}");

        Assert.Equal(LedgeredSurvivors.Order(), found.Order());
    }

    [Fact]
    public void TheAleRowIdiom_IsNotCountedAsACopy_ItIsALedgeredExemption()
    {
        // §3's ledger: "Station-name font sites (§4) keep INLINE
        // Consolas/Bold/16 — they are ALE-row idiom, listed here so 'no inline
        // copies' pins exclude them." The exclusion is not a special case in
        // the detector: 16 pt is not any promoted style's size, so the idiom
        // simply is not a copy of anything. Pinned positively (the cells DO
        // carry the inline idiom — AlePaneMarkupGuardTests asserts each one)
        // and negatively here, so a future promoted style at 16 pt has to
        // notice this collision instead of silently condemning eight cells.
        var idiomCells = Load(AlePane).Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && PropertyValue(e, "FontFamily") == "Consolas"
                && PropertyValue(e, "FontAttributes") == "Bold"
                && PropertyValue(e, "FontSize") == "16")
            .ToList();

        // Round 11 §4 doubles the idiom's population: the LQA report and the
        // LQA schedule mirror are ALE-row tables too, and both are TWO-LINE, so
        // each contributes its cells from both lines. The count moves with the
        // markup IN-PHASE (invariant 5) — it is the ledger, and a silent drift
        // in either direction is the thing it catches.
        //
        // ROUND 13 §4 A3: 18 → 15. The owner's one-line ruling (2026-08-20)
        // deleted the station template's PHONE field line, and its three cells
        // went with it. This guard lives OUTSIDE AlePaneMarkupGuardTests, so it
        // was not in the plan's enumeration of the pins the ruling breaks — it
        // is updated here, in the same commit, because it is exactly the drift
        // the ledger exists to catch and a count is not evidence of anything
        // once it stops being re-derived.
        //
        // ROUND 15 §17: 15 -> 16. The ONE station template became TWO cards
        // (Nets above Stations) and each row lost Type and Chan grp for the
        // ASSOC SELF cell - 3 cells on one template becomes 2 on each of two.
        // Re-derived, not adjusted:
        //   2 selfs
        // + 2 nets row (NAME|ASSOC SELF) + 2 stations row (the same geometry)
        // + 3 report line 1 (CHAN|RX|TX) + 3 report line 2 (SCORE|MEAS|RCVD)
        // + 2 schedule line 1 (KIND|ADDRESS) + 2 schedule line 2 (INTERVAL|START)
        //
        // BROADCAST ROUND (plan-ale-broadcast-round.md F2/F3, 2026-08-24):
        // 16 -> 20. F2 folded the schedule's two lines into ONE, which moved
        // its four cells without changing their number; F3 added the two PINNED
        // broadcast rows, which are fixed markup rather than a template and
        // therefore contribute their cells LITERALLY. This guard lives outside
        // AlePaneMarkupGuardTests, so it is updated here, in the same commit,
        // for the reason the round-13 note gives: a count stops being evidence
        // the moment it stops being re-derived.
        //   2 selfs
        // + 2 nets row + 2 stations row
        // + 3 report line 1 + 3 report line 2
        // + 4 schedule row (KIND|ADDRESS|INTERVAL|START — one line now)
        // + 2 pinned ANY row (ANY|—) + 2 pinned ALL row (ALL|—)
        // + 1 Inbox row FROM (linked-amd round — the message text cell is
        //   Consolas 16 UNBOLDED, deliberately not the idiom, so one cell)
        // + 1 Heard-stations row STATION (owner design 2026-08-24 — its
        //   channels cell is likewise Consolas 16 UNBOLDED, its time a Caption)
        Assert.Equal(22, idiomCells.Count);
        Assert.All(idiomCells, c => Assert.Null(InlineCopyOf(c)));
    }

    [Fact]
    public void TheCopyDetector_FiresOnEveryPromotedSet_AndOnNothingElse()
    {
        // Anti-vacuity, and the ONE place the StatusText spelling collision is
        // settled: a Label whose TEXT is bound to a ViewModel property called
        // StatusText is not an adoption and not a copy — only the Style
        // attribute means anything. A substring guard fails this document.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Label FontSize="30" FontAttributes="Bold" FontFamily="Consolas" Text="spinner copy" />
              <Label FontSize="18" FontAttributes="Bold" FontFamily="Consolas" Text="cellvalue copy" />
              <Label FontSize="18" Style="{StaticResource SecondaryLabel}" Text="hint copy" />
              <Label Style="{StaticResource ValueDisplayText}" FontFamily="Consolas" Text="display copy" />
              <Label Style="{StaticResource SpinnerDigit}" Text="clean adoption" />
              <Label FontFamily="Consolas" FontAttributes="Bold" FontSize="16" Text="ale row idiom" />
              <Label Text="{Binding StatusText}" />
              <!-- <Label FontSize="30" FontAttributes="Bold" FontFamily="Consolas" Text="commented" /> -->
            </ContentView>
            """);

        var labels = markup.Descendants().Where(e => e.Name.LocalName == "Label").ToList();
        Assert.Equal(7, labels.Count);                   // the comment is not an element

        Assert.Equal("SpinnerDigit", InlineCopyOf(labels[0]));
        Assert.Equal("CellValue", InlineCopyOf(labels[1]));
        Assert.Equal("EmptyPaneHint", InlineCopyOf(labels[2]));
        Assert.Equal("ValueDisplayText", InlineCopyOf(labels[3]));
        Assert.Null(InlineCopyOf(labels[4]));            // uses the style
        Assert.Null(InlineCopyOf(labels[5]));            // the §4 ALE-row idiom
        Assert.Null(InlineCopyOf(labels[6]));            // {Binding StatusText} is DATA, not a style
    }

    [Fact]
    public void TheAdoptionReader_DistinguishesStyleFromBoundText()
    {
        // The same collision from the ADOPTION side: the reader must find the
        // style on the element that SETS it and must not find it on the
        // element that merely displays a property of the same name.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Label Style="{StaticResource StatusText}" Text="adopted" />
              <Label Text="{Binding StatusText}" />
              <Label><Label.Style>{StaticResource StatusText}</Label.Style></Label>
            </ContentView>
            """);

        var labels = markup.Descendants().Where(e => e.Name.LocalName == "Label").ToList();

        Assert.Equal("{StaticResource StatusText}", PropertyValue(labels[0], "Style"));
        Assert.Null(PropertyValue(labels[1], "Style"));                       // bound TEXT, not a style
        Assert.Equal("{StaticResource StatusText}", PropertyValue(labels[2], "Style"));   // property-element form
    }

    /// <summary>The promoted style whose PROPERTY SET this element spells out
    /// inline, or null. One detector, four sets — the §3 styles whose identity
    /// is a font combination distinctive enough to recognise.
    /// <c>StatusText</c> is deliberately NOT here: its whole content is
    /// <c>FontAttributes="Bold"</c>, which every emphasised label in the app
    /// shares, so a "copy" of it is not a recognisable thing and a detector
    /// for it would flag half the markup.</summary>
    private static string? InlineCopyOf(XElement e)
    {
        string? size = PropertyValue(e, "FontSize");
        string? family = PropertyValue(e, "FontFamily");
        string? weight = PropertyValue(e, "FontAttributes");
        string style = PropertyValue(e, "Style") ?? "";

        // ValueDisplayText gained Consolas IN-STYLE (P1): an element that uses
        // the style AND still names the family is the copy §3 deletes.
        if (style.Contains("ValueDisplayText", StringComparison.Ordinal) && family is not null)
            return "ValueDisplayText";

        if (size == "30" && weight == "Bold" && family == "Consolas") return "SpinnerDigit";
        if (size == "18" && weight == "Bold" && family == "Consolas") return "CellValue";

        // EmptyPaneHint is 18 + SECONDARY colour, whichever way the colour is
        // reached (the twins wore it as the SecondaryLabel style).
        if (size == "18" && family is null
            && (style.Contains("SecondaryLabel", StringComparison.Ordinal)
                || (PropertyValue(e, "TextColor") ?? "").Contains("SecondaryText", StringComparison.Ordinal)))
            return "EmptyPaneHint";

        return null;
    }

    // ==== ROUND 13 §4 A2: the HOP sync ROW ==================================

    [Fact]
    public void TheHopSyncRow_IsAGrid_WithTheChipFixedAndSendSyncPinnedRight()
    {
        // ITEM 10 (owner 2026-08-19). The row used to be a
        // HorizontalStackLayout around a self-sizing chip, so Send Sync slid
        // sideways every time the sync wording changed length. The fix is
        // structural and therefore invisible to every VM test: a Grid whose
        // residual STAR column sits between the two controls, and a chip with a
        // fixed width. Both halves are needed — either alone still moves
        // something — so both are pinned here, on the row located by the chip
        // it contains.
        var chip = BorderWithDescription(HopPane, "Hop sync status");
        var row = chip.Parent!;

        Assert.Equal("Grid", row.Name.LocalName);
        Assert.Equal("Auto,*,Auto", PropertyValue(row, "ColumnDefinitions"));
        Assert.Equal("12", PropertyValue(row, "ColumnSpacing"));

        // The chip owns col 0 (implicit) and takes the NAMED width…
        Assert.Null(PropertyValue(chip, "Grid.Column"));
        Assert.Equal("{StaticResource SyncChipWidth}", PropertyValue(chip, "WidthRequest"));

        // …and Send Sync sits in col 2, past the star, with NO width of its own
        // (it is ACTION class — RefreshButtonWidthGuardTests owns that rule;
        // repeated here because "no width" is what makes the star column the
        // thing that positions it).
        var sendSync = row.Elements().Single(e =>
            e.Name.LocalName == "Button"
            && PropertyValue(e, "Command") == "{Binding Hop.SendSyncCommand}");
        Assert.Equal("2", PropertyValue(sendSync, "Grid.Column"));
        Assert.Null(PropertyValue(sendSync, "WidthRequest"));

        // Nothing occupies the star column, or it would consume the residual
        // the two controls are being held apart by.
        Assert.DoesNotContain(row.Elements(), e => PropertyValue(e, "Grid.Column") == "1");
    }

    [Fact]
    public void TheSyncChip_KEEPS_ItsFourStateTriggers_BorderFillAndLabelText()
    {
        // A2 audit round 1, MINOR 2. The row rework moved the chip into a Grid
        // and gave it a width; what it must NOT have done is disturb the state
        // colours, and nothing could see that — the auditor deleted BOTH
        // IsSyncFailed triggers (Border fill and Label text) and the whole
        // suite stayed green, leaving "Sync failed" rendering in the ordinary
        // surface colours with no test objecting.
        //
        // FOUR triggers, two flags × two elements, each with the setter that
        // makes it mean something. Pinned as exact CONTENT in document order,
        // so this cannot pass vacuously: a reader that found nothing would
        // return an empty list and fail against two expected entries.
        var chip = BorderWithDescription(HopPane, "Hop sync status");
        var label = chip.Elements().Single(e => e.Name.LocalName == "Label");

        (string Binding, string Property, string Value)[] borderFill =
        [
            ("{Binding Hop.IsInSync}", "BackgroundColor", "{StaticResource ChipOkColor}"),
            ("{Binding Hop.IsSyncFailed}", "BackgroundColor", "{StaticResource ChipErrColor}"),
        ];
        Assert.Equal(borderFill, DataTriggersOn(chip));

        // The label's pair exists because the two FILLS are dark in both
        // themes: without them the text keeps its theme colour and the chip
        // reads as a dark box with unreadable text (App.xaml's on-accent rule).
        (string Binding, string Property, string Value)[] labelText =
        [
            ("{Binding Hop.IsInSync}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Hop.IsSyncFailed}", "TextColor", "{StaticResource OnAccentColor}"),
        ];
        Assert.Equal(labelText, DataTriggersOn(label));
    }

    /// <summary>The <c>DataTrigger</c>s declared on an element ITSELF (its own
    /// <c>&lt;X.Triggers&gt;</c> block, never a descendant's), flattened to
    /// binding · setter property · setter value. Reading only the element's own
    /// block is what lets the Border's pair and the Label's pair be told apart
    /// — they bind the same two flags.</summary>
    private static IReadOnlyList<(string Binding, string Property, string Value)> DataTriggersOn(
        XElement element)
        =>
        [
            .. element.Elements()
                .Where(e => e.Name.LocalName == element.Name.LocalName + ".Triggers")
                .SelectMany(block => block.Elements().Where(e => e.Name.LocalName == "DataTrigger"))
                .SelectMany(trigger => trigger.Elements()
                    .Where(s => s.Name.LocalName == "Setter")
                    .Select(setter => (
                        PropertyValue(trigger, "Binding") ?? "",
                        PropertyValue(setter, "Property") ?? "",
                        PropertyValue(setter, "Value") ?? "")))
        ];

    [Fact]
    public void TheSyncChipWidth_IsAtTheUseSite_NeverOnTheSharedChipStyle()
    {
        // CONSTITUTION §3.3, pinned rather than trusted. Six Borders wear
        // `Chip`: the spine TUNE chip carries its OWN local width
        // (TuneStackWidth 150, aligning it with the Retune button), and the
        // other four — spine keyline, the two ALE chips, the HOP
        // net-list-invalid badge — size themselves. A width setter on the
        // shared style would fit neither width-wanting chip and would silently
        // pad the four that want none. So the style must carry no width, and
        // exactly ONE Border may reference this key.
        var app = Load(Path.Combine("src", "Falcon.App", "App.xaml"));

        var chipStyle = app.Descendants().Single(e =>
            e.Name.LocalName == "Style"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "Chip"));
        Assert.DoesNotContain(chipStyle.Elements(), setter =>
            (PropertyValue(setter, "Property") ?? "").Contains("Width", StringComparison.Ordinal));

        // Anti-vacuity: the SAME reader sees the setters the style really has,
        // so "no width setter" is not a blind scan.
        Assert.Contains(chipStyle.Elements(), setter =>
            PropertyValue(setter, "Property") == "Padding");

        var consumers = Load(HopPane).Descendants()
            .Where(e => (PropertyValue(e, "WidthRequest") ?? "")
                .Contains("SyncChipWidth", StringComparison.Ordinal))
            .ToList();
        Assert.Single(consumers);
        Assert.Equal("Border", consumers[0].Name.LocalName);
    }

    [Fact]
    public void TheTuneChipCarriesTheAutomationId_ItsBenchInstrumentLocatesItBy()
    {
        // ROUND 15 N3 (plan §3.6 rung 2, critic F23): `bench/uia-tune-chip.ps1`
        // finds the chip by `AutomationId` — a Border's
        // SemanticProperties.Description is NOT verifiably the UIA Name on
        // MAUI 10, so the id is the deterministic locator. Deleting or
        // renaming it silently blinds the instrument, which is exactly the
        // kind of drift a markup guard exists to catch.
        var chip = BorderWithDescription(Spine, "Coupler tune status");
        Assert.Equal("TuneChip", PropertyValue(chip, "AutomationId"));

        // …and it is the ONLY AutomationId in the file, so a future one has to
        // be added here consciously rather than inherited by a copy-paste.
        var ids = Load(Spine).Descendants()
            .Select(e => PropertyValue(e, "AutomationId"))
            .Where(v => v is not null)
            .ToList();
        Assert.Equal(["TuneChip"], ids);
    }

    // ---- element selection / XAML reading ---------------------------------------

    private static XElement LabelWithText(string file, string text)
        => Load(file).Descendants()
            .Single(e => e.Name.LocalName == "Label" && PropertyValue(e, "Text") == text);

    private static XElement BorderWithDescription(string file, string description)
        => Load(file).Descendants()
            .Single(e => e.Name.LocalName == "Border"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "SemanticProperties.Description" && a.Value == description));

    /// <summary>A property set as an ATTRIBUTE or as a property ELEMENT — the
    /// round-2 lesson every scan in this repo carries.</summary>
    private static string? PropertyValue(XElement element, string property)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == property)?.Value
            ?? element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)?.Value.Trim();

    private static string NameOf(string file)
        => Path.GetFileNameWithoutExtension(file).Replace(".xaml", "", StringComparison.Ordinal);

    private static XElement Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path).Root!;
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
