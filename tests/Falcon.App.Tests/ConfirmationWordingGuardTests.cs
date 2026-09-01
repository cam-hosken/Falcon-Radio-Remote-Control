using System.Text.RegularExpressions;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// ROUND 15 E-4 — <b>the confirmation vocabulary, pinned.</b> A destructive
/// question is the last thing an operator reads before the wire, and the app
/// had grown five voices for it: one prompt opened "Destructive:", one spoke
/// in the third person about "its individuals", one addressed the operator,
/// one led with an inventory. The rule (docs/ui.md, the display constitution):
///
/// <list type="bullet">
///   <item><b>TITLE</b> = <c>&lt;Verb&gt; &lt;object&gt;?</c> — one line,
///     ending in a question mark, opening with the ACCEPT verb.</item>
///   <item><b>MESSAGE</b> (the BASE constant) = one or two sentences; the
///     FIRST begins "The radio " and states what the RADIO does. No
///     "Destructive:" prefix, no second person.</item>
///   <item><b>ACCEPT</b> = the title's verb; <b>CANCEL</b> = "Cancel".</item>
/// </list>
///
/// <para><b>Three legs, each anti-vacuous</b> (critic F53), because a wording
/// guard is the easiest kind to write blind:
/// (i) the CONSTANTS' shape, read from the code that ships them;
/// (ii) a CALL-SITE MANIFEST — every <c>IConfirmationPrompt.ConfirmAsync</c>
/// caller in <c>src/</c>, asserted as a CLOSED set, so a sixth caller fails
/// here until it is worded and listed;
/// (iii) the RENDERED compositions — the MAXIMUM delete message (membership
/// line + schedule line + fault line, driven through the real VM on the real
/// stack) and the clone's composed message — because every prompt the operator
/// actually sees is a composition, and a constant that passes (i) can still be
/// rendered into something that breaks the rule.</para>
///
/// <para><b>The IMPACT lines are EXEMPT from the sentence count</b> (critic
/// F52): they are structured, one fact each, appended after the base. The
/// clone's role-change and drop lines are the same kind of structured line,
/// with one difference recorded here honestly: they are PREPENDED, so the
/// clone's rendered message opens with a role change rather than with "The
/// radio ". Leg (iii) therefore asserts what is actually true of it — the BASE
/// paragraph survives verbatim as the message's LAST paragraph, and the base
/// itself obeys the rule.</para>
/// </summary>
public class ConfirmationWordingGuardTests : SessionTestBase
{
    private const string RadioOpener = "The radio ";

    /// <summary>Every confirmation the app can raise, as the operator reads
    /// it: the four parts, from the constants that ship.</summary>
    public static TheoryData<string, string, string, string, string> Prompts => new()
    {
        // caller · title · base message · accept · cancel
        {
            "ALE delete address",
            AleProgrammingViewModel.DeleteAddressTitleFormat,
            AleProgrammingViewModel.DeleteAddressMessage,
            AleProgrammingViewModel.DeleteAccept,
            AleProgrammingViewModel.PromptCancel
        },
        {
            "ALE delete secondary self",
            AleProgrammingViewModel.DeleteSelfTitleFormat,
            AleProgrammingViewModel.DeleteSecondarySelfMessageFormat,
            AleProgrammingViewModel.DeleteAccept,
            AleProgrammingViewModel.PromptCancel
        },
        {
            "ALE delete primary self",
            AleProgrammingViewModel.DeletePrimarySelfTitleFormat,
            AleProgrammingViewModel.DeletePrimarySelfMessage,
            AleProgrammingViewModel.DeleteAccept,
            AleProgrammingViewModel.PromptCancel
        },
        {
            "ALE erase",
            AleProgrammingViewModel.EraseTitle,
            AleProgrammingViewModel.EraseMessage,
            AleProgrammingViewModel.EraseAccept,
            AleProgrammingViewModel.PromptCancel
        },
        {
            "HOP clear net",
            HopSettingsViewModel.ClearNetTitleFormat,
            HopSettingsViewModel.ClearNetMessage,
            HopSettingsViewModel.ClearNetAccept,
            HopSettingsViewModel.PromptCancel
        },
        {
            "clone write",
            CloneService.ConfirmTitle,
            CloneService.ConfirmMessage,
            CloneService.ConfirmAccept,
            CloneService.ConfirmCancel
        },
    };

    // ---- Leg (i): the constants' shape --------------------------------------

