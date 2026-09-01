using Falcon.App.Core.Cloning;

namespace Falcon.App.Tests;

/// <summary>
/// THE COMPLETENESS PIN for validation-by-enumeration
/// (plan/plan-clone-file-validation.md §2, executed by clone round 12 P2).
///
/// <para><b>What it is for.</b> Three consecutive P6 audit rounds each found
/// ONE route by which bad file content reached the wire after the erase. The
/// cure was never another discovery round: it was to stop asking "what hole did
/// the auditor find" and require every FIELD to prove something. This is the
/// machine half of that — a field added later FAILS here until somebody
/// dispositions it.</para>
///
/// <para><b>Accepted limitation, stated:</b> a machine holds COMPLETENESS and
/// AGREEMENT, not CORRECTNESS. Whether a disposition is RIGHT is a review
/// property, which is why each row carries its proof in words.</para>
/// </summary>
public class CloneFileValidationTests
{
    [Fact]
    public void EveryFieldOfTheFilesTypeGraph_IsDispositioned()
    {
        var walked = CloneFileValidation.WalkFields();
        var dispositioned = CloneFileValidation.Dispositions
            .Select(r => r.Field)
            .ToHashSet(StringComparer.Ordinal);

        var missing = walked.Where(f => !dispositioned.Contains(f)).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "these clone-file fields have no disposition: " + string.Join(" / ", missing));
    }

    [Fact]
    public void NoDisposition_IsForAFieldTheGraphNoLongerHas()
    {
        // The other direction: a stale row would keep the pin above green after
        // the model had moved on — the exact way a disposition table rots.
        var walked = CloneFileValidation.WalkFields().ToHashSet(StringComparer.Ordinal);
        var stale = CloneFileValidation.Dispositions
            .Select(r => r.Field)
            .Where(f => !walked.Contains(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "these dispositions name fields the clone file no longer has: " + string.Join(" / ", stale));
    }

    [Fact]
    public void TheDispositionTable_HasNoDuplicateRows_AndEveryProofIsAProof()
    {
        var keys = CloneFileValidation.Dispositions.Select(r => r.Field).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        foreach (var rule in CloneFileValidation.Dispositions)
        {
            Assert.True(Enum.IsDefined(rule.Disposition), rule.Field + " has no disposition");
            Assert.True(rule.Proof.Length > 20, "the proof for " + rule.Field + " is not a proof");
        }
    }

    /// <summary>
    /// ANTI-VACUITY for the walker itself (plan-clone-file-validation §2): the
    /// pins above are worth exactly what the walk can see.
    /// </summary>
    [Fact]
    public void TheWalker_SeesAKnownNumberOfLeaves_AndFollowsATypeItHasNeverSeen()
    {
        var walked = CloneFileValidation.WalkFields();

        // 1. A KNOWN COUNT. A walker that silently stopped following edges — or
        //    started skipping properties — would leave the completeness pin
        //    green while covering half the model.
        // 72 since round 17 F6 added `CloneFile.LoadNotices` (INERT); 73 since
        // round 17's clone-write-structural round added
        // `CloneFile.DefaultChannelsElided` (BOUNDED, D4/D6). Note what did NOT
        // move it: `CloneChannel.IsFactoryDefault` is a METHOD, so the walk
        // does not see it and the serializer does not write it — the
        // `CloneModemPreset.NameToken` rule, applied again.
        Assert.Equal(73, walked.Count);
        Assert.Equal(walked.Count, walked.Distinct(StringComparer.Ordinal).Count());

        // 2. The named landmarks: one scalar, one collection-of-scalars, one
        //    collection-of-rows, one ROW REACHED ONLY THROUGH a collection, and
        //    one reached only through the nested lockout object — each a
        //    different way for the recursion to have gone wrong.
        Assert.Contains("CloneFile.Version", walked);
        Assert.Contains("CloneNet.Members", walked);
        Assert.Contains("CloneFile.Channels", walked);
        Assert.Contains("CloneChannel.RxOnly", walked);
        Assert.Contains("CloneLockout.Item", walked);

        // 3. A COMPUTED projection is NOT a field: it stores nothing and its
        //    inputs are dispositioned already.
        Assert.DoesNotContain("CloneFile.ManifestDomains", walked);
        Assert.DoesNotContain("CloneFile.IncompleteDomains", walked);

        // 4. THE DELIBERATELY-ADDED TEST-ONLY TYPE (the §2 anti-vacuity clause,
        //    verbatim). A row type nobody has dispositioned must be FOUND, and
        //    found THROUGH a collection — the shape every real domain uses.
        var probe = CloneFileValidation.WalkFields(typeof(UndispositionedProbe));
        Assert.Contains("UndispositionedProbe.Rows", probe);
        Assert.Contains("CloneAddress.Name", probe);
        Assert.DoesNotContain(
            "UndispositionedProbe.Rows",
            CloneFileValidation.Dispositions.Select(r => r.Field));
    }

    /// <summary>A type the disposition table has never heard of, reached the
    /// way every real domain is — through a list. It lives in the TEST
    /// assembly, so the walker follows it only because it follows the property
    /// type, never because of a hand-kept list.</summary>
    private sealed class UndispositionedProbe
    {
        public List<CloneAddress> Rows { get; set; } = [];
    }

    [Fact]
    public void TheArchitectureDoc_CarriesEveryDispositionRow()
    {
        // DELIVERABLE: the table IN THE DOC (plan §5). Pinned against the CODE
        // — the DocsGuardTests idiom — so the document cannot describe a
        // validation surface the app does not have.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "software-architecture.md"));

        foreach (var rule in CloneFileValidation.Dispositions)
            Assert.True(doc.Contains("| `" + rule.Field + "` |", StringComparison.Ordinal),
                "software-architecture.md's clone-file disposition table is missing " + rule.Field);

        // …and every disposition WORD really appears, so a table that listed
        // the rows but dropped the column would fail too.
        foreach (var disposition in Enum.GetValues<CloneFieldDisposition>())
            Assert.Contains(DocWord(disposition), doc, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocRow_CarriesTheSameDisposition_TheCodeDoes()
    {
        // Agreement, not just presence: a row whose doc cell said VALIDATED
        // while the code said INERT would be worse than a missing row.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "software-architecture.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (var rule in CloneFileValidation.Dispositions)
        {
            var marker = "| `" + rule.Field + "` | ";
            int at = doc.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, "missing doc row for " + rule.Field);
            var cell = doc[(at + marker.Length)..doc.IndexOf('\n', at)];
            Assert.True(cell.StartsWith(DocWord(rule.Disposition), StringComparison.Ordinal),
                $"{rule.Field}: the doc says '{cell.Split('|')[0].Trim()}', the code says "
                + DocWord(rule.Disposition));
        }
    }

    private static string DocWord(CloneFieldDisposition disposition) => disposition switch
    {
        CloneFieldDisposition.Validated => "VALIDATED",
        CloneFieldDisposition.Bounded => "BOUNDED",
        CloneFieldDisposition.SwapDropped => "SWAP-DROPPED",
        _ => "INERT",
    };

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
