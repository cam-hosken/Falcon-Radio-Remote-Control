using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// UI-tweaks round 4 (AE) — the ACTION-CLASS guard for Refresh buttons, in
/// the house source-scan style (GuiOutScopeGuardTests / AppScopeGuardTests):
/// reflection cannot see a XAML attribute, so the pin reads the markup.
///
/// <para><b>Why this exists.</b> Round 3 pinned the HOP- and SSB-settings
/// Refresh buttons to the shared <c>SegmentWidth</c> (72 dp). "Refresh" needs
/// about 78, so the trailing "h" was clipped — an OWNER-REPORTED defect. The
/// round-3 rule already said a button whose text does not fit
/// <c>SegmentWidth</c> is ACTION-class and sizes naturally; round 4 applied
/// it. Nothing enforced it: a round-4 audit re-added the WidthRequest and the
/// whole App suite stayed green, so the fix could silently regress.</para>
///
/// <para><b>Why this parses XML instead of matching text.</b> The first
/// version of this guard regexed each Button's OPENING TAG, and the round-2
/// audit walked straight through it: rewriting the pin as the equivalent
/// PROPERTY-ELEMENT form —
/// <c>&lt;Button.WidthRequest&gt;72&lt;/Button.WidthRequest&gt;</c> — brought
/// the clipping back with the guard still green. XAML is well-formed XML and
/// a property can be set two ways, so no tag-shaped regex can be right here.
/// <see cref="XDocument"/> sees both forms, and it also ignores commented-out
/// markup for free (an XML comment is not an element).</para>
///
/// <para>The scan covers EVERY Refresh button in the app layer, not just the
/// two that were clipped — the same mistake on the ALE, device or port
/// Refresh would clip the same way.</para>
///
/// <para><b>ACCEPTED LIMITATION (owner deferral, 2026-08-12).</b> This guard
/// catches every ACCIDENT class demonstrated across three audit rounds
/// (attribute form, property-element form, commented-out markup). It does
/// NOT catch a width supplied indirectly — e.g. an inline/implicit Style
/// setter, a trigger, or code-behind — which a round-3 audit demonstrated.
/// That is adversarial construction, not a plausible regression; chasing it
/// is an unwinnable arms race for a scan. The backstops are code review /
/// the audit seat and the on-device bench width checks.</para>
/// </summary>
public class RefreshButtonWidthGuardTests
{
    private const string WidthRequest = "WidthRequest";

