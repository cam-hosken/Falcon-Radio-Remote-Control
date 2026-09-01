using System.Text;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// Byte-faithful replay: the R1 capture (docs/probes.md) fed through
/// LineFramer → ResponseParser exactly as the bytes arrived on COM20,
/// including the bare-LF RXONLY quirk and the "&lt;CR&gt;SSB&gt; " prompt shape.
/// </summary>
public class FramerParserIntegrationTests
{
    [Fact]
    public void R1Capture_ShBlock_ByteFaithful_PopulatesMirror()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // Verbatim from docs/probes.md R1 ("=== TX: SH ===" block):
        // <LF> then payload lines <CR><LF>, RXONLY ends with bare <LF>,
        // BFO line starts after a bare <CR>, block ends <CR><LF> <CR>SSB>.
        var bytes =
            "\n" +
            "CHAN 00 \r\n" +
            "KEY OFF \r\n" +
            "RxFr 01600000\r\n" +
            "TxFr 01600000\r\n" +
            "MODE CW \r\n" +
            "AGC MED \r\n" +
            "BAND 1.0 \r\n" +
            "RXONLY NO \n" +
            "\rBFO +0000\r\n" +
            "MODEM OFF\r\n" +
            "DV OFF\r\n" +
            "DGT_SQUELCH OFF\r\n" +
            "AVS OFF\r\n" +
            "ENCRYPT OFF\r\n" +
            "SQ_LEVEL HIGH\r\n" +
            "SQUELCH OFF\r\n" +
            "POWER low\r\n" +
            "ANTENNA   auto \r\n" +
            "CWOFFSET 0000\r\n" +
            "RWAS DISABLED\r\n" +
            "RETRANS DISABLED\r\n" +
            "\r\n" +
            "\rSSB> ";

        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
        {
            var r = parser.Parse(line);
            Assert.True(r.Handled, "Unhandled: '" + line + "'");
            Assert.Null(r.PayloadError);
        }

