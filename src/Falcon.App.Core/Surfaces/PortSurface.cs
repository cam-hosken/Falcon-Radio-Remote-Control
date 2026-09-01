using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>Read-only radio-port slice: the radio's own PORT_R dump as
/// mirrored (plan §4.5 — the Settings page's "Radio port (read-only)"
/// section). Display only; the ONE port-changing intent (the guarded baud
/// wizard) goes through <see cref="Session.BaudChangeFlow"/>, not a surface
/// — changing the remote port ends the session, which makes it session
/// lifecycle, not radio-domain display state.</summary>
public sealed class PortSurface : RadioSurface
{
    public PortSurface(Prc138Radio radio)
        : base(radio, RadioProperty.PortConfig, RadioProperty.PortRemoteEcho) { }

    public Confirmed<string> Baud => Radio.State.PortBaud;
    public Confirmed<string> Bits => Radio.State.PortBits;
    public Confirmed<string> Parity => Radio.State.PortParity;
    public Confirmed<string> StopBits => Radio.State.PortStopBits;
    public Confirmed<string> XonXoff => Radio.State.PortXonXoff;
    public Confirmed<OnOff> Echo => Radio.State.PortRemoteEcho;
}
