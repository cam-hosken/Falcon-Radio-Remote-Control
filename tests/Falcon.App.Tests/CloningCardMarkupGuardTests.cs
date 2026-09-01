using System.Text.RegularExpressions;
using System.Xml.Linq;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The Radio-settings CLONING card, AS BUILT (plan round 11 §9A; the identity
/// TABLE from plan-clone-field-round2 §3.3; the TWO-TAB reorganization from
/// plan-clone-pane-cleanup §5).
///
/// <para><b>This file REPLACES <c>CloningStubTests</c>.</b> That guard existed
/// to hold the round-4 AL3 card INERT — every control <c>IsEnabled="False"</c>,
/// not one Command binding anywhere in it — until the cloning backend landed.
/// The backend is P6, so the stub's pins are not "relaxed", they are DEAD: a
/// card that is wired cannot also be provably unwireable. The replacement
/// asserts the OPPOSITE properties (every control reachable, both campaigns
/// bound, the identity table present), plus an absence pin that the retired
/// file is really gone — so nobody can quietly restore a guard that now
/// contradicts the design.</para>
///
/// <para><b>STRUCTURAL, not textual.</b> The card is parsed as XML and asserted
/// on its element TREE — the tab pair and its two bodies, the three section
/// headings in order, and a BindableLayout over the rows whose ItemTemplate is
/// typed to <see cref="SelfRowViewModel"/> and holds the swap Picker, the
/// replace Entry and the scan-gate caption. A regex over the raw markup would
/// pass on a card whose controls had drifted out of the template — or out of
/// their tab — which is exactly the shape these rounds created.</para>
///
/// <para><b>D17 (2026-08-30) widens the file's SCOPE past the card, on
/// purpose.</b> The Console's export was unified with the card's D13 model, and
/// it lives on the SAME page, in the same code-behind, read by the same
/// scanners — so its pins are here (the "D17: the CONSOLE card's export"
/// region) rather than in a second file that would have to copy
/// <c>HandlerBody</c>, <c>CodeOnly</c> and the argument reader. The
/// no-durable-write pin is now FILE-WIDE for the same reason.</para>
/// </summary>
public class CloningCardMarkupGuardTests
{
    private const string PageRelativePath = @"src\Falcon.App\Views\RadioSettingsPage.xaml";

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2009/xaml";

    [Fact]
    public void TheRetiredStubGuard_IsGone_AndThisFileIsItsReplacement()
    {
        // The stub's own pin (`CloningCard_EveryControlIsDisabled`) asserted a
        // property this card must NOT have. Naming the retirement here is what
        // stops it coming back as a "fix" for the pins below.
        var retired = Path.Combine(FindRepoRoot(), "tests", "Falcon.App.Tests", "CloningStubTests.cs");
        Assert.False(File.Exists(retired),
            "CloningStubTests.cs is back — it pins the card INERT, which P6 deliberately ended.");
    }

    [Fact]
    public void TheCard_HasNoDisabledControl_TheStubsDefiningProperty()
    {
        var card = CloningCard();
        Assert.DoesNotContain(card.Descendants(), e =>
            string.Equals((string?)e.Attribute("IsEnabled"), "False", StringComparison.Ordinal));

        // ANTI-VACUITY: the card really was parsed and really does hold
        // controls, so "no disabled control" is not "no control at all".
        Assert.True(card.Descendants().Count() > 20,
            "the Cloning card parsed to " + card.Descendants().Count() + " elements — it is not the card");
    }

    [Fact]
    public void BothCampaigns_AreWired_AndSoIsTheFileImport()
    {
        var card = CloningCard();

        // The write campaign is a COMMAND (the VM owns its gate); the read and
        // the import are Clicked handlers, because each pairs the VM's campaign
        // with the view-owned file path (the Stage-8 export seam).
        Assert.Contains(card.Descendants(), e =>
            (string?)e.Attribute("Command") == "{Binding WriteCommand}");
        Assert.Contains(card.Descendants(), e => (string?)e.Attribute("Clicked") == "OnCloneReadClicked");
        Assert.Contains(card.Descendants(), e => (string?)e.Attribute("Clicked") == "OnCloneOpenClicked");

        // …and the handlers really exist in the code-behind, so a rename cannot
        // leave the markup pointing at nothing.
        var codeBehind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.Contains("void OnCloneReadClicked", codeBehind, StringComparison.Ordinal);
        Assert.Contains("void OnCloneOpenClicked", codeBehind, StringComparison.Ordinal);
    }

    // ---- The two tabs (§5) ---------------------------------------------------

    /// <summary>
    /// The tab strip is the MODEM PRESETS mechanism, copied: a full-width
    /// Segment pair, each button highlighted by a DataTrigger on the ONE
    /// tab-state property. A pair that switched the bodies but never
    /// highlighted would leave the operator unable to tell which tab they are
    /// on — the exact defect the round-4 backlight row had.
    /// </summary>
    [Fact]
    public void TheTabPair_IsTwoSegments_EachHighlightedByItsOwnTrigger()
    {
        var card = CloningCard();

        var read = TabButton(card, "Read");
        var write = TabButton(card, "Write");

        Assert.Equal("{Binding OpenReadTabCommand}", (string?)read.Attribute("Command"));
        Assert.Equal("{Binding OpenWriteTabCommand}", (string?)write.Attribute("Command"));
        Assert.Equal("Show the read-from-radio tab",
            (string?)read.Attribute("SemanticProperties.Description"));
        Assert.Equal("Show the write-to-radio tab",
            (string?)write.Attribute("SemanticProperties.Description"));

        // The house vocabulary, and full width (I-1): no ad-hoc size anywhere
        // on either selector.
        foreach (var button in new[] { read, write })
        {
            Assert.Equal("{StaticResource Segment}", (string?)button.Attribute("Style"));
            Assert.Equal("Fill", (string?)button.Attribute("HorizontalOptions"));
            Assert.Null(button.Attribute("WidthRequest"));
            Assert.Null(button.Attribute("FontSize"));
        }

        // The SELECTED treatment, one trigger each, on complementary values of
        // the same property.
        Assert.Equal("False", TabTriggerValue(read));
        Assert.Equal("True", TabTriggerValue(write));
    }

    /// <summary>The two bodies are switched on the SAME property the selectors
    /// read, and on complementary values — so exactly one is ever on
    /// screen.</summary>
    [Fact]
    public void TheTwoTabBodies_AreSwitchedOnTheOneTabStateProperty()
    {
        var card = CloningCard();
        var readTab = TabBody(card, "CloneReadTab");
        var writeTab = TabBody(card, "CloneWriteTab");

        // The read tab is the DEFAULT (D10): visible as declared, hidden by its
        // trigger when the write tab opens.
        Assert.Equal("True", (string?)readTab.Attribute("IsVisible"));
        Assert.Equal(("True", "False"), BodyTrigger(readTab));

        Assert.Equal("False", (string?)writeTab.Attribute("IsVisible"));
        Assert.Equal(("True", "True"), BodyTrigger(writeTab));

        // …and neither body is inside the other.
        Assert.DoesNotContain(readTab.Descendants(), e => e == writeTab);
        Assert.DoesNotContain(writeTab.Descendants(), e => e == readTab);
    }

    /// <summary>D7: the Write tab reads as three named sections, in the order
    /// the operator works through them. All three are CardHeadings (I-1) — the
    /// retired "ALE selfs for write" Caption is what the middle one
    /// replaced.</summary>
    [Fact]
    public void TheWriteTab_CarriesItsThreeSectionHeadings_InOrder()
    {
        var card = CloningCard();
        var writeTab = TabBody(card, "CloneWriteTab");

        var headings = writeTab.Descendants()
            .Where(e => e.Name.LocalName == "Label"
                && (string?)e.Attribute("Style") == "{StaticResource CardHeading}")
            .Select(e => (string?)e.Attribute("Text"))
            .ToList();

        Assert.Equal(["Clone file", "ALE identity", "Write"], headings);

        // The retired caption is GONE from the whole card, not merely from this
        // tab — a heading and a caption saying the same thing is two things to
        // keep in sync.
        Assert.DoesNotContain(card.Descendants(), e =>
            (string?)e.Attribute("Text") == "ALE selfs for write");
    }

    [Fact]
    public void TheReadTab_ReadsTopToBottom_Press_Reason_Status_Notice_Report()
    {
        var readTab = TabBody(CloningCard(), "CloneReadTab");

        var press = Assert.Single(readTab.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnCloneReadClicked");
        Assert.Equal("Read radio settings", TextOf(press));
        Assert.Equal("{Binding CanRead}", (string?)press.Attribute("IsEnabled"));

        var reason = Assert.Single(readTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding ReadGateReason}");
        Assert.Equal("{Binding HasReadGateReason}", (string?)reason.Attribute("IsVisible"));
        Assert.Equal("{StaticResource Caption}", (string?)reason.Attribute("Style"));

        var status = Assert.Single(readTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding ReadStatusText}");
        Assert.Equal("{StaticResource StatusText}", (string?)status.Attribute("Style"));

        AssertTwoLabelNoticeSlot(readTab, "ReadFileNotice", "ShowsReadFileNotice", "ShowsReadFileError");
        AssertReportBlock(readTab, "ReadReportLines", "HasReadReport",
            "{Binding ClearReadReportCommand}", "Clear the read report");
    }

    /// <summary>
    /// D13: BOTH export presses, on the READ tab. The read itself saves nothing
    /// now, so these two are the only ways a file leaves the app — "Store file…"
    /// through the system save-location picker, "Share…" through the share
    /// sheet. They are bound to the SAME VM gate (a file in hand, nothing
    /// running), which is NOT the read's gate: moving a file the app already has
    /// needs neither a radio nor a quiet one.
    /// </summary>
    [Theory]
    [InlineData("OnCloneStoreClicked", "Store file…", "Choose where to save the clone file")]
    [InlineData("OnCloneShareClicked", "Share…", "Send the clone file to another app")]
    public void TheReadTab_CarriesBothExportPresses_OnTheOneGate_D13(
        string handler, string text, string description)
    {
        var readTab = TabBody(CloningCard(), "CloneReadTab");

        var press = Assert.Single(readTab.Descendants(), e =>
            (string?)e.Attribute("Clicked") == handler);

        Assert.Equal(text, TextOf(press));
        Assert.Equal("{Binding CanStore}", (string?)press.Attribute("IsEnabled"));
        Assert.Equal(description, (string?)press.Attribute("SemanticProperties.Description"));

        // The house vocabulary, and full width (I-1): no ad-hoc size on it.
        Assert.Equal("{StaticResource Segment}", (string?)press.Attribute("Style"));
        Assert.Equal("Fill", (string?)press.Attribute("HorizontalOptions"));
        Assert.Null(press.Attribute("WidthRequest"));
        Assert.Null(press.Attribute("FontSize"));

        // …and the handler really exists, so a rename cannot leave the markup
        // pointing at nothing.
        var codeBehind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.Contains("void " + handler, codeBehind, StringComparison.Ordinal);

        // ANTI-VACUITY: this tab really is the one holding the READ press too —
        // so "the export presses are on the Read tab" is not "the scan found an
        // empty tab and agreed with itself". Deleting any button, or any
        // binding, fails this test.
        Assert.Contains(readTab.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnCloneReadClicked"
            && (string?)e.Attribute("IsEnabled") == "{Binding CanRead}");
        Assert.NotEqual("{Binding CanRead}", (string?)press.Attribute("IsEnabled"));

        // …and BOTH presses are there, side by side on the ONE gate — a build
        // that shipped Store alone (the D12 card) fails here.
        var pair = readTab.Descendants()
            .Where(e => (string?)e.Attribute("Clicked") is "OnCloneStoreClicked" or "OnCloneShareClicked")
            .ToList();
        Assert.Equal(2, pair.Count);
        Assert.All(pair, e => Assert.Equal("{Binding CanStore}", (string?)e.Attribute("IsEnabled")));
    }

    [Fact]
    public void TheWriteTab_CarriesTheFileLine_TheIdentityTable_AndTheWritePress()
    {
        var card = CloningCard();
        var writeTab = TabBody(card, "CloneWriteTab");

        // ONE file line, in the STATUS role — it is what the card says about
        // the file in hand, not a caption beside something else.
        var fileLine = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding FileLine}");
        Assert.Equal("{StaticResource StatusText}", (string?)fileLine.Attribute("Style"));

        var open = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnCloneOpenClicked");
        Assert.Equal("Open file…", TextOf(open));
        // The dead OnPlatform is gone: both of its branches said the same
        // thing, so it was a switch with one position.
        Assert.DoesNotContain("OnPlatform", TextOf(open) ?? "", StringComparison.Ordinal);

        AssertTwoLabelNoticeSlot(writeTab, "OpenFileNotice", "ShowsOpenFileNotice", "ShowsOpenFileError");

        var press = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Command") == "{Binding WriteCommand}");
        Assert.Equal("Write file to radio", TextOf(press));
        Assert.Equal("Fill", (string?)press.Attribute("HorizontalOptions"));

        var reason = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding WriteGateReason}");
        Assert.Equal("{Binding HasWriteGateReason}", (string?)reason.Attribute("IsVisible"));

        var status = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding WriteStatusText}");
        Assert.Equal("{StaticResource StatusText}", (string?)status.Attribute("Style"));

        AssertReportBlock(writeTab, "WriteReportLines", "HasWriteReport",
            "{Binding ClearWriteReportCommand}", "Clear the write report");

        // The identity table is on THIS tab — it is what the write is
        // configured with, and it has no business on the read tab.
        Assert.Contains(writeTab.Descendants(), e => e == SelfRowsLayout(card));
    }

    /// <summary>The retired single-surface bindings are GONE from the card. A
    /// card holding both would have two places for a status line and no rule
    /// about which one an operator is meant to read.</summary>
    [Fact]
    public void TheRetiredSingleSurfaceBindings_AreGoneFromTheCard()
    {
        var card = CloningCard();
        var bindings = card.Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        foreach (var retired in new[]
                 {
                     "{Binding FileText}", "{Binding FileNotice}", "{Binding HasFileNotice}",
                     "{Binding StatusText}", "{Binding SummaryLines}", "{Binding HasSummary}",
                     "{Binding GateReason}", "{Binding HasGateReason}",
                 })
            Assert.DoesNotContain(bindings, v => v.Contains(retired, StringComparison.Ordinal));

        // ANTI-VACUITY: the sweep really does see the bindings that ARE there,
        // so "none of the retired ones" is not "no bindings at all".
        foreach (var live in new[]
                 {
                     "{Binding ReadStatusText}", "{Binding WriteStatusText}",
                     "{Binding ReadReportLines}", "{Binding WriteReportLines}",
                     "{Binding FileLine}", "{Binding IsWriteTabOpen}",
                 })
            Assert.Contains(bindings, v => v.Contains(live, StringComparison.Ordinal));

        // …and no OnPlatform survives in this card at all: the only one it ever
        // had chose between two identical strings.
        Assert.DoesNotContain(bindings, v => v.Contains("OnPlatform", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-A: the identity control is a TABLE — one row per self — not round 11's
    /// single picker plus Entry. The rows come from <c>SelfRows</c> through a
    /// BindableLayout (a CollectionView cannot live inside the page's
    /// ScrollView), and the per-row controls are INSIDE its ItemTemplate.
    /// </summary>
    [Fact]
    public void TheIdentityControl_IsATableOverSelfRows_RA()
    {
        var layout = SelfRowsLayout(CloningCard());

        var template = Assert.Single(layout.Descendants(), e => e.Name.LocalName == "DataTemplate");
        Assert.Equal("vm:SelfRowViewModel", (string?)template.Attribute(X + "DataType"));

        // The row's TITLE, so a row is never an anonymous pair of controls.
        // ROUND 15 C-1: the title is what carries the NET ("Net HFL · group 2
        // · self W6HOS"), so this binding is the whole per-net rework on
        // screen — it is built in SelfRowViewModel, not composed here.
        Assert.Contains(template.Descendants(), e =>
            e.Name.LocalName == "Label" && (string?)e.Attribute("Text") == "{Binding Title}");
    }

    /// <summary>
    /// ROUND 15's two captions: C-Q5's "the book was not read" (which stands
    /// in for the rows) and C-3's fill-gate refusal. Neither may grey a
    /// control silently, so both are on screen with their own visibility.
    /// </summary>
    [Fact]
    public void TheCard_CarriesBothRoundFifteenCaptions_WithTheirOwnVisibility()
    {
        var card = CloningCard();

        // C-Q5 — quoted from the VM's own constant, so the card and the
        // ViewModel cannot drift apart.
        var bookNotRead = Assert.Single(card.Descendants(), e =>
            (string?)e.Attribute("Text") == CloneViewModel.BookNotReadCaption);
        Assert.Equal("{Binding ShowsBookNotRead}", (string?)bookNotRead.Attribute("IsVisible"));

        // C-3 — the sentence names the offending self, so it is BOUND rather
        // than literal.
        var fillGate = Assert.Single(card.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding FillGateReason}");
        Assert.Equal("{Binding HasFillGateReason}", (string?)fillGate.Attribute("IsVisible"));
        Assert.Equal("{StaticResource ErrorCaption}", (string?)fillGate.Attribute("Style"));

        // …and both sit OUTSIDE the row template: they are the card's, not a
        // row's.
        Assert.DoesNotContain(SelfRowsLayout(card).Descendants(), e => e == bookNotRead || e == fillGate);
    }

    [Fact]
    public void EachRow_HasItsSwapPicker_ItsReplaceEntry_AndTheScanGateCaption()
    {
        var template = Assert.Single(SelfRowsLayout(CloningCard()).Descendants(), e => e.Name.LocalName == "DataTemplate");

        // The SWAP picker, shown only where a swap is offered (A-1's other
        // half: the scan-gate and no-self rows do not get one).
        var picker = Assert.Single(template.Descendants(), e => e.Name.LocalName == "Picker");
        Assert.Equal("{Binding SwapChoices}", (string?)picker.Attribute("ItemsSource"));
        Assert.Equal("{Binding SwapSelection, Mode=TwoWay}", (string?)picker.Attribute("SelectedItem"));
        Assert.Equal("{Binding OffersSwap}", (string?)VisibilityOwner(picker).Attribute("IsVisible"));

        // The REPLACE entry, capped at the row's own name length (D2 — the
        // scan-gate row says 3).
        var entry = Assert.Single(template.Descendants(), e => e.Name.LocalName == "Entry");
        Assert.Equal("{Binding ReplaceInput, Mode=TwoWay}", (string?)entry.Attribute("Text"));
        Assert.Equal("{Binding NameLength}", (string?)entry.Attribute("MaxLength"));

        // The scan-gate CAPTION, byte-identical to the constant `docs/ui.md`
        // quotes, and visible on exactly the scan-gate rows.
        var caption = Assert.Single(template.Descendants(), e =>
            (string?)e.Attribute("Text") == SelfRowViewModel.ScanGateCaption);
        Assert.Equal("{Binding IsScanGateSelf}", (string?)caption.Attribute("IsVisible"));

        // I-1's listed exemption: the identity rows keep their 140 column,
        // unchanged by the reorganization.
        Assert.Equal(2, template.Descendants()
            .Count(e => (string?)e.Attribute("ColumnDefinitions") == "*,140"));
    }

    /// <summary>The round-11 control is GONE, not merely unbound — a card
    /// holding both would have two places to choose an identity and no rule
    /// about which one wins.</summary>
    [Fact]
    public void TheRoundElevenSinglePickerAndItsGateHint_AreGone()
    {
        var card = CloningCard();
        var bindings = card.Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        foreach (var retired in new[]
                 {
                     "IdentityChoices", "SelectedIdentity", "IdentityInput", "ShowsGateHint",
                 })
            Assert.DoesNotContain(bindings, v => v.Contains(retired, StringComparison.Ordinal));

        // The §5 gate hint's sentence went with it: the scan-gate rule is now a
        // per-row caption, not a hint about one chosen name.
        Assert.DoesNotContain(card.Descendants(), e =>
            ((string?)e.Attribute("Text"))?.Contains("satisfies the scan gate", StringComparison.Ordinal) == true
            && (string?)e.Attribute("IsVisible") != "{Binding IsScanGateSelf}");
    }

    [Fact]
    public void TheCard_ShowsTheIdentityError_WithItsOwnVisibility()
    {
        // Nothing greys silently: the table's own refusal has a home in the
        // markup, in the error style, on the tab it belongs to.
        var writeTab = TabBody(CloningCard(), "CloneWriteTab");

        var error = Assert.Single(writeTab.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding IdentityError}");
        Assert.Equal("{Binding HasIdentityError}", (string?)error.Attribute("IsVisible"));
        Assert.Equal("{StaticResource ErrorCaption}", (string?)error.Attribute("Style"));
    }

    [Fact]
    public void TheCard_BindsItsOwnViewModel_TheDeviceClockPattern()
    {
        var card = CloningCard();
        Assert.Equal("CloningSection", (string?)card.Attribute(X + "Name"));
        Assert.Equal("vm:CloneViewModel", (string?)card.Attribute(X + "DataType"));

        // I-5: the heading text is what SettingsPlacementGuardTests matches the
        // card on, so it is pinned here too.
        Assert.Contains(card.Descendants(), e =>
            e.Name.LocalName == "Label"
            && (string?)e.Attribute("Text") == "Cloning"
            && (string?)e.Attribute("Style") == "{StaticResource CardHeading}");

        var codeBehind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.Contains("CloningSection.BindingContext = clone;", codeBehind, StringComparison.Ordinal);
    }

    // ---- The code-behind's file I/O (audit round 1) --------------------------
    //
    // `Falcon.App` does not compile into this test project — the code-behind is
    // reachable only as SOURCE. Both pins below therefore scan the file with
    // its COMMENTS STRIPPED (so a commented-out call can never satisfy them)
    // but its string literals INTACT (the operator wording is the thing being
    // pinned), and each carries an anti-vacuity clause proving the scan really
    // found the handler it claims to be reading.

    /// <summary>
    /// D13 (2026-08-30, owner): THE READ OWNS NO EXPORT AT ALL. It used to
    /// persist its file and pop the share sheet from this handler, behind an
    /// audit-round-1 guard that skipped the whole block when the read had
    /// installed nothing (otherwise the PREVIOUSLY LOADED file went out under a
    /// fresh "read from this radio" name — one press from programming stale
    /// settings into the radio the operator is standing at). D13 removed the
    /// export instead of guarding it, so the handler is the campaign and
    /// nothing else, and that whole class of defect is gone by construction.
    ///
    /// <para>This pin is the REPLACEMENT for the stale-read guard, and it is
    /// STRICTLY STRONGER: the old one allowed a save the flag agreed with, this
    /// one allows no save at all.</para>
    /// </summary>
    [Fact]
    public void TheReadHandler_OwnsNoExportAtAll_D13()
    {
        var handler = HandlerBody("OnCloneReadClicked");
        var code = CodeOnly(handler);

        // The gate, the campaign, and the end of the handler.
        Assert.Matches(@"if\s*\(\s*!\s*_clone\.CanRead\s*\)\s*return\s*;", code);
        Assert.Contains("_clone.ReadCommand.ExecuteAsync(null)", code, StringComparison.Ordinal);

        // NOT ONE WAY TO PRODUCE A FILE. Read as CODE (audit round 1): a token
        // that appears only inside a string literal is not a call, and must not
        // be able to swing an absence pin either way.
        AssertOwnsNoExportPath(code);

        // …nor the JSON that would feed one, nor a dialog of any kind: a read
        // must be able to end without the operator answering anything.
        foreach (var gone in new[]
                 {
                     "BuildJson", "ExportCloneFileAsync", "FileSaver", "DisplayAlert",
                     "DisplayActionSheet", "LastStoredFileName", "LastReadInstalledNewFile",
                 })
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);

        // ANTI-VACUITY: this really is the read press — the VM member it drives
        // exists, so the absences above are not "the scan read an empty body".
        Assert.NotNull(typeof(CloneViewModel).GetProperty(nameof(CloneViewModel.CanRead)));
        Assert.Contains("await", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// D13: THE STORE PRESS IS THE SAVE-LOCATION PICKER. The app no longer
    /// chooses where a read goes — the operator does, in the system picker, and
    /// the toolkit writes the bytes through the picker's own stream. Three
    /// things are pinned: the picker is what is opened, the name it is SEEDED
    /// with is the shared one (so two presses offer one name, not two
    /// timestamps), and a DISMISSED picker returns without saying anything.
    /// </summary>
    [Fact]
    public void TheStoreHandler_OpensTheSaveLocationPicker_AndSaysNothingWhenItIsDismissed()
    {
        var handler = HandlerBody("OnCloneStoreClicked");
        var code = CodeOnly(handler);

        // The VM owns the decision; the handler asks it, and asks again for the
        // json rather than assuming a file is there.
        Assert.Matches(@"if\s*\(\s*!\s*_clone\.CanStore\s*\)\s*return\s*;", code);
        Assert.Matches(@"if\s*\(\s*json\s+is\s+null\s*\)\s*return\s*;", code);

        // THE PICKER, seeded with the shared name.
        Assert.Matches(@"FileSaver\.Default\.SaveAsync\(\s*name\s*,", code);
        Assert.Contains("var name = ExportFileName();", code, StringComparison.Ordinal);

        // A DISMISSED PICKER SAYS NOTHING — not a notice and not an error row.
        // The toolkit has no cancel signal of its own: the dismissal arrives as
        // a failed result carrying FileSaveException, so the TYPE is the
        // discriminator, and it must be read before the failure row is written.
        //
        // D22 MOVED THIS PIN, deliberately: the branch is no longer a bare
        // `return` but a CLEAR and a return, because the press now writes a wait
        // line at the gate and a dismissal must take it back off the screen. The
        // empty message is what empties the slot — it promotes no name and takes
        // no style — so "says nothing" is still exactly what the operator gets.
        Assert.Matches(
            @"result\.Exception\s+is\s+FileSaveException\s+or\s+OperationCanceledException\s*\)\s*\{"
                + @"\s*_clone\.NoteReadFileOutcome\(\s*""""\s*,\s*null\s*,\s*isError:\s*false\s*\)\s*;"
                + @"\s*return\s*;\s*\}",
            code);
        // …read from the LITERAL-BEARING body: the failure row's own
        // `result.Exception?.Message` lives inside an interpolated string, which
        // the code view empties.
        int cancel = handler.IndexOf("FileSaveException", StringComparison.Ordinal);
        int failed = handler.IndexOf("result.Exception?.Message", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && failed > cancel,
            "the Store press writes its failure row before it has ruled out a dismissed picker");

        // …and it writes no durable file of its own: the picker's stream is the
        // only thing that puts these bytes anywhere.
        AssertWritesNothingDurable(code);

        // ANTI-VACUITY: both VM members the handler names really exist, so no
        // pin above can be satisfied by text that compiles to nothing.
        Assert.NotNull(typeof(CloneViewModel).GetProperty(nameof(CloneViewModel.CanStore)));
        Assert.NotNull(typeof(CloneViewModel).GetProperty(nameof(CloneViewModel.LastStoredFileName)));
    }

    /// <summary>
    /// D13: THE SHARE PRESS STAGES TO THE CACHE, AND ONLY THE CACHE. The share
    /// sheet needs a file on disk to hand out, and the purgeable cache is where
    /// that copy belongs — the app keeps nothing. A build that staged through
    /// app storage or Documents would be the ballooning-storage defect D13
    /// exists to end, wearing a different button.
    /// </summary>
    [Fact]
    public void TheShareHandler_StagesToTheCacheOnly_D13()
    {
        var handler = HandlerBody("OnCloneShareClicked");
        var code = CodeOnly(handler);

        Assert.Matches(@"if\s*\(\s*!\s*_clone\.CanStore\s*\)\s*return\s*;", code);
        Assert.Matches(@"if\s*\(\s*json\s+is\s+null\s*\)\s*return\s*;", code);

        // THE ONE STAGING PATH, and the sheet it feeds.
        Assert.Matches(@"Path\.Combine\(\s*FileSystem\.CacheDirectory\s*,\s*name\s*\)", code);
        Assert.Contains("Share.Default.RequestAsync", code, StringComparison.Ordinal);
        Assert.Contains("var name = ExportFileName();", code, StringComparison.Ordinal);

        // …and nowhere else. THE ROW THAT MATTERS: no durable copy is left
        // behind by a share.
        AssertWritesNothingDurable(code);
    }

    /// <summary>
    /// THE IN-FLIGHT GATE (audit round 1, kept through D13). Both presses are
    /// <c>async void</c>, so nothing in the framework stops a double-tap from
    /// opening a second picker — or a picker over a share sheet — on top of the
    /// first. Each takes the bit before it does anything that can block, and
    /// gives it back in a <c>finally</c>, so a throw can neither wedge the
    /// buttons grey nor let the next press through as if it were the first.
    /// </summary>
    [Theory]
    [InlineData("OnCloneStoreClicked")]
    [InlineData("OnCloneShareClicked")]
    public void EachExportPress_RunsOneAtATime_AndAlwaysGivesTheGateBack(string press)
    {
        var withText = HandlerBody(press);
        var code = CodeOnly(withText);

        Assert.Matches(@"if\s*\(\s*_clone\.IsExporting\s*\)\s*return\s*;", code);

        // The early return comes BEFORE anything that opens a system surface or
        // touches a path. The ORDER is read from the literal-bearing body,
        // because the name it has to precede is minted from a literal.
        int gate = withText.IndexOf("IsExporting", StringComparison.Ordinal);
        foreach (var later in new[] { "ExportFileName()", "NoteReadFileOutcome" })
            Assert.True(withText.IndexOf(later, StringComparison.Ordinal) > gate,
                $"'{later}' runs before the in-flight gate in {press}");

        // Taken once, and RETURNED from a finally — not from the happy path,
        // which the dismissed-picker row returns out of.
        Assert.Matches(@"_clone\.SetExporting\(true\)\s*;\s*try", code);
        Assert.Matches(@"finally\s*\{\s*_clone\.SetExporting\(false\)\s*;\s*\}", code);

        // ANTI-VACUITY: the VM really carries the bit and the way to set it, and
        // its gate really consults it (the unit pins are CloneViewModelTests').
        Assert.NotNull(typeof(CloneViewModel).GetProperty(nameof(CloneViewModel.IsExporting)));
        Assert.NotNull(typeof(CloneViewModel).GetMethod(nameof(CloneViewModel.SetExporting)));
    }

    /// <summary>
    /// D13, THE WHOLE POINT: <b>the Cloning card writes NOTHING durable,
    /// anywhere.</b> Owner 2026-08-30 — "we can't have a ballooning app storage
    /// — the file should not persist there". The old model kept a timestamped
    /// copy of every read in Android app storage (invisible to the operator and
    /// never cleaned up) or wrote silently into the Windows Documents folder;
    /// the atomic-replace helper that made those copies survivable
    /// (<c>WriteDurableAsync</c>) has no target left and is GONE with them.
    ///
    /// <para>This is the pin that fails if anyone re-adds a durable write to
    /// the card, by any of the ways there are to open a file.</para>
    /// </summary>
    [Fact]
    public void TheCloningCard_WritesNothingDurable_Anywhere_D13()
    {
        var code = CodeOnly(StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"))));

        // The helper is gone, and so is every call that could want it.
        Assert.DoesNotContain("WriteDurableAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", code, StringComparison.Ordinal);

        // Each export handler ON THE PAGE, individually: no durable target of
        // any kind. D17 ADDS THE CONSOLE'S TWO — its old single press was the
        // last silent Documents write in the app, and the scope of this pin is
        // now the whole file rather than one card.
        foreach (var handler in new[]
                 {
                     "OnCloneReadClicked", "OnCloneStoreClicked", "OnCloneShareClicked",
                     "OnConsoleStoreClicked", "OnConsoleShareClicked",
                 })
            AssertWritesNothingDurable(CodeOnly(HandlerBody(handler)));

        // …and FILE-WIDE, which is what D17 makes true: the operator's own
        // folder is not named anywhere in this page, by any handler.
        Assert.DoesNotContain("MyDocuments", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder", code, StringComparison.Ordinal);

        // The retired console press is GONE, not merely unbound — a dead
        // handler left behind is a durable write waiting for a Clicked.
        Assert.DoesNotContain("OnSaveShareClicked", code, StringComparison.Ordinal);

        // AppDataDirectory survives in exactly ONE place — the sweep, which
        // DELETES. A second mention is a copy coming back.
        Assert.Equal(1, CountOf(code, "AppDataDirectory"));
        Assert.Contains("AppDataDirectory", CodeOnly(HandlerBody("SweepLegacyStoredClones")),
            StringComparison.Ordinal);

        // ANTI-VACUITY: the file really does still stage copies for the share
        // sheet, so "nothing durable" is not "nothing at all".
        Assert.Contains("FileSystem.CacheDirectory", code, StringComparison.Ordinal);

        // …and the SCANNER can still see a durable write when there is one.
        // Until D17 this half read the Console's own Documents export, which
        // was a real durable write on the same page; that write is deleted, so
        // the scanner is proved against the deleted line itself rather than
        // against nothing.
        Assert.ThrowsAny<Exception>(() => AssertWritesNothingDurable(
            "var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);"));
    }

    /// <summary>
    /// D13's LEGACY SWEEP. Six stranded reads were sitting in the owner's phone
    /// app storage when D13 was written, and nothing in the app could ever show
    /// or delete them. The sweep runs ONCE an app run, DELETES ONLY, and is
    /// silent about every failure — a stranded file is not worth a card that
    /// will not open.
    ///
    /// <para><b>It never touches Windows Documents.</b> Those copies are in the
    /// operator's own folder; an app that deleted files out of a user's
    /// Documents would be doing something far worse than leaving them.</para>
    /// </summary>
    [Fact]
    public void TheLegacySweep_DeletesOnlyFromAppStorage_OnceARun_AndSwallowsEverything()
    {
        var body = HandlerBody("SweepLegacyStoredClones");
        var code = CodeOnly(body);

        // ONCE A RUN: a latch, checked and SET before the work.
        Assert.Matches(@"if\s*\(\s*_sweptLegacyClones\s*\)\s*return\s*;\s*_sweptLegacyClones\s*=\s*true\s*;",
            code);
        int latch = code.IndexOf("_sweptLegacyClones = true", StringComparison.Ordinal);
        Assert.True(latch >= 0 && latch < code.IndexOf("Directory.EnumerateFiles", StringComparison.Ordinal),
            "the sweep does its work before it latches — a throw would leave it running every visit");

        // DELETE ONLY, scoped to app storage and to the app's own name pattern.
        Assert.Matches(@"Directory\.EnumerateFiles\(\s*FileSystem\.AppDataDirectory\s*,", code);
        Assert.Contains("falcon-clone-*.falconclone.json", body, StringComparison.Ordinal);
        Assert.Contains("File.Delete(stale)", code, StringComparison.Ordinal);

        // …and it WRITES nothing, by any of the ways there are — a sweep that
        // "tidied" by rewriting would be a durable copy wearing a broom.
        foreach (var write in new[]
                 {
                     "WriteAllText", "WriteAllBytes", "FileStream", "StreamWriter",
                     "File.Move", "File.Copy", "File.Create",
                 })
            Assert.DoesNotContain(write, code, StringComparison.Ordinal);

        // NEVER the operator's own folder.
        Assert.DoesNotContain("MyDocuments", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder", code, StringComparison.Ordinal);

        // SILENT: two catches — the per-file one, so a stubborn file does not
        // stop the rest, and the outer one, so the page still opens.
        Assert.True(CountOf(code, "catch (Exception)") >= 2,
            "the sweep no longer swallows both its per-file and its whole-sweep failures");

        // …and it really is called from the page's construction, which is the
        // once-per-app-run seam D13 picked.
        var behind = CodeOnly(StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"))));
        Assert.Equal(2, CountOf(behind, "SweepLegacyStoredClones"));  // the declaration + the one call
    }

    /// <summary>
    /// The §6 outcome table AT THE CALL SITE, for BOTH D13 presses. The VM seam
    /// is unit-pinned, but only this file decides which row each real outcome
    /// takes — and the NAME PROMOTION is the argument that reads backwards if it
    /// is wrong: the save-location picker's success is the ONE thing in the app
    /// that can honestly say where a file is, so it is the only row that
    /// promotes a name. A share that promoted one would have the Write tab
    /// naming a file this app cannot find.
    /// </summary>
    [Fact]
    public void EachExportOutcome_TakesItsRowFromTheTable_D13()
    {
        // ---- Store: waiting / stored / dismissed (cleared) / failed ---------
        // D22 makes it FIVE calls where D13 had three: the WAIT line at the gate,
        // and the CLEAR that takes it back off a dismissed picker. Both go through
        // this same channel on purpose — a second channel for "in progress" is how
        // a stale wait line outlives its own outcome.
        var store = HandlerBody("OnCloneStoreClicked");
        var storeCalls = OutcomeCalls(store);
        Assert.Equal(5, storeCalls.Count);

        // THE WAIT ROW: the shared constant, no name promoted, CAPTION style.
        var waiting = Row(storeCalls, "ExportWaitText");
        Assert.Equal("null", waiting[1]);
        Assert.Equal("isError: false", waiting[2]);

        // D23 (owner 2026-08-30): SUCCESS IS SILENT — the "stored:" row is
        // GONE and the success call carries an EMPTY message with the NAME
        // (the promotion point survives the silence). Two "" rows now exist,
        // told apart by their second argument: cancel clears with null,
        // success promotes with name.
        var emptyRows = storeCalls.Select(ArgumentsOf)
            .Where(a => a[0] == "\"\"").ToList();
        Assert.Equal(2, emptyRows.Count);
        Assert.Contains(emptyRows, a => a[1] == "null");        // the cancel CLEAR
        var stored = Assert.Single(emptyRows, a => a[1] == "name"); // THE PROMOTION POINT
        Assert.Equal("isError: false", stored[2]);
        Assert.DoesNotContain(storeCalls, c => c.Contains("stored:", StringComparison.Ordinal));

        var pickerFailed = Row(storeCalls, "{result.Exception?.Message}");
        Assert.Contains("save failed:", pickerFailed[0], StringComparison.Ordinal);
        Assert.Equal("null", pickerFailed[1]);                  // no name is promoted
        Assert.Equal("isError: true", pickerFailed[2]);

        var threw = Row(storeCalls, "$\"save failed: {ex.Message}\"");
        Assert.Equal("null", threw[1]);
        Assert.Equal("isError: true", threw[2]);

        // ---- Share: waiting / shared / failed --------------------------------
        // THREE under D22, not five: a dismissed sheet is not a cancel — the
        // request simply completes — so this press has no clearing ending, and
        // both of its outcomes REPLACE the wait line.
        var share = HandlerBody("OnCloneShareClicked");
        var shareCalls = OutcomeCalls(share);
        Assert.Equal(3, shareCalls.Count);

        var shareWaiting = Row(shareCalls, "ExportWaitText");
        Assert.Equal("null", shareWaiting[1]);
        Assert.Equal("isError: false", shareWaiting[2]);

        // A SHARE PROMOTES NO NAME — and under D23 (owner 2026-08-30) it
        // SAYS nothing either: the success call is the empty message that
        // clears the wait line, with null in the name slot.
        var shared = Assert.Single(shareCalls.Select(ArgumentsOf),
            a => a[0] == "\"\"" && a[1] == "null");
        Assert.Equal("isError: false", shared[2]);
        Assert.DoesNotContain(shareCalls, c => c.Contains("shared:", StringComparison.Ordinal));

        var shareFailed = Row(shareCalls, "$\"share failed: {ex.Message}\"");
        Assert.Equal("null", shareFailed[1]);
        // NOT the old non-error: under D13 nothing else kept a copy, so a share
        // that really failed means the file did not leave the app.
        Assert.Equal("isError: true", shareFailed[2]);

        // …and the READ writes no row at all — there is no file step to report.
        Assert.DoesNotContain("NoteReadFileOutcome", CodeOnly(HandlerBody("OnCloneReadClicked")),
            StringComparison.Ordinal);

        // THE RETIRED SENTENCES. The app no longer saves to app storage or to
        // Documents, so no surface may still say it does.
        var behind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.DoesNotContain("saved to app storage", behind, StringComparison.Ordinal);
    }

    /// <summary>The OPEN handler's rows: a clean load names the file, and both
    /// failures name none — which is what leaves a rejection's identity
    /// alone.</summary>
    [Fact]
    public void EachOpenOutcome_TakesItsRowFromTheTable()
    {
        var handler = HandlerBody("OnCloneOpenClicked");
        var calls = OutcomeCalls(handler, "NoteOpenFileOutcome");

        Assert.Equal(3, calls.Count);

        var loaded = Row(calls, "loaded:");
        Assert.Equal("picked.FileName", loaded[1]);
        Assert.Equal("isError: false", loaded[2]);

        // A REJECTED file names none, which is what leaves the previously
        // loaded file's identity exactly where it was.
        var rejected = Row(calls, "rejection,");
        Assert.Equal("null", rejected[1]);
        Assert.Equal("isError: true", rejected[2]);

        var failed = Row(calls, "open failed:");
        Assert.Equal("null", failed[1]);
        Assert.Equal("isError: true", failed[2]);

        // A cancelled picker says NOTHING at all — the table's first row.
        Assert.Matches(@"if\s*\(\s*picked\s+is\s+null\s*\)\s*return\s*;", handler);
    }

    // ---- D17: the CONSOLE card's export, on the same model -------------------
    //
    // The Console lives on the SAME PAGE and its export is scanned by the SAME
    // readers, which is why its pins are here rather than in a file that would
    // have to copy them. Owner 2026-08-30: "look at the console… unify the save
    // functionality across that too". Until D17 the Console carried ONE
    // platform-split press — `{OnPlatform Android='Share…', Default='Save'}` —
    // whose Windows half wrote SILENTLY into the operator's Documents folder,
    // the last such write in the app after D13 removed the card's.

    /// <summary>
    /// D17: BOTH export presses, in the Console toolbar — "Store file…" through
    /// the system save-location picker and "Share…" through the share sheet,
    /// the card's pair, the card's words.
    ///
    /// <para>They are ACTION class (natural width) rather than the card's
    /// two-column Grid: the toolbar WRAPS, and a fixed pair could not.
    /// <c>RefreshButtonWidthGuardTests</c> owns that half.</para>
    /// </summary>
    [Theory]
    [InlineData("OnConsoleStoreClicked", "Store file…", "Choose where to save the console log")]
    [InlineData("OnConsoleShareClicked", "Share…", "Send the console log to another app")]
    public void TheConsoleToolbar_CarriesBothExportPresses_D17(
        string handler, string text, string description)
    {
        var console = ConsoleSectionMarkup();

        var press = Assert.Single(console.Descendants(), e =>
            (string?)e.Attribute("Clicked") == handler);

        Assert.Equal(text, TextOf(press));
        Assert.Equal(description, (string?)press.Attribute("SemanticProperties.Description"));

        // The house vocabulary, and no ad-hoc size (I-1) — the toolbar's own
        // rule, which the retired button also kept.
        Assert.Equal("{StaticResource Segment}", (string?)press.Attribute("Style"));
        Assert.Equal("44", (string?)press.Attribute("MinimumHeightRequest"));
        Assert.Null(press.Attribute("WidthRequest"));
        Assert.Null(press.Attribute("FontSize"));

        // NO GATE BINDING, deliberately: the button it replaces had none — the
        // buffer is never empty in practice and an unexportable console is
        // worse than an empty file. The in-flight gate is the view's, and the
        // press pins below are what prove it.
        Assert.Null(press.Attribute("IsEnabled"));

        // …and the handler really exists, so a rename cannot leave the markup
        // pointing at nothing.
        var codeBehind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.Contains("void " + handler, codeBehind, StringComparison.Ordinal);

        // ANTI-VACUITY: this really is the Console section — the toolbar's
        // other presses are here too — and BOTH new presses are, side by side.
        Assert.Contains(console.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnCopyClicked");
        var pair = console.Descendants()
            .Where(e => (string?)e.Attribute("Clicked") is "OnConsoleStoreClicked" or "OnConsoleShareClicked")
            .ToList();
        Assert.Equal(2, pair.Count);
    }

    /// <summary>
    /// D17: THE PLATFORM SPLIT IS GONE, from the markup and from the code. One
    /// button whose TEXT changed per platform hid two different behaviours
    /// behind one press; the pair is the same on both, and the handler that
    /// carried the Windows Documents write no longer exists to be re-bound.
    /// </summary>
    [Fact]
    public void TheConsoleExport_HasNoPlatformSplit_AndTheRetiredPressIsGone_D17()
    {
        var console = ConsoleSectionMarkup();

        // No Console button's Text is an OnPlatform switch any more…
        foreach (var button in console.Descendants().Where(e => e.Name.LocalName == "Button"))
            Assert.DoesNotContain("OnPlatform", TextOf(button) ?? "", StringComparison.Ordinal);

        // …and neither the retired handler nor the retired description survives
        // anywhere in the page, markup or code-behind.
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath));
        var behind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        foreach (var retired in new[]
                 {
                     "OnSaveShareClicked",
                     "Export the full console log as a text file",
                     "Default='Save'",
                 })
        {
            Assert.DoesNotContain(retired, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(retired, behind, StringComparison.Ordinal);
        }

        // ANTI-VACUITY: the readers see the page, and the Console really does
        // still hold an OnPlatform-free toolbar of buttons.
        Assert.NotEmpty(console.Descendants().Where(e => e.Name.LocalName == "Button"));
        Assert.Contains("OnConsoleStoreClicked", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// D17: the Console's STORE press goes through the SAVE-LOCATION PICKER —
    /// the card's Store, line for line, minus the name promotion the Console
    /// has nothing to promote onto. The dismissal discriminator is read BEFORE
    /// the failure row, or a cancelled picker would report a failure that never
    /// happened.
    /// </summary>
    [Fact]
    public void TheConsoleStorePress_GoesThroughTheSaveLocationPicker_D17()
    {
        var handler = HandlerBody("OnConsoleStoreClicked");
        var code = CodeOnly(handler);

        Assert.Matches(@"FileSaver\.Default\.SaveAsync\(\s*name\s*,", code);
        Assert.Contains("var name = ConsoleExportFileName();", code, StringComparison.Ordinal);

        // A DISMISSED PICKER SAYS NOTHING, read before the failure row is
        // written. D22 moved this pin the same way it moved the card's: the
        // branch CLEARS the wait line and returns, and an empty message empties
        // the slot (both labels), so the silence is unchanged.
        Assert.Matches(
            @"result\.Exception\s+is\s+FileSaveException\s+or\s+OperationCanceledException\s*\)\s*\{"
                + @"\s*ShowExportNotice\(\s*""""\s*,\s*isError:\s*false\s*\)\s*;\s*return\s*;\s*\}",
            code);
        int cancel = handler.IndexOf("FileSaveException", StringComparison.Ordinal);
        int failed = handler.IndexOf("result.Exception?.Message", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && failed > cancel,
            "the Console Store press writes its failure row before it has ruled out a dismissed picker");

        // The SOURCE is the FULL-SESSION log (D19), not the display's 500-line
        // store: a leftover filter must never silently narrow a bench report,
        // and neither must the display's scrollback budget.
        Assert.Contains("_console.GetSessionLogText()", code, StringComparison.Ordinal);

        // …and it writes no durable file of its own: the picker's stream is the
        // only thing that puts these bytes anywhere.
        AssertWritesNothingDurable(code);
    }

    /// <summary>
    /// D17: the Console's SHARE press stages to the CACHE, and only the cache —
    /// the same purgeable copy the card's Share uses. This is what the Android
    /// half of the retired button already did; the change is that the WINDOWS
    /// half no longer writes into Documents instead.
    /// </summary>
    [Fact]
    public void TheConsoleSharePress_StagesToTheCacheOnly_D17()
    {
        var handler = HandlerBody("OnConsoleShareClicked");
        var code = CodeOnly(handler);

        Assert.Matches(@"Path\.Combine\(\s*FileSystem\.CacheDirectory\s*,\s*name\s*\)", code);
        Assert.Contains("Share.Default.RequestAsync", code, StringComparison.Ordinal);
        Assert.Contains("var name = ConsoleExportFileName();", code, StringComparison.Ordinal);
        Assert.Contains("_console.GetSessionLogText()", code, StringComparison.Ordinal);  // D19

        AssertWritesNothingDurable(code);

        // ONE NAME for both presses, and it is the buffer's own convention —
        // untouched by D17, so a stored log and a shared log are the same file.
        // (Read from the file, not through HandlerBody: the name helper is an
        // expression-bodied member and has no braced body to walk.)
        var behind = StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs")));
        Assert.Matches(
            @"string ConsoleExportFileName\(\)\s*=>\s*\$""falcon-console-\{DateTime\.Now:yyyyMMdd-HHmmss\}\.txt"";",
            behind);
        Assert.Equal(1, CountOf(behind, "falcon-console-"));
    }

    /// <summary>
    /// D17: THE IN-FLIGHT GATE, the card's rule in the Console's scope. Both
    /// presses are <c>async void</c>, so nothing in the framework stops a
    /// double-tap from opening a second picker — or a picker over a share sheet
    /// — on top of the first. The bit is a VIEW field, not the card's VM bit:
    /// the Console section binds <c>ConsoleViewModel</c>, and reaching into
    /// <c>CloneViewModel</c> from here would tie two unrelated cards together.
    /// </summary>
    [Theory]
    [InlineData("OnConsoleStoreClicked")]
    [InlineData("OnConsoleShareClicked")]
    public void EachConsoleExportPress_RunsOneAtATime_AndAlwaysGivesTheGateBack(string press)
    {
        var withText = HandlerBody(press);
        var code = CodeOnly(withText);

        Assert.Matches(@"if\s*\(\s*_consoleExporting\s*\)\s*return\s*;", code);

        // The early return comes BEFORE anything that opens a system surface or
        // touches a path.
        int gate = withText.IndexOf("_consoleExporting", StringComparison.Ordinal);
        foreach (var later in new[] { "ConsoleExportFileName()", "ShowExportNotice" })
            Assert.True(withText.IndexOf(later, StringComparison.Ordinal) > gate,
                $"'{later}' runs before the in-flight gate in {press}");

        // Taken once, and RETURNED from a finally — not from the happy path,
        // which the dismissed-picker row returns out of.
        Assert.Matches(@"_consoleExporting\s*=\s*true\s*;\s*try", code);
        Assert.Matches(@"finally\s*\{\s*_consoleExporting\s*=\s*false\s*;\s*\}", code);

        // ANTI-VACUITY: the field really is declared, so the pins above are not
        // reading a name that compiles to nothing.
        var behind = CodeOnly(StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"))));
        Assert.Contains("private bool _consoleExporting;", behind, StringComparison.Ordinal);
    }

    /// <summary>
    /// D17: the Console's outcome table, AT THE CALL SITE — the card's rows,
    /// with the card's silences. <c>stored:</c> names where the picker put the
    /// file; a DISMISSED picker writes no row at all; both failures take the
    /// ERROR style. The retired rows (<c>saved: {path}</c> from the Documents
    /// write, and the catch-all <c>export failed:</c>) are gone with the press
    /// that produced them.
    /// </summary>
    [Fact]
    public void EachConsoleExportOutcome_TakesItsRowFromTheTable_D17()
    {
        // ---- Store: waiting / stored / dismissed (cleared) / failed ---------
        // D22, mirrored from the card: FIVE calls — the WAIT line at the gate and
        // the CLEAR that takes it off a dismissed picker, through the SAME slot.
        var store = HandlerBody("OnConsoleStoreClicked");
        var storeCalls = OutcomeCalls(store, "ShowExportNotice");
        Assert.Equal(5, storeCalls.Count);

        var waiting = ConsoleRow(storeCalls, "ExportWaitText");
        Assert.Equal("isError: false", waiting[1]);

        var cleared = ConsoleRow(storeCalls, "\"\"");
        Assert.Equal("isError: false", cleared[1]);

        var stored = ConsoleRow(storeCalls, "$\"stored: {result.FilePath}\"");
        Assert.Equal("isError: false", stored[1]);

        var pickerFailed = ConsoleRow(storeCalls, "{result.Exception?.Message}");
        Assert.Contains("save failed:", pickerFailed[0], StringComparison.Ordinal);
        Assert.Equal("isError: true", pickerFailed[1]);

        var threw = ConsoleRow(storeCalls, "$\"save failed: {ex.Message}\"");
        Assert.Equal("isError: true", threw[1]);

        // ---- Share: waiting / shared / failed --------------------------------
        // THREE, for the card's reason: a dismissed sheet completes, so there is
        // no clearing ending here and both outcomes replace the wait line.
        var share = HandlerBody("OnConsoleShareClicked");
        var shareCalls = OutcomeCalls(share, "ShowExportNotice");
        Assert.Equal(3, shareCalls.Count);

        var shareWaiting = ConsoleRow(shareCalls, "ExportWaitText");
        Assert.Equal("isError: false", shareWaiting[1]);

        var shared = ConsoleRow(shareCalls, "$\"shared: {name}\"");
        Assert.Equal("isError: false", shared[1]);

        var shareFailed = ConsoleRow(shareCalls, "$\"share failed: {ex.Message}\"");
        Assert.Equal("isError: true", shareFailed[1]);

        // THE RETIRED SENTENCES. Nothing is saved into Documents any more, so
        // no surface may still say it is — and the old catch-all row, which
        // could not tell a failed save from a failed share, is gone too.
        var behind = File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs"));
        Assert.DoesNotContain("saved: {path}", behind, StringComparison.Ordinal);
        Assert.DoesNotContain("export failed:", behind, StringComparison.Ordinal);
    }

    /// <summary>
    /// D17: the Console's notice slot is the card's — ONE SLOT, TWO LABELS, so
    /// a clean export never appears in the error style and a failure never
    /// appears as an ordinary caption. The Console's are set from code-behind
    /// (there is no VM state behind them), so the complementary visibility is
    /// pinned where it lives: in <c>ShowExportNotice</c>.
    /// </summary>
    [Fact]
    public void TheConsoleNoticeSlot_IsTwoLabels_CaptionAndError_D17()
    {
        var console = ConsoleSectionMarkup();

        var caption = Assert.Single(console.Descendants(), e =>
            (string?)e.Attribute(X + "Name") == "ExportNotice");
        var error = Assert.Single(console.Descendants(), e =>
            (string?)e.Attribute(X + "Name") == "ExportError");

        Assert.Equal("{StaticResource Caption}", (string?)caption.Attribute("Style"));
        Assert.Equal("{StaticResource ErrorCaption}", (string?)error.Attribute("Style"));

        // Both start hidden — the slot says nothing until a press does.
        Assert.Equal("False", (string?)caption.Attribute("IsVisible"));
        Assert.Equal("False", (string?)error.Attribute("IsVisible"));

        // …and they are ONE slot, not two stacked notices: the same parent, and
        // exactly one of them is ever visible.
        Assert.Same(caption.Parent, error.Parent);

        // D22 ADDED THE EMPTY CASE and nothing else: the complementary
        // visibility is unchanged, and an empty message now hides BOTH labels —
        // the same `Length > 0` rule CloneViewModel applies on the card's side,
        // so the two slots empty on one condition rather than two.
        var show = CodeOnly(HandlerBody("ShowExportNotice"));
        Assert.Matches(@"ExportNotice\.IsVisible\s*=\s*message\.Length\s*>\s*0\s*&&\s*!\s*isError\s*;", show);
        Assert.Matches(@"ExportError\.IsVisible\s*=\s*message\.Length\s*>\s*0\s*&&\s*isError\s*;", show);
    }

    /// <summary>
    /// D22 (manager finding, 2026-08-30 solo bench run): THE EXPORT WAIT IS
    /// VISIBLE. A save or share picker can end up BEHIND the app window — that
    /// run lost minutes to a hidden "This file is in use" retry dialog holding
    /// the gate — and the in-flight bit greys both presses for exactly as long.
    /// The wait line is written the moment the gate is TAKEN, and BEFORE the
    /// call that can block behind the window: a line written after it would
    /// never render while the dialog was up, which is the whole failure.
    /// </summary>
    [Theory]
    [InlineData("OnCloneStoreClicked", "_clone.SetExporting(true)", "FileSaver.Default.SaveAsync")]
    [InlineData("OnCloneShareClicked", "_clone.SetExporting(true)", "Share.Default.RequestAsync")]
    [InlineData("OnConsoleStoreClicked", "_consoleExporting = true", "FileSaver.Default.SaveAsync")]
    [InlineData("OnConsoleShareClicked", "_consoleExporting = true", "Share.Default.RequestAsync")]
    public void EachExportPress_ShowsTheWaitLine_BetweenTheGateAndTheBlockingCall_D22(
        string press, string gateTaken, string blocks)
    {
        var body = HandlerBody(press);

        int gate = body.IndexOf(gateTaken, StringComparison.Ordinal);
        int wait = body.IndexOf("ExportWaitText", StringComparison.Ordinal);
        int block = body.IndexOf(blocks, StringComparison.Ordinal);

        Assert.True(gate >= 0, $"{press} no longer takes the in-flight gate");
        Assert.True(block > 0, $"{press} no longer opens the system surface this pin is about");
        Assert.True(wait > gate, $"{press} shows the wait line before it takes the gate");
        Assert.True(wait < block, $"{press} shows the wait line only AFTER the call that can block");
    }

    /// <summary>
    /// D22: ONE STRING, BOTH CARDS, CAPTION STYLE. The same press must not be
    /// called two things in one app — the D17 rule that made the two export
    /// pairs identical, applied to the line they now show while they wait. It is
    /// never an error: waiting is not a failure. And it carries NO RADIO TOKEN
    /// (I-3/R13): the picker is the operator's own system dialog.
    /// </summary>
    [Fact]
    public void TheExportWaitLine_IsOneSharedCaptionString_D22()
    {
        var behind = StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs")));

        // DECLARED ONCE, byte-for-byte — the manifest row
        // (plan-clone-pane-cleanup §7).
        Assert.Matches(@"const string ExportWaitText = ""Waiting on the save dialog…"";", behind);
        Assert.Equal(1, CountOf(behind, "\"Waiting on the save dialog…\""));

        // …and USED by all four presses, so neither card carries a private copy.
        foreach (var press in new[]
                 {
                     "OnCloneStoreClicked", "OnCloneShareClicked",
                     "OnConsoleStoreClicked", "OnConsoleShareClicked",
                 })
            Assert.Contains("ExportWaitText", HandlerBody(press), StringComparison.Ordinal);

        // NEVER the error style, on either card.
        Assert.DoesNotContain("ExportWaitText, null, isError: true", behind, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportWaitText, isError: true", behind, StringComparison.Ordinal);

        // …and it never leaks into a campaign report: the wait is a VIEW state,
        // and the summary belongs to the wire.
        Assert.DoesNotContain("Waiting on the save dialog",
            File.ReadAllText(Path.Combine(FindRepoRoot(),
                "src", "Falcon.App.Core", "Cloning", "CloneService.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// D21 (owner 2026-08-30: "the buttons are vertically spaced too close
    /// together"): every child of the CONSOLE toolbar carries a BOTTOM margin.
    /// A wrapping FlexLayout gives a wrapped row no spacing of its own, so with
    /// right-margins alone the rows touched on a narrow phone — which is where
    /// these nine controls reflow onto three or four rows.
    ///
    /// <para>CONSOLE CARD ONLY. The page's ONLY margins are this toolbar's, so a
    /// change that leaked onto the Cloning card or the Settings tab fails the
    /// count below rather than passing unnoticed.</para>
    /// </summary>
    [Fact]
    public void TheConsoleToolbarChildren_AllCarryTheBottomMargin_AndNothingElseOnThePageDoes_D21()
    {
        var page = XDocument.Load(Path.Combine(FindRepoRoot(), PageRelativePath));
        var toolbar = Assert.Single(page.Descendants(), e => e.Name.LocalName == "FlexLayout");
        var children = toolbar.Elements().ToList();

        // Pause · Copy · Store file… · Share… · filter · "paused" · Enable input
        // · command · Send.
        Assert.Equal(9, children.Count);
        Assert.All(children, child =>
            Assert.EndsWith(",4", (string?)child.Attribute("Margin") ?? "", StringComparison.Ordinal));   // D21 symmetric

        // The eight CONTROLS keep the right margin they already had; the one
        // inline word ("paused") gains the bottom margin and nothing else.
        Assert.Equal(8, children.Count(c => (string?)c.Attribute("Margin") == "0,4,8,4"));   // symmetric: WinUI FlexLayout clips an unaccounted bottom margin (owner 2026-08-30, "tops clipped")
        Assert.Equal(1, children.Count(c => (string?)c.Attribute("Margin") == "0,4,0,4"));

        // NOWHERE ELSE ON THE PAGE: the toolbar's nine are the page's only
        // margins, so no other surface took the change.
        Assert.Equal(9, page.Descendants().Count(e => e.Attribute("Margin") is not null));
    }

    // ---- D18: the CONSOLE's gated raw-command input, and the selectable log --
    //
    // Same file, same reason as D17's region: these controls live in the SAME
    // Console section, read by the SAME `ConsoleSectionMarkup` reader. Owner
    // 2026-08-30: "what will it take to add an input to the console so we can
    // send arbitrary commands (gated behind an enable button)? Also, we should
    // be able to highlight and copy console output text" → "do it".

    /// <summary>
    /// D18: THE THREE NEW CONTROLS, each with the binding that makes it work.
    /// Keyed by the binding rather than by the text, because the text is what a
    /// wording change moves and the binding is what a refactor breaks.
    ///
    /// <para>The Entry's <c>IsEnabled="{Binding InputEnabled}"</c> is the pin
    /// the gate hangs on: without it the box is live before the operator has
    /// armed anything, and the VM's <c>CanSend</c> would be the only thing left
    /// between a stray tap and the wire.</para>
    /// </summary>
    [Fact]
    public void TheConsoleToolbar_CarriesTheGatedInputRow_D18()
    {
        var console = ConsoleSectionMarkup();

        // (1) The ENABLE toggle — the Pause/Resume idiom: one command, and a
        // DataTrigger that flips the word.
        var toggle = Assert.Single(console.Descendants(), e =>
            (string?)e.Attribute("Command") == "{Binding ToggleInputCommand}");
        Assert.Equal("Enable input", TextOf(toggle));
        Assert.Equal("Arm or disarm the console command input",
            (string?)toggle.Attribute("SemanticProperties.Description"));
        Assert.Equal("{StaticResource Segment}", (string?)toggle.Attribute("Style"));
        var flip = Assert.Single(toggle.Descendants().Where(e => e.Name.LocalName == "DataTrigger"));
        Assert.Equal("{Binding InputEnabled}", (string?)flip.Attribute("Binding"));
        Assert.Equal("True", (string?)flip.Attribute("Value"));
        Assert.Contains(flip.Elements(), s =>
            (string?)s.Attribute("Property") == "Text" && (string?)s.Attribute("Value") == "Disable input");

        // (2) The COMMAND BOX — two-way text, GATED by the toggle, and the
        // return key runs the same command the Send button does.
        var entry = Assert.Single(console.Descendants(), e =>
            (string?)e.Attribute("Placeholder") == "command");
        Assert.Equal("Entry", entry.Name.LocalName);
        Assert.Equal("{Binding InputText, Mode=TwoWay}", (string?)entry.Attribute("Text"));
        Assert.Equal("{Binding InputEnabled}", (string?)entry.Attribute("IsEnabled"));
        Assert.Equal("{Binding SendCommand}", (string?)entry.Attribute("ReturnCommand"));
        Assert.Equal("Command to send to the radio",
            (string?)entry.Attribute("SemanticProperties.Description"));
        // A phone keyboard that capitalises or "corrects" sends a DIFFERENT
        // command than the one that was typed. Plain is the no-suggestions one.
        Assert.Equal("Plain", (string?)entry.Attribute("Keyboard"));

        // (3) SEND — the command, and the full three-term gate on IsEnabled.
        var send = Assert.Single(console.Descendants(), e =>
            e.Name.LocalName == "Button" && (string?)e.Attribute("Command") == "{Binding SendCommand}");
        Assert.Equal("Send", TextOf(send));
        Assert.Equal("{Binding CanSend}", (string?)send.Attribute("IsEnabled"));
        Assert.Equal("Send the typed command to the radio",
            (string?)send.Attribute("SemanticProperties.Description"));
        Assert.Equal("{StaticResource Segment}", (string?)send.Attribute("Style"));

        // NO LOGIC IN THE VIEW: the input row binds and nothing else — not one
        // Clicked handler among the three, so the gate cannot be re-decided in
        // code-behind.
        foreach (var control in new[] { toggle, entry, send })
            Assert.Null(control.Attribute("Clicked"));

        // ANTI-VACUITY: this really is the Console toolbar (D17's presses are
        // beside them), and the three new controls share its ONE wrapping
        // FlexLayout rather than sitting in a fixed row that cannot reflow.
        Assert.Contains(console.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnConsoleStoreClicked");
        Assert.Same(toggle.Parent, entry.Parent);
        Assert.Same(toggle.Parent, send.Parent);
        Assert.Equal("FlexLayout", toggle.Parent!.Name.LocalName);
    }

    /// <summary>
    /// D18: THE VM CARRIES THE GATE, and the markup's bindings resolve to it.
    /// A binding to a property that does not exist resolves to NOTHING in MAUI
    /// — silently — so the four names the row binds are checked against the
    /// type itself.
    /// </summary>
    [Fact]
    public void TheGatedInput_BindsRealViewModelMembers_AndStartsDisarmed_D18()
    {
        Assert.NotNull(typeof(ConsoleViewModel).GetProperty(nameof(ConsoleViewModel.InputEnabled)));
        Assert.NotNull(typeof(ConsoleViewModel).GetProperty(nameof(ConsoleViewModel.InputText)));
        Assert.NotNull(typeof(ConsoleViewModel).GetProperty(nameof(ConsoleViewModel.CanSend)));
        Assert.NotNull(typeof(ConsoleViewModel).GetProperty("SendCommand"));
        Assert.NotNull(typeof(ConsoleViewModel).GetProperty("ToggleInputCommand"));

        // Both command bindings really are commands (a same-named plain
        // property would bind and never fire).
        Assert.True(typeof(System.Windows.Input.ICommand).IsAssignableFrom(
            typeof(ConsoleViewModel).GetProperty("SendCommand")!.PropertyType));
        Assert.True(typeof(System.Windows.Input.ICommand).IsAssignableFrom(
            typeof(ConsoleViewModel).GetProperty("ToggleInputCommand")!.PropertyType));

        // Anti-vacuity: the reader can MISS.
        Assert.Null(typeof(ConsoleViewModel).GetProperty("InputEnabledThatIsNotThere"));
    }

    /// <summary>
    /// D18(b): the log line's TEXT cell is a <c>ConsoleLogLabel</c> — the
    /// selection SCOPING, expressed structurally. Native text selection is a
    /// handler mapping, and the mapping's only scope is this type: if the
    /// template goes back to a plain <c>Label</c> the mapping stops applying
    /// and the log silently stops being selectable, with nothing else in the
    /// app to notice.
    ///
    /// <para>The other half — that the mapping is scoped and does not make the
    /// whole app selectable — is pinned in
    /// <c>ConsoleSelectableLogGuardTests</c>.</para>
    /// </summary>
    [Fact]
    public void TheConsoleLogLine_UsesTheSelectableLabelType_AndKeepsItsStyling_D18()
    {
        var console = ConsoleSectionMarkup();

        var template = Assert.Single(console.Descendants()
            .Where(e => e.Name.LocalName == "DataTemplate"));
        var text = Assert.Single(template.Descendants()
            .Where(e => e.Name.LocalName == "ConsoleLogLabel"));

        Assert.Equal("{Binding Text}", (string?)text.Attribute("Text"));
        // STYLING UNCHANGED — the subclass adds selection, not a new look.
        Assert.Equal("Consolas", (string?)text.Attribute("FontFamily"));
        Assert.Equal("12", (string?)text.Attribute("FontSize"));
        Assert.Equal("CharacterWrap", (string?)text.Attribute("LineBreakMode"));
        Assert.Equal("2", (string?)text.Attribute("Grid.Column"));

        // SCOPE, in the markup: exactly ONE ConsoleLogLabel on this page — the
        // timestamp and badge cells stay plain labels, and no control outside
        // the log template borrowed the type.
        var page = XDocument.Load(Path.Combine(FindRepoRoot(), PageRelativePath));
        Assert.Single(page.Descendants().Where(e => e.Name.LocalName == "ConsoleLogLabel"));
        Assert.Equal(2, template.Descendants().Count(e => e.Name.LocalName == "Label"));

        // The whole-log COPY button is untouched by D18 (deliberately: the
        // selection is for a LINE, Copy is for the log).
        Assert.Contains(console.Descendants(), e =>
            (string?)e.Attribute("Clicked") == "OnCopyClicked");
    }

    /// <summary>The Console section's markup root. Fails loudly if it is
    /// renamed or removed.</summary>
    private static XElement ConsoleSectionMarkup()
    {
        var page = XDocument.Load(Path.Combine(FindRepoRoot(), PageRelativePath));
        var section = page.Descendants().SingleOrDefault(e =>
            (string?)e.Attribute(X + "Name") == "ConsoleSection");
        Assert.True(section is not null,
            "the Console section (x:Name=\"ConsoleSection\") is gone from " + PageRelativePath);
        return section!;
    }

    /// <summary>The one <c>ShowExportNotice</c> call carrying
    /// <paramref name="needle"/>, as its ARGUMENT LIST. The Console's rows take
    /// TWO arguments — message and style — where the card's take three, because
    /// the Console has no file identity to promote a stored name onto.</summary>
    private static IReadOnlyList<string> ConsoleRow(IReadOnlyList<string> calls, string needle)
    {
        var call = Assert.Single(calls, c => c.Contains(needle, StringComparison.Ordinal));
        var args = ArgumentsOf(call);
        Assert.Equal(2, args.Count);
        return args;
    }

    // ---- Source scanning -----------------------------------------------------

    /// <summary>One method's body, from its signature to its matching brace,
    /// with COMMENTS stripped and string literals intact. Preprocessor lines
    /// are left alone: the Android branch is text in this file and is exactly
    /// what the outcome pins are reading.
    ///
    /// <para>D12: the export is a shared <c>async Task</c> helper now, not only
    /// the <c>void</c> Clicked handlers — the outcome pins follow the code they
    /// pin, so this reads either return type.</para></summary>
    private static string HandlerBody(string method)
    {
        var code = StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs")));

        int at = code.IndexOf("void " + method + "(", StringComparison.Ordinal);
        if (at < 0) at = code.IndexOf("Task " + method + "(", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the code-behind no longer declares {method}");
        int open = code.IndexOf('{', at);
        Assert.True(open >= 0, $"{method} has no body");

        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }

        throw new InvalidOperationException($"{method}'s body is unterminated — the stripper is wrong");
    }

    /// <summary>Every <c>Note*FileOutcome(…)</c> call in a body, each as its own
    /// call text. WHITESPACE-TOLERANT between the name and the paren (audit
    /// round 2): <c>NoteReadFileOutcome (</c> is legal C#, and a scanner that
    /// missed it would report "no such call" as if the pin had passed.</summary>
    private static IReadOnlyList<string> OutcomeCalls(string body, string method = "NoteReadFileOutcome")
    {
        var calls = new List<string>();
        foreach (Match match in Regex.Matches(body, Regex.Escape(method) + @"\s*\("))
        {
            int end = body.IndexOf(");", match.Index, StringComparison.Ordinal);
            Assert.True(end > match.Index, "an outcome call in the code-behind is unterminated");
            calls.Add(body[match.Index..end]);
        }

        Assert.NotEmpty(calls);
        return calls;
    }

    /// <summary>The one call carrying <paramref name="needle"/>, as its ARGUMENT
    /// LIST. Every row's message interpolates the name it is about, so a
    /// substring search over the call text cannot tell the message from the
    /// argument — this is what makes the storedName pins mean something.</summary>
    private static IReadOnlyList<string> Row(IReadOnlyList<string> calls, string needle)
    {
        var call = Assert.Single(calls, c => c.Contains(needle, StringComparison.Ordinal));
        var args = ArgumentsOf(call);
        Assert.Equal(3, args.Count);
        return args;
    }

    /// <summary>A call's top-level arguments, trimmed. Commas inside string
    /// literals (every message here is interpolated) and inside nested
    /// brackets do not separate arguments.</summary>
    private static IReadOnlyList<string> ArgumentsOf(string call)
    {
        int open = call.IndexOf('(');
        Assert.True(open >= 0, "an outcome call has no argument list: " + call);

        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = open + 1; i < call.Length; i++)
        {
            char c = call[i];
            if (c is '"' or '\'')
            {
                // Audit round 3 (MINOR): a VERBATIM string escapes its quote by
                // doubling and treats backslash as a literal — regular-string
                // escaping over @"C:\" consumed the closing quote and swallowed
                // the arguments after it.
                bool verbatim = c == '"' && i >= 1
                    && (call[i - 1] == '@' || (call[i - 1] == '$' && i >= 2 && call[i - 2] == '@'));
                current.Append(c);
                for (i++; i < call.Length; i++)
                {
                    current.Append(call[i]);
                    if (!verbatim && call[i] == '\\' && i + 1 < call.Length) { current.Append(call[++i]); continue; }
                    if (call[i] == c)
                    {
                        if (verbatim && i + 1 < call.Length && call[i + 1] == c) { current.Append(call[++i]); continue; }
                        break;
                    }
                }
                continue;
            }
            if (c is '(' or '[' or '{') { depth++; current.Append(c); continue; }
            if (c is ')' or ']' or '}')
            {
                if (c == ')' && depth == 0) break;
                depth--;
                current.Append(c);
                continue;
            }
            if (c == ',' && depth == 0) { args.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0) args.Add(current.ToString().Trim());
        return args;
    }

    /// <summary>
    /// A body with its STRING LITERALS EMPTIED — quotes kept, contents gone
    /// (audit round 1). The file's standing doctrine keeps literals INTACT
    /// because the operator wording is what the outcome pins read; the
    /// CODE-SHAPE pins want the opposite, because a token that appears only
    /// inside a string is not a call and must not be able to satisfy — or
    /// defeat — a pin about what the code does.
    ///
    /// <para>Interpolation holes go with the literal they are inside, which is
    /// what the shape pins want; a hole containing a quoted string of its own
    /// would confuse this scanner, and there is none in the scanned file (the
    /// control test below is what would notice one arriving).</para>
    /// </summary>
    private static string CodeOnly(string code)
    {
        var kept = new System.Text.StringBuilder(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];

            // A VERBATIM literal: backslash is a literal character, and the
            // quote is escaped by doubling.
            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"')
            {
                kept.Append("@\"\"");
                for (i += 2; i < code.Length; i++)
                {
                    if (code[i] != '"') continue;
                    if (i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                    break;
                }
                continue;
            }

            if (c is '"' or '\'')
            {
                kept.Append(c).Append(c);
                for (i++; i < code.Length; i++)
                {
                    if (code[i] == '\\' && i + 1 < code.Length) { i++; continue; }
                    if (code[i] == c) break;
                }
                continue;
            }

            kept.Append(c);
        }

        return kept.ToString();
    }

    /// <summary>A body that owns NO export path of ANY kind — D13's read
    /// handler is the one this is written for: not one way to write, move,
    /// delete, share or NAME a file, and no name to give one. Takes a body
    /// ALREADY read as CODE, except the timestamped name, which is a literal and
    /// so is checked against the whole handler text by its callers.</summary>
    private static void AssertOwnsNoExportPath(string code)
    {
        foreach (var exportOnly in new[]
                 {
                     "WriteAllTextAsync", "WriteAllBytesAsync", "WriteAllText(", "FileStream",
                     "StreamWriter", "File.Move", "File.Delete", "Share.", "NoteReadFileOutcome",
                     "FileSystem.", "Path.Combine", "falcon-clone-", "ExportFileName",
                 })
            Assert.DoesNotContain(exportOnly, code, StringComparison.Ordinal);
    }

    /// <summary>A body that writes NOTHING DURABLE (D13): no app-storage
    /// target, no Documents target, no atomic-replace helper — every way there
    /// is to open a file for writing is checked against the two directories the
    /// old model used. Staging into <c>FileSystem.CacheDirectory</c> is
    /// deliberately NOT forbidden: that is the share sheet's copy, and the OS
    /// may purge it.</summary>
    private static void AssertWritesNothingDurable(string code)
    {
        foreach (var durable in new[]
                 {
                     "AppDataDirectory", "MyDocuments", "SpecialFolder",
                     "WriteDurableAsync", "File.Move", "File.Copy",
                 })
            Assert.DoesNotContain(durable, code, StringComparison.Ordinal);
    }

    private static int CountOf(string body, string needle)
    {
        int n = 0, at = 0;
        while ((at = body.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>Strip C# comments, keeping every string literal — the operator
    /// wording IS the thing being pinned. A scanner that ignored quoting would
    /// call the <c>//</c> in a saved path a comment; one that stripped literals
    /// would have nothing left to read.</summary>
    private static string StripComments(string code)
    {
        var kept = new System.Text.StringBuilder(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                kept.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"')
            {
                kept.Append(c).Append(code[++i]);
                for (i++; i < code.Length; i++)
                {
                    kept.Append(code[i]);
                    if (code[i] != '"') continue;
                    if (i + 1 < code.Length && code[i + 1] == '"') { kept.Append(code[++i]); continue; }
                    break;
                }
                continue;
            }
            if (c is '"' or '\'')
            {
                kept.Append(c);
                for (i++; i < code.Length; i++)
                {
                    kept.Append(code[i]);
                    if (code[i] == '\\' && i + 1 < code.Length) { kept.Append(code[++i]); continue; }
                    if (code[i] == c) break;
                }
                continue;
            }

            kept.Append(c);
        }

        return kept.ToString();
    }

    [Fact]
    public void TheSourceStripper_RemovesCommentsAndKeepsStrings_ItsOwnControl()
    {
        // The stripper's anti-vacuity control: without this, every pin above
        // could be passing on text no scanner actually understood.
        var stripped = StripComments(
            "var a = \"keep // me\";  // drop me\n/* drop */ var b = 'x'; var c = @\"a\"\"b\";\n#if ANDROID\n");

        Assert.Contains("\"keep // me\"", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("drop me", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("/* drop */", stripped, StringComparison.Ordinal);
        Assert.Contains("@\"a\"\"b\"", stripped, StringComparison.Ordinal);
        Assert.Contains("#if ANDROID", stripped, StringComparison.Ordinal);

        // …and it really does see the file this suite scans. D13 put the
        // timestamped name in ONE place — `ExportFileName`, which both presses
        // seed from — so the literal appears exactly once in the whole
        // code-behind, and a second one is a second naming rule.
        var behind = StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), PageRelativePath + ".cs")));
        Assert.Equal(1, CountOf(behind, "falcon-clone-{DateTime"));
        Assert.Contains("string ExportFileName()", behind, StringComparison.Ordinal);

        // THE CALL SCANNER'S OWN CONTROL (audit round 2). The space before the
        // paren is legal C#, and a scanner that missed it would find no calls
        // at all — which reads exactly like a pin that passed.
        var spaced = OutcomeCalls(
            "_clone.NoteReadFileOutcome (\n    $\"stored: {result.FilePath}, {name}\",\n"
            + "    name, isError: false);",
            "NoteReadFileOutcome");
        var args = ArgumentsOf(Assert.Single(spaced));

        // …and the argument splitter tells the MESSAGE from the ARGUMENT: the
        // message interpolates and carries a comma of its own.
        Assert.Equal(3, args.Count);
        Assert.Contains("stored:", args[0], StringComparison.Ordinal);
        Assert.Equal("name", args[1]);
        Assert.Equal("isError: false", args[2]);

        // A comma INSIDE a literal is not an argument separator, and a nested
        // call's commas are not either.
        Assert.Equal(["\"a, b\""], ArgumentsOf("f(\"a, b\")"));
        Assert.Equal(["g(1, 2)", "x"], ArgumentsOf("f(g(1, 2), x)"));

        // Audit round 3 (MINOR): a verbatim literal ending in a backslash must
        // not swallow its closing quote — and its doubled-quote escape is not
        // the end of the string.
        Assert.Equal(["@\"C:\\\"", "x"], ArgumentsOf("f(@\"C:\\\", x)"));
        Assert.Equal(["@\"a\"\"b\"", "y"], ArgumentsOf("f(@\"a\"\"b\", y)"));

        // THE LITERAL STRIPPER'S OWN CONTROL (audit round 1). The code-shape
        // pins must not be able to see a token that is only ever text — nor
        // lose the call that really is there beside it.
        var codeOnly = CodeOnly(
            "var a = \"Share.Default\"; Share.Default.RequestAsync(); var b = @\"C:\\x\"; var c = 'q';");

        Assert.Equal(1, CountOf(codeOnly, "Share.Default"));
        Assert.DoesNotContain("C:", codeOnly, StringComparison.Ordinal);
        Assert.Contains("var b = @\"\";", codeOnly, StringComparison.Ordinal);
        Assert.Contains("var c = '';", codeOnly, StringComparison.Ordinal);

        // …and it really does see the scanned file: the Store press's own
        // literals are gone from the code view while its calls are not.
        var press = CodeOnly(HandlerBody("OnCloneStoreClicked"));
        Assert.DoesNotContain("stored: ", press, StringComparison.Ordinal);
        Assert.Contains("NoteReadFileOutcome", press, StringComparison.Ordinal);
    }

    // ---- Shared shape assertions ---------------------------------------------

    /// <summary>ONE notice slot rendered as TWO labels on complementary
    /// computed visibilities — a clean save or load must never appear in the
    /// error style, and an error must never appear as an ordinary
    /// caption.</summary>
    private static void AssertTwoLabelNoticeSlot(
        XElement tab, string text, string showsNotice, string showsError)
    {
        var labels = tab.Descendants()
            .Where(e => (string?)e.Attribute("Text") == "{Binding " + text + "}")
            .ToList();

        Assert.Equal(2, labels.Count);
        var caption = Assert.Single(labels, e =>
            (string?)e.Attribute("Style") == "{StaticResource Caption}");
        var error = Assert.Single(labels, e =>
            (string?)e.Attribute("Style") == "{StaticResource ErrorCaption}");

        Assert.Equal("{Binding " + showsNotice + "}", (string?)caption.Attribute("IsVisible"));
        Assert.Equal("{Binding " + showsError + "}", (string?)error.Attribute("IsVisible"));
    }

    /// <summary>A report is a BindableLayout of Caption rows plus its own Clear,
    /// both inside ONE container the report's own emptiness hides — so an empty
    /// report leaves no orphan button behind.</summary>
    private static void AssertReportBlock(
        XElement tab, string lines, string has, string clearCommand, string clearDescription)
    {
        var layout = Assert.Single(tab.Descendants(), e =>
            (string?)e.Attribute(BindableItemsSource) == "{Binding " + lines + "}");

        var template = Assert.Single(layout.Descendants(), e => e.Name.LocalName == "DataTemplate");
        Assert.Contains(template.Descendants(), e =>
            e.Name.LocalName == "Label"
            && (string?)e.Attribute("Style") == "{StaticResource Caption}");

        var clear = Assert.Single(tab.Descendants(), e =>
            (string?)e.Attribute("Command") == clearCommand);
        Assert.Equal("Clear", TextOf(clear));
        Assert.Equal("{StaticResource Segment}", (string?)clear.Attribute("Style"));
        Assert.Equal("End", (string?)clear.Attribute("HorizontalOptions"));
        Assert.Equal(clearDescription, (string?)clear.Attribute("SemanticProperties.Description"));

        // ONE container, gated on the report having anything in it, holding
        // BOTH the rows and the button.
        var container = layout.Ancestors().First(a =>
            (string?)a.Attribute("IsVisible") == "{Binding " + has + "}");
        Assert.Contains(container.Descendants(), e => e == clear);
    }

    // ---- Parsing -------------------------------------------------------------

    /// <summary>An UNPREFIXED XAML attribute is in no XML namespace, attached
    /// property or not — so this is the literal name, not a MAUI-namespaced one.</summary>
    private static readonly XName BindableItemsSource = XName.Get("BindableLayout.ItemsSource");

    /// <summary>A control's Text, from the attribute OR the property-element
    /// form — a guard that read only attributes would pass on markup that had
    /// moved the value into a child element.</summary>
    private static string? TextOf(XElement element)
        => (string?)element.Attribute("Text")
            ?? element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == element.Name.LocalName + ".Text")?.Value.Trim();

    /// <summary>One of the two tab selectors, found by the command it sends
    /// rather than by position.</summary>
    private static XElement TabButton(XElement card, string tab)
        => Assert.Single(card.Descendants(), e =>
            e.Name.LocalName == "Button"
            && (string?)e.Attribute("Command") == "{Binding Open" + tab + "TabCommand}");

    /// <summary>The Value a selector's own DataTrigger fires on. Exactly one
    /// trigger, on the tab-state property, applying the accent treatment.</summary>
    private static string? TabTriggerValue(XElement button)
    {
        var trigger = Assert.Single(button.Descendants(), e => e.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding IsWriteTabOpen}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("Button", (string?)trigger.Attribute("TargetType"));

        var setters = trigger.Elements()
            .Where(e => e.Name.LocalName == "Setter")
            .Select(e => ((string?)e.Attribute("Property"), (string?)e.Attribute("Value")))
            .ToList();
        Assert.Contains(("BackgroundColor", "{StaticResource AccentColor}"), setters);
        Assert.Contains(("TextColor", "{StaticResource OnAccentColor}"), setters);

        return (string?)trigger.Attribute("Value");
    }

    /// <summary>A tab BODY, by name. Named elements, not positions: a body
    /// found by "the second VerticalStackLayout" would follow any reflow.</summary>
    private static XElement TabBody(XElement card, string name)
    {
        var body = card.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        Assert.True(body is not null, "the Cloning card no longer has a tab body named " + name);
        return body!;
    }

    /// <summary>A tab body's own visibility trigger: (the tab-state value it
    /// fires on, the visibility it then applies).</summary>
    private static (string?, string?) BodyTrigger(XElement body)
    {
        var trigger = Assert.Single(
            body.Elements().Where(e => e.Name.LocalName.EndsWith(".Triggers", StringComparison.Ordinal))
                .SelectMany(e => e.Elements()),
            e => e.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding IsWriteTabOpen}", (string?)trigger.Attribute("Binding"));

        var setter = Assert.Single(trigger.Elements(), e => e.Name.LocalName == "Setter");
        Assert.Equal("IsVisible", (string?)setter.Attribute("Property"));
        return ((string?)trigger.Attribute("Value"), (string?)setter.Attribute("Value"));
    }

    /// <summary>The BindableLayout the rows hang off. Its ItemsSource is
    /// <c>SelfRows</c> — the table's whole existence in one attribute.</summary>
    private static XElement SelfRowsLayout(XElement card)
    {
        return Assert.Single(card.Descendants(), e =>
            (string?)e.Attribute(BindableItemsSource) == "{Binding SelfRows}");
    }

    /// <summary>Where a control's visibility actually lives: on the control, or
    /// on the one-control layout row wrapped around it.</summary>
    private static XElement VisibilityOwner(XElement element)
        => element.Attribute("IsVisible") is not null ? element : element.Parent!;

    /// <summary>The Cloning card's Border. Fails loudly if the card is renamed
    /// or removed — a silently-passing guard is worse than none.</summary>
    private static XElement CloningCard()
    {
        var page = XDocument.Load(Path.Combine(FindRepoRoot(), PageRelativePath));
        var card = page.Descendants().SingleOrDefault(e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute(X + "Name") == "CloningSection");
        Assert.True(card is not null, "the Cloning card (Border x:Name=\"CloningSection\") is gone from " + PageRelativePath);
        return card!;
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
