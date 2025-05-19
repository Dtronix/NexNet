using System.Collections.Concurrent;
using NexNet.Cache;

namespace NexNet.Invocation;

/// <summary>
/// Provider for ServerNexusContext
/// </summary>
/// <typeparam name="TClientProxy">Client proxy.</typeparam>
public sealed class ServerNexusContextProvider<TClientProxy>
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly SessionManager _sessionManager;
    private readonly SessionCacheManager<TClientProxy> _cache;
    
    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    internal readonly ConcurrentBag<ServerNexusContext<TClientProxy>> ServerNexusContextCache = new();
    
    internal ServerNexusContextProvider(SessionManager sessionManager, SessionCacheManager<TClientProxy> cache)
    {
        _sessionManager = sessionManager;
        _cache = cache;
    }

    /// <summary>
    /// Rents a <see cref="ServerNexusContextOwner{TClientProxy}"/> for accessing of the <see cref="ServerNexusContext{TClientProxy}"/>.  Ensure to dispose upon completion.
    /// </summary>
    /// <returns>Owner managing the <see cref="ServerNexusContext{TClientProxy}"/></returns>
    /// <remarks>
    /// The purpose of this is to provide short-lived contexts for invocations then returns.
    /// Upon completion of usage, the owner resets the context and returns the context to the context pool.
    /// Do not maintain a long-lived reference to a single context.  If you need to invoke over long periods of time,
    /// get a reference to the <see cref="ServerNexusContextProvider{TClientProxy}"/> class for renting and returning. 
    /// </remarks>
    public ServerNexusContextOwner<TClientProxy> Rent()
    {
        if(!ServerNexusContextCache.TryTake(out var context))
            context = new ServerNexusContext<TClientProxy>(_sessionManager, _cache);
        
        return new ServerNexusContextOwner<TClientProxy>(this, context);
    }
}
