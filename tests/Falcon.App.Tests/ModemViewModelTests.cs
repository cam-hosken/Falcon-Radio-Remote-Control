using Falcon.App.Core.Demo;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// The cross-mode modem picker (round 8 ED — extracted from SignalViewModel
/// onto the power pattern). The round-2 D1 wheel and round-5 K7 transform
/// pins moved here verbatim; NEW are the cross-mode pins: the wheel works
/// from ALE and HOP prompts (both bench-confirmed — ALE by probe R8, HOP by
/// P5–P5d2 in clone-field round 2), and the gate is Ready + ANY confirmed mode
/// rather than confirmed SSB.
///
/// <para><b>F10 (owner ruling R-C, decision A-8): the wheel is MODE-SCOPED.</b>
/// Its positions are OFF plus the CONFIRMED mode's preset band — 0-6 at
/// SSB/ALE, 7-9 at HOP — and the presence store it skips disabled presets by is
/// keyed to the prompt its listing was read at.</para>
/// </summary>
public class ModemViewModelTests : SessionTestBase
{
    private ModemViewModel Vm()
        => new(new ModemSurface(Radio), Session);

    // ---- ROUND 13 B1 (item 6): the presence fixture --------------------------

    /// <summary>Answer the PRESENCE read the READY ARRIVAL dispatched, with a
    /// bulk listing naming exactly <paramref name="enabled"/>. Absence from a
    /// COMPLETED bulk listing is the only captured DISABLED signal there is
    /// (round 11 §6), so this is how a wheel test says "these presets are
    /// selectable and the rest are not". Self-checking at both ends: the read
    /// has to be open on entry and committed on exit, or the fixture is
    /// asserting against a store nothing ever wrote.</summary>
    private void CompletePresence(params int[] enabled)
        => CompletePresenceAt(OperatingMode.Ssb, enabled);

    /// <summary>
    /// CLONE-FIELD ROUND 2 F10 — the same fixture, now SCOPE-AWARE. A
    /// committed presence set only answers for the PROMPT BAND its listing was
    /// read at (0-6 at <c>SSB&gt;</c>/<c>ALE&gt;</c>, 7-9 at <c>HOP&gt;</c>), so
    /// the read this completes has to have gone out at a confirmed prompt.
    ///
    /// <para>AUDIT ROUND 2 (MAJOR 1) SIMPLIFIED THIS. The wheel's Ready arrival
    /// no longer issues an UNSCOPED read that has to be drained and re-issued:
    /// it OWES the landing and pays it when the prompt names the mode, so by
    /// the time this runs the read on the wire is ALREADY the right band's.
    /// The fixture asserting that is itself part of the pin.</para>
    /// </summary>
    private void CompletePresenceAt(OperatingMode prompt, params int[] enabled)
    {
        Assert.True(Radio.State.OperatingMode.IsConfirmed,
            "the fixture needs a CONFIRMED prompt before a scoped presence read");
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);
        Assert.Equal(prompt, Radio.State.ModemPresenceReadScope);

