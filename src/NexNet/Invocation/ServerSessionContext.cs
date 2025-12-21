using System;
using System.Collections.Generic;
using System.Linq;
using NexNet.Internals;
using NexNet.Pools;

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

    internal ServerSessionContext(INexusSession<TClientProxy> session, IServerSessionManager sessionManager)
        : base(session, sessionManager)
    {
        _proxy = new ClientProxy(session.PoolManager, this);
        Groups = new GroupManager(session, sessionManager.Groups);
    }


    /// <inheritdoc />
    public override void Reset()
    {
        _proxy.Reset();
    }

    private sealed class ClientProxy : IProxyClients<TClientProxy>
    {
        private readonly SessionPoolManager<TClientProxy> _poolManager;
        private readonly ServerSessionContext<TClientProxy> _context;
        private readonly Stack<TClientProxy> _instancedProxies = new Stack<TClientProxy>();

        private TClientProxy? _caller;

        public TClientProxy Caller
        {
            get => _caller ??= _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.Caller,
                null);
        }

        private TClientProxy? _all;
        public TClientProxy All
        {
            get => _all ??= _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.All,
                null);
        }

        private TClientProxy? _others;
        public TClientProxy Others
        {
            get => _others ??= _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.Others,
                null);
        }


        internal ClientProxy(
            SessionPoolManager<TClientProxy> poolManager,
            ServerSessionContext<TClientProxy> context)
        {
            _poolManager = poolManager;
            _context = context;
        }


        public TClientProxy Client(long id)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Clients(long[] ids)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Group(string groupName)
        {
            return Groups(new[] { groupName });
        }

        public TClientProxy GroupExceptCaller(string groupName)
        {
            return GroupsExceptCaller(new[] { groupName });
        }

        public TClientProxy Groups(string[] groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy GroupsExceptCaller(string[] groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                _context.Session,
                _context.SessionManager,
                _context.Session.PoolManager,
                ProxyInvocationMode.GroupsExceptCaller,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public IEnumerable<long> GetIds()
        {
            return _context.SessionManager?.Sessions.LocalSessions.Select(s => s.Id) ?? Array.Empty<long>();
        }

        public void Reset()
        {
            if (_caller != null)
            {
                _poolManager.ProxyPool.Return(_caller);
                _caller = null;
            }

            if (_all != null)
            {
                _poolManager.ProxyPool.Return(_all);
                _all = null;
            }

            if (_others != null)
            {
                _poolManager.ProxyPool.Return(_others);
                _others = null;
            }

            while (_instancedProxies.TryPop(out var proxy))
            {
                _poolManager.ProxyPool.Return(proxy);
            }
        }
    }
}
