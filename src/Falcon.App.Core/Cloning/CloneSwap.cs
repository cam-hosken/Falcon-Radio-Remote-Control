namespace Falcon.App.Core.Cloning;

/// <summary>The result of the identity transform: the file to write, every row
/// the transform could NOT carry with its reason, and every ROLE CHANGE it
/// made. Drops are PROBLEMS and role changes are NOTICES, and neither is ever
/// silent (invariant I-6; plan-clone-field-round2 §3.2).</summary>
public sealed record CloneSwapResult(
    CloneFile File,
    IReadOnlyList<string> Drops,
    IReadOnlyList<string> RoleChanges);

/// <summary>
/// The identity transform — owner ruling <b>R-A</b>
/// (plan-clone-field-round2 §2 F2, §3.2). The write's identity step is a TABLE
/// of <see cref="SelfDisposition"/> rows, one per self in the file, and this is
/// the function that reads it.
///
/// <para><b>What replaced what.</b> Round 11's version took ONE identity, moved
/// it into the FIRST write slot and demoted the file's first self — silently.
/// The 2026-08-21 live clone is what that cost: the operator chose an
/// individual, and <c>HOS</c>, the file's only scan-gate self, was demoted
/// without a word. Now every self has its own row, every slot keeps its own
/// occupant, and every role change is REPORTED.</para>
///
/// <para><b>Two entry points, one contract.</b> <see cref="Refusal"/> answers
/// "is this table usable" with one prose sentence or null;
/// <see cref="Apply"/> is PURE, TOTAL and deterministic over any table
/// <see cref="Refusal"/> accepted, and throws before touching anything over one
/// it did not (invariant I-4). The write preflight asks both, in that order.</para>
///
/// <para><b>The dispositions</b> (R-A):</para>
/// <list type="bullet">
/// <item><b>Keep</b> — written as-is. An OMITTED row means Keep, so an empty
/// table is exactly round 11's no-identity write, byte for byte (pinned).</item>
/// <item><b>Swap with an individual</b> — the individual takes the slot keeping
/// its OWN channel group; the self demotes to an individual in its own group,
/// associated to the new one. Round 15 C-1: the individual must be one of the
/// ROW'S OWN (<see cref="SwapCandidates"/>) — the rows are per NET.</item>
/// <item><b>Replace with a new name</b> — the typed name takes the slot,
/// INHERITING the self's group; the self demotes as above.</item>
/// <item>The <b>scan-gate self</b> (1-3 characters) is Replace-only, and the
/// replacement must itself be 1-3 characters (D2). Round 15 C-2 (owner rule
/// 3): its Replace DROPS the old name — no demoted individual is created for
/// it — and reports one role change instead of two.</item>
/// <item>A file with <b>no self at all</b> (a post-ERASE source) takes exactly
/// one synthetic row — <c>("", Replace, name)</c> — which is what repairs it
/// (A-6). No rows at all is the standing preflight rejection.</item>
/// </list>
///
/// <para><b>Unreplayable state.</b> A net whose associated self is BLANK (the
/// primary-deletion artifact) cannot be replayed — <c>NETAD</c> requires an
/// existing self — so unless the re-point step rescued it, it is DROPPED and
/// LISTED. So are membership rows the radio would refuse (only a net's OWN
/// associated self may be a self member) and schedule rows whose target changed
/// kind under the transform (EXCHANGE refuses a self; SOUND takes only a self)
/// or whose target is no longer in the book at all.</para>
/// </summary>
public static class CloneSwap
{
    /// <summary>The radio's own scan gate: a self of 1-3 characters is the one
    /// that satisfies <c>PRG 1-3 CHAR SLF</c>. Swapping is not offered for it
    /// and its replacement must be 1-3 characters too (D2).</summary>
    public static bool IsScanGateSelf(string name) => CloneFile.Normalize(name).Length <= 3;