        foreach (int n in enabled) Transport.InjectLine(ListingLine(prompt, n));
        AnswerSentinel();                               // the presence sentinel → it commits
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        Assert.Equal(enabled.Order(), Radio.State.ModemPresetPresence.Enabled);
        Assert.True(Radio.State.ModemPresetPresence.Covers(prompt),
            "the committed set is not keyed to the prompt it was read at");
    }

    /// <summary>A bulk-listing line in the shape THAT PROMPT prints: the SSB
    /// form with TYPE/INTER, or the SHORT HOP form with neither (P5).</summary>
    private static string ListingLine(OperatingMode prompt, int preset)
        => prompt == OperatingMode.Hop
            ? $"MODEM PRESET {preset} DAT{preset} ASYNC REMOTE BAUD 300   "
            : $"MODEM PRESET {preset} P{preset}  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long";

    /// <summary>The wheel armed at a confirmed SSB prompt on
    /// <paramref name="echo"/>, with the wire cleared for the press under
    /// test.</summary>
    private ModemViewModel ArmedAt(string echo, params int[] enabled)
        => ArmedAtPrompt(OperatingMode.Ssb, "SSB>", echo, enabled);

    private ModemViewModel ArmedAtPrompt(OperatingMode prompt, string promptLine, string echo, params int[] enabled)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine(promptLine);
        CompletePresenceAt(prompt, enabled);
        Transport.InjectLine(echo);
        Transport.ClearSent();
        Assert.True(vm.CanSpinModem);
        return vm;
    }

    [Fact]
    public void ModemWheel_SpinsFromConfirmedEcho_DisplayMovesOnlyOnEcho()
    {
        // D1 (UI tweaks round 2), WIDENED by clone round 12 §9 A6: the modem
        // is a picker wheel cycling OFF → 0 → 1 → … → 6 → wrap — EIGHT
        // positions. A spin needs a CONFIRMED echo to compute the next
        // position; the display IS the echo (no optimism).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.Equal("—", vm.ModemDisplayText);         // unreported = dash
        Assert.False(vm.CanSpinModem);
        vm.ModemUpCommand.Execute(null);                // no basis to spin
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("MODEM OFF");
        Assert.Equal("OFF", vm.ModemDisplayText);
        Assert.True(vm.CanSpinModem);

        // §9 A6 re-pin: this asserted "MODEM 1" through round 11, because
        // position 1 WAS preset 1 and preset 0 had no position at all.
        vm.ModemUpCommand.Execute(null);                // OFF → preset 0
        Assert.Equal(["MODEM 0"], Transport.SentLines);
        Assert.Equal("OFF", vm.ModemDisplayText);       // no optimism

        Transport.InjectLine("MODEM 1 T39");            // the selection echo
        Assert.Equal("1: T39", vm.ModemDisplayText);    // K7 formatting (round 5)
        Transport.ClearSent();

        vm.ModemUpCommand.Execute(null);                // 1 → 2
        Assert.Equal(["MODEM 2"], Transport.SentLines);
        Transport.ClearSent();

        // §9 A6 re-pin: down from preset 1 lands on preset 0, not on OFF.
        vm.ModemDownCommand.Execute(null);              // still confirmed 1 → 0
        Assert.Equal(["MODEM 0"], Transport.SentLines);
    }

    [Fact]
    public void ModemWheel_WrapsAtBothEnds()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODEM 6 K1");             // top of the wheel
        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);                // 6 wraps → OFF
        Assert.Equal(["MODEM OF"], Transport.SentLines);

        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();
        vm.ModemDownCommand.Execute(null);              // OFF wraps → 6
        Assert.Equal(["MODEM 6"], Transport.SentLines);
    }

    // ---- CLONE ROUND 12 §9 A6: the eighth position -------------------------

    /// <summary>Slot 0 is REACHABLE. The programming card has always
    /// programmed 0-6 (<c>ModemPresetsViewModel.PresetCount</c>); the wheel
    /// cycled OFF + 1-6, so preset 0 could be written and never selected. Both
    /// neighbours of the new position are pinned, in both directions, because
    /// an off-by-one in the mapping would still produce a plausible-looking
    /// wheel.</summary>
    [Fact]
    public void TheWheelReachesPresetZero_FromBothNeighbours_A6()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);                // OFF → 0
        Assert.Equal(["MODEM 0"], Transport.SentLines);

        Transport.InjectLine("MODEM 1 T39");
        Transport.ClearSent();
        vm.ModemDownCommand.Execute(null);              // 1 → 0
        Assert.Equal(["MODEM 0"], Transport.SentLines);
    }

    /// <summary>The CONFIRMED-ECHO mapping for preset 0: a "0 NAME" echo is a
    /// legal spin basis and its neighbours are OFF below and preset 1 above.
    /// Round 11's position test rejected <c>n == 0</c> outright, so an echo
    /// from slot 0 would have disabled the wheel entirely.</summary>
    [Fact]
    public void APresetZeroEcho_IsASpinBasis_AndSitsBetweenOffAndOne_A6()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODEM 0 T39");
        Assert.True(vm.CanSpinModem);                   // a legal position now
        Assert.Equal("0: T39", vm.ModemDisplayText);

        Transport.ClearSent();
        vm.ModemDownCommand.Execute(null);              // 0 → OFF
        Assert.Equal(["MODEM OF"], Transport.SentLines);

        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);                // 0 → 1
        Assert.Equal(["MODEM 1"], Transport.SentLines);
    }

    /// <summary>The wheel is EIGHT positions, asserted by walking it: eight
    /// ups from OFF return to OFF, having visited every preset the
    /// programming card programs and nothing else. This is the anti-vacuity
    /// pin for the two above — a seven- or nine-position wheel passes neither
    /// end-to-end.</summary>
    [Fact]
    public void EightUpsFromOff_WalkEveryPresetAndReturnToOff_A6()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODEM OFF");

        var walked = new List<string>();
        string echo = "MODEM OFF";
        for (int i = 0; i < 8; i++)
        {
            Transport.InjectLine(echo);                 // confirm where we are
            Transport.ClearSent();
            vm.ModemUpCommand.Execute(null);
            string sent = Assert.Single(Transport.SentLines);
            walked.Add(sent);
            // The radio's answer to MODEM n is the selection echo; MODEM OF
            // answers OFF. Feed the next iteration what the radio would say.
            echo = sent == "MODEM OF" ? "MODEM OFF" : "MODEM " + sent["MODEM ".Length..] + " NAME";
        }

        Assert.Equal(
            ["MODEM 0", "MODEM 1", "MODEM 2", "MODEM 3",
             "MODEM 4", "MODEM 5", "MODEM 6", "MODEM OF"],
            walked);
    }

    // ---- Round 8 (ED): the cross-mode gate -----------------------------------

    [Fact]
    public void ModemWheel_WorksFromAnAlePrompt_TheR8ConfirmedCase()
    {
        // Probe R8: MODEM 1 at an ALE> prompt answers the normal selection
        // echo (plus an async SCANNING resume the banner reports on its own
        // line). The wheel therefore spins from a confirmed ALE mode.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();

        Assert.True(vm.CanSpinModem);
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 0"], Transport.SentLines);   // §9 A6: OFF → preset 0
    }

    [Fact]
    public void ModemWheel_AtAHopPrompt_CyclesOffPlusSevenToNine()
    {
        // CLONE-FIELD ROUND 2 F10 (owner ruling R-C, decision A-8). This test
        // asserted `MODEM 0` while the HOP set path was PROVISIONAL and the
        // wheel had one fixed range. Probes P5/P5d2 settled the contract: a
        // `HOP>` prompt owns presets 7-9 and answers `INVALID MODEM PRESET` to
        // 0-6, so OFF's neighbour here is SEVEN.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();

        Assert.True(vm.CanSpinModem);
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 7"], Transport.SentLines);
    }

    /// <summary>THE RANGE PIN (F10), walked all the way round from OFF in each
    /// scope and back onto OFF. Under HOP the wheel is FOUR positions
    /// (OFF · 7 · 8 · 9); under SSB and ALE it is EIGHT (OFF · 0 … 6). Nothing
    /// but the confirmed prompt differs — same wheel, same instance shape, same
    /// presses. One case per test instance, because each walk needs its own
    /// session.</summary>
    [Theory]
    [InlineData("HOP>", "MODEM 7,MODEM 8,MODEM 9,MODEM OF-")]
    [InlineData("SSB>", "MODEM 0,MODEM 1,MODEM 2,MODEM 3,MODEM 4,MODEM 5,MODEM 6,MODEM OF-")]
    [InlineData("ALE>", "MODEM 0,MODEM 1,MODEM 2,MODEM 3,MODEM 4,MODEM 5,MODEM 6,MODEM OF-")]
    public void TheWheelPositions_AreOffPlusTheConfirmedModesBand_F10(string promptLine, string expected)
    {
        var want = expected.Split(',');
        Assert.Equal(want, WalkTheWheel(promptLine, want.Length));
    }

    /// <summary>Press ▶ <paramref name="positions"/> times from OFF, echoing
    /// each selection back so the next press has a confirmed basis, and return
    /// what went out. <c>MODEM OF</c> is normalised to <c>MODEM OF-</c> so a
    /// wrap onto OFF is visibly distinct from a preset number in the assertion.</summary>
    private List<string> WalkTheWheel(string promptLine, int positions)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine(promptLine);
        Transport.InjectLine("MODEM OFF");

        var sent = new List<string>();
        for (int i = 0; i < positions; i++)
        {
            Transport.ClearSent();
            vm.ModemUpCommand.Execute(null);
            var line = Assert.Single(Transport.SentLines);
            sent.Add(line == "MODEM OF" ? "MODEM OF-" : line);
            // Echo the radio's answer so the NEXT press has a confirmed basis.
            Transport.InjectLine(line == "MODEM OF"
                ? "MODEM OFF"
                : "MODEM " + line["MODEM ".Length..] + " NAME");
        }
        return sent;
    }

    [Fact]
    public void TheWheel_AtAHopPrompt_SkipsDisabledHopPresets_F10()
    {
        // The SKIP, per scope: 7 and 9 enabled, 8 not. One press from OFF lands
        // on 7; the next SKIPS 8 and lands on 9; the next wraps to OFF.
        var vm = ArmedAtPrompt(OperatingMode.Hop, "HOP>", "MODEM OFF", 7, 9);

        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 7"], Transport.SentLines);

        Transport.InjectLine("MODEM 7 DAT7");
        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 9"], Transport.SentLines);      // 8 is disabled

        Transport.InjectLine("MODEM 9 DAT9");
        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM OF"], Transport.SentLines);     // the wrap
    }

    [Fact]
    public void APresenceSetReadAtSsb_SaysNothingAboutTheHopBand_F10()
    {
        // THE SCOPE KEY, load-bearing. A COMPLETED listing read at `SSB>` names
        // the enabled 0-6 and CANNOT name 7-9 — reading it as "7, 8 and 9 are
        // all disabled" is exactly the misreading round 13 recorded as a
        // wholesale HOP refusal. Under HOP the wheel must treat it as NO DATA
        // and take the adjacent step, not as an empty enabled set (which would
        // make the wheel OFF-only and send nothing at all).
        var vm = ArmedAt("MODEM OFF", 0, 1);                 // an SSB-scoped set
        Assert.True(Radio.State.ModemPresetPresence.Covers(OperatingMode.Ssb));

        Transport.InjectLine("HOP>");
        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();

        Assert.False(Radio.State.ModemPresetPresence.Covers(OperatingMode.Hop));
        vm.ModemUpCommand.Execute(null);
        // The adjacent step in the HOP band — NOT silence, and NOT `MODEM 0`.
        Assert.Contains("MODEM 7", Transport.SentLines);
        Assert.DoesNotContain("MODEM 0", Transport.SentLines);
    }

    /// <summary>
    /// AUDIT ROUND 1, MAJOR 1 — <b>entering HOP asks for the HOP band's
    /// presence ON ARRIVAL</b>, before any press. A confirmed scope change is a
    /// LANDING, exactly as the Ready arrival is: without it the first chevron
    /// after the entry had neither data nor a read on the way, so it took the
    /// adjacent step and could select a DISABLED preset the radio then refused.
    /// </summary>
    [Fact]
    public void EnteringHop_AsksForTheHopBandsPresence_OnArrival_BeforeAnyPress_F10()
    {
        var vm = ArmedAt("MODEM OFF", 0, 1);            // an SSB-scoped set, committed
        Transport.ClearSent();

        Transport.InjectLine("HOP>");

        // The bare listing is on the wire from the ARRIVAL — no press involved.
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.True(Radio.State.ModemPresetPresence.State == RadioState.PresenceState.InFlight);

        // And it is not re-asked on every mirror change: the trigger is a
        // CHANGE OF SCOPE, so an abandoned read cannot make it poll.
        Transport.InjectLine("MODEM OFF");
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        AnswerSentinel();                               // abandon it (nothing listed)
        Transport.InjectLine("MODEM OFF");
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));

        Assert.Equal("OFF", vm.ModemDisplayText);
    }

    /// <summary>
    /// AUDIT ROUND 2, MAJOR 1 — <b>the ordinary connect, and the one the
    /// previous round got wrong.</b> Ready arrives BEFORE the prompt line, so
    /// at Ready the app cannot name a band; it must ask NOTHING and OWE the
    /// landing. When the first prompt is <c>HOP&gt;</c>, the one read that goes
    /// out is HOP-scoped — where before, an UNSCOPED read went out at Ready,
    /// the HOP landing then found it in flight and stood down, and its answer
    /// committed <c>Scope = null</c>, so the first press had no data for the
    /// band it was spinning and could pick a disabled preset.
    /// </summary>
    [Theory]
    [InlineData("HOP>", (int)OperatingMode.Hop)]
    [InlineData("SSB>", (int)OperatingMode.Ssb)]
    [InlineData("ALE>", (int)OperatingMode.Ale)]
    public void ReadyBeforeThePrompt_AsksNOTHING_ThenExactlyOneReadForTheBandThatArrives_MAJOR1(
        string promptLine, int expected)
    {
        var mode = (OperatingMode)expected;
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);

        // Ready with no band named: NOTHING asked.
        Assert.Equal(0, Transport.CountSent("MODEM PRE"));
        Assert.False(Radio.State.OperatingMode.IsConfirmed);

        Transport.InjectLine(promptLine);

        // EXACTLY ONE read, and it carries the band that just arrived.
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.Equal(mode, Radio.State.ModemPresenceReadScope);

        AnswerSentinel();
        Assert.True(Radio.State.ModemPresetPresence.Covers(mode));
        Assert.NotNull(vm);
    }

    /// <summary>
    /// AUDIT ROUND 2, MAJOR 1 — a scope change while a read for the OTHER band
    /// is IN FLIGHT must not be dropped. `EnsurePresenceLoaded` used to return
    /// on any in-flight operation; now it queues the new band's question behind
    /// the old one, so BOTH get asked and each commits to its own band.
    /// </summary>
    [Fact]
    public void AScopeChange_WhileTheOtherBandsReadIsInFlight_STILL_GetsItsOwnRead_MAJOR1()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");                       // the owed landing → SSB read
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.Equal(OperatingMode.Ssb, Radio.State.ModemPresenceReadScope);

        // HOP confirms while that read is still open.
        Transport.InjectLine("HOP>");
        Assert.Equal(RadioState.PresenceState.InFlight, Radio.State.ModemPresetPresence.State);
        // The HOP question is QUEUED, not lost — and not sent early either.
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.Equal(OperatingMode.Hop, Radio.State.ModemPresenceReadScope);

        // The SSB read answers (naming preset 1) and commits to ITS band…
        Transport.InjectLine(ListingLine(OperatingMode.Ssb, 1));
        AnswerSentinel();
        Assert.Equal(2, Transport.CountSent("MODEM PRE"));  // …and the HOP read follows it

        // The HOP read answers (naming preset 9) and commits to ITS band.
        Transport.InjectLine(ListingLine(OperatingMode.Hop, 9));
        AnswerSentinel();

        var presence = Radio.State.ModemPresetPresence;
        Assert.True(presence.Covers(OperatingMode.Hop));
        Assert.False(presence.Covers(OperatingMode.Ssb));
        Assert.Equal([9], presence.Enabled);                // NOT [1, 9]
        Assert.NotNull(vm);
    }

    [Fact]
    public void PresetDisabled_RendersTheProse_AndLeavesTheDisplayOnTheConfirmedState_F10()
    {
        // I-7 (display truth). `PRESET DISABLED` is the radio saying the select
        // changed NOTHING (P5d: `MODEM SH` afterwards still reports `MODEM
        // OFF`), so the wheel's display must stay exactly where the last
        // confirmed echo put it — and the operator must be told, in prose.
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        var vm = ArmedAtPrompt(OperatingMode.Hop, "HOP>", "MODEM 9 DAT9", 9);
        Assert.Equal("9: DAT9", vm.ModemDisplayText);

        Transport.InjectLine("PRESET DISABLED");

        var message = Assert.Single(errors);
        Assert.Contains("disabled", message, StringComparison.OrdinalIgnoreCase);
        // R13/I-5: prose, not the wire token.
        Assert.DoesNotContain("PRESET DISABLED", message, StringComparison.Ordinal);
        Assert.Equal("9: DAT9", vm.ModemDisplayText);
    }

    [Fact]
    public void EveryRewiredProperty_RaisesPropertyChanged_F10()
    {
        // The two properties the scoped wheel writes are the two the markup
        // binds. A range change that computed correctly and told nobody would
        // leave the row frozen on screen — the class of defect the round-11
        // audit called out for the presence store.
        var vm = Vm();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("MODEM OFF");

        Assert.Contains(nameof(ModemViewModel.ModemDisplayText), raised);
        Assert.Contains(nameof(ModemViewModel.CanSpinModem), raised);
        Assert.Equal("OFF", vm.ModemDisplayText);
        Assert.True(vm.CanSpinModem);
    }

    [Fact]
    public void NoConfirmedMode_NoSpin_EvenWithAConfirmedEcho()
    {
        // The gate is Ready + a CONFIRMED mode. A MODEM line without any
        // mode confirmation this session (no prompt seen) renders honestly
        // but arms nothing.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("MODEM OFF");
        Transport.ClearSent();

        Assert.Equal("OFF", vm.ModemDisplayText);       // display is honest
        Assert.False(vm.CanSpinModem);                  // but nothing sends
        vm.ModemUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NotReady_NothingSent()
    {
        var vm = Vm();

        Assert.False(vm.CanSpinModem);
        vm.ModemUpCommand.Execute(null);
        vm.ModemDownCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    // ---- K7: the modem display transform (UI tweaks round 5, BB) ------------

    [Fact]
    public void ModemDisplay_Unconfirmed_RendersTheDash()
    {
        // The constitution's second display state — nothing has been reported,
        // so nothing is claimed. (Pinned through the VM, not the helper, so a
        // future Refresh that forgot to call the transform still fails.)
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Assert.Equal("—", vm.ModemDisplayText);
    }

    [Fact]
    public void ModemDisplay_Off_RendersOff()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODEM OFF");

        Assert.Equal("OFF", vm.ModemDisplayText);
    }

    [Theory]
    [InlineData("MODEM 1 T39", "1: T39")]       // the captured selection echo
    [InlineData("MODEM 6 XXXX", "6: XXXX")]     // the width-defining widest legal text
    public void ModemDisplay_NumberThenName_GainsTheColon(string echo, string expected)
    {
        // BB1/K7: a pure FORMATTING transform — preset number, colon, space,
        // the STORED name. Nothing is mined from MODEM PRE and no name is
        // derived from a type (owner ruling: keep the display verbatim).
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine(echo);

        Assert.Equal(expected, vm.ModemDisplayText);
    }

    [Theory]
    [InlineData("MODEM 1 T39 EXTRA", "1 T39 EXTRA")]   // three tokens: not the K7 shape
    [InlineData("MODEM 7", "7")]                        // number alone: no name to punctuate
    public void ModemDisplay_AnyOtherConfirmedEcho_RendersVerbatim_ThroughTheVm(string line, string expected)
    {
        // The honesty fallback, pinned END TO END on echoes the parser really
        // does mirror. An echo whose shape was never captured is shown as the
        // radio wrote it — never reshaped into a format that would assert a
        // meaning we have not observed.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine(line);

        Assert.Equal(expected, vm.ModemDisplayText);
        Assert.DoesNotContain(":", vm.ModemDisplayText);
    }

    [Theory]
    // Shapes the CURRENT parser does not route into ActiveModem at all (the
    // PRESET listing feeds the round-8 presets mirror, not ActiveModem; a
    // name-only echo is not mirrored) — pinned on the transform directly, so
    // the fallback stays correct if a later parser round starts mirroring
    // them. Named as such rather than dressed up as behaviour the app can
    // reach today.
    [InlineData("PRESET 1 T39  ASYNC DATA   BAUD 2400")]
    [InlineData("T39")]
    [InlineData("NOT INSTALLED")]
    public void ModemDisplay_UncapturedShapes_TakeTheVerbatimPath(string echo)
    {
        var display = ModemViewModel.ModemDisplay(Falcon.Core.Radio.Confirmed<string>.Of(echo));

        Assert.Equal(echo, display);
    }

    [Fact]
    public void ModemDisplay_TransformIsFormattingOnly_NeverInventsOrDropsInformation()
    {
        // Anti-vacuity for the pins above: the ONLY difference between the
        // echo and the display is the inserted colon. Undo it and the echo is
        // back, character for character — so the transform cannot be quietly
        // replaced by one that DERIVES a name (the thing BB1 forbids) or
        // drops a token.
        var display = ModemViewModel.ModemDisplay(Falcon.Core.Radio.Confirmed<string>.Of("1 T39"));

        Assert.Equal("1: T39", display);
        Assert.Equal("1 T39", display.Replace(": ", " ", StringComparison.Ordinal));
        Assert.Equal("—", ModemViewModel.ModemDisplay(Falcon.Core.Radio.Confirmed<string>.Unconfirmed));
    }

    // ---- The extraction itself, pinned ---------------------------------------

    [Fact]
    public void SignalViewModel_NoLongerCarriesTheModemCluster()
    {
        // ED's structural half: a leftover XAML binding to the OLD home would
        // render nothing at runtime (MAUI swallows missing paths), so the
        // absence is pinned — and the new home's presence in the same breath.
        var signal = typeof(SignalViewModel).GetMembers().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in new[]
        {
            "ModemDisplayText", "CanSpinModem",
            "ModemUpCommand", "ModemDownCommand", "ModemDisplay",
        })
            Assert.DoesNotContain(gone, signal);

        var modem = typeof(ModemViewModel).GetMembers().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var present in new[]
        {
            "ModemDisplayText", "CanSpinModem",
            "ModemUpCommand", "ModemDownCommand",
        })
            Assert.Contains(present, modem);
    }

    // ==== ROUND 13 B1 (item 6, owner 2026-08-19 / ruling 2026-08-20) ==========
    // "When a chevron press would land on a DISABLED preset, do NOT show the
    // bottom-of-screen error message; VERIFY the selector silently skips over
    // deselected presets."
    //
    // The skip is computed at TARGET-COMPUTATION time and reads the PRESENCE
    // store — the app never invents a constraint it has not read. Everything
    // above this line runs with presence NOT completed and is UNCHANGED by this
    // round, which is the degrade-to-today's-behavior half of the contract.

    [Fact]
    public void TheWheel_SkipsOneDisabledPreset_R13B1()
    {
        // Preset 2 is absent from the completed listing ⇒ DISABLED. Up from
        // preset 1 lands on 3, and MODEM 2 never reaches the wire — which is
        // what stops the radio's refusal, and therefore the toast.
        var vm = ArmedAt("MODEM 1 T39", 0, 1, 3, 4, 5, 6);

        vm.ModemUpCommand.Execute(null);

        Assert.Equal(["MODEM 3"], Transport.SentLines);
        Assert.Equal("1: T39", vm.ModemDisplayText);        // no optimism: still the echo
    }

    [Fact]
    public void TheWheel_SkipsARUNOfDisabledPresets_InBothDirections_R13B1()
    {
        // 2, 3, 4 and 5 are all disabled: one press crosses the whole run
        // rather than stopping inside it.
        var vm = ArmedAt("MODEM 1 T39", 0, 1, 6);
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 6"], Transport.SentLines);

        // …and the same run is crossed the other way. (Down from 6 is the
        // mirror press: a direction-blind implementation that always searched
        // upward would send MODEM 0 here.)
        Transport.InjectLine("MODEM 6 P6");
        Transport.ClearSent();
        vm.ModemDownCommand.Execute(null);
        Assert.Equal(["MODEM 1"], Transport.SentLines);
    }

    [Fact]
    public void TheWheel_WrapsPastTheEndWhileSkipping_R13B1()
    {
        // The wrap and the skip compose. Down from OFF wraps to the top of the
        // wheel and then walks DOWN past 6, 5, 4, 3 and 2 — all disabled — to
        // the only enabled preset there is.
        var vm = ArmedAt("MODEM OFF", 1);

        vm.ModemDownCommand.Execute(null);

        Assert.Equal(["MODEM 1"], Transport.SentLines);
    }

    [Fact]
    public void OffIsTheBackstop_WhenEveryPresetIsDisabled_R13B1()
    {
        // OFF is ALWAYS selectable, so the walk is total: it can never run off
        // the end of the wheel looking for a position that does not exist.
        var vm = ArmedAt("MODEM 1 T39");                    // completed, enabled set EMPTY

        vm.ModemUpCommand.Execute(null);

        Assert.Equal(["MODEM OF"], Transport.SentLines);
    }

    [Fact]
    public void AnEmptyEnabledSet_AtOff_SendsNothingAndSaysNothing_R13B1()
    {
        // The one press with nowhere to go. OFF is the only selectable
        // position and the wheel is already on it, so the press sends NO
        // command and raises NO error — there is nothing to select and nothing
        // honest to say about it.
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);
        var vm = ArmedAt("MODEM OFF");                      // completed, enabled set EMPTY

        vm.ModemUpCommand.Execute(null);
        vm.ModemDownCommand.Execute(null);

        Assert.Empty(Transport.SentLines);
        Assert.Empty(errors);
        Assert.Equal("OFF", vm.ModemDisplayText);
    }

    [Fact]
    public void PresenceNotCompleted_TheWheelTakesThePlainAdjacentStep_R13B1()
    {
        // The DEGRADE half of the contract (constitution §3.1): with no
        // completed read the app has read no constraint, so it invents none —
        // the adjacent step goes out exactly as it did before this round and
        // the radio validates it.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODEM 1 T39");
        Transport.ClearSent();

        Assert.NotEqual(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 2"], Transport.SentLines);

        // ANTI-VACUITY: same wheel, same echo, same press — the ONLY thing that
        // changed is that the presence read answered, and preset 2 is not in
        // it. A skip that ignored presence would send MODEM 2 twice; one that
        // skipped on absent data would never have sent it at all.
        CompletePresence(0, 1, 3, 4, 5, 6);
        Transport.ClearSent();
        vm.ModemUpCommand.Execute(null);
        Assert.Equal(["MODEM 3"], Transport.SentLines);
    }

    [Fact]
    public void TheSkipIsScopedToPresence_AnEnabledPresetIsStillSelected_R13B1()
    {
        // The skip is scoped to the PRESENCE axis ONLY. An enabled preset is
        // selected without a second thought, so "skip" cannot quietly become
        // "the wheel only ever goes to OFF".
        var vm = ArmedAt("MODEM 1 T39", 0, 1, 2, 3, 4, 5, 6);

        vm.ModemUpCommand.Execute(null);

        Assert.Equal(["MODEM 2"], Transport.SentLines);
    }

    // ---- The presence ENSURE contract (plan §4 B1) --------------------------

    [Fact]
    public void ThePresenceEnsure_FiresOnceAcrossBothViewModels_R13B1()
    {
        // The gate lives on the SURFACE and is driven by the presence STATE, so
        // two consumers of one surface cost ONE bulk read between them. Round
        // 12's flag was private to the settings card and could not be shared
        // with the wheel at all. Driven by hand: ConnectReady's trailing
        // ClearSent would wipe the very sends under test.
        var surface = new ModemSurface(Radio);
        var wheel = new ModemViewModel(surface, Session);
        var card = new ModemPresetsViewModel(surface, Session);

        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);

        // AUDIT ROUND 2 (MAJOR 1): Ready alone asks NOTHING — a presence read
        // needs a band, and no prompt has named one yet. Both consumers OWE
        // their landing…
        Assert.Equal(0, Transport.CountSent("MODEM PRE"));
        Transport.InjectLine("SSB>");

        // …and between them they still pay for exactly ONE bulk read, which is
        // this test's whole contract.
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.NotNull(wheel);

        card.EnsureLoaded();                                // the card's landing gesture
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));

        Transport.InjectLine("MODEM OFF");
        wheel.ModemUpCommand.Execute(null);                 // the wheel's spin-time ensure
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.Contains("MODEM 0", Transport.SentLines);    // …and the press still worked
    }

    [Fact]
    public void EnsurePresenceLoaded_CoalescesWhileTheReadIsStillPending_R13B1()
    {
        // A presence request QUEUED behind an active targeted read is not
        // promoted to InFlight, so the store still reports Unknown and a second
        // ensure re-issues. The single-slot modem queue coalesces it onto the
        // same pending operation: extra calls, NO extra wire line.
        var surface = new ModemSurface(Radio);
        ConnectReady();                                     // no wheel VM here — nothing ensured
        Transport.InjectLine("SSB>");                       // …and a band to ask about (round 2)

        surface.QueryPreset(3);                             // the targeted read owns the slot
        surface.EnsurePresenceLoaded();                     // presence queues behind it
        Assert.Equal(RadioState.PresenceState.Unknown, surface.PresetPresence.State);

        surface.EnsurePresenceLoaded();
        surface.EnsurePresenceLoaded();
        Assert.Equal(0, Transport.CountSent("MODEM PRE"));  // still nothing bare on the wire

        AnswerSentinel();                                   // targeted sentinel → promotion
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));  // ONE read for three ensures
        Assert.Equal(RadioState.PresenceState.InFlight, surface.PresetPresence.State);
    }

    [Fact]
    public void EnsurePresenceLoaded_RetriesAfterAnAbandonedRead_R13B1()
    {
        // An unanswered operation restores the last COMMITTED presence —
        // Unknown when none ever committed — so a later ensure ASKS AGAIN.
        // Round 12's session flag could not: one silence and the card went dark
        // for the rest of the session. This is an improvement adopted
        // deliberately (plan §4 B1), not a side effect.
        var surface = new ModemSurface(Radio);
        ConnectReady();
        Transport.InjectLine("SSB>");                       // a band to ask about (round 2)
        Radio.Ssb.ModemReadTimeoutMs = 80;

        surface.EnsurePresenceLoaded();
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));
        Assert.True(WaitUntil(() =>
            Radio.State.ModemPresetPresence.State == RadioState.PresenceState.Unknown
            && !Radio.State.LastModemRead.Answered));

        Radio.Ssb.ModemReadTimeoutMs = 10_000;              // the retry gets a real window
        surface.EnsurePresenceLoaded();
        Assert.Equal(2, Transport.CountSent("MODEM PRE"));  // it asked again

        // ANTI-VACUITY: once a read COMMITS, ensure stops asking. Without this
        // the pin above would also pass for an ensure with no gate at all.
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        AnswerSentinel();
        Assert.Equal(RadioState.PresenceState.Completed, surface.PresetPresence.State);
        surface.EnsurePresenceLoaded();
        surface.EnsurePresenceLoaded();
        Assert.Equal(2, Transport.CountSent("MODEM PRE"));
    }
}

