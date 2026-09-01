using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The SSB mode-settings pane (GUI rejigger Wave 2 — plan round-8 SSB list):
/// the SSB-domain settings that are NOT on the Operate SSB pane. Grouped
/// Audio/RX (FM group, CW offset, AVS, RF gain, preamp), TX/antenna
/// (keyline, antenna, internal coupler, 1 kW PA, retransmit, PREPOST×3),
/// the RWAS group (RWAS, FORCE_W, RWAS_KEY, UNKEY_M) and BEEP.
///
/// Constitution (plan §2.4): state flows one-way from the CONFIRMED mirror;
/// highlights move only on the radio's report (no optimism); command bodies
/// re-check their guards (ICommand.Execute ignores CanExecute); a
/// programmatic mirror write sends nothing. These settings are SSB-scoped on
/// the wire, so the gate is Ready + confirmed SSB — and, since clone round 12
/// §9 C4, that is the ONLY gate on this pane.
///
/// <para><b>CLONE ROUND 12 §9 C4 — the modulation gates are DELETED.</b> Wave 2
/// greyed the FM group outside FM and CW offset outside CW, in TWO layers (an
/// enabled property read by the command bodies, and an <c>IsEnabled</c>
/// container in the markup). That was UI POLICY INVENTING A RADIO CONSTRAINT,
/// and the r12-p2 probe disproved it: at a confirmed <c>USB</c> with the
/// modulation held constant, <c>FMSQ_T NOISE</c>, <c>FMTONE</c>, <c>FMDE 8.0</c>
/// and <c>CWOFF 1000</c> ALL echoed as accepted (docs/protocol.md, "The FM trio
/// and CWOFF are settable at USB"). The read side had always been mode-free
/// (the 17-query capture answered FMDEV and CWOFFSET from one prompt), and the
/// DGT_S precedent had already retired one invented gate. Both layers came out
/// together: a gate surviving in either alone still greys the row.</para>
///
/// HONEST RENDERING: settings with a captured answer shape (W1 mirror) show
/// their confirmed value and highlight the active choice; settings with NO
/// mirror (RWAS_KEY) send but never highlight and display "—" — the display
/// stays "—" under DEMO too, which is correct, not a bug. FORCE_W is the
/// ASYMMETRIC case (§9 C3): Enable highlights off the round-12 P1 mirror,
/// Disable never does.
///
/// Round-3 Y1/V7: the pane gained a FULL Refresh (the old-app-derived
/// per-setting query set, <see cref="SsbSurface.RequestSettings"/>) plus the
/// ALE-style lazy first load — it no longer depends on the connect-`SH`
/// subset. Preamp, internal coupler and 1 kW PA moved from "never highlights"
/// to highlighting off PROVISIONAL mirrors (docs/protocol.md "Old-app-derived
/// SSB query set"); their spellings are bench-unconfirmed, so the compare
/// accepts the old app's report form and nothing else lights up until the
/// radio actually answers.
/// </summary>
public partial class SsbSettingsViewModel : ObservableObject
{
    private readonly SsbSurface _ssb;
    private readonly RadioSession _session;

    /// <summary>Round 14 C: the coupler convergence policy, told about this
    /// pane's coupler press so the operator's value becomes the policy's
    /// baseline (plan §4-C, owner ruling R10). Optional for the same reason as
    /// on the surfaces — the app's composition always supplies it, and a
    /// composition without one simply has no policy to tell.</summary>
    private readonly CouplerPolicy? _coupler;

    /// <summary>Lazy first load (Y1, the ALE idiom): the query set goes out
    /// ONCE per Ready+confirmed-SSB session; after that the mirror IS the
    /// cache and only the manual Refresh re-reads.</summary>
    private bool _loadedThisSession;

    // ---- Group gates + reasons ------------------------------------------

    [ObservableProperty] private bool areSettingsEnabled;
    [ObservableProperty] private string settingsDisabledReason = "";

