using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Power slice: reported level, thermal-cutback flag (POWER
/// CUTBACK / POWER RESTORED), and the set intent.</summary>
public sealed class PowerSurface : RadioSurface
{
    public PowerSurface(Prc138Radio radio)
        : base(radio, RadioProperty.PowerLevel, RadioProperty.PowerCutback) { }

    public Confirmed<PowerLevel> Level => Radio.State.PowerLevel;

    /// <summary>True after POWER CUTBACK, false after POWER RESTORED —
    /// shows the ⚠ note on the spine.</summary>
    public Confirmed<bool> Cutback => Radio.State.PowerCutback;

    public void Set(PowerLevel level) => Radio.SetPowerLevel(level);
}
