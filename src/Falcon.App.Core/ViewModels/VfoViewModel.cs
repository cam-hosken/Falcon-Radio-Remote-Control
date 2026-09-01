using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;

namespace Falcon.App.Core.ViewModels;

/// <summary>Which arrow key the platform page routed to the armed VFO.</summary>
public enum VfoKey { Up, Down, Left, Right }

/// <summary>
/// One digit of the frequency readout: its display character ("—" until the
/// radio reports the frequency this session) and the ▲/▼ chevron commands.
/// Chevron taps route to the parent — each sends an ABSOLUTE FR/RXF/TXF with
/// this digit's place bumped, computed from the CONFIRMED value (no
/// optimistic updates; the answer line moves the display).
/// </summary>
public partial class VfoDigitViewModel : ObservableObject
{
    private readonly VfoViewModel _parent;

    /// <summary>0 = most significant of the 8-digit Hz string.</summary>
    public int Index { get; }
    /// <summary>True for the TX readout row (split mode).</summary>
    public bool IsTxRow { get; }

    [ObservableProperty] private string text = "—";
    [ObservableProperty] private bool canBump;
    /// <summary>True for the digit under the armed keyboard cursor — the view
    /// paints its background green as the armed cue. In non-split BOTH rows'
    /// cursor-place digit is set (FR moves both); in split only the pointed
    /// row's. ↑/↓ edit this digit's place, ←/→ move the cursor.</summary>
    [ObservableProperty] private bool isCursor;

    internal VfoDigitViewModel(VfoViewModel parent, int index, bool isTxRow)
    {
        _parent = parent;
        Index = index;
        IsTxRow = isTxRow;
    }

    /// <summary>Place names for the 8-digit Hz string, most significant
    /// first (index 0 = 10 MHz).</summary>
    private static readonly string[] PlaceNames =
        ["10 MHz", "1 MHz", "100 kHz", "10 kHz", "1 kHz", "100 Hz", "10 Hz", "1 Hz"];

    // Accessibility (Stage 8 audit N1): place-bearing chevron labels so a
    // screen reader announces WHICH digit a chevron bumps, not just "up".
    public string UpDescription => $"{RowPrefix}{PlaceNames[Index]} digit up";
    public string DownDescription => $"{RowPrefix}{PlaceNames[Index]} digit down";
    private string RowPrefix => IsTxRow ? "TX " : "";

    [RelayCommand]
    private void Up() => _parent.BumpDigit(IsTxRow, Index, +1);

    [RelayCommand]
    private void Down() => _parent.BumpDigit(IsTxRow, Index, -1);
}

/// <summary>
/// The VFO (GUI rejigger F1–F4): per-digit readout with chevrons (the ONLY
/// tuning surface, plus the Windows keyboard), RX and TX rows both always
/// visible, the F2 split override model, the radio-authoritative STEP as a
/// passive display, and desktop keyboard arming anchored on the readout
/// digits. F6: frequency is channel-stored, so every frequency edit (and
/// split entry) is gated on the CONFIRMED channel being 00 — an unconfirmed
/// channel counts as NOT 00 (never enable on a default). Constitution:
/// displayed digits change ONLY when the radio's answer arrives; repeat-fire
/// inputs are clamped by <see cref="RepeatRateLimiter"/> (drop, never queue).
/// </summary>
public partial class VfoViewModel : ObservableObject
{
    /// <summary>Repeat-fire clamp (old VfoKnob spec: keyboard 125 ms; the
    /// prompt-gated transport is the real pace — this is the VM belt).</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(125);

    // F5 (plan-clone-field-round2.md, decision D3): the band bound is RADIO-WIDE
    // and has exactly ONE definition — Wire's, measured by probe P2. This VM
    // used to carry its own copy at an unmeasured 29 999 999 ceiling.
    private const int MinHz = Wire.MinFrequencyHz;
    private const int MaxHz = Wire.MaxFrequencyHz;

