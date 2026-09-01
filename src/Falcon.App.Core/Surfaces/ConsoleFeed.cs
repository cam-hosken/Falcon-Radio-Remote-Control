using Falcon.App.Core.Session;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

public enum ConsoleEntryKind
{
    Tx,
    Rx,
    /// <summary>App-originated command the operator did not directly cause
    /// (trigger-table re-poll, FM-squelch cycle) — principle #4: nothing the
    /// app sends is ever invisible.</summary>
    Auto,
    Error,
    /// <summary>Session lifecycle note (phase transitions).</summary>
    Session,
}

public readonly record struct ConsoleEntry(DateTime Timestamp, ConsoleEntryKind Kind, string Text);

/// <summary>
/// The Console page's line source: every TX line (LineSent), every RX line
/// verbatim (MessageReceived), every compensation burst (CompensationApplied),
/// every surfaced error, and session phase notes — one merged, timestamped
/// feed. Events arrive already marshalled.
///
/// <para><b>D18 (plan-clone-write-structural.md §2, 2026-08-30) makes it the
/// Console's seam in BOTH directions</b> — see <see cref="SendRaw"/>. The
/// Console is the one screen whose subject is the wire itself, and this is
/// already its surface; a second one-method surface beside it would have to
/// hold the same radio handle for the same screen. Keeping the send here is
/// also what makes the app-layer scope guard's allow-list exactly ONE FILE
/// (<c>AppScopeGuardTests</c>).</para>
///
/// <para><b>D19 (same plan row, 2026-08-30) makes it the FULL-SESSION
/// RECORD</b> — see <see cref="SessionEntries"/>. The log belongs here and
/// not in the view model because this is the point every line passes through
/// BEFORE the display decides what to do with it: pause, filter and the
/// 500-line display cap are all downstream of <see cref="Add"/>, so a line
/// the display never shows is still recorded.</para>
/// </summary>
public sealed class ConsoleFeed
{
    private readonly Prc138Radio _radio;

    public event Action<ConsoleEntry>? EntryAdded;

    // ---- D19: the FULL-SESSION log -----------------------------------------

    /// <summary>
    /// D19 (plan-clone-write-structural.md §2, 2026-08-30) — THE EVIDENCE
    /// INSTRUMENT'S CAP. The 2026-08-30 live gate failed and could not be
    /// diagnosed: the console export drew from the same 500-line rolling
    /// buffer as the DISPLAY (<c>ConsoleViewModel.MaxEntries</c>), and the
    /// 11:08 failing write's root window had already scrolled out of it by the
    /// time the operator pressed "Store file…". The display cap is a READING
    /// budget and stays at 500; this is a RECORDING budget and is sized so
    /// that no clone campaign can outrun it.
    ///
    /// <para><b>Memory, honestly.</b> A full log is ~10 MB at the line lengths
    /// this radio actually produces (30–40 characters) and ~25 MB worst case
    /// at ~100 characters — UTF-16 text plus string and entry overhead.
    /// Acceptable on both the Windows desktop build and the bench phone, and
    /// only ever reached by a session that really has produced 100k lines: a
    /// clone campaign produces thousands, not hundreds of thousands.</para>
    /// </summary>
    public const int MaxSessionEntries = 100_000;

    /// <summary>The session log's rolling cap. Test hook (the
    /// <c>CloneService</c> timeout idiom): a pin can prove the FRONT-drop with
    /// a handful of lines instead of allocating 100k.</summary>
    public int SessionCap { get; set; } = MaxSessionEntries;

    // A QUEUE, not a List: at the 100k cap a List's front-removal would
    // memmove the whole backing array on every single line. Enqueue/Dequeue
    // are amortized O(1), and Queue<T> enumerates oldest-first, which is the
    // order the export needs.
    private readonly Queue<ConsoleEntry> _session = new();

    /// <summary>
    /// D19: EVERY line this feed has raised in this session, oldest first,
    /// regardless of what the display did with it. Appended in
    /// <see cref="Add"/> BEFORE <see cref="EntryAdded"/> is raised, so no
    /// subscriber — the display's pause hold, its filter, its own 500-line
    /// trim, or a subscriber that throws — can keep a line out of it.
    ///
    /// <para><b>Lifetime: the process.</b> Deliberately the same as the
    /// display buffer's, which is never cleared either — this feed and the
    /// Console's view model are both DI singletons, so the log spans
    /// disconnects, reconnects and tab visits and ends only when the app
    /// does. D19 invents no new clearing point; a session log that emptied
    /// itself at a reconnect would lose exactly the reconnect that preceded a
    /// failure.</para>
    /// </summary>
    public IReadOnlyCollection<ConsoleEntry> SessionEntries => _session;

    public ConsoleFeed(Prc138Radio radio, RadioSession session)
    {
        _radio = radio;
        radio.LineSent += (_, e) => Add(ConsoleEntryKind.Tx, e.Line.Length == 0 ? "<CR>" : e.Line);
        radio.MessageReceived += (_, e) => Add(ConsoleEntryKind.Rx, e.Message);
        radio.CompensationApplied += (_, e) =>
            Add(ConsoleEntryKind.Auto, $"{string.Join(", ", e.Commands)} — {e.Reason}");
        radio.ErrorOccurred += (_, e) => Add(ConsoleEntryKind.Error, e.Message);
        session.SessionError += (_, e) => Add(ConsoleEntryKind.Error, e.Message);
        session.PhaseChanged += (_, _) => Add(ConsoleEntryKind.Session, $"session: {session.Phase}");
    }

    /// <summary>
    /// D18: the OPERATOR'S OWN LINE, straight to the Core's raw passthrough
    /// (<c>Prc138Radio.RawCommand</c> — built for exactly this and never
    /// surfaced until now). The ONE app-layer reference to that member; the
    /// scope guard's allow-list names this file and no other.
    ///
    /// <para><b>No echo of its own.</b> Nothing is written to the feed here:
    /// <c>RawCommand</c> → <c>SendLine</c> → the transport's write QUEUE, and
    /// <c>SendLine</c> raises <c>LineSent</c>, which the TX subscription above
    /// already turns into the console's TX line. An echo here would print the
    /// line twice, and would print it for a line the closed port silently
    /// dropped.</para>
    ///
    /// <para><b>No filtering</b> (D18, decided): the radio's own refusals and
    /// this visible log are the safety. Whitespace-only input is rejected by
    /// <c>RawCommand</c> itself; the view model trims and gates before it ever
    /// gets here.</para>
    /// </summary>
    public void SendRaw(string command) => _radio.RawCommand(command);

    private void Add(ConsoleEntryKind kind, string text)
    {
        var entry = new ConsoleEntry(DateTime.Now, kind, text);
        // D19: RECORD FIRST. The session log is written before any subscriber
        // runs, which is what makes it independent of the display's pause,
        // filter and cap — and of a subscriber that throws.
        _session.Enqueue(entry);
        // FRONT-DROP: the OLDEST line goes when the cap is reached. A log that
        // dropped the newest would throw away the failure it exists to record.
        while (_session.Count > SessionCap) _session.Dequeue();
        EntryAdded?.Invoke(entry);
    }
}
