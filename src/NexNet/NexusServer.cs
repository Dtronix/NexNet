using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Invocation;
using NexNet.Pools;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Collections;
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
    private readonly Timer _watchdogTimer;
    private ServerConfig? _config;
    private Func<TServerNexus>? _nexusFactory;
    private readonly SessionPoolManager<TClientProxy> _poolManager;
    private ITransportListener? _listener;
    private TaskCompletionSource? _stoppedTcs;
    private NexusServerState _state = NexusServerState.Stopped;
    private CancellationTokenSource? _cancellationTokenSource;
    
    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementer;
    private INexusLogger? _logger;
    private NexusCollectionManager _collectionManager = null!;
    private IConnectionRateLimiter? _rateLimiter;
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
        _watchdogTimer = new Timer(ConnectionWatchdog);
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
        

        _config = config;
        _nexusFactory = nexusFactory;
        var id = Interlocked.Increment(ref _idCounter);
        _logger = config.Logger?.CreateLogger($"SV{id}");

        // Initialize the session manager
        _sessionManager = config.GetSessionManager();

        // Set the collection manager and configure for this nexus.
        _collectionManager = new NexusCollectionManager(_logger, true);
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

        // Initialize rate limiter if configured
        if (config.RateLimiting?.IsEnabled == true)
        {
            _rateLimiter = new ConnectionRateLimiter(config.RateLimiting);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(_config);
        static void FireAndForget(Task? task)
        {
            // make sure that any exception is observed
            if (task == null) return;
            if (task.IsCompleted)
            {
                GC.KeepAlive(task.Exception);
                return;
            }

            task.ContinueWith(t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }
        if (State == NexusServerState.Running) 
            throw new InvalidOperationException("Server is already running");

        // Only execute if the server is stopped and not disposed.
        if (Interlocked.CompareExchange(ref _state, NexusServerState.Running, NexusServerState.Stopped) != NexusServerState.Stopped)
            return;
        
        if (_config.ConnectionMode == ServerConnectionMode.Listener)
        {
            // Startup the listener and stopping mechanisms.
            _cancellationTokenSource = new CancellationTokenSource();
            _stoppedTcs?.TrySetResult();
            _stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            
            _listener = await _config.CreateServerListener(cancellationToken).ConfigureAwait(false);
            
            if(_listener == null)
                throw new InvalidOperationException("""
                                                    Configuration returned a null listener.  
                                                    If the server is directly being provided connections via the AcceptTransport method,
                                                    then the ServerConnectionMode.Receiver should be set on the configuration.
                                                    """);

            StartOnScheduler(_config.ReceiveSessionPipeOptions.ReaderScheduler,
                _ => FireAndForget(ListenForConnectionsAsync()), null);
        }
        
        _watchdogTimer.Change(_config.Timeout / 4, _config.Timeout / 4);

        // Initialize the session manager
        await _sessionManager.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _collectionManager.Start();
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, NexusServerState.Stopped, NexusServerState.Running) != NexusServerState.Running)
            throw new InvalidOperationException("Server is not running");

        _logger?.LogInfo("Stopping server");

        _collectionManager.Stop();

        // If the server is not listening for connections, we are done.
        if (_config?.ConnectionMode == ServerConnectionMode.Listener)
        {
            var listener = _listener;
            _listener = null;

            try
            {
                // The async overload just uses more resources.
                // ReSharper disable once MethodHasAsyncOverload
                _cancellationTokenSource?.Cancel();
                
                // If the listener is null, then the incoming connections are not handled by a listener,
                // and we don't have any work to perform.
                if (listener != null)
                    await listener.CloseAsync(!_config!.InternalNoLingerOnShutdown).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _stoppedTcs?.TrySetResult();
        }
        
        foreach (var session in _sessionManager.Sessions.LocalSessions)
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
        }

        // Shutdown the session manager
        await _sessionManager.ShutdownAsync().ConfigureAwait(false);

        // Dispose rate limiter
        _rateLimiter?.Dispose();
        _rateLimiter = null;

        _poolManager.Clear();

        // Stop the watchdog timer as the server is no longer running.
        _watchdogTimer.Change(-1, -1);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var previousState = Interlocked.Exchange(ref _state, NexusServerState.Disposed);

        if (previousState == NexusServerState.Disposed || previousState == NexusServerState.Stopped)
            return;

        await StopAsync().ConfigureAwait(false);
    }
    
    ValueTask IAcceptsExternalTransport.AcceptTransport(ITransport transport, CancellationToken cancellationToken)
    {
        // If the server is not running, don't accept connections.
        if(_state != NexusServerState.Running)
            return ValueTask.CompletedTask;

        if (_config == null)
            throw new InvalidOperationException(
                "Can't accept new transport before server configuration has been completed.");

        // Rate limit check for external transports
        string? remoteAddress = null;
        if (_rateLimiter != null)
        {
            remoteAddress = transport.RemoteAddress;
            var result = _rateLimiter.TryAcquire(remoteAddress);

            if (result != ConnectionRateLimitResult.Allowed)
            {
                _logger?.LogDebug($"External transport rejected: {result} from {remoteAddress ?? "unknown"}");
                _ = transport.CloseAsync(false);
                return ValueTask.CompletedTask;
            }
        }

        var baseSessionId = _sessionIdIncrementer++;

        _config!.InternalOnConnect?.Invoke();

        return RunSessionAsync(new NexusSessionConfigurations<TServerNexus, TClientProxy>()
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
            RateLimiter = _rateLimiter
        }, cancellationToken);
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
        var timeoutTicks = Environment.TickCount64 - _config!.Timeout;

        foreach (var session in _sessionManager.Sessions.LocalSessions)
            session.DisconnectIfTimeout(timeoutTicks);
    }
    
    private async Task ListenForConnectionsAsync()
    {
        try
        {
            _logger?.LogInfo("Listening for connections");
            while (_state == NexusServerState.Running)
            {
                var clientTransport = await _listener!.AcceptTransportAsync(_cancellationTokenSource!.Token).ConfigureAwait(false);

                if(clientTransport == null)
                    continue;

                // Rate limit check using ITransport.RemoteAddress
                string? remoteAddress = null;
                if (_rateLimiter != null)
                {
                    remoteAddress = clientTransport.RemoteAddress;
                    var result = _rateLimiter.TryAcquire(remoteAddress);

                    if (result != ConnectionRateLimitResult.Allowed)
                    {
                        _logger?.LogDebug($"Connection rejected: {result} from {remoteAddress ?? "unknown"}");
                        try
                        {
                            await clientTransport.CloseAsync(false).ConfigureAwait(false);
                        }
                        catch { /* ignore close errors */ }
                        continue;
                    }
                }

                _config!.InternalOnConnect?.Invoke();

                // Create a composite ID of the current ticks along with the current ticks.
                // This makes guessing IDs harder, but not impossible.
                var baseSessionId = _sessionIdIncrementer++;

                // boxed, but only once per client
                StartOnScheduler(
                    _config.ReceiveSessionPipeOptions.ReaderScheduler,
                    RunSessionAsync,
                    new NexusSessionConfigurations<TServerNexus, TClientProxy>()
                    {
                        ConnectionState = ConnectionState.Connecting,
                        Transport = clientTransport,
                        Pool = _poolManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        Id = GenerateSecureSessionId(baseSessionId),
                        Nexus = _nexusFactory!.Invoke(),
                        CollectionManager = _collectionManager,
                        Logger = _logger,
                        RateLimiterAddress = remoteAddress,
                        RateLimiter = _rateLimiter
                    });
            }
        }
        catch (NullReferenceException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _config!.Logger?.LogError(ex, "Server shut down.");
            await StopAsync().ConfigureAwait(false);
        }
    }

    private static void StartOnScheduler(PipeScheduler? scheduler, Action<object?> callback, in NexusSessionConfigurations<TServerNexus, TClientProxy>? state)
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
}
