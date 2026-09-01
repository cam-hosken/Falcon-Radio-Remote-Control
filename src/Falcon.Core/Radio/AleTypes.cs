namespace Falcon.Core.Radio;

public enum AleAddressKind { Self, Individual, Net }

/// <summary>One ALE address as reported by SLFAD/INDAD/NETAD read-back.</summary>
public sealed record AleAddress
{
    public required string Address { get; init; }
    public required int ChannelGroup { get; init; }
    /// <summary>Associated self address; null for self addresses.</summary>
    public string? AssociatedSelf { get; init; }
}

/// <summary>
/// One scan channel group as reported by <c>CHG &lt;g&gt;</c>
/// ("CHGROUP 01 CHANS 00 01 ", probe R7). THREE states, never conflated:
/// <list type="bullet">
/// <item><c>Channels == null</c> — never queried this session ("—").</item>
/// <item><c>Channels == []</c> — queried and CONFIRMED empty (the radio
/// answers an empty group with nothing at all, protocol.md).</item>
/// <item><c>Channels == [..]</c> — confirmed membership, in the RADIO's
/// order and un-deduplicated (store what was sent).</item>
/// </list>
/// </summary>
public sealed record AleChannelGroup(int Group, IReadOnlyList<int>? Channels);

/// <summary>
/// One row of a net's membership, as reported by the TARGETED
/// <c>NETAD &lt;name&gt;</c> read's indented continuation lines
/// (<c>     MEMBER 01  I2</c> — captured 2026-08-17,
/// bench/transcripts/phase1-ale-membership). <see cref="Number"/> is the
/// radio's own printed index (it COMPACTS after a deletion); the list order
/// is INSERTION order, which is what the mirror preserves.
/// </summary>
public sealed record AleNetMember(int Number, string Address);

/// <summary>Which scheduler a queued LQA row belongs to — the row's own
/// leading token in the bare <c>EXCH</c> listing
/// (<c>EXCHANGE</c> / <c>SOUND</c>).</summary>
public enum LqaScheduleKind { Exchange, Sound }

/// <summary>
/// One queued LQA schedule row from the bare <c>EXCH</c> listing (≡ bare
/// <c>SOU</c> — identical output). Captured 2026-08-17
/// (bench/transcripts/phase2b-schedules):
/// <c>EXCHANGE I1              INTERVAL 01:00 START TIME 22:34</c>.
/// Interval and start are stored VERBATIM (<c>hh:mm</c>) — the radio does not
/// validate intervals, so the mirror shows exactly what it printed.
/// </summary>
public sealed record LqaSchedule(
    LqaScheduleKind Kind, string Address, string Interval, string StartTime);

/// <summary>The outcome of ONE sentinel-scoped read operation: the id its
/// requester was handed at request time, and whether the closing sentinel
/// was answered (false = nothing was published; the prior state stands).
/// Ids complete exactly once, so matching is id EQUALITY.</summary>
public readonly record struct AleReadCompletion(long ReadId, bool Answered);

/// <summary>The last programming refusal line the radio sent, with a
/// MONOTONE session sequence so a consumer can tell "the same refusal I
/// already saw" from "a new one" (the sequence is what
/// <c>AleProgrammingGate</c> brackets an operation with). Sequence 0 with a
/// null line = no refusal this session.</summary>
public readonly record struct AleProgrammingRefusal(long Sequence, string? Line);

/// <summary>A stored AMD (TXMSG) slot as reported by the TXMSG listing.</summary>
public sealed record AmdMessage
{
    public required int Slot { get; init; }
    public required string Text { get; init; }
}

/// <summary>A RECEIVED AMD as the radio announces it — CAPTURED 2026-08-24
/// (field-ale-first-contact-20260824-2144.txt, the Stage 9 two-station
/// session): <c>RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME:
/// 22:06</c> with the message text on the NEXT line. The mirror UPSERTS
/// BY SLOT (slot order = newest first — the radio stores newest at 00 and
/// shifts down); the shift an arrival implies for OLDER slots is not
/// narrated on the wire, so those rows go stale until a re-listing
/// corrects them (the Refresh's clear-then-relist).</summary>
public sealed record RxAmdMessage
{
    public required int Slot { get; init; }
    public required string From { get; init; }
    public required string Date { get; init; }
    public required string Time { get; init; }
    public required string Text { get; init; }
}

/// <summary>What kind of over-the-air event <see cref="AleHeard"/> records.</summary>
public enum AleHeardKind { Sounding, Response }

/// <summary>ONE station-heard event (field capture #2, 2026-08-24): a
/// `SOUND FROM:` (another station's sounding) or a `RESP FROM:` (the
/// partner's answer in a live exchange). Core keeps only the LATEST as an
/// event carrier — a NEW record instance per line, raised every time even
/// when the values repeat — and the LQA pane's Heard-stations frame does
/// the per-station aggregation (owner design 2026-08-24).</summary>
public sealed record AleHeard(AleHeardKind Kind, string Station, string Channel);

/// <summary>One row of a RANK report (stored LQA scores; passive read).</summary>
public sealed record LqaScore
{
    public required string Station { get; init; }
    public required string Channel { get; init; }
    public required string Score { get; init; }
    public required string MeasuredSnr { get; init; }
    public required string ReceivedSnr { get; init; }
}
