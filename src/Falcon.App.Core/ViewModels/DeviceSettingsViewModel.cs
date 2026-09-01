using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The Radio settings → Settings sub-tab (GUI rejigger E4): the device-wide,
/// mode-FREE set — backlight function (LIG) + intensity (INT), contrast
/// (CONT), set date/time (TIME+DAT+DAY from the device clock), and a battery
/// status display (BAT ST). Always accessible: every value renders "—" until
/// the radio reports it, and the controls are gated only on the session being
/// Ready (no mode gate — protocol.md "answered in every mode").
///
/// Constitution: display is ONE-WAY from the confirmed mirror. CONTRAST comes
/// from its bench-confirmed "CONTRAST nn" mirror, backlight function and
/// intensity from the round-4 PROVISIONAL verbatim mirrors (LIGHT/INTENSITY —
/// old-app-derived shapes, docs/protocol.md round-4 subsection). Every one
/// renders "—" until the radio reports it; a set NEVER moves a display.
///
/// <para><b>CLONE ROUND 12 — the Display card, rebuilt from the bench.</b>
/// §9 C1: the backlight FUNCTION row was an accent chip beside two
/// trigger-less Segment buttons — nothing could ever highlight. It is now the
/// house ChoiceItem row off the confirmed mirror, and the chip is deleted.
/// §9 C2: the intensity and contrast rows were a numeric Entry plus a Set
/// button; they are now ◀/▶ chevron pairs, SEND-ON-PRESS (confirmed ± 1,
/// clamped 0-8, no wrap), rate-limited like every other held chevron. The
/// readout stays the Option-B CONFIRMED display beside them, so the card still
/// shows only what the radio said. With no free-text input left on this card,
/// the Entry buffers and their shared client-side <c>InputError</c> note are
/// RETIRED.</para>
///
/// Lazy load (plan Q4 / N4 device-settings): the tab queries the mode-free
/// device read set ONCE per session via <see cref="EnsureLoaded"/> — driven by
/// the page's OnAppearing, by DeviceClockView's own Loaded (round 4, K2) AND
/// by the session reaching Ready while on the page — plus a manual Refresh.
/// Round 4 (AC) added the three display reads (LIG/INT/CONT) to that set, so
/// contrast no longer depends on its own set echo. Under DEMO the canned
/// script answers none of these, so every display stays "—" and the buttons
/// send+log — correct, not a bug (plan RULES).
/// </summary>
public partial class DeviceSettingsViewModel : ObservableObject
{
    private const int IntMin = 0, IntMax = 8;
    private const int ContrastMin = 0, ContrastMax = 8;

    private readonly DeviceSurface _device;
    private readonly RadioSession _session;
    private readonly TimeProvider _time;

    private bool _loadedThisSession;

    // Confirmed-state displays (one-way from the mirror).
    [ObservableProperty] private string contrastText = "—";
    [ObservableProperty] private string batteryText = "—";
    [ObservableProperty] private string radioTodText = "—";
    // Round 4 (AC): a PROVISIONAL verbatim payload — shown exactly as reported
    // ("04"), never re-formatted, so the bench sees the truth.
    //
    // CLONE ROUND 12 §9 C1: the BACKLIGHT FUNCTION readout is GONE with its
    // chip. Its row is now a highlighted choice pair off the same mirror, and
    // the highlight IS the readout. Recorded consequence: an unmapped third
    // payload spelling no longer renders verbatim anywhere — it leaves the row
    // un-highlighted, which is the round-3 provisional-mirror precedent
    // (preamp / internal coupler / 1 kW PA all behave exactly this way).
    [ObservableProperty] private string backlightIntensityText = "—";

    /// <summary>CLONE ROUND 12 §9 C1 — the BACKLIGHT FUNCTION choice row. It
    /// used to be an accent <c>ValueDisplay</c> chip beside two TRIGGER-LESS
    /// Segment buttons: nothing on that row could ever highlight, so the
    /// operator could not see which function was set without reading the chip.
    /// It is now the house <c>ChoiceItem</c> row off the CONFIRMED mirror, the
    /// same idiom every other choice row in the app uses, and the chip is
    /// deleted (the highlight IS the readout).</summary>
    [ObservableProperty] private IReadOnlyList<ChoiceItem> backlightFunctionChoices = [];

