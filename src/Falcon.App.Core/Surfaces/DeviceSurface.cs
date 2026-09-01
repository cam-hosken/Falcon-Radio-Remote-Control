using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>
/// Device slice (GUI rejigger E4 — the Radio settings → Settings sub-tab):
/// the genuinely mode-FREE-on-the-wire set (protocol.md "answered in every
/// mode" — LIG/CONT/TI/BAT ST). No OperatingMode is watched: these controls
/// are always accessible (they render "—" when the value has not been
/// reported this session), gated only by the SESSION being Ready.
///
/// Read-back (UI-tweaks round 4, AC — R4-Q1 mining): all three display
/// settings now have a bare-query READ. `LIG` and `CONT` are this project's
/// own bench facts (the sentinel probe answered both in every mode); `INT`
/// and the LIGHT/INTENSITY answer PAYLOADS are OLD-APP-DERIVED and
/// PROVISIONAL — mirrored verbatim, marked in docs/protocol.md's round-4
/// subsection, with bench-confirm items. CONTRAST keeps its bench-confirmed
/// int mirror (it also populates from its own set echo). Battery status and
/// the radio clock keep their direct queries (BAT ST / TI).
/// </summary>
public sealed class DeviceSurface : RadioSurface
{
    public DeviceSurface(Prc138Radio radio)
        : base(radio,
            RadioProperty.Contrast, RadioProperty.BatteryStatus,
            RadioProperty.RadioTimeOfDay,
            RadioProperty.BacklightFunction, RadioProperty.BacklightIntensity)
    { }

    /// <summary>Display contrast 0-8 ("CONTRAST nn"); confirmed by the bare-CONT
    /// read (round 4) and by the set echo.</summary>
    public Confirmed<int> Contrast => Radio.State.Contrast;

    /// <summary>Verbatim BATTERY payload ("Status FULL 31.4V").</summary>
    public Confirmed<string> BatteryStatus => Radio.State.BatteryStatus;

    /// <summary>Radio clock TOD, verbatim TIME payload ("20:37:12").</summary>
    public Confirmed<string> RadioTimeOfDay => Radio.State.RadioTimeOfDay;

    /// <summary>LIGHT payload verbatim — PROVISIONAL (old-app-derived
    /// "OFF"/"MOMENTARY").</summary>
    public Confirmed<string> BacklightFunction => Radio.State.BacklightFunction;

    /// <summary>INTENSITY payload verbatim — PROVISIONAL (old-app-derived
    /// "00".."08"; the zero-padding is a bench item).</summary>
    public Confirmed<string> BacklightIntensity => Radio.State.BacklightIntensity;

    // ---- Intents ----------------------------------------------------------

    /// <summary>Backlight function (LIG OFF|MOMENTARY). The LIGHT echo/answer
    /// is mirrored PROVISIONALLY (round 4).</summary>
    public void SetBacklightFunction(BacklightFunction function) => Radio.SetBacklightFunction(function);

    /// <summary>Backlight intensity (INT 0-8). The INTENSITY echo/answer is
    /// mirrored PROVISIONALLY (round 4).</summary>
    public void SetBacklightIntensity(int intensity) => Radio.SetBacklightIntensity(intensity);

    /// <summary>Display contrast (CONT 0-8). The "CONTRAST nn" echo is mirrored.</summary>
    public void SetContrast(int contrast) => Radio.SetContrast(contrast);

    /// <summary>TIME + DAT + DAY from the device clock, zero-padded — the
    /// existing all-three set (DAT does not recompute DAY). Same command
    /// shape the HOP pane's "set from device clock" uses; device-wide.</summary>
    public void SetTimeOfDay(DateTime now) => Radio.Hop.SetTimeOfDay(now);

    /// <summary>BAT ST — battery status (answered in every mode).</summary>
    public void RequestBattery() => Radio.QueryBatteryState();

    /// <summary>TI — radio clock (DAY/DATE/TIME triplet answer).</summary>
    public void RequestTime() => Radio.QueryTime();

    /// <summary>LIG (bare) — backlight function read (answered in every mode).</summary>
    public void RequestBacklightFunction() => Radio.QueryBacklightFunction();

    /// <summary>INT (bare) — backlight intensity read. PROVISIONAL: never sent
    /// to this radio (bench item).</summary>
    public void RequestBacklightIntensity() => Radio.QueryBacklightIntensity();

    /// <summary>CONT (bare) — contrast read (answered in every mode).</summary>
    public void RequestContrast() => Radio.QueryContrast();
}
