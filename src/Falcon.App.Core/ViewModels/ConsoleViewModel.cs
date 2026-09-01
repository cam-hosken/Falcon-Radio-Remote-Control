using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Surfaces;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Scrolling TX/RX line log with timestamps and badges (plan §4.5): every
/// sent line, every received line verbatim, every compensation burst, every
/// error, session notes. Bounded buffer; pause freezes the VIEW while the
/// buffer keeps collecting (bounded too), so nothing is lost while the
/// operator reads. Stage 8 adds the text filter (plan §4.5 "filter"): the
/// filter narrows the VIEW only — the backing buffer keeps every line, so
/// clearing the filter restores them.
///
/// <para><b>D19 (plan-clone-write-structural.md §2, 2026-08-30) moved the
/// EXPORT off this buffer entirely</b>: Save/Share now read
/// <see cref="GetSessionLogText"/>, which is the feed's FULL-SESSION log, not
/// this 500-line display store. Everything else here is unchanged — the cap,
/// the pause, the filter, and Copy (still the visible filtered log).</para>
///
/// <para><b>D18 (plan-clone-write-structural.md §2, 2026-08-30) adds the
/// GATED RAW INPUT</b>: <see cref="InputEnabled"/> arms
/// <see cref="InputText"/> + <see cref="SendCommand"/>, which hand the line to
/// <c>ConsoleFeed.SendRaw</c> and therefore to the Core's raw passthrough and
/// the transport's write queue. Everything the gate is, is here — the view
/// binds <see cref="InputEnabled"/> and <see cref="CanSend"/> and holds no
/// logic of its own.</para>
/// </summary>
public partial class ConsoleViewModel : ObservableObject
{
    public const int MaxEntries = 500;

    /// <summary>The VIEW: lines delivered to the display (not paused-pending)
    /// that match the current filter.</summary>
    public ObservableCollection<ConsoleLine> Entries { get; } = [];

    // Every delivered line regardless of filter (bounded like the view);
    // the filter re-projects Entries from this.
    private readonly List<ConsoleLine> _shown = [];
    private readonly List<ConsoleLine> _pendingWhilePaused = [];

    [ObservableProperty] private bool isPaused;

    /// <summary>APP-SIDE view state (two-way binding is legal): case-blind
    /// substring match against the line text or its badge (so "ERR"/"TX"
    /// filter by kind). Empty shows everything.</summary>
    [ObservableProperty] private string filterText = "";

    // ---- D18: the gated raw-command input ----------------------------------

    private readonly ConsoleFeed _feed;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §5.2 —
    /// invariant I-6: producers depend on the interface, never on
    /// <c>CloneService</c>). Null where there is no campaign to stand down
    /// for; the P2 optional-trailing-parameter convention.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>
    /// D18: THE GATE. False on a fresh view model and NEVER PERSISTED — no
    /// settings key, no restore path, nothing writes it but the operator's own
    /// press and the campaign disarm below.
    ///
    /// <para><b>"Off at every app run" means every PROCESS run.</b> This view
    /// model is a DI SINGLETON (MauiProgram) behind a transient page, so
    /// leaving the Console and coming back does not rebuild it and does not
    /// re-disarm: the gate holds for the life of the process, which is exactly
    /// the scope D18 decided. Killing and relaunching the app disarms
    /// it.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool inputEnabled;

    /// <summary>D18: the operator's typed line (two-way; APP-SIDE view state
    /// like <see cref="FilterText"/>). Cleared by a successful send.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string inputText = "";

    /// <summary>
    /// D18: the three conditions, in one place the view can bind and a test can
    /// read — armed, non-blank, and NO CLONE CAMPAIGN on the wire. The campaign
    /// term is the quiesce doctrine's collision class: a campaign owns the wire
    /// for a sequence whose answers it is counting, and an operator line
    /// injected into it is the 2026-08-28 field failure by hand.
    /// </summary>
    public bool CanSend
        => InputEnabled
           && !string.IsNullOrWhiteSpace(InputText)
           && !(_campaign?.CampaignActive ?? false);

    public ConsoleViewModel(ConsoleFeed feed, ICampaignSignal? campaign = null)
    {
        _feed = feed;
        _campaign = campaign;
        feed.EntryAdded += OnEntry;
        if (campaign is not null)
            campaign.Changed += (_, _) =>
            {
                // D18, THE BELT: the campaign START does not merely block sends
                // (CanSend already would) — it DROPS THE TOGGLE. A gate that
                // silently re-opens when the campaign ends is a gate the
                // operator stopped watching; re-arming is a deliberate press.
                if (campaign.CampaignActive) InputEnabled = false;
                // The END edge changes CanSend's third term with no property of
                // ours moving, so it is raised by hand.
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
            };
    }

