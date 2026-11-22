using System.Collections.Concurrent;
using NexNet.Collections;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet.IntegrationTests;

internal class NexusServerFactory<TServerNexus, TClientProxy> : INexusServerFactory 
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    public NexusServer<TServerNexus, TClientProxy> Server { get; }
    
    public INexusServer ServerBase => Server;
    
    public Task? StoppedTask => Server.StoppedTask;
    
    public Action<TServerNexus>? OnNexusCreated { get; set; }
    
    public ConcurrentQueue<TServerNexus> NexusCreatedQueue { get; } = new();

    public NexusServerFactory(ServerConfig config)
    {
        Server = new NexusServer<TServerNexus, TClientProxy>(config, () =>
        {
            var nexus = new TServerNexus();
            NexusCreatedQueue.Enqueue(nexus);
            OnNexusCreated?.Invoke(nexus);
            return nexus;
        });
    }

    private StoppedPortHold? _stoppedPortHold;

    /// <summary>Starts the server, on the ports <see cref="StopAsync"/> held until now.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ReleaseStoppedPorts();
        await ServerBase.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the server and holds its TCP or QUIC port until it starts again or the test ends
    /// (<see cref="BaseTests.HoldStoppedServerPortsAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The port could not be held: another process has it.</exception>
    public async Task StopAsync()
    {
        await Server.StopAsync().ConfigureAwait(false);
        ReleaseStoppedPorts();
        _stoppedPortHold = await BaseTests.HoldStoppedServerPortsAsync(Server).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ReleaseStoppedPorts() => Interlocked.Exchange(ref _stoppedPortHold, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        ReleaseStoppedPorts();
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}

internal interface INexusServerFactory : IAsyncDisposable
{
    
    public INexusServer ServerBase { get; }

    /// <summary>Releases the ports the factory holds for its stopped server, if any.</summary>
    void ReleaseStoppedPorts();
}
