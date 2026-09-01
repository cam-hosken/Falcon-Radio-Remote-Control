using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 5 (BF1, BF2, BH1) — the PLACEMENT facts, pinned against the
/// markup in the house source-scan style (RefreshButtonWidthGuardTests /
/// CloningStubTests). Reflection cannot see where a control sits, and the VM
/// tests cannot either: "the Channels card is last" and "Refresh channels
/// lives only in the list tab" are layout decisions the owner made for
/// reasons, and a reshuffle would undo them silently.
///
/// <para><b>Why this parses XML rather than matching text.</b> The same lesson
/// RefreshButtonWidthGuardTests learned the hard way: XAML is well-formed XML,
/// a property can be set as an attribute OR as a property element, and a
/// regex over opening tags sees only one of those. <see cref="XDocument"/>
/// sees both, treats commented-out markup as the non-element it is, and — the
/// part that matters most here — exposes the ANCESTOR chain, which is the only
/// honest way to say "inside the list tab".
///
/// <para><b>Why "which tab" is computed, not asserted by name.</b> Neither
/// sub-tab has an x:Name; both are plain layouts distinguished by a
/// DataTrigger. So the tabs are IDENTIFIED by their contents — the list tab is
/// the subtree that holds the hundred-row CollectionView, the programming tab
/// is the subtree that holds Store — and the test asks whether the button is
/// inside one and outside the other. That survives renames and restyling and
/// fails exactly when the button actually moves.</para>
///
/// <para><b>ACCEPTED LIMITATION</b> (the standing one for every scan in this
/// suite): markup emitted or reparented from code-behind is invisible here.
/// That is adversarial construction rather than a plausible regression; the
/// backstops are the UIA gate evidence and the bench pass.</para>
/// </summary>
public class SettingsPlacementGuardTests
{
    private static readonly string SsbPane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");

    private static readonly string RadioPage =
        Path.Combine("src", "Falcon.App", "Views", "RadioSettingsPage.xaml");

    // ---- CLONE ROUND 12 §9 C4: the MARKUP half of the gate removal ---------

