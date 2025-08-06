using System.Collections.Generic;
using NexNet.Messages;
using NexNet.Cache;
using NexNet.Transports;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using NexNet.Pipes;
using NexNet.Logging;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Internals.Pipelines.Threading;

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
    
    // NnP(DC4) = NexNetProtocol(Device Control Four)
    // [N] [n] [P] [(DC4)] [RESERVED 1] [RESERVED 2] [RESERVED 3] [Protocol Version]
    // ReSharper disable twice StaticMemberInGenericType
    private static readonly ReadOnlyMemory<byte> _protocolHeader = new byte[] { (byte)'N', (byte)'n', (byte)'P', (byte)'\u0014', 0, 0, 0, 1 };
    private static readonly uint ProtocolTag = BitConverter.ToUInt32(_protocolHeader.Slice(0, 4).Span);
    private const byte ProtocolVersion = 1;
    
    private ITransport _transportConnection;
    private PipeReader? _pipeInput;
    private PipeWriter? _pipeOutput;

    private readonly MutexSlim _writeMutex = new MutexSlim(int.MaxValue);
    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 8);
    private readonly byte[] _readBuffer = new byte[8];

    // mutable struct.  Don't set to readonly.
    private MessageHeader _recMessageHeader = new();
    private long _id;

    private readonly ConcurrentBag<InvocationTaskArguments> _invocationTaskArgumentsPool = new();

    private readonly SemaphoreSlim _invocationSemaphore;
    //private bool _isReconnected;
    private DisconnectReason _registeredDisconnectReason = DisconnectReason.None;

    private readonly TaskCompletionSource? _readyTaskCompletionSource;
    private readonly TaskCompletionSource<DisconnectReason>? _disconnectedTaskCompletionSource;
    private ConnectionState _state;
    private InternalState _internalState = InternalState.Unset;

    private readonly CancellationTokenSource _disconnectionCts;

    public Action<ConnectionState>? OnStateChanged;
    private readonly INexusClient? _client;

    /// <summary>
    /// State of the connection that 
    /// </summary>
    [Flags]
    private enum InternalState
    {
        Unset = 0,
        ProtocolConfirmed = 1 << 0,
        //VersionConfirmed = 1 << 1,
        InitialClientGreetingReceived = 1 << 2,
        InitialServerGreetingReceived = 1 << 3,
        InitialServerReconnectReceived = 1 << 4,
        ClientGreetingReconnectReceived = 1 << 5,
        NexusCompletedConnection = 1 << 6,
        ReconnectingInProgress = 1 << 7,
    }

    public NexusPipeManager PipeManager { get; }

    public long Id
    {
        get => _id;
        private set
        {
            _id = value;
            if (Logger != null)
                Logger.SessionDetails = value.ToString();

            PipeManager.SetSessionId(value);

        }
    }
    
    public SessionManager? SessionManager => _sessionManager;
    public SessionInvocationStateManager SessionInvocationStateManager { get; }
    public long LastReceived { get; private set; }

    public INexusLogger? Logger { get; }
    CacheManager INexusSession.CacheManager => CacheManager;

    public List<int> RegisteredGroups { get; } = new();
    
    public Lock RegisteredGroupsLock { get; } = new();

    public SessionCacheManager<TProxy> CacheManager => _cacheManager;
    public SessionStore SessionStore { get; }
    
    public NexusCollectionManager CollectionManager { get; }

    public Action? OnDisconnected { get; init; }

    public IIdentity? Identity { get; private set; }

    public Action? OnSent { get; set; }

    public ConnectionState State
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _state;
    }
    
    public ConfigBase Config { get; }
    public bool IsServer { get; }

    public DisconnectReason DisconnectReason { get; private set; } = DisconnectReason.None;

    public NexusSession(in NexusSessionConfigurations<TNexus, TProxy> configurations)
    {
        _state = configurations.ConnectionState;
        _id = configurations.Id;
        _pipeInput = configurations.Transport.Input;
        _pipeOutput = configurations.Transport.Output;
        _transportConnection = configurations.Transport;
        Config = _config = configurations.Configs;
        _readyTaskCompletionSource = configurations.ReadyTaskCompletionSource;
        _disconnectedTaskCompletionSource = configurations.DisconnectedTaskCompletionSource;
        _cacheManager = configurations.Cache;
        _sessionManager = configurations.SessionManager;
        IsServer = configurations.Client == null;
        _client = configurations.Client;
        _nexus = configurations.Nexus;
        _disconnectionCts = new CancellationTokenSource();
        CollectionManager = configurations.CollectionManager;
        _nexus.SessionContext = IsServer
            ? new ServerSessionContext<TProxy>(this, _sessionManager!)
            : new ClientSessionContext<TProxy>(this);

        Logger = _config.Logger?.CreateLogger("NexusSession", Id.ToString());
        
        if(configurations.ConnectionState == ConnectionState.Reconnecting)
            EnumUtilities<InternalState>.SetFlag(ref _internalState, InternalState.ReconnectingInProgress);
        
        PipeManager = _cacheManager.PipeManagerCache.Rent(this);
        PipeManager.Setup(this);

        SessionInvocationStateManager = new SessionInvocationStateManager(_cacheManager, _config.Logger);
        SessionStore = new SessionStore();
        _invocationSemaphore = new SemaphoreSlim(_config.MaxConcurrentConnectionInvocations,
            _config.MaxConcurrentConnectionInvocations);

        // Register the session if there is a manager.
        _sessionManager?.RegisterSession(this);

        _config.InternalOnSessionSetup?.Invoke(this);

        Logger?.LogInfo($"Created session {Id}");
    }

    public Task DisconnectAsync(DisconnectReason reason)
    {
        return DisconnectCore(reason, true).AsTask();
    }


    public bool DisconnectIfTimeout(long timeoutTicks)
    {
        if (_state != ConnectionState.Connected)
            return false;

        if (timeoutTicks > LastReceived)
        {
            Logger?.LogTrace($"Timed out session {Id}");
            DisconnectAsync(DisconnectReason.Timeout);
            return true;
        }

        return false;
    }

    public async ValueTask InitializeConnection(CancellationToken cancellationToken = default)
    {
        // Send the connection header prior to the greeting
        await SendRaw(_protocolHeader, cancellationToken).ConfigureAwait(false);
        
        _state = ConnectionState.Connected;
        OnStateChanged?.Invoke(State);
    }

    public async ValueTask StartAsClient()
    {
        var clientConfig = Unsafe.As<ClientConfig>(_config);

        var isReconnecting = _state == ConnectionState.Reconnecting;
        using IClientGreetingMessageBase greetingMessage = isReconnecting
            ? _cacheManager.Rent<ClientGreetingReconnectionMessage>()
            : _cacheManager.Rent<ClientGreetingMessage>();

        // Get the latest version of the 
        var latestVersion = TNexus.VersionHashTable.Keys.LastOrDefault();
        
        greetingMessage.Version = latestVersion;
        
        // If we are not versioning, use the default proxy hash. 
        greetingMessage.ServerNexusHash = latestVersion == null ? TProxy.MethodHash : TNexus.VersionHashTable.GetValueOrDefault(latestVersion, 0);
        greetingMessage.ClientNexusHash = TNexus.MethodHash;
        greetingMessage.AuthenticationToken = clientConfig.Authenticate?.Invoke() ?? Memory<byte>.Empty;
        
        await InitializeConnection().ConfigureAwait(false);
        
        // Start the reading loop on a task.
        _ = Task.Factory.StartNew(static state => Unsafe.As<NexusSession<TNexus, TProxy>>(state!).StartReadAsync(), 
            this, 
            TaskCreationOptions.DenyChildAttach);

        if (isReconnecting)
        {
            await SendMessage(Unsafe.As<ClientGreetingReconnectionMessage>(greetingMessage)).ConfigureAwait(false);
        }
        else
            await SendMessage(Unsafe.As<ClientGreetingMessage>(greetingMessage)).ConfigureAwait(false);
        
        Logger?.LogInfo("Client connected");
    }

    private async ValueTask DisconnectCore(DisconnectReason reason, bool sendDisconnect)
    {
        // If we are already disconnecting, don't do anything
        var state = Interlocked.Exchange(ref _state, ConnectionState.Disconnecting);

        if (state == ConnectionState.Disconnecting || state == ConnectionState.Disconnected)
            return;

        OnStateChanged?.Invoke(State);

        DisconnectReason = reason;

        _registeredDisconnectReason = reason;

        Logger?.LogInfo($"Session disconnected with reason: {reason}");
        
        if (sendDisconnect && !_config.InternalForceDisableSendingDisconnectSignal)
        {
            var delay = false;
            try
            {
                await SendHeaderCore((MessageType)reason, null,true).ConfigureAwait(false);
                delay = true;
            }
            catch (Exception e)
            {
                Logger?.LogInfo(e, "Error while sending disconnect message.");
            }

            if (delay && _config.DisconnectDelay > 0)
            {
                // Add a delay in here to ensure that the data has a chance to send on the wire before a full disconnection.
                try
                {
                    await Task.Delay(_config.DisconnectDelay).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        // This can not be stopped on some transports as they don't have an understanding about
        // shutting down of rending pipes separately from receiving pipes.
        // Cancel any pending reads.
        _pipeInput!.CancelPendingRead();
        
        // The CompleteAsync method does the exact same action, but introduces overhead into this call.
        // ReSharper disable once MethodHasAsyncOverload
        _pipeInput!.Complete();
        _pipeInput = null;

        if (_config.InternalNoLingerOnShutdown)
        {
            try
            {
                await _transportConnection.CloseAsync(false).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "Error while closing transport connection.");
            }
            
            return;
        }

        // Cancel all current invocations.
        SessionInvocationStateManager.CancelAll();

        // ReSharper disable once MethodHasAsyncOverload
        try
        {
            _pipeOutput!.Complete();
        }
        catch (ObjectDisposedException)
        {
            //noop
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while completing output pipe.");
        }

        _pipeOutput = null;
        try
        {
            await _transportConnection.CloseAsync(true).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while closing transport connection.");
        }
        
        _state = ConnectionState.Disconnected;

        await _disconnectionCts.CancelAsync().ConfigureAwait(false);
        OnStateChanged?.Invoke(State);

        // Cancel all pipes and return pipe manager to the cache.
        PipeManager.CancelAll();
        _cacheManager.PipeManagerCache.Return(PipeManager);

        _nexus.Disconnected(reason);
        OnDisconnected?.Invoke();

        _nexus.SessionContext.Reset();

        _sessionManager?.UnregisterSession(this);
        ((IDisposable)SessionStore).Dispose();
        _invocationTaskArgumentsPool.Clear();

        _disconnectedTaskCompletionSource?.TrySetResult(reason);

        Logger?.LogTrace("ReadyTaskCompletionSource fired in DisconnectCore");
        _readyTaskCompletionSource?.TrySetResult();
    }
    
    
    /// <summary>
    /// Method executed at the handshake timeout to verify the connection has been fully established.
    /// </summary>
    /// <param name="timeoutTask">Task which fired this method.</param>
    private async Task CheckHandshakeComplete(Task timeoutTask)
    {
        if (_state != ConnectionState.Connected)
            return;

        if ((_internalState & InternalState.ProtocolConfirmed) != 0)
            return;
        
        Logger?.LogDebug("Connection closed due to handshake timeout.");
            
        await DisconnectCore(DisconnectReason.Timeout, false).ConfigureAwait(false);
    }

    public override string ToString() => $"NexusSession [{Id}] IsServer:{IsServer}";
    
    private class InvocationTaskArguments
    {
        public InvocationMessage Message { get; set; } = null!;
        public NexusSession<TNexus, TProxy> Session { get; set; } = null!;
    }
}
