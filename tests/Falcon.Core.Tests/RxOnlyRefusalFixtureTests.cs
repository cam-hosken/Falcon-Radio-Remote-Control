using System.Text;
using System.Text.Json;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// ROUND 13 D1 (backlog item 3) — the RX-only keying refusal, end to end.
///
/// <para>Contract: <c>docs/protocol.md</c>, "The RX-only keying refusal
/// (CAPTURED 2026-08-19, r12-ptt transcript)", corrected the same day to
/// EDGE-TRIGGERED. Three clauses are pinned here, each against the evidence
/// that established it:</para>
/// <list type="number">
/// <item><b>One event per keyline EDGE.</b> ptt3 keyed the radio
/// ELECTRONICALLY (<c>K TRANSMIT</c>/<c>K RECEIVE</c> — no contacts, nothing
/// to bounce), twice, and produced exactly four refusals for four edges. Its
/// chunks are individually timestamped, so the replay below is faithful in
/// BYTES AND IN TIME: the same bytes, arriving at the same intervals, on a
/// clock the test drives from the transcript's own stamps.</item>
/// <item><b>Prompt-prefixed framing.</b> ptt1 and ptt2 caught the refusal glued
/// to a prompt on one line — <c>SSB&gt; ***RX Only***</c>. The tolerance for
/// that shape belongs to <c>LineFramer</c> (the prompt's own '&gt;' terminates
/// the line), so these fixtures ASSERT the split rather than re-implementing
/// it, and prove the refusal survives it intact.</item>
/// <item><b>Bounce tolerance.</b> Consecutive duplicates inside a short window
/// are ONE event. This half is SYNTHETIC BY NECESSITY and is labelled so: no
/// transcript captured a bounce pair byte-exactly — ptt3's two handset windows
/// recorded ZERO chunks — so the timings below are the test's, not the
/// radio's, and only the COLLAPSE RULE is being pinned, never a measured
/// interval.</item>
/// </list>
///
/// <para><b>And the async clause (plan §3.6):</b> the refusal is elicited by a
/// keyline edge, not by anything the app sent, so it must never complete, fail
/// or perturb the single-outstanding sentinel queue. Both transcript replays
/// assert that with a sentinel actually in flight.</para>
/// </summary>
public class RxOnlyRefusalFixtureTests
{
    private const string Ptt1 = "r12-ptt-20260819-224152.jsonl";
    private const string Ptt2 = "r12-ptt2-20260819-224845.jsonl";
    private const string Ptt3 = "r12-ptt3-20260819-225334.jsonl";

    // ======================================================================
    //  (a) BYTE-FAITHFUL TRANSCRIPT FIXTURES
    // ======================================================================

    /// <summary>
    /// ptt3, replayed chunk for chunk with the transcript's own arrival times:
    /// FOUR keyline edges (down, up, down, up) → FOUR refusal events, with no
    /// dedupe anywhere — the edges are 4–6 s apart and the bounce window is
    /// 500 ms.
    /// </summary>
    [Fact]
    public void Ptt3_FourElectronicKeyEdges_ProduceFourEvents_ByteAndTimeFaithful()
    {
        var chunks = TranscriptChunks(Ptt3);

        // Anti-vacuity, and the reason this transcript is the one: two of its
        // four refusals are SPLIT MID-TOKEN across chunk boundaries, which is
        // exactly the shape a line-oriented replay would never produce.
        Assert.Contains(chunks, c => c.Raw == "***RX On");
        Assert.Contains(chunks, c => c.Raw == "ly***\r\n");
        Assert.Contains(chunks, c => c.Raw == "*");
        Assert.Contains(chunks, c => c.Raw == "**RX Only***\r\n");

        using var h = new Harness();

        // A sentinel ON THE WIRE for the whole replay: if the refusal ever
        // paired with a pending command, this is where it would show.
        bool answered = false;
        h.Radio.Ping(() => answered = true);
        Assert.Equal(1, h.Radio.PendingPingCount);

        foreach (var chunk in chunks)
        {
            h.Clock.SetTo(chunk.Utc);
            h.Feed(chunk.Raw);
        }

        Assert.Equal(4, h.RxOnlyEvents);

        // §3.6: untouched, still owed, and still answerable afterwards.
        Assert.False(answered);
        Assert.Equal(1, h.Radio.PendingPingCount);
        Assert.Equal(0, h.Radio.PingAnswerDebt);
        h.Transport.InjectLine("Battery Status FULL 31.4V");
        Assert.True(answered);
        Assert.Equal(0, h.Radio.PendingPingCount);

        // §3.2: the operator's sentence carries no wire token. The raw line is
        // still in the Console feed (MessageReceived), which is where the
        // evidence belongs — it is simply not in the error text.
        Assert.All(h.Errors, e => Assert.DoesNotContain("RX Only", e, StringComparison.OrdinalIgnoreCase));
        Assert.All(h.Errors, e => Assert.DoesNotContain("*", e, StringComparison.Ordinal));
    }

