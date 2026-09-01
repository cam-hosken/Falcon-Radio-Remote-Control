using System.Globalization;
using System.Xml.Linq;

using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// UI tweaks round 10 (§3/§4) — the DISPLAY CONSTITUTION's vocabulary, pinned
/// against <c>App.xaml</c>, and the phone/desktop width ARITHMETIC that
/// justifies the numbers in it.
///
/// <para><b>Why the resources need a pin at all.</b> A named width or a shared
/// style is only a convention until something enforces it. Delete
/// <c>SegmentWidthWide</c> and every view that referenced it fails at RUNTIME
/// (a missing StaticResource is not a compile error in a Release XAML build
/// the way a missing symbol is), on whichever pane the operator opens first.
/// The keys the constitution is written in must exist, and their values must
/// be the ones the constitution reasoned about.</para>
///
/// <para><b>Why the arithmetic is a pin and not a comment.</b> The §3 widths
/// are LAYOUT-PROVISIONAL (plan §1): they were chosen against a stated phone
/// budget — originally an ASSUMED 360-dp phone giving ≈336 dp of content,
/// corrected by clone round 12 §9 A5 to the bench phone's MEASURED ≈448 dp
/// (see <c>PhoneContent</c> for the ruling and the evidence) — and an
/// implementation-time MEASURED overflow is allowed to change one. Invariant 8
/// says such a change is recorded, never silent, and the arithmetic moves with
/// it. The BUDGET itself may move only on evidence, and A5 is the one time it
/// has. So the gate here is always the INEQUALITY, read
/// against the value actually in App.xaml — never a hard-coded 90 or 132. Set
/// <c>SegmentWidthWide</c> to 200 and the row budgets fail; set it to 88 and
/// they pass, because 88 genuinely fits. That is the contract: the widths may
/// move, the rows must still fit.</para>
///
/// <para><b>Why XML and not text.</b> The house reason
/// (RefreshButtonWidthGuardTests / ChevronGeometryGuardTests): a XAML property
/// can be an attribute or a property element, and only a parser sees both. An
/// XML comment is not an element, so commented-out markup is invisible for
/// free — the evasion that defeated earlier regex guards.</para>
///
/// <para><b>ACCEPTED LIMITATION</b>, the same one every scan here carries: a
/// value supplied indirectly — merged dictionary, code-behind, a platform
/// override — is invisible. Accidents (deletion, retyping, commenting out) are
/// caught; adversarial construction is backstopped by review and the bench
/// width checks.</para>
/// </summary>
public class StyleVocabularyGuardTests
{
    // ---- The stated budget constants (plan §3 / §4) --------------------------
    // These are the plan's own numbers, not measurements. They are the
    // ASSUMPTIONS each inequality is evaluated under, written down so a later
    // reader can see exactly what was traded against what.

    /// <summary>
    /// Phone content budget. Round 10 §3 stated **336** — a 360-dp phone less
    /// card padding 10×2 and page padding — as its measurement CONFIG, i.e. a
    /// conservative assumption, not a measurement of the device this app is
    /// built for.
    ///
    /// <para><b>CLONE ROUND 12 §9 A5 corrects it to the measured device
    /// reality: 448.</b> Two independent sources, both dated 2026-08-19:</para>
    /// <list type="bullet">
    ///   <item>OWNER, on the bench phone, ruling on the clipped port label:
    ///     <i>"there is plenty of room to widen all of the modem type and port
    ///     type buttons together. there are only 2 columns used and it's
    ///     currently only taking half the screen width."</i> Two 136-dp columns
    ///     plus the 6-dp gap is 278 dp; the observation establishes SUBSTANTIAL
    ///     spare room beyond 336 (278 is ~83% of the old budget yet reads as
    ///     roughly half the screen) — it supports the correction but does not
    ///     by itself derive the 448 constant; the AH1 measurement below is the
    ///     quantitative source (audit r2 F2).</item>
    ///   <item>The AH1 no-clip measurement already recorded in docs/ui.md and
    ///     quoted in ChevronGeometryGuardTests: <b>448 dp on the bench
    ///     phone</b> (437 dp of desktop card content at a 500-dp window).
    ///     That number has been in the repo since round 4; the 336 assumption
    ///     was simply never reconciled with it.</item>
    /// </list>
    /// <para>The correction LOOSENS every inequality below, which is exactly
    /// why it is recorded here with its provenance rather than quietly edited:
    /// these budgets are the gate on the provisional widths, and a budget
    /// raised without evidence would disarm them. 448 is the evidence.</para>
    /// </summary>
    private const double PhoneContent = 448;

    /// <summary>
    /// Desktop content budget. ROUND 11 §9 MIGRATES this: it used to be the
    /// literal 560, SettingsPage's <c>MaximumWidthRequest</c> — a cap that no
    /// longer exists, on a page that was never the ALE pane anyway. The desktop
    /// budget is now DERIVED from the window that actually constrains it:
    /// <c>WindowFixedWidth</c> less the page padding (32) and the scrollbar
    /// (16).
    ///
    /// <para>Derived rather than copied ON PURPOSE. §9's whole argument for 540
    /// over 480 is an inequality between this budget and the station row's
    /// requirement — so if the window constant moves and this number does not
    /// follow it, the pin below would pass while the app clipped. Read from the
    /// source, the inequality re-evaluates for free (invariant 7).</para>
    /// </summary>
    private const double DesktopChrome = 48;

    private static double DesktopContent => WindowFixedWidthFromSource() - DesktopChrome;

