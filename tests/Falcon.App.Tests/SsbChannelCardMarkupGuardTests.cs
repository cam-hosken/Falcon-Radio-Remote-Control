using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 15 N2 — the SSB pane's CHANNEL CARD, pinned against the markup.
///
/// <para><b>Why a source pin.</b> OWNER 2026-08-23 (superseding the round-15
/// Q2 two-row ruling): row 1 is the two big digits ALONE; the new-channel
/// entry moved BENEATH them, left-justified on the Receive-only row. What
/// makes the merged row fit inside the 448 dp of card content the bench phone
/// leaves (the AH1 budget) is the SAME ruling's other two clauses: the
/// "New channel" caption is DROPPED (an owner-sanctioned exception to D8's
/// labels-left rule — the static 00-99 placeholder is the label) and the
/// entry is narrowed to ValueWidthNarrow (it only holds 2 digits). Entry 64 +
/// Select 72 + Receive-only 241 + gaps ≈ 395 ≤ 448; with the old caption and
/// NumericEntryWidth back it is ≈ 516 and the pair walks off the phone.
/// These are decisions a later "tidy-up" would undo without noticing, so they
/// have to update this file to happen.</para>
///
/// <para><b>Structural, not textual</b> — the pane is parsed as XML and read as
/// an element TREE, and every attribute is read in BOTH forms (attribute and
/// property element), for the reason <c>RefreshButtonWidthGuardTests</c>
/// documents. Same accepted limitation as every scan here: a value supplied
/// from a style or from code-behind is invisible.</para>
/// </summary>
public class SsbChannelCardMarkupGuardTests
{
    /// <summary>The bench phone's card-content budget (AH1, docs/ui.md).</summary>
    private const int PhoneCardBudget = 448;

    /// <summary>30-pt Consolas bold, ESTIMATED at 18 dp per digit (§1 N2).</summary>
    private const int SpinnerDigitWidth = 18;

    /// <summary>The DROPPED caption "New channel", ESTIMATED (§3.4) — kept
    /// only for the arithmetic pin proving why it had to go.</summary>
    private const int NewChannelCaptionWidth = 95;

    /// <summary>The Receive-only group: caption ≈ 85 + two Segments + gaps.</summary>
    private const int RxOnlyGroupWidth = 85 + 72 + 72 + 12;

