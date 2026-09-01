using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Connection status for the spine (dot + port + phase) plus the app's
/// rate-limited error toast (plan §2.4: errors max 1 per 2 s with a
/// suppressed count, into a status line + Console — never modal).
/// State flows one-way: session events → these observable properties.
/// </summary>
public partial class RadioSessionViewModel : ObservableObject
{
    private static readonly TimeSpan ToastInterval = TimeSpan.FromSeconds(2);

    private readonly RadioSession _session;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastToastAt = DateTimeOffset.MinValue;
    private int _suppressed;

    [ObservableProperty] private SessionPhase phase;
    [ObservableProperty] private string phaseText = "Disconnected";
    [ObservableProperty] private string portDisplay = "no port";
    [ObservableProperty] private bool isReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string toastText = "";

    /// <summary>Is there a toast on screen? The dismiss control binds this, so
    /// that a ✕ never floats beside an empty status line (round 13 C2, backlog
    /// item 13).</summary>
    public bool HasToast => !string.IsNullOrEmpty(ToastText);

    public RadioSessionViewModel(RadioSession session, ConsoleFeed feed, TimeProvider time)
    {
        _session = session;
        _time = time;
        session.PhaseChanged += (_, _) => Refresh();
        // One error pipeline: everything the Console logs as an error also
        // feeds the toast (rate-limited here; the Console keeps every line).
        feed.EntryAdded += e => { if (e.Kind == ConsoleEntryKind.Error) ShowToast(e.Text); };
        Refresh();
    }

    private void Refresh()
    {
        Phase = _session.Phase;
        IsReady = Phase == SessionPhase.Ready;
        PhaseText = Phase switch
        {
            SessionPhase.Connecting => "Connecting…",
            SessionPhase.Ready => "Ready",
            SessionPhase.Failed => "Failed",
            SessionPhase.Reconnecting => "Reconnecting…",
            _ => "Disconnected",
        };
        // Stage 8 (deferred-ledger fix, Stage 3 cosmetic): after a user Close
        // the session keeps its last settings (so a later Connect can reuse
        // them), but the spine must not display a port it is not attached to.
        // Failed/Reconnecting keep showing the port — there it is the useful
        // fact ("which port failed").
        PortDisplay = _session.PortName is null || Phase == SessionPhase.Disconnected
            ? "no port"
            : $"{_session.PortName} {_session.BaudRate}";

        if (Phase is SessionPhase.Ready or SessionPhase.Disconnected)
        {
            // F1 (Stage 3 audit round 1): a stale error line must not survive
            // a successful (re)connect or a user Close — red text next to a
            // green Ready dot. The Console log keeps the permanent record;
            // the pending suppressed count is display state and clears with
            // the toast it would have annotated.
            ToastText = "";
            _suppressed = 0;
        }
    }

    /// <summary>
    /// Clear the toast the operator has read (round 13 C2, backlog item 13 —
    /// "put a red X on the right side of the error messages so they can be
    /// cleared").
    ///
    /// <para>The rate-limit CLOCK is wound back as well, and that is the part
    /// worth stating: the limiter exists so a burst of errors cannot flood the
    /// status line, but a dismissal is the operator saying "I have read this".
    /// If the clock were left alone, an error arriving in the second after a
    /// dismissal would be counted as suppressed and the operator would watch
    /// their own ✕ swallow the next failure — the exact opposite of what the
    /// control is for. Setting the last-toast instant one full interval into
    /// the past makes the next error show IMMEDIATELY, and leaves the limiter
    /// untouched for everything after it.</para>
    ///
    /// <para>The suppressed count goes with it, for the same reason
    /// <see cref="Refresh"/> clears it: a "(+N suppressed)" tail is an
    /// annotation ON a toast, and there is no longer a toast for it to
    /// annotate. The Console feed keeps every line — nothing is lost here that
    /// the permanent record does not still hold.</para>
    /// </summary>
    [RelayCommand]
    private void DismissToast()
    {
        ToastText = "";
        _suppressed = 0;
        _lastToastAt = _time.GetUtcNow() - ToastInterval;
    }

    private void ShowToast(string message)
    {
        var now = _time.GetUtcNow();
        if (now - _lastToastAt >= ToastInterval)
        {
            ToastText = _suppressed > 0 ? $"{message} (+{_suppressed} suppressed)" : message;
            _suppressed = 0;
            _lastToastAt = now;
        }
        else
        {
            _suppressed++;
        }
    }
}