    /// <summary>Gap between the buttons of a wide choice row, and between the
    /// row label and the first button (plan §3's equations).</summary>
    private const double WideRowGap = 6;

    /// <summary>ColumnSpacing of both station grids (plan §4, literal).</summary>
    private const double StationGridSpacing = 8;

    /// <summary>ROUND 15 §17: the station grids' ONE fixed field column is the
    /// ASSOC SELF one, and its width is <c>ValueWidthWide</c> — READ from
    /// App.xaml rather than written down here, which is what makes the markup's
    /// literal 96 a cross-pin to the key instead of a second copy of a number.
    /// The Kind (44) and Chan-grp (64) constants that stood here are DELETED
    /// with the columns they measured, so nothing in this file can pin the old
    /// shape by accident.</summary>
    private static double AssocSelfColumn => Widths()["ValueWidthWide"];

    /// <summary>Fixed columns on a station row: Assoc self + the three action
    /// buttons, plus one spacing per gap across the five columns.</summary>
    private static double StationFixedColumns
        => AssocSelfColumn + (3 * Widths()["SegmentWidth"]) + (4 * StationGridSpacing);

    /// <summary>The stated minimum readable station-name width (plan §4).</summary>
    private const double MinimumNameWidth = 120;

    /// <summary>ROUND 13 §4 A2's STATED allowance for Send Sync's natural
    /// width. The button is ACTION class, so it pins no width and there is no
    /// number to read from App.xaml — the plan states 120 dp as the assumption
    /// the chip's fixed width was chosen under, and it is written here so a
    /// later reader sees exactly what was traded against what. The FINAL
    /// no-clip verdict is the T3 Android device pass (constitution §3.3: fit
    /// claims for Android-rendered text are never desktop text
    /// measurements).</summary>
    private const double SendSyncAllowance = 120;

    /// <summary>ColumnSpacing of the HOP sync row (§4 A2, literal).</summary>
    private const double SyncRowSpacing = 12;

    // Row-label allowances (plan §3, per row, stated constants).
    private const double KindLabelAllowance = 48;         // "Kind"
    private const double TypeLabelAllowance = 48;         // "Type"
    private const double PortLabelAllowance = 36;         // "Port" (round 11 §3; was "Mode")
    private const double PresetStateLabelAllowance = 92;  // "Preset state"

    // ---- Resource existence (§3's vocabulary) --------------------------------

    /// <summary>Every width key the constitution is written in EXISTS and
    /// parses as a number. Existence first, because a missing key is the
    /// failure mode that only shows up on the pane nobody opened.</summary>
    [Fact]
    public void EveryWidthKeyTheConstitutionNames_Exists()
    {
        var widths = Widths();

        foreach (var key in new[]
        {
            // Pre-existing, and §3/§4 depend on them.
            "SegmentWidth", "CellWidthNet", "CellWidthId", "CellWidthType",
            "CellWidthValue", "CellWidthFreq",
            // Round 10 (§3).
            "SegmentWidthWide", "SegmentWidthXWide",
            // Round 11 (§3).
            "SegmentWidthPort",
            "ValueWidthNarrow", "ValueWidthStd", "ValueWidthWide", "ValueWidthXWide",
            "NumericEntryWidth",
            // ROUND 13 §4 A2 (item 10).
            "SyncChipWidth",
        })
            Assert.True(widths.ContainsKey(key), $"App.xaml has no x:Double resource '{key}'");
    }

    /// <summary>The FIXED width values — the ones §3 states outright rather
    /// than derives from a budget. <c>SegmentWidth</c> 72 is cross-pinned here
    /// because §4's station grids cannot reference a resource inside a
    /// <c>ColumnDefinitions</c> string and must spell 72 literally: this is
    /// what stops the literal and the key drifting apart. ROUND 15 §17 puts
    /// <c>ValueWidthWide</c> 96 in exactly the same position — it is the
    /// Assoc-self column of both station grids, spelled literally there for the
    /// same reason — so the two keys are cross-pinned side by side.</summary>
    [Fact]
    public void TheFixedWidthValues_AreTheOnesTheConstitutionStates()
    {
        var widths = Widths();

        Assert.Equal(72, widths["SegmentWidth"]);          // §4 cross-pin
        Assert.Equal(64, widths["ValueWidthNarrow"]);
        Assert.Equal(72, widths["ValueWidthStd"]);
        Assert.Equal(96, widths["ValueWidthWide"]);        // §17 cross-pin
        Assert.Equal(110, widths["ValueWidthXWide"]);
        Assert.Equal(90, widths["NumericEntryWidth"]);
    }

    /// <summary>The LAYOUT-PROVISIONAL choice widths are positive numbers.
    /// Their VALUES are not asserted here on purpose (invariant 7 — a measured
    /// overflow may move them); the row budgets below are their gate.</summary>
    [Fact]
    public void TheProvisionalChoiceWidths_ExistAndArePositive()
    {
        var widths = Widths();

        Assert.True(widths["SegmentWidthWide"] > 0);
        Assert.True(widths["SegmentWidthXWide"] > 0);
        Assert.True(widths["SegmentWidthPort"] > 0);
        // ROUND 11 §3: the PORT width is the card's widest choice class — its
        // buttons carry "Remote port (async)" and its type row carries
        // "FSK narrow". If it were not wider than SegmentWidthWide there would
        // be no reason for it to exist, and the 2-per-row split it bought
        // would be arbitrary.
        Assert.True(widths["SegmentWidthPort"] > widths["SegmentWidthWide"],
            "SegmentWidthPort is the WIDEST choice width (§3: the port words); it cannot be "
            + "narrower than SegmentWidthWide");
    }