    // CLONE ROUND 12 §9 C4 — the FM-group and CW-offset gates are GONE.
    // See the class summary for the capture and the reasoning; both the
    // properties and the XAML containers they drove were removed together,
    // because a gate that survives in either layer alone still greys the row.

    // ---- Audio / RX ------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<ChoiceItem> fmSquelchTypeChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> fmToneChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> fmDeviationChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> cwOffsetChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> avsChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> preampChoices = [];
    // E2/H2: when the radio reports an AVS value outside On/Off (bench:
    // "NOT INSTALLED"), the odd value renders on the confirmed-value display
    // element in the radio's wording, our case ("Not installed") — the
    // buttons stay un-highlighted.
    [ObservableProperty] private string avsOddText = "";
    [ObservableProperty] private bool hasAvsOddValue;
    [ObservableProperty] private string rfGainText = "—";

    // ---- TX / antenna ----------------------------------------------------

    [ObservableProperty] private IReadOnlyList<ChoiceItem> antennaChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> internalCouplerChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> oneKilowattChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> retransmitChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> prePostFilterChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> prePostRxAntennaChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> prePostScanChoices = [];

    // NOTE: the Keyline intent (K ON|OFF) is DEFERRED this wave — see
    // SsbSurface: the Core GuiOutScopeGuardTests guard forbids the keying
    // builder's name in any app-layer source until its keying UI lands, and
    // dropping it from that guard's list is a Core-test edit outside this
    // wave's ownership.

    // ---- RWAS group ------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<ChoiceItem> rwasChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> forceWakeupChoices = [];
    [ObservableProperty] private IReadOnlyList<ChoiceItem> unkeyMaskChoices = [];

    // ---- Device ----------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<ChoiceItem> beepChoices = [];

    // ---- Numeric entries (I1/I2): app-side input buffers the radio NEVER
    // writes (two-way legal — operator input, not a mirror; the ALE numeric
    // idiom). RF gain has a confirmed display (RfGainText); RWAS_KEY is
    // write-only, its display stays "—" (no radio state to show). ----------

    [ObservableProperty] private string rfGainInput = "";
    [ObservableProperty] private string rwasKeyInput = "";

    /// <summary>Shared client-side validation note for the numeric fields
    /// (cleared on a valid send). Radio-side rejections still surface via the
    /// error toast like every other command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 5). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>
    /// An explicit Refresh PRESS accepted while a campaign owned the wire (§4
    /// SUPPRESSION SCOPE, decided): the button never greys, the press is
    /// remembered, and the read runs once the wire is free.
    ///
    /// <para><b>IT IS PAID BY THE PANE'S OWN GATE, NOT BY THE CAMPAIGN'S END</b>
    /// (audit round 1). A campaign can end in HOP with this pane's press still
    /// owed, and this pane may not read outside SSB. Clearing the latch at the
    /// campaign edge threw such a press away for the rest of the session — the
    /// operator pressed Refresh, the campaign moved the radio elsewhere, and
    /// coming back to SSB never re-read. So the debt is settled inside
    /// <see cref="Refresh"/>, on whatever event next finds the pane readable,
    /// and stays owed until then. A session DROP does discard it: the next
    /// Ready session may be a different radio, and the press was for this
    /// one.</para></summary>
    private bool _refreshPressOwed;

    public SsbSettingsViewModel(
        SsbSurface ssb, RadioSession session, CouplerPolicy? coupler = null,
        ICampaignSignal? campaign = null)
    {
        _ssb = ssb;
        _session = session;
        _coupler = coupler;
        _campaign = campaign;
        ssb.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            // A dropped session forgets the load flag: the next Ready session
            // may be a DIFFERENT radio and must be read fresh (the ALE idiom).
            if (_session.Phase != SessionPhase.Ready)
            {
                _loadedThisSession = false;
                _refreshPressOwed = false;      // the press was for the radio that left
            }
            Refresh();
        };
        // The campaign's END edge simply runs the recompute; Refresh settles
        // whatever is owed, IF this pane can read now, and leaves it owed if it
        // cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool SettingsEditable => Ready && _ssb.IsSsbConfirmed;

