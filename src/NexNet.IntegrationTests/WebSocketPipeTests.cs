using System.Net.WebSockets;
using NexNet.Asp.WebSocket;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class WebSocketPipeTests
{
    [Test]
    public async Task ReceiveCompletingAfterPipeCompleted_EndsReadLoopWithoutFault()
    {
        var socket = new GatedWebSocket();
        using var pipe = IWebSocketPipe.Create(socket, new WebSocketPipeOptions { CloseWhenCompleted = true },
            new WebSocketServerConfig { Path = "/" });

        var run = pipe.RunAsync();
        await socket.ReceiveStarted.Task.Timeout(1);

        // The transport is closed while a receive is in flight, then the receive returns data.
        await pipe.CompleteAsync().AsTask().Timeout(1);
        socket.ReleaseReceive.SetResult();

        Assert.That(async () => await run.Timeout(1), Throws.Nothing);
    }

    /// <summary>
    /// Open WebSocket whose first receive waits for <see cref="ReleaseReceive"/>, then returns a partial binary
    /// message; later receives report a close.
    /// </summary>
    private sealed class GatedWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _receives;

        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReceive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receives) > 1)
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);

            ReceiveStarted.SetResult();
            await ReleaseReceive.Task.ConfigureAwait(false);
            return new ValueWebSocketReceiveResult(Math.Min(16, buffer.Length), WebSocketMessageType.Binary, false);
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