    /// <summary>ROUND 11 §3 retires <c>SegmentWidthXWide</c>'s only consumer
    /// without deleting the key — round 11's manifests are CLOSED and do not
    /// list it for retirement. Pinned so the state is DELIBERATE rather than an
    /// oversight: the key survives, and nothing in the app references it.
    /// <para>Anti-vacuity: the scan that finds no XWide reference does find the
    /// Port ones, so "no matches" cannot be a broken reader.</para></summary>
    [Fact]
    public void SegmentWidthXWide_SurvivesAsVocabulary_WithNoConsumerLeft()
    {
        var xwide = ReferencesTo("SegmentWidthXWide");
        var port = ReferencesTo("SegmentWidthPort");

        Assert.Empty(xwide);
        Assert.NotEmpty(port);
        Assert.True(Widths().ContainsKey("SegmentWidthXWide"),
            "the key itself is NOT deleted — §3's manifest does not list it, and invariant 3 "
            + "says an unlisted item stays untouched");
    }

    /// <summary>Every style key §3 names exists, including the ones it only
    /// EXTENDS. A promoted style that is not there is a silent reversion to
    /// per-view fonts.</summary>
    [Fact]
    public void EveryStyleKeyTheConstitutionNames_Exists()
    {
        var styles = Styles();

        foreach (var key in new[]
        {
            // Pre-existing tiers §3 assigns sites to.
            "Segment", "Caption", "CardHeading", "SubHeading", "SecondaryLabel",
            "CellHeading", "CellValue", "ValueDisplay", "ValueDisplayText",
            // Round 10 (§3), new.
            "SpinnerDigit", "StatusText", "EmptyPaneHint",
            // CLONE ROUND 12 §6 F7, new.
            "TitleBarFlat",
        })
            Assert.True(styles.ContainsKey(key), $"App.xaml has no Style with x:Key '{key}'");
    }

    /// <summary>
    /// CLONE ROUND 12 §6 F7 — the title bar's About button, OWNER-RULED
    /// low-profile: "like the tab-bar items", NOT a Segment.
    ///
    /// <para><b>Why a style needs its own gate here.</b> The tab strip is
    /// SHELL-RENDERED on both heads, so there is no existing XAML style to
    /// inherit from and nothing that would fail if this one drifted back
    /// toward Segment — it would simply start looking like a control panel
    /// again, on the one surface that is visible from every page. So the
    /// setters are asserted as VALUES.</para>
    ///
    /// <para><b>The ABSENCE of width setters is half the contract.</b>
    /// "Natural width" is what makes a flat title-bar button read as a link
    /// rather than a chip; Segment's <c>MinimumWidthRequest</c> is exactly the
    /// setter that would take it away, and it is the one a copy-paste from
    /// Segment brings with it.</para>
    /// </summary>
    [Fact]
    public void TitleBarFlat_IsTransparentBorderless_TabFamilyText_AndPinsNoWidth()
    {
        var setters = Styles()["TitleBarFlat"];

        // Transparent and borderless — the low-profile ruling, literally.
        Assert.Equal("Transparent", setters["BackgroundColor"]);
        Assert.Equal("Transparent", setters["BorderColor"]);
        Assert.Equal("0", setters["BorderWidth"]);
        Assert.Equal("0", setters["CornerRadius"]);

        // Tab-family TEXT: plain weight (Segment's is Bold) in the app's
        // on-surface colour, per theme.
        Assert.Equal("None", setters["FontAttributes"]);
        var color = setters["TextColor"];
        Assert.Contains("OnSurfaceLight", color, StringComparison.Ordinal);
        Assert.Contains("OnSurfaceDark", color, StringComparison.Ordinal);

        // NO width, in any of its spellings.
        foreach (var width in new[] { "WidthRequest", "MinimumWidthRequest", "MaximumWidthRequest" })
            Assert.False(setters.ContainsKey(width),
                $"TitleBarFlat sets {width} — §6 F7's ruling is NATURAL width; the words size the button");

        // Anti-vacuity, and the contrast that gives the ruling meaning: the
        // style it deliberately is NOT does pin a minimum width, and is Bold.
        var segment = Styles()["Segment"];
        Assert.True(segment.ContainsKey("MinimumWidthRequest"));
        Assert.Equal("Bold", segment["FontAttributes"]);
    }

    // ---- The new/changed styles' contents ------------------------------------

    /// <summary>§3: <c>ValueDisplayText</c> gains Consolas IN-STYLE. This is
    /// the round's one acknowledged APPEARANCE CHANGE — the value displays
    /// that never had the inline copy (the modem displays on the SSB, ALE and
    /// HOP panes, the modem-presets baud display, the SSB-settings AVS
    /// display) start rendering monospaced. Pinned so a later "clean-up" that
    /// drops the setter cannot silently take it back while the inline copies
    /// are being deleted around it in P2/P3.</summary>
    [Fact]
    public void ValueDisplayText_CarriesConsolasInTheStyle()
    {
        var setters = Styles()["ValueDisplayText"];

        Assert.Equal("Consolas", setters["FontFamily"]);
        // The tier's existing identity is unchanged.
        Assert.Equal("Bold", setters["FontAttributes"]);
        Assert.Equal("Center", setters["HorizontalTextAlignment"]);
    }

