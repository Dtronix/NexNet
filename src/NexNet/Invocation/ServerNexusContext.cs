using System.Collections.Generic;
using System.Linq;
using NexNet.Pools;

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
        IServerSessionManager sessionManager,
        SessionPoolManager<TClientProxy> pool)
    {
        Clients = new ClientProxy(sessionManager, pool);

    }

    internal sealed class ClientProxy : IProxyBase<TClientProxy>
    {
        private readonly IServerSessionManager _sessionManager;
        private readonly SessionPoolManager<TClientProxy> _poolManager;
        private readonly Stack<TClientProxy> _instancedProxies = new Stack<TClientProxy>();

        private TClientProxy? _all;
        public TClientProxy All
        {
            get => _all ??= _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.All,
                null);
        }

        internal ClientProxy(
            IServerSessionManager sessionManager,
            SessionPoolManager<TClientProxy> poolManager)
        {
            _sessionManager = sessionManager;
            _poolManager = poolManager;
        }


        public TClientProxy Client(long id)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Client,
                new[] { id });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Clients(long[] ids)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Clients,
                ids);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy Group(string groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Groups,
                new[] { groupName });
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy GroupExceptCaller(string groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Groups,
                new[] { groupName });
            _instancedProxies.Push(proxy);

            return proxy;
        }


        public TClientProxy Groups(string[] groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public TClientProxy GroupsExceptCaller(string[] groupName)
        {
            var proxy = _poolManager.ProxyPool.Rent(
                null,
                _sessionManager,
                _poolManager,
                ProxyInvocationMode.Groups,
                groupName);
            _instancedProxies.Push(proxy);

            return proxy;
        }

        public IEnumerable<long> GetIds()
        {
            return _sessionManager.Sessions.LocalSessions.Select(s => s.Id);
        }

        public void Reset()
        {
            if (_all != null)
            {
                _poolManager.ProxyPool.Return(_all);
                _all = null;
            }

            while (_instancedProxies.TryPop(out var proxy))
            {
                _poolManager.ProxyPool.Return(proxy);
            }
        }
    }
}
