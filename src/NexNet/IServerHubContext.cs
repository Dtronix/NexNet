using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Invocation;

namespace NexNet;

public class IServerHubContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    public IProxyBase<TProxy> Clients { get; set; }


    private sealed class ClientProxy : IProxyBase<TProxy>
    {
        private readonly SessionCacheManager<TProxy> _cacheManager;
        private readonly ServerSessionContext<TProxy> _context;
        private readonly Stack<TProxy> _instancedProxies = new Stack<TProxy>();

        private TProxy? _all;
        public TProxy All
        {
            get => _all ??= _cacheManager.ProxyCache.Rent(_context.Session!, ProxyInvocationMode.All, null);
        }

        internal ClientProxy(SessionCacheManager<TProxy> cacheManager, ServerSessionContext<TProxy> context)
        {
            _cacheManager = cacheManager;
            _context = context;
        }


        public TProxy Client(long id)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session!,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Clients(long[] ids)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session!,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Group(string groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session!,
                ProxyInvocationMode.Groups,
                new[] { groupName });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TProxy Groups(string[] groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session!,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public void Reset()
        {
            if (_caller != null)
            {
                _cacheManager.ProxyCache.Return(_caller);
                _caller = null;
            }

            if (_all != null)
            {
                _cacheManager.ProxyCache.Return(_all);
                _all = null;
            }

            if (_others != null)
            {
                _cacheManager.ProxyCache.Return(_others);
                _others = null;
            }

            while (_instancedProxies.TryPop(out var proxy))
            {
                _cacheManager.ProxyCache.Return(proxy);
            }
        }
    }
}
