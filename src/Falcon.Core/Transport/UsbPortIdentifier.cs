using System.Globalization;

namespace Falcon.Core.Transport;

// Provenance: the VID:PID[:Serial] identifier scheme is lifted from the
// owner's SendIt project (SendIt/Platforms/Android/AndroidUsbSerialPort.cs),
// extracted into a plain Core type so the parse/format/match rules are unit
// tested on net10.0 with no Android host (Stage 7). The Android transport
// is the only production consumer; nothing here touches platform types.

/// <summary>
/// Android USB serial port identifier: <c>VID:PID[:Serial]</c>, lowercase
/// 4-digit hex VID/PID, optional device serial number.
///
/// <para>Why not Android's own DeviceId: Android assigns a fresh integer
/// DeviceId on every replug, so a stored DeviceId breaks across
/// unplug/replug and app restarts. VID:PID(:Serial) is stable — the saved
/// identifier is re-resolved to a live device at open time.</para>
///
/// <para>Why the serial is optional: without USB permission Android masks a
/// device's SerialNumber (reads null/throws), so an identifier built before
/// the permission grant degrades to bare <c>VID:PID</c>. The matcher
/// (<see cref="Matches"/>) tolerates both directions of that degradation —
/// see the rules there.</para>
/// </summary>
public readonly record struct UsbPortIdentifier(int VendorId, int ProductId, string? Serial)
{
    /// <summary>Formats as <c>vvvv:pppp[:Serial]</c> — VID/PID lowercase
    /// 4-digit hex, serial verbatim (case preserved; serials are matched
    /// ordinally).</summary>
    public override string ToString()
    {
        string head = string.Create(CultureInfo.InvariantCulture, $"{VendorId:x4}:{ProductId:x4}");
        return string.IsNullOrEmpty(Serial) ? head : $"{head}:{Serial}";
    }

    /// <summary>
    /// Parses <c>VID:PID</c> or <c>VID:PID:Serial</c>. VID/PID are hex
    /// (any case), each must fit 16 bits. The serial part is everything
    /// after the second colon (verbatim — a serial containing ':' round
    /// trips); an empty serial part means "no serial".
    /// </summary>
    public static bool TryParse(string? text, out UsbPortIdentifier identifier)
    {
        identifier = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Limit 3: the third element keeps any further colons, so
        // ToString() output always parses back to the same identifier.
        var parts = text.Split(':', 3);
        if (parts.Length < 2) return false;

        if (!TryParseHexId(parts[0], out int vid)) return false;
        if (!TryParseHexId(parts[1], out int pid)) return false;

        string? serial = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
        identifier = new UsbPortIdentifier(vid, pid, serial);
        return true;
    }

    private static bool TryParseHexId(string part, out int value)
        => int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
           && value is >= 0 and <= 0xFFFF;

    /// <summary>
    /// True if a device with the given VID/PID/serial is the one this
    /// (saved) identifier names. Rules:
    /// <list type="bullet">
    ///   <item>VID and PID must match exactly.</item>
    ///   <item>Saved identifier has no serial → any serial matches (a save
    ///         made before the permission grant still resolves after it).</item>
    ///   <item>Device serial unreadable (<paramref name="deviceSerial"/>
    ///         null — permission masks it) → VID:PID alone decides. The
    ///         permission mask must not hide the device: the open path then
    ///         requests permission and retries, instead of reporting a
    ///         device that is plainly attached as "not connected".</item>
    ///   <item>Both serials present → ordinal comparison.</item>
    /// </list>
    /// </summary>
    public bool Matches(int vendorId, int productId, string? deviceSerial)
    {
        if (vendorId != VendorId || productId != ProductId) return false;
        if (Serial is null || deviceSerial is null) return true;
        return string.Equals(Serial, deviceSerial, StringComparison.Ordinal);
    }
}
