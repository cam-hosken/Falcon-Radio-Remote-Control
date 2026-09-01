using System.Text.RegularExpressions;

namespace Falcon.Core.Tests.Transport;

/// <summary>
/// ROUND 14 PHASE D — SOURCE-LEVEL pins on the two transport repairs that no
/// host test can observe from the outside. Same idiom, and the same reasoning,
/// as <c>AndroidTeardownSourceGuardTests</c>: a string pin cannot prove a
/// mechanism is CORRECT, but it can prove it has not been quietly deleted or
/// refactored away — the realistic failure mode for code whose behaviour only
/// a cable-pull can exercise.
///
/// <para><b>Why source pins here.</b> Both repairs are invisible to the suite.
/// <see cref="Falcon.Core.Transport.WindowsSerialPort"/> is sealed and news up
/// a real <c>SerialPort</c>, so no fake can be injected into the yank path —
/// and the plan's test-design ruling (§4-D, critic F15) is that NO adapter or
/// factory is introduced for testability, because the churn is out of
/// proportion to the fix. The handle-leak repair is therefore verified in two
/// halves: the SHAPE here, and the BEHAVIOUR by the owner's recorded manual
/// yank check (pull → clean Failed → manual reconnect, and no handle growth
/// across three cycles). The <c>ConfigureAwait</c> discipline is a whole-file
/// RULE rather than a single call, which is a scan by nature.</para>
///
/// <para><b>They scan CODE, not text.</b> Every scan runs on
/// <see cref="Scannable"/> — comments and string literals stripped (the
/// stripper is <c>GuiOutScopeGuardTests</c>'s, same assembly, already
/// self-pinned there) — so the D2 lesson holds: a guard that a commented-out
/// line can satisfy is worse than no guard, because it reports safety. The
/// await scanner gets its own self-pin below for the same reason.</para>
/// </summary>
public class TransportSourceGuardTests
{
    private static readonly string TransportDir = Path.Combine("src", "Falcon.Core", "Transport");

    private const string WindowsPortFile = "WindowsSerialPort.cs";
    private const string SerialTransportFile = "SerialTransport.cs";

    /// <summary>Every C# file in the transport folder — the plan's scope for
    /// the <c>ConfigureAwait</c> rule is the FOLDER, not a named list, so a
    /// file added tomorrow is covered without anyone remembering to add
    /// it.</summary>
    private static IReadOnlyList<string> TransportSources()
    {
        var dir = Path.Combine(RepoRoot(), TransportDir);
        Assert.True(Directory.Exists(dir), "the transport folder moved: " + dir);

        var files = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Anti-vacuity: an empty (or renamed-away) folder would make every
        // Assert.Empty below pass while scanning nothing at all.
        Assert.True(files.Count >= 5, $"only {files.Count} transport sources found — the scan has drifted");
        foreach (var expected in new[] { WindowsPortFile, SerialTransportFile, "LineFramer.cs", "ISerialPort.cs" })
            Assert.Contains(files, f => Path.GetFileName(f) == expected);

        return files;
    }

    /// <summary>One transport source with comments and literals removed —
    /// what the compiler would see, near enough for a shape pin.</summary>
    private static string Scannable(string file)
    {
        var raw = File.ReadAllText(file);
        var code = GuiOutScopeGuardTests.DecodeUnicodeEscapes(
            GuiOutScopeGuardTests.StripCommentsAndLiterals(raw));

        // Checked on EVERY scan rather than once: a stripper that returned ""
        // would fail the Assert.Contains pins loudly, but would make every
        // Assert.Empty pass SILENTLY — the dangerous direction.
        Assert.True(code.Length > 200,
            $"{Path.GetFileName(file)} stripped to {code.Length} chars — the stripper has eaten the file");
        Assert.Contains("namespace Falcon.Core.Transport;", code, StringComparison.Ordinal);
        return code;
    }