    /// <summary>
    /// The individuals a Swap may put in THIS self's slot — the scope owner
    /// ruling C-2 made the card's rule (round 15 §13.3, C-1): the individuals
    /// ASSOCIATED with the self, plus any individual MEMBER of the nets this
    /// self is associated to that is not already among them. Never another
    /// net's individuals: cloning a net promotes one of THAT net's stations.
    ///
    /// <para>The scan-gate self and the synthetic no-self row offer no swap at
    /// all (D2, A-6), so their candidate set is empty. This is the ONE
    /// definition of the scope: the card's picker and
    /// <see cref="Refusal"/>'s rule read it, so a hand-edited table is judged
    /// by exactly what the card offered.</para>
    /// </summary>
    public static IReadOnlyList<CloneAddress> SwapCandidates(CloneFile file, string? selfName)
    {
        ArgumentNullException.ThrowIfNull(file);

        string self = CloneFile.Normalize(selfName ?? "");
        if (self.Length == 0 || IsScanGateSelf(self)) return [];

        var chosen = new List<CloneAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var individual in file.Individuals)
        {
            if (!string.Equals(CloneFile.Normalize(individual.AssociatedSelf ?? ""), self, StringComparison.Ordinal))
                continue;
            if (seen.Add(CloneFile.Normalize(individual.Name))) chosen.Add(individual);
        }

        // …and the net's own member list, for the fills that have one (the
        // owner's 2026-08-21 file has none — every net's members are empty —
        // so this arm is the general case, not the observed one).
        foreach (var net in file.Nets)
        {
            if (!string.Equals(CloneFile.Normalize(net.AssociatedSelf ?? ""), self, StringComparison.Ordinal))
                continue;
            foreach (var member in net.Members)
            {
                var individual = MatchIndividual(file, member);
                if (individual is null) continue;                     // a self or a net member is not a candidate
                if (seen.Add(CloneFile.Normalize(individual.Name))) chosen.Add(individual);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Why this disposition table cannot be applied to this file, or null when
    /// it can. ONE sentence, in the operator's words (I-5) — the FIRST refusal
    /// in §3.2's table order wins, so a table with several faults always names
    /// the same one and the operator fixes them in a stable order.
    ///
    /// <para>The write preflight re-asks this (layer 2 of the standing idiom)
    /// and the Cloning card asks it live, so the operator sees the refusal
    /// while editing rather than as a mid-book failure after the erase.</para>
    /// </summary>
    public static string? Refusal(CloneFile file, IReadOnlyList<SelfDisposition> rows)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(rows);

        bool noSelfFile = file.Selfs.Count == 0;
        var selfNames = file.Selfs.Select(s => CloneFile.Normalize(s.Name)).ToHashSet(StringComparer.Ordinal);

        // ---- 1. every row names a self the file actually holds --------------
        foreach (var row in rows)
        {
            string name = CloneFile.Normalize(row.SelfName ?? "");
            if (noSelfFile)
            {
                // "" is the synthetic row's name and the ONLY one this file offers.
                if (name.Length > 0) return NotASelf(name);
                continue;
            }
            if (name.Length == 0)
                return "A disposition row names no self at all — every row belongs to one of the file's own selfs.";
            if (!selfNames.Contains(name)) return NotASelf(name);
        }

        // ---- 2. one row per self --------------------------------------------
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string name = CloneFile.Normalize(row.SelfName ?? "");
            if (!seen.Add(name))
                return $"{Subject(name)} has more than one disposition — each self takes exactly one.";
        }

        // ---- 3. the disposition is one this app offers -----------------------
        foreach (var row in rows)
            if (!Enum.IsDefined(row.Kind))
                return $"{Subject(CloneFile.Normalize(row.SelfName ?? ""))} was given a disposition this app does not offer.";

        // ---- 4. Keep names nothing ------------------------------------------
        foreach (var row in rows)
            if (row.Kind == SelfDispositionKind.Keep && row.Counterpart is not null)
                return $"{Subject(CloneFile.Normalize(row.SelfName ?? ""))} is kept, so it cannot also name a replacement.";

        // ---- 5. Swap and Replace name SOMETHING ------------------------------
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            if (!string.IsNullOrWhiteSpace(row.Counterpart)) continue;
            string subject = Subject(CloneFile.Normalize(row.SelfName ?? ""));
            return row.Kind == SelfDispositionKind.SwapWithIndividual
                ? $"{subject}: choose the individual to swap it with."
                : $"{subject}: type the new name that takes its place.";
        }

