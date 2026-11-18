using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Invocation;
using NexNet.Pools;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.RateLimiting;

namespace NexNet;

/// <summary>
/// Class which manages session connections.
/// </summary>
/// <typeparam name="TServerNexus">The nexus which will be running locally on the server.</typeparam>
/// <typeparam name="TClientProxy">Proxy used to invoke methods on the remote nexus.</typeparam>
public sealed class NexusServer<TServerNexus, TClientProxy> : INexusServer<TServerNexus, TClientProxy>
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    // ReSharper disable once StaticMemberInGenericType
    private static long _idCounter = 0;

    /// <summary>
    /// Resets the server ID counter. For testing purposes only.
    /// </summary>
    internal static void ResetIdCounter() => Interlocked.Exchange(ref _idCounter, 0);

    private IServerSessionManager _sessionManager = null!;
    private ITimer? _watchdogTimer;
    private ServerConfig? _config;
    private Func<TServerNexus>? _nexusFactory;
    private readonly SessionPoolManager<TClientProxy> _poolManager;
    private TaskCompletionSource? _stoppedTcs;
    private volatile NexusServerState _state = NexusServerState.Stopped;
    private CancellationTokenSource? _cancellationTokenSource;

    // Serializes StartAsync, StopAsync and DisposeAsync. State reads stay lock-free.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    // Endpoints this server accepts sessions on. Today: exactly one, built from the primary config. In Listener mode
    // it owns a transport listener; in Receiver mode it is the external path fed through AcceptTransport.
    private readonly List<ServerEndpoint> _endpoints = new();
    private ServerEndpoint _primaryEndpoint = null!;

    /// <summary>Most concurrent session disconnects while the server stops.</summary>
    private const int MaxParallelShutdownDisconnects = 64;
    
    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementer;
    private INexusLogger? _logger;
    private NexusCollectionManager _collectionManager = null!;
    private IConnectionRateLimiter? _rateLimiter;
    private PreAuthFailureLog? _preAuthFailureLog;
    private MetricsTagSet _metricsTags = null!;

    // Connections between accept and completed authentication; bounded by ServerConfig.MaxPendingHandshakes.
    private int _pendingHandshakes;
    private readonly Action _handshakeEnded;
    //private Func<TServerNexus, ValueTask>? _configureCollections;

    /// <inheritdoc />
    public NexusServerState State => _state;

    /// <inheritdoc />
    public ServerConfig Config => _config ?? throw new InvalidOperationException("Nexus server has not been started yet.  Please setup with the parameterized constructor or invoke Configure().");

    /// <inheritdoc />
    public Task? StoppedTask => _stoppedTcs?.Task;

    /// <inheritdoc />
    public bool IsConfigured => _config != null;
    
    /// <inheritdoc />
    public ServerNexusContextProvider<TServerNexus, TClientProxy> ContextProvider { get; private set; } = null!;

    /// <summary>
    /// Snapshot of the server: pending handshakes, rate limiter statistics and a snapshot of every authenticated
    /// session. Not an atomic picture; exposes internal state, so do not serve it to unauthenticated callers. See
    /// docs/articles/metrics.md.
    /// </summary>
    public NexusServerSnapshot GetSnapshot()
    {
        var sessions = new List<NexusSessionSnapshot>();
        if (_sessionManager != null)
        {
            foreach (var session in _sessionManager.Sessions.LocalSessions)
                sessions.Add(session.GetSnapshot());
        }

        return new NexusServerSnapshot
        {
            State = _state,
            PendingHandshakes = Math.Max(0, Volatile.Read(ref _pendingHandshakes)),
            RateLimiter = _rateLimiter?.GetStats(),
            Sessions = sessions,
        };
    }

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    /// <param name="configureCollections">Function to configure collections.</param>
    public NexusServer(
        ServerConfig config, 
        Func<TServerNexus> nexusFactory, 
        Action<TServerNexus>? configureCollections = null)
        : this()
    {
        Configure(config, nexusFactory, configureCollections);
    }
    
    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// This is used in conjunction with the <see cref="Configure"/> method.  Configure must
    /// be invoked after instancing with this constructor prior to starting the server,
    /// otherwise it will throw on start. 
    /// </summary>
    public NexusServer()
    {
        _poolManager = new SessionPoolManager<TClientProxy>();
        _handshakeEnded = () => Interlocked.Decrement(ref _pendingHandshakes);
    }

    /// <summary>
    /// Configures the server after instancing.  This can only be executed a single time and with the
    /// <see cref="NexusServer{TServerNexus,TClientProxy}"/> paramaterless constructor.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    /// <param name="configureCollections"></param>
    /// <remarks>
    /// Do not use this method.  Instead, use the parameterized constructor.
    /// </remarks>
    public void Configure(ServerConfig config,
        Func<TServerNexus> nexusFactory,
        Action<TServerNexus>? configureCollections)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nexusFactory);
        if(_config != null)
            throw new InvalidOperationException("Server has already been configured.");

        config.ValidateFlowControl();
        

        _config = config;
        _watchdogTimer = _config.Time.CreateTimer(ConnectionWatchdog, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _nexusFactory = nexusFactory;
        var id = Interlocked.Increment(ref _idCounter);
        _logger = config.Logger?.CreateLogger($"SV{id}");

        // Initialize the session manager
        _sessionManager = config.GetSessionManager();

        // Set the collection manager and configure for this nexus.
        _collectionManager = new NexusCollectionManager(_logger, true, _config.Time);
        TServerNexus.ConfigureCollections(_collectionManager);

        ContextProvider = new ServerNexusContextProvider<TServerNexus, TClientProxy>(
            nexusFactory,
            _collectionManager,
            _sessionManager,
            _poolManager);
        
        if (configureCollections != null)
        {
            var configNexus = _nexusFactory!.Invoke();
            // Add a special context used for only configuring collections.  Any other usage of methods throws.
            configNexus.SessionContext = new ConfigurerSessionContext<TClientProxy>(_collectionManager);
            try
            {
                configureCollections.Invoke(configNexus);
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Exception while configuring collections.");
            }
        }

        _primaryEndpoint = new ServerEndpoint(config, config.TransportName,
            config.ConnectionMode == ServerConnectionMode.Listener);
        _endpoints.Add(_primaryEndpoint);

        _preAuthFailureLog = new PreAuthFailureLog(_logger, config.Time);
        _metricsTags = NexusMetrics.GetTagSet(config);
    }

    /// <summary>
    /// Takes a <see cref="ServerConfig.MaxPendingHandshakes"/> slot for a new transport. Returns false when none is
    /// free; the transport must then be closed.
    /// </summary>
    private bool TryBeginHandshake()
    {
        if (Interlocked.Increment(ref _pendingHandshakes) <= _config!.MaxPendingHandshakes)
            return true;

        Interlocked.Decrement(ref _pendingHandshakes);
        _preAuthFailureLog?.RecordHandshakeLimit();
        _metricsTags.Metrics.RecordRejection(_metricsTags, NexusMetrics.RejectionMaxPendingHandshakes);
        return false;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(_config);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == NexusServerState.Running)
                throw new InvalidOperationException("Server is already running");

            // Only execute if the server is stopped and not disposed.
            if (_state != NexusServerState.Stopped)
                return;

            _state = NexusServerState.Starting;

            try
            {
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await AbortStartAsync().ConfigureAwait(false);
                _state = NexusServerState.Stopped;
                throw;
            }

            _state = NexusServerState.Running;

            foreach (var endpoint in _endpoints)
            {
                endpoint.State = EndpointState.Running;
                if (endpoint.IsListener)
                {
                    StartOnScheduler(_config.ReceiveSessionPipeOptions.ReaderScheduler,
                        static state =>
                        {
                            var (server, ep) = ((NexusServer<TServerNexus, TClientProxy>, ServerEndpoint))state!;
                            ep.AcceptLoop = server.ListenForConnectionsAsync(ep);
                        }, (this, endpoint));
                }
            }

            _watchdogTimer!.Change(TimeSpan.FromMilliseconds(_config.Timeout / 4), TimeSpan.FromMilliseconds(_config.Timeout / 4));
            _collectionManager.Start();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Everything that can fail while starting: binds every listener endpoint and initializes the session manager.
    /// The server is not yet Running, so nothing is accepted. On failure, <see cref="AbortStartAsync"/> undoes it.
    /// </summary>
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var config = _config!;

        // Created on every start: StopAsync disposes it, so a restarted server gets a fresh one.
        if (config.RateLimiting?.IsEnabled == true)
            _rateLimiter = new ConnectionRateLimiter(config.RateLimiting, config.Time);

        if (config.ConnectionMode == ServerConnectionMode.Listener)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _stoppedTcs?.TrySetResult();
            _stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        foreach (var endpoint in _endpoints)
        {
            if (!endpoint.IsListener)
                continue;

            endpoint.Listener = await BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        await _sessionManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ITransportListener> BindAsync(ServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        ITransportListener? listener;
        try
        {
            listener = await endpoint.Config.CreateServerListener(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException e)
        {
            throw new TransportException(e.Error, $"Endpoint '{endpoint.Name}' failed to bind: {e.Message}", e);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            throw new TransportException(e.SocketErrorCode, $"Endpoint '{endpoint.Name}' failed to bind: {e.Message}", e);
        }

        return listener ?? throw new InvalidOperationException($"""
                                                                Configuration for endpoint '{endpoint.Name}' returned a null listener.
                                                                If the server is directly being provided connections via the AcceptTransport method,
                                                                then the ServerConnectionMode.Receiver should be set on the configuration.
                                                                """);
    }

    /// <summary>
    /// Undoes a failed <see cref="StartCoreAsync"/>: closes every endpoint bound so far and releases what start created.
    /// </summary>
    private async Task AbortStartAsync()
    {
        await CloseListenersAsync(linger: false).ConfigureAwait(false);

        foreach (var endpoint in _endpoints)
            endpoint.State = EndpointState.Stopped;

        DisposeCancellationTokenSource();

        _rateLimiter?.Dispose();
        _rateLimiter = null;

        // Nothing ran, so the server never stopped in the StoppedTask sense; leave the task completed so a waiter
        // does not hang on a server that is not running.
        _stoppedTcs?.TrySetResult();
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != NexusServerState.Running)
                throw new InvalidOperationException("Server is not running");

            await StopCoreAsync().ConfigureAwait(false);
            _state = NexusServerState.Stopped;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops a Running server. The caller holds the lifecycle lock and sets the final state.
    /// </summary>
    private async Task StopCoreAsync()
    {
        // Gates new sessions: the accept loops and AcceptTransport only admit while Running.
        _state = NexusServerState.Stopping;

        _logger?.LogInfo("Stopping server");

        // The async overload just uses more resources.
        // ReSharper disable once MethodHasAsyncOverload
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch
        {
            // ignored
        }

        await CloseListenersAsync(linger: !_config!.InternalNoLingerOnShutdown).ConfigureAwait(false);

        foreach (var endpoint in _endpoints)
            endpoint.State = EndpointState.Stopped;

        _collectionManager.Stop();

        await DisconnectSessionsAsync().ConfigureAwait(false);

        // Shutdown the session manager
        await _sessionManager.ShutdownAsync().ConfigureAwait(false);

        // Dispose rate limiter
        _rateLimiter?.Dispose();
        _rateLimiter = null;

        _poolManager.Clear();

        // Stop the watchdog timer as the server is no longer running.
        _watchdogTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        DisposeCancellationTokenSource();

        _stoppedTcs?.TrySetResult();
    }

    /// <summary>
    /// Closes every bound listener in parallel. Close errors are logged and otherwise ignored.
    /// </summary>
    private async Task CloseListenersAsync(bool linger)
    {
        List<Task>? closes = null;
        foreach (var endpoint in _endpoints)
        {
            var listener = endpoint.Listener;
            if (listener == null)
                continue;

            endpoint.Listener = null;
            (closes ??= new List<Task>()).Add(CloseListenerAsync(endpoint, listener, linger));
        }

        if (closes != null)
            await Task.WhenAll(closes).ConfigureAwait(false);
    }

    private async Task CloseListenerAsync(ServerEndpoint endpoint, ITransportListener listener, bool linger)
    {
        try
        {
            await listener.CloseAsync(linger).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogDebug($"Error while closing the listener of endpoint '{endpoint.Name}': {e.Message}");
        }
    }

    /// <summary>
    /// Disconnects every local session with <see cref="DisconnectReason.ServerShutdown"/>, at most
    /// <see cref="MaxParallelShutdownDisconnects"/> at a time, so each session's disconnect delay overlaps.
    /// </summary>
    private async Task DisconnectSessionsAsync()
    {
        var sessions = new List<INexusSession>(_sessionManager.Sessions.LocalSessions);
        if (sessions.Count == 0)
            return;

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelShutdownDisconnects };
        await Parallel.ForEachAsync(sessions, options, async (session, _) =>
        {
            try
            {
                await session.DisconnectAsync(DisconnectReason.ServerShutdown).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Ignore exceptions
                _config!.Logger?.LogError(e, $"Error while disconnecting session {session.Id} due to server shutdown.");
            }
        }).ConfigureAwait(false);
    }

    private void DisposeCancellationTokenSource()
    {
        var cts = _cancellationTokenSource;
        _cancellationTokenSource = null;
        cts?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == NexusServerState.Disposed)
                return;

            if (_state == NexusServerState.Running)
                await StopCoreAsync().ConfigureAwait(false);

            _state = NexusServerState.Disposed;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    ValueTask IAcceptsExternalTransport.AcceptTransport(ITransport transport, CancellationToken cancellationToken)
    {
        // If the server is not running, don't accept connections.
        if(_state != NexusServerState.Running)
            return ValueTask.CompletedTask;

        if (_config == null)
            throw new InvalidOperationException(
                "Can't accept new transport before server configuration has been completed.");

        return TryAdmitTransport(transport, _primaryEndpoint, out var sessionConfig)
            ? RunSessionAsync(sessionConfig, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The accept routine shared by the accept loops and <see cref="IAcceptsExternalTransport.AcceptTransport"/>.
    /// Applies the pending handshake limit and the rate limiter, then builds the session's configuration.
    /// Returns false when the transport is rejected; the transport is then closed.
    /// </summary>
    private bool TryAdmitTransport(
        ITransport transport,
        ServerEndpoint endpoint,
        out NexusSessionConfigurations<TServerNexus, TClientProxy> sessionConfig)
    {
        sessionConfig = default;

        if (!TryBeginHandshake())
        {
            CloseRejected(transport);
            return false;
        }

        // Rate limit check using ITransport.RemoteAddress
        string? remoteAddress = null;
        var rateLimiter = _rateLimiter;
        if (rateLimiter != null)
        {
            remoteAddress = transport.RemoteAddress;
            var result = rateLimiter.TryAcquire(remoteAddress);

            if (result != ConnectionRateLimitResult.Allowed)
            {
                _logger?.LogDebug($"Connection on endpoint '{endpoint.Name}' rejected: {result} from {remoteAddress ?? "unknown"}");
                _metricsTags.Metrics.RecordRejection(_metricsTags, NexusMetrics.RateLimitReasonName(result));
                _handshakeEnded();
                CloseRejected(transport);
                return false;
            }
        }

        _config!.InternalOnConnect?.Invoke();

        // Create a composite ID of the current ticks along with the current ticks.
        // This makes guessing IDs harder, but not impossible.
        var baseSessionId = Interlocked.Increment(ref _sessionIdIncrementer);

        sessionConfig = new NexusSessionConfigurations<TServerNexus, TClientProxy>()
        {
            ConnectionState = ConnectionState.Connecting,
            Transport = transport,
            Pool = _poolManager,
            Configs = _config,
            SessionManager = _sessionManager,
            Client = null,
            Id = GenerateSecureSessionId(baseSessionId),
            Nexus = _nexusFactory!.Invoke(),
            CollectionManager = _collectionManager,
            Logger = _logger,
            RateLimiterAddress = remoteAddress,
            RateLimiter = rateLimiter,
            HandshakeEnded = _handshakeEnded,
            PreAuthFailureLog = _preAuthFailureLog,
            MetricsTags = _metricsTags
        };
        return true;
    }

    private static void CloseRejected(ITransport transport)
    {
        static async Task CloseQuietlyAsync(ITransport transport)
        {
            try
            {
                await transport.CloseAsync(false).ConfigureAwait(false);
            }
            catch
            {
                /* ignore close errors */
            }
        }

        _ = CloseQuietlyAsync(transport);
    }

    private static async void RunSessionAsync(object? boxed)
    {
        try
        {
            var arguments = (NexusSessionConfigurations<TServerNexus, TClientProxy>)boxed!;
            await RunSessionAsync(arguments).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ((NexusSessionConfigurations<TServerNexus, TClientProxy>)boxed!).Configs.Logger
                ?.LogError(e, "Exception while running client");
        }
        
    }

    private static async ValueTask RunSessionAsync(
        NexusSessionConfigurations<TServerNexus, TClientProxy> arguments,
        CancellationToken cancellationToken = default)
    {
        var session = new NexusSession<TServerNexus, TClientProxy>(arguments);
        try
        {
            await session.InitializeConnection(cancellationToken).ConfigureAwait(false);
            await session.StartReadAsync(cancellationToken).ConfigureAwait(false);

            // Normally freed by DisconnectCore or a completed handshake; this covers a read loop that ended otherwise.
            session.EndHandshake();

            // The read loop exits as soon as the session is disconnecting, possibly while DisconnectCore is still
            // sending the disconnect frame. That disconnect completes the output and closes the transport itself;
            // completing them here first would drop the frame. Wait for it; the completions below are then no-ops,
            // except after a no-linger disconnect, which closes the transport without completing the output.
            if (session.IsDisconnectStarted)
                await session.DisconnectCompletion.ConfigureAwait(false);

            try
            {
                // ReSharper disable once MethodHasAsyncOverload
                arguments.Transport.Input.Complete();
            }
            catch
            {
                // ignored
            }

            try
            {
                // ReSharper disable once MethodHasAsyncOverload
                arguments.Transport.Output.Complete();
            }
            catch
            {
                // ignored
            }
        }
        catch (Exception ex)
        {
            session.EndHandshake();
            session.Logger?.LogError(ex, "Exception while running session");
            try
            {
                // ReSharper disable once MethodHasAsyncOverload
                arguments.Transport.Input.Complete(ex);
            }
            catch
            {
                // ignored
            }

            try
            {
                // ReSharper disable once MethodHasAsyncOverload
                arguments.Transport.Output.Complete(ex);
            }
            catch
            {
                // ignored
            }

            //OnClientFaulted(in client, ex);
        }
    }


    private void ConnectionWatchdog(object? state)
    {
        var timeoutTicks = _config!.Time.GetTickCount64() - _config!.Timeout;

        foreach (var session in _sessionManager.Sessions.LocalSessions)
            session.DisconnectIfTimeout(timeoutTicks);
    }
    
    private async Task ListenForConnectionsAsync(ServerEndpoint endpoint)
    {
        var listener = endpoint.Listener;
        if (listener == null)
            return;

        try
        {
            // Read inside the try: a concurrent stop may already have disposed the source.
            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            _logger?.LogInfo($"Listening for connections on endpoint '{endpoint.Name}'");
            while (_state == NexusServerState.Running && endpoint.State == EndpointState.Running)
            {
                var clientTransport = await listener.AcceptTransportAsync(cancellationToken).ConfigureAwait(false);

                if(clientTransport == null)
                    continue;

                if (!TryAdmitTransport(clientTransport, endpoint, out var sessionConfig))
                    continue;

                // boxed, but only once per client
                StartOnScheduler(
                    _config!.ReceiveSessionPipeOptions.ReaderScheduler,
                    RunSessionAsync,
                    sessionConfig);
            }
        }
        catch (Exception ex)
        {
            // A closed listener faults its pending accept once the server is stopping; that is not a fault.
            if (_state != NexusServerState.Running || endpoint.State != EndpointState.Running)
                return;

            endpoint.State = EndpointState.Faulted;
            _logger?.LogError(ex, $"Endpoint '{endpoint.Name}' faulted and stopped accepting connections.");
            await StopIfNoEndpointAliveAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the server after an endpoint fault when no listener endpoint is still accepting and there is no
    /// external path (Receiver mode). Otherwise the server keeps running on what is left.
    /// </summary>
    private async Task StopIfNoEndpointAliveAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != NexusServerState.Running)
                return;

            foreach (var endpoint in _endpoints)
            {
                if (!endpoint.IsListener || endpoint.State == EndpointState.Running)
                    return;
            }

            _logger?.LogError("No endpoint is accepting connections; server shut down.");
            await StopCoreAsync().ConfigureAwait(false);
            _state = NexusServerState.Stopped;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error while stopping the server after an endpoint fault.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static void StartOnScheduler(PipeScheduler? scheduler, Action<object?> callback, object? state)
    {
        if (scheduler == PipeScheduler.Inline) scheduler = null;
        (scheduler ?? PipeScheduler.ThreadPool).Schedule(callback, state);
    }

    /// <summary>
    /// Generates a secure session ID by combining an incrementing counter with cryptographically random bytes.
    /// The upper 32 bits contain the sequential counter (for uniqueness), and the lower 32 bits contain
    /// cryptographically random data (for unpredictability).
    /// </summary>
    private static long GenerateSecureSessionId(int baseSessionId)
    {
        Span<byte> randomBytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(randomBytes);
        uint randomPart = BitConverter.ToUInt32(randomBytes);
        return (long)baseSessionId << 32 | randomPart;
    }

    /// <summary>
    /// One place the server accepts sessions from. Internal only; the public surface is unchanged.
    /// </summary>
    private sealed class ServerEndpoint
    {
        public ServerEndpoint(ServerConfig config, string name, bool isListener)
        {
            Config = config;
            Name = name;
            IsListener = isListener;
        }

        /// <summary>Transport configuration the endpoint binds with.</summary>
        public ServerConfig Config { get; }

        /// <summary>Name used in logs and errors.</summary>
        public string Name { get; }

        /// <summary>Free-form tags. Empty until endpoints become configurable.</summary>
        public IReadOnlyList<string> Tags { get; } = Array.Empty<string>();

        /// <summary>True when the endpoint owns a listener; false for the external (AcceptTransport) path.</summary>
        public bool IsListener { get; }

        /// <summary>Bound listener while the server is starting or running.</summary>
        public ITransportListener? Listener { get; set; }

        /// <summary>The running accept loop, if any.</summary>
        public Task? AcceptLoop { get; set; }

        /// <summary>Lifecycle state of this endpoint alone.</summary>
        public EndpointState State
        {
            get => _state;
            set => _state = value;
        }

        private volatile EndpointState _state = EndpointState.Stopped;
    }

    private enum EndpointState
    {
        Stopped,
        Running,
        Faulted,
    }
}
