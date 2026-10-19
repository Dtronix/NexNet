using System.IO.Pipelines;
using System.Net.WebSockets;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// Configures the behavior of <see cref="IWebSocketPipe"/>.
/// </summary>
public class WebSocketPipeOptions
{
    /// <summary>
    /// Default options for <see cref="WebSocketPipe"/>.
    /// </summary>
    public static WebSocketPipeOptions Default { get; } = new WebSocketPipeOptions();

    /// <summary>
    /// Whether to close the underlying <see cref="WebSocket"/> when 
    /// either the <see cref="IDuplexPipe.Input"/> or <see cref="IDuplexPipe.Output"/> 
    /// is marked as completed. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Unless overriden in an explicit call to <see cref="IWebSocketPipe.CompleteAsync"/>, 
    /// the close status will be <see cref="WebSocketCloseStatus.NormalClosure"/>.
    /// </remarks>
    public bool CloseWhenCompleted { get; set; }

    private PipeOptions _inputPipeOptions = ConfigBase.CreateReceivePipeOptions(useSynchronizationContext: false);

    /// <summary>
    /// Allows fine-grained configuration options for the incoming side of the
    /// websocket pipe. Defaults to pausing at 256KB and resuming at 128KB. The pause threshold must be 0 (never
    /// pause) or at least two maximum frames (131,080 bytes).
    /// </summary>
    public PipeOptions InputPipeOptions
    {
        get => _inputPipeOptions;
        set
        {
            System.ArgumentNullException.ThrowIfNull(value);
            ConfigBase.ValidateReceivePipeOptions(value, nameof(InputPipeOptions));
            _inputPipeOptions = value;
        }
    }
}