    /// <summary>
    /// ptt1 and ptt2 — the PROMPT-PREFIXED framing. Their listen windows are
    /// single blobs with no per-line timing, so the clock here is the TEST'S
    /// (one second per framed line, comfortably outside the bounce window):
    /// what is pinned is the FRAMING and the RECOGNITION — one event per
    /// refusal LINE — not an interval the transcript never recorded.
    /// </summary>
    [Theory]
    [InlineData(Ptt1)]
    [InlineData(Ptt2)]
    public void PromptPrefixedRefusals_AreFramedApart_AndEachOneIsOneEvent(string transcript)
    {
        var blob = string.Concat(ListenBlobs(transcript));

        // Anti-vacuity: this transcript really carries the glued shape.
        Assert.Contains("SSB> ***RX Only***", blob, StringComparison.Ordinal);

        using var h = new Harness();

        bool answered = false;
        h.Radio.Ping(() => answered = true);

        // Fed as ONE byte run, exactly as the blob was captured; the framer
        // does the splitting.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.FeedFramedOneSecondApart(blob);

        // THE FRAMER SPLIT IT — asserted, not re-implemented. No emitted line
        // carries both the prompt and the refusal, the prompt is emitted on
        // its own, and the refusal arrives as a clean banner.
        Assert.DoesNotContain(h.Lines, l => l.Contains("SSB>", StringComparison.Ordinal)
                                         && l.Contains("RX Only", StringComparison.Ordinal));
        Assert.Contains(h.Lines, l => l.Trim() == "SSB>");
        Assert.Contains(h.Lines, l => l.Trim() == "***RX Only***");

        // One event per refusal LINE.
        int refusalLines = h.Lines.Count(l => l.Trim() == "***RX Only***");
        Assert.True(refusalLines >= 20, $"only {refusalLines} refusal lines replayed — the reader has drifted");
        Assert.Equal(refusalLines, h.RxOnlyEvents);

        // §3.6 again, on the other two transcripts.
        Assert.False(answered);
        Assert.Equal(1, h.Radio.PendingPingCount);
        Assert.Equal(0, h.Radio.PingAnswerDebt);
    }

    /// <summary>The parser's half of the contract, on the captured bytes: a
    /// prompt-prefixed refusal and a bare one both reach the consumer as the
    /// SAME banner, and neither is rebadged as the generic error.</summary>
    [Fact]
    public void TheCapturedSpellings_BothReachTheConsumerAsOneBanner()
    {
        using var h = new Harness();

        h.Feed("\n***RX Only***\r\n");
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Feed("\r\n\rSSB> ***RX Only***\r\n");

        Assert.Equal(2, h.RxOnlyEvents);
        Assert.Equal(2, h.Errors.Count);          // and NOTHING else was raised
    }

    /// <summary>
    /// A refusal keyed DURING SESSION INIT still reaches the operator.
    ///
    /// <para>The contract is one event per keyline EDGE with no session-start
    /// exception, and an operator holding the handset while the app connects
    /// is exactly the case that produces one. The <c>**</c> case suppresses
    /// banners while <c>Initializing</c> because the connect ritual's
    /// buffer-flush CRs turn stale bytes into rejected commands — but that
    /// flood is a property of the GENERIC arms, not of a keyline refusal,
    /// which no CR can elicit. So the RX-only arm runs ahead of the guard and
    /// the guard keeps every other banner class.</para>
    /// </summary>
    [Fact]
    public void ARefusalKeyedDuringSessionInit_StillReachesTheOperator()
    {
        using var h = new Harness(driveToReady: false);
        Assert.Equal(ConnectionState.Initializing, h.Radio.Connection);

        h.Feed("\n***RX Only***\r\n");
        Assert.Equal(1, h.RxOnlyEvents);

        // …and the init flood is STILL suppressed for every other banner.
        h.Feed("** ERROR **\r\n");
        h.Feed("** SOMETHING ELSE **\r\n");
        Assert.Equal(1, h.Errors.Count);

        // The session then reaches Ready normally, and the next edge shows.
        h.Transport.InjectLine("Battery Status FULL 31.4V");
        h.Transport.InjectLine("Battery Status FULL 31.4V");
        Assert.Equal(ConnectionState.Ready, h.Radio.Connection);

        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Feed("***RX Only***\r\n");
        Assert.Equal(2, h.RxOnlyEvents);

        // The bounce memory spans the init→Ready transition, because it is ONE
        // session: a duplicate of that edge is still swallowed.
        h.Clock.Advance(TimeSpan.FromMilliseconds(30));
        h.Feed("***RX Only***\r\n");
        Assert.Equal(2, h.RxOnlyEvents);
    }

