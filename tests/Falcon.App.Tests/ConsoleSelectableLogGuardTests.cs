using System.Text.RegularExpressions;

namespace Falcon.App.Tests;

/// <summary>
/// D18(b) (plan-clone-write-structural.md §2, 2026-08-30 — owner: "we should
/// be able to highlight and copy console output text"): the console log's text
/// is natively selectable, and NOTHING ELSE IN THE APP IS.
///
/// <para><b>Why a source scan.</b> The mechanism is a MAUI handler mapping on
/// <c>LabelHandler.Mapper</c>, which every label in the app passes through.
/// The scoping is one line of code inside that mapping — a type test — and no
/// unit test can observe it: the mapping only runs against a live platform
/// view, which this suite has none of (it is a plain xunit assembly, not a
/// device test). What CAN be held is the shape of the thing: the mapping is
/// keyed, it is entered by every label, and it LEAVES every label but
/// <c>ConsoleLogLabel</c> exactly as it found it. The UX itself (a drag really
/// selects; the recycler does not eat it) is the bench's — docs/bench-checklist.md.</para>
///
/// <para><b>The fallback is NOT built.</b> D18 records a select-mode toggle
/// swapping in a read-only editor as the fallback IF the Android recycler
/// defeats selection on the bench. Building both would ship two ways to do one
/// thing; the absence pin below says the fallback is not here.</para>
/// </summary>
public class ConsoleSelectableLogGuardTests
{
    private static readonly string ControlPath =
        Path.Combine("src", "Falcon.App", "Controls", "ConsoleLogLabel.cs");

    [Fact]
    public void TheSelectableLabel_Exists_AndIsAPlainLabelSubclass()
    {
        var source = Read(ControlPath);

        Assert.Matches(@"public\s+class\s+ConsoleLogLabel\s*:\s*Label", source);

        // It adds NO properties of its own — the console line's styling stays
        // in the DataTemplate, and the subclass exists only to be a type the
        // mapping can test for.
        Assert.DoesNotContain("BindableProperty", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapping_IsScopedToTheConsoleLabel_AndSetsBothPlatforms()
    {
        var source = Read(ControlPath);

        // THE SCOPE, verbatim: the early return is what keeps every other label
        // in the app unchanged. Delete this line and the whole GUI's text
        // becomes selectable.
        Assert.Matches(@"if\s*\(\s*view\s+is\s+not\s+ConsoleLogLabel\s*\)\s*return\s*;", source);

        // …and it comes BEFORE either platform call, so neither can run for a
        // label that is not ours.
        int scope = source.IndexOf("is not ConsoleLogLabel", StringComparison.Ordinal);
        foreach (var call in new[] { "SetTextIsSelectable", "IsTextSelectionEnabled" })
        {
            int at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at > scope, $"{call} runs before the ConsoleLogLabel scope test");
        }

        // BOTH platforms, each behind its own conditional — D18 names them.
        Assert.Matches(@"#if\s+ANDROID", source);
        Assert.Contains("SetTextIsSelectable(true)", source, StringComparison.Ordinal);
        Assert.Matches(@"#elif\s+WINDOWS", source);
        Assert.Matches(@"IsTextSelectionEnabled\s*=\s*true", source);

        // The mapping is APPENDED under a key of its own, so a later mapping
        // replaces it deliberately instead of stacking silently.
        Assert.Matches(@"LabelHandler\.Mapper\.AppendToMapping\(\s*MappingKey\s*,", source);
    }

    [Fact]
    public void TheCompositionRoot_TurnsTheMappingOn_Once()
    {
        var maui = Read(Path.Combine("src", "Falcon.App", "MauiProgram.cs"));
        Assert.Equal(1, Count(maui, "ConsoleLogLabel.EnableTextSelection()"));
    }

    /// <summary>
    /// THE SCOPE, from the other side: no OTHER file in the app head reaches
    /// for either platform selection switch. A second call site — a global
    /// mapping, a page code-behind poking a handler — is how "the console log
    /// is selectable" quietly becomes "everything is".
    /// </summary>
    [Fact]
    public void NoOtherAppFile_MakesTextSelectable()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "Falcon.App"), "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            if (relative.Equals(ControlPath, StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            if (text.Contains("SetTextIsSelectable", StringComparison.Ordinal)
                || text.Contains("IsTextSelectionEnabled", StringComparison.Ordinal))
                offenders.Add(relative);
        }
        Assert.Empty(offenders);

        // ANTI-VACUITY: the scanner really does read this tree and really can
        // see those names where they ARE.
        Assert.Contains("SetTextIsSelectable", Read(ControlPath), StringComparison.Ordinal);
    }

    /// <summary>D18's recorded FALLBACK — a select-mode toggle swapping the log
    /// CollectionView for a read-only Editor — is the alternative to the
    /// handler, not a companion to it. Pinned absent so a later round chooses
    /// one and says so, instead of accumulating both.</summary>
    [Fact]
    public void TheRecordedFallback_IsNotAlsoBuilt()
    {
        var page = Read(Path.Combine("src", "Falcon.App", "Views", "RadioSettingsPage.xaml"));
        Assert.DoesNotContain("<Editor", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectMode", page, StringComparison.Ordinal);

        // Anti-vacuity: the log really is still the CollectionView the fallback
        // would have replaced.
        Assert.Contains("<CollectionView", page, StringComparison.Ordinal);
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "missing: " + relative);
        return File.ReadAllText(path);
    }

    private static int Count(string haystack, string needle)
        => Regex.Matches(haystack, Regex.Escape(needle)).Count;

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
