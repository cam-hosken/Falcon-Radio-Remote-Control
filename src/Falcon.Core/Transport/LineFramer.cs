using System.Text;

namespace Falcon.Core.Transport;

/// <summary>
/// Splits the radio's byte stream into response lines. Terminators are CR,
/// LF (the radio ends "RXONLY NO " with a bare LF — bench-confirmed framing
/// quirk, reconfirmed by probe R1), and '>' — but '>' terminates ONLY when
/// the buffered line is exactly a mode prompt ("SSB>", "ALE>", "HOP>").
/// Anywhere else (e.g. inside stored AMD message text) '>' is ordinary
/// payload. CR/LF are stripped; the prompt's '>' is kept as part of the
/// line. Partial data is buffered until the next feed.
///
/// <para><b>The buffer is BOUNDED</b> (round 14 Phase D, plan §4-D item 3).
/// A stream that never terminates a line — a wedged driver repeating bytes,
/// a binary burst from a mis-set port — used to grow <c>_pending</c> without
/// limit. Past <see cref="PendingCapBytes"/> the buffer is DROPPED WHOLE and
/// two counters move; nothing is emitted. See <see cref="EnforceCap"/> for
/// why a drop, and never a synthetic line, is the only honest answer.</para>
///
/// <para><b>Threading.</b> Unsynchronised by design: a framer is fed from its
/// transport's port read thread alone (plus <see cref="Reset"/> on open and
/// close, when no read is in flight) — see <c>SerialTransport._framer</c>.
/// The counters inherit that contract; they are diagnostics, not a control
/// channel.</para>
/// </summary>
public sealed class LineFramer
{
    private const byte Cr = 0x0D;
    private const byte Lf = 0x0A;
    private const byte GreaterThan = 0x3E;

    /// <summary>Hard ceiling on the UNTERMINATED buffer, in characters (the
    /// stream is single-byte ASCII, so characters are bytes). 64 KiB is far
    /// above any captured radio line — the longest answers in the transcripts
    /// are hundreds of bytes — so a healthy session can never reach it.
    ///
    /// <para>It is the largest buffer the framer HOLDS: a run of exactly this
    /// many characters is kept and still frames normally, and the drop fires
    /// one character later, when the buffer would EXCEED the cap. Pinned at
    /// both sides by <c>TheCapBoundary_HoldsExactlyTheCap_AndDropsOneByteLater</c>
    /// — the code and the published contract (plan §4-D, the architecture doc)
    /// have to agree on which side of 65,536 the drop lives.</para></summary>
    public const int PendingCapBytes = 64 * 1024;

    private readonly StringBuilder _pending = new();

    /// <summary>How far the prompt check has already proven the buffer to be
    /// leading whitespace. Resumes where the last pass ended and is reset
    /// whenever the buffer is consumed or dropped — see
    /// <see cref="PendingIsBarePrompt"/>.</summary>
    private int _scanFrom;

    /// <summary>How many times the buffer has been dropped for exceeding
    /// <see cref="PendingCapBytes"/>. Cumulative for the life of this framer
    /// (<see cref="Reset"/> does NOT clear it — a session boundary is not a
    /// reason to forget that bytes were lost).</summary>
    public long OverflowCount { get; private set; }

    /// <summary>Total characters discarded by those drops. Same lifetime rule
    /// as <see cref="OverflowCount"/>.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Feed received bytes; returns zero or more complete lines.</summary>
    public IReadOnlyList<string> Feed(byte[] data, int count)
    {
        var lines = new List<string>();

        for (int i = 0; i < count; i++)
        {
            byte b = data[i];
            switch (b)
            {
                case Cr:
                case Lf:
                    Emit(lines);
                    break;

                case GreaterThan:
                    _pending.Append('>');
                    if (PendingIsBarePrompt()) Emit(lines);
                    else EnforceCap();
                    break;

                default:
                    _pending.Append((char)b);
                    EnforceCap();
                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Is the buffer now exactly a bare mode prompt? Equivalent to the
    /// original <c>_pending.ToString().Trim() is "SSB&gt;" or …</c> — the '>'
    /// was just appended, so there is no trailing whitespace to trim and only
    /// the LEADING run matters.
    ///
    /// <para><b>Why the position is remembered</b> (plan §4-D item 3). The
    /// old form allocated and trimmed the WHOLE buffer on every single '>'
    /// byte, so a long line carrying many '>' characters — stored AMD text is
    /// exactly that — rescanned O(n) per byte. Whitespace never stops being
    /// whitespace, so the scan resumes at <see cref="_scanFrom"/>: it advances
    /// once through the leading blanks and then parks on the first real
    /// character for the rest of the line, making every later pass O(1).</para>
    /// </summary>
    private bool PendingIsBarePrompt()
    {
        while (_scanFrom < _pending.Length && char.IsWhiteSpace(_pending[_scanFrom]))
            _scanFrom++;

        if (_pending.Length - _scanFrom != 4) return false;
        return _pending.ToString(_scanFrom, 4) is "SSB>" or "ALE>" or "HOP>";
    }

    /// <summary>
    /// THE CAP. Past <see cref="PendingCapBytes"/> — strictly past: the cap
    /// itself is held — the buffered partial is dropped WHOLE and the counters
    /// move. Since this runs after every appended character, the dropped run
    /// is always the cap plus the one character that broke it.
    ///
    /// <para><b>Why a drop and not a synthetic line.</b> Emitting the
    /// oversized buffer as a line would push fabricated text into the parser
    /// — the same path real radio answers travel — and the app is
    /// radio-authoritative: a line that the radio never terminated is not a
    /// line. Truncating and keeping the tail would be the same lie in a
    /// smaller font. So the bytes go, and the only trace is a counter that no
    /// production path reads.</para>
    ///
    /// <para>The residue of a drop is a garbled NEXT line (whatever remains
    /// of the run, glued to the bytes that follow it) — accepted, and the
    /// reason the cap sits far above any real answer: by the time it fires,
    /// the stream is already not carrying radio lines.</para>
    /// </summary>
    private void EnforceCap()
    {
        if (_pending.Length <= PendingCapBytes) return;
        DroppedBytes += _pending.Length;
        OverflowCount++;
        _pending.Clear();
        _scanFrom = 0;
    }

    private void Emit(List<string> lines)
    {
        if (_pending.Length == 0) return;
        var line = _pending.ToString();
        _pending.Clear();
        _scanFrom = 0;
        if (line.Trim().Length > 0) lines.Add(line);
    }

    /// <summary>Drop the buffered partial (open/close). The overflow counters
    /// deliberately survive — see <see cref="OverflowCount"/>.</summary>
    public void Reset()
    {
        _pending.Clear();
        _scanFrom = 0;
    }
}
