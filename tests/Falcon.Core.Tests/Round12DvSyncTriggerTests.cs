using Falcon.Core.Modes;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// CLONE ROUND 12 P4 — the DV STATE SYNC (trigger row (f)), the pending
/// re-poll's SPLIT BY DOMAIN, and the arm/satisfy race serialization.
///
/// <para>The radio facts these pins encode come from the graduated D1 matrix
/// (docs/protocol.md "Digital voice — the interaction matrix", captured
/// r12-p2) and probe R4: <c>DV ON</c> SILENTLY forces <c>USB</c> from AME, CW
/// or FM — the echo carries NO <c>MODE</c> line at all — forces analog squelch
/// ON, and moves the bandwidth in EVERY modulation; <c>DV OFF</c> reverses all
/// of it; and modulation leaving USB/LSB silently auto-SUSPENDS DV while
/// returning silently auto-RESTORES it.</para>
///
/// <para>SYNC, NOT GATE: nothing here blocks anything. The app's only job is
/// never to DISPLAY state the radio has silently moved — so a change of either
/// value unconfirms what the other silently moved and queues ONE <c>SH</c> for
/// the next SSB prompt.</para>
/// </summary>
public class Round12DvSyncTriggerTests : RadioTestBase
{
    /// <summary>Park a confirmed USB/2.7/squelch-ON/DV-OFF mirror and drain
    /// every first-sight arm, ending with the sync window CLOSED and the sent
    /// log clear — the state an operator's radio is actually in.</summary>
    private void SettleMirror()
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("DV OFF");
        Transport.InjectLine("SSB>");                     // the first-sight re-poll goes out
        AnswerSyncBlock("USB", "2.7", "OFF", "ON");       // …and its block closes the window
        Transport.ClearSent();
    }

    /// <summary>Answer a compensating <c>SH</c> with the four lines P4 cares
    /// about, in the SSB block's OWN order (MODE and BAND ahead of DV, SQUELCH
    /// last — the ordering both the display unconfirm and the FM-cycle
    /// compensation memory have to survive), and close it with its prompt.</summary>
    private void AnswerSyncBlock(string mode, string band, string dv, string squelch)
    {
        Transport.InjectLine("MODE " + mode);
        Transport.InjectLine("BAND " + band);
        Transport.InjectLine("DV " + dv);
        Transport.InjectLine("SQUELCH " + squelch);
        Transport.InjectLine("SSB>");
    }

    private int ShCount => Transport.SentLines.Count(l => l == "SH");

    /// <summary>Send a `ZERO` and drive it through the settle boundary, ending
    /// with a clear sent log — the state the trigger table is supposed to come
    /// back to.</summary>
    private void Wipe()
    {
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;
        Radio.Ssb.ZeroizeRadio();
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");
        Assert.True(Radio.ZeroizeSettled);
        Transport.ClearSent();
    }

    // ====================================================================
    // GREEN — the two producers
    // ====================================================================

    [Fact]
    public void AChangedDv_UnconfirmsTheSilentlyForcedValues_AndQueuesOneSh()
    {
        SettleMirror();

        Transport.InjectLine("DV ON");

        // The TRANSIENT reads UNCONFIRMED, never stale: "never display
        // silently-moved state" applies BEFORE the re-read lands too.
        Assert.False(Radio.State.ModulationMode.IsConfirmed);
        Assert.False(Radio.State.Bandwidth.IsConfirmed);
        Assert.False(Radio.State.AnalogSquelch.IsConfirmed);
        // …and DV itself is the REPORT, not a casualty of it.
        Assert.True(Radio.State.DigitalVoice.IsConfirmed);
        Assert.Equal(OnOff.On, Radio.State.DigitalVoice.Value);
        Assert.Empty(Transport.SentLines);      // queued for the prompt, not sent now

        Transport.InjectLine("SSB>");
        // ONE `SH` — coalesced with §9 B3's own `COM` for the same DV line.
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Theory]
    [InlineData("MODE CW")]     // the DEPARTURE direction: DV auto-suspends
    [InlineData("MODE USB")]    // the RETURN direction: DV auto-RESTORES
    public void AChangedMode_UnconfirmsDv_AndQueuesOneSh_InBothDirections(string modeLine)
    {
        SettleMirror();
        // Park the mirror on the OTHER side so both InlineData rows are a real
        // change, and DV confirmed ON so the unconfirm is observable.
        var parked = modeLine == "MODE USB" ? "CW" : "USB";
        Transport.InjectLine("MODE " + parked);
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock(parked, "2.7", "ON", "ON");
        Transport.ClearSent();
        Assert.True(Radio.State.DigitalVoice.IsConfirmed);

        Transport.InjectLine(modeLine);

        // NO DV-ON CONDITION: the R4 auto-RESTORE means a mode change can flip
        // DV either way, so every changed MODE unconfirms it.
        Assert.False(Radio.State.DigitalVoice.IsConfirmed);
        Assert.True(Radio.State.ModulationMode.IsConfirmed);   // the line's own report stands

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void ADvChangeAtAHopPrompt_FiresItsOneSh_AtTheNextSsbPrompt()
    {
        // SSB-domain commands are rejected at ALE>/HOP> prompts (session-18),
        // so the arm has to WAIT — and then still fire exactly once.
        SettleMirror();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("DV ON");
        Transport.ClearSent();

        Transport.InjectLine("HOP>");
        Assert.Empty(Transport.SentLines);
        Transport.InjectLine("ALE>");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void ADvAndAModeChangeInOneWindow_CoalesceIntoOneSh()
    {
        SettleMirror();

        Transport.InjectLine("MODE CW");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");

        Assert.Equal(1, ShCount);
    }

    // ====================================================================
    // RED — what must NOT arm
    // ====================================================================

    [Fact]
    public void DuringInit_NeitherProducerArms_AndAfterReadyBothDo()
    {
        // The connect ritual's own answers are the app LEARNING the values, not
        // the radio mutating them — the same exclusion every other trigger row
        // carries.
        Connect();
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("DV ON");
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Transport.ClearSent();

        Transport.InjectLine("SSB>");
        Assert.Empty(Transport.SentLines);           // nothing was owed

        // ANTI-VACUITY: the very same lines, now that the session is Ready.
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SSB>");
        Assert.Equal(1, ShCount);
    }

    [Fact]
    public void TheCompensatingShBlock_ArmsNothing_EvenCarryingChangedModeAndDv()
    {
        // THE IN-FLIGHT SUPPRESSION, on its worst case. The changed-line guard
        // alone provably cannot deliver one SH: RadioState.Set counts a
        // post-unconfirm RE-confirm as Changed, and the block legitimately
        // reports the genuinely-changed MODE the DV toggle caused. So the
        // block's lines parse and re-confirm every mirror — and arm nothing.
        SettleMirror();

        Transport.InjectLine("DV ON");               // THE ONE TRIGGER
        Transport.InjectLine("SSB>");
        Assert.Equal(1, ShCount);                    // the compensating SH goes out

        // …and the block it answers, carrying a GENUINELY changed modulation
        // (USB→FM) and a genuinely changed DV (ON→OFF).
        AnswerSyncBlock("FM", "2.7", "OFF", "ON");

        // ANTI-VACUITY: those lines really did move the mirror.
        Assert.Equal(ModulationMode.Fm, Radio.State.ModulationMode.Value);
        Assert.Equal(OnOff.Off, Radio.State.DigitalVoice.Value);

        Assert.Equal(1, ShCount);                    // ONE trigger → ONE SH, total
        Transport.InjectLine("SSB>");
        Assert.Equal(1, ShCount);                    // …and nothing owed afterwards
    }

    [Fact]
    public void AMissedSuppressionWindow_StillArmsNothing_BecauseTheProducersKeepMemories()
    {
        // THE TERMINATION PIN — the reason both producers ask "did the radio
        // report a DIFFERENT value" rather than reading ParseResult.Changed.
        //
        // The producers unconfirm each other's mirror BY DESIGN, so every
        // re-confirm that follows is `Changed`. The in-flight window is what
        // makes the count exactly one; the memories are what make it terminate
        // AT ALL when the window is missed — and the window CAN be missed,
        // because SendLine enqueues and the prompt-gated writer may still be
        // holding commands ahead of Core's `SH`.
        //
        // Here that is reproduced exactly: an unrelated answer carrying a MODE
        // line arrives, with its own prompt, while the `SH` is still queued —
        // closing the window early. The block that follows then re-confirms DV
        // to the value it ALREADY reported. On `Changed` that arms a second
        // read (and, before the modulation memory existed, a third, and so on);
        // on the memory it arms nothing.
        SettleMirror();
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("USB", "3.0", "ON", "ON");
        Transport.ClearSent();

        Transport.InjectLine("MODE CW");             // THE ONE TRIGGER — unconfirms DV
        Assert.False(Radio.State.DigitalVoice.IsConfirmed);
        Transport.InjectLine("SSB>");                // the `SH` is queued here
        Assert.Equal(1, ShCount);

        // …and an answer queued AHEAD of it lands first, closing the window.
        Transport.InjectLine("MODE CW");
        Transport.InjectLine("SSB>");

        // Now the block itself, with the window already shut. Its `DV ON` line
        // re-confirms a mirror the MODE producer unconfirmed — `Changed`, but
        // NOT a value the radio moved.
        Transport.InjectLine("MODE CW");
        Transport.InjectLine("BAND 1.0");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("SSB>");

        Assert.True(Radio.State.DigitalVoice.IsConfirmed);   // anti-vacuity: it DID re-confirm
        Assert.Equal(1, ShCount);
        Transport.InjectLine("SSB>");
        Assert.Equal(1, ShCount);                            // …and it stays settled
    }

    [Fact]
    public void AMissedSuppressionWindow_TerminatesOnTheModeSideToo()
    {
        // The MODE half of the pin above — same construction, mirrored. A DV
        // change unconfirms the modulation; with the window shut early, the
        // block's `MODE` line re-confirms the value the radio already reported.
        // `Changed` says yes, the memory says no, and only the memory is right.
        SettleMirror();

        Transport.InjectLine("DV ON");               // THE ONE TRIGGER
        Assert.False(Radio.State.ModulationMode.IsConfirmed);
        Transport.InjectLine("SSB>");                // the `SH` is queued here
        Assert.Equal(1, ShCount);

        Transport.InjectLine("DV ON");               // an answer ahead of it, closing early
        Transport.InjectLine("SSB>");

        Transport.InjectLine("MODE USB");            // the block: the SAME USB, re-confirmed
        Transport.InjectLine("BAND 3.0");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("SSB>");

        Assert.True(Radio.State.ModulationMode.IsConfirmed);
        Assert.Equal(1, ShCount);
        Transport.InjectLine("SSB>");
        Assert.Equal(1, ShCount);
    }

    // ====================================================================
    // THE SYNC WINDOW's three negative sequences (audit round 1, findings
    // 1–3). These are the pins that make "suppression" a mechanism rather
    // than a boolean: a window COLLECTS, and the decision is taken at its
    // close, once, on the whole window's evidence.
    // ====================================================================

    [Fact]
    public void AGenuineDvReportLandingInsideAnOrdinaryRead_IsNotSwallowed_AndArmsACorrection()
    {
        // FINDING 1. A bare suppression flag threw away real news: an ordinary
        // `SH` (nobody's trigger row — a settings read, an operator refresh)
        // was in flight, the radio reported `DV ON` mid-block, and because the
        // producer was suppressed the prompt closed with the block's own stale
        // `MODE FM` still CONFIRMED and no correcting read owed. The radio had
        // silently forced USB.
        SettleMirror();
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("FM", "2.7", "OFF", "ON");
        Transport.ClearSent();
        Assert.Equal(ModulationMode.Fm, Radio.State.ModulationMode.Value);

        Radio.Show();                                // an ORDINARY read
        Assert.Equal(1, ShCount);

        // Its block — and, before the block terminates, a REAL `DV ON`.
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("BAND 2.7");
        Transport.InjectLine("DV OFF");              // the block's own DV line
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("DV ON");               // …and the async report
        Transport.InjectLine("SSB>");

        // The window's evidence: DV was reported TWICE, so one of them is not
        // the block's, and the memory ended somewhere else than it started.
        // The stale modulation goes unconfirmed and a correcting read goes out.
        Assert.False(Radio.State.ModulationMode.IsConfirmed);
        Assert.False(Radio.State.Bandwidth.IsConfirmed);
        Assert.Equal(2, ShCount);
    }

    [Fact]
    public void ATruncatedRead_ClosesItsWindowAnyway_SoTheNextChangeIsStillHeard()
    {
        // FINDING 2, the mirror image of the loop the window exists to prevent.
        // A close that waited for evidence of the block LATCHED OPEN when the
        // radio truncated or swallowed one (R6 says it does): the next genuine
        // DV change was then consumed with nothing armed, and if no further
        // MODE/DV line ever arrived, forever.
        SettleMirror();

        Radio.Show();                                // a read whose block never comes
        Assert.Equal(1, ShCount);
        Transport.InjectLine("SSB>");                // …just a prompt
        Transport.ClearSent();

        Transport.InjectLine("DV ON");               // the next GENUINE change

        Assert.False(Radio.State.ModulationMode.IsConfirmed);   // heard, not swallowed
        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void AModeExcursion_RetiresAnAbandonedWindow_SoADvChangeInsideItIsStillHeard()
    {
        // AUDIT ROUND 2, THE MAJOR — reproduced exactly. A window left standing
        // by a truncated read used to survive an entire mode-family excursion:
        // the genuine `DV ON` that landed at the `ALE>` prompt was credited to
        // an obsolete head and suppressed, and the return to `SSB>` then closed
        // that window with ONE DV report and armed NOTHING — leaving `MODE FM`
        // confirmed while the radio had silently forced USB.
        SettleMirror();
        Transport.InjectLine("MODE FM");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("FM", "2.7", "OFF", "ON");
        Transport.ClearSent();
        Assert.Equal(ModulationMode.Fm, Radio.State.ModulationMode.Value);

        Radio.Show();                                // an ordinary read…
        Assert.Equal(1, ShCount);
        Transport.InjectLine("ALE>");                // …abandoned by a mode switch

        Transport.ClearSent();
        Transport.InjectLine("DV ON");               // the GENUINE change, at ALE>
        Assert.False(Radio.State.ModulationMode.IsConfirmed);   // heard, not swallowed

        Transport.InjectLine("ALE>");
        Assert.Empty(Transport.SentLines);           // nothing fires outside SSB
        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void AModeExcursion_OverAWindowSomethingMovedIn_RetiresItByARMING()
    {
        // THE CONSERVATIVE HALF of retirement. A value moved while the window
        // stood, and the block that would have accounted for it is abandoned —
        // so the one-report-vs-two discrimination is simply unavailable. It
        // arms: a spurious extra read on this rare path costs one `SH`, while a
        // lost change costs a display that is silently wrong until something
        // unrelated re-reads.
        SettleMirror();

        Radio.Show();
        Assert.Equal(1, ShCount);
        Transport.InjectLine("MODE FM");             // moved, INSIDE the window
        Transport.InjectLine("HOP>");                // …and then abandoned
        Transport.ClearSent();

        Transport.InjectLine("HOP>");
        Assert.Empty(Transport.SentLines);           // not at a HOP prompt…
        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);   // …but not lost either
    }

    [Fact]
    public void AModeExcursion_OverAQuietWindow_RetiresItWithoutArming()
    {
        // The BENIGN half, and the anti-vacuity for the conservative rule: an
        // excursion that abandons a window in which NOTHING moved must retire
        // it silently. Retirement is not an excuse to re-read.
        SettleMirror();

        Radio.Show();
        Assert.Equal(1, ShCount);
        Transport.InjectLine("HOP>");                // abandoned, but nothing moved
        Transport.ClearSent();

        Transport.InjectLine("HOP>");
        Transport.InjectLine("SSB>");
        Assert.Empty(Transport.SentLines);           // no spurious read
    }

    [Fact]
    public void TwoQueuedReads_CorrelateIndependently_AndCostNoThirdRead()
    {
        // FINDING 3. `SendLine` ENQUEUES and the writer is prompt-gated, so two
        // reads can be outstanding at once. Sharing ONE boolean, the first
        // block's prompt released the second block's suppression — and the
        // second block, carrying a genuinely different modulation, then armed a
        // read nobody needed.
        SettleMirror();

        Radio.Show();
        Radio.Show();
        Assert.Equal(2, ShCount);

        AnswerSyncBlock("USB", "2.7", "OFF", "ON");   // block 1
        AnswerSyncBlock("FM", "2.7", "OFF", "ON");    // block 2 — a real change
        Assert.Equal(2, ShCount);

        // ANTI-VACUITY: the second block really did move the mirror, and the
        // windows really did both close (a third read is not merely deferred).
        Assert.Equal(ModulationMode.Fm, Radio.State.ModulationMode.Value);
        Transport.InjectLine("SSB>");
        Assert.Equal(2, ShCount);
    }

    [Fact]
    public void UnchangedDvAndModeLines_NeverArm()
    {
        SettleMirror();
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("USB", "3.0", "ON", "ON");   // the window closes here
        Transport.ClearSent();

        // The SAME values, reported again outside any window.
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsAPendingDvSyncArm()
    {
        // A sync re-poll armed before the wipe must never fire INTO the settle
        // window — the radio is busy wiping RAM, and after it every mirror is
        // reset anyway. ResetTriggerFlags is part of the boundary.
        SettleMirror();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;
        Transport.InjectLine("DV ON");               // armed
        Transport.ClearSent();

        Radio.Ssb.ZeroizeRadio();
        Transport.ClearSent();
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");                // settles AND would have fired it

        Assert.True(Radio.ZeroizeSettled);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsTheModulationMemory_SoTheFirstReportAfterIsFresh()
    {
        // The boundary must reset the MEMORIES, not just the arms. A memory
        // that survived the wipe would classify the radio's first post-wipe
        // report of a value it also held BEFORE the wipe as "unchanged" — and
        // arm nothing — when it is first sight in a session whose mirror was
        // emptied. The old ZERO pin asserted arm-CLEARING only, and every
        // memory reset could be deleted without failing it.
        SettleMirror();                              // memory: MODE USB
        Wipe();

        Transport.InjectLine("MODE USB");            // the SAME value, post-wipe
        Transport.InjectLine("SSB>");

        // The `COM` is row (e), which reads ParseResult.Changed and therefore
        // fires either way — asserting the pair EXACTLY is what makes the `SH`
        // (row (f), which reads the MEMORY) the discriminating half.
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsTheDvMemory_SoTheFirstReportAfterIsFresh()
    {
        SettleMirror();                              // memory: DV OFF
        Wipe();

        Transport.InjectLine("DV OFF");              // the SAME value, post-wipe
        Transport.InjectLine("SSB>");

        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsTheWindowQueue_SoTheFirstReportAfterIsNotDeferred()
    {
        // AUDIT ROUND 2, THE MINOR. The boundary clears the window queue and
        // nothing pinned it — a stale pre-wipe window would defer the first
        // post-wipe report, which is first sight in a session whose mirror was
        // emptied. TWO reads are outstanding on purpose: the settle prompt is
        // itself an `SSB>` and would close one window on its own, so a single
        // one cannot tell the boundary's clear from the ordinary close.
        SettleMirror();
        Radio.Show();
        Radio.Show();
        Assert.Equal(2, ShCount);

        Wipe();

        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "COM"], Transport.SentLines);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsTheAnalogSquelchMemory_SoTheFmCycleCannotArmFromIt()
    {
        // The third memory, and the one whose survival would be silent: row
        // (d) would arm an OFF→ON squelch cycle off a modulation report that
        // nothing in this session has squelch-qualified.
        SettleMirror();                              // memory: SQUELCH ON
        Wipe();

        Transport.InjectLine("MODE FM");

        Assert.False(Radio.IsFmSquelchCyclePending);

        // ANTI-VACUITY: a squelch report in the NEW session still arms it.
        Transport.InjectLine("SQUELCH ON");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("MODE FM");
        Assert.True(Radio.IsFmSquelchCyclePending);
    }

    // ====================================================================
    // The DISPLAY-SCOPED unconfirm, and the compensation memory behind it
    // ====================================================================

    [Fact]
    public void DvOffRestoringFmWithSquelchOn_StillArmsTheFmCycle_DespiteTheDisplayUnconfirm()
    {
        // THE COMPENSATION-MEMORY PIN. P4's unconfirm is DISPLAY-scoped: the
        // FM-squelch cycle keeps its own last-REPORTED analog squelch, because
        // the SH block orders MODE before SQUELCH — so `MODE FM` arrives while
        // the display mirror is still unconfirmed, and a row (d) that read the
        // mirror would silently stop arming.
        SettleMirror();
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("USB", "3.0", "ON", "ON");
        Transport.ClearSent();
        Assert.False(Radio.IsFmSquelchCyclePending);

        Transport.InjectLine("DV OFF");              // the operator disengages DV

        // ANTI-VACUITY: the DISPLAY really is unconfirmed at this moment —
        // which is exactly the state row (d) used to refuse to arm from.
        Assert.False(Radio.State.AnalogSquelch.IsConfirmed);

        Transport.InjectLine("SSB>");                // Core's re-read goes out
        Transport.InjectLine("MODE FM");             // the block, MODE ahead of SQUELCH

        Assert.True(Radio.IsFmSquelchCyclePending);
    }

    [Fact]
    public void ARadioThatNeverReportedSquelch_StillArmsNothing()
    {
        // The other half of the memory: it is a MEMORY, never a default. The
        // old app was burned arming this off enum defaults.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("MODE FM");

        Assert.False(Radio.IsFmSquelchCyclePending);
    }

    [Fact]
    public void ARemirroredModulation_DoesNotFabricateTheFmCycleTrigger()
    {
        // Row (d) asks "did the radio report a DIFFERENT modulation", and P4's
        // display unconfirm must not answer yes for it: a re-confirm of the
        // value already standing is not a modulation change.
        SettleMirror();
        Transport.InjectLine("MODE FM");             // arms the cycle (squelch ON)
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("FM", "2.7", "OFF", "ON");
        Assert.True(Radio.IsFmSquelchCyclePending);
        Transport.ClearSent();

        // A DV change unconfirms the modulation for DISPLAY; the SH block then
        // re-confirms the SAME FM. Row (d)'s USB/LSB branch must not fire, and
        // no `SQ OFF` may go out.
        Transport.InjectLine("DV ON");
        Transport.InjectLine("SSB>");
        AnswerSyncBlock("FM", "3.0", "ON", "ON");

        Assert.True(Radio.IsFmSquelchCyclePending);
        Assert.DoesNotContain("SQ OFF", Transport.SentLines);

        // ANTI-VACUITY: a REAL return to USB still runs the cycle.
        Transport.InjectLine("MODE USB");
        Assert.Contains("SQ OFF", Transport.SentLines);
    }

    // ====================================================================
    // THE FLAG SPLIT (plan §6 P4, from the P1 round-3 recorded consequence)
    // ====================================================================

    [Fact]
    public void AHopPromptSh_SatisfiesTheKeylineHalf_ButNotTheChannelDomain()
    {
        // The recorded consequence, repaired. A `HOP>`-prompt `SH` answers the
        // HOP block — which carries the KEYLINE but NOT the SSB channel domain —
        // so a hop-net select still re-reads the channel values at the next
        // SSB prompt instead of leaving them unconfirmed-but-honest.
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("NET  00");
        Transport.InjectLine("NET  01");             // row (c): the channel half
        Transport.InjectLine(" TUNE COMPLETE  ");    // §9 B1: the keyline half
        Transport.ClearSent();

        Radio.Show();                                 // the HOP pane's own read
        Assert.Equal(["SH"], Transport.SentLines);
        Transport.InjectLine("HOP>");
        Assert.Equal(["SH"], Transport.SentLines);    // nothing fires at a HOP prompt

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH", "SH"], Transport.SentLines);   // the channel half survived
    }

    [Fact]
    public void AHopPromptSh_FullySatisfiesATuneOnlyArm_SoTheCountStaysAtOne()
    {
        // ANTI-VACUITY for the split: the keyline half really is satisfied by
        // ANY `SH`. Splitting must not turn every tune into two reads — that
        // was audit round 1's finding and it stays dead.
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Transport.ClearSent();

        Radio.Show();
        Transport.InjectLine("HOP>");
        Transport.InjectLine("SSB>");

        Assert.Equal(["SH"], Transport.SentLines);
    }

    [Fact]
    public void AnSsbPromptSh_SatisfiesBothHalves()
    {
        // The other side of the same rule: in SSB context one read answers
        // everything, so nothing is owed afterwards.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("NET  00");
        Transport.InjectLine("NET  01");
        Transport.InjectLine(" TUNE COMPLETE  ");
        Transport.ClearSent();

        Radio.Show();
        Transport.InjectLine("SSB>");

        Assert.Equal(["SH"], Transport.SentLines);
    }

    // ====================================================================
    // THE ARM/SATISFY RACE (P1 round-3 audit MINOR, deferred to P4)
    // ====================================================================

    [Fact]
    public void AnShRacingTheArmsCommit_DoesNotResurrectTheClearedArm()
    {
        // THE SCHEDULING-HOOK PROBE the audit asked for. The arm used to read
        // the SH counter, then write the flag, with nothing holding the two
        // together: an `SH` issued in between cleared a flag the arm then put
        // straight back, costing one redundant read. The hook fires in exactly
        // that window, from ANOTHER thread, and the versioned commit sees it.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Radio.ArmRaceHook = () =>
        {
            Radio.ArmRaceHook = null;                 // exactly once
            var racer = new Thread(() => Radio.Show());
            racer.Start();
            racer.Join();
        };

        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Equal(["SH"], Transport.SentLines);    // the racer's read, and only it

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines);    // the arm did NOT come back
    }

    [Fact]
    public void WithoutTheRace_TheSameSequenceDoesOweItsOwnSh()
    {
        // ANTI-VACUITY for the pin above: with nothing racing, the identical
        // sequence arms and fires. (A commit that simply never armed would pass
        // the race pin and delete the trigger table.)
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Transport.InjectLine(" TUNE COMPLETE  ");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines);
    }
}
