using System.Globalization;
using System.Text.RegularExpressions;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>
/// One stored channel from a DI dump line, fields VERBATIM as the radio
/// printed them ("SL", "USB", "2.7", "NO") — the dump uses its own
/// abbreviations (AGC SL) whose full spellings are not all captured, so
/// nothing is re-mapped or prettified (never fake a readable value).
/// </summary>
public sealed record StoredChannel
{
    public required int Number { get; init; }
    public required string RxFrequency { get; init; }
    public required string TxFrequency { get; init; }
    public required string Mode { get; init; }
    public required string Agc { get; init; }
    public required string Bandwidth { get; init; }
    public required string RxOnly { get; init; }

    // "00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO"
    // (the raw CH-line payload mirrored by Falcon.Core — session-23 shape).
    private static readonly Regex DiLine = new(
        @"^(\d+)\s+RXFR\s+(\S+)\s+TXFR\s+(\S+)\s+MODE\s+(\S+)\s+AGC\s+(\S+)\s+BA\s+(\S+)\s+RXONLY\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static StoredChannel? TryParse(string rawLine)
    {
        var m = DiLine.Match(rawLine.Trim());
        if (!m.Success) return null;
        return new StoredChannel
        {
            Number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            RxFrequency = m.Groups[2].Value,
            TxFrequency = m.Groups[3].Value,
            Mode = m.Groups[4].Value,
            Agc = m.Groups[5].Value,
            Bandwidth = m.Groups[6].Value,
            RxOnly = m.Groups[7].Value,
        };
    }
}

/// <summary>Channel slice (Stage 4): the current operating channel (CHAN
/// lines) and the stored-channel list mirrored from DI dump lines, plus the
/// select and dump intents. Channel PROGRAMMING has no surface of its own —
/// there is no channel-write command; programming is the ordinary live
/// SSB-domain commands while on the channel.
///
/// <para>UI-tweaks round 4 (AK2) adds the two intents the channel EDITOR
/// needs and nothing more: <see cref="SelectForStore"/> (a BARE
/// <c>CH n</c> — the store excursion, whose read-back is the editor's own
/// <c>DI n n</c> rather than an <c>SH</c>) and <see cref="RequestChannel"/>
/// (a ONE-channel <c>DI n n</c> read). Both route to EXISTING builders; no
/// scope-guard change is involved — <c>CH</c> and <c>DI</c> were never
/// guarded.</para></summary>
public sealed class ChannelSurface : RadioSurface
{
    public ChannelSurface(Prc138Radio radio)
        : base(radio, RadioProperty.OperatingChannel, RadioProperty.ChannelList) { }

    public Confirmed<int> Current => Radio.State.OperatingChannel;

    /// <summary>Parsed stored channels from the last DI dump (unparseable
    /// lines are skipped — they surface verbatim on the Console anyway).</summary>
    public IReadOnlyList<StoredChannel> Channels
    {
        get
        {
            var list = new List<StoredChannel>();
            foreach (var line in Radio.State.ChannelList)
                if (StoredChannel.TryParse(line) is { } channel)
                    list.Add(channel);
            return list;
        }
    }

    /// <summary>Select a channel — CH nn followed by SH, one obvious short
    /// sequence (thin-client principle #2), both visible in the Console.
    /// The SH re-read is required for display truth: the Stage 4 live gate
    /// measured that CH nn answers ONLY "CHAN nn" — the channel-stored
    /// freq/MODE/BW/AGC change WITHOUT being reported (same mutation class
    /// as trigger row (b), but app-initiated, so the app re-reads instead
    /// of trusting stale confirmed values).</summary>
    public void Select(int channel)
    {
        Radio.Ssb.SelectChannel(channel);
        Radio.Show();
    }

    /// <summary>
    /// UI-tweaks round 4 (AK2) — the channel editor's STORE-SELECT: a BARE
    /// <c>CH n</c>, deliberately WITHOUT the <see cref="Select"/> re-read.
    /// The editor is about to write the stored six on that channel and then
    /// verify with its own <c>DI n n</c>, so an <c>SH</c> here would only
    /// read state that is about to change. The honest full re-read happens at
    /// the END of the sequence, when the editor restores the operator's
    /// channel through <see cref="Select"/> (CH + SH).
    /// <para>This is a WRITE in the sense that it moves the radio off the
    /// operator's channel — hence its own name, so a reader can never confuse
    /// it with the ordinary operate-side select.</para>
    /// </summary>
    public void SelectForStore(int channel) => Radio.Ssb.SelectChannel(channel);

    /// <summary>ONE channel's stored record (<c>DI n n</c>) — the editor's
    /// lazy per-row read and its post-Store verify. Purely a read.
    /// <para>ROUND 11 §8: the mirror is now KEYED — a targeted answer replaces
    /// that channel's row and keeps its siblings, so sequential targeted reads
    /// accumulate instead of overwriting one another. (The old note here said
    /// the opposite, because the Core builder used to clear before every
    /// dump.)</para></summary>
    public void RequestChannel(int channel) => Radio.Ssb.DisplayChannels(channel, channel);

    /// <summary>Full stored-channel dump (DI 0 99 — plan §4.2).</summary>
    public void RequestDump() => Radio.Ssb.DisplayChannels(0, 99);

    /// <summary>Forget every stored channel the radio has reported, sending
    /// NOTHING — the explicit clear a "Refresh" gesture needs now that reads
    /// accumulate (round 11 §8).</summary>
    public void ForgetReportedChannels() => Radio.Ssb.ForgetStoredChannels();
}
