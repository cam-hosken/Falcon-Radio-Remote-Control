using Falcon.App.Core.Demo;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// The DEMO radio (plan/plan-demo-radio.md) over the REAL stack —
/// DemoSerialPort under the production SerialTransport, Prc138Radio and
/// RadioSession (no fakes above the byte seam). These double as the suite's
/// only end-to-end wiring tests: bytes → framer → parser → mirror → session.
/// The demo device is NOT part of the test doctrine (docs/tests.md) — these
/// tests pin the demo script itself, not radio behavior; bench/ stays
/// authoritative for the wire.
/// </summary>
public sealed class DemoRadioTests : IDisposable
{
    private readonly DemoSerialPort _demo = new() { ResponseDelayMs = 0, TuneTerminalDelayMs = 0 };
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;

    public DemoRadioTests()
    {
        _transport = new SerialTransport(_demo) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        // Always on: Drain() reads the log to tell a settled wire from a busy
        // one, and it is used by helpers every test reaches.
        CaptureWire();
    }

    private static PortSettings DemoSettings => new() { PortName = DemoSerialPort.DemoPortName };

    /// <summary>Demo responses arrive on the demo read thread — poll.</summary>
    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        Assert.True(condition(), "timed out waiting for: " + what);
    }

    private void ConnectReady()
    {
        _session.Connect(DemoSettings);
        WaitUntil(() => _session.Phase == SessionPhase.Ready, "session Ready over DEMO");
    }

    private void SelectModeConfirmed(OperatingMode mode)
    {
        _radio.SelectMode(mode);
        WaitUntil(
            () => _radio.State.OperatingMode.IsConfirmed && _radio.State.OperatingMode.Value == mode,
            "confirmed mode " + mode);
    }

    [Fact]
    public void Connect_OverDemoPort_ReachesReady()
    {
        ConnectReady();
        Assert.Equal(DemoSerialPort.DemoPortName, _transport.PortName);
    }

    [Fact]
    public void ConnectRitual_CannedSsbSh_PopulatesTheMirror()
    {
        ConnectReady();
        // The ritual's SH answered with the captured SSB block.
        WaitUntil(() => _radio.State.RxFrequency.IsConfirmed, "RxFr confirmed from canned SH");
        Assert.Equal("01600000", _radio.State.RxFrequency.Value);
        Assert.Equal(ModulationMode.Usb, _radio.State.ModulationMode.Value);
        Assert.Equal("2.7", _radio.State.Bandwidth.Value);
        Assert.Equal(PowerLevel.High, _radio.State.PowerLevel.Value);
    }

    [Fact]
    public void ModeLap_PromptsFlipTheConfirmedMode()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ale);
        SelectModeConfirmed(OperatingMode.Hop);
        SelectModeConfirmed(OperatingMode.Ssb);
    }

    [Fact]
    public void ShInHop_ServesTheHopBlock()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Hop);
        _radio.Show();
        WaitUntil(() => _radio.State.Hop.HopNum.IsConfirmed, "Hopnum confirmed from HOP SH");
        Assert.Equal(41, _radio.State.Hop.HopNum.Value);
    }

    // ---- Round-5 BC1-BC3: the canned HOP net table ---------------------------
    // The demo answers DIS / DIS n / HOPLIST n so the HOP panes have nets to
    // render. These pin the demo SCRIPT (values and shapes) through the real
    // parser and mirror — bench/ stays authoritative for the wire.

    private void HopReady()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Hop);
    }

    /// <summary>Collect the wire lines the demo emits for one net's DIS
    /// triplet — the only way to see a line the mirror would silently
    /// swallow.</summary>
    private List<string> WatchNetLines(int net, Action send, int expected)
    {
        var seen = new List<string>();
        string number = net.ToString("00");
        void Watch(object? _, MessageReceivedEventArgs e)
        {
            // "NETID    03  …" / "Hoptype 03 …" / "Center 03  …" / "Hopset 03  …"
            // …and "HOPLIST 03   …", which is a LIST net's DIS VALUE line, not
            // just the answer to a bare `HOPLIST n` (bench 2026-08-16). Omitting
            // it here is what made this helper time out rather than fail loudly.
            var t = e.Message.Trim();
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != number) return;
            if (parts[0].ToUpperInvariant() is not ("NETID" or "HOPTYPE" or "CENTER" or "HOPSET" or "HOPLIST"))
                return;
            lock (seen) seen.Add(t);
        }
        _radio.MessageReceived += Watch;
        send();
        WaitUntil(() => { lock (seen) return seen.Count >= expected; },
            $"{expected} DIS line(s) for net {net}");
        Thread.Sleep(50);   // let any EXTRA line arrive, so a surplus fails
        _radio.MessageReceived -= Watch;
        lock (seen) return [.. seen];
    }

    [Fact]
    public void DemoDisAllNets_ServesTheThreeProgrammedNets_AndTheWipedRest()
    {
        HopReady();
        _radio.Hop.QueryAllNets();                       // "DIS"
        WaitUntil(() => _radio.State.Hop.Nets.Count == 10, "all ten nets from the demo DIS");
        var nets = _radio.State.Hop.Nets;

        // Net 0 — the captured NB triplet.
        Assert.Equal("12345678", nets[0].NetId);
        Assert.Equal(HopType.Narrowband, nets[0].Type);
        Assert.Equal("11565", nets[0].CenterKHz);

        // Net 2 — WB, with the band edges the round-5 parser now mirrors.
        Assert.Equal("24680135", nets[2].NetId);
        Assert.Equal(HopType.Wideband, nets[2].Type);
        Assert.Equal("02000", nets[2].WidebandLowKHz);
        Assert.Equal("08000", nets[2].WidebandHighKHz);

        // Net 3 — LIST, and NO value of any kind (BC3: that shape is uncaptured).
        Assert.Equal("13579246", nets[3].NetId);
        Assert.Equal(HopType.List, nets[3].Type);
        Assert.Null(nets[3].CenterKHz);
        Assert.Null(nets[3].WidebandLowKHz);

        // The other seven — probe R9b's wiped record, reported unprogrammed.
        foreach (int n in new[] { 1, 4, 5, 6, 7, 8, 9 })
        {
            Assert.True(nets[n].IsReportedUnprogrammed, "net " + n + " should read unprogrammed");
            Assert.Null(nets[n].NetId);
            Assert.Equal(HopType.Wideband, nets[n].Type);
            Assert.Null(nets[n].WidebandLowKHz);
            Assert.Null(nets[n].WidebandHighKHz);
        }
    }

    [Fact]
    public void DemoDisOneNet_ServesOnlyThatNet()
    {
        HopReady();
        _radio.Hop.QueryNet(2);                          // "DIS 2"
        WaitUntil(() => _radio.State.Hop.Nets.ContainsKey(2), "net 2 from the demo DIS 2");
        WaitUntil(() => _radio.State.Hop.Nets[2].WidebandHighKHz is not null, "net 2 band edges");

        Assert.Equal("02000", _radio.State.Hop.Nets[2].WidebandLowKHz);
        Assert.Equal("08000", _radio.State.Hop.Nets[2].WidebandHighKHz);
        Assert.Single(_radio.State.Hop.Nets);            // DIS n is ONE net, not the dump
    }

    [Fact]
    public void DemoDisListNet_EmitsNoInventedValueLine()
    {
        // BENCH-CORRECTED 2026-08-16. BC3 had the demo emit NO value line for a
        // LIST net, because the shape was uncaptured and inventing one is what
        // the replay doctrine forbids. The bench captured it: a LIST net's DIS
        // record carries THE HOPLIST LINE ITSELF as its third line, and the
        // Hoptype echoes mixed-case "List" (docs/probes.md, S2). Emitting it is
        // now replay, and omitting it is now the fabrication — the demo would be
        // asserting an absence the radio does not have.
        HopReady();
        var lines = WatchNetLines(3, () => _radio.Hop.QueryNet(3), expected: 3);
        Assert.Equal(
            [
                "NETID    03  13579246",
                "Hoptype 03 List",
                "HOPLIST 03   10125  11010  12345  13570  15250  17635  19870  22105",
            ],
            lines);
    }

    [Fact]
    public void DemoDisWidebandNet_EmitsTheProvisionalHopsetLine()
    {
        // The counterpart pin: net 2 DOES carry a value line, in the
        // PROVISIONAL shape (docs/protocol.md). Pinning it at the wire is what
        // makes the LIST-net pin above meaningful rather than a demo that
        // simply never emits value lines.
        HopReady();
        var lines = WatchNetLines(2, () => _radio.Hop.QueryNet(2), expected: 3);
        Assert.Equal(
            ["NETID    02  24680135", "Hoptype 02 WB", "Hopset 02  02000  08000"], lines);
    }

    [Fact]
    public void DemoHopList_ServesTheSession16Shape_ForTheListNetOnly()
    {
        HopReady();
        _radio.Hop.QueryHopList(3);                      // "HOPLIST 3"
        WaitUntil(() => _radio.State.Hop.HopLists.ContainsKey(3), "the demo HOPLIST 3 answer");
        Assert.Equal(
            ["10125", "11010", "12345", "13570", "15250", "17635", "19870", "22105"],
            _radio.State.Hop.HopLists[3]);

        // A net with no canned list has no captured answer either → rule 6
        // (prompt only), never an invented empty or borrowed list. The ordered
        // follow-up read proves the first command was fully handled.
        _radio.Hop.QueryHopList(5);
        _radio.Hop.QueryAllNets();
        WaitUntil(() => _radio.State.Hop.Nets.Count == 10, "the DIS ordered after HOPLIST 5");
        Assert.False(_radio.State.Hop.HopLists.ContainsKey(5));
    }

    [Fact]
    public void DemoHopReads_AnswerAtTheHopPromptOnly()
    {
        // The HOP net reads are HOP-domain: at the SSB prompt the real radio
        // rejects them and that reject shape is uncaptured, so the demo stays
        // at rule 6 rather than serve the net table out of mode.
        ConnectReady();                                  // demo starts in SSB
        _radio.Hop.QueryAllNets();
        _radio.Ssb.SetSquelch(OnOff.On);                 // captured answer, ordered after
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.On,
            "AnalogSquelch ON (ordered after the ignored DIS)");
        Assert.Empty(_radio.State.Hop.Nets);
    }

    // ---- RAW TRANSCRIPTS (C1 audit round 1, MAJOR) ---------------------------
    // The pins above assert the MIRROR, so the parser's tolerance for spacing
    // hides drift in the demo's own bytes: "NETID 00 12345678" with the wrong
    // column widths would parse identically and ship undetected. The replay
    // doctrine is about the WIRE TEXT, so these read it, byte for byte, at the
    // seam — before any framer, parser or mirror can normalize it. Expected
    // strings are the plan §BC1/§BC2 pinned lines, spacing included.

    private const string Crlf = "\r\n";
    private const string HopPrompt = "HOP> ";

    /// <summary>Drive a bare <see cref="DemoSerialPort"/> and return the exact
    /// bytes it answers with, one string per command.</summary>
    private static List<string> RawDemoReplies(params string[] commands)
    {
        var port = new DemoSerialPort { ResponseDelayMs = 0, TuneTerminalDelayMs = 0 };
        var chunks = new System.Collections.Concurrent.BlockingCollection<string>();
        port.DataReceived += (_, e) => chunks.Add(System.Text.Encoding.ASCII.GetString(e.Data));

        try
        {
            port.OpenAsync(DemoSettings).GetAwaiter().GetResult();
            foreach (var command in commands)
                port.WriteAsync(System.Text.Encoding.ASCII.GetBytes(command)).GetAwaiter().GetResult();

            // ROUND 15 §3.5: a reply is however many CHUNKS the demo queues
            // for it, collected until one ENDS WITH A PROMPT — the demo's own
            // framing contract for "the answer is finished". One chunk per
            // command was true until the HOP tune leg arrived; every
            // single-chunk consumer is unaffected because its first chunk
            // already ends with the prompt.
            var replies = new List<string>();
            for (int i = 0; i < commands.Length; i++)
            {
                var reply = new System.Text.StringBuilder();
                do
                {
                    Assert.True(chunks.TryTake(out var chunk, 5_000),
                        $"timed out waiting for the demo's reply to '{commands[i]}'");
                    reply.Append(chunk);
                }
                while (!EndsWithPrompt(reply.ToString()));
                replies.Add(reply.ToString());
            }
            return replies;
        }
        finally
        {
            port.CloseAsync().GetAwaiter().GetResult();
        }
    }

    private static bool EndsWithPrompt(string reply)
        => reply.EndsWith(SsbPrompt, StringComparison.Ordinal)
            || reply.EndsWith(AlePrompt, StringComparison.Ordinal)
            || reply.EndsWith(HopPrompt, StringComparison.Ordinal);

    /// <summary>The reply to the LAST command, after switching the demo to HOP.</summary>
    private static string RawHopReply(string command) => RawDemoReplies("HO", command)[1];

    private const string AlePrompt = "ALE> ";

    /// <summary>The replies to <paramref name="commands"/>, after switching
    /// the demo to ALE (the mode switch's own reply is dropped).</summary>
    private static List<string> RawAleReplies(params string[] commands)
        => [.. RawDemoReplies([.. new[] { "ALE" }.Concat(commands)]).Skip(1)];

    private static string RawAleReply(string command) => RawAleReplies(command)[0];

    [Fact]
    public void RawTranscript_DisOneNet_Wideband_IsTheProvisionalShapeExactly()
    {
        // R1 framing: <CRLF> → payload lines → prompt. The Hopset line's two
        // spaces before each value are the captured placeholder's columns
        // (probe R9b), which is the whole basis of the PROVISIONAL shape.
        Assert.Equal(
            Crlf + "NETID    02  24680135" + Crlf
                 + "Hoptype 02 WB" + Crlf
                 + "Hopset 02  02000  08000" + Crlf + HopPrompt,
            RawHopReply("DIS 2"));
    }

    [Fact]
    public void RawTranscript_DisListNet_HasExactlyTwoLines_AndNoValueLine()
    {
        // BENCH-CORRECTED 2026-08-16 (see the mirror-level pin above). At the
        // byte level the LIST net's DIS record is three lines, the third being
        // the HOPLIST line — identical to what a bare `HOPLIST 3` answers, which
        // is exactly why the existing HOPLIST handler parses it with no new
        // shape. The mixed-case "List" is the radio's own spelling.
        Assert.Equal(
            Crlf + "NETID    03  13579246" + Crlf
                 + "Hoptype 03 List" + Crlf
                 + "HOPLIST 03   10125  11010  12345  13570  15250  17635  19870  22105"
                 + Crlf + HopPrompt,
            RawHopReply("DIS 3"));
    }

    [Fact]
    public void RawTranscript_HopList_IsTheSession16Columns()
    {
        // Session-16 spacing: THREE spaces after the net number, two between
        // frequencies. A single-space drift parses the same and would other-
        // wise ship silently.
        Assert.Equal(
            Crlf + "HOPLIST 03   10125  11010  12345  13570  15250  17635  19870  22105"
                 + Crlf + HopPrompt,
            RawHopReply("HOPLIST 3"));
    }

    [Fact]
    public void RawTranscript_DisAllNets_OpensWithTheCapturedTripletAndTheWipedForm()
    {
        var reply = RawHopReply("DIS");

        // Net 0 — the captured SH-block triplet, verbatim; net 1 — probe R9b's
        // wiped record. Asserted as a PREFIX so the pin names the two shapes
        // the plan pinned without restating all ten nets.
        Assert.StartsWith(
            Crlf + "NETID    00  12345678" + Crlf
                 + "Hoptype 00 NB" + Crlf
                 + "Center 00  11565" + Crlf
                 + "NETID    01  XXXXXXXX" + Crlf
                 + "Hoptype 01 WB" + Crlf
                 + "Hopset 01  XXXXXX  XXXXXX" + Crlf,
            reply, StringComparison.Ordinal);

        Assert.EndsWith(Crlf + HopPrompt, reply, StringComparison.Ordinal);

        // Ten nets, THREE lines each — 30. Was 29 while the LIST net emitted no
        // value line; the bench capture (2026-08-16) gave it one, so every net
        // now carries a value line whatever its type. That uniformity is the
        // point of counting here rather than the number itself.
        Assert.Equal(30, reply[Crlf.Length..^HopPrompt.Length].Split(Crlf, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // ---- Round 11 §8: the new answers, byte for byte ----------------------

    [Fact]
    public void RawTranscript_TargetedNetRead_IsTheCapturedRecordPlusMemberLines()
    {
        // Captured 2026-08-17 (bench/transcripts/phase1-ale-membership): the
        // record line, then FIVE-space-indented "MEMBER nn  <addr>"
        // continuations numbered from 01 in insertion order.
        Assert.Equal(
            Crlf + "NETAD NT1               CHGROUP 01   ASSOC SELF TST" + Crlf
                 + "     MEMBER 01  AAA" + Crlf
                 + "     MEMBER 02  TST" + Crlf + AlePrompt,
            RawAleReply("NETAD NT1"));
    }

    [Fact]
    public void RawTranscript_TargetedNetRead_MemberlessNet_IsThePositiveMarker()
    {
        // " NO MEMBERS PRGMD " — leading and trailing space, exactly as the
        // radio pads it. NOT silence: the marker is the radio SAYING none.
        Assert.Equal(
            Crlf + "NETAD ALLCALL           CHGROUP 03   ASSOC SELF CAM" + Crlf
                 + " NO MEMBERS PRGMD " + Crlf + AlePrompt,
            RawAleReply("NETAD ALLCALL"));
    }

    [Fact]
    public void RawTranscript_ScheduleListing_IsTheCapturedRowShape_AndTheMarkerAfterErase()
    {
        // "EXCHANGE I1              INTERVAL 01:00 START TIME 22:34" — the
        // captured columns (kind in an 8-wide field, address in a 15-wide one).
        var replies = RawAleReplies("EXCH", "ERASE", "EXCH");

        Assert.Equal(
            Crlf + "SOUND    CAM             INTERVAL 03:00 START TIME 13:02" + Crlf
                 + "EXCHANGE BOB             INTERVAL 01:00 START TIME 22:34" + Crlf + AlePrompt,
            replies[0]);
        Assert.Equal(Crlf + AlePrompt, replies[1]);             // ERASE is silent
        Assert.Equal(Crlf + " NO LQA SCHEDULED " + Crlf + AlePrompt, replies[2]);
    }

    [Fact]
    public void RawTranscript_ExcludeListing_IsTheCapturedRowShape()
    {
        // "Exclude 00  02000   03000 " — session-16 / 2026-08-17 columns,
        // trailing space and all. TWO rows is the PROVISIONAL multi-band form.
        Assert.Equal(
            Crlf + "Exclude 00  02000   03000 " + Crlf
                 + "Exclude 01  11000   11500 " + Crlf + HopPrompt,
            RawHopReply("EXC"));
    }

    [Fact]
    public void RawTranscript_InternalCouplerQuery_IsTheCapturedMixedCaseLine_AtEveryPrompt()
    {
        // "INTCoupler Enabled" — the radio's own MIXED CASE (docs/protocol.md,
        // captured at SSB> 2026-08-16, re-captured at HOP> by P-1 run A and at
        // ALE> by run C). PROMPT-FREE means ALL THREE, so all three are
        // asserted: the round-14 coupler row and its landing read fire at
        // HOP>, and the round-14 C policy's seeding read fires at whatever
        // prompt the session happens to be at — an SSB-gated demo would answer
        // nothing in either place.
        //
        // ALE> is not decoration here. It is the only one of the three that no
        // other pin covers, and a demo that quietly re-gated the coupler to
        // "SSB or HOP" would look exactly like a correct one from the row's
        // point of view (audit round 1, MAJOR 1: that mutation survived while
        // this test named only two prompts).
        Assert.Equal(
            Crlf + "INTCoupler Enabled" + Crlf + SsbPrompt,
            RawDemoReplies("INTCOUPLER")[0]);
        Assert.Equal(
            Crlf + "INTCoupler Enabled" + Crlf + HopPrompt,
            RawHopReply("INTCOUPLER"));
        Assert.Equal(
            Crlf + "INTCoupler Enabled" + Crlf + AlePrompt,
            RawAleReply("INTCOUPLER"));
    }

    [Fact]
    public void RawTranscript_InternalCouplerSet_EchoesTheNewState_AndTheStatePERSISTS()
    {
        // P-1 captured BOTH set echoes: `INTCOUPLER BYPASS` -> "INTCoupler
        // Bypassed", `INTCOUPLER ENABLE` -> "INTCoupler Enabled" — the SET
        // answers in the QUERY's own shape, carrying the NEW state.
        //
        // The interleaved queries are the load-bearing half: without persisted
        // state the demo would echo the set and then answer the next query with
        // the baseline, and the row would light one button and then jump back —
        // the exact behaviour the persistence exists to prevent.
        var replies = RawDemoReplies(
            "HO", "INTCOUPLER BYPASS", "INTCOUPLER", "INTCOUPLER ENABLE", "INTCOUPLER");

        Assert.Equal(Crlf + "INTCoupler Bypassed" + Crlf + HopPrompt, replies[1]);
        Assert.Equal(Crlf + "INTCoupler Bypassed" + Crlf + HopPrompt, replies[2]);
        Assert.Equal(Crlf + "INTCoupler Enabled" + Crlf + HopPrompt, replies[3]);
        Assert.Equal(Crlf + "INTCoupler Enabled" + Crlf + HopPrompt, replies[4]);

        // …and the SET is prompt-free too, not just the query (audit round 1,
        // MAJOR 1). P-1 run C set the coupler at ALE> and got the identical
        // echo, so the demo owes the same answer there — with the same
        // persistence, proved by the query that follows it.
        var ale = RawAleReplies("INTCOUPLER BYPASS", "INTCOUPLER");
        Assert.Equal(Crlf + "INTCoupler Bypassed" + Crlf + AlePrompt, ale[0]);
        Assert.Equal(Crlf + "INTCoupler Bypassed" + Crlf + AlePrompt, ale[1]);
    }

    [Fact]
    public void RawTranscript_ModemPresetReads_BulkOmitsTheDisabledOne_TargetedServesIt()
    {
        // The bulk listing lists ONLY ENABLED presets — the demo's preset 2 is
        // the canned DISabled row, so it is absent from the bulk and present
        // to the targeted read. That asymmetry IS the enabled/disabled signal.
        var replies = RawDemoReplies("MODEM PRE", "MODEM PRE 2");

        Assert.Equal(
            Crlf + "MODEM PRESET 0 SER  ASYNC DATA   BAUD 4800  TYPE serial  INTER uncoded " + Crlf
                 + "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    " + Crlf
                 + "MODEM PRESET 3 FW   ASYNC DATA   BAUD 300   TYPE fskws   " + Crlf
                 + "MODEM PRESET 4 FN   ASYNC DATA   BAUD 75    TYPE fskns   " + Crlf
                 + "MODEM PRESET 5 FV   ASYNC DATA   BAUD 600   TYPE fsk-v   MARK 1500 SPACE 1700" + Crlf
                 + "MODEM PRESET 6 T39B SYNC  DATA   BAUD 1200  TYPE 39tone  INTER short   " + Crlf
                 + SsbPrompt,
            replies[0]);
        Assert.Equal(
            Crlf + "MODEM PRESET 2 DAT2 ASYNC REMOTE BAUD 2400  TYPE 39tone  INTER long    " + Crlf
                 + SsbPrompt,
            replies[1]);
    }

    private const string SsbPrompt = "SSB> ";

    [Fact]
    public void RawTranscript_HopReadsOutsideHop_AreThePromptAlone()
    {
        // The rule-6 fallback, at the wire: no net table leaks at the SSB
        // prompt, and no reject shape is invented either.
        Assert.Equal(Crlf + "SSB> ", RawDemoReplies("DIS")[0]);
    }

    // ---- ROUND 15 §3.5: `HO` / `NET n` — the entry lifecycle and the TUNE LEG
    //
    // One pin per row of the plan's reply table, at the WIRE. The round-6
    // PROVISIONAL select shape (`NET 0n` / `Generating Hopset...` /
    // `Hopnum 0041`) is RETIRED by P6b: no select window carries a Hopnum, and
    // the coupler really does tune on the way into an untuned net (critic F3).
    // Line text — the two leading spaces of ` TUNING COUPLER `, the two
    // trailing of ` TUNE COMPLETE  `, the column shape of `NET  01` — is the
    // capture's, verbatim.

    /// <summary>The wire carries the entry answer's prompt and `Wait...` with
    /// NO break between them (P6b/P4 — the probe logs `HOP&gt; Wait...`), which
    /// is what confirms the mode and releases the write gate BEFORE the
    /// generation lines. The app's framer splits it back into `HOP&gt;` +
    /// `Wait...`; at the byte seam it is one string.</summary>
    private const string WaitLine = "HOP> Wait...";
    private const string GeneratingLine = "Generating Hopset...";

    [Fact]
    public void RawTranscript_HopEntry_ProgrammedCurrentNet_GeneratesOnceWithNoTuneLeg()
    {
        // P4 `A-ho-from-ssb`: every HOP entry regenerates. ONE chunk, ONE
        // cycle, no tune lines — the leg rides on `NET` only (D6).
        Assert.Equal(
            Crlf + WaitLine + Crlf + GeneratingLine + Crlf + HopPrompt,
            RawDemoReplies("HO")[0]);
    }

    [Fact]
    public void RawTranscript_HopEntry_WipedCurrentNet_IsThePromptAlone()
    {
        // A wiped net has nothing to generate from, so the entry imposes
        // nothing and says nothing (the same fact NoteHopEntry models).
        var replies = RawDemoReplies("HO", "HOPSET 0 DEL", "SS", "HO");
        Assert.Equal(Crlf + HopPrompt, replies[3]);
    }

    [Fact]
    public void RawTranscript_NetSelect_TheCurrentNet_EchoesAndStops()
    {
        // P6b `T3-net-same-0`: re-selecting the net you are on is an echo —
        // no generation, no tune.
        Assert.Equal(Crlf + "NET  00" + Crlf + HopPrompt, RawHopReply("NET 0"));
    }

    [Fact]
    public void RawTranscript_NetSelect_UntunedNet_CarriesTheTuneLeg()
    {
        // P6b `T1-net-1`, verbatim: the echo, the generation, ` TUNING
        // COUPLER `, and — in a SECOND chunk, after the tune delay — the
        // terminal with the closing prompt.
        Assert.Equal(
            Crlf + "NET  02" + Crlf + WaitLine + Crlf + GeneratingLine + Crlf
                 + " TUNING COUPLER " + Crlf
                 + Crlf + " TUNE COMPLETE  " + Crlf + HopPrompt,
            RawHopReply("NET 2"));
    }

    [Fact]
    public void RawTranscript_NetSelect_AnAlreadyTunedNet_GeneratesWithNoTuneLines()
    {
        // P6b `T2-net-back-0`: the coupler remembers. Net 2 is tuned by its
        // first select (COMPLETE), net 3 rotates the terminal to MARGINAL and
        // is tuned too, and the RETURN to net 2 plays the generation alone.
        // (Nets 0, 2 and 3 are the demo's programmed ones.)
        var replies = RawDemoReplies("HO", "NET 2", "NET 3", "NET 2");

        Assert.Contains(" TUNING COUPLER ", replies[1], StringComparison.Ordinal);   // anti-vacuity
        Assert.Contains("TUNE MARGINAL", replies[2], StringComparison.Ordinal);      // shared rotation
        Assert.Equal(
            Crlf + "NET  02" + Crlf + WaitLine + Crlf + GeneratingLine + Crlf + HopPrompt,
            replies[3]);
    }

    [Fact]
    public void RawTranscript_NetSelect_AFaultedTuneLeavesTheNetUntuned()
    {
        // DEMO-MODELLED (D3, probe P9 pending): the field rig faulted on every
        // entry, so a FAULT must not enter the memory — the next select
        // retries. Nets 2 and 3 take COMPLETE and MARGINAL; net 0 takes the
        // FAULT, and its re-select tunes again (COMPLETE, the rotation wraps).
        var replies = RawDemoReplies("HO", "NET 2", "NET 3", "NET 0", "NET 3", "NET 0");

        Assert.Contains(FaultTerminal, replies[3], StringComparison.Ordinal);
        Assert.DoesNotContain(" TUNING COUPLER ", replies[4], StringComparison.Ordinal);  // net 3 remembered
        Assert.Contains(" TUNING COUPLER ", replies[5], StringComparison.Ordinal);        // net 0 was not
        Assert.Contains(" TUNE COMPLETE  ", replies[5], StringComparison.Ordinal);
    }

    private const string FaultTerminal = "TUNE FAULT";

    [Fact]
    public void RawTranscript_NetSelect_WithTheCouplerBypassed_GeneratesWithNoTuneLines()
    {
        // DEMO-MODELLED (critic F22): the echo and the generation framing are
        // P6b's, "bypassed → generate only" is protocol.md's coupler rule, and
        // the closing prompt is the demo's Frame contract. No single capture of
        // a bypassed `NET` exists.
        var replies = RawDemoReplies("INTCOUPLER BYPASS", "HO", "NET 2");
        Assert.Equal(
            Crlf + "NET  02" + Crlf + WaitLine + Crlf + GeneratingLine + Crlf + HopPrompt,
            replies[2]);
    }

    [Fact]
    public void RawTranscript_NetSelect_UnprogrammedNet_AnswersNoHopset()
    {
        Assert.Equal(Crlf + "No Hopset" + Crlf + HopPrompt, RawHopReply("NET 7"));
    }

    [Fact]
    public void RawTranscript_NetSelect_OutsideHop_IsThePromptAlone()
    {
        Assert.Equal(Crlf + "SSB> ", RawDemoReplies("NET 2")[0]);
    }

    [Fact]
    public void DemoNetSelect_PlaysTheTuningLineBeforeTheTerminal()
    {
        // The CHUNK ORDER, through the real stack (the RETU idiom): the raw
        // pin above sees one concatenated string and cannot tell that the
        // terminal arrives LATER — which is the whole reason the spine chip
        // has a Tuning state to show.
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Hop);
        _demo.TuneTerminalDelayMs = 150;

        var seen = new List<string>();
        void Watch(object? _, MessageReceivedEventArgs e)
        {
            var t = e.Message.Trim();
            if (t.StartsWith("TUNING", StringComparison.Ordinal)
                || t.StartsWith("TUNE ", StringComparison.Ordinal))
                lock (seen) seen.Add(t);
        }
        _radio.MessageReceived += Watch;
        _radio.Hop.SelectNet(2);
        WaitUntil(() => { lock (seen) return seen.Count >= 1; }, "the demo's TUNING COUPLER line");
        lock (seen) Assert.Equal(["TUNING COUPLER"], seen);      // …and NOT the terminal yet
        WaitUntil(() => { lock (seen) return seen.Count >= 2; }, "the delayed tune terminal");
        _radio.MessageReceived -= Watch;

        lock (seen) Assert.Equal(["TUNING COUPLER", "TUNE COMPLETE"], seen);
    }

    // ---- Round 6 (CJ): the two canned DI channels -----------------------------

    [Fact]
    public void RawTranscript_DiChannelOne_IsTheCapturedDumpShape_Simplex()
    {
        // The session-23 dump line shape with canned values; AGC SL is the
        // only captured dump abbreviation, so both canned channels carry it.
        Assert.Equal(
            Crlf + "CH 01 RxFr 14313500 TxFr 14313500 MODE USB AGC SL BA 2.7  RXONLY NO"
                 + Crlf + "SSB> ",
            RawDemoReplies("DI 1 1")[0]);
    }

    [Fact]
    public void RawTranscript_DiChannelTwo_IsSplitLsbReceiveOnly()
    {
        Assert.Equal(
            Crlf + "CH 02 RxFr 07102000 TxFr 07215000 MODE LSB AGC SL BA 2.7  RXONLY YES"
                 + Crlf + "SSB> ",
            RawDemoReplies("DI 2 2")[0]);
    }

    /// <summary>
    /// The DUMP serves EVERY SLOT — re-based clone round 12 P2 to the real
    /// radio's 100-slot inventory. An unprogrammed slot is not omitted: it
    /// answers the DEFAULT ROW (`01600000 USB SL 2.7 RXONLY NO`), which is what
    /// the 2026-08-18 zeroize capture read back from `DI 50 50` on a freshly
    /// wiped radio. The round-11 demo omitted them, and that omission is what
    /// made a "target-only" channel unremovable by a clone.
    /// </summary>
    [Fact]
    public void RawTranscript_DiRange_ServesEverySlot_ProgrammedOrAtItsDefaultRow()
    {
        var dump = RawDemoReplies("DI 0 99")[0];
        Assert.Contains(
            "CH 01 RxFr 14313500 TxFr 14313500 MODE USB AGC SL BA 2.7  RXONLY NO",
            dump, StringComparison.Ordinal);
        Assert.Contains(
            "CH 02 RxFr 07102000 TxFr 07215000 MODE LSB AGC SL BA 2.7  RXONLY YES",
            dump, StringComparison.Ordinal);
        Assert.Equal(100, dump.Split("CH ", StringSplitOptions.None).Length - 1);

        // A slot nobody programmed answers the DEFAULT ROW rather than nothing.
        Assert.Equal(
            Crlf + "CH 05 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO"
                 + Crlf + "SSB> ",
            RawDemoReplies("DI 5 5")[0]);
    }

    // ---- Wave 2 (plan-gui-rejigger.md): the SSB operational-settings answers.
    // Each pins that the demo's rule-5 read-back drives the CORE mirror over the
    // real parser (the connect SH parked every value, so a confirmed CHANGE
    // proves the setting's own answer moved it, not the SH block).

    [Fact]
    public void DemoSquelchAnswer_ConfirmsAnalogSquelch()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.Off,
            "AnalogSquelch OFF from the canned SSB SH");
        _radio.Ssb.SetSquelch(OnOff.On);   // "SQ ON" → demo "SQUELCH ON"
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.On,
            "AnalogSquelch ON from the demo SQUELCH answer");
    }

    /// <summary>
    /// The DV response group's <c>DGT_SQUELCH</c> line is a RIDER — a REPORT,
    /// not a mutation.
    ///
    /// <para>CORRECTED by the P6 audit (round 1, MAJOR). The demo used to force
    /// the digital squelch OFF on every DV set, and this test pinned that as
    /// "the constant rider". protocol.md (digital squelch, bench-confirmed
    /// 2026-08-02) says the opposite in as many words: <c>DGT_S</c> is not
    /// gated on digital voice, it SURVIVES <c>DV ON</c>/<c>DV OFF</c> toggling
    /// and KEEPS ITS VALUE, and the line is only "reported inside the DV
    /// response group, which is presumably why the legacy GUI treated it as a
    /// digital-voice sub-setting. It is not one." The demo had
    /// re-implemented the legacy GUI's own mistake, and this pin had locked it
    /// in.</para>
    /// </summary>
    [Fact]
    public void DemoDigitalVoiceAnswer_ConfirmsDigitalVoice_AndTheDgtSquelchRiderOnlyREPORTS()
    {
        ConnectReady();
        // Pre-set digital squelch ON so the rider's value is OBSERVABLE (the SH
        // parked it OFF, which would make an "unchanged" assertion vacuous).
        _radio.Ssb.SetDigitalSquelch(OnOff.On);   // "DGT_S ON" → demo "DGT_SQUELCH ON"
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.On,
            "DigitalSquelch ON before DV (the rider's precondition)");

        _radio.Ssb.SetDigitalVoice(OnOff.On);     // "DV ON" → demo "DV ON" + "DGT_SQUELCH ON"
        WaitUntil(() => _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == OnOff.On,
            "DigitalVoice ON from the demo DV answer");

        // The digital squelch SURVIVES the toggle, in both directions.
        Assert.Equal(OnOff.On, _radio.State.DigitalSquelch.Value);
        _radio.Ssb.SetDigitalVoice(OnOff.Off);
        WaitUntil(() => _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == OnOff.Off,
            "DigitalVoice OFF from the demo DV answer");
        Assert.Equal(OnOff.On, _radio.State.DigitalSquelch.Value);
    }

    /// <summary>
    /// `RWAS` is ASYMMETRIC — RE-BASED, clone round 12 §4. BOTH directions
    /// REPORT the squelch lines alongside the RWAS line (so no re-poll is ever
    /// needed either way), but only **ENABLE FORCES** the three squelches ON.
    ///
    /// <para>THIS TEST PINNED THE DISPROVED FORM. The P6 audit had made the
    /// demo cascade in both directions on the strength of a protocol.md line
    /// that said "enabling or disabling forces … ON"; the 2026-08-18 §14 bench
    /// session sent `RWAS DIS` with analog and digital squelch OFF and got
    /// `SQUELCH OFF` / `FMSQUELCH ON` / `DGT_SQUELCH OFF` back, unchanged, and
    /// re-queried them one by one to be sure. Both halves are pinned below, so
    /// neither direction can drift back into the other.</para>
    /// </summary>
    [Fact]
    public void DemoRwasEnable_ForcesAllThreeSquelchesOn()
    {
        ConnectReady();
        _radio.Ssb.SetDigitalSquelch(OnOff.Off);
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.Off,
            "DigitalSquelch OFF before RWAS");

        _radio.Ssb.SetRwas(EnabledDisabled.Enabled);

        WaitUntil(() => _radio.State.Rwas.IsConfirmed && _radio.State.Rwas.Value == EnabledDisabled.Enabled,
            "RWAS Enabled");
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.On,
            "analog squelch forced ON by RWAS ENA");
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.On,
            "digital squelch forced ON by RWAS ENA");
        WaitUntil(() => _radio.State.FmSquelch.IsConfirmed && _radio.State.FmSquelch.Value == OnOff.On,
            "FM squelch forced ON by RWAS ENA");
    }

    [Fact]
    public void DemoRwasDisable_ReportsTheSquelches_ButForcesNothing()
    {
        // The captured asymmetry, and the reason it matters to the clone: if
        // DISABLE forced too, a campaign could write RWAS in any order and the
        // squelch rows would still land — the manifest's ORDER column would be
        // untestable. It does not force, so the order is real.
        ConnectReady();
        _radio.Ssb.SetDigitalSquelch(OnOff.Off);
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.Off,
            "DigitalSquelch OFF before RWAS");
        _radio.Ssb.SetSquelch(OnOff.Off);
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.Off,
            "analog squelch OFF before RWAS");

        _radio.Ssb.SetRwas(EnabledDisabled.Disabled);

        WaitUntil(() => _radio.State.Rwas.IsConfirmed && _radio.State.Rwas.Value == EnabledDisabled.Disabled,
            "RWAS Disabled");
        // REPORTED — the answer carries all four lines, which is why no re-poll
        // is needed in either direction…
        WaitUntil(() => _radio.State.FmSquelch.IsConfirmed, "FM squelch REPORTED by RWAS DIS");
        // …and NOT FORCED.
        Assert.Equal(OnOff.Off, _radio.State.AnalogSquelch.Value);
        Assert.Equal(OnOff.Off, _radio.State.DigitalSquelch.Value);
    }

    [Fact]
    public void DemoDigitalSquelchAnswer_ConfirmsDigitalSquelch()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.Off,
            "DigitalSquelch OFF from the canned SSB SH");
        _radio.Ssb.SetDigitalSquelch(OnOff.On);   // "DGT_S ON" → demo "DGT_SQUELCH ON"
        WaitUntil(() => _radio.State.DigitalSquelch.IsConfirmed && _radio.State.DigitalSquelch.Value == OnOff.On,
            "DigitalSquelch ON from the demo DGT_SQUELCH answer (independent peer)");
    }

    [Fact]
    public void DemoModemSelectAndOff_ConfirmActiveModem()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.ActiveModem.IsConfirmed && _radio.State.ActiveModem.Value == "OFF",
            "ActiveModem OFF from the canned SSB SH");
        _radio.Ssb.SelectModem("1");   // "MODEM 1" → demo "MODEM 1 T39" (captured shape)
        WaitUntil(() => _radio.State.ActiveModem.IsConfirmed && _radio.State.ActiveModem.Value == "1 T39",
            "ActiveModem '1 T39' from the demo MODEM select answer");
        _radio.Ssb.ModemOff();         // "MODEM OF" → demo "MODEM OFF"
        WaitUntil(() => _radio.State.ActiveModem.IsConfirmed && _radio.State.ActiveModem.Value == "OFF",
            "ActiveModem OFF from the demo MODEM OFF answer");
    }

    [Fact]
    public void DemoModemPresets_CapturedWriteEcho_ThenList_EachFeedTheMirror()
    {
        // Round 8 (EE): the app's T39 programming write echoes the captured
        // listing-form answer, and MODEM PRE answers the ONE captured listing
        // line (any other preset line stays honestly unanswered — rule 6).
        // Round 9: the write is now the SHORT-token line, so the demo's
        // mapping is PROVISIONAL — no longer byte-identical to session-15.
        // R8-review MAJOR 5: the WRITE goes FIRST, against an empty mirror,
        // so its echo is what populates it — a wait that follows the query
        // could pass on the query's own row and prove nothing about the echo.
        ConnectReady();
        Assert.Empty(_radio.State.ModemPresets);

        _radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400");
        WaitUntil(() => _radio.State.ModemPresets.Count == 1,
            "the captured write's listing-form echo populating the empty mirror");
        Assert.Equal("1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long",
            _radio.State.ModemPresets[0]);

        // Round 11 §8: the bulk read is the PRESENCE operation now, and it
        // touches NOTHING but the enabled set — the fields mirror keeps
        // exactly the echo's row, neither cleared nor added to.
        _radio.Ssb.QueryModemPresetPresence();
        WaitUntil(() => _radio.State.ModemPresetPresence.State
                == Falcon.Core.Radio.RadioState.PresenceState.Completed,
            "the presence operation's sentinel committing the enabled set");

        // Six of the demo's seven presets are enabled; preset 2 is the canned
        // DISABLED one and is absent from the bulk listing.
        Assert.Equal([0, 1, 3, 4, 5, 6], _radio.State.ModemPresetPresence.Enabled);
        // The mirror stores the line TRIMMED (the framer/parser trim), so the
        // captured trailing padding lives in the raw-byte pins, not here.
        Assert.Equal("1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long",
            Assert.Single(_radio.State.ModemPresets));
    }

    [Fact]
    public void DemoTargetedPresetRead_SeesTheDisabledPreset_TheBulkListingHides()
    {
        // Round 11 §8, the whole reason the targeted read exists: a DISABLED
        // preset is INVISIBLE to the bulk listing, and `MODEM PRE n` is the
        // only way to see its fields at all.
        ConnectReady();

        _radio.Ssb.QueryModemPreset(2);
        WaitUntil(() => _radio.State.ModemPresets.Count == 1,
            "the targeted read's answer for the DISABLED preset 2");
        Assert.Equal("2 DAT2 ASYNC REMOTE BAUD 2400  TYPE 39tone  INTER long",
            _radio.State.ModemPresets[0]);

        // …and it never joins the enabled set: the presence store is still
        // untouched, because a targeted row can never enter it.
        Assert.Equal(Falcon.Core.Radio.RadioState.PresenceState.Unknown,
            _radio.State.ModemPresetPresence.State);
    }

    [Fact]
    public void DemoTargetedNetRead_ServesMembersInInsertionOrder_AndTheEmptyMarker()
    {
        // Round 11 §8: the demo answers the TARGETED net read with the
        // captured MEMBER continuation shape, and the positive
        // ` NO MEMBERS PRGMD ` marker for a memberless net.
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ale);

        _radio.Ale.ReadNetMembers("NT1");
        WaitUntil(() => _radio.State.Ale.NetMembers.ContainsKey("NT1"),
            "the NT1 membership commit");
        Assert.Equal(["AAA", "TST"], _radio.State.Ale.NetMembers["NT1"].Select(m => m.Address));
        Assert.Equal([1, 2], _radio.State.Ale.NetMembers["NT1"].Select(m => m.Number));

        _radio.Ale.ReadNetMembers("ALLCALL");
        WaitUntil(() => _radio.State.Ale.NetMembers.ContainsKey("ALLCALL"),
            "the ALLCALL membership commit");
        Assert.Empty(_radio.State.Ale.NetMembers["ALLCALL"]);       // read-EMPTY, not unread
    }

    [Fact]
    public void DemoScheduleRead_ServesTwoRows_AndTheMarkerAfterErase()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ale);

        _radio.Ale.ReadLqaSchedules();
        WaitUntil(() => _radio.State.Ale.LqaSchedules is not null, "the schedule commit");
        Assert.Equal(
            [(LqaScheduleKind.Sound, "CAM"), (LqaScheduleKind.Exchange, "BOB")],
            _radio.State.Ale.LqaSchedules!.Select(s => (s.Kind, s.Address)));

        // ERASE clears the schedule queue (protocol.md hazard table), so the
        // next read is the positive EMPTY marker, not silence.
        _radio.Ale.EraseAllAddresses();
        _radio.Ale.ReadLqaSchedules();
        WaitUntil(() => _radio.State.Ale.LqaSchedules is { Count: 0 },
            "the NO LQA SCHEDULED marker committing read-empty");
    }

    [Fact]
    public void DemoExcludeRead_ServesTheCannedBands_InTheCapturedRowShape()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Hop);

        _radio.Hop.QueryExcludeBands();
        WaitUntil(() => _radio.State.Hop.ExcludeBands is not null, "the EXCLUDE commit");
        Assert.Equal(
            [(0, "02000", "03000"), (1, "11000", "11500")],
            _radio.State.Hop.ExcludeBands!.Select(b => (b.Band, b.LowKHz, b.HighKHz)));
    }

    [Fact]
    public void DemoUncapturedSquelchLevel_FabricatesNothing()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.SquelchLevel.IsConfirmed && _radio.State.SquelchLevel.Value == "HIGH",
            "SQ_LEVEL HIGH from the canned SSB SH");
        // "SQ_L LO" has no CAPTURED answer spelling → rule 6 (prompt only), no
        // invented SQ_LEVEL line. A subsequent CAPTURED answer confirms (proving
        // the LO command was fully handled), and the level is still HIGH.
        _radio.Ssb.SetSquelchLevel(SquelchLevel.Low);
        _radio.Ssb.SetSquelch(OnOff.On);
        WaitUntil(() => _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.On,
            "AnalogSquelch ON (ordered after the ignored SQ_L LO)");
        Assert.Equal("HIGH", _radio.State.SquelchLevel.Value);   // LO fabricated no read-back
    }

    [Fact]
    public void DemoFmSquelchAnswer_ConfirmsFmSquelch()
    {
        ConnectReady();
        // The canned SSB SH carries no FMSQUELCH line, so the mirror is
        // UNCONFIRMED until the demo's FMSQ answer moves it (unconfirmed → ON).
        Assert.False(_radio.State.FmSquelch.IsConfirmed);
        _radio.Ssb.SetFmSquelch(OnOff.On);   // "FMSQ ON" → demo "FMSQUELCH ON"
        WaitUntil(() => _radio.State.FmSquelch.IsConfirmed && _radio.State.FmSquelch.Value == OnOff.On,
            "FmSquelch ON from the demo FMSQUELCH answer");
    }

    [Fact]
    public void DemoCompressionAnswer_ConfirmsCompression()
    {
        ConnectReady();
        // No COMPRESS line in the SH — unconfirmed until the demo COM answer.
        Assert.False(_radio.State.Compression.IsConfirmed);
        _radio.Ssb.SetCompression(OnOff.On);   // "COM ON" → demo "COMPRESS ON"
        WaitUntil(() => _radio.State.Compression.IsConfirmed && _radio.State.Compression.Value == OnOff.On,
            "Compression ON from the demo COMPRESS answer");
    }

    [Fact]
    public void DemoBfoAnswer_ConfirmsBfoOffset()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.BfoOffset.IsConfirmed && _radio.State.BfoOffset.Value == "+0000",
            "BFO +0000 from the canned SSB SH");
        _radio.Ssb.SetBfoOffset(500);   // "BF +0500" → demo "BFO +0500"
        WaitUntil(() => _radio.State.BfoOffset.IsConfirmed && _radio.State.BfoOffset.Value == "+0500",
            "BfoOffset +0500 from the demo BFO answer");
    }

    [Fact]
    public void DemoSquelchLevelHighAnswer_EmitsCapturedReadBack()
    {
        ConnectReady();
        WaitUntil(() => _radio.State.SquelchLevel.IsConfirmed && _radio.State.SquelchLevel.Value == "HIGH",
            "SQ_LEVEL HIGH from the canned SSB SH");
        // "SQ_LEVEL HIGH" is the ONLY captured spelling and the SH already
        // confirmed HIGH, so a mirror CHANGE can't distinguish the SQ_L answer
        // from the SH — pin the demo's rule-5 read-back at the wire instead
        // (deleting the answer drops the line and fails this WaitUntil).
        string? sqLevelLine = null;
        void Watch(object? _, MessageReceivedEventArgs e)
        {
            var t = e.Message.Trim();
            if (t.StartsWith("SQ_LEVEL ", StringComparison.OrdinalIgnoreCase)) sqLevelLine = t;
        }
        _radio.MessageReceived += Watch;
        _radio.Ssb.SetSquelchLevel(SquelchLevel.High);   // "SQ_L HIGH" → demo "SQ_LEVEL HIGH"
        WaitUntil(() => sqLevelLine is not null,
            "SQ_LEVEL read-back line from the demo SQ_L answer");
        _radio.MessageReceived -= Watch;
        Assert.Equal("SQ_LEVEL HIGH", sqLevelLine);
        Assert.True(_radio.State.SquelchLevel.IsConfirmed && _radio.State.SquelchLevel.Value == "HIGH");
    }

    // ---- Coupler-tune lifecycle (plan-ui-tweaks.md §L) ------------------------

    [Fact]
    public void DemoRetune_PlaysTheTuneLifecycle_CyclingEveryChipState()
    {
        // The demo answers each RETU with ` TUNING COUPLER ` then, after its
        // delay, ONE terminal line — rotating so all three chip outcomes are
        // demonstrable without a radio. Asserted through the REAL parser and
        // mirror on the SpineStatusViewModel's own chip enum.
        ConnectReady();
        // A real tune takes seconds; the demo's delay is what makes the
        // Tuning state observable at all — hold the terminal back long
        // enough that each press's lifecycle is unambiguously sampled.
        _demo.TuneTerminalDelayMs = 150;
        var chip = new SpineStatusViewModel(new StatusSurface(_radio), _session);
        Assert.Equal(TuneChipState.None, chip.TuneChip);   // no tune this session yet

        TuneChipState PressRetune()
        {
            _radio.Ssb.Retune();
            // Tuning first (the animation), THEN the terminal — the second
            // wait cannot read a stale outcome because Tuning cleared it.
            WaitUntil(() => chip.TuneChip == TuneChipState.Tuning,
                "the demo's TUNING COUPLER line");
            WaitUntil(() => chip.TuneChip != TuneChipState.Tuning,
                "a terminal tune outcome from the demo RETU");
            return chip.TuneChip;
        }

        Assert.Equal(TuneChipState.Complete, PressRetune());
        Assert.Equal(TuneChipState.CompleteMarginal, PressRetune());
        Assert.Equal(TuneChipState.Fault, PressRetune());
        Assert.Equal(TuneChipState.Complete, PressRetune());   // wraps
    }

    [Fact]
    public void DemoRetune_EmitsTheTuningLineBeforeTheTerminal()
    {
        // The Tuning state is what makes the spine chip animate, so pin that
        // the demo really plays it (and in that order) at the wire.
        ConnectReady();
        var seen = new List<string>();
        void Watch(object? _, MessageReceivedEventArgs e)
        {
            var t = e.Message.Trim();
            if (t.StartsWith("TUNING", StringComparison.Ordinal)
                || t.StartsWith("TUNE ", StringComparison.Ordinal))
                lock (seen) seen.Add(t);
        }
        _radio.MessageReceived += Watch;
        _radio.Ssb.Retune();
        WaitUntil(() => { lock (seen) return seen.Count >= 2; }, "both tune lifecycle lines");
        _radio.MessageReceived -= Watch;

        lock (seen) Assert.Equal(["TUNING COUPLER", "TUNE COMPLETE"], seen);
    }

    // ---- Rule 4e: the canned ALE fill (plan-ale-programming.md §4.6) --------
    // The demo's ONE stateful area. These pin the SCRIPT — the R7 fill, the
    // captured listing/CHGROUP columns, the dependency refusals, the DELAD
    // cascade, what ERASE spares, and the baseline reset on every OPEN.
    // bench/ stays authoritative for the wire.

    [Fact]
    public void RawTranscript_SelfListing_IsTheR7Columns()
    {
        // "SLFAD ZZZ               CHGROUP 00" — the name sits in an
        // 18-character field. A single-space drift parses identically and
        // would otherwise ship silently (the HOPLIST-columns lesson).
        Assert.Equal(
            Crlf + "SLFAD ZZZ               CHGROUP 00" + Crlf
                 + "SLFAD TST               CHGROUP 01" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf + AlePrompt,
            RawAleReply("SLFAD"));
    }

    [Fact]
    public void RawTranscript_IndividualAndNetListings_CarryTheAssocSelfSegment()
    {
        var replies = RawAleReplies("INDAD", "NETAD");

        Assert.Equal(
            Crlf + "INDAD AAA               CHGROUP 01   ASSOC SELF TST" + Crlf
                 + "INDAD BBB               CHGROUP 01   ASSOC SELF TST" + Crlf
                 + "INDAD BOB               CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "INDAD HQ                CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "INDAD BASECAMP1         CHGROUP 03   ASSOC SELF CAM" + Crlf + AlePrompt,
            replies[0]);
        Assert.Equal(
            Crlf + "NETAD NT1               CHGROUP 01   ASSOC SELF TST" + Crlf
                 + "NETAD NET2              CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "NETAD ALLCALL           CHGROUP 03   ASSOC SELF CAM" + Crlf + AlePrompt,
            replies[1]);
    }

    [Fact]
    public void RawTranscript_ChannelGroup_IsTheR7Line_AndAnEmptyGroupAnswersNothing()
    {
        // "CHGROUP 01 CHANS 00 01 " — trailing space and all. An EMPTY group
        // answers NOTHING (protocol.md): the captured silence, never an
        // invented empty line.
        Assert.Equal(Crlf + "CHGROUP 01 CHANS 00 01 " + Crlf + AlePrompt, RawAleReply("CHG 1"));
        // The six-channel group renders on ONE line — the PROVISIONAL count
        // extension (the wrap question is A7c; §1's stated decision).
        Assert.Equal(
            Crlf + "CHGROUP 03 CHANS 05 15 25 35 45 55 " + Crlf + AlePrompt,
            RawAleReply("CHG 3"));
        Assert.Equal(Crlf + AlePrompt, RawAleReply("CHG 5"));
    }

    [Fact]
    public void RawTranscript_AleReadsOutsideAle_AreThePromptAlone()
    {
        // ALE-domain: at the SSB prompt the real radio rejects these and the
        // reject shape is uncaptured, so the demo stays at rule 6.
        Assert.Equal(Crlf + "SSB> ", RawDemoReplies("SLFAD")[0]);
        Assert.Equal(Crlf + "SSB> ", RawDemoReplies("CHG 1")[0]);
    }

    [Fact]
    public void DemoFillWrites_UpdateTheBook_AndAnswerPromptOnly()
    {
        // Clean-prompt SILENCE for the five fill writes is PROVISIONAL
        // (plan §1, bench item A7c) — the demo reproduces it, and the
        // re-listing is the verify, exactly as the app's own flow works.
        var replies = RawAleReplies("SLFAD NEW 2", "SLFAD", "INDAD IND 1 NEW", "INDAD");

        Assert.Equal(Crlf + AlePrompt, replies[0]);          // the write: prompt only
        Assert.Equal(
            Crlf + "SLFAD ZZZ               CHGROUP 00" + Crlf
                 + "SLFAD TST               CHGROUP 01" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf
                 + "SLFAD NEW               CHGROUP 02" + Crlf + AlePrompt,
            replies[1]);
        Assert.Equal(Crlf + AlePrompt, replies[2]);
        Assert.EndsWith(
            "INDAD IND               CHGROUP 01   ASSOC SELF NEW" + Crlf + AlePrompt,
            replies[3], StringComparison.Ordinal);
    }

    [Fact]
    public void DemoRefusals_ComeOutOfTheCannedState_InTheCapturedShapes()
    {
        var replies = RawAleReplies(
            "SLFAD ZZZ 0",          // a name the book already holds — global uniqueness
            "NETAD NT2 1 NOPE",     // an associated self that does not exist
            "ADDM NT1 NOPE",        // a member that does not exist
            "SLFAD");

        Assert.Equal(Crlf + " ADDRESS EXISTS " + Crlf + AlePrompt, replies[0]);
        Assert.Equal(Crlf + " INV ASSOC SELF " + Crlf + AlePrompt, replies[1]);
        Assert.Equal(Crlf + " INV MEMBER ADDR " + Crlf + AlePrompt, replies[2]);
        // …and a refused write changed NOTHING (the book still has three selfs).
        Assert.Equal(
            Crlf + "SLFAD ZZZ               CHGROUP 00" + Crlf
                 + "SLFAD TST               CHGROUP 01" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf + AlePrompt,
            replies[3]);
    }

    /// <summary>
    /// ROUND 11 P6 (sol-audit finding F3): deleting a SECONDARY self does NOT
    /// cascade. The characterization campaign (2026-08-17) measured the
    /// TWO-CASE model, and the demo used to implement the disproved universal
    /// cascade — demoing behavior OPPOSITE to the shipped delete caption for
    /// the COMMON case. TST is the second listing row, so its dependants
    /// survive and RE-POINT at the primary (ZZZ).
    /// </summary>
    [Fact]
    public void DemoDeleteOfASecondarySelf_RePointsItsDependantsAtThePrimary()
    {
        var replies = RawAleReplies("DELAD TST", "SLFAD", "INDAD", "NETAD");

        Assert.Equal(Crlf + AlePrompt, replies[0]);                      // silent
        Assert.Equal(
            Crlf + "SLFAD ZZZ               CHGROUP 00" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf + AlePrompt,
            replies[1]);
        // TST's individuals (AAA, BBB) SURVIVE, now associated to the primary
        // ZZZ; CAM's are untouched.
        Assert.Equal(
            Crlf + "INDAD AAA               CHGROUP 01   ASSOC SELF ZZZ" + Crlf
                 + "INDAD BBB               CHGROUP 01   ASSOC SELF ZZZ" + Crlf
                 + "INDAD BOB               CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "INDAD HQ                CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "INDAD BASECAMP1         CHGROUP 03   ASSOC SELF CAM" + Crlf + AlePrompt,
            replies[2]);
        // …and TST's net re-points too, rather than losing its self.
        Assert.Equal(
            Crlf + "NETAD NT1               CHGROUP 01   ASSOC SELF ZZZ" + Crlf
                 + "NETAD NET2              CHGROUP 02   ASSOC SELF CAM" + Crlf
                 + "NETAD ALLCALL           CHGROUP 03   ASSOC SELF CAM" + Crlf + AlePrompt,
            replies[3]);
    }

    /// <summary>The OTHER case of F3's two-case model: deleting the PRIMARY
    /// self (the first listing row) DOES destroy — its individuals go and its
    /// nets keep their entry with the associated self gone. This is the case
    /// the old universal-cascade demo got right by accident.</summary>
    [Fact]
    public void DemoDeleteOfThePrimarySelf_DeletesItsIndividuals_AndBlanksItsNets()
    {
        // Give the primary (ZZZ) dependants first, so the destructive case has
        // something to destroy — the baseline hangs everything off TST and CAM.
        var replies = RawAleReplies(
            "INDAD ZI 0 ZZZ", "NETAD ZN 0 ZZZ", "DELAD ZZZ", "SLFAD", "INDAD", "NETAD");

        Assert.Equal(Crlf + AlePrompt, replies[2]);                      // silent
        Assert.Equal(
            Crlf + "SLFAD TST               CHGROUP 01" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf + AlePrompt,
            replies[3]);
        // ZI is GONE; nothing else moved.
        Assert.DoesNotContain("ZI ", replies[4], StringComparison.Ordinal);
        Assert.Contains("INDAD AAA               CHGROUP 01   ASSOC SELF TST", replies[4], StringComparison.Ordinal);
        // ZN KEEPS its entry — without an associated self (the blank form's
        // real bytes are uncaptured (A7c), so the demo reuses the captured
        // no-assoc shape rather than invent one).
        Assert.Contains("NETAD ZN                CHGROUP 00" + Crlf, replies[5], StringComparison.Ordinal);
        Assert.Contains("NETAD NT1               CHGROUP 01   ASSOC SELF TST", replies[5], StringComparison.Ordinal);
    }

    [Fact]
    public void DemoErase_ClearsAddressesOnly_TheChannelGroupsSurvive()
    {
        var replies = RawAleReplies("ERASE", "SLFAD", "INDAD", "NETAD", "CHG 1");

        Assert.Equal(Crlf + AlePrompt, replies[0]);        // silent on success
        Assert.Equal(Crlf + AlePrompt, replies[1]);
        Assert.Equal(Crlf + AlePrompt, replies[2]);
        Assert.Equal(Crlf + AlePrompt, replies[3]);
        Assert.Equal(Crlf + "CHGROUP 01 CHANS 00 01 " + Crlf + AlePrompt, replies[4]);
    }

    /// <summary>
    /// ROUND 11 §9A: `ERASE` spares the STORED MESSAGES too — and the clone's
    /// leg order DEPENDS on it. The write campaign stores the file's message
    /// slots at leg 4 and erases the ALE fill at leg 6; if the erase took the
    /// messages with it, every clone would silently lose them and the only
    /// symptom would be a verify diff two legs later. Pinned here, where the
    /// behaviour lives, rather than left implicit in the round trip.
    /// </summary>
    [Fact]
    public void DemoErase_SparesTheStoredMessages_WhichTheCloneLegOrderRelieson()
    {
        var replies = RawAleReplies("TXMSG 3 KEEP ME", "ERASE", "TXMSG");

        Assert.Equal(Crlf + AlePrompt, replies[1]);        // silent on success
        Assert.Contains("TXMSG 03" + Crlf + "KEEP ME", replies[2], StringComparison.Ordinal);
        // …and the baseline slots are still there as well.
        Assert.Contains("RENDEZVOUS AT NOON", replies[2], StringComparison.Ordinal);
    }

    [Fact]
    public void DemoChannelEdits_AppendRemove_AndADuplicateAddIsSilentlyIgnored()
    {
        var replies = RawAleReplies(
            "ADDC 1 05", "CHG 1",
            "ADDC 1 05", "CHG 1",       // duplicate: silently ignored (protocol.md)
            "DELC 1 00", "CHG 1",
            "ADDC 4 09", "CHG 4");      // a previously EMPTY group

        Assert.Equal(Crlf + "CHGROUP 01 CHANS 00 01 05 " + Crlf + AlePrompt, replies[1]);
        Assert.Equal(Crlf + "CHGROUP 01 CHANS 00 01 05 " + Crlf + AlePrompt, replies[3]);
        Assert.Equal(Crlf + "CHGROUP 01 CHANS 01 05 " + Crlf + AlePrompt, replies[5]);
        Assert.Equal(Crlf + "CHGROUP 04 CHANS 09 " + Crlf + AlePrompt, replies[7]);
    }

    [Fact]
    public void DemoFill_ResetsToTheBaseline_OnEveryPortOpen()
    {
        // Decided lifecycle (§4.6): each connect is a factory-fresh demo
        // radio, so tests and demos are deterministic. Same port instance,
        // closed and reopened — the edits are gone.
        using var port = new RawDemoPort();

        port.Open();
        port.Exchange("ALE", "ERASE", "SLFAD MOD 3", "ADDC 1 07");
        Assert.Equal(
            Crlf + "SLFAD MOD               CHGROUP 03" + Crlf + AlePrompt,
            port.Exchange("SLFAD"));

        port.Close();
        port.Open();

        Assert.Equal(
            Crlf + "SLFAD ZZZ               CHGROUP 00" + Crlf
                 + "SLFAD TST               CHGROUP 01" + Crlf
                 + "SLFAD CAM               CHGROUP 02" + Crlf + AlePrompt,
            port.Exchange("ALE", "SLFAD"));
        Assert.Equal(Crlf + "CHGROUP 01 CHANS 00 01 " + Crlf + AlePrompt, port.Exchange("CHG 1"));
    }

    /// <summary>A bare <see cref="DemoSerialPort"/> a test can reopen — the
    /// only way to see the baseline reset. (The blocking waits live here, off
    /// the test methods themselves, exactly like
    /// <see cref="RawDemoReplies"/>.)</summary>
    private sealed class RawDemoPort : IDisposable
    {
        private readonly DemoSerialPort _port = new() { ResponseDelayMs = 0, TuneTerminalDelayMs = 0 };
        private readonly System.Collections.Concurrent.BlockingCollection<string> _chunks = [];

        public RawDemoPort()
            => _port.DataReceived += (_, e) => _chunks.Add(System.Text.Encoding.ASCII.GetString(e.Data));

        public void Open() => _port.OpenAsync(DemoSettings).GetAwaiter().GetResult();

        public void Close()
        {
            if (_port.IsOpen) _port.CloseAsync().GetAwaiter().GetResult();
            while (_chunks.TryTake(out _)) { }
        }

        /// <summary>Run the commands in order; returns the LAST reply.</summary>
        public string Exchange(params string[] commands)
        {
            string last = "";
            foreach (var command in commands)
            {
                _port.WriteAsync(System.Text.Encoding.ASCII.GetBytes(command)).GetAwaiter().GetResult();
                Assert.True(_chunks.TryTake(out var chunk, 5_000), "timed out on: " + command);
                last = chunk!;
            }
            return last;
        }

        public void Dispose() => Close();
    }

    [Fact]
    public void DemoFill_DrivesTheRealMirror_BookAndGroups()
    {
        // End to end over the production stack: the demo's lines through the
        // real framer, parser, read queue and mirror.
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ale);

        _radio.Ale.RefreshStationList();
        WaitUntil(() => _radio.State.Ale.NetAddresses.Count == 3, "the demo book committing");
        Assert.Equal(["ZZZ", "TST", "CAM"], _radio.State.Ale.SelfAddresses.Select(a => a.Address));
        Assert.Equal(["AAA", "BBB", "BOB", "HQ", "BASECAMP1"],
            _radio.State.Ale.IndividualAddresses.Select(a => a.Address));
        Assert.Equal("TST", _radio.State.Ale.NetAddresses[0].AssociatedSelf);
        Assert.Equal("CAM", _radio.State.Ale.NetAddresses[1].AssociatedSelf);

        _radio.Ale.RefreshChannelGroups();
        WaitUntil(() => _radio.State.Ale.ChannelGroups[1].Channels is not null,
            "the demo channel groups committing");
        Assert.Equal([0], _radio.State.Ale.ChannelGroups[0].Channels);
        Assert.Equal([0, 1], _radio.State.Ale.ChannelGroups[1].Channels);
        Assert.Equal([2, 3, 10], _radio.State.Ale.ChannelGroups[2].Channels);
        Assert.Equal([5, 15, 25, 35, 45, 55], _radio.State.Ale.ChannelGroups[3].Channels);
        // The unpopulated groups answered with silence → confirmed EMPTY,
        // which is NOT the same as never queried.
        Assert.Empty(_radio.State.Ale.ChannelGroups[4].Channels!);
        Assert.Empty(_radio.State.Ale.ChannelGroups[9].Channels!);
    }

    [Fact]
    public void DemoRefusal_DrivesTheRealRefusalMirror()
    {
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ale);

        _radio.Ale.SetSelfAddress("ZZZ", 0);      // a name the demo book holds
        WaitUntil(() => _radio.State.Ale.ProgrammingRefusal.Sequence == 1,
            "the demo ADDRESS EXISTS refusal reaching the mirror");
        Assert.Equal("ADDRESS EXISTS", _radio.State.Ale.ProgrammingRefusal.Line);
    }

    [Fact]
    public async Task Wrapper_AppendsDemoAfterPlatformPorts()
    {
        var wrapper = new DemoCapableSerialPort(new FakePortEnumerator { Ports = ["COM9"] });
        Assert.Equal(["COM9", "DEMO"], await wrapper.GetAvailablePortsAsync());
    }

    [Fact]
    public async Task Wrapper_RoutesDemoOpenToTheDemoPort_AndRealNamesToThePlatform()
    {
        var wrapper = new DemoCapableSerialPort(new FakePortEnumerator { Ports = ["COM9"] });

        // DEMO never touches the platform port (the enumeration-only fake
        // throws on open — reaching it would fail this test).
        await wrapper.OpenAsync(DemoSettings);
        Assert.True(wrapper.IsOpen);
        await wrapper.CloseAsync();
        Assert.False(wrapper.IsOpen);

        // A real port name routes to the platform implementation.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => wrapper.OpenAsync(new PortSettings { PortName = "COM9" }));
    }

    // ====================================================================
    // CLONE ROUND 12 P1 — the demo's new fidelity.
    // ====================================================================

    [Fact]
    public void DemoZero_PlaysTheCapturedTwoChunkSettle_AndTheSessionSurvives()
    {
        // The captured shape (bench/transcripts/r12-p1-20260818-222442): the
        // ZEROIZING banner with NO PROMPT, then silence, then
        // "*** ZEROIZE COMPLETE ***" and the prompt — 9.4 s on the real radio,
        // in the SAME session. The gap is the whole point: a campaign that
        // assumed an immediate prompt would write into a radio still wiping.
        ConnectReady();
        _demo.ZeroizeSettleDelayMs = 120;
        _radio.ZeroizeSettlePollMs = 30;
        _radio.ZeroizeSettleTimeoutMs = 5_000;

        var banners = new List<string>();
        _radio.MessageReceived += (_, e) => { if (e.Message.Contains("ZERO")) banners.Add(e.Message.Trim()); };

        _radio.Ssb.ZeroizeRadio();
        Assert.True(_radio.IsZeroizeSettling);

        WaitUntil(() => _radio.ZeroizeSettled, "the demo prompt returned after ZERO");
        Assert.False(_radio.ZeroizeFaulted);
        Assert.True(_transport.IsOpen);                    // the SESSION survived
        Assert.Equal(SessionPhase.Ready, _session.Phase);

        Assert.Contains("*** ZEROIZING RAM -- PLEASE WAIT ***", banners);
        Assert.Contains("*** ZEROIZE COMPLETE ***", banners);
    }

    [Fact]
    public void DemoZero_ClearsEveryDomain_AndSparesTheLineSettings()
    {
        ConnectReady();
        _demo.ZeroizeSettleDelayMs = 0;
        _radio.ZeroizeSettlePollMs = 20;
        _radio.ZeroizeSettleTimeoutMs = 5_000;

        // Something in each of two domains BEFORE the wipe, so "cleared" is
        // observable rather than vacuous.
        _radio.Ssb.DisplayChannels(0, 99);
        WaitUntil(() => _radio.State.ChannelList.Count > 0, "channels before ZERO");
        SelectModeConfirmed(OperatingMode.Ale);           // TXMSG is ALE-only
        _radio.Ale.QueryTxMessages();
        WaitUntil(() => _radio.State.Ale.TxMessages.Count > 0, "stored messages before ZERO");
        SelectModeConfirmed(OperatingMode.Ssb);           // …and ZERO is an SSB command

        _radio.Ssb.ZeroizeRadio();
        WaitUntil(() => _radio.ZeroizeSettled, "settle");

        // The radio is blank: re-reading finds nothing…
        _radio.Ssb.ForgetStoredChannels();
        _radio.Ssb.DisplayChannels(0, 99);
        _radio.QueryBatteryState();
        WaitUntil(() => _radio.State.BatteryStatus.IsConfirmed, "the demo answers after the wipe");

        // …with the CHANNEL TABLE the one domain that resets rather than
        // empties (re-based clone round 12 P2): the 2026-08-18 capture read
        // two programmed channels back as the DEFAULT ROW after the wipe, and
        // an unprogrammed `DI 50 50` answered one too. A wipe does not remove
        // slots; it resets them.
        Assert.Equal(100, _radio.State.ChannelList.Count);
        Assert.All(_radio.State.ChannelList,
            line => Assert.Contains("RxFr 01600000", line, StringComparison.Ordinal));

        // The stored messages are read at the ALE prompt — the radio-true one.
        SelectModeConfirmed(OperatingMode.Ale);
        _radio.Ale.ForgetStoredMessages();
        _radio.Ale.QueryTxMessages();
        _radio.QueryBatteryState();
        WaitUntil(() => _radio.State.BatteryStatus.IsConfirmed, "the demo answers the message read");
        Assert.Empty(_radio.State.Ale.TxMessages);

        // …and the LINE SETTINGS are spared by construction, which is why the
        // session is still up at the same rate (owner statement, §1).
        Assert.True(_transport.IsOpen);
    }

    /// <summary>
    /// `ZERO` IS ACCEPTED AT EVERY PROMPT, AND ALWAYS COMES BACK AT `SSB>`
    /// (captured 2026-08-19,
    /// bench/transcripts/r12-zero-prompts-20260819-061052.jsonl).
    ///
    /// <para>This is the radio fact the clone campaign's LITERAL ZERO-first
    /// shape rests on: the wipe goes out from wherever the operator left the
    /// radio, and the settle lands it where the next leg needs it, with no
    /// navigation from the app at all.</para>
    ///
    /// <para>The per-prompt PRE-BANNER lines are reproduced too, because they
    /// are the half a client can get wrong: an ALE-context wipe emits
    /// `IN_PROG`, a prompt echo and the fill-gate trailer before the banner,
    /// and a client that called any of them unrecognized would raise an error
    /// banner in the middle of the one operation that must not look like it
    /// went wrong.</para>
    /// </summary>
    [Theory]
    [InlineData(OperatingMode.Ale)]
    [InlineData(OperatingMode.Hop)]
    public void DemoZero_IsAcceptedAtAnyPrompt_AndSettlesBackAtSsb(OperatingMode from)
    {
        ConnectReady();
        _demo.ZeroizeSettleDelayMs = 0;
        _radio.ZeroizeSettlePollMs = 20;
        _radio.ZeroizeSettleTimeoutMs = 5_000;

        SelectModeConfirmed(from);
        var raised = new List<string>();
        _radio.ErrorOccurred += (_, e) => raised.Add(e.Message);

        _radio.Ssb.ZeroizeRadio();
        WaitUntil(() => _radio.ZeroizeSettled, "the wipe settled from a " + from + " prompt");

        // NO LINE OF THE PRE-BANNER INTERLEAVE WAS UNRECOGNIZED. (The
        // ZEROIZE-COMPLETE banner itself does surface — it is an unsolicited
        // `**` line and P1's discrimination carries its payload verbatim
        // rather than rebadging it — which is exactly why this asserts the
        // UNRECOGNIZED shape and not "nothing was raised".)
        Assert.DoesNotContain(raised, m => m.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(raised, m => m.Contains("IN_PROG", StringComparison.Ordinal));
        Assert.DoesNotContain(raised, m => m.Contains("PRG 1-3", StringComparison.Ordinal));

        // …and the radio IS at SSB>. The app learns it the way the clone
        // campaign does — with the FRESH MODE QUERY that follows the settle,
        // not by trusting a mirror the settle boundary deliberately reset. That
        // belt is the whole reason the campaign can send `ZERO` first and still
        // know where it is standing afterwards.
        Assert.False(_radio.State.OperatingMode.IsConfirmed,
            "the settle boundary is supposed to leave the mode UNCONFIRMED — the belt is what re-learns it");
        _radio.Show();
        WaitUntil(() => _radio.State.OperatingMode.IsConfirmed, "the fresh mode query answered");
        Assert.Equal(OperatingMode.Ssb, _radio.State.OperatingMode.Value);

        // ANTI-VACUITY: the wipe really happened from there — the radio is
        // blank and fully locked, not merely re-prompted.
        long id = _radio.Ssb.QueryLockouts();
        WaitUntil(() => _radio.State.LastLockoutRead.ReadId == id, "the lockout read committed");
        Assert.All(_radio.State.Lockouts.Rows, r => Assert.Equal(LockState.Lock, r.State));
    }

    [Fact]
    public void DemoLockouts_AnswerBothGlobalReports_WithTheCapturedSections()
    {
        ConnectReady();
        long id = _radio.Ssb.QueryLockouts();
        WaitUntil(() => _radio.State.LastLockoutRead.ReadId == id, "the lockout read committed");

        Assert.True(_radio.State.LastLockoutRead.Answered);
        Assert.Equal(LockoutReadState.Completed, _radio.State.Lockouts.State);
        Assert.Equal(22, _radio.State.Lockouts.Rows.Count);

        // The demo's baseline is MIXED, so a read that answered one value
        // everywhere could not pass this.
        Assert.Contains(_radio.State.Lockouts.Rows, r => r.State == LockState.Lock);
        Assert.Contains(_radio.State.Lockouts.Rows, r => r.State == LockState.Unlock);
    }

    [Fact]
    public void DemoLockoutSet_IsScopedToTheActivePrompt()
    {
        // THE P-1 CAPTURE, COPIED (2026-08-18): all six discrimination-matrix
        // cells moved exactly their own prompt's section. `DATA` exists in both
        // the SSB and HOP PROGRAM sections, so this is the cell that actually
        // discriminates.
        ConnectReady();
        SelectModeConfirmed(OperatingMode.Ssb);
        _radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Ssb, "DATA", LockState.Lock);
        long first = _radio.Ssb.QueryLockouts();
        WaitUntil(() => _radio.State.LastLockoutRead.ReadId == first, "read 1");

        Assert.Equal(LockState.Lock, RowState(LockoutFamily.Program, LockoutSection.Ssb, "DATA"));

        // The SAME command at the HOP prompt moves the HOP row instead.
        SelectModeConfirmed(OperatingMode.Hop);
        _radio.Ssb.SetLockout(LockoutFamily.Program, LockoutSection.Hop, "DATA", LockState.Unlock);
        long second = _radio.Ssb.QueryLockouts();
        WaitUntil(() => _radio.State.LastLockoutRead.ReadId == second, "read 2");

        Assert.Equal(LockState.Lock, RowState(LockoutFamily.Program, LockoutSection.Ssb, "DATA"));
        Assert.Equal(LockState.Unlock, RowState(LockoutFamily.Program, LockoutSection.Hop, "DATA"));
    }

    [Fact]
    public void DemoZero_PutsEveryLockoutBackToLock()
    {
        // Captured twice (r11-lockouts and r12-p1): 22/22 LOCK after a wipe.
        ConnectReady();
        _demo.ZeroizeSettleDelayMs = 0;
        _radio.ZeroizeSettlePollMs = 20;
        _radio.ZeroizeSettleTimeoutMs = 5_000;

        _radio.Ssb.ZeroizeRadio();
        WaitUntil(() => _radio.ZeroizeSettled, "settle");

        long id = _radio.Ssb.QueryLockouts();
        WaitUntil(() => _radio.State.LastLockoutRead.ReadId == id, "post-ZERO lockout read");

        Assert.Equal(22, _radio.State.Lockouts.Rows.Count);
        Assert.All(_radio.State.Lockouts.Rows, r => Assert.Equal(LockState.Lock, r.State));
    }

    private LockState RowState(LockoutFamily family, LockoutSection section, string item)
        => _radio.State.Lockouts.Rows
            .Single(r => r.Family == family && r.Section == section && r.Item == item).State;

    [Fact]
    public void DemoSelectingADisabledPreset_AnswersPresetDisabled_AndTheAppSaysSoInWords()
    {
        // §9 A1 end to end: the demo answers the captured refusal, the parser
        // recognizes it, and the operator gets WORDS — not the raw token, and
        // not the "Unrecognized message" banner that was the bench symptom.
        ConnectReady();
        string? error = null;
        _radio.ErrorOccurred += (_, e) => error = e.Message;

        _radio.Ssb.SelectModem("2");        // preset 2 is the canned DISABLED one
        WaitUntil(() => error is not null, "the refusal reached the operator");

        Assert.Contains("disabled", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unrecognized", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRESET DISABLED", error!, StringComparison.Ordinal);   // R13
    }

    [Fact]
    public void DemoFieldWrite_ReEnablesADisabledPreset()
    {
        // Captured 2026-08-18: `MODEM PRESET 6 BAUD 1200`, sent to a preset
        // ABSENT from the bulk listing, echoed and put the row BACK in it. The
        // enable/disable lockout is not a field the operator sets and leaves —
        // which is precisely why the app writes the state token LAST.
        ConnectReady();
        long before = _radio.Ssb.QueryModemPresetPresence();
        WaitUntil(() => _radio.State.LastModemRead.ReadId == before, "presence read 1");
        Assert.DoesNotContain(2, _radio.State.ModemPresetPresence.Enabled);

        _radio.Ssb.ProgramModemPreset(2, "DAT2", "39TONE", "ASYNC REM", "1200");
        DrainTheWriteEcho(2);
        long after = _radio.Ssb.QueryModemPresetPresence();
        WaitUntil(() => _radio.State.LastModemRead.ReadId == after, "presence read 2");

        Assert.Contains(2, _radio.State.ModemPresetPresence.Enabled);
    }

    /// <summary>
    /// A programming WRITE is not a read OPERATION, so its listing-form ECHO is
    /// not inside any window — and the round-11 §8 rule is that a row arriving
    /// inside a PRESENCE window counts as ENABLED. Requesting presence in the
    /// same breath as a write therefore races the echo into that window and
    /// "proves" whatever the echo says. The targeted read below is an
    /// operation: its window absorbs the echo, and only then is the presence
    /// question asked — which is exactly the order ModemPresetsViewModel uses.
    /// </summary>
    private void DrainTheWriteEcho(int preset)
    {
        long id = _radio.Ssb.QueryModemPreset(preset);
        WaitUntil(() => _radio.State.LastModemRead.ReadId == id, "the write echo drained by a targeted read");
    }

    [Fact]
    public void DemoFieldWriteWithAnExplicitDisable_StaysDisabled()
    {
        // The other half: the state token on the SAME line still decides, so
        // "any field write re-enables" cannot be read as "DIS is ignored".
        ConnectReady();
        _radio.Ssb.ProgramModemPreset(1, "T39", "39TONE", "ASYNC DAT", "2400", enabled: false);
        DrainTheWriteEcho(1);
        long id = _radio.Ssb.QueryModemPresetPresence();
        WaitUntil(() => _radio.State.LastModemRead.ReadId == id, "presence read");

        Assert.DoesNotContain(1, _radio.State.ModemPresetPresence.Enabled);
    }

    [Fact]
    public void DemoCompressionQuery_Answers()
    {
        // §9 B3 PRIMARY branch (P-2c capture): bare `COM` answers `COMPRESS x`.
        //
        // ISOLATED FROM THE PRECEDING ECHO (audit round 1, finding 5). The
        // first version set compression and then asserted the MIRROR after a
        // bare query — which the SET's own echo had already satisfied, so
        // deleting the demo's bare-COM response left all 1523 tests green.
        // This is the same echo-race class the preset tests hit. The fix is to
        // assert on the ANSWER LINE itself: the query's own reply is the only
        // thing that can produce it, and a demo that stops answering `COM`
        // fails here immediately.
        ConnectReady();
        _radio.Ssb.SetCompression(OnOff.Off);
        WaitUntil(() => _radio.State.Compression.IsConfirmed && _radio.State.Compression.Value == OnOff.Off,
            "compression OFF from the set echo");

        // Drain: everything the SET produced is now in the past.
        var answers = new List<string>();
        _radio.MessageReceived += (_, e) =>
        {
            if (e.Message.TrimStart().StartsWith("COMPRESS", StringComparison.Ordinal))
                answers.Add(e.Message.Trim());
        };
        _radio.QueryBatteryState();
        WaitUntil(() => _radio.State.BatteryStatus.IsConfirmed, "the drain barrier answered");
        Assert.Empty(answers);                 // nothing in flight before the query

        _radio.Ssb.QueryCompression();
        WaitUntil(() => answers.Count > 0, "the demo answered the bare COM query");

        Assert.Equal(["COMPRESS OFF"], answers);
    }

    /// <summary>
    /// <b>THE TEMPORARY INFIDELITY, RETIRED (clone round 12 P2).</b> The real
    /// radio's TXMSG family is <c>ALE&gt;</c>-ONLY — it answers
    /// <c>** ERROR **</c> at SSB and HOP (captured 2026-08-18). P1 deliberately
    /// KEPT the demo's <c>SSB&gt;</c> answering, because the clone campaign
    /// still issued its message leg there; this is the commit that moved the
    /// leg, so the demo is radio-true again and the pin asserts the OPPOSITE
    /// property it used to.
    /// </summary>
    [Fact]
    public void DemoTxmsg_AnswersAtTheAlePromptOnly_TheRadioTrueShape()
    {
        ConnectReady();

        SelectModeConfirmed(OperatingMode.Ale);
        _radio.Ale.ForgetStoredMessages();
        _radio.Ale.QueryTxMessages();
        WaitUntil(() => _radio.State.Ale.TxMessages.Count > 0, "TXMSG answered at ALE> (the RADIO-TRUE prompt)");

        string? error = null;
        _radio.ErrorOccurred += (_, e) => error = e.Message;
        SelectModeConfirmed(OperatingMode.Ssb);
        _radio.Ale.ForgetStoredMessages();
        _radio.Ale.QueryTxMessages();

        WaitUntil(() => error is not null, "the demo refused TXMSG at SSB> — the infidelity is gone");
        Assert.Empty(_radio.State.Ale.TxMessages);
    }

    [Fact]
    public void DemoTxmsg_AtAHopPrompt_AnswersTheCapturedRefusal()
    {
        // The other refused prompt, unchanged since P1: the radio answers
        // `** ERROR **` and the store stays untouched.
        ConnectReady();
        string? error = null;
        _radio.ErrorOccurred += (_, e) => error = e.Message;

        SelectModeConfirmed(OperatingMode.Hop);
        _radio.Ale.ForgetStoredMessages();
        _radio.Ale.QueryTxMessages();

        WaitUntil(() => error is not null, "the demo refused TXMSG at HOP>");
        Assert.Empty(_radio.State.Ale.TxMessages);
        // R13: the operator's sentence carries no radio token.
        Assert.DoesNotContain("**", error!);
    }

    // =====================================================================
    // CLONE ROUND 12 P4 — the DIGITAL VOICE interaction matrix (D1)
    //
    // The demo models ONLY the CAPTURED sequences (docs/protocol.md "Digital
    // voice — the interaction matrix", captured r12-p2, plus probe R4's
    // excursion). Every uncaptured transition below is labelled
    // DEMO-CHOICE-UNCAPTURED and carries its probe-track note; a capture that
    // disagrees is a content fix, not a redesign.
    // =====================================================================

    // ONE ordered wire log, sends and receipts together — the transport is
    // PROMPT-GATED (probe R10), so the lines between a command and the prompt
    // that follows it ARE that command's answer. Deterministic, which matters
    // here: P4's trigger row puts an `SH` on the wire straight after a DV echo,
    // and that block must not be mistaken for part of the echo. The demo raises
    // on its own read thread, so the log is locked.
    private readonly List<(bool Sent, string Text)> _wire = [];
    private readonly object _wireLock = new();

    private void CaptureWire()
    {
        _radio.LineSent += (_, e) => { lock (_wireLock) _wire.Add((true, e.Line.Trim())); };
        _radio.MessageReceived += (_, e) => { lock (_wireLock) _wire.Add((false, e.Message.Trim())); };
    }

    /// <summary>
    /// Wait until everything already queued has been ANSWERED, using the
    /// session's own barrier: command responses return in command order
    /// (protocol.md Q2/Q8), so a sentinel's answer proves the queue ahead of
    /// it drained. (On the REAL radio a mode entry can also print an EXTRA
    /// `Battery Status` — measured 2026-08-22 — which the stray rule refuses;
    /// the demo prints exactly one per `BAT ST`, so this drain is exact here.)
    /// <para>TWICE, deliberately. Core's trigger table queues its re-polls FROM
    /// the prompt that ends a block, so the first drain can leave one more
    /// compensation behind it; the second sweeps that up. Without this a test
    /// that captures the wire cannot tell its own answer from the tail of
    /// somebody else's.</para>
    /// </summary>
    private void Drain()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool done = false;
            _radio.Ping(_ => done = true, 5_000);
            WaitUntil(() => done, "the wire to drain (sentinel pass " + pass + ")");
        }
        // …AND the sentinel's own terminating PROMPT, which lands after the
        // `BATTERY` line the callback fired on. Sends are logged at ENQUEUE
        // time, so a command handed over in that gap logs AHEAD of a prompt
        // that is not its own — and AnswerTo would then read the tail of the
        // sentinel's answer as that command's.
        WaitUntil(WireIsSettledAtAPrompt, "the wire to settle at a prompt");
    }

    private bool WireIsSettledAtAPrompt()
    {
        lock (_wireLock)
            return _wire.Count > 0 && !_wire[^1].Sent && _wire[^1].Text.EndsWith('>');
    }

    /// <summary>The payload lines the radio answered <paramref name="command"/>
    /// with, or null until that answer's terminating prompt has arrived. Call
    /// <see cref="Drain"/> first: sends are logged at ENQUEUE time (the
    /// transport's writer is prompt-gated), so this only reads cleanly when
    /// nothing else was already in the queue.</summary>
    private string[]? AnswerTo(string command)
    {
        (bool Sent, string Text)[] wire;
        lock (_wireLock) wire = [.. _wire];

        int start = Array.FindLastIndex(wire, x => x.Sent && x.Text == command);
        if (start < 0) return null;

        var answer = new List<string>();
        for (int i = start + 1; i < wire.Length; i++)
        {
            if (wire[i].Sent) continue;
            if (wire[i].Text.EndsWith('>')) return [.. answer];
            if (wire[i].Text.Length > 0) answer.Add(wire[i].Text);
        }
        return null;
    }

    /// <summary>Read the live block back and wait for a mirror to settle. The
    /// demo answers a `MODE`/`BA` write PROMPT-ONLY (no capture records what a
    /// store-excursion write echoes), so the `SH` is how anything is
    /// observed — which is exactly what P4's own trigger row does for real.</summary>
    private void ShowAndWait(Func<bool> settled, string what)
    {
        Drain();                // let anything already queued be answered…
        _radio.Show();
        Drain();                // …and this read, plus any follow-up it provokes
        WaitUntil(settled, what);
    }

    /// <summary>Set DV and let the whole exchange — the echo AND the trigger
    /// row's own re-read — finish, so the mirror a test then reads is the
    /// settled one rather than a value some block is about to overwrite.</summary>
    private void SetDvAndWait(OnOff state, string what)
    {
        _radio.Ssb.SetDigitalVoice(state);
        Drain();
        WaitUntil(
            () => _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == state,
            what);
    }

    /// <summary>Park the demo on a known entry tuple: modulation, bandwidth,
    /// analog squelch OFF, DV OFF — the D1 bracket's own preconditions.</summary>
    private void EnterModulation(ModulationMode mode, string bandwidth)
    {
        _radio.Ssb.SetSquelch(OnOff.Off);
        _radio.Ssb.SetModulation(mode);
        _radio.Ssb.SetBandwidth(bandwidth);
        ShowAndWait(
            () => _radio.State.ModulationMode.IsConfirmed && _radio.State.ModulationMode.Value == mode
                  && _radio.State.Bandwidth.IsConfirmed && _radio.State.Bandwidth.Value == bandwidth
                  && _radio.State.AnalogSquelch.IsConfirmed && _radio.State.AnalogSquelch.Value == OnOff.Off
                  && _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == OnOff.Off,
            $"the {mode} entry tuple");
    }

    /// <summary>
    /// (i) THE FIVE PER-MODULATION LEGS, one InlineData per captured row.
    /// `DV ON` stores the entry tuple and forces USB (from AME, CW, FM),
    /// analog SQUELCH ON and BAND 3.0; same-channel `DV OFF` restores all
    /// three exactly. The entry bandwidths are the capture's own.
    /// </summary>
    [Theory]
    [InlineData("USB", "2.7", "USB")]
    [InlineData("LSB", "2.7", "LSB")]   // LSB is a sideband: the modulation stays
    [InlineData("AME", "6.0", "USB")]   // **silently forced**
    [InlineData("CW", "1.0", "USB")]    // **silently forced**
    [InlineData("FM", "2.7", "USB")]    // **silently forced**
    public void DemoDvOn_ForcesTheCapturedTuple_AndDvOffRestoresItExactly(
        string entry, string entryBand, string forced)
    {
        ConnectReady();
        var entryMode = Enum.Parse<ModulationMode>(entry, ignoreCase: true);
        var forcedMode = Enum.Parse<ModulationMode>(forced, ignoreCase: true);
        EnterModulation(entryMode, entryBand);

        SetDvAndWait(OnOff.On, "DV ON from the demo echo");
        ShowAndWait(
            () => _radio.State.Bandwidth.IsConfirmed && _radio.State.Bandwidth.Value == "3.0",
            "the forced BAND 3.0");
        Assert.Equal(forcedMode, _radio.State.ModulationMode.Value);
        Assert.Equal(OnOff.On, _radio.State.AnalogSquelch.Value);

        SetDvAndWait(OnOff.Off, "DV OFF from the demo echo");
        ShowAndWait(
            () => _radio.State.Bandwidth.IsConfirmed && _radio.State.Bandwidth.Value == entryBand,
            "the restored entry bandwidth");
        Assert.Equal(entryMode, _radio.State.ModulationMode.Value);
        Assert.Equal(OnOff.Off, _radio.State.AnalogSquelch.Value);
    }

    /// <summary>The ECHO SHAPE, which is the whole point: `MODEM OFF`,
    /// `DV x`, `DGT_SQUELCH x` — and NO `MODE` line, however far the
    /// modulation moved. The silent mutation is only findable by re-reading,
    /// which is what P4's trigger row exists to do.</summary>
    [Fact]
    public void DemoDvEcho_CarriesNoModeLine_HoweverFarTheModulationMoved()
    {
        ConnectReady();
        EnterModulation(ModulationMode.Fm, "2.7");

        Drain();

        _radio.Ssb.SetDigitalVoice(OnOff.On);
        Drain();
        WaitUntil(() => AnswerTo("DV ON") is not null, "the DV echo");

        var echo = AnswerTo("DV ON")!;
        Assert.Equal(["MODEM OFF", "DV ON", "DGT_SQUELCH OFF"], echo);
        // …and none of the three values it silently moved is mentioned. (The
        // `MODEM OFF` line is the modem's, not the modulation's — hence the
        // trailing space in the token being ruled out.)
        Assert.DoesNotContain(echo, l => l.StartsWith("MODE ", StringComparison.Ordinal));
        Assert.DoesNotContain(echo, l => l.StartsWith("BAND ", StringComparison.Ordinal));
        Assert.DoesNotContain(echo, l => l.StartsWith("SQUELCH ", StringComparison.Ordinal));
    }

    /// <summary>(ii) THE ONE CAPTURED EXCURSION (probe R4): with DV ON, a
    /// modulation leaving USB/LSB auto-SUSPENDS DV — a read then honestly says
    /// `DV OFF` — and returning to the sideband auto-RESTORES it. No
    /// compensation is wanted; the radio manages it.</summary>
    [Fact]
    public void DemoDvExcursion_AutoSuspendsOutsideTheSidebands_AndAutoRestoresOnReturn()
    {
        ConnectReady();
        EnterModulation(ModulationMode.Usb, "2.7");

        SetDvAndWait(OnOff.On, "DV engaged");

        _radio.Ssb.SetModulation(ModulationMode.Cw);        // …silently suspends it
        ShowAndWait(
            () => _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == OnOff.Off,
            "DV auto-suspended mid-excursion");
        Assert.Equal(ModulationMode.Cw, _radio.State.ModulationMode.Value);

        _radio.Ssb.SetModulation(ModulationMode.Usb);       // …and silently restores it
        ShowAndWait(
            () => _radio.State.DigitalVoice.IsConfirmed && _radio.State.DigitalVoice.Value == OnOff.On,
            "DV auto-restored on return");
        Assert.Equal(ModulationMode.Usb, _radio.State.ModulationMode.Value);
    }

    /// <summary>(iii) THE ONE CAPTURED CHANNEL OBSERVATION: at `CH 02` with
    /// `DV ON`, selecting `CH 01` answered `CHAN 01` ALONE and left `DV ON`
    /// standing, "with the same BAND 2.7 → 3.0 shift the DV toggle itself
    /// makes" — the overlay re-seats on the row just loaded.</summary>
    [Fact]
    public void DemoMidDvChannelSelect_AnswersChanAlone_AndKeepsDvWithItsOverlay()
    {
        ConnectReady();
        _radio.Ssb.SelectChannel(2);
        ShowAndWait(
            () => _radio.State.OperatingChannel.IsConfirmed && _radio.State.OperatingChannel.Value == 2,
            "CH 02 selected");
        SetDvAndWait(OnOff.On, "DV engaged at CH 02");

        Drain();
        _radio.Ssb.SelectChannel(1);
        Drain();
        WaitUntil(() => AnswerTo("CH 1") is not null, "the CHAN answer");

        // `CHAN nn` ALONE — no DV line, no MODE line, nothing else.
        Assert.Equal(["CHAN 01"], AnswerTo("CH 1")!);

        ShowAndWait(
            () => _radio.State.OperatingChannel.IsConfirmed && _radio.State.OperatingChannel.Value == 1,
            "CH 01 loaded");
        Assert.Equal(OnOff.On, _radio.State.DigitalVoice.Value);       // DV STAYS ON
        Assert.Equal("3.0", _radio.State.Bandwidth.Value);             // the overlay re-seated
        Assert.Equal("14313500", _radio.State.RxFrequency.Value);      // …on CH 01's own row
    }

    /// <summary>
    /// (iv) DEMO-CHOICE-UNCAPTURED — `DV OFF` AFTER a mid-DV channel change.
    /// Nothing captured says what it leaves behind. The demo answers the
    /// CURRENT channel's stored row with the overlay simply removed; the
    /// displaced analog squelch rides across the re-seat, so it is still the
    /// ENGAGEMENT's own value that comes back. PROBE TRACK: engage DV, change
    /// channel, disengage, and read both rows back.
    /// </summary>
    [Fact]
    public void DemoDvOffAfterAMidDvChannelChange_RestoresTheCurrentRow_DemoChoiceUncaptured()
    {
        ConnectReady();
        _radio.Ssb.SelectChannel(2);                       // LSB / 2.7 in the baseline
        ShowAndWait(() => _radio.State.OperatingChannel.IsConfirmed
                          && _radio.State.OperatingChannel.Value == 2, "CH 02");

        SetDvAndWait(OnOff.On, "DV engaged at CH 02");
        _radio.Ssb.SelectChannel(1);                       // USB / 2.7 in the baseline
        Drain();

        SetDvAndWait(OnOff.Off, "DV disengaged at CH 01");
        ShowAndWait(() => _radio.State.Bandwidth.IsConfirmed
                          && _radio.State.Bandwidth.Value == "2.7", "CH 01's own stored bandwidth");

        // THE DEMO CHOICE: CH 01's row, not CH 02's — and the engagement's own
        // squelch (OFF, the value standing when DV was engaged at CH 02).
        Assert.Equal(ModulationMode.Usb, _radio.State.ModulationMode.Value);
        Assert.Equal("14313500", _radio.State.RxFrequency.Value);
        Assert.Equal(OnOff.Off, _radio.State.AnalogSquelch.Value);
    }

    /// <summary>
    /// DEMO-CHOICE-UNCAPTURED — REPEATED / IDEMPOTENT DV commands. `DV ON`
    /// while DV already reads ON, and `DV OFF` while DV is AUTO-SUSPENDED,
    /// were never captured. The demo answers the echo and MOVES NOTHING:
    /// re-engaging would overwrite the stored entry tuple with the forced
    /// values (so the eventual `DV OFF` could no longer restore it), and
    /// un-suspending would fight the operator's own `MODE` write. PROBE TRACK:
    /// two commands at the bench settle both.
    /// </summary>
    [Fact]
    public void DemoRepeatedDvCommands_AnswerTheIdempotentEcho_DemoChoiceUncaptured()
    {
        ConnectReady();
        EnterModulation(ModulationMode.Ame, "6.0");

        SetDvAndWait(OnOff.On, "DV engaged from AME");

        // (a) `DV ON` again: the echo, and the entry tuple SURVIVES it.
        _radio.Ssb.SetDigitalVoice(OnOff.On);
        ShowAndWait(() => _radio.State.Bandwidth.IsConfirmed
                          && _radio.State.Bandwidth.Value == "3.0", "still overlaid");
        _radio.Ssb.SetDigitalVoice(OnOff.Off);
        ShowAndWait(() => _radio.State.Bandwidth.IsConfirmed
                          && _radio.State.Bandwidth.Value == "6.0", "the AME entry tuple still restores");
        Assert.Equal(ModulationMode.Ame, _radio.State.ModulationMode.Value);

        // (b) `DV OFF` while AUTO-SUSPENDED: the echo, and nothing moves — the
        // operator's own excursion modulation is left exactly where they put it.
        EnterModulation(ModulationMode.Usb, "2.7");
        SetDvAndWait(OnOff.On, "DV engaged from USB");
        _radio.Ssb.SetModulation(ModulationMode.Fm);
        ShowAndWait(() => _radio.State.DigitalVoice.IsConfirmed
                          && _radio.State.DigitalVoice.Value == OnOff.Off, "auto-suspended in FM");

        _radio.Ssb.SetDigitalVoice(OnOff.Off);
        ShowAndWait(() => _radio.State.ModulationMode.IsConfirmed
                          && _radio.State.ModulationMode.Value == ModulationMode.Fm,
            "the operator's FM survives the idempotent DV OFF");
        Assert.Equal(OnOff.Off, _radio.State.DigitalVoice.Value);
    }

    public void Dispose()
    {
        _session.Dispose();
        _radio.Dispose();
        _transport.Dispose();
    }
}
