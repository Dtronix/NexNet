using System;
using System.Collections.Generic;
using NexNet.Cache;
using NexNet.Invocation;

namespace NexNet;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TClientProxy"></typeparam>
public class ServerHubContext<TClientProxy> : IDisposable
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly INexNetServer<TClientProxy> _server;
    private readonly SessionManager _sessionManager;

    /// <summary>
    /// Client Proxy.
    /// </summary>
    public IProxyBase<TClientProxy> Clients { get; }


    internal ServerHubContext(INexNetServer<TClientProxy> server, SessionManager sessionManager, SessionCacheManager<TClientProxy> cache)
    {
        _server = server;
        _sessionManager = sessionManager;
        Clients = new ClientProxy(sessionManager, cache, this);

    }

    /// <summary>
    /// Gets all the connected client ids.
    /// </summary>
    /// <returns>Collection of connected client ids.</returns>
    public IEnumerable<long> GetClientIds()
    {
        return _sessionManager.Sessions.Keys;
    }

    /// <summary>
    /// Gets all the connected client ids.
    /// </summary>
    /// <returns>Collection of connected client ids.</returns>
    public IEnumerable<string> GetGroupNames()
    {
        return _sessionManager.Groups.Keys;
    }


    private sealed class ClientProxy : IProxyBase<TClientProxy>
    {
        private readonly SessionManager _sessionManager;
        private readonly SessionCacheManager<TClientProxy> _cacheManager;
        private readonly ServerHubContext<TClientProxy> _context;
        private readonly Stack<TClientProxy> _instancedProxies = new Stack<TClientProxy>();

        private TClientProxy? _all;
        public TClientProxy All
        {
            get => _all ??= _cacheManager.ProxyCache.Rent(
                null,
                _sessionManager,
                _cacheManager,
                ProxyInvocationMode.All,
                null);
        }

        internal ClientProxy(SessionManager sessionManager, SessionCacheManager<TClientProxy> cacheManager,
            ServerHubContext<TClientProxy> context)
        {
            _sessionManager = sessionManager;
            _cacheManager = cacheManager;
            _context = context;
        }


        public TClientProxy Client(long id)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                null,
                _sessionManager,
                _cacheManager,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Clients(long[] ids)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                null,
                _sessionManager,
                _cacheManager,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Group(string groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                null,
                _sessionManager,
                _cacheManager,
                ProxyInvocationMode.Groups,
                new[] { groupName });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Groups(string[] groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                null,
                _sessionManager,
                _cacheManager,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public void Reset()
        {
            if (_all != null)
            {
                _cacheManager.ProxyCache.Return(_all);
                _all = null;
            }

            while (_instancedProxies.TryPop(out var proxy))
            {
                _cacheManager.ProxyCache.Return(proxy);
            }
        }
    }

    /// <summary>
    /// Disposes the context for reuse at a later time.
    /// </summary>
    void IDisposable.Dispose()
    {
        ((ClientProxy)Clients).Reset();
        _server.ServerHubContextCache.Add(this);
    }
}
