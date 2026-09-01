using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Falcon.App.Tests;

/// <summary>
/// F4 (plan-clone-field-round2.md §3.7) — <b>the class of defect that took the
/// whole Mode settings page down, made structural.</b>
///
/// <para>Round 14 B copied a coupler row into <c>HopSettingsPaneView.xaml</c>
/// together with its <c>{StaticResource ChoiceButton}</c> binding. The template
/// that key names exists only in <c>SsbSettingsPaneView.xaml</c>'s OWN resource
/// dictionary (and separately in <c>RadioSettingsPage.xaml</c>'s). A
/// <c>ContentView</c> resolves a <c>StaticResource</c> when its XAML is loaded,
/// against its own dictionary and the application's — a SIBLING view's
/// resources are not in that chain — and <c>ModeSettingsPage</c> constructs all
/// three panes eagerly, so entering Mode settings threw
/// <c>XamlParseException: Position 477:52. StaticResource not found for key
/// ChoiceButton</c> in all three modes (crash buffer, 2026-08-21: three
/// identical frames at <c>ModeSettingsPage.InitializeComponent</c>).</para>
///
/// <para><b>What this pins</b>: for EVERY <c>.xaml</c> under
/// <c>src/Falcon.App/Views/**</c>, every <c>{StaticResource K}</c> resolves to
/// an <c>x:Key="K"</c> defined in the SAME file or in <c>App.xaml</c>'s
/// dictionary (plus any dictionary it merges by <c>Source=</c>). That is the
/// runtime's real lookup chain for these views, so the guard fails exactly when
/// the app would throw.</para>
///
/// <para><b>Structural, never raw text</b> (the round-2 Refresh-width lesson,
/// and the reason the fix's own explanatory comment — which quotes the dead key
/// verbatim — does not fool it): the XAML is parsed as XML and both the
/// ATTRIBUTE form (<c>Style="{StaticResource Card}"</c>) and the
/// PROPERTY-ELEMENT form (<c>&lt;Button.Style&gt;{StaticResource Card}
/// &lt;/Button.Style&gt;</c>) are read, along with the explicit
/// <c>{StaticResource Key=K}</c> spelling. Comments and CDATA are not markup
/// and are not scanned.</para>
/// </summary>
public class XamlResourceGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2009/xaml";

    /// <summary>Both spellings the MAUI markup extension accepts: the positional
    /// <c>{StaticResource Key}</c> and the named <c>{StaticResource Key=Key}</c>.
    /// One value can carry several (a <c>Setter</c> chain, an <c>OnIdiom</c>).</summary>
    private static readonly Regex Reference = new(
        @"\{\s*StaticResource\s+(?:Key\s*=\s*)?([A-Za-z_][A-Za-z0-9_.]*)\s*\}",
        RegexOptions.Compiled);

    [Fact]
    public void EveryStaticResourceInEveryView_ResolvesInItsOwnFileOrInAppXaml()
    {
        var appKeys = ApplicationKeys();
        var unresolved = new List<string>();
        var resolved = new List<string>();

        foreach (var view in ViewFiles())
        {
            var root = XDocument.Load(view).Root!;
            var ownKeys = KeysIn(root);
            foreach (var (key, where) in ReferencesIn(root))
            {
                if (ownKeys.Contains(key) || appKeys.Contains(key)) { resolved.Add(key); continue; }
                unresolved.Add($"{Path.GetFileName(view)}: {{StaticResource {key}}} on <{where}> "
                    + "is defined neither in that file nor in App.xaml");
            }
        }

        // Assert.True rather than Assert.Empty so the failure NAMES the file,
        // the key and the element — the crash log gave us all three, and a guard
        // that says only "collection was not empty" makes the next reader repeat
        // the search by hand.
        Assert.True(unresolved.Count == 0, string.Join(Environment.NewLine, unresolved));

        // ANTI-VACUITY, both halves. A scan that read nothing — a broken regex,
        // a wrong Views path, an XML reader that skipped attributes — would pass
        // the emptiness check above forever. The second half names the one
        // reference this guard was written for, so a scan that stopped seeing
        // the HOP pane in particular fails here rather than passing quietly.
        Assert.True(resolved.Count >= 50,
            $"the scan resolved only {resolved.Count} StaticResource references");
        Assert.Contains("HopChoiceButton", resolved);
    }

    /// <summary>The SELF-PIN: the very reference the crash was about is one the
    /// scan really sees, and it resolves — in the HOP pane's own resources, per
    /// decision A-2 (a per-view copy, no shared dictionary).</summary>
    [Fact]
    public void TheHopSettingsPane_OwnsTheChoiceTemplateItBinds()
    {
        var pane = Path.Combine(FindRepoRoot(),
            "src", "Falcon.App", "Views", "SettingsParts", "HopSettingsPaneView.xaml");
        var root = XDocument.Load(pane).Root!;

        var references = ReferencesIn(root).Select(r => r.Key).ToList();
        Assert.Contains("HopChoiceButton", references);
        Assert.Contains("HopChoiceButton", KeysIn(root));

        // …and the dead key is gone from the MARKUP. It still appears in the
        // fix's explanatory comment, which is precisely why this is an XML scan
        // and not a grep.
        Assert.DoesNotContain("ChoiceButton", references);
        Assert.Contains("{StaticResource ChoiceButton}", File.ReadAllText(pane));

        // No shared dictionary was introduced: App.xaml does not define it.
        Assert.DoesNotContain("HopChoiceButton", ApplicationKeys());
    }

    /// <summary>
    /// <b>THE SCANNER'S OWN FIXTURE.</b> The two tests above run over the
    /// repository's real markup, which today happens to write every
    /// <c>StaticResource</c> as an ATTRIBUTE — so audit round 1 emptied the
    /// property-element branch and both of them stayed green. A guard whose
    /// second half nothing exercises is a guard with a hole exactly where the
    /// round-2 Refresh-width lesson said one would be.
    ///
    /// <para>So this drives the SAME <see cref="ReferencesIn"/> over markup
    /// written in-test, covering every spelling the scanner claims: the
    /// attribute form, the property-element text node, a <c>Setter.Value</c>
    /// (property element nested one deeper), the explicit <c>Key=</c> spelling,
    /// and two in one attribute. It also pins what must NOT be seen — a key
    /// inside an XML COMMENT — because that is what makes an XML scan the right
    /// tool and a grep the wrong one, and it is not hypothetical: the F4 fix's
    /// own comment quotes the dead key verbatim.</para>
    /// </summary>
    [Fact]
    public void TheScanner_ReadsBothXamlSpellings_AndIgnoresComments()
    {
        const string markup = """
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <!-- {StaticResource FromAComment} must never be seen -->
              <VerticalStackLayout>
                <Label Style="{StaticResource FromAnAttribute}" />
                <Label>
                  <Label.Style>{StaticResource FromAPropertyElement}</Label.Style>
                </Label>
                <Button>
                  <Button.Triggers>
                    <DataTrigger TargetType="Button">
                      <Setter Property="BackgroundColor">
                        <Setter.Value>{StaticResource FromASetterValue}</Setter.Value>
                      </Setter>
                    </DataTrigger>
                  </Button.Triggers>
                </Button>
                <Border Style="{StaticResource Key=FromTheNamedSpelling}"
                        Padding="{StaticResource PadOne},{StaticResource PadTwo}" />
              </VerticalStackLayout>
            </ContentView>
            """;

        var found = ReferencesIn(XDocument.Parse(markup).Root!).Select(r => r.Key).ToList();

        Assert.Contains("FromAnAttribute", found);
        Assert.Contains("FromAPropertyElement", found);
        Assert.Contains("FromASetterValue", found);
        Assert.Contains("FromTheNamedSpelling", found);
        Assert.Contains("PadOne", found);
        Assert.Contains("PadTwo", found);
        Assert.DoesNotContain("FromAComment", found);
        Assert.Equal(6, found.Count);

        // …and the KEY reader sees keys in both places they can be written.
        var keys = KeysIn(XDocument.Parse("""
            <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <ContentView.Resources>
                <x:Double x:Key="ATopLevelKey">4</x:Double>
                <Style x:Key="ANestedKey" TargetType="Button" />
              </ContentView.Resources>
            </ContentView>
            """).Root!);
        Assert.Equal(["ANestedKey", "ATopLevelKey"], keys.Order());
    }

    // ---- the scan -----------------------------------------------------------

    private static IEnumerable<string> ViewFiles()
        => Directory.EnumerateFiles(
            Path.Combine(FindRepoRoot(), "src", "Falcon.App", "Views"),
            "*.xaml", SearchOption.AllDirectories).Order();

    /// <summary>The application dictionary's keys — <c>App.xaml</c>'s own plus
    /// every dictionary it merges by <c>Source=</c>. There are no merged sources
    /// today; the walk exists so that adding one does not silently make this
    /// guard wrong.</summary>
    private static HashSet<string> ApplicationKeys()
    {
        var app = Path.Combine(FindRepoRoot(), "src", "Falcon.App", "App.xaml");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([app]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var path = pending.Dequeue();
            if (!seen.Add(path) || !File.Exists(path)) continue;
            var root = XDocument.Load(path).Root!;
            foreach (var key in KeysIn(root)) keys.Add(key);

            foreach (var merged in root.Descendants()
                .Where(e => e.Name.LocalName == "ResourceDictionary")
                .Select(e => e.Attribute("Source")?.Value)
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var relative = merged!.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
                pending.Enqueue(Path.Combine(FindRepoRoot(), "src", "Falcon.App", relative));
            }
        }
        return keys;
    }

    private static HashSet<string> KeysIn(XElement root)
        => [.. root.DescendantsAndSelf()
            .Select(e => e.Attribute(Xaml + "Key")?.Value)
            .Where(k => k is not null)
            .Select(k => k!)];

    /// <summary>Every <c>{StaticResource …}</c> in the MARKUP: attribute values
    /// and property-element text alike. <see cref="XDocument"/> drops comments
    /// from this walk by construction — they are <c>XComment</c> nodes, not
    /// attributes and not <c>XText</c> inside an element's value.</summary>
    private static List<(string Key, string Where)> ReferencesIn(XElement root)
    {
        var found = new List<(string, string)>();
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
                foreach (Match m in Reference.Matches(attribute.Value))
                    found.Add((m.Groups[1].Value, $"{element.Name.LocalName} {attribute.Name.LocalName}"));

            // The PROPERTY-ELEMENT form: <Label.Style>{StaticResource X}</Label.Style>.
            // Only direct text nodes count — a child element's text belongs to
            // that child and is picked up on its own pass.
            foreach (var text in element.Nodes().OfType<XText>())
                foreach (Match m in Reference.Matches(text.Value))
                    found.Add((m.Groups[1].Value, element.Name.LocalName));
        }
        return found;
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