    // =====================================================================
    // GUARD 1 — the ConfigureAwait(false) discipline (plan §4-D item 2).
    // =====================================================================

    /// <summary>
    /// EVERY await in the transport sources continues off the captured
    /// context.
    ///
    /// <para><b>Why this is the fix, and why it is a rule rather than a
    /// patch.</b> The plan keeps the public SYNC <c>Open</c>/<c>Close</c> API
    /// and its three pinned contracts (§4-D, critic F14), which means a caller
    /// on the UI thread genuinely blocks on a task — <c>SerialTransport</c>'s
    /// bounded sync-over-async sites, reached under
    /// <c>BaudChangeFlow</c>'s lock. A bounded wait like that cannot deadlock
    /// so long as nothing underneath it wants the thread back. One
    /// <c>ConfigureAwait</c>-less await anywhere below the blocking call is
    /// enough to want it back — so the property has to hold for the whole
    /// tree, and only a scan can say that it does.</para>
    ///
    /// <para>Two REAL offenders existed when this guard was written, both in
    /// <c>CloseAsync</c> (the cancel and the bounded close race), both on the
    /// exact path a cable-pull teardown takes.</para>
    /// </summary>
    [Fact]
    public void EveryAwait_InTheTransportSources_ContinuesOffTheCapturedContext()
    {
        var offenders = new List<string>();
        int scanned = 0;

        foreach (var file in TransportSources())
        {
            var code = Scannable(file);
            foreach (var (index, segment) in AwaitExpressions(code))
            {
                scanned++;
                if (segment.Contains("ConfigureAwait(false)", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)} ≈line {LineOf(code, index)}: {Snippet(segment)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these awaits do not carry ConfigureAwait(false) — a blocking caller above them "
            + "(SerialTransport's retained sync API, reached under BaudChangeFlow's lock) can be "
            + "deadlocked by a continuation that wants the calling thread back:\n  "
            + string.Join("\n  ", offenders));

        // Anti-vacuity: the scan really found awaits to check.
        Assert.True(scanned >= 6, $"only {scanned} awaits seen across the transport sources — the scan has gone blind");
    }

    /// <summary>
    /// The scanner is load-bearing for the pin above, so it is pinned as a
    /// unit rather than trusted — the Android guard's own self-pin, and the
    /// same reasoning.
    /// </summary>
    [Fact]
    public void TheAwaitScanner_SeesRealAwaits_IgnoresDecoys_AndCannotBorrowAnotherAwaitsCall()
    {
        const string sample = """
            // await InAComment();
            /* await InABlockComment(); */
            var s = "await InAString();";
            await Good().ConfigureAwait(false);
            await Bare();
            if (await Raced().ConfigureAwait(false) != x) { Ignore(); }
            Foo(await Inner(), await Outer().ConfigureAwait(false));
            var awaitable = NotAwait();
            """;

        var code = GuiOutScopeGuardTests.StripCommentsAndLiterals(sample);
        var found = AwaitExpressions(code).ToList();

        // POSITIVE half — without it a scanner that matched nothing would pass
        // the whole guard above silently.
        Assert.Equal(5, found.Count);
        Assert.Contains(found, f => f.Segment.Contains("Good()", StringComparison.Ordinal));

        // NEGATIVE half — the three decoys are gone, and `awaitable` is not an
        // await token.
        foreach (var decoy in new[] { "InAComment", "InABlockComment", "InAString", "NotAwait" })
            Assert.DoesNotContain(found, f => f.Segment.Contains(decoy, StringComparison.Ordinal));

        // …and the verdicts themselves: exactly the two bare awaits are
        // reported. `Inner` is the one that matters — its statement DOES
        // contain a ConfigureAwait, belonging to `Outer`, and a segment that
        // ran to the semicolon would happily borrow it.
        var bare = found
            .Where(f => !f.Segment.Contains("ConfigureAwait(false)", StringComparison.Ordinal))
            .Select(f => Snippet(f.Segment))
            .ToList();
        // (`Inner`'s segment stops ON the sibling await, comma and all — the
        // bound is positional, and that is precisely what makes it unable to
        // reach the ConfigureAwait further along the statement.)
        Assert.Equal(["await Bare()", "await Inner(),"], bare);
    }

    /// <summary>
    /// The blocking sites are the RETAINED ones and no more (plan §4-D item 2:
    /// "no new <c>.Result</c>/<c>.Wait()</c> beyond the retained bounded-close
    /// shape").
    ///
    /// <para>The plan's architecture decision keeps the synchronous API, so
    /// this cannot be a ban — it is an INVENTORY, which is the honest form of
    /// "no new ones": adding a sync-over-async wait anywhere in the transport
    /// fails here and has to be argued for rather than slipped in.</para>
    ///
    /// <para>Deliberately NOT counted: <c>_promptSeen.Wait(…)</c> in the
    /// writer loop. That is a <c>ManualResetEventSlim</c> — the prompt gate
    /// itself, waiting on a signal with a deadline — not a task wait, and it
    /// has nothing to do with the deadlock class this phase closes.</para>
    /// </summary>
    [Fact]
    public void TheBlockingSites_AreExactlyTheRetainedSyncOverAsyncShape()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in TransportSources())
        {
            var code = Scannable(file);
            counts[Path.GetFileName(file)] = CountOf(code, "GetAwaiter().GetResult()");

            // A bare `.Result` or `.Wait()` is a blocking wait with no bound at
            // all — the shape the retained sites exist to avoid.
            Assert.False(Regex.IsMatch(code, @"\.Result(?![A-Za-z0-9_])"),
                Path.GetFileName(file) + " blocks on .Result");
            Assert.DoesNotContain(".Wait()", code, StringComparison.Ordinal);
        }

        // SerialTransport's five: the port open, the bounded close's race and
        // its fast-failure observation, the writer's write, and Dispose's port
        // disposal. They are the SYNC API the plan retains.
        Assert.Equal(5, counts[SerialTransportFile]);

        // …and nothing else in the transport blocks on a task at all.
        foreach (var (file, n) in counts)
        {
            if (file == SerialTransportFile) continue;
            Assert.True(n == 0, $"{file} has acquired {n} sync-over-async wait(s) — the plan permits none outside {SerialTransportFile}");
        }
    }