    private void OnEntry(ConsoleEntry entry)
    {
        var line = new ConsoleLine(entry);
        if (IsPaused)
        {
            _pendingWhilePaused.Add(line);
            if (_pendingWhilePaused.Count > MaxEntries) _pendingWhilePaused.RemoveAt(0);
            return;
        }
        Append(line);
    }

    private void Append(ConsoleLine line)
    {
        _shown.Add(line);
        if (Matches(line)) Entries.Add(line);
        while (_shown.Count > MaxEntries)
        {
            var removed = _shown[0];
            _shown.RemoveAt(0);
            // Entries is an ordered subset of _shown, so a trimmed line can
            // only ever be the view's own head.
            if (Entries.Count > 0 && ReferenceEquals(Entries[0], removed))
                Entries.RemoveAt(0);
        }
    }

    private bool Matches(ConsoleLine line)
        => FilterText.Length == 0
           || line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
           || line.Badge.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    partial void OnFilterTextChanged(string value)
    {
        // Re-project the view from the backing buffer. While paused this
        // re-filters the FROZEN set only — pending lines stay pending.
        Entries.Clear();
        foreach (var line in _shown)
            if (Matches(line)) Entries.Add(line);
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) return;
        foreach (var line in _pendingWhilePaused) Append(line);
        _pendingWhilePaused.Clear();
    }

    /// <summary>D18: the ENABLE toggle, the Pause/Resume idiom — one command,
    /// one bound flag, the button's text flipped by a DataTrigger.</summary>
    [RelayCommand]
    private void ToggleInput() => InputEnabled = !InputEnabled;

    /// <summary>
    /// D18: SEND. Trims, hands the line to the Console's own surface seam
    /// (<c>ConsoleFeed.SendRaw</c> → the Core's raw passthrough →
    /// <c>SendLine</c> → the transport's prompt-gated write queue), and clears
    /// the box. No echo line of its own: the TX row appears through
    /// <c>LineSent</c> like every other send, so the log shows what actually
    /// reached the wire rather than what was typed.
    ///
    /// <para>The <see cref="CanSend"/> re-check inside the body is not
    /// redundant with the <c>CanExecute</c>: <c>RelayCommand.Execute</c> does
    /// not consult <c>CanExecute</c>, so the gate would live only in the view
    /// without it.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (!CanSend) return;
        _feed.SendRaw(InputText.Trim());
        InputText = "";
    }

    /// <summary>The VISIBLE (filtered) log as text — the Copy button copies
    /// what the operator sees.</summary>
    public string GetLogText()
        => string.Join(Environment.NewLine, Entries.Select(l => l.ToString()));

    /// <summary>
    /// D19 (plan-clone-write-structural.md §2, 2026-08-30): THE EXPORT —
    /// the WHOLE SESSION as text, straight from <c>ConsoleFeed.SessionEntries</c>,
    /// which is written before this view model ever sees a line. It is
    /// therefore blind to the filter, to the pause hold, and to the display's
    /// own 500-line trim: the 2026-08-30 live gate failed and was undiagnosable
    /// because Save/Share read the same capped store the display read, and the
    /// failing write's root window had already scrolled out of it.
    ///
    /// <para>It REPLACES <c>GetFullLogText</c>, which exported
    /// <c>_shown</c> + <c>_pendingWhilePaused</c> — that is, the display's own
    /// 500-line store. Leaving both would leave two "full log" accessors, which
    /// is exactly the ambiguity that produced the failure.</para>
    ///
    /// <para><b>The FORMAT is unchanged by construction</b>: every entry is
    /// rendered through the same <see cref="ConsoleLine"/> the display builds,
    /// so an exported line is byte-identical to what the old export produced
    /// for that line — the committed field captures and the diagnosis tooling
    /// compare formats, and only the SOURCE moved.</para>
    /// </summary>
    public string GetSessionLogText()
        => string.Join(Environment.NewLine,
            _feed.SessionEntries.Select(e => new ConsoleLine(e).ToString()));
}

/// <summary>One rendered console line (immutable — display only).</summary>
public sealed class ConsoleLine(ConsoleEntry entry)
{
    public string Timestamp { get; } = entry.Timestamp.ToString("HH:mm:ss.fff");
    public ConsoleEntryKind Kind { get; } = entry.Kind;
    public string Badge { get; } = entry.Kind switch
    {
        ConsoleEntryKind.Tx => "TX",
        ConsoleEntryKind.Rx => "RX",
        ConsoleEntryKind.Auto => "AUTO",
        ConsoleEntryKind.Error => "ERR",
        _ => "--",
    };
    public string Text { get; } = entry.Text;

    public override string ToString() => $"{Timestamp} {Badge,-4} {Text}";
}
