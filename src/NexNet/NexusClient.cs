﻿using System;
using System.Threading.Tasks;
using NexNet.Messages;
using System.Threading;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Invocation;

namespace NexNet;
/// <summary>
/// Main client class which facilitates the communication with a matching NexNet server.
/// </summary>
/// <typeparam name="TClientNexus">Nexus used by this client for incoming invocation handling.</typeparam>
/// <typeparam name="TServerProxy">Server proxy implementation used for all remote invocations.</typeparam>
public sealed class NexusClient<TClientNexus, TServerProxy> : INexusClient
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly Timer _pingTimer;
    private readonly ClientConfig _config;
    private readonly SessionCacheManager<TServerProxy> _cacheManager;
    private readonly TClientNexus _nexus;
    private NexusSession<TClientNexus, TServerProxy>? _session;
    private TaskCompletionSource? _disconnectedTaskCompletionSource;

    internal NexusSession<TClientNexus, TServerProxy>? Session => _session;

    /// <summary>
    /// Current state of the connection
    /// </summary>
    public ConnectionState State => _session?.State ?? ConnectionState.Disconnected;

    /// <summary>
    /// Proxy used for invoking remote methods on the server.
    /// </summary>
    public TServerProxy Proxy { get; private set; }

    /// <summary>
    /// Configurations used for this session.  Should not be changed once connection has been established.
    /// </summary>
    public ClientConfig Config => _config;

    /// <summary>
    /// Task which completes upon the completed connection and optional authentication of the client.
    /// Set to complete when the client has not started connecting yet or after disconnection.
    /// </summary>
    public Task ReadyTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Task which completes upon the disconnection of the client.
    /// </summary>
    public Task DisconnectedTask => _disconnectedTaskCompletionSource?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Creates a NexNet client for communication with a matching NexNet server.
    /// </summary>
    /// <param name="config">Configurations for this client.</param>
    /// <param name="nexus">Hub used for handling incoming invocations.</param>
    public NexusClient(ClientConfig config, TClientNexus nexus)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _cacheManager = new SessionCacheManager<TServerProxy>();

        Proxy = new TServerProxy() { CacheManager = _cacheManager };
        _nexus = nexus;
        _pingTimer = new Timer(PingTimer);
    }

    /// <summary>
    /// Connects to the server.
    /// </summary>
    /// <returns>Task for completion</returns>
    /// <exception cref="InvalidOperationException">Throws when the client is already connected to the server.</exception>
    public Task ConnectAsync()
    {
        return ConnectAsync(false);
    }

    /// <summary>
    /// Connects to the server and optionally waits for the ready signal.
    /// </summary>
    /// <param name="waitForReady">If true, the returned task will not complete until the client is ready to communicate with the server.</param>
    /// <returns>Task for completion</returns>
    /// <exception cref="InvalidOperationException">Throws when the client is already connected to the server.</exception>
    public async Task ConnectAsync(bool waitForReady)
    {
        if (_session != null)
            throw new InvalidOperationException("Client is already connected.");

        // Set the ready task completion source now and get the task since the ConnectTransport call below can/will await.
        // This TCS needs to run continuations asynchronously to avoid deadlocks on the receiving end.
        var readyTaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTaskCompletionSource = new TaskCompletionSource();
        ReadyTask = readyTaskCompletionSource.Task;
        _disconnectedTaskCompletionSource = disconnectedTaskCompletionSource;

        var client = await _config.ConnectTransport().ConfigureAwait(false);

        _config.InternalOnClientConnect?.Invoke();

        var config = new NexusSessionConfigurations<TClientNexus, TServerProxy>()
        {
            Configs = _config,
            Transport = client,
            Cache = _cacheManager,
            SessionManager = null,
            IsServer = false,
            Id = 0,
            Nexus = _nexus,
            ReadyTaskCompletionSource = readyTaskCompletionSource,
            DisconnectedTaskCompletionSource = disconnectedTaskCompletionSource
        };

        _session = new NexusSession<TClientNexus, TServerProxy>(config)
        {
            OnDisconnected = OnDisconnected,
            OnSent = OnSent
        };

        Proxy.Configure(_session, null, ProxyInvocationMode.Caller, null);

        await _session.StartAsClient().ConfigureAwait(false);

        if (waitForReady)
            await ReadyTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <returns>Task which completes upon disconnection.</returns>
    public Task DisconnectAsync()
    {
        if (_session == null)
            return Task.CompletedTask;

        return _session.DisconnectAsync(DisconnectReason.Graceful);
    }

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <param name="waitForDisconnect">
    /// If true, the returned task will not complete until the client
    /// is disconnected from the server.
    /// </param>
    /// <returns>Task which completes upon disconnection.</returns>
    public async Task DisconnectAsync(bool waitForDisconnect)
    {
        if (_session == null)
            return;

        await _session.DisconnectAsync(DisconnectReason.Graceful);

        if(waitForDisconnect == true
           && _disconnectedTaskCompletionSource != null)
            await _disconnectedTaskCompletionSource.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the client and disconnects if the client is connected to a server.
    /// Same as <see cref="DisconnectAsync()"/>.
    /// </summary>
    /// <returns></returns>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a pipe for sending and receiving byte arrays.
    /// </summary>
    /// <returns>Pipe to use.</returns>
    public INexusDuplexPipe CreatePipe()
    {
        var pipe = _session?.PipeManager.GetPipe();
        if (pipe == null)
            throw new InvalidOperationException("Client is not connected.");

        return pipe;
    }

    private void PingTimer(object? state)
    {
        var timeoutTicks = Environment.TickCount64 - _config.Timeout;

        // Check to see if we have timed out on receiving first.
        if (_session?.DisconnectIfTimeout(timeoutTicks) == true)
            return;

        _session?.SendHeader(MessageType.Ping);
    }

    private void OnDisconnected()
    {
        //_receiveLoopThread = null;
        _pingTimer.Change(-1, -1);
        _session = null;
    }

    private void OnSent()
    {
        // Can reset the ping interval since we just sent.
        _pingTimer.Change(_config.PingInterval, _config.PingInterval);
    }
}