        // ---- 6. the name is one the radio can store --------------------------
        // Checked on the NORMALIZED value, because that is what gets stored.
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            if (!CloneService.IsValidIdentity(counterpart))
                return $"{counterpart} is not a name this radio can store — an ALE name is 1-15 letters or digits.";
        }

        // ---- 7. the scan-gate self is Replace-only (D2) -----------------------
        foreach (var row in rows)
        {
            string name = CloneFile.Normalize(row.SelfName ?? "");
            if (name.Length == 0 || !IsScanGateSelf(name)) continue;   // "" is the synthetic row, not a scan-gate self
            if (row.Kind == SelfDispositionKind.SwapWithIndividual)
                return $"{name} is the scan-gate self — it can only be given a new name, not swapped with an individual.";
        }

        // ---- 8. …and its replacement is 1-3 characters too (D2) ---------------
        foreach (var row in rows)
        {
            string name = CloneFile.Normalize(row.SelfName ?? "");
            if (name.Length == 0 || !IsScanGateSelf(name)) continue;
            if (row.Kind != SelfDispositionKind.Replace) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            if (!IsScanGateSelf(counterpart))
                return $"{counterpart} is too long for {name} — the scan-gate self takes a 1-3 character name.";
        }

        // ---- 9. a swap takes one of the file's OWN individuals ----------------
        foreach (var row in rows)
        {
            if (row.Kind != SelfDispositionKind.SwapWithIndividual) continue;
            if (MatchIndividual(file, row.Counterpart!) is null)
                return $"{CloneFile.Normalize(row.Counterpart!)} is not an individual in this file — "
                    + "a swap takes one of the file's own individuals.";
        }

        // ---- 9b. …and one of the ROW'S OWN individuals (C-1, round 15) --------
        // The rows are per NET now: a swap promotes one of THIS net's stations
        // to be this radio's self. Another net's individual is a different
        // radio's business, and a table that names one was hand-edited — the
        // card cannot offer it (`SwapCandidates` is what fills the picker).
        foreach (var row in rows)
        {
            if (row.Kind != SelfDispositionKind.SwapWithIndividual) continue;
            string self = CloneFile.Normalize(row.SelfName ?? "");
            // The SYNTHETIC row names no self, so it has no net and no scope:
            // "a no-self file's one row must give the radio a new name" is
            // rule 14's sentence, and it stays the one the operator reads.
            if (self.Length == 0) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            if (SwapCandidates(file, self).Any(c =>
                    string.Equals(CloneFile.Normalize(c.Name), counterpart, StringComparison.Ordinal)))
                continue;

            // Rule 9 already proved the counterpart IS one of the file's
            // individuals, so it has an association; name the net that
            // association belongs to, because that is the operator's word for
            // "the other radio's list".
            var owner = MatchIndividual(file, counterpart)!;
            string ownerSelf = CloneFile.Normalize(owner.AssociatedSelf ?? "");
            var ownerNet = file.Nets.FirstOrDefault(n => string.Equals(
                CloneFile.Normalize(n.AssociatedSelf ?? ""), ownerSelf, StringComparison.Ordinal));
            return ownerNet is not null
                ? $"{counterpart} belongs to net {ownerNet.Name} — {self} can only be swapped with "
                    + "one of its own net's individuals."
                : $"{counterpart} is not one of {self}'s own individuals — a swap takes an individual "
                    + "associated with this self.";
        }

        // ---- 10. no name may be a NET's ---------------------------------------
        // An ALE name is unique across selfs, individuals AND nets, so a name
        // that already belongs to a net could never also be stored as a self:
        // the transform would emit both and the radio would refuse the second
        // one MID-BOOK, after the erase (P6 audit round 1, BLOCKER).
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            var net = file.Nets.FirstOrDefault(
                n => string.Equals(CloneFile.Normalize(n.Name), counterpart, StringComparison.Ordinal));
            if (net is not null)
                return $"{net.Name} is already a net in this file — an ALE name is unique across selfs, "
                    + "individuals and nets, so it cannot also be this radio's self.";
        }

        // ---- 11. one name cannot fill two slots -------------------------------
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            if (!takenNames.Add(counterpart))
                return $"{counterpart} was chosen for two selfs — one name can only fill one slot.";
        }

        // ---- 12 (the plan's contract row 14, A-13). NO DUPLICATE MEMBERS ------
        // The BELT on the exchange below. A net's member list must be identical
        // in count, order and spelling on every station (manual §2.6.4.3.3), and
        // the radio refuses a repeat with `DUPLICATE MEMBER` — AFTER the wipe,
        // mid-book. The exchange makes a duplicate impossible by construction,
        // so this can only fire on a hand-edited file; it exists so that nothing
        // duplicate can reach the wire whatever produced the graph.
        var addresses = AddressMap(file, rows);
        foreach (var net in file.Nets)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in net.Members)
            {
                string afterMember = CloneFile.Normalize(RePoint(addresses, member) ?? member);
                if (!members.Add(afterMember))
                    return $"Net {net.Name} would list {afterMember} twice after the swap.";
            }
        }

        // ---- 13. the COMPLETE post-transform namespace ------------------------
        // Not "is the name taken today" but "is it taken AFTER the change":
        // kept selfs, every new occupant, untouched individuals, AND every
        // demoted old self. Replacing A with B while self B is itself replaced
        // collides on the DEMOTED B, which no read of the source alone can see.
        var after = PostTransformNames(file, rows);
        foreach (var row in rows)
        {
            if (row.Kind != SelfDispositionKind.Replace) continue;
            string counterpart = CloneFile.Normalize(row.Counterpart!);
            if (after.Count(n => string.Equals(n, counterpart, StringComparison.Ordinal)) > 1)
                return $"{counterpart} is already in this file's address book once the change is made — "
                    + "an ALE name is unique across selfs, individuals and nets.";
        }

        // ---- 14. the no-self file takes exactly ONE synthetic Replace row -----
        if (noSelfFile)
        {
            // No rows, or a synthetic row nobody filled in, is the standing
            // preflight rejection: the operator repairs it by choosing a name,
            // so it is a refusal WITH AN INSTRUCTION rather than a dead end.
            if (rows.Count == 0) return CloneService.NoSelfRejection;
            if (rows.Count == 1 && rows[0].Kind == SelfDispositionKind.Keep)
                return CloneService.NoSelfRejection;
            if (rows.Count != 1 || rows[0].Kind != SelfDispositionKind.Replace)
                return "This file has no self — the one row must give this radio a new name.";
        }

        return null;
    }

    /// <summary>
    /// Apply the table. PURE, TOTAL and deterministic over anything
    /// <see cref="Refusal"/> accepted (I-4): it never mutates its input, never
    /// invents a row, and has a defined answer for every state the radio can
    /// actually produce. A table it did NOT accept throws
    /// <see cref="CloneValueException"/> carrying that refusal, before any
    /// change — so a caller that skipped the preflight cannot half-transform a
    /// file instead.
    /// </summary>
    public static CloneSwapResult Apply(CloneFile source, IReadOnlyList<SelfDisposition> rows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        if (Refusal(source, rows) is { } refusal) throw new CloneValueException(refusal);

        var file = DeepCopy(source);
        var drops = new List<string>();
        var roleChanges = new List<string>();

        // The transform is SIMULTANEOUS over the SOURCE snapshot (§3.2): every
        // step reads `source`, never a half-built result, so no row can be
        // judged against another row's effect.
        var byName = new Dictionary<string, SelfDisposition>(StringComparer.Ordinal);
        foreach (var row in rows) byName[CloneFile.Normalize(row.SelfName ?? "")] = row;

        if (source.Selfs.Count == 0)
        {
            // The synthetic row (A-6): one new self, group 0. Nothing demotes
            // and nothing re-points — there was no old self to name.
            string name = CloneFile.Normalize(rows[0].Counterpart!);
            file.Selfs = [new CloneAddress { Name = name, Group = 0 }];
            roleChanges.Add($"{name} is the radio's self.");
        }
        else
        {
            // 1. old self → new name, from the source snapshot.
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var self in source.Selfs)
            {
                string key = CloneFile.Normalize(self.Name);
                if (!byName.TryGetValue(key, out var row)) continue;
                if (row.Kind == SelfDispositionKind.Keep) continue;
                map[key] = NewOccupantName(source, row);
            }

            // 2. the selfs, IN SOURCE ORDER, each slot holding its occupant.
            var selfs = new List<CloneAddress>(source.Selfs.Count);
            foreach (var self in source.Selfs)
            {
                byName.TryGetValue(CloneFile.Normalize(self.Name), out var row);
                if (row is null || row.Kind == SelfDispositionKind.Keep)
                {
                    selfs.Add(CopyAddress(self));
                    continue;
                }
                selfs.Add(new CloneAddress
                {
                    Name = NewOccupantName(source, row),
                    // A swapped-in individual keeps its OWN channel group; a
                    // typed name INHERITS the slot's (R-A).
                    Group = row.Kind == SelfDispositionKind.SwapWithIndividual
                        ? MatchIndividual(source, row.Counterpart!)!.Group
                        : self.Group,
                });
            }
            file.Selfs = selfs;

            // 3. the individuals: the survivors (minus everyone promoted into a
            //    slot), then one demoted old self per Swap/Replace, in row order.
            var promoted = rows
                .Where(r => r.Kind == SelfDispositionKind.SwapWithIndividual)
                .Select(r => CloneFile.Normalize(r.Counterpart!))
                .ToHashSet(StringComparer.Ordinal);
            var individuals = source.Individuals
                .Where(i => !promoted.Contains(CloneFile.Normalize(i.Name)))
                .Select(CopyAddress)
                .ToList();

            // 4. re-point the SURVIVORS before the demoted rows join them: a
            //    demoted row's association is already the new name.
            foreach (var individual in individuals)
                individual.AssociatedSelf = RePoint(map, individual.AssociatedSelf);

            foreach (var row in rows)
            {
                if (row.Kind == SelfDispositionKind.Keep) continue;
                var old = source.Selfs.First(
                    s => string.Equals(CloneFile.Normalize(s.Name), CloneFile.Normalize(row.SelfName), StringComparison.Ordinal));
                string name = NewOccupantName(source, row);

                // C-2 (owner rule 3, 2026-08-22): the SCAN-GATE self is
                // REPLACED, not demoted — "for the 3-letter self there should
                // NOT be an individual created that is associated with it".
                // The slot keeps its group (as ever) and `map` still re-points
                // anything that named the old name; the old name simply leaves
                // the book.
                if (IsScanGateReplace(old, row))
                {
                    roleChanges.Add($"{name} replaces {old.Name} as the scan-gate self.");
                    continue;
                }

                individuals.Add(new CloneAddress
                {
                    Name = old.Name,
                    Group = old.Group,
                    AssociatedSelf = name,
                });
                roleChanges.Add(row.Kind == SelfDispositionKind.SwapWithIndividual
                    ? $"{name} is now a self in {old.Name}'s place."
                    : $"{name} is the new self in {old.Name}'s place.");
                roleChanges.Add($"{old.Name} is now an individual of {name}.");
            }
            file.Individuals = individuals;

            // …and everything else that named an old self. ASSOCIATIONS follow
            // the one-way map (a net that hung off P now hangs off the new
            // occupant), but MEMBER LISTS and SCHEDULE ADDRESSES follow the
            // ADDRESS map — which, for a Swap, EXCHANGES the pair (A-13).
            var addresses = AddressMap(source, rows);
            foreach (var net in file.Nets)
            {
                net.AssociatedSelf = RePoint(map, net.AssociatedSelf);
                for (int i = 0; i < net.Members.Count; i++)
                    net.Members[i] = RePoint(addresses, net.Members[i]) ?? net.Members[i];
            }
            foreach (var row in file.Schedules)
                row.Address = RePoint(addresses, row.Address) ?? row.Address;
        }

        // 5. The unreplayable-state rules run in EVERY branch — a blank-assoc
        //    net is unwritable no matter what the table said, and the all-Keep
        //    branch must be just as total as the others.
        DropBlankAssociationNets(file, drops);
        DropInvalidMembers(file, drops);
        DropInvalidSchedules(file, drops);

        return new CloneSwapResult(file, drops, roleChanges);
    }

    /// <summary>The name that ends up in the slot: a Swap uses the matched
    /// source individual's STORED name verbatim (it is already the radio's own
    /// spelling), a Replace the normalized typed one.</summary>
    private static string NewOccupantName(CloneFile source, SelfDisposition row)
        => row.Kind == SelfDispositionKind.SwapWithIndividual
            ? MatchIndividual(source, row.Counterpart!)!.Name
            : CloneFile.Normalize(row.Counterpart!);

    /// <summary>C-2: a Replace of the file's SCAN-GATE self. The one branch
    /// that drops the demoted row — read by <see cref="Apply"/> and by
    /// <see cref="PostTransformNames"/>, so the refusal and the transform
    /// count the same book.</summary>
    private static bool IsScanGateReplace(CloneAddress old, SelfDisposition row)
        => row.Kind == SelfDispositionKind.Replace && IsScanGateSelf(old.Name);

    private static CloneAddress? MatchIndividual(CloneFile file, string name)
    {
        string x = CloneFile.Normalize(name);
        return file.Individuals.FirstOrDefault(
            i => string.Equals(CloneFile.Normalize(i.Name), x, StringComparison.Ordinal));
    }

    /// <summary>
    /// How an ADDRESS — a net member or a schedule target — reads after the
    /// table is applied (A-13, phase-2 audit round 1).
    ///
    /// <para>A Replace RENAMES: P becomes N wherever it is used. A Swap
    /// EXCHANGES: P's slot becomes X and X's slot becomes P, each keeping its
    /// POSITION. That is what the manual requires — a net's member list must be
    /// identical in count, order and spelling on every station (§2.6.4.3.3) and
    /// the associated self is itself a member — and it is also the only mapping
    /// that cannot produce a duplicate. A one-way rename collapsed a net that
    /// listed BOTH P and X into <c>[X, X]</c>, which the radio refuses with
    /// `DUPLICATE MEMBER` after the erase: the audit reproduced it end to end.
    /// After the swap this radio IS X, and the station that was P is an
    /// individual sitting in P's old slot — so the exchange is also what the
    /// list MEANS.</para>
    ///
    /// <para>ASSOCIATIONS are NOT exchanged: they name the self a row hangs
    /// off, and after the swap that self is X.</para>
    /// </summary>
    private static Dictionary<string, string> AddressMap(CloneFile source, IReadOnlyList<SelfDisposition> rows)
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            var old = source.Selfs.FirstOrDefault(s => string.Equals(
                CloneFile.Normalize(s.Name), CloneFile.Normalize(row.SelfName ?? ""), StringComparison.Ordinal));
            if (old is null) continue;                       // the synthetic no-self row
            string name = NewOccupantName(source, row);
            addresses[CloneFile.Normalize(old.Name)] = name;
            if (row.Kind == SelfDispositionKind.SwapWithIndividual)
                addresses[CloneFile.Normalize(name)] = old.Name;      // the other half of the exchange
        }
        return addresses;
    }

    private static string? RePoint(Dictionary<string, string> map, string? name)
        => name is not null && map.TryGetValue(CloneFile.Normalize(name), out var replacement)
            ? replacement
            : name;

    /// <summary>Every name the book holds AFTER the table is applied, as a LIST
    /// rather than a set — the collision this rule exists to catch is a name
    /// appearing TWICE, and a set cannot say that.</summary>
    private static List<string> PostTransformNames(CloneFile file, IReadOnlyList<SelfDisposition> rows)
    {
        var byName = new Dictionary<string, SelfDisposition>(StringComparer.Ordinal);
        foreach (var row in rows) byName[CloneFile.Normalize(row.SelfName ?? "")] = row;

        var names = new List<string>();
        var promoted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Kind == SelfDispositionKind.Keep) continue;
            names.Add(CloneFile.Normalize(row.Counterpart!));                       // the new occupant
            if (row.Kind == SelfDispositionKind.SwapWithIndividual)
                promoted.Add(CloneFile.Normalize(row.Counterpart!));
            string old = CloneFile.Normalize(row.SelfName ?? "");
            if (old.Length == 0) continue;                                          // the synthetic row
            // C-2: a replaced SCAN-GATE self is not demoted — its name leaves
            // the book, so counting it here would refuse a name that really is
            // free afterwards. Apply and this count read the same branch.
            var self = file.Selfs.FirstOrDefault(
                s => string.Equals(CloneFile.Normalize(s.Name), old, StringComparison.Ordinal));
            if (self is not null && IsScanGateReplace(self, row)) continue;
            names.Add(old);                                                         // the demoted old self
        }
        foreach (var self in file.Selfs)
        {
            string name = CloneFile.Normalize(self.Name);
            if (byName.TryGetValue(name, out var row) && row.Kind != SelfDispositionKind.Keep) continue;
            names.Add(name);                                                        // a kept self
        }
        foreach (var individual in file.Individuals)
        {
            string name = CloneFile.Normalize(individual.Name);
            if (!promoted.Contains(name)) names.Add(name);                          // an untouched individual
        }
        return names;
    }

    private static string NotASelf(string name)
        => $"{name} is not a self in this file — every row belongs to one of the file's own selfs.";

    /// <summary>How a refusal names the row it is about: the self, or the
    /// synthetic row's stand-in when the file has no self to name.</summary>
    private static string Subject(string normalizedSelfName)
        => normalizedSelfName.Length > 0 ? normalizedSelfName : "The radio's new self";


    /// <summary>A net with no associated self cannot be programmed at all
    /// (<c>NETAD</c> takes one and it must exist), so it goes — loudly.</summary>
    private static void DropBlankAssociationNets(CloneFile file, List<string> drops)
    {
        foreach (var net in file.Nets.Where(n => string.IsNullOrWhiteSpace(n.AssociatedSelf)).ToList())
        {
            file.Nets.Remove(net);
            drops.Add($"Net {net.Name}: dropped — it has no associated self, "
                + "so the radio cannot store it. (Its self was deleted on the source radio.)");
        }
    }

    /// <summary>The radio accepts as members: individuals, and exactly the
    /// net's OWN associated self. A self member of any other net is refused
    /// on the wire, so the transform drops it here instead of collecting a
    /// refusal there.</summary>
    private static void DropInvalidMembers(CloneFile file, List<string> drops)
    {
        var selfs = file.Selfs.Select(s => CloneFile.Normalize(s.Name)).ToHashSet(StringComparer.Ordinal);
        var known = KnownNames(file);

        foreach (var net in file.Nets)
        {
            string? assoc = net.AssociatedSelf is null ? null : CloneFile.Normalize(net.AssociatedSelf);
            foreach (var member in net.Members.ToList())
            {
                string m = CloneFile.Normalize(member);
                if (!known.Contains(m))
                {
                    net.Members.Remove(member);
                    drops.Add($"Member {member} of net {net.Name}: dropped — "
                        + "that address is no longer in the file's address book.");
                    continue;
                }
                if (selfs.Contains(m) && !string.Equals(m, assoc, StringComparison.Ordinal))
                {
                    net.Members.Remove(member);
                    drops.Add($"Member {member} of net {net.Name}: dropped — "
                        + "only the net's own associated self can be a member.");
                }
            }
        }
    }

    /// <summary>EXCHANGE takes an individual or a net and REFUSES a self;
    /// SOUND takes a self only. A row whose target changed kind under the
    /// swap — or left the book with a dropped net — is dropped and listed.</summary>
    private static void DropInvalidSchedules(CloneFile file, List<string> drops)
    {
        var selfs = file.Selfs.Select(s => CloneFile.Normalize(s.Name)).ToHashSet(StringComparer.Ordinal);
        var known = KnownNames(file);

        foreach (var row in file.Schedules.ToList())
        {
            string address = CloneFile.Normalize(row.Address);
            if (!known.Contains(address))
            {
                file.Schedules.Remove(row);
                drops.Add($"Schedule {row.Kind} {row.Address}: dropped — "
                    + "that address is no longer in the file's address book.");
                continue;
            }
            bool isSelf = selfs.Contains(address);
            if (row.Kind == "SOUND" && !isSelf)
            {
                file.Schedules.Remove(row);
                drops.Add($"Schedule SOUND {row.Address}: dropped — soundings run from a self, "
                    + "and this address is no longer one.");
            }
            else if (row.Kind == "EXCHANGE" && isSelf)
            {
                file.Schedules.Remove(row);
                drops.Add($"Schedule EXCHANGE {row.Address}: dropped — exchanges run against another "
                    + "station, and this address is now this radio's own self.");
            }
        }
    }

    private static HashSet<string> KnownNames(CloneFile file) =>
    [
        .. file.Selfs.Select(s => CloneFile.Normalize(s.Name)),
        .. file.Individuals.Select(i => CloneFile.Normalize(i.Name)),
        .. file.Nets.Select(n => CloneFile.Normalize(n.Name)),
    ];

    /// <summary>A by-hand deep copy: the transform is PURE, so it may not
    /// share a single list or row with its input.</summary>
    private static CloneFile DeepCopy(CloneFile s) => new()
    {
        Version = s.Version,
        CapturedUtc = s.CapturedUtc,
        OperatingState = s.OperatingState,
        OperatingMode = s.OperatingMode,
        OperatingChannel = s.OperatingChannel,
        OperatingHopNet = s.OperatingHopNet,
        BookState = s.BookState,
        Selfs = [.. s.Selfs.Select(CopyAddress)],
        Individuals = [.. s.Individuals.Select(CopyAddress)],
        Nets = [.. s.Nets.Select(n => new CloneNet
        {
            Name = n.Name, Group = n.Group, AssociatedSelf = n.AssociatedSelf, Members = [.. n.Members],
        })],
        GroupState = s.GroupState,
        ChannelGroups = [.. s.ChannelGroups.Select(g => new CloneChannelGroup
        {
            Group = g.Group, Channels = [.. g.Channels],
        })],
        ScheduleState = s.ScheduleState,
        Schedules = [.. s.Schedules.Select(r => new CloneSchedule
        {
            Kind = r.Kind, Address = r.Address, Interval = r.Interval, Start = r.Start,
        })],
        ChannelState = s.ChannelState,
        // D4/D6: the elision MARKER travels with the rows it describes. A copy
        // that dropped it would turn a sparse file into a file claiming 100
        // slots it does not have — and the write preflight revalidates this
        // very graph, so the loss would surface as a bogus rejection.
        DefaultChannelsElided = s.DefaultChannelsElided,
        Channels = [.. s.Channels.Select(c => new CloneChannel
        {
            Number = c.Number, RxFrequency = c.RxFrequency, TxFrequency = c.TxFrequency,
            Mode = c.Mode, Agc = c.Agc, Bandwidth = c.Bandwidth, RxOnly = c.RxOnly,
        })],
        HopNetState = s.HopNetState,
        HopNets = [.. s.HopNets.Select(n => new CloneHopNet
        {
            Number = n.Number, Wiped = n.Wiped, NetId = n.NetId, Type = n.Type,
            CenterKHz = n.CenterKHz, LowKHz = n.LowKHz, HighKHz = n.HighKHz,
            ListFrequencies = [.. n.ListFrequencies],
        })],
        ExcludeState = s.ExcludeState,
        ExcludeBands = [.. s.ExcludeBands.Select(b => new CloneExcludeBand
        {
            Band = b.Band, LowKHz = b.LowKHz, HighKHz = b.HighKHz,
        })],
        ModemState = s.ModemState,
        ModemPresets = [.. s.ModemPresets.Select(p => new CloneModemPreset
        {
            Number = p.Number, Fields = p.Fields, Enabled = p.Enabled,
        })],
        MessageState = s.MessageState,
        Messages = [.. s.Messages.Select(m => new CloneTxMessage { Slot = m.Slot, Text = m.Text })],
        SettingState = s.SettingState,
        Settings = [.. s.Settings.Select(v => new CloneSetting { Key = v.Key, Value = v.Value })],
        // The operator lockouts pass through IDENTITY-UNTOUCHED, and that is a
        // DISPOSITION rather than an omission (plan-clone-round12 §6): a swap
        // changes which station this radio IS — book roles, associations, the
        // rows that name them — and a front-panel lockout names none of those.
        // The rows are still deep-COPIED, because the transform is pure and may
        // not share a list with its input.
        Lockouts = s.Lockouts is null ? null : new CloneLockouts
        {
            State = s.Lockouts.State,
            Rows = [.. s.Lockouts.Rows.Select(r => new CloneLockout
            {
                Family = r.Family, Section = r.Section, Item = r.Item, State = r.State,
            })],
        },
    };

    private static CloneAddress CopyAddress(CloneAddress a) =>
        new() { Name = a.Name, Group = a.Group, AssociatedSelf = a.AssociatedSelf };
}
