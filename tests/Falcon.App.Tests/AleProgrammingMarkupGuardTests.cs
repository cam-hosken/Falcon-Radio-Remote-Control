using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// The ALE programming cards' MARKUP facts (plan-ale-programming.md §9 phase-2
/// clause 4), in the house structural-scan style
/// (SettingsPlacementGuardTests / HopPaneMarkupGuardTests): placement, tab
/// structure, kind-switched visibility, alignment, picker idiom and the ERASE
/// block's home. None of these is visible to a ViewModel test — the VM is
/// correct whichever element the markup happens to bind — and every one of
/// them is a decision the owner made for a reason.
///
/// <para><b>XAML is parsed as the XML it is</b>, never regex-matched: a
/// property can be set as an attribute OR as a property element, an XML
/// comment is not an element at all, and only the parsed tree exposes the
/// ANCESTOR chain that "inside the book tab" actually means. Every scanner
/// below carries an anti-vacuity self-pin — a guard that reads nothing passes
/// its absence assertions forever.</para>
///
/// <para><b>ACCEPTED LIMITATION</b> (the standing one for every scan in this
/// suite): markup emitted or reparented from code-behind is invisible here.
/// Adversarial construction rather than a plausible regression; the backstops
/// are the UIA gate evidence and the bench pass.</para>
/// </summary>
public class AleProgrammingMarkupGuardTests
{
    private static readonly string Pane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleSettingsPaneView.xaml");

    private static readonly string AddressCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleProgrammingView.xaml");

    private static readonly string GroupsCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleScanGroupsView.xaml");

    /// <summary>The anti-vacuity partner for the "no Refresh here" pins, since
    /// round 10 §6 left the ALE pane without one.</summary>
    private static readonly string SsbPane =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");

    // ==== Placement on the pane ============================================

    [Fact]
    public void BothCards_SitBelowTheScanAndTimingCard_AndAreThePanesLastElements()
    {
        // ROUND 10 §6: the pane's Refresh button is DELETED, so the two
        // programming cards are now the BOTTOM of the pane. The ordering
        // contract they actually carry — below the settings, address first —
        // is unchanged.
        var stack = PaneChildren();

        int scanAndTiming = stack.FindIndex(e => Descends(e, l => TextOf(l) == "Scan & timing"));
        int address = stack.FindIndex(e => e.Name.LocalName == "AleProgrammingView");
        int groups = stack.FindIndex(e => e.Name.LocalName == "AleScanGroupsView");

        Assert.True(scanAndTiming >= 0, "the Scan & timing card is gone from the ALE pane");
        Assert.True(address >= 0, "the Address programming card is not on the ALE pane");
        Assert.True(groups >= 0, "the Scan channel groups card is not on the ALE pane");

        Assert.True(scanAndTiming < address, "the address card must sit BELOW Scan & timing");
        Assert.True(address < groups, "the address card comes first, then the groups card");
        Assert.Equal(stack.Count - 1, groups);
    }