        Assert.Equal(0, state.OperatingChannel.Value);
        Assert.Equal(ModulationMode.Cw, state.ModulationMode.Value);
        Assert.Equal(AgcSpeed.Medium, state.AgcSpeed.Value);
        Assert.Equal("1.0", state.Bandwidth.Value);
        Assert.Equal(YesNo.No, state.ChannelRxOnly.Value);
        Assert.Equal(PowerLevel.Low, state.PowerLevel.Value);
        Assert.Equal(OperatingMode.Ssb, state.OperatingMode.Value);
    }

    [Fact]
    public void R2Capture_TriplePromptInterleave_ByteFaithful()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var bytes =
            "\n" +
            "IN_PROG\r\n" +
            "\r\n" +
            "\rALE> \r\n" +
            "\rALE> PRG 1-3 CHAR SLF\r\n" +
            "\r\n" +
            "\rALE> Battery Status FULL 31.2V\r\n" +
            "\r\n" +
            "\rALE> ";

        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
        {
            var r = parser.Parse(line);
            Assert.True(r.Handled, "Unhandled: '" + line + "'");
        }

        Assert.Equal(AleFillState.NeedSelfAddress, state.Ale.FillState.Value);
        Assert.Equal("Status FULL 31.2V", state.BatteryStatus.Value);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }

    [Fact]
    public void R2Capture_HopEntry_ByteFaithful()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var bytes =
            "\n" +
            "Wait...\r\n" +
            "No Hopset\r\n" +
            "Wait...\r\n" +
            "No Hopset\r\n" +
            "\r\n" +
            "\rHOP> \r\n" +
            "\rHOP> ";

        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
        {
            var r = parser.Parse(line);
            Assert.True(r.Handled, "Unhandled: '" + line + "'");
        }

        Assert.Equal(0, state.Hop.HopNum.Value);
        Assert.Equal(OperatingMode.Hop, state.OperatingMode.Value);
    }

    // ====================================================================
    // ROUND 16 FIXES S1 — ASYNC LINES INSIDE A LISTING, AS CAPTURED
    // ====================================================================
    // These four replays are the EVIDENCE BASE for S1's suspension rule. None
    // of them puts an async line between a wrap HEADER and its wrap LINE (no
    // capture does — that shape is ASSUMED, and its pins live in
    // ResponseParserTests as constructed fixtures); what they DO show is that
    // the radio interleaves exactly these lines into a listing at all, and
    // they are regression pins that the listings still land.

    /// <summary>
    /// <c>bench/transcripts/r11-ale-race-20260818-184900.jsonl</c> record 38
    /// (burst A14, `SLFAD SD 1` + `NETAD NTA` + `BAT ST`): a `KEY OFF ` and a
    /// `SCANNING` land INSIDE the targeted net record's own MEMBER block.
    ///
    /// <para>Asserted at the PARSER level only. The `MEMBER` rows need an
    /// ACTIVE targeted read to be attributed to a net, which a parser-level
    /// replay cannot establish — a stated limit, and why this pin asserts the
    /// NETAD book row, the async lines' own effects, and that nothing was
    /// unrecognized.</para>
    /// </summary>
    [Fact]
    public void R11AleRace_Record38_ByteFaithful_TheAsyncLinesLand_WithNothingUnrecognized()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // Record 38's `raw`, byte for byte.
        var bytes =
            "\n\n\nIN_PROG\r\n\r\n\rALE> NETAD NTA               CHGROUP 01   ASSOC SELF PRI\r\n"
            + "     MEMBER 01  IN1\r\n     MEMBER 02  IN2\r\n     MEMBER 03  IN3\r\n     MEMBER 04  IN4\r\n"
            + "KEY OFF \r\n\r\n\rALE> Battery Status FULL 26.4V\r\nSCANNING\r\n\r\n\rALE> \r\n\rALE> ";

        var unhandled = new List<string>();
        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
            if (!parser.Parse(line).Handled) unhandled.Add(line);

        Assert.Empty(unhandled);

        // The book row landed…
        var net = Assert.Single(state.Ale.NetAddresses);
        Assert.Equal("NTA", net.Address);
        Assert.Equal(1, net.ChannelGroup);
        Assert.Equal("PRI", net.AssociatedSelf);

        // …and each interleaved async line did its own job.
        Assert.Equal(KeylineState.Off, state.Keyline.Value);
        Assert.Equal("Status FULL 26.4V", state.BatteryStatus.Value);
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }

    /// <summary>
    /// <c>bench/transcripts/r11-exclude-20260818-182614.jsonl</c> record 37
    /// (`EXC 2 04000000 05000000`): the regeneration's `Wait...` and
    /// `WB_Invalid` arrive BETWEEN two `Exclude` rows of the same listing. All
    /// five bands must still be in the mirror.
    /// </summary>
    [Fact]
    public void R11Exclude_Record37_ByteFaithful_TheInterleavedLinesDoNotLoseABand()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // Record 37's `raw`, byte for byte.
        var bytes =
            "\n\r\nExclude 00  02000   03000 \r\nExclude 01  05000   06000 \r\n"
            + "Exclude 02  04000   05000 \r\nWait...\r\nWB_Invalid\r\n"
            + "Exclude 04  12000   13000 \r\nExclude 05  14000   14000 \r\n\r\n\rHOP> \r\n\rHOP> ";

        var unhandled = new List<string>();
        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
            if (!parser.Parse(line).Handled) unhandled.Add(line);

        Assert.Empty(unhandled);
        Assert.Equal(
            [(0, "02000", "03000"), (1, "05000", "06000"), (2, "04000", "05000"),
             (4, "12000", "13000"), (5, "14000", "14000")],
            state.Hop.ExcludeBands!.Select(b => (b.Band, b.LowKHz, b.HighKHz)));
    }

    /// <summary>
    /// LEADING-SPACE LISTING ROWS —
    /// <c>bench/transcripts/r15-p3-wire-member-20260822-231055.jsonl</c>
    /// records 132 (<c> INDAD KC1HAS …</c>) and 134 (<c> NETAD HFN …</c>).
    /// Both carry ONE leading space (a stripped prompt prefix) and are `rx`
    /// LINE records, so they are replayed as the framer would deliver
    /// them. The trim at the top of <c>Parse</c> already removes the space;
    /// this is a regression pin on that, not a change.
    /// </summary>
    [Fact]
    public void R15P3_Records132And134_LeadingSpaceListingRows_StillLand()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // The `line` field of each record, with the framer's own CRLF put back.
        var bytes =
            " INDAD KC1HAS            CHGROUP 02   ASSOC SELF W6HOS\r\n"
            + "ALE>\r\n"
            + " NETAD HFN               CHGROUP 01   ASSOC SELF W6HOS1\r\n"
            + "NETAD HFL               CHGROUP 02   ASSOC SELF W6HOS\r\n";

        var unhandled = new List<string>();
        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
            if (!parser.Parse(line).Handled) unhandled.Add(line);

        Assert.Empty(unhandled);

        var individual = Assert.Single(state.Ale.IndividualAddresses);
        Assert.Equal("KC1HAS", individual.Address);
        Assert.Equal(2, individual.ChannelGroup);
        Assert.Equal(
            [("HFN", 1, "W6HOS1"), ("HFL", 2, "W6HOS")],
            state.Ale.NetAddresses.Select(n => (n.Address, n.ChannelGroup, n.AssociatedSelf)));
    }

    /// <summary>
    /// THE SENTINEL ANSWER WITH A LEADING SPACE — the same transcript's record
    /// 137 (<c> Battery Status FULL 26.2V</c>). A sentinel that did not parse
    /// would never complete a read operation, so this one row is worth its own
    /// pin.
    /// </summary>
    [Fact]
    public void R15P3_Record137_TheLeadingSpaceSentinelAnswer_ParsesAsBattery()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var data = Encoding.ASCII.GetBytes(" Battery Status FULL 26.2V\r\n");
        var results = new List<ParseResult>();
        foreach (var line in framer.Feed(data, data.Length)) results.Add(parser.Parse(line));

        var r = Assert.Single(results);
        Assert.True(r.Handled);
        Assert.Equal("BATTERY", r.Token);
        Assert.Equal("Status FULL 26.2V", state.BatteryStatus.Value);
    }

    /// <summary>
    /// ROUND 16 FIXES S3 — the `Bad Hopset` REFUSAL WINDOW, byte-faithful.
    ///
    /// <para>Verbatim from
    /// <c>bench/transcripts/r14-coupler-20260820-121753.jsonl</c> record 242
    /// (the <c>s5-rewrite</c> window-end line, the exactly-2-MHz span rewrite
    /// under coupler bypass — P-1 run A step S5). Its refusal line was
    /// UNRECOGNIZED until this round, so this window raised an "Unrecognized
    /// message" banner at the operator.</para>
    ///
    /// <para>The generation is SEEDED before the replay: the captured window
    /// opens after the probe's own <c>Generating Hopset...</c>, and what the
    /// fix claims is recognition PLUS ending a generation in progress.</para>
    /// </summary>
    [Fact]
    public void R14BadHopset_Record242_ByteFaithful_EndsGeneration_WithNothingUnrecognized()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        parser.Parse("Generating Hopset...");
        Assert.True(state.Hop.IsGeneratingHopset);

        // Record 242's `lines[0]`, byte for byte.
        var bytes = "Hopset 09  04000   06000 \r\n\r\n\rHOP> Wait...\r\nBad Hopset\r\n\r\n\rHOP> ";

        var unhandled = new List<string>();
        var data = Encoding.ASCII.GetBytes(bytes);
        foreach (var line in framer.Feed(data, data.Length))
            if (!parser.Parse(line).Handled) unhandled.Add(line);

        Assert.Empty(unhandled);
        Assert.False(state.Hop.IsGeneratingHopset);

        // Anti-vacuity: the window really carried the refusal AND the band
        // echo it refused, so a fixture trimmed to the prompts would fail.
        Assert.Contains("Bad Hopset", bytes, StringComparison.Ordinal);
        Assert.Equal("04000", state.Hop.Nets[9].WidebandLowKHz);
        Assert.Equal("06000", state.Hop.Nets[9].WidebandHighKHz);
        Assert.True(state.OperatingMode.IsConfirmed);
        Assert.Equal(OperatingMode.Hop, state.OperatingMode.Value);
    }

    /// <summary>
    /// THE ZEROIZE SETTLE WINDOW, byte-faithful (clone round 12; audit round 1
    /// finding 2 made it byte-faithful).
    ///
    /// <para>Verbatim from
    /// <c>bench/transcripts/r12-p1-20260818-222442.jsonl</c> — the eight
    /// bare-CR polls between <c>ZERO</c> and the returning prompt, chunked
    /// exactly as they arrived. Two things in this stream are NOT in any
    /// tidied fixture: a poll that answered a <b>BARE NUL</b>, and a
    /// ZEROIZE-COMPLETE banner terminated by <b>THREE BELS</b>. Left
    /// unhandled, the NULs framed into a line that matched no token and raised
    /// "Unrecognized message" at the operator, and the BELs rode into the
    /// banner's payload and out into the operator's own error text.</para>
    /// </summary>
    [Fact]
    public void R12ZeroizeSettle_ByteFaithful_RaisesNothingUnrecognized_AndKeepsTheBannerClean()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        string nul = ((char)0x00).ToString();
        string bel = ((char)0x07).ToString();

        // One string per captured chunk, in capture order (the three
        // NO-BYTES polls in between contribute nothing, as they did on the
        // wire).
        string[] chunks =
        [
            "\n*** ZEROIZING RAM -- PLEASE WAIT ***\r\n",
            nul,
            nul + "\r\n*** ZEROIZE COMPLETE ***" + bel + bel + bel + "\r\n",
            "\n",
            "\r\n\rSSB> ",
        ];

        var seen = new List<ParseResult>();
        foreach (var chunk in chunks)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
            {
                var r = parser.Parse(line);
                Assert.True(r.Handled, "Unhandled: '" + line + "'");
                Assert.Null(r.PayloadError);
                seen.Add(r);
            }
        }

        // Both banners arrived, and BOTH are banners — not one banner and one
        // mystery line.
        var banners = seen.Where(r => r.Token == "**").Select(r => r.RawPayload).ToList();
        Assert.Equal(["ZEROIZING RAM -- PLEASE WAIT", "ZEROIZE COMPLETE"], banners);

        // The BELs never reach a payload — this is what the operator is shown.
        Assert.All(banners, b => Assert.DoesNotContain(bel, b!, StringComparison.Ordinal));

        // Neither banner is the generic syntax reject, so neither may have
        // poisoned the ALE refusal mirror.
        Assert.Null(state.Ale.ProgrammingRefusal.Line);

        // …and the settle really did end at a prompt.
        Assert.Equal(OperatingMode.Ssb, state.OperatingMode.Value);
    }

    /// <summary>
    /// BYTE-FAITHFUL: <c>ZERO</c> sent at an <c>ALE&gt;</c> prompt, and the
    /// settle that follows it
    /// (<c>bench/transcripts/r12-zero-prompts-20260819-061052.jsonl</c>, step 5).
    ///
    /// <para><b>Why the capture exists.</b> The clone campaign's invariant 3
    /// says the FIRST wire act after the one confirm is <c>ZERO</c> —
    /// LITERALLY, so the campaign may not navigate to <c>SSB&gt;</c> first.
    /// That was only affordable if the radio ACCEPTS the wipe from any prompt,
    /// which this probe settled: it does, from <c>ALE&gt;</c> and from
    /// <c>HOP&gt;</c> alike.</para>
    ///
    /// <para><b>Why it is a PARSER fixture and not just a fact.</b> The
    /// ALE-context wipe interleaves three lines the SSB-context one never
    /// emits — <c>IN_PROG</c>, a bare prompt echo, and the fill-gate trailer
    /// <c>PRG 1-3 CHAR SLF</c> — each arriving PROMPT-PREFIXED on its own line.
    /// A client that raised "unrecognized message" for any of them would greet
    /// the operator with an error banner in the middle of the one operation
    /// that must not look like it went wrong.</para>
    /// </summary>
    [Fact]
    public void R12ZeroAtAnAlePrompt_ByteFaithful_ParsesWithoutAnUnrecognizedLine()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // The NUL and the three BELs are BUILT, never typed: they are
        // BYTES, and a source file that carried them literally would be one
        // editor away from losing them silently — and would make git treat
        // the whole file as binary, which costs every review its diff.
        string nul = ((char)0x00).ToString();
        string bel = ((char)0x07).ToString();

        // Verbatim, chunk by chunk as they arrived.
        string[] chunks =
        [
            "\nIN_PROG\r\n\r\n\rALE> \r\n\rALE> PRG 1-3 CHAR SLF\r\n\r\n\rALE> "
                + "*** ZEROIZING RAM -- PLEASE WAIT ***\r\n",
            nul,
            nul + "\r\n*** ZEROIZE COMPLETE ***" + bel + bel + bel + "\r\n",
            "\n",
            "\r\n\rSSB> ",
        ];

        var seen = new List<ParseResult>();
        foreach (var chunk in chunks)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
            {
                var r = parser.Parse(line);
                Assert.True(r.Handled, "Unhandled: '" + line + "'");
                Assert.Null(r.PayloadError);
                seen.Add(r);
            }
        }

        // The wipe's own banner arrived as a BANNER, and so did the completion.
        Assert.Equal(
            ["ZEROIZING RAM -- PLEASE WAIT", "ZEROIZE COMPLETE"],
            seen.Where(r => r.Token == "**").Select(r => r.RawPayload));

        // The ALE-context interleave did NOT poison the refusal mirror: none of
        // those three lines is an error, and the fill gate is a fill fact.
        Assert.Null(state.Ale.ProgrammingRefusal.Line);

        // …AND THE SETTLE LANDS AT `SSB>` FROM AN ALE-CONTEXT WIPE. That is the
        // RADIO's behaviour, not the campaign's navigation — it is what lets the
        // campaign send `ZERO` first and still find itself where its next leg
        // needs to be.
        Assert.Equal(OperatingMode.Ssb, state.OperatingMode.Value);
    }

    /// <summary>
    /// The same from a <c>HOP&gt;</c> prompt (same transcript, step 16), where
    /// the wipe adds NO interleave of its own — and the settle lands at
    /// <c>SSB&gt;</c> again.
    ///
    /// <para>The chunk opens with a bare <c>HOP&gt;</c>, and it is replayed
    /// here because the bytes arrived that way; it is NOT part of the wipe's
    /// answer. It is the TRAILING PROMPT of the <c>HO</c> that preceded it,
    /// arriving late. Reading it as the wipe's own would invite a demo to
    /// invent a line the radio never sent.</para>
    /// </summary>
    [Fact]
    public void R12ZeroAtAHopPrompt_ByteFaithful_AlsoSettlesAtTheSsbPrompt()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // The NUL and the three BELs are BUILT, never typed: they are
        // BYTES, and a source file that carried them literally would be one
        // editor away from losing them silently — and would make git treat
        // the whole file as binary, which costs every review its diff.
        string nul = ((char)0x00).ToString();
        string bel = ((char)0x07).ToString();

        string[] chunks =
        [
            "\r\n\rHOP> \n*** ZEROIZING RAM -- PLEASE WAIT ***\r\n",
            nul,
            nul + "\r\n*** ZEROIZE COMPLETE ***" + bel + bel + bel + "\r\n",
            "\n",
            "\r\n\rSSB> ",
        ];

        var seen = new List<ParseResult>();
        foreach (var chunk in chunks)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
            {
                var r = parser.Parse(line);
                Assert.True(r.Handled, "Unhandled: '" + line + "'");
                Assert.Null(r.PayloadError);
                seen.Add(r);
            }
        }

        Assert.Equal(
            ["ZEROIZING RAM -- PLEASE WAIT", "ZEROIZE COMPLETE"],
            seen.Where(r => r.Token == "**").Select(r => r.RawPayload));
        Assert.Equal(OperatingMode.Ssb, state.OperatingMode.Value);
    }

    /// <summary>
    /// THE ROUND-14 FIELD-CLONE REPLAY (plan-round14.md §4 Phase F1) — attempt
    /// 2 of the owner's first live clone, 2026-08-20 17:39:05.034 through
    /// 17:39:39.279, preserved as
    /// <c>bench/transcripts/field-clone-console-20260820-1738.txt</c>.
    ///
    /// <para><b>What this test DECIDES.</b> F1's plan splits on one question:
    /// does the framer/parser pair CONFIRM <c>HOP</c> from the live rig's
    /// mode-entry stream, or does it lose the prompt in the generate/tune
    /// lifecycle? This replay answers it: <b>the parser is NOT the defect</b>
    /// — every one of the window's 350 received lines is recognised, none
    /// carries a payload error, and the operating mode ends CONFIRMED at
    /// <c>Hop</c>. The defect is therefore the CAMPAIGN's prompt gate, and
    /// <c>CloneRound14FieldHardeningTests.TheHopLeg_…</c> is where it is
    /// convicted.</para>
    ///
    /// <para><b>LINE-SEQUENCE-FAITHFUL, not byte-faithful</b> (audit round 1,
    /// MAJOR — the earlier claim was wrong on both counts). A console log is a
    /// record of FRAMED LINES: it has no byte truth in it, so no fixture built
    /// from one can be byte-faithful. What this fixture IS, is every RX line of
    /// the window, complete and in capture order — the ALE entry with
    /// <c>IN_PROG</c> and <c>SCANNING</c>, the owner's manual <c>ST</c>
    /// (<c>KEY OFF</c> then <c>SCAN STOPPED</c>) BEFORE the <c>SH</c> whose
    /// answer the STRAY late <c>SCAN STOPPED</c> rides in front of, the whole
    /// SSB read burst including all 100 channel-dump rows, the eight
    /// <c>MODEM PRESET</c> rows and both lockout state blocks, the ALE
    /// re-entry, the full book read answered WHILE <c>SCANNING</c> (its
    /// <c>TXMSG</c> continuation text, all five <c>INDAD</c> rows and the
    /// <c>CHG 0</c>-<c>CHG 9</c> sweep — of which only groups 01 and 02 answer
    /// with a row, the other eight answering prompt-only), the two
    /// <c>SOUND</c> rows that arrive AFTER the <c>HO</c> and before the
    /// old-prompt battery answer, and finally the entry lifecycle that
    /// generates and tunes TWICE, both ending <c>TUNE FAULT</c>, before the
    /// prompt.</para>
    ///
    /// <para>The CHUNK shapes are patterned on the P-1 capture of the same
    /// lifecycle (<c>bench/transcripts/r14-coupler-20260820-121753.jsonl</c>,
    /// <c>mode-select-hop</c>): payload lines <c>CR LF</c>-terminated, prompts
    /// as <c>"\r\n\rXXX&gt;"</c>. The prompt's captured TRAILING space is not
    /// re-emitted, because the console already carries it as the LEADING space
    /// of the next line (which is why <c>" TUNING COUPLER "</c> and
    /// <c>" SCANNING"</c> are indented in the log and <c>"Wait..."</c> is
    /// not) — re-adding it would double it.</para>
    /// </summary>
    [Fact]
    public void R14FieldClone_Attempt2_LineSequenceFaithful_ConfirmsHop_AndTheScanMirrorTracks()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var seen = new List<string>();
        foreach (var line in Attempt2ReceivedLines)
        {
            // One chunk per captured line, in capture order.
            var chunk = IsPrompt(line) ? "\r\n\r" + line : line + "\r\n";
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var framed in framer.Feed(data, data.Length))
            {
                var r = parser.Parse(framed);
                Assert.True(r.Handled, "Unhandled: '" + framed + "'");
                Assert.Null(r.PayloadError);
                seen.Add(framed.Trim());
            }
        }

        // ---- ANTI-VACUITY: the fixture cannot silently shrink -------------
        // Every captured line framed to exactly one line, and the counts below
        // are the window's own inventory. A block deleted from the fixture
        // fails here rather than quietly narrowing what the replay proves.
        Assert.Equal(350, seen.Count);
        Assert.Equal(Attempt2ReceivedLines.Length, seen.Count);
        Assert.Equal(100, seen.Count(l => l.StartsWith("CH ", StringComparison.Ordinal)));
        Assert.Equal(8, seen.Count(l => l.StartsWith("MODEM PRESET ", StringComparison.Ordinal)));
        Assert.Equal(13, seen.Count(l => l.StartsWith("PROGRAM ", StringComparison.Ordinal)));
        Assert.Equal(9, seen.Count(l => l.StartsWith("SELECT ", StringComparison.Ordinal)));
        Assert.Equal(7, seen.Count(l => l.StartsWith("TXMSG ", StringComparison.Ordinal)));
        Assert.Equal(3, seen.Count(l => l.StartsWith("SLFAD ", StringComparison.Ordinal)));
        Assert.Equal(5, seen.Count(l => l.StartsWith("INDAD ", StringComparison.Ordinal)));
        Assert.Equal(2, seen.Count(l => l.StartsWith("NETAD ", StringComparison.Ordinal)));
        Assert.Equal(2, seen.Count(l => l.StartsWith("CHGROUP ", StringComparison.Ordinal)));
        Assert.Equal(2, seen.Count(l => l.StartsWith("SOUND ", StringComparison.Ordinal)));
        Assert.Equal(3, seen.Count(l => l == "SCANNING"));
        Assert.Equal(2, seen.Count(l => l == "SCAN STOPPED"));
        Assert.Equal(2, seen.Count(l => l == "TUNE FAULT"));
        Assert.Equal(2, seen.Count(l => l == "Generating Hopset..."));
        Assert.Equal(36, seen.Count(l => l == "ALE>"));
        Assert.Equal(29, seen.Count(l => l == "SSB>"));
        Assert.Equal(2, seen.Count(l => l == "HOP>"));

        // ---- SEQUENCE: the three orderings the window is interesting for --
        // (1) The owner's manual ST is answered at 17:39:08.027, BEFORE the
        //     17:39:12.644 SH — the fixture may not reorder them.
        int manualStop = seen.IndexOf("SCAN STOPPED");
        int stray = seen.IndexOf("SCAN STOPPED", manualStop + 1);
        int shBlock = seen.FindIndex(l => l.StartsWith("LSTN", StringComparison.Ordinal));
        Assert.True(manualStop < stray && stray < shBlock, "the two SCAN STOPPED lines are out of order");
        // (2) …and the STRAY one (17:39:12.688) arrives at the very HEAD of
        //     that SH answer, which is the shape worth remembering.
        Assert.Equal(shBlock - 1, stray);
        // (3) The two SOUND rows land AFTER the last SCANNING and BEFORE the
        //     HOP entry's first Wait — i.e. after the HO went out.
        int lastScanning = seen.FindLastIndex(l => l == "SCANNING");
        int firstSound = seen.FindIndex(l => l.StartsWith("SOUND ", StringComparison.Ordinal));
        int firstWait = seen.FindIndex(l => l == "Wait...");
        Assert.True(lastScanning < firstSound && firstSound < firstWait,
            "the post-HO SOUND rows are not where the capture puts them");

        // ---- THE VERDICT --------------------------------------------------
        // The mode is CONFIRMED Hop: the parser confirms straight through the
        // live-rig entry lifecycle, so F1's defect is not here.
        Assert.True(state.OperatingMode.IsConfirmed);
        Assert.Equal(OperatingMode.Hop, state.OperatingMode.Value);

        // …and the scan mirror F2 reads is real and tracked the announcements.
        // Last one wins: the ALE re-entry's SCANNING is the final word, which
        // nothing in the HOP entry un-says.
        Assert.True(state.Ale.LinkState.IsConfirmed);
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value);
    }

    private static bool IsPrompt(string line) => line is "SSB>" or "ALE>" or "HOP>";

    /// <summary>Every RX line of attempt 2's window, verbatim and in capture
    /// order (transcript lines 86-497). TX lines are the app's own sends and
    /// are not part of the received stream.</summary>
    private static readonly string[] Attempt2ReceivedLines =
    [
        "ALE_INST  rf5122",
        "ALE>",
        "ALE>",
        " IN_PROG",
        "ALE>",
        "ALE>",
        " SCANNING",
        "ALE>",
        "ALE>",
        " KEY OFF ",
        "SCAN STOPPED",
        "ALE>",
        " KEY OFF ",
        "ALE>",
        "SCAN STOPPED",
        "LSTN        ON  ",
        "KEY_TO_CALL ON  ",
        "RAD_SIL     OFF ",
        "ALL_CALL    ON  ",
        "ANY_CALL    ON  ",
        "MAXCH 020",
        "TUNETIME 010",
        "TIME_OUT 006",
        "AMD_DISPLAY ON  ",
        "CHAN 16 ",
        "MODE USB",
        "RxFr 05371500",
        "TxFr 05371500",
        "KEY OFF ",
        "MODEM OFF",
        "DV OFF",
        "DGT_SQUELCH OFF",
        "AVS OFF",
        "ENCRYPT OFF",
        "RWAS DISABLED",
        "ALE>",
        "Battery Status FULL 27.4V",
        "ALE>",
        "SSB>",
        "Battery Status FULL 27.4V",
        "SSB>",
        "CHAN 16 ",
        "KEY OFF ",
        "RxFr 05371500",
        "TxFr 05371500",
        "MODE USB",
        "AGC SLOW",
        "BAND 2.7 ",
        "RXONLY NO ",
        "BFO +0000",
        "MODEM OFF",
        "DV OFF",
        "DGT_SQUELCH OFF",
        "AVS OFF",
        "ENCRYPT OFF",
        "SQ_LEVEL MED ",
        "SQUELCH OFF",
        "POWER hi ",
        "ANTENNA   auto ",
        "CWOFFSET 0000",
        "RWAS DISABLED",
        "RETRANS DISABLED",
        "SSB>",
        "CHAN 16 ",
        "KEY OFF ",
        "RxFr 05371500",
        "TxFr 05371500",
        "MODE USB",
        "AGC SLOW",
        "BAND 2.7 ",
        "RXONLY NO ",
        "BFO +0000",
        "MODEM OFF",
        "DV OFF",
        "DGT_SQUELCH OFF",
        "AVS OFF",
        "ENCRYPT OFF",
        "SQ_LEVEL MED ",
        "SQUELCH OFF",
        "POWER hi ",
        "ANTENNA   auto ",
        "CWOFFSET 0000",
        "RWAS DISABLED",
        "RETRANS DISABLED",
        "SSB>",
        "UNKEY_M DISABLED",
        "SSB>",
        "Step 00000100",
        "SSB>",
        "RFG 100 ",
        "SSB>",
        "BEEP ON ",
        "SSB>",
        "FMSQUELCH ON ",
        "FMSQ_TYPE noise",
        "SSB>",
        "FMTONE ON ",
        "SSB>",
        "FMDEV 8.0",
        "SSB>",
        "PREPOST FILTER DISABLE",
        "SSB>",
        "PREPOST RXANTENNA DISABLE",
        "SSB>",
        "PREPOST SCAN FAST",
        "SSB>",
        "CONTRAST 04",
        "SSB>",
        "COMPRESS ON ",
        "SSB>",
        "Battery Status FULL 27.4V",
        "SSB>",
        "CH 00 RxFr 03967000 TxFr 03967000 MODE LSB AGC ME BA 2.7  RXONLY NO ",
        "CH 01 RxFr 51500000 TxFr 51500000 MODE FM  AGC SL BA 2.7  RXONLY NO ",
        "CH 02 RxFr 51000000 TxFr 51000000 MODE FM  AGC SL BA 2.7  RXONLY NO ",
        "CH 03 RxFr 50250000 TxFr 50250000 MODE USB AGC ME BA 2.7  RXONLY NO ",
        "CH 04 RxFr 28500000 TxFr 28500000 MODE USB AGC ME BA 2.7  RXONLY NO ",
        "CH 05 RxFr 29000000 TxFr 29000000 MODE USB AGC ME BA 2.7  RXONLY NO ",
        "CH 06 RxFr 29600000 TxFr 29600000 MODE FM  AGC SL BA 2.7  RXONLY NO ",
        "CH 07 RxFr 14296000 TxFr 14296000 MODE USB AGC ME BA 2.7  RXONLY NO ",
        "CH 08 RxFr 07296000 TxFr 07296000 MODE USB AGC ME BA 2.7  RXONLY NO ",
        "CH 09 RxFr 03996000 TxFr 03996000 MODE USB AGC FA BA 2.7  RXONLY NO ",
        "CH 10 RxFr 07200000 TxFr 07200000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 11 RxFr 01843000 TxFr 01843000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 12 RxFr 01996000 TxFr 01996000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 13 RxFr 03596000 TxFr 03596000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 14 RxFr 03996000 TxFr 03996000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 15 RxFr 05357000 TxFr 05357000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 16 RxFr 05371500 TxFr 05371500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 17 RxFr 07102000 TxFr 07102000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 18 RxFr 07296000 TxFr 07296000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 19 RxFr 10131000 TxFr 10131000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 20 RxFr 10145500 TxFr 10145500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 21 RxFr 14109000 TxFr 14109000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 22 RxFr 14346000 TxFr 14346000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 23 RxFr 18106000 TxFr 18106000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 24 RxFr 18117500 TxFr 18117500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 25 RxFr 21096000 TxFr 21096000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 26 RxFr 21432500 TxFr 21432500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 27 RxFr 24926000 TxFr 24926000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "Battery Status FULL 27.4V",
        "CH 28 RxFr 24932000 TxFr 24932000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 29 RxFr 28146000 TxFr 28146000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 30 RxFr 28312500 TxFr 28312500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 31 RxFr 50162500 TxFr 50162500 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 32 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 33 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 34 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 35 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 36 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 37 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 38 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 39 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 40 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 41 RxFr 07128000 TxFr 07128000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 42 RxFr 07131000 TxFr 07131000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 43 RxFr 07134000 TxFr 07134000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 44 RxFr 07137000 TxFr 07137000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 45 RxFr 07140000 TxFr 07140000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 46 RxFr 07143000 TxFr 07143000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 47 RxFr 07146000 TxFr 07146000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 48 RxFr 07149000 TxFr 07149000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 49 RxFr 07152000 TxFr 07152000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 50 RxFr 07155000 TxFr 07155000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 51 RxFr 07158000 TxFr 07158000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 52 RxFr 07161000 TxFr 07161000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 53 RxFr 07164000 TxFr 07164000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 54 RxFr 07167000 TxFr 07167000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 55 RxFr 07170000 TxFr 07170000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 56 RxFr 07173000 TxFr 07173000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 57 RxFr 07176000 TxFr 07176000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 58 RxFr 07179000 TxFr 07179000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 59 RxFr 07182000 TxFr 07182000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 60 RxFr 07185000 TxFr 07185000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 61 RxFr 07188000 TxFr 07188000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 62 RxFr 07191000 TxFr 07191000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 63 RxFr 07194000 TxFr 07194000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 64 RxFr 07197000 TxFr 07197000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 65 RxFr 07200000 TxFr 07200000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 66 RxFr 07204000 TxFr 07204000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 67 RxFr 07207000 TxFr 07207000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 68 RxFr 07210000 TxFr 07210000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 69 RxFr 07213000 TxFr 07213000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 70 RxFr 07216000 TxFr 07216000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 71 RxFr 07219000 TxFr 07219000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 72 RxFr 07222000 TxFr 07222000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 73 RxFr 07225000 TxFr 07225000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 74 RxFr 07228000 TxFr 07228000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 75 RxFr 07231000 TxFr 07231000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 76 RxFr 07234000 TxFr 07234000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 77 RxFr 07237000 TxFr 07237000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 78 RxFr 07240000 TxFr 07240000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 79 RxFr 07243000 TxFr 07243000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 80 RxFr 07246000 TxFr 07246000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 81 RxFr 07249000 TxFr 07249000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 82 RxFr 07252000 TxFr 07252000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 83 RxFr 07255000 TxFr 07255000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 84 RxFr 07258000 TxFr 07258000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 85 RxFr 07261000 TxFr 07261000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 86 RxFr 07264000 TxFr 07264000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 87 RxFr 07267000 TxFr 07267000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 88 RxFr 07270000 TxFr 07270000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 89 RxFr 07273000 TxFr 07273000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 90 RxFr 07276000 TxFr 07276000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 91 RxFr 07279000 TxFr 07279000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 92 RxFr 07282000 TxFr 07282000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 93 RxFr 07285000 TxFr 07285000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 94 RxFr 07288000 TxFr 07288000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 95 RxFr 07291000 TxFr 07291000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 96 RxFr 07294000 TxFr 07294000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 97 RxFr 07297000 TxFr 07297000 MODE LSB AGC SL BA 2.7  RXONLY NO ",
        "CH 98 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CH 99 RxFr 01600000 TxFr 01600000 MODE USB AGC SL BA 2.7  RXONLY NO ",
        "CHAN 16 ",
        "MODEM PRESET 1 DAT1 ASYNC REMOTE BAUD 2400  TYPE serial  INTER short   ",
        "SSB>",
        "MODEM PRESET 2 DAT2 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ",
        "SSB>",
        "MODEM PRESET 3 DAT3 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ",
        "SSB>",
        "MODEM PRESET 4 DAT4 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ",
        "SSB>",
        "MODEM PRESET 5 DAT5 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ",
        "SSB>",
        "MODEM PRESET 6 DAT6 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long    ",
        "SSB>",
        "Battery Status FULL 27.4V",
        "SSB>",
        "MODEM PRESET 0 DAT0 ASYNC DATA   BAUD 2400  TYPE serial  INTER short   ",
        "MODEM PRESET 1 DAT1 ASYNC REMOTE BAUD 2400  TYPE serial  INTER short   ",
        "SSB>",
        "Battery Status FULL 27.4V",
        "SSB>",
        ">>SSB_Programmable_Parameters",
        "PROGRAM CHAN UNLOCK",
        "PROGRAM FILL UNLOCK",
        "PROGRAM CFIG UNLOCK",
        "PROGRAM DATA UNLOCK",
        "PROGRAM KEYS UNLOCK",
        ">>HOP_Programmable_Parameters",
        "PROGRAM NET UNLOCK",
        "PROGRAM EXCLUDE UNLOCK",
        "PROGRAM TX_POWER UNLOCK",
        "PROGRAM DATA UNLOCK",
        ">>EAM_Programmable_Parameters",
        "PROGRAM ADDRESS UNLOCK",
        "PROGRAM CHGROUP UNLOCK",
        "PROGRAM CFIG UNLOCK",
        "PROGRAM LQA UNLOCK",
        "SSB>",
        ">>SSB_Selectable_Parameters",
        "SELECT DATA UNLOCK",
        "SELECT KEY UNLOCK",
        "SELECT MODE UNLOCK",
        "SELECT TMP_CHAN UNLOCK",
        "SELECT BFO UNLOCK",
        ">>HOP_Selectable_Parameters",
        "SELECT DATA UNLOCK",
        "SELECT KEY UNLOCK",
        ">>EAM_Selectable_Parameters",
        "SELECT DATA UNLOCK",
        "SELECT KEY UNLOCK",
        "SSB>",
        "Battery Status FULL 27.4V",
        "SSB>",
        "ALE_INST  rf5122",
        "ALE>",
        "ALE>",
        " IN_PROG",
        "ALE>",
        "ALE>",
        " Battery Status FULL 27.4V",
        "ALE>",
        " SCANNING",
        "ALE>",
        "SCANNING",
        "LSTN        ON  ",
        "KEY_TO_CALL ON  ",
        "RAD_SIL     OFF ",
        "ALL_CALL    ON  ",
        "ANY_CALL    ON  ",
        "MAXCH 020",
        "TUNETIME 010",
        "TIME_OUT 006",
        "AMD_DISPLAY ON  ",
        "Battery Status FULL 27.4V",
        "Battery Status FULL 27.4V",
        "ALE>",
        "TXMSG 00",
        "  COPY  ",
        "TXMSG 01",
        "  AFFIRMATIVE ",
        "TXMSG 02",
        "  NEGATIVE ",
        "TXMSG 03",
        "  THIS IS W6HOS  ",
        "TXMSG 04",
        "  GOODBYE  ",
        "TXMSG 05",
        "  RADIO CHECK ",
        "TXMSG 06",
        "  N7BOI DE W6HOS. COPY LAST. ",
        "ALE>",
        "Battery Status FULL 27.4V",
        "ALE>",
        "SLFAD HOS               CHGROUP 00",
        "SLFAD W6HOS             CHGROUP 02",
        "SLFAD W6HOS1            CHGROUP 01",
        "ALE>",
        "INDAD KI6EZA1           CHGROUP 02   ASSOC SELF W6HOS",
        "INDAD KC1HAS            CHGROUP 02   ASSOC SELF W6HOS",
        "INDAD KG6KMJ            CHGROUP 02   ASSOC SELF W6HOS",
        "INDAD N7BOI             CHGROUP 01   ASSOC SELF W6HOS1",
        "INDAD N5PWU             CHGROUP 01   ASSOC SELF W6HOS1",
        "ALE>",
        "NETAD HFN               CHGROUP 01   ASSOC SELF W6HOS1",
        "NETAD HFL               CHGROUP 02   ASSOC SELF W6HOS",
        "ALE>",
        "Battery Status FULL 27.4V",
        "ALE>",
        "ALE>",
        "CHGROUP 01 CHANS 11 13 17 19 21 23 25 27 29 ",
        "ALE>",
        "CHGROUP 02 CHANS 12 14 15 16 18 20 22 24 26 28 30 ",
        "ALE>",
        "ALE>",
        "ALE>",
        "ALE>",
        "ALE>",
        "ALE>",
        "ALE>",
        "ALE>",
        "Battery Status FULL 27.4V",
        "ALE>",
        "SOUND    W6HOS           INTERVAL 01:00 START TIME 17:30",
        "SOUND    W6HOS1          INTERVAL 01:00 START TIME 17:35",
        "ALE>",
        "Battery Status FULL 27.4V",
        "ALE>",
        "Wait...",
        "Generating Hopset...",
        " TUNING COUPLER ",
        "   TUNE FAULT   ",
        "Battery Status FULL 27.4V",
        "Wait...",
        "Generating Hopset...",
        " TUNING COUPLER ",
        "   TUNE FAULT   ",
        "HOP>",
        "HOP>",
    ];

    // ====================================================================
    // CLONE-FIELD ROUND 2 F9 — THE `HOP>` MODEM PRESET READ, BYTE-FAITHFUL
    // ====================================================================

    /// <summary>
    /// Probe P5's <c>HOP-pre-0</c> / <c>HOP-pre-7</c> / <c>HOP-pre-8</c> /
    /// <c>HOP-pre-9</c> windows replayed CHUNK FOR CHUNK out of
    /// <c>bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl</c> —
    /// the serial reader's own splits, mid-token and mid-line, exactly as they
    /// were recorded. Four <c>MODEM PRE n</c> reads at a <c>HOP&gt;</c> prompt.
    ///
    /// <para>What it proves: the SHORT preset line (no <c>TYPE</c>, no
    /// <c>INTER</c>) reaches the mirror. The round-8 discriminator required a
    /// TYPE token to be present and BEFORE nothing, so every 7-9 row was
    /// dropped as an uncaptured shape — which is why the clone campaign could
    /// never carry them. <c>MODEM PRE 0</c> stays out of the mirror at this
    /// prompt because the radio answers <c>INVALID MODEM PRESET</c>, not a
    /// row.</para>
    /// </summary>
    [Fact]
    public void P5HopPresetReads_ChunkFaithful_PutTheShortFormRowsInTheMirror()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // VERBATIM chunk sequence, in capture order, `raw` field for `raw` field.
        string[] chunks =
        [
            // HOP-pre-0 — `MODEM PRE 0` at `HOP>`
            "\nINV", "ALID MODEM PRESE", "T\r\n", "\r\n\rHOP> ",
            // HOP-pre-7
            "\n", "MODEM PRESET 7", " DAT7 ASYNC REM", "OTE BAUD 300   ", "\r\n\r\n\rHOP> ",
            // HOP-pre-8
            "\n", "M", "ODEM PRESET 8 D", "AT8 ASYNC REMOTE", " BAUD 300   \r\n\r", "\n\rHOP> ",
            // HOP-pre-9
            "\n", "MODEM PRESE", "T 9 DAT9 ASYNC R", "EMOTE BAUD 300 ", "  \r\n\r\n\rHOP> ",
        ];

        foreach (var chunk in chunks)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length)) parser.Parse(line);
        }

        // The three HOP presets, keyed and in order, "PRESET" stripped and the
        // column padding trimmed — the exact strings the clone campaign stores
        // as `CloneModemPreset.Fields` after its own leading-number split.
        Assert.Equal(
            ["7 DAT7 ASYNC REMOTE BAUD 300",
             "8 DAT8 ASYNC REMOTE BAUD 300",
             "9 DAT9 ASYNC REMOTE BAUD 300"],
            state.ModemPresets);

        // The prompt was confirmed by the same bytes — this really is a `HOP>`
        // read, not an SSB one that happened to parse.
        Assert.True(state.OperatingMode.IsConfirmed);
        Assert.Equal(OperatingMode.Hop, state.OperatingMode.Value);
    }

    // ====================================================================
    // ROUND 15 ITEM I — THE BARE-`STA` LQA LIFECYCLE, RECORD FOR RECORD
    // ====================================================================

    /// <summary>
    /// Probe P14c replayed out of
    /// <c>bench/transcripts/p14c-sounding-clean-20260822-132151.jsonl</c>:
    /// the <c>step</c> record at JSONL line 16 (the bare <c>SOU STA W6HOS</c>
    /// and the first progress line it drew) followed by every NON-EMPTY
    /// <c>listen.raw</c> record from line 26 through line 118 — thirteen chunks
    /// in capture order, `raw` field for `raw` field, the serial reader's own
    /// splits and the radio's own column padding. The two records the range
    /// skips are the mid-run `note` (a window summary) and the mid-run
    /// <c>SH</c> step, which is not an async chunk; the <c>LQA/SOUND</c> line
    /// that step captured has its own pin in <c>ResponseParserTests</c>.
    ///
    /// <para>What it proves: the WHOLE lifecycle, not one line of it. A bare
    /// <c>SOU STA</c> is a MINUTES-LONG transmission that walks every channel
    /// of the self's group from the top (30, 28, 26 … 12), tunes the coupler on
    /// the four channels with no recent tune, and ENDS on a bare
    /// <c>SCANNING</c> — after which the LQA slot must be empty, because the
    /// run it described is over.</para>
    ///
    /// <para>The chunk set is COUNT-ASSERTED BEFORE it is replayed (critic
    /// F74). Without that, a fixture edited down to three lines would still
    /// end <c>Scanning</c> with an empty slot and the test would pass while
    /// pinning nothing.</para>
    /// </summary>
    [Fact]
    public void P14cSoundingRun_RecordFaithful_WalksTheGroupAndEndsOnScanning()
    {
        // VERBATIM chunk sequence — JSONL line numbers in the comments.
        string[] chunks =
        [
            "\n\r\n\rALE> SOUNDING W6HOS            CHANNEL: 30\r\n",   // 16 (step, `SOU STA W6HOS`)
            "SOUNDING W6HOS            CHANNEL: 28\r\n",                // 26
            "SOUNDING W6HOS            CHANNEL: 26\r\n",                // 35
            "SOUNDING W6HOS            CHANNEL: 24\r\n",                // 46
            "SOUNDING W6HOS            CHANNEL: 22\r\n",                // 55
            "SOUNDING W6HOS            CHANNEL: 20\r\n",                // 63
            " TUNING COUPLER \r\n TUNE COMPLETE  \r\n",                 // 64
            "SOUNDING W6HOS            CHANNEL: 18\r\n TUNING COUPLER \r\n TUNE COMPLETE  \r\n",  // 73
            "SOUNDING W6HOS            CHANNEL: 16\r\n TUNING COUPLER \r\n TUNE COMPLETE  \r\n",  // 82
            "SOUNDING W6HOS            CHANNEL: 15\r\n",                // 91
            "SOUNDING W6HOS            CHANNEL: 14\r\n",                // 100
            "SOUNDING W6HOS            CHANNEL: 12\r\n TUNING COUPLER \r\n TUNE COMPLETE  \r\n",  // 109
            "SCANNING\r\n\r\n\rALE> ",                                  // 118
        ];

        // FIRST: the fixture really is the captured run (F74).
        int Occurrences(string needle) =>
            chunks.Sum(c => (c.Length - c.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length);

        Assert.Equal(13, chunks.Length);
        Assert.Equal(11, Occurrences("SOUNDING"));         // every channel of group 2
        Assert.Equal(4, Occurrences("TUNING COUPLER"));    // only where the tune had lapsed
        Assert.Equal(4, Occurrences("TUNE COMPLETE"));
        Assert.Equal(1, Occurrences("SCANNING"));          // the terminator, exactly once

        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var channels = new List<string>();
        var unhandled = new List<string>();

        foreach (var chunk in chunks)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
            {
                var r = parser.Parse(line);
                if (!r.Handled) unhandled.Add(line);

                // Mid-run invariants, checked at EVERY line: while the run is
                // on air the state is Sounding and the slot names this radio's
                // own self — and the SCHEDULE mirror is never written, by any
                // line of the lifecycle (it stays UNREAD: no read ran).
                Assert.Null(state.Ale.LqaSchedules);
                if (state.Ale.LinkState is { IsConfirmed: true, Value: AleLinkState.Sounding })
                {
                    Assert.Equal("W6HOS", state.Ale.LqaStation);
                    if (state.Ale.LqaChannel is { } ch && (channels.Count == 0 || channels[^1] != ch))
                        channels.Add(ch);
                }
            }
        }

        // The run walked group 2 from the top, in the radio's own order.
        Assert.Equal(
            ["30", "28", "26", "24", "22", "20", "18", "16", "15", "14", "12"],
            channels);

        // It ENDED: the bare SCANNING is the terminator, and the slot goes with
        // the run. (The tune flags are the coupler's, and they stay as the last
        // tune left them — the spine chip's business, not the banner's.)
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value);
        Assert.Null(state.Ale.LqaStation);
        Assert.Null(state.Ale.LqaChannel);
        Assert.True(state.IsTuneComplete);

        // Every line was either handled or is unrecognized BY DESIGN: this
        // capture's only such line is the bare `ALE>`-prefixed first one, which
        // the framer splits so the prompt is its own line — and prompts ARE
        // handled. So: nothing unrecognized at all.
        Assert.Empty(unhandled);

        // The prompt in the same bytes confirms the mode this ran in.
        Assert.True(state.OperatingMode.IsConfirmed);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }

    // ====================================================================
    // THE BROADCAST ROUND — P20 / P20b, 2026-08-23 (ANY/ALL calls and AMD)
    // ====================================================================
    // Every byte string below is a `raw` field copied verbatim from
    // bench/transcripts/p20-amd-broadcast-20260823-233550.jsonl or
    // bench/transcripts/p20b-any-with-channel-20260823-233951.jsonl. They
    // carry the PROMPT-GLUED forms the parser-level fixtures cannot show —
    // `ALE> TERMINATING LINK` and `ALE> SCANNING` arrive as one chunk, and
    // the framer is what splits them.

    /// <summary>
    /// P20b record 4 + its first listen window: `SCA` against the STICKY ALL
    /// link. The terminator arrives GLUED to the prompt
    /// (<c>\r\nALE&gt; TERMINATING LINK\r\n</c>) and the radio's own
    /// <c>SCANNING</c> follows ~2 s later — which is the line that clears the
    /// link state. Nothing may surface unrecognized.
    /// </summary>
    [Fact]
    public void P20b_ScaTerminatesTheStickyAllLink_ByteFaithful_TheScanningClearsIt()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // Record 3's `SH` answer put the link in the mirror in the first
        // place — the sticky ALL link read as the block's FIRST line, in the
        // seat `SCANNING` holds otherwise (it had survived two `ST`s AND a
        // serial-session close/reopen).
        var sh =
            "\n\r\n\rALE> LINKED ALL               CHANNEL: 29\r\n"
            + "LINKED ALL               CHANNEL: 29\r\nLSTN        ON  \r\nKEY_TO_CALL ON  \r\n"
            + "RAD_SIL     OFF \r\nALL_CALL    ON  \r\nANY_CALL    ON  \r\nMAXCH 020\r\n"
            + "TUNETIME 010\r\nTIME_OUT 006\r\nAMD_DISPLAY ON  \r\nKEY OFF \r\nCHAN 29 \r\n"
            + "MODE USB\r\nRxFr 28146000\r\nTxFr 28146000\r\nKEY OFF \r\nMODEM OFF\r\nDV OFF\r\n"
            + "DGT_SQUELCH OFF\r\nAVS OFF\r\nENCRYPT OFF\r\nRWAS DISABLED\r\n\r\n\rALE> ";
        // Record 4 (`SCA`) and the listen window behind it.
        var sca = "\n\r\n\rALE> TERMINATING LINK\r\n";
        var scanning = "SCANNING\r\n\r\n\rALE> ";

        var unhandled = new List<string>();
        void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
                if (!parser.Parse(line).Handled) unhandled.Add(line);
        }

        Feed(sh);
        Assert.Equal(AleLinkState.Linked, state.Ale.LinkState.Value);
        Assert.Equal("ALL", state.Ale.LinkedStation);
        Assert.Equal("29", state.Ale.LinkedChannel);   // from the SH line's OWN payload

        Feed(sca);
        Assert.Equal(AleLinkState.Linked, state.Ale.LinkState.Value);   // the terminator claims nothing

        Feed(scanning);
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value); // …the radio does

        Assert.Empty(unhandled);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }

    /// <summary>
    /// P20b records 18 + 52/53: `CAL ANY 12` — the CALLING line arrives with
    /// NO prompt behind it, the answer window runs ~69 s, and it ends
    /// <c>NO RESPONSE     </c> with <c>ALE&gt; SCANNING</c> glued into the same
    /// chunk. The `SE 9 ANY 12` twin (records 94 + 128) has the identical
    /// shape and is replayed with it.
    /// </summary>
    [Theory]
    [InlineData("\nCALLING  ANY              CHANNEL: 12\r\n", AleLinkState.Calling)]
    [InlineData("\nSENDING  ANY              CHANNEL: 12\r\n", AleLinkState.Sending)]
    public void P20b_AnyOnAChannel_ByteFaithful_TheWindowEndsNoResponse(
        string command, AleLinkState onAir)
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var unhandled = new List<string>();
        void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
                if (!parser.Parse(line).Handled) unhandled.Add(line);
        }

        Feed(command);
        Assert.Equal(onAir, state.Ale.LinkState.Value);
        Assert.Equal("ANY", state.Ale.LinkedStation);
        Assert.Equal("12", state.Ale.LinkedChannel);

        // The window's end, verbatim (record 52 for the call; record 128's
        // twin carries the trailing prompt in one chunk).
        Feed("NO RESPONSE     \r\n\r\n\rALE> SCANNING\r\n\r\n\rALE> ");
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value);

        Assert.Empty(unhandled);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }

    /// <summary>
    /// P20 records 27/28 + 38: the AUTO-channel broadcast AMD, `SE 9 ALL` —
    /// the radio picks the channel and announces it, ~20 s of TX, then a bare
    /// <c>SCANNING</c>. No link forms. The `SENDING` row needed NO parser
    /// change (plan §2.3); this is the confirmation pin through the existing
    /// path, at the byte level.
    /// </summary>
    [Fact]
    public void P20_SeAllAuto_ByteFaithful_TheSendingRowRidesTheExistingPath()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var unhandled = new List<string>();
        void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
                if (!parser.Parse(line).Handled) unhandled.Add(line);
        }

        Feed("\n");                                              // record 27's whole answer
        Feed("SENDING  ALL              CHANNEL: 29\r\n");       // record 28, +2 015 ms
        Assert.Equal(AleLinkState.Sending, state.Ale.LinkState.Value);
        Assert.Equal("ALL", state.Ale.LinkedStation);
        Assert.Equal("29", state.Ale.LinkedChannel);

        Feed("SCANNING\r\n\r\n\rALE> ");                          // record 38, +22 233 ms
        Assert.Equal(AleLinkState.Scanning, state.Ale.LinkState.Value);

        Assert.Empty(unhandled);
    }

    /// <summary>
    /// P20 records 67/68/76: `CAL ALL` (auto) — `SCAN STOPPED`, the CALLING
    /// row, then ` KEY OFF ` and the LINKED row carrying its OWN
    /// <c>CHANNEL: 29</c>, prompt-glued.
    /// </summary>
    [Fact]
    public void P20_CalAllAuto_ByteFaithful_TheLinkedRowCarriesItsOwnChannel()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        var unhandled = new List<string>();
        void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
                if (!parser.Parse(line).Handled) unhandled.Add(line);
        }

        Feed("\nSCAN STOPPED\r\n\r\n\rALE> ");                     // record 67
        Assert.Equal(AleLinkState.Stopped, state.Ale.LinkState.Value);

        Feed("CALLING  ALL              CHANNEL: 29\r\n");         // record 68, +2 006 ms
        Assert.Equal(AleLinkState.Calling, state.Ale.LinkState.Value);

        Feed("KEY OFF \r\nLINKED ALL               CHANNEL: 29\r\n\r\n\rALE> \r\n\rALE> ");
        Assert.Equal(AleLinkState.Linked, state.Ale.LinkState.Value);   // record 76, +18 180 ms
        Assert.Equal("ALL", state.Ale.LinkedStation);
        Assert.Equal("29", state.Ale.LinkedChannel);
        Assert.Equal(KeylineState.Off, state.Keyline.Value);

        Assert.Empty(unhandled);
    }

    /// <summary>
    /// P20 record 8: bare <c>POW</c> ANSWERS at an `ALE&gt;` prompt
    /// (<c>POWER low</c>) — the wire fact the connect ritual's third init
    /// query rests on, since the ALE `SH` block carries no POWER line at all.
    /// </summary>
    [Fact]
    public void P20_BarePowAnswersAtTheAlePrompt_ByteFaithful()
    {
        var state = new RadioState();
        var parser = new ResponseParser(state);
        var framer = new LineFramer();

        // Record 3's ALE `SH` block, whole — NONE of its lines is POWER,
        // which is the reason F1 exists.
        var sh =
            "\nIN_PROG\r\n\r\n\rALE> \r\n\rALE> IN_PROG\r\nLSTN        ON  \r\nSCANNING\r\n"
            + "KEY_TO_CALL ON  \r\nRAD_SIL     OFF \r\nALL_CALL    ON  \r\nANY_CALL    ON  \r\n"
            + "MAXCH 020\r\nTUNETIME 010\r\nTIME_OUT 006\r\nAMD_DISPLAY ON  \r\nCHAN 12 \r\n"
            + "MODE USB\r\nRxFr 01996000\r\nTxFr 01996000\r\nKEY OFF \r\nMODEM OFF\r\nDV OFF\r\n"
            + "DGT_SQUELCH OFF\r\nAVS OFF\r\nENCRYPT OFF\r\nRWAS DISABLED\r\n\r\n\rALE> ";

        var unhandled = new List<string>();
        void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in framer.Feed(data, data.Length))
                if (!parser.Parse(line).Handled) unhandled.Add(line);
        }

        Feed(sh);
        Assert.False(state.PowerLevel.IsConfirmed);   // the SH block said nothing about power

        Feed("\nPOWER low\r\n\r\n\rALE> ");            // record 8, the bare `POW` answer
        Assert.True(state.PowerLevel.IsConfirmed);
        Assert.Equal(PowerLevel.Low, state.PowerLevel.Value);

        Assert.Empty(unhandled);
        Assert.Equal(OperatingMode.Ale, state.OperatingMode.Value);
    }
}
