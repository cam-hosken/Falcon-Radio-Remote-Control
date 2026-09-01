using System.Text;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 13 D2, repair 1 — SOURCE-LEVEL pins on
/// <c>AndroidUsbSerialPort</c>'s bounded, generation-safe teardown.
///
/// <para><b>Why string pins and not behaviour tests.</b> That class compiles
/// for the <c>net10.0-android</c> TFM only: it binds Android USB types, and
/// the unit suites are host <c>net10.0</c>. No host test can construct it, so
/// the plan's verification for it is deliberately SPLIT — these pins hold the
/// SHAPE of the mechanism, and the owner's device gate (pull → immediate
/// replug → reconnect → traffic flows) holds the behaviour. This is the house
/// idiom for platform files, the same one the GUI-out scope guards use.</para>
///
/// <para><b>They scan CODE, not text</b> (D2 audit round 1, MAJOR 2). The
/// first cut matched against raw file contents, and the auditor broke it in
/// the obvious way: delete the executable gate, leave the pinned expression
/// behind in a comment, and every guard stayed green. A guard that a comment
/// can satisfy is worse than no guard, because it reports safety. Every scan
/// below therefore runs on <see cref="Scannable"/> — comments and string
/// literals stripped, the idiom borrowed from
/// <c>GuiOutScopeGuardTests.StripCommentsAndLiterals</c> (a sibling test
/// assembly, so the code is duplicated rather than shared) — and the stripper
/// itself is pinned by <see cref="TheScanner_SeesCode_AndIgnoresCommentsAndLiterals"/>
/// so it cannot quietly start returning nothing and disarm the lot.</para>
///
/// <para><b>What a string pin can and cannot do.</b> It cannot prove the
/// generation check is correct. It CAN prove the mechanism has not been
/// quietly deleted or refactored away — which is the realistic failure mode
/// for code nothing executes in CI, and exactly what happened to the
/// unbounded close this repair replaced: it survived for rounds because no
/// test could see it.</para>
/// </summary>
public class AndroidTeardownSourceGuardTests
{
    private static readonly string AndroidPortFile =
        Path.Combine("src", "Falcon.App", "Platforms", "Android", "AndroidUsbSerialPort.cs");

