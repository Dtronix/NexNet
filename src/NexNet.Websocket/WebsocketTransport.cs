using System.IO.Pipelines;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using NexNet.Transports;

namespace NexNet.Websocket;

internal class WebsocketTransport : ITransport
{
    private readonly IWebSocketPipe _pipe;
    private readonly ClientWebSocket? _client;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    private WebsocketTransport(IWebSocketPipe pipe)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
    }

    private WebsocketTransport(IWebSocketPipe pipe, ClientWebSocket client)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
        _client = client;
    }

    public TransportConfiguration Configurations => new TransportConfiguration();

    public async ValueTask CloseAsync(bool linger)
    {
        await _pipe.CompleteAsync();

        if (_client != null)
            await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _client?.Dispose();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    internal static async ValueTask<ITransport> ConnectAsync(
        WebsocketClientConfig config,
        CancellationToken cancellationToken)
    {

        using var timeoutCancellation = new CancellationTokenSource(config.ConnectionTimeout);
        await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel, false);

        try
        {
            var client = new ClientWebSocket();

            await client.ConnectAsync(config.Url, cancellationTokenRegistration.Token);

            IWebSocketPipe pipe =
                new SimpleWebSocketPipe(client, new WebSocketPipeOptions { CloseWhenCompleted = true });

            // Run the receive loop.
            _ = Task.Run(async () => await pipe.RunAsync(CancellationToken.None), CancellationToken.None);

            return new WebsocketTransport(pipe, client);

        }
        catch (WebSocketException e)
        {
            throw new TransportException(GetTransportError(e.WebSocketErrorCode), e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, e.Message, e);
        }
    }

    internal static ITransport CreateFromConnection(IWebSocketPipe webSocketPipe)
    {
        return new WebsocketTransport(webSocketPipe);
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
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
        };
    }
}
