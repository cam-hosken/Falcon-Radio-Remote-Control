using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>
/// Base for the Q9 per-domain surfaces (plan §2.3 Q9: the old 90-member
/// IRadio is NOT inherited; ViewModels consume small per-domain slices).
/// A surface exposes read access to its slice of the reported-state mirror,
/// the explicit intents for that domain, and one <see cref="Changed"/> event
/// filtered to the domain's <see cref="RadioProperty"/> set. Events arrive
/// already marshalled (Prc138Radio Q10) — surfaces add no threading.
/// </summary>
public abstract class RadioSurface
{
    private protected readonly Prc138Radio Radio;

    /// <summary>Raised (marshalled) when any watched property changes.</summary>
    public event EventHandler? Changed;

    private protected RadioSurface(Prc138Radio radio, params RadioProperty[] watched)
    {
        Radio = radio;
        var set = new HashSet<RadioProperty>(watched);
        radio.StateChanged += (_, e) =>
        {
            if (set.Contains(e.PropertyChanged)) Changed?.Invoke(this, EventArgs.Empty);
        };
    }
}