    private void Refresh()
    {
        bool ssb = SettingsEditable;

        // Lazy first load (Y1): the first time a Ready session confirms SSB,
        // send the query set ONCE. Every query is visible in the Console; the
        // displays still move only when the answers land.
        //
        // D1 QUIESCE: a clone campaign owns the wire — the read DEFERS and the
        // latch is left unset, so this stays owed.
        //
        // THIS BLOCK IS ALSO WHERE THE DEBTS ARE SETTLED (audit round 1). Both
        // owed reads are the SAME query set, so at most one goes out; and
        // neither is cleared unless the pane's own gate lets it read NOW, which
        // is what keeps a press owed across a campaign that ended in HOP.
        if (ssb && _campaign?.CampaignActive != true)
        {
            if (!_loadedThisSession)
            {
                _loadedThisSession = true;
                _refreshPressOwed = false;      // the lazy load IS the press's read
                _ssb.RequestSettings();
            }
            else if (_refreshPressOwed)
            {
                _refreshPressOwed = false;
                _ssb.RequestSettings();
            }
        }

        AreSettingsEnabled = ssb;
        SettingsDisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed
                ? "SSB settings wait for the radio to confirm SSB."
                : "";

        // Audio / RX. Choice VALUES are the H1 display casing (title-case,
        // present tense); the wire form comes back in each select parser and
        // the IsActive compare maps display → wire internally.
        FmSquelchTypeChoices = StringChoices(_ssb.FmSquelchType, SetFmSquelchType,
            ("Noise", "NOISE"), ("Tone", "TONE"));
        FmToneChoices = OnOffChoices(_ssb.FmTone, SetFmTone);
        FmDeviationChoices = StringChoices(_ssb.FmDeviation, SetFmDeviation,
            ("5.0", "5.0"), ("6.5", "6.5"), ("8.0", "8.0"));
        CwOffsetChoices = CwOffsetChoiceList();
        AvsChoices = AvsChoiceList();
        var avs = _ssb.Avs;
        HasAvsOddValue = avs.IsConfirmed && avs.Value is not ("ON" or "OFF");
        AvsOddText = HasAvsOddValue ? SentenceCase(avs.Value!) : "";
        PreampChoices = ProvisionalBypassChoices(_ssb.RxPreamp, SetPreamp);

        var rf = _ssb.RfGain;
        RfGainText = rf.IsConfirmed ? rf.Value.ToString() : "—";

        // TX / antenna.
        // ROUND 15 H-2 / H-D1: Auto first. Antenna has no "on", so this is an
        // ARCHITECT DEFAULT, not a device fact — the manual's own vocabulary
        // (`BNc/AUto/TUned`, protocol.md) ranks nothing, and the owner's ask
        // was "the on setting on the left". Auto is the state that lets the
        // radio choose, which is the closest thing here to an affirmative. If
        // the owner prefers `BNC · Auto · Tuned`, this line and the Antenna
        // clause in ChoiceOrderGuardTests move back together. WIRE-NEUTRAL:
        // the display→wire mapping travels with each entry.
        AntennaChoices = StringChoices(_ssb.Antenna, SetAntenna,
            ("Auto", "AUTO"), ("BNC", "BNC"), ("Tuned", "TUNED"));
        InternalCouplerChoices = ProvisionalBypassChoices(_ssb.InternalCoupler, SetInternalCoupler);
        OneKilowattChoices = StringChoices(_ssb.OneKilowattPa, SetOneKilowatt,
            ("Yes", "YES"), ("No", "NO"));
        RetransmitChoices = StringChoices(_ssb.Retransmit, SetRetransmit,
            ("Enable", "ENABLED"), ("Disable", "DISABLED"));
        PrePostFilterChoices = StringChoices(_ssb.PrePostFilter, SetPrePostFilter,
            ("Enable", "ENABLE"), ("Disable", "DISABLE"));
        PrePostRxAntennaChoices = StringChoices(_ssb.PrePostRxAntenna, SetPrePostRxAntenna,
            ("Enable", "ENABLE"), ("Disable", "DISABLE"));
        PrePostScanChoices = StringChoices(_ssb.PrePostScanRate, SetPrePostScan,
            ("Slow", "SLOW"), ("Fast", "FAST"));

        // RWAS group.
        RwasChoices = EnabledDisabledChoices(_ssb.Rwas, SetRwas);
        ForceWakeupChoices = ForceWakeupChoiceList();
        UnkeyMaskChoices = EnabledDisabledChoices(_ssb.UnkeyMask, SetUnkeyMask);

        // Device.
        BeepChoices = OnOffChoices(_ssb.Beep, SetBeep);

        if (!Ready) InputError = "";                    // stale note dies with the session

        ApplyRfGainCommand.NotifyCanExecuteChanged();
        ApplyRwasKeyCommand.NotifyCanExecuteChanged();
        RefreshSettingsCommand.NotifyCanExecuteChanged();
    }

