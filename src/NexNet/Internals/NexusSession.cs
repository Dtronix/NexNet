using System.Collections.Generic;
using NexNet.Messages;
using NexNet.Pools;
using NexNet.Transports;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NexNet.Pipes;
using NexNet.Logging;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Internals.Threading;
using NexNet.RateLimiting;
using NexNet.Diagnostics;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy> : INexusSession<TProxy>, LargeMessageReceiver.IOwner, IMeteredSession
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private record struct ProcessResult(
        SequencePosition Position,
        DisconnectReason DisconnectReason,
        bool IssueDisconnectMessage);

    private readonly TNexus _nexus;
    private readonly ConfigBase _config;
    private readonly SessionPoolManager<TProxy> _poolManager;
    private readonly IServerSessionManager? _sessionManager;
    
    // NnP(DC4) = NexNetProtocol(Device Control Four)
    // [N] [n] [P] [(DC4)] [RESERVED 1] [RESERVED 2] [RESERVED 3] [Protocol Version]
    private const byte ProtocolVersion = 1;
    // Magic "NnP" + DC4, three reserved bytes (must be zero), protocol version.
    // ReSharper disable twice StaticMemberInGenericType
    private static readonly ReadOnlyMemory<byte> _protocolHeader =
        new byte[] { (byte)'N', (byte)'n', (byte)'P', 0x14, 0, 0, 0, ProtocolVersion };
    
    private ITransport _transportConnection;
    private PipeReader? _pipeInput;
    private PipeWriter? _pipeOutput;

    private readonly MutexSlim _writeMutex = new MutexSlim(int.MaxValue);
    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 8);
    private readonly byte[] _readBuffer = new byte[8];

    // mutable struct.  Don't set to readonly.
    private MessageHeader _recMessageHeader = new();
    private long _id;

    private readonly ConcurrentBag<InvocationWorkItem> _invocationWorkItemPool = new();

    // Context the invocations run in; captured when the read loop starts. Null if flow was suppressed.
    private ExecutionContext? _invocationExecutionContext;

    // Invocations received and not yet running. The read loop only enqueues; up to
    // MaxConcurrentConnectionInvocations run at once, each holding a slot until it finishes or registers its pipe.
    private readonly ConcurrentQueue<InvocationMessage> _invocationQueue = new();
    private int _freeInvocationSlots;

    private readonly SessionControlSender _controlSender;
    //private bool _isReconnected;
    private DisconnectReason _registeredDisconnectReason = DisconnectReason.None;

    private readonly TaskCompletionSource? _readyTaskCompletionSource;
    private readonly TaskCompletionSource<DisconnectReason>? _disconnectedTaskCompletionSource;
    private ConnectionState _state;
    private InternalState _internalState = InternalState.Unset;

    private readonly CancellationTokenSource _disconnectionCts;

    public Action<ConnectionState>? OnStateChanged;
    private readonly INexusClient? _client;
    
    private int _versionHash = 0;

    // Security limits
    // Largest frame body accepted before the connection completes. A greeting is well below this; the length is
    // checked as soon as it is read, before the body is buffered.
    internal const int MaxPreAuthBodyLength = 16 * 1024;
    private const int MaxVersionStringLength = 16; // Max length for version strings
    private const int MaxGreetingAttempts = 3; // Max greeting messages per session
    private int _greetingAttempts = 0;

    // Rate limiting
    private readonly string? _rateLimiterAddress;
    private readonly IConnectionRateLimiter? _rateLimiter;

    // Server only: frees this session's MaxPendingHandshakes slot (once), and records pre-auth failures.
    private Action? _handshakeEnded;
    private readonly PreAuthFailureLog? _preAuthFailureLog;

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

    public SessionBudget Budget { get; }

    public SessionControlSender ControlSender => _controlSender;

    // Tick count when the current outbound transport flush started; 0 when none is in progress.
    private long _flushStartedTicks;

    // Invocation credits (docs/internals/flow-control.md, section 4.7). _inboundCredits: invocations the peer may
    // still queue here. _outboundCredits: invocations this side may still send, released as the peer returns credit.
    private int _inboundCredits;
    private readonly SemaphoreSlim _outboundCredits = new(0, int.MaxValue);
    private int _peerInvocationCredits;
    private int _peerMaxMessageSize;
    private long _creditDrops;
    private long _lastCreditDropWarning;

    // Large arguments and results (docs/internals/flow-control.md, section 4.3): streams the peer is sending
    // here, and streams this side is sending, by (invocation ID, kind).
    private readonly LargeMessageReceiver _largeMessages;
    private readonly ConcurrentDictionary<int, OutgoingLargeMessage> _outgoingLargeMessages = new();

    // Metrics (docs/articles/metrics.md): the tags this session is counted under, and its slot in their table.
    private readonly MetricsTagSet _metricsTags;
    int IMeteredSession.MetricsSlot { get; set; } = -1;
    long IMeteredSession.MetricsStartTimestamp { get; set; }

    public long Id
    {
        get => _id;
        private set
        {
            _id = value;
            if (Logger != null)
                Logger.PathSegment = $"S{value}";
        }
    }
    
    public IServerSessionManager? SessionManager => _sessionManager;
    public ISessionInvocationStateManager SessionInvocationStateManager { get; }
    public long LastReceived { get; private set; }

    public INexusLogger? Logger { get; private set; }
    PoolManager INexusSession.PoolManager => PoolManager;

    public List<int> RegisteredGroups { get; } = new();
    
    public Lock RegisteredGroupsLock { get; } = new();

    public SessionPoolManager<TProxy> PoolManager => _poolManager;
    public SessionStore SessionStore { get; }
    
    public NexusCollectionManager CollectionManager { get; }

    public Action? OnDisconnected { get; init; }

    public IIdentity? Identity { get; private set; }

    public Action? OnSent { get; set; }

    public string? RemoteAddress { get; }
    public int? RemotePort { get; }

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
        _poolManager = configurations.Pool;
        _sessionManager = configurations.SessionManager;
        IsServer = configurations.Client == null;
        _client = configurations.Client;
        _nexus = configurations.Nexus;
        _disconnectionCts = new CancellationTokenSource();
        CollectionManager = configurations.CollectionManager;
        _nexus.SessionContext = IsServer
            ? new ServerSessionContext<TProxy>(this, _sessionManager!)
            : new ClientSessionContext<TProxy>(this);

        // Store remote endpoint info from transport
        RemoteAddress = configurations.Transport.RemoteAddress;
        RemotePort = configurations.Transport.RemotePort;

        // Store rate limiter info for release on disconnect
        _rateLimiterAddress = configurations.RateLimiterAddress;
        _rateLimiter = configurations.RateLimiter;
        _handshakeEnded = configurations.HandshakeEnded;
        _preAuthFailureLog = configurations.PreAuthFailureLog;

        Logger = configurations.Logger?.CreateLogger($"S{Id}");
        
        if(configurations.ConnectionState == ConnectionState.Reconnecting)
            EnumUtilities<InternalState>.SetFlag(ref _internalState, InternalState.ReconnectingInProgress);
        
        PipeManager = new NexusPipeManager();
        PipeManager.Setup(this);

        SessionInvocationStateManager = new SessionInvocationStateManager(_poolManager, _config.Logger, this);
        SessionStore = new SessionStore();
        _freeInvocationSlots = _config.MaxConcurrentConnectionInvocations;
        Budget = new SessionBudget(_config.SessionBudget);

        // The peer may queue InvocationCredits invocations of up to one frame each; reserve that up front.
        _inboundCredits = _config.InvocationCredits;
        Budget.TryReserve((long)_config.InvocationCredits * (ushort.MaxValue + 1));
        _controlSender = new SessionControlSender(this, _config.InvocationCredits / 2);
        _largeMessages = new LargeMessageReceiver(this, Budget, _config.MaxMessageSize, _poolManager,
            _config.SerializerOptions, _config.Time);
        LastReceived = _config.Time.GetTickCount64();
        _metricsTags = configurations.MetricsTags ?? NexusMetrics.GetTagSet(_config);

        // Note: Session registration is deferred until after authentication completes
        // (see NexusSession.Receiving.cs, MessageType.ClientGreeting handler)
        // to prevent unauthenticated sessions from appearing in the registry.

        _config.InternalOnSessionSetup?.Invoke(this);

        Logger?.LogInfo($"Created session {Id}");
    }

    public Task DisconnectAsync(
        DisconnectReason reason, 
        [CallerFilePath]string? filePath = null, 
        [CallerLineNumber] int? lineNumber = null)
    {
        // ReSharper disable twice ExplicitCallerInfoArgument
        return DisconnectCore(reason, true, filePath, lineNumber).AsTask();
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

        // Progress (ConfigBase.ProgressTimeout): an outbound flush that does not complete, a pipe whose close the
        // peer never confirms, an oversized channel item or large message whose data stops arriving. Waiting for
        // credit is not measured.
        var now = _config.Time.GetTickCount64();
        var flushStarted = Volatile.Read(ref _flushStartedTicks);
        if ((flushStarted != 0 && now - flushStarted > _config.ProgressTimeout)
            || PipeManager.HasStalledPipe(now, _config.ProgressTimeout)
            || _largeMessages.HasStalledStream(now, _config.ProgressTimeout))
        {
            Logger?.LogWarning($"Session {Id} made no progress within {_config.ProgressTimeout} ms.");
            DisconnectAsync(DisconnectReason.Stalled);
            return true;
        }

        return false;
    }

    public async ValueTask InitializeConnection(CancellationToken cancellationToken = default)
    {
        // Send the connection header prior to the greeting
        await SendRaw(_protocolHeader, cancellationToken).ConfigureAwait(false);

        // Counted from here; DisconnectCore or the end of the read loop stops counting it.
        _metricsTags.Metrics.Register(this);

        _state = ConnectionState.Connected;
        OnStateChanged?.Invoke(State);
    }

    public async ValueTask StartAsClient()
    {
        var clientConfig = Unsafe.As<ClientConfig>(_config);

        var isReconnecting = _state == ConnectionState.Reconnecting;
        using IClientGreetingMessageBase greetingMessage = isReconnecting
            ? _poolManager.Rent<ClientGreetingReconnectionMessage>()
            : _poolManager.Rent<ClientGreetingMessage>();

        // Get the latest version of the 
        var latestVersion = TProxy.VersionHashTable.Keys.LastOrDefault();
        
        greetingMessage.Version = latestVersion;
        
        // If we are not versioning, use the default proxy hash. 
        greetingMessage.ServerNexusHash = latestVersion == null 
            ? TProxy.MethodHash 
            : TProxy.VersionHashTable.GetValueOrDefault(latestVersion, TProxy.MethodHash);
        greetingMessage.ClientNexusHash = TNexus.MethodHash;
        greetingMessage.AuthenticationToken = clientConfig.Authenticate?.Invoke() ?? Memory<byte>.Empty;
        greetingMessage.MaxMessageSize = _config.MaxMessageSize;
        greetingMessage.InvocationCredits = _config.InvocationCredits;
        
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

    private async ValueTask DisconnectCore(
        DisconnectReason reason,
        bool sendDisconnect, 
        [CallerFilePath]string? filePath = null, 
        [CallerLineNumber] int? lineNumber = null)
    {
        // If we are already disconnecting, don't do anything
        var state = Interlocked.Exchange(ref _state, ConnectionState.Disconnecting);

        if (state == ConnectionState.Disconnecting || state == ConnectionState.Disconnected)
            return;

        OnStateChanged?.Invoke(State);

        DisconnectReason = reason;

        _registeredDisconnectReason = reason;

        Logger?.LogInfo($"Session disconnected with reason: {reason}; Source: {Path.GetFileName(filePath)}:{lineNumber}");

        if ((_internalState & InternalState.NexusCompletedConnection) == 0)
            _preAuthFailureLog?.Record(reason);
        EndHandshake();
        
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
                    // Real-time wait so the OS TCP stack actually has wall-clock time to flush the
                    // disconnect message before the socket is closed. Not routed through TimeProvider —
                    // faking this would not accelerate any real kernel work and would deadlock test
                    // teardown when the test no longer advances FakeTimeProvider.
                    await Task.Delay(_config.DisconnectDelay).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        // Wake the read loop; it completes the input itself when it exits. Completing it here could recycle the buffer
        // the read loop is still processing.
        _pipeInput?.CancelPendingRead();
        _pipeInput = null;

        while (_invocationQueue.TryDequeue(out var queued))
            queued.Dispose();

        RetireMetrics();

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

        // Complete the output under the write lock: PipeWriter is not thread-safe, and invocations may still be
        // sending results. A writer blocked on back pressure is released first so the lock can be taken.
        var output = _pipeOutput;
        MutexSlim.LockToken writeLock = default;
        try
        {
            output?.CancelPendingFlush();
        }
        catch (Exception)
        {
            // Best effort: stream-backed writers throw once their transport has already closed.
        }

        try
        {
            using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            writeLock = await _writeMutex.TryWaitAsync(lockTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger?.LogWarning("Timed out waiting for the write lock while disconnecting; completing the output without it.");
        }

        try
        {
            // ReSharper disable once MethodHasAsyncOverload
            output?.Complete();
        }
        catch (ObjectDisposedException)
        {
            //noop
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while completing output pipe.");
        }
        finally
        {
            _pipeOutput = null;
            writeLock.Dispose();
        }
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

        // Cancel all pipes. Each session creates its own manager and never reuses one: invocations of this session can
        // still be running (CancelAll above only signals them), and one that registers a pipe after a reuse would attach
        // it to another session.
        PipeManager.CancelAll();

        _nexus.Disconnected(reason);
        OnDisconnected?.Invoke();

        _nexus.SessionContext.Reset();

        // Unregister from groups and session registry
        if (_sessionManager != null)
        {
            await _sessionManager.Groups.RemoveFromAllGroupsAsync(this).ConfigureAwait(false);
            await _sessionManager.Sessions.UnregisterSessionAsync(this).ConfigureAwait(false);
        }

        // Release rate limiter slot
        _rateLimiter?.Release(_rateLimiterAddress);

        ((IDisposable)SessionStore).Dispose();
        _invocationWorkItemPool.Clear();

        _disconnectedTaskCompletionSource?.TrySetResult(reason);

        Logger?.LogTrace("ReadyTaskCompletionSource fired in DisconnectCore");
        _readyTaskCompletionSource?.TrySetResult();
    }
    
    
    /// <summary>
    /// Stops counting this session in metrics and records its disconnect. Safe to call more than once.
    /// </summary>
    private void RetireMetrics()
        => _metricsTags.Metrics.Retire(this, DisconnectReason, (_internalState & InternalState.NexusCompletedConnection) != 0);

    MetricsTagSet IMeteredSession.MetricsTags => _metricsTags;

    bool IMeteredSession.IsDisconnected => _state == ConnectionState.Disconnected;

    bool IMeteredSession.IsEstablished => (_internalState & InternalState.NexusCompletedConnection) != 0;

    void IMeteredSession.CollectMetrics(ref SessionMetricsTotals totals)
    {
        totals.Sessions++;
        if ((_internalState & InternalState.NexusCompletedConnection) == 0)
            totals.Handshaking++;
    }

    /// <summary>
    /// Frees this session's <c>MaxPendingHandshakes</c> slot. Safe to call more than once.
    /// </summary>
    internal void EndHandshake() => Interlocked.Exchange(ref _handshakeEnded, null)?.Invoke();

    /// <summary>
    /// Method executed at the handshake timeout to verify the connection has been fully established.
    /// </summary>
    /// <param name="timeoutTask">Task which fired this method.</param>
    private async Task CheckHandshakeComplete(Task timeoutTask)
    {
        if (_state != ConnectionState.Connected)
            return;

        if ((_internalState & InternalState.ProtocolConfirmed) != 0 
            && (_internalState & InternalState.NexusCompletedConnection) != 0)
            return;
        
        Logger?.LogDebug("Connection closed due to handshake timeout.");
            
        await DisconnectCore(DisconnectReason.Timeout, false).ConfigureAwait(false);
    }

    public override string ToString() => $"NexusSession [{Id}] IsServer:{IsServer}";
    
    public async ValueTask<bool> SendInvocation<TMessage>(TMessage message, CancellationToken cancellationToken = default, TimeSpan? creditTimeout = null)
        where TMessage : IMessageBase
    {
        if (!_outboundCredits.Wait(0))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectionCts.Token);
            try
            {
                if (!await _outboundCredits.WaitAsync(creditTimeout ?? Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false))
                {
                    RecordCreditDrop();
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The session ended while waiting.
                return false;
            }
        }

        await SendMessage(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Logs, at most once per 10 s, invocations dropped because the peer granted no credit in time.
    /// </summary>
    private void RecordCreditDrop()
    {
        var drops = Interlocked.Increment(ref _creditDrops);
        var now = _config.Time.GetTickCount64();
        var last = Volatile.Read(ref _lastCreditDropWarning);
        if (now - last < 10_000 || Interlocked.CompareExchange(ref _lastCreditDropWarning, now, last) != last)
            return;

        Logger?.LogWarning($"Session {Id} dropped invocations because the peer returned no invocation credit within " +
                           $"{_config.ProgressTimeout} ms ({drops} so far).");
    }

    /// <summary>
    /// Applies the peer's advertised limits from its greeting. Returns false if they are invalid.
    /// </summary>
    private bool ApplyPeerLimits(int maxMessageSize, int invocationCredits)
    {
        if (maxMessageSize < IInvocationMessage.MaxArgumentSize || invocationCredits < 1)
            return false;

        _peerMaxMessageSize = maxMessageSize;
        _peerInvocationCredits = invocationCredits;
        return true;
    }

    /// <summary>
    /// Largest value this side may send: the smaller of its own limit and the peer's.
    /// </summary>
    public int SendMaxMessageSize => Math.Min(_config.MaxMessageSize, _peerMaxMessageSize);

    /// <summary>
    /// Runs queued invocations while slots are free. Called after an invocation is queued and after a slot is
    /// returned, so a queued invocation always finds a free slot.
    /// </summary>
    private void DispatchInvocations()
    {
        while (!_invocationQueue.IsEmpty && TryTakeInvocationSlot())
        {
            if (!_invocationQueue.TryDequeue(out var message))
            {
                // Another dispatcher took it.
                Interlocked.Increment(ref _freeInvocationSlots);
                continue;
            }

            if (State != ConnectionState.Connected)
            {
                message.Dispose();
                Interlocked.Increment(ref _freeInvocationSlots);
                continue;
            }


            if (!_invocationWorkItemPool.TryTake(out var workItem))
                workItem = new InvocationWorkItem();

            workItem.Message = message;
            workItem.Session = this;
            workItem.SlotHeld = 1;
            message.Slot = workItem;

            // Dequeued for execution: the peer may queue another.
            ReturnInvocationCredit();

            // Local queue: the global queue delays the start under load, which holds execution slots and invocation
            // credits longer and leaves broadcast senders waiting on busy sessions.
            ThreadPool.UnsafeQueueUserWorkItem(workItem, preferLocal: true);
        }
    }

    bool LargeMessageReceiver.IOwner.IsCallOutstanding(ushort invocationId)
        => SessionInvocationStateManager.IsOutstanding(invocationId);

    bool LargeMessageReceiver.IOwner.TryTakeInvocationCredit()
        => Interlocked.Decrement(ref _inboundCredits) >= 0;

    void LargeMessageReceiver.IOwner.ReturnInvocationCredit() => ReturnInvocationCredit();

    void LargeMessageReceiver.IOwner.SendAbort(ushort invocationId, LargeMessageKind kind, LargeMessageAbortReason reason)
        => _controlSender.RequestAbort(invocationId, kind, reason);

    private void ReturnInvocationCredit()
    {
        Interlocked.Increment(ref _inboundCredits);
        _controlSender.ReturnInvocationCredit();
    }

    private bool TryTakeInvocationSlot()
    {
        var free = Volatile.Read(ref _freeInvocationSlots);
        while (free > 0)
        {
            var observed = Interlocked.CompareExchange(ref _freeInvocationSlots, free - 1, free);
            if (observed == free)
                return true;
            free = observed;
        }

        return false;
    }

    private void ReturnInvocationSlot()
    {
        Interlocked.Increment(ref _freeInvocationSlots);
        DispatchInvocations();
    }

    /// <summary>
    /// Runs one received invocation on the thread pool and awaits it. Pooled per session, so a dispatch allocates
    /// nothing.
    /// </summary>
    private sealed class InvocationWorkItem : IInvocationSlot, IThreadPoolWorkItem
    {
        public InvocationMessage Message = null!;
        public NexusSession<TNexus, TProxy> Session = null!;

        // 1 while the invocation holds its execution slot.
        public int SlotHeld;

        private readonly Action _onCompleted;
        private ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter _awaiter;

        public InvocationWorkItem()
        {
            _onCompleted = OnCompleted;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref SlotHeld, 0) == 1)
                Session.ReturnInvocationSlot();
        }

        void IThreadPoolWorkItem.Execute()
        {
            var context = Session._invocationExecutionContext;
            if (context == null)
                Start();
            else
                ExecutionContext.Run(context, static state => Unsafe.As<InvocationWorkItem>(state!).Start(), this);
        }

        private void Start()
        {
            try
            {
                Session.Logger?.LogTrace($"Invoking method {Message.MethodId}.");
                var awaiter = Session._nexus.InvokeMethod(Message).ConfigureAwait(false).GetAwaiter();
                if (!awaiter.IsCompleted)
                {
                    // The continuation only finishes this item, so it does not need the context.
                    _awaiter = awaiter;
                    awaiter.UnsafeOnCompleted(_onCompleted);
                    return;
                }

                awaiter.GetResult();
            }
            catch (Exception e)
            {
                LogFault(e);
            }

            Finish();
        }

        private void OnCompleted()
        {
            var awaiter = _awaiter;
            _awaiter = default;
            try
            {
                awaiter.GetResult();
            }
            catch (Exception e)
            {
                LogFault(e);
            }

            Finish();
        }

        private void LogFault(Exception e)
        {
            Session._config.Logger?.LogError(e, $"Invoked method {Message.MethodId} threw exception");
        }

        private void Finish()
        {
            var session = Session;

            // Frees the slot unless a pipe registration already did.
            Release();

            // Clear out the references before returning to the pool.
            Session = null!;
            Message = null!;
            session._invocationWorkItemPool.Add(this);
        }
    }
}
