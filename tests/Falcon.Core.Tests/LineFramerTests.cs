using System.Text;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// Framing rules from docs/protocol.md "Physical / framing": CR, LF and '>'
/// terminate; the bare-LF RXONLY quirk; '>' terminates ONLY exact mode
/// prompts. Byte inputs are verbatim from the R1 capture (docs/probes.md).
/// </summary>
public class LineFramerTests
{
    private static string[] Feed(LineFramer framer, string data)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        return [.. framer.Feed(bytes, bytes.Length)];
    }

    /// <summary>The shortest promptless run that overflows. The cap is the
    /// largest buffer the framer HOLDS, so the drop fires one character later
    /// — and because the check runs after every appended character, a drop
    /// always takes exactly this many with it.</summary>
    private const int OverflowingRun = LineFramer.PendingCapBytes + 1;

    [Fact]
    public void CrTerminatedLineIsEmitted()
    {
        var framer = new LineFramer();
        var lines = Feed(framer, "POWER low\r");
        Assert.Single(lines);
        Assert.Equal("POWER low", lines[0]);
    }

    [Fact]
    public void CrLfProducesOneLineNotTwo()
    {
        var framer = new LineFramer();
        Assert.Single(Feed(framer, "CHAN 00 \r\n"));
    }

    [Fact]
    public void RxOnlyQuirk_BareLfTerminates()
    {
        // R1 capture, byte-faithful: "RXONLY NO <LF>" then "<CR>BFO +0000<CR><LF>".
        var framer = new LineFramer();
        var lines = Feed(framer, "RXONLY NO \nBFO +0000\r\n");
        Assert.Equal(2, lines.Length);
        Assert.Equal("RXONLY NO ", lines[0]);
        Assert.Equal("BFO +0000", lines[1]);
    }

    [Fact]
    public void PromptTerminates_GreaterThanKeptAsPartOfLine()
    {
        foreach (var prompt in new[] { "SSB> ", "ALE> ", "HOP> " })
        {
            var framer = new LineFramer();
            var lines = Feed(framer, prompt);
            Assert.Single(lines);
            Assert.Equal(prompt.TrimEnd(), lines[0]);
        }
    }

    [Fact]
    public void GreaterThanInsidePayload_DoesNotSplitTheLine()
    {
        // Stored AMD text may contain '>' — only exact "SSB>"/"ALE>"/"HOP>"
        // buffers terminate on it.
        var framer = new LineFramer();
        var lines = Feed(framer, "MEET AT >GRID< 0900\r");
        Assert.Single(lines);
        Assert.Equal("MEET AT >GRID< 0900", lines[0]);
    }

    [Fact]
    public void PromptTokenInsideLongerLine_DoesNotTerminate()
    {
        // "SSB>" appearing mid-payload (message text quoting a prompt).
        var framer = new LineFramer();
        var lines = Feed(framer, "SAW SSB> ON CONSOLE\r");
        Assert.Single(lines);
        Assert.Equal("SAW SSB> ON CONSOLE", lines[0]);
    }

    [Fact]
    public void PromptPrefixedAsyncLine_SplitsIntoPromptAndPayload()
    {
        // R7 capture: async lines arrive prompt-prefixed ("<CR>ALE> SCANNING<CR><LF>").
        var framer = new LineFramer();
        var lines = Feed(framer, "\rALE> SCANNING\r\n");
        Assert.Equal(2, lines.Length);
        Assert.Equal("ALE>", lines[0]);
        Assert.Equal(" SCANNING", lines[1]);
    }

    [Fact]
    public void PartialLineIsBufferedAcrossFeeds()
    {
        var framer = new LineFramer();
        Assert.Empty(Feed(framer, "TxFr 0160"));
        var lines = Feed(framer, "0000\r");
        Assert.Single(lines);
        Assert.Equal("TxFr 01600000", lines[0]);
    }

    [Fact]
    public void EmptyAndWhitespaceLinesAreDropped()
    {
        var framer = new LineFramer();
        var lines = Feed(framer, "\r\n\r\n  \r\nKEY OFF \r\n");
        Assert.Single(lines);
        Assert.Equal("KEY OFF ", lines[0]);
    }

    [Fact]
    public void MultipleLinesInOneFeed()
    {
        var framer = new LineFramer();
        Assert.Equal(3, Feed(framer, "RxFr 01600000\r\nTxFr 01600000\r\nMODE CW \r\n").Length);
    }

    [Fact]
    public void TriplePromptInterleave_R2Capture_FramesAllBlocks()
    {
        // The definitive anti-pairing capture (R2): one BAT ST in zeroized
        // ALE produced THREE prompt-terminated blocks. Byte-faithful.
        var framer = new LineFramer();
        var lines = Feed(framer,
            "\nIN_PROG\r\n\r\n\rALE> \r\n\rALE> PRG 1-3 CHAR SLF\r\n\r\n\rALE> Battery Status FULL 31.2V\r\n\r\n\rALE> ");
        Assert.Equal(
            ["IN_PROG", "ALE>", "ALE>", " PRG 1-3 CHAR SLF", "ALE>", " Battery Status FULL 31.2V", "ALE>"],
            lines);
    }

    [Fact]
    public void Reset_DropsBufferedPartial()
    {
        var framer = new LineFramer();
        Feed(framer, "PARTIAL");
        framer.Reset();
        var lines = Feed(framer, "KEY OFF \r");
        Assert.Single(lines);
        Assert.Equal("KEY OFF ", lines[0]);
    }

    // ---- The bounded buffer (round 14 Phase D, plan §4-D item 3) ------------
    // A stream that never terminates a line grew `_pending` without limit. The
    // contract the plan pins: cap at 64 KiB, drop the buffer WHOLE, move two
    // counters, and never fabricate a line.

    /// <summary>A prompt-less run twice the cap: nothing is emitted (a
    /// synthetic line would enter the parser on the same path real radio
    /// answers travel), the buffer is dropped whole, and the counters say so.</summary>
    [Fact]
    public void PromptlessFlood_DropsTheBufferWhole_AndEmitsNothing()
    {
        var framer = new LineFramer();
        var lines = Feed(framer, new string('X', 100 * 1024));

        Assert.Empty(lines);                                        // no synthetic line, ever
        Assert.Equal(1, framer.OverflowCount);
        Assert.Equal(OverflowingRun, framer.DroppedBytes);
    }

    /// <summary>The cap is HONOURED, measured on the only thing an outsider
    /// can measure: what is left in the buffer, flushed by a terminator. 100
    /// KiB in, one 64 KiB drop, so the residue is the remainder — and, above
    /// all, ≤ the cap.</summary>
    [Fact]
    public void TheCapIsHonoured_TheSurvivingBufferNeverExceedsIt()
    {
        var framer = new LineFramer();
        const int fed = 100 * 1024;
        Feed(framer, new string('X', fed));

        var flushed = Feed(framer, "\r");
        Assert.Single(flushed);
        Assert.True(flushed[0].Length <= LineFramer.PendingCapBytes,
            $"the buffer held {flushed[0].Length} chars — above the {LineFramer.PendingCapBytes} cap");
        Assert.Equal(fed - OverflowingRun, flushed[0].Length);
        // Conservation: every byte fed was either dropped or still buffered —
        // so the drop really took the WHOLE buffer, not a trimmed slice of it.
        Assert.Equal(fed, framer.DroppedBytes + flushed[0].Length);
    }

    /// <summary>
    /// THE EXACT BOUNDARY, both sides (audit round 1, MINOR 1). The cap is
    /// the largest buffer the framer HOLDS — a run of exactly 65,536 is kept
    /// and still frames — and the drop fires on the character that would push
    /// it past. The first cut compared <c>&gt;=</c> and dropped AT the cap,
    /// one byte earlier than the plan and the architecture doc describe; no
    /// test could tell, because every other pin here feeds 100 KiB and lands
    /// far from the edge.
    /// </summary>
    [Fact]
    public void TheCapBoundary_HoldsExactlyTheCap_AndDropsOneByteLater()
    {
        var atCap = new LineFramer();
        Assert.Empty(Feed(atCap, new string('X', LineFramer.PendingCapBytes)));
        Assert.Equal(0, atCap.OverflowCount);
        Assert.Equal(0, atCap.DroppedBytes);

        // …and it was really HELD, not quietly lost: it frames whole.
        var flushed = Feed(atCap, "\r");
        Assert.Single(flushed);
        Assert.Equal(LineFramer.PendingCapBytes, flushed[0].Length);

        var overCap = new LineFramer();
        Assert.Empty(Feed(overCap, new string('X', OverflowingRun)));
        Assert.Equal(1, overCap.OverflowCount);
        Assert.Equal(OverflowingRun, overCap.DroppedBytes);

        // The buffer went WHOLE — there is nothing left to flush.
        Assert.Empty(Feed(overCap, "\r"));
    }

    /// <summary>Framing SURVIVES a drop. The line the overflow lands in is
    /// garbled — accepted, and why the cap sits far above any real answer —
    /// but every line after it frames exactly as before, prompts included.</summary>
    [Fact]
    public void FramingRecovers_AcrossAnOverflowDrop()
    {
        var framer = new LineFramer();
        Feed(framer, new string('X', 100 * 1024));
        Assert.Equal(1, framer.OverflowCount);

        var lines = Feed(framer, "\r\nBattery Status FULL 31.4V\r\n\r\rSSB> ");
        Assert.Equal(3, lines.Length);
        Assert.Equal(100 * 1024 - OverflowingRun, lines[0].Length);   // the garbled residue
        Assert.Equal("Battery Status FULL 31.4V", lines[1]);
        Assert.Equal("SSB>", lines[2]);
        Assert.Equal(1, framer.OverflowCount);                                    // and no further drop
    }

    /// <summary>Below the cap NOTHING changes: a long partial spanning many
    /// feeds still accumulates and still emits whole, counters untouched.</summary>
    [Fact]
    public void BelowTheCap_TheBufferStillAccumulatesAcrossFeeds()
    {
        var framer = new LineFramer();
        const int chunk = 1024;
        int chunks = (LineFramer.PendingCapBytes / chunk) - 1;      // one chunk shy of the cap

        for (int i = 0; i < chunks; i++)
            Assert.Empty(Feed(framer, new string('X', chunk)));

        var lines = Feed(framer, "\r");
        Assert.Single(lines);
        Assert.Equal(chunks * chunk, lines[0].Length);
        Assert.Equal(0, framer.OverflowCount);
        Assert.Equal(0, framer.DroppedBytes);
    }

    /// <summary>The counters are a RECORD, not session state: a reset drops
    /// the partial and leaves the tally standing. Pinned because it is a
    /// decision — bytes were lost, and a reconnect is not a reason to forget
    /// that.</summary>
    [Fact]
    public void Reset_DropsThePartial_ButNotTheOverflowTally()
    {
        var framer = new LineFramer();
        Feed(framer, new string('X', 100 * 1024));
        framer.Reset();

        Assert.Equal(1, framer.OverflowCount);
        Assert.Equal(OverflowingRun, framer.DroppedBytes);
        var lines = Feed(framer, "KEY OFF \r");
        Assert.Single(lines);
        Assert.Equal("KEY OFF ", lines[0]);
    }

    // ---- The resumed prompt scan -------------------------------------------
    // The '>' check used to trim the WHOLE buffer on every '>' byte. It now
    // resumes from a remembered position, which makes RESETTING that position
    // load-bearing: a stale one parked past a new line's leading blanks stops
    // the very next prompt from framing. Both reset paths are pinned.

    [Fact]
    public void TheScanPosition_ResetsWhenTheBufferIsConsumed()
    {
        // The first line advances the scan (a '>' inside payload, after a
        // leading blank), then is consumed by CR. A prompt arriving next with
        // NO leading blank is only recognised if the position went back to 0.
        var framer = new LineFramer();
        Assert.Single(Feed(framer, "  A>B\r"));

        var lines = Feed(framer, "SSB>");
        Assert.Single(lines);
        Assert.Equal("SSB>", lines[0]);
    }

    [Fact]
    public void TheScanPosition_ResetsWhenTheBufferIsDropped()
    {
        // Same pin on the overflow path: the run starts with a blank and a
        // '>', so the position parks at 1, and the drop must take it with it.
        var framer = new LineFramer();
        Feed(framer, " >" + new string('X', OverflowingRun - 2));
        Assert.Equal(1, framer.OverflowCount);

        var lines = Feed(framer, "HOP>");
        Assert.Single(lines);
        Assert.Equal("HOP>", lines[0]);
    }

    /// <summary>
    /// THE THIRD RESET PATH — <see cref="LineFramer.Reset"/> itself (audit
    /// round 1, MINOR 2: the auditor deleted <c>_scanFrom = 0</c> from Reset
    /// and all twenty framer tests stayed green).
    ///
    /// <para>This is the reconnect scenario, and it is the one that would
    /// hurt: <c>SerialTransport</c> resets the framer on every open and close,
    /// so a partial line abandoned by the OLD session leaves the scan position
    /// parked somewhere out in it. The very next thing the NEW session needs to
    /// frame is a prompt — the connect ritual waits on one — and a stale
    /// position past the prompt's own '>' makes the framer miss it silently.
    /// The session would then sit there waiting for a prompt that already
    /// arrived.</para>
    /// </summary>
    [Fact]
    public void Reset_ReturnsTheScanPosition_SoTheFirstPromptAfterItStillFrames()
    {
        var framer = new LineFramer();

        // A partial from the old session: the '>' drives the scan past six
        // blanks and parks it on the 'X' at index 6.
        Assert.Empty(Feed(framer, "      X>partial"));
        framer.Reset();

        // The new session's first prompt is four characters long — its '>'
        // falls at index 3, BEFORE that stale position.
        var lines = Feed(framer, "SSB>");
        Assert.Single(lines);
        Assert.Equal("SSB>", lines[0]);
    }

    [Fact]
    public void TheResumedScan_StillMatchesTheTrimRule_ForPaddedAndEmbeddedPrompts()
    {
        // The position trick is only sound if it agrees with the old
        // `Trim() is "SSB>"` on every shape: leading blanks yes, anything
        // before the prompt no, an interior blank no.
        var framer = new LineFramer();
        Assert.Equal(["   ALE>"], Feed(framer, "   ALE>"));

        Assert.Empty(Feed(framer, "X ALE>"));                  // not a bare prompt
        Assert.Equal(["X ALE>"], Feed(framer, "\r"));

        Assert.Empty(Feed(framer, " AL E>"));                  // interior blank
        Assert.Equal([" AL E>"], Feed(framer, "\r"));
    }
}