    /// <summary>§3: <c>SpinnerDigit</c> — 30 / Bold / Consolas / centered. The
    /// 30 pt is a ledger-recorded exemption from the type scale, so it is
    /// asserted as a VALUE: a style that exists but has drifted to 18 would
    /// shrink every digit readout in the app.</summary>
    [Fact]
    public void SpinnerDigit_Is30BoldConsolasCentered()
    {
        var setters = Styles()["SpinnerDigit"];

        Assert.Equal("30", setters["FontSize"]);
        Assert.Equal("Bold", setters["FontAttributes"]);
        Assert.Equal("Consolas", setters["FontFamily"]);
        Assert.Equal("Center", setters["HorizontalTextAlignment"]);
    }

    /// <summary>§3: <c>StatusText</c> — Bold at the DEFAULT size. The absence
    /// of a FontSize is half the contract: status lines sit inline with body
    /// text, and a size here would make them a third heading tier.</summary>
    [Fact]
    public void StatusText_IsBoldAtTheDefaultSize()
    {
        var setters = Styles()["StatusText"];

        Assert.Equal("Bold", setters["FontAttributes"]);
        Assert.False(setters.ContainsKey("FontSize"),
            "StatusText is Bold at the DEFAULT size (§3) — a FontSize here makes it a heading tier");
    }

    /// <summary>§3: <c>EmptyPaneHint</c> — 18 and secondary-colored. Both
    /// halves matter: the size is what makes it read as the pane's content,
    /// the secondary color is what stops it reading as data.</summary>
    [Fact]
    public void EmptyPaneHint_Is18AndSecondaryColoured()
    {
        var setters = Styles()["EmptyPaneHint"];

        Assert.Equal("18", setters["FontSize"]);
        var color = setters["TextColor"];
        Assert.Contains("SecondaryTextLight", color);
        Assert.Contains("SecondaryTextDark", color);
    }

    // ---- §3: the five wide choice rows' phone budgets -------------------------
    // Each test states its equation in the plan's own terms and evaluates it
    // against the value actually in App.xaml. The GATE is the inequality.

    [Fact]
    public void AleKindRow_FitsThePhoneBudget()
    {
        // Label "Type" LEFT: the buttons at SegmentWidthWide, with a gap
        // between each pair. ROUND 15 E-1 added a FOURTH segment (Member), so
        // the arithmetic is 4 × wide + 3 × gap — and the COUNT is read from the
        // markup, not assumed here, so a fifth segment re-evaluates the budget
        // instead of quietly overflowing it.
        double wide = Widths()["SegmentWidthWide"];
        int segments = AleKindSegmentCount();
        Assert.Equal(4, segments);

        double row = (segments * wide) + ((segments - 1) * WideRowGap);
        double available = PhoneContent - KindLabelAllowance - WideRowGap;

        Assert.True(row <= available,
            Budget($"ALE Type ({segments} × SegmentWidthWide + {segments - 1} gaps)", row, available, wide));
    }

    /// <summary>How many segments the ALE card's kind row actually renders —
    /// READ from the VM's own builder, which is what the markup binds.</summary>
    private static int AleKindSegmentCount()
    {
        using var harness = new AleKindHarness();
        return harness.Vm.KindChoices.Count;
    }

    /// <summary>A bare card, built only to count its kind segments.</summary>
    private sealed class AleKindHarness : SessionTestBase
    {
        public AleProgrammingViewModel Vm { get; }

        public AleKindHarness()
            => Vm = new AleProgrammingViewModel(
                new Falcon.App.Core.Surfaces.AleSurface(Radio), Session, new FakeConfirmationPrompt());
    }

    [Fact]
    public void ModemTypeRows_FitThePhoneBudget()
    {
        // ROUND 11 §3: label "Type" LEFT, THREE rows of TWO at the PORT width.
        // Round 10's 3+3 at SegmentWidthWide is replaced — the type words moved
        // to the wider class with the port row, and two of those per line is
        // what the same budget allows.
        double port = Widths()["SegmentWidthPort"];
        double row = (2 * port) + WideRowGap;
        double available = PhoneContent - TypeLabelAllowance - WideRowGap;

        Assert.True(row <= available, Budget("Modem Type, per row of two", row, available, port));
    }

    [Fact]
    public void InterleaveRow_FitsTheFullCardWidth()
    {
        // The ONE owner-chosen label-ABOVE row: the buttons get the whole card
        // width, no label allowance — rows of 3 and 2 at SegmentWidthWide.
        double wide = Widths()["SegmentWidthWide"];
        double row = (3 * wide) + (2 * WideRowGap);

        Assert.True(row <= PhoneContent,
            Budget("Interleave (label ABOVE, full card width)", row, PhoneContent, wide));
    }

    [Fact]
    public void HopSyncRow_FitsThePhoneBudget()
    {
        // ROUND 13 §4 A2 (item 10, owner 2026-08-19). The sync chip is the
        // app's first chip with a FIXED width — sized for the longest message
        // so Send Sync beside it stops moving (rule K). The row is
        // chip + spacing + Send Sync, and Send Sync is ACTION class (it pins
        // no width, so there is nothing to read for it): the plan's stated
        // 120-dp allowance stands in for its natural width.
        //
        // Evaluated against the MEASURED PhoneContent (448 — the round-12 A5
        // correction, provenance at the constant), NOT the retired 336.
        // The GATE is the inequality: widen the chip past the ceiling and this
        // fails, exactly as the other row budgets behave.
        double chip = Widths()["SyncChipWidth"];
        double available = PhoneContent - SendSyncAllowance - SyncRowSpacing;

        Assert.True(chip <= available,
            Budget("HOP sync row (SyncChipWidth + Send Sync allowance + spacing)",
                chip, available, chip));
    }