    [Fact]
    public void RefreshButtons_AreActionClass_AndNeverPinAWidth()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in AppXamlFiles(root))
            foreach (var button in RefreshButtons(file))
                if (PinsAWidth(button, out string how))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: Refresh button sets {WidthRequest} ({how})");

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheGuard_ActuallySeesTheFourSurvivingRefreshButtons()
    {
        // A source-scan guard that matches NOTHING passes vacuously, so the
        // anti-vacuity half names buttons that must EXIST.
        // ROUND 9 retargeted it once (the HOP settings pane's Refresh was
        // deleted by the read doctrine). ROUND 10 §6 rationalized the family
        // again — the ALE settings Refresh is DELETED too — so this retargets
        // to the CLOSED SURVIVING SET: the pane/list Refresh buttons the app
        // still has, each named for its scope. The retarget lands in the SAME
        // phase as the deletion (invariant 5).
        // ROUND 15 §16 F-5 makes the set FOUR: the ALE pane's LQA card gains
        // "Refresh LQA" (the schedule re-read), scope-named like the other
        // three and ACTION class like them. Round 15 G deletes the same pane's
        // old Stations "Refresh" — which was never in this set, because it was
        // not scope-named; AlePaneMarkupGuardTests owns that absence.
        var root = FindRepoRoot();

        var survivors = new (string Relative, string Text)[]
        {
            (Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml"),
                "Refresh SSB settings"),
            (Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml"),
                "Refresh channels"),
            (Path.Combine("src", "Falcon.App", "Views", "RadioSettingsPage.xaml"),
                "Refresh device settings"),
            (Path.Combine("src", "Falcon.App", "Views", "OperateParts", "AlePaneView.xaml"),
                "Refresh LQA"),
        };

        foreach (var (relative, text) in survivors)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), "markup missing: " + relative);

            var button = Assert.Single(RefreshButtons(path), b => TextOf(b) == text);
            Assert.False(PinsAWidth(button, out _), text + " must stay ACTION-class");
        }
    }

    [Fact]
    public void TheAleSettingsPane_HasNoRefreshButton_AndBindsNoRefreshCommand()
    {
        // ROUND 10 §6's ABSENCE pin. The nine ALE settings arrive in ONE SH
        // that the pane already sends lazily once per session, and all four
        // cards on it re-read their own target on every landing, so the button
        // answered a question nothing was still asking. A deletion with no pin
        // is a deletion that comes back — and a binding left pointing at the
        // deleted command would resolve to nothing SILENTLY in MAUI.
        var pane = Path.Combine(FindRepoRoot(),
            "src", "Falcon.App", "Views", "SettingsParts", "AleSettingsPaneView.xaml");
        Assert.True(File.Exists(pane));

        var document = XDocument.Load(pane);

        Assert.Empty(ButtonsIn(document).Where(IsRefreshButton));
        Assert.DoesNotContain(BindingTexts(document),
            t => t.Contains("RefreshSettingsCommand", StringComparison.Ordinal));

        // Anti-vacuity, both halves: the SAME readers, on the SAME pane, do
        // see the buttons and bindings that survived — so this is not a scan
        // that reads nothing.
        Assert.NotEmpty(ButtonsIn(document));
        Assert.Contains(BindingTexts(document),
            t => t.Contains("SetAllCallCommand", StringComparison.Ordinal));
    }

    // ---- The §3 ACTION-CLASS manifest (round 10) ----------------------------

    /// <summary>
    /// §3's ACTION class, as a CLOSED MANIFEST keyed by STABLE IDENTITY: a
    /// command binding where there is one, a SemanticProperties.Description
    /// where the Text is dynamic, and a literal Text only where the text is
    /// static and unique. Every one of these sizes NATURALLY — an action
    /// button says what it does, and a width pin is what clipped "Refresh" in
    /// the first place (round-4 AE).
    ///
    /// <para>The manifest is CLOSED (invariant 3): these controls and no
    /// others. Round 10 removed two entries with the markup that carried them
    /// — HOP's inline Proceed and Cancel went with the §5 popup rework — and
    /// added none. Round 11 §7 ADDS two, with the markup that carries them: the
    /// new exclusion-bands section's Add and per-row Remove. CLONE ROUND 12 §6
    /// RE-KEYS one (the Connect toggle moved file, AppShell → SettingsPage,
    /// keeping its no-WidthRequest rule) and ADDS one (the title bar's About,
    /// keyed by description because it has no command).</para>
    /// </summary>
    public static TheoryData<string, string, string> ActionClassButtons => new()
    {
        // file · how to find it · the identity value
        // ROUND 15 E-D2 (F54): Program and Add member became ONE action seat
        // whose TEXT switches with the kind, so the manifest's two rows became
        // one and the count dropped by one.
        { AddressCard, "Command", "{Binding ActionCommand}" },                    // Program / Add
        { SsbSettings, "Command", "{Binding StoreCommand}" },                     // Store (channels)
        { ModemCard, "Command", "{Binding StoreCommand}" },                       // Store (presets)
        { ClockView, "Command", "{Binding SetTimeFromDeviceCommand}" },           // Set from device clock
        { AddressCard, "Command", "{Binding EraseCommand}" },                     // Erase
        { HopSettings, "Command", "{Binding AddListFrequenciesCommand}" },        // Add (HOP)
        { GroupsCard, "Command", "{Binding AddChannelCommand}" },                 // Add (groups card)
        { AddressCard, "Command", "{Binding Delete}" },                           // Delete (book row)
        { GroupsCard, "Command", "{Binding Remove}" },                            // Remove (group channel)
        { HopSettings, "Command", "{Binding Remove}" },                           // Remove (hop frequency)
        { HopSettings, "Command", "{Binding RequestNetWipeCommand}" },            // Clear net
        // Round 11 §7 (R11/X9): the NEW exclusion-bands section's two buttons.
        // Both are ACTION class — "Remove" and "Add" say what they do and size
        // naturally — and both are keyed by their OWN command identity rather
        // than sharing the hop-frequency row's, so each is guarded separately.
        { HopSettings, "Command", "{Binding RemoveBand}" },                       // Remove (exclusion band)
        { HopSettings, "Command", "{Binding AddExcludeBandCommand}" },            // Add (exclusion band)
        { HopPane, "Command", "{Binding Hop.SelectPickedNetCommand}" },           // Select Net
        { HopPane, "Command", "{Binding Hop.SendSyncCommand}" },                  // Send Sync
        { AlePane, "Command", "{Binding Ale.Lqa.RequestReportCommand}" },         // Request report
        // CLONE ROUND 12 §6 F2: the Connect ⇄ Disconnect toggle MOVED —
        // AppShell's title bar → the Connection settings page. The row moves
        // with it and KEEPS its no-WidthRequest rule: full width there is
        // HorizontalOptions="Fill", which this guard permits and does not
        // inspect (SettingsPage's own structural pin in
        // ConnectionFlowSourceGuardTests carries that half).
        { ConnectionPage, "Command", "{Binding ToggleCommand}" },                 // Connect ⇄ Disconnect
        // §6 F7: the title bar's NEW About button, keyed by its description
        // (it has no command — it calls INavigator through a Clicked handler).
        // ACTION class, natural-sizing: the word sizes it.
        { AppShell, "SemanticProperties.Description", "Open the About page" },    // About
        // The Refresh family — the three §6 survivors plus the row-scoped port one.
        { SsbSettings, "Command", "{Binding RefreshSettingsCommand}" },
        { SsbSettings, "Command", "{Binding RefreshChannelsCommand}" },
        { RadioPage, "Command", "{Binding RefreshDeviceSettingsCommand}" },
        { ConnectionPage, "Command", "{Binding RefreshPortsCommand}" },
        { RadioPage, "Command", "{Binding TogglePauseCommand}" },                 // Pause/Resume
        // Dynamic / handler-driven texts, keyed by their description instead.
        { RadioPage, "SemanticProperties.Description",
            "Copy the visible console log to the clipboard" },                    // Copy
        // D17 (2026-08-30): the console's ONE platform-split export button
        // ("Share…" on Android / "Save" on Windows, keyed by the description
        // "Export the full console log as a text file") is REPLACED by the
        // Cloning card's PAIR. Two entries in, one out — both ACTION class
        // like the button they replace, because they live in the same wrapping
        // toolbar and size by their words.
        { RadioPage, "SemanticProperties.Description",
            "Choose where to save the console log" },                             // Store file…
        { RadioPage, "SemanticProperties.Description",
            "Send the console log to another app" },                              // Share…
        // D18 (2026-08-30): the console's GATED RAW INPUT adds two presses to
        // the same wrapping toolbar, both ACTION class for the same reason as
        // the four already there — the word sizes them, and a width pin is
        // what clips a toggle whose text grows from "Send" to "Disable input".
        // The toggle is keyed by its DESCRIPTION (its Text flips with the
        // gate); Send is keyed by its command.
        { RadioPage, "SemanticProperties.Description",
            "Arm or disarm the console command input" },                          // Enable / Disable input
        { RadioPage, "Command", "{Binding SendCommand}" },                        // Send
    };

    /// <summary>§3's action list, ROLE by role, against the manifest above. The
    /// theory proves each LISTED control sizes naturally; this proves the LIST
    /// is the plan's list — the failure mode a per-entry theory cannot see is an
    /// entry quietly missing, which makes the whole role unguarded.
    ///
    /// <para>Proven the hard way (audit round 1, MAJOR 3): the first version of
    /// this manifest omitted Delete, Remove ×2, Select Net, Send Sync, Request
    /// report and Connect/Disconnect, and pinning a fixed <c>SegmentWidth</c>
    /// onto "Request report" survived all 1007 tests.</para></summary>
    [Fact]
    public void TheActionManifest_CoversEverySectionThreeRole_AndNothingIsUnkeyed()
    {
        // The §3 role list, verbatim, with the identity each role is keyed by.
        (string Role, string File, string Attribute, string Identity)[] roles =
        [
            ("Program / Add", AddressCard, "Command", "{Binding ActionCommand}"),
            ("Store (channels)", SsbSettings, "Command", "{Binding StoreCommand}"),
            ("Store (presets)", ModemCard, "Command", "{Binding StoreCommand}"),
            ("Set from device clock", ClockView, "Command", "{Binding SetTimeFromDeviceCommand}"),
            ("Erase", AddressCard, "Command", "{Binding EraseCommand}"),
            ("Add (HOP)", HopSettings, "Command", "{Binding AddListFrequenciesCommand}"),
            ("Add (groups)", GroupsCard, "Command", "{Binding AddChannelCommand}"),
            ("Delete", AddressCard, "Command", "{Binding Delete}"),
            ("Remove (group channel)", GroupsCard, "Command", "{Binding Remove}"),
            ("Remove (hop frequency)", HopSettings, "Command", "{Binding Remove}"),
            ("Remove (exclusion band)", HopSettings, "Command", "{Binding RemoveBand}"),
            ("Add (exclusion band)", HopSettings, "Command", "{Binding AddExcludeBandCommand}"),
            ("Clear net", HopSettings, "Command", "{Binding RequestNetWipeCommand}"),
            ("Select Net", HopPane, "Command", "{Binding Hop.SelectPickedNetCommand}"),
            ("Send Sync", HopPane, "Command", "{Binding Hop.SendSyncCommand}"),
            ("Request report", AlePane, "Command", "{Binding Ale.Lqa.RequestReportCommand}"),
            // §6 F2 re-keys this role to the page the button moved to; §6 F7
            // adds the title bar's About in its place.
            ("Connect/Disconnect", ConnectionPage, "Command", "{Binding ToggleCommand}"),
            ("About", AppShell, "SemanticProperties.Description", "Open the About page"),
            ("Refresh SSB settings", SsbSettings, "Command", "{Binding RefreshSettingsCommand}"),
            ("Refresh channels", SsbSettings, "Command", "{Binding RefreshChannelsCommand}"),
            ("Refresh device settings", RadioPage, "Command", "{Binding RefreshDeviceSettingsCommand}"),
            ("Refresh ports", ConnectionPage, "Command", "{Binding RefreshPortsCommand}"),
            ("Pause/Resume", RadioPage, "Command", "{Binding TogglePauseCommand}"),
            ("Copy", RadioPage, "SemanticProperties.Description",
                "Copy the visible console log to the clipboard"),
            // D17: the Save/Share role SPLITS into the card's two presses.
            ("Console Store file…", RadioPage, "SemanticProperties.Description",
                "Choose where to save the console log"),
            ("Console Share…", RadioPage, "SemanticProperties.Description",
                "Send the console log to another app"),
            // D18: the gated raw-command input's two presses.
            ("Console enable input", RadioPage, "SemanticProperties.Description",
                "Arm or disarm the console command input"),
            ("Console Send", RadioPage, "Command", "{Binding SendCommand}"),
        ];

        // (1) Every role RESOLVES to a real button — a stale identity would
        // exempt that role silently.
        var missing = roles
            .Where(r => ButtonsIn(Load(r.File)).All(b => PropertyValue(b, r.Attribute) != r.Identity))
            .Select(r => r.Role)
            .ToList();
        Assert.Empty(missing);

        // (2) …and every role is IN the theory manifest, so the width check
        // above actually runs for it.
        var manifest = ActionClassButtons
            .Select(row => ((string)row[0]!, (string)row[1]!, (string)row[2]!))
            .ToHashSet();
        var unguarded = roles
            .Where(r => !manifest.Contains((r.File, r.Attribute, r.Identity)))
            .Select(r => r.Role)
            .ToList();
        Assert.Empty(unguarded);

        // (3) The manifest holds NOTHING BEYOND the role list — a closed
        // manifest is a decision, and a stray entry would widen it silently.
        Assert.Equal(roles.Length, manifest.Count);
    }

    [Fact]
    public void TheIdentityResolver_FindsNothingForAnIdentityThatIsNotThere()
    {
        // Anti-vacuity for the Assert.NotEmpty inside the theory AND for the
        // resolution check above: a reader that matched everything would make
        // both meaningless. A real file, a plausible-looking identity, no hits.
        Assert.Empty(ButtonsIn(Load(AddressCard))
            .Where(b => PropertyValue(b, "Command") == "{Binding NoSuchCommand}"));

        // …and it really does find one that IS there, in a DataTemplate — the
        // per-row commands the audit found missing live inside templates, which
        // is exactly where a shallow reader would stop looking.
        Assert.NotEmpty(ButtonsIn(Load(AddressCard))
            .Where(b => PropertyValue(b, "Command") == "{Binding Delete}"));
    }

    [Theory]
    [MemberData(nameof(ActionClassButtons))]
    public void EveryActionClassButton_SizesNaturally(string file, string attribute, string identity)
    {
        var buttons = ButtonsIn(Load(file))
            .Where(b => PropertyValue(b, attribute) == identity)
            .ToList();

        Assert.NotEmpty(buttons);
        Assert.All(buttons, b =>
            Assert.False(PinsAWidth(b, out string how),
                $"{identity} is ACTION-class (§3) and must not pin a width ({how})"));
    }

    [Fact]
    public void TheDeletedInlineConfirmButtons_AreGoneFromTheManifest_AndFromTheMarkup()
    {
        // §5 deleted the HOP inline Proceed/Cancel strip and the ALE book's
        // pending-confirm box; §3 says their manifest entries go with them.
        // Pinned from BOTH directions so the manifest and the markup cannot
        // disagree: no such button exists, and nothing binds the commands that
        // drove them.
        foreach (var file in new[] { HopSettings, AddressCard })
        {
            var document = Load(file);

            Assert.DoesNotContain(ButtonsIn(document), b => TextOf(b) == "Proceed");
            foreach (var command in new[]
            {
                "ConfirmNetWipeCommand", "CancelNetWipeCommand",
                "ConfirmDeleteCommand", "CancelDeleteCommand",
            })
                Assert.DoesNotContain(BindingTexts(document),
                    t => t.Contains(command, StringComparison.Ordinal));

            // Anti-vacuity: the readers see this file's surviving buttons.
            Assert.NotEmpty(ButtonsIn(document));
        }
    }

    [Fact]
    public void TheDePinnedSites_ReallyLostTheirWidth_AndTheSetterSitesKeptTheirs()
    {
        // §3's de-pin list, from the opposite direction: the point is not that
        // widths vanished everywhere, it is that the ACTION class lost them
        // while the SETTER/CHOICE class kept theirs. A sweep that deleted both
        // would satisfy every pin above and quietly unpick the button grid.
        var hop = Load(HopSettings);

        // Setter class, still pinned to the shared 72.
        var setButtons = ButtonsIn(hop).Where(b => TextOf(b) == "Set").ToList();
        Assert.NotEmpty(setButtons);
        Assert.All(setButtons, b =>
            Assert.Equal("{StaticResource SegmentWidth}", PropertyValue(b, "WidthRequest")));

        // …and the SETTER class on the Radio page keeps a NAMED width too.
        //
        // ROUND 11 §3 (owner ruling R6) located this at the literal "MOM"
        // button, which carried the shared SegmentWidth 72 and the wire token
        // as its CommandParameter. CLONE ROUND 12 §9 C1 replaced that pair
        // with the house ChoiceItem row — the buttons are TEMPLATED now, their
        // labels come from the VM, and the wire token never appears in this
        // markup at all. The locator therefore moves to the TEMPLATE, which is
        // the setter class's one site on this page: the point of this half of
        // the pin is that a SETTER still carries a named width, not that a
        // particular button does.
        var radioPage = Load(RadioPage);
        var choiceTemplate = radioPage.Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "ChoiceButton"));
        var templated = choiceTemplate.Descendants().Single(e => e.Name.LocalName == "Button");
        Assert.Equal("{StaticResource SegmentWidth}", PropertyValue(templated, "WidthRequest"));

        // Neither backlight LABEL is spelled in the markup any more — both
        // moved to the VM with the choices — and neither is the wire token,
        // which is what a display rename can no longer take with it
        // (invariant 4).
        foreach (var gone in new[] { "MOM", "MOMENTARY", "OFF" })
            Assert.DoesNotContain(ButtonsIn(radioPage), b => TextOf(b) == gone);
        Assert.DoesNotContain(BindingTexts(radioPage), t => t == "MOMENTARY");
    }

    // ---- file handles + readers ---------------------------------------------

    private static readonly string SsbSettings =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "SsbSettingsPaneView.xaml");
    private static readonly string HopSettings =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "HopSettingsPaneView.xaml");
    private static readonly string AddressCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleProgrammingView.xaml");
    private static readonly string GroupsCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "AleScanGroupsView.xaml");
    private static readonly string ModemCard =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "ModemPresetsView.xaml");
    private static readonly string ClockView =
        Path.Combine("src", "Falcon.App", "Views", "SettingsParts", "DeviceClockView.xaml");
    private static readonly string RadioPage =
        Path.Combine("src", "Falcon.App", "Views", "RadioSettingsPage.xaml");
    private static readonly string ConnectionPage =
        Path.Combine("src", "Falcon.App", "Views", "SettingsPage.xaml");
    private static readonly string HopPane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "HopPaneView.xaml");
    private static readonly string AlePane =
        Path.Combine("src", "Falcon.App", "Views", "OperateParts", "AlePaneView.xaml");
    private static readonly string AppShell =
        Path.Combine("src", "Falcon.App", "AppShell.xaml");

    private static XDocument Load(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "markup missing: " + relative);
        return XDocument.Load(path);
    }

    /// <summary>A property set as an attribute or as a property ELEMENT.</summary>
    private static string? PropertyValue(XElement e, string property)
        => e.Attribute(property)?.Value
           ?? e.Elements()
               .FirstOrDefault(c => c.Name.LocalName == e.Name.LocalName + "." + property)?.Value;

    private static string? TextOf(XElement e) => PropertyValue(e, "Text");

    private static IEnumerable<string> BindingTexts(XDocument document)
    {
        foreach (var e in document.Descendants())
        {
            foreach (var a in e.Attributes()) yield return a.Value;
            if (e.Name.LocalName.Contains('.', StringComparison.Ordinal) && !e.HasElements)
                yield return e.Value;
        }
    }

    [Fact]
    public void TheGuard_SeesBothWaysAPropertyCanBeSet()
    {
        // The round-2 evasion, pinned as a unit: attribute form AND
        // property-element form must BOTH register as a pinned width, and a
        // button with neither must register as clean. Without this, the only
        // proof the guard covers the property-element form is that someone
        // remembered to try it.
        var markup = XDocument.Parse(
            """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
              <VerticalStackLayout>
                <Button Text="Refresh" />
                <Button Text="Refresh" WidthRequest="72" />
                <Button Text="Refresh">
                  <Button.WidthRequest>72</Button.WidthRequest>
                </Button>
                <!-- <Button Text="Refresh" WidthRequest="72" /> -->
              </VerticalStackLayout>
            </ContentView>
            """);

        var buttons = ButtonsIn(markup).Where(IsRefreshButton).ToList();

        Assert.Equal(3, buttons.Count);                 // the commented one is not an element
        Assert.False(PinsAWidth(buttons[0], out _));    // clean
        Assert.True(PinsAWidth(buttons[1], out string attribute));
        Assert.Equal("attribute", attribute);
        Assert.True(PinsAWidth(buttons[2], out string element));
        Assert.Equal("property element", element);
    }

    // ---- The structural checks ----------------------------------------------

    /// <summary>A property can be set as an attribute (<c>WidthRequest="72"</c>)
    /// or as a property ELEMENT (<c>&lt;Button.WidthRequest&gt;</c>). Both
    /// count; missing either one is how the round-2 evasion worked.</summary>
    private static bool PinsAWidth(XElement button, out string how)
    {
        if (button.Attributes().Any(a => a.Name.LocalName == WidthRequest))
        {
            how = "attribute";
            return true;
        }
        if (button.Elements().Any(e => e.Name.LocalName == button.Name.LocalName + "." + WidthRequest))
        {
            how = "property element";
            return true;
        }
        how = "";
        return false;
    }

    /// <summary>Button ELEMENTS only — a <c>&lt;Button.Text&gt;</c> property
    /// element has local name "Button.Text", so this filter never mistakes a
    /// property element for the control itself.</summary>
    private static IEnumerable<XElement> ButtonsIn(XDocument document)
        => document.Descendants().Where(e => e.Name.LocalName == "Button");

    private static List<XElement> RefreshButtons(string xamlPath)
        => ButtonsIn(XDocument.Load(xamlPath)).Where(IsRefreshButton).ToList();

    /// <summary>Text, however it is set.</summary>
    private static bool IsRefreshButton(XElement button)
    {
        var text = button.Attribute("Text")?.Value
            ?? button.Elements()
                .FirstOrDefault(e => e.Name.LocalName == button.Name.LocalName + ".Text")?.Value;
        return text is not null && text.StartsWith("Refresh", StringComparison.Ordinal);
    }

    private static IEnumerable<string> AppXamlFiles(string root)
    {
        var layer = Path.Combine(root, "src", "Falcon.App");
        Assert.True(Directory.Exists(layer), "app directory missing: " + layer);

        foreach (var file in Directory.EnumerateFiles(layer, "*.xaml", SearchOption.AllDirectories))
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