    // ---- Choice-list builders (highlight ONLY from confirmed state).
    // ChoiceItem.Value carries the H1 DISPLAY casing; the wire token is
    // mapped inside each builder's IsActive compare and each select parser.

    private IReadOnlyList<ChoiceItem> OnOffChoices(Confirmed<OnOff> mirror, Action<string> select) =>
    [
        new ChoiceItem("On", mirror.IsConfirmed && mirror.Value == OnOff.On, select),
        new ChoiceItem("Off", mirror.IsConfirmed && mirror.Value == OnOff.Off, select),
    ];

    private static IReadOnlyList<ChoiceItem> StringChoices(
        Confirmed<string> mirror, Action<string> select, params (string Display, string Wire)[] items)
        => [.. items.Select(i => new ChoiceItem(i.Display, mirror.IsConfirmed && mirror.Value == i.Wire, select))];

    private static IReadOnlyList<ChoiceItem> EnabledDisabledChoices(
        Confirmed<EnabledDisabled> mirror, Action<string> select) =>
    [
        new ChoiceItem("Enable", mirror.IsConfirmed && mirror.Value == EnabledDisabled.Enabled, select),
        new ChoiceItem("Disable", mirror.IsConfirmed && mirror.Value == EnabledDisabled.Disabled, select),
    ];

    // The round-2 UnmirroredChoices helper ("no captured answer shape — the
    // buttons send but never highlight") is DELETED with clone round 12 §9 C3:
    // FORCE_W was its last caller, and FORCE_W is no longer unmirrored. The
    // honesty rule it encoded survives in the row below, which is where it now
    // has to be stated — because the two buttons stopped being symmetric.

    /// <summary>
    /// CLONE ROUND 12 §9 C3 — FORCE_W, the ASYMMETRIC choice row.
    ///
    /// <para>The radio announces <c>FORCE WAKEUP ENABLED</c> and says NOTHING
    /// at all when it is disabled, and a bare query answers nothing either. P1
    /// turned the parser's deliberate discard into a bounded session latch:
    /// the mirror confirms Enabled on that line, the DIS send path marks it
    /// UNCONFIRMED, and reconnect clears it like every other mirror.</para>
    ///
    /// <para>So the two buttons are NOT symmetric, and writing them as a pair
    /// would be the whole defect back again. ENABLE highlights on a confirmed
    /// mirror, exactly like every other row on this pane. DISABLE NEVER
    /// highlights: unconfirmed means "not known to be enabled", which is not
    /// the same fact as "confirmed disabled", and lighting Disable on the
    /// absence of a report would be the app claiming a state the radio has
    /// never reported. That asymmetry is RECORDED here rather than hidden in a
    /// helper, because a later "clean-up" to EnabledDisabledChoices would look
    /// like consistency and would be a lie.</para>
    /// </summary>
    private IReadOnlyList<ChoiceItem> ForceWakeupChoiceList()
    {
        var fw = _ssb.ForceWakeup;
        return
        [
            new ChoiceItem("Enable", fw.IsConfirmed && fw.Value == EnabledDisabled.Enabled, SetForceWakeup),
            new ChoiceItem("Disable", false, SetForceWakeup),
        ];
    }

