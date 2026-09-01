namespace Falcon.Core.Transport;

// Own enums — no System.IO.Ports types in the seam (plan §2.1): the Android
// implementation has no System.IO.Ports, and the Core must stay platform-free.

public enum PortParity { None, Even, Odd }

public enum PortStopBits { One, Two }

/// <summary>Serial line settings. The PRC-138 remote port is 8N1, no flow
/// control, radio-configurable rate (app default 9600).</summary>
public sealed class PortSettings
{
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public PortParity Parity { get; set; } = PortParity.None;
    public PortStopBits StopBits { get; set; } = PortStopBits.One;
}
