using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// THE CAMPAIGN COORDINATOR (plan-clone-write-structural.md §5.2) — the lease
/// itself, before any producer consumes it.
///
/// <para><b>Why a scoped lease and not a flag.</b>
/// <c>CloneService.State</c> reaches <c>Failed</c> BEFORE the closing restore
/// runs, and an exception or an early return leaves a plain flag stuck true
/// forever. Every pin here is about the property that replaces both: the lease
/// is released by the language on every exit path.</para>
/// </summary>
public sealed class CampaignWireCoordinatorTests
{
    [Fact]
    public void ABareCoordinator_IsNotActive_AndHasRaisedNothing()
    {
        var wire = new CampaignWireCoordinator();
        int raised = 0;
        wire.Changed += (_, _) => raised++;

        Assert.False(wire.CampaignActive);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ALease_MakesItActive_AndDisposingReleasesIt_OneEdgeEach()
    {
        var wire = new CampaignWireCoordinator();
        var edges = new List<bool>();
        wire.Changed += (_, _) => edges.Add(wire.CampaignActive);

        var lease = wire.Enter();
        Assert.True(wire.CampaignActive);
        lease.Dispose();
        Assert.False(wire.CampaignActive);

        // The property has ALREADY moved when the event arrives, both ways —
        // a handler that reads CampaignActive sees the new value.
        Assert.Equal([true, false], edges);
    }

    [Fact]
    public void ANestedLease_StaysActiveUntilTheOutermostExits_AndIsSilentInBetween()
    {
        var wire = new CampaignWireCoordinator();
        int raised = 0;
        wire.Changed += (_, _) => raised++;

        var outer = wire.Enter();
        var inner = wire.Enter();
        Assert.Equal(1, raised);              // the nested Enter is silent

        inner.Dispose();
        Assert.True(wire.CampaignActive);     // …and so is its release
        Assert.Equal(1, raised);

        outer.Dispose();
        Assert.False(wire.CampaignActive);
        Assert.Equal(2, raised);              // one start edge, one end edge
    }

    [Fact]
    public void ALeaseDisposedTwice_ReleasesOnce()
    {
        var wire = new CampaignWireCoordinator();
        var outer = wire.Enter();
        var inner = wire.Enter();

        inner.Dispose();
        inner.Dispose();                      // `using` plus an explicit dispose

        Assert.True(wire.CampaignActive);     // the outer lease still stands
        outer.Dispose();
        Assert.False(wire.CampaignActive);
    }

    [Fact]
    public void AnExceptionInsideTheUsingBlock_StillReleasesTheLease()
    {
        var wire = new CampaignWireCoordinator();

        static void Blow(CampaignWireCoordinator wire)
        {
            using (wire.Enter())
            {
                Assert.True(wire.CampaignActive);
                throw new InvalidOperationException("the campaign blew up");
            }
        }

        Assert.Throws<InvalidOperationException>(() => Blow(wire));

        Assert.False(wire.CampaignActive);
    }
}

/// <summary>
/// THE QUIESCE MANIFEST (plan-clone-write-structural.md D1, §4) — one pin per
/// producer, closed both ways.
///
/// <para><b>The field failure this closes.</b> 2026-08-28, first Android field
/// clone: the zeroize-first write landed every domain EXCEPT the ALE book, all
/// 32 book operations faulting with "the radio is behind on its sentinel
/// answers". Fourteen App.Core producers were campaign-blind and fired on
/// exactly the events a campaign lap generates — the worst being the ALE pane's
/// first-confirmation burst, which lands as the book leg opens its gate
/// brackets.</para>
///
/// <para><b>The shape of every pin</b> (§6 "Quiesce"). Two identical rigs on a
/// transport fake. The CONTROL runs the producer's autonomous trigger with no
/// campaign at all and records what it puts on the wire. The QUIESCED rig runs
/// the SAME trigger inside a campaign lease and must send NOTHING; when the
/// lease is released it must send EXACTLY what the control sent — same lines,
/// same order, once. That equality is what makes "the read was deferred, not
/// lost" and "exactly one owed read" one assertion instead of two guesses at a
/// wire spelling.</para>
///
/// <para>The <c>Assert.NotEmpty</c> on the control is the anti-vacuity check: a
/// trigger that stopped reading would make every quiesce pin pass for the wrong
/// reason.</para>
///
/// <para>D7/I-1: no demo-radio development. Every rig here is
/// <c>InjectingTransport</c> — a byte-level fake — and the radio's answers are
/// injected lines.</para>
/// </summary>
public sealed class CampaignQuiesceTests
{
    /// <summary>One app-side stack on a transport fake, with its own campaign
    /// signal. Deliberately NOT <c>SessionTestBase</c>: every pin needs TWO
    /// independent rigs in one test.</summary>
    private sealed class Rig : IDisposable
    {
        public InjectingTransport Transport { get; } = new();
        public Prc138Radio Radio { get; }
        public RadioSession Session { get; }
        public CampaignWireCoordinator Wire { get; } = new();

