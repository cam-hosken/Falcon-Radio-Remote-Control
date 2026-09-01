using Falcon.Core.Transport;

namespace Falcon.Core.Tests.Transport;

/// <summary>
/// Stage 7: the VID:PID[:Serial] identifier scheme the Android transport
/// resolves ports with (plan §2.2). The rules live in Falcon.Core precisely
/// so they are pinned here on net10.0 — the Android class itself only ever
/// runs on a device. Covers: parse, lowercase-hex format, serial-optional
/// matching, and the permission-masked-serial degradation both ways.
/// </summary>
public class UsbPortIdentifierTests
{
    // ---- Parse ------------------------------------------------------------

    [Fact]
    public void TryParse_VidPid_NoSerial()
    {
        Assert.True(UsbPortIdentifier.TryParse("0403:6001", out var id));
        Assert.Equal(0x0403, id.VendorId);
        Assert.Equal(0x6001, id.ProductId);
        Assert.Null(id.Serial);
    }

    [Fact]
    public void TryParse_VidPidSerial()
    {
        Assert.True(UsbPortIdentifier.TryParse("0403:6001:AB0KZ8MN", out var id));
        Assert.Equal(0x0403, id.VendorId);
        Assert.Equal(0x6001, id.ProductId);
        Assert.Equal("AB0KZ8MN", id.Serial);
    }

    [Fact]
    public void TryParse_UppercaseHex_Accepted()
    {
        Assert.True(UsbPortIdentifier.TryParse("04D8:00DF", out var id));
        Assert.Equal(0x04D8, id.VendorId);
        Assert.Equal(0x00DF, id.ProductId);
    }

    [Fact]
    public void TryParse_EmptySerialPart_MeansNoSerial()
    {
        // "0403:6001:" — a degraded save with a trailing colon.
        Assert.True(UsbPortIdentifier.TryParse("0403:6001:", out var id));
        Assert.Null(id.Serial);
    }

    [Fact]
    public void TryParse_SerialContainingColon_KeptVerbatim()
    {
        Assert.True(UsbPortIdentifier.TryParse("0403:6001:AB:CD", out var id));
        Assert.Equal("AB:CD", id.Serial);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0403")]                 // one part
    [InlineData("COM20")]                // a Windows port name is not an identifier
    [InlineData("zz03:6001")]            // non-hex VID
    [InlineData("0403:60zz")]            // non-hex PID
    [InlineData("10403:6001")]           // VID over 16 bits
    [InlineData("0403:16001")]           // PID over 16 bits
    [InlineData(":6001")]                // empty VID
    [InlineData("0403:")]                // empty PID
    public void TryParse_Rejects(string? text)
        => Assert.False(UsbPortIdentifier.TryParse(text, out _));

    // ---- Format -----------------------------------------------------------

    [Fact]
    public void ToString_LowercaseFourDigitHex()
        => Assert.Equal("04d8:00df", new UsbPortIdentifier(0x04D8, 0x00DF, null).ToString());

    [Fact]
    public void ToString_SerialAppendedVerbatim_CasePreserved()
        => Assert.Equal("0403:6001:AB0kz8MN",
            new UsbPortIdentifier(0x0403, 0x6001, "AB0kz8MN").ToString());

    [Theory]
    [InlineData("0403:6001")]
    [InlineData("0403:6001:AB0KZ8MN")]
    [InlineData("0403:6001:AB:CD")]
    public void RoundTrip_ToStringThenParse(string text)
    {
        Assert.True(UsbPortIdentifier.TryParse(text, out var id));
        Assert.True(UsbPortIdentifier.TryParse(id.ToString(), out var again));
        Assert.Equal(id, again);
    }

    [Fact]
    public void RoundTrip_UppercaseInput_NormalizesToLowercaseHex()
    {
        Assert.True(UsbPortIdentifier.TryParse("04D8:00DF:XYZ", out var id));
        Assert.Equal("04d8:00df:XYZ", id.ToString());
    }

    // ---- Matching ---------------------------------------------------------

    [Fact]
    public void Matches_VidPidMismatch_False()
    {
        var id = new UsbPortIdentifier(0x0403, 0x6001, null);
        Assert.False(id.Matches(0x0403, 0x6015, "S"));
        Assert.False(id.Matches(0x10C4, 0x6001, "S"));
    }

    [Fact]
    public void Matches_SavedWithoutSerial_MatchesAnySerial()
    {
        // A save made before the permission grant (serial was masked) still
        // resolves once permission reveals the serial.
        var id = new UsbPortIdentifier(0x0403, 0x6001, null);
        Assert.True(id.Matches(0x0403, 0x6001, "AB0KZ8MN"));
        Assert.True(id.Matches(0x0403, 0x6001, null));
    }

    [Fact]
    public void Matches_SavedWithSerial_ExactSerialMatch()
    {
        var id = new UsbPortIdentifier(0x0403, 0x6001, "AB0KZ8MN");
        Assert.True(id.Matches(0x0403, 0x6001, "AB0KZ8MN"));
        Assert.False(id.Matches(0x0403, 0x6001, "OTHER123"));
    }

    [Fact]
    public void Matches_SerialComparison_IsOrdinalCaseSensitive()
    {
        var id = new UsbPortIdentifier(0x0403, 0x6001, "ab0kz8mn");
        Assert.False(id.Matches(0x0403, 0x6001, "AB0KZ8MN"));
    }

    [Fact]
    public void Matches_DeviceSerialMasked_DegradesToVidPid()
    {
        // Permission masks the device serial (reads null): the saved
        // VID:PID:Serial must still find the device so the open path can
        // request permission — not report an attached device as missing.
        var id = new UsbPortIdentifier(0x0403, 0x6001, "AB0KZ8MN");
        Assert.True(id.Matches(0x0403, 0x6001, null));
        Assert.False(id.Matches(0x0403, 0x6015, null)); // VID:PID still decides
    }
}
