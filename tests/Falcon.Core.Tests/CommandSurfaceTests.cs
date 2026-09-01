using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// The v1 command surface (plan §1): every builder produces the
/// bench-confirmed wire form; client-side validation rejects what the radio
/// would silently ignore or reject; and nothing outside the v1 scope exists.
/// </summary>
public class CommandSurfaceTests : RadioTestBase
{
    /// <summary>
    /// AUDIT ROUND 2 (clone-field round 2, MAJOR 1) — <b>connected AND at the
    /// SSB prompt.</b> A modem preset command now REFUSES while the mode is
    /// unconfirmed: which presets exist is a fact about the PROMPT, and the
    /// app does not guess it. Every pin in this file that touches a preset
    /// already MEANT "at SSB" — its expected bytes are the 0-6 band's — so the
    /// prompt is confirmed once here instead of at a dozen call sites. The
    /// prompt line sends nothing; it only confirms the mirror.
    ///
    /// <para>The one pin that needs the UNCONFIRMED window
    /// (<see cref="EveryModemPresetCommand_RefusesWhileTheModeIsUNCONFIRMED_AndSendsNothing"/>)
    /// calls <c>base.ConnectReady()</c> and says why.</para></summary>
    private new void ConnectReady()
    {
        base.ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
    }

    // ---- General ------------------------------------------------------------

    [Fact]
    public void ModeAndPower_SendDocumentedForms()
    {
        ConnectReady();
        Radio.SelectSsb();
        Radio.SelectAle();
        Radio.SelectHop();
        Radio.SetPowerLevel(PowerLevel.Low);
        Radio.SetPowerLevel(PowerLevel.Medium);
        Radio.SetPowerLevel(PowerLevel.High);
        Radio.QueryBatteryState();
        Radio.QueryPortConfig();
        Radio.QueryTime();
        Radio.SetRemoteEcho(OnOff.On);

        Assert.Equal(
            ["SS", "ALE", "HO", "POW LOW", "POW MED", "POW HI", "BAT ST", "PORT_R", "TI", "PORT_R ECHO ON"],
            Transport.SentLines);
    }

