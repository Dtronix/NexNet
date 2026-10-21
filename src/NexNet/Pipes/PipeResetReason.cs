namespace NexNet.Pipes;

/// <summary>
/// Why a pipe was reset.
/// </summary>
internal enum PipeResetReason : byte
{
    /// <summary>
    /// A channel item broke the framing: a malformed length prefix, or a value that does not decode to exactly its
    /// length.
    /// </summary>
    InvalidItem,

    /// <summary>
    /// A channel item's length exceeds <see cref="NexNet.Transports.ConfigBase.MaxMessageSize"/>.
    /// </summary>
    TooLarge,
}
