using System.Text.RegularExpressions;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 14 C — the internal-coupler CONVERGENCE policy
/// (plan/plan-round14.md §4-C, owner rulings R3/R10/R11). Every heading below
/// is one bullet of the plan's R10 GATE list, in the plan's order.
///
/// <para><b>Everything is asserted on the FAKE TRANSPORT'S SEND LOG</b>, in
/// order. The policy's whole contract is what goes on the wire and in what
/// sequence (constitution §3.4: ordered enqueue IS the mechanism — no
/// confirmation gating, no time windows), so a test that asserted on the
/// policy's fields instead of on <c>Transport.SentLines</c> would be testing
/// the bookkeeping rather than the behaviour. The internal state hooks
/// (<c>DesiredIdle</c>, <c>PendingWrite</c>) appear only where the gate asks
/// for the LIFECYCLE of a value that has no other observer.</para>
///
/// <para><b>NOTIFICATION rows: none owed.</b> This phase added no bound
/// property anywhere — the policy is not an <c>ObservableObject</c>, has no
/// <c>[ObservableProperty]</c>, and raises no event; the operator sees the
/// coupler's state on the two settings rows, which render the SAME Core
/// mirror they always did and notify exactly as before. The only view models
/// C touched gained a constructor parameter and one call each, no properties.
/// </para>
/// </summary>
public class CouplerPolicyTests : SessionTestBase
{
    // ---- The stack under test ----------------------------------------------
    // The app's real wiring: ONE policy, shared by the two surfaces, over the
    // real Prc138Radio and RadioSession of SessionTestBase.

    private readonly CouplerPolicy _policy;
    private readonly HopSurface _hop;
    private readonly ModeSurface _mode;

    public CouplerPolicyTests()
    {
        _policy = new CouplerPolicy(Radio, Session);
        _hop = new HopSurface(Radio, _policy);
        _mode = new ModeSurface(Radio, _policy);
    }

    /// <summary>Ready with the connect ritual AND the policy's seeding
    /// <c>INTCOUPLER</c> drained off the send list.</summary>
    private void ReadyDrained() => ConnectReady();

    /// <summary>The radio's own mixed-case answer (docs/protocol.md, P-1
    /// capture) — the parser uppercases it before it reaches the mirror.</summary>
    private void ReportCoupler(BypassEnable state)
        => Transport.InjectLine(state == BypassEnable.Bypass
            ? "INTCoupler Bypassed"
            : "INTCoupler Enabled");

    /// <summary>Verbatim DIS-shaped lines for one net, then the NET report that
    /// makes it CURRENT (the HopViewModelTests idiom).</summary>
    private void ReportNet(int net, string type)
    {
        Transport.InjectLine($"NETID    {net:00}  12345678");
        Transport.InjectLine($"Hoptype {net:00} {type}");
    }

    private void ReportCurrentNet(int net) => Transport.InjectLine($"NET  {net:00}");

    /// <summary>Ready, confirmed HOP, coupler found in <paramref name="found"/>
    /// — which is also what seeds <c>DesiredIdle</c> — send list drained.</summary>
    private void ReadyInHopWithCoupler(BypassEnable found)
    {
        ReadyDrained();
        Transport.InjectLine("HOP>");
        ReportCoupler(found);
        Transport.ClearSent();
    }

    // =========================================================================
    // GATE BULLET 1 — ORDERED ENQUEUE: the coupler word precedes the gesture's
    // main command in every converging case.
    // =========================================================================

