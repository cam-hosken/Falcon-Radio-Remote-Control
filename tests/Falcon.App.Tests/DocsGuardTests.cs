using System.Globalization;
using System.Text.RegularExpressions;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// UI tweaks round 11 §11 — the DOCS are DELIVERABLES, so they get assertions.
///
/// <para><b>Why this file exists.</b> Round 11's own audit found the failure
/// mode first-hand: P2, P3 and P4 all landed with zero documentation updates,
/// README's status line claimed 444 Core tests while docs/tests.md claimed 459,
/// README's index promised "every doc in the repo is listed here" while three
/// plan files were absent, and protocol.md asserted as fact two things the
/// bench had disproved. Prose rots silently in exactly the way code does not,
/// and it rots WORST on the pages everyone treats as the easy part.</para>
///
/// <para><b>What is and is not pinned.</b> These are DRIFT pins, not style
/// review: each one ties a number or a string in a doc to the SOURCE OF TRUTH
/// it was copied from — the code constant, the other doc, the filesystem. A
/// doc claim with no machine-checkable source (a rationale, a decision, a
/// deviation note) is deliberately not pinned here; it is reviewed by a human,
/// which is the only thing that can review it.</para>
///
/// <para><b>ACCEPTED LIMITATION</b>, stated like every guard in this suite: a
/// pin can only catch a claim it knows how to look for. A doc that stays
/// silent about a new contract passes every test below. Completeness of PROSE
/// is a review property; completeness of the INDEX and agreement between
/// COPIES are the two parts a machine can hold, and those are what this
/// holds.</para>
///
/// <para><b>EVERY PROSE NEEDLE GOES THROUGH <see cref="ContainsPhrase"/></b>
/// (P6 audit round 1 — a GATE finding). A needle matched with raw
/// <c>string.Contains</c> is pinned not to the doc's WORDS but to its LINE
/// ENDINGS and its WRAPPING: two of the P6 needles embedded a <c>\n</c> and a
/// two-space continuation indent, so they passed on the authoring machine and
/// FAILED on a fresh CRLF clone — a green gate that was green for the wrong
/// reason. <c>ContainsPhrase</c> collapses every whitespace RUN to one space on
/// both sides, which makes a match depend on the sentence and nothing else:
/// CRLF vs LF, a reflowed paragraph and a changed indent all stop mattering,
/// while a changed WORD still fails. The sweep is complete — no assertion in
/// this file compares a multi-word phrase any other way, and the two
/// non-phrase readers (the index regex and the suite-count regex) run over
/// text this file has already normalised.</para>
/// </summary>
public class DocsGuardTests
{
    // ---- README's index: complete, and no dangling links ---------------------

    [Fact]
    public void TheReadmeIndex_NamesEveryMarkdownFileInTheRepo()
    {
        // README states outright: "This README is the index; every doc in the
        // repo is listed here." That sentence is the contract, and until
        // round 11 it was false for three plan files. A doc nobody can find
        // from the index is a doc that stops being maintained.
        var indexed = IndexedDocuments();
        var onDisk = RepositoryMarkdown();

        var missing = onDisk.Where(f => !indexed.Contains(f)).Order().ToList();

        Assert.True(missing.Count == 0,
            "README's documentation index claims to list every doc in the repo, but these are "
            + "not in it: " + string.Join(", ", missing));

        // Anti-vacuity: the two readers really found things, and the index is
        // not somehow a superset that would make the check trivially pass.
        Assert.NotEmpty(indexed);
        Assert.True(onDisk.Count >= 20, $"only {onDisk.Count} markdown files found — the scan is broken");
    }

    [Fact]
    public void TheReadmeIndex_HasNoDanglingLinks()
    {
        // The other direction: a row pointing at a file that has been renamed
        // or deleted. Same cost, opposite cause.
        var dangling = IndexedDocuments()
            .Where(target => !File.Exists(Path.Combine(RepoRoot(), target))
                             && !Directory.Exists(Path.Combine(RepoRoot(), target.TrimEnd('/'))))
            .Order()
            .ToList();

        Assert.True(dangling.Count == 0,
            "README's index points at documents that do not exist: " + string.Join(", ", dangling));
    }

    [Fact]
    public void TheReadmeIndex_CarriesTheRoundElevenRow()
    {
        // The round's own row, by the plan file it records. §11 names the
        // README index row as a P5 deliverable.
        Assert.Contains("plan/plan-ui-tweaks-round11.md", IndexedDocuments());
    }

