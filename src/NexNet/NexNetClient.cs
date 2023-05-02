﻿using System;
using System.Threading.Tasks;
using NexNet.Messages;
using System.Threading;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Invocation;
using Pipelines.Sockets.Unofficial;

namespace NexNet;

public sealed class NexNetClient<TClientHub, TServerProxy> : IAsyncDisposable
    where TClientHub : ClientHubBase<TServerProxy>, IMethodInvoker<TServerProxy>, IInterfaceMethodHash
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInterfaceMethodHash, new()

{
    private readonly Timer _pingTimer;
    private readonly ClientConfig _config;
    private readonly SessionCacheManager<TServerProxy> _cacheManager;
    private readonly TClientHub _hub;
    private NexNetSession<TClientHub, TServerProxy>? _session;

    internal NexNetSession<TClientHub, TServerProxy>? Session => _session;

    public ConnectionState State { get; private set; }

    public TServerProxy Proxy { get; private set; }

    public ClientConfig Config => _config;

    public NexNetClient(ClientConfig config, TClientHub hub)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _cacheManager = new SessionCacheManager<TServerProxy>();
        
        Proxy = new TServerProxy() { CacheManager = _cacheManager };
        _hub = hub;
        _pingTimer = new Timer(PingTimer);
    }

    public async Task ConnectAsync()
    {
        if (_session != null)
            throw new InvalidOperationException("Client is already connected.");

        State = ConnectionState.Connecting;

        var client = await _config.ConnectTransport();

        _config.InternalOnClientConnect?.Invoke();

        var config = new NexNetSessionConfigurations<TClientHub, TServerProxy>()
        {
            Configs = _config,
            Transport = client,
            Cache = _cacheManager,
            SessionManager = null,
            IsServer = false,
            Id = 0,
            Hub = _hub
        };

        _session = new NexNetSession<TClientHub, TServerProxy>(config)
        {
            OnDisconnected = OnDisconnected,
            OnSent = OnSent
        };
        
        Proxy.Configure(_session, ProxyInvocationMode.Caller, null);

        await _session.StartAsClient();
    }


    public Task DisconnectAsync()
    {
        if (_session == null)
            return Task.CompletedTask;

        return _session.DisconnectAsync(DisconnectReason.DisconnectGraceful);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private void PingTimer(object? state)
    {
        _session?.SendHeader(MessageType.Ping);
    }

    private void OnDisconnected()
    {
        //_receiveLoopThread = null;
        _pingTimer.Change(-1, -1);
        _session = null;
        State = ConnectionState.Disconnected;
    }

    private void OnSent()
    {
        // Can reset the ping interval since we just sent.
        _pingTimer.Change(_config.PingInterval, _config.PingInterval);
    }
}
