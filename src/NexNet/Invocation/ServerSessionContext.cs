using System;
using System.Collections.Generic;
using NexNet.Cache;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Base context for server hubs to use.
/// </summary>
/// <typeparam name="TClientProxy">Proxy class used for invocation.</typeparam>
public sealed class ServerSessionContext<TClientProxy> : SessionContext<TClientProxy>
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ClientProxy _proxy;

    /// <summary>
    /// Get the proxy for different client's invocations.
    /// </summary>
    public IProxyClients<TClientProxy> Clients => _proxy;

    /// <summary>
    /// Provides the identity of this connection if there is one.
    /// </summary>
    public IIdentity? Identity { get; internal set; }

    /// <summary>
    /// Manages the groups for this session.
    /// </summary>
    public GroupManager Groups { get; }

    internal ServerSessionContext(INexusSession<TClientProxy> session, SessionManager sessionManager)
        : base(session, sessionManager)
    {
        _proxy = new ClientProxy(session.CacheManager, this);
        Groups = new GroupManager(session, sessionManager);
    }

    internal override void Reset()
    {
        _proxy.Reset();
    }

    private sealed class ClientProxy : IProxyClients<TClientProxy>
    {
        private readonly SessionCacheManager<TClientProxy> _cacheManager;
        private readonly ServerSessionContext<TClientProxy> _context;
        private readonly Stack<TClientProxy> _instancedProxies = new Stack<TClientProxy>();

        private TClientProxy? _caller;

        public TClientProxy Caller
        {
            get => _caller ??= _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.Caller,
                null);
        }

        private TClientProxy? _all;
        public TClientProxy All
        {
            get => _all ??= _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.All, 
                null);
        }

        private TClientProxy? _others;
        public TClientProxy Others
        {
            get => _others ??= _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.Others,
                null);
        }


        internal ClientProxy(
            SessionCacheManager<TClientProxy> cacheManager,
            ServerSessionContext<TClientProxy> context)
        {
            _cacheManager = cacheManager;
            _context = context;
        }


        public TClientProxy Client(long id)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Clients(long[] ids)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Group(string groupName)
        {
            return Groups(new []{ groupName });
        }

        public TClientProxy GroupExceptCaller(string groupName)
        {
            return Groups(new[] { groupName });
        }

        public TClientProxy Groups(string[] groupName)
        {
            var proxy = _cacheManager.ProxyCache.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.CacheManager,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy GroupsExceptCaller(string[] groupName)
        {
            return Groups(groupName);
        }

        public IEnumerable<long> GetIds()
        {
            return _context.SessionManager?.Sessions.Keys ?? Array.Empty<long>();
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