    [Fact]
    public void TheReadmeIndex_CarriesTheRoundFifteenRow_MarkedComplete()
    {
        // The round's own row, by the plan file it records — §5's close-out
        // deliverable, in the round-11 idiom above. The row is what tells the
        // next reader (or the next agent) that the plan file is HISTORY rather
        // than work in flight, and a round that merges without rewriting it
        // leaves an "IN PROGRESS" row pointing at finished work — which is
        // exactly what this row said until this commit.
        const string plan = "plan/plan-round15.md";
        Assert.Contains(plan, IndexedDocuments());

        var row = Read("README.md").Split('\n')
            .Single(l => l.Contains("(" + plan + ")", StringComparison.Ordinal));

        AssertPhrase(row, "Round 15, **COMPLETE**", "the round-15 status");

        // …and it says what landed: the five phases by their letters, so a row
        // trimmed to a stub fails rather than passing as "some prose".
        foreach (var phase in new[] { "A0", "phase 2", "phase 3", "**Phase 4**", "**Phase 5**" })
            Assert.Contains(phase, row, StringComparison.OrdinalIgnoreCase);

        // The suite figure it quotes is the one round 15 CLOSED at — a
        // historical fact, so it is a literal.
        //
        // It used to be read from the README status line instead, on the
        // reasoning that "the three copies of one number cannot drift in a
        // pair". That only held while round 15 was the NEWEST round: the
        // status line carries the CURRENT figure, so the first later round to
        // move it turned this pin into a demand that a finished round's record
        // be rewritten with numbers it never measured. Round 16 fixes phase 1
        // is that round (788 / 2232 → 823 / 2246), and the historical row is
        // what stays true. The live status line and docs/tests.md are still
        // pinned to each other by
        // TheSuiteCounts_AgreeBetweenReadmeAndTestsDoc.
        Assert.Contains("788 Core / 2232 App", row, StringComparison.Ordinal);
    }

    // ---- Suite counts: one number, two documents ----------------------------

    [Fact]
    public void TheSuiteCounts_AgreeBetweenReadmeAndTestsDoc()
    {
        // The exact defect this catches HAPPENED: README read 444 Core while
        // docs/tests.md read 459, and neither was current. Two hand-maintained
        // copies of one number always end this way; the pin makes the next
        // divergence a red test instead of a reader's guess.
        var (readmeCore, readmeApp) = SuiteCounts(Read("README.md"));
        var (testsCore, testsApp) = SuiteCounts(Read(Path.Combine("docs", "tests.md")));

        Assert.Equal(testsCore, readmeCore);
        Assert.Equal(testsApp, readmeApp);

        // …and they are plausible figures, so a reader that matched the wrong
        // pattern (a year, a line number) fails here rather than passing.
        Assert.True(readmeCore > 400 && readmeApp > 1000,
            $"the parsed suite counts ({readmeCore} Core / {readmeApp} App) do not look like suite totals");
    }

    // ---- ui.md against the code it describes --------------------------------

    [Fact]
    public void TheUiDoc_QuotesTheAlePromptRefusal_ByteExact()
    {
        // §6 gives this string verbatim and R13 governs its wording, so it
        // exists in exactly two places: the ViewModel and the doc. Pinned
        // against the CODE, so the doc cannot describe a message the app does
        // not send.
        AssertPhrase(Read(Path.Combine("docs", "ui.md")), ModemPresetsViewModel.AleDisableRefusal,
            "the ALE-prompt disable refusal");
    }

    [Fact]
    public void TheUiDoc_QuotesTheOnAirReasons_ByteExact()
    {
        // ROUND 15 item I: the on-air refusals are operator-facing sentences,
        // so each lives in exactly two places — the ViewModel that raises it
        // and the paragraph that documents it — and the doc is pinned against
        // the CODE. The manager's 2026-08-23 ruling reworded two of them
        // (they still said "In a call" / "A call/send is in progress" while
        // the term they explain had become the on-air one), which is exactly
        // the drift a doc quoting a string by hand cannot survive.
        var ui = Read(Path.Combine("docs", "ui.md"));
        AssertPhrase(ui, AleViewModel.LqaInProgressReason, "the LQA-in-progress Scan reason");
        AssertPhrase(ui, MessagesViewModel.OnAirDisabledReason, "the AMD send's on-air reason");
        AssertPhrase(ui, AleProgrammingViewModel.InCallDisabledReason,
            "the programming cards' on-air reason");
    }

    [Fact]
    public void TheUiDoc_QuotesTheRoundElevenTypeAndPortWords_FromTheVocabulary()
    {
        // The §3 renames are display strings, and a half-applied rename is the
        // classic failure — five buttons in the new vocabulary, one in the old,
        // and a doc still describing the old. Every word is read from the
        // VOCABULARY, so this cannot drift into asserting its own literals.
        var ui = Read(Path.Combine("docs", "ui.md"));

        foreach (var value in ModemPresetVocabulary.Types.Concat(ModemPresetVocabulary.DataModes))
            AssertPhrase(ui, value.Display, "the display word");

        // …and the round-10 words it REPLACED are gone from the doc's own
        // description of the rows (they survive only where the doc is
        // explicitly recording what changed, which is why this checks the
        // renamed pair rather than every retired word). Phrase-matched too, so
        // a retired phrase cannot hide across a line break.
        Assert.False(ContainsPhrase(ui, "row label is just"),
            "docs/ui.md still carries the retired round-10 row-label wording");
    }

    [Fact]
    public void TheUiDoc_StatesTheWindowWidth_TheSourceConstantSays()
    {
        // §9's number lives in App.xaml.cs. ui.md explains WHY it is 540 —
        // an explanation attached to the wrong number is worse than none.
        var declared = WindowFixedWidthFromSource();
        var ui = Read(Path.Combine("docs", "ui.md"));

        AssertPhrase(ui, "`WindowFixedWidth` is **" + declared.ToString(CultureInfo.InvariantCulture),
            "the window-width statement");
    }