    /// <summary>The Android port's source with comments and string literals
    /// removed — what the compiler would see, near enough for a shape pin.</summary>
    private static string Scannable()
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), AndroidPortFile));
        var code = StripCommentsAndLiterals(raw);

        // ANTI-VACUITY, checked on every single scan rather than once: a
        // stripper that returned "" (or that ate the whole file after one
        // malformed literal) would make every Assert.Contains below fail
        // loudly — but it would also make every Assert.DoesNotContain pass
        // silently, which is the dangerous direction. So prove the scan has
        // real code in it before anyone matches against it.
        Assert.True(code.Length > 3_000,
            $"the scannable source collapsed to {code.Length} chars — the stripper has eaten the file");
        Assert.Contains("internal sealed class AndroidUsbSerialPort : ISerialPort", code, StringComparison.Ordinal);
        return code;
    }

    // ---- The stripper itself -------------------------------------------------

    /// <summary>
    /// The stripper is load-bearing for every scan in this file, so it is
    /// pinned as a unit rather than trusted — the same treatment, and the same
    /// reasoning, as the GUI-out scanner's own self-pin.
    /// </summary>
    [Fact]
    public void TheScanner_SeesCode_AndIgnoresCommentsAndLiterals()
    {
        const string sample = """
            // IsCurrentGeneration in a line comment
            /* IsCurrentGeneration in a block comment */
            var s = "IsCurrentGeneration in a string";
            var v = @"IsCurrentGeneration in a verbatim string";
            var c = 'x';
            if (IsCurrentGeneration(generation)) Emit();
            """;

        var code = StripCommentsAndLiterals(sample);

        // The NEGATIVE half: not one of the four decoy occurrences survives.
        Assert.Equal(1, CountOf(code, "IsCurrentGeneration"));
        // …and the POSITIVE half, which is what stops a stripper that just
        // returns "" from passing the line above.
        Assert.Contains("if (IsCurrentGeneration(generation)) Emit();", code, StringComparison.Ordinal);
        Assert.Contains("var c =", code, StringComparison.Ordinal);
    }

    // ---- The generation identity -------------------------------------------

    [Fact]
    public void TheReadLoop_TakesItsDriverAndGenerationAsArguments_NotFromFields()
    {
        // The whole race fix rests on this signature. A loop that reads the
        // _driver FIELD is a loop that can be pointed at the NEXT session's
        // port half-way through a teardown.
        var code = Scannable();
        Assert.Contains(
            "private async Task RunReadLoopAsync(UsbDriverBase driver, int generation, CancellationToken ct)",
            code, StringComparison.Ordinal);

        // …and Open must actually pass them in.
        Assert.Contains("Interlocked.Increment(ref _generation)", code, StringComparison.Ordinal);
        Assert.Contains("RunReadLoopAsync(driver, generation, cts.Token)", code, StringComparison.Ordinal);

        // The old field dereference must be gone: `_driver!.ReadAsync` was the
        // exact expression that made a stale loop dangerous.
        Assert.DoesNotContain("_driver!.ReadAsync", code, StringComparison.Ordinal);
    }

    [Fact]
    public void BothReadLoopEffects_AreGatedOnTheGenerationStillBeingCurrent()
    {
        var code = Scannable();

        // The predicate exists and reads the field with a volatile read (a
        // plain read could be hoisted out of the loop).
        Assert.Contains(
            "private bool IsCurrentGeneration(int generation) => Volatile.Read(ref _generation) == generation;",
            code, StringComparison.Ordinal);

        // EFFECT 1 — emitting bytes. Re-checked AFTER the await, because the
        // generation advances while a read is parked; that IS the race.
        Assert.Contains("if (bytesRead > 0 && IsCurrentGeneration(generation))", code, StringComparison.Ordinal);

        // EFFECT 2 — marking the session disconnected. A stale loop's fault is
        // the expected consequence of its own port closing; reporting it would
        // tear down whichever session is live now.
        Assert.Contains("if (!IsCurrentGeneration(generation)) return;", code, StringComparison.Ordinal);

        // And the loop stops looping once it is stale.
        Assert.Contains("while (!ct.IsCancellationRequested && IsCurrentGeneration(generation))",
            code, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PUBLICATION ORDER (D2 audit rounds 1 and 2, MAJOR 1). Getting these
    /// statements in the wrong order reopens the race the generation exists to
    /// close, and it does so invisibly — every individual pin above still
    /// passes either way. Order is the fix, so order is pinned.
    ///
    /// <para>The FIRST anchor is the increment at METHOD ENTRY, ahead of the
    /// driver open. Round 1 moved it ahead of the latch reset, which was not
    /// far enough: an open can FAIL — no device, permission refused, the
    /// driver's own open throwing — and with the increment below that call, an
    /// abandoned read still compared CURRENT for the whole duration of a
    /// failing reconnect and could surface stale lines as live traffic.</para>
    /// </summary>
    [Fact]
    public void OpenAsync_AdvancesTheGeneration_BeforeTheDriverOpen_TheLatchReset_AndIsOpen()
    {
        var code = Scannable();

        int generationUp = IndexOf(code, "Interlocked.Increment(ref _generation)");
        int driverOpen = IndexOf(code, "driver.OpenAsync(settings.BaudRate");
        int latchReset = IndexOf(code, "Interlocked.Exchange(ref _disconnectFired, 0)");
        int isOpenTrue = IndexOf(code, "_isOpen = true;");
        int loopStart = IndexOf(code, "RunReadLoopAsync(driver, generation, cts.Token)");

        Assert.True(generationUp < driverOpen,
            "the generation must advance BEFORE the driver is opened — otherwise a FAILED open "
            + "leaves the previous generation current, and an abandoned read can surface stale "
            + "lines for the whole duration of a failing reconnect");
        Assert.True(driverOpen < latchReset,
            "the disconnect latch must be re-armed only AFTER the open has succeeded");
        Assert.True(latchReset < isOpenTrue,
            "the latch must be armed before the session becomes observable");
        Assert.True(isOpenTrue < loopStart,
            "the session must be observable before its read loop starts");
    }

    /// <summary>
    /// NO CONDITIONAL COMPILATION IN THE SCANNED FILE (D2 audit round 2,
    /// MAJOR 2 residual) — which is what makes every other pin here sound.
    ///
    /// <para>The stripper removes comments and literals, so the scanned view
    /// is the executable view — <b>unless the file uses <c>#if</c></b>. Under
    /// conditional compilation a gate can sit in an INACTIVE region while an
    /// ungated sibling is the code that actually runs, and a stripper that
    /// does not evaluate preprocessor symbols cannot tell the two apart: it
    /// sees both, the pins match the dead one, and the guard reports safety
    /// over code that never executes.</para>
    ///
    /// <para>Rather than teach the stripper preprocessor semantics — which
    /// means resolving build symbols and would be a second, unverified
    /// compiler — the class of attack is closed at the source: this file has
    /// no conditional compilation at all, and may not acquire any. A future
    /// legitimate <c>#if</c> fails HERE, loudly, and forces a deliberate
    /// revision of these guards rather than silently hollowing them out.</para>
    /// </summary>
    [Fact]
    public void TheScannedFile_ContainsNoConditionalCompilation_SoTheStrippedViewIsTheExecutableView()
    {
        // Scanned RAW and line-anchored: a C# directive must be the first
        // non-whitespace token on its line, so this cannot be fooled by prose
        // in a comment ("// see the #if below" trims to "//", not "#if"), and
        // cannot miss a real one.
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), AndroidPortFile));
        string[] conditionals = ["#if", "#else", "#elif", "#endif"];

        var offenders = new List<string>();
        var lines = raw.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var t = lines[n].Trim();
            foreach (var d in conditionals)
            {
                // "#if" must be the directive itself, not a prefix of an
                // identifier-ish token — so require end-of-line or whitespace.
                if (t.StartsWith(d, StringComparison.Ordinal)
                    && (t.Length == d.Length || char.IsWhiteSpace(t[d.Length])))
                {
                    offenders.Add($"line {n + 1}: {t}");
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "AndroidUsbSerialPort.cs has acquired conditional compilation, so the comment/string "
            + "stripper no longer yields the executable view and every source pin in this file is "
            + "unsound until they are revised deliberately:\n  " + string.Join("\n  ", offenders));

        // Anti-vacuity: the reader really read the file.
        Assert.True(lines.Length > 100, $"only {lines.Length} lines read — the reader has drifted");
    }

    // ---- The bounded close --------------------------------------------------

    [Fact]
    public void CloseAsync_CapturesAndClearsTheSessionHandlesBeforeTearingDown()
    {
        // Capture-then-clear is what lets a concurrent reopen build clean
        // state while this teardown is still running on the old handles.
        var code = Scannable();
        int capture = IndexOf(code, "var readLoopTask = _readLoopTask;");
        int clear = IndexOf(code, "_readLoopTask = null;");
        int phaseOne = IndexOf(code, "await RunBoundedAsync(");

        Assert.Contains("var readLoopCts = _readLoopCts;", code, StringComparison.Ordinal);
        Assert.Contains("var driver = _driver;", code, StringComparison.Ordinal);
        Assert.True(clear > capture, "the fields must be cleared AFTER they are captured");
        Assert.True(phaseOne > clear, "the fields must be cleared BEFORE the teardown phases run");
    }

    [Fact]
    public void CloseAsync_RunsTwoBoundedPhases_WithCancellationInsideTheFirst()
    {
        var code = Scannable();

        // The cap itself, and the number the plan fixed it at.
        Assert.Contains("private const int TeardownPhaseMs = 1_000;", code, StringComparison.Ordinal);

        // The helper races the work against the deadline and ABANDONS it —
        // no await of the work on the timeout path, or the cap is a lie.
        Assert.Contains("await Task.WhenAny(task, Task.Delay(TeardownPhaseMs)).ConfigureAwait(false);",
            code, StringComparison.Ordinal);
        // The pool hop: without it the work's first synchronous stretch runs
        // on the caller's thread and blows the deadline before the race starts.
        Assert.Contains("var task = Task.Run(work);", code, StringComparison.Ordinal);

        // TWO phases, and exactly two.
        Assert.Equal(2, CountOf(code, "await RunBoundedAsync("));

        // CancelAsync runs registered callbacks and is itself unbounded, so it
        // must sit INSIDE phase 1 rather than in front of it (pass-3 F2).
        Assert.True(IndexOf(code, "readLoopCts.CancelAsync()") > IndexOf(code, "await RunBoundedAsync("),
            "CancelAsync must be awaited INSIDE the first bounded phase, not before it");
    }

    [Fact]
    public void TheSeamContract_Survives_IsOpenFalseBeforeAnyoneObserves_AndOneLatchPoint()
    {
        var code = Scannable();

        // The disconnect latch is still taken before the phases (an explicit
        // close must not surface as a spontaneous disconnect), and IsOpen is
        // still flipped false with it.
        int latch = IndexOf(code, "Interlocked.Exchange(ref _disconnectFired, 1);");
        int isOpenFalse = code.IndexOf("_isOpen = false;", latch, StringComparison.Ordinal);
        int phaseOne = IndexOf(code, "await RunBoundedAsync(");
        Assert.True(isOpenFalse > latch && phaseOne > isOpenFalse,
            "CloseAsync must latch and flip IsOpen false BEFORE the teardown phases");

        // Still exactly ONE latch point for the spontaneous sources.
        Assert.Contains("private bool TryLatchDisconnect()", code, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(code, "if (Interlocked.Exchange(ref _disconnectFired, 1) != 0) return false;"));
    }

    [Fact]
    public void TheDetachBroadcast_StillLeavesTheMainLooperImmediately()
    {
        // RadioForegroundService's ANR warning is the checklist: the broadcast
        // arrives on the MAIN thread, so only the latch may be taken there —
        // the event dispatch and the teardown chain it drags along go to the
        // pool. D2 lengthens nothing on this path, but it is pinned because
        // D2 is the round that made teardown bounded, and a future edit that
        // moved this back onto the looper would undo the point of it.
        var code = Scannable();
        int detach = IndexOf(code, "private void OnUsbDetached(");

        var body = code[detach..];
        int latch = body.IndexOf("TryLatchDisconnect()", StringComparison.Ordinal);
        int hop = body.IndexOf("Task.Run(() => Disconnected?.Invoke(", StringComparison.Ordinal);
        Assert.True(latch > 0 && hop > latch,
            "the detach handler must latch synchronously and then dispatch off the main thread");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>IndexOf that FAILS THE TEST rather than returning -1, so an
    /// ordering assertion can never be satisfied by two missing anchors.</summary>
    private static int IndexOf(string code, string needle)
    {
        int i = code.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"the Android port no longer contains `{needle}` as CODE — re-point this guard");
        return i;
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// Replace every comment and every string/char literal with a single
    /// space, leaving the code. Ported from
    /// <c>GuiOutScopeGuardTests.StripCommentsAndLiterals</c> — a different
    /// test assembly, so this is a deliberate duplicate rather than a shared
    /// helper. Handles line and block comments, raw string literals
    /// (<c>"""…"""</c>), verbatim strings (<c>@"…""…"</c>), and ordinary
    /// quoted strings and chars with backslash escapes.
    /// </summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char ch = source[i];

            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                sb.Append(' ');
            }
            else if (ch == '"' && QuoteRun(source, i) >= 3)
            {
                int open = QuoteRun(source, i);
                i += open;
                while (i < source.Length && QuoteRun(source, i) < open) i++;
                i += i < source.Length ? QuoteRun(source, i) : 0;
                sb.Append(' ');
            }
            else if (ch == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
            }
            else if (ch is '"' or '\'')
            {
                char quote = ch;
                i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }
                if (i < source.Length && source[i] == quote) i++;
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }
        return sb.ToString();
    }

    private static int QuoteRun(string s, int start)
    {
        int n = 0;
        while (start + n < s.Length && s[start + n] == '"') n++;
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
