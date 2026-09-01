using System.Xml.Linq;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// UI tweaks round 10 (§4) — the ALE OPERATE pane's LITERAL contracts, pinned
/// against the markup in the house source-scan style
/// (HopPaneMarkupGuardTests / RefreshButtonWidthGuardTests).
///
/// <para><b>Why the markup needs its own pins.</b> Every fact §4 decides is
/// invisible to a ViewModel test. The VM cannot see that the selfs card sits
/// ABOVE the stations, that a station's action buttons live in FIXED 72-dp
/// columns rather than Auto ones, or that the phone renders two lines where
/// the desktop renders one. Those are the decisions §4 spent its arithmetic
/// on, and a well-meaning "tidy the grid" edit undoes them without failing a
/// single existing test.</para>
///
/// <para><b>Why the COLUMN STRINGS are asserted literally.</b> §4 decided them
/// literally, for a stated reason: the action columns must be
/// <c>SegmentWidth</c> (72) spelled out, because a StaticResource reference
/// cannot live inside a <c>GridLengthCollection</c> string.
/// StyleVocabularyGuardTests owns the other end of that — it cross-pins
/// <c>SegmentWidth == 72</c> and the three row budgets — so between the two
/// suites the literal cannot drift away from the key it stands for, and the
/// geometry cannot drift away from the budget that justified it.</para>
///
/// <para><b>The net-row contract, pinned in BOTH directions.</b> A net has no
/// LQA (RAN is individuals-only), and §4 says the CELL goes empty while the
/// COLUMN stays. That is a two-sided fact: the button must be the thing that
/// disappears (its IsVisible is bound to the row's own CanLqa — see
/// AleViewModelTests for the VM half), and the column must be a fixed width
/// rather than Auto, so AMD/CALL cannot slide left on a net row. Pinning only
/// one side would let the other regress silently.</para>
///
/// <para>XML, not regex, for the reason RefreshButtonWidthGuardTests
/// documents: a XAML property can be an attribute or a property element, and
/// only a parser sees both — plus an XML comment is not an element, so
/// commented-out markup is invisible for free. Same ACCEPTED LIMITATION as
/// every scan here: a value supplied indirectly (style, trigger, code-behind)
/// is not seen.</para>
/// </summary>
public class AlePaneMarkupGuardTests : SessionTestBase
{
    private static readonly string AlePane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "AlePaneView.xaml");

    // §4's literal column contracts, spelled exactly as the markup must.
    private const string SelfsColumns = "*,64";

    /// <summary>The station tables' ONE geometry — shared by the Nets card and
    /// the Stations card (ROUND 15 §17, owner 2026-08-22). It was
    /// <c>"44,*,64,72,72,72"</c> (round 13 §4 A3): Type and Chan grp are DELETED
    /// and the 96-dp <b>Assoc self</b> column takes their place, which leaves
    /// the name 448 − 344 = 104 dp instead of 84. The 96 is
    /// <c>ValueWidthWide</c> and the 72s are <c>SegmentWidth</c>, both spelled
    /// literally because a StaticResource reference cannot live inside a
    /// GridLengthCollection string; StyleVocabularyGuardTests cross-pins BOTH
    /// keys so neither literal can drift from the key it stands for.</summary>
    private const string StationColumns = "*,96,72,72,72";
    private const string GridSpacing = "8";

    // The §4 ALE-ROW IDIOM: the cell font §3's ledger exempts from the
    // promoted styles (it is a table typeface, not a text tier).
    private const string IdiomFamily = "Consolas";
    private const string IdiomWeight = "Bold";
    private const string IdiomSize = "16";

    // ---- Round 11 §4's literal contracts --------------------------------------

    // Owner 2026-08-24: ONE line — the RX/TX freqs stack inside one cell.
    // 56+96+64+88+88 + 4×8 = 424 ≤ the 448 phone content budget.
    private const string ReportColumns = "56,96,64,88,88";     // CHAN | RX/TX | SCORE | MEAS | RCVD

    /// <summary>BROADCAST ROUND F2 (plan-ale-broadcast-round.md, OWNER RULING
    /// 6, 2026-08-24): the LQA schedule row is ONE line now. It was two —
    /// <c>"56,*"</c> for kind + address above a right-aligned
    /// <c>"56,56,Auto"</c> for interval / start / Delete — which put a
    /// schedule's WHEN on a different line from its WHO. Type · Address (star,
    /// wrapping in its own cell) · Interval · Next · Delete, on the same
    /// spacing every table here uses.</summary>
    private const string ScheduleColumns = "56,*,56,56,Auto";

    private const string BuilderLine1Columns = "Auto,*";       // label | picker
    private const string BuilderLine2Columns = "80,80,*,Auto,Auto";

    /// <summary>The AMD editor's re-measured height (§4, LAYOUT-PROVISIONAL —
    /// the measurement is recorded beside the literal in the markup).</summary>
    private const string AmdEditorHeight = "72";

    /// <summary>Phone content budget: the MEASURED 448 dp (clone round 12 §9
    /// A5, 2026-08-19, owner-confirmed — the AH1 no-clip figure; ui.md "the
    /// phone budget itself is now MEASURED"). This file carried round 10's
    /// assumed 336 until 2026-08-23, when widening the Schedule buttons
    /// (owner: the text clipped on the phone) pushed the builder rows past
    /// the old assumption — the first consumer of the slack the measurement
    /// found.</summary>
    private const double PhoneContent = 448;

    /// <summary>The Segment width the Now buttons carry (cross-pinned to the
    /// <c>SegmentWidth</c> key by StyleVocabularyGuardTests). ROUND 15 §16:
    /// the two buttons are Now and Schedule; OWNER 2026-08-23: Schedule is
    /// <c>SegmentWidthWide</c> now — "Schedule" clipped at 72 on the phone.</summary>
    private const double SegmentWidth = 72;

    /// <summary>The wide Segment width the Schedule buttons carry since
    /// 2026-08-23 (cross-pinned by StyleVocabularyGuardTests).</summary>
    private const double SegmentWidthWide = 90;

    /// <summary>§4's stated allowance for the natural-width per-row action
    /// button (round 15 F-1: it reads "Delete", not "Stop").</summary>
    private const double DeleteAllowance = 64;

    /// <summary>Consolas' advance at the ALE row idiom's FontSize 16 (0.55 em
    /// monospaced = 8.8 dp). Stated, not measured — the same provenance and the
    /// same accepted limitation <c>StyleVocabularyGuardTests</c> records for its
    /// own copy; it is used here only to turn a residual column width back into
    /// a character count.</summary>
    private const double ConsolasAdvanceAt16 = 8.8;

    /// <summary>The radio's longest legal ALE address (manual §2.8.4.5.2: the
    /// 9-character call limit is the CALL argument; the address book itself
    /// takes up to 15). The schedule row's address column is measured against
    /// it because the name is the row's identity — truncating it is how the
    /// operator deletes the WRONG schedule.</summary>
    private const double LongestAddressChars = 15;

    // ---- Card order (§4: link banner, Selfs, Stations, Messages) --------------

    [Fact]
    public void TheMainTabCards_AreInTheOrderSection4Decided()
    {
        // The order IS the design: the selfs are what the stations are called
        // FROM, so they read before the list of targets. Before round 10 the
        // self line was a strip UNDER the stations.
        // Round 11 §3: the fourth card's heading is "AMD" now — the CARD
        // HEADING is the whole manifest for that rename (there is no chip).
        // ROUND 15 §17: the ONE Stations card became TWO, NETS FIRST — a net is
        // what an operator calls when they do not want one station, and the
        // ORDER is the design exactly as it was when the selfs moved above the
        // stations.
        // Heard stations (owner design 2026-08-24) closes the pane: the
        // who's-reachable table reads after the scores it contextualises.
        Assert.Equal(
            ["link banner", "Self addresses", "Nets", "Stations", "AMD",
             // Owner 2026-08-25: Scores and Scheduling are separate frames.
             "Scores", "Scheduling", "Heard stations"],
            CardSequence());
    }

    [Fact]
    public void TheCardClassifier_NamesEveryCardItFinds_AndFindsThemAll()
    {
        // Anti-vacuity: the order assertion above is only meaningful if the
        // classifier actually resolves each card. An "unclassified" entry (or
        // a short list) would mean the pin is reading something else.
        var sequence = CardSequence();

        Assert.Equal(8, sequence.Count);
        Assert.DoesNotContain("unclassified", sequence);
        Assert.Equal(sequence.Count, sequence.Distinct().Count());
    }

    /// <summary>Every <c>Card</c>-styled Border in document order, named by the
    /// content that identifies it.</summary>
    private static IReadOnlyList<string> CardSequence()
        =>
        [
            .. Root().Descendants()
                .Where(e => e.Name.LocalName == "Border"
                    && (PropertyValue(e, "Style") ?? "").Contains("StaticResource Card", StringComparison.Ordinal))
                .Select(Classify)
        ];

    private static string Classify(XElement card)
    {
        if (card.Descendants().Any(e => Description(e) == "ALE link state banner"))
            return "link banner";

        var heading = card.Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal))
            .Select(TextOf)
            .FirstOrDefault(t => t is not null);

        return heading ?? "unclassified";
    }

    // ---- The SELFS table (§4, decided literally) ------------------------------

    [Fact]
    public void SelfsTable_HeaderAndRowGrids_CarryTheLiteralColumnContract()
    {
        var header = SelfsHeaderGrid();
        Assert.Equal(SelfsColumns, PropertyValue(header, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(header, "ColumnSpacing"));

        var row = SelfsRowGrid();
        Assert.Equal(SelfsColumns, PropertyValue(row, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(row, "ColumnSpacing"));
    }

    [Fact]
    public void SelfsTable_Headings_AreSelfAndChanGrp_InTheCellHeadingTier()
    {
        // "Chan grp" is owner ruling 1's vocabulary, verbatim: not "grp", not
        // "Group", not "Chan group".
        var labels = Labels(SelfsHeaderGrid());

        Assert.Equal(2, labels.Count);
        AssertCell(labels[0], "Self", column: 0);
        AssertCell(labels[1], "Chan grp", column: 1);

        Assert.All(labels, l =>
            Assert.Contains("CellHeading", PropertyValue(l, "Style") ?? ""));
    }

    [Fact]
    public void SelfsTable_Cells_AreAddressAndGroupText_InTheAleRowIdiom()
    {
        var cells = Labels(SelfsRowGrid());

        Assert.Equal(2, cells.Count);
        AssertCell(cells[0], "{Binding Address}", column: 0);
        AssertCell(cells[1], "{Binding GroupText}", column: 1);

        Assert.All(cells, AssertAleRowIdiom);
    }

    [Fact]
    public void SelfsTable_EmptyView_IsTheExactSection4Line()
    {
        // The empty view is a CONTRACT, not a nicety: "no selfs" and "not
        // asked yet" look identical on this pane, and this line is the only
        // thing that says the table is honestly empty rather than broken.
        var empty = SelfsList()
            .Elements().Single(e => e.Name.LocalName.EndsWith(".EmptyView", StringComparison.Ordinal))
            .Elements().Single();

        Assert.Equal("No self addresses reported yet.", TextOf(empty));
        Assert.Contains("Caption", PropertyValue(empty, "Style") ?? "");
    }

    [Fact]
    public void SelfsCard_HeaderRow_IsTheHeadingPlusTheFillChip()
    {
        // §4: "header row = heading 'Self addresses' + Fill chip". The chip
        // moved here from the deleted fill strip; §4 also says the chip
        // ITSELF is unchanged, so its own styling is not asserted — only that
        // it is on this row and still bound to the fill state.
        var card = SelfsCard();

        var heading = card.Descendants().First(e => e.Name.LocalName == "Label"
            && (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal));
        Assert.Equal("Self addresses", TextOf(heading));

        var chip = card.Descendants().Single(e => Description(e) == "ALE fill state (read-only)");
        Assert.Same(heading.Parent, chip.Parent);
        Assert.Contains(chip.Descendants(), e =>
            (TextOf(e) ?? "").Contains("Ale.FillStateText", StringComparison.Ordinal));
    }

    // ---- The NETS and STATIONS tables: one row, one geometry, two cards -------
    // CONTRACT CHANGE, owner ruling 2026-08-22 (plan/plan-round15.md §17): the
    // ONE Stations card became TWO — Nets above Stations — and the six-column
    // row lost Type and Chan grp for a 96-dp Assoc self. Every guard below runs
    // over BOTH cards from one theory, so neither card can be the one nobody
    // pinned; what each keeps from round 13 is its POSITIONAL half, which is
    // the whole reason the table reads as a table.

    public static TheoryData<string, string> TheTwoCards => new()
    {
        // card heading · the rows binding it renders
        { "Nets", "Ale.NetRows" },
        { "Stations", "Ale.StationRows" },
    };

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheHeader_IsOne_MirrorsTheFieldColumns_ActionColumnsUnheaded(
        string heading, string source)
    {
        var header = HeaderGrid(heading);
        Assert.Equal(GridSpacing, PropertyValue(header, "ColumnSpacing"));
        Assert.Equal(StationColumns, PropertyValue(header, "ColumnDefinitions"));

        // §17: the card names the KIND, so the heading over the name column is
        // the card's own word — and the new column is headed on BOTH cards.
        var labels = Labels(header);
        Assert.Equal(2, labels.Count);
        AssertCell(labels[0], heading, column: 0);
        AssertCell(labels[1], "Assoc self", column: 1);

        // The action columns exist in the header's geometry (so the headings
        // sit over the FIELD columns) but carry no heading of their own — the
        // button texts name themselves.
        Assert.All(labels, l =>
            Assert.Contains("CellHeading", PropertyValue(l, "Style") ?? ""));

        // …and it is UNCONDITIONAL: no idiom gate anywhere on it.
        Assert.Null(PropertyValue(header, "IsVisible"));
        Assert.Equal("{Binding " + source + "}", ItemsSource(List(heading)));
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheRow_IsExactlyOneLine_WithTheLiteralColumns(string heading, string source)
    {
        var row = Assert.Single(RowGrids(heading));

        Assert.Equal(StationColumns, PropertyValue(row, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(row, "ColumnSpacing"));
        Assert.Null(PropertyValue(row, "IsVisible"));
        _ = source;
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheCard_CarriesNoIdiomSplitAtAll(string heading, string source)
    {
        // The round-13 DELETION, pinned from the opposite direction — and it
        // must be pinned, because every positive assertion in this file would
        // still pass if a phone-only line crept back in beside the one row.
        var card = Card(heading);

        Assert.DoesNotContain(AllPropertyValues(card),
            v => v.Contains("OnIdiom", StringComparison.Ordinal));

        // Anti-vacuity: the same reader over the same card DOES see the values
        // that are there, so "no OnIdiom" is not an empty scan.
        Assert.Contains(AllPropertyValues(card),
            v => v.Contains(source, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheStationName_WrapsInItsCell_RatherThanClippingOrResplittingTheRow(
        string heading, string source)
    {
        // Round 13's other half, and it OUTLIVES the column change: with one
        // geometry on every device, a station wider than the residual star
        // column has to go somewhere. It wraps INSIDE the cell — CharacterWrap
        // because an ALE address is one contiguous token (word wrap has nothing
        // to break on and would clip), MaxLines 2 so a long name cannot grow the
        // row without bound, and centred so a wrapped row still lines up with
        // its single-line neighbours.
        var cells = Labels(RowGrids(heading)[0]);
        var name = cells[0];
        Assert.Equal("{Binding Address}", TextOf(name));

        Assert.Equal("CharacterWrap", PropertyValue(name, "LineBreakMode"));
        Assert.Equal("2", PropertyValue(name, "MaxLines"));
        Assert.Equal("Center", PropertyValue(name, "VerticalOptions"));

        // …and ONLY the name wraps: a fixed-width cell that wrapped would
        // re-introduce the two-line row round 13 deleted.
        Assert.Null(PropertyValue(cells[1], "LineBreakMode"));
        _ = source;
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheCells_AreNameAndAssocSelf_InTheAleRowIdiom(string heading, string source)
    {
        // §17: TWO field cells now. The COLUMN is half of each cell's contract,
        // and the half a document-order read cannot see: move Assoc self into
        // the star column and the fixed 96-dp column silently empties while the
        // text/binding/font checks all still pass (audit round 1, MAJOR).
        var cells = Labels(RowGrids(heading)[0]);
        Assert.Equal(2, cells.Count);

        AssertCell(cells[0], "{Binding Address}", column: 0);
        AssertCell(cells[1], "{Binding AssociatedSelfText}", column: 1);

        Assert.All(cells, AssertAleRowIdiom);

        // Both cells are vertically CENTRED, which is load-bearing with the
        // one-line ruling: the name can wrap to two lines inside its cell, so
        // its neighbour sits in a row taller than its own text.
        Assert.All(cells, c => Assert.Equal("Center", PropertyValue(c, "VerticalOptions")));
        _ = source;
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheActions_AreLqaAmdCall_InFixedColumns(string heading, string source)
    {
        // §17's order, and the columns each button is nailed to: the actions
        // start at column 2 now, after Name / Assoc self.
        AssertActionBlock(RowGrids(heading)[0], firstColumn: 2);
        _ = source;
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void NetRows_LoseTheLqaBUTTON_AndKeepItsCOLUMN(string heading, string source)
    {
        // The two-sided §4 contract, and it OUTLIVES the split — which is why
        // it is re-pinned on BOTH templates rather than only on the nets one.
        // Side one: the LQA button is what disappears, driven by the row's own
        // CanLqa (the VM half — net rows report CanLqa false — is pinned in
        // AleViewModelTests). Side two: the column it vacates is a LITERAL
        // width, so AMD and CALL cannot slide into it. An "Auto" anywhere in
        // the action segment is the regression this catches.
        var grid = RowGrids(heading)[0];
        const int firstActionColumn = 2;

        var lqa = Buttons(grid).Single(b => TextOf(b) == "LQA ▸");
        Assert.Equal(firstActionColumn, ColumnOf(lqa));
        Assert.Equal("{Binding CanLqa}", PropertyValue(lqa, "IsVisible"));
        Assert.Equal("{Binding CanLqa}", PropertyValue(lqa, "IsEnabled"));

        Assert.Equal(firstActionColumn + 1, ColumnOf(Buttons(grid).Single(b => TextOf(b) == "AMD ▸")));
        Assert.Equal(firstActionColumn + 2, ColumnOf(Buttons(grid).Single(b => TextOf(b) == "CALL")));

        var columns = (PropertyValue(grid, "ColumnDefinitions") ?? "").Split(',');
        Assert.All(columns[^3..], c => Assert.Equal("72", c));
        _ = source;
    }

    [Theory]
    [MemberData(nameof(TheTwoCards))]
    public void TheEmptyView_NamesTheCardsOwnKind(string heading, string source)
    {
        // The empty view is a CONTRACT, not a nicety: "no nets" and "not asked
        // yet" look identical on this pane, and with TWO cards a shared line
        // would leave the operator unable to tell WHICH list is empty.
        var empty = List(heading)
            .Elements().Single(e => e.Name.LocalName.EndsWith(".EmptyView", StringComparison.Ordinal))
            .Elements().Single();

        Assert.Equal(heading == "Nets" ? "No nets reported yet." : "No stations reported yet.",
            TextOf(empty));
        Assert.Contains("Caption", PropertyValue(empty, "Style") ?? "");
        _ = source;
    }

    // ==========================================================================
    // BROADCAST ROUND F3 — the PINNED ANY/ALL rows on the Nets card
    // (plan-ale-broadcast-round.md §2, owner rulings 1–4; probes P20/P20b)
    // ==========================================================================

    public static TheoryData<string, string, string> ThePinnedRows => new()
    {
        // literal · choices binding · selection binding
        { "ANY", "Ale.AnyChannelChoices", "Ale.SelectedAnyChannel" },
        { "ALL", "Ale.AllChannelChoices", "Ale.SelectedAllChannel" },
    };

    [Theory]
    [MemberData(nameof(ThePinnedRows))]
    public void ThePinnedRows_AreFIXEDMarkupOutsideTheTemplate_AfterTheListAndItsEmptyView(
        string address, string choices, string selection)
    {
        // THE placement decision (plan §2), and the one a VM test cannot see:
        // the rows must render REGARDLESS of the book being empty. Inside the
        // BindableLayout they would be template rows and would vanish with it;
        // before it they would read as the first two records. So: siblings of
        // the list, AFTER it, on the card's own stack.
        // (Every Card()/Root() call re-parses, so the whole walk is done from
        // the ROW's own parse — element identity does not survive a re-read.)
        var row = PinnedRow(address);
        Assert.False(IsInsideTemplate(row));

        var stack = row.Parent!;
        Assert.Equal("VerticalStackLayout", stack.Name.LocalName);
        Assert.Equal("Nets", Classify(stack.Parent!));      // …the Nets CARD's own stack

        var list = stack.Elements().Single(e => ItemsSource(e) is not null);
        Assert.Equal("{Binding Ale.NetRows}", ItemsSource(list));
        Assert.Single(list.Elements()
            .Where(e => e.Name.LocalName.EndsWith(".EmptyView", StringComparison.Ordinal)));

        var children = stack.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();
        Assert.True(children.IndexOf(row) > children.IndexOf(list),
            "a pinned broadcast row sits BEFORE the net list — it would read as a book record");
        _ = (choices, selection);
    }

    [Theory]
    [MemberData(nameof(ThePinnedRows))]
    public void ThePinnedRows_RideTheCardsColumns_WithTheFurnitureMarks(
        string address, string choices, string selection)
    {
        // Same geometry as a net row, so the pinned rows read as part of the
        // table — and invariant 2's marks, so they cannot be MISread as
        // records: the literal in the name cell, and a "—" associated self
        // (owner ruling 2 — the wire takes no self argument).
        var row = PinnedRow(address);
        Assert.Equal(StationColumns, PropertyValue(row, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(row, "ColumnSpacing"));

        var cells = Labels(row);
        Assert.Equal(2, cells.Count);
        AssertCell(cells[0], address, column: 0);
        AssertCell(cells[1], "—", column: 1);
        Assert.All(cells, AssertAleRowIdiom);
        _ = (choices, selection);
    }

    [Theory]
    [MemberData(nameof(ThePinnedRows))]
    public void ThePinnedRows_CarryTheirChannelPickerInTheEmptyLqaColumn(
        string address, string choices, string selection)
    {
        // Owner ruling 3: the picker takes the slot a net row leaves empty
        // (column 2 — RAN is individuals-only), FILLING it so the action block
        // stays a rectangle, at the 44-dp minimum touch height every picker on
        // this pane carries.
        var picker = Assert.Single(PinnedRow(address).Elements()
            .Where(e => e.Name.LocalName == "Picker"));

        Assert.Equal(2, ColumnOf(picker));
        Assert.Equal(0, RowOf(picker));
        Assert.Equal("Fill", PropertyValue(picker, "HorizontalOptions"));
        Assert.Equal("44", PropertyValue(picker, "MinimumHeightRequest"));
        Assert.Equal("{Binding " + choices + "}", PropertyValue(picker, "ItemsSource"));
        Assert.Equal("{Binding " + selection + ", Mode=TwoWay}", PropertyValue(picker, "SelectedItem"));
    }

    [Theory]
    [MemberData(nameof(ThePinnedRows))]
    public void ThePinnedRows_AmdAndCall_BindTheBroadcastCommands_AndNameTheOnAirTerm(
        string address, string choices, string selection)
    {
        // The buttons sit where a station row's AMD ▸ / CALL sit (columns 3
        // and 4), so the two blocks line up; CALL carries the ON-AIR term in
        // its description because it TRANSMITS to every scanning station.
        var buttons = Buttons(PinnedRow(address));
        Assert.Equal(["AMD ▸", "CALL"], buttons.Select(TextOf));

        Assert.Equal(3, ColumnOf(buttons[0]));
        Assert.Equal(4, ColumnOf(buttons[1]));
        Assert.All(buttons, b => Assert.Equal("Fill", PropertyValue(b, "HorizontalOptions")));
        Assert.All(buttons, b => Assert.Contains("Segment", PropertyValue(b, "Style") ?? ""));

        string kind = address == "ANY" ? "Any" : "All";
        Assert.Equal("{Binding Ale.Amd" + kind + "Command}", PropertyValue(buttons[0], "Command"));
        Assert.Equal("{Binding Ale.Call" + kind + "Command}", PropertyValue(buttons[1], "Command"));

        Assert.Contains(address, Description(buttons[1]) ?? "", StringComparison.Ordinal);
        Assert.Contains("transmits", Description(buttons[1]) ?? "", StringComparison.Ordinal);
        _ = (choices, selection);
    }

    [Fact]
    public void ThePinnedRows_CarryTheirOwnCaption_WhichIsTheAnyGatesReason()
    {
        // Disabled-with-reason, in the form a fixed pair of rows can take: ONE
        // caption under both, naming what a broadcast reaches AND the single
        // gate the operator has to clear. Without it CALL on the ANY row is
        // simply grey with no explanation on screen.
        var caption = Assert.Single(Card("Nets").Descendants(), e =>
            e.Name.LocalName == "Label"
            && (TextOf(e) ?? "").StartsWith("Broadcast —", StringComparison.Ordinal));

        Assert.Equal("Broadcast — every scanning station. ANY needs a channel.", TextOf(caption));
        Assert.Equal("{StaticResource Caption}", PropertyValue(caption, "Style"));
    }

    [Fact]
    public void ThePinnedRows_AreTheOnlyTwo_AndTheStationsCardHasNone()
    {
        // The COUNT, and the placement's other half (invariant 2): the
        // broadcast literals never appear in Stations. A third pinned row, or
        // one copied onto the wrong card, fails here.
        Assert.Equal(2, Card("Nets").Descendants()
            .Count(e => e.Name.LocalName == "Picker"));
        Assert.Empty(Card("Stations").Descendants()
            .Where(e => e.Name.LocalName == "Picker"));
        Assert.DoesNotContain(Card("Stations").Descendants()
            .Where(e => e.Name.LocalName == "Label"),
            l => TextOf(l) is "ANY" or "ALL");
    }

    // ==========================================================================
    // BROADCAST ROUND F4 — the compose channel row
    // ==========================================================================

    [Fact]
    public void TheComposeChannelRow_SitsBeneathTo_OnTheSameWidths_GatedOnTheBroadcastTargets()
    {
        // Plan §2: the row is BENEATH To, matches its widths so the two picker
        // lines read as one block, and is VISIBLE only for a broadcast target —
        // a book send takes no channel argument, so a control that did nothing
        // would be a lie rather than a convenience.
        var card = AmdCard();
        var to = card.Descendants().Single(e =>
            (PropertyValue(e, "ItemsSource") ?? "").Contains("Ale.Messages.Targets", StringComparison.Ordinal));
        var channel = card.Descendants().Single(e =>
            (PropertyValue(e, "ItemsSource") ?? "")
                .Contains("Ale.Messages.ComposeChannelChoices", StringComparison.Ordinal));

        Assert.Equal("{Binding Ale.Messages.SelectedComposeChannel, Mode=TwoWay}",
            PropertyValue(channel, "SelectedItem"));
        Assert.Equal(PropertyValue(to, "WidthRequest"), PropertyValue(channel, "WidthRequest"));
        Assert.Equal(PropertyValue(to, "MinimumHeightRequest"), PropertyValue(channel, "MinimumHeightRequest"));

        var row = channel.Parent!;
        Assert.Equal("{Binding Ale.Messages.IsChannelPickerVisible}", PropertyValue(row, "IsVisible"));
        Assert.Equal("Channel:", TextOf(Labels(row)[0]));

        // BENEATH To — assertable as document order among the compose stack's
        // own children, which is what "beneath" means in a vertical stack.
        var stack = row.Parent!;
        Assert.Same(stack, to.Parent!.Parent);
        var children = stack.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();
        Assert.Equal(children.IndexOf(to.Parent!) + 1, children.IndexOf(row));
    }

    [Fact]
    public void EverySectionFourGrid_OccupiesItsColumnsExactlyOnce()
    {
        // The DEFECT-FAMILY pin (audit round 1, MAJOR): every positional
        // contract on this pane, asserted as one fact about every grid at once,
        // so a future cell added or moved cannot be positionally unpinned merely
        // by being new. Two cells sharing a column is the failure this catches —
        // it looks fine in document order and leaves a fixed column empty.
        foreach (var (what, grid, expected) in new (string, XElement, int[])[]
        {
            ("selfs header",        SelfsHeaderGrid(),                    [0, 1]),
            ("selfs row",           SelfsRowGrid(),                       [0, 1]),
            // ROUND 15 §17: two cards, one geometry — five columns, four cells
            // (the LQA button and its empty-on-a-net column are the fifth).
            ("nets header",         HeaderGrid("Nets"),                   [0, 1]),
            ("nets row",            RowGrids("Nets")[0],                  [0, 1, 2, 3, 4]),
            ("stations header",     HeaderGrid("Stations"),               [0, 1]),
            ("stations row",        RowGrids("Stations")[0],              [0, 1, 2, 3, 4]),
            // BROADCAST ROUND F3: the two PINNED rows sit on the Nets card's
            // own five columns and join the family by the same rule — they are
            // fixed markup, so nothing else would notice a cell sliding.
            ("pinned ANY row",      PinnedRow("ANY"),                     [0, 1, 2, 3, 4]),
            ("pinned ALL row",      PinnedRow("ALL"),                     [0, 1, 2, 3, 4]),
            // Round 11 §4 joins the same family — a new cell cannot be
            // positionally unpinned merely by being new. The builders' line 2
            // deliberately SKIPS column 2: that is the star spacer that pushes
            // Now/Schedule to the right edge, and a cell landing in it is the
            // regression this catches.
            // Owner 2026-08-24: ONE report line — five header cells; the
            // row's flat cells sit at 0/2/3/4 and the RX/TX pair lives in a
            // column-1 STACK (not a positional label).
            ("report header",       ReportHeaderGrid(ReportColumns),       [0, 1, 2, 3, 4]),
            // …the row's column-1 CELL is the RX/TX stack itself.
            ("report row",          ReportRowGrids()[0],                   [0, 1, 2, 3, 4]),
            // BROADCAST ROUND F2: one header, one row — Type · Address ·
            // Interval · Next (· Delete on the row, unheaded).
            ("schedule header",     ScheduleHeaderGrid(),                  [0, 1, 2, 3]),
            ("schedule row",        ScheduleRowGrids()[0],                 [0, 1, 2, 3, 4]),
            ("EXCH builder line 1", BuilderLine1("Station:"),              [0, 1]),
            ("SOU builder line 1",  BuilderLine1("Self:"),                 [0, 1]),
            ("EXCH builder line 2", BuilderLine2("Exch"),                  [0, 1, 3, 4]),
            ("SOU builder line 2",  BuilderLine2("Sou"),                   [0, 1, 3, 4]),
        })
        {
            var cells = Cells(grid);
            int[] occupied = [.. cells.Select(ColumnOf)];

            Assert.True(expected.Length == cells.Count,
                $"§4 {what}: expected {expected.Length} cells, found {cells.Count}");
            Assert.True(expected.SequenceEqual(occupied),
                $"§4 {what}: cells occupy columns [{string.Join(",", occupied)}], "
                + $"expected [{string.Join(",", expected)}]");
            Assert.True(occupied.Distinct().Count() == occupied.Length,
                $"§4 {what}: two cells share a column [{string.Join(",", occupied)}] — "
                + "one of the fixed columns is now empty and its neighbours have collided");

            // Every §4 grid is ONE line: a stray Grid.Row would push a cell
            // into an implicit second row and break the table just as badly.
            Assert.All(cells, c => Assert.True(RowOf(c) == 0,
                $"§4 {what}: a cell sets Grid.Row {RowOf(c)} — these grids are single-line"));
        }
    }

    [Fact]
    public void ThePane_HasNoRefreshButtonAtAll_AndBindsNoStationRefreshCommand()
    {
        // §17 G-D1 INVERTS the round-10 pin that read "the Stations card keeps
        // its heading-row Refresh". Every app-side write closes with the bulk
        // book re-read into the ONE mirror both cards render from, so the
        // button answered a question nothing was still asking — and a binding
        // left pointing at the deleted command resolves to nothing SILENTLY in
        // MAUI. Asserted over the WHOLE pane: the Operate side has no Refresh
        // now except the LQA card's own (round 15 F-5), which is a different
        // control on a different card and is named for its own scope.
        var buttons = Root().Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var values = Root().Descendants()
            .SelectMany(e => e.Attributes().Select(a => a.Value))
            .ToList();

        Assert.DoesNotContain(buttons, b => TextOf(b) == "Refresh");
        Assert.DoesNotContain(values, v => v.Contains("RefreshStationsCommand", StringComparison.Ordinal));

        // TWO named survivors: F-5's schedule re-read on the LQA card, and
        // the Inbox's received-store re-read (linked-amd round, owner ask
        // 2026-08-24 — Stage 9 closed; RXMSG listing shape PROVISIONAL).
        // Each is named for its own scope; the STATION list still has none.
        Assert.Equal(2, buttons.Count(b => (TextOf(b) ?? "").StartsWith("Refresh", StringComparison.Ordinal)));
        Assert.Contains(values, v => v.Contains("Ale.Lqa.RefreshCommand", StringComparison.Ordinal));
        Assert.Contains(values, v => v.Contains("Ale.Messages.RefreshInboxCommand", StringComparison.Ordinal));

        // Anti-vacuity: the reader sees this pane's other buttons and bindings.
        Assert.Contains(buttons, b => TextOf(b) == "CALL");
        Assert.Contains(values, v => v.Contains("Ale.NetRows", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDeletedTypeAndChanGrpColumns_AreGoneFromTheWholePane()
    {
        // §17's deletion, from the absence side (invariant 5) — and it doubles
        // as the round-11 "Type" MANIFEST count: the manifest was three sites
        // (this pane's station header, the ALE programming row label, the
        // address-book column header) and this deletion takes it to TWO. The
        // other two are pinned in AleProgrammingMarkupGuardTests.
        var labels = Root().Descendants()
            .Where(e => e.Name.LocalName == "Label")
            .ToList();
        var values = Root().Descendants()
            .SelectMany(e => e.Attributes().Select(a => a.Value))
            .ToList();

        // CONTRACT NARROWED, BROADCAST ROUND F2 (owner ruling 6, 2026-08-24):
        // this pin read "no Label anywhere on the pane says Type". What §17
        // actually deleted was the STATION cards' kind column — the cell that
        // repeated what its card already said — and owner ruling 6 gives the
        // LQA schedule table a "Type" heading over EXCHANGE/SOUND, which no
        // card heading names. So the absence is asserted where §17 made it, and
        // the ONE surviving site is named rather than merely tolerated: a
        // "Type" that reappeared on a station card still fails here.
        Assert.DoesNotContain(Card("Nets").Descendants()
            .Concat(Card("Stations").Descendants())
            .Where(e => e.Name.LocalName == "Label"), l => TextOf(l) == "Type");
        Assert.Equal(ScheduleColumns, PropertyValue(
            Assert.Single(labels, l => TextOf(l) == "Type").Parent!, "ColumnDefinitions"));

        // KindText survives on the pane EXACTLY once - the LQA schedule row's
        // EXCH/SOU cell, a different row model entirely - and nowhere in either
        // station template.
        // (Each Root() call re-parses, so the pin compares the parent's own
        // column contract rather than element identity across parses.)
        Assert.Equal(ScheduleColumns, PropertyValue(
            Assert.Single(labels, l => TextOf(l) == "{Binding KindText}").Parent!,
            "ColumnDefinitions"));

        // Chan grp survives on the SELFS card and NOWHERE else on this pane:
        // it is the selfs' own fact, and §17 removed it only from the station
        // rows. GroupText is bound exactly once, in the selfs template.
        Assert.Single(labels, l => TextOf(l) == "Chan grp");
        Assert.Equal(SelfsColumns, PropertyValue(
            Assert.Single(labels, l => TextOf(l) == "{Binding GroupText}").Parent!,
            "ColumnDefinitions"));

        // Anti-vacuity: the same readers see the replacement column.
        Assert.Equal(2, labels.Count(l => TextOf(l) == "Assoc self"));
        Assert.Equal(2, labels.Count(l => TextOf(l) == "{Binding AssociatedSelfText}"));
    }

    // ==========================================================================
    // UI tweaks ROUND 11 §4 — the AMD card and the LQA sub-tab
    // ==========================================================================

    // ---- The AMD card (§3 rename + the re-measured editor) ---------------------

    [Fact]
    public void TheAmdCard_IsHeadedAmd_AndItsEditorCarriesTheReMeasuredHeight()
    {
        // The rename's whole manifest is this Label (⊕ there is no "Messages"
        // chip; the sub-tab strip is Stations-family). The editor beside it is
        // the §4 LAYOUT-PROVISIONAL value: 72, re-measured against the phone's
        // 336 dp card content (Segoe UI 14: one line 20.37 dp + 18.62 per
        // further line; 90 characters of operator text wrap to three lines =
        // 57.61, plus the TextBox's own vertical chrome). The old 96 was
        // unmeasured. A MEASURED change moves the literal and this pin together.
        var card = AmdCard();
        Assert.Equal("AMD", TextOf(card.Descendants().First(e => e.Name.LocalName == "Label"
            && (PropertyValue(e, "Style") ?? "").Contains("CardHeading", StringComparison.Ordinal))));

        var editor = card.Descendants().Single(e => e.Name.LocalName == "Editor");
        Assert.Equal(AmdEditorHeight, PropertyValue(editor, "HeightRequest"));
        Assert.Equal("Disabled", PropertyValue(editor, "AutoSize"));   // never grows on content
        Assert.Equal("90", PropertyValue(editor, "MaxLength"));        // the cap the height serves
    }

    // ---- The LQA REPORT table: two lines, both idioms ---------------------------

    [Fact]
    public void ReportHeader_IsOneLine_WithTheLiteralColumnContract()
    {
        // Owner 2026-08-24: the two-line report folded to ONE — the RX/TX
        // pair stacks in one cell under one header.
        var header = ReportHeaderGrid(ReportColumns);
        Assert.Equal(GridSpacing, PropertyValue(header, "ColumnSpacing"));
        var h = Labels(header);
        Assert.Equal(5, h.Count);
        AssertCell(h[0], "CHAN", column: 0);
        AssertCell(h[1], "RX/TX", column: 1);
        AssertCell(h[2], "SCORE", column: 2);
        AssertCell(h[3], "MEAS SNR", column: 3);
        AssertCell(h[4], "RCVD SNR", column: 4);

        // §4: Caption headers over the ALE-row cells (round-10 idiom).
        Assert.All(h, l =>
            Assert.Equal("{StaticResource Caption}", PropertyValue(l, "Style")));

        // BOTH idioms render this line: no OnIdiom switch on the report table.
        Assert.Null(PropertyValue(header, "IsVisible"));
    }

    [Fact]
    public void ReportRow_IsOneLine_WithTheStackedRxTxCell_AndTheAleRowIdiom()
    {
        var grids = ReportRowGrids();
        var row = Assert.Single(grids);
        Assert.Equal(ReportColumns, PropertyValue(row, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(row, "ColumnSpacing"));
        Assert.Null(PropertyValue(row, "IsVisible"));

        // Four FLAT label cells…
        var cells = Labels(row);
        Assert.Equal(4, cells.Count);
        AssertCell(cells[0], "{Binding Channel}", column: 0);
        AssertCell(cells.Single(c => TextOf(c) == "{Binding Score}"), "{Binding Score}", column: 2);
        AssertCell(cells.Single(c => TextOf(c) == "{Binding MeasuredSnr}"), "{Binding MeasuredSnr}", column: 3);
        AssertCell(cells.Single(c => TextOf(c) == "{Binding ReceivedSnr}"), "{Binding ReceivedSnr}", column: 4);

        // …and the column-1 CELL is the stack: RX over TX, in order.
        var stack = row.Elements().Single(e => e.Name.LocalName == "VerticalStackLayout");
        Assert.Equal("1", stack.Attribute("Grid.Column")?.Value);
        var pair = Labels(stack);
        Assert.Equal(2, pair.Count);
        Assert.Equal("{Binding RxText}", TextOf(pair[0]));
        Assert.Equal("{Binding TxText}", TextOf(pair[1]));

        Assert.All(cells.Concat(pair), AssertAleRowIdiom);
    }

    [Fact]
    public void ReportTable_BindsTheDisplayProjection_AndHasNoEmptyView()
    {
        // §4's three-state rule made flesh: ONE template, NO EmptyView, bound
        // to the projection that already contains the placeholder row. An
        // EmptyView here would be a SECOND, unspecified rendering of "empty" —
        // and it is precisely what used to say "No rows."
        var list = ReportList();

        Assert.Equal("{Binding Ale.Lqa.ReportDisplayRows}", ItemsSource(list));
        Assert.Empty(list.Elements()
            .Where(e => e.Name.LocalName.EndsWith(".EmptyView", StringComparison.Ordinal)));
        Assert.Single(list.Descendants().Where(e => e.Name.LocalName == "DataTemplate"));
    }

    // ---- The LQA SCHEDULE mirror ------------------------------------------------

    [Fact]
    public void ScheduleHeader_MirrorsTheFourFieldColumns_DeleteUnheaded()
    {
        // BROADCAST ROUND F2, OWNER RULING 6: the headings are Type / Address /
        // Interval / Next, and the Delete column gets NO heading — the button
        // names itself, exactly as the station cards' action columns do. The
        // header carries the row's FULL geometry so the four headings sit over
        // the four field columns rather than drifting left of them.
        var header = ScheduleHeaderGrid();
        Assert.Equal(ScheduleColumns, PropertyValue(header, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(header, "ColumnSpacing"));

        var labels = Labels(header);
        Assert.Equal(4, labels.Count);
        AssertCell(labels[0], "Type", column: 0);
        AssertCell(labels[1], "Address", column: 1);
        AssertCell(labels[2], "Interval", column: 2);
        AssertCell(labels[3], "Next", column: 3);
        Assert.All(labels, l => Assert.Equal("{StaticResource Caption}", PropertyValue(l, "Style")));
    }

    [Fact]
    public void ScheduleRow_IsExactlyOneLine_TypeAddressIntervalNextDelete()
    {
        // The ruling's own shape, and the DELETION of the two-line wrapper
        // pinned with it: exactly ONE grid in the template, so a stray second
        // line cannot come back beside the one row (invariant 5).
        var grid = Assert.Single(ScheduleRowGrids());

        Assert.Equal(ScheduleColumns, PropertyValue(grid, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(grid, "ColumnSpacing"));
        Assert.Null(PropertyValue(grid, "IsVisible"));
        Assert.Null(PropertyValue(grid, "HorizontalOptions"));   // the right-aligned line is gone

        var cells = Labels(grid);
        Assert.Equal(4, cells.Count);
        AssertCell(cells[0], "{Binding KindText}", column: 0);
        AssertCell(cells[1], "{Binding Address}", column: 1);
        AssertCell(cells[2], "{Binding IntervalText}", column: 2);
        AssertCell(cells[3], "{Binding StartText}", column: 3);

        Assert.All(cells, AssertAleRowIdiom);

        // The address owns the star column, so — like a station name — it WRAPS
        // inside its own cell rather than clipping or re-splitting the row.
        Assert.Equal("CharacterWrap", PropertyValue(cells[1], "LineBreakMode"));
        Assert.Equal("2", PropertyValue(cells[1], "MaxLines"));
        Assert.All(cells, c => Assert.Equal("Center", PropertyValue(c, "VerticalOptions")));

        // …and ONLY the address wraps: a fixed-width cell that wrapped would
        // re-introduce the two-line row the ruling deleted.
        Assert.All(new[] { cells[0], cells[2], cells[3] },
            c => Assert.Null(PropertyValue(c, "LineBreakMode")));
    }

    [Fact]
    public void ScheduleRow_DeleteIsThePerRowActionInTheLastColumn_AndThePlaceholderHasNone()
    {
        // ROUND 15 §16 F-1: the button read "Stop", which said the wrong thing —
        // the pane's own STOP (ST) halts a running LQA; THIS removes a queued
        // schedule. It binds the ROW's own command (the VM half — it acts on the
        // row's captured kind and address — is pinned in LqaViewModelTests), and
        // both its enablement and its VISIBILITY follow the row's CanDelete, so
        // the hyphen placeholder shows no button to press.
        // BROADCAST ROUND F2: same contract, LAST column of the one-line row.
        var row = Assert.Single(ScheduleRowGrids());
        var delete = Assert.Single(Buttons(row));

        Assert.Equal("Delete", TextOf(delete));
        Assert.Equal(4, ColumnOf(delete));
        Assert.Equal("{Binding DeleteCommand}", PropertyValue(delete, "Command"));
        Assert.Equal("{Binding CanDelete}", PropertyValue(delete, "IsEnabled"));
        Assert.Equal("{Binding CanDelete}", PropertyValue(delete, "IsVisible"));
        Assert.Contains("Segment", PropertyValue(delete, "Style") ?? "");
        Assert.Equal("Remove this scheduled LQA from the radio", Description(delete));
    }

    [Fact]
    public void ScheduleTable_BindsTheDisplayProjection_AndHasNoEmptyView()
    {
        var list = ScheduleList();

        Assert.Equal("{Binding Ale.Lqa.ScheduleDisplayRows}", ItemsSource(list));
        Assert.Empty(list.Elements()
            .Where(e => e.Name.LocalName.EndsWith(".EmptyView", StringComparison.Ordinal)));
        Assert.Single(list.Descendants().Where(e => e.Name.LocalName == "DataTemplate"));
    }

    // ---- The RESPONSIVE builder rows --------------------------------------------

    [Theory]
    [InlineData("Station:", "Exch")]
    [InlineData("Self:", "Sou")]
    public void BuilderRow_IsTwoLines_LabelPlusPicker_ThenTheEntriesAndButtons(
        string sideLabel, string vmPrefix)
    {
        // §4: the builders cannot fit one line in the fixed window, so each is
        // two. Line 1 is "Auto,*" — the label takes what it needs and the
        // picker FILLS, which is what let the picker lose its literal width.
        var line1 = BuilderLine1(sideLabel);
        Assert.Equal(BuilderLine1Columns, PropertyValue(line1, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(line1, "ColumnSpacing"));
        AssertCell(Labels(line1)[0], sideLabel, column: 0);

        var picker = Assert.Single(line1.Elements().Where(e => e.Name.LocalName == "Picker"));
        Assert.Equal(1, ColumnOf(picker));
        Assert.Null(PropertyValue(picker, "WidthRequest"));    // it fills the star column
        Assert.Contains($"Ale.Lqa.{vmPrefix}Choices", ItemsSourceOf(picker) ?? "");

        var line2 = BuilderLine2(vmPrefix);
        Assert.Equal(BuilderLine2Columns, PropertyValue(line2, "ColumnDefinitions"));
        Assert.Equal(GridSpacing, PropertyValue(line2, "ColumnSpacing"));

        // The caption header ("Interval" | "Start") sits IMMEDIATELY above
        // line 2, on the entries' own two 80-dp columns.
        var header = line2.ElementsBeforeSelf().Last(e => e.Name.LocalName == "Grid");
        Assert.Equal("80,80,*", PropertyValue(header, "ColumnDefinitions"));
        var hLabels = Labels(header);
        Assert.Equal(2, hLabels.Count);
        AssertCell(hLabels[0], "Interval", column: 0);
        AssertCell(hLabels[1], "Start", column: 1);
        Assert.All(hLabels, l => Assert.Equal("{StaticResource Caption}", PropertyValue(l, "Style")));

        var entries = Entries(line2);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, ColumnOf(entries[0]));
        Assert.Equal(1, ColumnOf(entries[1]));

        // §4: the two SIDE LABELS stay deleted (the room for the buttons);
        // owner 2026-08-24: the WORDS moved to a caption header row over the
        // entries, and the placeholders keep only the format hint.
        Assert.Equal("hh:mm", PropertyValue(entries[0], "Placeholder"));
        Assert.Equal("hh:mm", PropertyValue(entries[1], "Placeholder"));
        Assert.Empty(Labels(line2));

        // ROUND 15 §16 F-2/F-3/F-4: the five columns and the two widths are
        // UNCHANGED; WHICH buttons sit in them is the change. STO is gone (the
        // schedule table's rows carry Delete now), its column is Now's, and STA
        // reads "Schedule". ORDER is pinned: Now first — the immediate,
        // transmitting action must not sit where the operator's thumb expects
        // the scheduling one (critic F58).
        var buttons = Buttons(line2);
        Assert.Equal(["Now", "Schedule"], buttons.Select(TextOf));
        Assert.Equal(3, ColumnOf(buttons[0]));                 // column 2 is the spacer
        Assert.Equal(4, ColumnOf(buttons[1]));
        Assert.All(buttons, b => Assert.Contains("Segment", PropertyValue(b, "Style") ?? ""));
        // Owner 2026-08-23: "Schedule" clipped at SegmentWidth on the phone,
        // so it carries the Wide key; Now keeps the standard one.
        Assert.Equal("{StaticResource SegmentWidth}", PropertyValue(buttons[0], "WidthRequest"));
        Assert.Equal("{StaticResource SegmentWidthWide}", PropertyValue(buttons[1], "WidthRequest"));

        // Each button's own command, and the description that tells the
        // operator which of them transmits AT ONCE.
        string now = vmPrefix == "Exch" ? "NowExchangeCommand" : "NowSoundingCommand";
        string schedule = vmPrefix == "Exch" ? "StartExchangeCommand" : "StartSoundingCommand";
        Assert.Equal("{Binding Ale.Lqa." + now + "}", PropertyValue(buttons[0], "Command"));
        Assert.Equal("{Binding Ale.Lqa." + schedule + "}", PropertyValue(buttons[1], "Command"));
        Assert.Contains("(transmits)", Description(buttons[0]) ?? "");
        Assert.Contains("on schedule", Description(buttons[1]) ?? "");
    }

    [Fact]
    public void TheComposeRows_CarryNoStopButtonAndNoStopCommand_AnywhereOnThePane()
    {
        // F-3's absence half (invariant 5): the STO buttons are DELETED, and a
        // binding left pointing at a deleted command resolves to nothing
        // SILENTLY in MAUI. Asserted pane-wide over every bound value, so a
        // reintroduction anywhere fails rather than only in its old spot.
        var values = Root().Descendants()
            .SelectMany(e => e.Attributes().Select(a => a.Value))
            .ToList();

        Assert.DoesNotContain(Root().Descendants()
            .Where(e => e.Name.LocalName == "Button"), b => TextOf(b) == "STO");
        Assert.DoesNotContain(values, v => v.Contains("StopExchangeCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v.Contains("StopSoundingCommand", StringComparison.Ordinal));

        // Anti-vacuity: the same readers see the replacements — and the pane's
        // OWN Stop (the ALE scan Stop, a different control entirely) survives.
        Assert.Contains(values, v => v.Contains("NowExchangeCommand", StringComparison.Ordinal));
        Assert.Contains(values, v => v.Contains("NowSoundingCommand", StringComparison.Ordinal));
        Assert.Contains(values, v => v.Contains("Ale.StopCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanStopsDescription_SaysItAbortsAnLqa()
    {
        // §16 F-2: ST is the abort for the minutes-long LQA a Now starts (P14b),
        // and the button that sends it is the pane's STOP. The latch does not
        // track the run — this sentence is what tells the operator what does.
        var stop = Root().Descendants().Single(e => e.Name.LocalName == "Button"
            && (PropertyValue(e, "Command") ?? "") == "{Binding Ale.StopCommand}");

        Assert.Equal("Stop scan; disconnects during a call or link; aborts an LQA",
            Description(stop));
    }

    // ---- F-5: the LQA card's own Refresh ----------------------------------------

    [Fact]
    public void TheLqaCard_EndsWithTheRefreshButton_ActionClass_BoundToTheScheduleReRead()
    {
        // The constitution's Y2 rule: Refresh at the BOTTOM of the card it
        // refreshes, right-aligned. LAST CHILD is the assertable half of
        // "bottom" — a Refresh that drifted above the schedule table would
        // still be inside the card and would still look aligned.
        var card = SchedulingCard();
        var stack = card.Elements().Single(e => e.Name.LocalName == "VerticalStackLayout");
        var last = stack.Elements()
            .Last(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

        Assert.Equal("Button", last.Name.LocalName);
        Assert.Equal("Refresh LQA", TextOf(last));
        Assert.Equal("{Binding Ale.Lqa.RefreshCommand}", PropertyValue(last, "Command"));
        Assert.Equal("End", PropertyValue(last, "HorizontalOptions"));
        Assert.Contains("Segment", PropertyValue(last, "Style") ?? "");
        Assert.Equal("Re-read the queued LQA schedules", Description(last));

        // ACTION class (§3): the text sizes it. A width pin here is the round-4
        // clipping defect coming back — RefreshButtonWidthGuardTests owns the
        // app-wide sweep; this is the per-site half.
        Assert.Null(PropertyValue(last, "WidthRequest"));
        Assert.DoesNotContain(last.Elements(), e => e.Name.LocalName == "Button.WidthRequest");
    }

    // ---- The VM half of the button labels (critic F58) ---------------------------

    [Fact]
    public void TheComposeButtons_AreBoundToCommandsThatPutTheRightStaOnTheWire()
    {
        // The markup can only prove WHICH command a label binds; that the
        // command means STA — and which FORM of it — is a ViewModel fact. Both
        // halves in one test, joined through the binding read OUT of the
        // markup: "Schedule" must send the INTERVAL form and "Now" the BARE
        // one, so a label that pointed at the wrong command fails here even
        // though every geometry pin above still passes.
        var vm = LiveLqaViewModel();
        vm.SelectedExchTarget = vm.ExchChoices[0];               // AAA
        vm.SelectedSouSelf = vm.SouChoices[1];                   // TST
        vm.ExchIntervalText = "01:00";
        vm.SouIntervalText = "01:00";

        foreach (var (prefix, label, expected) in new[]
        {
            ("Exch", "Schedule", "EXCH STA AAA 01:00"),
            ("Exch", "Now", "EXCH STA AAA"),
            ("Sou", "Schedule", "SOU STA TST 01:00"),
            ("Sou", "Now", "SOU STA TST"),
        })
        {
            Transport.ClearSent();
            var button = Buttons(BuilderLine2(prefix)).Single(b => TextOf(b) == label);
            CommandBoundTo(vm, PropertyValue(button, "Command")!).Execute(null);
            Assert.Equal(expected, Transport.SentLines.FirstOrDefault());

            // Land the re-read each press asks for, so the next one is not
            // sitting behind an open read (and Now's latch is released).
            AnswerSentinel();
        }
    }

    /// <summary>The LQA ViewModel on a Ready session, ALE confirmed, with the
    /// verbatim R7 station book landed — the same fixture LqaViewModelTests
    /// uses, repeated here because this pin needs BOTH the markup and a live
    /// VM in one place.</summary>
    private LqaViewModel LiveLqaViewModel()
    {
        var vm = new LqaViewModel(new AleSurface(Radio), new ChannelSurface(Radio), Session);
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("SLFAD TST               CHGROUP 01");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF TST");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF TST");
        Transport.ClearSent();
        return vm;
    }

    /// <summary>Resolve <c>"{Binding Ale.Lqa.StartExchangeCommand}"</c> to the
    /// command it names on the live ViewModel. The LAST segment is the property;
    /// an unresolvable binding fails loudly rather than silently passing, which
    /// is exactly the MAUI failure mode this pin exists for.</summary>
    private static System.Windows.Input.ICommand CommandBoundTo(LqaViewModel vm, string binding)
    {
        string name = binding.Trim('{', '}').Replace("Binding ", "", StringComparison.Ordinal);
        name = name[(name.LastIndexOf('.') + 1)..];
        var property = typeof(LqaViewModel).GetProperty(name);
        Assert.True(property is not null, $"the pane binds Ale.Lqa.{name}, which the ViewModel does not have");
        return (System.Windows.Input.ICommand)property!.GetValue(vm)!;
    }

    [Fact]
    public void TheDeletedSideLabels_AreGoneFromTheWholePane()
    {
        // The absence half of the deletion (invariant 5). Lower-cased
        // "interval"/"start" were the LQA builders' own labels and nothing
        // else's, so their disappearance is assertable pane-wide.
        var texts = Root().Descendants()
            .Where(e => e.Name.LocalName == "Label")
            .Select(TextOf)
            .ToList();

        // Anti-vacuity: the scan sees the labels that ARE there, so an empty
        // (or mis-targeted) reader cannot pass this by finding nothing at all.
        Assert.Contains("Station:", texts);
        Assert.Contains("Self:", texts);

        Assert.DoesNotContain("interval", texts);
        Assert.DoesNotContain("start", texts);
    }

    [Fact]
    public void TheRetiredLqaSurfaces_AreGone()
    {
        // Deletions get absence pins (invariant 5): the report's prose header,
        // the session schedule CARDS and both of their empty views. Asserted
        // over every bound value and every literal in the pane, so a
        // reintroduction anywhere fails rather than only in its old spot.
        var values = Root().Descendants()
            .SelectMany(e => e.Attributes().Select(a => a.Value))
            .ToList();

        // Anti-vacuity: the same reader finds the REPLACEMENTS, so "no
        // ReportHeaderText anywhere" is a fact about the pane rather than about
        // a scan that reads nothing.
        Assert.Contains(values, v => v.Contains("ReportDisplayRows", StringComparison.Ordinal));
        Assert.Contains(values, v => v.Contains("ScheduleDisplayRows", StringComparison.Ordinal));

        Assert.DoesNotContain(values, v => v.Contains("ReportHeaderText", StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v.Contains("ScheduleCards", StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v.Contains("ScheduleCardViewModel", StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v == "No rows.");
        Assert.DoesNotContain(values, v => v == "No schedules set this session.");
    }

    [Fact]
    public void TheQueueFullReason_IsRenderedAsAnErrorCaption_OnTheSchedulingHeading()
    {
        // §4 gives the capacity gate a REASON, and a disabled button with an
        // invisible reason is the failure this catches.
        var label = Root().Descendants().Single(e => e.Name.LocalName == "Label"
            && TextOf(e) == "{Binding Ale.Lqa.QueueFullReason}");
        Assert.Equal("{StaticResource ErrorCaption}", PropertyValue(label, "Style"));
    }

    // ---- §4's arithmetic, pinned against the markup's own literals -------------

    [Fact]
    public void EverySection4Round11Line_FitsThePhoneContentBudget()
    {
        // The widths are read back OUT of the markup, so this is not a restated
        // constant: change a column and the inequality re-evaluates. Auto
        // columns contribute their stated ALLOWANCE, which is the only number
        // here that is a judgement rather than a measurement.
        foreach (var (what, grid, autoAllowance, expected) in new (string, XElement, double, double)[]
        {
            // Owner 2026-08-24: one report line — 56+96+64+88+88 + 4x8 = 424.
            ("report line",      ReportHeaderGrid(ReportColumns),         0,               424),
            // BROADCAST ROUND F2: the ONE schedule row —
            // 56 + star + 56 + 56 + Delete(64) + 4x8 = 264.
            ("schedule row",     ScheduleRowGrids()[0],                   DeleteAllowance, 264),
            // Each Auto column is charged at the WIDER button (Schedule, 90)
            // — conservative: the true row is 80+80+32+72+90 = 354.
            ("EXCH builder l.2", BuilderLine2("Exch"),                    SegmentWidthWide, 372),
            ("SOU builder l.2",  BuilderLine2("Sou"),                     SegmentWidthWide, 372),
        })
        {
            double line = LineWidth(grid, autoAllowance);
            Assert.True(line == expected,
                $"§4 {what}: the markup's columns now measure {line} dp, not the pinned {expected}");
            Assert.True(line <= PhoneContent,
                $"§4 {what}: {line} dp exceeds the {PhoneContent} dp phone content budget");
        }
    }

    [Fact]
    public void TheScheduleAddressColumn_KeepsEnoughRoomForA15CharacterName()
    {
        // RE-DERIVED, BROADCAST ROUND F2 (owner ruling 6, 2026-08-24). §4's
        // two-line row left the address the whole width less one 56-dp cell,
        // and this pin stated that slack as a flat 240 dp. Folding interval,
        // start and Delete onto the SAME line spends it: the residual is
        // 448 - 264 = 184 dp. The claim is therefore re-derived against what it
        // was always FOR — the radio's longest legal address at the ALE-row
        // idiom — instead of being restated at a number the ruling changed.
        // The name is the row's identity; truncating it is how the operator
        // deletes the WRONG schedule.
        double residual = PhoneContent - LineWidth(ScheduleRowGrids()[0], DeleteAllowance);
        double needed = LongestAddressChars * ConsolasAdvanceAt16;

        Assert.True(residual >= needed,
            $"F2 schedule row: {residual} dp left for the address, below the {needed} dp a "
            + $"{LongestAddressChars}-character address needs at Consolas 16. The one-line "
            + "ruling was made on the row FITTING — re-derive it with the owner rather than "
            + "letting the address column quietly get narrower than a legal name.");

        // …and the degradation past that is WRAPPING inside the cell, not
        // clipping — the half ScheduleRow_IsExactlyOneLine pins in the markup.
        Assert.True(residual <= PhoneContent,
            "the residual cannot exceed the budget it is computed from");
    }

    [Fact]
    public void TheLineWidthReader_CountsFixedColumnsSpacingsAndAllowances()
    {
        // Anti-vacuity for the arithmetic above: the reader must add the fixed
        // columns, add one spacing per gap, count "*" as zero (it is residual,
        // not demand) and charge every "Auto" the stated allowance. Getting any
        // one of those wrong makes every budget pin above meaningless.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Grid ColumnDefinitions="80,80,*,Auto,Auto" ColumnSpacing="8" />
              <Grid ColumnDefinitions="56,96,96" ColumnSpacing="8" />
              <Grid ColumnDefinitions="56,*" ColumnSpacing="8" />
            </ContentView>
            """);
        var grids = markup.Descendants().Where(e => e.Name.LocalName == "Grid").ToList();

        Assert.Equal(336, LineWidth(grids[0], 72));    // 80+80+0+72+72 + 4x8
        Assert.Equal(264, LineWidth(grids[1], 0));     // 56+96+96 + 2x8
        Assert.Equal(64, LineWidth(grids[2], 0));      // 56+0 + 1x8
    }

    /// <summary>A grid's demanded width: its fixed columns, its spacings, and
    /// <paramref name="autoAllowance"/> for every Auto column. Star columns
    /// demand nothing — they take what is left.</summary>
    private static double LineWidth(XElement grid, double autoAllowance)
    {
        var columns = (PropertyValue(grid, "ColumnDefinitions") ?? "").Split(',');
        double spacing = double.Parse(PropertyValue(grid, "ColumnSpacing") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        double total = spacing * (columns.Length - 1);
        foreach (var column in columns)
            total += column.Trim() switch
            {
                "*" => 0,
                "Auto" => autoAllowance,
                var w => double.Parse(w, System.Globalization.CultureInfo.InvariantCulture),
            };
        return total;
    }

    // ---- Anti-vacuity for the readers -----------------------------------------

    [Fact]
    public void TheGuard_ReadsBothWaysAPropertyCanBeSet_AndIgnoresComments()
    {
        // The helpers are load-bearing for every pin above, so they are pinned
        // as a unit against a synthetic sample rather than trusted: attribute
        // form, property-element form, an element that sets nothing, and a
        // commented-out element that must not be seen at all.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Grid ColumnDefinitions="44,*,64" />
              <Grid><Grid.ColumnDefinitions>72,72,72</Grid.ColumnDefinitions></Grid>
              <Grid />
              <!-- <Grid ColumnDefinitions="1,2,3" /> -->
            </ContentView>
            """);

        var grids = markup.Descendants().Where(e => e.Name.LocalName == "Grid").ToList();

        Assert.Equal(3, grids.Count);                                       // the comment is not an element
        Assert.Equal("44,*,64", PropertyValue(grids[0], "ColumnDefinitions"));
        Assert.Equal("72,72,72", PropertyValue(grids[1], "ColumnDefinitions"));
        Assert.Null(PropertyValue(grids[2], "ColumnDefinitions"));
    }

    [Fact]
    public void ThePositionReader_TreatsAnAbsentAttachmentAsColumnZero()
    {
        // Anti-vacuity for the whole positional family. The reader's ONE
        // subtlety is that an absent Grid.Column means column 0, not "no
        // opinion" — read it as "no opinion" and every first cell in the pane
        // becomes unpinned. Both spellings, the default, and a non-numeric
        // value (which must NOT quietly read as 0) are pinned together.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Grid>
                <Label />
                <Label Grid.Column="2" Grid.Row="1" />
                <Label><Grid.Column>3</Grid.Column></Label>
                <Label Grid.Column="{Binding Somewhere}" />
                <Grid.Triggers />
              </Grid>
            </ContentView>
            """);

        var grid = markup.Descendants().First(e => e.Name.LocalName == "Grid");
        var labels = Labels(grid);

        Assert.Equal(0, ColumnOf(labels[0]));       // absent  = XAML's default
        Assert.Equal(0, RowOf(labels[0]));
        Assert.Equal(2, ColumnOf(labels[1]));       // attribute form
        Assert.Equal(1, RowOf(labels[1]));
        Assert.Equal(3, ColumnOf(labels[2]));       // property-element form
        Assert.Equal(-1, ColumnOf(labels[3]));      // present but not a number: fails loudly

        // …and the cell reader excludes property elements like Grid.Triggers,
        // which would otherwise be counted as a cell in column 0.
        Assert.Equal(4, Cells(grid).Count);
    }

    [Fact]
    public void ThePositionAssertion_RejectsACellThatMovedColumn()
    {
        // The auditor's exact killing mutation, in miniature: same element,
        // same text, same fonts, different column. It must fail.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Label Grid.Column="2" Text="{Binding GroupText}" />
              <Label Grid.Column="1" Text="{Binding GroupText}" />
            </ContentView>
            """);

        var labels = markup.Descendants().Where(e => e.Name.LocalName == "Label").ToList();

        AssertCell(labels[0], "{Binding GroupText}", column: 2);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertCell(labels[1], "{Binding GroupText}", column: 2));
    }

    [Fact]
    public void TheIdiomAssertion_ActuallyChecksAllThreeFontProperties()
    {
        // AssertAleRowIdiom is the only thing standing between "the cells are
        // the §4 idiom" and "the cells are whatever". Prove it rejects each
        // single-property deviation rather than passing on presence alone.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Label FontFamily="Consolas" FontAttributes="Bold" FontSize="16" />
              <Label FontFamily="OpenSans" FontAttributes="Bold" FontSize="16" />
              <Label FontFamily="Consolas" FontSize="16" />
              <Label FontFamily="Consolas" FontAttributes="Bold" FontSize="12" />
            </ContentView>
            """);

        var labels = markup.Descendants().Where(e => e.Name.LocalName == "Label").ToList();

        AssertAleRowIdiom(labels[0]);                                        // the idiom
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAleRowIdiom(labels[1]));   // wrong family
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAleRowIdiom(labels[2]));   // no weight
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAleRowIdiom(labels[3]));   // wrong size
    }

    // ---- element selection ------------------------------------------------------

    private static XElement SelfsCard() => Card("Self addresses");

    /// <summary>Either station card, by its heading (§17: "Nets" or
    /// "Stations").</summary>
    private static XElement StationsCard(string heading) => Card(heading);

    private static XElement SelfsList()
        => SelfsCard().Descendants().Single(e =>
            (ItemsSource(e) ?? "").Contains("Ale.SelfRows", StringComparison.Ordinal));

    /// <summary>A station card's bound list. Located by the CARD, not by the
    /// binding name, so the two cards cannot be confused for one another.</summary>
    private static XElement List(string heading)
        => StationsCard(heading).Descendants().Single(e => ItemsSource(e) is not null);

    /// <summary>The selfs card's HEADER grid: the one two-column grid that is
    /// not inside the item template and not the card's heading row.</summary>
    private static XElement SelfsHeaderGrid()
        => SelfsCard().Descendants()
            .Single(e => e.Name.LocalName == "Grid"
                && PropertyValue(e, "ColumnDefinitions") == SelfsColumns
                && !IsInsideTemplate(e));

    private static XElement SelfsRowGrid()
        => Template(SelfsList(), "AleSelfRowViewModel").Descendants()
            .Single(e => e.Name.LocalName == "Grid");

    /// <summary>A station card's HEADER grid. BROADCAST ROUND F3: the Nets card
    /// carries TWO more grids on the same columns outside its template (the
    /// pinned ANY/ALL rows), so the header is identified by what only a header
    /// has — <c>CellHeading</c> cells.</summary>
    private static XElement HeaderGrid(string heading)
        => StationsCard(heading).Descendants()
            .Single(e => e.Name.LocalName == "Grid"
                && PropertyValue(e, "ColumnDefinitions") == StationColumns
                && !IsInsideTemplate(e)
                && Labels(e).Any(l =>
                    (PropertyValue(l, "Style") ?? "").Contains("CellHeading", StringComparison.Ordinal)));

    /// <summary>One PINNED broadcast row (F3), by the literal in its name
    /// cell — the rows are fixed markup, not a template, so they are located by
    /// content exactly as the two LQA builders are.</summary>
    private static XElement PinnedRow(string address)
        => Card("Nets").Descendants()
            .Single(e => e.Name.LocalName == "Grid"
                && !IsInsideTemplate(e)
                && Labels(e).Any(l => TextOf(l) == address));

    /// <summary>The row grids of a station card's item template. Round 13 §4 A3
    /// leaves exactly ONE — the collection shape is kept so the single-grid
    /// fact stays ASSERTED (TheRow_IsExactlyOneLine) rather than assumed by a
    /// helper that would quietly pick the first of several.</summary>
    private static IReadOnlyList<XElement> RowGrids(string heading)
        =>
        [
            .. Template(List(heading), "AleStationRowViewModel").Descendants()
                .Where(e => e.Name.LocalName == "Grid")
        ];

    // ---- Round 11 §4 element selection ---------------------------------------

    private static XElement AmdCard() => Card("AMD");
    // Owner 2026-08-25: the LQA card SPLIT — Scores and Scheduling are two
    // frames now, and each helper resolves through the card that owns it.
    private static XElement ScoresCard() => Card("Scores");
    private static XElement SchedulingCard() => Card("Scheduling");

    private static XElement Card(string heading)
        => Root().Descendants().Single(e => e.Name.LocalName == "Border"
            && (PropertyValue(e, "Style") ?? "").Contains("StaticResource Card", StringComparison.Ordinal)
            && Classify(e) == heading);

    private static XElement ReportList()
        => ScoresCard().Descendants().Single(e =>
            (ItemsSource(e) ?? "").Contains("Ale.Lqa.ReportDisplayRows", StringComparison.Ordinal));

    private static XElement ScheduleList()
        => SchedulingCard().Descendants().Single(e =>
            (ItemsSource(e) ?? "").Contains("Ale.Lqa.ScheduleDisplayRows", StringComparison.Ordinal));

    private static XElement ReportHeaderGrid(string columns)
        => ScoresCard().Descendants().Single(e => e.Name.LocalName == "Grid"
            && PropertyValue(e, "ColumnDefinitions") == columns
            && !IsInsideTemplate(e));

    private static IReadOnlyList<XElement> ReportRowGrids()
        =>
        [
            .. Template(ReportList(), "LqaReportRowViewModel").Descendants()
                .Where(e => e.Name.LocalName == "Grid")
        ];

    private static XElement ScheduleHeaderGrid()
        => SchedulingCard().Descendants().Single(e => e.Name.LocalName == "Grid"
            && PropertyValue(e, "ColumnDefinitions") == ScheduleColumns
            && !IsInsideTemplate(e));

    private static IReadOnlyList<XElement> ScheduleRowGrids()
        =>
        [
            .. Template(ScheduleList(), "LqaScheduleRowViewModel").Descendants()
                .Where(e => e.Name.LocalName == "Grid")
        ];

    /// <summary>A builder's LINE 1, identified by the side label it carries —
    /// the two builders share a geometry, so only their content tells them
    /// apart.</summary>
    private static XElement BuilderLine1(string sideLabel)
        => SchedulingCard().Descendants().Single(e => e.Name.LocalName == "Grid"
            && PropertyValue(e, "ColumnDefinitions") == BuilderLine1Columns
            && Labels(e).Any(l => TextOf(l) == sideLabel));

    /// <summary>A builder's LINE 2, identified by the ViewModel prefix its
    /// interval Entry binds ("Exch" / "Sou").</summary>
    private static XElement BuilderLine2(string vmPrefix)
        => SchedulingCard().Descendants().Single(e => e.Name.LocalName == "Grid"
            && PropertyValue(e, "ColumnDefinitions") == BuilderLine2Columns
            && Entries(e).Any(x => (TextOf(x) ?? "")
                .Contains($"Ale.Lqa.{vmPrefix}IntervalText", StringComparison.Ordinal)));

    private static List<XElement> Entries(XElement grid)
        => grid.Elements().Where(e => e.Name.LocalName == "Entry").ToList();

    private static string? ItemsSourceOf(XElement element)
        => PropertyValue(element, "ItemsSource");

    private static XElement Template(XElement list, string dataType)
        => list.Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate"
                && (e.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value ?? "")
                    .EndsWith(dataType, StringComparison.Ordinal));

    private static bool IsInsideTemplate(XElement e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
            if (p.Name.LocalName == "DataTemplate") return true;
        return false;
    }

    private static List<XElement> Labels(XElement grid)
        => grid.Elements().Where(e => e.Name.LocalName == "Label").ToList();

    private static List<XElement> Buttons(XElement grid)
        => grid.Elements().Where(e => e.Name.LocalName == "Button").ToList();

    /// <summary>Every CELL of a grid — the child controls, in document order.
    /// Property elements (<c>&lt;Grid.Triggers&gt;</c> and friends) carry a dot
    /// in their local name and are not cells.</summary>
    private static List<XElement> Cells(XElement grid)
        => grid.Elements()
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();

    /// <summary>A cell's <c>Grid.Column</c> / <c>Grid.Row</c>. An ABSENT
    /// attachment is 0 — XAML's own default, and the reason a positional pin
    /// has to read the default rather than the attribute's presence. A value
    /// that is present but not a number returns -1 so it fails loudly instead
    /// of silently reading as column 0.</summary>
    private static int ColumnOf(XElement cell) => GridIndex(cell, "Grid.Column");

    private static int RowOf(XElement cell) => GridIndex(cell, "Grid.Row");

    private static int GridIndex(XElement cell, string property)
    {
        string? raw = PropertyValue(cell, property);
        if (raw is null) return 0;
        return int.TryParse(raw, out int value) ? value : -1;
    }

    // ---- shared assertions ------------------------------------------------------

    /// <summary>A cell is its CONTENT and its POSITION. Asserting only the
    /// former is what audit round 1 caught: document order made a moved cell
    /// invisible.</summary>
    private static void AssertCell(XElement cell, string expectedText, int column)
    {
        Assert.Equal(expectedText, TextOf(cell));
        Assert.Equal(column, ColumnOf(cell));
        Assert.Equal(0, RowOf(cell));
    }

    private static void AssertAleRowIdiom(XElement cell)
    {
        Assert.Equal(IdiomFamily, PropertyValue(cell, "FontFamily"));
        Assert.Equal(IdiomWeight, PropertyValue(cell, "FontAttributes"));
        Assert.Equal(IdiomSize, PropertyValue(cell, "FontSize"));
    }

    private static void AssertActionBlock(XElement grid, int firstColumn)
    {
        var buttons = Buttons(grid);
        Assert.Equal(["LQA ▸", "AMD ▸", "CALL"], buttons.Select(TextOf));

        // ROUND 13 §4 A3: each button's COMMAND and its ENABLEMENT are pinned
        // here now. They used to be covered only incidentally — the LQA guard
        // asserted LQA's pair and nothing asserted AMD's or CALL's, so the
        // template collapse could have dropped an IsEnabled and left a button
        // live during an in-flight call with the whole suite green. These
        // survive the collapse and are re-pinned WITH it.
        (string Text, string Command, string Enabled)[] wiring =
        [
            ("LQA ▸", "{Binding LqaCommand}", "{Binding CanLqa}"),
            ("AMD ▸", "{Binding AmdCommand}", "{Binding CanAmd}"),
            ("CALL", "{Binding CallCommand}", "{Binding CanCall}"),
        ];

        for (int i = 0; i < buttons.Count; i++)
        {
            Assert.Equal(firstColumn + i, ColumnOf(buttons[i]));
            Assert.Equal(0, RowOf(buttons[i]));
            // §4: "buttons filling their fixed columns" — a natural-width
            // button in a fixed column leaves ragged gaps between rows.
            Assert.Equal("Fill", PropertyValue(buttons[i], "HorizontalOptions"));

            Assert.Equal(wiring[i].Command, PropertyValue(buttons[i], "Command"));
            Assert.Equal(wiring[i].Enabled, PropertyValue(buttons[i], "IsEnabled"));
        }
    }

    /// <summary>Every attribute value and property-element value under an
    /// element — everywhere a markup extension can legally live. Used by the
    /// no-OnIdiom pin, which has to see BOTH forms or the split could come back
    /// as a property element (the round-2 evasion this file documents).</summary>
    private static IEnumerable<string> AllPropertyValues(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            foreach (var a in e.Attributes()) yield return a.Value;
            if (e.Name.LocalName.Contains('.', StringComparison.Ordinal) && !e.HasElements)
                yield return e.Value;
        }
    }

    // ---- The link banner's state colours (round 15 item I) ---------------------

    [Fact]
    public void TheBannerChip_CarriesTheStateTriggersInPAIRS_BorderFillAndLabelText()
    {
        // Critic F72, and the round-14 lesson the HOP sync chip's guard already
        // records: a filled chip needs TWO triggers per state — the Border's
        // BackgroundColor and the Label's on-accent TextColor — because the
        // fills are dark in both themes, so a fill without its text colour
        // renders a dark box with unreadable text (App.xaml's on-accent rule).
        // Deleting either half breaks nothing a VM test can see.
        //
        // Pinned as exact CONTENT in document order, so the pin cannot pass
        // vacuously: a reader that found nothing would return an empty list and
        // fail against five expected entries.
        var chip = Root().Descendants().Single(e =>
            e.Name.LocalName == "Border" && Description(e) == "ALE link state banner");
        var label = chip.Descendants().Single(e => e.Name.LocalName == "Label");

        (string Binding, string Property, string Value)[] borderFill =
        [
            // Owner 2026-08-24 (linked-amd round): scanning wears the standard
            // accent blue; an established link is the good state - green.
            ("{Binding Ale.IsScanning}", "BackgroundColor", "{StaticResource AccentColor}"),
            ("{Binding Ale.IsCalling}", "BackgroundColor", "{StaticResource ChipWarnColor}"),
            ("{Binding Ale.IsSending}", "BackgroundColor", "{StaticResource ChipWarnColor}"),
            ("{Binding Ale.IsLinked}", "BackgroundColor", "{StaticResource ChipOkColor}"),
            // An LQA is a transmission — the same warn fill Calling/Sending wear.
            ("{Binding Ale.IsLqa}", "BackgroundColor", "{StaticResource ChipWarnColor}"),
            // The inbound handshake (field capture 2026-08-24): activity
            // detected is good news - ok green, like the link it becomes.
            ("{Binding Ale.IsIncomingCall}", "BackgroundColor", "{StaticResource ChipOkColor}"),
        ];
        Assert.Equal(borderFill, DataTriggersOn(chip));

        (string Binding, string Property, string Value)[] labelText =
        [
            ("{Binding Ale.IsScanning}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Ale.IsCalling}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Ale.IsSending}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Ale.IsLinked}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Ale.IsLqa}", "TextColor", "{StaticResource OnAccentColor}"),
            ("{Binding Ale.IsIncomingCall}", "TextColor", "{StaticResource OnAccentColor}"),
        ];
        Assert.Equal(labelText, DataTriggersOn(label));
    }

    [Fact]
    public void TheBannerCard_IsOTHERWISEUnchanged_OneLabelOneSpinnerOnCalling()
    {
        // The chip's CONTENT did not move for item I: one activity indicator
        // (Calling only — an LQA has no handshake to wait on) and one banner
        // Label, still the ALE row idiom's monospace.
        var chip = Root().Descendants().Single(e =>
            e.Name.LocalName == "Border" && Description(e) == "ALE link state banner");

        var spinner = Assert.Single(chip.Descendants(), e => e.Name.LocalName == "ActivityIndicator");
        Assert.Equal("{Binding Ale.IsCalling}", PropertyValue(spinner, "IsRunning"));
        Assert.Equal("{Binding Ale.IsCalling}", PropertyValue(spinner, "IsVisible"));

        var label = Assert.Single(chip.Descendants(), e => e.Name.LocalName == "Label");
        Assert.Equal("{Binding Ale.BannerText}", TextOf(label));
        Assert.Equal(IdiomFamily, PropertyValue(label, "FontFamily"));
    }

    /// <summary>The <c>DataTrigger</c>s declared on an element ITSELF (its own
    /// <c>&lt;X.Triggers&gt;</c> block, never a descendant's), flattened to
    /// binding · setter property · setter value — the
    /// <c>OperateStyleAdoptionGuardTests</c> reader, which is what lets the
    /// Border's set and the Label's set be told apart when they bind the same
    /// flags.</summary>
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

    // ---- reading XAML (attribute AND property-element forms) --------------------

    /// <summary>A property set as an ATTRIBUTE or as a property ELEMENT. Two
    /// property-element spellings exist and both are read: an OWN property is
    /// <c>&lt;Label.Text&gt;</c> (owner-prefixed), while an ATTACHED one keeps
    /// its own name — <c>&lt;Grid.Column&gt;</c> inside the child. Reading only
    /// the first spelling would leave every positional pin evadable by the
    /// property-element form, which is precisely the round-2 evasion
    /// RefreshButtonWidthGuardTests documents.</summary>
    private static string? PropertyValue(XElement element, string property)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == property)?.Value
            ?? element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + "." + property)?.Value.Trim()
            ?? (property.Contains('.', StringComparison.Ordinal)
                ? element.Elements().FirstOrDefault(e => e.Name.LocalName == property)?.Value.Trim()
                : null);

    private static string? TextOf(XElement element) => PropertyValue(element, "Text");

    private static string? Description(XElement element)
        => element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "SemanticProperties.Description")?.Value;

    private static string? ItemsSource(XElement element)
        => element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "BindableLayout.ItemsSource")?.Value;

    private static XElement Root()
    {
        var path = Path.Combine(FindRepoRoot(), AlePane);
        Assert.True(File.Exists(path), "pane markup missing: " + AlePane);
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
