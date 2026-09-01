using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Display-only spine status slice: keyline (RX/TX indicator — the
/// old rdoRx/rdoTx looked like inputs; they never were) and the tune
/// lifecycle flags behind the tune status chip. No intents: keying is out of
/// the v1 surface entirely, and the TUNE button is Stage 4's SSB pane.</summary>
public sealed class StatusSurface : RadioSurface
{
    public StatusSurface(Prc138Radio radio)
        : base(radio,
            RadioProperty.Keyline,
            RadioProperty.Tuning, RadioProperty.TuneComplete,
            RadioProperty.TuneMarginal, RadioProperty.TuneFail)
    { }

    public Confirmed<KeylineState> Keyline => Radio.State.Keyline;

    public bool IsTuning => Radio.State.IsTuning;
    public bool IsTuneComplete => Radio.State.IsTuneComplete;
    /// <summary>Qualifier on a completed tune, not a fourth outcome (plan §3).</summary>
    public bool IsTuneMarginal => Radio.State.IsTuneMarginal;
    /// <summary>TUNE FAULT — a routine, recoverable outcome on this radio's
    /// flaky coupler; display state, not an error flow.</summary>
    public bool IsTuneFail => Radio.State.IsTuneFail;
}
