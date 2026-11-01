using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket;

internal class HttpSocketTransport : ITransport
{
    private readonly HttpSocketDuplexPipe _pipe;

    // Client side only: the upgrade response and the handler that holds its connection, released on close.
    private readonly HttpResponseMessage? _response;
    private readonly HttpConnector? _connector;
    private readonly bool _ownsCertificate;

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate { get; }

    /// <summary>
    /// Server side transport over an accepted HttpSocket.
    /// </summary>
    /// <param name="pipe">Accepted pipe.</param>
    /// <param name="remoteAddress">Client address.</param>
    /// <param name="remotePort">Client port.</param>
    /// <param name="remoteCertificate">Client certificate from the TLS handshake; owned by the connection.</param>
    public HttpSocketTransport(HttpSocketDuplexPipe pipe, string? remoteAddress = null, int? remotePort = null,
        X509Certificate2? remoteCertificate = null)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        RemoteCertificate = remoteCertificate;
    }

    private HttpSocketTransport(HttpSocketDuplexPipe pipe, HttpResponseMessage response, HttpConnector connector)
        : this(pipe, null, null, connector.TakeServerCertificate())
    {
        _response = response;
        _connector = connector;
        _ownsCertificate = true;
    }

    public async ValueTask CloseAsync(bool linger)
    {
        await _pipe.CompleteAsync().ConfigureAwait(false);
        _response?.Dispose();
        _connector?.Dispose();
        if (_ownsCertificate)
            RemoteCertificate?.Dispose();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    internal static async ValueTask<ITransport> ConnectAsync(
        HttpSocketClientConfig config,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(config.ConnectionTimeout);
        using var cancellationTokenRegistration =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var connector = new HttpConnector(config);
        HttpResponseMessage? response = null;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, config.Url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            message.Headers.Connection.Add("Upgrade");
            message.Headers.Upgrade.Add(new ProductHeaderValue(HttpHeaderConstants.UpgradeHttpSocket));
            message.Headers.Authorization = config.AuthenticationHeader;

            response = await connector.Invoker.SendAsync(message, cancellationTokenRegistration.Token)
                .ConfigureAwait(false);

            // The protocol should be switching. If not, then check for error.
            if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
                response.EnsureSuccessStatusCode();

            // Don't cancel the stream from the passed cancellation token as it is only valid
            // until the connection has been completed.
            // ReSharper disable once MethodSupportsCancellation
            var connectedStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            var pipe = new HttpSocketDuplexPipe(connectedStream, false);

            return new HttpSocketTransport(pipe, response, connector);
        }
        catch (Exception e)
        {
            response?.Dispose();
            connector.Dispose();
            throw MapException(e);
        }
    }

    private static TransportException MapException(Exception exception)
    {
        if (HttpConnector.IsTlsAuthenticationFailure(exception))
            return new TransportException(TransportError.AuthenticationError,
                $"TLS authentication failed: {exception.Message}", exception);

        switch (exception)
        {
            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } e:
                return new TransportException(TransportError.AuthenticationError,
                    $"Http connection authentication failed with status code: {e.StatusCode}", e);

            case HttpRequestException { StatusCode: HttpStatusCode.InternalServerError } e:
                return new TransportException(TransportError.InternalError,
                    $"Http connection failed with status code: {e.StatusCode}", e);

            case HttpRequestException e:
                return new TransportException(GetTransportError(e.HttpRequestError), e.Message, e);

            case TaskCanceledException e:
                return new TransportException(TransportError.ConnectionTimeout, e.Message, e);

            default:
                return new TransportException(TransportError.ConnectionRefused, exception.Message, exception);
        }
    }

    private static TransportError GetTransportError(HttpRequestError error)
    {
        return error switch
        {
            HttpRequestError.NameResolutionError => TransportError.Unreachable,
            HttpRequestError.ConnectionError => TransportError.ConnectionTimeout,
            HttpRequestError.SecureConnectionError => TransportError.ProtocolError,
            HttpRequestError.HttpProtocolError => TransportError.ProtocolError,
            HttpRequestError.ExtendedConnectNotSupported => TransportError.ProtocolError,
            HttpRequestError.VersionNegotiationError => TransportError.VersionNegotiationError,
            HttpRequestError.UserAuthenticationError => TransportError.AuthenticationError,
            HttpRequestError.ProxyTunnelError => TransportError.Unreachable,
            HttpRequestError.InvalidResponse => TransportError.ProtocolError,
            HttpRequestError.ResponseEnded => TransportError.ConnectionAborted,
            _ => TransportError.InternalError,
        };
    }
}
