using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// A <see cref="IDuplexPipe"/> over a <see cref="WebSocket"/> which can be 
/// used for reading and writing to it.
/// </summary>
public interface IWebSocketPipe : IDuplexPipe, IDisposable
{
    /// <summary>
    /// Creates a new instance of a WebSocket pipe.
    /// </summary>
    /// <param name="webSocket">WebSocket to wrap.</param>
    /// <param name="options">Pipe and socket configurations.</param>
    /// <param name="config">Server configuration.</param>
    /// <returns>New websocket</returns>
    public static IWebSocketPipe Create(
        System.Net.WebSockets.WebSocket webSocket,
        WebSocketPipeOptions options, 
        AspServerConfig config) =>
        new WebSocketPipe(webSocket, options, config);
    /// <summary>
    /// Indicates the reason for the close handshake.
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; }

    /// <summary>
    /// Allows describing the reason why the connection was closed.
    /// </summary>
    public string? CloseStatusDescription { get; }

    /// <summary>
    /// Returns the current state of the underlying <see cref="WebSocket"/> connection.
    /// </summary>
    public WebSocketState State { get; }

    /// <summary>
    /// Gets the subprotocol that was negotiated during the opening handshake.
    /// </summary>
    public string? SubProtocol { get; }

    /// <summary>
    /// Stops the duplex pipe: sends what was flushed to a completed <see cref="IDuplexPipe.Output"/> (or stops
    /// sending at once if the output was not completed), optionally closes the underlying <see cref="WebSocket"/>
    /// with the specified status and reason, then stops reading from the socket.
    /// </summary>
    /// <remarks>
    /// The <see cref="IDuplexPipe.Input"/> is not completed by this call. Its writer is completed by the read loop
    /// once no receive is pending (this call waits briefly for that), and its reader is completed by whoever reads
    /// the input. Completing the input while a receive is still writing into its memory would return that memory to
    /// the shared pool, and the late bytes would land in another connection's data.
    /// </remarks>
    /// <param name="closeStatus">Optional close status to use for closing the underlying <see cref="WebSocket"/>. 
    ///     If no status is provided, the socket will be closed only if <see cref="WebSocketPipeOptions.CloseWhenCompleted"/> 
    ///     was specified when the duplex pipe was created.
    /// </param>
    /// <param name="closeStatusDescription">Optional close status description to use if the underlying 
    ///     <see cref="WebSocket"/> is closed.</param>
    /// <returns>A task that completes once the send loop has ended, the socket was closed if requested, and the read
    ///     loop has exited or a short wait for it has elapsed.</returns>
    public ValueTask CompleteAsync(WebSocketCloseStatus? closeStatus = null, string? closeStatusDescription = null);

    /// <summary>
    /// Starts populating the <see cref="IDuplexPipe.Input"/> with incoming data from the underlying 
    /// <see cref="WebSocket"/> as well as sending outgoing data written to the <see cref="IDuplexPipe.Output"/> 
    /// and finishes when either are completed (or an explicit call to <see cref="CompleteAsync"/> is performed).
    /// </summary>
    /// <param name="cancellation">Optional cancellation token to complete the operation.</param>
    /// <returns>
    /// A <see cref="Task"/> that will be completed when either <see cref="IDuplexPipe.Input" /> or 
    /// <see cref="IDuplexPipe.Output"/> are completed, or an explicit invocation of <see cref="CompleteAsync"/> 
    /// is executed.
    /// </returns>
    public Task RunAsync(CancellationToken cancellation = default);
}