        public Rig()
        {
            var context = new InlineContext();
            Radio = new Prc138Radio(Transport, context);
            Session = new RadioSession(Radio, Transport, context)
            {
                ReconnectIntervalMs = 3_600_000,
            };
        }

        public void ConnectReady()
        {
            Session.Connect(new PortSettings { PortName = "COM7", BaudRate = 9600 });
            Transport.InjectLine("Battery Status FULL 31.4V");
            Transport.InjectLine("Battery Status FULL 31.4V");
            Assert.Equal(SessionPhase.Ready, Session.Phase);
            Transport.ClearSent();
        }

        /// <summary>The radio announcing a prompt — what confirms a mode, and
        /// the event most of the §4 producers land on.</summary>
        public void Prompt(string prompt) => Transport.InjectLine(prompt);

        public IReadOnlyList<string> Sent => Transport.SentLines;

        public void Dispose()
        {
            Session.Dispose();
            Radio.Dispose();
        }
    }

    /// <summary>
    /// THE MANIFEST PIN, run per producer row.
    ///
    /// <para><paramref name="build"/> constructs the producer against a rig
    /// that is ALREADY Ready — with the campaign signal, or without it for the
    /// control. In the quiesced rig it is constructed INSIDE the lease, which
    /// is also the lazily-resolved-during-a-campaign case §5.2 names for
    /// <c>CouplerPolicy</c>.</para>
    ///
    /// <para><paramref name="trigger"/> is the producer's own autonomous read
    /// path — a mode prompt, a landing, a tab open. Null when construction
    /// alone is the trigger.</para>
    /// </summary>
    private static void PinProducer(
        Func<Rig, ICampaignSignal?, object> build,
        Action<Rig, object>? trigger = null)
    {
        // ---- CONTROL: no campaign, so this is the producer's normal wire ----
        using var control = new Rig();
        control.ConnectReady();
        var controlProducer = build(control, null);
        trigger?.Invoke(control, controlProducer);
        var expected = control.Sent.ToList();
        Assert.True(expected.Count > 0,
            "the control trigger read NOTHING, so this quiesce pin would pass vacuously");

        // ---- QUIESCED: the same trigger, inside a campaign -------------------
        using var rig = new Rig();
        rig.ConnectReady();
        var lease = rig.Wire.Enter();
        var producer = build(rig, rig.Wire);
        trigger?.Invoke(rig, producer);

        Assert.True(rig.Sent.Count == 0,
            "the producer talked over the campaign: " + string.Join(" | ", rig.Sent));

        // ---- THE CAMPAIGN LETS GO: the owed read, paid ONCE -----------------
        lease.Dispose();
        Assert.Equal(expected, rig.Sent);
    }