    // =====================================================================
    // GUARD 2 — the yank teardown routes through the dispose belt (item 1).
    // =====================================================================

    /// <summary>
    /// THE HANDLE LEAK, pinned shut. <c>RaiseDisconnect</c> used to clear
    /// <c>_port</c> and nothing else, so every cable-pull orphaned an open
    /// <c>SerialPort</c> — handle, internal reader thread and BaseStream —
    /// with no way for anyone else to reach it, because clearing the field is
    /// exactly what makes a later <c>CloseAsync</c> a no-op.
    ///
    /// <para>ORDER is pinned, not just presence, because the order carries
    /// three separate promises and every one of them is invisible to a
    /// presence check: the interlocked SINGLE-FIRE still guards the whole body;
    /// the port is CAPTURED before the field is cleared (a clear-first body
    /// leaks again, silently); and <c>IsOpen</c> is false BEFORE the event, the
    /// contract the method's own summary makes to subscribers.</para>
    /// </summary>
    [Fact]
    public void RaiseDisconnect_RoutesTheDroppedPort_ThroughTheDisposeBelt_InThatOrder()
    {
        var body = RaiseDisconnectBody(out var code);

        int latch = IndexIn(body, "if (Interlocked.Exchange(ref _disconnectFired, 1) != 0) return;", "RaiseDisconnect");
        int capture = IndexIn(body, "var port = _port;", "RaiseDisconnect");
        int clear = IndexIn(body, "_port = null;", "RaiseDisconnect");
        int belt = IndexIn(body, "RunDisposeBeltAsync(port)", "RaiseDisconnect");
        int raised = IndexIn(body, "Disconnected?.Invoke(", "RaiseDisconnect");

        Assert.True(latch < capture,
            "the interlocked single-fire must still guard the whole body — a second yank must not run a second belt");
        Assert.True(capture < clear,
            "the port must be CAPTURED before _port is cleared, or the dropped handle has no owner and leaks again");
        Assert.True(clear < belt && belt < raised,
            "_port must be cleared (IsOpen false) before the belt starts and before subscribers see the event");

        // The belt is the SAME one a deliberate close uses — that is what
        // "routes through the CloseAsync dispose belt" means. Three mentions:
        // the declaration, CloseAsync's call, and this one.
        Assert.Equal(3, CountOf(code, "RunDisposeBeltAsync"));
        Assert.True(IndexOf(code, "var closeTask = RunDisposeBeltAsync(port);")
                  < IndexOf(code, "Task.WhenAny(closeTask"),
            "CloseAsync must still build its bounded race around the belt");
    }

