using System;
using System.Collections.Concurrent;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet;
/// <summary>
/// Class which manages session connections.
/// </summary>
/// <typeparam name="TServerHub">The hub which will be running locally on the server.</typeparam>
/// <typeparam name="TClientProxy">Proxy used to invoke methods on remote hubs.</typeparam>
public sealed class NexNetServer<TServerHub, TClientProxy> : INexNetServer<TClientProxy>
    where TServerHub : ServerHubBase<TClientProxy>, IInvocationMethodHash
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly SessionManager _sessionManager = new();
    private readonly Timer _watchdogTimer;
    private readonly ServerConfig _config;
    private readonly Func<TServerHub> _hubFactory;
    private readonly SessionCacheManager<TClientProxy> _cacheManager;
    private ITransportListener? _listener;
    private readonly ConcurrentBag<ServerHubContext<TClientProxy>> _serverHubContextCache = new();
    private TaskCompletionSource? _stoppedTcs;

    /// <summary>
    /// Cache for all the server hub contexts.
    /// </summary>
    ConcurrentBag<ServerHubContext<TClientProxy>> INexNetServer<TClientProxy>.ServerHubContextCache =>
        _serverHubContextCache;

    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementer;

    /// <summary>
    /// True if the server is running, false otherwise.
    /// </summary>
    public bool IsStarted => _listener != null;

    /// <summary>
    /// Configurations the server us currently using.
    /// </summary>
    public ServerConfig Config => _config;

    /// <summary>
    /// Task completion source which completes upon the server stopping.
    /// </summary>
    public TaskCompletionSource? StoppedTcs => _stoppedTcs;

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="hubFactory">Factory called on each new connection.  Used to pass arguments to the hub.</param>
    public NexNetServer(ServerConfig config, Func<TServerHub> hubFactory)
    {
        _config = config;
        _hubFactory = hubFactory;

        _cacheManager = new SessionCacheManager<TClientProxy>();

        _watchdogTimer = new Timer(ConnectionWatchdog);

    }

    /// <summary>
    /// Gets a hub context which can be used outside of the hub.  Dispose after usage.
    /// </summary>
    /// <returns>Server hub context for invocation of client methods.</returns>
    public ServerHubContext<TClientProxy> GetContext()
    {
        if(!_serverHubContextCache.TryTake(out var context))
            context = new ServerHubContext<TClientProxy>(this, _sessionManager, _cacheManager);

        return context;
    }

    /// <summary>
    /// Starts the server.
    /// </summary>
    /// <exception cref="InvalidOperationException">Throws when the server is already running.</exception>
    public void Start()
    {
        if (_listener != null) throw new InvalidOperationException("Server is already running");
        _stoppedTcs = new TaskCompletionSource();
        _listener = _config.CreateServerListener();

        StartOnScheduler(_config.ReceivePipeOptions.ReaderScheduler, _ => FireAndForget(ListenForConnectionsAsync()), null);

        _watchdogTimer.Change(_config.Timeout / 4, _config.Timeout / 4);
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        if (listener != null)
        {
            try
            {
                _cacheManager.Clear();
                _watchdogTimer.Change(-1, -1);

                foreach (var session in _sessionManager.Sessions)
                {
                    session.Value.DisconnectAsync(DisconnectReason.ServerShutdown);
                }

                listener.Close(!_config.InternalNoLingerOnShutdown);
            }
            catch
            {
                // ignored
            }
        }

        _stoppedTcs?.SetResult();
    }

    /// <summary>
    /// Release any resources associated with this instance
    /// </summary>
    public void Dispose()
    {
        Stop();
    }

    private static async void RunClientAsync(object? boxed)
    {
        var arguments = (NexNetSessionConfigurations<TServerHub, TClientProxy>)boxed!;
        try
        {
            var session = new NexNetSession<TServerHub, TClientProxy>(arguments);

            await session.StartReadAsync().ConfigureAwait(false);

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
        /*finally
        {
            if (arguments.Transport is IDisposable d)
            {
                try
                {
                    d.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }*/
    }


    private void ConnectionWatchdog(object? state)
    {
        var timeoutTicks = Environment.TickCount64 - _config.Timeout;

        foreach (var session in _sessionManager.Sessions)
            session.Value.DisconnectIfTimeout(timeoutTicks);
    }
    
    private async Task ListenForConnectionsAsync()
    {
        try
        {
            while (true)
            {
                var clientTransport = await _listener!.AcceptTransportAsync().ConfigureAwait(false);

                if(clientTransport == null)
                    continue;

                _config.InternalOnConnect?.Invoke();

                // Create a composite ID of the current ticks along with the current ticks.
                // This makes guessing IDs harder, but not impossible.
                var baseSessionId = Interlocked.Increment(ref _sessionIdIncrementer);

                // boxed, but only once per client
                StartOnScheduler(
                    _config.ReceivePipeOptions.ReaderScheduler,
                    RunClientAsync,
                    new NexNetSessionConfigurations<TServerHub, TClientProxy>()
                    {
                        Transport = clientTransport,
                        Cache = _cacheManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        IsServer = true,
                        // ReSharper disable once RedundantCast
                        Id = (long)baseSessionId << 32 | (long)Environment.TickCount,
                        Hub = _hubFactory.Invoke()
                    });
            }
        }
        catch (NullReferenceException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _config.Logger?.LogError(ex, "Server shut down.");
            Stop();
        }
    }

    private static void StartOnScheduler(PipeScheduler? scheduler, Action<object?> callback, object? state)
    {
        if (scheduler == PipeScheduler.Inline) scheduler = null;
        (scheduler ?? PipeScheduler.ThreadPool).Schedule(callback, state);
    }

    private static void FireAndForget(Task? task)
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
}
