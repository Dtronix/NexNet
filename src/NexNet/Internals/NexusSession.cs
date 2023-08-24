﻿using System.Collections.Generic;
using NexNet.Messages;
using NexNet.Cache;
using NexNet.Transports;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Concurrent;
using Pipelines.Sockets.Unofficial.Threading;
using Pipelines.Sockets.Unofficial.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals.Pipes;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy> : INexusSession<TProxy>
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private record struct ProcessResult(
        SequencePosition Position,
        DisconnectReason DisconnectReason,
        bool IssueDisconnectMessage);

    private readonly TNexus _nexus;
    private readonly ConfigBase _config;
    private readonly SessionCacheManager<TProxy> _cacheManager;
    private readonly SessionManager? _sessionManager;

    private ITransport _transportConnection;
    private PipeReader? _pipeInput;
    private PipeWriter? _pipeOutput;

    private readonly MutexSlim _writeMutex = new MutexSlim(int.MaxValue);
    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 8);
    private readonly byte[] _readBuffer = new byte[8];

    // mutable struct.  Don't set to readonly.
    private MessageHeader _recMessageHeader = new MessageHeader();

    private readonly ConcurrentBag<InvocationTaskArguments> _invocationTaskArgumentsPool = new();

    private readonly SemaphoreSlim _invocationSemaphore;
    private bool _isReconnected;
    private DisconnectReason _registeredDisconnectReason = DisconnectReason.None;

    private readonly TaskCompletionSource? _readyTaskCompletionSource;
    private readonly TaskCompletionSource? _disconnectedTaskCompletionSource;
    private volatile int _state;

    public NexusPipeManager PipeManager { get; }

    public long Id { get; }

    public SessionManager? SessionManager => _sessionManager;
    public SessionInvocationStateManager SessionInvocationStateManager { get; }
    public long LastReceived { get; private set; }

    public INexusLogger? Logger { get; }
    CacheManager INexusSession.CacheManager => CacheManager;

    public List<int> RegisteredGroups { get; } = new List<int>();

    public SessionCacheManager<TProxy> CacheManager => _cacheManager;
    public SessionStore SessionStore { get; }

    public Action? OnDisconnected { get; set; }

    public IIdentity? Identity { get; private set; }

    public Action? OnSent { get; set; }

    public ConnectionState State
    {
        get => (ConnectionState)_state;
    }

    public ConfigBase Config { get; }
    public bool IsServer { get; }

    public NexusSession(in NexusSessionConfigurations<TNexus, TProxy> configurations)
    {
        _state = (int)ConnectionState.Connecting;
        Id = configurations.Id;
        _pipeInput = configurations.Transport.Input;
        _pipeOutput = configurations.Transport.Output;
        _transportConnection = configurations.Transport;
        Config = _config = configurations.Configs;
        _readyTaskCompletionSource = configurations.ReadyTaskCompletionSource;
        _disconnectedTaskCompletionSource = configurations.DisconnectedTaskCompletionSource;
        _cacheManager = configurations.Cache;
        _sessionManager = configurations.SessionManager;
        IsServer = configurations.IsServer;
        _nexus = configurations.Nexus;
        _nexus.SessionContext = configurations.IsServer
            ? new ServerSessionContext<TProxy>(this, _sessionManager!)
            : new ClientSessionContext<TProxy>(this);

        Logger = configurations.Configs.Logger?.CreateLogger<NexusSession<TNexus, TProxy>>();

        PipeManager = _cacheManager.PipeManagerCache.Rent(this);
        PipeManager.Setup(this);

        SessionInvocationStateManager = new SessionInvocationStateManager(configurations.Cache, _config.Logger);
        SessionStore = new SessionStore();
        _invocationSemaphore = new SemaphoreSlim(configurations.Configs.MaxConcurrentConnectionInvocations,
            configurations.Configs.MaxConcurrentConnectionInvocations);

        // Register the session if there is a manager.
        configurations.SessionManager?.RegisterSession(this);

        _config.InternalOnSessionSetup?.Invoke(this);
    }


    public Task DisconnectAsync(DisconnectReason reason)
    {
        return DisconnectCore(reason, true).AsTask();
    }


    public bool DisconnectIfTimeout(long timeoutTicks)
    {
        if (_state != (int)ConnectionState.Connected)
            return false;

        if (timeoutTicks > LastReceived)
        {
            Logger?.LogTrace($"Timed out session {Id}");
            DisconnectAsync(DisconnectReason.Timeout);
            return true;
        }

        return false;
    }

    public async ValueTask StartAsClient()
    {

        Logger?.LogTrace("StartAsClient()");
        var clientConfig = Unsafe.As<ClientConfig>(_config);
        using var greetingMessage = _cacheManager.Rent<ClientGreetingMessage>();

        greetingMessage.Version = 0;
        greetingMessage.ServerNexusMethodHash = TProxy.MethodHash;
        greetingMessage.ClientNexusMethodHash = TNexus.MethodHash;
        greetingMessage.AuthenticationToken = clientConfig.Authenticate?.Invoke() ?? Memory<byte>.Empty;

        _state = (int)ConnectionState.Connected;

        await SendMessage(greetingMessage).ConfigureAwait(false);

        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Factory.StartNew(StartReadAsync, TaskCreationOptions.LongRunning);

    }

    private async ValueTask<bool> TryReconnectAsClient()
    {
        Logger?.LogTrace("TryReconnectAsClient()");
        if (IsServer)
            return false;

        var clientConfig = Unsafe.As<ClientConfig>(_config);
        var clientHub = Unsafe.As<ClientNexusBase<TProxy>>(_nexus);

        if (clientConfig.ReconnectionPolicy == null)
            return false;

        _state = (int)ConnectionState.Reconnecting;

        // Notify the hub.
        await clientHub.Reconnecting().ConfigureAwait(false);
        var count = 0;

        while (true)
        {
            ITransport? transport = null;
            try
            {
                // Get the next delay or cancellation.
                var delay = clientConfig.ReconnectionPolicy.ReconnectDelay(count++);

                if (delay == null)
                    return false;

                await Task.Delay(delay.Value).ConfigureAwait(false);

                Logger?.LogTrace($"Reconnection attempt {count}");

                transport = await clientConfig.ConnectTransport(default).ConfigureAwait(false);
                _state = (int)ConnectionState.Connecting;

                _pipeInput = transport.Input;
                _pipeOutput = transport.Output;
                _transportConnection = transport;

                Logger?.LogTrace($"Reconnection succeeded.");

                _isReconnected = true;
                await StartAsClient().ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "Reconnection failed with exception.");
                if(transport != null)
                    await transport.CloseAsync(true).ConfigureAwait(false);
            }
        }

    }

    private async ValueTask DisconnectCore(DisconnectReason reason, bool sendDisconnect)
    {
        // If we are already disconnecting, don't do anything
        var state = Interlocked.Exchange(ref _state, (int)ConnectionState.Disconnecting);

        if (state == (int)ConnectionState.Disconnecting || state == (int)ConnectionState.Disconnected)
            return;

        if (reason == DisconnectReason.ProtocolError)
        {

        }

        _registeredDisconnectReason = reason;

        Logger?.LogTrace($"DisconnectCore({reason}, {sendDisconnect})");

        if (sendDisconnect && !_config.InternalForceDisableSendingDisconnectSignal)
        {
            await SendHeaderCore((MessageType)reason, false).ConfigureAwait(false);

            if (_config.DisconnectDelay > 0)
            {
                // Add a delay in here to ensure that the data has a chance to send on the wire before a full disconnection.
                await Task.Delay(_config.DisconnectDelay).ConfigureAwait(false);
            }
        }

        // This can not be stopped on some transports as they don't have an understanding about
        // shutting down of rending pipes separately from receiving pipes.
        // ReSharper disable once MethodHasAsyncOverload
        _pipeInput!.Complete();
        _pipeInput = null;

        if (_config.InternalNoLingerOnShutdown)
        {
            await _transportConnection.CloseAsync(false).ConfigureAwait(false);
            return;
        }
        else
        {
            // Cancel all current invocations.
            SessionInvocationStateManager.CancelAll();

            // ReSharper disable once MethodHasAsyncOverload
            try
            {
                _pipeOutput!.Complete();
            }
            catch
            {
                //noop
            }

            _pipeOutput = null;
            await _transportConnection.CloseAsync(true);
        }

        // If we match a limited type of disconnects, attempt to reconnect if we are the client
        if (IsServer == false
            && reason == DisconnectReason.SocketError
            || reason == DisconnectReason.Timeout
            || reason == DisconnectReason.ServerRestarting)
        {
            var clientConfig = Unsafe.As<ClientConfig>(_config);

            // If we have a reconnection policy and succeed in reconnecting, stop the disconnection process.
            if (clientConfig.ReconnectionPolicy != null
                && await TryReconnectAsClient().ConfigureAwait(false))
                return;
        }

        state = (int)ConnectionState.Disconnected;

        // Cancel all pipe manager pipes and return to the cache.
        PipeManager.CancelAll();
        _cacheManager.PipeManagerCache.Return(PipeManager);

        _nexus.Disconnected(reason);
        OnDisconnected?.Invoke();

        _nexus.SessionContext.Reset();

        _sessionManager?.UnregisterSession(this);
        ((IDisposable)SessionStore).Dispose();
        _invocationTaskArgumentsPool.Clear();

        _disconnectedTaskCompletionSource?.TrySetResult();

        Logger?.LogTrace("ReadyTaskCompletionSource fired in DisconnectCore");
        _readyTaskCompletionSource?.TrySetResult();
    }

    public override string ToString() => $"NexNetSession({Id}) IsServer:{IsServer}";

    private class InvocationTaskArguments
    {
        public InvocationMessage Message { get; set; } = null!;
        public NexusSession<TNexus, TProxy> Session { get; set; } = null!;
    }
}