    // ======================================================================
    //  (b) SYNTHETIC BOUNCE FIXTURES  — labelled synthetic, deliberately
    // ======================================================================
    //
    // WHY SYNTHETIC. The bounce claim comes from the OWNER'S OBSERVATION of a
    // handset ("sometimes key up sends twice"), and ptt3's two handset windows
    // captured ZERO chunks — so no transcript shows a bounce pair, and one
    // cannot be replayed. These fixtures therefore invent the timings and pin
    // only the RULE: duplicates inside the window are one event, duplicates
    // outside it are two. The window VALUE is ASSUMED tier (plan §9) and lives
    // in exactly one place, which is what the first assertion checks.

    [Fact]
    public void TheBounceWindow_IsTheOneTheContractNames()
    {
        Assert.Equal(500, Prc138Radio.RxOnlyBounceWindowMs);
        Assert.Equal("Channel is receive-only — transmit key refused.",
            Prc138Radio.RxOnlyRefusalMessage);
    }

    [Fact]
    public void Synthetic_TwoRefusals30msApart_AreOneEvent()
    {
        using var h = new Harness();

        h.Feed("***RX Only***\r\n");
        h.Clock.Advance(TimeSpan.FromMilliseconds(30));
        h.Feed("***RX Only***\r\n");

        Assert.Equal(1, h.RxOnlyEvents);
    }

    [Fact]
    public void Synthetic_TwoRefusals600msApart_AreTwoEvents()
    {
        using var h = new Harness();

        h.Feed("***RX Only***\r\n");
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        h.Feed("***RX Only***\r\n");

        Assert.Equal(2, h.RxOnlyEvents);
    }

