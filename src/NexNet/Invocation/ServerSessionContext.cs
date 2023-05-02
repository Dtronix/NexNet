using System.Collections.Generic;
using NexNet.Cache;
using NexNet.Internals;

namespace NexNet.Invocation;

public sealed class ServerSessionContext<TProxy> : SessionContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ClientProxy _proxy;
    public IProxyClients<TProxy> Clients => _proxy;

        internal ServerSessionContext(INexNetSession<TProxy> session)
        : base(session)
    {
        _proxy = new ClientProxy(session.CacheManager, this);
    }


    public void AddToGroup(string groupName)
    {
        Session!.SessionManager?.RegisterSessionGroup(groupName, Session);
    }

    public void AddToGroups(string[] groupNames)
    {
        Session!.SessionManager?.RegisterSessionGroup(groupNames, Session);
    }

    public void RemoveFromGroup(string groupName)
    {
        Session!.SessionManager?.UnregisterSessionGroup(groupName, Session);
    }

    internal override void Reset()
    {
        _proxy.Reset();
    }

    private sealed class ClientProxy : IProxyClients<TProxy>
    {
        private readonly SessionCacheManager<TProxy> _cacheManager;
        private readonly ServerSessionContext<TProxy> _context;
        private readonly Stack<TProxy> _instancedProxies = new Stack<TProxy>();

        private TProxy? _caller;
        public TProxy Caller
        {
            get => _caller ??= _cacheManager.ProxyCache.Rent(_context.Session!, ProxyInvocationMode.Caller, null);
        }

        private TProxy? _all;
        public TProxy All
        {
            get => _all ??= _cacheManager.ProxyCache.Rent(_context.Session!, ProxyInvocationMode.All, null);
        }

        private TProxy? _others;
        public TProxy Others
        {
            get => _others ??= _cacheManager.ProxyCache.Rent(_context.Session!, ProxyInvocationMode.Others, null);
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
                ProxyInvocationMode.Clients,
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
