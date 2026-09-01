using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Modem slice (round 8 ED — the PowerSurface shape for a
/// CROSS-MODE global setting): the active-modem echo mirror and the select
/// intents, plus the mode gate the picker needs. Extracted from SsbSurface:
/// the MODEM commands are not SSB-scoped — engagement is bench-confirmed
/// from SSB and ALE prompts (probe R8), and reported in every mode's SH
/// block.
/// <para><b>The HOP set path is no longer provisional (clone-field round 2
/// F9/F10, 2026-08-21).</b> P5–P5d2 ran every form at a <c>HOP&gt;</c> prompt:
/// the prompt owns presets <b>7-9</b>, selects them exactly as SSB does
/// (<c>MODEM 9</c> → <c>MODEM 9 DAT9</c>), answers <c>PRESET DISABLED</c> for a
/// disabled one and <c>INVALID MODEM PRESET</c> for 0-6. So the surface is
/// PROMPT-SCOPED now — see <see cref="PresetRange"/> — and the modem state at
/// <c>HOP&gt;</c> is SEPARATE from SSB's (P5d).</para></summary>
public sealed class ModemSurface : RadioSurface
{
    public ModemSurface(Prc138Radio radio)
        : base(radio, RadioProperty.ActiveModem, RadioProperty.OperatingMode,
            RadioProperty.ModemPresets,
            // Round 11 §8: the preset PRESENCE store (enabled/disabled) and
            // the completion of one modem read operation.
            RadioProperty.ModemPresetPresence, RadioProperty.ModemPresetRead) { }

    /// <summary>Active modem, verbatim ("OFF" or the selection echo "1 T39").</summary>
    public Confirmed<string> ActiveModem => Radio.State.ActiveModem;

    /// <summary>The picker's gate: the radio has CONFIRMED a mode this
    /// session (any mode — the pane hosting the row is already mode-gated
    /// by its own visibility).</summary>
    public bool IsModeConfirmed => Radio.State.OperatingMode.IsConfirmed;

    /// <summary>The session is CONFIRMED at an <c>ALE&gt;</c> prompt. Round 11
    /// §6: an <c>INTERLEAV</c>-carrying preset write is SWALLOWED there
    /// (VERIFIED 2026-08-16 — the radio answers as if it stored, and the
    /// interleave does not change), so the card refuses it app-side instead of
    /// reporting a success that did not happen. Unconfirmed reads FALSE: the
    /// guard only fires on a prompt the radio has actually reported.</summary>
    public bool IsAlePrompt =>
        Radio.State.OperatingMode.IsConfirmed
        && Radio.State.OperatingMode.Value == OperatingMode.Ale;

    /// <summary>The session is CONFIRMED at a <c>HOP&gt;</c> prompt — the
    /// scope in which presets 7-9 exist and 0-6 do not (F9/F10/F11). False
    /// while unconfirmed, like its ALE sibling: a scope the radio has not
    /// reported is not one this surface claims.</summary>
    public bool IsHopPrompt =>
        Radio.State.OperatingMode.IsConfirmed
        && Radio.State.OperatingMode.Value == OperatingMode.Hop;

    /// <summary>The CONFIRMED mode, or null. What the scope-following wheel and
    /// the scope-following presets card key on.</summary>
    public OperatingMode? ConfirmedMode =>
        Radio.State.OperatingMode.IsConfirmed ? Radio.State.OperatingMode.Value : null;

    /// <summary>The preset numbers the CONFIRMED prompt owns — 0-6 at
    /// <c>SSB&gt;</c>/<c>ALE&gt;</c>, 7-9 at <c>HOP&gt;</c>
    /// (<see cref="ModemPresetScope"/>).
    /// <para>UNCONFIRMED, this reads the SSB/ALE band as a placeholder — and
    /// NOTHING may act on that (audit round 2). It is a DISPLAY arithmetic
    /// helper, not a permission: every caller that could put a preset number on
    /// the wire gates on <see cref="ConfirmedMode"/> first, and Core refuses
    /// the whole preset family while the mode is unconfirmed, so the
    /// placeholder cannot reach the radio. The comment here used to justify it
    /// as "matching Core's own fallback"; that fallback is gone.</para></summary>
    public (int First, int Last) PresetRange =>
        ModemPresetScope.Range(ConfirmedMode ?? OperatingMode.Ssb);

    public void Select(string presetNumberOrName) => Radio.Ssb.SelectModem(presetNumberOrName);
    public void Off() => Radio.Ssb.ModemOff();

    // ---- Round 8 (EE, X7): the stored-preset listing + programming --------

    /// <summary>The preset FIELDS mirror: one raw line per preset the radio
    /// has reported, "PRESET" stripped ("1 T39  ASYNC DATA   BAUD 2400  TYPE
    /// 39tone  INTER long"), upsert-keyed on the preset number and NEVER
    /// cleared by a read (round 11 §8).</summary>
    public IReadOnlyList<string> Presets => Radio.State.ModemPresets;

    /// <summary>The ENABLED/DISABLED store: unknown until a presence read
    /// completes, then the enabled-number set the bulk listing reported. A
    /// preset with a fields row that is NOT in a COMPLETED set is
    /// DISABLED — the only captured signal there is.</summary>
    public RadioState.Presence PresetPresence => Radio.State.ModemPresetPresence;

    /// <summary>Completion record of the last modem read operation (targeted
    /// or presence) — matched to a request by id equality.</summary>
    public AleReadCompletion LastPresetRead => Radio.State.LastModemRead;

    /// <summary>MODEM PRE n + sentinel — ONE preset's FIELDS (the only read
    /// that sees a DISABLED preset). Returns the operation's read id.</summary>
    public long QueryPreset(int preset) => Radio.Ssb.QueryModemPreset(preset);

    /// <summary>The CONFIRMED PROMPT'S presets, one <c>MODEM PRE n</c> each +
    /// ONE sentinel — a SINGLE operation, so the presence read queued behind it
    /// cannot open its window early. <c>MODEM PRE 0</c> … <c>MODEM PRE 6</c> at
    /// <c>SSB&gt;</c>/<c>ALE&gt;</c>, <c>MODEM PRE 7</c> … <c>MODEM PRE 9</c> at
    /// <c>HOP&gt;</c> (F9).</summary>
    public long RefreshPresetFields() => Radio.Ssb.RefreshModemPresets();

    /// <summary>Bare MODEM PRE + sentinel — the ENABLED set, committed
    /// atomically on the sentinel. Never clears or replaces the fields
    /// mirror.</summary>
    public long QueryPresetPresence() => Radio.Ssb.QueryModemPresetPresence();

    /// <summary>
    /// ROUND 13 B1 (plan §4 B1, owner ruling 2026-08-20) — make sure the
    /// PRESENCE store has been asked for, from whichever surface consumer needs
    /// it first. Both the settings card (its state column) and the operate
    /// wheel (its skip logic) need the enabled set, and neither can pay for it
    /// per gesture.
    ///
    /// <para><b>The gate is the PRESENCE STATE itself, not a session flag.</b>
    /// Round 12 kept a <c>_presenceLoadedThisSession</c> bool on the settings
    /// ViewModel; that flag could not be shared with a second consumer without
    /// inventing a session seam, and it went dark for the WHOLE session after
    /// one failed read. The mirror already carries the fact: it resets to
    /// <see cref="RadioState.PresenceState.Unknown"/> on every connect
    /// (<c>RadioState.ResetForConnect</c>), so "once per session" is EMERGENT
    /// here — no subscription, no constructor change, no caller fan-out.</para>
    ///
    /// <para><b>The contract this adopts, explicitly</b> (plan §4 B1):
    /// COALESCED WHILE PENDING, RETRY AFTER AN ABANDONED READ.
    /// <list type="bullet">
    ///   <item><c>InFlight</c> / <c>Completed</c> → no-op.</item>
    ///   <item>A presence request QUEUED behind an active targeted read still
    ///     reports <c>Unknown</c> (it is not promoted to <c>InFlight</c> until
    ///     the targeted operation's sentinel answers), so a second ensure
    ///     re-issues — and the radio-side single-slot queue COALESCES it onto
    ///     the same pending id. One extra call, no extra wire line.</item>
    ///   <item>An UNANSWERED operation restores the last committed presence —
    ///     <c>Unknown</c> when none ever committed — so a later ensure RETRIES.
    ///     That is an improvement on the old flag, not a regression.</item>
    /// </list>
    /// Callers must therefore be GESTURE-driven (a landing, a spin), never
    /// driven by the mirror's own change event: a retry loop wired to
    /// <see cref="RadioSurface.Changed"/> would re-issue on every abandoned
    /// read forever.</para>
    /// </summary>
    public void EnsurePresenceLoaded()
    {
        // CLONE-FIELD ROUND 2 F10 — THE GATE IS SCOPE-AWARE. The bulk listing
        // is PROMPT-SCOPED (0-6 at SSB>/ALE>, 7-9 at HOP>), so a COMPLETED set
        // read at the other prompt says nothing about this one: treating it as
        // loaded would render every preset of the current scope "disabled" off
        // a listing that never mentioned them.
        //
        // AUDIT ROUND 2, MAJOR 1 — two corrections, both about NOT ASKING A
        // QUESTION THE APP CANNOT NAME THE PROMPT FOR:
        //
        //   * UNCONFIRMED ASKS NOTHING. This used to fall through to a read
        //     with a null scope, which is the very hole the scope key exists to
        //     close — and on the ordinary Ready-before-prompt ordering that was
        //     the FIRST read of every session. The caller OWES its landing and
        //     pays it when the mode confirms (ModemViewModel/ModemPresetsViewModel).
        //   * AN IN-FLIGHT READ IS ONLY A REASON TO WAIT IF IT IS ASKING THIS
        //     BAND'S QUESTION. Returning on any in-flight op let a scope change
        //     land silently on nothing: the unscoped/other-scope answer
        //     committed, no landing re-triggered, and the first press had no
        //     data for the band it was spinning. A read for ANOTHER band is now
        //     QUEUED BEHIND IT with its own scope (the round-1 queue mechanism),
        //     so both questions get asked and each commits to its own band.
        if (ConfirmedMode is not { } mode) return;
        if (PresetPresence.Covers(mode)) return;
        if (Radio.State.ModemPresenceReadScope is { } asked
            && ModemPresetScope.SameScope(asked, mode)) return;
        QueryPresetPresence();
    }

    /// <summary>One-line preset write in the round-9 short-token vocabulary
    /// (<see cref="ViewModels.ModemPresetVocabulary"/> owns the app-side
    /// column; the builder validates its own). The echoed listing-form line
    /// UPSERTS the fields mirror — but round 11 §6 retired the round-9 rule
    /// that it IS the read-back: the echo cannot show a SILENTLY CLAMPED baud
    /// and never carries EN/DIS at all, so a caller verifies with
    /// <see cref="QueryPreset"/> and, for a state write,
    /// <see cref="QueryPresetPresence"/>. Baud is a TOKEN, not a number: the
    /// HELP set is discrete and includes <c>VO</c>.</summary>
    public void ProgramPreset(
        int preset, string name, string type, string dataMode, string baud,
        string? interleave, string? mark, string? space, bool? enabled)
        => Radio.Ssb.ProgramModemPreset(preset, name, type, dataMode, baud,
            interleave, mark, space, enabled);

    /// <summary>The <c>HOP&gt;</c> preset write (F9/F11): the SHORT line with no
    /// TYPE, and the EN/DIS token on its OWN line LAST — any field write
    /// re-enables a disabled preset (P5b), so the state has to follow the
    /// fields. Baud is {75, 150, 300} and the builder refuses anything else,
    /// because an out-of-vocabulary value is SILENTLY ignored with the old
    /// value echoed back (P5c).</summary>
    public void ProgramHopPreset(
        int preset, string name, SyncMode sync, DataMode data, string baud, bool? enabled)
        => Radio.Ssb.ProgramHopModemPreset(preset, name, sync, data, baud, enabled);
}