    [Fact]
    public void PortRow_FitsThePhoneBudget()
    {
        // ROUND 11 §3: label "Port" LEFT (the buttons name the port and put the
        // signalling in parentheses): rows of 2 and 1 at SegmentWidthPort — the
        // widest row is the pair.
        double port = Widths()["SegmentWidthPort"];
        double row = (2 * port) + WideRowGap;
        double available = PhoneContent - PortLabelAllowance - WideRowGap;

        Assert.True(row <= available,
            Budget("Port (2 × SegmentWidthPort + 1 gap)", row, available, port));
    }

    // ---- CLONE ROUND 12 §9 A5 / §14 O2 — RESOLVED, owner ruling 2026-08-19 --
    //
    // HISTORY, because the shape of these two tests changed with the ruling.
    // The owner reported "Remote port (async)" looking wrong on the bench
    // phone. §14 O2's criterion made that measurable, and at SegmentWidthPort
    // 136 the verdict was CLIPPED. But A5's prescribed remedy — "widen it" —
    // appeared impossible against the STATED 336-dp phone budget, which capped
    // the tighter of the two rows sharing this width at 138 dp. P3 therefore
    // shipped two deliberately-failing COLLISION-RECORDING tests rather than
    // half-fixing with an arbitrary 2 dp.
    //
    // They did their job: the collision went to the owner, who ruled that the
    // BUDGET was the wrong number, not the layout — "there is plenty of room
    // to widen all of the modem type and port type buttons together. there are
    // only 2 columns used and it's currently only taking half the screen
    // width." PhoneContent is corrected to the measured 448 (its own
    // provenance is at the constant) and the width to 184. The collision tests
    // RETIRE with the collision; what replaces them is the ordinary assertion
    // this always should have been able to be — the width fits its longest
    // label.
    //
    // PLATFORM HONESTY — the audit's point, and the reason the margins below
    // are headroom rather than a fit-to-the-dp number: the measurement is
    // Segoe UI Bold + WinUI padding, and THE CLIPPED SURFACE IS ANDROID. The
    // Segment style pins no font family and no font size, so each head
    // resolves its own metrics. The owner's on-device observation is the
    // PRIMARY evidence; the Windows number is corroboration that bounds the
    // problem, not a cross-platform truth.

    /// <summary>Measured glyph advance of "Remote port (async)" — the widest
    /// string SegmentWidthPort exists for (Segoe UI Bold 14 px, GDI+
    /// MeasureString with GenericTypographic, bench machine 2026-08-19). Its
    /// siblings measured 113.57 ("Data port (async)") and 106.04 ("Data port
    /// (sync)"); the modem TYPE row's widest, "FSK narrow", measured 74.94.
    ///
    /// <para>Recorded as a constant rather than measured in-test because this
    /// host has no font stack: a net10.0 xunit process cannot ask MAUI what it
    /// would render, and adding a Windows-only drawing dependency to a
    /// portable test project to answer a layout question would be worse than
    /// writing the number down with its provenance — which is the "stated
    /// constants + inequality" idiom the rest of this file is built on.</para>
    /// </summary>
    private const double PortLabelTextWidth = 133.85;

    /// <summary>Horizontal padding a Segment button adds around its text on
    /// WINDOWS: WinUI's default ButtonPadding is 11,5,11,6, MAUI's handler does
    /// not override it, and the Segment style sets no Padding of its own.</summary>
    private const double WindowsButtonPadding = 22;

    /// <summary>The ANDROID allowance, deliberately more generous: Material's
    /// default button padding is 16 dp a side. Named separately from the
    /// Windows figure because the whole point of §9 A5's resolution is that
    /// the two heads do not share metrics, and one "padding" constant would
    /// hide that.</summary>
    private const double AndroidButtonPadding = 32;

    /// <summary>§9 A5, RESOLVED: the port width FITS its longest label, with
    /// headroom on BOTH heads. This replaces the collision-recording test that
    /// asserted the opposite at 136 — the block above says why that test
    /// existed and what retired it.
    ///
    /// <para>Headroom, not a tight fit, is the contract: the Windows metric
    /// does not transfer to the Android surface that actually clipped, so the
    /// assertion demands a MARGIN over the measurement rather than mere
    /// clearance. If a label grows or the width drifts back toward the
    /// measurement, this fails while there is still room to react.</para></summary>
    [Fact]
    public void ThePortLabel_FitsItsButton_WithHeadroomOnBothHeads_A5()
    {
        double port = Widths()["SegmentWidthPort"];

        double windowsNeeds = PortLabelTextWidth + WindowsButtonPadding;
        double androidNeeds = PortLabelTextWidth + AndroidButtonPadding;

        Assert.True(windowsNeeds <= port,
            $"\"Remote port (async)\" needs {windowsNeeds} dp with Windows padding but "
            + $"SegmentWidthPort is {port} dp — this is the §9 A5 clip, back again.");
        Assert.True(androidNeeds <= port,
            $"\"Remote port (async)\" needs {androidNeeds} dp with Android's wider button "
            + $"padding but SegmentWidthPort is {port} dp — and ANDROID is the surface the "
            + "owner saw clipping.");

        // The MARGIN, not just the fit. 10% over the more demanding of the two
        // is the stated floor: the Segment style pins no font family or size,
        // so a head whose font renders ~10% wider than the measured Segoe UI
        // Bold must still clear.
        Assert.True(port >= androidNeeds * 1.10,
            $"SegmentWidthPort {port} dp clears the Android requirement ({androidNeeds} dp) by "
            + $"only {port - androidNeeds} dp. §9 A5 was resolved with deliberate HEADROOM "
            + "because the Windows measurement does not transfer — sizing to the dp reopens it.");
    }

