using System.Text.RegularExpressions;
using Falcon.App.Core.Cloning;

namespace Falcon.App.Tests;

/// <summary>
/// DELIVERABLE 1's COMPLETENESS PIN (plan round 11 §11 P6): "every W1 row is
/// dispositioned, every included row has read+write+verify paths".
///
/// <para><b>Why it is worth a machine.</b> The manifest is derived by HAND
/// from another document. The failure mode is not a wrong disposition — a
/// reviewer catches those — it is a row nobody thought about at all, which
/// looks exactly like a row that was considered and excluded. So the pin walks
/// plan/phase-r-classification.md's own tables and requires a disposition for
/// every row it finds, and requires every field a disposition POINTS AT to
/// exist in the manifest.</para>
///
/// <para><b>ACCEPTED LIMITATION:</b> this cannot check that a disposition is
/// RIGHT, only that one exists and is internally consistent. Correctness is a
/// review property; completeness and agreement are the parts a machine can
/// hold, and those are what it holds.</para>
/// </summary>
public class CloneSettingsManifestTests
{
    /// <summary>Every W1 table row's FIRST cell, verbatim. Markdown escapes a
    /// literal pipe inside a cell (<c>&lt;n\|name&gt;</c>), so the split must
    /// ignore an escaped one — otherwise that row silently becomes a different
    /// key and the pin passes on a truncated name.</summary>
    private static IReadOnlyList<string> W1RowKeys()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "plan", "phase-r-classification.md"));
        var keys = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (!row.StartsWith("| ", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(row, @"^\|[-\s:|]+\|\s*$")) continue;      // the separator rule

            var cells = Regex.Split(row[1..], @"(?<!\\)\|");
            var key = cells[0].Trim();
            if (key.Length == 0) continue;
            if (key is "Command" or "Mirror member") continue;           // header rows
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }
        return keys;
    }

    [Fact]
    public void EveryW1Row_IsDispositioned()
    {
        var keys = W1RowKeys();
        var missing = keys
            .Where(k => !CloneSettingsManifest.W1Dispositions.ContainsKey(k))
            .ToList();

        Assert.True(missing.Count == 0,
            "these W1 rows have no clone-manifest disposition: " + string.Join(" / ", missing));

        // Anti-vacuity: the reader really found the tables (W1 has well over a
        // hundred rows across its eight sections), and the escaped-pipe row is
        // one of the keys it found — the exact row a naive split would mangle.
        Assert.True(keys.Count >= 120, $"only {keys.Count} W1 rows parsed — the reader has drifted");
        Assert.Contains("`MODEM <n\\|name>`", keys);
    }

    [Fact]
    public void NoDisposition_IsForARowW1DoesNotHave()
    {
        // The other direction: a disposition whose key no longer matches a W1
        // row is a stale entry that would keep the pin above green after the
        // source document had moved on.
        var keys = W1RowKeys().ToHashSet(StringComparer.Ordinal);
        var stale = CloneSettingsManifest.W1Dispositions.Keys
            .Where(k => !keys.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "these dispositions name W1 rows that no longer exist: " + string.Join(" / ", stale));
    }

    [Fact]
    public void EveryDispositionThatPointsAtFields_PointsAtRealOnes()
    {
        var fields = CloneSettingsManifest.Rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var excluded = CloneSettingsManifest.ExcludedFields.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        int pointing = 0;

        foreach (var (key, note) in CloneSettingsManifest.W1Dispositions)
        {
            if (!note.StartsWith("→ ", StringComparison.Ordinal)) continue;
            pointing++;
            // "→ A, B (C is excluded — reason)" — the parenthetical is prose.
            var listed = note[2..].Split('(', 2)[0];
            foreach (var name in listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!fields.Contains(name) && !excluded.Contains(name))
                    offenders.Add($"{key} points at '{name}', which is not a manifest field");
        }

        Assert.Empty(offenders);
        Assert.True(pointing >= 20, $"only {pointing} dispositions point at fields — the parse has drifted");
    }

    [Fact]
    public void EveryIncludedRow_HasAReadPathAWritePathAndAVerifyPath()
    {
        // The VERIFY path is the READ path by construction (the campaign
        // re-runs the read and compares), so a row with a reader and a writer
        // has all three. A row with a writer and no reader could be WRITTEN and
        // never checked, which is exactly the silent-failure the pin forbids.
        Assert.NotEmpty(CloneSettingsManifest.Rows);
        foreach (var row in CloneSettingsManifest.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Key));
            Assert.Contains(row.Prompt, new[] { "SSB>", "ALE>" });
            Assert.False(string.IsNullOrWhiteSpace(row.ReadOp));
            Assert.False(string.IsNullOrWhiteSpace(row.Setter));
            Assert.NotNull(row.Read);
            Assert.NotNull(row.Write);
            // A row whose read is NOT an SH block must say how it is queried;
            // a row that IS in an SH block must not queue a second read.
            if (row.ReadOp == "SH") Assert.Null(row.Query);
            else Assert.NotNull(row.Query);
        }
    }

    [Fact]
    public void TheFieldKeys_AreUnique_AndIsIncludedKeyAgrees()
    {
        var keys = CloneSettingsManifest.Rows.Select(r => r.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.True(CloneSettingsManifest.IsIncludedKey(k)));
        Assert.False(CloneSettingsManifest.IsIncludedKey("NotAField"));
    }

    [Fact]
    public void TheOrderColumn_PutsTheCascadingSettersFirst()
    {
        // RWAS forces all three squelches ON — on ENABLE ONLY (CORRECTED clone
        // round 12: `RWAS DIS` REPORTS them and forces none) — so it is order 1
        // and the DIGITAL squelch it moves is order 2. DV is order 1 on the
        // GRADUATED D1 MATRIX (RE-GROUNDED round 13 D1: the old "PRECAUTION
        // over a disputed AGC/RFG capture" reason is retired — the matrix
        // MEASURED the cascade). The notes must SAY why, because an order with
        // a disproved reason is an order nobody can maintain.
        var order = CloneSettingsManifest.Rows.ToDictionary(r => r.Key, r => r.Order, StringComparer.Ordinal);

        Assert.Equal(1, order["Rwas"]);
        Assert.Equal(1, order["DigitalVoice"]);
        Assert.Equal(2, order["DigitalSquelch"]);
        Assert.All(CloneSettingsManifest.Rows,
            r => Assert.InRange(r.Order, 1, CloneSettingsManifest.FinalsOrder));

        // …and the cascading rows really SAY why, so a reader of the table sees
        // the reason rather than an unexplained number.
        var rwas = CloneSettingsManifest.Rows.First(r => r.Key == "Rwas");
        Assert.Contains("squelch", rwas.Note, StringComparison.Ordinal);
        // THE ENA-ONLY CORRECTION (critic F17), pinned as a fact and not just
        // as prose: the note must state that DISABLING forces nothing. The
        // both-ways wording it replaced would make the ORDER column right only
        // by accident, and the demo used to model the disproved half.
        Assert.Contains("ENABLING forces", rwas.Note, StringComparison.Ordinal);
        Assert.Contains("DISABLING forces NOTHING", rwas.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("OR DISABLING** forces", rwas.Note, StringComparison.Ordinal);

        // The DV note's re-grounding is pinned in full by
        // TheDvRows_AreReGroundedOnTheGraduatedD1Matrix; here the ORDER test
        // asks only what an order test may ask — that the row states the
        // cascade it is ordered around.
        var dv = CloneSettingsManifest.Rows.First(r => r.Key == "DigitalVoice");
        Assert.Contains("cascading setter", dv.Note, StringComparison.Ordinal);
        Assert.Contains("writes FIRST", dv.Note, StringComparison.Ordinal);

        // …and every order-1 row really precedes every order-2 row in the
        // sequence the write leg produces.
        var written = CloneSettingsManifest.Rows
            .Where(r => r.Prompt == "SSB>" && r.Order != CloneSettingsManifest.FinalsOrder)
            .OrderBy(r => r.Order)
            .ToList();
        Assert.True(written.FindIndex(r => r.Order == 2) > written.FindLastIndex(r => r.Order == 1));
    }

    /// <summary>
    /// The two rows that REJOINED the manifest in clone round 12, and the one
    /// that is deliberately NOT written with the others.
    /// </summary>
    [Fact]
    public void TheTwoRejoiningRows_AreIncluded_AndAnalogSquelchIsTheOneFinalsRow()
    {
        var byKey = CloneSettingsManifest.Rows.ToDictionary(r => r.Key, r => r, StringComparer.Ordinal);

        // ActiveModem (critic F10): excluded in round 11 as a CASCADE CONFLICT,
        // rejoined once the bench settled that engagement moves LIVE state and
        // leaves the STORED channel record byte-identical.
        Assert.True(byKey.ContainsKey("ActiveModem"));
        Assert.Contains("BYTE-IDENTICAL", byKey["ActiveModem"].Note, StringComparison.Ordinal);
        Assert.Contains("RAW MIRROR STRING", byKey["ActiveModem"].Note, StringComparison.Ordinal);

        // AnalogSquelch (owner ruling R4): rejoined, and the ONE row that is
        // written at the finals rather than in the settings leg.
        Assert.True(byKey.ContainsKey("AnalogSquelch"));
        var finals = Assert.Single(CloneSettingsManifest.Rows,
            r => r.Order == CloneSettingsManifest.FinalsOrder);
        Assert.Equal("AnalogSquelch", finals.Key);
        // Both of R4's paths are named in the row, so neither can be quietly
        // dropped in favour of the other.
        Assert.Contains("GREEN PATH", finals.Note, StringComparison.Ordinal);
        Assert.Contains("RED PATH", finals.Note, StringComparison.Ordinal);
        Assert.Contains("SKIPPED", finals.Note, StringComparison.Ordinal);

        // Neither is still listed as excluded — the two lists must not
        // disagree (NoFieldIsBothIncludedAndExcluded holds the general rule;
        // these two name the specific rows this round moved).
        var excluded = CloneSettingsManifest.ExcludedFields.Select(f => f.Field).ToList();
        Assert.DoesNotContain("AnalogSquelch", excluded);
        Assert.DoesNotContain("ActiveModem", excluded);
    }

    /// <summary>
    /// ROUND 13 D1 (backlog item 2): the round-12 §9 B3 deferral is CLOSED and
    /// <c>Compression</c> is an INCLUDED row. The old pin held the exclusion
    /// reason byte-exact; its replacement holds the inclusion — the row exists,
    /// carries the whole read/write/verify triple, is ordered AFTER the DV
    /// cascade, and no longer appears anywhere as an exclusion.
    ///
    /// <para>The unlock CONDITION is what this pins, not just the outcome: the
    /// row was held out "pending the D1 DV-matrix graduation", so the reason
    /// must cite the graduation that released it. A row that flipped without
    /// naming its reason is a row nobody can re-audit.</para>
    /// </summary>
    [Fact]
    public void Compression_IsNowAnIncludedRow_CitingTheD1Graduation()
    {
        Assert.True(CloneSettingsManifest.IsIncludedKey("Compression"));

        var row = Assert.Single(CloneSettingsManifest.Rows, r => r.Key == "Compression");
        Assert.Equal("SSB>", row.Prompt);
        Assert.Equal("COM", row.ReadOp);
        Assert.Equal("Ssb.SetCompression", row.Setter);
        Assert.Equal(3, row.Order);
        // Not an SH block, so it MUST carry its own query builder — the
        // criterion-3 half of the unlock (round-12 P1's QueryCompression).
        Assert.NotNull(row.Query);

        // The reason names round 13 AND the graduation that released it.
        Assert.Contains("round 13", row.Note, StringComparison.Ordinal);
        Assert.Contains("D1", row.Note, StringComparison.Ordinal);
        Assert.Contains("UNLOCK CONDITION IS MET", row.Note, StringComparison.Ordinal);

        // ORDER 3 is not "anywhere"; it is AFTER the DV row, which is what
        // stops the DV cascade post-dating the compression write.
        var dvOrder = CloneSettingsManifest.Rows.First(r => r.Key == "DigitalVoice").Order;
        Assert.True(dvOrder < row.Order);

        // And it is excluded NOWHERE any more — neither in the exclusion list
        // nor in either W1 disposition that used to say so. The second of those
        // (`NEW .Compression (OnOff)`) was STALE for a whole round: it read "no
        // read builder" while the exclusion note beside it said the builder had
        // landed. Both are pinned, so they cannot drift apart again.
        Assert.DoesNotContain("Compression",
            CloneSettingsManifest.ExcludedFields.Select(f => f.Field));
        Assert.Equal("→ Compression", CloneSettingsManifest.W1Dispositions["`COMpression`"]);
        Assert.Equal("→ Compression", CloneSettingsManifest.W1Dispositions["NEW .Compression (OnOff)"]);
    }

    /// <summary>
    /// ROUND 13 D1 (backlog item 1): the DV and digital-squelch reasons are
    /// RE-GROUNDED on the graduated D1 matrix. The old DV note argued order 1
    /// from a PRECAUTION over two captures that disagreed; the matrix measured
    /// the cascade, so the hedge must be gone and the measurement must be in.
    /// </summary>
    [Fact]
    public void TheDvRows_AreReGroundedOnTheGraduatedD1Matrix()
    {
        var byKey = CloneSettingsManifest.Rows.ToDictionary(r => r.Key, r => r.Note, StringComparer.Ordinal);

        var dv = byKey["DigitalVoice"];
        Assert.Contains("GRADUATED D1 MATRIX", dv, StringComparison.Ordinal);
        Assert.Contains("SILENTLY FORCES", dv, StringComparison.Ordinal);
        Assert.Contains("MODE USB", dv, StringComparison.Ordinal);
        // The RETIRED reason must be named as retired, not merely absent: a
        // reader of the old note needs to know it was replaced by measurement.
        Assert.Contains("RETIRED", dv, StringComparison.Ordinal);
        Assert.Contains("DISAGREED", dv, StringComparison.Ordinal);
        // …and the honesty instruments for the verify leg are named.
        Assert.Contains("P4", dv, StringComparison.Ordinal);
        Assert.Contains("ArmCompressionRepoll", dv, StringComparison.Ordinal);

        // The digital squelch is order 2 because of RWAS, NOT because of DV —
        // the matrix's "what actually moved" column never carries this row.
        var dgt = byKey["DigitalSquelch"];
        Assert.Contains("GRADUATED D1 MATRIX", dgt, StringComparison.Ordinal);
        Assert.Contains("RIDER", dgt, StringComparison.Ordinal);
        Assert.Contains("ANALOG squelch", dgt, StringComparison.Ordinal);
        Assert.Contains("`RWAS ENA` is", dgt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The W1 rows this round RE-DISPOSITIONED. Each one had a reason that a
    /// 2026-08-18 capture or an owner ruling retired, and a disposition that
    /// still cites a retired reason is worse than none.
    /// </summary>
    [Fact]
    public void TheRoundTwelveRedispositions_NoLongerCiteTheirRetiredReasons()
    {
        var d = CloneSettingsManifest.W1Dispositions;

        // ZERO: "no builder exists and none may" is SUPERSEDED by R1.
        Assert.Contains("SUPERSEDED by owner ruling R1", d["`ZERO`"], StringComparison.Ordinal);
        Assert.DoesNotContain("Excluded", d["`ZERO`"], StringComparison.Ordinal);
        Assert.Contains("whitelist-narrowed", d["`ZERO`"], StringComparison.Ordinal);

        // PROGRAM / SELECT: out of scope no longer — they ARE the new domain.
        Assert.Contains("LOCKOUTS domain", d["`PROGram` / `SELect`"], StringComparison.Ordinal);
        Assert.DoesNotContain("OUT OF SCOPE", d["`PROGram` / `SELect`"], StringComparison.Ordinal);

        // The four DELETED reconcile legs, each citing the wipe.
        foreach (var key in new[] { "`ERASE`", "`DELCh`", "`HOPSET <n> DEL`" })
        {
            Assert.Contains("DELETED clone round 12", d[key], StringComparison.Ordinal);
            Assert.Contains("ZERO", d[key], StringComparison.Ordinal);
        }
        Assert.Contains("RECONCILE is DELETED", d["`EXCLUDE` set/query/DEL"], StringComparison.Ordinal);

        // TXMSG moved prompt AND lost its per-slot delete.
        Assert.Contains("`ALE>` prompt", d["`TXMsg`"], StringComparison.Ordinal);
        Assert.Contains("per-slot DELETE is DELETED", d["`TXMsg`"], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryExcludedField_CarriesAReason_AndTheBindingFourAreNamed()
    {
        Assert.NotEmpty(CloneSettingsManifest.ExcludedFields);
        Assert.All(CloneSettingsManifest.ExcludedFields, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Field));
            Assert.True(f.Reason.Length > 20, "the reason for " + f.Field + " is not a reason");
        });

        // §9A names four binding exclusions by name. Each must be present and
        // must say so — this is the pin that stops one quietly becoming an
        // inclusion.
        var byField = CloneSettingsManifest.ExcludedFields.ToDictionary(f => f.Field, f => f.Reason, StringComparer.Ordinal);
        Assert.Contains("binding exclusion", byField["Keyline"], StringComparison.Ordinal);
        Assert.Contains("binding exclusion", byField["PortBaud"], StringComparison.Ordinal);
        Assert.Contains("binding exclusion", byField["Encryption / EncryptionAvailability"], StringComparison.Ordinal);
        Assert.Contains("binding exclusion", byField["EncryptionKeySlots (ENC_KEY / USE_KEY)"], StringComparison.Ordinal);
    }

    [Fact]
    public void NoFieldIsBothIncludedAndExcluded()
    {
        var included = CloneSettingsManifest.Rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var excluded = CloneSettingsManifest.ExcludedFields.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(included.Intersect(excluded, StringComparer.Ordinal));
    }

    // ---- The doc table is the same table -------------------------------------

    [Fact]
    public void TheArchitectureDoc_CarriesEveryIncludedRow_AndEveryExcludedField()
    {
        // DELIVERABLE 1 is the table IN THE DOC. Pinned against the CODE, so
        // the doc cannot describe a manifest the app does not implement — the
        // DocsGuardTests idiom.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "software-architecture.md"));

        foreach (var row in CloneSettingsManifest.Rows)
            Assert.True(doc.Contains("| `" + row.Key + "` |", StringComparison.Ordinal),
                "software-architecture.md's clone manifest is missing the row for " + row.Key);

        foreach (var field in CloneSettingsManifest.ExcludedFields)
            Assert.True(doc.Contains("| " + field.Field + " |", StringComparison.Ordinal),
                "software-architecture.md's exclusion table is missing " + field.Field);
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
