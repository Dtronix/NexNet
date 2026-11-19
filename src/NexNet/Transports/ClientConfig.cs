using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base client configurations.
/// </summary>
public abstract class ClientConfig : ConfigBase
{
    /// <summary>
    /// Number of milliseconds before the connection cancels.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 50_000;

    /// <summary>
    /// Ping interval used when <see cref="PingInterval"/> is not set and the server advertises no idle timeout.
    /// </summary>
    internal const int DefaultPingInterval = 10_000;

    /// <summary>
    /// Shortest ping interval derived from the server's advertised idle timeout.
    /// </summary>
    internal const int MinimumDerivedPingInterval = 1_000;

    private int? _pingInterval;

    /// <summary>
    /// Number of milliseconds between ping messages sent to the server, or null (the default) to derive it from the
    /// server.
    /// </summary>
    /// <remarks>
    /// When null, the client pings at a third of the idle timeout the server advertises in its greeting (its
    /// endpoint's <see cref="ConfigBase.Timeout"/>), at least every 1000 ms; a server that advertises none is pinged
    /// every 10000 ms. A value set here is used as is; if it is not below the server's advertised idle timeout, the
    /// client logs a warning on connect, because the server will time the session out between pings.
    /// The interval in use is in <c>NexusSessionSnapshot.PingInterval</c> of the client's snapshot.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? PingInterval
    {
        get => _pingInterval;
        set
        {
            if (value is { } interval)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval, nameof(PingInterval));
            _pingInterval = value;
        }
    }

    /// <summary>
    /// Returns the ping interval in milliseconds for a server that advertised <paramref name="serverIdleTimeoutMs"/>
    /// (0 when it advertised none).
    /// </summary>
    internal int GetEffectivePingInterval(uint serverIdleTimeoutMs)
    {
        if (_pingInterval is { } explicitInterval)
            return explicitInterval;

        if (serverIdleTimeoutMs == 0)
            return DefaultPingInterval;

        return (int)Math.Clamp(serverIdleTimeoutMs / 3, MinimumDerivedPingInterval, int.MaxValue);
    }

    /// <summary>
    /// Policy for reconnecting to the server upon connection closing.
    /// Set to null to disable this functionality.
    /// </summary>
    public IReconnectionPolicy? ReconnectionPolicy { get; set; } = null;

    /// <summary>
    /// Method called to pass data to the server upon connection.  If not overridden,
    /// the client will not pass any authentication information to the server.
    /// </summary>
    /// <remarks>
    /// Called before every connect and reconnect. Mutually exclusive with <see cref="AuthenticateAsync"/>.
    /// </remarks>
    public Func<Memory<byte>>? Authenticate { get; set; }

    /// <summary>
    /// Asynchronous form of <see cref="Authenticate"/>: returns the token sent to the server in the greeting, for
    /// example a token fetched or refreshed from an identity provider.
    /// </summary>
    /// <remarks>
    /// Called before every connect and reconnect, before the transport connects. It must complete within
    /// <see cref="ConfigBase.HandshakeTimeout"/>; the token passed to it is canceled at that point or when the
    /// connect is canceled. If it throws or times out, the connect fails with
    /// <see cref="NexNet.Messages.DisconnectReason.Authentication"/> and no connection is opened.
    /// Setting both this and <see cref="Authenticate"/> is a configuration error.
    /// </remarks>
    public Func<CancellationToken, ValueTask<Memory<byte>>>? AuthenticateAsync { get; set; }

    /// <summary>
    /// True when the transport this configuration connects with encrypts the connection or is local to the
    /// machine (TLS, WSS, HTTPS, QUIC and Unix domain sockets). False for plain TCP, <c>ws://</c> and
    /// <c>http://</c>. Defaults to false, so a transport that does not override this is treated as plaintext and
    /// the client logs the plaintext warning for it.
    /// </summary>
    protected internal virtual bool IsSecureTransport => false;

    /// <summary>
    /// Throws when both <see cref="Authenticate"/> and <see cref="AuthenticateAsync"/> are set.
    /// </summary>
    internal void ValidateAuthentication()
    {
        if (Authenticate != null && AuthenticateAsync != null)
            throw new InvalidOperationException(
                $"Set either {nameof(Authenticate)} or {nameof(AuthenticateAsync)} on the client config, not both.");
    }

    /// <summary>
    /// Returns the greeting authentication token from <see cref="Authenticate"/> or <see cref="AuthenticateAsync"/>,
    /// bounded by <see cref="ConfigBase.HandshakeTimeout"/> on <see cref="ConfigBase.Time"/>.
    /// </summary>
    /// <exception cref="TransportException">
    /// <see cref="TransportError.AuthenticationError"/> when <see cref="AuthenticateAsync"/> does not complete in time.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    internal async ValueTask<Memory<byte>> GetAuthenticationToken(CancellationToken cancellationToken)
    {
        ValidateAuthentication();

        var authenticateAsync = AuthenticateAsync;
        if (authenticateAsync == null)
            return Authenticate?.Invoke() ?? Memory<byte>.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var timeout = TimeSpan.FromMilliseconds(HandshakeTimeout);
        using var timeoutCts = new CancellationTokenSource(timeout, Time);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var pending = authenticateAsync(linkedCts.Token);
            if (pending.IsCompletedSuccessfully)
                return pending.Result;

            // WaitAsync bounds the wait even when the delegate ignores its token.
            return await pending.AsTask().WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(TransportError.AuthenticationError,
                $"{nameof(AuthenticateAsync)} did not complete within {nameof(HandshakeTimeout)} ({HandshakeTimeout} ms).", null);
        }
    }

    /// <summary>
    /// Returns the transport configured and connected for the overridden configurations.
    /// </summary>
    /// <returns>Connected transport.</returns>
    /// <param name="cancellationToken">Cancellation token used to cancel the connection.</param>
    /// <exception cref="TransportException">Throws socket exception upon failure to connect.</exception>
    internal ValueTask<ITransport> ConnectTransport(CancellationToken cancellationToken)
    {
        return OnConnectTransport(cancellationToken);
    }

    /// <summary>
    /// Override to return the transport configured and connected for the overridden configurations.
    /// </summary>
    /// <returns>Connected transport.</returns>
    /// <param name="cancellationToken">Cancellation token used to cancel the connection.</param>
    /// <exception cref="TransportException">Throws socket exception upon failure to connect.</exception>
    protected abstract ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken);
}
