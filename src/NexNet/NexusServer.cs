﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;

namespace NexNet;

/// <summary>
/// Class which manages session connections.
/// </summary>
/// <typeparam name="TServerNexus">The nexus which will be running locally on the server.</typeparam>
/// <typeparam name="TClientProxy">Proxy used to invoke methods on the remote nexus.</typeparam>
public sealed class NexusServer<TServerNexus, TClientProxy> : INexusServer<TClientProxy> 
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly SessionManager _sessionManager = new();
    private readonly Timer _watchdogTimer;
    private ServerConfig? _config;
    private Func<TServerNexus>? _nexusFactory;
    private readonly SessionCacheManager<TClientProxy> _cacheManager;
    private ITransportListener? _listener;
    private readonly ConcurrentBag<ServerNexusContext<TClientProxy>> _serverNexusContextCache = new();
    private TaskCompletionSource? _stoppedTcs;
    private NexusServerState _state = NexusServerState.Stopped;
    private CancellationTokenSource? _cancellationTokenSource;
    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    ConcurrentBag<ServerNexusContext<TClientProxy>> INexusServer<TClientProxy>.ServerNexusContextCache =>
        _serverNexusContextCache;

    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementer;
    private INexusLogger? _logger;
    
    /// <inheritdoc />
    public NexusServerState State => _state;

    /// <inheritdoc />
    public ServerConfig Config => _config ?? throw new InvalidOperationException("Nexus server has not been started yet.  Please setup with the parameterized constructor or invoke Configure().");

    /// <inheritdoc />
    public Task? StoppedTask => _stoppedTcs?.Task;

    /// <inheritdoc />
    public bool IsConfigured => _config != null;

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    public NexusServer(ServerConfig config, Func<TServerNexus> nexusFactory)
        : this()
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nexusFactory);
        _config = config;
        _nexusFactory = nexusFactory;
        _logger = config.Logger?.CreateLogger("NexusServer");
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
    }

    /// <summary>
    /// Configures the server after instancing.  This can only be executed a single time and with the
    /// <see cref="NexusServer{TServerNexus,TClientProxy}"/> paramaterless constructor.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    /// <remarks>
    /// Do not use this method.  Instead, use the parameterized constructor.
    /// </remarks>
    public void Configure(ServerConfig config, Func<TServerNexus> nexusFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nexusFactory);
        if(_config != null)
            throw new InvalidOperationException("Server has already been configured.");
        
        _config = config;
        _nexusFactory = nexusFactory;
        _logger = config.Logger?.CreateLogger("NexusServer");
    }

    /// <summary>
    /// Gets a nexus context which can be used outside the nexus.  Dispose after usage.
    /// </summary>
    /// <returns>Server nexus context for invocation of client methods.</returns>
    public ServerNexusContext<TClientProxy> GetContext()
    {
        if(!_serverNexusContextCache.TryTake(out var context))
            context = new ServerNexusContext<TClientProxy>(this, _sessionManager, _cacheManager);

        return context;
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

        // Only execute if the server is stopped and not disposed.
        if (Interlocked.CompareExchange(ref _state, NexusServerState.Running, NexusServerState.Stopped) != NexusServerState.Stopped)
            return;

        if (_listener != null) throw new InvalidOperationException("Server is already running");
        _cancellationTokenSource = new CancellationTokenSource();
        _stoppedTcs?.TrySetResult();
        _stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _listener = await _config.CreateServerListener(cancellationToken).ConfigureAwait(false);

        StartOnScheduler(_config.ReceiveSessionPipeOptions.ReaderScheduler, _ => FireAndForget(ListenForConnectionsAsync()), null);

        _watchdogTimer.Change(_config.Timeout / 4, _config.Timeout / 4);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, NexusServerState.Stopped, NexusServerState.Running) != NexusServerState.Running)
            return;

        _logger?.LogInfo("Stopping server");

        var listener = _listener;
        _listener = null;

        try
        {
            _cancellationTokenSource?.Cancel();
            _cacheManager.Clear();
            _watchdogTimer.Change(-1, -1);

            foreach (var session in _sessionManager.Sessions)
            {
                try
                {
                    await session.Value.DisconnectAsync(DisconnectReason.ServerShutdown).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Ignore exceptions
                    _config!.Logger?.LogError(e, $"Error while disconnecting session {session.Key}");
                }

            }
            
            // If the listener is null, then the incoming connections are not handled by a listener,
            // and we don't have any work to perform.
            if(listener != null)
                await listener.CloseAsync(!_config!.InternalNoLingerOnShutdown).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _stoppedTcs?.TrySetResult();
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
        if (_config == null)
            throw new InvalidOperationException(
                "Can't accept new transport before server configuration has been completed.");
        var baseSessionId = _sessionIdIncrementer++;
        
        _config!.InternalOnConnect?.Invoke();
        
        return RunClientAsync(new NexusSessionConfigurations<TServerNexus, TClientProxy>()
        {
            Transport = transport,
            Cache = _cacheManager,
            Configs = _config,
            SessionManager = _sessionManager,
            IsServer = true,
            Id = (long)baseSessionId << 32 | (uint)Random.Shared.Next(),
            Nexus = _nexusFactory!.Invoke()
        }, cancellationToken);
    }

    private static async void RunClientAsync(object? boxed)
    {
        try
        {
            var arguments = (NexusSessionConfigurations<TServerNexus, TClientProxy>)boxed!;
            await RunClientAsync(arguments);
        }
        catch (Exception e)
        {
            ((NexusSessionConfigurations<TServerNexus, TClientProxy>)boxed!).Configs.Logger
                ?.LogError(e, "Exception while running client");
        }
        
    }

    private static async ValueTask RunClientAsync(
        NexusSessionConfigurations<TServerNexus, TClientProxy> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = new NexusSession<TServerNexus, TClientProxy>(arguments);

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
                    RunClientAsync,
                    new NexusSessionConfigurations<TServerNexus, TClientProxy>()
                    {
                        Transport = clientTransport,
                        Cache = _cacheManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        IsServer = true,
                        Id = (long)baseSessionId << 32 | (uint)Random.Shared.Next(),
                        Nexus = _nexusFactory!.Invoke()
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
