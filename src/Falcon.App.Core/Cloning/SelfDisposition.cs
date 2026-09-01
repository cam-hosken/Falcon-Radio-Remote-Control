namespace Falcon.App.Core.Cloning;

/// <summary>What the operator decided to do with ONE of the file's selfs
/// (plan-clone-field-round2 §3.1, owner ruling R-A).</summary>
public enum SelfDispositionKind
{
    /// <summary>Written as-is. The default, and what an OMITTED row means.</summary>
    Keep,
    /// <summary>An INDIVIDUAL from the file takes this self's slot; this self
    /// demotes to an individual associated to it.</summary>
    SwapWithIndividual,
    /// <summary>A typed NEW name takes this self's slot, inheriting its channel
    /// group; this self demotes to an individual associated to it.</summary>
    Replace,
}

/// <summary>
/// One row of the identity table (R-A): a self in the file, and what happens to
/// it. The write's identity step is a LIST of these — one per self — replacing
/// round 11's single chosen identity, which could only ever move the FIRST self
/// and did so silently.
/// </summary>
/// <param name="SelfName">The self's name AS IN THE FILE (matched
/// uppercase-normalized, the radio's own rule). <c>""</c> ONLY for the synthetic
/// "no self in the file" row (A-6), which a post-ERASE source legitimately
/// produces and which is <see cref="SelfDispositionKind.Replace"/>-only.</param>
/// <param name="Kind">What to do with it.</param>
/// <param name="Counterpart">Swap: the individual's name. Replace: the new
/// name. Keep: null — a Keep row that also names a counterpart is REFUSED
/// rather than silently interpreted.</param>
public sealed record SelfDisposition(
    string SelfName,
    SelfDispositionKind Kind,
    string? Counterpart);
