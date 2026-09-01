using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The Radio settings → Settings sub-tab (device-wide, mode-free: LIG/INT/
/// CONT/date-time/BAT ST). Pins the exact wire per control through
/// DeviceSurface → the W1 Prc138Radio device setters, confirmed-state
/// rendering (every display renders "—" until reported — always accessible),
/// range validation, the constitution programmatic-write pin, the Ready-only
/// gate (no mode gate), and the lazy once-per-session load (EnsureLoaded,
/// driven by the page's OnAppearing, by reaching Ready, and — round 4, K2 —
/// by DeviceClockView's own Loaded in EITHER of its two placements) + manual
/// Refresh. Round 4 (AC) added the LIG/INT/CONT reads and the two provisional
/// backlight displays.
/// </summary>
public class DeviceSettingsViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();

    private DeviceSettingsViewModel Vm() => new(new DeviceSurface(Radio), Session, _time);

    private DeviceSettingsViewModel ReadyVm()
    {
        var vm = Vm();
        ConnectReady();
        Transport.ClearSent();
        return vm;
    }

    // ---- Lazy load (plan N4: two triggers — OnAppearing AND reaching Ready) --
    // TI is the unique load marker: the connect ritual sends SH + PORT_R (and
    // BAT ST sentinels) but never TI, so CountSent("TI") isolates the device
    // load from ritual/sentinel traffic.

    [Fact]
    public void ReachingReady_OnThePage_QueriesOnce_WithoutOnAppearing()
    {
        // MINOR D4: the operator sits on the Radio-settings page while
        // Disconnected and connects WITHOUT leaving it — no OnAppearing fires,
        // but reaching Ready must still drive the load.
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Equal(1, Transport.CountSent("TI"));    // loaded on reaching Ready

        // Once per session: a later OnAppearing (EnsureLoaded) does not re-query.
        Transport.ClearSent();
        vm.EnsureLoaded();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OnAppearingBeforeReady_ThenReady_LoadsExactlyOnce()
    {
        // The other trigger: the tab appears while NOT Ready (no query), then
        // the session reaches Ready — the PhaseChanged path loads once.
        var vm = Vm();
        vm.EnsureLoaded();                             // OnAppearing while Disconnected: no-op
        Assert.Empty(Transport.SentLines);

        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(1, Transport.CountSent("TI"));
    }

    [Fact]
    public void EnsureLoaded_NotReady_SendsNothing()
    {
        var vm = Vm();                                 // Disconnected
        vm.EnsureLoaded();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void NewSession_LoadsAgain()
    {
        var vm = Vm();
        ConnectReady();                                // auto-loads on Ready, then clears
        Session.Close();

        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, Session.Phase);
        Assert.Equal(1, Transport.CountSent("TI"));    // reloads for the new session
        _ = vm;
    }

    // ---- Confirmed rendering: always accessible, "—" until reported --------

    [Fact]
    public void Contrast_UnreportedDash_ThenConfirmedFromEcho()
    {
        var vm = ReadyVm();
        Assert.Equal("—", vm.ContrastText);

        Transport.InjectLine("CONTRAST 05");           // the CONT set echo shape
        Assert.Equal("5", vm.ContrastText);
        Assert.Empty(Transport.SentLines);             // programmatic write sends nothing
    }

    [Fact]
    public void Battery_UnreportedDash_ThenConfirmedVerbatim()
    {
        var vm = Vm();
        Assert.Equal("—", vm.BatteryText);             // before any battery line

        ConnectReady();                                // the ritual sentinel reports battery
        Assert.Equal("Status FULL 31.4V", vm.BatteryText);
    }

    [Fact]
    public void RadioClock_UnreportedDash_ThenConfirmedTime()
    {
        var vm = ReadyVm();
        Assert.Equal("—", vm.RadioTodText);

        Transport.InjectLine("DAY Monday   ");
        Transport.InjectLine("DATE 01/27/92");
        Transport.InjectLine("TIME 20:37:12");
        Assert.Equal("20:37:12", vm.RadioTodText);
        Assert.Empty(Transport.SentLines);
    }

    // ---- Set: exact wire per builder ---------------------------------------

    [Theory]
    [InlineData("OFF", "LIG OFF")]
    [InlineData("MOMENTARY", "LIG MOMENTARY")]
    public void SetBacklight_SendsLig(string value, string wire)
    {
        var vm = ReadyVm();
        vm.SetBacklightCommand.Execute(value);
        Assert.Equal([wire], Transport.SentLines);
    }

    // ---- CLONE ROUND 12 §9 C2: the chevron pairs ---------------------------
    //
    // These replace round-4's "type a number, press Set" pins. The WIRE is
    // unchanged and still ZERO-PADDED (unpadded `INT` is silently ineffective
    // — owner-verified at the bench — and the P-2b capture proved `CONT 05`
    // echoes `CONTRAST 05` and reads back `05`); what changed is where the
    // number comes from: CONFIRMED ± 1, never an app-side buffer.

    [Fact]
    public void AChevronPress_SendsConfirmedPlusOne_ZeroPadded_C2()
    {
        var vm = ReadyVm();
        Transport.InjectLine("INTENSITY 03");
        Transport.InjectLine("CONTRAST 05");
        Transport.ClearSent();

        vm.BacklightIntensityUpCommand.Execute(null);
        vm.ContrastDownCommand.Execute(null);

        Assert.Equal(["INT 04", "CONT 04"], Transport.SentLines);
    }

    /// <summary>The display does NOT move on the press — this is the Option-B
    /// confirmed readout, not a pending pick, so the next press still steps
    /// from what the RADIO said. Two presses without an answer therefore send
    /// the SAME value, which is the honest behaviour: nothing was confirmed in
    /// between.</summary>
    [Fact]
    public void ChevronsStepFromCONFIRMED_NotFromALocalCounter_C2()
    {
        var vm = ReadyVm();
        Transport.InjectLine("INTENSITY 03");
        Transport.ClearSent();

        vm.BacklightIntensityUpCommand.Execute(null);
        _time.Now += DeviceSettingsViewModel.RepeatInterval;
        vm.BacklightIntensityUpCommand.Execute(null);

        Assert.Equal(["INT 04", "INT 04"], Transport.SentLines);
        Assert.Equal("03", vm.BacklightIntensityText);        // display never optimistic

        // …and the moment the radio answers, the next step follows it.
        Transport.InjectLine("INTENSITY 04");
        Transport.ClearSent();
        _time.Now += DeviceSettingsViewModel.RepeatInterval;
        vm.BacklightIntensityUpCommand.Execute(null);
        Assert.Equal(["INT 05"], Transport.SentLines);
    }

    /// <summary>CLAMPED 0-8 with NO WRAP, at both ends of both rows. At the
    /// edge there is nothing to send — a wrap would jump the front panel from
    /// full brightness to off on one press.</summary>
    [Theory]
    [InlineData("INTENSITY 08", true, true)]     // intensity at max, pressing up
    [InlineData("INTENSITY 00", true, false)]    // intensity at min, pressing down
    [InlineData("CONTRAST 08", false, true)]
    [InlineData("CONTRAST 00", false, false)]
    public void AtTheEdge_TheChevronSendsNothing_AndNeverWraps_C2(
        string report, bool intensity, bool up)
    {
        var vm = ReadyVm();
        Transport.InjectLine(report);
        Transport.ClearSent();

        (intensity
            ? (up ? vm.BacklightIntensityUpCommand : vm.BacklightIntensityDownCommand)
            : (up ? vm.ContrastUpCommand : vm.ContrastDownCommand)).Execute(null);

        Assert.Empty(Transport.SentLines);

        // Anti-vacuity: the OTHER direction from the same edge DOES send, so
        // "nothing sent" cannot mean "the chevron is simply dead here".
        //
        // …and NO time is advanced between the two presses, deliberately
        // (audit round 1, MINOR 1). A clamped no-op must NOT consume the
        // 125 ms limiter window: pressing ◀ at 00 sends nothing, so it has
        // nothing to rate-limit, and the operator's very next press — the
        // first one with anything to say — must go out. The earlier version of
        // this test advanced the clock here and therefore passed against the
        // defect.
        (intensity
            ? (up ? vm.BacklightIntensityDownCommand : vm.BacklightIntensityUpCommand)
            : (up ? vm.ContrastDownCommand : vm.ContrastUpCommand)).Execute(null);
        Assert.Single(Transport.SentLines);
    }

    /// <summary>Disabled until the radio has reported a value — a press with
    /// nothing confirmed has no basis to step from (the RF-gain / BFO /
    /// modem-wheel idiom), and guessing a starting point would put an
    /// invented value on the wire.</summary>
    [Fact]
    public void ChevronsAreDisabled_UntilTheFirstConfirmedReport_C2()
    {
        var vm = ReadyVm();

        Assert.False(vm.CanStepBacklightIntensity);
        Assert.False(vm.CanStepContrast);
        vm.BacklightIntensityUpCommand.Execute(null);
        vm.ContrastUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("INTENSITY 03");
        Assert.True(vm.CanStepBacklightIntensity);
        Assert.False(vm.CanStepContrast);              // each row gates on ITS OWN mirror

        Transport.InjectLine("CONTRAST 05");
        Assert.True(vm.CanStepContrast);
    }

    /// <summary>A payload the app cannot read as 0-8 leaves the row disabled.
    /// The INTENSITY mirror is a PROVISIONAL verbatim string, so this is a
    /// live possibility, not a hypothetical — and the readout still shows the
    /// payload verbatim, which is exactly the split C2 preserves.</summary>
    [Fact]
    public void AnUnreadableIntensityPayload_DisablesTheChevrons_ButStillDisplays_C2()
    {
        var vm = ReadyVm();
        Transport.InjectLine("INTENSITY HIGH");

        Assert.Equal("HIGH", vm.BacklightIntensityText);
        Assert.False(vm.CanStepBacklightIntensity);
    }

    /// <summary>Held chevrons are rate-limited — the house RepeatRateLimiter
    /// discipline (drop, never queue), so a finger held on the chevron cannot
    /// pile commands up behind the prompt-gated transport.</summary>
    [Fact]
    public void HeldChevrons_AreRateLimited_DropNeverQueue_C2()
    {
        var vm = ReadyVm();
        Transport.InjectLine("INTENSITY 03");
        Transport.ClearSent();

        vm.BacklightIntensityUpCommand.Execute(null);
        vm.BacklightIntensityUpCommand.Execute(null);   // inside the interval — DROPPED
        vm.BacklightIntensityUpCommand.Execute(null);
        Assert.Equal(["INT 04"], Transport.SentLines);

        _time.Now += DeviceSettingsViewModel.RepeatInterval;
        vm.BacklightIntensityUpCommand.Execute(null);
        Assert.Equal(["INT 04", "INT 04"], Transport.SentLines);
    }

    /// <summary>The retired plumbing is really gone, not merely unbound: the
    /// Entry buffers, their Apply commands and the card's shared client-side
    /// error note. Pinned by reflection so a markup-only removal (which would
    /// leave the VM able to grow a second input path back) still fails.</summary>
    [Fact]
    public void TheEntryAndInputErrorPlumbing_IsRetired_C2()
    {
        var t = typeof(DeviceSettingsViewModel);
        foreach (var gone in new[]
        {
            "BacklightIntensityInput", "ContrastInput", "InputError", "HasInputError",
            "ApplyBacklightIntensityCommand", "ApplyContrastCommand",
        })
            Assert.Null(t.GetProperty(gone));

        // Anti-vacuity: the READOUTS the rows kept are still properties here,
        // so a reflection call that simply found nothing cannot pass.
        Assert.NotNull(t.GetProperty(nameof(DeviceSettingsViewModel.BacklightIntensityText)));
        Assert.NotNull(t.GetProperty(nameof(DeviceSettingsViewModel.ContrastText)));
    }

    [Fact]
    public void SetTimeFromDevice_SendsAllThree_ZeroPadded()
    {
        var vm = ReadyVm();
        // 2026-01-02 08:05:09 UTC is a Friday; single digits pin the padding.
        _time.Now = new DateTimeOffset(2026, 1, 2, 8, 5, 9, TimeSpan.Zero);

        vm.SetTimeFromDeviceCommand.Execute(null);
        Assert.Equal(["TIME 08:05:09", "DAT 01/02/26", "DAY FRIDAY"], Transport.SentLines);
    }

    // ---- Ready gate (no mode gate) -----------------------------------------

    [Fact]
    public void NotConnected_ControlsDisabled_NothingSent()
    {
        var vm = Vm();
        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);

        vm.SetBacklightCommand.Execute("OFF");
        vm.ContrastUpCommand.Execute(null);
        vm.BacklightIntensityUpCommand.Execute(null);
        vm.SetTimeFromDeviceCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Ready_InAnyMode_ControlsEnabled()
    {
        // Mode-free: enabled on Ready regardless of the confirmed mode.
        var vm = ReadyVm();
        Assert.True(vm.AreControlsEnabled);
        Transport.InjectLine("HOP>");
        Assert.True(vm.AreControlsEnabled);
    }

    // ---- Manual refresh ----------------------------------------------------

    [Fact]
    public void RefreshDeviceSettings_ReQueriesTheWholeDeviceReadSet()
    {
        var vm = ReadyVm();
        vm.RefreshDeviceSettingsCommand.Execute(null);
        Assert.Equal(["LIG", "INT", "CONT", "BAT ST", "TI"], Transport.SentLines);
    }

    // ---- UI-tweaks round 4, AC: the device READ set (R4-Q1 mining) ---------
    // The three display reads joined the mode-free query set. LIG/CONT are
    // bench facts here (sentinel probe); INT and both answer PAYLOADS are
    // OLD-APP-DERIVED and PROVISIONAL (docs/protocol.md round-4 subsection).

    [Fact]
    public void LazyLoad_SendsTheWholeDeviceReadSet_Once()
    {
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();

        // Load and Refresh share ONE list, so the two can never drift. The
        // connect ritual owns everything before it (SH / PORT_R / sentinels),
        // so the load is exactly the tail.
        Assert.Equal(["LIG", "INT", "CONT", "BAT ST", "TI"], Transport.SentLines.TakeLast(5));

        Transport.ClearSent();
        vm.EnsureLoaded();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void VmConstructedWhileAlreadyReady_LoadsNothingUntilSomethingCallsEnsureLoaded()
    {
        // WHY the DeviceClockView owns a Loaded trigger (K2), stated as a
        // behavioural fact rather than a comment. The VM is a DI SINGLETON, so
        // an operator who connects and then opens Mode settings → HOP without
        // ever visiting Radio settings constructs it AFTER Ready: PhaseChanged
        // has already fired, no page OnAppearing covers that pane, and nothing
        // else would ever ask. The clock would render "—" for the whole
        // session. DeviceClockViewTests pins the subscription that saves it.
        ConnectReady();
        Transport.ClearSent();

        var vm = new DeviceSettingsViewModel(new DeviceSurface(Radio), Session, _time);
        Assert.Empty(Transport.SentLines);          // construction alone queries nothing
        Assert.Equal("—", vm.RadioTodText);

        vm.EnsureLoaded();                          // what the view's Loaded does
        Assert.Equal(["LIG", "INT", "CONT", "BAT ST", "TI"], Transport.SentLines);
    }

    [Fact]
    public void SecondHostCallingEnsureLoaded_SendsNothingMore()
    {
        // K2: DeviceClockView owns its OWN load trigger (its Loaded event) and
        // appears on BOTH Radio settings and the HOP settings pane, alongside
        // the page's existing OnAppearing call. Three callers, one query set.
        var vm = ReadyVm();          // already loaded on reaching Ready
        vm.EnsureLoaded();           // the page's OnAppearing
        vm.EnsureLoaded();           // the clock card's Loaded, first host
        vm.EnsureLoaded();           // the clock card's Loaded, second host
        Assert.Empty(Transport.SentLines);
    }

    /// <summary>CLONE ROUND 12 §9 C1 re-pin. This asserted a verbatim chip
    /// readout ("—" → "MOMENTARY"). The chip is deleted; the same mirror now
    /// drives a HIGHLIGHT, which is the fact the bench was missing — the old
    /// row could not highlight at all.</summary>
    [Theory]
    [InlineData("LIGHT OFF", DeviceSettingsViewModel.BacklightOffLabel)]
    [InlineData("LIGHT MOMENTARY", DeviceSettingsViewModel.BacklightMomentaryLabel)]
    public void BacklightFunction_NothingHighlighted_ThenTheConfirmedChoice_C1(
        string line, string expected)
    {
        var vm = ReadyVm();
        Assert.All(vm.BacklightFunctionChoices, c => Assert.False(c.IsActive));

        Transport.InjectLine(line);

        Assert.Equal(
            [expected],
            vm.BacklightFunctionChoices.Where(c => c.IsActive).Select(c => c.Value));
        Assert.Empty(Transport.SentLines);              // programmatic write sends nothing
    }

    /// <summary>The provisional-mirror rule, kept: an answer outside the two
    /// known payload spellings highlights NOTHING rather than guessing. Also
    /// the pin for the RECORDED consequence of deleting the chip — with no
    /// verbatim readout left on this row, "nothing highlighted" is the whole
    /// of what the operator sees, exactly as for preamp / internal coupler /
    /// 1 kW PA.</summary>
    [Fact]
    public void AnUnknownBacklightPayload_HighlightsNothing_C1()
    {
        var vm = ReadyVm();
        Transport.InjectLine("LIGHT MOMENTARY");
        Assert.Contains(vm.BacklightFunctionChoices, c => c.IsActive);

        Transport.InjectLine("LIGHT CONTINUOUS");       // never captured
        Assert.All(vm.BacklightFunctionChoices, c => Assert.False(c.IsActive));
    }

    /// <summary>The row's CHOICES send the wire tokens — the labels are
    /// display words ("MOM"), and the round-11 owner ruling R6 abbreviation
    /// must not reach the radio.</summary>
    [Fact]
    public void TheBacklightChoices_SendTheWireTokens_NotTheirLabels_C1()
    {
        var vm = ReadyVm();

        vm.BacklightFunctionChoices
            .Single(c => c.Value == DeviceSettingsViewModel.BacklightOffLabel)
            .SelectCommand.Execute(null);
        vm.BacklightFunctionChoices
            .Single(c => c.Value == DeviceSettingsViewModel.BacklightMomentaryLabel)
            .SelectCommand.Execute(null);

        Assert.Equal(["LIG OFF", "LIG MOMENTARY"], Transport.SentLines);
    }

    [Fact]
    public void BacklightIntensity_UnreportedDash_ThenConfirmedVerbatim()
    {
        var vm = ReadyVm();
        Assert.Equal("—", vm.BacklightIntensityText);

        // Shown EXACTLY as reported — the old app's zero-padded shape is
        // provisional and the display must not pretty it up before the bench
        // says what the radio actually sends.
        Transport.InjectLine("INTENSITY 04");
        Assert.Equal("04", vm.BacklightIntensityText);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void SettingAValue_DoesNotMoveItsDisplay()
    {
        // The constitution, on the round-4 displays: a SET moves the wire,
        // never the readout. Only the radio's own answer does.
        //
        // §9 C1/C2 re-pin: the backlight FUNCTION half is now about the
        // HIGHLIGHT rather than a chip, and the two numeric rows send from
        // CONFIRMED — so this drives them from reported values and checks the
        // readouts stay on those values, not on what was just sent.
        var vm = ReadyVm();
        Transport.InjectLine("INTENSITY 03");
        Transport.InjectLine("CONTRAST 05");
        Transport.ClearSent();

        vm.SetBacklightCommand.Execute("MOMENTARY");
        vm.BacklightIntensityUpCommand.Execute(null);
        vm.ContrastDownCommand.Execute(null);

        Assert.Equal(["LIG MOMENTARY", "INT 04", "CONT 04"], Transport.SentLines);
        Assert.All(vm.BacklightFunctionChoices, c => Assert.False(c.IsActive));
        Assert.Equal("03", vm.BacklightIntensityText);
        Assert.Equal("5", vm.ContrastText);
    }
}
