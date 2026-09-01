using Falcon.App.Core.Cloning;

namespace Falcon.App.Tests;

/// <summary>
/// The identity transform (owner ruling R-A, plan-clone-field-round2 §3.2) — a
/// table of <see cref="SelfDisposition"/> rows, a REFUSAL that has an answer for
/// every shape of table, and a PURE, TOTAL <c>Apply</c> over the ones it
/// accepted.
///
/// <para>Three properties are under test throughout. <b>Every case has one
/// answer</b>: §3.2's input-contract table gets one test per row with its
/// refusal pinned, in the table's own order. <b>Nothing is silent</b>: every row
/// the transform cannot carry is a DROP and every role it changed is a ROLE
/// CHANGE (I-6). <b>All-Keep changed nothing</b>: the output is byte-identical
/// to `main`'s no-identity write, from a captured fixture.</para>
/// </summary>
public class CloneSwapTests
{
    /// <summary>The round-11 book, UNCHANGED — three-character selfs, so it is
    /// also the scan-gate fixture, and it is the book the byte-identity fixture
    /// below was captured from on `main`. Do not edit it: the fixture would
    /// stop meaning what it says.</summary>
    internal static CloneFile Book()
    {
        var file = CloneFileTests.Complete();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        file.Selfs.Add(new CloneAddress { Name = "ZZZ", Group = 0 });
        file.Selfs.Add(new CloneAddress { Name = "TST", Group = 1 });
        file.Individuals.Add(new CloneAddress { Name = "AAA", Group = 1, AssociatedSelf = "TST" });
        file.Individuals.Add(new CloneAddress { Name = "BOB", Group = 2, AssociatedSelf = "ZZZ" });
        file.Nets.Add(new CloneNet
        { Name = "NT1", Group = 1, AssociatedSelf = "TST", Members = ["AAA", "TST"] });
        file.Nets.Add(new CloneNet
        { Name = "NET2", Group = 2, AssociatedSelf = "ZZZ", Members = ["BOB"] });
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "ZZZ", Interval = "03:00", Start = "13:02" });
        file.Schedules.Add(new CloneSchedule
        { Kind = "EXCHANGE", Address = "BOB", Interval = "01:00", Start = "22:34" });
        return file;
    }

    /// <summary>
    /// The FIELD's shape (the 2026-08-21 clone): several selfs, one of them the
    /// scan-gate self <c>HOS</c>, individuals hanging off two different selfs,
    /// nets with membership, and schedules pointing at both a self and an
    /// individual — so a table has something of every re-point kind to move.
    /// </summary>
    internal static CloneFile Roster()
    {
        var file = CloneFileTests.Complete();
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        file.Selfs.Add(new CloneAddress { Name = "BASE", Group = 0 });
        file.Selfs.Add(new CloneAddress { Name = "ALPHA", Group = 1 });
        file.Selfs.Add(new CloneAddress { Name = "HOS", Group = 2 });
        file.Individuals.Add(new CloneAddress { Name = "KC1HAS", Group = 1, AssociatedSelf = "ALPHA" });
        file.Individuals.Add(new CloneAddress { Name = "KG6KMJ", Group = 3, AssociatedSelf = "BASE" });
        file.Individuals.Add(new CloneAddress { Name = "N7BOI", Group = 2, AssociatedSelf = "HOS" });
        file.Nets.Add(new CloneNet
        { Name = "NETA", Group = 1, AssociatedSelf = "ALPHA", Members = ["N7BOI", "ALPHA"] });
        file.Nets.Add(new CloneNet
        { Name = "NETB", Group = 0, AssociatedSelf = "BASE", Members = ["KG6KMJ"] });
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "BASE", Interval = "03:00", Start = "13:02" });
        file.Schedules.Add(new CloneSchedule
        { Kind = "EXCHANGE", Address = "KG6KMJ", Interval = "01:00", Start = "22:34" });
        return file;
    }

    /// <summary>
    /// THE OWNER'S OWN FILL, verbatim from the clone the app read off his
    /// radio on 2026-08-21 (`falcon-clone-20260821-165147.falconclone.json`:
    /// selfs HOS/W6HOS/W6HOS1 in that order, five individuals hanging off the
    /// two long selfs, two nets with EMPTY member lists, two SOUND schedules).
    /// This is the file round 15 §13 is about, so the phase-C gate runs on it
    /// rather than on a shape invented for the test.
    /// </summary>
    internal static CloneFile OwnerFill()
    {
        var file = CloneFileTests.Complete();
        // The whole 100-slot inventory, because this fixture stands for a file
        // the WRITE GATE accepts: since round 17 F6 a `Read` channel domain
        // with a short dump is DOWNGRADED at load (the pre-fix truncation).
        CloneFileTests.FillChannels(file);
        file.Selfs.Clear();
        file.Individuals.Clear();
        file.Nets.Clear();
        file.Schedules.Clear();
        file.Selfs.Add(new CloneAddress { Name = "HOS", Group = 0 });
        file.Selfs.Add(new CloneAddress { Name = "W6HOS", Group = 2 });
        file.Selfs.Add(new CloneAddress { Name = "W6HOS1", Group = 1 });
        file.Individuals.Add(new CloneAddress { Name = "KI6EZA1", Group = 2, AssociatedSelf = "W6HOS" });
        file.Individuals.Add(new CloneAddress { Name = "KC1HAS", Group = 2, AssociatedSelf = "W6HOS" });
        file.Individuals.Add(new CloneAddress { Name = "KG6KMJ", Group = 2, AssociatedSelf = "W6HOS" });
        file.Individuals.Add(new CloneAddress { Name = "N7BOI", Group = 1, AssociatedSelf = "W6HOS1" });
        file.Individuals.Add(new CloneAddress { Name = "N5PWU", Group = 1, AssociatedSelf = "W6HOS1" });
        file.Nets.Add(new CloneNet { Name = "HFN", Group = 1, AssociatedSelf = "W6HOS1" });
        file.Nets.Add(new CloneNet { Name = "HFL", Group = 2, AssociatedSelf = "W6HOS" });
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "W6HOS", Interval = "01:00", Start = "16:30" });
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "W6HOS1", Interval = "01:00", Start = "16:35" });
        return file;
    }

    /// <summary>THE one helper every caller of the table uses — including the
    /// ~50 <c>WriteAsync</c> sites, which all want ALL-KEEP.</summary>
    internal static IReadOnlyList<SelfDisposition> Rows(params SelfDisposition[] rows) => rows;

    internal static SelfDisposition Keep(string self) => new(self, SelfDispositionKind.Keep, null);

    internal static SelfDisposition Swap(string self, string individual)
        => new(self, SelfDispositionKind.SwapWithIndividual, individual);

    internal static SelfDisposition Replace(string self, string name)
        => new(self, SelfDispositionKind.Replace, name);

    private static IReadOnlyList<string> Names(IEnumerable<CloneAddress> rows) => [.. rows.Select(r => r.Name)];

    // =========================================================================
    // §3.2's INPUT-CONTRACT TABLE — one test per row, in the table's own order,
    // with the refusal PINNED. First refusal in table order wins, so a table
    // with several faults always names the same one.
    // =========================================================================

    [Fact]
    public void Row0_ASelfWithNoRow_IsKept_AndAnEmptyTableIsAllKeep()
    {
        // Omitted rows imply Keep — which is what makes the empty table exactly
        // round 11's no-identity write.
        Assert.Null(CloneSwap.Refusal(Roster(), Rows()));
        Assert.Null(CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", "KC1HAS"))));

        var result = CloneSwap.Apply(Roster(), Rows(Keep("BASE")));
        Assert.Equal(["BASE", "ALPHA", "HOS"], Names(result.File.Selfs));
        Assert.Empty(result.RoleChanges);
    }

    [Fact]
    public void Row1_ARowNamingSomethingThatIsNotASelf_IsRefused()
    {
        Assert.Equal(
            "KC1HAS is not a self in this file — every row belongs to one of the file's own selfs.",
            CloneSwap.Refusal(Roster(), Rows(Keep("KC1HAS"))));

        // …and the blank name is the SYNTHETIC row's, which a file WITH selfs
        // does not offer.
        Assert.Equal(
            "A disposition row names no self at all — every row belongs to one of the file's own selfs.",
            CloneSwap.Refusal(Roster(), Rows(Replace("", "NEW"))));
    }

    [Fact]
    public void Row2_TwoRowsForTheSameSelf_AreRefused()
    {
        // Normalized, the radio's own way: " base " and "BASE" are one self.
        Assert.Equal(
            "BASE has more than one disposition — each self takes exactly one.",
            CloneSwap.Refusal(Roster(), Rows(Keep("BASE"), Replace(" base ", "NEW"))));
    }

    [Fact]
    public void Row3_ADispositionThisAppDoesNotOffer_IsRefused()
    {
        var bogus = new SelfDisposition("BASE", (SelfDispositionKind)77, "NEW");
        Assert.Equal(
            "BASE was given a disposition this app does not offer.",
            CloneSwap.Refusal(Roster(), Rows(bogus)));
    }

    [Fact]
    public void Row4_KeepThatAlsoNamesACounterpart_IsRefused()
    {
        // Not "ignore the counterpart": a row that says two things is a row
        // whose meaning nobody can explain to the operator.
        Assert.Equal(
            "BASE is kept, so it cannot also name a replacement.",
            CloneSwap.Refusal(Roster(), Rows(new SelfDisposition("BASE", SelfDispositionKind.Keep, "NEW"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Row5_SwapOrReplaceWithNothingChosen_IsRefused(string? blank)
    {
        Assert.Equal(
            "ALPHA: choose the individual to swap it with.",
            CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", blank!))));
        Assert.Equal(
            "ALPHA: type the new name that takes its place.",
            CloneSwap.Refusal(Roster(), Rows(Replace("ALPHA", blank!))));
    }

    [Theory]
    [InlineData("BAD NAME")]
    [InlineData("CAM!")]
    [InlineData("TOOLONGADDRESSXX")]
    public void Row6_ACounterpartTheRadioCannotStore_IsRefused_CheckedOnTheNormalizedValue(string typed)
    {
        // Checked on the NORMALIZED value, because that is what gets stored —
        // and the normalization is what makes " cam " legal in the first place.
        Assert.Equal(
            $"{CloneFile.Normalize(typed)} is not a name this radio can store — "
            + "an ALE name is 1-15 letters or digits.",
            CloneSwap.Refusal(Roster(), Rows(Replace("ALPHA", typed))));

        Assert.Null(CloneSwap.Refusal(Roster(), Rows(Replace("ALPHA", " newname "))));
    }

    [Fact]
    public void Row7_SwappingTheScanGateSelf_IsRefused_D2()
    {
        // HOS is the 1-3 character self: Replace only. This is the exact loss
        // the live clone produced, turned into a refusal.
        Assert.Equal(
            "HOS is the scan-gate self — it can only be given a new name, not swapped with an individual.",
            CloneSwap.Refusal(Roster(), Rows(Swap("HOS", "KC1HAS"))));
    }

    [Fact]
    public void Row8_ReplacingTheScanGateSelfWithALongerName_IsRefused_D2()
    {
        Assert.Equal(
            "HOSTS is too long for HOS — the scan-gate self takes a 1-3 character name.",
            CloneSwap.Refusal(Roster(), Rows(Replace("HOS", "HOSTS"))));

        // …and the boundary: three characters is accepted.
        Assert.Null(CloneSwap.Refusal(Roster(), Rows(Replace("HOS", "HO2"))));
    }

    [Fact]
    public void Row9_ASwapCounterpartThatIsNotAnIndividual_IsRefused_MatchedCaseNormalized()
    {
        Assert.Equal(
            "GHOST is not an individual in this file — a swap takes one of the file's own individuals.",
            CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", "GHOST"))));

        // A SELF is not swap material either — it is already in a slot.
        Assert.Equal(
            "BASE is not an individual in this file — a swap takes one of the file's own individuals.",
            CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", "BASE"))));

        // ANTI-VACUITY: the match is case-normalized, so a typed lowercase
        // individual IS found.
        Assert.Null(CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", " kc1has "))));
    }

    [Fact]
    public void Row10_ACounterpartThatIsAlreadyANet_IsRefused()
    {
        // ALE names are unique across selfs, individuals AND nets, so the
        // transform would emit the name twice and the radio would refuse the
        // second one MID-BOOK, after the erase (P6 audit round 1).
        Assert.Equal(
            "NETA is already a net in this file — an ALE name is unique across selfs, "
            + "individuals and nets, so it cannot also be this radio's self.",
            CloneSwap.Refusal(Roster(), Rows(Replace("ALPHA", " neta "))));
    }

    [Fact]
    public void Row11_TheSameCounterpartOnTwoRows_IsRefused()
    {
        Assert.Equal(
            "NEW was chosen for two selfs — one name can only fill one slot.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "NEW"), Replace("ALPHA", " new "))));
    }

    /// <summary>
    /// The plan's contract ROW 14 (A-13) — the belt on the exchange. It is
    /// EVALUATED between row 11 and row 12, which is where the amended §3.2
    /// table puts it; the name keeps the plan's own label.
    ///
    /// <para>A net's member list must be identical in count, order and spelling
    /// on every station (manual §2.6.4.3.3) and the radio refuses a repeat with
    /// a duplicate-member refusal — after the wipe. The exchange makes a
    /// duplicate impossible by construction, so this only fires on a hand-edited
    /// file; it exists so nothing duplicate can reach the wire whatever produced
    /// the graph.</para>
    /// </summary>
    [Fact]
    public void Row14_ANetWhoseMemberListWouldHoldANameTwice_IsRefused_A13()
    {
        // A file no radio produced: NETA already lists KC1HAS twice.
        var file = Roster();
        var neta = Assert.Single(file.Nets, n => n.Name == "NETA");
        neta.Members.Clear();
        neta.Members.AddRange(["KC1HAS", "kc1has"]);

        Assert.Equal(
            "Net NETA would list KC1HAS twice after the swap.",
            CloneSwap.Refusal(file, Rows()));

        // …and it is judged AFTER the transform, not before: this list holds no
        // repeat until the table is applied.
        var renamed = Roster();
        Assert.Single(renamed.Nets, n => n.Name == "NETB").Members.Add("BASE");
        Assert.Null(CloneSwap.Refusal(renamed, Rows()));
        Assert.Equal(
            "Net NETB would list KG6KMJ twice after the swap.",
            CloneSwap.Refusal(renamed, Rows(Replace("BASE", "KG6KMJ"))));
    }

    [Fact]
    public void Row12_AReplacementStillPresentAfterTheTransform_IsRefused()
    {
        // The COMPLETE post-transform namespace, which no read of the source
        // alone can see: replacing BASE with ALPHA while ALPHA is ITSELF
        // replaced collides on the DEMOTED ALPHA.
        Assert.Equal(
            "ALPHA is already in this file's address book once the change is made — "
            + "an ALE name is unique across selfs, individuals and nets.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "ALPHA"), Replace("ALPHA", "OMEGA"))));

        // A KEPT self.
        Assert.Equal(
            "ALPHA is already in this file's address book once the change is made — "
            + "an ALE name is unique across selfs, individuals and nets.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "ALPHA"))));

        // An UNTOUCHED individual (swap it in instead — that is what Swap is for).
        Assert.Equal(
            "KC1HAS is already in this file's address book once the change is made — "
            + "an ALE name is unique across selfs, individuals and nets.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "KC1HAS"))));

        // The row's OWN self, which demotes into the individuals.
        Assert.Equal(
            "BASE is already in this file's address book once the change is made — "
            + "an ALE name is unique across selfs, individuals and nets.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "base"))));

        // ANTI-VACUITY: an individual that is being SWAPPED OUT of the
        // individuals leaves its name free for nobody else's row to reuse —
        // but the name is still spoken for, and rule 11 says so first.
        Assert.Equal(
            "KC1HAS was chosen for two selfs — one name can only fill one slot.",
            CloneSwap.Refusal(Roster(), Rows(Swap("ALPHA", "KC1HAS"), Replace("BASE", "KC1HAS"))));
    }

    [Fact]
    public void Row13_TheNoSelfFile_TakesExactlyOneSyntheticReplaceRow_A6()
    {
        var empty = Roster();
        empty.Selfs.Clear();

        // No rows at all is the standing preflight rejection — an instruction,
        // not a dead end.
        Assert.Equal(CloneService.NoSelfRejection, CloneSwap.Refusal(empty, Rows()));
        // …and so is the synthetic row with nothing typed into it.
        Assert.Equal(CloneService.NoSelfRejection, CloneSwap.Refusal(empty, Rows(Keep(""))));

        // A swap has nothing to swap WITH — there is no slot yet.
        Assert.Equal(
            "This file has no self — the one row must give this radio a new name.",
            CloneSwap.Refusal(empty, Rows(Swap("", "KC1HAS"))));
        // Two rows is not the shape either — and because every row of a no-self
        // file names the SAME (blank) self, row 2 is what says so first.
        Assert.Equal(
            "The radio's new self has more than one disposition — each self takes exactly one.",
            CloneSwap.Refusal(empty, Rows(Replace("", "NEW"), Replace("", "OTHER"))));

        // The one shape it does take.
        Assert.Null(CloneSwap.Refusal(empty, Rows(Replace("", "NEW"))));
        // …and a row naming a self this file does not have is row 1's business.
        Assert.Equal(
            "BASE is not a self in this file — every row belongs to one of the file's own selfs.",
            CloneSwap.Refusal(empty, Rows(Replace("BASE", "NEW"))));
    }

    [Fact]
    public void ARefusedTable_ThrowsBeforeAnyChange_I4()
    {
        var source = Roster();
        var ex = Assert.Throws<CloneValueException>(
            () => CloneSwap.Apply(source, Rows(Swap("HOS", "KC1HAS"))));
        Assert.Equal(CloneSwap.Refusal(source, Rows(Swap("HOS", "KC1HAS"))), ex.Message);
        Assert.Equal(["BASE", "ALPHA", "HOS"], Names(source.Selfs));
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("HOS", true)]
    [InlineData("hos", true)]
    [InlineData("BASE", false)]
    [InlineData("KC1HAS", false)]
    public void TheScanGateSelf_IsOneToThreeCharacters_Normalized(string name, bool gate)
        => Assert.Equal(gate, CloneSwap.IsScanGateSelf(name));

    // =========================================================================
    // ALL-KEEP IS BYTE-IDENTICAL TO `main`
    // =========================================================================

    /// <summary>
    /// The phase's headline promise (§2 F2): an all-Keep table writes exactly
    /// what round 11's no-identity write wrote. The fixture is the output of
    /// <c>CloneSwap.Apply(Book(), "")</c> captured from `main` (548e627) BEFORE
    /// this phase touched <c>CloneSwap</c>, serialized with the file's own
    /// <c>Save()</c>; it is checked in at
    /// <c>tests/Falcon.App.Tests/Fixtures/clone-swap-all-keep-main.json</c>.
    ///
    /// <para>Newlines are normalized on both sides for one reason only: git owns
    /// the fixture file's line endings (<c>core.autocrlf</c>), and the
    /// transform's output does not. Every other byte is compared as-is.</para>
    ///
    /// <para><b>ONE amendment since the capture</b> (round 17,
    /// plan-clone-write-structural.md D4/D6): the file format grew
    /// <c>DefaultChannelsElided</c>, so the fixture grew the one line
    /// <c>"DefaultChannelsElided": false</c> in the serializer's own position.
    /// FALSE is the whole point of the amendment — a legacy book carries no
    /// elision, so the transform's output is byte-identical to `main`'s
    /// EXCEPT for a marker whose value says "nothing was elided". The pin's
    /// claim is unchanged; a copy-through that lost the marker, or one that
    /// invented <c>true</c>, still fails here.</para>
    /// </summary>
    [Fact]
    public void AnAllKeepTable_IsByteIdenticalToMainsNoIdentityWrite()
    {
        var result = CloneSwap.Apply(Book(), Rows());

        Assert.Equal(Normalize(MainAllKeepFixture()), Normalize(result.File.Save()));
        Assert.Empty(result.Drops);
        Assert.Empty(result.RoleChanges);

        // ANTI-VACUITY: the fixture is a real book, not an empty file, and an
        // actual change really does move it away from the fixture.
        Assert.Contains("\"ZZZ\"", MainAllKeepFixture(), StringComparison.Ordinal);
        var changed = CloneSwap.Apply(Book(), Rows(Replace("ZZZ", "QQQ")));
        Assert.NotEqual(Normalize(MainAllKeepFixture()), Normalize(changed.File.Save()));
    }

    [Fact]
    public void AnOmittedRowAndAnExplicitKeep_ProduceTheSameFile()
    {
        var omitted = CloneSwap.Apply(Book(), Rows());
        var explicitKeep = CloneSwap.Apply(Book(), Rows(Keep("ZZZ"), Keep("TST")));
        Assert.Equal(omitted.File.Save(), explicitKeep.File.Save());
        Assert.Empty(explicitKeep.RoleChanges);
    }

    // =========================================================================
    // THE TRANSFORM, STEP BY STEP (§3.2 steps 1-5)
    // =========================================================================

    [Fact]
    public void Step2_EachSlotKeepsItsPosition_AndItsOccupantsGroupRule()
    {
        // The primary stays whichever self was FIRST — a swap no longer
        // reorders the book, which is what silently demoted HOS on `main`.
        var result = CloneSwap.Apply(Roster(), Rows(
            Swap("ALPHA", "KC1HAS"),
            Replace("HOS", "HQ")));

        Assert.Equal(["BASE", "KC1HAS", "HQ"], Names(result.File.Selfs));
        // A swapped-in individual keeps its OWN channel group…
        Assert.Equal(1, result.File.Selfs[1].Group);         // KC1HAS' own group
        // …a typed name INHERITS the slot's.
        Assert.Equal(2, result.File.Selfs[2].Group);         // HOS' group
        // A self carries no association of its own.
        Assert.All(result.File.Selfs, s => Assert.Null(s.AssociatedSelf));
    }

    [Fact]
    public void Step3_TheSwappedInIndividualLeaves_AndTheOldSelfJoins_InRowOrder()
    {
        // ROUND 15 C-2 re-cast this case: the demotion half is shown with a
        // LONG self (BASE), because a scan-gate self is no longer demoted at
        // all (its own pin is below).
        var result = CloneSwap.Apply(Roster(), Rows(
            Replace("BASE", "NEWBASE"),
            Swap("ALPHA", "KC1HAS")));

        // Survivors first, in source order; then one demoted self per row, in
        // ROW order (BASE's row came first above, so BASE is appended first).
        Assert.Equal(["KG6KMJ", "N7BOI", "BASE", "ALPHA"], Names(result.File.Individuals));

        var @base = Assert.Single(result.File.Individuals, i => i.Name == "BASE");
        Assert.Equal(0, @base.Group);                    // its own group, kept
        Assert.Equal("NEWBASE", @base.AssociatedSelf);   // associated to its replacement

        var alpha = Assert.Single(result.File.Individuals, i => i.Name == "ALPHA");
        Assert.Equal(1, alpha.Group);
        Assert.Equal("KC1HAS", alpha.AssociatedSelf);
    }

    /// <summary>
    /// C-2 (owner rule 3, 2026-08-22): "for the 3-letter self there should NOT
    /// be an individual created that is associated with it". The scan-gate
    /// self's Replace writes the new name into the slot and the old name
    /// LEAVES THE BOOK — it is the radio's scan gate, not a station.
    /// </summary>
    [Fact]
    public void Step3_TheScanGateSelfsReplace_DropsTheOldSelf_C2()
    {
        var result = CloneSwap.Apply(Roster(), Rows(Replace("HOS", "HQ")));

        Assert.Equal(["BASE", "ALPHA", "HQ"], Names(result.File.Selfs));
        Assert.Equal(2, Assert.Single(result.File.Selfs, s => s.Name == "HQ").Group);   // the slot's group

        // The point: NO address named after the old scan-gate self survives,
        // in any of the three kinds.
        Assert.DoesNotContain(result.File.Individuals, i => i.Name == "HOS");
        Assert.DoesNotContain(result.File.Selfs, s => s.Name == "HOS");
        Assert.DoesNotContain(result.File.Nets, n => n.Name == "HOS");

        // …and `map` still re-points what named it: N7BOI hung off HOS.
        Assert.Equal("HQ", Assert.Single(result.File.Individuals, i => i.Name == "N7BOI").AssociatedSelf);

        // ANTI-VACUITY: the same table on a LONG self still demotes.
        Assert.Contains(
            CloneSwap.Apply(Roster(), Rows(Replace("BASE", "NEWBASE"))).File.Individuals,
            i => i.Name == "BASE");
    }

    /// <summary>The dropped name is really FREE afterwards (C-D2): the refusal
    /// counts the book AFTER the transform, so a table that re-uses the old
    /// scan-gate name for another slot is accepted — and produces no
    /// duplicate.</summary>
    [Fact]
    public void Step3_TheDroppedScanGateName_IsFreeForAnotherSlot_CD2()
    {
        var rows = Rows(Replace("HOS", "HQ"), Replace("BASE", "HOS"));
        Assert.Null(CloneSwap.Refusal(Roster(), rows));

        var result = CloneSwap.Apply(Roster(), rows);
        Assert.Equal(["HOS", "ALPHA", "HQ"], Names(result.File.Selfs));

        var everyName = result.File.Selfs.Select(s => s.Name)
            .Concat(result.File.Individuals.Select(i => i.Name))
            .Concat(result.File.Nets.Select(n => n.Name))
            .ToList();
        Assert.Equal(everyName.Count, everyName.Distinct(StringComparer.Ordinal).Count());

        // ANTI-VACUITY: re-using a name that IS still in the book is refused.
        Assert.Equal(
            "ALPHA is already in this file's address book once the change is made — "
            + "an ALE name is unique across selfs, individuals and nets.",
            CloneSwap.Refusal(Roster(), Rows(Replace("BASE", "ALPHA"))));
    }

    [Fact]
    public void Step4_EveryAssociationNamingAnOldSelf_RePoints_OnIndividualsNetsMembersAndSchedules()
    {
        var result = CloneSwap.Apply(Roster(), Rows(
            Swap("ALPHA", "KC1HAS"),
            Replace("BASE", "NEWBASE")));

        // …on an INDIVIDUAL's AssociatedSelf (N7BOI still hangs off HOS, kept).
        Assert.Equal("NEWBASE", Assert.Single(result.File.Individuals, i => i.Name == "KG6KMJ").AssociatedSelf);
        Assert.Equal("HOS", Assert.Single(result.File.Individuals, i => i.Name == "N7BOI").AssociatedSelf);

        // …on a NET's AssociatedSelf.
        Assert.Equal("KC1HAS", Assert.Single(result.File.Nets, n => n.Name == "NETA").AssociatedSelf);
        Assert.Equal("NEWBASE", Assert.Single(result.File.Nets, n => n.Name == "NETB").AssociatedSelf);

        // …on a net MEMBER (NETA's member ALPHA was the net's own associated
        // self and still is, now spelled KC1HAS — so it survives the drop rule;
        // the individual member N7BOI is verbatim).
        Assert.Equal(["N7BOI", "KC1HAS"], Assert.Single(result.File.Nets, n => n.Name == "NETA").Members);

        // …and on a SCHEDULE address.
        Assert.Equal("NEWBASE", Assert.Single(result.File.Schedules, s => s.Kind == "SOUND").Address);
        Assert.Equal("KG6KMJ", Assert.Single(result.File.Schedules, s => s.Kind == "EXCHANGE").Address);
    }

    [Fact]
    public void Step4_TheTransformIsSIMULTANEOUS_NotRowByRow()
    {
        // Two rows that trade places. Row by row, the second row would see the
        // FIRST row's output and re-point ALPHA's rows onto BASE's new name; a
        // snapshot transform gives each row exactly what the file said.
        var result = CloneSwap.Apply(Roster(), Rows(
            Replace("BASE", "ONE"),
            Replace("ALPHA", "TWO")));

        Assert.Equal(["ONE", "TWO", "HOS"], Names(result.File.Selfs));
        Assert.Equal("ONE", Assert.Single(result.File.Individuals, i => i.Name == "KG6KMJ").AssociatedSelf);
        Assert.Equal("TWO", Assert.Single(result.File.Individuals, i => i.Name == "KC1HAS").AssociatedSelf);
        Assert.Equal("ONE", Assert.Single(result.File.Individuals, i => i.Name == "BASE").AssociatedSelf);
        Assert.Equal("TWO", Assert.Single(result.File.Individuals, i => i.Name == "ALPHA").AssociatedSelf);
        Assert.Equal("TWO", Assert.Single(result.File.Nets, n => n.Name == "NETA").AssociatedSelf);
    }

    /// <summary>
    /// THE AUDIT ROUND-1 BLOCKER (A-13), as its own fixture. A net that lists
    /// BOTH the self being swapped out and the individual being swapped in used
    /// to collapse to <c>[KC1HAS, KC1HAS]</c> under a one-way rename — and leg 7
    /// then sent the member write TWICE, which the radio refuses as a duplicate,
    /// AFTER the erase, on a half-written radio.
    ///
    /// <para>The two names EXCHANGE SLOTS instead: count, order and spelling
    /// survive (manual §2.6.4.3.3), and the list still says what it meant — this
    /// radio is now KC1HAS, and the station that was ALPHA is an individual
    /// sitting in ALPHA's old slot.</para>
    /// </summary>
    [Fact]
    public void Step4_ASwapEXCHANGESTheTwoNamesInAMemberList_A13()
    {
        var file = Roster();
        var source = Assert.Single(file.Nets, n => n.Name == "NETA");
        source.Members.Clear();
        source.Members.AddRange(["KC1HAS", "ALPHA"]);

        var result = CloneSwap.Apply(file, Rows(Swap("ALPHA", "KC1HAS")));

        // X takes P's slot and P takes X's slot — position for position.
        var net = Assert.Single(result.File.Nets, n => n.Name == "NETA");
        Assert.Equal(["ALPHA", "KC1HAS"], net.Members);
        Assert.Equal("KC1HAS", net.AssociatedSelf);      // the ASSOCIATION renames, one-way
        // Nothing is lost and nothing repeats.
        Assert.Equal(2, net.Members.Count);
        Assert.Empty(result.Drops);
    }

    [Fact]
    public void Step4_TheExchangeCoversOneSidedListsAndSchedulesToo_A13()
    {
        // A net that lists only P becomes [X]…
        var onlyP = Roster();
        var netP = Assert.Single(onlyP.Nets, n => n.Name == "NETA");
        netP.Members.Clear();
        netP.Members.Add("ALPHA");
        Assert.Equal(["KC1HAS"],
            Assert.Single(CloneSwap.Apply(onlyP, Rows(Swap("ALPHA", "KC1HAS"))).File.Nets,
                n => n.Name == "NETA").Members);

        // …and one that lists only X becomes [P].
        var onlyX = Roster();
        var netX = Assert.Single(onlyX.Nets, n => n.Name == "NETA");
        netX.Members.Clear();
        netX.Members.Add("KC1HAS");
        Assert.Equal(["ALPHA"],
            Assert.Single(CloneSwap.Apply(onlyX, Rows(Swap("ALPHA", "KC1HAS"))).File.Nets,
                n => n.Name == "NETA").Members);

        // SCHEDULE addresses exchange the same way — and because they do, the
        // KINDS are preserved: the sounding still names a self and the exchange
        // still names another station, so neither is dropped.
        var scheduled = Roster();
        scheduled.Schedules.Clear();
        scheduled.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "ALPHA", Interval = "03:00", Start = "13:02" });
        scheduled.Schedules.Add(new CloneSchedule
        { Kind = "EXCHANGE", Address = "KC1HAS", Interval = "01:00", Start = "22:34" });

        var result = CloneSwap.Apply(scheduled, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.Equal("KC1HAS", Assert.Single(result.File.Schedules, s => s.Kind == "SOUND").Address);
        Assert.Equal("ALPHA", Assert.Single(result.File.Schedules, s => s.Kind == "EXCHANGE").Address);
        Assert.Empty(result.Drops);
    }

    [Fact]
    public void Step5_TheDropRulesStillRunAfterTheExchange()
    {
        // A member row the radio would refuse — a self that is not the net's own
        // associated self — is still dropped, and the exchange does not rescue
        // it. NETB hangs off BASE and (in this hand-edited file) lists ALPHA,
        // which the swap turns into the self KC1HAS.
        var file = Roster();
        Assert.Single(file.Nets, n => n.Name == "NETB").Members.Add("ALPHA");

        var result = CloneSwap.Apply(file, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.Equal(["KG6KMJ"], Assert.Single(result.File.Nets, n => n.Name == "NETB").Members);
        var drop = Assert.Single(result.Drops, d => d.Contains("Member KC1HAS of net NETB", StringComparison.Ordinal));
        Assert.Contains("own associated self", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void Step5_AScheduleWhoseTargetChangedKind_IsDroppedAndListed()
    {
        // A hand-edited SOUND against an INDIVIDUAL: soundings run from a self,
        // so the rule drops it — and it still runs after the exchange.
        var file = Roster();
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "KG6KMJ", Interval = "02:00", Start = "01:00" });

        var result = CloneSwap.Apply(file, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.Contains(result.Drops, d => d.Contains("Schedule SOUND KG6KMJ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.File.Schedules, s => s.Address == "KG6KMJ" && s.Kind == "SOUND");

        // …and a REPLACE still RENAMES a schedule that names the old self, which
        // is what keeps a renamed self's sounding rather than reporting it lost.
        var renamed = CloneSwap.Apply(Roster(), Rows(Replace("BASE", "NEWBASE")));
        Assert.Equal("NEWBASE", Assert.Single(renamed.File.Schedules, s => s.Kind == "SOUND").Address);
    }

    [Fact]
    public void TheNoSelfFile_GetsOneSelfInGroupZero_AndNothingDemotes()
    {
        var file = Roster();
        file.Selfs.Clear();
        // Every association is now dangling — the blank-assoc rule is what
        // makes this total.
        foreach (var net in file.Nets) net.AssociatedSelf = null;
        foreach (var individual in file.Individuals) individual.AssociatedSelf = null;

        var result = CloneSwap.Apply(file, Rows(Replace("", " new ")));

        var self = Assert.Single(result.File.Selfs);
        Assert.Equal("NEW", self.Name);           // stored normalized
        Assert.Equal(0, self.Group);
        Assert.DoesNotContain(result.File.Individuals, i => i.Name == "NEW");
        // Both nets had no self and neither was re-pointed, so both are gone —
        // loudly.
        Assert.Empty(result.File.Nets);
        Assert.Equal(2, result.Drops.Count(d => d.Contains("no associated self", StringComparison.Ordinal)));
    }

    // =========================================================================
    // ROUND 15 PHASE C — the identity step per NET, on the OWNER'S OWN FILL
    // (§13.3, §13.5's gate). One radio holds a net; cloning to the next radio
    // promotes one of THAT net's individuals to be its self.
    // =========================================================================

    [Fact]
    public void C1_TheCandidatesForAnetsSelf_AreThatNetsOwnIndividuals()
    {
        var file = OwnerFill();

        // HFL's self: the three individuals associated with it, in file order.
        Assert.Equal(
            ["KI6EZA1", "KC1HAS", "KG6KMJ"],
            CloneSwap.SwapCandidates(file, "W6HOS").Select(c => c.Name));
        // HFN's self: the other net's two — never the first net's.
        Assert.Equal(
            ["N7BOI", "N5PWU"],
            CloneSwap.SwapCandidates(file, "W6HOS1").Select(c => c.Name));
        // The scan-gate self is Replace-only, so it offers none at all (D2).
        Assert.Empty(CloneSwap.SwapCandidates(file, "HOS"));
        // …and so does the synthetic no-self row (A-6).
        Assert.Empty(CloneSwap.SwapCandidates(file, ""));
    }

    [Fact]
    public void C1_AnIndividualMemberOfTheNet_IsACandidateEvenWithoutTheAssociation()
    {
        // The owner's fill has no member lists at all, so this is the general
        // case: a net that LISTS an individual offers it too, once, after the
        // associated ones — and a SELF member is not an individual and is not
        // offered.
        var file = OwnerFill();
        var hfl = Assert.Single(file.Nets, n => n.Name == "HFL");
        hfl.Members.AddRange(["KC1HAS", "N7BOI", "W6HOS"]);

        Assert.Equal(
            ["KI6EZA1", "KC1HAS", "KG6KMJ", "N7BOI"],
            CloneSwap.SwapCandidates(file, "W6HOS").Select(c => c.Name));
    }

    [Fact]
    public void C1_ASwapWithAnotherNetsIndividual_IsRefused_NamingThatNet()
    {
        Assert.Equal(
            "N7BOI belongs to net HFN — W6HOS can only be swapped with one of its own net's individuals.",
            CloneSwap.Refusal(OwnerFill(), Rows(Swap("W6HOS", "N7BOI"))));

        // The row's OWN individual is accepted — the rule is a scope, not a ban.
        Assert.Null(CloneSwap.Refusal(OwnerFill(), Rows(Swap("W6HOS", "KC1HAS"))));

        // A self whose individuals hang off no net at all names no net.
        var netless = OwnerFill();
        netless.Nets.Clear();
        Assert.Equal(
            "N7BOI is not one of W6HOS's own individuals — a swap takes an individual associated with this self.",
            CloneSwap.Refusal(netless, Rows(Swap("W6HOS", "N7BOI"))));
    }

    /// <summary>
    /// THE OWNER'S CASE, end to end: this radio becomes KC1HAS for net HFL.
    /// The promoted individual takes the slot with its own group, the old self
    /// becomes one of its individuals, the net hangs off the new name and the
    /// sounding schedule is re-addressed — §13.5's gate, clause by clause.
    /// </summary>
    [Fact]
    public void C1_TheOwnersSwap_MovesTheSlot_TheNet_AndTheSchedule()
    {
        var result = CloneSwap.Apply(OwnerFill(), Rows(Swap("W6HOS", "KC1HAS")));

        Assert.Equal(["HOS", "KC1HAS", "W6HOS1"], Names(result.File.Selfs));
        Assert.Equal(2, Assert.Single(result.File.Selfs, s => s.Name == "KC1HAS").Group);

        var demoted = Assert.Single(result.File.Individuals, i => i.Name == "W6HOS");
        Assert.Equal(2, demoted.Group);
        Assert.Equal("KC1HAS", demoted.AssociatedSelf);
        Assert.DoesNotContain(result.File.Individuals, i => i.Name == "KC1HAS");

        Assert.Equal("KC1HAS", Assert.Single(result.File.Nets, n => n.Name == "HFL").AssociatedSelf);
        Assert.Equal("W6HOS1", Assert.Single(result.File.Nets, n => n.Name == "HFN").AssociatedSelf);

        // The SOUND schedule that ran from W6HOS runs from this radio now.
        Assert.Equal("KC1HAS", result.File.Schedules[0].Address);
        Assert.Equal("W6HOS1", result.File.Schedules[1].Address);
        Assert.All(result.File.Schedules, s => Assert.Equal("SOUND", s.Kind));

        // Nothing was lost on the way.
        Assert.Empty(result.Drops);
        Assert.Equal(
            ["KC1HAS is now a self in W6HOS's place.", "W6HOS is now an individual of KC1HAS."],
            result.RoleChanges);
    }

    [Fact]
    public void C2_ReplacingTheOwnersScanGateSelf_LeavesNoAddressNamedHOS()
    {
        var result = CloneSwap.Apply(OwnerFill(), Rows(Replace("HOS", "ABC")));

        Assert.Equal(["ABC", "W6HOS", "W6HOS1"], Names(result.File.Selfs));
        Assert.Equal(0, Assert.Single(result.File.Selfs, s => s.Name == "ABC").Group);
        Assert.DoesNotContain(
            result.File.Selfs.Select(s => s.Name)
                .Concat(result.File.Individuals.Select(i => i.Name))
                .Concat(result.File.Nets.Select(n => n.Name)),
            name => string.Equals(name, "HOS", StringComparison.Ordinal));
        Assert.Equal(["ABC replaces HOS as the scan-gate self."], result.RoleChanges);
        Assert.Empty(result.Drops);
    }

    [Fact]
    public void C_AllKeepOnTheOwnersFill_ChangesNothing()
    {
        // The standing property, on this fixture too: an empty table is the
        // file itself, byte for byte.
        var source = OwnerFill();
        var result = CloneSwap.Apply(source, Rows());

        Assert.Equal(source.Save(), result.File.Save());
        Assert.Empty(result.Drops);
        Assert.Empty(result.RoleChanges);
    }

    // =========================================================================
    // ROLE CHANGES — exact cardinality per kind (§3.2)
    // =========================================================================

    [Fact]
    public void RoleChanges_Swap_AreExactlyTwoLines()
    {
        var result = CloneSwap.Apply(Roster(), Rows(Swap("ALPHA", "KC1HAS")));
        Assert.Equal(
            ["KC1HAS is now a self in ALPHA's place.", "ALPHA is now an individual of KC1HAS."],
            result.RoleChanges);
    }

    [Fact]
    public void RoleChanges_Replace_AreExactlyTwoLines()
    {
        var result = CloneSwap.Apply(Roster(), Rows(Replace("BASE", "newbase")));
        Assert.Equal(
            ["NEWBASE is the new self in BASE's place.", "BASE is now an individual of NEWBASE."],
            result.RoleChanges);
    }

    /// <summary>C-2: the scan-gate self's Replace is ONE line, because there
    /// is no demoted individual to report — the role that changed is the
    /// radio's own scan gate.</summary>
    [Fact]
    public void RoleChanges_TheScanGateReplace_IsExactlyOneLine_C2()
    {
        var result = CloneSwap.Apply(Roster(), Rows(Replace("HOS", "hq")));
        Assert.Equal(["HQ replaces HOS as the scan-gate self."], result.RoleChanges);
    }

    [Fact]
    public void RoleChanges_TheNoSelfRow_IsExactlyOneLine()
    {
        var file = Roster();
        file.Selfs.Clear();
        var result = CloneSwap.Apply(file, Rows(Replace("", "NEW")));
        Assert.Equal(["NEW is the radio's self."], result.RoleChanges);
    }

    [Fact]
    public void RoleChanges_Keep_AreNone_AndSeveralRowsReportInRowOrder()
    {
        Assert.Empty(CloneSwap.Apply(Roster(), Rows(Keep("BASE"), Keep("ALPHA"))).RoleChanges);

        var result = CloneSwap.Apply(Roster(), Rows(
            Replace("HOS", "HQ"),
            Keep("BASE"),
            Swap("ALPHA", "KC1HAS")));
        Assert.Equal(
            [
                "HQ replaces HOS as the scan-gate self.",          // C-2: one line, no demotion
                "KC1HAS is now a self in ALPHA's place.",
                "ALPHA is now an individual of KC1HAS.",
            ],
            result.RoleChanges);
    }

    [Fact]
    public void RoleChangesAreProse_NeverARadioToken_I5()
        => Assert.All(
            CloneSwap.Apply(Roster(), Rows(Swap("ALPHA", "KC1HAS"), Replace("HOS", "HQ"))).RoleChanges,
            line =>
            {
                Assert.DoesNotContain("SLFAD", line, StringComparison.Ordinal);
                Assert.DoesNotContain("INDAD", line, StringComparison.Ordinal);
                Assert.EndsWith(".", line, StringComparison.Ordinal);
            });

    // =========================================================================
    // Unreplayable state — the three drop rules, unchanged
    // =========================================================================

    [Fact]
    public void ANetWithABlankAssociatedSelf_IsDroppedAndListed()
    {
        var file = Book();
        file.Nets[0].AssociatedSelf = null;      // the primary-deletion artifact

        var result = CloneSwap.Apply(file, Rows());

        Assert.DoesNotContain(result.File.Nets, n => n.Name == "NT1");
        var drop = Assert.Single(result.Drops, d => d.Contains("NT1", StringComparison.Ordinal));
        Assert.Contains("no associated self", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankNetRescuedByTheRePoint_IsKept_AndNotListed()
    {
        // The rule is "UNLESS the re-point rescued it" — a net whose associated
        // self is being renamed is replayable, so it must NOT be dropped.
        var file = Roster();
        var result = CloneSwap.Apply(file, Rows(Replace("BASE", "NEWBASE")));

        Assert.Contains(result.File.Nets, n => n.Name == "NETB" && n.AssociatedSelf == "NEWBASE");
        Assert.DoesNotContain(result.Drops, d => d.Contains("NETB", StringComparison.Ordinal));
    }

    [Fact]
    public void AMemberThatWouldBeRefused_IsDroppedAndListed()
    {
        // Only a net's OWN associated self may be a member.
        var file = Book();
        file.Nets[1].Members.Add("TST");         // NET2's assoc self is ZZZ

        var result = CloneSwap.Apply(file, Rows());

        Assert.Equal(["AAA", "TST"], Assert.Single(result.File.Nets, n => n.Name == "NT1").Members);
        Assert.Equal(["BOB"], Assert.Single(result.File.Nets, n => n.Name == "NET2").Members);
        var drop = Assert.Single(result.Drops);
        Assert.Contains("Member TST of net NET2", drop, StringComparison.Ordinal);
        Assert.Contains("own associated self", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void AScheduleForAnAddressTheFileNoLongerHolds_IsDroppedAndListed()
    {
        var file = Book();
        file.Schedules.Add(new CloneSchedule
        { Kind = "EXCHANGE", Address = "GHOST", Interval = "01:00", Start = "00:00" });

        var result = CloneSwap.Apply(file, Rows());

        Assert.DoesNotContain(result.File.Schedules, s => s.Address == "GHOST");
        Assert.Contains(result.Drops, d =>
            d.Contains("GHOST", StringComparison.Ordinal)
            && d.Contains("address book", StringComparison.Ordinal));
    }

    // =========================================================================
    // The function's own properties
    // =========================================================================

    [Fact]
    public void TheTransformIsPure_TheInputIsNeverMutated()
    {
        var source = Roster();
        var beforeSelfs = Names(source.Selfs);
        var beforeMembers = source.Nets[0].Members.ToList();

        var result = CloneSwap.Apply(source, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.Equal(beforeSelfs, Names(source.Selfs));
        Assert.Equal(beforeMembers, source.Nets[0].Members);
        Assert.Equal(2, source.Schedules.Count);
        Assert.Equal(3, source.Individuals.Count);
        // …and the output really is a different object graph.
        Assert.NotSame(source.Nets[0], result.File.Nets[0]);
    }

    [Fact]
    public void TheTransformIsDeterministic_TheSameTableTwiceGivesTheSameFile()
    {
        var rows = Rows(Swap("ALPHA", "KC1HAS"), Replace("HOS", "HQ"));
        var first = CloneSwap.Apply(Roster(), rows);
        var second = CloneSwap.Apply(Roster(), rows);
        Assert.Equal(first.File.Save(), second.File.Save());
        Assert.Equal(first.RoleChanges, second.RoleChanges);
        Assert.Equal(first.Drops, second.Drops);
    }

    [Fact]
    public void TheDropReportIsComplete_EveryRowTheOutputLostIsNamed()
    {
        // The completeness property, asserted structurally rather than by
        // listing the cases again: for a file rigged with one of EVERY droppable
        // kind, the count of rows the transform removed equals the count of
        // drop lines.
        var file = Book();
        file.Nets[0].AssociatedSelf = null;                       // a blank-assoc net
        file.Nets[1].Members.Add("TST");                          // an invalid member
        file.Schedules.Add(new CloneSchedule
        { Kind = "EXCHANGE", Address = "GHOST", Interval = "01:00", Start = "00:00" }); // a dangling target

        var result = CloneSwap.Apply(file, Rows());

        int lostNets = file.Nets.Count - result.File.Nets.Count;
        int lostMembers = file.Nets.Sum(n => n.Members.Count)
            - result.File.Nets.Sum(n => n.Members.Count)
            - file.Nets[0].Members.Count;      // the dropped net took its own members with it
        int lostSchedules = file.Schedules.Count - result.File.Schedules.Count;

        Assert.Equal(1, lostNets);
        Assert.Equal(1, lostMembers);
        Assert.Equal(1, lostSchedules);
        Assert.Equal(lostNets + lostMembers + lostSchedules, result.Drops.Count);

        // …and R13: an operator-facing string never quotes a radio token.
        Assert.All(result.Drops, d => Assert.DoesNotContain("INV ", d, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE LOCKOUT DISPOSITION (plan-clone-round12 §6): the operator lockouts
    /// pass through IDENTITY-UNTOUCHED, and that is a DECISION rather than an
    /// omission — a swap changes which station this radio IS (book roles,
    /// associations, the rows that name them), and a front-panel lockout names
    /// none of those.
    ///
    /// <para>The rows are still DEEP-COPIED, because the transform is pure and
    /// may not share a list with its input — which is the half an "untouched"
    /// disposition is easiest to get wrong.</para>
    /// </summary>
    [Fact]
    public void TheLockouts_PassThroughUntouched_ButAreDeepCopied()
    {
        var file = Roster();
        var before = file.Lockouts!.Rows
            .Select(r => $"{r.Family}/{r.Section}/{r.Item}={r.State}")
            .ToList();

        // A REAL role swap, not the all-Keep branch.
        var result = CloneSwap.Apply(file, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.NotEqual(file.Selfs[1].Name, result.File.Selfs[1].Name);   // the swap really happened
        Assert.Equal(before,
            result.File.Lockouts!.Rows.Select(r => $"{r.Family}/{r.Section}/{r.Item}={r.State}"));
        Assert.Equal(file.Lockouts.State, result.File.Lockouts.State);
        Assert.DoesNotContain(result.Drops, d => d.Contains("ockout", StringComparison.Ordinal));

        // PURITY: a new list of new rows, so mutating the output cannot reach
        // back into the input.
        Assert.NotSame(file.Lockouts, result.File.Lockouts);
        var copied = result.File.Lockouts.Rows.First(r => r.State == "Unlock");
        var original = file.Lockouts.Rows.First(
            r => r.Family == copied.Family && r.Section == copied.Section && r.Item == copied.Item);
        Assert.NotSame(original, copied);
        copied.State = "Lock";
        Assert.Equal("Unlock", original.State);
    }

    /// <summary>
    /// D4/D6 — THE ELISION MARKER TRAVELS WITH THE ROWS IT DESCRIBES. The
    /// transform's output is the graph the write campaign REVALIDATES and then
    /// sends, so a copy that dropped the marker would turn a sparse file into
    /// one claiming 100 slots it does not have, and the preflight would refuse
    /// the operator's own read with a message about a defect that is not there.
    /// Pinned in BOTH directions, because a copy that hard-coded either value
    /// would pass a one-sided pin.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheElisionMarker_SurvivesTheTransform(bool elided)
    {
        var file = Roster();
        file.DefaultChannelsElided = elided;
        if (elided) file.Channels.RemoveAll(c => c.IsFactoryDefault());

        var result = CloneSwap.Apply(file, Rows(Swap("ALPHA", "KC1HAS")));

        Assert.NotEqual(file.Selfs[1].Name, result.File.Selfs[1].Name);   // a REAL swap ran
        Assert.Equal(elided, result.File.DefaultChannelsElided);
        // …and the graph the write preflight revalidates really does pass.
        result.File.Validate();
    }

    // ---- The captured fixture ------------------------------------------------

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string MainAllKeepFixture()
    {
        var path = Path.Combine(
            FindRepoRoot(), "tests", "Falcon.App.Tests", "Fixtures", "clone-swap-all-keep-main.json");
        Assert.True(File.Exists(path), "the captured `main` fixture is missing: " + path);
        return File.ReadAllText(path);
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
