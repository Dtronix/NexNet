namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Represents an incoming stream open request from a remote peer.
/// </summary>
public interface INexusStreamRequest
{
    /// <summary>
    /// Gets the identifier of the requested resource (e.g., file path).
    /// </summary>
    string ResourceId { get; }

    /// <summary>
    /// Gets the requested access mode.
    /// </summary>
    StreamAccessMode Access { get; }

    /// <summary>
    /// Gets the requested share mode.
    /// </summary>
    StreamShareMode Share { get; }

    /// <summary>
    /// Gets the position to resume from, or -1 for a fresh start.
    /// </summary>
    long ResumePosition { get; }
}