    /// <summary>The other half of the resolution: the widened width still fits
    /// the CORRECTED phone budget. This replaces the collision test that
    /// recorded 184-vs-138 as impossible — impossible only against the stale
    /// 336, which the owner's ruling retired.
    ///
    /// <para>Kept as its own test beside the row budgets below (which evaluate
    /// the same inequality from the row's side) because THIS one names the
    /// resolution: it is the test a reader lands on when they ask "why is the
    /// port width 184, and where did 336 go".</para></summary>
    [Fact]
    public void TheWidenedPortWidth_FitsTheCorrectedPhoneBudget_A5()
    {
        double port = Widths()["SegmentWidthPort"];

        // The TIGHTER of the two rows sharing this width is Type (allowance 48
        // against Port's 36) — pinned as an ordering further below.
        double ceiling = (PhoneContent - TypeLabelAllowance - WideRowGap - WideRowGap) / 2;

        Assert.True(port <= ceiling,
            $"SegmentWidthPort {port} dp exceeds the {ceiling} dp the corrected {PhoneContent} dp "
            + "phone budget allows two-per-row on the Type row. Widening past the budget is what "
            + "invariant 8 forbids — the budget moved once, on measured evidence; it does not "
            + "move again to make a width fit.");

        // Anti-vacuity: the width really did widen PAST the old ceiling, so
        // this records a resolution rather than passing as it always would.
        Assert.True(port > 138,
            "the §9 A5 resolution widened SegmentWidthPort past the 138 dp the STALE 336 dp "
            + "budget allowed; a value at or under 138 means the widening was reverted");
    }

    [Fact]
    public void TheTypeRow_IsTheTIGHTER_OfTheTwoPortWidthRows()
    {
        // The two rows share ONE width, so only the tighter of them decides
        // whether 136 is legal — and it is the TYPE row, because "Type" claims
        // a 48-dp label allowance against "Port"'s 36. Stated so a later reader
        // who widens the class cannot check the roomier row and stop.
        Assert.True(TypeLabelAllowance > PortLabelAllowance,
            "if \"Port\" ever claims the larger allowance, the row that gates SegmentWidthPort "
            + "changes and both budget tests above need re-reading");
    }

    [Fact]
    public void PresetStateRow_FitsThePhoneBudget()
    {
        // Label LEFT, the longest of the five labels: one row of two.
        double wide = Widths()["SegmentWidthWide"];
        double row = (2 * wide) + WideRowGap;
        double available = PhoneContent - PresetStateLabelAllowance - WideRowGap;

        Assert.True(row <= available,
            Budget("Preset state (2 × SegmentWidthWide + 1 gap)", row, available, wide));
    }

    // ---- §4: the ALE Operate station grids ------------------------------------

    /// <summary>The DESKTOP single-row station grid (ROUND 15 §17:
    /// <c>*,96,72,72,72</c> at ColumnSpacing 8 — the SAME grid on the Nets card
    /// and the Stations card): its fixed columns plus spacing must leave the
    /// star column at least a readable name width inside the desktop content
    /// budget. The 96 is <c>ValueWidthWide</c> and the three 72s are
    /// <c>SegmentWidth</c>, both read from App.xaml — the markup must spell
    /// them literally (a resource reference cannot live inside a
    /// GridLengthCollection string), so this is the arithmetic AND the
    /// cross-pin.</summary>
    [Fact]
    public void DesktopStationRow_LeavesTheNameColumnEnoughRoom()
    {
        double fixedColumns = StationFixedColumns;
        double star = DesktopContent - fixedColumns;

        Assert.True(star >= MinimumNameWidth,
            $"§17 desktop station row: fixed columns take {fixedColumns} dp of the {DesktopContent} dp "
            + $"desktop budget, leaving {star} dp for the station name — below the stated {MinimumNameWidth} dp "
            + $"minimum (widths read from App.xaml: assoc self {AssocSelfColumn}, "
            + $"action {Widths()["SegmentWidth"]})");
    }

    // ---- ROUND 13 §4 A3: the responsive split is GONE ------------------------
    //
    // CONTRACT CHANGE, owner ruling 2026-08-20. Round 10 pinned the phone
    // geometry in three tests — the phone field line's own budget, the phone
    // action line's, and a NEGATIVE pin asserting the six-column row could not
    // fit a phone. All three described markup that no longer exists, and the
    // negative one was arguing from a budget that had already been superseded:
    // it was derived against the ASSUMED 336, and nobody re-derived it when §9
    // A5 measured 448. This block is what replaces them.

    /// <summary>Consolas' advance width at the ALE row idiom's FontSize 16.
    /// Consolas is monospaced at 0.55 em, so every glyph is 8.8 dp.
    ///
    /// <para>Recorded as a stated constant with its provenance, not measured:
    /// this host has no font stack (the same accepted limitation
    /// <c>PortLabelTextWidth</c> carries above), and constitution §3.3 forbids
    /// settling an Android fit by desktop text measurement in any case. What
    /// the constant is FOR is the reverse direction — turning the residual
    /// column width into a character count that can be compared against what
    /// the owner actually saw on the device.</para></summary>
    private const double ConsolasAdvanceAt16 = 8.8;