    /// <summary>Enable/Bypass pair off a PROVISIONAL verbatim mirror (PREAMP,
    /// INTCOUPLER — round-3 V7). The SET spelling ("BYPASS"/"ENABLE", HELP
    /// "BYpass/ENable") and the old app's REPORT spelling
    /// ("BYPASSED"/"ENABLED") DIFFER, and neither is bench-confirmed — so the
    /// compare matches the report form only. If the radio answers something
    /// else, NOTHING highlights, which is the honest outcome (and the bench
    /// item A5b is what fixes it).
    /// <para><b>THIS HELPER IS DUPLICATED.</b> Round 14 B (owner ruling R2,
    /// plan/plan-round14.md §4-B) put the coupler row on the HOP settings pane
    /// too, and copied these four lines into
    /// <c>HopSettingsViewModel.ProvisionalBypassChoices</c> rather than widen
    /// this VM's surface into a shared home. The two copies must agree: if the
    /// report spellings ever change, CHANGE BOTH. (The INTCOUPLER half is no
    /// longer provisional — P-1, 2026-08-20, captured the query answer AND both
    /// set echoes, docs/protocol.md; PREAMP still is, so the name stands.)</para>
    /// <para>ROUND 15 H-2: <b>Enable before Bypass</b> — the affirmative on the
    /// LEFT (docs/ui.md's constitution rule, pinned by
    /// <c>ChoiceOrderGuardTests</c>). This builder serves BOTH the PREAMP and
    /// the INTERNAL COUPLER rows, so both flip together, and so does the HOP
    /// pane's copy. WIRE-NEUTRAL: the setter parses the BUTTON LABEL.</para>
    /// </summary>
    private static IReadOnlyList<ChoiceItem> ProvisionalBypassChoices(
        Confirmed<string> mirror, Action<string> select) =>
    [
        new ChoiceItem("Enable", mirror.IsConfirmed && mirror.Value == "ENABLED", select),
        new ChoiceItem("Bypass", mirror.IsConfirmed && mirror.Value == "BYPASSED", select),
    ];

    private IReadOnlyList<ChoiceItem> CwOffsetChoiceList()
    {
        var cw = _ssb.CwOffset;                        // verbatim "0000"/"1000"
        return
        [
            new ChoiceItem("0", cw.IsConfirmed && cw.Value == "0000", SetCwOffset),
            new ChoiceItem("1000", cw.IsConfirmed && cw.Value == "1000", SetCwOffset),
        ];
    }

    private IReadOnlyList<ChoiceItem> AvsChoiceList()
    {
        var avs = _ssb.Avs;                            // "ON"/"OFF"/"NOT INSTALLED"
        return
        [
            new ChoiceItem("On", avs.IsConfirmed && avs.Value == "ON", SetAvs),
            new ChoiceItem("Off", avs.IsConfirmed && avs.Value == "OFF", SetAvs),
        ];
    }

    /// <summary>H2: an odd radio value renders in the radio's WORDING, our
    /// TENSE/case — "NOT INSTALLED" → "Not installed". Never invented, never
    /// echoed ALL-CAPS.</summary>
    private static string SentenceCase(string wire)
        => wire.Length == 0 ? wire : char.ToUpperInvariant(wire[0]) + wire[1..].ToLowerInvariant();

    // ---- Guarded select methods (each re-checks its gate) ----------------

