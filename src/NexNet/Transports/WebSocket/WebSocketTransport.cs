using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.WebSocket;

internal class WebSocketTransport : ITransport
{
    private readonly IWebSocketPipe _pipe;

    // Client side only: the socket and the handler that holds its connection, released on close.
    private readonly ClientWebSocket? _client;
    private readonly HttpConnector? _connector;
    private readonly bool _ownsCertificate;

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate { get; }

    /// <summary>
    /// Server side: the ASP.NET principal of the accepted request; null on clients.
    /// </summary>
    public ClaimsPrincipal? User { get; }

    /// <summary>
    /// Server side transport over an accepted WebSocket.
    /// </summary>
    /// <param name="pipe">Accepted pipe.</param>
    /// <param name="remoteAddress">Client address.</param>
    /// <param name="remotePort">Client port.</param>
    /// <param name="remoteCertificate">Client certificate from the TLS handshake; owned by the connection.</param>
    /// <param name="user">ASP.NET principal of the request that opened the connection.</param>
    public WebSocketTransport(IWebSocketPipe pipe, string? remoteAddress = null, int? remotePort = null,
        X509Certificate2? remoteCertificate = null, ClaimsPrincipal? user = null)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        RemoteCertificate = remoteCertificate;
        User = user;
    }

    private WebSocketTransport(IWebSocketPipe pipe, ClientWebSocket client, HttpConnector connector)
        : this(pipe, null, null, connector.TakeServerCertificate())
    {
        _client = client;
        _connector = connector;
        _ownsCertificate = true;
    }

    public async ValueTask CloseAsync(bool linger)
    {
        await _pipe.CompleteAsync().ConfigureAwait(false);

        if (_client == null)
            return;

        if (_client.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or InvalidOperationException)
            {
                // The peer went away during the close handshake.
            }
        }

        _pipe.Dispose();
        _connector?.Dispose();
        if (_ownsCertificate)
            RemoteCertificate?.Dispose();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    internal static async ValueTask<ITransport> ConnectAsync(
        WebSocketClientConfig config,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(config.ConnectionTimeout);
        using var cancellationTokenRegistration =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var connector = new HttpConnector(config);
        var client = new ClientWebSocket();
        try
        {
            client.Options.CollectHttpResponseDetails = true;

            if (config.AuthenticationHeader != null)
                client.Options.SetRequestHeader("Authorization", config.AuthenticationHeader.ToString());

            await client.ConnectAsync(config.Url, connector.Invoker, cancellationTokenRegistration.Token)
                .ConfigureAwait(false);

            IWebSocketPipe pipe =
                new WebSocketPipe(client, new WebSocketPipeOptions { CloseWhenCompleted = true }, config);

            // Run receive loop on a long-running task.
            _ = Task.Factory.StartNew(
                static async pipe => await Unsafe.As<IWebSocketPipe>(pipe!).RunAsync(CancellationToken.None).ConfigureAwait(false),
                pipe,
                TaskCreationOptions.DenyChildAttach);
            return new WebSocketTransport(pipe, client, connector);
        }
        catch (Exception e)
        {
            var statusCode = client.HttpStatusCode;
            client.Dispose();
            connector.Dispose();
            throw MapException(e, statusCode);
        }
    }

    private static TransportException MapException(Exception exception, HttpStatusCode statusCode)
    {
        if (HttpConnector.IsTlsAuthenticationFailure(exception))
            return new TransportException(TransportError.AuthenticationError,
                $"TLS authentication failed: {exception.Message}", exception);

        switch (exception)
        {
            case WebSocketException e when statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new TransportException(TransportError.AuthenticationError,
                    $"Websocket connection authentication failed with Http status code: {statusCode}", e);

            case WebSocketException e when statusCode == HttpStatusCode.InternalServerError:
                return new TransportException(TransportError.InternalError,
                    $"Websocket connection failed with Http status code: {statusCode}", e);

            case WebSocketException e:
                return new TransportException(GetTransportError(e.WebSocketErrorCode), e.Message, e);

            case TaskCanceledException e:
                return new TransportException(TransportError.ConnectionTimeout, e.Message, e);

            default:
                return new TransportException(TransportError.ConnectionRefused, exception.Message, exception);
        }
    }

    private static TransportError GetTransportError(WebSocketError error)
    {
        return error switch
        {
            WebSocketError.Success => TransportError.Success,
            WebSocketError.InvalidMessageType => TransportError.ProtocolError,
            WebSocketError.Faulted => TransportError.ProtocolError,
            WebSocketError.NativeError => TransportError.InternalError,
            WebSocketError.NotAWebSocket => TransportError.ProtocolError,
            WebSocketError.UnsupportedVersion => TransportError.ProtocolError,
            WebSocketError.UnsupportedProtocol => TransportError.ProtocolError,
            WebSocketError.HeaderError => TransportError.ProtocolError,
            WebSocketError.ConnectionClosedPrematurely => TransportError.ConnectionAborted,
            WebSocketError.InvalidState => TransportError.ProtocolError,
            _ => TransportError.InternalError,
        };
    }
}