    /// <summary>CLONE ROUND 12 §9 C2 — the chevron pairs' gate. A press sends
    /// CONFIRMED ± 1, so with nothing confirmed there is no basis to step from
    /// (the RF-gain / BFO / modem-wheel "step from confirmed" idiom).</summary>
    [ObservableProperty] private bool canStepBacklightIntensity;
    [ObservableProperty] private bool canStepContrast;

    /// <summary>Repeat-fire clamp for held chevrons — the same 125 ms
    /// discipline as the VFO and channel spinners (drop, never queue).</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(125);

    private readonly RepeatRateLimiter _intensityLimiter;
    private readonly RepeatRateLimiter _contrastLimiter;

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 13). This pane is the one P0 named as debt-capable — its <c>BAT ST</c>
    /// goes out as a BARE query, outside Core's ping queue, so it is exactly
    /// the shape that can leave a sentinel unpaid under a campaign. Null where
    /// there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>An explicit Refresh PRESS accepted while a campaign owned the
    /// wire (§4 SUPPRESSION SCOPE): the press stands, the reads run once the
    /// wire is free, and the button never greys.
    /// <para>Settled by this pane's OWN gate, not by the campaign edge (audit
    /// round 1): a campaign that ends because the SESSION DROPPED must not
    /// consume the debt — the pane cannot read, and the latch would be thrown
    /// away. A drop discards it deliberately instead, exactly like the load
    /// flag beside it.</para></summary>
    private bool _refreshPressOwed;

    public DeviceSettingsViewModel(
        DeviceSurface device, RadioSession session, TimeProvider time,
        ICampaignSignal? campaign = null)
    {
        _device = device;
        _session = session;
        _time = time;
        _campaign = campaign;
        _intensityLimiter = new RepeatRateLimiter(time, RepeatInterval);
        _contrastLimiter = new RepeatRateLimiter(time, RepeatInterval);
        device.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _loadedThisSession = false;
                _refreshPressOwed = false;      // the press was for the radio that left
            }
            else EnsureLoaded();   // reaching Ready loads even without an OnAppearing
            Refresh();
        };
        // The campaign's END edge settles whatever is owed — IF this pane can
        // read now. It cannot when the campaign ended on a session drop, and
        // then the drop branch above is what disposes of the debt.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) PayWhatIsOwed(); };
        Refresh();
    }

    /// <summary>Settle the deferred reads, ONCE, and only while this pane's own
    /// gate allows one. A deferred press and a still-owed lazy load are the same
    /// five reads, so at most one set goes out.</summary>
    private void PayWhatIsOwed()
    {
        if (!Ready) return;                     // still owed; the gate decides, not the edge
        bool lazyWillRead = !_loadedThisSession;
        EnsureLoaded();
        if (!_refreshPressOwed) return;
        _refreshPressOwed = false;
        if (!lazyWillRead) SendDeviceReads();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>Lazy first load, once per session (plan N4 device-settings).
    /// THREE triggers, all funnelling through this once-guarded call: the
    /// Settings tab appearing while Ready (page OnAppearing); the session
    /// reaching Ready while already on the page (PhaseChanged) — so the display
    /// is never stuck at "—" if the operator connects without leaving the page;
    /// and (round 4, K2) DeviceClockView's own Loaded, in EITHER of its two
    /// placements — which is the only trigger that fires when this singleton is
    /// first constructed by the HOP settings pane AFTER the session is already
    /// Ready. Queries the five mode-free reads: LIG, INT, CONT, BAT ST, TI (see
    /// SendDeviceReads). The mirror is the cache, so a second trigger does not
    /// re-query. Idempotent and safe to call any time.</summary>
    public void EnsureLoaded()
    {
        if (!Ready || _loadedThisSession) return;
        // D1 QUIESCE: a clone campaign owns the wire. The latch is left UNSET,
        // so this stays owed and the campaign-end handler runs it once — which
        // covers all three triggers, because all three funnel through here.
        if (_campaign?.CampaignActive == true) return;
        _loadedThisSession = true;
        SendDeviceReads();
    }

    private void Refresh()
    {
        var contrast = _device.Contrast;
        ContrastText = contrast.IsConfirmed
            ? contrast.Value.ToString(CultureInfo.InvariantCulture) : "—";

        var battery = _device.BatteryStatus;
        BatteryText = battery.IsConfirmed ? battery.Value! : "—";

        var tod = _device.RadioTimeOfDay;
        RadioTodText = tod.IsConfirmed ? tod.Value! : "—";

        var backlight = _device.BacklightFunction;

        var intensity = _device.BacklightIntensity;
        BacklightIntensityText = intensity.IsConfirmed ? intensity.Value! : "—";

        AreControlsEnabled = Ready;
        DisabledReason = Ready ? "" : "Not connected — open Settings → Connection to connect.";

        // §9 C1: highlight ONLY from the confirmed mirror, which is a
        // PROVISIONAL verbatim payload ("LIGHT MOMENTARY" → "MOMENTARY"). If
        // the radio ever answers a third spelling, NOTHING lights and the
        // operator sees the honest un-highlighted row — the AVS precedent.
        BacklightFunctionChoices =
        [
            new ChoiceItem(BacklightOffLabel,
                backlight.IsConfirmed && backlight.Value == BacklightFunction.Off.ToWire(), SetBacklight),
            new ChoiceItem(BacklightMomentaryLabel,
                backlight.IsConfirmed && backlight.Value == BacklightFunction.Momentary.ToWire(), SetBacklight),
        ];

        // §9 C2: a press sends CONFIRMED ± 1, so a chevron with nothing
        // confirmed has no basis to step from and stays disabled.
        CanStepBacklightIntensity = Ready && ConfirmedIntensity() is not null;
        CanStepContrast = Ready && ConfirmedContrast() is not null;

        SetBacklightCommand.NotifyCanExecuteChanged();
        BacklightIntensityUpCommand.NotifyCanExecuteChanged();
        BacklightIntensityDownCommand.NotifyCanExecuteChanged();
        ContrastUpCommand.NotifyCanExecuteChanged();
        ContrastDownCommand.NotifyCanExecuteChanged();
        SetTimeFromDeviceCommand.NotifyCanExecuteChanged();
        RefreshDeviceSettingsCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseDevice() => Ready;

    // ---- Backlight (LIG — set here, read back into the highlighted row) ----

    /// <summary>The two backlight-function BUTTON LABELS. "MOM" is the round-11
    /// §3 abbreviation (owner ruling R6) that let the pair share the standard
    /// segment width; §9 C1 moved the labels into the VM with the choice row,
    /// so the WIRE tokens now come from <c>BacklightFunction.ToWire()</c> and
    /// the label is a display fact — the round-11 markup carried the wire token
    /// as a CommandParameter, which is what a rename could have broken.</summary>
    public const string BacklightOffLabel = "OFF";
    public const string BacklightMomentaryLabel = "MOM";

    /// <summary>LIG OFF | MOMENTARY (the two old-app-derived values). Round 4
    /// (AC) gave it a read (bare LIG) and a PROVISIONAL verbatim mirror, so the
    /// row now carries a confirmed display — which this SET never moves: only a
    /// LIGHT line from the radio does.
    /// <para>§9 C1: the parameter is the BUTTON LABEL (the ChoiceItem idiom —
    /// display in, wire out). The round-11 wire spellings are still accepted so
    /// nothing that already calls this by wire token breaks.</para></summary>
    [RelayCommand(CanExecute = nameof(CanUseDevice))]
    private void SetBacklight(string? value)
    {
        if (!Ready) return;
        var fn = (value?.Trim().ToUpperInvariant()) switch
        {
            "OFF" => (BacklightFunction?)BacklightFunction.Off,
            "MOM" or "MOMENTARY" => BacklightFunction.Momentary,
            _ => null,
        };
        if (fn is null) return;
        _device.SetBacklightFunction(fn.Value);
    }

    // ---- CLONE ROUND 12 §9 C2: intensity + contrast as CHEVRON PAIRS -------
    //
    // Both rows were a numeric Entry plus a Set button. The bench found the
    // shape wrong for a 0-8 setting the operator nudges while watching the
    // front panel: type, tap, look up, repeat. They are now ◀/▶ pairs,
    // SEND-ON-PRESS — a press sends CONFIRMED ± 1, clamped 0-8 with NO WRAP.
    //
    // Deliberately NOT a pending-pick control: the display stays the round-4
    // Option-B CONFIRMED readout and moves only when the radio answers, so
    // there is no new ui.md ledger entry and no second source of truth about
    // what the radio holds. Stepping from CONFIRMED (rather than from a local
    // counter) is what keeps that honest — a dropped repeat simply recomputes
    // from wherever the radio actually is.
    //
    // The Entry/InputError plumbing these two rows used is RETIRED: with no
    // free-text input there is nothing left to validate client-side, and the
    // clamp cannot produce an out-of-range value by construction.

    private bool CanStepIntensity() => CanStepBacklightIntensity;
    private bool CanStepContrastNow() => CanStepContrast;

    [RelayCommand(CanExecute = nameof(CanStepIntensity))]
    private void BacklightIntensityUp() => StepIntensity(+1);

    [RelayCommand(CanExecute = nameof(CanStepIntensity))]
    private void BacklightIntensityDown() => StepIntensity(-1);

    [RelayCommand(CanExecute = nameof(CanStepContrastNow))]
    private void ContrastUp() => StepContrast(+1);

    [RelayCommand(CanExecute = nameof(CanStepContrastNow))]
    private void ContrastDown() => StepContrast(-1);

    // ORDER IS LOAD-BEARING: the clamp/no-op check runs BEFORE the limiter, so
    // only a press that will ACTUALLY SEND consumes the 125 ms window. The
    // other order made a clamped no-op eat the interval — sitting at 00 and
    // pressing ◀ (which sends nothing) would then swallow an immediate ▶, the
    // first press the operator makes that has anything to say. A rate limiter
    // exists to bound what reaches the wire; a press that reaches no wire is
    // not what it is bounding.

    private void StepIntensity(int delta)
    {
        if (!Ready || ConfirmedIntensity() is not { } current) return;
        int target = Math.Clamp(current + delta, IntMin, IntMax);
        if (target == current) return;              // at the edge — no wrap, nothing to send
        if (!_intensityLimiter.TryFire()) return;
        _device.SetBacklightIntensity(target);
    }

    private void StepContrast(int delta)
    {
        if (!Ready || ConfirmedContrast() is not { } current) return;
        int target = Math.Clamp(current + delta, ContrastMin, ContrastMax);
        if (target == current) return;
        if (!_contrastLimiter.TryFire()) return;
        _device.SetContrast(target);
    }

    /// <summary>The confirmed intensity as a number, or null. The mirror is a
    /// PROVISIONAL VERBATIM payload ("04"), so it is parsed here rather than
    /// re-formatted into the display: the readout keeps showing exactly what
    /// the radio said, padding and all, and only the ARITHMETIC needs a
    /// number. A payload that will not parse leaves the chevrons disabled —
    /// there is no basis to step from something we cannot read.</summary>
    private int? ConfirmedIntensity()
    {
        var m = _device.BacklightIntensity;
        return m.IsConfirmed && m.Value is { } v
            && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n >= IntMin && n <= IntMax
            ? n : null;
    }

    /// <summary>The confirmed contrast — already an int mirror ("CONTRAST nn"
    /// is bench-confirmed), range-checked for the same reason.</summary>
    private int? ConfirmedContrast()
    {
        var m = _device.Contrast;
        return m.IsConfirmed && m.Value >= ContrastMin && m.Value <= ContrastMax ? m.Value : null;
    }

    // ---- Date/time (TIME + DAT + DAY from the device clock) ----------------

    /// <summary>The old "Radio" tab's "Set Date/Time from PC clock" — all
    /// three, zero-padded, because DAT does not recompute DAY. Reuses the HOP
    /// pane's set shape (device-wide; TI is answered in every mode).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDevice))]
    private void SetTimeFromDevice()
    {
        if (!Ready) return;
        _device.SetTimeOfDay(_time.GetLocalNow().DateTime);
    }

    // ---- Manual refresh (plan Q4: lazy once + manual refresh) --------------

    [RelayCommand(CanExecute = nameof(CanUseDevice))]
    private void RefreshDeviceSettings()
    {
        if (!Ready) return;
        // D1 QUIESCE (§4 SUPPRESSION SCOPE): the press is ACCEPTED and the
        // reads wait for the campaign to let go of the wire.
        if (_campaign?.CampaignActive == true) { _refreshPressOwed = true; return; }
        SendDeviceReads();
    }

    /// <summary>The mode-free device READ set, in page order. One list, used by
    /// both the lazy load and Refresh so the two can never drift. LIG/INT/CONT
    /// joined it in round 4 (AC); INT is PROVISIONAL (old-app-derived) and may
    /// draw an error from this radio — that is the bench item, not a bug.</summary>
    private void SendDeviceReads()
    {
        _device.RequestBacklightFunction();
        _device.RequestBacklightIntensity();
        _device.RequestContrast();
        _device.RequestBattery();
        _device.RequestTime();
    }
}
