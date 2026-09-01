using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Tests;

/// <summary>
/// Parser fixtures. Every input line is verbatim from the R1–R11 captures in
/// docs/probes.md or the bench-confirmed blocks in docs/protocol.md.
/// Spec-first: where the old code and these documents disagreed, the
/// documents won (IN_PROG, TUNE FAULT, RFG riders, trailing BAND lines).
/// </summary>
public class ResponseParserTests
{
    private readonly RadioState _state = new();
    private readonly ResponseParser _parser;

    public ResponseParserTests() => _parser = new ResponseParser(_state);

    private ParseResult Parse(string line) => _parser.Parse(line);

    private void ParseAllHandled(params string[] lines)
    {
        foreach (var line in lines)
        {
            var r = Parse(line);
            Assert.True(r.Handled, "Unhandled: " + line);
            Assert.Null(r.PayloadError);
        }
    }

    // ---- SSB SH block (R1 capture, radio as found: CW/MED/low) ----------

    [Fact]
    public void SsbShowBlock_R1Capture_PopulatesMirror()
    {
        ParseAllHandled(
            "CHAN 00 ", "KEY OFF ", "RxFr 01600000", "TxFr 01600000",
            "MODE CW ", "AGC MED ", "BAND 1.0 ", "RXONLY NO ", "BFO +0000",
            "MODEM OFF", "DV OFF", "DGT_SQUELCH OFF", "AVS OFF", "ENCRYPT OFF",
            "SQ_LEVEL HIGH", "SQUELCH OFF", "POWER low", "ANTENNA   auto ",
            "CWOFFSET 0000", "RWAS DISABLED", "RETRANS DISABLED", "SSB> ");

        Assert.Equal(0, _state.OperatingChannel.Value);
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
        Assert.Equal("01600000", _state.RxFrequency.Value);
        Assert.Equal("01600000", _state.TxFrequency.Value);
        Assert.Equal(ModulationMode.Cw, _state.ModulationMode.Value);
        Assert.Equal(AgcSpeed.Medium, _state.AgcSpeed.Value);
        Assert.Equal("1.0", _state.Bandwidth.Value);
        Assert.Equal(YesNo.No, _state.ChannelRxOnly.Value);
        Assert.Equal(OnOff.Off, _state.AnalogSquelch.Value);
        Assert.Equal(PowerLevel.Low, _state.PowerLevel.Value);
        Assert.Equal("OFF", _state.ActiveModem.Value);
        Assert.Equal(OperatingMode.Ssb, _state.OperatingMode.Value);

        // Phase R mirrors — every settings line of the same capture:
        Assert.Equal("+0000", _state.BfoOffset.Value);
        Assert.Equal(OnOff.Off, _state.DigitalVoice.Value);
        Assert.Equal(OnOff.Off, _state.DigitalSquelch.Value);
        Assert.Equal("OFF", _state.Avs.Value);
        Assert.Equal(OnOff.Off, _state.Encryption.Value);
        Assert.Equal("HIGH", _state.SquelchLevel.Value);
        Assert.Equal("AUTO", _state.Antenna.Value);
        Assert.Equal("0000", _state.CwOffset.Value);
        Assert.Equal(EnabledDisabled.Disabled, _state.Rwas.Value);
        Assert.Equal("DISABLED", _state.Retransmit.Value);
    }

    // ---- ALE SH block (protocol.md, confirmed) ---------------------------

    [Fact]
    public void AleShowBlock_ParsesWithoutErrors_InProgIsNoise()
    {
        // IN_PROG heads the block even with a complete fill (R7) — it must
        // NOT set any fill state.
        ParseAllHandled(
            "IN_PROG",
            "LSTN        OFF ", "KEY_TO_CALL OFF ", "RAD_SIL     OFF ",
            "ALL_CALL    ON  ", "ANY_CALL    ON  ", "MAXCH 100", "TUNETIME 015",
            "TIME_OUT 000", "AMD_DISPLAY ON  ", "CHAN 00 ", "MODE USB",
            "RxFr 04123000", "TxFr 04123000", "KEY OFF ", "MODEM OFF",
            "DV OFF", "DGT_SQUELCH OFF", "AVS OFF", "ENCRYPT OFF",
            "RWAS DISABLED", "ALE> ");

        Assert.False(_state.Ale.FillState.IsConfirmed);   // IN_PROG is not a fill flag (R7)
        Assert.Equal(OperatingMode.Ale, _state.OperatingMode.Value);

        // Phase R: the nine ALE settings of the same block are mirrored.
        Assert.Equal(OnOff.Off, _state.Ale.ListenBeforeTx.Value);
        Assert.Equal(OnOff.Off, _state.Ale.KeyToCall.Value);
        Assert.Equal(OnOff.Off, _state.Ale.RadioSilence.Value);
        Assert.Equal(OnOff.On, _state.Ale.AllCall.Value);
        Assert.Equal(OnOff.On, _state.Ale.AnyCall.Value);
        Assert.Equal(OnOff.On, _state.Ale.AmdDisplay.Value);
        Assert.Equal(100, _state.Ale.MaxScanChannels.Value);
        Assert.Equal(15, _state.Ale.TuneTimeSeconds.Value);
        Assert.Equal(0, _state.Ale.LinkTimeoutMinutes.Value);
    }

    // ---- HOP SH block (protocol.md, confirmed) ---------------------------

    [Fact]
    public void HopShowBlock_PopulatesHopState()
    {
        ParseAllHandled(
            "NET  00", "KEY OFF ", "NETID    00  12345678", "Hoptype 00 NB  ",
            "Center 00  11565 ", "Hopnum 0041", "MODEM OFF", "ENCRYPT OFF",
            "POWER hi ", "No_Sync", "HOP> ");

        Assert.Equal(0, _state.Hop.CurrentNet.Value);
        Assert.Equal("12345678", _state.Hop.Nets[0].NetId);
        Assert.Equal(HopType.Narrowband, _state.Hop.Nets[0].Type);
        Assert.Equal("11565", _state.Hop.Nets[0].CenterKHz);
        Assert.Equal(41, _state.Hop.HopNum.Value);
        Assert.Equal(HopSyncState.NoSync, _state.Hop.SyncState.Value);
        Assert.Equal(OperatingMode.Hop, _state.OperatingMode.Value);
    }

    [Fact]
    public void HopShowBlock_NoHopsetForm()
    {
        // With no generated hopset the final line is "No_Hopset" (sessions 10-11).
        Parse("Hopnum 0041");
        ParseAllHandled("No_Hopset");
        Assert.Equal(0, _state.Hop.HopNum.Value);
    }

    // ---- Triple-prompt interleave (R2 — the anti-pairing capture) --------

    [Fact]
    public void TriplePromptInterleave_EveryLineParsesStandalone()
    {
        // One BAT ST in zeroized ALE → three prompt-terminated blocks.
        ParseAllHandled(
            "IN_PROG", "ALE>",
            "PRG 1-3 CHAR SLF", "ALE>",
            "Battery Status FULL 31.2V", "ALE>");

        Assert.Equal(AleFillState.NeedSelfAddress, _state.Ale.FillState.Value);
        Assert.Equal("Status FULL 31.2V", _state.BatteryStatus.Value);
        Assert.Equal(OperatingMode.Ale, _state.OperatingMode.Value);
    }

    // ---- Fill-gate walk (R7 fill session) ---------------------------------

    [Fact]
    public void FillGateWalk_TracksTheOutstandingGate()
    {
        Parse("PRG 1-3 CHAR SLF");
        Assert.Equal(AleFillState.NeedSelfAddress, _state.Ale.FillState.Value);

        Parse("IND NOT PROGRMD ");
        Assert.Equal(AleFillState.NeedIndividual, _state.Ale.FillState.Value);

        Parse("NO CHANS TO SCAN");
        Assert.Equal(AleFillState.NeedChannels, _state.Ale.FillState.Value);

        // The radio only auto-scans with a complete fill (protocol.md, ZERO
        // corollary) — SCANNING is the positive fill indicator.
        Parse("SCANNING");
        Assert.Equal(AleFillState.Complete, _state.Ale.FillState.Value);
    }

    [Fact]
    public void FillReadbacks_R7Capture_PopulateStationList()
    {
        ParseAllHandled(
            "SLFAD ZZZ               CHGROUP 00",
            "SLFAD TST               CHGROUP 01",
            "INDAD AAA               CHGROUP 01   ASSOC SELF TST",
            "INDAD BBB               CHGROUP 01   ASSOC SELF TST",
            "NETAD NT1               CHGROUP 01   ASSOC SELF TST",
            "CHGROUP 01 CHANS 00 01 ");

        Assert.Equal(2, _state.Ale.SelfAddresses.Count);
        Assert.Equal(2, _state.Ale.IndividualAddresses.Count);
        Assert.Single(_state.Ale.NetAddresses);
        Assert.Equal("TST", _state.Ale.IndividualAddresses[0].AssociatedSelf);
        Assert.Equal(1, _state.Ale.NetAddresses[0].ChannelGroup);
    }

    [Fact]
    public void BareGateEchoOnAddressToken_IsIgnoredNotAnError()
    {
        // A query on an empty fill answers only the gate trailer; the
        // address token line without CHGROUP payload must not fabricate
        // an entry.
        var r = Parse("SLFAD");
        Assert.True(r.Handled);
        Assert.Empty(_state.Ale.SelfAddresses);
    }

    // ---- Scan channel groups (X8 §4.1) ------------------------------------
    // Every input below is the R7 capture "CHGROUP 01 CHANS 00 01 " (trailing
    // space and all) or a deliberate deformation of it.