    [Fact]
    public void Synthetic_AWholeBounceBurstCollapses_AndTheNextRealEdgeStillShows()
    {
        // The window SLIDES: a burst is a run of duplicates each close to the
        // one before it, so anchoring on the burst's FIRST member would let its
        // tail through as a second toast. Then a real edge, seconds later,
        // must still reach the operator — a dedupe that eats real events is
        // worse than none.
        using var h = new Harness();

        h.Feed("***RX Only***\r\n");
        for (int i = 0; i < 5; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(120));
            h.Feed("***RX Only***\r\n");
        }
        Assert.Equal(1, h.RxOnlyEvents);

        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Feed("***RX Only***\r\n");
        Assert.Equal(2, h.RxOnlyEvents);
    }

    [Fact]
    public void Synthetic_ADifferentBannerIsNeverSwallowedByTheRxOnlyWindow()
    {
        // The window is the RX-ONLY recognizer's, not the `**` case's: another
        // banner arriving inside it keeps its own generic handling, and does
        // not consume the refusal's slot either.
        using var h = new Harness();

        h.Feed("***RX Only***\r\n");
        h.Clock.Advance(TimeSpan.FromMilliseconds(30));
        h.Feed("** SOMETHING ELSE **\r\n");
        h.Clock.Advance(TimeSpan.FromMilliseconds(30));
        h.Feed("***RX Only***\r\n");

        Assert.Equal(1, h.RxOnlyEvents);                       // the pair collapsed
        Assert.Equal(2, h.Errors.Count);                       // the other banner still spoke
        Assert.Contains(h.Errors, e => e.Contains("SOMETHING ELSE", StringComparison.Ordinal));
    }

    // ======================================================================
    //  Harness, transcript reader, manual clock
    // ======================================================================

    /// <summary>Framer → transport → radio, with the errors collected and the
    /// clock in the test's hand. Built to Ready first: the <c>**</c> consumer
    /// deliberately stays silent while <c>Initializing</c> (the dirty-buffer
    /// flush turns stale bytes into rejected commands), so a fixture that
    /// skipped the ritual would be pinning nothing.</summary>
    private sealed class Harness : IDisposable
    {
        public readonly InjectingTransport Transport = new();
        public readonly ManualTimeProvider Clock = new();
        public readonly Prc138Radio Radio;
        public readonly List<string> Errors = [];
        public readonly List<string> Lines = [];

        private readonly LineFramer _framer = new();

        /// <param name="driveToReady">False leaves the session in
        /// <c>Initializing</c>, which is its own fixture: the banner case
        /// deliberately behaves differently there.</param>
        public Harness(bool driveToReady = true)
        {
            Radio = new Prc138Radio(Transport, new InlineContext(), Clock);
            Radio.ErrorOccurred += (_, e) => Errors.Add(e.Message);

            Radio.Connect(new PortSettings { PortName = "TEST" });
            if (!driveToReady)
            {
                Assert.Equal(ConnectionState.Initializing, Radio.Connection);
                return;
            }

            Transport.InjectLine("Battery Status FULL 31.4V");   // completes init
            Transport.InjectLine("Battery Status FULL 31.4V");   // drains the redundancy sentinel
            Assert.Equal(ConnectionState.Ready, Radio.Connection);
            Assert.Equal(0, Radio.PendingPingCount);

            Errors.Clear();
            Lines.Clear();
            Transport.ClearSent();
        }

        /// <summary>Feed raw bytes exactly as captured; every line the framer
        /// emits goes to the radio verbatim.</summary>
        public void Feed(string chunk)
        {
            var data = Encoding.ASCII.GetBytes(chunk);
            foreach (var line in _framer.Feed(data, data.Length))
            {
                Lines.Add(line);
                Transport.InjectLine(line);
            }
        }

        /// <summary>Same bytes, but the clock steps one second between framed
        /// lines — for the two transcripts that recorded no per-line timing.</summary>
        public void FeedFramedOneSecondApart(string blob)
        {
            var data = Encoding.ASCII.GetBytes(blob);
            foreach (var line in _framer.Feed(data, data.Length))
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Lines.Add(line);
                Transport.InjectLine(line);
            }
        }

        public int RxOnlyEvents => Errors.Count(e => e == Prc138Radio.RxOnlyRefusalMessage);

        public void Dispose() => Radio.Dispose();
    }

    private sealed record Chunk(string Raw, DateTimeOffset Utc);

    /// <summary>Every <c>kind="chunk"</c> record of a per-chunk-timestamped
    /// transcript, in arrival order, with its own UTC stamp.</summary>
    private static IReadOnlyList<Chunk> TranscriptChunks(string file)
    {
        var chunks = new List<Chunk>();
        foreach (var record in TranscriptRecords(file))
        {
            if (!record.TryGetProperty("kind", out var kind) || kind.GetString() != "chunk") continue;
            if (!record.TryGetProperty("raw", out var raw)) continue;
            chunks.Add(new Chunk(raw.GetString()!, record.GetProperty("utc").GetDateTimeOffset()));
        }
        Assert.True(chunks.Count >= 40, $"only {chunks.Count} chunks read from {file} — the reader has drifted");
        return chunks;
    }

    /// <summary>The raw text of every listen window in a blob-per-window
    /// transcript, in order.</summary>
    private static IReadOnlyList<string> ListenBlobs(string file)
    {
        var blobs = new List<string>();
        foreach (var record in TranscriptRecords(file))
        {
            if (!record.TryGetProperty("kind", out var kind) || kind.GetString() != "listen") continue;
            if (record.TryGetProperty("raw", out var raw) && raw.GetString() is { Length: > 0 } text)
                blobs.Add(text);
        }
        Assert.NotEmpty(blobs);
        return blobs;
    }

    private static IEnumerable<JsonElement> TranscriptRecords(string file)
    {
        var path = Path.Combine(FindRepoRoot(), "bench", "transcripts", file);
        Assert.True(File.Exists(path), "missing transcript fixture: " + path);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Trim().Length == 0) continue;
            yield return JsonDocument.Parse(line).RootElement.Clone();
        }
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

/// <summary>
/// A manual-advance <see cref="TimeProvider"/> for the Core suite — the
/// instrument that lets a 500 ms window be pinned without a 500 ms test.
/// <para>Added here rather than borrowed: the App suite's <c>TestTime</c> is
/// App-tests-only, and nothing in Falcon.Core.Tests had a clock fake before
/// round 13 D1.</para>
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    /// <summary>One tick = one <see cref="TimeSpan"/> tick, so
    /// <see cref="Advance"/> and <see cref="SetTo"/> are the same units the
    /// callers think in.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    /// <summary>Move the clock to a transcript's own recorded moment — what
    /// makes a replay faithful in TIME as well as in bytes.</summary>
    public void SetTo(DateTimeOffset moment) => _ticks = moment.UtcTicks;
}