    /// <summary>
    /// The belt itself: BaseStream first, and that is not cosmetic.
    /// <c>SerialPort.Close()</c> on Windows deadlocks when a
    /// <c>BaseStream.ReadAsync</c> is pending — Close waits for the internal
    /// reader thread, parked in native I/O the token cannot unblock — so
    /// disposing the stream first is what faults the read out and lets Close
    /// return (provenance note 2). Reordering these three lines re-arms the
    /// deadlock the class was lifted with; nothing else would notice.
    /// </summary>
    [Fact]
    public void TheDisposeBelt_DisposesTheBaseStream_BeforeCloseAndDispose()
    {
        var code = Scannable(Path.Combine(RepoRoot(), TransportDir, WindowsPortFile));

        int declaration = IndexOf(code, "private static Task RunDisposeBeltAsync(SerialPort port)");
        var belt = code[declaration..];

        int stream = IndexIn(belt, "port.BaseStream?.Dispose();", "the dispose belt");
        int close = IndexIn(belt, "port.Close();", "the dispose belt");
        int dispose = IndexIn(belt, "port.Dispose();", "the dispose belt");

        Assert.True(stream < close && close < dispose,
            "the belt must dispose the BaseStream, THEN close, THEN dispose — any other order re-arms "
            + "the pending-read close deadlock");

        // It runs on the pool: a belt that ran inline would block the
        // presence-poller's timer thread (and the read loop's fault path) on a
        // wedged driver — which is exactly what RaiseDisconnect must not do.
        Assert.Contains("=> Task.Run(() =>", belt, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO CONDITIONAL COMPILATION in the scanned files — which is what makes
    /// every pin above sound. Under <c>#if</c> a mechanism can sit in an
    /// INACTIVE region while an ungated sibling is the code that actually runs,
    /// and a stripper that does not evaluate preprocessor symbols cannot tell
    /// the two apart: it sees both, the pins match the dead one, and the guard
    /// reports safety over code that never executes. (The android TFM drops
    /// <c>WindowsSerialPort</c> by CSPROJ EXCLUSION, not by directive — so this
    /// costs the platform split nothing.)
    /// </summary>
    [Fact]
    public void TheScannedFiles_CarryNoConditionalCompilation()
    {
        string[] directives = ["#if", "#else", "#elif", "#endif"];
        var offenders = new List<string>();
        int linesRead = 0;

        foreach (var file in TransportSources())
        {
            // RAW and line-anchored: a C# directive must be the first
            // non-whitespace token on its line, so prose in a comment ("see the
            // #if below") cannot fool this, and a real one cannot hide.
            var lines = File.ReadAllText(file).Split('\n');
            linesRead += lines.Length;
            for (int n = 0; n < lines.Length; n++)
            {
                var t = lines[n].Trim();
                foreach (var d in directives)
                    if (t.StartsWith(d, StringComparison.Ordinal)
                        && (t.Length == d.Length || char.IsWhiteSpace(t[d.Length])))
                    {
                        offenders.Add($"{Path.GetFileName(file)} line {n + 1}: {t}");
                        break;
                    }
            }
        }

        Assert.True(offenders.Count == 0,
            "a transport source has acquired conditional compilation, so the stripped view is no longer "
            + "the executable view and every source pin in this file is unsound until they are revised "
            + "deliberately:\n  " + string.Join("\n  ", offenders));
        Assert.True(linesRead > 300, $"only {linesRead} lines read — the reader has drifted");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// <c>RaiseDisconnect</c>'s body ALONE. Sliced at the next member so an
    /// ordering assertion cannot be satisfied by a line belonging to
    /// <c>CheckPortPresence</c> below it — and, more to the point, so
    /// <c>CloseAsync</c>'s own capture-and-clear (identical text, further up
    /// the file) can never stand in for this one.
    /// </summary>
    private static string RaiseDisconnectBody(out string code)
    {
        code = Scannable(Path.Combine(RepoRoot(), TransportDir, WindowsPortFile));
        int start = IndexOf(code, "private void RaiseDisconnect(Exception ex)");
        int end = IndexOf(code, "private void CheckPortPresence()");
        Assert.True(end > start, "RaiseDisconnect no longer precedes CheckPortPresence — re-point this guard");
        return code[start..end];
    }

    /// <summary>Every <c>await</c> token in the code, paired with the
    /// expression it awaits.
    ///
    /// <para>The segment runs to the first semicolon OR to the next
    /// <c>await</c>, whichever comes first. The second bound is what stops one
    /// await borrowing a sibling's <c>ConfigureAwait</c> out of the same
    /// statement — see the scanner's self-pin.</para>
    ///
    /// <para><b>Accepted limitation</b>, recorded rather than chased: the
    /// canonical spelling <c>.ConfigureAwait(false)</c> is what satisfies the
    /// rule, so a re-spaced <c>ConfigureAwait( false )</c> fails the guard.
    /// That is a loud, obvious failure with an obvious fix, which is the right
    /// direction for a guard to be wrong in.</para></summary>
    private static IEnumerable<(int Index, string Segment)> AwaitExpressions(string code)
    {
        const string token = @"(?<![A-Za-z0-9_])await(?![A-Za-z0-9_])";
        foreach (Match m in Regex.Matches(code, token))
        {
            int start = m.Index;
            int end = code.Length;

            int semicolon = code.IndexOf(';', start);
            if (semicolon >= 0) end = semicolon;

            var next = Regex.Match(code[(start + m.Length)..], token);
            if (next.Success) end = Math.Min(end, start + m.Length + next.Index);

            yield return (start, code[start..end]);
        }
    }

    /// <summary>Line number in the STRIPPED view. Comments keep their newlines
    /// (only a multi-line block comment or raw literal collapses one), so this
    /// lands on or just below the real line — the snippet is the exact
    /// anchor, and the "≈" says so.</summary>
    private static int LineOf(string code, int index) => code[..index].Count(c => c == '\n') + 1;

    private static string Snippet(string segment)
    {
        var flat = Regex.Replace(segment, @"\s+", " ").Trim();
        return flat.Length <= 90 ? flat : flat[..90] + "…";
    }

    /// <summary>IndexOf that FAILS rather than returning -1, so an ordering
    /// assertion can never be satisfied by two missing anchors.</summary>
    private static int IndexOf(string code, string needle)
    {
        int i = code.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"the transport no longer contains `{needle}` as CODE — re-point this guard");
        return i;
    }

    private static int IndexIn(string body, string needle, string where)
    {
        int i = body.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{where} no longer contains `{needle}` as CODE — re-point this guard");
        return i;
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string RepoRoot()
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