    /// <summary>The second of the two gate LAYERS. The VM half is pinned by
    /// reflection in SsbSettingsViewModelTests (the enabled properties are
    /// gone); this is the markup half, and it exists because either layer
    /// alone still greys the row — a re-added <c>IsEnabled</c> binding would
    /// restore the whole defect with the VM untouched and every VM test green.
    ///
    /// <para>The pin is BROAD on purpose: NO <c>IsEnabled</c> anywhere in the
    /// pane may name a modulation-scoped gate. The FM trio share one container
    /// and CW offset has its own, so naming the two old properties alone would
    /// miss a third spelling of the same idea.</para></summary>
    [Fact]
    public void NoModulationGate_SurvivesInTheSsbSettingsMarkup_C4()
    {
        var enabledBindings = Load(SsbPane).Descendants()
            .Select(e => PropertyValue(e, "IsEnabled"))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        foreach (var forbidden in new[] { "IsFmGroupEnabled", "IsCwOffsetEnabled", "Modulation" })
            Assert.DoesNotContain(enabledBindings,
                v => v.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        // ANTI-VACUITY: the reader really does find IsEnabled bindings in this
        // file — the PANE gate is still there, and C4 did not touch it. An
        // empty list would otherwise make the assertions above meaningless.
        Assert.Contains(enabledBindings,
            v => v.Contains("AreSettingsEnabled", StringComparison.Ordinal));
    }

    // ---- BF1: "Refresh channels" lives ONLY in the Channel list tab ---------

    [Fact]
    public void RefreshChannels_LivesInsideTheChannelListTab_AndNowhereElse()
    {
        var pane = Load(SsbPane);

        var buttons = pane.Descendants().Where(e => e.Name.LocalName == "Button")
            .Where(b => TextOf(b) == "Refresh channels").ToList();
        Assert.Single(buttons);

        var listTab = ListTabRoot(pane);
        var programmingTab = ProgrammingTabRoot(pane);

        Assert.Contains(listTab, buttons[0].Ancestors());
        Assert.DoesNotContain(programmingTab, buttons[0].Ancestors());
    }

    [Fact]
    public void TheTabSubtrees_AreActuallyDistinct_AndEachHoldsWhatIdentifiesIt()
    {
        // Anti-vacuity for the pin above. If ListTabRoot and
        // ProgrammingTabRoot ever resolved to the SAME element (say, the card
        // body), "inside one and outside the other" would be unsatisfiable and
        // the pin would fail loudly — but if they both resolved to something
        // TRIVIAL the pin could pass for the wrong reason. So: two different
        // elements, each containing its own identifying control and not the
        // other's.
        var pane = Load(SsbPane);
        var listTab = ListTabRoot(pane);
        var programmingTab = ProgrammingTabRoot(pane);

        Assert.NotSame(listTab, programmingTab);
        Assert.Contains(listTab.Descendants(), e => IsChannelList(e));
        Assert.DoesNotContain(listTab.Descendants(), e => IsStoreButton(e));
        Assert.Contains(programmingTab.Descendants(), e => IsStoreButton(e));
        Assert.DoesNotContain(programmingTab.Descendants(), e => IsChannelList(e));
    }

    // ---- BF2: the six blue read-back bindings are GONE ----------------------

    [Fact]
    public void NoAppMarkupStillBindsTheDeletedReadBackProperties()
    {
        // The VM test pins that the PROPERTIES are gone; this pins that no
        // BINDING was left pointing at them. MAUI resolves a missing binding
        // path to nothing, silently, so an orphaned binding renders a blank
        // cell forever and no test would otherwise notice.
        var deleted = new[]
        {
            "RxFrequencyReadBack", "TxFrequencyReadBack", "ModeReadBack",
            "BandwidthReadBack", "AgcReadBack", "RxOnlyReadBack",
        };

        var offenders = new List<string>();
        foreach (var file in AppXamlFiles(FindRepoRoot()))
        {
            var text = string.Join("\n", BindingTexts(XDocument.Load(file)));
            foreach (var name in deleted)
                if (text.Contains(name, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: still binds {name}");
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheBindingScanner_ActuallySeesTheRowBindingsThatReplacedThem()
    {
        // Anti-vacuity: a scanner that reads no bindings at all would report
        // "no offenders" forever. Pin the replacement it must be able to see.
        var texts = BindingTexts(XDocument.Load(Path.Combine(FindRepoRoot(), SsbPane))).ToList();

        Assert.Contains(texts, t => t.Contains("ReadBackRow.RxFrequencyText", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("ReadBackRow.AgcWordText", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("ReadBackRow.RxOnlyText", StringComparison.Ordinal));
    }

    // ---- Round 6 (CH): the read-back row sits BESIDE the picker -------------

    [Fact]
    public void ReadBackRow_SharesThePickerGrid_BesideNotBelow()
    {
        // The structural fact behind "squeeze the channel display next to the
        // picker": the read-back block and the picker spinners are children
        // of the SAME Grid, with the read-back in a LATER column — not a
        // sibling stacked underneath. A refactor that moves the row back
        // below the picker changes this ancestry and fails here.
        var pane = XDocument.Load(Path.Combine(FindRepoRoot(), SsbPane)).Root!;

        var readBackCell = pane.Descendants()
            .Single(e => e.Name.LocalName == "Label"
                && (e.Attribute("Text")?.Value ?? "").Contains("ReadBackRow.NumberText", StringComparison.Ordinal));
        var pickerButton = pane.Descendants()
            .First(e => e.Name.LocalName == "Button"
                && (e.Attribute("Command")?.Value ?? "").Contains("TensUpCommand", StringComparison.Ordinal));

        static XElement? NearestColumnGrid(XElement start)
        {
            for (var e = start.Parent; e is not null; e = e.Parent)
                if (e.Name.LocalName == "Grid" && e.Attribute("ColumnDefinitions") is not null) return e;
            return null;
        }

        var sharedGrid = NearestColumnGrid(pickerButton);
        Assert.NotNull(sharedGrid);
        Assert.Contains(readBackCell.Ancestors(), a => ReferenceEquals(a, sharedGrid));

        // Beside = a later column of that grid, not column 0.
        var readBackBlock = readBackCell.Ancestors()
            .First(a => a.Parent is not null && ReferenceEquals(a.Parent, sharedGrid));
        var column = readBackBlock.Attribute("Grid.Column")?.Value;
        Assert.NotNull(column);
        Assert.NotEqual("0", column);
    }

    // ---- BF1 / BH1: the two Refresh-at-the-bottom placements ----------------

    [Fact]
    public void ChannelsCard_IsTheLastCardOnTheSsbPane_WithThePaneRefreshBelowIt()
    {
        var pane = Load(SsbPane);
        var stack = PaneChildren(pane);

        int channelsCard = stack.FindIndex(e => NameOf(e) == "ChannelsCard");
        Assert.True(channelsCard >= 0, "the Channels card is no longer a direct child of the pane stack");

        // Last CARD: no Border (the Card style's element) follows it.
        Assert.DoesNotContain(
            stack.Skip(channelsCard + 1),
            e => e.Name.LocalName == "Border");

        // …and the pane's own Refresh is the last element on the pane.
        // ROUND 10 §6: RELABELED to name its scope — the behavior and the
        // placement are untouched.
        var last = stack[^1];
        Assert.Equal("Button", last.Name.LocalName);
        Assert.Equal("Refresh SSB settings", TextOf(last));
        Assert.Equal("{Binding RefreshSettingsCommand}", PropertyValue(last, "Command"));
    }

    [Fact]
    public void RadioSettings_RefreshSitsBelowTheCloningCard_AsThePagesLastElement()
    {
        // BH1 (owner ruling). Round 4 added the Cloning card UNDER the Refresh
        // button, which quietly broke the standing "Refresh at the bottom"
        // rule; this restores it and stops the next card from doing the same.
        var page = Load(RadioPage);
        var stack = SettingsSectionChildren(page);

        int cloning = stack.FindIndex(e => Descends(e, IsCloningHeading));
        Assert.True(cloning >= 0, "the Cloning card is gone from the Radio settings page");

        // ROUND 10 §6: relabeled "Refresh device settings" (MARKUP ONLY — the
        // VM already queried the battery; the label caught up with it).
        int refresh = stack.FindIndex(e =>
            e.Name.LocalName == "Button" && TextOf(e) == "Refresh device settings");
        Assert.True(refresh >= 0, "the Radio settings Refresh button is gone");

        Assert.True(refresh > cloning,
            "BH1: the Radio settings Refresh must sit BELOW the Cloning card");
        Assert.Equal(stack.Count - 1, refresh);
    }

    // ---- ROUND 9: the modem presets card, rebuilt on the channel model -------

    private static readonly string ModemCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "ModemPresetsView.xaml");

    private static readonly string AddressCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleProgrammingView.xaml");

    [Fact]
    public void ModemPresets_ReadBackRow_SharesThePickerGrid_BesideNotBelow()
    {
        // Ruling 3, the CH shape ported: the picked preset's row is a later
        // COLUMN of the picker's own grid, not a sibling stacked underneath.
        // A refactor that moves it below changes this ancestry and fails here.
        var card = Load(ModemCard).Root!;

        var readBackCell = card.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && (e.Attribute("Text")?.Value ?? "").Contains("PickedRow.NumberText", StringComparison.Ordinal));
        var pickerButton = card.Descendants().First(e =>
            e.Name.LocalName == "Button"
            && (e.Attribute("Command")?.Value ?? "").Contains("PresetUpCommand", StringComparison.Ordinal));

        static XElement? NearestColumnGrid(XElement start)
        {
            for (var e = start.Parent; e is not null; e = e.Parent)
                if (e.Name.LocalName == "Grid" && e.Attribute("ColumnDefinitions") is not null) return e;
            return null;
        }

        var sharedGrid = NearestColumnGrid(pickerButton);
        Assert.NotNull(sharedGrid);
        Assert.Contains(readBackCell.Ancestors(), a => ReferenceEquals(a, sharedGrid));

        var readBackBlock = readBackCell.Ancestors()
            .First(a => a.Parent is not null && ReferenceEquals(a.Parent, sharedGrid));
        var column = readBackBlock.Attribute("Grid.Column")?.Value;
        Assert.NotNull(column);
        Assert.NotEqual("0", column);
    }

    [Fact]
    public void ModemPresets_ReadBackRow_RendersTheSameThreeCellsAsAListRow()
    {
        // The BF2 contract: one projection, two places. If the read-back
        // stopped rendering a cell the list renders (or vice versa) the two
        // views of one preset could disagree, which is the whole reason the
        // blue per-field displays were dropped.
        var texts = BindingTexts(Load(ModemCard)).ToList();

        foreach (var cell in new[] { "NumberText", "NameText", "ParametersText" })
        {
            Assert.Contains(texts, t => t.Contains("PickedRow." + cell, StringComparison.Ordinal));
            // …and the list template's own bare binding to the same cell.
            Assert.Contains(texts, t => t.Contains("{Binding " + cell + "}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ModemPresets_HasNoBluePerFieldDisplays_AndNoRefreshButton()
    {
        // The three per-field read displays and the Refresh button both left
        // in round 9. The VM test pins the PROPERTIES gone; this pins that no
        // BINDING was left pointing at them anywhere in the app layer — MAUI
        // resolves a missing path to nothing, silently, so an orphan renders
        // a blank cell forever and nothing else would notice.
        var deleted = new[]
        {
            "NameDisplayText", "BaudDisplayText", "InterleaveDisplayText",
            "BaudInput", "RefreshPresetsCommand", "SelectedEnabled", "EnabledChoices",
        };

        var offenders = new List<string>();
        foreach (var file in AppXamlFiles(FindRepoRoot()))
        {
            var text = string.Join("\n", BindingTexts(XDocument.Load(file)));
            foreach (var name in deleted)
                if (text.Contains(name, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: still binds {name}");
        }
        Assert.Empty(offenders);

        Assert.DoesNotContain(Load(ModemCard).Descendants(), e =>
            e.Name.LocalName == "Button"
            && (TextOf(e) ?? "").StartsWith("Refresh", StringComparison.Ordinal));

        // Anti-vacuity: the replacements ARE visible to this scanner.
        var texts = BindingTexts(Load(ModemCard)).ToList();
        Assert.Contains(texts, t => t.Contains("BaudText", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("StateChoices", StringComparison.Ordinal));
    }

    // ---- ROUND 10 §3: the five wide choice rows, per-row --------------------
    // These pins REPLACE round 9's "all four rows right-align" theory and its
    // TypeRowSpacing knob. Placement is now decided PER ROW (owner-final
    // 2026-08-16): four labels LEFT, and Interleave alone ABOVE — the single
    // sanctioned above-label row in the app, ledger-recorded. A guard that
    // only checked alignment could not see that distinction at all, which is
    // exactly how "unify the labels" would silently undo the one exception.

    /// <summary>Every §3 wide choice row: the file it lives in, the collection
    /// its FIRST line renders, the row's LABEL text, and where that label
    /// sits. Keyed by the collection because a label text can be edited and a
    /// binding cannot be edited by accident.</summary>
    public static TheoryData<string, string, string, string> WideChoiceRows => new()
    {
        // ROUND 11 §3: the ALE row's LABEL reads "Type" now (display text
        // only — the collection it heads is still KindChoices, which is why
        // this table is keyed by the binding and not by the words).
        { AddressCard, "KindChoices", "Type", "LEFT" },
        { ModemCard, "TypeChoicesRow1", "Type", "LEFT" },
        { ModemCard, "InterleaveChoicesRow1", "Interleave", "ABOVE" },
        // ROUND 11 §3: the data-mode row's LABEL reads "Port" now (display
        // text only — the collection it heads is still DataModeChoices*, the
        // same reason the ALE row above is keyed by KindChoices).
        { ModemCard, "DataModeChoicesRow1", "Port", "LEFT" },
        { ModemCard, "StateChoices", "Preset state", "LEFT" },
        // CLONE-FIELD ROUND 2 F11: the HOP shape's two rows. Same §3 rule —
        // label LEFT — and they are here rather than exempt because "the card
        // has a second shape now" is exactly the kind of change that quietly
        // grows a second above-label row.
        { ModemCard, "SyncChoices", "Signalling", "LEFT" },
        { ModemCard, "PortChoices", "Port", "LEFT" },
    };

    [Theory]
    [MemberData(nameof(WideChoiceRows))]
    public void EachWideChoiceRow_PlacesItsLabelWhereSectionThreeSaid(
        string file, string collection, string labelText, string placement)
    {
        var host = SegmentRowHost(file, collection);
        var label = LabelFor(file, labelText, host);

        // The buttons still right-align, on every row, either placement.
        Assert.Equal("End", PropertyValue(host, "HorizontalOptions"));

        // LEFT = the label and the button block are COLUMNS of one Grid, with
        // the label in column 0 and the buttons in a later column.
        // ABOVE = the label is a preceding SIBLING in a vertical stack that
        // also contains the button block; there is no shared column grid.
        var sharedGrid = NearestCommonColumnGrid(label, host);

        if (placement == "LEFT")
        {
            Assert.NotNull(sharedGrid);
            Assert.Equal("0", ColumnOf(ChildOf(sharedGrid!, label)));
            Assert.NotEqual("0", ColumnOf(ChildOf(sharedGrid!, host)));
        }
        else
        {
            Assert.Null(sharedGrid);
            var stack = label.Parent!;
            var children = stack.Elements()
                .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
                .ToList();
            int labelIndex = children.FindIndex(e => ReferenceEquals(e, label));
            int hostIndex = children.FindIndex(e => ReferenceEquals(e, host));
            Assert.True(labelIndex >= 0 && hostIndex > labelIndex,
                "the ABOVE row's label must precede its buttons in the same stack");
        }
    }

    [Fact]
    public void TheWideRows_AreAllLeftAndExactlyOneAbove()
    {
        // The COUNT is the contract, not just each row's own placement: §3
        // sanctions exactly ONE above-label row. Flipping a second row to
        // ABOVE would satisfy its own theory case and quietly double the
        // exception; this fails. SEVEN rows since clone-field round 2 F11 added
        // the Modem card's HOP shape (Signalling + Port), both LEFT.
        var placements = WideChoiceRows.Select(r => (string)r[3]!).ToList();

        Assert.Equal(7, placements.Count);
        Assert.Equal(6, placements.Count(p => p == "LEFT"));
        Assert.Single(placements, p => p == "ABOVE");
        Assert.Equal("Interleave", (string)WideChoiceRows.Single(r => (string)r[3]! == "ABOVE")[1]! switch
        {
            "InterleaveChoicesRow1" => "Interleave",
            var other => other,
        });
    }

    /// <summary>The wide-row STRUCTURES: ROUND 11 §3 makes Type 2+2+2 and Port
    /// 2+1; Interleave stays 3+2. The markup renders one BindableLayout per
    /// line, so "three rows" is visible here as three hosts bound to the three
    /// slices — and the VM test pins how many items each slice holds.</summary>
    public static TheoryData<string, string[]> SplitWideRows => new()
    {
        { "Type", ["TypeChoicesRow1", "TypeChoicesRow2", "TypeChoicesRow3"] },
        { "Interleave", ["InterleaveChoicesRow1", "InterleaveChoicesRow2"] },
        { "Port", ["DataModeChoicesRow1", "DataModeChoicesRow2"] },
    };

    [Theory]
    [MemberData(nameof(SplitWideRows))]
    public void EachSplitWideRow_RendersEveryOneOfItsLines(string row, string[] slices)
    {
        var lines = slices.Select(s => SegmentRowHost(ModemCard, s)).ToList();

        // Distinct hosts: a row whose two "lines" were the same element would
        // satisfy every per-line assertion below and render one line.
        Assert.Equal(slices.Length, lines.Distinct().Count());

        foreach (var line in lines)
        {
            Assert.Equal("End", PropertyValue(line, "HorizontalOptions"));
            // Every line shares the house 6-dp spacing (round 9's row-local
            // TypeRowSpacing knob is gone — the WIDTHS carry the fit now).
            Assert.Equal("6", PropertyValue(line, "Spacing"));
        }

        // …and they are SIBLINGS of one stack, in SLICE ORDER. A row whose
        // lines rendered out of order would put "FSK VFT" above "39 tone" and
        // no per-line assertion would see it. (The ABOVE row's label is a
        // sibling too, which is why this reads only the bound layouts.)
        Assert.Single(lines.Select(l => l.Parent).Distinct());
        var bound = lines[0].Parent!.Elements()
            .Select(e => e.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "BindableLayout.ItemsSource")?.Value)
            .Where(v => v is not null)
            .Select(v => v!.Replace("{Binding ", "", StringComparison.Ordinal)
                           .Replace("}", "", StringComparison.Ordinal));
        Assert.Equal(slices, bound);
        Assert.NotEmpty(row);
    }

    [Fact]
    public void TheTypeRow_RendersTHREELines_NotTwo()
    {
        // ROUND 11 §3's actual change, stated as a count. The theory above
        // would pass just as happily on round 10's 3+3 if the third slice
        // simply were not listed — this is what notices.
        var typeSlices = (string[])SplitWideRows.Single(r => (string)r[0]! == "Type")[1]!;
        Assert.Equal(3, typeSlices.Length);
        Assert.Throws<InvalidOperationException>(
            () => SegmentRowHost(ModemCard, "TypeChoicesRow4"));
    }

    [Fact]
    public void TheRowLocalTypeSpacingResource_IsGone()
    {
        // Round 9's knob retires with the rework: the type row is 3+3 at a
        // NAMED width now, so shaving 6 dp to 4 is no longer the lever. An
        // absence pin with an anti-vacuity partner — the card still defines
        // its OTHER row-local widths.
        var keys = Load(ModemCard).Descendants()
            .Where(e => e.Name.LocalName == "Double")
            .Select(e => e.Attributes().First(a => a.Name.LocalName == "Key").Value)
            .ToList();

        Assert.DoesNotContain("TypeRowSpacing", keys);
        Assert.Contains("PresetWidthNumber", keys);
        Assert.Contains("PresetWidthName", keys);
    }

    [Fact]
    public void EveryWideChoiceButton_CarriesItsNamedWidth_NotALiteral()
    {
        // §3's widths are the whole reason the rows split the way they do, so
        // the templates must reference the KEYS: a literal here would drift
        // away from the arithmetic StyleVocabularyGuardTests evaluates.
        var templates = Load(ModemCard).Descendants()
            .Where(e => e.Name.LocalName == "DataTemplate"
                        && e.Attributes().Any(a => a.Name.LocalName == "Key"))
            .Where(e => e.Descendants().Any(c => c.Name.LocalName == "Button"))
            .ToDictionary(
                e => e.Attributes().First(a => a.Name.LocalName == "Key").Value,
                e => e.Descendants().First(c => c.Name.LocalName == "Button"));

        Assert.Equal("{StaticResource SegmentWidthWide}",
            PropertyValue(templates["PresetChoiceButton"], "WidthRequest"));
        // ROUND 11 §3: the second template is the PORT class now — renamed
        // with its width, because "Wide" beside SegmentWidthWide would have
        // read as the one thing it is not.
        Assert.Equal("{StaticResource SegmentWidthPort}",
            PropertyValue(templates["PresetPortChoiceButton"], "WidthRequest"));
        Assert.False(templates.ContainsKey("PresetWideChoiceButton"));

        // …and the ALE kind row's three buttons, the other SegmentWidthWide
        // consumer §3 names.
        var kindButton = Load(AddressCard).Descendants()
            .Where(e => e.Name.LocalName == "DataTemplate")
            .Single(e => e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "AleChoiceButton"))
            .Descendants().First(c => c.Name.LocalName == "Button");
        Assert.Equal("{StaticResource SegmentWidthWide}", PropertyValue(kindButton, "WidthRequest"));
    }

    // ---- ROUND 10 §8: the enriched read-back --------------------------------

    [Fact]
    public void ModemReadBack_RendersTheTwoParsedLines_AndTheOneVerbatimFallback()
    {
        // §8's rendering contract, structurally: line 1 carries Type and Data
        // mode beside # and Name, line 2 carries Baud / the type-switched
        // optional / State, and an UNPARSED row renders the ONE verbatim cell
        // instead. Every parsed cell gates on IsParsed; the fallback gates on
        // its complement, so the two can never both be on screen.
        var texts = BindingTexts(Load(ModemCard)).ToList();

        foreach (var cell in new[]
        {
            "TypeText", "DataModeText", "BaudText", "InterleaveText",
            // CLONE ROUND 12 §9 A3: the State cell binds PRESENCE now — the
            // listing-derived StateText it used to bind is deleted, because
            // the listing never carried the state.
            "MarkText", "SpaceText", "PresenceText",
        })
            Assert.Contains(texts, t => t.Contains("PickedRow." + cell, StringComparison.Ordinal));

        Assert.Contains(texts, t => t.Contains("PickedRow.IsParsed", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("PickedRow.IsNotParsed", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("PickedRow.ShowsInterleave", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("PickedRow.ShowsMarkSpace", StringComparison.Ordinal));

        // The FALLBACK is exactly ONE verbatim cell in the read-back block —
        // the picked row's Parameters — and it is gated on IsNotParsed.
        var fallback = Load(ModemCard).Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && (PropertyValue(e, "Text") ?? "").Contains("PickedRow.ParametersText", StringComparison.Ordinal));
        var gate = fallback.Ancestors()
            .First(a => (PropertyValue(a, "IsVisible") ?? "").Contains("IsNotParsed", StringComparison.Ordinal));
        Assert.NotNull(gate);
    }

    [Fact]
    public void ModemListTab_GainsTheStateColumn_AndNothingElseFromTheProjection()
    {
        // ROUND 10's pin said the LIST was # | Name | verbatim Parameters and
        // nothing else. ROUND 11 §6 adds EXACTLY ONE cell: the STATE, which is
        // the only thing on this card that comes from the PRESENCE store
        // rather than from a line. The pin keeps its original shape — an exact,
        // ordered cell list — so the enrichment still cannot creep in.
        var listTemplate = Load(ModemCard).Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "DataType"
                    && a.Value.Contains("ModemPresetRow", StringComparison.Ordinal)));

        var cells = listTemplate.Descendants()
            .Where(e => e.Name.LocalName == "Label")
            .Select(e => PropertyValue(e, "Text"))
            .ToList();

        Assert.Equal(
            [
                "{Binding NumberText}", "{Binding NameText}",
                "{Binding PresenceText}", "{Binding ParametersText}",
            ],
            cells);

        // The read-back's projected cells stay OUT of the list (§6: the
        // read-back row projection is unchanged, and so is this one apart from
        // the state).
        foreach (var projected in new[] { "TypeText", "DataModeText", "BaudText", "InterleaveText" })
            Assert.DoesNotContain(cells, c => c == "{Binding " + projected + "}");
    }

    [Fact]
    public void TheModemStateColumn_HasAHeading_AndTheHeadingsMatchTheCells()
    {
        // A four-cell row under a three-cell header is the failure this
        // catches — the columns would still lay out, silently mislabelled.
        var listTab = Load(ModemCard).Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "DataType"
                    && a.Value.Contains("ModemPresetRow", StringComparison.Ordinal)))
            .Ancestors().First(a => a.Name.LocalName == "VerticalStackLayout")
            .Parent!;

        var header = listTab.Elements()
            .First(e => e.Name.LocalName == "Grid")
            .Elements().Where(e => e.Name.LocalName == "Label")
            .Select(e => TextOf(e))
            .ToList();

        Assert.Equal(["#", "Name", "State", "Parameters"], header);
    }

    // ---- CLONE-FIELD ROUND 2 F11: the card's TWO FIELD STACKS ---------------

    [Fact]
    public void TheModemCard_CarriesTwoFieldStacks_OneVisiblePerScope_F11()
    {
        // The card FOLLOWS THE CONFIRMED MODE (owner ruling R-D, decision A-9):
        // the SSB/ALE shape and the HOP shape are two stacks bound to the two
        // halves of one flag, so exactly one renders. Structural, on the
        // IsVisible bindings and on what each stack CONTAINS — a pin on the
        // flag alone would pass on two stacks that both showed the same rows.
        XDocument card = Load(ModemCard);

        var ssbStack = StackBoundTo(card, "IsSsbScope");
        var hopStack = StackBoundTo(card, "IsHopScope");

        // Neither is inside the other: they are SIBLING alternatives, so a
        // nesting that made the HOP rows a sub-section of the SSB shape fails.
        Assert.DoesNotContain(hopStack.Ancestors(), a => ReferenceEquals(a, ssbStack));
        Assert.DoesNotContain(ssbStack.Ancestors(), a => ReferenceEquals(a, hopStack));

        // The SSB stack owns the rows a HOP preset HAS NO FIELD FOR…
        foreach (var collection in new[] { "TypeChoicesRow1", "InterleaveChoicesRow1", "DataModeChoicesRow1" })
            Assert.Contains(ssbStack.Descendants(), e => BindsCollection(e, collection));
        // …and the HOP stack owns the two columns only it has.
        foreach (var collection in new[] { "SyncChoices", "PortChoices" })
            Assert.Contains(hopStack.Descendants(), e => BindsCollection(e, collection));

        // ABSENT, NOT GREYED: the type/interleave rows are not in the HOP
        // stack at all, and the HOP columns are not in the SSB one.
        foreach (var collection in new[] { "TypeChoicesRow1", "InterleaveChoicesRow1" })
            Assert.DoesNotContain(hopStack.Descendants(), e => BindsCollection(e, collection));
        foreach (var collection in new[] { "SyncChoices", "PortChoices" })
            Assert.DoesNotContain(ssbStack.Descendants(), e => BindsCollection(e, collection));

        // The rows the two shapes SHARE stay outside both stacks — Name, the
        // baud wheel, Preset state and Store are one control each, not two.
        foreach (var shared in new[] { "{Binding NameInput, Mode=TwoWay}", "{Binding BaudText}" })
        {
            var control = card.Descendants().Single(e =>
                (PropertyValue(e, "Text") ?? "") == shared);
            Assert.DoesNotContain(control.Ancestors(), a => ReferenceEquals(a, ssbStack));
            Assert.DoesNotContain(control.Ancestors(), a => ReferenceEquals(a, hopStack));
        }
    }

    private static XElement StackBoundTo(XDocument card, string flag)
        => card.Descendants().Single(e =>
            e.Name.LocalName == "VerticalStackLayout"
            && (PropertyValue(e, "IsVisible") ?? "") == "{Binding " + flag + "}");

    private static bool BindsCollection(XElement element, string collection)
        => element.Attributes().Any(a =>
            a.Name.LocalName == "BindableLayout.ItemsSource"
            && a.Value.Contains("{Binding " + collection + "}", StringComparison.Ordinal));

    [Fact]
    public void TheAlignmentReader_SeesBothWaysThePropertyCanBeSet()
    {
        // Anti-vacuity for the theory above: attribute form AND property
        // element form must both register, and a layout with neither must
        // read as unset — otherwise "right-aligned" could be asserted by a
        // reader that only ever sees one of the two spellings.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <HorizontalStackLayout HorizontalOptions="End" />
                <HorizontalStackLayout>
                  <HorizontalStackLayout.HorizontalOptions>End</HorizontalStackLayout.HorizontalOptions>
                </HorizontalStackLayout>
                <HorizontalStackLayout />
              </VerticalStackLayout>
            </ContentView>
            """);

        var rows = markup.Root!.Descendants()
            .Where(e => e.Name.LocalName == "HorizontalStackLayout")
            .ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("End", PropertyValue(rows[0], "HorizontalOptions"));
        Assert.Equal("End", PropertyValue(rows[1], "HorizontalOptions"));
        Assert.Null(PropertyValue(rows[2], "HorizontalOptions"));
    }

    /// <summary>A property set either as an attribute or as a property
    /// ELEMENT (<c>&lt;Type.Property&gt;</c>) — XAML allows both, so a guard
    /// that reads only one of them can be walked straight past.</summary>
    private static string? PropertyValue(XElement element, string property)
        => element.Attribute(property)?.Value
           ?? element.Elements()
               .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)
               ?.Value;

    /// <summary>The layout that renders one segment row — the element whose
    /// BindableLayout.ItemsSource binds the named collection.</summary>
    private static XElement SegmentRowHost(string file, string collection)
        => Load(file).Descendants().Single(e =>
            e.Attributes().Any(a =>
                a.Name.LocalName == "BindableLayout.ItemsSource"
                && a.Value.Contains("{Binding " + collection + "}", StringComparison.Ordinal)));

    /// <summary>A ROW LABEL: the one Caption-styled Label in the file carrying
    /// this exact Text. Styled-scoped on purpose — "Type" and "Kind" are also
    /// CellHeading texts in the read-back and book tables of the same files,
    /// and a row label is the only one whose PLACEMENT §3 decides.</summary>
    private static XElement LabelFor(string file, string text, XElement? row = null)
    {
        var candidates = Load(file).Descendants().Where(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == text
            && (PropertyValue(e, "Style") ?? "").Contains("Caption", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 1 || row is null) return candidates.Single();

        // CLONE-FIELD ROUND 2 F11: the Modem card now carries TWO field stacks
        // (the SSB shape and the HOP shape, one visible at a time), and both
        // have a "Port" row — so a label TEXT is no longer unique in the file.
        // Disambiguate by the row it actually heads: the label that shares a
        // column Grid with this row's button host, or precedes it in the same
        // stack. Still `Single`, so a genuinely ambiguous pair still fails.
        return candidates.Single(l =>
            NearestCommonColumnGrid(l, row) is not null || ReferenceEquals(l.Parent, row.Parent));
    }

    /// <summary>The nearest Grid with ColumnDefinitions that contains BOTH —
    /// i.e. the grid whose COLUMNS they share. Null when no such grid exists,
    /// which is what an ABOVE row looks like.</summary>
    private static XElement? NearestCommonColumnGrid(XElement a, XElement b)
    {
        for (var e = a.Parent; e is not null; e = e.Parent)
        {
            if (e.Name.LocalName != "Grid" || e.Attribute("ColumnDefinitions") is null) continue;
            if (b.AncestorsAndSelf().Any(x => ReferenceEquals(x, e))) return e;
        }
        return null;
    }

    /// <summary>The direct child of <paramref name="grid"/> that contains (or
    /// is) <paramref name="descendant"/> — the element the Grid.Column
    /// attached property actually sits on.</summary>
    private static XElement ChildOf(XElement grid, XElement descendant)
        => descendant.AncestorsAndSelf().First(e => ReferenceEquals(e.Parent, grid));

    /// <summary>Grid.Column, either spelling. An ABSENT attached property is
    /// column 0 — MAUI's default, and the reason this returns "0" rather than
    /// null (a positional contract must assert a position, not an absence).</summary>
    private static string ColumnOf(XElement e) => PropertyValue(e, "Grid.Column") ?? "0";

    // ---- structure helpers ---------------------------------------------------

    /// <summary>The direct children of the SSB pane's root stack (inside the
    /// named Root ScrollView), in document order.</summary>
    private static List<XElement> PaneChildren(XDocument pane)
    {
        var root = pane.Descendants().First(e => NameOf(e) == "Root");
        return [.. LayoutChildren(root.Elements().First(IsLayout))];
    }

    private static List<XElement> SettingsSectionChildren(XDocument page)
    {
        var section = page.Descendants().First(e => NameOf(e) == "SettingsSection");
        return [.. LayoutChildren(section.Elements().First(IsLayout))];
    }

    /// <summary>Element children only — a property element (local name
    /// "Type.Property") is not a child CONTROL and must never be counted as
    /// one, or "the last element" would be whatever property came last.</summary>
    private static IEnumerable<XElement> LayoutChildren(XElement layout)
        => layout.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

    private static bool IsLayout(XElement e)
        => !e.Name.LocalName.Contains('.', StringComparison.Ordinal);

    /// <summary>The list tab's subtree: the nearest ancestor of the hundred-row
    /// CollectionView that does NOT also contain the Store button.</summary>
    private static XElement ListTabRoot(XDocument pane)
        => NearestAncestorExcluding(pane.Descendants().First(IsChannelList), IsStoreButton);

    /// <summary>The programming tab's subtree: the nearest ancestor of Store
    /// that does NOT also contain the channel list.</summary>
    private static XElement ProgrammingTabRoot(XDocument pane)
        => NearestAncestorExcluding(pane.Descendants().First(IsStoreButton), IsChannelList);

    private static XElement NearestAncestorExcluding(XElement from, Func<XElement, bool> exclude)
    {
        foreach (var ancestor in from.Ancestors())
            if (!ancestor.Descendants().Any(exclude))
                return ancestor;

        throw new InvalidOperationException(
            "no ancestor separates the two sub-tabs — the card's structure changed shape");
    }

    private static bool IsChannelList(XElement e)
        => e.Name.LocalName == "CollectionView" && NameOf(e) == "ChannelListView";

    private static bool IsStoreButton(XElement e)
        => e.Name.LocalName == "Button" && TextOf(e) == "Store";

    private static bool IsCloningHeading(XElement e)
        => e.Name.LocalName == "Label" && TextOf(e) == "Cloning";

    private static bool Descends(XElement root, Func<XElement, bool> match)
        => match(root) || root.Descendants().Any(match);

    /// <summary>x:Name, whichever namespace prefix the file happens to use.</summary>
    private static string? NameOf(XElement e)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    /// <summary>Text, set either way (the RefreshButtonWidthGuard lesson).</summary>
    private static string? TextOf(XElement e)
        => e.Attribute("Text")?.Value
           ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + ".Text")?.Value;

    /// <summary>Every attribute value and property-element value in the
    /// document — i.e. everywhere a {Binding …} can legally live.</summary>
    private static IEnumerable<string> BindingTexts(XDocument document)
    {
        foreach (var e in document.Descendants())
        {
            foreach (var a in e.Attributes()) yield return a.Value;
            if (e.Name.LocalName.Contains('.', StringComparison.Ordinal) && !e.HasElements)
                yield return e.Value;
        }
    }

    /// <summary>Parsed ONCE per file and shared, so every helper hands back the
    /// SAME element instances. The placement pins compare ANCESTRY by
    /// reference, and two separate parses of one document would make "these
    /// two share a Grid" unsatisfiable for the wrong reason (the
    /// AleProgrammingMarkupGuardTests lesson).</summary>
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
