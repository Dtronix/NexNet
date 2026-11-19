using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

#pragma warning disable VSTHRD200
#pragma warning disable CA1416

namespace NexNet.IntegrationTests;

/// <summary>
/// C# transports follow the bindings of protocol-specification.md §13: HttpSocket needs 101, origins compare
/// case-insensitively, WebSocket carries binary messages only, and connect failures are TransportExceptions.
/// </summary>
internal class TransportConformanceTests : BaseTests
{
    [Test]
    [Arguments(200)]
    [Arguments(204)]
    public async Task HttpSocket_SuccessStatusOtherThan101_FailsConnect(int status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // A plain HTTP server that answers the upgrade request without switching protocols.
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync().ConfigureAwait(false);
            await ReadRequestHeaders(socket).ConfigureAwait(false);
            var body = status == 204 ? "" : "not a nexnet stream";
            var response = $"HTTP/1.1 {status} {(status == 204 ? "No Content" : "OK")}\r\n"
                           + (status == 204 ? "" : $"Content-Length: {body.Length}\r\n")
                           + $"Connection: close\r\n\r\n{body}";
            await socket.SendAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
            socket.Shutdown(SocketShutdown.Both);
        });

        var config = new HttpSocketClientConfig { Url = new Uri($"http://127.0.0.1:{port}/httpsocket-test") };

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(async () =>
            await HttpSocketTransport.ConnectAsync(config, CancellationToken.None).AsTask().Timeout(TestTimeouts.Connect));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.ProtocolError);
        await serverTask.Timeout(TestTimeouts.Connect);
    }

    [Test]
    [Arguments("https://allowed.example", true)]
    [Arguments("HTTPS://ALLOWED.EXAMPLE", true)]
    [Arguments("https://Allowed.Example", true)]
    [Arguments("https://evil.example", false)]
    public async Task HttpSocket_OriginCheck_IgnoresCase(string origin, bool allowed)
    {
        var options = new HttpSocketOptions();
        options.AllowedOrigins.Add("https://Allowed.example");

        var nextCalled = false;
        var middleware = new HttpSocketMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLoggerFactory.Instance, options);

        var context = new DefaultHttpContext();
        context.Features.Set<IHttpUpgradeFeature>(new UpgradableRequestFeature());
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Connection = "Upgrade";
        context.Request.Headers.Upgrade = HttpHeaderConstants.UpgradeHttpSocket;
        context.Request.Headers.Origin = origin;

        await middleware.Invoke(context);

        await Assert.That(nextCalled).IsEqualTo(allowed);
        await Assert.That(context.Response.StatusCode)
            .IsEqualTo(allowed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task WebSocketPipe_TextMessage_ClosesWith1003()
    {
        var socket = new TextMessageWebSocket();
        using var pipe = IWebSocketPipe.Create(socket, new WebSocketPipeOptions(), new WebSocketServerConfig { Path = "/" });

        // The text payload never reaches the NexNet stream: the pending read ends with no bytes.
        var readTask = pipe.Input.ReadAsync().AsTask();
        await pipe.RunAsync().Timeout(1);

        await Assert.That(socket.CloseStatus).IsEqualTo(WebSocketCloseStatus.InvalidMessageType);

        var read = await readTask.Timeout(1);
        await Assert.That(read.IsCompleted).IsTrue();
        await Assert.That(read.Buffer.Length).IsEqualTo(0);
    }

    [Test]
    public async Task WebSocketServer_TextMessage_ClosesWith1003()
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(Type.WebSocket);
        var server = CreateServer(CreateServerConfig(Type.WebSocket), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(clientConfig.Url, CancellationToken.None).Timeout(TestTimeouts.Connect);
        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None)
            .Timeout(TestTimeouts.Connect);

        // Skip the server's binary bytes (its preamble) until the Close arrives.
        var buffer = new byte[1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask()
                .Timeout(TestTimeouts.Connect);
        } while (result.MessageType != WebSocketMessageType.Close);

        await Assert.That(socket.CloseStatus).IsEqualTo(WebSocketCloseStatus.InvalidMessageType);
        socket.Abort();
    }

    [Test]
    [NotInParallel]
    public async Task Quic_ClientStreamOpenFails_ThrowsTransportException()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var serverConfig = (QuicServerConfig)CreateServerConfig(Type.Quic);

        // A raw QUIC server that accepts the connection but grants no bidirectional streams, so the client's
        // stream open waits until its connection timeout.
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = serverConfig.EndPoint,
            ApplicationProtocols = [QuicHelpers.Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0x0A,
                DefaultCloseErrorCode = 0x0B,
                MaxInboundBidirectionalStreams = 0,
                ServerAuthenticationOptions = QuicHelpers.WithNexNetAlpn(serverConfig.SslServerAuthenticationOptions),
            }),
        }).Timeout(1);

        var clientConfig = (QuicClientConfig)CreateClientConfig(Type.Quic);
        clientConfig.ConnectionTimeout = 300;

        var connectTask = QuicTransport.ConnectAsync(clientConfig, CancellationToken.None).AsTask();
        await using var serverConnection = await listener.AcceptConnectionAsync().AsTask().Timeout(5);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(async () => await connectTask.Timeout(5));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.ConnectionTimeout);
    }

    private static async Task ReadRequestHeaders(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        while (true)
        {
            var count = await socket.ReceiveAsync(buffer).ConfigureAwait(false);
            if (count == 0)
                return;
            received.AddRange(buffer.AsSpan(0, count));
            if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                return;
        }
    }

    /// <summary>
    /// An HTTP/1.1 request that can be upgraded; the test never upgrades it.
    /// </summary>
    private sealed class UpgradableRequestFeature : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => true;

        [ExcludeFromCodeCoverage]
        public Task<Stream> UpgradeAsync() => throw new NotSupportedException();
    }

    /// <summary>
    /// Open WebSocket whose first receive returns a complete text message; it records the close status it sends.
    /// </summary>
    private sealed class TextMessageWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private int _receives;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receives) > 1)
                return ValueTask.FromResult(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));

            var count = Encoding.UTF8.GetBytes("hello", buffer.Span);
            return ValueTask.FromResult(new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, true));
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus ??= closeStatus;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus ??= closeStatus;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