    private readonly SsbSurface _ssb;
    private readonly ChannelSurface _channel;
    private readonly RadioSession _session;
    private readonly RepeatRateLimiter _limiter;
    private bool _splitArmed;
    private int? _lastConfirmedChannel;

    // ---- R12: the split-flash hold (owner ruling R12, round 11 §8) ----------
    // The wire settles the seam. `FR`/`INC`/`DEC` answer a SEPARATE `RxFr` line
    // and a SEPARATE `TxFr` line (docs/protocol.md, "FR"→"RxFr"+"TxFr"), and
    // Core commits and RAISES each independently — there is no two-value report
    // to commit atomically, so there is nothing Core could fix. Between the two
    // raises RX already holds the new frequency while TX still holds the old
    // one, which is indistinguishable from a real split: IsSplit flashed true on
    // EVERY frequency change and the TX row highlighted for one frame.
    //
    // The fix is transition suppression HERE — but a suppression that cannot
    // outlive the gap it exists to bridge. THE HOLD IS SCOPED TO THE ANSWER
    // THAT CREATES THE GAP: only a command THIS ViewModel sent whose answer
    // carries BOTH lines (FR from the non-split chevrons and from the merge
    // press, INC, DEC) opens a hold window, and the TxFr that completes that
    // answer closes it. Anything else — an `RXF` typed at the Console, a
    // radio-initiated report, an answer that simply never brings its TX half —
    // is NOT held, so a REAL split always surfaces on the line that proves it.
    //
    // (Audit round 1, MAJOR-1: the first shape of this fix armed on ANY RX-only
    // raise and released on a TX raise or a "prompt release" read as the
    // confirmed MODE changing. Both halves were wrong. Core raises
    // OperatingMode only on a CHANGE, so a trailing same-mode prompt released
    // nothing, and an `RXF` answered alone therefore left the hold open
    // FOREVER — the UI hid a genuine split. Scoping the window to the answer
    // this VM asked for removes the unbounded case by construction.)
    //
    // `_splitArmed` (the F2 override) is untouched — an ARMED display is
    // already split, so no transition is happening and nothing is ever held.
    private bool _splitHeld;
    private bool _twoLineAnswerPending;
    private (bool Confirmed, string? Value) _lastRx;
    private (bool Confirmed, string? Value) _lastTx;

    /// <summary>The radio-reported split AS DISPLAYED — <c>radioSplit</c> with a
    /// pending hold applied. <see cref="ToggleSplit"/> branches on THIS rather
    /// than on the raw mirror, so a press always does what the operator can see
    /// (audit round 1, MAJOR-1b: pressing inside a hold window read the raw
    /// split and sent a MERGE while the display said non-split and the operator
    /// meant to arm).</summary>
    private bool _displayedRadioSplit;

    // Armed-keyboard digit cursor — app-side VIEW state (sends nothing on its
    // own). Default on arm: index 4 (1 kHz place) on the RX row.
    private int _cursorIndex = 4;
    private bool _cursorOnTx;

    public IReadOnlyList<VfoDigitViewModel> RxDigits { get; }
    public IReadOnlyList<VfoDigitViewModel> TxDigits { get; }

    [ObservableProperty] private bool isSplit;
    [ObservableProperty] private string stepText = "—";
    [ObservableProperty] private bool areSsbControlsEnabled;
    [ObservableProperty] private string ssbDisabledReason = "";
    [ObservableProperty] private bool isVfoArmed;

    public VfoViewModel(SsbSurface ssb, ChannelSurface channel, RadioSession session, TimeProvider time)
    {
        _ssb = ssb;
        _channel = channel;
        _session = session;
        _limiter = new RepeatRateLimiter(time, RepeatInterval);

        RxDigits = [.. Enumerable.Range(0, 8).Select(i => new VfoDigitViewModel(this, i, isTxRow: false))];
        TxDigits = [.. Enumerable.Range(0, 8).Select(i => new VfoDigitViewModel(this, i, isTxRow: true))];

        ssb.Changed += (_, _) => Refresh();
        channel.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) => Refresh();
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool SsbReady => Ready && _ssb.IsSsbConfirmed;

