using System;
using System.Collections.Concurrent;
using NexNet.Collections;
using NexNet.Pools;

namespace NexNet.Invocation;

/// <summary>
/// Provider for ServerNexusContext
/// </summary>
/// <typeparam name="TClientProxy">Client proxy.</typeparam>
/// <typeparam name="TServerNexus">Server nexus.</typeparam>
public sealed class ServerNexusContextProvider<TServerNexus, TClientProxy> 
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly Func<TServerNexus> _nexusFactory;
    private readonly NexusCollectionManager _collectionManager;
    private readonly IServerSessionManager _sessionManager;
    private readonly SessionPoolManager<TClientProxy> _pool;

    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    internal readonly ConcurrentBag<ServerNexusContext<TClientProxy>> ServerNexusContextCache = new();

    internal ServerNexusContextProvider(
        Func<TServerNexus> nexusFactory,
        NexusCollectionManager collectionManager,
        IServerSessionManager sessionManager,
        SessionPoolManager<TClientProxy> pool)
    {
        _nexusFactory = nexusFactory;
        _collectionManager = collectionManager;
        _sessionManager = sessionManager;
        _pool = pool;
    }

    /// <summary>
    /// Rents a <see cref="ServerNexusContextOwner{TServerNexus, TClientProxy}"/> for accessing of the <see cref="ServerNexusContext{TClientProxy}"/>.  Ensure to dispose upon completion.
    /// </summary>
    /// <returns>Owner managing the <see cref="ServerNexusContext{TClientProxy}"/></returns>
    /// <remarks>
    /// The purpose of this is to provide short-lived contexts for invocations then returns.
    /// Upon completion of usage, the owner resets the context and returns the context to the context pool.
    /// Do not maintain a long-lived reference to a single context.  If you need to invoke over long periods of time,
    /// get a reference to the <see cref="ServerNexusContextProvider{TServerNexus, TClientProxy}"/> class for renting and returning. 
    /// </remarks>
    public ServerNexusContextOwner<TServerNexus, TClientProxy> Rent()
    {
        if(!ServerNexusContextCache.TryTake(out var context))
            context = new ServerNexusContext<TClientProxy>(_sessionManager, _pool);

        return new ServerNexusContextOwner<TServerNexus, TClientProxy>(_nexusFactory, _collectionManager, this, context);
    }
}
