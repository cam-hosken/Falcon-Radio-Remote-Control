using System.Globalization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Modes;

/// <summary>
/// HOP operations: net display/select, sync, time-of-day, and the net
/// PROGRAMMING builders (Phase R). Net programming is "backend in, GUI out"
/// (plan-gui-rejigger.md round 4, E3 — the 2026-08-02 select-only decision
/// now applies to the UI only): the builders exist, whitelisted, but the
/// app layer must never call them (GuiOutScopeGuardTests source scan).
/// Wire syntax per protocol.md's confirmed HOP programming table.
///
/// <para><b>AMENDED — scope amendment X9</b> (plan-ui-tweaks-round11.md §7,
/// owner ruling R11): the WB exclusion-band family gets a GUI this round, so
/// EXACTLY THREE builders left the GUI-out list — the per-band SET, the bulk
/// QUERY (now sentinel-scoped, below) and the per-band DELETE.
/// <c>DeleteAllExcludeBands</c> stays GUARDED (a whole-table wipe with no
/// screen asking for it) and so does <c>GenerateHopset</c>.</para>
/// </summary>
public sealed class HopController
{
    private readonly Prc138Radio _radio;
    internal HopController(Prc138Radio radio) => _radio = radio;

    /// <summary>Net table: all nets (DIS) or one net (DIS n).</summary>
    public void QueryAllNets() => _radio.Send("DIS");

