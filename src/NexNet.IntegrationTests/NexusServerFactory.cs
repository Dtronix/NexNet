using System.Collections.Concurrent;
using NexNet.Collections;
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

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ServerBase.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await Server.StartAsync();
    public Task StopAsync() => Server.StopAsync();
}

internal interface INexusServerFactory : IAsyncDisposable
{
    
    public INexusServer ServerBase { get; }
}