/// <summary>
/// ROUND 13 B1 (item 6) — the DEMO-PATH pin the phase gate names, driven over
/// the production stack (DemoSerialPort → SerialTransport → Prc138Radio →
/// RadioSession → ModemSurface → ModemViewModel) rather than the line-injecting
/// transport. The owner's report was about the app in front of a radio, and the
/// injecting transport cannot show the thing that actually matters: that the
/// radio never SEES a <c>MODEM 2</c>, so its <c>PRESET DISABLED</c> refusal —
/// the toast — never happens.
///
/// <para>The fixture is the demo's canned lockout: preset 2 is served by the
/// TARGETED read and absent from the BULK listing, and selecting it answers the
/// captured refusal (DemoSerialPort, clone round 12 §9 A1).</para>
/// </summary>
public sealed class ModemWheelDemoSkipTests : IDisposable
{
    private readonly DemoSerialPort _demo = new() { ResponseDelayMs = 0, TuneTerminalDelayMs = 0 };
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly ModemSurface _modem;

    /// <summary>The demo's canned DISABLED preset.</summary>
    private const int DisabledPreset = 2;

    public ModemWheelDemoSkipTests()
    {
        _transport = new SerialTransport(_demo) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _modem = new ModemSurface(_radio);
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        Assert.True(condition(), "timed out waiting for: " + what);
    }

