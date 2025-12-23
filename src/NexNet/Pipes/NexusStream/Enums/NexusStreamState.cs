namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Represents the current state of a NexStream stream.
/// </summary>
public enum NexusStreamState
{
    /// <summary>
    /// No stream is currently active.
    /// </summary>
    None = 0,

    /// <summary>
    /// An Open request has been sent and is awaiting response.
    /// </summary>
    Opening = 1,

    /// <summary>
    /// The stream is active and ready for operations.
    /// </summary>
    Open = 2,

    /// <summary>
    /// The stream has been closed.
    /// </summary>
    Closed = 3
}
