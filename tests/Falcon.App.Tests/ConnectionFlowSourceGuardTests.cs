using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// CLONE ROUND 12 §6 — the connection-first flow's APP-HEAD half, pinned as
/// SOURCE, in the house scan style (RefreshButtonWidthGuardTests /
/// DiRegistrationGuardTests).
///
/// <para><b>Why source and not behaviour.</b> Everything below lives in
/// <c>Falcon.App</c>, which targets android/windows only — this host-only
/// net10.0 project cannot reference it, cannot construct a Shell and cannot
/// execute a route. The DECISIONS still have to be held: which tab the app
/// opens on, that About is registered and PUSHED while the tab navigations
/// are ABSOLUTE, that the coordinator is actually resolved (a singleton
/// nobody resolves never subscribes), that the Connect button really moved,
/// and that the title bar is [icon][name][spacer][About]. Each is a
/// structural read of the file that carries it.</para>
///
/// <para><b>ROUND 13 C1</b> adds the About page's own half of that shape
/// (backlog item 12): its page-level TitleView with NO button — which is the
/// whole cure for the recursive push — the tab-bar lockout, the picture, and
/// the credit line's move from an <c>x:Static</c> bind to a code-behind
/// composition. Same limitation applies: this reads FILES, not a running
/// page. The rendered result is a manual check, ledgered in docs/ui.md.</para>
///
/// <para><b>ACCEPTED LIMITATION</b>, the same one every scan here carries: a
/// value supplied indirectly — a merged dictionary, a runtime assignment, a
/// platform override — is invisible, and none of this proves the RUNTIME
/// behaviour. Push/back and "a phase edge lands on the tab while About is
/// showing" are RECORDED MANUAL CHECKS on both heads (docs/ui.md); accidents
/// (a deleted line, a commented-out one, a route that lost its slashes) are
/// what this catches.</para>
/// </summary>
public class ConnectionFlowSourceGuardTests
{
    private static readonly string AppShellXaml = Path.Combine("src", "Falcon.App", "AppShell.xaml");
    private static readonly string AppShellCode = Path.Combine("src", "Falcon.App", "AppShell.xaml.cs");
    private static readonly string NavigatorCode =
        Path.Combine("src", "Falcon.App", "Services", "ShellNavigator.cs");
    private static readonly string SettingsXaml =
        Path.Combine("src", "Falcon.App", "Views", "SettingsPage.xaml");
    private static readonly string SettingsCode =
        Path.Combine("src", "Falcon.App", "Views", "SettingsPage.xaml.cs");
    private static readonly string AboutXaml =
        Path.Combine("src", "Falcon.App", "Views", "AboutPage.xaml");
    private static readonly string AboutCode =
        Path.Combine("src", "Falcon.App", "Views", "AboutPage.xaml.cs");
    private static readonly string StoreCode =
        Path.Combine("src", "Falcon.App", "Services", "PreferencesSettingsStore.cs");
    private static readonly string ConnectionSettingsCode =
        Path.Combine("src", "Falcon.App.Core", "ViewModels", "ConnectionSettingsViewModel.cs");
    private static readonly string AppProject =
        Path.Combine("src", "Falcon.App", "Falcon.App.csproj");
    private static readonly string IconAsset =
        Path.Combine("src", "Falcon.App", "Resources", "Images", "falconrc_icon.png");
    private static readonly string AboutAsset =
        Path.Combine("src", "Falcon.App", "Resources", "Images", "falconrc_about.png");

    // ---- F1: the default tab -------------------------------------------------

    [Fact]
    public void TheTabOrder_IsUnchanged_AndOperateIsStillFirst()
    {
        // F1 changes which tab is SELECTED, not where the tabs are. Pinned
        // because "make connection the default" is one edit away from
        // "move connection to the front", which is a different app.
        var routes = Load(AppShellXaml).Descendants()
            .Where(e => e.Name.LocalName == "ShellContent")
            .Select(e => e.Attribute("Route")?.Value)
            .ToList();

        Assert.Equal(["operate", "modesettings", "radiosettings", "settings"], routes);
    }