    /// <summary>
    /// THE REFRESH-PRESS PIN (§4 SUPPRESSION SCOPE, decided): an explicit
    /// Refresh press is ACCEPTED while a campaign runs — the button does not
    /// grey and the operator is not refused — and the read runs at campaign
    /// end.
    ///
    /// <para><paramref name="settle"/> gets the producer past its lazy first
    /// load OUTSIDE the campaign, so what the press produces is measured on its
    /// own.</para>
    /// </summary>
    private static void PinRefreshPress(
        Func<Rig, ICampaignSignal?, object> build,
        Action<Rig, object> settle,
        Action<object> press)
    {
        using var control = new Rig();
        control.ConnectReady();
        var controlProducer = build(control, null);
        settle(control, controlProducer);
        control.Transport.ClearSent();
        press(controlProducer);
        var expected = control.Sent.ToList();
        Assert.True(expected.Count > 0,
            "the control press read NOTHING, so this pin would pass vacuously");

        using var rig = new Rig();
        rig.ConnectReady();
        var producer = build(rig, rig.Wire);
        settle(rig, producer);
        rig.Transport.ClearSent();

        var lease = rig.Wire.Enter();
        press(producer);
        Assert.True(rig.Sent.Count == 0,
            "the press went to the wire during a campaign: " + string.Join(" | ", rig.Sent));

        lease.Dispose();
        Assert.Equal(expected, rig.Sent);
    }

    // ===================== THE 14 MANIFEST ROWS =============================

    /// <summary>ROW 1 — <c>ModemPresetsViewModel</c>. The scope-edge landing
    /// (<c>MODEM PRE n</c> + the presence operation). This is the row that
    /// needed a DEDICATED deferred-landing latch: its edge branch COMMITS the
    /// scope and clears the owed flag BEFORE reading, so re-running edge
    /// detection at campaign end could never pay the read.</summary>
    [Fact]
    public void Row01_ModemPresets_DefersItsLanding_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new ModemPresetsViewModel(new ModemSurface(rig.Radio), rig.Session, sig),
            (rig, vm) => { rig.Prompt("SSB>"); ((ModemPresetsViewModel)vm).EnsureLoaded(); });

    /// <summary>
    /// ROW 1, THE SCOPE-EDGE PATH — the case the dedicated latch exists for
    /// (§4 per-producer correction, critic pass 2).
    ///
    /// <para>The card is constructed BEFORE the connect, as DI does it, so the
    /// Ready arrival OWES it a landing and the mode confirmation pays it. That
    /// branch COMMITS <c>_landedHopScope</c> and clears
    /// <c>_landingOwedOnModeConfirm</c> BEFORE calling the read — so the edge is
    /// already absorbed by the time the campaign check intercepts, and
    /// re-running edge detection at campaign end would find NO edge and pay
    /// NOTHING. Only a latch recording the DEFERRAL itself can close it, and
    /// that is what this pin proves.</para>
    /// </summary>
    [Fact]
    public void Row01b_ModemPresets_ScopeEdgeLanding_SurvivesTheCampaign_ThoughTheEdgeIsAlreadyAbsorbed()
    {
        using var control = new Rig();
        var controlVm = new ModemPresetsViewModel(
            new ModemSurface(control.Radio), control.Session, null);
        control.ConnectReady();
        control.Prompt("SSB>");
        var expected = control.Sent.ToList();
        Assert.True(expected.Count > 0, "the scope-edge landing read nothing — vacuous");

        using var rig = new Rig();
        var vm = new ModemPresetsViewModel(new ModemSurface(rig.Radio), rig.Session, rig.Wire);
        var lease = rig.Wire.Enter();
        rig.ConnectReady();
        rig.Prompt("SSB>");
        Assert.True(rig.Sent.Count == 0,
            "the modem card talked over the campaign: " + string.Join(" | ", rig.Sent));

        lease.Dispose();
        Assert.Equal(expected, rig.Sent);
        _ = (controlVm, vm);
    }

