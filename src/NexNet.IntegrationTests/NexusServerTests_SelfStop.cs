using NexNet.Collections;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;

namespace NexNet.IntegrationTests;

/// <summary>
/// A server that stops itself, and the accept loop of a stopped run that wakes up late, must not leave anything
/// spinning (S17).
/// </summary>
/// <remarks>
/// Diagnosed on Linux (WSL, 2 cores) with the S16 trigger (fast stop/start cycles) and S16's fix reverted: the process
/// sat at about 1.5 to 2 cores while the restarted server was Running, and a counter on the accept loop's
/// "no transport" path read thousands per run instead of about one per stop. The accept loop of a stopped run kept
/// calling the listener with its cancelled token once the server and the endpoint were Running again; a cancelled
/// accept returns null at once, so the loop never waited. S16 made the loop exit once its run's token is cancelled.
/// </remarks>
internal class NexusServerTests_SelfStop : BaseTests
{
    /// <summary>
    /// The accept of a stopped run returns only after the server was started again. Its loop must exit instead of
    /// accepting again with the cancelled token.
    /// </summary>
    [Test]
    public async Task StaleAcceptLoop_ReturnsAfterRestart_DoesNotSpin()
    {
        var config = new GatedListenerServerConfig();
        var server = CreateServer(config, null);

        await server.StartAsync().Timeout(1);
        var stale = config.Listeners.Single();
        await stale.Accepting.Timeout(1);

        await server.StopAsync().Timeout(1);
        await server.StartAsync().Timeout(1);
        var current = config.Listeners.Last();
        await current.Accepting.Timeout(1);

        // The stopped run's pending accept returns now, while the restarted server and its endpoint are Running.
        stale.Release();
        await Task.Delay(200);

        using (Assert.Multiple())
        {
            await Assert.That(stale.AcceptCalls).IsEqualTo(1).Because("The stopped run's loop must not accept again.");
            await Assert.That(current.AcceptCalls).IsEqualTo(1);
            await Assert.That(server.State).IsEqualTo(NexusServerState.Running);
        }

        await server.StopAsync().Timeout(1);
        current.Release();
        await Task.Delay(50);
        await Assert.That(current.AcceptCalls).IsEqualTo(1);
    }

    /// <summary>
    /// The trigger from S16: the server's only endpoint faults right after a client enabled a collection, so the server
    /// stops itself. The server must stop accepting, the client and its collection must see the disconnect, and using
    /// the collection afterwards must fail at once.
    /// </summary>
    [Test]
    public async Task SelfStop_AfterClientEnabledCollection_LeavesNothingRunning()
    {
        var tcpConfig = (TcpServerConfig)CreateServerConfig(Type.Tcp);
        var config = new FaultInjectingTcpServerConfig
        {
            EndPoint = tcpConfig.EndPoint,
            Logger = tcpConfig.Logger,
            TcpNoDelay = true,
        };
        var server = CreateServer(config, null);
        var (client, _) = CreateClient(CreateClientConfig(Type.Tcp));

        // Generous: the first connect of a cold test process can be slow on small CI machines.
        await server.StartAsync().Timeout(5);
        await client.ConnectAsync().Timeout(5);
        var list = client.Proxy.IntListBi;
        await Assert.That(await list.EnableAsync().Timeout(5)).IsTrue();
        await Assert.That(await list.AddAsync(1).Timeout(5)).IsTrue();

        var listener = config.Listener!;
        listener.Fault();

        await server.StoppedTask!.Timeout(5);
        await client.DisconnectedTask.Timeout(5);
        await list.DisabledTask.Timeout(5);

        var acceptCalls = listener.AcceptCalls;
        var enable = list.EnableAsync().AsTask();
        await Task.WhenAny(enable, Task.Delay(TimeSpan.FromSeconds(1)));
        await Task.Delay(200);

        using (Assert.Multiple())
        {
            await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
            await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);
            await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
            await Assert.That(enable.IsCompleted).IsTrue().Because("Enabling on a closed session must not hang.");
            await Assert.That(enable.IsCompletedSuccessfully && enable.Result).IsFalse();
            await Assert.That(listener.AcceptCalls).IsEqualTo(acceptCalls).Because("The stopped server must not accept again.");
        }

        _ = enable.Exception;
    }

    /// <summary>
    /// A listener whose accept waits until <see cref="Release"/>, ignoring cancellation and close, so it can return
    /// after the run that called it was stopped. With a cancelled token it returns null at once, like
    /// <see cref="SocketTransportListener"/>.
    /// </summary>
    private sealed class GatedListener : ITransportListener
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _accepting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acceptCalls;

        public int AcceptCalls => Volatile.Read(ref _acceptCalls);

        /// <summary>
        /// Completes when the first accept is called.
        /// </summary>
        public Task Accepting => _accepting.Task;

        public void Release() => _release.TrySetResult();

        public ValueTask CloseAsync(bool linger) => default;

        public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acceptCalls);
            _accepting.TrySetResult();

            if (cancellationToken.IsCancellationRequested)
                return null;

            await _release.Task.ConfigureAwait(false);
            return null;
        }
    }

    private sealed class GatedListenerServerConfig() : ServerConfig(ServerConnectionMode.Listener)
    {
        private readonly List<GatedListener> _listeners = new();

        /// <summary>
        /// One listener per start, in order.
        /// </summary>
        public IReadOnlyList<GatedListener> Listeners
        {
            get
            {
                lock (_listeners)
                    return _listeners.ToArray();
            }
        }

        protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
        {
            var listener = new GatedListener();
            lock (_listeners)
                _listeners.Add(listener);
            return new ValueTask<ITransportListener?>(listener);
        }
    }

    /// <summary>
    /// Wraps a real listener; after <see cref="Fault"/>, the pending and every later accept throw.
    /// </summary>
    private sealed class FaultInjectingListener(ITransportListener inner) : ITransportListener
    {
        private readonly TaskCompletionSource _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acceptCalls;

        public int AcceptCalls => Volatile.Read(ref _acceptCalls);

        public void Fault() => _fault.TrySetResult();

        public ValueTask CloseAsync(bool linger) => inner.CloseAsync(linger);

        public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acceptCalls);
            if (!_fault.Task.IsCompleted)
            {
                var accept = inner.AcceptTransportAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(accept, _fault.Task).ConfigureAwait(false) == accept)
                    return await accept.ConfigureAwait(false);

                // Ends when the server closes the inner listener.
                _ = accept.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            throw new IOException("Injected accept fault.");
        }
    }

    private sealed class FaultInjectingTcpServerConfig : TcpServerConfig
    {
        public FaultInjectingListener? Listener { get; private set; }

        protected override async ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
        {
            var inner = await base.OnCreateServerListener(cancellationToken).ConfigureAwait(false);
            return Listener = new FaultInjectingListener(inner!);
        }
    }
}
