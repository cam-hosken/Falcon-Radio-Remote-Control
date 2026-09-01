namespace Falcon.Core.Tests;

/// <summary>
/// Phase R "GUI out, backend in" guard (plan-gui-rejigger.md round 4).
/// The Core now HAS builders for ALE fill editing (E2), HOP net programming
/// (E3), crypto (E1), diagnostics (E5), and the transmit-gated hazards —
/// this REPLACES the old Stage 1 "no builders exist" pins. The flipped
/// guard, in the app-layer RawCommand-guard style (source scan — reflection
/// cannot see call sites, and a scan also covers XAML): NO file in
/// src/Falcon.App.Core or src/Falcon.App may reference any of these builder
/// method names. Porting one to the UI is a conscious plan amendment that
/// edits this list.
///
/// AMENDED, UI-tweaks round 3 (plan-ui-tweaks-round3.md X2 — owner-confirmed
/// scope change): the FIVE builders the HOP-settings net-programming editor
/// needs left this list. Round 2's M5 recorded a "future editor"; round 3 is
/// that future. Everything else in the E3 family STAYS guarded — see the
/// note on the HOP block below for exactly which, and why each.
///
/// AMENDED AGAIN, UI-tweaks round 5 (plan-ui-tweaks-round5.md §2.2.2, X6 —
/// owner-confirmed): THREE more names left, and no others. Round 3's editor
/// wrote a net behind one Store button; round 5's is radio-native — per-field
/// writes, a real LIST editor, and a "Clear net" button — and those three are
/// what that costs. The exclusion-band family and GenerateHopset STAY guarded.
///
/// A source scan follows NAMES, and the app layer reaches the Core through
/// SURFACE WRAPPERS — so the amendment's single-sender pin scans the wrapper
/// names too (audit round 1, MAJOR): guarding only the Core names left
/// <c>HopSurface.ProgramNetId(…)</c> callable from any view model, which is
/// exactly the escape X2 was written to prevent.
///
/// AMENDED AGAIN, ALE programming (plan-ale-programming.md §4.2, scope
/// amendment X8 — owner-confirmed): the EIGHT ALE fill-editing builders left
/// this list, because the ALE settings pane now carries the two programming
/// cards they exist for. The X8 block below carries the same machinery X6
/// got, plus the adaptations §4.2 names: the file allow-list was STAGED
/// (phase 1 pinned the surface; phase 2 added the two view models and their
/// per-file narrowing pins — it is now COMPLETE), the ALE
/// guarded remainder is EMPTY (X6's nonempty assertion is a HopController
/// fact, not a law), the WRITE wrappers are pinned non-substring against
/// every Core builder name while the READ wrappers live in their own list
/// (reads are not guard-scoped), and the wire-prefix <c>"CHG"</c> became
/// <c>"CHGROUP "</c> in CommandSurfaceTests so the group QUERY passes while
/// the un-built set form stays forbidden to every sender.
/// </summary>
public class GuiOutScopeGuardTests
{
    /// <summary>The GUI-out builder names (distinctive identifiers — each
    /// exists only on the Core command surface).</summary>
    private static readonly string[] GuiOutBuilderNames =
    [
        // E2 — ALE fill/address editing (AleController): the whole family
        // LEFT this list in scope amendment X8 (plan-ale-programming.md
        // §4.2). Each name, and why it is now app-reachable — through the
        // AleSurface wrappers and the ONE programming gate, and nowhere else:
        //   SetSelfAddress       — the address card's Self kind; the fill ROOT,
        //                          so no other address can be programmed
        //                          without it.
        //   SetIndividualAddress — its Individual kind (assoc-self picked from
        //                          the mirror's own selfs; the radio still
        //                          refuses a bad one — no app-side pre-check).
        //   SetNetAddress        — its Net kind, same shape.
        //   AddNetMember         — the net member row. WRITE-ONLY and
        //                          UNREADABLE (no query, no DELM), which is
        //                          why the card logs SENDS and says so.
        //   DeleteAddress        — the book tab's per-row Delete, behind a
        //                          confirm that re-arms every press and names
        //                          the SELF cascade.
        //   AddScanChannel /     — the groups card's add/remove rows; the
        //   DeleteScanChannel      closing CHG read is the verify.
        //   EraseAllAddresses    — the guarded ERASE control. ROUND 10 §5: the
        //                          Core token gate is GONE (owner ruling 9 —
        //                          the GUI owns confirmation for this
        //                          destructive-DATA sender and asks with a
        //                          two-button popup). X8 is UNCHANGED by that:
        //                          the builder is still guard-scoped, still
        //                          reachable only through the surface wrapper,
        //                          and "ERASE" is still a forbidden wire prefix
        //                          for every swept sender.
        // E3 — HOP net programming (HopController), AMENDED round 3 (X2) and
        // again round 5 (X6).
        //
        // PERMITTED from the app layer — the eight the HOP-settings editor
        // uses, and no more:
        //   round 3 (the net-programming writes)
        //     SetNetId, SetHopType, SetNarrowbandHopset, SetWidebandHopset,
        //     AddHopListFrequencies
        //   round 5 (the radio-native editor: a list UI and a wipe button)
        //     QueryHopList           — the LIST net's stored frequencies. NO
        //                              CAPTURED DIS answer carries lists, so
        //                              this is not a competing read path — it
        //                              is the ONLY one. Round 3's "second
        //                              source of truth" rationale assumed a
        //                              DIS answer that does not exist.
        //     DeleteHopListFrequency — the list UI's per-row remove. Round 3's
        //                              accumulation caution dissolves with it:
        //                              an append is now visible AND reversible
        //                              instead of invisible and permanent.
        //     DeleteHopset           — the "Clear net" button. HOPSET n DEL
        //                              still wipes the ENTIRE net record,
        //                              NETID included (probe R9b) — round 5
        //                              SURFACES those semantics rather than
        //                              hiding them: the warning names the
        //                              whole-record wipe and opens on EVERY
        //                              press (no once-per-session latch).
        //
        //     round 11 (X9 — the exclusion-band section, owner ruling R11)
        //     SetExcludeBand         — the section's Add row. The editor that
        //                              "does not exist" now does; EXC's
        //                              regeneration side effect is SURFACED in
        //                              the section caption, not hidden.
        //     QueryExcludeBands      — its read, now sentinel-scoped (an empty
        //                              table answers NOTHING, so only the
        //                              sentinel separates read-empty from a
        //                              swallowed query).
        //     DeleteExcludeBand      — the per-row Remove.
        //
        // STILL GUARDED, each for a reason no editor changes:
        //   DeleteAllExcludeBands — the WHOLE-TABLE wipe. X9 un-guarded three
        //                           builders; no screen asks for this one, and
        //                           a per-row Remove is what the section
        //                           offers instead.
        //   GenerateHopset        — DOIT only acts on the CURRENT net (probe
        //                           R9); programming a row must never
        //                           regenerate and re-tune behind the
        //                           operator's back. Selecting the net is the
        //                           Operate pane's explicit action.
        "DeleteAllExcludeBands", "GenerateHopset",
        // E1 — crypto (Prc138Radio)
        "SetEncryption", "SetEncryptionKey", "ClearEncryptionKey",
        "SelectEncryptionKey",
        // E5 — diagnostics (SsbController)
        "QueryFirmwareVersions", "SelfTest", "VswrTest",
        // Transmit hazard (protocol.md hazard table) — keying is not a
        // GUI-out class per the plan (round 8 places a Keyline control),
        // but until that UI lands with its own confirm flow, no app-layer
        // file may reference the builder.
        "SetKeyline",
    ];

    [Fact]
    public void NoAppLayerSource_ReferencesAGuiOutBuilder()
    {
        var root = FindRepoRoot();
        string[] appLayers =
        [
            Path.Combine(root, "src", "Falcon.App.Core"),
            Path.Combine(root, "src", "Falcon.App"),
        ];

        _ = appLayers;   // enumerated by AppLayerSourceFiles, which applies the
                         // same roots and the same bin/obj exclusions

        var offenders = new List<string>();
        foreach (var file in AppLayerSourceFiles(root))
        {
            var text = ScannableText(file);
            foreach (var name in GuiOutBuilderNames)
                if (text.Contains(name, StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file) + " references " + name);
        }

        Assert.Empty(offenders);
        Assert.NotEmpty(AppLayerSourceFiles(root));   // anti-vacuity: files were scanned
    }

