using System;

namespace NexNet;

/// <summary>
/// Thrown locally when a value is larger than the protocol allows. The value is not sent and the session stays
/// connected.
/// </summary>
public class NexusMessageTooLargeException : Exception
{
    /// <summary>
    /// Serialized size of the value in bytes.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Largest size allowed in bytes.
    /// </summary>
    public long Limit { get; }

    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="size">Serialized size of the value in bytes.</param>
    /// <param name="limit">Largest size allowed in bytes.</param>
    public NexusMessageTooLargeException(long size, long limit)
        : base($"Message of {size} bytes exceeds the maximum of {limit} bytes.")
    {
        Size = size;
        Limit = limit;
    }
}