    [Fact]
    public void TheUiDoc_CarriesTheRoundElevenLedgerEntries()
    {
        // §11 names these four by name as P5 doc deliverables. Each is a
        // one-line change that is easy to make in the code and forget in the
        // ledger — which is precisely the class of defect this round's audit
        // kept finding.
        var ui = Read(Path.Combine("docs", "ui.md"));

        foreach (var (entry, needle) in new[]
        {
            ("the MOM rename", "\"MOM\""),
            ("the retired 560 cap", "RETIRED round 11 §9"),
            ("the fixed window", "The fixed Windows window (round 11 §9)"),
            ("the HOP net info view", "The net info view (round 11 §7)"),
            ("the EXCLUDE section", "Exclusion bands — a NEW section"),
        })
            AssertPhrase(ui, needle, entry);
    }

    [Fact]
    public void TheUiDoc_QuotesTheRoundFourteenHopLimitNotes_FromTheViewModel()
    {
        // ROUND 14 A2. The K6 tightening put FIVE operator-visible sentences on
        // screen, and they exist in exactly two places: HopSettingsViewModel and
        // this doc. Read from the CONSTANTS, so the doc cannot describe a
        // refusal the pane does not raise or an advisory whose wording has
        // moved — the ALE-prompt-refusal pin's shape, applied to the family
        // that replaced a bench capture with a client-side note.
        //
        // SCOPED to the Mode settings page section (the round-13 C1 lesson):
        // the K6 contract is what a reader actually reads, so that is what has
        // to be current. An incidental quote in a ledger elsewhere in a
        // 3000-line file must not be able to carry this pin.
        var settings = Section(Read(Path.Combine("docs", "ui.md")), "Mode settings page");

        foreach (var note in new[]
        {
            HopSettingsViewModel.BelowHopFloorRefusal,
            HopSettingsViewModel.SpanRefusesGenerationAdvisory,
            HopSettingsViewModel.MinimumSpanAdvisory,
            HopSettingsViewModel.ListFloorAdvisory,
            HopSettingsViewModel.ListSpanAdvisory,
        })
            AssertPhrase(settings, note, "a round-14 hop-limit note");

        // ANTI-VACUITY: a slicer that returned the whole document would turn
        // the five assertions above back into "somewhere in ui.md".
        Assert.StartsWith("## Mode settings page", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("## Operate page", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProtocolDoc_RecordsTheAleToHopDoubleTune_AsRadioSide_InTheHopSection()
    {
        // CLONE-FIELD ROUND 2 F6 (owner ruling R-B, decision D5) — the H2 branch
        // of the plan's outcome matrix. The whole deliverable IS this record:
        // probe P4 settled that the doubled generate/tune cycle on an ALE→HOP
        // entry is the RADIO's (a bare `HO` over the remote port double-cycles
        // from `ALE>` and single-cycles from `SSB>`), the owner confirmed it
        // from the front panel, and the app therefore changes NOTHING — no
        // re-routing through SSB. A "we decided not to write code" outcome is
        // the easiest of all to lose, so the sentence that carries it is
        // pinned, section-scoped like every other prose pin here.
        var hopSection = Section(Read(Path.Combine("docs", "protocol.md")), "HOP mode");

        AssertPhrase(hopSection, "from the FRONT PANEL", "the owner's front-panel observation (F6/R-B)");
        AssertPhrase(hopSection, "radio-side, not app traffic", "the H2 verdict");
        AssertPhrase(hopSection, "the app does NOT re-route", "ruling D5");
        AssertPhrase(hopSection, "p4-hop-entry-route-20260821-180243.jsonl", "the P4 transcript citation");

        Assert.StartsWith("## HOP mode", hopSection, StringComparison.Ordinal);
        Assert.DoesNotContain("## Modem", hopSection, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProtocolDoc_ScopesTheModemPresetNumbersToThePrompt()
    {
        // F9/F10/F11: "Preset numbers are 0–6 on this firmware" was the belief
        // that kept three presets out of every clone and a working control off
        // the HOP pane. The correction is the load-bearing sentence of the whole
        // phase, so the doc must carry BOTH bands and the transcript, and must
        // no longer carry the old absolute claim.
        var modem = Section(Read(Path.Combine("docs", "protocol.md")), "Modem");

        AssertPhrase(modem, "Preset numbers are PROMPT-SCOPED", "the corrected scope claim");
        AssertPhrase(modem, "0–6 at `SSB>`/`ALE>`, 7–9 at `HOP>`", "both bands");
        AssertPhrase(modem, "p5-hop-modem-presets-20260821-180547.jsonl", "the P5 transcript citation");
        AssertPhrase(modem, "p5c-hop-modem-baud-20260821-182807.jsonl", "the P5c transcript citation");
        AssertPhrase(modem, "PRESET DISABLED", "the disabled-select refusal token");

        Assert.False(ContainsPhrase(modem, "**Preset numbers are 0–6 on this firmware**"),
            "docs/protocol.md still asserts the unscoped 0–6 claim that probes P5–P5d2 corrected");
    }

    [Fact]
    public void TheReadmeIndex_CarriesTheRoundSixteenFixesRow_MarkedComplete()
    {
        // The round's own row, by the plan file it records — the round-15
        // idiom above. The row is what tells the next reader (or the next
        // agent) that the plan file is HISTORY rather than work in flight.
        const string plan = "plan/plan-round16-fixes.md";
        Assert.Contains(plan, IndexedDocuments());

        // The INDEX row, not the round-16 row that merely LINKS to this plan:
        // an index row is the one that opens with the document's own link.
        var row = Read("README.md").Split('\n')
            .Single(l => l.StartsWith("| [" + plan + "]", StringComparison.Ordinal));

        AssertPhrase(row, "Round 16 fixes, **COMPLETE**", "the round-16-fixes status");

        // …and it says what landed, so a row trimmed to a stub fails rather
        // than passing as "some prose".
        foreach (var item in new[] { "**S1**", "**S2**", "**S3**", "**S4**", "**S5**", "**S6**", "**S7**" })
            Assert.Contains(item, row, StringComparison.Ordinal);

        // The suite figure it quotes was the CURRENT one while round-16-fixes
        // was the latest round; the clone-write-structural round (2026-08-30,
        // 870/2503) superseded it, so the row's figure is now a HISTORICAL
        // fact pinned as a literal — it must never be "updated" to a later
        // round's counts (rewriting a dated ledger entry is history-editing).
        Assert.Contains("823 Core / 2248 App", row, StringComparison.Ordinal);
    }

    // ---- Round 16 fixes S6: the pacing rewrite and its probe --------------

    [Fact]
    public void TheProbesDoc_CarriesTheP17Section()
    {
        // P17 is the study the pacing rewrite rests on. A protocol.md claim
        // whose probe has no write-up is a claim a reader cannot check.
        var probes = Read(Path.Combine("docs", "probes.md"));

        Assert.Contains("### P17", probes, StringComparison.Ordinal);
        AssertPhrase(probes, "p17-gate-trade-20260823-084718.jsonl", "the P17 transcript citation");
        AssertPhrase(probes, "bench/probe-p17-gate-trade.ps1", "the P17 script path");
    }

    [Fact]
    public void TheProtocolDoc_PacingBullet_CitesP17_AndNoLongerClaimsTheMechanism()
    {
        // The gate's justification rested on ONE 2026-08-02 experiment that
        // recorded COUNTS ONLY (its raw was never committed) and stated the
        // mechanism as fact. P17 re-ran it with the bytes: the RULE survives,
        // the MECHANISM does not.
        var protocolDoc = Read(Path.Combine("docs", "protocol.md"));

        AssertPhrase(protocolDoc, "p17-gate-trade-20260823-084718.jsonl", "the P17 transcript citation");
        AssertPhrase(protocolDoc, "THE RULE STANDS; the MECHANISM does not", "the pacing bullet's new claim");

        Assert.False(
            ContainsPhrase(protocolDoc,
                "**the radio silently swallows commands that arrive while it is streaming a heavy response**"),
            "docs/protocol.md still asserts the 2026-08-02 mechanism claim that P17 retired");
    }

    [Fact]
    public void TheProtocolDoc_ShBlockCount_IsTheMeasuredOne()
    {
        // "22 value lines" counted an INTERLEAVED async line as an SH value.
        // The transcript is the source of truth and the doc must cite it.
        var protocolDoc = Read(Path.Combine("docs", "protocol.md"));

        AssertPhrase(protocolDoc, "**21 value lines**", "the measured SSB SH block size");
        AssertPhrase(protocolDoc, "p8-init-sentinel-timing-20260822-093258.jsonl",
            "the transcript the count was taken from");
        Assert.False(ContainsPhrase(protocolDoc, "22 value lines"),
            "docs/protocol.md still claims 22 value lines in the SSB SH block");
    }

    [Fact]
    public void TheUiDoc_RecordsTheHopModemRowReturn_InTheOperateSection()
    {
        // RE-TARGETED by clone-field round 2 F10 (owner ruling R-C): this pin
        // used to hold round 14 A3's REMOVAL record. The row is BACK — the r13
        // probe that grounded the removal asked `HOP>` for presets 0-6, the half
        // that prompt does not have, and P5-P5d2 found a working modem surface
        // over 7-9. A return is as easy to make in the markup and leave
        // unrecorded in the prose as a removal was, so the RECORD is again what
        // gets pinned: that the row is back, over which presets, on what
        // evidence, and how many placements the wheel now has.
        var operatePage = Section(Read(Path.Combine("docs", "ui.md")), "Operate page");

        foreach (var (entry, needle) in new[]
        {
            ("the return itself", "The modem row is BACK (clone-field round 2 F10"),
            ("its scope", "presets 7-9"),
            ("its bench grounding", "p5d2-hop-modem-select-enabled"),
            ("the corrected placement count", "THREE placements since clone-field round 2"),
        })
            AssertPhrase(operatePage, needle, entry);

        // …and BOTH superseded paragraphs are gone from this section, so a
        // half-applied edit that left either standing beside the new one fails
        // here: round 8's original claim, and round 14 A3's removal record.
        Assert.False(ContainsPhrase(operatePage, "adds the **modem row** at the card's bottom"),
            "docs/ui.md still carries the round-8 HOP modem-row paragraph");
        Assert.False(ContainsPhrase(operatePage, "The modem row is REMOVED (round 14 A3"),
            "docs/ui.md still carries the round-14 A3 removal record the F10 return replaced");

        Assert.StartsWith("## Operate page", operatePage, StringComparison.Ordinal);
        Assert.DoesNotContain("## Mode settings page", operatePage, StringComparison.Ordinal);
    }

    // ---- round 11 §9A: the cloning docs against the code ---------------------

    [Fact]
    public void TheUiDoc_QuotesTheCloneWriteConfirmation_ByteExact()
    {
        // The §9A confirmation is the round's most consequential string: ONE
        // prompt standing in for an erase-and-rewrite of almost everything.
        // Pinned against the CODE, so the doc cannot describe a narrower
        // question than the app actually asks.
        var ui = Read(Path.Combine("docs", "ui.md"));

        AssertPhrase(ui, CloneService.ConfirmTitle, "the clone confirm TITLE");
        AssertPhrase(ui, CloneService.ConfirmMessage, "the clone confirm MESSAGE");
        AssertPhrase(ui, CloneService.ConfirmAccept + " / " + CloneService.ConfirmCancel,
            "the clone confirm BUTTONS");
    }

    /// <summary>ROUND 15 D (critic F39) — the address-programming card's own
    /// subsection carries the book row's member line: the TAB TABLE says the
    /// book shows membership, and the membership paragraph names all THREE
    /// row states, read from the VM's constants so the doc cannot quote a line
    /// the card does not render. SUBSECTION-scoped: a whole-document search
    /// would be satisfied by any mention anywhere, and this card's section is
    /// what a reader actually reads.</summary>
    [Fact]
    public void TheUiDoc_RecordsTheBookRowsMemberLine_InTheAleSettingsSection()
    {
        var ale = Subsection(Read(Path.Combine("docs", "ui.md")), "ALE settings pane");

        // The tab table's Address-book row.
        AssertPhrase(ale, "each NET row carries its net membership", "the Address-book tab row");

        // The membership paragraph's three states, from the constants.
        AssertPhrase(ale, AleProgrammingViewModel.MemberPlaceholderText, "the UNREAD state");
        AssertPhrase(ale, AleProgrammingViewModel.NoMembersRowText, "the READ-EMPTY state");
        AssertPhrase(ale, AleProgrammingViewModel.MembersRowPrefix + "A, B, C", "the rows state");

        // ANTI-VACUITY: the slice is one card's subsection, not the page.
        Assert.StartsWith("### ALE settings pane", ale, StringComparison.Ordinal);
        Assert.DoesNotContain("### HOP settings pane", ale, StringComparison.Ordinal);
    }

    /// <summary>ROUND 15 E-4 — the CONFIRMATION VOCABULARY rule is in the
    /// display constitution, where a reader looks for a display rule, and it
    /// carries the clause every caller has to satisfy. Section-scoped: the
    /// rule's home is the constitution, not a passing mention.</summary>
    [Fact]
    public void TheUiDoc_CarriesTheConfirmationVocabularyRule_InTheConstitution()
    {
        var constitution = Section(Read(Path.Combine("docs", "ui.md")), "The display constitution");

        AssertPhrase(constitution, "CONFIRMATION VOCABULARY", "the rule's heading");
        AssertPhrase(constitution, "the FIRST begins", "the message clause");
        AssertPhrase(constitution, "ACCEPT** = the title's verb", "the accept clause");
        AssertPhrase(constitution, "EXEMPT from the sentence count", "the impact-line exemption (F52)");
        AssertPhrase(constitution, "ConfirmationWordingGuardTests", "the guard that pins it");

        // ROUND 15 H-2's rule lives beside it, with its SCOPE stated (F63).
        AssertPhrase(constitution, "THE AFFIRMATIVE IS ON THE LEFT", "the H-2 rule");
        AssertPhrase(constitution, "ChoiceOrderGuardTests", "the guard that pins it");
        AssertPhrase(constitution, "OUTSIDE the guard", "the rule's stated scope boundary");

        // ANTI-VACUITY: the slice really is the constitution section.
        Assert.StartsWith("## The display constitution", constitution, StringComparison.Ordinal);
        Assert.DoesNotContain("## Deviations and deferrals", constitution, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiDoc_QuotesTheScanGateRowCaption_FromTheViewModel()
    {
        // D2's rule lives on the identity table's scan-gate ROW now, not on a
        // hint about one chosen name. Read from the constant, so the doc cannot
        // drift into quoting a sentence the card does not show.
        AssertPhrase(Read(Path.Combine("docs", "ui.md")), SelfRowViewModel.ScanGateCaption,
            "the scan-gate row caption");
    }

    [Fact]
    public void TheUiDoc_QuotesRoundFifteensTwoCloningCaptions_FromTheViewModel()
    {
        // C-Q5 and C-3: both are sentences the operator reads INSTEAD of the
        // identity rows, so the doc must quote what the card shows — from the
        // constants, not from memory.
        var ui = Read(Path.Combine("docs", "ui.md"));

        AssertPhrase(ui, CloneViewModel.BookNotReadCaption, "the unread-book caption (C-Q5)");
        AssertPhrase(ui, CloneViewModel.FillGateCaption("W6HOS"), "the fill-gate caption (C-3)");
    }

    /// <summary>
    /// The clone-field-round-2 docs gate (§6): the clone-flow section carries
    /// BOTH of the round's structural changes by name. Scoped to the section —
    /// a needle searched across the whole document is satisfied by any mention
    /// anywhere, including a ledger entry, and then the canonical section is
    /// free to go stale (the round-13 C1 finding).
    /// </summary>
    [Fact]
    public void TheArchitectureDoc_CarriesTheCloneRoundTwoSeams_ByName()
    {
        var cloning = Section(
            Read(Path.Combine("docs", "software-architecture.md")), "Radio cloning");

        // Phase 1's F1 seam…
        AssertPhrase(cloning, "CLOSING RESTORE", "the closing-restore section");
        // …and phase 2's F2 ruling (R-A).
        AssertPhrase(cloning, "PER-SELF DISPOSITION", "the per-self disposition table");
        AssertPhrase(cloning, "role changes are NOTICES, drops are", "the I-6 disposition");
    }

    [Fact]
    public void TheArchitectureDoc_StatesTheCloneFileVersion_TheModelDeclares()
    {
        // A file-format doc naming the wrong version tag is worse than none:
        // it is what someone reaches for when a load rejects.
        AssertPhrase(Read(Path.Combine("docs", "software-architecture.md")), CloneFile.CurrentVersion,
            "the clone-file version tag");
    }

    [Fact]
    public void TheUiDoc_RecordsTheCloningCardAsBuilt_AndItsTwoResiduals()
    {
        // §11 names the card's ui.md entry as a P6 deliverable, and the two
        // RESIDUALS are exactly the kind of honest limitation that rots out of
        // a doc first — they are the reason a verify diff is expected rather
        // than alarming.
        var ui = Read(Path.Combine("docs", "ui.md"));

        foreach (var (entry, needle) in new[]
        {
            ("the wired card", "**WIRED in round 11 §9A**"),
            ("the retired stub guard", "`CloningStubTests` is RETIRED"),
            // R-A RETIRED round 11's "the Entry wins over the picker" rule: a
            // row can only be in one of three states now, so there is nothing
            // for a precedence rule to arbitrate. The doc records the TABLE
            // that replaced it, and its two settled rules.
            ("the identity table", "**The identity control is a TABLE**"),
            ("row exclusivity", "One explainable state per row (A-1)"),
            ("the scan-gate row", "The scan-gate self is Replace-only (D2)"),
            // These two spanned a line break when they were written, and the
            // embedded "\n  " is exactly what made the P6 gate line-ending
            // dependent. Written as PLAIN SENTENCES now; the phrase matcher
            // makes the wrapping irrelevant.
            ("the closed deferral", "CLOSED by round 11 §9A"),
            ("the target-only channel residual", "cannot be removed"),
            ("the analog-squelch residual", "Core's FM-squelch compensation"),
        })
            AssertPhrase(ui, needle, entry);
    }

    // ---- clone round 12 §6: the connection-flow docs against the code -------

    [Fact]
    public void TheUiDoc_QuotesTheAboutPagesCarriedFacts_FromTheConstants()
    {
        // The About facts exist in exactly two places — AboutContent and this
        // doc — and they are hardware facts (a cable part number, a connector,
        // three pin letters). Read from the CODE, so the doc cannot describe a
        // pinout the page does not show.
        //
        // SCOPED TO THE SECTION (round 13 C1 audit round 1, a CONFIRMED
        // finding). This used to search the WHOLE of ui.md, and that is not the
        // same assertion: C1 added an outstanding-manual-check entry to the
        // deviations ledger at the bottom of the file that also quotes the
        // byline, and with the search unscoped the auditor could revert the
        // CANONICAL About section to the old "Based on … (© 2020)" wording and
        // watch the whole suite stay green — the incidental mention downstream
        // was carrying the pin. What has to be true is that the section a
        // reader actually reads is current, so that section is what is read.
        var about = Section(Read(Path.Combine("docs", "ui.md")), "About page");

        foreach (var fact in new[]
        {
            AboutContent.CableRecommended,
            AboutContent.CableAlternate,
            AboutContent.MatingConnector,
            AboutContent.PinoutGround,
            AboutContent.PinoutTx,
            AboutContent.PinoutRx,
            // ROUND 13 C1: the credit constant stops before the year now, so
            // the doc is held to the BYLINE, not to a year that would make
            // this guard fail every New Year's Day.
            AboutContent.CreditPrefix,
        })
            AssertPhrase(about, fact, "an About page fact");

        // ANTI-VACUITY: a slicer that returned the whole document, or an empty
        // string, would turn every line above back into the assertion this
        // finding was about. So the slice is checked for being a REAL, BOUNDED
        // section: it starts at the heading, it stops before the next one, and
        // it does not reach the ledger entry at the foot of the file.
        Assert.StartsWith("## About page", about, StringComparison.Ordinal);
        Assert.DoesNotContain("Android tray Exit", about, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSTANDING — named, not skipped", about, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiDoc_RecordsTheDroppedKeyboardTip()
    {
        // §6 F6 drops the old frequency-scroll tip. "Recorded, not silent" is
        // the requirement, so the RECORD is what gets pinned — the doc must
        // still say the tip was dropped and why.
        //
        // SCOPED TO THE SECTION (round 13 C2, the family fix for C1 audit round
        // 1's confirmed finding). Same shape, same file, same section: the
        // needles were searched across the WHOLE of ui.md, so any later mention
        // of "Step Size +/- buttons" anywhere in a 3000-line document — a
        // deviations note, a round summary, a future page's prose — would keep
        // this green while the About section's own record was deleted. The
        // record has to be where the reader is.
        var about = Section(Read(Path.Combine("docs", "ui.md")), "About page");

        AssertPhrase(about, "DROPPED, recorded not silent", "the dropped-tip record");
        AssertPhrase(about, "Step Size +/- buttons", "the dropped tip's own words");
    }

    [Fact]
    public void TheUiDoc_CarriesTheConnectionFlowEntries()
    {
        // §6's five as-built entries, each a decision that is easy to make in
        // the code and forget in the doc — which is the exact class of defect
        // this file exists for.
        var ui = Read(Path.Combine("docs", "ui.md"));

        foreach (var (entry, needle) in new[]
        {
            ("the connection-first section", "Connection-first flow (clone round 12 §6)"),
            ("the F3 edge table", "Ready → Reconnecting"),
            ("the moved connect button", "The Connect ⇄ Disconnect button (clone round 12 §6 F2)"),
            ("the port poll", "The port poll and the selection model"),
            ("the TitleBarFlat entry", "the title bar's low-profile button style"),
            ("the routed About page", "About page (clone round 12 §6 F6)"),
            ("the Android tray Exit", "Android tray Exit (clone round 12 §6 F5)"),
            ("the outstanding manual checks", "OUTSTANDING — named, not skipped"),
        })
            AssertPhrase(ui, needle, entry);
    }

    [Fact]
    public void TheTestsDoc_RecordsTheP6Families()
    {
        // The suite grew by more than a hundred cases; a tests.md that does not
        // say what they PIN leaves the next reader counting instead of reading.
        var tests = Read(Path.Combine("docs", "tests.md"));
        AssertPhrase(tests, "P6 (radio cloning backend, §9A)", "the P6 heading");
        AssertPhrase(tests, "ROUND TRIP WITH PERTURBATION", "the round-trip gate");
    }

    // ---- phrase matching ----------------------------------------------------

    /// <summary>
    /// Does the document contain this PHRASE — its words, in order, regardless
    /// of how whitespace fell between them? Every whitespace RUN collapses to
    /// one space on both sides, so CRLF vs LF, a reflowed paragraph and a
    /// changed continuation indent all stop mattering, while a changed WORD
    /// still fails.
    /// </summary>
    private static bool ContainsPhrase(string document, string phrase)
        => Collapse(document).Contains(Collapse(phrase), StringComparison.Ordinal);

    private static void AssertPhrase(string document, string phrase, string what)
        => Assert.True(ContainsPhrase(document, phrase),
            $"the document is missing {what} (looked for: \"{phrase}\")");

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// The text of ONE `##` section — from the heading that starts with the
    /// given words, up to the next heading at the SAME level (or the end of the
    /// document). Round 13 C1's audit finding: a needle searched across a whole
    /// document is satisfied by ANY mention of it, including one in a
    /// deviations ledger, so a pin on "the doc says X" quietly becomes "the doc
    /// says X somewhere" — and the canonical section is free to go stale. A
    /// deeper heading (`###`) does NOT end the section: subsections belong to
    /// the section that contains them.
    /// </summary>
    /// <summary>The same slice at the SUBSECTION level (<c>###</c>) — for a
    /// pin whose canonical home is one card inside a page-sized section. It
    /// ends at the next heading of EITHER level, because a subsection is
    /// bounded by its siblings and by its parent's end.</summary>
    private static string Subsection(string document, string headingStartsWith)
    {
        var lines = document.Split('\n');

        int start = Array.FindIndex(lines,
            l => l.StartsWith("### " + headingStartsWith, StringComparison.Ordinal));
        Assert.True(start >= 0, $"the document has no \"### {headingStartsWith}…\" subsection");

        int end = Array.FindIndex(lines, start + 1,
            l => l.StartsWith("## ", StringComparison.Ordinal)
                 || l.StartsWith("### ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;

        return string.Join("\n", lines[start..end]);
    }

    [Fact]
    public void TheSubsectionSlicer_StopsAtTheNextHeadingOfEitherLevel()
    {
        // The second slicer's own control, held to the first one's standard:
        // every use of it is "the phrase is in THIS card's subsection", which
        // means nothing unless the slice really is bounded on both sides.
        const string doc =
            """
            ## Page
            intro

            ### Alpha card
            alpha body

            ### Beta card
            beta body

            ## Other page
            other body
            """;

        string alpha = Subsection(doc, "Alpha");
        Assert.Contains("alpha body", alpha, StringComparison.Ordinal);
        Assert.DoesNotContain("beta body", alpha, StringComparison.Ordinal);
        Assert.DoesNotContain("other body", alpha, StringComparison.Ordinal);

        string beta = Subsection(doc, "Beta");
        Assert.Contains("beta body", beta, StringComparison.Ordinal);
        Assert.DoesNotContain("other body", beta, StringComparison.Ordinal);
    }

    private static string Section(string document, string headingStartsWith)
    {
        var lines = document.Split('\n');

        int start = Array.FindIndex(lines,
            l => l.StartsWith("## " + headingStartsWith, StringComparison.Ordinal));
        Assert.True(start >= 0, $"the document has no \"## {headingStartsWith}…\" section");

        int end = Array.FindIndex(lines, start + 1,
            l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;

        return string.Join("\n", lines[start..end]);
    }

    [Fact]
    public void TheSectionSlicer_StopsAtTheNextHeading_ButNotAtASubheading()
    {
        // The slicer's own control. Every use of it above is "the phrase is in
        // THIS section", which is only stronger than the old whole-document
        // search if the slice really is bounded — so the boundary behaviour is
        // asserted directly rather than assumed from one green run.
        const string doc =
            """
            # Title
            intro

            ## Alpha
            alpha body
            ### Alpha detail
            still alpha

            ## Beta
            beta body
            """;

        var alpha = Section(doc, "Alpha");

        // It reaches the subsection…
        Assert.Contains("still alpha", alpha, StringComparison.Ordinal);
        Assert.Contains("### Alpha detail", alpha, StringComparison.Ordinal);
        // …and it STOPS at the sibling, which is the whole point.
        Assert.DoesNotContain("beta body", alpha, StringComparison.Ordinal);
        // …and it does not reach backwards either.
        Assert.DoesNotContain("intro", alpha, StringComparison.Ordinal);

        // The last section runs to the end of the document.
        Assert.Contains("beta body", Section(doc, "Beta"), StringComparison.Ordinal);

        // And a heading that is not there FAILS rather than returning
        // something empty and vacuously passing.
        Assert.ThrowsAny<Exception>(() => Section(doc, "Gamma"));
    }

    [Fact]
    public void ThePhraseMatcher_IgnoresLineEndingsAndWrapping_ButNotWords()
    {
        // The GATE finding's own regression pin. The first two cases are the
        // exact shapes that broke: a CRLF document, and a doc that wrapped the
        // phrase with a continuation indent.
        const string phrase = "CLOSED by round 11 §9A";

        Assert.True(ContainsPhrase("the deferral is CLOSED by round 11 §9A now", phrase));
        Assert.True(ContainsPhrase("the deferral is CLOSED by round 11\r\n  §9A now", phrase));
        Assert.True(ContainsPhrase("CLOSED\tby   round 11\n§9A", phrase));

        // …and it can still MISS, which is what makes every use of it an
        // assertion rather than a formality.
        Assert.False(ContainsPhrase("the deferral is OPEN in round 11 §9A", phrase));
        Assert.False(ContainsPhrase("CLOSED by round 12 §9A", phrase));
        Assert.False(ContainsPhrase("", phrase));
    }

    // ---- readers ------------------------------------------------------------

    /// <summary>The repo-relative targets of README's documentation-index
    /// table — the `| [text](target) |` rows.</summary>
    private static IReadOnlyCollection<string> IndexedDocuments()
        => [.. Regex.Matches(Read("README.md"), @"^\| \[[^\]]+\]\((?<target>[^)]+)\) \|", RegexOptions.Multiline)
            .Select(m => m.Groups["target"].Value)];

    /// <summary>Every markdown file in the working tree, repo-relative with
    /// forward slashes — build output, git internals and agent worktrees
    /// excluded (they are not documents).</summary>
    private static IReadOnlyCollection<string> RepositoryMarkdown()
    {
        var root = RepoRoot();
        var skip = new[] { ".git", "bin", "obj", ".claude", "node_modules" };
        return
        [
            .. Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !f.Split('/').Any(segment => skip.Contains(segment)))
                .Where(f => f != "README.md")   // the index does not list itself
        ];
    }

    /// <summary>The "N Core / M App" figure a document states. Both docs write
    /// it in the same shape, which is what makes the cross-check possible at
    /// all.</summary>
    private static (int Core, int App) SuiteCounts(string document)
    {
        var match = Regex.Match(document, @"\*\*(?<core>\d+) Core / (?<app>\d+) App\*\*");
        Assert.True(match.Success, "no bolded \"N Core / M App\" suite figure found in the document");
        return (
            int.Parse(match.Groups["core"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["app"].Value, CultureInfo.InvariantCulture));
    }

    private static double WindowFixedWidthFromSource()
    {
        var source = Read(Path.Combine("src", "Falcon.App", "App.xaml.cs"));
        var match = Regex.Match(source, @"const\s+double\s+WindowFixedWidth\s*=\s*(?<value>[0-9]+(\.[0-9]+)?)\s*;");
        Assert.True(match.Success, "App.xaml.cs declares no WindowFixedWidth constant");
        return double.Parse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Read a document with its line endings NORMALISED to LF. Git
    /// checks these files out CRLF on Windows and LF elsewhere, so a reader
    /// that kept them would make every pin below depend on the clone rather
    /// than on the document (P6 audit round 1). The two regex readers rely on
    /// this too: <c>^…$</c> in multiline mode and the bolded suite-count shape
    /// both behave the same either way once the text is normalised.</summary>
    private static string Read(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), "document missing: " + relative);
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string RepoRoot()
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
