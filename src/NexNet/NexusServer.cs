using System;
using System.Collections.Concurrent;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals;
using NexNet.Logging;

namespace NexNet;

/// <summary>
/// Class which manages session connections.
/// </summary>
/// <typeparam name="TServerNexus">The nexus which will be running locally on the server.</typeparam>
/// <typeparam name="TClientProxy">Proxy used to invoke methods on the remote nexus.</typeparam>
public sealed class NexusServer<TServerNexus, TClientProxy> : INexusServer<TClientProxy> 
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly SessionManager _sessionManager = new();
    private readonly Timer _watchdogTimer;
    private ServerConfig? _config;
    private Func<TServerNexus>? _nexusFactory;
    private readonly SessionCacheManager<TClientProxy> _cacheManager;
    private ITransportListener? _listener;
    private TaskCompletionSource? _stoppedTcs;
    private NexusServerState _state = NexusServerState.Stopped;
    private CancellationTokenSource? _cancellationTokenSource;
    
    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementer;
    private INexusLogger? _logger;
    private NexusCollectionManager _collectionManager = null!;
    private Func<TServerNexus, ValueTask>? _configureCollections;

    /// <inheritdoc />
    public NexusServerState State => _state;

    /// <inheritdoc />
    public ServerConfig Config => _config ?? throw new InvalidOperationException("Nexus server has not been started yet.  Please setup with the parameterized constructor or invoke Configure().");

    /// <inheritdoc />
    public Task? StoppedTask => _stoppedTcs?.Task;

    /// <inheritdoc />
    public bool IsConfigured => _config != null;
    
    /// <inheritdoc />
    public ServerNexusContextProvider<TClientProxy> ContextProvider { get; }

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    /// <param name="configureCollections">Function to configure collections.</param>
    public NexusServer(
        ServerConfig config, 
        Func<TServerNexus> nexusFactory, 
        Func<TServerNexus, ValueTask>? configureCollections = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nexusFactory);

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
        _cacheManager = new SessionCacheManager<TClientProxy>();
        _watchdogTimer = new Timer(ConnectionWatchdog);
        ContextProvider = new ServerNexusContextProvider<TClientProxy>(_sessionManager, _cacheManager);
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
        Func<TServerNexus, ValueTask>? configureCollections)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nexusFactory);
        if(_config != null)
            throw new InvalidOperationException("Server has already been configured.");
        
        _config = config;
        _nexusFactory = nexusFactory;
        _logger = config.Logger?.CreateLogger("NexusServer");
        _configureCollections = configureCollections;
        
        // Set the collection manager and configure for this nexus.
        _collectionManager = new NexusCollectionManager(config);
        TServerNexus.ConfigureCollections(_collectionManager);
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

        if (_configureCollections != null)
        {
            var configNexus = _nexusFactory!.Invoke();
            // Add a special context used for only configuring collections.  Any other usage of methods throws.
            configNexus.SessionContext = new ConfigurerSessionContext<TClientProxy>(_collectionManager);
            try
            {
                await _configureCollections.Invoke(configNexus).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _config.Logger?.LogError(e, "Exception while configuring collections.");
            }
        }

        _watchdogTimer.Change(_config.Timeout / 4, _config.Timeout / 4);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, NexusServerState.Stopped, NexusServerState.Running) != NexusServerState.Running)
            throw new InvalidOperationException("Server is not running");

        _logger?.LogInfo("Stopping server");

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
        
        foreach (var session in _sessionManager.Sessions)
        {
            try
            {
                await session.Value.DisconnectAsync(DisconnectReason.ServerShutdown).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Ignore exceptions
                _config!.Logger?.LogError(e, $"Error while disconnecting session {session.Key} due to server shutdown.");
            }
        }
        
        _cacheManager.Clear();
        
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
        var baseSessionId = _sessionIdIncrementer++;
        
        _config!.InternalOnConnect?.Invoke();
        
        return RunSessionAsync(new NexusSessionConfigurations<TServerNexus, TClientProxy>()
        {
            ConnectionState = ConnectionState.Connecting,
            Transport = transport,
            Cache = _cacheManager,
            Configs = _config,
            SessionManager = _sessionManager,
            Client = null,
            Id = (long)baseSessionId << 32 | (uint)Random.Shared.Next(),
            Nexus = _nexusFactory!.Invoke(),
            CollectionManager = _collectionManager
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

        foreach (var session in _sessionManager.Sessions)
            session.Value.DisconnectIfTimeout(timeoutTicks);
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
                        Cache = _cacheManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        Id = (long)baseSessionId << 32 | (uint)Random.Shared.Next(),
                        Nexus = _nexusFactory!.Invoke(),
                        CollectionManager = _collectionManager
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


}