    /// <summary>The guard list itself is pinned against the Core surface:
    /// every guarded name must actually exist as a public method on the
    /// command surface, so a builder rename cannot silently orphan the
    /// scan.</summary>
    [Fact]
    public void EveryGuardedName_ExistsOnTheCoreCommandSurface()
    {
        Type[] surface =
        [
            typeof(Radio.Prc138Radio),
            typeof(Modes.SsbController),
            typeof(Modes.AleController),
            typeof(Modes.HopController),
        ];
        var methods = surface
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var name in GuiOutBuilderNames)
            Assert.Contains(name, methods);
    }

    // ---- X2 (round 3) + X6 (round 5): the amendments themselves are pinned --
    // An amendment that only DELETES names from a list leaves no trace of what
    // was decided. These pins make each scope change explicit and bounded:
    // exactly which names moved, that they are real Core builders, that the
    // rest of the family did NOT move, and that the permitted names may only
    // appear in the app-layer files that own the editor.

    /// <summary>The CORE builders the HOP-settings editor may reach: the
    /// round-3 five (X1/V6 — NETID → HOPTYPE → HOPSET|HOPLIST) plus the
    /// round-5 three (X6 — the LIST read, the per-row remove, the wipe).
    /// Their only app-layer caller is the surface — a view model must not
    /// reach past it. This list is pinned EXACTLY (see
    /// <see cref="PermittedBuilders_AreExactlyTheTwoAmendments"/>): a ninth
    /// name here fails, which is what makes X6 "three, and no more".</summary>
    private static readonly string[] EditorPermittedBuilderNames =
    [
        "SetNetId", "SetHopType", "SetNarrowbandHopset", "SetWidebandHopset",
        "AddHopListFrequencies",
        // round-5 X6
        "QueryHopList", "DeleteHopListFrequency", "DeleteHopset",
        // round-11 X9 — the exclusion-band section (owner ruling R11)
        "SetExcludeBand", "QueryExcludeBands", "DeleteExcludeBand",
    ];

    /// <summary>The three names round-5 X6 moved out of the GUI-out list, held
    /// separately so the amendment's SIZE is itself assertable.</summary>
    private static readonly string[] Round5MovedNames =
    [
        "QueryHopList", "DeleteHopListFrequency", "DeleteHopset",
    ];

    /// <summary>The three names round-11 X9 moved — same treatment, so its
    /// size is assertable too. A FOURTH would have to be written here, which
    /// fails <see cref="X9Amendment_ExactlyThreeNamesMoved_AndTheyAreRealBuilders"/>.</summary>
    private static readonly string[] X9MovedNames =
    [
        "SetExcludeBand", "QueryExcludeBands", "DeleteExcludeBand",
    ];

    /// <summary>The SURFACE WRAPPERS over those builders. A source scan
    /// follows names, and the app layer calls the wrappers, not the builders
    /// — so guarding only the Core names would leave every view model free to
    /// program nets through <c>HopSurface</c> (audit round 1, MAJOR: a
    /// <c>ProgramNetId</c> reference planted in HopViewModel passed all five
    /// guard tests). These names carry the same single-sender rule.</summary>
    private static readonly string[] EditorPermittedWrapperNames =
    [
        "ProgramNetId", "ProgramHopType", "ProgramNarrowbandHopset",
        "ProgramWidebandHopset", "ProgramHopList",
        // round-5 X6
        "RequestHopList", "RemoveHopListFrequency", "ClearNet",
        // round-11 X9 — the exclusion-bands section's three wrappers. Listed
        // for the same reason as the eight above: the app layer calls the
        // WRAPPER, so guarding only the Core builder names would leave any view
        // model free to write exclusion bands through HopSurface.
        "RequestExcludeBands", "ProgramExcludeBand", "RemoveExcludeBand",
    ];

    /// <summary>Where each scanned name is allowed to appear. The Core names
    /// belong to the SURFACE alone (the VM goes through it); the wrapper
    /// names belong to the surface that defines them plus the editor VMs that
    /// call them. Anything else means the amendment leaked.</summary>
    private static readonly string HopSurfaceFile =
        Path.Combine("src", "Falcon.App.Core", "Surfaces", "HopSurface.cs");

    private static readonly string HopSettingsViewModelFile =
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "HopSettingsViewModel.cs");

    /// <summary>Round-5 BC4 puts a hoplist read on the OPERATE pane too (the
    /// current net's row shows a LIST net's frequency COUNT, which nothing but
    /// <c>HOPLIST n</c> can supply), so HopViewModel joins the wrapper
    /// allow-list. Its legitimate reach is <c>RequestHopList</c> ALONE — no
    /// write, no wipe — and
    /// <see cref="HopViewModel_ReachesExactlyOneWrapper_TheHopListRead"/> is
    /// the pin that says so.</summary>
    private static readonly string HopViewModelFile =
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "HopViewModel.cs");

    /// <summary>The ONE wrapper HopViewModel may name.</summary>
    private const string HopViewModelPermittedWrapper = "RequestHopList";

    /// <summary>The HOP wrappers whose declaration is <c>public long</c>, not
    /// <c>public void</c>: a SENTINEL-SCOPED read hands back its operation's
    /// READ ID so the caller can match the domain's own completion record
    /// (round 11 §9A). <c>RequestHopList</c> is NOT one — it is a plain read
    /// with no sentinel-scoped operation behind it, so it stays void, which is
    /// what makes this list a real distinction rather than "the reads".</summary>
    private static readonly string[] HopReadWrappersReturningAReadId = ["RequestExcludeBands"];

    /// <summary>
    /// ROUND 11 §9A, scope amendment X11 — the CLONE CAMPAIGN consumer. The
    /// radio-cloning orchestrator replays the HOP net domain (the clear-first
    /// net replay) and the exclusion-band reconcile, so it reaches the same
    /// wrappers the editor does. It is a THIRD consumer of an existing
    /// amendment, not a new one: no builder left the guard list for it, and
    /// its reach is narrowed per-file below exactly like HopViewModel's.
    /// </summary>
    private static readonly string CloneServiceFile =
        Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs");

    /// <summary>
    /// The HOP wrappers the clone campaign may name — a CLOSED manifest,
    /// NARROWED by clone round 12.
    ///
    /// <para><b>Three names LEFT this list, and the reason is one owner
    /// statement:</b> "it's safe to assume that zeroize clears everything
    /// except for the remote port baud rate". The campaign's first wire act is
    /// now the wipe, so the target is GUARANTEED BLANK and no leg may converge
    /// onto an unknown one. <c>ClearNet</c> went with the CLEAR-FIRST replay
    /// (every net is already wiped), and <c>RemoveExcludeBand</c> went with the
    /// exclusion reconcile (the table is already empty). Removing them here is
    /// what makes those absences MECHANICAL rather than a comment: a campaign
    /// that quietly reinstated either fails this guard.</para>
    ///
    /// <para><c>RemoveHopListFrequency</c> was never on the list at all, for the
    /// older version of the same reason.</para>
    /// </summary>
    private static readonly string[] CloneServicePermittedHopWrappers =
    [
        "ProgramHopType", "ProgramNetId",
        "ProgramNarrowbandHopset", "ProgramWidebandHopset", "ProgramHopList",
        "RequestHopList",
        "RequestExcludeBands", "ProgramExcludeBand",
    ];

    /// <summary>The wrapper allow-list, held once and pinned as an EXACT set
    /// by <see cref="WrapperAllowList_IsExactlyTheEditorFilesAndTheCloneCampaign"/>
    /// — the C1 audit removed a file from the inline version and nothing failed.</summary>
    private static readonly string[] WrapperAllowedFiles =
        [HopSurfaceFile, HopSettingsViewModelFile, HopViewModelFile, CloneServiceFile];

    [Fact]
    public void Round3Amendment_PermittedBuilders_LeftTheGuardList_ButAreRealBuilders()
    {
        foreach (var name in EditorPermittedBuilderNames)
            Assert.DoesNotContain(name, GuiOutBuilderNames);

        var methods = typeof(Modes.HopController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();
        foreach (var name in EditorPermittedBuilderNames)
            Assert.Contains(name, methods);
    }

    [Fact]
    public void Round5AmendmentX6_ExactlyThreeNamesMoved_AndTheyAreRealBuilders()
    {
        // The amendment's SIZE is the pin: three names, named here, out of the
        // guard list and onto the permitted list. A fourth would have to be
        // written into Round5MovedNames, which fails this count.
        Assert.Equal(3, Round5MovedNames.Length);
        Assert.Equal(
            ["DeleteHopListFrequency", "DeleteHopset", "QueryHopList"],
            Round5MovedNames.Order(StringComparer.Ordinal));

        var methods = typeof(Modes.HopController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var name in Round5MovedNames)
        {
            Assert.DoesNotContain(name, GuiOutBuilderNames);      // it really left
            Assert.Contains(name, EditorPermittedBuilderNames);   // …and landed
            Assert.Contains(name, methods);                       // …on a real builder
        }
    }

    [Fact]
    public void X9Amendment_ExactlyThreeNamesMoved_AndTheyAreRealBuilders()
    {
        // The amendment's SIZE is the pin (the X6 treatment): three names, out
        // of the guard list and onto the permitted list — and NOT
        // DeleteAllExcludeBands, which the next test holds down.
        Assert.Equal(3, X9MovedNames.Length);
        Assert.Equal(
            ["DeleteExcludeBand", "QueryExcludeBands", "SetExcludeBand"],
            X9MovedNames.Order(StringComparer.Ordinal));

        var methods = typeof(Modes.HopController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var name in X9MovedNames)
        {
            Assert.DoesNotContain(name, GuiOutBuilderNames);      // it really left
            Assert.Contains(name, EditorPermittedBuilderNames);   // …and landed
            Assert.Contains(name, methods);                       // …on a real builder
        }
    }

    [Fact]
    public void X9_DeleteAllExcludeBands_StaysGuarded()
    {
        // The half of X9 that is a REFUSAL: the whole-table wipe did NOT move.
        // Widening the amendment to four names fails the size pin above; this
        // one names the specific builder that must stay put, so deleting it
        // from the guard list fails here even if the count were adjusted.
        Assert.Contains("DeleteAllExcludeBands", GuiOutBuilderNames);
        Assert.DoesNotContain("DeleteAllExcludeBands", EditorPermittedBuilderNames);
        Assert.DoesNotContain("DeleteAllExcludeBands", X9MovedNames);
    }

    [Fact]
    public void PermittedBuilders_AreExactlyTheThreeAmendments()
    {
        // Anti-vacuity for the "and no more" half of all three amendments: the
        // permitted list is pinned as a SET, so widening it by one name — the
        // easy way to smuggle an extra builder past X6/X9 — fails here even
        // though every other pin would still be satisfied.
        Assert.Equal(
            [
                "AddHopListFrequencies", "DeleteExcludeBand", "DeleteHopListFrequency",
                "DeleteHopset", "QueryExcludeBands", "QueryHopList", "SetExcludeBand",
                "SetHopType", "SetNarrowbandHopset", "SetNetId", "SetWidebandHopset",
            ],
            EditorPermittedBuilderNames.Order(StringComparer.Ordinal));
    }

    /// <summary>The HOP builders that were NEVER GUI-out: the select and read
    /// intents the Operate pane has carried since Stage 5. Named here so the
    /// family partition below has somewhere honest to put them — they are
    /// neither an amendment nor a guarded hazard.
    ///
    /// <para><b>ADMISSION RULE (C1 audit round 2, MAJOR — read this before
    /// adding a name).</b> This bucket is CLOSED. It may only ever hold intents
    /// the app layer ALREADY reached before round 5; it is a record of history,
    /// not a place to put a new builder. Adding a name here removes it from the
    /// downward remainder in
    /// <see cref="EveryHopBuilder_IsAccountedFor_AndTheRemainderStaysGuarded"/>
    /// — i.e. it silently un-guards that builder with no proof it was ever a
    /// pre-round-5 intent, which is exactly the escape the downward computation
    /// was written to close. A builder that needs the app layer goes through a
    /// PLAN AMENDMENT and joins <see cref="EditorPermittedBuilderNames"/> with
    /// its own moved-names pin, the way X2 and X6 did. The exact-set pin below
    /// is what makes "closed" enforceable rather than advisory.</para></summary>
    private static readonly string[] SurfacePermittedBuilderNames =
    [
        "SelectNet", "Sync", "SetTimeOfDay", "QueryNet", "QueryAllNets",
    ];

    [Fact]
    public void SurfacePermittedBuilders_AreExactlyThePreRound5Intents()
    {
        // C1 audit round 2, MAJOR: this list was mutable with no pin at all,
        // so a future destructive builder could be dropped in and vanish from
        // the remainder unchallenged. Pinned as an EXACT set — the same
        // treatment EditorPermittedBuilderNames and WrapperAllowedFiles get,
        // and for the same reason: an allow-list is a DECISION, so changing it
        // must break a test that names the decision.
        Assert.Equal(
            ["QueryAllNets", "QueryNet", "SelectNet", "SetTimeOfDay", "Sync"],
            SurfacePermittedBuilderNames.Order(StringComparer.Ordinal));

        // …and every one is a REAL builder, so the bucket cannot be padded
        // with a name that excuses nothing (or left pointing at a rename).
        var methods = typeof(Modes.HopController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(Modes.HopController))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(methods);       // anti-vacuity: reflection found the type
        foreach (var name in SurfacePermittedBuilderNames)
            Assert.Contains(name, methods);
    }

    [Fact]
    public void TheThreeBuckets_ArePairwiseDisjoint()
    {
        // C1 audit round 2, MAJOR: disjointness was only ever checked
        // permitted-vs-guarded, never editor-vs-surface — so an
        // editor-permitted name duplicated into the surface bucket passed,
        // quietly giving it a second, unpinned justification. All THREE pairs
        // are checked here, in both directions by construction.
        (string Name, string[] Names)[] buckets =
        [
            ("editor-permitted", EditorPermittedBuilderNames),
            ("surface-permitted", SurfacePermittedBuilderNames),
            ("guarded", GuiOutBuilderNames),
        ];

        var overlaps = new List<string>();
        for (int i = 0; i < buckets.Length; i++)
            for (int j = i + 1; j < buckets.Length; j++)
                foreach (var shared in buckets[i].Names.Intersect(buckets[j].Names, StringComparer.Ordinal))
                    overlaps.Add($"{shared} is in BOTH {buckets[i].Name} and {buckets[j].Name}");

        Assert.Empty(overlaps);

        // Anti-vacuity: the loop is worthless if a bucket is empty, and the
        // pair count proves all three comparisons really ran.
        Assert.All(buckets, b => Assert.NotEmpty(b.Names));
        Assert.Equal(3, buckets.Length * (buckets.Length - 1) / 2);
    }

    [Fact]
    public void EveryHopBuilder_IsAccountedFor_AndTheRemainderStaysGuarded()
    {
        // C1 audit round 1, MAJOR: the first version computed the remainder as
        // GuiOutBuilderNames ∩ reflected-methods, which is blind by
        // construction — a NEW builder left out of BOTH arrays intersects with
        // nothing and passes. Computed the other way round, from the Core type
        // DOWN, every public HopController builder must land in exactly one of
        // three buckets, and whatever is left over must be guarded.
        var builders = typeof(Modes.HopController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(Modes.HopController))   // not object's
            .Where(m => !m.IsSpecialName)                                 // not the timeout accessors
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(builders);      // anti-vacuity: reflection really found the type

        var remainder = builders
            .Except(EditorPermittedBuilderNames, StringComparer.Ordinal)
            .Except(SurfacePermittedBuilderNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The amendments are bounded: the whole-table exclusion wipe and
        // hopset regeneration stay GUI-out, and NOTHING ELSE exists
        // un-triaged. A builder added to HopController tomorrow and listed
        // nowhere shows up here. (X9 moved the other three exclusion builders
        // onto the permitted list — X9Amendment_ExactlyThreeNamesMoved…)
        Assert.Equal(
            ["DeleteAllExcludeBands", "GenerateHopset"],
            remainder);

        // …and the remainder is not merely NAMED, it is actually guarded.
        foreach (var name in remainder)
            Assert.Contains(name, GuiOutBuilderNames);

        // Disjointness — all three pairs — lives in
        // TheThreeBuckets_ArePairwiseDisjoint, and the two permitted buckets
        // are each pinned as an exact set. Without those, subtracting a bucket
        // here would be subtracting whatever someone put in it.
    }

    [Fact]
    public void Round3Amendment_PermittedBuilders_AppearOnlyInTheSurface()
    {
        // The Core builder names may appear in HopSurface and NOWHERE else in
        // the app layer: a view model that reaches past the surface would be
        // outside the architecture as well as outside the amendment.
        AssertNamesAppearOnlyIn(EditorPermittedBuilderNames, [HopSurfaceFile]);
    }

    [Fact]
    public void Round3Amendment_PermittedWrappers_AppearOnlyInTheEditorsOwnFiles()
    {
        // The wrapper names may appear in the surface that DEFINES them and in
        // the editor VMs that call them. This is the pin that actually
        // enforces "GUI-permitted from the HOP-settings EDITOR" — the Core-name
        // pin above cannot see a wrapper call at all.
        //
        // HopViewModel joined the list in round 5 (BC4 reads a LIST net's
        // frequencies for the Operate row). That file is NOT thereby free to
        // program or wipe a net — the next pin narrows it to the one read.
        AssertNamesAppearOnlyIn(EditorPermittedWrapperNames, WrapperAllowedFiles);
    }

    [Fact]
    public void WrapperAllowList_IsExactlyTheEditorFilesAndTheCloneCampaign()
    {
        // C1 audit round 1, MAJOR: the allow-list was only ever CONSUMED, never
        // pinned — the auditor deleted HopViewModel.cs from it and all 367
        // tests stayed green, because a shorter allow-list only makes the scan
        // stricter and nothing was yet failing it. An allow-list is a DECISION
        // about who may send, so it is pinned as an exact set: dropping a file
        // (silently narrowing the amendment) and adding one (silently widening
        // it) both fail here.
        Assert.Equal(
            [
                Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs"),
                Path.Combine("src", "Falcon.App.Core", "Surfaces", "HopSurface.cs"),
                Path.Combine("src", "Falcon.App.Core", "ViewModels", "HopSettingsViewModel.cs"),
                Path.Combine("src", "Falcon.App.Core", "ViewModels", "HopViewModel.cs"),
            ],
            WrapperAllowedFiles.Order(StringComparer.Ordinal));

        // …and every allowed file is a real file, so a rename cannot leave the
        // list pointing at nothing (which would also silently narrow it).
        var root = FindRepoRoot();
        foreach (var relative in WrapperAllowedFiles)
            Assert.True(File.Exists(Path.Combine(root, relative)), "allow-listed file missing: " + relative);
    }

    /// <summary>X11's narrowing pin: adding a whole FILE to the allow-list
    /// would otherwise hand the clone campaign every HOP wrapper there is.
    /// Its reach is the closed manifest above — and the one it is NOT given
    /// (the per-frequency LIST removal) is asserted absent, because the
    /// clear-first replay has no use for it.</summary>
    [Fact]
    public void X11_CloneService_ReachesExactlyTheCloneCampaignsHopWrappers()
    {
        var path = Path.Combine(FindRepoRoot(), CloneServiceFile);
        Assert.True(File.Exists(path), "CloneService.cs missing: " + path);
        var text = ScannableText(path);

        foreach (var name in EditorPermittedWrapperNames)
        {
            bool permitted = CloneServicePermittedHopWrappers.Contains(name, StringComparer.Ordinal);
            Assert.Equal(permitted, ReferencesIdentifier(text, name));
        }

        // Anti-vacuity, both halves: the permitted manifest is a real SUBSET of
        // the scanned list (a renamed entry would silently exempt everything),
        // and it is a PROPER subset — something really is withheld.
        foreach (var name in CloneServicePermittedHopWrappers)
            Assert.Contains(name, EditorPermittedWrapperNames);
        foreach (var withheld in new[] { "RemoveHopListFrequency", "ClearNet", "RemoveExcludeBand" })
        {
            Assert.Contains(withheld, EditorPermittedWrapperNames);
            Assert.DoesNotContain(withheld, CloneServicePermittedHopWrappers);
        }
    }

    [Fact]
    public void HopViewModel_ReachesExactlyOneWrapper_TheHopListRead()
    {
        // Widening the allow-list by a whole FILE would otherwise hand the
        // Operate pane every programming wrapper. HopViewModel's legitimate
        // reach is the BC4 hoplist read and nothing else — a ProgramNetId or
        // ClearNet reference planted there fails here.
        var path = Path.Combine(FindRepoRoot(), HopViewModelFile);
        Assert.True(File.Exists(path), "HopViewModel.cs missing: " + path);
        var text = ScannableText(path);

        foreach (var name in EditorPermittedWrapperNames)
        {
            if (name == HopViewModelPermittedWrapper) continue;
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }

        // Anti-vacuity: the loop above is worthless if the exempted name is
        // not actually in the scanned list (a rename would silently exempt
        // everything). C1 ships the wrapper; C2 adds the call site — so this
        // pins list membership, not the call.
        Assert.Contains(HopViewModelPermittedWrapper, EditorPermittedWrapperNames);
    }

    // ====================================================================
    // X8 (plan-ale-programming.md §4.2): the ALE programming amendment.
    // Same six parts X6 got, with the two adaptations §4.2 names — the
    // STAGED file allow-list (part 5) and the EMPTY guarded remainder
    // (part 6) — plus the split write/read wrapper lists (part 4).
    // ====================================================================

    /// <summary>Part 2. The CORE builders the ALE programming cards may
    /// reach: exactly the eight fill writes, pinned as a SET by
    /// <see cref="X8_PermittedBuilders_AreExactlyTheEight"/>. Their only
    /// app-layer caller is the surface.</summary>
    private static readonly string[] AleEditorPermittedBuilderNames =
    [
        "SetSelfAddress", "SetIndividualAddress", "SetNetAddress",
        "AddNetMember", "DeleteAddress", "AddScanChannel", "DeleteScanChannel",
        "EraseAllAddresses",
    ];

    /// <summary>Part 3. The names X8 moved out of the GUI-out list, held
    /// separately so the amendment's SIZE is itself assertable.</summary>
    private static readonly string[] X8MovedNames =
    [
        "SetSelfAddress", "SetIndividualAddress", "SetNetAddress",
        "AddNetMember", "DeleteAddress", "AddScanChannel", "DeleteScanChannel",
        "EraseAllAddresses",
    ];

    /// <summary>Part 4a. The eight WRITE wrappers (§4.3 table). Guard-scoped
    /// exactly like the Core names, and pinned textually distinct from every
    /// Core builder name — a wrapper that CONTAINED a builder name (or vice
    /// versa) would make the two placement rules unsatisfiable at once.</summary>
    private static readonly string[] AleEditorPermittedWrapperNames =
    [
        "ProgramSelf", "ProgramIndividual", "ProgramNet", "ProgramNetMember",
        "RemoveAddress", "ProgramScanChannel", "RemoveScanChannel",
        "EraseAddressBook",
    ];

    /// <summary>Part 4b. The READ wrappers, held SEPARATELY and deliberately
    /// exempt from both the non-substring rule and guard scanning: a read is
    /// not a guard-scoped gesture, so a read wrapper may (and does) reuse its
    /// Core builder's name. Pinned only to EXIST on the surface — the
    /// exemption is a decision, not an oversight.</summary>
    private static readonly string[] AleReadWrapperNames =
    [
        "RequestStationBook", "RequestChannelGroup", "RequestAllChannelGroups",
    ];

    private static readonly string AleSurfaceFile =
        Path.Combine("src", "Falcon.App.Core", "Surfaces", "AleSurface.cs");

    private static readonly string AleProgrammingViewModelFile =
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "AleProgrammingViewModel.cs");

    private static readonly string AleScanGroupsViewModelFile =
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "AleScanGroupsViewModel.cs");

    /// <summary>Part 5, COMPLETE (the staging adaptation, §4.2.5): X6's
    /// allow-list covers every editor file at once; X8 phased it, because the
    /// two view models landed in phase 2. Phase 1 pinned the surface exists
    /// and the wrappers appear NOWHERE else; PHASE 2 adds the two view models
    /// here and lands the per-file NARROWING pins below — widening the
    /// allow-list by a whole FILE would otherwise hand each card every
    /// programming wrapper, which is the X6 lesson
    /// (HopViewModel_ReachesExactlyOneWrapper_TheHopListRead).</summary>
    private static readonly string[] AleWrapperAllowedFiles =
        [AleSurfaceFile, AleProgrammingViewModelFile, AleScanGroupsViewModelFile, CloneServiceFile];

    /// <summary>
    /// X11: the ALE wrappers the CLONE CAMPAIGN may name — a CLOSED manifest,
    /// NARROWED by clone round 12.
    ///
    /// <para><c>RemoveAddress</c> was always absent: the campaign never deletes
    /// an address one at a time, so a <c>DELAD</c> can never come out of a
    /// clone. TWO MORE joined it this round, both for the wipe's sake (owner
    /// statement §1): <c>EraseAddressBook</c>, because <c>ZERO</c> subsumes the
    /// ERASE leg outright, and <c>RemoveScanChannel</c>, because after the wipe
    /// every channel group is empty and the groups leg is PURE <c>ADDC</c>
    /// writes. The standalone ALE-erase card keeps its own leg and its own
    /// confirm — it is simply not the clone's.</para>
    /// </summary>
    private static readonly string[] CloneServicePermittedAleWrappers =
    [
        "ProgramSelf", "ProgramIndividual", "ProgramNet", "ProgramNetMember",
        "ProgramScanChannel",
    ];

    /// <summary>The ADDRESS card's legitimate reach: the six address-family
    /// WRITE wrappers, and no channel-group write ever.</summary>
    private static readonly string[] AddressCardPermittedWrappers =
    [
        "ProgramSelf", "ProgramIndividual", "ProgramNet", "ProgramNetMember",
        "RemoveAddress", "EraseAddressBook",
    ];

    /// <summary>The GROUPS card's legitimate reach: the two channel-group
    /// writes, and no address write ever.</summary>
    private static readonly string[] GroupsCardPermittedWrappers =
        ["ProgramScanChannel", "RemoveScanChannel"];

    [Fact]
    public void X8Amendment_ExactlyEightNamesMoved_AndTheyAreRealBuilders()
    {
        Assert.Equal(8, X8MovedNames.Length);
        Assert.Equal(
            [
                "AddNetMember", "AddScanChannel", "DeleteAddress",
                "DeleteScanChannel", "EraseAllAddresses", "SetIndividualAddress",
                "SetNetAddress", "SetSelfAddress",
            ],
            X8MovedNames.Order(StringComparer.Ordinal));

        var methods = typeof(Modes.AleController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var name in X8MovedNames)
        {
            Assert.DoesNotContain(name, GuiOutBuilderNames);           // it really left
            Assert.Contains(name, AleEditorPermittedBuilderNames);     // …and landed
            Assert.Contains(name, methods);                            // …on a real builder
        }
    }

    [Fact]
    public void X8_PermittedBuilders_AreExactlyTheEight()
    {
        // Anti-vacuity for the "and no more" half: widening the permitted
        // list by one name — the easy way to smuggle a ninth builder past X8
        // — fails here even though every other pin would still be satisfied.
        Assert.Equal(
            [
                "AddNetMember", "AddScanChannel", "DeleteAddress",
                "DeleteScanChannel", "EraseAllAddresses", "SetIndividualAddress",
                "SetNetAddress", "SetSelfAddress",
            ],
            AleEditorPermittedBuilderNames.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The X8 scans match whole IDENTIFIERS, not substrings — forced by a
    /// REAL collision with the X6 amendment: the §4.3 table's
    /// <c>ProgramNet</c> wrapper is a substring of X6's <c>ProgramNetId</c>,
    /// so a substring scan would report HopSurface.cs as an ALE offender and
    /// no naming choice inside the ALE family could fix it. Boundary matching
    /// is also strictly more correct: a file that names only
    /// <c>ProgramNetMember</c> is not a reference to <c>ProgramNet</c>.
    /// (The X6 scans keep their own substring helper, untouched.)
    /// </summary>
    private static bool ReferencesIdentifier(string scannableText, string identifier)
        => System.Text.RegularExpressions.Regex.IsMatch(
            scannableText, $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(identifier)}(?![A-Za-z0-9_])");

    [Fact]
    public void X8_TheIdentifierScanner_SeesCalls_AndIgnoresLongerIdentifiers()
    {
        // Anti-vacuity self-pin: the positive half proves the scan can still
        // catch a real call site (a matcher that never matched would disarm
        // every X8 placement pin), the negative half is the collision it
        // exists for.
        Assert.True(ReferencesIdentifier("surface.ProgramNet(a, 1, b);", "ProgramNet"));
        Assert.True(ReferencesIdentifier("public void ProgramNet(string a)", "ProgramNet"));
        Assert.False(ReferencesIdentifier("Radio.Hop.ProgramNetId(0, id);", "ProgramNet"));
        Assert.False(ReferencesIdentifier("vm.ProgramNetMember(n, m);", "ProgramNet"));
        Assert.False(ReferencesIdentifier("XProgramNet(a);", "ProgramNet"));
    }

    private static void AssertIdentifiersAppearOnlyIn(
        IReadOnlyList<string> names, IReadOnlyList<string> allowedRelativePaths)
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in AppLayerSourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            if (allowedRelativePaths.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;

            var text = ScannableText(file);
            foreach (var name in names)
                if (ReferencesIdentifier(text, name))
                    offenders.Add(relative + " references " + name);
        }

        Assert.Empty(offenders);
        Assert.NotEmpty(AppLayerSourceFiles(root));   // anti-vacuity: files were scanned
    }

    [Fact]
    public void X8_PermittedBuilders_AppearOnlyInTheAleSurface()
    {
        // The Core builder names may appear in AleSurface and NOWHERE else in
        // the app layer: a view model that reached past the surface would be
        // outside the architecture as well as outside the amendment.
        AssertIdentifiersAppearOnlyIn(AleEditorPermittedBuilderNames, [AleSurfaceFile]);
    }

    [Fact]
    public void X8_PermittedWrappers_AppearOnlyInTheAllowListedFiles()
    {
        // The pin the Core-name scan cannot make: a wrapper CALL is invisible
        // to it. Phase 1's allow-list is the surface alone.
        AssertIdentifiersAppearOnlyIn(AleEditorPermittedWrapperNames, AleWrapperAllowedFiles);
    }

    [Fact]
    public void X8_WrapperAllowList_IsExactlyTheThreeEditorFiles_AndTheyExist()
    {
        // An allow-list is a DECISION, so it is pinned as an exact set:
        // adding a file (silently widening the amendment) fails here, and so
        // does dropping one — the C1 audit deleted a file from X6's version
        // and nothing failed, because a SHORTER allow-list only makes the scan
        // stricter.
        Assert.Equal(
            [
                Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs"),
                Path.Combine("src", "Falcon.App.Core", "Surfaces", "AleSurface.cs"),
                Path.Combine("src", "Falcon.App.Core", "ViewModels", "AleProgrammingViewModel.cs"),
                Path.Combine("src", "Falcon.App.Core", "ViewModels", "AleScanGroupsViewModel.cs"),
            ],
            AleWrapperAllowedFiles.Order(StringComparer.Ordinal));

        // …and every allowed file is a real file, so a rename cannot leave the
        // list pointing at nothing (which would also silently narrow it).
        var root = FindRepoRoot();
        foreach (var relative in AleWrapperAllowedFiles)
            Assert.True(File.Exists(Path.Combine(root, relative)), "allow-listed file missing: " + relative);
    }

    [Fact]
    public void X8_TheTwoCardsSplitTheWrappersBetweenThem_WithNothingLeftOver()
    {
        // The narrowing lists are pinned as a PARTITION of the eight write
        // wrappers: disjoint, and together the whole set. Without this, a
        // wrapper dropped from both per-file lists would be un-narrowed —
        // reachable from either card with no pin to say so.
        Assert.Empty(AddressCardPermittedWrappers.Intersect(
            GroupsCardPermittedWrappers, StringComparer.Ordinal));
        Assert.Equal(
            AleEditorPermittedWrapperNames.Order(StringComparer.Ordinal),
            AddressCardPermittedWrappers.Concat(GroupsCardPermittedWrappers)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void X8_TheAddressCard_ReachesOnlyTheAddressFamilyWrappers()
    {
        // Widening the allow-list by a whole FILE would otherwise hand the
        // address card the channel-group writes as well. Its legitimate reach
        // is the six address-family wrappers; a ProgramScanChannel reference
        // planted there fails here.
        var text = ScannableText(Path.Combine(FindRepoRoot(), AleProgrammingViewModelFile));

        foreach (var name in AleEditorPermittedWrapperNames)
        {
            if (AddressCardPermittedWrappers.Contains(name, StringComparer.Ordinal)) continue;
            Assert.False(ReferencesIdentifier(text, name),
                AleProgrammingViewModelFile + " must not reach " + name);
        }

        // Anti-vacuity, both halves: the file really is scannable (it names
        // the wrappers it IS allowed) and the exemption list is really a
        // subset of what the scan covers — a rename would otherwise exempt
        // everything silently.
        foreach (var name in AddressCardPermittedWrappers)
        {
            Assert.Contains(name, AleEditorPermittedWrapperNames);
            Assert.True(ReferencesIdentifier(text, name),
                AleProgrammingViewModelFile + " no longer reaches " + name);
        }

        // …and it owns the BOOK read + the erase token path, which is the
        // other half of §4.2.5's "address family + book read + erase".
        Assert.True(ReferencesIdentifier(text, "RequestStationBook"));
        Assert.DoesNotContain("RequestChannelGroup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestAllChannelGroups", text, StringComparison.Ordinal);
    }

    [Fact]
    public void X8_TheGroupsCard_ReachesOnlyTheGroupFamilyWrappers()
    {
        var text = ScannableText(Path.Combine(FindRepoRoot(), AleScanGroupsViewModelFile));

        foreach (var name in AleEditorPermittedWrapperNames)
        {
            if (GroupsCardPermittedWrappers.Contains(name, StringComparer.Ordinal)) continue;
            Assert.False(ReferencesIdentifier(text, name),
                AleScanGroupsViewModelFile + " must not reach " + name);
        }

        foreach (var name in GroupsCardPermittedWrappers)
        {
            Assert.Contains(name, AleEditorPermittedWrapperNames);
            Assert.True(ReferencesIdentifier(text, name),
                AleScanGroupsViewModelFile + " no longer reaches " + name);
        }

        // The groups card reads GROUPS and never the station book.
        Assert.True(ReferencesIdentifier(text, "RequestChannelGroup"));
        Assert.DoesNotContain("RequestStationBook", text, StringComparison.Ordinal);
    }

    [Fact]
    public void X8_WriteWrapperNames_AreNonSubstringWithEveryCoreBuilderName()
    {
        // The scanner matches identifiers, so a wrapper that CONTAINED a
        // builder name (or the reverse) would make "builders only in the
        // surface" and "wrappers only in the allow-list" contradict each
        // other. Checked against EVERY name the guard machinery scans for,
        // not just the ALE eight.
        string[] allBuilders = [.. GuiOutBuilderNames, .. EditorPermittedBuilderNames,
            .. SurfacePermittedBuilderNames, .. AleEditorPermittedBuilderNames];

        var offenders = new List<string>();
        foreach (var wrapper in AleEditorPermittedWrapperNames)
            foreach (var builder in allBuilders)
            {
                if (wrapper.Contains(builder, StringComparison.Ordinal))
                    offenders.Add($"wrapper {wrapper} contains builder {builder}");
                if (builder.Contains(wrapper, StringComparison.Ordinal))
                    offenders.Add($"builder {builder} contains wrapper {wrapper}");
            }

        Assert.Empty(offenders);
        Assert.NotEmpty(allBuilders);                          // anti-vacuity
        Assert.Equal(8, AleEditorPermittedWrapperNames.Length);

        // The READ wrappers are EXEMPT and prove it: RequestChannelGroup is
        // the Core builder's own name. That is allowed because reads are not
        // guard-scoped — stated here so the exemption cannot be mistaken for
        // an omission.
        Assert.Contains("RequestChannelGroup", AleReadWrapperNames);
        Assert.Contains("RequestChannelGroup",
            typeof(Modes.AleController)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(m => m.Name));
    }

    [Fact]
    public void X8_EveryScannedWrapperName_ExistsOnTheAleSurface()
    {
        // Falcon.Core.Tests does not reference Falcon.App.Core (Core must not
        // depend on the app layer), so the surface is read as SOURCE —
        // stripped, so a commented-out declaration cannot satisfy the pin.
        var text = ScannableText(Path.Combine(FindRepoRoot(), AleSurfaceFile));

        foreach (var name in AleEditorPermittedWrapperNames)
            Assert.Contains("public void " + name, text, StringComparison.Ordinal);

        // ROUND 10 §5: the ERASE wrapper is PARAMETERLESS. The token it used
        // to pass through to Core is gone from both sides — confirmation is
        // the GUI's popup now — so the source pin names the exact signature,
        // not just the method name: re-adding a parameter here (the way the
        // token would creep back) fails this line while every other pin in the
        // file stays green. Core's own side is reflection-pinned in
        // CommandSurfaceTests.
        Assert.Contains("public void EraseAddressBook()", text, StringComparison.Ordinal);

        // Anti-vacuity for the shape above: a wrapper that DOES take arguments
        // must not match the parameterless spelling, or the pin would be
        // asserting nothing about arity at all.
        Assert.DoesNotContain("public void RemoveAddress()", text, StringComparison.Ordinal);

        // Read wrappers return the operation's read id, so their declaration
        // shape differs — pinned as its own line rather than folded in.
        foreach (var name in AleReadWrapperNames)
            Assert.Contains("public long " + name, text, StringComparison.Ordinal);

        // …and the gate the wrappers must be driven through really exists.
        Assert.Contains("public AleProgrammingGate Programming", text, StringComparison.Ordinal);
    }

    /// <summary>The ALE builders that were NEVER GUI-out: the operational
    /// intents, the station-list queries, the nine settings setters, and the
    /// X8-era read/barrier intents the programming cards call directly
    /// (reads and a bare sentinel are safe by construction).
    ///
    /// <para><b>ADMISSION RULE.</b> Same CLOSED-bucket rule the HOP list
    /// carries: a name here is removed from the downward remainder below, so
    /// it may only ever hold intents that are safe surface — never a write.
    /// A new WRITE goes through a plan amendment and joins
    /// <see cref="AleEditorPermittedBuilderNames"/> with its own moved-names
    /// pin, the way X8 did.</para></summary>
    private static readonly string[] AleSurfacePermittedBuilderNames =
    [
        // Operate / Messages / LQA intents (Stage 6).
        "StartScan", "Stop", "Call", "SendAmd", "QueryRxMessages", "Rank",
        "StartExchange", "StopExchange", "StartSounding", "StopSounding",
        // Station-list reads.
        "QuerySelfAddresses", "QueryIndividualAddresses", "QueryNetAddresses",
        "RefreshStationList",
        // X8 reads + the bare sentinel barrier (§4.1).
        "RequestChannelGroup", "RefreshChannelGroups", "Synchronize",
        // Round 11 §8 reads: one net's membership (targeted NETAD) and the
        // LQA schedule queue (bare EXCH). Safe surface by the ADMISSION RULE
        // above — both are READS with a closing sentinel and no write of any
        // kind; neither can mutate the radio.
        "ReadNetMembers", "ReadLqaSchedules",
        // Phase R settings (bench-confirmed query+set).
        "SetAllCall", "SetAnyCall", "SetAmdDisplay", "SetKeyToCall",
        "SetListenBeforeTx", "SetRadioSilence", "SetMaxScanChannels",
        "SetLinkTimeout", "SetTuneTime",
    ];

    /// <summary>X11's ALE narrowing pin — the twin of
    /// <see cref="X11_CloneService_ReachesExactlyTheCloneCampaignsHopWrappers"/>.</summary>
    [Fact]
    public void X11_CloneService_ReachesExactlyTheCloneCampaignsAleWrappers()
    {
        var text = ScannableText(Path.Combine(FindRepoRoot(), CloneServiceFile));

        foreach (var name in AleEditorPermittedWrapperNames)
        {
            bool permitted = CloneServicePermittedAleWrappers.Contains(name, StringComparer.Ordinal);
            Assert.Equal(permitted, ReferencesIdentifier(text, name));
        }

        // Anti-vacuity: a real, PROPER subset — the per-address delete, the
        // book ERASE and the group removal are all withheld, so the campaign
        // proves mechanically that it sends no DELAD, no ERASE and no DELC.
        foreach (var name in CloneServicePermittedAleWrappers)
            Assert.Contains(name, AleEditorPermittedWrapperNames);
        foreach (var withheld in new[] { "RemoveAddress", "EraseAddressBook", "RemoveScanChannel" })
        {
            Assert.Contains(withheld, AleEditorPermittedWrapperNames);
            Assert.DoesNotContain(withheld, CloneServicePermittedAleWrappers);
        }
    }

    // ====================================================================
    // X10 (round 11 §9A): the STORED TX MESSAGE STORE.
    // ====================================================================

    /// <summary>The three TXMSG-family builders round 11 admits — the ONLY new
    /// builders anywhere this round (§10, invariant 1). They are NOT a new
    /// command family: <c>SendAmd</c> already writes <c>TXMSG 9</c> and the
    /// parser already mirrors the listing. What is new is the WHOLE STORE,
    /// which the radio CLONE must carry because ERASE spares stored messages
    /// (owner ruling R8, full-radio scope) — consciously reversing the W1
    /// "whole-store editing stays out" named skip.</summary>
    private static readonly string[] AleMessageStoreBuilderNames =
        ["QueryTxMessages", "StoreTxMessage", "DeleteTxMessage", "ForgetStoredMessages",
         // Stage 9 closed 2026-08-24 (plan-ale-linked-amd-inbox.md): the
         // received store gains its write side - DEL provisional, the
         // TXMSG-family precedent. QueryRxMessages predates the capture and
         // stays in the surface-permitted bucket (X8 disjointness).
         "DeleteRxMessage", "ForgetReceivedMessages"];

    /// <summary>Their surface wrappers. Textually distinct from every Core
    /// builder name, like X8's.</summary>
    private static readonly string[] AleMessageStoreWrapperNames =
        ["RequestStoredMessages", "ProgramStoredMessage", "RemoveStoredMessage", "ForgetReportedMessages"];

    /// <summary>The received-store wrappers (Stage 9 closed 2026-08-24,
    /// linked-amd round). Their ONE consumer is the Inbox
    /// (MessagesViewModel); the clone campaigns do not touch the received
    /// store — it is the other station's traffic, not this radio's fill.</summary>
    private static readonly string[] AleReceivedStoreWrapperNames =
        ["RefreshRxMessages", "RemoveReceivedMessage"];

    [Fact]
    public void TheReceivedStoreWrappers_AppearOnlyInTheSurfaceAndTheInbox()
        => AssertIdentifiersAppearOnlyIn(AleReceivedStoreWrapperNames,
            [@"src\Falcon.App.Core\Surfaces\AleSurface.cs",
             @"src\Falcon.App.Core\ViewModels\MessagesViewModel.cs"]);

    [Fact]
    public void X10_TheMessageStoreBuilders_AreRealAndAreNotGuarded()
    {
        var methods = typeof(Modes.AleController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in AleMessageStoreBuilderNames)
        {
            Assert.Contains(name, methods);                 // a real builder
            Assert.DoesNotContain(name, GuiOutBuilderNames); // never guard-scoped
        }

        // The amendment's SIZE is the pin: X10's four names (three senders
        // and the mirror clear), plus the received store's two (linked-amd
        // round, Stage 9 closed 2026-08-24 — DEL and its mirror clear; the
        // listing sender predates the capture in the surface bucket).
        Assert.Equal(6, AleMessageStoreBuilderNames.Length);
    }

    [Fact]
    public void X10_TheMessageStoreBuilders_AppearOnlyInTheAleSurface()
        => AssertIdentifiersAppearOnlyIn(AleMessageStoreBuilderNames, [AleSurfaceFile]);

    [Fact]
    public void X10_TheMessageStoreWrappers_AppearOnlyInTheSurfaceAndTheCloneCampaign()
    {
        // The whole store has exactly ONE app-layer consumer: the clone. A
        // reference anywhere else is a new feature, not an implementation
        // detail, and it fails here.
        AssertIdentifiersAppearOnlyIn(AleMessageStoreWrapperNames, [AleSurfaceFile, CloneServiceFile]);

        // Anti-vacuity: the surface really declares all four (a rename would
        // otherwise make the scan pass by matching nothing).
        var surface = ScannableText(Path.Combine(FindRepoRoot(), AleSurfaceFile));
        foreach (var name in AleMessageStoreWrapperNames)
            Assert.True(ReferencesIdentifier(surface, name), "AleSurface no longer declares " + name);
    }

    [Fact]
    public void X8_TheFourAleBuckets_ArePairwiseDisjoint()
    {
        (string Name, string[] Names)[] buckets =
        [
            ("ale-editor-permitted", AleEditorPermittedBuilderNames),
            ("ale-surface-permitted", AleSurfacePermittedBuilderNames),
            ("ale-message-store (X10)", AleMessageStoreBuilderNames),
            ("guarded", GuiOutBuilderNames),
        ];

        var overlaps = new List<string>();
        for (int i = 0; i < buckets.Length; i++)
            for (int j = i + 1; j < buckets.Length; j++)
                foreach (var shared in buckets[i].Names.Intersect(buckets[j].Names, StringComparer.Ordinal))
                    overlaps.Add($"{shared} is in BOTH {buckets[i].Name} and {buckets[j].Name}");

        Assert.Empty(overlaps);
        Assert.All(buckets, b => Assert.NotEmpty(b.Names));
        // X10 added a FOURTH bucket, so the pairwise count moves with it — the
        // arithmetic sits here so a bucket added without thinking about
        // disjointness cannot slip past a stale constant.
        Assert.Equal(6, buckets.Length * (buckets.Length - 1) / 2);
    }

    [Fact]
    public void EveryAleBuilder_IsAccountedFor_AndTheRemainderIsEmpty()
    {
        // Computed from the Core type DOWN (the X6 lesson): every public
        // AleController builder must land in exactly one bucket, and a
        // builder added tomorrow and listed nowhere shows up in the
        // remainder.
        var builders = typeof(Modes.AleController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(Modes.AleController))   // not object's
            .Where(m => !m.IsSpecialName)                                 // not the timeout accessors
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(builders);      // anti-vacuity: reflection found the type

        var remainder = builders
            .Except(AleEditorPermittedBuilderNames, StringComparer.Ordinal)
            .Except(AleSurfacePermittedBuilderNames, StringComparer.Ordinal)
            .Except(AleMessageStoreBuilderNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // THE REMAINDER ADAPTATION (§4.2.6), stated rather than copied: after
        // X8 the ALE guarded remainder is EMPTY. X6 asserts its remainder is
        // NONEMPTY, but that is a HopController fact (exclusion bands, DOIT),
        // not a law — every ALE builder now has a home, and pinning the empty
        // list is what makes a NEW un-triaged ALE builder fail this test.
        Assert.Empty(remainder);

        // …and the two buckets really are the whole surface: an entry that
        // named nothing real would let the subtraction above pass vacuously.
        foreach (var name in AleEditorPermittedBuilderNames) Assert.Contains(name, builders);
        foreach (var name in AleSurfacePermittedBuilderNames) Assert.Contains(name, builders);
        foreach (var name in AleMessageStoreBuilderNames) Assert.Contains(name, builders);
    }

    private static void AssertNamesAppearOnlyIn(
        IReadOnlyList<string> names, IReadOnlyList<string> allowedRelativePaths)
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in AppLayerSourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            if (allowedRelativePaths.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;

            var text = ScannableText(file);
            foreach (var name in names)
                if (text.Contains(name, StringComparison.Ordinal))
                    offenders.Add(relative + " references " + name);
        }

        Assert.Empty(offenders);
    }

    // ---- The scan is STRUCTURAL, not textual (C1 audit round 1, MAJOR) ------
    // The scans used to read raw bytes, so a name inside a doc comment or a
    // string literal counted as a call site, and a COMMENTED-OUT method
    // satisfied the wrapper-existence pin. Neither is a reference to anything.
    // C# is stripped of comments and literals before matching; XAML is parsed
    // as the XML it is, so an XML comment disappears for free (the
    // RefreshButtonWidthGuardTests lesson: no tag-shaped regex can be right).
    //
    // ACCEPTED LIMITATION, recorded rather than chased: stripping literals also
    // hides a name used INSIDE an interpolated string hole. That is adversarial
    // construction, not a plausible regression, and the same deferral the
    // Refresh-width guard records.

    /// <summary>The text a scan may draw conclusions from: code for C#,
    /// attribute values and element text for XAML.</summary>
    private static string ScannableText(string file)
    {
        var raw = File.ReadAllText(file);
        return file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            ? XamlScannableText(raw)
            : DecodeUnicodeEscapes(StripCommentsAndLiterals(raw));
    }

    /// <summary>
    /// Decode C# <c>\uXXXX</c> / <c>\UXXXXXXXX</c> escapes, which are legal
    /// INSIDE IDENTIFIERS — <c>_ssb.ZeroizeRadio()</c> compiles and calls
    /// <c>ZeroizeRadio</c>.
    ///
    /// <para><b>Why this exists (audit round 1, finding 3).</b> Every scan in
    /// this file follows NAMES, and a name spelled with an escape is a
    /// different string to a regex and the same method to the compiler. The
    /// auditor planted exactly that call in a view model and the X12/X13
    /// placement guard passed. Decoding is applied AFTER the comment/literal
    /// strip, so an escape that only ever appeared inside a string literal is
    /// already gone and cannot resurrect as a false positive.</para>
    /// </summary>
    internal static string DecodeUnicodeEscapes(string stripped)
    {
        if (!stripped.Contains('\\')) return stripped;

        var sb = new System.Text.StringBuilder(stripped.Length);
        int i = 0;
        while (i < stripped.Length)
        {
            if (stripped[i] == '\\' && i + 1 < stripped.Length
                && (stripped[i + 1] == 'u' || stripped[i + 1] == 'U'))
            {
                int digits = stripped[i + 1] == 'u' ? 4 : 8;
                if (i + 2 + digits <= stripped.Length
                    && int.TryParse(stripped.AsSpan(i + 2, digits),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int code)
                    && code is >= 0 and <= 0x10FFFF)
                {
                    sb.Append(char.ConvertFromUtf32(code));
                    i += 2 + digits;
                    continue;
                }
            }
            sb.Append(stripped[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Attribute values + text nodes. XML comments are XComment nodes
    /// and never appear. Unparseable markup falls back to the raw text: a
    /// guard that goes BLIND on a malformed file is worse than one that
    /// over-reports.</summary>
    private static string XamlScannableText(string markup)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(markup);
            var parts = doc.Descendants()
                .SelectMany(e => e.Attributes().Select(a => a.Value)
                    .Concat(e.Nodes().OfType<System.Xml.Linq.XText>().Select(t => t.Value)));
            return string.Join("\n", parts);
        }
        catch (System.Xml.XmlException)
        {
            return markup;
        }
    }

    /// <summary>C# with comments, string literals and char literals replaced by
    /// a space (a space, not nothing, so removing a block comment cannot GLUE
    /// two identifiers into a third).</summary>
    internal static string StripCommentsAndLiterals(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char ch = source[i];

            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                sb.Append(' ');
            }
            else if (ch == '"' && QuoteRun(source, i) >= 3)
            {
                // Raw string literal: opened by N>=3 quotes, closed by the next
                // run of at least N.
                int open = QuoteRun(source, i);
                i += open;
                while (i < source.Length && QuoteRun(source, i) < open) i++;
                i += i < source.Length ? QuoteRun(source, i) : 0;
                sb.Append(' ');
            }
            else if (ch == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
            }
            else if (ch is '"' or '\'')
            {
                char quote = ch;
                i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }
                if (i < source.Length && source[i] == quote) i++;
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }
        return sb.ToString();
    }

    private static int QuoteRun(string s, int start)
    {
        int n = 0;
        while (start + n < s.Length && s[start + n] == '"') n++;
        return n;
    }

    [Fact]
    public void TheScanner_SeesCode_AndIgnoresCommentsAndLiterals()
    {
        // The stripper is now load-bearing for every scan in this file, so it
        // is pinned as a unit rather than trusted. A stripper that returned ""
        // would silently disarm all four scans — hence the positive half.
        const string sample = """
            // ClearNet in a line comment
            /* ClearNet in a block comment */
            var s = "ClearNet in a string";
            var v = @"ClearNet in a verbatim string";
            var c = 'x';
            RealCall(ClearNet);
            """;

        var stripped = StripCommentsAndLiterals(sample);

        Assert.Equal(1, CountOccurrences(stripped, "ClearNet"));   // only the real call
        Assert.Contains("RealCall", stripped);
    }

    [Fact]
    public void TheScanner_DoesNotGlueIdentifiersTogether()
    {
        // Removing a comment must not turn "Program/*x*/HopList" into a name
        // that was never written — a false POSITIVE is as bad as a false
        // negative for a guard nobody can easily debug.
        Assert.DoesNotContain("ProgramHopList", StripCommentsAndLiterals("Program/*x*/HopList"));
    }

    [Fact]
    public void TheXamlScanner_ReadsBindings_AndIgnoresCommentedMarkup()
    {
        const string markup = """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <!-- Command="{Binding ClearNetCommand}" -->
              <Button Command="{Binding RequestNetWipeCommand}" Text="Clear net" />
            </ContentView>
            """;

        var scannable = XamlScannableText(markup);

        Assert.Contains("RequestNetWipeCommand", scannable);
        Assert.DoesNotContain("ClearNetCommand", scannable);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>The scanned wrapper list is pinned against the surface itself,
    /// so renaming a wrapper cannot silently orphan the scan the way the
    /// original Core-only list silently missed all five.</summary>
    [Fact]
    public void Round3Amendment_EveryScannedWrapperName_ExistsOnTheHopSurface()
    {
        var surface = Type.GetType(
            "Falcon.App.Core.Surfaces.HopSurface, Falcon.App.Core", throwOnError: false);

        // Falcon.Core.Tests does not reference Falcon.App.Core (Core must not
        // depend on the app layer), so fall back to the source text — the same
        // authority the scan itself uses.
        if (surface is null)
        {
            // Stripped, so a COMMENTED-OUT "public void ClearNet" cannot
            // satisfy this pin (C1 audit round 1, MAJOR) — the existence check
            // has to see a declaration, not a mention of one.
            //
            // ROUND 11 §9A: the SENTINEL-SCOPED READS return their operation's
            // READ ID rather than void, because a caller that must know whether
            // THIS read committed matches the id against the domain's completion
            // record — judging it by any other sentinel judges a different
            // question. So those wrappers are pinned as `public long`, exactly
            // as AleSurface's read wrappers already are.
            var text = ScannableText(Path.Combine(FindRepoRoot(), HopSurfaceFile));
            foreach (var name in EditorPermittedWrapperNames)
                Assert.Contains(
                    (HopReadWrappersReturningAReadId.Contains(name, StringComparer.Ordinal)
                        ? "public long " : "public void ") + name,
                    text, StringComparison.Ordinal);
            return;
        }

        var methods = surface
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();
        foreach (var name in EditorPermittedWrapperNames)
            Assert.Contains(name, methods);
    }

    /// <summary>Anti-vacuity for the shape split above: the read-id list must
    /// name real wrappers, and must be a PROPER subset — a list that had
    /// swallowed every name would make the `public void` half assert
    /// nothing.</summary>
    [Fact]
    public void TheHopReadIdWrappers_AreRealWrappers_AndNotAllOfThem()
    {
        foreach (var name in HopReadWrappersReturningAReadId)
            Assert.Contains(name, EditorPermittedWrapperNames);

        Assert.True(HopReadWrappersReturningAReadId.Length < EditorPermittedWrapperNames.Length);
        // The plain read that does NOT return an id — the distinction is
        // "has a sentinel-scoped operation", not "is a read".
        Assert.DoesNotContain("RequestHopList", HopReadWrappersReturningAReadId);
    }

    // ====================================================================
    // CLONE ROUND 12 — X12 (operator lockouts) and X13 (zeroize).
    //
    // TWO THINGS MAKE THIS BLOCK DIFFERENT FROM X8/X9, and both are recorded
    // rather than worked around:
    //
    // 1. THE BUILDER AND WRAPPER NAMES COINCIDE. plan-clone-round12 §3 names
    //    the surface wrappers `SetLockout` and `ZeroizeRadio`, which are also
    //    the Core builders' names. X8 forbids that overlap for the ALE family
    //    because two scans with two allow-lists would contradict each other —
    //    so X12/X13 run ONE scan over the UNION of both name sets, with ONE
    //    allow-list. `QueryLockouts` (Core-only, no wrapper of that name) still
    //    gets the stricter surface-only treatment, which is what keeps the
    //    "a view model must not reach past the surface" rule real here.
    //
    // 2. THE CONSUMER NOW EXISTS — TIGHTENED TO THE EXACT-REFERENCE FORM
    //    (clone round 12 P2). P1 could only assert the SUBSET shape ("every
    //    X12/X13 name CloneService references is permitted"), because
    //    referencing NONE satisfied it and the campaign had not landed. P2's
    //    campaign consumes all three wrappers, so the pin is now the same
    //    EXACT-reference manifest X11 uses: a permitted name that STOPS being
    //    referenced fails just as loudly as an unpermitted one that starts.
    // ====================================================================

    private static readonly string SsbSurfaceFile =
        Path.Combine("src", "Falcon.App.Core", "Surfaces", "SsbSurface.cs");

    /// <summary>X12's Core builders. <c>QueryLockouts</c> is Core-ONLY (its
    /// wrapper is <c>RequestLockouts</c>); <c>SetLockout</c> shares its name
    /// with the wrapper — see note 1 above.</summary>
    private static readonly string[] X12CoreBuilderNames = ["QueryLockouts", "SetLockout"];

    /// <summary>X13's Core builder — same name as its wrapper.</summary>
    private static readonly string[] X13CoreBuilderNames = ["ZeroizeRadio"];

    /// <summary>The X12 surface wrappers (plan §3 names them explicitly).</summary>
    private static readonly string[] X12WrapperNames = ["RequestLockouts", "SetLockout"];

    /// <summary>The X13 surface wrapper.</summary>
    private static readonly string[] X13WrapperNames = ["ZeroizeRadio"];

    /// <summary>Everything the X12/X13 scan follows: builders ∪ wrappers.</summary>
    private static readonly string[] X12X13ScannedNames =
        [.. X12CoreBuilderNames.Concat(X13CoreBuilderNames)
             .Concat(X12WrapperNames).Concat(X13WrapperNames)
             .Distinct(StringComparer.Ordinal)];

    /// <summary>The Core-only name: it has no wrapper of the same spelling, so
    /// it may appear in the DECLARING SURFACE and nowhere else — a campaign
    /// that named it would be reaching past the surface.</summary>
    private static readonly string[] X12SurfaceOnlyNames = ["QueryLockouts"];

    /// <summary>Where an X12/X13 name may appear at all: the declaring surface
    /// (the WrapperAllowedFiles idiom — a surface must be able to name what it
    /// declares) plus the ONE authorized consumer.</summary>
    private static readonly string[] X12X13AllowedFiles = [SsbSurfaceFile, CloneServiceFile];

    /// <summary>The CLOSED manifest of names the clone campaign may reach —
    /// the WRAPPERS only. P2 asserts the EXACT-reference form (see note 2):
    /// every one of these is referenced, and nothing else is.</summary>
    private static readonly string[] CloneServicePermittedLockoutWrappers =
        [.. X12WrapperNames.Concat(X13WrapperNames).Distinct(StringComparer.Ordinal)];

    [Fact]
    public void X12X13_TheNames_AppearOnlyInTheDeclaringSurfaceAndTheCloneCampaign()
    {
        AssertIdentifiersAppearOnlyIn(X12X13ScannedNames, X12X13AllowedFiles);

        // Anti-vacuity: the surface really declares every scanned wrapper, so
        // the scan is matching real identifiers rather than passing on a
        // rename. (Without this the whole block would be satisfied by deleting
        // the feature.)
        var surface = ScannableText(Path.Combine(FindRepoRoot(), SsbSurfaceFile));
        foreach (var name in X12WrapperNames.Concat(X13WrapperNames))
            Assert.True(ReferencesIdentifier(surface, name), "SsbSurface no longer declares " + name);
    }

    [Fact]
    public void X12_TheCoreOnlyBuilder_AppearsOnlyInTheDeclaringSurface()
    {
        // The stricter half: a name with no same-spelled wrapper gets the X8
        // "builders only in the surface" rule, so the campaign cannot reach
        // past the surface for the lockout READ.
        AssertIdentifiersAppearOnlyIn(X12SurfaceOnlyNames, [SsbSurfaceFile]);

        var surface = ScannableText(Path.Combine(FindRepoRoot(), SsbSurfaceFile));
        Assert.True(ReferencesIdentifier(surface, "QueryLockouts"));
    }

    [Fact]
    public void X12X13_CloneService_ReachesExactlyThePermittedManifest()
    {
        // THE EXACT-REFERENCE FORM (P2), the X11 shape: for every scanned name,
        // "is it referenced" must EQUAL "is it permitted". A campaign that
        // reached the Core builder `QueryLockouts` directly fails — and so does
        // one that quietly stopped writing the lockouts at all, which the P1
        // subset form could not tell from a campaign that had not landed yet.
        var text = ScannableText(Path.Combine(FindRepoRoot(), CloneServiceFile));

        foreach (var name in X12X13ScannedNames)
        {
            bool permitted = CloneServicePermittedLockoutWrappers.Contains(name, StringComparer.Ordinal);
            Assert.Equal(permitted, ReferencesIdentifier(text, name));
        }

        // Anti-vacuity: the manifest is a PROPER subset of what the scan covers
        // (the Core-only read builder really is withheld), and the scanner
        // really can see a call.
        foreach (var name in CloneServicePermittedLockoutWrappers)
            Assert.Contains(name, X12X13ScannedNames);
        Assert.Contains("QueryLockouts", X12X13ScannedNames);
        Assert.DoesNotContain("QueryLockouts", CloneServicePermittedLockoutWrappers);
        Assert.True(ReferencesIdentifier("surface.RequestLockouts();", "RequestLockouts"));
    }

    /// <summary>
    /// THE SELF-PIN for the X12/X13 scan's own machinery (audit round 1,
    /// finding 3). Every placement pin in this block is worth exactly what the
    /// scanner can see, so the scanner is tested against the evasions it must
    /// catch and the near-misses it must not report — INCLUDING the
    /// UNICODE-ESCAPED call that got past the first version.
    /// </summary>
    [Fact]
    public void X12X13_TheScanner_CatchesTheEscapedCall_AndStillIgnoresCommentsAndLiterals()
    {
        // The escape introducers are BUILT AT RUNTIME, never typed as source
        // literals: a file cannot hold both the UNDECODED escape and the
        // character it decodes to in the same token, and this pin needs the
        // undecoded form to reach the scanner.
        string u4 = B + "u";        // the head of a 4-digit identifier escape
        string u8 = B + "U";        // …and of the 8-digit form
        Assert.Contains(B, u4, StringComparison.Ordinal);   // the input really is undecoded
        Assert.Contains(B, u8, StringComparison.Ordinal);

        // 1. THE EVASION THAT WORKED (audit round 1, finding 3). An escape is
        //    legal INSIDE AN IDENTIFIER, so this line COMPILES and calls
        //    ZeroizeRadio — and the raw-regex scan did not see it.
        var escapedCall = "void Wipe() { _ssb." + u4 + "005AeroizeRadio(); }";
        Assert.True(ReferencesIdentifier(ScannableSource(escapedCall), "ZeroizeRadio"),
            "an escaped identifier must be caught — this is finding 3");

        // 2. …the same for a wrapper name, and for the 8-digit form.
        Assert.True(ReferencesIdentifier(
            ScannableSource("surface." + u4 + "0052equestLockouts();"), "RequestLockouts"));
        Assert.True(ReferencesIdentifier(
            ScannableSource("surface." + u8 + "0000005AeroizeRadio();"), "ZeroizeRadio"));

        // 3. A PLAIN call is still caught — a decoder that ate everything
        //    would disarm the scan as thoroughly as one that decoded nothing.
        Assert.True(ReferencesIdentifier(ScannableSource("_ssb.ZeroizeRadio();"), "ZeroizeRadio"));

        // 4. …and the strip still wins where it should: a mention in a
        //    comment or a string literal is not a call, escaped or not.
        Assert.False(ReferencesIdentifier(
            ScannableSource("// call _ssb.ZeroizeRadio() one day"), "ZeroizeRadio"));
        Assert.False(ReferencesIdentifier(
            ScannableSource("var s = " + Quote + "ZeroizeRadio" + Quote + ";"), "ZeroizeRadio"));
        Assert.False(ReferencesIdentifier(
            ScannableSource("var s = " + Quote + u4 + "005AeroizeRadio" + Quote + ";"), "ZeroizeRadio"));

        // 5. Boundary matching survives decoding: a LONGER identifier that
        //    merely contains the name is not a reference to it.
        Assert.False(ReferencesIdentifier(
            ScannableSource("vm." + u4 + "005AeroizeRadioTwice();"), "ZeroizeRadio"));
    }

    /// <summary>A backslash, and a double quote, as VALUES — see the pin
    /// above for why neither may be typed into the strings it builds.</summary>
    private const string B = "\\";
    private const string Quote = "\"";

    /// <summary>The exact pipeline <see cref="ScannableText"/> applies to a
    /// <c>.cs</c> file, for the self-pin above — so the pin exercises the
    /// real scanner rather than a lookalike.</summary>
    private static string ScannableSource(string source)
        => DecodeUnicodeEscapes(StripCommentsAndLiterals(source));

    [Fact]
    public void X12X13_TheAmendmentsSize_AndTheirBuildersAreReal()
    {
        // The amendment's SIZE is the pin, exactly as X6/X8/X9 do it: X12 is
        // TWO Core builders (one read operation, one set) and X13 is ONE.
        // A fourth would have to be written here.
        Assert.Equal(2, X12CoreBuilderNames.Length);
        Assert.Single(X13CoreBuilderNames);
        Assert.Equal(["QueryLockouts", "SetLockout"], X12CoreBuilderNames.Order(StringComparer.Ordinal));
        Assert.Equal(["ZeroizeRadio"], X13CoreBuilderNames);

        var methods = typeof(Modes.SsbController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(Modes.SsbController))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(methods);       // anti-vacuity: reflection found the type
        foreach (var name in X12CoreBuilderNames.Concat(X13CoreBuilderNames))
            Assert.Contains(name, methods);

        // X12 is the round's ONLY new command family (invariant 1), and X13 is
        // a new BUILDER inside an existing posture — neither may be smuggled
        // into the blanket GUI-out list, which would make the file scan above
        // unsatisfiable for the surface that declares them.
        foreach (var name in X12X13ScannedNames)
            Assert.DoesNotContain(name, GuiOutBuilderNames);
    }

    [Fact]
    public void X12X13_TheAllowList_IsExactlyTheSurfaceAndTheCampaign_AndBothExist()
    {
        // An allow-list is a DECISION (the C1 lesson): adding a file silently
        // widens the amendment, dropping one silently narrows it, and a rename
        // would leave it pointing at nothing.
        Assert.Equal(
            [
                Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs"),
                Path.Combine("src", "Falcon.App.Core", "Surfaces", "SsbSurface.cs"),
            ],
            X12X13AllowedFiles.Order(StringComparer.Ordinal));

        var root = FindRepoRoot();
        foreach (var relative in X12X13AllowedFiles)
            Assert.True(File.Exists(Path.Combine(root, relative)), "allow-listed file missing: " + relative);
    }

    private static IEnumerable<string> AppLayerSourceFiles(string root)
    {
        string[] appLayers =
        [
            Path.Combine(root, "src", "Falcon.App.Core"),
            Path.Combine(root, "src", "Falcon.App"),
        ];

        foreach (var layer in appLayers)
        {
            Assert.True(Directory.Exists(layer), "app-layer directory missing: " + layer);
            foreach (var file in Directory.EnumerateFiles(layer, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = Path.GetRelativePath(root, file);
                if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                yield return file;
            }
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
