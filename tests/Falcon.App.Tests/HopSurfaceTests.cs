using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Tests;

/// <summary>
/// The round-11 §7 GENERATION-ATTEMPT STATE MACHINE, which lives on
/// <see cref="HopSurface"/> rather than in either HOP view model.
///
/// <para><b>Why the surface owns it.</b> The triggers span BOTH view models —
/// the net select is HopViewModel's, every hopset-family write is
/// HopSettingsViewModel's — and both resolve the same DI singleton surface. A
/// VM-side machine would need the two to talk to each other about a fact
/// neither of them owns.</para>
///
/// <para><b>Why a counter and not a flag.</b> Core reports <c>NO NET ID</c> as
/// a COUNT (a repeat carries no state change of its own for a consumer to
/// observe), so the machine SNAPSHOTS the count at each trigger and diffs it.
/// The contract, in four parts: a trigger snapshots; an increment inside the
/// window sets the refusal; the NEXT trigger clears it; and a report OUTSIDE
/// any window sets nothing, because it refuses nothing this app asked for.</para>
/// </summary>
public class HopSurfaceTests : SessionTestBase
{
    private HopSurface Surface() => new(Radio);

    /// <summary>Ready + confirmed HOP, with the ritual sends drained.</summary>
    private void EnterHop()
    {
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();
    }

    /// <summary>The captured ASYNC form (docs/protocol.md, the `NET n` select
    /// echo): a net that HAS a hopset but no ID answers this and generates
    /// nothing.</summary>
    private void ReportNoNetId() => Transport.InjectLine("NO NET ID");

    // ---- The §7 trigger manifest, CLOSED ------------------------------------

