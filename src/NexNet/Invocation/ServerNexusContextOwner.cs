using System;
using NexNet.Collections;

namespace NexNet.Invocation;

/// <summary>
/// Owns the context and manages disposal.
/// </summary>
/// <typeparam name="TClientProxy">Client proxy.</typeparam>
/// <typeparam name="TServerNexus">Server nexus.</typeparam>
public sealed class ServerNexusContextOwner<TServerNexus, TClientProxy> : IDisposable
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly ServerNexusContextProvider<TServerNexus, TClientProxy> _contextProvider;
    private ServerNexusContext<TClientProxy> _context;
    private bool _disposed;
    private readonly TServerNexus _configNexus;

    internal ServerNexusContextOwner(
        Func<TServerNexus> nexusFactory,
        NexusCollectionManager collectionManager,
        ServerNexusContextProvider<TServerNexus, TClientProxy> contextProvider,
        ServerNexusContext<TClientProxy> context)
    {
        _contextProvider = contextProvider;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        
        _configNexus = nexusFactory.Invoke();
        // Add a special context used for only accessing collections.  Any other usage of methods throws.
        _configNexus.SessionContext = new ConfigurerSessionContext<TClientProxy>(collectionManager);
    }
    
    /// <summary>
    /// Nexus for accessing the collections. Any other method accessed on the nexus will throw.
    /// </summary>
    /// <exception cref="ObjectDisposedException"></exception>
    public TServerNexus Collections
    {
        get
        {
            if (_disposed) 
                throw new ObjectDisposedException(nameof(ServerNexusContextOwner<TServerNexus, TClientProxy>));
            return _configNexus;
        }
    }

    /// <summary>
    /// Gets the owned context. Throws if the owner has already been disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException"></exception>
    public ServerNexusContext<TClientProxy> Context
    {
        get
        {
            if (_disposed) 
                throw new ObjectDisposedException(nameof(ServerNexusContextOwner<TServerNexus, TClientProxy>));
            return _context;
        }
    }
    
    /// <summary>
    /// Proxy for client methods.
    /// </summary>
    /// <exception cref="ObjectDisposedException"></exception>
    public IProxyBase<TClientProxy> Proxy
    {
        get
        {
            if (_disposed) 
                throw new ObjectDisposedException(nameof(ServerNexusContextOwner<TServerNexus, TClientProxy>));
            return _context.Clients;
        }
    }
    
    /// <summary>
    /// Disposes the owned ServerNexusContext, but only once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ((ServerNexusContext<TClientProxy>.ClientProxy)_context.Clients).Reset();
        _contextProvider.ServerNexusContextCache.Add(_context);
    }
}
