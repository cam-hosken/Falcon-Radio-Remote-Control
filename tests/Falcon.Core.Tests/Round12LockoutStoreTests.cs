using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// CLONE ROUND 12 §3 — the OPERATOR LOCKOUT read store, the FORCE WAKEUP
/// session latch, and the two §9 Core wire-truth rows (the compression re-read
/// and the tune-terminal re-poll).
///
/// <para>Every fixture line below is VERBATIM from
/// <c>bench/transcripts/r11-lockouts-20260818-194311.jsonl</c> and
/// <c>bench/transcripts/r12-p1-20260818-222442.jsonl</c> — the two live
/// captures of the <c>PROGRAM</c>/<c>SELECT</c> reports. Nothing here is
/// patterned or derived.</para>
/// </summary>
public class Round12LockoutStoreTests : RadioTestBase
{
    // ---- VERBATIM captures ----------------------------------------------

    private const string ProgramSsbHeader = ">>SSB_Programmable_Parameters";
    private const string ProgramHopHeader = ">>HOP_Programmable_Parameters";
    private const string ProgramEamHeader = ">>EAM_Programmable_Parameters";
    private const string SelectSsbHeader = ">>SSB_Selectable_Parameters";
    private const string SelectHopHeader = ">>HOP_Selectable_Parameters";
    private const string SelectEamHeader = ">>EAM_Selectable_Parameters";

    /// <summary>The whole PROGRAM report, all-LOCK, exactly as the radio
    /// printed it post-ZERO on 2026-08-18.</summary>
    private static readonly string[] ProgramReportAllLock =
    [
        ProgramSsbHeader,
        "PROGRAM CHAN LOCK", "PROGRAM FILL LOCK", "PROGRAM CFIG LOCK",
        "PROGRAM DATA LOCK", "PROGRAM KEYS LOCK",
        ProgramHopHeader,
        "PROGRAM NET LOCK", "PROGRAM EXCLUDE LOCK", "PROGRAM TX_POWER LOCK",
        "PROGRAM DATA LOCK",
        ProgramEamHeader,
        "PROGRAM ADDRESS LOCK", "PROGRAM CHGROUP LOCK", "PROGRAM CFIG LOCK",
        "PROGRAM LQA LOCK",
    ];

    /// <summary>The whole SELECT report, all-LOCK, same capture.</summary>
    private static readonly string[] SelectReportAllLock =
    [
        SelectSsbHeader,
        "SELECT DATA LOCK", "SELECT KEY LOCK", "SELECT MODE LOCK",
        "SELECT TMP_CHAN LOCK", "SELECT BFO LOCK",
        SelectHopHeader,
        "SELECT DATA LOCK", "SELECT KEY LOCK",
        SelectEamHeader,
        "SELECT DATA LOCK", "SELECT KEY LOCK",
    ];

    private void InjectAll(IEnumerable<string> lines)
    {
        foreach (var line in lines) Transport.InjectLine(line);
    }

    private void InjectFullReport() { InjectAll(ProgramReportAllLock); InjectAll(SelectReportAllLock); }

    private static void WaitForTimeout() => Thread.Sleep(300);

    private LockState StateOf(LockoutFamily family, LockoutSection section, string item)
    {
        var row = Radio.State.Lockouts.Rows.Single(
            r => r.Family == family && r.Section == section && r.Item == item);
        return row.State;
    }

    // ====================================================================
    // A. The CLOSED 22-item inventory (invariant 2)
    // ====================================================================

