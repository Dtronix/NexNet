using System.IO.Pipelines;
using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// Main interface for duplex pipe interaction with the Nexus system.
/// </summary>
public interface INexusDuplexPipe : IDuplexPipe
{
    /// <summary>
    /// Id of the pipe.
    /// </summary>
    ushort Id { get; }

    /// <summary>
    /// Task which completes when the pipe is ready for usage on the invoking side.
    /// </summary>
    Task ReadyTask { get; }

    /// <summary>
    /// Sets the pipe to the complete state and closes the other end of the connection.
    /// Do not use the pipe after calling this method.
    /// </summary>
    /// <returns>Task which completes when the pipe is closed.</returns>
    ValueTask CompleteAsync();

    internal NexusPipeWriter WriterCore { get; }
    internal NexusPipeReader ReaderCore { get; }
}