    /// <summary>F6 00-gate: frequency is one of the channel-stored six —
    /// editable only while the CONFIRMED channel is 00. Unconfirmed channel
    /// counts as NOT 00 (conservative; never enable on a default).</summary>
    private bool ChannelZero => _channel.Current.IsConfirmed && _channel.Current.Value == 0;

    private bool FreqEditable => SsbReady && ChannelZero;

    private void Refresh()
    {
        // F2a (owner ruling): the override does not survive a session drop —
        // cleared whenever the session leaves Ready, so after a reconnect
        // equal confirmed freqs render un-split (a radio-reported RX≠TX
        // still displays as split; the override is irrelevant there).
        if (!Ready) { _splitArmed = false; _splitHeld = false; _twoLineAnswerPending = false; }

        // F2: a CONFIRMED channel CHANGE clears the app-side split override
        // (the radio's reset rule — nothing else clears it during a session).
        var ch = _channel.Current;
        if (ch.IsConfirmed)
        {
            if (_lastConfirmedChannel is int last && last != ch.Value)
                _splitArmed = false;
            _lastConfirmedChannel = ch.Value;
        }

        var rx = _ssb.RxFrequency;
        var tx = _ssb.TxFrequency;

        bool radioSplit = rx.IsConfirmed && tx.IsConfirmed && rx.Value != tx.Value;
        IsSplit = ResolveSplit(rx, tx, radioSplit) || _splitArmed;

        for (int i = 0; i < 8; i++)
        {
            RxDigits[i].Text = DigitChar(rx, i);
            RxDigits[i].CanBump = FreqEditable && rx.IsConfirmed;
            TxDigits[i].Text = DigitChar(tx, i);
            // F2: TX controls are live only while split (radio-reported or
            // override-armed) — greyed otherwise, though the row always shows.
            TxDigits[i].CanBump = FreqEditable && tx.IsConfirmed && IsSplit;
        }

        StepText = !_ssb.Step.IsConfirmed ? "—" : _ssb.Step.Value switch
        {
            FrequencyStep.OneHz => "1 Hz",
            FrequencyStep.TenHz => "10 Hz",
            FrequencyStep.OneHundredHz => "100 Hz",
            FrequencyStep.OneKHz => "1 kHz",
            FrequencyStep.TenKHz => "10 kHz",
            FrequencyStep.OneHundredKHz => "100 kHz",
            _ => "—",
        };

        AreSsbControlsEnabled = FreqEditable;
        SsbDisabledReason = !Ready
            ? "Not connected — open Settings → Connection to connect."
            : !_ssb.IsSsbConfirmed
                ? "Frequency controls are SSB-only — waiting for the radio to confirm SSB."
                : !ChannelZero
                    ? "Channel-stored settings are editable on CH 00 only."
                    : "";

        // Auto-disarm when the group disables (old VfoKnob contract; the F6
        // gate closing — a channel change away from 00 — disarms too).
        if (!FreqEditable && IsVfoArmed) IsVfoArmed = false;

        // Keep the cursor on a valid row: a merge (or a split-override disarm)
        // to non-split pulls a TX cursor back to RX at the same index — there
        // is no TX frequency to point at when the rows are locked together.
        if (!IsSplit) _cursorOnTx = false;
        UpdateCursor();

        IncrementCommand.NotifyCanExecuteChanged();
        DecrementCommand.NotifyCanExecuteChanged();
        StepUpCommand.NotifyCanExecuteChanged();
        StepDownCommand.NotifyCanExecuteChanged();
        ToggleSplitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// R12: the radio-reported split, with the mid-answer flash HELD.
    ///
    /// <para>Each raise is classified by WHAT MOVED since the last one — the
    /// only signal available here, because the surface's Changed event is
    /// filtered to a property SET, not to a property. The flash shape is an
    /// RX-only move that would turn a non-split display split; it is held
    /// ONLY while a two-line answer this ViewModel asked for is outstanding,
    /// and the TxFr that completes that answer settles it.</para>
    ///
    /// <para><b>Every other route is deliberately unheld</b> — that is the
    /// bound. An <c>RXF</c> the operator typed at the Console answers one line
    /// and never another, so it surfaces on that line; a radio-initiated
    /// report does the same. The cost is that a frequency change this VM did
    /// not make (a channel select's <c>SH</c>, a console <c>FR</c>) can still
    /// flash — which is a cosmetic regression against hiding a real split, and
    /// the trade is recorded rather than hidden.</para>
    ///
    /// <para>Nothing is held when the display is ALREADY split either: there is
    /// no transition to suppress, so the F2 override and a standing radio split
    /// pass straight through, and a merge (<c>FR rx</c>, which answers
    /// RxFr-unchanged then TxFr-changed) transitions exactly once, on the TX
    /// line.</para>
    /// </summary>
    private bool ResolveSplit(
        Falcon.Core.Radio.Confirmed<string> rx, Falcon.Core.Radio.Confirmed<string> tx, bool radioSplit)
    {
        bool rxRaise = _lastRx != (rx.IsConfirmed, rx.Value);
        bool txRaise = _lastTx != (tx.IsConfirmed, tx.Value);

        _lastRx = (rx.IsConfirmed, rx.Value);
        _lastTx = (tx.IsConfirmed, tx.Value);

        // The TX half is what completes a two-line answer — and it is the only
        // thing that does. A window that is never completed is closed by the
        // session drop; it can never be closed by silence.
        if (txRaise) _twoLineAnswerPending = false;

        if (!radioSplit) _splitHeld = false;                       // nothing left to hold
        else if (_splitHeld) { if (txRaise || !_twoLineAnswerPending) _splitHeld = false; }
        else if (!IsSplit && rxRaise && !txRaise && _twoLineAnswerPending) _splitHeld = true;

        _displayedRadioSplit = radioSplit && !_splitHeld;
        return _displayedRadioSplit;
    }

    /// <summary>Open the R12 hold window: the command just sent answers with
    /// BOTH an <c>RxFr</c> and a <c>TxFr</c> line, so the gap between them is
    /// this ViewModel's to bridge. Called at the send sites and nowhere
    /// else — a window nobody opened is a flash nobody suppresses, which is
    /// the safe direction.</summary>
    private void ExpectTwoLineAnswer() => _twoLineAnswerPending = true;

    private static string DigitChar(Falcon.Core.Radio.Confirmed<string> freq, int index)
        => freq.IsConfirmed && freq.Value is { Length: 8 } v ? v[index].ToString() : "—";

    /// <summary>Paint the armed keyboard cursor: while armed on CH 00 in SSB,
    /// the pointed digit's <see cref="VfoDigitViewModel.IsCursor"/> is set (the
    /// view greens its background); everything else is cleared. NON-SPLIT sets
    /// BOTH rows' cursor-place digit (↑/↓ send FR, moving both); SPLIT sets
    /// only the pointed row's. Depends on no confirmed radio Step — the cursor
    /// shows the instant the VFO is armed.</summary>
    private void UpdateCursor()
    {
        for (int i = 0; i < 8; i++)
        {
            RxDigits[i].IsCursor = false;
            TxDigits[i].IsCursor = false;
        }
        if (!IsVfoArmed || !FreqEditable) return;

        if (IsSplit)
            (_cursorOnTx ? TxDigits : RxDigits)[_cursorIndex].IsCursor = true;
        else
        {
            RxDigits[_cursorIndex].IsCursor = true;
            TxDigits[_cursorIndex].IsCursor = true;
        }
    }

    // ---- Per-digit chevrons ------------------------------------------------

    /// <summary>Send an absolute frequency with digit <paramref name="index"/>
    /// bumped by <paramref name="sign"/> — computed from the CONFIRMED value,
    /// clamped to the measured 1.6–60 MHz band (out-of-band results send nothing),
    /// rate-limited (a held chevron drops repeats, it never queues them).</summary>
    internal void BumpDigit(bool txRow, int index, int sign)
    {
        if (!FreqEditable) return;      // SSB-domain command + F6 00-gate
        if (txRow && !IsSplit) return;  // F2: TX controls live only while split

        var source = txRow ? _ssb.TxFrequency : _ssb.RxFrequency;
        if (!source.IsConfirmed || source.Value is not { Length: 8 } current) return;

        long hz = long.Parse(current, CultureInfo.InvariantCulture);
        long place = (long)Math.Pow(10, 7 - index);
        long target = hz + sign * place;
        if (target is < MinHz or > MaxHz) return;

        if (!_limiter.TryFire()) return;

        Send(txRow, target.ToString("D8", CultureInfo.InvariantCulture));
    }

    private void Send(bool txRow, string frequency)
    {
        if (IsSplit)
        {
            // RXF/TXF answer ONE line. No hold window: whatever that line
            // reports is the whole truth of this answer (R12).
            if (txRow) _ssb.SetTxFrequency(frequency);
            else _ssb.SetRxFrequency(frequency);
        }
        else
        {
            ExpectTwoLineAnswer();          // FR answers RxFr THEN TxFr
            _ssb.SetFrequency(frequency);   // FR sets RX=TX
        }
    }

    // ---- Split (F2 override model) -----------------------------------------

    private bool CanToggleSplit() => FreqEditable;

    /// <summary>F2 legs, exactly: with radio-reported split (confirmed
    /// RxFr ≠ TxFr) a press sends FR &lt;rx&gt; — the merge, the only way out
    /// of a real split — and the rows un-highlight only when the answer
    /// lands. With no radio split, a press ARMS the app-side override (view
    /// state, sends nothing): the button highlights and the TX controls
    /// enable so a split can be entered via the TX digit chevrons; a press
    /// while override-armed disarms (sends nothing). The override clears on
    /// a confirmed channel change (see Refresh).</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSplit))]
    private void ToggleSplit()
    {
        if (!FreqEditable) return;

        // R12 (audit round 1, MAJOR-1b): branch on the DISPLAYED split, not on
        // the raw mirror. Inside a hold window the mirror already says RX != TX
        // while the display still says non-split; reading the mirror there sent
        // a MERGE to an operator who was looking at a non-split readout and
        // pressing to ARM. What the operator can see is what the press acts on.
        if (_displayedRadioSplit)
        {
            _splitArmed = false;
            var rx = _ssb.RxFrequency;
            if (rx.Value is not null)
            {
                ExpectTwoLineAnswer();      // the merge FR answers both lines
                _ssb.SetFrequency(rx.Value);
            }
        }
        else
        {
            _splitArmed = !_splitArmed;     // arm / disarm — sends nothing
        }
        Refresh();
    }

    // ---- INC/DEC + STEP (radio-authoritative; keyboard-only since F3) ------

    private bool CanTune() => FreqEditable;

    // In-body guards repeat the CanExecute checks: ICommand.Execute does not
    // consult CanExecute, and the transport may be OPEN while the radio is
    // outside SSB — the guard must live in the body, not just the binding.

    [RelayCommand(CanExecute = nameof(CanTune))]
    private void Increment()
    {
        if (!CanTune() || !_limiter.TryFire()) return;
        ExpectTwoLineAnswer();              // INC answers like FR: RxFr + TxFr
        _ssb.Increment();
    }

    [RelayCommand(CanExecute = nameof(CanTune))]
    private void Decrement()
    {
        if (!CanTune() || !_limiter.TryFire()) return;
        ExpectTwoLineAnswer();              // DEC answers like FR: RxFr + TxFr
        _ssb.Decrement();
    }

    private bool CanChangeStep() => SsbReady && _ssb.Step.IsConfirmed;

    [RelayCommand(CanExecute = nameof(CanChangeStep))]
    private void StepUp() => ChangeStep(+1);

    [RelayCommand(CanExecute = nameof(CanChangeStep))]
    private void StepDown() => ChangeStep(-1);

    /// <summary>STEP is radio state: the change is a command computed from
    /// the CONFIRMED current step; the answer moves the display. At either
    /// end of the range nothing is sent. Rate-limited with the same clamp as
    /// the tune inputs (held Left/Right keys repeat-fire too).</summary>
    private void ChangeStep(int direction)
    {
        if (!CanChangeStep()) return;
        int target = (int)_ssb.Step.Value + direction;
        if (target < (int)FrequencyStep.OneHz || target > (int)FrequencyStep.OneHundredKHz) return;
        if (!_limiter.TryFire()) return;
        _ssb.SetStep((FrequencyStep)target);
    }

    // ---- Desktop keyboard arming (old VfoKnob contract → MAUI) -------------------

    /// <summary>Click/tap the frequency readout digits to arm (F3a — the
    /// XAML trigger surface; Windows only). Armed only while connected,
    /// confirmed in SSB, and on CH 00 (the F6 gate — arming exists to tune,
    /// and frequency is channel-stored).</summary>
    [RelayCommand]
    private void ToggleArm()
    {
        if (!FreqEditable) { IsVfoArmed = false; UpdateCursor(); return; }
        IsVfoArmed = !IsVfoArmed;
        if (IsVfoArmed) { _cursorIndex = 4; _cursorOnTx = false; }   // default: 1 kHz, RX
        UpdateCursor();     // cursor appears/clears immediately on arm/disarm
    }

    /// <summary>Explicit disarm — the platform page calls this on focus
    /// loss / window deactivation / navigation away (the old auto-disarm).</summary>
    public void Disarm()
    {
        IsVfoArmed = false;
        UpdateCursor();
    }

    /// <summary>Arrow keys while armed (digit-cursor model): ↑/↓ EDIT the
    /// pointed digit by its place (reusing <see cref="BumpDigit"/> — non-split
    /// sends FR, split sends RXF/TXF for the pointed row; rate-limited); ←/→
    /// MOVE the cursor and send nothing. Returns true when the key was consumed
    /// (the page sets Handled — the old IsInputKey equivalent).</summary>
    public bool HandleKey(VfoKey key)
    {
        if (!IsVfoArmed || !FreqEditable) return false;
        switch (key)
        {
            case VfoKey.Up: EditCursor(+1); return true;
            case VfoKey.Down: EditCursor(-1); return true;
            case VfoKey.Left: MoveCursor(-1); return true;
            case VfoKey.Right: MoveCursor(+1); return true;
            default: return false;
        }
    }

    /// <summary>↑/↓: bump the pointed digit's place. Non-split edits RX (FR
    /// moves both); split edits the pointed row (RXF/TXF). Band-clamp and
    /// rate-limit live in <see cref="BumpDigit"/>.</summary>
    private void EditCursor(int sign) => BumpDigit(IsSplit && _cursorOnTx, _cursorIndex, sign);

    /// <summary>←/→: move the cursor, sending nothing. NON-SPLIT clamps at
    /// index 0 and 7 (one frequency, no cross-row). SPLIT walks a 16-position
    /// ring across RX[0..7]+TX[0..7] — right off RX[7]→TX[0], right off
    /// TX[7]→RX[0], left off RX[0]→TX[7], left off TX[0]→RX[7].</summary>
    private void MoveCursor(int step)
    {
        if (IsSplit)
        {
            int pos = (_cursorOnTx ? 8 : 0) + _cursorIndex;
            pos = ((pos + step) % 16 + 16) % 16;
            _cursorOnTx = pos >= 8;
            _cursorIndex = pos % 8;
        }
        else
        {
            _cursorOnTx = false;
            _cursorIndex = Math.Clamp(_cursorIndex + step, 0, 7);
        }
        UpdateCursor();
    }
}