    [Theory]
    [MemberData(nameof(Prompts))]
    public void EveryConfirmation_ObeysTheVocabulary(
        string caller, string title, string message, string accept, string cancel)
    {
        // TITLE: one line, a question, opening with the accept verb.
        Assert.DoesNotContain('\n', title);
        Assert.EndsWith("?", title, StringComparison.Ordinal);
        Assert.StartsWith(accept + " ", title + " ", StringComparison.Ordinal);

        // MESSAGE: one or two sentences, the first stating what the RADIO does.
        Assert.StartsWith(RadioOpener, message, StringComparison.Ordinal);
        int sentences = message.Count(c => c == '.');
        Assert.True(sentences is 1 or 2,
            $"{caller}: the base message is {sentences} sentences — the rule allows one or two:{Environment.NewLine}{message}");
        Assert.DoesNotContain("Destructive", message, StringComparison.Ordinal);
        Assert.DoesNotContain(" you ", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" your ", message, StringComparison.OrdinalIgnoreCase);

        // CANCEL is the same safe word everywhere.
        Assert.Equal("Cancel", cancel);
    }

    [Fact]
    public void TheVocabularyGuard_SeesEveryPromptTheAppCanRaise()
    {
        // Anti-vacuity for the theory: SIX prompts, and the two that this
        // round REWORDED are among them by their new text — so a revert to the
        // old wording fails here and not only in the theory's generic shape.
        Assert.Equal(6, Prompts.Count);

        Assert.Contains("hides its individuals", AleProgrammingViewModel.DeletePrimarySelfMessage,
            StringComparison.Ordinal);
        Assert.Contains("net membership", AleProgrammingViewModel.EraseMessage, StringComparison.Ordinal);
        Assert.Contains("zeroized", CloneService.ConfirmMessage, StringComparison.Ordinal);
    }

    // ---- Leg (ii): the CLOSED call-site manifest ----------------------------

