using System.Text;
using System.Text.RegularExpressions;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 4 (Gate C2) — the DI-registration guard, in the house
/// source-scan style (GuiOutScopeGuardTests / AppScopeGuardTests /
/// RefreshButtonWidthGuardTests).
///
/// <para><b>Why a source scan and not a container smoke.</b> A real resolution
/// test would have to build the container MauiProgram builds, but
/// <c>MauiProgram</c> lives in <c>Falcon.App</c>, which targets only
/// android/windows TFMs — this host-only net10.0 test project cannot
/// reference it (see the csproj's Stage-0 note; that is the whole reason the
/// ViewModels live in <c>Falcon.App.Core</c>). Re-declaring the registrations
/// against a local <c>ServiceCollection</c> would test the copy, not
/// MauiProgram, and would still pass with the real registration deleted —
/// exactly the defect this guards. So the pin reads the wiring as SOURCE,
/// which is also what lets it see a <c>#if ANDROID</c> registration.</para>
///
/// <para><b>The defect.</b> A code-behind that resolves a ViewModel
/// MauiProgram never registered compiles, ships, and throws the first time
/// the operator opens that pane. A round-1 audit deleted
/// <c>AddSingleton&lt;SsbChannelEditorViewModel&gt;()</c> and BOTH suites
/// stayed green. This pin DERIVES the required set by scanning what the app
/// actually resolves, so it also catches the next missed registration rather
/// than only the one that was reported.</para>
///
/// <para><b>Why the source is stripped first.</b> The round-2 audit walked
/// through the first version by COMMENTING OUT the registration: the text
/// still matched, so the guard stayed green while resolution would return
/// null at runtime. Comments, string literals and char literals are therefore
/// removed before anything is matched, on BOTH sides of the comparison — a
/// commented-out <c>GetService&lt;T&gt;()</c> must not invent a requirement
/// either. (Phase B built the same stripper for its own guard this round;
/// a post-merge pass can unify the two helpers.)</para>
///
/// <para><b>ACCEPTED LIMITATION (owner deferral, 2026-08-12).</b> The
/// stripper removes comments and string/char literals but does NOT evaluate
/// preprocessor regions: a registration wrapped in <c>#if false</c> (or an
/// undefined symbol) still matches while the compiler excludes it — a
/// round-3 audit demonstrated exactly that. Accident classes (deletion,
/// comment-out) ARE caught; preprocessor-hidden wiring is adversarial
/// construction, backstopped by review/audit, not by this scan.</para>
/// </summary>
public class DiRegistrationGuardTests
{
    /// <summary>`GetService&lt;T&gt;()` / `GetRequiredService&lt;T&gt;()`.</summary>
    private static readonly Regex GenericResolve = new(
        @"Get(?:Required)?Service<\s*([A-Za-z_][A-Za-z0-9_.]*)\s*>", RegexOptions.Compiled);

    /// <summary>`GetService(typeof(T))` — the other house spelling.</summary>
    private static readonly Regex TypeofResolve = new(
        @"Get(?:Required)?Service\(\s*typeof\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\)\s*\)", RegexOptions.Compiled);

    /// <summary>`AddSingleton&lt;T&gt;` / `AddTransient&lt;T&gt;` / `AddScoped&lt;T&gt;`,
    /// with or without a factory lambda.</summary>
    private static readonly Regex Registration = new(
        @"Add(?:Singleton|Transient|Scoped)<\s*([A-Za-z_][A-Za-z0-9_.]*)\s*[>,]", RegexOptions.Compiled);

    /// <summary>The TWO-TYPE spelling — `AddSingleton&lt;TService,
    /// TImplementation&gt;()`. The single-type pattern above captures only the
    /// first argument, so it cannot tell which implementation an interface was
    /// bound to; this one carries both.</summary>
    private static readonly Regex PairRegistration = new(
        @"Add(?:Singleton|Transient|Scoped)<\s*([A-Za-z_][A-Za-z0-9_.]*)\s*,\s*([A-Za-z_][A-Za-z0-9_.]*)\s*>",
        RegexOptions.Compiled);