    private static readonly string SsbPane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "SsbPaneView.xaml");

    private static readonly string AppXaml = Path.Combine("src", "Falcon.App", "App.xaml");

    // ---- Row 1: the digits alone; row 2: entry · Select · Receive-only -----

    [Fact]
    public void RowOne_IsTheDigitsStack_Alone_AboveTheEntryRow()
    {
        // Owner 2026-08-23: the current-channel digits stand alone; the entry
        // moved down to the Receive-only row. The digits stack is a DIRECT
        // child of the card's stack (not of the entry row's grid), and it
        // comes BEFORE the grid in reading order.
        var stack = DigitsStack();
        Assert.Equal("VerticalStackLayout", stack.Parent!.Name.LocalName);
        // Same-document comparison (two Load() calls give distinct trees):
        // the entry-row grid among the SAME parent's children.
        var siblings = stack.Parent.Elements().ToList();
        int grid = siblings.FindIndex(e => e.Name.LocalName == "Grid"
            && (string?)e.Attribute("ColumnDefinitions") == "Auto,*,Auto");
        Assert.True(grid >= 0 && siblings.IndexOf(stack) < grid,
            "the digits no longer sit above the entry row");
        // …and no Entry shares row 1 with them.
        Assert.DoesNotContain(stack.Elements(), e => e.Name.LocalName == "Entry");
    }

    [Fact]
    public void TheDigitsStack_HoldsTheTwoCurrentChannelDigits_Abutting()
    {
        var stack = DigitsStack();

        // Spacing 0: Consolas is monospace, so the two digits ARE the number.
        Assert.Equal("0", Attribute(stack, "Spacing"));

        var digits = stack.Elements().Where(e => e.Name.LocalName == "Label").ToList();
        Assert.Equal(
            ["{Binding Ssb.Channel.TensText}", "{Binding Ssb.Channel.UnitsText}"],
            digits.Select(d => Attribute(d, "Text")));
        Assert.All(digits, d => Assert.Equal("{StaticResource SpinnerDigit}", Attribute(d, "Style")));
    }

    [Fact]
    public void TheEntry_HasNoCaption_TheOwnerRuling_OverD8()
    {
        // Owner 2026-08-23: the "New channel" caption is DROPPED (the merged
        // row cannot fit it — the arithmetic pin below) and the static 00-99
        // placeholder is the label. An owner-sanctioned exception to D8.
        Assert.DoesNotContain(ChannelCard().Descendants(),
            e => Attribute(e, "Text") == "New channel");
    }

    [Fact]
    public void TheEntry_IsTheAppsNumericEntry_TwoDigits_SelectAllOnFocus_AndNoInlineFont()
    {
        var entry = TheEntry();

        // ValueWidthNarrow, not NumericEntryWidth: the entry only holds two
        // digits (owner 2026-08-23, the same ruling that merged the rows).
        Assert.Equal("{StaticResource ValueWidthNarrow}", Attribute(entry, "WidthRequest"));
        Assert.Equal("Numeric", Attribute(entry, "Keyboard"));
        Assert.Equal("2", Attribute(entry, "MaxLength"));
        Assert.Equal("{Binding Ssb.Channel.ChannelInput, Mode=TwoWay}", Attribute(entry, "Text"));
        // The placeholder is the STATIC range hint (the RF-gain idiom), never
        // a value the radio wrote.
        Assert.Equal("00-99", Attribute(entry, "Placeholder"));

        // The behaviour, inside its property element.
        Assert.Contains(entry.Descendants(), e => e.Name.LocalName == "SelectAllOnFocusBehavior");

        // Round 10 §3: the font is the STYLE's business. An inline copy here is
        // the drift that round retired.
        foreach (var font in new[] { "FontFamily", "FontSize", "FontAttributes" })
            Assert.Null(Attribute(entry, font));
    }

    [Fact]
    public void TheSelectButton_IsSegmentClass_AtTheSharedWidth_AndBoundToTheEnteredSelect()
    {
        var button = EntryRow().Descendants().Single(e =>
            e.Name.LocalName == "Button" && Attribute(e, "Text") == "Select");

        Assert.Equal("Select", Attribute(button, "Text"));
        Assert.Equal("{StaticResource Segment}", Attribute(button, "Style"));
        Assert.Equal("{StaticResource SegmentWidth}", Attribute(button, "WidthRequest"));
        Assert.Equal("{Binding Ssb.Channel.SelectEnteredCommand}", Attribute(button, "Command"));
    }

    // ---- The entry row and the warn line -------------------------------------

    [Fact]
    public void TheEntryRow_IsEntrySelectLeft_ReceiveOnlyRight()
    {
        var row = EntryRow();

        // Left group, column 0 (left-justified — the owner's words): the
        // entry then Select, in reading order.
        var left = row.Elements().First(e => e.Name.LocalName == "HorizontalStackLayout"
            && e.Attribute("Grid.Column") is null);
        Assert.Equal(["Entry", "Button"],
            left.Elements().Select(e => e.Name.LocalName));

        // Right group, column 2: the Receive-only pair, still End-aligned (C1)
        // — on the SAME row as the entry now (owner 2026-08-23, ex-Q2).
        var pair = Assert.Single(row.Elements(), e =>
            e.Name.LocalName == "HorizontalStackLayout"
            && (string?)e.Attribute("Grid.Column") == "2");
        Assert.Contains(pair.Elements(), c => c.Name.LocalName == "Label" && Attribute(c, "Text") == "Receive only");
        Assert.Equal("End", Attribute(pair, "HorizontalOptions"));

        // Both segments, still bound to the 00-gated command.
        var buttons = pair.Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(["Yes", "No"], buttons.Select(b => Attribute(b, "Text")));
        Assert.All(buttons, b =>
            Assert.Equal("{Binding Ssb.Channel.SetRxOnlyCommand}", Attribute(b, "Command")));
    }

    [Fact]
    public void TheWarnLine_ShowsTheProseRefusal_AndIsVisibleOnlyWhileThereIsOne()
    {
        var card = ChannelCard();

        var warn = Assert.Single(card.Descendants(), e =>
            Attribute(e, "Text") == "{Binding Ssb.Channel.InputError}");
        Assert.Equal("{Binding Ssb.Channel.HasInputError}", Attribute(warn, "IsVisible"));
        // The app's standing input-error style (the RF-gain row's).
        Assert.Equal("{StaticResource ErrorCaption}", Attribute(warn, "Style"));
    }

    [Fact]
    public void TheSpinnerCommands_AreGoneFromTheCard_N2()
    {
        // The deleted VM members, asserted ABSENT at the one site that bound
        // them: a re-added chevron would be a re-added command.
        var bindings = ChannelCard().Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        foreach (var gone in new[] { "TensUpCommand", "TensDownCommand", "UnitsUpCommand", "UnitsDownCommand" })
            Assert.DoesNotContain(bindings, v => v.Contains(gone, StringComparison.Ordinal));

        // ANTI-VACUITY: the bindings really were read, and the digits SURVIVE.
        Assert.Contains(bindings, v => v.Contains("Ssb.Channel.TensText", StringComparison.Ordinal));
    }

    // ---- The arithmetic ------------------------------------------------------

    [Fact]
    public void TheEntryRow_FitsTheBenchPhonesCardBudget_BecauseTheCaptionWent()
    {
        // Not a measurement — the ARITHMETIC the 2026-08-23 merge rests on,
        // reading the REAL resource values, so widening `ValueWidthNarrow` or
        // `SegmentWidth` re-runs it.
        int entry = Resource("ValueWidthNarrow");
        int select = Resource("SegmentWidth");
        int spacing = int.Parse(Attribute(EntryRow(), "ColumnSpacing")!, System.Globalization.CultureInfo.InvariantCulture);

        int row = entry + spacing + select + spacing + RxOnlyGroupWidth;

        Assert.True(row <= PhoneCardBudget,
            $"the entry row is {row} dp of content against the bench phone's {PhoneCardBudget} dp");

        // …and the shape the owner traded away is what this pin keeps out:
        // put the "New channel" caption and the old NumericEntryWidth back and
        // the row must NOT fit — that infeasibility is WHY the caption is gone
        // (an owner-sanctioned D8 exception, not a drift to tidy up).
        int oldShape = NewChannelCaptionWidth + spacing + Resource("NumericEntryWidth") + spacing + select + spacing + RxOnlyGroupWidth;
        Assert.True(oldShape > PhoneCardBudget,
            "the arithmetic no longer explains why the caption was dropped");
    }

    // ---- Parsing -------------------------------------------------------------

    /// <summary>The entry row: the Auto,*,Auto grid inside the Channel card.</summary>
    private static XElement EntryRow()
    {
        var card = ChannelCard();
        return Assert.Single(card.Descendants(), e =>
            e.Name.LocalName == "Grid" && (string?)e.Attribute("ColumnDefinitions") == "Auto,*,Auto");
    }

    /// <summary>The current-channel digits stack (zero-spacing, SpinnerDigit).</summary>
    private static XElement DigitsStack()
        => Assert.Single(ChannelCard().Descendants(), e =>
            e.Name.LocalName == "HorizontalStackLayout"
            && e.Elements().Any(c => Attribute(c, "Text") == "{Binding Ssb.Channel.TensText}"));

    /// <summary>The one Entry in the card.</summary>
    private static XElement TheEntry()
        => Assert.Single(ChannelCard().Descendants(), e => e.Name.LocalName == "Entry");

    /// <summary>The Channel card's Border — the first card on the pane, found
    /// by its static heading. Fails loudly if it is renamed or removed.</summary>
    private static XElement ChannelCard()
    {
        var pane = Load(SsbPane);
        var card = pane.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Border"
            && e.Descendants().Any(c => c.Name.LocalName == "Label"
                && Attribute(c, "Text") == "Channel"
                && Attribute(c, "Style") == "{StaticResource CardHeading}"));

        Assert.True(card is not null, "the Channel card (heading \"Channel\") is gone from " + SsbPane);
        Assert.True(card!.Descendants().Count() > 10,
            "the Channel card parsed to " + card.Descendants().Count() + " elements — it is not the card");
        return card;
    }

    /// <summary>A named double resource from App.xaml, as an integer.</summary>
    private static int Resource(string key)
    {
        var app = Load(AppXaml);
        var value = Assert.Single(app.Descendants(), e =>
            e.Name.LocalName == "Double"
            && e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value == key);
        return int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Both spellings: an attribute, or the property-element form.</summary>
    private static string? Attribute(XElement e, string name)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value
           ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + name)?.Value;

    private static XDocument Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path);
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