    /// <summary>The only files in <c>src/</c> allowed to raise a
    /// confirmation. A new caller is a new voice, so it fails here until it is
    /// listed above and worded to the rule.</summary>
    private static readonly string[] AllowedCallers =
    [
        Path.Combine("src", "Falcon.App.Core", "Cloning", "CloneService.cs"),
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "AleProgrammingViewModel.cs"),
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "HopSettingsViewModel.cs"),
    ];

    [Fact]
    public void TheConfirmAsyncCallSites_AreTheCLOSEDSet()
    {
        var root = FindRepoRoot();
        var callers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
            SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
                continue;

            // Comment- and string-stripped: the interface's own declaration and
            // every doc comment that NAMES ConfirmAsync must not read as a call.
            var code = DiRegistrationGuardTests.StripCommentsAndLiterals(File.ReadAllText(file));
            if (!CallSite.IsMatch(code)) continue;

            callers.Add(Path.GetRelativePath(root, file));
        }

        Assert.Equal(
            [.. AllowedCallers.OrderBy(c => c, StringComparer.Ordinal)],
            callers.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void TheCallSiteScanner_SeesACallAndIgnoresADeclarationOrAMention()
    {
        // Anti-vacuity for the manifest: the scanner must find a real call, and
        // must NOT be satisfied by the interface's declaration, by a doc
        // comment naming the method, or by the word inside a string.
        Assert.Matches(CallSite, "accepted = await _prompt.ConfirmAsync(title, message, a, c);");
        Assert.Matches(CallSite, "return await prompt.ConfirmAsync(t, m, a, c);");

        Assert.DoesNotMatch(CallSite, "Task<bool> ConfirmAsync(string title, string message);");
        Assert.DoesNotMatch(CallSite,
            DiRegistrationGuardTests.StripCommentsAndLiterals("// the card calls ConfirmAsync(…) first"));
        Assert.DoesNotMatch(CallSite,
            DiRegistrationGuardTests.StripCommentsAndLiterals("var s = \"ConfirmAsync(x)\";"));
    }

    /// <summary>A CALL — an invocation through a receiver — never the
    /// interface's own declaration (which has no <c>.</c> before it).</summary>
    private static readonly Regex CallSite =
        new(@"\.\s*ConfirmAsync\s*\(", RegexOptions.Compiled);

    // ---- Leg (iii): the RENDERED compositions -------------------------------

    [Fact]
    public void TheMAXIMUMDeleteComposition_StillOpensWithTheRadio()
    {
        // The worst case the card can render: the base message, the membership
        // line, the schedule line AND the fault line — driven through the real
        // VM on the real stack, because what the operator reads is the
        // COMPOSITION, not the constant.
        //
        // How the four are made to coexist: NT1's membership is loaded and
        // holds the target (the membership line); the schedule mirror holds a
        // row for it (the schedule line); and NT2's targeted read is left
        // UNANSWERED (the fault line) — which is exactly the state the fault
        // line exists to describe, one net known and another unknowable.
        var ale = new AleSurface(Radio);
        var prompt = new FakeConfirmationPrompt();
        var vm = new AleProgrammingViewModel(ale, Session, prompt);

        ConnectReady();
        // Short, so an UNANSWERED read faults inside the test rather than
        // holding it for the production ten seconds. Every read this test does
        // answer is answered synchronously, well inside the window.
        Radio.Ale.RefreshTimeoutMs = 300;

        Transport.InjectLine("ALE>");
        InjectStationBookAgain();
        AnswerSentinel();                               // the book commits

        vm.OpenBookTabCommand.Execute(null);
        InjectStationBookAgain();
        AnswerSentinel();                               // NT1's targeted read dispatches
        Transport.InjectLine("     MEMBER 01  AAA");
        AnswerSentinel();                               // NT1 committed, NT2 dispatches

        // NT2's read is on the wire and is NEVER answered: its key stays
        // absent, which is what makes the press re-request it — and fault.
        Assert.True(WaitUntil(() => !ale.LastMemberRead.Answered, 3_000),
            "NT2's targeted read never faulted");

        // ROUND 16 FIXES S5: that silence now draws exactly ONE retry. Let it
        // go out and fault as well — otherwise the schedule read below queues
        // behind it and the next AnswerSentinel credits the RETRY instead of
        // the schedules. NT2 stays unread either way, which is the state this
        // fixture needs; nothing asserted here changes.
        Assert.True(WaitUntil(() => Transport.CountSent("NETAD NT2") == 2, 3_000),
            "the S5 retry never went out");
        Assert.True(WaitUntil(() => Radio.PendingPingCount == 0, 3_000),
            "the retry's own sentinel never faulted");

        // The schedule mirror, holding a row for the target.
        ale.RequestLqaSchedules();
        Transport.InjectLine("EXCHANGE AAA              INTERVAL 01:00 START TIME 22:34");
        AnswerSentinel();
        Assert.NotNull(ale.LqaSchedules);

        vm.BookRows.Single(r => r.NameText == "AAA").Delete.Execute("AAA");
        Assert.True(WaitUntil(() => prompt.CallCount > 0, 5_000),
            "the delete prompt never opened");

        string message = prompt.Last.Message;

        Assert.StartsWith(RadioOpener, message, StringComparison.Ordinal);
        Assert.StartsWith(AleProgrammingViewModel.DeleteAddressMessage, message, StringComparison.Ordinal);
        Assert.Contains("\nMember of: NT1.", message, StringComparison.Ordinal);
        Assert.Contains(AleProgrammingViewModel.ImpactScheduleLine, message, StringComparison.Ordinal);
        Assert.Contains("Impact unknown (", message, StringComparison.Ordinal);

        // …and every APPENDED line is its own line — the exemption is
        // structural, not a licence for a paragraph.
        var lines = message.Split('\n');
        Assert.True(lines.Length >= 4, "the maximum composition should be four lines: " + message);
        Assert.All(lines.Skip(1), l => Assert.EndsWith(".", l, StringComparison.Ordinal));

        prompt.Last.Complete(false);
    }

    [Fact]
    public void TheCloneComposition_OpensWithTheBaseSentence_ThenItsStructuredLines()
    {
        // THE RULE APPLIES TO THE RENDERED MESSAGE, not only to the constant
        // (manager ruling, audit round 1). The clone's role-change and drop
        // lines used to be PREPENDED, so the question the operator actually
        // read opened with a NAME — "W6HOS becomes the primary self…" — and
        // said nothing about what the radio was about to do until four lines
        // in. The base leads now; the structured lines follow it, exactly as
        // the delete prompts' impact block does.
        string plain = CloneService.ConfirmMessageFor([], []);
        Assert.Equal(CloneService.ConfirmMessage, plain);
        Assert.StartsWith(RadioOpener, plain, StringComparison.Ordinal);

        string composed = CloneService.ConfirmMessageFor(
            ["W6HOS becomes the primary self"],
            ["HFL is dropped — no self"]);

        Assert.StartsWith(RadioOpener, composed, StringComparison.Ordinal);
        Assert.StartsWith(CloneService.ConfirmMessage, composed, StringComparison.Ordinal);
        Assert.EndsWith("HFL is dropped — no self", composed, StringComparison.Ordinal);

        // …and the lines keep their own order behind it: role changes, drops.
        Assert.True(
            composed.IndexOf("W6HOS becomes", StringComparison.Ordinal)
                < composed.IndexOf("HFL is dropped", StringComparison.Ordinal),
            "the role changes must precede the drops:" + Environment.NewLine + composed);

        var paragraphs = composed.Split(Environment.NewLine + Environment.NewLine);
        Assert.Equal(CloneService.ConfirmMessage, paragraphs[0]);
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>The book listing again — the book-tab landing re-reads it, and
    /// the read has to be answered before the targeted member reads
    /// dispatch.</summary>
    private void InjectStationBookAgain()
    {
        Transport.InjectLine("SLFAD ZZZ               CHGROUP 00");
        Transport.InjectLine("INDAD AAA               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NT1               CHGROUP 01   ASSOC SELF ZZZ");
        Transport.InjectLine("NETAD NT2               CHGROUP 01   ASSOC SELF ZZZ");
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
        throw new InvalidOperationException(
            "repo root (Falcon-Radio-Controller.slnx) not found above the test assembly");
    }
}