    [Fact]
    public void EveryServiceTheAppResolves_IsRegisteredInMauiProgram()
    {
        var root = FindRepoRoot();
        var registered = RegisteredTypes(root);
        var missing = new List<string>();

        foreach (var (file, type) in ResolvedTypes(root))
            if (!registered.Contains(type))
                missing.Add($"{file} resolves {type}, which MauiProgram does not register");

        Assert.Empty(missing);
    }

    [Fact]
    public void TheGuard_ActuallySeesTheSettingsPaneViewModels()
    {
        // A derived-set guard that derives an EMPTY set passes vacuously. Pin
        // the pane VMs the settings screens resolve at runtime — including the
        // round-4 channel editor, whose missing registration is the defect
        // that put this file here.
        var root = FindRepoRoot();
        var resolved = ResolvedTypes(root).Select(r => r.Type).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
        {
            "SsbChannelEditorViewModel",
            "SsbSettingsViewModel",
            "HopSettingsViewModel",
            "AleSettingsViewModel",
        })
            Assert.Contains(expected, resolved);
    }

    /// <summary>
    /// UI tweaks round 10 (§5): the confirmation seam's registration, pinned
    /// EXACTLY — <c>IConfirmationPrompt</c> bound to <c>ConfirmationPrompt</c>.
    ///
    /// <para><b>Why this needs its own pin.</b> The derived-set guard above
    /// only requires what the app RESOLVES, and it derives that from explicit
    /// <c>GetService&lt;T&gt;()</c> calls (evidence, plan §1). The prompt is
    /// CONSTRUCTOR-INJECTED into ViewModels, so it produces no such call:
    /// deleting the registration would leave every existing pin green and
    /// blow up at the first destructive gesture the operator makes. So the
    /// registration line is asserted directly.</para>
    ///
    /// <para>The PAIR is asserted, not merely the interface: a registration
    /// rebound to some other implementation is a different app. Read out of
    /// the STRIPPED source, so commenting the line out fails this test — the
    /// round-2 evasion, closed here too.</para>
    /// </summary>
    [Fact]
    public void MauiProgram_BindsIConfirmationPrompt_ToConfirmationPrompt()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Falcon.App", "MauiProgram.cs");
        Assert.True(File.Exists(path), "MauiProgram.cs missing at " + path);

        var pairs = RegisteredPairs(File.ReadAllText(path));

        Assert.Contains(("IConfirmationPrompt", "ConfirmationPrompt"), pairs);
    }

    /// <summary>Anti-vacuity partner for the pin above: the pair scan must be
    /// able to MISS, and must not see a commented-out or quoted line. Without
    /// this, a pattern that silently stopped matching would only ever be
    /// noticed by the pin it was supposed to protect — which would fail for
    /// the wrong reason, or (if the pattern matched everything) not at
    /// all.</summary>
    [Fact]
    public void ThePairScan_SeesARealBinding_AndNotACommentedOrQuotedOne()
    {
        const string source = """
            public static class Sample
            {
                public static void Wire(IServiceCollection services)
                {
                    services.AddSingleton<IRealSeam, RealSeamImpl>();
                    // services.AddSingleton<ICommentedSeam, CommentedSeamImpl>();
                    /* services.AddSingleton<IBlockSeam, BlockSeamImpl>(); */
                    Log("services.AddSingleton<IQuotedSeam, QuotedSeamImpl>();");
                    services.AddSingleton<PlainSingleton>();
                }
            }
            """;

        var pairs = RegisteredPairs(source);

        Assert.Contains(("IRealSeam", "RealSeamImpl"), pairs);
        Assert.DoesNotContain(("ICommentedSeam", "CommentedSeamImpl"), pairs);
        Assert.DoesNotContain(("IBlockSeam", "BlockSeamImpl"), pairs);
        Assert.DoesNotContain(("IQuotedSeam", "QuotedSeamImpl"), pairs);
        // A one-type registration is not a binding pair.
        Assert.DoesNotContain(pairs, p => p.Service == "PlainSingleton");
        // And a real binding is not reported under the wrong implementation.
        Assert.DoesNotContain(("IRealSeam", "SomeOtherImpl"), pairs);
    }

    /// <summary>
    /// UI tweaks round 11 (§9A): the CLONING card's two registrations, pinned
    /// directly for the same reason the confirmation seam is — neither is
    /// visible to the derived-set guard.
    ///
    /// <para><c>CloneService</c> is CONSTRUCTOR-INJECTED into
    /// <c>CloneViewModel</c>, and <c>CloneViewModel</c> is constructor-injected
    /// into <c>RadioSettingsPage</c>: no <c>GetService&lt;T&gt;()</c> call
    /// exists for either, so deleting a registration would leave every derived
    /// pin green and throw the first time the operator opens the Radio-settings
    /// tab. Read out of the STRIPPED source, so commenting a line out fails
    /// this test.</para>
    /// </summary>
    [Fact]
    public void MauiProgram_RegistersTheCloningService_AndItsViewModel()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Falcon.App", "MauiProgram.cs");
        Assert.True(File.Exists(path), "MauiProgram.cs missing at " + path);

        var registered = Registration.Matches(StripCommentsAndLiterals(File.ReadAllText(path)))
            .Select(m => ShortName(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CloneService", registered);
        Assert.Contains("CloneViewModel", registered);

        // Anti-vacuity: the scan can MISS — a type nobody registered is not in
        // the set, so the two assertions above are really assertions.
        Assert.DoesNotContain("CloneServiceThatDoesNotExist", registered);
    }

    /// <summary>
    /// CLONE ROUND 12 §6 F3: the NAVIGATION seam's two registrations, pinned
    /// directly for exactly the reason the confirmation seam is — neither is
    /// visible to the derived-set guard.
    ///
    /// <para><c>INavigator</c> is CONSTRUCTOR-INJECTED into AppShell, and
    /// <c>SessionNavigationCoordinator</c> is a singleton whose whole job is
    /// to subscribe: AppShell resolves it eagerly through its constructor so
    /// that it exists at all. Neither produces a <c>GetService&lt;T&gt;()</c>
    /// call, so deleting a registration would leave every derived pin green
    /// and fail at shell construction — on launch, before any screen.</para>
    ///
    /// <para>The PAIR is asserted for the navigator: a seam rebound to another
    /// implementation is a different app. Read out of the STRIPPED source, so
    /// commenting a line out fails this test.</para>
    /// </summary>
    [Fact]
    public void MauiProgram_BindsINavigator_AndRegistersTheNavigationCoordinator()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Falcon.App", "MauiProgram.cs");
        Assert.True(File.Exists(path), "MauiProgram.cs missing at " + path);

        var source = StripCommentsAndLiterals(File.ReadAllText(path));

        Assert.Contains(("INavigator", "ShellNavigator"), RegisteredPairs(File.ReadAllText(path)));

        var registered = Registration.Matches(source)
            .Select(m => ShortName(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SessionNavigationCoordinator", registered);
        // The routed About page is resolved by the Shell's route factory, not
        // by a GetService call the derived set can see.
        Assert.Contains("AboutPage", registered);

        // Anti-vacuity: the scan can MISS.
        Assert.DoesNotContain("NavigatorThatDoesNotExist", registered);
    }

    /// <summary>
    /// CLONE WRITE-STRUCTURAL D1 (plan §5.2): the CAMPAIGN SIGNAL's two
    /// registrations, and the ORDER constraint that makes them work.
    ///
    /// <para><b>Why this needs its own pin.</b> Neither binding is visible to
    /// the derived-set guard: <c>CampaignWireCoordinator</c> is
    /// constructor-injected into <c>CloneService</c>, and
    /// <c>ICampaignSignal</c> is constructor-injected into fourteen producers.
    /// Deleting either would leave every derived pin green and ship an app in
    /// which the panes talk over the clone campaign — the 2026-08-28 field
    /// failure, restored by a one-line deletion.</para>
    ///
    /// <para><b>ONE INSTANCE, not two.</b> The interface is bound through a
    /// FACTORY that resolves the concrete type, because the CloneService needs
    /// <c>Enter()</c> and everyone else needs only the signal. Two independent
    /// <c>AddSingleton</c> lines would compile, resolve, and give the producers
    /// a coordinator no campaign ever leases. So the factory's exact shape is
    /// asserted, in the EXACT-FORM style this file uses for
    /// <c>ISettingsStore</c>.</para>
    ///
    /// <para><b>REGISTERED FIRST.</b> §5.2's ordering note: the coupler policy
    /// is required by the surfaces the CloneService requires, so the signal has
    /// to exist before either. Read positionally out of the stripped source.</para>
    /// </summary>
    [Fact]
    public void MauiProgram_RegistersTheCampaignSignalFirst_AsOneSharedInstance()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Falcon.App", "MauiProgram.cs");
        Assert.True(File.Exists(path), "MauiProgram.cs missing at " + path);

        var source = StripCommentsAndLiterals(File.ReadAllText(path));
        var registered = Registration.Matches(source)
            .Select(m => ShortName(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CampaignWireCoordinator", registered);
        Assert.Contains("ICampaignSignal", registered);
        // Anti-vacuity: the scan can MISS.
        Assert.DoesNotContain("CampaignSignalThatDoesNotExist", registered);

        // ONE INSTANCE: the interface's factory resolves the concrete type.
        Assert.Matches(
            @"AddSingleton<\s*ICampaignSignal\s*>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<\s*CampaignWireCoordinator\s*>\(\)\s*\)",
            source);

        // FIRST: ahead of both consumers named in §5.2's ordering note.
        int coordinator = source.IndexOf("AddSingleton<CampaignWireCoordinator>", StringComparison.Ordinal);
        int coupler = source.IndexOf("AddSingleton<CouplerPolicy>", StringComparison.Ordinal);
        int clone = source.IndexOf("AddSingleton<CloneService>", StringComparison.Ordinal);
        Assert.True(coordinator >= 0, "the coordinator's registration line was not found");
        Assert.True(coupler > coordinator,
            "CouplerPolicy is registered before the campaign signal it takes");
        Assert.True(clone > coordinator,
            "CloneService is registered before the campaign signal it takes");
    }

    /// <summary>
    /// ROUND 14 G (R18): the SETTINGS seam — <c>ISettingsStore</c> bound to
    /// <c>PreferencesSettingsStore</c>, and actually CONSTRUCTED INTO
    /// <c>ConnectionSettingsViewModel</c>.
    ///
    /// <para><b>Why the second half needs saying.</b> The binding alone proves
    /// nothing about the feature: the VM's store is a constructor parameter,
    /// and a registration that resolved the VM without it would compile, pass
    /// every behaviour test in the suite (they inject their own fake) and ship
    /// an app that forgets the operator's port on every launch — which is the
    /// defect this phase exists to fix, restored by a one-line edit.</para>
    ///
    /// <para>Read in Phase C's EXACT-FORM style rather than by presence
    /// (CouplerPolicyTests' strategy note records the three shapes that walked
    /// through a presence scan): exactly one of the constructor's ARGUMENTS
    /// must BE <c>sp.GetRequiredService&lt;ISettingsStore&gt;()</c>, anchored
    /// end to end, and the binding must sit unconditionally in
    /// <c>CreateMauiApp</c>'s own body. Same accepted limitation as every scan
    /// in this file: it proves the wiring is WRITTEN, not that resolution
    /// succeeds at runtime.</para>
    /// </summary>
    [Fact]
    public void MauiProgram_BindsISettingsStore_AndConstructsItIntoTheConnectionSettingsViewModel()
    {
        var raw = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Falcon.App", "MauiProgram.cs"));
        var source = StripCommentsAndLiterals(raw);

        Assert.Contains(("ISettingsStore", "PreferencesSettingsStore"), RegisteredPairs(raw));

        Assert.True(
            BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore", source, "CreateMauiApp"),
            "MauiProgram does not bind ISettingsStore as an UNCONDITIONAL top-level statement of "
            + "CreateMauiApp — a nested or conditional binding compiles, passes every other pin, "
            + "and throws at the first resolution.");

        var arguments = CouplerPolicyTests.ConstructorArguments(
            "ConnectionSettingsViewModel",
            CouplerPolicyTests.RegistrationBody("ConnectionSettingsViewModel", source));

        Assert.True(
            CouplerPolicyTests.InjectsExactly("ISettingsStore", arguments),
            "ConnectionSettingsViewModel's registration does not pass EXACTLY "
            + "`sp.GetRequiredService<ISettingsStore>()` as one of its constructor arguments; "
            + $"its argument list reads: {arguments}");

        // Anti-vacuity: the argument reader is reading THIS registration's own
        // construction, so a type it does not receive is not in it.
        Assert.False(CouplerPolicyTests.InjectsExactly("IConfirmationPrompt", arguments));
    }

    /// <summary>Anti-vacuity partner: the binding reader must be able to say
    /// NO — to a conditional, to a nested block, to a lambda, to another
    /// method, to a rebound implementation, and to a type nobody binds.</summary>
    [Fact]
    public void TheBindingReader_AcceptsOnlyAnUnconditionalTopLevelBinding()
    {
        static string Method(string body)
            => "public static class P { public static MauiApp CreateMauiApp() { " + body + " } }";
        const string real = "builder.Services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();";

        Assert.True(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore", Method(real), "CreateMauiApp"));
        Assert.True(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("host . Services . AddSingleton < ISettingsStore , PreferencesSettingsStore > ( ) ;"),
            "CreateMauiApp"));

        // Phase C's round-3 evasion and its neighbours: a registration that may
        // not RUN is not a registration.
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("if (Environment.ProcessId == int.MinValue) " + real), "CreateMauiApp"));
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("if (x) { " + real + " }"), "CreateMauiApp"));
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("if (x) { } else " + real), "CreateMauiApp"));
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("{ " + real + " }"), "CreateMauiApp"));
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            Method("Later(() => " + real + ");"), "CreateMauiApp"));
        Assert.False(BindsUnconditionally("ISettingsStore", "PreferencesSettingsStore",
            "public static class P { public static MauiApp CreateMauiApp() { Nothing(); } "
            + "static void Other() { " + real + " } }", "CreateMauiApp"));

        // A seam rebound to another implementation is a different app…
        Assert.False(BindsUnconditionally("ISettingsStore", "SomeOtherStore", Method(real), "CreateMauiApp"));
        // …and a type nobody binds is absent.
        Assert.False(BindsUnconditionally("IStoreThatDoesNotExist", "PreferencesSettingsStore",
            Method(real), "CreateMauiApp"));
    }

    /// <summary>Whether <paramref name="service"/> is bound to
    /// <paramref name="implementation"/> by a statement that UNCONDITIONALLY
    /// RUNS in <paramref name="method"/>'s own body.
    ///
    /// <para>The two-type sibling of <c>CouplerPolicyTests</c>'
    /// <c>RegistersUnconditionally</c>, which reads the ONE-type spelling and
    /// so cannot see a binding pair; the depth-and-statement-start rule is
    /// that reader's, applied through its own helpers, and the reasoning
    /// behind it is documented there.</para></summary>
    private static bool BindsUnconditionally(
        string service, string implementation, string strippedSource, string method)
    {
        var (bodyOpen, bodyClose) = CouplerPolicyTests.MethodBody(strippedSource, method);
        if (bodyOpen < 0 || bodyClose < 0) return false;

        var depth = CouplerPolicyTests.BraceDepths(strippedSource);
        int bodyDepth = depth[bodyOpen] + 1;

        var statement = new Regex(
            @"[A-Za-z_][A-Za-z0-9_]*\s*\.\s*Services\s*\.\s*Add(?:Singleton|Transient|Scoped)\s*<\s*"
            + Regex.Escape(service) + @"\s*,\s*" + Regex.Escape(implementation) + @"\s*>\s*\(\s*\)\s*;");

        foreach (Match m in statement.Matches(strippedSource))
        {
            if (m.Index <= bodyOpen || m.Index >= bodyClose) continue;
            if (depth[m.Index] != bodyDepth) continue;

            int before = m.Index - 1;
            while (before >= 0 && char.IsWhiteSpace(strippedSource[before])) before--;
            if (before < 0) continue;
            if (strippedSource[before] is ';' or '{' or '}') return true;
        }
        return false;
    }

    private static HashSet<(string Service, string Implementation)> RegisteredPairs(string source)
        => PairRegistration.Matches(StripCommentsAndLiterals(source))
            .Select(m => (ShortName(m.Groups[1].Value), ShortName(m.Groups[2].Value)))
            .ToHashSet();

    [Fact]
    public void TheScanner_DoesNotSeeCommentedOutOrQuotedCode()
    {
        // The round-2 evasion, pinned as a unit. Commenting a registration out
        // must make it INVISIBLE to the scan — that is the whole point of
        // stripping — and the same for code that only appears inside a string.
        const string source = """
            public static class Sample
            {
                public static void Wire(IServiceCollection services)
                {
                    services.AddSingleton<RealViewModel>();
                    // services.AddSingleton<CommentedOutViewModel>();
                    /* services.AddSingleton<BlockCommentedViewModel>(); */
                    Log("services.AddSingleton<QuotedViewModel>();");
                    Log(@"services.AddSingleton<VerbatimQuotedViewModel>();");
                    var quote = '"';   // a quote char must not open a string
                    services.AddSingleton<AfterTheCharLiteralViewModel>();
                }
            }
            """;

        var found = Registration.Matches(StripCommentsAndLiterals(source))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("RealViewModel", found);
        Assert.Contains("AfterTheCharLiteralViewModel", found);   // the char literal did not swallow the rest
        Assert.DoesNotContain("CommentedOutViewModel", found);
        Assert.DoesNotContain("BlockCommentedViewModel", found);
        Assert.DoesNotContain("QuotedViewModel", found);
        Assert.DoesNotContain("VerbatimQuotedViewModel", found);
    }

    /// <summary>Types the app asks the container for, OUTSIDE MauiProgram
    /// itself — the factory lambdas in there resolve peers of the same
    /// registration block and are not the risk this guards.</summary>
    private static List<(string File, string Type)> ResolvedTypes(string root)
    {
        var found = new List<(string, string)>();
        foreach (var file in AppSourceFiles(root))
        {
            if (Path.GetFileName(file).Equals("MauiProgram.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(root, file);
            var code = StripCommentsAndLiterals(File.ReadAllText(file));
            foreach (var regex in new[] { GenericResolve, TypeofResolve })
                foreach (Match m in regex.Matches(code))
                    found.Add((relative, ShortName(m.Groups[1].Value)));
        }
        return found;
    }

    private static HashSet<string> RegisteredTypes(string root)
    {
        var path = Path.Combine(root, "src", "Falcon.App", "MauiProgram.cs");
        Assert.True(File.Exists(path), "MauiProgram.cs missing at " + path);

        var registered = Registration.Matches(StripCommentsAndLiterals(File.ReadAllText(path)))
            .Select(m => ShortName(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(registered.Count > 0, "no registrations found — the scan pattern has drifted");
        return registered;
    }

    /// <summary>
    /// Remove comments, string literals and char literals from C# source, in
    /// ONE left-to-right pass so the states cannot fool each other: a
    /// <c>//</c> inside a string is not a comment, and a quote inside a
    /// comment does not open a string. Newlines survive, so nothing that is
    /// kept changes line.
    /// </summary>
    internal static string StripCommentsAndLiterals(string source)
    {
        var kept = new StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')                       // line comment
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')                       // block comment
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') kept.Append('\n');
                    i++;
                }
                i = Math.Min(i + 2, source.Length);
                continue;
            }
            if (c == '@' && next == '"')                       // verbatim string
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }   // "" escape
                        i++;
                        break;
                    }
                    if (source[i] == '\n') kept.Append('\n');
                    i++;
                }
                continue;
            }
            if (c == '"')                                      // string literal
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { i += 2; continue; }
                    if (source[i] == '"') { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '\'')                                     // char literal
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { i += 2; continue; }
                    if (source[i] == '\'') { i++; break; }
                    i++;
                }
                continue;
            }

            kept.Append(c);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>`Falcon.App.Core.ViewModels.X` and `X` are the same service.</summary>
    private static string ShortName(string type)
    {
        int dot = type.LastIndexOf('.');
        return dot < 0 ? type : type[(dot + 1)..];
    }

    private static IEnumerable<string> AppSourceFiles(string root)
    {
        var layer = Path.Combine(root, "src", "Falcon.App");
        Assert.True(Directory.Exists(layer), "app directory missing: " + layer);

        foreach (var file in Directory.EnumerateFiles(layer, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            yield return file;
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