    /// <summary>ROW 2 — <c>ModemViewModel</c>: the operate wheel's presence
    /// landing (bare <c>MODEM PRE</c> + sentinel).</summary>
    [Fact]
    public void Row02_Modem_DefersItsPresenceLanding_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new ModemViewModel(new ModemSurface(rig.Radio), rig.Session, sig),
            (rig, _) => rig.Prompt("SSB>"));

    /// <summary>ROW 3 — <c>HopViewModel</c>: the pane's landing pair
    /// (<c>DIS n</c> + <c>SH</c>). Its generation observer defers by NOT
    /// ABSORBING, so the same Refresh at campaign end owes the same reads.</summary>
    [Fact]
    public void Row03_Hop_DefersItsLandingPair_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new HopViewModel(
                new HopSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            (rig, _) => rig.Prompt("HOP>"));

    /// <summary>ROW 4 — <c>HopSettingsViewModel</c>: the sight-edge landing
    /// (<c>DIS n</c>, <c>INTCOUPLER</c>, <c>EXC</c>).</summary>
    [Fact]
    public void Row04_HopSettings_DefersItsSightRead_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new HopSettingsViewModel(
                new HopSurface(rig.Radio), rig.Session, new FakeConfirmationPrompt(), null, sig),
            (rig, _) => rig.Prompt("HOP>"));

    /// <summary>ROW 5 — <c>SsbSettingsViewModel</c>: the first-SSB query
    /// set.</summary>
    [Fact]
    public void Row05_SsbSettings_DefersItsFirstLoad_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new SsbSettingsViewModel(new SsbSurface(rig.Radio), rig.Session, null, sig),
            (rig, _) => rig.Prompt("SSB>"));

    /// <summary>ROW 6 — <c>AleSettingsViewModel</c>: the first-ALE
    /// <c>SH</c>.</summary>
    [Fact]
    public void Row06_AleSettings_DefersItsFirstLoad_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new AleSettingsViewModel(new AleSurface(rig.Radio), rig.Session, sig),
            (rig, _) => rig.Prompt("ALE>"));

    /// <summary>ROW 7 — <c>AleViewModel</c>, THE WORST PRODUCER: the first-ALE
    /// burst is <c>SLFAD</c>+<c>INDAD</c>+<c>NETAD</c>+sentinel then
    /// <c>CHG 0..9</c>+sentinel, and it lands as a campaign's book leg opens its
    /// gate brackets.
    ///
    /// <para>Built as the app builds it — with the Messages and LQA panes it
    /// folds in — so the row measures the whole ALE pane's contribution, which
    /// is what a campaign actually meets.</para></summary>
    [Fact]
    public void Row07_Ale_DefersItsFirstAleBurst_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new AleViewModel(
                new AleSurface(rig.Radio), rig.Session,
                new MessagesViewModel(new AleSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
                new LqaViewModel(new AleSurface(rig.Radio), new ChannelSurface(rig.Radio),
                    rig.Session, TimeProvider.System, sig),
                null, sig),
            (rig, _) => rig.Prompt("ALE>"));

    /// <summary>ROW 8 — <c>AleProgrammingViewModel</c>: the editor's
    /// initial-sight book read.</summary>
    [Fact]
    public void Row08_AleProgramming_DefersItsSightRead_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new AleProgrammingViewModel(
                new AleSurface(rig.Radio), rig.Session, new FakeConfirmationPrompt(), sig),
            (rig, _) => rig.Prompt("ALE>"));

    /// <summary>ROW 9 — <c>AleScanGroupsViewModel</c>: the sight-edge
    /// <c>CHG g</c>.</summary>
    [Fact]
    public void Row09_AleScanGroups_DefersItsSightRead_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new AleScanGroupsViewModel(new AleSurface(rig.Radio), rig.Session, sig),
            (rig, _) => rig.Prompt("ALE>"));

    /// <summary>ROW 10 — <c>LqaViewModel</c>: the sub-tab landing's bare
    /// <c>EXCH</c>. A TAB OPEN, which the suppression scope covers exactly like
    /// a mirror-event landing.</summary>
    [Fact]
    public void Row10_Lqa_DefersItsTabLandingRead_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new LqaViewModel(
                new AleSurface(rig.Radio), new ChannelSurface(rig.Radio),
                rig.Session, TimeProvider.System, sig),
            (rig, vm) => { rig.Prompt("ALE>"); ((LqaViewModel)vm).OnLqaTabOpened(); });

    /// <summary>ROW 11 — <c>MessagesViewModel</c>: the inbox landing's
    /// <c>RXM</c>.</summary>
    [Fact]
    public void Row11_Messages_DefersItsInboxLanding_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new MessagesViewModel(
                new AleSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            (rig, _) => rig.Prompt("ALE>"));

    /// <summary>ROW 12 — <c>SsbChannelEditorViewModel</c>: the card's first-load
    /// <c>DI n n</c>.</summary>
    [Fact]
    public void Row12_SsbChannelEditor_DefersItsFirstLoad_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new SsbChannelEditorViewModel(
                new ChannelSurface(rig.Radio), new SsbSurface(rig.Radio), rig.Session, sig),
            (rig, _) => rig.Prompt("SSB>"));

    /// <summary>ROW 13 — <c>DeviceSettingsViewModel</c>, the row P0 named as the
    /// one DEBT-CAPABLE shape: its <c>BAT ST</c> goes out as a BARE query,
    /// outside Core's ping queue, so it is exactly what can leave a campaign
    /// sentinel unpaid. All three of its triggers funnel through
    /// <c>EnsureLoaded</c>, which is what this row drives.</summary>
    [Fact]
    public void Row13_DeviceSettings_DefersItsLazyLoad_AndPaysItOnceAtCampaignEnd()
        => PinProducer(
            (rig, sig) => new DeviceSettingsViewModel(
                new DeviceSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            (_, vm) => ((DeviceSettingsViewModel)vm).EnsureLoaded());

    /// <summary>ROW 14 — <c>CouplerPolicy</c>, the SURFACE row, and §5.2's
    /// lazy-seed rule: DI resolves singletons lazily, so this policy can first
    /// be CONSTRUCTED in the middle of a campaign — by a surface the campaign
    /// itself asked for. Construction IS the trigger here, and the seeding
    /// <c>INTCOUPLER</c> is what defers.</summary>
    [Fact]
    public void Row14_CouplerPolicy_DefersItsSeedingRead_AndSeedsOnceAtCampaignEnd()
        => PinProducer((rig, sig) => new CouplerPolicy(rig.Radio, rig.Session, sig));

    // ===================== THE REFRESH-PRESS ROWS ===========================
    //
    // §4 SUPPRESSION SCOPE, decided: "AND explicit Refresh-button presses (the
    // press is accepted, the read runs at campaign end; buttons do not grey)".

    [Fact]
    public void ARefreshPress_OnTheSsbSettingsPane_IsAcceptedAndRunsAtCampaignEnd()
        => PinRefreshPress(
            (rig, sig) => new SsbSettingsViewModel(new SsbSurface(rig.Radio), rig.Session, null, sig),
            (rig, _) => rig.Prompt("SSB>"),
            vm => ((SsbSettingsViewModel)vm).RefreshSettingsCommand.Execute(null));

    [Fact]
    public void ARefreshPress_OnTheDeviceSettingsPane_IsAcceptedAndRunsAtCampaignEnd()
        => PinRefreshPress(
            (rig, sig) => new DeviceSettingsViewModel(
                new DeviceSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            (_, vm) => ((DeviceSettingsViewModel)vm).EnsureLoaded(),
            vm => ((DeviceSettingsViewModel)vm).RefreshDeviceSettingsCommand.Execute(null));

    [Fact]
    public void ARefreshPress_OnTheMessagesInbox_IsAcceptedAndRunsAtCampaignEnd()
        => PinRefreshPress(
            (rig, sig) => new MessagesViewModel(
                new AleSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            (rig, _) => rig.Prompt("ALE>"),
            vm => ((MessagesViewModel)vm).RefreshInboxCommand.Execute(null));

    [Fact]
    public void ARefreshPress_OnTheLqaTab_IsAcceptedAndRunsAtCampaignEnd()
        => PinRefreshPress(
            (rig, sig) => new LqaViewModel(
                new AleSurface(rig.Radio), new ChannelSurface(rig.Radio),
                rig.Session, TimeProvider.System, sig),
            (rig, _) => rig.Prompt("ALE>"),
            vm => ((LqaViewModel)vm).RefreshCommand.Execute(null));

    [Fact]
    public void ARefreshPress_OnTheChannelEditor_IsAcceptedAndRunsAtCampaignEnd()
        => PinRefreshPress(
            (rig, sig) => new SsbChannelEditorViewModel(
                new ChannelSurface(rig.Radio), new SsbSurface(rig.Radio), rig.Session, sig),
            (rig, _) => rig.Prompt("SSB>"),
            vm => ((SsbChannelEditorViewModel)vm).RefreshChannelsCommand.Execute(null));

    // ===================== THE CROSS-MODE ROWS ==============================
    //
    // AUDIT ROUND 1, MAJOR: the campaign's END EDGE IS NOT THE PAYMENT POINT.
    //
    // The first version of every handler below cleared its owed latch at the
    // campaign edge and then tried the read — which silently threw the debt
    // away whenever the campaign ended somewhere the producer may not read.
    // The auditor's repro: press Refresh on the SSB settings pane during a
    // clone, the campaign ends in HOP, come back to SSB — and the read never
    // fires again for the rest of the session.
    //
    // The rule now: a debt is settled by the PRODUCER'S OWN GATE, on whatever
    // event next finds it readable. The campaign edge merely runs the
    // recompute. Each pin below drives exactly the auditor's scenario:
    // defer inside a campaign, end the campaign in a mode the producer cannot
    // read at, and require the read on the NEXT matching confirmation —
    // exactly once.

    /// <summary>
    /// The cross-mode pin, run per mode-scoped producer.
    ///
    /// <para><paramref name="settle"/> runs OUTSIDE the campaign, to get a
    /// producer past its lazy first load when the debt under test is a later
    /// one (a Refresh press). <paramref name="trigger"/> runs INSIDE, once the
    /// producer's gate has opened, and is what incurs the debt.</para>
    /// </summary>
    private static void PinCrossModeOwedRead(
        Func<Rig, ICampaignSignal?, object> build,
        string readyPrompt,
        string otherPrompt,
        Action<Rig, object>? settle = null,
        Action<Rig, object>? trigger = null)
    {
        using var rig = new Rig();
        rig.ConnectReady();
        var producer = build(rig, rig.Wire);
        settle?.Invoke(rig, producer);
        rig.Transport.ClearSent();

        // ---- The debt is incurred inside the campaign -----------------------
        var lease = rig.Wire.Enter();
        rig.Prompt(readyPrompt);
        trigger?.Invoke(rig, producer);
        Assert.True(rig.Sent.Count == 0,
            "the producer talked over the campaign: " + string.Join(" | ", rig.Sent));

        // ---- …and the campaign ends SOMEWHERE ELSE -------------------------
        rig.Prompt(otherPrompt);
        lease.Dispose();
        Assert.True(rig.Sent.Count == 0,
            $"an owed read went out at {otherPrompt}, where this producer may not read: "
                + string.Join(" | ", rig.Sent));

        // ---- THE PIN: the next matching confirmation pays it ---------------
        rig.Prompt(readyPrompt);
        Assert.True(rig.Sent.Count > 0,
            "the owed read was LOST — the campaign edge consumed a debt the producer could not settle");

        // ---- …and pays it ONCE -------------------------------------------
        rig.Transport.ClearSent();
        rig.Prompt(otherPrompt);
        rig.Prompt(readyPrompt);
        Assert.True(rig.Sent.Count == 0,
            "the owed read fired a second time: " + string.Join(" | ", rig.Sent));
    }

    /// <summary>ROW 5 — <b>THE AUDITOR'S OWN REPRO.</b> Refresh pressed on the
    /// SSB settings pane while a clone runs; the campaign ends in HOP; the read
    /// must still be waiting when the operator comes back to SSB.</summary>
    [Fact]
    public void Row05_SsbSettings_ARefreshPressDeferred_SurvivesACampaignThatEndsInHop()
        => PinCrossModeOwedRead(
            (rig, sig) => new SsbSettingsViewModel(new SsbSurface(rig.Radio), rig.Session, null, sig),
            readyPrompt: "SSB>", otherPrompt: "HOP>",
            settle: (rig, _) => rig.Prompt("SSB>"),          // past the lazy first load
            trigger: (_, vm) => ((SsbSettingsViewModel)vm).RefreshSettingsCommand.Execute(null));

    /// <summary>ROW 4 — the HOP settings pane's picked-net landing.</summary>
    [Fact]
    public void Row04_HopSettings_ADeferredLanding_SurvivesACampaignThatEndsOutsideHop()
        => PinCrossModeOwedRead(
            (rig, sig) => new HopSettingsViewModel(
                new HopSurface(rig.Radio), rig.Session, new FakeConfirmationPrompt(), null, sig),
            readyPrompt: "HOP>", otherPrompt: "SSB>");

    /// <summary>ROW 8 — the ALE programming card's sight read.</summary>
    [Fact]
    public void Row08_AleProgramming_ADeferredBookRead_SurvivesACampaignThatEndsOutsideAle()
        => PinCrossModeOwedRead(
            (rig, sig) => new AleProgrammingViewModel(
                new AleSurface(rig.Radio), rig.Session, new FakeConfirmationPrompt(), sig),
            readyPrompt: "ALE>", otherPrompt: "SSB>");

    /// <summary>ROW 9 — the scan-groups pane's sight read.</summary>
    [Fact]
    public void Row09_AleScanGroups_ADeferredSightRead_SurvivesACampaignThatEndsOutsideAle()
        => PinCrossModeOwedRead(
            (rig, sig) => new AleScanGroupsViewModel(new AleSurface(rig.Radio), rig.Session, sig),
            readyPrompt: "ALE>", otherPrompt: "SSB>");

    /// <summary>ROW 10 — the LQA tab's landing read.</summary>
    [Fact]
    public void Row10_Lqa_ADeferredTabRead_SurvivesACampaignThatEndsOutsideAle()
        => PinCrossModeOwedRead(
            (rig, sig) => new LqaViewModel(
                new AleSurface(rig.Radio), new ChannelSurface(rig.Radio),
                rig.Session, TimeProvider.System, sig),
            readyPrompt: "ALE>", otherPrompt: "SSB>",
            trigger: (_, vm) => ((LqaViewModel)vm).OnLqaTabOpened());

    /// <summary>ROW 11 — the messages inbox landing.</summary>
    [Fact]
    public void Row11_Messages_ADeferredInboxRead_SurvivesACampaignThatEndsOutsideAle()
        => PinCrossModeOwedRead(
            (rig, sig) => new MessagesViewModel(
                new AleSurface(rig.Radio), rig.Session, TimeProvider.System, sig),
            readyPrompt: "ALE>", otherPrompt: "SSB>");

    /// <summary>ROW 12 — the channel editor's first load.</summary>
    [Fact]
    public void Row12_SsbChannelEditor_ADeferredFirstLoad_SurvivesACampaignThatEndsOutsideSsb()
        => PinCrossModeOwedRead(
            (rig, sig) => new SsbChannelEditorViewModel(
                new ChannelSurface(rig.Radio), new SsbSurface(rig.Radio), rig.Session, sig),
            readyPrompt: "SSB>", otherPrompt: "HOP>");

    /// <summary>
    /// ROW 1 — <c>ModemPresetsViewModel</c>'s gate is a CONFIRMED MODE rather
    /// than a particular one, so its unsettleable case is a campaign that ends
    /// with the radio having named no mode at all. The landing must still be
    /// owed when the first mode report finally lands.
    /// </summary>
    [Fact]
    public void Row01c_ModemPresets_ADeferredLanding_SurvivesACampaignThatEndsWithNoModeConfirmed()
    {
        using var rig = new Rig();
        rig.ConnectReady();
        var vm = new ModemPresetsViewModel(new ModemSurface(rig.Radio), rig.Session, rig.Wire);

        // ANTI-VACUITY: Ready, and the radio has still never said where it is.
        Assert.False(rig.Radio.State.OperatingMode.IsConfirmed);

        var lease = rig.Wire.Enter();
        vm.EnsureLoaded();                        // the card's Loaded hook, deferred
        Assert.True(rig.Sent.Count == 0, string.Join(" | ", rig.Sent));

        lease.Dispose();                          // …and the campaign ends, still unconfirmed
        Assert.True(rig.Sent.Count == 0,
            "the landing went out with no mode confirmed: " + string.Join(" | ", rig.Sent));

        rig.Prompt("SSB>");                       // the first mode report pays it
        Assert.True(rig.Sent.Count > 0, "the deferred landing was LOST");

        rig.Transport.ClearSent();
        rig.Prompt("HOP>");
        rig.Prompt("SSB>");
        // A real scope CHANGE is a landing of its own and reads — that is the
        // card's standing contract, not this debt firing twice. What must not
        // happen is a THIRD read with no scope change behind it.
        rig.Transport.ClearSent();
        rig.Prompt("SSB>");
        Assert.True(rig.Sent.Count == 0,
            "the deferred landing fired again: " + string.Join(" | ", rig.Sent));
    }

    /// <summary>
    /// ROW 13 — <c>DeviceSettingsViewModel</c>'s gate is Ready alone, so the
    /// only way a campaign can end where it cannot read is a SESSION DROP. The
    /// decision there is DELIBERATE and the opposite of the rows above: the
    /// press was for the radio that left, so the drop DISCARDS it, exactly as
    /// it discards the load flag beside it. The reconnect's own lazy load is
    /// what reads for the new radio — once.
    /// </summary>
    [Fact]
    public void Row13_DeviceSettings_ADeferredPress_IsDiscardedByASessionDrop_NotCarriedToTheNextRadio()
    {
        using var rig = new Rig();
        rig.ConnectReady();
        var vm = new DeviceSettingsViewModel(
            new DeviceSurface(rig.Radio), rig.Session, TimeProvider.System, rig.Wire);
        vm.EnsureLoaded();                        // past the lazy first load
        rig.Transport.ClearSent();

        var lease = rig.Wire.Enter();
        vm.RefreshDeviceSettingsCommand.Execute(null);
        Assert.True(rig.Sent.Count == 0, string.Join(" | ", rig.Sent));

        // The session goes while the campaign still holds the wire.
        rig.Session.Close();
        lease.Dispose();
        Assert.True(rig.Sent.Count == 0,
            "something went out at a dead session: " + string.Join(" | ", rig.Sent));

        // The next Ready session does its OWN lazy load on the arrival (which
        // ConnectReady clears away), so this pane is fully read for the new
        // radio and owes nothing.
        rig.ConnectReady();

        // THE PIN, made observable: run a SECOND campaign and end it. If the
        // dead radio's press had survived the drop, settling it is exactly what
        // this edge would do — five device reads with nobody having pressed
        // anything. It must send nothing at all.
        using (rig.Wire.Enter()) { }
        Assert.True(rig.Sent.Count == 0,
            "a press for the radio that LEFT was carried into the next session: "
                + string.Join(" | ", rig.Sent));
    }

    // ===================== THE NOTIFICATION ROW =============================

    /// <summary>
    /// <b>A NOTHING-BOUND-CHANGED PIN.</b> The quiesce touches READS, never the
    /// gates a pane renders: §4's decision is explicit that "buttons do not
    /// grey". So a producer's enabled-state and its disabled reason must read
    /// IDENTICALLY inside a campaign and outside it — an operator must never be
    /// told the radio is unavailable because a clone is running.
    ///
    /// <para><b>The one recorded consequence, and why it is not a
    /// counter-example.</b> <c>HopViewModel</c>'s select-outcome block STANDS
    /// DOWN during a campaign (§4's parked-escape correction), so a net select
    /// whose outcome arrives mid-campaign keeps SEND SYNC greyed until the
    /// campaign lets go and the same block runs. That PROLONGS a grey the
    /// operator's own gesture opened; it never OPENS one. No campaign makes any
    /// producer's gate close.</para></summary>
    [Fact]
    public void ACampaignDoesNotGreyAnything_TheEnabledStateAndItsReasonAreUnchanged()
    {
        using var control = new Rig();
        control.ConnectReady();
        var free = new SsbSettingsViewModel(new SsbSurface(control.Radio), control.Session, null, null);
        control.Prompt("SSB>");

        using var rig = new Rig();
        rig.ConnectReady();
        using var lease = rig.Wire.Enter();
        var quiesced = new SsbSettingsViewModel(
            new SsbSurface(rig.Radio), rig.Session, null, rig.Wire);
        rig.Prompt("SSB>");

        Assert.True(free.AreSettingsEnabled);                       // anti-vacuity
        Assert.Equal(free.AreSettingsEnabled, quiesced.AreSettingsEnabled);
        Assert.Equal(free.SettingsDisabledReason, quiesced.SettingsDisabledReason);
    }
}
