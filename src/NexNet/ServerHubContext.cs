using System.Collections.Generic;
using NexNet.Cache;
using NexNet.Invocation;

namespace NexNet;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TProxy"></typeparam>
public class ServerHubContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    public IProxyBase<TProxy> Clients { get; }

    public ServerHubContext(SessionCacheManager<TProxy> cacheManager)
    {
        Clients = new ClientProxy(cacheManager, this)
    }

    private sealed class ClientProxy : IProxyBase<TProxy>
    {
        private readonly SessionCacheManager<TProxy> _cacheManager;
        private readonly ServerHubContext<TProxy> _context;
        private readonly Stack<TProxy> _instancedProxies = new Stack<TProxy>();

        private TProxy? _all;
        public TProxy All
        {
            get => _all ??= _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager, 
                ProxyInvocationMode.All,
                null);
        }

        internal ClientProxy(SessionCacheManager<TProxy> cacheManager, ServerHubContext<TProxy> context)
        {
            _cacheManager = cacheManager;
            _context = context;
        }


        public TProxy Client(long id)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Clients(long[] ids)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Group(string groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                ProxyInvocationMode.Groups,
                new[] { groupName });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Groups(string[] groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
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
}