    [Fact]
    public void TheConnectionTabIsNamed_AndTheShellSelectsItAtConstruction()
    {
        // The name exists only so the constructor can point CurrentItem at it;
        // both halves are pinned together, since either one alone is inert.
        var tab = Assert.Single(Load(AppShellXaml).Descendants(), e =>
            e.Name.LocalName == "ShellContent"
            && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ConnectionSettingsTab"));
        Assert.Equal("settings", tab.Attribute("Route")?.Value);

        Assert.Contains("CurrentItem = ConnectionSettingsTab;", Code(AppShellCode), StringComparison.Ordinal);
    }

    // ---- F3: activation, registration, and route SHAPES ----------------------

    [Fact]
    public void TheShell_RegistersTheAboutRoute_AndActivatesTheCoordinatorAfterSettingTheDefaultTab()
    {
        var code = Code(AppShellCode);

        // The app's FIRST routed page — About is pushed, not a fifth tab.
        Assert.Contains("Routing.RegisterRoute(AboutRoute, typeof(AboutPage));", code, StringComparison.Ordinal);

        // ACTIVATION: the coordinator is a constructor dependency (so the
        // container builds it) and is explicitly ACTIVATED here.
        Assert.Contains("SessionNavigationCoordinator coordinator", code, StringComparison.Ordinal);
        Assert.Contains("coordinator.Activate();", code, StringComparison.Ordinal);

        // ORDER (§6 F3, corrected in audit round 1): the default tab is set
        // BEFORE the coordinator starts listening. The earlier version of this
        // pin read a DISCARD statement, which could never have held the
        // ordering that matters — constructor ARGUMENTS are built before this
        // body runs, so a coordinator that subscribed in its own constructor
        // was already listening while the tab was still being chosen, and the
        // discard sat harmlessly after it. What must come after the assignment
        // is the ACTIVATION CALL, so that is what is read.
        int tab = code.IndexOf("CurrentItem = ConnectionSettingsTab;", StringComparison.Ordinal);
        int activate = code.IndexOf("coordinator.Activate();", StringComparison.Ordinal);
        Assert.True(tab >= 0 && activate > tab,
            "the F1 default-tab assignment must come BEFORE coordinator.Activate()");

        // …and the discard the pin used to read is GONE, so nobody can satisfy
        // the ordering with a statement that starts no subscription.
        Assert.DoesNotContain("_ = coordinator;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutButton_IsWiredToTheNavigator()
    {
        var code = Code(AppShellCode);
        Assert.Contains("_navigator.GoToAbout()", code, StringComparison.Ordinal);

        // …and the handler the markup names really exists.
        Assert.Contains("OnAboutClicked", code, StringComparison.Ordinal);
        Assert.Equal("OnAboutClicked",
            Load(AppShellXaml).Descendants()
                .Single(e => e.Name.LocalName == "Button")
                .Attribute("Clicked")?.Value);
    }

    [Fact]
    public void TheTabNavigationsAreAbsolute_AndTheAboutPushIsRelative()
    {
        // THE routing decision of §6 F3. Absolute (`//`) navigation resets the
        // stack, so a phase edge arriving while About is pushed lands on the
        // target tab instead of leaving About on top of it. About itself is
        // the deliberate opposite — a relative PUSH, so platform back returns.
        var code = Code(NavigatorCode);

        Assert.Contains("GoAsync(\"//operate\")", code, StringComparison.Ordinal);
        Assert.Contains("GoAsync(\"//settings\")", code, StringComparison.Ordinal);
        Assert.Contains("GoAsync(AppShell.AboutRoute)", code, StringComparison.Ordinal);

        // The explicit pop that makes "lands on the tab, About cleared" hold
        // WITHOUT resting on Shell's delta computation — §6 F3's premise that
        // absolute navigation alone clears a pushed page could not be verified
        // on the dev box (docs/ui.md's outstanding checks say why). It is
        // pinned as part of the contract rather than left as an incidental
        // line a tidy-up could remove.
        Assert.Contains("PopToRootAsync", code, StringComparison.Ordinal);
        Assert.Contains("IsAbsolute(route)", code, StringComparison.Ordinal);

        // The About route is RELATIVE — the one property that makes it a push.
        var declaration = Code(AppShellCode);
        var route = System.Text.RegularExpressions.Regex.Match(
            declaration, @"AboutRoute\s*=\s*""(?<route>[^""]*)""");
        Assert.True(route.Success, "AppShell declares no AboutRoute constant");
        Assert.False(route.Groups["route"].Value.StartsWith('/'),
            "the About route must be RELATIVE (no leading slashes) — an absolute one would "
            + "replace the tab instead of pushing over it, and back would have nowhere to go");
        Assert.NotEmpty(route.Groups["route"].Value);
    }

    // ---- F2: the Connect button moved ----------------------------------------

    [Fact]
    public void TheConnectToggle_IsGoneFromTheShell()
    {
        // Half of a move is the worst outcome: two buttons, one of which is
        // bound to a BindingContext the code-behind no longer sets.
        var shell = Load(AppShellXaml);

        Assert.DoesNotContain(shell.Descendants(),
            e => e.Name.LocalName == "Button"
                 && (e.Attribute("Command")?.Value ?? "").Contains("ToggleCommand", StringComparison.Ordinal));
        Assert.DoesNotContain("ConnectToggleViewModel", Code(AppShellCode), StringComparison.Ordinal);

        // Anti-vacuity: the reader does see this file's ONE surviving button.
        Assert.Single(shell.Descendants(), e => e.Name.LocalName == "Button");
    }

    [Fact]
    public void TheConnectToggle_SitsFullWidthBelowTheLastCard_AboveTheStatusLine()
    {
        // §6 F2's placement, structurally: the button is a sibling of the
        // cards, AFTER the last of them and BEFORE the status line — not
        // inside a card, which is where a later edit would most plausibly
        // tuck it.
        var stack = Load(SettingsXaml).Descendants()
            .Single(e => e.Name.LocalName == "VerticalStackLayout"
                         && e.Elements().Any(c => c.Name.LocalName == "Border"));
        var children = stack.Elements().ToList();

        int button = children.FindIndex(e =>
            e.Name.LocalName == "Button"
            && (Property(e, "Command") ?? "").Contains("ToggleCommand", StringComparison.Ordinal));
        int lastCard = children.FindLastIndex(e => e.Name.LocalName == "Border");
        int status = children.FindIndex(e =>
            e.Name.LocalName == "Label"
            && (Property(e, "Text") ?? "").Contains("StatusText", StringComparison.Ordinal));

        Assert.True(lastCard >= 0 && button > lastCard, "the toggle sits BELOW the last card");
        Assert.True(status > button, "the toggle sits ABOVE the status line");

        // FULL WIDTH — and this is the pin that carries it. The action-class
        // width guard asserts the button pins NO WidthRequest (that is how it
        // stays natural-sizing compliant), so nothing there can say the button
        // fills its row. This does.
        Assert.Equal("Fill", Property(children[button], "HorizontalOptions"));
        Assert.Null(Property(children[button], "WidthRequest"));
    }

    [Fact]
    public void TheSettingsPage_RootStack_FillsThePageWidth()
    {
        // 2026-08-23 owner ask: the cards and the Connect button must span
        // the window on EVERY idiom. The old root declared
        // OnIdiom Default=Start, Phone=Fill — desktop shrank the whole stack
        // to content width, and the toggle's own "Fill" (pinned above) could
        // only fill that shrunk stack. The pin reads the PLAIN value: an
        // idiom-split resurrection fails the equality just like a bare Start.
        var stack = Load(SettingsXaml).Descendants()
            .Single(e => e.Name.LocalName == "VerticalStackLayout"
                         && e.Elements().Any(c => c.Name.LocalName == "Border"));

        Assert.Equal("Fill", Property(stack, "HorizontalOptions"));
    }

    [Fact]
    public void TheSettingsPage_WiresThePickerToThePollsThreeSeams()
    {
        // Without these three lines the F4 machinery is unreachable: nothing
        // would defer scans while the dropdown is open, and no pick would ever
        // be attributed to the operator.
        var code = Code(SettingsCode);

        Assert.Contains("BeginPortInteraction()", code, StringComparison.Ordinal);
        Assert.Contains("EndPortInteraction()", code, StringComparison.Ordinal);
        Assert.Contains("SelectPortByUser(", code, StringComparison.Ordinal);

        // …and the DI wiring that moved with the button.
        Assert.Contains("ConnectToggleButton.BindingContext = connectToggle;", code, StringComparison.Ordinal);

        // The named Picker the handlers hang off really exists.
        Assert.Contains(Load(SettingsXaml).Descendants(),
            e => e.Name.LocalName == "Picker"
                 && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "PortPicker"));
    }

    // ---- ROUND 14 G (R18): the settings store's app-head half ------------------

    /// <summary>
    /// <c>PreferencesSettingsStore</c> really talks to the PLATFORM store.
    ///
    /// <para><b>The defect this catches.</b> Every behaviour test of the
    /// remembered port runs against a fake store, so a
    /// <c>PreferencesSettingsStore</c> whose <c>Get</c> returned null and whose
    /// <c>Set</c> did nothing would leave both suites green and ship an app
    /// that still forgets COM20 — the report this phase answers. The file lives
    /// in Falcon.App (android/windows TFMs), which this host-only project
    /// cannot reference, so the pin is STRUCTURAL: each method's own brace
    /// range must contain the platform call that makes it work, with the source
    /// stripped of comments and literals first so a commented-out or quoted
    /// call proves nothing.</para>
    ///
    /// <para>ACCEPTED LIMITATION, the same one the whole file carries: this
    /// proves the calls are WRITTEN, not that the platform stored anything.
    /// Round-trip on a device is a manual check.</para>
    /// </summary>
    [Fact]
    public void ThePreferencesStore_ReadsAndWritesThePlatformStore_InTheMethodsThatPromiseTo()
    {
        var source = DiRegistrationGuardTests.StripCommentsAndLiterals(
            File.ReadAllText(Path.Combine(FindRepoRoot(), StoreCode)));

        Assert.Contains("class PreferencesSettingsStore : ISettingsStore", source, StringComparison.Ordinal);

        Assert.True(Calls(source, "Get", "Preferences.Default.Get"),
            "PreferencesSettingsStore.Get does not read Preferences.Default — a store that "
            + "always answers null passes every fake-backed test and forgets the port on every launch");
        Assert.True(Calls(source, "Set", "Preferences.Default.Set"),
            "PreferencesSettingsStore.Set does not write Preferences.Default");
        // The contract's forget half: an empty value REMOVES the key rather
        // than storing a blank one that Get would then have to special-case.
        Assert.True(Calls(source, "Set", "Preferences.Default.Remove"),
            "PreferencesSettingsStore.Set does not remove the key for an empty value");
    }

    /// <summary>
    /// KEY IDENTITY — the half the calls-exist pin above cannot see (audit
    /// round 1, MAJOR).
    ///
    /// <para><b>The defect.</b> <c>Preferences.Default.Set(key + ".write",
    /// value)</c> calls the platform store in the method that promises to,
    /// satisfies every assertion above, and survived 1970/1970: the write lands
    /// under one key and the next launch reads another, which is the owner's
    /// "it forgets COM20" restored in full with a green suite. The seam's whole
    /// contract is that <c>Get(k)</c> answers what <c>Set(k, …)</c> stored, and
    /// a decorated key breaks it silently.</para>
    ///
    /// <para><b>How it is closed.</b> Exact-form whitelist, not a blacklist of
    /// decorations: EVERY platform call in each method must pass the method's
    /// own <c>key</c> parameter through as its first argument and NOTHING ELSE
    /// — the argument expression IS the identifier, so a concatenation, a
    /// method call on it, a cast, an interpolation or a different variable all
    /// fail by construction rather than by being enumerated. Two lexical
    /// escapes are closed with it: the parameter must be DECLARED
    /// <c>string key</c> (C# forbids a local shadowing a parameter, so an
    /// evasion cannot introduce its own <c>key</c>), and the body must never
    /// REASSIGN it.</para>
    /// </summary>
    [Fact]
    public void ThePreferencesStore_PassesTheCALLERS_KeyThrough_UNDECORATED()
    {
        var source = DiRegistrationGuardTests.StripCommentsAndLiterals(
            File.ReadAllText(Path.Combine(FindRepoRoot(), StoreCode)));

        Assert.True(DeclaresKeyParameter(source, "Get"), "PreferencesSettingsStore.Get(string key) has been renamed");
        Assert.True(DeclaresKeyParameter(source, "Set"), "PreferencesSettingsStore.Set(string key, …) has been renamed");

        foreach (var (method, call) in new[]
        {
            ("Get", "Preferences.Default.Get"),
            ("Set", "Preferences.Default.Set"),
            ("Set", "Preferences.Default.Remove"),
        })
            Assert.True(
                PassesKeyThrough(source, method, call),
                $"PreferencesSettingsStore.{method} does not pass its own `key` through to {call} "
                + "UNDECORATED — a key written under one spelling and read under another forgets the "
                + "operator's port on every launch, with the whole suite green");

        Assert.False(Reassigns(source, "Get", "key"), "PreferencesSettingsStore.Get reassigns its key parameter");
        Assert.False(Reassigns(source, "Set", "key"), "PreferencesSettingsStore.Set reassigns its key parameter");
    }

    [Fact]
    public void TheKeyReader_AcceptsOnlyTheBareIdentifier_AndRejectsTheAuditorsMutation()
    {
        // Anti-vacuity partner. The FIRST sample is the auditor's exact
        // mutation shape; the rest are the neighbours it belongs to.
        static string Store(string body)
            => "class S { public void Set(string key, string? value) { " + body + " } }";

        // The real shape, and the same thing spelled with whitespace.
        Assert.True(PassesKeyThrough(Store("Preferences.Default.Set(key, value);"), "Set", "Preferences.Default.Set"));
        Assert.True(PassesKeyThrough(
            Store("Preferences . Default . Set ( key , value ) ;"), "Set", "Preferences.Default.Set"));

        // THE AUDITOR'S MUTATION: a write under a decorated key.
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(key + \".write\", value);"), "Set", "Preferences.Default.Set"));
        // …and its family. None of these is the identifier itself.
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(key.ToUpperInvariant(), value);"), "Set", "Preferences.Default.Set"));
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(Prefix + key, value);"), "Set", "Preferences.Default.Set"));
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(other, value);"), "Set", "Preferences.Default.Set"));
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(Decorate(key), value);"), "Set", "Preferences.Default.Set"));

        // EVERY call must pass, not merely one of them: a good write beside a
        // bad one is still a store that answers under two spellings.
        Assert.False(PassesKeyThrough(
            Store("Preferences.Default.Set(key, value); Preferences.Default.Set(key + \".2\", value);"),
            "Set", "Preferences.Default.Set"));

        // A call nobody wrote passes nothing through (the reader cannot be
        // satisfied vacuously by an absent call)…
        Assert.False(PassesKeyThrough(Store("return;"), "Set", "Preferences.Default.Set"));
        // …and a call in ANOTHER method does not answer for this one.
        Assert.False(PassesKeyThrough(
            "class S { public void Set(string key, string? v) { } "
            + "void Other(string key) { Preferences.Default.Set(key, v); } }",
            "Set", "Preferences.Default.Set"));

        // The two lexical escapes: the parameter must keep its NAME…
        Assert.True(DeclaresKeyParameter(Store("return;"), "Set"));
        Assert.False(DeclaresKeyParameter(
            "class S { public void Set(string other, string? value) { } }", "Set"));
        // …and must never be REASSIGNED under it.
        Assert.True(Reassigns(Store("key = key + \".write\"; Preferences.Default.Set(key, value);"), "Set", "key"));
        Assert.True(Reassigns(Store("key += \".write\";"), "Set", "key"));
        Assert.False(Reassigns(Store("Preferences.Default.Set(key, value);"), "Set", "key"));
        // A COMPARISON is not an assignment (the reader must not cry wolf).
        Assert.False(Reassigns(Store("if (key == value) return;"), "Set", "key"));
    }

