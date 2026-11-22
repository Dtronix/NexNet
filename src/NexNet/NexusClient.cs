using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Messages;
using System.Threading;
using NexNet.Transports;
using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.Pools;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Pipes;

namespace NexNet;

/// <summary>
/// Main client class which facilitates the communication with a matching NexNet server.
/// </summary>
/// <typeparam name="TClientNexus">Nexus used by this client for incoming invocation handling.</typeparam>
/// <typeparam name="TServerProxy">Server _proxy implementation used for all remote invocations.</typeparam>
public sealed class NexusClient<TClientNexus, TServerProxy> : INexusClient
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private long _id;
    private ITimer? _pingTimer;
    private readonly ClientConfig _config;
    private readonly SessionPoolManager<TServerProxy> _poolManager;
    private readonly TClientNexus _nexus;
    private NexusSession<TClientNexus, TServerProxy>? _session;
    private TaskCompletionSource<DisconnectReason>? _disconnectedTaskCompletionSource;
    private readonly NexusCollectionManager _collectionManager;
    private bool _isReconnecting = false;
    private volatile bool _reconnectLoopActive;

    // Guards _session's assignment, the reconnection loop's ownership and the state changes the client raises itself,
    // so a DisconnectAsync, a ConnectAsync and a reconnection attempt agree on who owns the connection.
    private readonly object _connectionLock = new();

    // Counts DisconnectAsync calls. A session records the count it was created under; the reconnection loop it starts
    // runs only while the count is unchanged, so a DisconnectAsync stops a loop at any point (between attempts, during
    // an attempt's handshake or right after it connected) and a loop that has not started yet never starts.
    private int _disconnectRequests;

    // Cancels the running reconnection loop's delay and connect; taken and canceled by DisconnectAsync.
    private CancellationTokenSource? _reconnectLoop;

    // The last state change raised, so the client ends Disconnected exactly once when a loop stops.
    private ConnectionState _lastRaisedState = ConnectionState.Disconnected;
    private readonly INexusLogger? _logger;
    private readonly MetricsTagSet _metricsTags;
    private int _plaintextWarningLogged;
    private int _pingInterval;

    internal NexusSession<TClientNexus, TServerProxy>? Session => _session;

    /// <summary>
    /// Test hook: awaited by a connect attempt once its session's ready task completes (the handshake completed or the
    /// session ended), before the attempt reads the outcome.
    /// </summary>
    internal Func<Task>? InternalOnSessionReady { get; set; }
    
    /// <summary>
    /// Provides access to the client's nexus.
    /// </summary>
    public TClientNexus Nexus => _nexus;

    /// <summary>
    /// Current state of the connection. While the reconnection loop runs (between the loss of a session and either a
    /// new connected session or the end of the loop) it is <see cref="ConnectionState.Reconnecting"/>.
    /// </summary>
    public ConnectionState State
    {
        get
        {
            var state = _session?.State ?? ConnectionState.Disconnected;
            return _reconnectLoopActive && state != ConnectionState.Connected ? ConnectionState.Reconnecting : state;
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionState>? StateChanged;


    private TServerProxy _proxy;

    /// <summary>
    /// Proxy used for invoking remote methods on the server.
    /// </summary>
    public TServerProxy Proxy
    {
        get
        {
            if(_session == null)
                throw new InvalidOperationException("Client not connected");

            return _proxy;
        }
    }

    /// <inheritdoc />
    public ClientConfig Config => _config;

    /// <inheritdoc />
    public bool IsSecure => _config.IsSecureTransport;

    /// <summary>
    /// Snapshot of the current session's live state, or null when not connected. Not an atomic picture; exposes
    /// internal state, so do not serve it to unauthenticated callers. See docs/articles/metrics.md.
    /// </summary>
    public NexusSessionSnapshot? GetSnapshot() => _session?.GetSnapshot() is { } snapshot
        ? snapshot with
        {
            IsSecure = IsSecure,
            PingInterval = Volatile.Read(ref _pingInterval) is var ping and > 0 ? TimeSpan.FromMilliseconds(ping) : null
        }
        : null;

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
        config.ValidateFlowControl();
        config.ValidateAuthentication();

        var id = Interlocked.Increment(ref _id);
        _config = config;
        _logger = config.Logger?.CreateLogger($"CL{id}");
        _poolManager = new SessionPoolManager<TServerProxy>();
        _metricsTags = NexusMetrics.GetTagSet(config);

        // Set the collection manager and configure for this nexus.
        _collectionManager = new NexusCollectionManager(_logger, false, _config.Time);
        TClientNexus.ConfigureCollections(_collectionManager);

        _proxy = new TServerProxy() { PoolManager = _poolManager };
        _nexus = nexus;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.Exception != null)
                throw result.Exception;
            
            if(result.State == ConnectionResult.StateValue.Disconnected 
               && result.DisconnectReason == DisconnectReason.Authentication)
                throw new TransportException(TransportError.AuthenticationError, "Authentication failed.", result.Exception);
        }
    }

    /// <inheritdoc />
    public Task<ConnectionResult> TryConnectAsync(CancellationToken cancellationToken = default)
    {
        return TryConnectAsyncCore(false, 0, cancellationToken);
    }

    /// <param name="isReconnecting">True for an attempt of the reconnection loop.</param>
    /// <param name="disconnectRequests">
    /// For a reconnection attempt, the <see cref="_disconnectRequests"/> count its loop runs under: the attempt opens
    /// no session once a DisconnectAsync changed it. Unused otherwise.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    private async Task<ConnectionResult> TryConnectAsyncCore(bool isReconnecting, int disconnectRequests, CancellationToken cancellationToken = default)
    {
        if (_session != null)
            return new ConnectionResult(ConnectionResult.StateValue.Success, DisconnectReason.None);

        if (isReconnecting && Volatile.Read(ref _disconnectRequests) != disconnectRequests)
            return new ConnectionResult(ConnectionResult.StateValue.Canceled, DisconnectReason.None);

        _isReconnecting = isReconnecting;

        LogPlaintextWarningOnce();

        // The token is acquired before the transport connects, so a slow provider does not run down the server's
        // handshake timeout and a failing one opens no connection.
        Memory<byte> authenticationToken;
        try
        {
            authenticationToken = await _config.GetAuthenticationToken(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
        {
            return new ConnectionResult(ConnectionResult.StateValue.Canceled, DisconnectReason.None, e);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not acquire the authentication token.");
            return new ConnectionResult(ConnectionResult.StateValue.Exception, DisconnectReason.Authentication, e);
        }

        ITransport transport;

        try
        {
            transport = await _config.ConnectTransport(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException e)
        {
            return new ConnectionResult(ConnectionResult.StateValue.Disconnected,
                e.Error == TransportError.AuthenticationError
                    ? DisconnectReason.Authentication
                    : DisconnectReason.SocketError, e);
        }
        catch (Exception e)
        {
            return new ConnectionResult(ConnectionResult.StateValue.Exception, DisconnectReason.SocketError, e);
        }

        // The disconnected task of this attempt is published only once the session is created: from here on the
        // session completes it. A failed transport connect leaves DisconnectedTask on the previous, completed session
        // (or Task.CompletedTask) instead of on a task nothing would complete.
        // These TCSs need to run continuations asynchronously to avoid deadlocks on the receiving end.
        var readyTaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTaskCompletionSource = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);

        var config = new NexusSessionConfigurations<TClientNexus, TServerProxy>()
        {
            ConnectionState = isReconnecting ? ConnectionState.Reconnecting : ConnectionState.Connecting, 
            Configs = _config,
            Transport = transport,
            Pool = _poolManager,
            SessionManager = null,
            Client = this,
            Id = 0, // Initial value.  Not set by the client.
            Nexus = _nexus,
            ReadyTaskCompletionSource = readyTaskCompletionSource,
            DisconnectedTaskCompletionSource = disconnectedTaskCompletionSource,
            CollectionManager = _collectionManager,
            Logger = _logger,
            MetricsTags = _metricsTags
        };

        var sessionConnected = false;
        NexusSession<TClientNexus, TServerProxy>? session = null;
        var stopped = false;
        lock (_connectionLock)
        {
            // Checked and assigned together: a reconnection attempt and an application ConnectAsync that both passed
            // the check at the top must not both open a session, and an attempt must not open one after a
            // DisconnectAsync stopped its loop.
            if (isReconnecting && _disconnectRequests != disconnectRequests)
            {
                stopped = true;
            }
            else if (_session == null)
            {
                // The session's loss starts a reconnection loop only if no DisconnectAsync is made after this point.
                disconnectRequests = _disconnectRequests;
                _disconnectedTaskCompletionSource = disconnectedTaskCompletionSource;
                session = _session = new NexusSession<TClientNexus, TServerProxy>(config)
                {
                    OnDisconnected = () => OnDisconnected(session!),
                    OnStateChanged = OnSessionStateChanged,
                };

                void OnSessionStateChanged(ConnectionState state)
                {
                    lock (_connectionLock)
                    {
                        if (state == ConnectionState.Connected)
                        {
                            sessionConnected = true;
                            _reconnectLoopActive = false;
                        }
                        else if (!sessionConnected && _reconnectLoopActive)
                        {
                            // A reconnection attempt that never connected: the client stays Reconnecting until the loop
                            // connects or gives up, so the attempt's own Disconnecting/Disconnected are not reported.
                            return;
                        }

                        _lastRaisedState = state;
                    }

                    StateChanged?.Invoke(this, state);
                }
            }
        }

        if (session == null)
        {
            // Stopped by a DisconnectAsync, or another connect opened the session first.
            try
            {
                await transport.CloseAsync(false).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger?.LogDebug(e, "Error while closing the transport of an abandoned connect attempt.");
            }

            return stopped
                ? new ConnectionResult(ConnectionResult.StateValue.Canceled, DisconnectReason.None)
                : new ConnectionResult(ConnectionResult.StateValue.Success, DisconnectReason.None);
        }

        _ = disconnectedTaskCompletionSource.Task.ContinueWith(task => ConnectionClosed(task, disconnectRequests), cancellationToken);

        _proxy.Configure(session, null, ProxyInvocationMode.Caller, null);

        await session.StartAsClient(authenticationToken).ConfigureAwait(false);

        await readyTaskCompletionSource.Task.ConfigureAwait(false);

        if (InternalOnSessionReady is { } onSessionReady)
            await onSessionReady().ConfigureAwait(false);

        if (session.DisconnectReason != DisconnectReason.None)
        {
            session.OnStateChanged = null;
            return new ConnectionResult(ConnectionResult.StateValue.Disconnected, session.DisconnectReason);
        }

        // The interval comes from the server's advertised idle timeout unless the config sets one.
        var serverIdleTimeout = session.PeerIdleTimeoutMs;
        var pingInterval = _config.GetEffectivePingInterval(serverIdleTimeout);
        Volatile.Write(ref _pingInterval, pingInterval);
        if (_config.PingInterval != null && serverIdleTimeout != 0 && pingInterval >= serverIdleTimeout)
        {
            _logger?.LogWarning(
                $"PingInterval ({pingInterval} ms) is not below the server's idle timeout ({serverIdleTimeout} ms); the server may time the session out between pings.");
        }

        // Construct the ping timer here so it uses whatever TimeProvider is on _config at connect-time.
        // Disposing any prior timer first lets reconnects pick up a fresh provider if Config.Time was swapped.
        _pingTimer?.Dispose();
        _pingTimer = _config.Time.CreateTimer(PingTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pingTimer.Change(TimeSpan.FromMilliseconds(pingInterval), TimeSpan.FromMilliseconds(pingInterval));

        return new ConnectionResult(ConnectionResult.StateValue.Success, DisconnectReason.None);
    }

    /// <param name="task">The disconnected task of the session that ended.</param>
    /// <param name="disconnectRequests">The <see cref="_disconnectRequests"/> count the session was created under.</param>
    private async Task ConnectionClosed(Task<DisconnectReason> task, int disconnectRequests)
    {
        if (_isReconnecting)
            return;
        
        if (!task.IsCompletedSuccessfully)
            return;
        if (_config.ReconnectionPolicy == null)
        {
            _config.Logger?.LogTrace("No reconnection policy configured.");
            return;
        }
        var reason = task.Result;
        
        // The policy decides which disconnect reasons are reconnected for.
        if (!_config.ReconnectionPolicy.ShouldReconnect(reason))
        {
            _config.Logger?.LogInfo($"Can't reconnect with {reason} disconnection reason.");
            return;
        }

        var clientHub = Unsafe.As<ClientNexusBase<TServerProxy>>(_nexus);

        // The client is Reconnecting from here until an attempt connects, the policy gives up or DisconnectAsync stops
        // the loop. A DisconnectAsync made since the session was created means the loop does not start at all.
        var loop = new CancellationTokenSource();
        lock (_connectionLock)
        {
            if (_disconnectRequests != disconnectRequests || _reconnectLoop != null)
                return;

            _reconnectLoop = loop;
            _reconnectLoopActive = true;
            _lastRaisedState = ConnectionState.Reconnecting;
        }

        _config.Logger?.LogInfo($"Connection Lost. Reconnecting. {reason}");
        OnReconnectingStatusChange(false);
        StateChanged?.Invoke(this, ConnectionState.Reconnecting);
        var reconnected = false;
        try
        {
            // Notify the hub.
            await clientHub.Reconnecting().ConfigureAwait(false);
            var count = 0;

            while (!IsStopped())
            {
                // Get the next delay or cancellation.
                var delay = _config.ReconnectionPolicy.ReconnectDelay(count++);

                if (delay == null)
                    return;

                try
                {
                    await Task.Delay(delay.Value, _config.Time, loop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (loop.IsCancellationRequested)
                {
                    return;
                }

                if (IsStopped())
                    return;

                _config.ReconnectionPolicy.FireReconnection(this, count);

                _config.Logger?.LogTrace($"Reconnection attempt {count}");

                var result = await TryConnectAsyncCore(true, disconnectRequests, loop.Token).ConfigureAwait(false);

                // A DisconnectAsync during the attempt (its handshake included) ends the loop whatever the outcome:
                // the session it disconnected must not be retried.
                if (IsStopped())
                    return;

                if (result.Success)
                {
                    reconnected = true;
                    _isReconnecting = false;
                    _config.Logger?.LogInfo("Reconnected");
                    return;
                }

                if (result.Exception != null)
                {
                    _config.Logger?.LogError(result.Exception, $"Reconnection failed with exception. State: {result.State}");
                }
            }
        }
        finally
        {
            // The loop gave up (or failed): the client is Disconnected again. A loop that DisconnectAsync stopped no
            // longer owns the state; DisconnectAsync ends it.
            var raiseDisconnected = false;
            lock (_connectionLock)
            {
                if (_reconnectLoop == loop)
                {
                    _reconnectLoop = null;
                    if (!reconnected && _reconnectLoopActive)
                    {
                        _reconnectLoopActive = false;
                        raiseDisconnected = _lastRaisedState != ConnectionState.Disconnected;
                        _lastRaisedState = ConnectionState.Disconnected;
                    }
                }
            }

            if (raiseDisconnected)
                StateChanged?.Invoke(this, ConnectionState.Disconnected);
        }

        bool IsStopped() => Volatile.Read(ref _disconnectRequests) != disconnectRequests;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Also stops a running reconnection loop, whether it is waiting between attempts or an attempt is connecting:
    /// the client then ends <see cref="ConnectionState.Disconnected"/> and does not reconnect.
    /// </remarks>
    public async Task DisconnectAsync()
    {
        NexusSession<TClientNexus, TServerProxy>? session;
        TaskCompletionSource<DisconnectReason>? disconnected;
        CancellationTokenSource? loop;
        bool wasReconnecting;
        lock (_connectionLock)
        {
            _disconnectRequests++;
            session = _session;
            disconnected = _disconnectedTaskCompletionSource;
            loop = _reconnectLoop;
            _reconnectLoop = null;
            wasReconnecting = _reconnectLoopActive;
            _reconnectLoopActive = false;
        }

        // Wakes the loop from its delay or its attempt's connect; it sees the request and ends without raising a state.
        loop?.Cancel();

        if (session != null)
        {
            await session.DisconnectAsync(DisconnectReason.Graceful).ConfigureAwait(false);

            if (disconnected != null)
                await disconnected.Task.ConfigureAwait(false);
        }

        if (!wasReconnecting)
            return;

        // The loop was stopped while Reconnecting: between attempts, or during an attempt whose session never reported
        // its own state. The client ends Disconnected, once.
        bool raise;
        lock (_connectionLock)
        {
            raise = _lastRaisedState != ConnectionState.Disconnected;
            if (raise)
                _lastRaisedState = ConnectionState.Disconnected;
        }

        if (raise)
            StateChanged?.Invoke(this, ConnectionState.Disconnected);
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
    public INexusDuplexChannel<T> CreateChannel<T>()
    {
        return CreatePipe().GetChannel<T>();
    }

    private void LogPlaintextWarningOnce()
    {
        if (_config.IsSecureTransport || Interlocked.Exchange(ref _plaintextWarningLogged, 1) != 0)
            return;

        // A configuration type that does not set TransportName (a custom transport) is named by its type.
        var transport = _config switch
        {
            HttpClientConfig { Url: { } url } => $"{url.Scheme}://",
            { TransportName: "custom" } => _config.GetType().Name,
            _ => _config.TransportName,
        };
        _logger?.LogWarning(
            $"NexNet: unencrypted transport ({transport}); credentials and data are visible on the network.");
    }

    private void PingTimer(object? state)
    {
        var timeoutTicks = _config.Time.GetTickCount64() - _config.Timeout;

        // Check to see if we have timed out on receiving first.
        if (_session?.DisconnectIfTimeout(timeoutTicks) == true)
            return;

        _session?.SendHeader(MessageType.Ping);
    }

    private void OnDisconnected(NexusSession<TClientNexus, TServerProxy> session)
    {
        // Only the session that ended is cleared, never one that a later connect already opened.
        lock (_connectionLock)
        {
            if (_session != session)
                return;

            _session = null;
        }

        _pingTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }


    private void OnReconnectingStatusChange(bool reconnected)
    {
        if (reconnected)
        {
            var pingInterval = TimeSpan.FromMilliseconds(Volatile.Read(ref _pingInterval));
            _pingTimer?.Change(pingInterval, pingInterval);
        }
        else
        {
            _pingTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
