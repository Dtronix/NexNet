using System.Collections.Generic;
using NexNet.Cache;

namespace NexNet.Invocation;

/// <summary>
/// Context for external hub management.
/// </summary>
/// <typeparam name="TClientProxy">Proxy class used for invocation.</typeparam>
public class ServerNexusContext<TClientProxy>
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Client Proxy.
    /// </summary>
    public IProxyBase<TClientProxy> Clients { get; }

    internal ServerNexusContext(
        SessionManager sessionManager,
        SessionCacheManager<TClientProxy> cache)
    {
        Clients = new ClientProxy(sessionManager, cache);

    }

    internal sealed class ClientProxy : IProxyBase<TClientProxy>
    {
        private readonly SessionManager _sessionManager;
        private readonly SessionCacheManager<TClientProxy> _cacheManager;
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

        internal ClientProxy(
            SessionManager sessionManager,
            SessionCacheManager<TClientProxy> cacheManager)
        {
            _sessionManager = sessionManager;
            _cacheManager = cacheManager;
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

        public TClientProxy GroupExceptCaller(string groupName)
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

        public TClientProxy GroupsExceptCaller(string[] groupName)
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

        public IEnumerable<long> GetIds()
        {
            return _sessionManager.Sessions.Keys;
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
}