    /// <summary>
    /// THE SINGLE WRITE PATH (owner ruling, 2026-08-21): both gestures — the
    /// operator's pick and the CONNECT press — funnel through one recorder, so
    /// there is exactly ONE place that writes the store and exactly TWO that
    /// assign <c>PreferredPort</c> (the recorder, and the constructor's seed
    /// READ from the store).
    ///
    /// <para><b>Why it is pinned as source.</b> "There is no second writer" is
    /// a statement about the FILE, not about any one run: a second store write
    /// added beside the recorder would behave correctly in every test that
    /// exercises it and would silently reintroduce the class of defect this
    /// phase spent two rounds on — a preference and a stored key that can
    /// disagree. Reading an App.Core file structurally follows the precedent
    /// CouplerPolicyTests set for <c>RadioSession</c>'s locked increment: the
    /// placement is either true or false of the source, and a behavioural test
    /// cannot see it.</para>
    /// </summary>
    [Fact]
    public void ThePreference_AndTheStore_HaveExactlyOneWriterBetweenThem()
    {
        var source = DiRegistrationGuardTests.StripCommentsAndLiterals(
            File.ReadAllText(Path.Combine(FindRepoRoot(), ConnectionSettingsCode)));

        var (recorderOpen, recorderClose) = CouplerPolicyTests.MethodBody(source, "RecordPreference");
        Assert.True(recorderOpen > 0, "ConnectionSettingsViewModel declares no RecordPreference — the "
            + "single write path the owner ruling requires is gone");
        var (ctorOpen, ctorClose) = CouplerPolicyTests.MethodBody(source, "ConnectionSettingsViewModel");

        // The STORE: written in exactly one place, and that place is the recorder.
        var writes = CallSites(source, @"_settings\s*\.\s*Set\s*\(");
        Assert.True(writes.Count == 1 && writes[0] > recorderOpen && writes[0] < recorderClose,
            $"the settings store is written from {writes.Count} place(s), not from RecordPreference alone — "
            + "two writers are two spellings of what is remembered");

        // The PREFERENCE: the recorder, plus the constructor's seed, and nothing else.
        var assignments = CallSites(source, @"(?<![A-Za-z0-9_])PreferredPort\s*=(?!=)");
        Assert.Equal(2, assignments.Count);
        Assert.Contains(assignments, a => a > recorderOpen && a < recorderClose);
        Assert.Contains(assignments, a => a > ctorOpen && a < ctorClose && !(a > recorderOpen && a < recorderClose));
    }

