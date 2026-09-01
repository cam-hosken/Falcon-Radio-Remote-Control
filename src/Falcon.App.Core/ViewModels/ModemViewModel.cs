using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The modem picker wheel (round 8 ED — the PowerViewModel shape for a
/// CROSS-MODE global setting). One instance backs the modem row on ALL THREE
/// Operate panes (SSB Operate card, ALE link-banner card, HOP Status card):
/// the cluster moved here from the SSB-gated SignalViewModel because MODEM is
/// not SSB state — engagement is bench-confirmed from SSB and ALE prompts
/// (probe R8: same echo, same silent AGC/BAND drift, plus ALE's async
/// SCANNING resume), and every mode's SH block reports the MODEM line.
///
/// CLONE-FIELD ROUND 2 F10 (owner ruling R-C, decision A-8): the HOP set path
/// is CAPTURED and the row is back on the HOP pane — and the wheel is
/// MODE-SCOPED. Its positions are OFF plus the CONFIRMED mode's preset band:
/// 0-6 at SSB/ALE, <b>7-9 at HOP</b> (<see cref="Falcon.Core.Protocol.ModemPresetScope"/>; P5/P5d2,
/// transcripts p5-hop-modem-presets-20260821-180547.jsonl and
/// p5d2-hop-modem-select-enabled-20260821-183248.jsonl — `MODEM 9` at `HOP&gt;`
/// answers `MODEM 9 DAT9`, `MODEM 1` answers `INVALID MODEM PRESET`). Still ONE
/// wheel and ONE instance behind all three panes; only which positions it
/// counts moved. The two modem states are SEPARATE on the radio (P5d), which
/// the no-optimism display already handles: the wheel reads whatever the
/// current mode's SH block last reported.
///
/// D1 (round 2): a picker wheel — the display renders the confirmed
/// ActiveModem echo, "—" until reported; a spin computes the target from the
/// CONFIRMED position and the display moves only on the radio's echo (no
/// optimism). Gate: Ready + a CONFIRMED mode — the per-pane rows are already
/// mode-gated by their pane's visibility, so no per-mode logic lives here.
///
/// CLONE ROUND 12 §9 A6 (owner ruling, §14 O1): the SSB/ALE wheel cycles EIGHT
/// positions — OFF + presets 0-6 — where round 8 cycled seven (OFF + 1-6).
/// Slot 0 was programmable from the Radio settings card and unreachable from
/// the operate wheel; nothing about the wire changed, only which positions the
/// wheel counts. Under HOP the same arithmetic gives FOUR (OFF + 7-9).
///
/// Note the surviving "no per-mode logic lives here" sentence above no longer
/// holds as written: the RANGE is mode-scoped (F10), and since audit round 2
/// the GATE is too — a spin and the arrival landing both require a CONFIRMED
/// mode, because the band a preset number means is a fact about the prompt.
/// What the sentence still gets right is that no PER-PANE logic lives here:
/// one wheel backs all three, and the mode it reads is the radio's.
/// </summary>
public partial class ModemViewModel : ObservableObject
{
    private readonly ModemSurface _modem;
    private readonly RadioSession _session;

    // Round 5 (BB/K7) makes the display a pure FORMATTING transform of the
    // echo — see ModemDisplay.
    [ObservableProperty] private string modemDisplayText = "—";
    [ObservableProperty] private bool canSpinModem;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 2). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    public ModemViewModel(ModemSurface modem, RadioSession session, ICampaignSignal? campaign = null)
    {
        _modem = modem;
        _session = session;
        _campaign = campaign;
        // THE ONE OWED READ (D1): the campaign's END edge re-runs Refresh, and
        // the presence landing is still owed because the deferral below left
        // `_presenceLandingOwed` SET.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        modem.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            // ROUND 13 B1 (item 6): the READY ARRIVAL is a landing — the same
            // gesture the settings card treats as one (ModemPresetsViewModel's
            // ReadForLanding). The skip needs the enabled set BEFORE the first
            // chevron press, and a read the press itself starts cannot answer
            // in time to help that press. Deliberately NOT wired to
            // `modem.Changed`: an abandoned presence read restores Unknown and
            // raises, so a Changed-driven ensure would poll a silent radio
            // forever (see ModemSurface.EnsurePresenceLoaded's contract).
            // AUDIT ROUND 2, MAJOR 1: the arrival OWES the landing rather than
            // taking it. On this radio Ready arrives BEFORE the prompt line
            // that names the mode, so calling straight through here issued the
            // session's FIRST presence read with no scope at all — and then,
            // when the prompt turned out to be HOP, the scope-change landing
            // found that unscoped read still in flight and stood down. The
            // debt is paid in Refresh the moment a mode is confirmed, with
            // THAT mode's scope.
            //
            // A dead session re-arms it: the next Ready may be a different
            // radio, and its mirrors start Unknown.
            _presenceScopeAsked = null;
            _presenceLandingOwed = true;
            Refresh();
        };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>Ready + a confirmed mode. Any mode: SSB and ALE engagement
    /// are bench-confirmed, HOP rides the owner's round-8 ruling with its
    /// first live use as a bench item.</summary>
    private bool Editable => Ready && _modem.IsModeConfirmed;

    /// <summary>The lowest and highest preset the wheel reaches. CLONE ROUND
    /// 12 §9 A6 (owner ruling, §14 O1): the wheel used to cycle OFF + 1-6
    /// while the programming card programs 0-6 — slot 0 was programmable and
    /// unselectable. It is now reachable.
    ///
    /// <para><b>CLONE-FIELD ROUND 2 F10 (owner ruling R-C, decision A-8) — the
    /// range is MODE-SCOPED, not a constant.</b> The preset numbers that exist
    /// depend on the prompt: 0-6 at <c>SSB&gt;</c>/<c>ALE&gt;</c> and 7-9 at
    /// <c>HOP&gt;</c> (P5, transcript
    /// <c>bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl</c>).
    /// ONE wheel still backs all three panes; what changes is which positions
    /// it counts, from <see cref="Falcon.Core.Protocol.ModemPresetScope"/> via the surface.</para></summary>
    private (int First, int Last) Scope => _modem.PresetRange;

    /// <summary>Positions on the wheel: OFF plus one per preset in the CURRENT
    /// scope — EIGHT under SSB/ALE, FOUR under HOP.</summary>
    private int WheelPositions
    {
        get { var (first, last) = Scope; return 1 + last - first + 1; }
    }

    /// <summary>The preset band the wheel last ASKED THE RADIO ABOUT. Null
    /// until a mode is confirmed.</summary>
    private (int First, int Last)? _presenceScopeAsked;

    /// <summary>A Ready arrival that found no confirmed mode OWES the landing
    /// and pays it when one arrives (audit round 2, MAJOR 1).</summary>
    private bool _presenceLandingOwed;

    private void Refresh()
    {
        // AUDIT ROUND 1, MAJOR 1 — A CONFIRMED SCOPE CHANGE IS A LANDING.
        // Entering HOP makes the SSB enabled set say nothing about the band the
        // wheel now counts, so the FIRST press after the entry had neither data
        // nor a read on the way: it took the adjacent step and could land on a
        // DISABLED preset the radio then refused. The presence read for the new
        // scope goes out on the ARRIVAL instead, exactly as the Ready arrival
        // does, so the first press already has HOP-scoped data — or honestly
        // has none, and says so by stepping adjacent.
        //
        // This is SAFE against the retry loop EnsurePresenceLoaded's contract
        // warns about: the trigger is a CHANGE OF SCOPE, and an abandoned read
        // does not change the scope, so it cannot re-fire on its own failure.
        if (Ready && _modem.ConfirmedMode is not null
            && (_presenceScopeAsked != Scope || _presenceLandingOwed))
        {
            // D1 QUIESCE: a clone campaign owns the wire. DEFER by OWING the
            // landing — `_presenceScopeAsked` is deliberately left where it was
            // and the owed flag SET, so the campaign-end handler re-enters here
            // and issues the read once, with whatever scope is confirmed then.
            if (_campaign?.CampaignActive == true)
            {
                _presenceLandingOwed = true;
            }
            else
            {
                _presenceScopeAsked = Scope;
                _presenceLandingOwed = false;
                _modem.EnsurePresenceLoaded();
            }
        }

        // OFF + the confirmed mode's band. ActiveModem echoes "OFF" or "1 T39"
        // — the leading token is the active preset number. Spinning needs a
        // confirmed echo to compute the next position (the RF-gain/BFO
        // "step from confirmed" idiom).
        var modem = _modem.ActiveModem;
        ModemDisplayText = ModemDisplay(modem);
        CanSpinModem = Editable && ModemPosition(modem) >= 0;

        ModemUpCommand.NotifyCanExecuteChanged();
        ModemDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <b>Contract K7 (UI tweaks round 5, BB)</b> — the modem display is a
    /// pure FORMATTING transform of the CONFIRMED <c>ActiveModem</c> echo.
    /// Nothing is mined, derived or invented (owner ruling: "keep the modem
    /// display verbatim"); the only change is punctuation:
    ///
    /// <list type="bullet">
    ///   <item>unconfirmed → <c>"—"</c> (the constitution's second state);</item>
    ///   <item><c>"OFF"</c> → <c>"OFF"</c>;</item>
    ///   <item>a <c>"&lt;n&gt; &lt;name&gt;"</c> echo (the captured selection
    ///     shape, protocol.md "Modem" — <c>MODEM 1</c> answers
    ///     <c>MODEM 1 T39</c>) → <c>"n: name"</c>;</item>
    ///   <item>any OTHER confirmed echo → VERBATIM. The honesty fallback: an
    ///     echo whose shape was never captured is shown as the radio wrote
    ///     it rather than forced into a format it may not mean.</item>
    /// </list>
    ///
    /// <para>The name is whatever the radio stored (operator-programmed, 1–4
    /// alphanumeric characters in every capture) — it is NOT validated here,
    /// because a name outside that assumption is exactly the sort of fact the
    /// verbatim path must keep showing.</para>
    /// </summary>
    internal static string ModemDisplay(Confirmed<string> modem)
    {
        if (!modem.IsConfirmed || modem.Value is not { } echo) return "—";

        // Matched against the TRIMMED echo (the captured selection answer is
        // "MODEM 1 T39 " — trailing space, protocol.md), but the fallback
        // returns the echo the mirror actually holds, unaltered.
        var m = NumberThenName.Match(echo.Trim());
        return m.Success ? $"{m.Groups[1].Value}: {m.Groups[2].Value}" : echo;
    }

    /// <summary>K7's shape: a preset NUMBER, whitespace, then a single
    /// no-whitespace NAME token, and nothing else. A longer PRESET-form line
    /// deliberately does NOT match — it takes the verbatim path.</summary>
    private static readonly Regex NumberThenName = new(@"^(\d+)\s+(\S+)$", RegexOptions.Compiled);

    /// <summary>The active modem preset number from the "1 T39" echo, or null
    /// (OFF / unconfirmed / a name-only echo).</summary>
    private static string? ActiveModemPreset(Confirmed<string> modem)
    {
        if (!modem.IsConfirmed || modem.Value is not { } v || v == "OFF") return null;
        var head = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return head.Length > 0 && head[0].All(char.IsAsciiDigit) ? head[0] : null;
    }

    /// <summary>The picker-wheel position of the confirmed echo: 0 = OFF,
    /// then ONE position per preset — position <c>n+1</c> is preset <c>n</c>,
    /// so 1 = preset 0 … 7 = preset 6. −1 = unconfirmed or an unrecognized
    /// echo (no basis to spin from).
    ///
    /// <para>CLONE ROUND 12 §9 A6: round 8 mapped preset <c>n</c> to position
    /// <c>n</c> and used OFF's position 0 as the wheel's origin, which left no
    /// position for preset 0 — the slot the programming card has always
    /// programmed. The offset by one is what makes the eighth position
    /// exist.</para></summary>
    private int ModemPosition(Confirmed<string> modem)
    {
        if (!modem.IsConfirmed) return -1;
        if (modem.Value == "OFF") return 0;
        var (first, last) = Scope;
        // F10: an echo naming a preset OUTSIDE the confirmed prompt's band is
        // no basis to spin from — it is a report about the other scope's state
        // (SSB's modem state is separate from HOP's, P5d), and inventing a
        // position for it would put an out-of-scope number on the wire.
        return ActiveModemPreset(modem) is { } p
            && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n >= first && n <= last ? n - first + 1 : -1;
    }

    /// <summary>The inverse of <see cref="ModemPosition"/>: what a wheel
    /// position selects. Position 0 is OFF (<c>MODEM OF</c>, unchanged);
    /// every other position is its preset number IN THE CURRENT SCOPE.</summary>
    private string PositionTarget(int position)
        => position == 0
            ? "OFF"
            : (position - 1 + Scope.First).ToString(CultureInfo.InvariantCulture);

    // D1: the wheel cycles OFF → 0 → 1 → … → 6 → (wrap) — EIGHT positions
    // since §9 A6. A spin computes the target from the CONFIRMED echo's
    // position and sends MODEM <n> / MODEM OF; the display moves only on the
    // radio's echo.

    private bool CanSpinModemNow() => CanSpinModem;

    [RelayCommand(CanExecute = nameof(CanSpinModemNow))]
    private void ModemUp() => SpinModem(+1);

    [RelayCommand(CanExecute = nameof(CanSpinModemNow))]
    private void ModemDown() => SpinModem(-1);

    private void SpinModem(int direction)
    {
        if (!Editable) return;
        int pos = ModemPosition(_modem.ActiveModem);
        if (pos < 0) return;                            // no confirmed basis

        // ROUND 13 B1 (item 6, owner ruling 2026-08-20): the wheel needs the
        // ENABLED set to skip disabled presets, and the operate pane is where
        // the operator actually lives — so the wheel pays for the presence read
        // itself rather than hoping someone opened the settings card. The
        // surface's gate is the presence STATE, so this is a no-op on every
        // spin after the first (see ModemSurface.EnsurePresenceLoaded).
        _modem.EnsurePresenceLoaded();

        if (NextSelectablePosition(pos, direction) is not { } target) return;
        SelectModem(PositionTarget(target));
    }

    /// <summary>
    /// ROUND 13 B1 (item 6, owner ruling 2026-08-20) — THE SKIP, computed at
    /// TARGET-COMPUTATION time. Step in the pressed direction until a position
    /// the radio will actually take, at most one full turn of the wheel.
    ///
    /// <list type="bullet">
    ///   <item><b>OFF (position 0) is always selectable</b> and therefore
    ///     always terminates the walk — the backstop that makes this loop
    ///     total.</item>
    ///   <item><b>Preset <i>n</i> is selectable</b> iff a presence read has
    ///     COMPLETED and its enabled set contains <i>n</i>. Absence from a
    ///     completed bulk listing is the only captured DISABLED signal there
    ///     is (round 11 §6).</item>
    ///   <item><b>While presence is Unknown or InFlight the FIRST adjacent
    ///     step is taken, exactly as before</b> — the app never invents a
    ///     constraint it has not read (constitution §3.1). The radio validates;
    ///     a refusal still toasts, correctly.</item>
    ///   <item><b>A COMPLETED-and-empty enabled set makes the wheel
    ///     OFF-only</b>, and a press while already at OFF returns null: no
    ///     command, no error. There is nothing else to select and nothing
    ///     honest to say.</item>
    /// </list>
    ///
    /// <para>The no-optimism contract is UNTOUCHED: this changes only which
    /// target a press computes. The display still moves on the radio's echo
    /// alone, and the skip is scoped to the PRESENCE axis only — an axis
    /// presence does not predict (a hop-mode restriction, front-panel
    /// staleness) still refuses on the wire and still toasts.</para>
    ///
    /// <para>Null = this press selects nothing.</para>
    /// </summary>
    private int? NextSelectablePosition(int pos, int direction)
    {
        var presence = _modem.PresetPresence;
        // F10: the enabled set only answers about the band it was READ at. A
        // completed SSB listing says nothing about 7-9, so under HOP it is "no
        // data" and the wheel takes the adjacent step — the radio validates,
        // exactly as it did before any presence read existed.
        bool known = _modem.ConfirmedMode is { } mode && presence.Covers(mode);
        int positions = WheelPositions;
        int first = Scope.First;

        for (int step = 1; step <= positions; step++)
        {
            int candidate = ((pos + direction * step) % positions + positions) % positions;
            if (candidate == 0) return candidate;                   // OFF: always
            if (!known) return candidate;                           // no data → adjacent step
            int preset = candidate - 1 + first;
            if (presence.Enabled.Contains(preset)) return candidate;
        }

        // Unreachable in practice — OFF terminates every walk within one turn.
        return null;
    }

    private void SelectModem(string? target)
    {
        if (!Editable || string.IsNullOrWhiteSpace(target)) return;
        var modem = _modem.ActiveModem;
        if (target == "OFF")
        {
            if (modem.IsConfirmed && modem.Value == "OFF") return;     // re-click guard
            _modem.Off();
            return;
        }
        if (ActiveModemPreset(modem) == target) return;               // already active
        _modem.Select(target);
    }
}
