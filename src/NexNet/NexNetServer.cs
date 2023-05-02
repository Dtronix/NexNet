using System;
using System.Net.Sockets;
using System.Threading;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Internals;
using Pipelines.Sockets.Unofficial;

namespace NexNet;

public sealed class NexNetServer<TServerHub, TClientProxy>
    where TServerHub : ServerHubBase<TClientProxy>, IInterfaceMethodHash
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, IInterfaceMethodHash, new()
{
    private readonly SessionManager _sessionManager = new();
    private readonly Timer _watchdogTimer;
    private readonly ServerConfig _config;
    private readonly Func<TServerHub> _hubFactory;
    private readonly SessionCacheManager<TClientProxy> _cacheManager;

    // ReSharper disable once StaticMemberInGenericType
    private static int _sessionIdIncrementor;
    public bool IsStarted => _listener != null;

    private Socket? _listener;

    private readonly int _clientTimeout;

    public ServerConfig Config => _config;

    /// <summary>
    /// Creates a NexNetServer class for handling incoming connections.
    /// </summary>
    /// <param name="config">Server configurations</param>
    /// <param name="hubFactory">Factory called on each new connection.  Used to pass arguments to the hub.</param>
    public NexNetServer(ServerConfig config, Func<TServerHub> hubFactory)
    {
        _config = config;
        _hubFactory = hubFactory;
        _clientTimeout = _config.ClientTimeout;

        _cacheManager = new SessionCacheManager<TClientProxy>();

        _watchdogTimer = new Timer(ConnectionWatchdog);
    }


    public bool Start()
    {
        if (_listener != null) throw new InvalidOperationException("Server is already running");
        Socket listener = new Socket(_config.SocketAddressFamily, _config.SocketType, _config.SocketProtocolType);
        listener.Bind(_config.SocketEndPoint);
        listener.Listen(_config.AcceptorBacklog);

        _listener = listener;
        StartOnScheduler(_config.ReceivePipeOptions?.ReaderScheduler, _ => FireAndForget(ListenForConnectionsAsync()), null);

        _watchdogTimer.Change(_clientTimeout / 4, _clientTimeout / 4);
        return true;
    }

    public void Stop()
    {

        var socket = _listener;
        _listener = null;
        if (socket != null)
        {
            try
            {
                _cacheManager.Clear();
                _watchdogTimer.Change(-1, -1);

                foreach (var session in _sessionManager.Sessions)
                {
                    session.Value.DisconnectAsync(DisconnectReason.DisconnectServerShutdown);
                }

                if (_config.InternalNoLingerOnShutdown)
                {
                    socket.LingerState = new LingerOption(true, 0);
                    socket.Close(0);
                }
                else
                {
                    socket.Dispose();
                }

            }
            catch { }
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
        var arguments = (NexNetSessionConfigurations<TServerHub, TClientProxy>)boxed!;
        try
        {
            var session = new NexNetSession<TServerHub, TClientProxy>(arguments);

            await session.StartAsServer().ConfigureAwait(false);

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
        var currentTicks = Environment.TickCount64 - _clientTimeout;

        foreach (var ipcSession in _sessionManager.Sessions)
        {
            var session = ipcSession.Value;
            if (currentTicks > session.LastReceived)
            {
                _config.Logger?.LogTrace($"[IpcServer] Timed out session {session.Id}");
                session.DisconnectAsync(DisconnectReason.Timeout);
                
            }

        }
    }
    
    private async Task ListenForConnectionsAsync()
    {
        try
        {
            while (true)
            {
                var clientSocket = await _listener!.AcceptAsync().ConfigureAwait(false);
                SocketConnection.SetRecommendedServerOptions(clientSocket);

                ITransportBase transport;
                try
                {
                    transport = await _config.CreateTransport(clientSocket);
                }
                catch (Exception e)
                {
                    _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");

                    // Immediate disconnect.
                    clientSocket.LingerState = new LingerOption(true, 0);
                    clientSocket.Close(0);
                    return;
                }


                _config.InternalOnConnect?.Invoke();

                // Create a composite ID of the current ticks along with the current ticks.
                // This makes guessing IDs harder, but not impossible.
                var baseSessionId = Interlocked.Increment(ref _sessionIdIncrementor);

                // boxed, but only once per client
                StartOnScheduler(
                    _config.ReceivePipeOptions.ReaderScheduler,
                    RunClientAsync,
                    new NexNetSessionConfigurations<TServerHub, TClientProxy>()
                    {
                        Transport = transport,
                        Cache = _cacheManager,
                        Configs = _config,
                        SessionManager = _sessionManager,
                        IsServer = true,
                        Id = (long)baseSessionId << 32 | (long)Environment.TickCount,
                        Hub = _hubFactory.Invoke()
                    });
            }
        }
        catch (NullReferenceException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { OnServerFaulted(ex); }
    }

    /// <summary>
    /// Invoked when the server has faulted
    /// </summary>
    private void OnServerFaulted(Exception exception)
    {

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