    [Fact]
    public void TheWriterReader_CountsSites_AndKnowsWhichMethodTheyAreIn()
    {
        // Anti-vacuity partner, in the shapes the pin exists to reject.
        const string one = "class V { V() { PreferredPort = s.Get(K); } "
            + "void Pick(string p) { Record(p); } void Record(string? p) { PreferredPort = p; _settings.Set(K, p); } }";
        Assert.Single(CallSites(one, @"_settings\s*\.\s*Set\s*\("));
        Assert.Equal(2, CallSites(one, @"(?<![A-Za-z0-9_])PreferredPort\s*=(?!=)").Count);

        // A SECOND writer beside the recorder — the shape the pin forbids.
        const string two = "class V { void Claim() { PreferredPort = SelectedPort; _settings.Set(K, SelectedPort); } "
            + "void Record(string? p) { PreferredPort = p; _settings.Set(K, p); } }";
        Assert.Equal(2, CallSites(two, @"_settings\s*\.\s*Set\s*\(").Count);

        // A comparison is not an assignment, and a longer identifier is not
        // this one.
        Assert.Empty(CallSites("class V { void M() { if (PreferredPort == p) return; } }",
            @"(?<![A-Za-z0-9_])PreferredPort\s*=(?!=)"));
        Assert.Empty(CallSites("class V { void M() { LastPreferredPort = p; } }",
            @"(?<![A-Za-z0-9_])PreferredPort\s*=(?!=)"));

        // …and the body reader really does separate the two methods.
        var (open, close) = CouplerPolicyTests.MethodBody(two, "Record");
        Assert.Equal(1, CallSites(two, @"_settings\s*\.\s*Set\s*\(").Count(a => a > open && a < close));
    }