    /// <summary>What the owner reported fitting in the station column on the
    /// bench phone, 2026-08-20: "8 or 9 chars". The LOWER bound is the
    /// contract — the observation is the evidence the one-line ruling was made
    /// on, so the arithmetic must keep agreeing with it.</summary>
    private const double OwnerObservedStationChars = 8;

    /// <summary>ROUND 13 §4 A3: the ONE station row, on the PHONE — and ROUND
    /// 15 §17 WIDENS its name column. The six-column geometry that round 10
    /// called impossible fits the MEASURED budget with room for the owner's
    /// observed 8–9 characters, which is why the split died; deleting Type (44)
    /// and Chan grp (64) for one 96-dp Assoc self column then hands the name
    /// 20 dp MORE than it had, so the observation the ruling was made on is
    /// still satisfied with room over.
    ///
    /// <para>This is the pin that VERIFIES the ruling rather than merely
    /// recording it: 448 − 364 = 84 dp ≈ 9 Consolas-16 characters, and the
    /// owner independently reported 8 or 9. Two sources, one number — the
    /// one-line claim moves off "assumed" (plan §9's evidence register).
    /// The residual is computed, never written down, so widening any fixed
    /// column fails here.</para>
    ///
    /// <para>Narrower devices than the bench phone degrade by WRAPPING inside
    /// the name cell (AlePaneMarkupGuardTests pins the CharacterWrap+MaxLines
    /// half) — accepted in the ruling. Final no-clip confirmation is the T3
    /// device pass; §3.3 forbids claiming it from arithmetic.</para></summary>
    [Fact]
    public void TheOneStationRow_LeavesThePhoneTheOwnersEightToNineCharacters()
    {
        double fixedColumns = StationFixedColumns;
        double star = PhoneContent - fixedColumns;
        double characters = Math.Floor(star / ConsolasAdvanceAt16);

        Assert.True(star > 0,
            $"§17 station row: fixed columns take {fixedColumns} dp of the {PhoneContent} dp phone "
            + "budget, leaving the station name NOTHING — the one-line ruling assumed a residual");

        Assert.True(characters >= OwnerObservedStationChars,
            $"§17 station row: {fixedColumns} dp of fixed columns leaves {star} dp of star column "
            + $"≈ {characters} Consolas-16 characters, below the {OwnerObservedStationChars} the owner "
            + $"observed fitting on the bench phone (widths read from App.xaml: assoc self "
            + $"{AssocSelfColumn}, action {Widths()["SegmentWidth"]}). "
            + "The 2026-08-20 one-line ruling was made on that observation — re-derive it with the owner "
            + "rather than letting the row quietly get narrower than what was ruled on.");

        // §17's OWN claim, and the direction it must not drift in: the column
        // change BOUGHT the name room (84 dp under the six-column row), so the
        // residual has to be more than that, not merely enough.
        Assert.True(star > 84,
            $"§17 claimed the deletion widens the name column past the six-column row's 84 dp; "
            + $"it now measures {star} dp");
    }

    // ---- §9: the fixed Windows window ----------------------------------------
    // App.xaml.cs is MAUI-typed and lives in a TFM this net10.0 test host
    // cannot load, so the pin reads the SOURCE — the same accepted limitation
    // every XAML guard here carries, and for the same reason.