    [Fact]
    public void TheChevron_SkipsTheDemosDisabledPreset_SilentlyAndWithNoError_R13B1()
    {
        var errors = new List<string>();
        _radio.ErrorOccurred += (_, e) => { lock (errors) errors.Add(e.Message); };

        // Constructed BEFORE the connect, the way the app composes it: the
        // wheel's Ready-arrival ensure is what has the enabled set in hand
        // before the operator's first press.
        var vm = new ModemViewModel(_modem, _session);
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        WaitFor(() => _session.Phase == SessionPhase.Ready, "session Ready over DEMO");
        WaitFor(() => _radio.State.ModemPresetPresence.State == RadioState.PresenceState.Completed,
            "the wheel's presence read to commit");
        Assert.DoesNotContain(DisabledPreset, _radio.State.ModemPresetPresence.Enabled);
        WaitFor(() => vm.CanSpinModem, "the wheel armed (confirmed mode + echo)");

        // Walk up to the preset BELOW the disabled one, one echo at a time —
        // the display moves only on the radio's answer.
        vm.ModemUpCommand.Execute(null);                    // OFF → 0
        WaitFor(() => vm.ModemDisplayText == "0: SER", "the preset 0 echo");
        vm.ModemUpCommand.Execute(null);                    // 0 → 1
        WaitFor(() => vm.ModemDisplayText == "1: T39", "the preset 1 echo");

        // THE PRESS UNDER TEST: 1 → (2 is disabled) → 3.
        vm.ModemUpCommand.Execute(null);
        WaitFor(() => vm.ModemDisplayText == "3: FW", "the preset 3 echo — 2 was skipped");

        lock (errors) Assert.Empty(errors);                 // no refusal ⇒ no toast

        // ANTI-VACUITY: the demo really does refuse preset 2, in words. The
        // skip is what prevented the refusal — not a fixture that never
        // refuses anything.
        _radio.Ssb.SelectModem(DisabledPreset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WaitFor(() => { lock (errors) return errors.Count > 0; }, "the demo's PRESET DISABLED refusal");
        lock (errors) Assert.Contains("disabled", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CLONE-FIELD ROUND 2 F10, end to end on the DEMO RADIO: at a <c>HOP&gt;</c>
    /// prompt the wheel's neighbour of OFF is preset 7, the DISABLED 7 and 8
    /// are SKIPPED, and selecting 9 answers <c>MODEM 9 DAT9</c> — the exact
    /// echo probe P5d2 captured
    /// (<c>bench/transcripts/p5d2-hop-modem-select-enabled-20260821-183248.jsonl</c>,
    /// label <c>select-9</c>). The demo seeds 7-9 disabled except 9, which is
    /// the state the real bench radio was found in.
    /// </summary>
    [Fact]
    public void TheChevron_AtAHopPrompt_SkipsTheDisabledHopPresets_AndSelectsNine_F10()
    {
        var errors = new List<string>();
        _radio.ErrorOccurred += (_, e) => { lock (errors) errors.Add(e.Message); };

        var vm = new ModemViewModel(_modem, _session);
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        WaitFor(() => _session.Phase == SessionPhase.Ready, "session Ready over DEMO");
        WaitFor(() => vm.CanSpinModem, "the wheel armed at SSB");

        // Into HOP. NOTHING ELSE: the wheel's own arrival landing must ask for
        // the new band's presence (audit round 1, MAJOR 1 — this test used to
        // call EnsurePresenceLoaded by hand here, which MASKED the defect the
        // whole test exists to catch).
        _radio.SelectHop();
        WaitFor(() => _radio.State.OperatingMode is { IsConfirmed: true, Value: OperatingMode.Hop },
            "the HOP prompt confirmed");
        WaitFor(() => vm.ModemDisplayText == "OFF", "the HOP SH block's MODEM OFF");
        WaitFor(() => _radio.State.ModemPresetPresence.Covers(OperatingMode.Hop),
            "a presence read committed for the HOP band, asked for by the wheel's own arrival landing");

        // The demo's found state, and the bench radio's: 9 enabled, 7 and 8 not.
        Assert.Equal([9], _radio.State.ModemPresetPresence.Enabled);

        // ONE press from OFF: 7 and 8 are disabled, so the target is 9 — and
        // the answer is P5d2's echo, verbatim through the K7 transform.
        vm.ModemUpCommand.Execute(null);
        WaitFor(() => vm.ModemDisplayText == "9: DAT9", "the MODEM 9 DAT9 echo");
        lock (errors) Assert.Empty(errors);         // nothing refused ⇒ no toast

        // ANTI-VACUITY, both directions. The demo really does refuse a DISABLED
        // hop preset in the captured words…
        _radio.Ssb.SelectModem("7");
        WaitFor(() => { lock (errors) return errors.Count > 0; }, "the demo's PRESET DISABLED refusal");
        lock (errors) Assert.Contains("disabled", errors[0], StringComparison.OrdinalIgnoreCase);

        // …and the display did NOT move on it (I-7: nothing lights that the
        // radio has not reported — `PRESET DISABLED` changes no state).
        Assert.Equal("9: DAT9", vm.ModemDisplayText);
    }

    public void Dispose()
    {
        _session.Dispose();
        _radio.Dispose();
        _transport.Dispose();
    }
}