    /// <summary>Every index at which <paramref name="pattern"/> matches —
    /// the site list a "how many writers are there" pin needs.</summary>
    private static List<int> CallSites(string strippedSource, string pattern)
        => Regex.Matches(strippedSource, pattern).Select(m => m.Index).ToList();

    /// <summary>Whether EVERY <paramref name="call"/> inside
    /// <paramref name="method"/>'s own body passes the bare identifier
    /// <c>key</c> as its first argument — and that there is at least one.
    /// Whitespace between the call's own tokens is free; the ARGUMENT is
    /// anchored end to end, which is what makes this a whitelist.</summary>
    private static bool PassesKeyThrough(string strippedSource, string method, string call)
    {
        var (open, close) = CouplerPolicyTests.MethodBody(strippedSource, method);
        if (open < 0 || close < 0) return false;

        var pattern = new Regex(
            string.Join(@"\s*\.\s*", call.Split('.').Select(Regex.Escape)) + @"\s*\(");

        int found = 0;
        foreach (Match m in pattern.Matches(strippedSource))
        {
            if (m.Index <= open || m.Index >= close) continue;
            found++;
            if (FirstArgument(strippedSource, m.Index + m.Length) != "key") return false;
        }
        return found > 0;
    }

    /// <summary>The first top-level argument of a call whose open paren has
    /// just been consumed — commas nested inside brackets belong to an inner
    /// expression and do not end it.</summary>
    private static string FirstArgument(string source, int afterOpenParen)
    {
        int depth = 0;
        for (int i = afterOpenParen; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ')' && depth-- == 0) return source[afterOpenParen..i].Trim();
            else if (c == ',' && depth == 0) return source[afterOpenParen..i].Trim();
        }
        return string.Empty;
    }

    /// <summary>Whether <paramref name="method"/> declares <c>string key</c> as
    /// its FIRST parameter — the name the whitelist above anchors on.</summary>
    private static bool DeclaresKeyParameter(string strippedSource, string method)
        => Regex.IsMatch(
            strippedSource,
            @"(?<![A-Za-z0-9_])" + Regex.Escape(method) + @"\s*\(\s*string\s+key\s*[,)]");

    /// <summary>Whether <paramref name="method"/>'s body ASSIGNS to
    /// <paramref name="name"/> — <c>=</c> (but not <c>==</c>) or any compound
    /// assignment. The one way a whitelisted identifier could still carry a
    /// decorated value.</summary>
    private static bool Reassigns(string strippedSource, string method, string name)
    {
        var (open, close) = CouplerPolicyTests.MethodBody(strippedSource, method);
        if (open < 0 || close < 0) return false;

        return Regex.Matches(
                strippedSource,
                @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"\s*(?:[-+*/%|&^]=|=(?!=))")
            .Any(m => m.Index > open && m.Index < close);
    }

    [Fact]
    public void TheMethodBodyReader_TellsOneMethodFromAnother_AndSeesNothingThatIsNotThere()
    {
        // Anti-vacuity partner: the reader must be able to say NO — otherwise
        // the three assertions above are three ways of asserting nothing.
        const string sample = """
            class S
            {
                public string? Get(string key) { return Preferences.Default.Get(key, ""); }
                public void Set(string key, string? value) { Preferences.Default.Remove(key); }
            }
            """;

        Assert.True(Calls(sample, "Get", "Preferences.Default.Get"));
        Assert.True(Calls(sample, "Set", "Preferences.Default.Remove"));
        // Each call belongs to ITS OWN method…
        Assert.False(Calls(sample, "Set", "Preferences.Default.Get"));
        Assert.False(Calls(sample, "Get", "Preferences.Default.Remove"));
        // …a call nobody wrote is absent…
        Assert.False(Calls(sample, "Set", "Preferences.Default.Set"));
        // …and a method nobody declared answers for nothing.
        Assert.False(Calls(sample, "Clear", "Preferences.Default.Remove"));
        // A commented-out call is not a call (the stripper's half of the pin).
        Assert.False(Calls(
            DiRegistrationGuardTests.StripCommentsAndLiterals(
                "class S { public void Set(string k, string? v) { /* Preferences.Default.Set(k, v); */ } }"),
            "Set", "Preferences.Default.Set"));
    }

    /// <summary>Whether <paramref name="call"/> appears inside
    /// <paramref name="method"/>'s OWN brace range — structural, so a
    /// neighbouring method's call cannot answer for this one.</summary>
    private static bool Calls(string strippedSource, string method, string call)
    {
        var (open, close) = CouplerPolicyTests.MethodBody(strippedSource, method);
        if (open < 0 || close < 0) return false;

        int at = strippedSource.IndexOf(call, open, StringComparison.Ordinal);
        return at > open && at < close;
    }

    // ---- F7: the title bar ----------------------------------------------------

    [Fact]
    public void TheTitleView_IsIconThenNameThenAboutAtTheRightEdge()
    {
        var grid = Load(AppShellXaml).Descendants()
            .Single(e => e.Name.LocalName == "Shell.TitleView")
            .Elements().Single(e => e.Name.LocalName == "Grid");
        var children = grid.Elements().ToList();

        Assert.Equal(3, children.Count);
        Assert.Equal("Image", children[0].Name.LocalName);
        Assert.Equal("Label", children[1].Name.LocalName);
        Assert.Equal("Button", children[2].Name.LocalName);

        // Order in a Grid is COLUMNS, not document order — so both are pinned.
        // The icon takes the implicit column 0; the elastic column is the
        // label's, which is what pushes About to the right EDGE.
        Assert.Null(Property(children[0], "Grid.Column"));
        Assert.Equal("1", Property(children[1], "Grid.Column"));
        Assert.Equal("2", Property(children[2], "Grid.Column"));
        Assert.Equal("Auto,*,Auto", Property(grid, "ColumnDefinitions"));

        // The icon is the NAMED MauiImage, at title-bar scale.
        Assert.Equal("falconrc_icon.png", Property(children[0], "Source"));
        Assert.Equal("24", Property(children[0], "HeightRequest"));

        // The About button is LOW-PROFILE (owner ruling) and keeps the margin
        // the Connect button vacated.
        Assert.Equal("About", Property(children[2], "Text"));
        Assert.Equal("{StaticResource TitleBarFlat}", Property(children[2], "Style"));
        Assert.Equal("0,0,8,0", Property(children[2], "Margin"));
    }

    [Fact]
    public void TheIconAsset_ExistsAndIsDeclaredAsAMauiImage()
    {
        // pass-2 F2: the app icon is a MauiIcon, which produces platform
        // app-icon assets and is NOT addressable from an in-page Image Source.
        // The title bar therefore needs its own image ITEM, and this is the
        // project's first MauiImage — so the declaration is explicit and both
        // halves (declaration and file) are pinned.
        var path = Path.Combine(FindRepoRoot(), IconAsset);
        Assert.True(File.Exists(path), "the title-bar icon asset is missing: " + IconAsset);
        Assert.True(new FileInfo(path).Length > 0, "the title-bar icon asset is empty");

        var project = XDocument.Load(Path.Combine(FindRepoRoot(), AppProject));
        Assert.Contains(project.Descendants(),
            e => e.Name.LocalName == "MauiImage"
                 && (e.Attribute("Include")?.Value ?? "")
                     .EndsWith("falconrc_icon.png", StringComparison.Ordinal));
    }

    // ---- F6: the About page ---------------------------------------------------

    [Fact]
    public void TheAboutPage_RendersEveryCarriedFact_AndItsOwnVersion()
    {
        // The facts live as constants so this host can pin them byte-exact
        // (AboutContentTests). That only means anything if the PAGE actually
        // shows them — a constant nothing renders is a comment.
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), AboutXaml));

        foreach (var member in new[]
        {
            nameof(AboutContent.Description),
            nameof(AboutContent.CableHeading),
            nameof(AboutContent.CableRecommended),
            nameof(AboutContent.CableAlternate),
            nameof(AboutContent.MatingConnector),
            nameof(AboutContent.PinoutGround),
            nameof(AboutContent.PinoutTx),
            nameof(AboutContent.PinoutRx),
        })
            Assert.Contains("AboutContent." + member, markup, StringComparison.Ordinal);

        // The VERSION is the RUNNING app's, never a constant.
        var code = Code(AboutCode);
        Assert.Contains("AppInfo", code, StringComparison.Ordinal);
        Assert.Contains("VersionString", code, StringComparison.Ordinal);
        Assert.Contains("AboutContent.VersionPrefix", code, StringComparison.Ordinal);
    }

    // ---- round 13 C1: the About rework (backlog item 12) ---------------------

    [Fact]
    public void TheCreditLine_IsComposedInCodeBehind_FromThePrefixAndTheClock()
    {
        // The credit left the markup in round 13 C1. It used to be an x:Static
        // like every other fact, and the guard above was what proved the page
        // showed it; now the YEAR has to come from the clock, so the Label is
        // NAMED and the code-behind fills it — and this is the replacement
        // proof. All three halves are pinned, because any one of them missing
        // is a blank line on the page or a year frozen at build time.
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), AboutXaml));
        var code = Code(AboutCode);

        Assert.Contains("x:Name=\"CreditLabel\"", markup, StringComparison.Ordinal);
        Assert.Contains("CreditLabel.Text", code, StringComparison.Ordinal);
        Assert.Contains("AboutContent.CreditPrefix", code, StringComparison.Ordinal);
        Assert.Contains("DateTime.Now.Year", code, StringComparison.Ordinal);

        // …and it did NOT stay a constant bind: an x:Static credit would mean
        // the constant carries the year again.
        Assert.DoesNotContain("AboutContent.Credit}", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutPage_CarriesItsOwnTitleView_WithNoButtonInIt()
    {
        // THE RECURSION FIX (backlog item 12, owner 2026-08-19): the shell's
        // title view carries an About button, and an inherited one on the About
        // page pushed another About every press, without limit. A page-level
        // TitleView overrides the shell's for this page only — so the pin is
        // "About has its own, and it has no Button". AppShell keeps its button;
        // its own three-child pin above is untouched.
        var titleView = Load(AboutXaml).Descendants()
            .Single(e => e.Name.LocalName == "Shell.TitleView");

        Assert.DoesNotContain(titleView.Descendants(), e => e.Name.LocalName == "Button");

        // Anti-vacuity: an EMPTY title view would satisfy the line above and
        // leave the operator with no title at all. The page's own name is what
        // the override exists to show (Title="About" was always there — this is
        // what makes it visible).
        var grid = titleView.Elements().Single(e => e.Name.LocalName == "Grid");
        var children = grid.Elements().ToList();

        Assert.Equal("Auto,*", Property(grid, "ColumnDefinitions"));
        Assert.Equal(2, children.Count);
        Assert.Equal("Image", children[0].Name.LocalName);
        Assert.Equal("falconrc_icon.png", Property(children[0], "Source"));
        Assert.Equal("24", Property(children[0], "HeightRequest"));
        Assert.Equal("Label", children[1].Name.LocalName);
        Assert.Equal("1", Property(children[1], "Grid.Column"));
        Assert.Equal("About", Property(children[1], "Text"));
        Assert.Equal("About", Property(Load(AboutXaml).Root!, "Title"));
    }

    [Fact]
    public void TheAboutPage_HidesTheTabBar_WhileItIsUp()
    {
        // The owner's "lock out the bottom-bar buttons while on About". One
        // attached property, on the ROOT — set anywhere else it would not
        // apply, so the root is where it is read.
        Assert.Equal("False", Property(Load(AboutXaml).Root!, "Shell.TabBarIsVisible"));
    }

    [Fact]
    public void TheAboutAsset_ExistsAndIsDeclaredAsAMauiImage_AndThePageRendersIt()
    {
        // Same idiom as the title-bar icon above, and for the same reason: this
        // project deliberately has NO implicit Resources\Images glob, so an
        // asset that is committed but undeclared silently renders as nothing.
        // Three halves, all pinned: the file, the build item, the <Image>.
        var path = Path.Combine(FindRepoRoot(), AboutAsset);
        Assert.True(File.Exists(path), "the About picture asset is missing: " + AboutAsset);
        Assert.True(new FileInfo(path).Length > 0, "the About picture asset is empty");

        var project = XDocument.Load(Path.Combine(FindRepoRoot(), AppProject));
        Assert.Contains(project.Descendants(),
            e => e.Name.LocalName == "MauiImage"
                 && (e.Attribute("Include")?.Value ?? "")
                     .EndsWith("falconrc_about.png", StringComparison.Ordinal));

        var image = Load(AboutXaml).Descendants()
            .Single(e => e.Name.LocalName == "Image"
                         && Property(e, "Source") == "falconrc_about.png");

        // AspectFit + a MAXIMUM (not a fixed) width: the old box rendered it at
        // 507, and this must not upscale past that on a desktop nor overflow a
        // phone.
        Assert.Equal("AspectFit", Property(image, "Aspect"));
        Assert.Equal("507", Property(image, "MaximumWidthRequest"));
        Assert.Null(Property(image, "WidthRequest"));
    }

    [Fact]
    public void TheAboutPage_RootStack_FillsThePageWidth()
    {
        // The SettingsPage root-fill pin's About twin (same 2026-08-23 owner
        // ask): the cards span the page on every idiom, while the photo keeps
        // its OWN 507 maximum (pinned above) so it never upscales.
        var stack = Load(AboutXaml).Descendants()
            .Single(e => e.Name.LocalName == "VerticalStackLayout"
                         && e.Elements().Any(c => c.Name.LocalName == "Border"));

        Assert.Equal("Fill", Property(stack, "HorizontalOptions"));
    }

    [Fact]
    public void AboutIsARoutedPage_NotAFifthTab()
    {
        // The owner's ruling that superseded the earlier 5th-tab shape. Pinned
        // from both directions: four tabs, and a registered route.
        Assert.Equal(4, Load(AppShellXaml).Descendants().Count(e => e.Name.LocalName == "ShellContent"));
        Assert.Contains("Routing.RegisterRoute", Code(AppShellCode), StringComparison.Ordinal);
    }

    // ---- Anti-vacuity ---------------------------------------------------------

    [Fact]
    public void TheCommentStripper_HidesCommentsWithoutEatingRouteStrings()
    {
        // Every C# assertion above is "the source contains this line", so the
        // reader must be able to MISS: a commented-out registration is a
        // deleted one. And it must NOT mistake a route literal's `//` for the
        // start of a comment — which is the one thing this particular file
        // reads that the house stripper (which drops literals entirely) could
        // not have told it.
        const string source = """
            public static class Sample
            {
                public static void Wire()
                {
                    Go("//operate");            // real
                    // Go("//commented");
                    /* Go("//blockCommented"); */
                }
            }
            """;

        var stripped = StripComments(source);

        Assert.Contains("Go(\"//operate\");", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("//commented", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("//blockCommented", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkupReader_SeesBothWaysAPropertyCanBeSet_AndReportsUnsetAsNull()
    {
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <Button HorizontalOptions="Fill" />
              <Button><Button.HorizontalOptions>Fill</Button.HorizontalOptions></Button>
              <Button />
            </ContentView>
            """);

        var buttons = markup.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal("Fill", Property(buttons[0], "HorizontalOptions"));
        Assert.Equal("Fill", Property(buttons[1], "HorizontalOptions"));
        Assert.Null(Property(buttons[2], "HorizontalOptions"));
    }

    // ---- readers ---------------------------------------------------------------

    /// <summary>A property set as an attribute or as a property ELEMENT (the
    /// house rule — a XAML property has two spellings and only a parser sees
    /// both).</summary>
    private static string? Property(XElement e, string property)
        => e.Attribute(property)?.Value
           ?? e.Elements()
               .FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + property)?.Value;

    /// <summary>C# source with COMMENTS removed and string literals KEPT.
    /// The house stripper (DiRegistrationGuardTests) drops literals too, which
    /// is right for a registration scan and wrong here: the route strings ARE
    /// the contract. Written as one left-to-right pass so the states cannot
    /// fool each other — a <c>//</c> inside a string is not a comment.</summary>
    internal static string StripComments(string source)
    {
        var kept = new StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
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
            if (c == '@' && next == '"')
            {
                kept.Append(c).Append(next);
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { kept.Append("\"\""); i += 2; continue; }
                        kept.Append('"');
                        i++;
                        break;
                    }
                    kept.Append(source[i]);
                    i++;
                }
                continue;
            }
            if (c == '"')
            {
                kept.Append(c);
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        kept.Append(source[i]).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    kept.Append(source[i]);
                    if (source[i] == '"') { i++; break; }
                    i++;
                }
                continue;
            }

            kept.Append(c);
            i++;
        }

        return kept.ToString();
    }

    private static string Code(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "source missing: " + relative);
        return StripComments(File.ReadAllText(path));
    }

    private static XDocument Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path);
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
