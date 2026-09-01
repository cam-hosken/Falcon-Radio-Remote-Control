using CommunityToolkit.Mvvm.ComponentModel;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>Tune status chip's exclusive display state. None renders "—"
/// until a tune lifecycle line has been seen this session (rejigger S7 —
/// there is no "Idle"; unreported is never a default). Marginal is a
/// qualifier on Complete, not a fourth outcome; FAULT is a normal outcome
/// (recovery = the operator tuning again — the TUNE button never disables
/// on FAULT).</summary>
public enum TuneChipState { None, Tuning, Complete, CompleteMarginal, Fault }

/// <summary>
/// Display-only spine status: keyline RX/TX indicator and the tune chip.
/// Three display states throughout: confirmed values render, unconfirmed
/// renders "—" (never a default — the enum-default leak class).
/// </summary>
public partial class SpineStatusViewModel : ObservableObject
{
    private readonly StatusSurface _status;

    [ObservableProperty] private string keylineText = "—";
    [ObservableProperty] private bool isKeylineConfirmed;
    [ObservableProperty] private bool isTransmitting;
    [ObservableProperty] private string tuneChipText = "—";
    [ObservableProperty] private TuneChipState tuneChip = TuneChipState.None;
    [ObservableProperty] private bool isTuning;

    public SpineStatusViewModel(StatusSurface status, RadioSession session)
    {
        _status = status;
        status.Changed += (_, _) => Refresh();
        // ResetForConnect clears the tune/keyline state SILENTLY (documented
        // "no events"), so a fresh session would otherwise keep displaying
        // the PREVIOUS session's outcome — "this session" (S7) demands a
        // re-read when the phase moves (audit round 1, W2).
        session.PhaseChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        IsTuning = _status.IsTuning;

        // CLONE ROUND 12 §9 B1 (the DISPLAY half; the Core re-poll half landed
        // in P1). A coupler tune TRANSMITS, but every tune line — TUNING and
        // all three terminals — carries no keyline report, so RadioState
        // deliberately UNCONFIRMS the keyline across the whole lifecycle
        // (RadioState.SetTuning / EndTune). The chip therefore blanked to "—"
        // mid-tune, at precisely the moment the operator most needs to see
        // that the radio is on air. That was the bench report.
        //
        // The fix is DISPLAY POLICY, not a fabricated mirror: while the
        // CONFIRMED tuning flag is up the chip reads TX, because a tune IS a
        // transmission and IsTuning is itself a reported fact (the radio said
        // TUNING). The keyline MIRROR is NOT written — nothing here calls into
        // RadioState, Core still holds the keyline unconfirmed, and the
        // instant the tune ends the chip falls back to whatever a real KEY
        // line has said. P1's re-poll is what brings a real one back.
        // The three properties below are DISPLAY state, not mirror claims —
        // IsKeylineConfirmed answers "has this chip something real to show",
        // which during a tune it does (the radio SAID `TUNING`). Nothing here
        // writes RadioState; the pin
        // TheMidTuneTx_IsDisplayPolicyOnly_TheKeylineMirrorStaysUnconfirmed_B1
        // asserts that against Core rather than trusting this comment.
        var keyline = _status.Keyline;
        bool keyedByReport = keyline.IsConfirmed && keyline.Value != KeylineState.Off;

        IsTransmitting = IsTuning || keyedByReport;
        IsKeylineConfirmed = IsTuning || keyline.IsConfirmed;
        KeylineText = IsTransmitting ? "TX" : (keyline.IsConfirmed ? "RX" : "—");

        // UI tweaks round 2 (A4): self-describing chip texts — the "TUNE:"
        // XAML prefix is gone, so each state names itself. Display wording is
        // ours; the wire token behind Fail is TUNE FAULT.
        (TuneChipState chip, string text) =
            _status.IsTuning ? (TuneChipState.Tuning, "Tuning…")
            : _status.IsTuneFail ? (TuneChipState.Fault, "Tune Fail")
            : _status.IsTuneComplete && _status.IsTuneMarginal ? (TuneChipState.CompleteMarginal, "Tune Marginal")
            : _status.IsTuneComplete ? (TuneChipState.Complete, "Tune Complete")
            : (TuneChipState.None, "—");
        TuneChip = chip;
        TuneChipText = text;
    }
}
