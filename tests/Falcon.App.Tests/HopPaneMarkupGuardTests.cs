using System.Xml.Linq;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 5 (BD1, BG6) — the two HOP markup facts no VM test can see,
/// in the house source-scan style (RefreshButtonWidthGuardTests): a HEADING
/// literal and an ELEMENT ORDER. Both were named as gate items to be checked by
/// grep / UIA; a structural scan is stronger than either, and it re-runs in a
/// clone where no Windows session exists.
///
/// <para>XAML is well-formed XML, so this parses it rather than matching text —
/// the round-2 lesson from the Refresh-width guard, where a tag-shaped regex
/// missed the equivalent property-element form.</para>
/// </summary>
public class HopPaneMarkupGuardTests
{
    private static readonly string OperatePane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "HopPaneView.xaml");

    private static readonly string SettingsPane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "HopSettingsPaneView.xaml");

    /// <summary>Read ONLY by the round-14 coupler-copy pin below: that row was
    /// copied from this pane, and a copy is worth pinning against its source
    /// rather than re-describing.</summary>
    private static readonly string SsbSettingsPane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");

    [Fact]
    public void TheValueColumnHeaders_AreTheSharedConstantAndTheTypeAwareBinding()
    {
        // BD1 as amended by round-7 DD: the settings NET-LIST keeps the ONE
        // generic literal (its ten rows mix types), compared against the
        // VM-side constant so markup and code cannot drift. The OPERATE
        // header now FOLLOWS the current net's confirmed type, so its pin is
        // the BINDING to the VM property that computes it — a literal there
        // would re-freeze what the owner made dynamic.
        var settingsHeadings = CellHeadings(SettingsPane);
        Assert.Contains(HopNetDisplay.ValueHeading, settingsHeadings);
        Assert.DoesNotContain(settingsHeadings, h =>
            h.Contains("Centre", StringComparison.OrdinalIgnoreCase));

        var operateHeadings = CellHeadings(OperatePane);
        Assert.Contains(operateHeadings, h =>
            h.Contains("Binding Hop.ValueColumnHeading", StringComparison.Ordinal));
        Assert.DoesNotContain(operateHeadings, h => h == HopNetDisplay.ValueHeading);
    }

    [Fact]
    public void TheGuard_ActuallyReadsHeadings_NotAnEmptyList()
    {
        // Anti-vacuity: a scan that finds no headings would pass the
        // DoesNotContain half above forever. Both tables carry the four
        // round-4 K1 columns.
        foreach (var relative in new[] { OperatePane, SettingsPane })
        {
            var headings = CellHeadings(relative);
            Assert.Contains("Net", headings);
            Assert.Contains("ID", headings);
            Assert.Contains("Type", headings);
        }
    }

    [Fact]
    public void HopSettingsPane_HasNoRefreshButton_AndTheClockIsItsBottomElement()
    {
        // ROUND 9 replaces the BG6 pin. The pane's Refresh button is DELETED
        // — under the unified read doctrine an editor landing re-reads its
        // target, so a manual "read the radio" button here answered a
        // question the picker already answers. What must hold now: NO Refresh
        // anywhere on this pane, and the radio-clock card is the pane's true
        // bottom element (the BG6 layout, minus the button).
        var root = XDocument.Load(Path.Combine(FindRepoRoot(), SettingsPane)).Root!;

        Assert.DoesNotContain(root.Descendants(), IsRefreshButton);

        // …and no binding was left pointing at the deleted command, which
        // MAUI would resolve to nothing SILENTLY. Attribute AND
        // property-element values, since a Command can be set either way.
        Assert.DoesNotContain(BindingTexts(root),
            t => t.Contains("RefreshNetsCommand", StringComparison.Ordinal));

        var clock = root.Descendants()
            .Single(e => e.Name.LocalName == "DeviceClockView");
        var siblings = clock.Parent!.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();
        Assert.Same(clock, siblings[^1]);
    }

    [Fact]
    public void TheChannelListTab_KEEPS_ItsRefresh_UnificationMustNotOverDelete()
    {
        // The other half of the round-9 ruling, pinned from the OPPOSITE
        // direction so "delete the Refresh buttons" cannot be applied one
        // step too far: Refresh survives exactly where a read is genuinely
        // expensive. DI ×100 is the one heavy read in the app.
        // This is ALSO the anti-vacuity half of the pin above: the SAME
        // detector, run on a document that genuinely has one, must find it.
        var pane = XDocument.Load(Path.Combine(
            FindRepoRoot(), "src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml")).Root!;

        Assert.Contains(pane.Descendants(), e =>
            IsRefreshButton(e) && TextOf(e) == "Refresh channels");
    }

    [Fact]
    public void TheRefreshDetector_SeesBothWaysTheTextCanBeSet()
    {
        // A "there is no Refresh button" pin is only as good as its
        // detector. XAML sets a property as an attribute OR a property
        // element, and an XML comment is not an element at all — all three
        // pinned as a unit, so the absence assertions above cannot pass by
        // being blind.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <Button Text="Refresh" />
                <Button><Button.Text>Refresh nets</Button.Text></Button>
                <Button Text="Store" />
                <!-- <Button Text="Refresh" /> -->
              </VerticalStackLayout>
            </ContentView>
            """);

        Assert.Equal(2, markup.Root!.Descendants().Count(IsRefreshButton));
    }

    /// <summary>A Button whose Text starts "Refresh", however that Text is
    /// set (attribute or property element).</summary>
    private static bool IsRefreshButton(XElement e)
        => e.Name.LocalName == "Button"
           && (TextOf(e) ?? "").StartsWith("Refresh", StringComparison.Ordinal);

    /// <summary>Every attribute value and property-element value — i.e.
    /// everywhere a <c>{Binding …}</c> can legally live.</summary>
    private static IEnumerable<string> BindingTexts(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            foreach (var a in e.Attributes()) yield return a.Value;
            if (e.Name.LocalName.Contains('.', StringComparison.Ordinal) && !e.HasElements)
                yield return e.Value;
        }
    }

    // ---- BG2: the type-switched sections are bound to the CONFIRMED type -----

    /// <summary>Each value section and the property whose value decides whether
    /// it is on screen. Getting one of these wrong shows the operator NB
    /// entries for a WB net — a wrong-type write is then one Return away, and
    /// no VM test can see it because the VM is correct either way.</summary>
    public static TheoryData<string, string> TypeSections => new()
    {
        { "Center (MHz)", "IsNarrowbandConfirmed" },
        { "Low (MHz)", "IsWidebandConfirmed" },
        { "Add", "IsListConfirmed" },
    };

    /// <summary>The anchors above are the editor's ROW LABELS, which carry the
    /// plain <c>Caption</c> style. ROUND 11 §7 made that qualifier load-bearing:
    /// the new exclusion-bands section on the same pane heads its columns
    /// "Low (MHz)" and "High (MHz)" — §7 pins those words — so "the Label whose
    /// Text is Low (MHz)" stopped being unique. It is unique again as "the
    /// Label whose Text is Low (MHz) AND whose Style is Caption": the section's
    /// headers are <c>CellHeading</c>, a DIFFERENT key (and a different tier —
    /// SettingsStyleAdoptionGuardTests draws the same distinction for
    /// "Frequencies (MHz)", which also appears twice on this pane).</summary>
    private static XElement RowLabel(XElement root, string text)
        => root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == text
            && PropertyValue(e, "Style") == "{StaticResource Caption}");

    [Fact]
    public void TheSectionAnchors_AreUniqueOnlyBecauseOfTheirStyle_AndTheCollisionIsReal()
    {
        // Anti-vacuity for the qualifier itself: if the exclusion headers ever
        // stopped colliding, the qualifier would be silently unnecessary and
        // the next collision would go unnoticed. Both halves are pinned — the
        // raw text really is ambiguous, and the styled lookup really is not.
        var root = PaneRoot(SettingsPane);

        foreach (var text in new[] { "Low (MHz)", "High (MHz)" })
        {
            var byText = root.Descendants()
                .Where(e => e.Name.LocalName == "Label" && TextOf(e) == text)
                .ToList();
            Assert.Equal(2, byText.Count);
            Assert.Equal(
                ["{StaticResource Caption}", "{StaticResource CellHeading}"],
                byText.Select(e => PropertyValue(e, "Style")).Order(StringComparer.Ordinal));
        }

        Assert.NotNull(RowLabel(root, "Low (MHz)"));
    }

    [Theory]
    [MemberData(nameof(TypeSections))]
    public void HopSettingsPane_EachTypeSection_IsBoundToItsOwnConfirmedTypeFlag(
        string labelInsideTheSection, string expectedFlag)
    {
        // C2 audit round 1, MAJOR: the auditor swapped the NB grid's IsVisible
        // to IsWidebandConfirmed and all 547 App tests stayed green — the exact
        // class of defect these structural guards exist for. The section is
        // located by a label only IT contains, then the nearest ancestor that
        // sets IsVisible must bind that section's own flag.
        var root = PaneRoot(SettingsPane);

        var anchor = RowLabel(root, labelInsideTheSection);

        var binding = AncestorVisibilityBinding(anchor);

        Assert.NotNull(binding);
        Assert.Equal(expectedFlag, binding);
    }

    [Fact]
    public void HopSettingsPane_TheNoTypeState_HasNoValueControlsAndNoCaption()
    {
        // The fourth state (BG2) after round 6 (CC, owner ruling): no
        // confirmed type ⇒ no value controls AND no caption — the old
        // "Waiting for the radio to report…" label was deleted, and nothing
        // in the pane binds HasNoConfirmedType any more. This pin keeps the
        // deletion deliberate: a caption creeping back (or anything else
        // binding the flag in markup) fails it and forces a conscious call.
        var root = PaneRoot(SettingsPane);

        Assert.DoesNotContain(root.Descendants(),
            e => (TextOf(e) ?? "").StartsWith("Waiting for the radio to report", StringComparison.Ordinal));
        Assert.DoesNotContain(root.Descendants(),
            e => VisibilityBinding(e) == "HasNoConfirmedType");
    }

    [Fact]
    public void TheGuard_ReallyResolvesEachSection_ToADifferentElement()
    {
        // Anti-vacuity, two ways. (1) If the three anchors resolved to the same
        // element, or to an element with no IsVisible ancestor, the theory
        // above would be testing one binding three times or nothing at all.
        // (2) The three flags must be DISTINCT — a markup change that bound
        // every section to the same flag would otherwise satisfy each case.
        var root = PaneRoot(SettingsPane);

        var owners = new List<XElement>();
        foreach (var label in new[] { "Center (MHz)", "Low (MHz)", "Add" })
        {
            var anchor = RowLabel(root, label);
            var owner = AncestorSettingVisibility(anchor);
            Assert.NotNull(owner);
            owners.Add(owner!);
        }

        Assert.Equal(3, owners.Distinct().Count());
        Assert.Equal(3, owners.Select(VisibilityBinding).Distinct().Count());
    }

    /// <summary>The nearest ancestor (or self) that SETS IsVisible, in either
    /// form. Walking up is what makes the anchor-label approach robust: the
    /// section root is a Grid or a stack, and which one it is has changed
    /// twice already.</summary>
    private static XElement? AncestorSettingVisibility(XElement start)
    {
        for (var e = start; e is not null; e = e.Parent)
            if (VisibilityBinding(e) is not null) return e;
        return null;
    }

    private static string? AncestorVisibilityBinding(XElement start)
        => AncestorSettingVisibility(start) is { } owner ? VisibilityBinding(owner) : null;

    /// <summary>The property name inside an <c>IsVisible="{Binding X}"</c>, in
    /// EITHER form a XAML property can be set (the round-2 lesson from the
    /// Refresh-width guard: a tag-shaped read misses the property-element
    /// form). Returns null when the element does not set IsVisible, and for a
    /// literal like <c>IsVisible="False"</c>.</summary>
    private static string? VisibilityBinding(XElement element)
        => PropertyValue(element, "IsVisible") is { } raw ? BindingName(raw) : null;

    /// <summary>The property name inside a <c>{Binding X}</c> markup extension,
    /// or null for anything that is not one (a literal, a StaticResource, a
    /// binding with a converter). Shared by the IsVisible reader above and the
    /// round-13 DataTrigger reader below — one parse, pinned once.</summary>
    private static string? BindingName(string raw)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(), @"^\{\s*Binding\s+(?:Path\s*=\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}$");
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>A property set as an attribute, or as a property ELEMENT
    /// (<c>&lt;Grid.IsVisible&gt;…&lt;/Grid.IsVisible&gt;</c>). Both count —
    /// the structural-scan rule covers both, and the heading helper below used
    /// to read only the attribute form.</summary>
    private static string? PropertyValue(XElement element, string property)
        => element.Attribute(property)?.Value
            ?? element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)?.Value;

    private static string? TextOf(XElement element) => PropertyValue(element, "Text");

    // ---- ROUND 10 §5: the inline Clear-net strip is DELETED -----------------

    [Fact]
    public void HopSettingsPane_HasNoInlineWipeStrip_AndBindsNoneOfItsState()
    {
        // The Proceed/Cancel strip and its pending-wipe warning box left with
        // §5's popup rework. Their markup pins are REPLACED by the VM's
        // lifecycle pins; what belongs here is that the markup really lost
        // them — a Proceed button creeping back would give the wipe a SECOND
        // path to the wire, which is precisely what BG4 forbids.
        var root = PaneRoot(SettingsPane);

        Assert.DoesNotContain(root.Descendants(), e => TextOf(e) == "Proceed");

        foreach (var name in new[]
        {
            "IsWipeWarningOpen", "PendingWipeNetLabel", "WipeWarningText",
            "ConfirmNetWipeCommand", "CancelNetWipeCommand",
        })
            Assert.DoesNotContain(BindingTexts(root),
                t => t.Contains(name, StringComparison.Ordinal));

        // …and the ONE surviving path — the button that raises the popup — is
        // still there (anti-vacuity for every absence above).
        var clearNet = root.Descendants().Single(e =>
            e.Name.LocalName == "Button" && TextOf(e) == "Clear net");
        Assert.Equal("{Binding RequestNetWipeCommand}", PropertyValue(clearNet, "Command"));
    }

    // ---- ROUND 10 §3: the list editor gains a header row --------------------

    [Fact]
    public void TheListEditor_HasAFreqHeader_AndLeavesTheRemoveColumnUnheaded()
    {
        // §3's table rule reaches the HOP list editor: a table gets a header
        // row. The Remove column is deliberately UNHEADED — a heading over a
        // column of buttons names nothing — so the header grid carries exactly
        // ONE heading, and it sits over the value column.
        var root = PaneRoot(SettingsPane);

        var freq = root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "Freq"
            && (PropertyValue(e, "Style") ?? "").Contains("CellHeading", StringComparison.Ordinal));

        var headerGrid = freq.Parent!;
        Assert.Equal("Grid", headerGrid.Name.LocalName);
        Assert.Single(headerGrid.Elements().Where(e => e.Name.LocalName == "Label"));

        // The heading shares the ROW's value-column width, or the columns do
        // not line up — which is the only thing a header row is for.
        Assert.Equal("{StaticResource CellWidthValue}", PropertyValue(freq, "WidthRequest"));

        // …and the section HEAD above it is a SubHeading now, not a cell
        // heading: it names the section, the header row names the column.
        var sectionHead = root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == HopNetDisplay.ValueHeading
            && (PropertyValue(e, "Style") ?? "").Contains("SubHeading", StringComparison.Ordinal));
        Assert.NotNull(sectionHead);
    }

    [Fact]
    public void WidebandSection_HasExactlyOneSetButton_AndBothEntriesStillCommit()
    {
        // Round 6 (CB, owner): one HOPSET command carries the low+high pair,
        // so ONE Set button sends it — the round-5 per-row duplicates must
        // not creep back — while Return on either entry still commits.
        var root = PaneRoot(SettingsPane);

        var wbSection = AncestorSettingVisibility(RowLabel(root, "Low (MHz)"))!;
        Assert.Equal("IsWidebandConfirmed", VisibilityBinding(wbSection));

        var setButtons = wbSection.Descendants()
            .Where(e => e.Name.LocalName == "Button"
                && (PropertyValue(e, "Command") ?? "").Contains("CommitBandEdgesCommand", StringComparison.Ordinal))
            .ToList();
        Assert.Single(setButtons);
        Assert.Equal("Set", TextOf(setButtons[0]));

        var returnCommits = wbSection.Descendants()
            .Where(e => e.Name.LocalName == "Entry"
                && (PropertyValue(e, "ReturnCommand") ?? "").Contains("CommitBandEdgesCommand", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, returnCommits.Count);
    }

    [Fact]
    public void TheGuard_ReadsBothWaysAPropertyCanBeSet()
    {
        // The helpers above are load-bearing for every pin in this file, so
        // they are pinned as a unit against a synthetic sample rather than
        // trusted — attribute form, property-element form, a literal, and an
        // element that sets nothing.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Grid IsVisible="{Binding IsNarrowbandConfirmed}" />
              <Grid>
                <Grid.IsVisible>{Binding IsWidebandConfirmed}</Grid.IsVisible>
              </Grid>
              <Grid IsVisible="False" />
              <Grid />
            </ContentView>
            """);

        var grids = markup.Descendants().Where(e => e.Name.LocalName == "Grid").ToList();

        Assert.Equal("IsNarrowbandConfirmed", VisibilityBinding(grids[0]));
        Assert.Equal("IsWidebandConfirmed", VisibilityBinding(grids[1]));
        Assert.Null(VisibilityBinding(grids[2]));       // a literal is not a binding
        Assert.Null(VisibilityBinding(grids[3]));       // nothing set
    }

    // ==== ROUND 13 items 9 + 14: the TYPE ROW ================================
    // The row was UNGUARDED until now — no test in the suite could see its
    // columns or which flag lit its segments, so either fix could be reverted
    // silently. Both are markup-only facts, invisible to every VM test.

    [Fact]
    public void HopSettingsPane_TheTypeRow_RightJustifiesItsSegments()
    {
        // ITEM 9 (owner, 2026-08-19; plan/plan-round13.md §4 A1). Right
        // justification here is a COLUMN fact, not an alignment one: the
        // residual star column sits at index 1, AHEAD of the three Auto
        // segment columns, and nothing occupies it — so the segments are
        // pushed against the right edge no matter how wide the pane gets.
        var root = PaneRoot(SettingsPane);
        var label = RowLabel(root, "Type");
        var row = label.Parent!;

        Assert.Equal("Grid", row.Name.LocalName);
        Assert.Equal("Auto,*,Auto,Auto,Auto", PropertyValue(row, "ColumnDefinitions"));

        // The label keeps col 0 (implicit) and the pane-wide 86-dp caption
        // gutter, so this row's label still lines up with every other row's.
        Assert.Null(PropertyValue(label, "Grid.Column"));
        Assert.Equal("86", PropertyValue(label, "WidthRequest"));

        var buttons = row.Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(["NB", "WB", "LIST"], buttons.Select(TextOf));
        Assert.Equal(["2", "3", "4"], buttons.Select(b => PropertyValue(b, "Grid.Column")));

        // …and the star column really is EMPTY — a control landing in col 1
        // would consume the residual space and un-justify the row.
        Assert.DoesNotContain(row.Elements(), e => PropertyValue(e, "Grid.Column") == "1");
    }

    [Fact]
    public void HopSettingsPane_TheTypeSegments_LightOnTheREPORTEDType_NotTheConfirmedOne()
    {
        // ITEM 14 (owner ruling 2026-08-20). The highlight is decoupled from
        // the gates: each segment lights on its OWN reported-type flag, so a
        // type press lights on the radio's echo even on a net with no net ID.
        // The confirmed-type flags must be GONE from this row — leaving one
        // behind would restore the reported bug for that one type, and no VM
        // test can see which flag the markup binds.
        var root = PaneRoot(SettingsPane);
        var row = RowLabel(root, "Type").Parent!;

        var buttons = row.Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(3, buttons.Count);
        Assert.Equal(
            ["IsNarrowbandReported", "IsWidebandReported", "IsListReported"],
            buttons.Select(b => Assert.Single(HighlightTriggerBindings(b))));
        Assert.DoesNotContain(BindingTexts(row),
            t => t.Contains("Confirmed", StringComparison.Ordinal));

        // The OTHER half of the ruling, from the opposite direction: the
        // rebind must NOT have been over-applied. Nothing on this pane decides
        // VISIBILITY from a reported-type flag — what exists on screen is
        // still the CONFIRMED type's business, which is what keeps a wiped
        // net's `Hoptype WB` from offering band-edge entries.
        Assert.DoesNotContain(root.DescendantsAndSelf(), e =>
            (VisibilityBinding(e) ?? "").EndsWith("Reported", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHighlightTriggerReader_SeesBothWaysADataTriggerCanBind()
    {
        // Anti-vacuity for the two pins above, which are only as good as this
        // reader: a reader that found NO triggers would make the
        // "no Confirmed flags here" half pass forever. XAML can set
        // DataTrigger.Binding as an attribute or as a property element, and a
        // button may carry no trigger at all — all three run through the same
        // reader against a synthetic document whose answer is known.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Button Text="A">
                <Button.Triggers>
                  <DataTrigger TargetType="Button" Binding="{Binding IsNarrowbandReported}" Value="True" />
                </Button.Triggers>
              </Button>
              <Button Text="B">
                <Button.Triggers>
                  <DataTrigger TargetType="Button" Value="True">
                    <DataTrigger.Binding>{Binding IsListReported}</DataTrigger.Binding>
                  </DataTrigger>
                </Button.Triggers>
              </Button>
              <Button Text="C" />
            </ContentView>
            """);

        var buttons = markup.Root!.Elements()
            .Where(e => e.Name.LocalName == "Button").ToList();

        Assert.Equal("IsNarrowbandReported", Assert.Single(HighlightTriggerBindings(buttons[0])));
        Assert.Equal("IsListReported", Assert.Single(HighlightTriggerBindings(buttons[1])));
        Assert.Empty(HighlightTriggerBindings(buttons[2]));
    }

    /// <summary>Every property name a Button's own <c>DataTrigger</c>s bind, in
    /// document order — the highlight condition. Both forms a XAML property can
    /// take, per this file's structural-scan rule.</summary>
    private static IReadOnlyList<string> HighlightTriggerBindings(XElement button)
        =>
        [
            .. button.Descendants()
                .Where(e => e.Name.LocalName == "DataTrigger")
                .Select(e => PropertyValue(e, "Binding"))
                .Where(raw => raw is not null)
                .Select(raw => BindingName(raw!))
                .Where(name => name is not null)
                .Select(name => name!)
        ];

    // ==== ROUND 11 §7 ========================================================

    /// <summary>The Operate pane's frame order, exact — §7's reflow. An ORDER
    /// is invisible to every VM test, and the round-4 order (Select → Current →
    /// Status) was pinned nowhere at all, so it could have been reflowed by
    /// accident in either direction.</summary>
    [Fact]
    public void HopOperatePane_FrameOrder_IsCurrentNetThenStatusThenSelectNet()
    {
        Assert.Equal(["Current net", "Status", "Select net"], CardHeadings(OperatePane));
    }

    [Fact]
    public void TheFrameOrderReader_ReallyReadsHeadingsInDocumentOrder()
    {
        // Anti-vacuity: an equality against a list is only as good as the list
        // the reader produces. A reader that returned the headings SORTED, or
        // that missed one, would make the pin above meaningless — so the same
        // reader is run over a synthetic document whose order is known and
        // deliberately not alphabetical.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <Border><Label Text="Zebra" Style="{StaticResource CardHeading}" /></Border>
                <Border><Label Text="Alpha" Style="{StaticResource CardHeading}" /></Border>
                <Border><Label Text="Not a heading" Style="{StaticResource Caption}" /></Border>
              </VerticalStackLayout>
            </ContentView>
            """);

        Assert.Equal(["Zebra", "Alpha"], HeadingsIn(markup.Root!));
    }

    /// <summary>§7's net info view: the two-line read-back stack, its CONTENT
    /// and its PLACEMENT (positional doctrine — the plan pins where it sits,
    /// not only what it says).</summary>
    [Fact]
    public void TheNetInfoView_IsATwoLineStack_BesideThePicker_InTheResidualStarColumn()
    {
        var root = PaneRoot(OperatePane);

        // The stack is located by its FIRST-LINE HEADER, which §7 fixes as a
        // literal — and the markup takes it from the VM constant, so the pin,
        // the markup and the VM cannot drift into three different strings.
        var header = root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{x:Static vm:HopViewModel.NetInfoHeading}");
        Assert.Equal("Net · Net ID · Type", HopViewModel.NetInfoHeading);

        // CONTENT: heading / value / heading / value, in that order, with the
        // §7 style per line and the values BOUND (a literal would freeze what
        // the mirror decides).
        var stack = header.Parent!;
        var lines = stack.Elements().Where(e => e.Name.LocalName == "Label").ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(
            [
                "{x:Static vm:HopViewModel.NetInfoHeading}",
                "{Binding Hop.PickedNetInfoText}",
                "{Binding Hop.PickedNetValueHeading}",
                "{Binding Hop.PickedNetValueText}",
            ],
            lines.Select(TextOf));
        Assert.Equal(
            ["CellHeading", "CellValue", "CellHeading", "CellValue"],
            lines.Select(l => (PropertyValue(l, "Style") ?? "")
                .Replace("{StaticResource ", "", StringComparison.Ordinal)
                .Replace("}", "", StringComparison.Ordinal)));

        // PLACEMENT, three facts. (1) Centered horizontally — the stack itself
        // and every line in it.
        Assert.Equal("Center", PropertyValue(stack, "HorizontalOptions"));
        Assert.All(lines, l => Assert.Equal("Center", PropertyValue(l, "HorizontalOptions")));

        // (2) It sits in the RESIDUAL STAR column of the picker's grid…
        var grid = Ancestors(stack).First(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,*", PropertyValue(grid, "ColumnDefinitions"));
        var starChild = Ancestors(stack).TakeWhile(e => e != grid).Last();
        Assert.Equal("1", PropertyValue(starChild, "Grid.Column"));

        // (3) …BESIDE the picker, i.e. the Auto column really is the spinner.
        var picker = grid.Elements()
            .Single(e => PropertyValue(e, "Grid.Column") is null);
        Assert.Contains(picker.Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{Binding Hop.PickedNetText}");
    }

    [Fact]
    public void TheNetInfoView_LivesInTheSelectNetFrame_NotTheCurrentNetOne()
    {
        // §7 puts the PICKED net's read-back where the pick happens. Landing it
        // in the "Current net" frame instead would put two different nets under
        // one heading — the exact confusion the round-4 split was made to end.
        var root = PaneRoot(OperatePane);
        var header = root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{x:Static vm:HopViewModel.NetInfoHeading}");

        var card = Ancestors(header).First(e => e.Name.LocalName == "Border");
        Assert.Equal("Select net", HeadingsIn(card).Single());
    }

    [Fact]
    public void TheNoNetIdStatusLine_IsInTheStatusFrame_InTheStatusTextStyle()
    {
        // §7: HopViewModel renders the refusal; the STATE MACHINE is the
        // surface's. What markup owes is the line's tier and its home.
        var root = PaneRoot(OperatePane);

        var line = root.Descendants().Single(e =>
            e.Name.LocalName == "Label" && TextOf(e) == "{Binding Hop.StatusText}");

        Assert.Equal("{StaticResource StatusText}", PropertyValue(line, "Style"));
        Assert.Equal("{Binding Hop.HasStatusText}", PropertyValue(line, "IsVisible"));

        var card = Ancestors(line).First(e => e.Name.LocalName == "Border");
        Assert.Equal("Status", HeadingsIn(card).Single());
    }

    // ---- §7 / R11: the exclusion-bands section ------------------------------

    [Fact]
    public void TheExclusionSection_IsItsOwnCard_AboveTheClock_WithTheExactCaption()
    {
        var root = PaneRoot(SettingsPane);

        var card = ExclusionCard(root);
        Assert.Equal("Exclusion bands", HeadingsIn(card).Single());

        // BYTE-PINNED, and taken from the VM constant so markup and code read
        // one source. It is the only place the operator is told that a write
        // here REGENERATES the hopset.
        Assert.Contains(card.Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{x:Static vm:HopSettingsViewModel.ExcludeCaption}"
            && PropertyValue(e, "Style") == "{StaticResource Caption}");
        Assert.Equal(
            "Applies to WB nets. Changes regenerate the current hopset.",
            HopSettingsViewModel.ExcludeCaption);

        // The clock stays the pane's bottom element (the standing pin above);
        // the new card is a SIBLING before it, not a row inside the net editor
        // — the wire's `EXC` names no net.
        var siblings = card.Parent!.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();
        Assert.Equal("DeviceClockView", siblings[^1].Name.LocalName);
        Assert.Same(card, siblings[^2]);
    }

    [Fact]
    public void TheExclusionSection_IsTheListEditorIdiom_HeadedRowsPlusPerRowRemove()
    {
        var root = PaneRoot(SettingsPane);
        var card = ExclusionCard(root);

        // Header row: three headings, the Remove column deliberately UNHEADED.
        var headings = card.Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (PropertyValue(e, "Style") ?? "").Contains("CellHeading", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["Band", "Low (MHz)", "High (MHz)"], headings.Select(TextOf));

        // The row TEMPLATE mirrors the header, cell for cell and width for
        // width — a header row that does not line up with its rows is worse
        // than none.
        var template = card.Descendants().Single(e => e.Name.LocalName == "DataTemplate");
        var cells = template.Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (PropertyValue(e, "Style") ?? "").Contains("CellValue", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            ["{Binding BandText}", "{Binding LowText}", "{Binding HighText}"],
            cells.Select(TextOf));
        Assert.Equal(
            headings.Select(h => PropertyValue(h, "WidthRequest")),
            cells.Select(c => PropertyValue(c, "WidthRequest")));

        // ROW BUDGET (literal columns, the round-11 §4 idiom): the fixed cells
        // plus the action-class Remove must fit the 336-dp phone card.
        var widths = cells.Select(c => int.Parse(PropertyValue(c, "WidthRequest")!,
            System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([56, 76, 76], widths);
        Assert.Equal("8", PropertyValue(cells[0].Parent!, "ColumnSpacing"));
        Assert.True(widths.Sum() + 3 * 8 + 84 <= 336, "exclusion row overflows the phone card");

        // PER-ROW REMOVE: its own command identity (never the hop-frequency
        // row's), the BAND SLOT as its parameter — never a displayed MHz — and
        // HIDDEN on the unread placeholder, which has no row to remove.
        var remove = template.Descendants().Single(e =>
            e.Name.LocalName == "Button" && TextOf(e) == "Remove");
        Assert.Equal("{Binding RemoveBand}", PropertyValue(remove, "Command"));
        Assert.Equal("{Binding BandText}", PropertyValue(remove, "CommandParameter"));
        Assert.Equal("{Binding CanRemove}", PropertyValue(remove, "IsVisible"));

        // …and NO confirmation markup anywhere near it: per-row Removes are
        // unconfirmed by decision (round-10 §5), so a prompt binding creeping
        // in here would be a silent policy change.
        Assert.DoesNotContain(BindingTexts(card),
            t => t.Contains("Confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExclusionSection_HasBothStateTexts_AndAnAddRowWithItsDisabledReason()
    {
        var root = PaneRoot(SettingsPane);
        var card = ExclusionCard(root);

        // State 2 of three, byte-pinned via the VM constant. (State 1, the
        // hyphen ROW, and state 3 are the VM's projection — pinned there.)
        Assert.Contains(card.Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{x:Static vm:HopSettingsViewModel.NoExcludeBandsCaption}"
            && PropertyValue(e, "IsVisible") == "{Binding HasNoExcludeBands}");
        Assert.Equal("No exclusion bands programmed.", HopSettingsViewModel.NoExcludeBandsCaption);
        Assert.Equal("All 10 bands used.", HopSettingsViewModel.ExcludeFullReason);

        // The add row: two MHz entries and ONE Add, both entries committing on
        // Return like every other editor row on this pane.
        var add = card.Descendants().Single(e =>
            e.Name.LocalName == "Button"
            && PropertyValue(e, "Command") == "{Binding AddExcludeBandCommand}");
        Assert.Equal("Add", TextOf(add));

        var entries = card.Descendants().Where(e => e.Name.LocalName == "Entry").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(
            ["{Binding ExcludeLowInput, Mode=TwoWay}", "{Binding ExcludeHighInput, Mode=TwoWay}"],
            entries.Select(e => PropertyValue(e, "Text")));
        Assert.All(entries, e =>
            Assert.Equal("{Binding AddExcludeBandCommand}", PropertyValue(e, "ReturnCommand")));

        // The disabled REASON is on screen, not just in the VM: a greyed Add
        // with no explanation is the failure this round keeps removing.
        Assert.Contains(card.Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{Binding AddExcludeBandDisabledReason}"
            && PropertyValue(e, "IsVisible") == "{Binding HasAddExcludeBandDisabledReason}");
    }

    [Fact]
    public void TheExclusionSection_GreysWithThePane_AndKeepsItsOwnErrorLine()
    {
        var card = ExclusionCard(PaneRoot(SettingsPane));

        Assert.Contains(card.Descendants(),
            e => PropertyValue(e, "IsEnabled") == "{Binding AreControlsEnabled}");

        // Its OWN note, not the net editor's: InputError prefixes the picked
        // NET, and a global table has no net to name.
        Assert.Contains(card.Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{Binding ExcludeError}"
            && PropertyValue(e, "Style") == "{StaticResource ErrorCaption}");
        Assert.DoesNotContain(BindingTexts(card),
            t => t.Contains("InputError", StringComparison.Ordinal));
    }

    [Fact]
    public void TheListAddPlaceholder_NamesTheSpaceDelimiter_FromTheVmConstant()
    {
        // §7's exact placeholder. It matters because the delimiter closed to
        // SPACE this round: the hint is what tells the operator that a comma
        // is now part of the token rather than a separator.
        var root = PaneRoot(SettingsPane);

        var box = root.Descendants().Single(e =>
            e.Name.LocalName == "Entry"
            && PropertyValue(e, "Text") == "{Binding ListAddInput, Mode=TwoWay}");

        Assert.Equal("{x:Static vm:HopSettingsViewModel.ListAddPlaceholder}",
            PropertyValue(box, "Placeholder"));
        Assert.Equal("e.g. 5.320 7.450 (MHz, space-separated)",
            HopSettingsViewModel.ListAddPlaceholder);
    }

    // ---- Round 14 B / R2: the coupler row ----------------------------------

    [Fact]
    public void TheCouplerCard_IsThePanesFIRSTCard_ThenTheNetEditor_ThenExclusions()
    {
        // PLACEMENT IS THE CONTRACT. Round 14 §4-B put the coupler row
        // immediately ABOVE the exclusion table — the pane's WB corner. ROUND
        // 15 H-1 (owner) moves it to the TOP: it is the setting to check
        // BEFORE programming a net, not a footnote below the editor that
        // depends on it. Pinned by sibling INDEX, not "somewhere on the pane".
        var root = PaneRoot(SettingsPane);
        var card = CouplerCard(root);

        var siblings = card.Parent!.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();

        // Antenna coupler · Net programming · Exclusion bands · the clock.
        Assert.Same(card, siblings[0]);
        Assert.Same(NetProgrammingCard(root), siblings[1]);
        Assert.Same(ExclusionCard(root), siblings[2]);
        Assert.Equal("DeviceClockView", siblings[^1].Name.LocalName);
        Assert.Equal(4, siblings.Count);
    }

    /// <summary>The net-programming card — located by its heading, the same
    /// idiom the coupler and exclusion locators use.</summary>
    private static XElement NetProgrammingCard(XElement root)
        => root.Descendants().Single(e =>
            e.Name.LocalName == "Border"
            && e.Elements().SelectMany(c => c.Elements()).Any(l =>
                l.Name.LocalName == "Label" && TextOf(l) == "Net programming"));

    [Fact]
    public void TheCouplerRow_IsTheCopiedSsbRow_CellForCell()
    {
        // The owner's ask was "copy the SSB settings screen's control buttons",
        // so the pin compares the two markups rather than describing one of
        // them.
        //
        // AUDIT ROUND 1, MAJOR 2: this comparison used to name three
        // properties, and a HOP-only `Spacing` 6 -> 7 sailed through it. A
        // "cell for cell" claim that reads three cells is not that claim.
        //
        // AUDIT ROUND 2, MAJOR: the replacement was complete over ATTRIBUTES
        // but not over property ELEMENTS — it flattened them to their text and
        // could not see attached ones at all. Both holes are closed by
        // normalization, not by narrowing the claim (resolution A): every
        // property folds to one entry in whichever spelling it is written,
        // owned or attached, scalar or object-valued, with its content signed
        // recursively. The claim below is therefore the real one — the
        // comparison is COMPLETE over the compared subtree: every property,
        // every element's own text, and child ORDER, at every depth — so a
        // drift anywhere on either side breaks it whether or not anyone thought
        // to name that property. The four probes that pin the normalization
        // live in TheSignature_NormalizesBothXamlSpellings_OwnedAndATTACHED.
        //
        // DELIBERATE DIFFERENCES, exhaustively: ONE, added by
        // plan-clone-field-round2.md F4 (decision A-2) — the ItemTemplate's
        // resource KEY. Everything else is still attribute-for-attribute
        // identical, binding paths included: both view models expose
        // `InternalCouplerChoices`.
        //
        // WHY THE KEY HAD TO DIFFER. The copy brought
        // `{StaticResource ChoiceButton}` with it, and that template exists only
        // in the SSB pane's OWN resources. A ContentView resolves a
        // StaticResource against its own dictionary and the application's, never
        // a sibling view's, so entering Mode settings threw
        // `XamlParseException: StaticResource not found for key ChoiceButton` in
        // all three modes (crash buffer, 2026-08-21). A-2 chose a per-view copy
        // over a shared dictionary, and the copy carries its OWN key so that a
        // future copy-paste out of the HOP pane says where it came from.
        //
        // The exception is expressed as a SUBSTITUTION rather than by narrowing
        // the comparison: every other property, every element's own text and
        // child order are still compared whole, at every depth, which is what
        // audit rounds 1 and 2 bought and what weakening this would give back.
        var hopRow = CouplerRow(PaneRoot(SettingsPane));
        var ssbRow = CouplerRow(PaneRoot(SsbSettingsPane));

        var ssbSignature = MarkupSignature(ssbRow);
        // ANTI-VACUITY: the substitution below is only meaningful if the SSB
        // signature really carries the key it renames.
        Assert.Contains("{StaticResource ChoiceButton}", ssbSignature, StringComparison.Ordinal);
        Assert.Equal(
            ssbSignature.Replace(
                "{StaticResource ChoiceButton}", "{StaticResource HopChoiceButton}",
                StringComparison.Ordinal),
            MarkupSignature(hopRow));

        // …and the VALUES, so the two cannot drift together into something
        // else and still compare equal. (A copy pinned only against its source
        // is a pin against half the failure modes.)
        Assert.Equal("*,Auto", PropertyValue(hopRow, "ColumnDefinitions"));
        Assert.Equal("6", PropertyValue(hopRow, "ColumnSpacing"));

        foreach (var (row, templateKey) in new[]
            { (hopRow, "HopChoiceButton"), (ssbRow, "ChoiceButton") })
        {
            var caption = row.Elements().First(e => e.Name.LocalName == "Label");
            Assert.Equal("Internal coupler", TextOf(caption));
            Assert.Equal("{StaticResource Caption}", PropertyValue(caption, "Style"));

            var buttons = row.Elements()
                .Single(e => e.Name.LocalName == "HorizontalStackLayout");
            Assert.Equal("1", PropertyValue(buttons, "Grid.Column"));
            Assert.Equal("End", PropertyValue(buttons, "HorizontalOptions"));
            Assert.Equal("{Binding InternalCouplerChoices}",
                PropertyValue(buttons, "BindableLayout.ItemsSource"));
            // The ONE deliberate difference (F4 / A-2), asserted per pane rather
            // than dropped: each pane binds the template IT owns. That a key
            // resolves at all is the structural guard's job
            // (XamlResourceGuardTests); that each pane names its OWN is this
            // one's.
            Assert.Equal($"{{StaticResource {templateKey}}}",
                PropertyValue(buttons, "BindableLayout.ItemTemplate"));
            Assert.Equal("6", PropertyValue(buttons, "Spacing"));
        }
    }

    [Fact]
    public void TheCopyComparison_ReallyReadsEveryProperty_NotAHandfulOfNamedOnes()
    {
        // ANTI-VACUITY for the comparison above, and the direct answer to what
        // the audit found: the signature must CHANGE when any single property
        // changes. Proved on a throwaway in-memory clone rather than by
        // trusting the reader — the previous version of that test would have
        // passed this file's own description of it.
        var row = CouplerRow(PaneRoot(SettingsPane));
        var baseline = MarkupSignature(row);

        // (a) an attribute VALUE on a nested child — the auditor's own mutation
        //     (Spacing on the button stack). The new value is DERIVED from the
        //     old one rather than written as a literal, so this pin cannot be
        //     accidentally satisfied by markup that already carries the literal.
        var spacingChanged = new XElement(row);
        var stack = spacingChanged.Elements()
            .Single(e => e.Name.LocalName == "HorizontalStackLayout");
        Assert.NotNull(PropertyValue(stack, "Spacing"));
        stack.SetAttributeValue("Spacing", PropertyValue(stack, "Spacing") + "9");
        Assert.NotEqual(baseline, MarkupSignature(spacingChanged));

        // (b) an attribute REMOVED
        var attributeDropped = new XElement(row);
        attributeDropped.Elements()
            .Single(e => e.Name.LocalName == "HorizontalStackLayout")
            .Attribute("HorizontalOptions")!.Remove();
        Assert.NotEqual(baseline, MarkupSignature(attributeDropped));

        // (c) child ORDER — the caption and the buttons swapped puts the
        //     control in the star column and the label in the Auto one
        var reordered = new XElement(row);
        var first = reordered.Elements().First();
        first.Remove();
        reordered.Add(first);
        Assert.NotEqual(baseline, MarkupSignature(reordered));

        // …and the signature is not simply everything-differs noise: an
        // untouched clone matches.
        Assert.Equal(baseline, MarkupSignature(new XElement(row)));

        // (d) an element's own TEXT — the content-property spelling of a
        //     caption. Without this the signature would call two Labels with
        //     different inner text identical.
        var retexted = new XElement(row);
        retexted.Elements().First(e => e.Name.LocalName == "Label").Add("smuggled");
        Assert.NotEqual(baseline, MarkupSignature(retexted));

        // …and the signature is not simply everything-differs noise: an
        // untouched clone matches.
        Assert.Equal(baseline, MarkupSignature(new XElement(row)));

        // …and it is not vacuously short — it really carries the row's
        // properties, so an empty-signature bug could not pass (a) to (d).
        Assert.Contains("ColumnDefinitions=*,Auto", baseline, StringComparison.Ordinal);
        Assert.Contains("BindableLayout.ItemsSource={Binding InternalCouplerChoices}",
            baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignature_NormalizesBothXamlSpellings_OwnedAndATTACHED()
    {
        // AUDIT ROUND 2, MAJOR — the auditor's own four probes, pinned as
        // EXPECTATIONS rather than described in a comment. Round 1's signature
        // passed the first and failed the other three: it reduced a property
        // element to its concatenated text (losing every nested attribute and
        // all ordering) and it only recognised property elements OWNED by the
        // parent type, so an ATTACHED one disappeared from the signature
        // altogether — two rows differing only there compared equal.
        //
        // The contract these four fix: ONE property, EITHER spelling, ONE
        // signature entry — and different values are different signatures,
        // however deeply the difference sits.

        // (1) OWNED scalar: attribute spelling ≡ property-element spelling.
        Assert.Equal(
            Sig("<Grid ColumnSpacing='6' />"),
            Sig("<Grid><Grid.ColumnSpacing>6</Grid.ColumnSpacing></Grid>"));

        // (2) ATTACHED scalar: same equivalence, and the one round 1 got
        //     wrong — `BindableLayout.ItemTemplate` is not owned by the
        //     stack, so the prefix test never saw it.
        Assert.Equal(
            Sig("<HorizontalStackLayout BindableLayout.ItemTemplate='{StaticResource ChoiceButton}' />"),
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "{StaticResource ChoiceButton}"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"));

        // (3) ATTACHED property elements with DIFFERENT values must NOT
        //     collide — round 1 dropped both, so they did.
        Assert.NotEqual(
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "{StaticResource ChoiceButton}"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"),
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "{StaticResource SomethingElse}"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"));

        // (4) OBJECT-VALUED property elements whose nested children differ
        //     must NOT collide — `e.Value` saw only text, so a template whose
        //     inner markup changed entirely signed identically.
        Assert.NotEqual(
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "<DataTemplate><Label Text='A' /></DataTemplate>"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"),
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "<DataTemplate><Label Text='B' /></DataTemplate>"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"));

        // …and the equalities in (1) and (2) are not the signature collapsing
        // everything to a constant: two plainly different elements differ.
        Assert.NotEqual(Sig("<Grid ColumnSpacing='6' />"), Sig("<Grid ColumnSpacing='7' />"));
        Assert.NotEqual(Sig("<Grid />"), Sig("<Label />"));

        // …and an object-valued property element still differs from a scalar
        // one of the same name, rather than both flattening to the same text.
        Assert.NotEqual(
            Sig("<HorizontalStackLayout><BindableLayout.ItemTemplate>"
                + "<DataTemplate><Label Text='A' /></DataTemplate>"
                + "</BindableLayout.ItemTemplate></HorizontalStackLayout>"),
            Sig("<HorizontalStackLayout BindableLayout.ItemTemplate='A' />"));
    }

    /// <summary>Signature of a markup fragment written inline — the probes
    /// above compare fragments against each other, never against the panes, so
    /// their namespace-free names are exactly the point.</summary>
    private static string Sig(string xaml) => MarkupSignature(XElement.Parse(xaml));

    /// <summary>A COMPLETE structural signature of an element and its subtree.
    /// Every PROPERTY the element carries — in either XAML spelling, attribute
    /// or property ELEMENT, owned (<c>&lt;Grid.ColumnDefinitions&gt;</c>) or
    /// attached (<c>&lt;BindableLayout.ItemTemplate&gt;</c>) — normalizes to
    /// one <c>qualified-name = content</c> entry, sorted, because XML attribute
    /// order carries no meaning. Then the element's own direct TEXT, then its
    /// real children in DOCUMENT order, which does carry meaning. Recursive
    /// throughout: a property's content is signed the same way as anything
    /// else, so two object-valued properties differ whenever their contents do.
    ///
    /// <para><b>Audit round 2, MAJOR (resolution A — full normalization).</b>
    /// Round 1's version reduced a property element to <c>e.Value</c>, which
    /// dropped every attribute and every ordering inside it, and it recognised
    /// only OWNED property elements, so an ATTACHED one vanished from the
    /// signature entirely. Four probes are pinned as expectations in
    /// <c>TheSignature_NormalizesBothXamlSpellings_…</c> below: owned scalar
    /// attribute ≡ owned property element; attached scalar attribute ≡ attached
    /// property element; attached property elements with different values
    /// DIFFER; object-valued property elements with different nested children
    /// DIFFER. The alternative — refusing to compare any subtree containing a
    /// property element — was rejected: it closes the hole by making the tool
    /// unusable the first time markup legitimately needs one.</para></summary>
    private static string MarkupSignature(XElement element)
    {
        var properties = element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            // Qualified, so `x:DataType` and a same-named attribute from
            // another namespace cannot collide. Unprefixed attributes carry no
            // namespace, so ordinary ones read as their plain name.
            .Select(a => a.Name.ToString() + "=" + a.Value)
            .Concat(element.Elements()
                .Where(IsPropertyElement)
                .Select(p => PropertyKey(element, p) + "=" + PropertyContent(p)))
            .OrderBy(p => p, StringComparer.Ordinal);

        var children = element.Elements()
            .Where(e => !IsPropertyElement(e))
            .Select(MarkupSignature);

        return element.Name.ToString()
            + "[" + string.Join("; ", properties) + "]"
            + DirectText(element)
            + "(" + string.Join("; ", children) + ")";
    }

    /// <summary>The element's OWN text, not its descendants' — so a
    /// content-property Label (<c>&lt;Label&gt;text&lt;/Label&gt;</c>) is
    /// distinguishable from an empty one. Absent when there is none, so it adds
    /// nothing to the ordinary attribute-only case.</summary>
    private static string DirectText(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        return text.Length == 0 ? "" : "{" + text + "}";
    }

    /// <summary>A property element's VALUE, signed so that it compares equal to
    /// the attribute spelling of the same property: a scalar one is its text
    /// (exactly what the attribute would hold), an object-valued one is the
    /// recursive signature of its content.</summary>
    private static string PropertyContent(XElement property)
        => property.Elements().Any()
            ? "(" + string.Join("; ", property.Elements().Select(MarkupSignature)) + ")"
            : property.Value.Trim();

    /// <summary>A DOTTED element name is XAML's property-element syntax — type
    /// names never contain a dot, and prefixes use a colon. That one rule
    /// covers both kinds, which is the round-2 fix: the parent-prefix test it
    /// replaces saw <c>&lt;Grid.ColumnDefinitions&gt;</c> under a
    /// <c>Grid</c> and missed <c>&lt;BindableLayout.ItemTemplate&gt;</c>
    /// entirely.</summary>
    private static bool IsPropertyElement(XElement child)
        => child.Name.LocalName.Contains('.', StringComparison.Ordinal);

    /// <summary>The property's name AS AN ATTRIBUTE would spell it — which is
    /// what makes the two spellings fold together. An OWNED property drops its
    /// owner prefix (<c>&lt;Grid.ColumnSpacing&gt;</c> on a Grid → the
    /// <c>ColumnSpacing</c> attribute); an ATTACHED one keeps its dotted name,
    /// because the attribute spelling is dotted too
    /// (<c>BindableLayout.ItemTemplate="…"</c>).</summary>
    private static string PropertyKey(XElement parent, XElement property)
    {
        var owner = parent.Name.LocalName + ".";
        var name = property.Name.LocalName;
        return name.StartsWith(owner, StringComparison.Ordinal) ? name[owner.Length..] : name;
    }

    [Fact]
    public void TheCouplerCard_CarriesNOCaption_AndStillGreysWithThePane()
    {
        var card = CouplerCard(PaneRoot(SettingsPane));

        // ROUND 15 H-1: the advisory caption is DELETED, and so is the VM
        // constant that carried it — a binding left pointing at a deleted
        // static would not even compile, but a LEFTOVER caption of any kind
        // would, so the absence is asserted structurally: the card holds its
        // heading, its row label and nothing else that is prose.
        Assert.DoesNotContain(BindingTexts(card),
            t => t.Contains("CouplerCaption", StringComparison.Ordinal));
        Assert.Empty(card.Descendants().Where(e =>
            e.Name.LocalName == "Label"
            && PropertyValue(e, "Style") == "{StaticResource Caption}"
            && TextOf(e) != "Internal coupler"));

        // Anti-vacuity: the pane's OTHER card still carries its caption, so a
        // reader that saw no captions at all would fail here.
        Assert.Contains(ExclusionCard(PaneRoot(SettingsPane)).Descendants(), e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "{x:Static vm:HopSettingsViewModel.ExcludeCaption}");

        // The CONTROLS grey with the pane like every other send on it…
        Assert.Contains(card.Descendants(),
            e => PropertyValue(e, "IsEnabled") == "{Binding AreControlsEnabled}");

        // …and NO confirmation markup: this row is a plain send, never a
        // popup-guarded one (the round-10 §5 matrix covers whole-record
        // destruction, which a coupler press is not).
        Assert.DoesNotContain(BindingTexts(card),
            t => t.Contains("Confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGuard_ActuallyFindsTwoCouplerRows_NotAnEmptyScan()
    {
        // Anti-vacuity for the copy test above: the reader must locate a row on
        // EACH pane. A single-sided scan would let the SSB row be deleted, or
        // the HOP one never added, with the comparison passing vacuously.
        foreach (var pane in new[] { SettingsPane, SsbSettingsPane })
        {
            var row = CouplerRow(PaneRoot(pane));
            Assert.Equal("Grid", row.Name.LocalName);
            Assert.Equal(2, row.Elements().Count());
        }
    }

    /// <summary>The coupler card on the HOP settings pane, located by its
    /// heading.</summary>
    private static XElement CouplerCard(XElement root)
        => root.Descendants()
            .Where(e => e.Name.LocalName == "Border")
            .Single(b => HeadingsIn(b).Contains("Antenna coupler"));

    /// <summary>The coupler ROW on either pane, located by the binding it
    /// carries — the one thing both copies must share.</summary>
    private static XElement CouplerRow(XElement root)
        => root.Descendants()
            .Single(e => e.Name.LocalName == "Grid"
                && e.Elements().Any(c =>
                    PropertyValue(c, "BindableLayout.ItemsSource")
                        == "{Binding InternalCouplerChoices}"));

    /// <summary>The exclusion card, located by its heading.</summary>
    private static XElement ExclusionCard(XElement root)
        => root.Descendants()
            .Where(e => e.Name.LocalName == "Border")
            .Single(b => HeadingsIn(b).Contains("Exclusion bands"));

    /// <summary>Every <c>CardHeading</c> Text under an element, in DOCUMENT
    /// order — which is what makes it a frame-ORDER reader.</summary>
    private static IReadOnlyList<string> HeadingsIn(XElement scope)
        =>
        [
            .. scope.Descendants()
                .Where(e => e.Name.LocalName == "Label"
                    && (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal))
                .Select(e => TextOf(e))
                .Where(t => t is not null)
                .Select(t => t!)
        ];

    private static IReadOnlyList<string> CardHeadings(string relativePath)
        => HeadingsIn(PaneRoot(relativePath));

    private static IEnumerable<XElement> Ancestors(XElement start)
    {
        for (var e = start.Parent; e is not null; e = e.Parent) yield return e;
    }

    private static XElement PaneRoot(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), "pane markup missing: " + relativePath);
        return XDocument.Load(path).Root!;
    }

    private static IReadOnlyList<string> CellHeadings(string relativePath)
        =>
        [
            .. PaneRoot(relativePath).Descendants()
                .Where(e => e.Name.LocalName == "Label"
                    && (PropertyValue(e, "Style") ?? "").Contains("CellHeading", StringComparison.Ordinal))
                .Select(e => TextOf(e))
                .Where(t => t is not null)
                .Select(t => t!)
        ];

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