    [Fact]
    public void ThePanesRefreshButton_IsGONE_AndNothingBindsItsCommand()
    {
        // §6's ABSENCE pin. Round 9 relabeled this button "Refresh ALE
        // settings"; round 10 deleted it outright, because the nine settings
        // arrive in one lazily-sent SH and every card below re-reads its own
        // target on landing. A binding left pointing at the deleted command
        // would resolve to nothing SILENTLY in MAUI, so both are pinned.
        Assert.DoesNotContain(Root(Pane).Descendants(), IsRefreshButton);

        var refreshBindings = BindingTexts(Root(Pane))
            .Where(t => t.StartsWith("{Binding", StringComparison.Ordinal)
                        && t.Contains("Refresh", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.Empty(refreshBindings);

        // Anti-vacuity, both readers: the pane really does have buttons and
        // bindings for this scan to have seen.
        Assert.Contains(Root(Pane).Descendants(), e => e.Name.LocalName == "Button");
        Assert.Contains(BindingTexts(Root(Pane)),
            t => t.Contains("SetAllCallCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void NeitherCard_CarriesARefreshButton_TheRoundNineDoctrine()
    {
        foreach (var card in new[] { AddressCard, GroupsCard })
            Assert.DoesNotContain(Root(card).Descendants(), IsRefreshButton);
    }

    [Fact]
    public void TheRefreshDetector_SeesBothWaysTheTextCanBeSet()
    {
        // Anti-vacuity for the pin above: a "no Refresh button" assertion is
        // only as good as its detector. Attribute form, property-element form,
        // and an XML comment (which is not an element at all) as a unit.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <Button Text="Refresh" />
                <Button><Button.Text>Refresh ALE settings</Button.Text></Button>
                <Button Text="Program" />
                <!-- <Button Text="Refresh" /> -->
              </VerticalStackLayout>
            </ContentView>
            """);

        Assert.Equal(2, markup.Root!.Descendants().Count(IsRefreshButton));

        // …and the same detector, on a pane that genuinely HAS one, finds it.
        // ROUND 10 §6 deleted the ALE pane's button, so the anti-vacuity
        // partner RETARGETS to the SSB pane — which keeps two of the three
        // surviving Refresh buttons in the app.
        Assert.Contains(Root(SsbPane).Descendants(), IsRefreshButton);
    }

    // ==== Tab strips and default tabs ======================================

    public static TheoryData<string, string, string, string> TabStrips => new()
    {
        { nameof(AddressCard), "Program", "Address book", "IsBookTabOpen" },
        { nameof(GroupsCard), "Program", "Groups", "IsGroupsTabOpen" },
    };

    [Theory]
    [MemberData(nameof(TabStrips))]
    public void EachCard_HasATwoButtonTabStrip_WithTheLeftTabHighlightedByDefault(
        string card, string left, string right, string flag)
    {
        var root = Root(card == nameof(AddressCard) ? AddressCard : GroupsCard);

        var leftTab = root.Descendants().Single(e =>
            e.Name.LocalName == "Button" && TextOf(e) == left
            && (PropertyValue(e, "Command") ?? "").Contains("OpenProgramTabCommand", StringComparison.Ordinal));
        var rightTab = root.Descendants().Single(e =>
            e.Name.LocalName == "Button" && TextOf(e) == right
            && (PropertyValue(e, "Command") ?? "").StartsWith("{Binding Open", StringComparison.Ordinal));

        // The strip is ONE grid: the two tabs are siblings, not stacked.
        Assert.Same(leftTab.Parent, rightTab.Parent);

        // The LEFT tab lights when the flag is False — i.e. Program is the
        // default, which is also what the bodies' literal IsVisible says.
        Assert.Equal(flag, TriggerBinding(leftTab));
        Assert.Equal("False", TriggerValue(leftTab));
        Assert.Equal(flag, TriggerBinding(rightTab));
        Assert.Equal("True", TriggerValue(rightTab));
    }

    [Fact]
    public void TheAddressCard_ProgramTabIsTheDefaultBody_AndTheBookTabIsHidden()
    {
        Assert.Equal("True", PropertyValue(AddressProgramTab(), "IsVisible"));
        Assert.Equal("False", PropertyValue(AddressBookTab(), "IsVisible"));
        Assert.NotSame(AddressProgramTab(), AddressBookTab());
    }

    [Fact]
    public void TheGroupsCard_ProgramTabIsTheDefaultBody_AndTheGroupsTabIsHidden()
    {
        Assert.Equal("True", PropertyValue(GroupsProgramTab(), "IsVisible"));
        Assert.Equal("False", PropertyValue(GroupsListTab(), "IsVisible"));
        Assert.NotSame(GroupsProgramTab(), GroupsListTab());
    }

    [Fact]
    public void TheTabSubtrees_AreDistinct_AndEachHoldsOnlyWhatIdentifiesIt()
    {
        // Anti-vacuity for every "inside tab X" pin below: if the two subtrees
        // ever resolved to the same element, "inside one and outside the other"
        // would be unsatisfiable and the pins would be testing nothing.
        Assert.Contains(AddressProgramTab().Descendants(), IsActionButton);
        Assert.DoesNotContain(AddressProgramTab().Descendants(), IsEraseButton);
        Assert.Contains(AddressBookTab().Descendants(), IsEraseButton);
        Assert.DoesNotContain(AddressBookTab().Descendants(), IsActionButton);

        Assert.Contains(GroupsProgramTab().Descendants(), IsAddChannelButton);
        Assert.DoesNotContain(GroupsProgramTab().Descendants(), IsGroupListLayout);
        Assert.Contains(GroupsListTab().Descendants(), IsGroupListLayout);
        Assert.DoesNotContain(GroupsListTab().Descendants(), IsAddChannelButton);
    }

    // ==== The ERASE block lives on the BOOK tab ============================

    [Fact]
    public void TheErase_IsONEFramelessButton_LastOnTheAddressBookTab()
    {
        // ROUND 15 E-3 (owner). The warn-stroked Border, the "Erase address
        // book" SubHeading and the standing WarnCaption paragraph are DELETED:
        // what stops a mis-press is the question, which is asked on EVERY
        // press. What is left is one button whose own text says what it does.
        // ROUND 10 §5's facts still hold — no typed token anywhere, and no
        // CommandParameter (the popup asks; the command sends).
        var root = Root(AddressCard);

        var erase = Assert.Single(root.Descendants().Where(IsEraseButton));
        Assert.Equal("Erase address book", TextOf(erase));
        Assert.Equal("End", PropertyValue(erase, "HorizontalOptions"));
        Assert.Null(PropertyValue(erase, "CommandParameter"));

        Assert.Contains(AddressBookTab(), erase.Ancestors());
        Assert.DoesNotContain(AddressProgramTab(), erase.Ancestors());

        // LAST child of the tab, and NO Border between it and the tab body:
        // the frame is gone, not merely restyled.
        Assert.Same(AddressBookTab(), erase.Parent);
        Assert.Same(erase, AddressBookTab().Elements().Last());
        Assert.DoesNotContain(erase.Ancestors().TakeWhile(a => a != AddressBookTab()),
            a => a.Name.LocalName == "Border");

        // The deleted furniture, by name.
        Assert.DoesNotContain(root.Descendants(), e =>
            e.Name.LocalName == "Label" && TextOf(e) == "Erase address book");
        Assert.DoesNotContain(root.Descendants(), e =>
            (PropertyValue(e, "Style") ?? "").Contains("WarnCaption", StringComparison.Ordinal));
        Assert.DoesNotContain(BindingTexts(root),
            t => t.Contains("EraseWarningText", StringComparison.Ordinal));
        Assert.DoesNotContain(BindingTexts(root),
            t => t.Contains("EraseInput", StringComparison.Ordinal));

        // Anti-vacuity: the card genuinely HAS entries and Borders elsewhere,
        // so the absences above are facts rather than a blind reader. (The
        // card's own outer Card border is the one that remains.)
        Assert.Contains(root.Descendants(), e => e.Name.LocalName == "Entry");
        Assert.Contains(root.Descendants(), e => e.Name.LocalName == "Border");
    }

    [Fact]
    public void TheInlineDeleteConfirmBox_IsGONE_AndNothingBindsItsState()
    {
        // §5's other deletion on this card: the pending-confirm box, with its
        // Proceed/Cancel pair, is replaced by the popup. Its markup pins moved
        // to the VM's lifecycle tests; what belongs HERE is that the box and
        // its bindings really left.
        var root = Root(AddressCard);

        foreach (var name in new[]
        {
            "IsDeleteConfirmOpen", "PendingDeleteLabel", "DeleteWarningText",
            "ConfirmDeleteCommand", "CancelDeleteCommand",
        })
            Assert.DoesNotContain(BindingTexts(root),
                t => t.Contains(name, StringComparison.Ordinal));

        Assert.DoesNotContain(root.Descendants(), e => TextOf(e) == "Proceed");

        // Anti-vacuity: the per-row Delete that RAISES the popup is still here.
        Assert.Contains(BindingTexts(root),
            t => t.Contains("RequestDeleteCommand", StringComparison.Ordinal)
                 || t.Contains("{Binding Delete}", StringComparison.Ordinal));
    }

    // ==== §7: the self-length correction on the card ========================

    [Fact]
    public void TheNameEntry_CarriesTheFifteenCharacterPlaceholder_AndCap()
    {
        var entry = Root(AddressCard).Descendants().Single(e =>
            e.Name.LocalName == "Entry"
            && (PropertyValue(e, "Text") ?? "").Contains("NameInput", StringComparison.Ordinal));

        Assert.Equal("1-15 characters", PropertyValue(entry, "Placeholder"));
        Assert.Equal("15", PropertyValue(entry, "MaxLength"));
    }

    // ==== ROUND 11 §5: the standing captions DIE, one CONTEXTUAL hint lands ==

    [Fact]
    public void TheStandingSelfGateCaptionLines_AreGONE_FromTheMarkup()
    {
        // §5's deletion, as an ABSENCE: the two round-10 Caption lines and the
        // group-0 caption they replaced are all gone. The anti-vacuity partner
        // is the pin below — the markup still binds a VM static for the HINT,
        // so this scanner demonstrably reads x:Static texts.
        var texts = BindingTexts(Root(AddressCard)).ToList();

        foreach (var dead in new[]
        {
            "SelfGateCaptionLine1", "SelfGateCaptionLine2", "SelfGateCaption",
            "GroupZeroCaption", "MemberLogCaption",
        })
            Assert.DoesNotContain(texts, t => t.Contains(dead, StringComparison.Ordinal));
    }

    [Fact]
    public void TheContextualGateHint_IsOneCaptionLine_GatedOnShowSelfGateHint()
    {
        // R2: ONE line, bound to the VM's own static (a literal here could
        // drift from the string the VM test pins byte-for-byte), Caption-styled,
        // on the PROGRAM tab beside the name and group rows it explains — and
        // CONDITIONAL, which is the whole ruling: an always-on caption is what
        // it replaced.
        var hints = Root(AddressCard).Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (PropertyValue(e, "Text") ?? "")
                    .Contains("AleProgrammingViewModel.SelfGateHint", StringComparison.Ordinal))
            .ToList();

        var hint = Assert.Single(hints);
        Assert.Equal("{StaticResource Caption}", PropertyValue(hint, "Style"));
        Assert.Contains(AddressProgramTab(), hint.Ancestors());
        Assert.Equal("ShowSelfGateHint", VisibilityBinding(hint));
    }

    // ==== Kind-switched visibility =========================================

    /// <summary>Each kind-switched section and the flag that decides whether it
    /// is on screen. Getting one wrong offers an associated-self wheel on a
    /// SELF — and no VM test can see it, because the VM is correct either
    /// way.</summary>
    /// <remarks>ROUND 11 §5 deleted the "Net members" SubHeading, so the
    /// member section's anchor is its own row label — the one the wheel it
    /// gates sits on.</remarks>
    public static TheoryData<string, string> KindSections => new()
    {
        { "Name", "ShowAddressFields" },
        { "Channel group", "ShowAddressFields" },
        { "Associated self", "ShowAssociatedSelf" },
        { "Net", "ShowMemberSection" },
        { "Member", "ShowMemberSection" },
    };

    [Theory]
    [MemberData(nameof(KindSections))]
    public void EachKindSwitchedSection_IsBoundToItsOwnFlag(string anchorLabel, string expectedFlag)
        => Assert.Equal(expectedFlag, AncestorVisibilityBinding(RowLabel(anchorLabel)));

    /// <summary>A ROW LABEL — the Caption-styled one. Style-scoped since round
    /// 11 §5: "Member" is now BOTH a row label and the member table's column
    /// heading, and only the row label anchors a section.</summary>
    private static XElement RowLabel(string text)
        => Root(AddressCard).Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == text
            && (PropertyValue(e, "Style") ?? "").Contains("Caption", StringComparison.Ordinal));

    [Fact]
    public void TheKindSectionGuard_ResolvesTwoDifferentElements_AndTwoDifferentFlags()
    {
        // Anti-vacuity: two anchors resolving to the same owner (or to the
        // same flag) would let one binding satisfy both theory cases.
        var owners = new[] { "Name", "Channel group", "Associated self", "Net", "Member" }
            .Select(RowLabel)
            .Select(AncestorSettingVisibility)
            .ToList();

        Assert.All(owners, o => Assert.NotNull(o));
        // FOUR distinct owners (Net and Member share the picker section) over
        // THREE distinct flags — so no one binding can satisfy every case.
        Assert.Equal(4, owners.Distinct().Count());
        Assert.Equal(3, owners.Select(o => VisibilityBinding(o!)).Distinct().Count());
    }

    // ==== Segment rows are right-aligned ===================================

    [Fact]
    public void TheKindSegmentRow_IsRightAligned_AndItsButtonsTakeTheWideWidth()
    {
        // ROUND 10 §3: the row keeps its right alignment and the house 6-dp
        // spacing; what changed is the button WIDTH (SegmentWidthWide) and the
        // label PLACEMENT (now LEFT — pinned structurally in
        // SettingsPlacementGuardTests, which owns the five-row contract).
        var row = Root(AddressCard).Descendants().Single(e =>
            (PropertyValue(e, "BindableLayout.ItemsSource") ?? "")
                .Contains("{Binding KindChoices}", StringComparison.Ordinal));

        Assert.Equal("End", PropertyValue(row, "HorizontalOptions"));
        Assert.Equal("6", PropertyValue(row, "Spacing"));

        var button = Root(AddressCard).Descendants()
            .Where(e => e.Name.LocalName == "DataTemplate")
            .Single(e => e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "AleChoiceButton"))
            .Descendants().First(e => e.Name.LocalName == "Button");
        Assert.Equal("{StaticResource SegmentWidthWide}", PropertyValue(button, "WidthRequest"));
    }

    [Fact]
    public void TheAlignmentReader_SeesBothWaysThePropertyCanBeSet()
    {
        // Anti-vacuity for the pin above AND for every PropertyValue read in
        // this file: attribute form, property-element form, and "unset".
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
            .Where(e => e.Name.LocalName == "HorizontalStackLayout").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("End", PropertyValue(rows[0], "HorizontalOptions"));
        Assert.Equal("End", PropertyValue(rows[1], "HorizontalOptions"));
        Assert.Null(PropertyValue(rows[2], "HorizontalOptions"));
    }

    // ==== The two picker idioms ============================================

    [Fact]
    public void TheGroupsCard_UsesTheVERTICALSpinner_ForTargetIdentity()
    {
        // The spinner is WHICH GROUP the card edits, so it is the vertical
        // ▲/▼ idiom and a spin READS. Its two buttons are siblings in one
        // vertical stack with the picked-group label between them.
        var root = Root(GroupsCard);

        var up = root.Descendants().Single(e => e.Name.LocalName == "Button" && TextOf(e) == "▲");
        var down = root.Descendants().Single(e => e.Name.LocalName == "Button" && TextOf(e) == "▼");

        Assert.Equal("{Binding GroupUpCommand}", PropertyValue(up, "Command"));
        Assert.Equal("{Binding GroupDownCommand}", PropertyValue(down, "Command"));
        Assert.Same(up.Parent, down.Parent);
        Assert.Equal("VerticalStackLayout", up.Parent!.Name.LocalName);
        Assert.Contains(up.Parent.Elements(), e =>
            e.Name.LocalName == "Label"
            && (PropertyValue(e, "Text") ?? "").Contains("PickedGroupText", StringComparison.Ordinal));

        // …and there is no horizontal wheel on this card: nothing here is a
        // pending selection.
        Assert.DoesNotContain(root.Descendants(), e => TextOf(e) is "◀" or "▶");
    }

    [Fact]
    public void TheAddressCard_KeepsExactlyONEHorizontalWheel_ForItsPendingSelection()
    {
        // ROUND 15 E-5/E-1 (critic F48): the associated-self and member wheels
        // became PICKERS, so the card is down to ONE wheel — the channel
        // group. It is still ◀/▶, because it is still a value the operator is
        // composing rather than a target identity.
        var root = Root(AddressCard);

        Assert.Equal(1, root.Descendants().Count(e => TextOf(e) == "◀"));
        Assert.Equal(1, root.Descendants().Count(e => TextOf(e) == "▶"));
        Assert.DoesNotContain(root.Descendants(), e => TextOf(e) is "▲" or "▼");

        // …and the two it replaced are Pickers now, bound to the VM seats.
        var pickers = root.Descendants().Where(e => e.Name.LocalName == "Picker").ToList();
        Assert.Equal(
            ["{Binding AssociatedSelfSelection, Mode=TwoWay}",
             "{Binding NetPick, Mode=TwoWay}",
             "{Binding MemberPick, Mode=TwoWay}"],
            pickers.Select(e => PropertyValue(e, "SelectedItem")));
    }

    [Fact]
    public void EveryChevronOnBothCards_IsTheFortyFourDpClass()
    {
        var chevrons = new[] { AddressCard, GroupsCard }
            .SelectMany(card => Root(card).Descendants())
            .Where(e => e.Name.LocalName == "Button" && TextOf(e) is "◀" or "▶" or "▲" or "▼")
            .ToList();

        Assert.Equal(4, chevrons.Count);        // anti-vacuity: 2 wheel + 2 spinner
        foreach (var chevron in chevrons)
        {
            Assert.Equal("44", PropertyValue(chevron, "WidthRequest"));
            Assert.Equal("44", PropertyValue(chevron, "HeightRequest"));
        }
    }

    // ==== ROUND 11 §5: the member section rebuild ==========================

    [Fact]
    public void TheNetMembersSubHeading_IsGONE_AndTheSectionStillExists()
    {
        // §5's deletion. The section it headed is still here, so this is not
        // passing because the member markup vanished.
        // ROUND 15 E-3 note: the SubHeading STYLE has no remaining user on
        // this card (the ERASE block that carried the other one is gone), so
        // the anti-vacuity partner is now the section itself plus the card's
        // OWN heading style — a detector that saw no styles at all would fail
        // on that.
        var root = Root(AddressCard);

        Assert.DoesNotContain(root.Descendants(), e =>
            e.Name.LocalName == "Label" && TextOf(e) == "Net members");
        Assert.Contains(root.Descendants(), e =>
            (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal));
        Assert.Contains(root.Descendants(), IsActionButton);
    }

    [Fact]
    public void TheONEActionButton_SitsOnItsOwnRow_RightAligned_AndItsTextIsBound()
    {
        // ROUND 15 E-D2 (critic F50): ONE button, ONE command seat, and a TEXT
        // that switches with the kind — which is precisely why the text is
        // BOUND rather than literal. Its own row = it is not a cell of any
        // row's Grid.
        var root = Root(AddressCard);

        var action = Assert.Single(root.Descendants().Where(IsActionButton));
        Assert.Equal("{Binding ActionText}", PropertyValue(action, "Text"));
        Assert.Equal("End", PropertyValue(action, "HorizontalOptions"));
        Assert.Equal("Grid", RowLabel("Channel group").Parent!.Name.LocalName);
        Assert.NotEqual("Grid", action.Parent!.Name.LocalName);

        // …and the deleted seats are gone from the markup entirely.
        Assert.DoesNotContain(BindingTexts(root),
            t => t.Contains("ProgramCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(BindingTexts(root),
            t => t.Contains("AddMemberCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMemberSection_CarriesTwoLeftLabelledPickers_AndItsProseCaptions()
    {
        // ROUND 15 E-1: both operands are PICKED from the mirror. The labels
        // are LEFT (the constitution), the member picker renders the typed
        // candidate's Display and is DEAD until a net is picked, and the two
        // prose captions — "pick a net first" and the no-removal rule (E-D1) —
        // are the VM's own statics, so the card cannot drift from them.
        var root = Root(AddressCard);

        var netRow = RowLabel("Net").Parent!;
        var netPicker = netRow.Elements().Single(e => e.Name.LocalName == "Picker");
        Assert.Equal("{Binding NetChoices}", PropertyValue(netPicker, "ItemsSource"));
        Assert.Same(RowLabel("Net"), netRow.Elements().First());        // label LEFT

        var memberRow = RowLabel("Member").Parent!;
        var memberPicker = memberRow.Elements().Single(e => e.Name.LocalName == "Picker");
        Assert.Equal("{Binding MemberChoices}", PropertyValue(memberPicker, "ItemsSource"));
        Assert.Equal("{Binding Display}", PropertyValue(memberPicker, "ItemDisplayBinding"));
        Assert.Equal("{Binding CanPickMember}", PropertyValue(memberPicker, "IsEnabled"));
        Assert.Same(RowLabel("Member"), memberRow.Elements().First());  // label LEFT

        foreach (var (needle, gate) in new[]
        {
            ("AleProgrammingViewModel.PickANetFirstCaption", "ShowPickANetFirst"),
            ("AleProgrammingViewModel.NoMemberRemovalCaption", (string?)null),
        })
        {
            var caption = root.Descendants().Single(e =>
                (PropertyValue(e, "Text") ?? "").Contains(needle, StringComparison.Ordinal));
            Assert.Equal("{StaticResource Caption}", PropertyValue(caption, "Style"));
            if (gate is not null) Assert.Equal(gate, VisibilityBinding(caption));
        }
    }

    [Fact]
    public void TheAssociatedSelfPicker_SitsABOVETheChannelGroupWheel()
    {
        // ROUND 15 E-5 (owner): picking the self is what tells you which group
        // to expect — the pick SETS the wheel — so it reads above it.
        var program = AddressProgramTab();

        int assoc = program.Elements().ToList().FindIndex(e => e.Descendants()
            .Any(d => (PropertyValue(d, "SelectedItem") ?? "")
                .Contains("AssociatedSelfSelection", StringComparison.Ordinal)));
        int group = program.Elements().ToList().FindIndex(e => e.Descendants()
            .Any(d => TextOf(d) == "Channel group"));

        Assert.True(assoc >= 0, "the associated-self picker row is not a child of the Program tab");
        Assert.True(group >= 0, "the channel-group row is not a child of the Program tab");
        Assert.True(assoc < group, "the associated-self picker must sit ABOVE the channel group wheel");
    }

    [Fact]
    public void TheMemberTable_RendersTheMirrorProjection_WithItsHashAndMemberHeaders()
    {
        // §5's member DISPLAY: the rows bind the display projection (never the
        // raw mirror), the headers are `#` | `Member`, the cells are the
        // round's Consolas/Bold/16 row idiom, and the READ-EMPTY caption is the
        // VM's own static gated on HasNoMembers. The UNREAD state is the
        // projection's single hyphen row — a VM fact, pinned there.
        var root = Root(AddressCard);

        var table = root.Descendants().Single(e =>
            (PropertyValue(e, "BindableLayout.ItemsSource") ?? "")
                .Contains("{Binding MemberDisplayRows}", StringComparison.Ordinal));

        var template = table.Descendants().Single(e => e.Name.LocalName == "DataTemplate");
        var cells = template.Descendants().Where(e => e.Name.LocalName == "Label").ToList();
        Assert.Equal(
            ["{Binding NumberText}", "{Binding AddressText}"],
            cells.Select(c => PropertyValue(c, "Text")));
        Assert.All(cells, c =>
        {
            Assert.Equal("Consolas", PropertyValue(c, "FontFamily"));
            Assert.Equal("Bold", PropertyValue(c, "FontAttributes"));
            Assert.Equal("16", PropertyValue(c, "FontSize"));
        });

        // The headers sit immediately above the table, in the same stack.
        var headerRow = root.Descendants().Single(e =>
            e.Name.LocalName == "Grid"
            && e.Elements().Any(c => c.Name.LocalName == "Label" && TextOf(c) == "#"));
        Assert.Equal(
            ["#", "Member"],
            headerRow.Elements().Where(e => e.Name.LocalName == "Label").Select(TextOf));
        Assert.All(headerRow.Elements().Where(e => e.Name.LocalName == "Label"),
            l => Assert.Equal("{StaticResource CellHeading}", PropertyValue(l, "Style")));
        Assert.Same(table.Parent, headerRow.Parent);

        var empty = root.Descendants().Single(e =>
            (PropertyValue(e, "Text") ?? "")
                .Contains("AleProgrammingViewModel.NoMembersCaption", StringComparison.Ordinal));
        Assert.Equal("{StaticResource Caption}", PropertyValue(empty, "Style"));
        Assert.Equal("HasNoMembers", VisibilityBinding(empty));
    }

    [Fact]
    public void NoRemoveMember_ExistsAnywhereOnEitherCard()
    {
        // §5's absence pin. There is no remove-member verb on the wire, so a
        // control offering one would be the app inventing a command. Anti-
        // vacuity: the OTHER card's per-row Remove (a real DELC) is still
        // there, so the detector demonstrably finds Remove buttons.
        foreach (var card in new[] { AddressCard, GroupsCard })
            Assert.DoesNotContain(Root(card).Descendants(), e =>
                e.Name.LocalName == "Button"
                && (TextOf(e) ?? "").Contains("member", StringComparison.OrdinalIgnoreCase)
                && (TextOf(e) ?? "").StartsWith("Remove", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(BindingTexts(Root(AddressCard)),
            t => t.Contains("RemoveMember", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(Root(GroupsCard).Descendants(), e =>
            e.Name.LocalName == "Button" && TextOf(e) == "Remove");
    }

    [Fact]
    public void TheONESurvivingWheelDisplay_TakesTheSharedWidthKey()
    {
        // §5's commonization, round 15 E-5's survivor. The COUNT is part of
        // the contract — a second wheel arriving with a literal width would
        // otherwise pass unnoticed. Scoped to the wheel ROW: the same binding
        // names a BOOK CELL on the other tab, and that is a table cell with
        // its own width.
        var displays = Root(AddressCard).Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (PropertyValue(e, "Text") ?? "") == "{Binding GroupText}"
                && e.Parent!.Name.LocalName == "HorizontalStackLayout"
                && e.Parent.Elements().Any(s => TextOf(s) == "◀"))
            .ToList();

        var display = Assert.Single(displays);
        Assert.Equal("{StaticResource AleWheelValueWidth}", PropertyValue(display, "WidthRequest"));
    }

    // ---- The wheel width's VALUE, and the row budgets that gate it ---------
    // AUDIT ROUND 1, MAJOR-3: spelling the key is only half the contract. A
    // guard that never reads the NUMBER lets the key drift to any value at all
    // — 260 survived the first version of this file.
    // AUDIT ROUND 2: and one row's arithmetic is only that row's. The first
    // fix read the CHANNEL-GROUP row's spacings and paired them with the
    // ASSOCIATED-SELF row's label allowance, so widening the associated-self
    // stack to 20 dp put that row 4 dp over budget with both pins green. Each
    // of the THREE rows is now evaluated against ITS OWN read terms.

    /// <summary>The phone content budget §3 states: a 360-dp phone, less the
    /// card padding (10×2) and the page padding, ≈ 336 dp of content. The same
    /// figure StyleVocabularyGuardTests evaluates its rows against.</summary>
    private const double PhoneContent = 336;

    /// <summary>The house 6-dp gap, the value both wheel spacings should hold
    /// (asserted from the markup, never assumed by the budget itself).</summary>
    private const double WideRowGap = 6;

    /// <summary>
    /// Every wheel row on the card: its Caption row LABEL and that label's
    /// stated dp allowance at the Caption tier (12-pt). LAYOUT-PROVISIONAL
    /// (invariant 7): a MEASURED overflow moves the number and the inequality
    /// re-evaluates — the GATE is the inequality, never the chosen allowance.
    /// Each row is checked with its OWN spacings, so a row cannot be widened
    /// behind its neighbour's arithmetic.
    /// <para>ROUND 15 E-5/E-1: ONE row. "Associated self" (the old binding row
    /// at fifteen glyphs) and "Member" became Pickers, which fill their column
    /// and have no fixed-width terms to budget.</para>
    /// </summary>
    public static TheoryData<string, double> WheelRowAllowances => new()
    {
        { "Channel group", 95 },
    };

    [Fact]
    public void TheWheelWidthKey_Is96()
    {
        // The VALUE, as a literal (LAYOUT-PROVISIONAL — §5 states 96, and the
        // two name wheels carried 96 before the commonization). The budgets
        // below are what make 96 legal.
        Assert.Equal(96, AppWidths()["AleWheelValueWidth"]);
    }

    [Theory]
    [MemberData(nameof(WheelRowAllowances))]
    public void EachWheelRow_FitsThePhoneBudget_OnItsOWNReadTerms(string label, double allowance)
    {
        var stack = WheelStackFor(label);

        // Every term but the allowance is READ, and read from THIS row: the
        // grid's column spacing, the stack's own spacing, both of its chevrons,
        // and the width key its value display actually references.
        double gridSpacing = Number(PropertyValue(stack.Parent!, "ColumnSpacing"));
        double stackSpacing = Number(PropertyValue(stack, "Spacing"));

        var chevrons = stack.Elements()
            .Where(e => e.Name.LocalName == "Button" && TextOf(e) is "◀" or "▶")
            .ToList();
        Assert.Equal(2, chevrons.Count);
        double chevronWidth = chevrons.Sum(c => Number(PropertyValue(c, "WidthRequest")));

        var display = stack.Elements().Single(e =>
            e.Name.LocalName == "Label"
            && (PropertyValue(e, "WidthRequest") ?? "").Contains("AleWheelValueWidth", StringComparison.Ordinal));
        _ = display;
        double value = AppWidths()["AleWheelValueWidth"];

        // label | gap | ◀ | gap | value | gap | ▶
        double row = allowance + gridSpacing + chevronWidth + (2 * stackSpacing) + value;

        Assert.True(row <= PhoneContent,
            $"ALE \"{label}\" wheel row (allowance {allowance} + grid gap {gridSpacing} + chevrons "
            + $"{chevronWidth} + 2 × stack gap {stackSpacing} + value {value}) = {row} dp, over the "
            + $"{PhoneContent} dp phone content budget");
    }

    [Fact]
    public void TheWheelBudgetReader_ReallyReadsEachRow_AndTheDictionary()
    {
        // Anti-vacuity for the theory above (the P2-era idiom), now in THREE
        // directions.
        //
        // (1) The App.xaml reader really reads.
        var widths = AppWidths();
        Assert.True(widths.Count > 5, "the App.xaml width reader found almost nothing");
        Assert.Equal(72, widths["SegmentWidth"]);        // a value this file does not set
        Assert.True(widths.ContainsKey("AleWheelValueWidth"));

        // (2) The three rows are three DISTINCT stacks — the defect audit
        // round 2 found was one row's terms standing in for another's, so a
        // locator that resolved them to the same element would make the whole
        // iteration one test wearing three hats. They are also ALL the wheel
        // rows the card has.
        var labels = WheelRowAllowances.Select(r => (string)r[0]!).ToList();
        var stacks = labels.Select(WheelStackFor).ToList();
        Assert.Single(stacks.Distinct());
        Assert.Equal(1, Root(AddressCard).Descendants().Count(e =>
            e.Name.LocalName == "HorizontalStackLayout"
            && e.Elements().Any(c => TextOf(c) == "◀")));

        // (3) Each row's terms are the house values — asserted HERE so the
        // budget itself can keep reading them rather than assuming them.
        foreach (var stack in stacks)
        {
            Assert.Equal(WideRowGap, Number(PropertyValue(stack, "Spacing")));
            Assert.Equal(WideRowGap, Number(PropertyValue(stack.Parent!, "ColumnSpacing")));
            Assert.All(
                stack.Elements().Where(e => e.Name.LocalName == "Button"),
                c => Assert.Equal(44, Number(PropertyValue(c, "WidthRequest"))));
        }

        // …and the budget genuinely BITES on the surviving row: 260 (the
        // round-1 mutation) cannot fit beside its label, and 96 must.
        double binding = (double)WheelRowAllowances
            .Single(r => (string)r[0]! == "Channel group")[1]!;
        double headroom = PhoneContent - (binding + WideRowGap + (2 * 44) + (2 * WideRowGap));
        Assert.True(headroom < 260, "a 260-dp wheel display must NOT fit the phone budget");
        Assert.True(headroom >= 96, "96 must fit, or the theory above asserts a contradiction");
    }

    /// <summary>The wheel stack belonging to ONE row — located from that row's
    /// own Caption label, never "the first wheel on the card".</summary>
    private static XElement WheelStackFor(string rowLabel)
        => RowLabel(rowLabel).Parent!.Elements().Single(e =>
            e.Name.LocalName == "HorizontalStackLayout"
            && e.Elements().Any(c => TextOf(c) == "◀"));

    private static double Number(string? raw)
        => double.Parse(raw ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every <c>x:Double</c> in App.xaml, by key — the constitution's
    /// own numbers, read from the file the app really loads.</summary>
    private static Dictionary<string, double> AppWidths()
    {
        var app = Root(Path.Combine("src", "Falcon.App", "App.xaml"));
        return app.Descendants()
            .Where(e => e.Name.LocalName == "Double")
            .ToDictionary(
                e => e.Attributes().First(a => a.Name.LocalName == "Key").Value,
                e => Number(e.Value));
    }

    // ==== ROUND 11 §3/§5: the Type sites and the PRIMARY tag ===============

    [Fact]
    public void TheProgrammingRowLabel_AndTheBookColumnHeader_BothReadType()
    {
        // §3's two P3 sites, and the absence of the word they replaced.
        // (Placement of the row label is SettingsPlacementGuardTests' pin; what
        // belongs here is the WORD, on both sites, and that neither survives as
        // "Kind".)
        var root = Root(AddressCard);

        Assert.Equal("Type", TextOf(RowLabel("Type")));

        var header = root.Descendants().Single(e =>
            e.Name.LocalName == "Label"
            && TextOf(e) == "Type"
            && (PropertyValue(e, "Style") ?? "").Contains("CellHeading", StringComparison.Ordinal));
        Assert.Contains(AddressBookTab(), header.Ancestors());

        Assert.DoesNotContain(root.Descendants(), e =>
            e.Name.LocalName == "Label" && TextOf(e) == "Kind");

        // Anti-vacuity: the INTERNAL name is untouched — the row still renders
        // KindChoices, which is what makes this a DISPLAY rename.
        Assert.Contains(BindingTexts(root), t => t.Contains("KindChoices", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBookRow_CarriesThePrimaryTagCell_GatedOnTheRowsOwnFlag()
    {
        // R3: the tag is a cell of the BOOK LISTING's row template, gated on
        // the row's own IsPrimarySelf (the VM decides WHICH row; the markup
        // decides that there is a cell at all).
        var template = Root(AddressCard).Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "DataType"
                    && a.Value.Contains("AleBookRow", StringComparison.Ordinal)));

        var tag = template.Descendants().Single(e =>
            (PropertyValue(e, "Text") ?? "").Contains("PrimaryTagText", StringComparison.Ordinal));

        Assert.Equal("{StaticResource Caption}", PropertyValue(tag, "Style"));
        Assert.Equal("IsPrimarySelf", VisibilityBinding(tag));
        Assert.Contains(AddressBookTab(), tag.Ancestors());
    }

    // ==== ROUND 15 D: the member line under a NET row =====================

    /// <summary>The book listing's row template — the one DataTemplate typed
    /// to <c>AleBookRow</c>.</summary>
    private XElement BookRowTemplate()
        => Root(AddressCard).Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "DataType"
                    && a.Value.Contains("AleBookRow", StringComparison.Ordinal)));

    [Fact]
    public void TheBookRow_CarriesOneMemberLine_IndentedUnderName_GatedOnTheRowsOwnFlag()
    {
        // §14.2: ONE label, in the template's SECOND row, aligned under Name
        // and spanning Name·Grp·Assoc, Caption-styled, gated on the row's own
        // HasMembersText, and WRAPPING — a long member list must not widen the
        // row, because the 448-dp budget is the phone's.
        var template = BookRowTemplate();
        var grid = template.Elements().Single(e => e.Name.LocalName == "Grid");

        Assert.Equal("Auto,Auto", PropertyValue(grid, "RowDefinitions"));

        var line = template.Descendants().Single(e =>
            (PropertyValue(e, "Text") ?? "") == "{Binding MembersText}");

        Assert.Equal("Label", line.Name.LocalName);
        Assert.Equal("1", PropertyValue(line, "Grid.Row"));
        Assert.Equal("1", PropertyValue(line, "Grid.Column"));      // under Name
        Assert.Equal("3", PropertyValue(line, "Grid.ColumnSpan"));  // Name·Grp·Assoc
        Assert.Equal("{StaticResource Caption}", PropertyValue(line, "Style"));
        Assert.Equal("HasMembersText", VisibilityBinding(line));
        Assert.Equal("WordWrap", PropertyValue(line, "LineBreakMode"));
        Assert.Contains(AddressBookTab(), line.Ancestors());
    }

    [Fact]
    public void TheBookRowsFirstRow_KeepsItsSixCells_AndDeleteIsStillTheLast()
    {
        // The member line is an ADDITION, not a rearrangement: row 0's six
        // cells keep their bindings AND their column order, and the Delete
        // button is still the template's last child in DOCUMENT order — which
        // is why the new label is written BEFORE it (critic F36).
        var template = BookRowTemplate();
        var grid = template.Elements().Single(e => e.Name.LocalName == "Grid");

        var rowZero = grid.Elements()
            .Where(e => PropertyValue(e, "Grid.Row") is null)        // row 0 is implicit
            .ToList();

        Assert.Equal(
            ["{Binding KindText}", "{Binding NameText}", "{Binding GroupText}",
             "{Binding AssociatedSelfText}", "{Binding PrimaryTagText}", "Delete"],
            rowZero.Select(e => PropertyValue(e, "Text")));
        Assert.Equal(
            [null, "1", "2", "3", "4", "5"],
            rowZero.Select(e => PropertyValue(e, "Grid.Column")));

        var last = grid.Elements().Last();
        Assert.Equal("Button", last.Name.LocalName);
        Assert.Equal("Delete", TextOf(last));

        // ROUND 15 E-2, and the reason D's row-0 pin lives in the same phase
        // (critic F43): EVERY row-0 cell is vertically centred. The Delete
        // button is 44 dp tall and the four value labels had no
        // VerticalOptions at all, so the text sat at the top of the row and
        // read as if it belonged to a different line than its own button.
        // (The PrimaryTag cell was already centred; E adds the other five.)
        Assert.All(rowZero, e => Assert.Equal("Center", PropertyValue(e, "VerticalOptions")));

        // …and the member LINE is NOT centred — it is a second row under the
        // first, not a cell beside one.
        var line = template.Descendants().Single(e =>
            (PropertyValue(e, "Text") ?? "") == "{Binding MembersText}");
        Assert.Null(PropertyValue(line, "VerticalOptions"));
    }

    [Fact]
    public void ThePrimaryTag_IsOnTheBookListingONLY_NotTheOperateSelfsCard()
    {
        // R3's scope, from the other side: the Operate pane's selfs card is
        // UNTOUCHED. Anti-vacuity — that card really does render self rows for
        // a tag to have been added to.
        var operate = Root(Path.Combine("src", "Falcon.App", "Views", "OperateParts", "AlePaneView.xaml"));

        Assert.DoesNotContain(BindingTexts(operate),
            t => t.Contains("PrimaryTag", StringComparison.Ordinal)
                 || t.Contains("IsPrimarySelf", StringComparison.Ordinal));
        Assert.DoesNotContain(operate.Descendants(), e => TextOf(e) == "PRIMARY");
        Assert.Contains(operate.Descendants(), e =>
            (PropertyValue(e, "BindableLayout.ItemsSource") ?? "")
                .Contains("SelfRows", StringComparison.Ordinal));
    }

    // ==== ROUND 11 §3/§5: the channel-groups card ==========================

    [Fact]
    public void TheGroupsCardHeading_ReadsChannelGroups()
    {
        var heading = Root(GroupsCard).Descendants().Single(e =>
            (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal));

        Assert.Equal("Channel groups", TextOf(heading));
    }

    [Fact]
    public void TheAddChannelBox_TakesASpaceSeparatedLIST()
    {
        // §5's multi-add: the placeholder is the VM's own static, and the two
        // single-channel constraints are GONE — a numeric keyboard has no
        // space bar and a 2-character cap cannot hold "5 12 47".
        var entry = Root(GroupsCard).Descendants().Single(e =>
            e.Name.LocalName == "Entry"
            && (PropertyValue(e, "Text") ?? "").Contains("AddChannelInput", StringComparison.Ordinal));

        Assert.Contains("AleScanGroupsViewModel.AddChannelsPlaceholder",
            PropertyValue(entry, "Placeholder") ?? "", StringComparison.Ordinal);
        Assert.Null(PropertyValue(entry, "MaxLength"));
        Assert.Null(PropertyValue(entry, "Keyboard"));

        // Anti-vacuity: the reader DOES see these properties where they exist
        // (the address card's name entry still caps at 15).
        var name = Root(AddressCard).Descendants().Single(e =>
            e.Name.LocalName == "Entry"
            && (PropertyValue(e, "Text") ?? "").Contains("NameInput", StringComparison.Ordinal));
        Assert.Equal("15", PropertyValue(name, "MaxLength"));
    }

    // ==== No read-back displays; row widths are card-local =================

    [Fact]
    public void NeitherCard_CarriesABlueValueDisplay()
    {
        // §4.5: an add-new form has no "current value" to read back, so
        // confirmed state renders ONLY as book/group rows. The wheel displays
        // are plain cell labels precisely so they cannot be mistaken for one.
        foreach (var card in new[] { AddressCard, GroupsCard })
            Assert.DoesNotContain(Root(card).DescendantsAndSelf(), e =>
                (PropertyValue(e, "Style") ?? "").Contains("ValueDisplay", StringComparison.Ordinal));

        // Anti-vacuity: the SAME detector, on a pane that genuinely has them,
        // finds them — so this is not a scanner that reads nothing.
        Assert.Contains(Root(Pane).Descendants(), e =>
            (PropertyValue(e, "Style") ?? "").Contains("ValueDisplay", StringComparison.Ordinal));
    }

    [Fact]
    public void EachCard_DefinesItsRowWidthsInItsOwnResources()
    {
        foreach (var (card, keys) in new[]
        {
            (AddressCard, new[] { "AleWidthKind", "AleWidthName", "AleWidthGroup" }),
            (GroupsCard, ["GroupWidthNumber", "GroupWidthChannel"]),
        })
        {
            var defined = Root(card).Descendants()
                .Where(e => e.Name.LocalName == "Double")
                .Select(e => e.Attributes().First(a => a.Name.LocalName == "Key").Value)
                .ToList();
            foreach (var key in keys) Assert.Contains(key, defined);
        }
    }

    // ==== structure helpers ================================================

    private static List<XElement> PaneChildren()
    {
        var body = Root(Pane).Descendants().First(e => NameOf(e) == "Body");
        return [.. LayoutChildren(body.Elements().First(IsLayout))];
    }

    /// <summary>A tab BODY, identified the only honest way: neither sub-tab
    /// has an x:Name, so a body is the element whose literal
    /// <c>IsVisible</c> is flipped by its OWN DataTrigger on the card's tab
    /// flag. Located structurally, so it survives renames and restyling and
    /// fails exactly when the tab really changes shape.</summary>
    private static XElement TabBody(string card, string flag, string visibleByDefault)
        => Root(card).Descendants().Single(e =>
            PropertyValue(e, "IsVisible") == visibleByDefault
            && OwnTriggers(e).Any(t =>
                t.Name.LocalName == "DataTrigger"
                && BindingName(t.Attribute("Binding")?.Value) == flag));

    /// <summary>An element's OWN triggers — the
    /// <c>&lt;Type.Triggers&gt;</c> property element — never a descendant
    /// control's, which would make every ancestor look like a tab body.</summary>
    private static IEnumerable<XElement> OwnTriggers(XElement e)
        => e.Elements()
            .FirstOrDefault(x => x.Name.LocalName == e.Name.LocalName + ".Triggers")
            ?.Elements() ?? [];

    private static XElement AddressProgramTab() => TabBody(AddressCard, "IsBookTabOpen", "True");

    private static XElement AddressBookTab() => TabBody(AddressCard, "IsBookTabOpen", "False");

    private static XElement GroupsProgramTab() => TabBody(GroupsCard, "IsGroupsTabOpen", "True");

    private static XElement GroupsListTab() => TabBody(GroupsCard, "IsGroupsTabOpen", "False");

    /// <summary>ROUND 15 E-D2: ONE action seat. The button's TEXT switches
    /// with the kind, so it is located by its COMMAND, never its text.</summary>
    private static bool IsActionButton(XElement e)
        => e.Name.LocalName == "Button"
           && PropertyValue(e, "Command") == "{Binding ActionCommand}";

    private static bool IsEraseButton(XElement e)
        => e.Name.LocalName == "Button"
           && PropertyValue(e, "Command") == "{Binding EraseCommand}";

    private static bool IsAddChannelButton(XElement e)
        => e.Name.LocalName == "Button"
           && PropertyValue(e, "Command") == "{Binding AddChannelCommand}";

    private static bool IsGroupListLayout(XElement e)
        => (PropertyValue(e, "BindableLayout.ItemsSource") ?? "")
            .Contains("{Binding GroupRows}", StringComparison.Ordinal);

    private static bool IsRefreshButton(XElement e)
        => e.Name.LocalName == "Button"
           && (TextOf(e) ?? "").StartsWith("Refresh", StringComparison.Ordinal);

    private static bool IsPaneRefreshButton(XElement e)
        => IsRefreshButton(e) && TextOf(e) == "Refresh ALE settings";

    /// <summary>The DataTrigger's bound property on a tab-strip button.</summary>
    private static string? TriggerBinding(XElement button)
        => TriggerOf(button) is { } t ? BindingName(t.Attribute("Binding")?.Value) : null;

    private static string? TriggerValue(XElement button)
        => TriggerOf(button)?.Attribute("Value")?.Value;

    private static XElement? TriggerOf(XElement button)
        => button.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataTrigger");

    private static XElement? AncestorSettingVisibility(XElement start)
    {
        for (var e = start; e is not null; e = e.Parent)
            if (VisibilityBinding(e) is not null) return e;
        return null;
    }

    private static string? AncestorVisibilityBinding(XElement start)
        => AncestorSettingVisibility(start) is { } owner ? VisibilityBinding(owner) : null;

    /// <summary>The property name inside an <c>IsVisible="{Binding X}"</c>, in
    /// EITHER form a XAML property can be set. Null when IsVisible is unset or
    /// is a literal like "False" — which is what makes the default-tab pins
    /// (literal) and the kind-switch pins (binding) different assertions.</summary>
    private static string? VisibilityBinding(XElement element)
        => BindingName(PropertyValue(element, "IsVisible"));

    private static string? BindingName(string? raw)
    {
        if (raw is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(), @"^\{\s*Binding\s+(?:Path\s*=\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}$");
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>A property set as an attribute, or as a property ELEMENT
    /// (<c>&lt;Grid.IsVisible&gt;…&lt;/Grid.IsVisible&gt;</c>). Both count —
    /// a reader that sees only one spelling can be walked straight past.</summary>
    private static string? PropertyValue(XElement element, string property)
        => element.Attribute(property)?.Value
           ?? element.Elements()
               .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)
               ?.Value;

    private static string? TextOf(XElement element) => PropertyValue(element, "Text");

    private static string? NameOf(XElement e)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    private static bool Descends(XElement root, Func<XElement, bool> match)
        => match(root) || root.Descendants().Any(match);

    /// <summary>Element children only — a property element ("Type.Property") is
    /// not a child CONTROL, or "the last element" would be whatever property
    /// happened to come last.</summary>
    private static IEnumerable<XElement> LayoutChildren(XElement layout)
        => layout.Elements().Where(IsLayout);

    private static bool IsLayout(XElement e)
        => !e.Name.LocalName.Contains('.', StringComparison.Ordinal);

    private static IEnumerable<string> BindingTexts(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            foreach (var a in e.Attributes()) yield return a.Value;
            if (e.Name.LocalName.Contains('.', StringComparison.Ordinal) && !e.HasElements)
                yield return e.Value;
        }
    }

    /// <summary>Parsed ONCE per file and shared, so every helper hands back
    /// the SAME element instances: the ancestry pins compare by reference, and
    /// two separate parses of one document would make "inside this tab"
    /// unsatisfiable for the wrong reason.</summary>
    private static readonly Dictionary<string, XElement> Parsed = [];

    private static XElement Root(string relativePath)
    {
        lock (Parsed)
        {
            if (Parsed.TryGetValue(relativePath, out var cached)) return cached;
            var path = Path.Combine(FindRepoRoot(), relativePath);
            Assert.True(File.Exists(path), "markup missing: " + relativePath);
            var root = XDocument.Load(path).Root!;
            Parsed[relativePath] = root;
            return root;
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