    // CLONE ROUND 12 §9 C4: these four read the PANE gate (SettingsEditable)
    // like every other setter here. They used to read an extra
    // modulation gate that the r12-p2 capture disproved.
    private void SetFmSquelchType(string label)
    {
        if (!SettingsEditable) return;
        if (!Enum.TryParse<FmSquelchType>(label, ignoreCase: true, out var type)) return;
        var c = _ssb.FmSquelchType;                     // mirror holds the wire casing
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetFmSquelchType(type);
    }

    private void SetFmTone(string label)
    {
        if (!SettingsEditable || ToOnOff(label) is not { } state) return;
        var c = _ssb.FmTone;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetFmTone(state);
    }

    private void SetFmDeviation(string label)
    {
        if (!SettingsEditable || !Wire.FmDeviationValues.Contains(label)) return;
        var c = _ssb.FmDeviation;
        if (c.IsConfirmed && c.Value == label) return;
        _ssb.SetFmDeviation(label);
    }

    private void SetCwOffset(string label)
    {
        if (!SettingsEditable || !int.TryParse(label, out int hz)) return;
        var c = _ssb.CwOffset;
        if (c.IsConfirmed && c.Value == hz.ToString("D4")) return;   // re-click guard
        _ssb.SetCwOffset(hz);
    }

    private void SetAvs(string label)
    {
        if (!SettingsEditable || ToOnOff(label) is not { } state) return;
        var c = _ssb.Avs;
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetAvs(state);
    }

    // Preamp / internal coupler / 1 kW PA: mirrored since round-3 V7, but the
    // mirror is PROVISIONAL (old-app-derived spellings, bench-unconfirmed).
    // That is good enough to HIGHLIGHT — the display only ever shows what the
    // radio said — but deliberately NOT good enough to SUPPRESS a send: a
    // re-click guard built on an unconfirmed spelling would silently swallow
    // an operator's command on an assumption. Guards arrive with the bench
    // confirmation (bench-checklist A5b).
    private void SetPreamp(string label)
    {
        if (!SettingsEditable || !Enum.TryParse<BypassEnable>(label, ignoreCase: true, out var state)) return;
        _ssb.SetRxPreamp(state);
    }

    private void SetAntenna(string label)
    {
        if (!SettingsEditable || !Enum.TryParse<AntennaPort>(label, ignoreCase: true, out var port)) return;
        var c = _ssb.Antenna;
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetAntenna(port);
    }

    private void SetInternalCoupler(string label)
    {
        if (!SettingsEditable || !Enum.TryParse<BypassEnable>(label, ignoreCase: true, out var state)) return;
        // ROUND 14 C (plan §4-C, owner ruling R10): an EXPLICIT operator set
        // moves the policy's baseline, so the policy converges toward what the
        // operator chose instead of undoing them. Reported, not mirror-inferred
        // — the mirror also moves for the policy's own writes.
        _coupler?.NotifyOperatorCouplerWrite(state);
        _ssb.SetInternalCoupler(state);   // provisional mirror — see SetPreamp
    }

    private void SetOneKilowatt(string label)
    {
        if (!SettingsEditable || Wire.ParseYesNo(label.ToUpperInvariant()) is not { } installed) return;
        _ssb.SetOneKilowattPa(installed);   // provisional mirror — see SetPreamp
    }

    private void SetRetransmit(string label)
    {
        if (!SettingsEditable || ToEnabledDisabled(label) is not { } state) return;
        var c = _ssb.Retransmit;            // string mirror: "ENABLED"/"DISABLED"
        string wire = state == EnabledDisabled.Enabled ? "ENABLED" : "DISABLED";
        if (c.IsConfirmed && c.Value == wire) return;
        _ssb.SetRetransmit(state);
    }

    private void SetPrePostFilter(string label)
    {
        if (!SettingsEditable || ToEnableOnOff(label) is not { } state) return;
        var c = _ssb.PrePostFilter;         // string mirror: "ENABLE"/"DISABLE"
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetPrePostFilter(state);
    }

