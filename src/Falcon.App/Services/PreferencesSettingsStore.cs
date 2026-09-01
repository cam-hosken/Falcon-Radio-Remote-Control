using Falcon.App.Core.Services;

namespace Falcon.App.Services;

/// <summary>
/// ROUND 14 G — <see cref="ISettingsStore"/> over MAUI's
/// <c>Preferences.Default</c>: the per-app key/value store each platform
/// already provides (Windows: the app's local settings; Android:
/// SharedPreferences). Chosen over a file because it is the platform's own
/// answer to "remember this one small thing", needs no path, no IO error
/// handling and no first-run bootstrap.
///
/// <para>The <see cref="ISettingsStore"/> contract is honoured here rather
/// than at the call site: an empty stored value reads back as NULL (absent and
/// empty are one answer), and storing null or empty REMOVES the key instead of
/// writing a blank one.</para>
///
/// <para>Behaviour is pinned as SOURCE — this file lives in Falcon.App, whose
/// android/windows TFMs the host-only test project cannot reference (see
/// ConnectionFlowSourceGuardTests for the same limitation and the same
/// treatment). What the tests DO execute is every consumer of the seam,
/// against a fake store.</para>
/// </summary>
public sealed class PreferencesSettingsStore : ISettingsStore
{
    public string? Get(string key)
    {
        var value = Preferences.Default.Get(key, string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Preferences.Default.Remove(key);
        else Preferences.Default.Set(key, value);
    }
}
