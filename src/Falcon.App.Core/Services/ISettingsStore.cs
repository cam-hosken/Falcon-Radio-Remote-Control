namespace Falcon.App.Core.Services;

/// <summary>
/// ROUND 14 G (plan/plan-round14.md §Phase G, owner report R18) — the app's
/// one persistent setting seam: strings in, strings out, keyed by the caller.
///
/// <para><b>Why an interface at all.</b> The only implementation is MAUI's
/// <c>Preferences</c>, which lives in <c>Falcon.App</c> and does not exist in
/// this host-testable layer (the same split that puts every ViewModel here).
/// A ViewModel that called <c>Preferences</c> directly could not be tested at
/// all, and the first test that ran would write to the machine's real
/// settings store.</para>
///
/// <para><b>Why it is this small.</b> No typed API, no change events, no
/// namespacing helper, no async: the one thing the app remembers is the
/// operator's chosen serial port, and a seam wider than its single use is a
/// seam nobody can reason about. It grows when a second setting arrives, not
/// before.</para>
///
/// <para><b>Contract.</b> <see cref="Get"/> returns null for a key that was
/// never set, and for one stored as null or empty — "absent" and "empty" are
/// the same answer, because a remembered port name is never empty.
/// <see cref="Set"/> with a null or empty value FORGETS the key, which is what
/// makes "no preference" storable.</para>
/// </summary>
public interface ISettingsStore
{
    /// <summary>The stored value, or null if this key holds nothing.</summary>
    string? Get(string key);

    /// <summary>Store a value, or forget the key when the value is null or
    /// empty. Takes effect immediately — the next process reads it back.</summary>
    void Set(string key, string? value);
}