    private void SetPrePostRxAntenna(string label)
    {
        if (!SettingsEditable || ToEnableOnOff(label) is not { } state) return;
        var c = _ssb.PrePostRxAntenna;
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetPrePostRxAntenna(state);
    }

    private void SetPrePostScan(string label)
    {
        if (!SettingsEditable || !Enum.TryParse<PrePostScanRate>(label, ignoreCase: true, out var rate)) return;
        var c = _ssb.PrePostScanRate;
        if (c.IsConfirmed && string.Equals(c.Value, label, StringComparison.OrdinalIgnoreCase)) return;
        _ssb.SetPrePostScanRate(rate);
    }

    private void SetRwas(string label)
    {
        if (!SettingsEditable || ToEnabledDisabled(label) is not { } state) return;
        var c = _ssb.Rwas;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetRwas(state);
    }

    private void SetForceWakeup(string label)
    {
        if (!SettingsEditable || ToEnabledDisabled(label) is not { } state) return;
        // No re-click guard, deliberately (§9 C3). ENA is idempotent and
        // re-answers (P-2e), and DIS is silent by contract — guarding either
        // direction on this latch would swallow the one command that can
        // restore agreement between the app and the radio.
        _ssb.SetForceWakeup(state);
    }

    private void SetUnkeyMask(string label)
    {
        if (!SettingsEditable || ToEnabledDisabled(label) is not { } state) return;
        var c = _ssb.UnkeyMask;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetUnkeyMask(state);
    }

    private void SetBeep(string label)
    {
        if (!SettingsEditable || ToOnOff(label) is not { } state) return;
        var c = _ssb.Beep;
        if (c.IsConfirmed && c.Value == state) return;
        _ssb.SetBeep(state);
    }

    // Display-label parsers (H1 title-case button texts).

    private static OnOff? ToOnOff(string label) =>
        label == "On" ? OnOff.On : label == "Off" ? OnOff.Off : null;

    private static OnOff? ToEnableOnOff(string label) =>
        label == "Enable" ? OnOff.On : label == "Disable" ? OnOff.Off : null;

    private static EnabledDisabled? ToEnabledDisabled(string label) =>
        label == "Enable" ? EnabledDisabled.Enabled
        : label == "Disable" ? EnabledDisabled.Disabled : null;

    // ---- Numeric entries (I1/I2): validate the app-side buffer, send the
    // documented wire form. The Entry is never written by the radio; the
    // confirmed display (RF gain) / "—" (write-only RWAS key) carries the
    // radio's side of the story. -------------------------------------------

    private bool CanSend() => SettingsEditable;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void ApplyRfGain()
    {
        if (!SettingsEditable) return;
        if (!int.TryParse((RfGainInput ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            || v is < 0 or > 100)
        {
            InputError = "RF gain must be a whole number 0-100.";
            return;
        }
        InputError = "";
        _ssb.SetRfGain(v);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void ApplyRwasKey()
    {
        if (!SettingsEditable) return;
        if (!int.TryParse((RwasKeyInput ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            || v is < 0 or > 99)
        {
            InputError = "RWAS key must be a whole number 0-99.";
            return;
        }
        InputError = "";
        _ssb.SetRwasKey(v);
    }

    // ---- Manual refresh (Y1: lazy once + manual Refresh, the ALE idiom) ----

    /// <summary>Re-read every SSB setting from the radio (the round-3 V7
    /// query set — 17 explicit, Console-visible reads; nothing is written).
    /// Distinct from the private display recompute above.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void RefreshSettings()
    {
        if (!SettingsEditable) return;      // re-check: Execute ignores CanExecute
        // D1 QUIESCE (§4 SUPPRESSION SCOPE): the press is ACCEPTED — the button
        // does not grey and the operator is not refused — but the read waits
        // for the campaign to let go of the wire.
        if (_campaign?.CampaignActive == true) { _refreshPressOwed = true; return; }
        _ssb.RequestSettings();
    }
}
