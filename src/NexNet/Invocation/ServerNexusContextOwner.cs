using System;

namespace NexNet.Invocation;

/// <summary>
/// Owns the context and manages disposal.
/// </summary>
/// <typeparam name="TClientProxy">Client proxy.</typeparam>
public sealed class ServerNexusContextOwner<TClientProxy> : IDisposable
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ServerNexusContextProvider<TClientProxy> _contextProvider;
    private ServerNexusContext<TClientProxy> _context;
    private bool _disposed;

    internal ServerNexusContextOwner(
        ServerNexusContextProvider<TClientProxy> contextProvider,
        ServerNexusContext<TClientProxy> context)
    {
        _contextProvider = contextProvider;
        _context = context ?? throw new ArgumentNullException(nameof(context));
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
                throw new ObjectDisposedException(nameof(ServerNexusContextOwner<TClientProxy>));
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
                throw new ObjectDisposedException(nameof(ServerNexusContextOwner<TClientProxy>));
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