    [Fact]
    public void OrderedEnqueue_TheCouplerWord_PrecedesTheNetSelect()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);

        // The ORDER is the assertion — the coupler must be bypassed before the
        // radio regenerates, or the select answers WB_Invalid (docs/protocol.md,
        // the SOLVED section).
        Assert.Equal(["INTCOUPLER BYPASS", "NET 4"], Transport.SentLines);
    }

    [Fact]
    public void OrderedEnqueue_TheCouplerWord_PrecedesTheModeCommand()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);
        ReportNet(4, "WB");
        ReportCurrentNet(4);
        Transport.ClearSent();

        // Leaving HOP: converge to the found-and-therefore-desired ENABLED.
        // The mirror still says ENABLED here, so make it disagree first by
        // going through a WB select — see the mode-switch bullet below for the
        // paired case. This one pins the ORDER on the mode command itself.
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);       // the policy's write confirms
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Ssb);

        Assert.Equal(["INTCOUPLER ENABLE", "SS"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 2 — THE REQUIRED TRUTH TABLE.
    // =========================================================================

    [Theory]
    [InlineData("WB")]
    [InlineData("LIST")]
    public void Required_WideOrListSelect_FromConfirmedEnabled_SendsBypass(string type)
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(
            2, type == "WB" ? HopType.Wideband : HopType.List);

        Assert.Equal(["INTCOUPLER BYPASS", "NET 2"], Transport.SentLines);
    }

    [Fact]
    public void Required_NarrowbandSelect_WhenConfirmedDiffersFromDesiredIdle_SendsTheIdleWord()
    {
        // Found ENABLED, so DesiredIdle = Enable. Then the coupler goes
        // BYPASSED underneath (here: the policy's own WB write confirming), so
        // an NB select must put it back to the operator's idle state.
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(["INTCOUPLER ENABLE", "NET 1"], Transport.SentLines);
    }

    [Fact]
    public void Required_NarrowbandSelect_WhenConfirmedAlreadyEqualsDesiredIdle_SendsNothing()
    {
        // The convergence is a CONVERGENCE, not a re-assertion: already there
        // means nothing goes out.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(["NET 1"], Transport.SentLines);
    }

    [Fact]
    public void Required_AnUnreportedNetType_SendsNothing_TheAppNeverWritesOnAGuess()
    {
        // §3.1. A selectable net can have an unreported type, and "no type
        // reported" is not "narrowband".
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(7, null);

        Assert.Equal(["NET 7"], Transport.SentLines);
    }

    [Fact]
    public void Required_AnUnconfirmedCouplerMirror_SendsNothing_EvenForAWideNet()
    {
        // The other half of §3.1, and the one that matters most: a WB select is
        // the case the policy exists for, and it STILL says nothing while the
        // coupler's state is unknown. (This is also the "no baseline" corner —
        // the first confirmation is what seeds DesiredIdle, so a policy write
        // can never precede the seed.)
        ReadyDrained();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);

        Assert.Equal(["NET 4"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 3 — THE DesiredIdle LIFECYCLE.
    // =========================================================================

    [Fact]
    public void DesiredIdle_IsSeededFromTheFirstConfirmedRead_AndLaterMirrorMovesDoNotMoveIt()
    {
        ReadyDrained();
        Assert.Null(_policy.DesiredIdle);

        ReportCoupler(BypassEnable.Enable);
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);

        // A later mirror change is NOT an operator decision — it is whatever
        // the radio (or this policy) just did. The baseline must not follow it.
        ReportCoupler(BypassEnable.Bypass);
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);
    }

    [Fact]
    public void DesiredIdle_IsNotMovedByThePolicysOwnWrite_NorByItsConfirmation()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);   // policy writes BYPASS
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);

        ReportCoupler(BypassEnable.Bypass);                      // …and it confirms
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);
    }

    [Fact]
    public void DesiredIdle_IsOverwrittenByAnOperatorWriteFromTheSsbRow()
    {
        // The VM call site, not a direct policy call: the gate says "either
        // row", and the row is where the one-liner lives.
        ReadyDrained();
        ReportCoupler(BypassEnable.Enable);
        Transport.InjectLine("SSB>");

        var ssb = new SsbSettingsViewModel(new SsbSurface(Radio), Session, _policy);
        Press(ssb.InternalCouplerChoices, "Bypass");

        Assert.Equal(BypassEnable.Bypass, _policy.DesiredIdle);
    }

    [Fact]
    public void DesiredIdle_IsOverwrittenByAnOperatorWriteFromTheHopRow()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);

        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Press(hopSettings.InternalCouplerChoices, "Bypass");

        Assert.Equal(BypassEnable.Bypass, _policy.DesiredIdle);
    }

    [Fact]
    public void DesiredIdle_ClearsOnASessionDrop_AndNoConvergenceFiresUntilItIsReDerived()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);

        Session.Close();
        Assert.NotEqual(SessionPhase.Ready, Session.Phase);
        Assert.Null(_policy.DesiredIdle);

        // And the silence is real, not just the field: a WB select on the next
        // session says nothing until a confirmed read lands again.
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        Assert.Equal(["NET 4"], Transport.SentLines);

        Transport.ClearSent();
        ReportCoupler(BypassEnable.Enable);                    // re-derived
        _hop.SelectNetWithCouplerPolicy(5, HopType.Wideband);
        Assert.Equal(["INTCOUPLER BYPASS", "NET 5"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 4 — THE OVERRIDE CASE, verbatim from R10: "if the user
    // overrides it, don't set it back".
    // =========================================================================

    [Fact]
    public void TheOverrideCase_AnOperatorBypassBecomesTheBaseline_SoTheModeExitNeverEnablesItBack()
    {
        // R10's sentence, as a sequence: the policy bypasses for a WB net; the
        // operator then presses Bypass EXPLICITLY (they want it bypassed, full
        // stop); leaving HOP must converge toward the OPERATOR's value — which
        // is where the radio already is — so nothing goes out. The latch design
        // this replaced would have re-enabled here, undoing the operator.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);

        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Press(hopSettings.InternalCouplerChoices, "Bypass");
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Ssb);

        Assert.Equal(["SS"], Transport.SentLines);
    }

    [Fact]
    public void TheOverrideCase_AnOperatorEnableBecomesTheBaseline_SoTheModeExitTargetsIt()
    {
        // The mirror image, and the one that proves the baseline really MOVED
        // rather than the policy merely having gone quiet: found BYPASSED (so
        // DesiredIdle starts as Bypass), the operator presses Enable, its echo
        // lands, a WB select bypasses again, and the mode exit converges to the
        // operator's ENABLE — a word the un-overridden policy would never have
        // sent.
        ReadyInHopWithCoupler(BypassEnable.Bypass);

        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Press(hopSettings.InternalCouplerChoices, "Enable");
        ReportCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Ssb);

        Assert.Equal(["INTCOUPLER ENABLE", "SS"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 5 — THE MODE-SWITCH TRIGGERS, both directions.
    // =========================================================================

    [Theory]
    [InlineData("WB")]
    [InlineData("LIST")]
    public void EnteringHop_WithAConfirmedWideOrListCurrentNet_SendsBypassBeforeTheModeCommand(string type)
    {
        // The R9 fact this trigger exists for: mode entry REGENERATES the
        // current net (docs/protocol.md, both P-1 runs), so the coupler has to
        // be right BEFORE `HO` goes out.
        ReadyDrained();
        Transport.InjectLine("HOP>");
        ReportNet(4, type);
        ReportCurrentNet(4);
        ReportCoupler(BypassEnable.Enable);
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Hop);

        Assert.Equal(["INTCOUPLER BYPASS", "HO"], Transport.SentLines);
    }

    [Fact]
    public void EnteringHop_WithANarrowbandCurrentNet_ConvergesToTheBaselineAndSaysNothingWhenItAgrees()
    {
        ReadyDrained();
        Transport.InjectLine("HOP>");
        ReportNet(0, "NB ");
        ReportCurrentNet(0);
        ReportCoupler(BypassEnable.Enable);
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Hop);

        Assert.Equal(["HO"], Transport.SentLines);
    }

    [Fact]
    public void EnteringHop_WithNoConfirmedCurrentNet_SendsNothing()
    {
        // Same §3.1 rule as the unreported-type select: an unknown current net
        // is not a licence to guess.
        ReadyDrained();
        ReportCoupler(BypassEnable.Enable);
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Hop);

        Assert.Equal(["HO"], Transport.SentLines);
    }

    [Fact]
    public void LeavingHop_WithTheConfirmedStateDifferentFromTheBaseline_SendsTheIdleWordFirst()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        _mode.SelectAsOperatorGesture(OperatingMode.Ale);

        Assert.Equal(["INTCOUPLER ENABLE", "ALE"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 6 — THE CLONE PATHS ARE SILENT, BOTH WAYS.
    // =========================================================================

    [Fact]
    public void TheRawSelectNet_SendsNoCouplerWord_WithThePolicyInAFullyConvergingState()
    {
        // Constitution §3.3. The state is deliberately the one that WOULD
        // converge — WB target, coupler confirmed ENABLED — so the silence is
        // structural (the clone campaign typed the other name), not incidental.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNet(4);

        Assert.Equal(["NET 4"], Transport.SentLines);
    }

    [Fact]
    public void ThePlainModeSelect_SendsNoCouplerWord_WithThePolicyInAFullyConvergingState()
    {
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        // The clone campaign's own mode transition (CloneService.cs:1409).
        _mode.Select(OperatingMode.Ssb);

        Assert.Equal(["SS"], Transport.SentLines);
    }

    [Fact]
    public void TheCloneCampaign_NeverNamesEitherGestureWrapper()
    {
        // The behavioural pins above prove the RAW methods are silent; this
        // proves the clone flow still calls them. Without it, moving
        // CloneService onto a gesture wrapper would leave the whole suite
        // green while breaking §3.3 — the exemption is a property of WHICH
        // NAME the campaign types.
        //
        // Read STRIPPED (the house reader, DiRegistrationGuardTests): a
        // commented-out or quoted mention is not a call, and the round-2
        // evasion this project already met — satisfying a text pin from a
        // comment — must not work in either direction.
        var source = Code(Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs"));

        Assert.Equal(0, ReferencesTo("SelectNetWithCouplerPolicy", source));
        Assert.Equal(0, ReferencesTo("SelectAsOperatorGesture", source));

        // Anti-vacuity: the file really is the one that selects nets and modes,
        // so the two absences above are absences of something this file COULD
        // plausibly have. (`SelectNet` is matched as a whole member name, so
        // the policy wrapper's longer name cannot satisfy it.)
        Assert.True(ReferencesTo("SelectNet", source) > 0, "CloneService no longer names the raw SelectNet");
        Assert.True(ReferencesTo("Select", source) > 0, "CloneService no longer names the plain mode Select");
    }

    [Fact]
    public void TheGestureWrappers_HaveExactlyTheOneCallerEach_ThePlanNames()
    {
        // The other half of the exemption: a SECOND caller appearing anywhere
        // in the app layer would widen the R3 doctrine exception silently.
        // §4-C names them — HopViewModel for the select, ModeViewModel for the
        // mode press — and this counts CALL SITES across the whole app layer.
        //
        // A reference is any MEMBER ACCESS (`.Name`), read out of stripped
        // source — NOT call syntax; see ReferencesTo for the method-group
        // evasion that forced that. Declarations in HopSurface.cs /
        // ModeSurface.cs, and the prose about them in CouplerPolicy.cs's
        // summary, do not count, without special-casing any filename.
        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["SelectNetWithCouplerPolicy"] = [],
            ["SelectAsOperatorGesture"] = [],
        };

        foreach (var file in AppLayerSources())
        {
            var code = DiRegistrationGuardTests.StripCommentsAndLiterals(File.ReadAllText(file));
            foreach (var member in callers.Keys)
                if (ReferencesTo(member, code) > 0) callers[member].Add(Path.GetFileName(file));
        }

        Assert.Equal(["HopViewModel.cs"], callers["SelectNetWithCouplerPolicy"]);
        Assert.Equal(["ModeViewModel.cs"], callers["SelectAsOperatorGesture"]);
    }

    [Fact]
    public void TheReferenceScanner_SeesEveryWayToReachTheMember_AndNotADeclarationCommentOrString()
    {
        // Anti-vacuity for BOTH scans above. Every one of their assertions is
        // "this name is / is not reached here", so the reader must be able to
        // MISS — and must not be satisfied by the four things that are not
        // references: a declaration, a line comment, a block comment, a
        // literal.
        //
        // The METHOD GROUP and the delegate conversion are the round-1 audit's
        // own evasion, now first-class rows: the auditor put
        // `((Action<OperatingMode>)_mode.SelectAsOperatorGesture)(wanted)` into
        // CloneService and the whole suite stayed green.
        const string sample = """
            public sealed class Sample
            {
                public void SelectAsOperatorGesture(int mode) { }   // the DECLARATION
                public void Wire(Surface s)
                {
                    s.SelectAsOperatorGesture(1);                          // a plain call
                    ((Action<int>)s.SelectAsOperatorGesture)(2);           // the AUDITOR's evasion
                    Action<int> held = s.SelectAsOperatorGesture;          // a bare method group
                    Run(nameof(Surface.SelectAsOperatorGesture));          // nameof — counted, see ReferencesTo
                    // s.SelectAsOperatorGesture(6);
                    /* s.SelectAsOperatorGesture(7); */
                    Log("s.SelectAsOperatorGesture(8);");
                    Log(@"s.SelectAsOperatorGesture(9);");
                }
            }
            """;

        var code = DiRegistrationGuardTests.StripCommentsAndLiterals(sample);

        Assert.Equal(4, ReferencesTo("SelectAsOperatorGesture", code));
        Assert.Equal(0, ReferencesTo("SelectNetWithCouplerPolicy", code));

        // And the longer name is not a reference to the shorter one, which is
        // what keeps the CloneService anti-vacuity row honest.
        Assert.Equal(0, ReferencesTo("SelectNet", "x.SelectNetWithCouplerPolicy(3, null);"));
    }

    // =========================================================================
    // THE APP'S COMPOSITION — the policy is registered AND actually CONSTRUCTED
    // INTO all four consumers. Nothing else can see this: every behavioural pin
    // in this file builds the stack by hand, so a factory that quietly stopped
    // passing `CouplerPolicy` would leave the whole suite green and the feature
    // DEAD in the shipped app — the optional parameter is exactly what makes
    // that silent.
    //
    // Read as SOURCE, for the reason DiRegistrationGuardTests gives: MauiProgram
    // lives in Falcon.App, which targets only android/windows TFMs, so this
    // host-only net10.0 project cannot reference it and cannot build the real
    // container. ACCEPTED LIMITATION, unchanged from that file: a source scan
    // proves the wiring is WRITTEN, not that resolution succeeds at runtime.
    //
    // THE STRATEGY, and why it changed TWICE. Round 1 required the TOKEN
    // `CouplerPolicy` somewhere in the registration body; the auditor walked
    // through with an always-null factory carrying `nameof(CouplerPolicy)`.
    // Round 2 required a RESOLUTION CALL to appear in the constructor argument
    // text; the auditor walked through that with
    // `true ? (CouplerPolicy?)null : sp.GetRequiredService<CouplerPolicy>()`.
    //
    // Both were BLACKLISTS wearing a whitelist's clothes: they asked what the
    // argument text CONTAINS, and containment is satisfiable by any expression
    // that happens to embed the right substring. Presence-scanning loses this
    // game one shape at a time, so the strategy is now EXACT FORM: the argument
    // list is SPLIT into its argument expressions, and exactly one of them must
    // BE the resolution — `<identifier>.GetRequiredService<CouplerPolicy>()`,
    // anchored end to end, modulo the provider identifier and whitespace.
    //
    // That is not evadable by another expression SHAPE, by construction: every
    // wrapper — a conditional, a cast, a coalesce, a helper call, an extra
    // argument — leaves tokens outside the anchors and fails. What it cannot
    // see is the provider IDENTIFIER being something other than the lambda's
    // parameter (`sp` rebound to a different provider). That is adversarial
    // construction rather than an accident class, and it sits with the
    // preprocessor gap DiRegistrationGuardTests already documents as out of
    // scope for a source scan.
    // =========================================================================

    /// <summary>The four consumers whose constructors must actually RECEIVE the
    /// policy, and the type each registration constructs.</summary>
    private static readonly string[] PolicyConsumers =
        ["ModeSurface", "HopSurface", "SsbSettingsViewModel", "HopSettingsViewModel"];

    [Fact]
    public void MauiProgram_RegistersThePolicy_AndConstructsItIntoAllFourConsumers()
    {
        var source = Code(Path.Combine("src", "Falcon.App", "MauiProgram.cs"));

        // The REGISTRATION half, exact-form like the arguments below (audit
        // round 3, MAJOR). `Assert.Contains` let the auditor through with
        // `if (Environment.ProcessId == int.MinValue) builder.Services
        // .AddSingleton<CouplerPolicy>();` — every consumer argument still
        // perfect, the whole suite green, and the first resolution throwing on
        // the operator's machine. A registration that does not RUN is not a
        // registration, so the statement must sit unconditionally in
        // CreateMauiApp's own body.
        Assert.True(
            RegistersUnconditionally("CouplerPolicy", source, "CreateMauiApp"),
            "MauiProgram does not register CouplerPolicy as an UNCONDITIONAL top-level "
            + "statement of CreateMauiApp — a nested or conditional registration compiles, "
            + "passes every other pin, and throws at the first resolution.");

        foreach (var consumer in PolicyConsumers)
        {
            var arguments = ConstructorArguments(consumer, RegistrationBody(consumer, source));

            Assert.True(
                InjectsExactly("CouplerPolicy", arguments),
                $"{consumer}'s registration does not pass EXACTLY "
                + $"`sp.GetRequiredService<CouplerPolicy>()` as one of its constructor arguments; "
                + $"its argument list reads: {arguments}");
        }

        // Anti-vacuity: the readers really are reading ONE registration's own
        // construction — a peer registration's dependency is not in it.
        Assert.False(InjectsExactly(
            "IConfirmationPrompt", ConstructorArguments("ModeSurface", RegistrationBody("ModeSurface", source))));
    }

    [Fact]
    public void TheCompositionReaders_AcceptOnlyTheExactInjection_AndRejectEveryEvasionSoFar()
    {
        // Anti-vacuity partner, and the standing record of every shape an
        // auditor has used to walk through this pin. Each MUST-FAIL sample is a
        // registration that mentions CouplerPolicy — several of them resolve it
        // — and still hands the consumer null.
        static string Args(string registration)
            => ConstructorArguments("HopSurface", RegistrationBody("HopSurface", registration));
        static string Reg(string policyArgument)
            => "services.AddSingleton<HopSurface>(sp => new HopSurface("
               + "sp.GetRequiredService<Prc138Radio>(), " + policyArgument + "));";

        // The one accepted form, and the same thing spelled with whitespace and
        // a differently-named provider.
        Assert.True(InjectsExactly("CouplerPolicy", Args(Reg("sp.GetRequiredService<CouplerPolicy>()"))));
        Assert.True(InjectsExactly("CouplerPolicy", Args(Reg("provider . GetRequiredService < CouplerPolicy > ( )"))));

        // ROUND-2 AUDIT's evasion: a conditional whose dead branch carries the
        // resolution. Presence-scanning passed this; exact form does not.
        Assert.False(InjectsExactly("CouplerPolicy",
            Args(Reg("true ? (CouplerPolicy?)null : sp.GetRequiredService<CouplerPolicy>()"))));

        // ROUND-1 AUDIT's evasion, and the plain shapes.
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("nameof(CouplerPolicy) is null ? null : null"))));
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("(CouplerPolicy?)null"))));
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("null"))));

        // Wrappers of every other kind: a cast OF the resolution, a coalesce, a
        // helper call. None is the resolution expression itself.
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("(CouplerPolicy)sp.GetRequiredService<CouplerPolicy>()"))));
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("sp.GetRequiredService<CouplerPolicy>() ?? null"))));
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("Wrap(sp.GetRequiredService<CouplerPolicy>())"))));

        // Resolved in the lambda but never CONSTRUCTED IN — the argument-list
        // scope is what catches this one.
        Assert.False(InjectsExactly("CouplerPolicy", Args(
            "services.AddSingleton<HopSurface>(sp => { _ = sp.GetRequiredService<CouplerPolicy>(); "
            + "return new HopSurface(sp.GetRequiredService<Prc138Radio>()); });")));

        // The splitter must not be fooled by a NESTED argument list.
        Assert.False(InjectsExactly("CouplerPolicy", Args(Reg("Wrap(a, sp.GetRequiredService<CouplerPolicy>())"))));

        // ---- the REGISTRATION statement, same treatment -------------------
        // The auditor's round-3 evasion and its neighbours. Only a statement
        // that unconditionally RUNS in the method's own body counts.
        static string Method(string body)
            => "public static class P { public static MauiApp CreateMauiApp() { " + body + " } }";

        Assert.True(RegistersUnconditionally("CouplerPolicy",
            Method("builder.Services.AddSingleton<CouplerPolicy>();"), "CreateMauiApp"));

        // ROUND-3 AUDIT's evasion: guarded by a condition that is never true.
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            Method("if (Environment.ProcessId == int.MinValue) builder.Services.AddSingleton<CouplerPolicy>();"),
            "CreateMauiApp"));
        // The braced form of the same thing, an else, a nested bare block, and
        // a lambda — each is a registration that may not run.
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            Method("if (x) { builder.Services.AddSingleton<CouplerPolicy>(); }"), "CreateMauiApp"));
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            Method("if (x) { } else builder.Services.AddSingleton<CouplerPolicy>();"), "CreateMauiApp"));
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            Method("{ builder.Services.AddSingleton<CouplerPolicy>(); }"), "CreateMauiApp"));
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            Method("Later(() => builder.Services.AddSingleton<CouplerPolicy>());"), "CreateMauiApp"));
        // A registration in some OTHER method is not this method's.
        Assert.False(RegistersUnconditionally("CouplerPolicy",
            "public static class P { public static MauiApp CreateMauiApp() { Nothing(); } "
            + "static void Other() { builder.Services.AddSingleton<CouplerPolicy>(); } }",
            "CreateMauiApp"));
        // And it can MISS a type nobody registers.
        Assert.False(RegistersUnconditionally("PolicyThatDoesNotExist",
            Method("builder.Services.AddSingleton<CouplerPolicy>();"), "CreateMauiApp"));

        // And the body reader still stops at the registration it read.
        const string pair = """
            services.AddSingleton<Alpha>(sp => new Alpha(sp.GetRequiredService<Wanted>()));
            services.AddSingleton<Beta>(sp => new Beta(sp.GetRequiredService<Other>()));
            """;
        Assert.Contains("Wanted", RegistrationBody("Alpha", pair), StringComparison.Ordinal);
        Assert.DoesNotContain("Other", RegistrationBody("Alpha", pair), StringComparison.Ordinal);
        Assert.DoesNotContain("Wanted", RegistrationBody("Beta", pair), StringComparison.Ordinal);
    }

    // =========================================================================
    // THE SESSION-IDENTITY COUNTER'S OWN INVARIANT.
    //
    // `RadioSession.ReadySession` exists only for this policy (audit round 2),
    // and its whole value is that it is a RELIABLE identity. The increment
    // shares `_phase`'s mutex because the two must move together: the poller
    // and transport events arrive on worker threads, and a `SetPhase` that
    // published a new phase with a stale counter — or bumped the counter twice
    // for one entry — would hand this policy a session number that names
    // nothing. Moving `_readySession++` one line down, outside the lock,
    // compiles and passed 1,952 tests (audit round 3, MAJOR).
    //
    // Pinned STRUCTURALLY rather than by a concurrency hammer: a race is
    // probabilistic and a green run would prove nothing, while the placement is
    // a fact about the source that is either true or false. It lives in this
    // file rather than RadioSessionTests because the counter is Phase C's, and
    // so are the stripper and repo readers it uses.
    // =========================================================================

    [Fact]
    public void TheReadySessionIncrement_SitsInsideSetPhasesLock_WhereThePhaseItNamesIsWritten()
    {
        var source = Code(Path.Combine("src", "Falcon.App.Core", "Session", "RadioSession.cs"));

        Assert.True(
            IsInsideLock(source, "SetPhase", "_lock", "_readySession++"),
            "RadioSession.SetPhase increments _readySession outside its lock — the counter and "
            + "the phase it names can then disagree, and CouplerPolicy's session identity "
            + "stops identifying anything.");
    }

    [Fact]
    public void TheLockReader_TellsInsideFromAfter_AndFromAnotherMethodEntirely()
    {
        // Anti-vacuity: the reader must be able to say NO. `after` is the exact
        // round-3 mutation — one line further down, same method, same file.
        const string inside = """
            class S { void SetPhase(int v) { lock (_lock) { _phase = v; _readySession++; } Post(); } }
            """;
        const string after = """
            class S { void SetPhase(int v) { lock (_lock) { _phase = v; } _readySession++; Post(); } }
            """;
        const string elsewhere = """
            class S { void SetPhase(int v) { lock (_lock) { _phase = v; } }
                      void Other() { lock (_lock) { _readySession++; } } }
            """;

        Assert.True(IsInsideLock(inside, "SetPhase", "_lock", "_readySession++"));
        Assert.False(IsInsideLock(after, "SetPhase", "_lock", "_readySession++"));
        Assert.False(IsInsideLock(elsewhere, "SetPhase", "_lock", "_readySession++"));
        // A statement nobody wrote is not inside anything.
        Assert.False(IsInsideLock(inside, "SetPhase", "_lock", "_notAStatement++"));
    }

    // =========================================================================
    // GATE BULLET 7 — THE NOT-RESTORED CORNERS, tested AS NON-EVENTS.
    // Each is an enumerated non-goal under the R10 licence, not a defect: the
    // point of pinning them is that the policy must not grow machinery for
    // them by accident.
    // =========================================================================

    [Fact]
    public void TheOwnerRuling_AnOperatorPressBeforeTheFirstConfirmedMirror_IsGroundTruth()
    {
        // OWNER RULING (round-14 C, audit round 3): an operator's explicit
        // coupler press counts as GROUND TRUTH. It may establish DesiredIdle
        // and — through PendingWrite — an Effective state before any confirmed
        // `INTCOUPLER` answer has arrived, and convergence may fire from it.
        //
        // "The app never writes on a guess" (§3.1) means never from an
        // UNCONFIRMED MIRROR ALONE. An operator's own instruction is not a
        // guess: they said what they want, and the app knows where the radio is
        // headed because it is the app that just sent it there.
        //
        // Nothing has answered `INTCOUPLER` in this session.
        ReadyDrained();
        Transport.InjectLine("HOP>");
        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Transport.ClearSent();
        Assert.Null(_policy.DesiredIdle);

        Press(hopSettings.InternalCouplerChoices, "Enable");
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);

        Assert.Equal(
            ["INTCOUPLER ENABLE", "INTCOUPLER BYPASS", "NET 4"],
            Transport.SentLines);
    }

    [Fact]
    public void Corner_NoBaselineYet_MeansNoIdleConvergenceEver()
    {
        // Nothing has confirmed the coupler, so there is no baseline and no
        // effective state. Neither trigger writes, in either direction.
        ReadyDrained();
        Transport.InjectLine("HOP>");
        ReportNet(4, "WB");
        ReportCurrentNet(4);
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);
        _mode.SelectAsOperatorGesture(OperatingMode.Ssb);

        Assert.Equal(["NET 1", "SS"], Transport.SentLines);
    }

    [Fact]
    public void Corner_ARefusedCouplerWrite_IsNeverRetried()
    {
        // The radio answers the write with nothing the parser recognises as a
        // coupler report (here: the house `** ERROR **` shape). No retry, no
        // watchdog — the console shows the radio's answer and that is the whole
        // response. The next gestures still converge from the PENDING value, so
        // the policy does not stack a second identical word either.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        Transport.InjectLine("** ERROR **");
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(5, HopType.Wideband);
        _hop.SelectNetWithCouplerPolicy(6, HopType.List);

        Assert.Equal(["NET 5", "NET 6"], Transport.SentLines);
    }

    [Fact]
    public void Corner_ASessionDropWhilePolicyBypassed_OwesNoRestore()
    {
        // No restore is owed across a dead session: the close sends the
        // teardown and nothing else, and the reconnect does not "remember" a
        // bypass to undo.
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        Session.Close();

        Assert.DoesNotContain("INTCOUPLER ENABLE", Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 8 — THE QUEUED-GESTURE TRUTH TABLE (scoped-critic F3): two
    // gestures decided BEFORE any coupler echo lands.
    // =========================================================================

    [Fact]
    public void QueuedGestures_WideThenNarrow_BeforeAnyEcho_ReadBypassNetEnableNet()
    {
        // The plan's line, verbatim: `BYPASS, NET w, ENABLE, NET n`. The second
        // gesture decided from PendingWrite — a decision made off the still-
        // standing ENABLED confirmation would have sent nothing and landed the
        // NB net with the coupler bypassed.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(
            ["INTCOUPLER BYPASS", "NET 4", "INTCOUPLER ENABLE", "NET 1"],
            Transport.SentLines);
    }

    [Fact]
    public void QueuedGestures_NarrowThenWide_BeforeAnyEcho_ReadEnableNetBypassNet()
    {
        // The mirror image. Reaching it needs a state where the baseline is
        // ENABLE and the confirmed mirror is BYPASSED with nothing pending —
        // which is exactly where a WB select leaves the radio once its BYPASS
        // has confirmed. From there the NB gesture enables and the WB gesture
        // behind it bypasses again, both before any echo.
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);
        _hop.SelectNetWithCouplerPolicy(5, HopType.Wideband);

        Assert.Equal(
            ["INTCOUPLER ENABLE", "NET 1", "INTCOUPLER BYPASS", "NET 5"],
            Transport.SentLines);
    }

    [Fact]
    public void QueuedGestures_TwoWideSelects_BeforeAnyEcho_SendOneBypassOnly()
    {
        // The third row of the same table, and the one that shows PendingWrite
        // is a state and not a counter: the second WB gesture is ALREADY
        // heading where it needs to be, so it adds no second word.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        _hop.SelectNetWithCouplerPolicy(5, HopType.List);

        Assert.Equal(["INTCOUPLER BYPASS", "NET 4", "NET 5"], Transport.SentLines);
    }

    [Fact]
    public void QueuedGestures_TheEchoOfAnOLDERWord_DoesNotRetireTheOneStillOutstanding()
    {
        // ROUND-1 AUDIT, THE BLOCKER, as a permanent row. Retiring PendingWrite
        // on ANY mirror movement made the mirror speak for a radio that was
        // still travelling: from confirmed ENABLED, a WB select then an NB
        // select put BYPASS, NET 4, ENABLE, NET 1 on the wire; the BYPASS echo
        // then arrived while ENABLE was still outstanding, and a third gesture
        // read "already bypassed", sent nothing, and regenerated a WB net with
        // the coupler about to be ENABLED — WB_Invalid, the exact defect this
        // policy exists to prevent.
        ReadyInHopWithCoupler(BypassEnable.Enable);

        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);      // BYPASS, NET 4
        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);    // ENABLE, NET 1

        // The BYPASS echo lands — answering the OLDEST word, not the last one.
        ReportCoupler(BypassEnable.Bypass);

        _hop.SelectNetWithCouplerPolicy(5, HopType.Wideband);

        // The third gesture must still bypass: the radio is heading to ENABLED.
        Assert.Equal(
            [
                "INTCOUPLER BYPASS", "NET 4",
                "INTCOUPLER ENABLE", "NET 1",
                "INTCOUPLER BYPASS", "NET 5",
            ],
            Transport.SentLines);
    }

    [Fact]
    public void QueuedGestures_ALateConfirmationOfTheOperatorsOwnWord_DoesNotSilenceTheNextGesture()
    {
        // The audit's second trace, and the mirror image of the one above: here
        // the late echo belongs to the OPERATOR's word rather than the policy's.
        // Found BYPASSED; the operator presses Enable (baseline moves); a WB
        // select bypasses on top of it; THEN the operator's own `Enabled`
        // confirmation finally lands. Retiring on it would leave the following
        // NB select reading "already enabled" and sending nothing — landing an
        // NB net with the coupler bypassed, against the operator's own choice.
        ReadyInHopWithCoupler(BypassEnable.Bypass);
        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Transport.ClearSent();

        Press(hopSettings.InternalCouplerChoices, "Enable");       // INTCOUPLER ENABLE
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);      // BYPASS, NET 4

        ReportCoupler(BypassEnable.Enable);                        // the OPERATOR's echo, late

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(
            [
                "INTCOUPLER ENABLE",
                "INTCOUPLER BYPASS", "NET 4",
                "INTCOUPLER ENABLE", "NET 1",
            ],
            Transport.SentLines);
    }

    [Fact]
    public void APendingWordThatTheMirrorAlreadyReports_IsNotRecordedAtAll()
    {
        // The other half of the retirement fix. The coupler rows have NO
        // re-click guard by design, so pressing the LIT button sends a word
        // whose echo carries no value CHANGE — no mirror movement will ever
        // arrive to retire it. Recording it would leave a PendingWrite standing
        // for the rest of the session, ready to speak for a destination the
        // radio is not heading to after some LATER, unrelated change.
        ReadyInHopWithCoupler(BypassEnable.Enable);
        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);

        Press(hopSettings.InternalCouplerChoices, "Enable");        // already ENABLED
        Assert.Null(_policy.PendingWrite);

        // Now the coupler moves under the app from somewhere else entirely and
        // a read confirms it. With a stale PendingWrite standing, the NB select
        // below would think the radio was already heading back to ENABLED and
        // say nothing.
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(["INTCOUPLER ENABLE", "NET 1"], Transport.SentLines);
    }

    [Fact]
    public void QueuedGestures_AnOperatorPress_IsAlsoSeenByTheNextGestureBeforeItsEcho()
    {
        // The operator row reports its write to the policy, so the very next
        // gesture decides from it. Found BYPASSED, operator presses Enable, and
        // an immediate NB select must NOT send a second ENABLE.
        ReadyInHopWithCoupler(BypassEnable.Bypass);

        var hopSettings = new HopSettingsViewModel(
            _hop, Session, new FakeConfirmationPrompt(), _policy);
        Press(hopSettings.InternalCouplerChoices, "Enable");
        Transport.ClearSent();

        _hop.SelectNetWithCouplerPolicy(1, HopType.Narrowband);

        Assert.Equal(["NET 1"], Transport.SentLines);
    }

    // =========================================================================
    // GATE BULLET 9 — THE SEEDING READ.
    // =========================================================================

    [Fact]
    public void TheSeedingRead_GoesOutExactlyOnceOnEnteringReady()
    {
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);

        Assert.Equal(1, Transport.CountSent("INTCOUPLER"));
    }

    [Theory]
    [InlineData("SSB>")]
    [InlineData("HOP>")]
    [InlineData("ALE>")]
    public void TheSeedingRead_IsNotModeGated_TheFamilyIsPromptFree(string prompt)
    {
        // P-1 run C (bench/transcripts/r14-coupler-c-20260820-170918.jsonl):
        // `INTCOUPLER` is answered identically at all three prompts, so there is
        // no mode gate and no deferral machinery to test — the read goes out on
        // Ready and the confirmed mode never enters the decision.
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Transport.ClearSent();
        Transport.InjectLine(prompt);

        // Nothing more goes out when the prompt lands…
        Assert.Equal(0, Transport.CountSent("INTCOUPLER"));

        // …and the ONE that already went out is answerable here, whatever the
        // prompt: the seed lands and the policy is armed.
        ReportCoupler(BypassEnable.Enable);
        Assert.Equal(BypassEnable.Enable, _policy.DesiredIdle);
    }

    [Fact]
    public void TheSeedingRead_DoesNotRepeat_WhileTheSessionStaysReady()
    {
        ReadyDrained();
        ReportCoupler(BypassEnable.Enable);
        Transport.InjectLine("HOP>");
        Transport.InjectLine("SSB>");

        Assert.Equal(0, Transport.CountSent("INTCOUPLER"));
    }

    [Fact]
    public void TheSeedingRead_ReArmsForANewSession()
    {
        ReadyDrained();                       // seed 1 (drained by ConnectReady)
        Session.Close();
        Transport.ClearSent();

        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();

        Assert.Equal(1, Transport.CountSent("INTCOUPLER"));
    }

    [Fact]
    public void TheSeedingRead_DoesNotRepeat_WhenAQueuedReadyNotificationLandsAfterTheConstructorSeeded()
    {
        // ROUND-1 AUDIT (MAJOR): the `_seedRequested` flag was reported as a
        // race guard no test could stage. It can be staged, and this is the
        // auditor's recipe — with the flag removed this reads 2, not 1.
        //
        // The race is real in the app: RadioSession sets its phase FIELD
        // synchronously but POSTS the notification to the captured context, and
        // DI resolves singletons lazily. So the policy can be constructed in
        // the window after Ready and before the notification is delivered — it
        // seeds from the constructor, and then OnPhaseChanged (which reads the
        // session's CURRENT phase, not an event payload) finds Ready and would
        // seed again.
        var context = new QueueingContext();
        var transport = new InjectingTransport();
        var radio = new Prc138Radio(transport, new InlineContext());
        using var session = new RadioSession(radio, transport, context)
        {
            ReconnectIntervalMs = 3_600_000,
        };

        session.Connect(TestSettings);
        transport.InjectLine("Battery Status FULL 31.4V");
        transport.InjectLine("Battery Status FULL 31.4V");

        // Ready by the field; the notification is still sitting in the queue.
        Assert.Equal(SessionPhase.Ready, session.Phase);
        transport.ClearSent();

        _ = new CouplerPolicy(radio, session);
        Assert.Equal(1, transport.CountSent("INTCOUPLER"));         // the constructor seeded

        // Anti-vacuity: the staging really did defer a notification, so the
        // assertion below is about delivery ORDER and not about nothing.
        Assert.True(context.Drain() > 0, "no notification was queued — this test stages nothing");

        Assert.Equal(1, transport.CountSent("INTCOUPLER"));
    }

    [Fact]
    public void ACoalescedDropAndReconnect_DiscardsTheDeadSessionsStateEvenThoughNoCallbackEverSawTheDrop()
    {
        // ROUND-2 AUDIT, THE BLOCKER. The round-1 seeding race has a mirror
        // image on the CLEAR side: if a drop AND its repair both complete
        // before the context drains, every queued callback observes Ready and
        // OnPhaseChanged's "is the phase Ready?" test never fires the clear.
        // The dead session's baseline and — fatally — its in-flight PendingWrite
        // then survive into the NEW session and suppress the write a WB select
        // needs, reproducing WB_Invalid on a fresh, healthy link.
        var rig = new QueuedSessionRig();

        // Session 1: found ENABLED, and a WB select leaves BYPASS in flight.
        rig.ConnectToReady();
        rig.Drain();
        rig.ReportCoupler(BypassEnable.Enable);
        rig.Hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        Assert.Equal(BypassEnable.Bypass, rig.Policy.PendingWrite);

        // The link dies and comes back — BOTH before anything drains, so every
        // callback below sees Ready and none of them looks like a drop.
        rig.DropAndReconnectWithoutDraining();
        rig.Drain();

        // Session 2's radio is ENABLED too.
        rig.Transport.ClearSent();
        rig.ReportCoupler(BypassEnable.Enable);

        rig.Hop.SelectNetWithCouplerPolicy(5, HopType.Wideband);

        Assert.Equal(["INTCOUPLER BYPASS", "NET 5"], rig.Transport.SentLines);
    }

    [Fact]
    public void ACoalescedDropAndReconnect_ReArmsTheSeedingRead_BecauseItIsANewSession()
    {
        // The other half of the same fix: a new Ready session owes a new seed
        // (the plan's rule), and the coalesced boundary must not swallow it —
        // otherwise the policy re-arms its silence instead of its baseline.
        var rig = new QueuedSessionRig();

        rig.ConnectToReady();
        rig.Drain();
        rig.ReportCoupler(BypassEnable.Enable);

        rig.DropAndReconnectWithoutDraining();
        rig.Transport.ClearSent();

        rig.Drain();

        Assert.Equal(1, rig.Transport.CountSent("INTCOUPLER"));

        // And the baseline really was discarded rather than carried over: it is
        // re-derived from session 2's own answer.
        Assert.Null(rig.Policy.DesiredIdle);
        rig.ReportCoupler(BypassEnable.Bypass);
        Assert.Equal(BypassEnable.Bypass, rig.Policy.DesiredIdle);
    }

    [Fact]
    public void TheSeedingRead_AlsoGoesOutWhenThePolicyIsBuiltAfterReady()
    {
        // DI singletons resolve lazily. A policy first constructed after the
        // session reached Ready must still seed — otherwise the app's very
        // first session would be silent, which is the one case an operator
        // would never think to look for.
        ReadyDrained();
        Transport.ClearSent();

        _ = new CouplerPolicy(Radio, Session);

        Assert.Equal(1, Transport.CountSent("INTCOUPLER"));
    }

    // =========================================================================
    // The VM seams: the two gesture wrappers really are what the operator's
    // presses reach.
    // =========================================================================

    [Fact]
    public void TheOperateSelectPress_CarriesThePickedNetsReportedTypeIntoThePolicy()
    {
        // End to end from the command the button binds to, so the "reported
        // type" plumbing is pinned at its real source (the mirror projection)
        // rather than only at the surface's parameter.
        var vm = new HopViewModel(_hop, Session, new TestTime());
        ReadyInHopWithCoupler(BypassEnable.Enable);
        ReportNet(4, "WB");
        ReportCurrentNet(0);

        for (int i = 0; i < 10 && vm.PickedNet != 4; i++) vm.NetUpCommand.Execute(null);
        Assert.Equal(4, vm.PickedNet);
        Assert.True(vm.CanSelectPickedNet);

        // Drained HERE, not before the picker moves: walking the picker over
        // unreported nets issues their `DIS n` reads (the round-11 picker
        // idiom), which are not this test's subject.
        Transport.ClearSent();

        vm.SelectPickedNetCommand.Execute(null);

        Assert.Equal(["INTCOUPLER BYPASS", "NET 4"], Transport.SentLines);
    }

    [Fact]
    public void TheModePress_RoutesThroughTheGestureWrapper()
    {
        var vm = new ModeViewModel(_mode, Session);
        ReadyInHopWithCoupler(BypassEnable.Enable);
        _hop.SelectNetWithCouplerPolicy(4, HopType.Wideband);
        ReportCoupler(BypassEnable.Bypass);
        Transport.ClearSent();

        vm.SelectModeCommand.Execute("Ssb");

        Assert.Equal(["INTCOUPLER ENABLE", "SS"], Transport.SentLines);
    }

    // ---- Helpers -------------------------------------------------------------

    /// <summary>Press one of a settings row's provisional choice buttons by its
    /// label — the operator's actual gesture, through the bound command.</summary>
    private static void Press(IReadOnlyList<ChoiceItem> choices, string label)
        => choices.Single(c => c.Value == label).SelectCommand.Execute(null);

    /// <summary>A whole policy stack whose SESSION notifications are queued
    /// rather than run at the post — the app's marshalled delivery, under the
    /// test's control. The radio keeps the inline context, so mirror lines
    /// still land synchronously and only the phase notifications are deferred:
    /// that is exactly the asymmetry the two coalescing races live in.</summary>
    private sealed class QueuedSessionRig
    {
        private readonly QueueingContext _context = new();

        public readonly InjectingTransport Transport = new();
        public readonly Prc138Radio Radio;
        public readonly RadioSession Session;
        public readonly CouplerPolicy Policy;
        public readonly HopSurface Hop;

        public QueuedSessionRig()
        {
            Radio = new Prc138Radio(Transport, new InlineContext());
            Session = new RadioSession(Radio, Transport, _context)
            {
                ReconnectIntervalMs = 3_600_000,
            };
            Policy = new CouplerPolicy(Radio, Session);
            Hop = new HopSurface(Radio, Policy);
        }

        public void ConnectToReady()
        {
            Session.Connect(TestSettings);
            Transport.InjectLine("Battery Status FULL 31.4V");
            Transport.InjectLine("Battery Status FULL 31.4V");
            Assert.Equal(SessionPhase.Ready, Session.Phase);
        }

        /// <summary>The link dies and comes back with NOTHING drained in
        /// between, so every notification the boundary produced is delivered
        /// later, when the phase already reads Ready again.</summary>
        public void DropAndReconnectWithoutDraining()
        {
            Session.Close();
            Assert.NotEqual(SessionPhase.Ready, Session.Phase);
            ConnectToReady();
        }

        public int Drain() => _context.Drain();

        public void ReportCoupler(BypassEnable state)
            => Transport.InjectLine(state == BypassEnable.Bypass
                ? "INTCoupler Bypassed"
                : "INTCoupler Enabled");
    }

    /// <summary>A <see cref="SynchronizationContext"/> that QUEUES posted
    /// callbacks until a test drains them — the app's marshalled delivery, made
    /// observable. <c>InlineContext</c> (the house default) runs them at the
    /// post, which is what hides the ordering this file needs to stage.</summary>
    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue((d, state));

        /// <summary>Run everything queued, including anything the callbacks
        /// queue in turn. Returns how many ran.</summary>
        public int Drain()
        {
            int ran = 0;
            while (_posted.Count > 0)
            {
                var (callback, state) = _posted.Dequeue();
                callback(state);
                ran++;
            }
            return ran;
        }
    }

    // ---- The source readers --------------------------------------------------
    // Structural, never raw text: everything below reads source that has had
    // comments, string literals and char literals removed by the HOUSE stripper
    // (DiRegistrationGuardTests.StripCommentsAndLiterals), so a commented-out or
    // quoted mention can neither satisfy a pin nor break one. Each reader has an
    // anti-vacuity self-pin above.

    /// <summary>How many times <paramref name="member"/> is REFERENCED — any
    /// member access, `.Name`, whether or not a `(` follows — in
    /// already-stripped source.
    ///
    /// <para><b>Accesses, not call syntax</b> (round-1 audit, MAJOR). The first
    /// version matched `.Name(` only, and the auditor walked straight through
    /// it with `((Action&lt;OperatingMode&gt;)_mode.SelectAsOperatorGesture)(wanted)`
    /// — a method group converted to a delegate and invoked through the
    /// variable. That compiles, ships, and lets the clone campaign emit
    /// `INTCOUPLER`, with every test green. Anything that can NAME the member
    /// can reach it, so naming it is what counts.</para>
    ///
    /// <para><b>`nameof(Type.Member)` IS counted</b>, deliberately. It is not a
    /// call, but it is a coupling to the member, and the two failure directions
    /// are not symmetric: counting it can only produce a loud failure a reader
    /// resolves in a minute, while excluding it re-opens a hiding place. No
    /// production file needs one today.</para>
    ///
    /// <para>A DECLARATION (`public void Name(`) has no leading dot and does
    /// not count, which is what lets the declaring surfaces be scanned like
    /// every other file. The trailing `\b` keeps a longer name from matching a
    /// shorter one — `.SelectNetWithCouplerPolicy` is not a `SelectNet`
    /// reference.</para></summary>
    private static int ReferencesTo(string member, string strippedSource)
        => Regex.Matches(strippedSource, @"\.\s*" + Regex.Escape(member) + @"\b").Count;

    /// <summary>The argument list of ONE `AddSingleton&lt;Type&gt;(…)`
    /// registration, read by matching parentheses from the call's own open
    /// paren — so a peer registration's dependencies are never in the answer.
    /// Empty when the type is not registered at all.</summary>
    internal static string RegistrationBody(string type, string strippedSource)
    {
        var head = Regex.Match(
            strippedSource,
            @"Add(?:Singleton|Transient|Scoped)<\s*" + Regex.Escape(type) + @"\s*>\s*\(");
        if (!head.Success) return string.Empty;

        int start = head.Index + head.Length;   // just inside the open paren
        int i = start;
        int depth = 1;
        while (i < strippedSource.Length)
        {
            if (strippedSource[i] == '(') depth++;
            else if (strippedSource[i] == ')' && --depth == 0) return strippedSource[start..i];
            i++;
        }
        return strippedSource[start..];         // unbalanced source — report all of it
    }

    /// <summary>The argument list of the `new <paramref name="type"/>( … )` the
    /// registration body CONSTRUCTS, by matching parentheses from that call's
    /// own open paren. Empty when the body does not construct the type at all
    /// (the reflection-activated `AddSingleton&lt;T&gt;()` form has no body).
    ///
    /// <para>This scope is the point: a dependency the lambda resolves and then
    /// drops on the floor is not an injection, and only the argument list can
    /// tell the difference.</para></summary>
    internal static string ConstructorArguments(string type, string registrationBody)
    {
        var head = Regex.Match(registrationBody, @"\bnew\s+" + Regex.Escape(type) + @"\s*\(");
        if (!head.Success) return string.Empty;

        int start = head.Index + head.Length;
        int i = start;
        int depth = 1;
        while (i < registrationBody.Length)
        {
            if (registrationBody[i] == '(') depth++;
            else if (registrationBody[i] == ')' && --depth == 0) return registrationBody[start..i];
            i++;
        }
        return registrationBody[start..];
    }

    /// <summary>Whether one of the constructor's ARGUMENT EXPRESSIONS IS the
    /// container resolution for <paramref name="type"/> — the whole
    /// expression, anchored end to end, not a text that merely contains one.
    ///
    /// <para>WHITELIST, deliberately (round-2 audit). "Does the argument text
    /// mention a resolution?" is satisfied by
    /// `true ? (CouplerPolicy?)null : sp.GetRequiredService&lt;CouplerPolicy&gt;()`
    /// and by every future wrapper nobody has thought of yet. "Is this argument
    /// exactly `&lt;identifier&gt;.GetRequiredService&lt;T&gt;()`?" is satisfied
    /// by nothing else, because any wrapper leaves tokens outside the
    /// anchors.</para>
    ///
    /// <para>Both house spellings are accepted (`GetService` /
    /// `GetRequiredService`) and whitespace is free; the provider identifier is
    /// free too, and is the one thing this cannot check — see the strategy note
    /// on the pin.</para></summary>
    internal static bool InjectsExactly(string type, string constructorArguments)
        => SplitArguments(constructorArguments).Any(argument => Regex.IsMatch(
            argument,
            @"^[A-Za-z_][A-Za-z0-9_]*\s*\.\s*Get(?:Required)?Service\s*<\s*"
                + Regex.Escape(type) + @"\s*>\s*\(\s*\)$"));

    /// <summary>Whether <paramref name="type"/> is registered by a statement
    /// that UNCONDITIONALLY RUNS in <paramref name="method"/>'s own body.
    ///
    /// <para>Two things at once, because a registration that does not run is
    /// not a registration (audit round 3). The statement must sit at the
    /// method body's own brace depth — so an <c>if (…) { … }</c>, a nested
    /// block, a local function, or another method does not count — AND it must
    /// START a statement, which is what rejects the brace-less
    /// <c>if (…) builder.Services.AddSingleton&lt;T&gt;();</c> that lives at
    /// the very same depth. The preceding non-space character of a real
    /// statement is <c>;</c>, <c>{</c> or <c>}</c>; after an <c>if (…)</c> it
    /// is <c>)</c>, and after an <c>else</c> or a <c>=&gt;</c> it is a letter
    /// or <c>&gt;</c>.</para></summary>
    private static bool RegistersUnconditionally(string type, string strippedSource, string method)
    {
        var (bodyOpen, bodyClose) = MethodBody(strippedSource, method);
        if (bodyOpen < 0 || bodyClose < 0) return false;

        var depth = BraceDepths(strippedSource);
        int bodyDepth = depth[bodyOpen] + 1;

        var statement = new Regex(
            @"[A-Za-z_][A-Za-z0-9_]*\s*\.\s*Services\s*\.\s*AddSingleton\s*<\s*"
            + Regex.Escape(type) + @"\s*>\s*\(\s*\)\s*;");

        foreach (Match m in statement.Matches(strippedSource))
        {
            // Inside THIS method's braces, at its own statement depth. The
            // range check is what stops a sibling method's body — same depth,
            // different method — from answering for this one.
            if (m.Index <= bodyOpen || m.Index >= bodyClose) continue;
            if (depth[m.Index] != bodyDepth) continue;

            int before = m.Index - 1;
            while (before >= 0 && char.IsWhiteSpace(strippedSource[before])) before--;
            if (before < 0) continue;
            if (strippedSource[before] is ';' or '{' or '}') return true;
        }
        return false;
    }

    /// <summary>The brace range of a method's own body: the index of its
    /// opening <c>{</c> and of the <c>}</c> that closes it, or (-1, -1).
    ///
    /// <para>A DECLARATION, not a call: the parameter list is followed by
    /// <c>{</c>, where a call is followed by <c>;</c> or an operator. Reading
    /// this wrong is not hypothetical — the first literal "SetPhase" in
    /// RadioSession.cs is a call site nine methods above the
    /// declaration.</para></summary>
    internal static (int Open, int Close) MethodBody(string strippedSource, string method)
    {
        var declaration = Regex.Match(
            strippedSource,
            @"(?<![A-Za-z0-9_])" + Regex.Escape(method) + @"\s*\([^()]*\)\s*\{");
        if (!declaration.Success) return (-1, -1);

        int open = declaration.Index + declaration.Length - 1;
        var depth = BraceDepths(strippedSource);
        int inside = depth[open] + 1;

        for (int i = open + 1; i < strippedSource.Length; i++)
            if (strippedSource[i] == '}' && depth[i] < inside) return (open, i);
        return (open, -1);
    }

    /// <summary>Whether <paramref name="statement"/> sits inside the
    /// <c>lock (<paramref name="lockObject"/>)</c> block of
    /// <paramref name="method"/> — read structurally, by matching the lock
    /// block's own braces, so "after the lock" and "in a different method"
    /// both answer false.</summary>
    private static bool IsInsideLock(
        string strippedSource, string method, string lockObject, string statement)
    {
        var (bodyOpen, bodyClose) = MethodBody(strippedSource, method);
        if (bodyOpen < 0 || bodyClose < 0) return false;

        var head = new Regex(
                @"(?<![A-Za-z0-9_])lock\s*\(\s*" + Regex.Escape(lockObject) + @"\s*\)\s*\{")
            .Match(strippedSource, bodyOpen);
        if (!head.Success || head.Index >= bodyClose) return false;   // this method's lock, or none

        int open = head.Index + head.Length - 1;      // the lock block's `{`
        var depth = BraceDepths(strippedSource);
        int inside = depth[open] + 1;

        int close = open + 1;
        while (close < strippedSource.Length && !(strippedSource[close] == '}' && depth[close] < inside))
            close++;
        if (close >= strippedSource.Length) return false;

        int at = strippedSource.IndexOf(statement, open, StringComparison.Ordinal);
        return at > open && at < close;
    }

    /// <summary>For each index, how many <c>{</c> are still unclosed before it —
    /// so every statement directly inside a block shares that block's
    /// number.</summary>
    internal static int[] BraceDepths(string source)
    {
        var depths = new int[source.Length];
        int depth = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '}') depth--;
            depths[i] = depth;
            if (source[i] == '{') depth++;
        }
        return depths;
    }

    /// <summary>An argument list split into its top-level argument
    /// expressions — commas nested inside parentheses, brackets or braces
    /// belong to an inner call and do not split. (Generic type arguments carry
    /// no commas at any of these sites; a two-parameter generic would need this
    /// to track `&lt;&gt;` as well, which is not decidable lexically and is not
    /// needed here.)</summary>
    private static IEnumerable<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(arguments[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(arguments[start..].Trim());
        return parts.Where(p => p.Length > 0);
    }

    /// <summary>Every hand-written C# file in the app layer (`src/`), obj/bin
    /// generated output excluded.</summary>
    private static IEnumerable<string> AppLayerSources()
    {
        var root = Path.Combine(FindRepoRoot(), "src");
        Assert.True(Directory.Exists(root), "src directory missing: " + root);

        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(files.Count > 0, "no app-layer sources found — the sweep has drifted");
        return files;
    }

    private static string Code(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), "source missing: " + relativePath);
        return DiRegistrationGuardTests.StripCommentsAndLiterals(File.ReadAllText(path));
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