    [Fact]
    public void TheInventory_IsExactlyTheTwentyTwoCapturedRows()
    {
        Assert.Equal(22, LockoutInventory.Count);
        Assert.Equal(LockoutInventory.Count, LockoutInventory.All.Count);

        // 13 PROGRAM + 9 SELECT, split exactly as the two reports print them.
        Assert.Equal(13, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Program));
        Assert.Equal(9, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Select));

        // …and the set itself, spelled out — a twenty-third row has to be
        // written HERE before it can exist anywhere.
        Assert.Equal(
            [
                "Program/Ssb/CHAN", "Program/Ssb/FILL", "Program/Ssb/CFIG",
                "Program/Ssb/DATA", "Program/Ssb/KEYS",
                "Program/Hop/NET", "Program/Hop/EXCLUDE", "Program/Hop/TX_POWER",
                "Program/Hop/DATA",
                "Program/Eam/ADDRESS", "Program/Eam/CHGROUP", "Program/Eam/CFIG",
                "Program/Eam/LQA",
                "Select/Ssb/DATA", "Select/Ssb/KEY", "Select/Ssb/MODE",
                "Select/Ssb/TMP_CHAN", "Select/Ssb/BFO",
                "Select/Hop/DATA", "Select/Hop/KEY",
                "Select/Eam/DATA", "Select/Eam/KEY",
            ],
            LockoutInventory.All.Select(k => $"{k.Family}/{k.Section}/{k.Item}"));
    }

    [Fact]
    public void TheInventory_KeysBySectionToo_BecauseItemNamesRepeat()
    {
        // THE WHOLE REASON THE KEY IS A TRIPLE. If a future refactor keyed by
        // item alone, these repeats would collide silently — so the repetition
        // is asserted as a FACT of the radio, not left implicit.
        Assert.Equal(2, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Program && k.Item == "DATA"));
        Assert.Equal(2, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Program && k.Item == "CFIG"));
        Assert.Equal(3, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Select && k.Item == "DATA"));
        Assert.Equal(3, LockoutInventory.All.Count(k => k.Family == LockoutFamily.Select && k.Item == "KEY"));

        Assert.True(LockoutInventory.Contains(LockoutFamily.Program, LockoutSection.Hop, "DATA"));
        Assert.False(LockoutInventory.Contains(LockoutFamily.Program, LockoutSection.Eam, "DATA"));
        Assert.False(LockoutInventory.Contains(LockoutFamily.Select, LockoutSection.Hop, "MODE"));
    }

    // ====================================================================
    // B. THREE STATES: unread / in-flight / rows
    // ====================================================================

    [Fact]
    public void BeforeAnyRead_TheMirrorIsUnknown_NotEmptyRows()
    {
        // "Nothing read yet" and "the radio said nothing is locked" are
        // DIFFERENT facts, and a display that conflated them would call a fully
        // locked radio unlocked.
        Assert.Equal(LockoutReadState.Unknown, Radio.State.Lockouts.State);
        Assert.Empty(Radio.State.Lockouts.Rows);
    }

    [Fact]
    public void WhileAReadIsOnTheWire_TheMirrorSaysInFlight()
    {
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        Assert.Equal(LockoutReadState.InFlight, Radio.State.Lockouts.State);
        Assert.True(Radio.State.IsLockoutReadActive);
    }

    [Fact]
    public void AnAnsweredSentinel_CommitsAllTwentyTwoRowsAtomically()
    {
        ConnectReady();
        long id = Radio.Ssb.QueryLockouts();
        InjectFullReport();

        // Nothing is published until the sentinel: a half-arrived table must
        // never be readable as the answer.
        Assert.Equal(LockoutReadState.InFlight, Radio.State.Lockouts.State);

        AnswerSentinel();

        Assert.Equal(LockoutReadState.Completed, Radio.State.Lockouts.State);
        Assert.Equal(22, Radio.State.Lockouts.Rows.Count);
        Assert.All(Radio.State.Lockouts.Rows, r => Assert.Equal(LockState.Lock, r.State));
        Assert.Equal(new AleReadCompletion(id, true), Radio.State.LastLockoutRead);
    }

    [Fact]
    public void TheRowsAreKeyedByFamilySectionAndItem_NotByItemAlone()
    {
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        // The captured MIXED read: one row moved, twenty-one did not. Both
        // PROGRAM/DATA rows are present and must not overwrite each other.
        InjectAll(
        [
            ProgramSsbHeader, "PROGRAM CHAN UNLOCK", "PROGRAM FILL UNLOCK",
            "PROGRAM CFIG UNLOCK", "PROGRAM DATA LOCK", "PROGRAM KEYS UNLOCK",
            ProgramHopHeader, "PROGRAM NET UNLOCK", "PROGRAM EXCLUDE UNLOCK",
            "PROGRAM TX_POWER UNLOCK", "PROGRAM DATA UNLOCK",
            ProgramEamHeader, "PROGRAM ADDRESS UNLOCK", "PROGRAM CHGROUP UNLOCK",
            "PROGRAM CFIG UNLOCK", "PROGRAM LQA UNLOCK",
        ]);
        InjectAll(SelectReportAllLock);
        AnswerSentinel();

        Assert.Equal(22, Radio.State.Lockouts.Rows.Count);
        Assert.Equal(LockState.Lock, StateOf(LockoutFamily.Program, LockoutSection.Ssb, "DATA"));
        Assert.Equal(LockState.Unlock, StateOf(LockoutFamily.Program, LockoutSection.Hop, "DATA"));
        Assert.Equal(LockState.Lock, StateOf(LockoutFamily.Select, LockoutSection.Ssb, "DATA"));
        Assert.Equal(LockState.Lock, StateOf(LockoutFamily.Select, LockoutSection.Hop, "DATA"));
        Assert.Equal(LockState.Lock, StateOf(LockoutFamily.Select, LockoutSection.Eam, "DATA"));
    }

    [Fact]
    public void AnUnansweredSentinel_KeepsThePriorMirrorExactly()
    {
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        InjectFullReport();
        AnswerSentinel();
        Assert.Equal(22, Radio.State.Lockouts.Rows.Count);

        // A second read whose listing is swallowed must NOT read as "no rows".
        Radio.Ssb.LockoutReadTimeoutMs = 80;
        long second = Radio.Ssb.QueryLockouts();
        Transport.InjectLine(ProgramSsbHeader);
        Transport.InjectLine("PROGRAM CHAN UNLOCK");
        WaitForTimeout();

        Assert.Equal(LockoutReadState.Completed, Radio.State.Lockouts.State);
        Assert.Equal(22, Radio.State.Lockouts.Rows.Count);
        Assert.All(Radio.State.Lockouts.Rows, r => Assert.Equal(LockState.Lock, r.State));
        Assert.Equal(new AleReadCompletion(second, false), Radio.State.LastLockoutRead);
    }

    // ====================================================================
    // C. Attribution: an echo outside a read window mirrors NOTHING
    // ====================================================================

    [Fact]
    public void ASetEchoOutsideAReadWindow_InvalidatesTheMirror_RatherThanGuessingItsSection()
    {
        // `PROGRAM DATA LOCK` is BYTE-IDENTICAL as a report row and as a set
        // echo — but the echo carries no section header, and DATA exists in
        // both SSB and HOP. Attributing it would invent a fact; leaving the
        // mirror as it was would show a value the radio no longer holds. So the
        // store goes back to UNREAD and the campaign re-reads.
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        InjectFullReport();
        AnswerSentinel();
        Assert.Equal(LockoutReadState.Completed, Radio.State.Lockouts.State);

        Transport.InjectLine("PROGRAM DATA UNLOCK");     // a bare set echo

        Assert.Equal(LockoutReadState.Unknown, Radio.State.Lockouts.State);
        Assert.Empty(Radio.State.Lockouts.Rows);
    }

    [Fact]
    public void AnAllEcho_InvalidatesTheMirror_BecauseItMovedEverything()
    {
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        InjectFullReport();
        AnswerSentinel();

        Assert.Equal(LockoutReadState.Completed, Radio.State.Lockouts.State);
        Transport.InjectLine("PROGRAM ALL UNLOCK");      // captured echo form

        Assert.Equal(LockoutReadState.Unknown, Radio.State.Lockouts.State);
    }

    [Fact]
    public void ARowOutsideTheClosedInventory_IsALoudFact_NotATwentyThirdRow()
    {
        ConnectReady();
        string? error = null;
        Radio.ErrorOccurred += (_, e) => error = e.Message;

        Radio.Ssb.QueryLockouts();
        Transport.InjectLine(ProgramSsbHeader);
        Transport.InjectLine("PROGRAM CHAN LOCK");
        Transport.InjectLine("PROGRAM WIDGET LOCK");     // not in the inventory
        AnswerSentinel();

        Assert.Single(Radio.State.Lockouts.Rows);
        Assert.NotNull(error);
        Assert.Contains("WIDGET", error);
    }

    [Fact]
    public void AReconnect_ClearsTheMirror()
    {
        ConnectReady();
        Radio.Ssb.QueryLockouts();
        InjectFullReport();
        AnswerSentinel();
        Assert.Equal(22, Radio.State.Lockouts.Rows.Count);

        Radio.Disconnect();
        ConnectReady();

        Assert.Equal(LockoutReadState.Unknown, Radio.State.Lockouts.State);
        Assert.Empty(Radio.State.Lockouts.Rows);
    }

    [Fact]
    public void TheZeroizeBoundary_ClearsTheLockoutMirror()
    {
        // ZERO resets every item to LOCK (captured twice: r11-lockouts and
        // r12-p1). The mirror must not keep showing the pre-wipe table — and
        // it must not FABRICATE the all-LOCK answer either: it goes UNREAD, and
        // the campaign's own re-read is what publishes the truth.
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;
        Radio.Ssb.QueryLockouts();
        InjectFullReport();
        AnswerSentinel();
        Assert.Equal(LockoutReadState.Completed, Radio.State.Lockouts.State);

        Radio.Ssb.ZeroizeRadio();
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");

        Assert.True(Radio.ZeroizeSettled);
        Assert.Equal(LockoutReadState.Unknown, Radio.State.Lockouts.State);
        Assert.Empty(Radio.State.Lockouts.Rows);
    }

    // ====================================================================
    // D. FORCE WAKEUP — the bounded session latch (§9 C3)
    // ====================================================================

    [Fact]
    public void ForceWakeup_IsUnconfirmedUntilTheRadioSaysEnabled()
    {
        Assert.False(Radio.State.ForceWakeup.IsConfirmed);
    }

    [Fact]
    public void TheEnabledLine_ConfirmsTheLatch()
    {
        ConnectReady();
        Transport.InjectLine("FORCE WAKEUP ENABLED");    // verbatim capture
        Assert.True(Radio.State.ForceWakeup.IsConfirmed);
        Assert.Equal(EnabledDisabled.Enabled, Radio.State.ForceWakeup.Value);
    }

    [Fact]
    public void TheDisableSend_MarksItUnconfirmed_NeverConfirmedDisabled()
    {
        // The radio answers NOTHING to FORCE_W DIS (re-confirmed 2026-08-18,
        // P-2 step e), so "disabled" can never be a confirmed value. Claiming
        // it would be the app inventing a report.
        ConnectReady();
        Transport.InjectLine("FORCE WAKEUP ENABLED");
        Assert.True(Radio.State.ForceWakeup.IsConfirmed);

        Radio.Ssb.SetForceWakeup(EnabledDisabled.Disabled);

        Assert.False(Radio.State.ForceWakeup.IsConfirmed);
        Assert.Equal(["FORCE_W DIS"], Transport.SentLines);
    }

    [Fact]
    public void AReconnect_ClearsTheLatch()
    {
        ConnectReady();
        Transport.InjectLine("FORCE WAKEUP ENABLED");
        Assert.True(Radio.State.ForceWakeup.IsConfirmed);

        Radio.Disconnect();
        ConnectReady();

        Assert.False(Radio.State.ForceWakeup.IsConfirmed);
    }

    [Fact]
    public void TheLatchRaisesItsOwnProperty()
    {
        ConnectReady();
        var seen = new List<RadioProperty>();
        Radio.StateChanged += (_, e) => seen.Add(e.PropertyChanged);

        Transport.InjectLine("FORCE WAKEUP ENABLED");
        Assert.Contains(RadioProperty.ForceWakeup, seen);

        seen.Clear();
        Radio.Ssb.SetForceWakeup(EnabledDisabled.Disabled);
        Assert.Contains(RadioProperty.ForceWakeup, seen);
    }

    // ====================================================================
    // E. §9 B3 — the compression re-read trigger row (PRIMARY branch)
    // ====================================================================

    [Fact]
    public void AConfirmedModeChange_QueuesOneCompressionReadForTheNextSsbPrompt()
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Transport.InjectLine("MODE FM ");
        Assert.Empty(Transport.SentLines);      // queued, not sent mid-block

        Transport.InjectLine("SSB>");
        Assert.Contains("COM", Transport.SentLines);
    }

    [Fact]
    public void AConfirmedDvChange_QueuesTheSameRead()
    {
        // Captured 2026-08-18 (P-2 step g): `DV ON` outside USB/LSB silently
        // forces the modulation to USB and moves the bandwidth — a cascade
        // whose reach the app cannot see, so compression is re-read rather than
        // assumed unchanged.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Transport.InjectLine("DV ON ");
        Transport.InjectLine("SSB>");

        Assert.Contains("COM", Transport.SentLines);
    }

    [Fact]
    public void TheCompressionReadFiresOnce_AndNotDuringInit()
    {
        // During init the app is LEARNING values, not observing mutations —
        // the same exclusion every other trigger row carries.
        Connect();
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SSB>");
        Assert.DoesNotContain("COM", Transport.SentLines);

        AnswerSentinel();
        AnswerSentinel();
        Transport.ClearSent();

        Transport.InjectLine("MODE LSB");
        Transport.InjectLine("SSB>");
        Assert.Equal(1, Transport.SentLines.Count(l => l == "COM"));

        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        Assert.DoesNotContain("COM", Transport.SentLines);      // one-shot
    }

    // ====================================================================
    // F. §9 B1 — the tune terminal arms the SHARED re-poll flag
    // ====================================================================

    [Theory]
    [InlineData(" TUNE COMPLETE  ")]
    [InlineData("TUNE MARGINAL")]
    [InlineData("TUNE FAULT")]
    public void EveryTuneTerminal_ArmsTheSharedRePoll_SoTheKeylineComesBack(string terminal)
    {
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("KEY ON");
        Assert.True(Radio.State.Keyline.IsConfirmed);

        Transport.InjectLine(" TUNING COUPLER ");
        Assert.False(Radio.State.Keyline.IsConfirmed);   // the tune says nothing about it
        Transport.ClearSent();

        Transport.InjectLine(terminal);
        Assert.Empty(Transport.SentLines);               // queued for the prompt

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines);       // ONE re-read, not three
    }

    [Fact]
    public void TwoProducers_CoalesceIntoOneSh()
    {
        // The point of reusing rows (b)/(c)'s flag rather than adding a
        // parallel one: a hop-net select and a tune terminal in the same window
        // produce ONE `SH`, by construction.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("NET  00");
        Transport.InjectLine("NET  01");                 // trigger row (c)
        Transport.InjectLine(" TUNE COMPLETE  ");        // §9 B1
        Transport.ClearSent();

        Transport.InjectLine("SSB>");
        Assert.Equal(1, Transport.SentLines.Count(l => l == "SH"));
    }

    // ====================================================================
    // G. §9 A1 / B2 — what the OPERATOR is told
    // ====================================================================

    [Fact]
    public void PresetDisabled_ReachesTheOperatorInWords_NotAsAnUnrecognizedLine()
    {
        // The bench symptom WAS the app's own "Unrecognized message" banner —
        // which is how the spelling got captured. R13: the operator's sentence
        // carries no radio token.
        ConnectReady();
        string? error = null;
        Radio.ErrorOccurred += (_, e) => error = e.Message;

        Transport.InjectLine("PRESET DISABLED");

        Assert.NotNull(error);
        Assert.DoesNotContain("Unrecognized", error, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESET DISABLED", error, StringComparison.Ordinal);
        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyTheExactErrorBanner_RaisesTheGenericRejection()
    {
        ConnectReady();
        string? error = null;
        Radio.ErrorOccurred += (_, e) => error = e.Message;

        Transport.InjectLine("** ERROR **");

        // R13 (audit round 1, finding 8): TOKEN-FREE. The sentence used to
        // quote "** ERROR **" back at the operator; the raw line still reaches
        // the Console feed, which is where the evidence belongs.
        Assert.Equal("The radio rejected that command.", error);
        Assert.DoesNotContain("**", error);
        Assert.DoesNotContain("ERROR", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherBanner_ReachesTheOperatorWithItsOwnPayload()
    {
        // §9 B2's claim, unchanged: rebadging a `**` banner as a syntax reject
        // tells the operator the radio refused a command it did not refuse —
        // and throws away the only content the line had.
        //
        // THE EXAMPLE MOVED (plan-clone-field-round2.md F3, decision A-5). This
        // used to inject `*** ZEROIZE COMPLETE ***`, which was the captured
        // banner available when round 12 was written. That line is now
        // recognized and raises NOTHING — a wipe the operator authorised
        // announcing its own success is not a fault, and toasting it as one is
        // exactly what the field reported on 2026-08-21. The two zeroize banners
        // and the RX-only refusal are the arm's named lines; EVERY OTHER `**`
        // banner still carries its own payload, which is what this pins.
        // (The zeroize half is pinned in
        // StateMirrorTests.ZeroizeBanners_RaiseNoError_WhileEveryOtherBannerStillDoes.)
        ConnectReady();
        string? error = null;
        Radio.ErrorOccurred += (_, e) => error = e.Message;

        Transport.InjectLine("*** SELF TEST FAILED ***");

        Assert.NotNull(error);
        Assert.Contains("SELF TEST FAILED", error, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATuneTerminalOutsideSsb_IsStillArmed_AndFiresAtTheNextSsbPrompt()
    {
        // THE ARBITRATION, as re-cut by audit round 2. The arm is
        // UNCONDITIONAL — a tune that terminates at a HOP or ALE prompt
        // unconfirms the keyline just as surely as one at SSB, and §9 B1 says
        // the keyline re-confirms "regardless of outcome". It simply cannot
        // fire THERE: SSB-domain commands are rejected at ALE>/HOP> prompts.
        ConnectReady();
        Transport.InjectLine("HOP>");
        Transport.ClearSent();

        Transport.InjectLine(" TUNE COMPLETE  ");
        Transport.InjectLine("HOP>");
        Assert.Empty(Transport.SentLines);      // not at a HOP prompt…

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines);   // …but not lost, either
    }

    [Fact]
    public void AnySh_SatisfiesThePendingRePoll_WhoeverSendsIt()
    {
        // THE COALESCING RULE. The pending flag asks one question — "re-read
        // the block" — and an `SH` IS that read, whoever sent it. Here the
        // caller sends one before the prompt comes round, so Core's own
        // compensation dissolves rather than duplicating it.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine(" TUNE COMPLETE  ");   // armed
        Transport.ClearSent();

        Radio.Show();                              // somebody else asks first
        Assert.Equal(["SH"], Transport.SentLines);

        Transport.InjectLine("SSB>");
        Assert.Equal(["SH"], Transport.SentLines); // Core adds nothing
    }

    [Fact]
    public void TheChannelSelectPair_AlsoSatisfiesIt_WithNoPerCallerRule()
    {
        // The sweep's other named site: the channel select sends `CH nn` then
        // `SH`. Nothing in that path knows about the re-poll flag — the rule
        // lives at the ONE send site, so every SH-issuing caller is covered by
        // construction rather than by a list.
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("NET  00");
        Transport.InjectLine("NET  01");            // trigger row (c) arms
        Transport.ClearSent();

        Radio.Ssb.SelectChannel(7);
        Radio.Show();
        Assert.Equal(["CH 7", "SH"], Transport.SentLines);

        Transport.InjectLine("SSB>");
        Assert.Equal(["CH 7", "SH"], Transport.SentLines);
    }

    [Fact]
    public void ANonShSend_DoesNotSatisfyIt()
    {
        // Anti-vacuity for the rule above: only an `SH` answers the question,
        // so a busy session full of other traffic must not dissolve the
        // re-poll. (A rule that cleared on ANY send would pass every pin above
        // and quietly delete the whole trigger table.)
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine(" TUNE COMPLETE  ");   // armed
        Transport.ClearSent();

        Radio.QueryBatteryState();
        Radio.Ssb.QueryChannel();
        Transport.InjectLine("SSB>");

        Assert.Equal(["BAT ST", "CH", "SH"], Transport.SentLines);
    }
}
