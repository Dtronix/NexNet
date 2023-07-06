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

namespace NexNet;
/// <summary>
/// Class which manages session connections.
/// </summary>
/// <typeparam name="TServerNexus">The nexus which will be running locally on the server.</typeparam>
/// <typeparam name="TClientProxy">Proxy used to invoke methods on the remote nexus.</typeparam>
public sealed class NexusServer<TServerNexus, TClientProxy> : INexusServer<TClientProxy>
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly SessionManager _sessionManager = new();
    private readonly Timer _watchdogTimer;
    private readonly ServerConfig _config;
    private readonly Func<TServerNexus> _nexusFactory;
    private readonly SessionCacheManager<TClientProxy> _cacheManager;
    private ITransportListener? _listener;
    private readonly ConcurrentBag<ServerNexusContext<TClientProxy>> _serverNexusContextCache = new();

    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    ConcurrentBag<ServerNexusContext<TClientProxy>> INexusServer<TClientProxy>.ServerNexusContextCache =>
        _serverNexusContextCache;

    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementor;

    /// <summary>
    /// True if the server is running, false otherwise.
    /// </summary>
    public bool IsStarted => _listener != null;

    /// <summary>
    /// Configurations the server us currently using.
    /// </summary>
    public ServerConfig Config => _config;

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="nexusFactory">Factory called on each new connection.  Used to pass arguments to the nexus.</param>
    public NexusServer(ServerConfig config, Func<TServerNexus> nexusFactory)
    {
        _config = config;
        _nexusFactory = nexusFactory;

        _cacheManager = new SessionCacheManager<TClientProxy>();

        _watchdogTimer = new Timer(ConnectionWatchdog);

    }

    /// <summary>
    /// Gets a nexus context which can be used outside of the nexus.  Dispose after usage.
    /// </summary>
    /// <returns>Server nexus context for invocation of client methods.</returns>
    public ServerNexusContext<TClientProxy> GetContext()
    {
        if(!_serverNexusContextCache.TryTake(out var context))
            context = new ServerNexusContext<TClientProxy>(this, _sessionManager, _cacheManager);

        return context;
    }

    /// <summary>
    /// Starts the server.
    /// </summary>
    /// <exception cref="InvalidOperationException">Throws when the server is already running.</exception>
    public void Start()
    {
        if (_listener != null) throw new InvalidOperationException("Server is already running");
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
        var arguments = (NexusSessionConfigurations<TServerNexus, TClientProxy>)boxed!;
        try
        {
            var session = new NexusSession<TServerNexus, TClientProxy>(arguments);

            await session.StartReadAsync().ConfigureAwait(false);

            try
            {
                arguments.Transport.Input.Complete();
            }
            catch
            {
                // ignored
            }

            try
            {
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
                arguments.Transport.Input.Complete(ex);
            }
            catch
            {
                // ignored
            }

            try
            {
                arguments.Transport.Output.Complete(ex);
            }
            catch
            {
                // ignored
            }

            //OnClientFaulted(in client, ex);
        }
        finally
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
        }
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
                var baseSessionId = Interlocked.Increment(ref _sessionIdIncrementor);

                // boxed, but only once per client
                StartOnScheduler(
                    _config.ReceivePipeOptions.ReaderScheduler,
                    RunClientAsync,
                    new NexusSessionConfigurations<TServerNexus, TClientProxy>()
                    {
                        Transport = clientTransport,
                        Cache = _cacheManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        IsServer = true,
                        // ReSharper disable once RedundantCast
                        Id = (long)baseSessionId << 32 | (long)Environment.TickCount,
                        Nexus = _nexusFactory.Invoke()
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