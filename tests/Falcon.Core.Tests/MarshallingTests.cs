using System.Collections.Concurrent;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.Core.Tests;

/// <summary>
/// Q10: SynchronizationContext marshalling captured at construction — no
/// Core event ever raises on a transport thread when a context is supplied.
/// </summary>
public class MarshallingTests
{
    /// <summary>Context that runs every posted callback on ONE dedicated
    /// worker thread and records it, so tests can assert delivery thread.</summary>
    private sealed class WorkerContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = [];
        public Thread Worker { get; }

        public WorkerContext()
        {
            Worker = new Thread(() =>
            {
                foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
            })
            { IsBackground = true };
            Worker.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Drain()
        {
            using var done = new ManualResetEventSlim();
            _queue.Add((_ => done.Set(), null));
            Assert.True(done.Wait(5000), "worker context did not drain");
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public void AllPublicEvents_ArriveOnTheCapturedContext_NeverOnTheTransportThread()
    {
        using var ctx = new WorkerContext();
        var transport = new InjectingTransport();
        using var radio = new Prc138Radio(transport, ctx);

        var eventThreads = new ConcurrentBag<Thread>();
        radio.StateChanged += (_, _) => eventThreads.Add(Thread.CurrentThread);
        radio.MessageReceived += (_, _) => eventThreads.Add(Thread.CurrentThread);
        radio.LineSent += (_, _) => eventThreads.Add(Thread.CurrentThread);
        radio.ErrorOccurred += (_, _) => eventThreads.Add(Thread.CurrentThread);
        radio.CompensationApplied += (_, _) => eventThreads.Add(Thread.CurrentThread);

        radio.Connect(new PortSettings { PortName = "TEST" });

        // Lines injected from a separate "transport read" thread:
        var readThread = new Thread(() =>
        {
            transport.InjectLine("Battery Status FULL 31.4V");
            transport.InjectLine("Battery Status FULL 31.4V");
            transport.InjectLine("POWER hi ");
            transport.InjectLine("GIBBERISH 42");
            transport.InjectLine("SSB> ");
            transport.InjectLine("SQUELCH ON ");
            transport.InjectLine("MODE FM ");
            transport.InjectLine("MODE USB");        // fires the compensation path
        });
        readThread.Start();
        readThread.Join();
        ctx.Drain();

        Assert.NotEmpty(eventThreads);
        Assert.All(eventThreads, t => Assert.Same(ctx.Worker, t));
        Assert.DoesNotContain(readThread, eventThreads);
    }

    [Fact]
    public void PingCallbacks_AreMarshalledToo()
    {
        using var ctx = new WorkerContext();
        var transport = new InjectingTransport();
        using var radio = new Prc138Radio(transport, ctx);

        radio.Connect(new PortSettings { PortName = "TEST" });

        Thread? callbackThread = null;
        using var done = new ManualResetEventSlim();
        radio.Ping(_ => { callbackThread = Thread.CurrentThread; done.Set(); }, 0);

        var readThread = new Thread(() =>
        {
            transport.InjectLine("Battery Status FULL 31.4V");
            transport.InjectLine("Battery Status FULL 31.4V");
            transport.InjectLine("Battery Status FULL 31.4V");
        });
        readThread.Start();
        readThread.Join();

        Assert.True(done.Wait(5000));
        Assert.Same(ctx.Worker, callbackThread);
    }

    [Fact]
    public void WithoutAContext_EventsRunInline()
    {
        // Headless/bench usage: no context captured → synchronous delivery.
        var transport = new InjectingTransport();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var radio = new Prc138Radio(transport);
            radio.Connect(new PortSettings { PortName = "TEST" });

            Thread? eventThread = null;
            radio.MessageReceived += (_, _) => eventThread = Thread.CurrentThread;
            transport.InjectLine("POWER hi ");
            Assert.Same(Thread.CurrentThread, eventThread);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
