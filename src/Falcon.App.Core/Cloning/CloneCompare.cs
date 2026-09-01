using System.Globalization;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.Cloning;

/// <summary>
/// The CANONICAL COMPARISON RULES (plan round 11 §9A, FULL VERIFY). The write
/// campaign's last leg re-runs the read campaign and compares what came back
/// against the TRANSFORMED file — and the comparison has to know, per domain,
/// what the radio is entitled to reorder and what it is not:
///
/// <list type="bullet">
/// <item><b>Selfs</b> and <b>per-net members</b>: ORDER-SENSITIVE. Self order
/// IS the primary rule (first listed = primary) and member order is insertion
/// order — reordering either is a real difference.</item>
/// <item><b>Individuals, nets</b>: keyed by NAME.</item>
/// <item><b>SSB channels, modem presets, HOP nets, exclusion bands, stored
/// messages</b>: keyed by NUMBER/SLOT, exact equality. An extra row on the
/// target is a DIFF, not a tolerance. <b>The TARGET-ONLY-SURVIVOR rule is
/// DELETED (clone round 12, owner statement §1: "it's safe to assume that
/// zeroize clears everything except for the remote port baud rate").</b> It
/// existed because a slot the file did not carry could not be removed — with
/// no channel-delete verb and a read that omitted unprogrammed slots, a
/// target-only row was the one thing a clone could not undo. The campaign now
/// ZEROIZES first, so NOTHING survives to be tolerated, and the read captures
/// every slot the radio reports (the default rows included).</item>
/// <item><b>Operator lockouts</b>: keyed EXACT on (family, section, item) —
/// item names repeat across sections, so nothing here is keyed by item
/// alone.</item>
/// <item><b>Channel groups</b> and <b>HOP LIST frequencies</b>: SET-compare —
/// the radio sorts them and an order difference carries no information.</item>
/// <item><b>LQA schedules</b>: keyed by (kind, address) comparing the stored
/// interval and start; LIST ORDER IGNORED, because the listing is
/// chronological by next start and therefore clock-dependent.</item>
/// <item><b>Settings</b>: keyed, exact. A SILENTLY CLAMPED value (the modem
/// baud ceilings) stays a GENUINE diff — read-back is truth, and hiding it
/// would be the app inventing a success.</item>
/// <item><b>Read-state markers</b>: compared, so a domain that came back
/// FAULTED is a diff rather than an accidental match.</item>
/// </list>
///
/// <para>Every diff line is operator-facing (R13): plain words, no radio
/// token.</para>
/// </summary>
public static class CloneCompare
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Compare the state the write should have produced
    /// (<paramref name="expected"/>, the transformed file) against what the
    /// verify read campaign actually found. Empty = clean.</summary>
    /// <param name="notAttempted">Manifest domain names (the spellings
    /// <see cref="CloneFile.ManifestDomains"/> uses) whose WRITE LEG was
    /// abandoned — D3. Their per-row comparison is SKIPPED, marker included:
    /// comparing a domain the write never attempted produces tens of true but
    /// useless diffs, and the campaign says once, in its own words, that it did
    /// not attempt it. Null (the default) compares everything, which is what
    /// every caller but the write's verify wants.</param>
    public static IReadOnlyList<string> Diff(
        CloneFile expected, CloneFile actual, IReadOnlySet<string>? notAttempted = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var diffs = new List<string>();
        var skipped = notAttempted ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        CompareMarkers(expected, actual, diffs, skipped);
        CompareOperating(expected, actual, diffs);
        if (!skipped.Contains("address book")) CompareBook(expected, actual, diffs);
        if (!skipped.Contains("channel groups")) CompareGroups(expected, actual, diffs);
        CompareSchedules(expected, actual, diffs);
        CompareChannels(expected, actual, diffs);
        CompareHopNets(expected, actual, diffs);
        CompareExcludeBands(expected, actual, diffs);
        CompareModemPresets(expected, actual, diffs);
        CompareMessages(expected, actual, diffs);
        CompareSettings(expected, actual, diffs);
        CompareLockouts(expected, actual, diffs);

        return diffs;
    }

    private static void CompareMarkers(
        CloneFile e, CloneFile a, List<string> diffs, IReadOnlySet<string> skipped)
    {
        var expectedStates = e.ManifestDomains;
        var actualStates = a.ManifestDomains;
        for (int i = 0; i < expectedStates.Count; i++)
            if (!skipped.Contains(actualStates[i].Name)
                && actualStates[i].State != CloneDomainState.Read)
                diffs.Add($"{Cap(actualStates[i].Name)}: the verify read did not complete "
                    + $"({Describe(actualStates[i].State)}), so this domain is unverified.");
    }

    private static void CompareOperating(CloneFile e, CloneFile a, List<string> diffs)
    {
        if (!string.Equals(e.OperatingMode, a.OperatingMode, StringComparison.Ordinal))
            diffs.Add($"Operating mode: expected {Show(e.OperatingMode)}, the radio reports {Show(a.OperatingMode)}.");
        if (e.OperatingChannel != a.OperatingChannel)
            diffs.Add($"Operating channel: expected {Show(e.OperatingChannel)}, the radio reports {Show(a.OperatingChannel)}.");
        if (e.OperatingHopNet != a.OperatingHopNet)
            diffs.Add($"Operating HOP net: expected {Show(e.OperatingHopNet)}, the radio reports {Show(a.OperatingHopNet)}.");
    }

    private static void CompareBook(CloneFile e, CloneFile a, List<string> diffs)
    {
        // Selfs: ORDER-SENSITIVE — the first row is the primary.
        var expectedSelfs = e.Selfs.Select(Describe).ToList();
        var actualSelfs = a.Selfs.Select(Describe).ToList();
        if (!expectedSelfs.SequenceEqual(actualSelfs, StringComparer.Ordinal))
            diffs.Add("Self addresses: expected " + Join(expectedSelfs)
                + ", the radio lists " + Join(actualSelfs) + " (order matters — the first self is the primary).");

        CompareKeyed(
            e.Individuals.ToDictionary(i => CloneFile.Normalize(i.Name), Describe, StringComparer.Ordinal),
            a.Individuals.ToDictionary(i => CloneFile.Normalize(i.Name), Describe, StringComparer.Ordinal),
            "Individual", diffs);

        CompareKeyed(
            e.Nets.ToDictionary(n => CloneFile.Normalize(n.Name), DescribeNet, StringComparer.Ordinal),
            a.Nets.ToDictionary(n => CloneFile.Normalize(n.Name), DescribeNet, StringComparer.Ordinal),
            "Net", diffs);

        // Members: ORDER-SENSITIVE, per net, only for nets both sides hold
        // (a missing net is already reported above and would double-report).
        var actualNets = a.Nets.ToDictionary(n => CloneFile.Normalize(n.Name), n => n, StringComparer.Ordinal);
        foreach (var net in e.Nets)
        {
            if (!actualNets.TryGetValue(CloneFile.Normalize(net.Name), out var other)) continue;
            var expectedMembers = net.Members.Select(CloneFile.Normalize).ToList();
            var gotMembers = other.Members.Select(CloneFile.Normalize).ToList();
            if (!expectedMembers.SequenceEqual(gotMembers, StringComparer.Ordinal))
                diffs.Add($"Net {net.Name} members: expected {Join(expectedMembers)}, "
                    + $"the radio lists {Join(gotMembers)} (order matters — members list in the order they were added).");
        }
    }

    private static void CompareGroups(CloneFile e, CloneFile a, List<string> diffs)
    {
        // SET-compare: the radio sorts a group's channels.
        var expected = e.ChannelGroups.ToDictionary(g => g.Group, g => g.Channels.ToHashSet());
        var actual = a.ChannelGroups.ToDictionary(g => g.Group, g => g.Channels.ToHashSet());
        foreach (int group in expected.Keys.Union(actual.Keys).Order())
        {
            expected.TryGetValue(group, out var want);
            actual.TryGetValue(group, out var got);
            want ??= [];
            got ??= [];
            if (!want.SetEquals(got))
                diffs.Add($"Channel group {group}: expected channels {Join(want.Order().Select(Two))}, "
                    + $"the radio holds {Join(got.Order().Select(Two))}.");
        }
    }

    private static void CompareSchedules(CloneFile e, CloneFile a, List<string> diffs)
    {
        // Keyed by (kind, address); LIST ORDER IGNORED — the listing is
        // chronological by next start, so it moves with the clock.
        static Dictionary<string, string> Key(CloneFile f) => f.Schedules.ToDictionary(
            s => s.Kind + " " + CloneFile.Normalize(s.Address),
            s => $"every {s.Interval} from {s.Start}",
            StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Schedule", diffs);
    }

    /// <summary>
    /// THE CHANNEL DOMAIN'S CANONICAL RULE, under D4's elision: <b>a slot
    /// ABSENT from a file is a slot expected to hold
    /// <see cref="Wire.DefaultChannel"/></b> — which is what the radio really
    /// holds there, because a never-written slot prints the factory row and a
    /// ZEROIZE puts every slot back to it.
    ///
    /// <para>Both sides are therefore filled out over the union of the slots
    /// EITHER side mentions. Three consequences, and all three are the point:
    /// a re-read default row matches an absent file row SILENTLY; a re-read
    /// NON-default row that the file does not hold is a real difference, named
    /// by channel number; and a file row the radio came back default for is
    /// still the "the write did not land" diff it always was. The domain keeps
    /// its old "extra rows are a diff, not a tolerance" doctrine — what changed
    /// is only what ABSENCE means, and absence now means a value rather than
    /// nothing.</para>
    /// </summary>
    private static void CompareChannels(CloneFile e, CloneFile a, List<string> diffs)
    {
        var expected = e.Channels.ToDictionary(c => c.Number);
        var actual = a.Channels.ToDictionary(c => c.Number);
        var slots = expected.Keys.Union(actual.Keys).Order().ToList();

        CompareKeyed(
            slots.ToDictionary(Two, n => Describe(expected, n), StringComparer.Ordinal),
            slots.ToDictionary(Two, n => Describe(actual, n), StringComparer.Ordinal),
            "SSB channel", diffs);

        static string Describe(Dictionary<int, CloneChannel> rows, int number)
        {
            if (rows.TryGetValue(number, out var row))
                return $"rx {row.RxFrequency} tx {row.TxFrequency} {row.Mode} agc {row.Agc} "
                    + $"bw {row.Bandwidth} receive-only {row.RxOnly}";
            var d = Wire.DefaultChannel;
            return $"rx {d.RxFrequency} tx {d.TxFrequency} {d.Mode} agc {d.Agc} "
                + $"bw {d.Bandwidth} receive-only {d.RxOnly}";
        }
    }

    private static void CompareHopNets(CloneFile e, CloneFile a, List<string> diffs)
    {
        var expected = e.HopNets.ToDictionary(n => n.Number, n => n);
        var actual = a.HopNets.ToDictionary(n => n.Number, n => n);
        foreach (int net in expected.Keys.Union(actual.Keys).Order())
        {
            expected.TryGetValue(net, out var want);
            actual.TryGetValue(net, out var got);
            if (want is null || got is null)
            {
                diffs.Add($"HOP net {net}: " + (want is null
                    ? "the radio holds a net the file does not."
                    : "the radio does not hold this net."));
                continue;
            }
            if (DescribeHopNet(want) != DescribeHopNet(got))
                diffs.Add($"HOP net {net}: expected {DescribeHopNet(want)}, the radio reports {DescribeHopNet(got)}.");
            // LIST frequencies SET-compare (the radio sorts them).
            var wantList = want.ListFrequencies.ToHashSet(StringComparer.Ordinal);
            var gotList = got.ListFrequencies.ToHashSet(StringComparer.Ordinal);
            if (!wantList.SetEquals(gotList))
                diffs.Add($"HOP net {net} frequencies: expected {Join(wantList.Order(StringComparer.Ordinal))}, "
                    + $"the radio holds {Join(gotList.Order(StringComparer.Ordinal))}.");
        }
    }

    private static void CompareExcludeBands(CloneFile e, CloneFile a, List<string> diffs)
    {
        static Dictionary<string, string> Key(CloneFile f) => f.ExcludeBands.ToDictionary(
            b => b.Band.ToString(Inv), b => $"{b.LowKHz}-{b.HighKHz}", StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Exclusion band", diffs);
    }

    private static void CompareModemPresets(CloneFile e, CloneFile a, List<string> diffs)
    {
        // Exact, keyed by preset number. A SILENTLY CLAMPED baud reads back
        // as a different value and stays a GENUINE diff — read-back is truth.
        static Dictionary<string, string> Key(CloneFile f) => f.ModemPresets.ToDictionary(
            p => p.Number.ToString(Inv),
            p => Squash(p.Fields) + (p.Enabled ? " [enabled]" : " [disabled]"),
            StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Modem preset", diffs);
    }

    private static void CompareMessages(CloneFile e, CloneFile a, List<string> diffs)
    {
        static Dictionary<string, string> Key(CloneFile f) => f.Messages.ToDictionary(
            m => m.Slot.ToString(Inv), m => m.Text, StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Stored message", diffs);
    }

    private static void CompareSettings(CloneFile e, CloneFile a, List<string> diffs)
    {
        static Dictionary<string, string> Key(CloneFile f) => f.Settings.ToDictionary(
            s => s.Key, s => s.Value, StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Setting", diffs);
    }

    /// <summary>The operator lockouts, KEYED EXACT on (family, section, item).
    /// Keying by item alone would silently merge rows the radio keeps apart —
    /// PROGRAM carries DATA twice and CFIG twice, SELECT carries DATA and KEY
    /// three times each.</summary>
    private static void CompareLockouts(CloneFile e, CloneFile a, List<string> diffs)
    {
        static Dictionary<string, string> Key(CloneFile f) =>
            (f.Lockouts?.Rows ?? []).ToDictionary(
                r => $"{r.Family} {r.Section} {r.Item}", r => r.State, StringComparer.Ordinal);
        CompareKeyed(Key(e), Key(a), "Lockout", diffs);
    }

    /// <summary>The shared keyed comparison: present-on-one-side and
    /// value-differs are separate, differently-worded diffs.</summary>
    private static void CompareKeyed(
        Dictionary<string, string> expected, Dictionary<string, string> actual, string what, List<string> diffs)
    {
        foreach (var key in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            bool hasExpected = expected.TryGetValue(key, out var want);
            bool hasActual = actual.TryGetValue(key, out var got);
            if (hasExpected && !hasActual)
                diffs.Add($"{what} {key}: the radio does not hold it (expected {want}).");
            else if (!hasExpected && hasActual)
                diffs.Add($"{what} {key}: the radio holds it and the file does not (it holds {got}).");
            else if (!string.Equals(want, got, StringComparison.Ordinal))
                diffs.Add($"{what} {key}: expected {want}, the radio reports {got}.");
        }
    }

    // ---- rendering ---------------------------------------------------------

    private static string Describe(CloneAddress a)
        => a.AssociatedSelf is { Length: > 0 } self
            ? $"{CloneFile.Normalize(a.Name)} (group {a.Group}, self {CloneFile.Normalize(self)})"
            : $"{CloneFile.Normalize(a.Name)} (group {a.Group})";

    private static string DescribeNet(CloneNet n)
        => $"{CloneFile.Normalize(n.Name)} (group {n.Group}, self "
            + (n.AssociatedSelf is { Length: > 0 } s ? CloneFile.Normalize(s) : "none") + ")";

    private static string DescribeHopNet(CloneHopNet n)
        => n.Wiped
            ? "wiped"
            : $"id {n.NetId ?? "none"}, type {n.Type ?? "none"}, "
                + $"centre {n.CenterKHz ?? "—"}, low {n.LowKHz ?? "—"}, high {n.HighKHz ?? "—"}";

    private static string Describe(CloneDomainState state) => state switch
    {
        CloneDomainState.Unread => "it was never read",
        CloneDomainState.Faulted => "the radio stopped answering",
        _ => "read",
    };

    /// <summary>Column padding differs between a listing and an echo, and
    /// carries no meaning — compare the WORDS.</summary>
    private static string Squash(string line) =>
        string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Two(int n) => n.ToString("00", Inv);

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static string Show(string? value) => value ?? "none";
    private static string Show(int? value) => value?.ToString(Inv) ?? "none";
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