    [Fact]
    public void CommandsBeforeConnect_AreNotSent()
    {
        Radio.SetPowerLevel(PowerLevel.High);
        Radio.Ssb.Retune();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void RawCommand_PassesThroughVerbatim()
    {
        ConnectReady();
        Radio.RawCommand("HELP MORE");
        Assert.Equal(["HELP MORE"], Transport.SentLines);
    }

    // ---- SSB ------------------------------------------------------------------

    [Fact]
    public void SsbVfo_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SetFrequency("14234500");
        Radio.Ssb.SetRxFrequency("01600000");
        Radio.Ssb.SetTxFrequency("29999999");
        Radio.Ssb.IncrementFrequency();
        Radio.Ssb.DecrementFrequency();
        Radio.Ssb.SetStep(FrequencyStep.OneKHz);
        Radio.Ssb.QueryStep();

        Assert.Equal(
            ["FR 14234500", "RXF 01600000", "TXF 29999999", "INC", "DEC", "STEP 00001000", "STEP"],
            Transport.SentLines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("160000")]        // 6 digits
    [InlineData("1600000O")]      // letter O
    [InlineData("00000000")]      // masked-textbox artifact
    // F5 (plan-clone-field-round2.md, D3): the ceiling MOVED from an
    // unmeasured 29 999 999 to the MEASURED 59 999 999 — probe P2, transcript
    // bench/transcripts/p2-freq-range-20260821-175802.jsonl. `30000000` used to
    // sit on this list as "above band"; the real radio stores it (and read it
    // back), so pinning it as invalid was pinning a defect. The rejects below
    // are the two values the radio itself answered `** ERROR **` to.
    [InlineData("01599999")]      // below band — P2: ** ERROR **, value unchanged
    [InlineData("60000000")]      // above band — P2: ** ERROR **, value unchanged
    [InlineData("99999999")]      // P2: ** ERROR **, value unchanged
    public void BadFrequencies_RejectedBeforeReachingTheRadio(string? frequency)
    {
        ConnectReady();
        Assert.ThrowsAny<ArgumentException>(() => Radio.Ssb.SetFrequency(frequency!));
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>F5 — the values probe P2 proved the radio ACCEPTS now reach the
    /// wire. `30000000` and `51500000` were both refused app-side until this
    /// round; the second is the shape that made the field clone drop six
    /// channels (plan-clone-field-round2.md §1). Transcript:
    /// bench/transcripts/p2-freq-range-20260821-175802.jsonl.</summary>
    [Theory]
    [InlineData("30000000")]
    [InlineData("45000000")]
    [InlineData("51500000")]
    [InlineData("59999999")]
    [InlineData("01600000")]
    public void ProbedAcceptedFrequencies_ReachTheWire(string frequency)
    {
        ConnectReady();
        Radio.Ssb.SetRxFrequency(frequency);
        Radio.Ssb.SetTxFrequency(frequency);
        Assert.Equal(["RXF " + frequency, "TXF " + frequency], Transport.SentLines);
    }

    /// <summary>ONE definition of the band bound (F5, D3): Core's constants are
    /// the P2 numbers, and the refusal message names the real range.</summary>
    [Fact]
    public void TheFrequencyBound_IsTheProbedWindow_AndTheRefusalNamesIt()
    {
        Assert.Equal(1_600_000, Wire.MinFrequencyHz);
        Assert.Equal(59_999_999, Wire.MaxFrequencyHz);

        ConnectReady();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetFrequency("60000000"));
        Assert.Contains("1.6–60 MHz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SsbSignal_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SetModulation(ModulationMode.Usb);
        Radio.Ssb.SetModulation(ModulationMode.Cw);
        Radio.Ssb.SetAgc(AgcSpeed.Slow);
        Radio.Ssb.SetBandwidth("2.7");
        Radio.Ssb.SetBandwidth(".35");      // normalized to the canonical form
        Radio.Ssb.QueryBandwidth();
        Radio.Ssb.QueryAgc();
        Radio.Ssb.SetRxOnly(YesNo.Yes);
        Radio.Ssb.SetRxOnly(YesNo.No);
        Radio.Ssb.Retune();

        Assert.Equal(
            ["MODE USB", "MODE CW", "AG SLOW", "BA 2.7", "BA 0.35", "BA", "AG", "RXON YES", "RXON NO", "RETU"],
            Transport.SentLines);
    }

    [Fact]
    public void UnknownBandwidth_Rejected()
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SetBandwidth("7.5"));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ChannelSelectAndDump_SendDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SelectChannel(7);
        Radio.Ssb.DisplayChannels(0, 99);
        Assert.Equal(["CH 7", "DI 0 99"], Transport.SentLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SelectChannel(100));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.DisplayChannels(5, 2));
    }

    [Fact]
    public void ChannelDump_ClearsThePreviousList()
    {
        ConnectReady();
        Transport.InjectLine("CH 00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Assert.Single(Radio.State.ChannelList);

        Radio.Ssb.DisplayAllChannels();
        Assert.Empty(Radio.State.ChannelList);      // repopulated by the dump lines
    }

    // ---- ALE -------------------------------------------------------------------

    [Fact]
    public void AleOperations_SendDocumentedForms()
    {
        ConnectReady();
        Radio.Ale.StartScan();
        Radio.Ale.Stop();
        Radio.Ale.Call("bob");
        Radio.Ale.Call("NET1", "05");
        Radio.Ale.Rank("bob");
        Radio.Ale.QueryRxMessages();
        Radio.Ale.QuerySelfAddresses();
        Radio.Ale.QueryIndividualAddresses();
        Radio.Ale.QueryNetAddresses();

        Assert.Equal(
            ["SCA", "ST", "CAL BOB", "CAL NET1 05", "RAN BOB", "RXMSG", "SLFAD", "INDAD", "NETAD"],
            Transport.SentLines);
    }

    [Fact]
    public void BroadcastCallsRideTheExistingArgumentSlots_NoNewSender()
    {
        // The broadcast round (P20/P20b, 2026-08-23): ANY and ALL are ORDINARY
        // addresses to Core — no special-casing, no new spelling of `CAL`.
        // The channel is REQUIRED for ANY (the radio refuses a bare one with
        // ` NO CHANS IN GRP `, P20) and OPTIONAL for ALL (`CAL ALL` picks its
        // own — `CALLING  ALL              CHANNEL: 29`, P20). Core does not
        // enforce that asymmetry: it is the caller's, because the radio's own
        // refusal is the honest answer to a bad one.
        ConnectReady();
        Radio.Ale.Call("ANY", "12");        // P20b: → CALLING  ANY  CHANNEL: 12
        Radio.Ale.Call("ALL");              // P20:  → CALLING  ALL  CHANNEL: 29
        Radio.Ale.Call("ALL", "12");        // P20b's `SE 9 ALL 12` twin form
        Radio.Ale.Call("any", "12");        // uppercased like every other address

        Assert.Equal(
            ["CAL ANY 12", "CAL ALL", "CAL ALL 12", "CAL ANY 12"],
            Transport.SentLines);
    }

    [Fact]
    public void SendAmd_ToABroadcastAddressOnAChannel_SendsSeNineAnyChannel()
    {
        // P20b: `SE 9 ANY 12` → `SENDING  ANY              CHANNEL: 12`. The
        // scratch-slot verify is unchanged — a broadcast AMD is still never
        // sent unverified.
        ConnectReady();
        Radio.Ale.SendAmd("BROADCAST PROBE P20B", "ANY", "12");

        Assert.Equal(
            ["TXMSG 9 BROADCAST PROBE P20B", "TXMSG", "BAT ST"], Transport.SentLines);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));

        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("  BROADCAST PROBE P20B ");     // the listing's own padding
        AnswerSentinel();

        Assert.Equal("SE 9 ANY 12", Transport.SentLines[^1]);
    }

    [Fact]
    public void SendAmd_ToAllWithNoChannel_SendsTheAutoForm()
    {
        // P20's `SE 9 ALL` — the radio picks the channel and announces it
        // (`SENDING  ALL              CHANNEL: 29`). This is the wire form
        // the ALL row's "Auto" choice will emit.
        ConnectReady();
        Radio.Ale.SendAmd("BROADCAST PROBE P20", "ALL");
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("  BROADCAST PROBE P20  ");
        AnswerSentinel();

        Assert.Equal("SE 9 ALL", Transport.SentLines[^1]);
    }

    [Fact]
    public void LqaScheduling_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ale.StartExchange("BOB", "01:00", "12:30");
        Radio.Ale.StartExchange("BOB");
        Radio.Ale.StopExchange("BOB");
        Radio.Ale.StartExchange("BOB", "23:59");          // range boundary: legal
        Radio.Ale.StartSounding("CAM", "02:00", null);
        Radio.Ale.StopSounding("CAM");

        Assert.Equal(
            ["EXCH STA BOB 01:00 12:30", "EXCH STA BOB", "EXCH STO BOB",
             "EXCH STA BOB 23:59", "SOU STA CAM 02:00", "SOU STO CAM"],
            Transport.SentLines);

        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange("BOB", "60"));
        // Round 10 §7: the self bound is 1-15 (PROVISIONAL), so the refusal
        // case is a SIXTEEN-character self, not the old seven-character one.
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding("SIXTEENCHARSELF!"));

        // Audit round 1, F3 — range, not just shape: EXCH/SOU answer NOTHING
        // (Stage 6 wire fact), so the client is the only defense. 24:00 and
        // 23:60 must never reach the wire.
        Transport.ClearSent();
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange("BOB", "24:00"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange("BOB", "23:60"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding("CAM", "99:99"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding("CAM", "01:00", "24:00"));
        Assert.Empty(Transport.SentLines);
    }

    // ---- AMD scratch-slot send (write + verify + SE) ------------------------------

    [Fact]
    public void SendAmd_WritesScratchSlot_VerifiesReadback_ThenSends()
    {
        ConnectReady();
        Radio.Ale.SendAmd("MEET AT GRID 0900", "BOB");

        Assert.Equal(["TXMSG 9 MEET AT GRID 0900", "TXMSG", "BAT ST"], Transport.SentLines);
        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));

        // Radio lists the stored slot (verbatim listing shape), then the
        // sentinel answer proves the listing is complete:
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("MEET AT GRID 0900 ");
        AnswerSentinel();

        Assert.Equal("SE 9 BOB", Transport.SentLines[^1]);
    }

    [Fact]
    public void SendAmd_WithChannel_AppendsIt()
    {
        ConnectReady();
        Radio.Ale.SendAmd("HELLO", "BOB", "03");
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("HELLO");
        AnswerSentinel();
        Assert.Equal("SE 9 BOB 03", Transport.SentLines[^1]);
    }

    [Fact]
    public void SendAmd_ReadbackMismatch_RaisesErrorAndNeverSends()
    {
        // The radio silently ignores some bad writes (documented behavior
        // class) — an unverified send must never fire.
        ConnectReady();
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Radio.Ale.SendAmd("MEET AT GRID 0900", "BOB");
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("STALE OLD MESSAGE");
        AnswerSentinel();

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.Contains(errors, m => m.Contains("AMD not sent"));
    }

    [Fact]
    public void SendAmd_ReadbackTimeout_RaisesErrorAndNeverSends()
    {
        ConnectReady();
        Radio.Ale.AmdVerifyTimeoutMs = 80;
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);

        Radio.Ale.SendAmd("HELLO", "BOB");
        Thread.Sleep(300);

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.Contains(errors, m => m.Contains("AMD not sent"));
    }

    // Stage 6: the optional outcome callback (the Messages page's sent-log
    // consumer). Exactly-once, both verdicts, and failure still raises the
    // error event — the callback is an addition, not a replacement.

    [Fact]
    public void SendAmd_OutcomeCallback_TrueAfterVerifiedSe()
    {
        ConnectReady();
        var outcomes = new List<(bool Ok, string? Reason)>();
        Radio.Ale.SendAmd("TEST MSG STAGE6", "AAA", null, (ok, reason) => outcomes.Add((ok, reason)));

        Assert.Empty(outcomes);                    // nothing until the verify completes
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("TEST MSG STAGE6");
        AnswerSentinel();

        Assert.Equal("SE 9 AAA", Transport.SentLines[^1]);
        Assert.Equal([(true, (string?)null)], outcomes);
    }

    [Fact]
    public void SendAmd_OutcomeCallback_FalseWithReasonOnMismatch_ErrorStillRaised()
    {
        ConnectReady();
        var errors = new List<string>();
        Radio.ErrorOccurred += (_, e) => errors.Add(e.Message);
        var outcomes = new List<(bool Ok, string? Reason)>();

        Radio.Ale.SendAmd("TEST MSG STAGE6", "AAA", null, (ok, reason) => outcomes.Add((ok, reason)));
        Transport.InjectLine("TXMSG 09");
        Transport.InjectLine("STALE OLD MESSAGE");
        AnswerSentinel();

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.Single(outcomes);
        Assert.False(outcomes[0].Ok);
        Assert.Contains("read back", outcomes[0].Reason);
        Assert.Contains(errors, m => m.Contains("AMD not sent"));
    }

    [Fact]
    public void SendAmd_OutcomeCallback_FalseOnTimeout()
    {
        ConnectReady();
        Radio.Ale.AmdVerifyTimeoutMs = 80;
        var outcomes = new List<(bool Ok, string? Reason)>();

        Radio.Ale.SendAmd("HELLO", "AAA", null, (ok, reason) => outcomes.Add((ok, reason)));
        Thread.Sleep(300);

        Assert.DoesNotContain(Transport.SentLines, l => l.StartsWith("SE"));
        Assert.Single(outcomes);
        Assert.False(outcomes[0].Ok);
        Assert.Contains("did not answer", outcomes[0].Reason);
    }

    [Fact]
    public void SendAmd_ValidatesInput()
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd(new string('X', 91), "BOB"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd("", "BOB"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd("HI", "WAY TOO LONG ADDR"));
        Assert.Empty(Transport.SentLines);
    }

    // ---- Station-list refresh (accumulate + commit) ---------------------------------

    /// <summary>Publish the last-confirmed book across all three kinds so
    /// the accumulate-and-commit pins below can assert FULL book contents
    /// (audit round 1, F2: asserting only [0]=="OLD" let an
    /// uncommitted-publication mutation survive — the audit-6 D4 class).</summary>
    private void PublishConfirmedBook()
    {
        Transport.InjectLine("SLFAD OLD               CHGROUP 01");
        Transport.InjectLine("INDAD OLDI              CHGROUP 01   ASSOC SELF OLD");
        Transport.InjectLine("NETAD OLDN              CHGROUP 01   ASSOC SELF OLD");
    }

    private void AssertBookIsExactlyTheOldOne()
    {
        Assert.Equal(["OLD"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(["OLDI"], Radio.State.Ale.IndividualAddresses.Select(a => a.Address));
        Assert.Equal(["OLDN"], Radio.State.Ale.NetAddresses.Select(a => a.Address));
    }

    [Fact]
    public void RefreshStationList_MidRefresh_PublishedBookIsExactlyTheOldBook()
    {
        ConnectReady();
        PublishConfirmedBook();
        AssertBookIsExactlyTheOldOne();

        Radio.Ale.RefreshStationList();

        // Mid-refresh, with NEW rows accumulating: the published book must
        // be EXACTLY the old one — not old-plus-leaking-new (F2 mutation:
        // publishing the uncommitted accumulation adds NEW alongside OLD).
        Transport.InjectLine("SLFAD NEW               CHGROUP 01");
        Transport.InjectLine("INDAD NEWI              CHGROUP 01   ASSOC SELF NEW");
        AssertBookIsExactlyTheOldOne();

        AnswerSentinel();      // sentinel proves the listing completed → commit
        Assert.Equal(["NEW"], Radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(["NEWI"], Radio.State.Ale.IndividualAddresses.Select(a => a.Address));
        Assert.Empty(Radio.State.Ale.NetAddresses);      // honest: no NETAD answered
    }

    [Fact]
    public void RefreshStationList_DiscardsOnTimeout_KeepsTheConfirmedBook()
    {
        // A swallowed listing query (documented quirk) must not leave an
        // empty book that matches nothing.
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        PublishConfirmedBook();

        Radio.Ale.RefreshStationList();
        Thread.Sleep(300);      // sentinel times out, nothing answered

        AssertBookIsExactlyTheOldOne();
    }

    [Fact]
    public void RefreshStationList_PartialRowsThenTimeout_PublishedBookStillExactlyTheConfirmedOne()
    {
        // Audit round 1, F2(b): partial listing rows arrive, then the
        // closing sentinel is swallowed — the accumulation must be
        // discarded WHOLE; the published book stays byte-for-byte the last
        // radio-confirmed one (audit-6 D4: a half-book matches nothing).
        ConnectReady();
        Radio.Ale.RefreshTimeoutMs = 80;
        PublishConfirmedBook();

        Radio.Ale.RefreshStationList();
        Transport.InjectLine("SLFAD NEW               CHGROUP 01");
        Transport.InjectLine("INDAD NEWI              CHGROUP 01   ASSOC SELF NEW");
        Thread.Sleep(300);      // sentinel swallowed → timeout → discard

        AssertBookIsExactlyTheOldOne();
    }

    [Fact]
    public void RefreshStationList_CanPublishAnEmptyList()
    {
        // An honestly-empty radio commits an empty book (accumulation empty
        // at commit) — distinct from the discard path above.
        ConnectReady();
        Transport.InjectLine("SLFAD OLD               CHGROUP 01");

        Radio.Ale.RefreshStationList();
        AnswerSentinel();

        Assert.Empty(Radio.State.Ale.SelfAddresses);
    }

    // ---- HOP -----------------------------------------------------------------------

    [Fact]
    public void HopOperations_SendDocumentedForms()
    {
        ConnectReady();
        Radio.Hop.QueryAllNets();
        Radio.Hop.QueryNet(3);
        Radio.Hop.SelectNet(0);
        Radio.Hop.Sync();
        Radio.Hop.SetTimeOfDay(new DateTime(2026, 8, 2, 20, 37, 12));

        Assert.Equal(
            ["DIS", "DIS 3", "NET 0", "SY", "TIME 20:37:12", "DAT 08/02/26", "DAY SUNDAY"],
            Transport.SentLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.SelectNet(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.QueryNet(-1));
    }

    // ---- Command-surface scope guard (audit round 1, F4; Phase R amended) -------
    // Phase R (plan-gui-rejigger.md round 4) flipped the old "no builders at
    // all" pins for fill editing, net programming, crypto and diagnostics:
    // builders now EXIST (backend in), whitelisted below, and the guard
    // moved to "NO app-layer file references them" (GuiOutScopeGuardTests —
    // the RawCommand-guard pattern). Still guarded two ways here:
    // (a) the ENTIRE public method surface is asserted against an explicit
    // whitelist — any new public method fails the suite until consciously
    // whitelisted; (b) every whitelisted SAFE command-sending method is
    // exercised once and the wire is swept for forbidden tokens; the
    // gated/GUI-out builders are excluded from the sweep and their outputs
    // are pinned as prefix-catchable separately (the Stage 11 pattern).

    private static readonly IReadOnlyDictionary<Type, string[]> PublicSurfaceWhitelist =
        new Dictionary<Type, string[]>
        {
            [typeof(Prc138Radio)] =
            [
                // "Ping" twice: two real overloads (R8-review MAJOR 1 — the
                // whitelist counts overloads now, one entry per overload).
                "Connect", "Disconnect", "Dispose", "Ping", "Ping",
                // ROUND 13 D2 (repair 3): the clean-disconnect notifier. It is
                // whitelisted as DATA, not as a command — it sends NOTHING and
                // cannot: it exists precisely because the port is already dead.
                // The wire sweep below sees no builder in it, and the
                // behaviour pin (NotifyTransportClosed_...) holds that.
                "NotifyTransportClosed",
                // D20 (plan-clone-write-structural.md §2): the campaign-start
                // ledger re-baseline. Whitelisted as DATA on the same footing as
                // NotifyTransportClosed — it re-baselines three counters under
                // the ping lock and sends NOTHING; it does not even read the
                // transport. The wire sweep below sees no builder in it, and the
                // arithmetic is pinned in SentinelLedgerResetReplayTests.
                "ResetSentinelLedger",
                "SelectMode", "SelectSsb", "SelectAle", "SelectHop",
                "Show", "QueryBatteryState", "SetPowerLevel", "QueryPowerLevel",
                "QueryPortConfig", "QueryTime", "SetRemoteEcho", "RawCommand",
                // Stage 11: the ONE whitelisted session-ending command (plan
                // §7 decision 3 — the guarded baud wizard). It WAS token-gated
                // like Zeroize was in the old repo; round 10 §5 removed that
                // gate (the GUI owns confirmation for this sender now), so the
                // wire-sweep carve below and the signature pin are what contain
                // it. See both.
                "SetRemoteBaud",
                // Phase R device settings (answered in every mode).
                "SetBacklightFunction", "SetBacklightIntensity", "SetContrast",
                // UI-tweaks round-4 AC: the device READ set (R4-Q1 mining —
                // docs/protocol.md round-4 provisional subsection). Reads only,
                // safe surface, swept below.
                "QueryBacklightFunction", "QueryBacklightIntensity", "QueryContrast",
                // Phase R crypto (E1: backend in, GUI out — source-scan
                // guarded; valid in all modes per protocol.md COMSEC).
                "SetEncryption", "SetEncryptionKey", "ClearEncryptionKey",
                "SelectEncryptionKey",
            ],
            [typeof(Falcon.Core.Modes.SsbController)] =
            [
                "SetFrequency", "SetRxFrequency", "SetTxFrequency",
                "IncrementFrequency", "DecrementFrequency", "QueryFrequency",
                "QueryStep", "SetStep", "SetModulation", "QueryModulation",
                "SetBandwidth", "QueryBandwidth", "SetAgc", "QueryAgc",
                "SetRxOnly",
                "SelectChannel", "QueryChannel", "DisplayChannels",
                "DisplayAllChannels", "Retune",
                // Round 11 §8: the explicit channel-mirror clear (sends
                // nothing) that the keyed-upsert change made a gesture rather
                // than a side effect of every DI.
                "ForgetStoredChannels",
                // Phase R SSB settings vocabulary.
                "SetSquelch", "SetDigitalVoice", "SetDigitalSquelch",
                "SetSquelchLevel", "SetFmSquelch", "SetFmSquelchType",
                "SetFmTone", "SetFmDeviation", "SetBfoOffset", "SetCwOffset",
                "SetCompression", "SetRfGain", "SetRxPreamp", "SetAvs",
                "SetAntenna", "SetInternalCoupler", "SetOneKilowattPa",
                "SetRetransmit", "SetPrePostFilter", "SetPrePostRxAntenna",
                "SetPrePostScanRate", "SetBeep",
                "SetRwas", "SetForceWakeup", "SetRwasKey", "SetUnkeyMask",
                "SelectModem", "ModemOff",
                // UI-tweaks round-8 EE (X7 — owner: modem family out of the
                // scope guard): the one-line preset programmer behind the
                // Radio settings editor.
                "ProgramModemPreset",
                // CLONE-FIELD ROUND 2 F9/F11 (decision A-9): the `HOP>`
                // preset's OWN builder. A separate method rather than an
                // overload or a flag, so the SSB line's bytes stay pinned
                // untouched — the two shapes share a command name and nothing
                // else (no TYPE, a three-value baud set, the state token on its
                // own line LAST).
                "ProgramHopModemPreset",
                // UI-tweaks round 11 §8: the preset READ SEAM replacing the
                // old clear-then-bulk QueryModemPresets — the targeted field
                // read, the seven-read batch, and the sentinel-scoped presence
                // operation. All three are safe surface (reads + BAT ST).
                "QueryModemPreset", "RefreshModemPresets", "QueryModemPresetPresence",
                // UI-tweaks round-3 V7: the OLD-APP-DERIVED per-setting query
                // set (plan-ui-tweaks-round3.md; docs/protocol.md provisional
                // section). Reads only — safe surface, swept below. FORCE_W
                // and RWAS_KEY have NO query by design (the old app documents
                // why: silent-disable and ** ERROR ** respectively).
                "QueryFmSquelchType", "QueryFmTone", "QueryFmDeviation",
                "QueryCwOffset", "QueryAvs", "QueryRxPreamp", "QueryRfGain",
                "QueryAntenna", "QueryInternalCoupler", "QueryOneKilowattPa",
                "QueryRetransmit", "QueryPrePostFilter",
                "QueryPrePostRxAntenna", "QueryPrePostScanRate",
                "QueryRwas", "QueryUnkeyMask", "QueryBeep",
                // CLONE ROUND 12 §9 B3 (PRIMARY branch): the EIGHTEENTH
                // settings read. Bare `COM` answers `COMPRESS ON` — captured
                // 2026-08-18, bench/transcripts/r12-p2-* step c — so the
                // compression mirror has a read path for the first time.
                // Safe surface, swept below.
                "QueryCompression",
                // CLONE ROUND 12 §3 — X12, the OPERATOR LOCKOUT family. The
                // round's ONLY new command family (invariant 1). The READ is a
                // sentinel-bracketed operation over the two GLOBAL reports; the
                // SET emits `PROGRAM|SELECT <ITEM> LOCK|UNLOCK` and leaves the
                // PROMPT POSITIONING to the orchestrator (the radio scopes a
                // set to the active prompt's mode section — round-12 P-1).
                // Both are excluded from the wire sweep: "PROGRAM"/"SELECT" are
                // forbidden prefixes for every OTHER sender, and their exact
                // forms are pinned separately (the SetRemoteBaud treatment).
                "QueryLockouts", "SetLockout",
                // CLONE ROUND 12 §3 leg 2 — X13, ZEROIZE. Irreversible, and
                // whitelist-narrowed to the clone campaign in the app layer.
                // "ZERO" is a forbidden prefix, so it is likewise excluded from
                // the sweep and pinned exactly.
                "ZeroizeRadio",
                // Phase R gated hazards (token-gated: keying/TE transmit).
                "SetKeyline", "SelfTest", "VswrTest",
                // Phase R diagnostics (E5: GUI out, source-scan guarded).
                "QueryFirmwareVersions",
            ],
            [typeof(Falcon.Core.Modes.AleController)] =
            [
                "StartScan", "Stop", "Call", "SendAmd", "QueryRxMessages",
                "Rank", "StartExchange", "StopExchange", "StartSounding",
                "StopSounding", "QuerySelfAddresses", "QueryIndividualAddresses",
                "QueryNetAddresses",
                // X8 (plan-ale-programming.md §4.1). RefreshStationList's
                // signature CHANGED (void → long: it returns the read id its
                // completion record carries) — the deliberate, whitelisted
                // one; the two group reads and the bare sentinel barrier are
                // new, and all four are safe surface (reads + BAT ST).
                // There is deliberately NO bare-CHG sender: every group read
                // goes through one of these two, so every one has a commit
                // barrier.
                "RefreshStationList", "RequestChannelGroup",
                "RefreshChannelGroups", "Synchronize",
                // UI-tweaks round 11 §8: the two NEW sentinel-scoped read
                // stores — one net's membership (targeted NETAD) and the LQA
                // schedule queue (bare EXCH).
                "ReadNetMembers", "ReadLqaSchedules",
                // UI-tweaks round 11 §9A (X10): the STORED TX MESSAGE STORE —
                // the ONLY new builders this round admits anywhere (§10,
                // invariant 1), and not a new command FAMILY: SendAmd already
                // writes TXMSG 9 and the parser already mirrors the listing.
                // The clone must carry the whole store because ERASE spares it.
                // ForgetStoredMessages sends NOTHING (the ForgetStoredChannels
                // gesture): the mirror is upsert-only, so a re-listing after a
                // slot DELETE would still show the deleted row.
                "QueryTxMessages", "StoreTxMessage", "DeleteTxMessage",
                // Stage 9 closed 2026-08-24 (linked-amd round): the received
                // store's write side - DEL provisional, TXMSG-family precedent.
                "DeleteRxMessage", "ForgetReceivedMessages",
                "ForgetStoredMessages",
                // Phase R ALE settings (bench-confirmed query+set).
                "SetAllCall", "SetAnyCall", "SetAmdDisplay", "SetKeyToCall",
                "SetListenBeforeTx", "SetRadioSilence", "SetMaxScanChannels",
                "SetLinkTimeout", "SetTuneTime",
                // Phase R fill editing (E2: backend in, GUI out — this
                // consciously REPLACES the Stage 1 "no builders exist" pin,
                // plan-gui-rejigger.md round 4).
                "SetSelfAddress", "SetIndividualAddress", "SetNetAddress",
                "AddNetMember", "DeleteAddress", "AddScanChannel",
                "DeleteScanChannel", "EraseAllAddresses",
            ],
            [typeof(Falcon.Core.Modes.HopController)] =
            [
                "QueryAllNets", "QueryNet", "SelectNet", "Sync", "SetTimeOfDay",
                // Phase R net programming (E3: backend in, GUI out — the
                // 2026-08-02 select-only pin now applies to the UI only,
                // plan-gui-rejigger.md round 4).
                "SetNetId", "SetHopType", "SetNarrowbandHopset",
                "SetWidebandHopset", "DeleteHopset", "AddHopListFrequencies",
                "DeleteHopListFrequency", "QueryHopList", "SetExcludeBand",
                "QueryExcludeBands", "DeleteExcludeBand", "DeleteAllExcludeBands",
                "GenerateHopset",
            ],
        };

    [Fact]
    public void PublicSurface_IsExactlyTheWhitelistedV1Surface()
    {
        // R8-review MAJOR 1 (the surviving mutation): the comparison counts
        // OVERLOADS — a Distinct() here let a second overload of an approved
        // sender (e.g. a SetCompression(string) passthrough) join the
        // surface unseen, which is exactly the evasion route the X7 sweep
        // relaxation must not open. A legitimately overloaded name appears
        // in the whitelist once per overload.
        foreach (var (type, allowed) in PublicSurfaceWhitelist)
        {
            var actual = type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)        // property/event accessors are data access, not commands
                .Select(m => m.Name)
                .Order()
                .ToArray();

            Assert.Equal(allowed.Order().ToArray(), actual);
        }
    }

    /// <summary>
    /// ROUND 13 D2 (repair 3) — the BEHAVIOUR behind the new whitelist entry.
    /// Whitelisting a member says only that someone looked at it; this says
    /// what it does, which is the part that must not drift.
    ///
    /// <para>Three claims: (1) it SENDS NOTHING — the port is already gone,
    /// and a notifier that wrote to it would be the very hang this repair
    /// removes; (2) it brings Core's state down to <c>Disconnected</c> and
    /// clears the sentinels nothing can answer any more; (3) it is idempotent,
    /// so a doubled teardown cannot storm subscribers.</para>
    /// </summary>
    [Fact]
    public void NotifyTransportClosed_SendsNothing_AndBringsCoreDownCleanly()
    {
        ConnectReady();
        Radio.Ping(() => { });                       // a sentinel nothing will answer
        Assert.Equal(1, Radio.PendingPingCount);
        Transport.ClearSent();

        int connectionEvents = 0;
        Radio.StateChanged += (_, e) =>
        {
            if (e.PropertyChanged is RadioProperty.ConnectionState or RadioProperty.ConnectionOpen)
                connectionEvents++;
        };

        Radio.NotifyTransportClosed();

        Assert.Empty(Transport.SentLines);           // (1) not one byte
        Assert.Equal(ConnectionState.Disconnected, Radio.Connection);
        Assert.Equal(0, Radio.PendingPingCount);     // (2) owed callbacks released
        Assert.Equal(2, connectionEvents);           // ConnectionState + ConnectionOpen, once each

        Radio.NotifyTransportClosed();               // (3) idempotent
        Assert.Empty(Transport.SentLines);
        Assert.Equal(2, connectionEvents);
        Assert.Equal(ConnectionState.Disconnected, Radio.Connection);
    }

    // ---- Scope-guard hardening (Stage 1 round-2 NIT, closed at Stage 2) --------
    // The instance-method whitelist above filters IsSpecialName and instance
    // members only, leaving two evasion routes for a command-sending member:
    // a public STATIC method, and a property ACCESSOR (a setter that sends is
    // a command in property clothing). Both surfaces are now pinned to
    // explicit allowances — empty for statics, data-only for accessors.

    /// <summary>Public static methods allowed per guarded type. Currently
    /// none anywhere: a static cannot reach the transport without smuggling
    /// an instance, so any appearance is a design smell to whitelist
    /// consciously or reject.</summary>
    private static readonly IReadOnlyDictionary<Type, string[]> PublicStaticWhitelist =
        new Dictionary<Type, string[]>
        {
            [typeof(Prc138Radio)] = [],
            [typeof(Falcon.Core.Modes.SsbController)] = [],
            [typeof(Falcon.Core.Modes.AleController)] = [],
            [typeof(Falcon.Core.Modes.HopController)] = [],
        };

    [Fact]
    public void PublicStaticSurface_IsExactlyTheWhitelistedSet_CurrentlyEmpty()
    {
        foreach (var (type, allowed) in PublicStaticWhitelist)
        {
            var actual = type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .Distinct()
                .Order()
                .ToArray();

            Assert.Equal(allowed.Distinct().Order().ToArray(), actual);
        }
    }

    /// <summary>Every public property accessor (get_/set_, instance AND
    /// static) allowed per guarded type. All entries are pure data access —
    /// state/controller handles, connection facts, and timeout knobs. A new
    /// accessor fails the suite until consciously whitelisted; a setter that
    /// sends commands must never be whitelisted (commands go through methods,
    /// where the wire sweep sees them).</summary>
    private static readonly IReadOnlyDictionary<Type, string[]> PropertyAccessorWhitelist =
        new Dictionary<Type, string[]>
        {
            [typeof(Prc138Radio)] =
            [
                "get_State", "get_Ssb", "get_Ale", "get_Hop",
                "get_Connection", "get_IsInitialized", "get_IsConnectionOpen",
                "get_RemotePort", "get_IsModeChangePending",
                // X8 (plan-ale-programming.md §4.3): the sentinel-queue depth
                // is now PUBLIC data — the ALE programming bracket must know
                // whether another producer's sentinel is on the wire before
                // it releases a write, because only an empty queue makes its
                // closing sentinel dispatch adjacent to that write. A getter,
                // nothing more: reading it sends no command.
                "get_PendingPingCount",
                // Audit round 2, BLOCKER: the same bracket must also know
                // whether a PREVIOUSLY timed-out sentinel's answer is still
                // in flight — Core's late-answer credit would otherwise
                // complete the bracket's own barrier early and put the write
                // outside its window. Also a getter, also sends nothing.
                "get_PingAnswerDebt",
                // ROUND 15 (bench 2026-08-22): BATTERY lines that answered
                // NOTHING because the queue head had not been written yet —
                // the radio's extra answer at a mode entry. A getter, like the
                // two above; reading it sends nothing, and a bench harness
                // reports it beside the raw wire counts.
                "get_StrayBatteryAnswers",
                "get_InitializationTimeoutMs", "set_InitializationTimeoutMs",
                "get_EffectiveInitializationTimeoutMs",
                // CLONE FIELD ROUND 2 §3.5 (F7, decision A-3): the FIRST init
                // sentinel's own, shorter timeout. A knob, like the two beside
                // it — reading or setting it sends nothing; QueueInitSentinels
                // is the only reader and it runs from Connect and the watchdog.
                "get_FirstInitSentinelTimeoutMs", "set_FirstInitSentinelTimeoutMs",
                "get_ModeChangeTimeoutMs", "set_ModeChangeTimeoutMs",
                // CLONE ROUND 12 §3 leg 6: the FM-squelch cycle flag the clone
                // campaign waits on before writing AnalogSquelch. A getter over
                // Core's own compensation bookkeeping — reading it sends
                // nothing, and there is no setter (the campaign may observe the
                // cycle, never fake it).
                "get_IsFmSquelchCyclePending",
                // CLONE ROUND 12 §3 leg 2: the zeroize settle machine's
                // observables and its two knobs. All data — the ARMING is
                // internal (SsbController.ZeroizeRadio), so no app-layer caller
                // can start a settle without going through the guarded builder.
                "get_IsZeroizeSettling", "get_ZeroizeSettled", "get_ZeroizeFaulted",
                "get_ZeroizeSettleTimeoutMs", "set_ZeroizeSettleTimeoutMs",
                "get_ZeroizeSettlePollMs", "set_ZeroizeSettlePollMs",
            ],
            // Round 11 §8: the modem read queue's settle time — a knob, like
            // the ALE ones below; reading or writing it sends nothing.
            // Round 12 §3: the lockout read's settle time, same treatment.
            [typeof(Falcon.Core.Modes.SsbController)] =
            [
                "get_ModemReadTimeoutMs", "set_ModemReadTimeoutMs",
                "get_LockoutReadTimeoutMs", "set_LockoutReadTimeoutMs",
            ],
            [typeof(Falcon.Core.Modes.AleController)] =
            [
                "get_AmdVerifyTimeoutMs", "set_AmdVerifyTimeoutMs",
                "get_RefreshTimeoutMs", "set_RefreshTimeoutMs",
            ],
            // Round 11 §8 (X9): the EXCLUDE read's settle time.
            [typeof(Falcon.Core.Modes.HopController)] =
                ["get_ReadTimeoutMs", "set_ReadTimeoutMs"],
        };

    [Fact]
    public void PropertyAccessors_AreExactlyTheWhitelistedSet()
    {
        foreach (var (type, allowed) in PropertyAccessorWhitelist)
        {
            var actual = type
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .SelectMany(p => new[] { p.GetGetMethod(), p.GetSetMethod() })
                .Where(m => m is not null && m.IsPublic)    // non-public accessors return null / are filtered
                .Select(m => m!.Name)
                .Distinct()
                .Order()
                .ToArray();

            Assert.Equal(allowed.Distinct().Order().ToArray(), actual);
        }
    }

    /// <summary>Wire tokens no SWEPT (safe-surface) sender may ever emit —
    /// hazardous or guarded command families. "SLFAD " etc. with a trailing
    /// space = write arity; the bare query forms are allowed. Phase R
    /// (plan-gui-rejigger.md round 4) DELETED the old "no builders exist"
    /// pins for these families: gated/GUI-out builders now legitimately
    /// emit some of these forms — each is EXCLUDED from the sweep and its
    /// output is pinned as prefix-catchable (the Stage 11 SetRemoteBaud
    /// pattern), so any NEW unauthorized sender still fails the sweep.
    /// Removed outright per round 4 (now safe-surface settings): "AVS",
    /// "RWAS", "MODEM " (preset SELECT is a setting). "TE "/"TE\r" narrowed
    /// to "TE 4" + the exact bare "TE" (below) so the safe "TE 3" firmware
    /// query passes.
    /// AMENDED round 8 (plan-ui-tweaks-round8.md, X7 — owner: "take all the
    /// modem stuff out of the scope guard"): "MODEM PRESET" removed outright
    /// — preset PROGRAMMING is now a safe-surface setting behind the Radio
    /// settings preset editor (ProgramModemPreset, one validated line;
    /// QueryModemPresets never matched a prefix). The builders' exact wire
    /// forms are pinned below with the other modem forms.
    /// AMENDED, ALE programming (plan-ale-programming.md §4.2, X8): the bare
    /// <c>"CHG"</c> prefix is REPLACED by <c>"CHGROUP "</c>. The group QUERY
    /// (<c>CHG n</c>) is now a swept safe-surface read, while the un-built
    /// whole-list SET form (<c>CHGROUP g ch ch …</c>) stays forbidden to
    /// EVERY sender — no builder emits it, and none may. Non-vacuity is
    /// pinned in
    /// <see cref="X8_ChgroupSetForm_StaysForbidden_WhileTheGroupQueryPasses"/>.
    /// The eight fill-write prefixes STAY forbidden: X8 moved them into the
    /// app's reach through the surface, not out of the sweep.</summary>
    private static readonly string[] ForbiddenWirePrefixes =
    [
        "ZERO", "ERASE", "K ", "KEYLINE", "TE 4", "ENC_KEY", "USE_KEY",
        "ENCR ", "SLFAD ", "INDAD ", "NETAD ", "ADDM", "DELAD",
        "ADDC", "DELC", "CHGROUP ", "PASSWORD", "PROGRAM",
        "SELECT", "LEVEL", "DOIT", "NETID ", "HOPTYPE ", "HOPSET ", "HOPLIST ",
        "EXC ", "PORT_R BAUD", "PORT_R BITS", "PORT_R PARITY", "PORT_R STOP",
        "PORT_R XON",
    ];

    /// <summary>Exact whole-line forbidden commands (prefixes cannot catch
    /// the bare "TE" self test without also catching "TE 3").</summary>
    private static readonly string[] ForbiddenExactWireLines = ["TE"];

    private static bool IsForbiddenLine(string line) =>
        ForbiddenExactWireLines.Any(exact => string.Equals(line, exact, StringComparison.OrdinalIgnoreCase))
        || ForbiddenWirePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void WireSweep_EveryCommandSendingMethod_EmitsNoForbiddenToken()
    {
        ConnectReady();

        // Exercise every whitelisted SAFE command-sending method once (valid
        // arguments; connection-lifecycle and passthrough members excluded —
        // RawCommand is the operator's own console input by definition).
        Radio.SelectMode(OperatingMode.Ssb);
        Radio.SelectSsb(); Radio.SelectAle(); Radio.SelectHop();
        Radio.Show();
        Radio.QueryBatteryState();
        Radio.SetPowerLevel(PowerLevel.High);
        Radio.QueryPowerLevel();
        Radio.QueryPortConfig();
        Radio.QueryTime();
        Radio.SetRemoteEcho(OnOff.Off);
        Radio.SetBacklightFunction(BacklightFunction.Momentary);
        Radio.SetBacklightIntensity(4);
        Radio.SetContrast(5);
        Radio.QueryBacklightFunction();
        Radio.QueryBacklightIntensity();
        Radio.QueryContrast();
        Radio.Ping(_ => { }, 0);
        // NOT exercised here — the gated / GUI-out builders (plan round 4:
        // fill editing E2, net programming E3, crypto E1, TE hazards E5,
        // keyline, ERASE, and Stage 11's SetRemoteBaud): their prefixes STAY
        // forbidden so any NEW unauthorized sender fails this sweep; each
        // builder's own output is pinned separately (exact wire string) and
        // GatedAndGuiOutBuilders_AreExactlyTheForbiddenPrefixCatchSet proves
        // every one of them is still caught by this list.

        Radio.Ssb.SetFrequency("14234500");
        Radio.Ssb.SetRxFrequency("01600000");
        Radio.Ssb.SetTxFrequency("29999999");
        Radio.Ssb.IncrementFrequency();
        Radio.Ssb.DecrementFrequency();
        Radio.Ssb.QueryFrequency();
        Radio.Ssb.QueryStep();
        Radio.Ssb.SetStep(FrequencyStep.OneKHz);
        Radio.Ssb.SetModulation(ModulationMode.Usb);
        Radio.Ssb.QueryModulation();
        Radio.Ssb.SetBandwidth("2.7");
        Radio.Ssb.QueryBandwidth();
        Radio.Ssb.SetAgc(AgcSpeed.Slow);
        Radio.Ssb.QueryAgc();
        Radio.Ssb.SetRxOnly(YesNo.No);
        Radio.Ssb.SelectChannel(1);
        Radio.Ssb.QueryChannel();
        Radio.Ssb.DisplayChannels(0, 99);
        Radio.Ssb.DisplayAllChannels();
        Radio.Ssb.ForgetStoredChannels();     // sends nothing — swept anyway
        Radio.Ssb.Retune();
        Radio.Ssb.SetSquelch(OnOff.On);
        Radio.Ssb.SetDigitalVoice(OnOff.On);
        Radio.Ssb.SetDigitalSquelch(OnOff.Off);
        Radio.Ssb.SetSquelchLevel(SquelchLevel.Low);
        Radio.Ssb.SetFmSquelch(OnOff.On);
        Radio.Ssb.SetFmSquelchType(FmSquelchType.Tone);
        Radio.Ssb.SetFmTone(OnOff.Off);
        Radio.Ssb.SetFmDeviation("8.0");
        Radio.Ssb.SetBfoOffset(-1000);
        Radio.Ssb.SetCwOffset(1000);
        Radio.Ssb.SetCompression(OnOff.On);
        Radio.Ssb.SetRfGain(100);
        Radio.Ssb.SetRxPreamp(BypassEnable.Enable);
        Radio.Ssb.SetAvs(OnOff.Off);
        Radio.Ssb.SetAntenna(AntennaPort.Auto);
        Radio.Ssb.SetInternalCoupler(BypassEnable.Bypass);
        Radio.Ssb.SetOneKilowattPa(YesNo.No);
        Radio.Ssb.SetRetransmit(EnabledDisabled.Disabled);
        Radio.Ssb.SetPrePostFilter(OnOff.On);
        Radio.Ssb.SetPrePostRxAntenna(OnOff.Off);
        Radio.Ssb.SetPrePostScanRate(PrePostScanRate.Slow);
        Radio.Ssb.SetBeep(OnOff.On);
        Radio.Ssb.SetRwas(EnabledDisabled.Enabled);
        Radio.Ssb.SetForceWakeup(EnabledDisabled.Disabled);
        Radio.Ssb.SetRwasKey(7);
        Radio.Ssb.SetUnkeyMask(EnabledDisabled.Enabled);
        Radio.Ssb.SelectModem("T39");
        Radio.Ssb.ModemOff();
        // Round 8 (X7): the modem preset read + program are safe-surface now.
        // Round 9: short value tokens, baud a token from the discrete set.
        // Round 11 §8: three read operations replace the old bulk one.
        Radio.Ssb.QueryModemPreset(2);
        Radio.Ssb.RefreshModemPresets();
        Radio.Ssb.QueryModemPresetPresence();
        Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400",
            interleave: "LO", mark: "1575", space: "1425", enabled: true);
        // Clone-field round 2 F9: the HOP-scoped preset builder, same safe
        // surface (a stored-preset write, no transmit). It needs its OWN
        // prompt — the line is INVALID at the other two and the builder refuses
        // to guess (audit round 2) — so the sweep visits HOP for it and comes
        // straight back, which is also the only honest way to sweep a
        // prompt-scoped command's bytes at all.
        Transport.InjectLine("HOP>");
        Radio.Ssb.ProgramHopModemPreset(9, "DAT9", SyncMode.Async, DataMode.Remote, "300", enabled: false);
        Transport.InjectLine("SSB>");
        Radio.Ssb.QueryFirmwareVersions();
        // Round-3 V7 query set (old-app-derived reads — safe surface).
        Radio.Ssb.QueryFmSquelchType();
        Radio.Ssb.QueryFmTone();
        Radio.Ssb.QueryFmDeviation();
        Radio.Ssb.QueryCwOffset();
        Radio.Ssb.QueryAvs();
        Radio.Ssb.QueryRxPreamp();
        Radio.Ssb.QueryRfGain();
        Radio.Ssb.QueryAntenna();
        Radio.Ssb.QueryInternalCoupler();
        Radio.Ssb.QueryOneKilowattPa();
        Radio.Ssb.QueryRetransmit();
        Radio.Ssb.QueryPrePostFilter();
        Radio.Ssb.QueryPrePostRxAntenna();
        Radio.Ssb.QueryPrePostScanRate();
        Radio.Ssb.QueryRwas();
        Radio.Ssb.QueryUnkeyMask();
        Radio.Ssb.QueryBeep();
        // Round 12 §9 B3: the compression read. Bare "COM" is caught by no
        // forbidden prefix and is a harmless read, so it is swept like the rest
        // of the query set.
        Radio.Ssb.QueryCompression();
        // SetKeyline is NOT swept even for OFF: both its forms start with
        // the forbidden "K " prefix (hazard family) — pinned separately.
        // NOR are the X12/X13 builders: QueryLockouts emits "PROGRAM"/"SELECT"
        // and ZeroizeRadio emits "ZERO", all three of which are forbidden
        // prefixes for every other sender. They take the SetRemoteBaud
        // treatment — excluded here, exact forms pinned, and
        // GatedAndGuiOutBuilders_AreExactlyTheForbiddenPrefixCatchSet proves
        // the prefixes still catch them.

        Radio.Ale.StartScan();
        Radio.Ale.Stop();
        Radio.Ale.Call("BOB", "01");
        Radio.Ale.SendAmd("HI", "BOB");          // TXMSG 9 write is the allowed scratch exception
        Radio.Ale.QueryRxMessages();
        Radio.Ale.Rank("BOB");
        Radio.Ale.StartExchange("BOB", "01:00", "12:30");
        Radio.Ale.StopExchange("BOB");
        Radio.Ale.StartSounding("CAM", "02:00", null);
        Radio.Ale.StopSounding("CAM");
        Radio.Ale.QuerySelfAddresses();
        Radio.Ale.QueryIndividualAddresses();
        Radio.Ale.QueryNetAddresses();
        Radio.Ale.RefreshStationList();
        // X8: the group READS and the bare sentinel barrier join the manual
        // sweep list — their "CHG n" / "BAT ST" lines must pass the forbidden
        // set, which is exactly what the "CHG" → "CHGROUP " swap buys.
        Radio.Ale.RequestChannelGroup(3);
        Radio.Ale.RefreshChannelGroups();
        Radio.Ale.Synchronize();
        // Round 11 §8: the bare-EXCH schedule read is safe surface and joins
        // the sweep. ReadNetMembers is NOT swept — its "NETAD <name>" line is
        // caught by the "NETAD " write-arity prefix (a targeted READ and a net
        // WRITE are indistinguishable by prefix), so it takes the SetRemoteBaud
        // treatment instead: excluded here, its exact wire form pinned, and the
        // prefix proven still to catch it. See
        // ReadNetMembers_SendsExactlyTheTargetedQueryAndItsSentinel and
        // WireSweepException_TargetedNetadRead_IsStillForbiddenForEveryOtherSender.
        Radio.Ale.ReadLqaSchedules();
        // Round 11 §9A (X10): the stored-message store. "TXMSG" is not a
        // forbidden prefix (the scratch-slot AMD write already emits it), so
        // all three are swept like any other safe-surface sender; the clear
        // sends nothing and is swept anyway.
        Radio.Ale.QueryTxMessages();
        Radio.Ale.StoreTxMessage(0, "RADIO CHECK");
        Radio.Ale.DeleteTxMessage(0);
        Radio.Ale.ForgetStoredMessages();
        Radio.Ale.SetAllCall(OnOff.On);
        Radio.Ale.SetAnyCall(OnOff.Off);
        Radio.Ale.SetAmdDisplay(OnOff.On);
        Radio.Ale.SetKeyToCall(OnOff.Off);
        Radio.Ale.SetListenBeforeTx(OnOff.On);
        Radio.Ale.SetRadioSilence(OnOff.Off);
        Radio.Ale.SetMaxScanChannels(100);
        Radio.Ale.SetLinkTimeout(0);
        Radio.Ale.SetTuneTime(15);

        Radio.Hop.QueryAllNets();
        Radio.Hop.QueryNet(3);
        Radio.Hop.SelectNet(0);
        Radio.Hop.Sync();
        Radio.Hop.SetTimeOfDay(new DateTime(2026, 8, 2, 20, 37, 12));
        // X9 (round 11): the exclusion-band QUERY left the GUI-out list and is
        // now the sentinel-scoped read. Its bare "EXC" passes the forbidden
        // set — "EXC " (with the space) still catches every SET and DEL.
        Radio.Hop.QueryExcludeBands();

        Assert.NotEmpty(Transport.SentLines);
        foreach (var line in Transport.SentLines)
            Assert.False(IsForbiddenLine(line), "Forbidden wire form: " + line);
    }

    // ---- Wire-injection guard: control chars in free-string arguments -------
    // The transport is CR-terminated and the send path does NO escaping
    // (CommandFactory joins with spaces, SerialTransport appends "\r"), so an
    // embedded CR/LF in any free string reaching the wire would emit a SECOND
    // arbitrary command. The static forbidden-prefix sweep cannot see a
    // RUNTIME argument, so the validators are the only defense. Every ALE
    // builder taking a free string (address/self/AMD text/channel) must
    // REJECT control chars and send NOTHING (BLOCKER, coordinator re-audit).

    [Fact]
    public void DeleteAddress_TheExactExploit_ThrowsAndSendsNothing()
    {
        // DeleteAddress("AAA\rZERO") -> "DELAD AAA\rZERO\r" -> radio runs
        // DELAD AAA then ZERO (wipes memory). Must never reach the wire.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.DeleteAddress("AAA\rZERO"));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("AAA\rZERO")]                 // CR — the injection carrier
    [InlineData("AAA\nERASE")]                // LF
    [InlineData("AAA\rPORT_R BAUD 75")]       // session-ending injection
    [InlineData("A\tB")]                      // any other control char
    [InlineData("A\0B")]                      // NUL
    public void AleFreeStringBuilders_ControlCharArgument_ThrowsAndSendsNothing(string poison)
    {
        ConnectReady();

        // Every builder whose free-string arg flows through ValidateAddress /
        // ValidateSelf / the AMD-text guard / ValidateChannel.
        Assert.Throws<ArgumentException>(() => Radio.Ale.Call(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.Rank(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StopExchange(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.DeleteAddress(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.AddNetMember(poison, "BOB"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.AddNetMember("NT1", poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetIndividualAddress(poison, 1, "CAM"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetNetAddress(poison, 1, "CAM"));
        // AMD free message text (only the 90-char-fitting poisons apply).
        if (poison.Length <= 90)
            Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd(poison, "BOB"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd("HI", poison));

        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("AA\rZERO")]                  // 3+ chars, and control-bearing
    [InlineData("A\rB")]
    public void SelfAddressBuilders_ControlCharArgument_ThrowsAndSendsNothing(string poison)
    {
        // ValidateSelf gates the self-address slot AND the associated-self
        // slot of INDAD/NETAD, plus SOU (sounding) — all self-typed.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetSelfAddress(poison, 1));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetIndividualAddress("BOB", 1, poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetNetAddress("NT1", 1, poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding(poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StopSounding(poison));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("01\rZERO")]
    [InlineData("0\n")]
    [InlineData("AB")]        // non-digit channel is rejected too (structural)
    public void CallAndSendAmd_PoisonedChannel_ThrowsAndSendsNothing(string channel)
    {
        // The optional CAL/SE channel is a free string reaching the wire —
        // "01\rZERO" would inject. SendAmd validates it UP FRONT so not even
        // the TXMSG store lines go out.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.Call("BOB", channel));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd("HI", "BOB", channel));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AmdTextWithEmbeddedCr_ThrowsBeforeAnyTxmsgSend()
    {
        // The AMD text guard must fire before the FIRST send, or the two
        // TXMSG lines leak even though SE never fires.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.SendAmd("MEET\rZERO", "BOB"));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("12:34\n")]        // .NET $ matches before a trailing \n
    [InlineData("12:34\r")]        // CR carrier
    [InlineData("01:00\r\n")]
    public void LqaSchedule_HhMmWithTrailingControlChar_ThrowsAndSendsNothing(string poison)
    {
        // ValidateHhMm's \A..\z anchors (NOT ^..$) forbid a trailing newline,
        // so "12:34\n" cannot slip an LF onto the CR-terminated EXCH/SOU line
        // (auditor MINOR: $ + int.Parse trailing-whitespace tolerance let it
        // through). Both the interval and start slots of both builders.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange("BOB", poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartExchange("BOB", "01:00", poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding("CAM", poison));
        Assert.Throws<ArgumentException>(() => Radio.Ale.StartSounding("CAM", "01:00", poison));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void CleanArguments_StillPassThrough_GuardIsNotOverZealous()
    {
        // The guard rejects control chars ONLY — ordinary addresses, AMD
        // text with spaces, and digit channels are unaffected.
        ConnectReady();
        Radio.Ale.Call("BOB", "05");
        Radio.Ale.DeleteAddress("CAM");
        Radio.Ale.SetSelfAddress("CAM", 1);
        Assert.Equal(["CAL BOB 05", "DELAD CAM", "SLFAD CAM 1"], Transport.SentLines);
    }

    // ---- Phase R: SSB settings builders (plan-gui-rejigger.md round 4) -------
    // Exact wire forms per docs/protocol.md HELP listings; builders whose
    // set form was never bench-sent are flagged in the classification table.

    [Fact]
    public void SsbSquelchFamily_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SetSquelch(OnOff.On);
        Radio.Ssb.SetSquelch(OnOff.Off);
        Radio.Ssb.SetDigitalVoice(OnOff.On);
        Radio.Ssb.SetDigitalSquelch(OnOff.Off);
        Radio.Ssb.SetSquelchLevel(SquelchLevel.Low);
        Radio.Ssb.SetSquelchLevel(SquelchLevel.Medium);
        Radio.Ssb.SetSquelchLevel(SquelchLevel.High);
        Radio.Ssb.SetFmSquelch(OnOff.On);
        Radio.Ssb.SetFmSquelchType(FmSquelchType.Noise);
        Radio.Ssb.SetFmSquelchType(FmSquelchType.Tone);
        Radio.Ssb.SetFmTone(OnOff.Off);
        Radio.Ssb.SetFmDeviation("6.5");

        Assert.Equal(
            ["SQ ON", "SQ OFF", "DV ON", "DGT_S OFF", "SQ_L LO", "SQ_L MEDIUM",
             "SQ_L HIGH", "FMSQ ON", "FMSQ_T NOISE", "FMSQ_T TONE",
             "FMTONE OFF", "FMDE 6.5"],
            Transport.SentLines);
    }

    [Fact]
    public void SsbAudioAndTxSettings_SendDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SetBfoOffset(0);
        Radio.Ssb.SetBfoOffset(1000);
        Radio.Ssb.SetBfoOffset(-250);
        Radio.Ssb.SetCwOffset(0);
        Radio.Ssb.SetCwOffset(1000);
        Radio.Ssb.SetCompression(OnOff.On);
        Radio.Ssb.SetRfGain(0);
        Radio.Ssb.SetRfGain(100);
        Radio.Ssb.SetRxPreamp(BypassEnable.Bypass);
        Radio.Ssb.SetAvs(OnOff.On);
        Radio.Ssb.SetAntenna(AntennaPort.Bnc);
        Radio.Ssb.SetAntenna(AntennaPort.Tuned);
        Radio.Ssb.SetInternalCoupler(BypassEnable.Enable);
        Radio.Ssb.SetOneKilowattPa(YesNo.Yes);
        Radio.Ssb.SetRetransmit(EnabledDisabled.Enabled);
        Radio.Ssb.SetPrePostFilter(OnOff.On);
        Radio.Ssb.SetPrePostRxAntenna(OnOff.Off);
        Radio.Ssb.SetPrePostScanRate(PrePostScanRate.Fast);
        Radio.Ssb.SetBeep(OnOff.Off);

        Assert.Equal(
            ["BF +0000", "BF +1000", "BF -0250", "CWOFF 0000", "CWOFF 1000",
             "COM ON", "RF 0", "RF 100", "PRE BYPASS", "AVS ON", "ANTENNA BNC",
             "ANTENNA TUNED", "INTCOUPLER ENABLE", "KWAT YES", "RETR ENA",
             "PREPOST FILTER ENABLE", "PREPOST RXANTENNA DISABLE",
             "PREPOST SCAN FAST", "BEEP OFF"],
            Transport.SentLines);
    }

    [Fact]
    public void SsbSettings_ValidationRejectsBeforeTheWire()
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SetFmDeviation("7.0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetBfoOffset(10000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetBfoOffset(-10000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetCwOffset(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetRfGain(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetRfGain(-1));
        Assert.Empty(Transport.SentLines);
    }

    // ---- UI-tweaks round 3, V7: the old-app-derived SSB query set ----------
    // PROVISIONAL wire forms (docs/protocol.md "Old-app-derived SSB query set
    // (PROVISIONAL — bench-unconfirmed)"): each command is pinned to the exact
    // string the WinForms app sends (old repo
    // src/Falcon.Core/Radio/Prc138Radio.cs:987-1009, :1063, :1050, :1140).
    // These pins are what a bench correction has to come through: if the radio
    // wants a different abbreviation, this test is the one place that changes.

    [Fact]
    public void SsbSettingsQueries_SendTheOldAppDerivedForms()
    {
        ConnectReady();
        Radio.Ssb.QueryFmSquelchType();
        Radio.Ssb.QueryFmTone();
        Radio.Ssb.QueryFmDeviation();
        Radio.Ssb.QueryCwOffset();
        Radio.Ssb.QueryAvs();
        Radio.Ssb.QueryRxPreamp();
        Radio.Ssb.QueryRfGain();
        Radio.Ssb.QueryAntenna();
        Radio.Ssb.QueryInternalCoupler();
        Radio.Ssb.QueryOneKilowattPa();
        Radio.Ssb.QueryRetransmit();
        Radio.Ssb.QueryPrePostFilter();
        Radio.Ssb.QueryPrePostRxAntenna();
        Radio.Ssb.QueryPrePostScanRate();
        Radio.Ssb.QueryRwas();
        Radio.Ssb.QueryUnkeyMask();
        Radio.Ssb.QueryBeep();

        Assert.Equal(
            ["FMSQ_T", "FMTONE", "FMDE", "CWOFF", "AVS", "PRE", "RF",
             "ANTENNA", "INTCOUPLER", "KWAT", "RETR", "PREPOST FILTER",
             "PREPOST RXANTENNA", "PREPOST SCAN", "RWAS", "UNKEY_M", "BEEP"],
            Transport.SentLines);
    }

    [Fact]
    public void SsbSettingsQueries_BeforeConnect_AreNotSent()
    {
        Radio.Ssb.QueryRwas();
        Radio.Ssb.QueryPrePostFilter();
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>A bare query must never be confusable with a SET: the query
    /// builders emit the command token ALONE. Pinned because "RF" vs "RF 0"
    /// and "RWAS" vs "RWAS DIS" differ by one argument, and a query that
    /// accidentally carried one would silently change the radio.</summary>
    [Fact]
    public void SsbSettingsQueries_CarryNoArgument()
    {
        ConnectReady();
        Radio.Ssb.QueryRfGain();
        Radio.Ssb.QueryRwas();
        Radio.Ssb.QueryUnkeyMask();
        Radio.Ssb.QueryAvs();
        Radio.Ssb.QueryBeep();
        Radio.Ssb.QueryAntenna();

        Assert.Equal(["RF", "RWAS", "UNKEY_M", "AVS", "BEEP", "ANTENNA"], Transport.SentLines);
        foreach (var line in Transport.SentLines)
            Assert.DoesNotContain(' ', line);
    }

    [Fact]
    public void RwasGroup_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SetRwas(EnabledDisabled.Enabled);
        Radio.Ssb.SetRwas(EnabledDisabled.Disabled);
        Radio.Ssb.SetForceWakeup(EnabledDisabled.Enabled);
        Radio.Ssb.SetRwasKey(0);
        Radio.Ssb.SetRwasKey(99);
        Radio.Ssb.SetUnkeyMask(EnabledDisabled.Disabled);

        Assert.Equal(
            ["RWAS ENA", "RWAS DIS", "FORCE_W ENA", "RWAS_KEY 00",
             "RWAS_KEY 99", "UNKEY_M DIS"],
            Transport.SentLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetRwasKey(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.SetRwasKey(-1));
    }

    [Fact]
    public void ModemSelect_SendsBenchForms_OtherFormsCannotBeSmuggledThroughSelect()
    {
        ConnectReady();
        Radio.Ssb.SelectModem("1");
        Radio.Ssb.SelectModem("t39");        // uppercased
        Radio.Ssb.ModemOff();
        Assert.Equal(["MODEM 1", "MODEM T39", "MODEM OF"], Transport.SentLines);

        Transport.ClearSent();
        // Round 8 (X7): PRE and PRESET have their OWN builders now, but the
        // SELECT path still refuses them — each wire form has exactly one
        // sender. MODEM SH remains builder-less entirely.
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem("PRESET"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem("PRESET 1 NAME X"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem("SH"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem("PRE"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem("OFF"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelectModem(""));
        Assert.Empty(Transport.SentLines);
    }

    // ---- Round 8 (EE, X7): the modem preset read + program builders ---------

    // ---- Round 11 §8: the modem preset READ SEAM ---------------------------
    // The old QueryModemPresets pin ("sends MODEM PRE and CLEARS the mirror
    // first") is DELETED with the behavior: clearing is exactly what made the
    // three preset states unrepresentable. Its replacements are below.

    [Fact]
    public void QueryModemPreset_SendsTheTargetedForm_AndItsSentinel()
    {
        ConnectReady();
        long readId = Radio.Ssb.QueryModemPreset(2);

        Assert.Equal(["MODEM PRE 2", "BAT ST"], Transport.SentLines);
        Assert.NotEqual(0, readId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void QueryModemPreset_OutOfRange_ThrowsAndSendsNothing(int preset)
    {
        // The 0-6 band, AT A CONFIRMED SSB PROMPT — this class's ConnectReady
        // confirms one (audit round 2: an unconfirmed preset command refuses
        // outright, so "out of range" can only be asked inside a known band).
        // 7 answers INVALID MODEM PRESET there; -1 is in no band at all.
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.QueryModemPreset(preset));
        Assert.Empty(Transport.SentLines);
    }

    // ---- F9: the PROMPT-SCOPED preset band ---------------------------------
    // "Presets are 0-6 on this firmware" was a fact about the prompt nobody had
    // left. `MODEM PRE 7` answers INVALID MODEM PRESET at `SSB>`/`ALE>` and a
    // stored preset at `HOP>`, where 0-6 are the INVALID half instead (probe
    // P5, bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl). The
    // scope comes from the radio's OWN confirmed mode, never from a caller.

    /// <summary>Puts the radio's confirmed mode where the guards read it, by
    /// replaying the prompt line the parser confirms modes from.</summary>
    private void ConfirmPrompt(string prompt)
    {
        Transport.InjectLine(prompt);
        Assert.True(Radio.State.OperatingMode.IsConfirmed);
        Transport.ClearSent();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void TheGuard_RefusesAnSsbPreset_AtAHopPrompt(int preset)
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.QueryModemPreset(preset));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(
            preset, "T39", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramHopModemPreset(
            preset, "DAT9", SyncMode.Async, DataMode.Remote, "300"));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("SSB>")]
    [InlineData("ALE>")]
    public void TheGuard_RefusesAHopPreset_AtAnSsbOrAlePrompt(string prompt)
    {
        ConnectReady();
        ConfirmPrompt(prompt);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.QueryModemPreset(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramModemPreset(
            7, "T39", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramHopModemPreset(
            9, "DAT9", SyncMode.Async, DataMode.Remote, "300"));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void QueryModemPreset_AtAHopPrompt_ReadsTheHopBand()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        Radio.Ssb.QueryModemPreset(9);
        Assert.Equal(["MODEM PRE 9", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void RefreshModemPresets_AtAHopPrompt_IsTheTHREEHopReadsAndONESentinel()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        Radio.Ssb.RefreshModemPresets();
        Assert.Equal(["MODEM PRE 7", "MODEM PRE 8", "MODEM PRE 9", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void ProgramHopModemPreset_IsTheShortLine_WithTheStateTokenLASTOnItsOwnLine()
    {
        // The line P5b applied, byte for byte, plus the state token on its own
        // line AFTER it — because any field write RE-ENABLES a disabled preset
        // (P5b), so a DIS carried with the fields would be undone by them.
        ConnectReady();
        ConfirmPrompt("HOP>");

        Radio.Ssb.ProgramHopModemPreset(9, "DAT9", SyncMode.Async, DataMode.Remote, "300", enabled: false);

        Assert.Equal(
            ["MODEM PRESET 9 NAME DAT9 ASYNC REMOTE BAUD 300", "MODEM PRESET 9 DIS"],
            Transport.SentLines);
    }

    [Fact]
    public void ProgramHopModemPreset_WithNoStateArgument_SendsTheFieldLineAlone()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        Radio.Ssb.ProgramHopModemPreset(7, "tst7", SyncMode.Sync, DataMode.Data, "75");

        Assert.Equal(["MODEM PRESET 7 NAME TST7 SYNC DATA BAUD 75"], Transport.SentLines);
    }

    [Fact]
    public void ProgramHopModemPreset_EnabledTrue_PutsENOnTheTrailingLine()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        Radio.Ssb.ProgramHopModemPreset(8, "DAT8", SyncMode.Async, DataMode.Data, "150", enabled: true);

        Assert.Equal(
            ["MODEM PRESET 8 NAME DAT8 ASYNC DATA BAUD 150", "MODEM PRESET 8 EN"],
            Transport.SentLines);
    }

    [Fact]
    public void ProgramHopModemPreset_RejectsEverythingOutsideTheCapturedShapes()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");

        // Preset number: the HOP band only.
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramHopModemPreset(
            6, "DAT9", SyncMode.Async, DataMode.Remote, "300"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramHopModemPreset(
            10, "DAT9", SyncMode.Async, DataMode.Remote, "300"));

        // Name: the existing 1-4 alnum rule, and the selector-token collision.
        foreach (var name in new[] { "", "TOOLONG", "D-9", "OFF", "PRE" })
            Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramHopModemPreset(
                9, name, SyncMode.Async, DataMode.Remote, "300"));

        // BAUD: exactly {75, 150, 300} — P5c swept the rest and every one was
        // SILENTLY ignored with the old value echoed back, which is the failure
        // a caller cannot see and therefore the one worth refusing.
        foreach (var baud in new[] { "50", "100", "110", "200", "600", "1200", "2400", "4800", "9600", "VO", "" })
            Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramHopModemPreset(
                9, "DAT9", SyncMode.Async, DataMode.Remote, baud));

        Assert.Empty(Transport.SentLines);
    }

    /// <summary>
    /// AUDIT ROUND 1, MAJOR 1 — <b>a QUEUED presence read commits the scope it
    /// was QUEUED WITH.</b> The single-slot modem queue holds a presence read
    /// behind an active targeted one, and promotion used to leave the scope
    /// field holding whatever the last DIRECTLY-dispatched read had set. The
    /// exact live sequence: the card's editor landing sends `MODEM PRE n` and
    /// queues the presence read behind it, so on the FIRST HOP landing the
    /// listing that eventually goes out at `HOP>` committed labelled `Ssb` —
    /// a set claiming to cover a band it says nothing about, which is worse
    /// than no set at all.
    /// </summary>
    [Fact]
    public void AQueuedPresenceRead_CommitsTheScopeItWasQueuedWith_NotTheLastDispatchedOne()
    {
        ConnectReady();

        // A presence read dispatched DIRECTLY at SSB — this is what sets the
        // scope field, and what used to leak into the next one.
        ConfirmPrompt("SSB>");
        Radio.Ssb.QueryModemPresetPresence();
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        AnswerSentinel();
        Assert.True(Radio.State.ModemPresetPresence.Covers(OperatingMode.Ssb));

        // Now HOP: a TARGETED read takes the queue, and the presence read is
        // QUEUED behind it — the card's own landing order.
        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPreset(9);
        Radio.Ssb.QueryModemPresetPresence();
        Assert.True(Radio.State.IsModemTargetedReadActive, "the targeted read should own the queue");

        AnswerSentinel();                       // the targeted sentinel → presence PROMOTES
        Assert.True(Radio.State.IsModemPresenceReadActive);
        Transport.InjectLine("MODEM PRESET 9 DAT9 ASYNC REMOTE BAUD 300   ");
        AnswerSentinel();                       // the presence sentinel → it commits

        var presence = Radio.State.ModemPresetPresence;
        Assert.Equal([9], presence.Enabled);
        Assert.True(presence.Covers(OperatingMode.Hop), "the promoted read committed the WRONG scope");
        Assert.False(presence.Covers(OperatingMode.Ssb));
    }

    /// <summary>
    /// AUDIT ROUND 2, MAJOR 1 — <b>NO MODEM READ GOES OUT WITHOUT A CONFIRMED
    /// PROMPT.</b> The band a preset command means depends entirely on which
    /// prompt it is sent at, so a command sent before the radio has named one
    /// is a guess — and the guess it used to make (the SSB band) puts
    /// <c>MODEM PRE 0</c> on the wire, which at <c>HOP&gt;</c> is simply
    /// <c>INVALID MODEM PRESET</c>. The whole family refuses instead, and the
    /// wire stays clean.
    /// </summary>
    [Fact]
    public void EveryModemPresetCommand_RefusesWhileTheModeIsUNCONFIRMED_AndSendsNothing()
    {
        // base.ConnectReady: Ready with NO prompt line, which is the real
        // window — the connect ritual's `SH` answer carries the prompt, and
        // until it lands nothing has named the band.
        base.ConnectReady();
        Assert.False(Radio.State.OperatingMode.IsConfirmed);

        Assert.Throws<InvalidOperationException>(() => Radio.Ssb.QueryModemPreset(0));
        Assert.Throws<InvalidOperationException>(() => Radio.Ssb.RefreshModemPresets());
        Assert.Throws<InvalidOperationException>(() => Radio.Ssb.QueryModemPresetPresence());
        Assert.Throws<InvalidOperationException>(() => Radio.Ssb.ProgramModemPreset(
            1, "T39", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<InvalidOperationException>(() => Radio.Ssb.ProgramHopModemPreset(
            9, "DAT9", SyncMode.Async, DataMode.Remote, "300"));

        Assert.Empty(Transport.SentLines);

        // ANTI-VACUITY: the same calls go through the moment a prompt lands.
        ConfirmPrompt("SSB>");
        Radio.Ssb.QueryModemPreset(0);
        Assert.Equal(["MODEM PRE 0", "BAT ST"], Transport.SentLines);
    }

    /// <summary>
    /// AUDIT ROUND 2, MAJOR 2 — <b>a read window admits only ITS OWN band's
    /// rows.</b> The two bands are disjoint, so a row naming the other one
    /// cannot be an answer to this question; it is a straggler from the
    /// previous window. Left in, it was not merely stale but FALSE: a late
    /// preset-1 row inside a <c>HOP&gt;</c> listing committed
    /// <c>Enabled = [1]</c> under <c>Scope = Hop</c>, which every consumer
    /// reads as "7, 8 and 9 are all disabled".
    /// </summary>
    [Fact]
    public void AHopPresenceWindow_DISCARDS_ALateSsbRow_AndStillCommitsToTheHopBand()
    {
        ConnectReady();
        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPresetPresence();

        // The straggler: an `SSB>`-band row arriving inside the HOP listing.
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        AnswerSentinel();

        var presence = Radio.State.ModemPresetPresence;
        Assert.True(presence.Covers(OperatingMode.Hop));
        Assert.Empty(presence.Enabled);                  // NOT [1]
        Assert.DoesNotContain(1, presence.Enabled);
    }

    [Fact]
    public void AnSsbPresenceWindow_DISCARDS_ALateHopRow_TheMirrorCase()
    {
        ConnectReady();
        ConfirmPrompt("SSB>");
        Radio.Ssb.QueryModemPresetPresence();

        Transport.InjectLine("MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300   ");
        AnswerSentinel();

        var presence = Radio.State.ModemPresetPresence;
        Assert.True(presence.Covers(OperatingMode.Ssb));
        Assert.Empty(presence.Enabled);
    }

    [Fact]
    public void AHopPresenceWindow_ADMITS_ItsOwnBandsRows()
    {
        // The filter's other side: it must not eat the answers. Without this
        // the two pins above would pass on a window that admitted NOTHING.
        ConnectReady();
        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPresetPresence();

        Transport.InjectLine("MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300   ");
        Transport.InjectLine("MODEM PRESET 9 DAT9 ASYNC REMOTE BAUD 300   ");
        AnswerSentinel();

        Assert.Equal([7, 9], Radio.State.ModemPresetPresence.Enabled);
        Assert.True(Radio.State.ModemPresetPresence.Covers(OperatingMode.Hop));
    }

    [Fact]
    public void ATargetedWindow_DISCARDS_AnOutOfBandRow_FromTheFIELDSMirror()
    {
        // The same rule on the other window kind: a stray `MODEM PRESET 1 …`
        // during a HOP targeted read must not upsert into the fields mirror,
        // where it would then be read back as a HOP preset's row.
        ConnectReady();
        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPreset(9);

        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        Assert.Empty(Radio.State.ModemPresets);

        Transport.InjectLine("MODEM PRESET 9 DAT9 ASYNC REMOTE BAUD 300   ");
        Assert.Equal(["9 DAT9 ASYNC REMOTE BAUD 300"], Radio.State.ModemPresets);
        AnswerSentinel();
    }

    [Fact]
    public void WithNOWindowOpen_APresetLineStillUpserts_TheEchoPathIsUntouched()
    {
        // ANTI-OVERREACH: the band filter is a property of a WINDOW, and there
        // is no window here — a programming echo or a `MODEM SH` answer has no
        // question to be an answer to and takes the unfiltered path it always
        // did. Both bands, to prove the filter is not simply always-on.
        ConnectReady();
        ConfirmPrompt("HOP>");

        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        Transport.InjectLine("MODEM PRESET 9 DAT9 ASYNC REMOTE BAUD 300   ");

        Assert.Equal(
            ["1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long", "9 DAT9 ASYNC REMOTE BAUD 300"],
            Radio.State.ModemPresets);
    }

    [Fact]
    public void AQueuedTargetedBatch_REPLACES_ItsPresetsAcrossAScopeChange_NotUnions()
    {
        // The same family, found while fixing MAJOR 2: the queue COALESCES
        // targeted requests by unioning their preset sets, which is right
        // within a band and wrong across one — a batch queued at `SSB>` and
        // then re-requested at `HOP>` would have sent `MODEM PRE 0` at the HOP
        // prompt. The later request is the current one and wins outright.
        ConnectReady();                                  // shadowed: at SSB>
        Radio.Ssb.QueryModemPresetPresence();            // something owns the slot
        Radio.Ssb.QueryModemPreset(0);                   // an SSB batch QUEUES behind it

        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPreset(9);                   // …re-requested at the new band
        Transport.ClearSent();

        AnswerSentinel();                                // presence completes → batch promotes
        Assert.Equal(["MODEM PRE 9", "BAT ST"], Transport.SentLines);
    }

    [Fact]
    public void AQueuedTargetedBatch_REPLACES_ItsPresetsGoingHopToSsbToo_TheMirrorDirection()
    {
        // THE MIRROR OF THE PIN ABOVE, and it earns its place (audit round 3):
        // the rule is symmetric but a pin on ONE direction cannot say so. A
        // mutant that cleared the pending presets only when the NEW scope is
        // HOP leaves HOP→SSB as a union — `MODEM PRE 9` would go out at an
        // `SSB>` prompt, where it answers `INVALID MODEM PRESET` — and the
        // whole Core suite still passed. This is the case that convicts it.
        ConnectReady();
        ConfirmPrompt("HOP>");
        Radio.Ssb.QueryModemPresetPresence();            // something owns the slot
        Radio.Ssb.QueryModemPreset(9);                   // a HOP batch QUEUES behind it

        ConfirmPrompt("SSB>");
        Radio.Ssb.QueryModemPreset(0);                   // …re-requested at the new band
        Transport.ClearSent();

        AnswerSentinel();                                // presence completes → batch promotes
        Assert.Equal(["MODEM PRE 0", "BAT ST"], Transport.SentLines);
        Assert.DoesNotContain("MODEM PRE 9", Transport.SentLines);
    }

    [Fact]
    public void TheHopBaudVocabulary_IsExactlyTheProbedThree()
    {
        // The one place the {75,150,300} set is written down in Core, pinned
        // against P5c so a widening has to be a deliberate edit here.
        Assert.Equal(["75", "150", "300"], Wire.HopModemBauds);
    }

    [Fact]
    public void RefreshModemPresets_IsSevenTargetedReadsAndONESentinel()
    {
        // ONE operation, not seven: the closing sentinel is what tells the
        // presence read queued behind it that the targeted window is shut.
        ConnectReady();
        Radio.Ssb.RefreshModemPresets();

        Assert.Equal(
            ["MODEM PRE 0", "MODEM PRE 1", "MODEM PRE 2", "MODEM PRE 3",
             "MODEM PRE 4", "MODEM PRE 5", "MODEM PRE 6", "BAT ST"],
            Transport.SentLines);
    }

    [Fact]
    public void QueryModemPresetPresence_SendsTheBulkFormAndItsSentinel_AndNeverClearsTheFieldsMirror()
    {
        ConnectReady();
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        Assert.Single(Radio.State.ModemPresets);

        Radio.Ssb.QueryModemPresetPresence();

        Assert.Equal(["MODEM PRE", "BAT ST"], Transport.SentLines);
        // THE round-11 change: the fields mirror is untouched by the read.
        Assert.Single(Radio.State.ModemPresets);
    }

    [Fact]
    public void ProgramModemPreset_MinimalForm_IsTheShortTokenLine()
    {
        // Round 9: the ARGUMENT NAMES are session-15's captured write; the
        // VALUE tokens are the HELP screen's abbreviations (session-07,
        // "capital letters denote acceptable abbreviation"). Short-token
        // WRITE ACCEPTANCE is ASSUMED — bench item A6d round-trips it.
        ConnectReady();
        Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400");
        Assert.Equal(["MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400"], Transport.SentLines);
    }

    [Fact]
    public void ProgramModemPreset_Optionals_AppendInHelpOrder_WithShortTokens()
    {
        ConnectReady();
        Radio.Ssb.ProgramModemPreset(0, "fsk1", "fsk-a", "sync dat", "75",
            interleave: "alts", mark: "1575", space: "1425", enabled: false);
        Assert.Equal(
            ["MODEM PRESET 0 NAME FSK1 TYPE FSK-A SYNC DAT BAUD 75 INTERLEAV ALTS MARK 1575 SPACE 1425 DIS"],
            Transport.SentLines);

        // ENable is the other half of the DISable pin — both abbreviated.
        Transport.ClearSent();
        Radio.Ssb.ProgramModemPreset(6, "SE1", "SE", "ASYNC REM", "VO",
            interleave: "ZE", enabled: true);
        Assert.Equal(
            ["MODEM PRESET 6 NAME SE1 TYPE SE ASYNC REM BAUD VO INTERLEAV ZE EN"],
            Transport.SentLines);
    }

    [Fact]
    public void ProgramModemPreset_EveryTypeAndBaudTokenIsTheHelpAbbreviation()
    {
        // Each column pinned end to end, so a single renamed token fails
        // here rather than at the bench.
        ConnectReady();
        foreach (var type in new[] { "39TONE", "FSKW", "FSKN", "FSK-A", "FSK-V", "SE" })
            Radio.Ssb.ProgramModemPreset(1, "T39", type, "ASYNC DAT", "2400");
        foreach (var baud in new[] { "75", "150", "300", "600", "1200", "2400", "4800", "VO" })
            Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", baud);

        Assert.Equal(
        [
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE FSKW ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE FSKN ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE FSK-A ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE FSK-V ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE SE ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 75",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 150",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 300",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 600",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 1200",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 4800",
            "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD VO",
        ], Transport.SentLines);
    }

    [Fact]
    public void ProgramModemPreset_RejectsEverythingOutsideTheDocumentedShapes()
    {
        ConnectReady();
        // Preset 7 answers INVALID MODEM PRESET on this firmware — refused
        // client-side (the radio silently keeps nothing).
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramModemPreset(7, "T39", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ssb.ProgramModemPreset(-1, "T39", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "TOOLONG", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "", "39TONE", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "OFF", "39TONE", "ASYNC DAT", "2400"));   // unselectable name
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "40TONE", "ASYNC DAT", "2400"));
        // The round-8 LONG spellings are no longer wire tokens — pinning
        // their REFUSAL is what stops a silent revert to the old vocabulary.
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "FSKWS", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "SERIAL", "ASYNC DAT", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DATA", "2400"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC", "2400"));
        // BAUD is a DISCRETE set, not a 75-4800 range: an in-range value that
        // is not one of the eight is refused, and so is VOICE spelled out.
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "1000"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2401"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "74"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "4801"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "VOICE"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", ""));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400", interleave: "LONG"));
        // MARK without SPACE (and the reverse) is undefined — refused.
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "FSK-A", "ASYNC DAT", "2400", mark: "1575"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "FSK-A", "ASYNC DAT", "2400", space: "1425"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.ProgramModemPreset(1, "T39", "FSK-A", "ASYNC DAT", "2400", mark: "15.75", space: "1425"));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void DeviceSettings_SendDocumentedForms()
    {
        ConnectReady();
        Radio.SetBacklightFunction(BacklightFunction.Off);
        Radio.SetBacklightFunction(BacklightFunction.Momentary);
        Radio.SetBacklightIntensity(0);
        Radio.SetBacklightIntensity(8);
        Radio.SetContrast(3);

        // CLONE ROUND 12 §9 C2 — BOTH are ZERO-PADDED to two digits.
        // `INT` unpadded is silently ineffective (OWNER-VERIFIED at the
        // bench: the backlight only moves on a two-digit value), and `CONT`
        // shares the same helper. P-2 step b settled CONT on the real radio
        // (bench/transcripts/r12-p2-*, 2026-08-18): `CONT 05` echoed
        // `CONTRAST 05` and read back `05` — the GREEN branch — so the helper
        // stayed shared and both pad. This assertion IS the branch record.
        Assert.Equal(
            ["LIG OFF", "LIG MOMENTARY", "INT 00", "INT 08", "CONT 03"],
            Transport.SentLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SetBacklightIntensity(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SetContrast(-1));
    }

    /// <summary>UI-tweaks round 4 (AC / R4-Q1): the device READ set. The bare
    /// forms are the old app's (`Configuration.cs:41-43` →
    /// `Prc138Radio.cs:997-999`); `LIG` and `CONT` are additionally this
    /// project's own sentinel-probe facts, `INT` is old-app-derived only
    /// (docs/protocol.md round-4 provisional subsection, bench item). Bare
    /// tokens, no arity — a query that grew an argument would be a set.</summary>
    [Fact]
    public void DeviceQueries_SendTheOldAppDerivedForms()
    {
        ConnectReady();
        Radio.QueryBacklightFunction();
        Radio.QueryBacklightIntensity();
        Radio.QueryContrast();

        Assert.Equal(["LIG", "INT", "CONT"], Transport.SentLines);
    }

    // ---- Phase R: ALE settings builders --------------------------------------

    [Fact]
    public void AleSettings_SendDocumentedForms()
    {
        ConnectReady();
        Radio.Ale.SetAllCall(OnOff.On);
        Radio.Ale.SetAnyCall(OnOff.Off);
        Radio.Ale.SetAmdDisplay(OnOff.On);
        Radio.Ale.SetKeyToCall(OnOff.Off);
        Radio.Ale.SetListenBeforeTx(OnOff.On);
        Radio.Ale.SetRadioSilence(OnOff.Off);
        Radio.Ale.SetMaxScanChannels(0);
        Radio.Ale.SetMaxScanChannels(100);
        Radio.Ale.SetLinkTimeout(0);          // 0 measured VALID (session-18)
        Radio.Ale.SetLinkTimeout(60);
        Radio.Ale.SetTuneTime(1);
        Radio.Ale.SetTuneTime(60);

        Assert.Equal(
            ["ALL_C ON", "ANY_C OFF", "AMD_D ON", "KEY_T OFF", "LSTN ON",
             "RAD_S OFF", "MAXCH 0", "MAXCH 100", "TIME_OU 0", "TIME_OU 60",
             "TUNE 1", "TUNE 60"],
            Transport.SentLines);

        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.SetMaxScanChannels(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.SetLinkTimeout(61));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.SetTuneTime(0));
    }

    [Fact]
    public void PhaseRSetters_BeforeConnect_AreNotSent()
    {
        Radio.Ssb.SetSquelch(OnOff.On);
        Radio.Ale.SetAllCall(OnOff.On);
        Radio.SetContrast(4);
        Radio.Hop.SetNetId(0, "12345678");
        Assert.Empty(Transport.SentLines);
    }

    // ---- Phase R: fill editing (E2 — backend in, GUI out) --------------------

    [Fact]
    public void AleFillEditing_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Ale.SetSelfAddress("cam", 1);
        Radio.Ale.SetIndividualAddress("bob", 1, "cam");
        Radio.Ale.SetNetAddress("NT1", 2, "CAM");
        Radio.Ale.AddNetMember("NT1", "BOB");
        Radio.Ale.DeleteAddress("bob");
        Radio.Ale.AddScanChannel(1, 0);
        Radio.Ale.DeleteScanChannel(1, 5);

        Assert.Equal(
            ["SLFAD CAM 1", "INDAD BOB 1 CAM", "NETAD NT1 2 CAM",
             "ADDM NT1 BOB", "DELAD BOB", "ADDC 1 00", "DELC 1 05"],
            Transport.SentLines);
    }

    [Fact]
    public void AleFillEditing_ValidationRejectsBeforeTheWire()
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetSelfAddress("SIXTEENCHARSELF!", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.SetSelfAddress("CAM", 10));
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetIndividualAddress("BOB", 1, "SIXTEENCHARSELF!"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.AddNetMember("", "BOB"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.AddScanChannel(1, 100));
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>Round 10 §7 (owner ruling 3): the SELF-ADDRESS BOUND, pinned
    /// at both edges and by its message.
    ///
    /// <para>The old bound was 1-3, read off the radio's fill-gate prompt
    /// (<c>PRG 1-3 CHAR SLF</c>) — a gate line, not a stored-length limit.
    /// The client bound is now 1-15 like every other ALE address, so the
    /// RADIO decides: 15 must reach the wire, 16 must not, and the refusal
    /// must name the new bound. PROVISIONAL — the true maximum is a pending
    /// bench probe (§12); if it measures lower, this test moves with the
    /// code.</para>
    ///
    /// <para>All three ValidateSelf call sites are covered, so a partial
    /// relaxation (one site loosened, another left at 3) fails here.</para>
    /// </summary>
    [Fact]
    public void SelfAddressBound_IsOneToFifteen_Provisional()
    {
        ConnectReady();

        const string fifteen = "FIFTEENCHARSELF";     // exactly at the bound
        const string sixteen = "SIXTEENCHARSELF!";    // one over
        Assert.Equal(15, fifteen.Length);
        Assert.Equal(16, sixteen.Length);

        // At the bound: every ValidateSelf caller reaches the wire.
        Radio.Ale.SetSelfAddress(fifteen, 1);
        Radio.Ale.SetIndividualAddress("BOB", 1, fifteen);
        Radio.Ale.SetNetAddress("NT1", 1, fifteen);
        Radio.Ale.StartSounding(fifteen);
        Radio.Ale.StopSounding(fifteen);

        Assert.Equal(
            [$"SLFAD {fifteen} 1", $"INDAD BOB 1 {fifteen}", $"NETAD NT1 1 {fifteen}",
             $"SOU STA {fifteen}", $"SOU STO {fifteen}"],
            Transport.SentLines);

        // One over: refused before the wire, by the message the bound states.
        Transport.ClearSent();
        foreach (var refuse in new Action[]
        {
            () => Radio.Ale.SetSelfAddress(sixteen, 1),
            () => Radio.Ale.SetIndividualAddress("BOB", 1, sixteen),
            () => Radio.Ale.SetNetAddress("NT1", 1, sixteen),
            () => Radio.Ale.StartSounding(sixteen),
            () => Radio.Ale.StopSounding(sixteen),
        })
        {
            var ex = Assert.Throws<ArgumentException>(refuse);
            Assert.StartsWith("ALE self address must be 1-15 characters.", ex.Message);
        }

        // Empty is still refused at the low edge.
        Assert.Throws<ArgumentException>(() => Radio.Ale.SetSelfAddress("", 1));
        Assert.Empty(Transport.SentLines);
    }

    // ---- X8: the scan channel-group reads + the sentinel barrier ------------
    // plan-ale-programming.md §4.1. Wire forms are byte-pinned: "CHG <g>" is
    // the confirmed query (protocol.md programming table) and the sentinel is
    // the same BAT ST every refresh already closes with.

    [Fact]
    public void RequestChannelGroup_SendsTheQueryAndItsSentinel()
    {
        ConnectReady();
        long readId = Radio.Ale.RequestChannelGroup(3);

        Assert.Equal(["CHG 3", "BAT ST"], Transport.SentLines);
        Assert.True(readId > 0);
        // Every line a group read emits also passes the forbidden set — the
        // sweep's own list cannot prove it for BOTH group readers (the second
        // coalesces by design), so it is proven here.
        foreach (var line in Transport.SentLines)
            Assert.False(IsForbiddenLine(line), "Forbidden wire form: " + line);
    }

    [Fact]
    public void RefreshChannelGroups_SendsTenQueriesAndOneSentinel()
    {
        ConnectReady();
        Radio.Ale.RefreshChannelGroups();

        Assert.Equal(
            ["CHG 0", "CHG 1", "CHG 2", "CHG 3", "CHG 4", "CHG 5", "CHG 6",
             "CHG 7", "CHG 8", "CHG 9", "BAT ST"],
            Transport.SentLines);
        foreach (var line in Transport.SentLines)
            Assert.False(IsForbiddenLine(line), "Forbidden wire form: " + line);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void RequestChannelGroup_OutOfRange_ThrowsAndSendsNothing(int group)
    {
        // Validation is the ONLY defense: a "CHG 10" would be a command the
        // radio has no answer shape for, and the read would never commit.
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.RequestChannelGroup(group));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Synchronize_EmitsOnlyTheSentinel_AndCompletesWithItsOwnId()
    {
        ConnectReady();
        long syncId = Radio.Ale.Synchronize();

        Assert.Equal(["BAT ST"], Transport.SentLines);
        Assert.Equal(default, Radio.State.Ale.LastSync);      // nothing yet

        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(syncId, true), Radio.State.Ale.LastSync);
    }

    [Fact]
    public void Synchronize_NeverDefersIntoTheStoreQueues_AndCompletesInCallOrder()
    {
        // The barrier is INDEPENDENT of the two STORE queues (§4.1): with a
        // book read active, a second book read COALESCES (sends nothing and
        // shares the pending id) while every Synchronize gets its own id, its
        // own sentinel and its own completion, in call order.
        //
        // Recorded, because the app-layer bracket is built on it: the wire
        // dispatch is still serialized by Core's single-outstanding-ping
        // queue (Prc138Radio Q3) — only the HEAD's BAT ST is on the wire, so
        // a barrier queued behind another sentinel is SENT when that one
        // answers. Ordering and completion are unaffected; what it means for
        // the programming bracket is recorded on AleProgrammingGate.
        ConnectReady();
        Radio.Ale.RefreshStationList();
        Transport.ClearSent();

        Radio.Ale.RefreshStationList();      // same store: coalesces, sends nothing
        Assert.Empty(Transport.SentLines);

        long first = Radio.Ale.Synchronize();
        long second = Radio.Ale.Synchronize();
        Assert.NotEqual(first, second);      // never coalesced into one barrier

        AnswerSentinel();                    // the book read's sentinel
        // The first barrier goes out, and the COALESCED book operation is
        // promoted behind it (its three listings, then its own sentinel).
        Assert.Equal(["BAT ST", "SLFAD", "INDAD", "NETAD"], Transport.SentLines);
        Assert.Equal(default, Radio.State.Ale.LastSync);      // barrier not answered yet

        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(first, true), Radio.State.Ale.LastSync);

        AnswerSentinel();
        Assert.Equal(new AleReadCompletion(second, true), Radio.State.Ale.LastSync);
    }

    /// <summary>The X8 prefix swap, with its non-vacuity proof: the un-built
    /// whole-list SET form stays forbidden to every sender, while the group
    /// query the two readers emit passes. Deleting "CHGROUP " from the
    /// forbidden list fails this test; putting the old bare "CHG" back fails
    /// it too (it would catch the query).</summary>
    [Fact]
    public void X8_ChgroupSetForm_StaysForbidden_WhileTheGroupQueryPasses()
    {
        Assert.Contains("CHGROUP ", ForbiddenWirePrefixes);
        Assert.DoesNotContain("CHG", ForbiddenWirePrefixes);

        // The synthetic set form — no builder emits it, and this is what
        // stops one from being added quietly.
        Assert.True(IsForbiddenLine("CHGROUP 1 00"));
        Assert.True(IsForbiddenLine("CHGROUP 1 00 01 02"));

        // …and the real query forms pass.
        Assert.False(IsForbiddenLine("CHG 3"));
        Assert.False(IsForbiddenLine("CHG 0"));
    }

    // ---- Round 11 §9A (X10): the stored TX message store ---------------------

    [Fact]
    public void MessageStoreBuilders_SendExactlyTheDocumentedForms()
    {
        ConnectReady();
        Radio.Ale.QueryTxMessages();
        Radio.Ale.StoreTxMessage(0, "RENDEZVOUS AT NOON");
        Radio.Ale.DeleteTxMessage(9);
        Radio.Ale.ForgetStoredMessages();      // sends NOTHING

        Assert.Equal(
            ["TXMSG", "TXMSG 0 RENDEZVOUS AT NOON", "TXMSG DEL 9"],
            Transport.SentLines);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void MessageStoreBuilders_RejectSlotsOutsideZeroToNine_AndSendNothing(int slot)
    {
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.StoreTxMessage(slot, "HI"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Ale.DeleteTxMessage(slot));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HI\rZERO")]                   // the injection carrier
    [InlineData("HI\nERASE")]
    public void StoreTxMessage_RejectsBadText_AndSendsNothing(string text)
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.StoreTxMessage(3, text));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void StoreTxMessage_RejectsTextOverNinetyCharacters_AndSendsNothing()
    {
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.StoreTxMessage(3, new string('A', 91)));
        Assert.Empty(Transport.SentLines);
        // …and the boundary itself is accepted, so the bound is a bound and
        // not an off-by-one.
        Radio.Ale.StoreTxMessage(3, new string('A', 90));
        Assert.Single(Transport.SentLines);
    }

    [Fact]
    public void EraseAllAddresses_SendsExactlyTheDocumentedForm()
    {
        // ERASE destroys every ALE address (protocol.md hazard table). It WAS
        // token-gated in Core, like Zeroize in the old repo; round 10 §5
        // (owner ruling 9) removed that gate — confirmation for this
        // destructive-DATA sender is the GUI's popup now, and Core executes
        // what it is told. What contains it here is the wire form below, the
        // SIGNATURE pin beneath that, and the untouched forbidden-prefix sweep
        // ("ERASE" is still a forbidden prefix for every swept sender).
        ConnectReady();
        Radio.Ale.EraseAllAddresses();
        Assert.Equal(["ERASE"], Transport.SentLines);
    }

    /// <summary>Round 10 §5: the erase token removal, pinned as a SIGNATURE —
    /// the same mechanism P1 used for <c>SetRemoteBaud</c>, and for the same
    /// reason. The public-surface whitelist counts overload NAMES, so it
    /// catches an ADDED overload but NOT an arity change in place: restoring
    /// <c>(string)</c> instead of <c>()</c> is still exactly one method called
    /// EraseAllAddresses. This asserts the PARAMETER LIST.</summary>
    [Fact]
    public void EraseAllAddresses_HasExactlyOneOverload_TakingNoParameters()
    {
        var overloads = typeof(Falcon.Core.Modes.AleController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(Falcon.Core.Modes.AleController.EraseAllAddresses))
            .ToList();

        var method = Assert.Single(overloads);
        Assert.Empty(method.GetParameters());
    }

    /// <summary>The other half of §5's SCOPING, pinned in the same file as the
    /// two removals: the THREE TRANSMIT-HAZARD token gates are UNTOUCHED.
    /// Round 10 moved confirmation to the GUI for the two destructive-DATA
    /// senders only (ERASE and the baud change); a keying or test-transmit
    /// command still refuses without its literal "TRANSMIT" token, in Core,
    /// where a GUI cannot be the last line of defence.</summary>
    [Fact]
    public void TheThreeTransmitTokenGates_StillTakeTheirToken_AndStillRefuseWithoutIt()
    {
        ConnectReady();

        Assert.Throws<ArgumentException>(() => Radio.Ssb.SetKeyline(OnOff.On, "yes"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelfTest("yes"));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.VswrTest("yes"));
        Assert.Empty(Transport.SentLines);

        // …and each still HAS the string parameter round 10 deliberately left
        // in place (an arity change here would be the smuggled scope creep).
        var ssb = typeof(Falcon.Core.Modes.SsbController);
        Assert.Contains(typeof(string),
            ssb.GetMethod(nameof(Falcon.Core.Modes.SsbController.SelfTest))!
               .GetParameters().Select(p => p.ParameterType));
        Assert.Contains(typeof(string),
            ssb.GetMethod(nameof(Falcon.Core.Modes.SsbController.VswrTest))!
               .GetParameters().Select(p => p.ParameterType));
        Assert.Contains(
            ssb.GetMethods().Where(m => m.Name == nameof(Falcon.Core.Modes.SsbController.SetKeyline)),
            m => m.GetParameters().Any(p => p.ParameterType == typeof(string)));
    }

    // ---- Phase R: HOP net programming (E3 — backend in, GUI out) -------------

    [Fact]
    public void HopNetProgramming_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.Hop.SetNetId(0, "12345678");
        Radio.Hop.SetHopType(0, HopType.Narrowband);
        Radio.Hop.SetHopType(1, HopType.Wideband);
        Radio.Hop.SetHopType(3, HopType.List);
        Radio.Hop.SetNarrowbandHopset(0, "11565");
        Radio.Hop.SetWidebandHopset(1, "02000", "03000");
        Radio.Hop.DeleteHopset(0);
        Radio.Hop.AddHopListFrequencies(3, "11010", "11015", "11020");
        Radio.Hop.DeleteHopListFrequency(3, "11010");
        Radio.Hop.QueryHopList(3);
        Radio.Hop.SetExcludeBand(0, "02000000", "03000000");
        Radio.Hop.QueryExcludeBands();
        Radio.Hop.DeleteExcludeBand(0);
        Radio.Hop.DeleteAllExcludeBands();
        Radio.Hop.GenerateHopset();

        Assert.Equal(
            ["NETID 0 12345678", "HOPTYPE 0 NB", "HOPTYPE 1 WB", "HOPTYPE 3 LIST",
             "HOPSET 0 11565", "HOPSET 1 02000 03000", "HOPSET 0 DEL",
             "HOPLIST 3 ADD 11010 11015 11020", "HOPLIST 3 DEL 11010", "HOPLIST 3",
             // Round 11 §8: the EXCLUDE query is now sentinel-scoped, so its
             // closing "BAT ST" is part of the documented form.
             "EXC 0 02000000 03000000", "EXC", "BAT ST", "EXC 0 DEL", "EXC DEL", "DOIT"],
            Transport.SentLines);
    }

    [Fact]
    public void HopNetProgramming_ValidationRejectsWhatTheRadioSilentlyIgnores()
    {
        // Wrong hop-frequency formats are SILENTLY IGNORED by the radio
        // (protocol.md) — client validation is the only defense.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetNetId(0, "1234567"));      // 7 digits
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetNarrowbandHopset(0, "1156"));   // 4 digits
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetNarrowbandHopset(0, "11563")); // last digit not 0/5
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.SetNarrowbandHopset(0, "01595")); // below band
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.SetNarrowbandHopset(0, "30000")); // above band
        Assert.Throws<ArgumentException>(() => Radio.Hop.AddHopListFrequencies(3));    // no freqs
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetExcludeBand(0, "2000000", "03000000")); // 7-digit Hz
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.SetNetId(10, "12345678"));
        Assert.Empty(Transport.SentLines);
    }

    // ---- Phase R: crypto (E1 — backend in, GUI out) --------------------------

    [Fact]
    public void Crypto_SendsDocumentedForms()
    {
        ConnectReady();
        Radio.SetEncryption(OnOff.On);
        Radio.SetEncryption(OnOff.Off);
        Radio.SetEncryptionKey(1, "123456789012");
        Radio.ClearEncryptionKey(1);
        Radio.SelectEncryptionKey(6);

        Assert.Equal(
            ["ENCR ON", "ENCR OFF", "ENC_KEY 1 123456789012", "ENC_KEY 1 CLEAR",
             "USE_KEY 6"],
            Transport.SentLines);
    }

    [Fact]
    public void Crypto_ValidationRejectsBeforeTheWire()
    {
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SetEncryptionKey(0, "123456789012"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SetEncryptionKey(7, "123456789012"));
        Assert.Throws<ArgumentException>(() => Radio.SetEncryptionKey(1, "12345678901"));    // 11 digits
        Assert.Throws<ArgumentException>(() => Radio.SetEncryptionKey(1, "12345678901A"));   // non-digit
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SelectEncryptionKey(0));
        Assert.Empty(Transport.SentLines);
    }

    // ---- Phase R: transmit-gated diagnostics + keyline (E5 / hazard table) ---

    [Fact]
    public void QueryFirmwareVersions_SendsTe3_TheSafeForm()
    {
        ConnectReady();
        Radio.Ssb.QueryFirmwareVersions();
        Assert.Equal(["TE 3"], Transport.SentLines);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("transmit")]
    [InlineData("")]
    [InlineData(" TRANSMIT")]
    public void SelfTestAndVswr_WrongToken_ThrowAndSendNothing(string token)
    {
        // TE and TE 4 TRANSMIT (protocol.md hazard table) — gated like
        // Zeroize was in the old repo, on the literal token "TRANSMIT". Two of
        // the three TRANSMIT-hazard gates round 10 §5 left untouched.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SelfTest(token));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.VswrTest(token));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SelfTestAndVswr_WithToken_SendExactlyTheDocumentedForms()
    {
        ConnectReady();
        Radio.Ssb.SelfTest("TRANSMIT");
        Radio.Ssb.VswrTest("TRANSMIT");
        Assert.Equal(["TE", "TE 4"], Transport.SentLines);
    }

    [Fact]
    public void KeylineOn_RequiresTheTransmitToken_OffDoesNot()
    {
        // K ON transmits and STAYS KEYED until turned off (protocol.md
        // hazard table). Gating ON is a W1 judgment call extending the
        // tasked TE gates — recorded in plan/phase-r-classification.md.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SetKeyline(OnOff.On));
        Assert.Throws<ArgumentException>(() => Radio.Ssb.SetKeyline(OnOff.On, "yes"));
        Assert.Empty(Transport.SentLines);

        Radio.Ssb.SetKeyline(OnOff.On, "TRANSMIT");
        Radio.Ssb.SetKeyline(OnOff.Off);
        Assert.Equal(["K ON", "K OFF"], Transport.SentLines);
    }

    /// <summary>Every gated / GUI-out builder's output is still CAUGHT by
    /// the forbidden list (generalizing the Stage 11 PORT_R BAUD pin): the
    /// builders are excluded from the wire sweep, so this is what proves a
    /// NEW unauthorized sender of any of these forms would fail the sweep.</summary>
    [Fact]
    public void GatedAndGuiOutBuilders_AreExactlyTheForbiddenPrefixCatchSet()
    {
        ConnectReady();
        Radio.Ssb.SetKeyline(OnOff.On, "TRANSMIT");
        Radio.Ssb.SetKeyline(OnOff.Off);
        Radio.Ssb.SelfTest("TRANSMIT");
        Radio.Ssb.VswrTest("TRANSMIT");
        Radio.SetEncryption(OnOff.On);
        Radio.SetEncryptionKey(1, "123456789012");
        Radio.ClearEncryptionKey(1);
        Radio.SelectEncryptionKey(1);
        Radio.Ale.SetSelfAddress("CAM", 1);
        Radio.Ale.SetIndividualAddress("BOB", 1, "CAM");
        Radio.Ale.SetNetAddress("NT1", 1, "CAM");
        Radio.Ale.AddNetMember("NT1", "BOB");
        Radio.Ale.DeleteAddress("BOB");
        Radio.Ale.AddScanChannel(1, 0);
        Radio.Ale.DeleteScanChannel(1, 0);
        Radio.Ale.EraseAllAddresses();
        Radio.Hop.SetNetId(0, "12345678");
        Radio.Hop.SetHopType(0, HopType.Narrowband);
        Radio.Hop.SetNarrowbandHopset(0, "11565");
        Radio.Hop.SetWidebandHopset(0, "02000", "03000");
        Radio.Hop.DeleteHopset(0);
        Radio.Hop.AddHopListFrequencies(0, "11010", "11015", "11020");
        Radio.Hop.DeleteHopListFrequency(0, "11010");
        Radio.Hop.QueryHopList(0);
        // X9 (round 11) un-guarded the three EXC builders for the app layer,
        // but that changed nothing at the WIRE level: the SET and the per-band
        // DEL still emit "EXC …", which is still forbidden to every swept
        // sender, so both stay here. DeleteAllExcludeBands stays GUARDED
        // outright (X9_DeleteAllExcludeBands_StaysGuarded).
        Radio.Hop.SetExcludeBand(0, "02000000", "03000000");
        Radio.Hop.DeleteExcludeBand(0);
        Radio.Hop.DeleteAllExcludeBands();
        Radio.Hop.GenerateHopset();
        Radio.SetRemoteBaud(4800);
        // CLONE ROUND 12: the X12 SET and the X13 ZEROIZE. Their wire forms
        // ("PROGRAM …", "SELECT …", "ZERO") were ALREADY forbidden prefixes
        // before this round — which is exactly why they belong here rather than
        // in the sweep: the guard list did not have to grow, and any NEW sender
        // of these forms still fails it.
        //
        // QueryLockouts is NOT called here: it is an OPERATION, so it also
        // emits its closing "BAT ST", which is (rightly) not forbidden. Its two
        // report commands are held to the same standard by
        // X12_TheLockoutReportCommands_AreStillForbiddenToEveryOtherSender.
        Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "CHAN", LockState.Lock);
        Radio.Ssb.SetLockout(LockoutFamily.Select, LockoutSection.Hop, "KEY", LockState.Unlock);
        Radio.ZeroizeSettleTimeoutMs = 30;   // arm-and-fault fast; nothing may outlive the test
        Radio.ZeroizeSettlePollMs = 10_000;  // …and no bare-CR poll inside it
        Radio.Ssb.ZeroizeRadio();
        // The bare-"EXC" READ is NOT here: X9 made it safe surface and it
        // joins the wire sweep instead — no prefix can catch it without
        // catching nothing else, and it is a harmless read.

        Assert.NotEmpty(Transport.SentLines);
        foreach (var line in Transport.SentLines)
            Assert.True(IsForbiddenLine(line),
                "Gated/GUI-out builder emitted a line the forbidden list does not catch: " + line);
    }

    // ---- Stage 11: the whitelisted PORT_R BAUD builder (plan §7 decision 3) --
    // PORT_R BAUD ends the session immediately (protocol.md hazard table).
    // It is whitelisted as the ONE deliberate exception to the
    // session-ending-commands exclusion.
    //
    // UI TWEAKS ROUND 10 (§5, owner ruling 9): the CONFIRMATION TOKEN
    // parameter is REMOVED — for this destructive-DATA sender the GUI owns
    // confirmation and Core executes. The wrong-token / null-token pins are
    // therefore deleted; the SIGNATURE ITSELF is now pinned by reflection
    // below, which is what stops the token creeping back (or a second
    // overload being added beside it). The three TRANSMIT-hazard token gates
    // (SetKeyline TRANSMIT / SelfTest / VswrTest) are UNTOUCHED and keep
    // their own pins above.

    [Fact]
    public void SetRemoteBaud_SendsExactlyTheDocumentedForm()
    {
        ConnectReady();
        Radio.SetRemoteBaud(4800);
        Assert.Equal(["PORT_R BAUD 4800"], Transport.SentLines);
    }

    /// <summary>Round 10 §5: the token removal, pinned as a SIGNATURE.
    ///
    /// <para>The public-surface whitelist above counts overload NAMES — one
    /// entry per overload — so it catches an ADDED overload: a second
    /// <c>SetRemoteBaud</c> beside the clean one makes the name count 2 against
    /// its 1 and fails the whitelist (verified by mutation, P1 audit). What it
    /// cannot see is an ARITY CHANGE that leaves the count alone: restoring
    /// <c>(int, string)</c> IN PLACE of <c>(int)</c> is still exactly one
    /// method called SetRemoteBaud, and the whitelist stays green while the
    /// removed token gate is back (also verified by mutation — the whole Core
    /// suite passed except this pin). That is the gap this pin closes: it
    /// asserts the PARAMETER LIST, not the name — <c>SetRemoteBaud</c> exists
    /// exactly once and takes exactly <c>(int)</c>.</para></summary>
    [Fact]
    public void SetRemoteBaud_HasExactlyOneOverload_TakingExactlyAnInt()
    {
        var overloads = typeof(Prc138Radio)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(Prc138Radio.SetRemoteBaud))
            .ToList();

        var method = Assert.Single(overloads);
        Assert.Equal(
            [typeof(int)],
            method.GetParameters().Select(p => p.ParameterType));
    }

    [Theory]
    [InlineData(75)]          // radio-supported but out of the app's set
    [InlineData(1200)]
    [InlineData(0)]
    [InlineData(19200)]
    public void SetRemoteBaud_UnsupportedRate_ThrowsAndSendsNothing(int baud)
    {
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.SetRemoteBaud(baud));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SetRemoteBaud_BeforeConnect_SendsNothing()
    {
        Radio.SetRemoteBaud(4800);   // rate is valid; the closed port drops it
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>The wire-sweep exception is itself pinned: "PORT_R BAUD"
    /// REMAINS a forbidden prefix, and the whitelisted builder's own output is
    /// exactly the line that prefix catches — so a NEW unauthorized PORT_R
    /// BAUD sender exercised by the sweep still fails it, and removing the
    /// prefix from the forbidden list fails THIS test. (Round 10 removed the
    /// TOKEN, not the wire-level containment: this is now the only structural
    /// stop on a casual second sender, so it matters more, not less.)</summary>
    [Fact]
    public void WireSweepException_PortRBaud_IsStillForbiddenForEveryOtherSender()
    {
        Assert.Contains("PORT_R BAUD", ForbiddenWirePrefixes);

        ConnectReady();
        Radio.SetRemoteBaud(4800);
        var line = Assert.Single(Transport.SentLines);
        Assert.Contains(ForbiddenWirePrefixes,
            prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // ====================================================================
    // Round 11 §8 — the two NEW ALE read stores' wire forms, and X9.
    // ====================================================================

    [Fact]
    public void ReadNetMembers_SendsExactlyTheTargetedQueryAndItsSentinel()
    {
        // The TARGETED net read — the only way to read membership at all
        // (bulk NETAD hides the MEMBER lines). Uppercased like every other
        // address the surface sends.
        ConnectReady();
        long readId = Radio.Ale.ReadNetMembers("nt1");

        Assert.Equal(["NETAD NT1", "BAT ST"], Transport.SentLines);
        Assert.NotEqual(0, readId);
    }

    /// <summary>The wire-sweep exclusion is itself pinned (the PORT_R BAUD
    /// pattern): "NETAD " REMAINS a forbidden prefix, and the targeted read's
    /// own line is exactly what that prefix catches — so a NEW unauthorized
    /// NETAD sender exercised by the sweep still fails it, and removing the
    /// prefix fails THIS test. A targeted READ and a net WRITE cannot be told
    /// apart by prefix, which is precisely why the read is swept by hand.</summary>
    [Fact]
    public void WireSweepException_TargetedNetadRead_IsStillForbiddenForEveryOtherSender()
    {
        Assert.Contains("NETAD ", ForbiddenWirePrefixes);

        ConnectReady();
        Radio.Ale.QueryNetAddresses("NT1");
        var line = Assert.Single(Transport.SentLines);
        Assert.True(IsForbiddenLine(line));

        // …and the BARE listing form is NOT caught — the query leg of every
        // book refresh has to keep passing the sweep.
        Transport.ClearSent();
        Radio.Ale.QueryNetAddresses();
        Assert.False(IsForbiddenLine(Assert.Single(Transport.SentLines)));
    }

    [Fact]
    public void ReadNetMembers_RejectsAControlCharArgument_AndSendsNothing()
    {
        // Same injection guard every free-string ALE builder carries: the read
        // must not be the one hole in it.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Ale.ReadNetMembers("NT1\rZERO"));
        Assert.Throws<ArgumentException>(() => Radio.Ale.QueryNetAddresses("NT1\rERASE"));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ReadLqaSchedules_SendsBareExchAndItsSentinel()
    {
        // Bare EXCH ≡ bare SOU (identical listing), so there is ONE builder.
        ConnectReady();
        long readId = Radio.Ale.ReadLqaSchedules();

        Assert.Equal(["EXCH", "BAT ST"], Transport.SentLines);
        Assert.NotEqual(0, readId);
    }

    // ---- X9: the three EXC builders (plan §7, owner ruling R11) ------------

    [Fact]
    public void X9_TheThreeExcludeBuilders_SendTheDocumentedForms()
    {
        // 8-digit Hz IN (the kHz echo comes back OUT — the asymmetry is the
        // radio's, captured 2026-08-17).
        ConnectReady();
        Radio.Hop.SetExcludeBand(0, "02000000", "03000000");
        Radio.Hop.DeleteExcludeBand(9);
        Radio.Hop.QueryExcludeBands();

        Assert.Equal(["EXC 0 02000000 03000000", "EXC 9 DEL", "EXC", "BAT ST"],
            Transport.SentLines);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void X9_ExcludeBandNumber_IsValidated_AndNothingIsSent(int band)
    {
        ConnectReady();
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.SetExcludeBand(band, "02000000", "03000000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Radio.Hop.DeleteExcludeBand(band));
        Assert.Empty(Transport.SentLines);
    }

    [Theory]
    [InlineData("2000000")]     // 7 digits
    [InlineData("020000000")]   // 9 digits
    [InlineData("0200000X")]
    public void X9_ExcludeFrequencies_MustBeExactlyEightHzDigits(string bad)
    {
        // The EXC family is the 5-digit rule's SIBLING: wrong widths are
        // silently mis-stored by the radio, so the client is the only defense.
        ConnectReady();
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetExcludeBand(0, bad, "03000000"));
        Assert.Throws<ArgumentException>(() => Radio.Hop.SetExcludeBand(0, "02000000", bad));
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void X9_DeleteAllExcludeBands_StaysGuarded_AndKeepsItsForbiddenForm()
    {
        // X9 un-guards EXACTLY three builders. The whole-table wipe is not one
        // of them: no screen asks for it, so it stays GUI-out (the source scan
        // in GuiOutScopeGuardTests) and its wire form stays forbidden here.
        ConnectReady();
        Radio.Hop.DeleteAllExcludeBands();
        var line = Assert.Single(Transport.SentLines);
        Assert.Equal("EXC DEL", line);
        Assert.True(IsForbiddenLine(line));
    }

    // ====================================================================
    // CLONE ROUND 12 — X12 (operator lockouts) and X13 (zeroize).
    // Every wire form below is the 2026-08-18 capture
    // (bench/transcripts/r11-lockouts-* and r12-p1-*).
    // ====================================================================

    [Fact]
    public void X12_QueryLockouts_SendsBothGlobalReportsAndOneClosingSentinel()
    {
        // ONE operation, not two reads: both reports are GLOBAL and answer from
        // the same prompt (captured), so one sentinel brackets the pair. A
        // second sentinel would mean two commit points for one table.
        ConnectReady();
        long id = Radio.Ssb.QueryLockouts();

        Assert.Equal(["PROGRAM", "SELECT", "BAT ST"], Transport.SentLines);
        Assert.True(id > 0);
    }

    [Fact]
    public void X12_QueryLockouts_Coalesces_WhileOneIsOnTheWire()
    {
        ConnectReady();
        long first = Radio.Ssb.QueryLockouts();
        Transport.ClearSent();

        long second = Radio.Ssb.QueryLockouts();
        Assert.Empty(Transport.SentLines);      // nothing new on the wire
        Assert.NotEqual(first, second);         // …but the caller gets its own id
    }

    [Theory]
    [InlineData(LockoutFamily.Program, LockoutSection.Ssb, "CHAN", LockState.Lock, "PROGRAM CHAN LOCK")]
    [InlineData(LockoutFamily.Program, LockoutSection.Hop, "TX_POWER", LockState.Unlock, "PROGRAM TX_POWER UNLOCK")]
    [InlineData(LockoutFamily.Program, LockoutSection.Eam, "CHGROUP", LockState.Lock, "PROGRAM CHGROUP LOCK")]
    [InlineData(LockoutFamily.Select, LockoutSection.Ssb, "TMP_CHAN", LockState.Unlock, "SELECT TMP_CHAN UNLOCK")]
    [InlineData(LockoutFamily.Select, LockoutSection.Eam, "KEY", LockState.Lock, "SELECT KEY LOCK")]
    public void X12_SetLockout_SendsTheCapturedForm_AndNeverNamesTheSection(
        LockoutFamily family, LockoutSection section, string item, LockState state, string expected)
    {
        // THE SECTION IS NOT ON THE WIRE. The radio scopes a set to the ACTIVE
        // PROMPT's mode section (round-12 P-1: all six matrix cells moved
        // exactly their own prompt's section and nothing else), so the section
        // is a contract with the orchestrator — asserted here by the ABSENCE of
        // any section token in the line.
        ConnectReady();
        Radio.Ssb.SetLockout(family, section, item, state);

        var line = Assert.Single(Transport.SentLines);
        Assert.Equal(expected, line);
        Assert.DoesNotContain("SSB", line, StringComparison.Ordinal);
        Assert.DoesNotContain("HOP", line, StringComparison.Ordinal);
        Assert.DoesNotContain("EAM", line, StringComparison.Ordinal);
    }

    [Fact]
    public void X12_SetLockout_RejectsAnyRowOutsideTheClosedInventory_AndSendsNothing()
    {
        ConnectReady();
        // Real item, WRONG section: TX_POWER is HOP-only, CHAN is SSB-only,
        // BFO is SELECT/SSB-only. Each of these would be a plausible-looking
        // programming error that put a meaningless line on the wire.
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "TX_POWER", LockState.Lock));
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Eam, "CHAN", LockState.Lock));
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "BFO", LockState.Lock));
        // Right item name, WRONG family.
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Select, LockoutSection.Ssb, "CHAN", LockState.Lock));
        // Not an item at all.
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "ALL", LockState.Unlock));
        Assert.Throws<ArgumentException>(() =>
            Radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "", LockState.Unlock));

        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void X12_TheLockoutReportCommands_AreStillForbiddenToEveryOtherSender()
    {
        // The wire-sweep guarantee for the X12 family, made without putting the
        // operation's own "BAT ST" through the catch-set test. "PROGRAM" and
        // "SELECT" were already forbidden prefixes before this round — the
        // amendment did NOT relax the guard, it added the one authorized
        // sender.
        Assert.True(IsForbiddenLine("PROGRAM"));
        Assert.True(IsForbiddenLine("SELECT"));
        Assert.True(IsForbiddenLine("PROGRAM CHAN LOCK"));
        Assert.True(IsForbiddenLine("SELECT KEY UNLOCK"));
        Assert.True(IsForbiddenLine("ZERO"));
        // Anti-vacuity: the sweep is not simply calling everything forbidden.
        Assert.False(IsForbiddenLine("BAT ST"));
        Assert.False(IsForbiddenLine("COM"));
    }

    [Fact]
    public void X13_ZeroizeRadio_SendsExactlyZero_AndArmsTheSettleMachine()
    {
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;     // no poll inside this test
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        Assert.False(Radio.IsZeroizeSettling);
        Radio.Ssb.ZeroizeRadio();

        Assert.Equal(["ZERO"], Transport.SentLines);
        Assert.True(Radio.IsZeroizeSettling);
        Assert.False(Radio.ZeroizeSettled);
        Assert.False(Radio.ZeroizeFaulted);
    }

    [Fact]
    public void X13_SettleGreen_ThePromptEndsIt_AndTheBoundaryResetsTheMirrors()
    {
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        // Something confirmed BEFORE the wipe, so the reset is observable.
        Transport.InjectLine("POWER low");
        Assert.True(Radio.State.PowerLevel.IsConfirmed);

        Radio.Ssb.ZeroizeRadio();
        Assert.True(Radio.IsZeroizeSettling);

        // The captured recovery, in ITS CAPTURED ORDER: the wipe's own
        // banner first, then the prompt coming back in the SAME SESSION
        // (round-12 P-1: 8 bare-CR polls, 9.4 s, no reconnect). The banner
        // is what OPENS the settle window — see the ALE-context capture,
        // where two prompts arrive BEFORE it and settle nothing.
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");

        Assert.True(Radio.ZeroizeSettled);
        Assert.False(Radio.IsZeroizeSettling);
        Assert.False(Radio.ZeroizeFaulted);
        // The BOUNDARY: mirrors reset. (OperatingMode is re-confirmed by the
        // very prompt that settled it, which is the radio speaking again — the
        // reset ran first, and that is what this asserts about PowerLevel.)
        Assert.False(Radio.State.PowerLevel.IsConfirmed);
    }

    /// <summary>
    /// THE SETTLE WINDOW OPENS ON THE BANNER, NOT ON ANY PROMPT
    /// (corrected 2026-08-19 — clone round 12 P2's literal ZERO-first ruling;
    /// bench/transcripts/r12-zero-prompts-20260819-061052.jsonl).
    ///
    /// <para>The original rule — "any prompt settles a zeroize" — was true of
    /// the only capture that existed, an SSB-context wipe whose banner arrives
    /// with no prompt before it. The ruling made the other two prompts
    /// reachable, and there the rule is FALSE: an ALE-context <c>ZERO</c>
    /// answers <c>IN_PROG</c>, a prompt, the fill-gate trailer, another prompt,
    /// and only THEN the banner. Settling on one of those would declare the
    /// wipe finished before the radio had begun it, and the campaign's next act
    /// would go out into a radio about to fall silent for nine seconds.</para>
    /// </summary>
    [Fact]
    public void X13_SettlePromptsBEFORETheBanner_SettleNothing_TheAleContextCapture()
    {
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        Transport.InjectLine("POWER low");
        Assert.True(Radio.State.PowerLevel.IsConfirmed);

        Radio.Ssb.ZeroizeRadio();

        // The ALE-context preamble, verbatim from the transcript: the fill gate
        // re-reporting as the book empties, EACH LINE PROMPT-TERMINATED.
        Transport.InjectLine("IN_PROG");
        Transport.InjectLine("ALE>");
        Transport.InjectLine("PRG 1-3 CHAR SLF");
        Transport.InjectLine("ALE>");

        // NOTHING has settled: the radio has not said it started.
        Assert.True(Radio.IsZeroizeSettling);
        Assert.False(Radio.ZeroizeSettled);
        Assert.True(Radio.State.PowerLevel.IsConfirmed, "the boundary ran on a prompt that settles nothing");

        // …then the banner opens the window, and the NEXT prompt closes it.
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Assert.False(Radio.ZeroizeSettled, "the banner alone is not a settle — the radio is still wiping");

        Transport.InjectLine("SSB>");
        Assert.True(Radio.ZeroizeSettled);
        Assert.False(Radio.State.PowerLevel.IsConfirmed);   // the boundary really did run
    }

    [Fact]
    public void X13_SettleGreen_NotifiesEveryResetStore_AndLeavesTheTransportAlone()
    {
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        var seen = new List<RadioProperty>();
        Radio.StateChanged += (_, e) => seen.Add(e.PropertyChanged);

        Radio.Ssb.ZeroizeRadio();
        Transport.ClearSent();
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");

        // THE EXPECTATION IS DERIVED INDEPENDENTLY (audit round 1, finding 4).
        // It used to be read out of the very list it was validating — so
        // dropping a property from RadioState.ZeroizeNotifiedProperties made
        // the production sweep smaller AND the assertion weaker in one edit,
        // and the auditor removed PowerLevel with all 627 tests still green.
        // Computed here from the ENUM minus the four session exclusions, which
        // are written out as literals: a silent exclusion now fails twice over,
        // once on the list and once on the events.
        RadioProperty[] sessionOnly =
        [
            RadioProperty.ConnectionOpen,
            RadioProperty.ConnectionState,
            RadioProperty.ModeChangePending,
            RadioProperty.ZeroizeSettle,
        ];
        var expected = Enum.GetValues<RadioProperty>()
            .Where(p => !sessionOnly.Contains(p))
            .Order()
            .ToArray();

        Assert.NotEmpty(expected);                                  // anti-vacuity
        Assert.Equal(expected, RadioState.ZeroizeNotifiedProperties.Order().ToArray());
        foreach (var property in expected)
            Assert.Contains(property, seen);

        // …and the four SESSION properties really are excluded: a wipe changes
        // neither the connection nor a pending mode change nor the settle
        // machine's own bookkeeping. (Named individually so widening the
        // exclusion list has to be written HERE first.)
        foreach (var property in sessionOnly)
            Assert.DoesNotContain(property, RadioState.ZeroizeNotifiedProperties);
        Assert.Equal(4, sessionOnly.Length);

        // A spot-check the auditor's own mutation names: PowerLevel is a
        // MIRROR, so it is in the sweep and it really was notified.
        Assert.Contains(RadioProperty.PowerLevel, RadioState.ZeroizeNotifiedProperties);
        Assert.Contains(RadioProperty.PowerLevel, seen);

        // THE TRANSPORT IS UNTOUCHED: the session is alive, the settle poll owns
        // the sequencing, and the boundary sends NOTHING of its own.
        Assert.Empty(Transport.SentLines);
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.True(Radio.IsConnectionOpen);
        Assert.Equal(0, Radio.PendingPingCount);
    }

    [Fact]
    public void X13_SettleRed_TheBoundExpires_AndFaultsLoudly()
    {
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;     // no poll: the fault is the subject
        Radio.ZeroizeSettleTimeoutMs = 60;

        string? error = null;
        Radio.ErrorOccurred += (_, e) => error = e.Message;

        Radio.Ssb.ZeroizeRadio();
        Thread.Sleep(300);                      // the bound expires

        Assert.True(Radio.ZeroizeFaulted);
        Assert.False(Radio.ZeroizeSettled);
        Assert.False(Radio.IsZeroizeSettling);
        Assert.NotNull(error);
        // R13: operator wording, no radio token.
        Assert.DoesNotContain("ZERO", error, StringComparison.Ordinal);
    }

    [Fact]
    public void X13_TheSettlePoll_IsABareCr_NotASentinel()
    {
        // A sentinel carries LATE-ANSWER DEBT (the Q3 credit): a timed-out
        // BAT ST whose answer arrives late completes the NEXT sentinel early,
        // which inside a multi-second silence is precisely the failure mode.
        // The poll therefore asks for nothing but the prompt.
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 40;
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        Radio.Ssb.ZeroizeRadio();
        Transport.ClearSent();
        Thread.Sleep(250);
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");           // stop the poll

        var polls = Transport.SentLines;
        Assert.NotEmpty(polls);
        Assert.All(polls, line => Assert.Equal("", line));
        Assert.Equal(0, Radio.PendingPingCount);        // no sentinel was queued
    }

    [Fact]
    public void X13_TheZeroizeBoundary_ClearsAPendingTuneRePoll()
    {
        // §9 B1's tune→ZERO interaction: a re-poll armed by a tune terminal
        // BEFORE the wipe must never fire INTO the settle window (it would ask
        // a radio that is busy wiping RAM). ResetTriggerFlags is part of the
        // boundary, so the flag is gone by the time the prompt returns.
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;
        Transport.InjectLine("SSB>");           // confirmed SSB
        Transport.InjectLine(" TUNE COMPLETE  ");   // arms the shared re-poll flag
        Transport.ClearSent();

        Radio.Ssb.ZeroizeRadio();
        Transport.ClearSent();
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");           // settles AND would have fired the re-poll

        Assert.True(Radio.ZeroizeSettled);
        Assert.Empty(Transport.SentLines);      // no SH went out
    }
}
