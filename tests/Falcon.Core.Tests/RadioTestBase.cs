using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>Shared setup: a Prc138Radio over the line-injecting transport
/// with inline (synchronous) marshalling.</summary>
public abstract class RadioTestBase : IDisposable
{
    protected readonly InjectingTransport Transport = new();
    protected readonly Prc138Radio Radio;

    protected RadioTestBase()
    {
        Radio = new Prc138Radio(Transport, new InlineContext());
    }

    protected void Connect() => Radio.Connect(new PortSettings { PortName = "TEST" });

    /// <summary>Verbatim BATTERY answer (R1 capture).</summary>
    protected void AnswerSentinel() => Transport.InjectLine("Battery Status FULL 31.4V");

    /// <summary>Connect and drive to Ready: first sentinel answer completes
    /// init; the second drains the redundancy sentinel the ritual queues.</summary>
    protected void ConnectReady()
    {
        Connect();
        AnswerSentinel();      // completes init (Ready), dispatches sentinel #2
        AnswerSentinel();      // drains sentinel #2
        Assert.Equal(ConnectionState.Ready, Radio.Connection);
        Assert.Equal(0, Radio.PendingPingCount);
        Transport.ClearSent();
    }

    public void Dispose() => Radio.Dispose();
}