    [Fact]
    public void ChannelGroupLine_R7Capture_MirrorsTheChannels_InTheRadiosOrder()
    {
        var r = Parse("CHGROUP 01 CHANS 00 01 ");
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[1].Channels);
        // …and only that slot moved: the other nine are still "never queried".
        Assert.Null(_state.Ale.ChannelGroups[0].Channels);
        Assert.Null(_state.Ale.ChannelGroups[9].Channels);
    }

    [Fact]
    public void ChannelGroupLine_KeepsRadioOrderAndDuplicates_StoresWhatWasSent()
    {
        Parse("CHGROUP 3 CHANS 07 02 07 ");
        Assert.Equal([7, 2, 7], _state.Ale.ChannelGroups[3].Channels);
    }

    // ---- The CHANS WRAP (captured 2026-08-17, phase 2) --------------------
    // A 40-channel group prints 20 channels per line: "CHGROUP 03 CHANS
    // 00 … 19" then a continuation line of BARE numbers "20 21 … 39". Before
    // the ChgChans continuation the second line matched no handler and the
    // group SILENTLY under-displayed at 20 channels.

    [Fact]
    public void ChannelGroupWrap_ContinuationLine_AppendsToTheSameGroup()
    {
        string first = "CHGROUP 03 CHANS " + string.Join(' ', Enumerable.Range(0, 20).Select(n => $"{n:00}"));
        string wrap = string.Join(' ', Enumerable.Range(20, 20).Select(n => $"{n:00}"));

        Parse(first);
        var r = Parse(wrap);
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal(Enumerable.Range(0, 40).ToArray(), _state.Ale.ChannelGroups[3].Channels);
    }

    [Fact]
    public void ChannelGroupWrap_SurvivesMultipleWrapLines()
    {
        // A full MAXCH-100 group would be five lines; the continuation must
        // stay armed across them.
        Parse("CHGROUP 05 CHANS 00 01");
        Parse("02 03");
        Parse("04");
        Assert.Equal([0, 1, 2, 3, 4], _state.Ale.ChannelGroups[5].Channels);
    }

    [Fact]
    public void ChannelGroupWrap_AnyNonChannelLine_EndsTheContinuation()
    {
        // The captured trailer follows the listing immediately; it must be
        // processed normally, and a LATER bare-number line must not be
        // misread as channels once the continuation is closed.
        Parse("CHGROUP 06 CHANS 00 01");
        var trailer = Parse("NO CHANS TO SCAN");
        Assert.True(trailer.Handled);                       // the normal gate line
        var orphan = Parse("02 03");
        Assert.False(orphan.Handled);                       // orphan wrap = unrecognized, loudly
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[6].Channels);
    }

    [Fact]
    public void ChannelGroupWrap_OutOfDomainNumber_IsNotAContinuation()
    {
        // "100" cannot be a channel: the line is NOT consumed as a wrap and
        // surfaces unrecognized; the group keeps only the first line.
        Parse("CHGROUP 07 CHANS 00 01");
        var r = Parse("02 100");
        Assert.False(r.Handled);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[7].Channels);
    }

    // ---- The WRAP SUSPENSION (round 16 fixes S1) --------------------------
    // A `CHGROUP nn CHANS` listing COMMITS PROGRESSIVELY (one
    // ApplyChannelGroup per wrap line), so an ASYNC line arriving between the
    // header and its wrap line used to (a) publish the group at 20 channels
    // and (b) send the following wrap line down the unrecognized path — an
    // "Unrecognized message" banner at the operator. Same for `HOPLIST` at 8
    // frequencies.
    //
    // The fix: an ENUMERATED async line SUSPENDS the continuation — it is
    // parsed as ITSELF and the continuation stays armed; anything else
    // terminates it exactly as before.
    //
    // The theory covers EVERY predicate of `WrapSurvivors` once (13 rows = the
    // 13 regexes). The interleaved SHAPE is ASSUMED, not captured — no capture
    // shows an async line BETWEEN a header and its wrap (the captured async
    // lines sit between whole LISTING ROWS: `KEY OFF` in
    // r11-ale-race-20260818-184900.jsonl record 38, `Wait...`/`WB_Invalid` in
    // r11-exclude-20260818-182614.jsonl record 37, both replayed verbatim in
    // FramerParserIntegrationTests). The LINES themselves are verbatim
    // captures; their POSITION is the constructed part.

    [Theory]
    [InlineData("SCANNING")]                          // r11-ale-race record 34
    [InlineData("SCAN STOPPED")]                      // r11-ale-race record 40
    [InlineData("KEY OFF ")]                          // r11-ale-race record 38 (trailing space and all)
    [InlineData("IN_PROG")]                           // r11-ale-race record 38
    [InlineData("Battery Status FULL 26.4V")]         // r11-ale-race record 38
    [InlineData("POWER CUTBACK   ")]                  // p8-init-sentinel-timing-20260822-093258.jsonl L74
    [InlineData("Wait...")]                           // r11-exclude record 37
    [InlineData("WB_Invalid")]                        // r11-exclude record 37
    [InlineData("Generating Hopset...")]              // r14-coupler record 87
    [InlineData(" TUNING COUPLER ")]                  // p14c-sounding-clean-20260822-132151.jsonl L64
    [InlineData(" TUNE COMPLETE  ")]                  // p14c L64
    [InlineData("   TUNE FAULT   ")]                  // field-clone-console-20260820-1738.txt L47 (padding and all)
    [InlineData("SOUNDING W6HOS            CHANNEL: 30")]   // p14c L16
    public void ChgChansWrap_AnEnumeratedAsyncLine_SUSPENDS_TheContinuation(string asyncLine)
    {
        string first = "CHGROUP 03 CHANS " + string.Join(' ', Enumerable.Range(0, 20).Select(n => $"{n:00}"));
        string wrap = string.Join(' ', Enumerable.Range(20, 20).Select(n => $"{n:00}"));

        Parse(first);

        // The async line is parsed as ITSELF — handled, and its own mirror
        // moved where it has one.
        var async = Parse(asyncLine);
        Assert.True(async.Handled, "Unhandled: " + asyncLine);
        Assert.Null(async.PayloadError);
        AssertOwnEffect(asyncLine);

        // …and the continuation is STILL ARMED: the wrap line lands.
        var r = Parse(wrap);
        Assert.True(r.Handled);
        Assert.Equal(Enumerable.Range(0, 40).ToArray(), _state.Ale.ChannelGroups[3].Channels);
    }

    /// <summary>What each survivor line does IN ITS OWN RIGHT. A suspension
    /// that swallowed the line would keep the wrap intact and still be wrong,
    /// so every row whose line has an observable effect asserts it.</summary>
    private void AssertOwnEffect(string asyncLine)
    {
        switch (asyncLine.Trim())
        {
            case "SCANNING":
                Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
                break;
            case "SCAN STOPPED":
                Assert.Equal(AleLinkState.Stopped, _state.Ale.LinkState.Value);
                break;
            case "KEY OFF":
                Assert.Equal(KeylineState.Off, _state.Keyline.Value);
                break;
            case "Battery Status FULL 26.4V":
                Assert.Equal("Status FULL 26.4V", _state.BatteryStatus.Value);
                break;
            case "POWER CUTBACK":
                Assert.True(_state.PowerCutback.Value);
                break;
            case "Generating Hopset...":
                Assert.True(_state.Hop.IsGeneratingHopset);
                break;
            case "TUNING COUPLER":
                Assert.True(_state.IsTuning);
                break;
            case "TUNE COMPLETE":
                Assert.True(_state.IsTuneComplete);
                break;
            case "TUNE FAULT":
                Assert.True(_state.IsTuneFail);
                break;
            case "SOUNDING W6HOS            CHANNEL: 30":
                Assert.Equal(AleLinkState.Sounding, _state.Ale.LinkState.Value);
                Assert.Equal("W6HOS", _state.Ale.LqaStation);
                Assert.Equal("30", _state.Ale.LqaChannel);
                break;
            // IN_PROG, Wait... and WB_Invalid are RECOGNIZED NOISE by decision
            // (each has its own pin elsewhere in this file): the claim for them
            // is Handled, asserted by the caller, and nothing else.
            case "IN_PROG" or "Wait..." or "WB_Invalid":
                break;
            default:
                Assert.Fail("no effect declared for the survivor line: " + asyncLine);
                break;
        }
    }

    [Fact]
    public void HoplistFreqs_SurvivesKeyOff()
    {
        // The OTHER wrap state, same rule. `KEY OFF ` is the captured async
        // line that really does interleave a listing (r11-ale-race record 38).
        Parse("HOPLIST 03   11010  11015  11020  11025  11030  11035  11040  11045");
        Assert.True(Parse("KEY OFF ").Handled);
        Assert.True(Parse("11050  11055  11060  11065  11070  11075  11080  11085").Handled);

        Assert.Equal(16, _state.Hop.HopLists[3].Count);
        Assert.Equal("11085", _state.Hop.HopLists[3][15]);
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
    }

    [Fact]
    public void ChgChans_UnrelatedDigitsAfterPrompt_NotAppended()
    {
        // The NEGATIVE that bounds the suspension: a prompt is NOT a survivor,
        // so it terminates the continuation as it always has and a later bare
        // numeric line is an orphan, not channels 12 and 34.
        Parse("CHGROUP 01 CHANS 00 01 02 03 04");
        Assert.True(Parse("ALE> ").Handled);
        Assert.False(Parse("12 34").Handled);
        Assert.Equal([0, 1, 2, 3, 4], _state.Ale.ChannelGroups[1].Channels);
    }

    [Fact]
    public void ChgChans_BlankLineStillTerminates()
    {
        // Pinned so S1 is never read as widening the blank-line rule (the
        // 2026-08-17 Sol finding): a blank line ends the wrap, survivor set or
        // not — including a blank line that arrives after a SUSPENSION.
        Parse("CHGROUP 06 CHANS 00 01");
        Assert.True(Parse("SCANNING").Handled);       // suspended, still armed
        Parse("");                                     // …and now ended
        Assert.False(Parse("02 03").Handled);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[6].Channels);
    }

    [Fact]
    public void ChgChans_SuspensionDoesNotRecommit()
    {
        // A suspension is not a commit: the async line publishes NOTHING about
        // the group. (Today's parser does not re-commit either — this is a
        // regression pin, and it is what stops a future "just re-apply it to be
        // safe" from double-raising the mirror at 20 channels.)
        Parse("CHGROUP 03 CHANS 00 01");

        var raised = new List<RadioProperty>();
        _state.Changed += p => { if (p == RadioProperty.AleChannelGroups) raised.Add(p); };

        Parse("SCANNING");
        Assert.Empty(raised);                          // the suspension itself published nothing

        // ANTI-VACUITY, off its OWN arming so the check is independent of the
        // suspension: a genuine wrap line publishes exactly once. (Written this
        // way deliberately — resuming the SUSPENDED continuation here would
        // make this a RED-first pin instead of the regression pin it is; the
        // resumption is what the 13-row theory above asserts.)
        raised.Clear();
        Parse("CHGROUP 03 CHANS 00 01");
        raised.Clear();
        Parse("02 03");
        Assert.Single(raised);
        Assert.Equal([0, 1, 2, 3], _state.Ale.ChannelGroups[3].Channels);
    }

    // ---- The HOPLIST wrap (captured 2026-08-17, phase 3) ------------------
    // A HOPLIST answer wraps at EIGHT frequencies per line, continuation lines
    // being bare 5-digit values — including inside a DIS record, where the
    // HOPLIST line is a LIST net's value line. Before the HoplistFreqs
    // continuation, a >8-frequency list silently truncated in the mirror.

    [Fact]
    public void HoplistWrap_ContinuationLines_AppendToTheSameNet()
    {
        // The captured 26-frequency shape: 8 + 8 + 8 + 2.
        Parse("HOPLIST 09   11000  11010  11015  11020  11025  11030  11035  11040");
        Parse("11045  11050  11055  11060  11065  11070  11075  11080");
        Parse("11085  11090  11095  11100  11105  11110  11115  11120");
        var r = Parse("15000  19995");
        Assert.True(r.Handled);
        Assert.Equal(26, _state.Hop.HopLists[9].Count);
        Assert.Equal("11000", _state.Hop.HopLists[9][0]);
        Assert.Equal("19995", _state.Hop.HopLists[9][25]);
    }

    [Fact]
    public void HoplistWrap_AnyNonFrequencyLine_EndsTheContinuation_Loudly()
    {
        Parse("HOPLIST 03   11010  11015  11020");
        var prompt = Parse("HOP>");
        Assert.True(prompt.Handled);                        // ends it, processed normally
        var orphan = Parse("11025  11030");
        Assert.False(orphan.Handled);                       // orphan wrap = unrecognized
        Assert.Equal(3, _state.Hop.HopLists[3].Count);
    }

    [Fact]
    public void HoplistWrap_TwoDigitNumbers_AreNotConsumed_AndViceVersa()
    {
        // The two wrap continuations must not eat each other's lines: a CHG
        // wrap line is 1-2 digit channels, a HOPLIST wrap line is 5-digit
        // frequencies.
        Parse("HOPLIST 03   11010  11015  11020");
        var chgLike = Parse("20 21");
        Assert.False(chgLike.Handled);                      // not five digits — not a hoplist wrap
        Assert.Equal(3, _state.Hop.HopLists[3].Count);

        Parse("CHGROUP 05 CHANS 00 01");
        var hopLike = Parse("11025 11030");
        Assert.False(hopLike.Handled);                      // >99 — not a CHG wrap
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[5].Channels);
    }

    // ---- The NET-token collision (captured 2026-08-17, phase 2) -----------

    [Fact]
    public void NetChansReqd_IsARefusal_NotACurrentNetPoisoning()
    {
        // " NET CHANS REQD " begins with the HOP net token; before the guard
        // it reached ParseInt("CHANS") and produced a payload error.
        _state.Hop.SetCurrentNet(3);
        var r = Parse(" NET CHANS REQD ");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Equal(3, _state.Hop.CurrentNet.Value);       // untouched
        Assert.Equal("NET CHANS REQD", _state.Ale.ProgrammingRefusal.Line);
    }

    // ---- Sol audit fixes (findings 1/4/5/6, applied 2026-08-18) -----------
    // The POSITIVE half of each shape below is a verbatim capture (the CHGROUP
    // and HOPLIST answers, the help banner, the refusal lines). The NEGATIVE
    // probes — the orphan `02 03` / `11025 11030` lines, `+5`, `5`,
    // `LQA STATUS`, `INDIV SOMETHING`, `SELF CHANS`, `NET CHANS REQD EXTRA` —
    // are CONSTRUCTED negatives, deliberately never-captured shapes whose whole
    // point is that the parser must NOT claim them (the DERIVED-fixture
    // convention of Round11ReadStoreTests: say which is which).

    [Fact]
    public void WrapContinuations_AreEndedByABlankLine()
    {
        // Audit finding 1: a blank line returned early WITHOUT clearing the
        // wrap continuations, so a much-later numeric line could be consumed
        // as phantom channels/frequencies.
        Parse("CHGROUP 06 CHANS 00 01");
        Parse("");                                          // blank ends it
        var orphan = Parse("02 03");
        Assert.False(orphan.Handled);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[6].Channels);

        Parse("HOPLIST 03   11010  11015  11020");
        Parse("   ");                                       // whitespace-only too
        var hopOrphan = Parse("11025  11030");
        Assert.False(hopOrphan.Handled);
        Assert.Equal(3, _state.Hop.HopLists[3].Count);
    }

    [Fact]
    public void WrapContinuations_AreEndedByEnteringAHelpBlock()
    {
        // Audit finding 1: help-block entry returned early with the
        // continuation still armed; it then survived the whole block.
        Parse("CHGROUP 06 CHANS 00 01");
        Parse("Embedded Adaptive HELP commands consist of:");   // help banner
        Parse("ALE> ");                                         // ends the block
        var orphan = Parse("02 03");
        Assert.False(orphan.Handled);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[6].Channels);
    }

    [Fact]
    public void TxMsgContinuation_IsNotEndedByABlankLine_TheFinding1ScopeBoundary()
    {
        // The BINDING scope note of audit finding 1: the early-return paths
        // clear ONLY the two WRAP states. TxMsgText has the same exposure but
        // PREDATES the wrap work, so changing its blank-line behavior is a
        // separate decision — and nothing else in the suite pinned it, which
        // let EndWrapContinuation be widened to cover TxMsgText with all 549
        // tests still green (P2.5 audit, MINOR-1).
        //
        // This pins TODAY'S behavior as the fix's BOUNDARY, not as an
        // endorsement of it: if the blank-line drop is ever revisited for
        // TxMsgText, that is a deliberate change and this pin is what says so.
        Parse("TXMSG 00");
        Parse("");                              // blank does NOT end this one
        Parse("MEET AT GRID 0900");             // still consumed as message text

        Assert.Single(_state.Ale.TxMessages);
        Assert.Equal(0, _state.Ale.TxMessages[0].Slot);
        Assert.Equal("MEET AT GRID 0900", _state.Ale.TxMessages[0].Text);
    }

    [Fact]
    public void ChgWrapTokens_MustBeExactlyTwoDigits()
    {
        // Audit finding 5: NumberStyles.Integer accepted "+5" and "5" — forms
        // no capture has ever shown. The wire prints zero-padded two-digit.
        //
        // Each negative gets its OWN armed continuation. A rejected line
        // DISARMS ChgChans, so probing both shapes off ONE arming would make
        // the second assertion pass because nothing was armed rather than
        // because the token shape was refused — tautological, and blind to a
        // matcher that accepts one digit (P2.5 audit, MINOR-2).
        Parse("CHGROUP 07 CHANS 00 01");
        Assert.False(Parse("+5").Handled);
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[7].Channels);

        Parse("CHGROUP 07 CHANS 00 01");        // re-arm: the one-digit probe
        Assert.False(Parse("5").Handled);       //   now meets a LIVE continuation
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[7].Channels);
    }

    [Theory]
    [InlineData("LQA STATUS")]
    [InlineData("INDIV SOMETHING")]
    [InlineData("SELF CHANS")]
    [InlineData("NET CHANS REQD EXTRA")]
    public void GuardedRefusalTokens_WithUnmatchedPayloads_SurfaceLoudly(string line)
    {
        // Audit finding 4/6: the guarded routes half-handled unmatched forms
        // (Handled stayed true, nothing recorded). They must opt OUT so the
        // line reaches the unrecognized path — except NET, whose unmatched
        // forms surface as a payload error through its own numeric path.
        var r = Parse(line);
        bool surfaced = !r.Handled || r.PayloadError is not null;
        Assert.True(surfaced, $"'{line}' vanished half-handled");
        Assert.Equal(default, _state.Ale.ProgrammingRefusal);   // nothing recorded
    }

    [Fact]
    public void ChannelGroupLine_WithNoChannels_IsAConfirmedEmptyGroup()
    {
        // The captured EMPTY-group behavior is silence, so this shape has
        // never been seen — but if it arrives it means exactly "queried, and
        // there is nothing in it", which is not the same as "never queried".
        Parse("CHGROUP 04 CHANS");
        Assert.NotNull(_state.Ale.ChannelGroups[4].Channels);
        Assert.Empty(_state.Ale.ChannelGroups[4].Channels!);
    }

    [Theory]
    [InlineData("CHGROUP 10 CHANS 05")]       // group outside 0-9
    [InlineData("CHGROUP 1 CHANS 100")]       // channel outside 0-99
    [InlineData("CHGROUP 1 CHANS 00 100")]    // …one bad channel poisons the line
    [InlineData("CHGROUP 01")]                // no CHANS token
    [InlineData("CHGROUP 01 CHANS AB")]       // non-numeric channel
    [InlineData("CHGROUP 01 CHANS 00 EXTRA")] // trailing junk
    public void ChannelGroupLine_OutsideTheDomainOrTheShape_MutatesNothing_AndSurfaces(string line)
    {
        var r = Parse(line);

        // Nothing is mirrored from a line that cannot be honestly parsed…
        for (int g = 0; g < 10; g++) Assert.Null(_state.Ale.ChannelGroups[g].Channels);
        // …and it opts OUT of Handled (the SCAN-payload precedent, audit
        // NIT-1) so it surfaces through the unrecognized-line path instead of
        // vanishing: the group under-displays and the stack says so.
        Assert.False(r.Handled);
    }

    [Fact]
    public void ChannelGroupLine_DomainNegative_DoesNotWipeAConfirmedGroup()
    {
        // Anti-vacuity for the pin above: "mutates nothing" must also hold
        // when the slot ALREADY carries confirmed channels.
        Parse("CHGROUP 01 CHANS 00 01 ");
        Parse("CHGROUP 01 CHANS 100");
        Assert.Equal([0, 1], _state.Ale.ChannelGroups[1].Channels);
    }

    // ---- Programming refusals (X8 §4.1) -----------------------------------
    // The captured lines, VERBATIM (docs/protocol.md refusal set): the radio
    // pads them with a leading and trailing space, and the parser trims.

    [Theory]
    [InlineData(" ADDRESS EXISTS ", "ADDRESS EXISTS")]
    [InlineData(" INV ASSOC SELF ", "INV ASSOC SELF")]
    [InlineData(" INV MEMBER ADDR ", "INV MEMBER ADDR")]
    [InlineData(" INV SELF ADDRESS ", "INV SELF ADDRESS")]
    [InlineData(" INV IND ADDRESS ", "INV IND ADDRESS")]
    [InlineData(" INV ADDRESS      ", "INV ADDRESS")]
    // The phase-1/2 captures (2026-08-17): duplicate ADDM; a self that is not
    // the net's assoc self; a re-STA on a queued target; the full LQA queue;
    // the per-kind schedule chan-group gates.
    [InlineData(" DUPLICATE MEMBER ", "DUPLICATE MEMBER")]
    [InlineData(" INV SELF MEMBER ", "INV SELF MEMBER")]
    [InlineData(" ADR ALREADY QUED ", "ADR ALREADY QUED")]
    [InlineData(" LQA QUEUE FULL ", "LQA QUEUE FULL")]
    [InlineData(" INDIV CHANS REQD ", "INDIV CHANS REQD")]
    [InlineData(" SELF CHANS REQD ", "SELF CHANS REQD")]
    [InlineData(" NET CHANS REQD ", "NET CHANS REQD")]
    public void RefusalLines_AreRecordedVerbatim_WithAMonotoneSequence(string line, string trimmed)
    {
        Assert.Equal(default, _state.Ale.ProgrammingRefusal);   // nothing yet

        var r = Parse(line);

        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal(trimmed, _state.Ale.ProgrammingRefusal.Line);
        Assert.Equal(1, _state.Ale.ProgrammingRefusal.Sequence);

        Parse(line);
        Assert.Equal(2, _state.Ale.ProgrammingRefusal.Sequence);   // monotone, even repeated
    }

    [Fact]
    public void ErrorBanner_KeepsItsExistingBehavior_AND_IsRecordedAsARefusal()
    {
        // The `**` branch returns before the dispatch table, so its refusal
        // note is a SECOND behavior on the same branch — both halves pinned
        // together, so removing either fails here.
        var r = Parse("** ERROR **");

        Assert.True(r.Handled);
        Assert.Equal("**", r.Token);
        Assert.Equal("ERROR", r.Payload);
        Assert.Equal("** ERROR **", _state.Ale.ProgrammingRefusal.Line);
        Assert.Equal(1, _state.Ale.ProgrammingRefusal.Sequence);
    }

    [Fact]
    public void OtherDomainsRejects_AreNotRoutedIntoTheAleRefusalSlot()
    {
        // "INVALID ENCR KEY" / "INVALID MODEM PRESET" are crypto and modem
        // rejects: recognized, but never attributable to a fill write.
        Assert.True(Parse("INVALID ENCR KEY").Handled);
        Assert.Equal(default, _state.Ale.ProgrammingRefusal);
    }

    // ---- CAL lifecycle (R7, dummy load) -----------------------------------

    [Fact]
    public void CalLifecycle_R7Capture()
    {
        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);

        Parse("SCAN STOPPED");
        Assert.Equal(AleLinkState.Stopped, _state.Ale.LinkState.Value);

        Parse("CALLING  AAA              CHANNEL: 01");
        Assert.Equal(AleLinkState.Calling, _state.Ale.LinkState.Value);
        Assert.Equal("AAA", _state.Ale.LinkedStation);
        Assert.Equal("01", _state.Ale.LinkedChannel);

        Parse(" TUNING COUPLER ");
        Assert.True(_state.IsTuning);
        // The R7 capture contains NO KEY lines in the tune window: keyline
        // stays unconfirmed — never fabricated from the tune lifecycle
        // (plan §0; audit round 1, F1).
        Assert.False(_state.Keyline.IsConfirmed);

        Parse(" TUNE COMPLETE  ");
        Assert.True(_state.IsTuneComplete);
        Assert.False(_state.IsTuning);
        Assert.False(_state.Keyline.IsConfirmed);

        Parse("SENDING  AAA              CHANNEL: 01");
        Assert.Equal(AleLinkState.Sending, _state.Ale.LinkState.Value);

        Parse("LINKED");
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void ScanToken_OnlyStoppedPayloadSetsLinkState_OtherPayloadsUnhandled()
    {
        // Audit round 1, NIT-1: "SCAN STOPPED" is the only captured SCAN
        // line — a synthetic "SCAN GARBAGE" must NOT flip the banner and
        // surfaces as unrecognized (honest surface).
        var garbage = Parse("SCAN GARBAGE");
        Assert.False(garbage.Handled);
        Assert.False(_state.Ale.LinkState.IsConfirmed);

        var stopped = Parse("SCAN STOPPED");
        Assert.True(stopped.Handled);
        Assert.Equal(AleLinkState.Stopped, _state.Ale.LinkState.Value);
    }

    [Theory]
    [InlineData("SOUNDING  XYZ")]
    // "TERMINATING LINK" LEFT this theory on 2026-08-23: probe P20b captured
    // its lifecycle, so it is recognized now (see
    // TerminatingLink_IsRecognized_AndMirrorsNothing below). Class (2)'s rule
    // is unchanged — a shape enters when a capture pins it, and this is the
    // first one to do so.
    [InlineData("TERMINATING PARTIALLY")]   // …but only the CAPTURED payload
    [InlineData("EXCHANGE  XYZ")]
    [InlineData("SIGNAL LOST")]             // SIGNAL claims only RECEIVED
    [InlineData("RECEIVING SIGNAL")]        // RECEIVING claims only CALL/AMD
    public void MalformedProgressAndUncapturedShapes_DoNotChangeLinkState(string line)
    {
        // RE-KEYED 2026-08-23 (round 15 item I, critic F75). Two classes now
        // share this no-mutation rule, and the theory's original name had
        // become false for two of its own rows:
        //
        // (1) MALFORMED PROGRESS — "SOUNDING  XYZ" / "EXCHANGE  XYZ" carry
        //     tokens the parser DOES handle since P14, but not the captured
        //     shape (no "CHANNEL: nn"). A token is not a line: an anchored
        //     shape that fails to match is not something this parser
        //     understands, and it must not flip the banner.
        // (2) PAYLOAD-GUARDED TOKENS — the two-station session (2026-08-24,
        //     field-ale-first-contact) graduated SIGNAL RECEIVED and
        //     RECEIVING CALL/AMD into handled shapes, so this class now
        //     holds each token's UNCAPTURED payloads: TERMINATING (only
        //     `LINK` captured, P20b), SIGNAL (only `RECEIVED`), RECEIVING
        //     (only `CALL` and `AMD`).
        //
        // Both classes surface through the unrecognized-line path.
        Parse("SCANNING");
        var r = Parse(line);
        Assert.False(r.Handled);
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
        Assert.Null(_state.Ale.LqaStation);
        Assert.Null(_state.Ale.LqaChannel);
    }

    // ---- The inbound handshake + received AMD (field capture 2026-08-24,
    // bench/transcripts/field-ale-first-contact-20260824-2144.txt — the first
    // two-station session; Stage 9 closed) ------------------------------------

    [Fact]
    public void TheInboundHandshake_WalksItsCapturedLifecycle_ToLinked()
    {
        // 21:56:35-55: SCANNING → ` SIGNAL RECEIVED ` → `RECEIVING CALL  ` →
        // (coupler pair) → KEY OFF → LINKED KC1HAS1 CHANNEL: 29.
        Parse("SCANNING");
        var r1 = Parse(" SIGNAL RECEIVED ");
        Assert.True(r1.Handled);
        Assert.Equal(AleLinkState.SignalReceived, _state.Ale.LinkState.Value);

        var r2 = Parse("RECEIVING CALL  ");
        Assert.True(r2.Handled);
        Assert.Equal(AleLinkState.ReceivingCall, _state.Ale.LinkState.Value);

        Parse(" TUNING COUPLER ");
        Parse(" TUNE COMPLETE  ");
        Parse("KEY OFF ");
        Parse("LINKED KC1HAS1           CHANNEL: 29");
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);
        Assert.Equal("KC1HAS1", _state.Ale.LinkedStation);
        Assert.Equal("29", _state.Ale.LinkedChannel);
    }

    [Fact]
    public void ASignalNotForThisStation_ResolvesBackToScanning()
    {
        // 22:01:41-44: ` SIGNAL RECEIVED ` then the bare SCANNING — no call,
        // no link, and the banner follows the radio back down.
        Parse("SCANNING");
        Parse(" SIGNAL RECEIVED ");
        Assert.Equal(AleLinkState.SignalReceived, _state.Ale.LinkState.Value);
        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void ReceivingAmd_IsRecognized_AndMovesNothing()
    {
        // 22:06:58: `RECEIVING AMD   ` between RECEIVING CALL and the RXMSG
        // record — recognized (no more console noise), state untouched.
        Parse("SCANNING");
        Parse("RECEIVING CALL  ");
        var r = Parse("RECEIVING AMD   ");
        Assert.True(r.Handled);
        Assert.Equal(AleLinkState.ReceivingCall, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void AReceivedAmd_LandsInTheRxMirror_HeaderThenText()
    {
        // 22:06:59, byte-faithful: the FROM/DATE/TIME header, the text on the
        // NEXT line (leading/trailing spaces trimmed into the slot).
        Parse("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        var r = Parse("  TESTING  ");
        Assert.True(r.Handled);
        var m = Assert.Single(_state.Ale.RxMessages);
        Assert.Equal(0, m.Slot);
        Assert.Equal("KC1HAS1", m.From);
        Assert.Equal("24-AUG-26", m.Date);
        Assert.Equal("22:06", m.Time);
        Assert.Equal("TESTING", m.Text);
    }

    [Fact]
    public void TheRxHeader_SurvivesAnEnumeratedAsyncLine_LikeTxMsg()
    {
        // The SAME six-shape rule as the TXMSG continuation: an enumerated
        // async line routes as itself and the header stays ARMED.
        Parse("RXMSG 01   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:07");
        Parse("Battery Status FULL 26.1V");
        Parse("  COPY  ");
        var m = Assert.Single(_state.Ale.RxMessages);
        Assert.Equal("COPY", m.Text);
        Assert.Equal(1, m.Slot);
    }

    [Fact]
    public void RxMessages_UpsertBySlot_AndClear()
    {
        Parse("RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06");
        Parse("  TESTING  ");
        Parse("RXMSG 00   FROM N7BOI            DATE: 24-AUG-26  TIME: 23:00");
        Parse("  HELLO  ");
        var m = Assert.Single(_state.Ale.RxMessages);   // same slot: replaced
        Assert.Equal("N7BOI", m.From);

        _state.Ale.ClearRxMessages();
        Assert.Empty(_state.Ale.RxMessages);
    }

    [Fact]
    public void AnRxHeaderInAnotherShape_StaysUnmirrored_TheOldBehavior()
    {
        // Only the CAPTURED FROM/DATE/TIME payload arms the continuation; a
        // bare-numbered RXMSG (the still-uncaptured listing guess) keeps the
        // pre-capture behavior — recognized, nothing mirrored, and the next
        // line is NOT consumed as text.
        var r = Parse("RXMSG 03");
        Assert.True(r.Handled);
        var r2 = Parse("SOMETHING ELSE");
        Assert.False(r2.Handled);
        Assert.Empty(_state.Ale.RxMessages);
    }

    // ---- Heard soundings + exchange responses (field capture 2026-08-24 #2,
    // bench/transcripts/field-ale-sounding-lqa-20260824-2312.txt) ------------

    [Fact]
    public void AHeardSounding_IsRecognized_AndMovesNothing()
    {
        // Five captures of the lifecycle: SCANNING → ` SIGNAL RECEIVED ` →
        // `SOUND FROM:   KC1HAS1         CHANNEL: 27` → `SCANNING`. The heard
        // line itself is an observation, not a state.
        Parse("SCANNING");
        Parse(" SIGNAL RECEIVED ");
        var r = Parse("SOUND FROM:   KC1HAS1         CHANNEL: 27");
        Assert.True(r.Handled);
        Assert.True(r.Changed);                         // the heard-event slot moved
        Assert.Equal(AleHeardKind.Sounding, _state.Ale.LastHeard!.Kind);
        Assert.Equal("KC1HAS1", _state.Ale.LastHeard.Station);
        Assert.Equal("27", _state.Ale.LastHeard.Channel);
        Assert.Equal(AleLinkState.SignalReceived, _state.Ale.LinkState.Value);
        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void TheSoundScheduleRow_StillParses_TheTokenForksOnPayload()
    {
        // The existing idiom (TheExchangeToken_CarriesBothShapes…): a listing
        // row is Handled+Changed with NO link state; the MIRROR commits only
        // on a read's closing sentinel (Round11ReadStoreTests, end-to-end).
        var r = Parse("SOUND    W6HOS1          INTERVAL 01:00 START TIME 23:30");
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.False(_state.Ale.LinkState.IsConfirmed);
    }

    [Fact]
    public void AnExchangeResponse_IsRecognized_AndTheRunKeepsItsChannel()
    {
        // 23:15:04: ` EXCHANGE KC1HAS1          CHANNEL: 29` then
        // `RESP  FROM:   KC1HAS1         CHANNEL: 29` — the partner answered;
        // the NEXT EXCHANGE line is what moves the channel.
        Parse("SCANNING");
        Parse(" EXCHANGE KC1HAS1          CHANNEL: 29");
        Assert.Equal(AleLinkState.Exchanging, _state.Ale.LinkState.Value);
        var r = Parse("RESP  FROM:   KC1HAS1         CHANNEL: 29");
        Assert.True(r.Handled);
        Assert.Equal(AleHeardKind.Response, _state.Ale.LastHeard!.Kind);
        Assert.Equal("29", _state.Ale.LastHeard.Channel);
        Assert.Equal(AleLinkState.Exchanging, _state.Ale.LinkState.Value);
        Assert.Equal("KC1HAS1", _state.Ale.LqaStation);
        Assert.Equal("29", _state.Ale.LqaChannel);
        Parse("EXCHANGE KC1HAS1          CHANNEL: 27");
        Assert.Equal("27", _state.Ale.LqaChannel);
    }

    [Theory]
    [InlineData("RESP  TO:   KC1HAS1         CHANNEL: 29")]   // RESP claims only FROM:
    [InlineData("RESP  FROM:   KC1HAS1         CHANNEL: 129")] // …with a TWO-digit channel
    public void AnUncapturedRespPayload_StaysUnrecognized(string line)
    {
        var r = Parse(line);
        Assert.False(r.Handled);
    }

    // ---- The broadcast round: ANY/ALL call and AMD shapes -------------------
    // Every line below is VERBATIM from
    // bench/transcripts/p20-amd-broadcast-20260823-233550.jsonl and
    // bench/transcripts/p20b-any-with-channel-20260823-233951.jsonl (2026-08-23,
    // RT-1694 + RF-5122, HFLINK fill, dummy load), column padding and trailing
    // spaces included. The byte-faithful, prompt-glued replays of the same
    // records live in FramerParserIntegrationTests.

    [Fact]
    public void TerminatingLink_IsRecognized_AndMirrorsNothing_TheScanningBehindItOwnsTheMove()
    {
        // P20b record 4: `SCA` against the held ALL link answered
        // "ALE> TERMINATING LINK" and the radio's own `SCANNING` followed.
        // The line is recognized — it may NOT reach the operator's
        // "Unrecognized message" banner — but it writes nothing: claiming a
        // state here would only race the SCANNING two bytes behind it.
        Parse("LINKED ALL               CHANNEL: 29");
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);

        var r = Parse("TERMINATING LINK");
        Assert.True(r.Handled);
        Assert.False(r.Changed);
        Assert.Null(r.PayloadError);
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);   // unmoved
        Assert.Equal("ALL", _state.Ale.LinkedStation);
        Assert.Equal("29", _state.Ale.LinkedChannel);

        Parse("SCANNING");                                                // …the radio's own move
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void NoResponse_IsRecognized_AndMirrorsNothing_TheScanningBehindItOwnsTheMove()
    {
        // P20b: `CAL ANY 12` opened a ~69 s answer window that ended
        // " NO RESPONSE     " (68 752 ms; the `SE 9 ANY 12` twin at 68 827 ms),
        // with `ALE> SCANNING` in the same chunk. Same rule as TERMINATING
        // LINK: recognized, nothing mirrored.
        Parse("CALLING  ANY              CHANNEL: 12");
        Assert.Equal(AleLinkState.Calling, _state.Ale.LinkState.Value);

        var r = Parse("NO RESPONSE     ");
        Assert.True(r.Handled);
        Assert.False(r.Changed);
        Assert.Null(r.PayloadError);
        Assert.Equal(AleLinkState.Calling, _state.Ale.LinkState.Value);   // unmoved

        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Theory]
    [InlineData("SENDING  ALL              CHANNEL: 29", "ALL", "29")]   // P20, `SE 9 ALL`
    [InlineData("SENDING  ALL              CHANNEL: 12", "ALL", "12")]   // P20b, `SE 9 ALL 12`
    [InlineData("SENDING  ANY              CHANNEL: 12", "ANY", "12")]   // P20b, `SE 9 ANY 12`
    [InlineData("CALLING  ANY              CHANNEL: 12", "ANY", "12")]   // P20b, `CAL ANY 12`
    [InlineData("CALLING  ALL              CHANNEL: 29", "ALL", "29")]   // P20, `CAL ALL`
    public void BroadcastCallProgress_RidesTheEXISTINGPath_NoParserChangeWasNeeded(
        string line, string station, string channel)
    {
        // Plan §2.3: `SENDING`/`CALLING` already route through
        // HandleCallProgress, whose regex parses station + channel — and
        // ANY/ALL are ordinary station tokens to it. These rows are the
        // CONFIRMATION pins for that claim, not a new behavior.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal(station, _state.Ale.LinkedStation);
        Assert.Equal(channel, _state.Ale.LinkedChannel);
    }

    [Fact]
    public void LinkedWithAChannelInItsPayload_ParsesTHATChannel_NotTheStaleCallOne()
    {
        // P20: the `CAL ALL` handshake announced CHANNEL: 29 and the link
        // landed on 29 — but a radio that had last CALLED on another channel
        // would otherwise have kept the stale number, because the LINKED
        // handler used to reuse LinkedChannel unconditionally.
        Parse("CALLING  BOB              CHANNEL: 01");
        Assert.Equal("01", _state.Ale.LinkedChannel);

        var r = Parse("LINKED ALL               CHANNEL: 29");
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);
        Assert.Equal("ALL", _state.Ale.LinkedStation);
        Assert.Equal("29", _state.Ale.LinkedChannel);
    }

    [Fact]
    public void LinkedWithoutAChannel_KeepsTheCallsChannel_ExactlyAsBefore()
    {
        // The pre-P20 behavior, pinned so the new branch cannot swallow it:
        // a payload with no "CHANNEL: nn" still reuses the last
        // CALLING/SENDING channel, and a BARE `LINKED` still touches neither
        // slot (the R7 lifecycle).
        Parse("CALLING  BOB              CHANNEL: 01");

        Parse("LINKED BOB");
        Assert.Equal("BOB", _state.Ale.LinkedStation);
        Assert.Equal("01", _state.Ale.LinkedChannel);

        Parse("LINKED");
        Assert.Equal("BOB", _state.Ale.LinkedStation);
        Assert.Equal("01", _state.Ale.LinkedChannel);
    }

    [Theory]
    [InlineData("XX")]      // the auditor's own case
    [InlineData("2B")]
    [InlineData("9")]       // one digit: no capture prints it
    [InlineData("100")]     // three: out of the radio's own 00-99 range
    public void LinkedWithAnUncapturedChannelToken_KeepsTheCallsChannel_AndStillReadsAsLinked(
        string token)
    {
        // AUDIT ROUND 1 (MAJOR): the LINKED branch reuses ProgressShape, whose
        // channel group is `(\S+)` — neither end-anchored nor digit-restricted
        // — so "LINKED ALL               CHANNEL: XX" mirrored "XX" verbatim
        // and a consumer would render "CH XX", a value NO transcript supports.
        // Only the captured spelling (exactly two digits) is adopted; anything
        // else takes the without-a-channel path and keeps the last
        // CALLING/SENDING channel. The line is still RECOGNIZED and still moves
        // the state to Linked — the token is not a reason to disbelieve the
        // link, only a reason not to claim a channel.
        Parse("CALLING  BOB              CHANNEL: 01");

        var r = Parse($"LINKED ALL               CHANNEL: {token}");

        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);
        Assert.Equal("ALL", _state.Ale.LinkedStation);   // the station IS captured
        Assert.Equal("01", _state.Ale.LinkedChannel);    // …the channel is not

        // Anti-vacuity: the CAPTURED spelling on the very same shape DOES land,
        // so this pin cannot pass by the branch simply never adopting one.
        Parse("LINKED ALL               CHANNEL: 29");
        Assert.Equal("29", _state.Ale.LinkedChannel);
    }

    // ---- The bare-STA LQA lifecycle (round 15 item I; probes P14b/P14c) ------

    [Fact]
    public void SoundingProgressLine_SetsSounding_AndFillsTheLqaSlot_NotTheCallSlot()
    {
        // VERBATIM from bench/transcripts/p14c-sounding-clean-20260822-132151.jsonl
        // (the `SOU STA W6HOS` step record), column padding included.
        Parse("CALLING  AAA              CHANNEL: 01");     // a real call slot, first
        var r = Parse("SOUNDING W6HOS            CHANNEL: 30");

        Assert.True(r.Handled);
        Assert.Equal(AleLinkState.Sounding, _state.Ale.LinkState.Value);
        Assert.Equal("W6HOS", _state.Ale.LqaStation);
        Assert.Equal("30", _state.Ale.LqaChannel);

        // The CALL slot is untouched — critic F73: for a sounding the
        // "station" is this radio's OWN self, and a later bare LINKED renders
        // whatever the call slot holds.
        Assert.Equal("AAA", _state.Ale.LinkedStation);
        Assert.Equal("01", _state.Ale.LinkedChannel);
    }

    [Fact]
    public void TheExchangeToken_CarriesBothShapes_ScheduleRowFirst()
    {
        // ONE token, TWO captured shapes. The listing row (2026-08-16) must be
        // byte-identical in behaviour after the branch, and the progress line
        // (P14b) must write NO schedule row — the branch ORDER is the pin.
        var listing = Parse("EXCHANGE I1              INTERVAL 01:00 START TIME 22:34");
        Assert.True(listing.Handled);
        Assert.True(listing.Changed);
        Assert.False(_state.Ale.LinkState.IsConfirmed);      // a listing is no link state

        var progress = Parse("EXCHANGE KC1HAS           CHANNEL: 30");
        Assert.True(progress.Handled);
        Assert.Equal(AleLinkState.Exchanging, _state.Ale.LinkState.Value);
        Assert.Equal("KC1HAS", _state.Ale.LqaStation);
        Assert.Equal("30", _state.Ale.LqaChannel);

        // (The schedule MIRROR commits only on a read's closing sentinel, so
        // the "a progress line writes no row" half of the invariant is pinned
        // end-to-end in Round11ReadStoreTests, over a real read.)
    }

    [Fact]
    public void TheShowBlocksLqaSoundLine_NamesNoStation_AndRejectsAPayload()
    {
        // P14c, the SH taken mid-run: "LQA/SOUND" stands where SCANNING stands
        // otherwise, ONE token with no payload, and it does NOT say whether a
        // sounding or an exchange is running — so it names no station either,
        // and it must not invent one.
        Parse("SCANNING");
        var r = Parse("LQA/SOUND");

        Assert.True(r.Handled);
        Assert.Equal(AleLinkState.Lqa, _state.Ale.LinkState.Value);
        Assert.Null(_state.Ale.LqaStation);                  // it names no station
        Assert.Null(_state.Ale.LqaChannel);

        // A payload makes it a shape no capture shows — unrecognized, and the
        // state it already set stays.
        Assert.False(Parse("LQA/SOUND RUNNING").Handled);
        Assert.Equal(AleLinkState.Lqa, _state.Ale.LinkState.Value);
    }

    [Theory]
    // NEWS — the app knows of no run, so the line is the only thing it has.
    [InlineData("", true, AleLinkState.Lqa)]                   // unreported
    [InlineData("SCANNING", true, AleLinkState.Lqa)]
    [InlineData("SCAN STOPPED", true, AleLinkState.Lqa)]
    // NOT NEWS — a state already on air. Five name a KIND this line would
    // replace…
    [InlineData("SOUNDING W6HOS            CHANNEL: 30", false, AleLinkState.Sounding)]
    [InlineData("EXCHANGE KC1HAS           CHANNEL: 30", false, AleLinkState.Exchanging)]
    [InlineData("CALLING  AAA              CHANNEL: 01", false, AleLinkState.Calling)]
    [InlineData("SENDING  AAA              CHANNEL: 01", false, AleLinkState.Sending)]
    [InlineData("LINKED AAA", false, AleLinkState.Linked)]
    // …and the EIGHTH is Lqa ITSELF (audit round 1, MINOR 1): a REPEATED `SH`
    // during one run — the pane's status read, a Console read, a campaign's
    // lap. Nothing moves, so nothing may be reported as moved.
    [InlineData("LQA/SOUND", false, AleLinkState.Lqa)]
    public void TheLqaSoundLine_IsNewsOnlyWhenTheAppKnowsOfNoRun(
        string prior, bool expectedChanged, AleLinkState expectedState)
    {
        // MANAGER RULING 2026-08-23, from the phase-5 wire leg: a mid-run `SH`
        // was DOWNGRADING the banner from "SOUNDING W6HOS — CH 28" to the
        // kind-unknown "LQA IN PROGRESS" for 11 s. `LQA/SOUND` is the LESS
        // specific report of the same fact; it confirms the run, it does not
        // replace what the radio already named. Handled in EVERY row — the
        // line is always recognized; `Changed` is the honest half, and this
        // parser's contract is that it means a mirror VALUE moved.
        if (prior.Length > 0) Parse(prior);
        var r = Parse("LQA/SOUND");

        Assert.True(r.Handled);
        Assert.Equal(expectedChanged, r.Changed);
        Assert.Equal(expectedState, _state.Ale.LinkState.Value);

        // ANTI-VACUITY for the "not news" rows: the state the app already had
        // is not merely unmoved, it is still CONFIRMED — nothing was
        // unconfirmed on the way through.
        Assert.True(_state.Ale.LinkState.IsConfirmed);
    }

    [Theory]
    [InlineData("SCANNING", AleLinkState.Scanning)]
    [InlineData("SCAN STOPPED", AleLinkState.Stopped)]
    public void TheRunsTerminators_ClearTheLqaSlot(string line, AleLinkState expected)
    {
        // P14c ends with a bare SCANNING (scan resumes); P14b's ST abort ends
        // with SCAN STOPPED. Either way the run is over and its channel is not
        // a fact about the radio any more.
        Parse("SOUNDING W6HOS            CHANNEL: 30");
        Parse(line);

        Assert.Equal(expected, _state.Ale.LinkState.Value);
        Assert.Null(_state.Ale.LqaStation);
        Assert.Null(_state.Ale.LqaChannel);
    }

    [Fact]
    public void AfterASounding_ABareLinked_RendersNoStaleSelf()
    {
        // Critic F73, the defect the separate slot exists to prevent: if a
        // sounding wrote this radio's own self into the CALL slot, the next
        // bare LINKED would claim a link to ourselves.
        Parse("SOUNDING W6HOS            CHANNEL: 30");
        Parse("SCANNING");
        Parse("LINKED");

        Assert.Equal(AleLinkState.Linked, _state.Ale.LinkState.Value);
        Assert.Null(_state.Ale.LinkedStation);
        Assert.Null(_state.Ale.LqaStation);
    }

    [Fact]
    public void ARankListing_SurvivesAnLqaThatStartsMidReport()
    {
        // Critic F68: a QUEUED LQA fires on its own clock, so its lines can
        // land inside a RANK listing. Before item I those tokens were not
        // survivors and would have ended the continuation, dropping every
        // remaining CHAN: row on the floor.
        Parse("RANK  BOB ");
        Parse("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Parse("SOUNDING W6HOS            CHANNEL: 30");
        Parse(" TUNING COUPLER ");
        Parse(" TUNE COMPLETE  ");
        Parse("LQA/SOUND");
        Parse("EXCHANGE KC1HAS           CHANNEL: 28");
        Parse("CHAN: 01  SCORE: 85     MEASURED SNR 20  RECEIVED SNR 18");

        Assert.Equal(2, _state.Ale.LqaReport.Count);
        Assert.Equal("01", _state.Ale.LqaReport[1].Channel);

        // ANTI-VACUITY: a token that is NOT a survivor still ends it.
        Parse("MODEM OFF");
        Parse("CHAN: 02  SCORE: 70     MEASURED SNR 10  RECEIVED SNR 09");
        Assert.Equal(2, _state.Ale.LqaReport.Count);
    }

    // ---- Tune outcomes -------------------------------------------------------

    [Fact]
    public void TuneFault_TheRadiosSpelling_SetsFailState()
    {
        Parse(" TUNING COUPLER ");
        var r = Parse("TUNE FAULT");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.True(_state.IsTuneFail);
        Assert.False(_state.IsTuneComplete);
        Assert.False(_state.IsTuning);
    }

    [Fact]
    public void TuneFail_TheDocumentsSpelling_AlsoAccepted()
    {
        Parse(" TUNING COUPLER ");
        Assert.Null(Parse("TUNE FAIL").PayloadError);
        Assert.True(_state.IsTuneFail);
    }

    [Fact]
    public void TuneMarginal_IsAQualifierOnComplete()
    {
        Parse(" TUNING COUPLER ");
        Parse("TUNE MARGINAL");
        Assert.True(_state.IsTuneComplete);
        Assert.True(_state.IsTuneMarginal);
        Assert.False(_state.IsTuneFail);
    }

    [Fact]
    public void TuneLifecycle_UnconfirmsAPreviouslyConfirmedKeyline()
    {
        // A confirmed keyline goes STALE when a tune starts (the radio keys
        // for the tune but reports no KEY line): Confirmed → unconfirmed,
        // re-confirmed only by a real KEY line (F1).
        Parse("KEY OFF ");
        Assert.True(_state.Keyline.IsConfirmed);

        Parse(" TUNING COUPLER ");
        Assert.False(_state.Keyline.IsConfirmed);

        Parse("TUNE FAULT");
        Assert.False(_state.Keyline.IsConfirmed);

        Parse("KEY OFF ");     // the radio actually reporting it
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
    }

    [Fact]
    public void NewTuneClearsThePreviousOutcome()
    {
        Parse(" TUNING COUPLER ");
        Parse("TUNE FAULT");
        Parse(" TUNING COUPLER ");
        Assert.False(_state.IsTuneFail);
        Assert.True(_state.IsTuning);
    }

    // ---- D16: a frequency change resets the tune state -----------------------
    //
    // The coupler's tune is valid for the frequency it tuned AT (the radio's
    // per-frequency TUNE MEMORY — probes P6/P6b). Owner 2026-08-30: "when
    // changing frequency, the tune complete state should be reset."

    /// <summary>Latch a TUNE COMPLETE on a first-confirmed 01600000 pair, and
    /// return the raise log armed from that point on.</summary>
    private List<RadioProperty> TunedAt1600ThenWatch()
    {
        ParseAllHandled("RxFr 01600000", "TxFr 01600000", " TUNING COUPLER ", " TUNE COMPLETE  ");
        Assert.True(_state.IsTuneComplete);

        var raised = new List<RadioProperty>();
        _state.Changed += raised.Add;
        return raised;
    }

    [Fact]
    public void D16_AConfirmedRxFrequencyMovingClearsTheTuneOutcome_AndRaises()
    {
        var raised = TunedAt1600ThenWatch();

        Parse("RxFr 03596000");

        Assert.False(_state.IsTuneComplete);
        Assert.False(_state.IsTuneMarginal);
        Assert.False(_state.IsTuneFail);
        Assert.Contains(RadioProperty.TuneComplete, raised);
    }

    [Fact]
    public void D16_AConfirmedTxFrequencyMovingClearsTheTuneOutcome_AndRaises()
    {
        var raised = TunedAt1600ThenWatch();

        Parse("TxFr 03596000");

        Assert.False(_state.IsTuneComplete);
        Assert.Contains(RadioProperty.TuneComplete, raised);
    }

    [Fact]
    public void D16_ARereportOfTheSameFrequencyKeepsTheTune_AndRaisesNothing()
    {
        // Every `SH` block re-reads both frequencies; a re-read must never
        // blank a tune the coupler still holds.
        var raised = TunedAt1600ThenWatch();

        ParseAllHandled("RxFr 01600000", "TxFr 01600000");

        Assert.True(_state.IsTuneComplete);
        Assert.Empty(raised);
    }

    [Fact]
    public void D16_TheFirstConfirmationOfASession_ClearsNothing_AndRaisesNoTuneEvent()
    {
        // No prior confirmed value = no transition. The flags are false anyway;
        // what this forbids is the SPURIOUS raise.
        var raised = new List<RadioProperty>();
        _state.Changed += raised.Add;

        ParseAllHandled("RxFr 01600000", "TxFr 01600000");

        Assert.False(_state.IsTuneComplete);
        Assert.DoesNotContain(RadioProperty.TuneComplete, raised);
        Assert.DoesNotContain(RadioProperty.TuneMarginal, raised);
        Assert.DoesNotContain(RadioProperty.TuneFail, raised);
    }

    [Fact]
    public void D16_AMarginalTuneClearsTheSameWay()
    {
        ParseAllHandled("RxFr 01600000", " TUNING COUPLER ", "TUNE MARGINAL");
        Assert.True(_state.IsTuneComplete);
        Assert.True(_state.IsTuneMarginal);

        Parse("RxFr 03596000");

        Assert.False(_state.IsTuneComplete);
        Assert.False(_state.IsTuneMarginal);
    }

    [Fact]
    public void D16_AFaultedTuneClearsTheSameWay()
    {
        ParseAllHandled("RxFr 01600000", " TUNING COUPLER ", "TUNE FAULT");
        Assert.True(_state.IsTuneFail);

        Parse("RxFr 03596000");

        Assert.False(_state.IsTuneFail);
    }

    [Fact]
    public void D16_AFrequencyReportMidTune_LeavesTheTuningTransientAlone()
    {
        // `_isTuning` is governed by its own lines: the tune in flight is FOR
        // the new frequency, and blanking the on-air indicator mid-transmission
        // is exactly the §9 B1 defect. The terminal still latches afterwards.
        ParseAllHandled("RxFr 01600000", " TUNING COUPLER ");
        Assert.True(_state.IsTuning);

        Parse("RxFr 03596000");
        Assert.True(_state.IsTuning);

        Parse(" TUNE COMPLETE  ");
        Assert.True(_state.IsTuneComplete);
        Assert.False(_state.IsTuning);
    }

    [Fact]
    public void D16_TheCapturedChannelChange_ClearsONCE_AcrossThePairedRxAndTxMove()
    {
        // VERBATIM windows from bench/transcripts/p2b-channel-select-20260822-220517.jsonl:
        // the `sh-before` window-end line list (JSONL line 38, CHAN 25 @ 21432500)
        // and the `sh-after-select` one (JSONL line 83, CHAN 12 @ 03596000), with
        // the `select` window's own answer (line 49) between them. A tune is
        // latched on the first channel; the second channel's SH block moves BOTH
        // frequencies, and the coupler's outcome must clear exactly once.
        string[] shBefore =
        [
            "SCANNING", "LSTN        ON  ", "KEY_TO_CALL ON  ", "RAD_SIL     OFF ",
            "ALL_CALL    ON  ", "ANY_CALL    ON  ", "MAXCH 020", "TUNETIME 010",
            "TIME_OUT 006", "AMD_DISPLAY ON  ", "CHAN 25 ", "MODE USB",
            "RxFr 21432500", "TxFr 21432500", "KEY OFF ", "MODEM OFF", "DV OFF",
            "DGT_SQUELCH OFF", "AVS OFF", "ENCRYPT OFF", "RWAS DISABLED", "ALE> ",
        ];
        string[] shAfterSelect =
        [
            "SCANNING", "LSTN        ON  ", "KEY_TO_CALL ON  ", "RAD_SIL     OFF ",
            "ALL_CALL    ON  ", "ANY_CALL    ON  ", "MAXCH 020", "TUNETIME 010",
            "TIME_OUT 006", "AMD_DISPLAY ON  ", "CHAN 12 ", "MODE USB",
            "RxFr 03596000", "TxFr 03596000", "KEY OFF ", "MODEM OFF", "DV OFF",
            "DGT_SQUELCH OFF", "AVS OFF", "ENCRYPT OFF", "RWAS DISABLED", "ALE> ",
        ];

        ParseAllHandled(shBefore);
        ParseAllHandled(" TUNING COUPLER ", " TUNE COMPLETE  ");
        Assert.True(_state.IsTuneComplete);

        ParseAllHandled("CHAN 16 ", "ALE> ");           // the select window's answer
        Assert.True(_state.IsTuneComplete);             // a channel number alone clears nothing

        var raised = new List<RadioProperty>();
        _state.Changed += raised.Add;
        ParseAllHandled(shAfterSelect);

        Assert.False(_state.IsTuneComplete);
        // ONE tune notification for the pair: `RxFr` clears, and the `TxFr`
        // move that follows finds the flags already false, so SetTuneFlags
        // raises nothing. The two FREQUENCY raises are separate by nature —
        // they are two different mirrored values.
        Assert.Equal(1, raised.Count(p => p == RadioProperty.TuneComplete));
        Assert.DoesNotContain(RadioProperty.TuneMarginal, raised);
        Assert.DoesNotContain(RadioProperty.TuneFail, raised);
        Assert.Equal(1, raised.Count(p => p == RadioProperty.RxFrequency));
        Assert.Equal(1, raised.Count(p => p == RadioProperty.TxFrequency));
    }

    // ---- Hopset generation + sync lifecycles (R9/R9b) ---------------------

    [Fact]
    public void HopsetGenerationLifecycle_R9bCapture()
    {
        ParseAllHandled("NET  00", "Wait...", "Generating Hopset...");
        Assert.True(_state.Hop.IsGeneratingHopset);

        ParseAllHandled(" TUNING COUPLER ", " TUNE COMPLETE  ");
        Assert.False(_state.Hop.IsGeneratingHopset);

        Parse("Hopnum 0041");
        Assert.Equal(41, _state.Hop.HopNum.Value);
    }

    [Fact]
    public void SyncLifecycle_R9bCapture_IncludingFailure()
    {
        Parse("Sending_Sync_Req");
        Assert.Equal(HopSyncState.SendingSyncRequest, _state.Hop.SyncState.Value);

        Parse("Awaiting_Sync");
        Assert.Equal(HopSyncState.AwaitingSync, _state.Hop.SyncState.Value);

        Parse("Sync_Failed");
        Assert.Equal(HopSyncState.SyncFailed, _state.Hop.SyncState.Value);

        Parse("In_Sync");
        Assert.Equal(HopSyncState.InSync, _state.Hop.SyncState.Value);
    }

    [Fact]
    public void AsyncNoHopset_ZeroesHopnumAndEndsGeneration()
    {
        Parse("Hopnum 0041");
        Parse("Generating Hopset...");
        Parse("No Hopset");
        Assert.Equal(0, _state.Hop.HopNum.Value);
        Assert.False(_state.Hop.IsGeneratingHopset);
    }

    [Fact]
    public void HopPromptEndsGeneration_ExcludePathQuirk()
    {
        // The EXCLUDE path prints "Generating Hopset..." with none of the
        // usual clearing lines (session-16).
        Parse("Generating Hopset...");
        Parse("HOP>");
        Assert.False(_state.Hop.IsGeneratingHopset);
    }

    [Fact]
    public void ListInvalid_IsParsedAsOperatorFacingState()
    {
        var r = Parse("List_Invalid");
        Assert.True(r.Handled);
        Assert.True(_state.Hop.IsHopListInvalid);
    }

    // ---- `Bad Hopset` / `Bad_Hopset` — the FIFTH refusal (round 16 S3) -----
    // BOTH spellings are CAPTURED in
    // bench/transcripts/r14-coupler-20260820-121753.jsonl (P-1 run A, step
    // S5): the async form WITH a space at record 240, the `SH` sync-state
    // slot's underscore form at record 265. protocol.md:1058 documents them as
    // the fifth generation-refusal token, and until this round NEITHER was
    // recognized — so every HOP `SH` of such a net raised an "Unrecognized
    // message" banner at the operator (the class the WB_Invalid/Exclusions
    // keys closed).
    //
    // What the fix CLAIMS is recognition plus ending a generation IN PROGRESS.
    // Neither captured window carries a preceding `Generating Hopset...` (the
    // probe's HOPSET rewrite window opens after it), so both pins SEED the
    // generation and assert the FALSE EDGE — the shape `No Hopset`'s own pins
    // use above.

    [Fact]
    public void BadHopset_TheAsyncSpelling_IsRecognized_AndEndsGeneration()
    {
        var raised = new List<RadioProperty>();
        _state.Changed += p => { if (p == RadioProperty.HopGeneratingHopset) raised.Add(p); };

        Parse("Generating Hopset...");
        Assert.True(_state.Hop.IsGeneratingHopset);

        // Record 240, verbatim: "Wait...\r\nBad Hopset\r\n".
        ParseAllHandled("Wait...", "Bad Hopset");

        Assert.False(_state.Hop.IsGeneratingHopset);
        // Both edges, in order: the seed's true edge and the refusal's false one.
        Assert.Equal(
            [RadioProperty.HopGeneratingHopset, RadioProperty.HopGeneratingHopset],
            raised);
    }

    [Fact]
    public void BadHopset_TheUnderscoreSpelling_InTheShBlock_IsRecognized_AndEndsGeneration()
    {
        // Record 265's window-end `lines`, verbatim and in capture order — the
        // whole HOP `SH` block, with `Bad_Hopset` where a sync state would sit.
        // EVERY line handled is the claim: today the block's tenth line raises
        // the banner.
        ParseAllHandled(
            "NET  09", "KEY OFF ", "NETID    09  87654321", "Hoptype 09 WB  ",
            "Hopset 09  04000   06000 ", "Hopnum 0000", "MODEM OFF", "ENCRYPT OFF",
            "POWER hi ", "Bad_Hopset", "HOP> ");

        // The generation edge is asserted SEPARATELY, off a fresh seed: this
        // captured block's own `Hopnum 0000` (line six) already ends a
        // generation, so asserting the flag after the block would pass without
        // `Bad_Hopset` doing anything at all.
        Parse("Generating Hopset...");
        Assert.True(_state.Hop.IsGeneratingHopset);
        Parse("Bad_Hopset");
        Assert.False(_state.Hop.IsGeneratingHopset);
    }

    [Fact]
    public void Bad_WithAnyOtherPayload_StillSurfacesUnrecognized()
    {
        // The NIT-1 opt-out idiom: only the captured payload is claimed, so an
        // unseen `BAD …` line still reaches the operator instead of being
        // swallowed by the new key. A CONSTRUCTED negative — never captured.
        var r = Parse("BAD SOMETHING");
        Assert.False(r.Handled);
    }

    // ---- Net-scoped transient state on a net change (Stage 5 audit F1) ------

    [Fact]
    public void NetChange_UnconfirmsSyncState_AndClearsListInvalid()
    {
        // Sync state and List_Invalid are properties OF the current net: a
        // confirmed net CHANGE must unconfirm them (net A's badge/chip must
        // not carry onto net B); the radio re-reports both when still true.
        ParseAllHandled("NET  03", "List_Invalid", "No_Sync");
        Assert.True(_state.Hop.SyncState.IsConfirmed);
        Assert.True(_state.Hop.IsHopListInvalid);

        Parse("NET  01");                                 // confirmed net CHANGE
        Assert.False(_state.Hop.SyncState.IsConfirmed);   // back to "—"
        Assert.False(_state.Hop.IsHopListInvalid);

        Parse("No_Sync");                                 // the new net's SH re-reports
        Assert.Equal(HopSyncState.NoSync, _state.Hop.SyncState.Value);
    }

    [Fact]
    public void NetChange_MirrorIsConsistent_WhenTheFirstRaiseFires()
    {
        // Stage 8 deferred-ledger fix: the net-scoped unconfirms must be
        // MUTATED before the HopCurrentNet raise, so no Changed handler can
        // observe the new net together with the old net's sync state or
        // List_Invalid badge (previously the raise fired first — a window
        // narrower than a render cycle, but observable to event handlers).
        ParseAllHandled("NET  03", "List_Invalid", "Sync_Failed");

        bool observedInconsistency = false;
        bool sawCurrentNetRaise = false;
        _state.Changed += p =>
        {
            if (p != RadioProperty.HopCurrentNet) return;
            sawCurrentNetRaise = true;
            if (_state.Hop.SyncState.IsConfirmed || _state.Hop.IsHopListInvalid)
                observedInconsistency = true;
        };

        Parse("NET  01");                                 // confirmed net CHANGE

        Assert.True(sawCurrentNetRaise);
        Assert.False(observedInconsistency);
    }

    [Fact]
    public void FirstNetReport_IsLearningNotAChange_SyncStateKept()
    {
        // Unconfirmed → confirmed is the app LEARNING the net (every HOP SH
        // block carries a NET line) — same first-sight convention as the
        // trigger table; nothing may unconfirm on it.
        Parse("No_Sync");
        Parse("NET  01");
        Assert.True(_state.Hop.SyncState.IsConfirmed);
    }

    [Fact]
    public void SameNetRereport_KeepsSyncAndBadge()
    {
        ParseAllHandled("NET  01", "List_Invalid", "No_Sync");
        Parse("NET  01");                                 // every SH re-reports the net
        Assert.True(_state.Hop.SyncState.IsConfirmed);
        Assert.True(_state.Hop.IsHopListInvalid);
    }

    // ---- A generation starts an UNREPORTED sync epoch (round 15 N1, §3.1) ----

    [Fact]
    public void GeneratingHopset_UnconfirmsSyncState_AndRaisesItAfterTheGenerationFlag()
    {
        // Owner ruling Q1 = a: sync is a property of the hopset the net runs
        // on, so generating a new one drops it. The radio does not say so on
        // re-entry (P7), which is exactly why the mirror must go UNREPORTED
        // rather than be told a value — "—" is the third state (I-1/I-4).
        ParseAllHandled("NET  01", "In_Sync");
        Assert.True(_state.Hop.SyncState.IsConfirmed);

        var raised = new List<RadioProperty>();
        _state.Changed += p =>
        {
            if (p is RadioProperty.HopGeneratingHopset or RadioProperty.HopSyncState) raised.Add(p);
            // Stage-8 ordering: everything is mutated before the FIRST raise,
            // so no handler can see "generating" with the old sync chip.
            if (p == RadioProperty.HopGeneratingHopset)
                Assert.False(_state.Hop.SyncState.IsConfirmed);
        };

        Parse("Generating Hopset...");

        Assert.True(_state.Hop.IsGeneratingHopset);
        Assert.False(_state.Hop.SyncState.IsConfirmed);
        Assert.Equal(
            [RadioProperty.HopGeneratingHopset, RadioProperty.HopSyncState],
            raised);
    }

    [Fact]
    public void GeneratingHopset_WhileAlreadyGenerating_RaisesNothing()
    {
        // The equality guard is the edge rule: only FALSE→TRUE unconfirms, so
        // a repeated line cannot wipe a sync report that arrived during the
        // generation.
        ParseAllHandled("NET  01", "Generating Hopset...", "In_Sync");
        Assert.True(_state.Hop.SyncState.IsConfirmed);

        var raised = new List<RadioProperty>();
        _state.Changed += raised.Add;
        Parse("Generating Hopset...");

        Assert.Empty(raised);
        Assert.True(_state.Hop.SyncState.IsConfirmed);
    }

    [Fact]
    public void TheGenerationClearers_LeaveSyncAlone()
    {
        // Every TRUE→FALSE clearer (Hopnum / No Hopset / the HOP prompt)
        // touches nothing on the sync mirror — the epoch ends when the radio
        // reports sync again, not when the generation ends.
        foreach (var clearer in new[] { "Hopnum 0041", "No Hopset", "HOP>" })
        {
            var state = new RadioState();
            var parser = new ResponseParser(state);
            foreach (var line in new[] { "NET  01", "Generating Hopset...", "In_Sync", clearer })
                Assert.True(parser.Parse(line).Handled, "Unhandled: " + line);

            Assert.False(state.Hop.IsGeneratingHopset);
            Assert.True(state.Hop.SyncState.IsConfirmed);
            Assert.Equal(HopSyncState.InSync, state.Hop.SyncState.Value);
        }
    }

    [Fact]
    public void AGenerationOnAnUnreportedSync_RaisesOnlyTheGenerationFlag()
    {
        // Nothing to unconfirm: the sync raise is not fired speculatively.
        Parse("NET  01");
        var raised = new List<RadioProperty>();
        _state.Changed += raised.Add;

        Parse("Generating Hopset...");
        Assert.Equal([RadioProperty.HopGeneratingHopset], raised);
    }

    // ---- No-Hopset counter (Stage 5 audit F4) --------------------------------

    [Fact]
    public void NoHopsetLines_AlwaysBumpTheCounter_BothForms()
    {
        // The line is the only reliable no-generation-outcome signal: HopNum
        // may already be a confirmed 0, which re-raises no change event.
        Assert.Equal(0, _state.Hop.NoHopsetCount);
        Parse("No Hopset");                               // async form
        Assert.Equal(1, _state.Hop.NoHopsetCount);
        Parse("No_Hopset");                               // SH-block form
        Assert.Equal(2, _state.Hop.NoHopsetCount);
        Parse("No Hopset");                               // HopNum already 0 — still counts
        Assert.Equal(3, _state.Hop.NoHopsetCount);
        Assert.Equal(0, _state.Hop.HopNum.Value);
    }

    // ---- No-Net-ID counter (bench 2026-08-16) --------------------------------

    [Fact]
    public void NoNetIdLines_AlwaysBumpTheCounter_BothForms()
    {
        // Selecting a net that HAS a hopset but NO net ID refuses to generate:
        // async " NO NET ID " and, in the SH block, "No_Net_ID" (docs/probes.md
        // S2). BOTH forms went unhandled before this — the async one fell into
        // the ["NO"] handler and matched none of its branches, and the SH form
        // had no handler at all, so the app could not say why nothing generated.
        // A counter for the same reason as No-Hopset: HopNum may already be 0.
        _state.Hop.SetHopNum(41);
        Assert.Equal(0, _state.Hop.NoNetIdCount);

        Parse("NO NET ID");                               // async form
        Assert.Equal(1, _state.Hop.NoNetIdCount);
        Assert.Equal(0, _state.Hop.HopNum.Value);         // generation produced nothing

        Parse("No_Net_ID");                               // SH-block form
        Assert.Equal(2, _state.Hop.NoNetIdCount);

        Parse("NO NET ID");                               // HopNum already 0 — still counts
        Assert.Equal(3, _state.Hop.NoNetIdCount);

        // Distinct from the No-Hopset signal: the two refusals have different
        // causes (no ID vs no hopset) and a consumer must not conflate them.
        Assert.Equal(0, _state.Hop.NoHopsetCount);
    }

    [Fact]
    public void NoNetIdLines_AreRecognized_NotUnhandled()
    {
        // The regression that matters: an unrecognized line is the failure mode
        // this fixed. ParseAllHandled asserts the parser CLAIMS both forms.
        ParseAllHandled("NO NET ID", "No_Net_ID");
    }

    [Fact]
    public void DisBlock_UnprogrammedNet_XPlaceholdersBecomeNull()
    {
        ParseAllHandled("NETID    05  XXXXXXXX", "Hoptype 05 WB  ", "Hopset 05  XXXXXX  XXXXXX");
        Assert.Null(_state.Hop.Nets[5].NetId);
        Assert.Equal(HopType.Wideband, _state.Hop.Nets[5].Type);
        Assert.Null(_state.Hop.Nets[5].WidebandLowKHz);
        Assert.Null(_state.Hop.Nets[5].WidebandHighKHz);
    }

    // ---- WB band edges from the DIS Hopset line (round-5 BD3) ---------------
    // The line used to be discarded as noise, so a WB net could only ever
    // display a placeholder. The PROGRAMMED shape is PROVISIONAL (only the
    // wiped form is captured) — docs/protocol.md carries the marking and the
    // bench item; these pins hold the parse the app is built on.

    [Fact]
    public void DisHopsetLine_ProgrammedWidebandNet_MirrorsBothEdges()
    {
        ParseAllHandled("NETID    02  24680135", "Hoptype 02 WB", "Hopset 02  02000  08000");
        Assert.Equal("02000", _state.Hop.Nets[2].WidebandLowKHz);
        Assert.Equal("08000", _state.Hop.Nets[2].WidebandHighKHz);
        Assert.Equal(HopType.Wideband, _state.Hop.Nets[2].Type);
        Assert.False(_state.Hop.Nets[2].IsReportedUnprogrammed);
    }

    [Fact]
    public void DisHopsetLine_WipedForm_ClearsEdgesThatWereReported()
    {
        // The bench cycle a wipe actually produces: a programmed net, then
        // HOPSET n DEL, whose echo AND next DIS both carry the X-form. Setting
        // the edges first is what stops this pin being vacuous — asserting
        // null on a net nobody ever described proves nothing.
        ParseAllHandled("Hopset 02  02000  08000");
        Assert.Equal("02000", _state.Hop.Nets[2].WidebandLowKHz);

        ParseAllHandled("Hopset 02  XXXXXX  XXXXXX");
        Assert.Null(_state.Hop.Nets[2].WidebandLowKHz);
        Assert.Null(_state.Hop.Nets[2].WidebandHighKHz);
    }

    [Fact]
    public void DisHopsetLine_EdgesAreAPair_BothMutateBeforeTheRaise()
    {
        // Round-4 Phase-D precedent (HopState.SetCurrentNet): net-scoped fields
        // mutate BEFORE the raise, so no Changed handler can observe a mirror
        // that is half-updated — here, a new low against the previous high.
        ParseAllHandled("Hopset 02  02000  08000");

        bool sawRaise = false, observedInconsistency = false;
        _state.Changed += p =>
        {
            if (p != RadioProperty.HopNets) return;
            sawRaise = true;
            var net = _state.Hop.Nets[2];
            if (net.WidebandLowKHz != "03000" || net.WidebandHighKHz != "09000")
                observedInconsistency = true;
        };

        Parse("Hopset 02  03000  09000");

        Assert.True(sawRaise);
        Assert.False(observedInconsistency);
    }

    [Fact]
    public void HopsetLine_ShapeItDoesNotRecognize_StaysNoise()
    {
        // The programmed shape is PROVISIONAL, so an unrecognized Hopset line
        // must not raise a PayloadError against a guess — it stays handled and
        // changes nothing (the pre-round-5 behavior for anything but the two
        // known shapes). "Hopset 02 DEL" is the two-token case.
        ParseAllHandled("Hopset 02  02000  08000");
        var r = Parse("Hopset 02 DEL");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Equal("02000", _state.Hop.Nets[2].WidebandLowKHz);
    }

    [Theory]
    // The two shapes the C1 audit EXECUTED against the unanchored regex: both
    // mutated the mirror as if they were band edges. A value of the wrong
    // WIDTH is not the shape this parser claims to understand — and since the
    // programmed shape is PROVISIONAL, mirroring a near-miss would put a
    // number of unknown units on screen instead of leaving the cell honestly
    // unreported for the bench item to settle.
    [InlineData("Hopset 02  2000  08000")]        // 4-digit low
    [InlineData("Hopset 02  020000  08000")]      // 6-digit low
    [InlineData("Hopset 02  02000  0800")]        // 4-digit high
    [InlineData("Hopset 02  02000  080000")]      // 6-digit high
    [InlineData("Hopset 02  02000  08000  09000")]// a third value
    [InlineData("Hopset 02  02000  08000 kHz")]   // trailing text
    [InlineData("Hopset 02  XXXXX  XXXXX")]       // 5-X, not the captured 6-X
    [InlineData("Hopset 02  XXXXXXX  XXXXXXX")]   // 7-X
    [InlineData("Hopset 02  02000  XXXXXX")]      // mixed: never observed
    public void HopsetLine_NearMissShapes_AreIgnoredEntirely(string line)
    {
        // Precondition: net 2 already carries REAL edges, so "ignored" is
        // observable as "unchanged" rather than as "still null".
        ParseAllHandled("Hopset 02  02000  08000");

        var r = Parse(line);

        Assert.True(r.Handled);          // the token is still recognized…
        Assert.Null(r.PayloadError);     // …and a guessed shape raises no error
        Assert.False(r.Changed);         // …but nothing moved
        Assert.Equal("02000", _state.Hop.Nets[2].WidebandLowKHz);
        Assert.Equal("08000", _state.Hop.Nets[2].WidebandHighKHz);
    }

    [Fact]
    public void HopsetLine_TheTwoDeclaredShapes_AreStillAccepted()
    {
        // Anti-vacuity for the near-miss theory above: an anchored regex that
        // matched NOTHING would pass every one of those cases.
        Assert.True(Parse("Hopset 02  02000  08000").Changed);
        Assert.Equal("02000", _state.Hop.Nets[2].WidebandLowKHz);

        Assert.True(Parse("Hopset 02  XXXXXX  XXXXXX").Changed);
        Assert.Null(_state.Hop.Nets[2].WidebandLowKHz);
    }

    // ---- The reported-unprogrammed marker (round-4 Phase D) ------------------
    // The wire's `NETID n XXXXXXXX` is the ONLY honest signal that a net is
    // unprogrammed. Mapping it to a null ID and stopping there left the app
    // unable to tell "the radio said unprogrammed" from "nobody mentioned the
    // ID" — the mirror now carries the distinction.

    [Fact]
    public void NetIdXForm_MarksTheNetReportedUnprogrammed()
    {
        ParseAllHandled("NETID    05  XXXXXXXX", "Hoptype 05 WB  ");
        Assert.True(_state.Hop.Nets[5].IsReportedUnprogrammed);
        Assert.Null(_state.Hop.Nets[5].NetId);
    }

    [Fact]
    public void RealNetIdReport_ClearsTheUnprogrammedMarker()
    {
        // The bench cycle: a wiped net gets programmed, and the next DIS says
        // so. The marker must not survive the net that disproved it.
        ParseAllHandled("NETID    05  XXXXXXXX");
        Assert.True(_state.Hop.Nets[5].IsReportedUnprogrammed);

        ParseAllHandled("NETID    05  12345678");
        Assert.False(_state.Hop.Nets[5].IsReportedUnprogrammed);
        Assert.Equal("12345678", _state.Hop.Nets[5].NetId);
    }

    [Fact]
    public void HoptypeOnlyReport_SetsNeitherTheIdNorTheMarker()
    {
        // A record created by a Hoptype line alone proves NOTHING about the
        // ID — and "record with only a type" is not an unprogrammed signature
        // either (protocol.md: a wiped net reports BOTH NETID XXXXXXXX and a
        // Hoptype WB line).
        ParseAllHandled("Hoptype 04 NB  ");
        Assert.Null(_state.Hop.Nets[4].NetId);
        Assert.False(_state.Hop.Nets[4].IsReportedUnprogrammed);
    }

    [Fact]
    public void HopList_Session16Capture_Parses()
    {
        Parse("HOPLIST 03   11010  11015  11020");
        Assert.Equal(["11010", "11015", "11020"], _state.Hop.HopLists[3]);
    }

    // ---- Bandwidth matrix answers (R5) -------------------------------------

    [Theory]
    [InlineData("BAND 1.0 ", "1.0")]
    [InlineData("BAND 2.7 ", "2.7")]
    [InlineData("BAND 3.0 ", "3.0")]
    [InlineData("BAND 6.0 ", "6.0")]
    [InlineData("BAND 0.35", "0.35")]
    [InlineData("BAND .35", "0.35")]     // sub-1 spelling tolerated both ways
    public void BandAnswers_AreTheReadback(string line, string expected)
    {
        Assert.Null(Parse(line).PayloadError);
        Assert.Equal(expected, _state.Bandwidth.Value);
    }

    [Fact]
    public void ModeAnswer_CarriesTrailingBandLine_R11()
    {
        // "MODE USB" answers MODE + a trailing BAND line announcing the
        // per-modulation default reset (R11).
        ParseAllHandled("MODE USB", "BAND 2.7 ");
        Assert.Equal(ModulationMode.Usb, _state.ModulationMode.Value);
        Assert.Equal("2.7", _state.Bandwidth.Value);
    }

    [Fact]
    public void FmModeCascade_R5Capture_ParsesWhateverArrives()
    {
        // Cascade line-sets vary between captures — parse what arrives.
        ParseAllHandled("MODE FM ", "BAND 2.7 ", "FMDEV 8.0", "FMTONE ON ");
        Assert.Equal(ModulationMode.Fm, _state.ModulationMode.Value);
    }

    // ---- DV excursion (R4) ---------------------------------------------------

    [Fact]
    public void DvExcursion_R4Capture_AllLinesHandled()
    {
        // DV lines are recognized (not mirrored — v1 does not display DV);
        // the AGC answer rides with an RFG line.
        ParseAllHandled(
            "MODE USB", "BAND 2.7 ",
            "MODEM OFF", "DV ON ", "DGT_SQUELCH OFF",
            "DV ON ", "DGT_SQUELCH OFF",
            "MODE CW ", "BAND 1.0 ",
            "DV OFF", "DGT_SQUELCH OFF",
            "MODE USB", "BAND 2.7 ",
            "AGC MED ", "RFG 100 ");
        Assert.Equal(AgcSpeed.Medium, _state.AgcSpeed.Value);
    }

    // ---- PORT_R dump (R1) ------------------------------------------------------

    [Fact]
    public void PortRemoteDump_R1Capture_PopulatesPortConfig()
    {
        ParseAllHandled(
            "PORT_REMOTE BAUD 9600",
            "PORT_REMOTE BITS 8",
            "PORT_REMOTE PARITY none",
            "PORT_REMOTE STOP 1",
            "PORT_REMOTE ECHO OFF",
            "PORT_REMOTE XON_XOFF disable");

        Assert.Equal("9600", _state.PortBaud.Value);
        Assert.Equal("8", _state.PortBits.Value);
        Assert.Equal("NONE", _state.PortParity.Value);
        Assert.Equal("1", _state.PortStopBits.Value);
        Assert.Equal(OnOff.Off, _state.PortRemoteEcho.Value);
        Assert.Equal("DISABLE", _state.PortXonXoff.Value);
    }

    // ---- Battery, power chatter, errors ------------------------------------

    [Fact]
    public void BatteryStatus_StoredVerbatim()
    {
        Parse("Battery Status FULL 31.4V");
        Assert.Equal("Status FULL 31.4V", _state.BatteryStatus.Value);
    }

    [Fact]
    public void PowerCutbackChatter_TrackedNotAnError()
    {
        Parse("POWER hi ");
        var r = Parse("POWER CUTBACK   ");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Equal(PowerLevel.High, _state.PowerLevel.Value);   // level unchanged
        Assert.True(_state.PowerCutback.Value);

        Parse("POWER RESTORED   ");
        Assert.False(_state.PowerCutback.Value);
    }

    [Fact]
    public void ErrorBanner_IsRecognized()
    {
        var r = Parse("** ERROR **");
        Assert.True(r.Handled);
        Assert.Equal("**", r.Token);
    }

    [Fact]
    public void UnrecognizedLine_IsFlagged()
    {
        Assert.False(Parse("BOGUS NONSENSE 42").Handled);
    }

    [Fact]
    public void RecognizedTokenWithBadPayload_ReportsPayloadError()
    {
        var r = Parse("MODE WTF");
        Assert.True(r.Handled);
        Assert.NotNull(r.PayloadError);
    }

    // ---- MODEM forms -----------------------------------------------------------

    [Fact]
    public void ModemSelectionEcho_SetsActiveModem()
    {
        // R8: "MODEM 1 T39" is the selection echo / SH short form.
        Parse("MODEM 1 T39 ");
        Assert.Equal("1 T39", _state.ActiveModem.Value);

        Parse("MODEM OFF");
        Assert.Equal("OFF", _state.ActiveModem.Value);
    }

    [Fact]
    public void ModemPresetListingLine_FeedsThePresetsMirror_NeverActiveModem()
    {
        // Round 8 (EE): the LISTING form (ASYNC/SYNC before TYPE — the
        // bench-pinned discriminator) feeds the ModemPresets mirror,
        // "PRESET" stripped. ActiveModem stays untouched — a listing says
        // what is STORED, not what is engaged.
        var r = Parse("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.False(_state.ActiveModem.IsConfirmed);
        Assert.Equal(["1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long"],
            _state.ModemPresets);
    }

    [Fact]
    public void ModemPresetLine_MissingEitherToken_StaysRecognizedOnly()
    {
        // R8-review MAJOR 2: the discriminator needs BOTH tokens — the pin
        // is "ASYNC/SYNC BEFORE TYPE", and a line missing the data-format
        // token is an UNCAPTURED shape that must never be guessed into the
        // mirror.
        //
        // CLONE-FIELD ROUND 2 F9: the "no TYPE" half of this pin MOVED. A
        // TYPE-less line WAS uncaptured when this test was written; probe P5
        // captured it as the `HOP>` form (`MODEM PRESET 7 DAT7 ASYNC REMOTE
        // BAUD 300`), so it is now a listing and belongs in the mirror — see
        // ModemShortHopForm_… below. What remains uncaptured, and still stays
        // recognized-only, is a TYPE-less line with NO BAUD either: the short
        // form's shape is name + mode phrase + BAUD, and anything less is not
        // a shape any capture shows.
        var r1 = Parse("MODEM PRESET 1 T39  ASYNC DATA");               // no TYPE, no BAUD
        Assert.True(r1.Handled);
        Assert.Empty(_state.ModemPresets);

        var r2 = Parse("MODEM PRESET 1 T39  TYPE 39tone  BAUD 2400");   // no ASYNC/SYNC
        Assert.True(r2.Handled);
        Assert.Empty(_state.ModemPresets);

        // BAUD BEFORE the mode phrase is not the short form either — the
        // captured order is name, mode, BAUD.
        var r3 = Parse("MODEM PRESET 1 T39  BAUD 2400  ASYNC DATA");
        Assert.True(r3.Handled);
        Assert.Empty(_state.ModemPresets);
    }

    /// <summary>
    /// CLONE-FIELD ROUND 2 F9 — the SHORT <c>HOP&gt;</c> preset line, replayed
    /// from probe P5's transcript
    /// (<c>bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl</c>,
    /// labels <c>HOP-pre-7</c> / <c>HOP-pre-8</c> / <c>HOP-pre-9</c>) and from
    /// P5b's <c>SYNC DATA</c> write echo
    /// (<c>p5b-hop-modem-preset-write-20260821-181018.jsonl</c>, label
    /// <c>T4</c>). Presets 7-9 exist at a <c>HOP&gt;</c> prompt in a line with
    /// NO <c>TYPE</c> and no <c>INTER</c> column, and the round-8
    /// discriminator dropped every one of them — which is why the clone never
    /// carried them.
    /// </summary>
    [Fact]
    public void ModemShortHopForm_UpsertsTheMirror_TrailingColumnPaddingAndAll()
    {
        // VERBATIM, trailing spaces included, as the transcript recorded them.
        Parse("MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300   ");
        Parse("MODEM PRESET 8 DAT8 ASYNC REMOTE BAUD 300   ");
        Parse("MODEM PRESET 9 TST9 SYNC  DATA   BAUD 300   ");

        Assert.Equal(
            ["7 DAT7 ASYNC REMOTE BAUD 300", "8 DAT8 ASYNC REMOTE BAUD 300", "9 TST9 SYNC  DATA   BAUD 300"],
            _state.ModemPresets);
    }

    [Fact]
    public void ModemShStatusForm_TypeBeforeAsync_StaysRecognizedOnly()
    {
        // The MODEM SH answer with a preset active is the SAME fields with
        // TYPE first (protocol.md "MODEM SH vs MODEM PRE") — it is a STATUS
        // line, not a listing, and must not pollute the presets mirror (the
        // radio sometimes swallows MODEM SH during init bursts, so the two
        // must never be confused).
        var r = Parse("MODEM PRESET 1 T39  TYPE 39tone  ASYNC DATA   BAUD 2400  INTER long");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Empty(_state.ModemPresets);
        Assert.False(_state.ActiveModem.IsConfirmed);
    }

    [Fact]
    public void ModemPresetListing_UpsertsByPresetNumber_TheWriteEchoReplaces()
    {
        // A MODEM PRE listing then a programming echo for the same preset:
        // the echo REPLACES that preset's row (no duplicates); a different
        // preset appends.
        Parse("MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        Parse("MODEM PRESET 2 FSK1  ASYNC DATA   BAUD 75  TYPE fskws  INTER long");
        Parse("MODEM PRESET 1 T39  ASYNC DATA   BAUD 1200  TYPE 39tone  INTER long");

        Assert.Equal(
            ["1 T39  ASYNC DATA   BAUD 1200  TYPE 39tone  INTER long",
             "2 FSK1  ASYNC DATA   BAUD 75  TYPE fskws  INTER long"],
            _state.ModemPresets);
    }

    // ---- TXMSG continuation ----------------------------------------------------

    [Fact]
    public void TxMsgListing_HeaderThenTextContinuation()
    {
        Parse("TXMSG 09");
        Parse("  MEET AT GRID 0900 ");
        Assert.Single(_state.Ale.TxMessages);
        Assert.Equal(9, _state.Ale.TxMessages[0].Slot);
        Assert.Equal("MEET AT GRID 0900", _state.Ale.TxMessages[0].Text);
    }

    [Fact]
    public void TxMsgText_BeginningWithProtocolKeyword_IsTextNotState()
    {
        Parse("KEY OFF ");                      // establish a keyline report
        Parse("TXMSG 00");
        Parse("KEY OFF AT NOON");               // message text, not a KEY line
        Assert.Equal("KEY OFF AT NOON", _state.Ale.TxMessages[0].Text);
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
    }

    [Fact]
    public void TxMsgHeaderFollowedByPrompt_DoesNotEatThePrompt()
    {
        Parse("TXMSG 03");
        var r = Parse("ALE>");
        Assert.True(r.Handled);
        Assert.Equal(OperatingMode.Ale, _state.OperatingMode.Value);
        Assert.Empty(_state.Ale.TxMessages);
    }

    [Fact]
    public void TxMsgContinuation_DoesNotLeakOntoLaterLines()
    {
        Parse("TXMSG 00");
        Parse("HELLO");
        Parse("KEY OFF ");     // must be handled normally now
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
        Assert.Single(_state.Ale.TxMessages);
    }

    // ---- The TXMSG ASYNC LINE (round 16 fixes S2) -------------------------
    // The line after a `TXMSG nn` header is stored as the message text unless
    // it is a mode prompt — so an async line there BECAME the message, and the
    // real text then surfaced unrecognized. The scan is usually running at
    // `ALE>`, which is the only prompt the whole TXMSG family answers at
    // (protocol.md's TXMSG row), so the exposure is real.
    //
    // The fix routes six FULL-LINE shapes as themselves and leaves the header
    // ARMED. The set is DELIBERATELY shorter than S1's: message text is free
    // operator text, so each predicate widens an ambiguity, and the HOP-only
    // lines (`Wait...`, `WB_Invalid`, `Generating Hopset...`) and the tune
    // lines cannot arrive at an `ALE>` TXMSG listing anyway.
    //
    // The interleaved SHAPE is ASSUMED — no capture shows it (P19). The LINES
    // are verbatim captures.

    [Theory]
    [InlineData("SCANNING")]                          // r11-ale-race record 34, at ALE>
    [InlineData("SCAN STOPPED")]                      // r11-ale-race record 40, at ALE>
    [InlineData("KEY OFF ")]                          // r11-ale-race record 38, at ALE>
    [InlineData("IN_PROG")]                           // r11-ale-race record 38, at ALE>
    [InlineData("Battery Status FULL 26.4V")]         // r11-ale-race record 38, at ALE>
    [InlineData("POWER CUTBACK   ")]                  // p8 L74 (inside an SSB SH; ASSUMED at ALE>)
    public void TxMsgText_AnEnumeratedAsyncLine_IsItself_AndTheHeaderStaysArmed(string asyncLine)
    {
        Parse("TXMSG 03");

        var async = Parse(asyncLine);
        Assert.True(async.Handled, "Unhandled: " + asyncLine);
        Assert.Null(async.PayloadError);
        Assert.Empty(_state.Ale.TxMessages);          // it did NOT become the message
        AssertOwnEffect(asyncLine);

        // …and the REAL text still lands in the slot the header named.
        Parse("HELLO");
        var message = Assert.Single(_state.Ale.TxMessages);
        Assert.Equal(3, message.Slot);
        Assert.Equal("HELLO", message.Text);
    }

    [Fact]
    public void TxMsgText_WholeLineMatchOnly()
    {
        // The bound on S2's irreducible ambiguity: only an EXACT whole-line
        // match is read as the async event. A stored message that BEGINS with
        // one of the six words is still text — the existing
        // `KEY OFF AT NOON` pin's own rule, restated for the new predicates.
        Parse("TXMSG 03");
        Parse("SCANNING NOW");

        var message = Assert.Single(_state.Ale.TxMessages);
        Assert.Equal("SCANNING NOW", message.Text);
        Assert.False(_state.Ale.LinkState.IsConfirmed);   // no link state was moved
    }

    // ---- RANK report -------------------------------------------------------------

    [Fact]
    public void RankReport_ParsesScores()
    {
        Parse("RANK  BOB ");
        Parse("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");

        var lqa = Assert.Single(_state.Ale.LqaReport);
        Assert.Equal("BOB", lqa.Station);
        Assert.Equal("00", lqa.Channel);
        Assert.Equal("---", lqa.Score);
    }

    [Fact]
    public void RankReport_SurvivesInterleavedAsyncChatter()
    {
        Parse("RANK  BOB ");
        Parse("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Parse("SCANNING");
        Parse("POWER CUTBACK   ");
        Parse("CHAN: 01  SCORE: 85     MEASURED SNR 20  RECEIVED SNR 18");
        Assert.Equal(2, _state.Ale.LqaReport.Count);
    }

    [Fact]
    public void NewRank_ClearsThePreviousReport()
    {
        Parse("RANK  BOB ");
        Parse("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Parse("RANK  SUE ");
        Assert.Empty(_state.Ale.LqaReport);
        Parse("CHAN: 00  SCORE: 90     MEASURED SNR 25  RECEIVED SNR 22");
        Assert.Equal("SUE", _state.Ale.LqaReport[0].Station);
    }

    // ---- HELP-block suppression -----------------------------------------------
    // Every banner and menu line below is VERBATIM from the old repo's
    // transcripts (sessions 05/06/07/10/19) — one test per real banner
    // family, each proving suppression engages on the banner, swallows menu
    // lines that would otherwise corrupt state, and disengages at the next
    // prompt (audit round 1, F2).

    private void AssertSuppressed(params string[] lines)
    {
        foreach (var line in lines)
        {
            var r = Parse(line);
            Assert.True(r.Handled, "Not suppressed: " + line);
            Assert.Null(r.PayloadError);
        }
    }

    [Fact]
    public void SsbHelp_Session19Banner_Suppresses()
    {
        // "SSB Commands:" family. The AGc/MODE menu line would throw a
        // payload error and " Keyline - (ON/OFf) ... ZERO ..." carries junk.
        AssertSuppressed(
            "SSB Commands:                                                                 ",
            "------------------------------------------------------------------------------- ",
            " AGc         - (OFf/SLow/MEd/FAst/DAta)  MODE        - (USb/LSb/AMe/CW/FM)      ",
            " CHan        - (00...99)                 SQuelch     - (ON/OFf)                 ",
            " Keyline     - (ON/OFf)                  ZERO        - Clears Radio Memory      ",
            "** HElp MORE gives additional commands **                                       ");
        Assert.False(_state.AgcSpeed.IsConfirmed);
        Assert.False(_state.ModulationMode.IsConfirmed);
        Assert.False(_state.OperatingChannel.IsConfirmed);

        Parse("SSB> ");                          // prompt ends the block
        Parse("POWER hi ");                      // parsing resumes
        Assert.Equal(PowerLevel.High, _state.PowerLevel.Value);
    }

    [Fact]
    public void SsbHelpMoreAndRwas_Session19Banners_Suppress()
    {
        // "MORE SSB Commands:" and "SSB RWAS Commands:" families.
        AssertSuppressed(
            "MORE SSB Commands:                                                            ",
            " BATtery     - (STatus) battery voltage  KWATt       - (YEs/NO) 1KW installed?  ",
            " DATe        - (mm/dd/yy)                PROGram     - see HElp SECurity        ");
        Parse("SSB> ");

        AssertSuppressed(
            "SSB RWAS Commands:                                                            ",
            " RWAS             - (ENAble/DISable) Robust Wakeup Active Squelch               ",
            " UNKEY_Mask       - (ENAble/DISable) ignore unkey postamble                     ");
        Assert.False(_state.BatteryStatus.IsConfirmed);

        Parse("SSB> ");
        Parse("Battery Status FULL 31.4V");
        Assert.True(_state.BatteryStatus.IsConfirmed);
    }

    [Fact]
    public void HopHelp_Session10Banner_Suppresses()
    {
        // "HOP Commands:" family. The HOPList menu line would throw a
        // payload error ("(0..9)" is not a net number) if unsuppressed.
        AssertSuppressed(
            "HOP Commands:",
            "--------------------------------------------------------------------------",
            " DISplay - all nets, (0..9) one net     NEt     - (0..9)                      ",
            " HOPList - (0..9) (ADD) (freq) ...      SSb     - single channel mode         ",
            " HOPSet  - (0..9) DELete                ZEROize - clear radio memory          ");
        Assert.Empty(_state.Hop.HopLists);
        Assert.False(_state.Hop.CurrentNet.IsConfirmed);

        Parse("HOP> ");
        Parse("NET  00");
        Assert.Equal(0, _state.Hop.CurrentNet.Value);
    }

    [Fact]
    public void AleHelpProg_Session06Banner_Suppresses()
    {
        // "… commands consist of:" family (HELP PROG). TXMsg's menu line
        // would arm the text continuation; SLFADdr/RXMsg carry junk.
        AssertSuppressed(
            "Embedded Adaptive PROGRAM commands consist of:",
            " ERASE                  - Clear ALE Addresses",
            " SLFADdr                - (Address) (Chan Group <0-9>)",
            " TXMsg                  - (Message Number <0-9> (Up to 90 characters)",
            " ZERO                   - clear radio memory");
        Assert.Empty(_state.Ale.SelfAddresses);
        Assert.Empty(_state.Ale.TxMessages);

        Parse("ALE> ");
        Parse("KEY OFF ");                       // NOT eaten as TXMSG text
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
        Assert.Empty(_state.Ale.TxMessages);
    }

    [Fact]
    public void AleHelpOper_Session06Banner_Suppresses()
    {
        // HELP OPER: "SCAn - start scanning" would set LinkState, "RANk …"
        // would wipe the LQA report.
        Parse("RANK  BOB ");
        Parse("CHAN: 00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --");
        Assert.Single(_state.Ale.LqaReport);

        AssertSuppressed(
            "Embedded Adaptive OPERATIONAL commands consist of:",
            "------------------------------------------------------------------------",
            " SCAn       - start scanning",
            " RANk       - (Individual Address)",
            " EXCHange   - (STArt/STOp) (Address) (interval hh:mm) (start hh:mm)");
        Assert.False(_state.Ale.LinkState.IsConfirmed);
        Assert.Single(_state.Ale.LqaReport);     // report not wiped

        Parse("ALE> ");
        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void AleHelpRootAndSecurity_Session05And06Banners_Suppress()
    {
        // Root HELP ("Embedded Adaptive HELP commands consist of:",
        // session-05) and HELP SECURITY ("Embedded Adaptive SECURITY
        // Commands:", session-06).
        AssertSuppressed(
            "Embedded Adaptive HELP commands consist of:",
            " HElp PRog     - Embedded Adaptive PROGRAM     Help Menu",
            " HElp SECurity - Embedded Adaptive SECURITY    Help Menu");
        Parse("ALE> ");

        AssertSuppressed(
            "Embedded Adaptive SECURITY Commands:                                          ",
            "'LOCk' prevents changes to the parameter type, and 'UNLock' allows changes.     ",
            " SELect KEY       - (LOCk/UNLock) encryption keys                               ",
            "NOTE: These are Front Panel commands that take effect after cycling power!      ");
        Parse("ALE> ");
        Parse("SCANNING");
        Assert.Equal(AleLinkState.Scanning, _state.Ale.LinkState.Value);
    }

    [Fact]
    public void ModemHelp_Session07Banner_SuppressesAndResumesAtPrompt()
    {
        // "Modem commands:" family — the banner itself starts with the
        // MODEM token and would otherwise be a payload error. The fixture's
        // tail is session-07 verbatim: the prompt line arrives fused with an
        // async POWER line ("<CR>ALE> POWER med"), which the framer splits;
        // parsing must resume exactly there.
        AssertSuppressed(
            "Modem commands:                                                                ",
            "------------------------------------------------------------------------------- ",
            "Prefixed by MODEM:                                                              ",
            " OFf       - disable modem               PREset    - show all modem presets     ",
            " SHow      - show current modem info     xxxx      - set modem to preset xxxx   ",
            "*** capital letters denote acceptable abbreviation                              ");
        Assert.False(_state.ActiveModem.IsConfirmed);

        Parse("ALE>");
        Parse(" POWER med");
        Parse("POWER CUTBACK   ");
        Assert.Equal(PowerLevel.Medium, _state.PowerLevel.Value);
        Assert.True(_state.PowerCutback.Value);
    }

    // ---- DI channel dump (session-23) --------------------------------------------

    [Fact]
    public void ChannelDump_AccumulatesRawLines()
    {
        Parse("CH 00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Parse("CH 01 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO");
        Parse("CHAN 00 ");
        Assert.Equal(2, _state.ChannelList.Count);
        Assert.StartsWith("00 RxFr", _state.ChannelList[0]);
        Assert.Equal(0, _state.OperatingChannel.Value);
    }

    // ---- Rejection lines ------------------------------------------------------------

    [Theory]
    [InlineData(" INV SELF ADDRESS ")]
    [InlineData(" INV IND ADDRESS ")]
    [InlineData(" ADDRESS EXISTS ")]
    [InlineData("Invalid In Hopping")]
    public void RejectionLines_AreRecognized(string line)
    {
        Assert.True(Parse(line).Handled);
    }

    // ---- Misc noise the radio emits ----------------------------------------------

    [Theory]
    [InlineData("ALE_INST  rf5122")]
    [InlineData("Wait...")]
    [InlineData("AGC MED ")]
    [InlineData("RFG 100 ")]
    [InlineData("Step 00001000")]
    [InlineData("DAY Monday   ")]
    [InlineData("DATE 01/27/92")]
    [InlineData("TIME 20:37:12")]
    [InlineData("FMDEV 8.0")]
    [InlineData("FMTONE ON ")]
    [InlineData("COMPRESS ON")]
    [InlineData("Exclude 00  02000   03000 ")]
    [InlineData("Module 01A  Revision 8214B")]
    [InlineData("ENCRYPTION NOT INSTALLED")]
    [InlineData("AVS NOT INSTALLED")]
    [InlineData("PORT_DATA   BAUD 2400")]
    public void KnownRadioLines_NeverParseAsErrors(string line)
    {
        var r = Parse(line);
        Assert.True(r.Handled, "Unhandled: " + line);
        Assert.Null(r.PayloadError);
    }

    [Fact]
    public void AsyncKeyOnOff_FromKeying_NeverAParseError()
    {
        // Keying emits async KEY ON / KEY OFF (owner knowledge, B7).
        var r = Parse("KEY ON ");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Equal(KeylineState.On, _state.Keyline.Value);

        Parse("KEY OFF ");
        Assert.Equal(KeylineState.Off, _state.Keyline.Value);
    }

    [Fact]
    public void StepAnswer_SetsFrequencyStep()
    {
        Parse("Step 00001000");
        Assert.Equal(Falcon.Core.Protocol.FrequencyStep.OneKHz, _state.FrequencyStep.Value);
    }

    // ---- Phase R settings mirrors (plan-gui-rejigger.md round 4) --------------
    // Replay doctrine: every fixture line below is verbatim from a capture
    // (R4/R5 probes, sessions 14/20/23, or the protocol.md bench-confirmed
    // blocks). PREAMP/INTCOUPLER/KWATT left the recognized-as-noise set in
    // UI-tweaks round 3, and LIGHT/INTENSITY in round 4, on OLD-APP-DERIVED
    // evidence: their pins are marked PROVISIONAL and live in their own
    // sections further down.

    [Fact]
    public void DvAnswer_R4Capture_MirrorsDvAndTheDgtSquelchRider()
    {
        // "DV" query answers "DV x" + "DGT_SQUELCH x" (probe R4). Each line
        // parses standalone into its own INDEPENDENT mirror (plan F5).
        ParseAllHandled("DV ON ", "DGT_SQUELCH OFF");
        Assert.Equal(OnOff.On, _state.DigitalVoice.Value);
        Assert.Equal(OnOff.Off, _state.DigitalSquelch.Value);

        ParseAllHandled("DV OFF", "DGT_SQUELCH OFF");
        Assert.Equal(OnOff.Off, _state.DigitalVoice.Value);
    }

    [Fact]
    public void DgtSquelch_IndependentOfDv_BenchTable()
    {
        // The DGT_S probe table (protocol.md): setting either squelch leaves
        // the other untouched.
        ParseAllHandled("SQUELCH OFF", "DGT_SQUELCH ON ");
        Assert.Equal(OnOff.Off, _state.AnalogSquelch.Value);
        Assert.Equal(OnOff.On, _state.DigitalSquelch.Value);
        Assert.False(_state.DigitalVoice.IsConfirmed);   // no DV line arrived
    }

    [Fact]
    public void FmSquelchQuery_Capture_MirrorsSquelchAndType()
    {
        // "FMSQ -> FMSQUELCH ON | FMSQ_TYPE tone" (protocol.md squelch
        // section). Type is mirrored VERBATIM (uppercased) — "tone" is the
        // only captured spelling.
        ParseAllHandled("FMSQUELCH ON", "FMSQ_TYPE tone");
        Assert.Equal(OnOff.On, _state.FmSquelch.Value);
        Assert.Equal("TONE", _state.FmSquelchType.Value);
    }

    [Fact]
    public void FmModeCascade_R5Capture_MirrorsDeviationAndTone()
    {
        ParseAllHandled("MODE FM ", "BAND 2.7 ", "FMDEV 8.0", "FMTONE ON ");
        Assert.Equal("8.0", _state.FmDeviation.Value);
        Assert.Equal(OnOff.On, _state.FmTone.Value);
    }

    [Fact]
    public void CompressAnswer_R5NegativeControl_Mirrors()
    {
        Parse("COMPRESS ON");
        Assert.Equal(OnOff.On, _state.Compression.Value);
    }

    [Fact]
    public void RfgRider_R4Capture_MirrorsRfGain()
    {
        // "AG MED" answers "AGC MED" + "RFG 100" (probe R4).
        ParseAllHandled("AGC MED ", "RFG 100 ");
        Assert.Equal(100, _state.RfGain.Value);
    }

    [Fact]
    public void RwasAnswers_Session20AndProtocol_MirrorBothValues()
    {
        // "RWAS DISABLED" is session-20 verbatim; ENABLED is the documented
        // counterpart (protocol.md RWAS table: query returns RWAS ENABLED /
        // RWAS DISABLED).
        Parse("RWAS DISABLED");
        Assert.Equal(EnabledDisabled.Disabled, _state.Rwas.Value);
        Parse("RWAS ENABLED");
        Assert.Equal(EnabledDisabled.Enabled, _state.Rwas.Value);
    }

    [Fact]
    public void UnkeyMaskAnswer_Session20Capture_Mirrors()
    {
        Parse("UNKEY_M DISABLED");
        Assert.Equal(EnabledDisabled.Disabled, _state.UnkeyMask.Value);
    }

    [Fact]
    public void ForceWakeupEnabled_TheAsymmetricAnswer_IsRecognizedNotMirrored()
    {
        // "FORCE WAKEUP ENABLED" (protocol.md RWAS table). Deliberately NOT
        // mirrored: disabling is silent with no read-back — a mirror would
        // latch a stale ENABLED forever.
        var r = Parse("FORCE WAKEUP ENABLED");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
    }

    [Fact]
    public void AvsAnswers_Session14_MirroredVerbatim()
    {
        // The SH block prints "AVS OFF" even on a cardless radio; the direct
        // query answers "AVS NOT INSTALLED" (protocol.md COMSEC) — the
        // mirror shows whichever the radio last said.
        Parse("AVS OFF");
        Assert.Equal("OFF", _state.Avs.Value);
        Parse("AVS NOT INSTALLED");
        Assert.Equal("NOT INSTALLED", _state.Avs.Value);
    }

    [Fact]
    public void EncryptionAnswers_Session14_MirrorStateAndAvailability()
    {
        // "ENCR -> ENCRYPTION NOT INSTALLED | ENCRYPT OFF" (protocol.md).
        ParseAllHandled("ENCRYPTION NOT INSTALLED", "ENCRYPT OFF");
        Assert.Equal("NOT INSTALLED", _state.EncryptionAvailability.Value);
        Assert.Equal(OnOff.Off, _state.Encryption.Value);
    }

    [Fact]
    public void CurKeyAnswer_ProtocolShape_MirroredVerbatim()
    {
        // "CUR_KEY XX" (XX = slot or none) — protocol.md COMSEC table.
        Parse("CUR_KEY none");
        Assert.Equal("NONE", _state.CurrentEncryptionKey.Value);
    }

    [Fact]
    public void BeepAnswer_Session20Capture_Mirrors()
    {
        Parse("BEEP ON ");
        Assert.Equal(OnOff.On, _state.Beep.Value);
    }

    [Fact]
    public void PrePostDump_Session20Capture_MirrorsAllThreeVerbatim()
    {
        ParseAllHandled(
            "PREPOST FILTER ENABLE",
            "PREPOST RXANTENNA DISABLE",
            "PREPOST SCAN SLOW");
        Assert.Equal("ENABLE", _state.PrePostFilter.Value);
        Assert.Equal("DISABLE", _state.PrePostRxAntenna.Value);
        Assert.Equal("SLOW", _state.PrePostScanRate.Value);
    }

    [Fact]
    public void ContrastAnswer_SentinelTableShape_MirrorsAsInt()
    {
        // Shape documented in protocol.md's sentinel table ("CONT" →
        // "CONTRAST nn"); the numeric payload is representative — no raw
        // archive of a specific value exists (classification table).
        Parse("CONTRAST 05");
        Assert.Equal(5, _state.Contrast.Value);
    }

    [Fact]
    public void LevelAnswer_Session20Capture_StaysNoise()
    {
        // "LEVEL rs-232" — port level is out of scope; recognized only.
        var r = Parse("LEVEL rs-232 ");
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
    }

    [Theory]
    [InlineData("DV WTF")]
    [InlineData("FMSQUELCH MAYBE")]
    [InlineData("RWAS SOMETIMES")]
    [InlineData("BEEP LOUD")]
    [InlineData("ENCRYPT 42")]
    public void PhaseRMirrorTokens_JunkPayloads_ArePayloadErrors(string line)
    {
        // Recognized token + junk payload = honest payload error, never a
        // silently-mirrored lie.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.NotNull(r.PayloadError);
    }

    // (UI-tweaks round 4, AC: LIGHT/INTENSITY LEFT the recognized-as-noise set
    // — the round-3 note that they had "no old-app evidence either" was wrong.
    // Their PROVISIONAL pins are in the round-4 section below.)

    // ---- UI-tweaks round 3, V7: PROVISIONAL answer shapes ---------------------
    // OLD-APP-DERIVED, NOT bench-captured (docs/protocol.md "Old-app-derived
    // SSB query set (PROVISIONAL — bench-unconfirmed)"; matching CONFIRM items
    // in docs/bench-checklist.md). The old WinForms app parses these three
    // tokens (old repo src/Falcon.Core/Protocol/ResponseParser.cs:272-274) with
    // the spellings in its Wire.cs (BypassState ENABLED/BYPASSED :38-42;
    // YesNoState YES/NO :28-32). We mirror the payload VERBATIM rather than
    // through an enum, so the pins below assert the STORAGE contract (whatever
    // the radio says is what is kept) and not a spelling we have not seen.

    [Theory]
    [InlineData("PREAMP ENABLED", "ENABLED")]
    [InlineData("PREAMP BYPASSED", "BYPASSED")]
    [InlineData("PREAMP bypassed", "BYPASSED")]     // uppercased like every mirror
    public void PreampAnswer_Provisional_MirrorsVerbatim(string line, string expected)
    {
        Assert.False(_state.RxPreamp.IsConfirmed);   // unreported = unconfirmed
        Parse(line);
        Assert.Equal(expected, _state.RxPreamp.Value);
    }

    [Theory]
    [InlineData("INTCOUPLER ENABLED", "ENABLED")]
    [InlineData("INTCOUPLER BYPASSED", "BYPASSED")]
    public void IntCouplerAnswer_Provisional_MirrorsVerbatim(string line, string expected)
    {
        Parse(line);
        Assert.Equal(expected, _state.InternalCoupler.Value);
    }

    [Theory]
    [InlineData("KWATT YES", "YES")]
    [InlineData("KWATT NO", "NO")]
    public void KwattAnswer_Provisional_MirrorsVerbatim(string line, string expected)
    {
        Parse(line);
        Assert.Equal(expected, _state.OneKilowattPa.Value);
    }

    [Fact]
    public void ProvisionalMirrors_SurviveAnUnexpectedSpelling()
    {
        // The whole point of the verbatim contract: if the bench proves the
        // radio says something ELSE, the parser stores it instead of throwing
        // or dropping it — the display then shows the radio's truth and the
        // provisional doc entry gets corrected, not the parser.
        ParseAllHandled("PREAMP ON", "INTCOUPLER OFF", "KWATT NOT INSTALLED");
        Assert.Equal("ON", _state.RxPreamp.Value);
        Assert.Equal("OFF", _state.InternalCoupler.Value);
        Assert.Equal("NOT INSTALLED", _state.OneKilowattPa.Value);
    }

    [Theory]
    [InlineData("PREAMP")]
    [InlineData("INTCOUPLER")]
    [InlineData("KWATT")]
    public void ProvisionalMirrors_BarePayloadlessLine_IsAPayloadErrorNotAConfirmation(string line)
    {
        // A payloadless line confirms nothing — it must never leave the
        // mirror looking like the radio answered.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.NotNull(r.PayloadError);
        Assert.False(_state.RxPreamp.IsConfirmed);
        Assert.False(_state.InternalCoupler.IsConfirmed);
        Assert.False(_state.OneKilowattPa.IsConfirmed);
    }

    // ---- UI-tweaks round 4, AC: PROVISIONAL device answer shapes --------------
    // OLD-APP-DERIVED, NOT bench-captured (docs/protocol.md round-4 provisional
    // subsection; CONFIRM items in docs/bench-checklist.md "Radio settings:
    // device queries"). The WinForms app parses both tokens (old repo
    // src/Falcon.Core/Protocol/ResponseParser.cs:269 and :271) through enums
    // whose spellings live in its Wire.cs (BacklightFunctions OFF|MOMENTARY
    // :182-186; Intensities "00".."08" :187-197). We mirror VERBATIM — not
    // through an enum and NOT through ParseInt — so these pins assert the
    // STORAGE contract, not a spelling we have not seen. INTENSITY's
    // zero-padding is exactly the kind of detail a ParseInt would erase.

    [Theory]
    [InlineData("LIGHT OFF", "OFF")]
    [InlineData("LIGHT MOMENTARY", "MOMENTARY")]
    [InlineData("LIGHT momentary", "MOMENTARY")]    // uppercased like every mirror
    public void BacklightFunctionAnswer_Provisional_MirrorsVerbatim(string line, string expected)
    {
        Assert.False(_state.BacklightFunction.IsConfirmed);   // unreported = unconfirmed
        Parse(line);
        Assert.Equal(expected, _state.BacklightFunction.Value);
    }

    [Theory]
    [InlineData("INTENSITY 00", "00")]
    [InlineData("INTENSITY 08", "08")]
    public void BacklightIntensityAnswer_Provisional_MirrorsVerbatim(string line, string expected)
    {
        Assert.False(_state.BacklightIntensity.IsConfirmed);
        Parse(line);
        Assert.Equal(expected, _state.BacklightIntensity.Value);
    }

    [Fact]
    public void DeviceProvisionalMirrors_SurviveAnUnexpectedSpelling()
    {
        // Same contract as the round-3 provisional mirrors: if the radio says
        // something else — an UNPADDED intensity, a third backlight mode — the
        // parser keeps what it said. The doc entry gets corrected, not the app.
        ParseAllHandled("LIGHT ALWAYS", "INTENSITY 4");
        Assert.Equal("ALWAYS", _state.BacklightFunction.Value);
        Assert.Equal("4", _state.BacklightIntensity.Value);
    }

    [Theory]
    [InlineData("LIGHT")]
    [InlineData("INTENSITY")]
    public void DeviceProvisionalMirrors_BarePayloadlessLine_IsAPayloadErrorNotAConfirmation(string line)
    {
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.NotNull(r.PayloadError);
        Assert.False(_state.BacklightFunction.IsConfirmed);
        Assert.False(_state.BacklightIntensity.IsConfirmed);
    }

    // ====================================================================
    // Round 11 §8 — the shapes the 2026-08-17 characterization campaign
    // captured. Every input line below is VERBATIM from
    // bench/transcripts/*.jsonl (named per block), padding included.
    // ====================================================================

    // ---- WB exclusion bands (phase3-hop-channel) -------------------------

    [Fact]
    public void ExcludeBand_CapturedEcho_UpsertsTheMirrorBySlot()
    {
        // "EXC 0 02000000 03000000" -> "Exclude 00  02000   03000 " — 8-digit
        // Hz in, 5-digit kHz out, trailing space and all. Outside a read
        // operation this is the set ECHO, so it upserts (standalone-line
        // doctrine).
        Assert.Null(_state.Hop.ExcludeBands);                   // unread

        ParseAllHandled("Exclude 00  02000   03000 ");

        var band = Assert.Single(_state.Hop.ExcludeBands!);
        Assert.Equal(0, band.Band);
        Assert.Equal("02000", band.LowKHz);
        Assert.Equal("03000", band.HighKHz);

        // A second slot joins it, sorted by slot; a repeat of slot 0 REPLACES.
        ParseAllHandled("Exclude 01  11000   11500 ", "Exclude 00  04000   05000 ");
        Assert.Equal(
            [(0, "04000", "05000"), (1, "11000", "11500")],
            _state.Hop.ExcludeBands!.Select(b => (b.Band, b.LowKHz, b.HighKHz)));
    }

    [Theory]
    [InlineData("Exclude 00  2000   03000")]        // 4-digit low
    [InlineData("Exclude 00  020000  03000")]       // 6-digit low
    [InlineData("Exclude 00  02000")]               // no high edge
    [InlineData("Exclude 00  02000  03000  04000")] // a third value
    [InlineData("Exclude 10  02000  03000")]        // band outside 0-9
    public void ExcludeBand_AnyOtherShape_MirrorsNothing_AndSurfacesUnrecognized(string line)
    {
        // A PROVISIONAL multi-row shape is exactly the wrong place to be
        // liberal: anything that is not the captured row leaves the mirror
        // unread and flows through the unrecognized-line path.
        var r = Parse(line);
        Assert.False(r.Handled);
        Assert.Null(_state.Hop.ExcludeBands);
    }

    // ---- Membership + schedules: the SHAPES (attribution is the store's) --

    [Theory]
    // phase1-ale-membership: five-space indent, two spaces before the address.
    [InlineData("     MEMBER 01  I2")]
    [InlineData("     MEMBER 02  LOW")]
    [InlineData("     MEMBER 03  BASECAMP1")]
    public void MemberContinuation_CapturedShape_IsHandled(string line)
    {
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.True(r.Changed);
    }

    [Theory]
    [InlineData("MEMBER 01")]              // no address
    [InlineData("MEMBER 01  I2  I3")]      // two addresses
    [InlineData("MEMBER AA  I2")]          // non-numeric index
    public void MemberContinuation_AnyOtherShape_SurfacesUnrecognized(string line)
    {
        Assert.False(Parse(line).Handled);
    }

    [Theory]
    // phase2b-schedules, both spellings, verbatim columns.
    [InlineData("EXCHANGE I1              INTERVAL 01:00 START TIME 22:34")]
    [InlineData("SOUND    S1              INTERVAL 03:00 START TIME 13:02")]
    [InlineData("EXCHANGE I2              INTERVAL 24:00 START TIME 22:00")]
    public void ScheduleRow_CapturedShape_IsHandled(string line)
    {
        // 24:00 included on purpose: the radio does NOT validate intervals, so
        // the parser must mirror what it printed rather than reject it.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.True(r.Changed);
    }

    [Theory]
    [InlineData("EXCHANGE I1  INTERVAL 01:00")]                       // no start
    [InlineData("EXCHANGE I1  INTERVAL 1:00 START TIME 22:34")]        // hh not 2 digits
    [InlineData("SOUND    S1  INTERVAL 03:00 START TIME 13:02 EXTRA")] // trailing junk
    public void ScheduleRow_AnyOtherShape_SurfacesUnrecognized(string line)
    {
        Assert.False(Parse(line).Handled);
    }

    [Theory]
    [InlineData(" NO MEMBERS PRGMD ")]
    [InlineData(" NO LQA SCHEDULED ")]
    public void EmptyStateMarkers_AreRecognizedAsFacts_NotUnrecognizedLines(string line)
    {
        // Both are POSITIVE markers — the radio saying "none" — so neither may
        // fall through the NO handler unnoticed the way they used to.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.True(r.Changed);
        Assert.Null(r.PayloadError);
    }

    // ---- INVALID routing: exactly one family in, two staying out ---------

    [Fact]
    public void InvalidAddress_RoutesToTheProgrammingRefusalMirror()
    {
        ParseAllHandled(" INVALID ADDRESS ");
        Assert.Equal("INVALID ADDRESS", _state.Ale.ProgrammingRefusal.Line);
        Assert.Equal(1, _state.Ale.ProgrammingRefusal.Sequence);
    }

    [Theory]
    [InlineData("INVALID ENCR KEY")]
    [InlineData("INVALID MODEM PRESET")]
    public void OtherInvalidFamilies_StayOutOfTheAleRefusalMirror(string line)
    {
        // Other domains' rejects. Routing one would let the ALE programming
        // gate attribute a crypto or modem refusal to an address write.
        ParseAllHandled(line);
        Assert.Equal(0, _state.Ale.ProgrammingRefusal.Sequence);
        Assert.Null(_state.Ale.ProgrammingRefusal.Line);
    }

    // ---- Keyed channel mirror (round 11 §8) ------------------------------

    [Fact]
    public void ChannelLines_UpsertByChannelNumber_KeepingSiblings()
    {
        // Captured session-23 DI dump shape. Two different channels
        // accumulate; a REPEAT of one replaces its row in place rather than
        // appending a second (the LQA report's per-channel reads depend on
        // both halves).
        ParseAllHandled(
            "CH 04 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO",
            "CH 09 RxFr 14313500 TxFr 14313500 MODE LSB AGC SL BA 3.0  RXONLY YES");
        Assert.Equal(2, _state.ChannelList.Count);

        ParseAllHandled("CH 04 RxFr 07100000 TxFr 07200500 MODE AME AGC MED BA 6.0  RXONLY YES");
        Assert.Equal(2, _state.ChannelList.Count);
        Assert.StartsWith("04 RxFr 07100000", _state.ChannelList[0], StringComparison.Ordinal);
        Assert.StartsWith("09 RxFr 14313500", _state.ChannelList[1], StringComparison.Ordinal);
    }

    // ---- Radio clock (TI / TIME set echo — R9 capture) ------------------------

    [Fact]
    public void TimeTriplet_R9Capture_MirrorsTimeOfDayOnly()
    {
        // TI and each of TIME/DAT/DAY answer the full triplet; only the TOD
        // is mirrored (Stage 5 HOP pane), DATE/DAY stay noise.
        Assert.False(_state.RadioTimeOfDay.IsConfirmed);   // unreported = unconfirmed

        ParseAllHandled("DAY Monday   ", "DATE 01/27/92", "TIME 20:37:12");

        Assert.Equal("20:37:12", _state.RadioTimeOfDay.Value);
    }

    // ====================================================================
    // CLONE ROUND 12 §4 / §9 — the parser tolerances and the new recognizers.
    // ====================================================================

    /// <summary>The two control bytes the captured zeroize settle window
    /// really emits, as VALUES — an invisible byte inside a source literal is
    /// a fixture nobody can review (audit round 1, finding 2).</summary>
    private static readonly string Nul = ((char)0x00).ToString();
    private static readonly string Bel = ((char)0x07).ToString();

    // ---- §4: the two bare HOP markers (r11-exclude captures) ------------

    [Theory]
    [InlineData("WB_Invalid")]
    [InlineData("Exclusions")]
    public void TheBareHopMarkers_AreRecognized_NotAnUnrecognizedBanner(string line)
    {
        // VERBATIM from bench/transcripts/r11-exclude-20260818-182614: both
        // ride the HOP `SH` block, and `WB_Invalid` additionally rides EVERY
        // `EXC` write's regeneration answer. Unrecognized before round 12 —
        // i.e. every exclusion write raised an error banner at the operator.
        var r = Parse(line);
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
    }

    [Fact]
    public void TheHopShBlock_WithBothMarkers_ParsesWholeAndStillMirrorsTheNet()
    {
        // The captured block, verbatim, in order (same transcript, `SH` after
        // a WB net select). The markers must not disturb the rows around them.
        ParseAllHandled(
            "NET  09", "KEY OFF ", "NETID    09  87654321", "Hoptype 09 WB  ",
            "Hopset 09  02000   08000 ", "Hopnum 0000", "MODEM OFF",
            "ENCRYPT OFF", "POWER low", "WB_Invalid", "Exclusions");

        Assert.Equal(9, _state.Hop.CurrentNet.Value);
        Assert.Equal("87654321", _state.Hop.Nets[9].NetId);
        Assert.Equal("02000", _state.Hop.Nets[9].WidebandLowKHz);
    }

    [Fact]
    public void ExcludeRowsInsideADisRecord_MirrorTheBandTable()
    {
        // A bare `DIS` answer ENDS with the exclusion table (captured
        // 2026-08-18, r11-exclude-b): ten net triplets and then the Exclude
        // rows. The rows arrive OUTSIDE any `EXC` read window, so they take
        // the published-upsert path — and the net records must survive them.
        ParseAllHandled(
            "NETID    00  12345678", "Hoptype 00 NB  ", "Center 00  07295 ",
            "NETID    01  23456789", "Hoptype 01 NB  ", "Center 01  07250 ",
            "Exclude 00  02000   03000 ");

        Assert.Equal("12345678", _state.Hop.Nets[0].NetId);
        Assert.Equal("23456789", _state.Hop.Nets[1].NetId);
        var band = Assert.Single(_state.Hop.ExcludeBands!);
        Assert.Equal(new HopExcludeBand(0, "02000", "03000"), band);
    }

    // ---- §9 B2: the `**` banner discrimination --------------------------

    [Fact]
    public void OnlyTheExactErrorBanner_FeedsTheAleRefusalMirror()
    {
        var r = Parse("** ERROR **");
        Assert.True(r.Handled);
        Assert.Equal("**", r.Token);
        Assert.Equal("** ERROR **", _state.Ale.ProgrammingRefusal.Line);
    }

    [Fact]
    public void AnotherBannerKeepsItsPayload_AndNeverPoisonsTheRefusalMirror()
    {
        // THE CAPTURE THAT PROVES THIS IS NOT THEORETICAL: the zeroize settle
        // window emits this banner unsolicited between the wipe and the
        // returning prompt. Before round 12 it was fed into the ALE refusal
        // mirror as the literal "** ERROR **" and its payload dropped — so an
        // ALE programming bracket open at the time would have attributed a
        // syntax reject to its own write.
        //
        // BYTE-FAITHFUL (audit round 1, finding 2): the captured line carries
        // THREE TRAILING BELS —
        //   '\x00\r\n*** ZEROIZE COMPLETE ***\x07\x07\x07\r\n'
        // (bench/transcripts/r12-p1-20260818-222442.jsonl). An earlier version
        // of this fixture quietly dropped them, which is how BEL taint reached
        // the operator's own error text unnoticed.
        var r = Parse("*** ZEROIZE COMPLETE ***" + Bel + Bel + Bel);

        Assert.True(r.Handled);
        Assert.Equal("**", r.Token);
        Assert.Equal("ZEROIZE COMPLETE", r.Payload);
        Assert.Equal("ZEROIZE COMPLETE", r.RawPayload);
        Assert.DoesNotContain(Bel, r.RawPayload!, StringComparison.Ordinal);
        Assert.Null(_state.Ale.ProgrammingRefusal.Line);
    }

    [Fact]
    public void ANulOnlyLine_IsHandled_NotAnUnrecognizedBanner()
    {
        // Captured in the same settle window: one poll answered a BARE NUL and
        // the next opened with another, so the framer emitted a line of two
        // NULs. `Trim()` does not remove them (they are not whitespace), so the
        // line reached the dispatch table, matched nothing, and raised
        // "Unrecognized message" at the operator — for line noise.
        var r = Parse(Nul + Nul);
        Assert.True(r.Handled);
        Assert.Null(r.PayloadError);
        Assert.Equal("", r.Token);
    }

    [Fact]
    public void ControlBytes_AreStrippedFromParsing_ButNeverFromAPayloadsMeaning()
    {
        // The stripper is load-bearing for the two pins above, so it is pinned
        // as a unit: control bytes go, everything else — spacing included —
        // stays exactly as the radio sent it.
        Assert.Equal("SQUELCH ON ", ResponseParser.StripControlCharacters("SQUELCH" + Nul + " ON" + Bel + " "));
        Assert.Equal("", ResponseParser.StripControlCharacters(Nul + Nul));
        // …and a line with nothing to strip comes back UNCHANGED (the common
        // case, and the one that must not pay for the two above).
        Assert.Equal("RXONLY NO ", ResponseParser.StripControlCharacters("RXONLY NO "));
    }

    [Fact]
    public void ABannerPayload_KeepsItsOriginalCase()
    {
        // The operator sees the RADIO'S words. Uppercasing them would be the
        // app editing the evidence.
        var r = Parse("***RX Only***");
        Assert.Equal("RX ONLY", r.Payload);
        Assert.Equal("RX Only", r.RawPayload);
        Assert.Null(_state.Ale.ProgrammingRefusal.Line);
    }

    // ---- §9 A1: the PRESET DISABLED recognizer --------------------------

    [Fact]
    public void PresetDisabled_IsRecognized()
    {
        // The spelling comes from the app's OWN "Unrecognized message" banner
        // at the bench — the defect was the capture.
        var r = Parse("PRESET DISABLED");
        Assert.True(r.Handled);
        Assert.Equal("PRESET", r.Token);
        Assert.Equal("DISABLED", r.Payload);
    }

    [Fact]
    public void AnUnseenPresetForm_StillSurfacesAsUnrecognized()
    {
        // The SCAN/CHGROUP precedent: the branch recognizes exactly what was
        // captured, so a form nobody has seen is not swallowed by it.
        Assert.False(Parse("PRESET SOMETHING ELSE").Handled);
    }

    // ---- §9 C3: the FORCE WAKEUP latch ----------------------------------

    [Fact]
    public void ForceWakeupEnabled_ConfirmsTheLatch()
    {
        Assert.False(_state.ForceWakeup.IsConfirmed);
        var r = Parse("FORCE WAKEUP ENABLED");
        Assert.True(r.Handled);
        Assert.True(_state.ForceWakeup.IsConfirmed);
        Assert.Equal(EnabledDisabled.Enabled, _state.ForceWakeup.Value);
    }

    [Fact]
    public void AnUnseenForceForm_IsRecognizedButChangesNothing()
    {
        // FORCE was already a recognized-as-noise token; keeping unseen forms
        // handled preserves that, while the latch moves only on the captured
        // line.
        var r = Parse("FORCE SOMETHING");
        Assert.True(r.Handled);
        Assert.False(_state.ForceWakeup.IsConfirmed);
    }

    // ---- §3: the lockout report rows ------------------------------------

    [Fact]
    public void ALockoutRowWithoutItsSectionHeader_MirrorsNothing()
    {
        // A set ECHO is byte-identical to a report row and carries no header.
        // The parser must not attribute it — the mirror has no rows to show.
        var r = Parse("PROGRAM DATA LOCK");
        Assert.True(r.Handled);
        Assert.Equal(LockoutReadState.Unknown, _state.Lockouts.State);
        Assert.Empty(_state.Lockouts.Rows);
    }

    [Fact]
    public void APromptEndsTheReport_SoALaterEchoIsNotAttributedToTheLastSection()
    {
        // Without the prompt reset, an echo arriving after a report would
        // inherit the last header's section — inventing exactly the fact the
        // (family, section, item) key exists to protect.
        ParseAllHandled(">>HOP_Programmable_Parameters", "PROGRAM DATA UNLOCK", "SSB>");
        var r = Parse("PROGRAM DATA LOCK");
        Assert.True(r.Handled);
        Assert.Equal(LockoutReadState.Unknown, _state.Lockouts.State);
    }

    [Fact]
    public void ARowOutsideTheClosedInventory_OptsOutOfHandled()
    {
        ParseAllHandled(">>SSB_Programmable_Parameters");
        Assert.False(Parse("PROGRAM WIDGET LOCK").Handled);
        // …and a real item under the WRONG family's header is refused too.
        Assert.False(Parse("SELECT CHAN LOCK").Handled);
    }

    [Fact]
    public void AMalformedLockoutRow_OptsOutOfHandled()
    {
        ParseAllHandled(">>SSB_Programmable_Parameters");
        Assert.False(Parse("PROGRAM CHAN").Handled);            // no state token
        Assert.False(Parse("PROGRAM CHAN SIDEWAYS").Handled);   // not LOCK/UNLOCK
        Assert.False(Parse("PROGRAM CHAN LOCK EXTRA").Handled); // three tokens
    }
}
