using System;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Transports.WebSocket;

public class WebSocketTransport : ITransport
{
    private readonly IWebSocketPipe _pipe;
    private readonly ClientWebSocket? _client;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    private WebSocketTransport(IWebSocketPipe pipe)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
    }

    private WebSocketTransport(IWebSocketPipe pipe, ClientWebSocket client)
    {
        _pipe = pipe;
        Input = pipe.Input;
        Output = pipe.Output;
        _client = client;
    }

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
        WebSocketClientConfig config,
        CancellationToken cancellationToken)
    {

        using var timeoutCancellation = new CancellationTokenSource(config.ConnectionTimeout);
        await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel, false);

        try
        {
            var client = new ClientWebSocket();

            await client.ConnectAsync(config.Url, cancellationTokenRegistration.Token);

            IWebSocketPipe pipe =
                new WebSocketPipe(client, new WebSocketPipeOptions { CloseWhenCompleted = true });

            // Run the receive loop.
            _ = Task.Run(async () => await pipe.RunAsync(CancellationToken.None), CancellationToken.None);

            return new WebSocketTransport(pipe, client);

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

    public static ITransport CreateFromConnection(IWebSocketPipe webSocketPipe)
    {
        return new WebSocketTransport(webSocketPipe);
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
