using System;
using System.Threading.Tasks;
using NexNet.Messages;
using System.Threading;
using NexNet.Transports;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet;

/// <summary>
/// Main client class which facilitates the communication with a matching NexNet server.
/// </summary>
/// <typeparam name="TClientNexus">Nexus used by this client for incoming invocation handling.</typeparam>
/// <typeparam name="TServerProxy">Server proxy implementation used for all remote invocations.</typeparam>
public sealed class NexusClient<TClientNexus, TServerProxy> : INexusClient
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly Timer _pingTimer;
    private readonly ClientConfig _config;
    private readonly SessionCacheManager<TServerProxy> _cacheManager;
    private readonly TClientNexus _nexus;
    private NexusSession<TClientNexus, TServerProxy>? _session;
    private TaskCompletionSource? _disconnectedTaskCompletionSource;

    internal NexusSession<TClientNexus, TServerProxy>? Session => _session;

    /// <summary>
    /// Current state of the connection
    /// </summary>
    public ConnectionState State => _session?.State ?? ConnectionState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>
    /// Proxy used for invoking remote methods on the server.
    /// </summary>
    public TServerProxy Proxy { get; private set; }

    /// <inheritdoc />
    public ClientConfig Config => _config;

    /// <inheritdoc />
    public Task DisconnectedTask => _disconnectedTaskCompletionSource?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Creates a NexNet client for communication with a matching NexNet server.
    /// </summary>
    /// <param name="config">Configurations for this client.</param>
    /// <param name="nexus">Nexus used for handling incoming invocations.</param>
    public NexusClient(ClientConfig config, TClientNexus nexus)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _cacheManager = new SessionCacheManager<TServerProxy>();

        Proxy = new TServerProxy() { CacheManager = _cacheManager };
        _nexus = nexus;
        _pingTimer = new Timer(PingTimer);
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.Exception != null)
                throw result.Exception;
            
            if(result.State == ConnectionResult.StateValue.AuthenticationFailed)
                throw new TransportException(TransportError.AuthenticationError, "Authentication failed.", result.Exception);
        }


    }

    /// <inheritdoc />
    public async Task<ConnectionResult> TryConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session != null)
            return new ConnectionResult(ConnectionResult.StateValue.Success);

        // Set the ready task completion source now and get the task since the ConnectTransport call below can/will await.
        // This TCS needs to run continuations asynchronously to avoid deadlocks on the receiving end.
        var readyTaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectedTaskCompletionSource = disconnectedTaskCompletionSource;
        ITransport transport;

        try
        {
            transport = await _config.ConnectTransport(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException e)
        {
            return new ConnectionResult(e.Error == TransportError.AuthenticationError
                ? ConnectionResult.StateValue.AuthenticationFailed
                : ConnectionResult.StateValue.Exception, e);
        }
        catch (Exception e)
        {
            return new ConnectionResult(ConnectionResult.StateValue.Exception, e);
        }

        _config.InternalOnClientConnect?.Invoke();

        var config = new NexusSessionConfigurations<TClientNexus, TServerProxy>()
        {
            Configs = _config,
            Transport = transport,
            Cache = _cacheManager,
            SessionManager = null,
            Client = this,
            Id = 0, // Initial value.  Not set by the client.
            Nexus = _nexus,
            ReadyTaskCompletionSource = readyTaskCompletionSource,
            DisconnectedTaskCompletionSource = disconnectedTaskCompletionSource
        };

        var session = _session = new NexusSession<TClientNexus, TServerProxy>(config)
        {
            OnDisconnected = OnDisconnected,
            OnReconnectingStatusChange = OnReconnectingStatusChange,
            OnStateChanged = (state) => StateChanged?.Invoke(this, state)
        };

        Proxy.Configure(session, null, ProxyInvocationMode.Caller, null);

        await session.StartAsClient(false).ConfigureAwait(false);

        await readyTaskCompletionSource.Task.ConfigureAwait(false);

        if (session.DisconnectReason != DisconnectReason.None)
        {
            session.OnStateChanged = null;
            return new ConnectionResult(session.DisconnectReason switch
            {
                DisconnectReason.Timeout => ConnectionResult.StateValue.Timeout,
                DisconnectReason.Authentication => ConnectionResult.StateValue.AuthenticationFailed,
                _ => ConnectionResult.StateValue.UnknownException
            });
        }

        _pingTimer.Change(_config.PingInterval, _config.PingInterval);

        return new ConnectionResult(ConnectionResult.StateValue.Success);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (_session == null)
            return;

        await _session.DisconnectAsync(DisconnectReason.Graceful).ConfigureAwait(false);

        if(_disconnectedTaskCompletionSource != null)
            await _disconnectedTaskCompletionSource.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the client and disconnects if the client is connected to a server. 
    /// Same as <see cref="DisconnectAsync"/>.
    /// </summary>
    /// <returns></returns>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IRentedNexusDuplexPipe CreatePipe()
    {
        var pipe = _session?.PipeManager.RentPipe();
        if (pipe == null)
            throw new InvalidOperationException("Client is not connected.");

        return pipe;
    }

    /// <inheritdoc />
    public INexusDuplexUnmanagedChannel<T> CreateUnmanagedChannel<T>()
        where T : unmanaged
    {
        return CreatePipe().GetUnmanagedChannel<T>();
    }

    /// <inheritdoc />
    public INexusDuplexChannel<T> CreateChannel<T>()
    {
        return CreatePipe().GetChannel<T>();
    }

    private void PingTimer(object? state)
    {
        var timeoutTicks = Environment.TickCount64 - _config.Timeout;

        // Check to see if we have timed out on receiving first.
        if (_session?.DisconnectIfTimeout(timeoutTicks) == true)
            return;

        _session?.SendHeader(MessageType.Ping);
    }

    private void OnDisconnected()
    {
        //_receiveLoopThread = null;
        _pingTimer.Change(-1, -1);
        _session = null;
    }
    
    
    private void OnReconnectingStatusChange(bool reconnected)
    {
        if (reconnected)
        {
            _pingTimer.Change(_config.PingInterval, _config.PingInterval);
        }
        else
        {
            _pingTimer.Change(-1, -1);
        }
    }
}
