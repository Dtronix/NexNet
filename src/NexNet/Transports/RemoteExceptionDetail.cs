namespace NexNet.Transports;

/// <summary>
/// What the executing side sends to the caller when a method that returns a result throws.
/// </summary>
public enum RemoteExceptionDetail
{
    /// <summary>
    /// Nothing: the caller gets a <see cref="NexNet.Invocation.ProxyRemoteInvocationException"/> without detail.
    /// </summary>
    None = 0,

    /// <summary>
    /// The exception's full type name and its message, truncated to 256 and 2048 characters. They can reveal
    /// internal details to the peer.
    /// </summary>
    TypeAndMessage = 1
}
