namespace Falcon.App.Tests;

/// <summary>
/// App-layer scope guard (Stage 5 audit F5), companion to Falcon.Core.Tests'
/// CommandSurfaceTests: the app layer (Falcon.App.Core + Falcon.App) may
/// reference <c>Prc138Radio.RawCommand</c> from ONE FILE and no other.
/// RawCommand is the OPERATOR console passthrough — bench harnesses use it
/// because the harness IS the operator (every line still LineSent-visible).
/// Source-scan style: reflection cannot see call sites without IL
/// disassembly, and a source scan also covers XAML.
///
/// <para><b>AMENDED by D18</b> (plan-clone-write-structural.md §2,
/// 2026-08-30 — owner: "what will it take to add an input to the console so
/// we can send arbitrary commands (gated behind an enable button)?" → "do
/// it"). Until D18 this file asserted ZERO app-layer references, and said in
/// so many words that "the app itself gets a Console send box only by an
/// explicit future decision, at which point this guard is consciously
/// amended". D18 is that decision, and this is that amendment: the guard
/// becomes an ALLOW-LIST of exactly one file, so the property it holds is
/// unchanged in kind — the app has ONE way to put an arbitrary line on the
/// wire, and it is the one the operator can see.</para>
///
/// <para><b>Why the surface file and not the view model.</b> The line reaches
/// the Core the way every other app-layer intent does — through a surface
/// (<c>ConsoleFeed.SendRaw</c>). Allow-listing the surface keeps the Core
/// member out of every view model and every page, which is the same shape the
/// GUI-out guard uses for the builders it releases (see
/// <c>GuiOutScopeGuardTests</c>' note on wrapper names).</para>
/// </summary>
public class AppScopeGuardTests
{
    /// <summary>THE ALLOW-LIST — repo-relative paths, exactly the files that
    /// may name <c>RawCommand</c>. Adding a row is a plan amendment, not a
    /// fix.</summary>
    private static readonly string[] AllowedCallSites =
    [
        Path.Combine("src", "Falcon.App.Core", "Surfaces", "ConsoleFeed.cs"),   // D18: ConsoleFeed.SendRaw
    ];

    [Fact]
    public void OnlyTheAllowedCallSite_ReferencesRawCommand()
    {
        var named = AppLayerFilesNaming("RawCommand");

        // (1) Nothing outside the allow-list names it. A SECOND file — a view
        // model reaching past the surface, a page code-behind, a XAML
        // x:Static — fails here, which is the whole point of the amendment.
        var offenders = named.Except(AllowedCallSites, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Empty(offenders);

        // (2) ANTI-VACUITY, the other direction: every allow-listed file really
        // does name it. A rename or a deleted send box must not leave a
        // permanent hole in this guard that nobody notices.
        var missing = AllowedCallSites.Except(named, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void TheScanner_ReallyReadsTheAppLayer_AndCanSeeANameThatIsThere()
    {
        // Anti-vacuity for the scanner itself: an enumeration that found no
        // files, or a reader that read nothing, would make the pin above pass
        // for the wrong reason. `SendRaw` is the seam's own name and lives in
        // the same file; `RawCommandThatDoesNotExist` lives nowhere.
        Assert.Contains(Path.Combine("src", "Falcon.App.Core", "Surfaces", "ConsoleFeed.cs"),
            AppLayerFilesNaming("SendRaw"));
        Assert.Empty(AppLayerFilesNaming("RawCommandThatDoesNotExist"));
    }

    /// <summary>Every app-layer .cs/.xaml file (bin/obj excluded) whose text
    /// contains <paramref name="name"/>, as repo-relative paths.</summary>
    private static List<string> AppLayerFilesNaming(string name)
    {
        var root = FindRepoRoot();
        string[] appLayers =
        [
            Path.Combine(root, "src", "Falcon.App.Core"),
            Path.Combine(root, "src", "Falcon.App"),
        ];

        var hits = new List<string>();
        foreach (var layer in appLayers)
        {
            Assert.True(Directory.Exists(layer), "app-layer directory missing: " + layer);
            foreach (var file in Directory.EnumerateFiles(layer, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = Path.GetRelativePath(root, file);
                if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                if (File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                    hits.Add(relative);
            }
        }
        return hits;
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
