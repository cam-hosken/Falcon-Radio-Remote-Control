using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Coupler slice (Stage 4): the Retune intent plus the tune
/// lifecycle flags the TUNE button gates on. The spine's tune chip
/// (StatusSurface/SpineStatusViewModel) remains the status DISPLAY; this
/// surface exists for the button. FAULT is a routine outcome on this
/// radio's flaky coupler — recovery is simply tuning again.</summary>
public sealed class CouplerSurface : RadioSurface
{
    public CouplerSurface(Prc138Radio radio)
        : base(radio,
            RadioProperty.Tuning, RadioProperty.TuneComplete,
            RadioProperty.TuneMarginal, RadioProperty.TuneFail)
    { }

    public bool IsTuning => Radio.State.IsTuning;
    public bool IsTuneComplete => Radio.State.IsTuneComplete;
    public bool IsTuneMarginal => Radio.State.IsTuneMarginal;
    public bool IsTuneFail => Radio.State.IsTuneFail;

    /// <summary>RETU — retune the antenna coupler (TRANSMITS during the tune).</summary>
    public void Retune() => Radio.Ssb.Retune();
}