    public void QueryNet(int net)
    {
        ValidateNet(net);
        _radio.Send("DIS", net.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Select the active net. The radio generates the hopset and
    /// TUNES THE COUPLER (transmits); sync on the previous net is lost; and
    /// the SSB current channel silently changes (probe R9b — handled by the
    /// trigger table).</summary>
    public void SelectNet(int net)
    {
        ValidateNet(net);
        _radio.Send("NET", net.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Send a sync request (TRANSMITS), or answer a received one.
    /// A silent no-op when the current net has no hopset (probe R9).</summary>
    public void Sync() => _radio.Send("SY");

    /// <summary>Set the radio's time of day AND date from the device clock.
    /// TIME alone does not touch the date (probe R9: the radio's calendar
    /// read 1992 with current TOD) — DAT/DAY are sent too.</summary>
    public void SetTimeOfDay(DateTime now)
    {
        _radio.Send("TIME", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        // Zero-padded mm/dd/yy: HELP documents "(mm/dd/yy)" and the radio's
        // own echo is zero-padded ("DATE 01/27/92"). Never bench-exercised
        // as a SET — flagged for the Stage 2 live check (audit F3).
        _radio.Send("DAT", now.ToString("MM/dd/yy", CultureInfo.InvariantCulture));
        _radio.Send("DAY", now.DayOfWeek.ToString().ToUpperInvariant());
    }

    // ====================================================================
    // Net programming — backend in, GUI OUT (plan round 4, E3). Wrong
    // frequency formats are SILENTLY IGNORED by the radio (protocol.md), so
    // client-side validation is the only defense; where the radio DOES
    // report (NETID echo, List_Invalid), the answer is the read-back.
    // ====================================================================

    /// <summary>Program a net ID (NETID &lt;net&gt; &lt;8-digit id&gt; —
    /// echoes "NETID    00  12345678").</summary>
    public void SetNetId(int net, string netId)
    {
        ValidateNet(net);
        if (netId is null || netId.Length != 8 || !netId.All(char.IsAsciiDigit))
            throw new ArgumentException("Net ID is exactly 8 digits.", nameof(netId));
        _radio.Send("NETID", net.ToString(CultureInfo.InvariantCulture), netId);
    }

    /// <summary>Set a net's hop type (HOPTYPE &lt;net&gt; NB|WB|LIST) —
    /// must be set BEFORE HOPSET/HOPLIST (protocol.md).</summary>
    public void SetHopType(int net, HopType type)
    {
        ValidateNet(net);
        _radio.Send("HOPTYPE", net.ToString(CultureInfo.InvariantCulture), type.ToWire());
    }

    /// <summary>Narrowband hopset (HOPSET &lt;net&gt; &lt;center&gt;).
    /// Generation only happens when the net is CURRENT (probe R9).</summary>
    public void SetNarrowbandHopset(int net, string centerKHz)
    {
        ValidateNet(net);
        _radio.Send("HOPSET", net.ToString(CultureInfo.InvariantCulture), ValidateHopFrequency(centerKHz));
    }

    /// <summary>Wideband hopset (HOPSET &lt;net&gt; &lt;low&gt; &lt;high&gt;).</summary>
    public void SetWidebandHopset(int net, string lowKHz, string highKHz)
    {
        ValidateNet(net);
        _radio.Send("HOPSET", net.ToString(CultureInfo.InvariantCulture),
            ValidateHopFrequency(lowKHz), ValidateHopFrequency(highKHz));
    }

    /// <summary>Delete a hopset (HOPSET &lt;net&gt; DEL). <b>Wipes the
    /// ENTIRE net record</b> — NETID reverts to unprogrammed too (probe
    /// R9b), not just the frequencies.</summary>
    public void DeleteHopset(int net)
    {
        ValidateNet(net);
        _radio.Send("HOPSET", net.ToString(CultureInfo.InvariantCulture), "DEL");
    }

    /// <summary>Add LIST-type hop frequencies (HOPLIST &lt;net&gt; ADD
    /// &lt;f&gt; …). Fewer than three total is refused radio-side with
    /// "List_Invalid" (bench 2026-08-01) — the radio reports it, so the
    /// builder deliberately does not second-guess the count.</summary>
    public void AddHopListFrequencies(int net, params string[] frequenciesKHz)
    {
        ValidateNet(net);
        if (frequenciesKHz is null || frequenciesKHz.Length == 0)
            throw new ArgumentException("At least one frequency is required.", nameof(frequenciesKHz));
        var parts = new List<string?> { "HOPLIST", net.ToString(CultureInfo.InvariantCulture), "ADD" };
        foreach (var f in frequenciesKHz) parts.Add(ValidateHopFrequency(f));
        _radio.Send([.. parts]);
    }

    /// <summary>Remove one LIST frequency (HOPLIST &lt;net&gt; DEL &lt;f&gt;).</summary>
    public void DeleteHopListFrequency(int net, string frequencyKHz)
    {
        ValidateNet(net);
        _radio.Send("HOPLIST", net.ToString(CultureInfo.InvariantCulture), "DEL", ValidateHopFrequency(frequencyKHz));
    }

    /// <summary>List a net's stored hop frequencies (HOPLIST &lt;net&gt; →
    /// "HOPLIST 03   11010  11015  11020", session-16; mirrored).</summary>
    public void QueryHopList(int net)
    {
        ValidateNet(net);
        _radio.Send("HOPLIST", net.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Set a WB exclusion band (EXC &lt;band&gt; &lt;lowHz&gt;
    /// &lt;highHz&gt; — 8-digit Hz IN, kHz echo OUT, session-16; triggers
    /// hopset regeneration).</summary>
    public void SetExcludeBand(int band, string lowHz, string highHz)
    {
        ValidateBand(band);
        _radio.Send("EXC", band.ToString(CultureInfo.InvariantCulture),
            ValidateHzFrequency(lowHz, nameof(lowHz)), ValidateHzFrequency(highHz, nameof(highHz)));
    }

    /// <summary>How long to wait for a sentinel-scoped HOP read to settle.</summary>
    public int ReadTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Read every exclusion band (<c>EXC</c> + ONE closing sentinel — round 11
    /// §8, the AleState read-store pattern).
    /// <para><b>The sentinel is load-bearing, not decoration.</b> An EMPTY
    /// exclusion table answers NOTHING AT ALL (captured 2026-08-17), which is
    /// byte-identical to a swallowed query. Bracketing the read makes the two
    /// distinguishable by construction: rows before an ANSWERED sentinel commit
    /// atomically, NO rows before an answered sentinel IS the read-empty state,
    /// and an unanswered sentinel publishes nothing at all.</para>
    /// <para>Returns the operation's read id, matching
    /// <see cref="HopState.LastExcludeRead"/> by equality; a request arriving
    /// while one is on the wire sends nothing and returns the pending
    /// operation's id.</para>
    /// </summary>
    public long QueryExcludeBands()
    {
        long readId = _radio.State.Hop.RequestExcludeRead(out bool dispatch);
        if (dispatch) DispatchExcludeRead(readId);
        return readId;
    }

    private void DispatchExcludeRead(long readId)
    {
        _radio.Send("EXC");
        _radio.Ping(answered => CompleteExcludeRead(readId, answered), ReadTimeoutMs);
    }

    private void CompleteExcludeRead(long readId, bool answered)
    {
        _radio.State.Hop.CompleteExcludeRead(readId, answered, out long nextReadId, out bool dispatchNext);
        if (dispatchNext) DispatchExcludeRead(nextReadId);
    }

    /// <summary>Delete one exclusion band (EXC &lt;band&gt; DEL — silent,
    /// session-16).</summary>
    public void DeleteExcludeBand(int band)
    {
        ValidateBand(band);
        _radio.Send("EXC", band.ToString(CultureInfo.InvariantCulture), "DEL");
    }

    /// <summary>Delete every exclusion band (EXC DEL — silent).</summary>
    public void DeleteAllExcludeBands() => _radio.Send("EXC", "DEL");

    /// <summary>Signal the transec module to generate the hopset (DOIT).
    /// Generation only happens on the CURRENT net (probe R9).</summary>
    public void GenerateHopset() => _radio.Send("DOIT");

    /// <summary>Hop frequencies are 5-digit kHz, last digit 0 or 5, range
    /// 01600-29995 — wrong formats are SILENTLY IGNORED by the radio
    /// (protocol.md), so this validation is the only defense.</summary>
    internal static string ValidateHopFrequency(string frequencyKHz)
    {
        var f = (frequencyKHz ?? "").Trim();
        if (f.Length != 5 || !f.All(char.IsAsciiDigit) || (f[4] != '0' && f[4] != '5'))
            throw new ArgumentException(
                "Hop frequency is 5-digit kHz with the last digit 0 or 5 (e.g. 11565).", nameof(frequencyKHz));
        int kHz = int.Parse(f, CultureInfo.InvariantCulture);
        if (kHz is < 1600 or > 29995)
            throw new ArgumentOutOfRangeException(nameof(frequencyKHz), "Hop frequency is 01600-29995 kHz.");
        return f;
    }

    private static string ValidateHzFrequency(string frequencyHz, string name)
    {
        var f = (frequencyHz ?? "").Trim();
        if (f.Length != 8 || !f.All(char.IsAsciiDigit))
            throw new ArgumentException("EXCLUDE frequencies are 8-digit Hz (e.g. 02000000).", name);
        return f;
    }

    private static void ValidateBand(int band)
    {
        if (band is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(band), "Exclusion band is 0-9.");
    }

    private static void ValidateNet(int net)
    {
        if (net is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(net), "Net number is 0-9.");
    }
}