    /// <summary>Every wrapper §7 enumerates as a generation trigger: the net
    /// select, plus the seven hopset-family writes the editor uses. Named so
    /// the theory below runs once per wrapper and a wrapper added without a
    /// window shows up as a missing case rather than as silence.</summary>
    public static TheoryData<string> TriggerNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Triggers.Keys) data.Add(name);
            return data;
        }
    }

    private static readonly IReadOnlyDictionary<string, Action<HopSurface>> Triggers =
        new Dictionary<string, Action<HopSurface>>(StringComparer.Ordinal)
        {
            ["SelectNet"] = s => s.SelectNet(3),
            // ROUND 14 C (plan §4-C): the operator's select route. It sends the
            // same `NET n`, so it is a §7 trigger and the CLOSED manifest gains
            // it DELIBERATELY — the plan names this addition. (The coupler word
            // it may prepend is not a trigger; the INTCOUPLER pins below say
            // why, and this entry passes null for the type so this theory tests
            // the window and nothing else.)
            ["SelectNetWithCouplerPolicy"] = s => s.SelectNetWithCouplerPolicy(3, null),
            ["ProgramNetId"] = s => s.ProgramNetId(3, "12345678"),
            ["ProgramHopType"] = s => s.ProgramHopType(3, HopType.Wideband),
            ["ProgramNarrowbandHopset"] = s => s.ProgramNarrowbandHopset(3, "11565"),
            ["ProgramWidebandHopset"] = s => s.ProgramWidebandHopset(3, "02000", "08000"),
            ["ProgramHopList"] = s => s.ProgramHopList(3, "11010"),
            ["RemoveHopListFrequency"] = s => s.RemoveHopListFrequency(3, "11010"),
            ["ClearNet"] = s => s.ClearNet(3),
        };

    [Theory]
    [MemberData(nameof(TriggerNames))]
    public void EveryEnumeratedWrapper_OpensAWindow(string trigger)
    {
        // The pin §7 asks for by name. Before the trigger a NO NET ID means
        // nothing; after it, the same line is a refusal — which is only true
        // if THIS wrapper opened a window.
        var surface = Surface();
        EnterHop();

        ReportNoNetId();
        Assert.False(surface.GenerationRefusedNoNetId);

        Triggers[trigger](surface);
        ReportNoNetId();

        Assert.True(surface.GenerationRefusedNoNetId);
    }

    [Fact]
    public void TheTriggerManifest_IsExactlyTheNineNamesSectionSevenEnumerates()
    {
        // A closed manifest is a DECISION, so its membership is pinned as a set
        // — the failure a per-entry theory cannot see is an entry quietly
        // missing, which leaves that wrapper unguarded.
        //
        // The wrappers deliberately OUTSIDE it, each with its own pin further
        // down: the two `EXC` writers, and (round 14 B) the two `INTCOUPLER`
        // ones. Adding a member to HopSurface owes an answer here either way.
        //
        // ROUND 14 C widened it by ONE, deliberately and by plan (§4-C):
        // `SelectNetWithCouplerPolicy` is the operator's route to the SAME
        // `NET n`, so it opens the same window.
        Assert.Equal(
            [
                "ClearNet", "ProgramHopList", "ProgramHopType",
                "ProgramNarrowbandHopset", "ProgramNetId", "ProgramWidebandHopset",
                "RemoveHopListFrequency", "SelectNet", "SelectNetWithCouplerPolicy",
            ],
            Triggers.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheExclusionWrappers_AreDeliberatelyNOT_Triggers()
    {
        // RECORDED DECISION, not an oversight. `EXC` writes DO regenerate the
        // hopset (docs/protocol.md), so on mechanism alone they look like
        // triggers — but §7's enumeration is a CLOSED manifest of the net
        // select and the hopset-family writes, and no probe has ever seen a
        // no-net-id answer to an exclusion write. Widening the manifest is a
        // plan amendment; this pin makes the narrow reading visible instead of
        // leaving it to be discovered.
        var surface = Surface();
        EnterHop();

        surface.ProgramExcludeBand(0, "02000000", "03000000");
        ReportNoNetId();
        Assert.False(surface.GenerationRefusedNoNetId);

        surface.RemoveExcludeBand(0);
        ReportNoNetId();
        Assert.False(surface.GenerationRefusedNoNetId);
    }

    // ---- The four contract parts --------------------------------------------

    [Fact]
    public void AReportWithNoTriggerAtAll_SetsNothing()
    {
        // The snapshot's whole point. An unsolicited NO NET ID — or a straggler
        // answering a query sent before anything was asked of the radio — is
        // not a refusal of an operator's action, and must not be reported as
        // one.
        var surface = Surface();
        EnterHop();

        ReportNoNetId();
        ReportNoNetId();

        Assert.False(surface.GenerationRefusedNoNetId);
    }

    [Fact]
    public void AnIncrementInsideTheWindow_SetsTheRefusal_AndRaisesItsOwnEvent()
    {
        var surface = Surface();
        EnterHop();

        int raises = 0;
        surface.GenerationRefusedNoNetIdChanged += (_, _) => raises++;

        surface.SelectNet(3);
        Assert.False(surface.GenerationRefusedNoNetId);
        Assert.Equal(0, raises);              // opening a window changes nothing

        ReportNoNetId();

        Assert.True(surface.GenerationRefusedNoNetId);
        Assert.Equal(1, raises);
    }

    [Fact]
    public void TheEvent_IsRaisedOnTheEDGE_NotOnEveryReport()
    {
        // A repeat report inside the same window is the same answer to the same
        // question. Re-raising would make a consumer redraw for no change —
        // and, worse, would make "the refusal changed" untrustworthy.
        var surface = Surface();
        EnterHop();

        surface.SelectNet(3);
        int raises = 0;
        surface.GenerationRefusedNoNetIdChanged += (_, _) => raises++;

        ReportNoNetId();
        ReportNoNetId();
        ReportNoNetId();

        Assert.True(surface.GenerationRefusedNoNetId);
        Assert.Equal(1, raises);
    }

    [Fact]
    public void TheNextTrigger_ClearsIt_AndRaisesAgain()
    {
        var surface = Surface();
        EnterHop();

        surface.SelectNet(3);
        ReportNoNetId();
        Assert.True(surface.GenerationRefusedNoNetId);

        int raises = 0;
        surface.GenerationRefusedNoNetIdChanged += (_, _) => raises++;

        // §7: the NEXT trigger clears it — the operator has asked for something
        // new, so last time's answer comes off the screen at the moment the
        // question changes, not when the answer arrives.
        surface.ProgramNetId(3, "12345678");

        Assert.False(surface.GenerationRefusedNoNetId);
        Assert.Equal(1, raises);
    }

    [Fact]
    public void TheClearingTrigger_CanBeAnyOfTheEight_NotOnlyTheOneThatSetIt()
    {
        // Cross-VM by construction: the select that drew the refusal is the
        // Operate pane's, and the write that clears it is the settings pane's.
        // Both go through this one surface, which is the whole reason the state
        // lives here.
        foreach (var (name, trigger) in Triggers)
        {
            var surface = Surface();
            surface.SelectNet(3);
            ReportNoNetId();
            Assert.True(surface.GenerationRefusedNoNetId, name);

            trigger(surface);
            Assert.False(surface.GenerationRefusedNoNetId, name);
        }
    }

    [Fact]
    public void ASecondWindow_WithNoReport_LeavesTheRefusalClear()
    {
        // The window is a diff, not a latch: a trigger that draws no NO NET ID
        // must not inherit the previous window's answer.
        var surface = Surface();
        EnterHop();

        surface.SelectNet(3);
        ReportNoNetId();
        Assert.True(surface.GenerationRefusedNoNetId);

        surface.SelectNet(4);
        Transport.InjectLine("Hopnum 0041");           // a NORMAL outcome

        Assert.False(surface.GenerationRefusedNoNetId);
    }

    // ---- The reconnect leg (the sanctioned Core fix, from this side) --------

    [Fact]
    public void AReconnect_DropsTheRefusal_TheCounterStepsBackPastTheSnapshot()
    {
        // Core's ResetForConnect zeroes the No-Net-ID counter (round-11 P4).
        // That is what makes a fresh connection DETECTABLE here: the live count
        // falls below what this surface has already seen, so the open window
        // and anything it produced are known to describe a radio that is gone.
        // Without the Core fix the count would only ever rise, and a refusal
        // raised by the previous radio would still be on screen.
        var surface = Surface();
        EnterHop();

        surface.SelectNet(3);
        ReportNoNetId();
        Assert.True(surface.GenerationRefusedNoNetId);

        Session.Close();
        ConnectReady();

        Assert.Equal(0, Radio.State.Hop.NoNetIdCount);
        Assert.False(surface.GenerationRefusedNoNetId);
    }

    [Fact]
    public void AfterAReconnect_TheMachineStillWorks_FromAFreshSnapshot()
    {
        // …and the abandoned window does not leave the machine wedged: the next
        // session's own trigger + report behaves exactly like the first.
        var surface = Surface();
        EnterHop();
        surface.SelectNet(3);
        ReportNoNetId();

        Session.Close();
        ConnectReady();
        Transport.InjectLine("HOP>");

        ReportNoNetId();                              // no window yet
        Assert.False(surface.GenerationRefusedNoNetId);

        surface.SelectNet(5);
        ReportNoNetId();
        Assert.True(surface.GenerationRefusedNoNetId);
    }

    [Fact]
    public void TheWindowOpens_ATENTRY_NotWhenAnOutcomeArrives()
    {
        // §7 says "snapshots AT ENTRY". The observable consequence: the state
        // change happens inside the trigger call itself, with the command only
        // just on the wire and NO answer to it yet. A machine that waited for
        // an outcome to re-arm would leave the previous refusal on screen while
        // the new command was in flight — the operator would read last time's
        // answer as this time's.
        var surface = Surface();
        EnterHop();

        surface.SelectNet(3);
        ReportNoNetId();
        Assert.True(surface.GenerationRefusedNoNetId);

        Transport.ClearSent();
        surface.SelectNet(4);

        Assert.Equal(["NET 4"], Transport.SentLines);   // sent, unanswered…
        Assert.False(surface.GenerationRefusedNoNetId); // …and already cleared
    }

    // ---- The exclusion mirror the section renders ---------------------------

    [Fact]
    public void ExcludeBands_SurfacesTheMirrorsThreeStates_Verbatim()
    {
        // The surface must PASS THROUGH the three states, not flatten them: a
        // null that arrived as an empty list is the difference between "the
        // radio says there are none" and "nobody has asked".
        var surface = Surface();
        EnterHop();
        Assert.Null(surface.ExcludeBands);

        surface.RequestExcludeBands();
        Assert.Equal(["EXC", "BAT ST"], Transport.SentLines);
        AnswerSentinel();
        Assert.Empty(surface.ExcludeBands!);

        Transport.ClearSent();
        surface.RequestExcludeBands();
        Transport.InjectLine("Exclude 00  02000   03000 ");
        AnswerSentinel();

        var row = Assert.Single(surface.ExcludeBands!);
        Assert.Equal(0, row.Band);
        Assert.Equal("02000", row.LowKHz);
        Assert.Equal("03000", row.HighKHz);
    }

    [Fact]
    public void TheExcludeWrappers_SendTheirBuilders_AndNothingElse()
    {
        var surface = Surface();
        EnterHop();

        surface.ProgramExcludeBand(4, "02000000", "03000000");
        Assert.Equal(["EXC 4 02000000 03000000"], Transport.SentLines);

        Transport.ClearSent();
        surface.RemoveExcludeBand(4);
        Assert.Equal(["EXC 4 DEL"], Transport.SentLines);
    }

    // ---- Round-14 B: the internal-coupler wrappers ---------------------------
    // plan/plan-round14.md §4-B, owner ruling R2. Two NEW public members on this
    // surface, so the §7 manifest above owes an answer about each of them — and
    // the answer is the same one the exclusion wrappers get.

    [Fact]
    public void TheCouplerWrappers_SendTheirBuilders_AndNothingElse()
    {
        // The wire forms, byte-exact. The SET spellings are the sent tokens
        // (Wire.cs BypassEnable) and the query is bare — both confirmed as sent
        // by P-1 (docs/protocol.md, "INTCOUPLER is FULLY GRADUATED").
        var surface = Surface();
        EnterHop();

        surface.SetInternalCoupler(BypassEnable.Bypass);
        Assert.Equal(["INTCOUPLER BYPASS"], Transport.SentLines);

        Transport.ClearSent();
        surface.SetInternalCoupler(BypassEnable.Enable);
        Assert.Equal(["INTCOUPLER ENABLE"], Transport.SentLines);

        Transport.ClearSent();
        surface.QueryInternalCoupler();
        Assert.Equal(["INTCOUPLER"], Transport.SentLines);
    }

    [Fact]
    public void TheCouplerWrappers_AreDeliberatelyNOT_Triggers()
    {
        // RECORDED DECISION, in the TheExclusionWrappers_… idiom directly above
        // — and the reason this pair is named here at all, since the §7
        // manifest is CLOSED and a new wrapper that is simply absent from it is
        // indistinguishable from one that was forgotten.
        //
        // Why not triggers: `INTCOUPLER` neither carries nor addresses a
        // hopset. The radio may retune its coupler afterwards (owner ruling
        // R11 — the radio owns that), but nothing generates, and no probe has
        // ever seen a NO NET ID answer to either form. Widening the manifest to
        // include them would make an unrelated straggler read as a refusal of
        // the operator's coupler press.
        var surface = Surface();
        EnterHop();

        surface.SetInternalCoupler(BypassEnable.Bypass);
        ReportNoNetId();
        Assert.False(surface.GenerationRefusedNoNetId);

        surface.QueryInternalCoupler();
        ReportNoNetId();
        Assert.False(surface.GenerationRefusedNoNetId);
    }

    [Fact]
    public void TheCouplerMirror_IsTheSameOneTheSsbSurfaceProjects()
    {
        // The two-placement fact, pinned: ONE Core mirror, two surfaces. If
        // this ever became a HOP-local copy the two panes could disagree, and
        // the round-14 row's whole safety argument would be gone.
        var hop = Surface();
        var ssb = new SsbSurface(Radio);
        EnterHop();

        Assert.False(hop.InternalCoupler.IsConfirmed);

        // The radio's own mixed-case answer; the parser uppercases it.
        Transport.InjectLine("INTCoupler Bypassed");

        Assert.Equal("BYPASSED", hop.InternalCoupler.Value);
        Assert.Equal(ssb.InternalCoupler.Value, hop.InternalCoupler.Value);
    }

    [Fact]
    public void TheCouplerMirror_IsWATCHED_SoTheSurfaceRaisesChangedForIt()
    {
        // The watched-set addition, pinned separately from the projection: a
        // projection nobody is notified about leaves the pane's choices frozen
        // until some unrelated HOP line happens to arrive.
        var surface = Surface();
        EnterHop();

        int raised = 0;
        surface.Changed += (_, _) => raised++;

        Transport.InjectLine("INTCoupler Bypassed");

        Assert.Equal(1, raised);
    }

    // ====================================================================
    // CLONE ROUND 12 §4 — the DIS-at-SSB trap.
    //
    // At an `SSB>` prompt `DIS 0` is NOT refused: `DI` is the minimum
    // abbreviation of the CHANNEL command, so `DIS` still matches it and the
    // radio answers a CHANNEL DUMP. The rows then parse perfectly — as
    // channels — and a caller expecting HOP nets files channel data as net
    // data with nothing anywhere saying so. (`SLFAD`, `CHG` and `EXC` at
    // `SSB>` all answer `** ERROR **` honestly; it is `DIS` specifically that
    // lands on another command.)
    // ====================================================================

    [Fact]
    public void TheNetReads_RefuseOffPrompt_RatherThanFileChannelRowsAsHopData()
    {
        var surface = Surface();
        ConnectReady();
        Transport.InjectLine("SSB>");          // confirmed SSB — the trap prompt
        Transport.ClearSent();

        Assert.False(surface.RequestAllNets());
        Assert.False(surface.RequestNet(3));
        Assert.Empty(Transport.SentLines);     // NOTHING went out
    }

    [Fact]
    public void TheNetReads_SendNormally_AtAConfirmedHopPrompt()
    {
        // The other half: the guard is about the PROMPT, so it must not have
        // simply disabled the reads. Without this, deleting the sends would
        // satisfy the refusal pin above.
        var surface = Surface();
        EnterHop();

        Assert.True(surface.RequestAllNets());
        Assert.True(surface.RequestNet(3));
        Assert.Equal(["DIS", "DIS 3"], Transport.SentLines);
    }

    [Fact]
    public void TheNetReads_RefuseBeforeAnyPromptHasBeenConfirmed()
    {
        // Unconfirmed is not SSB — but it is not HOP either, and a read that
        // cannot be attributed is worse than a read that did not happen.
        var surface = Surface();
        ConnectReady();
        Transport.ClearSent();

        Assert.False(surface.RequestAllNets());
        Assert.False(surface.RequestNet(0));
        Assert.Empty(Transport.SentLines);
    }

    // ---- Round-14 C: the policy wrapper's NO-POLICY composition -------------

    [Fact]
    public void WithNoPolicyInTheComposition_TheWrapperIsExactlyTheRawSelect()
    {
        // The optional constructor parameter is a DECISION, so it gets a pin
        // rather than being left to be discovered. The compositions without a
        // policy are exactly the ones the policy must never reach — the clone
        // campaign's stacks and the bench/ harnesses — and in those the wrapper
        // is the plain select: one `NET n`, no `INTCOUPLER`, whatever type is
        // passed.
        //
        // This surface (Surface()) is built WITHOUT a policy, so the mirror
        // below would otherwise be a converging case: WB target, coupler
        // confirmed ENABLED. CouplerPolicyTests holds the same setup WITH a
        // policy and asserts the BYPASS that this one must not produce.
        var surface = Surface();
        EnterHop();
        Transport.InjectLine("INTCoupler Enabled");
        Transport.ClearSent();

        surface.SelectNetWithCouplerPolicy(3, HopType.Wideband);

        Assert.Equal(["NET 3"], Transport.SentLines);
    }
}