    [Fact]
    public void TheWindow_IsFixedToOneConstant_OnAllThreeWidthProperties()
    {
        var source = AppCodeBehind();

        // ONE constant, three properties. A window that set only Width would
        // still be draggable; one that set only the Minimum/Maximum pair would
        // open at the platform default and snap. The §9 contract is that all
        // three read the SAME name — a literal on any of them is how they drift.
        foreach (var property in new[] { "Width", "MinimumWidth", "MaximumWidth" })
            Assert.Contains($"window.{property} = WindowFixedWidth;", source, StringComparison.Ordinal);

        // …and HEIGHT is deliberately free (§9: the panes scroll).
        foreach (var property in new[] { "Height", "MinimumHeight", "MaximumHeight" })
            Assert.DoesNotContain($"window.{property} =", source, StringComparison.Ordinal);

        // Windows only: the phone has no window to fix.
        Assert.Contains("#if WINDOWS", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesktopBudget_IsDerivedFromTheWindow_NotACopiedNumber()
    {
        // §9's budget migration, pinned as a DERIVATION. The old constant was
        // the literal 560 (SettingsPage's now-deleted cap); the budget is the
        // window less its chrome, so a change to the window moves it.
        Assert.Equal(WindowFixedWidthFromSource() - DesktopChrome, DesktopContent);

        // The window is a positive, plausible dp figure — the reader really
        // parsed a number and did not fall through to a default.
        Assert.True(WindowFixedWidthFromSource() >= 320);
    }

    /// <summary>§9's own arithmetic, as the inequality it is: the desktop
    /// station row's fixed columns plus a readable name must fit the content
    /// the fixed window leaves. This is WHY the window is 540 and not 480, and
    /// it is what re-evaluates if either number moves.</summary>
    [Fact]
    public void TheFixedWindow_LeavesEnoughContentForTheDesktopStationRow()
    {
        double required = StationFixedColumns + MinimumNameWidth;

        Assert.True(required <= DesktopContent,
            $"§9: the desktop station row needs {required} dp of content, but WindowFixedWidth "
            + $"{WindowFixedWidthFromSource()} leaves only {DesktopContent} after {DesktopChrome} dp "
            + "of page padding and scrollbar. Invariant 7: move the constant and every pin with it.");
    }

    [Fact]
    public void TheConnectionPage_NoLongerCarriesTheRetired560Cap()
    {
        // §9 retires the ledger entry as well as the number. Pinned from the
        // markup so the ledger and the page cannot disagree; anti-vacuity below
        // proves the reader can see this file's other attributes.
        var page = XDocument.Load(Path.Combine(FindRepoRoot(),
            "src", "Falcon.App", "Views", "SettingsPage.xaml"));

        Assert.DoesNotContain(page.Descendants(),
            e => e.Attributes().Any(a => a.Name.LocalName == "MaximumWidthRequest"));
        Assert.Contains(page.Descendants(),
            e => e.Name.LocalName == "Picker"
                 && e.Attribute("Title")?.Value == "Serial port");
    }

    // ---- Anti-vacuity ---------------------------------------------------------

    /// <summary>The scan actually READS App.xaml. Every pin above is of the
    /// form "this key says that": a reader that returned an empty dictionary
    /// would fail loudly on existence — but a reader that returned a
    /// permissive default, or that silently found a different file, would not.
    /// So: known values that predate this round must come back correct, and a
    /// key that does not exist must come back missing.</summary>
    [Fact]
    public void TheScan_SeesTheResourcesThatWereAlreadyThere()
    {
        var widths = Widths();
        var styles = Styles();

        // Pre-existing widths, with the values they have had since rounds 3-6.
        Assert.Equal(72, widths["SegmentWidth"]);
        Assert.Equal(56, widths["CellWidthNet"]);
        Assert.Equal(112, widths["CellWidthFreq"]);

        // A pre-existing style's setters, in both the size and the family.
        Assert.Equal("16", styles["CardHeading"]["FontSize"]);
        Assert.Equal("Bold", styles["CardHeading"]["FontAttributes"]);
        Assert.Equal("Consolas", styles["CellValue"]["FontFamily"]);

        // And the reader does not invent what is not there.
        Assert.False(widths.ContainsKey("NoSuchWidthKey"));
        Assert.False(styles.ContainsKey("NoSuchStyleKey"));
        Assert.False(styles["CardHeading"].ContainsKey("FontFamily"));
    }

    // ---- reading App.xaml + App.xaml.cs -----------------------------------------

    private static readonly string AppXaml =
        Path.Combine("src", "Falcon.App", "App.xaml");

    private static readonly string AppCodeBehindPath =
        Path.Combine("src", "Falcon.App", "App.xaml.cs");

    private static string AppCodeBehind()
    {
        var path = Path.Combine(FindRepoRoot(), AppCodeBehindPath);
        Assert.True(File.Exists(path), "source missing: " + AppCodeBehindPath);
        return File.ReadAllText(path);
    }

    /// <summary>The declared value of <c>WindowFixedWidth</c>, read from
    /// App.xaml.cs. There is exactly one declaration and this fails loudly if
    /// there is not — a second copy is the drift this whole derivation exists
    /// to prevent.</summary>
    private static double WindowFixedWidthFromSource()
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            AppCodeBehind(),
            @"const\s+double\s+WindowFixedWidth\s*=\s*(?<value>[0-9]+(\.[0-9]+)?)\s*;");

        Assert.Single(matches);
        return double.Parse(matches[0].Groups["value"].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Every MARKUP reference to a width key, across the app's XAML.
    /// Used to pin a key's consumer set — including the empty one.</summary>
    private static IReadOnlyList<string> ReferencesTo(string key)
    {
        var needle = "{StaticResource " + key + "}";
        var root = Path.Combine(FindRepoRoot(), "src", "Falcon.App");
        return
        [
            .. Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f))
        ];
    }

    /// <summary>`x:Double` resources by key.</summary>
    private static Dictionary<string, double> Widths()
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var e in Load().Descendants().Where(e => e.Name.LocalName == "Double"))
        {
            var key = Key(e);
            if (key is null) continue;
            if (double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                found[key] = value;
        }
        return found;
    }

    /// <summary>Keyed styles, each as its Property → Value setter map. Both
    /// setter spellings are read: the attribute form and the
    /// <c>&lt;Setter.Value&gt;</c> property-element form.</summary>
    private static Dictionary<string, Dictionary<string, string>> Styles()
    {
        var found = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var style in Load().Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = Key(style);
            if (key is null) continue;

            var setters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var setter in style.Elements().Where(e => e.Name.LocalName == "Setter"))
            {
                var property = setter.Attribute("Property")?.Value;
                if (property is null) continue;
                var value = setter.Attribute("Value")?.Value
                            ?? setter.Elements()
                                .FirstOrDefault(c => c.Name.LocalName == "Setter.Value")?.Value.Trim();
                if (value is not null) setters[property] = value;
            }
            found[key] = setters;
        }
        return found;
    }

    private static string? Key(XElement e)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

    private static string Budget(string what, double needs, double available, double width)
        => $"{what} needs {needs} dp but only {available} dp is available "
           + $"(width read from App.xaml: {width}). Plan invariant 8: a PROVISIONAL width may move on a "
           + "MEASURED overflow — but the row must still fit its stated budget, and the change is recorded, "
           + "never silent.";

    private static XDocument Load()
    {
        var path = Path.Combine(FindRepoRoot(), AppXaml);
        Assert.True(File.Exists(path), "markup missing: " + AppXaml);
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
